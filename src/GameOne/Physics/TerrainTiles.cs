using System.Numerics;
using Brutal.JoltApi;
using Brutal.Pointers;
using Brutal.Numerics;

namespace GameOne.Physics;

/// <summary>
/// The ground the physics collides with: small static height fields (tiles) laid round each awake
/// craft near the ground, on a fixed grid so crafts near each other share them, and let go once
/// nothing has needed them for <see cref="Scene.TerrainTileLingerSeconds"/>. Each physics' tiles
/// are its body's (<see cref="Body"/>): each tile follows that body's ground
/// (<see cref="GroundBody.Height"/>), its curve baked into its heights: under the sea, its bed
/// (the water is no collider: <see cref="RocketPhysics"/> floats things on it).
/// </summary>
/// <remarks>
/// <para>
/// A tile's shape is made ahead of need on the one worker thread every world shares
/// (<see cref="TileWorker"/>, <see cref="JoltShared.Tiles"/>; each job its body's), so the step
/// mostly only adds a finished one's body: working out its heights and making its shape were most
/// of what the tiles cost the step. Only the cells a craft could reach within
/// <see cref="Scene.TerrainTileUrgentSeconds"/> are made in the step, at once, whatever the worker
/// has (see <see cref="Update"/>), so nothing ever waits for the worker or goes without its ground.
/// </para>
/// <para>
/// The grid's cells are squares of each cube face (<see cref="CubeSphere"/>), equal-angle, keyed
/// (face, i, j). A tile is centred on its cell, in the tangent plane there, and is wide enough to
/// cover the whole cell with room to spare (<see cref="Slack"/>) and a sample's overlap, so the
/// tiles of neighbouring cells overlap and leave no gaps, even at a cube corner, where a cell is a
/// rhombus (the faces meet at 120° there).
/// </para>
/// <para>
/// The tile's frame is turned to its cell's own across direction (the face's u), not east and
/// north: those turn right round near the poles (and are undefined on them), which would need
/// smaller cells there to stay covered.
/// </para>
/// <para>
/// The tiles' bodies take Jolt IDs from a range of their own, the first <see cref="Scene.MaxTerrainTiles"/>
/// indices, so a vessel's ID never depends on how many tiles have been made before it (Jolt orders
/// some of its work by body ID, and tiles are laid at times that vary). The constructor, run before
/// any other body is made, fills the range with placeholders (tiny static bodies, never added to the
/// world, so they neither collide nor cost a step). Laying a tile destroys its slot's placeholder and
/// makes the tile with that ID; taking one out makes the placeholder again. Each make comes right
/// after its destroy, on the main thread, so Jolt's own allocator never takes a slot in the range.
/// </para>
/// </remarks>
internal sealed class TerrainTiles : IDisposable
{
    /// <summary>A cell of the tiles' grid: a cube face, and which square of it each way.</summary>
    internal readonly record struct Cell(int Face, int I, int J);

    /// <summary>A tile in the world: its body, and our own reference to its shape and the elevation of its centre, kept for when it is taken out.</summary>
    private sealed class Tile(uint body, Shape shape, float centre)
    {
        public uint Body { get; } = body;
        public Shape Shape { get; } = shape;
        public float Centre { get; } = centre;
        public double LastUsed { get; set; }
    }

    /// <summary>A shape kept after its tile was taken out (our reference to it, the body's having gone with the body), and its centre's elevation.</summary>
    private sealed record Kept(Cell Cell, Shape Shape, float Centre);

    /// <summary>How far apart the points a craft's stretch of ground is sampled at are, metres.</summary>
    private const float SampleStep = 4f;

    /// <summary>
    /// How far past its cell a tile reaches at least (besides the sample's overlap). A point within
    /// the margin is within this of a sampled point (half the sample grid's diagonal), whose cell's
    /// tile then covers it.
    /// </summary>
    private static readonly float Slack = SampleStep / MathF.Sqrt(2f);

    private static readonly int Samples = Scene.TerrainTileSamples;
    private static readonly float Half = (Samples - 1) * Scene.TerrainTileSpacing / 2f;

    /// <summary>
    /// Cells across a cube face of <paramref name="body"/>, each way. A cell's width on the ground,
    /// at its middle, is at most the face's quarter-circle at the highest ground over this; at a
    /// cube corner its rhombus spans 0.71 of that either side of its centre (0.94 of the width per
    /// side, times 0.75, in the frame along its u), so the width is set to fit that inside the tile
    /// with its slack and overlap (and a tenth to spare). The tests check every kind of cell fits.
    /// Each body keeps its own (<see cref="GroundBody.TileCellsPerFace"/>).
    /// </summary>
    internal static int CellsFor(GroundBody body)
    {
        var room = Half - Scene.TerrainTileSpacing - Slack;
        var width = room * MathF.Sqrt(2f) * 0.9f;
        var highest = body.SeaRadius + body.Map.MaxHeight + Scene.FineDetailMargin; // the fine detail's few metres on top
        return (int)MathF.Ceiling(MathF.PI / 2f * highest / width);
    }

