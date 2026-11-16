using Xunit;

namespace GameOne.Tests;

/// <summary>
/// A mission step, once done, stays done: whatever did it can stop being true (a booster that
/// landed takes off again), and the step keeps its tick and the points it was given then. A step
/// not yet done can still be done later, and only a step not yet done can be lost.
/// </summary>
public sealed class MissionProgressTests
{
    /// <summary>What the test mission's one step reads: whether it is done, lost, and its points.</summary>
    private sealed class Switches
    {
        public bool Done, Lost;
        public int Points;
    }

    private static (Simulation Sim, Switches Step) Fly()
    {
        var step = new Switches();
        var mission = new Mission
        {
            Name = "TEST",
            Briefing = "",
            Vehicle = Vehicle.Booster,
            Steps = [new("The step", _ => step.Done, Lost: _ => step.Lost, Points: _ => step.Points)],
        };
        return (new Simulation(mission), step);
    }

    [Fact]
    public void A_step_stays_done_when_what_did_it_stops_being_true()
    {
        var (sim, step) = Fly();
        using var _ = sim;
        step.Done = true;
        sim.Step(default);
        step.Done = false;
        sim.Step(default);
        Assert.True(sim.Flight.Run!.Progress.IsDone(0));
        Assert.True(sim.Flight.Run!.Succeeded);
    }

    [Fact]
    public void A_done_step_is_never_lost()
    {
        var (sim, step) = Fly();
        using var _ = sim;
        step.Done = true;
        sim.Step(default);
        (step.Done, step.Lost) = (false, true);
        sim.Step(default);
        Assert.False(sim.Flight.Run!.IsLost(0));
        Assert.False(sim.Flight.Run!.Failed);
    }

    [Fact]
    public void A_step_not_yet_done_can_be_done_later()
    {
        var (sim, step) = Fly();
        using var _ = sim;
        for (var i = 0; i < 60; i++)
            sim.Step(default);
        Assert.False(sim.Flight.Run!.Progress.IsDone(0));
        step.Done = true;
        sim.Step(default);
        Assert.True(sim.Flight.Run!.Progress.IsDone(0));
    }

    [Fact]
    public void A_steps_points_are_kept_from_when_it_was_done()
    {
        var (sim, step) = Fly();
        using var _ = sim;
        (step.Done, step.Points) = (true, 5);
        sim.Step(default);
        step.Points = 9;
        sim.Step(default);
        Assert.Equal(5, sim.Flight.Run!.Progress.Points);
        step.Done = false;
        sim.Step(default);
        step.Done = true;
        sim.Step(default);
        Assert.Equal(5, sim.Flight.Run!.Progress.Points);
        Assert.Equal(5, sim.Flight.Run!.Score);
    }

    [Fact]
    public void Starting_again_starts_the_progress_again()
    {
        var (sim, step) = Fly();
        using var _ = sim;
        step.Done = true;
        sim.Step(default);
        step.Done = false;
        sim.Step(new FlightInput(Restart: true));
        Assert.False(sim.Flight.Run!.Progress.IsDone(0));
    }

    [Fact]
    public void The_missions_points_are_on_their_orbit_landing_and_target_steps()
    {
        // The points a mission gives are each step's own, fixed when it is done: on the orbit,
        // the landings and the landings on target, and nowhere else.
        static string[] Scored(Mission m) => [.. m.Steps.Where(s => s.Points is not null).Select(s => s.Label)];
        Assert.Equal(["Land on the landing zone"], Scored(Mission.LaunchAndLand));
        Assert.Equal(3, Scored(Mission.ReachOrbit).Length);
        Assert.Equal(5, Scored(Mission.DeploySatellite).Length);
        Assert.Empty(Scored(Mission.FreePlay));
    }
}
