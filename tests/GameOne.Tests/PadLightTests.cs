using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The lights at every launch pad, landing zone and drone ship (<see cref="PadLights"/>): floor
/// lights round each edge, floodlight masts where the ground allows, and lights up each tower.
/// </summary>
public sealed class PadLightTests(ITestOutputHelper output)
{
    private static IEnumerable<LitPlace> OnTheGround => PadLights.All.Where(p => p.Ship is null);

    /// <summary>Every place's lights fully on: for the tests where it matters only that they are lit.</summary>
    private static readonly float[] AllLit = Enumerable.Repeat(1f, PadLights.All.Count).ToArray();

    /// <summary>The index in <see cref="PadLights.All"/> of the place a light at <paramref name="at"/> is one of: the one whose origin is nearest.</summary>
    private static int Owner(Vector3 at) => Enumerable.Range(0, PadLights.All.Count).MinBy(i => Vector3.Distance(at, PadLights.All[i].Origin));

    [Fact]
    public void Every_pad_and_zone_has_its_floor_lights()
    {
        foreach (var place in OnTheGround)
        {
            var floor = place.Lamps.Count(l => l.Kind == LampKind.Floor);
            var edge = place.IsPad ? 8f * Scene.PadHalfSize : MathF.Tau * Scene.LandingZoneRadius;
            output.WriteLine($"{place.Name}: {floor} floor lights, {place.Masts.Length} masts");
            Assert.InRange(floor, edge / Scene.FloorLampSpacing - 4, edge / Scene.FloorLampSpacing + 4);
        }
    }

    [Fact]
    public void Masts_stand_only_where_the_ground_allows()
    {
        foreach (var place in OnTheGround)
        {
            var level = Planet.Altitude(place.ToWorld(Vector3.Zero));
            var reach = place.IsPad ? Scene.PadHalfSize * MathF.Sqrt(2f) : Scene.LandingZoneRadius;
            foreach (var mast in place.Masts)
            {
                var d = Planet.Up(place.ToWorld(mast.Foot));
                Assert.False(Terrain.IsSea(d), $"a mast of {place.Name} stands in the sea");
                Assert.True(MathF.Abs(Terrain.Height(d) - level) <= Scene.MastLevelTolerance + 0.01f, $"a mast of {place.Name} is {Terrain.Height(d) - level:0.0} m off its level");
                Assert.True(Vector3.Dot(Terrain.Normal(d), d) >= MathF.Cos(float.DegreesToRadians(Scene.MastMaxSlopeDegrees)) - 1e-4f, $"a mast of {place.Name} stands on a steep slope");
                Assert.True(new Vector2(mast.Foot.X, mast.Foot.Z).Length() >= reach + 8f, $"a mast of {place.Name} is too near it");
                // On the ground: its foot no higher than the ground under it, nor much lower.
                var under = Terrain.Height(d) - Planet.Altitude(place.ToWorld(mast.Foot));
                Assert.InRange(under, -0.05f, 0.05f);
            }
        }
        Assert.Equal(4, PadLights.Pad(LaunchSites.Named("Desert")).Masts.Length);
        Assert.Contains(OnTheGround, p => p.Masts.Length < 4); // a site whose ground leaves some out
    }

    [Fact]
    public void The_drone_ship_has_four_short_masts_at_its_corners()
    {
        var ships = PadLights.All.Where(p => p.Ship is not null).ToList();
        Assert.Equal(Rafts.Ships.Count, ships.Count);
        foreach (var ship in ships)
        {
            Assert.Equal(4, ship.Masts.Length);
            foreach (var mast in ship.Masts)
            {
                Assert.True(MathF.Abs(mast.Foot.X) <= Scene.DroneShipHalfLength && MathF.Abs(mast.Foot.Z) <= Scene.DroneShipHalfBeam, "off the deck");
                Assert.True(new Vector2(mast.Foot.X, mast.Foot.Z).Length() > Scene.LandingTargetRadius + 5f, "on the target");
                Assert.True(mast.Height < Scene.MastHeight);
            }
            Assert.Contains(ship.Lamps, l => l.Kind == LampKind.Flood);
        }
    }

