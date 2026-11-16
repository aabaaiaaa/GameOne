using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The sun eclipsed by the moon (<see cref="SolarSystem.SunSeen(Vector3, double)"/>), and by any
/// body over it (<see cref="Sun.Seen(IReadOnlyList{GroundBody}, GroundBody, Vector3, double)"/>): a total
/// eclipse comes somewhere on the planet in its season, and there the sun is gone and the lights
/// come on by day; outside the moon's shadow nothing changes; across the shadow's edge the light
/// falls smoothly and the lights switch once each way; the flare dims with the sun covered.
/// </summary>
public sealed class EclipseTests(ITestOutputHelper output)
{
    private const double Year = Scene.YearDays * (double)Scene.DaySeconds;

    /// <summary>The moon's orbit's normal at <paramref name="t"/>, in the ground's frame.</summary>
    private static Vector3 OrbitNormal(double t) =>
        Vector3.Normalize(Vector3.Cross(SolarSystem.MoonCentre(t) - Planet.Centre, SolarSystem.MoonCentre(t + 1) - Planet.Centre));

    /// <summary>The sun's angle from the moon's rising line, round the north pole, in radians (as SolarSystemTests finds the seasons).</summary>
    private static double FromTheNode(double t)
    {
        var pole = Planet.NorthPole;
        var node = Vector3.Normalize(Vector3.Cross(pole, OrbitNormal(t)));
        var sun = SolarSystem.SunDirection(t);
        return Math.Atan2(Vector3.Dot(Vector3.Cross(node, sun), pole), Vector3.Dot(node, sun));
    }

    /// <summary>
    /// The sine of the eclipse season's half-width round the line (as Part 1's eclipse test has it):
    /// the moon within the planet's and its own width (and the sun's) of the sun's direction is
    /// possible only with the sun this near the line.
    /// </summary>
    private static readonly double SeasonSine =
        Math.Sin(Math.Asin((Planet.Radius + Scene.MoonRadius) / Scene.MoonDistance) + SolarSystem.SunAngularRadius) / Math.Sin(double.DegreesToRadians(Scene.MoonTiltDegrees));

    private static bool InSeason(double t, double margin = 1e-4) => Math.Abs(Math.Sin(FromTheNode(t))) <= SeasonSine + margin;

    /// <summary>
    /// Where the line from the sun's centre through the moon's meets the planet's ground at
    /// <paramref name="t"/> (the middle of the moon's shadow), if it does on the sun's side.
    /// </summary>
    private static bool ShadowMiddle(double t, out Vector3 ground)
    {
        ground = default;
        var o = SolarSystem.MoonCentre(t) - Planet.Centre;
        var d = -SolarSystem.SunDirectionFromMoon(t);
        var b = Vector3.Dot(o, d);
        var disc = b * b - (o.LengthSquared() - Planet.Radius * Planet.Radius);
        if (disc <= 0 || b >= 0)
            return false;
        var up = Vector3.Normalize(o + d * (-b - MathF.Sqrt(disc)));
        ground = OnGround(up);
        return true;
    }

    private static Vector3 OnGround(Vector3 up) => Planet.Centre + up * (Planet.SeaRadius + Terrain.SurfaceHeight(up));

    /// <summary>The first time in the first year the moon's shadow's middle is on the ground with the sun high there, and the place.</summary>
    private static (double Time, Vector3 Place) FirstTotality()
    {
        for (var t = 0.0; t < Year; t += 5)
            if (ShadowMiddle(t, out var p) && Sun.Height(GroundBody.Planet, p, t) > 0.8f && SolarSystem.SunSeen(p, t) == 0f)
                return (t, p);
        throw new InvalidOperationException("no total eclipse with the sun high in the first year");
    }

