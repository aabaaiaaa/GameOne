using System.Numerics;

namespace GameOne;

/// <summary>
/// The sun, from the world's clock (<see cref="Flight.WorldTime"/>): where it is in the sky, and
/// what time of day it is anywhere. A day (<see cref="Scene.DaySeconds"/>) is noon to noon; the
/// clock reads 00:00 at a place's local midnight, so noon is 10:00 (the clock's "hours" are the
/// day's minutes). Everything that is lit by the sun or says whether it is day asks this, at the
/// world's time, so nothing reads a sun of another moment.
/// </summary>
internal static class Sun
{
    /// <summary>Home's east and up: the plane of the equator through Home, that longitudes are measured in.</summary>
    private static readonly Vector3 HomeEast = Vector3.UnitX, HomeUp = Vector3.UnitY;

    /// <summary>The world's clock read from Home's midnight on its first day: its time plus <see cref="Scene.ClockStart"/>.</summary>
    private static double FromHomesMidnight(double worldTime) => worldTime + Scene.ClockStart;

    /// <summary>
    /// The unit direction towards the sun at <paramref name="worldTime"/> from <paramref name="body"/>'s
    /// centre, in its frame (its ground's, which turns with it), by its pose among the bodies: the
    /// sun's place (<see cref="SolarSystem.SunDirection"/>, from the planet's centre in the planet's
    /// ground's frame) less the body's centre's, turned into its frame (<see cref="Bodies.Change"/>).
    /// On the planet: round once a day westward over the ground (overhead at Home at its noon, under
    /// it at midnight, rising due east at 05:00), always over the equator, as the planet goes round
    /// the sun in its plane. A body that goes round nothing is the root of the bodies' frames, the
    /// sun's direction's own frame.
    /// </summary>
    public static Vector3 Direction(GroundBody body, double worldTime)
    {
        var sun = SolarSystem.SunDirection(worldTime);
        if (body.Parent is null)
            return sun;
        var root = body;
        while (root.Parent is { } parent)
            root = parent;
        var toRoot = Bodies.Change(body, root, worldTime);
        var from = toRoot.Point(body.Centre) - (Vector3Double)root.Centre; // the body's centre from the root's
        var towards = Vector3Double.Normalize((Vector3Double)sun * Scene.SunDistance - from);
        return toRoot.Inverse.Direction(towards).ToSingle();
    }

    /// <summary>
    /// <see cref="SolarSystem.SunDirection"/> for a planet tilted by <paramref name="tiltDegrees"/>, so a test
    /// can try one the planet doesn't have: the paths' direction taken north or south of the equator
    /// by the season (on the equator as each year starts, the tilt north a quarter on).
    /// </summary>
    internal static Vector3 Direction(double worldTime, float tiltDegrees)
    {
        var year = Scene.YearDays * (double)Scene.DaySeconds;
        var declination = Math.Asin(Math.Sin(double.DegreesToRadians(tiltDegrees)) * Math.Sin(Math.Tau * Wrap(FromHomesMidnight(worldTime), year) / year));
        return Vector3.Normalize(SolarSystem.SunDirection(worldTime) * (float)Math.Cos(declination) + GroundBody.Planet.NorthPole * (float)Math.Sin(declination));
    }

