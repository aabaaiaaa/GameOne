using System.Numerics;

namespace GameOne.Rendering;

/// <summary>
/// The planet's map as colours, for reading the ground's colour per pixel however far off: each
/// texel its kind's colour (<see cref="Scene.KindColors"/>), RGBA8, a layer per cube face, with a
/// full mip chain, each level's texel the average of the 2 by 2 under it, so a far pixel's filtered
/// read is the average of the ground it covers rather than a few texels' point reads (which
/// shimmer) or its triangle's corners' (which show the triangles).
/// </summary>
/// <remarks>
/// The faces' insides only, without the map's aprons, as <see cref="FaceLevels"/> lays them out.
/// The cost is at the faces' seams: clamped to the edge, the base's last half texel (7.5 m) reads
/// its own face's colour rather than blending into the next face's, and at coarse levels, as each
/// level's edge texel averages only its own face, the colour steps by about one of that level's
/// texels across a seam.
/// The texels are display values, as every colour is (the shaders convert with <c>toLinear</c>), in
/// a UNORM format; the levels are averaged in linear light (by <see cref="Scene.DisplayGamma"/>, <c>toLinear</c>'s power),
/// as a far pixel is the light of what it covers added up, then written back as display values.
/// The hardware's filtering between neighbouring texels is in display values, as the near reading's
/// hand blend (<c>kindColor</c>) is, so near and far agree where they meet.
/// </remarks>
internal sealed class GroundColors
{
    private GroundColors(int side, int levels, byte[] pixels, int[] offsets)
    {
        Side = side;
        Levels = levels;
        Pixels = pixels;
        _offsets = offsets;
    }

    private readonly int[] _offsets;

    /// <summary>The base level's side, in texels (the map's resolution).</summary>
    public int Side { get; }

    /// <summary>How many levels, down to 1 by 1.</summary>
    public int Levels { get; }

    /// <summary>Every level, base first, each its six faces in turn, row (v) after row, RGBA a texel: as the upload copies it.</summary>
    public byte[] Pixels { get; }

    /// <summary>Where level <paramref name="level"/> starts in <see cref="Pixels"/>.</summary>
    public int Offset(int level) => _offsets[level];

    /// <summary>Level <paramref name="level"/>'s side, in texels.</summary>
    public int LevelSide(int level) => Side >> level;

    /// <summary>Texel (<paramref name="i"/>, <paramref name="j"/>) of a face at a level, as display values in [0, 1].</summary>
    public Vector3 Texel(int level, int face, int i, int j)
    {
        var side = LevelSide(level);
        var at = Offset(level) + 4 * ((face * side + j) * side + i);
        return new Vector3(Pixels[at], Pixels[at + 1], Pixels[at + 2]) / 255f;
    }

    /// <summary>A display value in linear light, as the shaders' <c>toLinear</c>.</summary>
    public static Vector3 ToLinear(Vector3 c) =>
        new(MathF.Pow(c.X, Scene.DisplayGamma), MathF.Pow(c.Y, Scene.DisplayGamma), MathF.Pow(c.Z, Scene.DisplayGamma));

    /// <summary>A linear value back as a display value.</summary>
    public static Vector3 ToDisplay(Vector3 c) =>
        new(MathF.Pow(c.X, 1f / Scene.DisplayGamma), MathF.Pow(c.Y, 1f / Scene.DisplayGamma), MathF.Pow(c.Z, 1f / Scene.DisplayGamma));

    /// <summary>The colour map of <paramref name="map"/>, whose resolution must be a power of two.</summary>
    public static GroundColors Build(TerrainMap map)
    {
        const string what = "the ground's colour map";
        var n = map.Resolution;
        var levels = FaceLevels.Count(n, what);
        if (Scene.KindColors.Length != Enum.GetValues<SurfaceKind>().Length)
            throw new InvalidOperationException("Scene.KindColors needs a colour for every SurfaceKind");

        // Averaged in linear light, in floats, so the levels aren't quantised level by level.
        var linear = Scene.KindColors.Select(ToLinear).ToArray();
        var mapSide = n + 2;
        byte Kind(int face, int i, int j) => map.Kinds[(face * mapSide + j + 1) * mapSide + i + 1];
        var means = FaceLevels.Build(n, 3, (face, into) =>
        {
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    var c = linear[Kind(face, i, j)];
                    var at = 3 * (j * n + i);
                    (into[at], into[at + 1], into[at + 2]) = (c.X, c.Y, c.Z);
                }
        }, what, keepBase: false);

        var offsets = new int[levels];
        var total = 0;
        for (var k = 0; k < levels; k++)
        {
            offsets[k] = total;
            total += 6 * 4 * (n >> k) * (n >> k);
        }
        var pixels = new byte[total];
        // The base is the kinds' own colours, exactly; each level above it its means as display values.
        Parallel.For(0, 6, face =>
        {
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                    Write(pixels, offsets[0] + 4 * ((face * n + j) * n + i), Scene.KindColors[Kind(face, i, j)]);
        });
        for (var k = 1; k < levels; k++)
        {
            var (level, side) = (means[k], n >> k);
            var offset = offsets[k];
            Parallel.For(0, 6, face =>
            {
                for (int t = 3 * face * side * side, end = t + 3 * side * side, at = offset + 4 * face * side * side; t < end; t += 3, at += 4)
                    Write(pixels, at, ToDisplay(new Vector3(level[t], level[t + 1], level[t + 2])));
            });
        }
        return new GroundColors(n, levels, pixels, offsets);
    }

    private static void Write(byte[] pixels, int at, Vector3 c)
    {
        pixels[at] = Byte(c.X);
        pixels[at + 1] = Byte(c.Y);
        pixels[at + 2] = Byte(c.Z);
        pixels[at + 3] = 255;
    }

    private static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
}
