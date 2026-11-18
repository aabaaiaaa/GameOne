using System.Collections.Immutable;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Manoeuvre nodes (<see cref="ManoeuvreNode"/>, a <see cref="ManoeuvreChain"/> of them): the path
/// a plan of them makes, flying one by its guide (the delta-v left, counting down, and the
/// overshoot), how they go once matched with the engine cut (and stay until then), that they stay where they were set whatever the vessel
/// does meanwhile, how changing, moving and deleting one redoes the chain after it, and that they
/// are kept per vessel.
/// </summary>
public sealed class NodeTests(ITestOutputHelper output)
{
    /// <summary>The speed to add on a circular orbit <paramref name="from"/> m up to reach <paramref name="to"/> m up on the far side (half a Hohmann transfer).</summary>
    private static float RaiseFrom(float from, float to)
    {
        float r1 = Planet.SeaRadius + from, r2 = Planet.SeaRadius + to;
        return MathF.Sqrt(Planet.Mu / r1) * (MathF.Sqrt(2f * r2 / (r1 + r2)) - 1f);
    }

    /// <summary>The satellites world, flying its lowest satellite (in a circular orbit 9 km up).</summary>
    private static Simulation Satellite(out Vessel satellite)
    {
        var sim = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        satellite = sim.Flight.WorldVessels.First(v => v.Flight == 0);
        sim.Step(new FlightInput(SwitchTo: satellite));
        return sim;
    }

    /// <summary><paramref name="nodes"/> anchored on <paramref name="v"/>'s path as it is now, the first at world time <paramref name="first"/> (its gap from there: 0 puts it there).</summary>
    internal static ManoeuvreChain Chain(Vessel v, double now, double first, params ManoeuvreNode[] nodes) =>
        ManoeuvreChain.AnchoredOn(v, first, [.. nodes], now);

    /// <summary>A chain of three on the satellite's orbit: the first 100 s on, the others 300 s and 400 s after the one before.</summary>
    private static ManoeuvreChain Three(Vessel satellite, double now) =>
        Chain(satellite, now, now + 100, new ManoeuvreNode(0, Prograde: 20f), new ManoeuvreNode(300, Prograde: 3f), new ManoeuvreNode(400, Radial: 2f, Normal: -1f));

    private static ManoeuvrePlan PlanOf(ManoeuvreChain chain, double now)
    {
        var plan = new ManoeuvrePlan();
        plan.Update(chain, now);
        return plan;
    }

    /// <summary>Where <paramref name="satellite"/>'s own orbit (the engine off) has it <paramref name="seconds"/> from now, as the ground is now.</summary>
    private static Vector3 OnItsOrbit(Vessel satellite, double seconds) =>
        new Orbit(GroundBody.Planet, satellite.CentreOfMass, Planet.AmongStars(satellite.CentreOfMass, satellite.Velocity)).StateAfter(seconds).Position;

