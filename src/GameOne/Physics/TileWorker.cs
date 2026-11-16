using Brutal.JoltApi;

namespace GameOne.Physics;

/// <summary>
/// Makes the ground's tiles' shapes (their heights and Jolt height fields) on a thread of its own,
/// ahead of need, so a physics' step only adds a finished one's body (see <see cref="TerrainTiles"/>).
/// There is one for every world (<see cref="JoltShared.Tiles"/>): each world opens its own
/// <see cref="Jobs"/> on it, which carry its body (what each tile is made of), so a job is always
/// made of the ground of the world that wants it, whatever else the worker makes. A world's step
/// says which cells it wants and by when (<see cref="Jobs.TryWant"/>, a round of them at a time),
/// and takes what is finished (<see cref="Jobs.TryTake"/>); it never waits on the worker.
/// </summary>
/// <remarks>
/// <para>
/// Everything but a job's making runs on the main thread. The worker touches Jolt only to make a
/// shape (which Jolt allows on any thread), never a body, and never releases one (every shape it
/// makes is handed back, and released on the main thread by whoever takes it, or as its jobs are
/// closed); nor does it touch <see cref="Timings"/> (its statics aren't thread-safe). One lock
/// guards what the threads share: every world's cells wanted, the one in hand, and the shapes
/// finished. The main thread only tries it (<see cref="Monitor.TryEnter(object)"/>): should the
/// worker hold it (it does only for moments, but it runs below normal priority, so it can be put
/// aside while it does), that update's hand-over is skipped rather than waited for, and the next one
/// catches up. The step loses nothing by it: the cells it could reach soon are made in the step
/// whatever the worker has. Only closing a world's jobs waits for it, for a tile of that world's in hand.
/// </para>
/// <para>
/// It runs below normal priority, so it takes the time the steps leave rather than crowding Jolt's
/// job threads or a tile a step is making itself, and as a background thread, so it never keeps
/// the game from closing (the game's lives as long as the process; <see cref="Dispose"/> stops one
/// made for a test). It starts on the first cell any world wants, takes the cell due soonest of
/// every world's, by the time it has to go (each world's dues are on its own physics' clock, which
/// starts when that physics is made), and with nothing to do it waits on its lock's signal, costing nothing.
/// </para>
/// </remarks>
internal sealed class TileWorker : IDisposable
{
    /// <summary>A cell wanted: by when (lower is sooner), and in which round of its world's wants it was last wanted.</summary>
    internal struct Wanted
    {
        public double Due;
        public long Round;
    }

    /// <summary>
    /// One world's work on the worker: the cells it wants, the shapes finished for it, and how a cell
    /// of its ground is made (its body's heights, into the worker's own buffer).
    /// </summary>
    internal sealed class Jobs : IDisposable
    {
        private readonly TileWorker _worker;
        internal readonly Func<TerrainTiles.Cell, float[], (Shape Shape, float Centre)> Make;
        internal readonly Dictionary<TerrainTiles.Cell, Wanted> Pending = []; // wanted, not yet started
        internal readonly Queue<(TerrainTiles.Cell Cell, Shape Shape, float Centre)> Done = new();
        internal readonly HashSet<TerrainTiles.Cell> DoneCells = []; // the cells of those finished, not to be made again
        internal long Round; // the round of wants open now: one an update, each handed over by TryWant
        internal double Now; // its world's clock as the last round was handed over: its dues are measured from it
        internal bool Closed;
        internal Exception? Failed;

        internal Jobs(TileWorker worker, Func<TerrainTiles.Cell, float[], (Shape Shape, float Centre)> make) =>
            (_worker, Make) = (worker, make);

        /// <summary>How many shapes are finished and waiting to be taken.</summary>
        public int Finished
        {
            get
            {
                lock (_worker._lock)
                    return Done.Count;
            }
        }

        /// <summary>
        /// Whether the worker has a cell of this world's pending or in hand (not counting those
        /// finished), or hasn't yet gone back to waiting: once false, the worker is idle on its
        /// signal (holding no lock), so this world's next hand-over and take are never skipped.
        /// </summary>
        public bool Busy
        {
            get
            {
                lock (_worker._lock)
                    return Failed is null && !Closed && !_worker._stopping && (Pending.Count > 0 || _worker._inHand.Jobs == this || !_worker._idle);
            }
        }

