using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Hitting the moon: its ground is the ground's kind (<see cref="SolidKind.Ground"/>), named by its
/// body (<see cref="GroundBody.GroundName"/>, "the moon"), so a fast closing on it is warned of, drops
/// the warp once and is slowed for as the planet's ground is; and from beyond its sphere of
/// influence, along the path patched into its pull (<see cref="Patched.Legs"/>), before the vessel
/// crosses. Slow touches are judged by the ground's rules, unwarned, as on the planet: hard, a crash
/// in the world's log; soft, a landing.
/// </summary>
public sealed class MoonCollisionTests(ITestOutputHelper output)
{
    private static GroundBody Moon => GroundBody.Moon;
    private static GroundBody Home => GroundBody.Planet;
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>Presses <see cref="FlightInput.Faster"/> until the player's rate is <paramref name="rate"/>.</summary>
    private static void WarpTo(Simulation sim, float rate)
    {
        while (sim.Flight.PlayerRate < rate)
            sim.Step(new FlightInput(Faster: true));
        Assert.Equal(rate, sim.Flight.PlayerRate);
    }

    private static WorldState World(params VesselState[] vessels) =>
        new() { Time = 0.0, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    /// <summary>A world of <paramref name="state"/> alone, flown from the first frame.</summary>
    private static (Simulation Sim, Vessel Flown) Flown(VesselState state, GroundBody body)
    {
        var sim = new Simulation(Mission.FreePlay, world: World(state), body: body);
        var v = sim.Flight.WorldVessels.Single(x => x.Id == state.Id);
        sim.Step(new FlightInput(SwitchTo: v));
        return (sim, v);
    }

    /// <summary>
    /// The upper stage at the moon, 3.5 km over the plains at its landing spot, its engine off, going
    /// 400 m/s among the moon's stars 70° below level (eastwards): a fast closing on its ground about
    /// 10 s off, past its lead.
    /// </summary>
    private static VesselState Diving()
    {
        var up = OnTheMoon.LandingSpot;
        var at = Moon.Centre + up * (Moon.SeaRadius + Moon.Height(up) + 3_500f);
        var down = 70f * MathF.PI / 180f;
        var stars = (Moon.East(at) * MathF.Cos(down) - up * MathF.Sin(down)) * 400f;
        var velocity = Moon.OverGround(at, stars);
        return CrossingTests.Craft(0, Moon, at, velocity, velocity);
    }

    [Fact]
    public void A_vessel_heading_into_the_moon_is_warned_of_the_moon()
    {
        var (sim, flown) = Flown(Diving(), Moon);
        using var _ = sim;
        Assert.True(flown.Fuel > 0f);
        WarpTo(sim, 4f);
        Assert.True(sim.Run(Nobody, f => f.Warning is not null, maxSeconds: 10), "never warned of");
        var warning = sim.Flight.Warning!.Value;
        var lead = Collisions.WarningSeconds(flown, warning);
        output.WriteLine($"warned at {sim.Time:0.000} s: {Collisions.Name(warning)} in {warning.Seconds:0.000} s, closing {warning.ClosingSpeed:0} m/s, hitting {warning.HitSpeed:0} m/s; lead {lead:0.000} s");
        Assert.Equal(SolidKind.Ground, warning.With);
        Assert.Same(Moon, warning.Body);
        Assert.Null(warning.Hit); // at its own body
        Assert.Equal("the moon", Collisions.Name(warning));
        Assert.Equal($"COLLISION: the moon in {warning.Seconds:0.0} s ({warning.ClosingSpeed:#,0} m/s)", Hud.WarningLine(warning));
        Assert.True(warning.HitSpeed > Scene.FastCollisionSpeed);
        Assert.True(warning.Seconds <= lead && warning.Seconds >= lead - Flight.WarpStepSeconds,
            $"warned {warning.Seconds:0.000} s ahead, its lead {lead:0.000} s");
        Assert.Equal(1f, sim.Flight.PlayerRate); // the warp dropped

        // Warped again by hand: not dropped again for the same collision; slowed as it nears, for the moon.
        WarpTo(sim, 2f);
        Slowdown? slowed = null;
        var frames = 0;
        Assert.True(sim.Run(Nobody, f =>
        {
            if (f.Slowed is { NearMiss: false } s && (slowed is null || s.Scale < slowed.Value.Scale))
                slowed = s;
            frames++;
            if (flown.State != FlightState.Crashed)
                Assert.Equal(2f, f.PlayerRate);
            return flown.State == FlightState.Crashed;
        }, maxSeconds: 15), "it never hit");
        output.WriteLine($"hit at {sim.Time:0.000} s after {frames} frames: {flown.CrashReason}; slowed to {slowed?.Scale:0.0000} for {slowed?.Reason}");
        Assert.NotNull(slowed);
        Assert.True(slowed.Value.Scale < 0.1f, $"slowed only to {slowed.Value.Scale:0.000}");
        Assert.Contains("the moon", slowed.Value.Reason);
        Assert.StartsWith("Hit the moon", flown.CrashReason);
    }

    /// <summary>
    /// The upper stage at the planet, 300 m short of the moon's sphere of influence on its far side,
    /// going <c>280 m/s</c> among the moon's stars at a slant towards a point 800 m off its centre: about
    /// a second from crossing, and about 15 s from its ground (hitting it at about 290 m/s).
    /// </summary>
    private static VesselState Outside(float fuel)
    {
        var (toPlanet, across) = CrossingTests.Ways();
        var start = -toPlanet * (Moon.SphereRadius + 300f);
        var stars = Vector3.Normalize(across * 800f - start) * 280f;
        var (at, velocity) = CrossingTests.FromMoon(start, stars, Home);
        return CrossingTests.Craft(0, Home, at, velocity, velocity, fuel: fuel);
    }

    [Fact]
    public void From_beyond_the_sphere_the_moon_is_warned_of_through_the_patched_path()
    {
        // With its tank empty no dodge is possible, so it is warned of as soon as it is found: before
        // it crosses, along the path patched into the moon's pull.
        var (sim, flown) = Flown(Outside(fuel: 0f), Home);
        using var _ = sim;
        Assert.Same(Home, flown.Body);
        var warning = sim.Flight.Warning;
        output.WriteLine($"after the first frame at {sim.Time:0.000} s, at the {flown.Body.Name}: {warning}");
        Assert.NotNull(warning);
        var o = warning.Value;
        Assert.Equal(SolidKind.Ground, o.With);
        Assert.Same(Moon, o.Hit);
        Assert.Same(Moon, o.Body);
        Assert.Equal("the moon", Collisions.Name(o));
        Assert.InRange(o.Seconds, 13.0, 17.0);
        Assert.True(o.HitSpeed > Scene.FastCollisionSpeed);
        // Its closing speed is over the moon's ground, not the planet's.
        var change = Bodies.Change(Home, Moon, sim.Flight.WorldTime);
        Assert.Equal((float)change.Velocity(Collisions.Middle(flown), flown.Velocity).Length(), o.ClosingSpeed, 0.5f);
        var predicted = sim.Flight.WorldTime + o.Seconds;
        // The readout, what a script flies by, names it by the body it is hit at too.
        var coming = Readout.FlightReadings(sim.Flight)[$"Coming at {WorldLog.Named(flown)}"];
        output.WriteLine($"the readout: {coming}");
        Assert.StartsWith("the moon in ", coming);

        // Asked along the patched path again and again, the asking allocates nothing (a scale of 0
        // asks the ground every time).
        var asking = new Collisions(sim.Flight);
        asking.Soonest(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds, 0f);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            asking.Soonest(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds, 0f);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"asked 100 times along the patched path: {allocated} bytes");
        Assert.Equal(0, allocated);

        // Kept through the crossing (one collision: the warp isn't dropped again), and hit where
        // and when it said.
        var crossedAt = double.NaN;
        Assert.True(sim.Run(Nobody, f =>
        {
            if (double.IsNaN(crossedAt) && flown.Body == Moon)
                crossedAt = f.WorldTime;
            if (flown.State != FlightState.Crashed)
                Assert.NotNull(f.Warning);
            return flown.State == FlightState.Crashed;
        }, maxSeconds: 20), "it never hit");
        output.WriteLine($"crossed at {crossedAt:0.000} s; hit at {sim.Flight.WorldTime:0.000} s, predicted {predicted:0.000} s: {flown.CrashReason}");
        Assert.True(crossedAt > 0.5 && crossedAt < 2.0, $"crossed at {crossedAt:0.000} s");
        Assert.InRange(sim.Flight.WorldTime - predicted, -0.3, 0.3);
        Assert.StartsWith("Hit the moon", flown.CrashReason);
    }

