using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A flight on a physics made for the moon (<see cref="Simulation"/> with <see cref="GroundBody.Moon"/>),
/// in the moon's own frame: its ground is solid by the ground's rules, so a lander set down softly
/// lands and one dropped hard crashes; it falls by the moon's pull alone, a sixth of the planet's;
/// and what it reads is the moon's, while the planet's own values stay the planet's. Each drop is
/// on the plains at the landing spot, 20° from the moon's north pole (<see cref="Scene.MoonLandingSpot"/>, levelled).
/// </summary>
public sealed class MoonLandingTests(ITestOutputHelper output)
{
    private static GroundBody Moon => GroundBody.Moon;

    /// <summary>The ground at the moon's landing spot, in the moon's frame.</summary>
    internal static Vector3 LandingSpot()
    {
        var (latitude, longitude) = Scene.MoonLandingSpot;
        var direction = CubeSphere.FromLatLon(float.DegreesToRadians(latitude), float.DegreesToRadians(longitude));
        return Moon.Centre + direction * (Moon.SeaRadius + Moon.Height(direction));
    }

    /// <summary>A booster, legs out and engine off, upright with its lowest foot 5 m over the landing spot, falling at <paramref name="speed"/>.</summary>
    internal static VesselState Lander(float speed, int id = 0)
    {
        var at = LandingSpot();
        return Drops.Placed(at, Moon.East(at), speed, clearance: 5f, id: id, body: Moon);
    }