    [Fact]
    public void The_masts_are_clear_of_the_towers_the_other_places_and_the_test_range()
    {
        // A mast in a tower, on another place's concrete, or on the test range would be a wall
        // where a vessel lands or a light standing in something drawn.
        const float clear = 2f; // metres between the mast's side and anything else's
        foreach (var place in OnTheGround)
            foreach (var mast in place.Masts)
            {
                var foot = place.ToWorld(mast.Foot);
                foreach (var site in LaunchSites.All)
                {
                    var local = site.ToLocal(foot);
                    var tower = local - Scene.TowerOffset;
                    Assert.True(MathF.Max(MathF.Abs(tower.X), MathF.Abs(tower.Z)) > Scene.TowerHalfWidth + Scene.MastHalfWidth + clear || MathF.Abs(local.Y) > 100f,
                        $"a mast of {place.Name} stands in {site.Name}'s tower");
                    Assert.True(MathF.Max(MathF.Abs(local.X), MathF.Abs(local.Z)) > Scene.PadHalfSize + Scene.MastHalfWidth + clear || MathF.Abs(local.Y) > 100f,
                        $"a mast of {place.Name} stands on {site.Name}'s pad");
                }
                foreach (var plinth in Foundations.LandingZonePlinth)
                {
                    var d = foot - plinth.Top;
                    var across = (d - plinth.Up * Vector3.Dot(d, plinth.Up)).Length();
                    Assert.True(across > Scene.LandingZoneRadius + Scene.MastHalfWidth + clear || d.Length() > 200f,
                        $"a mast of {place.Name} stands on the landing zone at {plinth.Centre} (or the test range's pad there)");
                }
            }
    }

    [Fact]
    public void A_zones_masts_stand_in_its_own_frame()
    {
        // Each zone's lit place is in the zone's frame (its plinth's centre and tilt), and its
        // masts on its diagonals, MastOut past its rim, their feet on the ground about its level.
        foreach (var place in OnTheGround.Where(p => !p.IsPad))
        {
            var plinth = Assert.Single(Foundations.LandingZonePlinth, p => Vector3.Distance(p.Centre, place.Origin) < 0.01f);
            Assert.True(MathF.Abs(Quaternion.Dot(plinth.Tilt, place.Frame)) > 0.99999f, $"{place.Name} is turned from its zone");
            foreach (var mast in place.Masts)
            {
                var across = new Vector2(mast.Foot.X, mast.Foot.Z);
                Assert.Equal(Scene.LandingZoneRadius + Scene.MastOut, across.Length(), 1);
                Assert.True(MathF.Abs(MathF.Abs(across.X) - MathF.Abs(across.Y)) < 0.1f, $"a mast of {place.Name} is off its diagonals");
                Assert.InRange(mast.Foot.Y, -Scene.MastLevelTolerance - 0.5f, Scene.MastLevelTolerance + 0.5f);
            }
        }
    }

    [Fact]
    public void Every_flood_aims_at_its_places_tops_centre()
    {
        // Every flood, at a pad, a zone or a drone ship, aims at the middle of its top (its deck's,
        // on a ship), give or take 0.4 m to one side (two to a mast's head).
        Span<PadLight> lights = stackalloc PadLight[PadLights.MaxLights];
        foreach (var place in PadLights.All)
        {
            var top = new Vector3(0f, place.Ship is not null ? Scene.DroneShipFreeboard : Scene.PadTop, 0f);
            foreach (var lamp in place.Lamps.Where(l => l.Kind == LampKind.Flood))
            {
                var miss = Vector3.Cross(top - lamp.Position, lamp.Aim).Length();
                Assert.True(miss < 0.45f, $"a flood of {place.Name} misses its top's centre by {miss:0.00} m");
            }
            // And the light the shaders take for each mast's two, from its head.
            PadLights.Nearest(place.ToWorld(top + new Vector3(0f, 30f, 0f)), AllLit, lights, out var shadowed);
            Assert.Equal(place.Masts.Length, shadowed);
            for (var i = 0; i < shadowed; i++)
            {
                var miss = Vector3.Cross(place.ToWorld(top) - lights[i].Position, lights[i].Aim).Length();
                Assert.True(miss < 0.05f, $"a flood light of {place.Name} misses its top's centre by {miss:0.00} m");
            }
        }
    }

    [Fact]
    public void A_drone_ships_masts_are_solid_where_they_are_drawn()
    {
        // A booster (legs stowed) let fall onto each mast's head from 0.3 m: it comes to rest on
        // the head (the ship's hull carries the masts as its collider's parts), not falling on past it.
        using var physics = new GameOne.Physics.RocketPhysics(GroundBody.Planet);
        var bell = Vehicle.Booster.Stages[0].BellHeight;
        foreach (var place in PadLights.All.Where(p => p.Ship is not null))
            foreach (var mast in place.Masts)
            {
                var head = mast.Foot + new Vector3(0f, mast.Height, 0f);
                var craft = physics.Restore(Vehicle.Booster, 0, 0, place.ToWorld(head + new Vector3(0f, bell + 0.3f, 0f)), place.Frame,
                    Vector3.Zero, Vector3.Zero, legDeploy: 0f, fairingOn: false);
                for (var i = 0; i < 24; i++)
                    physics.Update(1f / 60, []);
                var tip = place.ToLocal(physics.Position(craft)).Y - bell; // the bell's bottom, in the ship's frame
                output.WriteLine($"{place.Name}: the bell's bottom {tip - head.Y:0.00} m over the mast's head");
                Assert.True(tip - head.Y > -0.2f, $"{place.Name}'s mast at {mast.Foot} let it fall {head.Y - tip:0.00} m past its head");
                physics.Remove(craft);
            }
    }

