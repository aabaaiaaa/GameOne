using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Several vessels in one flight: once the rocket stages, each part is a vessel of its own. One
/// with guidance can be flown (the pilot switches between them); one without is debris. The ones
/// not being flown carry on with whatever they were last given. They fly the orbit mission, whose
/// booster carries guidance so it can come back.
/// </summary>
public sealed class VesselTests(ITestOutputHelper output)
{
    /// <summary>The two-stage rocket, but with a booster that carries no guidance, so once dropped it is debris.</summary>
    private static readonly Mission Expendable = new()
    {
        Name = "TEST: EXPENDABLE BOOSTER",
        Briefing = "",
        Vehicle = Vehicle.TwoStage with
        {
            Name = "Expendable two-stage rocket",
            Stages = [Vehicle.TwoStage.Stages[0] with { Guided = false, LandingReserve = 0f }, Vehicle.TwoStage.Stages[1]],
        },
        TargetAltitude = Scene.AtmosphereHeight,
        Steps = [],
    };

    private static readonly Pilot Climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;

    /// <summary>The vessel that came away when the rocket staged: the one not being flown.</summary>
    private static Vessel Dropped(Flight flight) => flight.Vessels.Single(v => v != flight.Controlled);

    /// <summary>
    /// The satellite launcher's stages <paramref name="first"/> to <paramref name="last"/> over the
    /// air, tumbling end over end at 0.2 rad/s, flown; its fairing on with <paramref name="fairing"/>.
    /// </summary>
    private static (Simulation Sim, Vessel Rocket) Tumbling(int first, int last, bool fairing = false)
    {
        var at = Planet.Centre + Vector3.UnitY * (Planet.SeaRadius + Scene.AtmosphereHeight + 2000f);
        var state = CrossingTests.Craft(0, GroundBody.Planet, at, Vector3.Zero, Vector3.UnitY, first: first, last: last) with
        {
            AngularVelocity = new Vector3(0f, 0f, 0.2f), FairingOn = fairing,
        };
        return MoonTripTests.Flying(MoonTrip.World(MoonTrip.Start, state));
    }

    [Fact]
    public void A_turning_rocket_comes_apart_each_part_going_on_as_it_moved_with_the_whole()
    {
        // Each part's centre of mass moved as the whole's did and by the turning besides, so they
        // part at that, and the push apart along the axis; and the turning's shares cancel over
        // the parts (by their masses), so the whole's centre of mass goes on as it was.
        var (sim, rocket) = Tumbling(0, 1);
        using var _ = sim;
        // A frame first, to see how the whole's centre of mass moves on over one (falling).
        var (c0, v0) = (rocket.CentreOfMass, rocket.Velocity);
        sim.Step(default);
        var (c1, v1) = (rocket.CentreOfMass, rocket.Velocity);
        var spin = sim.Physics.AngularVelocity(rocket.Craft); // as it came apart (each part's steering turns it after)
        var up = rocket.Up;
        sim.Step(new FlightInput(Stage: true));
        var flight = sim.Flight;
        var dropped = flight.WorldVessels.Single(v => v.Vehicle == rocket.Vehicle && v is { Stage: 0, TopStage: 0 }); // not the new rocket on the pad
        Assert.Equal((1, 0), (rocket.Stage, dropped.Stage));
        var turning = Vector3.Cross(spin, rocket.CentreOfMass - dropped.CentreOfMass);
        var expected = turning + up * Scene.SeparationSpeed;
        var apart = rocket.Velocity - dropped.Velocity;
        output.WriteLine($"turning at {spin.Length():0.000} rad/s, the parts' centres {Vector3.Distance(rocket.CentreOfMass, dropped.CentreOfMass):0.00} m apart: parting at {apart.Length():0.000} m/s, {Vector3.Distance(apart, expected):0.000} m/s from the turning's {turning.Length():0.000} m/s and the push apart's {Scene.SeparationSpeed:0.0}");
        Assert.True(turning.Length() > 1f, $"the turning moves the parts apart at only {turning.Length():0.000} m/s");
        Assert.True(Vector3.Distance(apart, expected) < 0.05f, $"parting at {apart} m/s against {expected} m/s");

        // The whole's momentum: the parts' velocities, less their pushes, weighed by their shares of
        // its mass (as its centre of mass, moved on a frame, lies between theirs), are its own moved on a frame.
        var (centre, along) = (c1 + (c1 - c0), v1 + (v1 - v0));
        var between = rocket.CentreOfMass - dropped.CentreOfMass;
        var share = Vector3.Dot(centre - dropped.CentreOfMass, between) / between.LengthSquared(); // the part above's
        var mean = share * (rocket.Velocity - up * (Scene.SeparationSpeed / 2)) + (1f - share) * (dropped.Velocity + up * (Scene.SeparationSpeed / 2));
        output.WriteLine($"the part above {share:P1} of the mass: the parts' mean, less the pushes, {Vector3.Distance(mean, along):0.000} m/s from the whole's");
        Assert.InRange(share, 0.05f, 0.95f);
        Assert.True(Vector3.Distance(mean, along) < 0.05f, $"the parts' mean {mean} m/s against the whole's {along} m/s");
    }

