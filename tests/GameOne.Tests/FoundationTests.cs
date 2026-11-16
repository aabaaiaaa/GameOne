using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The far pads' and far zones' foundations (<see cref="Footing"/>): a straight-sided slab
/// <see cref="Scene.FoundationSlab"/> thick, then narrowing inward at 45° down into the ground; and
/// the shapes that don't change (Home's pad and zone, the test pads).
/// </summary>
public sealed class FoundationTests(ITestOutputHelper output)
{
    private static int SiteIndex(string name) => Array.IndexOf(LaunchSites.All, LaunchSites.Named(name));

    private static Plinth ZoneOf(string name)
    {
        var zone = LaunchSites.Named(name).LandingZone!.Value;
        return Foundations.LandingZonePlinth.Single(p => Vector3.Distance(p.Centre, zone) < 0.01f);
    }

    /// <summary>
    /// Every far pad's and far zone's footing, named, each with where a point in its own frame is in
    /// the world, its way out of the top, and how far across it a point (x, z) is from its middle
    /// (as its outline measures).
    /// </summary>
    private static IEnumerable<(string Name, Footing Footing, Func<Vector3, Vector3> ToWorld, Vector3 Normal, Func<float, float, float> Across)> Far()
    {
        foreach (var site in LaunchSites.All.Skip(1))
        {
            var pad = Foundations.PadFootings[SiteIndex(site.Name)];
            Assert.True(pad is not null, $"{site.Name}'s pad has no footing");
            yield return ($"{site.Name}'s pad", pad, site.ToWorld, site.Up, (x, z) => MathF.Max(MathF.Abs(x), MathF.Abs(z)));
        }
        foreach (var site in LaunchSites.All.Skip(1).Where(s => s.HasLandingZone))
        {
            var plinth = ZoneOf(site.Name);
            Assert.True(plinth.Footing is not null, $"{site.Name}'s zone has no footing");
            yield return ($"{site.Name}'s zone", plinth.Footing, p => plinth.Centre + Vector3.Transform(p, plinth.Tilt), plinth.Normal, (x, z) => MathF.Sqrt(x * x + z * z));
        }
    }

    /// <summary>The mountain sites' footings (Polar's and Highcrest's), standing high over their slopes.</summary>
    private static IEnumerable<(string Name, Footing Footing, Func<Vector3, Vector3> ToWorld, Vector3 Normal, Func<float, float, float> Across)> Mountains() =>
        Far().Where(f => f.Name.StartsWith("Polar's") || f.Name.StartsWith("Highcrest's"));

    [Fact]
    public void Home_and_the_test_pads_keep_their_shapes_and_the_far_ones_stand_on_footings()
    {
        // Home's pad one block, down to its depth; Home's zone a drum; the test range's tilted pads
        // on their plinths' own columns.
        Assert.Null(Foundations.PadFootings[0]);
        Assert.Equal(Foundations.LaunchPad, Foundations.LaunchPads[0]);
        Assert.Null(Foundations.LandingZonePlinth[0].Footing);
        for (var i = 0; i < Scene.TestPads.Length; i++)
            Assert.Null(Foundations.LandingZonePlinth[1 + i].Footing);
        foreach (var (_, footing, _, _, _) in Far())
            Assert.Equal(Scene.FoundationSlab, footing.Slab);
        foreach (var s in Enumerable.Range(1, LaunchSites.All.Length - 1))
        {
            var footing = Foundations.PadFootings[s]!;
            Assert.Equal((FootingOutline.Square, Scene.PadHalfSize), (footing.Outline, footing.Half));
            Assert.Equal(footing.Depth, Foundations.LaunchPads[s]);
        }
        foreach (var site in LaunchSites.All.Skip(1).Where(s => s.HasLandingZone))
        {
            var plinth = ZoneOf(site.Name);
            Assert.Equal((FootingOutline.Circle, Scene.LandingZoneRadius), (plinth.Footing!.Outline, plinth.Footing.Half));
            Assert.Equal(plinth.Footing.Depth, plinth.Depth);
        }
    }

