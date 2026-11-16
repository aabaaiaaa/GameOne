namespace TerrainGen;

/// <summary>
/// Erodes the land as running water and slumping slopes would, on the whole cube sphere at once
/// (flow crosses face edges like any other texel step), so valleys and ridges come out carved
/// rather than as noise. Each iteration:
/// <list type="number">
/// <item>fills every hollow to its spill point (a priority flood from the sea, Barnes et al. 2014),
/// as sediment would, so all water reaches the sea, and orders the land from the sea up;</item>
/// <item>routes each texel's water to its two steepest neighbours downhill and sums the area draining
/// through every texel;</item>
/// <item>cuts each texel down towards where it drains by stream power (incision rate
/// <c>K A^0.5 S</c>, once past a channel head), against an uplift back towards the layout's shape,
/// solved implicitly from the sea upward (Braun and Willett 2013): ridges, which drain almost
/// nothing, keep the layout's heights while valleys are cut deeper the more water runs down them;</item>
/// <item>softens the slopes (diffusion) and lets any too steep for loose ground slump (talus).</item>
/// </list>
/// Everything is either per texel in parallel (each writing only its own texel) or a sequential
/// sweep in a fixed order, so the result is the same every run.
/// </summary>
internal static class Erosion
{
    // A hollow is filled to just above its spill point, rising this much per texel towards it, so
    // the water on the filled surface still runs one way.
    private const float FillStep = 1e-3f;

    // How strongly water favours the steeper of its two ways down.
    private const float FlowExponent = 2f;

    /// <summary>Erodes <paramref name="height"/> (land only: water texels are the sea, fixed at <paramref name="seaLevel"/> as base level). Returns each texel's drainage area, m².</summary>
    public static float[] Run(Grid grid, float[] height, bool[] water, float seaLevel, float minLand, ErosionLayout settings, int iterations, float incision, float diffusion,
        Action<string> log)
    {
        var count = grid.Count;
        var order = new int[count];
        // Each texel's water goes to its two steepest lower neighbours, the share to the first in
        // "split": with only the steepest (D8), valleys line up with the grid's eight directions.
        var first = new int[count];
        var second = new int[count];
        var split = new float[count];
        var area = new float[count];
        var scratch = new float[count];
        var tanTalus = MathF.Tan(settings.Talus * MathF.PI / 180f);
        // How far each iteration moves towards rest: the iterations together go about four times over.
        var uplift = 4f / iterations;
        var shape = (float[])height.Clone();
        // Water cuts a channel only once this much drains through: smaller slopes are shaped by the
        // hillslope processes alone, so valleys are spaced as real ones are, not one per texel.
        var channelHead = MathF.Sqrt(settings.ChannelArea);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var land = Fill(grid, height, water, seaLevel, order);

            // On the filled surface every lower neighbour is nearer the sea in the order.
            Parallel.For(0, land, o =>
            {
                var i = order[o];
                int a = i, b = i;
                float sa = 0f, sb = 0f;
                for (var k = 0; k < 8; k++)
                {
                    var n = grid.Neighbour(i, k);
                    var below = water[n] ? seaLevel : height[n];
                    var slope = (height[i] - below) / grid.Distance(i, n);
                    if (slope > sa)
                    {
                        (b, sb) = (a, sa);
                        (a, sa) = (n, slope);
                    }
                    else if (slope > sb)
                    {
                        (b, sb) = (n, slope);
                    }
                }

                first[i] = a;
                second[i] = sb > 0f ? b : a;
                var wa = MathF.Pow(sa, FlowExponent);
                var wb = sb > 0f ? MathF.Pow(sb, FlowExponent) : 0f;
                split[i] = sa > 0f ? wa / (wa + wb) : 1f;
            });

            // Drainage area, summed from the ridges down.
            for (var o = 0; o < land; o++) area[order[o]] = grid.Areas[order[o]];
            for (var o = land - 1; o >= 0; o--)
            {
                var i = order[o];
                if (first[i] == i) continue;
                if (!water[first[i]]) area[first[i]] += area[i] * split[i];
                if (!water[second[i]]) area[second[i]] += area[i] * (1f - split[i]);
            }

            // Stream power against uplift towards the authored shape, implicit (n = 1):
            // h' = (h + u base + F h'_r) / (1 + u + F), with h'_r the two receivers' new heights
            // shared as the water is, from the sea up so they are already done. At rest,
            // h = base - (incision) (sqrt(A) - sqrt(A_head)) S: ridges, draining almost nothing, keep
            // the layout's heights while channels cut valleys, deeper the more they drain.
            for (var o = 0; o < land; o++)
            {
                var i = order[o];
                if (first[i] == i) continue;
                var w = split[i];
                var below = w * (water[first[i]] ? seaLevel : height[first[i]]) +
                            (1f - w) * (water[second[i]] ? seaLevel : height[second[i]]);
                var run = w * grid.Distance(i, first[i]) + (1f - w) * grid.Distance(i, second[i]);
                var f = uplift * incision * MathF.Max(MathF.Sqrt(area[i]) - channelHead, 0f) / run;
                height[i] = MathF.Max((height[i] + uplift * shape[i] + f * below) / (1f + uplift + f), minLand);
            }

            Soften(grid, height, water, seaLevel, minLand, diffusion, tanTalus, scratch);
            if ((iteration + 1) % 10 == 0) log($"  erosion at {grid.N}: {iteration + 1}/{iterations}");
        }