        /// <summary>
        /// Hands over one round of wants (once an update), under one lock: each cell of
        /// <paramref name="wants"/> made by its due time (seconds of its world's physics time, whose
        /// clock is <paramref name="now"/>: the least time to go is made first, of every world's;
        /// wanting a cell again re-ranks it), and every cell of this
        /// world's pending that isn't among them dropped, as nothing needs it now; then the worker
        /// is woken to what is. A cell already in hand or finished (and not yet taken) isn't made
        /// again: it is on its way. Returns false, having handed over nothing, if the worker holds
        /// the lock just now (see the remarks): what it had pending stays, and the next round puts it right.
        /// </summary>
        public bool TryWant(List<(TerrainTiles.Cell Cell, double Due)> wants, double now) // a List, not an interface: walked with no enumerator allocated, every update
        {
            var worker = _worker;
            if (!Monitor.TryEnter(worker._lock))
                return false;
            try
            {
                if (Closed || Failed is not null || worker._stopping)
                    return true;
                Now = now;
                var inHand = worker._inHand.Jobs == this ? worker._inHand.Cell : (TerrainTiles.Cell?)null;
                foreach (var (cell, due) in wants)
                    if (cell != inHand && !DoneCells.Contains(cell))
                        Pending[cell] = new Wanted { Due = due, Round = Round };
                List<TerrainTiles.Cell>? dropped = null;
                foreach (var (cell, wanted) in Pending)
                    if (wanted.Round != Round)
                        (dropped ??= []).Add(cell);
                if (dropped is not null)
                    foreach (var cell in dropped)
                        Pending.Remove(cell);
                Round++;
                if (Pending.Count > 0)
                {
                    if (!worker._started)
                    {
                        worker._started = true;
                        worker._thread.Start();
                    }
                    Monitor.PulseAll(worker._lock);
                }
                return true;
            }
            finally
            {
                Monitor.Exit(worker._lock);
            }
        }

        /// <summary>
        /// Takes every finished shape into <paramref name="into"/>, under one lock: the caller then
        /// owns each shape (and must dispose it, or hand it on). Throws, here on the main thread, an
        /// exception the worker hit making one of this world's (it makes no more of them after it).
        /// Returns false, having taken nothing, if the worker holds the lock just now (see the
        /// remarks): they are taken next time.
        /// </summary>
        public bool TryTake(List<(TerrainTiles.Cell Cell, Shape Shape, float Centre)> into)
        {
            if (!Monitor.TryEnter(_worker._lock))
                return false;
            try
            {
                if (Failed is { } failed)
                    throw new InvalidOperationException($"The ground's tile worker failed: {failed.Message}", failed);
                while (Done.TryDequeue(out var done))
                {
                    DoneCells.Remove(done.Cell);
                    into.Add(done);
                }
                return true;
            }
            finally
            {
                Monitor.Exit(_worker._lock);
            }
        }

        /// <summary>
        /// How its work stands, in words, for a test that waited on it too long: this world's cells
        /// pending and finished, whether one of its is in hand, every world's pending, and whether
        /// the worker is back on its signal. Read-only.
        /// </summary>
        internal string Describe()
        {
            lock (_worker._lock)
            {
                var all = 0;
                foreach (var open in _worker._open)
                    all += open.Pending.Count;
                return $"{Pending.Count} of its cells pending ({all} of every world's), {Done.Count} finished, "
                    + $"{(_worker._inHand.Jobs == this ? "one of its in hand" : _worker._inHand.Jobs is null ? "none in hand" : "another world's in hand")}, "
                    + $"the worker {(_worker._idle ? "idle" : "not idle")}{(Closed ? ", closed" : "")}{(Failed is { } e ? $", failed: {e.Message}" : "")}";
            }
        }

        /// <summary>
        /// Drops every cell of this world's still pending (none in hand is stopped): for a world
        /// left with nothing to lay tiles for (released to the pool, <see cref="Worlds"/>), so the
        /// worker makes nothing more of it. Waits for the lock, which the worker holds only for moments.
        /// </summary>
        public void Drop()
        {
            lock (_worker._lock)
                Pending.Clear();
        }

        /// <summary>Closes this world's jobs: drops what is pending, waits for a cell of its in hand, and disposes every shape not taken. Idempotent.</summary>
        public void Dispose()
        {
            lock (_worker._lock)
            {
                if (Closed)
                    return;
                Closed = true;
                Pending.Clear();
                while (_worker._inHand.Jobs == this)
                    Monitor.Wait(_worker._lock); // the worker signals as it hands back what it had in hand
                _worker._open.Remove(this);
            }
            foreach (var (_, shape, _) in Done)
                shape.Dispose();
            Done.Clear();
            DoneCells.Clear();
        }
    }

