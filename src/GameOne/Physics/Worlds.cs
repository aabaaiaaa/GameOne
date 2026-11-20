using System.Numerics;

namespace GameOne.Physics;

/// <summary>
/// The physics at each body in use: one world (<see cref="RocketPhysics"/>) a body, in that body's
/// frame, made as a vessel there first needs one (<see cref="Need"/>, or ahead of need off the
/// step: <see cref="Ahead"/>) and stepped while a craft is in it (<see cref="Step"/>). A world left
/// with no craft in it for <see cref="Scene.PhysicsIdleSeconds"/> of the world's time is released
/// to the pool, and the next body to need one takes it, rebuilt for that body
/// (<see cref="RocketPhysics.Rebuild"/>): Jolt's world is kept, so that costs less than a new one.
/// So a body nothing is in has no physics, and costs nothing; the witnessed (the flown vessel and the
/// small views, <see cref="Scene.MaxSmallViews"/>, and the one the main camera is on) are at seven
/// bodies at most, though an emptied world is kept a while: nothing nobody watches keeps one
/// running (the formula plays out whatever it is doing). Nothing here is written for any number of
/// bodies: a world is found by its body, never as "the other one".
/// </summary>
/// <remarks>
/// Everything is asked on the main thread, but a world made ahead, which the tile worker makes in
/// its idle time (<see cref="TileWorker.WhenIdle"/>): the pool and the worlds made ahead are shared
/// with it under one lock; the worlds in use are the main thread's alone. Each world in use is
/// stepped in turn on the main thread, in <see cref="Bodies.All"/> order (by
/// <see cref="GroundBody.Index"/>), which is what lets them share Jolt's job system and memory
/// (<see cref="JoltShared"/>).
/// </remarks>
internal sealed class Worlds : IDisposable
{
    private readonly object _gate = new();
    private readonly List<RocketPhysics> _inUse = [];   // by body index (the main thread's)
    private readonly List<double> _emptySince = [];     // each one's: the world time it was last seen with no craft (NaN while it has one)
    private readonly List<RocketPhysics> _released = []; // the pool (under _gate)
    private readonly List<RocketPhysics> _madeAhead = []; // made by the worker, not yet in use (under _gate)
    private readonly List<GroundBody> _asked = [];       // asked ahead, not yet made (under _gate)
    private volatile int _madeAheadCount;               // _madeAhead's count, read without the lock
    private Exception? _failed;                         // what making one ahead threw, handed back on the main thread
    private bool _disposed;

    /// <summary>How many bodies have a physics now (made, and not released).</summary>
    public int InUse
    {
        get
        {
            TakeMadeAhead();
            return _inUse.Count;
        }
    }

    /// <summary>Every world in use, by body index: for the timings, the drawing and the tests.</summary>
    public IReadOnlyList<RocketPhysics> All
    {
        get
        {
            TakeMadeAhead();
            return _inUse;
        }
    }

    /// <summary>
    /// How much shorter than a full step every world's step is (<see cref="RocketPhysics.StepScale"/>):
    /// the flight's slow-down's, the same at every body, as the game's clock is one. Each world in
    /// use is set as it changes, and one put in use takes it then.
    /// </summary>
    public float StepScale
    {
        get => _stepScale;
        set
        {
            _lag *= value / _stepScale; // the same share of a step, as each world's is (RocketPhysics.StepScale)
            _stepScale = value;
            for (var i = 0; i < _inUse.Count; i++)
                _inUse[i].StepScale = value;
        }
    }
    private float _stepScale = 1f;

    /// <summary>
    /// How far (seconds) every world's crafts are behind the world time the last <see cref="Step"/>
    /// ran to (<see cref="RocketPhysics.Lag"/>): the same at every body, so a craft's motion read in
    /// one world is at the moment its own world's crafts are, wherever it is taken.
    /// </summary>
    public float Lag => _lag;
    private float _lag;

    /// <summary>How many worlds are in the pool, released and not yet taken again.</summary>
    public int Released
    {
        get
        {
            lock (_gate)
                return _released.Count;
        }
    }

    /// <summary><paramref name="body"/>'s physics, if it has one made (or made ahead and ready), else null. Allocates nothing.</summary>
    public RocketPhysics? At(GroundBody body)
    {
        TakeMadeAhead();
        for (var i = 0; i < _inUse.Count; i++)
            if (_inUse[i].Body == body)
                return _inUse[i];
        return null;
    }

