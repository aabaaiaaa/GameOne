using System.Numerics;
using GameOne.Physics;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Vessels collide with each other: a gentle knock pushes them apart, and a hard one destroys both,
/// if they closed faster than they can take (<see cref="Vessel.ImpactTolerance"/>). Stages coming
/// apart start touching, and aren't a hit; nor is a hard manoeuvre beside another.
/// </summary>
public sealed class HitTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;
    private static readonly Vector3 Zone = Scene.LandingZoneCentre;

    /// <summary>A booster standing on the landing zone, landed and frozen there, as saved.</summary>
    /// <summary>How far apart two crafts' bodies are: their axes, bell to top, less both their widest radii.</summary>
    private static float Gap(RocketPhysics physics, Craft a, Craft b)
    {
        (Vector3, Vector3) Axis(Craft c)
        {
            var model = physics.Model(c);
            var bell = c.Vehicle.Stages[c.First].BellHeight;
            return (Vector3.Transform(new Vector3(0, -bell, 0), model), Vector3.Transform(new Vector3(0, c.Height, 0), model));
        }
        var ((a0, a1), (b0, b1)) = (Axis(a), Axis(b));
        return TowersAndMasts.Closest(a0, a1, b0, b1).Apart - a.Widest - b.Widest;
    }

    private static VesselState Standing()
    {
        using var sim = Drops.At(Zone, Planet.East(Zone), 1f);
        var booster = sim.Flight.Controlled;
        Assert.True(sim.Run(Nobody, _ => booster.Craft.Frozen, maxSeconds: 60), $"it never froze: {booster.State}");
        return sim.Flight.Capture().Vessels.Single(v => v.Id == 0);
    }

    /// <summary>
    /// A world of the booster standing on the zone, and another (its legs stowed) upright over it,
    /// its bell <paramref name="gap"/> above the standing one's nose, falling at <paramref name="speed"/>;
    /// the second is flown.
    /// </summary>
    internal static (Simulation Sim, Vessel Below, Vessel Above) DroppedOnto(float gap, float speed)
    {
        var below = Standing();
        var up = Planet.Up(below.Position.ToSingle());
        var nose = below.Position + up * RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var above = Drops.Placed(Zone, Planet.East(Zone), speed, legsOut: false, id: 1) with
        {
            Position = nose + up * (Vehicle.Booster.Stages[0].BellHeight + gap),
        };
        var sim = new Simulation(world: new WorldState { Vessels = [below, above], NextVesselId = 2 });
        var vessels = sim.Flight.WorldVessels;
        var (b, a) = (vessels.First(v => v.Id == 0), vessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: a));
        return (sim, b, a);
    }

    [Fact]
    public void Jolt_exports_what_the_collisions_use()
    {
        // Moving a craft between object layers (to let stages part before they can collide).
        using var physics = new Physics.RocketPhysics(GroundBody.Planet); // loads jolt.dll
        var jolt = NativeLibrary.Load("jolt", typeof(Physics.RocketPhysics).Assembly, null);
        Assert.True(NativeLibrary.TryGetExport(jolt, "BodyInterface_SetObjectLayer", out _));
    }

    [Fact]
    public void A_booster_dropped_onto_a_frozen_one_does_not_pass_through_it()
    {
        var (sim, below, above) = DroppedOnto(0.3f, 1f);
        using var _ = sim;
        Assert.Equal(FlightState.Landed, below.State); // restored frozen, and woken as the other comes near
        var height = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var closest = float.MaxValue;
        sim.Run(Nobody, _ =>
        {
            closest = MathF.Min(closest, Vector3.Distance(below.CentreOfMass, above.CentreOfMass));
            return false;
        }, maxSeconds: 5);
        output.WriteLine($"closest {closest:0.0} m apart (each {height:0.0} m tall); below {below.State} {below.CrashReason}, above {above.State} {above.CrashReason}");
        Assert.True(closest > 0.8f * height, $"they came within {closest:0.0} m: it passed through");
    }

    [Fact]
    public void A_gentle_knock_pushes_them_apart_and_wrecks_neither()
    {
        var (sim, below, above) = DroppedOnto(0.3f, 1f);
        using var _ = sim;
        sim.Run(Nobody, _ => false, maxSeconds: 2);
        output.WriteLine($"below {below.State} {below.CrashReason}; above {above.State} {above.CrashReason}");
        Assert.False(below.CrashReason?.StartsWith("Hit another vessel") ?? false, below.CrashReason);
        Assert.False(above.CrashReason?.StartsWith("Hit another vessel") ?? false, above.CrashReason);
        Assert.NotEqual(FlightState.Crashed, below.State);
    }

    [Fact]
    public void A_hard_hit_wrecks_both()
    {
        // Closing at 12 m/s, twice what either can take: both are wrecked, whichever did the hitting.
        var (sim, below, above) = DroppedOnto(0.3f, 12f);
        using var _ = sim;
        sim.Run(Nobody, _ => above.State == FlightState.Crashed && below.State == FlightState.Crashed, maxSeconds: 3);
        output.WriteLine($"below {below.State} {below.CrashReason}; above {above.State} {above.CrashReason}");
        Assert.Equal(FlightState.Crashed, above.State);
        Assert.StartsWith("Hit another vessel at", above.CrashReason);
        Assert.Equal(FlightState.Crashed, below.State);
        Assert.StartsWith("Hit another vessel at", below.CrashReason);
    }

    [Fact]
    public void Hitting_a_frozen_one_just_under_the_tolerance_wrecks_neither()
    {
        // Falling at 5.3 m/s from 0.3 m over the frozen one's nose, it touches it closing at about
        // 5.8 m/s (√(5.3² + 2 g 0.3)), inside the 6 either can take.
        var (sim, below, above) = DroppedOnto(0.3f, 5.3f);
        using var _ = sim;
        sim.Run(Nobody, _ => false, maxSeconds: 2);
        output.WriteLine($"below {below.State} {below.CrashReason}; above {above.State} {above.CrashReason}");
        Assert.False(below.CrashReason?.StartsWith("Hit another vessel") ?? false, below.CrashReason);
        Assert.False(above.CrashReason?.StartsWith("Hit another vessel") ?? false, above.CrashReason);
    }

    [Fact]
    public void Hitting_a_frozen_one_just_over_the_tolerance_wrecks_both()
    {
        // As above, but falling at 6.2 m/s: closing at about 6.7, just over what they can take.
        var (sim, below, above) = DroppedOnto(0.3f, 6.2f);
        using var _ = sim;
        sim.Run(Nobody, _ => above.State == FlightState.Crashed && below.State == FlightState.Crashed, maxSeconds: 2);
        output.WriteLine($"below {below.State} {below.CrashReason}; above {above.State} {above.CrashReason}");
        Assert.StartsWith("Hit another vessel at", above.CrashReason);
        Assert.StartsWith("Hit another vessel at", below.CrashReason);
    }

    [Fact]
    public void A_hard_burn_beside_another_vessel_is_not_a_hit()
    {
        // Lifting off at full throttle 0.4 m from another's side: a sudden change of its own speed
        // right beside another, but nothing touched it, so it is no hit on either.
        var below = Standing();
        var up = Planet.Up(below.Position.ToSingle());
        var east = Planet.East(below.Position.ToSingle());
        var radius = Vehicle.Booster.Stages[0].Radius;
        var beside = Drops.Placed(Zone, east, 0f, legsOut: false, id: 1) with
        {
            Position = below.Position + east * (2f * radius + 0.4f) + up * 3f,
            Velocity = Vector3.Zero,
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [below, beside], NextVesselId = 2 });
        var (standing, burning) = (sim.Flight.WorldVessels.First(v => v.Id == 0), sim.Flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: burning));
        sim.Run((_, _) => new FlightInput(Throttle: 1f), _ => false, maxSeconds: 2);
        output.WriteLine($"standing {standing.State} {standing.CrashReason}; burning {burning.State} {burning.CrashReason}, {burning.Velocity.Length():0.0} m/s");
        Assert.False(standing.CrashReason?.StartsWith("Hit another vessel") ?? false, standing.CrashReason);
        Assert.False(burning.CrashReason?.StartsWith("Hit another vessel") ?? false, burning.CrashReason);
    }

    /// <summary>A booster lying on its side on level ground, landed and frozen there, as saved; and the ground's way across it.</summary>
    private static (VesselState Lying, Vector3 Side) LyingDown()
    {
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround); // dead level: a booster lying on its side, a cylinder, rolls off down any slope it can (nothing in Jolt resists rolling)
        using var sim = Drops.At(at, downhill, 0.5f, leanDegrees: 90f, legsOut: false);
        var lying = sim.Flight.Controlled;
        Assert.True(sim.Run(Nobody, _ => lying.Craft.Frozen, maxSeconds: 60), $"it never froze: {lying.State} {lying.CrashReason}");
        return (sim.Flight.Capture().Vessels.Single(v => v.Id == 0), Vector3.Normalize(Vector3.Cross(Planet.Up(at), downhill)));
    }

    [Fact]
    public void Touching_down_close_beside_another_vessel_is_no_hit()
    {
        // Lying down, parallel to one already lying there, less than a metre from it: coming down at
        // 5.5 m/s (inside the ground's 6) it has landed: its touchdown's own stop, and the bounce just
        // after it (which can read 6.5 m/s), are not a hit on the other.
        var (lying, side) = LyingDown();
        var radius = Vehicle.Booster.Stages[0].Radius;
        var up = Planet.Up(lying.Position.ToSingle());
        var other = lying with
        {
            Id = 1, Position = lying.Position + side * (2f * radius + 0.8f) + up * 0.3f, State = FlightState.Flying, Velocity = -up * 5.5f,
            WasTouching = false, HullWasTouching = false, // in the air, so its touchdown is judged
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [lying, other], NextVesselId = 2 });
        var vessel = sim.Flight.WorldVessels.First(v => v.Id == 1);
        sim.Step(new FlightInput(SwitchTo: vessel));
        sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 20);
        output.WriteLine($"{vessel.State} {vessel.CrashReason}; touchdown {vessel.TouchdownSpeed:0.0} m/s");
        Assert.Equal(FlightState.Landed, vessel.State);
    }

    [Fact]
    public void A_hard_skidding_touchdown_beside_another_vessel_is_the_ground_s()
    {
        // Lying down, parallel to one already lying there, less than a metre from it, as above, but
        // coming down hard and skidding along its length (away from nothing: the other lies beside
        // it, not ahead). Its body touching the ground, at its whole speed there (the skid in it), is
        // its hard touchdown, the ground's; it never touches the other, so there is no hit. At 20 m/s
        // it falls a third of a metre a step, so it is started from heights across a whole step's
        // fall, touching the ground at every point in a step.
        var (lying, side) = LyingDown();
        var radius = Vehicle.Booster.Stages[0].Radius;
        var up = Planet.Up(lying.Position.ToSingle());
        var along = Vector3.Normalize(Vector3.Cross(side, up));
        for (var height = 0.30f; height < 0.65f; height += 0.03f)
        {
            var other = lying with
            {
                Id = 1, Position = lying.Position + side * (2f * radius + 0.5f) + up * height, State = FlightState.Flying,
                Velocity = -up * 20f + along * 15f, WasTouching = false, HullWasTouching = false,
            };
            using var sim = new Simulation(world: new WorldState { Vessels = [lying, other], NextVesselId = 2 });
            var vessel = sim.Flight.WorldVessels.First(v => v.Id == 1);
            sim.Step(new FlightInput(SwitchTo: vessel));
            sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 5);
            output.WriteLine($"from {height:0.00} m: {vessel.State} {vessel.CrashReason}");
            Assert.Equal(FlightState.Crashed, vessel.State);
            Assert.StartsWith("Hit the ground at", vessel.CrashReason);
        }
    }

    [Fact]
    public void A_frozen_vessel_touched_slowly_is_woken_by_the_touch()
    {
        // Set down very slowly on the frozen one's nose (too slow to wake it by coming near: see
        // Scene.WakeSpeed), it is woken by the touch, not held fixed in place.
        var (sim, below, above) = DroppedOnto(0.05f, 0f); // touching it at under 1 m/s, a few steps in
        using var _ = sim;
        Assert.True(below.Craft.Frozen, "it was woken before the touch: this shows nothing");
        sim.Run(Nobody, _ => !below.Craft.Frozen, maxSeconds: 2);
        output.WriteLine($"below {(below.Craft.Frozen ? "frozen" : "awake")} {below.State}; above {above.State}");
        Assert.False(below.Craft.Frozen);
    }

    [Fact]
    public void A_hard_hit_from_the_side_wrecks_both()
    {
        // Flown into the standing one's side at 16 m/s, its legs stowed, about halfway up: they
        // close faster than either can take.
        var below = Standing();
        var up = Planet.Up(below.Position.ToSingle());
        var east = Planet.East(below.Position.ToSingle());
        var height = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var radius = Vehicle.Booster.Stages[0].Radius;
        var striker = Drops.Placed(Zone, east, 0f, legsOut: false, id: 1) with
        {
            Position = below.Position - east * (2f * radius + 1f) + up * 3f,
            Velocity = east * 16f,
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [below, striker], NextVesselId = 2 });
        var (struck, hitter) = (sim.Flight.WorldVessels.First(v => v.Id == 0), sim.Flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: hitter));
        sim.Run(Nobody, _ => struck.State == FlightState.Crashed && hitter.State == FlightState.Crashed, maxSeconds: 2);
        output.WriteLine($"struck {struck.State} {struck.CrashReason} ({height:0.0} m tall); striker {hitter.State} {hitter.CrashReason}");
        Assert.StartsWith("Hit another vessel at", struck.CrashReason);
        Assert.StartsWith("Hit another vessel at", hitter.CrashReason);
    }

    [Fact]
    public void A_floating_vessel_hit_hard_is_wrecked()
    {
        var deep = Planet.Centre + WaterTests.DeepWater.Value * Planet.SeaRadius;
        VesselState floating;
        using (var first = Drops.At(deep, Planet.East(deep), 3f))
        {
            var vessel = first.Flight.Controlled;
            Assert.True(first.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 90));
            Assert.Equal(FlightState.Floating, vessel.State);
            floating = first.Flight.Capture().Vessels.Single(v => v.Id == 0);
        }
        var up = Planet.Up(floating.Position.ToSingle());
        var radius = Vehicle.Booster.Stages[0].Radius;
        // Lying as the floating one does, just over it, coming down at 16 m/s: closing faster than
        // either can take (Vessel.ImpactTolerance), both are wrecked.
        var falling = floating with { Id = 1, Position = floating.Position + up * (2f * radius + 0.3f), State = FlightState.Flying, Velocity = -up * 16f };
        using var sim = new Simulation(world: new WorldState { Vessels = [floating, falling], NextVesselId = 2, Time = 0 });
        var (struck, hitter) = (sim.Flight.WorldVessels.First(v => v.Id == 0), sim.Flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: hitter));
        sim.Run(Nobody, _ => struck.State == FlightState.Crashed && hitter.State == FlightState.Crashed, maxSeconds: 3);
        output.WriteLine($"floating one {struck.State} {struck.CrashReason}; falling one {hitter.State} {hitter.CrashReason}");
        Assert.StartsWith("Hit another vessel at", struck.CrashReason);
        Assert.StartsWith("Hit another vessel at", hitter.CrashReason);
    }

    [Fact]
    public void The_rocket_on_the_pad_hit_hard_is_wrecked()
    {
        // Flown into its side from the east (away from the tower) at 30 m/s, arriving about halfway
        // up: it starts 40 m off, outside what a new flight clears from round the pad, and high
        // enough to fall that far on the way. Closing at 30 m/s, both are wrecked.
        Vector3 middle, up, east;
        using (var first = new Simulation())
        {
            var pad = first.Flight.Controlled;
            (up, east) = (pad.LocalUp, Planet.East(pad.Position));
            middle = pad.Position + up * (RocketGeometry.StackHeight(pad.Vehicle, 0, pad.TopStage) / 2f);
        }
        const float speed = 30f, off = 40f;
        var t = off / speed;
        var striker = Drops.Placed(Zone, east, 0f, legsOut: false, id: 0) with
        {
            Position = middle + east * off + up * (0.5f * Scene.SurfaceGravity * t * t - 3f),
            Velocity = -east * speed,
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [striker], NextVesselId = 1 });
        var rocket = sim.Flight.Controlled;
        Assert.Equal(FlightState.Ready, rocket.State);
        Assert.Equal(2, sim.Flight.WorldVessels.Count); // the striker is far enough off not to be cleared away
        var other = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Run(Nobody, _ => rocket.State == FlightState.Crashed && other.State == FlightState.Crashed, maxSeconds: 4);
        output.WriteLine($"the rocket on the pad {rocket.State} {rocket.CrashReason}; the striker {other.State} {other.CrashReason}");
        Assert.StartsWith("Hit another vessel at", rocket.CrashReason);
        Assert.StartsWith("Hit another vessel at", other.CrashReason);
    }

    /// <summary>
    /// Two boosters lying side by side on dead level ground, 5 cm apart, landed and frozen there,
    /// as saved; with the way up there, the way across them (from the first to the second), and
    /// their radius. (Dead level: a booster lying on its side, a cylinder, rolls off down any slope
    /// it can, as nothing in Jolt resists rolling.)
    /// </summary>
    private static (VesselState A, VesselState B, Vector3 Up, Vector3 Side, float Radius) LyingSideBySide()
    {
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var radius = Vehicle.Booster.Stages[0].Radius;
        var up = Planet.Up(at);
        var side = Vector3.Normalize(Vector3.Cross(up, downhill));
        using var first = Drops.At(at, downhill, 0.5f, leanDegrees: 90f, legsOut: false);
        var lying = first.Flight.Controlled;
        Assert.True(first.Run(Nobody, _ => lying.Craft.Frozen, maxSeconds: 60), $"it never froze: {lying.State} {lying.CrashReason}");
        var one = first.Flight.Capture().Vessels.Single(v => v.Id == 0);
        var two = one with { Id = 1, Position = one.Position + side * (2f * radius + 0.05f) };
        // Watched, so they settle in the physics (nobody watching them, they would leave it at
        // once, and one woken as the other's body was taken out would leave unfrozen).
        using var pair = new Simulation(world: new WorldState { Vessels = [one, two], NextVesselId = 2 });
        var both = pair.Flight.WorldVessels.Where(v => v.Flight == 0).ToArray();
        Assert.True(pair.Run((_, _) => new FlightInput(Watched: both), f => f.WorldVessels.All(v => v.Craft.Frozen), maxSeconds: 60), "the pair never froze");
        Assert.All(both, v => Assert.False(v.OnFormula));
        var world = pair.Flight.Capture();
        return (world.Vessels.Single(v => v.Id == 0), world.Vessels.Single(v => v.Id == 1), up, side, radius);
    }

    [Fact]
    public void At_rest_on_other_vessels_it_lands_and_freezes_and_wakes_when_they_move()
    {
        // Two boosters lying side by side on level ground, landed and frozen; a third laid along the
        // groove between them, coming down onto the two at about 2.6 m/s (from 0.3 m up, at 1.5 m/s),
        // resting on them (a booster laid across one alone just rolls off it). Its touchdown is its
        // touch on them: it never touched the ground.
        var (a, b, up, _, radius) = LyingSideBySide();
        var groove = (a.Position + b.Position) / 2f + up * (radius * MathF.Sqrt(3f) + 0.3f);
        var above = a with { Id = 2, Position = groove, State = FlightState.Flying, Velocity = -up * 1.5f, TouchdownSpeed = 0f };

        using var sim = new Simulation(world: new WorldState { Vessels = [a, b, above], NextVesselId = 3 });
        var vessels = sim.Flight.WorldVessels;
        var (lower, upper) = (vessels.First(v => v.Id == 0), vessels.First(v => v.Id == 2));
        sim.Step(new FlightInput(SwitchTo: upper));
        sim.Run(Nobody, _ => upper.Craft.Frozen || upper.State == FlightState.Crashed, maxSeconds: 30);
        output.WriteLine($"upper {upper.State} {upper.CrashReason}, {(upper.Craft.Frozen ? "frozen" : "awake")}, {upper.Ground:0.0} m up");
        Assert.Equal(FlightState.Landed, upper.State);
        Assert.True(upper.Craft.Frozen);
        Assert.True(upper.Ground > radius, $"only {upper.Ground:0.0} m up: it isn't lying on the others");
        output.WriteLine($"its touchdown {upper.TouchdownSpeed:0.00} m/s");
        Assert.InRange(upper.TouchdownSpeed, 2f, 3.2f);

        // One under it flown away: the one on top is woken, and moved.
        sim.Step(new FlightInput(SwitchTo: lower));
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => upper.State != FlightState.Landed, maxSeconds: 3);
        output.WriteLine($"then upper {upper.State} {upper.CrashReason}, {(upper.Craft.Frozen ? "frozen" : "awake")}");
        Assert.False(upper.Craft.Frozen);
        Assert.NotEqual(FlightState.Landed, upper.State);
    }

    [Fact]
    public void Laid_slowly_on_frozen_ones_it_wakes_them_by_its_touch_and_freezes_on_them()
    {
        // As above, but let go 2 cm over the groove, still: too slow to wake the two by coming
        // near (Scene.WakeSpeed), it wakes them only by touching them (Jolt's contact: a frozen
        // craft has none of its own until it wakes), and the three settle and freeze together,
        // it resting on them, none fallen through or rolled off.
        var (a, b, up, _, radius) = LyingSideBySide();
        var groove = (a.Position + b.Position) / 2f + up * (radius * MathF.Sqrt(3f) + 0.02f);
        var above = a with { Id = 2, Position = groove, State = FlightState.Flying, Velocity = Vector3.Zero, TouchdownSpeed = 0f };

        using var sim = new Simulation(world: new WorldState { Vessels = [a, b, above], NextVesselId = 3 });
        var vessels = sim.Flight.WorldVessels;
        var (first, second, upper) = (vessels.First(v => v.Id == 0), vessels.First(v => v.Id == 1), vessels.First(v => v.Id == 2));
        sim.Step(new FlightInput(SwitchTo: upper));
        Assert.True(first.Craft.Frozen && second.Craft.Frozen, "they were woken before the touch: this shows nothing");
        var woken = false;
        var settled = sim.Run(Nobody, _ =>
        {
            woken |= !first.Craft.Frozen || !second.Craft.Frozen;
            return woken && vessels.All(v => v.Craft.Frozen);
        }, maxSeconds: 30);
        output.WriteLine($"woken {woken}, all frozen again {settled}: upper {upper.State} {upper.CrashReason}, {upper.Ground:0.00} m up; "
            + $"under it {first.State} and {second.State}, moved {Vector3.Distance(first.Position, a.Position.ToSingle()):0.000} and {Vector3.Distance(second.Position, b.Position.ToSingle()):0.000} m");
        Assert.True(woken, "neither was woken by its touch");
        Assert.True(settled, "they never froze again");
        Assert.Equal(FlightState.Landed, upper.State);
        Assert.True(upper.Ground > radius, $"only {upper.Ground:0.0} m up: it isn't lying on the others");
        Assert.All([first, second], v => Assert.Equal(FlightState.Landed, v.State));
        Assert.True(Vector3.Distance(first.Position, a.Position.ToSingle()) < 0.2f && Vector3.Distance(second.Position, b.Position.ToSingle()) < 0.2f, "the two under it were pushed from where they lay");
    }

    [Fact]
    public void Of_its_touches_on_other_vessels_the_hardest_is_its_touchdown()
    {
        // The two boosters lying side by side, and a third saved mid-landing, having already touched
        // another vessel gently in it (0.5 m/s), coming down onto the groove between the two at
        // about 2.6 m/s (from 0.3 m up, at 1.5 m/s). At rest on them, its touchdown is the harder
        // of its touches on them, not the first.
        var (a, b, up, _, radius) = LyingSideBySide();
        var groove = (a.Position + b.Position) / 2f + up * (radius * MathF.Sqrt(3f) + 0.3f);
        var above = a with
        {
            Id = 2, Position = groove, State = FlightState.Flying, Velocity = -up * 1.5f,
            TouchdownSpeed = 0f, TouchedDown = false, VesselTouch = 0.5f,
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [a, b, above], NextVesselId = 3 });
        var upper = sim.Flight.WorldVessels.First(v => v.Id == 2);
        sim.Step(new FlightInput(SwitchTo: upper));
        sim.Run(Nobody, _ => upper.State != FlightState.Flying, maxSeconds: 30);
        output.WriteLine($"upper {upper.State} {upper.CrashReason}, {upper.Ground:0.0} m up; touchdown {upper.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Landed, upper.State);
        Assert.True(upper.Ground > radius, $"only {upper.Ground:0.0} m up: it isn't lying on the others");
        Assert.InRange(upper.TouchdownSpeed, 2f, 3.2f);
    }

    [Fact]
    public void Crafts_touching_in_a_chain_freeze_together_once_the_last_is_ready()
    {
        // Three boosters lying side by side on the landing zone (dead level), C beside B beside A,
        // each touching the next but A not touching C: a chain. A lies the other way round, so its
        // origin (the bottom of its body) is at the far end from the others', and something on the
        // move within Scene.WakeMargin of A alone (a booster tossed up beside it) keeps A's clock
        // from running while B's and C's run: B and C are ready seconds before A. C touches only B,
        // which is ready, so checking its neighbours alone would freeze it then; but through B it
        // touches A, still awake, and a pile frozen in part is woken again by the touch. So none
        // freezes until A is ready, and then all three do, in the same step.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var along = Planet.North(Zone);
        var side = Planet.East(Zone) * (2f * Scene.RocketRadius + 0.05f);
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var bell = Vehicle.Booster.Stages[0].BellHeight;
        var b = Zone - along * ((length - bell) / 2f);
        Craft Lay(Vector3 at, Vector3 towards)
        {
            var placed = Drops.Placed(at, towards, 0f, leanDegrees: 90f, legsOut: false, clearance: 0.02f);
            return physics.Restore(Vehicle.Booster, 0, 0, placed.Position.ToSingle(), placed.Rotation, Vector3.Zero, Vector3.Zero, legDeploy: 0f, fairingOn: false);
        }
        var (cCraft, bCraft, aCraft) = (Lay(b - side, along), Lay(b, along), Lay(b + side + along * (length - bell), -along));
        var chain = new[] { cCraft, bCraft, aCraft };

        // Tossed up at 7 m/s, upright beside the middle of A on the side away from B, its bottom 1.5 m
        // up: it rises about 2.5 m and is taken away after 1.3 s, before it comes down, on the move
        // all that while (but for a moment at the top). Its body is as far from A's as the margin
        // less half the step from one axis to the next, so A is within the margin of it and B (and
        // C, further) out of it, by more than its rise changes (each checked as it goes).
        var up = Planet.Up(Zone);
        var out_ = Vector3.Normalize(side);
        var beside = Zone + side + out_ * (2f * aCraft.Widest + Scene.WakeMargin - side.Length() / 2f) + up * 1.5f;
        var tossed = physics.AddLoose(Vehicle.Booster, beside, Planet.UprightAt(beside), up * 7f, Vector3.Zero);
        const float Tossed = 1.3f;
        float Reach(Craft craft) => Gap(physics, craft, tossed);
        output.WriteLine($"from the tossed one: C {Reach(cCraft):0.0} m, B {Reach(bCraft):0.0} m, A {Reach(aCraft):0.0} m");

        const float Frame = 1f / 60;
        float? together = null;
        for (var t = 0f; t < 20f && together is null; t += Frame)
        {
            if (!tossed.Removed)
            {
                Assert.True(Reach(aCraft) < Scene.WakeMargin - 0.5f, $"A {Reach(aCraft):0.0} m from the tossed one at {t:0.00} s");
                Assert.True(MathF.Min(Reach(bCraft), Reach(cCraft)) > Scene.WakeMargin + 0.5f, $"B or C within reach at {t:0.00} s");
                if (t >= Tossed)
                {
                    Assert.True(Planet.GroundAltitude(physics.Position(tossed)) > 1f, "the tossed one came down before it was taken away");
                    physics.Remove(tossed);
                }
            }
            physics.Update(Frame, []);
            var frozen = chain.Count(c => c.Frozen);
            Assert.True(frozen is 0 or 3, $"at {t:0.00} s {frozen} of the three froze: {string.Join(", ", chain.Select(c => c.Frozen))} (C, B, A)");
            if (frozen == 3)
                together = t;
        }
        output.WriteLine($"all three froze together at {together:0.00} s");
        Assert.NotNull(together);
        Assert.True(together >= Tossed + Scene.FreezeSeconds - 0.1f, $"froze at {together:0.00} s, before A could have been ready");
    }

    [Fact]
    public void Staged_in_flight_the_stages_part_without_a_hit()
    {
        var state = Drops.Placed(Zone, Planet.East(Zone), 0f, Vehicle.TwoStage, clearance: 1000f, mission: Mission.ReachOrbit);
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var rocket = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: rocket));
        sim.Step(new FlightInput(CutThrottle: true, Stage: true));
        var parts = sim.Flight.WorldVessels.Where(v => v.Vehicle == Vehicle.TwoStage && v.State != FlightState.Ready).ToArray();
        Assert.Equal(2, parts.Length);
        sim.Run(Nobody, _ => false, maxSeconds: 5);
        output.WriteLine(string.Join("; ", parts.Select(p => $"stage {p.Stage}: {p.State} {p.CrashReason}")));
        Assert.All(parts, p => Assert.NotEqual(FlightState.Crashed, p.State));
    }

    [Fact]
    public void Staged_again_as_the_fairing_comes_off_the_halves_go_on_parting_from_the_payload()
    {
        // The satellite launcher's upper stage and satellite, fairing on, high over the zone: the
        // fairing jettisoned and, a frame later, the satellite let go while the halves are still
        // round it. Each half goes on parting from the satellite it was round (not from the stack,
        // now gone), so it doesn't join the moving crafts while still inside it.
        var state = Drops.Placed(Zone, Planet.East(Zone), 0f, Vehicle.ThreeStage, first: 1, clearance: 1000f, mission: Mission.DeploySatellite, fairingOn: true);
        using var sim = new Simulation(Mission.DeploySatellite, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var stack = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: stack));
        Assert.True(sim.Flight.JettisonsFairing);
        sim.Step(new FlightInput(CutThrottle: true, Stage: true)); // the fairing
        var halves = sim.Flight.WorldVessels.Where(v => v.Vehicle == Vehicle.FairingHalf).ToArray();
        Assert.Equal(2, halves.Length);
        sim.Step(new FlightInput(CutThrottle: true, Stage: true)); // and the satellite
        var satellite = sim.Flight.WorldVessels.Single(v => v is { Vehicle.Name: "Satellite launcher", Stage: 2 });
        var upper = sim.Flight.WorldVessels.Single(v => v is { Vehicle.Name: "Satellite launcher", Stage: 1, TopStage: 1 });
        output.WriteLine(string.Join("; ", halves.Select(h => $"half {h.Id} parting from "
            + (h.Craft.PartingFrom == satellite.Craft ? "the satellite" : h.Craft.PartingFrom == upper.Craft ? "the upper stage" : h.Craft.PartingFrom is null ? "nothing" : "a craft gone"))));
        Assert.All(halves, h => Assert.Same(satellite.Craft, h.Craft.PartingFrom));
        sim.Run(Nobody, _ => false, maxSeconds: 5);
        output.WriteLine($"5 s on: satellite {satellite.State} {satellite.CrashReason}; upper stage {upper.State} {upper.CrashReason}; halves parting {halves.Count(h => h.Craft.PartingFrom is not null)}");
        Assert.NotEqual(FlightState.Crashed, satellite.State);
        Assert.NotEqual(FlightState.Crashed, upper.State);
        Assert.All(halves, h => Assert.Null(h.Craft.PartingFrom)); // clear of it by now
    }

    /// <summary>
    /// The new flight's rocket on Home's pad (a booster), struck in its side from the east (away
    /// from the tower) at <paramref name="speed"/>, arriving about halfway up, by a booster that
    /// starts 40 m off (outside what a new flight clears from round the pad), high enough to fall
    /// that far on the way. Returns the simulation, the rocket and the striker.
    /// </summary>
    private static (Simulation Sim, Vessel Rocket, Vessel Striker) StruckOnThePad(float speed)
    {
        Vector3 middle, up, east;
        using (var first = new Simulation())
        {
            var pad = first.Flight.Controlled;
            (up, east) = (pad.LocalUp, Planet.East(pad.Position));
            middle = pad.Position + up * (RocketGeometry.StackHeight(pad.Vehicle, 0, pad.TopStage) / 2f);
        }
        const float off = 40f;
        var t = off / speed;
        var striker = Drops.Placed(Zone, east, 0f, legsOut: false, id: 0) with
        {
            Position = middle + east * off + up * (0.5f * Scene.SurfaceGravity * t * t - 3f),
            Velocity = -east * speed,
        };
        var sim = new Simulation(world: new WorldState { Vessels = [striker], NextVesselId = 1 });
        return (sim, sim.Flight.Controlled, sim.Flight.WorldVessels.First(v => v.Id == 0));
    }

    [Fact]
    public void A_wreck_stays_in_the_world_while_its_hit_plays_out()
    {
        // Struck at 16 m/s, both are wrecked as they collide; each one's body stays in the world for
        // Scene.WreckSeconds (0.4 s) after, so the hit plays out against it, and is then taken out.
        var (sim, rocket, striker) = StruckOnThePad(16f);
        using var _ = sim;
        double? strikerCrashed = null, rocketCrashed = null;
        bool? strikerSoon = null, strikerLater = null, rocketSoon = null, rocketLater = null;
        var parts = new List<StagePart>();
        int? drawnSoon = null; // the stages drawn while both wrecks are still in the world: none, as a wreck is gone from the scene
        sim.Run(Nobody, _ =>
        {
            if (strikerCrashed is null && striker.State == FlightState.Crashed)
                strikerCrashed = sim.Time;
            if (rocketCrashed is null && rocket.State == FlightState.Crashed)
                rocketCrashed = sim.Time;
            if (strikerCrashed is { } s)
            {
                if (strikerSoon is null && sim.Time >= s + 0.3)
                    strikerSoon = striker.Craft.Removed;
                if (strikerLater is null && sim.Time >= s + 0.5)
                    strikerLater = striker.Craft.Removed;
            }
            if (rocketCrashed is { } r)
            {
                if (rocketSoon is null && sim.Time >= r + 0.3)
                    rocketSoon = rocket.Craft.Removed;
                if (drawnSoon is null && strikerCrashed is not null && sim.Time >= r + 0.3)
                {
                    sim.Physics.GetParts(parts);
                    drawnSoon = parts.Count;
                }
                if (rocketLater is null && sim.Time >= r + 0.5)
                    rocketLater = rocket.Craft.Removed;
            }
            return strikerLater is not null && rocketLater is not null;
        }, maxSeconds: 4);
        output.WriteLine($"the rocket {rocket.State} {rocket.CrashReason} at {rocketCrashed:0.000} s; the striker {striker.State} {striker.CrashReason} at {strikerCrashed:0.000} s");
        Assert.StartsWith("Hit another vessel at", striker.CrashReason);
        Assert.StartsWith("Hit another vessel at", rocket.CrashReason);
        Assert.False(strikerSoon, "the striker's wreck was taken out within 0.3 s");
        Assert.True(strikerLater, "the striker's wreck was still there 0.5 s on");
        Assert.False(rocketSoon, "the rocket's wreck was taken out within 0.3 s");
        Assert.True(rocketLater, "the rocket's wreck was still there 0.5 s on");
        Assert.Equal(0, drawnSoon); // in the world, but not drawn
    }

    [Fact]
    public void A_hard_foot_first_hit_on_anothers_side_is_a_hit()
    {
        // Flown into the standing one's side at 20 m/s with its legs out, its leg 0 leading, the
        // foot about halfway up the other's side: the foot strikes it (its axis and its widest
        // radius are well clear of the other's, 4.9 m off), and the hit is judged then.
        var below = Standing();
        var up = Planet.Up(below.Position.ToSingle());
        var east = Planet.East(below.Position.ToSingle());
        var radius = Vehicle.Booster.Stages[0].Radius;
        var striker = Drops.Placed(Zone, east, 0f, legsOut: true, id: 1);
        var foot = Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, 0), striker.Rotation); // pointing east
        striker = striker with
        {
            Position = below.Position + up * 9f - east * (radius + 1f) - foot,
            Velocity = east * 20f,
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [below, striker], NextVesselId = 2 });
        var (struck, hitter) = (sim.Flight.WorldVessels.First(v => v.Id == 0), sim.Flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: hitter));
        var axes = float.MaxValue;
        sim.Run(Nobody, _ =>
        {
            axes = MathF.Min(axes, Vector3.Distance(struck.Position, hitter.Position + Vector3.Dot(struck.Position - hitter.Position, up) * up));
            return hitter.State == FlightState.Crashed || struck.State == FlightState.Crashed;
        }, maxSeconds: 0.5);
        output.WriteLine($"struck {struck.State} {struck.CrashReason}; striker {hitter.State} {hitter.CrashReason}; axes came within {axes:0.0} m");
        Assert.True(struck.CrashReason?.StartsWith("Hit another vessel at") == true || hitter.CrashReason?.StartsWith("Hit another vessel at") == true,
            "neither was judged hit");
    }

    [Fact]
    public void Struck_hard_just_after_its_own_touchdown_it_is_hit()
    {
        // Set down upright on its legs at 3 m/s, it is struck in the side at 16 m/s by another (its
        // legs stowed) a tenth of a second after its touchdown, while its landing is still going on:
        // the blow is a touch with another vessel, judged by their closing speed, so a hit (held
        // over its gentle touchdown, which wrecks nothing, and then told).
        var landing = Drops.Placed(Zone, Planet.East(Zone), 3f, id: 0);
        var up = Planet.Up(landing.Position.ToSingle());
        var east = Planet.East(landing.Position.ToSingle());
        var radius = Vehicle.Booster.Stages[0].Radius;
        var touchdown = Drops.Clearance / 3f; // seconds, about
        var striker = Drops.Placed(Zone, east, 0f, legsOut: false, id: 1) with
        {
            Position = landing.Position - east * (2f * radius + 16f * (touchdown + 0.1f)) + up * 3f,
            Velocity = east * 16f,
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [landing, striker], NextVesselId = 2 });
        var (struck, hitter) = (sim.Flight.WorldVessels.First(v => v.Id == 0), sim.Flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: struck));
        double? touched = null, hit = null;
        sim.Run(Nobody, _ =>
        {
            if (touched is null && struck.Ground < 0.12f)
                touched = sim.Time;
            if (hit is null && hitter.State == FlightState.Crashed)
                hit = sim.Time;
            return struck.State == FlightState.Crashed;
        }, maxSeconds: 3);
        output.WriteLine($"touched down at {touched:0.000} s, hit at {hit:0.000} s; struck {struck.State} {struck.CrashReason}; striker {hitter.State} {hitter.CrashReason}");
        Assert.NotNull(touched);
        Assert.NotNull(hit);
        Assert.InRange(hit.Value - touched.Value, 0.02, 0.25); // the blow lands while its touchdown is still going on
        Assert.StartsWith("Hit another vessel at", struck.CrashReason);
    }

    [Fact]
    public void Touching_anothers_foot_is_touching_it()
    {
        // A booster with its legs out, high over the zone, and another lying under one of its feet,
        // its body just into the foot's pad but some 2.8 m clear of the standing one's body (axis
        // to axis, less both radii): after a step, Jolt has each touching the other, as a hit
        // between them, or one resting on the other, is told by. 2 m further off, neither is.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var at = Zone + Planet.Up(Zone) * 500f;
        var up = Planet.Up(at);
        var upright = Planet.UprightAt(at);
        var legs = physics.AddLoose(Vehicle.Booster, at, upright, Vector3.Zero, Vector3.Zero);
        physics.SetLegs(legs, 1f);
        var foot = at + Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, 0), upright);
        var outward = Vector3.Normalize(foot - at - up * Vector3.Dot(foot - at, up));
        var along = Vector3.Cross(up, outward); // lying across the foot's way out
        var lying = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(up, along)), MathF.PI / 2) * upright);
        Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitY, lying), along) > 0.999f);
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var radius = Vehicle.Booster.Stages[0].Radius;
        Craft LyingUnder(float further) =>
            physics.AddLoose(Vehicle.Booster, foot - up * (radius - 0.01f) + outward * further - along * (length / 2f), lying, Vector3.Zero, Vector3.Zero);

        var off = LyingUnder(2f);
        physics.Update(1f / 60, []);
        Assert.False(physics.Touching(off, SolidKind.Craft));
        Assert.False(physics.Touching(legs, SolidKind.Craft));
        physics.Remove(off);
        var under = LyingUnder(0f);
        physics.Update(1f / 60, []);
        Assert.True(physics.Touching(under, SolidKind.Craft), "the one under the foot isn't touching the one standing on it");
        Assert.True(physics.Touching(legs, SolidKind.Craft, CraftPart.Foot), "the one standing on it isn't touching the one under its foot, by its foot");
    }
}
