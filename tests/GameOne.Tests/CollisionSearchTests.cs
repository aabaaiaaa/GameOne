using System.Diagnostics;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The map's predicted collisions (<see cref="CollisionSearch"/>): between any two vessels in the
/// world, searched along their orbits on a thread of its own, and drawn by <see cref="Paths"/> as a
/// red cross where they collide with the stretch of each path into it, fainter for a vessel that is
/// neither flown nor the target.
/// </summary>
public sealed class CollisionSearchTests(ITestOutputHelper output)
{
    /// <summary>How long any wait on the search may take before the test fails: far longer than any pass here needs.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private const float Size = 3f;

    /// <summary>The radius the made-up orbits hit at: 9.5 km up, clear of the satellites preset's 9, 9.6 and 10 km.</summary>
    private static readonly float Radius = Planet.SeaRadius + 9500f;

    /// <summary>Where the two made-up orbits hit: a place off every axis, so nothing lines up by chance.</summary>
    private static readonly Vector3 Place = Planet.Centre + Vector3.Normalize(new Vector3(0.6f, 0.7f, -0.4f)) * Radius;

    /// <summary>
    /// Two orbits through <see cref="Place"/>, in planes 60° or so apart, each there
    /// <paramref name="hitsIn"/> seconds from now (the second <paramref name="lateBy"/> later): one
    /// a little out of round (there at its lowest), the other round. Each as it is now, among the
    /// stars, run back from there by Kepler.
    /// </summary>
    private static (SearchVessel A, SearchVessel B, float Closing) Crossing(double hitsIn, double lateBy = 0)
    {
        var up = Vector3.Normalize(Place - Planet.Centre);
        var speed = MathF.Sqrt(Planet.Mu / Radius);
        var (movingA, movingB) = (Vector3.Normalize(Vector3.Cross(Vector3.Cross(up, Vector3.UnitX), up)) * speed * 1.02f,
            Vector3.Normalize(Vector3.Cross(Vector3.Cross(up, Vector3.UnitY), up)) * speed);
        var a = new Orbit(GroundBody.Planet, Place, movingA, findEnd: false).StateAfter(-hitsIn);
        var b = new Orbit(GroundBody.Planet, Place, movingB, findEnd: false).StateAfter(-(hitsIn + lateBy));
        return (new SearchVessel(1, GroundBody.Planet, a.Position, a.Velocity, Size), new SearchVessel(2, GroundBody.Planet, b.Position, b.Velocity, Size), Vector3.Distance(movingA, movingB));
    }

    /// <summary>Asks <paramref name="search"/> for a pass and waits for it, returning what it found.</summary>
    private static List<PredictedCollision> Pass(CollisionSearch search, ReadOnlySpan<SearchVessel> vessels, double now, double horizon)
    {
        Assert.True(search.TryAsk(vessels, now, horizon));
        var found = new List<PredictedCollision>();
        var clock = Stopwatch.StartNew();
        while (!search.TryTake(found))
        {
            Assert.True(clock.Elapsed < Patience, "the pass should finish");
            Thread.Sleep(1);
        }
        return found;
    }

    [Fact]
    public void Two_satellites_on_crossing_orbits_are_found_to_collide_where_and_when_they_do()
    {
        var (a, b, closing) = Crossing(1800);
        using var search = new CollisionSearch();
        const double now = 500;
        var found = Pass(search, [a, b], now, Scene.MapCollisionHorizonSeconds);
        var collision = Assert.Single(found);
        output.WriteLine($"hits at {collision.At - now:0.000} s, {Vector3.Distance(collision.Where, Place):0.0} m from the place, closing at {collision.ClosingSpeed:0} m/s; the pass took {search.LastPassSeconds * 1000:0.000} ms");
        Assert.Equal((1, 2), (collision.A, collision.B));
        Assert.InRange(collision.At - now, 1799.0, 1801.0);
        Assert.InRange(Vector3.Distance(collision.Where, Place), 0f, 50f);
        // How fast they are apart there: the two velocities they were made with at the place.
        Assert.InRange(collision.ClosingSpeed, closing - 1f, closing + 1f);
    }

