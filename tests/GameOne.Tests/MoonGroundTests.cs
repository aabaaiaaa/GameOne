using System.Numerics;
using TerrainGen;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The moon's ground (<see cref="MoonGround"/>, its map <c>Assets/moon.bin.gz</c> made by
/// tools/TerrainGen's moon mode from moon.json): made the same every time and refused for another
/// moon, its two big craters and the crescent canyon where the layout puts them and as big as the
/// design asks, the far side's tail and its mountain, gentle plains, a level landing spot, pale rims
/// and rays, and the ground's own shadows.
/// </summary>
public sealed class MoonGroundTests(ITestOutputHelper output)
{
    private static readonly Lazy<MoonLayout> Layout = new(() => MoonLayout.Load(LayoutPath()));

    private static MoonLayout L => Layout.Value;

    private static Vector3 At(float[] latLon) => At(latLon[0], latLon[1]);

    private static Vector3 At(float latitude, float longitude) =>
        CubeSphere.FromLatLon(latitude * MathF.PI / 180f, longitude * MathF.PI / 180f);

    private static float Latitude(Vector3 d) => CubeSphere.LatLon(d).Latitude * 180f / MathF.PI;

    private static float Longitude(Vector3 d) => CubeSphere.LatLon(d).Longitude * 180f / MathF.PI;