    [Fact]
    public void Which_places_floods_cast_shadows_holds_for_a_few_metres()
    {
        // A camera midway between Home's pad and its zone: the nearest's floods cast shadows, and
        // keep them until the other is nearer by more than Scene.ShadowPlaceHysteresis, so the
        // shadows don't flip from one to the other, frame to frame, as it drifts across the middle.
        var (pad, zone) = (PadLights.All.First(p => p.Name == "Home's pad"), PadLights.All.First(p => p.Name == "Home's landing zone"));
        var way = Vector3.Normalize(zone.Origin - pad.Origin);
        var middle = (pad.Origin + zone.Origin) / 2 + Planet.Up(pad.Origin) * 30f;
        var lights = new PadLight[PadLights.MaxLights];
        var shadowPlace = -1;
        LitPlace Shadowing(float along)
        {
            PadLights.Nearest(middle + way * along, AllLit, lights, ref shadowPlace, out var shadowed);
            Assert.True(shadowed > 0);
            return PadLights.All[shadowPlace];
        }
        var h = Scene.ShadowPlaceHysteresis;
        Assert.Same(pad, Shadowing(-0.25f * h));
        Assert.Same(pad, Shadowing(0.2f * h)); // the zone nearer, by less than the hysteresis
        Assert.Same(pad, Shadowing(-0.1f * h));
        Assert.Same(zone, Shadowing(0.75f * h)); // nearer by 1.5 times it
        Assert.Same(zone, Shadowing(-0.2f * h));
        Assert.Same(pad, Shadowing(-0.75f * h));
        // The first lights are the shadowing place's floods.
        Assert.Same(pad, Shadowing(0.2f * h));
        Assert.True(Vector3.Distance(lights[0].Position, pad.Origin) < Vector3.Distance(lights[0].Position, zone.Origin));
        // With no place held from before, the nearest.
        var none = -1;
        PadLights.Nearest(middle + way * 0.2f * h, AllLit, lights, ref none, out _);
        Assert.Same(zone, PadLights.All[none]);
    }

    [Fact]
    public void Each_tower_is_lit_from_its_foot()
    {
        foreach (var place in OnTheGround.Where(p => p.IsPad))
            Assert.Equal(3, place.Lamps.Count(l => l.Kind == LampKind.Tower && l.Aim.Y > 0.9f));
    }

    [Fact]
    public void The_nearest_lights_are_chosen_without_allocating()
    {
        // Chosen every frame for the shaders: the nearest places' lights, none far from them all.
        Span<PadLight> lights = stackalloc PadLight[PadLights.MaxLights];
        var nearHome = PadLights.Nearest(new Vector3(30f, 40f, 30f), AllLit, lights, out var shadowed);
        Assert.Equal(PadLights.Pad(LaunchSites.Home).Masts.Length, shadowed); // the nearest place's floods, first, cast shadows
        Assert.All(lights[..shadowed].ToArray(), l => Assert.True(l.CosOuter > -1f));
        Assert.InRange(nearHome, 4, PadLights.MaxLights);
        Assert.Contains(lights[..nearHome].ToArray(), l => l.CosOuter > -1f); // a floodlight (a spot) among them

        // Somewhere more than 3 km from every place (of a few directions round the planet, the first that is).
        var far = Enumerable.Range(0, 64).Select(k => Planet.Centre + Vector3.Normalize(new Vector3(MathF.Sin(k * 2.4f), MathF.Cos(k * 1.7f), MathF.Sin(k * 0.9f))) * (Planet.SeaRadius + 500f))
            .First(p => PadLights.All.All(place => Vector3.Distance(place.Origin, p) > 3000f));
        Assert.Equal(0, PadLights.Nearest(far, AllLit, lights));

        PadLights.Nearest(Vector3.Zero, AllLit, lights); // (anything worked out once, first)
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var camera = Planet.Centre + Vector3.Normalize(new Vector3(MathF.Sin(i), MathF.Cos(i * 0.7f), MathF.Sin(i * 1.3f))) * (Planet.SeaRadius + 200f);
            PadLights.Nearest(camera, AllLit, lights);
        }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Views_at_one_place_share_one_light_set()
    {
        // The main view's camera and two small views' vessels, all at Home's pad: one set (the
        // main view's), its floods in the first block, so the small views look as the main one does.
        var sets = new LightSets();
        sets.Clear();
        var home = PadLights.Pad(LaunchSites.Home);
        var (main, a, b) = (-1, -1, -1);
        Assert.Equal(0, sets.Choose(home.ToWorld(new Vector3(30f, 40f, 30f)), AllLit, [], ref main));
        Assert.Equal(0, sets.Choose(home.ToWorld(new Vector3(0f, 20f, 0f)), AllLit, [], ref a));
        Assert.Equal(0, sets.Choose(home.ToWorld(new Vector3(5f, 30f, -5f)), AllLit, [], ref b));
        Assert.Equal(1, sets.Sets.Length);
        var set = sets.Sets[0];
        Assert.Same(home, PadLights.All[set.Place]);
        Assert.Equal(0, set.Block);
        Assert.Equal(home.Masts.Length, set.Shadowed);
        Assert.True(set.Lights.Length > set.Shadowed);
        Assert.Equal(1, sets.Blocks);
    }

