using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A landing is a stop, not an end: a landed vessel (this flight's or an earlier one's) stays in
/// the world's rules, and is judged again as soon as anything moves it.
/// </summary>
public sealed class LiveLandingTests(ITestOutputHelper output)
{
    /// <summary>Lets the flown vessel come down, for up to <paramref name="seconds"/>, and logs how it ended.</summary>
    private Vessel Land(Simulation sim, double seconds = 20)
    {
        var vessel = sim.Flight.Controlled;
        sim.Run((_, _) => default, _ => vessel.State != FlightState.Flying, seconds);
        output.WriteLine($"{vessel.State} {vessel.CrashReason}; lean {vessel.TiltDegrees:0.0}°; touchdown {vessel.TouchdownSpeed:0.00} m/s");
        return vessel;
    }

    /// <summary>
    /// Stows <paramref name="vessel"/>'s legs (it must be the one flown), then runs on until it has
    /// settled or crashed, for up to <paramref name="seconds"/>. Returns whether it was flying again
    /// at some point after its legs went.
    /// </summary>
    private bool StowLegs(Simulation sim, Vessel vessel, double seconds = 30)
    {
        sim.Step(new FlightInput(ToggleLegs: true));
        var flew = false;
        sim.Run((_, _) => default, _ =>
        {
            flew |= vessel.State == FlightState.Flying;
            return flew && vessel.State != FlightState.Flying;
        }, seconds);
        output.WriteLine($"legs stowed: flew again {flew}; {vessel.State} {vessel.CrashReason}; lean {vessel.TiltDegrees:0.0}°");
        return flew;
    }

    [Fact]
    public void A_landed_booster_whose_legs_are_stowed_is_judged_again()
    {
        using var sim = Drops.OnPad(4, 2f); // the 25° pad: it lands leaning, and without its legs it can't stand
        var booster = Land(sim);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(StowLegs(sim, booster), "it was never flying again: nothing judged it");
        Assert.True(booster.State is FlightState.Landed or FlightState.Crashed, $"{booster.State}");
        Assert.NotEqual("Toppled over.", booster.CrashReason);
    }

