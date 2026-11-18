using System.Numerics;

namespace GameOne;

/// <summary>
/// A body something can be at: its centre in its own frame, its size, its pull, its turn, its
/// heights, and whether it has a sea, air and the sites. Every read of the ground, the sea, the
/// air and the sites says which body it asks (a physics is made for one, <c>RocketPhysics.Body</c>;
/// a vessel is at one, <see cref="Vessel.Body"/>); none assumes one. <see cref="GameOne.Planet"/>
/// is the planet's own values, fixed, for what is the planet's alone (its sites, rafts and sea,
/// the launch, its drawing).
/// </summary>
/// <remarks>
/// Each body's frame is fixed to its ground and turns with it, as the planet's does: its origin on
/// the ground (the planet's at Home's pad), up +Y there, north along <see cref="NorthPole"/>; the
/// physics' world for it stays fixed to that ground, so what moves among the stars is worked out
/// by <see cref="GroundSpin"/> and its kin. Every height is measured from its zero
/// (<see cref="SeaRadius"/> from the centre: the planet's calm sea): <see cref="Altitude"/> the
/// air's and orbits', <see cref="SurfaceAltitude"/> from the ground or the calm sea, and
/// <see cref="GroundAltitude"/> from what is solid: the ground, or under the sea its bed. The
/// bodies are a list (<see cref="Bodies.All"/>): nothing is written for exactly two.
/// </remarks>
internal sealed class GroundBody
{
    /// <summary>The planet's radius: the sphere through Home's pad.</summary>
    public const float PlanetRadius = Scene.PlanetRadius;

    /// <summary>The planet's sea's radius, the zero of its heights (Home's pad is <see cref="Scene.HomeElevation"/> above it).</summary>
    public const float PlanetSeaRadius = PlanetRadius - Scene.HomeElevation;

    /// <summary>The planet's gravitational parameter (G times mass), set so its surface gravity is <see cref="Scene.SurfaceGravity"/>.</summary>
    public const float PlanetMu = Scene.SurfaceGravity * PlanetRadius * PlanetRadius;

    /// <summary>
    /// How long one turn of the planet against the stars takes, in seconds. A day (noon to noon)
    /// is longer than that, as it has gone on round the sun meanwhile: it turns
    /// <see cref="Scene.YearDays"/> + 1 times for every <see cref="Scene.YearDays"/> days.
    /// </summary>
    public const float PlanetTurnSeconds = Scene.DaySeconds * Scene.YearDays / (Scene.YearDays + 1f);

    /// <summary>How fast the planet turns, in radians per second.</summary>
    public const float PlanetSpinRate = MathF.Tau / PlanetTurnSeconds;

    /// <summary>The planet: the world as it is played.</summary>
    public static readonly GroundBody Planet = new(
        "Planet", new Vector3(0f, -PlanetRadius, 0f), PlanetRadius, Scene.HomeElevation, PlanetMu, Scene.SurfaceGravity,
        new Vector3(0f, 0f, -1f), PlanetTurnSeconds, PlanetSpinRate, hasSea: true, hasAir: true, hasSites: true,
        () => Terrain.LoadPlanetMap(), Terrain.PlanetHeight,
        new Family(0, "the ground", null, 0f, null, PlanetsGround: true));

    /// <summary>
    /// The moon, in its own frame: its origin on its datum sphere (<see cref="Scene.MoonRadius"/>)
    /// at its latitude 0, longitude 0, up +Y and north (0, 0, -1) there, so the frame's directions
    /// from its centre are its moon-fixed ones (<see cref="MoonGround"/>); turning about its north
    /// once a lap among the stars (<see cref="SolarSystem.MoonLapSeconds"/>: its axis taken as its
    /// north, as the tilt between them changes nothing in a lander's fall), with its own pull
    /// alone (<see cref="SolarSystem.MoonMu"/>: the planet's would need a frame carried round the
    /// planet with the moon, which this is not). No sea, no air, and nothing built on it. Made on
    /// first use, after the solar system, which it reads.
    /// </summary>
    public static GroundBody Moon => LazyMoon.Value;

    private static readonly Lazy<GroundBody> LazyMoon = new(() => new(
        "Moon", new Vector3(0f, -Scene.MoonRadius, 0f), Scene.MoonRadius, 0f, SolarSystem.MoonMu, Scene.MoonSurfaceGravity,
        new Vector3(0f, 0f, -1f), SolarSystem.MoonLapSeconds, (float)(Math.Tau / SolarSystem.MoonLapSeconds), hasSea: false, hasAir: false, hasSites: false,
        () => MoonGround.Map, MoonGround.Height,
        new Family(1, "the moon", Planet, Scene.MoonDistance,
            t => new BodyPose(SolarSystem.MoonCentreAt(t), SolarSystem.MoonOrientationAt(t), SolarSystem.MoonVelocity(t), SolarSystem.MoonSpin(t)),
            SolarSystem.MoonCentreAt)));

