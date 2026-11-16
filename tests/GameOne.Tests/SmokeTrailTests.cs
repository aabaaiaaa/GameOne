using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The smoke engines leave behind (<see cref="SmokeTrail"/>): an unbroken trail along the path,
/// however far the engine went in a frame; none in thin air; smoke pouring out across the ground
/// where the exhaust reaches it, high ground and the sea as well as the pad; and every puff gone
/// once it has thinned away. And an explosion's smoke (<see cref="Effects"/>) fades into the
/// ground it is on, not the sphere under it.
/// </summary>
public sealed class SmokeTrailTests
{
    private static readonly Vector3 Down = -Vector3.UnitY;

    [Fact]
    public void A_fast_engine_leaves_an_unbroken_trail()
    {
        using var sim = new Simulation();
        var vessel = sim.Flight.Controlled;
        var trail = new SmokeTrail();

        // 200 m up and climbing at 300 m/s: 5 m a frame, more than the puffs' spacing.
        var at = new Vector3(0, 200, 0);
        var time = 0.0;
        for (var i = 0; i < 60; i++, time += 1.0 / 60)
            trail.Emit(vessel, at += Vector3.UnitY * 5f, Down, 1f, 0.8f, time, 1f / 60);
        trail.Update(time);

        var heights = trail.Clouds.ToArray().Select(c => c.Centre.Y).Order().ToArray();
        Assert.True(heights.Length > 100, $"only {heights.Length} puffs along 300 m");
        var gaps = heights.Zip(heights.Skip(1), (a, b) => b - a);
        Assert.True(gaps.Max() < 6f, $"a gap of {gaps.Max():0.0} m in the trail");
    }

    [Fact]
    public void There_is_no_trail_in_thin_air()
    {
        using var sim = new Simulation();
        var trail = new SmokeTrail();
        for (var i = 0; i < 60; i++)
            trail.Emit(sim.Flight.Controlled, new Vector3(0, 2500 + i * 5, 0), Down, 1f, 0.8f, i / 60.0, 1f / 60);
        Assert.Equal(0, trail.Count);
    }

    [Fact]
    public void Exhaust_reaching_the_ground_pours_smoke_out_across_it()
    {
        using var sim = new Simulation();
        var trail = new SmokeTrail();
        // Hovering 10 m over the pad for two seconds, then ten seconds for the smoke to spread.
        var nozzle = new Vector3(0, 10, 0);
        var time = 0.0;
        for (var i = 0; i < 120; i++, time += 1.0 / 60)
            trail.Emit(sim.Flight.Controlled, nozzle, Down, 1f, 0.8f, time, 1f / 60);
        trail.Update(time + 10);

        var low = trail.Clouds.ToArray().Where(c => c.Squash < 0.7f).ToArray(); // the ground's flatter puffs
        Assert.True(low.Length > 20, $"only {low.Length} puffs across the ground");
        var spread = low.Max(c => new Vector2(c.Centre.X, c.Centre.Z).Length());
        Assert.True(spread > 40f, $"the ground smoke only spread {spread:0} m");
        Assert.All(low, c => Assert.True(c.Centre.Y < 15f, $"a ground puff {c.Centre.Y:0} m up"));
    }

    /// <summary>A point on the ground (land, not a pad) well above the sphere: a hill or plateau.</summary>
    private static Vector3 HighGround()
    {
        var rng = new Random(5);
        while (true)
        {
            var d = Vector3.Normalize(new Vector3(rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f));
            if (Terrain.SurfaceHeight(d) > 300f)
                return Planet.Centre + d * (Planet.SeaRadius + Terrain.SurfaceHeight(d));
        }
    }

    /// <summary>
    /// Hovers <paramref name="height"/> over <paramref name="ground"/> for a second, firing straight
    /// down, and returns the ground's flatter puffs as each frame made them (before they have
    /// spread over ground that isn't as high: still at their first size).
    /// </summary>
    private static SmokeCloud[] GroundPuffs(Vector3 ground, float height)
    {
        using var sim = new Simulation();
        var trail = new SmokeTrail();
        var up = Planet.Up(ground);
        var made = new List<SmokeCloud>();
        var time = 0.0;
        for (var i = 0; i < 60; i++, time += 1.0 / 60)
        {
            trail.Emit(sim.Flight.Controlled, ground + up * height, -up, 1f, 0.8f, time, 1f / 60);
            trail.Update(time);
            made.AddRange(trail.Clouds.ToArray().Where(c => c.Squash < 0.7f && c.Radius < 3.1f));
        }
        return [.. made];
    }

