using System.Numerics;
using GameOne.Rendering;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The sun's shadow areas (<see cref="SunAreas"/>): the main view's as it always was, a small view
/// sharing it while its vessel is well inside it, an area of its own when its vessel is away from
/// every area already chosen, and the areas' places in the shadow map.
/// </summary>
public sealed class SunAreaTests
{
    private static readonly LaunchSite Home = LaunchSites.Home, Highcrest = LaunchSites.Named("Highcrest"), Sentinel = LaunchSites.Named("Sentinel");

    /// <summary>A vessel standing at <paramref name="local"/> in <paramref name="site"/>'s frame (its bottom stage's model).</summary>
    private static Matrix4x4 Standing(LaunchSite site, Vector3 local) =>
        Matrix4x4.CreateFromQuaternion(site.Frame) * Matrix4x4.CreateTranslation(site.ToWorld(local));

    /// <summary>The camera a little way off a vessel at <paramref name="local"/>.</summary>
    private static Vector3 Beside(LaunchSite site, Vector3 local) => site.ToWorld(local + new Vector3(0f, 12f, 40f));

    /// <summary>The sun as a new world starts (07:30 at Home), for the views below.</summary>
    private static readonly Vector3 AnySun = Sun.Direction(GroundBody.Planet, 0);

    /// <summary>The sun's view as it was worked out before small views had their own (Task 3's SunViewProjection), to hold the main view's to; up is the north pole, which the sun never lies along.</summary>
    private static (Matrix4x4 ViewProj, float TexelSize) AsBefore(Matrix4x4? rocket, Vector3 camera, Vector3 sun)
    {
        static float SmoothStep(float e0, float e1, float x) { var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f); return t * t * (3f - 2f * t); }
        var pad = LaunchSites.Nearest(camera).ToWorld(new Vector3(0, Scene.TowerHeight / 2, 0));
        var booster = rocket is { } model ? Vector3.Transform(new Vector3(0, Scene.RocketBodyHeight / 2, 0), model) : pad;
        var apart = Vector3.Distance(booster, pad);
        var away = SmoothStep(150f, 250f, apart);
        var centre = Vector3.Lerp((booster + pad) * 0.5f, booster, away);
        var radius = float.Lerp(apart / 2 + 30f, 30f, away);
        const float back = 2000f;
        var view = Matrix4x4.CreateLookAt(centre + sun * back, centre, Planet.NorthPole);
        var projection = Matrix4x4.CreateOrthographic(2 * radius, 2 * radius, 1f, 2 * back);
        projection.M22 = -projection.M22;
        return (view * projection, 2 * radius / 2048);
    }

    [Fact]
    public void With_no_area_chosen_the_main_views_is_the_one_round_its_camera()
    {
        // The renderer must never be left with no area (a 0 by 0 shadow pass, a stale sun matrix).
        var camera = Beside(Home, Vector3.Zero);
        var into = new SunArea[VulkanRenderer.ShadowAreas];
        Assert.Equal(1, VulkanRenderer.FillSunAreas([], camera, into));
        Assert.Equal(SunAreas.Around(null, camera), into[0]);

        // With some, they are copied, no more than the map has room for.
        var chosen = Enumerable.Range(0, VulkanRenderer.ShadowAreas + 2).Select(i => new SunArea(new Vector3(i, 0, 0), 30f + i)).ToArray();
        Assert.Equal(VulkanRenderer.ShadowAreas, VulkanRenderer.FillSunAreas(chosen, camera, into));
        Assert.Equal(chosen.Take(VulkanRenderer.ShadowAreas), into);
    }

    [Fact]
    public void The_main_views_area_is_as_it_was()
    {
        // On the pad, a little way up, past where the area stops taking in the pad, far off, and blown up.
        foreach (var (rocket, camera) in new (Matrix4x4?, Vector3)[]
                 {
                     (Standing(Home, Vector3.Zero), Beside(Home, Vector3.Zero)),
                     (Standing(Home, new Vector3(0f, 120f, 0f)), Beside(Home, new Vector3(0f, 120f, 0f))),
                     (Standing(Home, new Vector3(80f, 400f, 0f)), Beside(Home, new Vector3(80f, 400f, 0f))),
                     (Standing(Highcrest, new Vector3(30f, 0f, 10f)), Beside(Highcrest, new Vector3(30f, 0f, 10f))),
                     (null, Beside(Sentinel, Vector3.Zero)),
                 })
        {
            var areas = new SunAreas();
            var shared = false;
            Assert.Equal(0, areas.Choose(rocket, camera, ref shared));
            var (expected, texel) = AsBefore(rocket, camera, AnySun);
            var (actual, actualTexel) = VulkanRenderer.SunViewProjection(areas.Areas[0], VulkanRenderer.SunAreaRect(0).Size, AnySun);
            Assert.Equal(2048, VulkanRenderer.SunAreaRect(0).Size);
            Assert.Equal(texel, actualTexel);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void At_Homes_noon_the_suns_view_still_takes_in_the_pad()
    {
        // The sun straight overhead, along the default view's up (+Y): with the north pole as up the
        // view is still a proper one, and a box on the pad lands inside the area's square.
        var sun = Sun.Direction(GroundBody.Planet, Sun.WorldTimeFor(Vector3.Zero, 600));
        var areas = new SunAreas();
        var shared = false;
        areas.Choose(Standing(Home, Vector3.Zero), Beside(Home, Vector3.Zero), ref shared);
        var (viewProj, texel) = VulkanRenderer.SunViewProjection(areas.Areas[0], VulkanRenderer.SunAreaRect(0).Size, sun);
        Assert.True(float.IsFinite(texel));
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                Assert.True(float.IsFinite(viewProj[row, column]), $"the sun's view has {viewProj[row, column]} at [{row}, {column}]");
        foreach (var x in new[] { -5f, 5f })
            foreach (var y in new[] { 0f, 10f })
                foreach (var z in new[] { -5f, 5f })
                {
                    var clip = Vector4.Transform(new Vector4(Home.ToWorld(new Vector3(x, y, z)), 1f), viewProj);
                    var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
                    Assert.True(MathF.Abs(ndc.X) < 1f && MathF.Abs(ndc.Y) < 1f && ndc.Z is > 0f and < 1f, $"a corner of the box at ({x}, {y}, {z}) is at {ndc}, outside the area");
                }
    }

    [Fact]
    public void Views_together_share_one_area()
    {
        // The main view's vessel on Home's pad, a small view's beside it on the pad, another's a few metres up.
        var areas = new SunAreas();
        bool main = false, a = false, b = false;
        areas.Choose(Standing(Home, Vector3.Zero), Beside(Home, Vector3.Zero), ref main);
        Assert.Equal(0, areas.Choose(Standing(Home, new Vector3(8f, 0f, 0f)), Beside(Home, new Vector3(8f, 0f, 0f)), ref a));
        Assert.Equal(0, areas.Choose(Standing(Home, new Vector3(0f, 10f, 5f)), Beside(Home, new Vector3(0f, 10f, 5f)), ref b));
        Assert.Equal(1, areas.Areas.Length);
        Assert.True(a && b);
    }

    [Fact]
    public void Views_far_apart_each_have_their_own_area_over_the_same_ground_as_the_main_views()
    {
        var areas = new SunAreas();
        bool main = false, high = false, high2 = false, sentinel = false;
        areas.Choose(Standing(Home, Vector3.Zero), Beside(Home, Vector3.Zero), ref main);
        var highcrest = Standing(Highcrest, new Vector3(30f, 0f, 10f));
        Assert.Equal(1, areas.Choose(highcrest, Beside(Highcrest, new Vector3(30f, 0f, 10f)), ref high));
        Assert.Equal(1, areas.Choose(Standing(Highcrest, new Vector3(31f, 0f, 10f)), Beside(Highcrest, new Vector3(31f, 0f, 10f)), ref high2)); // with the first there
        Assert.Equal(2, areas.Choose(Standing(Sentinel, Vector3.Zero), Beside(Sentinel, Vector3.Zero), ref sentinel));
        Assert.Equal(3, areas.Areas.Length);
        Assert.False(high);
        Assert.True(high2);
        Assert.False(sentinel);

        // Highcrest's covers the ground a main view's would there (the same centre and radius), at half the main view's resolution.
        Assert.Equal(SunAreas.Around(highcrest, Beside(Highcrest, new Vector3(30f, 0f, 10f))), areas.Areas[1]);
        Assert.Equal(VulkanRenderer.SunAreaRect(0).Size / 2, VulkanRenderer.SunAreaRect(1).Size);
        Assert.Equal(VulkanRenderer.SunAreaRect(1).Size, VulkanRenderer.SunAreaRect(VulkanRenderer.ShadowAreas - 1).Size);

        // Next frame afresh.
        areas.Clear();
        Assert.Equal(0, areas.Choose(Standing(Sentinel, Vector3.Zero), Beside(Sentinel, Vector3.Zero), ref main));
        Assert.Equal(1, areas.Areas.Length);
    }

    [Fact]
    public void A_vessel_leaving_the_main_views_area_keeps_sharing_it_within_the_hysteresis()
    {
        // The main view's vessel 300 m up over Home (its area 30 m round it, past taking in the
        // pad); a small view's drifting sideways away from it, and back.
        var main = Standing(Home, new Vector3(0f, 300f, 0f));
        var camera = Beside(Home, new Vector3(0f, 300f, 0f));
        var radius = SunAreas.Around(main, camera).Radius;
        var areas = new SunAreas();
        var held = false;
        int Frame(float x)
        {
            areas.Clear();
            var m = false;
            areas.Choose(main, camera, ref m);
            return areas.Choose(Standing(Home, new Vector3(x, 300f, 0f)), Beside(Home, new Vector3(x, 300f, 0f)), ref held);
        }
        var inside = radius - Scene.SunAreaMargin;
        Assert.Equal(0, Frame(inside - 1f));
        Assert.True(held);
        Assert.Equal(0, Frame(inside + Scene.SunAreaHysteresis - 1f)); // past the edge, but held
        Assert.Equal(1, Frame(inside + Scene.SunAreaHysteresis + 1f)); // beyond the hold: its own
        Assert.False(held);
        Assert.Equal(1, Frame(inside + 1f));                            // back past the edge: still its own
        Assert.Equal(0, Frame(inside - 1f));                            // well inside again: shared
    }

    [Fact]
    public void A_small_view_of_a_wreck_has_no_area_of_its_own()
    {
        // The main view at Home; a small view looking at where a vessel blew up at Sentinel, far
        // off (nothing of it is left to cast a shadow): it shares the main view's, costing nothing.
        var areas = new SunAreas();
        bool main = false, wreck = false, high = false, nearHigh = false;
        areas.Choose(Standing(Home, Vector3.Zero), Beside(Home, Vector3.Zero), ref main);
        Assert.Equal(0, areas.Choose(null, Beside(Sentinel, Vector3.Zero), ref wreck));
        Assert.Equal(1, areas.Areas.Length);

        // One by a place another view has an area over shares that one instead.
        Assert.Equal(1, areas.Choose(Standing(Highcrest, Vector3.Zero), Beside(Highcrest, Vector3.Zero), ref high));
        Assert.Equal(1, areas.Choose(null, Beside(Highcrest, Vector3.Zero), ref nearHigh));
        Assert.Equal(2, areas.Areas.Length);

        // The main view's own, blown up, is still over the pad by its camera (as it always was).
        areas.Clear();
        Assert.Equal(0, areas.Choose(null, Beside(Sentinel, Vector3.Zero), ref main));
        Assert.Equal(SunAreas.Around(null, Beside(Sentinel, Vector3.Zero)), areas.Areas[0]);
    }

    [Fact]
    public void Each_area_is_drawn_from_the_first_view_chosen_for_it_and_one_no_drawn_view_chose_is_skipped()
    {
        static ViewInput View(int area) => new(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, Vector2.Zero, Vector2.One, 0, area, ChaseCamera.Planes, Vector3.Zero, Vector4.Zero);
        ReadOnlySpan<ViewInput> views = [View(0), View(2), View(1), View(2)];
        Assert.Equal(0, VulkanRenderer.SunAreaOwner(0, views));  // the main view's, always
        Assert.Equal(3, VulkanRenderer.SunAreaOwner(1, views));  // small view 2 (uniforms 1 + 2)
        Assert.Equal(2, VulkanRenderer.SunAreaOwner(2, views));  // small view 1, the first of two
        Assert.Equal(-1, VulkanRenderer.SunAreaOwner(3, views)); // chosen by a view not drawn: skipped
        Assert.Equal(0, VulkanRenderer.SunAreaOwner(0, []));
        Assert.Equal(-1, VulkanRenderer.SunAreaOwner(1, []));
    }

    [Fact]
    public void Choosing_every_views_area_allocates_nothing()
    {
        var areas = new SunAreas();
        var held = new bool[1 + Scene.MaxSmallViews];
        var at = LaunchSites.All.Select(site => site.Model).ToArray();
        void Frame(int f)
        {
            areas.Clear();
            for (var v = 0; v < held.Length; v++)
            {
                var model = at[(f / 50 + v * 3) % at.Length] * Matrix4x4.CreateTranslation(MathF.Sin(f + v) * 8f, 0f, MathF.Cos(f * 0.7f) * 8f);
                areas.Choose(v == 4 ? null : model, model.Translation + new Vector3(0f, 10f, 0f), ref held[v]);
            }
        }
        Frame(0);
        var (before, collections) = (GC.GetAllocatedBytesForCurrentThread(), GC.CollectionCount(0));
        for (var f = 0; f < 2000; f++)
            Frame(f);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(collections, GC.CollectionCount(0));
    }
}
