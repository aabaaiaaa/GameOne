using System.Numerics;

namespace GameOne;

/// <summary>
/// The sun, the planet and the moon as bodies on their paths, from the world's clock
/// (<see cref="Flight.WorldTime"/>). The sun is still, the system's centre; the planet goes round it
/// on a circle in the plane of its equator, the way it turns, once a year; the moon goes round the
/// planet on a circle tilted from the equator (<see cref="Scene.MoonTiltDegrees"/>), the same way,
/// its line across the equator fixed among the stars, keeping one face to the planet but for a nod
/// north and south, as its spin axis is tilted from its orbit's normal (<see cref="Scene.MoonSpinTiltDegrees"/>).
/// <para>
/// Among the stars is the frame the ground is in at world time 0 (as <see cref="Planet.Turned"/>
/// has it); the ground's frame (the physics') at a time is the stars' turned back by the planet's
/// turn since (<see cref="Planet.TurnAngle"/>). Every angle is worked out in double and wrapped to a
/// turn before it is a float, so a world played for a long time keeps the float's precision.
/// </para>
/// </summary>
internal static class SolarSystem
{
    /// <summary>The stars' frame's equator: Home's east at world time 0, and a quarter turn on from it round the pole (the planet's way).</summary>
    private static readonly Vector3 StarsX = Vector3.UnitX, StarsY = Vector3.Cross(GroundBody.Planet.NorthPole, Vector3.UnitX);

    /// <summary>
    /// The planet's lap round the sun among the stars, in seconds: the year. Worked out from the day
    /// and the planet's turn (<see cref="Planet.TurnSeconds"/>, a float, as the ground's frame turns
    /// by it), so the sun crosses the ground in exactly <see cref="Scene.DaySeconds"/> however long a
    /// world is played; it is <see cref="Scene.YearDays"/> days to within the float's rounding of the turn.
    /// </summary>
    private static readonly double YearSeconds = 1 / (1 / (double)GroundBody.PlanetTurnSeconds - 1 / (double)Scene.DaySeconds);

    /// <summary>The sun's gravitational parameter (m³/s²): what makes the planet's lap at <see cref="Scene.SunDistance"/> the year.</summary>
    public static readonly double SunMu = Math.Pow(Math.Tau / YearSeconds, 2) * Math.Pow(Scene.SunDistance, 3);

    /// <summary>The moon's lap round the planet among the stars, in seconds, from the planet's pull at its distance.</summary>
    public static readonly double MoonLapSeconds = Math.Tau * Math.Sqrt(Math.Pow(Scene.MoonDistance, 3) / GroundBody.PlanetMu);

    /// <summary>The moon's day: its lap against the sun (a little longer than among the stars, as the planet goes round the sun meanwhile).</summary>
    public static readonly double MoonDaySeconds = 1 / (1 / MoonLapSeconds - 1 / YearSeconds);

    /// <summary>The moon's gravitational parameter (m³/s²), from its pull at its surface.</summary>
    public const float MoonMu = Scene.MoonSurfaceGravity * Scene.MoonRadius * Scene.MoonRadius;

    /// <summary>The sun's disc's angular radius in the sky, in radians: its radius over its distance.</summary>
    public static readonly float SunAngularRadius = MathF.Asin(Scene.SunRadius / Scene.SunDistance);

    /// <summary>
    /// The planet's angle round the sun among the stars at world time 0, from <see cref="StarsX"/>:
    /// set so the sun's direction at Home is the clock's (<see cref="Scene.ClockStart"/> from its
    /// midnight, the sun a turn of the day round from straight under it).
    /// </summary>
    private static readonly double PlanetStart = -Math.PI / 2 - Math.Tau * Scene.ClockStart / Scene.DaySeconds;

    /// <summary>The moon's rising line among the stars, and its orbit's other axis (a quarter lap on: the furthest north).</summary>
    private static readonly Vector3 Node = Round(double.DegreesToRadians(Scene.MoonNodeDegrees)),
        Across = Tilted(Vector3.Cross(GroundBody.Planet.NorthPole, Node), GroundBody.Planet.NorthPole);

    /// <summary>The moon's angle along its orbit from its rising line at world time 0, in radians.</summary>
    private static readonly double MoonStart = double.DegreesToRadians(Scene.MoonStartDegrees);