    /// <summary>
    /// A body going round <paramref name="parent"/> at <paramref name="distance"/>, moved by
    /// <paramref name="pose"/> (its place in its parent's frame at a time), with a datum sphere of
    /// <paramref name="radius"/> and no sea, air or sites: a body other than the game's
    /// (<see cref="Bodies.All"/>), so a list of others can be chosen among and their physics made.
    /// Its ground is <paramref name="map"/> and <paramref name="height"/> if given (another body's,
    /// say), else none: a body with no ground of its own has no map to read.
    /// </summary>
    internal static GroundBody Circling(string name, string groundName, int index, GroundBody parent, float distance,
        float radius, float mu, Func<double, BodyPose> pose, Func<TerrainMap>? map = null, Func<Vector3, float>? height = null) =>
        new(name, new Vector3(0f, -radius, 0f), radius, 0f, mu, mu / (radius * radius), new Vector3(0f, 0f, -1f),
            Math.Tau * Math.Sqrt(Math.Pow(distance, 3) / parent.Mu), (float)(1 / Math.Sqrt(Math.Pow(distance, 3) / parent.Mu)),
            hasSea: false, hasAir: false, hasSites: false,
            map, height ?? (_ => 0f),
            new Family(index, groundName, parent, distance, pose));

    /// <summary>
    /// Where a body is among the others: its place in the list, its ground's name, what it goes
    /// round, how far out, and where it is at a time (its whole pose, and its centre's place alone
    /// where that is cheaper: its pose's otherwise); and whether its ground is the planet's
    /// (<see cref="Terrain.PlanetHeight"/>, read straight).
    /// </summary>
    private readonly record struct Family(int Index, string GroundName, GroundBody? Parent, float Distance, Func<double, BodyPose>? Pose,
        Func<double, Vector3Double>? Place = null, bool PlanetsGround = false);

    /// <param name="name">What it is called.</param>
    /// <param name="centre">Its centre, in its own frame.</param>
    /// <param name="radius">The sphere through its frame's origin.</param>
    /// <param name="elevation">How high its frame's origin is above the zero of its heights.</param>
    /// <param name="mu">Its gravitational parameter (G times mass).</param>
    /// <param name="surfaceGravity">Its pull at the ground, as the touchdowns' leeway takes it (not worked out from <paramref name="mu"/>, so the planet's is exactly <see cref="Scene.SurfaceGravity"/>).</param>
    /// <param name="northPole">Its spin axis, from its centre towards its north pole.</param>
    /// <param name="turnSeconds">How long a turn against the stars takes (in double, for the turn's angle over a long time).</param>
    /// <param name="spinRate">How fast it turns about <paramref name="northPole"/>, in radians a second (given, so the planet's is its own constant).</param>
    /// <param name="hasSea">Whether it has a sea at its heights' zero (else the ground is all there is, below 0 too).</param>
    /// <param name="hasAir">Whether it has air (<see cref="Scene.AtmosphereHeight"/> of it).</param>
    /// <param name="hasSites">Whether the launch sites, landing zones, their foundations, towers and masts are on it.</param>
    /// <param name="map">Loads its baked map (once, the first time it is needed).</param>
    /// <param name="height">Its ground's height (above its heights' zero) in a unit direction from its centre, in its frame.</param>
    /// <param name="family">Where it is among the bodies.</param>
    private GroundBody(string name, Vector3 centre, float radius, float elevation, float mu, float surfaceGravity, Vector3 northPole,
        double turnSeconds, float spinRate, bool hasSea, bool hasAir, bool hasSites, Func<TerrainMap>? map, Func<Vector3, float> height,
        Family family)
    {
        (Index, GroundName, Parent, _pose, _place) = (family.Index, family.GroundName, family.Parent, family.Pose, family.Place);
        TooFastToTouchDown = $"Too fast to touch down on {family.GroundName}! Throttle up to slow down";
        SphereRadius = family.Parent is { } parent
            ? (float)(family.Distance * Math.Pow(mu / (double)parent.Mu, 0.4))
            : float.PositiveInfinity;
        (Name, Centre, Radius, SeaRadius, Mu, SurfaceGravity) = (name, centre, radius, radius - elevation, mu, surfaceGravity);
        (NorthPole, TurnSeconds, SpinRate, Spin) = (northPole, turnSeconds, spinRate, northPole * spinRate);
        (HasSea, HasAir, HasSites, _height) = (hasSea, hasAir, hasSites, height);
        _planetsGround = family.PlanetsGround;
        _map = new(map ?? (() => throw new InvalidOperationException($"{name} has no map")));
        _maxHeight = new(() => Map.MaxHeight);
        // The sea, or the lowest ground less the fine detail; with no map (a body made up by Circling
        // with none), its ground is level at its heights' zero.
        _lowestHeight = new(() => hasSea || map is null ? 0f : Map.MinHeight - Scene.FineDetailMargin);
        _highestWave = new(() => Map.Seas.Select(s => s.Waves / 2f).DefaultIfEmpty(0f).Max());
        _tileCellsPerFace = new(() => Physics.TerrainTiles.CellsFor(this));
    }