    [Fact]
    public void A_mountain_foundation_is_a_slab_then_a_45_degree_taper()
    {
        // Under the top, concrete the whole width for the slab's 3 m; below it, at d under the top,
        // only within Half - (d - 3) of the middle (each way across it, and on the diagonals): a
        // point further out is under the slab, in the air or the hill, so what is under it is the ground.
        foreach (var (name, footing, toWorld, _, across) in Mountains())
        {
            output.WriteLine($"{name}: {footing.Depth:0.00} m deep, {footing.Boxes.Count} boxes");
            Assert.Equal(3f, footing.Slab);
            Assert.True(footing.Depth > footing.Slab + 1f, $"{name}'s foundation hardly tapers ({footing.Depth:0.00} m deep): it isn't on a slope");
            for (var d = 0.5f; d < footing.Depth - 0.2f; d += 0.5f)
            {
                var half = footing.Half - MathF.Max(0f, d - footing.Slab);
                Assert.Equal(half, footing.HalfWidthAt(d), 4);
                for (var k = 0; k < 8; k++)
                {
                    var (s, c) = MathF.SinCos(MathF.Tau * k / 8);
                    // At the given distance across, as the outline measures it, along this way.
                    Vector3 At(float distance)
                    {
                        var scale = distance / across(c, s);
                        return toWorld(new Vector3(c * scale, Scene.PadTop - d, s * scale));
                    }
                    var inside = At(half - 0.3f);
                    Assert.True(MathF.Abs(Planet.GroundAltitude(inside) + d) < 0.01f,
                        $"{d} m under {name}'s top, 0.3 m in from its taper, reads {Planet.GroundAltitude(inside):0.00} m (in the concrete: {-d})");
                    if (d > footing.Slab + 0.3f)
                    {
                        var outside = At(half + 0.3f);
                        Assert.True(MathF.Abs(Planet.GroundAltitude(outside) - Planet.TerrainAltitude(outside)) < 1e-3f,
                            $"{d} m under {name}'s top, 0.3 m out from its taper, reads {Planet.GroundAltitude(outside):0.00} m, not the ground's {Planet.TerrainAltitude(outside):0.00}");
                    }
                }
            }
            // UnderAt says the same: the slab under its edge, deeper in to the taper's bottom; nothing outside it.
            Assert.Equal(footing.Slab, footing.UnderAt(footing.Half, 0f)!.Value, 4);
            Assert.Equal(footing.Depth, footing.UnderAt(0f, 0f)!.Value, 4);
            Assert.Null(footing.UnderAt(footing.Half + 0.1f, 0f));
        }
    }

    [Fact]
    public void A_far_foundation_ends_its_margin_into_the_ground_and_no_deeper_than_it_needs()
    {
        // Every far one's bottom (all of the taper at its depth) is the margin under the ground
        // there (less a little, for the ground between the points it was measured at), so no sky
        // shows under its downhill side; a mountain one's, a little higher, would be partly in the
        // air, so it can't stop shorter.
        static List<Vector2> Points(Footing footing, Func<float, float, float> across)
        {
            var points = new List<Vector2>();
            for (var x = -footing.Half; x <= footing.Half; x += 0.5f)
            for (var z = -footing.Half; z <= footing.Half; z += 0.5f)
                if (across(x, z) <= footing.Half)
                    points.Add(new Vector2(x, z));
            return points;
        }
        foreach (var (name, footing, toWorld, _, across) in Far())
        {
            var bottom = footing.HalfWidthAt(footing.Depth);
            var shallowest = float.PositiveInfinity;
            foreach (var at in Points(footing, across).Where(p => across(p.X, p.Y) <= bottom))
                shallowest = MathF.Min(shallowest, -Planet.TerrainAltitude(toWorld(new Vector3(at.X, Scene.PadTop - footing.Depth, at.Y))));
            output.WriteLine($"{name}: {footing.Depth:0.00} m deep, its bottom at least {shallowest:0.00} m into the ground");
            Assert.True(shallowest >= Scene.FoundationMargin - 0.1f, $"{name}'s bottom is only {shallowest:0.00} m into the ground");
        }
        foreach (var (name, footing, toWorld, _, across) in Mountains())
        {
            var points = Points(footing, across);
            var higher = footing.Depth - Scene.FoundationMargin - 0.5f;
            Assert.True(higher > footing.Slab, $"{name}'s taper is too short to show this");
            var width = footing.HalfWidthAt(higher);
            Assert.Contains(points.Where(p => across(p.X, p.Y) <= width),
                at => Planet.TerrainAltitude(toWorld(new Vector3(at.X, Scene.PadTop - higher, at.Y))) > 0.3f);
        }
    }

