using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The sun, the planet and the moon on their paths (<see cref="SolarSystem"/>): the sun's
/// direction from them is the clock's (<see cref="Sun.Direction"/>), the planet's lap round the sun is the year,
/// the moon keeps its distance, its lap and its tilt, its face nods at the planet by its spin's tilt
/// (the planet rising and setting from its pole, never from the middle of its near side) and its own
/// axes turn there and back exactly, eclipses come only in their seasons, the
/// moon's clock follows its own sun, the sun's disc is its size over its distance, and the readout
/// and the frame's fill have the moon where it is.
/// </summary>
public sealed class SolarSystemTests(ITestOutputHelper output)
{
    private const double Year = Scene.YearDays * (double)Scene.DaySeconds;
    private static readonly Vector3 Home = LaunchSites.Home.Pad;

    /// <summary>The angle between two directions, in radians (worked out in double, so a tiny one isn't lost to acos's rounding).</summary>
    private static double Angle(Vector3 a, Vector3 b)
    {
        var (x, y) = (Vector3.Normalize(a), Vector3.Normalize(b));
        return Math.Atan2(Vector3.Cross(x, y).Length(), Vector3.Dot(x, y));
    }

    /// <summary><paramref name="x"/> wrapped into [0, <paramref name="period"/>).</summary>
    private static double Wrap(double x, double period) => x - Math.Floor(x / period) * period;

    /// <summary>
    /// The sun's direction as the clock alone gave it before it came from the paths: round once a
    /// day westward over the ground, north or south of the equator by the season (none here). The
    /// reference the paths must match.
    /// </summary>
    private static Vector3 TodaysDirection(double worldTime)
    {
        var c = worldTime + Scene.ClockStart;
        var hour = Math.Tau * Wrap(c, Scene.DaySeconds) / Scene.DaySeconds;
        var declination = Math.Asin(Math.Sin(double.DegreesToRadians(Scene.AxialTilt)) * Math.Sin(Math.Tau * Wrap(c, Year) / Year));
        var across = Math.Cos(declination);
        return Vector3.Normalize(Vector3.UnitX * (float)(across * Math.Sin(hour)) - Vector3.UnitY * (float)(across * Math.Cos(hour))
                                 + Planet.NorthPole * (float)Math.Sin(declination));
    }

    /// <summary>Times through a long-played world: the first day, a year, and 100 and 200 years in, and 1,000 between.</summary>
    private static IEnumerable<double> Times()
    {
        foreach (var t in new[] { 0, 1, 450, 1199, Scene.DaySeconds, Year, 100 * Year, 200 * Year })
            yield return t;
        var random = new Random(1234);
        for (var i = 0; i < 1000; i++)
            yield return random.NextDouble() * 200 * Year;
    }

    [Fact]
    public void The_suns_direction_from_the_paths_is_todays()
    {
        foreach (var t in Times())
        {
            Assert.Equal(1f, SolarSystem.SunDirection(t).Length(), 4);
            var off = Angle(SolarSystem.SunDirection(t), TodaysDirection(t));
            Assert.True(off < 1e-4, $"at {t:0.0} s the sun is {off:E2} rad from where it was");
            Assert.Equal(SolarSystem.SunDirection(t), Sun.Direction(GroundBody.Planet, t));
        }
    }

    [Fact]
    public void The_planets_lap_round_the_sun_is_the_year()
    {
        var lap = Math.Tau * Math.Sqrt(Math.Pow(Scene.SunDistance, 3) / SolarSystem.SunMu);
        // The year is worked out from the turn, a float: the turn's rounding (within a float's step
        // of it) is magnified YearDays + 1 times in the year (the year is 1 / (1/turn - 1/day), and
        // the day is YearDays + 1 turns' worth over the turn's difference from it).
        var turnStep = (MathF.BitIncrement(Planet.TurnSeconds) - Planet.TurnSeconds) / (double)Planet.TurnSeconds;
        var tolerance = turnStep * (Scene.YearDays + 1);
        Assert.InRange(lap / Year - 1, -tolerance, tolerance);
    }

