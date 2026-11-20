using System.Collections.Immutable;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The warp's drops from a formula's rate to <see cref="Scene.PhysicsTopRate"/> (<see cref="WarpDrop"/>,
/// <see cref="Flight.LastDrop"/>): <see cref="Scene.WarpDropLeadSeconds"/> ahead of a witnessed
/// vessel's way into the air (or into reach of an airless body's ground), and of the flown vessel's
/// next node, once each; at once for a vessel at rest set moving, a steering key, or a vessel in
/// the air switched to or given a view; never climbing back by itself; and said in the readout.
/// </summary>
public sealed class WarpDropTests(ITestOutputHelper output)
{
    private static GroundBody Planet => GroundBody.Planet;

    /// <summary>Over level ground away from Home's pad (nothing under it but the ground).</summary>
    private static readonly Vector3 OverLevelGround = new(Drops.LevelGround.X, 0f, Drops.LevelGround.Z);

    /// <summary>
    /// The satellite launcher's upper stage, numbered <paramref name="id"/>, at the top of an orbit
    /// round <paramref name="body"/> from <paramref name="apoapsis"/> down to <paramref name="periapsis"/>
    /// (both over its datum), going east over its equator.
    /// </summary>
    private static VesselState Dipping(GroundBody body, int id, float apoapsis, float periapsis)
    {
        var state = InOrbit.Round(body, id, apoapsis - body.OrbitFloor);
        var centre = state.CentreOfMass.ToSingle();
        var stars = body.AmongStars(centre, state.Velocity.ToSingle());
        var (top, bottom) = (body.SeaRadius + apoapsis, body.SeaRadius + periapsis);
        return state with { Velocity = body.OverGround(centre, stars * MathF.Sqrt(2f * bottom / (top + bottom))) };
    }

    /// <summary>The orbit <paramref name="v"/> is on now, its engine off, from its centre of mass.</summary>
    private static Orbit OrbitOf(Vessel v) =>
        new(v.Body, v.CentreOfMass, v.Body.AmongStars(v.CentreOfMass, v.Velocity), findEnd: false);

    /// <summary>
    /// <paramref name="state"/> flown at the fastest rate, its real frames shortened so it comes
    /// down to its floor (<see cref="Formula.FloorRadius"/>) some <paramref name="frames"/> frames on.
    /// </summary>
    private static (Simulation Sim, Vessel Flown) ComingDown(VesselState state, double frames = 5.5)
    {
        var (sim, flown) = WarpScenes.Flying(state);
        var down = OrbitOf(flown).FallingTo(Formula.FloorRadius(flown))!.Value;
        sim.RealFrame = (float)(down / (frames * Scene.TopRate));
        FormulaRateTests.ToTheTop(sim.Flight);
        return (sim, flown);
    }

    /// <summary>Runs frames until the warp drops (at most <paramref name="frames"/>), returning how many it took.</summary>
    private static int UntilDropped(Simulation sim, int frames = 20, Func<FlightInput>? input = null)
    {
        for (var i = 1; i <= frames; i++)
        {
            sim.Step(input?.Invoke() ?? default);
            if (sim.Flight.LastDrop is not null)
                return i;
        }
        Assert.Fail($"the warp never dropped in {frames} frames");
        return 0;
    }

    [Fact]
    public void Coming_down_into_the_air_the_warp_drops_to_the_physics_top_rate_ahead_of_it()
    {
        var (sim, flown) = ComingDown(Dipping(Planet, 0, 6000f, 2000f));
        using var _ = sim;
        var flight = sim.Flight;
        var frames = UntilDropped(sim);
        var toTheAir = OrbitOf(flown).FallingTo(Planet.SeaRadius + Scene.AtmosphereHeight)!.Value;
        output.WriteLine($"dropped {frames} frames on, {flight.Altitude:0} m up, {toTheAir:0.000} s from the air's top (its floor {Formula.FloorRadius(flown) - Planet.SeaRadius:0} m up)");
        Assert.Equal(WarpDrop.Air, flight.LastDrop!.Value.Reason);
        Assert.Equal(WorldLog.Named(flown), flight.LastDrop.Value.Vessel);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.True(flight.Altitude > Scene.AtmosphereHeight);
        Assert.InRange(toTheAir, Scene.WarpDropLeadSeconds - 0.01, Scene.WarpDropLeadSeconds + Scene.PhysicsTopRate / 60.0);
        Assert.False(flown.OnFormula);
    }

    [Fact]
    public void At_the_moon_the_ground_coming_within_reach_drops_it_by_the_ground()
    {
        var moon = GroundBody.Moon;
        var (sim, flown) = ComingDown(Dipping(moon, 0, 3000f, 300f));
        using var _ = sim;
        var flight = sim.Flight;
        var frames = UntilDropped(sim);
        var toTheFloor = OrbitOf(flown).FallingTo(Formula.FloorRadius(flown))!.Value;
        output.WriteLine($"dropped {frames} frames on, {flight.Altitude:0} m up, {toTheFloor:0.000} s from its floor");
        Assert.Same(moon, flown.Body);
        Assert.Equal(WarpDrop.Ground, flight.LastDrop!.Value.Reason);
        Assert.Equal(WorldLog.Named(flown), flight.LastDrop.Value.Vessel);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.InRange(toTheFloor, Scene.WarpDropLeadSeconds - 0.01, Scene.WarpDropLeadSeconds + Scene.PhysicsTopRate / 60.0);
        Assert.False(flown.OnFormula);
    }

    [Fact]
    public void A_drop_never_climbs_back()
    {
        // A shallow dip, so the air's drag leaves it on an orbit out again (2,000 m down, it comes down to the ground).
        var (sim, flown) = ComingDown(Dipping(Planet, 0, 6000f, 2900f));
        using var _ = sim;
        var flight = sim.Flight;
        UntilDropped(sim);
        sim.RealFrame = Simulation.FrameSeconds;

        // Into the air and out again, its engine off: at the physics' top rate all the way.
        var (wasIn, frames) = (false, 0);
        while (!(wasIn && flown.AboveTheAir) && frames++ < 3000)
        {
            sim.Step(default);
            wasIn |= !flown.AboveTheAir;
            Assert.True(Scene.PhysicsTopRate == flight.PlayerRate, $"{flight.PlayerRate}x {frames} frames on, {flight.Altitude:0} m up, {flown.State}; warned of {flight.Warning?.Seconds}, warping to {flight.WarpingTo}");
        }
        output.WriteLine($"out of the air again {frames} frames on, {flight.Altitude:0} m up");
        Assert.True(wasIn && flown.AboveTheAir, "it should have flown into the air and out again");
        for (var i = 0; i < 300; i++)
        {
            sim.Step(default);
            Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        }
        Assert.Equal(FlightState.Flying, flown.State);
    }

    /// <summary>A booster landed at Home's landing zone, settled; with <paramref name="vehicle"/>, that vehicle instead.</summary>
    private static (Simulation Sim, Vessel Flown) LandedAtTheZone(Vehicle? vehicle = null)
    {
        var zone = Scene.LandingZoneCentre;
        var (sim, flown) = WarpScenes.Flying(Drops.Placed(zone, Planet.East(zone), 0f, vehicle, mission: Mission.FreePlay));
        Assert.True(sim.Run((_, _) => default, _ => flown.State == FlightState.Landed && flown.AtRest, maxSeconds: 30), "it should have landed and settled");
        return (sim, flown);
    }

    /// <summary>The lander launcher's lander alone, landed on its legs at the moon's landing spot, its bay shut (as <see cref="OnTheMoon.Lander"/> lands the upper stage).</summary>
    internal static VesselState LanderLanded()
    {
        var vehicle = Vehicle.LanderLauncher;
        const int Lander = 2;
        var (position, rotation) = OnTheMoon.Standing(OnTheMoon.LandingSpot, vehicle.Stages[Lander].LegGeometry);
        return OnTheMoon.Lander(0, OnTheMoon.LandingSpot) with
        {
            Vehicle = vehicle, First = Lander, Last = Lander, Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * 0.5f)],
            Position = position, Rotation = rotation, CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, Lander, Lander),
            Aim = Vector3.Transform(Vector3.UnitY, rotation),
        };
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("stage")]
    [InlineData("legs")]
    [InlineData("bay doors")]
    [InlineData("throttle")]
    public void Setting_a_vessel_at_rest_moving_drops_the_warp_before_it_moves(string action)
    {
        // Each on what it moves: a rocket on its pad launched, a two-stage rocket landed staged, a
        // lander on the moon its doors opened, a booster landed its legs or its throttle.
        Simulation sim;
        Vessel flown;
        switch (action)
        {
            case "launch":
                sim = new Simulation(Mission.FreePlay);
                sim.Step(default);
                flown = sim.Flight.Controlled;
                Assert.Equal(FlightState.Ready, flown.State);
                break;
            case "stage":
                (sim, flown) = LandedAtTheZone(Vehicle.TwoStage);
                break;
            case "bay doors":
                sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [LanderLanded()], NextVesselId = 1 }, body: GroundBody.Moon);
                flown = sim.Flight.WorldVessels.Single(v => v.Id == 0);
                sim.Step(new FlightInput(SwitchTo: flown));
                sim.Step(default);
                Assert.Equal(FlightState.Landed, flown.State);
                Assert.True(flown.HasBay);
                break;
            default:
                (sim, flown) = LandedAtTheZone();
                break;
        }
        using var _ = sim;
        var flight = sim.Flight;
        var (count, legs, doors, name) = (flight.WorldVessels.Count, flown.LegsWanted, flown.BayDoorsWanted, WorldLog.Named(flown)); // named as it was set moving: staged, it is named by what is left
        FormulaRateTests.ToTheTop(flight);
        sim.Step(default);
        Assert.True(flown.OnFormula);

        sim.Step(action switch
        {
            "launch" => new FlightInput(Launch: true),
            "stage" => new FlightInput(Stage: true),
            "legs" => new FlightInput(ToggleLegs: true),
            "bay doors" => new FlightInput(ToggleBayDoors: true),
            _ => new FlightInput(Throttle: 1f),
        });
        output.WriteLine($"{action}: {flight.PlayerRate}x, dropped for {flight.LastDrop?.Reason}");
        Assert.Equal(new WarpDropped(WarpDrop.SetMoving, name, Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.False(flown.OnFormula);
        // And the action taken, in the physics.
        switch (action)
        {
            case "launch":
                Assert.Equal(FlightState.Flying, flown.State);
                break;
            case "stage":
                Assert.Equal(count + 1, flight.WorldVessels.Count);
                break;
            case "legs":
                Assert.NotEqual(legs, flown.LegsWanted);
                break;
            case "bay doors":
                Assert.NotEqual(doors, flown.BayDoorsWanted);
                break;
            default:
                Assert.True(flown.Throttle > 0f);
                break;
        }
    }

    /// <summary>The angle between two directions, in degrees.</summary>
    private static float Degrees(Vector3 a, Vector3 b) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)));

    [Fact]
    public void A_steering_key_at_a_formula_rate_drops_the_warp_and_steers_as_at_normal_speed()
    {
        // The same low orbit twice: one at the fastest rate, one at the physics' top rate, each given
        // the same steering key for a frame.
        float Turned(bool fastest)
        {
            var (sim, flown) = WarpScenes.LowOrbit();
            using var _ = sim;
            var flight = sim.Flight;
            if (fastest)
                FormulaRateTests.ToTheTop(flight);
            else
                while (flight.PlayerRate < Scene.PhysicsTopRate)
                    flight.RunFrame(0f, new FlightInput(Faster: true));
            for (var i = 0; i < 3; i++)
                sim.Step(default);
            Assert.Equal(fastest, flown.OnFormula);
            var aim = flown.Aim;
            sim.Step(new FlightInput(Lean: flown.Right));
            Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
            Assert.False(flown.OnFormula);
            if (fastest)
                Assert.Equal(new WarpDropped(WarpDrop.Steering, WorldLog.Named(flown), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
            else
                Assert.Null(flight.LastDrop);
            return Degrees(aim, flown.Aim);
        }

        var (dropped, physics) = (Turned(fastest: true), Turned(fastest: false));
        output.WriteLine($"turned {dropped:0.000}° dropped from the fastest rate, {physics:0.000}° at the physics' top rate");
        Assert.True(physics > 0.1f, $"it should have turned ({physics:0.000}°)");
        Assert.InRange(dropped, 0.9f * physics, 1.1f * physics);
    }

    [Fact]
    public void A_node_coming_up_drops_the_warp_once()
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        var node = flight.WorldTime + 2000.0;
        sim.Step(new FlightInput(Plan: ManoeuvreChain.AnchoredOn(flown, node, [new ManoeuvreNode(0.0, Prograde: 20f)], flight.WorldTime)));
        Assert.Equal(node, flown.Chain!.TimeOf(0), 6);
        var burn = flight.Guide!.Value.BurnSeconds;
        Assert.True(burn > 0f && float.IsFinite(burn));
        sim.RealFrame = Simulation.TopRateFrame * 600f; // 80 s a frame: some 25 frames to the node
        FormulaRateTests.ToTheTop(flight);

        var frames = UntilDropped(sim, frames: 100);
        output.WriteLine($"dropped {frames} frames on, {node - flight.WorldTime:0.000} s before the node, its burn {burn:0.00} s");
        Assert.Equal(WarpDrop.Node, flight.LastDrop!.Value.Reason);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.False(flown.OnFormula);
        Assert.InRange(node - flight.WorldTime, Scene.WarpDropLeadSeconds + burn / 2 - 0.01, Scene.WarpDropLeadSeconds + burn / 2 + 0.01);

        // Faster again, at once: on past the node with no second drop for it.
        var dropped = flight.LastDrop;
        FormulaRateTests.ToTheTop(flight);
        Assert.True(sim.Run((_, _) => default, f => f.WorldTime > node + 60.0, maxSeconds: 1000));
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Equal(dropped, flight.LastDrop);
    }

    [Fact]
    public void A_node_deleted_and_made_again_drops_the_warp_again()
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        var node = flight.WorldTime + 2000.0;
        var chain = ManoeuvreChain.AnchoredOn(flown, node, [new ManoeuvreNode(0.0, Prograde: 20f)], flight.WorldTime);
        sim.Step(new FlightInput(Plan: chain));
        sim.RealFrame = Simulation.TopRateFrame * 600f;
        FormulaRateTests.ToTheTop(flight);
        UntilDropped(sim, frames: 100);
        Assert.Equal(WarpDrop.Node, flight.LastDrop!.Value.Reason);
        var first = flight.LastDrop;

        // Deleted at the physics' top rate, and made again the same (value for value) a frame later.
        sim.RealFrame = Simulation.FrameSeconds;
        sim.Step(new FlightInput(Plan: ManoeuvreChain.Empty));
        Assert.Null(flown.Chain);
        sim.Step(new FlightInput(Plan: chain));
        Assert.Equal(chain, flown.Chain);

        // Faster again, to the formula's first rate: dropped again for it in that frame, at once
        // (it is within its lead already).
        flight.RunFrame(0f, new FlightInput(Faster: true));
        output.WriteLine($"dropped again {node - flight.WorldTime:0.000} s before the node");
        Assert.Equal(WarpDrop.Node, flight.LastDrop!.Value.Reason);
        Assert.NotEqual(first, flight.LastDrop);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
    }

    /// <summary>At the physics' top rate, by the rate key pressed in frames of no time.</summary>
    private static void ToThePhysicsTop(Flight flight)
    {
        while (flight.PlayerRate < Scene.PhysicsTopRate)
            flight.RunFrame(0f, new FlightInput(Faster: true));
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
    }

    [Fact]
    public void Faster_from_the_physics_top_rate_with_a_launch_stays_there_and_launches_in_the_physics()
    {
        using var sim = new Simulation(Mission.FreePlay);
        sim.Step(default);
        var flight = sim.Flight;
        var flown = flight.Controlled;
        var stood = flown.Position;
        ToThePhysicsTop(flight);
        sim.Step(new FlightInput(Faster: true, Launch: true, FullThrottle: true));
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.False(flown.OnFormula);
        Assert.Equal(FlightState.Flying, flown.State);
        Assert.True(sim.Run((_, _) => default, _ => Vector3.Distance(flown.Position, stood) > 1f, maxSeconds: 5), "it never lifted off");
        Assert.False(flown.OnFormula);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Faster_or_a_warp_to_a_point_from_the_physics_top_rate_with_a_steering_key_stays_there(bool warpTo)
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        ToThePhysicsTop(flight);
        var aim = flown.Aim;
        sim.Step(warpTo ? new FlightInput(Lean: flown.Right, WarpTo: flight.WorldTime + 300.0) : new FlightInput(Lean: flown.Right, Faster: true));
        output.WriteLine($"{flight.PlayerRate}x, warping to {flight.WarpingTo}, turned {Degrees(aim, flown.Aim):0.000}°");
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.False(flown.OnFormula);
        Assert.True(Degrees(aim, flown.Aim) > 0.1f, "it should have steered, in the physics");
        if (warpTo)
            Assert.NotNull(flight.WarpingTo); // begun, at the physics' top rate
    }

    /// <summary>The satellite launcher's upper stage in a low orbit, flown, beside a booster falling from 500 m over level ground (numbered 1), the warp at its fastest.</summary>
    private static (Simulation Sim, Vessel Flown, Vessel Booster) BesideABoosterInTheAir()
    {
        var booster = Drops.Placed(OverLevelGround, GroundBody.Planet.East(OverLevelGround), 0f, clearance: 500f, mission: Mission.FreePlay, id: 1);
        var (sim, flown) = WarpScenes.Flying(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), booster);
        FormulaRateTests.ToTheTop(sim.Flight);
        var inTheAir = sim.Flight.WorldVessels.Single(v => v.Id == 1);
        Assert.True(inTheAir.OnFormula);
        return (sim, flown, inTheAir);
    }

    [Fact]
    public void A_switch_at_the_top_rate_to_a_vessel_in_the_air_drops_the_warp_and_flies_it_in_the_physics()
    {
        var (sim, _, booster) = BesideABoosterInTheAir();
        using var _s = sim;
        var flight = sim.Flight;
        // The switch dropping the warp as the frame it is asked in begins, and the booster flown
        // behind its link-up in the next.
        flight.RunFrame(0f, new FlightInput(SwitchTo: booster));
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        flight.RunFrame(0f, default);
        Assert.Same(booster, flight.Controlled);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Equal(new WarpDropped(WarpDrop.Air, WorldLog.Named(booster), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
        Assert.False(booster.OnFormula);

        var height = flight.Ground;
        sim.Step(default);
        output.WriteLine($"{height:0.0} m up, then {flight.Ground:0.0} m a frame at {flight.PlayerRate}x on");
        Assert.False(booster.OnFormula);
        Assert.Equal(FlightState.Flying, booster.State);
        Assert.True(flight.Ground < height, "it should be falling, in the physics");
    }

    [Fact]
    public void A_switch_asked_in_a_whole_frame_at_the_top_rate_drops_the_warp_before_the_frame_runs()
    {
        var (sim, flown, booster) = BesideABoosterInTheAir();
        using var _s = sim;
        var flight = sim.Flight;
        var (time, height) = (flight.WorldTime, booster.Body.SurfaceAltitude(booster.CentreOfMass));
        // A frame of a real frame's length: at the top rate some 270 s, time to bring the booster
        // down unwatched; dropped as it begins, it is some 0.13 s at the physics' top rate.
        flight.RunFrame(Simulation.FrameSeconds, new FlightInput(SwitchTo: booster));
        output.WriteLine($"{flight.WorldTime - time:0.000} s on at {flight.PlayerRate}x; {height:0.0} m up, then {booster.Body.SurfaceAltitude(booster.CentreOfMass):0.0} m");
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Equal(new WarpDropped(WarpDrop.Air, WorldLog.Named(booster), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
        Assert.InRange(flight.WorldTime - time, 0.0, Simulation.FrameSeconds * Scene.PhysicsTopRate + 1e-3);
        Assert.Equal(FlightState.Flying, booster.State);
        Assert.False(booster.OnFormula);
        Assert.Same(flown, flight.Controlled);

        flight.RunFrame(Simulation.FrameSeconds, default);
        Assert.Same(booster, flight.Controlled);
        Assert.Equal(FlightState.Flying, booster.State);
        Assert.False(booster.OnFormula);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
    }

    [Fact]
    public void A_switch_at_the_top_rate_to_a_body_with_no_physics_waits_on_its_physics_made_ahead()
    {
        var (sim, _) = WarpScenes.Flying(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), InOrbit.Round(GroundBody.Moon, 1, 400f));
        using var _s = sim;
        var flight = sim.Flight;
        var round = flight.WorldVessels.Single(v => v.Id == 1);
        FormulaRateTests.ToTheTop(flight);
        Assert.Null(sim.Worlds.At(GroundBody.Moon));
        flight.RunFrame(0f, new FlightInput(SwitchTo: round));
        var frames = 0;
        for (; flight.Controlled != round && frames < 2000; frames++)
        {
            Assert.True(flight.LinkUp.Waiting);
            Thread.Sleep(1);
            flight.RunFrame(0f, default);
        }
        output.WriteLine($"flown {frames} frames after the switch");
        Assert.Same(round, flight.Controlled);
        // Flown only once its body's physics was made ahead, so the drop never makes it in a frame.
        Assert.NotNull(sim.Worlds.At(GroundBody.Moon));
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
    }

    [Fact]
    public void A_small_view_opened_at_the_top_rate_on_a_vessel_in_the_air_drops_the_warp()
    {
        var (sim, flown, booster) = BesideABoosterInTheAir();
        using var _s = sim;
        var flight = sim.Flight;
        flight.OpenView(booster); // as the HUD opens one, between frames
        flight.RunFrame(0f, new FlightInput(Watched: [booster]));
        Assert.Same(flown, flight.Controlled);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Equal(new WarpDropped(WarpDrop.Air, WorldLog.Named(booster), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
        Assert.False(booster.OnFormula);
        Assert.False(flown.OnFormula);
    }

    [Fact]
    public void The_readout_says_why_the_warp_dropped()
    {
        var (sim, flown) = ComingDown(Dipping(Planet, 0, 6000f, 2000f));
        using var _ = sim;
        var flight = sim.Flight;
        Assert.False(Readout.FlightReadings(flight).ContainsKey("Warp dropped"));
        UntilDropped(sim);
        sim.RealFrame = Simulation.FrameSeconds;
        var said = $"Air {WorldLog.Named(flown)} to {Scene.PhysicsTopRate:0}x";
        Assert.Equal("Air Upper stage 0 to 8x", said);
        var since = flight.LastDrop!.Value.RealTime;
        while (flight.RealTime - since <= Scene.WarpDropShownSeconds - Simulation.FrameSeconds)
        {
            Assert.Equal(said, Readout.FlightReadings(flight).GetValueOrDefault("Warp dropped"));
            sim.Step(default);
        }
        for (var i = 0; i < 3; i++)
            sim.Step(default);
        Assert.False(Readout.FlightReadings(flight).ContainsKey("Warp dropped"));
    }
}
