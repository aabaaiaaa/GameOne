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

    /// <summary>
    /// A bound orbit from 20 km up whose farthest from the sun is <paramref name="metres"/> past the
    /// edge (before, if negative): near its apoapsis it hardly moves over a year, while the sun goes
    /// round the planet once, so the farthest it gets from the sun is its apoapsis's farthest from
    /// the sun's circle (<see cref="SolarSystem.FarthestSunFrom"/>). Tuned by a small outward speed
    /// beside its sideways one (the sideways speed alone, a float, places the apoapsis only to a few
    /// per cent); and how far past the edge it really gets.
    /// </summary>
    private static (Vector3 Start, Vector3 Velocity, double Past) Grazing(double metres)
    {
        var start = Above(20000f);
        double periapsis = Vector3.Distance(start, Planet.Centre), apoapsis = 0.9 * Scene.SolarSystemEdge;
        var sideways = (float)Math.Sqrt(Planet.Mu * 2 * apoapsis / (periapsis * (periapsis + apoapsis)));
        double Past(float outward)
        {
            var orbit = new Orbit(Planet, start, East * sideways + Up * outward);
            if (orbit.NextApoapsis is not { } top)
                return double.PositiveInfinity;
            var seconds = orbit.TimeTo(top);
            var leg = new PathLeg(Planet, orbit, Now, double.PositiveInfinity);
            return SolarSystem.FarthestSunFrom(GroundAt(leg, Now + seconds), Now + seconds) - Scene.SolarSystemEdge;
        }
        var (lo, hi) = (0f, 1f);
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (mid <= lo || mid >= hi)
                break;
            if (Past(mid) < metres)
                lo = mid;
            else
                hi = mid;
        }
        return (start, East * sideways + Up * hi, Past(hi));
    }

    [Theory]
    [InlineData(1_000_000.0)]
    [InlineData(1_000.0)]
    public void An_orbit_just_reaching_past_the_edge_ends_there(double metres)
    {
        // Past the edge for a moment a year near its apoapsis: a sliver of its slow way out there.
        var (start, velocity, past) = Grazing(metres);
        output.WriteLine($"its farthest from the sun {past / 1e3:0.000} km past the edge");
        Assert.InRange(past, 0.5 * metres, 2 * metres);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var legs = LegsOf(start, velocity);
        output.WriteLine($"found in {watch.Elapsed.TotalMilliseconds:0.00} ms");
        EndsAtTheEdge(legs);
    }

    [Fact]
    public void An_orbit_just_short_of_the_edge_has_no_edge_end()
    {
        var (start, velocity, past) = Grazing(-1_000.0);
        output.WriteLine($"its farthest from the sun {-past / 1e3:0.000} km short of the edge");
        Assert.InRange(past, -2_000.0, -500.0);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var legs = LegsOf(start, velocity);
        output.WriteLine($"looked for in {watch.Elapsed.TotalMilliseconds:0.00} ms");
        Assert.All(legs, leg => Assert.False(leg.LeavesSystem));
    }

    [Fact]
    public void The_farthest_the_sun_gets_from_a_place_bounds_it_and_is_reached_within_a_year()
    {
        // Places off the sun's plane and in it, near and far: the sun is never further than the
        // bound, and comes within tens of metres of it at some time in a year (its circle swept once).
        var year = Scene.YearDays * Scene.DaySeconds * 1.01;
        foreach (var p in new[] { new Vector3Double(4e9, 1e9, -7e9), new Vector3Double(0, 0, 2e6), (Vector3Double)Above(20000f) })
        {
            var bound = SolarSystem.FarthestSunFrom(p, Now);
            var farthest = 0.0;
            for (var t = Now; t < Now + year; t += 1.0)
            {
                var d = SolarSystem.FromSun(Planet, p, t);
                Assert.True(d <= SolarSystem.FarthestSunFrom(p, t) + 1e-3, "the sun past its bound");
                farthest = Math.Max(farthest, d);
            }
            Assert.InRange(bound - farthest, -1e-3, 50.0); // the samples a second apart: within tens of metres of its top
        }
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

    // ---- Crossing it ----

    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>
    /// Reach Orbit's upper stage numbered <paramref name="id"/>, still over the ground about
    /// <paramref name="inside"/> metres inside the edge at world time <paramref name="now"/>, on the
    /// side the sun's circle round the planet (in the ground's turning frame, a lap a day) is going
    /// away from: so the edge passes it within a second or so. (Jolt, in floats, can't move a craft
    /// this far out by a step's travel, so the flown vessel leaves by the edge going past it.)
    /// </summary>
    private static VesselState SweptPast(int id, double inside, double now = Now)
    {
        var away = -Vector3Double.Normalize(SolarSystem.SunCentreAt(now + 0.5) - SolarSystem.SunCentreAt(now - 0.5)).ToSingle();
        var (best, bestMiss) = (Vector3.Zero, double.MaxValue);
        for (var d = Scene.SolarSystemEdge - inside - 4000; d < Scene.SolarSystemEdge - inside + 4000; d += 100)
        {
            var at = Planet.Centre + away * (float)d;
            var miss = Math.Abs(Scene.SolarSystemEdge - SolarSystem.FromSun(Planet, at, now) - inside);
            if (miss < bestMiss)
                (best, bestMiss) = (at, miss);
        }
        return InOrbit.At(InOrbit.UpperStage(id, 20000f, 1f), best, Planet.AmongStars(best, Vector3.Zero));
    }

    /// <summary>
    /// Reach Orbit's upper stage numbered <paramref name="id"/> out along the planet's north pole
    /// from its centre (where the ground's turn moves nothing), about <paramref name="inside"/>
    /// metres inside the edge at world time <paramref name="now"/> (past it, if negative), as near
    /// as a float's kilometre step there allows, moving straight out at <paramref name="outward"/>.
    /// </summary>
    private static VesselState AtTheEdge(int id, double inside, float outward, double now = Now, int flight = 1)
    {
        var north = Planet.NorthPole;
        var (best, bestMiss) = (Vector3.Zero, double.MaxValue);
        for (var d = Scene.SolarSystemEdge - inside - 4000; d < Scene.SolarSystemEdge - inside + 4000; d += 100)
        {
            var at = Planet.Centre + north * (float)d;
            var miss = Math.Abs(Scene.SolarSystemEdge - SolarSystem.FromSun(Planet, at, now) - inside);
            if (miss < bestMiss)
                (best, bestMiss) = (at, miss);
        }
        return InOrbit.At(InOrbit.UpperStage(id, 20000f, 1f, flight), best, north * outward);
    }

    private static WorldState EdgeWorld(params VesselState[] vessels) =>
        new() { Time = Now, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 2 };

    private static void LeftAndLogged(Flight flight, int id, int entries = 1)
    {
        Assert.DoesNotContain(flight.WorldVessels, v => v.Id == id);
        var name = $"{Vessel.NameOf(Vehicle.TwoStage, 1, 1)} {id}";
        var logged = flight.Log.Where(e => e.What == name).ToList();
        Assert.Equal(entries, logged.Count);
        Assert.All(logged, e => Assert.Equal(WorldLog.LeftTheSolarSystem, e.How));
        Assert.Equal("left the solar system", WorldLog.LeftTheSolarSystem);
    }

    [Fact]
    public void A_vessel_under_formula_past_the_edge_is_removed_and_logged()
    {
        using var sim = new Simulation(Mission.FreePlay, world: EdgeWorld(AtTheEdge(0, 1000, 2000f)));
        var flight = sim.Flight;
        var far = flight.WorldVessels.Single(v => v.Id == 0);
        Assert.True(far.OnFormula, "far from the pad, it should be under formula");
        var asked = false;
        Assert.True(sim.Run(Nobody, f =>
        {
            asked |= f.EdgeAsked > 0;
            return !f.WorldVessels.Contains(far);
        }, maxSeconds: 5), "it should have gone past the edge");
        Assert.True(asked, "it should have been asked whether it was past the edge");
        LeftAndLogged(flight, 0);
        var entry = flight.Log.Single(e => e.How == WorldLog.LeftTheSolarSystem);
        Assert.False(entry.Witnessed);
        Assert.Null(entry.Speed);
        Assert.Equal(FlightState.Ready, flight.Controlled.State); // the flown rocket is untouched
    }

    [Fact]
    public void A_vessel_jumping_past_the_edge_in_warp_is_removed()
    {
        using var sim = new Simulation(Mission.FreePlay, world: EdgeWorld(AtTheEdge(0, 50_000, 2000f)));
        var flight = sim.Flight;
        for (var i = 0; i < Flight.Rates.Length; i++)
            sim.Step(new FlightInput(Faster: true));
        Assert.Equal(Flight.Rates[^1], flight.PlayerRate);
        Assert.True(sim.Run(Nobody, f => !f.WorldVessels.Any(v => v.Id == 0), maxSeconds: 120), "it should have gone past the edge");
        LeftAndLogged(flight, 0);
    }

    [Fact]
    public void A_loaded_world_with_a_vessel_past_the_edge_removes_it_on_the_first_step()
    {
        using var sim = new Simulation(Mission.FreePlay, world: EdgeWorld(AtTheEdge(0, -1000, 0f)));
        var flight = sim.Flight;
        Assert.Contains(flight.WorldVessels, v => v.Id == 0);
        sim.Step(default);
        LeftAndLogged(flight, 0);
    }

    [Fact]
    public void The_flown_vessel_leaving_is_treated_as_a_crash()
    {
        // The run's upper stage, flown, just inside the edge as it sweeps past, its booster
        // dropped: the booster is the one to fly after it.
        var steps = Mission.ReachOrbit.Steps.Length;
        var run = new RunState
        {
            Mission = Mission.ReachOrbit, Site = LaunchSites.Home.Name, Number = 1, LastFlown = 0,
            Done = [.. Enumerable.Range(0, steps).Select(i => i < 2)], Points = new int[steps],
        };
        // (Its booster, in a low orbit, is the run's too, but not what its steps need.)
        var other = InOrbit.UpperStage(1, 20000f, 1f) with { First = 0, Last = 0, CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(Vehicle.TwoStage, 0, 0) };
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        flight.Restore(EdgeWorld(SweptPast(0, 1000), other) with { Runs = [run], Flown = 0 });
        Assert.True(flight.CarryOn(flight.Controlled));
        var flown = flight.Controlled;
        Assert.Equal(0, flown.Id);
        var theRun = flight.Run!;
        Assert.True(sim.Run(Nobody, _ => flown.State == FlightState.Crashed, maxSeconds: 5), "it should have gone past the edge");

        // Lost as a crash is: shown as its message, the camera where it was (it is still the
        // one flown, its wreck's place kept), and its run failed as for any lost vessel.
        Assert.Same(flown, flight.Controlled);
        Assert.Equal("Left the solar system.", flight.CrashReason);
        Assert.Equal(RunEnd.Failed, theRun.End);
        Assert.Contains(flight.Log, e => e.What == WorldLog.Named(theRun) && e.How.StartsWith("failed", StringComparison.Ordinal));
        Assert.DoesNotContain(flight.Capture().Vessels, v => v.Id == 0);
        var logged = Assert.Single(flight.Log, e => e.What == WorldLog.Named(flown));
        Assert.Equal(WorldLog.LeftTheSolarSystem, logged.How);
        Assert.True(logged.Witnessed);
        for (var i = 0; i < 30; i++)
            sim.Step(default); // nothing more is written, nor does it come back
        Assert.Single(flight.Log, e => e.What == WorldLog.Named(flown));

        // Tab flies another, as after a crash, and the one that left is gone from the world.
        Assert.True(flight.CanSwitch);
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Equal(1, flight.Controlled.Id);
        sim.Step(default);
        LeftAndLogged(flight, 0);
        Assert.DoesNotContain(flight.Capture().Vessels, v => v.Id == 0);
    }

    [Fact]
    public void A_vessel_near_its_body_is_not_asked()
    {
        // In orbit, and the new rocket on its pad: far inside the edge, neither is asked, at warp too.
        using var sim = new Simulation(Mission.FreePlay, world: EdgeWorld(InOrbit.UpperStage(0, 20000f, 1f), AtTheEdge(1, 1e6, 0f)));
        var frames = 0;
        sim.Run(Nobody, f =>
        {
            if (frames > 0) // (asked before the first step too)
                Assert.Equal(1, f.EdgeAsked); // the far one alone, 1,000 km inside
            return ++frames > 60;
        }, maxSeconds: 10);
        for (var i = 0; i < Flight.Rates.Length; i++)
            sim.Step(new FlightInput(Faster: true));
        frames = 0;
        sim.Run(Nobody, f =>
        {
            if (frames > 0)
                Assert.Equal(1, f.EdgeAsked);
            return ++frames > 30;
        }, maxSeconds: 600);
        Assert.Equal(3, sim.Flight.WorldVessels.Count);
    }
}