    [Fact]
    public void Two_at_different_bodies_are_never_paired()
    {
        // The same two crossing orbits, the second at a body just like the planet (its centre, size
        // and pull, so its path is the same) but another: the two would hit, but are never paired.
        var twin = GroundBody.Circling("Twin", "the ground", 9, GroundBody.Planet, 1f, Planet.Radius, Planet.Mu,
            _ => new BodyPose(default, QuaternionDouble.Identity, default, default));
        Assert.Equal(Planet.Centre, twin.Centre);
        var (a, b, _) = Crossing(1800);
        using var search = new CollisionSearch();
        Assert.Single(Pass(search, [a, b], 500, Scene.MapCollisionHorizonSeconds));
        Assert.Empty(Pass(search, [a, b with { Body = twin }], 500, Scene.MapCollisionHorizonSeconds));
    }

    [Fact]
    public void One_coming_down_onto_one_landed_below_the_moons_datum_is_paired()
    {
        // The moon has no sea, and its craters' floors lie below its datum: a path is followed down
        // to its lowest ground (GroundBody.LowestRadius), not cut at the datum short of one landed
        // there, and one already flying below the datum has a course.
        var moon = GroundBody.Moon;
        var (deepest, floor) = (Vector3.UnitY, float.PositiveInfinity);
        for (var lat = -80; lat <= 80; lat += 2)
            for (var lon = -180; lon < 180; lon += 2)
            {
                var d = CubeSphere.FromLatLon(float.DegreesToRadians(lat), float.DegreesToRadians(lon));
                if (moon.Height(d) < floor)
                    (deepest, floor) = (d, moon.Height(d));
            }
        output.WriteLine($"the deepest floor found: {floor:0.0} m (the map's lowest {moon.Map.MinHeight:0.0} m; lowest surface {moon.LowestRadius - moon.SeaRadius:0.0} m)");
        Assert.True(floor < -4f * Size, $"no floor deep enough below the datum: {floor:0.0} m");
        Assert.True(moon.LowestRadius < moon.SeaRadius + floor);

        // Landed there, its middle its size over the floor; held by the moon's turn.
        var landed = moon.Centre + deepest * (moon.SeaRadius + floor + Size);
        var held = new SearchVessel(2, moon, landed, moon.GroundSpin(landed), Size, OnGround: true);
        // Coming down 45° onto where it will be, at 60 m/s among the stars, from 20 s off (above the
        // datum) and from 1 s off (already below it).
        foreach (var seconds in new[] { 20.0, 1.0 })
        {
            var there = moon.Turned(landed, seconds);
            var up = moon.Up(there);
            var level = Vector3.Normalize(Vector3.Cross(up, moon.NorthPole));
            var arriving = (level - up) * (60f / MathF.Sqrt(2f));
            var (at, moving) = new Orbit(moon, there, arriving, findEnd: false).StateAfter(-seconds);
            output.WriteLine($"{seconds:0} s off: {moon.Altitude(at):0.0} m over the datum");
            Assert.Equal(seconds > 10 ? 1 : -1, MathF.Sign(moon.Altitude(at)));
            using var search = new CollisionSearch();
            var found = Pass(search, [new SearchVessel(1, moon, at, moving, Size), held], 0, 60);
            var collision = Assert.Single(found);
            output.WriteLine($"  paired: at {collision.At:0.000} s, closing {collision.ClosingSpeed:0.0} m/s");
            Assert.InRange(collision.At, seconds - 1.0, seconds + 0.1);
        }
    }