    [Fact]
    public void Views_at_different_places_each_have_their_own_set_and_block_in_order()
    {
        // The main view at Home, a small view on Sentinel's drone ship, one on Highcrest's pad and
        // one far from every place: three sets with a block of floods each, the main view's first,
        // and the far view an empty one (no lights, no block: it costs nothing).
        var sets = new LightSets();
        sets.Clear();
        var home = PadLights.Pad(LaunchSites.Home);
        var sentinel = LaunchSites.All.First(s => s.Name == "Sentinel").DroneShips[0];
        var ship = PadLights.All.Where(p => p.Ship is not null).MinBy(p => Vector3.Distance(p.Origin, sentinel))!;
        var highcrest = PadLights.Pad(LaunchSites.All.First(s => s.Name == "Highcrest"));
        var far = Enumerable.Range(0, 64).Select(k => Planet.Centre + Vector3.Normalize(new Vector3(MathF.Sin(k * 2.4f), MathF.Cos(k * 1.7f), MathF.Sin(k * 0.9f))) * (Planet.SeaRadius + 500f))
            .First(p => PadLights.All.All(place => Vector3.Distance(place.Origin, p) > 3000f));

        var places = new int[5];
        Array.Fill(places, -1);
        Assert.Equal(0, sets.Choose(home.ToWorld(new Vector3(30f, 40f, 30f)), AllLit, [], ref places[0]));
        Assert.Equal(1, sets.Choose(ship.ToWorld(new Vector3(0f, 20f, 0f)), AllLit, [], ref places[1]));
        Assert.Equal(2, sets.Choose(highcrest.ToWorld(new Vector3(0f, 20f, 0f)), AllLit, [], ref places[2]));
        Assert.Equal(3, sets.Choose(far, AllLit, [], ref places[3]));
        Assert.Equal(0, sets.Choose(home.ToWorld(new Vector3(-10f, 25f, 0f)), AllLit, [], ref places[4])); // back at Home: the main view's

        Assert.Equal(4, sets.Sets.Length);
        LitPlace[] expected = [home, ship, highcrest];
        for (var i = 0; i < 3; i++)
        {
            var set = sets.Sets[i];
            Assert.Same(expected[i], PadLights.All[set.Place]);
            Assert.Equal(i, set.Block);
            Assert.Equal(expected[i].Masts.Length, set.Shadowed);
            // Its floods are its own place's, first.
            var origin = expected[i].Origin;
            Assert.All(set.Lights.Span[..set.Shadowed].ToArray(), l => Assert.True(Vector3.Distance(l.Position, origin) < 100f));
        }
        Assert.Equal((-1, 0, 0, -1), (sets.Sets[3].Place, sets.Sets[3].Lights.Length, sets.Sets[3].Shadowed, sets.Sets[3].Block));
        Assert.Equal(3, sets.Blocks);

        // Next frame, chosen afresh: from the start.
        sets.Clear();
        Assert.Equal(0, sets.Sets.Length);
        Assert.Equal(0, sets.Choose(ship.ToWorld(new Vector3(0f, 20f, 0f)), AllLit, [], ref places[1])); // (the main view now at the ship)
        Assert.Equal(0, sets.Sets[0].Block);
    }