    /// <summary>
    /// The moon's spin axis among the stars: its orbit's normal tilted by <see cref="Scene.MoonSpinTiltDegrees"/>
    /// about the rising line, back towards the planet's pole (so its own equator is tilted the less
    /// from the planet's, the sun's path, and its seasons are mild).
    /// </summary>
    private static readonly Vector3 SpinAxis = SpinTilted();

    /// <summary>
    /// The moon's orientation among the stars as it crosses its rising line (along its orbit 0): its
    /// own north (<see cref="Planet.NorthPole"/> in its axes) on <see cref="SpinAxis"/>, and its lat 0
    /// lon 0 (+Y in its axes, as <see cref="CubeSphere.FromLatLon"/> has it) towards the planet. As
    /// the spin axis is tilted about that line, the planet there is on the moon's equator, and the
    /// moon's nod (the planet's latitude on it) and its swing east and west (the planet's longitude)
    /// are each as far one way as the other a lap round from it: so lat 0 lon 0 faces the planet on average.
    /// </summary>
    private static readonly Quaternion MoonAtNode = AtNode();

    /// <summary><see cref="MoonAtNode"/>: the rows are where the moon's own x, y and z go (z is south, as on the planet's map).</summary>
    private static Quaternion AtNode()
    {
        var (x, y, z) = (Vector3.Cross(Node, SpinAxis), -Node, -SpinAxis);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1)));
    }

    /// <summary><see cref="SpinAxis"/>: the pole tilted about the rising line by the orbit's tilt less the spin's (the orbit's normal is the pole tilted by the whole of the orbit's).</summary>
    private static Vector3 SpinTilted()
    {
        var tilt = double.DegreesToRadians(Scene.MoonTiltDegrees - Scene.MoonSpinTiltDegrees);
        return Vector3.Normalize(GroundBody.Planet.NorthPole * (float)Math.Cos(tilt) - Vector3.Cross(GroundBody.Planet.NorthPole, Node) * (float)Math.Sin(tilt));
    }

    /// <summary>The moon's angle along its orbit from its rising line at <paramref name="worldTime"/>, in radians, wrapped to a turn.</summary>
    private static double MoonAlong(double worldTime) => Turn(MoonStart + Math.Tau * worldTime / MoonLapSeconds);

    /// <summary>The direction in the stars' equator <paramref name="angle"/> round the pole from <see cref="StarsX"/>.</summary>
    private static Vector3 Round(double angle) => StarsX * (float)Math.Cos(angle) + StarsY * (float)Math.Sin(angle);

    /// <summary><paramref name="a"/> tilted by the moon's tilt towards <paramref name="b"/> (square to it).</summary>
    private static Vector3 Tilted(Vector3 a, Vector3 b)
    {
        var tilt = double.DegreesToRadians(Scene.MoonTiltDegrees);
        return Vector3.Normalize(a * (float)Math.Cos(tilt) + b * (float)Math.Sin(tilt));
    }

    /// <summary><paramref name="angle"/> (radians) wrapped to a turn, in double, before it is used as a float.</summary>
    private static double Turn(double angle) => Sun.Wrap(angle, Math.Tau);

    /// <summary>
    /// The unit direction towards the sun at <paramref name="worldTime"/>, in the ground's frame:
    /// opposite the planet's place round it, turned back by the planet's turn.
    /// </summary>
    public static Vector3 SunDirection(double worldTime) =>
        GroundBody.Planet.TurnedDirection(-Round(Turn(PlanetStart + Math.Tau * worldTime / YearSeconds)), -worldTime);

    /// <summary>The planet's angle round the sun among the stars at <paramref name="worldTime"/>, from <see cref="StarsX"/>, wrapped to a turn.</summary>
    private static double PlanetAlong(double worldTime) => Turn(PlanetStart + Math.Tau * worldTime / YearSeconds);

    /// <summary>The direction in the stars' equator <paramref name="angle"/> round the pole from <see cref="StarsX"/>, in double.</summary>
    private static Vector3Double RoundAt(double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return (Vector3Double)StarsX * cos + (Vector3Double)StarsY * sin;
    }

    /// <summary>
    /// The sun's centre at <paramref name="worldTime"/>, in the planet's ground frame, in double:
    /// <see cref="Scene.SunDistance"/> from the planet's centre the way <see cref="SunDirection"/>
    /// points. What a place far from the planet (the map's camera, zoomed all the way out) sees
    /// the sun from: its way and its size from there differ from the planet's.
    /// </summary>
    public static Vector3Double SunCentreAt(double worldTime) =>
        (Vector3Double)GroundBody.Planet.Centre + GroundTurn(worldTime).Rotate(-RoundAt(PlanetAlong(worldTime)) * Scene.SunDistance);

    /// <summary>
    /// How far <paramref name="p"/> (a place in <paramref name="body"/>'s frame) is from the sun's
    /// centre at <paramref name="worldTime"/>, in double: changed into the planet's frame, where
    /// the sun's place is (<see cref="SunCentreAt"/>). What the solar system's edge
    /// (<see cref="Scene.SolarSystemEdge"/>) is measured by.
    /// </summary>
    public static double FromSun(GroundBody body, Vector3Double p, double worldTime) =>
        (Bodies.Change(body, GroundBody.Planet, worldTime).Point(p) - SunCentreAt(worldTime)).Length();

    /// <summary>
    /// The farthest the sun's centre ever gets from <paramref name="p"/> (a place in the planet's
    /// frame at <paramref name="worldTime"/>), as it goes round the planet's centre on its circle of
    /// <see cref="Scene.SunDistance"/>: from the far side of the circle, by the place's height over
    /// the circle's plane and its distance out from its axis. Never less than its distance from the
    /// sun now (<see cref="FromSun"/>), and reached once a year by a place that stays put.
    /// </summary>
    public static double FarthestSunFrom(Vector3Double p, double worldTime)
    {
        var axis = GroundTurn(worldTime).Rotate(Vector3Double.Normalize(Vector3Double.Cross(StarsX, StarsY)));
        var offset = p - (Vector3Double)GroundBody.Planet.Centre;
        var up = Vector3Double.Dot(offset, axis);
        var across = (offset - axis * up).Length() + Scene.SunDistance;
        return Math.Sqrt(up * up + across * across);
    }

    /// <summary>How fast the sun's centre goes round the planet's among the stars (m/s): its circle once a year.</summary>
    public static readonly double SunSpeed = Math.Tau * Scene.SunDistance / YearSeconds;

    /// <summary>
    /// The farthest <paramref name="body"/>'s centre can ever be from the sun's: the sun's distance
    /// from the planet's centre, and each body's from its parent's on the way to it (as at
    /// <paramref name="worldTime"/>: each goes round on a circle). A place that far from the
    /// body's centre, give or take its distance from there, is that far from the sun.
    /// </summary>
    public static double FarthestFromSun(GroundBody body, double worldTime)
    {
        double farthest = Scene.SunDistance;
        for (var b = body; b.Parent is { } parent; b = parent)
            farthest += (Bodies.CentreIn(b, parent, worldTime) - (Vector3Double)parent.Centre).Length();
        return farthest;
    }

    /// <summary>
    /// The sun's disc as seen from <paramref name="fromSun"/> (the way from a place to the sun's
    /// centre, in double): the way to its centre, and in w its angular radius from there (a right
    /// angle from on or in it).
    /// </summary>
    public static Vector4 DiscSeenFrom(Vector3Double fromSun)
    {
        var distance = fromSun.Length();
        var way = (fromSun / distance).ToSingle();
        return new Vector4(way, (float)Math.Asin(Math.Min(Scene.SunRadius / distance, 1.0)));
    }

    /// <summary>
    /// The planet's path round the sun, a whole lap from where it is at <paramref name="now"/>
    /// round to there again, into <paramref name="points"/> (the first and the last its centre now),
    /// in its ground frame at <paramref name="now"/>: a circle round the sun among the stars, as they
    /// match the ground then (as an orbit among the stars is drawn). Worked out in double.
    /// </summary>
    public static void PlanetPath(double now, Span<Vector3> points)
    {
        for (var i = 0; i < points.Length; i++)
            points[i] = PlanetPathAt(now, points.Length > 1 ? (double)i / (points.Length - 1) : 0).ToSingle();
    }

    /// <summary>
    /// The point <paramref name="share"/> of a lap on along <see cref="PlanetPath"/> from where the
    /// planet is at <paramref name="now"/>, in its ground frame then, in double.
    /// </summary>
    internal static Vector3Double PlanetPathAt(double now, double share)
    {
        var along = PlanetAlong(now);
        var fromNow = (RoundAt(along + Math.Tau * share) - RoundAt(along)) * Scene.SunDistance; // among the stars, from its centre now
        return (Vector3Double)GroundBody.Planet.Centre + GroundTurn(now).Rotate(fromNow);
    }

    /// <summary>The moon's centre at <paramref name="worldTime"/>, in the ground's frame (game coordinates, like <see cref="Planet.Centre"/>).</summary>
    public static Vector3 MoonCentre(double worldTime) => MoonCentreAt(worldTime).ToSingle();

    /// <summary>The planet's turn over <paramref name="worldTime"/>, undone, as a rotation in double: what turns a direction among the stars into the ground's frame at <paramref name="worldTime"/>.</summary>
    private static QuaternionDouble GroundTurn(double worldTime) =>
        QuaternionDouble.FromAxisAngle(GroundBody.Planet.NorthPole, GroundBody.Planet.TurnRadians(-worldTime));

    /// <summary>How fast the planet turns among the stars, in radians a second, in double (as its turn's angle is).</summary>
    private static readonly double PlanetTurnRate = Math.Tau / GroundBody.Planet.TurnSeconds;

    /// <summary>How fast the moon goes round its orbit among the stars (and turns about its axis), in radians a second.</summary>
    private static readonly double MoonLapRate = Math.Tau / MoonLapSeconds;

    /// <summary><see cref="MoonCentre"/> in double, for what is changed between the bodies' frames.</summary>
    public static Vector3Double MoonCentreAt(double worldTime)
    {
        var (sin, cos) = Math.SinCos(MoonAlong(worldTime));
        var amongStars = ((Vector3Double)Node * cos + (Vector3Double)Across * sin) * Scene.MoonDistance;
        return (Vector3Double)GroundBody.Planet.Centre + GroundTurn(worldTime).Rotate(amongStars);
    }

    /// <summary>
    /// How fast the moon's centre moves over the planet's ground at <paramref name="worldTime"/>, in
    /// double: along its orbit among the stars, less the ground's own turning under it.
    /// </summary>
    public static Vector3Double MoonVelocity(double worldTime)
    {
        var (sin, cos) = Math.SinCos(MoonAlong(worldTime));
        var turn = GroundTurn(worldTime);
        var along = turn.Rotate(((Vector3Double)Across * cos - (Vector3Double)Node * sin) * (Scene.MoonDistance * MoonLapRate));
        var fromCentre = turn.Rotate(((Vector3Double)Node * cos + (Vector3Double)Across * sin) * Scene.MoonDistance);
        return along - Vector3Double.Cross((Vector3Double)GroundBody.Planet.NorthPole * PlanetTurnRate, fromCentre);
    }

    /// <summary>
    /// The moon's angular velocity over the planet's ground at <paramref name="worldTime"/>, in
    /// double, in the ground's frame: its turn about its spin axis among the stars, less the planet's.
    /// </summary>
    public static Vector3Double MoonSpin(double worldTime) =>
        GroundTurn(worldTime).Rotate(SpinAxisD * MoonLapRate) - (Vector3Double)GroundBody.Planet.NorthPole * PlanetTurnRate;

    /// <summary>
    /// The moon's spin axis at <paramref name="worldTime"/>, in the ground's frame (its own north
    /// turned out): fixed among the stars, tilted from its orbit's normal by
    /// <see cref="Scene.MoonSpinTiltDegrees"/>; it turns about it once a lap, the planet's way, so it
    /// keeps one face to the planet but for a nod north and south by the tilt.
    /// </summary>
    public static Vector3 MoonSpinAxis(double worldTime) => GroundBody.Planet.TurnedDirection(SpinAxis, -worldTime);

    /// <summary>
    /// The moon's orientation at <paramref name="worldTime"/>: what turns a direction in its own axes
    /// (fixed on it, as the planet's map has them: <see cref="CubeSphere"/>, north
    /// <see cref="Planet.NorthPole"/>, lat 0 lon 0 its mean point under the planet) into the ground's
    /// frame. Turned about its spin axis by its angle along its orbit, then back by the planet's turn.
    /// </summary>
    public static Quaternion MoonOrientation(double worldTime) => MoonOrientationAt(worldTime).ToSingle();

    /// <summary>
    /// <see cref="MoonOrientation"/> in double, exactly unit, so a point turned to the moon's axes
    /// and back lands on itself to well within a float's step at the moon's distance from the origin,
    /// and its change over a few hundredths of a second is its spin, not its rounding.
    /// </summary>
    public static QuaternionDouble MoonOrientationAt(double worldTime)
    {
        var spun = QuaternionDouble.Concatenate(MoonAtNodeD, QuaternionDouble.FromAxisAngle(SpinAxisD, MoonAlong(worldTime)));
        return QuaternionDouble.Normalize(QuaternionDouble.Concatenate(spun, GroundTurn(worldTime)));
    }

    /// <summary><see cref="MoonAtNode"/> and <see cref="SpinAxis"/> in double, made exactly unit there.</summary>
    private static readonly QuaternionDouble MoonAtNodeD = QuaternionDouble.From(MoonAtNode);
    private static readonly Vector3Double SpinAxisD = Vector3Double.Normalize(SpinAxis);

    /// <summary>A point <paramref name="p"/> in the ground's frame (game coordinates) in the moon's own axes, from its centre, at <paramref name="worldTime"/>.</summary>
    public static Vector3 ToMoon(Vector3 p, double worldTime) =>
        MoonOrientationAt(worldTime).Conjugate.Rotate(p - MoonCentreAt(worldTime)).ToSingle();

    /// <summary>A point <paramref name="m"/> in the moon's own axes (from its centre) in the ground's frame at <paramref name="worldTime"/>: the inverse of <see cref="ToMoon"/>.</summary>
    public static Vector3 FromMoon(Vector3 m, double worldTime) =>
        (MoonCentreAt(worldTime) + MoonOrientationAt(worldTime).Rotate(m)).ToSingle();

    /// <summary>
    /// The sine of the planet's centre's angle above the horizon at the place on the moon's sphere
    /// (no ground) in direction <paramref name="moonFixedDirection"/> (in its own axes) at
    /// <paramref name="worldTime"/>: below 0, it is set there.
    /// </summary>
    public static float PlanetHeightFromMoon(Vector3 moonFixedDirection, double worldTime)
    {
        var up = Vector3.Transform(Vector3.Normalize(moonFixedDirection), MoonOrientation(worldTime));
        var place = MoonCentre(worldTime) + up * Scene.MoonRadius;
        return Vector3.Dot(up, Vector3.Normalize(GroundBody.Planet.Centre - place));
    }

    /// <summary>
    /// The unit direction towards the sun from the moon's centre at <paramref name="worldTime"/>, in
    /// the ground's frame: the sun's place less the moon's, which differs from the direction from the
    /// planet's centre (<see cref="SunDirection"/>) by up to the moon's distance over the sun's
    /// (about 0.8°, a few seconds of the moon's clock).
    /// </summary>
    public static Vector3 SunDirectionFromMoon(double worldTime) => SunDirectionFrom(MoonCentre(worldTime), worldTime);

    /// <summary>The unit direction towards the sun from <paramref name="p"/> (game coordinates) at <paramref name="worldTime"/>.</summary>
    private static Vector3 SunDirectionFrom(Vector3 p, double worldTime) =>
        Vector3.Normalize(SunDirection(worldTime) * Scene.SunDistance - (p - GroundBody.Planet.Centre));

    /// <summary>
    /// The share of the sun's disc not covered by the moon seen from <paramref name="p"/> (game
    /// coordinates) at <paramref name="worldTime"/>: 1 in the open, 0 in the moon's total shadow, and
    /// between, in its penumbra, by the area the two discs overlap. The sun is where it truly is
    /// (its direction from <paramref name="p"/>, not from the planet's centre) and the moon is its
    /// sphere (its ground's few hundred metres of relief are a few pixels on the disc). The planet
    /// is not in it: a place on the night side with the moon in line beyond the planet has its sun
    /// covered too, which changes nothing there (the sun is under its horizon), and at twilight
    /// dims the sky and the haze with the air above in the moon's shadow, as it is. Shaders'
    /// <c>sunSeen</c> is its twin.
    /// <para>
    /// The drawn eclipse's share, the moon's alone as the shaders have it (the tests check what is
    /// drawn by it); the dark the lights go by is judged past every body by <see cref="Sun.Seen(GroundBody, Vector3, double)"/>, which
    /// gives the same share on the planet. It first asks, once a world time
    /// (<see cref="NoShadowWithinTheAir"/>), whether the moon is too far from the sun for its
    /// shadow to reach anywhere within the air; if so, a place there has the whole sun (exactly 1,
    /// as the full answer would be), and otherwise the full answer is worked out from the same sun
    /// and moon.
    /// </para>
    /// </summary>
    public static float SunSeen(Vector3 p, double worldTime)
    {
        if (NoShadowWithinTheAir(worldTime) && (p - GroundBody.Planet.Centre).LengthSquared() <= AirReach * AirReach)
            return 1f;
        return SunSeen(p, _seenSun, _seenMoon);
    }

    /// <summary>The air's top from the planet's centre: every place and vessel the reject below speaks for is within it.</summary>
    private const float AirReach = GroundBody.PlanetSeaRadius + Scene.AtmosphereHeight;

    /// <summary>
    /// The least angle between the moon and the sun seen from the planet's centre at which no
    /// place within <see cref="AirReach"/> (r) of it can have the moon over any of the sun: seen
    /// from such a place, the moon moves by at most asin(r / D) (D its distance), the sun by
    /// asin(r / S), the moon looks at most asin(R / (D - r)) across (R its radius) and the sun
    /// <see cref="SunAngularRadius"/>; with a milliradian over, for the floats' rounding.
    /// </summary>
    private static readonly float ShadowReach = (float)(Math.Asin(AirReach / (double)Scene.MoonDistance)
        + Math.Asin(Scene.MoonRadius / (double)(Scene.MoonDistance - AirReach))
        + Math.Asin(AirReach / (double)Scene.SunDistance) + SunAngularRadius + 1e-3);

    // The sun and moon at the last world time asked of SunSeen on this thread, and whether the
    // moon's shadow is clear of the air then (per thread, as the map's search runs on its own).
    [ThreadStatic] private static bool _seenKnown, _seenClear;
    [ThreadStatic] private static double _seenTime;
    [ThreadStatic] private static Vector3 _seenSun, _seenMoon;

    /// <summary>
    /// Whether the moon is so far from the sun at <paramref name="worldTime"/>, seen from the
    /// planet's centre, that its shadow touches nothing within the air (<see cref="ShadowReach"/>):
    /// so most of each lap, all but near the new moon. Worked out once a world time a thread.
    /// </summary>
    internal static bool NoShadowWithinTheAir(double worldTime)
    {
        if (!_seenKnown || _seenTime != worldTime)
        {
            (_seenKnown, _seenTime) = (true, worldTime);
            (_seenSun, _seenMoon) = (SunDirection(worldTime), MoonCentre(worldTime));
            var moon = _seenMoon - GroundBody.Planet.Centre;
            _seenClear = MathF.Atan2(Vector3.Cross(_seenSun, moon).Length(), Vector3.Dot(_seenSun, moon)) > ShadowReach;
        }
        return _seenClear;
    }

    /// <summary>
    /// <see cref="SunSeen(Vector3, double)"/> from a frame's own sun and moon: the sun towards
    /// <paramref name="sunDirection"/> from the planet's centre (<see cref="SunDirection"/>) and the
    /// moon's centre at <paramref name="moonCentre"/> (<see cref="MoonCentre"/>), so the renderer
    /// can ask it of what it was handed.
    /// </summary>
    public static float SunSeen(Vector3 p, Vector3 sunDirection, Vector3 moonCentre)
    {
        var sun = Vector3.Normalize(sunDirection * Scene.SunDistance - (p - GroundBody.Planet.Centre));
        var toMoon = moonCentre - p;
        var distance = toMoon.Length();
        var moon = MathF.Asin(MathF.Min(Scene.MoonRadius / distance, 1f));
        var apart = MathF.Atan2(Vector3.Cross(sun, toMoon).Length(), Vector3.Dot(sun, toMoon));
        return DiscSeen(apart, SunAngularRadius, moon);
    }

    /// <summary>
    /// The share of a disc of angular radius <paramref name="r"/> left uncovered by one of
    /// <paramref name="cover"/> whose centre is <paramref name="apart"/> from it (radians; the discs
    /// taken as flat, as both are small): 1 exactly once they don't overlap, so outside an eclipse
    /// nothing it multiplies changes; otherwise 1 less the lens the two make over the disc's area.
    /// Shaders' <c>discSeen</c> is its twin.
    /// </summary>
    public static float DiscSeen(float apart, float r, float cover)
    {
        if (cover <= 0f || apart >= r + cover)
            return 1f;
        if (apart <= cover - r)
            return 0f; // wholly covered
        if (apart <= r - cover)
            return 1f - cover * cover / (r * r); // the cover wholly on the disc (a ring left)
        var (aa, rr, cc) = (apart * apart, r * r, cover * cover);
        var a = MathF.Acos(Math.Clamp((aa + rr - cc) / (2f * apart * r), -1f, 1f));
        var b = MathF.Acos(Math.Clamp((aa + cc - rr) / (2f * apart * cover), -1f, 1f));
        var k = MathF.Sqrt(MathF.Max((r + cover - apart) * (apart + r - cover) * (apart - r + cover) * (apart + r + cover), 0f));
        var lens = rr * a + cc * b - 0.5f * k;
        return Math.Clamp(1f - lens / (MathF.PI * rr), 0f, 1f);
    }

    /// <summary>
    /// The share and colour of the sunlight reaching <paramref name="p"/> (game coordinates, out
    /// past the planet's air: on the moon) past the planet at <paramref name="worldTime"/>, linear
    /// rgb: 1, 1, 1 in the open. Each of <see cref="Scene.PlanetShadowSamples"/> points across the
    /// sun's disc, along the line through the planet's centre as seen from <paramref name="p"/>, is
    /// seen along a straight ray: clear of the air, all of it; through the air, the air's
    /// transmittance over the ray's whole chord through it (<see cref="AirTables"/>: from its
    /// closest approach out, squared); into the planet, the light of the ray grazing it times
    /// <see cref="Scene.PlanetShadowGlow"/> (more of it within a scale height of the edge, so
    /// nothing jumps there). The disc's points are weighted by its width there, so as it goes
    /// behind the air and the planet the light dims and reddens smoothly. Shaders' <c>planetShadowLight</c>
    /// is its twin: change one and change the other.
    /// </summary>
    public static Vector3 PlanetShadowLight(Vector3 p, double worldTime) =>
        ShadowLight(GroundBody.Planet, p - GroundBody.Planet.Centre, SunDirectionFrom(p, worldTime));

    /// <summary>
    /// <see cref="PlanetShadowLight"/> past any body with air (<see cref="GroundBody.HasAir"/>: the
    /// air's model, <see cref="AirTables"/>, over its sea): the share and colour of the sunlight
    /// reaching a place <paramref name="fromCentre"/> from <paramref name="body"/>'s centre, out past
    /// its air, the sun towards <paramref name="sun"/> (a unit way from the place). What the moon's
    /// ground is drawn lit by, and so what the dark there is judged by (<see cref="Sun.Seen(GroundBody, Vector3, double)"/>).
    /// </summary>
    public static Vector3 ShadowLight(GroundBody body, Vector3 fromCentre, Vector3 sun)
    {
        var distance = fromCentre.Length();
        var toCentre = -fromCentre / distance;
        var angle = MathF.Atan2(Vector3.Cross(sun, toCentre).Length(), Vector3.Dot(sun, toCentre));
        var r = SunAngularRadius;
        var top = MathF.Asin(MathF.Min((body.SeaRadius + Scene.AtmosphereHeight) / distance, 1f));
        if (angle - r >= top)
            return Vector3.One; // the whole disc clear of the air
        var grazing = AirTables.Look(0, 0f, 0f);
        grazing *= grazing;
        var (sum, weights) = (Vector3.Zero, 0f);
        for (var i = 0; i < Scene.PlanetShadowSamples; i++)
        {
            var x = (i + 0.5f) / Scene.PlanetShadowSamples * 2f - 1f;
            var weight = MathF.Sqrt(1f - x * x);
            sum += PastTheBody(body.SeaRadius, distance, angle + x * r, grazing) * weight;
            weights += weight;
        }
        return sum / weights;
    }

    /// <summary>
    /// The light along a ray from <paramref name="distance"/> from a body's centre at
    /// <paramref name="angle"/> (radians) from the way to it: by how high over its sea
    /// (<paramref name="seaRadius"/>) it passes at its closest, through the air's whole chord there,
    /// or (into the body) the <paramref name="grazing"/> ray's light the air bends into the shadow.
    /// </summary>
    private static Vector3 PastTheBody(float seaRadius, float distance, float angle, Vector3 grazing)
    {
        var closest = (angle >= MathF.PI / 2 ? distance : distance * MathF.Sin(angle)) - seaRadius;
        if (closest >= Scene.AtmosphereHeight)
            return Vector3.One;
        if (closest >= 0f)
        {
            var half = AirTables.Look(0, closest, 0f);
            return half * half;
        }
        return grazing * (Scene.PlanetShadowGlow + (1f - Scene.PlanetShadowGlow) * MathF.Exp(closest / Scene.AtmosphereScaleHeight));
    }

    /// <summary>
    /// The light off the planet at <paramref name="p"/> (game coordinates) at
    /// <paramref name="worldTime"/>, as a share of the sunlight on a face turned to the planet: the
    /// share of the planet's face seen from <paramref name="p"/> that is lit (all at a new moon,
    /// none at a full one) times <see cref="Scene.PlanetshineStrength"/>. Shaders' <c>planetshine</c> is its twin.
    /// </summary>
    public static float Planetshine(Vector3 p, double worldTime) =>
        (1f + Vector3.Dot(SunDirection(worldTime), Vector3.Normalize(p - GroundBody.Planet.Centre))) * 0.5f * Scene.PlanetshineStrength;

    /// <summary>
    /// What the moon gives back of the sunlight at <paramref name="worldTime"/>, as a share of it
    /// (linear rgb) on a face turned to the moon with all its lit face seen, before the planet's
    /// air: its grey (<see cref="Scene.MoonAlbedo"/>) times <see cref="Scene.MoonlightStrength"/>,
    /// dimmed and reddened by the planet's shadow over its centre (<see cref="PlanetShadowLight"/>),
    /// so in a lunar eclipse its light goes too. The same for every place: worked out once a frame
    /// and handed to the shaders (<c>frame.moonlight</c>).
    /// </summary>
    public static Vector3 MoonShine(double worldTime) =>
        PlanetShadowLight(MoonCentre(worldTime), worldTime) * (Scene.MoonAlbedo * Scene.MoonlightStrength);

    /// <summary>
    /// The moon's light at <paramref name="p"/> (game coordinates) at <paramref name="worldTime"/>,
    /// as a share of the sunlight (linear rgb) on a face turned to the moon: its light
    /// (<see cref="MoonShine"/>) times the share of its face seen from <paramref name="p"/> that is
    /// lit (all at a full moon, none at a new one; the sun's direction taken from the planet's
    /// centre, as the shaders have it) times the air's transmittance towards it (none with it below
    /// the horizon). A dim light from the moon's direction, with no shadow. Shaders' <c>moonlight</c>
    /// is its twin, which multiplies in the sun's colour and the exposure on top, as the shaders
    /// light everything by the sun.
    /// </summary>
    public static Vector3 Moonlight(Vector3 p, double worldTime)
    {
        var toMoon = Vector3.Normalize(MoonCentre(worldTime) - p);
        var lit = (1f - Vector3.Dot(SunDirection(worldTime), toMoon)) * 0.5f;
        var o = p - GroundBody.Planet.Centre;
        var air = AirTables.Look(0, o.Length() - GroundBody.Planet.SeaRadius, Vector3.Dot(Vector3.Normalize(o), toMoon));
        return MoonShine(worldTime) * (lit * air);
    }

    /// <summary>
    /// Seconds into the local day at <paramref name="p"/> on (or over) the moon at
    /// <paramref name="worldTime"/>, from 0 (midnight) to just under <see cref="Scene.DaySeconds"/>,
    /// read as the planet's clock is (noon at half the day) but over the moon's longer day
    /// (<see cref="MoonDaySeconds"/>): by how far round the moon's spin axis <paramref name="p"/> is
    /// east of the point the sun is over, as the moon sees it (<see cref="SunDirectionFromMoon"/>).
    /// </summary>
    public static double MoonLocalSeconds(Vector3 p, double worldTime)
    {
        var axis = MoonSpinAxis(worldTime);
        var centre = MoonCentre(worldTime);
        var sun = SunDirectionFrom(centre, worldTime);
        var up = p - centre;
        var (noon, at) = (sun - axis * Vector3.Dot(sun, axis), up - axis * Vector3.Dot(up, axis));
        var east = Math.Atan2(Vector3.Dot(Vector3.Cross(noon, at), axis), Vector3.Dot(noon, at));
        return Sun.Wrap(Scene.DaySeconds / 2 + east / Math.Tau * Scene.DaySeconds, Scene.DaySeconds);
    }
}
