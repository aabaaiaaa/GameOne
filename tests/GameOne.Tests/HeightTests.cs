using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// Heights are measured from the sea (<see cref="Planet.Altitude"/>): the air, orbits and every
/// height shown, the terrain's and the sea's (at 0) among them. Home's pad is 35 m above it.
/// </summary>
public sealed class HeightTests
{
    private static Vector3 AboveTheSea(float height) => Planet.Centre + Vector3.UnitY * (Planet.SeaRadius + height);

    [Fact]
    public void Home_pad_is_35_m_above_the_sea()
    {
        Assert.Equal(35f, Planet.Altitude(Vector3.Zero), 0.01f);
        Assert.Equal(35f, Terrain.SurfaceHeight(Planet.Up(Vector3.Zero)), 0.01f);
        Assert.Equal(35f, LaunchSites.Home.Elevation);
        Assert.Equal(35f, Scene.HomeElevation);
    }

    [Fact]
    public void The_open_sea_is_at_0()
    {
        var dir = WaterTests.DeepWater.Value;
        Assert.Equal(0f, Terrain.SurfaceHeight(dir), 0.01f);
        Assert.Equal(0f, Planet.Altitude(Planet.Centre + dir * Planet.SeaRadius), 0.01f);
    }

    /// <summary>
    /// Where each site's pad, tower foot, landing zone and drone ship are. Home's as they were before
    /// heights were from the sea (nothing of Home's moves); the far sites' as their pads and zones
    /// stand at the highest ground under them (pad lights, part 3), recorded when that was made.
    /// </summary>
    private static readonly Dictionary<string, (Vector3 Pad, Vector3 Tower, Vector3? Zone, Vector3? Ship)> WhereTheyWere = new()
    {
        ["Home"] = (new(0f, 0f, 0f), new(-12f, 0f, -2f), new(-220f, -5.0664062f, -230f), new(2381.6348f, -323.79102f, 0f)),
        ["Polar"] = (new(213.17311f, -6514.651f, 9593.816f), new(201.31023f, -6512.0425f, 9593.133f), new(2.3290956f, -6308.188f, 9442.836f), null),
        ["Highcrest"] = (new(10627.889f, -7152.2666f, -2138.7278f), new(10624.414f, -7140.7744f, -2140.691f), new(10335.408f, -6979.6143f, -2350.7334f), null),
        ["Desert"] = (new(-4612.3735f, -1679.0576f, -3275.8584f), new(-4622.553f, -1685.4448f, -3277.7495f), new(-4761.9243f, -1865.3628f, -3489.21f), null),
        ["Coral Isle"] = (new(2007.9147f, -19446.5f, 2587.73f), new(2019.76f, -19444.512f, 2585.798f), null, null),
        ["Northreach"] = (new(-741.18945f, -14203.491f, -9153.479f), new(-729.057f, -14203.79f, -9154.324f), null, null),
        ["Sentinel"] = (new(-7115.4697f, -16185.383f, -3431.5444f), new(-7107.0806f, -16193.99f, -3433.4238f), null, new(-8424.813f, -14167.88f, -3309.4595f)),
    };

    [Fact]
    public void Nothing_moved()
    {
        // Heights are numbered from the sea now, but nothing in the world is anywhere else.
        foreach (var site in LaunchSites.All)
        {
            var (pad, tower, zone, ship) = WhereTheyWere[site.Name];
            Assert.True(Vector3.Distance(pad, site.Pad) < 0.01f, $"{site.Name}'s pad moved to {site.Pad}");
            Assert.True(Vector3.Distance(tower, site.TowerFoot) < 0.01f, $"{site.Name}'s tower moved to {site.TowerFoot}");
            Assert.Equal(zone.HasValue, site.LandingZone.HasValue);
            if (zone is { } z) Assert.True(Vector3.Distance(z, site.LandingZone!.Value) < 0.01f, $"{site.Name}'s zone moved to {site.LandingZone}");
            if (ship is { } s) Assert.True(Vector3.Distance(s, site.DroneShips[0]) < 0.01f, $"{site.Name}'s first ship moved to {site.DroneShips[0]}");
        }
    }