    [Fact]
    public void A_view_crossing_between_two_places_keeps_its_set_within_the_hysteresis()
    {
        // The main view at Home's pad; a small view's vessel midway between the pad and the zone,
        // drifting across the middle: it shares the main view's set (the pad's) until the zone is
        // nearer by more than Scene.ShadowPlaceHysteresis, then has the zone's set of its own, and
        // keeps it until the pad is as much nearer again.
        var (pad, zone) = (PadLights.All.First(p => p.Name == "Home's pad"), PadLights.All.First(p => p.Name == "Home's landing zone"));
        var way = Vector3.Normalize(zone.Origin - pad.Origin);
        var middle = (pad.Origin + zone.Origin) / 2 + Planet.Up(pad.Origin) * 30f;
        var sets = new LightSets();
        var (main, view) = (-1, -1);
        LitPlace Lit(float along)
        {
            sets.Clear();
            sets.Choose(pad.ToWorld(new Vector3(30f, 40f, 30f)), AllLit, [], ref main);
            var chosen = sets.Choose(middle + way * along, AllLit, [], ref view);
            var set = sets.Sets[chosen];
            Assert.True(set.Shadowed > 0 && set.Block >= 0);
            return PadLights.All[set.Place];
        }
        var h = Scene.ShadowPlaceHysteresis;
        Assert.Same(pad, Lit(-0.25f * h));
        Assert.Same(pad, Lit(0.2f * h)); // the zone nearer, by less than the hysteresis
        Assert.Equal(1, sets.Sets.Length); // shared with the main view
        Assert.Same(zone, Lit(0.75f * h));
        Assert.Equal((2, 1), (sets.Sets.Length, sets.Sets[1].Block));
        Assert.Same(zone, Lit(-0.2f * h));
        Assert.Same(pad, Lit(-0.75f * h));
        Assert.Equal(1, sets.Sets.Length);
    }

    [Fact]
    public void Choosing_every_views_lights_allocates_nothing()
    {
        // Every frame, for the main view and each small one, over many frames as they move about
        // among the places.
        var sets = new LightSets();
        var places = new int[1 + Scene.MaxSmallViews];
        Array.Fill(places, -1);
        var at = PadLights.All.Select(p => p.ToWorld(new Vector3(0f, 25f, 0f))).ToArray();
        void Frame(int f)
        {
            sets.Clear();
            for (var v = 0; v < places.Length; v++)
                sets.Choose(at[(f / 50 + v * 3) % at.Length] + new Vector3(MathF.Sin(f + v), 0f, MathF.Cos(f * 0.7f)) * 8f, AllLit, [], ref places[v]);
        }
        Frame(0); // (anything worked out once, first)
        var (before, collections) = (GC.GetAllocatedBytesForCurrentThread(), GC.CollectionCount(0));
        for (var f = 0; f < 2000; f++)
            Frame(f);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(collections, GC.CollectionCount(0));
    }

    /// <summary>A lit vessel's light at <paramref name="at"/>, shining straight down there, as far on as <paramref name="lit"/>.</summary>
    private static VesselLight Lit(Vector3 at, float lit = 1f) => new(at, -Planet.Up(at), lit);

    /// <summary>The light a vessel's <paramref name="vessel"/> hands into a set: at its bell, down its axis, its colour by its fraction.</summary>
    private static void AssertIsVessels(VesselLight vessel, PadLight light)
    {
        Assert.Equal(vessel.At, light.Position);
        Assert.Equal(vessel.Down, light.Aim);
        Assert.Equal(Scene.LandingLightRange, light.Range);
        Assert.True(Vector3.Distance(Scene.LandingLightColour * Scene.LandingLightBrightness * vessel.Lit, light.Colour) < 1e-5f, $"{light.Colour}");
        Assert.Equal(MathF.Cos(float.DegreesToRadians(Scene.LandingLightConeDegrees)), light.CosOuter, 5);
        Assert.Equal(MathF.Cos(float.DegreesToRadians(Scene.LandingLightCoreDegrees)), light.CosInner, 5);
    }

    /// <summary>The set <paramref name="sets"/> chooses for a view at <paramref name="at"/> among <paramref name="vessels"/>.</summary>
    private static LightSet Chosen(LightSets sets, Vector3 at, ReadOnlySpan<VesselLight> vessels, ref int place)
    {
        var index = sets.Choose(at, AllLit, vessels, ref place);
        return sets.Sets[index];
    }

    [Fact]
    public void A_view_gets_the_nearest_four_lit_vessels_after_the_pad_lights()
    {
        // At Home's pad, six lit vessels 20 to 700 m off (given out of order): the set's pad lights,
        // then the four nearest vessels' (nearest first); the 100 m and 700 m ones are left out.
        var home = PadLights.Pad(LaunchSites.Home);
        var camera = home.ToWorld(new Vector3(0f, 30f, 0f));
        var vessels = new[] { 100f, 40f, 700f, 20f, 80f, 60f }.Select(d => Lit(home.ToWorld(new Vector3(d, 30f, 0f)), 0.5f + d / 2000f)).ToArray();
        var sets = new LightSets();
        sets.Clear();
        var place = -1;
        var set = Chosen(sets, camera, vessels, ref place);
        Assert.Same(home, PadLights.All[set.Place]);
        var lights = set.Lights.Span;
        Assert.True(lights.Length > Scene.LandingLightsLit);
        var tail = lights[^Scene.LandingLightsLit..];
        float[] nearest = [20f, 40f, 60f, 80f];
        for (var i = 0; i < nearest.Length; i++)
            AssertIsVessels(vessels.Single(v => MathF.Abs(Vector3.Distance(v.At, camera) - nearest[i]) < 0.5f), tail[i]);
        foreach (var far in vessels.Where(v => Vector3.Distance(v.At, camera) > 90f))
            Assert.DoesNotContain(lights.ToArray(), l => l.Position == far.At);
        // The pad lights before them are as Nearest hands them in, its floods first.
        Assert.Equal(home.Masts.Length, set.Shadowed);
    }