    [Fact]
    public void The_top_of_a_mountain_foundation_reads_as_before()
    {
        // Half a metre over it anywhere, half a metre up, along the top's normal; 1 m into the
        // slab, 1 m down.
        foreach (var (name, footing, toWorld, normal, across) in Mountains())
            for (var x = -footing.Half + 0.5f; x < footing.Half; x += 2.5f)
            for (var z = -footing.Half + 0.5f; z < footing.Half; z += 2.5f)
            {
                if (across(x, z) > footing.Half - 0.3f)
                    continue;
                var (height, way) = Planet.Ground(toWorld(new Vector3(x, Scene.PadTop + 0.5f, z)));
                Assert.Equal(0.5f, height, 2);
                Assert.True(Vector3.Distance(way, normal) < 1e-4f, $"over {name}'s top at ({x}, {z}) the normal is {way}");
                Assert.Equal(-1f, Planet.GroundAltitude(toWorld(new Vector3(x, Scene.PadTop - 1f, z))), 2);
            }
    }

    /// <summary>The mesh a far pad or zone is drawn with, in its footing's frame (its pad's site's, or its zone's).</summary>
    private static GameOne.Rendering.MeshVertex[] Drawn(string name, Footing footing)
    {
        var site = name[..name.IndexOf("'s ", StringComparison.Ordinal)];
        return name.EndsWith("'s pad", StringComparison.Ordinal)
            ? GameOne.Rendering.Meshes.BuildPad(footing, Foundations.LaunchPads[SiteIndex(site)])
            : GameOne.Rendering.Meshes.BuildLandingZone(ZoneOf(site));
    }

