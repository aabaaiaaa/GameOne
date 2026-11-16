using System.Numerics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The sea's one wave function (<see cref="Sea"/>) over the shipped map: deterministic, round sea
/// level, smooth in time however long a world runs, continuous over the coast and between seas,
/// and each sea's wave size as the layout gives it.
/// </summary>
public sealed class SeaTests(ITestOutputHelper output)
{
    private static TerrainMap Map => Terrain.Map;

    private static int Side => Map.Resolution + 2;

    /// <summary>The point <paramref name="metres"/> east of the pad, on the equator.</summary>
    private static Vector3 EastOfPad(float metres) => CubeSphere.FromLatLon(0f, metres / Scene.PlanetRadius);

    private static SeaInfo SeaNamed(string name) => Map.Seas.Single(s => s.Name == name);

    private static float[] PhasesAt(double time)
    {
        var phases = new float[Sea.WaveCount];
        Sea.Phases(time, phases);
        return phases;
    }

    [Fact]
    public void The_waves_are_the_same_every_time()
    {
        var at = EastOfPad(2000f);
        var a = Sea.Height(at, PhasesAt(1234.5));
        var b = Sea.Height(at, PhasesAt(1234.5));
        Assert.Equal(a, b);
        Assert.Equal(Sea.Flow(at, PhasesAt(1234.5)), Sea.Flow(at, PhasesAt(1234.5)));
    }

    [Fact]
    public void The_sea_rises_and_falls_round_sea_level()
    {
        var at = EastOfPad(2000f);
        var waves = SeaNamed("Sunrise Sea").Waves;
        float lo = float.MaxValue, hi = float.MinValue;
        double sum = 0;
        var count = 0;
        for (var step = 0; step <= 2000; step++)
        {
            var h = Sea.Height(at, PhasesAt(step * 0.1));
            lo = MathF.Min(lo, h);
            hi = MathF.Max(hi, h);
            sum += h;
            count++;
        }

        var mean = sum / count;
        output.WriteLine($"2 km east, over 200 s: {lo:+0.000;-0.000} to {hi:+0.000;-0.000} m about sea level " +
                         $"(moves {hi - lo:0.000} m; the sea's waves are {waves} m), mean {mean:+0.0000;-0.0000} m");
        Assert.InRange(lo, -waves / 2f * 1.01f, 0f);
        Assert.InRange(hi, 0f, waves / 2f * 1.01f);
        Assert.True(hi - lo >= waves / 2f, $"the sea moves only {hi - lo} m");
        Assert.InRange(mean, -0.05, 0.05);
    }

    [Fact]
    public void The_waves_are_smooth_in_time_even_after_hours()
    {
        var at = EastOfPad(2000f);
        var a = Sea.Height(at, PhasesAt(1e6));
        var b = Sea.Height(at, PhasesAt(1e6 + 0.01));
        output.WriteLine($"at 10^6 s: {a:+0.0000;-0.0000} m, 0.01 s later {b:+0.0000;-0.0000} m");
        Assert.True(MathF.Abs(a - b) < 0.05f, $"the sea jumps {MathF.Abs(a - b)} m in 0.01 s");

        // Each phase moves on by exactly its frequency times the step. Phases or a time held in
        // float would lose that step to rounding this far in, and the waves would stutter.
        var before = PhasesAt(1e6);
        var after = PhasesAt(1e6 + 0.01);
        var worst = 0.0;
        for (var i = 0; i < Sea.WaveCount; i++)
        {
            var moved = Math.IEEERemainder(after[i] - before[i], 2.0 * Math.PI);
            var expected = Math.IEEERemainder(Sea.Frequency(Sea.Waves[i].Wavelength) * 0.01, 2.0 * Math.PI);
            worst = Math.Max(worst, Math.Abs(moved - expected));
            Assert.True(Math.Abs(moved - expected) < 1e-5, $"wave {i} moved {moved} rad in 0.01 s, not {expected}");
        }

        output.WriteLine($"each phase's step over 0.01 s is right to within {worst:0.0e0} rad");
    }

