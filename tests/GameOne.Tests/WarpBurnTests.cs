using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Burns, the holds and the rings at a formula's rate (<see cref="Scene.TopRate"/>): a burn held
/// prograde, or along a node by the manoeuvre hold, flown by the formula as it is in the physics at
/// normal speed; a ring's click or drag turning the vessel and keeping the warp; the warp dropped
/// ahead of a burn's fuel running out (<see cref="WarpDrop.FuelOut"/>) or of a burn taking it down
/// into the air; and a burning vessel above the air holding nothing.
/// </summary>
public sealed class WarpBurnTests(ITestOutputHelper output)
{
    private static GroundBody Planet => GroundBody.Planet;

    /// <summary>
    /// <paramref name="state"/> burning at <paramref name="throttle"/>, its nose along its prograde
    /// among the stars (against it for <see cref="HoldMode.Retrograde"/>), holding
    /// <paramref name="hold"/>; with <paramref name="fuel"/> kg in its tank, if given.
    /// </summary>
    private static VesselState Burning(VesselState state, float throttle, HoldMode hold, float? fuel = null)
    {
        var com = state.CentreOfMass.ToSingle();
        var way = Vector3.Normalize(Planet.AmongStars(com, state.Velocity.ToSingle())) * (hold == HoldMode.Retrograde ? -1f : 1f);
        return Burning(state, throttle, hold, way, fuel);
    }

    /// <summary><paramref name="state"/> burning at <paramref name="throttle"/>, its nose along <paramref name="way"/>, holding <paramref name="hold"/>.</summary>
    private static VesselState Burning(VesselState state, float throttle, HoldMode hold, Vector3 way, float? fuel = null)
    {
        var com = state.CentreOfMass.ToSingle();
        way = Vector3.Normalize(way);
        var rotation = UnwatchedTests.RotationTo(way);
        var tanks = (float[])state.Fuel.Clone();
        if (fuel is { } kg)
            tanks[state.First] = kg;
        return state with
        {
            Rotation = rotation, Aim = way, Position = com - Vector3.Transform(state.CentreOfMassOffset, rotation),
            Throttle = throttle, Hold = hold, Fuel = tanks,
        };
    }

    /// <summary>The height of the apoapsis over the sea of the orbit <paramref name="v"/> is on now, its engine off.</summary>
    private static double Apoapsis(Vessel v) =>
        new Orbit(v.Body, v.CentreOfMass, v.Body.AmongStars(v.CentreOfMass, v.Velocity), findEnd: false).ApoapsisDistance - v.Body.SeaRadius;

    /// <summary>The fuel in <paramref name="v"/>'s tank, kg.</summary>
    private static float Tank(Vessel v) => v.Fuel * v.CurrentStage.FuelMass;

    /// <summary>
    /// Flies the scene <paramref name="seconds"/> of game time on from now, at the fastest rate in
    /// <paramref name="frames"/> frames (the rate pressed to in frames of no time), or at normal
    /// speed in the physics, then cuts the throttle in a frame of no time; returns the time it got to.
    /// </summary>
    private static double Burn(Simulation sim, double seconds, bool top, int frames)
    {
        var flight = sim.Flight;
        var end = flight.WorldTime + seconds;
        if (top)
        {
            FormulaRateTests.ToTheTop(flight);
            sim.RealFrame = (float)(seconds / (frames * Scene.TopRate));
            for (var i = 0; i < frames; i++)
                sim.Step(default);
            Assert.Equal(Scene.TopRate, flight.PlayerRate);
            Assert.Null(flight.LastDrop);
        }
        else
        {
            sim.RealFrame = Simulation.FrameSeconds;
            while (end - flight.WorldTime > Simulation.FrameSeconds / 2)
                sim.Step(default);
            Assert.False(flight.Controlled.OnFormula);
        }
        flight.RunFrame(0f, new FlightInput(CutThrottle: true));
        return flight.WorldTime;
    }

