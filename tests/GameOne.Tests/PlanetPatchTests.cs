using System.Numerics;
using GameOne.Rendering;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The patches the planet is drawn in (<see cref="PlanetPatches.Select"/>): finest under the
/// camera, few from far out, covering every face once, nothing beyond the horizon, and water only
/// where there is sea.
/// </summary>
public sealed class PlanetPatchTests
{
    private static readonly Vector3 AbovePad = new(0f, 30f, 0f);

    private static List<PlanetPatch> Select(Vector3 camera, bool cull = true, float lodRatio = Scene.TerrainLodRatio)
    {
        var patches = new List<PlanetPatch>();
        PlanetPatches.Select(camera, lodRatio, patches, cull);
        return patches;
    }

    /// <summary>The patch whose square holds <paramref name="direction"/> (edges included, so the first of those that share an edge).</summary>
    private static PlanetPatch Holding(List<PlanetPatch> patches, Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        return patches.First(p => p.Face == face && u >= p.U0 && u <= p.U0 + p.Size && v >= p.V0 && v <= p.V0 + p.Size);
    }

    /// <summary>The direction <paramref name="metres"/> round the planet from the pad, on a bearing (degrees clockwise from north).</summary>
    private static Vector3 FromPad(float bearingDegrees, float metres)
    {
        var b = float.DegreesToRadians(bearingDegrees);
        var angle = metres / Planet.Radius;
        var heading = new Vector3(MathF.Sin(b), 0f, -MathF.Cos(b)); // east is +x, north is -z
        return Vector3.Normalize(Vector3.UnitY * MathF.Cos(angle) + heading * MathF.Sin(angle));
    }

    [Fact]
    public void On_the_ground_the_patch_under_the_camera_is_finest()
    {
        var patches = Select(AbovePad);
        Assert.Equal(Scene.TerrainMaxLevel, Holding(patches, Vector3.UnitY).Level);
        Assert.True(patches.Count <= Scene.TerrainPatchBudget, $"{patches.Count} patches");
    }