    [Fact]
    public void The_water_runs_forward_under_the_crests()
    {
        // Each wave's surface flow is in step with its height, so over a long time the height
        // times the flow averages to each wave's height times its speed, halved: zero if the flow
        // ran a quarter-wave out of step. The waves' frequencies differ, so their cross terms
        // average out.
        var at = EastOfPad(2000f);
        var scale = Sea.WaveScale(at);
        var expected = Vector3.Zero;
        foreach (var (d, wavelength, share) in Sea.Waves)
        {
            var across = d - Vector3.Dot(d, at) * at;
            var height = scale * 0.5f * share * across.Length();
            var speed = (float)Sea.Frequency(wavelength) * scale * 0.5f * share;
            expected += across * (height * speed * 0.5f);
        }

        var sum = Vector3.Zero;
        var count = 0;
        for (var step = 0; step < 40_000; step++)
        {
            var phases = PhasesAt(step * 0.05);
            sum += (Sea.Height(at, phases)) * Sea.Flow(at, phases);
            count++;
        }

        var mean = sum / count;
        output.WriteLine($"mean height x flow {mean.Length():0.0000} m²/s against {expected.Length():0.0000} expected; off by {(mean - expected).Length():0.0000}");
        Assert.True((mean - expected).Length() < 0.1f * expected.Length(), $"the height times the flow averages {mean}, not {expected}");
    }

    [Fact]
    public void The_waves_are_continuous_across_the_coast_and_between_seas()
    {
        // Every water texel beside land (the coast) or beside another sea (a boundary).
        var coast = new List<(int Face, int I, int J, int Di, int Dj)>();
        var boundary = new List<(int Face, int I, int J)>();
        var n = Map.Resolution;
        for (var face = 0; face < 6; face++)
        {
            for (var j = 1; j <= n; j++)
            {
                for (var i = 1; i <= n; i++)
                {
                    var region = Map.Regions[face * Side * Side + j * Side + i];
                    if (region == 0) continue;
                    foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        var next = Map.Regions[face * Side * Side + (j + dj) * Side + i + di];
                        if (next == region) continue;
                        if (next == 0) coast.Add((face, i, j, di, dj));
                        else boundary.Add((face, i, j));
                        break;
                    }
                }
            }
        }

        var rng = new Random(7);
        Vector3 Texel(int face, float i, float j) =>
            CubeSphere.FromFace(face, ((i - 0.5f) / n) * 2f - 1f, ((j - 0.5f) / n) * 2f - 1f);
        Vector3 Jitter(int face, int i, int j) => Texel(face, i + rng.NextSingle() - 0.5f, j + rng.NextSingle() - 0.5f);

        // Points near the coast and the boundaries, each against a neighbour half a metre away, at a random moment.
        var points = new List<Vector3>();
        for (var k = 0; k < 1500; k++)
        {
            var c = coast[rng.Next(coast.Count)];
            points.Add(Jitter(c.Face, c.I, c.J));
        }

        for (var k = 0; k < 500; k++)
        {
            var b = boundary[rng.Next(boundary.Count)];
            points.Add(Jitter(b.Face, b.I, b.J));
        }

        var worst = 0f;
        foreach (var p in points)
        {
            var phases = PhasesAt(rng.NextDouble() * 3600.0);
            var a = Vector3.Normalize(Vector3.Cross(MathF.Abs(p.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, p));
            var b = Vector3.Cross(p, a);
            var angle = rng.NextSingle() * 2f * MathF.PI;
            var q = Vector3.Normalize(p + (a * MathF.Cos(angle) + b * MathF.Sin(angle)) * (0.5f / Scene.PlanetRadius));
            var step = MathF.Abs(Sea.Height(p, phases) - Sea.Height(q, phases));
            worst = MathF.Max(worst, step);
            Assert.True(step < 0.1f, $"the sea steps {step} m in half a metre at {p}");
        }

        // The shore: from each of the coast's texels towards the land beside it, where the water is under half a metre deep.
        var shores = 0;
        var highest = 0f;
        for (var k = 0; k < 1000; k++)
        {
            var c = coast[rng.Next(coast.Count)];
            var wet = Texel(c.Face, c.I, c.J);
            var dry = Texel(c.Face, c.I + c.Di, c.J + c.Dj);
            float Depth(Vector3 d) => -Terrain.Height(d);
            if (Depth(wet) <= 0.5f || Depth(dry) >= 0f) continue;
            for (var s = 0; s < 30; s++)
            {
                var mid = Vector3.Normalize(wet + dry);
                if (Depth(mid) > 0.25f) wet = mid;
                else dry = mid;
            }

            var shore = Depth(wet) < 0.5f ? wet : dry;
            if (Depth(shore) is < 0f or >= 0.5f) continue;
            var off = MathF.Abs(Sea.Height(shore, PhasesAt(rng.NextDouble() * 3600.0)));
            highest = MathF.Max(highest, off);
            shores++;
            Assert.True(off < 0.05f, $"the sea is {off} m off sea level at the shore at {shore}");
        }

        output.WriteLine($"{coast.Count} coast texels, {boundary.Count} boundary texels; the biggest step in half a metre over " +
                         $"{points.Count} points is {worst:0.0000} m; at {shores} shore points the sea is at most {highest:0.0000} m off sea level");
        Assert.True(shores >= 100, $"only {shores} shore points found");
    }