    [Fact]
    public void A_prograde_burn_at_the_top_rate_matches_the_same_burn_at_normal_speed()
    {
        // A gentle burn (some 50 m/s), so the orbit it ends on is still closed.
        (double Apoapsis, float Used, double Time) Burnt(bool top)
        {
            var (sim, flown) = WarpScenes.Flying(Burning(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), 0.015f, HoldMode.Prograde));
            using var _ = sim;
            var fuel = Tank(flown);
            var time = Burn(sim, 120.0, top, frames: 4);
            Assert.Equal(top, flown.OnFormula);
            Assert.False(flown.EngineLit);
            return (Apoapsis(flown), fuel - Tank(flown), time);
        }

        var (physics, formula) = (Burnt(top: false), Burnt(top: true));
        output.WriteLine($"apoapsis {physics.Apoapsis:0.0} m at 1x, {formula.Apoapsis:0.0} m at the top rate; fuel used {physics.Used:0.000} kg, {formula.Used:0.000} kg; at {physics.Time:0.000} s, {formula.Time:0.000} s");
        Assert.True(physics.Apoapsis > WarpScenes.LowOrbitHeight + Scene.AtmosphereHeight + 1000.0, "the burn should have raised the apoapsis");
        Assert.InRange(formula.Apoapsis, physics.Apoapsis * 0.995, physics.Apoapsis * 1.005);
        Assert.InRange(formula.Used, physics.Used * 0.995f, physics.Used * 1.005f);
        Assert.Equal(physics.Time, formula.Time, 3);
    }

    [Fact]
    public void A_burn_along_a_node_at_the_top_rate_matches_the_same_burn_at_normal_speed()
    {
        // A node of 20 m/s prograde, where it is now (so none comes up to drop the warp for), flown
        // by the manoeuvre hold at a twentieth of the throttle (some 15 s).
        (double Apoapsis, float Used, float Left, double Time) Burnt(bool top)
        {
            var (sim, flown) = WarpScenes.Flying(Burning(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), 0.05f, HoldMode.None));
            using var _ = sim;
            var flight = sim.Flight;
            flight.RunFrame(0f, new FlightInput(Plan: ManoeuvreChain.AnchoredOn(flown, flight.WorldTime, [new ManoeuvreNode(0.0, Prograde: 20f)], flight.WorldTime)));
            flight.RunFrame(0f, new FlightInput(ChooseHold: HoldMode.Manoeuvre));
            Assert.Equal(HoldMode.Manoeuvre, flown.Hold);
            var fuel = Tank(flown);
            var time = Burn(sim, 15.0, top, frames: 3);
            Assert.Equal(top, flown.OnFormula);
            var left = flight.Guide?.Left.Length() ?? 0f;
            return (Apoapsis(flown), fuel - Tank(flown), left, time);
        }

        var (physics, formula) = (Burnt(top: false), Burnt(top: true));
        output.WriteLine($"apoapsis {physics.Apoapsis:0.0} m at 1x, {formula.Apoapsis:0.0} m at the top rate; fuel used {physics.Used:0.000} kg, {formula.Used:0.000} kg; "
            + $"left {physics.Left:0.000} m/s, {formula.Left:0.000} m/s; at {physics.Time:0.000} s, {formula.Time:0.000} s");
        Assert.True(physics.Left < 2f, $"the burn should have flown nearly all the node ({physics.Left:0.00} m/s left)");
        Assert.InRange(formula.Apoapsis, physics.Apoapsis * 0.995, physics.Apoapsis * 1.005);
        Assert.InRange(formula.Used, physics.Used * 0.995f, physics.Used * 1.005f);
        Assert.InRange(formula.Left, 0f, physics.Left + 0.5f);
        Assert.Equal(physics.Time, formula.Time, 3);
    }

    [Fact]
    public void A_ring_click_at_the_top_rate_turns_the_vessel_to_it_and_keeps_the_warp()
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        sim.Step(default);
        Assert.True(flown.OnFormula);
        // Along the nose's heading, a right angle below its pitch: 90° off the nose.
        var (heading, pitch) = RingAngles.Of(flown.Body, flown.CentreOfMass, flown.Aim, 0f);
        pitch -= MathF.PI / 2f;
        Assert.InRange(Vector3.Dot(flown.Aim, RingAngles.Direction(flown.Body, flown.CentreOfMass, heading, pitch)), -0.01f, 0.01f);

        sim.Step(new FlightInput(RingHeading: heading, RingPitch: pitch));
        var ring = RingAngles.Direction(flown.Body, flown.CentreOfMass, flown.RingAim.Heading, flown.RingAim.Pitch);
        output.WriteLine($"{flight.PlayerRate}x, {Vector3.Dot(flown.Aim, ring):0.000000} along the ring's way, holding {flown.Hold}");
        Assert.Equal(HoldMode.Rings, flown.Hold);
        Assert.Equal((heading, pitch), flown.RingAim);
        Assert.True(Vector3.Dot(flown.Aim, ring) > 0.9999f);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Null(flight.LastDrop);
        Assert.True(flown.OnFormula);
    }

    [Fact]
    public void A_ring_drag_at_the_top_rate_keeps_the_warp_too()
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        sim.Step(default);
        var way = flown.Right; // across the nose
        for (var i = 0; i < 2; i++)
            sim.Step(new FlightInput(Hold: way));
        output.WriteLine($"{flight.PlayerRate}x, {Vector3.Dot(flown.Aim, way):0.000000} along the drag's way");
        Assert.True(Vector3.Dot(flown.Aim, way) > 0.9999f);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Null(flight.LastDrop);
        Assert.True(flown.OnFormula);
    }

    [Fact]
    public void A_burn_s_fuel_running_out_drops_the_warp_ahead_of_it()
    {
        // 40 kg left at a fiftieth of the throttle: some 14 s of burning, 60 s a frame.
        var (sim, flown) = WarpScenes.Flying(Burning(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), 0.02f, HoldMode.Prograde, fuel: 40f));
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        sim.RealFrame = (float)(60.0 / Scene.TopRate);
        Assert.True(flown.FuelSeconds > Scene.WarpDropLeadSeconds + 5.0);
        sim.Step(default);
        output.WriteLine($"{flight.PlayerRate}x, dropped for {flight.LastDrop?.Reason}, {flown.FuelSeconds:0.000} s of fuel left");
        Assert.Equal(new WarpDropped(WarpDrop.FuelOut, WorldLog.Named(flown), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.False(flown.OnFormula);
        Assert.True(flown.EngineLit);
        Assert.InRange(flown.FuelSeconds, Scene.WarpDropLeadSeconds - Scene.PhysicsTopRate / 60.0, Scene.WarpDropLeadSeconds + Scene.PhysicsTopRate / 60.0);
    }

    [Fact]
    public void A_burn_taking_it_down_into_the_air_drops_the_warp_ahead_of_the_air()
    {
        // Held retrograde at a tenth of the throttle, from a kilometre over the air: 10 s a frame.
        var (sim, flown) = WarpScenes.Flying(Burning(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), 0.1f, HoldMode.Retrograde));
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        sim.RealFrame = (float)(10.0 / Scene.TopRate);
        var frames = 0;
        while (flight.LastDrop is null && frames++ < 100)
            sim.Step(default);
        var dropped = flight.WorldTime;
        Assert.Equal(new WarpDropped(WarpDrop.Air, WorldLog.Named(flown), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.False(flown.OnFormula);
        Assert.True(flown.EngineLit);

        // Burning on in the physics, at its rate: into the air the lead on, about.
        var floor = Formula.FloorRadius(flown);
        Assert.True((flown.CentreOfMass - flown.Body.Centre).Length() > floor);
        sim.RealFrame = Simulation.FrameSeconds;
        while ((flown.CentreOfMass - flown.Body.Centre).Length() > floor && flight.WorldTime - dropped < 30.0)
            sim.Step(default);
        output.WriteLine($"dropped {frames} frames on, {flight.WorldTime - dropped:0.000} s before it came down to its floor");
        Assert.InRange(flight.WorldTime - dropped, Scene.WarpDropLeadSeconds - 0.5, Scene.WarpDropLeadSeconds + 0.5);
    }

    [Fact]
    public void A_burning_vessel_in_a_small_view_lets_the_top_rate_in_above_the_air()
    {
        var (sim, flown) = WarpScenes.Flying(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight),
            Burning(InOrbit.Round(Planet, 1, WarpScenes.LowOrbitHeight + 1000f), 0.015f, HoldMode.Prograde));
        using var _ = sim;
        var flight = sim.Flight;
        var burner = flight.WorldVessels.Single(v => v.Id == 1);
        flight.OpenView(burner);
        FormulaRateTests.ToTheTop(flight, watched: [burner]);
        var fuel = Tank(burner);
        sim.Step(new FlightInput(Watched: [burner]));
        output.WriteLine($"{flight.PlayerRate}x, held by {(flight.WarpHeldBy is { } h ? WorldLog.Named(h) : "none")}, {fuel - Tank(burner):0.00} kg burnt");
        Assert.Null(flight.WarpHeldBy);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Null(flight.LastDrop);
        Assert.True(burner.OnFormula && flown.OnFormula);
        Assert.True(burner.EngineLit);
        Assert.True(Tank(burner) < fuel, "it should have burnt fuel");
    }

    /// <summary>The satellite launcher's upper stage numbered <paramref name="id"/> on a round polar orbit <see cref="WarpScenes.LowOrbitHeight"/> over the air, <paramref name="degrees"/> east of where <see cref="InOrbit.Round"/> puts one, going north.</summary>
    private static VesselState Polar(int id, float degrees)
    {
        var state = InOrbit.Round(Planet, id, WarpScenes.LowOrbitHeight);
        var turn = Quaternion.CreateFromAxisAngle(Planet.NorthPole, float.DegreesToRadians(degrees));
        var com = Planet.Centre + Vector3.Transform(state.CentreOfMass.ToSingle() - Planet.Centre, turn);
        var stars = Planet.North(com) * Planet.CircularSpeed(Planet.OrbitFloor + WarpScenes.LowOrbitHeight);
        var rotation = Planet.UprightAt(com);
        return state with
        {
            Rotation = rotation, Aim = Vector3.Transform(Vector3.UnitY, rotation),
            Position = com - Vector3.Transform(state.CentreOfMassOffset, rotation), Velocity = Planet.OverGround(com, stars),
        };
    }

    [Fact]
    public void A_burn_held_against_its_target_at_the_top_rate_matches_the_same_burn_at_normal_speed()
    {
        // The target on a polar orbit 20° ahead (some 370 m/s across it), the burn held retrograde
        // in the target's frame: the hold steers by the target as it is at each of its steps.
        var target = Polar(1, 20f);
        var flownState = InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight);
        var (com, targetCom) = (flownState.CentreOfMass.ToSingle(), target.CentreOfMass.ToSingle());
        var relative = Planet.AmongStars(com, flownState.Velocity.ToSingle()) - Planet.AmongStars(targetCom, target.Velocity.ToSingle());
        (Vector3 Velocity, Vector3 Relative, double Time) Burnt(bool top)
        {
            var (sim, flown) = WarpScenes.Flying(Burning(flownState, 0.05f, HoldMode.Retrograde, -relative), target);
            using var _ = sim;
            var flight = sim.Flight;
            var other = flight.WorldVessels.Single(v => v.Id == 1);
            flight.RunFrame(0f, new FlightInput(SetTarget: NavTarget.OfVessel(other)));
            flight.RunFrame(0f, new FlightInput(ChooseFrame: FrameChoice.Target));
            Assert.Equal(Frame.Target, flight.Frame);
            Assert.Equal(HoldMode.Retrograde, flown.Hold);
            var time = Burn(sim, 24.0, top, frames: 3);
            return (flown.Body.AmongStars(flown.CentreOfMass, flown.Velocity), flight.FrameVelocity, time);
        }

        var (physics, formula) = (Burnt(top: false), Burnt(top: true));
        var off = Vector3.Distance(physics.Velocity, formula.Velocity);
        output.WriteLine($"{off:0.000} m/s apart among the stars; relative speed {physics.Relative.Length():0.00} m/s at 1x, {formula.Relative.Length():0.00} m/s at the top rate (from {relative.Length():0.00})");
        Assert.True(relative.Length() - physics.Relative.Length() > 20f, "the burn should have slowed it against its target");
        Assert.InRange(off, 0f, 0.3f);
        Assert.Equal(physics.Time, formula.Time, 3);
    }

    [Fact]
    public void A_full_throttle_burn_at_the_top_rate_matches_the_same_burn_at_normal_speed()
    {
        // Full throttle for 10 s (its tank full: 44 s of it, so its fuel isn't about to run out),
        // the rings holding its nose north, across its orbit: some 160 m/s, still a closed orbit.
        var state = InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight);
        var north = Planet.North(state.CentreOfMass.ToSingle());
        var full = state.Vehicle.Stages[state.First].FuelMass;
        (double Apoapsis, Vector3 Velocity, float Used, double Time) Burnt(bool top)
        {
            var (sim, flown) = WarpScenes.Flying(Burning(state, 1f, HoldMode.Rings, north, fuel: full) with { RingHeading = 0f, RingPitch = 0f });
            using var _ = sim;
            var fuel = Tank(flown);
            var time = Burn(sim, 10.0, top, frames: 2);
            Assert.Equal(top, flown.OnFormula);
            return (Apoapsis(flown), flown.Body.AmongStars(flown.CentreOfMass, flown.Velocity), fuel - Tank(flown), time);
        }

        var (physics, formula) = (Burnt(top: false), Burnt(top: true));
        var off = Vector3.Distance(physics.Velocity, formula.Velocity);
        output.WriteLine($"apoapsis {physics.Apoapsis:0.0} m at 1x, {formula.Apoapsis:0.0} m at the top rate; {off:0.000} m/s apart; fuel used {physics.Used:0.0} kg, {formula.Used:0.0} kg");
        Assert.True(physics.Used > 1000f, "it should have burnt hard");
        Assert.InRange(formula.Apoapsis, physics.Apoapsis * 0.995, physics.Apoapsis * 1.005);
        Assert.InRange(off, 0f, 0.005f * physics.Velocity.Length());
        Assert.InRange(formula.Used, physics.Used * 0.995f, physics.Used * 1.005f);
        Assert.Equal(physics.Time, formula.Time, 3);
    }

    [Fact]
    public void A_small_view_s_vessel_flies_its_node_at_the_top_rate()
    {
        // Vessel 1, its nose straight up, burning gently with a 20 m/s node where it is now and the
        // manoeuvre hold on, given a small view while vessel 0 is flown: at the top rate its hold
        // turns it along the burn left and its burn counts against the node, as in the physics.
        var (sim, burner) = WarpScenes.Flying(new WorldState
        {
            Time = 0.0, NextVesselId = 2, Flights = 1,
            Vessels = [InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), InOrbit.Round(Planet, 1, WarpScenes.LowOrbitHeight + 2000f) with { Throttle = 0.05f }],
        }, 1);
        using var _ = sim;
        var flight = sim.Flight;
        flight.RunFrame(0f, new FlightInput(Plan: ManoeuvreChain.AnchoredOn(burner, flight.WorldTime, [new ManoeuvreNode(0.0, Prograde: 20f)], flight.WorldTime)));
        flight.RunFrame(0f, new FlightInput(ChooseHold: HoldMode.Manoeuvre));
        Assert.Equal(HoldMode.Manoeuvre, burner.Hold);
        var flown = flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: flown));
        Assert.Same(flown, flight.Controlled);
        flight.OpenView(burner);
        IReadOnlyList<Vessel> watched = [burner];
        FormulaRateTests.ToTheTop(flight, watched);
        var before = burner.BurnLeft(flight.WorldTime)!.Value.Length();
        sim.RealFrame = (float)(8.0 / Scene.TopRate);
        sim.Step(new FlightInput(Watched: watched));
        var left = burner.BurnLeft(flight.WorldTime)!.Value;
        output.WriteLine($"{flight.PlayerRate}x; {Vector3.Dot(burner.Aim, Vector3.Normalize(left)):0.0000} along the burn left, {before:0.00} m/s left, then {left.Length():0.00} m/s");
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.True(burner.OnFormula);
        Assert.Equal(HoldMode.Manoeuvre, burner.Hold);
        Assert.True(Vector3.Dot(burner.Aim, Vector3.Normalize(left)) > 0.99f);
        Assert.True(left.Length() < before - 2f, "its burn should have counted against the node");
    }

    [Fact]
    public void A_vessel_standing_on_its_engine_on_the_moon_holds_the_warp_and_stands_on()
    {
        // The lander landed at the moon's landing spot, its engine pushing up at about half its
        // weight: standing there burning, it isn't at rest, so it holds the warp at the physics'
        // top rate (never flown at a formula's), and stands on, burning, frame after frame.
        var (sim, flown) = WarpScenes.Flying(new WorldState
        {
            Time = 0.0, NextVesselId = 1, Flights = 1, Vessels = [WarpDropTests.LanderLanded() with { Throttle = 0.25f }],
        }, 0);
        using var _ = sim;
        var flight = sim.Flight;
        Assert.Same(GroundBody.Moon, flown.Body);
        sim.Step(default);
        Assert.Equal(FlightState.Flying, flown.State);
        Assert.True(flown.EngineLit);
        var (stood, fuel) = (flown.CentreOfMass, Tank(flown));
        FormulaRateTests.Press(flight, Flight.Rates.Length);
        Assert.Same(flown, flight.WarpHeldBy);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        for (var i = 0; i < 30; i++)
            sim.Step(default);
        output.WriteLine($"{flight.PlayerRate}x, held by {(flight.WarpHeldBy is { } h ? WorldLog.Named(h) : "none")}; {Vector3.Distance(stood, flown.CentreOfMass):0.000} m from where it stood, {fuel - Tank(flown):0.0} kg burnt");
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.False(flown.OnFormula);
        Assert.Equal(FlightState.Flying, flown.State);
        Assert.True(Vector3.Distance(stood, flown.CentreOfMass) < 0.1f, "it should stand where it was");
        Assert.True(Tank(flown) < fuel, "it should have burnt fuel");
    }

    [Fact]
    [Trait("Run", "Alone")] // timed in real time, the node's frames against the same burn's without it: beside the other shards' tests both move with theirs
    public void Burning_past_a_node_on_a_moon_transfer_at_the_top_rate_keeps_its_frames_short()
    {
        // The upper stage on its way to the moon, its tank full, burning gently (some 2,000 s of
        // it), a 20 m/s node's time now: every second's step at the top rate asks the burn left
        // along the plan's path past the node (patched into the moon's sphere), which is worked out
        // once, not at every step, and homed in on from where it was. Timed beside the same burn
        // held prograde with no node, so what the node costs a frame shows; a frame is 273 s.
        (List<double> Times, string Said) Frames(bool node)
        {
            var (state, time) = MoonTrip.Transferring(0, WarpScenes.TransferSeconds);
            var (sim, flown) = WarpScenes.Flying(MoonTrip.World(time, state with { Throttle = 0.02f, Hold = node ? HoldMode.None : HoldMode.Prograde, Fuel = [.. state.Vehicle.Stages.Select(s => s.FuelMass)] }), state.Id);
            using var _ = sim;
            var flight = sim.Flight;
            if (node)
            {
                flight.RunFrame(0f, new FlightInput(Plan: ManoeuvreChain.AnchoredOn(flown, flight.WorldTime, [new ManoeuvreNode(0.0, Prograde: 20f)], flight.WorldTime)));
                flight.RunFrame(0f, new FlightInput(ChooseHold: HoldMode.Manoeuvre));
                Assert.Equal(HoldMode.Manoeuvre, flown.Hold);
            }
            FormulaRateTests.ToTheTop(flight);
            sim.Step(default); // the first, untimed (its JIT), as each frame is 273 s of game time
            var times = new List<double>();
            for (var i = 0; i < 5 && flight.PlayerRate == Scene.TopRate && flown.EngineLit; i++)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                sim.Step(default);
                if (flight.PlayerRate == Scene.TopRate)
                    times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            times.Sort();
            return (times, $"{times.Count} frames at the top rate: {string.Join(", ", times.Select(t => $"{t:0.0}"))} ms; "
                + $"{flown.BurnLeft(flight.WorldTime)?.Length() ?? 0f:0.0} m/s left, on {flown.Body.Name}, dropped for {flight.LastDrop?.Why ?? "nothing"}");
        }

        var (alone, along) = (Frames(node: false), Frames(node: true));
        output.WriteLine($"held prograde: {alone.Said}");
        output.WriteLine($"along the node: {along.Said}");
        Assert.True(along.Times.Count >= 3 && alone.Times.Count >= 3, "it should have burnt at the top rate for frames");
        var (median, without) = (along.Times[along.Times.Count / 2], alone.Times[alone.Times.Count / 2]);
        // A frame along the node no more than three times one without (patching the path afresh at
        // every step makes it some seven times).
        Assert.InRange(median, 0.0, 3.0 * without);
    }
}