    [Fact]
    public void Choosing_them_again_allocates_nothing()
    {
        // Every view does it every frame, into a list it keeps.
        var patches = Select(AbovePad);
        var before = GC.GetAllocatedBytesForCurrentThread();
        PlanetPatches.Select(new Vector3(5f, 40f, -3f), Scene.TerrainLodRatio, patches);
        PlanetPatches.Select(AbovePad, Scene.SmallViewTerrainLodRatio, patches);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Far_out_the_patches_are_few()
    {
        var patches = Select(Planet.Centre + Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)) * 150_000f);
        Assert.True(patches.Count <= 60, $"{patches.Count} patches");
        Assert.All(patches, p => Assert.True(p.Level <= 3, $"a patch at level {p.Level}"));
    }

    [Fact]
    public void Without_culling_the_patches_cover_every_face_exactly_once()
    {
        var rng = new Random(11);
        for (var n = 0; n < 20; n++)
        {
            Vector3 direction;
            do direction = new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1);
            while (direction.LengthSquared() is < 0.01f or > 1f);
            var camera = Planet.Centre + Vector3.Normalize(direction) * (Planet.Radius + 20f + rng.NextSingle() * rng.NextSingle() * 50_000f);
            var patches = Select(camera, cull: false);

            for (var face = 0; face < 6; face++)
            {
                var own = patches.Where(p => p.Face == face).ToArray();
                Assert.Equal(4.0, own.Sum(p => (double)p.Size * p.Size), 6);
                for (var i = 0; i < own.Length; i++)
                for (var j = i + 1; j < own.Length; j++)
                {
                    var (a, b) = (own[i], own[j]);
                    var overlap = a.U0 < b.U0 + b.Size && b.U0 < a.U0 + a.Size && a.V0 < b.V0 + b.Size && b.V0 < a.V0 + a.Size;
                    Assert.False(overlap, $"patches {a} and {b} overlap (camera {camera})");
                }
            }
        }
    }

    /// <summary>A view's projection (the game's field of view, at 16:9), from <paramref name="camera"/> towards <paramref name="target"/>.</summary>
    private static Matrix4x4 ViewFrom(Vector3 camera, Vector3 target) =>
        Matrix4x4.CreateLookAt(camera, target, Planet.Up(camera))
        * Matrix4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(Scene.CameraVerticalFovDegrees), 16f / 9f, 0.5f, 40_000f);

    [Fact]
    public void The_view_leaves_out_only_what_is_off_the_screen()
    {
        // Looking north from above the pad, a little down.
        var view = ViewFrom(AbovePad, new Vector3(0f, 0f, -200f));
        var all = Select(AbovePad);
        var seen = new List<PlanetPatch>();
        PlanetPatches.Select(AbovePad, Scene.TerrainLodRatio, seen, viewProjection: view);
        Assert.True(seen.Count < all.Count * 0.6f, $"{seen.Count} patches of {all.Count} kept");
        Assert.DoesNotContain(seen, p => p.Face == 2 && p.V0 + p.Size <= -0.1f); // well behind (south of: -v) the camera

        // Wherever the ground is on the screen, a patch is still drawn there.
        var rng = new Random(5);
        for (var n = 0; n < 2000; n++)
        {
            var direction = FromPad(rng.NextSingle() * 360f, rng.NextSingle() * 3000f);
            var ground = Planet.Centre + direction * (Planet.SeaRadius + Terrain.Height(direction));
            var clip = Vector4.Transform(new Vector4(ground, 1f), view);
            if (clip.W <= 0f || MathF.Abs(clip.X) > clip.W || MathF.Abs(clip.Y) > clip.W)
                continue;
            if (!all.Any(p => Holds(p, direction)))
                continue; // below the horizon
            Assert.Contains(seen, p => Holds(p, direction));
        }
    }

    [Fact]
    public void Anywhere_a_view_needs_no_more_than_the_budget()
    {
        // Cameras all over the planet (a third of them at a cube face's edge, where patches are
        // smallest), from just off the ground to 3 km up, looking every way from level to steeply down.
        var rng = new Random(3);
        var patches = new List<PlanetPatch>();
        for (var n = 0; n < 1500; n++)
        {
            Vector3 d;
            do d = new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1);
            while (d.LengthSquared() is < 0.01f or > 1f);
            d = Vector3.Normalize(d);
            if (n % 3 == 0)
            {
                var (face, u, v) = CubeSphere.ToFace(d);
                d = CubeSphere.FromFace(face, u > 0 ? 0.999f : -0.999f, v);
            }
            var camera = Planet.Centre + d * (Planet.SeaRadius + Terrain.SurfaceHeight(d) + 2f + MathF.Pow(rng.NextSingle(), 3) * 3000f);
            var east = Vector3.Normalize(Vector3.Cross(new Vector3(0.01f, 1f, 0.02f), d));
            var north = Vector3.Cross(d, east);
            var (heading, pitch) = (rng.NextSingle() * MathF.Tau, -1.2f + rng.NextSingle() * 1.3f);
            var look = (east * MathF.Cos(heading) + north * MathF.Sin(heading)) * MathF.Cos(pitch) + d * MathF.Sin(pitch);
            var view = ViewFrom(camera, camera + look * 100f);
            foreach (var ratio in (ReadOnlySpan<float>)[Scene.TerrainLodRatio, Scene.SmallViewTerrainLodRatio])
            {
                PlanetPatches.Select(camera, ratio, patches, viewProjection: view);
                Assert.True(patches.Count <= Scene.TerrainPatchBudget, $"{patches.Count} patches from {camera}");
            }
        }
    }

    private static bool Holds(PlanetPatch p, Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        return p.Face == face && u >= p.U0 && u <= p.U0 + p.Size && v >= p.V0 && v <= p.V0 + p.Size;
    }

    [Fact]
    public void The_horizon_hides_the_far_side() =>
        Assert.DoesNotContain(Select(AbovePad), p => p.Face == 3);

    [Fact]
    public void Water_is_drawn_only_where_there_is_sea()
    {
        // From 300 m up, where the sea 1.5 km east is over the horizon (on this small planet, from
        // 30 m it is only some 800 m away).
        var patches = Select(new Vector3(0f, 300f, 0f));
        Assert.True(Holding(patches, FromPad(90f, 1500f)).Water, "no water over the sea 1.5 km east");

        // The highest ground inland (west), 3 to 8 km out: well above the sea.
        var peak = Vector3.UnitY;
        for (var bearing = 225f; bearing <= 315f; bearing += 5f)
        for (var d = 3000f; d <= 8000f; d += 100f)
            if (Terrain.Height(FromPad(bearing, d)) > Terrain.Height(peak))
                peak = FromPad(bearing, d);
        Assert.True(Terrain.Height(peak) > 200f, $"the range is only {Terrain.Height(peak)} m high");
        var over = Holding(patches, peak);
        Assert.True(Terrain.Map.Range(over.Face, over.U0, over.V0, over.Size).Min > 10f,
            $"the patch over the peak ({over}) reaches the sea");
        Assert.False(over.Water, $"water drawn over the range ({over})");
    }

    [Fact]
    public void Water_patches_contain_the_highest_waves()
    {
        // Culling and the frustum's spheres take a patch's heights from here, so a crest above
        // them could be culled while on the screen. From the pad, out over the sea, and from orbit.
        var crest = Terrain.Map.Seas.Max(s => s.Waves) / 2f;
        var trough = -Terrain.Map.Seas.Max(s => s.Waves) / 2f;
        var seen = 0;
        foreach (var camera in (ReadOnlySpan<Vector3>)[AbovePad, Planet.Centre + FromPad(90f, 3000f) * (Planet.Radius + 5f),
                     Planet.Centre + Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)) * 30_000f])
        foreach (var patch in Select(camera, cull: false).Where(p => p.Water))
        {
            var (low, high, water) = PlanetPatches.Heights(Terrain.Map, patch.Face, patch.U0, patch.V0, patch.Size);
            Assert.True(water);
            Assert.True(high >= crest, $"{patch} reaches only {high} m, under the highest crest at {crest} m");
            Assert.True(low <= trough, $"{patch} reaches down only to {low} m, over the lowest trough at {trough} m");
            seen++;
        }
        Assert.True(seen > 0, "no water patches");
    }
}
