using System.Numerics;

namespace GameOne.Rendering;

/// <summary>
/// A mip chain over the six cube faces' insides (a map's faces without their aprons), for the
/// maps the shaders read filtered however far off (<see cref="GroundColors"/>,
/// <see cref="Waterline"/>, <see cref="SeaColors"/>): each level's texel the mean of the 2 by 2
/// under it, so it is the mean of the 2^k by 2^k block of the base under it, down to one texel a
/// face. The faces' sides must be a power of two, so every level halves exactly and its texels
/// sit over whole blocks (texel i of the base is the map's texel i + 1). As each level's edge
/// texel averages only its own face, the values step by about one of that level's texels across
/// a seam between faces.
/// </summary>
internal static class FaceLevels
{
    /// <summary>How many levels a face <paramref name="side"/> texels square has, down to 1 by 1; refuses a side that isn't a power of two.</summary>
    public static int Count(int side, string what)
    {
        if (side < 1 || (side & (side - 1)) != 0)
            throw new InvalidDataException($"{what} needs a power-of-two resolution, not {side}");
        return BitOperations.Log2((uint)side) + 1;
    }

    /// <summary>
    /// Every level of <paramref name="channels"/> floats a texel: <paramref name="fill"/> writes a
    /// face's base (row (v) after row, its channels in turn), and each level above it is averaged
    /// from the one below, the faces in parallel. Level k is laid out face after face, as the base is.
    /// Without <paramref name="keepBase"/> the base is left empty (for a map that writes its own
    /// base, so it isn't held twice): each face's is kept only while its levels are made.
    /// </summary>
    public static float[][] Build(int side, int channels, Action<int, float[]> fill, string what, bool keepBase = true)
    {
        var count = Count(side, what);
        var levels = new float[count][];
        for (var k = 0; k < count; k++)
            levels[k] = k == 0 && !keepBase ? [] : new float[6 * channels * (side >> k) * (side >> k)];
        Parallel.For(0, 6, face =>
        {
            var base_ = new float[channels * side * side];
            fill(face, base_);
            if (keepBase)
                base_.CopyTo(levels[0], face * base_.Length);
            for (int k = 1, s = side / 2; k < count; k++, s /= 2)
            {
                // Level 1 from this face's own base (held whole or not); the rest from the level below.
                var below = k == 1 ? base_ : levels[k - 1];
                var above = levels[k];
                int wide = 2 * s, from = k == 1 ? 0 : face * channels * wide * wide, to = face * channels * s * s;
                for (var j = 0; j < s; j++)
                    for (var i = 0; i < s; i++)
                        for (var c = 0; c < channels; c++)
                        {
                            var at = from + channels * (2 * j * wide + 2 * i) + c;
                            above[to + channels * (j * s + i) + c] = 0.25f *
                                (below[at] + below[at + channels] + below[at + channels * wide] + below[at + channels * (wide + 1)]);
                        }
            }
        });
        return levels;
    }

    /// <summary>
    /// Where the GPU's bilinear read, clamped to the edge, at face coordinates (<paramref name="u"/>,
    /// <paramref name="v"/>) falls on a level <paramref name="side"/> texels square (by texel
    /// centres): the two columns and two rows it blends (clamped to the face) and how far across
    /// each it is, for <see cref="Mix(float, float, float, float, float, float)"/>.
    /// </summary>
    public static (int I0, int I1, int J0, int J1, float X, float Y) Bilinear(float u, float v, int side)
    {
        float x = (u + 1f) * 0.5f * side - 0.5f, y = (v + 1f) * 0.5f * side - 0.5f;
        int i = (int)MathF.Floor(x), j = (int)MathF.Floor(y);
        return (Math.Clamp(i, 0, side - 1), Math.Clamp(i + 1, 0, side - 1), Math.Clamp(j, 0, side - 1), Math.Clamp(j + 1, 0, side - 1), x - i, y - j);
    }

    /// <summary>The bilinear blend of the four texels a <see cref="Bilinear"/> read takes (at (I0, J0), (I1, J0), (I0, J1), (I1, J1)), <paramref name="x"/> and <paramref name="y"/> across.</summary>
    public static float Mix(float a00, float a10, float a01, float a11, float x, float y) =>
        (a00 * (1f - x) + a10 * x) * (1f - y) + (a01 * (1f - x) + a11 * x) * y;

    /// <inheritdoc cref="Mix(float, float, float, float, float, float)"/>
    public static Vector3 Mix(Vector3 a00, Vector3 a10, Vector3 a01, Vector3 a11, float x, float y) =>
        (a00 * (1f - x) + a10 * x) * (1f - y) + (a01 * (1f - x) + a11 * x) * y;
}
