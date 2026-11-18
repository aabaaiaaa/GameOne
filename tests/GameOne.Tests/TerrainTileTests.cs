using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal.JoltApi;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The ground the physics collides with: height-field tiles laid round awake crafts (see
/// <see cref="TerrainTiles"/>), boosters landing on them, crashing into them and resting on them,
/// and crafts at rest frozen (no tiles) until something wakes them.
/// </summary>
public sealed class TerrainTileTests(ITestOutputHelper output)
{
    private static readonly Vehicle Booster = Vehicle.Booster;

    /// <summary>The worst a tile's own cell may reach from its centre, in its frame (metres): the tile less a sample's overlap and the slack.</summary>
    private static readonly float CellRoom = (Scene.TerrainTileSamples - 1) * Scene.TerrainTileSpacing / 2f
        - Scene.TerrainTileSpacing - 4f / MathF.Sqrt(2f);

    private static float Slope(Vector3 direction) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Terrain.Normal(direction), direction), -1f, 1f)));

    private static float Ground(Vector3 direction) => Planet.SeaRadius + Terrain.SurfaceHeight(direction);

    /// <summary>How far round the planet from <paramref name="p"/> to the nearest of the pad and the landing zones, metres.</summary>
    private static float FromThePads(Vector3 direction)
    {
        var p = Planet.Centre + direction * Planet.Radius;
        var nearest = Planet.Radius * Planet.AngleBetween(p, Vector3.Zero);
        foreach (var (centre, _) in Scene.LandingZones)
            nearest = MathF.Min(nearest, Planet.Radius * Planet.AngleBetween(p, centre));
        return nearest;
    }

    /// <summary>Directions on land within <paramref name="metres"/> of the pad (round the planet), at least <paramref name="clear"/> from the pads, whose slope <paramref name="accept"/> likes.</summary>
    private static IEnumerable<Vector3> LandPoints(int seed, float metres, Func<float, bool> accept, float clear = 300f)
    {
        var rng = new Random(seed);
        var reach = metres / Planet.Radius;
        while (true)
        {
            var (lat, lon) = ((rng.NextSingle() * 2f - 1f) * reach, (rng.NextSingle() * 2f - 1f) * reach);
            var d = CubeSphere.FromLatLon(lat, lon);
            if (MathF.Acos(Math.Clamp(d.Y, -1f, 1f)) > reach || Terrain.IsSea(d) || FromThePads(d) < clear || !accept(Slope(d)))
                continue;
            yield return d;
        }
    }

    /// <summary>
    /// A booster standing upright at <paramref name="direction"/> with its legs out, the lowest of
    /// its feet (hanging at full stroke) <paramref name="drop"/> above the ground; as a state, to load.
    /// <paramref name="settled"/> leans it square to the slope, as one that landed there would have
    /// come to rest: upright, on a slope, only its downhill foot touches, and it rocks onto the
    /// others as soon as it wakes.
    /// </summary>
    private static VesselState Standing(int id, Vector3 direction, float drop, FlightState state = FlightState.Flying, bool settled = false)
    {
        var rotation = Planet.UprightAt(Planet.Centre + direction);
        if (settled)
        {
            var normal = Terrain.Normal(direction);
            var axis = Vector3.Cross(direction, normal);
            if (axis.Length() > 1e-6f)
                rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Asin(MathF.Min(axis.Length(), 1f))) * rotation;
        }
        var position = Planet.Centre + direction * (Ground(direction) + 10f);
        var model = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        var lowest = Enumerable.Range(0, RocketGeometry.LegCount)
            .Min(leg => Planet.SurfaceAltitude(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg), model)));
        var stroke = state == FlightState.Landed ? 0f : LegGeometry.Rocket.Stroke; // a landed craft is loaded with its feet at no extension
        position -= direction * (lowest - stroke - drop);
        return new VesselState
        {
            Id = id, Flight = 0, Mission = Mission.LaunchAndLand, Vehicle = Booster, First = 0, Last = 0,
            Fuel = [Booster.Stages[0].FuelMass * 0.3f], State = state, Position = position, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(Booster, 0, 0),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero, Aim = direction, LegsWanted = true, LegDeploy = 1f,
            MaxAltitude = 400f, HasBeenAirborne = true, WasTouching = state == FlightState.Landed,
        };
    }

    private static WorldState World(IEnumerable<VesselState> vessels)
    {
        var list = vessels.ToList();
        return new WorldState { Vessels = list, NextVesselId = list.Count };
    }

    /// <summary>How far the lowest foot of <paramref name="vessel"/> is above the solid ground (or the pad under it).</summary>
    private static float FootGap(Vessel vessel) => Enumerable.Range(0, RocketGeometry.LegCount)
        .Min(leg => Planet.SurfaceAltitude(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg), vessel.Model)));

    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>
    /// The world's own vessels (not the new flight's rocket on the pad) watched: so one in the air
    /// stays in the physics, whose tiles a test is about, as nobody watching it, it would be moved
    /// by formula, with no body and no tiles (see <see cref="Unwatched"/>).
    /// </summary>
    private static FlightInput Watching(Simulation sim) => new(Watched: [.. sim.Flight.WorldVessels.Where(v => v.Flight == 0)]);

    private static readonly Pilot WatchingThem = (f, _) => new FlightInput(Watched: [.. f.WorldVessels.Where(v => v.Flight == 0)]);

    /// <summary>How long a wait on the worker may take before the test fails: far longer than any needs.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits (bounded, in real time) until the worker has nothing pending or in hand: the cells
    /// wanted so far are all finished, to be laid in the next step. The simulation runs many times
    /// faster than real time, so a test that wants the worker's tiles waits for them like this
    /// rather than for some seconds of flight, which may pass before the worker has made one.
    /// </summary>
    private static void WaitForWorker(Simulation sim) =>
        Assert.True(Waits.Until(() => !sim.Physics.Tiles.Worker.Busy, Patience), $"the worker never finished: {sim.Physics.Tiles.Worker.Describe()}");

    /// <summary>
    /// Steps <paramref name="sim"/> until its tile count has stopped growing for 30 frames (up to
    /// 3 s), waiting for the worker before each step, so the tiles it makes are in whatever the
    /// machine's speed.
    /// </summary>
    private static void Settle(Simulation sim, bool watched = false)
    {
        var (last, still) = (sim.Physics.TileCount, 0);
        for (var frame = 0; frame < 180 && still < 30; frame++)
        {
            WaitForWorker(sim);
            sim.Step(watched ? Watching(sim) : default);
            still = sim.Physics.TileCount == last ? still + 1 : 0;
            last = sim.Physics.TileCount;
        }
    }

    [Fact]
    public void The_tile_sample_count_is_accepted_at_the_default_block_size()
    {
        using var physics = new RocketPhysics(GroundBody.Planet); // Jolt's types registered
        var n = Scene.TerrainTileSamples;
        var samples = new float[n * n];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (i % n) * 0.01f - (i / n) * 0.02f;
        using var shape = HeightFields.Create(samples, (uint)n, new Vector3(-16f, 0f, -16f), new Vector3(0.5f, 1f, 0.5f));
        var field = Unsafe.BitCast<Shape, HeightFieldShape>(shape);
        output.WriteLine($"{n} samples: Jolt made {field.GetSampleCount()} at block size {field.GetBlockSize()}");
        Assert.Equal(2u, field.GetBlockSize()); // the default
        // Exactly as given: no row or column of Jolt's own (see Scene.TerrainTileSamples).
        Assert.Equal((uint)n, field.GetSampleCount());
        foreach (var (x, y) in new[] { (0, 0), (3, 5), (n - 1, 0), (0, n - 1), (n - 1, n - 1), (32, 32) })
        {
            var p = field.GetPosition((uint)x, (uint)y);
            Assert.Equal(-16f + 0.5f * x, p.X, 1e-4f);
            Assert.Equal(-16f + 0.5f * y, p.Z, 1e-4f);
            Assert.Equal(samples[y * n + x], p.Y, 0.005f);
            Assert.False(field.IsNoCollision((uint)Math.Min(x, n - 2), (uint)Math.Min(y, n - 2)));
        }
    }

    [Fact]
    public void Every_cell_fits_inside_its_tile_even_at_a_cube_corner()
    {
        var n = GroundBody.Planet.TileCellsPerFace;
        var highest = Planet.SeaRadius + Terrain.Map.MaxHeight + 20f;
        output.WriteLine($"{n} cells across a face; a cell is at most {MathF.PI / 2f * highest / n:0.0} m across");
        Assert.True(MathF.PI / 2f * highest / n <= 30f, "no cell may be wider than 30 m");
        var rng = new Random(4);
        var worst = 0f;
        var cells = new List<(int, int)> { (0, 0), (n - 1, 0), (0, n - 1), (n - 1, n - 1), (n / 2, 0), (0, n / 2), (n / 2, n / 2), (n / 2, n - 1) };
        for (var k = 0; k < 2000; k++)
            cells.Add((rng.Next(n), rng.Next(n)));
        for (var face = 0; face < 6; face++)
            foreach (var (i, j) in cells)
            {
                var cell = new TerrainTiles.Cell(face, i, j);
                var up = TerrainTiles.Frame(GroundBody.Planet, cell, out var across, out var along);
                Assert.True(MathF.Abs(Vector3.Dot(Vector3.Cross(across, up), along) - 1f) < 1e-4f, "the frame must be right-handed");
                foreach (var (a, b) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                {
                    var corner = TerrainTiles.Corner(GroundBody.Planet, face, i + a, j + b) * highest - up * highest;
                    worst = MathF.Max(worst, MathF.Max(MathF.Abs(Vector3.Dot(corner, across)), MathF.Abs(Vector3.Dot(corner, along))));
                }
            }
        output.WriteLine($"the furthest a cell reaches from its centre: {worst:0.00} m (room {CellRoom:0.00} m)");
        Assert.True(worst <= CellRoom);
    }

    [Fact]
    public void The_tiles_a_craft_needs_cover_the_ground_round_it()
    {
        // Anywhere, including the cube's corners and edges, still or moving fast: every point within
        // the margin of the stretch it covers lies on one of the tiles it needs.
        var rng = new Random(5);
        var places = new List<(Vector3 Position, Vector3 Velocity)>();
        foreach (var corner in new[] { new Vector3(1, 1, 1), new Vector3(-1, 1, -1), new Vector3(1, 0.001f, 1), new Vector3(0, 1, 0.0001f) })
            places.Add((Planet.Centre + Vector3.Normalize(corner) * (Ground(Vector3.Normalize(corner)) + 20f), new Vector3(30f, 0f, -40f)));
        for (var k = 0; k < 40; k++)
        {
            var d = Vector3.Normalize(new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1));
            places.Add((Planet.Centre + d * (Ground(d) + 50f), new Vector3(rng.NextSingle() - 0.5f, 0f, rng.NextSingle() - 0.5f) * 300f));
        }
        var room = (Scene.TerrainTileSamples - 1) * Scene.TerrainTileSpacing / 2f - Scene.TerrainTileSpacing;
        foreach (var (position, velocity) in places)
        {
            var cells = new HashSet<TerrainTiles.Cell>();
            TerrainTiles.Needed(GroundBody.Planet, position, position + velocity, cells);
            var up = Planet.Up(position);
            var ground = Ground(up);
            var a = Vector3.Normalize(Vector3.Cross(up, MathF.Abs(up.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
            var b = Vector3.Cross(up, a);
            var to = Vector3.Normalize(position + velocity - Planet.Centre) * ground - up * ground;
            for (var k = 0; k < 400; k++)
            {
                // A point within the margin of the stretch.
                var t = rng.NextSingle();
                var angle = rng.NextSingle() * MathF.Tau;
                var r = MathF.Sqrt(rng.NextSingle()) * Scene.TerrainTileMargin;
                var q = Vector3.Normalize(up * ground + to * t + (a * MathF.Cos(angle) + b * MathF.Sin(angle)) * r);
                var covered = cells.Any(cell =>
                {
                    var centre = TerrainTiles.Frame(GroundBody.Planet, cell, out var across, out var along);
                    var p = q * ground - centre * ground;
                    return Vector3.Dot(centre, q) > 0.99f && MathF.Abs(Vector3.Dot(p, across)) <= room && MathF.Abs(Vector3.Dot(p, along)) <= room;
                });
                Assert.True(covered, $"a point within the margin of a craft at {position} isn't on any of its {cells.Count} tiles");
            }
        }
    }

    [Fact]
    public void A_tile_follows_the_solid_ground()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        var n = Scene.TerrainTileSamples;
        var heights = new float[n * n];
        var worst = 0f;
        foreach (var d in LandPoints(6, 8000f, s => s > 15f).Take(5).Append(Vector3.UnitY))
        {
            var cell = TerrainTiles.CellOf(GroundBody.Planet, d);
            var centre = TerrainTiles.Heights(GroundBody.Planet, cell, heights);
            var up = TerrainTiles.Frame(GroundBody.Planet, cell, out var across, out var along);
            var half = (n - 1) * Scene.TerrainTileSpacing / 2f;
            using var shape = HeightFields.Create(heights, (uint)n, new Vector3(-half, 0f, -half),
                new Vector3(Scene.TerrainTileSpacing, 1f, Scene.TerrainTileSpacing));
            var field = Unsafe.BitCast<Shape, HeightFieldShape>(shape);
            var origin = Planet.Centre + up * (Planet.SeaRadius + centre);
            for (var x = 0; x < n; x += 4)
                for (var y = 0; y < n; y += 4)
                {
                    var local = field.GetPosition((uint)x, (uint)y);
                    var world = origin + across * local.X + up * local.Y + along * local.Z;
                    worst = MathF.Max(worst, MathF.Abs(Planet.Altitude(world) - Terrain.Height(Planet.Up(world))));
                }
        }
        output.WriteLine($"the furthest a tile's sample is from the solid ground: {worst * 1000:0.0} mm");
        Assert.True(worst < 0.01f);
    }

    [Fact]
    public void Over_the_sea_a_tile_is_the_seabed()
    {
        var n = Scene.TerrainTileSamples;
        var heights = new float[n * n];
        var cell = TerrainTiles.CellOf(GroundBody.Planet, WaterTests.DeepWater.Value);
        var centre = TerrainTiles.Heights(GroundBody.Planet, cell, heights);
        var up = TerrainTiles.Frame(GroundBody.Planet, cell, out var across, out var along);
        var half = (n - 1) * Scene.TerrainTileSpacing / 2f;
        var origin = Planet.Centre + up * (Planet.SeaRadius + centre);
        var worst = 0f;
        for (var x = 0; x < n; x += 4)
            for (var y = 0; y < n; y += 4)
            {
                var world = origin + across * (-half + x * Scene.TerrainTileSpacing) + up * heights[y * n + x] + along * (-half + y * Scene.TerrainTileSpacing);
                worst = MathF.Max(worst, MathF.Abs(Planet.Altitude(world) - Terrain.Height(Planet.Up(world))));
            }
        output.WriteLine($"the tile's centre is {centre:0.0} m (the sea at 0); its samples are at most {worst * 1000:0.0} mm off the seabed");
        Assert.True(centre < -100f, $"the tile is at {centre:0.0} m, not down on the seabed");
        Assert.True(worst < 0.01f);
    }

    [Fact]
    public void Tiles_build_quickly()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        var heights = new float[Scene.TerrainTileSamples * Scene.TerrainTileSamples];
        var cells = LandPoints(7, 8000f, _ => true).Take(60).Select(d => TerrainTiles.CellOf(GroundBody.Planet, d)).ToArray();
        TerrainTiles.Heights(GroundBody.Planet, cells[0], heights); // warmed up
        var clock = Stopwatch.StartNew();
        foreach (var cell in cells)
            using (HeightFields.Create(heights, (uint)Scene.TerrainTileSamples, Vector3.Zero, Vector3.One))
                TerrainTiles.Heights(GroundBody.Planet, cell, heights);
        var perSecond = cells.Length / clock.Elapsed.TotalSeconds;
        output.WriteLine($"{perSecond:0} tiles a second ({clock.Elapsed.TotalMilliseconds / cells.Length:0.00} ms each), on {Environment.ProcessorCount} processors");
        Assert.True(perSecond > 50, $"only {perSecond:0} tiles a second");
    }

    [Fact]
    public void A_height_field_tile_is_accepted()
    {
        var d = LandPoints(8, 8000f, s => s < 5f).First();
        using var sim = new Simulation(world: World([Standing(0, d, 0f)]));
        Assert.Equal(0, sim.Physics.TileCount);
        sim.Step(default);
        output.WriteLine($"{sim.Physics.TileCount} tiles round the booster and the rocket on the pad");
        Assert.True(sim.Physics.TileCount > 0);
    }

    [Fact]
    public void A_tile_laid_again_is_made_from_its_kept_shape()
    {
        // A booster at rest freezes (needing no tiles), its tiles go once unused, and when it wakes
        // they are laid again from the shapes kept, not made again from the terrain.
        var d = LandPoints(8, 8000f, s => s < 5f).First();
        using var sim = new Simulation(world: World([Standing(0, d, 0f, FlightState.Landed)]));
        var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
        // Watched, so it stays in the physics frozen (unwatched and at rest, it would leave it: see
        // Unwatched); and brought in at once, as it may load under formula, far from the pad.
        var watched = new[] { booster };
        sim.Flight.BringIn(booster);
        sim.Physics.Wake(booster.Craft);
        sim.Run((_, _) => new FlightInput(Watched: watched), _ => booster.Craft.Frozen, maxSeconds: 20);
        Assert.True(booster.Craft.Frozen);
        sim.Run((_, _) => new FlightInput(Watched: watched), _ => false, maxSeconds: Scene.TerrainTileLingerSeconds + 1);
        Assert.False(booster.OnFormula);
        var (made, fromKept) = (sim.Physics.Tiles.MadeInStep, sim.Physics.Tiles.FromKept);
        sim.Physics.Wake(booster.Craft);
        Assert.True(sim.Physics.Tiles.FromKept > fromKept, "none laid from a kept shape");
        Assert.Equal(made, sim.Physics.Tiles.MadeInStep); // the cells under it: all kept
    }

    [Fact]
    public void A_booster_dropped_on_open_ground_comes_to_rest_on_it()
    {
        // Twenty places on land within 8 km of the pad, one sloping 5-10°, the rest under 5°. Each
        // dropped from a metre (feet hanging): from 5 m it would hit at 9.9 m/s, past the legs' 6.
        var places = LandPoints(9, 8000f, s => s is > 5f and < 10f).Take(1)
            .Concat(LandPoints(10, 8000f, s => s < 5f).Take(19)).ToArray();
        using var sim = new Simulation(world: World(places.Select((d, i) => Standing(i, d, 1f))));
        var boosters = sim.Flight.WorldVessels.Where(v => v.Flight == 0).ToArray();
        // Watched, so each comes down in the physics, on the tiles this is about.
        sim.Run(WatchingThem, _ => boosters.All(b => b.State != FlightState.Flying), maxSeconds: 20);
        sim.Run(WatchingThem, _ => false, maxSeconds: 1);
        Assert.All(boosters, b => Assert.False(b.OnFormula));
        for (var i = 0; i < boosters.Length; i++)
        {
            var b = boosters[i];
            output.WriteLine($"{i}: slope {Slope(places[i]):0.0}°, {Terrain.Kind(places[i])}: {b.State} {b.CrashReason} at {b.TouchdownSpeed:0.0} m/s, " +
                $"altitude {b.Ground:0.000} m, lowest foot {(b.State == FlightState.Crashed ? float.NaN : FootGap(b)):0.000} m, tilt {b.TiltDegrees:0.0}°");
        }
        for (var i = 0; i < boosters.Length; i++)
        {
            var b = boosters[i];
            if (i == 0 && b.State == FlightState.Crashed)
            {
                Assert.StartsWith("Hit the ground at", b.CrashReason);
                continue;
            }
            Assert.Equal(FlightState.Landed, b.State);
            Assert.True(b.Ground < 0.05f);
            Assert.True(FootGap(b) >= -0.05f); // on its legs, not into the ground (the tiles themselves: see the box test below)
        }
    }

    [Fact]
    public void A_booster_rests_at_a_cube_corner()
    {
        var corners = new List<Vector3>();
        foreach (var x in new[] { -1f, 1f })
            foreach (var y in new[] { -1f, 1f })
                foreach (var z in new[] { -1f, 1f })
                    corners.Add(Vector3.Normalize(new Vector3(x, y, z)));
        // A corner on land (out at sea it would float, not stand on the tiles).
        var place = corners.First(c => !Terrain.IsSea(c) && Slope(c) < 8f);
        output.WriteLine($"at the corner {place} (land, slope {Slope(place):0.0}°), cell {TerrainTiles.CellOf(GroundBody.Planet, place)}");
        using var sim = new Simulation(world: World([Standing(0, place, 1f)]));
        var booster = sim.Flight.WorldVessels.First(v => v.Flight == 0);
        sim.Run(WatchingThem, _ => booster.State != FlightState.Flying, maxSeconds: 20); // watched: on the tiles, in the physics
        Assert.False(booster.OnFormula);
        output.WriteLine($"{booster.State} {booster.CrashReason}; lowest foot {FootGap(booster):0.000} m");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.Ground < 0.05f);
        Assert.True(FootGap(booster) >= -0.05f); // on its legs, not into the ground (the tiles themselves: see the box test below)
    }

    [Fact]
    public void A_box_set_down_on_open_ground_rests_on_the_tiles_at_the_terrains_height()
    {
        // A booster on its legs is held up by them (by the height function, not the tiles), so
        // the tiles themselves are checked with a loose box: a fairing half, whose collider is
        // one box (2.4 m across, 5.4 m long, 1.2 m thick, from its origin up y and out along +z),
        // set down flat 10 cm above the ground at the same twenty places, and at a cube corner.
        var places = LandPoints(9, 8000f, s => s is > 5f and < 10f).Take(1)
            .Concat(LandPoints(10, 8000f, s => s < 5f).Take(19))
            .Append(Vector3.Normalize(new Vector3(-1f, 1f, -1f))).ToArray(); // a cube corner on land (most are out at sea, where it would float)
        using var physics = new RocketPhysics(GroundBody.Planet);
        physics.Reset(Booster, LaunchSites.Home);
        physics.Remove(physics.Crafts[0]);
        const float width = Scene.RocketRadius, length = Scene.FairingHeight + Scene.FairingNoseHeight;
        var worst = (Low: 0f, High: 0f);
        foreach (var d in places)
        {
            var normal = Terrain.Normal(d);
            var along = Vector3.Normalize(Vector3.Cross(normal, Planet.East(Planet.Centre + d)));
            var start = Planet.Centre + d * Ground(d) - along * (length / 2) + normal * 0.1f;
            var x = Vector3.Cross(along, normal);
            var lying = Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, along.X, along.Y, along.Z, 0f, normal.X, normal.Y, normal.Z, 0f, 0f, 0f, 0f, 1f));
            var box = physics.AddLoose(Vehicle.FairingHalf, start, lying, Vector3.Zero, Vector3.Zero);
            for (var t = 0f; t < 3f; t += 1f / 60)
                physics.Update(1f / 60, []);
            var model = physics.Model(box);
            var heights = ((ReadOnlySpan<Vector3>)[new(-width, 0, 0), new(width, 0, 0), new(-width, length, 0), new(width, length, 0)]).ToArray()
                .Select(c => Vector3.Transform(c, model))
                .Select(p => Planet.Altitude(p) - Terrain.SurfaceHeight(Planet.Up(p))).ToArray();
            output.WriteLine($"slope {Slope(d):0.0}°: its bottom corners {heights.Min() * 100:0.0} to {heights.Max() * 100:0.0} cm off the ground, at {physics.Velocity(box).Length():0.000} m/s");
            worst = (MathF.Min(worst.Low, heights.Min()), MathF.Max(worst.High, heights.Min()));
            Assert.True(heights.Min() >= -0.05f, $"at slope {Slope(d):0.0}°, the box is {-heights.Min():0.00} m into the ground");
            Assert.True(heights.Min() <= 0.05f, $"at slope {Slope(d):0.0}°, the box rests {heights.Min():0.00} m above the ground");
            physics.Remove(box);
        }
        output.WriteLine($"lowest corners from {worst.Low * 100:0.0} to {worst.High * 100:0.0} cm off the ground");
    }

    [Fact]
    public void A_booster_flown_into_a_mountainside_crashes()
    {
        var rng = new Random(11);
        Vector3 d;
        do
            d = Vector3.Normalize(new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1));
        while (Terrain.IsSea(d) || Slope(d) < 30f);
        var normal = Terrain.Normal(d);
        var uphill = -Vector3.Normalize(normal - d * Vector3.Dot(normal, d));
        var state = Standing(0, d, 20f) with { Velocity = uphill * 50f, LegsWanted = false, LegDeploy = 0f };
        output.WriteLine($"slope {Slope(d):0.0}° at {d}, {Terrain.SurfaceHeight(d):0} m up");
        using var sim = new Simulation(world: World([state]));
        var booster = sim.Flight.WorldVessels.First(v => v.Flight == 0);
        var deepest = float.MaxValue;
        var tiled = true;
        var before = booster.Velocity;
        var lost = 0f;
        sim.Run(WatchingThem, _ => // watched: the physics' tiles and collider are what is tested
        {
            Assert.False(booster.OnFormula);
            if (booster.State == FlightState.Crashed)
            {
                // The frame it hit: how much of its velocity went at once.
                lost = (before - booster.Velocity).Length();
                return true;
            }
            deepest = MathF.Min(deepest, Planet.Altitude(booster.Position) - Terrain.SurfaceHeight(Planet.Up(booster.Position)));
            tiled &= sim.Time == 0 || sim.Physics.HasTile(TerrainTiles.CellOf(GroundBody.Planet, booster.Position - Planet.Centre));
            before = booster.Velocity;
            return false;
        }, maxSeconds: 5);
        output.WriteLine($"{booster.State}: {booster.CrashReason}; {before.Length():0.0} m/s before, {lost:0.0} m/s lost in the frame it hit; " +
            $"tiles under it all the way {tiled}; at its lowest {deepest:0.00} m above the ground");
        Assert.Equal(FlightState.Crashed, booster.State);
        // It was stopped by the collider (the tiles under it), not merely judged by the contact
        // check: a sudden loss of speed only a collision makes, and it never got into the mountain.
        Assert.True(tiled, "it flew over ground without its tile");
        Assert.StartsWith("Hit the ground", booster.CrashReason);
        Assert.True(lost > 15f, $"only {lost:0.0} m/s lost when it hit");
        Assert.True(deepest > -2f, $"it went {-deepest:0.0} m into the mountain");
    }

    [Fact]
    public void A_booster_at_rest_freezes_without_tiles_and_stays_put()
    {
        using var sim = new Simulation(Mission.LaunchAndLand);
        Assert.True(sim.RunToEnd(new Autopilot().Fly), "the booster should come down");
        var flight = sim.Flight;
        Assert.Equal(FlightState.Landed, flight.State);
        var craft = flight.Controlled.Craft;

        sim.Run((_, _) => default, _ => false, maxSeconds: 5);
        output.WriteLine($"5 s after landing: frozen {craft.Frozen}, {sim.Physics.TileCount} tiles");
        Assert.True(craft.Frozen);
        Assert.False(sim.Physics.IsActive(craft));
        Assert.Equal(0, sim.Physics.TileCount);

        var at = sim.Physics.Position(craft);
        sim.Run((_, _) => default, _ => false, maxSeconds: 60);
        var moved = Vector3.Distance(at, sim.Physics.Position(craft));
        output.WriteLine($"a minute on: moved {moved * 1000:0.000} mm, still frozen {craft.Frozen}, Jolt simulating it {sim.Physics.IsActive(craft)}");
        Assert.True(moved < 0.001f);
        Assert.True(craft.Frozen);
        Assert.Equal(FlightState.Landed, flight.State);
    }

    [Fact]
    public void A_frozen_rocket_wakes_when_flown_and_a_booster_landed_near_sleeps_through_its_launch()
    {
        // A booster landed on open ground 150 m from the pad, loaded frozen; and the rocket on the
        // pad, which freezes too as it waits there. The pad stands on raised ground that falls away
        // past its levelled site, so the booster is on a gentle slope, settled square to it.
        var near = LandPoints(12, 190f, s => s < 4f, clear: 0f).First(d => Planet.Radius * Planet.AngleBetween(Planet.Centre + d, Vector3.Zero) > 120f);
        var landed = Standing(0, near, 0f, FlightState.Landed, settled: true);
        using var sim = new Simulation(world: World([landed]));
        var flight = sim.Flight;
        var other = flight.WorldVessels.First(v => v.Flight == 0);
        output.WriteLine($"the landed booster is {Planet.Radius * Planet.AngleBetween(other.Position, Vector3.Zero):0} m from the pad");
        Assert.True(other.Craft.Frozen);

        sim.Run((_, _) => default, _ => false, maxSeconds: 5);
        var rocket = flight.Controlled.Craft;
        Assert.True(rocket.Frozen, "the rocket waiting on the pad should have frozen");
        Assert.True(other.Craft.Frozen, "a rocket standing still on the pad shouldn't wake it");
        var otherAt = sim.Physics.Position(other.Craft);

        // Launched: thrust wakes the rocket, with the ground laid round it, and it lifts off.
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        Assert.True(sim.Run((_, _) => default, f => f.EngineLit, maxSeconds: 5));
        sim.Step(default);
        Assert.False(rocket.Frozen);
        Assert.True(sim.Physics.TileCount > 0);
        var woke = false;
        Assert.True(sim.Run((_, _) => default, f =>
        {
            woke |= !other.Craft.Frozen;
            return f.Ground > 100f;
        }, maxSeconds: 20), "the rocket should lift off");

        // The rocket went up past it, never on a way that could touch it: it slept through, where it stood.
        output.WriteLine($"the landed booster woke: {woke}; moved {Vector3.Distance(otherAt, sim.Physics.Position(other.Craft)) * 1000:0.0} mm");
        Assert.False(woke, "woken by a launch that never came near it");
        Assert.Equal(FlightState.Landed, other.State);
        Assert.True(other.Craft.Frozen);
        Assert.Equal(otherAt, sim.Physics.Position(other.Craft));
    }

    [Fact]
    public void Tile_bodies_take_ids_of_their_own_so_a_vessel_s_id_never_depends_on_them()
    {
        // Jolt orders some of its work by body ID, and the tiles are laid at times that vary, so
        // a vessel's ID must be the same whatever tiles have been laid and let go before it.
        // Two worlds alike but for that: a booster frozen on open ground, woken by its legs (so
        // the ground is laid under it in the first), then taken out, and the tiles left to lapse
        // in the first. Then the same craft is added to each, as a parting stage or a loaded vessel is.
        var d = LandPoints(14, 8000f, slope => slope < 4f).First();
        var s = Standing(0, d, 0f, FlightState.Landed);

        (RocketPhysics Physics, Craft New, List<uint> Laid) Run(bool wake)
        {
            var physics = new RocketPhysics(GroundBody.Planet);
            var laid = new List<uint>();
            var scenery = physics.BodyCount; // no tile yet: the scenery alone, whatever the placeholders
            var craft = physics.Restore(Booster, 0, 0, s.Position.ToSingle(), s.Rotation, Vector3.Zero, Vector3.Zero, 1f, fairingOn: false, frozen: true);
            if (wake)
                physics.SetLegs(craft, 0.98f);
            for (var t = 0f; t < 3f; t += 1f / 60)
                physics.Update(1f / 60, []);
            laid.AddRange(physics.Tiles.TileBodies);
            Assert.Equal(scenery + 1 + physics.TileCount, physics.BodyCount); // the scenery, the craft and the tiles laid
            physics.Remove(craft);
            for (var t = 0f; t < Scene.TerrainTileLingerSeconds + 1f; t += 1f / 60)
                physics.Update(1f / 60, []);
            Assert.Equal(scenery, physics.BodyCount);
            var up = Planet.Up(s.Position.ToSingle());
            var added = physics.AddLoose(Booster, s.Position.ToSingle() + up * 100f, s.Rotation, Vector3.Zero, Vector3.Zero);
            Assert.Equal(scenery + 1, physics.BodyCount); // one more than before it: the placeholders are never counted
            return (physics, added, laid);
        }

        var (with, withCraft, laid) = Run(wake: true);
        using var _1 = with;
        var (without, withoutCraft, none) = Run(wake: false);
        using var _2 = without;
        output.WriteLine($"{laid.Count} tiles laid in the first; new craft's ID {withCraft.Id} with, {withoutCraft.Id} without");

        Assert.NotEmpty(laid);
        Assert.Empty(none);
        Assert.All(laid, id => Assert.True(TerrainTiles.IsTileBody(id), $"tile body {id} is outside the range"));
        Assert.Equal(withoutCraft.Id, withCraft.Id);
        Assert.False(TerrainTiles.IsTileBody(withCraft.Id));
        Assert.All(with.Crafts, c => Assert.False(TerrainTiles.IsTileBody(c.Id)));
        Assert.Equal(0, with.TileCount);
        Assert.Equal(without.BodyCount, with.BodyCount); // the placeholders are never counted (and Run checked the count absolutely)
    }

    [Fact]
    public void A_tile_slot_made_again_past_its_sequence_numbers_still_works_and_an_old_id_no_longer_resolves()
    {
        // A tile's slot is made again (laid, then a placeholder again) under its ID with the
        // sequence number one on, so an old ID never resolves to the new body; the number has 8
        // bits, so past 255 it wraps. A craft of the test's own (no body: only the tiles see it) is
        // put low over open ground and the tiles updated, laying its cells; then the world is
        // stepped with no craft until they lapse. Each round makes each slot twice, 130 rounds past
        // the wrap. Each round, every tile is in the world under its new ID, and the last round's
        // IDs no longer resolve; at the end a booster dropped there still comes down onto the ground.
        var d = LandPoints(18, 8000f, slope => slope < 4f).First();
        var ground = Planet.Centre + d * (Planet.SeaRadius + Terrain.Height(d));
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var settings = SphereShapeSettings.Create(1f);
        using var craft = new Craft(Booster, 0, 0, uint.MaxValue, ((ShapeSettings)settings).CreateShape(), null);
        (Vector3, Vector3, Vector3) Motion(Craft _) => (ground + d * 5f, Vector3.Zero, d);
        var sequences = new Dictionary<uint, List<uint>>(); // by slot (the ID's index), each round's sequence number
        uint[] last = [];
        const int rounds = 130;
        for (var round = 0; round < rounds; round++)
        {
            physics.Tiles.Update([craft], Motion);
            var ids = physics.Tiles.TileBodies.ToArray();
            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.True(physics.IsInWorld(id), $"round {round}: tile {id:X8} is not in the world"));
            Assert.All(last, id => Assert.False(physics.IsInWorld(id), $"round {round}: the last round's ID {id:X8} still resolves"));
            foreach (var id in ids)
            {
                if (!sequences.TryGetValue(id & 0x7FFFFF, out var list))
                    sequences[id & 0x7FFFFF] = list = [];
                list.Add(id >> 23);
            }
            last = ids;
            for (var t = 0f; t < Scene.TerrainTileLingerSeconds + 0.1f; t += 1f / 60)
                physics.Update(1f / 60, []);
            Assert.Equal(0, physics.TileCount);
        }
        var wrapped = sequences.Count(s => s.Value.Zip(s.Value.Skip(1)).Any(p => p.Second < p.First));
        output.WriteLine($"{sequences.Count} slots used, each {sequences.Values.Min(l => l.Count)} to {sequences.Values.Max(l => l.Count)} rounds; {wrapped} wrapped past 255; " +
            $"made from the terrain {physics.Tiles.MadeInStep}, from kept shapes {physics.Tiles.FromKept}");
        Assert.True(wrapped > 0, "no slot's sequence number wrapped");

        // Its body's bottom (its origin) dropped from 5 m, its bell under it: resting on it, or toppled onto its side.
        var booster = physics.AddLoose(Booster, ground + d * 5f, Planet.UprightAt(ground), Vector3.Zero, Vector3.Zero);
        for (var t = 0f; t < 3f; t += 1f / 60)
            physics.Update(1f / 60, []);
        var above = Planet.GroundAltitude(physics.Position(booster));
        output.WriteLine($"a booster dropped from 5 m: its bottom {above:0.00} m above the ground after 3 s, on {physics.TileCount} tiles");
        Assert.InRange(above, 0f, 4f);
    }

    [Fact]
    public void A_frozen_booster_wakes_when_its_legs_move()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        var s = Standing(0, LandPoints(14, 8000f, slope => slope < 4f).First(), 0f, FlightState.Landed);
        var craft = physics.Restore(Booster, 0, 0, s.Position.ToSingle(), s.Rotation, Vector3.Zero, Vector3.Zero, 1f, fairingOn: false, frozen: true);
        Assert.True(craft.Frozen);
        Assert.False(physics.IsActive(craft));
        Assert.Equal(0, physics.TileCount);

        physics.SetLegs(craft, 0.98f); // starting to fold
        Assert.False(craft.Frozen);
        Assert.True(physics.IsActive(craft));
        Assert.True(physics.TileCount > 0, "woken, it should have the ground laid under it first");
    }

    [Fact]
    public void A_frozen_stage_wakes_when_it_jettisons_its_fairing()
    {
        // The satellite launcher's upper stage and satellite, fairing on, standing on its bell.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var d = LandPoints(15, 8000f, slope => slope < 2f).First();
        var stage = Vehicle.ThreeStage.Stages[1];
        var position = Planet.Centre + d * (Ground(d) + stage.BellHeight);
        var craft = physics.Restore(Vehicle.ThreeStage, 1, 2, position, Planet.UprightAt(position), Vector3.Zero, Vector3.Zero, 0f,
            fairingOn: true, frozen: true);
        Assert.True(craft.Frozen);
        Assert.Equal(0, physics.TileCount);

        physics.JettisonFairing(craft);
        Assert.False(craft.FairingOn);
        Assert.False(craft.Frozen);
        Assert.True(physics.IsActive(craft));
        Assert.True(physics.TileCount > 0, "woken, it should have the ground laid under it first");
    }

    [Fact]
    public void A_field_of_frozen_boosters_wakes_only_the_row_flown_over_and_none_falls_through()
    {
        // 49 landed boosters, 11 m apart, on the landing zone's flat top (within 44 m), loaded
        // frozen: as crowded on each tile as the whole field below, so each of its rules shows here.
        // The booster flying over them starts 70 m west of the zone heading east at 50 m/s, low over
        // the middle row: it could touch those under it, so they wake as it comes (the next one or
        // two together, a quarter of a second ahead of it), and nothing beside them.
        Field(within: 4, atLeast: 45, startEast: -70f, speed: 50f, flySeconds: 3f);
    }

    [LongFact]
    [Trait("Run", "Long")]
    public void A_whole_field_of_frozen_boosters_wakes_only_the_row_flown_over_and_none_falls_through()
    {
        // The whole field: about 170 landed boosters, 11 m apart, on and round the landing zone: of
        // the 197 places within 88 m, the zone's raised ground falls away past ~70 m, too steep (6
        // to 10 degrees) to stand on in about 30 of them. The booster flying over them starts 100 m
        // west of the zone and flies east at 25 m/s low over the middle row and on past it, so the
        // row it wakes runs over the ground's fall as well as the zone's top. It is a long test, and
        // the quicker field above checks the same rules in the normal suite.
        Field(within: 8, atLeast: 160, startEast: -100f, speed: 25f, flySeconds: 14f);
    }

    /// <summary>
    /// Landed boosters, loaded frozen, 11 m apart within <paramref name="within"/> places of the
    /// landing zone's centre, where the ground is level enough, numbered
    /// from <paramref name="firstId"/>: <see cref="Field"/>'s, and the paced fast collisions' dive
    /// near them (<see cref="PerfScenes.FastCollisions"/>).
    /// </summary>
    internal static List<VesselState> FrozenField(int within, int firstId = 0)
    {
        var zone = Scene.LandingZoneCentre;
        var (east, north) = (Planet.East(zone), Planet.North(zone));
        var places = new List<Vector3>();
        for (var x = -within; x <= within; x++)
            for (var z = -within; z <= within; z++)
            {
                if (x * x + z * z > within * within)
                    continue;
                var d = Vector3.Normalize(zone - Planet.Centre + (east * x + north * z) * 11f);
                if (!Terrain.IsSea(d) && Slope(d) < 6f)
                    places.Add(d);
            }
        return places.Select((d, i) => Standing(firstId + i, d, 0f, FlightState.Landed)).ToList();
    }

    /// <summary>
    /// Landed boosters loaded frozen (<see cref="FrozenField"/>, at least <paramref name="atLeast"/> of
    /// them), and a booster flying low over the middle row (<see cref="FieldFlyGap"/> over their tops),
    /// starting <paramref name="startEast"/> east of the zone (west if negative), flying east at
    /// <paramref name="speed"/>, flown to hold its height and speed for <paramref name="flySeconds"/>:
    /// every one in that row must wake once, and none beside it, with one tile update a step, none
    /// falling through, and freeze again once it has gone.
    /// </summary>
    /// <summary>How far the body of the booster flying over a field is held over the tops of those under it: within the wake's margin of them, and the next rows, 11 m off, out of it.</summary>
    private const float FieldFlyGap = 0.6f * Scene.WakeMargin;

    private void Field(int within, int atLeast, float startEast, float speed, float flySeconds)
    {
        var zone = Scene.LandingZoneCentre;
        var east = Planet.East(zone);
        var up = Planet.Up(zone);
        var states = FrozenField(within);
        // Its lowest point held over the ground so far over their tops, its body's and theirs' widths between.
        var tops = states.Max(s => Planet.GroundAltitude(s.Position.ToSingle() + Vector3.Transform(Vector3.UnitY, s.Rotation) * Booster.Stages[0].Height));
        var height = tops + 2f * Booster.Stages[0].Radius + FieldFlyGap;

        var fuel = Booster.Stages[0].FuelMass * 0.5f;
        var hover = Scene.SurfaceGravity * (Booster.Stages[0].DryMass + fuel) / Booster.Stages[0].Thrust;
        var start = Planet.Centre + Vector3.Normalize(zone - Planet.Centre + east * startEast) * (Ground(up) + height);
        states.Add(Standing(states.Count, Planet.Up(start), 0f) with
        {
            Position = start, Velocity = Planet.East(start) * speed, Fuel = [fuel], Throttle = hover, LegsWanted = false, LegDeploy = 0f,
            Aim = Planet.Up(start), Rotation = Planet.UprightAt(start),
        });
        using var sim = new Simulation(world: World(states));
        var flight = sim.Flight;
        var field = flight.WorldVessels.Where(v => v.Flight == 0 && v.State == FlightState.Landed).ToArray();
        var flyer = flight.WorldVessels.Single(v => v.Flight == 0 && v.State == FlightState.Flying);
        output.WriteLine($"{field.Length} landed boosters, all frozen: {field.All(b => b.Craft.Frozen)}");
        Assert.True(field.Length >= atLeast, $"only {field.Length} boosters");
        Assert.All(field, b => Assert.True(b.Craft.Frozen));
        var north = Planet.North(zone);
        var inRow = field.Select(b => MathF.Abs(Vector3.Dot(b.Position - zone, north)) < 5.5f).ToArray(); // 11 m apart
        var wakes = new int[field.Length];
        var wasFrozen = field.Select(b => b.Craft.Frozen).ToArray();
        sim.Step(new FlightInput(SwitchTo: flyer));
        var maxLean = MathF.Min(0.4f * speed / 25f, 0.6f);
        Pilot hold = (f, _) =>
        {
            // Leaning east enough to hold its speed against the drag, and throttled to hold its height.
            var eastward = Planet.East(f.Position);
            var lean = Math.Clamp(0.2f * speed / 25f + 0.05f * (speed - Vector3.Dot(f.Velocity, eastward)), 0f, maxLean);
            var wanted = (height - f.Ground) * 0.5f;
            var throttle = (hover / MathF.Cos(lean) + (wanted - f.VerticalSpeed) * 0.3f - f.Throttle) * 4f;
            return new FlightInput(Throttle: Math.Clamp(throttle, -1f, 1f), Hold: Vector3.Normalize(f.LocalUp + eastward * lean));
        };

        // Over the field and on past it, timing every frame, counting each booster's wakes, and
        // watching its feet while it is awake.
        var (lowest, highest) = (float.MaxValue, float.MinValue);
        var (slowest, total) = (0.0, 0.0);
        var updates = sim.Physics.TileUpdates;
        var frames = 0;
        var clock = new Stopwatch();
        for (var t = 0f; t < flySeconds; t += Simulation.FrameSeconds)
        {
            clock.Restart();
            sim.Step(hold(flight, Simulation.FrameSeconds));
            slowest = Math.Max(slowest, clock.Elapsed.TotalSeconds);
            total += clock.Elapsed.TotalSeconds;
            frames++;
            for (var i = 0; i < field.Length; i++)
            {
                var frozen = field[i].Craft.Frozen;
                if (wasFrozen[i] && !frozen)
                    wakes[i]++;
                wasFrozen[i] = frozen;
                if (!frozen && t > 1f) // given a moment to settle on its legs
                {
                    var gap = FootGap(field[i]);
                    (lowest, highest) = (MathF.Min(lowest, gap), MathF.Max(highest, gap));
                }
            }
        }
        var perStep = (sim.Physics.TileUpdates - updates) / (double)(frames * 2);
        output.WriteLine($"flyer {flyer.State} at {flyer.Ground:0} m, {Planet.Radius * Planet.AngleBetween(flyer.Position, zone):0} m past the zone; " +
            $"woken: {wakes.Count(w => w > 0)} of the row's {inRow.Count(r => r)}, most wakes of one {wakes.Max()}; slowest frame {slowest * 1000:0} ms, " +
            $"{total:0.00} s in all for {frames} frames; tile updates a step {perStep:0.00}; " +
            $"tiles now {sim.Physics.TileCount}; awake feet from {lowest:0.000} to {highest:0.000} m off the ground");

        Assert.Equal(FlightState.Flying, flyer.State);
        for (var i = 0; i < field.Length; i++) // every one under it woke, once (no freezing while it's near), and none beside it
            Assert.True(wakes[i] == (inRow[i] ? 1 : 0), $"{(inRow[i] ? "in" : "beside")} the row, {Vector3.Dot(field[i].Position - zone, north):0} m north: woken {wakes[i]} times");
        // One tile update a step, however many woke together.
        Assert.True(perStep < 1.05, $"{perStep:0.00} tile updates a step");
        // Generous, as a busy machine can stall any frame. The count of tile updates above is the
        // exact check; this one catches a wake grown costly some other way.
        Assert.True(slowest < 1.0, $"a frame took {slowest:0.00} s");
        Assert.All(field, b => Assert.Equal(FlightState.Landed, b.State));
        Assert.InRange(lowest, -0.05f, 0.05f);
        Assert.InRange(highest, -0.05f, 0.05f);

        // Once it has gone, they freeze again, and their tiles go.
        sim.Run(hold, _ => field.All(b => b.Craft.Frozen), maxSeconds: 20);
        foreach (var b in field.Where(b => !b.Craft.Frozen))
            output.WriteLine($"awake: v {sim.Physics.Velocity(b.Craft).Length():0.0000} w {sim.Physics.AngularVelocity(b.Craft)} about its axis {Vector3.Dot(sim.Physics.AngularVelocity(b.Craft), b.Up):0.0000} still {b.Craft.StillFor:0.00} gap {FootGap(b):0.000} " +
                $"from zone {Vector3.Distance(b.Position, zone):0.0} tilt {b.TiltDegrees:0.0} legs {b.Craft.LegExtension}");
        Assert.All(field, b => Assert.True(b.Craft.Frozen));
    }

    [Fact]
    public void Tiles_are_let_go_once_nothing_needs_them()
    {
        using var sim = new Simulation(Mission.LaunchAndLand);
        Pilot climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : new FlightInput(FullThrottle: true);
        sim.Step(default);
        Assert.True(sim.Physics.TileCount > 0);
        Assert.True(sim.Run(climb, f => f.Ground > 1000f, maxSeconds: 60), "the booster should climb past 1 km");
        sim.Run(climb, _ => false, maxSeconds: Scene.TerrainTileLingerSeconds + 1);
        output.WriteLine($"at {sim.Flight.Ground:0} m: {sim.Physics.TileCount} tiles");
        Assert.Equal(0, sim.Physics.TileCount);
    }

    [Fact]
    public void A_world_of_many_landed_vessels_loads_frozen()
    {
        var places = LandPoints(13, 8000f, s => s < 4f).Take(500).ToArray();
        var world = World(places.Select((d, i) => Standing(i, d, 0f, FlightState.Landed)));
        var clock = Stopwatch.StartNew();
        using var sim = new Simulation(world: world);
        clock.Stop();
        var boosters = sim.Flight.WorldVessels.Where(v => v.Flight == 0).ToArray();
        output.WriteLine($"{boosters.Length} landed boosters loaded in {clock.Elapsed.TotalSeconds:0.00} s: {sim.Physics.BodyCount} bodies, "
            + $"{sim.Physics.TileCount} tiles, {boosters.Count(b => b.OnFormula)} straight under formula");
        Assert.Equal(500, boosters.Length);
        Assert.True(clock.Elapsed.TotalSeconds < 10);
        // Each frozen in the physics, or (far from the pad, nothing to hold it there) under formula
        // with no body at all: landed as it was, either way.
        Assert.All(boosters, b => Assert.True(b.OnFormula ? b.Craft.Removed : b.Craft.Frozen));
        // And some frozen in the physics (those near enough the pad), so the frozen load is still what is tested.
        Assert.Contains(boosters, b => !b.OnFormula && b.Craft.Frozen);
        Assert.Equal(0, sim.Physics.TileCount);
        Assert.True(sim.Physics.BodyCount <= RocketPhysics.MaxBodies);

        // And they stay that way: only the rocket on the pad has tiles round it. Nobody watching
        // them, most leave the physics, landed as they were (see Unwatched); a few are watched, so
        // the physics' own part (frozen, no tiles laid) is still asked of some.
        var watched = boosters.Take(3).ToArray();
        sim.Run((_, _) => new FlightInput(Watched: watched), _ => false, maxSeconds: 2);
        var under = boosters.Count(b => b.OnFormula);
        output.WriteLine($"2 s on: {sim.Physics.TileCount} tiles, {under} under formula, {sim.WallTime.TotalSeconds:0.00} s to fly");
        Assert.All(boosters, b => Assert.True(b.State == FlightState.Landed && (b.OnFormula || b.Craft.Frozen),
            $"{b.Id}: {b.State}, {(b.OnFormula ? "under formula" : b.Craft.Frozen ? "frozen" : "awake")}"));
        Assert.All(watched, b => Assert.True(!b.OnFormula && b.Craft.Frozen, $"watched {b.Id} is not frozen in the physics"));
        Assert.True(under > 0, "none left the physics");
        Assert.True(sim.Physics.TileCount < 30);
    }

    [Fact]
    public void An_upright_craft_gets_its_tiles_by_its_bottom_ends_height()
    {
        // A booster upright over the Boreal Sea's frozen sheet (flat, and far from the pad), still,
        // its body's bottom a little under the tiles' reach and its middle over it: its height is
        // measured from its ends as well as its middle, so it has its tiles; 20 m higher, out of
        // reach at both ends, it has none. (Only its middle measured, it had none at either height:
        // and one lying across a cliff's edge, its end on the ground and its middle over deep
        // water, lost the ground under its end.)
        var sheet = IceTests.SheetMiddle("Boreal Sea");
        var ground = Planet.Centre + sheet * (Planet.SeaRadius + Terrain.Height(sheet));
        var up = Planet.Up(ground);
        int Tiles(float bottom)
        {
            var state = Standing(0, sheet, 0f) with { Position = ground + up * bottom, Velocity = Vector3.Zero };
            using var sim = new Simulation(world: World([state]));
            Settle(sim, watched: true); // its tiles come from the worker (watched, so in the physics as it falls)
            return sim.Physics.TileCount;
        }

        var middle = RocketGeometry.StackHeight(Booster, 0, 0) / 2f;
        Assert.True(middle > 6f, $"its middle is only {middle} m up its body");
        var (within, beyond) = (Tiles(Scene.TerrainTileReach - 4f), Tiles(Scene.TerrainTileReach + 20f));
        output.WriteLine($"its bottom {Scene.TerrainTileReach - 4f} m up (its middle {Scene.TerrainTileReach - 4f + middle:0} m): {within} tiles; " +
                         $"{Scene.TerrainTileReach + 20f} m up: {beyond} (the rocket on the pad's)");
        Assert.True(within > beyond, $"{within} tiles with its bottom in reach, {beyond} out of it");
    }

    [Fact]
    public void A_craft_over_a_floe_far_above_the_seabed_has_no_tiles()
    {
        // A booster upright 20 m over a floe where the sea is far deeper than the tiles' reach: the
        // tiles are the seabed (the floe is a body of its own), which it is far out of reach of,
        // so it has none under it. (Measured from the floe's top, as its legs are, it had the
        // seabed laid under it, every floe it crossed, and a fleet crossing the floe band spent
        // most of its frames laying tiles nothing could reach.)
        var floe = Floes.All.First(f => -Terrain.Height(Planet.Up(f.Centre)) > Scene.TerrainTileReach + 100f);
        var up = Planet.Up(floe.Centre);
        var state = Standing(0, up, 0f) with { Position = floe.Centre + up * (Scene.FloeFreeboard + 20f), Velocity = Vector3.Zero };
        using var sim = new Simulation(world: World([state]));
        sim.Step(Watching(sim)); // watched, so in the physics, whose tiles this is about
        var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
        Assert.False(booster.OnFormula);
        output.WriteLine($"{-Terrain.Height(up):0} m of water under floe {floe.Id}; {Planet.GroundAltitude(booster.Position):0.0} m over its top; {sim.Physics.TileCount} tiles in all");
        Assert.False(sim.Physics.HasTile(TerrainTiles.CellOf(GroundBody.Planet, booster.Position - Planet.Centre)), "the seabed is laid under it");
    }

    [Fact]
    public void A_craft_far_above_the_ground_has_its_tiles_made_by_the_worker_not_in_the_step()
    {
        // The booster upright and still over the Boreal Sea's frozen sheet, its bottom a little
        // under the tiles' reach: it needs no tile within a quarter second, so the step makes none
        // of its; the worker does, a moment later. (Made in the step, a craft coming into reach cost
        // a step 10 ms or more, and a fleet of them a frame's spike.)
        var sheet = IceTests.SheetMiddle("Boreal Sea");
        var ground = Planet.Centre + sheet * (Planet.SeaRadius + Terrain.Height(sheet));
        var up = Planet.Up(ground);
        Simulation Over(float bottom)
        {
            // In the physics from the start (far from the pad, it would load under formula), as
            // its tiles in the first step are what this is about.
            var sim = new Simulation(world: World([Standing(0, sheet, 0f) with { Position = ground + up * bottom, Velocity = Vector3.Zero }]));
            foreach (var v in sim.Flight.WorldVessels)
                sim.Flight.BringIn(v);
            return sim;
        }

        long pad; // the rocket on the pad's, made in the step: it stands on the ground
        using (var alone = Over(Scene.TerrainTileReach + 20f))
        {
            alone.Step(Watching(alone));
            pad = alone.Physics.Tiles.MadeInStep;
        }
        // Watched, so it stays in the physics as it falls, its tiles laid under it.
        using var sim = Over(Scene.TerrainTileReach - 4f);
        var tiles = sim.Physics.Tiles;
        sim.Step(Watching(sim));
        var (made, laid) = (tiles.MadeInStep, sim.Physics.TileCount);
        Assert.True(Waits.Until(() => tiles.Worker.Finished > 0, Patience), "the worker made none of its tiles");
        Assert.True(sim.Run(WatchingThem, _ => sim.Physics.TileCount > laid, maxSeconds: 2), "none of the worker's tiles was laid");
        output.WriteLine($"made in the step: {made} (the rocket on the pad's alone: {pad}); then {tiles.FromWorker} from the worker, {sim.Physics.TileCount - laid} more tiles");
        Assert.Equal(pad, made);
        Assert.True(tiles.FromWorker > 0);
        Assert.Equal(pad, tiles.MadeInStep); // and still none of its in the step
    }

    [Fact]
    public void A_cell_the_worker_has_finished_is_laid_from_its_shape_when_it_turns_urgent()
    {
        // A booster high over the Boreal Sea's sheet wants its cells of the worker, which finishes
        // them; before they are laid, a booster landed right under it wakes, and its own cells, the
        // same ones, are urgent. The worker's shapes are taken first, so they are laid from those,
        // not made again in the step, one body each, and none is left kept. Both are loaded frozen
        // and woken by hand, each wake one update of the tiles, so nothing takes the worker's
        // shapes in between, however fast the worker is.
        var sheet = IceTests.SheetMiddle("Boreal Sea");
        var landed = Standing(0, sheet, 0f, FlightState.Landed);
        var high = landed with { Id = 1, Position = landed.Position + sheet * (Scene.TerrainTileReach - 4f) };
        using var sim = new Simulation(world: World([landed, high]));
        var tiles = sim.Physics.Tiles;
        var (below, above) = (sim.Flight.WorldVessels.First(v => v.Id == 0), sim.Flight.WorldVessels.First(v => v.Id == 1));
        sim.Flight.BringIn(below); // in the physics, frozen (far from the pad, they load under formula)
        sim.Flight.BringIn(above);
        Assert.True(below.Craft.Frozen && above.Craft.Frozen);
        sim.Physics.Wake(above.Craft); // wants its cells of the worker
        var cells = above.Craft.TileCells.ToArray();
        Assert.NotEmpty(cells);
        Assert.All(cells, c => Assert.False(sim.Physics.HasTile(c), "the high booster's tile was laid in the step"));
        WaitForWorker(sim);
        var finished = tiles.Worker.Finished;
        Assert.True(finished >= cells.Length, $"{finished} finished for its {cells.Length} cells");

        var (count, others) = (sim.Physics.TileCount, sim.Physics.BodyCount - sim.Physics.TileCount);
        var (made, fromWorker, fromKept) = (tiles.MadeInStep, tiles.FromWorker, tiles.FromKept);
        sim.Physics.Wake(below.Craft); // its cells (the same ones) urgent: laid from the worker's shapes
        var under = below.Craft.TileCells.Where(cells.Contains).ToArray();
        output.WriteLine($"{cells.Length} cells ({finished} finished, {under.Length} under the landed one); tiles {count} before the wake, " +
            $"{sim.Physics.TileCount} after; {tiles.MadeInStep - made} made in the step, {tiles.FromWorker - fromWorker} from the worker, {tiles.FromKept - fromKept} from kept shapes");
        Assert.NotEmpty(under);
        Assert.Equal(0, tiles.Worker.Finished);
        Assert.Equal(made, tiles.MadeInStep); // none of its made again in the step
        Assert.Equal(fromKept, tiles.FromKept);
        Assert.Equal(finished, tiles.FromWorker - fromWorker); // every finished shape laid, the urgent ones' among them
        Assert.Equal(count + finished, sim.Physics.TileCount); // each cell once ...
        Assert.Equal(others, sim.Physics.BodyCount - sim.Physics.TileCount); // ... and no body besides
        Assert.All(cells, c => Assert.True(sim.Physics.HasTile(c) && !tiles.IsKept(c), $"{c}: laid {sim.Physics.HasTile(c)}, kept {tiles.IsKept(c)}"));
    }

    [Fact]
    public void A_frozen_craft_about_to_wake_has_its_ground_made_ahead_of_need()
    {
        // A craft of the test's own (no body: only the tiles see it) frozen on open ground: it needs
        // nothing under it, until something on the move comes near enough that it will wake soon
        // (Craft.WakingSoon); then its cells are wanted of the worker, never made in the step, and
        // laid as the worker finishes them, so a field woken together lays shapes made already.
        var d = LandPoints(19, 8000f, slope => slope < 4f).First();
        var ground = Planet.Centre + d * (Planet.SeaRadius + Terrain.Height(d));
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var settings = SphereShapeSettings.Create(1f);
        using var craft = new Craft(Booster, 0, 0, uint.MaxValue, ((ShapeSettings)settings).CreateShape(), null) { Frozen = true };
        (Vector3, Vector3, Vector3) Motion(Craft _) => (ground + d * 5f, Vector3.Zero, d);
        var tiles = physics.Tiles;
        var cell = TerrainTiles.CellOf(GroundBody.Planet, d);

        tiles.Update([craft], Motion);
        Assert.False(physics.HasTile(cell), "frozen and not about to wake, it was given ground");
        Assert.Equal(0, physics.TileCount);

        craft.WakingSoon = true;
        tiles.Update([craft], Motion); // its cells wanted of the worker
        Assert.Equal(0, tiles.MadeInStep);
        Assert.True(Waits.Until(() => { tiles.Update([craft], Motion); return physics.HasTile(cell); }, Patience), "its ground was never laid");
        output.WriteLine($"about to wake: {physics.TileCount} tiles laid, {tiles.FromWorker} from the worker, {tiles.MadeInStep} made in the step");
        Assert.Equal(0, tiles.MadeInStep);
        Assert.True(tiles.FromWorker > 0);
    }

    [Fact]
    public void The_urgent_stretch_shortens_with_the_step()
    {
        // A craft of the test's own (no body: only the tiles see it) 5 m over open ground at the
        // top speed, predicted to collide the ground a quarter second on (Craft.GroundAhead, as the
        // look ahead hands a fast craft its ground): its urgent cells (those made in the step) are
        // those of its way in due within a quarter second's steps, so with the step shortened
        // twentyfold (a forced slow-down), those due within a twentieth of that, the same real
        // time ahead; the rest of its way in is the worker's.
        var d = LandPoints(19, 8000f, slope => slope < 4f).First();
        var ground = Planet.Centre + d * (Planet.SeaRadius + Terrain.Height(d));
        var level = Vector3.Normalize(Vector3.Cross(d, MathF.Abs(d.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        long Urgent(float stepScale)
        {
            using var physics = new RocketPhysics(GroundBody.Planet);
            using var settings = SphereShapeSettings.Create(1f);
            using var craft = new Craft(Booster, 0, 0, uint.MaxValue, ((ShapeSettings)settings).CreateShape(), null);
            var hits = Scene.TopSpeed * Scene.TerrainTileUrgentSeconds; // a quarter second on
            (Vector3, Vector3, Vector3) Motion(Craft _) => (ground + d * 5f, level * Scene.TopSpeed, d);
            craft.GroundAhead = (ground + level * hits, level, Scene.TopSpeed, Scene.TerrainTileUrgentSeconds);
            physics.Tiles.Update([craft], Motion, stepScale);
            return physics.Tiles.MadeInStep;
        }
        var (full, slowed) = (Urgent(1f), Urgent(0.05f));
        output.WriteLine($"at {Scene.TopSpeed:0} m/s, 5 m up: {full} cells made in the step at full steps, {slowed} with them at a twentieth");
        Assert.True(full >= 20, $"only {full} urgent cells at full steps: this shows little");
        Assert.True(slowed * 5 <= full, $"{slowed} urgent cells slowed, {full} at full steps");
    }

    [Fact]
    public void A_late_worker_shape_for_a_cell_already_laid_is_kept_with_no_second_body()
    {
        // A worker's shape can come for a cell laid meanwhile (made in the step as urgent, while
        // the worker had it in hand): it is kept, for when the tile is taken out and wanted again,
        // never laid as a second body. A craft of the test's own (no body: only the tiles see it)
        // low over open ground has its cells laid in the step; one of them is then handed to the
        // real worker, as a round of wants, and finished; one more update takes it. No step runs
        // between, so the tiles' clock stands still and nothing lapses.
        var d = LandPoints(19, 8000f, slope => slope < 4f).First();
        var ground = Planet.Centre + d * (Planet.SeaRadius + Terrain.Height(d));
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var settings = SphereShapeSettings.Create(1f);
        using var craft = new Craft(Booster, 0, 0, uint.MaxValue, ((ShapeSettings)settings).CreateShape(), null);
        (Vector3, Vector3, Vector3) Motion(Craft _) => (ground + d * 5f, Vector3.Zero, d);
        var tiles = physics.Tiles;
        tiles.Update([craft], Motion); // its cells urgent: laid in the step
        var cell = TerrainTiles.CellOf(GroundBody.Planet, d);
        Assert.True(physics.HasTile(cell), "the cell under it wasn't laid");
        Assert.False(tiles.IsKept(cell));

        Assert.True(Waits.Until(() => tiles.Worker.TryWant([(cell, 0)], 0), Patience), "the worker held its lock all along");
        Assert.True(Waits.Until(() => tiles.Worker.Finished == 1 && !tiles.Worker.Busy, Patience), "the worker never finished it");
        var (count, others) = (physics.TileCount, physics.BodyCount - physics.TileCount);
        var (made, fromWorker, fromKept) = (tiles.MadeInStep, tiles.FromWorker, tiles.FromKept);
        tiles.Update([craft], Motion); // takes the late shape
        output.WriteLine($"{count} tiles before, {physics.TileCount} after; {physics.BodyCount - physics.TileCount} other bodies (were {others}); " +
            $"the cell laid {physics.HasTile(cell)}, its shape kept {tiles.IsKept(cell)}; {tiles.FromWorker - fromWorker} laid from the worker");
        Assert.Equal(0, tiles.Worker.Finished);
        Assert.Equal(count, physics.TileCount);
        Assert.Equal(others, physics.BodyCount - physics.TileCount); // no second body
        Assert.True(physics.HasTile(cell) && tiles.IsKept(cell), "the late shape wasn't kept");
        Assert.Equal((made, fromWorker, fromKept), (tiles.MadeInStep, tiles.FromWorker, tiles.FromKept));
    }

    [Fact]
    [Trait("Run", "Alone")] // its wait on the worker (below normal priority) is in real time: beside the other shards it can be starved past it
    public void Low_and_fast_over_rough_ground_and_coming_down_onto_it_nothing_falls_through()
    {
        // The rough-ground perf scene's six boosters (legs stowed, engines off), thrown up and out
        // at 20 m/s up and 80 m/s across, 60 degrees apart, from 60 m over a rough slope: low and
        // fast all the way, then coming down onto it. Each frame, every one still flying near the
        // ground has its tile laid under its lowest point, and is above the ground there, so none
        // passed through, with the worker laying ahead of them what the step doesn't. (The worker
        // is waited for once, after the first frame, so it has laid some whatever the machine's
        // speed: the flight runs many times faster than real time.)
        var slope = Drops.FindSlope(15f, 35f).At;
        var (east, north) = (Planet.East(slope), Planet.North(slope));
        var states = new VesselState[6];
        for (var i = 0; i < states.Length; i++)
        {
            var (s, c) = MathF.SinCos(MathF.Tau * i / states.Length);
            var across = east * c + north * s;
            states[i] = Drops.Placed(slope + across * 30f, across, speed: -20f, sideways: across * 80f, clearance: 60f, legsOut: false, id: i);
        }
        using var sim = new Simulation(Mission.LaunchAndLand, world: new WorldState { Vessels = states, NextVesselId = states.Length });
        // All watched (the first flown), so all fly in the physics, over the tiles this is about.
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0), Watched: Watching(sim).Watched));
        WaitForWorker(sim);
        var boosters = sim.Flight.WorldVessels.Where(v => v.Id < states.Length).ToArray();
        var flying = boosters.Select(_ => true).ToArray();
        var lowest = boosters.Select(_ => float.MaxValue).ToArray();
        var bare = boosters.Select(_ => 0).ToArray(); // frames near the ground with no tile under it
        var down = sim.Run(WatchingThem, _ =>
        {
            Assert.All(boosters, b => Assert.False(b.OnFormula));
            for (var i = 0; i < boosters.Length; i++)
                if (flying[i]) // and the frame it came down in
                {
                    lowest[i] = MathF.Min(lowest[i], Drops.Lowest(boosters[i]));
                    if (!Drops.TileUnder(sim, boosters[i]))
                        bare[i]++;
                    flying[i] = boosters[i].State == FlightState.Flying;
                }
            return !flying.Any(f => f);
        }, maxSeconds: 15);
        var tiles = sim.Physics.Tiles;
        for (var i = 0; i < boosters.Length; i++)
            output.WriteLine($"{i}: {boosters[i].State} {boosters[i].CrashReason}; at its lowest {lowest[i]:0.00} m above the ground; {bare[i]} frames near it with no tile under it");
        output.WriteLine($"tiles: {tiles.MadeInStep} made in the step, {tiles.FromWorker} from the worker, {tiles.FromKept} from kept shapes");
        Assert.True(down, "not all came down");
        Assert.All(bare, b => Assert.True(b == 0, $"one was near the ground with no tile under it for {b} frames"));
        Assert.All(lowest, l => Assert.True(l > -1f, $"one went {-l:0.00} m into the ground"));
        Assert.True(tiles.FromWorker > 0, "the worker laid none");
    }

    [Fact]
    public void A_craft_flying_level_at_a_steep_slope_hits_it_and_never_passes_through()
    {
        // A booster (legs stowed, engine off) flying level at 300 m/s, the realistic top, straight
        // up a steep mountainside it hits within about a second: at the start it is far higher
        // over the ground under it than the urgent height (30 m and its quarter second's travel),
        // so the ground under it alone doesn't make its cells urgent; the ground ahead does (the
        // urgency is judged along its stretch too). It hits the face, and never goes into it.
        const float speed = 300f, back = 300f;
        var half = RocketGeometry.StackHeight(Booster, 0, 0) / 2f;
        var within = Scene.TerrainTileUrgent + speed * Scene.TerrainTileUrgentSeconds;
        Vector3? found = null;
        var uphill = Vector3.Zero;
        foreach (var d in LandPoints(17, 8000f, s => s is > 30f and < 60f).Take(4000))
        {
            var normal = Terrain.Normal(d);
            var up = -Vector3.Normalize(normal - d * Vector3.Dot(normal, d));
            var top = Terrain.Height(d);
            float GroundBack(float metres) => Terrain.Height(Vector3.Normalize(d * Planet.Radius - up * metres));
            // Its bottom end flies at the face's height there: from 300 m back, clear of the
            // ground all the way to it, and starting well over the urgent height above it.
            if (top - GroundBack(back) < within + 20f)
                continue;
            var clear = true;
            for (var m = 8f; m <= back && clear; m += 4f)
                clear = GroundBack(m) < top - 2f;
            if (clear)
            {
                (found, uphill) = (d, up);
                break;
            }
        }
        Assert.True(found.HasValue, "no steep face to fly at");
        var face = found!.Value;
        var heading = Vector3.Normalize(uphill - face * Vector3.Dot(uphill, face));
        var middle = Planet.Centre + face * (Planet.SeaRadius + Terrain.Height(face) + half);
        var start = middle - heading * back;
        var state = Drops.Placed(face, uphill, 0f, legsOut: false) with
        {
            Position = start - Vector3.Transform(Vector3.UnitY, Planet.UprightAt(start)) * half, // its origin, the bottom of its body
            Rotation = Planet.UprightAt(start), Aim = Planet.Up(start), Velocity = heading * speed,
        };
        var above = Planet.Altitude(start) - Terrain.Height(Planet.Up(start)) - half;
        output.WriteLine($"a {Slope(face):0}° face {Terrain.Height(face):0} m up; it starts {above:0} m over the ground under it (urgent under {within:0} m)");
        Assert.True(above > within, $"it starts only {above:0} m over the ground under it");
        using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
        var (lowest, bare) = (float.MaxValue, 0);
        var crashed = sim.Run(WatchingThem, _ => // watched: the physics' tiles and collider are what is tested
        {
            Assert.False(booster.OnFormula);
            lowest = MathF.Min(lowest, Drops.Lowest(booster));
            if (!Drops.TileUnder(sim, booster))
                bare++;
            return booster.State == FlightState.Crashed;
        }, maxSeconds: 5);
        var tiles = sim.Physics.Tiles;
        output.WriteLine($"{booster.State} {booster.CrashReason} after {sim.Time:0.00} s; at its lowest {lowest:0.00} m above the ground; " +
            $"{bare} frames near it with no tile under it; tiles: {tiles.FromWorker} from the worker, {tiles.MadeInStep} made in the step, {tiles.FromKept} from kept shapes");
        Assert.True(crashed, "it never hit");
        Assert.True(bare == 0, $"it was near the ground with no tile under it for {bare} frames");
        Assert.True(sim.Time > 0.5, $"it was judged crashed after {sim.Time:0.00} s, before it could have reached the face");
        Assert.StartsWith("Hit the ground", booster.CrashReason);
        Assert.True(lowest > -1f, $"it went {-lowest:0.00} m into the ground");
    }

    [Theory]
    [InlineData(100f, 90f)] [InlineData(100f, 45f)] [InlineData(100f, 10f)]
    [InlineData(300f, 90f)] [InlineData(300f, 45f)] [InlineData(300f, 10f)]
    [InlineData(1000f, 90f)] [InlineData(1000f, 45f)] [InlineData(1000f, 10f)]
    [InlineData(3000f, 90f)] [InlineData(3000f, 45f)] [InlineData(3000f, 10f)]
    public void A_craft_crashing_into_rough_ground_hits_it_and_never_passes_through(float speed, float degreesBelowLevel)
    {
        // Up to 3,000 m/s, straight down, at 45° and grazing at 10°: a booster
        // (legs stowed, engine off) aimed at the rough slope (Drops), coming at it from its downhill
        // side, heading down at that angle to the level there. It starts a second and a half's
        // travel back along that line, so it has its full second's lookahead in the air before it
        // hits (the air slows the fastest a little, so they arrive a little later); judged crashed
        // sooner, it was judged so in the air. Every frame it is within a frame's travel of the
        // ground, its tile is laid under its lowest point: it is judged to have hit the ground from
        // the terrain's heights, tile or no tile, and the slower ones come down too slowly to go far
        // into it in a frame, so neither the verdict nor how deep it got would show one missing.
        var (state, start) = Drops.Crashing(speed, degreesBelowLevel);
        Assert.True(Planet.GroundAltitude(start) > 10f, $"it starts {Planet.GroundAltitude(start):0} m above the ground");
        using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
        var (lowest, bare) = (float.MaxValue, 0);
        var crashed = sim.Run(WatchingThem, _ => // watched: the physics' tiles and collider are what is tested
        {
            Assert.False(booster.OnFormula);
            lowest = MathF.Min(lowest, Drops.Lowest(booster));
            if (!Drops.TileUnder(sim, booster))
                bare++;
            return booster.State == FlightState.Crashed;
        }, maxSeconds: 10);
        var tiles = sim.Physics.Tiles;
        output.WriteLine($"{speed} m/s at {degreesBelowLevel}°, from {Planet.GroundAltitude(start):0} m up: {booster.State} {booster.CrashReason} after {sim.Time:0.00} s; " +
            $"at its lowest {lowest:0.00} m above the ground; {bare} frames near it with no tile under it; " +
            $"tiles: {tiles.FromWorker} from the worker, {tiles.MadeInStep} made in the step, {tiles.FromKept} from kept shapes");
        Assert.True(crashed, "it never came down");
        Assert.True(bare == 0, $"it was near the ground with no tile under it for {bare} frames");
        Assert.True(sim.Time > 1.0, $"it was judged crashed after {sim.Time:0.00} s, {lowest:0} m up: before it could have reached the ground");
        Assert.StartsWith("Hit the ground", booster.CrashReason);
        Assert.True(lowest > -1f, $"it went {-lowest:0.00} m into the ground");
    }

    [Fact]
    public void With_the_tiles_full_a_finished_shape_is_kept_not_laid_and_the_worker_rests()
    {
        // Four hundred boosters landed (frozen) on open ground, and one frozen high over the Boreal
        // Sea's sheet, all woken by hand: each wake is one update of the tiles, all at one physics
        // time (no step between), so every tile laid stays in use. Landed ones are woken until room
        // is left for at most ten more tiles (each lays nine at most, all urgent, made in the step);
        // then the high one, whose cells are wanted of the worker; then, its shapes finished, one
        // more landed one, which fills the tiles before the worker's shapes are taken. Those with no
        // room are kept, not laid (none lost, nothing past the cap, nothing thrown); and with no
        // room to be had, nothing more is asked of the worker: it rests.
        var places = LandPoints(16, 8000f, s => s < 4f).Take(400).ToArray();
        var states = places.Select((d, i) => Standing(i, d, 0f, FlightState.Landed)).ToList();
        var sheet = IceTests.SheetMiddle("Boreal Sea");
        var landed = Standing(states.Count, sheet, 0f, FlightState.Landed);
        states.Add(landed with { Position = landed.Position + sheet * (Scene.TerrainTileReach - 4f) });
        using var sim = new Simulation(world: World(states));
        foreach (var v in sim.Flight.WorldVessels)
            sim.Flight.BringIn(v); // each in the physics, frozen (those far from the pad load under formula)
        var tiles = sim.Physics.Tiles;
        var fleet = sim.Flight.WorldVessels.Where(v => v.Flight == 0 && v.Id < places.Length).ToArray();
        var high = sim.Flight.WorldVessels.Single(v => v.Id == places.Length);
        var next = 0;
        while (Scene.MaxTerrainTiles - sim.Physics.TileCount > 10)
        {
            Assert.True(next < fleet.Length - 1, $"the fleet's {sim.Physics.TileCount} tiles don't fill the cap");
            sim.Physics.Wake(fleet[next++].Craft);
        }
        var room = Scene.MaxTerrainTiles - sim.Physics.TileCount;
        sim.Physics.Wake(high.Craft); // its cells wanted of the worker
        var cells = high.Craft.TileCells.Where(c => !sim.Physics.HasTile(c)).ToArray();
        output.WriteLine($"{next} woken: {sim.Physics.TileCount} tiles, room for {room}; the high booster's {cells.Length} cells wanted");
        Assert.InRange(room, 1, 10);
        Assert.True(cells.Length > room, $"only {cells.Length} cells for {room} tiles' room");
        WaitForWorker(sim);
        Assert.True(tiles.Worker.Finished >= cells.Length);

        var fromWorker = tiles.FromWorker;
        sim.Physics.Wake(fleet[next].Craft); // fills the tiles, then takes the worker's shapes
        output.WriteLine($"{sim.Physics.TileCount} tiles; of the high booster's cells {cells.Count(tiles.IsKept)} kept, {cells.Count(sim.Physics.HasTile)} laid; " +
            $"{tiles.FromWorker - fromWorker} laid from the worker; worker busy {tiles.Worker.Busy}");
        Assert.Equal(Scene.MaxTerrainTiles, sim.Physics.TileCount);
        Assert.Contains(cells, tiles.IsKept);
        Assert.All(cells, c => Assert.True(tiles.IsKept(c) || sim.Physics.HasTile(c), $"{c}'s shape was lost"));
        Assert.Equal(0, tiles.Worker.Finished);
        Assert.False(tiles.Worker.Busy, "the worker is still asked for cells there is no room for");

        // And so it stays while they are full: a frame on, still nothing asked of it.
        sim.Step(default);
        output.WriteLine($"a frame on: {sim.Physics.TileCount} tiles; worker busy {tiles.Worker.Busy}, {tiles.Worker.Finished} finished");
        Assert.True(sim.Physics.TileCount <= Scene.MaxTerrainTiles);
        Assert.False(tiles.Worker.Busy || tiles.Worker.Finished > 0, "the worker was asked for cells there is no room for");
    }
}
