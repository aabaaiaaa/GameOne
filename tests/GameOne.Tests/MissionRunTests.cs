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
        public bool On;
    }

    /// <summary>A booster mission whose steps are its launch and a step done when <paramref name="step"/> is on.</summary>
    private static Mission Test(string name, Switch step) => new()
    {
        Name = name,
        Briefing = "",
        Vehicle = Vehicle.Booster,
        Steps =
        [
            new("Launch", r => r.Vessels[0].State != FlightState.Ready),
            new("The switch", _ => step.On),
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
        Assert.True(flight.Runs[0].Progress.IsDone(0), "A's launch wasn't ticked");

        a.On = true;
        sim.Step(default);
        Assert.True(flight.Runs[0].Progress.IsDone(1), "A's step wasn't ticked while B's vessel was flown");
        Assert.False(flight.Runs[1].Progress.IsDone(1), "B's step was ticked by A's switch");
        Assert.False(flight.Runs[1].Progress.IsDone(0), "B's rocket hasn't launched");
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
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Run((_, _) => default, _ => false, maxSeconds: 2);
        sim.Step(new FlightInput(Stage: true));
        var run = Assert.Single(flight.Runs);
        Assert.Equal(2, run.Vessels.Count);
        Assert.All(flight.WorldVessels, v => Assert.Contains(v, run.Vessels));
        Assert.Same(run, flight.RunOf(run.Vessels[1]));
        Assert.Equal(run.Number, run.Vessels[1].Flight);
    }
}
