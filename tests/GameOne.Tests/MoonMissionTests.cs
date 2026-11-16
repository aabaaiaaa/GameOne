using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The mission LAND ON THE MOON (<see cref="Mission.LandOnTheMoon"/>): its landing scored by how
/// near the landing spot it comes down (<see cref="Mission.SpotPoints"/>), the lander down on the
/// moon (<see cref="Mission.LanderDown"/>) and its bay open there (<see cref="Mission.BayOpen"/>);
/// losing the lander fails it, losing the booster only its bonus, and the upper stage once the
/// lander is away nothing. The satellite launcher and the missions flying it are as they were.
/// </summary>
public sealed class MoonMissionTests(ITestOutputHelper output)
{
    private static readonly Pilot Climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;
    private static readonly Pilot Nobody = (_, _) => default;
    private static GroundBody Moon => GroundBody.Moon;
    private static Mission LandOnTheMoon => Mission.LandOnTheMoon;

    private static int Step(string label) => Array.FindIndex(LandOnTheMoon.Steps, s => s.Label == label);
    private static readonly string Landing = "Land the lander on the moon", Bay = "Open its bay doors";

    /// <summary>The lander alone landed on the moon at the landing spot, standing on its legs, its bay doors <paramref name="doors"/> open.</summary>
    private static VesselState LandedOnTheMoon(float doors)
    {
        var (position, rotation) = OnTheMoon.Standing(OnTheMoon.LandingSpot, LegGeometry.Lander);
        var vehicle = LanderTests.Alone;
        return new VesselState
        {
            Id = 0, Flight = 1, Mission = Mission.FreePlay, Vehicle = vehicle, First = 0, Last = 0,
            Fuel = [vehicle.Stages[0].FuelMass * 0.5f],
            State = FlightState.Landed, Position = position, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 0, 0),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero,
            Aim = Vector3.Transform(Vector3.UnitY, rotation), LegsWanted = true, LegDeploy = 1f, HasBeenAirborne = true, TouchedDown = true,
            BayDoors = doors, BayDoorsWanted = doors > 0f,
            Body = Moon.Name,
        };
    }

    /// <summary>The vessel of <paramref name="state"/>, loaded into a world of its own at <paramref name="body"/> (nothing stepped).</summary>
    private static (Simulation Sim, Vessel Vessel) Loaded(VesselState state, GroundBody body)
    {
        var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 }, body: body);
        return (sim, sim.Flight.WorldVessels.Single(v => v.Id == 0));
    }

    [Fact]
    public void Landing_on_the_spot_scores_most_and_further_off_less()
    {
        Assert.Equal(1000f, Scene.MoonSpotScoreReach);
        Assert.Equal(3000, Mission.SpotPoints(0f));
        Assert.Equal(1500, Mission.SpotPoints(500f));
        Assert.Equal(0, Mission.SpotPoints(1000f));
        Assert.Equal(0, Mission.SpotPoints(5000f));
        var last = int.MaxValue;
        for (var metres = 0f; metres <= 2000f; metres += 10f)
        {
            var points = Mission.SpotPoints(metres);
            Assert.True(points <= last, $"{points} at {metres} m, up from {last}");
            last = points;
        }
    }

    [Fact]
    public void The_landing_needs_the_lander_down_on_the_moon()
    {
        var (landed, lander) = Loaded(LandedOnTheMoon(0f), Moon);
        using (landed)
        {
            Assert.Same(Moon, lander.Body);
            Assert.True(Mission.LanderDown(lander));
        }

        var flyingState = Drops.Placed(SpotSky.Place, Moon.East(SpotSky.Place), 0f, LanderTests.Alone, clearance: 50f, mission: Mission.FreePlay, body: Moon);
        var (flying, overTheSpot) = Loaded(flyingState, Moon);
        using (flying)
            Assert.False(Mission.LanderDown(overTheSpot));

        var zone = Scene.LandingZoneCentre;
        var onThePlanet = Drops.Placed(zone, Planet.East(zone), 0f, LanderTests.Alone, clearance: 0f, mission: Mission.FreePlay) with { State = FlightState.Landed, TouchedDown = true };
        var (planet, home) = Loaded(onThePlanet, GroundBody.Planet);
        using (planet)
        {
            Assert.Equal(FlightState.Landed, home.State);
            Assert.False(Mission.LanderDown(home));
        }
        Assert.False(Mission.LanderDown(null));
    }

    [Fact]
    public void The_bay_counts_only_fully_open_once_landed()
    {
        foreach (var (doors, open) in (ReadOnlySpan<(float, bool)>)[(1f, true), (0.5f, false), (0f, false)])
        {
            var (sim, lander) = Loaded(LandedOnTheMoon(doors), Moon);
            using (sim)
            {
                Assert.Equal(doors, lander.BayDoors);
                Assert.Equal(open, Mission.BayOpen(lander));
            }
        }

        var flyingState = Drops.Placed(SpotSky.Place, Moon.East(SpotSky.Place), 0f, LanderTests.Alone, clearance: 50f, mission: Mission.FreePlay, body: Moon)
            with { BayDoors = 1f, BayDoorsWanted = true };
        var (flying, overTheSpot) = Loaded(flyingState, Moon);
        using (flying)
        {
            Assert.Equal(1f, overTheSpot.BayDoors);
            Assert.False(Mission.BayOpen(overTheSpot));
        }
        Assert.False(Mission.BayOpen(null));
    }

    /// <summary>
    /// From the pad: climbs, drops the booster, climbs on with the upper stage to 2.5 km, jettisons
    /// the fairing and releases the lander; the upper stage's engine cut, and the lander flown.
    /// </summary>
    private static (Vessel Upper, Vessel Lander) ReleasedAt2500m(Simulation sim)
    {
        var flight = sim.Flight;
        Assert.Same(Vehicle.LanderLauncher, flight.Vessels[0].Vehicle);
        Assert.True(sim.Run(Climb, f => f.Ground > 150f, maxSeconds: 60), "the rocket should climb");
        sim.Step(new FlightInput(Stage: true));
        var upper = flight.Vessels.Single(v => v.Stage == 1);
        sim.Step(new FlightInput(SwitchTo: upper));
        sim.Step(new FlightInput(FullThrottle: true));
        Assert.True(sim.Run(Nobody, f => f.Controlled == upper && upper.Altitude > 2500f, maxSeconds: 120), "the upper stage should climb to 2.5 km");
        Assert.True(upper.FairingOn);
        sim.Step(new FlightInput(Stage: true)); // the fairing
        Assert.False(upper.FairingOn);
        sim.Step(new FlightInput(Stage: true)); // the lander
        var lander = flight.Vessels.Single(v => v is { Stage: 2, TopStage: 2 });
        var left = flight.Vessels.Single(v => v is { Stage: 1, TopStage: 1 });
        sim.Step(new FlightInput(SwitchTo: left));
        sim.Step(new FlightInput(CutThrottle: true));
        sim.Step(new FlightInput(SwitchTo: lander));
        Assert.False(flight.Run!.Failed);
        return (left, lander);
    }

    [Fact]
    public void Losing_the_lander_fails_land_on_the_moon()
    {
        // Released, its engine cut: it falls back to the planet and crashes.
        using var sim = new Simulation(LandOnTheMoon);
        var flight = sim.Flight;
        var (_, lander) = ReleasedAt2500m(sim);
        sim.Step(new FlightInput(CutThrottle: true));
        Assert.True(sim.Run(Nobody, _ => lander.State == FlightState.Crashed, maxSeconds: 300), $"the lander, its engine cut, should fall and crash: {lander.State}");
        output.WriteLine($"the lander: {lander.CrashReason} at {sim.Time:0} s");

        Assert.True(flight.Run!.Failed);
        Assert.Null(flight.Run!.Score);
        Assert.True(flight.Run!.IsLost(Step(Landing)));
        Assert.True(flight.Run!.IsLost(Step(Bay)));
    }

    [Fact]
    public void Losing_the_booster_only_loses_land_on_the_moons_bonus()
    {
        using var sim = new Simulation(LandOnTheMoon);
        var flight = sim.Flight;
        Assert.True(sim.Run(Climb, f => f.Ground > 150f, maxSeconds: 60), "the rocket should climb");
        sim.Step(new FlightInput(Stage: true));
        var booster = flight.Vessels.Single(v => v.Stage == 0);
        sim.Step(new FlightInput(FullThrottle: true)); // the upper stage climbs on
        Assert.True(sim.Run(Nobody, _ => booster.State == FlightState.Crashed, maxSeconds: 60), "the dropped booster should fall and crash");

        Assert.NotEqual(FlightState.Crashed, flight.Vessels.Single(v => v.Stage == 1).State);
        Assert.False(flight.Run!.Failed);
        Assert.False(flight.Run!.IsLost(Step(Landing)));
        Assert.False(flight.Run!.IsLost(Step(Bay)));
    }

    [Fact]
    public void Losing_the_upper_stage_once_the_lander_is_away_costs_nothing()
    {
        // Released, the lander burns on (its engine slows its fall) while the upper stage, its
        // engine cut, falls back first and crashes.
        using var sim = new Simulation(LandOnTheMoon);
        var flight = sim.Flight;
        var (upper, lander) = ReleasedAt2500m(sim);
        sim.Step(new FlightInput(FullThrottle: true));
        Assert.True(sim.Run(Nobody, _ => upper.State == FlightState.Crashed || lander.State == FlightState.Crashed, maxSeconds: 300), $"the upper stage should fall and crash: {upper.State}");
        output.WriteLine($"the upper stage: {upper.State} {upper.CrashReason} at {sim.Time:0} s; the lander {lander.State}, {lander.Altitude:0} m up");

        Assert.Equal(FlightState.Crashed, upper.State);
        Assert.NotEqual(FlightState.Crashed, lander.State);
        Assert.False(flight.Run!.Failed);
        Assert.False(flight.Run!.IsLost(Step(Landing)));
        Assert.False(flight.Run!.IsLost(Step(Bay)));
    }

    [Fact]
    public void The_satellite_launcher_and_its_missions_are_unchanged()
    {
        Assert.Same(Vehicle.ThreeStage, Mission.DeploySatellite.Vehicle);
        Assert.Same(Vehicle.ThreeStage, Mission.FreePlay.Vehicle);
        Assert.Empty(Mission.DeploySatellite.PointsOfInterest);
        Assert.Empty(Mission.FreePlay.PointsOfInterest);

        var stages = Vehicle.ThreeStage.Stages;
        Assert.Equal(3, stages.Length);
        Assert.Same(Vehicle.TwoStage.Stages[0], stages[0]);
        Assert.Equal(StageKind.Upper, stages[1].Kind);
        Assert.Equal(0.5f, stages[1].LandingReserve);
        Assert.True(stages[1] is { Guided: true, Legs: true, Fairing: false, Bay: false });
        Assert.Same(LegGeometry.Rocket, stages[1].LegGeometry);
        Assert.Equal(StageKind.Satellite, stages[2].Kind);
        Assert.True(stages[2] is { Guided: true, Fairing: true, Legs: false, Bay: false });
        Assert.Same(FairingGeometry.Satellite, stages[2].FairingGeometry);
        Assert.Same(Vehicle.FairingHalf, Vehicle.HalfOf(FairingGeometry.Satellite));
        Assert.Same(FairingGeometry.Satellite, Vehicle.FairingHalf.Stages.Single().FairingGeometry);
        Assert.Equal(FairingGeometry.Satellite.Mass / 2f, Vehicle.FairingHalf.Stages.Single().DryMass);

        // The new mission flies the lander launcher, listed before free play, with its spot as its one point of interest.
        Assert.Same(Vehicle.LanderLauncher, LandOnTheMoon.Vehicle);
        Assert.Equal(Array.IndexOf(Mission.All, Mission.FreePlay) - 1, Array.IndexOf(Mission.All, LandOnTheMoon));
        Assert.Equal([PointOfInterest.MoonLandingSpot], LandOnTheMoon.PointsOfInterest);
        Assert.Contains($"({Scene.MoonLandingSpot.Latitude:0}° N on its near side)", LandOnTheMoon.Briefing);
    }
}
