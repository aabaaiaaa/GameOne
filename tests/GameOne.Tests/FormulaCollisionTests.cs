using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Collisions between vessels under formula over a frame at a formula's rate
/// (<see cref="FormulaCollisions"/>): found along each window's way, looking back once every
/// vessel has been moved to its end, and judged as at normal speed (both wrecked over the
/// tolerance, where they met; a bump or a near pass leaves both as they were), none skipped among
/// a hundred, and nothing allocated.
/// </summary>
public sealed class FormulaCollisionTests(ITestOutputHelper output)
{
    /// <summary>How far into the frame the pairs here meet: well within a top-rate frame (273 s), many windows in.</summary>
    private const float MeetIn = 150f;

    /// <summary>How high over the sea the pairs here meet: high enough that at the slowest (250 m/s each) the meeting is the lowest of their orbits, all of it above the air.</summary>
    internal const float MeetHeight = 20000f;

    /// <summary>Where the pairs here meet: over <see cref="UnwatchedTests.Opposite"/>, <see cref="MeetHeight"/> up.</summary>
    private static readonly Vector3 HitPoint = Planet.Centre + Planet.Up(UnwatchedTests.Opposite) * (Planet.SeaRadius + MeetHeight);

    /// <summary>
    /// <see cref="FastCollisionTests.Pair"/>'s two boosters, their middles meeting at <see cref="HitPoint"/>,
    /// closing at <paramref name="closing"/> with their middles <paramref name="sideways"/> apart,
    /// <paramref name="meetIn"/> s after the world's start (time 0) along their Kepler ways, as the
    /// formula flies them: so far back, the physics' own fall (Pair's aim) and Kepler's part by
    /// hundreds of metres.
    /// </summary>
    internal static WorldState Meeting(float closing, float sideways = 0f, float meetIn = MeetIn)
    {
        var there = FastCollisionTests.Pair(closing, MeetHeight, 0f, sideways: sideways, over: UnwatchedTests.Opposite);
        return there with { Time = 0.0, Vessels = [.. there.Vessels.Select(s => Back(GroundBody.Planet, s, meetIn))] };
    }

    /// <summary>
    /// <paramref name="s"/> (flying free) moved to where it was <paramref name="seconds"/> before, along
    /// its Kepler orbit: flown on that long the other way and turned round, in the ground's frame
    /// as it was then, its attitude kept among the stars.
    /// </summary>
    internal static VesselState Back(GroundBody body, VesselState s, double seconds)
    {
        var at = s.CentreOfMass.ToSingle();
        var (place, moving) = new Orbit(body, at, -body.AmongStars(at, s.Velocity.ToSingle()), findEnd: false).StateAfter(seconds);
        // Among the stars as the ground was at the meeting: the ground then, seconds earlier, was turned back by its turn.
        var then = body.Turned(place, seconds);
        var velocity = body.OverGround(then, body.TurnedDirection(-moving, seconds));
        var rotation = Quaternion.Normalize(Quaternion.Concatenate(s.Rotation, Quaternion.CreateFromAxisAngle(body.NorthPole, body.TurnAngle(seconds))));
        return s with { Position = (Vector3Double)then - QuaternionDouble.From(rotation).Rotate(s.CentreOfMassOffset), Rotation = rotation, Aim = body.TurnedDirection(s.Aim, seconds), Velocity = velocity };
    }

    [Theory]
    [InlineData(500f)]
    [InlineData(2000f)]
    [InlineData(8000f)]
    public void Two_vessels_under_formula_colliding_inside_a_top_rate_frame_are_both_wrecked_as_at_normal_speed(float closing)
    {
        var (sim, a, b) = UnwatchedTests.Unflown(Meeting(closing), UnwatchedTests.Opposite);
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        var start = flight.WorldTime;
        sim.Step(default); // one frame, the collision within it
        output.WriteLine($"{flight.WorldTime - start:0.0} s in {flight.FormulaWindows} windows; {a.State} {a.CrashReason}; {b.State} {b.CrashReason}");
        Assert.True(flight.WorldTime - start > MeetIn, "the frame should run past the collision");
        foreach (var v in new[] { a, b })
        {
            Assert.Equal(FlightState.Crashed, v.State);
            Assert.StartsWith("Hit another vessel", v.CrashReason);
            var off = Vector3.Distance(v.CrashPosition, HitPoint);
            output.WriteLine($"vessel {v.Id} wrecked {off:0.0} m from where they were to collide");
            Assert.InRange(off, 0f, 50f);
        }
        Assert.Equal(Scene.TopRate, flight.PlayerRate); // nobody watched them: the warp goes on
    }

