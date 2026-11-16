using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The launch smoke at every launch site (<see cref="LaunchSites.SmokeSites"/>): the engine stirs up
/// and thickens the smoke where its exhaust hits, wherever on the planet that is, measured by the
/// ground there, not the world's Y (which is up only at Home).
/// </summary>
public sealed class SmokeSiteTests
{
    private static LaunchSite Polar => LaunchSites.All.Single(s => s.Name == "Polar");

    [Fact]
    public void Every_pad_and_landing_zone_has_smoke()
    {
        var sites = LaunchSites.SmokeSites;
        Assert.Equal(LaunchSites.Home.Pad, sites[0]);                   // Home's pad and zone first, as ever
        Assert.Equal(Scene.LandingZoneCentre, sites[1]);
        foreach (var site in LaunchSites.All)
        {
            Assert.Contains(site.Pad, sites);
            if (site.LandingZone is { } zone)
                Assert.Contains(zone, sites);
        }
    }

    [Fact]
    public void The_exhaust_on_a_far_sites_pad_thickens_that_sites_smoke()
    {
        var stirs = new SmokeStirs();
        var up = Polar.Up;
        var nozzle = Polar.ToWorld(new Vector3(0f, Scene.PadTop + 5f, 0f));
        for (var i = 0; i < 120; i++)
            stirs.Update(1f / 60f, null, 0f, nozzle, -up, 1f);
        var polar = Array.IndexOf(LaunchSites.SmokeSites, Polar.Pad);
        Assert.True(stirs.Thickness[polar] > Scene.SmokeHaze + 0.1f, $"Polar's pad's smoke is only {stirs.Thickness[polar]:0.00} thick");
        Assert.Equal(Scene.SmokeHaze, stirs.Thickness[0], 3); // Home's is as it was
    }

    [Fact]
    public void The_exhaust_hitting_the_ground_past_a_far_sites_reach_leaves_its_smoke()
    {
        // 100 m north of Polar's pad, over the ground (past Scene.SmokeSiteReach, 90 m), measured
        // along the ground there: not by the world's x and z, which are nothing like it that far round.
        var stirs = new SmokeStirs();
        var ground = Polar.ToWorld(new Vector3(0f, 0f, -100f));
        var nozzle = ground + Planet.Up(ground) * (5f - Planet.SurfaceAltitude(ground)); // 5 m over the surface (ground is a little under the pad's top)
        for (var i = 0; i < 120; i++)
            stirs.Update(1f / 60f, null, 0f, nozzle, -Planet.Up(ground), 1f);
        var polar = Array.IndexOf(LaunchSites.SmokeSites, Polar.Pad);
        Assert.Equal(Scene.SmokeHaze, stirs.Thickness[polar], 3);
    }

    [Fact]
    public void A_blast_at_a_far_site_starts_at_its_ground()
    {
        var stirs = new SmokeStirs();
        var at = Polar.ToWorld(new Vector3(30f, 4f, 0f)); // a little off the pad, 4 m up
        stirs.Blast(at);
        stirs.Update(1e-3f, null, 0f, at, -Polar.Up, 0f);
        var stir = Assert.Single(stirs.Stirs.ToArray());
        Assert.InRange(Planet.SurfaceAltitude(stir.Position), 1f, 3f); // 2 m over the ground there
    }
}
