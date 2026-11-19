using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Brutal;
using Brutal.ShaderCApi;
using GameOne.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The GLSL, which is otherwise only compiled when the game starts: every shader compiles (ShaderC
/// needs no GPU), and the per-frame uniform block lays out as the renderer's struct does.
/// </summary>
public sealed class ShaderTests(ITestOutputHelper output)
{
    public static TheoryData<string> Names => new(Sources.Keys);

    private static readonly Dictionary<string, (string Source, ShaderKind Kind)> Sources = new()
    {
        ["scene.vert"] = (Shaders.SceneVertex, ShaderKind.VertexShader),
        ["scene.frag"] = (Shaders.SceneFragment(Terrain.Map), ShaderKind.FragmentShader),
        ["planet.vert"] = (Shaders.PlanetVertex(Terrain.Map), ShaderKind.VertexShader),
        ["moon.vert"] = (Shaders.MoonVertex, ShaderKind.VertexShader),
        ["moon.frag"] = (Shaders.MoonFragment, ShaderKind.FragmentShader),
        ["shadow.vert"] = (Shaders.ShadowVertex, ShaderKind.VertexShader),
        ["shadow.frag"] = (Shaders.ShadowFragment, ShaderKind.FragmentShader),
        ["spotshadow.vert"] = (Shaders.SpotShadowVertex, ShaderKind.VertexShader),
        ["sky.vert"] = (Shaders.SkyVertex, ShaderKind.VertexShader),
        ["sky.frag"] = (Shaders.SkyFragment, ShaderKind.FragmentShader),
        ["flare.vert"] = (Shaders.FlareVertex, ShaderKind.VertexShader),
        ["flare.frag"] = (Shaders.FlareFragment, ShaderKind.FragmentShader),
        ["glow.vert"] = (Shaders.GlowVertex, ShaderKind.VertexShader),
        ["glow.frag"] = (Shaders.GlowFragment, ShaderKind.FragmentShader),
        ["smoke.vert"] = (Shaders.SmokeVertex, ShaderKind.VertexShader),
        ["smoke.frag"] = (Shaders.SmokeFragment, ShaderKind.FragmentShader),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Compiles(string name)
    {
        var (source, kind) = Sources[name];
        using var compiler = ShaderC.CreateCompiler();
        using var spirv = compiler.Compile(source, kind, name); // throws, with the compiler's message, if it doesn't
        Assert.True(spirv.GetLength() > 0);
    }

    [Fact]
    public void The_sky_writes_the_moons_and_the_suns_depth_else_the_far_planes()
    {
        // The sky is drawn first, its depth always passing and written (its pipeline's), so the
        // moon and the sun hide what is behind them: every path through its shader (which Compiles
        // compiles) writes gl_FragDepth, the far plane's (0: the depth is reversed) but where the
        // moon covers the pixel, there its hit's, and else where the sun's sphere does, there its,
        // placed from the camera as the meshes are.
        var main = Regex.Match(Shaders.SkyFragment, @"void main\(\)(?<body>.*)", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("bool moonHere = frame.moon.w > 0.0 && frame.moonlight.w == 0.0 && along > 0.0 && !sunInFront;", main);
        Assert.Matches(@"float depth = 0\.0;\s*float reach = -1\.0;\s*if \(moonHere && cover >= 0\.5\)", main);
        Assert.Matches(@"else if \(sunHit > 0\.0\)\s*reach = sunHit;", main);
        Assert.Contains("frame.viewProj * vec4(d * reach, 0.0) + frame.cameraClip", main);
        // Under the lowest ground by two of the reversed depth's steps (the distance times 2^-23) where the ray meets it.
        Assert.Contains("max(1.0, 2.0 * hit * exp2(-23.0))", main);
        Assert.Contains("reach = inLow > 0.0 ? along - sqrt(inLow) : along;", main); // round the limb, the way's middle
        Assert.Matches(@"gl_FragDepth = depth;\s*\}\s*$", main);
        // The disc where the sun is from the camera, as big as it looks from there.
        Assert.Contains("vec3 toSun = frame.sunDisc.xyz;", main);
        Assert.Contains("float sunRadius = frame.sunDisc.w;", main);
        // The eclipse's share by that disc too (as the corona's), and the spheres met by their rays'
        // closest approach as a difference of vectors, which holds from the map's furthest zoom.
        Assert.Contains("float covered = 1.0 - sunSeenFromCamera();", main);
        Assert.Contains("return discSeen(apart, frame.sunDisc.w, moon);", Shaders.SkyFragment);
        Assert.Contains("float b = dot(o, d), miss = length(o - d * b);", Shaders.SkyFragment);
        Assert.Contains("float towards = dot(toSea, d), seaMiss = length(toSea - d * towards);", main);
        // The moon is dimmed by the air only where the way to it passes through the air.
        Assert.Contains("throughAir ? airTransmittance(", Shaders.SkyFragment);
        // Never less than the triangle's own depth (it is at the far plane, 0), and said so.
        Assert.Contains("layout(depth_greater) out float gl_FragDepth;", Shaders.SkyFragment);
        Assert.Contains("gl_Position = vec4(p, 0.0, 1.0);", Shaders.SkyVertex);
    }

    [Fact]
    public void The_seabed_is_pushed_behind_the_water_drawn_over_it_out_to_the_maps_zooms()
    {
        // Zoomed out over Home (the map's first zoom, and further), every corner of the patches drawn
        // well inside where the water is drawn (the waterline, Waterline.Depth, its push ramped a quarter of
        // the way in or more) but the ground stands above the sea: its push (the shader's, taken off its reversed depth) puts it further behind the
        // water than it stands above it, along the way the camera sees it, so it never shows through
        // the water as ragged land.
        var source = Shaders.PlanetVertex(Terrain.Map);
        Assert.Contains("gl_Position.z = max(gl_Position.z - SEABED_PUSH * clamp(waterline / pushOver, 0.0, 1.0) * gl_Position.w, 0.0);", source);
        var push = float.Parse(Regex.Match(source, @"const float SEABED_PUSH = ([0-9.e-]+);").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var near = ChaseCamera.Planes.Near;
        var waterline = Waterline.Build(Terrain.Map);
        var (checkedCorners, worst) = (0, float.PositiveInfinity);
        foreach (var clicks in new[] { 0f, 10f })
        {
            const float dt = 1f / 60f;
            var camera = new ChaseCamera(null);
            var focus = new System.Numerics.Vector3(0f, 20f, 0f);
            camera.Update(dt, focus, dt, GroundBody.Planet, 0);
            camera.ToggleZoomOut(1.5f * Planet.Radius);
            for (var i = 0; i < 90; i++)
                camera.Update(dt, focus, dt, GroundBody.Planet, 0);
            camera.Zoom(-clicks);
            camera.Update(dt, focus, dt, GroundBody.Planet, 0);
            var patches = new List<PlanetPatch>();
            PlanetPatches.Select(camera.Position, Scene.TerrainLodRatio, patches, true, camera.ViewProjection(new System.Numerics.Vector2(1280f, 720f)));
            foreach (var p in patches)
                for (var j = 0; j <= Scene.PlanetPatchGrid; j++)
                    for (var i = 0; i <= Scene.PlanetPatchGrid; i++)
                    {
                        var (u, v) = (p.U0 + p.Size * i / Scene.PlanetPatchGrid, p.V0 + p.Size * j / Scene.PlanetPatchGrid);
                        var dir = CubeSphere.FromFace(p.Face, u, v);
                        var height = Terrain.PlanetHeight(dir);
                        var at = Planet.Centre + dir * (Planet.SeaRadius + height);
                        var z = System.Numerics.Vector3.Distance(at, camera.Position);
                        var spacing = MathF.Max(1f, z / (Scene.TerrainLodRatio * Scene.PlanetPatchGrid));
                        var (drawn, over) = waterline.Depth(p.Face, u, v, spacing, -height);
                        if (drawn < 0.25f * over || height <= 0f)
                            continue; // (where its push is only ramping in, it is at the drawn water's edge, as low as the water there)
                        var depth = near / z - push * Math.Clamp(drawn / over, 0f, 1f);
                        var pushed = depth <= 0f ? float.PositiveInfinity : near / depth - z;
                        var slant = MathF.Abs(System.Numerics.Vector3.Dot(System.Numerics.Vector3.Normalize(at - camera.Position), dir));
                        var needed = height / MathF.Max(slant, 1e-3f);
                        checkedCorners++;
                        worst = MathF.Min(worst, pushed - needed);
                        Assert.True(pushed > needed, $"at {z / 1000f:0.0} km a corner {height:0.0} m above the sea under the drawn water is pushed back only {pushed:0.0} m, short of the {needed:0.0} m it stands out along the view");
                    }
        }
        Assert.True(checkedCorners > 0, "no corner had the water drawn over ground above the sea");
        output.WriteLine($"{checkedCorners} corners with the water drawn over ground above the sea, each pushed at least {worst:0} m further back than it stands out");
    }

    [Fact]
    public void The_frame_block_matches_the_renderers_struct()
    {
        // std140, with only vec4s, mat4s and arrays of vec4: each member follows the last, in
        // 16-byte steps, as the C# struct's sequential fields do. So the names in order, and each
        // one's offset, must agree.
        var block = Regex.Match(Shaders.SkyFragment, @"uniform Frame\s*\{(?<body>.*?)\}\s*frame;", RegexOptions.Singleline).Groups["body"].Value;
        var maxStirs = int.Parse(Regex.Match(Shaders.SkyFragment, @"#define MAX_STIRS (\d+)").Groups[1].Value);
        var spotTiles = int.Parse(Regex.Match(Shaders.SkyFragment, @"#define SPOT_TILES (\d+)").Groups[1].Value);
        var glsl = new List<(string Name, int Offset)>();
        var offset = 0;
        foreach (Match m in Regex.Matches(Regex.Replace(block, "//.*", ""), @"(?<type>mat4|vec4)\s+(?<name>\w+)(\[(?<count>[^\]]+)\])?\s*;"))
        {
            glsl.Add((m.Groups["name"].Value, offset));
            var count = m.Groups["count"].Success ? Count(m.Groups["count"].Value, maxStirs, spotTiles) : 1;
            offset += (m.Groups["type"].Value == "mat4" ? 64 : 16) * count;
        }

        var type = typeof(VulkanRenderer).GetNestedType("FrameUniforms", BindingFlags.NonPublic)!;
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(glsl.Count, fields.Length);
        for (var i = 0; i < fields.Length; i++)
        {
            Assert.Equal(glsl[i].Name, fields[i].Name, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(glsl[i].Offset, (int)Marshal.OffsetOf(type, fields[i].Name));
        }
        // The moon (its centre from the view's camera, and its radius) is one of them, a vec4.
        Assert.Matches(@"vec4 moon;", block);
        Assert.Contains(fields, f => f.Name == "Moon" && f.FieldType == typeof(System.Numerics.Vector4));
        // ... and right after it the turn from the ground's frame into the moon's own axes, a mat4.
        Assert.Matches(@"vec4 moon;[^;]*mat4 moonFrame;", block);
        Assert.Contains(fields, f => f.Name == "MoonFrame" && f.FieldType == typeof(System.Numerics.Matrix4x4));
        // ... and after the moonlight the sun's disc as the view's camera sees it, a vec4.
        Assert.Matches(@"vec4 moonlight;\s*vec4 sunDisc;", Regex.Replace(block, "//.*", ""));
        Assert.Contains(fields, f => f.Name == "SunDisc" && f.FieldType == typeof(System.Numerics.Vector4));
        // (Unsafe.SizeOf, not Marshal.SizeOf: the marshaller sizes an inline array as its one declared element.)
        var size = (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf")!.MakeGenericMethod(type).Invoke(null, null)!;
        Assert.Equal(offset, size);
    }

    [Fact]
    public void The_shadow_maps_layout_agrees_with_the_renderers()
    {
        // The GLSL finds each view's sun area and each floodlight block by SHADOW_AREAS (the map's
        // columns, one per view: the main and each small one), and spotViewProj holds every block's
        // four floodlights' views.
        var areas = int.Parse(Regex.Match(Shaders.SkyFragment, @"#define SHADOW_AREAS (\d+)").Groups[1].Value);
        Assert.Equal(1 + Scene.MaxSmallViews, areas);
        Assert.Equal(areas, (int)typeof(VulkanRenderer).GetField("ShadowAreas", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!);
        // (spotViewProj is sized by the generated SPOT_TILES, checked below, not a literal.)
        Assert.Matches(@"mat4 spotViewProj\[SPOT_TILES\]", Shaders.SkyFragment);
        var spots = VulkanRenderer.SpotTiles;
        Assert.Equal(PadLights.MaxShadowed * areas, spots);

        // Each floodlight tile's place in the map, as the GLSL has it (spotTiles, written from the
        // renderer's layout): its corner and size as fractions of the map.
        Assert.Equal(spots, int.Parse(Regex.Match(Shaders.SkyFragment, @"#define SPOT_TILES (\d+)").Groups[1].Value));
        var table = Regex.Match(Shaders.SkyFragment, @"const vec4 spotTiles\[SPOT_TILES\] = vec4\[\]\((?<body>[^;]*)\);").Groups["body"].Value;
        var tiles = Regex.Matches(table, @"vec4\(([^)]*)\)").Select(m => m.Groups[1].Value.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
        Assert.Equal(spots, tiles.Count);
        var (width, height) = (VulkanRenderer.ShadowMapWidth, VulkanRenderer.ShadowMapHeight);
        for (var t = 0; t < spots; t++)
        {
            var (x, y, size) = VulkanRenderer.SpotTileRect(t);
            Assert.Equal([(float)x / width, (float)y / height, (float)size / width, (float)size / height], tiles[t]);
        }
    }

    [Fact]
    public void The_shadow_maps_areas_and_tiles_fill_it_without_overlapping()
    {
        // The main view's sun area (2048²), each small view's (1024², over the same ground: a small
        // view can't show finer) and every floodlight tile (1024²), each its own square of the map,
        // and the map no bigger than they need: 11264 x 3072, 132 MiB at 32-bit depth.
        var rects = Enumerable.Range(0, VulkanRenderer.ShadowAreas).Select(VulkanRenderer.SunAreaRect)
            .Concat(Enumerable.Range(0, VulkanRenderer.SpotTiles).Select(VulkanRenderer.SpotTileRect)).ToList();
        Assert.Equal(2048, rects[0].Size);
        Assert.All(rects.Skip(1), r => Assert.Equal(1024, r.Size));
        var (width, height) = (VulkanRenderer.ShadowMapWidth, VulkanRenderer.ShadowMapHeight);
        foreach (var r in rects)
            Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.Size <= width && r.Y + r.Size <= height, $"{r} is off the map");
        for (var i = 0; i < rects.Count; i++)
            for (var j = i + 1; j < rects.Count; j++)
            {
                var (a, b) = (rects[i], rects[j]);
                Assert.False(a.X < b.X + b.Size && b.X < a.X + a.Size && a.Y < b.Y + b.Size && b.Y < a.Y + a.Size, $"{a} and {b} overlap");
            }
        Assert.Equal(rects.Sum(r => (long)r.Size * r.Size), (long)width * height); // none wasted
        Assert.Equal((11264, 3072), (width, height));
    }

    [Fact]
    public void The_air_tables_are_bound_as_a_sampler_array_at_binding_6()
    {
        // The descriptor set's layout is the renderer's (CreateSetLayout declares binding 6 a combined
        // image sampler, the terrain's way); the GLSL must declare the same binding, a sampler2DArray.
        Assert.Matches(@"layout\(set = 0, binding = 6\) uniform sampler2DArray uAir;", Shaders.SkyFragment);
        Assert.Matches(@"vec3 airTransmittance\(float aboveSea, float sunUp\)", Shaders.SkyFragment);
        Assert.Matches(@"vec3 airSkyLight\(float aboveSea, float sunUp\)", Shaders.SkyFragment);
    }

    [Fact]
    public void The_vessels_glows_are_a_storage_buffer_at_binding_7()
    {
        // CreateSetLayout declares binding 7 a storage buffer for the vertex stage, one vec4 a
        // vessel (where, and how far on with its twinkle's seed), written per frame from
        // FrameInput.VesselGlows; the glow's vertex shader reads it by instance.
        Assert.Matches(@"layout\(std430, set = 0, binding = 7\) readonly buffer VesselGlows \{ vec4 vesselGlows\[\]; \};", Shaders.GlowVertex);
        Assert.Contains("vesselGlows[gl_InstanceIndex]", Shaders.GlowVertex);
    }

    [Fact]
    public void The_moons_ground_is_lit_by_the_view_s_lights_as_the_planet_s_is()
    {
        // The lights handed to a view (the landing lights of the vessels near it, and any place's)
        // and the engine flame light the moon's ground mesh too, by its normal turned into the
        // scene's frame (the frame the lights are in), on top of the sun and the planetshine. Each
        // call's normal is the ground's normal turned out of the moon's axes (by the transpose of
        // frame.moonFrame), whether written in place or through a variable that holds it, and the
        // shadow lookup is by that same normal: a normal left in the moon's axes fails.
        var main = Regex.Match(Shaders.MoonFragment, @"void main\(\)(?<body>.*)", RegexOptions.Singleline);
        Assert.True(main.Success, "can't find the moon fragment shader's main");
        var body = main.Groups["body"].Value;
        const string Turned = @"transpose\(mat3\(frame\.moonFrame\)\) \* n\b";
        var names = Regex.Matches(body, @"vec3 (?<name>\w+) = " + Turned + ";").Select(m => Regex.Escape(m.Groups["name"].Value));
        var turned = "(?:" + string.Join("|", names.Append(Turned)) + ")";
        foreach (var call in new[] { "pointLighting", "padLighting", "shadowMapVisibility" })
            Assert.Matches(call + @"\(vWorldPos, " + turned + @"\)", body);
    }

    [Fact]
    public void The_ground_colours_are_a_sampler_array_at_binding_10_read_per_pixel()
    {
        // CreateSetLayout declares binding 10 a combined image sampler (the mipmapped GroundColors);
        // the planet's GLSL declares the same, and the ground's colour comes from it per pixel, with
        // the near reading (kindColor) cross-faded in, never from the vertices' colour.
        var source = Shaders.SceneFragment(Terrain.Map);
        Assert.Matches(@"layout\(set = 0, binding = 10\) uniform sampler2DArray uGroundColors;", source);
        var ground = Regex.Match(source, @"if \(kind == KindGround\)\s*\{(?<body>.*?)
\s*\}\s*else if \(kind == KindWater\)", RegexOptions.Singleline);
        Assert.True(ground.Success, "can't find the ground's branch");
        var body = ground.Groups["body"].Value;
        Assert.DoesNotContain("vColor", body);
        Assert.Contains("groundColors(vFace, vTerrain.xy)", body);
        Assert.Contains("texture(uGroundColors,", source);
        var vertexMain = Regex.Match(Shaders.PlanetVertex(Terrain.Map), @"vec3 kindColor\(int face.*?void main\(\)(?<body>.*)", RegexOptions.Singleline);
        Assert.True(vertexMain.Success, "can't find the planet vertex shader's main after kindColor");
        Assert.DoesNotContain("kindColor(", vertexMain.Groups["body"].Value);
    }

    [Fact]
    public void A_vessels_far_glow_twinkles_by_the_vessel_not_where_it_is()
    {
        // Its w carries its seed (its id) and how far on it is, each read back whole by the
        // shader's own sums (seed = floor(w / 2), lit = w - 2 seed), over every fraction and seed;
        // the shader seeds the twinkle by it, a lamp's by where it is.
        foreach (var id in new[] { 0, 1, 17, VulkanRenderer.GlowSeeds - 1, VulkanRenderer.GlowSeeds + 5, 999_999 })
            foreach (var lit in new[] { 0f, 0.001f, 0.25f, 0.5f, 0.999f, 1f })
            {
                var w = VulkanRenderer.GlowW(new VesselLight(default, default, lit, id));
                var seed = MathF.Floor(w * 0.5f);
                Assert.Equal(id % VulkanRenderer.GlowSeeds, (int)seed);
                Assert.Equal(lit, w - 2f * seed, 3);
            }
        Assert.Contains("float seed = floor(light.w * 0.5);", Shaders.GlowVertex);
        Assert.Contains("key = vec3(seed, 7.0, 3.0);", Shaders.GlowVertex);
        Assert.Contains("vec3 key = floor(origin * 0.5);", Shaders.GlowVertex);
        Assert.Contains("dot(key, vec3(", Shaders.GlowVertex);
    }

    /// <summary>An array's length as the block writes it: a number, or a multiple of MAX_STIRS.</summary>
    private static int Count(string text, int maxStirs, int spotTiles)
    {
        var m = Regex.Match(text.Trim(), @"^(?:(\d+)\s*\*\s*)?(MAX_STIRS|SPOT_TILES|\d+)$");
        Assert.True(m.Success, $"can't read the array length '{text}'");
        var n = m.Groups[2].Value switch { "MAX_STIRS" => maxStirs, "SPOT_TILES" => spotTiles, var digits => int.Parse(digits) };
        return m.Groups[1].Success ? int.Parse(m.Groups[1].Value) * n : n;
    }
}
