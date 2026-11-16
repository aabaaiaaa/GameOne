using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Each witnessed vessel's soonest fast collision, predicted ahead (<see cref="Collisions"/>): with
/// another vessel, the ground or the sea, the service tower or a light mast, and only a fast one
/// (over <see cref="Scene.FastCollisionSpeed"/> as it hits, with its present thrust). A pass wide of
/// another, a slow collision, a landing burn and a launch's stages parting beside the tower are none.
/// The worlds are <see cref="FastCollisionTests"/>' builders; "N s apart" is their centres coming together in N s.
/// </summary>
public sealed class CollisionTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;

    private static Collision? Soonest(Simulation sim, Vessel watched, Collisions? collisions = null) =>
        (collisions ?? new Collisions(sim.Flight)).Soonest(watched, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds);

    [Theory]
    [InlineData(500f)]
    [InlineData(8000f)]
    public void Two_crafts_head_on_are_seen_coming(float closing)
    {
        const float Apart = 10f;
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(closing, 5000f, Apart));
        using var _ = sim;
        var o = Soonest(sim, flown);
        output.WriteLine($"{o}");
        Assert.NotNull(o);
        var collision = o.Value;
        Assert.Equal(SolidKind.Craft, collision.With);
        Assert.Same(other, collision.Other);
        Assert.Same(flown, collision.Watched);
        var expected = Apart - sim.Time - (Collisions.Size(flown) + Collisions.Size(other!)) / closing;
        Assert.InRange(collision.Seconds, expected * 0.98, expected * 1.02);
        // Closing now: each set off at closing/2, but along its own east, and on a planet 20 km
        // across the two easts 5 km apart differ by 19° (at 500 m/s, 10 s apart), so they close at
        // 494 m/s now, and at 500 as they collide, each path bent towards the other's.
        var now = (other!.Velocity - flown.Velocity).Length();
        Assert.InRange(collision.ClosingSpeed, now * 0.99f, now * 1.01f);
        Assert.InRange(collision.HitSpeed, closing * 0.99f, closing * 1.01f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(15f)]
    public void A_collision_19_s_out_is_seen_coming_at_once(float sideways)
    {
        // Within the horizon from the start: the paths flown ahead, not one pull for the whole way
        // (which, on this small a planet, found a hit 20 s out only 15 s off).
        const float Apart = 19f;
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, Apart, sideways: sideways));
        using var _ = sim;
        var o = Soonest(sim, flown);
        output.WriteLine($"at {sim.Time:0.000} s: {o}");
        Assert.NotNull(o);
        Assert.Same(other, o.Value.Other);
        Assert.InRange(o.Value.Seconds, (Apart - sim.Time) * 0.99, (Apart - sim.Time) * 1.01);
        Assert.InRange(o.Value.HitSpeed, 990f, 1050f);
    }

    [Fact]
    public void A_pass_is_timed_to_the_closest_approach()
    {
        // Slow enough (300 m/s) that reaching each other's size comes 0.8% before the centres'
        // closest approach: Seconds is the closest approach's, to 0.5%, so a pass has one time
        // running through it, to and then since its closest.
        const float Apart = 10f, Closing = 300f;
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(Closing, 5000f, Apart));
        using var _ = sim;
        var o = Soonest(sim, flown);
        output.WriteLine($"{o}; reaching each other's size {(Collisions.Size(flown) + Collisions.Size(other!)) / Closing:0.000} s before");
        Assert.NotNull(o);
        var closest = Apart - sim.Time;
        Assert.InRange(o.Value.Seconds, closest * 0.995, closest * 1.005);
    }

    [Theory]
    [InlineData(240f, true)]
    [InlineData(190f, false)]
    public void Only_a_collision_over_the_threshold_is_a_fast_one(float closing, bool fast)
    {
        // Either side of Scene.FastCollisionSpeed (216 m/s), in the vacuum, hit 3 s on.
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(closing, 5000f, 3f));
        using var _ = sim;
        var o = Soonest(sim, flown);
        output.WriteLine($"{o}");
        Assert.Equal(fast, o is not null);
        if (o is { } collision)
            Assert.True(collision.HitSpeed > Scene.FastCollisionSpeed);
    }

    [Fact]
    public void Two_crafts_passing_wide_are_not_colliding()
    {
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 10f, sideways: 50f));
        using var _ = sim;
        Assert.Null(Soonest(sim, flown));
    }

    [Fact]
    public void A_slow_collision_is_not_a_fast_one()
    {
        Assert.True(150f < Scene.FastCollisionSpeed);
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(150f, 5000f, 10f));
        using var _ = sim;
        Assert.Null(Soonest(sim, flown));
    }

    [Theory]
    [InlineData(500f)]
    [InlineData(4000f)]
    public void A_dive_into_the_ground_is_seen_coming(float speed)
    {
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Into("ground", speed, 10f));
        using var _ = sim;
        var collisions = new Collisions(sim.Flight);
        var o = Soonest(sim, flown, collisions);
        var orbit = Orbit.OverGround(GroundBody.Planet, flown.CentreOfMass, flown.Velocity);
        output.WriteLine($"at {sim.Time:0.000} s: {o}; Orbit says {orbit.SecondsToEnd:0.000} s (ends on the ground: {orbit.EndsOnGround})");
        Assert.NotNull(o);
        Assert.Equal(SolidKind.Ground, o.Value.With);
        Assert.Null(o.Value.Other);
        Assert.Equal("the ground", Collisions.Name(o.Value));
        Assert.InRange(o.Value.Seconds, orbit.SecondsToEnd * 0.95, orbit.SecondsToEnd * 1.05);

        // A tenth of a second on, inside the ground's check, its answer is counted down by the
        // time gone, to within a step.
        var (asked, then) = (sim.Time, o.Value.Seconds);
        sim.Run(Nobody, _ => false, maxSeconds: 0.1);
        Assert.True(sim.Time - asked < Scene.GroundCheckSeconds);
        var later = Soonest(sim, flown, collisions);
        Assert.NotNull(later);
        Assert.InRange(then - later.Value.Seconds - (sim.Time - asked), -RocketPhysics.StepSeconds, RocketPhysics.StepSeconds);
    }

    [Fact]
    public void Slowed_the_ground_is_asked_again_on_real_time()
    {
        // Two asked of the same dive, one at the game's full rate and one slowed tenfold, then the
        // engine lit for three frames (a twentieth of a second of game time, half a second of real
        // time slowed): the one at full rate is still counting its first answer down, the slowed
        // one has asked again, and has the burn's new time.
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Into("ground", 500f, 3f));
        using var _ = sim;
        var (full, slowed) = (new Collisions(sim.Flight), new Collisions(sim.Flight));
        const float Scale = 0.1f;
        var first = full.Soonest(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds)!.Value.Seconds;
        slowed.Soonest(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds, Scale);
        var asked = sim.Time;
        for (var i = 0; i < 3; i++)
            sim.Step(new FlightInput(FullThrottle: true));
        Assert.True(flown.EngineLit);
        var gone = sim.Time - asked;
        Assert.InRange(gone, Scene.GroundCheckSeconds * Scale, Scene.GroundCheckSeconds); // over the slowed check, under the full one
        // Its size-sphere's path, as the ground is asked (from its middle, its size the clearance).
        var middle = Vector3.Transform(new Vector3(0f, flown.Height / 2f, 0f), flown.Model);
        var fresh = Orbit.OverGround(GroundBody.Planet, middle, flown.Velocity, clearance: Collisions.Size(flown)).SecondsToEnd;
        var counted = full.Soonest(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds)!.Value.Seconds;
        var asked2 = slowed.Soonest(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds, Scale)!.Value.Seconds;
        output.WriteLine($"counted down {counted:0.00000} s; asked again {asked2:0.00000} s; the orbit now {fresh:0.00000} s");
        Assert.True(Math.Abs(fresh - (first - gone)) > 1e-3, "the burn didn't change its time: this shows nothing");
        Assert.Equal(first - gone, counted, 1e-6);
        Assert.Equal(fresh, asked2, 1e-6);
    }

    [Theory]
    [InlineData("floe", "an ice floe")]
    [InlineData("ship", "the drone ship")]
    public void A_dive_onto_a_raft_is_seen_coming_as_the_raft(string what, string name)
    {
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Into(what, 500f, 5f));
        using var _ = sim;
        var o = Soonest(sim, flown);
        output.WriteLine($"{o}");
        Assert.NotNull(o);
        Assert.Equal(SolidKind.Raft, o.Value.With);
        Assert.Equal(name, Collisions.Name(o.Value));
    }

    [Fact]
    public void A_craft_flying_at_the_tower_is_seen_coming()
    {
        const float Off = 5f;
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Into("tower", 1000f, Off, diveDegrees: 30f));
        using var _ = sim;
        var o = Soonest(sim, flown);
        output.WriteLine($"{o}");
        Assert.NotNull(o);
        Assert.Equal(SolidKind.Tower, o.Value.With);
        var expected = Off - sim.Time;
        Assert.InRange(o.Value.Seconds, expected * 0.98, expected * 1.02);
        Assert.Equal("the service tower", Collisions.Name(o.Value));
    }

    [Fact]
    public void A_craft_flying_at_a_mast_is_seen_coming()
    {
        const float Off = 5f;
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Into("mast", 1000f, Off));
        using var _ = sim;
        var o = Soonest(sim, flown);
        output.WriteLine($"{o}");
        Assert.NotNull(o);
        Assert.Equal(SolidKind.Mast, o.Value.With);
        var expected = Off - sim.Time;
        Assert.InRange(o.Value.Seconds, expected * 0.98, expected * 1.02);
        Assert.Equal("a light mast", Collisions.Name(o.Value));
    }

    [Fact]
    public void A_landing_burn_is_no_fast_collision()
    {
        var (ground, _, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var booster = Drops.Placed(ground, Planet.East(ground), 250f, clearance: 800f) with { Throttle = 1f };
        var (sim, flown, _) = FastCollisionTests.Fly(new WorldState { Vessels = [booster], NextVesselId = 1 });
        using var _ = sim;
        Assert.True(flown.EngineLit, "its engine isn't lit: this shows nothing");
        var collisions = new Collisions(sim.Flight);
        var frames = 0;
        sim.Run(Nobody, _ =>
        {
            var o = Soonest(sim, flown, collisions);
            Assert.True(o is null, $"at {sim.Time:0.000} s, {flown.Ground:0} m up at {flown.Velocity.Length():0} m/s: {o}");
            return ++frames >= 60;
        }, maxSeconds: 2);
        Assert.Equal(60, frames);
    }

    [Fact]
    public void Parting_and_lifting_off_beside_the_tower_is_no_fast_collision()
    {
        // The launch to orbit from Home's pad, beside its tower and masts: its first 30 s, and on
        // through the booster's dropping to 5 s after it, asked of the flown vessel and of every
        // other part of it (as a small view would watch the dropped booster).
        using var sim = new Simulation(Mission.ReachOrbit);
        var pilot = new OrbitAutopilot();
        var collisions = new Collisions(sim.Flight);
        double? staged = null;
        var frames = 0;
        var ran = sim.Run(pilot.Fly, f =>
        {
            for (var i = 0; i < f.Vessels.Count; i++)
            {
                var v = f.Vessels[i];
                var o = collisions.Soonest(v, f.WorldVessels, Scene.CollisionHorizonSeconds);
                Assert.True(o is null, $"at {sim.Time:0.000} s, {v.Name} (stage {v.Stage}) {Planet.GroundAltitude(v.Position):0} m up at {v.Velocity.Length():0} m/s: {o}");
            }
            frames++;
            if (staged is null && f.Vessels.Count > 1)
                staged = sim.Time;
            return sim.Time >= 30 && staged is { } at && sim.Time >= at + 5;
        }, maxSeconds: 120);
        output.WriteLine($"{frames} frames; staged at {staged:0.0} s; ended at {sim.Time:0.0} s, {sim.Flight.Ground:0} m up");
        Assert.True(ran, "it never staged");
    }

    [Theory]
    [InlineData("mast")]
    [InlineData("pair")]
    public void Asking_allocates_nothing(string world)
    {
        // Asked every step for the flown vessel and the small views': flying at a pad's mast (the
        // towers' and masts' checks, and the ground's orbit), or at another vessel (the pairs').
        var (sim, flown, _) = FastCollisionTests.Fly(world == "mast" ? FastCollisionTests.Into("mast", 1000f, 5f) : FastCollisionTests.Pair(2000f, 1000f, 5f));
        using var _ = sim;
        var collisions = new Collisions(sim.Flight);
        var vessels = sim.Flight.WorldVessels;
        Assert.NotNull(collisions.Soonest(flown, vessels, Scene.CollisionHorizonSeconds)); // each is hit: so its checks ran to the end
        var clock = System.Diagnostics.Stopwatch.StartNew(); // before the count: it is allocated itself
        var before = GC.GetAllocatedBytesForCurrentThread();
        const int Asks = 200;
        for (var i = 0; i < Asks; i++)
            collisions.Soonest(flown, vessels, Scene.CollisionHorizonSeconds, scale: i % 2 == 0 ? 1f : 0f); // at a scale of 0, the ground is asked again every time
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        clock.Stop();
        output.WriteLine($"{clock.Elapsed.TotalMilliseconds * 1000 / Asks:0.0} µs an ask; {allocated} bytes allocated");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_world_loaded_at_the_same_time_is_predicted_from_its_own_vessels()
    {
        // The world's snapshot is read once a step, keyed by its time: a restart or load comes back
        // at the time it was captured, with the same list (the flight's, refilled) and as many in
        // it, so only the flight's own count of starts tells the snapshot it is another world's.
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 5f));
        using var _ = sim;
        var collisions = new Collisions(sim.Flight);
        var saved = sim.Flight.Capture();
        var (time, count) = (sim.Flight.WorldTime, sim.Flight.WorldVessels.Count);
        Assert.Same(other, Soonest(sim, flown, collisions)?.Other); // the snapshot taken at this time
        sim.Flight.Load(saved, sim.Flight.Mission);
        Assert.Equal(time, sim.Flight.WorldTime);
        Assert.Equal(count, sim.Flight.WorldVessels.Count); // (as many: else the old count would rebuild it anyway)
        var (flownAgain, otherAgain) = (sim.Flight.WorldVessels.Single(v => v.Id == flown.Id), sim.Flight.WorldVessels.Single(v => v.Id == other!.Id));
        var o = Soonest(sim, flownAgain, collisions);
        output.WriteLine($"{o}");
        Assert.NotNull(o);
        Assert.Same(otherAgain, o.Value.Other);
    }

    [Fact]
    public void The_warning_lead_is_the_turn_the_dodge_and_the_reaction()
    {
        {
            var (sim, a, b) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 10f));
            using var _ = sim;
            Assert.True(a.Fuel > 0f);
            var o = new Collision(a, SolidKind.Craft, b, 10, 1000f, 1000f, a.Position);
            // The turn: a right angle at its turn rate, and the steering's lag behind its aim
            // (asin(rate (1 + damping / blend) / SteerGain) more, at that rate).
            var behind = a.TurnRate * (1f + RocketPhysics.AngularDamping / RocketPhysics.SteerBlendRate) / RocketPhysics.SteerGain;
            var turn = (MathF.PI / 2f + MathF.Asin(behind)) / a.TurnRate;
            var expected = turn + MathF.Sqrt(2f * (Collisions.Size(a) + Collisions.Size(b!)) / a.MaxAcceleration) + 1.5f;
            Assert.Equal(expected, Collisions.WarningSeconds(a, o), 1e-3);
        }
        {
            var (sim, a, b) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 10f, fuel: 0f));
            using var _ = sim;
            var o = new Collision(a, SolidKind.Craft, b, 10, 1000f, 1000f, a.Position);
            Assert.Equal(double.PositiveInfinity, Collisions.WarningSeconds(a, o));
        }
    }

    [Fact]
    public void What_is_coming_is_named_by_its_kind()
    {
        var (sim, a, b) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 10f));
        using var _ = sim;
        var (land, _, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var sea = Planet.Centre + IceTests.FloeBand("Boreal Sea") * Planet.SeaRadius;
        Assert.True(Terrain.Height(Planet.Up(sea)) < 0f, "the sea's point isn't under the sea");
        Collision Of(SolidKind with, Vessel? other, Vector3 at) => new(a, with, other, 5, 1000f, 1000f, at);
        Assert.Equal("another vessel", Collisions.Name(Of(SolidKind.Craft, b, a.Position)));
        Assert.Equal("the ground", Collisions.Name(Of(SolidKind.Ground, null, land)));
        Assert.Equal("the water", Collisions.Name(Of(SolidKind.Ground, null, sea)));
        Assert.Equal("the service tower", Collisions.Name(Of(SolidKind.Tower, null, LaunchSites.Home.TowerFoot)));
        Assert.Equal("a light mast", Collisions.Name(Of(SolidKind.Mast, null, land)));
        var ship = Rafts.Ship;
        Assert.Equal("the drone ship", Collisions.Name(Of(SolidKind.Raft, null, ship.Current.Position + ship.Normal * ship.Freeboard)));
        var floe = Floes.Near(sea, 300f).MaxBy(f => f.HalfLength)!;
        Rafts.Now(floe);
        Assert.Equal("an ice floe", Collisions.Name(Of(SolidKind.Raft, null, floe.Current.Position + floe.Normal * floe.Freeboard)));
    }

    [Fact]
    public async Task A_pair_whose_march_goes_to_NaN_is_beyond_the_horizon_at_once()
    {
        // A NaN in a velocity makes every pull and approach NaN: the march must end, not spin
        // (asked on its own thread, so a spin fails the test rather than hanging the run).
        var above = new Vector3(0f, 1000f, 0f);
        var nan = new Vector3(float.NaN, 0f, 0f);
        var march = Task.Run(() => Collisions.Closest(GroundBody.Planet, above, nan, false, Vector3.Zero, above + new Vector3(500f, 0f, 0f), Vector3.Zero, false,
            Vector3.Zero, 1f, Scene.CollisionHorizonSeconds, out _, out _, out _));
        Assert.Same(march, await Task.WhenAny(march, Task.Delay(TimeSpan.FromSeconds(5))));
        var seconds = await march;
        Assert.True(seconds > Scene.CollisionHorizonSeconds, $"{seconds}");
    }
}
