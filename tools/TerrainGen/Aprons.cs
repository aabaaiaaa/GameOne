using System.Numerics;

namespace TerrainGen;

/// <summary>
/// Turns a grid's texels into a map's arrays: each face with its apron (the true surface just over
/// the edge, read from the next face), and heights quantised so 0 is exact. The planet's generator
/// and the moon's share it, so both maps meet across their faces' edges alike.
/// </summary>
internal static class Aprons
{
    /// <summary>The map's texels (<c>6 * (N + 2)^2</c>, as <see cref="GameOne.TerrainMap"/> lays them out) from the grid's.</summary>
    public static (float[] Metres, byte[] Kinds, byte[] Regions) Add(Grid grid, float[] height, byte[] kind, byte[] region)
    {
        var n = grid.N;
        var side = n + 2;
        var count = 6 * side * side;
        var metres = new float[count];
        var kinds = new byte[count];
        var regions = new byte[count];
        Parallel.For(0, 6 * side, row =>
        {
            var face = row / side;
            var tj = row % side;
            for (var ti = 0; ti < side; ti++)
            {
                var at = face * side * side + tj * side + ti;
                if (ti >= 1 && ti <= n && tj >= 1 && tj <= n)
                {
                    var src = grid.Index(face, ti - 1, tj - 1);
                    metres[at] = height[src];
                    kinds[at] = kind[src];
                    regions[at] = region[src];
                    continue;
                }

                // The apron: the next face's texel just over the edge. Across a cube edge the two
                // faces' rows line up (equal-angle coordinates agree along it), so with each face's
                // apron holding the other's edge texels, both read exactly the same surface at the edge.
                var near = grid.Texel(face, ti - 1, tj - 1);
                metres[at] = height[near];
                kinds[at] = kind[near];
                regions[at] = region[near];
            }
        });

        // The apron's corners. Only three faces meet at a cube corner, so there is no texel
        // diagonally over it: the nearest-texel guess leaves each face's corner read its own surface
        // there, up to half a metre apart. Each face's corner apron instead holds the mean of the
        // three faces' corner texels, so all three read that same mean at the corner itself, and
        // the same surface along each edge out of it. Its kind and region are those of the corner
        // texel whose height is nearest the mean (the first, on a tie), the same for all three
        // faces, so the kinds (and the detail they bring) agree at the corner too.
        Span<int> around = stackalloc int[3];
        for (var face = 0; face < 6; face++)
        {
            foreach (var (ti, tj) in new[] { (0, 0), (side - 1, 0), (0, side - 1), (side - 1, side - 1) })
            {
                var corner = GameOne.CubeSphere.FromFace(face, ti == 0 ? -1f : 1f, tj == 0 ? -1f : 1f);
                var faces = 0;
                for (var f = 0; f < 6; f++)
                {
                    foreach (var (ci, cj) in new[] { (0, 0), (n - 1, 0), (0, n - 1), (n - 1, n - 1) })
                    {
                        if (Vector3.Distance(GameOne.CubeSphere.FromFace(f, ci == 0 ? -1f : 1f, cj == 0 ? -1f : 1f), corner) > 1e-4f) continue;
                        if (faces == 3) throw new InvalidOperationException("more than three faces meet at a cube corner");
                        around[faces++] = grid.Index(f, ci, cj);
                    }
                }

                if (faces != 3) throw new InvalidOperationException($"{faces} faces meet at a cube corner, not 3");
                var mean = (height[around[0]] + height[around[1]] + height[around[2]]) / 3f;
                var nearest = around[0];
                for (var k = 1; k < 3; k++)
                    if (MathF.Abs(height[around[k]] - mean) < MathF.Abs(height[nearest] - mean)) nearest = around[k];

                var at = face * side * side + tj * side + ti;
                metres[at] = mean;
                kinds[at] = kind[nearest];
                regions[at] = region[nearest];
            }
        }

        return (metres, kinds, regions);
    }

    /// <summary>Heights in metres as the map's 16-bit levels, from <c>Min</c> to <c>Max</c>, with 0 exactly on a level, so a site levelled to 0 reads exactly 0.</summary>
    public static (ushort[] Heights, float Min, float Max) Quantise(float[] metres)
    {
        var lo = metres.Min();
        var hi = metres.Max();
        var step = (hi - lo) / 65535f * 1.0001f;
        var min = -MathF.Ceiling(-lo / step) * step;
        var max = min + 65535f * step;
        var heights = new ushort[metres.Length];
        Parallel.For(0, metres.Length, i =>
            heights[i] = (ushort)Math.Clamp(MathF.Round((metres[i] - min) / step), 0f, 65535f));
        return (heights, min, max);
    }
}
