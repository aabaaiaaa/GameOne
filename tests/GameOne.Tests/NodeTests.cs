using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Manoeuvre nodes (<see cref="ManoeuvreNode"/>): the path a plan of them makes, flying one by its
/// guide (the delta-v left, counting down, and the overshoot), how they go once flown past, that a
/// later one keeps its time when an earlier burn changes the orbit, and that they are kept per vessel.
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

    [Fact]
    public void A_prograde_node_raises_the_far_side_of_the_planned_path()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var node = new ManoeuvreNode(flight.WorldTime + 100, Prograde: RaiseFrom(9000f, 10000f));
        var plan = new ManoeuvrePlan();
        plan.Update(GroundBody.Planet, satellite.CentreOfMass, Planet.AmongStars(satellite.CentreOfMass, satellite.Velocity), flight.WorldTime, [node], null);

        Assert.Equal(2, plan.Legs.Count);
        Assert.Equal(node.Time, plan.Legs[0].End, 6);
        var after = plan.Last.Orbit;
        var far = after.NextApoapsis!.Value;
        var top = after.Distance(far) - Planet.SeaRadius;
        output.WriteLine($"top of the planned path {top:0} m, {after.TimeTo(far):0} s after the node");
        Assert.InRange(top, 9990, 10010);
        // Half a lap of the new orbit on from the node: the far side is opposite where it burned.
        var (at, _) = plan.NodeAt(0)!.Value;
        Assert.True(Vector3.Dot(Vector3.Normalize(at - Planet.Centre), Vector3.Normalize(after.At(far) - Planet.Centre)) < -0.999f);
    }

    [Fact]
    public void A_node_flown_by_its_guide_gives_the_orbit_it_planned_and_then_goes()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var node = new ManoeuvreNode(flight.WorldTime + 60, Prograde: RaiseFrom(9000f, 10000f));
        sim.Step(new FlightInput(Plan: [node]));
        Assert.Single(satellite.Nodes);
        var plan = new ManoeuvrePlan();
        plan.Update(GroundBody.Planet, satellite.CentreOfMass, Planet.AmongStars(satellite.CentreOfMass, satellite.Velocity), flight.WorldTime, satellite.Nodes, null);
        var planned = plan.Last.Orbit;
        var plannedTop = planned.Distance(planned.NextApoapsis!.Value) - Planet.SeaRadius;

        // As a pilot would: point along the burn (prograde hold, here), light the engine half the
        // burn's time before the node, and cut it once the delta-v left is gone (or goes past).
        sim.Step(new FlightInput(ToggleProgradeHold: true));
        var startLeft = flight.Guide!.Value.Left.Length();
        var lowest = float.MaxValue;
        var overshot = false;
        var timeWhenGone = 0.0;
        Assert.True(sim.Run((f, _) =>
        {
            if (f.Guide is not { } guide)
                return default;
            lowest = MathF.Min(lowest, guide.Left.Length());
            overshot |= guide.Overshot;
            if (f.EngineLit)
                return guide.Overshot || guide.Left.Length() < 0.05f ? new FlightInput(CutThrottle: true) : default;
            return guide.Seconds <= guide.BurnSeconds / 2 && lowest > 0.05f ? new FlightInput(FullThrottle: true) : default;
        }, f =>
        {
            timeWhenGone = f.WorldTime;
            return satellite.Nodes.Count == 0;
        }, maxSeconds: 120), "the node should go once flown past");
        output.WriteLine($"delta-v left from {startLeft:0.00} m/s down to {lowest:0.000}; overshot: {overshot}");
        Assert.InRange(startLeft, RaiseFrom(9000f, 10000f) - 0.05f, RaiseFrom(9000f, 10000f) + 0.05f);
        Assert.True(lowest < 0.1f, $"the burn should use up the delta-v left (it got to {lowest:0.000} m/s)");

        // It went once its time was past with the engine off: no sooner (the burn, either side of
        // its time, finished half a second or so after it).
        output.WriteLine($"gone {timeWhenGone - node.Time:0.00} s after its time");
        Assert.InRange(timeWhenGone, node.Time, node.Time + 1.5);
        Assert.False(flight.EngineLit);
        Assert.Null(flight.Guide);
        var top = Orbit.OverGround(GroundBody.Planet, satellite.CentreOfMass, satellite.Velocity) is var now ? now.Distance(now.NextApoapsis!.Value) - Planet.SeaRadius : 0;
        output.WriteLine($"planned top {plannedTop:0} m, flown {top:0} m");
        Assert.InRange(top, plannedTop - 30, plannedTop + 30);
    }

    [Fact]
    public void The_guide_counts_the_burn_down_and_says_when_it_goes_past()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Plan: [new ManoeuvreNode(flight.WorldTime + 5, Prograde: 2f)], ToggleProgradeHold: true));
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
        Assert.Single(satellite.Nodes); // the engine is still lit: it stays until that stops
    }

    [Fact]
    public void A_later_node_keeps_its_time_when_an_earlier_burn_changes_the_orbit()
    {
        using var sim = Satellite(out var satellite);
        var flight = sim.Flight;
        var later = new ManoeuvreNode(flight.WorldTime + 200, Radial: 1f);
        sim.Step(new FlightInput(Plan: [new ManoeuvreNode(flight.WorldTime + 2, Prograde: 3f), later]));
        Assert.Equal(2, satellite.Nodes.Count);
        sim.Step(new FlightInput(ToggleProgradeHold: true));
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 3);
        sim.Run((_, _) => new FlightInput(CutThrottle: true), _ => false, maxSeconds: 1);
        Assert.Equal([later], satellite.Nodes); // the first, flown past, has gone; the second is as it was
    }

    [Fact]
    public void Each_vessel_keeps_its_own_nodes()
    {
        using var sim = Satellite(out var first);
        var flight = sim.Flight;
        var second = flight.WorldVessels.Where(v => v.Flight == 0).ElementAt(1);
        var node = new ManoeuvreNode(flight.WorldTime + 300, Normal: -4f);
        sim.Step(new FlightInput(Plan: [node]));
        sim.Step(new FlightInput(SwitchTo: second));
        Assert.Empty(second.Nodes);
        Assert.Null(flight.Guide);
        sim.Step(new FlightInput(SwitchTo: first));
        Assert.Equal([node], first.Nodes);
        Assert.NotNull(flight.Guide);
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