    [Fact]
    public void Home_sees_a_total_eclipse_by_day_in_the_first_year()
    {
        // The first second of the first year with Home's pad wholly in the moon's shadow with the
        // sun well up, a while into the world (so the moon is in its sky first); and how long it lasts.
        var home = LaunchSites.Home.Pad;
        var start = -1.0;
        for (var t = 0.0; t < Year && start < 0; t += 1)
            if (Sun.Height(GroundBody.Planet, home, t) > 0.2f && SolarSystem.SunSeen(home, t) == 0f)
                start = t;
        Assert.True(start >= 0, "Home sees no total eclipse by day in its first year");
        var end = start;
        while (SolarSystem.SunSeen(home, end + 1) == 0f)
            end += 1;
        output.WriteLine($"totality at Home from {start:0} s to {end:0} s (day {Math.Floor((start + Scene.ClockStart) / Scene.DaySeconds) + 1}, {Sun.Clock(Sun.LocalSeconds(GroundBody.Planet, home, start))} local), the sun {Sun.Height(GroundBody.Planet, home, start):0.00} high");
        Assert.True(start > Scene.DaySeconds / 2, $"Home's eclipse comes at {start:0} s, in the world's first minutes");
        Assert.True(end - start >= 10, $"Home's totality lasts only {end - start + 1:0} s");
        Assert.True(InSeason(start), "Home's eclipse out of its season");
    }

    [Fact]
    public void A_total_eclipse_comes_in_its_season_somewhere_on_the_planet()
    {
        var found = 0;
        for (var t = 0.0; t < Year; t += 5)
        {
            if (!ShadowMiddle(t, out var p) || Sun.Height(GroundBody.Planet, p, t) <= 0f)
                continue;
            Assert.Equal(0f, SolarSystem.SunSeen(p, t));
            Assert.True(InSeason(t), $"a total eclipse at {t:0} s with the sun {double.RadiansToDegrees(FromTheNode(t)):0.0}° from the line, out of its season");
            found++;
        }
        Assert.True(found > 0, "no total eclipse by day in the first year");
    }

    [Fact]
    public void In_totality_the_sun_is_gone_and_the_pad_lights_come_on()
    {
        var (t, p) = FirstTotality();
        Assert.Equal(0f, SolarSystem.SunSeen(p, t));
        Assert.True(Sun.Height(GroundBody.Planet, p, t) > 0.8f, "the sun not high");
        Assert.False(Sun.IsDark(Sun.Height(GroundBody.Planet, p, t), wasDark: false), "dark by the sun's height alone");
        Assert.True(Sun.IsDark(GroundBody.Planet, p, t, wasDark: false), "not dark in totality");
        Assert.True(Sun.IsDark(GroundBody.Planet, p, t, wasDark: true));

        // A place with lights the shadow crosses by day: its lights come on as it passes and go off
        // again after, stepped a frame at a time as the flight steps them.
        var places = PadLights.All;
        var (at, place) = (-1.0, -1);
        for (var u = 0.0; u < Year && place < 0; u += 1)
            for (var i = 0; i < places.Count && place < 0; i++)
                if (Sun.Height(GroundBody.Planet, places[i].Origin, u) > 0.5f && SolarSystem.SunSeen(places[i].Origin, u) == 0f)
                    (at, place) = (u, i);
        Assert.True(place >= 0, "no lit place in a total eclipse by day in the first year");

        var lights = new PlaceLights();
        lights.Reset(at - 120);
        Assert.False(lights.IsDark(place), $"{places[place].Name} dark before the eclipse");
        Assert.Equal(0f, lights.Lit[place]);
        var (on, off) = (false, false);
        const float frame = 1f / 60;
        for (var u = at - 120; u < at + 120; u += frame)
        {
            lights.Step(u, frame);
            on |= lights.IsDark(place) && lights.Lit[place] == 1f;
            off |= on && !lights.IsDark(place);
        }
        Assert.True(on, $"{places[place].Name}'s lights never came fully on in totality at {at:0} s");
        Assert.True(off, $"{places[place].Name}'s lights never went off after it");
    }

    [Fact]
    public void The_sun_seen_past_every_body_is_the_moons_eclipse_on_the_planet()
    {
        // On the planet only the moon can cover it: past every body (Sun.Seen) the share is the
        // moon's own (SolarSystem.SunSeen), in totality, round its shadow and far from it.
        var (t, middle) = FirstTotality();
        Assert.Equal(0f, Sun.Seen(GroundBody.Planet, middle, t));
        var random = new Random(9);
        var partial = 0;
        foreach (var u in new[] { t - 60, t - 20, t, t + 20, t + 60, 1_000.0, Year / 3 })
            for (var i = 0; i < 200; i++)
            {
                var up = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
                var near = Vector3.Normalize(middle - Planet.Centre + up * (Planet.Radius * 0.3f));
                var p = OnGround(i % 2 == 0 ? up : near);
                var moon = SolarSystem.SunSeen(p, u);
                partial += moon is > 0f and < 1f ? 1 : 0;
                Assert.Equal(moon, Sun.Seen(GroundBody.Planet, p, u), 1e-4f);
            }
        Assert.True(partial > 0, "no place in the moon's partial shadow among those tried");
    }

