using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using GameOne.Rendering;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The ground's colour map (<see cref="GroundColors"/>): its base level the kinds' colours the GLSL
/// has, and each level above it the average, in linear light, of the block of the base under it.
/// </summary>
public sealed class GroundColorsTests
{
    private static readonly Lazy<GroundColors> Planet = new(() => GroundColors.Build(Terrain.Map));

    [Fact]
    public void The_glsls_kind_colours_are_the_scenes()
    {
        // The GLSL's table is written from Scene.KindColors, which the colour map's base is made
        // from: read back, every entry is the same float, one for each kind.
        var source = Shaders.SceneFragment(Terrain.Map);
        var table = Regex.Match(source, @"const vec3 kindColors\[KIND_COUNT\] = vec3\[\]\((?<body>.*?)\);").Groups["body"].Value;
        var colors = Regex.Matches(table, @"vec3\(([^,]+), ([^,]+), ([^)]+)\)")
            .Select(m => new Vector3(Parse(m.Groups[1].Value), Parse(m.Groups[2].Value), Parse(m.Groups[3].Value)))
            .ToArray();
        Assert.Equal(Enum.GetValues<SurfaceKind>().Length, Scene.KindColors.Length);
        Assert.Equal(Scene.KindColors, colors);
        Assert.Contains($"#define KIND_COUNT {Scene.KindColors.Length}", source);
        // The GLSL's toLinear and finish use the same power as the map's averaging.
        Assert.Contains($"#define DISPLAY_GAMMA {Scene.DisplayGamma.ToString("R", CultureInfo.InvariantCulture)}", source);
        Assert.Contains("pow(max(c, vec3(0.0)), vec3(DISPLAY_GAMMA))", source);
    }

    [Fact]
    public void The_base_level_is_each_texels_kind_colour()
    {
        var map = Terrain.Map;
        var colors = Planet.Value;
        Assert.Equal(map.Resolution, colors.Side);
        Assert.Equal(11, colors.Levels); // 1024 down to 1
        var side = map.Resolution + 2;
        var seen = new HashSet<byte>();
        var random = new Random(5);
        for (var n = 0; n < 4000; n++)
        {
            int face = random.Next(6), i = random.Next(colors.Side), j = random.Next(colors.Side);
            var kind = map.Kinds[(face * side + j + 1) * side + i + 1];
            seen.Add(kind);
            AssertNear(Scene.KindColors[kind], colors.Texel(0, face, i, j), 0.5f / 255f);
        }
        Assert.True(seen.Count >= 6, $"only {seen.Count} kinds met"); // the spots cover the land's many kinds
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void Each_level_averages_the_block_of_the_base_under_it_on_the_planets_map(int level)
    {
        var colors = Planet.Value;
        var random = new Random(level);
        var levelSide = colors.LevelSide(level);
        for (var n = 0; n < 6; n++)
        {
            int face = random.Next(6), i = random.Next(levelSide), j = random.Next(levelSide);
            AssertNear(BlockMean(Terrain.Map, level, face, i, j), colors.Texel(level, face, i, j), 1f / 255f);
        }
    }

    [Fact]
    public void Each_level_averages_the_block_of_the_base_under_it_on_a_small_map()
    {
        // An 8 by 8 face of made-up kinds: every texel of every level, down to the one texel that is
        // the whole face's average.
        const int resolution = 8, side = resolution + 2;
        var count = 6 * side * side;
        var random = new Random(11);
        var kinds = new byte[count];
        for (var t = 0; t < count; t++)
            kinds[t] = (byte)random.Next(Scene.KindColors.Length);
        var map = new TerrainMap(resolution, Scene.PlanetRadius, Scene.HomeElevation, -100f, 100f,
            new ushort[count], kinds, new byte[count], []);

        var colors = GroundColors.Build(map);
        Assert.Equal(4, colors.Levels);
        Assert.Equal(6 * 4 * (64 + 16 + 4 + 1), colors.Pixels.Length);
        for (var level = 0; level < colors.Levels; level++)
            for (var face = 0; face < 6; face++)
                for (var j = 0; j < colors.LevelSide(level); j++)
                    for (var i = 0; i < colors.LevelSide(level); i++)
                        AssertNear(BlockMean(map, level, face, i, j), colors.Texel(level, face, i, j), 1f / 255f);
    }

    [Fact]
    public void A_level_averages_in_linear_light_not_display_values()
    {
        // Snow beside rock: the mean of their light, shown, is paler than the mean of their display values.
        var snow = Scene.KindColors[(int)SurfaceKind.Snow].X;
        var rock = Scene.KindColors[(int)SurfaceKind.Rock].X;
        const int resolution = 2, side = resolution + 2, count = 6 * side * side;
        var kinds = new byte[count];
        for (var t = 0; t < count; t++)
            kinds[t] = (byte)((t % side) % 2 == 0 ? SurfaceKind.Snow : SurfaceKind.Rock);
        var map = new TerrainMap(resolution, Scene.PlanetRadius, Scene.HomeElevation, -100f, 100f,
            new ushort[count], kinds, new byte[count], []);
        var top = GroundColors.Build(map).Texel(1, 0, 0, 0).X;
        Assert.Equal(MathF.Pow((MathF.Pow(snow, 2.2f) + MathF.Pow(rock, 2.2f)) / 2f, 1f / 2.2f), top, 2f / 255f);
        Assert.True(top > (snow + rock) / 2f + 0.03f, $"{top} is the display values' mean");
    }

    [Fact]
    public void A_map_whose_side_isnt_a_power_of_two_is_refused()
    {
        const int resolution = 12, side = resolution + 2, count = 6 * side * side;
        var map = new TerrainMap(resolution, Scene.PlanetRadius, Scene.HomeElevation, -100f, 100f,
            new ushort[count], new byte[count], new byte[count], []);
        Assert.Throws<InvalidDataException>(() => GroundColors.Build(map));
    }

    /// <summary>The display value of the linear mean of the kinds' colours in the 2^level square of the base under a level's texel.</summary>
    private static Vector3 BlockMean(TerrainMap map, int level, int face, int i, int j)
    {
        var size = 1 << level;
        var side = map.Resolution + 2;
        var sum = Vector3.Zero;
        for (var y = j * size; y < (j + 1) * size; y++)
            for (var x = i * size; x < (i + 1) * size; x++)
                sum += GroundColors.ToLinear(Scene.KindColors[map.Kinds[(face * side + y + 1) * side + x + 1]]);
        return GroundColors.ToDisplay(sum / (size * size));
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.True(Vector3.Abs(expected - actual) is { X: var x, Y: var y, Z: var z } && x <= tolerance && y <= tolerance && z <= tolerance,
            $"expected {expected}, got {actual}");

    private static float Parse(string text) => float.Parse(text, CultureInfo.InvariantCulture);
}
