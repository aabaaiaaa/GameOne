using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The gate on the formula's rates (<see cref="Flight.WarpHeldBy"/>): while a witnessed vessel (the
/// flown one, a small view's, the main camera's) flies in the air, low over the ground, or isn't
/// yet at rest, the rate keys go no faster than <see cref="Scene.PhysicsTopRate"/>, the time line
/// saying by which; one seen at a formula's rate drops the warp there (<see cref="Flight.LastDrop"/>),
/// and nothing raises it again but the keys.
/// </summary>
public sealed class WarpGateTests(ITestOutputHelper output)
{
    /// <summary>Over level ground away from Home's pad (nothing under it but the ground).</summary>
    private static readonly Vector3 OverLevelGround = new(Drops.LevelGround.X, 0f, Drops.LevelGround.Z);

    /// <summary>A booster 500 m up, falling from rest, numbered <paramref name="id"/>.</summary>
    private static VesselState BoosterUp(int id) =>
        Drops.Placed(OverLevelGround, Planet.East(OverLevelGround), 0f, clearance: 500f, mission: Mission.FreePlay, id: id);

    /// <summary>The rate key pressed <paramref name="times"/> times, each in a frame of no time (so a formula's rate runs no steps).</summary>
    private static void Press(Flight flight, int times, bool faster = true, IReadOnlyList<Vessel>? watched = null, Vessel? watching = null)
    {
        for (var i = 0; i < times; i++)
            flight.RunFrame(0f, new FlightInput(Slower: !faster, Faster: faster, Watched: watched, Watching: watching));
    }

    [Fact]
    public void In_the_air_the_rate_keys_stop_at_the_physics_top_rate_and_say_who_holds_it()
    {
        var (sim, flown) = WarpScenes.Flying(BoosterUp(0));
        using var _ = sim;
        var flight = sim.Flight;
        for (var i = 0; i < 20; i++)
            sim.Step(new FlightInput(Faster: true));
        output.WriteLine($"{flight.PlayerRate}x, held by {flight.WarpHeldBy?.Name}, {flight.Ground:0} m up");
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Same(flown, flight.WarpHeldBy);
        Assert.Null(flight.LastDrop); // held, never at a formula's rate to drop from
    }