    [Fact]
    public void A_turning_rockets_fairing_halves_go_on_as_they_moved_with_it()
    {
        // The halves' pushes out cancel between them; what is left of their mean against the
        // rocket's is the turning's share where they are.
        var (sim, rocket) = Tumbling(1, 2, fairing: true);
        using var _ = sim;
        var spin = sim.Physics.AngularVelocity(rocket.Craft);
        sim.Step(new FlightInput(Stage: true));
        Assert.False(rocket.FairingOn);
        var halves = sim.Flight.WorldVessels.Where(v => v.IsFairing).ToArray();
        Assert.Equal(2, halves.Length);
        var middle = (halves[0].CentreOfMass + halves[1].CentreOfMass) / 2f;
        var turning = Vector3.Cross(spin, middle - rocket.CentreOfMass);
        var mean = (halves[0].Velocity + halves[1].Velocity) / 2f - rocket.Velocity;
        output.WriteLine($"turning at {spin.Length():0.000} rad/s, the halves' middle {Vector3.Distance(middle, rocket.CentreOfMass):0.00} m from the rocket's centre: their mean {mean.Length():0.000} m/s against the rocket, {Vector3.Distance(mean, turning):0.000} m/s from the turning's {turning.Length():0.000} m/s");
        Assert.True(turning.Length() > 0.3f, $"the turning moves the halves at only {turning.Length():0.000} m/s");
        Assert.True(Vector3.Distance(mean, turning) < 0.05f, $"the halves' mean {mean} m/s against the turning's {turning} m/s");
    }

    [Fact]
    public void Staging_makes_the_dropped_stage_a_vessel_of_its_own()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var crafts = sim.Physics.Crafts.Count;
        sim.Run(Climb, f => f.Ground > 200f, maxSeconds: 30);

        sim.Step(new FlightInput(Stage: true));
        var flight = sim.Flight;
        Assert.Equal(2, flight.Vessels.Count);
        Assert.Equal(crafts + 1, sim.Physics.Crafts.Count);
        var upper = flight.Controlled;  // the pilot flies on with the stages above ...
        var booster = Dropped(flight);  // ... and the booster is the new vessel
        Assert.Equal("Booster", booster.Name);
        Assert.Equal("Upper stage", upper.Name);
        Assert.Equal(1, upper.Stage);
        Assert.Equal(0, booster.Stage);
        Assert.Equal(0f, booster.Throttle); // its engine is shut down as it drops away
    }

    [Fact]
    public void A_dropped_stage_without_guidance_is_debris_and_cannot_be_flown()
    {
        using var sim = new Simulation(Expendable);
        sim.Run(Climb, f => f.Ground > 200f, maxSeconds: 30);
        sim.Step(new FlightInput(Stage: true));

        var flight = sim.Flight;
        var upper = flight.Controlled;
        Assert.False(Dropped(flight).Guided);
        Assert.False(flight.CanSwitch);
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(upper, flight.Controlled);
    }

    [Fact]
    public void Switching_hands_the_controls_over_and_the_other_vessel_keeps_its_last_inputs()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        sim.Run(Climb, f => f.Ground > 300f, maxSeconds: 30);
        sim.Step(new FlightInput(Stage: true));
        var flight = sim.Flight;
        var upper = flight.Controlled;
        var booster = Dropped(flight);
        sim.Run((_, _) => default, _ => false, maxSeconds: Scene.StageIgnitionSeconds + 0.2);
        Assert.True(upper.EngineLit, "the upper stage should have lit at full throttle");

        // Over to the booster, and light its engine: the input goes to it now.
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(booster, flight.Controlled);
        var boosterFuel = booster.Fuel;
        var upperFuel = upper.Fuel;
        sim.Step(new FlightInput(FullThrottle: true));
        sim.Run((_, _) => default, _ => false, maxSeconds: 2);
        output.WriteLine($"booster fuel {boosterFuel:P1} -> {booster.Fuel:P1}, upper stage fuel {upperFuel:P1} -> {upper.Fuel:P1}");
        Assert.True(booster.EngineLit);
        Assert.True(booster.Fuel < boosterFuel);

        // Meanwhile the upper stage, not being flown, carries on burning at the throttle it was left at.
        Assert.Equal(1f, upper.Throttle);
        Assert.True(upper.EngineLit);
        Assert.True(upper.Fuel < upperFuel);

        // And back again.
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(upper, flight.Controlled);
    }

    [Fact]
    public void Losing_the_flown_vessel_does_not_end_the_flight_while_another_still_flies()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var crafts = sim.Physics.Crafts.Count;
        sim.Run(Climb, f => f.Ground > 300f, maxSeconds: 30);
        sim.Step(new FlightInput(Stage: true));
        var flight = sim.Flight;
        var upper = flight.Controlled;

        // Fly the booster, and do nothing with it: it falls back without its legs and is destroyed.
        sim.Step(new FlightInput(SwitchVessel: true));
        var booster = flight.Controlled;
        Assert.True(sim.Run((_, _) => default, _ => booster.State == FlightState.Crashed, maxSeconds: 120), "the booster never came down");
        output.WriteLine($"booster: {booster.CrashReason}");

        Assert.Equal(FlightState.Flying, flight.State); // the upper stage is still up there
        Assert.Equal(FlightState.Flying, upper.State);
        sim.Run((_, _) => default, _ => false, maxSeconds: Scene.WreckSeconds + 0.05); // left for its hit to play out, then gone
        Assert.Equal(crafts, sim.Physics.Crafts.Count); // the wreck is gone from the physics
        Assert.True(flight.CanSwitch);
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(upper, flight.Controlled);
    }

    [Fact]
    public void Starting_again_leaves_just_the_rocket_on_the_pad()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var crafts = sim.Physics.Crafts.Count;
        sim.Run(Climb, f => f.Ground > 300f, maxSeconds: 30);
        sim.Step(new FlightInput(Stage: true));
        sim.Step(new FlightInput(SwitchVessel: true));

        sim.Step(new FlightInput(Restart: true));
        Assert.Single(sim.Flight.Vessels);
        Assert.Equal(FlightState.Ready, sim.Flight.State);
        Assert.Equal(0, sim.Flight.Controlled.Id);
        Assert.Equal(crafts, sim.Physics.Crafts.Count);
    }
}