    [Fact]
    public void A_far_foundation_is_drawn_as_its_footing_is_and_its_collider_fits_the_drawing()
    {
        // The drawn slab's sides run straight down from the top's edge, 3 m. Under it the drawn
        // underside (at each point, the lowest face over it) and every vertex of it are within a
        // step of the footing's (UnderAt, which Planet.Under reads); every drawn point under the
        // slab is within a step of a collider box, and every box's outer edge within a step of the
        // drawn shape: what is seen is what is hit and judged. And the concrete has no seam: no
        // face's UVs reach further apart than its corners do by as much as a tile (4 m).
        foreach (var (name, footing, _, _, across) in Far())
        {
            var vertices = Drawn(name, footing);
            var step = Scene.FoundationStep;
            static Vector3 P(GameOne.Rendering.MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);
            float DepthAt(Vector3 p) => Scene.PadTop - p.Y;
            // UnderAt, on the outline too (where rounding can put a point a hair outside it).
            float? Under(float x, float z) => footing.UnderAt(x, z) ?? (across(x, z) <= footing.Half + 1e-3f ? footing.Slab : null);
            float FromBoxes(float x, float d, float z) => footing.Boxes.Min(b =>
            {
                var outward = footing.Outline == FootingOutline.Square
                    ? new Vector2(MathF.Max(0f, MathF.Abs(x) - b.Half), MathF.Max(0f, MathF.Abs(z) - b.Half)).Length()
                    : MathF.Max(0f, MathF.Sqrt(x * x + z * z) - b.Half);
                var vertical = MathF.Max(0f, MathF.Max(b.Top - d, d - b.Depth));
                return MathF.Sqrt(outward * outward + vertical * vertical);
            });
            // The drawn underside under (x, z): how far below the top the lowest face over it is.
            float? DrawnUnder(float x, float z)
            {
                float? lowest = null;
                for (var i = 0; i < vertices.Length; i += 3)
                {
                    Vector3 a = P(vertices[i]), b = P(vertices[i + 1]), c = P(vertices[i + 2]);
                    if (DepthAt(a) < 1e-3f && DepthAt(b) < 1e-3f && DepthAt(c) < 1e-3f)
                        continue; // the top
                    var area = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
                    if (MathF.Abs(area) < 1e-6f)
                        continue; // upright, or a sliver
                    var u = ((b.X - x) * (c.Z - z) - (c.X - x) * (b.Z - z)) / area;
                    var v = ((c.X - x) * (a.Z - z) - (a.X - x) * (c.Z - z)) / area;
                    var w = 1f - u - v;
                    if (u < -1e-4f || v < -1e-4f || w < -1e-4f)
                        continue;
                    var depth = DepthAt(a * u + b * v + c * w);
                    lowest = MathF.Max(lowest ?? depth, depth);
                }
                return lowest;
            }

            // The slab's sides.
            var sides = vertices.Where(v => MathF.Abs(v.Normal.Y) < 0.01f).Select(P).ToList();
            Assert.NotEmpty(sides);
            foreach (var p in sides)
                Assert.True(MathF.Abs(across(p.X, p.Z) - footing.Half) < 1e-3f && DepthAt(p) > -1e-3f && DepthAt(p) < footing.Slab + 1e-3f,
                    $"{name} is drawn with a straight side {across(p.X, p.Z):0.00} m across, {DepthAt(p):0.00} m down");
            Assert.Equal(0f, sides.Min(DepthAt), 3);
            Assert.Equal(footing.Slab, sides.Max(DepthAt), 3);
            Assert.Equal(footing.Depth, vertices.Max(v => DepthAt(P(v))), 3);

            // The taper's vertices.
            foreach (var p in vertices.Select(P).Where(p => DepthAt(p) > footing.Slab + 1e-3f))
            {
                var under = Under(p.X, p.Z);
                Assert.True(under is { } u && MathF.Abs(DepthAt(p) - u) <= step,
                    $"{name} is drawn {DepthAt(p):0.00} m down at ({p.X:0.00}, {p.Z:0.00}), where its footing's underside is {under:0.00} m");
                Assert.True(FromBoxes(p.X, DepthAt(p), p.Z) <= step, $"{name} is drawn {FromBoxes(p.X, DepthAt(p), p.Z):0.00} m from its collider at ({p.X:0.00}, {DepthAt(p):0.00} m down, {p.Z:0.00})");
            }

            // The drawn underside, across the top.
            for (var x = -footing.Half + 0.25f; x < footing.Half; x += 1f)
            for (var z = -footing.Half + 0.25f; z < footing.Half; z += 1f)
            {
                if (across(x, z) > footing.Half - 0.05f)
                    continue;
                var drawn = DrawnUnder(x, z);
                Assert.True(drawn is not null, $"{name} has nothing drawn under its top at ({x}, {z})");
                var d = drawn.Value;
                Assert.True(MathF.Abs(d - Under(x, z)!.Value) <= step, $"{name}'s drawn underside is {d:0.00} m down at ({x}, {z}), its footing's {Under(x, z):0.00} m");
                Assert.True(FromBoxes(x, d, z) <= step, $"{name}'s drawn underside at ({x}, {z}) is {FromBoxes(x, d, z):0.00} m from its collider");
            }

            // Each step's outer edge, at its bottom (where it stands furthest out of the taper).
            foreach (var box in footing.Boxes.Skip(1))
            {
                var edge = footing.Outline == FootingOutline.Square
                    ? Enumerable.Range(0, 9).Select(k => new Vector2(k % 3 == 0 ? 1f : k % 3 == 1 ? 0f : -1f, k / 3 - 1f)).Where(e => e != Vector2.Zero).Select(e => e * box.Half)
                    : Enumerable.Range(0, 32).Select(k => MathF.SinCos(MathF.Tau * k / 32)).Select(sc => new Vector2(sc.Cos, sc.Sin) * box.Half);
                foreach (var e in edge)
                {
                    var drawn = DrawnUnder(e.X, e.Y);
                    Assert.True(drawn is { } d && box.Depth - d <= step,
                        $"{name}'s collider box at ({e.X:0.00}, {e.Y:0.00}) reaches {box.Depth:0.00} m down, its drawing {drawn:0.00} m");
                }
            }

            AssertNoSeam(name, vertices);
        }
    }

