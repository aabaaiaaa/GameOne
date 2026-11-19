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
    internal static void ToTheTop(Flight flight, IReadOnlyList<Vessel>? watched = null)
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
    public void A_rewind_at_the_top_rate_runs_no_rules_on_the_world_it_brings_back()
    {
        // Carried on while burning (the rewind point), then coasting at the top rate: the rewind
        // brings it back burning, and the frame it is asked in runs nothing on it unstepped.
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        sim.Step(new FlightInput(FullThrottle: true));
        Assert.True(flown.EngineLit);
        var fuel = flown.Fuel; // as the carry-on's step begins, where its rewind point is taken
        sim.Step(new FlightInput(CarryOn: flown.Id));
        Assert.True(flight.CanRewind);
        sim.Step(new FlightInput(CutThrottle: true));
        ToTheTop(flight);
        sim.Step(default);
        Assert.Equal(Scene.TopRate, flight.TimeScale);

        sim.Step(new FlightInput(Rewind: true));
        var back = flight.Controlled;
        output.WriteLine($"fuel {fuel:0.000000} at the rewind point, {back.Fuel:0.000000} after it; {flight.PlayerRate}x");
        Assert.Equal(flown.Id, back.Id);
        Assert.True(back.EngineLit);
        Assert.Equal(1f, flight.PlayerRate);
        Assert.False(back.OnFormula);
        Assert.Equal(fuel, back.Fuel, 6);
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

    /// <summary>
    /// The satellite launcher's upper stage (numbered <paramref name="id"/>, engine off) at
    /// <paramref name="body"/>, along <paramref name="way"/> from its centre (on its equator) at
    /// <paramref name="from"/> metres over its datum, on its way down to a periapsis
    /// <paramref name="periapsis"/> over its datum on an orbit of eccentricity
    /// <paramref name="eccentricity"/>, going east: at world time 0.
    /// </summary>
    private static VesselState Descending(GroundBody body, int id, Vector3 way, double from, double periapsis, double eccentricity)
    {
        double mu = body.Mu, r0 = body.SeaRadius + from, rp = body.SeaRadius + periapsis;
        var p = rp * (1 + eccentricity);
        var a = p / (1 - eccentricity * eccentricity);
        var speed = Math.Sqrt(mu * (2 / r0 - 1 / a));
        var across = Math.Sqrt(mu * p) / r0;
        var down = Math.Sqrt(Math.Max(0, speed * speed - across * across));
        var at = body.Centre + way * (float)r0;
        var stars = body.East(at) * (float)across - body.Up(at) * (float)down;
        return CrossingTests.Craft(id, body, at, body.OverGround(at, stars), stars);
    }

    /// <summary>The flown satellite on a low round orbit round the planet (numbered 0), and <paramref name="other"/> beside it in the world, at world time 0.</summary>
    private static WorldState WithFlown(VesselState other) =>
        new() { Time = 0.0, Vessels = [InOrbit.Round(GroundBody.Planet, 0, WarpScenes.LowOrbitHeight), other], NextVesselId = 2, Flights = 1 };

    [Fact]
    public void An_orbit_dipping_into_the_air_within_a_top_rate_frame_is_stepped_through_its_dip()
    {
        // Its periapsis 2,500 m up, under the air's top (3 km), some 60 s of its lap in the air,
        // and on the far side of the planet from the flown satellite.
        var planet = GroundBody.Planet;
        var dipping = Descending(planet, 1, -Vector3.UnitX, Scene.AtmosphereHeight + 600.0, 2500.0, 0.176);
        var (fast, _) = WarpScenes.Flying(WithFlown(dipping), 0);
        using var _f = fast;
        var v = fast.Flight.WorldVessels.Single(x => x.Id == 1);
        Assert.True(v.OnFormula);
        var before = v.Apoapsis;
        ToTheTop(fast.Flight);
        var start = fast.Flight.WorldTime;
        fast.Step(default); // one frame, the dip within it
        Assert.Equal(Scene.TopRate, fast.Flight.TimeScale);
        Assert.True(fast.Flight.WorldTime - start > 200, "the frame should be minutes long");
        Assert.Equal(FlightState.Flying, v.State);
        Assert.False(v.Motion.Stepping, "it should be back on its orbit after its dip");
        var after = v.Apoapsis;

        // The same at normal speed, under formula all the same (nobody watches it).
        var (slow, _) = WarpScenes.Flying(WithFlown(dipping), 0);
        using var _s = slow;
        var w = slow.Flight.WorldVessels.Single(x => x.Id == 1);
        var (inAir, stepped) = (0.0, false);
        Assert.True(slow.Run((_, _) => default, f =>
        {
            if (w.Motion.Stepping)
                (inAir, stepped) = (inAir + Simulation.FrameSeconds, true);
            return stepped && !w.Motion.Stepping && f.WorldTime > start + 10;
        }, maxSeconds: 200), "it never came out of the air at normal speed");
        Assert.True(w.OnFormula);
        output.WriteLine($"apoapsis {before:0.000} m before, {after:0.000} m after at the top rate, {w.Apoapsis:0.000} m at normal speed; {inAir:0.0} s stepped");
        Assert.True(before - after > 1f, $"its apoapsis only fell {before - after:0.000} m: the dip wasn't flown through the air");
        Assert.True(Math.Abs(after - w.Apoapsis) < 1f, $"{Math.Abs(after - w.Apoapsis):0.000} m from its apoapsis at normal speed");
    }

    /// <summary>
    /// On an ellipse of eccentricity <paramref name="e"/> round <paramref name="body"/> with its
    /// periapsis <paramref name="periapsis"/> from its centre: how long it takes to come down from
    /// <paramref name="radius"/> to its periapsis (Kepler's equation), and the radius it is at
    /// <paramref name="seconds"/> before its periapsis.
    /// </summary>
    private static (Func<double, double> SecondsDown, Func<double, double> RadiusBefore) Ellipse(GroundBody body, double periapsis, double e)
    {
        var a = periapsis / (1 - e);
        var n = Math.Sqrt(body.Mu / (a * a * a));
        double Down(double r)
        {
            var eccentric = Math.Acos(Math.Clamp((1 - r / a) / e, -1, 1));
            return (eccentric - e * Math.Sin(eccentric)) / n;
        }
        double Before(double seconds)
        {
            var mean = seconds * n;
            var eccentric = mean;
            for (var i = 0; i < 50; i++)
                eccentric -= (eccentric - e * Math.Sin(eccentric) - mean) / (1 - e * Math.Cos(eccentric));
            return a * (1 - e * Math.Cos(eccentric));
        }
        return (Down, Before);
    }

    [Fact]
    public void An_orbit_dipping_into_the_air_between_two_window_ends_is_stepped_through_its_dip()
    {
        // Its periapsis 10 m under the air's top: a few seconds in the air, wholly between two of
        // a top-rate frame's window ends (its periapsis 12 s on, the frame's second window from 8
        // to 16 s), so neither end finds it in the air. Passed over, its orbit would be unchanged.
        var planet = GroundBody.Planet;
        const double Periapsis = Scene.AtmosphereHeight - 10.0, E = 0.3, ToPeriapsis = 12.0;
        var rp = planet.SeaRadius + Periapsis;
        var (secondsDown, radiusBefore) = Ellipse(planet, rp, E);
        var dipping = Descending(planet, 1, -Vector3.UnitX, radiusBefore(ToPeriapsis) - planet.SeaRadius, Periapsis, E);
        var (fast, _) = WarpScenes.Flying(WithFlown(dipping), 0);
        using var _f = fast;
        var v = fast.Flight.WorldVessels.Single(x => x.Id == 1);
        Assert.True(v.OnFormula);
        var floor = Formula.FloorRadius(v);
        Assert.Equal(planet.SeaRadius + Scene.AtmosphereHeight, floor, 3); // the floor is the air's top
        var down = Formula.ComesDownAt(v.FormulaOrbit, floor)!.Value;
        var up = down + 2 * secondsDown(floor);
        var before = v.Apoapsis;
        ToTheTop(fast.Flight);
        var start = fast.Flight.WorldTime;
        var window = (int)Math.Floor((down - start) / Scene.FormulaWindowSeconds);
        output.WriteLine($"in the air from {down - start:0.000} s to {up - start:0.000} s into the frame ({up - down:0.000} s), window {window}");
        Assert.Equal(window, (int)Math.Floor((up - start) / Scene.FormulaWindowSeconds)); // between two window ends
        fast.Step(default);
        Assert.Equal(Scene.TopRate, fast.Flight.TimeScale);
        Assert.Equal(FlightState.Flying, v.State);
        Assert.False(v.Motion.Stepping);
        var after = v.Apoapsis;

        var (slow, _) = WarpScenes.Flying(WithFlown(dipping), 0);
        using var _s = slow;
        var w = slow.Flight.WorldVessels.Single(x => x.Id == 1);
        Assert.True(slow.Run((_, _) => default, f => f.WorldTime > up + 1.0, maxSeconds: 60));
        Assert.True(w.OnFormula);
        Assert.False(w.Motion.Stepping);
        output.WriteLine($"apoapsis {before:0.0000} m before, {after:0.0000} m after at the top rate, {w.Apoapsis:0.0000} m at normal speed");
        Assert.True(before - after > 0.01f, $"its apoapsis fell only {before - after:0.0000} m: the dip was passed over");
        Assert.True(Math.Abs(after - w.Apoapsis) < 0.01f, $"{Math.Abs(after - w.Apoapsis):0.0000} m from its apoapsis at normal speed");
    }

    [Fact]
    public void At_the_moon_an_orbit_under_its_highest_ground_is_stepped_and_its_touch_judged()
    {
        // Coming down from 300 m over its orbit's floor to a periapsis under its lowest ground.
        var moon = GroundBody.Moon;
        var floor = moon.MaxHeight + 50.0 + Scene.FineDetailMargin;
        var falling = Descending(moon, 1, Vector3.UnitX, floor + 300.0, moon.LowestRadius - moon.SeaRadius - 200.0, 0.3);
        var (fast, _) = WarpScenes.Flying(WithFlown(falling), 0);
        using var _f = fast;
        var v = fast.Flight.WorldVessels.Single(x => x.Id == 1);
        Assert.True(v.OnFormula);
        ToTheTop(fast.Flight);
        fast.Step(default);
        Assert.Equal(Scene.TopRate, fast.Flight.TimeScale);
        Assert.Equal(FlightState.Crashed, v.State);

        var (slow, _) = WarpScenes.Flying(WithFlown(falling), 0);
        using var _s = slow;
        var w = slow.Flight.WorldVessels.Single(x => x.Id == 1);
        Assert.True(slow.Run((_, _) => default, _ => w.State == FlightState.Crashed, maxSeconds: 200), "it never came down at normal speed");
        var off = (v.TrueCentreOfMass - w.TrueCentreOfMass).Length();
        output.WriteLine($"{v.CrashReason} / {w.CrashReason}; {off:0.000} m apart");
        Assert.Equal(w.CrashReason, v.CrashReason);
        Assert.True(off < 1.0, $"it came down {off:0.000} m from where it did at normal speed");
    }

    [Fact]
    public void A_vessel_past_the_solar_system_s_edge_in_a_top_rate_frame_is_lost_there()
    {
        var far = SolarEdgeTests.AtTheEdge(0, 50_000, 2000f);
        var world = new WorldState
        {
            Time = SolarEdgeTests.Now, Vessels = [InOrbit.Round(GroundBody.Planet, 1, WarpScenes.LowOrbitHeight), far], NextVesselId = 2, Flights = 2,
        };
        var (sim, _) = WarpScenes.Flying(world, 1);
        using var _s = sim;
        var flight = sim.Flight;
        Assert.True(flight.WorldVessels.Single(v => v.Id == 0).OnFormula);
        ToTheTop(flight);
        sim.Step(default);
        SolarEdgeTests.LeftAndLogged(flight, 0);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
    }
}
