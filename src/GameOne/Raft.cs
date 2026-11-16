using System.Numerics;

namespace GameOne;

/// <summary>
/// A raft's pose: where its waterline centre is and which way it faces. In its own frame x runs
/// along its length (its <see cref="Raft.Heading"/> at rest), y is up and z across.
/// </summary>
internal readonly record struct DeckPose(Vector3 Position, Quaternion Rotation)
{
    /// <summary>As a model matrix, for drawing (row vectors: rotate, then move).</summary>
    public Matrix4x4 Model => Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
}

/// <summary>The outline of a raft's top, seen from above: the drone ship's rectangle, or a floe's disc.</summary>
internal enum RaftShape { Box, Disc }

/// <summary>
/// Something riding the sea's waves (<see cref="Sea"/>) that a craft can stand on: the drone ship,
/// and the sea ice's floes. Its pose is a plane fitted to the water under its top, whose middle is
/// its heave and whose slopes are its pitch (about its z) and roll (about its x). It stays where it
/// is (the ship is moored, a floe held in the pack), so only its height and tilt move.
/// <see cref="Set(double)"/> fixes the moment the rest of the game reads (the ground queries, the physics,
/// the drawing), so they all agree on where its top is within a step.
/// </summary>
internal sealed class Raft
{
    /// <summary>The fit's grid over a box: this many points along its length, and across it.</summary>
    private const int Along = 5, Across = 3;

    /// <summary>The fit's points round a disc's rim (its centre is one more).</summary>
    private const int Rim = 8;

    /// <summary>How far either side of a moment the velocities are measured, in seconds.</summary>
    private const double Step = 0.05;

    /// <summary>How high above its top a point still counts as over it.</summary>
    private const float Reach = 50f;

    /// <param name="id">Which raft it is: the drone ships 0 and down (Home's 0), the floes from 1 (see <see cref="Rafts"/>).</param>
    /// <param name="shape">Its top's outline.</param>
    /// <param name="centre">Where its waterline's centre is on a calm sea.</param>
    /// <param name="heading">Its x at rest: a unit vector along the ground at <paramref name="centre"/>.</param>
    /// <param name="halfLength">Half its length (a disc: its radius).</param>
    /// <param name="halfBeam">Half its width (a disc: its radius).</param>
    /// <param name="freeboard">How far its top stands above the waterline.</param>
    /// <param name="draft">How far its bottom reaches below the waterline.</param>
    public Raft(int id, RaftShape shape, Vector3 centre, Vector3 heading, float halfLength, float halfBeam, float freeboard, float draft)
    {
        (Id, Shape, Centre, Heading, HalfLength, HalfBeam, Freeboard, Draft) = (id, shape, centre, heading, halfLength, halfBeam, freeboard, draft);
        // As far from its middle as a point over its top can be (see HeightAbove): its corner, out
        // as far along its normal as its bottom below (Draft under the waterline centre) or its
        // reach above (Reach over its top, Freeboard up).
        var deep = MathF.Max(Draft, Reach + Freeboard);
        _farSquared = HalfLength * HalfLength + HalfBeam * HalfBeam + deep * deep;
        _halfDiagonal = MathF.Sqrt(HalfLength * HalfLength + HalfBeam * HalfBeam);
        Current = Pose(0.0); // somewhere, before anything sets it
    }

    public int Id { get; }

    /// <summary>Whether it is a drone ship (one of <see cref="Rafts.Ships"/>), not a floe.</summary>
    public bool IsShip => Id <= 0;
    public RaftShape Shape { get; }
    public Vector3 Centre { get; }
    public Vector3 Heading { get; }
    public float HalfLength { get; }
    public float HalfBeam { get; }
    public float Freeboard { get; }
    public float Draft { get; }

