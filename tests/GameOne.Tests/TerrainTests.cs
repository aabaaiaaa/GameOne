using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The cube sphere (directions to face coordinates and latitude/longitude) and the terrain map
/// format (saving, loading, refusing another radius or Home's elevation, bilinear reads and the
/// min/max ranges), and the one height function (<see cref="Terrain"/>) read from the shipped map.
/// </summary>
public sealed class TerrainTests(ITestOutputHelper output)
{
    private static Vector3 RandomDirection(Random rng)
    {
        while (true)
        {
            var d = new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1);
            if (d.LengthSquared() is > 0.01f and < 1f) return Vector3.Normalize(d);
        }
    }

    private static TerrainMap Synthetic(int resolution, float radius, Random rng, float homeElevation = Scene.HomeElevation)
    {
        var count = 6 * (resolution + 2) * (resolution + 2);
        var heights = new ushort[count];
        var kinds = new byte[count];
        var regions = new byte[count];
        for (var i = 0; i < count; i++)
        {
            heights[i] = (ushort)rng.Next(65536);
            kinds[i] = (byte)rng.Next(8);
            regions[i] = (byte)rng.Next(3);
        }

        return new TerrainMap(resolution, radius, homeElevation, -30f, 900f, heights, kinds, regions,
            [new SeaInfo("Bay of Tests", SeaKind.Warm, 0.7f, new Vector3(0.2f, 0.6f, 0.6f), new Vector3(0.03f, 0.15f, 0.3f)),
             new SeaInfo("Frost Sea", SeaKind.Icy, 2.5f, new Vector3(0.4f, 0.5f, 0.55f), new Vector3(0.05f, 0.08f, 0.12f))]);
    }

    [Fact]
    public void Directions_round_trip_through_every_face()
    {
        var rng = new Random(1);
        var faces = new HashSet<int>();
        for (var n = 0; n < 10_000; n++)
        {
            var d = RandomDirection(rng);
            var (face, u, v) = CubeSphere.ToFace(d);
            faces.Add(face);
            Assert.InRange(u, -1.0001f, 1.0001f);
            Assert.InRange(v, -1.0001f, 1.0001f);
            Assert.True((CubeSphere.FromFace(face, u, v) - d).Length() < 1e-5f);
        }

        Assert.Equal(6, faces.Count);
        var pad = CubeSphere.ToFace(Vector3.UnitY);
        Assert.Equal((2, 0f, 0f), (pad.Face, pad.U, pad.V));
    }

    [Fact]
    public void LatLon_puts_the_pad_at_the_origin_and_east_at_plus_longitude()
    {
        Assert.Equal((0f, 0f), CubeSphere.LatLon(Vector3.UnitY));
        var east = CubeSphere.LatLon(Planet.Up(Scene.DroneShipCentre)).Longitude;
        // The drone ship is 2390 m along the chord (x), so its longitude is asin(x / R), not x / R.
        Assert.Equal(MathF.Asin(2390f / Scene.PlanetRadius), east, 1e-4f);

        var rng = new Random(2);
        for (var n = 0; n < 100; n++)
        {
            var d = RandomDirection(rng);
            var (lat, lon) = CubeSphere.LatLon(d);
            Assert.True((CubeSphere.FromLatLon(lat, lon) - d).Length() < 1e-5f);
        }
    }

    [Fact]
    public void A_map_saves_and_loads_unchanged()
    {
        var map = Synthetic(16, Scene.PlanetRadius, new Random(3));
        var path = Path.Combine(Path.GetTempPath(), $"terrain-test-{Guid.NewGuid():N}.bin.gz");
        try
        {
            map.Save(path);
            var loaded = TerrainMap.Load(path);
            Assert.Equal(map.Resolution, loaded.Resolution);
            Assert.Equal(map.Radius, loaded.Radius);
            Assert.Equal(map.HomeElevation, loaded.HomeElevation);
            Assert.Equal(map.MinHeight, loaded.MinHeight);
            Assert.Equal(map.MaxHeight, loaded.MaxHeight);
            Assert.Equal(map.Heights, loaded.Heights);
            Assert.Equal(map.Kinds, loaded.Kinds);
            Assert.Equal(map.Regions, loaded.Regions);
            Assert.Equal(map.Seas, loaded.Seas);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_map_made_for_another_radius_is_refused()
    {
        var map = Synthetic(4, Scene.PlanetRadius * 2, new Random(4));
        var path = Path.Combine(Path.GetTempPath(), $"terrain-test-{Guid.NewGuid():N}.bin.gz");
        try
        {
            map.Save(path);
            var error = Assert.Throws<InvalidDataException>(() => TerrainMap.Load(path));
            Assert.Contains("regenerate", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_map_made_for_another_home_elevation_is_refused()
    {
        // Its heights are above the sea, and the generator levelled Home's pad at its elevation: made
        // for another, the pad (always Scene.HomeElevation up) would stand off its ground.
        var map = Synthetic(4, Scene.PlanetRadius, new Random(6), Scene.HomeElevation + 10f);
        var path = Path.Combine(Path.GetTempPath(), $"terrain-test-{Guid.NewGuid():N}.bin.gz");
        try
        {
            map.Save(path);
            var error = Assert.Throws<InvalidDataException>(() => TerrainMap.Load(path));
            Assert.Contains("elevation", error.Message);
            Assert.Contains("regenerate", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Range_contains_every_height_in_the_patch()
    {
        var rng = new Random(5);
        var map = Synthetic(64, Scene.PlanetRadius, rng);
        for (var n = 0; n < 200; n++)
        {
            var face = rng.Next(6);
            var size = rng.NextSingle() * 0.5f + 0.01f;
            var u0 = rng.NextSingle() * (2f - size) - 1f;
            var v0 = rng.NextSingle() * (2f - size) - 1f;
            var (min, max) = map.Range(face, u0, v0, size);
            for (var k = 0; k < 100; k++)
            {
                var h = map.HeightAt(face, u0 + rng.NextSingle() * size, v0 + rng.NextSingle() * size);
                Assert.InRange(h, min - 1e-3f, max + 1e-3f);
            }
        }
    }

    /// <summary>A unit vector across the ground at <paramref name="d"/> (perpendicular to it), at an angle round it.</summary>
    private static Vector3 Tangent(Vector3 d, float angle)
    {
        var a = Vector3.Normalize(Vector3.Cross(MathF.Abs(d.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, d));
        var b = Vector3.Cross(d, a);
        return a * MathF.Cos(angle) + b * MathF.Sin(angle);
    }

    /// <summary>The direction <paramref name="metres"/> along the ground from <paramref name="d"/>, along tangent <paramref name="t"/>.</summary>
    private static Vector3 Step(Vector3 d, Vector3 t, float metres) => Vector3.Normalize(d + t * (metres / Scene.PlanetRadius));

    private static float Degrees(Vector3 a, Vector3 b) => MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b)) * 180f / MathF.PI;

    [Fact]
    public void The_ground_at_the_pad_is_where_it_was()
    {
        Assert.Equal(0f, Planet.SurfaceAltitude(new Vector3(0, Scene.PadTop, 0)), 1e-4f);
        // 30 m from the pad's centre (14 m past its edge), on the pad's plane: the sphere there is
        // 0.045 m below the plane, and the levelled site is the sphere.
        Assert.InRange(Planet.SurfaceAltitude(new Vector3(30f, 0f, 0f)), -0.05f, 0.05f);
    }

    [Fact]
    public void The_height_is_continuous()
    {
        // Over 1 cm, no more than 4 cm: steeper than any slope on the map (its steepest, on the
        // ranges, are ~70 degrees, 3 m a metre), so only a step in the height fails, and one of
        // over 4 cm. (Over 10 cm, a limit of 20 cm was too close to those slopes.)
        var rng = new Random(11);
        void Check(Vector3 a, Vector3 t)
        {
            var b = Step(a, t, 0.01f);
            var ha = Terrain.Height(a);
            var hb = Terrain.Height(b);
            Assert.True(MathF.Abs(ha - hb) < 0.04f, $"{ha} at {a} but {hb} 0.01 m away");
        }

        for (var n = 0; n < 2000; n++)
        {
            var a = RandomDirection(rng);
            Check(a, Tangent(a, rng.NextSingle() * MathF.Tau));
        }

        // Along each of the 12 cube edges (where two coordinates' sizes are equal and the largest),
        // and across it each way.
        var signs = new[] { -1f, 1f };
        for (var axis = 0; axis < 3; axis++)
        {
            foreach (var s1 in signs)
            {
                foreach (var s2 in signs)
                {
                    for (var k = 0; k <= 200; k++)
                    {
                        var along = k / 100f - 1f;
                        var c = new float[3];
                        c[axis] = along;
                        c[(axis + 1) % 3] = s1;
                        c[(axis + 2) % 3] = s2;
                        var a = Vector3.Normalize(new Vector3(c[0], c[1], c[2]));
                        for (var q = 0; q < 4; q++)
                            Check(a, Tangent(a, q * MathF.PI / 2f + 0.3f));
                    }
                }
            }
        }

        // And round each of the 8 corners.
        foreach (var x in signs)
            foreach (var y in signs)
                foreach (var z in signs)
                {
                    var a = Vector3.Normalize(new Vector3(x, y, z));
                    for (var q = 0; q < 12; q++)
                        Check(a, Tangent(a, q * MathF.Tau / 12f));
                }
    }

    [Fact]
    public void The_same_direction_always_gives_the_same_height()
    {
        var rng = new Random(12);
        for (var n = 0; n < 200; n++)
        {
            var d = RandomDirection(rng);
            Assert.Equal(Terrain.Height(d), Terrain.Height(d));
        }
    }

    [Fact]
    public void Over_the_sea_the_surface_is_the_calm_sea()
    {
        var offshore = CubeSphere.FromLatLon(0f, 3000f / Scene.PlanetRadius);
        Assert.True(Terrain.IsSea(offshore));
        Assert.True(Terrain.Height(offshore) < 0f);
        Assert.Equal(0f, Terrain.SurfaceHeight(offshore));
    }

    [Fact]
    public void The_normal_is_up_on_flat_ground_and_tilted_on_slopes()
    {
        Assert.InRange(Degrees(Terrain.Normal(Vector3.UnitY), Planet.Up(Vector3.Zero)), 0f, 0.5f);

        // The steepest ground on a grid 25 m apart within 8 km of the pad.
        var east = Vector3.UnitX;
        var north = -Vector3.UnitZ;
        (float Slope, Vector3 At) steepest = (0f, Vector3.UnitY);
        float SlopeDegrees(Vector3 d)
        {
            var e = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, -1), d));
            var n = Vector3.Cross(d, e);
            var r = (Planet.SeaRadius + Terrain.SurfaceHeight(d)) / Scene.PlanetRadius;
            var ge = (Terrain.SurfaceHeight(Step(d, e, 0.5f)) - Terrain.SurfaceHeight(Step(d, e, -0.5f))) / r;
            var gn = (Terrain.SurfaceHeight(Step(d, n, 0.5f)) - Terrain.SurfaceHeight(Step(d, n, -0.5f))) / r;
            return MathF.Atan(MathF.Sqrt(ge * ge + gn * gn)) * 180f / MathF.PI;
        }

        for (var x = -8000f; x <= 8000f; x += 25f)
        {
            for (var z = -8000f; z <= 8000f; z += 25f)
            {
                if (x * x + z * z > 8000f * 8000f) continue;
                var d = Vector3.Normalize(Vector3.UnitY + (east * x + north * z) / Scene.PlanetRadius);
                var slope = SlopeDegrees(d);
                if (slope > steepest.Slope) steepest = (slope, d);
            }
        }

        Assert.True(steepest.Slope > 10f, $"the steepest ground near the pad is only {steepest.Slope}°");
        var tilt = Degrees(Terrain.Normal(steepest.At), steepest.At);
        Assert.True(MathF.Abs(tilt - steepest.Slope) <= 2f, $"at {steepest.At} (height {Terrain.SurfaceHeight(steepest.At)} m, {Terrain.Kind(steepest.At)}) the ground slopes {steepest.Slope}° but the normal tilts {tilt}°");

        // And it leans away from uphill, not into it.
        var at = steepest.At;
        var east2 = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, -1), at));
        var north2 = Vector3.Cross(at, east2);
        var uphill = east2 * (Terrain.SurfaceHeight(Step(at, east2, 0.5f)) - Terrain.SurfaceHeight(Step(at, east2, -0.5f))) +
                     north2 * (Terrain.SurfaceHeight(Step(at, north2, 0.5f)) - Terrain.SurfaceHeight(Step(at, north2, -0.5f)));
        Assert.True(Vector3.Dot(Terrain.Normal(at), Vector3.Normalize(uphill)) < 0f, "the normal leans uphill");
    }

    private static IEnumerable<(Vector3 Centre, Quaternion Tilt, float Depth, Func<int, Vector3> Edge)> Pads()
    {
        yield return (Vector3.Zero, Quaternion.Identity, Foundations.LaunchPad, k =>
        {
            // 200 points round the square's edge.
            var t = k / 200f * 4f;
            var side = (int)t;
            var along = (t - side) * 2f - 1f;
            var h = Scene.PadHalfSize;
            return side switch { 0 => new Vector3(h, 0, along * h), 1 => new Vector3(-along * h, 0, h), 2 => new Vector3(-h, 0, -along * h), _ => new Vector3(along * h, 0, -h) };
        });
        // And the landing zones on drums or tilted plinths: Home's and the test pads'. (The far
        // pads and zones stand on footings: see FoundationTests.)
        foreach (var plinth in Foundations.LandingZonePlinth.Where(p => p.Footing is null))
            yield return (plinth.Centre, plinth.Tilt, plinth.Depth, k =>
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 200f);
                return new Vector3(c, 0, s) * Scene.LandingZoneRadius;
            });
    }

    [Fact]
    public void Every_pad_stands_on_a_foundation_reaching_below_the_ground()
    {
        foreach (var (centre, tilt, depth, edge) in Pads())
        {
            var bottom = Planet.Altitude(centre + Planet.Up(centre) * Scene.PadTop) - depth;
            for (var k = 0; k < 200; k++)
            {
                var direction = Planet.Up(centre + Vector3.Transform(edge(k), tilt));
                Assert.True(Terrain.Height(direction) >= bottom + Scene.FoundationMargin - 0.01f,
                    $"ground at the pad near {centre} is {Terrain.Height(direction) - bottom:F2} m above its foundation's bottom");
            }
        }
    }

    /// <summary>The plinth of <paramref name="name"/>'s landing zone.</summary>
    private static Plinth ZoneOf(string name)
    {
        var zone = LaunchSites.Named(name).LandingZone!.Value;
        return Foundations.LandingZonePlinth.Single(p => Vector3.Distance(p.Centre, zone) < 0.01f);
    }

    [Fact]
    public void A_mountain_zones_slab_is_solid_where_it_is_drawn()
    {
        // A booster lying level against Polar's zone's side where it stands highest over the
        // ground, 1.5 m under its top (the slab's straight side), pushed at the wall: the slab
        // stops it at the rim, rather than it passing into the concrete.
        var plinth = ZoneOf("Polar");
        var r = Scene.LandingZoneRadius;
        var a = Vector3.Normalize(Vector3.Cross(plinth.Up, Vector3.UnitX));
        var outward = Enumerable.Range(0, 72).Select(k => MathF.SinCos(MathF.Tau * k / 72))
            .Select(sc => a * sc.Cos + Vector3.Cross(plinth.Up, a) * sc.Sin)
            .MaxBy(way => Planet.TerrainAltitude(plinth.Top + way * (r + 3f)));
        Assert.True(Planet.TerrainAltitude(plinth.Top + outward * (r + 3f)) > 9f, "Polar's zone stands too low over its ground to show anything");
        using var physics = new GameOne.Physics.RocketPhysics(GroundBody.Planet);
        var radius = Vehicle.Booster.Stages[0].Radius;
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var along = Vector3.Cross(plinth.Up, outward);
        var upright = Planet.UprightAt(plinth.Top);
        var lying = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(plinth.Up, along)), MathF.PI / 2) * upright);
        Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitY, lying), along) > 0.999f);
        var middle = plinth.Top - plinth.Up * 1.5f + outward * (r + radius + 0.3f);
        var craft = physics.Restore(Vehicle.Booster, 0, 0, middle - along * (length / 2f), lying, -outward * 3f, Vector3.Zero, legDeploy: 0f, fairingOn: false);
        var nearest = float.PositiveInfinity;
        for (var i = 0; i < 36; i++)
        {
            physics.Update(1f / 60, []);
            var axis = physics.Position(craft) + Vector3.Transform(Vector3.UnitY, physics.Rotation(craft)) * (length / 2f) - plinth.Top;
            nearest = MathF.Min(nearest, (axis - plinth.Up * Vector3.Dot(axis, plinth.Up)).Length());
        }
        output.WriteLine($"its middle came {nearest - r:0.00} m from the rim's line (its radius {radius:0.00} m)");
        Assert.True(nearest >= r + radius - 0.25f, $"its middle came {nearest - r:0.00} m from the rim's line (its radius {radius:0.00} m)");
    }

    [Fact]
    public void A_mountain_zone_is_drawn_within_its_rim_down_to_its_footing()
    {
        // Nothing drawn past the rim, and down to the footing's bottom.
        foreach (var name in new[] { "Polar", "Highcrest" })
        {
            var plinth = ZoneOf(name);
            var vertices = GameOne.Rendering.Meshes.BuildLandingZone(plinth).Select(v => new Vector3(v.Position.X, v.Position.Y, v.Position.Z)).ToList();
            var r = Scene.LandingZoneRadius;
            Assert.All(vertices, v => Assert.True(v.X * v.X + v.Z * v.Z <= r * r + 0.01f, $"{name}'s zone is drawn out to {MathF.Sqrt(v.X * v.X + v.Z * v.Z):0.00} m"));
            Assert.Equal(Scene.PadTop - plinth.Footing!.Depth, vertices.Min(v => v.Y), 3);
        }
    }

    [Fact]
    public void Each_test_pad_is_tilted_to_its_slope_and_stands_on_its_foundation()
    {
        for (var i = 0; i < Scene.TestPads.Length; i++)
        {
            var (name, centre, slope, tilt) = Scene.TestPads[i];
            var zone = Array.IndexOf(Scene.LandingZones, (centre, tilt));
            Assert.True(zone >= 1, $"{name} isn't one of the landing zones after the first");
            var up = Planet.Up(centre);
            var normal = Vector3.Transform(Vector3.UnitY, tilt);
            Assert.True(MathF.Abs(Degrees(normal, up) - slope) <= 0.1f, $"{name} is tilted {Degrees(normal, up)}°");

            // The top is what stands under a point just above it, at its centre and at its downhill edge.
            Vector3 OnTop(float x, float z) => centre + Vector3.Transform(new Vector3(x, Scene.PadTop, z), tilt);
            var downhill = OnTop(16f, 0f);
            Assert.True(Vector3.Dot(downhill - OnTop(0f, 0f), Planet.East(centre)) > 0f && Vector3.Dot(downhill - OnTop(0f, 0f), up) <= 0f,
                $"{name} doesn't fall away to the east");
            foreach (var at in (ReadOnlySpan<Vector3>)[OnTop(0f, 0f), downhill])
            {
                var reading = Planet.SurfaceAltitude(at + normal * 0.5f);
                Assert.True(MathF.Abs(reading - 0.5f) <= 0.01f, $"0.5 m above {name}'s top reads {reading} m");
            }

            // Below the ground all round (the foundation reaches FoundationMargin under it).
            var bottom = Planet.Altitude(centre + up * Scene.PadTop) - Foundations.LandingZones[zone];
            for (var k = 0; k < 64; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 64f);
                var rim = OnTop(c * Scene.LandingZoneRadius, s * Scene.LandingZoneRadius);
                var ground = Terrain.Height(Planet.Up(rim));
                Assert.True(ground >= bottom + Scene.FoundationMargin - 0.01f, $"the ground under {name}'s rim is {ground - bottom:F2} m above its foundation's bottom");

                // Its sides are vertical: just outside the rim, all the way down, is open air over the ground.
                var radial = Vector3.Transform(new Vector3(c, 0f, s), tilt);
                var outward = Vector3.Normalize(radial - up * Vector3.Dot(radial, up)); // horizontal, at right angles to up
                var height = Planet.Altitude(rim) - ground;
                for (var down = 0f; down < height; down += 1f)
                {
                    var p = rim - up * down + outward * 0.3f;
                    var over = Planet.Altitude(p) - Terrain.SurfaceHeight(Planet.Up(p));
                    Assert.True(MathF.Abs(Planet.SurfaceAltitude(p) - over) < 1e-3f, $"{name} overhangs the ground {down} m below its rim, at {k * 360 / 64}°");
                }

                // And solid: inside the rim, halfway down to the ground, is inside the plinth.
                var inside = rim - up * (height / 2f) - outward * 0.5f;
                Assert.True(Planet.SurfaceAltitude(inside) <= 0f, $"{name}'s plinth is hollow under its rim at {k * 360 / 64}°");
            }
        }
    }
}
