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

    // The rings' hold: one click on a ring sends its angle, and the vessel turns there and keeps it,
    // the other angle left as it was.

    /// <summary>A satellite in a round orbit round <paramref name="body"/>, flown, its nose turned to <paramref name="heading"/> and <paramref name="pitch"/> (degrees) and settled there.</summary>
    private static (Simulation Sim, Vessel Flown) Pointed(GroundBody body, float heading, float pitch)
    {
        var (sim, flown) = MoonFlights.Flying(5000.0, InOrbit.Round(body, 0, 2000f));
        sim.Run((_, _) => new FlightInput(Hold: Direction(flown, heading, pitch)), f => Angle(f.Aim, Direction(flown, heading, pitch)) < 0.5f, maxSeconds: 30);
        return (sim, flown);
    }

    private static Vector3 Direction(Vessel v, float heading, float pitch) =>
        v.Body.Towards(v.CentreOfMass, float.DegreesToRadians(heading)) * MathF.Cos(float.DegreesToRadians(pitch)) + v.Body.Up(v.CentreOfMass) * MathF.Sin(float.DegreesToRadians(pitch));

    private static float HeadingOf(Vessel v) => float.RadiansToDegrees(v.Body.Bearing(v.CentreOfMass, v.Aim));

    private static float PitchOf(Vessel v) => float.RadiansToDegrees(MathF.Asin(Math.Clamp(Vector3.Dot(v.Aim, v.Body.Up(v.CentreOfMass)), -1f, 1f)));

    private static float Between(float a, float b) => MathF.Abs((a - b + 540f) % 360f - 180f);

    [Fact]
    public void One_click_on_the_heading_ring_turns_to_that_heading_and_holds_it()
    {
        var (sim, flown) = Pointed(GroundBody.Planet, 0f, 45f);
        using (sim)
        {
            sim.Step(new FlightInput(RingHeading: float.DegreesToRadians(120f)));
            Assert.Equal(HoldMode.Rings, sim.Flight.Holding);
            sim.Run((_, _) => default, _ => false, maxSeconds: 15);
            output.WriteLine($"heading {HeadingOf(flown):0.0}°, pitch {PitchOf(flown):0.0}°");
            Assert.True(Between(HeadingOf(flown), 120f) < 2f, $"heading {HeadingOf(flown):0.0}°");
            Assert.True(MathF.Abs(PitchOf(flown) - 45f) < 2f, $"pitch {PitchOf(flown):0.0}°, which was left as it was");
            Assert.Equal(HoldMode.Rings, sim.Flight.Holding);
        }
    }

    [Fact]
    public void Clicking_both_rings_turns_on_both_together()
    {
        var (sim, flown) = Pointed(GroundBody.Planet, 0f, 60f);
        using (sim)
        {
            sim.Step(new FlightInput(RingHeading: float.DegreesToRadians(180f), RingPitch: float.DegreesToRadians(20f)));
            var pitchBegun = false;
            sim.Run((_, _) => default, _ =>
            {
                pitchBegun |= MathF.Abs(PitchOf(flown) - 60f) > 3f && Between(HeadingOf(flown), 180f) > 10f;
                return false;
            }, maxSeconds: 20);
            output.WriteLine($"heading {HeadingOf(flown):0.0}°, pitch {PitchOf(flown):0.0}°");
            Assert.True(pitchBegun, "the pitch only began to turn once the heading had finished");
            Assert.True(Between(HeadingOf(flown), 180f) < 2f, $"heading {HeadingOf(flown):0.0}°");
            Assert.True(MathF.Abs(PitchOf(flown) - 20f) < 2f, $"pitch {PitchOf(flown):0.0}°");
        }
    }

    [Fact]
    public void A_pitch_click_while_the_nose_is_still_turning_to_a_held_heading_keeps_that_heading()
    {
        var (sim, flown) = Pointed(GroundBody.Planet, 0f, 45f);
        using (sim)
        {
            sim.Step(new FlightInput(RingHeading: float.DegreesToRadians(150f)));
            sim.Run((_, _) => default, _ => false, maxSeconds: 1);
            Assert.True(Between(HeadingOf(flown), 150f) > 20f, $"heading {HeadingOf(flown):0.0}°: it should still be turning");
            sim.Step(new FlightInput(RingPitch: float.DegreesToRadians(10f)));
            Assert.Equal(HoldMode.Rings, sim.Flight.Holding);
            sim.Run((_, _) => default, _ => false, maxSeconds: 20);
            output.WriteLine($"heading {HeadingOf(flown):0.0}°, pitch {PitchOf(flown):0.0}°");
            Assert.True(Between(HeadingOf(flown), 150f) < 2f, $"heading {HeadingOf(flown):0.0}°: the held one, not where the nose was as the pitch was clicked");
            Assert.True(MathF.Abs(PitchOf(flown) - 10f) < 2f, $"pitch {PitchOf(flown):0.0}°");
        }
    }

    [Fact]
    public void A_ring_press_let_go_without_moving_is_a_click_however_long_it_is_held()
    {
        var press = new NavigationControl.RingPress();
        press.Begin(new Vector2(100f, 100f));
        for (var frame = 0; frame < 6; frame++) // a click a person makes lasts several frames
            press.Follow(new Vector2(100f, 100f + (frame == 3 ? 1f : 0f)));
        Assert.False(press.Dragging);

        // Moved further, all told, it is a drag: steering while held, no click when let go.
        press.Begin(new Vector2(100f, 100f));
        press.Follow(new Vector2(103f, 100f));
        press.Follow(new Vector2(100f, 100f));
        Assert.True(press.Dragging);
    }

    [Fact]
    public void Steering_by_the_keys_ends_the_ring_hold()
    {
        var (sim, _) = Pointed(GroundBody.Planet, 0f, 45f);
        using (sim)
        {
            sim.Step(new FlightInput(RingPitch: float.DegreesToRadians(30f)));
            Assert.Equal(HoldMode.Rings, sim.Flight.Holding);
            sim.Step(new FlightInput(Lean: Vector3.UnitX));
            Assert.Equal(HoldMode.None, sim.Flight.Holding);

            // And so does dragging a ring.
            sim.Step(new FlightInput(RingPitch: float.DegreesToRadians(30f)));
            sim.Step(new FlightInput(Hold: Vector3.UnitY));
            Assert.Equal(HoldMode.None, sim.Flight.Holding);
        }
    }

    [Fact]
    public void The_ring_hold_works_round_the_moon()
    {
        var (sim, flown) = Pointed(GroundBody.Moon, 0f, 45f);
        using (sim)
        {
            Assert.Same(GroundBody.Moon, flown.Body);
            sim.Step(new FlightInput(RingHeading: float.DegreesToRadians(250f)));
            sim.Run((_, _) => default, _ => false, maxSeconds: 15);
            output.WriteLine($"heading {HeadingOf(flown):0.0}°, pitch {PitchOf(flown):0.0}°");
            Assert.True(Between(HeadingOf(flown), 250f) < 2f, $"heading {HeadingOf(flown):0.0}°");
            Assert.True(MathF.Abs(PitchOf(flown) - 45f) < 2f, $"pitch {PitchOf(flown):0.0}°");
        }
    }

    [Fact]
    public void The_rings_are_drawn_and_read_by_the_moon_s_compass_at_the_moon()
    {
        // The rings (NavigationControl) lay out their points, read the nose and send a click's
        // angle by RingAngles; the hold steers by the vessel's own body. At the moon both must be
        // the moon's, so the nose settles on the ring's point and the held mark sits on it.
        var (sim, flown) = Pointed(GroundBody.Moon, 0f, 45f);
        using (sim)
        {
            var (heading, pitch) = (float.DegreesToRadians(250f), float.DegreesToRadians(30f));
            sim.Step(new FlightInput(RingHeading: heading, RingPitch: pitch));
            sim.Run((_, _) => default, _ => false, maxSeconds: 15);
            var drawn = RingAngles.Direction(flown.Body, flown.CentreOfMass, heading, pitch);
            var read = RingAngles.Of(flown.Body, flown.CentreOfMass, flown.Aim, 0f);
            output.WriteLine($"nose {Angle(flown.Aim, drawn):0.00}° off the ring's point; read {float.RadiansToDegrees(read.Heading):0.0}°, {float.RadiansToDegrees(read.Pitch):0.0}°");
            Assert.True(Angle(flown.Aim, drawn) < 2f, $"the nose is {Angle(flown.Aim, drawn):0.0}° off the ring's point");
            Assert.True(Between(float.RadiansToDegrees(read.Heading), 250f) < 2f, $"heading read {float.RadiansToDegrees(read.Heading):0.0}°");
            Assert.True(MathF.Abs(float.RadiansToDegrees(read.Pitch) - 30f) < 2f, $"pitch read {float.RadiansToDegrees(read.Pitch):0.0}°");
            var ringPoint = RingAngles.Direction(flown.Body, flown.CentreOfMass, heading, 0f);
            Assert.True(MathF.Abs(Vector3.Dot(ringPoint, flown.Body.Up(flown.CentreOfMass))) < 1e-3f, "the heading ring lies flat on the moon's horizon");
            Assert.True(Between(float.RadiansToDegrees(flown.Body.Bearing(flown.CentreOfMass, ringPoint)), 250f) < 0.1f, "its point is at that bearing by the moon's north");
        }
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
