using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Whole flights, end to end, through the real flight rules and physics: the autopilot flies the
/// mission (or gets it wrong on purpose), and the tests check how the flight judged it.
/// </summary>
public sealed class FlightTests(ITestOutputHelper output)
{
    [PlaythroughFact(Long = false)]
    [Trait("Kind", "Playthrough")]
    public void Autopilot_flies_the_mission_and_lands_on_the_landing_zone()
    {
        using var sim = new Simulation();
        var pilot = new Autopilot();

        Assert.True(sim.RunToEnd(pilot.Fly), "the flight never ended");
        Report(sim);

        var flight = sim.Flight;
        Assert.Equal(FlightState.Landed, flight.State);
        Assert.True(flight.ReachedTarget, $"only reached {flight.MaxAltitude:0} m");
        Assert.True(flight.LandedOnTarget, $"landed {flight.DistanceToLandingSite:0.0} m from the landing zone's centre");
        Assert.True(flight.Run!.Succeeded);
        Assert.InRange(flight.TouchdownSpeed, 0f, Scene.SafeTouchdownSpeed);
        Assert.True(flight.Fuel > 0f);

        // Then it takes off again: the landing stays, and so does the score.
        var score = flight.Run!.Score;
        Assert.NotNull(score);
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, 3);
        output.WriteLine($"{flight.State}, {flight.Ground:0.0} m up; score {score} then {flight.Run!.Score}");
        Assert.Equal(FlightState.Flying, flight.State);
        Assert.True(flight.Ground > 3f, $"only {flight.Ground:0.00} m up");
        Assert.True(flight.Run!.Progress.IsDone(Array.FindIndex(flight.Mission.Steps, s => s.Label == "Land on the landing zone")));
        Assert.Equal(score, flight.Run!.Score);
    }

    [Fact]
    public void Coming_down_gently_on_the_engine_bell_lands_the_booster_as_it_stands_on_the_pad()
    {
        using var sim = new Simulation();
        var pilot = new Autopilot { DeployLegs = false };

        sim.RunToEnd(pilot.Fly);
        Report(sim);

        var flight = sim.Flight;
        Assert.False(flight.LegsWanted);
        Assert.Equal(FlightState.Landed, flight.State);
        Assert.True(flight.LandedOnTarget, $"landed {flight.DistanceToLandingSite:0.0} m from the landing zone's centre");
        Assert.InRange(flight.TouchdownSpeed, 0f, Scene.SafeBellTouchdownSpeed);
    }

    [Fact]
    public void Coming_down_on_the_engine_bell_faster_than_it_takes_destroys_the_booster()
    {
        // Slow enough for the legs, but not for the bare bell.
        using var sim = new Simulation();
        var pilot = new Autopilot { DeployLegs = false, TouchdownSpeed = 4f };

        sim.RunToEnd(pilot.Fly);
        Report(sim);

        Assert.Equal(FlightState.Crashed, sim.Flight.State);
        Assert.Contains("engine bell", sim.Flight.CrashReason);
        Assert.InRange(sim.Flight.TouchdownSpeed, Scene.SafeBellTouchdownSpeed, Scene.SafeTouchdownSpeed);
    }

    [Fact]
    public void Touching_down_too_fast_destroys_the_booster()
    {
        using var sim = new Simulation();
        var pilot = new Autopilot { TouchdownSpeed = 12f };

        sim.RunToEnd(pilot.Fly);
        Report(sim);

        Assert.Equal(FlightState.Crashed, sim.Flight.State);
        Assert.Contains("Hit the ground", sim.Flight.CrashReason);
        Assert.True(sim.Flight.TouchdownSpeed > Scene.SafeTouchdownSpeed);
    }

    [Fact]
    public void Flying_into_the_service_tower_destroys_the_booster()
    {
        using var sim = new Simulation();
        // Throttle back to just enough to climb slowly, and lean hard towards the tower (to the
        // west of the pad) from lift-off, so it drifts into it rather than climbing over it.
        var towards = Vector2.Normalize(new Vector2(Scene.TowerOffset.X, Scene.TowerOffset.Z));
        Pilot pilot = (f, _) => f.State == FlightState.Ready
            ? new FlightInput(Launch: true, FullThrottle: true)
            : new FlightInput(Throttle: f.Throttle > 0.7f ? -1f : 0f, Lean: new Vector3(towards.X, 0f, towards.Y));

        sim.RunToEnd(pilot, maxSeconds: 30);
        Report(sim);

        Assert.Equal(FlightState.Crashed, sim.Flight.State);
        Assert.Contains("tower", sim.Flight.CrashReason);
    }

    [Fact]
    public void Lying_against_a_landing_zones_plinth_its_body_is_touching_it()
    {
        // A far zone's plinth, a drum standing high over the ground on its downhill side: a
        // booster (legs stowed) lying level against that side, 3 m under the zone's top, its middle
        // 5 cm into the plinth's wall, well clear of the ground under it. Its body is touching
        // the plinth (it is down, as a vessel on the ground is), however far the ground is below.
        var (plinth, outward, drop) = Foundations.LandingZonePlinth
            .Where(p => Vector3.Dot(p.Normal, p.Up) > 0.999f) // level zones, not the test range's
            .SelectMany(p => Enumerable.Range(0, 72).Select(k =>
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 72);
                var a = Vector3.Normalize(Vector3.Cross(p.Up, Vector3.UnitX));
                var way = a * c + Vector3.Cross(p.Up, a) * s;
                return (p, way, Planet.TerrainAltitude(p.Top + way * (Scene.LandingZoneRadius + 3f)));
            }))
            .MaxBy(x => x.Item3);
        output.WriteLine($"the plinth at {plinth.Centre}: its top {drop:0.0} m over the ground 3 m out from its rim");
        Assert.True(drop > 12f, "no plinth stands high enough over the ground for this to show anything");
        var radius = Vehicle.Booster.Stages[0].Radius;
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var along = Vector3.Cross(plinth.Up, outward); // level, along the plinth's side
        var middle = plinth.Top - plinth.Up * 3f + outward * (Scene.LandingZoneRadius + radius - 0.05f);
        var lying = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(outward, -MathF.PI / 2) * Planet.UprightAt(middle));
        Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitY, lying), along) > 0.99f);
        var bell = Vehicle.Booster.Stages[0].BellHeight;
        var state = Drops.Placed(middle, outward, 0f, legsOut: false) with
        {
            Position = middle - along * ((length - bell) / 2f), Rotation = lying, Velocity = Vector3.Zero,
            Aim = Vector3.Transform(Vector3.UnitY, lying),
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
        Assert.True(Planet.GroundAltitude(booster.CentreOfMass - plinth.Up * radius - outward * 0.5f) > 6f, "the ground is too near under it to show anything");
        sim.Step(new FlightInput(SwitchTo: booster));
        output.WriteLine($"{booster.State} {booster.CrashReason}; grounded {booster.Grounded}");
        Assert.True(booster.Grounded, "its body against the plinth's wall isn't touching it");
    }

    [Fact]
    public void Landing_away_from_the_landing_zone_is_a_landing_but_not_a_success()
    {
        using var sim = new Simulation();
        var pilot = new Autopilot { Target = Scene.LandingZoneCentre + new Vector3(60f, 0f, 0f) };

        sim.RunToEnd(pilot.Fly);
        Report(sim);

        var flight = sim.Flight;
        Assert.Equal(FlightState.Landed, flight.State);
        Assert.False(flight.LandedOnTarget);
        Assert.False(flight.Run!.Succeeded);
    }

    [Fact]
    public void The_engine_stops_when_the_fuel_runs_out()
    {
        using var sim = new Simulation();
        Pilot fullThrottle = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : new FlightInput(Throttle: 1f);

        var burn = Vehicle.Booster.Stages[0].BurnSeconds;

        Assert.True(sim.Run(fullThrottle, f => f.Fuel <= 0f, maxSeconds: burn + 10));
        sim.Step(new FlightInput(Throttle: 1f));
        Report(sim);

        Assert.False(sim.Flight.EngineLit);
        Assert.InRange(sim.Time, burn - 1, burn + 1);
    }

    [Fact]
    public void Space_lights_the_engine_at_once_at_the_set_throttle()
    {
        using var sim = new Simulation();
        Assert.Equal(Scene.LaunchThrottle, sim.Flight.Throttle);

        sim.Step(new FlightInput(Launch: true));
        Assert.Equal(FlightState.Flying, sim.Flight.State);
        Assert.True(sim.Flight.EngineLit);
        Assert.Equal(Scene.LaunchThrottle, sim.Flight.Throttle);

        sim.Run((_, _) => default, _ => false, maxSeconds: 3);
        Assert.True(sim.Flight.Ground > 2f, $"only {sim.Flight.Ground:0.00} m up: {Scene.LaunchThrottle:P0} should lift it");
    }

    [Fact]
    public void The_space_that_launches_it_drops_no_stage()
    {
        // Space is both launch and stage (see Controls): the press that lights the engine is the launch alone.
        using var sim = new Simulation(Mission.ReachOrbit);
        sim.Step(new FlightInput(Launch: true, Stage: true));
        Assert.True(sim.Flight.EngineLit);
        Assert.Single(sim.Flight.Vessels);
    }

    private static (Simulation Sim, Vessel First, Vessel Second) TwoVessels()
    {
        var sim = new Simulation(Mission.ReachOrbit);
        sim.Step(new FlightInput(Launch: true));
        sim.Run((_, _) => new FlightInput(FullThrottle: true), f => f.Ground > 200f, maxSeconds: 60);
        sim.Step(new FlightInput(Stage: true));
        var first = sim.Flight.Controlled;
        var second = sim.Flight.WorldVessels.First(v => v != first && v.State != FlightState.Crashed);
        return (sim, first, second);
    }

    [Fact]
    public void A_view_given_with_a_switch_is_kept_by_the_vessel_flown_before_it()
    {
        var (sim, first, second) = TwoVessels();
        using var _ = sim;
        var view = new CameraView(true, 1f, 0.3f, 60f, 2f, 0.9f, 90_000f, CameraCentre.Own);
        sim.Flight.RunFrame(Simulation.FrameSeconds, new FlightInput(View: view, SwitchTo: second));
        sim.Flight.RunFrame(Simulation.FrameSeconds, default); // flown behind its link-up
        Assert.Same(second, sim.Flight.Controlled);
        Assert.Equal(view, first.View);
        Assert.Null(second.View);
    }

    [Fact]
    public void A_view_is_taken_in_a_warp_frames_first_step_only()
    {
        var (sim, first, second) = TwoVessels();
        using var _ = sim;
        var view = new CameraView(false, 1f, 0.3f, 60f, 2f, 0.9f, 90_000f, CameraCentre.Own);
        sim.Flight.Advance(0.1f, new FlightInput(View: view, SwitchTo: second)); // several steps
        sim.Flight.RunFrame(Simulation.FrameSeconds, default); // flown behind its link-up
        Assert.Same(second, sim.Flight.Controlled);
        Assert.Equal(view, first.View);
        Assert.Null(second.View);
    }

    [Fact]
    public void At_zero_throttle_space_leaves_it_on_the_pad_until_throttled_up()
    {
        using var sim = new Simulation();
        sim.Step(new FlightInput(CutThrottle: true));
        sim.Step(new FlightInput(Launch: true));
        Assert.Equal(FlightState.Flying, sim.Flight.State);
        Assert.False(sim.Flight.EngineLit);

        sim.Run((_, _) => default, _ => false, maxSeconds: 2);
        Assert.True(sim.Flight.Ground < 0.5f, $"{sim.Flight.Ground:0.00} m up with its engine off");
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 3);
        Assert.True(sim.Flight.Ground > 2f, $"only {sim.Flight.Ground:0.00} m up at full throttle");
    }

    [Fact]
    public void Starting_again_puts_the_booster_back_on_the_pad()
    {
        using var sim = new Simulation();
        sim.RunToEnd(new Autopilot { TouchdownSpeed = 12f }.Fly);
        Assert.Equal(FlightState.Crashed, sim.Flight.State);

        sim.Step(new FlightInput(StartAgain: true));
        sim.Run((_, _) => default, _ => false, maxSeconds: 1);

        var flight = sim.Flight;
        Assert.Equal(FlightState.Ready, flight.State);
        Assert.Equal(1f, flight.Fuel);
        Assert.True(flight.MaxAltitude < 0.05f, $"best altitude still {flight.MaxAltitude} m");
        Assert.True(Vector3.Distance(flight.Position, Scene.RocketStart) < 0.1f, $"it's at {flight.Position}");
        Assert.True(flight.Ground < 0.05f);
    }

    /// <summary>How the flight went, for the test log.</summary>
    private void Report(Simulation sim)
    {
        var f = sim.Flight;
        output.WriteLine(
            $"{f.State} after {sim.Time:0.0} s of flight, simulated in {sim.WallTime.TotalMilliseconds:0} ms " +
            $"({sim.Time / Math.Max(sim.WallTime.TotalSeconds, 1e-6):0}x real time)");
        output.WriteLine(
            $"best altitude {f.MaxAltitude:0} m, touchdown {f.TouchdownSpeed:0.0} m/s, {f.DistanceToLandingSite:0.0} m from target, " +
            $"fuel {f.Fuel * 100:0}%, tilt {f.TiltDegrees:0}°");
        if (f.CrashReason is { } reason)
            output.WriteLine($"crash: {reason}");
    }
}
