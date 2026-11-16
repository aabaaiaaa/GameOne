using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Free play: the satellite launcher with no objectives. Whatever is flown, nothing is won, lost
/// or scored, and nothing is the mission's landing site; what happens to the vessels (a landing)
/// still happens.
/// </summary>
public sealed class FreePlayTests(ITestOutputHelper output)
{
    private static readonly Pilot Climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;

    private static void NothingWonLostOrScored(Flight flight)
    {
        Assert.Null(flight.Run); // its vessels belong to no run: nothing to win, lose or score
        Assert.All(flight.WorldVessels, v => Assert.Null(v.LandingSite));
    }

    [Fact]
    public void Free_play_flies_the_satellite_launcher_with_no_objectives()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var flight = sim.Flight;
        Assert.Same(Vehicle.ThreeStage, flight.Mission.Vehicle);
        Assert.Same(Vehicle.ThreeStage, flight.Controlled.Vehicle);
        Assert.Empty(flight.Mission.Steps);
        Assert.False(flight.Mission.HasObjectives);
        NothingWonLostOrScored(flight); // on the pad: not won before it starts

        Assert.True(sim.Run(Climb, f => f.Ground > Scene.TargetAltitude, maxSeconds: 60), "never climbed past 400 m");
        NothingWonLostOrScored(flight);
        sim.Step(new FlightInput(Stage: true));
        sim.Run(Climb, _ => false, maxSeconds: 3); // on up, on the upper stage
        output.WriteLine($"{flight.Vessels.Count} vessels, flying the {flight.Controlled.Name} at {flight.Ground:0} m");

        Assert.Equal(2, flight.Vessels.Count);
        Assert.Equal(1, flight.Stage);
        Assert.Equal(FlightState.Flying, flight.State);
        NothingWonLostOrScored(flight);
    }

    [PlaythroughFact(Long = false)]
    [Trait("Kind", "Playthrough")]
    public void A_booster_landed_in_free_play_is_landed_but_nothing_is_won()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var flight = sim.Flight;
        Assert.True(sim.Run(Climb, f => f.Ground > 600f, maxSeconds: 60), "never climbed to 600 m");
        sim.Step(new FlightInput(Stage: true));
        var booster = flight.Vessels.Single(v => v is { Stage: 0, TopStage: 0 });

        // Over to the booster, still rising: brought down towards the landing zone by the pad (which is no target of the mission's).
        sim.Step(new FlightInput(SwitchTo: booster));
        Assert.Same(booster, flight.Controlled);
        var lander = Autopilot.ReturningBooster(Scene.LandingZoneCentre);
        Assert.True(sim.Run(lander.Fly, _ => booster.State != FlightState.Flying, maxSeconds: 180), "the booster never came down");
        output.WriteLine($"booster {booster.State}: {booster.TouchdownSpeed:0.0} m/s, {booster.Fuel * 100:0}% fuel left");

        Assert.Equal(FlightState.Landed, booster.State);
        Assert.False(booster.LandedOnTarget, "nothing is the target in free play");
        NothingWonLostOrScored(flight);
    }
}
