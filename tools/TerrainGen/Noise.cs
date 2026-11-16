using System.Numerics;

namespace TerrainGen;

/// <summary>
/// Seeded 3D gradient noise (Perlin's improved noise) and its sums, sampled on the sphere at
/// <c>direction * radius / wavelength</c>, so sizes are in metres whatever the radius. It only adds
/// detail and roughness for erosion to work on: the world's shapes come from the layout.
/// </summary>
internal sealed class Noise
{
    private readonly int[] _perm = new int[512];
    private readonly Vector3[] _offsets = new Vector3[64];

    public Noise(int seed)
    {
        var rng = new Random(seed);
        var p = Enumerable.Range(0, 256).ToArray();
        for (var i = 255; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        for (var i = 0; i < 512; i++) _perm[i] = p[i & 255];
        // Each field (warps, hills, cliffs...) reads the noise at its own far-off offset, so they don't line up.
        for (var i = 0; i < _offsets.Length; i++)
            _offsets[i] = new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * 1000f;
    }

    /// <summary>Noise in about [-1, 1].</summary>
    public float At(Vector3 p)
    {
        var xi = (int)MathF.Floor(p.X);
        var yi = (int)MathF.Floor(p.Y);
        var zi = (int)MathF.Floor(p.Z);
        var x = p.X - xi;
        var y = p.Y - yi;
        var z = p.Z - zi;
        xi &= 255;
        yi &= 255;
        zi &= 255;
        var u = Fade(x);
        var v = Fade(y);
        var w = Fade(z);
        var a = _perm[xi] + yi;
        var aa = _perm[a] + zi;
        var ab = _perm[a + 1] + zi;
        var b = _perm[xi + 1] + yi;
        var ba = _perm[b] + zi;
        var bb = _perm[b + 1] + zi;
        return Lerp(w,
            Lerp(v, Lerp(u, Grad(_perm[aa], x, y, z), Grad(_perm[ba], x - 1, y, z)),
                Lerp(u, Grad(_perm[ab], x, y - 1, z), Grad(_perm[bb], x - 1, y - 1, z))),
            Lerp(v, Lerp(u, Grad(_perm[aa + 1], x, y, z - 1), Grad(_perm[ba + 1], x - 1, y, z - 1)),
                Lerp(u, Grad(_perm[ab + 1], x, y - 1, z - 1), Grad(_perm[bb + 1], x - 1, y - 1, z - 1))));
    }

    /// <summary>Fractal sum of <paramref name="octaves"/> octaves of field <paramref name="field"/>, each half the size and strength of the last; about [-1, 1].</summary>
    public float Fbm(int field, Vector3 p, int octaves, float gain = 0.5f)
    {
        p += _offsets[field];
        float sum = 0f, amplitude = 1f, total = 0f;
        for (var o = 0; o < octaves; o++)
        {
            sum += At(p) * amplitude;
            total += amplitude;
            amplitude *= gain;
            p = p * 2.03f + new Vector3(17.1f, 3.7f, 9.3f);
        }

        return sum / total * 1.6f;
    }

    /// <summary>Ridged sum: sharp crests where the noise crosses zero, in [0, 1].</summary>
    public float Ridged(int field, Vector3 p, int octaves)
    {
        p += _offsets[field];
        float sum = 0f, amplitude = 1f, total = 0f, weight = 1f;
        for (var o = 0; o < octaves; o++)
        {
            var r = 1f - MathF.Abs(At(p) * 1.4f);
            r = MathF.Max(r, 0f);
            r *= r * weight;
            weight = Math.Clamp(r * 1.5f, 0f, 1f);
            sum += r * amplitude;
            total += amplitude;
            amplitude *= 0.5f;
            p = p * 2.03f + new Vector3(17.1f, 3.7f, 9.3f);
        }

        return sum / total;
    }

    /// <summary>A smooth displacement (each component about [-1, 1]) for domain warping.</summary>
    public Vector3 Warp(int field, Vector3 p, int octaves) =>
        new(Fbm(field, p, octaves), Fbm(field + 1, p, octaves), Fbm(field + 2, p, octaves));

    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static float Lerp(float t, float a, float b) => a + t * (b - a);

    private static float Grad(int hash, float x, float y, float z)
    {
        var h = hash & 15;
        var u = h < 8 ? x : y;
        var v = h < 4 ? y : h is 12 or 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
