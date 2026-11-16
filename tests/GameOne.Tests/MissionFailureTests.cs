using Xunit;

namespace GameOne.Tests;

/// <summary>
/// A mission fails when something it needs is destroyed: a step that can't be done any more
/// (the upper stage to land, once it has crashed) fails it, and it scores nothing. A bonus that
/// can't be done any more doesn't.
/// </summary>
public sealed class MissionFailureTests
{
    private static readonly Pilot Climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;
    private static readonly Pilot Coast = (_, _) => default;

    private static bool Crashed(Vessel? vessel) => vessel is { State: FlightState.Crashed };

    /// <summary>Climbs a little, then drops the booster: it falls with its engine shut down.</summary>
    private static Vessel DropTheBoosterLow(Simulation sim)
    {
        Assert.True(sim.Run(Climb, f => f.Ground > 150f, maxSeconds: 60), "the rocket should climb");
        sim.Step(new FlightInput(Stage: true));
        return sim.Flight.Vessels.Single(v => v.Stage == 0);
    }

    [Fact]
    public void Losing_the_booster_fails_launch_and_land()
    {
        using var sim = new Simulation(Mission.LaunchAndLand);
        var flight = sim.Flight;
        Assert.True(sim.Run(Climb, f => f.Ground > 150f, maxSeconds: 60));
        Assert.False(flight.Mission.Failed(flight));
        sim.Step(new FlightInput(CutThrottle: true));
        Assert.True(sim.Run(Coast, f => Crashed(f.Vessels[0]), maxSeconds: 60), "with its engine cut it should fall and crash");

        Assert.True(flight.Mission.Failed(flight));
        Assert.Null(flight.Mission.Score(flight));
        Assert.True(Mission.IsLost(Array.FindIndex(flight.Mission.Steps, s => s.Label == "Land on the landing zone"), flight));
    }

    [Fact]
    public void Losing_the_upper_stage_fails_deploy_a_satellite()
    {
        using var sim = new Simulation(Mission.DeploySatellite);
        var flight = sim.Flight;
        DropTheBoosterLow(sim);
        var upper = flight.Vessels.Single(v => v.Stage == 1);
        sim.Step(new FlightInput(SwitchTo: upper));
        sim.Step(new FlightInput(CutThrottle: true));
        Assert.False(flight.Mission.Failed(flight));
        Assert.True(sim.Run(Coast, f => Crashed(upper), maxSeconds: 60), "the upper stage, its engine cut, should fall and crash");

        Assert.True(flight.Mission.Failed(flight));
        Assert.Null(flight.Mission.Score(flight));
        Assert.True(Mission.IsLost(Array.FindIndex(flight.Mission.Steps, s => s.Label == "Land the upper stage"), flight));
        Assert.True(Mission.IsLost(Array.FindIndex(flight.Mission.Steps, s => s.Label == "Release the satellite"), flight)); // it went down with it
    }

    [Fact]
    public void Losing_the_booster_only_loses_reach_orbit_s_bonus()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        var booster = DropTheBoosterLow(sim);
        sim.Step(new FlightInput(FullThrottle: true)); // the upper stage climbs on
        Assert.True(sim.Run(Coast, _ => Crashed(booster), maxSeconds: 60), "the dropped booster should fall and crash");

        Assert.NotEqual(FlightState.Crashed, flight.Vessels.Single(v => v.Stage == 1).State);
        Assert.False(flight.Mission.Failed(flight));
    }
}