    /// <summary>
    /// <paramref name="body"/>'s physics, made now if it has none: from a world in the pool when
    /// there is one (its own body's first, which needs no rebuilding), else a new one.
    /// </summary>
    public RocketPhysics Need(GroundBody body)
    {
        if (At(body) is { } made)
            return made;
        var world = FromPool(body);
        if (world is null)
            world = new RocketPhysics(body);
        else if (world.Body != body)
            world.Rebuild(body);
        Use(world);
        return world;
    }

    /// <summary>
    /// Asks for <paramref name="body"/>'s physics ahead of need (a vessel soon to be there): made
    /// on the tile worker's idle time (<see cref="TileWorker.WhenIdle"/>), never in the step, and in
    /// use from the first <see cref="At"/> or <see cref="Need"/> after it is ready. Nothing if it
    /// has one, or is asked already.
    /// </summary>
    public void Ahead(GroundBody body)
    {
        if (At(body) is not null)
            return;
        lock (_gate)
        {
            if (_disposed || _asked.Contains(body) || IndexOf(_madeAhead, body) >= 0)
                return;
            _asked.Add(body);
        }
        MakeAhead(body);
    }

    /// <summary>Has the tile worker make <paramref name="body"/>'s world in its idle time (see <see cref="Ahead"/>): a method of its own, so an ask that finds it made or asked already makes no closure.</summary>
    private void MakeAhead(GroundBody body) =>
        JoltShared.Tiles.WhenIdle(() =>
            {
                RocketPhysics? world;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        _asked.Remove(body);
                        return; // nothing to make it for
                    }
                    world = FromPoolLocked(body);
                }
                try
                {
                    if (world is null)
                        world = new RocketPhysics(body, takeRafts: false); // the rafts are the world in use's: taken as it is put in use
                    else if (world.Body != body)
                        world.Rebuild(body);
                }
                catch (Exception e)
                {
                    lock (_gate)
                    {
                        _failed = e;
                        _asked.Remove(body);
                        _madeAheadCount = -1; // looked at on the main thread's next ask
                    }
                    return;
                }
                lock (_gate)
                {
                    _asked.Remove(body);
                    if (_disposed)
                    {
                        world.Dispose();
                        return;
                    }
                    _madeAhead.Add(world);
                    _madeAheadCount = _madeAhead.Count;
                }
            });

    /// <summary>
    /// Steps each world in use with a craft in it on by <paramref name="dt"/> (see
    /// <see cref="RocketPhysics.Update"/>), in <see cref="Bodies.All"/> order, each taking the
    /// <paramref name="controls"/> of its own crafts; <paramref name="player"/>, the flown vessel's
    /// place, only for its own body's (<paramref name="flown"/>). One with no craft is not stepped,
    /// and once it has had none for <see cref="Scene.PhysicsIdleSeconds"/> of the world's time
    /// (<paramref name="worldTime"/>), it is released to the pool.
    /// </summary>
    public void Step(float dt, ReadOnlySpan<RocketControl> controls, GroundBody flown, Vector3? player, double worldTime)
    {
        TakeMadeAhead();
        SeaStepped = false;
        // Every world stepped from the same leftover of a step, so each is at the same moment after
        // (one that had no craft, or was put in use since, taken up to it), and a craft taken from
        // one into another is at the same moment in both.
        var lag = _lag;
        _lag = RocketPhysics.LagAfter(lag, dt, _stepScale);
        for (var i = 0; i < _inUse.Count; i++)
        {
            var world = _inUse[i];
            if (world.Crafts.Count > 0)
            {
                _emptySince[i] = double.NaN;
                world.Lag = lag;
                world.Update(dt, controls, world.Body == flown ? player : null, worldTime);
                _lag = world.Lag;
                SeaStepped |= world.Body.HasSea;
                continue;
            }
            if (double.IsNaN(_emptySince[i]) || worldTime < _emptySince[i])
                _emptySince[i] = worldTime;
            else if (worldTime - _emptySince[i] >= Scene.PhysicsIdleSeconds)
            {
                world.Idle(); // its last wants of the tile worker dropped: nothing is there to need them
                lock (_gate)
                    _released.Add(world);
                _inUse.RemoveAt(i);
                _emptySince.RemoveAt(i);
                i--;
            }
        }
    }

    /// <summary>Whether the last <see cref="Step"/> stepped a world at a body with a sea, which sets the rafts' time and poses (<see cref="Rafts.Time"/>) as it steps.</summary>
    public bool SeaStepped { get; private set; }

    /// <summary>
    /// Puts every world in use at the world time itself, no leftover of a step behind it
    /// (<see cref="Lag"/> 0): as the formula's rates take every craft out (none is left behind the
    /// clock to be stepped on to it), so what comes back as they drop is at the clock's moment.
    /// </summary>
    public void ResetLag()
    {
        _lag = 0f;
        for (var i = 0; i < _inUse.Count; i++)
            _inUse[i].Lag = 0f;
    }

    /// <summary>Takes every craft out of every world in use (a world being loaded).</summary>
    public void Clear()
    {
        for (var i = 0; i < _inUse.Count; i++)
            _inUse[i].Clear();
        _lag = 0f;
    }

    /// <summary>Takes every wreck out of every world in use now (see <see cref="RocketPhysics.FlushWrecks"/>).</summary>
    public void FlushWrecks()
    {
        for (var i = 0; i < _inUse.Count; i++)
            _inUse[i].FlushWrecks();
    }

    /// <summary>Every stage of every craft in every world in use, to draw (see <see cref="RocketPhysics.GetParts"/>).</summary>
    public void GetParts(List<StagePart> parts)
    {
        parts.Clear();
        for (var i = 0; i < _inUse.Count; i++)
            _inUse[i].AddParts(parts);
    }

    /// <summary>How many of the ground's tiles are in the worlds in use, all told.</summary>
    public int TileCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _inUse.Count; i++)
                count += _inUse[i].TileCount;
            return count;
        }
    }

    /// <summary>A world from the pool for <paramref name="body"/>: its own body's if there is one, else any; none if it is empty.</summary>
    private RocketPhysics? FromPool(GroundBody body)
    {
        lock (_gate)
            return FromPoolLocked(body);
    }

    private RocketPhysics? FromPoolLocked(GroundBody body)
    {
        if (_released.Count == 0)
            return null;
        var at = IndexOf(_released, body);
        if (at < 0)
            at = _released.Count - 1;
        var world = _released[at];
        _released.RemoveAt(at);
        return world;
    }

    /// <summary>Where <paramref name="body"/>'s world is in <paramref name="worlds"/>, or -1 (a plain search: no delegate made).</summary>
    private static int IndexOf(List<RocketPhysics> worlds, GroundBody body)
    {
        for (var i = 0; i < worlds.Count; i++)
            if (worlds[i].Body == body)
                return i;
        return -1;
    }

    /// <summary>Puts <paramref name="world"/> in use, in its place by its body's index.</summary>
    private void Use(RocketPhysics world)
    {
        world.StepScale = _stepScale;
        world.TakeRafts(); // on the main thread: one made or rebuilt off it left them alone
        var at = 0;
        while (at < _inUse.Count && _inUse[at].Body.Index <= world.Body.Index)
            at++;
        _inUse.Insert(at, world);
        _emptySince.Insert(at, double.NaN);
    }

    /// <summary>
    /// Puts in use any world the worker has made ahead (one whose body has a world already, made
    /// meanwhile by <see cref="Need"/>, goes to the pool), and throws here what making one threw.
    /// A read of a field while there are none.
    /// </summary>
    private void TakeMadeAhead()
    {
        if (_madeAheadCount == 0)
            return;
        lock (_gate)
        {
            if (_failed is { } failed)
            {
                _failed = null;
                _madeAheadCount = _madeAhead.Count;
                throw new InvalidOperationException($"A body's physics made ahead of need failed: {failed.Message}", failed);
            }
            foreach (var world in _madeAhead)
            {
                if (IndexOf(_inUse, world.Body) >= 0)
                    _released.Add(world);
                else
                    Use(world);
            }
            _madeAhead.Clear();
            _madeAheadCount = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var world in _released)
                world.Dispose();
            foreach (var world in _madeAhead)
                world.Dispose();
            _released.Clear();
            _madeAhead.Clear();
        }
        foreach (var world in _inUse)
            world.Dispose();
        _inUse.Clear();
        _emptySince.Clear();
    }
}