    [Fact]
    public void The_moon_keeps_its_distance_and_its_lap()
    {
        foreach (var t in Times())
            Assert.InRange(Vector3.Distance(SolarSystem.MoonCentre(t), Planet.Centre), Scene.MoonDistance - 0.5f, Scene.MoonDistance + 0.5f);

        // A lap on it is where it was among the stars (the later place turned back by the planet's turn since).
        var lap = SolarSystem.MoonLapSeconds;
        foreach (var t in new[] { 0, 777, Year, 100 * Year, 200 * Year })
        {
            var then = SolarSystem.MoonCentre(t);
            var later = Planet.Turned(SolarSystem.MoonCentre(t + lap), lap);
            Assert.InRange(Vector3.Distance(then, later), 0, 1);
            Assert.True(Vector3.Distance(then, Planet.Turned(SolarSystem.MoonCentre(t + lap / 2), lap / 2)) > Scene.MoonDistance, "half a lap on it should be across the planet");
        }
        Assert.InRange(lap, 1620 - 60, 1620 + 60);
        Assert.InRange(lap / (Math.Tau * Math.Sqrt(Math.Pow(Scene.MoonDistance, 3) / Planet.Mu)) - 1, -1e-9, 1e-9);
    }

    /// <summary>
    /// The moon's orbit's normal at <paramref name="t"/>, in the ground's frame: from its places among
    /// the stars then and a little later (so it goes round it the planet's way).
    /// </summary>
    private static Vector3 OrbitNormal(double t)
    {
        var now = Planet.Turned(SolarSystem.MoonCentre(t), t) - Planet.Centre;
        var next = Planet.Turned(SolarSystem.MoonCentre(t + 10), t + 10) - Planet.Centre;
        return Planet.TurnedDirection(Vector3.Normalize(Vector3.Cross(now, next)), -t);
    }

    [Fact]
    public void The_moons_orbit_is_tilted_and_goes_the_way_the_planet_turns()
    {
        var plane = Planet.TurnedDirection(OrbitNormal(0), 0); // its orbit's normal among the stars, at the start
        foreach (var t in new[] { 0, 333, Year / 3, 100 * Year, 200 * Year })
        {
            Assert.Equal(Scene.MoonTiltDegrees, double.RadiansToDegrees(Angle(OrbitNormal(t), Planet.NorthPole)), 0.01);

            // Among the stars (each place turned on by the planet's turn to then), a second on.
            var now = Planet.Turned(SolarSystem.MoonCentre(t), t) - Planet.Centre;
            var next = Planet.Turned(SolarSystem.MoonCentre(t + 1), t + 1) - Planet.Centre;
            Assert.True(Vector3.Dot(Vector3.Cross(now, next), Planet.NorthPole) > 0, $"at {t:0} s the moon goes against the planet's turn");

            // Its orbit's plane is fixed among the stars: the same normal then as at the start, and
            // the moon's place in that plane.
            var normal = Planet.TurnedDirection(OrbitNormal(t), t);
            Assert.True(Angle(normal, plane) < 1e-4, $"at {t:0} s its orbit's plane has turned {Angle(normal, plane):E2} rad among the stars");
            Assert.InRange(Vector3.Dot(Vector3.Normalize(now), plane), -1e-4, 1e-4);

            // And it spins the way it goes round: its spin axis, tilted from the orbit's normal, is on
            // the normal's side of its orbit.
            var axis = Vector3.Normalize(Planet.TurnedDirection(SolarSystem.MoonSpinAxis(t), t));
            Assert.True(Vector3.Dot(Vector3.Cross(now, next), axis) > 0, "the moon spins against the way it goes round");
        }
    }

    /// <summary>The sun's angle from the moon's rising line, round the north pole (the planet's way round), in radians.</summary>
    private static double FromTheNode(double t)
    {
        var pole = Planet.NorthPole;
        var node = Vector3.Normalize(Vector3.Cross(pole, OrbitNormal(t))); // where the orbit crosses the equator going north
        var sun = SolarSystem.SunDirection(t);
        return Math.Atan2(Vector3.Dot(Vector3.Cross(node, sun), pole), Vector3.Dot(node, sun));
    }

    [Fact]
    public void Eclipses_come_only_in_their_seasons()
    {
        // The moon in front of the sun's disc from somewhere on the planet, or in the planet's shadow:
        // its direction from the centre within the planet's and its own width (and the sun's) of the
        // sun's or of the sun's opposite. Possible only while the sun is within the window of the
        // orbit's line, which the orbit's tilt sets: two seasons a year.
        var margin = Math.Asin((Planet.Radius + Scene.MoonRadius) / Scene.MoonDistance) + SolarSystem.SunAngularRadius;
        var tilt = double.DegreesToRadians(Scene.MoonTiltDegrees);
        var window = Math.Asin(Math.Sin(margin) / Math.Sin(tilt));

        // From a moment with the sun a quarter turn from the line, so 3 years hold 6 whole seasons.
        var start = Wrap(Math.PI / 2 - FromTheNode(0), Math.PI) / Math.Tau * Year;
        var seasons = new int[6];
        for (var t = start; t < start + 3 * Year; t += 10)
        {
            var moon = SolarSystem.MoonCentre(t) - Planet.Centre;
            var sun = SolarSystem.SunDirection(t);
            if (Angle(moon, sun) > margin && Angle(moon, -sun) > margin)
                continue;
            var fromLine = FromTheNode(t);
            Assert.True(Math.Abs(Math.Sin(fromLine)) <= Math.Sin(window) + 1e-4,
                $"an eclipse at {t:0} s with the sun {double.RadiansToDegrees(fromLine):0.0}° from the line, outside the season's {double.RadiansToDegrees(window):0.0}°");
            seasons[(int)Math.Floor((t - start) / (Year / 2))]++;
        }
        Assert.All(seasons, n => Assert.True(n > 0, $"a season with no eclipse: {string.Join(", ", seasons)}"));
    }