    [Fact]
    public void The_map_search_holds_a_lander_on_the_moon_where_it_stands()
    {
        // A lander standing on the moon's plains, held on the moon's ground as the moon turns (its
        // course fixed there, by the moon's own turn, not the planet's), and one at the moon on its
        // way into it from 90 s off: found at the lander's place by then, within 5 m.
        var moon = GroundBody.Moon;
        var landed = OnTheMoon.Standing(OnTheMoon.LandingSpot).Position + moon.Up(OnTheMoon.Standing(OnTheMoon.LandingSpot).Position) * Size;
        var held = new SearchVessel(2, moon, landed, moon.GroundSpin(landed), Size, OnGround: true);
        const double hitsIn = 90;
        var there = moon.Turned(landed, hitsIn);
        var up = moon.Up(there);
        var level = Vector3.Normalize(Vector3.Cross(up, moon.NorthPole));
        var arriving = level * 100f - up * 150f;
        var (at, moving) = new Orbit(moon, there, arriving, findEnd: false).StateAfter(-hitsIn);
        Assert.True(moon.Altitude(at) > 100f, "it starts well above the ground");
        using var search = new CollisionSearch();
        var collision = Assert.Single(Pass(search, [new SearchVessel(1, moon, at, moving, Size), held], 0, 600));
        var miss = Vector3.Distance(collision.Where, moon.Turned(landed, collision.At));
        output.WriteLine($"found at {collision.At:0.000} s, {miss:0.00} m from where the lander stands then ({Vector3.Distance(landed, there):0.0} m from where it stands now), closing at {collision.ClosingSpeed:0.0} m/s");
        Assert.InRange(collision.At, hitsIn - 1, hitsIn + 0.1);
        Assert.True(miss < 5f, $"found {miss:0.00} m from the lander");
    }

    [Fact]
    public void Two_in_one_plane_are_found_to_collide_too()
    {
        // No line of nodes to look round: one round, the other a little faster through the same
        // place the same way (there at its lowest), so they come together there, nearly side by side.
        var up = Vector3.Normalize(Place - Planet.Centre);
        var speed = MathF.Sqrt(Planet.Mu / Radius);
        var along = Vector3.Normalize(Vector3.Cross(Vector3.Cross(up, Vector3.UnitX), up)) * speed;
        var a = new Orbit(GroundBody.Planet, Place, along, findEnd: false).StateAfter(-1800);
        var b = new Orbit(GroundBody.Planet, Place, along * 1.02f, findEnd: false).StateAfter(-1800);
        using var search = new CollisionSearch();
        var collision = Assert.Single(Pass(search, [new(1, GroundBody.Planet, a.Position, a.Velocity, Size), new(2, GroundBody.Planet, b.Position, b.Velocity, Size)], 0, Scene.MapCollisionHorizonSeconds));
        output.WriteLine($"hits at {collision.At:0.000} s, {Vector3.Distance(collision.Where, Place):0.0} m from the place, closing at {collision.ClosingSpeed:0.0} m/s");
        Assert.InRange(collision.At, 1799.0, 1801.0);
        Assert.InRange(Vector3.Distance(collision.Where, Place), 0f, 50f);
    }

    [Fact]
    public void Orbits_that_never_come_near_give_no_collision()
    {
        // Through the same place half a minute apart (their nodes' passes never line up in the hour
        // after, either), and two round in the same plane at different heights.
        var (a, b, _) = Crossing(1800, lateBy: 30);
        var low = WorldPresets.CircularOrbit(9000f, 0f, 1f, 0f);
        var high = WorldPresets.CircularOrbit(10000f, 0f, 2f, 0f);
        SearchVessel[] vessels =
        [
            a, b,
            new(3, GroundBody.Planet, low.CentreOfMass, Planet.AmongStars(low.CentreOfMass, low.Velocity), Size),
            new(4, GroundBody.Planet, high.CentreOfMass, Planet.AmongStars(high.CentreOfMass, high.Velocity), Size),
        ];
        using var search = new CollisionSearch();
        Assert.Empty(Pass(search, vessels, 0, Scene.MapCollisionHorizonSeconds));
    }

    [Fact]
    public void A_dodge_takes_the_collision_away()
    {
        var (a, b, _) = Crossing(1200);
        using var search = new CollisionSearch();
        Assert.Single(Pass(search, [a, b], 0, Scene.MapCollisionHorizonSeconds));
        // The second burns 3 m/s along its way: the next pass has it on its new orbit, and no collision.
        var dodged = b with { StarsVelocity = b.StarsVelocity * (1f + 3f / b.StarsVelocity.Length()) };
        Assert.Empty(Pass(search, [a, dodged], 0, Scene.MapCollisionHorizonSeconds));
    }