    public DeckPose Current
    {
        get => _current;
        private set
        {
            _current = value;
            // Its axes as they are now, worked out once here: a point is looked for over the
            // rafts many times a step (every ship's, and the floes' near it), each by these.
            (_x, _y, _z) = (Vector3.Transform(Vector3.UnitX, value.Rotation), Vector3.Transform(Vector3.UnitY, value.Rotation), Vector3.Transform(Vector3.UnitZ, value.Rotation));
        }
    }

    private DeckPose _current;
    private Vector3 _x, _y, _z;
    private readonly float _farSquared, _halfDiagonal;

    /// <summary>How fast it is moving at <see cref="SetAt"/> (worked out the first time it is read then: see <see cref="Set(double)"/>).</summary>
    public Vector3 Velocity
    {
        get
        {
            Rates();
            return _velocity;
        }
    }

    /// <summary>How fast it is turning at <see cref="SetAt"/> (worked out with <see cref="Velocity"/>).</summary>
    public Vector3 AngularVelocity
    {
        get
        {
            Rates();
            return _angularVelocity;
        }
    }

    private Vector3 _velocity, _angularVelocity;
    private double _ratesAt = double.NaN; // the time they were worked out for

    /// <summary>The world time it was last <see cref="Set(double)"/> to (NaN until then).</summary>
    public double SetAt { get; private set; } = double.NaN;

    /// <summary>Its top's normal now.</summary>
    public Vector3 Normal => _y;

    /// <summary><see cref="Current"/> as a model matrix, for drawing (row vectors: rotate, then move).</summary>
    public Matrix4x4 Model => Current.Model;

