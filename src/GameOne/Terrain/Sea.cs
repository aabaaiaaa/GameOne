using System.Numerics;

namespace GameOne;

/// <summary>
/// A named sea, as the map records it: its climate, its wave size (<see cref="Waves"/>, the height
/// in metres from a crest to a trough of its biggest waves, at most: the waves seldom all line up,
/// so the sea usually moves rather less) and its water's colours (display values) over the shelf
/// and over deep water.
/// </summary>
internal sealed record SeaInfo(string Name, SeaKind Kind, float Waves, Vector3 Shallow, Vector3 Deep);

/// <summary>
/// The sea's surface: one fixed set of waves (<see cref="Waves"/>) running over the whole planet,
/// scaled in each place by the sea there (<see cref="WaveScale"/>). Everything that needs the water
/// (drawing it, floating on it, splashing into it) reads this, with the phases of the moment from
/// <see cref="Phases"/>.
/// </summary>
/// <remarks>
/// The planet shader repeats <see cref="Height"/> and <see cref="WaveScale"/> in GLSL, so they are
/// written to transcribe: float maths only, the phases handed in as floats, and every sine's
/// argument wrapped into [-π, π] first (<see cref="Wrap"/>), where GLSL's sine is accurate.
/// </remarks>
internal static class Sea
{
    public const int WaveCount = 8;

    /// <summary>
    /// Each wave's direction (a unit vector: its crests are the planes across it, so each wave is
    /// straight where its direction lies along the ground and fades out where it points straight
    /// up), its wavelength in metres and its share of the sea's wave size (the shares sum to 1).
    /// The directions are spread apart so no two waves run alike anywhere. Most of the height is in
    /// the long waves, so the surface stays gentle enough to float on and to draw.
    /// </summary>
    public static readonly IReadOnlyList<(Vector3 Direction, float Wavelength, float Share)> Waves =
    [
        (Vector3.Normalize(new(0.62f, 0.28f, 0.73f)), 90f, 0.26f),
        (Vector3.Normalize(new(-0.71f, 0.35f, 0.61f)), 72f, 0.20f),
        (Vector3.Normalize(new(0.21f, -0.54f, 0.81f)), 57f, 0.16f),
        (Vector3.Normalize(new(0.93f, -0.31f, -0.19f)), 45f, 0.12f),
        (Vector3.Normalize(new(-0.27f, -0.18f, -0.95f)), 36f, 0.09f),
        (Vector3.Normalize(new(0.45f, 0.86f, -0.24f)), 29f, 0.07f),
        (Vector3.Normalize(new(-0.58f, 0.72f, -0.38f)), 23f, 0.06f),
        (Vector3.Normalize(new(-0.83f, -0.49f, 0.27f)), 18f, 0.04f),
    ];

    /// <summary>
    /// The highest any sea's waves reach above sea level, and as far below it their troughs: half
    /// the roughest sea's wave size (<see cref="SeaInfo.Waves"/> is crest to trough), as all the
    /// waves at once can reach. Anything higher is out of the water; nothing drawn of the sea is
    /// outside it (<see cref="Rendering.PlanetPatches"/>).
    /// </summary>
    public static float Highest => GroundBody.Planet.HighestWave;

    /// <summary>A wave's angular frequency, in radians a second: deep water's, so the long waves run faster.</summary>
    public static double Frequency(float wavelength) => Math.Sqrt(Scene.SurfaceGravity * 2.0 * Math.PI / wavelength);

    /// <summary>
    /// Fills <paramref name="into"/> (<see cref="WaveCount"/> long) with each wave's phase at
    /// <paramref name="time"/> (seconds), in radians in [0, 2π). Worked out in double precision and
    /// wrapped before it becomes a float, so the waves stay smooth however long the world has run.
    /// </summary>
    public static void Phases(double time, Span<float> into)
    {
        for (var i = 0; i < WaveCount; i++)
        {
            var phase = (Frequency(Waves[i].Wavelength) * time) % (2.0 * Math.PI);
            into[i] = (float)(phase < 0.0 ? phase + 2.0 * Math.PI : phase);
        }
    }

    /// <summary>
    /// An angle in radians brought into [-π, π]: GLSL only promises an accurate sine there, and
    /// the waves' arguments run to thousands of radians across the planet.
    /// </summary>
    public static float Wrap(float angle) => angle - 2f * MathF.PI * MathF.Floor(angle / (2f * MathF.PI) + 0.5f);