    /// <summary>
    /// A world of <paramref name="copies"/> × the satellites preset's four as the search is handed
    /// them: each centre of mass and its velocity among the stars, and a satellite's size.
    /// </summary>
    private static SearchVessel[] Satellites(int copies, float size)
    {
        var world = WorldTests.ManySatellites(copies);
        return [.. world.Vessels.Select(s =>
        {
            var centre = s.Position + Vector3.Transform(s.CentreOfMassOffset, s.Rotation);
            return new SearchVessel(s.Id, GroundBody.Planet, centre, Planet.AmongStars(centre, s.Velocity), size);
        })];
    }

    /// <summary>
    /// A pass over <paramref name="copies"/> × the satellites preset's four, the longest horizon it
    /// is ever asked (a lap of the flown vessel's orbit is less), timed by the search itself.
    /// </summary>
    private void PassInTime(int copies)
    {
        // A satellite's own size, as the flight hands it over.
        float size;
        using (var sim = new Simulation(world: WorldPresets.Satellites()))
            size = Collisions.Size(sim.Flight.WorldVessels[0]);
        using var search = new CollisionSearch();
        var vessels = Satellites(copies, size);
        var found = Pass(search, vessels, 0, Scene.MapCollisionHorizonSeconds);
        output.WriteLine($"{vessels.Length} satellites (size {size:0.0} m): a pass took {search.LastPassSeconds:0.000} s, {found.Count} collisions found, the first in {(found.Count > 0 ? found.Min(m => m.At) : double.NaN):0} s");
        Assert.True(search.LastPassSeconds < Scene.CollisionSearchSeconds, $"a pass over {vessels.Length} took {search.LastPassSeconds:0.000} s");
    }

    /// <summary>The hundred satellites of the short performance tests' busy world: a few hundredths of a second, so it can share the machine.</summary>
    [Fact]
    public void A_pass_over_a_hundred_satellites_finishes_in_time() => PassInTime(25);

    /// <summary>
    /// A thousand (the world's capacity): about a second in a Debug build, a third of one in
    /// Release; run on its own (<c>Run=Alone</c>), as its thread runs below normal priority and,
    /// beside the other shards, waits on them (3.5 s in a parallel run).
    /// </summary>
    [Fact]
    [Trait("Run", "Alone")]
    public void A_pass_over_a_thousand_satellites_finishes_in_time() => PassInTime(250);

    /// <summary>
    /// A thousand again, but a quarter on the ground (held there as the planet turns) and a
    /// quarter coming down through the air over them: the pairs the ground's vessels add (every
    /// one in the air against every one on the ground, as their heights overlap), each followed
    /// by the angle between them. Run alone, as the thousand satellites are.
    /// </summary>
    [Fact]
    [Trait("Run", "Alone")]
    public void A_pass_over_a_thousand_with_a_crowd_on_the_ground_and_in_the_air_finishes_in_time()
    {
        var satellites = Satellites(125, 1.5f); // 500
        var vessels = new List<SearchVessel>(satellites);
        for (var k = 0; k < 250; k++)
        {
            // Spread round the planet, each on the ground with one coming down over it.
            var (sin, cos) = MathF.SinCos(k * MathF.Tau / 250);
            var up = Vector3.Normalize(new Vector3(cos, 0.6f + 0.3f * sin, sin));
            var standing = Planet.Centre + up * (Planet.SeaRadius + 50f);
            var above = Planet.Centre + up * (Planet.SeaRadius + 2000f);
            var across = Vector3.Normalize(Vector3.Cross(up, Planet.NorthPole));
            vessels.Add(new SearchVessel(1000 + k, GroundBody.Planet, standing, Planet.GroundSpin(standing), 20f, OnGround: true));
            vessels.Add(new SearchVessel(2000 + k, GroundBody.Planet, above, Planet.GroundSpin(above) + across * 100f - up * 50f, 20f));
        }
        using var search = new CollisionSearch();
        var found = Pass(search, [.. vessels], 0, Scene.MapCollisionHorizonSeconds);
        output.WriteLine($"{vessels.Count} vessels (500 satellites, 250 on the ground, 250 coming down): a pass took {search.LastPassSeconds:0.000} s, {found.Count} collisions found ({found.Count(m => m.A >= 1000 && m.A < 2000 || m.B >= 1000 && m.B < 2000)} with one on the ground)");
        Assert.True(search.LastPassSeconds < Scene.CollisionSearchSeconds, $"a pass took {search.LastPassSeconds:0.000} s");
    }