    [Fact]
    public void From_the_moon_the_planet_covers_the_sun()
    {
        // A place in the moon's frame behind the planet from the sun (three of its radii from its
        // centre, on the line away from the sun): the planet, a body with air, lets past only the
        // light its shadow is drawn with (the air's grazing light), under the dark's share, and it
        // is dark there; on the sun's side, the whole sun is seen. Nothing names the planet as the
        // one that covers it.
        const double t = 1_000;
        var moon = GroundBody.Moon;
        var sun = (Vector3Double)SolarSystem.SunDirection(t);
        var toMoon = Bodies.Change(GroundBody.Planet, moon, t);
        var behind = toMoon.Point((Vector3Double)Planet.Centre - sun * (3.0 * Planet.Radius)).ToSingle();
        var before = toMoon.Point((Vector3Double)Planet.Centre + sun * (3.0 * Planet.Radius)).ToSingle();
        var drawn = Air.Luminance(SolarSystem.PlanetShadowLight(((Vector3Double)Planet.Centre - sun * (3.0 * Planet.Radius)).ToSingle(), t));
        Assert.Equal(drawn, Sun.Seen(moon, behind, t), 1e-5f);
        Assert.InRange(Sun.Seen(moon, behind, t), 0.001f, Scene.EclipseDarkSeen);
        Assert.Equal(1f, Sun.Seen(moon, before, t));
        Assert.True(Sun.IsDark(moon, behind, t, wasDark: false), "not dark in the planet's shadow");
    }

    [Fact]
    public void A_lander_on_the_moon_has_its_lights_on_as_the_ground_is_drawn_dark_by_the_planets_shadow()
    {
        // Under the sun on the moon's ground (the sun overhead there), through the moon's going
        // into the planet's shadow and out of it: dark (the lights on) exactly when the light the
        // ground is drawn with (PlanetShadowLight, the shader's twin) is under the dark's share, held
        // until it is over the light's; so through the air's edge too, not only past the solid limb.
        var moon = GroundBody.Moon;
        var entering = -1.0;
        for (var t = 0.0; t < Year && entering < 0; t += 5)
            if (Air.Luminance(SolarSystem.PlanetShadowLight(SolarSystem.MoonCentre(t), t)) < 0.5f)
                entering = t;
        Assert.True(entering >= 0, "no lunar eclipse in the first year");
        var (dark, light, between, wasDark) = (0, 0, 0, false);
        for (var t = entering - 30; t < entering + 600; t += 0.25)
        {
            var up = Sun.Direction(moon, t);
            var p = moon.Centre + up * (moon.SeaRadius + moon.Height(up));
            var drawn = Air.Luminance(SolarSystem.PlanetShadowLight(Bodies.Change(moon, GroundBody.Planet, t).Point(p).ToSingle(), t));
            var threshold = wasDark ? Scene.EclipseLightSeen : Scene.EclipseDarkSeen;
            var isDark = Sun.IsDark(moon, p, t, wasDark);
            if (MathF.Abs(drawn - threshold) > 1e-4f)
                Assert.True(isDark == drawn < threshold, $"at {t:0.00} s the ground drawn at {drawn:0.0000} of the sun but {(isDark ? "dark" : "light")}");
            (dark, light) = (dark + (isDark ? 1 : 0), light + (isDark ? 0 : 1));
            between += drawn is > 0.03f and < 0.97f ? 1 : 0;
            wasDark = isDark;
        }
        output.WriteLine($"from {entering - 30:0} s: {dark} dark, {light} light, {between} in the shadow's edge");
        Assert.True(dark > 0 && light > 0 && between > 0, "the shadow's edge not crossed");
    }