    private GroundBody _body;
    private readonly BodyInterface _bodies;
    private readonly ushort _layer;
    private readonly Func<double> _clock;
    private readonly Shape _placeholderShape;
    private readonly Stack<uint> _free = new(Scene.MaxTerrainTiles); // the range's IDs with no tile in them (a placeholder stands there)
    private readonly Dictionary<Cell, Tile> _tiles = [];
    private readonly List<(Craft Craft, float Altitude, Vector3 Position, Vector3 Velocity, float HalfLength)> _near = [];
    private readonly List<Cell> _gone = [];
    private readonly HashSet<Cell> _urgent = []; // an urgent craft's cells within the urgent time, worked out each update
    private readonly Dictionary<Cell, double> _wants = []; // the cells needed and not laid this update, each by when it is due
    private readonly Dictionary<Cell, LinkedListNode<Kept>> _kept = []; // the shapes of tiles taken out (or the worker's not laid) ...
    private readonly LinkedList<Kept> _keptOrder = new(); // ... the least recently kept first
    private readonly List<(Cell Cell, double Due)> _ask = []; // the cells wanted of the worker this update, handed over at once
    private readonly List<(Cell Cell, Shape Shape, float Centre)> _taken = []; // what the worker had finished, as taken ...
    private readonly Dictionary<Cell, (Shape Shape, float Centre)> _fresh = []; // ... by cell, until laid or kept, within an update
    private readonly float[] _heights = new float[Samples * Samples]; // the step's own (the worker has its own)
    private TileWorker.Jobs _worker;
    private readonly Action<uint> _removed;

    /// <remarks>
    /// Run before any other body is made: the placeholders it makes must take Jolt's first indices
    /// (0 to <see cref="Scene.MaxTerrainTiles"/> - 1), which is what makes <see cref="IsTileBody"/> true of them.
    /// </remarks>
    /// <param name="body">The body whose ground they are, in its frame.</param>
    /// <param name="bodies">The physics world's bodies, to add the tiles to.</param>
    /// <param name="layer">The object layer the tiles go in: the one that doesn't move.</param>
    /// <param name="clock">The physics' time, in seconds, for how long a tile has gone unused.</param>
    /// <param name="removed">
    /// Told each tile's body as it is taken out, so what touched it forgets it at once: Jolt reports
    /// a contact's end only in its next step, and none for a sleeping body taken out.
    /// </param>
    public TerrainTiles(GroundBody body, BodyInterface bodies, ushort layer, Func<double> clock, Action<uint> removed)
    {
        (_body, _bodies, _layer, _clock, _removed) = (body, bodies, layer, clock, removed);
        using var shapeSettings = SphereShapeSettings.Create(0.05f);
        _placeholderShape = ((ShapeSettings)shapeSettings).CreateShape();
        var ids = new uint[Scene.MaxTerrainTiles];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = MakePlaceholder(null).GetID();
            if (!IsTileBody(ids[i]))
                throw new InvalidOperationException($"The tiles' bodies must be the world's first, but body {i} took index {ids[i] & IndexMask}: the physics world already had bodies.");
        }
        for (var i = ids.Length - 1; i >= 0; i--)
            _free.Push(ids[i]); // the lowest first

