using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>The drone ship's motion (<see cref="Rafts.Ship"/>): moored, riding the sea's waves, and its deck measured along its normal.</summary>
public class DroneShipTests
{
    [Fact]
    public void It_holds_its_position_and_rides_the_waves()
    {
        // Over two minutes: never drifting, heaving and tilting a little, as the Sunrise Sea's 1 m waves have it.
        float heave = 0f, tilt = 0f;
        for (var t = 0.0; t <= 120.0; t += 0.25)
        {
            var pose = Rafts.Ship.Pose(t);
            var off = pose.Position - Scene.DroneShipCentre;
            var up = Planet.Up(Scene.DroneShipCentre);
            Assert.True((off - up * Vector3.Dot(off, up)).Length() < 0.01f, $"drifted at {t} s");
            heave = MathF.Max(heave, MathF.Abs(Vector3.Dot(off, up)));
            var deckUp = Vector3.Transform(Vector3.UnitY, pose.Rotation);
            tilt = MathF.Max(tilt, float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(deckUp, up), -1f, 1f))));
        }
        Assert.InRange(heave, 0.05f, 0.5f);
        Assert.InRange(tilt, 0.1f, 3f);
    }

    [Fact]
    public void Its_length_runs_east() =>
        Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitX, Rafts.Ship.Pose(0).Rotation), Planet.East(Scene.DroneShipCentre)) > 0.99f);

    [Fact]
    public void It_moves_smoothly_however_long_the_world_has_run()
    {
        // A 1/120 s step apart, the deck moves no more than its velocity allows, at the start and 10^6 s on.
        foreach (var t0 in (double[])[0.0, 1e6])
        {
            Rafts.Ship.Set(t0);
            var (a, b) = (Rafts.Ship.Pose(t0), Rafts.Ship.Pose(t0 + 1.0 / 120.0));
            Assert.True(Vector3.Distance(a.Position, b.Position) < 0.01f);
            Assert.True(Rafts.Ship.Velocity.Length() < 1f);
        }
    }

    [Fact]
    public void A_point_on_the_deck_moves_as_its_velocity_says()
    {
        // A deck corner, furthest from the centre, so its turning counts most: its velocity (the
        // ship's, and its turning there) is how fast the corner itself moves, between the poses
        // just either side.
        const double h = 0.02;
        var corner = new Vector3(Scene.DroneShipHalfLength, Scene.DroneShipFreeboard, Scene.DroneShipHalfBeam);
        Vector3 At(DeckPose pose) => pose.Position + Vector3.Transform(corner, pose.Rotation);
        foreach (var t in (double[])[3.0, 17.5, 40.0, 81.25])
        {
            Rafts.Ship.Set(t);
            var moved = (At(Rafts.Ship.Pose(t + h)) - At(Rafts.Ship.Pose(t - h))) / (float)(2.0 * h);
            var said = Rafts.Ship.VelocityAt(At(Rafts.Ship.Current));
            Assert.True(Vector3.Distance(said, moved) < 0.005f, $"at {t} s: said {said}, moved {moved}");
            // And the turning is in it: the corner doesn't just move as the centre does.
            Assert.True(Vector3.Distance(Rafts.Ship.Velocity, moved) > 0.01f, $"at {t} s the corner moved as the centre did");
        }
    }

    [Fact]
    public void The_deck_is_measured_along_its_normal()
    {
        Rafts.Ship.Set(17.0);
        var top = Rafts.Ship.Current.Position + Rafts.Ship.Normal * Scene.DroneShipFreeboard;
        Assert.Equal(2f, Rafts.Ship.HeightAbove(top + Rafts.Ship.Normal * 2f)!.Value, 0.01f);
        Assert.True(Rafts.Ship.HeightAbove(top - Rafts.Ship.Normal * 1f) < 0f);                  // inside the hull
        var beside = top + Vector3.Transform(Vector3.UnitZ, Rafts.Ship.Current.Rotation) * (Scene.DroneShipHalfBeam + 1f);
        Assert.Null(Rafts.Ship.HeightAbove(beside));                                              // over the sea beside it
        Assert.Null(Rafts.Ship.HeightAbove(top - Rafts.Ship.Normal * (Scene.DroneShipFreeboard + Scene.DroneShipDraft + 1f))); // under its keel
    }

    /// <summary>Home's second drone ship, the moon trip's booster's (<see cref="Scene.TripDroneShipCentre"/>).</summary>
    private static Raft TripShip => Rafts.Ships.Single(s => Vector3.Distance(s.Centre, Scene.TripDroneShipCentre) < 1f);

    /// <summary>The sea's depth (m) under a place, on a calm sea.</summary>
    private static float Depth(Vector3 p) => -Terrain.Height(Planet.Up(p));

    [Fact]
    public void Home_has_a_second_drone_ship_out_on_the_trips_heading()
    {
        var home = LaunchSites.Home;
        Assert.Equal(2, home.DroneShips.Count);
        Assert.Equal(Scene.DroneShipCentre, home.DroneShips[0]);
        Assert.Equal(Scene.TripDroneShipCentre, home.DroneShips[1]);
        // Deep enough for its keel in the roughest sea's troughs, as every drone ship is.
        Assert.True(Scene.DroneShipMinDepth >= Scene.DroneShipDraft + Sea.Highest, $"{Scene.DroneShipMinDepth} m against a {Scene.DroneShipDraft} m draught and {Sea.Highest:0.00} m troughs");
        foreach (var ship in Rafts.Ships)
            Assert.True(Depth(ship.Centre) >= Scene.DroneShipMinDepth, $"a drone ship at {ship.Centre} is in {Depth(ship.Centre):0.0} m of sea");
        // Where the lander launcher's booster came down on the climb into the trip's plane.
        var off = Vector3.Distance(Scene.TripDroneShipCentre, LanderTripTests.BoosterCameDown);
        Assert.True(off < LanderTripTests.BoosterReach, $"{off:0} m from where the booster came down"); // within its reach of it
        Assert.True(Terrain.IsSea(Planet.Up(Scene.TripDroneShipCentre)));
        Assert.Same(TripShip, Rafts.Ships[1]); // site by site: Home's two first
    }

    [Fact]
    public void The_second_drone_ship_floats_lit_and_landable_as_the_first()
    {
        var ship = TripShip;
        Assert.True(ship.IsShip);

        // It rides the waves where it is moored.
        float heave = 0f;
        for (var t = 0.0; t <= 60.0; t += 0.25)
        {
            var off = ship.Pose(t).Position - ship.Centre;
            var up = Planet.Up(ship.Centre);
            Assert.True((off - up * Vector3.Dot(off, up)).Length() < 0.01f, $"drifted at {t} s");
            heave = MathF.Max(heave, MathF.Abs(Vector3.Dot(off, up)));
        }
        Assert.InRange(heave, 0.05f, 0.5f);

        // A booster dropped onto its deck, with it as the landing site, lands on it.
        ship.Set(0.0);
        var at = ship.Current.Position + ship.Normal * Scene.DroneShipFreeboard;
        Assert.Same(ship, Rafts.Under(at + ship.Normal)?.Raft);
        var mission = new Mission { Name = "TEST", Briefing = "", Vehicle = Vehicle.TwoStage, Steps = [], LandingSites = [Scene.TripDroneShipCentre] };
        using (var sim = Drops.At(at, Planet.East(at), 2f, Vehicle.TwoStage, first: 0, last: 0, mission: mission, flight: 1))
        {
            var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
            Assert.True(sim.Run((_, _) => default, _ => booster.State != FlightState.Flying, maxSeconds: 60));
            Assert.Equal(FlightState.Landed, booster.State);
            Assert.True(booster.LandedOnTarget, $"{booster.DistanceToLandingSite:0.0} m off");
            Assert.InRange(ship.HeightAbove(booster.Position)!.Value, -0.5f, 3f); // on its deck
        }

        // Its own lights and masts, on in its own dark and off in its day; and a marker of its own.
        var place = Enumerable.Range(0, PadLights.All.Count).Single(i => PadLights.All[i].Ship == ship);
        Assert.Equal(4, PadLights.All[place].Masts.Length);
        var lights = new PlaceLights();
        lights.Reset(Sun.WorldTimeFor(ship.Centre, Scene.DaySeconds / 2));
        Assert.Equal(0f, lights.Lit[place]);
        lights.Reset(Sun.WorldTimeFor(ship.Centre, 0));
        Assert.Equal(1f, lights.Lit[place]);
        var marker = Array.FindIndex(Sites.All, s => s.Centre == Scene.TripDroneShipCentre);
        Assert.Equal("Drone ship 2", Sites.All[marker].Name);
        Assert.Same(ship, Sites.RaftOf(marker));
        // Every ship's lit place is named as its marker is.
        Assert.Equal("Drone ship 2", PadLights.All[place].Name);
        foreach (var lit in PadLights.All.Where(p => p.Ship is not null))
            Assert.Equal(Sites.All.Single(s => s.Centre == lit.Ship!.Centre).Name, lit.Name);
    }

    [Fact]
    public void The_other_missions_keep_their_drone_ship()
    {
        Assert.Equal([Scene.DroneShipCentre], Mission.ReachOrbit.LandingSites);
        Assert.Equal([Scene.DroneShipCentre, Scene.LandingZoneCentre], Mission.DeploySatellite.LandingSites);
        Assert.Equal([Scene.TripDroneShipCentre], Mission.LandOnTheMoon.LandingSites);
        var sentinel = LaunchSites.Named("Sentinel");
        Assert.Single(sentinel.DroneShips);
        Assert.Equal(1, Rafts.Ships.Count(s => Vector3.Distance(s.Centre, sentinel.DroneShips[0]) < 1f));
        Assert.All(LaunchSites.All.Where(s => s != LaunchSites.Home && s != sentinel), s => Assert.Empty(s.DroneShips));
    }

    [Fact]
    public void The_drawn_ship_is_the_collided_ship()
    {
        // The mesh, in the ship's own frame, fills the hull box the physics moves (x along it, z
        // across, from the keel to the deck) and no more, and its deck is where boosters stand.
        var vertices = GameOne.Rendering.Meshes.BuildDroneShip();
        Assert.NotEmpty(vertices);
        var deck = 0;
        foreach (var v in vertices)
        {
            Assert.InRange(MathF.Abs(v.Position.X), 0f, Scene.DroneShipHalfLength);
            Assert.InRange(MathF.Abs(v.Position.Z), 0f, Scene.DroneShipHalfBeam);
            Assert.InRange(v.Position.Y, -Scene.DroneShipDraft, Scene.DroneShipFreeboard);
            if (v.Normal.Y > 0.99f)
            {
                Assert.Equal(Scene.DroneShipFreeboard, v.Position.Y);
                deck++;
            }
        }
        Assert.True(deck >= 6, "no deck top"); // at least one quad facing up
    }
}