    [Fact]
    public void The_sun_seen_passing_over_bodies_far_off_its_way_is_the_full_sum()
    {
        // Sun.Seen passes over a body clear of the sun's way, and stops once nothing is seen: the
        // same answers as asking every body in full (here, the share each leaves worked out in the
        // test), from the planet and the moon, near and far from the shadows, and with a test
        // list's second moon too.
        var random = new Random(11);
        var (t0, middle) = FirstTotality();
        var moon = GroundBody.Moon;
        var second = GroundBody.Circling("Second moon", "the second moon", 2, GroundBody.Planet, Scene.MoonDistance, Scene.MoonRadius,
            SolarSystem.MoonMu / 2, t => new BodyPose(-(Vector3Double)SolarSystem.MoonCentre(t) + (Vector3Double)Planet.Centre * 2.0, QuaternionDouble.Identity, default, default));
        foreach (IReadOnlyList<GroundBody> bodies in new IReadOnlyList<GroundBody>[] { Bodies.All, [GroundBody.Planet, moon, second] })
            foreach (var t in new[] { t0, t0 + 30, 1_000.0, Year / 3, Year / 2 + 77 })
                for (var i = 0; i < 300; i++)
                {
                    var d = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
                    var onMoon = i % 2 == 1;
                    var body = onMoon ? moon : GroundBody.Planet;
                    var p = onMoon
                        ? moon.Centre + d * (moon.SeaRadius + 50f + 3_000f * random.NextSingle())
                        : i % 4 == 0 ? OnGround(Vector3.Normalize(middle - Planet.Centre + d * 2_000f)) : OnGround(d);
                    Assert.Equal(Full(bodies, body, p, t), Sun.Seen(bodies, body, p, t), 1e-5f);
                }
    }

    /// <summary>The share of the sun seen past every other body, each asked in full (no body passed over), as <see cref="Sun.Seen(IReadOnlyList{GroundBody}, GroundBody, Vector3, double)"/> has it.</summary>
    private static float Full(IReadOnlyList<GroundBody> bodies, GroundBody body, Vector3 p, double t)
    {
        var root = GroundBody.Planet;
        var at = Bodies.Change(body, root, t).Point(p) - (Vector3Double)root.Centre;
        var sun = Vector3Double.Normalize((Vector3Double)SolarSystem.SunDirection(t) * Scene.SunDistance - at);
        var seen = 1f;
        foreach (var cover in bodies)
        {
            if (cover == body)
                continue;
            var toCover = Bodies.Change(cover, root, t).Point(cover.Centre) - (Vector3Double)root.Centre - at;
            if (cover.HasAir)
            {
                seen = MathF.Min(seen, Math.Clamp(Air.Luminance(SolarSystem.ShadowLight(cover, (-toCover).ToSingle(), sun.ToSingle())), 0f, 1f));
                continue;
            }
            var along = Vector3Double.Dot(sun, toCover);
            if (along <= 0)
                continue;
            var radius = MathF.Asin(MathF.Min((float)(cover.Radius / toCover.Length()), 1f));
            var apart = (float)Math.Atan2(Vector3Double.Cross(sun, toCover).Length(), along);
            seen = MathF.Min(seen, SolarSystem.DiscSeen(apart, SolarSystem.SunAngularRadius, radius));
        }
        return seen;
    }

    [Fact]
    public void Any_body_in_the_list_between_the_sun_and_a_place_eclipses_it()
    {
        // A second moon (a test list's) 10 km from a place on the planet towards the sun, the moon
        // far from it: the second moon covers the sun from there, the game's list leaves it whole.
        const double t = 1_000;
        var sun = SolarSystem.SunDirection(t);
        var p = OnGround(sun);
        Assert.Equal(1f, SolarSystem.SunSeen(p, t));
        var towards = Vector3Double.Normalize((Vector3Double)sun * Scene.SunDistance - ((Vector3Double)p - Planet.Centre));
        var centre = (Vector3Double)p + towards * 10_000.0;
        var second = GroundBody.Circling("Second moon", "the second moon", 2, GroundBody.Planet, Scene.MoonDistance, Scene.MoonRadius,
            SolarSystem.MoonMu / 2, _ => new BodyPose(centre, QuaternionDouble.Identity, default, default));
        IReadOnlyList<GroundBody> bodies = [GroundBody.Planet, GroundBody.Moon, second];
        Assert.Equal(0f, Sun.Seen(bodies, GroundBody.Planet, p, t));
        Assert.Equal(1f, Sun.Seen(GroundBody.Planet, p, t));
        // From a place some 27 degrees round the planet from it, the sun is whole again.
        var side = Vector3.Normalize(Vector3.Cross(sun, Planet.NorthPole));
        var aside = OnGround(Vector3.Normalize(sun + side * (2f * 2.5f / 10f)));
        Assert.Equal(1f, Sun.Seen(bodies, GroundBody.Planet, aside, t));
    }