    [Fact]
    public void One_coming_down_onto_a_vessel_on_the_ground_is_found_to_hit_it()
    {
        // Something on the ground 100 m up a hillside, held there as the planet turns, and one
        // coming down at a slant onto where it will be a minute from now (its path ending at the
        // sea's level, beyond it).
        var standing = Planet.Centre + Vector3.Normalize(new Vector3(0.3f, 0.9f, 0.2f)) * (Planet.SeaRadius + 100f);
        const double hitsIn = 60;
        var there = Planet.Turned(standing, hitsIn);
        var up = Vector3.Normalize(there - Planet.Centre);
        var across = Vector3.Normalize(Vector3.Cross(up, Vector3.UnitZ));
        var moving = across * 300f - up * 30f; // nearly round fast, so it was a few hundred metres up a minute before, not below the sea
        var falling = new Orbit(GroundBody.Planet, there, moving, findEnd: false).StateAfter(-hitsIn);
        using var search = new CollisionSearch();
        SearchVessel[] vessels =
        [
            new(1, GroundBody.Planet, standing, Planet.GroundSpin(standing), Size, OnGround: true),
            new(2, GroundBody.Planet, falling.Position, falling.Velocity, Size),
        ];
        var collision = Assert.Single(Pass(search, vessels, 0, Scene.MapCollisionHorizonSeconds));
        output.WriteLine($"hits at {collision.At:0.000} s, {Vector3.Distance(collision.Where, there):0.0} m from where it stands by then, closing at {collision.ClosingSpeed:0.0} m/s");
        Assert.InRange(collision.At, hitsIn - 1, hitsIn + 1);
        Assert.InRange(Vector3.Distance(collision.Where, there), 0f, 50f);
        Assert.InRange(collision.ClosingSpeed, Vector3.Distance(moving, Planet.GroundSpin(there)) - 1f, Vector3.Distance(moving, Planet.GroundSpin(there)) + 1f);

        // Two on the ground never hit, however close.
        Assert.Empty(Pass(search, [vessels[0], new(3, GroundBody.Planet, standing + up * 20f, Planet.GroundSpin(standing), Size, OnGround: true)], 0, Scene.MapCollisionHorizonSeconds));
    }

    [Fact]
    public void Each_is_wrecked_by_its_own_tolerance_and_its_later_collisions_dropped()
    {
        // Two crossings through the same place: vessel 1 hits 2 there at 600 s, then, a lap of
        // its own later, 3 (on 2's orbit, but that much behind). Over 1's tolerance the first
        // wrecks it, and the second never comes; with a tolerance over the closing speed, the
        // first is a bump and both are shown.
        var (a, b, closing) = Crossing(600);
        var lap = new Orbit(GroundBody.Planet, a.Position, a.StarsVelocity, findEnd: false).Period;
        var later = Crossing(600 + lap).B with { Id = 3 };
        using var search = new CollisionSearch();
        var wrecks = Pass(search, [a, b, later], 0, Scene.MapCollisionHorizonSeconds);
        output.WriteLine($"wrecked by the first: {string.Join(", ", wrecks.Select(m => $"{m.A}-{m.B} at {m.At:0}"))}");
        Assert.DoesNotContain(wrecks, m => m.B == 3);
        var bumps = Pass(search, [a with { ImpactTolerance = closing + 10f }, b with { ImpactTolerance = closing + 10f }, later], 0, Scene.MapCollisionHorizonSeconds);
        output.WriteLine($"bumped by the first: {string.Join(", ", bumps.Select(m => $"{m.A}-{m.B} at {m.At:0}"))}");
        Assert.Contains(bumps, m => (m.A, m.B) == (1, 3));
    }