    [Fact]
    public void Floes_damp_the_waves()
    {
        // In the middle of the Boreal Sea's floe band, and just out past it, both in deep water.
        var (centre, _, _) = IceTests.Layout("Boreal Sea");
        var band = IceTests.FloeBand("Boreal Sea");
        Assert.Equal(SurfaceKind.FloeField, Terrain.Kind(band));
        var reach = Scene.PlanetRadius * MathF.Acos(Math.Clamp(Vector3.Dot(centre, band), -1f, 1f));
        while (Terrain.Kind(IceTests.Travel(centre, IceTests.SearchBearing, reach)) == SurfaceKind.FloeField) reach += 5f;
        var past = IceTests.Travel(centre, IceTests.SearchBearing, reach + 50f);
        Assert.Equal(SurfaceKind.Seabed, Terrain.Kind(past));
        foreach (var at in new[] { band, past })
            Assert.True(-Terrain.Height(at) > Scene.SeaWaveFullDepth, $"only {-Terrain.Height(at)} m deep at {at}");

        var (damped, open) = (Sea.WaveScale(band), Sea.WaveScale(past));
        output.WriteLine($"the waves are {damped:0.000} m among the floes and {open:0.000} m just past them ({Scene.FloeWaveDamping} damping)");
        Assert.InRange(damped / open, Scene.FloeWaveDamping * 0.98f, Scene.FloeWaveDamping * 1.02f);
        Assert.InRange(open, SeaNamed("Boreal Sea").Waves * 0.999f, SeaNamed("Boreal Sea").Waves * 1.001f); // the four texels' weights sum to 1, give or take rounding

        // The GLSL's twin (seaWaveScale) damps the same kind by the same amount. There is no GPU to
        // run it on here, so this checks it is written from the same numbers.
        var glsl = Rendering.Shaders.PlanetVertex(Map);
        Assert.Contains($"#define KIND_FLOE_FIELD {(int)SurfaceKind.FloeField}u", glsl);
        Assert.Contains($"#define FLOE_WAVE_DAMPING {Scene.FloeWaveDamping.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}", glsl);
        Assert.Matches(@"float seaWaveScale\([\s\S]*?\.r == KIND_FLOE_FIELD \? FLOE_WAVE_DAMPING : 1\.0", glsl);
    }

    [Fact]
    public void Each_sea_has_its_wave_size_and_the_icy_seas_are_roughest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "TerrainGen", "layout.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tools", "TerrainGen", "layout.json")), options);
        static Vector3 Colour(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());

        var layout = json.RootElement.GetProperty("seas").EnumerateArray().ToList();
        Assert.Equal(layout.Count, Map.Seas.Count);
        for (var i = 0; i < layout.Count; i++)
        {
            var sea = Map.Seas[i];
            Assert.Equal(layout[i].GetProperty("name").GetString(), sea.Name);
            Assert.Equal(layout[i].GetProperty("kind").GetString(), sea.Kind.ToString(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(layout[i].GetProperty("waves").GetSingle(), sea.Waves);
            Assert.Equal(Colour(layout[i].GetProperty("shallow")), sea.Shallow);
            Assert.Equal(Colour(layout[i].GetProperty("deep")), sea.Deep);
            output.WriteLine($"{sea.Name} ({sea.Kind}): waves {sea.Waves} m");
        }

        var calmestIcy = Map.Seas.Where(s => s.Kind == SeaKind.Icy).Min(s => s.Waves);
        var roughestWarm = Map.Seas.Where(s => s.Kind == SeaKind.Warm).Max(s => s.Waves);
        Assert.True(calmestIcy > roughestWarm, $"an icy sea's waves ({calmestIcy} m) are no bigger than a warm sea's ({roughestWarm} m)");
        Assert.InRange(SeaNamed("Sunrise Sea").Waves, 0.8f, 1.2f);
    }
}
