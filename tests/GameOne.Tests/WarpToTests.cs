using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A right click on the flown vessel's path warps to that point (<see cref="FlightInput.WarpTo"/>,
/// <see cref="Flight.WarpingTo"/>): the fastest rate at once, and normal speed within a step of
/// the time asked, the vessel where its path said it would be; ended early, at normal speed, by a
/// rate key, a switch, a warning (a collision's or a near miss's), a landing, the vessel wrecked
/// or a world loaded; under a slow-down no faster than it; no later than the path goes, burning
/// or not; and the HUD's hit on the drawn line (up to the first node), which an object under the
/// pointer, or a press that deleted a node, wins over.
/// </summary>
public sealed class WarpToTests(ITestOutputHelper output)
{
    /// <summary>The fastest rate, for a warp begun where nothing holds it (above the air).</summary>
    private static readonly float Fastest = Flight.Rates[^1];

    /// <summary>The fastest for a warp begun in the air, or low over the ground: held to the physics' top rate (<see cref="Flight.WarpHeldBy"/>).</summary>
    private static readonly float InTheAir = Scene.PhysicsTopRate;

    private static List<PathLeg> PathOf(Vessel vessel, double now)
    {
        var legs = new List<PathLeg>(Scene.PatchedLegs);
        Patched.Legs(vessel.Body, vessel.CentreOfMass, vessel.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity), now, legs);
        return legs;
    }

    [Fact]
    public void It_warps_at_the_fastest_rate_and_stops_within_a_step_of_the_time_where_the_path_said()
    {
        var (sim, flown) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f));
        using var _ = sim;
        var flight = sim.Flight;
        var now = flight.WorldTime;
        var to = now + 40.0;
        var legs = PathOf(flown, now);
        var expected = SolarEdgeTests.GroundAt(legs[0], to).ToSingle();

        // At the fastest rate a frame is 273 s: there in the first, its last window cut at the time.
        sim.Step(new FlightInput(WarpTo: to));
        var frames = 1;
        while (flight.WarpingTo is not null && frames < 1000)
        {
            sim.Step(default);
            frames++;
        }
        output.WriteLine($"{frames} frames; stopped at {flight.WorldTime - to:+0.000000;-0.000000} s from the time asked; {Vector3.Distance(flown.CentreOfMass, expected):0.00} m from where the path had it");
        Assert.Null(flight.WarpingTo);
        Assert.Equal(1f, flight.PlayerRate);
        Assert.InRange(flight.WorldTime, to - 1e-4, to + 1e-4);
        Assert.Equal((int)Math.Ceiling(40.0 / (Fastest * sim.RealFrame)), frames);
        Assert.True(Vector3.Distance(flown.CentreOfMass, expected) < 5f, "where its path said it would be then");

        // At normal speed from there on.
        var before = flight.WorldTime;
        sim.Step(default);
        Assert.Equal(sim.RealFrame, flight.WorldTime - before, 5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_rate_key_ends_it_at_normal_speed(bool slower)
    {
        var (sim, _) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f));
        using var _s = sim;
        sim.RealFrame = Simulation.TopRateFrame; // many frames to the time, as at the physics' top rate, not one of minutes
        var flight = sim.Flight;
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 300.0));
        sim.Step(default);
        Assert.NotNull(flight.WarpingTo);
        sim.Step(new FlightInput(Slower: slower, Faster: !slower));
        Assert.Null(flight.WarpingTo);
        Assert.Equal(1f, flight.PlayerRate);
    }

    [Fact]
    public void A_switch_to_another_vessel_ends_it_at_normal_speed()
    {
        var other = InOrbit.Round(GroundBody.Planet, 1, 2000f);
        other = other with { Position = other.Position + new Vector3(0f, 0f, 3000f) };
        var (sim, _) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f), other);
        using var _s = sim;
        sim.RealFrame = Simulation.TopRateFrame; // many frames to the time, as at the physics' top rate, not one of minutes
        var flight = sim.Flight;
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 300.0));
        sim.Step(default);
        Assert.NotNull(flight.WarpingTo);
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.Single(v => v.Id == 1)));
        Assert.Equal(1, flight.Controlled.Id);
        Assert.Null(flight.WarpingTo);
        Assert.Equal(1f, flight.PlayerRate);
    }

    [Fact]
    public void Past_the_ground_it_stops_where_the_path_comes_down_and_the_air_and_a_warning_end_it_first()
    {
        // Falling from 6 km at a third of the speed that would hold it there: down within the minute.
        var (sim, flown) = WarpScenes.Flying(InOrbit.UpperStage(0, 6000f, 0.3f));
        using var _ = sim;
        sim.RealFrame = Simulation.TopRateFrame; // many frames to the time, as at the physics' top rate, not one of minutes
        var flight = sim.Flight;
        var now = flight.WorldTime;
        var down = Patched.Ends(PathOf(flown, now));
        Assert.True(double.IsFinite(down) && down > now + 15.0, $"comes down {down - now:0.0} s on");

        sim.Step(new FlightInput(WarpTo: now + 1e6));
        Assert.Equal(down, flight.WarpingTo!.Value, 0); // no later than the path goes
        Assert.Equal(Fastest, flight.PlayerRate); // above the air, nothing holds it

        // The air coming up drops it to the physics' top rate, and so ends it.
        Assert.True(sim.Run((_, _) => default, f => f.WarpingTo is null, maxSeconds: 300));
        output.WriteLine($"ended {down - flight.WorldTime:0.0} s short, {flight.Altitude:0} m up, dropped for {flight.LastDrop?.Reason}");
        Assert.Equal(WarpDrop.Air, flight.LastDrop?.Reason);
        Assert.Equal(WorldLog.Named(flown), flight.LastDrop?.Vessel);
        Assert.Equal(InTheAir, flight.PlayerRate);
        Assert.Null(flight.Warning);
        Assert.Equal(FlightState.Flying, flown.State);

        // Asked again, at the physics' top rate in the air (the drop came its lead ahead of the
        // air, so on into it first): the ground ahead is warned of, which drops the warp, and so
        // ends it, before it is reached.
        sim.RealFrame = Simulation.FrameSeconds;
        Assert.True(sim.Run((_, _) => default, _ => !flown.AboveTheAir, maxSeconds: 2 * Scene.WarpDropLeadSeconds));
        sim.Step(new FlightInput(WarpTo: down));
        Assert.Equal(InTheAir, flight.PlayerRate);
        Assert.True(sim.Run((_, _) => default, f => f.WarpingTo is null, maxSeconds: 300));
        output.WriteLine($"ended {down - flight.WorldTime:0.0} s short, warned of {flight.Warning?.With}");
        Assert.NotNull(flight.Warning);
        Assert.Equal(1f, flight.PlayerRate);
        Assert.Equal(FlightState.Flying, flown.State);

        // Asked again (that warning drops the warp only once), it runs on to where the path comes
        // down (the air has held it back: it is still a little up).
        sim.Step(new FlightInput(WarpTo: down));
        Assert.Equal(InTheAir, flight.PlayerRate);
        Assert.True(sim.Run((_, _) => default, f => f.WarpingTo is null, maxSeconds: 300));
        output.WriteLine($"{flown.State} {flight.WorldTime - down:+0.00;-0.00} s from the path's end");
        Assert.Equal(1f, flight.PlayerRate);
        Assert.True(flown.State == FlightState.Crashed || Math.Abs(flight.WorldTime - down) < Flight.WarpStepSeconds);
    }

    [Fact]
    public void The_vessel_wrecked_ends_it_at_normal_speed()
    {
        // Nose down 500 m over the ground by Home, its engine driving it down: into the ground
        // before its path (with the engine off) says, and slower than a fast collision, so unwarned.
        var state = InOrbit.UpperStage(0, Scene.HomeElevation + 500f, 1f);
        var centre = state.CentreOfMass.ToSingle();
        var up = Planet.Up(centre);
        state = InOrbit.At(state, centre, Planet.AmongStars(centre, Vector3.Zero),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI) * Planet.UprightAt(centre)) with { Aim = -up };
        var (sim, flown) = WarpScenes.Flying(state);
        using var _ = sim;
        var flight = sim.Flight;
        sim.Step(new FlightInput(FullThrottle: true));
        var down = Patched.Ends(PathOf(flown, flight.WorldTime));
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 1e6));
        Assert.Equal(InTheAir, flight.PlayerRate);
        Assert.True(sim.Run((_, _) => default, f => f.WarpingTo is null, maxSeconds: 60));
        output.WriteLine($"{flown.State} ({flown.CrashReason}) {down - flight.WorldTime:0.00} s before the path's end; warned of {flight.Warning?.With.ToString() ?? "nothing"}");
        Assert.Equal(FlightState.Crashed, flown.State);
        Assert.True(flight.WorldTime < down - Flight.WarpStepSeconds, "ended by the wreck, not by getting there");
        Assert.Equal(1f, flight.PlayerRate);
    }

    [Fact]
    public void Past_the_solar_system_s_edge_it_stops_there()
    {
        // Leaving at 3 km/s from 20 km up: its path ends at the edge, days on.
        var state = InOrbit.UpperStage(0, 20000f, 1f);
        var centre = state.CentreOfMass.ToSingle();
        state = InOrbit.At(state, centre, Planet.East(centre) * 3000f + Planet.Up(centre) * 1000f);
        var (sim, flown) = WarpScenes.Flying(state);
        using var _ = sim;
        sim.RealFrame = Simulation.TopRateFrame; // many frames to the time, as at the physics' top rate, not one of minutes
        var flight = sim.Flight;
        var legs = PathOf(flown, flight.WorldTime);
        Assert.True(legs[^1].LeavesSystem);
        sim.Step(new FlightInput(WarpTo: double.MaxValue));
        Assert.Equal(legs[^1].End, flight.WarpingTo!.Value, 0);
    }

    [Fact]
    public void A_time_behind_or_a_vessel_not_flying_warps_nowhere()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var flight = sim.Flight;
        sim.Step(default);
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 60.0)); // the new rocket, on its pad
        Assert.Null(flight.WarpingTo);
        Assert.Equal(1f, flight.PlayerRate);

        var (inOrbit, _) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f));
        using var _s = inOrbit;
        inOrbit.Step(new FlightInput(WarpTo: inOrbit.Flight.WorldTime - 1.0));
        Assert.Null(inOrbit.Flight.WarpingTo);
        Assert.Equal(1f, inOrbit.Flight.PlayerRate);
    }

    [Fact]
    public void The_hit_is_the_nearest_point_on_the_drawn_line_within_the_figure_and_nothing_beyond()
    {
        Vector2[] points = [new(0, 0), new(100, 0), new(100, 100)];
        bool[] all = [true, true, true];
        var within = Scene.PathPixels;

        Assert.Equal(1, NodeEditor.NearestPiece(points, all, new Vector2(30, 5), within, out var t, out var distance));
        Assert.Equal(0.3f, t, 4);
        Assert.Equal(5f, distance, 4);
        // Near both pieces: the nearer.
        Assert.Equal(2, NodeEditor.NearestPiece(points, all, new Vector2(97, 20), within, out t, out distance));
        Assert.Equal(0.2f, t, 4);
        Assert.Equal(3f, distance, 4);
        // Just beyond the figure, nothing.
        Assert.Equal(-1, NodeEditor.NearestPiece(points, all, new Vector2(30, within + 0.5f), within, out _, out _));
        // Past the line's end, measured to the end itself.
        Assert.Equal(-1, NodeEditor.NearestPiece(points, all, new Vector2(-within - 0.5f, 0), within, out _, out _));
        Assert.Equal(1, NodeEditor.NearestPiece(points, all, new Vector2(-within + 0.5f, 0), within, out t, out _));
        Assert.Equal(0f, t);
        // A point not drawn (behind a body, behind the camera) has no piece to or from it.
        Assert.Equal(-1, NodeEditor.NearestPiece(points, [true, false, true], new Vector2(30, 1), within, out _, out _));
        Assert.Equal(-1, NodeEditor.NearestPiece(points, [true, false, true], new Vector2(100, 50), within, out _, out _));
    }

    [Fact]
    public void An_object_under_the_pointer_wins_over_the_path()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var vessel = sim.Flight.Controlled;
        Assert.Equal(123.0, Hud.RightClickWarp(null, null, 123.0));
        Assert.Null(Hud.RightClickWarp(new CameraCentre(CentreKind.Body, 1), null, 123.0)); // a marker: a body's, a vessel's, a site's
        Assert.Null(Hud.RightClickWarp(null, vessel, 123.0)); // a vessel, the flown one's own included
        Assert.Null(Hud.RightClickWarp(null, null, null)); // off the path
    }

    [Theory]
    [InlineData(5000.0)] // its clock long past the warp's time
    [InlineData(-30.0)]  // and before it
    public void A_world_loaded_mid_warp_drops_it_and_its_clock_never_goes_back(double clockFromNow)
    {
        var (sim, _) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f));
        using var _s = sim;
        sim.RealFrame = Simulation.TopRateFrame; // many frames to the time, as at the physics' top rate, not one of minutes
        var flight = sim.Flight;
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 300.0));
        sim.Step(default);
        Assert.NotNull(flight.WarpingTo);
        var world = flight.Capture() with { Time = Math.Max(0.0, flight.WorldTime + clockFromNow) };
        flight.Restore(world);
        Assert.Null(flight.WarpingTo);
        Assert.Equal(1f, flight.PlayerRate);
        var loaded = flight.WorldTime;
        sim.Step(default);
        Assert.Equal(loaded + sim.RealFrame, flight.WorldTime, 5); // one frame on at normal speed, never back
    }

    [Fact]
    public void A_landing_ends_it_at_normal_speed()
    {
        // A booster coming down slowly onto the moon (its weak pull: the path, to its middle meeting
        // the ground, ends well after its feet touch down and it settles).
        var at = MoonLandingTests.LandingSpot();
        var moon = GroundBody.Moon;
        var state = Drops.Placed(at, moon.East(at), 0.5f, clearance: 0.1f, mission: Mission.FreePlay, body: moon);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 }, body: moon);
        var flown = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: flown));
        var flight = sim.Flight;
        var down = Patched.Ends(PathOf(flown, flight.WorldTime));
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 1e6));
        Assert.Equal(InTheAir, flight.PlayerRate);
        Assert.True(sim.Run((_, _) => default, f => f.WarpingTo is null, maxSeconds: 60));
        output.WriteLine($"{flown.State} {down - flight.WorldTime:0.00} s before its path's end");
        Assert.Equal(FlightState.Landed, flown.State);
        Assert.True(flight.WorldTime < down - Flight.WarpStepSeconds, "ended by the landing, not by getting there");
        Assert.Equal(1f, flight.PlayerRate);
    }

    [Fact(Skip = "At a formula's rate no look ahead runs yet: the witnessed vessels' collisions and near misses ahead of the warp come with Collisions.AheadOfTheWarp")]
    public void A_near_miss_warning_ends_it_at_normal_speed()
    {
        // A pass 1,000 m/s beside the flown booster 25 s ahead, past the look ahead's horizon
        // until the warp has run a few seconds.
        var (sim, flown, other) = FastCollisionTests.Fly(NearMissTests.NearMissPass(1000f, seconds: 25f));
        using var _ = sim;
        sim.RealFrame = Simulation.TopRateFrame; // many frames to the time, as at the physics' top rate, not one of minutes
        var flight = sim.Flight;
        var to = flight.WorldTime + 24.0;
        sim.Step(new FlightInput(WarpTo: to, NearMisses: true));
        Assert.Equal(Fastest, flight.PlayerRate);
        Assert.True(sim.Run((_, _) => new FlightInput(NearMisses: true), f => f.WarpingTo is null, maxSeconds: 30));
        output.WriteLine($"ended {to - flight.WorldTime:0.0} s short; near miss {flight.NearMissWarning}; warning {flight.Warning}");
        Assert.NotNull(flight.NearMissWarning);
        Assert.Same(other, flight.NearMissWarning!.Value.Other);
        Assert.True(flight.WorldTime < to - 1.0);
        Assert.Equal(1f, flight.PlayerRate);
        Assert.Same(flown, flight.Controlled);
    }

    [Fact]
    public void Under_a_slow_down_it_runs_at_the_slow_down_s_rate_and_still_stops_on_the_time()
    {
        // The near miss slowed for (its warning already given, and the warp it dropped), then a
        // warp to a moment a little on: no faster than the slow-down allows, and stopped on it.
        var (sim, _, _) = FastCollisionTests.Fly(NearMissTests.NearMissPass(1000f, seconds: 5f));
        using var _s = sim;
        var flight = sim.Flight;
        Assert.True(sim.Run((_, _) => new FlightInput(NearMisses: true), f => f.Slowed is not null, maxSeconds: 10));
        var to = flight.WorldTime + 0.05;
        sim.Step(new FlightInput(WarpTo: to, NearMisses: true));
        var frames = 0;
        while (flight.WarpingTo is not null && frames++ < 5000)
        {
            Assert.True(flight.Slowed is null || flight.TimeScale <= flight.Slowed.Value.Scale, "no faster than the slow-down");
            sim.Step(new FlightInput(NearMisses: true));
        }
        output.WriteLine($"{frames} frames; stopped {flight.WorldTime - to:+0.000000;-0.000000} s from the time asked");
        Assert.Null(flight.WarpingTo);
        Assert.InRange(flight.WorldTime, to - Flight.WarpStepSeconds, to + 1e-4);
        Assert.Equal(1f, flight.PlayerRate);
    }

    [Fact]
    public void While_it_burns_the_warp_is_held_to_where_its_path_now_comes_down()
    {
        // Round a low orbit, burning retrograde: its path comes down sooner and sooner, and the
        // warp is held to where it does, never run on past it.
        var (sim, flown) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f));
        using var _ = sim;
        var flight = sim.Flight;
        sim.Step(new FlightInput(ChooseHold: HoldMode.Retrograde));
        sim.Run((_, _) => default, _ => false, maxSeconds: 5); // turned round
        var to = flight.WorldTime + 1e6;
        Assert.True(double.IsPositiveInfinity(Patched.Ends(PathOf(flown, flight.WorldTime))), "round for ever before the burn");
        sim.Step(new FlightInput(FullThrottle: true)); // lit first: a frame at a formula's rate never runs a witnessed burn, so it warps at the physics' top rate
        sim.Step(new FlightInput(WarpTo: to));
        Assert.Equal(InTheAir, flight.PlayerRate);
        Assert.Equal(to, flight.WarpingTo); // round for ever: nothing to hold it to yet
        // Held once a frame, as the frame begins: to where the path, as the last frame left it, ends.
        var (held, ending) = (false, double.PositiveInfinity);
        for (var i = 0; i < 200 && flight.WarpingTo is not null && flown.State == FlightState.Flying; i++)
        {
            var burning = flight.EngineLit;
            sim.Step(default);
            if (flight.WarpingTo is { } now && burning)
            {
                held |= now < to;
                Assert.True(now <= ending + 1e-6, $"warping to {now - flight.WorldTime:0.0} s on, past its path's end then, {ending - flight.WorldTime:0.0} s on");
            }
            ending = Patched.Ends(PathOf(flown, flight.WorldTime));
        }
        Assert.True(held, "never held to the path's end");
    }

    [Fact]
    public void On_a_plan_it_reaches_only_the_first_node_and_a_right_press_that_deleted_a_node_warps_nowhere()
    {
        var chain = new ManoeuvreChain(default, [new ManoeuvreNode(0, Prograde: 5f)]);
        var first = chain.TimeOf(0);
        Assert.Equal(first, NodeEditor.FlownWithoutABurn(chain));
        Assert.True(double.IsPositiveInfinity(NodeEditor.FlownWithoutABurn(null)));
        Assert.True(double.IsPositiveInfinity(NodeEditor.FlownWithoutABurn(ManoeuvreChain.Empty)));
        Assert.Equal(first - 1.0, NodeEditor.WarpTime(first - 1.0, first - 10.0, first));
        Assert.Equal(first, NodeEditor.WarpTime(first, first - 10.0, first));
        Assert.Null(NodeEditor.WarpTime(first + 1.0, first - 10.0, first)); // past the first node: the plan's, not flown without a burn
        Assert.Null(NodeEditor.WarpTime(first - 20.0, first - 10.0, first)); // behind now

        var press = new NodeEditor.RightPress();
        Assert.False(press.WasANodes);
        press.Pressed();
        press.DeletedANode();
        Assert.True(press.WasANodes); // its click, let go over the path, warps nowhere
        press.Pressed();
        Assert.False(press.WasANodes); // the next press, off the nodes, is the path's again
    }
}