    /// <summary>
    /// Three satellites as the map tests have them: the preset's lowest (0), and two others (1
    /// and 2) a little higher, on round orbits in planes at right angles, both coming to the far
    /// side of the planet <paramref name="hitsIn"/> seconds from now.
    /// </summary>
    private static WorldState CollidingPair(float hitsIn, float altitude = 9300f)
    {
        var preset = WorldPresets.Satellites();
        var flown = preset.Vessels[0];
        var (vehicle, stage) = (flown.Vehicle, flown.First);
        var height = RocketGeometry.CentreOfMassHeight(vehicle, stage, stage);
        var turnRate = Planet.CircularSpeed(altitude) / (Planet.SeaRadius + altitude); // radians a second round it
        VesselState At(int id, float tilt)
        {
            var (position, centre, velocity) = WorldPresets.CircularOrbit(altitude, tilt, MathF.PI - turnRate * hitsIn, height);
            return flown with { Id = id, Position = position, Rotation = WorldPresets.RotationTo(Vector3.Normalize(centre - Planet.Centre)), Velocity = velocity, Aim = Vector3.Normalize(centre - Planet.Centre) };
        }
        return preset with { Vessels = [flown, At(1, 0f), At(2, MathF.PI / 2)], NextVesselId = 3 };
    }

    /// <summary>Builds the map's paths, frame after frame (as the game draws it), until a collision is drawn.</summary>
    private static Paths MapUntilACollision(Flight flight)
    {
        var paths = new Paths();
        var clock = Stopwatch.StartNew();
        do
        {
            Assert.True(clock.Elapsed < Patience, "the collision should be found");
            Thread.Sleep(1);
            Build(paths, flight);
        }
        while (paths.CollisionMarks.Count == 0);
        return paths;
    }

    private static readonly ManoeuvrePlan Plan = new();

    /// <summary>One frame's map, from the flown vessel's path as it is (no nodes).</summary>
    private static void Build(Paths paths, Flight flight)
    {
        Plan.Update(GroundBody.Planet, flight.CentreOfMass, Planet.AmongStars(flight.CentreOfMass, flight.Velocity), flight.WorldTime);
        paths.Build(flight, Plan);
    }