    /// <summary>
    /// Seconds into the local day at <paramref name="p"/> (in <paramref name="body"/>'s frame) at
    /// <paramref name="worldTime"/>, from 0 (midnight) to just under <see cref="Scene.DaySeconds"/>.
    /// At the root of the bodies' frames (the planet) it is the world's own clock, Home's, on by the
    /// part of a turn <paramref name="p"/> is east of Home (by its longitude, round the north pole).
    /// At any other body it is read as that clock is (noon at half the day) over the body's own day:
    /// by how far round its north <paramref name="p"/> is east of where the sun is overhead
    /// (<see cref="Direction(GroundBody, double)"/>).
    /// </summary>
    public static double LocalSeconds(GroundBody body, Vector3 p, double worldTime)
    {
        if (body.Parent is null)
            return Wrap(FromHomesMidnight(worldTime) + EastOfHome(p) / Math.Tau * Scene.DaySeconds, Scene.DaySeconds);
        var (axis, sun, up) = (body.NorthPole, Direction(body, worldTime), p - body.Centre);
        var (noon, at) = (sun - axis * Vector3.Dot(sun, axis), up - axis * Vector3.Dot(up, axis));
        var east = Math.Atan2(Vector3.Dot(Vector3.Cross(noon, at), axis), Vector3.Dot(noon, at));
        return Wrap(Scene.DaySeconds / 2 + east / Math.Tau * Scene.DaySeconds, Scene.DaySeconds);
    }

    /// <summary>
    /// The first world time from 0 at which <paramref name="p"/>'s local clock reads
    /// <paramref name="localSeconds"/> (always within the first day): to start a world at a time of
    /// day somewhere.
    /// </summary>
    public static double WorldTimeFor(Vector3 p, double localSeconds) =>
        Wrap(localSeconds - LocalSeconds(GroundBody.Planet, p, 0), Scene.DaySeconds);

    /// <summary>
    /// How high the sun is at <paramref name="p"/> (in <paramref name="body"/>'s frame) at
    /// <paramref name="worldTime"/>: 1 overhead, 0 on the horizon, below 0 under it, by the body's up
    /// there and the sun seen from it (<see cref="Direction(GroundBody, double)"/>).
    /// </summary>
    public static float Height(GroundBody body, Vector3 p, double worldTime) => Vector3.Dot(body.Up(p), Direction(body, worldTime));

    /// <summary>
    /// Whether it is dark at <paramref name="p"/> (in <paramref name="body"/>'s frame): the sun lower than <see cref="Scene.DuskSunHeight"/>
    /// once it has been light, not yet higher than <see cref="Scene.DawnSunHeight"/> once it has been
    /// dark (<paramref name="wasDark"/>), so dusk and dawn each switch once and the lights never flicker
    /// on a height between the two. Or another body over the sun (<see cref="Seen(GroundBody, Vector3, double)"/>:
    /// the moon from the planet, the planet from the moon): less of its disc seen than
    /// <see cref="Scene.EclipseDarkSeen"/> once it has been light, not yet more than
    /// <see cref="Scene.EclipseLightSeen"/> once it has been dark, held the same way, so the lights
    /// come on in a total eclipse as at dusk and don't flicker at the shadow's edge. (The bodies are
    /// asked only while the sun is up: below it, it is dark already; and a body's own ground covers
    /// only a sun already set, which its height has made dark, so it is never asked of itself.)
    /// </summary>
    public static bool IsDark(GroundBody body, Vector3 p, double worldTime, bool wasDark) =>
        IsDark(Height(body, p, worldTime), wasDark) || Eclipsed(body, p, worldTime, wasDark);

    /// <summary>Whether the bodies leave too little of the sun's disc seen from <paramref name="p"/> (in <paramref name="body"/>'s frame) for light, held as <see cref="IsDark(GroundBody, Vector3, double, bool)"/> says.</summary>
    private static bool Eclipsed(GroundBody body, Vector3 p, double worldTime, bool wasDark) =>
        Seen(body, p, worldTime) < (wasDark ? Scene.EclipseLightSeen : Scene.EclipseDarkSeen);

    /// <summary>
    /// The share of the sun's disc seen from <paramref name="p"/> (in <paramref name="body"/>'s
    /// frame) at <paramref name="worldTime"/>, past the game's bodies (<see cref="Bodies.All"/>):
    /// <see cref="Seen(IReadOnlyList{GroundBody}, GroundBody, Vector3, double)"/>.
    /// </summary>
    public static float Seen(GroundBody body, Vector3 p, double worldTime) => Seen(Bodies.All, body, p, worldTime);

