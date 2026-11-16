using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The moon's landing spot's light: on every lap of the moon over a year the sun is up there, with
/// the planet in its sky, for a fair stretch (a sixth of the moon's day), so a lander can be sent at
/// any time of year.
/// </summary>
public sealed class LandingSpotLightTests(ITestOutputHelper output)
{
    /// <summary>The least a lap's daylight with the planet up may be, in seconds: a sixth of the moon's day (about 1,800 s).</summary>
    private const double FairSeconds = 300.0;

    [Fact]
    public void Every_lap_of_the_moon_has_a_fair_stretch_of_daylight_at_the_landing_spot_with_the_planet_in_its_sky()
    {
        var year = Scene.YearDays * (double)Scene.DaySeconds;
        var lap = SolarSystem.MoonLapSeconds;
        var laps = (int)Math.Floor(year / lap); // whole laps: the year's end cuts the next one short
        var least = double.MaxValue;
        var failed = new List<int>();
        var lit = false;
        for (var i = 0; i < laps; i++)
        {
            double day = 0, planet = 0, both = 0;
            var from = Scene.ClockStart + i * lap;
            for (var t = from; t < from + lap; t += SpotSky.Step)
            {
                lit = SpotSky.Lit(t, lit);
                var seen = SpotSky.PlanetSeen(t);
                if (lit) day += SpotSky.Step;
                if (seen) planet += SpotSky.Step;
                if (lit && seen) both += SpotSky.Step;
            }
            output.WriteLine($"lap {i + 1}: daylight {day:0} s, planet up {planet:0} s, together {both:0} s");
            least = Math.Min(least, both);
            if (both < FairSeconds)
                failed.Add(i + 1);
        }
        output.WriteLine($"the year's least together: {least:0} s");
        Assert.True(failed.Count == 0, $"laps with under {FairSeconds:0} s of daylight and the planet up together: {string.Join(", ", failed)} (least {least:0} s)");
    }

    [Fact]
    public void The_fair_time_has_the_sun_up_and_the_planet_in_the_sky()
    {
        var t = SpotSky.FairTime(Scene.ClockStart);
        output.WriteLine($"fair time {t:0}");
        Assert.True(SpotSky.Lit(t, false));
        Assert.True(SpotSky.PlanetSeen(t));
        Assert.True(SpotSky.Lit(t + SpotSky.Holds, true));
        Assert.True(SpotSky.PlanetSeen(t + SpotSky.Holds));
    }
}
