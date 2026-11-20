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

    /// <summary>
    /// How many of a float's steps (at the copy's size) Jolt's centre of mass and velocity are from
    /// <paramref name="v"/>'s copy, at worst in any one axis: the copy as the last step left it,
    /// read as it is (not through <see cref="Physics.RocketPhysics.TrueCentreOfMass"/>, which would
    /// take it from Jolt again were they apart), and still kept, as it is for a craft flying free.
    /// </summary>
    private static (double Place, double Speed) Apart(Vessel v)
    {
        var physics = v.Physics!;
        Assert.True(v.Craft.Kept, $"{v.Name}'s copy was let go, flying free");
        var (centre, velocity) = (v.Craft.KeptCentre, v.Craft.KeptVelocity);
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

    /// <summary>
    /// <paramref name="seconds"/> of game time with <paramref name="v"/> under formula, advanced
    /// after each frame (as FormulaTests' Coast), <paramref name="watched"/> kept in the physics.
    /// </summary>
    private static void Coast(Simulation sim, Vessel v, double seconds, Vessel[] watched)
    {
        sim.Run((_, _) => new FlightInput(Watched: watched), f =>
        {
            Formula.Advance(v, f.WorldTime);
            return false;
        }, seconds);
    }

    [Theory]
    [InlineData(false)] // on its orbit by Kepler: the Satellites preset's 60° satellite
    [InlineData(true)]  // stepped: low over the moon, under its highest ground's height
    public void A_vessel_handed_to_formula_and_back_keeps_its_place_in_double(bool low)
    {
        // Flown free in the physics a second (its copy stepped), handed to the formula, coasted
        // ten seconds and handed back: the formula goes on from the copy itself, and the copy it
        // comes back with is where the formula had it, in double (made from its float place, it
        // would be off by a float's rounding: some 2 mm at the planet's satellites).
        var world = low
            ? new WorldState { Time = 0, Vessels = [LowPass.Start(0, Moon, 0, 1)], NextVesselId = 1, Flights = 1 }
            : WorldPresets.Satellites();
        using var sim = new Simulation(Mission.FreePlay, world: world);
        foreach (var w in sim.Flight.WorldVessels)
            sim.Flight.BringIn(w);
        var v = sim.Flight.WorldVessels.Single(x => x.Id == (low ? 0 : 2));
        var all = sim.Flight.WorldVessels.ToArray();
        sim.Run((_, _) => new FlightInput(Watched: all), _ => false, maxSeconds: 1);
        Assert.False(v.OnFormula);
        Assert.True(v.Craft.Kept, "flying free, its copy should be kept");
        var (copy, copyVelocity) = (v.Physics!.TrueCentreOfMass(v.Craft), v.Physics.TrueVelocity(v.Craft));

        v.LeavePhysics(sim.Flight.WorldTime);
        Assert.True(v.OnFormula);
        Assert.Equal(low, v.Motion.Stepping);
        Assert.Equal(copy, v.TrueCentreOfMass);
        Assert.Equal(copyVelocity, v.TrueVelocity);
        Coast(sim, v, 10, [.. all.Where(o => o != v)]);
        Assert.True(v.OnFormula);
        Assert.Equal(FlightState.Flying, v.State);
        Assert.Equal(low, v.Motion.Stepping);
        var (at, velocity) = (v.TrueCentreOfMass, v.TrueVelocity);
        Assert.True((at - copy).Length() > 100, "it should have coasted on");
        if (low)
            Assert.NotEqual(at, (Vector3Double)at.ToSingle()); // stepped in double: more than its float holds

        v.ReturnToPhysics(sim.Flight.WorldTime);
        Assert.False(v.OnFormula);
        Assert.True(v.Craft.Kept, "back in the physics, its copy should be kept");
        var (off, offSpeed) = ((v.Craft.KeptCentre - at).Length(), (v.Craft.KeptVelocity - velocity).Length());
        output.WriteLine($"back {off:0.000e0} m and {offSpeed:0.000e0} m/s from where the formula had it");
        Assert.InRange(off, 0, 1e-6);
        Assert.InRange(offSpeed, 0, 1e-9);
        // And flown on from there by the physics, the copy kept and Jolt within its rounding.
        sim.Run((_, _) => new FlightInput(Watched: all), _ => false, maxSeconds: 0.5);
        var (place, speed) = Apart(v);
        Assert.InRange(place, 0, 2);
        Assert.InRange(speed, 0, 2);
    }

    [Fact]
    public void A_stepped_vessel_under_formula_holds_its_orbit()
    {
        // The lander alone low over the moon, under its highest ground's height (so the formula
        // steps it, never on its orbit by Kepler), burning a little (prograde, a second at a 50th
        // of its thrust, till its tank is dry), then coasting two laps. Each lap's mean size (the
        // size worked out at each step swings some 15 mm round a lap) is the one before's, within
        // the millimetre the physics' copy holds an orbit to a lap (WorldTests' creep test). With
        // its pull read where it is rather than where its drift takes it, the formula gains some
        // 34 m a lap; stepped in floats with its pull read there, its size read a lap on is some
        // 0.15 m out (0.03 m of that the size's swing, read at one moment rather than a lap's mean).
        const float Throttle = 0.02f, BurnSeconds = 1f;
        var stage = LanderTests.Alone.Stages[0];
        var start = LowPass.Start(0, Moon, 0, 3) with { Throttle = Throttle, Fuel = [stage.FuelMass / stage.BurnSeconds * Throttle * BurnSeconds] };
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Time = 0, Vessels = [start], NextVesselId = 1, Flights = 1 });
        var v = sim.Flight.WorldVessels.Single(x => x.Id == 0);
        static double Size(in FormulaMotion m) => Elements(Moon, m.At, m.Velocity).A;
        var before = Elements(Moon, start.CentreOfMass, start.Velocity).A;
        Assert.True(sim.Run((_, _) => default, _ => v.OnFormula && !v.EngineLit, maxSeconds: 10), "it should have burned its tank dry under formula");
        Assert.True(v.Motion.Stepping);
        var burned = Size(v.Motion);
        Assert.True(burned - before > 0.1, $"the burn should have raised its orbit: {burned - before:0.000} m");

        var lap = Math.Tau * Math.Sqrt(burned * burned * burned / Moon.Mu);
        var steps = (int)Math.Round(lap / Scene.FormulaStepSeconds);
        var t = sim.Flight.WorldTime;
        var means = new double[2];
        for (var n = 0; n < means.Length; n++)
        {
            var (sum, lo, hi) = (0.0, double.MaxValue, double.MinValue);
            for (var i = 0; i < steps; i++)
            {
                Formula.Advance(v, t += Scene.FormulaStepSeconds);
                Assert.True(v.Motion.Stepping, "low, it should be stepped all the way round");
                var a = Size(v.Motion);
                (sum, lo, hi) = (sum + a, Math.Min(lo, a), Math.Max(hi, a));
            }
            means[n] = sum / steps;
            output.WriteLine($"lap {n + 1}: mean size {means[n]:0.000000} m, swinging {hi - lo:0.000000} m");
        }
        Assert.Equal(FlightState.Flying, v.State);
        output.WriteLine($"raised {burned - before:0.000} m by the burn; the second lap's mean {means[1] - means[0]:0.000000} m from the first's");
        Assert.InRange(means[1] - means[0], -0.001, 0.001);
    }

    [Fact]
    public void A_landing_is_unchanged()
    {
        // The autopilot's whole mission onto the landing zone: once it touches, the copy plays no
        // part (it is let go, and Jolt's floats are its motion), so its touchdown is held within
        // 0.01 m/s of the one Jolt's floats alone gave (2.444340 m/s); the copy's flight down to it
        // moves it by a few mm/s.
        using var sim = new Simulation();
        Assert.True(sim.RunToEnd(new Autopilot().Fly), "the flight never ended");
        output.WriteLine($"{sim.Flight.State}, touchdown {sim.Flight.TouchdownSpeed:0.000000} m/s");
        Assert.Equal(FlightState.Landed, sim.Flight.State);
        Assert.InRange(sim.Flight.TouchdownSpeed, 2.444340f - 0.01f, 2.444340f + 0.01f);
    }
}
