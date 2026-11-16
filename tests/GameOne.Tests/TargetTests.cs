using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// Targets (picked by clicking a marker, so set through <see cref="FlightInput"/>): each vessel
/// keeps its own, which comes back when it is flown again; a vessel can't be its own; one that is
/// destroyed is let go. And the player's settings, which say which markers show, saved beside the
/// worlds.
/// </summary>
public sealed class TargetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static readonly NavTarget Pad = new(TargetKind.Site, 0), DroneShip = new(TargetKind.Site, 2);

    [Fact]
    public void A_target_is_set_and_let_go_and_a_vessel_cannot_be_its_own()
    {
        using var sim = new Simulation();
        var flight = sim.Flight;
        Assert.Null(flight.Target);

        sim.Step(new FlightInput(SetTarget: DroneShip));
        Assert.Equal(DroneShip, flight.Target);
        // The drone ship rides the waves: its deck is near its calm-sea top, not on it.
        Assert.True(Vector3.Distance(Sites.Top(2), flight.Locate(DroneShip)!.Value.Position) < 3f);

        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(flight.Controlled)));
        Assert.Equal(DroneShip, flight.Target); // not itself: the old one stays
        sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, 99)));
        Assert.Equal(DroneShip, flight.Target); // nowhere

        sim.Step(new FlightInput(ClearTarget: true));
        Assert.Null(flight.Target);
    }

    [Fact]
    public void Each_vessel_keeps_its_own_target_and_a_destroyed_one_is_let_go()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        var pilot = new OrbitAutopilot();
        // Up through separation: the booster is dropped, and falls back.
        Assert.True(sim.Run(pilot.Fly, f => f.Vessels.Count > 1, maxSeconds: 120));
        var upper = flight.Controlled;
        var booster = flight.Vessels.Single(v => v != upper);
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(booster)));
        Assert.Equal(NavTarget.OfVessel(booster), flight.Target);
        Assert.Equal(booster.CentreOfMass, flight.Locate(flight.Target!.Value)!.Value.Position);

        // Flying the booster, it has a target of its own; the upper stage's is still its.
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(booster, flight.Controlled);
        Assert.Null(flight.Target);
        sim.Step(new FlightInput(SetTarget: Pad));
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(upper, flight.Controlled);
        Assert.Equal(NavTarget.OfVessel(booster), flight.Target);
        Assert.Equal(Pad, flight.TargetOf(booster));

        // Left to fall, the booster is destroyed: the upper stage's target goes with it.
        Assert.True(sim.Run(pilot.Fly, _ => booster.State == FlightState.Crashed, maxSeconds: 200), "the booster never came down");
        Assert.Null(flight.Target);
        Assert.Null(upper.Target);
    }

    [Fact]
    public void Settings_are_saved_beside_the_worlds_and_not_listed_as_one()
    {
        var store = new WorldStore(_directory);
        Assert.Equal(new Settings(), store.LoadSettings()); // none saved: the defaults, every marker on
        Assert.True(new Settings().Shows(MarkerKind.Debris));
        Assert.False(new Settings().ShowTimings); // off by default
        Assert.False(new Settings().CapFrameRate); // uncapped by default: how high it goes shows
        Assert.False(new Settings().SlowNearMisses); // off by default: some playthroughs want it off

        var changed = new Settings
        {
            DebrisMarkers = false, EarlierFlightMarkers = false, LaunchSite = "Polar", ShowTimings = true, CapFrameRate = true, SlowNearMisses = true,
        };
        store.SaveSettings(changed);
        Assert.Equal(changed, new WorldStore(_directory).LoadSettings());
        Assert.False(changed.Shows(MarkerKind.Debris));
        Assert.True(changed.Shows(MarkerKind.ThisFlight));
        Assert.False((changed with { Markers = false }).Shows(MarkerKind.ThisFlight));
        Assert.Empty(store.List());

        File.WriteAllText(Path.Combine(_directory, "player.settings"), "{ not settings");
        Assert.Equal(new Settings(), store.LoadSettings()); // unreadable: the defaults again
    }
}