    /// <summary>A point on the moon's surface in the ground's frame's direction <paramref name="d"/> from its centre.</summary>
    private static Vector3 OnTheMoon(double t, Vector3 d) => SolarSystem.MoonCentre(t) + Vector3.Normalize(d) * Scene.MoonRadius;

    /// <summary>How far apart two clock readings are, the shorter way round the day (−half a day to half).</summary>
    private static double ClockApart(double later, double earlier) => Wrap(later - earlier + Scene.DaySeconds / 2, Scene.DaySeconds) - Scene.DaySeconds / 2;

    [Fact]
    public void The_moons_spin_among_the_stars_is_its_lap_rate_to_a_doubles_rounding()
    {
        // Among the stars (the planet's turn added back), the spin is the lap rate to a double's
        // rounding, not a float axis's: the same exactly unit axis the orientation is turned about.
        var planetSpin = (Vector3Double)Planet.NorthPole * (Math.Tau / Planet.TurnSeconds);
        foreach (var t in new[] { 0.0, 500, Year / 5, 100 * Year + 321 })
        {
            var amongStars = SolarSystem.MoonSpin(t) + planetSpin;
            Assert.InRange(Math.Abs(amongStars.Length() / (Math.Tau / SolarSystem.MoonLapSeconds) - 1), 0, 1e-13);
        }
    }

