using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The launch sites round the planet (<see cref="LaunchSites"/>): each a pad and a service tower,
/// some with a landing zone or a drone ship, every one of them usable as Home's is.
/// </summary>
public sealed class LaunchSiteTests(ITestOutputHelper output)
{
    private static void Near(Vector3 expected, Vector3 actual, float within = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < within, $"expected {expected}, got {actual}");

    [Fact]
    public void Home_is_where_the_pad_always_was()
    {
        var home = LaunchSites.Home;
        Assert.Equal("Home", home.Name);
        Near(Vector3.Zero, home.Pad);
        Near(new Vector3(0f, Scene.PadTop + Scene.RocketBellHeight, 0f), home.RocketStart);
        Near(new Vector3(-12f, 0f, -2f), home.TowerFoot);
        Near(Planet.SurfacePoint(-220f, -230f), home.LandingZone!.Value);
        Near(Scene.DroneShipCentre, home.DroneShips[0]);
        foreach (var v in new[] { Vector3.UnitX, new Vector3(3f, -2f, 7f), new Vector3(-100f, 30f, 45f) })
            Near(v, home.ToWorld(v));
        Assert.True(Quaternion.Dot(Quaternion.Identity, home.Frame) > 0.999999f, $"Home's frame is {home.Frame}");
        output.WriteLine($"Home: pad {home.Pad}, zone {home.LandingZone}, ships {string.Join(", ", home.DroneShips)}");
    }

    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>
    /// How high over <paramref name="ground"/> (a pad's or a zone's centre, on its levelled ground) a
    /// booster set down there, landed, stands, along the up there; and, landing there with
    /// <paramref name="mission"/>, whether it landed on target.
    /// </summary>
    private (float Height, bool OnTarget) StandingOn(string name, Vector3 ground, Mission? mission = null)
    {
        using var sim = Drops.At(ground, Planet.East(ground), 1f, mission: mission);
        var booster = sim.Flight.Controlled;
        sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 20);
        var height = Vector3.Dot(booster.Position - ground, Planet.Up(ground));
        output.WriteLine($"{name}: {booster.State} {booster.CrashReason}, {height:0.000} m over its ground, on target {booster.LandedOnTarget}");
        Assert.Equal(FlightState.Landed, booster.State);
        return (height, booster.LandedOnTarget);
    }

    /// <summary>
    /// How high one stands on Home's landing zone: on its concrete, <see cref="Scene.PadTop"/> over its
    /// ground, as on a pad. (Not on Home's pad: a new flight clears that for its own rocket.)
    /// </summary>
    private float OnConcrete() => StandingOn("Home's landing zone", LaunchSites.Home.LandingZone!.Value).Height;

    [Fact]
    public void A_booster_set_down_on_each_sites_pad_stands_on_it()
    {
        // As high as on concrete: on the pad, not on the ground under it.
        var concrete = OnConcrete();
        foreach (var site in LaunchSites.All.Skip(1))
            Assert.True(MathF.Abs(StandingOn($"{site.Name}'s pad", site.Pad).Height - concrete) < 0.1f,
                $"on {site.Name}'s pad it stands at another height than on concrete ({concrete:0.000} m)");
    }

    [Fact]
    public void Each_sites_tower_is_solid()
    {
        // (Not Home's: a new flight clears what is near its pad, and FlightTests flies into it.)
        foreach (var site in LaunchSites.All.Skip(1))
        {
            // Flown north into the tower's side at 10 m/s, its legs stowed, from 8 m south of it, 6 m up.
            var state = Drops.Placed(site.Pad, Planet.East(site.Pad), 0f, legsOut: false) with
            {
                Position = site.ToWorld(Scene.TowerOffset + new Vector3(0f, 6f, 8f)),
                Rotation = site.Frame,
                Velocity = Vector3.Transform(new Vector3(0f, 0f, -10f), site.Frame),
            };
            using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
            var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
            sim.Step(new FlightInput(SwitchTo: vessel));
            sim.Run(Nobody, _ => vessel.State == FlightState.Crashed, maxSeconds: 2);
            output.WriteLine($"{site.Name}: {vessel.State} {vessel.CrashReason}");
            Assert.Equal("Flew into the service tower.", vessel.CrashReason);
        }
    }

    [Fact]
    public void Touching_the_tower_gently_wrecks_the_vessel()
    {
        // Upright, its legs stowed, 10 m up and 3 cm from the tower's side, moving into it at
        // 0.3 m/s, so it has hardly begun to fall as it touches it (Jolt begins a contact a couple
        // of centimetres off): the tower wrecks on any touch, however gentle.
        var site = LaunchSites.All[1];
        var radius = Vehicle.Booster.Stages[0].Radius;
        var start = site.ToWorld(Scene.TowerOffset + new Vector3(0f, 10f, Scene.TowerHalfWidth + radius + 0.03f));
        var state = Drops.Placed(site.Pad, Planet.East(site.Pad), 0f, legsOut: false) with
        {
            Position = start,
            Rotation = site.Frame,
            Velocity = Vector3.Transform(new Vector3(0f, 0f, -0.3f), site.Frame),
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        var touched = 0f;
        sim.Run(Nobody, _ =>
        {
            foreach (var touch in sim.Physics.TouchesBegun(vessel.Craft))
                if (touch.With == SolidKind.Tower)
                    touched = MathF.Max(touched, touch.ClosingSpeed);
            return vessel.State == FlightState.Crashed;
        }, maxSeconds: 1.5);
        output.WriteLine($"{site.Name}: {vessel.State} {vessel.CrashReason}, touching the tower at {touched:0.00} m/s");
        Assert.Equal("Flew into the service tower.", vessel.CrashReason);
        Assert.InRange(touched, 0.1f, 0.7f);
    }

    [Fact]
    public void A_booster_lands_on_each_sites_landing_zone()
    {
        // On its concrete, as on Home's, and on target there.
        var concrete = OnConcrete();
        foreach (var site in LaunchSites.All.Skip(1).Where(s => s.HasLandingZone))
        {
            var zone = site.LandingZone!.Value;
            var mission = new Mission { Name = "TEST", Briefing = "", Vehicle = Vehicle.Booster, Steps = [], LandingSites = [zone] };
            var (height, onTarget) = StandingOn($"{site.Name}'s landing zone", zone, mission);
            Assert.True(MathF.Abs(height - concrete) < 0.1f, $"on {site.Name}'s landing zone it stands at another height than on concrete ({concrete:0.000} m)");
            Assert.True(onTarget, $"not on {site.Name}'s landing zone");
        }
    }
    [Fact]
    public void A_rocket_put_on_each_sites_pad_stands_upright_on_it()
    {
        foreach (var site in LaunchSites.All)
        {
            using var sim = new Simulation(site: site);
            var rocket = sim.Flight.Controlled;
            Assert.Equal(site, sim.Flight.Site);
            Assert.Equal(FlightState.Ready, rocket.State);
            output.WriteLine($"{site.Name}: lean {rocket.TiltDegrees:0.000}°, {Vector3.Distance(rocket.Position, site.RocketStart):0.000} m from its start");
            Assert.True(rocket.TiltDegrees < 0.5f, $"at {site.Name} it leans {rocket.TiltDegrees:0.0}°");
            Assert.True(Vector3.Distance(rocket.Position, site.RocketStart) < 0.1f, $"at {site.Name} it isn't on the pad");
        }
    }

    [Fact]
    public void A_new_rocket_clears_only_its_own_pad()
    {
        // A booster standing on Polar's pad, and one on Desert's: a flight launched from Polar clears Polar's.
        static VesselState StandingOn(LaunchSite site, int id) => Drops.Placed(site.Pad, Planet.East(site.Pad), 0f, id: id) with { State = FlightState.Landed };
        var (polar, desert) = (LaunchSites.All.Single(s => s.Name == "Polar"), LaunchSites.All.Single(s => s.Name == "Desert"));
        using var sim = new Simulation(world: new WorldState { Vessels = [StandingOn(polar, 0), StandingOn(desert, 1)], NextVesselId = 2 }, site: polar);
        Assert.Equal(1, sim.Flight.ClearedFromPad);
        Assert.DoesNotContain(sim.Flight.WorldVessels, v => v.Id == 0);
        Assert.Contains(sim.Flight.WorldVessels, v => v.Id == 1);
    }

    private static LaunchSite Polar => LaunchSites.All.Single(s => s.Name == "Polar");

    [Fact]
    public void Choosing_a_mission_from_a_site_launches_it_there()
    {
        using var sim = new Simulation();
        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand, From: Polar));
        Assert.Same(Polar, sim.Flight.Site);
        Assert.True(Vector3.Distance(sim.Flight.Controlled.Position, Polar.RocketStart) < 0.1f);
    }

    [Fact]
    public void Choosing_a_mission_at_another_site_leaves_the_rocket_waiting_at_the_first()
    {
        // The rocket waiting on Home's pad is its run's, still in progress: launching from Polar
        // clears only Polar's pad, so it stays standing there.
        using var sim = new Simulation();
        var waiting = sim.Flight.Controlled;
        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand, From: Polar));
        Assert.Equal(2, sim.Flight.WorldVessels.Count);
        Assert.Equal(FlightState.Ready, waiting.State);
        Assert.Equal(0, sim.Flight.ClearedFromPad);
    }

    [Fact]
    public void The_physics_can_be_reset_to_any_sites_pad()
    {
        using var physics = new GameOne.Physics.RocketPhysics(GroundBody.Planet);
        var craft = physics.Reset(Vehicle.Booster, Polar);
        Assert.True(Vector3.Distance(physics.Position(craft), Polar.RocketStart) < 0.1f, $"at {physics.Position(craft)}");
    }

    [Fact]
    public void Restarting_relaunches_from_the_same_site()
    {
        using var sim = new Simulation(site: Polar);
        sim.Run((f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default, _ => false, maxSeconds: 2);
        sim.Step(new FlightInput(Restart: true));
        Assert.Same(Polar, sim.Flight.Site);
        Assert.Equal(FlightState.Ready, sim.Flight.Controlled.State);
        Assert.True(Vector3.Distance(sim.Flight.Controlled.Position, Polar.RocketStart) < 0.1f);
    }

    [Fact]
    public void Loading_a_world_keeps_the_picked_site()
    {
        using var sim = new Simulation(site: Polar);
        sim.Flight.Load(new WorldState(), Mission.LaunchAndLand);
        Assert.Same(Polar, sim.Flight.Site);
        Assert.True(Vector3.Distance(sim.Flight.Controlled.Position, Polar.RocketStart) < 0.1f);
    }

    [Fact]
    public void Loading_a_world_from_a_site_puts_its_rocket_there()
    {
        // The world list loads at the row's pick, so the rocket behind the mission menu is where it says.
        using var sim = new Simulation();
        sim.Flight.Load(new WorldState(), Mission.LaunchAndLand, Polar);
        Assert.Same(Polar, sim.Flight.Site);
        Assert.True(Vector3.Distance(sim.Flight.Controlled.Position, Polar.RocketStart) < 0.1f);
    }

    [Fact]
    public void Named_an_unknown_site_is_Home()
    {
        Assert.Same(Polar, LaunchSites.Named("Polar"));
        Assert.Same(LaunchSites.Home, LaunchSites.Named("Nowhere"));
        Assert.Same(LaunchSites.Home, LaunchSites.Named(null));
        Assert.Equal("Home", new Settings().LaunchSite);
    }

    [Fact]
    public void The_site_line_says_what_is_there()
    {
        string Line(string name) => Hud.SiteLine(LaunchSites.Named(name), 0);
        foreach (var site in LaunchSites.All)
            output.WriteLine(Hud.SiteLine(site, 0));
        Assert.Contains("35 m up", Line("Home")); // Home's pad is 35 m above the sea
        Assert.Contains("landing zone", Line("Home"));
        Assert.Contains("drone ship", Line("Home"));
        Assert.Contains("1244 m up", Line("Highcrest")); // the highest ground under its pad
        Assert.DoesNotContain("sea level", Line("Coral Isle")); // levelled 33 m above the water
        Assert.DoesNotContain("landing zone", Line("Coral Isle"));
        Assert.DoesNotContain("drone ship", Line("Coral Isle"));
        Assert.Contains("drone ship", Line("Sentinel"));
        Assert.Contains("70°S", Line("Polar"));
    }

    [Fact]
    public void The_site_line_says_day_or_night()
    {
        // By the sun's height over each site as a new world starts (world time 0, 07:30 at Home):
        // sites go all round the planet, so some are in the dark then.
        var sun = Sun.Direction(GroundBody.Planet, 0);
        foreach (var site in LaunchSites.All)
        {
            var line = Hud.SiteLine(site, 0);
            var up = Vector3.Dot(site.Up, sun) > 0f;
            Assert.True(up ? line.Contains("daylight") && !line.Contains("night") : line.Contains("night") && !line.Contains("daylight"), line);
        }
        Assert.Contains("daylight", Hud.SiteLine(LaunchSites.Home, 0)); // the sun is up over Home
        Assert.Contains(LaunchSites.All, s => Hud.SiteLine(s, 0).Contains("night"));
        // And as the day goes on, Home's turns to night.
        Assert.Contains("night", Hud.SiteLine(LaunchSites.Home, Sun.WorldTimeFor(Vector3.Zero, 0)));
    }

    [Fact]
    public void The_nearest_site_to_each_pad_is_its_own()
    {
        // What the shadows are drawn round (the nearest site's pad and tower), wherever the camera is.
        foreach (var site in LaunchSites.All)
        {
            Assert.Same(site, LaunchSites.Nearest(site.Pad + site.Up * 500f));
        }
    }

    [Fact]
    public void A_site_is_the_same_site_before_and_after_its_height_is_read()
    {
        // (As a record with its height cached in it, its hash changed once the height was read, and
        // a copy made with `with` kept the copied site's direction: a site is a fixed place, by name.)
        var site = new LaunchSite("Test", 40f, 40f, Vector3.One, hasLandingZone: false, droneShips: []);
        var hash = site.GetHashCode();
        _ = site.Elevation;
        Assert.Equal(hash, site.GetHashCode());
    }

    [Fact]
    public void Every_site_fits_the_shaders()
    {
        // The apron is drawn round each pad and zone from the shader's arrays: one too many would be
        // left out silently, so the renderer refuses to start instead, and this says there is room.
        Assert.True(LaunchSites.All.Length <= GameOne.Rendering.VulkanRenderer.MaxSites, $"{LaunchSites.All.Length} sites");
        Assert.True(LaunchSites.All.Count(s => s.HasLandingZone) <= GameOne.Rendering.VulkanRenderer.MaxSites);
    }

    [Fact]
    public void Each_zones_cross_is_square_to_its_pad()
    {
        // Its painted cross runs along the zone's own axes: lined up with its pad's, as Home's is.
        foreach (var site in LaunchSites.All.Skip(1).Where(s => s.HasLandingZone))
        {
            var zone = site.LandingZone!.Value;
            var up = Planet.Up(zone);
            var padSouth = Vector3.Transform(Vector3.UnitZ, site.Frame);
            var along = Vector3.Normalize(padSouth - up * Vector3.Dot(padSouth, up)); // the pad's south, on the zone's ground
            var zoneSouth = Vector3.Transform(Vector3.UnitZ, site.LandingZoneTilt!.Value);
            var degrees = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(along, zoneSouth), -1f, 1f)));
            Assert.True(degrees < 0.5f, $"{site.Name}'s zone is turned {degrees:0.0}° from its pad");
        }
    }

    [Fact]
    public void The_towers_are_each_a_colour_of_their_own() =>
        Assert.Equal(LaunchSites.All.Length, LaunchSites.All.Select(s => s.TowerColour).Distinct().Count());
}
