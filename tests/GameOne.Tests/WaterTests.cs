using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The sea as water (see <see cref="RocketPhysics"/>): things float in it, bob and drift with its
/// waves (<see cref="Sea"/>), and sink into it rather than standing on it; the legs never stand on
/// it; and something floating far from the player freezes, and floats again when they come.
/// </summary>
/// <remarks>
/// These drive the physics alone, with the player's place handed in as <see cref="Flight"/> hands
/// in the controlled vessel's: a booster that lies down in the water would be judged by the
/// flight's rules, which are splashdown's to settle, not the water's.
/// </remarks>
public sealed class WaterTests(ITestOutputHelper output)
{
    private const float Frame = 1f / 60;

    /// <summary>A spot in the Sunrise Sea, east of the pad on the equator, with the sea more than 100 m deep all round it.</summary>
    internal static readonly Lazy<Vector3> DeepWater = new(() =>
    {
        var sunrise = (byte)(Terrain.Map.Seas.Select((s, i) => (s, i)).Single(x => x.s.Name == "Sunrise Sea").i + 1);
        for (var metres = 1000f; metres < 8000f; metres += 50f)
        {
            var d = CubeSphere.FromLatLon(0f, metres / Scene.PlanetRadius);
            var (face, u, v) = CubeSphere.ToFace(d);
            if (Terrain.Map.RegionAt(face, u, v) != sunrise)
                continue;
            var deep = true;
            for (var k = 0; k < 16 && deep; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 16);
                var p = Planet.Centre + d * Planet.Radius + (Planet.East(Planet.Centre + d * Planet.Radius) * c + Planet.North(Planet.Centre + d * Planet.Radius) * s) * 80f;
                deep = Terrain.Height(Planet.Up(p)) < -100f;
            }
            if (deep && Terrain.Height(d) < -100f)
                return d;
        }
        throw new InvalidOperationException("no deep water in the Sunrise Sea east of the pad");
    });

    private static float SunriseWaves => Terrain.Map.Seas.Single(s => s.Name == "Sunrise Sea").Waves;

    /// <summary>The point on the calm sea in <paramref name="direction"/>.</summary>
    private static Vector3 OnTheSea(Vector3 direction) => Planet.Centre + direction * Planet.SeaRadius;

    /// <summary>
    /// A booster set on the sea at <paramref name="direction"/> as <see cref="Drops.At"/> sets one
    /// down: upright, with its legs out and its engine off, still, its lowest foot (hanging at full
    /// stroke) on the calm sea.
    /// </summary>
    private static Craft SetOnTheSea(RocketPhysics physics, Vector3 direction)
    {
        var at = OnTheSea(direction);
        var up = Planet.Up(at);
        var upright = Planet.UprightAt(at);
        var position = at + up * 30f;
        var model = Matrix4x4.CreateFromQuaternion(upright) * Matrix4x4.CreateTranslation(position);
        var lowest = Enumerable.Range(0, RocketGeometry.LegCount)
            .Min(leg => Planet.SurfaceAltitude(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg), model)));
        position -= up * lowest;
        return physics.Restore(Vehicle.Booster, 0, 0, position, upright, Vector3.Zero, Vector3.Zero, legDeploy: 1f, fairingOn: false);
    }

    /// <summary>The lowest point down <paramref name="craft"/>'s axis (from its bell's bottom to its top), above sea level.</summary>
    private static float Lowest(RocketPhysics physics, Craft craft)
    {
        var model = physics.Model(craft);
        var bell = craft.Vehicle.Stages[craft.First].BellHeight;
        var height = RocketGeometry.StackHeight(craft.Vehicle, craft.First, craft.Last);
        return Math.Min(
            Planet.Altitude(Vector3.Transform(new Vector3(0, -bell, 0), model)),
            Planet.Altitude(Vector3.Transform(new Vector3(0, height, 0), model)));
    }

    private static float AboveTheSea(RocketPhysics physics, Craft craft) => Planet.Altitude(physics.CentreOfMass(craft));

    [Fact]
    public void A_craft_wholly_under_the_water_is_pushed_back_up()
    {
        // Upright, its top 5 m under the calm sea: every part of it is in the water.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var at = OnTheSea(DeepWater.Value);
        var up = Planet.Up(at);
        var height = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var craft = physics.Restore(Vehicle.Booster, 0, 0, at - up * (height + 5f), Planet.UprightAt(at), Vector3.Zero, Vector3.Zero, legDeploy: 1f, fairingOn: false);
        for (var t = 0f; t < 0.1f; t += Frame)
            physics.Update(Frame, [], physics.Position(craft));
        var rising = Vector3.Dot(physics.Velocity(craft), up);
        output.WriteLine($"submerged {craft.Submerged:0.000}; rising at {rising:0.00} m/s after 0.1 s");
        Assert.Equal(1f, craft.Submerged, 1e-4f);
        // Pushed up by WaterBuoyancy g against 1 g of gravity, less the water's drag.
        Assert.True(rising > 1.5f, $"rising at only {rising:0.00} m/s");
    }

    /// <summary>
    /// Long enough for a booster set on the sea to have sunk in and tipped over onto its side (it
    /// floats lying down): how it rides the waves is watched from then on.
    /// </summary>
    private const float Settling = 40f;

    [Fact]
    public void A_booster_set_on_the_sea_floats_and_bobs_with_the_waves()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        var craft = SetOnTheSea(physics, DeepWater.Value);
        float furthest = 0f, deepest = float.MaxValue, low = float.MaxValue, high = float.MinValue, tipping = 0f;
        for (var t = 0f; t < Settling + 60f; t += Frame)
        {
            physics.Update(Frame, [], physics.Position(craft));
            deepest = MathF.Min(deepest, Lowest(physics, craft)); // from the start: the plunge too
            if (t < Settling)
            {
                tipping = MathF.Max(tipping, physics.AngularVelocity(craft).Length());
                continue;
            }
            var above = AboveTheSea(physics, craft);
            furthest = MathF.Max(furthest, MathF.Abs(above));
            (low, high) = (MathF.Min(low, above), MathF.Max(high, above));
        }
        var tilt = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(physics.Up(craft), Planet.Up(physics.Position(craft))), -1f, 1f)));
        output.WriteLine($"its lowest point went at most {-deepest:0.00} m down; settling, it turned at most {tipping:0.00} rad/s; " +
                         $"then over 60 s its centre of mass rode {low:+0.00;-0.00} to {high:+0.00;-0.00} m about sea level (moves {high - low:0.00} m; " +
                         $"the sea's waves are {SunriseWaves} m); it ends leaning {tilt:0}°, {craft.Submerged:0.00} submerged");
        Assert.False(craft.Removed || craft.Frozen);
        Assert.True(furthest < 10f, $"it got {furthest:0.0} m from the surface");
        Assert.True(deepest > -20f, $"it sank {-deepest:0.0} m");
        Assert.True(high - low >= 0.3f * SunriseWaves, $"it only moved {high - low:0.00} m with the waves");
    }

    [Fact]
    public void A_floating_booster_is_carried_about_by_the_waves()
    {
        // How far the waves carry it about along the water (its centre of mass's path across the
        // ground, the bobbing left out), and where that leaves it. The waves' flow only goes to
        // and fro, so it ends about where it was: a drift (a current, or wind) is still to come,
        // and would be measured by where it ends, not by this path.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var craft = SetOnTheSea(physics, DeepWater.Value);
        for (var t = 0f; t < Settling; t += Frame)
            physics.Update(Frame, [], physics.Position(craft));
        var from = physics.CentreOfMass(craft);
        var (last, path) = (from, 0f);
        for (var t = 0f; t < 120f; t += Frame)
        {
            physics.Update(Frame, [], physics.Position(craft));
            var now = physics.CentreOfMass(craft);
            path += Planet.Radius * Planet.AngleBetween(last, now);
            last = now;
        }
        var away = Planet.Radius * Planet.AngleBetween(from, last);
        output.WriteLine($"over 120 s the waves carry it {path:0.00} m along the water, leaving it {away:0.00} m from where it was");
        Assert.InRange(path, 1f, 50f);
        Assert.True(away < 50f);
    }

    /// <summary>A spot in the Grey Sea as near 45° north as it is more than 100 m deep all round: where the ground's turn pulls hardest towards the equator.</summary>
    private static readonly Lazy<Vector3> MidLatitudeWater = new(() =>
    {
        var grey = (byte)(Terrain.Map.Seas.Select((s, i) => (s, i)).Single(x => x.s.Name == "Grey Sea").i + 1);
        Vector3? best = null;
        var bestOff = float.MaxValue;
        for (var latitude = 36f; latitude <= 54f; latitude += 0.5f)
            for (var longitude = 80f; longitude <= 110f; longitude += 0.5f)
            {
                var d = CubeSphere.FromLatLon(float.DegreesToRadians(latitude), float.DegreesToRadians(longitude));
                var (face, u, v) = CubeSphere.ToFace(d);
                if (MathF.Abs(latitude - 45f) >= bestOff || Terrain.Map.RegionAt(face, u, v) != grey || Terrain.Height(d) >= -100f)
                    continue;
                var at = Planet.Centre + d * Planet.Radius;
                var deep = true;
                for (var k = 0; k < 16 && deep; k++)
                {
                    var (s, c) = MathF.SinCos(MathF.Tau * k / 16);
                    deep = Terrain.Height(Planet.Up(at + (Planet.East(at) * c + Planet.North(at) * s) * 80f)) < -100f;
                }
                if (deep)
                    (best, bestOff) = (d, MathF.Abs(latitude - 45f));
            }
        return best ?? throw new InvalidOperationException("no deep water in the Grey Sea");
    });

    [Fact]
    public void A_booster_floating_off_the_equator_stays_where_it_is()
    {
        // The ground's turn pulls outward from its axis, which off the equator has a part along the
        // sea towards the equator (about 0.16 m/s² at 45°). The water must hold it against that as
        // against gravity, or it drifts towards the equator by metres a minute, with the player by.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var d = MidLatitudeWater.Value;
        var craft = SetOnTheSea(physics, d);
        for (var t = 0f; t < Settling; t += Frame)
            physics.Update(Frame, [], physics.Position(craft));
        var from = physics.CentreOfMass(craft);
        for (var t = 0f; t < 120f; t += Frame)
            physics.Update(Frame, [], physics.Position(craft));
        var to = physics.CentreOfMass(craft);
        var moved = to - from;
        var across = moved - Planet.Up(from) * Vector3.Dot(moved, Planet.Up(from));
        var latitude = float.RadiansToDegrees(CubeSphere.LatLon(Planet.Up(from)).Latitude);
        output.WriteLine($"at {latitude:0.0}°N over 120 s it moved {across.Length():0.00} m across the sea " +
                         $"({Vector3.Dot(across, Planet.North(from)):+0.00;-0.00} m north, {Vector3.Dot(across, Planet.East(from)):+0.00;-0.00} m east)");
        Assert.False(craft.Frozen);
        // The drift this guards against was 12.8 m over the two minutes; the waves' to and fro
        // alone leaves it up to about 0.7 m off, wherever in their swing the two minutes end, so
        // 2 m is clear of that and still far under the drift.
        Assert.True(across.Length() < 2f, $"it drifted {across.Length():0.0} m");
    }

    [Fact]
    public void The_legs_never_stand_on_the_water()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        var craft = SetOnTheSea(physics, DeepWater.Value);
        for (var t = 0f; t < Settling; t += Frame) // sinking in upright, then lying on its side
        {
            physics.Update(Frame, [], physics.Position(craft));
            if (craft.Submerged > 0f)
                Assert.Equal(Vector4.One, craft.LegExtension);
        }
        Assert.True(craft.Submerged > 0f, "it isn't in the water");
    }

    [Fact]
    public void The_sea_is_not_solid()
    {
        // A loose box (a fairing half) dropped flat from 5 m into deep water: it goes in, and
        // only the water holds it up.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var d = DeepWater.Value;
        var at = OnTheSea(d);
        var up = Planet.Up(at);
        var east = Planet.East(at);
        var x = Vector3.Cross(east, up);
        var lying = Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, east.X, east.Y, east.Z, 0f, up.X, up.Y, up.Z, 0f, 0f, 0f, 0f, 1f));
        var box = physics.AddLoose(Vehicle.FairingHalf, at + up * 5f, lying, Vector3.Zero, Vector3.Zero);
        var deepest = float.MaxValue;
        for (var t = 0f; t < 20f; t += Frame)
        {
            physics.Update(Frame, [], physics.Position(box));
            deepest = MathF.Min(deepest, AboveTheSea(physics, box));
        }
        var end = AboveTheSea(physics, box);
        output.WriteLine($"it went {-deepest:0.00} m under, and after 20 s rides {end:+0.00;-0.00} m about sea level, {box.Submerged:0.00} submerged (the seabed is {-Terrain.Height(d):0} m down)");
        Assert.True(deepest < -1f, $"it only went {-deepest:0.00} m under: the sea held it up as if solid");
        Assert.True(MathF.Abs(end) < 2f, $"it is {end:0.0} m from sea level: the water isn't holding it up");
        Assert.InRange(box.Submerged, 0.01f, 0.99f);
    }

    [Fact]
    public void A_floating_craft_far_from_the_player_freezes_and_floats_again_when_they_come()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        var d = DeepWater.Value;
        var craft = SetOnTheSea(physics, d);
        var at = OnTheSea(d);
        var (east, up) = (Planet.East(at), Planet.Up(at));
        var far = at + east * 2500f;
        var froze = float.NaN;
        for (var t = 0f; t < 10f && float.IsNaN(froze); t += Frame)
        {
            physics.Update(Frame, [], far);
            if (craft.Frozen)
                froze = t;
        }
        output.WriteLine($"with the player 2.5 km away, it froze after {froze:0.00} s, {craft.Submerged:0.00} submerged");
        Assert.True(craft.Frozen, "it didn't freeze");
        Assert.True(craft.Submerged > 0f);

        // The player comes within 900 m: within 2 s it is awake, and moving with the water.
        var near = at + east * 900f;
        var woke = float.NaN;
        var (low, high) = (float.MaxValue, float.MinValue);
        for (var t = 0f; t < 2f; t += Frame)
        {
            physics.Update(Frame, [], near);
            if (craft.Frozen || !physics.IsActive(craft))
                continue;
            if (float.IsNaN(woke))
                woke = t;
            var h = Vector3.Dot(physics.CentreOfMass(craft) - at, up);
            (low, high) = (MathF.Min(low, h), MathF.Max(high, h));
        }
        output.WriteLine($"with the player 900 m away, it woke after {woke:0.00} s, and moved {high - low:0.00} m up and down by 2 s");
        Assert.False(float.IsNaN(woke), "it didn't wake");
        Assert.True(high - low > 0.02f, "it lies still on the water");
    }

    [Fact]
    public void A_floating_craft_with_the_player_near_the_edge_stays_as_it_is()
    {
        // Between the distances it wakes within and freezes beyond: awake, it stays awake; frozen,
        // it stays frozen.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var d = DeepWater.Value;
        var craft = SetOnTheSea(physics, d);
        var at = OnTheSea(d);
        var east = Planet.East(at);
        var edge = at + east * ((Scene.FloatWakeDistance + Scene.FloatFreezeDistance) / 2);
        var changes = 0;
        for (var t = 0f; t < 10f; t += Frame)
        {
            physics.Update(Frame, [], edge);
            changes += craft.Frozen ? 1 : 0;
        }
        output.WriteLine($"awake, with the player {Vector3.Distance(edge, physics.Position(craft)):0} m off: frozen for {changes} frames of 600");
        Assert.Equal(0, changes);

        for (var t = 0f; t < 10f && !craft.Frozen; t += Frame)
            physics.Update(Frame, [], at + east * 2500f);
        Assert.True(craft.Frozen, "it didn't freeze with the player 2.5 km away");
        changes = 0;
        for (var t = 0f; t < 10f; t += Frame)
        {
            physics.Update(Frame, [], edge);
            changes += craft.Frozen ? 0 : 1;
        }
        output.WriteLine($"frozen, with the player back at the edge: awake for {changes} frames of 600");
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_craft_loaded_frozen_on_a_wave_wakes_as_the_player_comes()
    {
        // Upright, its bell's bottom between the calm sea's surface at the start of time and a
        // crest's at the time it is loaded: in the water only in the waves as they are then.
        var d = DeepWater.Value;
        var calm = new float[Sea.WaveCount];
        Sea.Phases(0, calm);
        var phases = new float[Sea.WaveCount];
        var time = 0.0;
        do
            Sea.Phases(time += 0.25, phases);
        while (Sea.Height(d, phases) < Sea.Height(d, calm) + 0.2f && time < 600);
        var (low, high) = (Sea.Height(d, calm), Sea.Height(d, phases));
        Assert.True(high - low >= 0.2f, "no crest found");

        using var physics = new RocketPhysics(GroundBody.Planet);
        physics.WavesAt(time);
        var bell = Vehicle.Booster.Stages[0].BellHeight;
        var bottom = Planet.Centre + d * (Planet.SeaRadius + (low + high) / 2);
        var craft = physics.Restore(Vehicle.Booster, 0, 0, bottom + d * bell, Planet.UprightAt(bottom), Vector3.Zero, Vector3.Zero,
            legDeploy: 1f, fairingOn: false, frozen: true);
        output.WriteLine($"loaded {time:0.00} s in, its bell's bottom {(high - low) / 2:0.00} m under a crest: {craft.Submerged:0.000} submerged");
        Assert.True(craft.Frozen);
        Assert.True(craft.Submerged > 0f, "it was loaded as if out of the water");

        physics.Update(Frame, [], bottom + Planet.East(bottom) * 900f, time + Frame);
        Assert.False(craft.Frozen, "the player came within 900 m and it didn't wake");
    }
}