    [Fact]
    public void An_earlier_flights_landed_vessel_is_judged_again_when_moved()
    {
        WorldState world;
        using (var first = Drops.OnPad(4, 2f))
        {
            Assert.Equal(FlightState.Landed, Land(first).State);
            first.Run((_, _) => default, _ => false, Scene.FreezeSeconds + 1); // and frozen there
            world = first.Flight.Capture();
        }

        using var sim = new Simulation(world: world);
        var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: booster));
        Assert.Equal(booster, sim.Flight.Controlled);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.Craft.Frozen);
        Assert.True(StowLegs(sim, booster), "the earlier flight's booster was never judged again");
    }

    /// <summary>How far it leans now, from its pose (a frozen vessel's <see cref="Vessel.TiltDegrees"/> isn't measured again).</summary>
    private static float Lean(Vessel v) => float.RadiansToDegrees(MathF.Acos(Math.Clamp(System.Numerics.Vector3.Dot(v.Up, v.LocalUp), -1f, 1f)));

    [Fact]
    public void Set_down_gently_on_its_side_it_has_landed()
    {
        // Its legs stowed, lying on its side on level land: it rests on its body, neither its feet
        // nor its bell on the ground. Which way up it is doesn't matter: it came down gently.
        var (at, downhill, _) = Drops.FindSlope(0f, 2f);
        using var sim = Drops.At(at, downhill, 1f, leanDegrees: 90f, legsOut: false);
        var vessel = Land(sim);
        Assert.Equal(FlightState.Landed, vessel.State);
        Assert.True(Lean(vessel) > 80f, $"it leans only {Lean(vessel):0}°: it isn't lying on its side");
    }

    [Fact]
    public void Dropped_on_its_side_too_fast_it_crashes()
    {
        var (at, downhill, _) = Drops.FindSlope(0f, 2f);
        using var sim = Drops.At(at, downhill, 8f, leanDegrees: 90f, legsOut: false);
        var vessel = Land(sim);
        Assert.Equal(FlightState.Crashed, vessel.State);
        Assert.StartsWith("Hit the ground at", vessel.CrashReason);
    }

    [Fact]
    public void Touching_down_leaning_over_is_no_crash()
    {
        using var sim = Drops.At(Scene.LandingZoneCentre, Planet.East(Scene.LandingZoneCentre), 2f, leanDegrees: 25f);
        var vessel = Land(sim);
        Assert.False(vessel.CrashReason?.StartsWith("Touched down leaning") ?? false, vessel.CrashReason);
        Assert.True(vessel.State == FlightState.Landed || vessel.CrashReason!.StartsWith("Hit the ground at"), $"{vessel.State} {vessel.CrashReason}");
    }

    [Fact]
    public void Throttled_up_a_landed_booster_lifts_off()
    {
        using var sim = Drops.At(Scene.LandingZoneCentre, Planet.East(Scene.LandingZoneCentre), 1f);
        var booster = Land(sim);
        Assert.Equal(FlightState.Landed, booster.State);
        sim.Step(new FlightInput(FullThrottle: true));
        Assert.Equal(FlightState.Flying, booster.State);
        Assert.True(booster.EngineLit);
        sim.Run((_, _) => default, _ => false, 3);
        output.WriteLine($"{booster.State}, {booster.Ground:0.0} m up");
        Assert.True(booster.Ground > 3f, $"only {booster.Ground:0.00} m up");
    }

    [Fact]
    public void A_landed_vessel_with_no_fuel_stays_landed_throttle_or_not()
    {
        using var sim = Drops.At(Scene.LandingZoneCentre, Planet.East(Scene.LandingZoneCentre), 1f, fuel: 0f);
        var booster = Land(sim);
        Assert.Equal(FlightState.Landed, booster.State);
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ =>
        {
            Assert.Equal(FlightState.Landed, booster.State);
            return false;
        }, 2);
    }

    [Fact]
    public void Staging_a_landed_rocket_leaves_the_booster_standing()
    {
        using var sim = Drops.At(Scene.LandingZoneCentre, Planet.East(Scene.LandingZoneCentre), 1f, vehicle: Vehicle.TwoStage);
        var rocket = Land(sim);
        Assert.Equal(FlightState.Landed, rocket.State);
        sim.Step(new FlightInput(CutThrottle: true, Stage: true)); // the upper stage's engine stays off: just coming apart
        var parts = sim.Flight.WorldVessels.Where(v => v.Vehicle == Vehicle.TwoStage && v.State != FlightState.Ready).ToArray(); // not a rocket on the pad
        Assert.Equal(2, parts.Length);
        var booster = parts.Single(v => v.Stage == 0);
        var upper = parts.Single(v => v.Stage == 1);
        // Upright on level ground, the upper stage is left sitting on the booster: it doesn't fall
        // down through it (they collide), though it may tip off it in time.
        var top = RocketGeometry.StackHeight(Vehicle.TwoStage, 0, 0);
        sim.Run((_, _) => default, _ => false, 3);
        output.WriteLine($"after 3 s: upper stage {upper.State} {upper.CrashReason}, {upper.Ground:0.0} m up (the booster's top is {top:0.0} m up)");
        Assert.NotEqual(FlightState.Crashed, upper.State);
        Assert.True(upper.Ground > top - 0.5f, $"the upper stage is {upper.Ground:0.0} m up: it went down through the booster");
        sim.Run((_, _) => default, _ => false, 17);
        output.WriteLine($"booster {booster.State} {booster.CrashReason}; upper stage {upper.State} {upper.CrashReason}");
        Assert.Equal(FlightState.Landed, booster.State);
    }

    [Fact]
    public void An_awake_landed_vessel_that_is_still_stays_landed()
    {
        using var sim = Drops.At(Scene.LandingZoneCentre, Planet.East(Scene.LandingZoneCentre), 1f);
        var booster = Land(sim);
        Assert.Equal(FlightState.Landed, booster.State);
        sim.Run((_, _) => default, _ =>
        {
            Assert.Equal(FlightState.Landed, booster.State);
            return false;
        }, 3);
    }

    [Fact]
    public void A_landed_vessel_never_takes_off_by_itself_flown_or_not()
    {
        // Two boosters landed on the zone, frozen there, as saved, 60 m apart: one as it landed
        // (its throttle down), and one left with its throttle up but its tank empty (it ran dry
        // coming down). Neither takes off: not while another vessel is flown, nor once switched
        // to with nothing touched (only the flown vessel takes off, and only by its throttle).
        WorldState world;
        using (var first = Drops.At(Scene.LandingZoneCentre, Planet.East(Scene.LandingZoneCentre), 1f))
        {
            var landed = Land(first);
            Assert.Equal(FlightState.Landed, landed.State);
            first.Run((_, _) => default, _ => landed.Craft.Frozen, 10);
            world = first.Flight.Capture();
        }
        var down = world.Vessels.Single(v => v.Id == 0);
        Assert.Equal(0f, down.Throttle);
        var north = Planet.North(down.Position.ToSingle());
        var dry = down with { Id = 1, Position = down.Position + north * 60f, Throttle = 1f, Fuel = [.. down.Fuel.Select(_ => 0f)] };
        using var sim = new Simulation(world: world with { Vessels = [down, dry], NextVesselId = 2 });
        var vessels = sim.Flight.WorldVessels.Where(v => v.Id is 0 or 1).ToArray();
        void StayLanded(double seconds) => sim.Run((_, _) => default, _ =>
        {
            Assert.All(vessels, v => Assert.Equal(FlightState.Landed, v.State));
            return false;
        }, seconds);

        StayLanded(3); // the new flight's rocket on the pad is flown
        foreach (var vessel in vessels)
        {
            sim.Step(new FlightInput(SwitchTo: vessel));
            Assert.Same(vessel, sim.Flight.Controlled);
            StayLanded(3);
            Assert.All(vessels, v => Assert.False(v.EngineLit));
        }
    }

    [Fact]
    public void A_gentle_hop_after_its_touchdown_does_not_soften_it()
    {
        // Set down on its engine bell (legs stowed) at about 2 m/s, then hopped half a metre on its
        // engine and set down again, far more gently: its touchdown is the harder first touch, not
        // the gentle second one (a landing can't be made softer after the fact).
        using var sim = Drops.At(Scene.LandingZoneCentre, Planet.East(Scene.LandingZoneCentre), 1.9f, legsOut: false, clearance: 0.15f);
        var booster = sim.Flight.Controlled;
        float? first = null;
        var (phase, touches, lastTouching, highest) = (0, 0, false, 0f);
        sim.Run((f, _) =>
        {
            var touching = booster.Ground < 0.12f;
            if (touching && !lastTouching)
                touches++;
            lastTouching = touching;
            highest = phase is 1 or 2 ? MathF.Max(highest, booster.Ground) : highest;
            if (phase == 0 && touching)
            {
                first ??= booster.TouchdownSpeed;
                phase = 1; // up again, on the engine
            }
            else if (phase == 1 && booster.VerticalSpeed > 2.5f)
                phase = 2; // and down, slowly: the engine on whenever it falls faster than 0.8 m/s
            else if (phase == 2 && touching && highest > 0.3f)
                phase = 3; // down again: engine off
            var burn = phase == 1 || (phase == 2 && booster.VerticalSpeed < -0.8f);
            return new FlightInput(FullThrottle: burn, CutThrottle: !burn);
        }, _ => booster.State != FlightState.Flying && phase == 3, 30);
        output.WriteLine($"{booster.State} {booster.CrashReason}; touches {touches}, the first at {first:0.00} m/s, hopped {highest:0.00} m; touchdown {booster.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(touches >= 2, "it never hopped");
        Assert.NotNull(first);
        Assert.True(first > 1.5f, $"the first touch was only {first:0.00} m/s: this shows nothing");
        Assert.Equal(first.Value, booster.TouchdownSpeed);
    }

    [Fact]
    public void A_harder_touch_after_a_gentle_first_one_is_its_touchdown()
    {
        // The satellite (legs it has none), leaning 55° and set down on its bell at 0.3 m/s: it
        // tips on over onto its side, which comes down harder than the bell did. Its touchdown is
        // that harder touch: a gentle first touch doesn't hide what came after it.
        var zone = Scene.LandingZoneCentre;
        using var sim = Drops.At(zone, Planet.East(zone), 0.3f, Vehicle.ThreeStage, first: 2, last: 2, leanDegrees: 55f, legsOut: false, clearance: 0.05f);
        var satellite = sim.Flight.Controlled;
        float? first = null;
        var most = 0f;
        sim.Run((_, _) => default, _ =>
        {
            if (satellite.TouchdownSpeed > 0f)
            {
                first ??= satellite.TouchdownSpeed;
                most = MathF.Max(most, satellite.TouchdownSpeed);
            }
            return satellite.State != FlightState.Flying;
        }, 30);
        output.WriteLine($"{satellite.State} {satellite.CrashReason}; first touch {first:0.00} m/s; touchdown {satellite.TouchdownSpeed:0.00} m/s; lean {satellite.TiltDegrees:0}°");
        Assert.Equal(FlightState.Landed, satellite.State);
        Assert.NotNull(first);
        Assert.True(satellite.TouchdownSpeed > first.Value + 0.2f, $"its touchdown {satellite.TouchdownSpeed:0.00} m/s is no harder than its first touch, {first:0.00}");
    }
}
