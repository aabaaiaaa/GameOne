using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Fast collisions, flown as the game flies them: two boosters colliding head on or crossing (in the
/// vacuum and in the air), and a fast craft hitting the ground, a frozen vessel, a floe and a
/// light mast, each set to collide 3 s after the start. Each must be hit and judged, never passed
/// through: the forced slow-down (<see cref="Flight.Slowed"/>) steps each finely enough as it
/// hits. The world builders (<see cref="Pair"/>, <see cref="Into"/>) are the other fast
/// collisions' tests' too.
/// </summary>
public sealed class FastCollisionTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>
    /// When every collision here is set to happen, in game seconds from the start: 3 s, well inside
    /// every horizon, and half a physics step more. A collision at a step's end is seen however fast
    /// (the two overlap there, even closing at 8,000 m/s); half-way between two steps is the
    /// hardest, as each step then leaves them furthest from it, before and after.
    /// </summary>
    private const float HitsAt = 3f + RocketPhysics.StepSeconds / 2f;

    /// <summary>How long after the collision a run waits for it to be judged.</summary>
    private const float Wait = 3f;

    internal static readonly float BoosterHeight = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
    internal static readonly float BoosterWidest = MathF.Max(Vehicle.Booster.Stages[0].Radius, Vehicle.Booster.Stages[0].BellRadius);

    /// <summary>A booster's size for collision: the radius of the sphere round its middle (as <see cref="Collisions"/> sizes a vessel).</summary>
    internal static readonly float BoosterSize = MathF.Sqrt(BoosterHeight * BoosterHeight / 4f + BoosterWidest * BoosterWidest);

    // ---- The world builders ----

    /// <summary>
    /// Two boosters, their legs stowed and their engines off, each nose first along its way, over
    /// level ground, the middles of their size-spheres coming together <paramref name="altitude"/> above
    /// the sea after <paramref name="seconds"/>: head on (one going east, one west, each at
    /// <paramref name="closing"/>/2, so their middles start about <c>closing × seconds</c> apart),
    /// or <paramref name="crossing"/> (one going east, one north, each at closing/√2). They pass
    /// <paramref name="sideways"/> apart at their closest (along the level, across their way). The
    /// first (vessel 0, going east) is laid over <paramref name="lieAcross"/> degrees from its line
    /// of travel, turned about the up there, so at 90 it lies across its way. Each stage's tank is
    /// <paramref name="fuel"/> full. The flown one is listed first (see <see cref="Fly"/>): vessel
    /// 0 with <paramref name="flownFirst"/>, else vessel 1. They hit over level ground, or over
    /// <paramref name="over"/> if given.
    /// </summary>
    /// <remarks>
    /// On a planet 10 km across, a few seconds at these speeds is a long way round it, and the
    /// turning frame's sideways pull on something at 4,000 m/s is about 40 m/s², so each start is
    /// found by flying the physics' own fall backwards and forwards (<see cref="Shot"/>) until it
    /// arrives where and when it should. Crossing, each goes at closing/√2, so over
    /// 2√2 × <see cref="Scene.TopSpeed"/> Jolt caps them, and they collide later and slower than asked.
    /// </remarks>
    internal static WorldState Pair(float closing, float altitude, float seconds, bool crossing = false, float sideways = 0f,
        bool flownFirst = true, float fuel = 0.3f, float lieAcross = 0f, Vector3? over = null)
    {
        var hitPoint = over is { } place ? Planet.Centre + Planet.Up(place) * (Planet.SeaRadius + altitude) : PairHitPoint(altitude);
        var up = Planet.Up(hitPoint);
        var (east, north) = (Planet.East(hitPoint), Planet.North(hitPoint));
        var (way0, way1) = crossing ? (east, north) : (east, -east);
        var speed = crossing ? closing / MathF.Sqrt(2f) : closing / 2f;
        var across = Vector3.Normalize(Vector3.Cross(up, way0 - way1)) * (sideways / 2f);
        var first = Flying(hitPoint - across, way0, speed, seconds, 0, fuel, lieAcross);
        var second = Flying(hitPoint + across, way1, speed, seconds, 1, fuel, 0f);
        return new WorldState { Vessels = flownFirst ? [first, second] : [second, first], NextVesselId = 2 };
    }

    /// <summary>Where <see cref="Pair"/>'s middles come together: <paramref name="altitude"/> above the sea, over level ground.</summary>
    internal static Vector3 PairHitPoint(float altitude)
    {
        var (ground, _, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        return Planet.Centre + Planet.Up(ground) * (Planet.SeaRadius + altitude);
    }

    /// <summary>
    /// A booster (legs stowed, engine off, nose first) at <paramref name="speed"/> into
    /// <paramref name="what"/>, its middle reaching it after <paramref name="seconds"/>, diving
    /// <paramref name="diveDegrees"/> below the level, heading east: "ground" (level ground),
    /// "frozen" (the middle of a booster standing frozen on <see cref="Scene.LandingZoneCentre"/>),
    /// "floe" (the middle of the deck of the largest floe near the Boreal Sea's band), "ship" (the
    /// middle of Home's drone ship's deck), "mast" (the middle of the first pad's first light
    /// mast, flown level and across the pad's diagonal, so nothing else is in its way) or "tower"
    /// (the middle of the second launch site's service tower, dived at from the side away from its pad). The flying one is vessel 0, listed first.
    /// </summary>
    internal static WorldState Into(string what, float speed, float seconds, float diveDegrees = 45f) => Aimed(what, speed, seconds, diveDegrees).World;

    /// <summary><see cref="Into"/>'s world, and the point it aims at.</summary>
    private static (WorldState World, Vector3 Target) Aimed(string what, float speed, float seconds, float diveDegrees = 45f)
    {
        Vector3 Dive(Vector3 at)
        {
            var angle = float.DegreesToRadians(diveDegrees);
            return Vector3.Normalize(Planet.East(at) * MathF.Cos(angle) - Planet.Up(at) * MathF.Sin(angle));
        }
        switch (what)
        {
            case "ground":
            {
                var (at, _, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
                return (World(Flying(at, Dive(at), speed, seconds, 0)), at);
            }
            case "frozen":
            {
                var standing = StandingOnTheZone() with { Id = 1 };
                var middle = Middle(standing.Position.ToSingle(), standing.Rotation);
                return (World(Flying(middle, Dive(middle), speed, seconds, 0), standing), middle);
            }
            case "floe":
            {
                Rafts.Time = 0.0;
                var raft = Floes.Near(Planet.Centre + IceTests.FloeBand("Boreal Sea") * Planet.SeaRadius, 300f).MaxBy(f => f.HalfLength)!;
                raft.Set(seconds); // where its deck will be as the booster reaches it
                var deck = raft.Current.Position + raft.Normal * raft.Freeboard;
                raft.Set(0.0); // the simulation's world starts at time 0
                return (World(Flying(deck, Dive(deck), speed, seconds, 0)), deck);
            }
            case "ship":
            {
                Rafts.Time = 0.0;
                var ship = Rafts.Ship;
                ship.Set(seconds); // where its deck will be as the booster reaches it
                var deck = ship.Current.Position + ship.Normal * ship.Freeboard;
                ship.Set(0.0);
                return (World(Flying(deck, Dive(deck), speed, seconds, 0)), deck);
            }
            case "tower":
            {
                // Diving at the middle of the second site's tower (Home's pad holds the new flight's
                // rocket, beside its tower), heading east: from the side away from its pad (it stands
                // at the pad's west edge), and from above, so neither the ground nor a mast is hit first.
                var site = LaunchSites.All[1];
                var middle = site.ToWorld(Scene.TowerOffset + new Vector3(0f, Scene.TowerHeight / 2f, 0f));
                var way = Dive(middle);
                Assert.True(Clearance(middle, way, speed, seconds) > BoosterSize, "the way to the tower is not clear of the ground");
                return (World(Flying(middle, way, speed, seconds, 0)), middle);
            }
            case "mast":
            {
                var place = PadLights.All.First(p => p.IsPad);
                var mast = place.Masts[0];
                var middle = place.ToWorld(mast.Foot + new Vector3(0f, mast.Height / 2f, 0f));
                // Level, across the pad's diagonal (the mast stands on it), so its way passes the
                // pad's square, its tower and the other masts by: whichever way along that keeps
                // further over the ground.
                var up = Planet.Up(middle);
                var diagonal = Vector3.Transform(Vector3.Normalize(new Vector3(mast.Foot.X, 0f, mast.Foot.Z)), place.Frame);
                var level = Vector3.Normalize(Vector3.Cross(up, diagonal));
                var way = new[] { level, -level }.MaxBy(w => Clearance(middle, w, speed, seconds));
                Assert.True(Clearance(middle, way, speed, seconds) > BoosterWidest + 1f, "the way to the mast is not clear of the ground");
                return (World(Flying(middle, way, speed, seconds, 0)), middle);
            }
            default:
                throw new ArgumentException(what);
        }
    }

    /// <summary>
    /// A booster (legs stowed, engine off, nose first) at <paramref name="speed"/>, flying east
    /// level over a booster standing frozen on <see cref="Scene.LandingZoneCentre"/> (as
    /// <see cref="Into"/>'s "frozen"), its middle <paramref name="above"/> over that one's after
    /// <paramref name="seconds"/>: passing it by, beside it and then behind. The flying one is
    /// vessel 0, listed first.
    /// </summary>
    internal static WorldState Over(float speed, float seconds, float above)
    {
        var standing = StandingOnTheZone() with { Id = 1 };
        var middle = Middle(standing.Position.ToSingle(), standing.Rotation);
        return World(Flying(middle + Planet.Up(middle) * above, Planet.East(middle), speed, seconds, 0), standing);
    }

    private static WorldState World(params VesselState[] vessels) => new() { Vessels = vessels, NextVesselId = vessels.Length };

    /// <summary>
    /// A booster (legs stowed, engine off, nose first) at <paramref name="speed"/>, flying level
    /// past two masts in a row: along one side of a landing zone (no tower or rocket there), from
    /// one corner's mast to the next, <paramref name="beside"/> outside the line between them and
    /// 12 m up. Close enough that each is a collision as predicted (inside its size-sphere's reach),
    /// wide enough to miss both. Its middle reaches the place beside the first after
    /// <paramref name="seconds"/>. Returns the world and each mast's middle, in the order passed.
    /// </summary>
    internal static (WorldState World, Vector3 First, Vector3 Second) PastTwoMasts(float speed, float seconds, float beside, float fuel = 0.3f)
    {
        var side = (Scene.LandingZoneRadius + Scene.MastOut) * MathF.Sqrt(2f);
        foreach (var place in PadLights.All.Where(p => p.Ship is null))
        {
            var masts = place.Masts;
            for (var i = 0; i < masts.Length; i++)
                for (var j = 0; j < masts.Length; j++)
                {
                    var (a, b) = (masts[i].Foot, masts[j].Foot);
                    var along = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
                    if (i == j || along.Length() > side * 1.1f) // across the zone's diagonal, not a side
                        continue;
                    var outward = Vector3.Normalize(new Vector3(a.X + b.X, 0f, a.Z + b.Z));
                    var up = new Vector3(0f, 12f, 0f);
                    var (first, second) = (place.ToWorld(a + outward * beside + up), place.ToWorld(b + outward * beside + up));
                    var way = Vector3.Normalize(second - first);
                    if (Clearance(first, way, speed, seconds) < BoosterWidest + 3f)
                        continue;
                    var middle = new Vector3(0f, Scene.MastHeight / 2f, 0f);
                    return (World(Flying(first, way, speed, seconds, 0, fuel)), place.ToWorld(a + middle), place.ToWorld(b + middle));
                }
        }
        throw new InvalidOperationException("no landing zone has two masts in a row with a clear way past them");
    }

    /// <summary>
    /// The simulation of <paramref name="world"/> (one of the builders'), its first-listed vessel
    /// flown (switched to in its first frame), and the other, if any.
    /// </summary>
    internal static (Simulation Sim, Vessel Flown, Vessel? Other) Fly(WorldState world)
    {
        var sim = new Simulation(world: world);
        var vessels = sim.Flight.WorldVessels;
        var flown = vessels.First(v => v.Id == world.Vessels[0].Id);
        var other = world.Vessels.Count > 1 ? vessels.First(v => v.Id == world.Vessels[1].Id) : null;
        sim.Step(new FlightInput(SwitchTo: flown));
        return (sim, flown, other);
    }

    /// <summary>
    /// A booster (numbered <paramref name="id"/>, legs stowed, engine off) that flies its middle
    /// to <paramref name="to"/> in <paramref name="seconds"/>, arriving along <paramref name="way"/>,
    /// having set off at <paramref name="speed"/>, its nose along that way (laid over from it by
    /// <paramref name="lieAcross"/> degrees about the up at <paramref name="to"/>).
    /// </summary>
    internal static VesselState Flying(Vector3 to, Vector3 way, float speed, float seconds, int id, float fuel = 0.3f, float lieAcross = 0f)
    {
        var (from, velocity) = Shot(to, way, speed, seconds);
        var up = Planet.Up(to);
        var nose = lieAcross == 0f ? way : Vector3.Transform(way, Quaternion.CreateFromAxisAngle(up, float.DegreesToRadians(lieAcross)));
        var rotation = Pointing(nose);
        var position = from - Vector3.Transform(Vector3.UnitY, rotation) * (BoosterHeight / 2f);
        return Drops.Placed(to, Planet.East(to), 0f, legsOut: false, fuel: fuel, id: id) with
        {
            Position = position, Rotation = rotation, Aim = Vector3.Transform(Vector3.UnitY, rotation), Velocity = velocity,
        };
    }

    /// <summary>
    /// A booster (numbered <paramref name="id"/>, legs stowed, engine off) that
    /// <paramref name="flown"/> (one of these builders' boosters, coasting) overtakes: its middle
    /// <paramref name="offset"/> from <paramref name="flown"/>'s after <paramref name="seconds"/>,
    /// going its way there, having set off <paramref name="slower"/> m/s slower. So
    /// <paramref name="flown"/> closes on it at about that, and passes it at its closest then.
    /// </summary>
    internal static VesselState Overtaken(VesselState flown, float seconds, float slower, Vector3 offset, int id)
    {
        var (at, arriving) = Flown(Middle(flown.Position.ToSingle(), flown.Rotation), flown.Velocity.ToSingle(), seconds);
        return Flying(at + offset, Vector3.Normalize(arriving), flown.Velocity.ToSingle().Length() - slower, seconds, id);
    }

    /// <summary>A rotation that points a stage's axis (its own +Y) along <paramref name="nose"/>.</summary>
    private static Quaternion Pointing(Vector3 nose)
    {
        var axis = Vector3.Cross(Vector3.UnitY, nose);
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitY, nose), -1f, 1f));
        return axis.LengthSquared() < 1e-12f ? (angle < 1f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI))
            : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
    }

    /// <summary>A booster's middle (its size-sphere's centre), half its height up its axis from the bottom of its body.</summary>
    internal static Vector3 Middle(Vector3 position, Quaternion rotation) => position + Vector3.Transform(Vector3.UnitY, rotation) * (BoosterHeight / 2f);

    /// <summary>The middle of <paramref name="vessel"/>'s size-sphere now.</summary>
    private static Vector3 Middle(Vessel vessel) => Vector3.Transform(new Vector3(0f, BoosterHeight / 2f, 0f), vessel.Model);

    /// <summary>
    /// Where to start something, and at what velocity, so that, set off at <paramref name="speed"/>,
    /// it arrives at <paramref name="to"/> along <paramref name="way"/> after
    /// <paramref name="seconds"/>: flown as the physics falls (<see cref="Planet.FallStep"/>, with
    /// gravity, the turning frame and the air, capped at <see cref="Scene.TopSpeed"/> as Jolt caps
    /// it), and set off again from where and how it missed, until it arrives.
    /// </summary>
    private static (Vector3 From, Vector3 Velocity) Shot(Vector3 to, Vector3 way, float speed, float seconds)
    {
        var velocity = way * speed;
        var from = to - velocity * seconds;
        for (var i = 0; i < 30; i++)
        {
            var (at, arriving) = Flown(from, velocity, seconds);
            var miss = at - to;
            var off = way - Vector3.Normalize(arriving);
            from -= miss;
            velocity = Vector3.Normalize(Vector3.Normalize(velocity) + off) * speed;
            if (miss.Length() < 0.01f && off.Length() < 1e-5f)
                break;
        }
        return (from, velocity);
    }

    /// <summary>
    /// Where something at <paramref name="p"/> moving at <paramref name="v"/> is after
    /// <paramref name="seconds"/> of the physics' fall, and its velocity then; each step's place to
    /// <paramref name="visit"/>. Seconds part-way through a step end on the way to its next place.
    /// </summary>
    private static (Vector3 At, Vector3 Velocity) Flown(Vector3 p, Vector3 v, float seconds, Action<Vector3>? visit = null)
    {
        var whole = (int)MathF.Floor(seconds / RocketPhysics.StepSeconds + 1e-3f);
        var part = seconds - whole * RocketPhysics.StepSeconds;
        for (var i = 0; i <= whole; i++)
        {
            var dt = i < whole ? RocketPhysics.StepSeconds : part;
            if (dt <= 1e-6f)
                break;
            var drag = Scene.DragRate * Planet.AirDensity(Planet.Altitude(p));
            v += Planet.FallStep(v, Planet.Gravity(p) + Planet.OutwardPull(p), drag, dt);
            if (v.Length() > Scene.TopSpeed)
                v = Vector3.Normalize(v) * Scene.TopSpeed;
            p += v * dt;
            visit?.Invoke(p);
        }
        return (p, v);
    }

    /// <summary>How far over the ground (or the pad) the middle of a booster flown to <paramref name="to"/> along <paramref name="way"/> keeps on its way there, at its lowest.</summary>
    internal static float Clearance(Vector3 to, Vector3 way, float speed, float seconds)
    {
        var (from, velocity) = Shot(to, way, speed, seconds);
        var lowest = float.MaxValue;
        Flown(from, velocity, seconds, p => lowest = MathF.Min(lowest, Planet.GroundAltitude(p)));
        return lowest;
    }

    /// <summary>A booster standing on the landing zone, landed and frozen there, as saved (as <c>HitTests</c>' standing one).</summary>
    private static VesselState StandingOnTheZone()
    {
        var zone = Scene.LandingZoneCentre;
        using var sim = Drops.At(zone, Planet.East(zone), 1f);
        var booster = sim.Flight.Controlled;
        Assert.True(sim.Run(Nobody, _ => booster.Craft.Frozen, maxSeconds: 60), $"it never froze: {booster.State}");
        return sim.Flight.Capture().Vessels.Single(v => v.Id == 0);
    }

    // ---- The probe ----

    [Theory] // closing speed, metres up (5,000: vacuum, over Scene.AtmosphereHeight; 1,000 and 2,500: in the air)
    [InlineData(500f, 5000f)]
    [InlineData(2000f, 5000f)]
    [InlineData(8000f, 5000f)]
    [InlineData(500f, 1000f)]
    [InlineData(2000f, 1000f)]
    [InlineData(8000f, 2500f)] // in thin air, higher: at the top speed the ground's tiles are laid under it 4 km up, and costly so fast
    public void Two_crafts_head_on_collide(float closing, float altitude) => PairCollides(Pair(closing, altitude, HitsAt));

    [Theory]
    [InlineData(500f, 5000f)]
    [InlineData(2000f, 5000f)]
    [InlineData(8000f, 5000f)] // flown closing at 5,657 m/s, not 8,000: each would go at 5,657, and is capped at Scene.TopSpeed (4,000). Unslowed, it passed through (24 m apart at the nearest step)
    [InlineData(2000f, 1000f)]
    public void Two_crafts_crossing_collide(float closing, float altitude) => PairCollides(Pair(closing, altitude, HitsAt, crossing: true));

    private void PairCollides(WorldState world)
    {
        var (sim, a, b) = Fly(world);
        using var _ = sim;
        var closest = (Distance: float.MaxValue, Time: 0.0);
        var slowing = new Slowing();
        sim.Run(slowing.Watch, _ =>
        {
            var apart = Vector3.Distance(Middle(a), Middle(b!));
            if (apart < closest.Distance && a.State != FlightState.Crashed && b!.State != FlightState.Crashed)
                closest = (apart, sim.Time);
            return a.State == FlightState.Crashed && b!.State == FlightState.Crashed;
        }, maxSeconds: HitsAt + Wait - sim.Time);
        output.WriteLine($"at {sim.Time:0.000} s: closest (both whole) {closest.Distance:0.0} m at {closest.Time:0.000} s (sizes {BoosterSize:0.0} m each);"
            + $" flown {a.State} {a.CrashReason}; other {b!.State} {b.CrashReason}; {slowing}");
        Assert.Equal(FlightState.Crashed, a.State);
        Assert.StartsWith("Hit another vessel", a.CrashReason);
        Assert.Equal(FlightState.Crashed, b.State);
        Assert.StartsWith("Hit another vessel", b.CrashReason);
    }

    // ---- Watched: the forced slow-down, for the user to judge by eye ----

    /// <summary>
    /// A fast collision for the user to watch: two boosters head on at 1,000 m/s that hit, the flown
    /// one's camera tracking the other in (with <c>GAMEONE_WATCH=1</c>), slowed by the forced
    /// slow-down as it is (<see cref="Slowdown"/>), from its first slowed frame to the end of its
    /// ease back. Its report says where the slowing began, how slow it got (and how fast that
    /// looks), and how long each part took in real time.
    /// </summary>
    [Fact]
    public void A_fast_collision_to_watch() => WatchedCollision(1000f, 5f);

    /// <summary>The same at the most two crafts can close at (each at <see cref="Scene.TopSpeed"/>), starting 2 s apart so neither starts halfway round the planet.</summary>
    [Fact]
    public void A_top_speed_fast_collision_to_watch() => WatchedCollision(2f * Scene.TopSpeed, 2f);

    private void WatchedCollision(float closing, float seconds)
    {
        var (sim, a, b) = Fly(Pair(closing, 5000f, seconds));
        using var _ = sim;
        var (slowedIn, slowedAfter, least, startedAt, hit) = (0, 0, 1f, float.NaN, false);
        sim.Run((_, _) => default, f =>
        {
            var apart = Vector3.Distance(Middle(a), Middle(b!));
            hit |= a.State == FlightState.Crashed;
            if (f.Slowed is { } s)
            {
                if (float.IsNaN(startedAt))
                    startedAt = apart;
                least = MathF.Min(least, s.Scale);
                if (hit)
                    slowedAfter++;
                else
                    slowedIn++;
            }
            return hit && f.Slowed is null;
        }, maxSeconds: seconds + 3f);
        var real = (double)Simulation.FrameSeconds;
        output.WriteLine($"{closing} m/s head on: slowing began {startedAt:0} m apart; in over {slowedIn * real:0.00} s real, to {least:0.0000} "
            + $"(it hit at about {closing * least:0} m/s to the eye); after the hit {slowedAfter * real:0.00} s real; flown {a.CrashReason}");
        Assert.True(hit, "never touched");
        Assert.StartsWith("Hit another vessel", a.CrashReason);
        Assert.StartsWith("Hit another vessel", b!.CrashReason);
    }

    [Theory]
    [InlineData(500f)]
    [InlineData(2000f)]
    [InlineData(4000f)]
    public void A_fast_craft_hits_the_ground(float speed)
    {
        var (flown, _) = HitsInto("ground", speed);
        Assert.StartsWith("Hit the ground at", flown.CrashReason);
    }

    [Theory]
    [InlineData(500f)]
    [InlineData(2000f)]
    [InlineData(4000f)]
    public void A_fast_craft_hits_a_frozen_vessel(float speed)
    {
        var (world, target) = Aimed("frozen", speed, HitsAt);
        var (sim, flown, standing) = Fly(world);
        using var _ = sim;
        Assert.True(standing!.Craft.Frozen, "the standing one isn't frozen: this shows nothing");
        var slowing = new Slowing();
        sim.Run(slowing.Watch, _ => flown.State == FlightState.Crashed && standing.State == FlightState.Crashed, maxSeconds: HitsAt + Wait - sim.Time);
        output.WriteLine($"at {sim.Time:0.000} s: flown {flown.State} {flown.CrashReason} ({Vector3.Distance(Middle(flown), target):0.0} m from the standing one's middle);"
            + $" standing {standing.State} {standing.CrashReason}; {slowing}");
        Assert.Equal(FlightState.Crashed, flown.State);
        Assert.Equal(FlightState.Crashed, standing.State);
        Assert.StartsWith("Hit another vessel", standing.CrashReason);
    }

    [Theory]
    // Unslowed, 500 and 2,000 were "Hit the water": the nose through the 1.6 m slab in a step.
    [InlineData(500f)]
    [InlineData(2000f)]
    [InlineData(4000f)]
    public void A_fast_craft_hits_a_floe(float speed)
    {
        var (flown, _) = HitsInto("floe", speed);
        Assert.StartsWith("Hit the ground at", flown.CrashReason);
    }

    [Theory]
    [InlineData(500f)]
    [InlineData(2000f)]
    [InlineData(4000f)]
    public void A_fast_craft_hits_a_light_mast(float speed)
    {
        var (flown, last) = HitsInto("mast", speed);
        Assert.Equal("Hit a light mast.", flown.CrashReason);
        // The mast it hit is the one it flew at: of all the masts, that one is nearest where it last was whole.
        var aimed = PadLights.All.First(p => p.IsPad);
        var nearest = PadLights.All.SelectMany(p => p.Masts.Select((m, i) => (Place: p, Index: i, Middle: p.ToWorld(m.Foot + new Vector3(0f, m.Height / 2f, 0f)))))
            .MinBy(m => Vector3.Distance(m.Middle, last));
        output.WriteLine($"nearest mast: {nearest.Place.Name}'s mast {nearest.Index}, {Vector3.Distance(nearest.Middle, last):0.0} m away");
        Assert.True(nearest.Place == aimed && nearest.Index == 0, $"the nearest mast is {nearest.Place.Name}'s mast {nearest.Index}, not the one aimed at");
    }

    /// <summary>
    /// Flies a booster into <paramref name="what"/> (<see cref="Into"/>) until it is judged, and
    /// asserts it crashed where it hit it: its middle within its size and a frame's travel of the
    /// point it aimed at, not further on (through it, and judged on what is beyond).
    /// </summary>
    private (Vessel Flown, Vector3 LastWhole) HitsInto(string what, float speed)
    {
        var (world, target) = Aimed(what, speed, HitsAt);
        var (sim, flown, _) = Fly(world);
        using var _ = sim;
        var lastWhole = Vector3.Zero;
        var slowing = new Slowing();
        sim.Run(slowing.Watch, _ =>
        {
            if (flown.State == FlightState.Crashed)
                return true;
            lastWhole = Middle(flown);
            return false;
        }, maxSeconds: HitsAt + Wait - sim.Time);
        var from = Vector3.Distance(lastWhole, target);
        output.WriteLine($"into the {what} at {speed} m/s: at {sim.Time:0.000} s {flown.State} {flown.CrashReason}; its middle {from:0.0} m from the aim the frame before"
            + $" ({Planet.GroundAltitude(lastWhole):0.0} m over the ground there); {slowing}");
        Assert.Equal(FlightState.Crashed, flown.State);
        Assert.True(from < BoosterSize + speed * Simulation.FrameSeconds, $"it crashed {from:0.0} m from what it flew into: through it");
        return (flown, lastWhole);
    }

    /// <summary>A pilot that flies nothing, counting how long (in real frames) the game was slowed, and how slow it went, for the output.</summary>
    private sealed class Slowing
    {
        private int _frames;
        private float _least = 1f;

        public FlightInput Watch(Flight flight, float dt)
        {
            if (flight.TimeScale < 1f)
                _frames++;
            _least = MathF.Min(_least, flight.TimeScale);
            return default;
        }

        public override string ToString() => $"slowed {_frames * Simulation.FrameSeconds:0.00} s of real time, to {_least:0.0000}";
    }
}