    [Fact]
    public void Among_three_lit_places_the_vessels_still_have_room()
    {
        // A camera within reach of three lit places (a midway point among them: Home's pad, its zone
        // and the drone ship off its coast), and near four lit vessels: all four in its set, after
        // the places' lights, as many as Nearest hands in but at most the room kept for them.
        var places = PadLights.All;
        var camera = places.SelectMany(a => places.Select(b => (a.Origin + b.Origin) / 2f)).Select(p => p + Planet.Up(p) * 30f)
            .First(p => places.Count(q => Vector3.Distance(q.Origin, p) <= Scene.LightReach) >= 3);
        var uncapped = PadLights.Nearest(camera, AllLit, new PadLight[PadLights.MaxLights]);
        output.WriteLine($"{uncapped} lights from the places, uncapped");
        var east = Planet.East(camera);
        var vessels = Enumerable.Range(1, 4).Select(k => Lit(camera + east * (25f * k))).ToArray();
        var sets = new LightSets();
        sets.Clear();
        var place = -1;
        var set = Chosen(sets, camera, vessels, ref place);
        var lights = set.Lights.Span;
        Assert.Equal(Math.Min(uncapped, PadLights.MaxLights - Scene.LandingLightsLit), lights.Length - Scene.LandingLightsLit);
        for (var i = 0; i < vessels.Length; i++)
            AssertIsVessels(vessels[i], lights[lights.Length - Scene.LandingLightsLit + i]);
    }

    [Fact]
    public void A_vessel_at_zero_lights_nothing()
    {
        // Two vessels near the camera, the nearer with its lights off: only the other is chosen.
        var home = PadLights.Pad(LaunchSites.Home);
        var camera = home.ToWorld(new Vector3(0f, 30f, 0f));
        VesselLight[] vessels = [Lit(home.ToWorld(new Vector3(10f, 30f, 0f)), 0f), Lit(home.ToWorld(new Vector3(50f, 30f, 0f)))];
        var sets = new LightSets();
        sets.Clear();
        var place = -1;
        var withBoth = Chosen(sets, camera, vessels, ref place).Lights.ToArray(); // (copied: the next choice reuses its room)
        sets.Clear();
        var withNone = Chosen(sets, camera, [], ref place).Lights.ToArray();
        Assert.Equal(withNone.Length + 1, withBoth.Length);
        AssertIsVessels(vessels[1], withBoth[^1]);
        Assert.DoesNotContain(withBoth, l => l.Position == vessels[0].At);
    }

    [Fact]
    public void A_shared_set_carries_the_first_views_vessel_lights()
    {
        // Two views at Home's pad, 240 m apart either side of it, each with four lit vessels near
        // it: one place, so the second shares the first's set, and its vessel lights are the four
        // nearest the first view (the second's own, within reach of the first, left out).
        var home = PadLights.Pad(LaunchSites.Home);
        var (a, b) = (home.ToWorld(new Vector3(-120f, 30f, 0f)), home.ToWorld(new Vector3(120f, 30f, 0f)));
        VesselLight[] Near(float x) => [.. Enumerable.Range(1, 4).Select(k => Lit(home.ToWorld(new Vector3(x, 30f, 15f * k))))];
        var (nearA, nearB) = (Near(-120f), Near(120f));
        VesselLight[] vessels = [.. nearB, .. nearA];
        var sets = new LightSets();
        sets.Clear();
        var (pa, pb) = (-1, -1);
        var (ia, ib) = (sets.Choose(a, AllLit, vessels, ref pa), sets.Choose(b, AllLit, vessels, ref pb));
        Assert.Same(home, PadLights.All[pa]);
        Assert.Equal(pa, pb);
        Assert.Equal(ia, ib);
        Assert.Single(sets.Sets.ToArray());
        var lights = sets.Sets[ia].Lights.ToArray();
        var tail = lights[^Scene.LandingLightsLit..];
        for (var i = 0; i < nearA.Length; i++)
            AssertIsVessels(nearA[i], tail[i]);
        foreach (var other in nearB)
            Assert.DoesNotContain(lights, l => l.Position == other.At);
    }