    [Fact]
    public void The_moons_local_time_follows_its_sun()
    {
        // Its sun is the sun seen from the moon, not from the planet's centre: the two differ by up to
        // the moon's distance over the sun's, a few seconds of its clock, more than the tolerance at
        // some of these times.
        var parallaxSeen = false;
        foreach (var t in new[] { 0, 500, Year / 5, 100 * Year + 321 })
        {
            var sun = Vector3.Normalize(Planet.Centre + SolarSystem.SunDirection(t) * Scene.SunDistance - SolarSystem.MoonCentre(t));
            Assert.InRange(Angle(SolarSystem.SunDirectionFromMoon(t), sun), 0, 1e-5);
            parallaxSeen |= Math.Abs(ClockApart(SolarSystem.MoonLocalSeconds(OnTheMoon(t, SolarSystem.SunDirection(t)), t), Scene.DaySeconds / 2)) > 1.5;
            var axis = SolarSystem.MoonSpinAxis(t);
            Assert.Equal(Scene.DaySeconds / 2, SolarSystem.MoonLocalSeconds(OnTheMoon(t, sun), t), 1);
            Assert.InRange(ClockApart(SolarSystem.MoonLocalSeconds(OnTheMoon(t, -sun), t), 0), -1, 1);
            var noon = sun - axis * Vector3.Dot(sun, axis);
            Assert.Equal(Scene.DaySeconds * 0.75, SolarSystem.MoonLocalSeconds(OnTheMoon(t, Vector3.Cross(axis, noon)), t), 1);
            var clock = SolarSystem.MoonLocalSeconds(OnTheMoon(t, sun), t);
            Assert.InRange(clock, 0, Scene.DaySeconds);
        }
        Assert.True(parallaxSeen, "at none of these times does the sun from the moon read differently from the sun from the planet's centre");

        // Its day is its lap against the sun.
        var day = SolarSystem.MoonDaySeconds;
        Assert.InRange(day / (1 / (1 / SolarSystem.MoonLapSeconds - 1 / Year)) - 1, -1e-6, 1e-6);

        // A point fixed on the moon, carried with its spin (once a lap among the stars, about its
        // axis), reads the same clock a day later, but for the sun's swing: on the moon's tilted
        // equator the sun's way round is uneven over a year, by up to the swing its equator's tilt
        // from the planet's (the sun's path) gives, as the equation of time is on a tilted planet.
        // The two equators cross on the orbit's rising line, as the spin axis is tilted from the
        // orbit's normal about it.
        var tilt = Angle(SolarSystem.MoonSpinAxis(0), Planet.NorthPole);
        double Swing(double t) // how far the sun is round the moon's equator ahead of its way round the planet's
        {
            var l = FromTheNode(t);
            return Math.Atan2(Math.Cos(tilt) * Math.Sin(l), Math.Cos(l)) - l;
        }
        foreach (var t0 in new[] { 0, 777, Year / 3, Year * 0.6, 50 * Year + 99 })
        {
            var up = new Vector3(0.3f, 0.8f, -0.52f);
            var axis = Planet.TurnedDirection(SolarSystem.MoonSpinAxis(t0), t0); // among the stars
            var t1 = t0 + day;
            var carried = Vector3.Transform(Planet.TurnedDirection(up, t0), Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), (float)Wrap(Math.Tau * day / SolarSystem.MoonLapSeconds, Math.Tau)));
            var then = SolarSystem.MoonLocalSeconds(OnTheMoon(t0, up), t0);
            var later = SolarSystem.MoonLocalSeconds(OnTheMoon(t1, Planet.TurnedDirection(carried, -t1)), t1);
            var swung = ClockApart(0, (Swing(t1) - Swing(t0)) / Math.Tau * Scene.DaySeconds);
            Assert.InRange(ClockApart(later, then) - swung, -1, 1);
            Assert.InRange(Math.Abs(swung), 0, 30);
        }
    }

    /// <summary>World times through one of the moon's laps from <paramref name="start"/>, <paramref name="count"/> evenly spaced.</summary>
    private static IEnumerable<double> Lap(double start, int count)
    {
        for (var i = 0; i < count; i++)
            yield return start + i * SolarSystem.MoonLapSeconds / count;
    }

    /// <summary>The planet's centre's direction from the moon's centre, in the moon's own axes.</summary>
    private static Vector3 PlanetFromMoon(double t) =>
        Vector3.Normalize(SolarSystem.ToMoon(Planet.Centre, t));

    [Fact]
    public void The_moons_face_turns_to_the_planet_on_average_and_nods_by_its_tilt()
    {
        foreach (var start in new[] { 0, 1234, 100 * Year })
        {
            var (north, south, mean) = (0.0, 0.0, Vector3.Zero);
            foreach (var t in Lap(start, 64))
            {
                var planet = PlanetFromMoon(t);
                var (lat, _) = CubeSphere.LatLon(planet);
                var latitude = double.RadiansToDegrees(lat);
                Assert.InRange(latitude, -Scene.MoonSpinTiltDegrees - 0.5, Scene.MoonSpinTiltDegrees + 0.5);
                (north, south) = (Math.Max(north, latitude), Math.Min(south, latitude));
                mean += planet;
            }
            Assert.True(north > Scene.MoonSpinTiltDegrees - 1 && south < -(Scene.MoonSpinTiltDegrees - 1),
                $"from {start:0} s the planet nods only from {south:0.0}° to {north:0.0}°");
            Assert.InRange(double.RadiansToDegrees(Angle(mean, CubeSphere.FromLatLon(0, 0))), 0, 1);
        }
    }

    [Fact]
    public void The_moons_spin_axis_is_tilted_from_its_orbit_and_fixed_among_the_stars()
    {
        var first = SolarSystem.MoonSpinAxis(0);
        foreach (var t in new[] { 0, 333, Year / 3, 100 * Year, 200 * Year })
        {
            var axis = SolarSystem.MoonSpinAxis(t);
            Assert.Equal(Scene.MoonSpinTiltDegrees, double.RadiansToDegrees(Angle(axis, OrbitNormal(t))), 0.01);
            Assert.InRange(Angle(Planet.TurnedDirection(axis, t), first), 0, 1e-4);
            Assert.InRange(Angle(axis, Vector3.Transform(Planet.NorthPole, SolarSystem.MoonOrientation(t))), 0, 1e-5);
        }
    }

    /// <summary>The sine of the planet's centre's height from moon-fixed <paramref name="place"/>, through a lap from <paramref name="start"/>.</summary>
    private static IEnumerable<float> PlanetHeights(Vector3 place, double start) =>
        Lap(start, 256).Select(t => SolarSystem.PlanetHeightFromMoon(place, t));

    [Fact]
    public void From_the_north_pole_the_planet_rises_and_sets_wholly_once_a_lap()
    {
        var wholly = MathF.Sin(MathF.Asin(Scene.PlanetRadius / Scene.MoonDistance) + float.DegreesToRadians(1));
        foreach (var start in new[] { 0, 777, 100 * Year })
        {
            var heights = PlanetHeights(Planet.NorthPole, start).ToArray();
            Assert.True(heights.Max() > wholly, $"from {start:0} s it rises only to {heights.Max():0.000}, not {wholly:0.000}");
            Assert.True(heights.Min() < -wholly, $"from {start:0} s it sets only to {heights.Min():0.000}, not {-wholly:0.000}");
            var crossings = 0;
            for (var i = 0; i < heights.Length; i++)
                if (heights[i] > 0 != heights[(i + 1) % heights.Length] > 0)
                    crossings++;
            Assert.Equal(2, crossings);
        }
    }

    [Fact]
    public void From_the_middle_of_the_near_side_the_planet_never_sets()
    {
        foreach (var start in new[] { 0, 777, 100 * Year })
            Assert.All(PlanetHeights(CubeSphere.FromLatLon(0, 0), start), h => Assert.True(h > MathF.Sin(MathF.PI / 3), $"the planet is only {h:0.000} up"));
    }

    [Theory, InlineData(0.0), InlineData(200 * Year)]
    public void To_and_from_the_moon_are_inverse_even_in_a_long_played_world(double t)
    {
        Assert.Equal(1f, SolarSystem.MoonOrientation(t).Length(), 5);
        var random = new Random(99);
        for (var i = 0; i < 100; i++)
        {
            var d = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var p = OnTheMoon(t, d);
            Assert.InRange(Vector3.Distance(SolarSystem.FromMoon(SolarSystem.ToMoon(p, t), t), p), 0, 1e-3f);
            Assert.Equal(Scene.MoonRadius, SolarSystem.ToMoon(p, t).Length(), 1);
        }
    }

    [Fact]
    public void The_suns_disc_is_its_radius_over_its_distance()
    {
        Assert.Equal(MathF.Asin(89_000f / 3_000_000f), SolarSystem.SunAngularRadius, 6);
        Assert.Equal(1.7, double.RadiansToDegrees(SolarSystem.SunAngularRadius), 1);
    }

    [Fact]
    public void The_moons_pull_is_a_sixth_of_the_planets_at_its_surface()
    {
        Assert.Equal(Scene.SurfaceGravity / 6, SolarSystem.MoonMu / (Scene.MoonRadius * Scene.MoonRadius), 4);
    }

    [Fact]
    public void A_new_worlds_first_evening_at_Home_has_the_moon_up()
    {
        // From sunset (15:00) to midnight on day 1.
        var dusk = Sun.WorldTimeFor(Home, 900);
        Assert.InRange(dusk, 0, Scene.DaySeconds);
        for (var t = dusk; t <= dusk + Scene.DaySeconds / 4; t += 5)
        {
            var height = Vector3.Dot(Vector3.Normalize(SolarSystem.MoonCentre(t) - Home), Planet.Up(Home));
            Assert.True(height > 0.1f, $"at {Sun.Clock(Sun.LocalSeconds(GroundBody.Planet, Home, t))} the moon is down at Home ({height:0.00})");
        }
    }

    /// <summary>The moon's centre's angle from the planet's shadow's axis (the sun's opposite from the planet's centre) at <paramref name="t"/>.</summary>
    private static double FromTheShadowsAxis(double t) => Angle(SolarSystem.MoonCentre(t) - Planet.Centre, -SolarSystem.SunDirection(t));

    /// <summary>
    /// The time in the first sixty years at which the moon is deepest in the planet's shadow
    /// (stepped every 5 s): near its axis only once in a while, as the moon must cross its orbit's
    /// line just as the sun is on it. Worked out once for the tests that need it.
    /// </summary>
    private static readonly Lazy<double> Deepest = new(() =>
    {
        var (best, deepest) = (0.0, double.MaxValue);
        for (var t = 0.0; t < 60 * Year; t += 5)
            if (FromTheShadowsAxis(t) < deepest)
                (best, deepest) = (t, FromTheShadowsAxis(t));
        return best;
    });

    private static double DeepestInTheShadow() => Deepest.Value;

    /// <summary>The grazing ray's light through the whole of the planet's air (in and out again), from its tables.</summary>
    private static Vector3 Grazing()
    {
        var half = AirTables.Look(0, 0f, 0f);
        return half * half;
    }

    [Fact]
    public void Out_of_the_planets_shadow_the_moon_has_the_full_sun()
    {
        var t = 0.0;
        while (FromTheShadowsAxis(t) < double.DegreesToRadians(90))
            t += 10;
        var subSolar = SolarSystem.MoonCentre(t) + SolarSystem.SunDirectionFromMoon(t) * Scene.MoonRadius;
        Assert.Equal(Vector3.One, SolarSystem.PlanetShadowLight(subSolar, t));
    }

    [Fact]
    public void In_the_planets_shadow_the_moon_has_the_colour_through_the_air()
    {
        var t = DeepestInTheShadow();
        Assert.InRange(double.RadiansToDegrees(FromTheShadowsAxis(t)), 0, 3);
        var grazing = Grazing();
        foreach (var d in new[] { Vector3.Zero, Vector3.Normalize(Planet.Centre - SolarSystem.MoonCentre(t)) * Scene.MoonRadius })
        {
            var light = SolarSystem.PlanetShadowLight(SolarSystem.MoonCentre(t) + d, t);
            Assert.True(light.X < 0.1f && light.Y < 0.1f && light.Z < 0.1f, $"too bright in the shadow: {light}");
            Assert.True(light.Z > 0f && light.X > 2 * light.Z, $"not the grazing light's red: {light}");
            // The grazing light's colour, its share of it Scene.PlanetShadowGlow.
            Assert.Equal(grazing.X / grazing.Z, light.X / light.Z, 0.05 * grazing.X / grazing.Z);
            Assert.Equal(Scene.PlanetShadowGlow, light.X / grazing.X, 0.01);
        }
    }

    [Fact]
    public void The_planets_shadow_edge_on_the_moon_is_smooth()
    {
        // At the moon's distance, across the planet's shadow's edge: from three scale heights of the
        // air inside where the sun's whole disc has just gone behind the planet's edge (as seen from
        // the planet's centre: the glow there fading to the deep shadow's) to where it is wholly
        // clear of its air. Smooth is a gradient that holds as the steps shrink: a jump anywhere
        // would show as a steepest change a degree twice as great at half the step.
        var t = DeepestInTheShadow();
        var axis = -SolarSystem.SunDirection(t);
        var across = Vector3.Normalize(Vector3.Cross(axis, Planet.NorthPole));
        var (r, d) = (SolarSystem.SunAngularRadius, Scene.MoonDistance);
        var inside = Math.Asin(GroundBody.PlanetSeaRadius / d) - r - 3 * Scene.AtmosphereScaleHeight / d;
        var outside = Math.Asin((GroundBody.PlanetSeaRadius + Scene.AtmosphereHeight) / d) + r;
        Vector3 At(double angle) =>
            SolarSystem.PlanetShadowLight(Planet.Centre + (axis * (float)Math.Cos(angle) + across * (float)Math.Sin(angle)) * d, t);
        var (first, last) = (At(inside), At(outside));
        Assert.True(first.X < 0.2f, $"not in the shadow where it begins: {first}");
        Assert.True(last.Z > 0.95f, $"not out of it where it ends: {last}");
        double Steepest(int steps)
        {
            var (steepest, step) = (0.0, (outside - inside) / steps);
            for (var i = 0; i < steps; i++)
            {
                var change = Vector3.Abs(At(inside + step * (i + 1)) - At(inside + step * i));
                steepest = Math.Max(steepest, MathF.Max(change.X, MathF.Max(change.Y, change.Z)) / double.RadiansToDegrees(step));
            }
            return steepest;
        }
        var (coarse, fine) = (Steepest(200), Steepest(400));
        output.WriteLine($"the edge spans {double.RadiansToDegrees(outside - inside):0.000} °; its steepest change {coarse:0.0} a degree in 200 steps, {fine:0.0} in 400");
        Assert.True(fine < coarse * 1.25, $"steeper at the finer step ({fine:0.0} a degree against {coarse:0.0}): a jump in the edge");
        // And no steeper than the air's own: the whole change over a tenth of the span at the least.
        Assert.True(coarse * double.RadiansToDegrees(outside - inside) < 10, $"{coarse:0.0} a degree over a {double.RadiansToDegrees(outside - inside):0.000} ° edge");
    }

    [Fact]
    public void Planetshine_is_most_when_the_planets_lit_face_is_turned_to_the_moon()
    {
        // A lap in an eclipse season, so its full moon is opposite the sun and its new one in front of it.
        var full = DeepestInTheShadow();
        var (half, @new) = (full + SolarSystem.MoonLapSeconds / 4, full + SolarSystem.MoonLapSeconds / 2);
        float Shine(double t) => SolarSystem.Planetshine(SolarSystem.MoonCentre(t), t);
        Assert.True(Shine(@new) > Shine(half) && Shine(half) > Shine(full), $"new {Shine(@new):0.0000}, half {Shine(half):0.0000}, full {Shine(full):0.0000}");
        Assert.InRange(Shine(full), 0, 0.01f * Scene.PlanetshineStrength);
        Assert.InRange(Shine(@new), 0.9f * Scene.PlanetshineStrength, Scene.PlanetshineStrength);
    }

    /// <summary>A place 100 m over the sea straight under the moon at <paramref name="t"/>: the moon overhead.</summary>
    private static Vector3 UnderTheMoon(double t) =>
        Planet.Centre + Vector3.Normalize(SolarSystem.MoonCentre(t) - Planet.Centre) * (GroundBody.PlanetSeaRadius + 100);

    /// <summary>
    /// A full moon clear of the planet's shadow: a twelfth of the moon's day after the
    /// deepest eclipse (30 degrees on, well past the shadow's 15 and its air), so wholly lit.
    /// </summary>
    private static double FullClearOfTheShadow() => DeepestInTheShadow() + SolarSystem.MoonDaySeconds / 12;

    [Fact]
    public void A_full_moon_lights_the_night_more_than_a_crescent_and_a_new_moon_none()
    {
        // A lap in an eclipse season, so its new moon is in front of the sun (its near side all dark).
        var deepest = DeepestInTheShadow();
        var full = FullClearOfTheShadow();
        var crescent = deepest + SolarSystem.MoonDaySeconds * 3 / 8;
        Assert.Equal(Vector3.One, SolarSystem.PlanetShadowLight(SolarSystem.MoonCentre(full), full));
        float Light(double t) => SolarSystem.Moonlight(UnderTheMoon(t), t).Y;
        // The new moon: the least light within a minute of half the moon's day on.
        var @new = deepest + SolarSystem.MoonDaySeconds / 2;
        for (var t = @new - 60; t <= @new + 60; t += 1)
            if (Light(t) < Light(@new))
                @new = t;
        Assert.True(Light(full) > 2 * Light(crescent) && Light(crescent) > 0, $"full {Light(full):0.000000}, crescent {Light(crescent):0.000000}");
        Assert.InRange(Light(@new), 0, 0.01f * Light(full));
        // A full moon overhead: its whole lit face, through the air straight up, at Scene's strength.
        var overhead = AirTables.Look(0, 100, 1f).Y * Scene.MoonAlbedo * Scene.MoonlightStrength;
        Assert.InRange(Light(full), 0.9f * overhead, overhead);
    }

    [Fact]
    public void The_moon_below_the_horizon_lights_nothing()
    {
        var full = FullClearOfTheShadow();
        var antipode = 2 * Planet.Centre - UnderTheMoon(full);
        Assert.Equal(Vector3.Zero, SolarSystem.Moonlight(antipode, full));
        // Set at Home by more than the sea's horizon dips there (about 5 degrees from its 35 m).
        var set = FirstTime(Home, up: false, margin: 10);
        Assert.Equal(Vector3.Zero, SolarSystem.Moonlight(Home, set));
    }

    [Fact]
    public void Moonlight_is_dim_beside_the_day()
    {
        // The full moon overhead beside the sun overhead at the same place, in each colour.
        var full = FullClearOfTheShadow();
        var moon = SolarSystem.Moonlight(UnderTheMoon(full), full);
        var noon = AirTables.Look(0, 100, 1f);
        Assert.True(moon.X > 0 && moon.Y > 0 && moon.Z > 0, $"no moonlight: {moon}");
        Assert.True(moon.X < 0.02f * noon.X && moon.Y < 0.02f * noon.Y && moon.Z < 0.02f * noon.Z, $"moonlight {moon} beside the noon sun's {noon}");
    }

    [Fact]
    public void In_a_total_lunar_eclipse_the_moonlight_all_but_goes()
    {
        // Deep in the planet's shadow the moon has only the air's grazing light (red, a tenth of
        // it: Scene.PlanetShadowGlow), and gives back no more: its light the full moon's clear of
        // the shadow, dimmed and reddened as the moon is (green, the eye's most, a few hundredths).
        var (deepest, full) = (DeepestInTheShadow(), FullClearOfTheShadow());
        var clear = SolarSystem.Moonlight(UnderTheMoon(full), full);
        var eclipsed = SolarSystem.Moonlight(UnderTheMoon(deepest), deepest);
        var glow = Scene.PlanetShadowGlow;
        Assert.True(eclipsed.X < glow * clear.X && eclipsed.Y < 0.05f * clear.Y && eclipsed.Z < glow * clear.Z, $"eclipsed {eclipsed} against {clear}");
        Assert.True(eclipsed.X > 2 * eclipsed.Z, $"not the shadow's red: {eclipsed}");
        var shadow = SolarSystem.PlanetShadowLight(SolarSystem.MoonCentre(deepest), deepest);
        Assert.Equal(shadow * (Scene.MoonAlbedo * Scene.MoonlightStrength), SolarSystem.MoonShine(deepest));
    }

    [Fact]
    public void The_bodies_allocate_nothing()
    {
        var p = OnTheMoon(100, Vector3.UnitY);
        var sum = 0.0;
        void Ask(int i)
        {
            var t = i * 37.0;
            sum += SolarSystem.SunDirection(t).X + SolarSystem.MoonCentre(t).Y + SolarSystem.MoonSpinAxis(t).Z
                   + SolarSystem.MoonLocalSeconds(p, t) + SolarSystem.SunAngularRadius + SolarSystem.SunMu
                   + SolarSystem.MoonLapSeconds + SolarSystem.MoonDaySeconds + SolarSystem.MoonMu + Sun.Direction(GroundBody.Planet, t).X + SolarSystem.SunDirectionFromMoon(t).Y
                   + SolarSystem.MoonOrientation(t).W + SolarSystem.ToMoon(p, t).X + SolarSystem.FromMoon(Vector3.UnitY, t).Z
                   + SolarSystem.PlanetHeightFromMoon(Vector3.UnitY, t)
                   + SolarSystem.PlanetShadowLight(p, t).X + SolarSystem.Planetshine(p, t)
                   + SolarSystem.Moonlight(Home, t).Y + SolarSystem.MoonShine(t).Z;
        }
        for (var i = 0; i < 100; i++)
            Ask(i);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            Ask(i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.False(double.IsNaN(sum));
    }

    /// <summary>The moon's centre's angle above the horizon at <paramref name="p"/> (by <see cref="Planet.Up"/> there), in degrees.</summary>
    private static double MoonHeight(Vector3 p, double t) =>
        double.RadiansToDegrees(Math.Asin(Vector3.Dot(Planet.Up(p), Vector3.Normalize(SolarSystem.MoonCentre(t) - p))));

    /// <summary>The first world time, stepping from 0, at which the moon's centre is up (or down) at <paramref name="p"/> by more than <paramref name="margin"/> degrees.</summary>
    private static double FirstTime(Vector3 p, bool up, double margin)
    {
        for (var t = 0.0; t < 10 * Scene.DaySeconds; t += 10)
            if (up ? MoonHeight(p, t) > margin : MoonHeight(p, t) < -margin)
                return t;
        throw new InvalidOperationException("the moon never got there");
    }

    /// <summary>A reading's number (the readout's first figure, in the readout's culture).</summary>
    private static double Number(string reading) => double.Parse(reading.Split(' ')[0], System.Globalization.CultureInfo.CurrentCulture);

    [Fact]
    public void The_readout_says_where_the_moon_is()
    {
        using (var sim = new Simulation(world: new WorldState { Time = FirstTime(Home, up: true, margin: 10) }))
        {
            var flight = sim.Flight;
            var readings = Readout.FlightReadings(flight);
            var t = flight.WorldTime;
            var distance = Vector3.Distance(flight.Position, SolarSystem.MoonCentre(t)) - Scene.MoonRadius;
            Assert.Equal($"{distance:0} m", readings["Moon"]);
            var height = Number(readings["Moon height"]);
            Assert.True(height > 0, $"the moon is up, but reads {readings["Moon height"]}");
            Assert.InRange(height, MoonHeight(flight.Position, t) - 1, MoonHeight(flight.Position, t) + 1);
        }
        using (var sim = new Simulation(world: new WorldState { Time = FirstTime(Home, up: false, margin: 10) }))
        {
            var reading = Readout.FlightReadings(sim.Flight)["Moon height"];
            Assert.True(Number(reading) < 0, $"the moon is down, but reads {reading}");
        }
    }

    [Fact]
    public void Filling_the_frame_allocates_nothing()
    {
        // GameView's fill of the sky's bodies (the sun, the stars' turn and the moon) for a frame,
        // as the game does it each frame: GameView itself needs a window, so its fill is asked directly.
        var input = new GameOne.Rendering.FrameInput();
        GameView.FillSky(ref input, 1000); // warm-up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 0; f < 100; f++)
            GameView.FillSky(ref input, 1000 + f * (double)Simulation.FrameSeconds);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        // And what it filled is the bodies' at that time.
        var t = 1000 + 99 * (double)Simulation.FrameSeconds;
        Assert.Equal(t, input.WorldTime);
        Assert.Equal(SolarSystem.MoonOrientation(t), input.MoonOrientation);
        Assert.Equal(Scene.MoonRadius, input.MoonRadius);
        Assert.Equal(Sun.Direction(GroundBody.Planet, t), input.SunDirection);
        Assert.Equal(Planet.TurnAngle(t), input.StarTurn);
        Assert.Equal(SolarSystem.MoonShine(t), input.MoonShine);
    }
}