    [Fact]
    public void From_beyond_the_sphere_the_warning_comes_at_its_lead_and_drops_the_warp_once()
    {
        var (sim, flown) = Flown(Outside(fuel: 0.5f), Home);
        using var _ = sim;
        // Found before it crosses, along the patched path, though not yet within its lead.
        var asking = new Collisions(sim.Flight);
        var found = asking.Soonest(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds);
        output.WriteLine($"at the {flown.Body.Name}, found {found}");
        Assert.Same(Home, flown.Body);
        Assert.NotNull(found);
        Assert.Same(Moon, found.Value.Hit);
        Assert.Equal("the moon", Collisions.Name(found.Value));
        var lead = Collisions.WarningSeconds(flown, found.Value);
        Assert.True(found.Value.Seconds > lead, $"{found.Value.Seconds:0.0} s off, within its lead {lead:0.0} s");
        Assert.Null(sim.Flight.Warning);

        WarpTo(sim, 4f);
        Assert.True(sim.Run(Nobody, f =>
        {
            if (f.Warning is not null)
                return true;
            // Probed only while at the planet: the moon's ground, another body's, hands it no
            // ground ahead (Craft.GroundAhead), so the probe changes nothing. At the moon the
            // warning's own bounds below (no later than a step past its lead) say the same.
            if (flown.Body == Home && asking.Soonest(flown, f.WorldVessels, Scene.CollisionHorizonSeconds) is { } o)
                Assert.True(o.Seconds > Collisions.WarningSeconds(flown, o), $"not warned of {o}, within its lead");
            return false;
        }, maxSeconds: 20), "never warned of");
        var warning = sim.Flight.Warning!.Value;
        lead = Collisions.WarningSeconds(flown, warning);
        output.WriteLine($"warned at {sim.Time:0.000} s, at the {flown.Body.Name}: {Collisions.Name(warning)} in {warning.Seconds:0.000} s; lead {lead:0.000} s");
        Assert.Equal("the moon", Collisions.Name(warning));
        Assert.Same(Moon, warning.Body);
        Assert.True(warning.Seconds <= lead && warning.Seconds >= lead - Flight.WarpStepSeconds,
            $"warned {warning.Seconds:0.000} s ahead, its lead {lead:0.000} s");
        Assert.Equal(1f, sim.Flight.PlayerRate);
        WarpTo(sim, 2f);
        Assert.True(sim.Run(Nobody, f =>
        {
            if (flown.State != FlightState.Crashed)
                Assert.Equal(2f, f.PlayerRate);
            return flown.State == FlightState.Crashed;
        }, maxSeconds: 20), "it never hit");
        Assert.StartsWith("Hit the moon", flown.CrashReason);
    }