    [Fact]
    public void An_old_map_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"old-map-{Guid.NewGuid():N}.bin.gz");
        try
        {
            using (var gzip = new System.IO.Compression.GZipStream(File.Create(path), System.IO.Compression.CompressionLevel.Fastest))
            using (var w = new BinaryWriter(gzip))
            {
                w.Write("GOTR"u8.ToArray());
                w.Write(2); // the version with heights above the sphere
            }
            var e = Assert.Throws<InvalidDataException>(() => TerrainMap.Load(path));
            Assert.Contains("regenerate", e.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_air_ends_3000_m_above_the_sea()
    {
        Assert.True(Planet.AirDensity(Planet.Altitude(AboveTheSea(2990f))) > 0f);
        Assert.Equal(0f, Planet.AirDensity(Planet.Altitude(AboveTheSea(3001f))));
    }

    [Fact]
    public void An_orbit_is_measured_from_the_sea()
    {
        var (apoapsis, periapsis) = Planet.Orbit(AboveTheSea(4000f), Vector3.UnitX * Planet.CircularSpeed(4000f));
        Assert.Equal(4000f, apoapsis, 1f);
        Assert.Equal(4000f, periapsis, 1f);
    }

    [Fact]
    public void An_escape_keeps_an_infinite_apoapsis()
    {
        var (apoapsis, periapsis) = Planet.Orbit(AboveTheSea(4000f), Vector3.UnitX * 2f * Planet.CircularSpeed(4000f));
        Assert.True(float.IsPositiveInfinity(apoapsis));
        Assert.Equal(4000f, periapsis, 1f);
    }

    [Fact]
    public void A_rocket_on_Homes_pad_is_on_the_ground_35_m_above_the_sea()
    {
        using var sim = new Simulation();
        var rocket = sim.Flight.Controlled;
        Assert.True(rocket.Ground < 0.1f, $"{rocket.Ground} m over the pad");
        Assert.Equal(35f + Scene.PadTop, rocket.Altitude, 0.5f);
    }

    [Fact]
    public void High_over_a_slope_with_its_legs_out_its_height_is_its_lowest_foots()
    {
        // 300 m over a slope of 8 to 12°, its legs out (far from the ground, where its height is
        // read from the terrain alone): its height over the ground is its lowest foot's over the
        // terrain under that foot, the feet some 10 m apart across the slope. Leaning 3° uphill, its
        // uphill foot is its lowest above the sea, but its downhill one is its lowest above the ground.
        var (at, downhill, degrees) = Drops.FindSlope(8f, 12f);
        using var sim = Drops.At(at, -downhill, 0f, leanDegrees: -3f, clearance: 300f);
        var booster = sim.Flight.Controlled;
        sim.Step(default);
        var feet = Enumerable.Range(0, RocketGeometry.LegCount)
            .Select(i => Planet.TerrainAltitude(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, i), booster.Model)) - LegGeometry.Rocket.Stroke).ToList();
        var told = $"on {degrees:0.0}°: Ground {booster.Ground:0.000} m; the feet over the ground {string.Join(", ", feet.Select(f => f.ToString("0.000")))} m";
        Assert.True(feet.Max() - feet.Min() > 0.5f, $"the feet are all but level, so this shows nothing: {told}");
        Assert.True(MathF.Abs(booster.Ground - feet.Min()) < 0.05f, told);
    }

    private static readonly Pilot Launch = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;
    private const int Climb = 1; // Launch and Land's "Climb past 400 m"

    [Fact]
    public void At_Highcrest_the_climb_is_done_before_lift_off()
    {
        using var sim = new Simulation(site: LaunchSites.Named("Highcrest"));
        sim.Run(Launch, _ => false, maxSeconds: 1);
        Assert.True(sim.Flight.Run!.Progress.IsDone(Climb));
    }

    [Fact]
    public void At_Home_the_climb_needs_the_climb()
    {
        using var sim = new Simulation();
        sim.Run(Launch, _ => false, maxSeconds: 1);
        Assert.False(sim.Flight.Run!.Progress.IsDone(Climb));
    }

    [Fact]
    public void Afloat_its_altitude_is_below_0()
    {
        var water = Planet.Centre + WaterTests.DeepWater.Value * Planet.SeaRadius;
        using var sim = Drops.At(water, Planet.East(water), 3f);
        var vessel = sim.Flight.Controlled;
        Assert.True(sim.Run((_, _) => default, _ => vessel.State != FlightState.Flying, maxSeconds: 90));
        Assert.Equal(FlightState.Floating, vessel.State);
        Assert.Equal(0f, vessel.Ground);
        // Its lowest point is under the water: a height below the sea, not clamped to 0.
        Assert.True(vessel.Altitude is < 0f and > -30f, $"{vessel.Altitude} m above the sea");
    }

    [Fact]
    public void On_the_seabed_its_altitude_is_the_depth()
    {
        var at = SplashdownTests.ShallowWater.Value;
        var seabed = Terrain.Height(Planet.Up(at));
        using var sim = Drops.At(at, Planet.East(at), 2f, seabed: true);
        var vessel = sim.Flight.Controlled;
        Assert.True(sim.Run((_, _) => default, _ => vessel.State != FlightState.Flying, maxSeconds: 30));
        Assert.Equal(FlightState.Landed, vessel.State);
        Assert.True(vessel.Ground < 0.2f, $"{vessel.Ground} m over the seabed");
        // Its feet on the seabed, about 3 m down (and up to a leg's stroke lower, as Ground reckons it).
        Assert.InRange(vessel.Altitude, seabed - LegGeometry.Rocket.Stroke - 0.5f, seabed + 0.5f);
    }

    [Fact]
    public void The_highest_over_the_ground_is_kept()
    {
        // What the "Too fast!" cue goes by: at Highcrest, 1,242 m above the sea, a hop of 10 m is a hop.
        using var sim = new Simulation(site: LaunchSites.Named("Highcrest"));
        var rocket = sim.Flight.Controlled;
        sim.Run(Launch, _ => rocket.Ground > 10f, maxSeconds: 10);
        sim.Run((_, _) => new FlightInput(CutThrottle: true), _ => false, maxSeconds: 0.5);
        Assert.InRange(rocket.MaxGround, 10f, 30f);
        Assert.True(rocket.MaxAltitude > 1200f);
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => rocket.Ground > 80f, maxSeconds: 20);
        Assert.True(rocket.MaxGround > 80f, $"{rocket.MaxGround}");
    }

    [Fact]
    public void An_old_world_never_crashes_the_list()
    {
        // A world saved before heights were from the sea, with the field that was dropped then.
        var folder = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var sim = new Simulation();
            var store = new WorldStore(folder);
            store.Save(sim.Flight.Capture() with { Id = "old", Name = "Old" });
            var file = Directory.GetFiles(folder, "*.json").Single();
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"MaxAltitude\"", "\"MaxSphereAltitude\": 12.5, \"MaxAltitude\""));
            Assert.Single(store.List());
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_satellites_world_starts_in_stable_orbits()
    {
        foreach (var v in WorldPresets.Satellites().Vessels)
            Assert.True(Planet.Orbit(v.CentreOfMass, Planet.AmongStars(v.CentreOfMass, v.Velocity)).Periapsis > Scene.AtmosphereHeight, $"vessel {v.Id}");
    }
}
