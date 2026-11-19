using System.Numerics;

namespace GameOne;

/// <summary>
/// The light of the air: the sun's light dimmed by the air it crosses, and the sky's, scattered
/// towards the eye by the air along the way. Single scattering, by the air's molecules (Rayleigh)
/// and its haze (Mie), and absorbed by a layer of ozone (which takes the orange and green out of
/// the low sun's light: what keeps a twilight sky blue), worked out by marching along the ray as Nishita, O'Neil and Hillaire do. This
/// is the reference: the tables the GPU reads are built from it and the shaders repeat its maths,
/// so change one and change the others. Colours are linear RGB; directions are unit; the sun's
/// light is <see cref="Scene.SunColor"/> before any air, and a sky is in the same units (the
/// radiance a view ray gathers, to be read against it).
/// </summary>
internal static class Air
{
    /// <summary>A point or direction in double: lengths along a ray on a sphere of 10 km would lose their sense in float.</summary>
    private readonly record struct D3(double X, double Y, double Z)
    {
        public static D3 From(Vector3 v) => new(v.X, v.Y, v.Z);
        public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static D3 operator *(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(D3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public double Length => Math.Sqrt(Dot(this));
    }

    /// <summary>The sun's light before any air, linear (<see cref="Scene.SunColor"/> is a display colour, as the shaders' <c>toLinear</c> reads it).</summary>
    private static readonly Vector3 SunLight = new(
        MathF.Pow(Scene.SunColor.X, Scene.DisplayGamma), MathF.Pow(Scene.SunColor.Y, Scene.DisplayGamma), MathF.Pow(Scene.SunColor.Z, Scene.DisplayGamma));

    private const double SeaRadius = GroundBody.PlanetSeaRadius;
    private const double TopRadius = GroundBody.PlanetSeaRadius + Scene.AtmosphereHeight;

    /// <summary>
    /// How much of the sun's light gets through the air from <paramref name="from"/> along
    /// <paramref name="dir"/> out to space: 1 in each colour through no air, less the more there is
    /// (blue goes first), and 0 only if the ray meets the sea's sphere (a ray out of space that crosses no air is 1).
    /// </summary>
    public static Vector3 Transmittance(Vector3 from, Vector3 dir)
    {
        var (o, d) = Start(from, dir);
        return Escape(o, d);
    }

    /// <summary>The sun's light at <paramref name="at"/>, with <paramref name="sun"/> the direction towards it: <see cref="Scene.SunColor"/> (linear) times the air's transmittance.</summary>
    public static Vector3 Sunlight(Vector3 at, Vector3 sun) => SunLight * Transmittance(at, sun);

    /// <summary>
    /// How much the lens flare is dimmed seen from <paramref name="camera"/>, the sun towards
    /// <paramref name="sun"/>: the brightness (luminance) of the sun's light there against its own
    /// before any air, 1 through no air, 0 with the sea in the way; times <paramref name="sunSeen"/>,
    /// the share of the sun's disc the moon leaves uncovered there (<see cref="SolarSystem.SunSeen(Vector3, Vector3, Vector3)"/>:
    /// none in a total eclipse).
    /// </summary>
    public static float FlareDimming(Vector3 camera, Vector3 sun, float sunSeen) =>
        Math.Clamp(Luminance(Sunlight(camera, sun)) / Luminance(SunLight), 0f, 1f) * sunSeen;

    /// <summary>
    /// How much the air dims the moon's light seen from <paramref name="camera"/> along <paramref name="d"/>
    /// (unit), the moon <paramref name="toMoon"/> away on it: the table's transmittance there
    /// (<see cref="AirTables.Look"/>) if that way comes within the air's top of the planet's centre,
    /// else 1, as no air lies on it. Not the ray's to infinity, which from beyond the moon meets
    /// the sea behind it and would be 0. Twin of the sky shader's <c>throughAir</c> and its dimming
    /// of the moon's face in <c>Shaders.SkyFragment</c>.
    /// </summary>
    public static Vector3 MoonSeenThrough(Vector3 camera, Vector3 d, float toMoon)
    {
        var toPlanet = D3.From(GroundBody.Planet.Centre - camera);
        var nearest = (toPlanet + D3.From(d) * -Math.Clamp(toPlanet.Dot(D3.From(d)), 0, toMoon)).Length;
        if (nearest >= TopRadius)
            return Vector3.One;
        var from = camera - GroundBody.Planet.Centre;
        return AirTables.Look(0, (float)(D3.From(from).Length - SeaRadius), Vector3.Dot(Vector3.Normalize(from), d));
    }

    /// <summary>A linear colour's brightness to the eye (Rec. 709's weights).</summary>
    internal static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>
    /// The sky's light seen from <paramref name="from"/> looking along <paramref name="dir"/>:
    /// single scattering of the sun (towards <paramref name="sun"/>) along the ray, from the eye to
    /// where it leaves the air or meets the sea. Zero where the ray crosses no air.
    /// </summary>
    public static Vector3 Sky(Vector3 from, Vector3 dir, Vector3 sun)
    {
        var (o, d) = Start(from, dir);
        return Reach(o, d, out var near, out var far) ? March(o, d, near, far, D3.From(sun)) : Vector3.Zero;
    }

    /// <summary>The same light along the segment from <paramref name="from"/> to <paramref name="to"/>, for the fog: what the air between adds.</summary>
    public static Vector3 InScatter(Vector3 from, Vector3 to, Vector3 sun)
    {
        var (o, d, length) = Between(from, to);
        return length > 0 ? March(o, d, 0, length, D3.From(sun)) : Vector3.Zero;
    }

    /// <summary>How much light gets through the air between <paramref name="from"/> and <paramref name="to"/>: what the ground's colour is dimmed by on its way to the eye.</summary>
    public static Vector3 TransmittanceBetween(Vector3 from, Vector3 to)
    {
        var (o, d, length) = Between(from, to);
        return length > 0 ? Through(o, d, 0, length, Scene.SkySamples) : Vector3.One;
    }

    /// <summary>
    /// The sky's light falling on an upward face <paramref name="aboveSea"/> above Home's spot of
    /// the sea, with the sun <paramref name="sunUp"/> (the cosine of its angle from straight up):
    /// the sky's radiance over the upper hemisphere, cosine-weighted (an irradiance, in the same
    /// units as the sun's light), from 64 directions spread evenly over it.
    /// </summary>
    public static Vector3 SkyLight(float aboveSea, float sunUp)
    {
        const int directions = 64;
        var up = GroundBody.Planet.Up(Vector3.Zero);
        var east = GroundBody.Planet.East(Vector3.Zero);
        var north = GroundBody.Planet.North(Vector3.Zero);
        var at = GroundBody.Planet.Centre + up * (GroundBody.PlanetSeaRadius + MathF.Max(aboveSea, 0f));
        var sun = up * sunUp + east * MathF.Sqrt(MathF.Max(1f - sunUp * sunUp, 0f));
        var total = Vector3.Zero;
        for (var i = 0; i < directions; i++)
        {
            // Cosine-weighted: the cosine of the angle from up is the root of an even spread, the turn a golden angle each.
            var cos = MathF.Sqrt((i + 0.5f) / directions);
            var sin = MathF.Sqrt(1f - cos * cos);
            var turn = i * 2.39996323f;
            var dir = up * cos + east * (sin * MathF.Cos(turn)) + north * (sin * MathF.Sin(turn));
            total += Sky(at, dir, sun);
        }
        return total * (MathF.PI / directions);
    }

    /// <summary>The ray's start (from the planet's centre) and direction, in double; a point under the sea is lifted onto the sea's sphere (below it there is no air to speak of, and its light stays finite).</summary>
    private static (D3 o, D3 d) Start(Vector3 from, Vector3 dir) => (Lift(from), D3.From(dir));

    private static D3 Lift(Vector3 p)
    {
        var o = D3.From(p - GroundBody.Planet.Centre);
        var r = o.Length;
        return r > 0 && r < SeaRadius ? o * (SeaRadius / r) : o;
    }

    /// <summary>
    /// A segment's start from the centre, its direction and its length (0 for a point). Its start is
    /// lifted onto the sea's sphere if under it, and an end under the sea clips the segment where
    /// it meets the sea's sphere, so a seabed point counts the air down to the water and no further.
    /// </summary>
    private static (D3 o, D3 d, double length) Between(Vector3 from, Vector3 to)
    {
        var o = Lift(from);
        var along = D3.From(to - GroundBody.Planet.Centre) + o * -1;
        var length = along.Length;
        if (length <= 0)
            return (o, along, 0);
        var d = along * (1 / length);
        if (D3.From(to - GroundBody.Planet.Centre).Length < SeaRadius)
            length = Hit(o, d, SeaRadius, out var near, out _) ? Math.Clamp(near, 0, length) : 0;
        return (o, d, length);
    }

    /// <summary>The roots of a ray against a sphere of radius <paramref name="radius"/> about the centre (near, far), if it meets it.</summary>
    private static bool Hit(D3 o, D3 d, double radius, out double near, out double far)
    {
        var b = o.Dot(d);
        var disc = b * b - (o.Dot(o) - radius * radius);
        if (disc < 0)
        {
            (near, far) = (0, 0);
            return false;
        }
        var root = Math.Sqrt(disc);
        (near, far) = (-b - root, -b + root);
        return true;
    }

    /// <summary>Whether the ray meets the sea's sphere ahead of it (it does going down from the sea's level, and doesn't going up from it).</summary>
    private static bool MeetsSea(D3 o, D3 d) => Hit(o, d, SeaRadius, out _, out var far) && far > 0.01;

    /// <summary>
    /// How much light gets through the air along the ray to space: 0 if it meets the sea first, 1 if
    /// it crosses no air (out of the air looking away, or missing it), else what the air in its way lets by.
    /// </summary>
    private static Vector3 Escape(D3 o, D3 d)
    {
        if (MeetsSea(o, d))
            return Vector3.Zero;
        if (!Hit(o, d, TopRadius, out var near, out var far) || far <= 0)
            return Vector3.One;
        return Through(o, d, Math.Max(near, 0), far, Scene.SunSamples); // (from outside the air, its way starts where it enters)
    }

    /// <summary>
    /// The stretch of a view ray that is in the air: from where it enters it (0 if it starts inside)
    /// to where it leaves it or meets the sea.
    /// </summary>
    private static bool Reach(D3 o, D3 d, out double from, out double to)
    {
        (from, to) = (0, 0);
        if (!Hit(o, d, TopRadius, out var near, out var far) || far <= 0)
            return false;
        (from, to) = (Math.Max(near, 0), far);
        if (Hit(o, d, SeaRadius, out var seaNear, out var seaFar) && seaFar > 0.01)
            to = Math.Min(to, Math.Max(seaNear, 0));
        return to > from;
    }

    /// <summary>The thickness of the air's molecules at <paramref name="p"/> (1 at the sea: <see cref="Planet.AirDensity"/>).</summary>
    private static float Molecules(D3 p) => GroundBody.PlanetAirDensity((float)(p.Length - SeaRadius));

    /// <summary>The thickness of the haze at <paramref name="p"/>: the air's own shape with its own scale height.</summary>
    private static float Haze(D3 p)
    {
        var h = (float)Math.Max(p.Length - SeaRadius, 0);
        return h >= Scene.AtmosphereHeight ? 0f : MathF.Exp(-h / Scene.MieScaleHeight) * (1f - h / Scene.AtmosphereHeight);
    }

    /// <summary>The thickness of the ozone at <paramref name="p"/>: a layer peaking at <see cref="Scene.OzoneHeight"/> (1), down to none <see cref="Scene.OzoneHalfWidth"/> above and below it.</summary>
    private static float Ozone(D3 p) => MathF.Max(0f, 1f - MathF.Abs((float)(p.Length - SeaRadius) - Scene.OzoneHeight) / Scene.OzoneHalfWidth);

    /// <summary>What the air's extinction adds up to for one step at <paramref name="p"/>: per colour, the molecules' and the haze's scattering (<paramref name="molecules"/> and <paramref name="haze"/> thick) and what the ozone absorbs, over <paramref name="step"/> metres.</summary>
    private static Vector3 Extinction(D3 p, float molecules, float haze, double step) =>
        (Scene.RayleighScatter * molecules + new Vector3(Scene.MieExtinction * haze) + Scene.OzoneAbsorption * Ozone(p)) * (float)step;

    private static Vector3 Dim(Vector3 depth) => new(MathF.Exp(-depth.X), MathF.Exp(-depth.Y), MathF.Exp(-depth.Z));

    /// <summary>The light left after <paramref name="steps"/> midpoint steps along the ray from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Vector3 Through(D3 o, D3 d, double from, double to, int steps)
    {
        var step = (to - from) / steps;
        var depth = Vector3.Zero;
        for (var i = 0; i < steps; i++)
        {
            var p = o + d * (from + (i + 0.5) * step);
            depth += Extinction(p, Molecules(p), Haze(p), step);
        }
        return Dim(depth);
    }

    /// <summary>
    /// The light scattered towards the eye along the ray from <paramref name="from"/> to
    /// <paramref name="to"/> along it: at each step the sun's light there (dimmed by its own way
    /// through the air; none if the sea's sphere is between), scattered by the phase of the angle to
    /// the sun, and dimmed again on its way to the eye.
    /// </summary>
    private static Vector3 March(D3 o, D3 d, double from, double to, D3 sun)
    {
        var steps = Scene.SkySamples;
        var step = (to - from) / steps;
        var mu = d.Dot(sun);
        var (rayleighPhase, hazePhase) = (RayleighPhase(mu), HazePhase(mu));
        var (depth, light) = (Vector3.Zero, Vector3.Zero);
        for (var i = 0; i < steps; i++)
        {
            var p = o + d * (from + (i + 0.5) * step);
            var (molecules, haze) = (Molecules(p), Haze(p));
            // The eye's dimming runs to the middle of the step, so half of this step's own is in it.
            var here = Extinction(p, molecules, haze, step);
            if (molecules > 0 || haze > 0)
            {
                // The sun's light at the step: none if the sea is in the way, else through the air to space.
                var sunLight = SunLight * Escape(p, sun);
                var scattered = Scene.RayleighScatter * (molecules * rayleighPhase) + new Vector3(Scene.MieScatter * haze * hazePhase);
                light += sunLight * Dim(depth + here * 0.5f) * scattered * (float)step;
            }
            depth += here;
        }
        return light;
    }

    /// <summary>Rayleigh's phase, 3/(16π)(1 + μ²), of μ the cosine of the angle between the view's direction and the sun's.</summary>
    private static float RayleighPhase(double mu) => (float)(3.0 / (16.0 * Math.PI) * (1 + mu * mu));

    /// <summary>The haze's phase, Cornette-Shanks with <see cref="Scene.MieG"/>, of μ as <see cref="RayleighPhase"/>.</summary>
    private static float HazePhase(double mu)
    {
        double g = Scene.MieG, g2 = g * g;
        return (float)(3.0 / (8.0 * Math.PI) * (1 - g2) * (1 + mu * mu) / ((2 + g2) * Math.Pow(1 + g2 - 2 * g * mu, 1.5)));
    }
}