    [Fact]
    public void Outside_the_moons_shadow_nothing_changes()
    {
        var random = new Random(5);
        var tried = 0;
        while (tried < 500)
        {
            var t = random.NextDouble() * 3 * Year;
            if (InSeason(t, margin: 0.02))
                continue;
            var up = Vector3.Normalize(new Vector3(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1));
            var p = OnGround(up);
            Assert.Equal(1f, SolarSystem.SunSeen(p, t));
            foreach (var wasDark in new[] { false, true })
                Assert.Equal(Sun.IsDark(Sun.Height(GroundBody.Planet, p, t), wasDark), Sun.IsDark(GroundBody.Planet, p, t, wasDark));
            tried++;
        }
    }

    /// <summary>
    /// A place <paramref name="distance"/> metres along the ground from <paramref name="middle"/>,
    /// the way <paramref name="axis"/> turns it, at the middle's own height (a smooth sphere, so
    /// the ground's bumps don't jog the light along the way).
    /// </summary>
    private static Vector3 Along(Vector3 middle, Vector3 axis, float distance)
    {
        var o = middle - Planet.Centre;
        return Planet.Centre + Vector3.Transform(o, Quaternion.CreateFromAxisAngle(axis, distance / o.Length()));
    }

    /// <summary>How far along the way from the middle the sun seen first passes <paramref name="share"/>, by halving.</summary>
    private static float Reach(double t, Vector3 middle, Vector3 axis, float share)
    {
        var (near, far) = (0f, 10_000f);
        for (var i = 0; i < 40; i++)
        {
            var mid = 0.5f * (near + far);
            if (SolarSystem.SunSeen(Along(middle, axis, mid), t) > share)
                far = mid;
            else
                near = mid;
        }
        return far;
    }

    [Fact]
    public void At_the_edge_of_the_shadow_the_light_falls_smoothly_and_the_lights_dont_flicker()
    {
        var (t, middle) = FirstTotality();
        var axis = Vector3.Normalize(Vector3.Cross(Planet.Up(middle), Planet.NorthPole));
        var (inner, outer) = (Reach(t, middle, axis, 0f), Reach(t, middle, axis, 0.9999f));
        var margin = 0.05f * (outer - inner);
        var (from, to) = (inner - margin, outer + margin);
        Vector3 Step(int i) => Along(middle, axis, from + (to - from) * i / 100f);

        // Out from the shadow's middle across the penumbra.
        var seen = new float[101];
        for (var i = 0; i <= 100; i++)
            seen[i] = SolarSystem.SunSeen(Step(i), t);
        Assert.Equal(0f, seen[0]);
        Assert.Equal(1f, seen[100]);
        for (var i = 1; i <= 100; i++)
        {
            Assert.True(seen[i] >= seen[i - 1], $"the light falls back at step {i}: {seen[i - 1]} then {seen[i]}");
            Assert.True(seen[i] - seen[i - 1] <= 0.05f, $"a jump of {seen[i] - seen[i - 1]:0.000} at step {i}");
        }

        // The lights, their state carried: off once on the way out, on once on the way back in.
        var dark = Sun.IsDark(GroundBody.Planet, Step(0), t, wasDark: false);
        Assert.True(dark);
        var changes = 0;
        for (var i = 1; i <= 100; i++)
        {
            var now = Sun.IsDark(GroundBody.Planet, Step(i), t, dark);
            changes += now != dark ? 1 : 0;
            dark = now;
        }
        Assert.False(dark);
        Assert.Equal(1, changes);
        changes = 0;
        for (var i = 99; i >= 0; i--)
        {
            var now = Sun.IsDark(GroundBody.Planet, Step(i), t, dark);
            changes += now != dark ? 1 : 0;
            dark = now;
        }
        Assert.True(dark);
        Assert.Equal(1, changes);

        // Between the two thresholds the state holds: dark if it was, light if it was.
        var between = Along(middle, axis, Reach(t, middle, axis, 0.5f * (Scene.EclipseDarkSeen + Scene.EclipseLightSeen)));
        Assert.InRange(SolarSystem.SunSeen(between, t), Scene.EclipseDarkSeen, Scene.EclipseLightSeen);
        Assert.True(Sun.IsDark(GroundBody.Planet, between, t, wasDark: true), "the lights go off before the sun is past the light threshold");
        Assert.False(Sun.IsDark(GroundBody.Planet, between, t, wasDark: false), "the lights come on before the sun is under the dark threshold");
    }