    public string Name { get; }

    /// <summary>Its place in <see cref="Bodies.All"/> (what a target names it by).</summary>
    public int Index { get; }

    /// <summary>What its ground is called where a reason or the HUD names it ("the ground" on the planet, "the moon").</summary>
    public string GroundName { get; }

    /// <summary>The HUD's line for coming down on its ground faster than a landing survives (<see cref="Hud.TooFastLine"/>), by its <see cref="GroundName"/>: made once, as it is shown a frame at a time.</summary>
    public string TooFastToTouchDown { get; }

    /// <summary>What it goes round; none for the planet, the root of the bodies' frames.</summary>
    public GroundBody? Parent { get; }

    /// <summary>
    /// How far from its centre its own pull alone is reckoned (its sphere of influence): its
    /// distance from its parent times (its mass over its parent's) to the 2/5, worked out from the
    /// masses. Infinite with no parent. A vessel enters it at this radius and leaves only past
    /// <see cref="Scene.SphereLeaveFactor"/> times it (<see cref="Bodies.Choose(GroundBody, Vector3, double)"/>).
    /// </summary>
    public float SphereRadius { get; }

    private readonly Func<double, BodyPose>? _pose;

    /// <summary>
    /// Where its frame is in its parent's ground frame at world time <paramref name="t"/>: its
    /// centre, its orientation (its own axes into its parent's), its centre's velocity and its
    /// angular velocity over its parent's ground, in double (velocity and spin worked out, not
    /// differenced). A body with no parent has none.
    /// </summary>
    public BodyPose PoseIn(double t) =>
        _pose is { } pose ? pose(t) : throw new InvalidOperationException($"{Name} goes round nothing");

    private readonly Func<double, Vector3Double>? _place;

    /// <summary>
    /// Its centre in its parent's ground frame at world time <paramref name="t"/>: <see cref="PoseIn"/>'s
    /// centre alone, worked out without its orientation, velocity and spin where the body can (the moon's: two of
    /// its pose's seven sines and cosines), for what asks only how far a place is from it
    /// (<see cref="Bodies.PlacesIn"/>, every body every step, for the crossing's asking of every vessel).
    /// </summary>
    public Vector3Double PlaceIn(double t) => _place is { } place ? place(t) : PoseIn(t).Centre;

    public Vector3 Centre { get; }
    public float Radius { get; }

    /// <summary>How far the zero of its heights is from its centre (the planet's calm sea).</summary>
    public float SeaRadius { get; }

    public float Mu { get; }
    public float SurfaceGravity { get; }
    public Vector3 NorthPole { get; }
    public double TurnSeconds { get; }
    public float SpinRate { get; }

    /// <summary>Its angular velocity among the stars.</summary>
    public Vector3 Spin { get; }

    /// <summary>Whether it has a sea at its heights' zero (else the ground is all there is, below 0 too). The planet's sea is <see cref="Sea"/>, its rafts <see cref="Rafts"/>: read only where this is so.</summary>
    public bool HasSea { get; }

    /// <summary>Whether it has air (<see cref="Scene.AtmosphereHeight"/> of it); with none it is <see cref="AboveAir"/> everywhere.</summary>
    public bool HasAir { get; }

    /// <summary>Whether the launch sites, landing zones, their foundations, towers and masts stand on it (the planet's alone): read only where this is so.</summary>
    public bool HasSites { get; }

    private readonly Func<Vector3, float> _height;
    private readonly bool _planetsGround; // read straight, not through the delegate: the tiles' every sample reads it

    /// <summary>Its ground's height above its heights' zero (the land, or under the sea its bed) in a unit <paramref name="direction"/> from its centre, in its frame.</summary>
    public float Height(Vector3 direction) => _planetsGround ? Terrain.PlanetHeight(direction) : _height(direction);

    /// <summary>The highest its map's ground gets above its heights' zero (the fine detail's few metres not counted).</summary>
    public float MaxHeight => _maxHeight.Value;
    private readonly Lazy<float> _maxHeight;

    /// <summary>
    /// The distance from its centre no surface of it is ever below: its sea's, or with no sea its
    /// map's lowest ground less the fine detail's margin (<see cref="Scene.FineDetailMargin"/>), as
    /// its ground may lie below its heights' zero (the moon's plains and craters). A path down to
    /// here has met its surface; a turn bounded here bounds every one over it. Its map is read only
    /// with no sea, and once; one made up with no map is level at its heights' zero.
    /// </summary>
    public float LowestRadius => SeaRadius + _lowestHeight.Value;
    private readonly Lazy<float> _lowestHeight;

