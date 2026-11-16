using System.Numerics;
using System.Text.Json;
using GameOne.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The seas' colour map (<see cref="SeaColors"/>): two seas' colours blend across their border by
/// the seas' weights, gradually everywhere on the planet's map (the Grey Sea and the Coral Sea's
/// straight border among them), and each sea keeps its own colours in its middle.
/// </summary>
public sealed class SeaColorsTests(ITestOutputHelper output)
{
    private static readonly Lazy<SeaColors> Planet = new(() => SeaColors.Build(Terrain.Map));

    [Fact]
    public void Water_of_a_sea_the_map_doesnt_name_is_refused()
    {
        // A made-up map of one sea with a texel of a second it has no colours for: refused as the
        // map is made, so the colour map never meets water with no sea's weight to divide by.
        const int resolution = 64, side = resolution + 2, count = 6 * side * side;
        var regions = new byte[count];
        Array.Fill(regions, (byte)1);
        regions[(2 * side + 30) * side + 30] = 2;
        var a = new SeaInfo("A", SeaKind.Temperate, 1f, new(0.30f, 0.48f, 0.44f), new(0.07f, 0.12f, 0.17f));
        var error = Assert.Throws<InvalidDataException>(() => new TerrainMap(resolution, Scene.PlanetRadius, Scene.HomeElevation, -100f, 100f,
            new ushort[count], new byte[count], regions, [a]));
        Assert.Contains("past the map's 1 seas", error.Message);
    }

    [Fact]
    public void Two_seas_blend_across_their_border_by_their_weights()
    {
        // A made-up map, every texel sea 1 but face 4's half past u = 0, sea 2, and a block of land
        // in sea 1 there. Along a row across the border, each cell's colour is the two seas' mixed
        // by their weights, exp(-d / blend), d the distance to the other sea's nearest cell.
        const int resolution = 256, side = resolution + 2, count = 6 * side * side, face = 4;
        var regions = new byte[count];
        Array.Fill(regions, (byte)1);
        for (var j = 0; j < side; j++)
            for (var i = side / 2; i < side; i++)
                regions[(face * side + j) * side + i] = 2;
        for (var j = 100; j < 120; j++)
            for (var i = 40; i < 60; i++)
                regions[(face * side + j) * side + i] = 0;
        var a = new SeaInfo("A", SeaKind.Temperate, 1f, new(0.30f, 0.48f, 0.44f), new(0.07f, 0.12f, 0.17f));
        var b = new SeaInfo("B", SeaKind.Warm, 1f, new(0.08f, 0.76f, 0.68f), new(0.05f, 0.09f, 0.42f));
        var map = new TerrainMap(resolution, Scene.PlanetRadius, Scene.HomeElevation, -100f, 100f,
            new ushort[count], new byte[count], regions, [a, b]);

        var colors = SeaColors.Build(map);
        var cells = colors.Side;
        Assert.Equal(64, cells);
        Assert.Equal(7, colors.Levels);
        Assert.Equal(2 * 4 * 12 * (4096 + 1024 + 256 + 64 + 16 + 4 + 1), colors.Pixels().Length);
        // The border falls between cells 31 and 32 (texel 129, the face's u = 0, is sea 2's, and cell 32 holds it).
        var row = 10;
        Vector3 Centre(int i) => CubeSphere.FromFace(face, (i + 0.5f) / cells * 2f - 1f, (row + 0.5f) / cells * 2f - 1f);
        float Along(int from, int to)
        {
            var d = 0f;
            for (var i = from; i < to; i++)
                d += GroundBody.PlanetSeaRadius * 2f * MathF.Asin(Vector3.Distance(Centre(i), Centre(i + 1)) * 0.5f);
            return d;
        }
        for (var i = 20; i < 44; i++)
        {
            var (mine, other) = i < 32 ? (a, b) : (b, a);
            var away = (i < 32 ? Along(i, 32) : Along(31, i)) - 0.5f * Along(31, 32);
            var w = MathF.Exp(-away / Scene.SeaColorBlend);
            var deep = GroundColors.ToDisplay((GroundColors.ToLinear(mine.Deep) + w * GroundColors.ToLinear(other.Deep)) / (1f + w));
            var shallow = GroundColors.ToDisplay((GroundColors.ToLinear(mine.Shallow) + w * GroundColors.ToLinear(other.Shallow)) / (1f + w));
            var (s, d) = colors.Texel(0, face, i, row);
            AssertNear(deep, d, 2e-3f);
            AssertNear(shallow, s, 2e-3f);
        }
        // Far from the border each is its own sea's; the land takes the colours of the water round it.
        AssertNear(a.Deep, colors.Texel(0, 1, 30, 30).Deep, 2e-3f);
        AssertNear(b.Deep, colors.Texel(0, face, 47, 30).Deep, 2e-3f);
        AssertNear(colors.Texel(0, face, 9, 27).Deep, colors.Texel(0, face, 12, 27).Deep, 2e-3f); // inside the land block (cells 10 to 14)
    }

