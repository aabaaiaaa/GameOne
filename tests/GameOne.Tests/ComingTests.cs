using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// What is coming at each controlled vessel (<see cref="Flight.ComingAt"/>): the flown one's and
/// each small view's, a fast collision from its warning on, a near miss with the setting on, until
/// it is over or its other is wrecked; each vessel's its own; and the small views' flash
/// (<see cref="Hud.FlashOn"/>), at its rate by real time. The worlds are <see cref="FastCollisionTests"/>' builders.
/// </summary>
public sealed class ComingTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;

    [Fact]
    public void A_fast_collision_is_coming_at_the_flown_vessel_from_its_warning_on()
    {
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 15f));
        using var _ = sim;
        var (warned, comingFrames, firstComing) = (false, 0, double.NaN);
        sim.Run(Nobody, f =>
        {
            var coming = f.ComingAt(flown);
            warned |= f.Warning is not null;
            if (!warned)
                Assert.Null(coming); // nothing before its warning
            else if (f.Warning is not null)
            {
                Assert.NotNull(coming);
                Assert.Same(other, coming.Value.Other);
                Assert.Equal(SolidKind.Craft, coming.Value.With);
                Assert.Same(flown, coming.Value.Watched);
                if (comingFrames++ == 0)
                    firstComing = coming.Value.Seconds;
            }
            if (flown.State == FlightState.Crashed)
                Assert.Null(coming); // hit: over
            return flown.State == FlightState.Crashed && other!.State == FlightState.Crashed;
        }, maxSeconds: 17);
        output.WriteLine($"coming in {comingFrames} frames, first {firstComing:0.00} s off; flown {flown.State} {flown.CrashReason}");
        Assert.True(warned, "never warned of");
        Assert.True(comingFrames > 0);
        Assert.Equal(FlightState.Crashed, flown.State);
        sim.Step(new FlightInput());
        Assert.Null(sim.Flight.ComingAt(flown));
    }

    [Fact]
    public void The_ground_coming_at_a_small_views_vessel_is_coming_at_it()
    {
        using var sim = new Simulation(world: FastCollisionTests.Into("ground", 400f, 5f, 60f));
        var diver = sim.Flight.WorldVessels.First(v => v.Id == 0);
        Assert.NotSame(diver, sim.Flight.Controlled);
        IReadOnlyList<Vessel> views = [diver];
        Coming? seen = null;
        Assert.True(sim.Run((_, _) => new FlightInput(Watched: views), f =>
        {
            Assert.Null(f.ComingAt(f.Controlled)); // the rocket on the pad: nothing is coming at it
            seen = f.ComingAt(diver);
            return seen is not null;
        }, maxSeconds: 6), "nothing ever came at the small view's vessel");
        output.WriteLine($"{seen}");
        Assert.Equal(SolidKind.Ground, seen!.Value.With);
        Assert.Null(seen.Value.Other);
        Assert.Same(diver, seen.Value.Watched);
        Assert.Contains("Coming at " + WorldLog.Named(diver), Readout.FlightReadings(sim.Flight).Keys);
        Assert.Matches(@"^the ground in \d+[.,]\d s$", Readout.FlightReadings(sim.Flight)["Coming at " + WorldLog.Named(diver)]);
    }

    [Fact]
    public void Nothing_is_coming_at_a_small_views_vessel_before_its_warning_nor_with_nothing_coming()
    {
        // The flown one is the new rocket on the pad; Pair's two collide head on 15 s off, far away,
        // one watched: nothing is coming at it for its first seconds (the collision beyond its
        // lead), then the other is.
        using (var sim = new Simulation(world: FastCollisionTests.Pair(2000f, 5000f, 15f)))
        {
            var watched = sim.Flight.WorldVessels.First(v => v.Id == 0);
            Assert.NotSame(watched, sim.Flight.Controlled);
            IReadOnlyList<Vessel> views = [watched];
            var (before, first) = (0, (Coming?)null);
            sim.Run((_, _) => new FlightInput(Watched: views), f =>
            {
                first = f.ComingAt(watched);
                if (first is null)
                    before++;
                return first is not null;
            }, maxSeconds: 14);
            output.WriteLine($"nothing coming for {before} frames, then {first}");
            Assert.True(before > 60, $"coming after only {before} frames: before its warning");
            Assert.NotNull(first);
            Assert.Equal(1, first.Value.Other!.Id);
        }

        // One alone, coasting up and away from the planet: nothing ever comes at it.
        var alone = FastCollisionTests.Pair(2000f, 5000f, 15f).Vessels[0];
        using (var sim = new Simulation(world: new WorldState { Vessels = [alone], NextVesselId = 1 }))
        {
            var watched = sim.Flight.WorldVessels.First(v => v.Id == 0);
            IReadOnlyList<Vessel> views = [watched];
            sim.Run((_, _) => new FlightInput(Watched: views, NearMisses: true), f =>
            {
                Assert.Null(f.ComingAt(watched));
                return sim.Time > 3;
            }, maxSeconds: 4);
        }
    }

    [Fact]
    public void A_lost_other_hides_nothing_else_coming()
    {
        // Two wrecked against each other, and the rocket on the pad: given a near miss with a
        // wreck just before a fast collision with the ground, the collision is what is coming, not
        // nothing (each kept only while its other is there, before the sooner is chosen).
        using var sim = new Simulation(world: FastCollisionTests.Pair(2000f, 5000f, 1f));
        var wreck = sim.Flight.WorldVessels.First(v => v.Id == 0);
        Assert.True(sim.Run(Nobody, _ => wreck.State == FlightState.Crashed, maxSeconds: 3), "never wrecked");
        var flown = sim.Flight.Controlled;
        var ground = new Collision(flown, SolidKind.Ground, null, 0.5, 500f, 500f, flown.CentreOfMass);
        var miss = new NearMiss(flown, wreck, 0.2, 5f, 1000f, 0.5f);
        var comings = new Comings();
        comings.Keep(flown, ground, miss, null);
        var coming = comings.At(flown);
        output.WriteLine($"{coming}");
        Assert.NotNull(coming);
        Assert.Equal(SolidKind.Ground, coming.Value.With);
        Assert.Null(coming.Value.Other);
    }

    [Fact]
    public void A_near_miss_is_coming_only_with_the_setting_on()
    {
        // Pair's two head on at 1,000 m/s, passing beside each other 5 s from the start, within a
        // length but never within each other's sizes (as NearMissTests' pass): no fast collision.
        var beside = 2f * FastCollisionTests.BoosterSize + 1f;
        var world = FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: beside);
        foreach (var on in new[] { false, true })
        {
            var (sim, flown, other) = FastCollisionTests.Fly(world);
            using var _ = sim;
            Coming? seen = null;
            sim.Run((_, _) => new FlightInput(NearMisses: on), f =>
            {
                Assert.Null(f.Warning); // no fast collision in it
                seen ??= f.ComingAt(flown);
                return sim.Time > 6;
            }, maxSeconds: 7);
            output.WriteLine($"setting {(on ? "on" : "off")}: {seen}");
            if (!on)
                Assert.Null(seen);
            else
            {
                Assert.NotNull(seen);
                Assert.Same(other, seen.Value.Other);
                Assert.InRange(seen.Value.Seconds, 0.0, 5.1);
            }
        }
    }

    [Fact]
    public void Something_coming_at_the_flown_vessel_and_a_small_views_at_once_are_each_their_own()
    {
        // Two head-on pairs, a third of the way round the planet from each other: the flown one in
        // the first, the watched one in the second.
        var hitPoint = FastCollisionTests.PairHitPoint(5000f);
        var far = Planet.Centre + Vector3.Transform(hitPoint - Planet.Centre, Quaternion.CreateFromAxisAngle(Planet.North(hitPoint), MathF.PI * 2f / 3f));
        var first = FastCollisionTests.Pair(2000f, 5000f, 8f).Vessels;
        var second = FastCollisionTests.Pair(2000f, 5000f, 8f, over: far).Vessels;
        var world = new WorldState { Vessels = [first[0], first[1], second[0] with { Id = 2 }, second[1] with { Id = 3 }], NextVesselId = 4 };
        var (sim, flown, flownsOther) = FastCollisionTests.Fly(world);
        using var _ = sim;
        var (watched, watchedsOther) = (sim.Flight.WorldVessels.First(v => v.Id == 2), sim.Flight.WorldVessels.First(v => v.Id == 3));
        IReadOnlyList<Vessel> views = [watched];
        Assert.True(sim.Run((_, _) => new FlightInput(Watched: views), f => f.ComingAt(flown) is not null && f.ComingAt(watched) is not null, maxSeconds: 8),
            "never both coming at once");
        var (a, b) = (sim.Flight.ComingAt(flown)!.Value, sim.Flight.ComingAt(watched)!.Value);
        output.WriteLine($"{a}\n{b}");
        Assert.Same(flownsOther, a.Other);
        Assert.Same(watchedsOther, b.Other);
        Assert.Same(flown, a.Watched);
        Assert.Same(watched, b.Watched);
        Assert.Equal(2, sim.Flight.Comings.Length);
        var readings = Readout.FlightReadings(sim.Flight);
        Assert.StartsWith(WorldLog.Named(flownsOther!) + " in ", readings["Coming at " + WorldLog.Named(flown)]);
        Assert.StartsWith(WorldLog.Named(watchedsOther) + " in ", readings["Coming at " + WorldLog.Named(watched)]);
    }

    [Fact]
    public void One_wrecked_while_coming_is_coming_no_more()
    {
        // Pair's two head on at 2,000 m/s, colliding at 10 s; a third catches the other up from
        // behind, 1,500 m/s faster, and hits it at 7 s, while it is still coming at the flown one.
        var pair = FastCollisionTests.Pair(2000f, 5000f, 10f).Vessels;
        var third = FastCollisionTests.Overtaken(pair[1], 7f, -1500f, Vector3.Zero, id: 2);
        var (sim, flown, other) = FastCollisionTests.Fly(new WorldState { Vessels = [pair[0], pair[1], third], NextVesselId = 3 });
        using var _ = sim;
        var hitter = sim.Flight.WorldVessels.First(v => v.Id == 2);
        var (comingBefore, after) = (false, 0);
        sim.Run(Nobody, f =>
        {
            var coming = f.ComingAt(flown);
            if (other!.State != FlightState.Crashed)
                comingBefore |= coming is { } c && c.Other == other;
            else
            {
                Assert.True(coming is null || coming.Value.Other != other, $"still coming after it was wrecked: {coming}");
                after++;
            }
            return after > 30;
        }, maxSeconds: 10);
        output.WriteLine($"the other {other!.State} {other.CrashReason} at {sim.Time:0.00} s; the hitter {hitter.State}; {after} frames after");
        Assert.True(comingBefore, "it was never coming before it was wrecked: this shows nothing");
        Assert.Equal(FlightState.Crashed, other.State);
        Assert.StartsWith("Hit another vessel", other.CrashReason);
        Assert.NotEqual(FlightState.Crashed, flown.State);
    }

    [Fact]
    public void One_removed_for_room_while_coming_is_coming_no_more()
    {
        // Pair's two head on at 2,000 m/s, colliding at 10 s, the one coming the oldest in a world full
        // to its capacity, the other in a small view. The rocket on the pad stages, and the oldest
        // goes to make room for the stage dropped.
        var pair = FastCollisionTests.Pair(2000f, 5000f, 10f, flownFirst: false).Vessels;
        var pad = Scene.TestPads[0].Centre;
        var landed = Drops.Placed(pad, Planet.East(pad), 0f) with { State = FlightState.Landed };
        var full = pair.Concat(Enumerable.Range(2, Flight.Capacity - 3).Select(i => landed with { Id = i })).ToList();
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = full, NextVesselId = full.Count });
        var flight = sim.Flight;
        Assert.Equal(Flight.Capacity, flight.WorldVessels.Count);
        var (watched, oldest) = (flight.WorldVessels.First(v => v.Id == 0), flight.WorldVessels.First(v => v.Id == 1));
        Assert.Same(oldest, flight.WorldVessels[0]);
        IReadOnlyList<Vessel> views = [watched];
        Assert.True(sim.Run((_, _) => new FlightInput(Watched: views), f => f.ComingAt(watched) is { } c && c.Other == oldest, maxSeconds: 8),
            "it was never coming before it was removed: this shows nothing");

        sim.Step(new FlightInput(Watched: views, Launch: true));
        sim.Step(new FlightInput(Watched: views, Stage: true));
        Assert.DoesNotContain(oldest, flight.WorldVessels);
        Assert.NotEqual(FlightState.Crashed, oldest.State);
        for (var i = 0; i < 30; i++)
        {
            Assert.True(flight.ComingAt(watched) is not { } coming || coming.Other != oldest, $"still coming after it was removed: {flight.ComingAt(watched)}");
            sim.Step(new FlightInput(Watched: views));
        }
        output.WriteLine($"removed at {sim.Time:0.00} s; the watched one {watched.State}");
    }

    /// <summary>
    /// For the user to watch (<c>GAMEONE_WATCH=1</c>): a small view's booster warned of a collision
    /// head on at 1,000 m/s, its view flashing red (a collision's danger), then switched to 5 s before it, so the collision
    /// is seen slowed from the flown booster, the camera turned to keep the other in view.
    /// </summary>
    [Fact]
    public void A_small_views_warning_switched_to_for_the_crash_to_watch() =>
        SmallViewSwitchedTo(FastCollisionTests.Pair(1000f, 5000f, 10f), nearMisses: false);

    /// <summary>The same with a near miss, the setting on: its view flashing amber (it misses if nothing is done), the pass beside it slowed from the flown booster.</summary>
    [Fact]
    public void A_small_views_warning_switched_to_for_the_near_miss_to_watch() =>
        SmallViewSwitchedTo(NearMissTests.NearMissPass(1000f, 10f), nearMisses: true);

    private void SmallViewSwitchedTo(WorldState world, bool nearMisses)
    {
        using var sim = new Simulation(world: world);
        var flight = sim.Flight;
        var (rocket, viewed, other) = (flight.Controlled, flight.WorldVessels.First(v => v.Id == 0), flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(Watched: [viewed], NearMisses: nearMisses));
        sim.OpenView(viewed);
        IReadOnlyList<Vessel> views = [viewed];
        var (comingInView, switchedAt, passed) = (double.NaN, double.NaN, false);
        sim.Run((f, _) =>
        {
            if (!double.IsNaN(switchedAt) || f.ComingAt(viewed) is not { } coming || coming.Seconds > 5.0)
                return new FlightInput(Watched: views, NearMisses: nearMisses);
            switchedAt = sim.Time;
            views = [rocket]; // the one flown till now takes its place in the strip
            return new FlightInput(Watched: views, NearMisses: nearMisses, SwitchTo: viewed);
        }, f =>
        {
            if (f.ComingAt(viewed) is { } c && c.Other == other)
            {
                if (double.IsNaN(comingInView))
                    comingInView = sim.Time;
                // Amber for a near miss, red for a collision (Hud.ComingColor), and read out so.
                Assert.Equal(nearMisses, c.NearMiss);
                Assert.Equal(nearMisses, Readout.FlightReadings(f)["Coming at " + WorldLog.Named(viewed)].EndsWith(", a near miss"));
            }
            var closing = Vector3.Dot(Collisions.Middle(other) - Collisions.Middle(viewed), other.Velocity - viewed.Velocity) < 0f;
            passed |= !double.IsNaN(switchedAt) && (viewed.State == FlightState.Crashed || !closing);
            return passed && f.Slowed is null;
        }, maxSeconds: 20);
        output.WriteLine($"coming at the small view's from {comingInView:0.00} s, switched to at {switchedAt:0.00} s; it {viewed.State} {viewed.CrashReason}, the other {other.State}");
        Assert.False(double.IsNaN(comingInView), "never coming at the small view's vessel");
        Assert.True(comingInView < switchedAt - 1.0, "coming too late to see its view flash first");
        Assert.Same(viewed, flight.Controlled);
        Assert.True(passed, "never touched or passed");
        if (nearMisses)
            Assert.NotEqual(FlightState.Crashed, viewed.State);
        else
            Assert.StartsWith("Hit another vessel", viewed.CrashReason);
    }

    [Fact]
    public void The_flash_is_on_and_off_at_its_rate()
    {
        Assert.True(Hud.FlashOn(0));
        Assert.False(Hud.FlashOn(1 / (2.0 * Scene.ComingFlashHz) + 1e-6));
        Assert.True(Hud.FlashOn(1 / (double)Scene.ComingFlashHz + 1e-6));
    }

    [Fact]
    public void The_tape_offset_wraps_and_holds_at_the_ends()
    {
        // Degrees from the tape's middle, either way round north, held at its ends (160 shown).
        Assert.Equal(20f, Hud.TapeOffset(10f, 350f), 3);
        Assert.Equal(-20f, Hud.TapeOffset(350f, 10f), 3);
        Assert.Equal(80f, MathF.Abs(Hud.TapeOffset(180f, 0f)), 3);
        Assert.Equal(-80f, Hud.TapeOffset(100f, 280f), 3); // behind, a little to the left
        Assert.Equal(45f, Hud.TapeOffset(45f + 360f, 0f), 3); // the away mark's bearing, past a turn
    }

    [Fact]
    public void The_mark_is_on_the_vessel_coming_and_on_nothing_else()
    {
        // A vessel coming at the flown one is marked.
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 15f));
        using (sim)
        {
            Assert.Null(Markers.ComingVessel(sim.Flight)); // nothing coming yet
            Assert.True(sim.Run(Nobody, f => f.ComingAt(flown) is not null, maxSeconds: 15), "nothing ever came");
            Assert.Same(other, Markers.ComingVessel(sim.Flight));
        }

        // The ground, the sea, a floe, the drone ship, the tower and a mast coming are not (the
        // COLLISION line names them).
        foreach (var (what, name, dive) in new[]
        {
            ("ground", "the ground", 45f), ("sea", "the water", 45f), ("floe", "an ice floe", 45f), ("ship", "the drone ship", 45f),
            ("tower", "the service tower", 30f), ("mast", "a light mast", 45f),
        })
        {
            var (into, diver, _) = FastCollisionTests.Fly(what == "sea" ? IntoTheSea(1000f, 5f) : FastCollisionTests.Into(what, 1000f, 5f, diveDegrees: dive));
            using var _ = into;
            Assert.True(into.Run(Nobody, f => f.ComingAt(diver) is not null, maxSeconds: 5), $"{what}: nothing ever came");
            var coming = into.Flight.ComingAt(diver)!.Value;
            output.WriteLine($"{what}: {coming}");
            Assert.Equal(name, Collisions.Name(GroundBody.Planet, coming.With, coming.At));
            Assert.Null(Markers.ComingVessel(into.Flight));
        }
    }

    /// <summary>
    /// A booster diving at 45° into open water 800 m east of Home's drone ship (clear of it, and of
    /// any ice: the ships sail where none is) at <paramref name="speed"/>, hitting it after
    /// <paramref name="seconds"/>, as <see cref="FastCollisionTests.Into"/>'s dives.
    /// </summary>
    private static WorldState IntoTheSea(float speed, float seconds)
    {
        Rafts.Time = 0.0;
        var deck = Rafts.Ship.Current.Position;
        var water = Planet.Centre + Planet.Up(deck + Planet.East(deck) * 800f) * Planet.SeaRadius;
        Assert.True(Terrain.Height(Planet.Up(water)) < 0f, "the sea's point isn't over the sea");
        var dive = Vector3.Normalize(Planet.East(water) - Planet.Up(water));
        return new WorldState { Vessels = [FastCollisionTests.Flying(water, dive, speed, seconds, 0)], NextVesselId = 1 };
    }

    [Fact]
    public void Only_a_double_click_on_a_marker_or_the_red_mark_picks_its_vessel_or_lets_it_go()
    {
        // What a press does, by where it and the press before it were (the red mark is clicked as its
        // vessel's marker: Markers.Draw).
        var (one, two) = (new NavTarget(TargetKind.Vessel, 1), new NavTarget(TargetKind.Vessel, 2));
        var none = ((NavTarget?)null, false);
        Assert.Equal(none, Markers.Click(twice: false, first: null, one, current: null)); // a single click on a marker: nothing
        Assert.Equal(none, Markers.Click(twice: false, first: null, one, current: two)); // nor instead of the target
        Assert.Equal(none, Markers.Click(twice: false, first: null, one, current: one)); // nor on the target's own
        Assert.Equal(none, Markers.Click(twice: false, first: null, clicked: null, current: one)); // a single click off them all: kept
        Assert.Equal(((NavTarget?)one, false), Markers.Click(twice: true, first: one, one, current: null)); // a double click: targeted
        Assert.Equal(((NavTarget?)one, false), Markers.Click(twice: true, first: one, one, current: two)); // another instead
        Assert.Equal(((NavTarget?)null, true), Markers.Click(twice: true, first: one, one, current: one)); // the target's own: let go
        Assert.Equal(none, Markers.Click(twice: true, first: null, clicked: null, current: one)); // a double click off them all: kept
        Assert.Equal(none, Markers.Click(twice: true, first: null, one, current: two)); // its first press on a node or the path: theirs
        Assert.Equal(none, Markers.Click(twice: true, first: two, one, current: null)); // its first press on another marker
    }

    [Fact]
    public void A_press_over_a_marker_reaches_the_nodes_and_the_steering_rings()
    {
        // A marker under the pointer is none of what blocks them: a single click on it never
        // changes the target, so it is theirs as if the marker weren't there.
        Assert.False(Hud.NodesBlocked(menuOpen: false, ringsInUse: false));
        Assert.True(Hud.NodesBlocked(menuOpen: true, ringsInUse: false));
        Assert.True(Hud.NodesBlocked(menuOpen: false, ringsInUse: true));
        Assert.False(Hud.RingsBlocked(onPlan: false, nodeSelected: false));
        Assert.True(Hud.RingsBlocked(onPlan: true, nodeSelected: false));
        Assert.True(Hud.RingsBlocked(onPlan: false, nodeSelected: true));
    }

    [Fact]
    public void Presses_in_a_row_count_by_ImGui_s_double_click_time_and_distance()
    {
        Assert.Equal(1, Markers.PressCount(0, 0.1, 0f, 0.3f, 6f)); // the first
        Assert.Equal(2, Markers.PressCount(1, 0.2, 2f, 0.3f, 6f)); // a double click
        Assert.Equal(3, Markers.PressCount(2, 0.2, 2f, 0.3f, 6f)); // a third: no double click
        Assert.Equal(1, Markers.PressCount(1, 0.4, 2f, 0.3f, 6f)); // too late: a new first
        Assert.Equal(1, Markers.PressCount(1, 0.2, 7f, 0.3f, 6f)); // too far: a new first
    }

    [Fact]
    public void A_press_is_on_the_red_mark_then_a_marker_then_a_body_s_line_or_disc()
    {
        var (coming, marker) = (new NavTarget(TargetKind.Vessel, 1), new NavTarget(TargetKind.Vessel, 2));
        var moon = NavTarget.OfBody(GroundBody.Moon);
        Assert.Equal(coming, Markers.PressedOn(coming, onMarker: true, marker, moon));
        Assert.Equal(marker, Markers.PressedOn(null, onMarker: true, marker, moon));
        Assert.Null(Markers.PressedOn(null, onMarker: true, marker: null, moon)); // a marker of nothing to target, over the moon: nothing
        Assert.Equal(moon, Markers.PressedOn(null, onMarker: false, null, moon)); // its disc, its ring faded
        Assert.Null(Markers.PressedOn(null, onMarker: false, null, body: null)); // off them all
    }

    [Fact]
    public void Keeping_what_is_coming_allocates_nothing()
    {
        var beside = 2f * FastCollisionTests.BoosterSize + 1f;
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: beside));
        using var _ = sim;
        IReadOnlyList<Vessel> views = [other!];
        Assert.True(sim.Run((_, _) => new FlightInput(Watched: views, NearMisses: true),
            f => f.ComingAt(flown) is not null && f.ComingAt(other!) is not null, maxSeconds: 5), "never coming at both");
        sim.Flight.LookAhead(views, nearMisses: true); // (anything worked out once, first)
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 120; i++)
        {
            sim.Flight.LookAhead(views, nearMisses: true);
            Assert.True(sim.Flight.ComingAt(flown) is not null && sim.Flight.ComingAt(other!) is not null);
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"looking ahead 120 times: {bytes} bytes; {sim.Flight.ComingAt(flown)}");
        Assert.Equal(0, bytes);
        Assert.NotNull(sim.Flight.ComingAt(flown)); // asked again at the same time, nothing changes
    }
}
