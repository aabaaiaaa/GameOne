using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The world's log (<see cref="LogEntry"/>): each vessel lost is written once, whoever lost it (the
/// physics, the formula, or the world's room), with when, how, how fast, where and whether it was seen.
/// </summary>
public sealed class WorldLogTests
{
    private static readonly Pilot Nobody = (_, _) => default;

    [Fact]
    public void A_vessel_crashed_on_the_ground_is_logged_with_its_time_reason_and_speed()
    {
        using var sim = Drops.At(Scene.LandingZoneCentre, Planet.East(Scene.LandingZoneCentre), 20f);
        var v = sim.Flight.Controlled;
        Assert.True(sim.Run(Nobody, _ => v.State == FlightState.Crashed, maxSeconds: 10), "it never crashed");
        sim.Run(Nobody, _ => false, maxSeconds: 2); // the wreck stays: still one entry
        var entry = Assert.Single(Losses(sim.Flight));
        Assert.Equal($"{v.Name} {v.Id}", entry.What);
        Assert.Equal("Hit the ground", entry.How);
        Assert.Equal(WorldLog.SpeedIn(v.CrashReason), entry.Speed); // its touch's
        Assert.InRange(entry.Speed!.Value, 18f, 40f);
        Assert.Equal("Home", entry.Where);
        Assert.True(entry.Witnessed);
        Assert.InRange(entry.Time, sim.Flight.WorldTime - 3.0, sim.Flight.WorldTime);
    }

    [Fact]
    public void Two_vessels_that_collide_are_one_entry()
    {
        var (sim, below, above) = HitTests.DroppedOnto(0.3f, 12f);
        using var _ = sim;
        Assert.True(sim.Run(Nobody, _ => above.State == FlightState.Crashed && below.State == FlightState.Crashed, maxSeconds: 3));
        sim.Run(Nobody, _ => false, maxSeconds: 1);
        var entry = Assert.Single(Losses(sim.Flight));
        Assert.Contains(" and ", entry.What);
        Assert.Contains($"{below.Name} {below.Id}", entry.What);
        Assert.Contains($"{above.Name} {above.Id}", entry.What);
        Assert.Equal("Hit another vessel", entry.How);
        // Their closing speed (the harder of the two's, should they be judged a step apart).
        Assert.Equal(MathF.Max(WorldLog.SpeedIn(above.CrashReason)!.Value, WorldLog.SpeedIn(below.CrashReason)!.Value), entry.Speed);
    }

    [Fact]
    public void Two_vessels_wrecked_by_one_collision_steps_apart_are_one_entry()
    {
        // Two satellites far apart under formula, each wrecked by hitting the other (as the physics
        // may judge the two of a contact a step or more apart), with a third's loss between: the
        // first's entry is the pair's, when the first was lost, at the harder of their closings;
        // the third's its own.
        var (sim, a, b) = UnwatchedTests.Unflown(new WorldState
        {
            Vessels = [UnwatchedTests.Satellite(0, 9000f, 0f, 30f), UnwatchedTests.Satellite(1, 9000f, 0f, 90f), UnwatchedTests.Satellite(2, 9000f, 0f, 150f)],
            NextVesselId = 3,
        }, UnwatchedTests.Opposite);
        using var _ = sim;
        var c = sim.Flight.WorldVessels.Single(v => v.Id == 2);
        a.WreckByFormula(Vessel.HitAnotherVessel(20f), a.CentreOfMass, hit: b.Craft);
        sim.Step(default);
        var lostAt = Assert.Single(Losses(sim.Flight)).Time;
        c.WreckByFormula(Vessel.HitAnotherVessel(9f), c.CentreOfMass, hit: null);
        sim.Step(default);
        sim.Step(default);
        b.WreckByFormula(Vessel.HitAnotherVessel(22f), b.CentreOfMass, hit: a.Craft);
        sim.Step(default);
        sim.Step(default);
        Assert.Equal(2, Losses(sim.Flight).Count);
        var pair = Losses(sim.Flight)[0];
        Assert.Equal($"{a.Name} {a.Id} and {b.Name} {b.Id}", pair.What);
        Assert.Equal(lostAt, pair.Time);
        Assert.Equal(22f, pair.Speed);
        Assert.Equal($"{c.Name} {c.Id}", Losses(sim.Flight)[1].What);
    }

