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
    /// 0.14 m a second), with half as much again for room. Held to Jolt's old cap of 4,000 m/s, it
    /// was 1,145 m off.
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

    /// <summary>Runs <paramref name="seconds"/> after its first frame, failing if <paramref name="v"/>, still flying, is ever faster than the ground's top speed or out of the band low over the ground: the fastest it went.</summary>
    private float HeldFor(Simulation sim, Vessel v, double seconds, bool onFormula)
    {
        sim.Step(default); // its first step holds it
        var fastest = 0f;
        sim.Run(Nobody, _ =>
        {
            Assert.Equal(FlightState.Flying, v.State);
            Assert.Equal(onFormula, v.OnFormula);
            Assert.True(Home.LowOverTheGround(v.CentreOfMass));
            fastest = MathF.Max(fastest, v.Velocity.Length());
            return false;
        }, maxSeconds: seconds);
        output.WriteLine($"{(onFormula ? "under formula" : "in the physics")}: at most {fastest:0.000} m/s over the ground, {Home.SurfaceAltitude(v.CentreOfMass):0} m up");
        Assert.InRange(fastest, Scene.GroundTopSpeed - 50f, Scene.GroundTopSpeed + 0.01f);
        return fastest;
    }

    [Fact]
    public void Fast_low_over_the_ground_is_held_at_the_ground_cap()
    {
        // At half as fast again as the ground's top speed, 1 km over the ground: flown (in the
        // physics) off the pad, and another (under formula) over the far side of the planet,
        // where nothing near would hold it in the physics.
        var far = Home.Centre - (Scene.RocketStart - Home.Centre);
        using var sim = new Simulation(Mission.FreePlay, world: World(
            Skimming(0, Scene.RocketStart, 1000f, 1.5f * Scene.GroundTopSpeed), Skimming(1, far, 1000f, 1.5f * Scene.GroundTopSpeed)));
        var (flown, other) = (sim.Flight.WorldVessels.Single(x => x.Id == 0), sim.Flight.WorldVessels.Single(x => x.Id == 1));
        sim.Step(new FlightInput(SwitchTo: flown));
        Assert.False(flown.OnFormula);
        Assert.True(other.OnFormula, "far from everything, it should be under formula");
        HeldFor(sim, flown, 0.5, onFormula: false);
        HeldFor(sim, other, 0.1, onFormula: true);
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