    private readonly object _lock = new();
    private readonly List<Jobs> _open = []; // every world's jobs, in the order opened
    private readonly Queue<Action> _whenIdle = new(); // work for the worker's idle time, in the order asked (see WhenIdle)
    private readonly Thread _thread;
    private readonly float[] _heights = new float[Scene.TerrainTileSamples * Scene.TerrainTileSamples]; // the worker's own
    private bool _started;
    private bool _stopping;
    private bool _idle = true; // waiting on its signal, holding no lock (or not started)
    private (Jobs? Jobs, TerrainTiles.Cell Cell) _inHand; // the cell the worker is making, and whose

    public TileWorker() =>
        _thread = new Thread(Work) { Name = "Ground tiles", IsBackground = true, Priority = ThreadPriority.BelowNormal };

    /// <summary>
    /// Opens a world's jobs: <paramref name="make"/> makes a cell's shape from its ground, and the
    /// elevation of its centre, into the heights buffer given (the worker's own), on the worker's thread.
    /// </summary>
    public Jobs Open(Func<TerrainTiles.Cell, float[], (Shape Shape, float Centre)> make)
    {
        var jobs = new Jobs(this, make);
        lock (_lock)
            _open.Add(jobs);
        return jobs;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the worker's thread once it has no cell of any world's to
    /// make (a cell wanted always goes first: a step may soon need it), never on a step's: for
    /// what takes a while but is wanted only ahead of need (a body's physics made ahead,
    /// <see cref="Worlds.Ahead"/>). The work catches its own failures and hands them back itself;
    /// it is dropped if the worker stops first.
    /// </summary>
    public void WhenIdle(Action work)
    {
        lock (_lock)
        {
            if (_stopping)
                return;
            _whenIdle.Enqueue(work);
            if (!_started)
            {
                _started = true;
                _thread.Start();
            }
            Monitor.PulseAll(_lock);
        }
    }

    private void Work()
    {
        while (true)
        {
            Jobs? jobs = null;
            TerrainTiles.Cell cell = default;
            Action? idleWork = null;
            lock (_lock)
            {
                while (!_stopping && !Soonest(out jobs, out cell))
                {
                    if (_whenIdle.TryDequeue(out idleWork))
                        break;
                    _idle = true;
                    Monitor.Wait(_lock);
                }
                if (_stopping)
                    return;
                _idle = false;
                if (jobs is not null)
                {
                    jobs.Pending.Remove(cell);
                    _inHand = (jobs, cell);
                }
            }
            if (jobs is null)
            {
                idleWork?.Invoke(); // outside the lock: it may open a world's jobs on this worker
                continue;
            }
            try
            {
                var (shape, centre) = jobs.Make(cell, _heights);
                lock (_lock)
                {
                    // Handed back even if its world is closing: closing releases it, on the main thread.
                    _inHand = default;
                    jobs.Done.Enqueue((cell, shape, centre));
                    jobs.DoneCells.Add(cell);
                    Monitor.PulseAll(_lock); // to a world closing, waiting for it
                }
            }
            catch (Exception e)
            {
                lock (_lock)
                {
                    (jobs.Failed, _inHand) = (e, default);
                    jobs.Pending.Clear();
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }

    /// <summary>
    /// The pending cell with the least time to go of every open world's (under the lock): its due
    /// less its world's clock as its round was handed over, so worlds whose clocks started apart
    /// are ranked alike; the first opened's on a tie. A plain search: there are tens of them, a few
    /// hundred at most.
    /// </summary>
    private bool Soonest(out Jobs? jobs, out TerrainTiles.Cell best)
    {
        (jobs, best) = (null, default);
        var toGo = 0.0;
        for (var i = 0; i < _open.Count; i++)
        {
            var open = _open[i];
            if (open.Closed || open.Failed is not null)
                continue;
            foreach (var (cell, wanted) in open.Pending)
                if (jobs is null || wanted.Due - open.Now < toGo)
                    (jobs, best, toGo) = (open, cell, wanted.Due - open.Now);
        }
        return jobs is not null;
    }

    /// <summary>
    /// Stops the worker and waits for it (it finishes a tile in hand first), then disposes every
    /// open world's shapes not taken: for a worker made for a test (the game's, <see cref="JoltShared.Tiles"/>,
    /// lives as long as the process). Each world's jobs are closed by their own world as ever. Idempotent.
    /// </summary>
    public void Dispose()
    {
        bool started;
        lock (_lock)
        {
            if (_stopping)
                return;
            (_stopping, started) = (true, _started);
            Monitor.PulseAll(_lock);
        }
        if (started)
            _thread.Join();
        lock (_lock)
            foreach (var open in _open)
            {
                foreach (var (_, shape, _) in open.Done)
                    shape.Dispose();
                open.Done.Clear();
                open.DoneCells.Clear();
                open.Pending.Clear();
            }
    }
}
