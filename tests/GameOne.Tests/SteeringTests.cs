using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The steering, which is the same for every mission: a held direction (the direction rings) or a
/// lean turns the rocket only as fast as its engine and thrusters can; let go, and the air turns it
/// along its direction of travel, or above the atmosphere nothing does.
/// </summary>
public sealed class SteeringTests(ITestOutputHelper output)
{
    private static readonly Pilot Climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;

    [Fact]
    public void Leaning_tilts_the_booster_and_it_drifts_that_way()
    {
        using var sim = new Simulation();
        sim.Run(Climb, f => f.Ground > 50f, maxSeconds: 20);

        sim.Run((_, _) => new FlightInput(Lean: Vector3.UnitX), _ => false, maxSeconds: 2);
        Report(sim);
        Assert.True(sim.Flight.TiltDegrees > 20f, $"only tilted {sim.Flight.TiltDegrees:0}°");
        Assert.True(sim.Flight.Velocity.X > 1f, "it should drift east as it leans east");
    }

    [Fact]
    public void Holding_a_direction_turns_the_rocket_to_it_and_keeps_it_there()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        sim.Run(Climb, f => f.Ground > 30f, maxSeconds: 20);

        var tilt = float.DegreesToRadians(30f);
        var wanted = Vector3.Normalize(Vector3.UnitY * MathF.Cos(tilt) + Vector3.UnitX * MathF.Sin(tilt));
        sim.Run((_, _) => new FlightInput(Hold: wanted), _ => false, maxSeconds: 6);
        Report(sim);

        Assert.InRange(sim.Flight.TiltDegrees, 26f, 34f);
        Assert.True(Angle(sim.Flight.Aim, wanted) < 4f, $"aim is {Angle(sim.Flight.Aim, wanted):0.0}° off");
    }

    [Fact]
    public void Letting_go_the_air_turns_the_nose_along_the_direction_of_travel()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        sim.Run(Climb, f => f.Ground > 30f, maxSeconds: 20);

        // A pitch kick east, then let go: a gravity turn.
        var kick = Vector3.Normalize(Vector3.UnitY + Vector3.UnitX * 0.2f);
        sim.Run((_, _) => new FlightInput(Hold: kick), _ => false, maxSeconds: 3);
        sim.Run((_, _) => default, _ => false, maxSeconds: 12);
        Report(sim);

        var flight = sim.Flight;
        var offTravel = Angle(flight.Aim, flight.Velocity);
        output.WriteLine($"nose {offTravel:0.0}° off the direction of travel, tilt {flight.TiltDegrees:0}°");
        // It lags a few degrees behind: gravity keeps bending the path down, and the air keeps up.
        Assert.True(offTravel < 10f, $"nose {offTravel:0.0}° off the direction of travel");
        // The kick tipped it about 11°; gravity bends its path over further, and the nose follows.
        // Not by much: climbing, the turning frame pushes it west (−2 Ω × v), against the eastward
        // kick. In 8 s it went 1.6° past the kick, only 0.6° clear of the 1° asked; so it is let go
        // for 12 s, which takes it 2.6° past (1.6° clear).
        var kickDegrees = Angle(kick, Vector3.UnitY);
        output.WriteLine($"kick {kickDegrees:0.00}°, tilt {flight.TiltDegrees:0.00}°: {flight.TiltDegrees - kickDegrees:0.00}° past it");
        Assert.True(flight.TiltDegrees > kickDegrees + 1f, $"the gravity turn only tipped it {flight.TiltDegrees:0.0}° (kick {kickDegrees:0.0}°)");
    }

    [Fact]
    public void Above_the_atmosphere_letting_go_keeps_the_nose_where_it_points()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var pilot = new OrbitAutopilot();
        Assert.True(sim.Run(pilot.Fly, f => f.AchievedOrbit, maxSeconds: 300));

        // Kept among the stars: over the ground it turns back by the planet's turn.
        var (aim, start) = (sim.Flight.Aim, sim.Time);
        sim.Run((_, _) => default, _ => false, maxSeconds: 8);
        Report(sim);
        var amongStars = Planet.TurnedDirection(sim.Flight.Aim, sim.Time - start);
        Assert.True(Angle(amongStars, aim) < 0.5f, $"the nose drifted {Angle(amongStars, aim):0.0}° among the stars");
    }

    [Fact]
    public void With_the_engine_off_it_turns_only_as_fast_as_its_thrusters_can()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        sim.Run(Climb, f => f.Ground > 300f, maxSeconds: 30);
        sim.Step(new FlightInput(CutThrottle: true));
        Assert.Equal(Scene.TiltRate * Scene.ThrusterAuthority, sim.Flight.TurnRate, 4);

        var before = sim.Flight.Aim;
        sim.Run((_, _) => new FlightInput(Hold: Vector3.UnitX), _ => false, maxSeconds: 1);
        var turned = Angle(before, sim.Flight.Aim);
        Assert.InRange(turned, 0f, float.RadiansToDegrees(Scene.TiltRate * Scene.ThrusterAuthority) + 1f);
    }

    [Fact]
    public void The_booster_can_be_dropped_before_its_fuel_runs_out()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        sim.Run(Climb, f => f.Ground > 200f, maxSeconds: 30);
        Assert.True(sim.Flight.Fuel > 0.3f);

        sim.Step(new FlightInput(Stage: true));
        Assert.Equal(1, sim.Flight.Stage);
        sim.Run((_, _) => default, _ => false, maxSeconds: Scene.StageIgnitionSeconds + 0.1);
        Assert.True(sim.Flight.EngineLit);
    }

    [Fact]
    public void Choosing_a_mission_mid_flight_starts_again_on_it()
    {
        using var sim = new Simulation();
        sim.Run(Climb, f => f.Ground > 100f, maxSeconds: 30);

        sim.Step(new FlightInput(Choose: Mission.ReachOrbit));
        var flight = sim.Flight;
        Assert.Equal(FlightState.Ready, flight.State);
        Assert.Same(Mission.ReachOrbit, flight.Mission);
        Assert.Equal(2, flight.StageCount);
        Assert.True(flight.Ground < 0.1f);
    }

    private static float Angle(Vector3 a, Vector3 b) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)));

    private void Report(Simulation sim)
    {
        var f = sim.Flight;
        output.WriteLine($"{f.State} at {sim.Time:0.0} s: altitude {f.Ground:0} m, tilt {f.TiltDegrees:0}°, " +
            $"speed {f.Velocity.Length():0} m/s, throttle {f.Throttle * 100:0}%");
    }
}
