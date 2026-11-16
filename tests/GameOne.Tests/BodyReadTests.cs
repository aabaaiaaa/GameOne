using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.JoltApi;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Every read says which body it asks about: a physics is made for a body (<see cref="RocketPhysics.Body"/>),
/// and one on the planet and one on the moon run in one process side by side, each as it would
/// alone; the planet's own values (<see cref="Planet"/>) are fixed; and the one tile worker every
/// world shares makes each world's tiles of that world's ground, whatever else it is making.
/// </summary>
public sealed class BodyReadTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>How many frames each run is stepped for: three seconds, a physics step a frame.</summary>
    private const int Frames = 360;

    /// <summary>A lander dropped onto the moon's landing spot, falling at 3 m/s, in a physics made for the moon.</summary>
    private static Simulation OnTheMoon() =>
        new(world: new WorldState { Vessels = [MoonLandingTests.Lander(3f)], NextVesselId = 1 }, body: GroundBody.Moon);

    /// <summary>A booster coming down onto Home's landing zone at 2 m/s from 1.2 m up, its legs out, in a physics made for the planet.</summary>
    private static Simulation AtHome()
    {
        var zone = LaunchSites.Home.LandingZone!.Value;
        var state = Drops.Placed(zone, Planet.East(zone), 2f, clearance: 1.2f);
        return new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
    }

    /// <summary>
    /// One frame of exactly one physics step (so no tile update runs between two steps unwaited
    /// for), the first switching to the vessel; then every given world's jobs on the tile worker
    /// are waited for until it is idle. So each update takes all the worker was asked for in the
    /// last one, whatever the threads' timing: the run is the same every time (the only way the
    /// worker's timing reaches a run is which tiles are laid in which update).
    /// </summary>
    private static Vector3 Frame(Simulation sim, int frame, params Simulation[] drain)
    {
        var input = frame == 0 ? new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0)) : default;
        sim.Flight.RunFrame(RocketPhysics.StepSeconds, input);
        foreach (var each in drain)
            Assert.True(Waits.Until(() => !each.Physics.Tiles.Worker.Busy, Patience), "the tile worker never went idle");
        return sim.Flight.WorldVessels.First(v => v.Id == 0).Position;
    }

    private static List<Vector3> Alone(Func<Simulation> make)
    {
        using var sim = make();
        var positions = new List<Vector3>();
        for (var i = 0; i < Frames; i++)
            positions.Add(Frame(sim, i, sim));
        return positions;
    }

    [Fact]
    public void A_physics_on_the_planet_and_one_on_the_moon_step_side_by_side_as_each_alone()
    {
        var landerAlone = Alone(OnTheMoon);
        var boosterAlone = Alone(AtHome);

        using var moon = OnTheMoon();
        using var home = AtHome();
        var (lander, booster) = (new List<Vector3>(), new List<Vector3>());
        for (var i = 0; i < Frames; i++)
        {
            lander.Add(Frame(moon, i, moon, home));
            booster.Add(Frame(home, i, moon, home));
        }

        var (landed, homed) = (moon.Flight.Controlled, home.Flight.Controlled);
        output.WriteLine($"the lander {landed.State} on the moon, fell {Vector3.Distance(lander[0], lander[^1]):0.00} m, {moon.Physics.TileCount} tiles; " +
            $"the booster {homed.State} at Home, fell {Vector3.Distance(booster[0], booster[^1]):0.00} m, {home.Physics.TileCount} tiles");
        Assert.Same(GroundBody.Moon, landed.Body);
        Assert.Same(GroundBody.Planet, homed.Body);
        Assert.Equal((FlightState.Landed, FlightState.Landed), (landed.State, homed.State));
        Assert.True(Vector3.Distance(lander[0], lander[^1]) > 0.5f && Vector3.Distance(booster[0], booster[^1]) > 0.5f, "they hardly moved: this shows little");
        Assert.True(moon.Physics.TileCount > 0 && home.Physics.TileCount > 0, "a world laid no ground: this shows little");
        for (var i = 0; i < Frames; i++)
        {
            Assert.True(lander[i] == landerAlone[i], $"frame {i}: the lander at {lander[i]} beside the booster, {landerAlone[i]} alone");
            Assert.True(booster[i] == boosterAlone[i], $"frame {i}: the booster at {booster[i]} beside the lander, {boosterAlone[i]} alone");
        }
    }

    [Fact]
    public void The_planets_values_are_fixed()
    {
        // Nothing of the planet's is set: what can run at any body reads the body it is about, so
        // there is no "body stood on" to set. (A body's own values are its own, never set either.)
        const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var type in new[] { typeof(Planet), typeof(GroundBody) })
        {
            var properties = type.GetProperties(All).Where(p => p.SetMethod is not null).Select(p => p.Name);
            var fields = type.GetFields(All).Where(f => !f.IsInitOnly && !f.IsLiteral && !f.Name.Contains("k__BackingField")).Select(f => f.Name);
            var settable = properties.Concat(fields).ToArray();
            output.WriteLine($"{type.Name}: {type.GetProperties(All).Length} properties, {type.GetFields(All).Length} fields, settable: {string.Join(", ", settable)}");
            Assert.Empty(settable);
        }
    }

    [Fact]
    public void A_tile_made_for_the_moon_is_the_moons_ground_whatever_else_runs()
    {
        using var moon = new RocketPhysics(GroundBody.Moon);
        using var planet = new RocketPhysics(GroundBody.Planet);
        var spot = Vector3.Normalize(MoonLandingTests.LandingSpot() - GroundBody.Moon.Centre);
        var moonCell = TerrainTiles.CellOf(GroundBody.Moon, spot);
        // The planet's cells round Home, due from now to a second on, and the moon's due half way
        // through them (both clocks at 0): the worker makes the moon's among them, not after.
        var planetCells = Enumerable.Range(-3, 7).SelectMany(i => Enumerable.Range(-3, 7).Select(j =>
            TerrainTiles.CellOf(GroundBody.Planet, Vector3.Normalize(new Vector3(i * 0.004f, 1f, j * 0.004f))))).Distinct().ToList();
        Assert.True(Waits.Until(() => planet.Tiles.Worker.TryWant([.. planetCells.Select((c, i) => (c, (double)i / planetCells.Count))], 0), Patience));
        Assert.True(Waits.Until(() => moon.Tiles.Worker.TryWant([(moonCell, 0.5)], 0), Patience));
        Assert.True(Waits.Until(() => !planet.Tiles.Worker.Busy && !moon.Tiles.Worker.Busy, Patience), "the worker never finished");

        var (moonShapes, planetShapes) = (new List<(TerrainTiles.Cell Cell, Shape Shape, float Centre)>(), new List<(TerrainTiles.Cell Cell, Shape Shape, float Centre)>());
        Assert.True(moon.Tiles.Worker.TryTake(moonShapes) && planet.Tiles.Worker.TryTake(planetShapes));
        try
        {
            Assert.Equal(moonCell, Assert.Single(moonShapes).Cell);
            Assert.Equal(planetCells.Count, planetShapes.Count);
            var moonWorst = Worst(GroundBody.Moon, moonShapes[0], MoonGround.Height);
            var planetWorst = planetShapes.Max(made => Worst(GroundBody.Planet, made, Terrain.Height));
            output.WriteLine($"the moon's tile ({moonShapes[0].Cell}, made among {planetShapes.Count} of the planet's): its samples at most {moonWorst * 1000:0.00} mm off the moon's ground; " +
                $"the planet's at most {planetWorst * 1000:0.00} mm off the planet's");
            Assert.True(moonWorst < 0.001f, $"the moon's tile is {moonWorst * 1000:0.00} mm off its ground");
            Assert.True(planetWorst < 0.01f, $"a planet's tile is {planetWorst * 1000:0.00} mm off its ground"); // as TerrainTileTests holds them
        }
        finally
        {
            foreach (var (_, shape, _) in moonShapes.Concat(planetShapes))
                shape.Dispose();
        }
    }

    /// <summary>The furthest any sample of a tile made for <paramref name="body"/> is from that body's ground (<paramref name="height"/>, its own function).</summary>
    private static float Worst(GroundBody body, (TerrainTiles.Cell Cell, Shape Shape, float Centre) made, Func<Vector3, float> height)
    {
        var n = Scene.TerrainTileSamples;
        var up = TerrainTiles.Frame(body, made.Cell, out var across, out var along);
        var origin = body.Centre + up * (body.SeaRadius + made.Centre);
        var field = Unsafe.BitCast<Shape, HeightFieldShape>(made.Shape);
        var worst = 0f;
        for (var x = 0; x < n; x++)
            for (var y = 0; y < n; y++)
            {
                var local = field.GetPosition((uint)x, (uint)y);
                var at = origin + across * local.X + up * local.Y + along * local.Z;
                worst = MathF.Max(worst, MathF.Abs(body.Altitude(at) - height(body.Up(at))));
            }
        return worst;
    }
}