        // The worker works out its heights on its own thread, not over the thread pool: out of the
        // way of Jolt's job threads and of a tile the step is making itself. It touches Jolt only to
        // make the shape (allowed on any thread); its bodies are made here, on the main thread. Each
        // job carries this body, so what the worker reads is this world's ground whatever else it
        // makes. Its thread starts only on the first cell any world wants.
        _worker = Open(body);
    }

    /// <summary>This world's jobs for <paramref name="body"/>'s ground, opened on the worker.</summary>
    private static TileWorker.Jobs Open(GroundBody body) =>
        JoltShared.Tiles.Open((cell, heights) =>
        {
            var centre = Heights(body, cell, heights, parallel: false);
            return (MakeShape(heights), centre);
        });

    /// <summary>Asks the worker for nothing more of this world's (see <see cref="TileWorker.Jobs.Drop"/>): the tiles laid and shapes kept stay.</summary>
    public void DropWants() => _worker.Drop();

    /// <summary>
    /// Makes them <paramref name="body"/>'s ground instead, for a world reused for another body
    /// (<see cref="RocketPhysics.Rebuild"/>): every tile laid taken out (its placeholder made again),
    /// every shape kept or finished let go, and this world's jobs on the worker closed and opened
    /// again for the new ground. The placeholders stay, at Jolt's first indices.
    /// </summary>
    public void Rebuild(GroundBody body)
    {
        _worker.Dispose();
        foreach (var (shape, _) in _fresh.Values)
            shape.Dispose();
        _fresh.Clear();
        foreach (var cell in _tiles.Keys.ToArray())
            Remove(cell);
        foreach (var kept in _keptOrder)
            kept.Shape.Dispose();
        _keptOrder.Clear();
        _kept.Clear();
        _wants.Clear();
        _ask.Clear();
        _taken.Clear();
        _urgent.Clear();
        _near.Clear();
        _gone.Clear();
        _body = body;
        _worker = Open(body);
    }

    /// <summary>The body whose ground the tiles are.</summary>
    public GroundBody Body => _body;

    /// <summary>Cells across a cube face of its body, each way.</summary>
    public int CellsPerFace => _body.TileCellsPerFace;

    /// <summary>This world's jobs on the worker making the tiles' shapes ahead of need.</summary>
    internal TileWorker.Jobs Worker => _worker;

    /// <summary>Closes this world's jobs on the worker (waiting for a tile of its in hand), before the physics world goes. Idempotent; <see cref="Dispose"/> does it too.</summary>
    public void StopWorker() => _worker.Dispose();

    /// <summary>The low 23 bits of a Jolt body ID are its index; the next 8 are a sequence number.</summary>
    private const uint IndexMask = 0x7FFFFF;

    /// <summary>
    /// <paramref name="id"/> with its sequence number one on (wrapping within its 8 bits), the index kept: a slot's
    /// body is remade under this, so an old ID of the slot never resolves to the new body (Jolt's stale-ID protection).
    /// </summary>
    private static uint Bumped(uint id) => (id & IndexMask) | ((((id >> 23) + 1) & 0xFFu) << 23);

    /// <summary>Whether <paramref name="id"/> is of a body in the range reserved for the tiles (a tile, or a placeholder for one).</summary>
    public static bool IsTileBody(uint id) => (id & IndexMask) < Scene.MaxTerrainTiles;

    /// <summary>The IDs of the tiles' bodies, in the world now.</summary>
    public IEnumerable<uint> TileBodies => _tiles.Values.Select(t => t.Body);

    /// <summary>A placeholder body, not in the world: at <paramref name="id"/> if given, else at the lowest index free.</summary>
    private Body MakePlaceholder(uint? id)
    {
        using var settings = BodyCreationSettings.Create(_placeholderShape, new float3(0f, 0f, 0f), new floatQuat(0f, 0f, 0f, 1f), MotionType.Static, _layer);
        return id is { } at ? _bodies.CreateBodyWithID(at, settings) : _bodies.CreateBody(settings);
    }

    /// <summary>How many tiles are in the world.</summary>
    public int Count => _tiles.Count;

    /// <summary>How many times <see cref="Update"/> has been called.</summary>
    public long Updates { get; private set; }

    /// <summary>How many tiles have been taken out of the world so far.</summary>
    public long Removals { get; private set; }

    /// <summary>How many tiles have been made from the terrain's heights (their shapes too) in the step so far.</summary>
    public long MadeInStep { get; private set; }

    /// <summary>How many tiles have been laid from a kept shape so far.</summary>
    public long FromKept { get; private set; }

    /// <summary>How many tiles have been laid from a shape the worker made so far.</summary>
    public long FromWorker { get; private set; }

    /// <summary>Whether a shape is kept for <paramref name="cell"/>.</summary>
    internal bool IsKept(Cell cell) => _kept.ContainsKey(cell);

    /// <summary>
    /// Takes the kept shape of <paramref name="cell"/>, if there is one: the caller then owns its
    /// reference (and must dispose it), and it is no longer kept.
    /// </summary>
    private bool TryTakeKept(Cell cell, out Shape shape, out float centre)
    {
        if (!_kept.Remove(cell, out var node))
        {
            (shape, centre) = (default!, 0f);
            return false;
        }
        _keptOrder.Remove(node);
        (shape, centre) = (node.Value.Shape, node.Value.Centre);
        return true;
    }

    /// <summary>
    /// Keeps <paramref name="shape"/> (the caller's reference, now ours) for <paramref name="cell"/>, letting
    /// the least recently kept go (disposed) past <see cref="Scene.KeptTerrainShapes"/>.
    /// </summary>
    private void Keep(Cell cell, Shape shape, float centre)
    {
        if (_kept.Remove(cell, out var old))
        {
            _keptOrder.Remove(old);
            old.Value.Shape.Dispose();
        }
        _kept[cell] = _keptOrder.AddLast(new Kept(cell, shape, centre));
        while (_kept.Count > Scene.KeptTerrainShapes)
        {
            var first = _keptOrder.First!;
            _keptOrder.RemoveFirst();
            _kept.Remove(first.Value.Cell);
            first.Value.Shape.Dispose();
        }
    }

    /// <summary>Whether the tile for <paramref name="cell"/> is in the world.</summary>
    public bool Has(Cell cell) => _tiles.ContainsKey(cell);

    /// <summary>
    /// Adds the tiles each awake craft near the ground needs (see <see cref="Needed"/>), while there
    /// is room for them (<see cref="Scene.MaxTerrainTiles"/>, making room by letting go the longest
    /// unused), and takes out those nothing has needed for <see cref="Scene.TerrainTileLingerSeconds"/>.
    /// Those it could reach within <see cref="Scene.TerrainTileUrgentSeconds"/> are urgent: laid
    /// here, at once, from the worker's shape if it has finished one (taken before them), else a
    /// kept one, else made here, never waiting for what the worker has in hand. The rest are
    /// wanted of the worker, soonest first, and laid as it finishes them (or at once from a kept
    /// shape). A frozen craft about to wake (<see cref="Craft.WakingSoon"/>) has its cells wanted
    /// too, at once but never urgently, so they are ready by the time it wakes; a wreck has none
    /// laid for it. <paramref name="motion"/> is where a craft is (the middle of it), how fast it is
    /// moving, and the way from its middle to its top end.
    /// </summary>
    /// <remarks>
    /// The urgent time is in steps, not game seconds: <see cref="Scene.TerrainTileUrgentSeconds"/>
    /// of steps at their full length, so shortened with them by <paramref name="stepScale"/> (the
    /// physics' <see cref="RocketPhysics.StepScale"/>, in a forced slow-down). The steps a real
    /// second stay the same as they shorten, so that is the same real time for the worker as at
    /// full speed; a quarter of a game second slowed twentyfold was five real seconds' travel,
    /// made in the step all at once as a slowed dive turned urgent (a 4,000 m/s dive at the rough
    /// slope at 10° made 42 in one step, 23 ms, the worker not yet caught up on them).
    /// </remarks>
    public void Update(IReadOnlyList<Craft> crafts, Func<Craft, (Vector3 Position, Vector3 Velocity, Vector3 HalfLength)> motion, float stepScale = 1f)
    {
        var now = _clock();
        var urgentSeconds = Scene.TerrainTileUrgentSeconds * stepScale;
        Updates++;
        _near.Clear();
        for (var i = 0; i < crafts.Count; i++)
        {
            var craft = crafts[i];
            if (craft.Removed || (craft.Frozen && !craft.WakingSoon))
                continue; // a frozen craft is still in effect: it needs nothing under it, till it is about to wake
            // Nor does a wreck, left in the world only for its hit on another craft to play out
            // (unseen, unjudged, gone in Scene.WreckSeconds): the tiles already laid stay for it,
            // but a new way it is thrown along by the hit would want a whole new urgent stretch
            // at once (at the ground's top speed, a kilometre's: 27 tiles made in one step, 15 ms).
            if (craft.WreckedFor is not null)
                continue;
            var (position, velocity, half) = motion(craft);
            // Faster than a fast collision, it can do little but go where its path takes it: its ground
            // is only where that comes down (Craft.GroundAhead, below), never all under its way.
            if (velocity.LengthSquared() > Scene.FastCollisionSpeed * Scene.FastCollisionSpeed)
                continue;
            // How high it is over the ground (over the sea, from its bed: all there is to hit),
            // measured from its middle or either end, whichever is lowest. So one lying across a
            // cliff's edge (the sea ice's, over deep water), its middle far above the ground and
            // its end on it, keeps its tiles; and every craft standing or coming down upright gets
            // them from its bottom end's height, half its length sooner than from its middle's.
            // (They are still laid round its middle.) It is the ground the tiles are made of, not
            // what stands on it: over a raft (a floe, the drone ship's deck), not from its top, or
            // every craft crossing the floe band has the seabed laid under it, however far down.
            var altitude = MathF.Min(AboveTheGround(position), MathF.Min(AboveTheGround(position - half), AboveTheGround(position + half)));
            if (altitude < Scene.TerrainTileReach + velocity.Length() * Scene.TerrainTileLookahead)
                _near.Add((craft, altitude, position, velocity, half.Length()));
        }
        _near.Sort((a, b) => a.Altitude.CompareTo(b.Altitude)); // the nearest the ground first, while there is room

        // What the worker has finished is taken first, so a cell it has made is laid from its
        // shape, urgent or not, never made again here: when it lags, the cell it has just finished
        // is the very one about to turn urgent. Those not laid in this update are kept, at its end.
        _taken.Clear();
        _worker.TryTake(_taken); // skipped if the worker holds its lock just now: taken next update
        foreach (var (cell, shape, centre) in _taken)
            _fresh.Add(cell, (shape, centre)); // the worker makes a cell once until it is taken
        _taken.Clear();

        // Then, for every craft, the cells it could reach within the urgent time: laid here, at
        // once (from the worker's shape or a kept one if there is one, else made here), as it may
        // be on them before the worker could finish them. (If the worker has one in hand, it is
        // made here all the same, never waited for: the worker's, when it comes, is kept.) It is
        // urgent only while its lowest end is lower than it could go in that time, and a short way
        // more, over the ground under it or anywhere along that stretch (see UrgentAhead). These
        // cells are worked out every update, not cached as the whole stretch's are below, and only
        // for a craft that is urgent; caching them too is in TODO.md ("each craft's urgent cells
        // cached"), with what it would gain.
        foreach (var (craft, altitude, position, velocity, half) in _near)
        {
            if (craft.Frozen)
                continue; // about to wake, not yet: its cells are only wanted (below), ahead of need
            var within = Scene.TerrainTileUrgent + velocity.Length() * urgentSeconds;
            if (altitude >= within && !UrgentAhead(position, velocity, half, within, urgentSeconds))
                continue;
            _urgent.Clear();
            Needed(_body, position, position + velocity * urgentSeconds, _urgent, SeekSlack);
            foreach (var cell in _urgent)
            {
                if (_tiles.TryGetValue(cell, out var tile))
                    tile.LastUsed = now;
                else if (MakeRoom(now))
                    Add(cell, now);
            }
        }

        // Then every cell of each craft's whole stretch not yet laid is wanted, by when the craft
        // could reach it (its distance over the craft's speed, at least 1 m/s, from now: the
        // tiles' clock), the soonest of the crafts that want it.
        _wants.Clear();
        foreach (var (craft, _, position, velocity, _) in _near)
        {
            // The cells it needs are worked out again only once either end of its stretch has
            // gone SeekSlack from where they were, that much wider, so they always cover it: the
            // grid of points across it, for every craft each step, cost more than all the rest.
            var to = position + velocity * Scene.TerrainTileLookahead;
            var cells = craft.TileCells;
            if (!(Vector3.DistanceSquared(position, craft.TilesFrom) <= SeekSlack * SeekSlack
                  && Vector3.DistanceSquared(to, craft.TilesTo) <= SeekSlack * SeekSlack))
            {
                cells.Clear();
                Needed(_body, position, to, cells, SeekSlack);
                (craft.TilesFrom, craft.TilesTo) = (position, to);
            }
            var speed = MathF.Max(velocity.Length(), 1f);
            float? ground = null; // the cells' centres taken at the height of the ground under it: worked out only once one is wanted
            foreach (var cell in cells)
            {
                if (_tiles.TryGetValue(cell, out var tile))
                {
                    tile.LastUsed = now;
                    continue;
                }
                ground ??= _body.SeaRadius + _body.Height(_body.Up(position));
                // One frozen and about to wake needs its ground as soon as it wakes: now, at the
                // latest (it doesn't move to it, so its distance says nothing of when).
                var due = craft.Frozen ? now : now + Vector3.Distance(position, _body.Centre + CentreOf(_body, cell) * ground.Value) / speed;
                _wants[cell] = _wants.TryGetValue(cell, out var sooner) ? Math.Min(sooner, due) : due;
            }
        }

        // And a fast craft's ground (it has none above, under its way): where it is predicted to
        // hit the ground (Craft.GroundAhead, the flight's look ahead), the stretch of its way in
        // within reach of the ground there. Wanted now, by when it will reach each cell, so the
        // worker makes them while it is still far off; laid here at once once it is due within
        // the urgent time. Some go unused if it turns aside; they are let go as any other.
        for (var i = 0; i < crafts.Count; i++)
        {
            var craft = crafts[i];
            if (craft.GroundAhead is not { } ahead)
                continue;
            if (craft.Removed || craft.Frozen || craft.WreckedFor is not null)
                continue;
            if (double.IsNaN(craft.GroundAheadFrom))
                craft.GroundAheadFrom = now;
            ahead.Seconds = (float)Math.Max(0.0, ahead.Seconds - (now - craft.GroundAheadFrom)); // this physics update's later steps
            var cells = craft.AheadCells;
            if (!(Vector3.DistanceSquared(ahead.At, craft.AheadAt) <= SeekSlack * SeekSlack)) // (never worked out yet: NaN, so not <=)
            {
                cells.Clear();
                // Back along its way in as far as it is within reach of the ground (by how steeply it
                // comes down, the ground taken as level there), and a margin on past where it hits it.
                var steep = MathF.Max(-Vector3.Dot(ahead.Arriving, _body.Up(ahead.At)), 0.02f);
                var stretch = MathF.Min(Scene.TerrainTileReach / steep, Scene.TerrainTileUrgent + ahead.Speed * Scene.TerrainTileUrgentSeconds);
                Needed(_body, ahead.At - ahead.Arriving * stretch, ahead.At + ahead.Arriving * Scene.TerrainTileMargin, cells, SeekSlack);
                craft.AheadAt = ahead.At;
            }
            var speed = MathF.Max(ahead.Speed, 1f);
            var radius = (ahead.At - _body.Centre).Length();
            foreach (var cell in cells)
            {
                if (_tiles.TryGetValue(cell, out var tile))
                {
                    tile.LastUsed = now; // kept for it, not let go before it comes
                    continue;
                }
                var due = now + Math.Max(0.0, ahead.Seconds - Vector3.Distance(_body.Centre + CentreOf(_body, cell) * radius, ahead.At) / speed);
                if (due - now <= urgentSeconds + Scene.TerrainTileUrgent / speed) // the urgent time, and its margin: as the urgent ones above
                {
                    if (MakeRoom(now))
                        Add(cell, now); // due within the urgent time: laid here, as the urgent ones above
                    continue;
                }
                _wants[cell] = _wants.TryGetValue(cell, out var sooner) ? Math.Min(sooner, due) : due;
            }
        }

        // The rest wanted: laid at once from the worker's shape or a kept one (no heights to work
        // out, so no reason to wait), else asked of the worker, all in one hand-over. A cell no
        // longer wanted is dropped from its list. With every tile in use and none able to go,
        // nothing is asked: the worker would make shapes only to have them kept, pushing out other
        // kept ones to be made again, and never rest; it goes idle, and is asked again once there
        // is room.
        _ask.Clear();
        var full = !CouldMakeRoom(now);
        foreach (var (cell, due) in _wants)
        {
            if (full)
                break;
            if (_tiles.ContainsKey(cell))
                continue; // laid meanwhile (as urgent)
            if (_fresh.ContainsKey(cell) || _kept.ContainsKey(cell))
            {
                if (!MakeRoom(now))
                {
                    full = true; // kept until there is room
                    break;
                }
                if (_fresh.Remove(cell, out var made))
                {
                    LayMade(cell, now, made.Shape, made.Centre);
                    FromWorker++;
                    continue;
                }
                if (TryTakeKept(cell, out var shape, out var centre)) // making room keeps another, which may have let this one go
                {
                    LayMade(cell, now, shape, centre);
                    FromKept++;
                    continue;
                }
            }
            _ask.Add((cell, due));
        }
        _worker.TryWant(_ask, now); // skipped if the worker holds its lock just now: the next update asks again

        // What the worker finished and wasn't laid (no longer wanted, laid meanwhile as urgent, or
        // no room) is kept, for laying when it is wanted.
        foreach (var (cell, (shape, centre)) in _fresh)
            Keep(cell, shape, centre);
        _fresh.Clear();

        _gone.Clear();
        foreach (var (cell, tile) in _tiles)
            if (now - tile.LastUsed > Scene.TerrainTileLingerSeconds)
                _gone.Add(cell);
        foreach (var cell in _gone)
            Remove(cell);
    }

    /// <summary>How high <paramref name="p"/> is over its body's ground (<see cref="GroundBody.TerrainAltitude"/>: under the sea, its bed), what the tiles are made of.</summary>
    private float AboveTheGround(Vector3 p) => _body.TerrainAltitude(p);

    /// <summary>The most points <see cref="UrgentAhead"/> looks at along a stretch (past it they spread out: 16 m apart up to 4,096 m/s).</summary>
    private const int MostAhead = 64;

    /// <summary>
    /// Whether a craft at <paramref name="position"/> (its middle, <paramref name="half"/> from either
    /// end) moving at <paramref name="velocity"/> comes within <paramref name="within"/> of the ground
    /// anywhere along its stretch of <paramref name="seconds"/> (the urgent time: see <see cref="Update"/>): over a cliff or a
    /// steep slope ahead, the ground under it alone could leave a face it is about to hit out of
    /// the urgent cells, to the worker. The points are at most <see cref="Scene.TerrainTileMargin"/>
    /// apart, so ground under 60° can't hide the urgent height (30 m) between two; its lowest end is
    /// taken as its half length under its middle, whichever way it lies. (Urgency is judged again
    /// every step, so the ground under it alone misses only a face rising that much within one
    /// step's travel: none on this map, but cliffs may come.)
    /// </summary>
    private bool UrgentAhead(Vector3 position, Vector3 velocity, float half, float within, float seconds)
    {
        var length = velocity.Length() * seconds;
        var count = Math.Min((int)MathF.Ceiling(length / Scene.TerrainTileMargin), MostAhead);
        for (var k = 1; k <= count; k++)
            if (AboveTheGround(position + velocity * (seconds * k / count)) - half < within)
                return true;
        return false;
    }

    /// <summary>How far (metres) either end of a craft's stretch of ground can go before its cells are worked out again (see <see cref="Update"/>).</summary>
    private const float SeekSlack = 2f;

    /// <summary>
    /// The cells under the stretch of ground from below <paramref name="from"/> to below
    /// <paramref name="to"/>, widened by <see cref="Scene.TerrainTileMargin"/> (and
    /// <paramref name="wider"/> more): the points of a grid <see cref="SampleStep"/> apart across
    /// it, in the tangent plane on the ground below <paramref name="from"/>, each in its cell.
    /// </summary>
    /// <remarks>
    /// The grid is laid along the stretch (its rows along it, its columns across), so only the
    /// stretch's own strip is walked: a grid square to the plane's axes would walk the stretch's
    /// whole bounding box, which for a long stretch lying across them is most of a square its
    /// length wide (at 8,000 m/s, slowed for a crash, about five times the time). Any square grid this fine covers
    /// it alike: a point within the margin (and <paramref name="wider"/>) of the stretch is within
    /// <see cref="Slack"/> of a point of the grid, which is then within the reach kept.
    /// </remarks>
    internal static void Needed(GroundBody body, Vector3 from, Vector3 to, HashSet<Cell> cells, float wider = 0f)
    {
        var up = Vector3.Normalize(from - body.Centre);
        var ground = body.SeaRadius + body.Height(up);
        var a = Vector3.Normalize(Vector3.Cross(up, MathF.Abs(up.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
        var b = Vector3.Cross(up, a);
        var along = Vector3.Normalize(to - body.Centre) * ground - up * ground;
        var end = new Vector2(Vector3.Dot(along, a), Vector3.Dot(along, b));

        var reach = Scene.TerrainTileMargin + wider + Slack;
        var length = end.Length();
        // The strip's own axes in the plane: along the stretch, and across it (a's way for none).
        var way = length > 1e-3f ? end / length : Vector2.UnitX;
        var across = new Vector2(-way.Y, way.X);
        var countAlong = (int)MathF.Ceiling((length + 2f * reach) / SampleStep) + 1;
        var countAcross = (int)MathF.Ceiling(2f * reach / SampleStep) + 1;
        for (var i = 0; i < countAlong; i++)
        {
            var s = -reach + i * SampleStep;
            // Past either end, only the round cap within the reach of it.
            var off = s < 0f ? -s : s > length ? s - length : 0f;
            for (var j = 0; j < countAcross; j++)
            {
                var w = -reach + j * SampleStep;
                if (off * off + w * w > reach * reach)
                    continue;
                var q = way * s + across * w;
                cells.Add(CellOf(body, up * ground + a * q.X + b * q.Y));
            }
        }
    }

    /// <summary>The cell of <paramref name="body"/>'s grid <paramref name="direction"/> (from its centre, any length) falls in.</summary>
    public static Cell CellOf(GroundBody body, Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(Vector3.Normalize(direction));
        var n = body.TileCellsPerFace;
        return new Cell(face, Math.Clamp((int)((u + 1f) * 0.5f * n), 0, n - 1), Math.Clamp((int)((v + 1f) * 0.5f * n), 0, n - 1));
    }

    /// <summary>A cell's corner (0 to n each way), as a unit direction.</summary>
    public static Vector3 Corner(GroundBody body, int face, int i, int j)
    {
        var n = body.TileCellsPerFace;
        return CubeSphere.FromFace(face, -1f + 2f * i / n, -1f + 2f * j / n);
    }

    /// <summary>
    /// The tile's frame for <paramref name="cell"/>: up at its centre, and across along the face's
    /// u there (<paramref name="across"/>) and at right angles to it (<paramref name="along"/>,
    /// across × up), as a right-handed frame.
    /// </summary>
    public static Vector3 Frame(GroundBody body, Cell cell, out Vector3 across, out Vector3 along)
    {
        var (u, v) = FaceCentre(body, cell);
        var up = CubeSphere.FromFace(cell.Face, u, v);
        var du = CubeSphere.FromFace(cell.Face, u + 1e-3f, v) - CubeSphere.FromFace(cell.Face, u - 1e-3f, v);
        across = Vector3.Normalize(du - up * Vector3.Dot(du, up));
        along = Vector3.Cross(across, up);
        return up;
    }

    /// <summary>The middle of <paramref name="cell"/>, in its face's coordinates (-1 to 1 each way).</summary>
    private static (float U, float V) FaceCentre(GroundBody body, Cell cell)
    {
        var n = body.TileCellsPerFace;
        return (-1f + (cell.I + 0.5f) * 2f / n, -1f + (cell.J + 0.5f) * 2f / n);
    }

    /// <summary>The middle of <paramref name="cell"/>, as a unit direction: its tile's up (as <see cref="Frame"/>, without the rest).</summary>
    public static Vector3 CentreOf(GroundBody body, Cell cell)
    {
        var (u, v) = FaceCentre(body, cell);
        return CubeSphere.FromFace(cell.Face, u, v);
    }

    /// <summary>
    /// The tile's heights for <paramref name="cell"/> into <paramref name="heights"/> (row by row
    /// along <c>along</c>, each row along <c>across</c>), measured up from the ground at its
    /// centre, which is returned (an elevation). Each is the solid ground straight above or below
    /// its point of the frame's plane: found along the radial line through the ground there, which
    /// is worked out twice (the first guess at the centre's height), as the line leans a little
    /// with height. With <paramref name="parallel"/>, its rows are spread over the thread pool (the
    /// step's, wanting it at once); without, worked out on this thread alone (the worker's).
    /// </summary>
    internal static float Heights(GroundBody body, Cell cell, float[] heights, bool parallel = true)
    {
        var up = Frame(body, cell, out var across, out var along);
        var centre = body.Height(up);
        var radius = (double)body.SeaRadius; // heights are above its zero
        var spacing = Scene.TerrainTileSpacing;
        // Each row is its own work, written to its own part of the array: the same whatever the threads.
        if (parallel)
            Parallel.For(0, Samples, Row);
        else
            for (var row = 0; row < Samples; row++)
                Row(row);
        return centre;

        void Row(int row)
        {
            var z = (row - (Samples - 1) / 2f) * spacing;
            for (var column = 0; column < Samples; column++)
            {
                var x = (column - (Samples - 1) / 2f) * spacing;
                var y = 0.0;
                for (var pass = 0; pass < 2; pass++)
                {
                    var direction = Vector3.Normalize(up * (float)(radius + centre + y) + across * x + along * z);
                    var h = body.Height(direction);
                    y = Math.Sqrt((radius + h) * (radius + h) - x * x - z * z) - (radius + centre);
                }
                heights[row * Samples + column] = (float)y;
            }
        }
    }

    /// <summary>A tile's height field from its <paramref name="heights"/> (see <see cref="Heights"/>), centred on its frame's origin.</summary>
    internal static Shape MakeShape(float[] heights) => HeightFields.Create(heights, (uint)Samples,
        new Vector3(-Half, 0f, -Half), new Vector3(Scene.TerrainTileSpacing, 1f, Scene.TerrainTileSpacing));

    /// <summary>Whether <see cref="MakeRoom"/> would find room, without making it.</summary>
    private bool CouldMakeRoom(double now) => _tiles.Count < Scene.MaxTerrainTiles || _tiles.Values.Any(t => t.LastUsed < now);

    /// <summary>Whether there is room for another tile, letting the longest unused one go if need be (one in use now never goes).</summary>
    private bool MakeRoom(double now)
    {
        if (_tiles.Count < Scene.MaxTerrainTiles)
            return true;
        Cell? oldest = null;
        var at = double.PositiveInfinity;
        foreach (var (cell, tile) in _tiles)
            if (tile.LastUsed < now && tile.LastUsed < at)
                (oldest, at) = (cell, tile.LastUsed);
        if (oldest is not { } go)
            return false;
        Remove(go);
        return true;
    }

    /// <summary>Lays the tile for <paramref name="cell"/> now, in the step: from the worker's shape or its kept one if there is one, else made here from the terrain.</summary>
    private void Add(Cell cell, double now)
    {
        if (_fresh.Remove(cell, out var made))
        {
            LayMade(cell, now, made.Shape, made.Centre);
            FromWorker++;
            return;
        }
        // A cell whose tile was taken out lately (or the worker made unasked) has its shape kept: no heights to work out.
        if (TryTakeKept(cell, out var shape, out var centre))
        {
            LayMade(cell, now, shape, centre);
            FromKept++;
            return;
        }
        var started = Timings.Start();
        centre = Heights(_body, cell, _heights);
        Timings.Stop(StepDetail.Samples, started);
        started = Timings.Start();
        shape = MakeShape(_heights);
        MadeInStep++;
        try
        {
            Lay(cell, now, shape, centre);
        }
        catch
        {
            shape.Dispose();
            throw;
        }
        Timings.Stop(StepDetail.Bodies, started);
    }

    /// <summary>Lays a tile from a shape made already (the worker's, or a kept one), which it takes: timed as <see cref="StepDetail.Adds"/>.</summary>
    private void LayMade(Cell cell, double now, Shape shape, float centre)
    {
        var started = Timings.Start();
        try
        {
            Lay(cell, now, shape, centre);
        }
        catch
        {
            shape.Dispose();
            throw;
        }
        Timings.Stop(StepDetail.Adds, started);
    }

    /// <summary>
    /// Makes the tile's body from <paramref name="shape"/> and adds it. The tile keeps our reference to
    /// the shape (the body takes its own), to keep it once the tile is taken out.
    /// </summary>
    private void Lay(Cell cell, double now, Shape shape, float centre)
    {
        var up = Frame(_body, cell, out var across, out var along);
        var rotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            across.X, across.Y, across.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            along.X, along.Y, along.Z, 0f,
            0f, 0f, 0f, 1f));
        var position = _body.Centre + up * (_body.SeaRadius + centre);
        using var settings = BodyCreationSettings.Create(shape, new float3(position.X, position.Y, position.Z),
            new floatQuat(rotation.X, rotation.Y, rotation.Z, rotation.W), MotionType.Static, _layer);
        settings.SetFriction(0.8f);
        settings.SetRestitution(0.1f);
        settings.SetUserData((ulong)SolidKind.Ground);
        // The slot's placeholder goes and the tile takes its ID, so Jolt's own allocator is never asked for one.
        var slot = _free.Pop();
        var id = Bumped(slot);
        _bodies.DestroyBody(slot);
        if (_bodies.CreateBodyWithID(id, settings).IsNull())
            throw new InvalidOperationException($"Jolt would not make a tile with body ID {id}: the range reserved for the tiles broke.");
        _bodies.AddBody(id, Activation.DontActivate);
        _tiles.Add(cell, new Tile(id, shape, centre) { LastUsed = now });
    }

    /// <summary>Takes the tile for <paramref name="cell"/> out of the world, keeping its shape (an update does, for one gone unused).</summary>
    internal void Remove(Cell cell)
    {
        var started = Timings.Start();
        var tile = _tiles[cell];
        _bodies.RemoveBody(tile.Body);
        _bodies.DestroyBody(tile.Body);
        _removed(tile.Body);
        // The tile is gone from here on, and its shape kept, before the placeholder is made: so a
        // placeholder Jolt refuses (the range broken, which is fatal) leaks no shape.
        _tiles.Remove(cell);
        Keep(cell, tile.Shape, tile.Centre);
        var slot = Bumped(tile.Body);
        if (MakePlaceholder(slot).IsNull())
            throw new InvalidOperationException($"Jolt would not make a placeholder with body ID {slot}: the range reserved for the tiles broke.");
        _free.Push(slot);
        Removals++;
        Timings.Stop(StepDetail.Removals, started);
    }

    public void Dispose()
    {
        _worker.Dispose(); // first: the worker may be making one of its shapes
        foreach (var (shape, _) in _fresh.Values)
            shape.Dispose(); // any taken in an update that threw before keeping them
        _fresh.Clear();
        foreach (var tile in _tiles.Values)
        {
            _bodies.RemoveBody(tile.Body);
            _bodies.DestroyBody(tile.Body);
            tile.Shape.Dispose();
        }
        _tiles.Clear();
        foreach (var kept in _keptOrder)
            kept.Shape.Dispose();
        _keptOrder.Clear();
        _kept.Clear();
        foreach (var id in _free)
            _bodies.DestroyBody(id);
        _free.Clear();
        _placeholderShape.Dispose();
    }
}

