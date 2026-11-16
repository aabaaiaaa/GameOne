using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The air's light (<see cref="Air"/>, the reference the sky's tables and shaders repeat): a blue
/// sky and a paler horizon at noon, an orange low sun, no sun at night, twilight while the air
/// above is still lit, a thinner sky higher up, the rim glowing from orbit, finite light from any
/// ray, and a noon haze half faded at about 2.3 km.
/// </summary>
public sealed class AirTests
{
    private static readonly Vector3 Home = LaunchSites.Home.Pad;

    private static LaunchSite Site(string name) => LaunchSites.All.Single(s => s.Name == name);

    private static Vector3 SunAt(Vector3 p, double localSeconds) => Sun.Direction(GroundBody.Planet, Sun.WorldTimeFor(p, localSeconds));

    private static float Sum(Vector3 c) => c.X + c.Y + c.Z;

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    private static Vector3 ToLinear(Vector3 c) => new(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f));

    private static bool Fine(Vector3 c) => float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.Z) && c.X >= 0 && c.Y >= 0 && c.Z >= 0;

    private static Vector3 ZenithSky(Vector3 pad, Vector3 sun) => Air.Sky(pad + Planet.Up(pad) * 2f, Planet.Up(pad), sun);

    [Fact]
    public void The_noon_sky_is_blue()
    {
        var sky = ZenithSky(Home, SunAt(Home, 600));
        Assert.True(sky.Z > sky.Y && sky.Y > sky.X, $"{sky}");
    }

    [Fact]
    public void The_horizon_is_paler_and_brighter_than_the_zenith()
    {
        var sun = SunAt(Home, 600);
        var zenith = ZenithSky(Home, sun);
        var level = Air.Sky(Home + Planet.Up(Home) * 2f, Planet.North(Home), sun);
        Assert.True(Sum(level) > Sum(zenith), $"{level} vs {zenith}");
        Assert.True(level.Z / level.X < zenith.Z / zenith.X, $"{level} vs {zenith}");
    }

    [Fact]
    public void A_low_sun_is_orange()
    {
        var low = Planet.Up(Home) * MathF.Sin(float.DegreesToRadians(1)) + Planet.East(Home) * MathF.Cos(float.DegreesToRadians(1));
        var light = Air.Sunlight(Home, low);
        var noon = Air.Sunlight(Home, SunAt(Home, 600));
        Assert.True(light.X > light.Y && light.Y > light.Z, $"{light}");
        Assert.True(light.Z < 0.3f * noon.Z, $"{light} vs {noon}");
    }

    [Fact]
    public void Noon_sunlight_is_nearly_white()
    {
        var light = Air.Sunlight(Home, SunAt(Home, 600));
        var sun = ToLinear(Scene.SunColor);
        Assert.True(light.X > 0.75f * sun.X && light.Y > 0.75f * sun.Y && light.Z > 0.75f * sun.Z, $"{light} of {sun}");
    }

    [Fact]
    public void No_sunlight_reaches_the_night_side()
    {
        var sun = SunAt(Home, 0);
        Assert.Equal(Vector3.Zero, Air.Sunlight(Home, sun));
        Assert.Equal(Vector3.Zero, Air.Transmittance(Home + Planet.Up(Home) * 50f, sun));
    }

    [Fact]
    public void Twilight_is_the_air_still_lit_above()
    {
        var noon = Sum(ZenithSky(Home, SunAt(Home, 600)));
        var dusk = Sun.Direction(GroundBody.Planet, Sun.WorldTimeFor(Home, 900) + 20);
        var night = SunAt(Home, 0);
        Assert.True(Sum(ZenithSky(Home, dusk)) > 0.02f * noon);
        Assert.True(Sum(ZenithSky(Home, night)) < 0.001f * noon);
    }

    [Fact]
    public void Higher_places_see_a_darker_sky()
    {
        // The same sun and the same place, only higher: Home's spot raised to Highcrest's elevation (its own latitude would change the sun).
        var up = Planet.Up(Home);
        var sun = SunAt(Home, 600);
        var high = Home + up * (Site("Highcrest").Elevation - Scene.HomeElevation);
        Assert.True(Sum(ZenithSky(high, sun)) < Sum(ZenithSky(Home, sun)));
    }

    [Fact]
    public void Polar_is_lit_by_a_low_sun_all_day()
    {
        var polar = Site("Polar").Pad;
        var at = Air.Sunlight(polar, SunAt(polar, 600));
        var home = Air.Sunlight(Home, SunAt(Home, 600));
        Assert.True(at.X / at.Z > home.X / home.Z, $"{at} vs {home}");
    }

    [Fact]
    public void From_orbit_the_rim_glows_and_space_is_black()
    {
        var up = Planet.Up(Home);
        var from = Home + up * 6000f;
        var sun = SunAt(Home, 600);
        Assert.Equal(Vector3.Zero, Air.Sky(from, up, sun));
        // A ray tangent to the sphere 1000 m above the sea, towards the north.
        var sinAway = (Planet.SeaRadius + 1000f) / (from - Planet.Centre).Length();
        var dir = -up * MathF.Sqrt(1 - sinAway * sinAway) + Planet.North(Home) * sinAway;
        var rim = Air.Sky(from, dir, sun);
        Assert.True(Sum(rim) > Sum(ZenithSky(Home, sun)), $"{rim}"); // against the noon zenith: the absolute exposure is the shaders'
    }

    [Fact]
    public void Heights_below_the_sea_give_finite_light()
    {
        var sun = SunAt(Home, 600);
        var below = Planet.Centre + Planet.Up(Home) * (Planet.SeaRadius - 50f);
        Assert.True(Fine(Air.Sky(below, Planet.Up(Home), sun)));
        Assert.True(Fine(Air.Sunlight(below, sun)));
        Assert.True(Fine(Air.SkyLight(-50f, 1f)));
    }

    [Fact]
    public void Rays_up_down_and_grazing_are_finite()
    {
        var sun = SunAt(Home, 600);
        var up = Planet.Up(Home);
        var from = Home + up * 2f;
        // The true horizon dips by acos(sea radius / distance from the centre); try rays just above and just below it.
        var dip = MathF.Acos(Planet.SeaRadius / (from - Planet.Centre).Length());
        Vector3 Dipping(float angle) => Planet.North(Home) * MathF.Cos(angle) - up * MathF.Sin(angle);
        var (above, below) = (Air.Sky(from, Dipping(dip - 0.001f), sun), Air.Sky(from, Dipping(dip + 0.001f), sun));
        Assert.True(Fine(above) && Fine(below), $"{above} {below}");
        Assert.True(Sum(below) <= Sum(above), $"{below} vs {above}"); // it ends at the sea, the other carries on through the air
        foreach (var dir in new[] { up, -up, Planet.North(Home) })
            Assert.True(Fine(Air.Sky(from, dir, sun)), $"{dir}");
    }

    [Fact]
    public void Sunlight_from_above_the_air_is_the_suns_own()
    {
        var light = Air.Sunlight(Home + Planet.Up(Home) * 6000f, SunAt(Home, 600));
        var sun = ToLinear(Scene.SunColor);
        Assert.True(Vector3.Distance(light, sun) < 1e-5f, $"{light} vs {sun}");
    }

    [Fact]
    public void A_segment_gives_what_its_ray_does()
    {
        var up = Planet.Up(Home);
        var from = Home + up * 2f;
        var sun = SunAt(Home, 600);
        var top = Planet.Centre + up * (Planet.SeaRadius + Scene.AtmosphereHeight);
        var whole = Air.TransmittanceBetween(from, top);
        var ray = Air.Transmittance(from, up);
        Assert.True(Vector3.Distance(whole, ray) < 0.03f, $"{whole} vs {ray}"); // (16 steps against 8)
        var scattered = Air.InScatter(from, top, sun);
        var sky = Air.Sky(from, up, sun);
        Assert.True(Vector3.Distance(scattered, sky) < 1e-5f, $"{scattered} vs {sky}");
        Assert.Equal(Vector3.One, Air.TransmittanceBetween(from, from));
        // A point under the sea ends the segment at the sea, however deep it is.
        var seabed = Planet.Centre + up * (Planet.SeaRadius - 40f);
        Assert.Equal(Air.TransmittanceBetween(from, seabed), Air.TransmittanceBetween(from, Planet.Centre + up * (Planet.SeaRadius - 400f)));
    }

    [Fact]
    public void The_noon_zenith_keeps_todays_luminance()
    {
        // Before the air's model the sky was a gradient whose zenith was this display colour; the
        // model's radiance is physical (a few hundredths of the sunlight), so the shaders scale it
        // by Scene.SkyExposure, which is chosen so the noon zenith from the pad is as bright to the
        // eye (Rec. 709 luminance) as that was.
        var today = Luminance(ToLinear(new Vector3(0.20f, 0.40f, 0.74f)));
        var zenith = Luminance(ZenithSky(Home, SunAt(Home, 600)) * Scene.SkyExposure);
        Assert.InRange(zenith, 0.85f * today, 1.15f * today);
    }

    [Fact]
    public void The_twilight_zenith_is_blue()
    {
        // 20 s after Home's sunset the sun lights only the air above, by light that has come a long
        // way low through it: scattering alone leaves that olive; the ozone takes out its green.
        var dusk = Sun.Direction(GroundBody.Planet, Sun.WorldTimeFor(Home, 900) + 20);
        var sky = ZenithSky(Home, dusk);
        Assert.True(sky.Z > sky.Y, $"{sky}");
    }

    [Fact]
    public void The_flare_dims_with_the_sun()
    {
        var from = Home + Planet.Up(Home) * 2f;
        var low = Planet.Up(Home) * MathF.Sin(float.DegreesToRadians(3)) + Planet.East(Home) * MathF.Cos(float.DegreesToRadians(3));
        Assert.True(Air.FlareDimming(from, SunAt(Home, 600), 1f) > 0.85f, $"{Air.FlareDimming(from, SunAt(Home, 600), 1f)}"); // (0.9 in the plan, before the air was made 1.45 times thicker and given its ozone: 0.87 now)
        Assert.InRange(Air.FlareDimming(from, low, 1f), 0.05f, 0.65f); // (0.6 in the plan: 0.57 now; on this small planet the path through the air grows fast only in the last degree or two)
        Assert.Equal(0f, Air.FlareDimming(from, SunAt(Home, 0), 1f));
    }

    [Fact]
    public void The_noon_haze_is_half_faded_between_2_2_and_2_8_km()
    {
        var extinction = Scene.RayleighScatter + new Vector3(Scene.MieExtinction);
        var mean = (extinction.X + extinction.Y + extinction.Z) / 3f;
        var halfFaded = MathF.Log(2f) / mean; // metres of sea-level air that halve the light: the fog's reach
        Assert.InRange(halfFaded, 2200f, 2800f);
    }
}