    /// <summary>
    /// The share of the sun's disc seen from <paramref name="p"/> (in <paramref name="body"/>'s
    /// frame, one of <paramref name="bodies"/>) at <paramref name="worldTime"/> past every one of
    /// them but <paramref name="body"/> itself (whose ground covers only a sun set by its height): the least
    /// any one leaves, 1 with none over it. A body with no air covers it as a sphere of its
    /// <see cref="GroundBody.Radius"/> (<see cref="SolarSystem.DiscSeen"/>); one with air by the
    /// light it lets past, the brightness of <see cref="SolarSystem.ShadowLight"/> (what the moon's
    /// ground is drawn lit by in its shadow: dimmed and reddened through its air, its grazing
    /// light inside), so the dark comes where the ground is drawn dark. A body clear of the sun's
    /// way by its reach (its radius, or its air's top) is passed over with no angle worked out, and
    /// once nothing is seen the rest aren't asked. Worked out in the root's frame (the sun's
    /// direction's, <see cref="SolarSystem.SunDirection"/>), in double; the bodies' centres and
    /// each frame's change into the root's once a world time a thread (the map's search asks on
    /// its own; every list has one root, the planet), so a frame asking it of every place and
    /// vessel works them out once.
    /// </summary>
    public static float Seen(IReadOnlyList<GroundBody> bodies, GroundBody body, Vector3 p, double worldTime)
    {
        var root = body;
        while (root.Parent is { } parent)
            root = parent;
        Known(bodies, root, worldTime);
        // From the root's centre (no change asked of the root's own places).
        var at = (body == root ? (Vector3Double)p : _toRoot![body.Index].Point(p)) - (Vector3Double)root.Centre;
        // The sun's way from the place is within this sine of its way from the root's centre.
        var skew = at.Length() / (Scene.SunDistance - at.Length());
        // Read once: this thread's, not a field's each time.
        var centres = _centres!.AsSpan(0, _coverCount);
        var covers = _covers!.AsSpan(0, _coverCount);
        var sunFromRoot = _sun;
        Vector3Double? sunFromPlace = null;
        var seen = 1f;
        for (var i = 0; i < covers.Length && seen > 0f; i++)
        {
            var cover = covers[i];
            if (cover == body)
                continue;
            var toCover = centres[i] - at;
            // Passed over, by the sun's way from the root's centre (no square root but one): behind
            // the place, or clear of the sun's disc (its middle further off the sun's way than its
            // reach and the disc's own: were they to touch, the middle would be within reach +
            // distance times the sine of the sun's radius), each with the skew and a metre over.
            var squared = toCover.LengthSquared();
            var distance = Math.Sqrt(squared);
            var along0 = Vector3Double.Dot(sunFromRoot, toCover);
            if (along0 < -distance * skew)
                continue;
            var reach = cover.HasAir ? cover.SeaRadius + (double)Scene.AtmosphereHeight : cover.Radius;
            var clear = reach + distance * (SinSunRadius + skew) + 1.0;
            if (along0 > 0 && squared - along0 * along0 > clear * clear)
                continue;
            var sun = sunFromPlace ??= Vector3Double.Normalize(sunFromRoot * Scene.SunDistance - at);
            var along = Vector3Double.Dot(sun, toCover);
            if (along <= 0)
                continue; // behind the place, away from the sun
            var off = Vector3Double.Cross(sun, toCover).Length();
            var share = cover.HasAir
                ? Math.Clamp(Air.Luminance(SolarSystem.ShadowLight(cover, (-toCover).ToSingle(), sun.ToSingle())), 0f, 1f)
                : SolarSystem.DiscSeen((float)Math.Atan2(off, along), SolarSystem.SunAngularRadius,
                    MathF.Asin(MathF.Min((float)(cover.Radius / distance), 1f)));
            seen = MathF.Min(seen, share);
        }
        return seen;
    }