    /// <summary>
    /// The pose at world time <paramref name="time"/>, from the plane fitted to the waves under its
    /// top: over a 5 × 3 grid on a box, its centre and 8 points round its rim on a disc.
    /// </summary>
    public DeckPose Pose(double time)
    {
        var (sin, cos) = Trig(time);

        // Least squares for height = heave + pitch * x + roll * z. Both footprints' points are
        // symmetric about the centre and about its axes, so the three unknowns separate: the
        // mean, and each slope over its own axis.
        var fit = _fit ??= new Fit(this);
        var (up, across) = (fit.RestUp, fit.RestAcross);
        float sum = 0f, alongMoment = 0f, acrossMoment = 0f, alongSquares = 0f, acrossSquares = 0f;
        for (var k = 0; k < fit.X.Length; k++)
        {
            var (x, z) = (fit.X[k], fit.Z[k]);
            // The sea's height there (Sea.Terms): the waves' terms, at these phases.
            var h = 0f;
            for (var i = 0; i < Sea.WaveCount; i++)
                h += fit.Sines[k * Sea.WaveCount + i] * cos[i] - fit.Cosines[k * Sea.WaveCount + i] * sin[i];
            sum += h;
            alongMoment += h * x;
            acrossMoment += h * z;
            alongSquares += x * x;
            acrossSquares += z * z;
        }

        var heave = sum / fit.X.Length;
        var pitch = alongMoment / alongSquares; // the slope along the length (every point counts alike)
        var roll = acrossMoment / acrossSquares; // the slope across it

        var xAxis = Vector3.Normalize(Heading + up * pitch);
        var yAxis = Vector3.Normalize(Vector3.Cross(across + up * roll, xAxis));
        var zAxis = Vector3.Cross(xAxis, yAxis);
        var basis = new Matrix4x4(
            xAxis.X, xAxis.Y, xAxis.Z, 0f,
            yAxis.X, yAxis.Y, yAxis.Z, 0f,
            zAxis.X, zAxis.Y, zAxis.Z, 0f,
            0f, 0f, 0f, 1f);
        return new DeckPose(Centre + up * heave, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis)));
    }

    /// <summary>
    /// The sines and cosines of the waves' phases at <paramref name="time"/>, the same for every
    /// raft: kept for the last time asked for, as the physics poses all of its rafts at one time,
    /// and the drawing every floe in sight at another, one after another.
    /// </summary>
    private static (float[] Sin, float[] Cos) Trig(double time)
    {
        if (!_trigAt.Equals(time))
        {
            Span<float> phases = stackalloc float[Sea.WaveCount];
            Sea.Phases(time, phases);
            for (var i = 0; i < Sea.WaveCount; i++)
                (_sin[i], _cos[i]) = MathF.SinCos(phases[i]);
            _trigAt = time;
        }
        return (_sin, _cos);
    }

    private static readonly float[] _sin = new float[Sea.WaveCount], _cos = new float[Sea.WaveCount];
    private static double _trigAt = double.NaN;

    /// <summary>
    /// The fit's points (see <see cref="Pose"/>), worked out the first time it is posed: where each
    /// is along and across from the centre, and the sea's waves there (<see cref="Sea.Terms"/>),
    /// read in the direction of the point on the top's plane, not the centre's. None of them
    /// changes, and the wave size and the sines are most of the work of reading the sea, so each
    /// pose after the first only adds up the waves: the floes in sight are posed every frame to be drawn.
    /// </summary>
    private sealed class Fit
    {
        public readonly float[] X, Z, Sines, Cosines; // the last two Sea.WaveCount to a point
        public readonly Vector3 RestUp, RestAcross;   // its y and z at rest

        public Fit(Raft raft)
        {
            RestUp = Planet.Up(raft.Centre);
            var across = RestAcross = Vector3.Cross(raft.Heading, RestUp); // z, so that x, y, z are right-handed
            var count = raft.Shape == RaftShape.Box ? Along * Across : Rim + 1;
            (X, Z) = (new float[count], new float[count]);
            (Sines, Cosines) = (new float[count * Sea.WaveCount], new float[count * Sea.WaveCount]);
            for (var k = 0; k < count; k++)
            {
                (X[k], Z[k]) = raft.Point(k);
                var direction = Vector3.Normalize(raft.Centre + raft.Heading * X[k] + across * Z[k] - Planet.Centre);
                Sea.Terms(direction, Sines.AsSpan(k * Sea.WaveCount, Sea.WaveCount), Cosines.AsSpan(k * Sea.WaveCount, Sea.WaveCount));
            }
        }
    }

    private Fit? _fit;

    /// <summary>
    /// The fit's <paramref name="k"/>th point, along and across from the centre: a box's grid, row
    /// by row along its length; a disc's centre, then round its rim.
    /// </summary>
    private (float X, float Z) Point(int k)
    {
        if (Shape == RaftShape.Box)
        {
            var (i, j) = (k / Across, k % Across);
            return (HalfLength * (2f * i / (Along - 1) - 1f), HalfBeam * (2f * j / (Across - 1) - 1f));
        }
        if (k == 0)
            return (0f, 0f);
        var angle = (k - 1) * (2f * MathF.PI / Rim);
        return (HalfLength * MathF.Cos(angle), HalfLength * MathF.Sin(angle));
    }

    /// <summary>Moves it to world time <paramref name="time"/>.</summary>
    public void Set(double time) => Set(time, Pose(time));

    /// <summary>
    /// Moves it to world time <paramref name="time"/>, where it is at <paramref name="pose"/>
    /// (<see cref="Pose"/> at that time, worked out already: the physics has it from the step before).
    /// How fast it is going then is only worked out if it is asked for (see <see cref="Rates"/>):
    /// most of the floes the physics keeps up to date have nothing on them, and that is two more
    /// poses each, every step.
    /// </summary>
    public void Set(double time, DeckPose pose)
    {
        Current = pose;
        SetAt = time;
    }

    /// <summary>
    /// How fast it is going and turning at <see cref="SetAt"/>, if not already worked out: a
    /// central difference either side, as the waves are smooth.
    /// </summary>
    private void Rates()
    {
        if (_ratesAt.Equals(SetAt))
            return;
        var time = SetAt;
        _ratesAt = time;
        var (before, after) = (Pose(time - Step), Pose(time + Step));
        _velocity = (after.Position - before.Position) / (float)(2.0 * Step);

        // Each of its axes turns at the angular velocity: axis' = w x axis, so w is half the sum
        // of axis x axis' over the three.
        var turn = Vector3.Zero;
        foreach (var axis in (ReadOnlySpan<Vector3>)[Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ])
        {
            var rate = (Vector3.Transform(axis, after.Rotation) - Vector3.Transform(axis, before.Rotation)) / (float)(2.0 * Step);
            turn += Vector3.Cross(Vector3.Transform(axis, Current.Rotation), rate);
        }

        _angularVelocity = turn * 0.5f;
    }

    /// <summary>
    /// <paramref name="p"/>'s height above its top, along <see cref="Normal"/> (negative inside it),
    /// when it is over its top (inside its rectangle, or its disc), above its bottom and less than
    /// 50 m above the top; otherwise null.
    /// </summary>
    public float? HeightAbove(Vector3 p)
    {
        var offset = p - Current.Position;
        if (offset.LengthSquared() > _farSquared)
            return null; // nowhere near it (most calls: every point looked for is tried against every ship)
        var x = Vector3.Dot(offset, _x);
        var y = Vector3.Dot(offset, _y);
        var z = Vector3.Dot(offset, _z);
        var outside = Shape == RaftShape.Box
            ? MathF.Abs(x) > HalfLength || MathF.Abs(z) > HalfBeam
            : x * x + z * z > HalfLength * HalfLength;
        if (outside) return null;
        var height = y - Freeboard;
        return height < -(Freeboard + Draft) || height >= Reach ? null : height;
    }

    /// <summary>
    /// Whether a point within <paramref name="distance"/> of <paramref name="p"/> could be over its
    /// top (see <see cref="HeightAbove"/>), as it is now: within its reach of its middle, however it lies.
    /// </summary>
    public bool Within(Vector3 p, float distance) =>
        Vector3.Distance(p, Current.Position) <= MathF.Sqrt(HalfLength * HalfLength + HalfBeam * HalfBeam) + Freeboard + Draft + Reach + distance;

    /// <summary>
    /// Whether a point within <paramref name="distance"/> of <paramref name="p"/> could be beside its
    /// side, or in it: across its top's plane, within its footprint's corner (its half-diagonal) and
    /// the distance; and along its normal, no higher than its top and no lower than its bottom.
    /// (Not <see cref="Within"/>: that reaches 50 m over its top as well, for what is over it.)
    /// </summary>
    public bool Beside(Vector3 p, float distance)
    {
        var offset = p - Current.Position;
        var height = Vector3.Dot(offset, Normal);
        if (height >= Freeboard + distance || height <= -(Draft + distance))
            return false;
        var far = _halfDiagonal + distance;
        return offset.LengthSquared() - height * height <= far * far;
    }

    /// <summary>The velocity of its body at <paramref name="p"/>: its own, plus its turning about its centre.</summary>
    public Vector3 VelocityAt(Vector3 p) => Velocity + Vector3.Cross(AngularVelocity, p - Current.Position);
}

