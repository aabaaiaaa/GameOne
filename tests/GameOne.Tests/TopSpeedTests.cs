using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// No speed cap in open space, one low over the ground: Jolt's own cap never takes hold, so an
/// escape far from the planet's axis (where the turning ground's frame itself moves faster than
/// the ground's top speed) follows the path drawn for it, and a vessel burning in open space goes
/// as fast as its engine takes it; low over the ground (<see cref="GroundBody.LowOverTheGround"/>)
/// the game holds the speed over the ground to <see cref="Scene.GroundTopSpeed"/>, in the physics
/// and under formula alike.
/// </summary>
public sealed class TopSpeedTests(ITestOutputHelper output)
{
    private static GroundBody Home => GroundBody.Planet;
    private static readonly Pilot Nobody = (_, _) => default;
    private const double Now = 5000.0;

    private static WorldState World(params VesselState[] vessels) =>
        new() { Time = Now, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 2 };

    private const double EscapeSeconds = 20.0;

    /// <summary>
    /// How far the physics may leave the path drawn at the start, <paramref name="r"/> from the
    /// axis, over <see cref="EscapeSeconds"/> (metres): its kick and drift lead the true path by
    /// half its acceleration times its step times the time flown, the same lead it has everywhere
    /// (see Formula's Step), and something still among the stars is pulled round in the ground's
    /// frame by its turn's Ω²r (32 m/s² 1,000 km out): 2.7 m in 20 s, measured 2.9 (growing evenly,
    /// 0.14 m a second), with half as much again for room. With Jolt capping it at 4,000 m/s, the
    /// same flight leaves its path by over a kilometre.
    /// </summary>
    private static double EscapeMiss(double r) =>
        1.5 * 0.5 * Home.SpinRate * (double)Home.SpinRate * r * Physics.RocketPhysics.StepSeconds * EscapeSeconds;

    [Fact]
    public void An_escape_far_off_the_axis_follows_its_path()
    {
        // The upper stage 1,000 km from the planet's centre in its equator's plane (so as far from
        // its axis), on the side away from the moon, going straight out among the stars at
        // 1,000 m/s, well past escaping: over the ground, which turns under it, about 5,800 m/s.
        var moon = SolarSystem.MoonCentre(Now) - Home.Centre;
        var away = -Vector3.Normalize(moon - Home.NorthPole * Vector3.Dot(moon, Home.NorthPole));
        var at = Home.Centre + away * 1_000_000f;
        var stars = away * 1_000f;
        var velocity = Home.OverGround(at, stars);
        output.WriteLine($"{Vector3.Cross(Home.NorthPole, at - Home.Centre).Length() / 1e3:#,0} km from the axis, {velocity.Length():0} m/s over the ground");
        Assert.True(velocity.Length() > 1.4f * Scene.GroundTopSpeed);

        using var sim = new Simulation(Mission.FreePlay, world: World(CrossingTests.Craft(0, Home, at, velocity, away)));
        var v = sim.Flight.WorldVessels.Single(x => x.Id == 0);
        sim.Step(new FlightInput(SwitchTo: v));
        Assert.False(v.OnFormula);
        Assert.NotNull(v.Physics);

        double Time() => sim.Flight.WorldTime - sim.Worlds.Lag;
        var start = Time();
        var (c, u) = (v.TrueCentreOfMass, v.TrueVelocity);
        var legs = new List<PathLeg>(Scene.PatchedLegs);
        Patched.Legs(Home, c.ToSingle(), Home.AmongStars(c.ToSingle(), u.ToSingle()), start, legs);
        Assert.Single(legs); // nowhere near the moon's sphere
        var leg = legs[0];

        var (worst, frames) = (0.0, 0);
        sim.Run(Nobody, _ =>
        {
            Assert.False(v.OnFormula);
            Assert.Equal(FlightState.Flying, v.State);
            worst = Math.Max(worst, (v.TrueCentreOfMass - SolarEdgeTests.GroundAt(leg, Time())).Length());
            frames++;
            return false;
        }, maxSeconds: EscapeSeconds);
        output.WriteLine($"over {frames} frames ({Time() - start:0.0} s, {(v.TrueCentreOfMass - c).Length() / 1e3:0} km flown), at worst {worst:0.000} m off its path; "
            + $"now {v.Velocity.Length():0} m/s over the ground");
        Assert.InRange(worst, 0, EscapeMiss(1_000_000));
        Assert.True(v.Velocity.Length() > Scene.GroundTopSpeed);
    }

    /// <summary>
    /// A booster (legs stowed, engine off) at <paramref name="at"/>, <paramref name="clearance"/>
    /// over what is under it, going east at <paramref name="speed"/> over the ground.
    /// </summary>
    private static VesselState Skimming(int id, Vector3 at, float clearance, float speed) =>
        Drops.Placed(at, Home.North(at), 0f, sideways: Home.East(at) * speed, clearance: clearance, legsOut: false, id: id, flight: 1);

