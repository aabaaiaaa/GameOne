using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The solar system's edge (<see cref="Scene.SolarSystemEdge"/>), a sphere round the sun: a path
/// that reaches it ends there (<see cref="PathLeg.LeavesSystem"/>), a hyperbola's and a bound
/// ellipse's whose far end is past it alike, while one inside it has no such end; the distance is
/// the sun's, not the planet's; and the flown vessel's path is drawn out to it with its mark,
/// in a bounded number of points and allocating nothing.
/// </summary>
public sealed class SolarEdgeTests(ITestOutputHelper output)
{
    private static GroundBody Planet => GroundBody.Planet;
    private static readonly Vector3 Up = Vector3.UnitY, East = Vector3.UnitX;
    private const double Now = 5000.0;

    /// <summary>A point over the pad, <paramref name="height"/> above the sea.</summary>
    private static Vector3 Above(float height) => Planet.Centre + Up * (Planet.SeaRadius + height);

    /// <summary>Where <paramref name="leg"/> is at world time <paramref name="t"/>, in its body's ground frame then.</summary>
    private static Vector3Double GroundAt(in PathLeg leg, double t)
    {
        var reference = leg.Start - leg.Orbit.From;
        var p = leg.Orbit.PlaceAfter(t - leg.Start);
        var back = QuaternionDouble.FromAxisAngle(leg.Body.NorthPole, leg.Body.TurnRadians(-(t - reference)));
        return leg.Body.Centre + back.Rotate(p - leg.Body.Centre);
    }

    private static List<PathLeg> LegsOf(Vector3 position, Vector3 starsVelocity)
    {
        var legs = new List<PathLeg>(Scene.PatchedLegs);
        Patched.Legs(Planet, position, starsVelocity, Now, legs);
        return legs;
    }

    private void EndsAtTheEdge(List<PathLeg> legs)
    {
        var last = legs[^1];
        Assert.True(last.LeavesSystem, "the path should end at the edge");
        Assert.True(double.IsFinite(last.End));
        var distance = SolarSystem.FromSun(last.Body, GroundAt(last, last.End), last.End);
        output.WriteLine($"{legs.Count} legs; ends {(last.End - Now) / 86400:0.0} days on, {distance / 1e3:#,0} km from the sun");
        Assert.InRange(distance, 0.99 * Scene.SolarSystemEdge, 1.01 * Scene.SolarSystemEdge);
        // Closer than the sun's distance from the planet, so the edge is round the sun's place then.
        Assert.InRange(distance, Scene.SolarSystemEdge - 1e5, Scene.SolarSystemEdge + 1e5);
        for (var i = 0; i < legs.Count - 1; i++)
            Assert.False(legs[i].LeavesSystem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_escape_path_ends_at_the_edge(bool straightUp)
    {
        var start = Above(20000f);
        EndsAtTheEdge(LegsOf(start, (straightUp ? Up : East) * 1500f));
    }

    [Fact]
    public void An_orbit_reaching_past_the_edge_ends_there()
    {
        // Bound, at its periapsis 20 km up, its apoapsis 2e10 m out: it would come back, but the
        // edge ends it on its way out.
        var start = Above(20000f);
        double periapsis = Vector3.Distance(start, Planet.Centre), apoapsis = 2e10;
        var speed = Math.Sqrt(Planet.Mu * 2 * apoapsis / (periapsis * (periapsis + apoapsis)));
        var legs = LegsOf(start, East * (float)speed);
        Assert.True(legs[0].Orbit.Eccentricity < 1, "the orbit should be bound");
        EndsAtTheEdge(legs);
    }

    [Fact]
    public void An_orbit_inside_the_edge_has_no_edge_end()
    {
        var legs = LegsOf(Above(10000f), East * Planet.CircularSpeed(10000f));
        Assert.All(legs, leg => Assert.False(leg.LeavesSystem));
        Assert.Equal(double.PositiveInfinity, legs[^1].End);
    }

    [Fact]
    public void The_edge_is_round_the_sun_not_the_planet()
    {
        foreach (var t in new[] { 0.0, Now, 1e6, 3e7 })
            Assert.InRange(SolarSystem.FromSun(Planet, Planet.Centre, t), Scene.SunDistance - 1.0, Scene.SunDistance + 1.0);
    }

    [Fact]
    public void An_escape_path_is_drawn_to_the_edge_with_its_mark()
    {
        var upper = InOrbit.UpperStage(0, 20000f, 1500f / Planet.CircularSpeed(20000f));
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [upper], NextVesselId = 1 });
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.Single(v => v.Id == 0)));
        var paths = new Paths();
        var plan = FrameTests.PlanFor(flight);
        for (var i = 0; i < 3; i++)
            paths.Build(flight, plan);

        var mark = Assert.Single(paths.EdgeMarks);
        Assert.Equal("Leaves the solar system", mark.Label);
        Assert.Equal(paths.Line[^1], mark.At);
        // The line's points: no more than a lap's samples for each leg, however far out it goes.
        output.WriteLine($"{paths.Line.Count} points, the last {Vector3.Distance(paths.Line[^1], Planet.Centre) / 1e3:#,0} km out");
        Assert.InRange(paths.Line.Count, 2, Scene.PatchedLegs * 257);
        Assert.True(Vector3.Distance(paths.Line[^1], Planet.Centre) > 0.99 * Scene.SolarSystemEdge);

        var before = GC.GetAllocatedBytesForCurrentThread();
        paths.Build(flight, plan);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