    [Fact]
    public void Exhaust_reaching_high_ground_pours_smoke_out_across_it_there()
    {
        var ground = HighGround();
        var low = GroundPuffs(ground, 10f);
        Assert.True(low.Length > 5, $"only {low.Length} puffs across ground {Planet.Altitude(ground):0} m up");
        // Fading into that ground, and made just above it (each has hardly moved yet).
        var radius = Vector3.Distance(ground, Planet.Centre); // a cloud's ground: how far it is from the planet's centre
        Assert.All(low, c => Assert.InRange(c.Ground, radius - 1f, radius + 1f));
        Assert.All(low, c => Assert.InRange(Planet.SurfaceAltitude(c.Centre), 0f, 3f));
    }

    [Fact]
    public void Over_the_sea_the_ground_smoke_starts_on_the_water()
    {
        var east = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(-Vector3.UnitZ, 5000f / Planet.Radius)));
        Assert.True(Terrain.IsSea(east));
        var water = Planet.Centre + east * Planet.SeaRadius;
        var low = GroundPuffs(water, 10f);
        Assert.True(low.Length > 5, $"only {low.Length} puffs across the sea");
        Assert.All(low, c => Assert.InRange(Planet.SurfaceAltitude(c.Centre), 0f, 3f));
    }

    [Fact]
    public void An_explosion_on_a_hill_fades_into_the_hill()
    {
        var ground = HighGround();
        var effects = new Effects();
        effects.Explode(GroundBody.Planet, ground, 0);
        var frames = new BodyFrames();
        frames.Begin(3, GroundBody.Planet);
        effects.Update(3, frames);
        Assert.NotEqual(0, effects.Clouds.Length);
        var radius = Vector3.Distance(ground, Planet.Centre);
        Assert.All(effects.Clouds.ToArray(), c => Assert.InRange(c.Ground, radius - 0.1f, radius + 0.1f));
    }

    [Fact]
    public void An_explosion_where_there_is_no_air_is_its_flash_and_fireballs_alone()
    {
        // On the moon: no pall of smoke at any age, and the fireballs stay where they burst (nothing
        // for hot gas to rise through), drawn where the moon has them in the scene's frame.
        var moon = GroundBody.Moon;
        var at = OnTheMoon.Standing(OnTheMoon.LandingSpot).Position;
        var effects = new Effects();
        effects.Explode(moon, at, 0);
        var frames = new BodyFrames();
        Vector3? first = null;
        foreach (var t in (ReadOnlySpan<double>)[0.05, 0.5, 1.5, 3, 8])
        {
            frames.Begin(t, GroundBody.Planet);
            effects.Update(t, frames);
            Assert.Equal(0, effects.Clouds.Length);
            if (effects.Fireballs.Length == 0)
                continue;
            var middle = Bodies.Change(GroundBody.Planet, moon, t).Point(effects.Fireballs[0].Centre).ToSingle(); // the first burst's, back on the moon
            first ??= middle;
            Assert.True(Vector3.Distance(middle, first.Value) < 0.05f, $"a fireball moved {Vector3.Distance(middle, first.Value):0.00} m on the moon");
        }
        Assert.NotNull(first);
        frames.Begin(0.05, GroundBody.Planet);
        effects.Update(0.05, frames);
        Assert.NotEqual(Vector3.Zero, effects.Flash.Light);
    }

    [Fact]
    public void Puffs_thin_away_and_go()
    {
        using var sim = new Simulation();
        var trail = new SmokeTrail();
        trail.Emit(sim.Flight.Controlled, new Vector3(0, 300, 0), Down, 1f, 0.8f, 0, 1f / 60);
        trail.Update(1);
        Assert.NotEqual(0, trail.Clouds.Length);
        trail.Update(200); // well past a minute and a half
        Assert.Equal(0, trail.Clouds.Length);
    }
}
