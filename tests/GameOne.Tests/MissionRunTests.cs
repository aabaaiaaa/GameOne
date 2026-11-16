using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// Runs: each mission begun is judged by its own vessels (the rocket and every part that comes off
/// it), whichever vessel is flown, and any number can be in progress in a world at once. The
/// mission panel's run is the flown vessel's.
/// </summary>
public sealed class MissionRunTests
{
    /// <summary>What a test mission's second step reads.</summary>
    private sealed class Switch
    {
        public bool On, Lost;
    }

    /// <summary>
    /// A booster mission whose steps are its launch and a step done when <paramref name="step"/> is
    /// on (lost when it is lost), worth 1,800; and with <paramref name="bonus"/>, a bonus never done nor lost.
    /// </summary>
    private static Mission Test(string name, Switch step, bool bonus = false) => new()
    {
        Name = name,
        Briefing = "",
        Vehicle = Vehicle.Booster,
        Steps =
        [
            new("Launch", r => r.Vessels[0].State != FlightState.Ready),
            new("The switch", _ => step.On, Lost: _ => step.Lost, Points: _ => 1800),
            .. bonus ? [new MissionStep("Bonus: never", _ => false, Optional: true)] : Array.Empty<MissionStep>(),
        ],
    };

    private static LaunchSite Highcrest => LaunchSites.All.Single(s => s.Name == "Highcrest");

    /// <summary>Mission A launched from Highcrest, then mission B chosen at Home (its rocket flown, on the pad).</summary>
    private static (Simulation Sim, Switch A, Switch B) TwoRuns()
    {
        var (a, b) = (new Switch(), new Switch());
        var sim = new Simulation(Test("A", a), site: Highcrest);
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Step(new FlightInput(Choose: Test("B", b), From: LaunchSites.Home));
        return (sim, a, b);
    }

    [Fact]
    public void Two_runs_in_progress_each_tick_their_own_steps()
    {
        var (sim, a, _) = TwoRuns();
        using var _ = sim;
        var flight = sim.Flight;
        Assert.Equal(2, flight.Runs.Count);
        Assert.Equal("A", flight.Runs[0].Mission.Name);
        Assert.Same(flight.Runs[1], flight.Run); // B's rocket is flown
        var (runA, runB) = (flight.Runs[0], flight.Runs[1]);
        Assert.True(runA.Progress.IsDone(0), "A's launch wasn't ticked");

        a.On = true;
        sim.Step(default);
        Assert.True(runA.Progress.IsDone(1), "A's step wasn't ticked while B's vessel was flown");
        Assert.False(runB.Progress.IsDone(1), "B's step was ticked by A's switch");
        Assert.False(runB.Progress.IsDone(0), "B's rocket hasn't launched");
    }

    [Fact]
    public void The_panels_run_follows_the_flown_vessel()
    {
        var (sim, _, _) = TwoRuns();
        using var _ = sim;
        var flight = sim.Flight;
        var (a, b) = (flight.Runs[0], flight.Runs[1]);
        sim.Step(new FlightInput(SwitchTo: a.Vessels[0]));
        Assert.Same(a.Vessels[0], flight.Controlled);
        Assert.Same(a, flight.Run);
        Assert.Equal("A", flight.Mission.Name);
        sim.Step(new FlightInput(SwitchTo: b.Vessels[0]));
        Assert.Same(b, flight.Run);
        Assert.Equal("B", flight.Mission.Name);
    }

    [Fact]
    public void A_new_mission_leaves_another_sites_waiting_rocket()
    {
        var (a, b) = (new Switch(), new Switch());
        using var sim = new Simulation(Test("A", a), site: Highcrest);
        var waiting = sim.Flight.Controlled;
        sim.Step(new FlightInput(Choose: Test("B", b), From: LaunchSites.Home));
        Assert.Contains(waiting, sim.Flight.WorldVessels);
        Assert.Equal(FlightState.Ready, waiting.State);
        Assert.Equal(2, sim.Flight.Runs.Count);
        Assert.Equal(RunEnd.InProgress, sim.Flight.Runs[0].End);
        Assert.Same(waiting, sim.Flight.Runs[0].Vessels[0]);
    }