    [Fact]
    public void An_unwatched_loss_is_logged_as_not_witnessed()
    {
        // Left alone over Polar's pad, it falls by formula onto the ground.
        var site = LaunchSites.Named("Polar")!;
        var up = Planet.Up(site.Pad);
        var state = Drops.Placed(site.Pad, Planet.East(site.Pad), 100f);
        state = state with { Position = state.Position + up * 3000f };
        var (sim, v, watch) = FormulaTests.Start(state, watched: false);
        using var _ = sim;
        FormulaTests.Fly(sim, v, watch, 90, x => x.State == FlightState.Crashed);
        Assert.Equal(FlightState.Crashed, v.State);
        var entry = Assert.Single(Losses(sim.Flight));
        Assert.False(entry.Witnessed);
        Assert.Equal("Polar", entry.Where);
        Assert.InRange(entry.Speed!.Value, 90f, 400f);
    }

    [Fact]
    public void Vessels_removed_for_room_are_logged_unseen_and_without_a_speed()
    {
        // 1,008 in a world that holds 1,000: the oldest eight go as it is loaded (never brought to
        // life), and one more as the new rocket is put on the pad, every vessel still in the
        // physics then, before any step has chosen who is to be there: none was seen.
        var world = WorldTests.ManySatellites(Flight.Capacity / 4 + 2);
        using var sim = new Simulation(Mission.ReachOrbit, world: world);
        var log = Losses(sim.Flight);
        var gone = world.Vessels.Count - (Flight.Capacity - 1);
        Assert.Equal(gone, log.Count);
        Assert.Equal(gone, sim.Flight.RemovedForRoom);
        for (var i = 0; i < gone; i++)
        {
            Assert.Equal(WorldLog.RemovedForRoom, log[i].How);
            Assert.Equal(WorldLog.Named(world.Vessels[i]), log[i].What); // oldest first
            Assert.Equal($"{sim.Flight.WorldVessels[0].Name} {world.Vessels[i].Id}", log[i].What); // named as one brought to life is
            Assert.Null(log[i].Speed);
            Assert.False(log[i].Witnessed, $"{log[i].What} was seen");
        }
        Assert.DoesNotContain("m/s", Hud.LogLine(log[0]));
        Assert.Contains(WorldLog.RemovedForRoom + ", ", Hud.LogLine(log[0]));
    }