    [Fact]
    public void A_small_views_vessel_in_the_air_holds_it_at_the_physics_top_rate()
    {
        var (sim, flown) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, WarpScenes.LowOrbitHeight), BoosterUp(1));
        using var _ = sim;
        var flight = sim.Flight;
        var booster = flight.WorldVessels.Single(v => v.Id == 1);
        Assert.Null(flight.WarpHeldBy); // the satellite above the air holds nothing
        Press(flight, 20, watched: [booster]);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Same(booster, flight.WarpHeldBy);
        Assert.Same(flown, flight.Controlled);
    }

    [Fact]
    public void The_main_cameras_vessel_in_the_air_holds_it_too()
    {
        var (sim, _) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, WarpScenes.LowOrbitHeight), BoosterUp(1));
        using var _s = sim;
        var flight = sim.Flight;
        var booster = flight.WorldVessels.Single(v => v.Id == 1);
        Press(flight, 20, watching: booster);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Same(booster, flight.WarpHeldBy);
    }

    [Fact]
    public void Waiting_on_the_pad_or_landed_on_the_moon_nothing_holds_it()
    {
        using (var sim = new Simulation(Mission.FreePlay))
        {
            sim.Step(default);
            Assert.Equal(FlightState.Ready, sim.Flight.Controlled.State);
            Press(sim.Flight, 20);
            Assert.Equal(Scene.TopRate, sim.Flight.PlayerRate);
            Assert.Null(sim.Flight.WarpHeldBy);
        }

        var moon = GroundBody.Moon;
        var lander = OnTheMoon.Lander(0, OnTheMoon.LandingSpot);
        using var onTheMoon = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [lander], NextVesselId = 1 }, body: moon);
        var flown = onTheMoon.Flight.WorldVessels.Single(v => v.Id == 0);
        onTheMoon.Step(new FlightInput(SwitchTo: flown));
        onTheMoon.Step(default);
        Assert.Same(flown, onTheMoon.Flight.Controlled);
        Assert.Equal(FlightState.Landed, flown.State);
        Press(onTheMoon.Flight, 20);
        Assert.Equal(Scene.TopRate, onTheMoon.Flight.PlayerRate);
        Assert.Null(onTheMoon.Flight.WarpHeldBy);
    }

    [Fact]
    public void A_vessel_not_yet_at_rest_holds_it()
    {
        // Coming down at 1 m/s from just over level ground: touched down, but not yet settled
        // (Scene.LandedSettleSeconds), so not yet at rest.
        var state = Drops.Placed(OverLevelGround, Planet.East(OverLevelGround), 1f, clearance: 0.05f, mission: Mission.FreePlay);
        var (sim, flown) = WarpScenes.Flying(state);
        using var _ = sim;
        for (var i = 0; i < 30; i++)
            sim.Step(default);
        Assert.Equal(FlightState.Flying, flown.State);
        Assert.False(flown.AtRest);
        Press(sim.Flight, 20);
        Assert.Equal(Scene.PhysicsTopRate, sim.Flight.PlayerRate);
        Assert.Same(flown, sim.Flight.WarpHeldBy);
        Assert.Equal($"{WorldLog.Named(flown)} not yet at rest", sim.Flight.WarpHeld);
    }

    [Fact]
    public void A_vessel_settling_in_the_water_holds_it()
    {
        // A booster just over the deep sea, coming down at 2 m/s: in the water and settling, not
        // yet afloat, so not yet at rest.
        var water = Planet.Centre + WaterTests.DeepWater.Value * Planet.SeaRadius;
        var (sim, flown) = WarpScenes.Flying(Drops.Placed(water, Planet.East(water), 2f, mission: Mission.FreePlay));
        using var _ = sim;
        Assert.True(sim.Run((_, _) => default, _ => flown.Craft.Settling, maxSeconds: 5), "it never came down in the water");
        Press(sim.Flight, 20);
        Assert.Equal(Scene.PhysicsTopRate, sim.Flight.PlayerRate);
        Assert.Same(flown, sim.Flight.WarpHeldBy);
        Assert.Equal($"{WorldLog.Named(flown)} not yet at rest", sim.Flight.WarpHeld);
        // Settled afloat (and no longer settling), at rest: it holds nothing.
        Assert.True(sim.Run((_, _) => default, _ => flown.State == FlightState.Floating && !flown.Craft.Settling, maxSeconds: 60), $"still {flown.State}");
        Press(sim.Flight, 20);
        Assert.Null(sim.Flight.WarpHeldBy);
        Assert.Equal(Scene.TopRate, sim.Flight.PlayerRate);
    }

    [Fact]
    public void Climbing_out_of_the_air_at_the_physics_top_rate_stays_there()
    {
        // The upper stage 2,900 m up over Home, coasting straight up at 300 m/s, its engine off.
        var state = InOrbit.UpperStage(0, 2900f, 0f);
        var centre = state.CentreOfMass.ToSingle();
        state = InOrbit.At(state, centre, Planet.AmongStars(centre, Planet.Up(centre) * 300f));
        var (sim, flown) = WarpScenes.Flying(state);
        using var _ = sim;
        var flight = sim.Flight;
        Press(flight, 5);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Same(flown, flight.WarpHeldBy);

        Assert.True(sim.Run((_, _) => default, _ => flown.AboveTheAir, maxSeconds: 30), "it should have climbed out of the air");
        for (var i = 0; i < 30; i++)
        {
            sim.Step(default);
            Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate); // never back up by itself
            Assert.Null(flight.WarpHeldBy);
        }
        output.WriteLine($"{flight.Altitude:0} m up at {flight.PlayerRate}x");

        // Above it, faster goes on to the formula's rates.
        Press(flight, 1);
        Assert.Equal(2f * Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.True(flight.ByFormula);
    }

    [Fact]
    public void A_vessel_in_the_air_seen_at_a_formula_rate_drops_the_warp_to_the_physics_top_rate()
    {
        var (sim, _) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, WarpScenes.LowOrbitHeight), BoosterUp(1));
        using var _s = sim;
        var flight = sim.Flight;
        var booster = flight.WorldVessels.Single(v => v.Id == 1);
        Press(flight, 20);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);

        // Given a small view: the warp drops to the physics' top rate, by its name, and stays there.
        Press(flight, 1, watched: [booster]);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Equal(new WarpDropped(WarpDrop.Air, WorldLog.Named(booster), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
        Assert.Equal($"WARP DROPPED to 8x: {WorldLog.Named(booster)} coming into the air",
            Hud.TimeLine(null, flight.PlayerRate, flight.TimeScale, flight.TimeScale, flight.WarpHeld, flight.LastDrop, flight.RealTime)!.Value.Text);
        Press(flight, 5, watched: [booster]);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
    }

    [Fact]
    public void Throttling_up_in_orbit_at_a_formula_rate_drops_nothing_and_the_lit_engine_holds_nothing()
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        Press(flight, 20);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);

        // The throttle raised: not a drop (only a vessel at rest is set moving by it).
        flight.RunFrame(Simulation.TopRateFrame, new FlightInput(FullThrottle: true));
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Null(flight.LastDrop);
        Assert.True(flown.EngineLit);

        // Lit above the air, it holds nothing: the formula flies its burn.
        flight.RunFrame(0f, default);
        output.WriteLine($"{flight.PlayerRate}x, dropped for {flight.LastDrop?.Reason}");
        Assert.Null(flight.WarpHeldBy);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Null(flight.LastDrop);
        Assert.True(flown.OnFormula);
    }

    [Fact]
    public void Set_moving_from_rest_at_a_formula_rate_drops_it()
    {
        using var sim = new Simulation(Mission.FreePlay);
        sim.Step(default);
        var flight = sim.Flight;
        Press(flight, 20);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        flight.RunFrame(0f, new FlightInput(FullThrottle: true));
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Equal(new WarpDropped(WarpDrop.SetMoving, WorldLog.Named(flight.Controlled), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_vessel_watched_whose_link_up_still_waits_holds_it_from_the_first_frame(bool smallView)
    {
        // A booster falling 500 m over the moon, where no physics is made: watched from a low orbit
        // round the planet, its link-up waits a frame or more for the moon's physics.
        var moon = GroundBody.Moon;
        var at = moon.Centre + OnTheMoon.LandingSpot * moon.SeaRadius;
        var booster = Drops.Placed(at, moon.East(at), 0f, clearance: 500f, mission: Mission.FreePlay, id: 1, body: moon);
        var (sim, _) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, WarpScenes.LowOrbitHeight), booster);
        using var _s = sim;
        var flight = sim.Flight;
        var watched = flight.WorldVessels.Single(v => v.Id == 1);
        Press(flight, 20);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);

        if (smallView)
        {
            flight.OpenView(watched); // as the HUD opens one, between frames
            flight.RunFrame(0f, new FlightInput(Watched: [watched]));
            Assert.True(flight.ViewLink(watched)?.Waiting, "its link-up should still wait");
        }
        else
        {
            flight.RunFrame(0f, new FlightInput(Watching: watched));
            Assert.True(flight.WatchLink.Waiting, "its link-up should still wait");
        }
        Assert.Same(watched, flight.WarpHeldBy);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Equal(WarpDrop.Ground, flight.LastDrop?.Reason); // the moon has no air
    }

    [Fact]
    public void The_time_line_says_who_holds_the_warp_and_why_it_dropped()
    {
        Assert.Equal(("WARP 8x  (held: Booster 1 flying in the air)   ( , and . )", false), Hud.TimeLine(null, 8f, 8f, 8f, "Booster 1 flying in the air", null, 0));
        Assert.Equal(("WARP 8x  (held: Lander 2 flying low over the ground)   ( , and . )", false), Hud.TimeLine(null, 8f, 8f, 8f, "Lander 2 flying low over the ground", null, 0));
        var dropped = new WarpDropped(WarpDrop.Air, "Upper stage", 8f, 10.0);
        Assert.Equal("WARP DROPPED to 8x: Upper stage coming into the air", Hud.TimeLine(null, 8f, 8f, 8f, null, dropped, 11.0)!.Value.Text);
        Assert.Equal(("WARP 8x   ( , and . )", true), Hud.TimeLine(null, 8f, 8f, 8f, null, dropped, 10.0 + Scene.WarpDropShownSeconds + 0.1));
        Assert.Equal(("WARP 16384x   ( , and . )", true), Hud.TimeLine(null, 16384f, 16384f, 16384f, null, null, 0));
        // Once the player has changed the rate, the drop is no longer said.
        Assert.Equal(("WARP 4x   ( , and . )", true), Hud.TimeLine(null, 4f, 4f, 4f, null, dropped, 11.0));

        string Why(WarpDrop reason) => new WarpDropped(reason, "Booster 1", 8f, 0).Why;
        Assert.Equal("Booster 1 coming into the air", Why(WarpDrop.Air));
        Assert.Equal("Booster 1 near the ground", Why(WarpDrop.Ground));
        Assert.Equal("Booster 1 set moving", Why(WarpDrop.SetMoving));
        Assert.Equal("steering", Why(WarpDrop.Steering));
        Assert.Equal("a collision coming at Booster 1", Why(WarpDrop.Collision));
        Assert.Equal("a near miss coming at Booster 1", Why(WarpDrop.NearMiss));
        Assert.Equal("a node coming up", Why(WarpDrop.Node));
        Assert.Equal("Booster 1's fuel running out", Why(WarpDrop.FuelOut));
    }
}