    [Fact]
    public void One_just_inside_the_moons_sphere_is_warned_of_the_moons_ground()
    {
        // The upper stage 10 m inside the moon's sphere, its tank empty, diving at 280 m/s, has
        // crossed into it by the first frame, and is warned of the moon's ground.
        var (toPlanet, across) = CrossingTests.Ways();
        var startA = -toPlanet * (Moon.SphereRadius - 10f);
        var (atA, velocityA) = CrossingTests.FromMoon(startA, Vector3.Normalize(across * 800f - startA) * 280f, Home);
        using var sim = new Simulation(Mission.FreePlay, world: World(
            CrossingTests.Craft(0, Home, atA, velocityA, velocityA, fuel: 0f)));
        var flown = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: flown));
        output.WriteLine($"after the first frame: at the {flown.Body.Name}; {sim.Flight.Warning}");
        Assert.Same(Moon, flown.Body);
        var warning = Assert.NotNull(sim.Flight.Warning);
        Assert.Equal(SolidKind.Ground, warning.With);
        Assert.Null(warning.Hit); // its own body's ground
        Assert.Equal("the moon", Collisions.Name(warning));
        Assert.InRange(warning.Seconds, 12.0, 17.0);
    }

    [Fact]
    public void Another_bodys_ground_is_asked_only_of_one_that_could_reach_it_fast()
    {
        // MayReachFast: whether a vessel too slow over its own ground to hit it fast could hit
        // another body's, so its ground is asked (along the patched path) at all.
        const float Horizon = Scene.CollisionHorizonSeconds;
        bool May(GroundBody body, Vector3 at, Vector3 velocity) =>
            Collisions.MayReachFast(body, at, velocity, (body.Gravity(at) + body.OutwardPull(at)).Length(), 0.0, Horizon);

        // Landed on the moon, or on the pad: no other body's ground within its reach.
        var landed = OnTheMoon.Lander(0, OnTheMoon.LandingSpot);
        Assert.False(May(Moon, landed.Position.ToSingle(), Vector3.Zero));
        Assert.False(May(Home, Vector3.Zero, Vector3.Zero));

        // 300 m outside the moon's sphere, at the planet: diving at 280 m/s, it could; drifting at
        // 10 m/s among the moon's stars, it couldn't be fast by the time it got there.
        var diving = Outside(fuel: 0.5f);
        Assert.True(May(Home, diving.Position.ToSingle(), diving.Velocity.ToSingle()));
        var (toPlanet, _) = CrossingTests.Ways();
        var (at, drifting) = CrossingTests.FromMoon(-toPlanet * (Moon.SphereRadius + 300f), toPlanet * 10f, Home);
        Assert.False(May(Home, at, drifting));
        output.WriteLine($"diving: {diving.Velocity.Length():0} m/s over the planet's ground; drifting: {drifting.Length():0} m/s over it");
    }

    [Fact]
    public void Every_bodys_ground_is_one_kind_named_by_its_body()
    {
        // No kind of its own for any body: the ground's, named by the body it is at.
        Assert.DoesNotContain(Enum.GetNames<SolidKind>(), n => Bodies.All.Any(b => b.Name == n));
        var second = GroundBody.Circling("Second moon", "the second moon", 2, Home, Scene.MoonDistance, Scene.MoonRadius,
            SolarSystem.MoonMu / 2, _ => new BodyPose(default, QuaternionDouble.Identity, default, default));
        foreach (var body in Bodies.All.Append(second))
        {
            var land = body.Centre + Vector3.UnitY * body.SeaRadius; // each frame's origin, on its ground (the planet's at Home's pad)
            Assert.False(body.IsSea(Vector3.UnitY));
            Assert.Equal(body.GroundName, Collisions.Name(body, SolidKind.Ground, land));
            Assert.Equal($"Hit {body.GroundName}", WorldLog.How(Vessel.HitTheGround(30f, body)));
        }
        Assert.Equal("the moon", Collisions.Name(Moon, SolidKind.Ground, Moon.Centre + Vector3.UnitY * Moon.SeaRadius));
        Assert.Equal("the second moon", Collisions.Name(second, SolidKind.Ground, second.Centre + Vector3.UnitY * second.SeaRadius));
    }

    [Fact]
    public void Crashing_into_the_moon_is_judged_by_the_grounds_rules()
    {
        var (sim, lander) = Flown(MoonLandingTests.Lander(40f), Moon);
        using var _ = sim;
        Assert.True(sim.Run(Nobody, f =>
        {
            Assert.Null(f.Warning); // too slow to be a fast collision: unwarned and unslowed, as on the planet
            Assert.Null(f.Slowed);
            return lander.State != FlightState.Flying;
        }, maxSeconds: 10), "it never came down");
        output.WriteLine($"{lander.State}: {lander.CrashReason}");
        Assert.Equal(FlightState.Crashed, lander.State);
        var speed = WorldLog.SpeedIn(lander.CrashReason);
        Assert.NotNull(speed);
        Assert.InRange(speed.Value, 38f, 45f);
        Assert.Equal(Vessel.HitTheGround(speed.Value, Moon), lander.CrashReason);
        var entry = Assert.Single(sim.Flight.Log);
        output.WriteLine($"logged: {entry}");
        Assert.Equal(WorldLog.Named(lander), entry.What);
        Assert.Equal("Hit the moon", entry.How);
        Assert.Equal(speed, entry.Speed);
        Assert.Equal("on the moon", entry.Where); // its ground by its name, as the reason has it
    }

    [Fact]
    public void Coming_down_softly_on_the_moon_is_no_warning()
    {
        var (sim, lander) = Flown(MoonLandingTests.Lander(1f), Moon);
        using var _ = sim;
        Assert.True(sim.Run(Nobody, f =>
        {
            Assert.Null(f.Warning);
            Assert.Null(f.Slowed);
            Assert.Null(f.ComingAt(lander));
            return lander.State != FlightState.Flying;
        }, maxSeconds: 30), "it never came down");
        output.WriteLine($"{lander.State} at {lander.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.Empty(sim.Flight.Log);
    }
}