    /// <summary>
    /// The lowest an orbit round it stays up above: the top of its air, or with none the highest its
    /// map's ground gets (<see cref="MaxHeight"/>).
    /// </summary>
    public float OrbitFloor => HasAir ? Scene.AtmosphereHeight : MaxHeight;

    /// <summary>Its baked map.</summary>
    public TerrainMap Map => _map.Value;
    private readonly Lazy<TerrainMap> _map;

    /// <summary>The highest its seas' waves reach above its heights' zero (see <see cref="Sea.Highest"/>); 0 with no sea.</summary>
    public float HighestWave => _highestWave.Value;
    private readonly Lazy<float> _highestWave;

    /// <summary>Its ground's tiles' cells across a cube face, each way (see <see cref="Physics.TerrainTiles.CellsPerFace"/>).</summary>
    public int TileCellsPerFace => _tileCellsPerFace.Value;
    private readonly Lazy<int> _tileCellsPerFace;

    /// <summary>Straight up at <paramref name="p"/> on this body: away from its centre.</summary>
    public Vector3 Up(Vector3 p) => Vector3.Normalize(p - Centre);

    /// <summary>East at <paramref name="p"/> on this body.</summary>
    public Vector3 East(Vector3 p) => Vector3.Normalize(Vector3.Cross(NorthPole, Up(p)));

    /// <summary>North at <paramref name="p"/> on this body.</summary>
    public Vector3 North(Vector3 p) => Vector3.Cross(Up(p), East(p));

    /// <summary>
    /// How far this body turns in <paramref name="seconds"/>, in radians about its north, wrapped
    /// to −π..π: worked out in double and wrapped before it is a float, so a long time keeps its precision.
    /// </summary>
    public float TurnAngle(double seconds) => (float)TurnRadians(seconds);

    /// <summary><see cref="TurnAngle"/> left in double, for what is worked out across the bodies' frames.</summary>
    public double TurnRadians(double seconds)
    {
        var angle = Math.Tau / TurnSeconds * seconds;
        return angle - Math.Round(angle / Math.Tau) * Math.Tau;
    }

    /// <summary>A direction fixed to this body's ground now, after <paramref name="seconds"/> of its turn, among the stars (negative seconds turn back).</summary>
    public Vector3 TurnedDirection(Vector3 d, double seconds) =>
        Vector3.Transform(d, Quaternion.CreateFromAxisAngle(NorthPole, TurnAngle(seconds)));

    /// <summary>
    /// How thick the planet's air is <paramref name="aboveSea"/> above its sea: 1 at the sea,
    /// thinning exponentially, and tapering to exactly 0 at the top of the atmosphere.
    /// </summary>
    public static float PlanetAirDensity(float aboveSea)
    {
        if (aboveSea >= Scene.AtmosphereHeight)
            return 0f;
        var a = MathF.Max(aboveSea, 0f);
        return MathF.Exp(-a / Scene.AtmosphereScaleHeight) * (1f - a / Scene.AtmosphereHeight);
    }

    /// <summary>
    /// The compass bearing of <paramref name="direction"/> seen from <paramref name="p"/>: radians
    /// clockwise from north (east is π/2), 0 to 2π. Only its part across the ground counts.
    /// </summary>
    public float Bearing(Vector3 p, Vector3 direction)
    {
        var bearing = MathF.Atan2(Vector3.Dot(direction, East(p)), Vector3.Dot(direction, North(p)));
        return bearing < 0f ? bearing + MathF.Tau : bearing;
    }

    /// <summary>Across the ground at <paramref name="p"/>, towards compass bearing <paramref name="bearing"/> (radians clockwise from north).</summary>
    public Vector3 Towards(Vector3 p, float bearing) => North(p) * MathF.Cos(bearing) + East(p) * MathF.Sin(bearing);

    /// <summary>The turn that stands something upright at <paramref name="p"/>: from +Y to the local up.</summary>
    public Quaternion UprightAt(Vector3 p)
    {
        var up = Up(p);
        var axis = Vector3.Cross(Vector3.UnitY, up);
        var sin = axis.Length();
        // The angle from both its sine and cosine: past a quarter of the way round, the sine alone would give the wrong one.
        if (sin < 1e-6f)
            return up.Y > 0f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        return Quaternion.CreateFromAxisAngle(axis / sin, MathF.Atan2(sin, up.Y));
    }

    /// <summary>
    /// How far apart <paramref name="a"/> and <paramref name="b"/> are round it, as the angle
    /// between them at its centre, in radians: precise even a metre apart (unlike an arccosine).
    /// </summary>
    public float AngleBetween(Vector3 a, Vector3 b)
    {
        var (ua, ub) = (Up(a), Up(b));
        return MathF.Atan2(Vector3.Cross(ua, ub).Length(), Vector3.Dot(ua, ub));
    }