    [Fact]
    public void Two_under_formula_that_miss_by_a_little_inside_a_frame_are_both_untouched()
    {
        // Closing at 2,000 m/s, passing with their middles two sizes and 2 m apart: just outside
        // each other's reach.
        var miss = 2f * FastCollisionTests.BoosterSize + 2f;
        var (sim, a, b) = UnwatchedTests.Unflown(Meeting(2000f, sideways: miss), UnwatchedTests.Opposite);
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        var start = flight.WorldTime;
        sim.Step(default);
        Assert.True(flight.WorldTime - start > MeetIn, "the frame should run past their pass");
        Assert.Equal(FlightState.Flying, a.State);
        Assert.Equal(FlightState.Flying, b.State);
    }

    [Fact]
    public void Two_under_formula_passing_within_their_sizes_but_clear_of_each_others_bodies_inside_a_frame_are_both_untouched()
    {
        // Their middles passing 10 m apart: within their sizes (22 m together), but each body clear of the other's.
        var (sim, a, b) = UnwatchedTests.Unflown(Meeting(2000f, sideways: 10f), UnwatchedTests.Opposite);
        using var _ = sim;
        FormulaRateTests.ToTheTop(sim.Flight);
        sim.Step(default);
        Assert.Equal(FlightState.Flying, a.State);
        Assert.Equal(FlightState.Flying, b.State);
    }

    [Fact]
    public void A_slow_bump_inside_a_top_rate_frame_wrecks_neither()
    {
        // Two satellites on one circle 9 km up, the one behind 3 m/s faster: they touch some 5 s
        // in, closing at about 3 m/s, under the tolerance, and pass through each other after.
        var (behind, ahead) = UnwatchedTests.Closing(3f);
        var (sim, first, second) = UnwatchedTests.Unflown(new WorldState { Vessels = [behind, ahead], NextVesselId = 2 }, behind.Position.ToSingle());
        using var _ = sim;
        FormulaRateTests.ToTheTop(sim.Flight);
        sim.Step(default);
        Assert.Equal(FlightState.Flying, first.State);
        Assert.Equal(FlightState.Flying, second.State);
    }