/// <summary>
/// Every raft (<see cref="Raft"/>): the drone ships, and the floes. Which one a point is over, and
/// which are kept up to date: <see cref="Physics.RocketPhysics"/> sets each of <see cref="Active"/>
/// at the start of each step (and moves its hull, if it has one), so those are the ones whose pose
/// may be read.
/// </summary>
internal static class Rafts
{
    /// <summary>
    /// The drone ships: every launch site's (<see cref="LaunchSite.DroneShips"/>), site by site in
    /// their order, Home's first, each its length running east. Numbered 0 and down, so the floes keep theirs from 1.
    /// </summary>
    public static IReadOnlyList<Raft> Ships { get; } =
        [.. LaunchSites.All.SelectMany(s => s.DroneShips).Select((centre, i) => new Raft(-i, RaftShape.Box, centre, Planet.East(centre),
            Scene.DroneShipHalfLength, Scene.DroneShipHalfBeam, Scene.DroneShipFreeboard, Scene.DroneShipDraft))];

    /// <summary>Home's first drone ship (see <see cref="Scene.DroneShipCentre"/>), the missions' downrange.</summary>
    public static Raft Ship => Ships[0];

    // The ships are always active, as their hulls are always bodies. A floe joins them only while
    // it is kept up to date (its body near an awake craft, or a craft frozen aboard it), and is
    // otherwise set only when a point is looked for over it, so the work each step is as much as
    // the floes in use, not all of them.
    private static readonly List<Raft> _active = [.. Ships];