    [Fact]
    public void Two_views_near_no_place_far_apart_dont_share()
    {
        // Two views 5 km apart, both beyond every place's reach, each near a lit vessel of its own:
        // two sets (not one shared because both are at no place), each with its own vessel's light.
        Vector3 Far(int k) => Planet.Centre + Vector3.Normalize(new Vector3(MathF.Sin(k * 2.4f), MathF.Cos(k * 1.7f), MathF.Sin(k * 0.9f))) * (Planet.SeaRadius + 500f);
        static bool Unlit(Vector3 p) => PadLights.All.All(place => Vector3.Distance(place.Origin, p) > Scene.LightReach);
        var (a, b) = Enumerable.Range(0, 64).Select(Far).Select(p => (p, p + Planet.East(p) * 5000f)).First(ab => Unlit(ab.Item1) && Unlit(ab.Item2));
        VesselLight[] vessels = [Lit(a + Planet.North(a) * 30f), Lit(b + Planet.North(b) * 30f)];
        var sets = new LightSets();
        sets.Clear();
        var (pa, pb) = (-1, -1);
        var (ia, ib) = (sets.Choose(a, AllLit, vessels, ref pa), sets.Choose(b, AllLit, vessels, ref pb));
        Assert.Equal((-1, -1), (pa, pb));
        Assert.NotEqual(ia, ib);
        var (la, lb) = (sets.Sets[ia].Lights.ToArray(), sets.Sets[ib].Lights.ToArray());
        Assert.Equal((1, 1), (la.Length, lb.Length));
        AssertIsVessels(vessels[0], la[0]);
        AssertIsVessels(vessels[1], lb[0]);
        Assert.Equal((-1, -1), (sets.Sets[ia].Block, sets.Sets[ib].Block)); // no floods: no shadows
    }

    [Fact]
    public void Choosing_with_vessels_allocates_nothing()
    {
        // Every frame, for the main view and each small one, among a thousand lit vessels scattered
        // round the places.
        var sets = new LightSets();
        var places = new int[1 + Scene.MaxSmallViews];
        Array.Fill(places, -1);
        var at = PadLights.All.Select(p => p.ToWorld(new Vector3(0f, 25f, 0f))).ToArray();
        var vessels = Enumerable.Range(0, 1000).Select(i => Lit(at[i % at.Length] + new Vector3(MathF.Sin(i), MathF.Cos(i * 0.3f), MathF.Sin(i * 0.7f)) * (i % 600), (i % 5) / 4f)).ToArray();
        void Frame(int f)
        {
            sets.Clear();
            for (var v = 0; v < places.Length; v++)
                sets.Choose(at[(f / 50 + v * 3) % at.Length] + new Vector3(MathF.Sin(f + v), 0f, MathF.Cos(f * 0.7f)) * 8f, AllLit, vessels, ref places[v]);
        }
        Frame(0); // (anything worked out once, first)
        var (before, collections) = (GC.GetAllocatedBytesForCurrentThread(), GC.CollectionCount(0));
        for (var f = 0; f < 2000; f++)
            Frame(f);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(collections, GC.CollectionCount(0));
    }