    [Fact]
    public void Fast_low_over_the_ground_is_held_at_the_ground_cap()
    {
        // At half as fast again as the ground's top speed, 1 km over the ground: flown (in the
        // physics) off the pad, and another (under formula) over the far side of the planet,
        // where nothing near would hold it in the physics; both checked every frame for half a
        // second (some 2 km, and five of the formula's steps), from the first step, which holds them.
        var far = Home.Centre - (Scene.RocketStart - Home.Centre);
        using var sim = new Simulation(Mission.FreePlay, world: World(
            Skimming(0, Scene.RocketStart, 1000f, 1.5f * Scene.GroundTopSpeed), Skimming(1, far, 1000f, 1.5f * Scene.GroundTopSpeed)));
        var (flown, other) = (sim.Flight.WorldVessels.Single(x => x.Id == 0), sim.Flight.WorldVessels.Single(x => x.Id == 1));
        sim.Step(new FlightInput(SwitchTo: flown));
        var fastest = new float[2];
        var frames = 0;
        sim.Run(Nobody, _ =>
        {
            foreach (var (v, i, onFormula) in new[] { (flown, 0, false), (other, 1, true) })
            {
                Assert.Equal(FlightState.Flying, v.State);
                Assert.Equal(onFormula, v.OnFormula);
                Assert.True(Home.LowOverTheGround(v.CentreOfMass));
                fastest[i] = MathF.Max(fastest[i], v.Velocity.Length());
            }
            frames++;
            return false;
        }, maxSeconds: 0.5);
        output.WriteLine($"over {frames} frames: at most {fastest[0]:0.000} m/s in the physics, {fastest[1]:0.000} m/s under formula; "
            + $"{Home.SurfaceAltitude(flown.CentreOfMass):0} m and {Home.SurfaceAltitude(other.CentreOfMass):0} m up");
        Assert.True(frames >= 29);
        Assert.All(fastest, f => Assert.InRange(f, Scene.GroundTopSpeed - 50f, Scene.GroundTopSpeed + 0.01f));
    }

    [Fact]
    public void Fast_low_over_the_moon_under_formula_is_stepped_and_held()
    {
        // The upper stage under formula at the moon (no air: low over its highest ground, with its
        // engine off, it would be on its orbit), 500 m over its highest ground at 6,000 m/s across:
        // loaded stepped, not on its orbit, as it is faster than the ground's top speed there; held
        // to that speed from its first step (a tenth of a second on); and no faster from then until it has risen out of the
        // band (at the cap and no faster, it may go on its orbit, which is asked again each step).
        var moon = GroundBody.Moon;
        var up = Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f));
        var at = moon.Centre + up * (moon.SeaRadius + moon.MaxHeight + Scene.FineDetailMargin + 500f);
        var across = Vector3.Normalize(Vector3.Cross(up, moon.NorthPole));
        using var sim = new Simulation(Mission.FreePlay, world: World(CrossingTests.Craft(0, moon, at, across * 1.5f * Scene.GroundTopSpeed, across)));
        var v = sim.Flight.WorldVessels.Single(x => x.Id == 0);
        Assert.Same(moon, v.Body);
        Assert.True(v.OnFormula, "at the moon, nobody watching it, it should be under formula");
        Assert.False(v.EngineLit);
        Assert.True(moon.LowOverTheGround(v.CentreOfMass));
        // Clear of its ground and with no air, only the hold keeps it off its orbit.
        Assert.True(Formula.OnItsOrbitAt(v, v.CentreOfMass, Vector3.Zero));
        Assert.False(Formula.OnItsOrbitAt(v, v.CentreOfMass, v.Velocity));
        Assert.True(v.Motion.Stepping, "held, it should be stepped, not on its orbit");
        var loaded = v.Motion.Time;
        while (v.Motion.Time == loaded)
            sim.Step(default); // until its first formula step, which holds it
        var (inBand, fastest) = (0, 0f);
        sim.Run(Nobody, _ =>
        {
            Assert.Equal(FlightState.Flying, v.State);
            Assert.True(v.OnFormula);
            if (!moon.LowOverTheGround(v.CentreOfMass))
                return true;
            fastest = MathF.Max(fastest, v.Velocity.Length());
            inBand++;
            return false;
        }, maxSeconds: 2);
        output.WriteLine($"{inBand} frames in the band, at most {fastest:0.000} m/s over the moon's ground; out of it at {moon.Altitude(v.CentreOfMass) - moon.MaxHeight:0} m over its highest ground");
        Assert.True(inBand >= 10, $"only {inBand} frames in the band");
        Assert.InRange(fastest, Scene.GroundTopSpeed - 50f, Scene.GroundTopSpeed + 0.01f);
    }

    [Theory]
    [InlineData(4_000f)]
    [InlineData(16_000f)]
    public void Fast_in_open_space_is_not_capped(float past)
    {
        // The upper stage 20 km up, far above the ground's band, coasting east just under
        // `past` over the ground with its nose that way: its engine takes it past.
        var start = Home.Centre + Home.Up(Scene.RocketStart) * (Home.SeaRadius + 20_000f);
        var east = Home.East(start);
        using var sim = new Simulation(Mission.FreePlay, world: World(CrossingTests.Craft(0, Home, start, east * (past - 20f), east)));
        var v = sim.Flight.WorldVessels.Single(x => x.Id == 0);
        sim.Step(new FlightInput(SwitchTo: v));
        Assert.False(v.OnFormula);
        Assert.False(Home.LowOverTheGround(v.CentreOfMass));
        var fastest = 0f;
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ =>
        {
            Assert.Equal(FlightState.Flying, v.State);
            fastest = MathF.Max(fastest, v.Velocity.Length());
            return fastest > past + 10f;
        }, maxSeconds: 10);
        output.WriteLine($"{fastest:0.0} m/s over the ground, {Home.SurfaceAltitude(v.CentreOfMass) / 1e3:0.0} km up");
        Assert.True(v.EngineLit);
        Assert.True(fastest > past + 10f, $"only {fastest:0.0} m/s");
    }
}