    [Fact]
    public void Neighbouring_sea_texels_on_the_planets_map_change_colour_gradually()
    {
        // Every pair of neighbouring cells with water in them, along a face's rows and columns
        // (61 m apart, four of the map's texels): no channel of either colour changes by more than
        // 0.0125 (about 0.003 a texel of the map), where the seas' own colours differ by up to 0.36
        // (the Coral and Austral seas' shallow reds) and the regions' own colours would change from
        // one texel to the next across a border (the Grey and Coral seas' deep blues by 0.25). The steepest is that most different pair's
        // blend: 0.36 x 61 m / (4 x Scene.SeaColorBlend), 0.011.
        var colors = Planet.Value;
        var map = Terrain.Map;
        int side = colors.Side, block = map.Resolution / side, mapSide = map.Resolution + 2;
        bool Wet(int face, int i, int j)
        {
            for (var y = 0; y < block; y++)
                for (var x = 0; x < block; x++)
                    if (map.Regions[(face * mapSide + j * block + y + 1) * mapSide + i * block + x + 1] != 0) return true;
            return false;
        }
        float worst = 0f;
        var pairs = 0;
        for (var face = 0; face < 6; face++)
            for (var j = 0; j < side; j++)
                for (var i = 0; i < side; i++)
                {
                    if (!Wet(face, i, j)) continue;
                    var here = colors.Texel(0, face, i, j);
                    foreach (var (x, y) in new[] { (i + 1, j), (i, j + 1) })
                    {
                        if (x >= side || y >= side || !Wet(face, x, y)) continue;
                        var there = colors.Texel(0, face, x, y);
                        var change = MathF.Max(MaxChannel(here.Shallow - there.Shallow), MaxChannel(here.Deep - there.Deep));
                        worst = MathF.Max(worst, change);
                        pairs++;
                    }
                }
        output.WriteLine($"{pairs} pairs, the most a channel changes {worst:F4}");
        Assert.True(pairs > 100_000, $"only {pairs} pairs");
        Assert.True(worst <= 0.0125f, $"a colour changes by {worst:F4} from one texel to the next");
    }