    /// <summary>
    /// No seam in a footing's drawn concrete (its 4 m slabs, laid by its UVs): no face's UVs reach
    /// further apart than its corners do by as much as a slab, and where faces meet (the slab's side
    /// and the taper, a square's corners, where U wraps round) a point has the same V on each, and
    /// the same U or a whole number of slabs apart, so the slabs' joints carry on across. (Not at the
    /// middle, where a taper can come to a point that every face meets at.)
    /// </summary>
    private static void AssertNoSeam(string name, GameOne.Rendering.MeshVertex[] vertices)
    {
        static Vector3 P(GameOne.Rendering.MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);
        for (var i = 0; i < vertices.Length; i += 3)
            for (var j = 0; j < 3; j++)
            {
                var (a, b) = (vertices[i + j], vertices[i + (j + 1) % 3]);
                var uv = new Vector2(a.Uv.X - b.Uv.X, a.Uv.Y - b.Uv.Y).Length();
                Assert.True(uv - Vector3.Distance(P(a), P(b)) < 4f, $"{name}'s concrete jumps {uv:0.0} m across a face {Vector3.Distance(P(a), P(b)):0.0} m across");
            }
        var sides = vertices.Where(v => MathF.Abs(v.Normal.Y) <= 0.99f && v.Position.X * v.Position.X + v.Position.Z * v.Position.Z > 1e-4f);
        foreach (var point in sides.GroupBy(v => (MathF.Round(v.Position.X * 1000f), MathF.Round(v.Position.Y * 1000f), MathF.Round(v.Position.Z * 1000f))))
        {
            var first = point.First();
            foreach (var v in point)
            {
                var du = v.Uv.X - first.Uv.X;
                var off = du - 4f * MathF.Round(du / 4f);
                Assert.True(MathF.Abs(off) < 1e-3f && MathF.Abs(v.Uv.Y - first.Uv.Y) < 1e-3f,
                    $"{name}'s concrete is cut at ({first.Position.X:0.00}, {first.Position.Y:0.00}, {first.Position.Z:0.00}): its UVs there are ({first.Uv.X:0.00}, {first.Uv.Y:0.00}) and ({v.Uv.X:0.00}, {v.Uv.Y:0.00})");
            }
        }
    }

    [Fact]
    public void A_footing_of_any_size_is_drawn_without_a_seam()
    {
        // The pads' and zones' own sizes happen to be whole numbers of slabs round (16 m and 18 m
        // across from their middles): footings of other sizes, on Polar's slope, are seamless too.
        var site = LaunchSites.Named("Polar");
        foreach (var half in new[] { 13.3f, 9.1f, 21.7f })
        {
            var square = Footing.Measure(FootingOutline.Square, half, site.Pad, (x, z) => site.ToWorld(new Vector3(x, 0f, z)));
            Assert.True(square.Depth > square.Slab + 1f, $"a {half} m square on Polar's slope hardly tapers");
            AssertNoSeam($"a {half} m square", GameOne.Rendering.Meshes.BuildPad(square, square.Depth));
            var round = Footing.Measure(FootingOutline.Circle, half, site.Pad, (x, z) => site.ToWorld(new Vector3(x, 0f, z)));
            AssertNoSeam($"a {half} m disc", GameOne.Rendering.Meshes.BuildLandingZone(new Plinth(site.Pad, site.Frame, round)));
        }
    }

    [Fact]
    public void Home_and_the_test_pads_stand_as_deep_as_before_the_footings()
    {
        // As measured at 0853f8c, before the far ones' footings: Home's pad, then Home's zone and
        // the six test pads, in order (to within float rounding).
        static void Same(float before, float now, string what) =>
            Assert.True(MathF.Abs(now - before) < 1e-5f, $"{what} is {now:R} m deep, not {before:R} m as before");
        Same(1.5253906f, Foundations.LaunchPad, "Home's pad");
        float[] zones = [1.516673f, 3.252942f, 4.797325f, 6.27781f, 7.776843f, 9.243692f, 14.421235f];
        Assert.Equal(1 + Scene.TestPads.Length, zones.Length);
        for (var i = 0; i < zones.Length; i++)
            Same(zones[i], Foundations.LandingZones[i], i == 0 ? "Home's zone" : $"test pad {i}");
    }