    /// <summary>
    /// Height above its heights' zero (the sea's, on the planet; not the ground: see
    /// <see cref="SurfaceAltitude"/>), on the same scale as its ground's heights: what the air,
    /// orbits and every height shown are measured by. Below the zero it is negative.
    /// </summary>
    public float Altitude(Vector3 p) => (p - Centre).Length() - SeaRadius;

    /// <summary>
    /// Whether <paramref name="p"/> is above the air (<see cref="Scene.AtmosphereHeight"/>; with no
    /// air, everywhere): where nothing holds a craft to the ground's turn, so it keeps its attitude
    /// among the stars. The physics and the steering both judge it by this, at the craft's centre
    /// of mass, so they agree.
    /// </summary>
    public bool AboveAir(Vector3 p) => !HasAir || Altitude(p) > Scene.AtmosphereHeight;

    /// <summary>
    /// How thick the air is <paramref name="aboveSea"/> above its heights' zero: the planet's
    /// (<see cref="PlanetAirDensity"/>) where it has air, 0 everywhere on a body with none.
    /// </summary>
    public float AirDensity(float aboveSea) => HasAir ? PlanetAirDensity(aboveSea) : 0f;

    /// <summary>Its pull at <paramref name="p"/>, as an acceleration.</summary>
    public Vector3 Gravity(Vector3 p)
    {
        var toCentre = Centre - p;
        var r2 = toCentre.LengthSquared();
        return toCentre / MathF.Sqrt(r2) * (Mu / r2);
    }

    /// <summary>The ground's velocity at <paramref name="p"/> among the stars: how fast its turn carries it.</summary>
    public Vector3 GroundSpin(Vector3 p) => Vector3.Cross(Spin, p - Centre);

    /// <summary>A velocity measured against the ground at <paramref name="p"/>, as it is among the stars.</summary>
    public Vector3 AmongStars(Vector3 p, Vector3 groundVelocity) => groundVelocity + GroundSpin(p);

    /// <summary>A velocity among the stars at <paramref name="p"/>, as it is measured against the ground (the inverse of <see cref="AmongStars"/>).</summary>
    public Vector3 OverGround(Vector3 p, Vector3 starsVelocity) => starsVelocity - GroundSpin(p);

    /// <summary>
    /// The turning frame's pull at <paramref name="p"/> on something moving at
    /// <paramref name="groundVelocity"/> over the ground, as an acceleration: the sideways one
    /// (Coriolis, −2 Ω × v) and the outward one (centrifugal, −Ω × (Ω × r)). The physics world
    /// stays fixed to the ground, so these are what keep its paths the ones the stars would see:
    /// something dropped lands east of under it, and an orbit stays put among the stars while the
    /// ground turns beneath it.
    /// </summary>
    public Vector3 TurningPull(Vector3 p, Vector3 groundVelocity) =>
        -2f * Vector3.Cross(Spin, groundVelocity) + OutwardPull(p);

    /// <summary>
    /// The outward part of <see cref="TurningPull"/> alone (centrifugal, −Ω × (Ω × r)): the part
    /// that depends only on where something is, as gravity does.
    /// </summary>
    public Vector3 OutwardPull(Vector3 p) => -Vector3.Cross(Spin, GroundSpin(p));

    /// <summary>
    /// How much a step of <paramref name="seconds"/> changes <paramref name="velocity"/> (over the
    /// ground) under <paramref name="pull"/> (what pulls it where it is: gravity and the outward
    /// pull), the sideways pull of the turn (Coriolis) and drag at <paramref name="drag"/> per
    /// second: the physics' fall (<c>RocketPhysics.Fall</c>), and anything that predicts one as it
    /// does. The position is then moved by the velocity this leaves, so a step is a kick and a drift.
    /// </summary>
    /// <remarks>
    /// With a pull that depends on where it is alone, a kick and a drift keep an orbit's energy for
    /// ever, but the sideways pull depends on the velocity: kicked by it as the velocity was at the
    /// start of the step, an orbit lost about 3 m of its size a lap (exactly as a plain integration
    /// in doubles does, so not float error), and turning the velocity by the whole step's angle
    /// first does worse. So the step is the one charged particles are moved by in a magnetic field
    /// (Boris's), which the sideways pull is like: half the kick from where it is, then the
    /// sideways pull's turn of the velocity, worked out on the mean of the velocity before and
    /// after it (a turn that keeps its length), then the other half of the kick. That holds an
    /// orbit's size as well as a body that doesn't turn does. <paramref name="noHalfKick"/>
    /// turns the velocity as it is, without the first half kick (something on the ground: see
    /// <c>RocketPhysics.Fall</c>).
    /// </remarks>
    public Vector3 FallStep(Vector3 velocity, Vector3 pull, float drag, float seconds, bool noHalfKick = false)
    {
        var half = noHalfKick ? velocity : velocity + pull * (seconds / 2);
        var t = Spin * seconds; // the sideways pull, −2 Ω × v, turns v by 2|Ω|dt; t is half that
        var turn = Vector3.Cross(half + Vector3.Cross(half, t), t) * (2f / (1f + t.LengthSquared()));
        return pull * seconds + turn - velocity * (1f - MathF.Exp(-drag * seconds));
    }