    [Fact]
    public void The_map_draws_a_collision_and_the_lead_in_fainter_for_other_vessels()
    {
        const float altitude = 9300f, hitsIn = 60f;
        var world = CollidingPair(hitsIn, altitude);
        using var sim = new Simulation(world: world);
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.Single(v => v.Id == 0)));
        Assert.Null(flight.Target);

        var paths = MapUntilACollision(flight);
        var mark = Assert.Single(paths.CollisionMarks);
        output.WriteLine($"\"{mark.Label}\"; lead-ins: {string.Join(", ", paths.LeadIns.Select(l => $"vessel {l.Vessel} at {l.Alpha} ({l.Points.Count} points)"))}");
        Assert.Equal(2, paths.LeadIns.Count);
        Assert.Equal([1, 2], paths.LeadIns.Select(l => l.Vessel).Order());
        Assert.All(paths.LeadIns, l => Assert.Equal(Scene.OtherPathAlpha, l.Alpha));
        // Each runs into the cross, from no earlier than now (the collision is under the lead's two minutes off).
        Assert.All(paths.LeadIns, l => Assert.InRange(Vector3.Distance(l.Points[^1], mark.At), 0f, 2 * Size + 10f));
        var far = Planet.Centre - Vector3.UnitY * (Planet.SeaRadius + altitude); // the far side, under the pad
        // Drawn in the line's frame: among the stars, where it is; over the ground, where the ground will be by then.
        Assert.InRange(Vector3.Distance(mark.At, Frames.OnLine(GroundBody.Planet, flight.LineFrame, far, hitsIn - flight.WorldTime + world.Time)), 0f, 100f);
        Assert.Contains("Satellite 1", mark.Label);
        Assert.Contains("Satellite 2", mark.Label);

        // One of the two wrecked since the pass (by anything): the collision is no longer drawn.
        var gone = flight.WorldVessels.Single(v => v.Id == 1);
        Assert.True(gone.OnFormula);
        gone.WreckByFormula("Wrecked before it got there.", gone.CentreOfMass, explode: false);
        Build(paths, flight);
        Assert.Empty(paths.CollisionMarks);
        Assert.Empty(paths.LeadIns);
    }

    [Fact]
    public void The_map_draws_the_flown_vessels_and_the_targets_lead_ins_as_strong_as_their_paths()
    {
        // Flying one of the two, the other its target: their lead-ins as their own lines are drawn.
        using var sim = new Simulation(world: CollidingPair(60f));
        var flight = sim.Flight;
        var (flown, target) = (flight.WorldVessels.Single(v => v.Id == 1), flight.WorldVessels.Single(v => v.Id == 2));
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(target)));
        Assert.Equal(NavTarget.OfVessel(target), flight.Target);

        var paths = MapUntilACollision(flight);
        Assert.Single(paths.CollisionMarks);
        Assert.Equal(0.9f, paths.LeadIns.Single(l => l.Vessel == 1).Alpha);
        Assert.Equal(0.55f, paths.LeadIns.Single(l => l.Vessel == 2).Alpha);
    }

    /// <summary>
    /// The map's own frame (its <see cref="Paths.Build"/>: the flown path and, here, the collisions)
    /// in the world of a thousand satellites, once the search's collisions are in: each frame timed,
    /// the worst and the 95th percentile printed and held under <see cref="ShortPerf.PlayableFrameMs"/>.
    /// Run alone, as the search's thread works beside it throughout, and on a shared machine its
    /// frames would be the other shards' too. The game's Release numbers are in the task's report.
    /// </summary>
    [Fact]
    [Trait("Run", "Alone")]
    public void The_maps_frame_in_a_world_of_a_thousand_satellites_is_bounded()
    {
        using var sim = new Simulation(world: WorldTests.ManySatellites(250));
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.State == FlightState.Flying)));
        var paths = MapUntilACollision(flight);
        var pass = flight.CollisionPass;
        Assert.True(pass > 0, "no pass was ever taken");
        var frames = new List<double>();
        var clock = Stopwatch.StartNew();
        while (frames.Count < 600 || flight.CollisionPass < pass + 2) // over two passes taken and laid, at least
        {
            Assert.True(clock.Elapsed < Patience, "the passes should keep coming");
            sim.Step(default); // the world moves on, as in play (a pass is asked only once it has), untimed
            var start = Stopwatch.GetTimestamp();
            Build(paths, flight);
            frames.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        Assert.True(flight.CollisionPass >= pass + 2);
        frames.Sort();
        var (worst, p95, median) = (frames[^1], frames[(int)(frames.Count * 0.95)], frames[frames.Count / 2]);
        output.WriteLine($"{flight.MapCollisions.Count} collisions in the last pass, {paths.CollisionMarks.Count} drawn; over {frames.Count} frames the map took {median:0.000} ms (median), {p95:0.000} ms (95th percentile), {worst:0.000} ms at worst; a pass {flight.CollisionSearchLastPassSeconds:0.000} s");
        Assert.InRange(paths.CollisionMarks.Count, 1, Scene.CollisionsShown);
        Assert.True(p95 < ShortPerf.PlayableFrameMs, $"the map's frame took {p95:0.000} ms at the 95th percentile");
    }

    [Fact]
    public void The_map_asks_for_no_pass_while_the_world_stands_still()
    {
        // The map up, the game paused (nothing stepped): once a pass is taken, none is asked over
        // the same unchanged world, so the search's thread rests. The world moved on a step, the
        // next pass comes.
        using var sim = new Simulation(world: CollidingPair(60f));
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.Single(v => v.Id == 0)));
        var paths = MapUntilACollision(flight);
        var pass = flight.CollisionPass;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(0.5))
        {
            Thread.Sleep(1);
            Build(paths, flight);
        }
        Assert.Equal(pass, flight.CollisionPass); // none asked, so none taken
        Assert.Single(paths.CollisionMarks); // and the last one's still drawn

        sim.Step(default);
        clock.Restart();
        while (flight.CollisionPass == pass)
        {
            Assert.True(clock.Elapsed < Patience, "no pass came once the world moved on");
            Thread.Sleep(1);
            Build(paths, flight);
        }
    }

    [Fact]
    public void The_search_stops_with_its_flight()
    {
        var (a, b, _) = Crossing(600);
        using (var search = new CollisionSearch())
        {
            Pass(search, [a, b], 0, Scene.MapCollisionHorizonSeconds);
            Assert.True(search.Running);
            search.Dispose();
            Assert.False(search.Running);
        }

        // A flight's own, once it has been asked for one (by the map), goes with the flight.
        var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels[0]));
        flight.LookForCollisions();
        Assert.True(flight.CollisionSearchRunning);
        sim.Dispose();
        Assert.False(flight.CollisionSearchRunning);
    }
}