    [Fact]
    public void Every_collider_box_follows_the_taper()
    {
        // The slab first, the whole top across and Slab deep; then steps, each FoundationStep tall
        // (the last one's remainder down to the taper's bottom, or, if that would be a sliver
        // shorter than Scene.FoundationShortestStep, folded into the one above it), stacked down to the
        // taper's bottom, each within FoundationStep of it and never past the slab's edges.
        foreach (var (name, footing, _, _, _) in Far())
        {
            var boxes = footing.Boxes;
            Assert.Equal(new Column(0f, footing.Slab, footing.Half), boxes[0]);
            Assert.Equal(footing.Depth, boxes[^1].Depth, 4);
            for (var i = 1; i < boxes.Count; i++)
            {
                var box = boxes[i];
                Assert.Equal(boxes[i - 1].Depth, box.Top, 4);
                Assert.True(box.Depth - box.Top <= Scene.FoundationStep + Scene.FoundationShortestStep + 1e-4f, $"a step of {name}'s is {box.Depth - box.Top:0.00} m tall");
                Assert.True(i == 1 || box.Depth - box.Top >= Scene.FoundationShortestStep, $"a step of {name}'s is a sliver {box.Depth - box.Top:0.000} m tall");
                Assert.True(box.Half <= footing.HalfWidthAt(box.Top) + 1e-4f, $"a step of {name}'s stands out past the taper at its top");
                Assert.True(box.Half - footing.HalfWidthAt(box.Depth) <= Scene.FoundationStep, $"a step of {name}'s stands {box.Half - footing.HalfWidthAt(box.Depth):0.00} m out past the taper at its bottom");
                Assert.True(footing.HalfWidthAt(box.Top) - box.Half <= Scene.FoundationStep, $"a step of {name}'s is {footing.HalfWidthAt(box.Top) - box.Half:0.00} m in from the taper at its top");
                Assert.True(box.Half < footing.Half, $"a step of {name}'s reaches past its slab");
            }
        }
    }

    [Fact]
    public void A_booster_dropped_beside_a_mountain_pads_edge_falls_past_its_foundation_to_the_ground()
    {
        // Lying level along Polar's pad's edge on its downhill side, just clear of it, a booster let
        // go falls past the slab and the taper tucked under it to the ground, not caught on anything.
        var s = SiteIndex("Polar");
        var site = LaunchSites.All[s];
        var half = Scene.PadHalfSize;
        var (outward, along) = new[] { (Vector3.UnitX, Vector3.UnitZ), (-Vector3.UnitX, Vector3.UnitZ), (Vector3.UnitZ, Vector3.UnitX), (-Vector3.UnitZ, Vector3.UnitX) }
            .MaxBy(w => Planet.TerrainAltitude(site.ToWorld(w.Item1 * (half + 1f) + Vector3.UnitY * Scene.PadTop)));
        var drop = Planet.TerrainAltitude(site.ToWorld(outward * (half + 1f) + Vector3.UnitY * Scene.PadTop));
        output.WriteLine($"Polar's pad's top is {drop:0.0} m over the ground 1 m out from its downhill edge");
        Assert.True(drop > Scene.FoundationSlab + 4f, "Polar's pad stands too low over its ground to show anything");

        using var physics = new GameOne.Physics.RocketPhysics(GroundBody.Planet);
        var radius = Vehicle.Booster.Stages[0].Radius;
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var (worldAlong, worldOut) = (Vector3.Transform(along, site.Frame), Vector3.Transform(outward, site.Frame));
        var lying = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(site.Up, worldAlong)), MathF.PI / 2) * site.Frame);
        Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitY, lying), worldAlong) > 0.999f);
        var middle = site.ToWorld(outward * (half + radius + 0.3f) + Vector3.UnitY * (Scene.PadTop - 1f));
        var craft = physics.Restore(Vehicle.Booster, 0, 0, middle - worldAlong * (length / 2f), lying, Vector3.Zero, Vector3.Zero, legDeploy: 0f, fairingOn: false);
        var nearest = float.PositiveInfinity;
        for (var i = 0; i < 6 * 60; i++)
        {
            physics.Update(1f / 60, []);
            var axis = site.ToLocal(physics.Position(craft) + Vector3.Transform(Vector3.UnitY, physics.Rotation(craft)) * (length / 2f));
            nearest = MathF.Min(nearest, Vector3.Dot(axis, outward));
        }
        var end = physics.Position(craft) + Vector3.Transform(Vector3.UnitY, physics.Rotation(craft)) * (length / 2f);
        output.WriteLine($"its middle came to rest {Planet.TerrainAltitude(end):0.00} m over the ground, {site.ToLocal(end).Y - Scene.PadTop:0.00} m from the pad's top; nearest the pad {nearest - half:0.00} m out from its edge");
        Assert.True(Planet.TerrainAltitude(end) < radius + 1f, $"its middle stopped {Planet.TerrainAltitude(end):0.00} m over the ground");
        Assert.True(nearest >= half + radius - 0.25f, $"it came {nearest - half:0.00} m from the edge's line (its radius {radius:0.00} m)");
    }
}