    [Fact]
    public void Parts_that_come_off_belong_to_their_run()
    {
        // A two-stage rocket of run A climbing from Highcrest, then run B chosen at Home: A's is no
        // longer the newest run when it is staged.
        using var sim = new Simulation(Mission.ReachOrbit, site: Highcrest);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Run((_, _) => default, _ => false, maxSeconds: 2);
        sim.Step(new FlightInput(Choose: Test("B", new Switch()), From: LaunchSites.Home));
        Assert.Equal(2, flight.Runs.Count);
        var (a, b) = (flight.Runs[0], flight.Runs[1]);
        var (rocket, bRocket) = (a.Vessels[0], b.Vessels[0]);

        sim.Step(new FlightInput(SwitchTo: rocket));
        Assert.Same(rocket, flight.Controlled);
        sim.Step(new FlightInput(Stage: true));
        var part = Assert.Single(flight.WorldVessels, v => v != rocket && v != bRocket);
        Assert.Equal([rocket, part], a.Vessels);
        Assert.Equal([bRocket], b.Vessels);
        Assert.Same(a, flight.RunOf(part));
        Assert.Equal(a.Number, part.Flight);
    }

    [Fact]
    public void A_restored_world_keeps_its_runs_and_flown_vessel()
    {
        using var sim = new Simulation(Mission.LandOnTheMoon);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Step(default);
        Assert.True(flight.Runs[0].Progress.IsDone(0), "the launch wasn't ticked");
        var (flown, ids) = (flight.Controlled.Id, flight.WorldVessels.Select(v => v.Id).ToArray());

        flight.Restore(flight.Capture());
        var run = Assert.Single(flight.Runs);
        Assert.Same(Mission.LandOnTheMoon, run.Mission);
        for (var i = 0; i < run.Mission.Steps.Length; i++)
            Assert.Equal(i == 0, run.Progress.IsDone(i));
        Assert.Equal(flown, flight.Controlled.Id);
        Assert.Same(run, flight.Run);
        Assert.Equal(ids, flight.WorldVessels.Select(v => v.Id).ToArray()); // no new rocket
        Assert.False(flight.Chosen);
    }

    [Fact]
    public void A_world_left_before_a_choice_is_saved_as_it_was()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var flight = sim.Flight;
        flight.Restore(new WorldState { Flights = 3, NextVesselId = 7 });
        Assert.False(flight.Chosen);
        Assert.Equal(FlightState.Ready, flight.Controlled.State); // the placeholder behind the menu
        var saved = flight.Capture();
        Assert.Empty(saved.Vessels);
        Assert.Null(saved.Flown);
        Assert.Equal(3, saved.Flights);
        Assert.Equal(7, saved.NextVesselId);