    [Fact]
    public void A_place_lights_scale_by_its_fraction()
    {
        // Home's pad half on: each of its lights half as bright as fully on, its neighbours' unchanged.
        var home = PadLights.All.ToList().IndexOf(PadLights.Pad(LaunchSites.Home));
        var camera = LaunchSites.Home.ToWorld(new Vector3(0f, 30f, 0f));
        var (full, half) = (new PadLight[PadLights.MaxLights], new PadLight[PadLights.MaxLights]);
        var lit = (float[])AllLit.Clone();
        var n = PadLights.Nearest(camera, lit, full);
        lit[home] = 0.5f;
        Assert.Equal(n, PadLights.Nearest(camera, lit, half));
        var homes = 0;
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(full[i].Position, half[i].Position);
            var scale = Owner(full[i].Position) == home ? 0.5f : 1f;
            homes += scale < 1f ? 1 : 0;
            Assert.True(Vector3.Distance(full[i].Colour * scale, half[i].Colour) < 1e-5f, $"light {i}: {half[i].Colour} against {full[i].Colour}");
        }
        Assert.True(homes > 0);
    }

    [Fact]
    public void A_place_that_is_off_hands_in_no_lights_and_shadows_nothing()
    {
        // Every place off (as by day): nothing lit at Home, and no place's floods cast shadows, though
        // Home's pad held them before it went off.
        var lit = new float[PadLights.All.Count];
        var lights = new PadLight[PadLights.MaxLights];
        var shadowPlace = PadLights.All.ToList().IndexOf(PadLights.Pad(LaunchSites.Home));
        Assert.Equal(0, PadLights.Nearest(LaunchSites.Home.ToWorld(new Vector3(0f, 30f, 0f)), lit, lights, ref shadowPlace, out var shadowed));
        Assert.Equal(-1, shadowPlace);
        Assert.Equal(0, shadowed);
    }

    [Fact]
    public void The_shadows_move_on_as_the_shadowing_place_goes_off()
    {
        // Between Home's pad and zone, the pad holding the shadows with the zone nearer (within the
        // hysteresis); the pad off, the zone takes them in the same frame, and none of the pad's lights is left.
        var (pad, zone) = (PadLights.All.First(p => p.Name == "Home's pad"), PadLights.All.First(p => p.Name == "Home's landing zone"));
        var (padIndex, zoneIndex) = (PadLights.All.ToList().IndexOf(pad), PadLights.All.ToList().IndexOf(zone));
        var way = Vector3.Normalize(zone.Origin - pad.Origin);
        var middle = (pad.Origin + zone.Origin) / 2 + Planet.Up(pad.Origin) * 30f;
        var lights = new PadLight[PadLights.MaxLights];
        var lit = (float[])AllLit.Clone();
        var h = Scene.ShadowPlaceHysteresis;
        var shadowPlace = -1;
        PadLights.Nearest(middle - way * 0.25f * h, lit, lights, ref shadowPlace, out _);
        PadLights.Nearest(middle + way * 0.2f * h, lit, lights, ref shadowPlace, out _);
        Assert.Equal(padIndex, shadowPlace);
        lit[padIndex] = 0f;
        var n = PadLights.Nearest(middle + way * 0.2f * h, lit, lights, ref shadowPlace, out var shadowed);
        Assert.Equal(zoneIndex, shadowPlace);
        Assert.Equal(zone.Masts.Length, shadowed);
        Assert.True(n > 0);
        for (var i = 0; i < n; i++)
            Assert.NotEqual(padIndex, Owner(lights[i].Position));
    }

    private static readonly Pilot Nobody = (_, _) => default;

    [Fact]
    public void A_vessel_flown_into_a_mast_is_wrecked()
    {
        // Flown into a mast's side at 10 m/s, its legs stowed, from 8 m off, 6 m up.
        var place = PadLights.Pad(LaunchSites.Named("Desert"));
        var mast = place.Masts[0];
        var towards = Vector3.Normalize(new Vector3(-mast.Foot.X, 0f, -mast.Foot.Z)); // from the mast towards the pad
        var start = place.ToWorld(mast.Foot + new Vector3(0f, 6f, 0f) + towards * 8f);
        var state = Drops.Placed(start, Planet.East(start), 0f, legsOut: false) with
        {
            Position = start,
            Rotation = place.Frame,
            Velocity = Vector3.Transform(-towards * 10f, place.Frame),
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        sim.Run(Nobody, _ => vessel.State == FlightState.Crashed, maxSeconds: 2);
        output.WriteLine($"{vessel.State} {vessel.CrashReason}");
        Assert.Equal("Hit a light mast.", vessel.CrashReason);
    }

    [Fact]
    public void Touching_a_mast_gently_wrecks_the_vessel()
    {
        // Upright, its legs stowed, 6 m up and 3 cm from the side of a mast facing the pad, moving
        // into it at 0.3 m/s, so it has hardly begun to fall as it touches it (Jolt begins a contact
        // a couple of centimetres off): a mast wrecks on any touch, however gentle.
        var place = PadLights.Pad(LaunchSites.Named("Desert"));
        var mast = place.Masts[0];
        // The mast's side towards the pad, square on (the mast stands along the place's axes).
        var side = MathF.Abs(mast.Foot.X) > MathF.Abs(mast.Foot.Z) ? new Vector3(-MathF.Sign(mast.Foot.X), 0f, 0f) : new Vector3(0f, 0f, -MathF.Sign(mast.Foot.Z));
        var radius = Vehicle.Booster.Stages[0].Radius;
        var start = place.ToWorld(mast.Foot + new Vector3(0f, 6f, 0f) + side * (Scene.MastHalfWidth + radius + 0.03f));
        var state = Drops.Placed(start, Planet.East(start), 0f, legsOut: false) with
        {
            Position = start,
            Rotation = place.Frame,
            Velocity = Vector3.Transform(-side * 0.3f, place.Frame),
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        var touched = 0f;
        sim.Run(Nobody, _ =>
        {
            foreach (var touch in sim.Physics.TouchesBegun(vessel.Craft))
                if (touch.With == SolidKind.Mast)
                    touched = MathF.Max(touched, touch.ClosingSpeed);
            return vessel.State == FlightState.Crashed;
        }, maxSeconds: 1.5);
        output.WriteLine($"{vessel.State} {vessel.CrashReason}, touching the mast at {touched:0.00} m/s");
        Assert.Equal("Hit a light mast.", vessel.CrashReason);
        Assert.InRange(touched, 0.1f, 0.7f);
    }
}