    /// <summary>
    /// <see cref="FallStep(Vector3, Vector3, float, float, bool)"/> in double, step for step, for the
    /// physics' own copy of a craft flying free (<c>RocketPhysics.TrueCentreOfMass</c>): in floats
    /// each step's rounding leans the same way lap after lap in the turning frame, so an orbit would creep.
    /// </summary>
    public Vector3Double FallStep(Vector3Double velocity, Vector3Double pull, double drag, double seconds)
    {
        var half = velocity + pull * (seconds / 2);
        var t = (Vector3Double)Spin * seconds;
        var turn = Vector3Double.Cross(half + Vector3Double.Cross(half, t), t) * (2 / (1 + t.LengthSquared()));
        return pull * seconds + turn - velocity * (1 - Math.Exp(-drag * seconds));
    }

    /// <summary><see cref="Gravity(Vector3)"/> and <see cref="OutwardPull(Vector3)"/> together at <paramref name="p"/>, in double: what pulls something there flying free.</summary>
    public Vector3Double Pull(Vector3Double p)
    {
        var toCentre = (Vector3Double)Centre - p;
        var r2 = toCentre.LengthSquared();
        Vector3Double spin = Spin;
        return toCentre / Math.Sqrt(r2) * (Mu / r2) + Vector3Double.Cross(spin, Vector3Double.Cross(spin, toCentre));
    }

    /// <summary>
    /// Where a point fixed to the ground now will be after <paramref name="seconds"/>, among the
    /// stars, in the frame that matches the ground now: turned about the axis through the centre.
    /// Negative seconds turn back. The angle is worked out in double (<see cref="TurnAngle"/>) and
    /// wrapped to a turn first, so a long time doesn't cost the float its precision.
    /// </summary>
    public Vector3 Turned(Vector3 p, double seconds) => Centre + TurnedDirection(p - Centre, seconds);

    /// <summary>
    /// The height of its surface in <paramref name="direction"/>: the ground, or the calm sea over
    /// it (a body with no sea has only its ground). What a path ends on and altitude is measured
    /// from; the sea is water, so what stands on the ground or collides with it reads
    /// <see cref="Height"/>, the seabed under the sea.
    /// </summary>
    public float SurfaceHeight(Vector3 direction) => HasSea ? MathF.Max(Height(direction), 0f) : Height(direction);

    /// <summary>Whether <paramref name="direction"/> is out at sea: the ground there is under its sea (never, on a body with no sea).</summary>
    public bool IsSea(Vector3 direction) => HasSea && Height(direction) < 0f;