    private static readonly double SinSunRadius = Math.Sin(SolarSystem.SunAngularRadius);

    // The sun's direction, each body's centre from the root's and each frame's change into the
    // root's, at the last world time and list asked on this thread.
    [ThreadStatic] private static IReadOnlyList<GroundBody>? _knownFor;
    [ThreadStatic] private static double _knownAt;
    [ThreadStatic] private static Vector3Double _sun;
    [ThreadStatic] private static Vector3Double[]? _centres;
    [ThreadStatic] private static GroundBody[]? _covers; // the list's bodies, read straight in the loop over them
    [ThreadStatic] private static int _coverCount;
    [ThreadStatic] private static FrameChange[]? _toRoot;

    /// <summary>Works out what <see cref="Seen(IReadOnlyList{GroundBody}, GroundBody, Vector3, double)"/> reads, unless this thread has for <paramref name="bodies"/> at <paramref name="worldTime"/> already. Allocates only for a longer list than any before on the thread.</summary>
    private static void Known(IReadOnlyList<GroundBody> bodies, GroundBody root, double worldTime)
    {
        if (_knownFor == bodies && _knownAt == worldTime)
            return;
        var count = bodies.Count;
        var slots = MaxIndex(bodies) + 1;
        if (_centres is null || _centres.Length < count)
            (_centres, _covers) = (new Vector3Double[count], new GroundBody[count]);
        if (_toRoot is null || _toRoot.Length < slots)
            _toRoot = new FrameChange[slots];
        _sun = (Vector3Double)SolarSystem.SunDirection(worldTime);
        for (var i = 0; i < count; i++)
        {
            var change = Bodies.Change(bodies[i], root, worldTime);
            _toRoot[bodies[i].Index] = change;
            _centres[i] = change.Point(bodies[i].Centre) - (Vector3Double)root.Centre;
            _covers![i] = bodies[i];
        }
        (_knownFor, _knownAt, _coverCount) = (bodies, worldTime, count);
    }

    private static int MaxIndex(IReadOnlyList<GroundBody> bodies)
    {
        var most = 0;
        for (var i = 0; i < bodies.Count; i++)
            most = Math.Max(most, bodies[i].Index);
        return most;
    }

    /// <summary>Whether the sun at <paramref name="height"/> (its <see cref="Height"/>) leaves it dark by its height alone, as <see cref="IsDark(GroundBody, Vector3, double, bool)"/> does with no moon over it.</summary>
    public static bool IsDark(float height, bool wasDark) =>
        height < (wasDark ? Scene.DawnSunHeight : Scene.DuskSunHeight);

    /// <summary>A time of day as the clock shows it, <c>"07:42"</c>: the minutes and seconds into the day (wrapped to one), two digits each.</summary>
    public static string Clock(double seconds)
    {
        var whole = (int)Math.Floor(Wrap(seconds, Scene.DaySeconds));
        return $"{whole / 60:00}:{whole % 60:00}";
    }

    /// <summary>The world's own clock, <c>"Day 3, 07:42"</c>: Home's day (the first is day 1) and its time of day.</summary>
    public static string WorldClock(double worldTime)
    {
        var c = FromHomesMidnight(worldTime);
        return $"Day {(long)Math.Floor(c / Scene.DaySeconds) + 1}, {Clock(c)}";
    }

    /// <summary>How far round the north pole <paramref name="p"/> is east of Home, in radians (−π to π).</summary>
    private static double EastOfHome(Vector3 p)
    {
        var up = Planet.Up(p);
        return Math.Atan2(Vector3.Dot(up, HomeEast), Vector3.Dot(up, HomeUp));
    }

    /// <summary><paramref name="x"/> wrapped into [0, <paramref name="period"/>).</summary>
    internal static double Wrap(double x, double period)
    {
        var wrapped = x - Math.Floor(x / period) * period;
        return wrapped >= period ? 0 : wrapped; // (a tiny negative x rounds up to the period itself)
    }
}
