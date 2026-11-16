using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The sun's motion (<see cref="Sun"/>): across every place's sky from the world's clock, once a
/// day of 20 minutes, rising in the east; each place's local time by its longitude; once round
/// the stars in a year; moved by the time warp as much as the clock; and the clock's text.
/// </summary>
public sealed class SunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>The angle between two directions, in degrees (worked out in double, so a tiny one isn't lost to acos's rounding).</summary>
    private static double Degrees(Vector3 a, Vector3 b)
    {
        var (x, y) = (Vector3.Normalize(a), Vector3.Normalize(b));
        var cross = Vector3.Cross(x, y).Length();
        return double.RadiansToDegrees(Math.Atan2(cross, Vector3.Dot(x, y)));
    }

    /// <summary>How far <paramref name="to"/> is round from <paramref name="from"/> about <see cref="Planet.NorthPole"/>, in degrees (positive the planet's way round), across the plane of the equator.</summary>
    private static double RoundThePole(Vector3 from, Vector3 to)
    {
        var pole = Planet.NorthPole;
        var (a, b) = (from - pole * Vector3.Dot(from, pole), to - pole * Vector3.Dot(to, pole));
        return double.RadiansToDegrees(Math.Atan2(Vector3.Dot(Vector3.Cross(a, b), pole), Vector3.Dot(a, b)));
    }

    private static Vector3 SunAt(Vector3 p, double localSeconds) => Sun.Direction(GroundBody.Planet, Sun.WorldTimeFor(p, localSeconds));

    [Fact]
    public void Noon_is_overhead_at_Home_and_midnight_below()
    {
        Assert.InRange(Degrees(SunAt(Vector3.Zero, 600), Vector3.UnitY), 0, 0.1);
        Assert.InRange(Degrees(SunAt(Vector3.Zero, 0), -Vector3.UnitY), 0, 0.1);
        Assert.InRange(Degrees(SunAt(Vector3.Zero, 300), Vector3.UnitX), 0, 0.1); // 05:00: the sun rises in the east
        Assert.InRange(Degrees(SunAt(Vector3.Zero, 900), -Vector3.UnitX), 0, 0.1); // 15:00: and sets in the west
    }

    [Fact]
    public void A_new_world_starts_at_half_past_seven_at_Home()
    {
        Assert.Equal(450, Sun.LocalSeconds(GroundBody.Planet, Vector3.Zero, 0), 6);
        Assert.Equal("Day 1, 07:30", Sun.WorldClock(0));
    }

    [Fact]
    public void A_day_is_twenty_minutes()
    {
        Assert.Equal(0f, Scene.AxialTilt); // (with a tilt the sun's height would change a little over a day)
        foreach (var t in new[] { 0.0, 333.0, 1e7 })
            Assert.InRange(Degrees(Sun.Direction(GroundBody.Planet, t), Sun.Direction(GroundBody.Planet, t + 1200)), 0, 0.01);
        Assert.Equal("Day 2, 00:00", Sun.WorldClock(1200 - 450));

        // And after a long play it is still where the clock says: Home's noon, some 8,300 days in, is overhead.
        var noon = Sun.WorldTimeFor(Vector3.Zero, 600) + 8333 * 1200.0;
        Assert.InRange(noon, 9.9e6, 1.01e7);
        Assert.InRange(Degrees(Sun.Direction(GroundBody.Planet, noon), Vector3.UnitY), 0, 0.1);
    }

    [Fact]
    public void A_tilted_planet_has_the_sun_off_the_equator_by_the_season()
    {
        // (A tilt the planet doesn't have, through the internal overload.) A year starts with the sun
        // on the equator; a quarter on, it is the tilt north of it, and three quarters on, south.
        const double year = Scene.YearDays * (double)Scene.DaySeconds;
        double Declination(double clock, float tilt) =>
            double.RadiansToDegrees(Math.Asin(Vector3.Dot(Sun.Direction(clock - Scene.ClockStart, tilt), Planet.NorthPole)));
        Assert.Equal(0, Declination(year, 23.5f), 3);
        Assert.Equal(23.5, Declination(year / 4, 23.5f), 3);
        Assert.Equal(-23.5, Declination(year * 3 / 4, 23.5f), 3);
        Assert.Equal(0, Declination(year / 4, 0f), 3);
    }

    [Fact]
    public void Local_time_goes_by_longitude()
    {
        // A quarter turn east round the equator: 300 s (a quarter day) later.
        var east = Planet.Centre + Vector3.UnitX * Planet.Radius;
        foreach (var t in new[] { 0.0, 1000.0, 54321.0 })
        {
            var later = Sun.LocalSeconds(GroundBody.Planet, east, t) - Sun.LocalSeconds(GroundBody.Planet, Vector3.Zero, t);
            later -= Math.Floor(later / Scene.DaySeconds) * Scene.DaySeconds;
            Assert.Equal(300, later, 2);
        }

        // Polar, far south: the sun circles low all day, never higher than the pole's tilt from overhead, and is up at noon.
        var polar = LaunchSites.Named("Polar");
        var highest = 90 - Math.Abs(polar.Latitude) + 0.5;
        for (var s = 0; s < Scene.DaySeconds; s += 10)
        {
            var height = 90 - Degrees(polar.Up, SunAt(polar.Pad, s));
            Assert.True(height <= highest, $"at {Sun.Clock(s)} the sun is {height:0.0}° up at Polar");
        }
        Assert.True(Vector3.Dot(polar.Up, SunAt(polar.Pad, 600)) > 0f, "the sun should be up at Polar's local noon");
    }

    [Fact]
    public void WorldTimeFor_round_trips()
    {
        foreach (var site in LaunchSites.All)
            foreach (var s in new[] { 0.0, 450.0, 1199.0 })
            {
                var t = Sun.WorldTimeFor(site.Pad, s);
                Assert.InRange(t, 0, Scene.DaySeconds - 1e-9);
                var back = Sun.LocalSeconds(GroundBody.Planet, site.Pad, t);
                var off = Math.Abs(back - s);
                off = Math.Min(off, Scene.DaySeconds - off); // (just under a day and just over 0 are the same moment)
                Assert.True(off < 0.01, $"{site.Name} at {s} s: back {back} s");
            }
    }

    [Fact]
    public void The_sun_goes_once_round_the_stars_in_a_year()
    {
        const double year = Scene.YearDays * Scene.DaySeconds;
        static Vector3 AmongStars(double t) => Planet.TurnedDirection(Sun.Direction(GroundBody.Planet, t), t);
        foreach (var t in new[] { 0.0, 777.0 })
        {
            Assert.InRange(Degrees(AmongStars(t), AmongStars(t + year)), 0, 0.01);
            Assert.Equal(90, RoundThePole(AmongStars(t), AmongStars(t + year / 4)), 1); // eastward: the planet's way round
        }
    }

    [Fact]
    public void The_warp_moves_the_sun_with_the_clock()
    {
        using var sim = new Simulation();
        var flight = sim.Flight;
        var (startTime, startSun) = (flight.WorldTime, Sun.Direction(GroundBody.Planet, flight.WorldTime));
        for (var frame = 0; frame < 60; frame++) // one real second's frames, at 32x
            flight.Advance(32f / 60f, default);
        Assert.Equal(startTime + 32, flight.WorldTime, 3);
        var turned = RoundThePole(startSun, Sun.Direction(GroundBody.Planet, flight.WorldTime));
        Assert.InRange(-turned, 9.6 * 0.99, 9.6 * 1.01); // (the sun goes round the ground westward, against the pole)
    }

    [Fact]
    public void The_clock_and_the_sun_survive_a_save()
    {
        var store = new WorldStore(_directory);
        using (var sim = new Simulation(world: new WorldState { Time = 777 }))
            store.Save(sim.Flight.Capture() with { Id = "late", Name = "Later in the day" });
        var back = store.Load("late");
        Assert.Equal(777, back.Time);
        using var again = new Simulation(world: back);
        Assert.Equal(777, again.Flight.WorldTime);
        Assert.Equal(Sun.Direction(GroundBody.Planet, 777), Sun.Direction(GroundBody.Planet, again.Flight.WorldTime));
    }

    [Fact]
    public void Clock_text()
    {
        Assert.Equal("00:00", Sun.Clock(0));
        Assert.Equal("07:42", Sun.Clock(462));
        Assert.Equal("19:59", Sun.Clock(1199.9));
    }
}
