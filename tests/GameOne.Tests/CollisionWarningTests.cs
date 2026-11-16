using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The warning of a fast collision (<see cref="Flight.Warning"/>): the flown vessel's soonest one,
/// shown in time to dodge it (its lead, <see cref="Collisions.WarningSeconds"/>, ahead), or at once
/// with no fuel to dodge with, and gone once it has hit or is none; and the time warp dropped once
/// for it, even in the middle of a 32x frame. A vessel only watched in a small view is never
/// warned of. The worlds are <see cref="FastCollisionTests"/>' builders, in the vacuum (5 km up);
/// "N s apart" is their centres coming together in N s.
/// </summary>
public sealed class CollisionWarningTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>Presses <see cref="FlightInput.Faster"/> until the player's rate is <paramref name="rate"/>.</summary>
    private static void WarpTo(Simulation sim, float rate)
    {
        while (sim.Flight.PlayerRate < rate)
            sim.Step(new FlightInput(Faster: true));
        Assert.Equal(rate, sim.Flight.PlayerRate);
    }

    [Fact]
    public void A_collision_is_warned_of_in_time_to_dodge()
    {
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 20f));
        using var _ = sim;
        Assert.True(flown.Fuel > 0f);
        var asking = new Collisions(sim.Flight);
        Assert.True(sim.Run(Nobody, f =>
        {
            if (f.Warning is not null)
                return true;
            // Not yet: so nothing it could be warned of is within its lead.
            if (asking.Soonest(flown, f.WorldVessels, Scene.CollisionHorizonSeconds) is { } o)
                Assert.True(o.Seconds > Collisions.WarningSeconds(flown, o), $"not warned of {o}, within its lead");
            return false;
        }, maxSeconds: 20), "never warned of");
        var warning = sim.Flight.Warning!.Value;
        var lead = Collisions.WarningSeconds(flown, warning);
        output.WriteLine($"warned at {sim.Time:0.000} s: {warning}; lead {lead:0.000} s");
        Assert.Equal(SolidKind.Craft, warning.With);
        Assert.Same(other, warning.Other);
        Assert.Same(flown, warning.Watched);
        Assert.InRange(warning.Seconds, lead - Simulation.FrameSeconds, lead);
        // The HUD's words, and the readout's the same.
        var line = Hud.WarningLine(warning);
        // In the HUD's own culture, as the rest of it is: "1,022 m/s" here, "1.022 m/s" in German.
        Assert.Equal($"COLLISION: another vessel in {warning.Seconds:0.0} s ({warning.ClosingSpeed:#,0} m/s)", line);
        Assert.Contains($"({1022f:#,0} m/s)", Hud.WarningLine(warning with { ClosingSpeed = 1022f }));
        Assert.NotEqual("1022", $"{1022f:#,0}"); // a thousands separator, whatever the culture's
        Assert.Equal(line, Readout.FlightReadings(sim.Flight)["Warning"]);
    }

    [Fact]
    public void Dodging_after_the_warning_clears_the_collision()
    {
        // Lying across the way it comes, its engine pushes it straight out of the way.
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 20f, lieAcross: 90f));
        using var _ = sim;
        Assert.True(sim.Run(Nobody, f => f.Warning is not null, maxSeconds: 20), "never warned of");
        var closest = sim.Time + sim.Flight.Warning!.Value.Seconds;
        output.WriteLine($"warned at {sim.Time:0.000} s, the closest approach at {closest:0.000} s");
        sim.Run((_, _) => new FlightInput(Throttle: 1f, FullThrottle: true), _ => false, maxSeconds: closest + 2.0 - sim.Time);
        output.WriteLine($"at {sim.Time:0.000} s: {flown.State} {flown.CrashReason}, {other!.State} {other.CrashReason}, warning {sim.Flight.Warning}");
        Assert.NotEqual(FlightState.Crashed, flown.State);
        Assert.NotEqual(FlightState.Crashed, other.State);
        Assert.Null(sim.Flight.Warning);
    }

    [Fact]
    public void Dodging_after_turning_clears_the_collision()
    {
        // The whole lead used, as a player would: nose along the way it comes (pointing at the
        // other), it waits the player's moment, turns a right angle at its own turn rate (its
        // engine off: the thrusters'), and only then burns out of the way. The turn takes no longer
        // than the lead's turn (Collisions.TurnSeconds), and leaves at least the lead's dodge to
        // burn in. If this fails, the lead (Collisions.WarningSeconds) is too short to dodge in.
        // Turned is within 2° (its thrust then 99.94% across): the lead's turn is the steady lag's
        // right angle, and the last of the lag settles from there as the burn begins.
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 20f));
        using var _ = sim;
        Assert.True(sim.Run(Nobody, f => f.Warning is not null, maxSeconds: 20), "never warned of");
        var warning = sim.Flight.Warning!.Value;
        var lead = Collisions.WarningSeconds(flown, warning);
        var turnTerm = Collisions.TurnSeconds(flown.TurnRate);
        var dodgeTerm = lead - turnTerm - Scene.WarningReactionSeconds;
        var closest = sim.Time + warning.Seconds;
        sim.Run(Nobody, _ => false, maxSeconds: Scene.WarningReactionSeconds);
        var turning = sim.Time;
        var across = Vector3.Normalize(Vector3.Cross(Planet.Up(flown.CentreOfMass), other!.CentreOfMass - flown.CentreOfMass));
        Assert.True(sim.Run((_, _) => new FlightInput(Hold: across), _ => Vector3.Dot(flown.Up, across) > MathF.Cos(float.DegreesToRadians(2f)), maxSeconds: 20),
            "it never turned");
        var turned = sim.Time - turning;
        var left = closest - sim.Time;
        output.WriteLine($"warned {warning.Seconds:0.000} s ahead (lead {lead:0.000} s); turned to within 2° in {turned:0.000} s"
            + $" (the lead's turn {turnTerm:0.000} s; a bare right angle at {flown.TurnRate:0.000} rad/s {MathF.PI / 2f / flown.TurnRate:0.000} s);"
            + $" {left:0.000} s left to burn (the lead's dodge {dodgeTerm:0.000} s)");
        Assert.True(turned <= turnTerm, $"the turn took {turned:0.000} s, the lead's {turnTerm:0.000} s");
        Assert.True(left >= dodgeTerm, $"{left:0.000} s were left to burn, the lead's dodge {dodgeTerm:0.000} s");
        sim.Run((_, _) => new FlightInput(Throttle: 1f, FullThrottle: true, Hold: across), _ => false, maxSeconds: closest + 2.0 - sim.Time);
        output.WriteLine($"at {sim.Time:0.000} s: {flown.State} {flown.CrashReason}, {other.State} {other.CrashReason}, warning {sim.Flight.Warning}");
        Assert.NotEqual(FlightState.Crashed, flown.State);
        Assert.NotEqual(FlightState.Crashed, other.State);
        Assert.Null(sim.Flight.Warning);
    }

    [Fact]
    public void Two_collisions_of_one_kind_in_a_row_are_each_warned_of()
    {
        // Past one of a landing zone's masts and on to the next, 40 m on: each a collision of its
        // own, each warned of and each dropping the warp (warped again by hand after the first).
        // Low and fast through the thick air, the way there is short (3 s), so with no fuel it is
        // warned of as soon as it is seen: warped first, flying the pad's rocket, then switched to.
        var (world, first, second) = FastCollisionTests.PastTwoMasts(600f, 3f, beside: 8f, fuel: 0f);
        using var sim = new Simulation(world: world);
        var flown = sim.Flight.WorldVessels.First(v => v.Id == 0);
        WarpTo(sim, 4f);
        Assert.Null(sim.Flight.Warning);
        sim.Step(new FlightInput(SwitchTo: flown));
        Assert.True(sim.Run(Nobody, f => f.Warning is not null, maxSeconds: 3), "never warned of the first");
        var one = sim.Flight.Warning!.Value;
        output.WriteLine($"at {sim.Time:0.000} s: {one}; {Vector3.Distance(one.At, first):0.0} m from the first mast's middle");
        Assert.Equal(SolidKind.Mast, one.With);
        Assert.True(Vector3.Distance(one.At, first) <= Scene.MastHeight / 2f + 0.1f, "the first warning isn't the first mast's");
        Assert.Equal(1f, sim.Flight.PlayerRate);
        WarpTo(sim, 2f);
        Assert.True(sim.Run(Nobody, f => f.Warning is { } w && Vector3.Distance(w.At, one.At) > 20f, maxSeconds: 4), "never warned of the second");
        var two = sim.Flight.Warning!.Value;
        output.WriteLine($"at {sim.Time:0.000} s: {two}; {Vector3.Distance(two.At, second):0.0} m from the second mast's middle; {flown.State}");
        Assert.Equal(SolidKind.Mast, two.With);
        Assert.True(Vector3.Distance(two.At, second) <= Scene.MastHeight / 2f + 0.1f, "the second warning isn't the second mast's");
        Assert.NotEqual(FlightState.Crashed, flown.State);
        Assert.Equal(1f, sim.Flight.PlayerRate);
    }

    [Theory]
    [InlineData(0.2, false)]
    [InlineData(Scene.WarnedGraceSeconds + 0.2, true)]
    public void A_collision_no_fast_one_for_a_while_is_new_only_past_the_grace(double burnSeconds, bool dropsAgain)
    {
        // Its nose held against their closing, a burn slows the closing enough that it would hit
        // slower than a fast collision (none, while it burns), still on its way to the other; cut, it
        // is one again. After a few steps it is the collision already warned of, and the warp warped
        // again by hand stays; gone for longer than the grace (Scene.WarnedGraceSeconds), it is a
        // new one, and drops the warp again.
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(400f, 5000f, 20f, lieAcross: 180f));
        using var _ = sim;
        Vector3 Against() => Vector3.Normalize(other!.Velocity - flown.Velocity);
        WarpTo(sim, 4f);
        Assert.True(sim.Run((_, _) => new FlightInput(Hold: Against()), f => f.Warning is not null, maxSeconds: 20), "never warned of");
        Assert.Equal(1f, sim.Flight.PlayerRate);
        WarpTo(sim, 2f);
        double? goneAt = null;
        sim.Run((_, _) => new FlightInput(FullThrottle: true, Hold: Against()), f =>
        {
            if (f.Warning is null)
                goneAt ??= f.WorldTime;
            else
                Assert.Null(goneAt); // gone for the whole burn, once gone
            return goneAt is { } at && f.WorldTime - at >= burnSeconds;
        }, maxSeconds: burnSeconds + 0.5);
        Assert.True(goneAt is not null, "the burn never took it under a fast collision");
        Assert.Equal(2f, sim.Flight.PlayerRate);
        sim.Step(new FlightInput(CutThrottle: true, Hold: Against()));
        Assert.True(sim.Run((_, _) => new FlightInput(Hold: Against()), f => f.Warning is not null, maxSeconds: 0.5), "never a fast collision again");
        output.WriteLine($"gone {sim.Flight.WorldTime - goneAt!.Value:0.000} s; at {sim.Time:0.000} s: {sim.Flight.Warning}");
        Assert.Equal(dropsAgain ? 1f : 2f, sim.Flight.PlayerRate);
    }

    [Fact]
    public void A_solid_is_told_apart_by_a_radius_under_half_the_closest_two_of_its_kind()
    {
        // The radius a solid's collision is told by (Scene.SameSolidRadius) is its own, not the
        // flown vessel's size (a large one would run two masts together): under half the closest
        // two masts stand at any one place (a pad, a zone or a drone ship), and the closest two
        // towers.
        var closestMasts = float.MaxValue;
        foreach (var place in PadLights.All)
            for (var i = 0; i < place.Masts.Length; i++)
                for (var j = i + 1; j < place.Masts.Length; j++)
                    closestMasts = MathF.Min(closestMasts, Vector3.Distance(place.Masts[i].Foot, place.Masts[j].Foot));
        var closestTowers = float.MaxValue;
        for (var i = 0; i < LaunchSites.All.Length; i++)
            for (var j = i + 1; j < LaunchSites.All.Length; j++)
                closestTowers = MathF.Min(closestTowers, Vector3.Distance(LaunchSites.All[i].TowerFoot, LaunchSites.All[j].TowerFoot));
        output.WriteLine($"the closest two masts {closestMasts:0.0} m apart, towers {closestTowers:0} m; told apart within {Scene.SameSolidRadius} m");
        Assert.True(2f * Scene.SameSolidRadius < closestMasts);
        Assert.True(2f * Scene.SameSolidRadius < closestTowers);
    }

    [Fact]
    public void Switching_vessel_forgets_the_collisions_warned_of()
    {
        // The same collision, flown from the other side and back: each switch starts afresh, so it
        // is warned of (and the warp dropped) again each time.
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 20f));
        using var _ = sim;
        WarpTo(sim, 2f);
        Assert.True(sim.Run(Nobody, f => f.Warning is not null, maxSeconds: 20), "never warned of");
        Assert.Equal(1f, sim.Flight.PlayerRate);
        WarpTo(sim, 2f);
        sim.Step(new FlightInput(SwitchTo: other));
        Assert.Same(flown, sim.Flight.Warning?.Other);
        Assert.Equal(1f, sim.Flight.PlayerRate);
        WarpTo(sim, 2f);
        sim.Step(new FlightInput(SwitchTo: flown));
        Assert.Same(other, sim.Flight.Warning?.Other);
        Assert.Equal(1f, sim.Flight.PlayerRate); // the first flown's memory of it went with the switch
    }

    [Fact]
    public void Looking_ahead_allocates_nothing_a_step()
    {
        // A stretch of steps with a collision coming and warned of allocates no more than the same
        // stretch with none (the pair passing 3 km apart): the step itself allocates a little,
        // in the physics (TODO.md, "the flight's step allocates"), the same with or without. And
        // the looking ahead, asked on its own over and over with the collision warned of, nothing.
        const int Steps = 120;
        long Stretch(float sideways, bool warned)
        {
            var (sim, _, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 20f, sideways: sideways));
            using var _ = sim;
            sim.Run(Nobody, _ => false, maxSeconds: 12);
            Assert.Equal(warned, sim.Flight.Warning is not null);
            sim.Step(default); // (anything worked out once, first)
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Steps; i++)
                sim.Step(default);
            var stepped = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(warned, sim.Flight.Warning is not null);
            if (warned)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < Steps; i++)
                    sim.Flight.LookAhead();
                var looking = GC.GetAllocatedBytesForCurrentThread() - before;
                output.WriteLine($"looking ahead {Steps} times on its own: {looking} bytes");
                Assert.Equal(0, looking);
            }
            return stepped;
        }
        var (with, without) = (Stretch(0f, warned: true), Stretch(3000f, warned: false));
        output.WriteLine($"{Steps} steps: {with} bytes with a collision warned of, {without} with none");
        Assert.Equal(without, with);
    }

    [Fact]
    public void Without_fuel_it_is_warned_of_at_once()
    {
        // No dodge is possible, so there is no lead to wait for: as soon as it is found.
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 15f, fuel: 0f));
        using var _ = sim;
        output.WriteLine($"after the first frame: {sim.Flight.Warning}");
        Assert.NotNull(sim.Flight.Warning);
        Assert.Same(other, sim.Flight.Warning.Value.Other);
        Assert.Same(flown, sim.Flight.Warning.Value.Watched);
    }

    [Fact]
    public void The_warning_drops_the_warp_once()
    {
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 20f));
        using var _ = sim;
        WarpTo(sim, 8f);
        Assert.True(sim.Run(Nobody, f => f.Warning is not null, maxSeconds: 20), "never warned of");
        output.WriteLine($"warned at {sim.Time:0.000} s: {sim.Flight.Warning}");
        Assert.Equal(1f, sim.Flight.PlayerRate);
        // Warped again by hand: this collision has been warned of, so it isn't dropped again.
        WarpTo(sim, 4f);
        var frames = 0;
        Assert.True(sim.Run(Nobody, f =>
        {
            Assert.Equal(4f, f.PlayerRate);
            frames++;
            return f.Warning is null;
        }, maxSeconds: 20), "the collision never came");
        output.WriteLine($"hit at {sim.Time:0.000} s, after {frames} frames at 4x: {flown.State} {flown.CrashReason}");
        Assert.True(frames > 1);
        Assert.Equal(4f, sim.Flight.PlayerRate);
    }

    [Fact]
    public void Warping_into_a_collision_drops_out_of_warp_in_time()
    {
        // At 32x a frame is 32 steps of game time: the warning must stop the frame at its own
        // step, not after the rest of the frame (half a second of game time) has run past it.
        // 15 km up: set off a minute before at 5 km, the one going west (slower among the stars)
        // falls from far out through the air on its way and is lost to the ground first.
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 15000f, 60f));
        using var _ = sim;
        WarpTo(sim, 32f);
        Assert.True(sim.Run(Nobody, f => f.Warning is not null, maxSeconds: 60), "never warned of");
        var warning = sim.Flight.Warning!.Value;
        var lead = Collisions.WarningSeconds(flown, warning);
        output.WriteLine($"warned at {sim.Time:0.000} s: {warning}; lead {lead:0.000} s");
        Assert.True(warning.Seconds >= lead - Flight.WarpStepSeconds, $"warned {warning.Seconds:0.000} s ahead, its lead {lead:0.000} s");
        Assert.Equal(1f, sim.Flight.PlayerRate);
    }

    [Fact]
    public void A_vessel_in_a_small_view_is_not_warned_of()
    {
        // The flown one is the new rocket on the pad; the pair hit far off, one of them watched.
        using var sim = new Simulation(world: FastCollisionTests.Pair(1000f, 5000f, 10f));
        var watched = sim.Flight.WorldVessels.First(v => v.Id == 0);
        Assert.NotSame(watched, sim.Flight.Controlled);
        IReadOnlyList<Vessel> views = [watched];
        var asking = new Collisions(sim.Flight);
        var seen = false;
        sim.Run((_, _) => new FlightInput(Watched: views), f =>
        {
            Assert.Null(f.Warning);
            seen |= asking.Soonest(watched, f.WorldVessels, Scene.CollisionHorizonSeconds) is not null;
            return watched.State == FlightState.Crashed;
        }, maxSeconds: 12);
        Assert.True(seen, "the watched one never had a collision to be warned of");
    }

    [Fact]
    public void An_empty_stage_under_a_fuelled_one_is_warned_of_by_the_next_ones_push()
    {
        // A two-stage rocket whose booster is empty, its upper stage half full: it can still dodge
        // (dropping the booster and lighting the upper stage), so its lead is the upper stage's,
        // not "at once" as with no fuel left anywhere. With both empty, it is at once.
        var (at, _, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var upper = Vehicle.TwoStage.Stages[1];
        var state = Drops.Placed(at, Planet.East(at), 0f, vehicle: Vehicle.TwoStage, last: 1, mission: Mission.ReachOrbit, clearance: 1000f);
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [state with { Fuel = [0f, upper.FuelMass * 0.5f] }], NextVesselId = 1 });
        var rocket = sim.Flight.WorldVessels.First(v => v.Id == 0);
        var collision = new Collision(rocket, SolidKind.Ground, null, 5.0, 1000f, 1000f, at);
        var lead = Collisions.WarningSeconds(rocket, collision);
        var push = upper.Thrust / (upper.DryMass + upper.FuelMass * 0.5f);
        var expected = Collisions.TurnSeconds(rocket.TurnRate) + MathF.Sqrt(2f * Collisions.Size(rocket) / push) + Scene.WarningReactionSeconds;
        output.WriteLine($"its booster empty, its upper stage half full: lead {lead:0.000} s (the upper stage's push {push:0.0} m/s², {expected:0.000} s)");
        Assert.Equal(0f, rocket.Fuel);
        Assert.Equal(expected, lead, 3);

        using var dry = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [state with { Fuel = [0f, 0f] }], NextVesselId = 1 });
        var empty = dry.Flight.WorldVessels.First(v => v.Id == 0);
        Assert.Equal(double.PositiveInfinity, Collisions.WarningSeconds(empty, collision with { Watched = empty }));
    }
}
