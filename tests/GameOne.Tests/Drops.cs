using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// Placed drops, for trying the legs on a slope: a booster with its legs out and its engine off,
/// upright, falling onto a test pad or a piece of real ground. And a search of the real terrain for
/// ground that slopes evenly enough to stand on.
/// </summary>
internal static class Drops
{
    /// <summary>How far the lowest foot (hanging at full stroke) starts above what it will land on.</summary>
    public const float Clearance = 0.3f;

    /// <summary>Dropped onto the middle of test pad <paramref name="testPad"/> (see <see cref="Scene.TestPads"/>), with a leg pointing down it (east).</summary>
    public static Simulation OnPad(int testPad, float speed, Vehicle? vehicle = null)
    {
        var centre = Scene.TestPads[testPad].Centre;
        return At(centre, Planet.East(centre), speed, vehicle);
    }

    /// <summary>
    /// A simulation holding one guided vessel (a booster, unless <paramref name="vehicle"/> says
    /// otherwise), flown: upright over <paramref name="at"/> with its first leg (leg 0) pointing
    /// <paramref name="downhill"/> (so leg 2 points uphill), its legs out and its engine off, its
    /// lowest foot <see cref="Clearance"/> above what is under it, and falling at
    /// <paramref name="speed"/>, plus <paramref name="sideways"/> along the ground. It is the
    /// vehicle's stages <paramref name="first"/> to <paramref name="last"/> (all of them, by
    /// default), flown on <paramref name="mission"/> (Launch and Land, by default), as a vessel of
    /// flight <paramref name="flight"/>: 1 is the flight the simulation starts, so a stage it drops
    /// is one of that flight's, for the mission to count. With <paramref name="leanDegrees"/>, it
    /// isn't upright but leans that far, its nose towards <paramref name="downhill"/>. Its lowest foot
    /// starts <paramref name="clearance"/> above what is under it: the calm sea, over the sea, unless
    /// <paramref name="seabed"/> asks for the seabed under it. Its payload's fairing is on with <paramref name="fairingOn"/>.
    /// Without <paramref name="legsOut"/> its legs are stowed, and the lowest point of its hull
    /// (its engine bell and body, all round) starts <paramref name="clearance"/> above what is under it.
    /// Each stage's tank is <paramref name="fuel"/> full.
    /// </summary>
    public static Simulation At(Vector3 at, Vector3 downhill, float speed, Vehicle? vehicle = null, Vector3 sideways = default,
        int first = 0, int? last = null, Mission? mission = null, int flight = 0, float leanDegrees = 0f, float clearance = Clearance, bool seabed = false, bool fairingOn = false, bool legsOut = true, float fuel = 0.3f)
    {
        mission ??= Mission.LaunchAndLand;
        var state = Placed(at, downhill, speed, vehicle, sideways, first, last, mission, flight, leanDegrees, clearance, seabed, fairingOn, legsOut, fuel);
        var sim = new Simulation(mission, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0))); // flown, so a watched run follows it
        return sim;
    }

    /// <summary>The vessel <see cref="At"/> places (numbered <paramref name="id"/>), to put in a world of one's own: on <paramref name="body"/> (the planet, by default), in its frame.</summary>
    public static VesselState Placed(Vector3 at, Vector3 downhill, float speed, Vehicle? vehicle = null, Vector3 sideways = default,
        int first = 0, int? last = null, Mission? mission = null, int flight = 0, float leanDegrees = 0f, float clearance = Clearance, bool seabed = false, bool fairingOn = false, bool legsOut = true, float fuel = 0.3f, int id = 0,
        GroundBody? body = null)
    {
        vehicle ??= Vehicle.Booster;
        mission ??= Mission.LaunchAndLand;
        body ??= GroundBody.Planet;
        var legs = vehicle.Stages[first].LegGeometry;
        var up = body.Up(at);
        var upright = body.UprightAt(at);
        var level = Vector3.Normalize(downhill - up * Vector3.Dot(downhill, up));
        var leg = Vector3.Transform(RocketGeometry.LegOutward(0), upright);
        var turn = MathF.Atan2(Vector3.Dot(Vector3.Cross(leg, level), up), Vector3.Dot(leg, level));
        var rotation = Quaternion.Concatenate(upright, Quaternion.CreateFromAxisAngle(up, turn));
        if (leanDegrees != 0f)
            rotation = Quaternion.Concatenate(rotation, Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(up, level)), float.DegreesToRadians(leanDegrees)));

        // Lowered onto it a few times over: over a tilted pad the height is measured along its
        // normal, not straight down, so one step down doesn't land it exactly.
        var position = at + up * 30f;
        for (var i = 0; i < 4; i++)
        {
            var model = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            float Above(Vector3 local) => seabed ? body.GroundAltitude(Vector3.Transform(local, model)) : body.SurfaceAltitude(Vector3.Transform(local, model));
            var lowest = legsOut
                ? Enumerable.Range(0, RocketGeometry.LegCount).Min(foot => Above(RocketGeometry.Foot(legs, foot))) - legs.Stroke
                : HullPoints(vehicle.Stages[first], RocketGeometry.StackHeight(vehicle, first, last)).Min(Above);
            position -= up * (lowest - clearance);
        }

        return new VesselState
        {
            Id = id, Flight = flight, Mission = mission, Vehicle = vehicle, First = first, Last = last ?? vehicle.Stages.Length - 1,
            Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * fuel)],
            State = FlightState.Flying, Position = position, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, first, last ?? vehicle.Stages.Length - 1),
            Velocity = -up * speed + sideways, AngularVelocity = Vector3.Zero,
            Aim = Vector3.Transform(Vector3.UnitY, rotation), LegsWanted = legsOut, LegDeploy = legsOut ? 1f : 0f, FairingOn = fairingOn, MaxAltitude = 400f, HasBeenAirborne = true,
            Body = body.Name,
        };
    }

    /// <summary>
    /// A booster (legs stowed, engine off) aimed at the rough slope (<see cref="FindSlope"/>),
    /// coming at it from its downhill side, heading down at <paramref name="degreesBelowLevel"/>
    /// to the level there at <paramref name="speed"/>. It starts a second and a half's travel
    /// back along that line, so it has its full second's lookahead in the air before it hits (the
    /// air slows the fastest a little, so they arrive a little later). Returns the vessel and where it starts.
    /// </summary>
    public static (VesselState State, Vector3 Start) Crashing(float speed, float degreesBelowLevel)
    {
        var (at, downhill, _) = FindSlope(15f, 35f);
        var up = Planet.Up(at);
        var level = Vector3.Normalize(downhill - up * Vector3.Dot(downhill, up));
        var angle = float.DegreesToRadians(degreesBelowLevel);
        var heading = Vector3.Normalize(-level * MathF.Cos(angle) - up * MathF.Sin(angle));
        var start = at - heading * speed * 1.5f;
        var state = Placed(at, downhill, 0f, legsOut: false) with
        {
            Position = start, Rotation = Planet.UprightAt(start), Aim = Planet.Up(start), Velocity = heading * speed,
        };
        return (state, start);
    }

    /// <summary>
    /// A booster (legs stowed, engine off) coasting low over the rough slope (<see cref="FindSlope"/>)
    /// at <paramref name="speed"/> along it, in a low ballistic arc that stays inside the band over
    /// the ground where its tiles are urgent (<see cref="Scene.TerrainTileUrgent"/> and its
    /// <see cref="Scene.TerrainTileUrgentSeconds"/>' travel) until it comes down: it starts 40% of
    /// the band up, a second's travel before the slope, with the upward speed that takes the arc's
    /// top to 90% of it (over the ground it starts over). The slope may rise ahead of it, so over
    /// the ground under it later it may fly lower in the band than that, and come down sooner.
    /// </summary>
    public static VesselState Level(float speed)
    {
        var band = Scene.TerrainTileUrgent + speed * Scene.TerrainTileUrgentSeconds;
        var start = 0.4f * band;
        var upSpeed = MathF.Sqrt(2f * Scene.SurfaceGravity * (0.9f * band - start));
        var (at, downhill, _) = FindSlope(15f, 35f);
        var up = Planet.Up(at);
        var heading = -Vector3.Normalize(downhill - up * Vector3.Dot(downhill, up));
        return Placed(at - heading * speed, heading, -upSpeed, legsOut: false, sideways: heading * speed, clearance: start);
    }

    /// <summary>
    /// How far the lowest point of <paramref name="vessel"/> is above the solid ground (or the pad):
    /// the bottom of its body, the rim of its engine bell all round, and its feet if its legs are out.
    /// </summary>
    public static float Lowest(Vessel vessel) => LowestPoint(vessel).Altitude;

    /// <summary>The lowest point of <paramref name="vessel"/> (as <see cref="Lowest"/>), and how far it is above the solid ground (or the pad).</summary>
    public static (Vector3 At, float Altitude) LowestPoint(Vessel vessel)
    {
        var model = vessel.Model;
        var lowest = (At: model.Translation, Altitude: Planet.GroundAltitude(model.Translation));
        void Try(Vector3 p)
        {
            var altitude = Planet.GroundAltitude(p);
            if (altitude < lowest.Altitude)
                lowest = (p, altitude);
        }
        for (var i = 0; i < 12; i++)
            Try(Vector3.Transform(RocketGeometry.BellRim(vessel.CurrentStage, i, 12), model));
        if (vessel.HasLegs && vessel.LegDeploy >= 1f)
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
                Try(Vector3.Transform(RocketGeometry.Foot(vessel.Legs, leg), model));
        return lowest;
    }

    /// <summary>How far past a frame's travel a craft is counted as near the ground, for <see cref="TileUnder"/> (metres).</summary>
    private const float NearMargin = 2f;

    /// <summary>
    /// Whether the ground's tile under the lowest point of <paramref name="vessel"/> is laid, or it
    /// needs none yet: it is further from the ground than a frame's travel (at its speed) and
    /// <see cref="NearMargin"/> more. Checked every frame of a fall, this is what never falling
    /// through needs; how deep a craft gets into the ground (<see cref="Lowest"/>) can't tell, as a
    /// craft is judged to have hit the ground from the terrain's heights whether a tile is there or
    /// not, and comes down too slowly to go far into it in one frame.
    /// </summary>
    public static bool TileUnder(Simulation sim, Vessel vessel)
    {
        var (at, altitude) = LowestPoint(vessel);
        if (altitude > vessel.Velocity.Length() * Simulation.FrameSeconds + NearMargin)
            return true;
        return sim.Physics.HasTile(GameOne.Physics.TerrainTiles.CellOf(GroundBody.Planet, at - Planet.Centre));
    }

    /// <summary>Points all round a stack's hull (its bottom stage's bell, and its body as wide as that stage), in its own space, half a metre apart down it.</summary>
    private static IEnumerable<Vector3> HullPoints(Stage bottom, float height)
    {
        for (var y = -bottom.BellHeight; y <= height; y += 0.5f)
            for (var k = 0; k < 12; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 12);
                var radius = y < 0f ? bottom.BellRadius : bottom.Radius;
                yield return new Vector3(c * radius, y, s * radius);
            }
    }

    /// <summary>
    /// A place on real ground (land, away from every site) that slopes by between
    /// <paramref name="minDegrees"/> and <paramref name="maxDegrees"/>, evenly: over a 12 m square the
    /// ground is a near-plane, and where a booster's four feet would stand (one downhill) it is
    /// within 0.15 m of that plane, so it is a slope and not a bump. Searched on a grid from the
    /// launch pad outwards (or from <paramref name="from"/>, a place on the grid: x and z, as
    /// <see cref="Planet.SurfacePoint"/> takes them), out to <paramref name="within"/> of it, so it is
    /// the same place every run.
    /// </summary>
    public static (Vector3 At, Vector3 Downhill, float Degrees) FindSlope(float minDegrees, float maxDegrees, float within = 8000f, (float X, float Z) from = default)
    {
        const float spacing = 25f;
        var rings = (int)(within / spacing);
        if (Slope(Planet.SurfacePoint(from.X, from.Z), minDegrees, maxDegrees) is { } there)
            return there;
        for (var ring = 1; ring <= rings; ring++)
            for (var i = -ring; i <= ring; i++)
                for (var j = -ring; j <= ring; j += Math.Abs(i) == ring ? 1 : 2 * ring)
                {
                    var (dx, dz) = (i * spacing, j * spacing);
                    var (x, z) = (from.X + dx, from.Z + dz);
                    // Within reach of where it started, and on the sphere through the pad:
                    // SurfacePoint stands x and z on it (the square root of what the radius
                    // leaves), so a search started far out could otherwise step past its rim.
                    if (dx * dx + dz * dz > within * within || x * x + z * z >= Planet.Radius * Planet.Radius)
                        continue;
                    if (Slope(Planet.SurfacePoint(x, z), minDegrees, maxDegrees) is { } found)
                        return found;
                }
        throw new InvalidOperationException($"no even slope of {minDegrees}° to {maxDegrees}° within {within} m");
    }

    /// <summary>
    /// Where <see cref="FindSlope"/> finds dead-level ground (0° to 0.02°) searching from the pad:
    /// about 7 km out on its grid (ring 268 of 320), so that search looks at most of the grid on
    /// the way, about 3 s. Searched from here, it finds the same place at once; should the map
    /// change so that it is no longer level, the search goes on outwards from here and finds the
    /// nearest that is.
    /// </summary>
    public static readonly (float X, float Z) LevelGround = (-6700f, 2125f);

    /// <summary>The slope at <paramref name="p"/> (on the sphere), if it is one that will do (see <see cref="FindSlope"/>).</summary>
    private static (Vector3 At, Vector3 Downhill, float Degrees)? Slope(Vector3 p, float minDegrees, float maxDegrees)
    {
        const float half = 6f, fit = 0.15f, even = 0.3f;
        var up = Planet.Up(p);
        if (Terrain.IsSea(up) || Terrain.Kind(up) is SurfaceKind.Site or SurfaceKind.Seabed or SurfaceKind.Beach)
            return null;
        foreach (var site in Sites.All)
            if (Vector3.Distance(site.Centre, p) < site.Radius + 100f)
                return null;

        var (east, north) = (Planet.East(p), Planet.North(p));
        float Height(float u, float v) => Terrain.SurfaceHeight(Vector3.Normalize(up * Scene.PlanetRadius + east * u + north * v));

        // A plane h = a + b u + c v through 5 x 5 heights over the square, by least squares (the
        // samples are symmetric about the middle, so each term comes out on its own).
        float a = 0f, b = 0f, c = 0f, uu = 0f;
        Span<float> heights = stackalloc float[25];
        for (var i = 0; i < 5; i++)
            for (var j = 0; j < 5; j++)
            {
                var (u, v) = (-half + half * i / 2f, -half + half * j / 2f);
                var h = heights[i * 5 + j] = Height(u, v);
                (a, b, c, uu) = (a + h, b + h * u, c + h * v, uu + u * u);
            }
        (a, b, c) = (a / 25f, b / uu, c / uu);
        var degrees = float.RadiansToDegrees(MathF.Atan(MathF.Sqrt(b * b + c * c)));
        if (degrees < minDegrees || degrees > maxDegrees)
            return null;
        for (var i = 0; i < 5; i++)
            for (var j = 0; j < 5; j++)
                if (MathF.Abs(heights[i * 5 + j] - (a + b * (-half + half * i / 2f) + c * (-half + half * j / 2f))) > even)
                    return null;

        // Where the feet would stand: one downhill, the others a quarter of the way round from it.
        var downhill = Vector3.Normalize(-(east * b + north * c));
        var foot = RocketGeometry.Foot(LegGeometry.Rocket, 0);
        var reach = MathF.Sqrt(foot.X * foot.X + foot.Z * foot.Z);
        var (du, dv) = (Vector3.Dot(downhill, east), Vector3.Dot(downhill, north));
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            var (s, co) = MathF.SinCos(leg * MathF.PI / 2f);
            var (u, v) = (reach * (du * co - dv * s), reach * (du * s + dv * co));
            if (MathF.Abs(Height(u, v) - (a + b * u + c * v)) > fit)
                return null;
        }
        return (Planet.Centre + up * (Planet.SeaRadius + a), downhill, degrees);
    }
}