    [Fact]
    public void The_grey_and_coral_seas_meet_gradually()
    {
        // At 20°N their border runs at 144.8°E (where the regions change from one texel to the
        // next, the deep blue jumping 0.25 from the Grey Sea's 0.17 to the Coral Sea's 0.42). Two
        // reads 33 m apart across it differ by under 0.01; 2.5 km either way each sea's own colour
        // has most of the mix.
        var colors = Planet.Value;
        Vector3 At(float east) => CubeSphere.FromLatLon(float.DegreesToRadians(20f), float.DegreesToRadians(east));
        Assert.Equal(Region("Grey Sea"), Terrain.Map.RegionAt(CubeSphere.ToFace(At(144.7f)).Face, CubeSphere.ToFace(At(144.7f)).U, CubeSphere.ToFace(At(144.7f)).V));
        Assert.Equal(Region("Coral Sea"), Terrain.Map.RegionAt(CubeSphere.ToFace(At(144.9f)).Face, CubeSphere.ToFace(At(144.9f)).U, CubeSphere.ToFace(At(144.9f)).V));
        var west = colors.At(At(144.7f)).Deep;
        var east = colors.At(At(144.9f)).Deep;
        output.WriteLine($"deep at 144.7°E {west}, at 144.9°E {east}");
        Assert.True(MaxChannel(west - east) < 0.01f, $"{west} beside {east}");
        // 1° of longitude at 20°N is 164 m: 2.5 km is 15°.
        for (var e = 125f; e <= 165f; e += 2.5f)
            output.WriteLine($"  {e,5:F1}°E: deep {colors.At(At(e)).Deep}");
        var grey = colors.At(At(144.8f - 15f)).Deep;
        var coral = colors.At(At(144.8f + 15f)).Deep;
        Assert.True(grey.Z < 0.20f, $"the Grey Sea's deep is {grey}");
        Assert.True(coral.Z > 0.39f, $"the Coral Sea's deep is {coral}");
    }

    [Fact]
    public void Each_sea_keeps_its_own_colours_in_its_middle()
    {
        // At the point each sea is laid out from, its colours are its own (no neighbour's mixed in):
        // the blend washes no sea out.
        var colors = Planet.Value;
        foreach (var (name, at) in Layout.Value)
        {
            var sea = Terrain.Map.Seas.Single(s => s.Name == name);
            var (shallow, deep) = colors.At(at);
            output.WriteLine($"{name}: {shallow} / {deep}, its own {sea.Shallow} / {sea.Deep}");
            AssertNear(sea.Shallow, shallow, 0.01f);
            AssertNear(sea.Deep, deep, 0.01f);
        }
    }

    [Fact]
    public void The_water_reads_its_colours_from_the_map_at_binding_12()
    {
        // CreateSetLayout declares binding 12 a combined image sampler (the mipmapped SeaColors);
        // the water's colour is two filtered reads of it, by the derivatives taken before the
        // discard, not the seas' table by region.
        var source = Shaders.SceneFragment(Terrain.Map);
        Assert.Matches(@"layout\(set = 0, binding = 12\) uniform sampler2DArray uSeaColors;", source);
        Assert.Contains("textureGrad(uSeaColors, vec3(st, float(face)),", source);
        Assert.Contains("textureGrad(uSeaColors, vec3(st, float(face + 6)),", source);
        Assert.DoesNotContain("seaShallow", source);
        Assert.DoesNotContain("seaDeep", source);
        Assert.Matches(@"vec2 uvDx = dFdx\(vTerrain\.xy\), uvDy = dFdy\(vTerrain\.xy\);\s*if \(vWaterline < 0\.0\)", source);
    }

    private static byte Region(string sea) => (byte)(Terrain.Map.Seas.Select((s, i) => (s, i)).Single(p => p.s.Name == sea).i + 1);

    /// <summary>Each sea in the layout, by name, and the direction it is laid out from (its <c>at</c>).</summary>
    private static readonly Lazy<List<(string Name, Vector3 At)>> Layout = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "TerrainGen", "layout.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tools", "TerrainGen", "layout.json")), options);
        return json.RootElement.GetProperty("seas").EnumerateArray()
            .Select(s =>
            {
                var at = s.GetProperty("at");
                return (s.GetProperty("name").GetString()!, CubeSphere.FromLatLon(float.DegreesToRadians(at[0].GetSingle()), float.DegreesToRadians(at[1].GetSingle())));
            })
            .ToList();
    });

    private static float MaxChannel(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.True(MaxChannel(expected - actual) <= tolerance, $"expected {expected}, got {actual}");
}