    /// <summary>The rafts whose pose is kept up to date this step (a list, so it is walked each step without allocating).</summary>
    public static IReadOnlyList<Raft> Active => _active;

    /// <summary>
    /// The world time the rafts are at: the physics' step (see <see cref="Physics.RocketPhysics"/>),
    /// or the world's time as it is loaded. A floe not kept up to date is set to it when it is wanted (<see cref="Now"/>).
    /// </summary>
    public static double Time { get; set; }

    /// <summary>Makes <paramref name="floes"/> the active floes, after the ships (the physics' choice, each step).</summary>
    public static void SetActiveFloes(List<Raft> floes)
    {
        _active.Clear();
        _active.AddRange(Ships);
        _active.AddRange(floes);
        _from = floes;
    }

    // Whose floes the active ones are (the world in use at the sea's body: see RocketPhysics.TakeRafts).
    private static List<Raft>? _from;

    /// <summary>Whether the active floes are <paramref name="floes"/>' (the last set by <see cref="SetActiveFloes"/>).</summary>
    public static bool HeldBy(List<Raft> floes) => _from == floes;

    /// <summary><paramref name="raft"/>, set as it is at <see cref="Time"/> if it isn't already.</summary>
    public static Raft Now(Raft raft)
    {
        if (raft.SetAt != Time)
            raft.Set(Time);
        return raft;
    }

    /// <summary>
    /// <paramref name="raft"/>'s pose at <see cref="Time"/>, the one <see cref="Now"/> would set,
    /// without setting it (so without its velocities, a third of the work): for drawing every
    /// floe in sight each frame, where only a few are kept up to date.
    /// </summary>
    public static DeckPose PoseNow(Raft raft) => raft.SetAt == Time ? raft.Current : raft.Pose(Time);

    /// <summary>
    /// The raft whose top <paramref name="p"/> is over (see <see cref="Raft.HeightAbove"/>), and how
    /// high above it, or null: a ship (as it was last set), or a floe (as it is at <see cref="Time"/>:
    /// see <see cref="Floes.Under"/>). They never overlap, so it is the only one. The rafts are
    /// the planet's sea's alone, in its frame: asked only where the body has a sea (<see cref="GroundBody.HasSea"/>).
    /// </summary>
    public static (Raft Raft, float Height)? Under(Vector3 p)
    {
        for (var i = 0; i < Ships.Count; i++) // not foreach: over an IReadOnlyList that would allocate, and this is asked many times a step
            if (Ships[i].HeightAbove(p) is { } deck)
                return (Ships[i], deck);
        return Floes.Under(p);
    }

    /// <summary>
    /// Whether a point within <paramref name="distance"/> of <paramref name="p"/> could be over a
    /// raft (see <see cref="Under"/>): near a ship, or near a floe.
    /// </summary>
    public static bool AnyNear(Vector3 p, float distance)
    {
        for (var i = 0; i < Ships.Count; i++)
            if (Ships[i].Within(p, distance))
                return true;
        return Floes.AnyNear(p, distance);
    }

    /// <summary>Whether a point within <paramref name="distance"/> of <paramref name="p"/> could be beside a raft's side, or in it (see <see cref="Raft.Beside"/>, <see cref="Floes.AnyBeside"/>).</summary>
    public static bool AnyBeside(Vector3 p, float distance)
    {
        for (var i = 0; i < Ships.Count; i++)
            if (Ships[i].Beside(p, distance))
                return true;
        return Floes.AnyBeside(p, distance);
    }
}