    /// <summary>A world on the moon of one lander (<see cref="Lander"/>), flown.</summary>
    private static Simulation Dropped(float speed)
    {
        var sim = new Simulation(world: new WorldState { Vessels = [Lander(speed)], NextVesselId = 1 }, body: Moon);
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0)));
        return sim;
    }

    [Fact]
    public void A_lander_set_down_softly_on_the_moon_lands()
    {
        using var sim = Dropped(0f);
        var lander = sim.Flight.Controlled;
        sim.Run((_, _) => default, _ => lander.State != FlightState.Flying, 30);
        output.WriteLine($"{lander.State} {lander.CrashReason} after {sim.Time:0.0} s, touchdown {lander.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(lander.TouchdownSpeed < Scene.SafeTouchdownSpeed, $"it came down at {lander.TouchdownSpeed:0.00} m/s");
        // Five metres under a sixth of g is 4 m/s: under the planet's pull it would be 9.9.
        Assert.InRange(lander.TouchdownSpeed, 3f, 5f);
    }

    [Fact]
    public void A_lander_dropped_hard_on_the_moon_crashes()
    {
        using var sim = Dropped(20f);
        var lander = sim.Flight.Controlled;
        sim.Run((_, _) => default, _ => lander.State != FlightState.Flying, 30);
        output.WriteLine($"{lander.State} {lander.CrashReason}, touchdown {lander.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Crashed, lander.State);
    }

    [Fact]
    public void On_the_moon_a_vessel_falls_at_a_sixth_of_g()
    {
        using var sim = new Simulation(world: new WorldState { Vessels = [Lander(0f)], NextVesselId = 1 }, body: Moon);
        var lander = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Flight.BringIn(lander); // in the moon's physics from rest (at another body than the pad's, it loads under formula)
        var (from, up) = (lander.Position, Moon.Up(lander.Position));

        // A second of the physics' steps from rest, the first frame switching to it.
        var steps = (int)MathF.Round(1f / RocketPhysics.StepSeconds);
        sim.Step(new FlightInput(SwitchTo: lander));
        while (sim.Physics.Steps < steps)
            sim.Step(default);
        Assert.Equal(steps, sim.Physics.Steps);

        var fallen = Vector3.Dot(from - lander.Position, up);
        var expected = Scene.MoonSurfaceGravity / 2f; // ½ g t², a second
        output.WriteLine($"fell {fallen:0.0000} m in a second, against {expected:0.0000} (a sixth of g's)");
        Assert.Equal(FlightState.Flying, lander.State);
        Assert.InRange(fallen / expected, 0.98f, 1.02f);
    }

    [Fact]
    public void A_physics_made_for_the_moon_reads_the_moon_and_the_planet_stays_the_planet()
    {
        var home = Terrain.Height(Vector3.UnitY);
        using (var sim = Dropped(0f))
        {
            var lander = sim.Flight.Controlled;
            Assert.Same(Moon, sim.Physics.Body);
            Assert.Same(Moon, lander.Body);
            Assert.Equal(new Vector3(0f, -Scene.MoonRadius, 0f), lander.Body.Centre);
            Assert.False(lander.Body.HasSea || lander.Body.HasAir || lander.Body.HasSites);
            Assert.True(lander.Body.AboveAir(Vector3.Zero));
            Assert.Equal(0f, lander.Body.AirDensity(0f));
            Assert.Equal(MoonGround.Height(Vector3.UnitY), lander.Body.Height(Vector3.UnitY));
            Assert.Equal(lander.Body.MaxHeight, lander.Body.OrbitFloor); // no air: an orbit's floor is its highest ground
        }

        Assert.Equal(new Vector3(0f, -10000f, 0f), Planet.Centre);
        Assert.Equal(Scene.PlanetRadius, Planet.Radius);
        Assert.Equal(GroundBody.PlanetSeaRadius, Planet.SeaRadius);
        Assert.Equal(GroundBody.PlanetMu, Planet.Mu);
        Assert.Equal(GroundBody.PlanetSpinRate, Planet.SpinRate);
        Assert.Equal(Scene.SurfaceGravity, Planet.SurfaceGravity);
        Assert.True(GroundBody.Planet.HasSea && GroundBody.Planet.HasAir && GroundBody.Planet.HasSites);
        Assert.Equal(Scene.AtmosphereHeight, GroundBody.Planet.OrbitFloor);
        Assert.Equal(home, Terrain.Height(Vector3.UnitY));
        Assert.Equal(home, GroundBody.Planet.Height(Vector3.UnitY));
        Assert.False(Planet.AboveAir(Vector3.Zero));
    }

    [Fact]
    public void The_too_fast_line_is_judged_for_the_body_coming_down_on()
    {
        const float height = 10f, low = 30f, flown = 500f;
        // Each body's limit: a landing there survives up to it, by its own pull.
        foreach (var body in Bodies.All)
        {
            var limit = Scene.TouchdownLimit(body, Scene.SafeTouchdownSpeed);
            Assert.Null(Hud.TooFastLine(body, low, -(limit - 0.05f), flown, depth: -5f, height));
            var line = Hud.TooFastLine(body, low, -(limit + 0.05f), flown, depth: -5f, height);
            Assert.Equal($"Too fast to touch down on {body.GroundName}! Throttle up to slow down", line);
            // Only within its height of the ground, and only once it has been up off it.
            Assert.Null(Hud.TooFastLine(body, Scene.TooFastBelow + 1f, -20f, flown, depth: -5f, height));
            Assert.Null(Hud.TooFastLine(body, low, -20f, maxGround: Scene.TooFastBelow - 1f, depth: -5f, height));
        }
        // The moon's limit is by its own pull, not the planet's.
        Assert.True(Scene.TouchdownLimit(Moon, Scene.SafeTouchdownSpeed) < Scene.TouchdownLimit(GroundBody.Planet, Scene.SafeTouchdownSpeed));
        Assert.Contains("the moon", Hud.TooFastLine(Moon, low, -10f, flown, depth: -5f, height));

        // Deep water on the planet takes a splashdown's faster limit, and says so.
        Assert.Null(Hud.TooFastLine(GroundBody.Planet, low, -10f, flown, depth: 50f, height));
        Assert.Equal("Too fast to splash down! Throttle up to slow down", Hud.TooFastLine(GroundBody.Planet, low, -(Scene.SafeSplashdownSpeed + 1f), flown, depth: 50f, height));
        // The moon has no sea: ground below its datum (a crater's floor) is still ground.
        Assert.Equal("Too fast to touch down on the moon! Throttle up to slow down", Hud.TooFastLine(Moon, low, -10f, flown, depth: 50f, height));

        // Shown a frame at a time, it is the same line each time, made once: nothing allocated.
        foreach (var body in Bodies.All)
        {
            var first = Hud.TooFastLine(body, low, -20f, flown, depth: -5f, height);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var again = Hud.TooFastLine(body, low, -20f, flown, depth: -5f, height);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Same(first, again);
        }
    }
}