    [Fact]
    public void A_bump_over_the_tolerance_inside_a_top_rate_frame_wrecks_both()
    {
        // As the slow bump, the one behind 10 m/s faster: over the tolerance (6 m/s). Their bodies
        // begin to touch seconds before their middles are closest, which is found.
        var (behind, ahead) = UnwatchedTests.Closing(10f);
        var (sim, first, second) = UnwatchedTests.Unflown(new WorldState { Vessels = [behind, ahead], NextVesselId = 2 }, behind.Position.ToSingle());
        using var _ = sim;
        var flight = sim.Flight;
        var judged = flight.FormulaCollisions.Judged;
        FormulaRateTests.ToTheTop(flight);
        sim.Step(default);
        foreach (var v in new[] { first, second })
        {
            Assert.Equal(FlightState.Crashed, v.State);
            Assert.StartsWith("Hit another vessel at", v.CrashReason);
        }
        Assert.Equal(1, flight.FormulaCollisions.Judged - judged);
        output.WriteLine($"{first.CrashReason}");
        var closing = float.Parse(first.CrashReason!.Split(' ')[4], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(closing, 6f, 12f); // judged at their touch's beginning, closing at about 10 m/s
    }

    [Fact]
    public void A_crowd_of_a_hundred_at_the_top_rate_finds_every_collision_brute_force_finds()
    {
        // Five pairs among the hundred satellites set to meet at random moments within one frame:
        // the second of each put where the first will be then, crossing its way at right angles
        // on a round orbit of its own, sent back along it to the world's start. The polar orbit's
        // copies (every fourth) all pass over the pole, and meet there: each raised 30 m over the
        // last, so the five are the crowd's only collisions.
        var planet = GroundBody.Planet;
        var world = WorldTests.HundredSatellites() with { Time = 0.0 };
        var vessels = world.Vessels.ToArray();
        for (var k = 3; k < vessels.Length; k += 4)
            vessels[k] = Raised(planet, vessels[k], 30f * (k / 4));
        var random = new Random(6);
        (int A, int B)[] pairs = [(5, 6), (13, 22), (30, 47), (58, 71), (85, 99)];
        var frame = Scene.TopRate * (double)Simulation.FrameSeconds;
        foreach (var (i, j) in pairs)
        {
            var meet = 10.0 + random.NextDouble() * (frame - 30.0);
            vessels[j] = Crossing(planet, vessels[i], vessels[j], meet);
        }
        var crowd = world with { Vessels = [.. vessels] };
        var (sim, _) = WarpScenes.Flying(crowd, crowd.Vessels[0].Id);
        using var _s = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        var (start, judged) = (flight.WorldTime, flight.FormulaCollisions.Judged);
        sim.Step(default);
        var end = flight.WorldTime;
        var found = flight.FormulaCollisions.Judged - judged;
        output.WriteLine($"{end - start:0.0} s in {flight.FormulaWindows} windows: {found} judged");

        var expected = pairs.SelectMany(p => new[] { vessels[p.A].Id, vessels[p.B].Id }).ToHashSet();
        foreach (var v in flight.WorldVessels)
        {
            if (expected.Contains(v.Id))
            {
                Assert.Equal(FlightState.Crashed, v.State);
                Assert.StartsWith("Hit another vessel", v.CrashReason);
            }
            else
                Assert.NotEqual(FlightState.Crashed, v.State); // the satellites flying on, the free-play rocket still on its pad
        }

        // Brute force: every pair, along their Kepler ways from the world's start, sampled every
        // 0.05 s across the frame, each stretch between samples looked along for its closest.
        var ways = crowd.Vessels.Select(s =>
        {
            var at = s.CentreOfMass.ToSingle();
            return new Orbit(planet, at, planet.AmongStars(at, s.Velocity.ToSingle()), findEnd: false);
        }).ToArray();
        const double Sample = 0.05;
        var samples = (int)Math.Ceiling((end - start) / Sample) + 1;
        var places = new Vector3[ways.Length, samples];
        for (var k = 0; k < ways.Length; k++)
            for (var s = 0; s < samples; s++)
                places[k, s] = ways[k].StateAfter(start + s * Sample).Position;
        var touching = 0;
        var width = flight.WorldVessels[0].Width; // all alike
        for (var p = 0; p < ways.Length; p++)
            for (var q = p + 1; q < ways.Length; q++)
            {
                var closest = float.MaxValue;
                for (var s = 1; s < samples; s++)
                {
                    var (r0, r1) = (places[q, s - 1] - places[p, s - 1], places[q, s] - places[p, s]);
                    var d = r1 - r0;
                    var t = d.LengthSquared() > 1e-9f ? Math.Clamp(-Vector3.Dot(r0, d) / d.LengthSquared(), 0f, 1f) : 0f;
                    closest = MathF.Min(closest, (r0 + d * t).Length());
                }
                if (closest < 2f * width)
                {
                    output.WriteLine($"brute force: {crowd.Vessels[p].Id} and {crowd.Vessels[q].Id} within {closest:0.00} m");
                    touching++;
                }
            }
        Assert.Equal(pairs.Length, touching);
        Assert.Equal(touching, found);
    }

    /// <summary>
    /// <paramref name="other"/> moved onto a round orbit of its own that meets <paramref name="first"/>
    /// (on its round orbit) at world time <paramref name="meet"/>, crossing its way there at right
    /// angles: where that orbit has it at the world's start (time 0), flown back from the meeting.
    /// </summary>
    private static VesselState Crossing(GroundBody body, VesselState first, VesselState other, double meet)
    {
        var at = first.CentreOfMass.ToSingle();
        var (place, moving) = new Orbit(body, at, body.AmongStars(at, first.Velocity.ToSingle()), findEnd: false).StateAfter(meet);
        var up = Vector3.Normalize(place - body.Centre);
        var crossing = Vector3.Transform(moving, Quaternion.CreateFromAxisAngle(up, MathF.PI / 2f));
        // Back along it: flown on from the meeting the other way, and turned round.
        var (back, backMoving) = new Orbit(body, place, -crossing, findEnd: false).StateAfter(meet);
        var rotation = Quaternion.Concatenate(other.Rotation, Formula.Turn(Vector3.Transform(Vector3.UnitY, other.Rotation), Vector3.Normalize(back - body.Centre)));
        var origin = (Vector3Double)back - QuaternionDouble.From(rotation).Rotate(other.CentreOfMassOffset);
        return other with { Position = origin, Rotation = rotation, Aim = Vector3.Normalize(back - body.Centre), Velocity = body.OverGround(back, -backMoving) };
    }

    /// <summary><paramref name="s"/>, on a round orbit, put on the round orbit <paramref name="metres"/> higher, along the same way.</summary>
    private static VesselState Raised(GroundBody body, VesselState s, float metres)
    {
        var at = s.CentreOfMass.ToSingle();
        var radius = Vector3.Distance(at, body.Centre);
        var higher = body.Centre + (at - body.Centre) * ((radius + metres) / radius);
        var stars = body.AmongStars(at, s.Velocity.ToSingle()) * MathF.Sqrt(radius / (radius + metres));
        return s with { Position = s.Position + (Vector3Double)(higher - at), Velocity = body.OverGround(higher, stars) };
    }

    [Fact]
    public void Finding_collisions_over_a_top_rate_frame_allocates_nothing()
    {
        var (sim, _) = WarpScenes.HundredSatellites();
        using var _s = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        for (var i = 0; i < 5; i++)
            sim.Step(default); // warmed up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
            flight.RunFrame(Simulation.FrameSeconds, default);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
    }
}