    [Fact]
    public void The_reject_speaks_only_where_the_whole_sun_is_seen()
    {
        // Near every new moon of two years: the places within the air nearest the shadow's axis
        // (the line from the sun's centre through the moon's, either way), and round them. Where
        // the once-a-time reject fires, the full answer there is the whole sun; asked through it,
        // the answer is the full one's to the bit; and both happen: times it fires, and times it
        // doesn't with the shadow on some of them.
        var top = GroundBody.PlanetSeaRadius + Scene.AtmosphereHeight;
        float[] radii = [GroundBody.PlanetSeaRadius, GroundBody.PlanetSeaRadius + 0.5f * Scene.AtmosphereHeight, top];
        var (fired, reached) = (0, 0);
        for (var t = 0.0; t < 2 * Year; t += 2)
        {
            var (sun, moon) = (SolarSystem.SunDirection(t), SolarSystem.MoonCentre(t));
            var toMoon = moon - Planet.Centre;
            if (Vector3.Dot(Vector3.Normalize(toMoon), sun) < 0.75f)
                continue; // far from a new moon
            var axis = SolarSystem.SunDirectionFromMoon(t);
            var nearest = moon + axis * Vector3.Dot(Planet.Centre - moon, axis) - Planet.Centre;
            if (nearest.LengthSquared() < 1f)
                continue;
            var n = Vector3.Normalize(nearest);
            var (a, b) = (Vector3.Normalize(Vector3.Cross(n, axis)), Vector3.Normalize(Vector3.Cross(n, Vector3.Cross(n, axis))));
            var reject = SolarSystem.NoShadowWithinTheAir(t);
            var any = false;
            foreach (var r in radii)
                foreach (var (da, db) in new[] { (0f, 0f), (0.03f, 0f), (-0.03f, 0f), (0f, 0.03f), (0f, -0.03f), (0.1f, 0f), (-0.1f, 0f), (0f, 0.1f), (0f, -0.1f) })
                {
                    var p = Planet.Centre + Vector3.Normalize(n + a * da + b * db) * r;
                    var full = SolarSystem.SunSeen(p, sun, moon);
                    Assert.Equal(full, SolarSystem.SunSeen(p, t));
                    if (reject)
                        Assert.True(full == 1f, $"the reject fired at {t:0} s with {full} of the sun seen {r - GroundBody.PlanetSeaRadius:0} m up");
                    any |= full < 1f;
                }
            fired += reject ? 1 : 0;
            reached += !reject && any ? 1 : 0;
        }
        Assert.True(fired > 0 && reached > 0, $"fired {fired}, reached {reached}");

        // Well out of the seasons (the sun far from the moon's line), it fires every time.
        var random = new Random(7);
        for (var tried = 0; tried < 1000;)
        {
            var t = random.NextDouble() * 3 * Year;
            if (Math.Abs(Math.Sin(FromTheNode(t))) < 0.95)
                continue;
            Assert.True(SolarSystem.NoShadowWithinTheAir(t), $"no reject at {t:0} s, out of season");
            tried++;
        }
    }

    [Fact]
    public void The_flare_dims_with_the_sun_covered()
    {
        var (t, middle) = FirstTotality();
        var sun = SolarSystem.SunDirection(t);
        var moon = SolarSystem.MoonCentre(t);
        var camera = middle + Planet.Up(middle) * 2f;
        Assert.Equal(0f, Air.FlareDimming(camera, sun, SolarSystem.SunSeen(camera, sun, moon)));

        // Half covered (as the renderer asks it, from the frame's sun and moon): about half the flare.
        var axis = Vector3.Normalize(Vector3.Cross(Planet.Up(middle), Planet.NorthPole));
        var half = Along(middle, axis, Reach(t, middle, axis, 0.5f));
        var seen = SolarSystem.SunSeen(half, sun, moon);
        Assert.Equal(SolarSystem.SunSeen(half, t), seen);
        Assert.InRange(seen, 0.49f, 0.51f);
        var open = Air.FlareDimming(half, sun, 1f);
        Assert.True(open > 0.5f, $"the open sun's flare is dimmed to {open} by the air alone");
        Assert.InRange(Air.FlareDimming(half, sun, seen) / open, 0.49f, 0.51f);
    }
}