        Fill(grid, height, water, seaLevel, order);
        return area;
    }

    /// <summary>
    /// Fills hollows in the land (in place) and writes the land texels to <paramref name="order"/>
    /// from the sea up, each after the one it drains into. Returns how many land texels there are.
    /// </summary>
    private static int Fill(Grid grid, float[] height, bool[] water, float seaLevel, int[] order)
    {
        var count = grid.Count;
        var done = new bool[count];
        var heap = new Heap(1 << 20);
        for (var i = 0; i < count; i++)
        {
            if (!water[i]) continue;
            done[i] = true;
            for (var k = 0; k < 8; k++)
            {
                if (water[grid.Neighbour(i, k)]) continue;
                heap.Push(seaLevel, i);
                break;
            }
        }

        var land = 0;
        while (heap.TryPop(out var level, out var c))
        {
            if (!water[c]) order[land++] = c;
            for (var k = 0; k < 8; k++)
            {
                var n = grid.Neighbour(c, k);
                if (done[n]) continue;
                done[n] = true;
                height[n] = MathF.Max(height[n], level + FillStep);
                heap.Push(height[n], n);
            }
        }

        return land;
    }

    /// <summary>
    /// Hillslope diffusion (each land texel moves <paramref name="diffusion"/> of the way to its
    /// four sides' mean), then talus: a texel standing steeper than the talus angle above its
    /// lowest side neighbour slumps half the excess (into the valley, where the water carries it
    /// off). Jacobi steps (read one array, write another), so the parallel loop is the same every run.
    /// </summary>
    private static void Soften(Grid grid, float[] height, bool[] water, float seaLevel, float minLand, float diffusion,
        float tanTalus, float[] scratch)
    {
        Parallel.For(0, grid.Count, i =>
        {
            if (water[i])
            {
                scratch[i] = height[i];
                return;
            }

            var mean = 0f;
            var lowest = float.MaxValue;
            var lowestDistance = 1f;
            for (var k = 0; k < 4; k++)
            {
                var n = grid.Neighbour(i, k);
                var h = water[n] ? MathF.Max(height[n], seaLevel) : height[n];
                mean += h;
                if (h < lowest)
                {
                    lowest = h;
                    lowestDistance = grid.Distance(i, n);
                }
            }

            var soft = height[i] + diffusion * (mean * 0.25f - height[i]);
            var limit = lowest + tanTalus * lowestDistance;
            if (soft > limit) soft -= 0.5f * (soft - limit);
            scratch[i] = MathF.Max(soft, minLand);
        });

        Array.Copy(scratch, height, grid.Count);
    }
}