    [Fact]
    public void A_prograde_node_raises_the_far_side_of_the_planned_path()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 100, new ManoeuvreNode(0, Prograde: RaiseFrom(9000f, 10000f)));
        var plan = PlanOf(chain, flight.WorldTime);

        Assert.Equal(2, plan.Legs.Count);
        Assert.Equal(chain.TimeOf(0), plan.Legs[0].End, 6);
        var after = plan.Last.Orbit;
        var far = after.NextApoapsis!.Value;
        var top = after.Distance(far) - Planet.SeaRadius;
        output.WriteLine($"top of the planned path {top:0} m, {after.TimeTo(far):0} s after the node");
        Assert.InRange(top, 9990, 10010);
        // Half a lap of the new orbit on from the node: the far side is opposite where it burned.
        var (at, _) = plan.NodeAt(0)!.Value;
        Assert.True(Vector3.Dot(Vector3.Normalize(at - Planet.Centre), Vector3.Normalize(after.At(far) - Planet.Centre)) < -0.999f);
    }

    /// <summary>
    /// As a pilot would: point along the burn (prograde hold, set beforehand), light the engine
    /// half the burn's time before the node (or at once, past it), and cut it once the delta-v left
    /// is gone (or goes past). Seen in <paramref name="seen"/>: the least delta-v left, whether it
    /// was ever matched while lit, and when the engine was cut.
    /// </summary>
    private static Pilot ByTheGuide(Vessel vessel, Seen seen) => (f, _) =>
    {
        if (vessel.Guide(f.WorldTime) is not { } guide)
            return default;
        seen.Lowest = MathF.Min(seen.Lowest, guide.Left.Length());
        if (f.EngineLit)
        {
            seen.MatchedLit |= guide.Matched;
            if (guide.Overshot || guide.Left.Length() < 0.05f)
            {
                seen.Cut ??= f.WorldTime;
                return new FlightInput(CutThrottle: true);
            }
            return default;
        }
        return seen.Cut is null && guide.Seconds <= guide.BurnSeconds / 2 ? new FlightInput(FullThrottle: true) : default;
    };

    private sealed class Seen
    {
        public float Lowest = float.MaxValue;
        public bool MatchedLit;
        public double? Cut;
    }

    /// <summary>The height of the top of <paramref name="v"/>'s orbit now over the sea.</summary>
    private static double TopOf(Vessel v) =>
        Orbit.OverGround(GroundBody.Planet, v.CentreOfMass, v.Velocity) is var o ? o.Distance(o.NextApoapsis!.Value) - Planet.SeaRadius : 0;

    [Fact]
    public void The_manoeuvre_hold_points_the_nose_along_the_burn_left()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 60, new ManoeuvreNode(0, Prograde: 20f, Normal: 10f));
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Manoeuvre));
        Assert.Equal(HoldMode.Manoeuvre, flight.Holding);
        sim.Run((_, _) => default, f => f.WorldTime >= chain.TimeOf(0) - 49, maxSeconds: 20);

        var left = satellite.BurnLeft(flight.WorldTime)!.Value;
        var off = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(satellite.Up), Vector3.Normalize(left)), -1f, 1f)));
        output.WriteLine($"nose {off:0.0}° off the burn left");
        Assert.True(off < 2f, $"the nose is {off:0.0}° off the burn left");
        Assert.Equal(HoldMode.Manoeuvre, flight.Holding);
    }

    [Fact]
    public void The_manoeuvre_hold_cannot_be_chosen_without_a_node()
    {
        using var sim = Satellite(out _);
        sim.Step(new FlightInput(ChooseHold: HoldMode.Manoeuvre));
        Assert.Equal(HoldMode.None, sim.Flight.Holding);

        // Nor does it displace a hold already on.
        sim.Step(new FlightInput(ChooseHold: HoldMode.Prograde));
        sim.Step(new FlightInput(ChooseHold: HoldMode.Manoeuvre));
        Assert.Equal(HoldMode.Prograde, sim.Flight.Holding);
    }

    [Fact]
    public void The_manoeuvre_hold_turns_off_when_the_last_node_goes()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Plan: Chain(satellite, flight.WorldTime, flight.WorldTime + 60, new ManoeuvreNode(0, Prograde: 5f)), ChooseHold: HoldMode.Manoeuvre));
        Assert.Equal(HoldMode.Manoeuvre, flight.Holding);

        sim.Step(new FlightInput(Plan: Chain(satellite, flight.WorldTime, flight.WorldTime + 60)));
        Assert.Null(satellite.Chain);
        sim.Step(new FlightInput());
        Assert.Equal(HoldMode.None, flight.Holding);
    }

    [Fact]
    public void A_node_flown_by_its_guide_turns_matched_and_goes_when_the_engine_is_cut()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 60, new ManoeuvreNode(0, Prograde: RaiseFrom(9000f, 10000f)));
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Prograde));
        Assert.Single(satellite.Chain!.Nodes);
        var planned = PlanOf(satellite.Chain, flight.WorldTime).Last.Orbit;
        var plannedTop = planned.Distance(planned.NextApoapsis!.Value) - Planet.SeaRadius;
        var startLeft = flight.Guide!.Value.Left.Length();
        Assert.False(flight.Guide!.Value.Matched);

        var seen = new Seen();
        var timeWhenGone = 0.0;
        Assert.True(sim.Run(ByTheGuide(satellite, seen), f =>
        {
            timeWhenGone = f.WorldTime;
            return satellite.Chain is null;
        }, maxSeconds: 120), "the node should go once matched and cut");
        output.WriteLine($"delta-v left from {startLeft:0.00} m/s down to {seen.Lowest:0.000}; cut {seen.Cut - chain.TimeOf(0):0.00} s after its time, gone {timeWhenGone - chain.TimeOf(0):0.00} s after");
        Assert.InRange(startLeft, RaiseFrom(9000f, 10000f) - 0.05f, RaiseFrom(9000f, 10000f) + 0.05f);
        Assert.True(seen.Lowest < 0.1f, $"the burn should use up the delta-v left (it got to {seen.Lowest:0.000} m/s)");
        Assert.True(seen.MatchedLit, "the node should turn matched as the burn nears its end");

        // It went as the engine was cut, matched: within a step or two of the cut.
        Assert.NotNull(seen.Cut);
        Assert.InRange(timeWhenGone, seen.Cut!.Value, seen.Cut.Value + 0.1);
        Assert.False(flight.EngineLit);
        Assert.Null(flight.Guide);
        var top = TopOf(satellite);
        output.WriteLine($"planned top {plannedTop:0} m, flown {top:0} m");
        Assert.InRange(top, plannedTop - 30, plannedTop + 30);
    }

    [Fact]
    public void A_node_past_its_time_stays_until_matched()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 5, new ManoeuvreNode(0, Prograde: 10f));
        sim.Step(new FlightInput(Plan: chain));
        sim.Run((_, _) => default, f => f.WorldTime > chain.TimeOf(0) + 20, maxSeconds: 30);

        Assert.Equal(chain, satellite.Chain);
        var guide = flight.Guide!.Value;
        output.WriteLine($"{-guide.Seconds:0.0} s past its time, {guide.Left.Length():0.00} m/s left");
        Assert.True(guide.Seconds < -20);
        Assert.False(guide.Matched);
        Assert.InRange(guide.Left.Length(), 9f, 11f); // the whole burn, still to make
    }

    [Fact]
    public void Cutting_the_engine_before_it_is_matched_leaves_the_node()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 10, new ManoeuvreNode(0, Prograde: 10f));
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Prograde));
        // Turned prograde by half a second before its time, about 3 m/s of the 10 either side of
        // it, then cut.
        sim.Run((_, _) => default, f => f.Guide!.Value.Seconds < 0.5, maxSeconds: 10);
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 1);
        var cut = flight.Guide!.Value;
        sim.Run((_, _) => new FlightInput(CutThrottle: true), _ => false, maxSeconds: 1);
        Assert.False(flight.EngineLit);
        sim.Run((_, _) => default, _ => false, maxSeconds: 5);

        Assert.Equal(chain, satellite.Chain);
        var guide = flight.Guide!.Value;
        output.WriteLine($"{cut.Left.Length():0.00} m/s left as it was cut, {guide.Left.Length():0.00} m/s {-guide.Seconds:0.0} s past its time");
        Assert.False(cut.Matched);
        Assert.False(guide.Matched);
        Assert.InRange(guide.Left.Length(), 6f, 8f);
    }

    [Theory]
    [InlineData(10f, 1f)]
    [InlineData(100f, 3f)]
    [InlineData(1000f, 5f)]
    public void The_match_tolerance_is_a_share_of_the_burn_held_between_1_and_5_ms(float size, float tolerance)
    {
        Assert.Equal(tolerance, new ManoeuvreNode(0, Prograde: size).MatchTolerance, 4);
        Assert.Equal(tolerance, new ManoeuvreNode(0, Prograde: -size * 0.6f, Radial: size * 0.8f).MatchTolerance, 4);
    }

    [Fact]
    public void A_node_matched_and_cut_in_warp_goes()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 20, new ManoeuvreNode(0, Prograde: RaiseFrom(9000f, 10000f)));
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Prograde));
        while (flight.PlayerRate < 4f)
            sim.Step(new FlightInput(Faster: true));

        var seen = new Seen();
        Assert.True(sim.Run(ByTheGuide(satellite, seen), _ => satellite.Chain is null, maxSeconds: 60), "the node should go once matched and cut, in warp too");
        output.WriteLine($"at {flight.PlayerRate}x: delta-v left down to {seen.Lowest:0.000} m/s, cut {seen.Cut - chain.TimeOf(0):0.00} s after its time");
        Assert.Equal(4f, flight.PlayerRate);
        Assert.True(seen.MatchedLit);
        Assert.False(flight.EngineLit);
    }

    [Fact]
    public void A_late_burn_counts_against_the_planned_path_nearest_the_vessel()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 2, new ManoeuvreNode(0, Prograde: RaiseFrom(9000f, 10000f)));
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Prograde));
        sim.Run((_, _) => default, f => f.WorldTime > chain.TimeOf(0) + 10, maxSeconds: 15);

        // The planned path after the burn, sampled finely: the point on it nearest the vessel, and
        // how the path moves there, less how the vessel does.
        var now = flight.WorldTime;
        var plan = PlanOf(chain, now);
        var after = plan.Legs[plan.NodeLeg(0)!.Value + 1];
        var (best, nearest) = (float.MaxValue, default(Vector3));
        for (var t = chain.TimeOf(0); t < now + 30; t += 0.001)
        {
            var (at, moving) = after.Orbit.StateAfter(t - after.Start);
            var d = Vector3.Distance(at, satellite.CentreOfMass);
            if (d < best)
                (best, nearest) = (d, moving);
        }
        var expected = nearest - Planet.AmongStars(satellite.CentreOfMass, satellite.Velocity);
        var guide = flight.Guide!.Value;
        output.WriteLine($"{now - chain.TimeOf(0):0.0} s late, {best:0.00} m off the planned path: left {guide.Left} ({guide.Left.Length():0.000} m/s), expected {expected} ({expected.Length():0.000})");
        Assert.True(Vector3.Distance(guide.Left, expected) < 0.01f, $"the guide is {Vector3.Distance(guide.Left, expected):0.0000} m/s off");
        Assert.False(guide.Matched);

        // Flown late by that, it is matched, and goes; on nearly the orbit it planned.
        var planned = plan.Last.Orbit;
        var plannedTop = planned.Distance(planned.NextApoapsis!.Value) - Planet.SeaRadius;
        var seen = new Seen();
        Assert.True(sim.Run(ByTheGuide(satellite, seen), _ => satellite.Chain is null, maxSeconds: 30), "a late burn flown by the guide should match the planned path");
        var top = TopOf(satellite);
        output.WriteLine($"delta-v left down to {seen.Lowest:0.000} m/s; planned top {plannedTop:0} m, flown {top:0} m");
        Assert.True(seen.MatchedLit);
        Assert.InRange(top, plannedTop - 50, plannedTop + 50);
    }

    /// <summary>
    /// What a late burn still needs, worked out by brute force: <paramref name="chain"/>'s planned
    /// path after its first burn, sampled every millisecond over a whole lap of it from the node,
    /// its point nearest <paramref name="v"/>, how the path moves there less how the vessel does.
    /// </summary>
    /// <remarks>
    /// Kilometres off the path, the distance is too flat near its least for a float's millimetres
    /// to find it to better than a hundredth of a second (a few hundredths of a metre a second of
    /// the path's speed), so the sampled least is narrowed down by bisecting where the way to the
    /// vessel turns from ahead of the path's motion to behind it.
    /// </remarks>
    private static (Vector3 Left, float Off) NearestByBruteForce(ManoeuvreChain chain, Vessel v, double now)
    {
        var plan = PlanOf(chain, now);
        var after = plan.Legs[plan.NodeLeg(0)!.Value + 1];
        var place = v.CentreOfMass;
        var (best, nearest) = (float.MaxValue, 0.0);
        for (var t = chain.TimeOf(0); t < chain.TimeOf(0) + after.Orbit.Period; t += 0.001)
        {
            var d = Vector3.Distance(after.Orbit.StateAfter(t - after.Start).Position, place);
            if (d < best)
                (best, nearest) = (d, t);
        }
        double Ahead(double t) => after.Orbit.StateAfter(t - after.Start) is var (p, u) ? Vector3.Dot(place - p, u) : 0;
        var (lo, hi) = (nearest - 0.5, nearest + 0.5);
        if (Ahead(lo) > 0 && Ahead(hi) < 0)
            for (var i = 0; i < 40; i++)
                (lo, hi) = Ahead((lo + hi) / 2) > 0 ? ((lo + hi) / 2, hi) : (lo, (lo + hi) / 2);
        var (_, moving) = after.Orbit.StateAfter((lo + hi) / 2 - after.Start);
        return (moving - Planet.AmongStars(v.CentreOfMass, v.Velocity), best);
    }

    /// <summary>One node of <paramref name="nodes"/> first, anchored on <paramref name="v"/>'s own orbit <paramref name="seconds"/> before now (at its time: its gap from there).</summary>
    private static ManoeuvreChain Before(Vessel v, double now, double seconds, params ManoeuvreNode[] nodes) =>
        ManoeuvreChain.AnchoredOn(v, now - seconds, [.. nodes], now);

    [Theory]
    [InlineData(0.3)]
    [InlineData(1.25)] // more than a lap late, a quarter of a lap past the node's place
    [InlineData(1.5)]
    [InlineData(1.8)]
    public void A_node_more_than_a_lap_late_counts_against_the_nearest_point_whatever_its_phase(double laps)
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var lap = new Orbit(GroundBody.Planet, satellite.CentreOfMass, Planet.AmongStars(satellite.CentreOfMass, satellite.Velocity)).Period;
        sim.Step(new FlightInput(Plan: Before(satellite, flight.WorldTime, laps * lap, new ManoeuvreNode(0, Prograde: 10f))));
        var chain = satellite.Chain!;
        var now = flight.WorldTime;
        var (expected, off) = NearestByBruteForce(chain, satellite, now);
        var guide = flight.Guide!.Value;
        output.WriteLine($"{(now - chain.TimeOf(0)) / lap:0.00} laps late, {off:0.0} m off the planned path: left {guide.Left.Length():0.000} m/s, expected {expected.Length():0.000}");
        Assert.True(Vector3.Distance(guide.Left, expected) < 0.01f, $"the guide is {Vector3.Distance(guide.Left, expected):0.0000} m/s off");
        Assert.False(guide.Matched);
        Assert.Equal(chain, satellite.Chain);
    }

    /// <summary>
    /// As a pilot would, steering by the guide: the nose held along the delta-v left, the engine lit
    /// once it is lined up (half the burn's time before the node, or at once past it) and cut once
    /// it is gone or goes past.
    /// </summary>
    private static Pilot AlongTheGuide(Vessel vessel, Seen seen) => (f, _) =>
    {
        if (vessel.Guide(f.WorldTime) is not { } guide)
            return default;
        seen.Lowest = MathF.Min(seen.Lowest, guide.Left.Length());
        Vector3? aim = guide.Left.Length() > 0.01f ? Vector3.Normalize(guide.Left) : null;
        if (f.EngineLit)
        {
            seen.MatchedLit |= guide.Matched;
            if (guide.Overshot || guide.Left.Length() < 0.05f)
            {
                seen.Cut ??= f.WorldTime;
                return new FlightInput(CutThrottle: true);
            }
            return new FlightInput(Hold: aim);
        }
        if (seen.Cut is not null || aim is not { } along)
            return default;
        var lined = Vector3.Dot(vessel.Up, along) > MathF.Cos(float.DegreesToRadians(2f));
        return new FlightInput(Hold: along, FullThrottle: lined && guide.Seconds <= guide.BurnSeconds / 2);
    };

    [Fact]
    public void A_large_burn_flown_well_late_is_guided_by_the_nearest_point_and_matches()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var node = new ManoeuvreNode(0, Prograde: 25f, Radial: -10f);
        sim.Step(new FlightInput(Plan: Before(satellite, flight.WorldTime, 90, node)));
        var chain = satellite.Chain!;
        Assert.False(flight.Guide!.Value.Short);

        // Late, and part way through a burn flown late by the guide: it is the nearest point's.
        var seen = new Seen();
        var pilot = AlongTheGuide(satellite, seen);
        foreach (var share in new[] { 1f, 0.5f })
        {
            Assert.True(sim.Run(pilot, f => f.Guide!.Value.Left.Length() < share * node.Size, maxSeconds: 30));
            var now = flight.WorldTime;
            var (expected, off) = NearestByBruteForce(chain, satellite, now);
            var guide = flight.Guide!.Value;
            output.WriteLine($"{now - chain.TimeOf(0):0.0} s late, {off:0.0} m off the planned path: left {guide.Left.Length():0.000} m/s, expected {expected.Length():0.000}");
            Assert.True(Vector3.Distance(guide.Left, expected) < 0.01f, $"the guide is {Vector3.Distance(guide.Left, expected):0.0000} m/s off");
        }

        Assert.True(sim.Run(pilot, _ => satellite.Chain is null, maxSeconds: 60), "flown by the guide, it should match the planned path and go");
        output.WriteLine($"delta-v left down to {seen.Lowest:0.000} m/s, cut {seen.Cut - chain.TimeOf(0):0.0} s after its time");
        Assert.True(seen.MatchedLit);
        Assert.True(seen.Lowest < node.MatchTolerance);
    }

    [Fact]
    public void An_early_burn_cut_within_tolerance_lets_the_node_go_before_its_time()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 30, new ManoeuvreNode(0, Prograde: RaiseFrom(9000f, 10000f)));
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Prograde));
        // The whole burn well before its time.
        double? cut = null, gone = null;
        Assert.True(sim.Run((f, _) =>
        {
            var guide = f.Guide!.Value;
            if (f.EngineLit)
            {
                if (guide.Left.Length() >= 0.05f && !guide.Overshot)
                    return default;
                Assert.True(guide.Matched);
                cut ??= f.WorldTime;
                return new FlightInput(CutThrottle: true);
            }
            return cut is null && guide.Seconds <= guide.BurnSeconds + 5 ? new FlightInput(FullThrottle: true) : default;
        }, f =>
        {
            gone = f.WorldTime;
            return satellite.Chain is null;
        }, maxSeconds: 30), "matched and cut, it should go");
        output.WriteLine($"cut {chain.TimeOf(0) - cut:0.00} s before its time, gone {chain.TimeOf(0) - gone:0.00} s before");
        Assert.True(gone < chain.TimeOf(0) - 2, "it should go as it is cut, before its time");
    }

    [Fact]
    public void A_fresh_node_before_its_time_is_never_matched()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        // Less than the least tolerance: unburned, only its time can match it.
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 10, new ManoeuvreNode(0, Prograde: 0.5f));
        sim.Step(new FlightInput(Plan: chain));
        Assert.True(sim.Run((f, _) =>
        {
            Assert.False(f.Guide!.Value.Matched);
            Assert.Equal(chain, satellite.Chain);
            return default;
        }, f => f.Guide!.Value.Seconds < 0.1, maxSeconds: 10));
        // From its time on its path already matches the one it plans (half a metre a second off),
        // so it goes then, the engine off.
        Assert.True(sim.Run((_, _) => default, _ => satellite.Chain is null, maxSeconds: 0.5));
    }

    [Fact]
    public void Matched_holds_while_lit_until_half_as_much_again_is_left()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var node = new ManoeuvreNode(0, Prograde: 10f);
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 15, node);
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Prograde));
        sim.Run((_, _) => default, f => f.Guide!.Value.Seconds < f.Guide!.Value.BurnSeconds / 2, maxSeconds: 15);

        // Burned on past it until well over: each frame lit, what is left and whether it is matched.
        var frames = new List<(float Left, bool Matched)>();
        Assert.True(sim.Run((f, _) =>
        {
            if (f.EngineLit)
                frames.Add((f.Guide!.Value.Left.Length(), f.Guide!.Value.Matched));
            return new FlightInput(FullThrottle: true);
        }, _ => frames.Count > 0 && frames[^1].Matched is false && frames.Any(x => x.Matched), maxSeconds: 10));
        var tolerance = node.MatchTolerance;
        var first = frames.FindIndex(x => x.Matched);
        output.WriteLine($"matched from {frames[first].Left:0.000} m/s, held to {frames.Where(x => x.Matched).Max(x => x.Left):0.000}, let go at {frames[^1].Left:0.000} (tolerance {tolerance})");
        Assert.True(frames[first].Left < tolerance, "it is matched only once within the tolerance");
        Assert.All(frames.Take(first), x => Assert.True(x.Left >= tolerance));
        var after = frames.Skip(first).ToList();
        Assert.Contains(after, x => x.Matched && x.Left > tolerance); // held past it, overshooting
        Assert.All(after, x => Assert.Equal(x.Left < Scene.NodeMatchHold * tolerance, x.Matched));

        // Cut once let go: not matched, so it stays.
        sim.Run((_, _) => new FlightInput(CutThrottle: true), _ => false, maxSeconds: 1);
        Assert.Equal(chain, satellite.Chain);
        Assert.False(flight.Guide!.Value.Matched);
    }

    [Fact]
    public void A_cut_while_the_hold_shows_it_matched_lets_the_node_go()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var node = new ManoeuvreNode(0, Prograde: 10f);
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 15, node);
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Prograde));
        sim.Run((_, _) => default, f => f.Guide!.Value.Seconds < f.Guide!.Value.BurnSeconds / 2, maxSeconds: 15);

        // Burned past it, and cut while it is shown matched only by the hold.
        float? cutAt = null;
        Assert.True(sim.Run((f, _) =>
        {
            if (!f.EngineLit)
                return cutAt is null ? new FlightInput(FullThrottle: true) : default;
            var guide = f.Guide!.Value;
            if (!guide.Matched || guide.Left.Length() <= node.MatchTolerance * 1.2f)
                return default;
            cutAt ??= guide.Left.Length();
            return new FlightInput(CutThrottle: true);
        }, _ => satellite.Chain is null, maxSeconds: 10), "cut while shown matched, it should go");
        output.WriteLine($"cut with {cutAt:0.000} m/s left (tolerance {node.MatchTolerance}), shown matched");
        Assert.InRange(cutAt!.Value, node.MatchTolerance, Scene.NodeMatchHold * node.MatchTolerance);
    }

    [Fact]
    public void In_the_air_drag_bringing_it_within_tolerance_lets_the_node_go()
    {
        // Coasting in the air with the engine off, a little short of a node planned a little slower
        // than it is: the air slows it into the tolerance, with nothing else to judge it by.
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Time = 0.0, Vessels = [InOrbit.UpperStage(0, 2500f, 1f)], NextVesselId = 1, Flights = 1 });
        var v = sim.Flight.WorldVessels.Single(w => w.Id == 0);
        sim.Step(new FlightInput(SwitchTo: v));
        var now = sim.Flight.WorldTime;
        sim.Step(new FlightInput(Plan: ManoeuvreChain.AnchoredOn(v, now, [new ManoeuvreNode(0, Prograde: -1.6f)], now)));
        Assert.False(v.Body.AboveAir(v.CentreOfMass));
        var start = v.Guide(sim.Flight.WorldTime)!.Value;
        Assert.False(start.Matched);
        Assert.False(sim.Flight.EngineLit);

        var last = start.Left.Length();
        Assert.True(sim.Run((f, _) =>
        {
            last = v.Guide(f.WorldTime)?.Left.Length() ?? last;
            return default;
        }, _ => v.Chain is null, maxSeconds: 30), $"slowed by the air into the tolerance, it should go (left {last:0.000} m/s)");
        output.WriteLine($"left {start.Left.Length():0.000} m/s at first, {last:0.000} as it went, {sim.Flight.WorldTime - now:0.0} s on");
        Assert.True(last < Scene.NodeMatchMin + 0.05f); // (its tolerance: 1.6 m/s is under the least share)
    }

    [Fact]
    public void A_node_matched_after_the_next_ones_time_leaves_the_next_late_on_the_present_path()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var next = new ManoeuvreNode(20, Radial: 5f);
        sim.Step(new FlightInput(Plan: Before(satellite, flight.WorldTime, 40, new ManoeuvreNode(0, Prograde: 10f), next)));
        var chain = satellite.Chain!;
        Assert.True(chain.TimeOf(1) < flight.WorldTime - 15);

        var seen = new Seen();
        Assert.True(sim.Run(AlongTheGuide(satellite, seen), _ => satellite.Chain!.Nodes.Length < 2, maxSeconds: 30), "flown late by the guide, the first should go");
        Assert.True(seen.MatchedLit);
        sim.Run((_, _) => default, _ => false, maxSeconds: 3);

        // The next is at its time, long past, on the path the vessel is on now; not matched, it
        // stays, and its guide is its burn measured late.
        var now = flight.WorldTime;
        var rest = satellite.Chain!;
        Assert.Equal(next with { Gap = 0 }, Assert.Single(rest.Nodes));
        Assert.Equal(chain.TimeOf(1), rest.TimeOf(0), 9);
        var off = Vector3.Distance(rest.StartAt(now).Position, OnItsOrbit(satellite, rest.TimeOf(0) - now));
        var guide = flight.Guide!.Value;
        var (expected, _) = NearestByBruteForce(rest, satellite, now);
        output.WriteLine($"the next {now - rest.TimeOf(0):0.0} s late, anchored {off:0.00} m from its orbit now; left {guide.Left.Length():0.000} m/s, expected {expected.Length():0.000}");
        Assert.True(off < 5f); // (anchored as a step began, with its rules at the step's end time)
        Assert.False(guide.Matched);
        Assert.InRange(guide.Left.Length(), 4f, 15f);
        Assert.True(Vector3.Distance(guide.Left, expected) < 0.01f);
    }

    [Fact]
    public void The_late_guide_allocates_nothing_once_warm()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Plan: Before(satellite, flight.WorldTime, 30, new ManoeuvreNode(0, Prograde: 10f))));
        var now = flight.WorldTime;
        for (var i = 0; i < 3; i++)
            _ = satellite.Guide(now + i);
        Assert.Equal(0, PatchedPathTests.Allocated(() =>
        {
            for (var i = 0; i < 10; i++)
                _ = satellite.Guide(now + i);
        }));
    }

    [Fact]
    public void Under_formula_the_manoeuvre_hold_steers_nothing()
    {
        // No node is flown under formula: its hold waits for its pilot, its nose where it was.
        var far = UnwatchedTests.Satellite(1, 10000f, 0f, 240f);
        var chain = new ManoeuvreChain(new ChainAnchor(GroundBody.Planet.Name, 0.0, far.Position, GroundBody.Planet.AmongStars(far.Position, far.Velocity)),
            [new ManoeuvreNode(200, Radial: 30f)]);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState
        {
            Time = 0.0, NextVesselId = 2, Flights = 1, Vessels = [far with { Chain = chain, Hold = HoldMode.Manoeuvre }],
        });
        var satellite = sim.Flight.WorldVessels.Single(v => v.Id == 1);
        sim.Step(default);
        Assert.True(satellite.OnFormula);
        var aim = satellite.Aim;
        sim.Run((_, _) => default, _ => false, maxSeconds: 3);
        Assert.True(satellite.OnFormula);
        Assert.Equal(HoldMode.Manoeuvre, satellite.Hold);
        var turned = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(aim), Vector3.Normalize(satellite.Aim)), -1f, 1f)));
        Assert.True(turned < 1f, $"its nose turned {turned:0.0}° by the hold");
    }

    [Fact]
    public void A_settled_vessel_s_node_is_judged_only_as_something_changes()
    {
        // In flight, a touch is judged every step it lasts and the step after; landed or afloat
        // only as it begins or ends; the engine cut, the chain changed, the air, as ever.
        Assert.True(Vessel.Rejudged(justCut: false, changed: false, touching: true, wasTouching: true, settled: false, inAir: false));
        Assert.True(Vessel.Rejudged(false, false, touching: false, wasTouching: true, settled: false, inAir: false)); // the step after
        Assert.False(Vessel.Rejudged(false, false, touching: false, wasTouching: false, settled: false, inAir: false)); // coasting above the air
        Assert.True(Vessel.Rejudged(false, false, false, false, settled: false, inAir: true));
        Assert.False(Vessel.Rejudged(false, false, touching: true, wasTouching: true, settled: true, inAir: true)); // landed, still
        Assert.True(Vessel.Rejudged(false, false, touching: true, wasTouching: false, settled: true, inAir: true)); // a touch begun
        Assert.True(Vessel.Rejudged(false, false, touching: false, wasTouching: true, settled: true, inAir: true)); // and ended
        Assert.True(Vessel.Rejudged(justCut: true, false, true, true, settled: true, inAir: true));
        Assert.True(Vessel.Rejudged(false, changed: true, true, true, settled: true, inAir: true));
    }

    [Fact]
    public void Anchoring_a_chain_allocates_only_the_chain()
    {
        // As dragging the first node does every frame: the chain made is all it allocates.
        using var sim = Satellite(out var satellite);
        var now = sim.Flight.WorldTime;
        ImmutableArray<ManoeuvreNode> nodes = [new ManoeuvreNode(0, Prograde: 10f)];
        for (var i = 0; i < 3; i++)
            _ = ManoeuvreChain.AnchoredOn(satellite, now + 100 + i, nodes, now);
        var one = PatchedPathTests.Allocated(() => _ = new ManoeuvreChain(default, nodes));
        Assert.Equal(10 * one, PatchedPathTests.Allocated(() =>
        {
            for (var i = 0; i < 10; i++)
                _ = ManoeuvreChain.AnchoredOn(satellite, now + 100 + i, nodes, now);
        }));
    }

    [Fact]
    public void The_guide_counts_the_burn_down_and_says_when_it_goes_past()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Plan: Chain(satellite, flight.WorldTime, flight.WorldTime + 5, new ManoeuvreNode(0, Prograde: 2f)), ChooseHold: HoldMode.Prograde));
        sim.Run((_, _) => default, f => f.Guide!.Value.Seconds < 0.5, maxSeconds: 10);
        var before = flight.Guide!.Value;
        Assert.False(before.Overshot);
        Assert.True(before.BurnSeconds is > 0.4f and < 1f, $"2 m/s at about 2.9 m/s² takes {before.BurnSeconds:0.00} s");

        // Two seconds at full throttle is about 6 m/s: well past the 2 asked for.
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 0.3);
        var during = flight.Guide!.Value;
        Assert.True(during.Left.Length() < before.Left.Length() - 0.5f, $"left {during.Left.Length():0.00} m/s, from {before.Left.Length():0.00}");
        Assert.False(during.Overshot);
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 1.7);
        var after = flight.Guide!.Value;
        output.WriteLine($"left {before.Left.Length():0.00}, then {during.Left.Length():0.00}, then {after.Left.Length():0.00} m/s, overshot {after.Overshot}");
        Assert.True(after.Overshot);
        Assert.InRange(after.Left.Length(), 3f, 5f);
        Assert.Single(satellite.Chain!.Nodes); // the engine is still lit: it stays until that stops
    }

    [Fact]
    public void A_node_stays_where_it_was_set_when_the_path_changes_before_it()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var set = flight.WorldTime;
        var chain = Chain(satellite, set, set + 120, new ManoeuvreNode(0, Prograde: 20f));
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Prograde));
        var before = PlanOf(satellite.Chain!, set);
        var node = before.NodeAt(0)!.Value.Position;
        var path = before.Last.Orbit.StateAfter(300).Position;
        var ownBefore = OnItsOrbit(satellite, chain.TimeOf(0) - set);

        // Coasting, planned later, the node is where the vessel's own orbit has it then, as the
        // physics has flown it: the anchor's turn into the later frame is the ground's own.
        sim.Run((_, _) => default, _ => false, maxSeconds: 5);
        var coasted = flight.WorldTime;
        var onOrbit = Vector3.Distance(PlanOf(satellite.Chain!, coasted).NodeAt(0)!.Value.Position, OnItsOrbit(satellite, chain.TimeOf(0) - coasted));
        output.WriteLine($"{coasted - set:0.0} s on, the node is {onOrbit * 1000:0.0} mm from its orbit as flown");
        Assert.True(onOrbit < 0.1f);

        // A little prograde, well before the node: its own path is somewhere else now.
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 1);
        sim.Run((_, _) => new FlightInput(CutThrottle: true), _ => false, maxSeconds: 1);
        var now = flight.WorldTime;
        Assert.True(now < chain.TimeOf(0) - 100);
        var ownAfter = Planet.Turned(OnItsOrbit(satellite, chain.TimeOf(0) - now), now - set); // as the ground was when it was set
        output.WriteLine($"its own path at the node's time moved {Vector3.Distance(ownBefore, ownAfter):0.0} m");
        Assert.True(Vector3.Distance(ownBefore, ownAfter) > 10f, "the burn should have changed its own path");

        // The node and the path it plans are where they were: the chain is as it was set, and its
        // plan depends on it and the time alone.
        Assert.Equal(chain, satellite.Chain);
        var after = PlanOf(satellite.Chain!, set);
        output.WriteLine($"the node moved {Vector3.Distance(node, after.NodeAt(0)!.Value.Position) * 1000:0.000} mm, its path {Vector3.Distance(path, after.Last.Orbit.StateAfter(300).Position) * 1000:0.000} mm");
        Assert.True(Vector3.Distance(node, after.NodeAt(0)!.Value.Position) < 1e-3f);
        Assert.True(Vector3.Distance(path, after.Last.Orbit.StateAfter(300).Position) < 1e-3f);
        // Planned now, it is the same place among the stars (turned on by the ground's turn since).
        var later = PlanOf(satellite.Chain!, now);
        var turnedBack = Planet.Turned(later.NodeAt(0)!.Value.Position, now - set);
        output.WriteLine($"planned now, the node is {Vector3.Distance(node, turnedBack) * 1000:0.0} mm from where it was");
        Assert.True(Vector3.Distance(node, turnedBack) < 0.02f);
    }

    [Fact]
    public void Changing_a_node_redoes_the_chain_after_it_keeping_their_gaps_and_amounts()
    {
        using var sim = Satellite(out var satellite);
        var now = sim.Flight.WorldTime;
        var chain = Three(satellite, now);
        var changed = chain.WithNode(1, chain.Nodes[1] with { Prograde = 10f });
        Assert.Equal(chain.Anchor, changed.Anchor);
        Assert.Equal(chain.Nodes[0], changed.Nodes[0]);
        Assert.Equal(chain.Nodes[2], changed.Nodes[2]);
        Assert.Equal(chain.TimeOf(2), changed.TimeOf(2));

        var (before, after) = (PlanOf(chain, now), PlanOf(changed, now));
        // The nodes up to it are where they were; the one after it is at the same time, on its new path.
        Assert.True(Vector3.Distance(before.NodeAt(0)!.Value.Position, after.NodeAt(0)!.Value.Position) < 1e-3f);
        Assert.True(Vector3.Distance(before.NodeAt(1)!.Value.Position, after.NodeAt(1)!.Value.Position) < 1e-3f);
        var moved = Vector3.Distance(before.NodeAt(2)!.Value.Position, after.NodeAt(2)!.Value.Position);
        output.WriteLine($"the third node moved {moved:0} m");
        Assert.True(moved > 10f, "the third node should be on the second's new path");
        var leg = after.Legs[after.NodeLeg(2)!.Value];
        Assert.Equal(changed.TimeOf(2), leg.End, 6);
        // Its burn is its own amounts, measured on that path.
        var (at, velocity) = after.NodeAt(2)!.Value;
        var (_, burned) = after.Legs[after.NodeLeg(2)!.Value + 1].Orbit.StateAfter(0);
        Assert.True(Vector3.Distance(burned - velocity, changed.Nodes[2].DeltaV(leg.Body, at, velocity)) < 1e-3f);
    }

    [Fact]
    public void Moving_a_node_moves_the_later_ones_with_it()
    {
        using var sim = Satellite(out var satellite);
        var now = sim.Flight.WorldTime;
        var chain = Three(satellite, now);
        var moved = chain.MoveTo(1, chain.TimeOf(1) + 50, satellite, now);
        Assert.Equal(chain.Anchor, moved.Anchor);
        Assert.Equal(chain.TimeOf(0), moved.TimeOf(0));
        Assert.Equal(chain.TimeOf(1) + 50, moved.TimeOf(1), 9);
        Assert.Equal(chain.TimeOf(2) + 50, moved.TimeOf(2), 9);
        Assert.Equal(chain.Nodes[1] with { Gap = 350 }, moved.Nodes[1]);
        Assert.Equal(chain.Nodes[2], moved.Nodes[2]);
    }

    [Fact]
    public void A_node_cannot_be_moved_before_the_one_before_it()
    {
        using var sim = Satellite(out var satellite);
        var now = sim.Flight.WorldTime;
        var chain = Three(satellite, now);
        var moved = chain.MoveTo(1, chain.TimeOf(0) - 100, satellite, now);
        Assert.Equal(chain.TimeOf(0) + ManoeuvreChain.MinimumGap, moved.TimeOf(1), 9);
        Assert.Equal(moved.TimeOf(1) + chain.Nodes[2].Gap, moved.TimeOf(2), 9);
        // Nor the first before now.
        var first = chain.MoveTo(0, now - 100, satellite, now);
        Assert.Equal(now + ManoeuvreChain.MinimumGap, first.TimeOf(0), 9);
    }

    [Fact]
    public void Deleting_a_middle_node_joins_its_neighbours()
    {
        using var sim = Satellite(out var satellite);
        var now = sim.Flight.WorldTime;
        var chain = Three(satellite, now);
        var joined = chain.Delete(1, satellite, now);
        Assert.Equal(2, joined.Nodes.Length);
        Assert.Equal(chain.Anchor, joined.Anchor);
        Assert.Equal(chain.Nodes[0], joined.Nodes[0]);
        Assert.Equal(chain.TimeOf(2), joined.TimeOf(1), 9);
        Assert.Equal(chain.TimeOf(2) - chain.TimeOf(0), joined.Nodes[1].Gap, 9);
        Assert.Equal(chain.Nodes[2] with { Gap = joined.Nodes[1].Gap }, joined.Nodes[1]);
        // It is on the first's path now: where that one alone plans it at its time.
        var firstAlone = PlanOf(chain with { Nodes = [chain.Nodes[0]] }, now);
        var onFirst = firstAlone.Last.Orbit.StateAfter(chain.TimeOf(2) - firstAlone.Last.Start).Position;
        Assert.True(Vector3.Distance(onFirst, PlanOf(joined, now).NodeAt(1)!.Value.Position) < 1e-3f);
    }

    [Fact]
    public void Deleting_the_first_node_anchors_the_second_on_the_vessels_path()
    {
        using var sim = Satellite(out var satellite);
        var now = sim.Flight.WorldTime;
        var chain = Three(satellite, now);
        var rest = chain.Delete(0, satellite, now);
        Assert.Equal(2, rest.Nodes.Length);
        Assert.Equal(chain.TimeOf(1), rest.Anchor.Time, 9);
        Assert.Equal(chain.TimeOf(1), rest.TimeOf(0), 9);
        Assert.Equal(chain.TimeOf(2), rest.TimeOf(1), 9);
        Assert.Equal(chain.Nodes[1] with { Gap = 0 }, rest.Nodes[0]);
        Assert.Equal(chain.Nodes[2], rest.Nodes[1]);
        // Its anchor is where the vessel's own orbit has it then, not where the first node's burn would have.
        var anchored = rest.StartAt(now).Position;
        var own = OnItsOrbit(satellite, chain.TimeOf(1) - now);
        var planned = PlanOf(chain, now).NodeAt(1)!.Value.Position;
        output.WriteLine($"anchored {Vector3.Distance(anchored, own) * 1000:0.0} mm from its own orbit, {Vector3.Distance(anchored, planned):0} m from the first's planned path");
        Assert.True(Vector3.Distance(anchored, own) < 0.02f);
        Assert.True(Vector3.Distance(anchored, planned) > 100f);
    }

    [Fact]
    public void Moving_the_first_node_snaps_it_onto_the_vessels_present_orbit()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var set = flight.WorldTime;
        sim.Step(new FlightInput(Plan: Chain(satellite, set, set + 200, new ManoeuvreNode(0, Prograde: 5f), new ManoeuvreNode(100, Radial: 1f)), ChooseHold: HoldMode.Prograde));
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 1);
        sim.Run((_, _) => new FlightInput(CutThrottle: true), _ => false, maxSeconds: 1);
        var now = flight.WorldTime;
        var chain = satellite.Chain!;
        var to = chain.TimeOf(0) + 20;
        var moved = chain.MoveTo(0, to, satellite, now);
        Assert.Equal(to, moved.TimeOf(0), 9);
        Assert.Equal(chain.TimeOf(1) + 20, moved.TimeOf(1), 9);
        Assert.Equal(chain.Nodes[0] with { Gap = 0 }, moved.Nodes[0]);
        Assert.Equal(chain.Nodes[1], moved.Nodes[1]);
        // Onto the orbit it is on now, off the one it was set on.
        var anchored = moved.StartAt(now).Position;
        var own = OnItsOrbit(satellite, to - now);
        var (position, velocity) = chain.StartAt(now);
        var wasOn = new Orbit(GroundBody.Planet, position, velocity, from: chain.Anchor.Time - now).StateAfter(20).Position;
        output.WriteLine($"anchored {Vector3.Distance(anchored, own) * 1000:0.0} mm from its orbit now, {Vector3.Distance(anchored, wasOn):0.0} m from the one it was set on");
        Assert.True(Vector3.Distance(anchored, own) < 0.02f);
        Assert.True(Vector3.Distance(anchored, wasOn) > 10f);
    }

    [Fact]
    public void A_chain_anchored_at_the_moon_is_planned_round_the_moon()
    {
        var moon = GroundBody.Moon;
        var state = InOrbit.Round(moon, 0, 400f);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Time = 0.0, Vessels = [state], NextVesselId = 1, Flights = 1 });
        var v = sim.Flight.WorldVessels.Single(w => w.Id == 0);
        sim.Step(new FlightInput(SwitchTo: v));
        sim.Step(default);
        Assert.Same(moon, v.Body);
        var now = sim.Flight.WorldTime;
        var chain = Chain(v, now, now + 60, new ManoeuvreNode(0, Prograde: 5f));
        Assert.Equal(moon.Name, chain.Anchor.Body);
        Assert.Same(moon, chain.Body);

        var plan = PlanOf(chain, now);
        Assert.All(plan.Legs, leg => Assert.Same(moon, leg.Body));
        var (at, before) = plan.NodeAt(0)!.Value;
        var height = Vector3.Distance(at, moon.Centre) - moon.SeaRadius;
        output.WriteLine($"the node is {height:0.0} m over the moon's datum");
        Assert.InRange(height, moon.OrbitFloor + 390f, moon.OrbitFloor + 410f);
        Assert.True(Vector3.Distance(at, OnItsOrbitRound(moon, v, 60)) < 0.02f, "the node should be on its orbit round the moon");
        var (_, after) = plan.Legs[plan.NodeLeg(0)!.Value + 1].Orbit.StateAfter(0);
        Assert.True(Vector3.Distance(after - before, Manoeuvres.Frame(moon, at, before).Prograde * 5f) < 1e-3f);

        // Set on it, its guide is that burn.
        sim.Step(new FlightInput(Plan: chain));
        Assert.InRange(sim.Flight.Guide!.Value.Left.Length(), 4.99f, 5.01f);

        static Vector3 OnItsOrbitRound(GroundBody body, Vessel v, double seconds) =>
            new Orbit(body, v.CentreOfMass, body.AmongStars(v.CentreOfMass, v.Velocity)).StateAfter(seconds).Position;
    }

    [Fact]
    public void When_a_node_goes_the_next_snaps_onto_the_present_orbit()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var (later, last) = (new ManoeuvreNode(198, Radial: 1f), new ManoeuvreNode(150, Normal: -2f));
        var chain = Chain(satellite, flight.WorldTime, flight.WorldTime + 15, new ManoeuvreNode(0, Prograde: 3f), later, last);
        sim.Step(new FlightInput(Plan: chain, ChooseHold: HoldMode.Prograde)); // turned prograde well before it
        Assert.Equal(3, satellite.Chain!.Nodes.Length);
        var seen = new Seen();
        Assert.True(sim.Run(ByTheGuide(satellite, seen), _ => satellite.Chain!.Nodes.Length < 3, maxSeconds: 30), "the first should go once matched and cut");
        Assert.True(seen.MatchedLit);

        // The first has gone; the second is at its time (its gap after the first's) with its
        // amounts, anchored on the path the vessel is on now, and the third after it as it was.
        var now = flight.WorldTime;
        var rest = satellite.Chain!;
        Assert.Equal(2, rest.Nodes.Length);
        Assert.Equal(later with { Gap = 0 }, rest.Nodes[0]);
        Assert.Equal(last, rest.Nodes[1]);
        Assert.Equal(chain.TimeOf(0) + later.Gap, rest.TimeOf(0), 9);
        Assert.Equal(chain.TimeOf(2), rest.TimeOf(1), 9);
        var off = Vector3.Distance(rest.StartAt(now).Position, OnItsOrbit(satellite, rest.TimeOf(0) - now));
        output.WriteLine($"anchored {off:0.000} m from its orbit now");
        Assert.True(off < 5f); // (taken as the step began, its rules run at the step's end time: a step's travel at most)
        // The chain is redone from there: the third is on the second's path from its new anchor.
        var plan = PlanOf(rest, now);
        var onAnchor = Vector3.Distance(plan.NodeAt(0)!.Value.Position, rest.StartAt(now).Position);
        output.WriteLine($"the second planned {onAnchor * 1000:0.0} mm from its anchor");
        Assert.True(onAnchor < 0.02f);
        Assert.Equal(rest.TimeOf(1), plan.Legs[plan.NodeLeg(1)!.Value].End, 6);
    }

    [Theory]
    [InlineData(0f)]   // straight down: it falls back to the ground
    [InlineData(0.5f)] // a hop, down again within a lap
    [InlineData(1.6f)] // escaping
    public void A_chain_anchored_past_where_the_path_ends_is_anchored_where_it_ends_and_saves(float share)
    {
        var state = InOrbit.UpperStage(0, 9000f, share);
        var (centre, stars) = (state.CentreOfMass, InOrbit.Stars(state));
        var chain = ManoeuvreChain.AnchoredOn(GroundBody.Planet, centre, stars, 1e7, [new ManoeuvreNode(0, Prograde: 5f)], 0);
        var anchor = chain.Anchor;
        output.WriteLine($"share {share}: anchored at {anchor.Time:0.0} s, {anchor.Position}, {anchor.StarsVelocity}");
        Assert.True(double.IsFinite(anchor.Time) && anchor.Time < 1e7, "anchored no later than where the path ends");
        Assert.True(Finite(anchor.Position) && Finite(anchor.StarsVelocity), "the anchor should be a state, not NaN or infinity");
        Assert.True(PlanOf(chain, 0).Legs.All(leg => Finite(leg.Orbit.StateAfter(0).Position)));

        // Saved with a vessel, it loads back as it was.
        var directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorldStore(directory);
            WorldState world;
            using (var sim = new Simulation(Mission.FreePlay, world: new WorldState { Time = 0.0, Vessels = [InOrbit.UpperStage(0, 9000f, 1f)], NextVesselId = 1, Flights = 1 }))
                world = sim.Flight.Capture() with { Id = "ends", Name = "Ends" };
            store.Save(world with { Vessels = [world.Vessels[0] with { Chain = chain }] });
            Assert.Equal(chain, store.Load("ends").Vessels[0].Chain);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    [Fact]
    public void A_node_is_moved_only_once_the_pointer_has_left_where_it_was_grabbed()
    {
        var grabbed = new Vector2(400f, 300f);
        Assert.False(NodeEditor.Dragged(grabbed, grabbed));
        Assert.False(NodeEditor.Dragged(grabbed, grabbed + new Vector2(2f, 2f)));
        Assert.True(NodeEditor.Dragged(grabbed, grabbed + new Vector2(NodeEditor.DragPixels + 0.5f, 0f)));
    }

    [Fact]
    public void Each_vessel_keeps_its_own_nodes()
    {
        using var sim = Satellite(out var first);
        var flight = sim.Flight;
        var second = flight.WorldVessels.Where(v => v.Flight == 0).ElementAt(1);
        var chain = Chain(first, flight.WorldTime, flight.WorldTime + 300, new ManoeuvreNode(0, Normal: -4f));
        sim.Step(new FlightInput(Plan: chain));
        sim.Step(new FlightInput(SwitchTo: second));
        Assert.Null(second.Chain);
        Assert.Null(flight.Guide);
        sim.Step(new FlightInput(SwitchTo: first));
        Assert.Equal(chain, first.Chain);
        Assert.NotNull(flight.Guide);
    }

    [Fact]
    public void The_readout_gives_each_nodes_world_time()
    {
        using var sim = Satellite(out var satellite);
        var now = sim.Flight.WorldTime;
        var chain = Three(satellite, now);
        sim.Step(new FlightInput(Plan: chain));
        var readings = Readout.FlightReadings(sim.Flight);
        Assert.Equal("3", readings["Nodes"]);
        Assert.Equal(string.Join(" ", Enumerable.Range(0, 3).Select(i => $"{chain.TimeOf(i):0.0}")), readings["Node times"]);
        sim.Step(new FlightInput(Plan: ManoeuvreChain.Empty));
        Assert.False(Readout.FlightReadings(sim.Flight).ContainsKey("Node times"));
    }

    [Fact]
    public void An_empty_chain_clears_the_plan()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Plan: Chain(satellite, flight.WorldTime, flight.WorldTime + 300, new ManoeuvreNode(0, Normal: -4f))));
        Assert.NotNull(satellite.Chain);
        sim.Step(new FlightInput(Plan: satellite.Chain!.Delete(0, satellite, flight.WorldTime)));
        Assert.Null(satellite.Chain);
        Assert.Null(flight.Guide);
    }

    [Fact]
    public void A_node_inserted_before_the_first_is_anchored_on_the_vessels_path()
    {
        using var sim = Satellite(out var satellite);
        var now = sim.Flight.WorldTime;
        var chain = Three(satellite, now);
        var earlier = chain.Insert(now + 40, satellite, now);
        Assert.Equal(4, earlier.Nodes.Length);
        Assert.Equal(new ManoeuvreNode(0), earlier.Nodes[0]);
        Assert.Equal(now + 40, earlier.TimeOf(0), 9);
        for (var i = 0; i < 3; i++)
            Assert.Equal(chain.TimeOf(i), earlier.TimeOf(i + 1), 9);
        Assert.True(Vector3.Distance(earlier.StartAt(now).Position, OnItsOrbit(satellite, 40)) < 0.02f);
        // Between two, it takes its place among them, the one after keeping its time.
        var between = chain.Insert(chain.TimeOf(1) + 10, satellite, now);
        Assert.Equal(chain.Anchor, between.Anchor);
        Assert.Equal(new ManoeuvreNode(10), between.Nodes[2]);
        Assert.Equal(chain.Nodes[2] with { Gap = 390 }, between.Nodes[3]);
        Assert.Equal(chain.TimeOf(2), between.TimeOf(3), 9);
        Assert.Equal(2, chain.SlotFor(chain.TimeOf(1) + 10));
    }

    [Fact]
    public void The_frame_is_prograde_radial_out_and_normal()
    {
        var at = Planet.Centre + Vector3.UnitY * (Planet.SeaRadius + 9000f);
        var east = Vector3.UnitX * Planet.CircularSpeed(9000f);
        var (prograde, radial, normal) = Manoeuvres.Frame(GroundBody.Planet, at, east);
        Assert.True(Vector3.Dot(prograde, Vector3.UnitX) > 0.9999f);
        Assert.True(Vector3.Dot(radial, Vector3.UnitY) > 0.9999f, $"radial out should be up, is {radial}");
        Assert.True(MathF.Abs(Vector3.Dot(normal, Vector3.UnitZ)) > 0.9999f);
        Assert.True(Vector3.Dot(Vector3.Cross(at - Planet.Centre, east), normal) > 0f, "normal is the way the orbit's spin points");
    }
}
