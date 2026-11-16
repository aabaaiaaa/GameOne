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
}
