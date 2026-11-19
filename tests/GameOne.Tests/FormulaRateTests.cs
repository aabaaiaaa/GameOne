using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A frame at a formula's rate (<see cref="Flight.IsFormulaRate"/>) is one advance by formula, in
/// windows (<see cref="Scene.FormulaWindowSeconds"/>, <see cref="Flight.FormulaWindows"/>): every
/// vessel, the flown one too, out of the physics, nothing of Jolt's stepped; a coast on its Kepler
/// orbit, a rocket on its pad and a lander on the moon each where the formula has it, and back in
/// the physics, the flown one first, as the warp drops; a slow frame stopped at a window's end by
/// its budget; only the witnessed vessels' bodies asked ahead; saved where the formula has it; and
/// a new flight at normal speed in the physics.
/// </summary>
public sealed class FormulaRateTests(ITestOutputHelper output)
{
    /// <summary>The rate key pressed <paramref name="times"/> times, each in a frame of no time (so no rate on the way runs a step of any length).</summary>
    private static void Press(Flight flight, int times, IReadOnlyList<Vessel>? watched = null)
    {
        for (var i = 0; i < times; i++)
            flight.RunFrame(0f, new FlightInput(Faster: true, Watched: watched));
    }

    /// <summary>The fastest rate, pressed to in frames of no time.</summary>
    private static void ToTheTop(Flight flight, IReadOnlyList<Vessel>? watched = null)
    {
        Press(flight, Flight.Rates.Length, watched);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
    }

    [Fact]
    public void A_coast_at_the_top_rate_ends_where_Kepler_puts_it()
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        // Where its motion is (its world's, behind the clock by the step's leftover), in double.
        var (since, at, velocity) = (flight.WorldTime - sim.Worlds.Lag, flown.TrueCentreOfMass, flown.TrueVelocity);
        var start = flight.WorldTime;
        ToTheTop(flight);
        for (var i = 0; i < 60; i++)
            sim.Step(default);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
        Assert.True(flown.OnFormula);

