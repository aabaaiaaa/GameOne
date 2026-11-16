using System.Numerics;

namespace GameOne;

/// <summary>
/// The planet's ground: the one height function its physics, altitude, foundations, predicted
/// paths and drawing all read. A height is an elevation (metres above the sea, <see cref="Planet.SeaRadius"/>
/// from the centre) in a unit direction from the planet's centre: the baked map (<see cref="TerrainMap"/>)
/// plus fine detail made here, which follows the kind of ground under it. Code that can run at any
/// body reads its body's ground instead (<see cref="GroundBody.Height"/>, <see cref="GroundBody.SurfaceHeight"/>,
/// <see cref="GroundBody.Normal"/>), which on the planet is this.
/// </summary>
/// <remarks>
/// The planet shader repeats <see cref="Detail"/> in GLSL, so it is written to transcribe: ints,
/// uints and floats only, and the octaves as plain arrays.
/// </remarks>
internal static class Terrain
{
    private const int KindCount = 10; // SurfaceKind's values

    /// <summary>
    /// Loads the planet's map (<see cref="GroundBody.Planet"/> keeps it). The game can't run
    /// without it, so a missing map, or one made for another radius or Home's elevation
    /// (TerrainMap.Load refuses it), is an error, not a fallback to a plain sphere.
    /// </summary>
    internal static TerrainMap LoadPlanetMap()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "terrain.bin.gz");
        if (!File.Exists(path))
            throw new FileNotFoundException($"the terrain map is missing ({path}): make it with tools/TerrainGen", path);
        return TerrainMap.Load(path);
    }

    /// <summary>The planet's baked map, loaded once, the first time it is needed.</summary>
    public static TerrainMap Map => GroundBody.Planet.Map;

    /// <summary>The map's height alone (metres above the sea) in <paramref name="direction"/>, a unit vector.</summary>
    public static float BaseHeight(Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        return Map.HeightAt(face, u, v);
    }

    /// <summary>The ground's height, with its fine detail: the land, or under the sea its bed.</summary>
    public static float Height(Vector3 direction) => PlanetHeight(direction);

    /// <summary>The planet's ground's height, with its fine detail (<see cref="GroundBody.Height"/> reads it straight, for the planet).</summary>
    public static float PlanetHeight(Vector3 direction)
    {
        // One face lookup and one set of texels for both the map's height and the detail: this is
        // read for every sample of the physics' tiles, and five times for each normal.
        var (face, u, v) = CubeSphere.ToFace(direction);
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        var map = GroundBody.Planet.Map;
        map.Texels(face, u, v, index, weight);
        var h = 0f;
        for (var i = 0; i < 4; i++) h += map.Metres(map.Heights[index[i]]) * weight[i];
        return h + Detail(direction, map, index, weight);
    }

    /// <inheritdoc cref="GroundBody.SurfaceHeight"/>
    public static float SurfaceHeight(Vector3 direction) => GroundBody.Planet.SurfaceHeight(direction);

    /// <inheritdoc cref="GroundBody.IsSea"/>
    public static bool IsSea(Vector3 direction) => GroundBody.Planet.IsSea(direction);

    /// <inheritdoc cref="GroundBody.Normal"/>
    public static Vector3 Normal(Vector3 direction) => GroundBody.Planet.Normal(direction);

    /// <summary>What the planet's ground is in <paramref name="direction"/>: the kind of the most heavily weighted of the four texels a read uses (the first, on a tie).</summary>
    public static SurfaceKind Kind(Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        var map = GroundBody.Planet.Map;
        map.Texels(face, u, v, index, weight);
        var best = 0;
        for (var i = 1; i < 4; i++)
            if (weight[i] > weight[best]) best = i;
        return (SurfaceKind)map.Kinds[index[best]];
    }

    /// <summary>
    /// The planet's fine detail in <paramref name="direction"/>, in metres: each of the four texels a read uses
    /// adds its weight times its kind's octaves (<see cref="Scene.TerrainDetail"/>), so the detail
    /// blends across a change of kind, and is exactly 0 where all four are the site.
    /// </summary>
    public static float Detail(Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        var map = GroundBody.Planet.Map;
        map.Texels(face, u, v, index, weight);
        return Detail(direction, map, index, weight);
    }

    /// <summary>The fine detail, from the four texels (<see cref="TerrainMap.Texels"/>) already found for <paramref name="direction"/>.</summary>
    private static float Detail(Vector3 direction, TerrainMap map, ReadOnlySpan<int> index, ReadOnlySpan<float> weight)
    {
        // Each kind's total weight, so a kind shared by several texels is worked out once.
        Span<float> kindWeight = stackalloc float[KindCount];
        kindWeight.Clear();
        for (var i = 0; i < 4; i++)
            kindWeight[map.Kinds[index[i]]] += weight[i];

        var p = direction * Scene.DetailScale;
        var detail = 0f;
        for (var kind = 0; kind < KindCount; kind++)
        {
            if (kindWeight[kind] <= 0f) continue;
            var octaves = Scene.TerrainDetail[kind];
            var sum = 0f;
            for (var o = 0; o < octaves.Length; o++)
                sum += octaves[o].Amplitude * Noise(p / octaves[o].Wavelength);
            detail += kindWeight[kind] * sum;
        }

        return detail;
    }

    /// <summary>Value noise at <paramref name="q"/> (lattice units): trilinear between the lattice's corners, quintic-smoothed, in [-1, 1].</summary>
    internal static float Noise(Vector3 q)
    {
        var fx = MathF.Floor(q.X);
        var fy = MathF.Floor(q.Y);
        var fz = MathF.Floor(q.Z);
        int i = (int)fx, j = (int)fy, k = (int)fz;
        var sx = Quintic(q.X - fx);
        var sy = Quintic(q.Y - fy);
        var sz = Quintic(q.Z - fz);

        var x00 = Lerp(Lattice(i, j, k), Lattice(i + 1, j, k), sx);
        var x10 = Lerp(Lattice(i, j + 1, k), Lattice(i + 1, j + 1, k), sx);
        var x01 = Lerp(Lattice(i, j, k + 1), Lattice(i + 1, j, k + 1), sx);
        var x11 = Lerp(Lattice(i, j + 1, k + 1), Lattice(i + 1, j + 1, k + 1), sx);
        var value = Lerp(Lerp(x00, x10, sy), Lerp(x01, x11, sy), sz);
        return value * 2f - 1f;
    }

    /// <summary>
    /// The noise's value at a lattice point, in [0, 1]. The GLSL's hash must match exactly: the
    /// multiplies wrap as 32-bit ints (unchecked here, as GLSL's int arithmetic does), then are
    /// taken as uints. The lattice cell (<c>floor(p / wavelength)</c>) may come out one different
    /// on the GPU right on a cell boundary, where the noise is continuous, so it can't be seen.
    /// </summary>
    private static float Lattice(int i, int j, int k)
    {
        unchecked
        {
            var x = (uint)(i * 73856093) ^ (uint)(j * 19349663) ^ (uint)(k * 83492791);
            x ^= x >> 13;
            x *= 0x5bd1e995;
            x ^= x >> 15;
            return x / 4294967296f;
        }
    }

    private static float Quintic(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
