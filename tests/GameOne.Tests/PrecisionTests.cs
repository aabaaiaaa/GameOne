using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Each craft's place and velocity kept in double by the physics (<see cref="Physics.RocketPhysics.TrueCentreOfMass"/>):
/// stepped by the game while it flies free and handed to Jolt rounded once a step, so Jolt and the
/// copy never part by more than a float's rounding; taken from Jolt again across a stage parting;
/// carried across a crossing in double; and a landing, all Jolt's once it touches, unchanged.
/// </summary>
public sealed class PrecisionTests(ITestOutputHelper output)
{
    private static GroundBody Home => GroundBody.Planet;
    private static GroundBody Moon => GroundBody.Moon;
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>A float's step at <paramref name="size"/>: the gap from it to the next float up.</summary>
    private static double FloatStep(double size)
    {
        var f = (float)size;
        return MathF.BitIncrement(f) - f;
    }

    /// <summary>How many of a float's steps (at the copy's size) Jolt's centre of mass and velocity are from <paramref name="v"/>'s copy, at worst in any one axis.</summary>
    private static (double Place, double Speed) Apart(Vessel v)
    {
        var physics = v.Physics!;
        var (centre, velocity) = (physics.TrueCentreOfMass(v.Craft), physics.TrueVelocity(v.Craft));
        var (jolt, joltVelocity) = (physics.CentreOfMass(v.Craft), physics.Velocity(v.Craft));
        static double Worst(Vector3 a, Vector3Double b) => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));
        return (Worst(jolt, centre) / FloatStep(centre.Length()), Worst(joltVelocity, velocity) / FloatStep(velocity.Length()));
    }

    /// <summary>
    /// The size (semi-major axis, negative on a hyperbola) and angular momentum of the orbit round
    /// <paramref name="body"/> of something at <paramref name="p"/> moving at <paramref name="groundVelocity"/>
    /// over its ground, worked out in double.
    /// </summary>
    private static (double A, double H) Elements(GroundBody body, Vector3Double p, Vector3Double groundVelocity)
    {
        Vector3Double spin = body.Spin;
        var r = p - (Vector3Double)body.Centre;
        var v = groundVelocity + Vector3Double.Cross(spin, r);
        return (1 / (2 / r.Length() - v.LengthSquared() / body.Mu), Vector3Double.Cross(r, v).Length());
    }

    /// <summary>
    /// The physics' fall (<see cref="GroundBody.FallStep(Vector3, Vector3, float, float, bool)"/>)
    /// above the air, written out again here in double: how a step changes a velocity at
    /// <paramref name="p"/> over <paramref name="body"/>'s ground.
    /// </summary>
    private static Vector3Double FallStep(GroundBody body, Vector3Double p, Vector3Double velocity, double seconds)
    {
        Vector3Double spin = body.Spin;
        var toCentre = (Vector3Double)body.Centre - p;
        var r2 = toCentre.LengthSquared();
        var pull = toCentre / Math.Sqrt(r2) * (body.Mu / r2) + Vector3Double.Cross(spin, Vector3Double.Cross(spin, toCentre));
        var half = velocity + pull * (seconds / 2);
        var t = spin * seconds;
        var turn = Vector3Double.Cross(half + Vector3Double.Cross(half, t), t) * (2 / (1 + t.LengthSquared()));
        return pull * seconds + turn;
    }

    private static WorldState World(params VesselState[] vessels) =>
        new() { Time = 0.0, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    [Fact]
    public void A_free_craft_s_copy_and_Jolt_never_part_by_more_than_a_float_step()
    {
        // The Satellites preset's 60° satellite, the one that crept most in floats, for a quarter of
        // a lap: the copy is handed to Jolt every step, so a quarter shows what a lap would (the
        // merged orbit test in WorldTests flies the whole lap, for the creep), within the normal
        // tests' time limit.
        using var sim = new Simulation(Mission.LaunchAndLand, world: WorldPresets.Satellites());
        var satellite = sim.Flight.WorldVessels.Where(v => v.Flight == 0).ToArray()[2];
        sim.Step(new FlightInput(SwitchTo: satellite));
        Assert.Same(satellite, sim.Flight.Controlled);
        var lap = Orbit.OverGround(Home, satellite.CentreOfMass, satellite.Velocity).Period;
        var (place, speed, frames) = (0.0, 0.0, 0);
        sim.Run(Nobody, _ =>
        {
            Assert.False(satellite.OnFormula);
            var (p, s) = Apart(satellite);
            (place, speed) = (Math.Max(place, p), Math.Max(speed, s));
            frames++;
            return false;
        }, maxSeconds: lap / 4);
        output.WriteLine($"over {frames} frames, at worst {place:0.00} float steps apart in place, {speed:0.00} in velocity");
        Assert.Equal(FlightState.Flying, satellite.State);
        Assert.InRange(place, 0, 2);
        Assert.InRange(speed, 0, 2);
    }

    [Fact]
    public void A_stage_parting_in_orbit_follows_Jolt()
    {
        // The two-stage rocket whole, coasting round a circle 20 km up: staged, each part's copy is
        // Jolt's from the step after, and goes on from there with no jump (Jolt's pushes apart are
        // the truth, and the copy follows them rather than snapping back to the whole's path).
        var centre = Home.Centre + Home.Up(Scene.RocketStart) * (Home.SeaRadius + 20_000f);
        var velocity = Home.OverGround(centre, Home.East(centre) * Home.CircularSpeed(20_000f));
        var whole = CrossingTests.Craft(0, Home, centre, velocity, Home.Up(centre), first: 0, last: 1, vehicle: Vehicle.TwoStage);
        using var sim = new Simulation(Mission.FreePlay, world: World(whole));
        var rocket = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: rocket));
        sim.Run(Nobody, _ => false, maxSeconds: 1);
        sim.Step(new FlightInput(Stage: true));
        var parts = sim.Flight.WorldVessels.Where(v => v.State == FlightState.Flying).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.All(parts, v => Assert.NotNull(v.Physics));

        double Time() => sim.Flight.WorldTime - sim.Worlds.Lag;
        var before = parts.Select(v => (v.Physics!.TrueCentreOfMass(v.Craft), v.Physics.TrueVelocity(v.Craft))).ToArray();
        var t = Time();
        var (place, speed, jump) = (0.0, 0.0, 0.0);
        for (var frame = 0; frame < 60; frame++)
        {
            sim.Step(default);
            var now = Time();
            for (var i = 0; i < parts.Length; i++)
            {
                var v = parts[i];
                var (p, s) = Apart(v);
                (place, speed) = (Math.Max(place, p), Math.Max(speed, s));
                var (c, u) = (v.Physics!.TrueCentreOfMass(v.Craft), v.Physics.TrueVelocity(v.Craft));
                // Where its own velocity, over the frame, carried it: a few steps of a fall far from a
                // millimetre off, but a snap back to another path would be metres.
                var carried = before[i].Item1 + (before[i].Item2 + u) * ((now - t) / 2);
                jump = Math.Max(jump, (c - carried).Length());
                before[i] = (c, u);
            }
            t = now;
        }
        output.WriteLine($"the parts' copies at worst {place:0.00} float steps from Jolt's place, {speed:0.00} in velocity; at worst {jump * 1000:0.000} mm off where their velocity carried them");
        Assert.InRange(place, 0, 2);
        Assert.InRange(speed, 0, 2);
        Assert.InRange(jump, 0, 0.001);
    }

    [Fact]
    public void Crossing_into_the_moon_s_sphere_carries_the_copy()
    {
        // The upper stage coming at the moon's sphere, as the crossing tests start one: each frame,
        // where the physics' steps (counted) carry it is worked out again in double, changed into
        // the moon's frame at the motion's time as it crosses, and followed on there: the path it
        // should be on, which its copy keeps to.
        var (out_, across) = CrossingTests.Ways();
        const float Aside = 4_500f, Seconds = 3f, Speed = 100f;
        var into = MathF.Sqrt(Moon.SphereRadius * Moon.SphereRadius - Aside * Aside);
        var (at, velocity) = CrossingTests.FromMoon(out_ * (into + Seconds * Speed) + across * Aside, -out_ * Speed, Home);
        using var sim = new Simulation(Mission.FreePlay, world: World(CrossingTests.Craft(0, Home, at, velocity, velocity)));
        var v = sim.Flight.WorldVessels.Single(x => x.Id == 0);
        sim.Step(new FlightInput(SwitchTo: v));
        var planet = v.Physics!;
        var (c, u) = (planet.TrueCentreOfMass(v.Craft), planet.TrueVelocity(v.Craft));
        void Follow(GroundBody body, Physics.RocketPhysics physics, long steps)
        {
            for (var i = 0; i < physics.Steps - steps; i++)
            {
                u += FallStep(body, c, u, physics.StepLength);
                c += u * physics.StepLength;
            }
        }
        while (v.Body != Moon && sim.Time < 10)
        {
            var steps = planet.Steps;
            sim.Step(default);
            Follow(Home, planet, steps);
        }
        Assert.Same(Moon, v.Body);
        var change = Bodies.Change(Home, Moon, sim.Flight.WorldTime - sim.Worlds.Lag);
        (c, u) = (change.Point(c), change.Velocity(c, u));
        var predicted = Elements(Moon, c, u);
        var physics = v.Physics!;
        Assert.NotSame(planet, physics);
        var crossed = Elements(Moon, physics.TrueCentreOfMass(v.Craft), physics.TrueVelocity(v.Craft));
        var (offA, offH) = (Math.Abs(crossed.A - predicted.A), Math.Abs(crossed.H - predicted.H));
        var (place, speed, off) = (0.0, 0.0, 0.0);
        for (var frame = 0; frame < 60; frame++)
        {
            var steps = physics.Steps;
            sim.Step(default);
            Follow(Moon, physics, steps);
            var (p, s) = Apart(v);
            (place, speed) = (Math.Max(place, p), Math.Max(speed, s));
            off = Math.Max(off, (physics.TrueCentreOfMass(v.Craft) - c).Length());
        }
        output.WriteLine($"predicted a {predicted.A:0.000} m, h {predicted.H:0.000} m²/s; across, off by {offA:0.000000} m and {offH:0.000000} m²/s; " +
            $"over the next 60 frames at worst {off * 1000:0.000000} mm off its path; the copy at worst {place:0.00} float steps from Jolt's place, {speed:0.00} in velocity");
        Assert.InRange(place, 0, 2);
        Assert.InRange(speed, 0, 2);
        // Exact across (to a micrometre), and the copy keeps to the path from there. Carried
        // across in floats and read back in them, the orbit round the moon is 0.00024 m out in
        // size and 0.077 m²/s in angular momentum.
        Assert.InRange(offA, 0, 1e-6);
        Assert.InRange(offH, 0, 1e-3);
        Assert.InRange(off, 0, 1e-6);
    }

    [Fact]
    public void A_landing_is_unchanged()
    {
        // The autopilot's whole mission onto the landing zone: once it touches, the copy is Jolt's,
        // so its touchdown is the one Jolt's floats alone give (2.444340 m/s; with the copy, 2.446265).
        using var sim = new Simulation();
        Assert.True(sim.RunToEnd(new Autopilot().Fly), "the flight never ended");
        output.WriteLine($"{sim.Flight.State}, touchdown {sim.Flight.TouchdownSpeed:0.000000} m/s");
        Assert.Equal(FlightState.Landed, sim.Flight.State);
        Assert.InRange(sim.Flight.TouchdownSpeed, 2.444340f - 0.01f, 2.444340f + 0.01f);
    }
}