        // Its orbit among the stars from there, turned back into the ground's frame at the end.
        var body = flown.Body;
        var orbit = new Orbit(body, at.ToSingle(), body.AmongStars(at.ToSingle(), velocity.ToSingle()), findEnd: false);
        var t = flight.WorldTime - since;
        var expected = (Vector3Double)body.Turned(orbit.StateAfter(t).Position, -t);
        var off = (flown.TrueCentreOfMass - expected).Length();
        output.WriteLine($"{flight.WorldTime - start:0.000} s on, {t / orbit.Period:0.00} laps: {off:0.000} m from Kepler's");
        Assert.True(off < 1.0, $"{off:0.000} m from where Kepler puts it");
        Assert.True(Math.Abs(60 * Scene.TopRate * (double)Simulation.FrameSeconds - (flight.WorldTime - start)) < 1e-2,
            $"{flight.WorldTime - start} s on, not the 60 frames' worth");
    }

    [Fact]
    public void At_a_formula_rate_nothing_is_in_the_physics()
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        ToTheTop(flight);
        sim.Step(default);
        Assert.True(flown.OnFormula);
        Assert.NotEmpty(sim.Worlds.All); // kept, not stepped: the drop finds it made
        Assert.All(sim.Worlds.All, w => Assert.Empty(w.Crafts));
        output.WriteLine($"{flight.FormulaWindows} windows a frame");
        Assert.True(flight.FormulaWindows >= 34, $"{flight.FormulaWindows} windows");
    }

    [Fact]
    public void Waiting_on_the_pad_at_the_top_rate_it_stands_where_it_stood_and_launching_drops_the_warp()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var flight = sim.Flight;
        sim.Step(default);
        var flown = flight.Controlled;
        Assert.Equal(FlightState.Ready, flown.State);
        var padPlace = flown.Position;
        ToTheTop(flight);
        for (var i = 0; i < 60; i++)
            sim.Step(default);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
        Assert.True(flown.OnFormula);
        Assert.True(Vector3.Distance(flown.Position, padPlace) < 0.01f, $"{Vector3.Distance(flown.Position, padPlace):0.000} m from where it stood");

        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Equal(WarpDrop.SetMoving, flight.LastDrop!.Value.Reason);
        Assert.False(flown.OnFormula); // flown in the physics from that frame
        Assert.True(sim.Run((_, _) => default, _ => Vector3.Distance(flown.Position, padPlace) > 1f, maxSeconds: 2), "it never lifted off");
        Assert.Equal(FlightState.Flying, flown.State);
    }

    [Fact]
    public void Landed_on_the_moon_at_the_top_rate_it_stays_where_it_stood_and_throttling_up_drops_the_warp()
    {
        var moon = GroundBody.Moon;
        var lander = OnTheMoon.Lander(0, OnTheMoon.LandingSpot);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [lander], NextVesselId = 1 }, body: moon);
        var flight = sim.Flight;
        var flown = flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Step(default);
        Assert.Same(flown, flight.Controlled);
        Assert.Equal(FlightState.Landed, flown.State);
        var stood = flown.Position;
        ToTheTop(flight);
        for (var i = 0; i < 60; i++)
            sim.Step(default);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
        Assert.True(flown.OnFormula);
        Assert.Equal(FlightState.Landed, flown.State);
        Assert.True(Vector3.Distance(flown.Position, stood) < 0.01f, $"{Vector3.Distance(flown.Position, stood):0.000} m from where it stood");

        sim.Step(new FlightInput(FullThrottle: true));
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Equal(WarpDrop.SetMoving, flight.LastDrop!.Value.Reason);
        Assert.False(flown.OnFormula);
        Assert.Equal(FlightState.Flying, flown.State); // its engine lit, in the physics
    }

    [Fact]
    public void The_drop_brings_the_flown_vessel_back_into_the_physics_first_where_the_formula_had_it()
    {
        // Another round orbit 3 km behind it, in a small view.
        var other = InOrbit.Round(GroundBody.Planet, 1, WarpScenes.LowOrbitHeight);
        other = other with { Position = other.Position + new Vector3(0f, 0f, 3000f) };
        var (sim, flown) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, WarpScenes.LowOrbitHeight), other);
        using var _ = sim;
        var flight = sim.Flight;
        var viewed = flight.WorldVessels.Single(v => v.Id == 1);
        IReadOnlyList<Vessel> watched = [viewed];
        ToTheTop(flight, watched);
        for (var i = 0; i < 10; i++)
            sim.Step(new FlightInput(Watched: watched));
        Assert.True(flown.OnFormula && viewed.OnFormula);
        var (flownAt, viewedAt) = (flown.TrueCentreOfMass, viewed.TrueCentreOfMass);

        // Its engine lit in a frame of no time: the next frame's gate holds the warp, and drops it.
        flight.RunFrame(0f, new FlightInput(FullThrottle: true, Watched: watched));
        flight.RunFrame(0f, new FlightInput(Watched: watched));
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.Equal(WarpDrop.Air, flight.LastDrop!.Value.Reason);
        Assert.False(flown.OnFormula);
        Assert.False(viewed.OnFormula);
        Assert.Same(flight.PhysicsAt(flown.Body), flown.Physics);
        Assert.True((flown.TrueCentreOfMass - flownAt).Length() < 1e-3, $"{(flown.TrueCentreOfMass - flownAt).Length()} m from where the formula had it");
        Assert.True((viewed.TrueCentreOfMass - viewedAt).Length() < 1e-3, $"{(viewed.TrueCentreOfMass - viewedAt).Length()} m from where the formula had it");
    }

    [Fact]
    public void A_slow_frame_at_the_top_rate_stops_at_a_window_s_end_within_its_budget()
    {
        var (sim, _) = WarpScenes.HundredSatellites();
        using var _s = sim;
        var flight = sim.Flight;
        ToTheTop(flight);
        sim.Step(default); // warmed up
        var before = flight.WorldTime;
        var advanced = flight.RunFrame(0.1f, default, budgetSeconds: 0.001);
        var windows = advanced / Scene.FormulaWindowSeconds;
        output.WriteLine($"{advanced:0.000} s of {0.1f * Scene.TopRate:0.0} asked, {flight.FormulaWindows} windows");
        Assert.True(advanced < 0.1f * Scene.TopRate - Scene.FormulaWindowSeconds, $"{advanced} s: the budget never stopped it");
        Assert.True(advanced > 0f);
        Assert.Equal(Math.Round(windows), windows, 3); // a whole number of windows
        Assert.Equal(flight.FormulaWindows, (int)Math.Round(windows));
        Assert.Equal(advanced, flight.WorldTime - before, 3);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
    }

    [Fact]
    public void At_a_formula_rate_the_bodies_asked_ahead_are_only_the_witnessed_ones()
    {
        // The transfer just after its burn: its path into the moon's sphere some 330 s on.
        var (state, time) = MoonTrip.Transferring(0, 0.0);
        var (sim, flown) = WarpScenes.Flying(MoonTrip.World(time, state), state.Id);
        using var _ = sim;
        var flight = sim.Flight;
        var legs = new List<PathLeg>(Scene.PatchedLegs);
        Patched.Legs(flown.Body, flown.CentreOfMass, flown.Body.AmongStars(flown.CentreOfMass, flown.Velocity), flight.WorldTime, legs);
        Assert.True(legs.Count > 1 && legs[1].Body == GroundBody.Moon, "the transfer reaches the moon's sphere");
        var crossing = legs[1].Start;
        // Frames of a tenth of the game's (a top-rate frame is 273 s, most of the way there): the
        // physics' own horizon (its top rate's) is 80 s, the top rate's would be hours.
        sim.RealFrame = Simulation.FrameSeconds / 10f;
        var frame = Scene.TopRate * sim.RealFrame;
        var reach = Scene.PhysicsAheadSeconds * Scene.PhysicsTopRate;
        ToTheTop(flight);
        Assert.True(crossing - flight.WorldTime > frame + reach, $"crosses only {crossing - flight.WorldTime:0} s on");
        var frames = 0;
        while (crossing - flight.WorldTime > frame + reach)
        {
            sim.Step(default);
            frames++;
            Assert.Equal(1, sim.Worlds.InUse);
        }
        Thread.Sleep(200); // time for the tile worker to have made the moon's physics, had it been asked for
        output.WriteLine($"{frames} frames, {crossing - flight.WorldTime:0} s from the moon's sphere");
        Assert.True(frames > 0);
        Assert.Equal(1, sim.Worlds.InUse);
    }

    [Fact]
    public void A_world_saved_at_the_top_rate_comes_back_where_the_formula_had_it()
    {
        var other = InOrbit.Round(GroundBody.Planet, 1, WarpScenes.LowOrbitHeight + 500f);
        var (sim, _) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, WarpScenes.LowOrbitHeight), other);
        var flight = sim.Flight;
        ToTheTop(flight);
        for (var i = 0; i < 5; i++)
            sim.Step(default);
        Assert.All(flight.WorldVessels, v => Assert.True(v.OnFormula));
        var places = flight.WorldVessels.ToDictionary(v => v.Id, v => v.TrueCentreOfMass);
        using var back = sim.Reloaded();
        foreach (var v in back.Flight.WorldVessels)
        {
            var off = (v.TrueCentreOfMass - places[v.Id]).Length();
            output.WriteLine($"vessel {v.Id}: {off:0.000000} m off");
            Assert.True(off < 1e-3, $"vessel {v.Id} came back {off} m from where the formula had it");
        }
        Assert.Equal(1f, back.Flight.PlayerRate);
    }

    [Fact]
    public void Starting_again_at_the_top_rate_comes_back_at_normal_speed_in_the_physics()
    {
        var (sim, _) = WarpScenes.LowOrbit();
        using var _s = sim;
        var flight = sim.Flight;
        ToTheTop(flight);
        sim.Step(default);
        sim.Step(new FlightInput(StartAgain: true));
        Assert.Equal(1f, flight.PlayerRate);
        Assert.Equal(FlightState.Ready, flight.Controlled.State);
        Assert.False(flight.Controlled.OnFormula);
        var before = flight.WorldTime;
        sim.Step(default);
        Assert.Equal(Simulation.FrameSeconds, flight.WorldTime - before, 5); // at normal speed from there
    }

    [Fact]
    public void A_frame_at_the_top_rate_allocates_nothing()
    {
        var (sim, _) = WarpScenes.LowOrbit();
        using var _s = sim;
        var flight = sim.Flight;
        ToTheTop(flight);
        for (var i = 0; i < 5; i++)
            sim.Step(default); // warmed up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 30; i++)
            flight.RunFrame(Simulation.FrameSeconds, default);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
    }
}