    [Fact]
    public void A_vessel_cleared_from_the_launch_pad_is_logged()
    {
        // A booster standing on Home's pad as a world is loaded: cleared for the new rocket, never seen.
        static VesselState StandingOn(LaunchSite site, int id) =>
            Drops.Placed(site.Pad, Planet.East(site.Pad), 0f, id: id) with { State = FlightState.Landed, HasBeenAirborne = true };
        using (var sim = new Simulation(world: new WorldState { Vessels = [StandingOn(LaunchSites.Home, 0)], NextVesselId = 1 }))
        {
            Assert.Equal(1, sim.Flight.ClearedFromPad);
            var entry = Assert.Single(Losses(sim.Flight));
            Assert.Equal(WorldLog.ClearedFromPad, entry.How);
            Assert.EndsWith(" 0", entry.What);
            Assert.Equal("Home", entry.Where);
            Assert.Null(entry.Speed);
            Assert.False(entry.Witnessed);
        }

        // One standing on Polar's, flown, and a flight chosen from Polar: it was in the physics,
        // seen as it went. (The rocket never launched on Home's pad is just replaced, unlogged.)
        var polar = LaunchSites.Named("Polar")!;
        using (var sim = new Simulation(world: new WorldState { Vessels = [StandingOn(polar, 0)], NextVesselId = 1 }))
        {
            Assert.Empty(Losses(sim.Flight));
            sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.Single(v => v.Id == 0)));
            sim.Step(default);
            sim.Step(new FlightInput(Choose: Mission.LaunchAndLand, From: polar));
            Assert.Equal(1, sim.Flight.ClearedFromPad);
            var entry = Assert.Single(Losses(sim.Flight));
            Assert.Equal(WorldLog.ClearedFromPad, entry.How);
            Assert.Equal("Polar", entry.Where);
            Assert.True(entry.Witnessed);
        }
    }

    [Fact]
    public void One_that_hit_two_in_one_step_leaves_all_three_in_the_log()
    {
        // A wrecked by hitting B and C at once (a booster falling onto two touching ones): its
        // hardest is B, and each of the two hit it. Whichever order they are written in (A first,
        // or C), all three losses are in the log, each once: a pair's entry and the third's own
        // (C must not join A's entry, "A and B", making it "A and C" and losing B's).
        foreach (var cFirst in new[] { false, true })
        {
            var (sim, a, b) = UnwatchedTests.Unflown(new WorldState
            {
                Vessels = cFirst
                    ? [UnwatchedTests.Satellite(2, 9000f, 0f, 150f), UnwatchedTests.Satellite(0, 9000f, 0f, 30f), UnwatchedTests.Satellite(1, 9000f, 0f, 90f)]
                    : [UnwatchedTests.Satellite(0, 9000f, 0f, 30f), UnwatchedTests.Satellite(1, 9000f, 0f, 90f), UnwatchedTests.Satellite(2, 9000f, 0f, 150f)],
                NextVesselId = 3,
            }, UnwatchedTests.Opposite);
            using var _ = sim;
            var three = sim.Flight.WorldVessels.Where(v => v.Id < 3).OrderBy(v => v.Id).ToArray();
            var (va, vb, vc) = (three[0], three[1], three[2]);
            va.WreckByFormula(Vessel.HitAnotherVessel(30f), va.CentreOfMass, hit: vb.Craft);
            vb.WreckByFormula(Vessel.HitAnotherVessel(30f), vb.CentreOfMass, hit: va.Craft);
            vc.WreckByFormula(Vessel.HitAnotherVessel(20f), vc.CentreOfMass, hit: va.Craft);
            sim.Step(default);
            sim.Step(default);
            var log = Losses(sim.Flight).Select(e => e.What).ToArray();
            Assert.Equal(2, log.Length);
            foreach (var v in three)
                Assert.Single(log, w => w.Contains($"{v.Name} {v.Id}"));
        }
    }

    [Fact]
    public void Two_pairs_colliding_side_by_side_are_an_entry_each_by_who_hit_whom()
    {
        // Two head-on pairs under formula, 40 m apart side by side, collision at the same moment: each
        // of the four wrecked within 100 m of the other pair's, so only who actually hit whom
        // tells the pairs, not how near they ended.
        var hitPoint = UnwatchedTests.Opposite;
        var north = Planet.North(hitPoint);
        var first = FastCollisionTests.Pair(500f, 5000f, UnwatchedTests.HitsAt, over: hitPoint);
        var second = FastCollisionTests.Pair(500f, 5000f, UnwatchedTests.HitsAt, over: hitPoint + north * 40f);
        var world = new WorldState
        {
            Vessels = [.. first.Vessels, .. second.Vessels.Select(s => s with { Id = s.Id + 2 })],
            NextVesselId = 4,
        };
        var (sim, a, _) = UnwatchedTests.Unflown(world, hitPoint);
        using var _ = sim;
        var four = sim.Flight.WorldVessels.Where(v => v.Id < 4).ToArray();
        Assert.True(sim.Run(Nobody, _ => four.All(v => v.State == FlightState.Crashed), maxSeconds: UnwatchedTests.HitsAt + 2f),
            string.Join("; ", four.Select(v => $"{v.Id} {v.State}")));
        Assert.True(Vector3.Distance(four[0].CrashPosition, four[2].CrashPosition) < 100f, "the pairs ended too far apart to show this");
        var entries = Losses(sim.Flight).Select(e => e.What).OrderBy(w => w).ToArray();
        Assert.Equal(2, entries.Length);
        string Pair(int x, int y) => $"{four[x].Name} {x} and {four[y].Name} {y}";
        Assert.Contains(entries, w => w == Pair(0, 1) || w == Pair(1, 0));
        Assert.Contains(entries, w => w == Pair(2, 3) || w == Pair(3, 2));
    }

    [Fact]
    public void The_log_keeps_the_newest_entries_only()
    {
        // 300 already there and one more lost (the 301st): the first goes.
        var old = Enumerable.Range(0, Scene.WorldLogEntries).Select(i => new LogEntry(i, $"Old {i}", "Hit the ground", 10f, "Home", true)).ToArray();
        var at = Scene.LandingZoneCentre;
        var world = new WorldState { Vessels = [Drops.Placed(at, Planet.East(at), 20f)], NextVesselId = 1, Log = old };
        using var sim = new Simulation(Mission.FreePlay, world: world); // no run, so nothing in the log but the losses
        Assert.Equal(Scene.WorldLogEntries, sim.Flight.Log.Count);
        var v = sim.Flight.WorldVessels.Single(x => x.Id == 0);
        sim.Step(new FlightInput(SwitchTo: v));
        Assert.True(sim.Run(Nobody, _ => v.State == FlightState.Crashed, maxSeconds: 10));
        sim.Step(default);
        Assert.Equal(Scene.WorldLogEntries, sim.Flight.Log.Count);
        Assert.Equal("Old 1", sim.Flight.Log[0].What);
        Assert.Equal($"{v.Name} {v.Id}", sim.Flight.Log[^1].What);
    }

    /// <summary>The log's entries of vessels lost or removed, leaving out the missions' (a run begun, a step done, a run ended), which name a flight.</summary>
    internal static IReadOnlyList<LogEntry> Losses(Flight flight) => [.. flight.Log.Where(e => !e.What.EndsWith(')'))];

    [Fact]
    public void Missions_are_logged()
    {
        // A run begun, its one step done with its points, then completed with its score: each an
        // entry of its own, in order, at its site and seen.
        var step = false;
        var mission = new Mission
        {
            Name = "TEST",
            Briefing = "",
            Vehicle = Vehicle.Booster,
            Steps = [new("The step", _ => step, Points: _ => 1800)],
        };
        using var sim = new Simulation(mission);
        sim.Step(default);
        step = true;
        sim.Step(default);
        sim.Step(default);
        var log = sim.Flight.Log;
        Assert.Equal([WorldLog.Begun, WorldLog.StepDone("The step", 1800), WorldLog.Completed(1800)], log.Select(e => e.How));
        Assert.All(log, e => Assert.Equal(("TEST (flight 1)", (float?)null, "Home", true), (e.What, e.Speed, e.Where, e.Witnessed)));
        Assert.Contains("1,800", WorldLog.Completed(1800));
        Assert.Empty(Losses(sim.Flight));

        // Free play has no run, so nothing of it is logged.
        using var free = new Simulation(Mission.FreePlay);
        free.Step(new FlightInput(Launch: true, FullThrottle: true));
        free.Step(default);
        Assert.Empty(free.Flight.Log);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_reason_is_worded_without_its_advice_and_its_speed_is_the_entrys(bool legs)
    {
        // Each reason the game gives (the physics' and the formula's are the same strings): its
        // first sentence or clause, its speed taken out to be the entry's, so a line in the log
        // never says two speeds (it once read "Hit another vessel at 313.5 m/s, 249 m/s").
        Assert.Equal("Hit another vessel", WorldLog.How(Vessel.HitAnotherVessel(313.5f)));
        Assert.Equal(313.5f, WorldLog.SpeedIn(Vessel.HitAnotherVessel(313.5f)));
        Assert.Equal("Hit the ground", WorldLog.How(Vessel.HitTheGround(18.2f, GroundBody.Planet)));
        Assert.Equal(18.2f, WorldLog.SpeedIn(Vessel.HitTheGround(18.2f, GroundBody.Planet)));
        Assert.Equal("Hit the water", WorldLog.How(Vessel.HitTheWater(41f)));
        Assert.Equal(41f, WorldLog.SpeedIn(Vessel.HitTheWater(41f)));
        // The bell's advice comes after a colon, not a full stop.
        var at = Scene.LandingZoneCentre;
        using var sim = new Simulation(world: new WorldState { Vessels = [Drops.Placed(at, Planet.East(at), 0f, legsOut: legs)], NextVesselId = 1 });
        var bell = sim.Flight.WorldVessels.Single(x => x.Id == 0).OnItsBell(5.2f);
        Assert.Equal("Came down on its engine bell", WorldLog.How(bell));
        Assert.Equal(5.2f, WorldLog.SpeedIn(bell));
        // The break-up's colon begins its cause, not advice: cut there all the same, as a log's
        // line wants only what happened.
        var payload = sim.Flight.WorldVessels.Single(x => x.Id == 0);
        Assert.Equal($"The {payload.Vehicle.Stages[payload.TopStage].Name.ToLowerInvariant()} broke up", WorldLog.How(payload.BreakUpReason));
        Assert.Null(WorldLog.SpeedIn(payload.BreakUpReason));
        // No speed in it: none taken, and the entry's is the vessel's own.
        Assert.Equal("Hit a light mast", WorldLog.How(Vessel.MastReason));
        Assert.Null(WorldLog.SpeedIn(Vessel.MastReason));
        Assert.Equal("destroyed", WorldLog.How(null));
    }
}
