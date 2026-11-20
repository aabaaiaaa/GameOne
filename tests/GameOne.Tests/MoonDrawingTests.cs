using System.Numerics;
using GameOne.Physics;
using GameOne.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The moon drawn up close as a mesh (<see cref="PlanetPatches"/> for a <see cref="PatchBody"/>):
/// the planet's own patches as they were, the moon's finest under a camera near it and covering
/// what it sees, and at <see cref="Scene.MoonMeshReach"/> the mesh's outline where the sky's
/// ray-cast has it, so the handover doesn't show.
/// </summary>
public sealed class MoonDrawingTests(ITestOutputHelper output)
{
    /// <summary>A view's projection (the game's field of view, at 16:9), from <paramref name="camera"/> towards <paramref name="target"/>.</summary>
    private static Matrix4x4 ViewFrom(Vector3 camera, Vector3 target, Vector3 up) =>
        Matrix4x4.CreateLookAt(camera, target, up)
        * Matrix4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(Scene.CameraVerticalFovDegrees), 16f / 9f, 0.5f, 40_000f);

    /// <summary>A hash of every field of every patch, in order.</summary>
    private static ulong Hash(List<PlanetPatch> patches)
    {
        var hash = 14695981039346656037UL;
        void Add(int value)
        {
            hash ^= (uint)value;
            hash *= 1099511628211UL;
        }
        foreach (var p in patches)
        {
            Add(p.Face);
            Add(BitConverter.SingleToInt32Bits(p.U0));
            Add(BitConverter.SingleToInt32Bits(p.V0));
            Add(BitConverter.SingleToInt32Bits(p.Size));
            Add(p.Level);
            Add(p.Water ? 1 : 0);
        }
        return hash;
    }

    [Fact]
    public void The_planets_patches_match_its_own_selections()
    {
        // Six places (above the pad, out over the sea, by a cube face's edge, high up, from orbit
        // and from the moon's distance), each looking somewhere, with both views' ratios, culled by
        // the view and not: each list, to the bit, as PlanetsPatches has it (the planet's own
        // selection's, with no turn and nothing of any other body in it).
        Vector3[] cameras =
        [
            new(0f, 30f, 0f),
            new(1500f, 8f, -200f),
            Planet.Centre + Vector3.Normalize(new Vector3(1f, 0.999f, 0.3f)) * (Planet.Radius + 60f),
            new(-400f, 2500f, 900f),
            Planet.Centre + Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)) * 30_000f,
            Planet.Centre + Vector3.Normalize(new Vector3(-0.6f, 0.4f, 0.7f)) * 45_000f,
        ];
        var patches = new List<PlanetPatch>();
        var hashes = new List<string>();
        foreach (var camera in cameras)
        {
            var view = ViewFrom(camera, Planet.Centre + Vector3.Normalize(camera - Planet.Centre + new Vector3(300f, 0f, -900f)) * Planet.Radius, Planet.Up(camera));
            foreach (var ratio in (ReadOnlySpan<float>)[Scene.TerrainLodRatio, Scene.SmallViewTerrainLodRatio])
            {
                PlanetPatches.Select(camera, ratio, patches, viewProjection: view);
                hashes.Add($"{patches.Count}:{Hash(patches):x16}");
                PlanetPatches.Select(camera, ratio, patches, cull: false);
                hashes.Add($"{patches.Count}:{Hash(patches):x16}");
            }
        }
        output.WriteLine(string.Join(", ", hashes.Select(h => $"\"{h}\"")));
        Assert.Equal(PlanetsPatches, hashes);
    }

    [Fact]
    public void At_the_handover_the_mesh_is_shaded_as_the_sky_shades_the_moon()
    {
        // The mesh's relief eases to none at the reach, but its shading must not: the map's slopes
        // are read in full at every relief, over the same spread as the sky's ray-cast reads them,
        // so a sphere drawn by either looks the same. (A check of the GLSL's text: the shaders can
        // be compiled here but not run.) The fragment reads nothing of the relief (its draw's
        // model, where the vertex shader takes it), and the sky's normal is the same slope's.
        var fragment = Shaders.MoonFragment;
        var main = System.Text.RegularExpressions.Regex.Replace(fragment[fragment.LastIndexOf("void main()", StringComparison.Ordinal)..], @"//[^\n]*", ""); // its code, not its comments
        const string spread = "clamp(footprint, MOON_TEXEL, 32.0 * MOON_TEXEL)";
        Assert.DoesNotContain("draw.model", main);
        Assert.DoesNotContain("relief", main);
        Assert.Contains($"vec3 n = normalize(m - moonSlope(m, height, {spread}) - vDetailSlope);", main);
        Assert.Contains($"vec3 n = moonNormal(m, height, {spread});", Shaders.SkyFragment);
        Assert.Contains("return normalize(d - moonSlope(d, height, spread));", Shaders.SkyFragment);
        // Both light it by the one function.
        Assert.Contains("vec3 sun = sunFromMoon();", main);
        Assert.Contains("moonLight(m, height, albedo, n, sun, vWorldPos, shadowed)", main); // and the vessels' shadows on it, which the sky has none of
        Assert.Contains("moonLight(m, height, albedo, n, sunFromMoon(), world, 1.0)", Shaders.SkyFragment);
    }

    [Fact]
    public void In_an_eclipse_the_mesh_draws_the_coronas_glow_over_the_moons_limb()
    {
        // The sky paints the corona's glow coming in over the moon's limb, which the mesh, opaque,
        // covers within the reach: so the mesh draws that part itself, by the sky's own function
        // (its light from the camera's share of the sun's disc, as drawn, covered), and the sky the rest.
        var main = Shaders.MoonFragment[Shaders.MoonFragment.LastIndexOf("void main()", StringComparison.Ordinal)..];
        Assert.Contains("corona(normalize(vWorldPos - frame.cameraPos.xyz), (1.0 - sunSeenFromCamera()) * (1.0 - frame.averaged.x), true)", main); // (the share by the disc as drawn from the camera; none in the averaged light)
        Assert.Contains("rgb += corona(d, covered, false);", Shaders.SkyFragment);
    }

    /// <summary>A time of the world to look at the moon at, a little way into its lap and the planet's turn.</summary>
    private const double Time = 1234.5;

    private static Vector3 LandingSpot =>
        CubeSphere.FromLatLon(float.DegreesToRadians(Scene.MoonLandingSpot.Latitude), float.DegreesToRadians(Scene.MoonLandingSpot.Longitude));

    /// <summary>The moon as the renderer would hand it to the selection from <paramref name="camera"/> at <see cref="Time"/>.</summary>
    private static PatchBody MoonFrom(Vector3 camera)
    {
        var centre = SolarSystem.MoonCentre(Time);
        return PatchBody.Moon(centre, SolarSystem.MoonOrientation(Time), PatchBody.MoonRelief((camera - centre).Length()));
    }

    private static bool Holds(PlanetPatch p, Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        return p.Face == face && u >= p.U0 && u <= p.U0 + p.Size && v >= p.V0 && v <= p.V0 + p.Size;
    }

    [Fact]
    public void Near_the_moon_its_patches_cover_what_the_camera_sees()
    {
        // 100 m over the landing spot, looking out along its ground a little down.
        var spot = LandingSpot;
        var camera = SolarSystem.FromMoon(spot * (Scene.MoonRadius + MoonGround.Height(spot) + 100f), Time);
        var body = MoonFrom(camera);
        Assert.Equal(1f, body.Relief);
        var up = SolarSystem.FromMoon(spot, Time) - SolarSystem.MoonCentre(Time);
        up = Vector3.Normalize(up);
        var across = Vector3.Normalize(Vector3.Cross(up, new Vector3(0.3f, 0.1f, 0.9f)));
        var view = ViewFrom(camera, camera + across * 400f - up * 60f, up);

        var all = new List<PlanetPatch>();
        PlanetPatches.Select(body, camera, Scene.TerrainLodRatio, all);
        // (The spot is on an edge between patches: every one holding it.)
        var under = all.Where(p => Holds(p, spot)).ToArray();
        Assert.NotEmpty(under);
        Assert.All(under, p => Assert.Equal(Scene.MoonPatchMaxLevel, p.Level));
        Assert.True(all.Count <= Scene.TerrainPatchBudget, $"{all.Count} patches");

        var seen = new List<PlanetPatch>();
        PlanetPatches.Select(body, camera, Scene.TerrainLodRatio, seen, viewProjection: view);
        Assert.True(seen.Count < all.Count, $"{seen.Count} patches of {all.Count} kept");
        output.WriteLine($"{all.Count} patches round, {seen.Count} in the view; under the camera {under[0]}");

        // Wherever the moon's ground is on the screen (and over the horizon), a patch is drawn there.
        var rng = new Random(7);
        var checkedPoints = 0;
        for (var n = 0; n < 3000; n++)
        {
            var d = Vector3.Normalize(spot + new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f) * 0.6f);
            var ground = SolarSystem.FromMoon(d * (Scene.MoonRadius + MoonGround.Height(d)), Time);
            var clip = Vector4.Transform(new Vector4(ground, 1f), view);
            if (clip.W <= 0f || MathF.Abs(clip.X) > clip.W || MathF.Abs(clip.Y) > clip.W)
                continue;
            if (!all.Any(p => Holds(p, d)))
                continue; // below the horizon
            Assert.Contains(seen, p => Holds(p, d));
            checkedPoints++;
        }
        Assert.True(checkedPoints > 200, $"only {checkedPoints} points of ground on the screen");

        // Every view does it every frame, into a list it keeps.
        var before = GC.GetAllocatedBytesForCurrentThread();
        PlanetPatches.Select(body, camera, Scene.TerrainLodRatio, seen, viewProjection: view);
        PlanetPatches.Select(body, camera, Scene.SmallViewTerrainLodRatio, seen);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Anywhere_near_the_moon_a_view_needs_no_more_than_the_budget()
    {
        var rng = new Random(9);
        var patches = new List<PlanetPatch>();
        var most = 0;
        for (var n = 0; n < 400; n++)
        {
            Vector3 d;
            do d = new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1);
            while (d.LengthSquared() is < 0.01f or > 1f);
            d = Vector3.Normalize(d);
            var height = MoonGround.Height(d) + 2f + MathF.Pow(rng.NextSingle(), 3) * (Scene.MoonMeshReach - Scene.MoonRadius - 1000f);
            var camera = SolarSystem.FromMoon(d * (Scene.MoonRadius + height), Time);
            var up = Vector3.Normalize(camera - SolarSystem.MoonCentre(Time));
            var east = Vector3.Normalize(Vector3.Cross(new Vector3(0.01f, 1f, 0.02f), up));
            var north = Vector3.Cross(up, east);
            var (heading, pitch) = (rng.NextSingle() * MathF.Tau, -1.5f + rng.NextSingle() * 1.6f);
            var look = (east * MathF.Cos(heading) + north * MathF.Sin(heading)) * MathF.Cos(pitch) + up * MathF.Sin(pitch);
            var view = ViewFrom(camera, camera + look * 100f, up);
            foreach (var ratio in (ReadOnlySpan<float>)[Scene.TerrainLodRatio, Scene.SmallViewTerrainLodRatio])
            {
                PlanetPatches.Select(MoonFrom(camera), camera, ratio, patches, viewProjection: view);
                most = Math.Max(most, patches.Count);
                Assert.True(patches.Count <= Scene.TerrainPatchBudget, $"{patches.Count} patches from {height} m up");
            }
        }
        output.WriteLine($"at most {most} patches");
    }

    [Fact]
    public void At_the_handover_the_mesh_and_the_ray_cast_agree()
    {
        // At the reach the mesh's relief is gone: it is the datum's sphere, the ray-cast's, shaded
        // from the same map; within the relief's own reach it is all there.
        Assert.Equal(0f, PatchBody.MoonRelief(Scene.MoonMeshReach));
        Assert.Equal(1f, PatchBody.MoonRelief(Scene.MoonReliefWithin));
        Assert.False(PatchBody.DrawsMoon(Scene.MoonMeshReach));
        Assert.True(PatchBody.DrawsMoon(Scene.MoonMeshReach - 1f));

        // The outline: rays from just inside the reach, all round the limb, against the coarse
        // patches' triangles (their grid points on the sphere, as the vertex shader puts them) and
        // against the sphere. The mesh's edge, found ray by ray, is within a pixel of the sphere's
        // (at 1280x720), with both views' ratios and from several sides.
        var pixel = float.DegreesToRadians(Scene.CameraVerticalFovDegrees) / 720f;
        var distance = Scene.MoonMeshReach - 1f;
        var sphere = MathF.Asin(Scene.MoonRadius / distance);
        var worst = 0f;
        var patches = new List<PlanetPatch>();
        foreach (var side in (ReadOnlySpan<Vector3>)[new(0f, 0f, 1f), new(0.6f, -0.3f, 0.74f), new(-1f, 1f, 1f), new(0.2f, 1f, -0.1f)])
        {
            var outward = Vector3.Normalize(side); // moon-fixed, from its centre to the camera
            var camera = SolarSystem.FromMoon(outward * distance, Time);
            var centre = SolarSystem.MoonCentre(Time);
            var body = MoonFrom(camera);
            Assert.Equal(0f, body.Relief, 6);
            var up = Vector3.Normalize(Vector3.Cross(centre - camera, new Vector3(0.2f, 0.9f, 0.4f)));
            var view = ViewFrom(camera, centre, up);
            foreach (var ratio in (ReadOnlySpan<float>)[Scene.TerrainLodRatio, Scene.SmallViewTerrainLodRatio])
            {
                PlanetPatches.Select(body, camera, ratio, patches, viewProjection: view);
                var grids = Grids(patches);
                var eye = outward * distance; // in the moon's own axes
                var a = Vector3.Normalize(Vector3.Cross(outward, MathF.Abs(outward.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
                var b = Vector3.Cross(outward, a);
                for (var k = 0; k < 90; k++)
                {
                    var phi = k * MathF.Tau / 90f;
                    var round = a * MathF.Cos(phi) + b * MathF.Sin(phi);
                    Vector3 Ray(float angle) => -outward * MathF.Cos(angle) + round * MathF.Sin(angle);
                    // The edge lies between a ray that hits the mesh and one that misses it.
                    var (hit, miss) = (sphere - 4f * pixel, sphere + 0.01f * pixel);
                    Assert.True(Hits(grids, eye, Ray(hit)), $"a ray {hit} rad from the centre misses the mesh");
                    Assert.False(Hits(grids, eye, Ray(miss)), $"a ray outside the sphere hits the mesh");
                    for (var i = 0; i < 10; i++)
                    {
                        var mid = (hit + miss) * 0.5f;
                        if (Hits(grids, eye, Ray(mid))) hit = mid; else miss = mid;
                    }
                    worst = MathF.Max(worst, (sphere - hit) / pixel);
                }
            }
        }
        output.WriteLine($"the mesh's outline at most {worst:F3} pixels inside the sphere's");
        Assert.True(worst < 1f, $"the mesh's outline {worst} pixels inside the sphere's");
    }

    /// <summary>Each patch's grid points with no relief (on the datum's sphere, moon-fixed), as the vertex shader places them, row by row.</summary>
    private static List<Vector3[]> Grids(List<PlanetPatch> patches)
    {
        const int n = Scene.PlanetPatchGrid;
        var grids = new List<Vector3[]>();
        foreach (var p in patches)
        {
            var grid = new Vector3[(n + 1) * (n + 1)];
            for (var j = 0; j <= n; j++)
            for (var i = 0; i <= n; i++)
                grid[j * (n + 1) + i] = CubeSphere.FromFace(p.Face, p.U0 + p.Size * ((float)i / n), p.V0 + p.Size * ((float)j / n)) * Scene.MoonRadius;
            grids.Add(grid);
        }
        return grids;
    }

    /// <summary>Whether a ray from <paramref name="eye"/> along <paramref name="ray"/> (both moon-fixed) meets a triangle of the grids (two a square, as the patches' indices have them).</summary>
    private static bool Hits(List<Vector3[]> grids, Vector3 eye, Vector3 ray)
    {
        // Where it passes nearest the centre: only the squares round that can be what it meets at the limb.
        var nearest = Vector3.Normalize(eye + ray * -Vector3.Dot(eye, ray)) * Scene.MoonRadius;
        var near = Scene.MoonRadius * Scene.MoonRadius * MathF.Cos(0.4f);
        const int n = Scene.PlanetPatchGrid, side = n + 1;
        foreach (var grid in grids)
        {
            if (Vector3.Dot(grid[0], nearest) < 0f && Vector3.Dot(grid[^1], nearest) < 0f && Vector3.Dot(grid[n], nearest) < 0f && Vector3.Dot(grid[n * side], nearest) < 0f)
                continue;
            for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
            {
                int a = j * side + i, b = a + 1, c = a + side + 1, d = a + side;
                if (Vector3.Dot(grid[a], nearest) < near)
                    continue;
                if (Crosses(eye, ray, grid[a], grid[b], grid[c]) || Crosses(eye, ray, grid[a], grid[c], grid[d]))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Möller–Trumbore: whether the ray meets the triangle, ahead of the eye.</summary>
    private static bool Crosses(Vector3 eye, Vector3 ray, Vector3 a, Vector3 b, Vector3 c)
    {
        var (e1, e2) = (b - a, c - a);
        var p = Vector3.Cross(ray, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f)
            return false;
        var t = eye - a;
        var u = Vector3.Dot(t, p) / det;
        if (u < 0f || u > 1f)
            return false;
        var q = Vector3.Cross(t, e1);
        var v = Vector3.Dot(ray, q) / det;
        return v >= 0f && u + v <= 1f && Vector3.Dot(e2, q) / det > 0f;
    }

    [Fact]
    public void Up_close_the_ground_is_placed_to_a_millimetre()
    {
        // As the vertex shader places it, in floats: the moon's centre from the camera (the
        // uniforms' frame.moon, from the two floats the game has), plus the ground's point turned
        // out of the moon's axes by the uniforms' rotation (frame.moonFrame's inverse). Against the
        // same worked out in double from the same floats: what the float maths adds, which is what
        // could make the ground jitter as the camera moves. 100 m over the landing spot, the ground
        // under it and 2 km off.
        var spot = LandingSpot;
        var centre = SolarSystem.MoonCentre(Time);
        var orientation = SolarSystem.MoonOrientation(Time);
        var camera = SolarSystem.FromMoon(spot * (Scene.MoonRadius + MoonGround.Height(spot) + 100f), Time);
        var turn = Matrix4x4.CreateFromQuaternion(orientation); // the inverse of frame.moonFrame (its transpose)
        var worst = 0.0;
        foreach (var d in (ReadOnlySpan<Vector3>)[spot, Vector3.Normalize(spot + new Vector3(0.8f, 0f, 0f)), Vector3.Normalize(spot + new Vector3(0f, 0.4f, 0f))])
        {
            var point = d * (Scene.MoonRadius + MoonGround.Height(d));
            var drawn = (centre - camera) + Vector3.Transform(point, turn);
            var (x, y, z) = Exact(point, orientation);
            var truth = (X: (double)centre.X - camera.X + x, Y: (double)centre.Y - camera.Y + y, Z: (double)centre.Z - camera.Z + z);
            var error = Math.Sqrt(Math.Pow(drawn.X - truth.X, 2) + Math.Pow(drawn.Y - truth.Y, 2) + Math.Pow(drawn.Z - truth.Z, 2));
            worst = Math.Max(worst, error);
        }
        output.WriteLine($"placed to {worst * 1000:F3} mm");
        Assert.True(worst < 0.001, $"placed only to {worst * 1000} mm");
    }

    /// <summary>
    /// The sky from a lander standing at the landing spot, seen from the planet's ground's frame (the
    /// renderer's), the camera following it as the game's does, 60 frames a second for ten seconds: a pixel's view ray, made as the sky pass makes it
    /// (<see cref="VulkanRenderer.ViewRays"/>, then in float as the shader does), moves against the
    /// moon's ground by well under a pixel from one frame to the next, as the ground itself does.
    /// The whole view-projection's inverse, less the camera's place, moved it by pixels there (the
    /// stars and the planet swimming while the ground held still).
    /// </summary>
    [Fact]
    public void The_sky_holds_still_over_a_lander_on_the_moon()
    {
        var place = OnTheMoon.Standing(OnTheMoon.LandingSpot).Position - GroundBody.Moon.Centre; // from the moon's centre, in its own axes
        var camera = new ChaseCamera(null);
        var fov = float.DegreesToRadians(Scene.CameraVerticalFovDegrees);
        var pixels = 1080f / fov; // a radian across a 1080-pixel-tall view
        Vector3? last = null;
        var worst = 0f;
        for (var i = 0; i < 600; i++)
        {
            var t = 13_600.0 + i / 60.0;
            camera.Update(1f / 60f, SolarSystem.FromMoon(place + Vector3.Normalize(place) * 6f, t), 1f / 60f, GroundBody.Planet, 0);
            var view = Matrix4x4.CreateLookAt(camera.Position, camera.Target, camera.WorldUp);
            var (near, far) = ChaseCamera.Planes;
            var projection = VulkanRenderer.CameraProjection(fov, 16f / 9f, near, far);
            var point = Vector4.Transform(new Vector4(0.3f, -0.4f, 1f, 1f), VulkanRenderer.ViewRays(view, projection));
            var ray = Vector3.Normalize(new Vector3(point.X, point.Y, point.Z) / point.W);
            var onMoon = Vector3.Transform(ray, Quaternion.Conjugate(SolarSystem.MoonOrientation(t))); // still, for a camera still on the moon
            if (last is { } before)
                worst = MathF.Max(worst, MathF.Atan2(Vector3.Cross(before, onMoon).Length(), Vector3.Dot(before, onMoon)) * pixels); // not Acos, whose float steps near 1 are a third of a pixel
            last = onMoon;
        }
        output.WriteLine($"the sky moved at most {worst:F3} px a frame (camera {camera.Position.Length():0} m from the origin)");
        Assert.True(worst < 0.2f, $"the sky moved {worst} px in a frame");
    }

    [Fact]
    public void A_small_view_draws_as_far_as_the_main_view()
    {
        // From the moon the planet's far side is some 50 km off: a small view's camera on a lander
        // there (its own, in the moon's frame, as Views keeps it) draws as far as the main view's
        // would there, past it (beyond 40 km).
        const double T = 1234.5;
        var small = new ChaseCamera(null);
        small.Update(1f / 60f, OnTheMoon.Standing(OnTheMoon.LandingSpot).Position, null, GroundBody.Moon, 0);
        Assert.Equal(ChaseCamera.Planes, small.Clip);
        var planet = Bodies.CentreIn(GroundBody.Planet, GroundBody.Moon, T).ToSingle();
        Assert.True(small.Clip.Far > Vector3.Distance(small.Position, planet) + Planet.Radius, "the planet's far side is beyond it");
    }

    [Fact]
    public void The_skys_rays_are_true_from_a_pad_far_from_the_origin()
    {
        // Coral Isle's pad is about 20 km from the origin, where a ray made by subtracting the
        // camera from a far point snapped to a grid pixels across: the moon drawn in blocks.
        var site = LaunchSites.Named("Coral Isle");
        var camera = site.Pad + site.Up * 40f + Planet.East(site.Pad) * 8f;
        var look = Vector3.Normalize(SolarSystem.MoonCentre(2330) - camera);
        var view = Matrix4x4.CreateLookAt(camera, camera + look * 12f, site.Up);
        var fov = float.DegreesToRadians(Scene.CameraVerticalFovDegrees);
        var (near, far) = ChaseCamera.Planes;
        var projection = VulkanRenderer.CameraProjection(fov, 16f / 9f, near, far);
        var rays = VulkanRenderer.ViewRays(view, projection);
        var turn = view;
        turn.Translation = Vector3.Zero;
        Matrix4x4.Invert(turn, out var unturn);
        var (tanY, tanX) = (MathF.Tan(fov / 2f), MathF.Tan(fov / 2f) * 16f / 9f);
        var pixels = 1080f / fov;
        var worst = 0f;
        for (var row = 0; row < 120; row++) // rows a pixel apart round the middle, as across the moon
        {
            var (x, y) = (0.05f, (row - 60) * 2f / 1080f);
            var point = Vector4.Transform(new Vector4(x, y, 1f, 1f), rays);
            var ray = Vector3.Normalize(new Vector3(point.X, point.Y, point.Z) / point.W);
            var truth = Vector3.Normalize(Vector3.TransformNormal(new Vector3(x * tanX, -y * tanY, -1f), unturn));
            worst = MathF.Max(worst, MathF.Atan2(Vector3.Cross(ray, truth).Length(), Vector3.Dot(ray, truth)) * pixels);
        }
        output.WriteLine($"the rays were at most {worst:F3} px off (camera {camera.Length():0} m from the origin)");
        Assert.True(worst < 0.2f, $"a ray was {worst} px off");
    }

    /// <summary>A world at <paramref name="time"/> holding <paramref name="vessels"/> (beside the new rocket on Home's pad).</summary>
    private static WorldState WorldOf(double time, params VesselState[] vessels) =>
        new() { Time = time, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    /// <summary>The vessel numbered <paramref name="id"/>, flown: at a body with no physics, made behind the link-up the frame after.</summary>
    private static Vessel Fly(Simulation sim, int id)
    {
        var vessel = sim.Flight.WorldVessels.Single(v => v.Id == id);
        sim.Step(new FlightInput(SwitchTo: vessel));
        sim.Step(default);
        Assert.Same(vessel, sim.Flight.Controlled);
        return vessel;
    }

    /// <summary>
    /// One frame as GameView draws it, without the window: the bodies' frames begun, the camera
    /// following the flown vessel in its body's frame, and every vessel gathered for the renderer.
    /// </summary>
    private static void Draw(Flight flight, ChaseCamera camera, BodyFrames frames, SceneInput scene)
    {
        var vessel = flight.Controlled;
        frames.Begin(flight.WorldTime, flight.Body);
        camera.Update(Simulation.FrameSeconds, Views.Focus(vessel), Simulation.FrameSeconds, vessel.Body, 0);
        scene.Gather(flight, frames, camera, paused: false);
    }

    [Fact]
    public void The_depth_reaches_past_every_body_and_the_sun_from_anywhere_a_vessel_or_the_map_can_be()
    {
        // The planes are the same everywhere, at every zoom: near half a metre, far at infinity, so
        // every body's far side and the sun's are in front of the far plane from anywhere (the
        // map's furthest zoom included). The depth is reversed (1 at the near plane), so as a float
        // (D32F) it tells apart two places a millionth of their distance apart at any distance:
        // each body's and the sun's near side from a hair behind it, from Home's pad out to the
        // map's furthest zoom. In standard depth with the far plane past the sun, its last step there
        // would be all of the planet.
        var (near, far) = ChaseCamera.Planes;
        Assert.Equal(0.5f, near);
        Assert.Equal(float.PositiveInfinity, far);
        var (planet, moon) = (GroundBody.Planet, GroundBody.Moon);
        var places = new (string Name, GroundBody Frame, Vector3 At)[]
        {
            ("Home's pad", planet, new Vector3(0f, 2f, 0f)),
            ("a low orbit over Home", planet, planet.Centre + Vector3.UnitY * (planet.SeaRadius + 1500f)),
            ("a low orbit on the far side", planet, planet.Centre - Vector3.UnitY * (planet.SeaRadius + 1500f)),
            ("the moon's sphere's edge", moon, moon.Centre + Vector3.UnitX * moon.SphereRadius),
            ("landed on the moon", moon, OnTheMoon.Standing(OnTheMoon.LandingSpot).Position + Vector3.UnitY * 6f),
            ("the map's furthest zoom", planet, planet.Centre + Vector3.Normalize(new Vector3(0.3f, 0.4f, -1f)) * Scene.ZoomOutMaxDistance),
        };
        var projection = VulkanRenderer.CameraProjection(float.DegreesToRadians(Scene.CameraVerticalFovDegrees), 16f / 9f, near, far);
        float Depth(float distance) // straight ahead, as the renderer's depth has it
        {
            var clip = Vector4.Transform(new Vector4(0f, 0f, -distance, 1f), projection);
            return clip.Z / clip.W;
        }
        Assert.Equal(1f, Depth(near), 1e-6f);
        var furthest = 0.0;
        foreach (var (name, frame, at) in places)
            for (var k = 0; k < 12; k++) // round a lap of the moon
            {
                var t = SolarSystem.MoonLapSeconds * k / 12;
                var sun = (SolarSystem.SunCentreAt(t) - Bodies.Change(frame, planet, t).Point(at)).Length();
                foreach (var (what, centre, radius) in Bodies.All.Select(b => (b.Name, Vector3.Distance(at, Bodies.CentreIn(b, frame, t).ToSingle()), b.SeaRadius + b.MaxHeight))
                    .Append(("the sun", (float)sun, Scene.SunRadius)))
                {
                    var (nearSide, farSide) = (centre - radius, centre + radius);
                    Assert.True(Depth(farSide) > 0f, $"from {name} at {t:0} s {what}'s far side, {farSide:0} m off, is at the far plane");
                    Assert.True(nearSide < near || Depth(nearSide) > Depth(nearSide * (1f + 1e-6f)), // (on or by the body itself, its near side is under the camera)
$"from {name} at {t:0} s the depth can't tell {what}'s near side, {nearSide:0} m off, from a millionth further");
                    furthest = Math.Max(furthest, farSide);
                }
            }
        output.WriteLine($"the furthest far side, {furthest:0} m off, is at depth {Depth((float)furthest):E3}; its step there {MathF.BitIncrement(Depth((float)furthest)) - Depth((float)furthest):E3}");
    }

    [Fact]
    public void A_vessel_landed_on_the_moon_is_drawn_still_on_its_ground()
    {
        // A lander on the moon flown, the camera following it, for ten seconds: its model as handed
        // to the renderer (from the camera), less the ground under it as the renderer places that
        // (the moon's centre from the camera, and its ground turned out of the moon's own axes by
        // its orientation, as the mesh's vertex shader does), holds still to well under 5 mm a
        // frame, though the moon carries both some 40 km from the scene's origin.
        using var sim = new Simulation(Mission.FreePlay, world: WorldOf(Time, OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var lander = Fly(sim, 0);
        var moon = GroundBody.Moon;
        var down = Vector3.Normalize(lander.Position - moon.Centre); // its moon-fixed direction, from the moon's centre
        var ground = down * (moon.SeaRadius + moon.Height(down));
        var (camera, frames, scene) = (new ChaseCamera(null), new BodyFrames(), new SceneInput());
        Vector3? last = null;
        var (worst, moved, start) = (0f, 0f, lander.Model.Translation);
        for (var i = 0; i < 600; i++)
        {
            sim.Step(default);
            Draw(sim.Flight, camera, frames, scene);
            var sky = new FrameInput();
            GameView.FillSky(ref sky, sim.Flight.WorldTime);
            var drawnGround = scene.MoonFromCamera + Vector3.Transform(ground, sky.MoonOrientation);
            var expected = (Bodies.Change(moon, GroundBody.Planet, sim.Flight.WorldTime).Point(lander.Model.Translation) - scene.Origin).ToSingle();
            var part = scene.Parts.MinBy(p => Vector3.Distance(p.Model.Translation, expected));
            Assert.True(Vector3.Distance(part.Model.Translation, expected) < 0.01f, "the lander is drawn where it is");
            var onGround = part.Model.Translation - drawnGround;
            if (last is { } before)
                worst = MathF.Max(worst, Vector3.Distance(onGround, before));
            last = onGround;
            moved = MathF.Max(moved, Vector3.Distance(lander.Model.Translation, start));
        }
        output.WriteLine($"drawn on its ground to {worst * 1000:0.000} mm a frame (it moved {moved * 1000:0.0} mm on the moon; the camera {scene.CameraPosition.Length():0} m from the scene's origin)");
        Assert.True(worst < 0.005f, $"it moved {worst * 1000:0.0} mm against its ground in a frame");
    }

    [Fact]
    public void Vessels_at_every_body_are_drawn_where_they_are()
    {
        // The new rocket on Home's pad (in the planet's physics), a lander flown on the moon (in the
        // moon's) and another far round the moon (under formula there): each stage handed to the
        // renderer is its own model in its body's frame changed into the planet's by
        // Bodies.Change(its body, planet), placed from the camera, to within a centimetre.
        using var sim = new Simulation(Mission.FreePlay, world: WorldOf(Time,
            OnTheMoon.Lander(0, OnTheMoon.LandingSpot), OnTheMoon.Lander(1, CubeSphere.FromLatLon(0f, 0f))));
        Fly(sim, 0);
        for (var i = 0; i < 10; i++)
            sim.Step(default);
        var (camera, frames, scene) = (new ChaseCamera(null), new BodyFrames(), new SceneInput());
        Draw(sim.Flight, camera, frames, scene);

        var flight = sim.Flight;
        var t = flight.WorldTime;
        var expected = new List<(GroundBody Body, StagePart Part)>();
        var own = new List<StagePart>();
        foreach (var body in Bodies.All)
            if (flight.PhysicsAt(body) is { } physics)
            {
                own.Clear();
                physics.AddParts(own);
                expected.AddRange(own.Select(p => (body, p)));
            }
        foreach (var v in flight.WorldVessels.Where(v => v.OnFormula))
        {
            own.Clear();
            v.FormulaParts(own);
            expected.AddRange(own.Select(p => (v.Body, p)));
        }
        Assert.Contains(expected, e => e.Body == GroundBody.Planet);
        Assert.Contains(expected, e => e.Body == GroundBody.Moon);
        Assert.Contains(flight.WorldVessels, v => v.OnFormula && v.Body == GroundBody.Moon);
        Assert.Equal(expected.Count, scene.Parts.Count);
        var worst = 0.0;
        for (var i = 0; i < expected.Count; i++)
        {
            var (body, part) = expected[i];
            var change = Bodies.Change(body, GroundBody.Planet, t);
            var at = change.Point(part.Model.Translation) - scene.Origin;
            var drawn = scene.Parts[i].Model.Translation;
            var off = Math.Sqrt(Math.Pow(drawn.X - at.X, 2) + Math.Pow(drawn.Y - at.Y, 2) + Math.Pow(drawn.Z - at.Z, 2));
            worst = Math.Max(worst, off);
            Assert.True(off < 0.01, $"a stage at {body.Name} is drawn {off:0.000} m from where it is");
            // And turned as its body's frame is turned in the planet's.
            var up = Vector3.Normalize(change.Direction(Vector3.TransformNormal(Vector3.UnitY, part.Model)).ToSingle());
            Assert.True(Vector3.Distance(up, Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, scene.Parts[i].Model))) < 1e-4f, $"a stage at {body.Name} is drawn turned");
        }
        output.WriteLine($"{expected.Count} stages, each drawn within {worst * 1000:0.000} mm of where it is");
    }

    [Fact]
    public void Gathering_what_is_drawn_allocates_nothing()
    {
        // GameView's gathering of every vessel to draw, each changed from its body's frame into the
        // planet's, a frame at a time, with vessels at both bodies and an explosion on the moon
        // (GameView itself needs a window, so what it asks is asked directly).
        using var sim = new Simulation(Mission.FreePlay, world: WorldOf(Time,
            OnTheMoon.Lander(0, OnTheMoon.LandingSpot), OnTheMoon.Lander(1, CubeSphere.FromLatLon(0f, 0f))));
        Fly(sim, 0);
        var (camera, frames, scene, effects) = (new ChaseCamera(null), new BodyFrames(), new SceneInput(), new Effects());
        effects.Explode(GroundBody.Moon, OnTheMoon.Standing(OnTheMoon.LandingSpot).Position, sim.Flight.WorldTime);
        for (var i = 0; i < 30; i++)
        {
            sim.Step(default);
            Draw(sim.Flight, camera, frames, scene);
            effects.Update(sim.Flight.WorldTime, frames);
        }
        var bytes = 0L;
        for (var i = 0; i < 60; i++)
        {
            sim.Step(default);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Draw(sim.Flight, camera, frames, scene);
            effects.Update(sim.Flight.WorldTime, frames);
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, bytes);
    }

    /// <summary><paramref name="p"/> turned by <paramref name="q"/>, in double.</summary>
    private static (double X, double Y, double Z) Exact(Vector3 p, Quaternion q)
    {
        double w = q.W, x = q.X, y = q.Y, z = q.Z;
        var n = Math.Sqrt(w * w + x * x + y * y + z * z);
        (w, x, y, z) = (w / n, x / n, y / n, z / n);
        // v + 2w(u x v) + 2u x (u x v), u the quaternion's vector part.
        double cx = y * p.Z - z * p.Y, cy = z * p.X - x * p.Z, cz = x * p.Y - y * p.X;
        double ex = y * cz - z * cy, ey = z * cx - x * cz, ez = x * cy - y * cx;
        return (p.X + 2 * (w * cx + ex), p.Y + 2 * (w * cy + ey), p.Z + 2 * (w * cz + ez));
    }

    /// <summary>
    /// The planet's patches for <see cref="The_planets_patches_match_its_own_selections"/>'s views, in its
    /// order: each list's count and the hash of its fields (<see cref="Hash"/>).
    /// </summary>
    private static readonly string[] PlanetsPatches =
    [
        "152:44e3780d9a0c2ea8", "942:c4e5a3c025c09cbd", "79:b8c738c013a76f34", "312:4219a084db26b152",
        "137:6456389cb297a3e1", "861:369f86996c8c5959", "53:45de67929276a460", "210:972ca344b5a79f71",
        "223:ad7a3e0f3b0efe79", "1149:bf4be14847f733c1", "63:c22a24ca38f7edfe", "297:5b93fb9b8a538eec",
        "142:2f8fcbe1e19660a0", "336:79923b70c831329a", "42:96a1e042d8b7c197", "78:f16c07f36830cd4e",
        "43:ecc45de511521e32", "48:7681dc3624528295", "8:4dcbd3abb29e9895", "9:fc1f9ff9a9e6290d",
        "20:4aeaeda365d0a57a", "24:c4d9fc7228ab87cd", "6:f9089b3cd5347bec", "6:f9089b3cd5347bec",
    ];
}