        // A choice made, the placeholder goes, and the new run is the world's next flight.
        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        Assert.True(flight.Chosen);
        var vessel = Assert.Single(flight.WorldVessels);
        Assert.Equal((4, 7), (vessel.Flight, vessel.Id));
        Assert.Equal(4, Assert.Single(flight.Runs).Number);
    }

    [Fact]
    public void Carrying_on_a_run_whose_last_flown_is_gone_flies_another_of_it()
    {
        // Run A's two-stage rocket climbing, staged, its new part flown last; then that part gone
        // from the world as it is saved.
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Run((_, _) => default, _ => false, maxSeconds: 2);
        var rocket = flight.Controlled;
        sim.Step(new FlightInput(Stage: true));
        var part = Assert.Single(flight.WorldVessels, v => v != rocket);
        sim.Step(new FlightInput(SwitchTo: part));
        Assert.Equal(part.Id, flight.Run!.LastFlownId);
        var saved = flight.Capture();
        Assert.Equal(part.Id, saved.Runs.Single().LastFlown);

        flight.Restore(saved with { Vessels = [.. saved.Vessels.Where(v => v.Id != part.Id)] });
        var choice = flight.CarryOnChoices()[0];
        Assert.Equal(rocket.Id, choice.Vessel.Id);
        Assert.Same(flight.Runs[0], choice.Run);
        sim.Step(new FlightInput(CarryOn: rocket.Id));
        Assert.True(flight.Chosen);
        Assert.Equal(rocket.Id, flight.Controlled.Id);
        Assert.Same(choice.Run, flight.Run);
        Assert.Equal(rocket.Id, flight.Run!.LastFlownId);
    }

    [Fact]
    public void A_free_play_vessel_is_carried_on_with_no_run()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Run((_, _) => default, _ => false, maxSeconds: 1);
        var (id, ids) = (flight.Controlled.Id, flight.WorldVessels.Select(v => v.Id).ToArray());

        flight.Restore(flight.Capture());
        Assert.Empty(flight.Runs);
        var choice = Assert.Single(flight.CarryOnChoices());
        Assert.Equal(id, choice.Vessel.Id);
        Assert.Null(choice.Run);
        sim.Step(new FlightInput(CarryOn: id));
        Assert.True(flight.Chosen);
        Assert.Equal(id, flight.Controlled.Id);
        Assert.Null(flight.Run);
        Assert.False(flight.Mission.HasObjectives);
        Assert.Equal(ids, flight.WorldVessels.Select(v => v.Id).ToArray()); // nothing added or removed
    }

    [Fact]
    public void Debris_and_wrecks_are_never_choices()
    {
        // A fairing half lying by the landing zone, and a booster dropped beside it too fast, with
        // the new rocket on the pad.
        var zone = Scene.LandingZoneCentre;
        var half = Drops.Placed(zone + 30f * Planet.East(zone), Planet.East(zone), 0f, Vehicle.FairingHalf, mission: Mission.FreePlay, legsOut: false, clearance: 0.5f, id: 0);
        var falling = Drops.Placed(zone, Planet.East(zone), 40f, Vehicle.Booster, mission: Mission.FreePlay, clearance: 2f, id: 1);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [half, falling], NextVesselId = 2 });
        var flight = sim.Flight;
        var booster = flight.WorldVessels.Single(v => v.Id == 1);
        Assert.True(sim.Run((_, _) => default, _ => booster.State == FlightState.Crashed, maxSeconds: 5), $"the booster should crash: {booster.State}");
        Assert.Contains(booster, flight.WorldVessels);

        var choices = flight.CarryOnChoices();
        Assert.DoesNotContain(choices, c => c.Vessel.Id is 0 or 1);
        Assert.Equal([flight.Controlled], choices.Select(c => c.Vessel)); // the new rocket alone
    }

    [Fact]
    public void A_run_with_no_optional_steps_completes_when_its_steps_are_done()
    {
        var (sim, a, _) = TwoRuns();
        using var _ = sim;
        var flight = sim.Flight;
        var run = flight.Runs[0];
        a.On = true;
        sim.Step(default);
        Assert.Equal(RunEnd.Completed, run.End);
        Assert.Equal(1800, run.Score);
        Assert.DoesNotContain(run, flight.Runs);
        Assert.All(run.Vessels, v => Assert.Null(flight.RunOf(v)));
        Assert.Equal("B", Assert.Single(flight.Capture().Runs).Mission.Name); // B's alone is saved
    }

    [Fact]
    public void A_run_with_an_optional_step_open_waits_for_complete_mission()
    {
        var step = new Switch();
        using var sim = new Simulation(Test("A", step, bonus: true));
        var flight = sim.Flight;
        var run = flight.Run!;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        step.On = true;
        sim.Step(default);
        Assert.Equal(RunEnd.InProgress, run.End);
        Assert.True(run.OptionalOpen);

        sim.Step(new FlightInput(CompleteRun: true));
        Assert.Equal(RunEnd.Completed, run.End);
        Assert.False(run.Progress.IsDone(2));
        Assert.True(run.IsLost(2), "a bonus not done when the mission is completed is lost");
        Assert.Equal(1800, run.Score);
        Assert.Empty(flight.Runs);
    }

    [Fact]
    public void A_failed_run_ends_with_the_lost_step_named()
    {
        var step = new Switch();
        using var sim = new Simulation(Test("A", step));
        var flight = sim.Flight;
        var run = flight.Run!;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        step.Lost = true;
        sim.Step(default);
        Assert.Equal(RunEnd.Failed, run.End);
        Assert.Equal("The switch", run.EndReason);
        Assert.Empty(flight.Runs);
        Assert.Contains(flight.Log, e => e.What == "A (flight 1)" && e.How == WorldLog.FailedBecause("The switch"));
    }

    [Fact]
    public void An_abandoned_run_leaves_its_vessels_in_the_world()
    {
        using var sim = new Simulation(Test("A", new Switch()));
        var flight = sim.Flight;
        var run = flight.Run!;
        var rocket = flight.Controlled;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Step(new FlightInput(AbandonRun: true));
        Assert.Equal(RunEnd.Abandoned, run.End);
        Assert.Contains(rocket, flight.WorldVessels);
        Assert.Null(flight.RunOf(rocket));
        Assert.Empty(flight.Runs);
        Assert.Contains(flight.CarryOnChoices(), c => c.Vessel == rocket && c.Run is null);
        Assert.Contains(flight.Log, e => e.What == "A (flight 1)" && e.How == WorldLog.Abandoned);
        Assert.Null(flight.Notice); // the flown vessel's own run: its panel says so
    }

    [Fact]
    public void A_run_ending_unflown_gives_a_notice()
    {
        var (sim, a, _) = TwoRuns();
        using var _ = sim;
        a.On = true;
        sim.Step(default);
        Assert.StartsWith("A (flight 1)", sim.Flight.Notice?.Text);
        Assert.Contains("1,800", sim.Flight.Notice?.Text);
    }

    [Fact]
    public void The_ended_runs_result_stays_until_a_switch()
    {
        var (sim, a, _) = TwoRuns();
        using var _ = sim;
        var flight = sim.Flight;
        var (runA, runB) = (flight.Runs[0], flight.Runs[1]);
        var rocket = runA.Vessels[0];
        sim.Step(new FlightInput(SwitchTo: rocket));
        a.On = true;
        sim.Step(default);
        Assert.Equal(RunEnd.Completed, runA.End);
        Assert.Same(runA, flight.Run); // the panel and its result stay
        Assert.Null(flight.Notice);
        sim.Step(default);
        Assert.Same(runA, flight.Run);

        sim.Step(new FlightInput(SwitchTo: runB.Vessels[0]));
        Assert.Same(runB, flight.Run);
        sim.Step(new FlightInput(SwitchTo: rocket));
        Assert.Same(rocket, flight.Controlled);
        Assert.Null(flight.Run);
    }

    [Fact]
    public void A_waiting_rocket_cleared_from_the_pad_abandons_its_run()
    {
        using var sim = new Simulation(Test("A", new Switch()));
        var flight = sim.Flight;
        var a = flight.Run!;
        sim.Step(new FlightInput(Choose: Test("B", new Switch()), From: LaunchSites.Home));
        Assert.Equal(RunEnd.Abandoned, a.End);
        Assert.Equal("B", Assert.Single(flight.Runs).Mission.Name);
        Assert.Single(flight.Log, e => e.What == "A (flight 1)" && e.How == WorldLog.Abandoned);
        sim.Step(default);
        Assert.Single(flight.Log, e => e.What == "A (flight 1)" && e.How == WorldLog.Abandoned); // ended once
    }

    [Fact]
    public void A_run_left_with_no_vessels_fails()
    {
        // Run A's booster (flight 1) falling onto the landing zone too fast; once its wreck is
        // cleared away by a new mission, A has nothing left to do its steps with.
        var zone = Scene.LandingZoneCentre;
        var falling = Drops.Placed(zone, Planet.East(zone), 40f, Vehicle.Booster, flight: 1, clearance: 2f, id: 0);
        var mission = Test("A", new Switch());
        var world = new WorldState
        {
            Vessels = [falling],
            NextVesselId = 1,
            Flights = 1,
            Runs = [new RunState { Mission = mission, Site = "Home", Number = 1, Done = [true, false], Points = [0, 0], LastFlown = 0, BegunAt = 0 }],
        };
        using var sim = new Simulation(Mission.FreePlay, world: world, site: Highcrest);
        var flight = sim.Flight;
        var run = Assert.Single(flight.Runs);
        var booster = Assert.Single(run.Vessels);
        Assert.True(sim.Run((_, _) => default, _ => booster.State == FlightState.Crashed, maxSeconds: 5), $"the booster should crash: {booster.State}");
        Assert.Equal(RunEnd.InProgress, run.End); // its wreck is still there

        sim.Step(new FlightInput(Choose: Mission.FreePlay, From: Highcrest));
        Assert.Equal(RunEnd.Failed, run.End);
        Assert.Equal("The switch", run.EndReason);
        Assert.Empty(flight.Runs);
        Assert.Empty(flight.Capture().Runs);
        Assert.Contains(flight.Log, e => e.What == "A (flight 1)" && e.How == WorldLog.FailedBecause("The switch"));
    }

    [Fact]
    public void Carrying_on_any_choice_flies_its_choices_run()
    {
        // Run A launched from Highcrest, then free play chosen at Desert, then run B at Home; then
        // A fails. The choices: B's rocket in B, the free play rocket in none, A's in none.
        var (a, b) = (new Switch(), new Switch());
        using var sim = new Simulation(Test("A", a), site: Highcrest);
        var flight = sim.Flight;
        var runA = flight.Run!;
        var aRocket = flight.Controlled;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Step(new FlightInput(Choose: Mission.FreePlay, From: LaunchSites.All.Single(s => s.Name == "Desert")));
        var free = flight.Controlled;
        sim.Step(new FlightInput(Choose: Test("B", b), From: LaunchSites.Home));
        var runB = flight.Run!;
        a.Lost = true;
        sim.Step(default);
        Assert.Equal(RunEnd.Failed, runA.End);

        var choices = flight.CarryOnChoices();
        Assert.Contains(choices, c => c.Vessel == runB.Vessels[0] && c.Run == runB);
        Assert.Contains(choices, c => c.Vessel == free && c.Run is null);
        Assert.Contains(choices, c => c.Vessel == aRocket && c.Run is null);
        var lastFlown = runA.LastFlownId;
        foreach (var vessel in new[] { aRocket, free, runB.Vessels[0], aRocket })
        {
            var choice = choices.Single(c => c.Vessel == vessel);
            sim.Step(new FlightInput(CarryOn: vessel.Id));
            Assert.Same(vessel, flight.Controlled);
            Assert.Same(choice.Run, flight.Run);
        }
        Assert.Equal(lastFlown, runA.LastFlownId); // an ended run's last flown is left as it ended
    }

    /// <summary>
    /// Mission A launched from Highcrest and flown up clear of its pad's tower, then mission B
    /// chosen at Home (its rocket flown, on the pad).
    /// </summary>
    private static (Simulation Sim, Switch A, Switch B) TwoRunsOneFlying()
    {
        var (a, b) = (new Switch(), new Switch());
        var sim = new Simulation(Test("A", a), site: Highcrest);
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        var rocket = sim.Flight.Controlled;
        Assert.True(sim.Run((_, _) => new FlightInput(Throttle: 1f), _ => Highcrest.ToLocal(rocket.CentreOfMass).Y > Scene.TowerHeight + 50f, maxSeconds: 30), "A's rocket never cleared its tower");
        sim.Step(new FlightInput(Choose: Test("B", b), From: LaunchSites.Home));
        return (sim, a, b);
    }

    [Fact]
    public void Starting_again_leaves_the_old_vessels_and_the_rest_untouched()
    {
        var (sim, _, _) = TwoRunsOneFlying();
        using var _ = sim;
        var flight = sim.Flight;
        var (runA, runB) = (flight.Runs[0], flight.Runs[1]);
        var oldRocket = runA.Vessels[0];
        sim.Step(new FlightInput(SwitchTo: oldRocket));
        var bDone = Enumerable.Range(0, runB.Mission.Steps.Length).Select(runB.Progress.IsDone).ToArray();
        var before = flight.WorldVessels.ToArray();

        sim.Step(new FlightInput(StartAgain: true));
        Assert.Equal(RunEnd.Abandoned, runA.End);
        Assert.Contains(flight.Log, e => e.What == "A (flight 1)" && e.How == WorldLog.StartedAgain);
        Assert.NotSame(oldRocket, flight.Controlled);
        Assert.Equal(FlightState.Ready, flight.Controlled.State);
        Assert.Same(Highcrest, flight.Run!.Site);
        Assert.Equal("A", flight.Run.Mission.Name);
        Assert.Equal(3, flight.Run.Number);
        foreach (var vessel in before)
            Assert.Contains(vessel, flight.WorldVessels);
        Assert.Null(flight.RunOf(oldRocket));
        Assert.Equal(RunEnd.InProgress, runB.End);
        Assert.Equal(bDone, Enumerable.Range(0, runB.Mission.Steps.Length).Select(runB.Progress.IsDone));
        Assert.True(flight.CanRewind);
        Assert.False(flight.RewindResumed);
    }

    [Fact]
    public void Starting_again_after_a_failure_starts_the_vessels_own_mission()
    {
        var (sim, a, _) = TwoRunsOneFlying();
        using var _ = sim;
        var flight = sim.Flight;
        var runA = flight.Runs[0];
        var rocket = runA.Vessels[0];
        a.Lost = true;
        sim.Step(default);
        Assert.Equal(RunEnd.Failed, runA.End);
        sim.Step(new FlightInput(SwitchTo: rocket));
        Assert.Null(flight.Run); // flying a vessel of no run now

        var logged = flight.Log.Count;
        sim.Step(new FlightInput(StartAgain: true));
        Assert.Equal("A", flight.Run!.Mission.Name);
        Assert.Same(Highcrest, flight.Run.Site);
        Assert.Equal(FlightState.Ready, flight.Controlled.State);
        Assert.Contains(rocket, flight.WorldVessels);
        Assert.DoesNotContain(flight.Log.Skip(logged), e => e.How == WorldLog.StartedAgain); // nothing in progress to give up
    }

    /// <summary>Mission A chosen at Highcrest and B at Home, both rockets on their pads; then the world restored as saved.</summary>
    private static (Simulation Sim, Vessel A, Vessel B) TwoRunsRestored()
    {
        var sim = new Simulation(Test("A", new Switch()), site: Highcrest);
        sim.Step(new FlightInput(Choose: Test("B", new Switch()), From: LaunchSites.Home));
        var flight = sim.Flight;
        flight.Restore(flight.Capture());
        Assert.False(flight.CanRewind);
        var (a, b) = (flight.Runs[0].Vessels[0], flight.Runs[1].Vessels[0]);
        return (sim, a, b);
    }

    [Fact]
    public void A_rewind_brings_back_the_whole_world_as_it_was_at_the_choice()
    {
        var (sim, a, b) = TwoRunsRestored();
        using var _ = sim;
        var flight = sim.Flight;
        sim.Step(new FlightInput(CarryOn: a.Id));
        Assert.True(flight.CanRewind);
        Assert.True(flight.RewindResumed);
        var time = flight.WorldTime;
        var places = flight.WorldVessels.Select(v => (v.Id, v.Position)).ToArray();
        var logged = flight.Log.Count;

        // A launched and flown for 30 s, and B's rocket (another run's) launched too.
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Run((_, _) => new FlightInput(Throttle: 1f), _ => false, maxSeconds: 15);
        sim.Step(new FlightInput(SwitchTo: b));
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Run((_, _) => new FlightInput(Throttle: 1f), f => f.WorldTime >= time + 30, maxSeconds: 20);
        Assert.Equal(FlightState.Flying, b.State);
        Assert.Equal(30, flight.RewindSeconds, Simulation.FrameSeconds);

        sim.Step(new FlightInput(Rewind: true));
        Assert.Equal(time, flight.WorldTime, 1e-6);
        Assert.Equal(logged, flight.Log.Count);
        var after = flight.WorldVessels.Select(v => (v.Id, v.Position)).ToArray();
        Assert.Equal(places.Select(p => p.Id), after.Select(p => p.Id));
        for (var i = 0; i < places.Length; i++)
            Assert.True(Vector3.Distance(places[i].Position, after[i].Position) < 0.01f, $"vessel {places[i].Id} isn't back where it was");
        Assert.Equal(a.Id, flight.Controlled.Id);
        Assert.All(flight.WorldVessels, v => Assert.Equal(FlightState.Ready, v.State));
        Assert.Equal(2, flight.Runs.Count);
        Assert.All(flight.Runs, r => Assert.False(r.Progress.IsDone(0), "a launch undone is still ticked"));
        Assert.True(flight.CanRewind); // the same point, to go back to again
        Assert.True(flight.RewindResumed);
        Assert.Equal(0, flight.RewindSeconds, 1e-6);
    }

    [Fact]
    public void A_rewind_goes_back_to_the_menu_choice_not_a_switch()
    {
        var (sim, a, b) = TwoRunsRestored();
        using var _ = sim;
        var flight = sim.Flight;
        sim.Step(new FlightInput(CarryOn: a.Id));
        var time = flight.WorldTime;
        sim.Run((_, _) => default, _ => false, maxSeconds: 2);
        sim.Step(new FlightInput(SwitchTo: b));
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Run((_, _) => new FlightInput(Throttle: 1f), _ => false, maxSeconds: 3);

        sim.Step(new FlightInput(Rewind: true));
        Assert.Equal(time, flight.WorldTime, 1e-6);
        Assert.Equal(a.Id, flight.Controlled.Id);
        Assert.Same(flight.Runs[0], flight.Run);
    }

    [Fact]
    public void There_is_nothing_to_rewind_to_before_a_choice()
    {
        using var sim = new Simulation(Test("A", new Switch()));
        var flight = sim.Flight;
        flight.Restore(flight.Capture());
        Assert.False(flight.CanRewind);
        var time = flight.WorldTime;
        sim.Step(new FlightInput(Rewind: true)); // nothing happens
        Assert.True(flight.WorldTime > time);
        Assert.False(flight.CanRewind);
    }

    /// <summary>A booster of <paramref name="flight"/> standing near the landing zone, <paramref name="k"/> lots of 40 m east of it.</summary>
    private static VesselState Standing(int id, int flight, int k, Vehicle? vehicle = null)
    {
        var zone = Scene.LandingZoneCentre;
        return Drops.Placed(zone + 40f * k * Planet.East(zone), Planet.East(zone), 0f, vehicle ?? Vehicle.Booster, mission: Mission.FreePlay, flight: flight,
            legsOut: vehicle is null, clearance: 0.5f, id: id);
    }

    /// <summary>A run of <paramref name="mission"/> (two steps: its launch done) of flight <paramref name="number"/>, saved.</summary>
    private static RunState SavedRun(Mission mission, int number, int lastFlown) =>
        new() { Mission = mission, Site = "Home", Number = number, Done = [true, false], Points = [0, 0], LastFlown = lastFlown, BegunAt = 0 };

    [Fact]
    public void A_saved_flown_vessel_that_is_debris_or_missing_falls_back_to_the_newest_to_fly()
    {
        var world = new WorldState
        {
            Vessels = [Standing(0, 0, 0), Standing(1, 0, 1), Standing(2, 0, 2, Vehicle.FairingHalf)],
            NextVesselId = 3,
            Flown = 2, // the fairing half: debris, never flown
        };
        using var sim = new Simulation(Mission.FreePlay, world: world);
        var flight = sim.Flight;
        foreach (var saved in (ReadOnlySpan<int?>)[2, 99, null])
        {
            flight.Restore(world with { Flown = saved });
            Assert.Equal(1, flight.Controlled.Id);
            Assert.Equal(saved, flight.SavedFlown);
            // No choice is marked flown last when the world wasn't saved flying it.
            Assert.All(flight.CarryOnChoices(), c => Assert.DoesNotContain("(last flown)", CarryOnRow.Of(c, flight.SavedFlown).Line));
        }
        flight.Restore(world with { Flown = 0 });
        Assert.Equal(0, flight.Controlled.Id);
        var rows = flight.CarryOnChoices().Select(c => CarryOnRow.Of(c, flight.SavedFlown)).ToArray();
        Assert.Contains("(last flown)", rows.Single(r => r.VesselId == 0).Line);
        Assert.DoesNotContain("(last flown)", rows.Single(r => r.VesselId == 1).Line);
    }

    [Fact]
    public void A_runs_points_survive_a_restore()
    {
        var step = new Switch();
        using var sim = new Simulation(Test("A", step, bonus: true));
        var flight = sim.Flight;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        step.On = true;
        sim.Step(default);
        Assert.Equal(1800, flight.Run!.Progress.Points);

        flight.Restore(flight.Capture());
        var run = Assert.Single(flight.Runs);
        Assert.Equal(1800, run.Progress.PointsFor(1));
        Assert.Equal(1800, run.Progress.Points);
        Assert.True(run.Progress.IsDone(1));
    }

    [Fact]
    public void The_placeholders_body_goes_with_it_once_a_choice_is_made()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var flight = sim.Flight;
        flight.Restore(new WorldState());
        var placeholder = flight.Controlled;
        var physics = placeholder.Physics!;
        Assert.Contains(placeholder.Craft, physics.Crafts);
        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        Assert.DoesNotContain(placeholder, flight.WorldVessels);
        Assert.DoesNotContain(placeholder.Craft, physics.Crafts);
    }

    [Fact]
    public void The_choices_are_runs_oldest_first_then_the_last_flown_then_flights_newest_first()
    {
        var (a, b, c, d) = (new Switch(), new Switch(), new Switch(), new Switch { Lost = true });
        var world = new WorldState
        {
            Vessels =
            [
                Standing(0, 1, 0), Standing(1, 2, 1), Standing(2, 3, 2), Standing(3, 4, 3), Standing(4, 5, 4),
                Standing(5, 6, 5, Vehicle.FairingHalf), // run 6's only vessel: nothing in it to fly
                Standing(6, 7, 6), // run 7's, which has failed
            ],
            NextVesselId = 7,
            Flights = 7,
            Flown = 4,
            Runs = [SavedRun(Test("A", a), 1, 0), SavedRun(Test("B", b), 2, 1), SavedRun(Test("C", c), 6, 5), SavedRun(Test("D", d), 7, 6)],
        };
        using var sim = new Simulation(Mission.FreePlay, world: world);
        var flight = sim.Flight;
        flight.Restore(world);
        var choices = flight.CarryOnChoices();
        Assert.Equal([(0, "A"), (1, "B"), (4, null), (6, null), (3, null), (2, null)],
            choices.Select(ch => (ch.Vessel.Id, ch.Run?.Mission.Name)));
    }

    [Fact]
    public void Carrying_on_the_vessel_flown_lets_its_ended_runs_result_go()
    {
        var (sim, a, _) = TwoRuns();
        using var _ = sim;
        var flight = sim.Flight;
        var runA = flight.Runs[0];
        var rocket = runA.Vessels[0];
        sim.Step(new FlightInput(SwitchTo: rocket));
        a.On = true;
        sim.Step(default);
        Assert.Same(runA, flight.Run); // ended, its result shown
        sim.Step(new FlightInput(CarryOn: rocket.Id));
        Assert.Same(rocket, flight.Controlled);
        Assert.Null(flight.Run);
    }

    [Fact]
    public void A_rewind_to_a_mission_begun_comes_back_as_it_was_chosen_each_time()
    {
        using var sim = new Simulation(Test("A", new Switch()), site: Highcrest);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Step(new FlightInput(Choose: Test("B", new Switch()), From: LaunchSites.Home));
        var (time, id, logged) = (flight.WorldTime, flight.Controlled.Id, flight.Log.Count);
        Assert.False(flight.RewindResumed);
        for (var rewind = 0; rewind < 2; rewind++)
        {
            sim.Step(new FlightInput(Launch: true, FullThrottle: true));
            sim.Run((_, _) => new FlightInput(Throttle: 1f), _ => false, maxSeconds: 3);
            sim.Step(new FlightInput(Rewind: true));
            Assert.Equal(time, flight.WorldTime, 1e-6);
            Assert.Equal(logged, flight.Log.Count); // the log as it was, its run begun again
            Assert.Equal((id, FlightState.Ready), (flight.Controlled.Id, flight.Controlled.State));
            Assert.Equal("B", flight.Run!.Mission.Name);
            Assert.True(flight.CanRewind);
        }
    }

    [Fact]
    public void A_world_loaded_over_its_capacity_loses_debris_then_no_runs_vessels_then_runs_never_the_one_flown()
    {
        // Its flown vessel the oldest of run 1's; then the next oldest of run 1, two of no run and a
        // fairing half; then the rest of run 1, enough to be four over.
        var pad = Scene.TestPads[0].Centre;
        var landed = Drops.Placed(pad, Planet.East(pad), 0f, flight: 1) with { State = FlightState.Landed };
        List<VesselState> vessels =
        [
            landed with { Id = 0 }, landed with { Id = 1 }, landed with { Id = 2, Flight = 2 }, landed with { Id = 3, Flight = 2 },
            Standing(4, 0, 0, Vehicle.FairingHalf),
            .. Enumerable.Range(5, Flight.Capacity - 1).Select(i => landed with { Id = i }),
        ];
        var world = new WorldState { Vessels = vessels, NextVesselId = vessels.Count, Flights = 2, Flown = 0, Runs = [SavedRun(Test("A", new Switch()), 1, 0)] };
        using var sim = new Simulation(Mission.FreePlay);
        var flight = sim.Flight;
        flight.Restore(world);
        Assert.Equal(Flight.Capacity, flight.WorldVessels.Count);
        Assert.Equal(4, flight.RemovedForRoom);
        Assert.Equal(0, flight.Controlled.Id);
        Assert.Equal([0, .. Enumerable.Range(5, Flight.Capacity - 1)], flight.WorldVessels.Select(v => v.Id));
        Assert.Equal(["Booster 1", "Booster 2", "Booster 3"], flight.Log.Where(e => e.How == WorldLog.RemovedForRoom).Select(e => e.What).Order()); // (a fairing half isn't logged)
    }

    [Fact]
    public void The_readout_reads_the_runs_and_the_vessel_flown()
    {
        var (sim, a, b) = TwoRunsRestored();
        using var _ = sim;
        var flight = sim.Flight;
        var readings = Readout.FlightReadings(flight);
        Assert.Equal("2", readings["Runs in progress"]);
        Assert.Equal("no", readings["Can rewind"]);
        Assert.Equal(WorldLog.Named(flight.Run!), readings["Run"]);
        Assert.Contains($"Run {WorldLog.Named(flight.Runs[0])}", readings.Keys);

        sim.Step(new FlightInput(CarryOn: a.Id));
        sim.Run((_, _) => default, _ => false, maxSeconds: 2);
        readings = Readout.FlightReadings(flight);
        Assert.Equal($"{a.Id}", readings["Vessel id"]);
        Assert.Equal("A (flight 1)", readings["Run"]);
        Assert.Equal("2 s", readings["Can rewind"]);
        var p = flight.Position;
        Assert.Equal($"{p.X:0.00} {p.Y:0.00} {p.Z:0.00}", readings["Position"]);
    }
}
