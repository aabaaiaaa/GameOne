using System.Globalization;
using GameOne.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The waterline far off (<see cref="Waterline"/>): its levels the mean of the clamped heights under
/// them, the level the shader picks for a spacing (none near, so near ground draws its water by its
/// own heights as before), and on the planet's map, the ice sheets' edges drawn where they are
/// rather than in steps along the squares.
/// </summary>
public sealed class WaterlineTests(ITestOutputHelper output)
{
    private static readonly Lazy<Waterline> Planet = new(() => Waterline.Build(Terrain.Map));

    [Fact]
    public void Each_level_averages_the_clamped_heights_under_it_on_a_small_map()
    {
        // An 8 by 8 face of made-up heights, some far past the clamp, some within it: every texel of
        // every level, down to the one texel that is the whole face's mean.
        const int resolution = 8, side = resolution + 2, count = 6 * side * side;
        var random = new Random(3);
        var heights = new ushort[count];
        for (var t = 0; t < count; t++)
            heights[t] = (ushort)(random.Next(2) == 0 ? random.Next(65536) : 32768 + random.Next(-200, 200));
        var map = new TerrainMap(resolution, Scene.PlanetRadius, Scene.HomeElevation, -100f, 100f, heights, new byte[count], new byte[count], []);

        var waterline = Waterline.Build(map);
        Assert.Equal(4, waterline.Levels);
        Assert.Equal(2 * 6 * (64 + 16 + 4 + 1), waterline.Pixels().Length);
        for (var level = 0; level < waterline.Levels; level++)
            for (var face = 0; face < 6; face++)
                for (var j = 0; j < resolution >> level; j++)
                    for (var i = 0; i < resolution >> level; i++)
                        Assert.Equal(BlockMean(map, level, face, i, j), waterline.Texel(level, face, i, j), 1e-3f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void Each_level_averages_the_clamped_heights_under_it_on_the_planets_map(int level)
    {
        var waterline = Planet.Value;
        Assert.Equal(11, waterline.Levels); // 1024 down to 1
        var random = new Random(level);
        for (var n = 0; n < 12; n++)
        {
            int face = random.Next(6), i = random.Next(waterline.Side >> level), j = random.Next(waterline.Side >> level);
            Assert.Equal(BlockMean(Terrain.Map, level, face, i, j), waterline.Texel(level, face, i, j), 1e-3f);
        }
    }

    [Fact]
    public void Near_ground_draws_its_water_by_its_own_heights()
    {
        // Squares two texels apart or closer read no level: the corner's own depth, with the
        // seabed's push over its first 2 m, to the bit, the near ground's own. The main view's
        // squares are that fine out to about 2.9 km, a small view's (coarser) to about 1.2 km.
        var texel = Waterline.TexelMetres(Terrain.Map.Resolution);
        Assert.Equal(GroundBody.PlanetSeaRadius * MathF.PI / 2f / 1024f, texel, 1e-3f); // about 15.3 m
        Assert.Equal(0f, Waterline.Level(2f * texel, Terrain.Map.Resolution), 1e-5f);
        foreach (var spacing in new[] { Scene.TerrainFinestSpacing, 1f, 10f, 2f * texel * 0.999f })
        {
            Assert.True(Waterline.Level(spacing, Terrain.Map.Resolution) <= 0f, $"{spacing} m reads a level");
            Assert.Equal((-3.25f, 2f), Planet.Value.Depth(0, 0.1f, 0.2f, spacing, -3.25f));
            Assert.Equal((0.125f, 2f), Planet.Value.Depth(2, -0.5f, 0.7f, spacing, 0.125f));
        }

        // Coarser, it blends over one level to the clamped levels' reading, and the push over the clamp.
        var far = Planet.Value.Depth(0, 0.1f, 0.2f, 4f * texel, -3.25f);
        Assert.Equal(-Planet.Value.Sample(0, 0.1f, 0.2f, 1f), far.Depth, 1e-6f);
        Assert.Equal(Waterline.Reach, far.PushOver, 1e-6f);
    }

    [Fact]
    public void The_shaders_level_and_numbers_are_the_csharps()
    {
        var source = Shaders.PlanetVertex(Terrain.Map);
        Assert.Contains("float waterlineLevel(float spacing) { return log2(spacing / WATERLINE_TEXEL) - 1.0; }", source);
        Assert.Contains($"#define WATERLINE_TEXEL {Waterline.TexelMetres(Terrain.Map.Resolution).ToString("R", CultureInfo.InvariantCulture)}", source);
        Assert.Contains($"#define WATERLINE_REACH {Waterline.Reach.ToString("R", CultureInfo.InvariantCulture)}", source);
        Assert.Contains("#define WATERLINE_NEAR_PUSH 2.0", source);
        Assert.Contains("float waterline = -height, pushOver = WATERLINE_NEAR_PUSH;", source);
        Assert.Contains("if (level > 0.0)", source);
        Assert.Contains("textureLod(uWaterline, vec3((uv + 1.0) * 0.5, float(face)), level).r", source);
        // The seabed's push and the water's discard read the same waterline.
        Assert.Contains("SEABED_PUSH * clamp(waterline / pushOver, 0.0, 1.0)", source);
        Assert.Contains("vWaterline = waterline;", source);
        Assert.Matches(@"if \(vWaterline < 0\.0\)\s*discard;", Shaders.SceneFragment(Terrain.Map));
        Assert.Matches(@"layout\(set = 0, binding = 11\) uniform sampler2DArray uWaterline;", source);
    }

    [Fact]
    public void An_ice_sheets_edge_is_drawn_where_it_is_far_off()
    {
        // The squares of a patch of a face's half (244.6 m, as the main view draws them some 23 km
        // off, or a small view some 9 km off), and of a quarter: along each of their lines across
        // both sheets, where the drawn waterline crosses between two corners (by the corners'
        // waterlines, blended linearly as the GPU blends them across a square) against where the
        // map's own heights cross the sea level. Read by the corners' own heights instead (the
        // stair), it is up to most of a square off, in and out by turns: the steps. The shader
        // works each corner's spacing out from its distance, so within a patch's band of distances
        // it reads up to about half a level either side of the square's own: each is checked at
        // the level the square's size asks, and half a level finer and coarser, a little looser
        // there (half a level coarser averages over a whole square, so smooths a tongue further:
        // still well inside the stair's).
        List<string> faults = [];
        float[] offsets = [-0.5f, 0f, 0.5f];
        foreach (var size in new[] { 1f / 32f, 1f / 64f })
        {
            var (drawns, corners) = Crossings(size, offsets);
            for (var o = 0; o < offsets.Length; o++)
            {
                var (offset, drawn) = (offsets[o], drawns[o]);
                var name = $"{size * GroundBody.PlanetSeaRadius * MathF.PI / 4f:F1} m squares, level {offset:+0.0;-0.0;0}";
                output.WriteLine($"{name}: {drawn.Count} crossings, " +
                    $"drawn mean {drawn.Average():F3} 90% {Percentile(drawn, 0.9f):F3} worst {drawn.Max():F3}; " +
                    $"by the corners' heights mean {corners.Average():F3} 90% {Percentile(corners, 0.9f):F3} worst {corners.Max():F3} (squares)");
                var (mean, tenth, worst) = offset == 0f ? (0.15f, 0.25f, 0.5f) : (0.2f, 0.3f, 0.6f);
                if (drawn.Count < 30) faults.Add($"{name}: only {drawn.Count} crossings");
                if (Percentile(corners, 0.9f) <= 0.5f) faults.Add($"{name}: the corners' heights don't step: the test doesn't see the fault");
                if (drawn.Average() >= mean) faults.Add($"{name}: the waterline is {drawn.Average():F3} of a square off on average");
                if (Percentile(drawn, 0.9f) > tenth) faults.Add($"{name}: a tenth of the waterline is over {Percentile(drawn, 0.9f):F3} of a square off");
                if (drawn.Max() > worst) faults.Add($"{name}: the waterline is {drawn.Max():F3} of a square off");
            }
        }
        Assert.True(faults.Count == 0, string.Join("; ", faults));
    }

    /// <summary>
    /// How far off (in squares, along the line) each true crossing of the sheets' edges is from the
    /// nearest drawn one, and from the nearest by the corners' own heights, on the grid of
    /// <paramref name="size"/> (in face coordinates) as patches lay it, along lines that cross a
    /// sheet in and out (the crossings by a sheet's ice, not a shore's), their true crossings a
    /// square and a half apart or more (so each is matched with its own; a line that only grazes a
    /// tongue, in and out within a hundredth of a square, is seen crossing once, and left out), the
    /// corners' waterlines read at each of <paramref name="levelOffsets"/> levels off the one the
    /// square's size asks (a list of the drawn ones' for each). So the narrow tongues and bights, under a square and a half across, are not measured:
    /// no averaging over a square can keep them, and what this checks is that the edge doesn't step.
    /// </summary>
    private static (List<float>[] Drawn, List<float> Corners) Crossings(float size, float[] levelOffsets)
    {
        var map = Terrain.Map;
        var waterline = Planet.Value;
        var spacings = levelOffsets.Select(o => size * GroundBody.PlanetSeaRadius * 0.7853982f * MathF.Pow(2f, o)).ToArray();
        var drawn = levelOffsets.Select(_ => new List<float>()).ToArray();
        List<float> corners = [];
        foreach (var sea in new[] { "Boreal Sea", "Austral Sea" })
        {
            var (centre, pack, _) = IceTests.Layout(sea);
            var (face, cu, cv) = CubeSphere.ToFace(centre);
            var reach = 4f * pack / (GroundBody.PlanetSeaRadius * 0.7853982f);
            int first = (int)MathF.Floor((Math.Min(cu, cv) - reach) / size), last = (int)MathF.Ceiling((Math.Max(cu, cv) + reach) / size);
            foreach (var along in new[] { true, false })
                for (var line = first; line <= last; line++)
                {
                    (float U, float V) At(float k) => along ? (k * size, line * size) : (line * size, k * size);
                    List<float> trues = [], heights = [];
                    var waterlines = spacings.Select(_ => new List<float>()).ToArray();
                    for (var k = first; k < last; k++)
                    {
                        var (u0, v0) = At(k);
                        var (u1, v1) = At(k + 1);
                        for (var o = 0; o < spacings.Length; o++)
                        {
                            float a = waterline.Depth(face, u0, v0, spacings[o], -map.HeightAt(face, u0, v0)).Depth;
                            float b = waterline.Depth(face, u1, v1, spacings[o], -map.HeightAt(face, u1, v1)).Depth;
                            if (a >= 0f != b >= 0f) waterlines[o].Add(k + a / (a - b));
                        }
                        float ha = map.HeightAt(face, u0, v0), hb = map.HeightAt(face, u1, v1);
                        if (ha >= 0f != hb >= 0f) heights.Add(k + ha / (ha - hb));
                        var before = ha;
                        for (var q = 1; q <= 100; q++)
                        {
                            var (u, v) = At(k + q / 100f);
                            var h = map.HeightAt(face, u, v);
                            if (before >= 0f != h >= 0f) trues.Add(k + (q - 0.5f) / 100f);
                            before = h;
                        }
                    }
                    if (trues.Count == 0 || trues.Count % 2 != 0 || trues.Zip(trues.Skip(1)).Any(p => p.Second - p.First < 1.5f)) continue;
                    foreach (var t in trues)
                    {
                        var (tu, tv) = At(t);
                        if (!OnIce(map, face, tu, tv)) continue; // a shore, not a sheet's edge
                        for (var o = 0; o < spacings.Length; o++)
                            drawn[o].Add(waterlines[o].Count == 0 ? float.PositiveInfinity : waterlines[o].Min(d => MathF.Abs(d - t)));
                        corners.Add(heights.Count == 0 ? float.PositiveInfinity : heights.Min(d => MathF.Abs(d - t)));
                    }
                }
        }
        return (drawn, corners);
    }

    /// <summary>Whether a sheet's ice is among the four texels a read at a point blends.</summary>
    private static bool OnIce(TerrainMap map, int face, float u, float v)
    {
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        map.Texels(face, u, v, index, weight);
        foreach (var i in index)
            if (map.Kinds[i] == (byte)SurfaceKind.PackIce) return true;
        return false;
    }

    private static float Percentile(List<float> values, float share) => values.Order().ElementAt((int)(share * (values.Count - 1)));

    /// <summary>The mean of the map's heights, each clamped to ±<see cref="Waterline.Reach"/>, in the 2^level square of the base under a level's texel.</summary>
    private static float BlockMean(TerrainMap map, int level, int face, int i, int j)
    {
        var size = 1 << level;
        var side = map.Resolution + 2;
        var sum = 0.0;
        for (var y = j * size; y < (j + 1) * size; y++)
            for (var x = i * size; x < (i + 1) * size; x++)
                sum += Math.Clamp(map.Metres(map.Heights[(face * side + y + 1) * side + x + 1]), -Waterline.Reach, Waterline.Reach);
        return (float)(sum / (size * size));
    }
}
