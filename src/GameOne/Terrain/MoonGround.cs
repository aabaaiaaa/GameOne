using System.Numerics;

namespace GameOne;

/// <summary>What the moon's ground is at a texel of its map (the map's kinds byte): it sets how pale it is (<see cref="Scene.MoonAlbedos"/>).</summary>
internal enum MoonKind : byte
{
    Plain,
    Rim,   // a big crater's raised rim
    Ray,   // the rock thrown out from a big crater in streaks across the plains
    Floor, // a big crater's floor, or the canyon's
    SmallRim, // a larger small crater's rim, faintly pale, so the crater field reads from far off
}

/// <summary>
/// The moon's ground, as <see cref="Terrain"/> is the planet's: its baked map
/// (<c>Assets/moon.bin.gz</c>, made by tools/TerrainGen's moon mode) plus fine detail. Every
/// direction here is moon-fixed (<see cref="SolarSystem.ToMoon"/>: the axes the planet's map has,
/// north (0, 0, -1), latitude 0 longitude 0 the near side's middle), a unit vector from the moon's
/// centre, and every height is in metres above the datum sphere of <see cref="Scene.MoonRadius"/>.
/// </summary>
/// <remarks>
/// <see cref="Detail"/> and <see cref="SunSeenOver"/> are for the moon's GLSL to repeat, so they
/// are written to transcribe: floats only, a fixed number of steps, no allocation or recursion.
/// Change one and change the other.
/// </remarks>
internal static class MoonGround
{
    // Loaded on first use; a missing map, or one made for another radius (TerrainMap.Load refuses
    // it), is an error, not a fallback to a plain sphere.
    private static readonly Lazy<TerrainMap> Loaded = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "moon.bin.gz");
        if (!File.Exists(path))
            throw new FileNotFoundException($"the moon's map is missing ({path}): make it with tools/TerrainGen --moon", path);
        return TerrainMap.Load(path, Scene.MoonRadius, 0f);
    });

    /// <summary>The moon's baked map, loaded once, the first time it is needed.</summary>
    public static TerrainMap Map => Loaded.Value;

    /// <summary>The ground's height in <paramref name="direction"/>: the map plus its fine detail; below 0 in the floors.</summary>
    public static float Height(Vector3 direction) => Height(Map, direction);

    /// <summary>Straight out of the ground in <paramref name="direction"/> (moon-fixed), from heights <see cref="Scene.TerrainNormalStep"/> apart each way across it.</summary>
    public static Vector3 Normal(Vector3 direction) => Normal(Map, direction);

    /// <summary>What the ground is in <paramref name="direction"/>: the kind of the most heavily weighted of the four texels a read uses (the first, on a tie).</summary>
    public static MoonKind Kind(Vector3 direction) => Kind(Map, direction);

    /// <summary>How pale the ground is in <paramref name="direction"/>: the four texels' albedos (<see cref="Scene.MoonAlbedos"/>) by their weights, so it changes smoothly from kind to kind.</summary>
    public static float Albedo(Vector3 direction) => Albedo(Map, direction);

    /// <summary>
    /// How much of the sun's disc is seen over the ground from the ground in
    /// <paramref name="direction"/> (0 to 1), the sun lying along <paramref name="sun"/> (a unit
    /// moon-fixed direction): the moon's own shadows, the rims' and the canyon's walls' long ones
    /// when the sun is low. Not the light's slant on the ground, which the normal gives.
    /// </summary>
    public static float SunSeenOver(Vector3 direction, Vector3 sun) => SunSeenOver(Map, direction, sun);

    /// <summary>The map's height alone in <paramref name="direction"/>.</summary>
    public static float BaseHeight(TerrainMap map, Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        return map.HeightAt(face, u, v);
    }

    /// <summary><see cref="Height"/> on a given map (TerrainGen's preview reads the map it has just made).</summary>
    public static float Height(TerrainMap map, Vector3 direction) => BaseHeight(map, direction) + Detail(direction);

    /// <summary><see cref="Normal"/> on a given map.</summary>
    public static Vector3 Normal(TerrainMap map, Vector3 direction)
    {
        // As Terrain.Normal: any two directions across the ground will do.
        var a = Vector3.Normalize(Vector3.Cross(MathF.Abs(direction.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, direction));
        var b = Vector3.Cross(direction, a);
        var half = Scene.TerrainNormalStep * 0.5f;
        var angle = half / Scene.MoonRadius;
        var span = 2f * half * (Scene.MoonRadius + Height(map, direction)) / Scene.MoonRadius;
        var slopeA = (Height(map, Vector3.Normalize(direction + a * angle)) - Height(map, Vector3.Normalize(direction - a * angle))) / span;
        var slopeB = (Height(map, Vector3.Normalize(direction + b * angle)) - Height(map, Vector3.Normalize(direction - b * angle))) / span;
        return Vector3.Normalize(direction - a * slopeA - b * slopeB);
    }

    /// <summary><see cref="Kind"/> on a given map.</summary>
    public static MoonKind Kind(TerrainMap map, Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        map.Texels(face, u, v, index, weight);
        var best = 0;
        for (var i = 1; i < 4; i++)
            if (weight[i] > weight[best]) best = i;
        return (MoonKind)map.Kinds[index[best]];
    }

    /// <summary><see cref="Albedo"/> on a given map.</summary>
    public static float Albedo(TerrainMap map, Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        map.Texels(face, u, v, index, weight);
        var albedo = 0f;
        for (var i = 0; i < 4; i++) albedo += Scene.MoonAlbedos[map.Kinds[index[i]]] * weight[i];
        return albedo;
    }

    /// <summary>The fine detail in <paramref name="direction"/>, in metres: <see cref="Scene.MoonDetail"/>'s octaves, the same on every kind of ground.</summary>
    public static float Detail(Vector3 direction)
    {
        var p = direction * Scene.MoonDetailScale;
        var detail = 0f;
        var octaves = Scene.MoonDetail;
        for (var o = 0; o < octaves.Length; o++)
            detail += octaves[o].Amplitude * Terrain.Noise(p / octaves[o].Wavelength);
        return detail;
    }

    /// <summary>
    /// <see cref="SunSeenOver"/> on a given map. A ray from just above the ground
    /// (<see cref="Scene.MoonShadowLift"/>) towards the sun is marched over the map's heights (the
    /// fine detail is too small to cast a shadow worth a GPU's reads) in
    /// <see cref="Scene.MoonShadowSteps"/> steps spaced out quadratically to
    /// <see cref="Scene.MoonShadowReach"/>, close together near the place, where a wall beside it
    /// matters most, and stopping once the ray is over the map's highest ground or the whole disc
    /// is hidden (the march's cost on the GPU, Shaders' <c>sunSeenOver</c>). The lowest the ray passes over the ground, as an angle seen from the place,
    /// against the sun's angular radius gives how much of the disc is above what blocks it.
    /// </summary>
    public static float SunSeenOver(TerrainMap map, Vector3 direction, Vector3 sun)
    {
        var start = direction * (Scene.MoonRadius + BaseHeight(map, direction) + Scene.MoonShadowLift);
        var lowest = float.MaxValue;
        for (var i = 1; i <= Scene.MoonShadowSteps; i++)
        {
            var f = (float)i / Scene.MoonShadowSteps;
            var along = Scene.MoonShadowReach * f * f;
            var at = start + sun * along;
            var reach = at.Length();
            // Above the map's highest ground the ray can only climb further from it (the ground
            // curves away below it): nothing further on can block it.
            if (reach - Scene.MoonRadius > map.MaxHeight) break;
            var clear = reach - Scene.MoonRadius - BaseHeight(map, at / reach);
            lowest = MathF.Min(lowest, clear / along);
            // The sun's whole disc is under what blocks it: nothing further on can make it less.
            if (lowest <= -SolarSystem.SunAngularRadius) break;
        }

        // The clearance over the distance is the angle's tangent; near the sun's edge, where the
        // softening matters, it is the angle itself.
        return Math.Clamp(0.5f + lowest / (2f * SolarSystem.SunAngularRadius), 0f, 1f);
    }
}