/// <summary>
/// Height-field shapes, made through Jolt's own constructor (<c>HeightFieldShapeSettings_Create</c>
/// honours its arguments since BRUTAL v2026.10.1). It keeps no state, so it can be called from any thread.
/// </summary>
internal static class HeightFields
{
    /// <summary>
    /// A height field of <paramref name="count"/> × <paramref name="count"/> samples (row by row):
    /// its points are <paramref name="offset"/> + <paramref name="scale"/> × (x, height, y). Jolt's
    /// default block size (2) is kept. Throws, with Jolt's reason, if Jolt refuses it.
    /// </summary>
    public static unsafe Shape Create(float[] samples, uint count, Vector3 offset, Vector3 scale)
    {
        if (samples.Length != count * count)
            throw new ArgumentException($"{samples.Length} samples for a {count} × {count} height field", nameof(samples));
        HeightFieldShapeSettings settings;
        try
        {
            // Jolt copies the samples while it builds the settings, so they need pinning only for this call.
            fixed (float* p = samples)
                settings = HeightFieldShapeSettings.Create(new Ptr<float>(p),
                    new float3(offset.X, offset.Y, offset.Z), new float3(scale.X, scale.Y, scale.Z),
                    count, default, []);
        }
        catch (JoltException e)
        {
            throw new InvalidOperationException($"Jolt refused the settings of a {count} × {count} height field: {e.Message}", e);
        }
        using (settings)
        {
            try
            {
                return ((ShapeSettings)settings).CreateShape();
            }
            catch (JoltException e)
            {
                throw new InvalidOperationException($"Jolt refused to make the shape of a {count} × {count} height field: {e.Message}", e);
            }
        }
    }
}
