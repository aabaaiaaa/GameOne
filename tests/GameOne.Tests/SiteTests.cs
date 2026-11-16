using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Quick tests at every launch site (<see cref="LaunchSites.All"/>), each started where it is
/// needed rather than flown there from Home: the rocket lifts off its pad, and a booster flown
/// down from about 30 m comes down safely where one lands there (its landing zone, its drone ship,
/// or the ground or sea beside the pad), on the ground each site is in.
/// </summary>
public sealed class SiteTests(ITestOutputHelper output)
{
    public static TheoryData<string> Sites => [.. LaunchSites.All.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Sites))]
    public void Lifts_off_from_its_pad(string name)
    {
        var site = LaunchSites.Named(name);
        using var sim = new Simulation(site: site);
        sim.Run((f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default, _ => false, maxSeconds: 3);
        var rocket = sim.Flight.Controlled;
        output.WriteLine($"{name}: {rocket.State} {rocket.CrashReason}, {rocket.Ground:0.0} m up");
        Assert.Equal(FlightState.Flying, rocket.State);
        Assert.True(rocket.Ground > 20f, $"only {rocket.Ground:0.0} m up");
    }

    /// <summary>
    /// Somewhere a booster would set down beside <paramref name="site"/>'s pad: the first ground (or sea)
    /// round it, 60 m out and on, gentler than 10°, where it really is (off a mountain pad, it can be
    /// tens of metres below, and the ground right beside it a cliff); or, with none within 200 m, the pad itself.
    /// </summary>
    private static Vector3 Beside(LaunchSite site)
    {
        for (var metres = 60f; metres <= 200f; metres += 20f)
            for (var k = 0; k < 16; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 16);
                var d = Planet.Up(site.ToWorld(new Vector3(s * metres, 0f, -c * metres)));
                var gentle = Terrain.IsSea(d) || Vector3.Dot(Terrain.Normal(d), d) > MathF.Cos(float.DegreesToRadians(10f));
                if (gentle)
                    return Planet.Centre + d * (Planet.SeaRadius + Terrain.SurfaceHeight(d));
            }
        return site.Pad; // nowhere gentle beside it (Northreach, on its steep coast): back on the pad, the one flat place
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void A_booster_comes_down_safely_there(string name)
    {
        var site = LaunchSites.Named(name);
        // Where one lands there, and whether it is a target to land on.
        Vector3 where;
        bool target;
        if (site.LandingZone is { } zone)
            (where, target) = (zone, true);
        else if (site.DroneShips.Count > 0)
        {
            var ship = Rafts.Ships.Single(s => Vector3.Distance(s.Centre, site.DroneShips[0]) < 1f);
            ship.Set(0.0); // the simulation's world starts at time 0
            (where, target) = (ship.Current.Position + ship.Normal * Scene.DroneShipFreeboard, true);
        }
        else
            (where, target) = (Beside(site), false);

        var mission = new Mission { Name = "TEST", Briefing = "", Vehicle = Vehicle.Booster, Steps = [], LandingSites = target ? [site.LandingZone ?? site.DroneShips[0]] : [] };
        using var sim = Drops.At(where, Planet.East(where), 0f, clearance: 30f, mission: mission);
        var booster = sim.Flight.Controlled;
        var pilot = new Autopilot { Target = where, CutOffAltitude = 0f };
        sim.Run(pilot.Fly, f => booster.State != FlightState.Flying, maxSeconds: 60);
        output.WriteLine($"{name}: {booster.State} {booster.CrashReason}, touchdown {booster.TouchdownSpeed:0.0} m/s, on target {booster.LandedOnTarget}, {booster.DistanceToLandingSite:0.0} m off, after {sim.Time:0.0} s");
        Assert.True(booster.State is FlightState.Landed or FlightState.Floating, $"{booster.State} {booster.CrashReason}");
        if (target)
            Assert.True(booster.LandedOnTarget, $"not on {name}'s {(site.HasLandingZone ? "landing zone" : "drone ship")}");
    }
}