    /// <summary>
    /// Straight out of the ground (<see cref="Height"/>, the seabed under the sea: a foot stands on
    /// nothing else) in <paramref name="direction"/>, in its frame: from heights
    /// <see cref="Scene.TerrainNormalStep"/> apart each way across it.
    /// </summary>
    public Vector3 Normal(Vector3 direction)
    {
        // Any two directions across the ground will do: the normal doesn't depend on which.
        var a = Vector3.Normalize(Vector3.Cross(MathF.Abs(direction.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, direction));
        var b = Vector3.Cross(direction, a);

        // Each step is half the spacing to either side, as an angle at the centre; at the ground's
        // height that angle spans a little more than the step, which matters on the peaks.
        var half = Scene.TerrainNormalStep * 0.5f;
        var angle = half / Radius;
        var span = 2f * half * (SeaRadius + Height(direction)) / Radius;
        var slopeA = (Height(Vector3.Normalize(direction + a * angle)) - Height(Vector3.Normalize(direction - a * angle))) / span;
        var slopeB = (Height(Vector3.Normalize(direction + b * angle)) - Height(Vector3.Normalize(direction - b * angle))) / span;
        return Vector3.Normalize(direction - a * slopeA - b * slopeB);
    }

    /// <summary>
    /// The height of <paramref name="p"/> above whatever is under it: the top of a launch pad, a
    /// landing zone or the drone ship's deck if it is over one (where it has them), or else the
    /// ground, or over the sea its calm surface (at 0: the waves are the physics', which knows the
    /// time). Over a tilted zone (a test pad) or the deck it is measured along the top's normal,
    /// the distance to touch it, not straight down: see <see cref="Surface"/> for the way it is measured.
    /// </summary>
    public float SurfaceAltitude(Vector3 p) => Surface(p).Height;

    /// <summary>
    /// The height of <paramref name="p"/> above whatever solid thing is under it: a pad, the
    /// drone ship's deck, the ground, or under the sea its bed. What the legs stand on (see <see cref="Ground"/>).
    /// </summary>
    public float GroundAltitude(Vector3 p) => Under(p, water: false).Height;

    /// <summary>The height of <paramref name="p"/> above the ground (or the seabed) under it, as <see cref="GroundAltitude"/> reads it where there is nothing else (see <see cref="OnlyGroundNear"/>).</summary>
    public float TerrainAltitude(Vector3 p) => Altitude(p) - Height(Up(p));

    /// <summary>
    /// <see cref="SurfaceAltitude"/>, with the way it is measured: the normal of the pad's top under
    /// <paramref name="p"/>, or the local up over the ground or the sea (taken as level). Something
    /// heading for the surface along a direction <c>d</c> meets it after <c>Height / -dot(d, Normal)</c>.
    /// </summary>
    public (float Height, Vector3 Normal) Surface(Vector3 p)
    {
        var (height, normal, _) = Under(p, water: true);
        return (height, normal);
    }

    /// <summary>
    /// <see cref="GroundAltitude"/>, with the way straight out of what is there: a pad's top, or
    /// over the ground (or the seabed) its own slope (<see cref="Normal"/>). What a foot standing
    /// there is pushed back along.
    /// </summary>
    public (float Height, Vector3 Normal) Ground(Vector3 p)
    {
        var (height, normal, onGround) = Under(p, water: false);
        return (height, onGround ? Normal(normal) : normal);
    }

    /// <summary>
    /// How fast what is under <paramref name="p"/> is moving: a raft's top there (the drone ship's
    /// deck), if <paramref name="p"/> is over one (see <see cref="Rafts.Under"/>; only on a sea);
    /// anything else (the pads, the ground) stands still. Touching down, standing and sliding are
    /// all measured against it.
    /// </summary>
    public Vector3 GroundVelocity(Vector3 p) => HasSea && Rafts.Under(p) is { } under ? under.Raft.VelocityAt(p) : Vector3.Zero;

    /// <summary>
    /// Whether under every point within <paramref name="reach"/> of <paramref name="p"/> there is
    /// nothing but the ground (or the seabed): no launch pad, landing zone or raft. There
    /// <see cref="GroundAltitude"/> is <see cref="TerrainAltitude"/>, a good deal quicker to read
    /// (a quarter of a microsecond against two): for something reading it at many points close
    /// together, as a hull's are, asked once for them all.
    /// </summary>
    public bool OnlyGroundNear(Vector3 p, float reach)
    {
        if (HasSites)
        {
            var sites = LaunchSites.All;
            for (var s = 0; s < sites.Length; s++)
            {
                var q = p - sites[s].Pad;
                var along = Vector3.Dot(q, sites[s].Up);
                var far = PadReach + reach;
                if (q.LengthSquared() - along * along <= far * far)
                    return false; // over or round one of the pads, as Under looks for them
            }
            var plinths = Foundations.LandingZonePlinth;
            for (var i = 0; i < plinths.Count; i++) // (by index: a foreach over the list would allocate)
                if (plinths[i].Within(p, reach))
                    return false;
        }
        return !HasSea || !Rafts.AnyNear(p, reach);
    }

    /// <summary>
    /// Whether a point within <paramref name="reach"/> of <paramref name="p"/> could be beside the
    /// side of a launch pad, a landing zone's plinth or a raft, or in it: within its footprint and
    /// <paramref name="reach"/> of it across the ground, and no higher than its top (not up in the
    /// room over a top that their Within allows, for what is over one). A side is a wall, which
    /// <see cref="GroundAltitude"/> reads only for points inside it (as under its top): beside one,
    /// the height under a point says nothing of the points round it. Over a top, or anywhere else,
    /// the ground under nearby points is near enough a plane.
    /// </summary>
    public bool BesideASide(Vector3 p, float reach)
    {
        if (HasSites)
        {
            var sites = LaunchSites.All;
            for (var s = 0; s < sites.Length; s++)
            {
                var q = p - sites[s].Pad;
                var along = Vector3.Dot(q, sites[s].Up);
                var far = PadHalfDiagonal + reach; // its footprint's corner, across the ground
                if (q.LengthSquared() - along * along <= far * far && sites[s].ToLocal(p).Y < Scene.PadTop + reach)
                    return true;
            }
            var plinths = Foundations.LandingZonePlinth;
            for (var i = 0; i < plinths.Count; i++)
                if (plinths[i].Beside(p, reach))
                    return true;
        }
        return HasSea && Rafts.AnyBeside(p, reach);
    }

    /// <summary>How far across the ground from a launch pad's centre a point can be and still be over it (metres, past its corners): further, the pad isn't looked at.</summary>
    private const float PadReach = Scene.PadHalfSize * 1.5f;

    /// <summary>How far across the ground from a launch pad's centre its corners are: no point beside its side is further, but by how far it is beside it.</summary>
    private const float PadHalfDiagonal = Scene.PadHalfSize * 1.41422f;

    /// <summary>
    /// The height above what is under <paramref name="p"/>, measured to the calm sea over the sea
    /// with <paramref name="water"/>, or else to the seabed; and whether it is the ground (not a pad) there.
    /// </summary>
    private (float Height, Vector3 Normal, bool OnGround) Under(Vector3 p, bool water)
    {
        // The pads and zones are the planet's alone (HasSites).
        if (HasSites)
        {
            // Each launch pad is a box on the planet, square to its site's own frame (see LaunchSite.Frame).
            var sites = LaunchSites.All;
            for (var s = 0; s < sites.Length; s++)
            {
                var q = p - sites[s].Pad;
                var along = Vector3.Dot(q, sites[s].Up);
                if (q.LengthSquared() - along * along > PadReach * PadReach)
                    continue; // nowhere near this one
                // Over it, and above its foundation's underside there (on a footing, a point below the
                // slab further out than its taper is under the slab, in the air or the hill: the ground's).
                var onPad = sites[s].ToLocal(p);
                var under = Foundations.PadFootings[s] is { } footing ? footing.UnderAt(onPad.X, onPad.Z)
                    : MathF.Max(MathF.Abs(onPad.X), MathF.Abs(onPad.Z)) <= Scene.PadHalfSize ? Foundations.LaunchPads[s] : (float?)null;
                if (under is { } depth && onPad.Y > Scene.PadTop - depth)
                    return (onPad.Y - Scene.PadTop, sites[s].Up, false);
            }

            // Each landing zone is a disc standing on the surface, level there (or tilted, on the test
            // range), on a plinth with vertical sides (or its footing): over one, the height is measured up from its top.
            var plinths = Foundations.LandingZonePlinth;
            for (var i = 0; i < plinths.Count; i++) // (by index: a foreach over the list would allocate, at every point read)
                if (plinths[i].HeightAbove(p) is { } above)
                    return (above, plinths[i].Normal, false);
        }

        // A raft (the drone ship's deck, a floe: the planet's sea's alone), as it is this moment
        // (Raft.Set): over one, the height is measured up from its top, along its normal, as over a
        // tilted zone. It stands out of the sea, so it is what is under p for the water as well as
        // for the solid.
        if (HasSea && Rafts.Under(p) is { } raft)
            return (raft.Height, raft.Raft.Normal, false);

        var up = Up(p);
        return (Altitude(p) - (water ? SurfaceHeight(up) : Height(up)), up, true);
    }

    /// <summary>
    /// The orbit something at <paramref name="position"/> moving at <paramref name="velocity"/> is on
    /// round it, if nothing but its pull acts: its highest and lowest heights above its heights'
    /// zero (not the ground). If it is moving fast enough to escape altogether, the apoapsis is
    /// infinite. The velocity is the stars' (<see cref="AmongStars"/>), where an orbit is an
    /// ellipse: over the turning ground it isn't one.
    /// </summary>
    public (float Apoapsis, float Periapsis) Orbit(Vector3 position, Vector3 velocity)
    {
        // In doubles from the start: for a nearly circular orbit the eccentricity is the root of a
        // small difference of two numbers near 1, so the float rounding of the speed and the angular
        // momentum (a part in ten million) would read as an eccentricity of a few parts in ten
        // thousand: the apoapsis and periapsis jittering several metres either side of a steady orbit.
        var r = position - Centre;
        var (rx, ry, rz) = ((double)r.X, (double)r.Y, (double)r.Z);
        var (vx, vy, vz) = ((double)velocity.X, (double)velocity.Y, (double)velocity.Z);
        var distance = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        var speed2 = vx * vx + vy * vy + vz * vz;
        var energy = speed2 / 2 - Mu / distance;              // per unit mass
        var (hx, hy, hz) = (ry * vz - rz * vy, rz * vx - rx * vz, rx * vy - ry * vx);
        var h2 = hx * hx + hy * hy + hz * hz;                 // angular momentum, squared
        var eccentricity = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h2 / ((double)Mu * Mu)));
        if (energy >= 0)
            return (float.PositiveInfinity, (float)(h2 / Mu / (1 + eccentricity) - SeaRadius));
        var semiMajor = -Mu / (2 * energy);
        return ((float)(semiMajor * (1 + eccentricity) - SeaRadius), (float)(semiMajor * (1 - eccentricity) - SeaRadius));
    }

    /// <summary>The speed of a circular orbit <paramref name="aboveSea"/> above its heights' zero.</summary>
    public float CircularSpeed(float aboveSea) => MathF.Sqrt(Mu / (SeaRadius + aboveSea));
}
