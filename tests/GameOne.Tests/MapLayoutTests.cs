using System.Numerics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The shipped map (<c>Assets/terrain.bin.gz</c>, made by tools/TerrainGen from its layout.json):
/// the launch site laid out as it should be (the pad level, the sea to the east under the launch
/// path, peaks inland to the west), nothing extreme, several seas, the icy ones' ice, deserts where
/// they belong, and no seams across the cube's edges.
/// </summary>
public sealed class MapLayoutTests(ITestOutputHelper output)
{
    private static readonly Lazy<TerrainMap> Shipped = new(() =>
        TerrainMap.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "terrain.bin.gz")));

    private static TerrainMap Map => Shipped.Value;

    private static int Side => Map.Resolution + 2;

    /// <summary>The ground's height (elevation above the sea) in a direction.</summary>
    private static float HeightAt(Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        return Map.HeightAt(face, u, v);
    }

    /// <summary>The direction <paramref name="metres"/> from <paramref name="from"/> along the ground, on a bearing (degrees clockwise from north).</summary>
    private static Vector3 Travel(Vector3 from, float bearingDegrees, float metres)
    {
        var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, -1), from)); // as Planet.East
        var north = Vector3.Cross(from, east);
        var b = bearingDegrees * MathF.PI / 180f;
        var heading = north * MathF.Cos(b) + east * MathF.Sin(b);
        var angle = metres / Map.Radius;
        return Vector3.Normalize(from * MathF.Cos(angle) + heading * MathF.Sin(angle));
    }

    /// <summary>The centre direction of a map texel (interior or apron).</summary>
    private static Vector3 TexelDirection(int face, int i, int j) =>
        CubeSphere.FromFace(face, ((i - 0.5f) / Map.Resolution) * 2f - 1f, ((j - 0.5f) / Map.Resolution) * 2f - 1f);

    /// <summary>Every interior texel whose centre is within <paramref name="metres"/> of <paramref name="centre"/>.</summary>
    private static List<int> TexelsWithin(Vector3 centre, float metres)
    {
        var (face, u, v) = CubeSphere.ToFace(centre);
        var n = Map.Resolution;
        var ci = (int)((u + 1f) * 0.5f * n) + 1;
        var cj = (int)((v + 1f) * 0.5f * n) + 1;
        var reach = (int)(metres / (Map.Radius * MathF.PI / 2f / n)) + 3;
        var found = new List<int>();
        for (var j = Math.Max(1, cj - reach); j <= Math.Min(n, cj + reach); j++)
        {
            for (var i = Math.Max(1, ci - reach); i <= Math.Min(n, ci + reach); i++)
            {
                var d = TexelDirection(face, i, j);
                if (Map.Radius * MathF.Acos(Math.Clamp(Vector3.Dot(d, centre), -1f, 1f)) <= metres)
                    found.Add(face * Side * Side + j * Side + i);
            }
        }

        return found;
    }

    private static float Metres(ushort h) => Map.MinHeight + (Map.MaxHeight - Map.MinHeight) * h / 65535f;

    private static void AssertLevelSite(Vector3 centre, float metres, float elevation = Scene.HomeElevation)
    {
        var texels = TexelsWithin(centre, metres);
        Assert.NotEmpty(texels);
        foreach (var t in texels)
        {
            Assert.True(MathF.Abs(Metres(Map.Heights[t]) - elevation) < 0.05f, $"texel {t} is at {Metres(Map.Heights[t])} m, not {elevation} m");
            Assert.Equal(SurfaceKind.Site, (SurfaceKind)Map.Kinds[t]);
        }

        // And the surface between the texels, as the game reads it.
        var rng = new Random(7);
        for (var n = 0; n < 500; n++)
        {
            var p = Travel(centre, rng.NextSingle() * 360f, MathF.Sqrt(rng.NextSingle()) * metres);
            Assert.True(MathF.Abs(HeightAt(p) - elevation) < 0.05f, $"{HeightAt(p)} m inside the site, not {elevation} m");
        }
    }

    [Fact]
    public void Every_launch_site_is_on_land()
    {
        // (Home's is level at its elevation, below; the others stand at the highest ground under them, above.)
        foreach (var site in LaunchSites.All)
        {
            output.WriteLine($"{site.Name}: {site.Elevation:0.0} m, its zone {site.ZoneElevation:0.0} m");
            Assert.True(HeightAt(site.Up) > 0f, $"{site.Name} is at sea");
        }
    }

    private static LaunchSite Site(string name) => LaunchSites.All.Single(s => s.Name == name);

    [Fact]
    public void The_high_sites_are_high()
    {
        Assert.True(HeightAt(Site("Highcrest").Up) > 1000f, $"Highcrest is {HeightAt(Site("Highcrest").Up):0} m up");
        Assert.True(Site("Polar").Latitude <= -68f, "Polar isn't near the south pole"); // the land is low there: Polar is for polar orbits, not height
    }

    [Fact]
    public void Coral_Isle_is_an_island()
    {
        // Sea within 1.5 km every way round it.
        var centre = Site("Coral Isle").Up;
        for (var bearing = 0f; bearing < 360f; bearing += 45f)
            Assert.True(Enumerable.Range(1, 30).Any(k => HeightAt(Travel(centre, bearing, k * 50f)) < 0f),
                $"no sea within 1.5 km on a bearing of {bearing}°");
    }

    [Fact]
    public void Northreach_is_on_the_cold_north_coast()
    {
        var site = Site("Northreach");
        Assert.True(site.Latitude > 60f);
        // An icy sea within 1.5 km of it.
        var icy = Map.Seas.Select((sea, i) => (sea, region: (byte)(i + 1))).Where(s => s.sea.Kind == SeaKind.Icy).Select(s => s.region).ToHashSet();
        var near = false;
        for (var bearing = 0f; bearing < 360f && !near; bearing += 15f)
            for (var k = 1; k <= 30 && !near; k++)
            {
                var d = Travel(site.Up, bearing, k * 50f);
                var (face, u, v) = CubeSphere.ToFace(d);
                near = HeightAt(d) < 0f && icy.Contains(Map.RegionAt(face, u, v));
            }
        Assert.True(near, "no icy sea within 1.5 km of Northreach");
    }

    /// <summary>The ground under a far site's pad (a 0.5 m grid over its square, finer than the game's 1 m, so a high point between its samples shows) and its zone (over its disc), as the game reads it.</summary>
    private static IEnumerable<(string Name, float Level, IEnumerable<float> Ground)> FarFootprints()
    {
        foreach (var site in LaunchSites.All.Skip(1))
        {
            yield return ($"{site.Name}'s pad", site.Elevation, Grid(site.Pad, site.Frame, Scene.PadHalfSize, (x, z) => MathF.Max(MathF.Abs(x), MathF.Abs(z)) <= Scene.PadHalfSize));
            if (site.LandingZone is { } zone)
                yield return ($"{site.Name}'s zone", site.ZoneElevation, Grid(zone, site.Frame, Scene.LandingZoneRadius, (x, z) => x * x + z * z <= Scene.LandingZoneRadius * Scene.LandingZoneRadius));
        }

        // Out to the footprint's half size or radius (reach), either way.
        static IEnumerable<float> Grid(Vector3 centre, Quaternion frame, float reach, Func<float, float, bool> inside)
        {
            var steps = (int)MathF.Ceiling(reach * 2f);
            for (var x = -steps * 0.5f; x <= steps * 0.5f; x += 0.5f)
            for (var z = -steps * 0.5f; z <= steps * 0.5f; z += 0.5f)
                if (inside(x, z))
                    yield return Terrain.Height(Planet.Up(centre + Vector3.Transform(new Vector3(x, 0f, z), frame)));
        }
    }

    [Fact]
    public void A_far_pad_stands_at_the_highest_ground_under_it()
    {
        // So its foundation lifts it clear of the ground (never cut into it), levelled no more than it needs.
        foreach (var (name, level, ground) in FarFootprints())
        {
            var highest = ground.Max();
            Assert.True(highest <= level + 0.05f, $"the ground under {name} reaches {highest:0.00} m, over its {level:0.00} m");
            Assert.True(highest >= level - 0.3f, $"the ground under {name} is at most {highest:0.00} m, under its {level:0.00} m");
        }
    }

    [Fact]
    public void A_steep_site_falls_away_beside_its_pad()
    {
        // Its ground isn't levelled into a gentle slope: just past the pad, the mountain drops away.
        // (Polar, on its slope: Highcrest is up on a plateau, only ~4 m down 25 m out.)
        var site = LaunchSites.Named("Polar");
        var lowest = Enumerable.Range(0, 32).Min(k => HeightAt(Travel(site.Up, k * 360f / 32f, 25f)));
        Assert.True(lowest < site.Elevation - 8f, $"only {site.Elevation - lowest:0.0} m below the pad, 25 m out");
    }

    [Fact]
    public void The_pad_site_is_level_at_Homes_elevation()
    {
        AssertLevelSite(Planet.Up(Vector3.Zero), 60f);
        AssertLevelSite(Planet.Up(Scene.LandingZoneCentre), 40f);
    }

    [Fact]
    public void The_test_pads_stand_on_levelled_ground_away_from_the_coast()
    {
        Assert.Equal(6, Scene.TestPads.Length);
        foreach (var (name, centre, _, _) in Scene.TestPads)
        {
            var under = Planet.Up(centre);
            Assert.True(MathF.Abs(HeightAt(under) - Scene.HomeElevation) <= 0.5f, $"the ground under {name} is at {HeightAt(under)} m");

            // On land, with no sea within 150 m.
            for (var bearing = 0f; bearing < 360f; bearing += 5f)
            for (var d = 0f; d <= 150f; d += 5f)
                Assert.True(HeightAt(Travel(under, bearing, d)) > 0f, $"sea {d} m from {name}, on a bearing of {bearing}°");

            foreach (var (other, _) in Scene.LandingZones)
            {
                if (other == centre) continue;
                var apart = Planet.Radius * Planet.AngleBetween(centre, other);
                Assert.True(apart >= 2 * Scene.LandingZoneRadius, $"{name} is only {apart} m from the zone at {other}");
            }
        }
    }

    [Fact]
    public void The_sea_is_east_of_the_pad_under_the_launch_path()
    {
        // "East" is arc length along the equator. The pad stands on raised ground, 35 m above the
        // sea, with the coast ~330 m east (the layout's outline; ~310 m on the map): land to 280 m,
        // the shore between 280 and 400 m, and only sea from 400 m to 5 km.
        Vector3 East(float metres) => CubeSphere.FromLatLon(0f, metres / Map.Radius);

        for (var x = 0f; x <= 280f; x += 5f)
            Assert.True(HeightAt(East(x)) > 0f, $"sea {x} m east of the pad");

        var coast = false;
        for (var x = 280f; x <= 400f; x += 5f)
            coast |= HeightAt(East(x)) < 0f;
        Assert.True(coast, "no sea between 280 and 400 m east");

        for (var x = 400f; x <= 5000f; x += 5f)
            Assert.True(HeightAt(East(x)) < 0f, $"land {x} m east of the pad");

        // Under the drone ship (its deck's rectangle at rest, and 20 m round it), deep enough that
        // its keel never touches the seabed, with 10 m to spare.
        var ship = Scene.DroneShipCentre;
        var (shipEast, shipUp) = (Planet.East(ship), Planet.Up(ship));
        var across = Vector3.Cross(shipEast, shipUp);
        const float margin = 20f;
        for (var x = -Scene.DroneShipHalfLength - margin; x <= Scene.DroneShipHalfLength + margin; x += 2f)
            for (var z = -Scene.DroneShipHalfBeam - margin; z <= Scene.DroneShipHalfBeam + margin; z += 2f)
            {
                var depth = -HeightAt(Planet.Up(ship + shipEast * x + across * z));
                Assert.True(depth >= Scene.DroneShipDraft + 10f, $"only {depth:0.0} m deep {x} m along and {z} m across from the drone ship's centre");
            }

        Assert.True(HeightAt(East(5000f)) < -100f, $"only {-HeightAt(East(5000f))} m deep 5 km east");
    }

    [Fact]
    public void Peaks_rise_over_the_horizon_inland()
    {
        // How far a peak shows over the horizon from 30 m up at the pad: its height less the drop
        // of the curve beyond the horizon (~775 m off at 10 km). The Westwall's crest is ~2.5 km
        // west, so close that most of it shows: the best shows ~1,150 m.
        var r = Map.Radius;
        var horizon = MathF.Sqrt(2f * r * 30f);
        var pad = Planet.Up(Vector3.Zero);
        var best = float.MinValue;
        for (var bearing = 225f; bearing <= 315f; bearing += 1f)
        {
            for (var d = 1500f; d <= 8000f; d += 10f)
            {
                var shows = HeightAt(Travel(pad, bearing, d)) - (d - horizon) * (d - horizon) / (2f * r);
                best = MathF.Max(best, shows);
            }
        }

        Assert.True(best >= 800f, $"the highest peak inland shows only {best} m over the horizon");
    }

    // Above the sea, as the map's heights are (TerrainGen rounds its peaks off under 1,590 m above the sea).
    [Fact]
    public void Nothing_is_extreme() => Assert.True(Map.MaxHeight <= 1600f, $"the map rises to {Map.MaxHeight} m above the sea");

    [Fact]
    public void There_are_several_seas_warm_and_icy()
    {
        Assert.True(Map.Seas.Count >= 4, $"only {Map.Seas.Count} seas");
        Assert.Contains(Map.Seas, s => s.Kind == SeaKind.Warm);
        Assert.Contains(Map.Seas, s => s.Kind == SeaKind.Icy);
        foreach (var sea in Map.Seas)
        {
            Assert.True(sea.Waves > 0f, $"{sea.Name} has no waves");
            Assert.True(Vector3.Distance(sea.Shallow, sea.Deep) > 0.05f, $"{sea.Name}'s shallows and deeps are one colour");
        }

        var n = Map.Resolution;
        var counts = new long[Map.Seas.Count + 1];
        double water = 0, total = 0;
        for (var face = 0; face < 6; face++)
        {
            for (var j = 1; j <= n; j++)
            {
                for (var i = 1; i <= n; i++)
                {
                    // A texel's solid angle on the equal-angle cube: the angle it spans each way,
                    // squared, over the cube's stretch there.
                    var x = MathF.Tan((((i - 0.5f) / n) * 2f - 1f) * MathF.PI / 4f);
                    var y = MathF.Tan((((j - 0.5f) / n) * 2f - 1f) * MathF.PI / 4f);
                    var stretch = (1 + x * x) * (1 + y * y) / MathF.Pow(1 + x * x + y * y, 1.5f);
                    var region = Map.Regions[face * Side * Side + j * Side + i];
                    counts[region]++;
                    total += stretch;
                    if (region != 0) water += stretch;
                }
            }
        }

        for (var s = 1; s <= Map.Seas.Count; s++)
            Assert.True(counts[s] > 0, $"{Map.Seas[s - 1].Name} has no water");
        Assert.InRange(water / total, 0.40, 0.75);
    }

    [Fact]
    public void The_icy_seas_have_a_frozen_sheet_and_a_floe_band()
    {
        var icy = IceTests.Seas.Value;
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        Assert.Equal(["Austral Sea", "Boreal Sea"], icy.Keys.Order());
        foreach (var (name, (centre, pack, floes)) in icy)
        {
            float shortest = float.MaxValue, longest = 0f, narrowest = float.MaxValue, widest = 0f;
            for (var b = 0; b < 16; b++)
            {
                var bearing = b * 22.5f;
                // Out from the centre: the sheet, then the floes, then open water. Both edges are
                // ragged at the texels' size (each texel's own bearing sets them), so a line out can
                // graze one, leaving the sheet (or the floes) and touching it again a few metres on.
                const float ragged = 30f;
                var kinds = new List<(float Distance, SurfaceKind Kind)>();
                for (var d = 0f; d <= pack * 1.25f + floes + 500f; d += 5f)
                    kinds.Add((d, Terrain.Kind(IceTests.Travel(centre, bearing, d))));
                string Where(float d) => $"{name}, {d} m out on a bearing of {bearing}°";

                var sheet = kinds.First(k => k.Kind != SurfaceKind.PackIce).Distance;
                var lastIce = kinds.Last(k => k.Kind == SurfaceKind.PackIce).Distance + 5f;
                Assert.InRange(sheet, pack * 0.75f, pack * 1.25f);
                Assert.True(lastIce - sheet <= ragged, $"{name}'s sheet comes back {lastIce - sheet} m past its edge on a bearing of {bearing}°");
                var end = kinds.Last(k => k.Kind == SurfaceKind.FloeField).Distance + 5f;
                foreach (var (d, kind) in kinds.Where(k => k.Distance >= sheet))
                {
                    if (d < lastIce) Assert.True(kind is SurfaceKind.PackIce or SurfaceKind.FloeField, $"{kind} at the sheet's edge at {Where(d)}");
                    else if (d < end - ragged) Assert.True(kind == SurfaceKind.FloeField, $"{kind} among the floes at {Where(d)}");
                    else if (kind != SurfaceKind.FloeField) Assert.True(kind == SurfaceKind.Seabed, $"{kind} at the floes' edge or past it at {Where(d)}");
                }

                var band = end - sheet;
                Assert.InRange(band, floes - 40f, floes + 40f);
                Assert.True(kinds[^1].Distance - end >= 100f, $"no open water past {name}'s floes on a bearing of {bearing}°");
                (shortest, longest) = (MathF.Min(shortest, sheet), MathF.Max(longest, sheet));
                (narrowest, widest) = (MathF.Min(narrowest, band), MathF.Max(widest, band));

                // The sheet is ground just above the sea (its detail on top), all the way to its edge,
                // wherever all four texels read there are the sheet: within a texel of any that isn't,
                // the ground drops away to the seabed.
                var checkedOnIce = 0;
                for (var d = 0f; d < sheet; d += 10f)
                {
                    var at = IceTests.Travel(centre, bearing, d);
                    var (face, u, v) = CubeSphere.ToFace(at);
                    Map.Texels(face, u, v, index, weight);
                    var allIce = true;
                    foreach (var t in index) allIce &= (SurfaceKind)Map.Kinds[t] == SurfaceKind.PackIce;
                    if (!allIce) continue;
                    checkedOnIce++;
                    Assert.False(Terrain.IsSea(at), $"{name}'s sheet is sea {d} m out on a bearing of {bearing}°");
                    Assert.InRange(Terrain.Height(at), Scene.PackIceFreeboard - 0.4f, Scene.PackIceFreeboard + 0.4f);
                }

                Assert.True(checkedOnIce * 10f > sheet * 0.9f, $"only {checkedOnIce} points of {name}'s sheet checked on a bearing of {bearing}°");
            }

            output.WriteLine($"{name}: the sheet reaches {shortest:0} to {longest:0} m (pack {pack:0}); the floes are {narrowest:0} to {widest:0} m wide (floes {floes:0})");
            Assert.True(longest - shortest > 0.2f * pack, $"{name}'s sheet is round: it reaches {shortest} to {longest} m");
        }

        // Nowhere else: no ice texel belongs to a warm or temperate sea, and every one is near an icy sea's centre.
        var n = Map.Resolution;
        for (var face = 0; face < 6; face++)
        for (var j = 1; j <= n; j++)
        for (var i = 1; i <= n; i++)
        {
            var at = face * Side * Side + j * Side + i;
            if ((SurfaceKind)Map.Kinds[at] is not (SurfaceKind.PackIce or SurfaceKind.FloeField)) continue;
            var region = Map.Regions[at];
            Assert.True(region == 0 || Map.Seas[region - 1].Kind == SeaKind.Icy, $"ice in the {Map.Seas[Math.Max(region - 1, 0)].Name}");
            var d = TexelDirection(face, i, j);
            Assert.Contains(icy.Values, s => Map.Radius * MathF.Acos(Math.Clamp(Vector3.Dot(d, s.Centre), -1f, 1f)) <= s.Pack * 1.25f + s.Floes + 30f);
        }
    }

    [Fact]
    public void Deserts_lie_near_the_equator_away_from_water()
    {
        var maxLatitude = LayoutMaxDesertLatitude() * MathF.PI / 180f;
        var n = Map.Resolution;

        // Water texels beside land, bucketed by direction, so "no water within 1 km" is a few lookups.
        var cell = 1000f / Map.Radius;
        var coast = new Dictionary<(int, int, int), List<Vector3>>();
        (int, int, int) Key(Vector3 d) => ((int)MathF.Floor(d.X / cell), (int)MathF.Floor(d.Y / cell), (int)MathF.Floor(d.Z / cell));
        for (var face = 0; face < 6; face++)
        {
            for (var j = 1; j <= n; j++)
            {
                for (var i = 1; i <= n; i++)
                {
                    var at = face * Side * Side + j * Side + i;
                    if (Map.Regions[at] == 0) continue;
                    var shore = Map.Regions[at - 1] == 0 || Map.Regions[at + 1] == 0 ||
                                Map.Regions[at - Side] == 0 || Map.Regions[at + Side] == 0;
                    if (!shore) continue;
                    var d = TexelDirection(face, i, j);
                    if (!coast.TryGetValue(Key(d), out var list)) coast[Key(d)] = list = [];
                    list.Add(d);
                }
            }
        }

        var deserts = 0;
        for (var face = 0; face < 6; face++)
        {
            for (var j = 1; j <= n; j += 4)
            {
                for (var i = 1; i <= n; i += 4)
                {
                    var at = face * Side * Side + j * Side + i;
                    if ((SurfaceKind)Map.Kinds[at] != SurfaceKind.Desert) continue;
                    deserts++;
                    var d = TexelDirection(face, i, j);
                    Assert.True(MathF.Abs(CubeSphere.LatLon(d).Latitude) <= maxLatitude + 1e-4f, "desert too far from the equator");
                    var (kx, ky, kz) = Key(d);
                    for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (!coast.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                        foreach (var w in list)
                            Assert.True(Vector3.Distance(w, d) * Map.Radius > 1000f, "desert within 1 km of water");
                    }
                }
            }
        }

        Assert.True(deserts > 0, "no desert");
    }

    /// <summary>The desert rule's latitude limit, from the generator's layout in the repo.</summary>
    private static float LayoutMaxDesertLatitude()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "TerrainGen", "layout.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tools", "TerrainGen", "layout.json")), options);
        return json.RootElement.GetProperty("deserts").GetProperty("maxLatitude").GetSingle();
    }

    // How close to the edge each side is read, in face coordinates: ~1 cm, so the two reads differ
    // only by a seam, not by the slope between them (1e-4 is ~0.8 m, which on a steep slope alone
    // makes half a metre).
    private const float Approach = 1e-6f;

    [Fact]
    public void The_map_is_continuous_across_every_cube_edge()
    {
        var edges = new HashSet<(int, int)>();
        // Each face's four edges, as (u, v) along the edge for t in [-1, 1], and the way out.
        (Func<float, float, (float, float)> At, int)[] sides =
        [
            ((t, s) => (1f + s, t), 0), ((t, s) => (-1f - s, t), 1),
            ((t, s) => (t, 1f + s), 2), ((t, s) => (t, -1f - s), 3),
        ];
        for (var face = 0; face < 6; face++)
        {
            foreach (var (at, _) in sides)
            {
                var other = CubeSphere.ToFace(CubeSphere.FromFace(face, at(0f, 0.01f).Item1, at(0f, 0.01f).Item2)).Face;
                edges.Add((Math.Min(face, other), Math.Max(face, other)));
                for (var k = 0; k < 512; k++)
                {
                    var t = ((k + 0.5f) / 512f) * 2f - 1f;
                    var (ui, vi) = at(t, -Approach);
                    var inside = Map.HeightAt(face, ui, vi);
                    var (uo, vo) = at(t, Approach);
                    var (f2, u2, v2) = CubeSphere.ToFace(CubeSphere.FromFace(face, uo, vo));
                    Assert.NotEqual(face, f2);
                    var outside = Map.HeightAt(f2, u2, v2);
                    Assert.True(MathF.Abs(inside - outside) < 0.5f,
                        $"face {face} to {f2} at t={t}: {inside} m against {outside} m");
                }
            }
        }

        Assert.Equal(12, edges.Count);
    }
}