    /// <summary>The ground's slope at a place, in degrees, from its normal.</summary>
    private static float Slope(Vector3 d) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(MoonGround.Normal(d), d), -1f, 1f)) * 180f / MathF.PI;

    /// <summary>The canyon as the generator lays it, rough path and all, at a share of the way along its arc.</summary>
    private static readonly Lazy<MoonGenerator> Generator = new(() => new MoonGenerator(L, _ => { }));

    private static MoonGenerator.CanyonSection Canyon(float along) => Generator.Value.CanyonAt(along);

    private static Vector3 LandingSpot => At(Scene.MoonLandingSpot.Latitude, Scene.MoonLandingSpot.Longitude);

    [Fact]
    public void Every_sixteenth_row_of_the_moon_map_is_made_as_the_shipped_one()
    {
        // The quick form of the check below: a run of the generator over every sixteenth row of each
        // face (a sixteenth of its texels, in this process and build) gives the shipped map's
        // kinds there exactly and its heights to within the half step it quantises them to, so the
        // shipped map is the layout's. It leaves out the rest of the rows, the map's height range
        // and the aprons, which the whole run checks before a merge.
        const int Every = 16;
        var (heights, kinds) = new MoonGenerator(L, _ => { }).Rows(Every);
        var map = MoonGround.Map;
        int n = map.Resolution, side = n + 2, rows = (n + Every - 1) / Every;
        var step = (map.MaxHeight - map.MinHeight) / 65535f;
        var worst = 0f;
        for (var row = 0; row < 6 * rows; row++)
        {
            int face = row / rows, j = row % rows * Every;
            for (var i = 0; i < n; i++)
            {
                var at = (face * side + j + 1) * side + i + 1;
                Assert.Equal(map.Kinds[at], kinds[row * n + i]);
                var off = MathF.Abs(heights[row * n + i] - map.Metres(map.Heights[at]));
                worst = MathF.Max(worst, off);
            }
        }
        output.WriteLine($"{6 * rows * n:N0} texels: the kinds the same, the heights within {worst * 1000:0.0} mm (half a step {step * 500:0.0} mm)");
        // Half a step, and a hundredth more: the quantising's division of a height up to 65,535
        // steps up is rounded by a float's step there, about a hundredth of one.
        Assert.True(worst <= step * 0.51f, $"a height {worst:0.0000} m off the shipped map's, over half its step");
    }

    [LongFact]
    [Trait("Run", "Long")]
    public void The_moon_map_is_made_the_same_from_the_same_layout()
    {
        var folder = Directory.CreateTempSubdirectory("gameone-moon-");
        try
        {
            // One run here gives the very bytes of the shipped map, made by another run (in another
            // process and build): so the same layout always gives the same map, and the shipped map
            // is the layout's, regenerated after any change to it. (The whole run is about 8 s, over
            // the normal suite's limit: the sixteenth of it above is the normal suite's check.)
            var first = Path.Combine(folder.FullName, "first.bin.gz");
            new MoonGenerator(L, _ => { }).Run().Save(first);
            Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "moon.bin.gz")), File.ReadAllBytes(first));
            var made = TerrainMap.Load(first, Scene.MoonRadius, 0f);
            Assert.Equal(made.Heights, MoonGround.Map.Heights);
            Assert.Equal(made.Kinds, MoonGround.Map.Kinds);
            Assert.Equal(made.MinHeight, MoonGround.Map.MinHeight);
            Assert.Equal(made.MaxHeight, MoonGround.Map.MaxHeight);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(Scene.MoonRadius + 100f, 0f, "radius")]
    [InlineData(Scene.MoonRadius, Scene.HomeElevation, "elevation")]
    public void A_moon_map_made_for_another_radius_is_refused(float radius, float elevation, string named)
    {
        const int n = 16;
        var count = 6 * (n + 2) * (n + 2);
        var map = new TerrainMap(n, radius, elevation, -1f, 1f, new ushort[count], new byte[count], new byte[count], []);
        var folder = Directory.CreateTempSubdirectory("gameone-moon-");
        try
        {
            var path = Path.Combine(folder.FullName, "moon.bin.gz");
            map.Save(path);
            var error = Assert.Throws<InvalidDataException>(() => TerrainMap.Load(path, Scene.MoonRadius, 0f));
            Assert.Contains("moon.bin.gz", error.Message);
            Assert.Contains(named, error.Message);
            Assert.Contains("regenerate", error.Message);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_two_craters_are_a_fifth_and_an_eighth_of_the_moons_width()
    {
        Assert.Equal(2, L.Craters.Count);
        float[] wanted = [2f * Scene.MoonRadius / 5f, 2f * Scene.MoonRadius / 8f];
        for (var c = 0; c < 2; c++)
        {
            var crater = L.Craters[c];
            Assert.Equal(wanted[c], crater.Diameter, 1f);
            var centre = At(crater.At);
            var radius = crater.Diameter * 0.5f;

            // Rim to rim along the line through its centre, east to west: each side's crest the
            // highest ground between half and one and a half radii out.
            var rims = new List<(float Distance, float Height)>();
            foreach (var bearing in new[] { 90f, 270f })
            {
                var best = (Distance: 0f, Height: float.MinValue);
                for (var m = radius * 0.5f; m <= radius * 1.5f; m += 1f)
                {
                    var h = MoonGround.Height(MoonGenerator.Out(centre, bearing, m));
                    if (h > best.Height) best = (m, h);
                }

                rims.Add(best);
            }

            var across = rims[0].Distance + rims[1].Distance;
            // The plains round it: a ring out north of it, clear of the canyon to its south.
            var plains = 0f;
            var samples = 0;
            for (var bearing = -60f; bearing <= 60f; bearing += 10f, samples++)
                plains += MoonGround.Height(MoonGenerator.Out(centre, bearing, radius * 2.2f));
            plains /= samples;
            var floor = MoonGround.Height(centre);
            output.WriteLine($"{crater.Name}: rim to rim {across:F0} m (wanted {wanted[c]:F0}), rims {rims[0].Height:F1} and {rims[1].Height:F1} m, floor {floor:F1} m, plains round it {plains:F1} m");

            Assert.InRange(across, wanted[c] * 0.9f, wanted[c] * 1.1f);
            Assert.All(rims, rim => Assert.True(rim.Height > plains + crater.RimHeight * 0.5f, $"{crater.Name}'s rim is {rim.Height - plains:F1} m above the plains"));
            Assert.True(floor < plains - crater.Depth * 0.8f, $"{crater.Name}'s floor is {plains - floor:F1} m below the plains");
            Assert.Equal(MoonKind.Floor, MoonGround.Kind(centre));
            Assert.Equal(MoonKind.Rim, MoonGround.Kind(MoonGenerator.Out(centre, 0f, radius)));
        }
    }

    [Fact]
    public void The_canyon_is_a_crescent_south_of_the_craters()
    {
        var c = L.Canyon;
        var centre = At(c.At);

        // Along its rough path, its horns' last shallowing left out: its floor (where the generator
        // has its middle line) well below the higher of the plains just outside its walls, by most
        // of its depth there; that depth never under a third of the layout's (a slump fills it part
        // way) and mostly over half; and its middle line on the floor.
        int deep = 0, onFloor = 0, samples = 0;
        float narrowest = float.MaxValue, widest = 0f, nearest = float.MaxValue, farthest = 0f;
        for (var f = 0.2f; f <= 0.8f; f += 0.02f, samples++)
        {
            var at = Canyon(f);
            var floor = MoonGround.Height(at.Middle);
            var beyond = at.Width * 0.5f + 60f;
            var plains = MathF.Max(MoonGround.Height(MoonGenerator.Out(centre, at.Bearing, at.Radius - beyond)),
                MoonGround.Height(MoonGenerator.Out(centre, at.Bearing, at.Radius + beyond)));
            Assert.True(plains - floor >= at.Depth * 0.6f, $"at {at.Bearing:F0}° the canyon's floor is only {plains - floor:F1} m below the plains (its depth there {at.Depth:F1} m)");
            Assert.True(at.Depth >= c.Depth / 3f, $"at {at.Bearing:F0}° the canyon is only {at.Depth:F1} m deep");
            if (at.Depth >= c.Depth * 0.5f) deep++;
            if (MoonGround.Kind(at.Middle) == MoonKind.Floor) onFloor++;
            (narrowest, widest) = (MathF.Min(narrowest, at.Width), MathF.Max(widest, at.Width));
            (nearest, farthest) = (MathF.Min(nearest, at.Radius), MathF.Max(farthest, at.Radius));
        }

        output.WriteLine($"canyon: {deep} of {samples} sections over half its depth, {onFloor} on its floor; {narrowest:F0} to {widest:F0} m wide; its middle {nearest:F0} to {farthest:F0} m out");
        Assert.True(deep >= samples * 0.8f, $"only {deep} of {samples} sections are over half the canyon's depth");
        Assert.True(onFloor >= samples * 0.9f, $"only {onFloor} of {samples} sections' middles are on its floor");
        // Rough, not an even arc: its width and its path both wander.
        Assert.True(widest > narrowest * 1.3f, "the canyon's width hardly changes along it");
        Assert.True(farthest - nearest > c.Wander, "the canyon's path hardly wanders");

        // Every point of its path south of both craters' centres, and of the lower one.
        var craterLatitude = L.Craters.Min(k => k.At[0]);
        for (var f = 0f; f <= 1.0001f; f += 0.02f)
        {
            var latitude = Latitude(Canyon(f).Middle);
            Assert.True(latitude < craterLatitude, $"the canyon reaches {latitude:F1}° N, north of the lower crater at {craterLatitude:F1}°");
        }

        // Its bulge to the south: the middle far south of the horns, and of the craters.
        var middle = Latitude(Canyon(0.5f).Middle);
        var startHorn = Canyon(0f).Middle;
        var endHorn = Canyon(1f).Middle;
        output.WriteLine($"canyon: middle {middle:F1}° N, horns {Latitude(startHorn):F1}° N {Longitude(startHorn):F1}° E and {Latitude(endHorn):F1}° N {Longitude(endHorn):F1}° E");
        Assert.True(middle < Latitude(startHorn) - 10f && middle < Latitude(endHorn) - 10f, "the canyon doesn't bulge south");
        Assert.True(middle < craterLatitude - 25f, "the canyon's middle isn't well south of the craters");

        // Its horns curving up round the craters: each out past the crater on its side, clear of it.
        var west = L.Craters.MinBy(k => k.At[1])!;
        var east = L.Craters.MaxBy(k => k.At[1])!;
        var (westHorn, eastHorn) = Longitude(startHorn) < Longitude(endHorn) ? (startHorn, endHorn) : (endHorn, startHorn);
        Assert.True(MoonGenerator.Arc(westHorn, At(west.At)) > west.Diameter * 0.5f + c.Width && Longitude(westHorn) < west.At[1], "the west horn doesn't curve round the west crater");
        Assert.True(MoonGenerator.Arc(eastHorn, At(east.At)) > east.Diameter * 0.5f + c.Width && Longitude(eastHorn) > east.At[1], "the east horn doesn't curve round the east crater");
    }

    [Fact]
    public void The_two_craters_are_not_level_side_by_side()
    {
        var (a, b) = (L.Craters[0], L.Craters[1]);
        Assert.True(MathF.Abs(a.At[0] - b.At[0]) >= 5f, "the two craters sit at the same latitude");
        Assert.True(MathF.Abs(a.At[1] - b.At[1]) >= 40f, "the two craters are close together");
        // Both on the near side, where Home sees them face on.
        Assert.All(L.Craters, k => Assert.True(MoonGenerator.Arc(At(k.At), At(0f, 0f)) < Scene.MoonRadius * 35f * MathF.PI / 180f, $"{k.Name} isn't near the near side's middle"));
    }

    /// <summary>How far a place is from the big features' edges (the craters' rims, the canyon's along its rough path), in metres.</summary>
    private static float FromFeatures(Vector3 d)
    {
        var nearest = float.MaxValue;
        foreach (var crater in L.Craters)
            nearest = MathF.Min(nearest, MoonGenerator.Arc(d, At(crater.At)) - crater.Diameter * 0.5f);
        for (var f = 0f; f <= 1.0001f; f += 0.005f)
        {
            var at = Canyon(f);
            nearest = MathF.Min(nearest, MoonGenerator.Arc(d, at.Middle) - at.Width * 0.5f);
        }

        // The far side's tail: its ridge (at its widest, every few samples) and its mountain.
        var tail = Generator.Value.Tail;
        nearest = MathF.Min(nearest, MoonGenerator.Arc(d, tail.Summit) - L.Tail.MountainRadius);
        for (var i = 0; i < tail.Points.Length; i += 4)
            nearest = MathF.Min(nearest, MoonGenerator.Arc(d, tail.Points[i]) - 0.5f * L.Tail.BaseWidth - L.Tail.Wander);
        return nearest;
    }

    [Fact]
    public void The_far_side_has_a_tail_rising_to_a_mountain_with_a_crater_in_its_top()
    {
        var t = L.Tail;
        var tail = Generator.Value.Tail;
        var nearSide = At(0f, 0f);
        output.WriteLine($"tail: {tail.Length:F0} m long, tip {Latitude(tail.Points[0]):F1}° N {Longitude(tail.Points[0]):F1}° E, summit {Latitude(tail.Summit):F1}° N {Longitude(tail.Summit):F1}° E");
        Assert.InRange(tail.Length, 4500f, 5500f);

        // All on the far side, the mountain's foot included, and far behind the landing spot's horizon.
        var mountainAngle = t.MountainRadius / Scene.MoonRadius;
        Assert.All(tail.Points, p => Assert.True(Vector3.Dot(p, nearSide) < -0.3f, "the tail reaches the near side"));
        Assert.True(MathF.Acos(Vector3.Dot(tail.Summit, nearSide)) - mountainAngle > MathF.PI / 2f, "the mountain's foot reaches the near side");
        var spot = LandingSpot;
        Assert.True(tail.Points.Min(p => MoonGenerator.Arc(p, spot)) > 1500f, "the tail is close to the landing spot");
        Assert.True(MoonGenerator.Arc(tail.Summit, spot) - t.MountainRadius > 1500f, "the mountain is close to the landing spot");

        // The ridge along its line: its highest across it (the line wanders by up to Wander) above the
        // plains beyond its foot, rising from its thin tip towards the mountain, out on the plains
        // (clear of the mountain's foot).
        float Half(int i) => 0.5f * (t.TipWidth + (t.BaseWidth - t.TipWidth) * tail.Along[i]) + t.Wander;
        Vector3 Off(int i, float metres)
        {
            var p = tail.Points[i];
            var side = Vector3.Normalize(Vector3.Cross(p, tail.Points[i + 1] - p));
            return Vector3.Normalize(p + side * (metres / Scene.MoonRadius));
        }

        float Crest(int i)
        {
            var crest = float.MinValue;
            for (var m = -Half(i); m <= Half(i); m += 2f) crest = MathF.Max(crest, MoonGround.Height(Off(i, m)));
            return crest;
        }

        float RiseAt(int i) => Crest(i) - 0.5f * (MoonGround.Height(Off(i, -Half(i) - 150f)) + MoonGround.Height(Off(i, Half(i) + 150f)));

        var onPlains = Enumerable.Range(0, tail.Points.Length - 1)
            .Where(i => MoonGenerator.Arc(tail.Points[i], tail.Summit) > t.MountainRadius + 0.5f * t.BaseWidth + 150f).ToList();
        var plainsEnd = tail.Along[onPlains[^1]];
        float MeanRise(float from, float to)
        {
            var rises = onPlains.Where(i => tail.Along[i] >= from * plainsEnd && tail.Along[i] <= to * plainsEnd).Select(RiseAt).ToList();
            Assert.All(rises, rise => Assert.True(rise > 0f, "the ridge is missing along its line"));
            return rises.Average();
        }

        var (tip, middle, base_) = (MeanRise(0.05f, 0.35f), MeanRise(0.4f, 0.65f), MeanRise(0.75f, 1f));
        output.WriteLine($"ridge above the plains: {tip:F1} m near its tip, {middle:F1} m in its middle, {base_:F1} m by the mountain's foot (the plains' stretch ending {plainsEnd:P0} along)");
        Assert.True(tip < middle && middle < base_, "the ridge doesn't rise from its tip to the mountain");
        Assert.True(tip < 40f, $"the ridge is {tip:F1} m high near its tip");
        Assert.True(base_ > 80f, $"the ridge is only {base_:F1} m high by the mountain");

        // Up the mountain's flank it is a spur, standing out of the flank (above the flank as high
        // the other side of the summit) and sloping down off it: where it still stands out, its crest
        // is well below the summit.
        var round = 0f;
        for (var bearing = 0f; bearing < 360f; bearing += 30f) round += MoonGround.Height(MoonGenerator.Out(tail.Summit, bearing, t.MountainRadius * 1.3f)) / 12f;
        var spur = Enumerable.Range(0, tail.Points.Length - 1)
            .Where(i => MoonGenerator.Arc(tail.Points[i], tail.Summit) is var r && r > t.CraterDiameter && r < t.MountainRadius)
            .Select(i =>
            {
                var p = tail.Points[i];
                var across = Vector3.Normalize(2f * Vector3.Dot(p, tail.Summit) * tail.Summit - p); // the same distance out the other side
                return (Crest: Crest(i) - round, Out: Crest(i) - MoonGround.Height(across));
            }).Where(s => s.Out > 20f).ToList();
        Assert.NotEmpty(spur);
        var joins = spur.Max(s => s.Crest);
        output.WriteLine($"spur: {spur.Count} points standing out of the flank (by up to {spur.Max(s => s.Out):F0} m), the highest {joins:F0} m above the plains");
        Assert.True(spur.Max(s => s.Out) > 50f, "the ridge hardly stands out of the mountain's flank");
        Assert.True(joins < t.MountainHeight * 0.55f, $"the spur reaches {joins:F0} m up the mountain, near its summit");

        // The mountain: its summit crater's rim about the layout's height over the plains round its
        // foot, and the crater's floor well below its rim.
        var rc = t.CraterDiameter * 0.5f;
        var rim = float.MinValue;
        for (var bearing = 0f; bearing < 360f; bearing += 10f)
            for (var m = 0f; m <= rc * 1.5f; m += 5f)
                rim = MathF.Max(rim, MoonGround.Height(MoonGenerator.Out(tail.Summit, bearing, m)));
        var floor = MoonGround.Height(tail.Summit);
        output.WriteLine($"mountain: rim {rim - round:F0} m above the plains round it, its crater's floor {rim - floor:F0} m below the rim");
        Assert.InRange(rim - round, t.MountainHeight * 0.9f, t.MountainHeight * 1.15f);
        Assert.True(rim - floor > t.CraterDepth * 0.7f, $"the summit crater's floor is only {rim - floor:F0} m below its rim");
        Assert.Equal(MoonKind.Floor, MoonGround.Kind(tail.Summit));
        Assert.Equal(MoonKind.Rim, MoonGround.Kind(MoonGenerator.Out(tail.Summit, 0f, rc)));

        // Its long shadow: on the plains 1.8 km east of the summit, with the sun just up (0.5°) over
        // it, the mountain hides the sun; with the sun as low the other way, nothing does.
        var east = MoonGenerator.Out(tail.Summit, 90f, 1800f);
        var towards = Vector3.Normalize(tail.Summit - east * Vector3.Dot(tail.Summit, east));
        var grazing = 0.5f * MathF.PI / 180f;
        var behind = MoonGround.SunSeenOver(east, Vector3.Normalize(towards * MathF.Cos(grazing) + east * MathF.Sin(grazing)));
        var away = MoonGround.SunSeenOver(east, Vector3.Normalize(-towards * MathF.Cos(5f * grazing) + east * MathF.Sin(5f * grazing)));
        output.WriteLine($"1.8 km east of the summit: sun seen {behind:F3} behind the mountain, {away:F3} the other way at 2.5°");
        Assert.True(behind < 0.1f, $"the mountain casts no shadow 1.8 km off ({behind:F3})");
        Assert.True(away > 0.9f, $"the plains beside the mountain see only {away:F3} of a low sun away from it");
    }

    /// <summary>Random places on the moon at least <paramref name="clear"/> metres from the big features, the same each run.</summary>
    private static IEnumerable<Vector3> PlainsPoints(int seed, float clear)
    {
        var random = new Random(seed);
        while (true)
        {
            var z = 2f * random.NextSingle() - 1f;
            var around = 2f * MathF.PI * random.NextSingle();
            var ring = MathF.Sqrt(1f - z * z);
            var d = new Vector3(ring * MathF.Cos(around), ring * MathF.Sin(around), z);
            if (FromFeatures(d) >= clear) yield return d;
        }
    }

    /// <summary>The ground's slope at a place over 10 m (from heights 5 m each way east and north of it), in degrees: a crater's wall, not the fine detail's ripples.</summary>
    private static float Slope10(Vector3 d)
    {
        var (east, north) = MoonGenerator.Across(d);
        var angle = 5f / Scene.MoonRadius;
        float H(Vector3 way, float sign) => MoonGround.Height(Vector3.Normalize(d + way * (sign * angle)));
        var dx = (H(east, 1f) - H(east, -1f)) / 10f;
        var dy = (H(north, 1f) - H(north, -1f)) / 10f;
        return MathF.Atan(MathF.Sqrt(dx * dx + dy * dy)) * 180f / MathF.PI;
    }

    private static bool InFlats(Vector3 d) => Generator.Value.InFlats(d);

    [Fact]
    public void The_plains_are_crowded_with_craters()
    {
        // Out on the plains, away from the big features: a crater's wall (over 15° across 10 m) under
        // a good share of them (a few hundred scattered craters would put one under about 2%), and
        // some of the larger ones' faintly pale rims among them.
        var points = PlainsPoints(17, 600f).Take(2000).ToList();
        var steep = points.Count(d => Slope10(d) > 15f) / (float)points.Count;
        var steepOutside = points.Where(d => !InFlats(d)).ToList();
        var steepThere = steepOutside.Count(d => Slope10(d) > 15f) / (float)steepOutside.Count;
        var pale = points.Count(d => MoonGround.Kind(d) == MoonKind.SmallRim);
        output.WriteLine($"plains: {steep:P1} of {points.Count} places over 15° across 10 m ({steepThere:P1} outside the flats), {pale} on a small crater's pale rim");
        Assert.True(steep > 0.06f, $"only {steep:P1} of the plains are a crater's wall");
        Assert.True(pale >= 5, $"only {pale} places are on a small crater's pale rim");
        Assert.True(Scene.MoonAlbedos[(int)MoonKind.SmallRim] > Scene.MoonAlbedo && Scene.MoonAlbedos[(int)MoonKind.SmallRim] < Scene.MoonAlbedos[(int)MoonKind.Rim]);
    }

    [Fact]
    public void The_flats_are_a_third_of_the_ground_and_gentle()
    {
        var random = new Random(23);
        var inFlats = 0;
        const int samples = 4000;
        for (var i = 0; i < samples; i++)
        {
            var z = 2f * random.NextSingle() - 1f;
            var around = 2f * MathF.PI * random.NextSingle();
            var ring = MathF.Sqrt(1f - z * z);
            if (InFlats(new Vector3(ring * MathF.Cos(around), ring * MathF.Sin(around), z))) inFlats++;
        }

        // In them, away from the big features and the rock thrown out round the craters, nearly all
        // gentle; and the landing spot in one.
        var slopes = PlainsPoints(7, 600f).Where(InFlats).Take(800).Select(Slope).ToList();
        slopes.Sort();
        var under10 = slopes.Count(s => s < 10f) / (float)slopes.Count;
        output.WriteLine($"flats: {inFlats / (float)samples:P1} of the ground; median slope {slopes[slopes.Count / 2]:F2}°, 95th percentile {slopes[(int)(slopes.Count * 0.95f)]:F2}°, steepest {slopes[^1]:F1}°, {under10:P1} under 10°");
        Assert.InRange(inFlats / (float)samples, 0.28f, 0.4f);
        Assert.True(under10 >= 0.97f, $"only {under10:P1} of the flats are under 10°");
        Assert.True(InFlats(LandingSpot));
    }

    /// <summary>Whether the ground 60 m across round <paramref name="d"/> is flat: under 5° across 10 m at its middle and at 16 places on two rings round it.</summary>
    private static bool FlatPatch(Vector3 d)
    {
        if (Slope10(d) >= 5f) return false;
        foreach (var ring in new[] { 15f, 30f })
            for (var bearing = 0f; bearing < 360f; bearing += 45f)
                if (Slope10(MoonGenerator.Out(d, bearing, ring)) >= 5f) return false;
        return true;
    }

    [Fact]
    public void A_flat_patch_is_never_far()
    {
        // From 50 places anywhere outside the big features, a patch 60 m across flat enough to land
        // on within 300 m: searched outward in rings 20 m apart.
        var farthest = 0f;
        foreach (var from in PlainsPoints(29, 0f).Take(50))
        {
            float? found = null;
            for (var ring = 0f; ring <= 300f && found == null; ring += 20f)
            {
                var steps = ring == 0f ? 1 : (int)MathF.Ceiling(2f * MathF.PI * ring / 20f);
                for (var s = 0; s < steps && found == null; s++)
                    if (FlatPatch(ring == 0f ? from : MoonGenerator.Out(from, 360f * s / steps, ring)))
                        found = ring;
            }

            Assert.True(found != null, $"no flat patch within 300 m of {Latitude(from):F1}° N {Longitude(from):F1}° E");
            farthest = MathF.Max(farthest, found!.Value);
        }

        output.WriteLine($"flat patches: the farthest of 50 places is {farthest:F0} m from one");
    }

    [Fact]
    public void The_landing_spot_is_level_plains_clear_of_the_features()
    {
        var (latitude, longitude) = Scene.MoonLandingSpot;
        Assert.Equal(0f, longitude);
        Assert.InRange(latitude, 60f, 72f);

        var spot = LandingSpot;
        var steepest = 0f;
        float lowest = float.MaxValue, highest = float.MinValue;
        for (var ring = 0f; ring <= Scene.MoonLandingSpotRadius; ring += 10f)
        {
            for (var bearing = 0f; bearing < 360f; bearing += ring == 0f ? 360f : 15f)
            {
                var d = MoonGenerator.Out(spot, bearing, ring);
                steepest = MathF.Max(steepest, Slope(d));
                Assert.Equal(MoonKind.Plain, MoonGround.Kind(d));
                var mapHeight = MoonGround.BaseHeight(MoonGround.Map, d);
                (lowest, highest) = (MathF.Min(lowest, mapHeight), MathF.Max(highest, mapHeight));
            }
        }

        var clear = FromFeatures(spot) - Scene.MoonLandingSpotRadius;
        output.WriteLine($"landing spot: steepest {steepest:F2}°, the map's heights over it {highest - lowest:F4} m apart, {clear:F0} m clear of the big features");
        Assert.True(steepest < 3f, $"the landing spot slopes at up to {steepest:F2}°");
        // Levelled, not merely gentle: the map itself is flat there (its fine detail alone left).
        Assert.True(highest - lowest < 0.05f, $"the map's heights over the landing spot span {highest - lowest:F3} m");
        Assert.True(clear > 1000f, $"the landing spot is only {clear:F0} m from a big feature");
    }

    [Fact]
    public void The_rims_and_rays_are_paler_than_the_plains()
    {
        Assert.Equal(Scene.MoonAlbedo, Scene.MoonAlbedos[(int)MoonKind.Plain]);
        Assert.Equal(Scene.MoonAlbedo, Scene.MoonAlbedos[(int)MoonKind.Floor]);
        Assert.True(Scene.MoonAlbedos[(int)MoonKind.Rim] > Scene.MoonAlbedo);
        Assert.True(Scene.MoonAlbedos[(int)MoonKind.Ray] > Scene.MoonAlbedo);

        foreach (var crater in L.Craters)
        {
            var centre = At(crater.At);
            var radius = crater.Diameter * 0.5f;
            // All round its crest, pale.
            for (var bearing = 0f; bearing < 360f; bearing += 10f)
                Assert.True(MoonGround.Albedo(MoonGenerator.Out(centre, bearing, radius)) > Scene.MoonAlbedo + 0.03f, $"{crater.Name}'s rim isn't pale at {bearing}°");

            // Its rays: streaks of pale ground out over the plains round it, only part of the way round.
            var random = new Random(11);
            int rays = 0, plains = 0;
            for (var i = 0; i < 2000; i++)
            {
                var d = MoonGenerator.Out(centre, 360f * random.NextSingle(), radius * (1.5f + random.NextSingle()));
                switch (MoonGround.Kind(d))
                {
                    case MoonKind.Ray:
                        rays++;
                        Assert.True(MoonGround.Albedo(d) > Scene.MoonAlbedo, $"{crater.Name}'s ray isn't paler there");
                        break;
                    case MoonKind.Plain:
                        plains++;
                        break;
                }
            }

            output.WriteLine($"{crater.Name}: {rays} of 2,000 points round it on its rays, {plains} on the plains");
            Assert.InRange(rays, 40, 1000);
        }
    }

    [Fact]
    public void With_the_sun_low_across_it_the_canyon_floor_is_in_shadow()
    {
        var centre = At(L.Canyon.At);
        // On the floor at the canyon's bottom, halfway from its middle line to the inner wall's
        // foot (the wall nearer the canyon's centre, to the north there), with the sun 5° up over
        // that wall.
        var bottom = Canyon(0.5f);
        var floor = MoonGenerator.Out(centre, bottom.Bearing, bottom.Radius - 0.5f * MoonGenerator.CanyonFloor * bottom.Width);
        Assert.Equal(MoonKind.Floor, MoonGround.Kind(floor));
        var toWall = Vector3.Normalize(centre - floor * Vector3.Dot(centre, floor));
        var low = 5f * MathF.PI / 180f;
        var shadowed = MoonGround.SunSeenOver(floor, Vector3.Normalize(toWall * MathF.Cos(low) + floor * MathF.Sin(low)));
        var overhead = MoonGround.SunSeenOver(floor, floor);

        // The flats with the sun as low, from any way: lit nearly everywhere (a small crater's rim
        // may shadow a few).
        var random = new Random(5);
        var seen = PlainsPoints(13, 600f).Where(InFlats).Take(60).Select(d =>
        {
            var (east, north) = MoonGenerator.Across(d);
            var bearing = 2f * MathF.PI * random.NextSingle();
            var way = north * MathF.Cos(bearing) + east * MathF.Sin(bearing);
            return MoonGround.SunSeenOver(d, Vector3.Normalize(way * MathF.Cos(low) + d * MathF.Sin(low)));
        }).ToList();
        var lit = seen.Count(s => s > 0.9f);

        // And the sun's middle on the level landing spot's horizon: only part of its disc seen.
        var spot = LandingSpot;
        var grazing = MoonGround.SunSeenOver(spot, -MoonGenerator.Across(spot).North);
        output.WriteLine($"sun seen: on the canyon's floor {shadowed:F3} at 5°, {overhead:F3} overhead; {lit} of {seen.Count} places in the flats over 0.9 at 5° (least {seen.Min():F3}); on the horizon {grazing:F3}");
        Assert.True(shadowed < 0.1f, $"the canyon's floor sees {shadowed:F3} of the sun at 5°");
        Assert.True(overhead > 0.9f, $"the canyon's floor sees only {overhead:F3} of the sun overhead");
        Assert.True(lit >= seen.Count * 0.9f, $"only {lit} of {seen.Count} places in the flats see the sun at 5°");
        Assert.True(grazing > 0.1f && grazing < 0.9f, $"with the sun on the horizon the spot sees {grazing:F3} of it");
    }

    private static string LayoutPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "TerrainGen", "moon.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tools", "TerrainGen", "moon.json");
    }
}