    /// <summary>
    /// The sea's wave size (<see cref="SeaInfo.Waves"/>) in <paramref name="direction"/>, a unit
    /// vector: blended over the four texels round it as the map's heights are, land counting 0, so
    /// it changes smoothly where two seas meet; damped among the floes round the icy seas' frozen
    /// sheets (each <see cref="SurfaceKind.FloeField"/> texel's share scaled by
    /// <see cref="Scene.FloeWaveDamping"/>), so it eases into the band rather than stepping; and
    /// faded out as the water shallows (<see cref="Scene.SeaWaveFullDepth"/>), so the waves die
    /// away at the shore.
    /// </summary>
    public static float WaveScale(Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        var map = Terrain.Map;
        map.Texels(face, u, v, index, weight);
        var scale = 0f;
        for (var i = 0; i < 4; i++)
        {
            var region = map.Regions[index[i]];
            if (region == 0) continue;
            var damping = map.Kinds[index[i]] == (byte)SurfaceKind.FloeField ? Scene.FloeWaveDamping : 1f;
            scale += map.Seas[region - 1].Waves * weight[i] * damping; // the map promises every region is a sea it lists
        }

        if (scale <= 0f) return 0f;
        var depth = -Terrain.Height(direction);
        var t = Math.Clamp(depth / Scene.SeaWaveFullDepth, 0f, 1f);
        return scale * t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The sea's surface in <paramref name="direction"/> (a unit vector), as an elevation (metres
    /// above the sea, around 0), with the phases of the moment (<see cref="Phases"/>). Each wave is weighted
    /// by how far its direction lies along the ground there, so none lifts the whole sea at once.
    /// </summary>
    public static float Height(Vector3 direction, ReadOnlySpan<float> phases)
    {
        var scale = WaveScale(direction);
        if (scale <= 0f) return 0f;
        var p = direction * Scene.DetailScale;
        var sum = 0f;
        for (var i = 0; i < WaveCount; i++)
        {
            var (d, wavelength, share) = Waves[i];
            var along = Vector3.Dot(d, direction);
            var tangential = MathF.Sqrt(MathF.Max(1f - along * along, 0f));
            var k = 2f * MathF.PI / wavelength;
            sum += share * tangential * MathF.Sin(Wrap(k * Vector3.Dot(d, p) - phases[i]));
        }

        return scale * 0.5f * sum;
    }

    /// <summary>
    /// <see cref="Height"/> in <paramref name="direction"/> taken apart by wave, for a point read
    /// at many moments (a raft's, see <see cref="Raft.Pose"/>): as sin(x − φ) = sin x cos φ −
    /// cos x sin φ, its height above sea level is Σ <paramref name="sines"/>[i] cos φᵢ −
    /// <paramref name="cosines"/>[i] sin φᵢ over the waves' phases φ (<see cref="Phases"/>). The
    /// costly parts (the wave size from the map and the ground, and a sine a wave) are done once
    /// here, so each read after is a sum, with no sines but the phases' own. The same terms as
    /// <see cref="Height"/>'s, so it is the same sea to within a float's rounding.
    /// </summary>
    public static void Terms(Vector3 direction, Span<float> sines, Span<float> cosines)
    {
        var scale = WaveScale(direction);
        var p = direction * Scene.DetailScale;
        for (var i = 0; i < WaveCount; i++)
        {
            var (d, wavelength, share) = Waves[i];
            var a = Vector3.Dot(d, direction);
            var tangential = MathF.Sqrt(MathF.Max(1f - a * a, 0f));
            var k = 2f * MathF.PI / wavelength;
            var (sin, cos) = MathF.SinCos(Wrap(k * Vector3.Dot(d, p)));
            var weight = scale <= 0f ? 0f : scale * 0.5f * share * tangential;
            (sines[i], cosines[i]) = (weight * sin, weight * cos);
        }
    }

    /// <summary>
    /// The water's velocity at the surface in <paramref name="direction"/> (world space, metres a
    /// second, along the ground): each wave's orbital speed (its frequency times its height) along
    /// its own direction across the ground, in step with its height: forward under a crest, back
    /// in a trough, so with the rise and fall a floating thing goes round in a circle. It is what
    /// carries floating things about.
    /// </summary>
    public static Vector3 Flow(Vector3 direction, ReadOnlySpan<float> phases)
    {
        var scale = WaveScale(direction);
        if (scale <= 0f) return Vector3.Zero;
        var p = direction * Scene.DetailScale;
        var flow = Vector3.Zero;
        for (var i = 0; i < WaveCount; i++)
        {
            var (d, wavelength, share) = Waves[i];
            // The direction's part along the ground: its length is the wave's weight there, so
            // this is the weighted height and the way it runs in one.
            var across = d - Vector3.Dot(d, direction) * direction;
            var k = 2f * MathF.PI / wavelength;
            var omega = (float)Frequency(wavelength);
            flow += across * (omega * scale * 0.5f * share * MathF.Sin(Wrap(k * Vector3.Dot(d, p) - phases[i])));
        }

        return flow;
    }

    /// <summary>
    /// How fast the sea's surface in <paramref name="direction"/> is rising (falling, negative), in
    /// metres a second: <see cref="Height"/>'s rate as its phases turn. With <see cref="Flow"/>, how
    /// the water at the surface moves; only the physics' judging asks it (it isn't drawn).
    /// </summary>
    public static float Rise(Vector3 direction, ReadOnlySpan<float> phases)
    {
        var scale = WaveScale(direction);
        if (scale <= 0f) return 0f;
        var p = direction * Scene.DetailScale;
        var sum = 0f;
        for (var i = 0; i < WaveCount; i++)
        {
            var (d, wavelength, share) = Waves[i];
            var along = Vector3.Dot(d, direction);
            var tangential = MathF.Sqrt(MathF.Max(1f - along * along, 0f));
            var k = 2f * MathF.PI / wavelength;
            // Each phase grows at the wave's frequency, so its sine's argument falls at it.
            sum -= share * tangential * (float)Frequency(wavelength) * MathF.Cos(Wrap(k * Vector3.Dot(d, p) - phases[i]));
        }

        return scale * 0.5f * sum;
    }
}
