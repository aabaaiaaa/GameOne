using System.Diagnostics;
using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A physics at each body in use (<see cref="Worlds"/>): every vessel at its own body
/// (<see cref="Vessel.Body"/>), in that body's physics or under formula there, all of them played
/// out at once; a body with nothing in its physics has none, and one left empty is released and
/// its world reused; the moon's dark lights a lander there; a world saves each vessel's body; and
/// nothing is allocated a frame with vessels at several bodies.
/// </summary>
public sealed class BodiesInPlayTests(ITestOutputHelper output)
{
    private static GroundBody Moon => GroundBody.Moon;
    private static GroundBody Home => GroundBody.Planet;
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>
    /// A booster on its landing burn over Home's landing zone, its legs out: coming down at 3 m/s from
    /// 8 m up, its engine at a quarter throttle and a little more (a shade over its weight), held
    /// retrograde, and fuel for under four seconds of it, so it is flown down by its hold and its
    /// burn and lands gently as the fuel runs out (unpowered from there, it would hit at some 13 m/s).
    /// </summary>
    private static VesselState OnItsLandingBurn(int id)
    {
        var zone = LaunchSites.Home.LandingZone!.Value;
        return Drops.Placed(zone, Planet.East(zone), 3f, clearance: 8f, fuel: 0.02f, id: id) with { Throttle = 0.26f, Hold = HoldMode.Retrograde };
    }

    /// <summary>A booster landed on Home's landing zone, still, standing on its legs (its feet half their stroke in).</summary>
    private static VesselState LandedOnTheZone(int id)
    {
        var zone = LaunchSites.Home.LandingZone!.Value;
        return Drops.Placed(zone, Planet.East(zone), 0f, clearance: -1.5f * Scene.LegStroke, id: id) with { State = FlightState.Landed, Velocity = Vector3.Zero, TouchedDown = true };
    }

    private static WorldState World(params VesselState[] vessels) => World(0.0, vessels);

    private static WorldState World(double time, params VesselState[] vessels) =>
        new() { Time = time, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    private static Vessel Get(Simulation sim, int id) => sim.Flight.WorldVessels.Single(v => v.Id == id);

    /// <summary>Its orbit round its body now, from its centre of mass: its apoapsis and periapsis.</summary>
    private static (float Apoapsis, float Periapsis) OrbitOf(Vessel v) =>
        v.Body.Orbit(v.CentreOfMass, v.Body.AmongStars(v.CentreOfMass, v.Velocity));

    [Fact]
    public void A_small_view_at_the_planet_lands_its_booster_while_flying_at_the_moon()
    {
        // The booster alone first, nothing at another body: its burn and touchdown to compare with.
        float alone;
        int burnedAlone;
        using (var sim = new Simulation(Mission.FreePlay, world: World(OnItsLandingBurn(1))))
        {
            var booster = Get(sim, 1);
            IReadOnlyList<Vessel> watched = [booster];
            var lit = 0;
            Assert.True(sim.Run((_, _) => new FlightInput(Watched: watched), _ =>
            {
                lit += booster.EngineLit ? 1 : 0;
                return booster.State != FlightState.Flying;
            }, maxSeconds: 10));
            Assert.Equal(FlightState.Landed, booster.State);
            (alone, burnedAlone) = (booster.TouchdownSpeed, lit);
        }

        using (var sim = new Simulation(Mission.FreePlay, world: World(InOrbit.Round(Moon, 0, 400f), OnItsLandingBurn(1))))
        {
            var (lander, booster) = (Get(sim, 0), Get(sim, 1));
            IReadOnlyList<Vessel> watched = [booster];
            sim.Step(new FlightInput(SwitchTo: lander, Watched: watched));
            sim.Step(new FlightInput(Watched: watched)); // the moon's physics made, and the lander flown, the frame after the link-up begins
            Assert.Same(lander, sim.Flight.Controlled);
            var (apoapsis, periapsis) = OrbitOf(lander);
            var (worstA, worstP, lit) = (0f, 0f, 0);
            sim.Run((_, _) => new FlightInput(Watched: watched), _ =>
            {
                lit += booster.EngineLit && booster.State == FlightState.Flying ? 1 : 0;
                var (a, p) = OrbitOf(lander);
                (worstA, worstP) = (MathF.Max(worstA, MathF.Abs(a - apoapsis)), MathF.Max(worstP, MathF.Abs(p - periapsis)));
                // Each in its own body's physics, both played out in full.
                Assert.Same(sim.Flight.PhysicsAt(Moon), lander.Physics);
                Assert.True(booster.State != FlightState.Flying || booster.Physics == sim.Flight.PhysicsAt(Home), "the booster left the physics while watched");
                return false;
            }, maxSeconds: 10);
            output.WriteLine($"the booster {booster.State} at {booster.TouchdownSpeed:0.000} m/s, burning {lit} frames in flight (alone {alone:0.000}, {burnedAlone} frames); the lander's orbit {apoapsis:0.0} by {periapsis:0.0} m, off by {worstA:0.000} and {worstP:0.000} m");
            Assert.Equal(FlightState.Landed, booster.State);
            Assert.InRange(booster.TouchdownSpeed, alone - 0.05f, alone + 0.05f);
            Assert.True(lit > 60, $"it burned only {lit} frames: its burn is what this is about"); // its thrust given in the planet's physics while the moon's is the flown one's
            Assert.Equal(FlightState.Flying, lander.State);
            Assert.True(worstA < 1f && worstP < 1f, $"the lander's orbit drifted {worstA:0.00} m and {worstP:0.00} m");
            Assert.Equal(2, sim.Worlds.InUse);
        }
    }

    [Fact]
    public void A_world_loaded_with_legs_swinging_under_formula_swings_them_on()
    {
        // Saved with its legs on their way out, far from anything: it loads under formula, with
        // no body nor a physics for it, and its legs carry on out there.
        using var sim = new Simulation(Mission.FreePlay, world: World(InOrbit.Round(Home, 0, 2_000f) with { LegsWanted = true, LegDeploy = 0.3f }));
        var v = Get(sim, 0);
        Assert.True(v.OnFormula && v.Physics is null, "it loaded in the physics");
        Assert.True(sim.Run(Nobody, _ => v.LegDeploy >= 1f, maxSeconds: Scene.LegDeploySeconds + 1f), $"its legs stopped at {v.LegDeploy:0.00}");
        Assert.True(v.OnFormula);
        Assert.Equal(FlightState.Flying, v.State);
    }

    [Fact]
    public void A_vessel_handed_out_with_its_legs_swinging_swings_them_on_under_formula()
    {
        // Watched as its legs start out (in the moon's physics), then no more: handed out while
        // they are still on their way, they carry on out under formula.
        using var sim = new Simulation(Mission.FreePlay, world: World(InOrbit.Round(Moon, 0, 400f) with { LegsWanted = true, LegDeploy = 0f }));
        var v = Get(sim, 0);
        IReadOnlyList<Vessel> watched = [v];
        sim.Step(new FlightInput(Watched: watched));
        Assert.False(v.OnFormula);
        Assert.Same(sim.Flight.PhysicsAt(Moon), v.Physics);
        Assert.True(sim.Run(Nobody, _ => v.OnFormula, maxSeconds: 1), "it was never handed out");
        Assert.InRange(v.LegDeploy, 0.01f, 0.99f); // still on their way
        Assert.True(sim.Run(Nobody, _ => v.LegDeploy >= 1f, maxSeconds: Scene.LegDeploySeconds + 1f), $"its legs stopped at {v.LegDeploy:0.00}");
        Assert.True(v.OnFormula);
    }

    [Fact]
    public void A_small_view_at_the_moon_lands_while_flying_at_the_planet()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(MoonLandingTests.Lander(3f)));
        var lander = Get(sim, 0);
        IReadOnlyList<Vessel> watched = [lander];
        var input = new FlightInput(Watched: watched);
        Assert.True(sim.Run((_, _) => input, _ => lander.State != FlightState.Flying, maxSeconds: 30), "it never came down");
        output.WriteLine($"{lander.State} at {lander.TouchdownSpeed:0.00} m/s, flying the rocket on Home's pad");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.Same(Moon, lander.Body);
        Assert.Same(sim.Flight.PhysicsAt(Moon), lander.Physics);
        Assert.NotSame(lander, sim.Flight.Controlled);
        var settled = sim.Time;
        Assert.True(sim.Run((_, _) => input, _ => lander.Craft.Frozen, maxSeconds: 10), "it never froze");
        output.WriteLine($"frozen {sim.Time - settled:0.0} s after it landed");
    }

    [Fact]
    public void A_lander_on_the_moon_stays_on_its_place_in_a_long_played_world()
    {
        var years = 200.0 * Scene.YearDays * Scene.DaySeconds;
        using var sim = new Simulation(Mission.FreePlay, world: World(years, OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var lander = Get(sim, 0);
        var placed = lander.Position;
        sim.Step(new FlightInput(SwitchTo: lander));
        var worst = 0f;
        sim.Run(Nobody, _ =>
        {
            worst = MathF.Max(worst, Vector3.Distance(lander.Position, placed));
            return false;
        }, maxSeconds: 60);
        output.WriteLine($"world time {sim.Flight.WorldTime:0} s: {worst * 1000:0.00} mm off its place at worst over 60 s, {lander.State}");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.Same(sim.Flight.PhysicsAt(Moon), lander.Physics);
        Assert.True(worst < 0.01f, $"{worst:0.000} m off its place");
    }

    [Fact]
    public void A_vessel_left_orbiting_the_moon_under_formula_keeps_its_orbit()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(InOrbit.Round(Moon, 0, 400f)));
        sim.Step(default);
        var v = Get(sim, 0);
        Assert.True(v.OnFormula, "nobody watches it, but it is in the physics");
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        var (apoapsis, periapsis) = OrbitOf(v);
        var lap = new Orbit(Moon, v.CentreOfMass, Moon.AmongStars(v.CentreOfMass, v.Velocity), findEnd: false).Period;
        var (worstA, worstP) = (0f, 0f);
        // Ten laps, the formula asked every few seconds (it works its orbit out afresh for any time).
        var (from, now) = (sim.Flight.WorldTime, sim.Flight.WorldTime);
        const float Every = 3f;
        while (now < from + 10 * lap)
        {
            now += Every;
            Formula.Advance(v, Every, now);
            var (a, p) = OrbitOf(v);
            (worstA, worstP) = (MathF.Max(worstA, MathF.Abs(a - apoapsis)), MathF.Max(worstP, MathF.Abs(p - periapsis)));
        }
        output.WriteLine($"a lap {lap:0} s; over ten, its orbit {apoapsis:0.0} by {periapsis:0.0} m off by {worstA * 100:0.0} and {worstP * 100:0.0} cm");
        Assert.Equal(FlightState.Flying, v.State);
        Assert.True(worstA < 1f && worstP < 1f, $"its orbit drifted {worstA:0.00} m and {worstP:0.00} m");
    }

    [Fact]
    public void Under_formula_at_the_planet_a_falling_booster_lands_while_flying_at_the_moon()
    {
        // Falling the last metre onto Home's zone: nothing at the planet is watched (the camera is
        // at the moon), so it is handed out at once and lands by the formula's rules there.
        var zone = LaunchSites.Home.LandingZone!.Value;
        var booster = Drops.Placed(zone, Planet.East(zone), 1f, clearance: 1f, id: 1);
        using var sim = new Simulation(Mission.FreePlay, world: World(InOrbit.Round(Moon, 0, 400f), booster));
        var (lander, falling) = (Get(sim, 0), Get(sim, 1));
        sim.Step(new FlightInput(SwitchTo: lander));
        sim.Step(default); // the moon's physics made, and the lander flown, the frame after the link-up begins
        var held = false;
        Assert.True(sim.Run(Nobody, _ =>
        {
            held |= !falling.OnFormula;
            return falling.State != FlightState.Flying;
        }, maxSeconds: 10), "it never came down");
        output.WriteLine($"{falling.State} {falling.CrashReason} at {falling.TouchdownSpeed:0.00} m/s, under formula all the way");
        Assert.False(held, "it was held in the physics");
        Assert.Equal(FlightState.Landed, falling.State);
        Assert.InRange(falling.TouchdownSpeed, 0.5f, Scene.SafeTouchdownSpeed);
    }

    [Fact]
    public void Under_formula_at_the_moon_a_vessel_into_the_ground_is_wrecked()
    {
        var at = MoonLandingTests.LandingSpot();
        var diving = Drops.Placed(at, Moon.East(at), 60f, clearance: 200f, id: 0, body: Moon);
        using var sim = new Simulation(Mission.FreePlay, world: World(diving));
        var v = Get(sim, 0);

        Assert.True(sim.Run(Nobody, _ => v.State != FlightState.Flying, maxSeconds: 10), "it never came down");
        output.WriteLine($"{v.State}: {v.CrashReason}; logged {string.Join("; ", sim.Flight.Log.Select(e => $"{e.What} {e.How} {e.Speed}"))}");
        Assert.Equal(FlightState.Crashed, v.State);
        Assert.StartsWith("Hit the moon at", v.CrashReason);
        Assert.Contains(sim.Flight.Log, e => e.What == WorldLog.Named(v) && e.How == "Hit the moon" && e.Speed >= 60f && !e.Witnessed);
        Assert.Null(sim.Flight.PhysicsAt(Moon)); // never in the physics
    }

    [Fact]
    public void A_body_with_nothing_in_its_physics_has_none()
    {
        using var sim = new Simulation(Mission.FreePlay,
            world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot), OnTheMoon.Lander(1, CubeSphere.FromLatLon(0f, 0f))));
        sim.Run(Nobody, _ => false, maxSeconds: 2);
        Assert.All(sim.Flight.WorldVessels.Where(v => v.Body == Moon), v => Assert.True(v.OnFormula && v.Physics is null, $"{v.Id} is in the physics"));
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        Assert.NotNull(sim.Flight.PhysicsAt(Home)); // the rocket on Home's pad
        Assert.Equal(1, sim.Worlds.InUse);
    }

    [Fact]
    public void A_body_left_empty_has_its_physics_released()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var lander = Get(sim, 0);
        IReadOnlyList<Vessel> watched = [lander];
        sim.Run((_, _) => new FlightInput(Watched: watched), _ => false, maxSeconds: 1);
        var moon = sim.Flight.PhysicsAt(Moon);
        Assert.NotNull(moon);
        Assert.Same(moon, lander.Physics);
        Assert.Equal(2, sim.Worlds.InUse);

        // Watched no more: handed out (landed, it can be), the moon's physics is empty, then released.
        sim.Run(Nobody, _ => lander.OnFormula, maxSeconds: 1);
        Assert.True(lander.OnFormula);
        Assert.Same(moon, sim.Flight.PhysicsAt(Moon)); // kept a while, empty
        var emptied = sim.Time;
        Assert.True(sim.Run(Nobody, _ => sim.Flight.PhysicsAt(Moon) is null, maxSeconds: Scene.PhysicsIdleSeconds + 1), "never released");
        output.WriteLine($"released {sim.Time - emptied:0.00} s after it emptied");
        Assert.InRange(sim.Time - emptied, Scene.PhysicsIdleSeconds - 0.1, Scene.PhysicsIdleSeconds + 0.1);
        Assert.Equal((1, 1), (sim.Worlds.InUse, sim.Worlds.Released));

        // Watched again: back in the moon's physics, the world released before.
        sim.Step(new FlightInput(Watched: watched));
        Assert.Same(moon, sim.Flight.PhysicsAt(Moon));
        Assert.Same(moon, lander.Physics);
        Assert.Equal((2, 0), (sim.Worlds.InUse, sim.Worlds.Released));
        sim.Run((_, _) => new FlightInput(Watched: watched), _ => false, maxSeconds: 1);
        Assert.Equal(FlightState.Landed, lander.State);
    }

    [Fact]
    public void A_body_asked_for_ahead_has_its_physics_made_off_the_step()
    {
        // Asked ahead of need, the moon's physics is made on the tile worker's idle time, and is
        // the moon's from the first ask after; asking again meanwhile makes no second one.
        using var worlds = new Worlds();
        worlds.Need(Home);
        worlds.Ahead(Moon);
        worlds.Ahead(Moon);
        Assert.True(Waits.Until(() => worlds.At(Moon) is not null, TimeSpan.FromSeconds(30)), "it was never made");
        var moon = worlds.At(Moon)!;
        Assert.Same(Moon, moon.Body);
        Assert.Same(moon, worlds.Need(Moon));
        Assert.Equal(2, worlds.InUse);
        Assert.Equal([Home, Moon], worlds.All.Select(w => w.Body));
        Assert.Equal(0, worlds.Released);
    }

    [Fact]
    public void A_world_that_never_held_the_rafts_leaves_them_as_it_goes()
    {
        // A booster dropped onto a floe: the planet's physics in use keeps that floe up to date
        // (the process's rafts, Rafts.Active). Another made off the main thread, as one made ahead
        // is, never takes them; disposed (as the pool's are, on the tile worker too), it leaves
        // them the world in use's, and so does one released to the pool and let go.
        var floe = FloeTests.BigFloe();
        using var sim = Drops.At(FloeTests.Top(floe), Planet.East(floe.Centre), 1.5f);
        sim.Run(Nobody, _ => false, maxSeconds: 0.5);
        Assert.Contains(floe, Rafts.Active);
        new RocketPhysics(Home, takeRafts: false).Dispose();
        Assert.Contains(floe, Rafts.Active);
        var other = new RocketPhysics(Moon);
        other.Idle();
        other.Dispose();
        Assert.Contains(floe, Rafts.Active);
        sim.Run(Nobody, _ => false, maxSeconds: 0.1);
        Assert.Contains(floe, Rafts.Active);
    }

    /// <summary>
    /// The most a body's physics (<see cref="RocketPhysics"/>, its Jolt world and its tiles'
    /// placeholders) may take of the process's memory: half as much again as one was measured to
    /// take (6.5 MB at most of the four alike, 6.3 on average), so a world that came to cost a good deal more is seen.
    /// </summary>
    private const long WorldBytesBound = 9_750_000;

    [Fact]
    [Trait("Run", "Alone")] // what a world holds is native (Jolt's), read as the process's private memory: beside the other shards' tests it moves with theirs
    public void With_six_views_at_six_bodies_six_worlds_run()
    {
        // A list of six bodies (not the game's two): the planet, the moon, and four more going
        // round the planet, each with the moon's ground; a craft at each, high over its ground,
        // as a view of a vessel there would have it in the physics. This is the pool's work (six
        // made, ordered, each stepped, what each costs) and no more: nothing at more than two
        // bodies goes through Flight, Unwatched or Views (a vessel names its body among the game's,
        // Bodies.All, which a test can't add to without a switch in the game), and the crafts, 2 km
        // up, want no tiles, so six worlds' wants on the one tile worker are not tried here.
        var bodies = new List<GroundBody> { Home, Moon };
        for (var i = 0; i < 4; i++)
        {
            var distance = 60_000f + 20_000f * i;
            bodies.Add(GroundBody.Circling($"Body {i + 2}", "the ground", 2 + i, Home, distance, Scene.MoonRadius, SolarSystem.MoonMu,
                t => new BodyPose(Home.Centre + new Vector3Double(distance, 0, 0), QuaternionDouble.Identity, default, default),
                () => MoonGround.Map, MoonGround.Height));
        }
        Assert.Equal(1 + Scene.MaxSmallViews, bodies.Count);
        _ = (Home.Map, Moon.Map, Home.MaxHeight, Moon.MaxHeight); // the maps loaded first: not a world's
        using var worlds = new Worlds();
        var sizes = new List<long>();
        foreach (var body in bodies)
        {
            var before = Memory();
            var world = worlds.Need(body);
            sizes.Add(Memory() - before);
            var up = Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f));
            var at = body.Centre + up * (body.SeaRadius + body.MaxHeight + 2_000f);
            world.AddLoose(Vehicle.Booster, at, body.UprightAt(at), Vector3.Zero, Vector3.Zero);
        }
        Assert.Equal(6, worlds.InUse);
        Assert.Equal(bodies, worlds.All.Select(w => w.Body));
        var steps = worlds.All.Select(w => w.Steps).ToList();
        for (var frame = 0; frame < 60; frame++)
            worlds.Step(Simulation.FrameSeconds, [], Home, null, frame * (double)Simulation.FrameSeconds);
        for (var i = 0; i < 6; i++)
            Assert.InRange(worlds.All[i].Steps - steps[i], 119, 121);
        // Each world's own: the first two pay for what is made once for the process (Jolt's
        // job threads, the planet's sites and ships), so the four alike are what one costs.
        var each = sizes.Skip(2).ToList();
        output.WriteLine($"memory per world: {string.Join(", ", sizes.Select(s => $"{s / 1e6:0.0} MB"))}; the four alike {each.Average() / 1e6:0.0} MB on average, the bound {WorldBytesBound / 1e6:0.0} MB");
        Assert.All(each, size => Assert.True(size < WorldBytesBound, $"a world took {size / 1e6:0.0} MB"));

        static long Memory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            return process.PrivateMemorySize64;
        }
    }

    /// <summary>
    /// The first world time from <paramref name="from"/> at which the place on the moon's sphere in
    /// moon-fixed <paramref name="direction"/> reads <paramref name="local"/> on its clock
    /// (<see cref="Sun.LocalSeconds"/>): stepped on by what is left to go, over the moon's day.
    /// </summary>
    private static double MoonTimeFor(Vector3 direction, double local, double from)
    {
        var t = from;
        var place = Moon.Centre + direction * Moon.Radius;
        for (var i = 0; i < 6; i++)
        {
            var left = Sun.Wrap(local - Sun.LocalSeconds(Moon, place, t), Scene.DaySeconds);
            if (i > 0 && left > Scene.DaySeconds / 2)
                left -= Scene.DaySeconds; // a hair past it, after the first step
            t += left / Scene.DaySeconds * SolarSystem.MoonDaySeconds;
        }
        return t;
    }

    [Theory]
    [InlineData("the near side's middle", 0f, 0f)]
    [InlineData("the landing spot", 70f, 0f)]
    public void A_lander_on_the_moon_lights_by_the_moons_dark(string where, float latitude, float longitude)
    {
        // Lit by the moon's day, not the planet's: off at its noon, on at its midnight. At the landing spot,
        // 20 ° from the pole, the sun is 10 ° or more up at noon and 10 ° or more down at midnight, whatever the season.
        Assert.Equal((70f, 0f), Scene.MoonLandingSpot);
        var direction = CubeSphere.FromLatLon(float.DegreesToRadians(latitude), float.DegreesToRadians(longitude));
        var noon = MoonTimeFor(direction, Scene.DaySeconds / 2.0, 0.0);
        foreach (var (time, lit) in new[] { (noon, false), (MoonTimeFor(direction, 0.0, noon), true) })
        {
            using var sim = new Simulation(Mission.FreePlay, world: World(time, OnTheMoon.Lander(0, direction)));
            var lander = Get(sim, 0);
            sim.Step(new FlightInput(SwitchTo: lander));
            sim.Run(Nobody, _ => false, maxSeconds: Scene.PadLightFadeSeconds + 1f);
            var t = sim.Flight.WorldTime;
            var height = Sun.Height(Moon, lander.Position, t);
            output.WriteLine($"{where} at its {(lit ? "midnight" : "noon")} ({Sun.LocalSeconds(Moon, lander.Position, t):0} s on the moon's clock, " +
                $"world time {t:0}): the sun {float.RadiansToDegrees(MathF.Asin(height)):0.0} ° up there; its lights {lander.LandingLights:0.00}");
            Assert.Equal(lit ? 1f : 0f, lander.LandingLights);
        }
    }

    [Fact]
    public void A_world_with_vessels_at_several_bodies_saves_and_loads_where_each_was()
    {
        var world = World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot), InOrbit.Round(Moon, 1, 400f), InOrbit.Round(Home, 2, 2_000f),
            LandedOnTheZone(3));
        using var sim = new Simulation(Mission.FreePlay, world: world);
        sim.Run(Nobody, _ => false, maxSeconds: 2);
        var before = sim.Flight.WorldVessels.Where(v => v.Id <= 3).ToDictionary(v => v.Id, v => (v.Body, v.Position, v.OnFormula));
        Assert.True(before[3].OnFormula == false && before[0].OnFormula && before[1].OnFormula && before[2].OnFormula,
            "the booster by Home's pad is in the physics, the rest under formula");
        var saved = sim.Flight.Capture();
        Assert.Equal([Moon.Name, Moon.Name, Home.Name, Home.Name], saved.Vessels.Where(v => v.Id <= 3).OrderBy(v => v.Id).Select(v => v.Body));
        using var back = sim.Reloaded();
        foreach (var (id, (body, position, onFormula)) in before)
        {
            var v = Get(back, id);
            var off = Vector3.Distance(v.Position, position);
            output.WriteLine($"{WorldLog.Named(v)} at {v.Body.Name} {off * 1000:0.000} mm from where it was, {(v.OnFormula ? "under formula" : "in the physics")}");
            Assert.Same(body, v.Body);
            Assert.True(off < 1e-3f, $"{id} came back {off:0.0000} m off");
            Assert.Equal(onFormula, v.OnFormula);
        }
    }

    [Fact]
    public void A_world_of_the_old_format_is_refused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorldStore(directory);
            store.Save(World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)) with { Id = "old", Name = "Old" });
            store.Save(World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)) with { Id = "elsewhere", Name = "Elsewhere" });
            Rewrite(Path.Combine(directory, "old.json"), file => file["Version"] = 1); // a version with no body saved for each vessel
            Rewrite(Path.Combine(directory, "elsewhere.json"), file => file["World"]!["Vessels"]![0]!["Body"] = "Nowhere");
            var old = Assert.Throws<InvalidDataException>(() => store.Load("old"));
            var elsewhere = Assert.Throws<InvalidDataException>(() => store.Load("elsewhere"));
            output.WriteLine($"{old.Message} / {elsewhere.Message}");
            Assert.Contains("older version", old.Message);
            Assert.Contains("Nowhere", elsewhere.Message);
            Assert.All(store.List(), w => Assert.NotNull(w.Error)); // listed as unreadable, to delete
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        static void Rewrite(string path, Action<System.Text.Json.Nodes.JsonNode> change)
        {
            var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            change(file);
            File.WriteAllText(path, file.ToJsonString());
        }
    }

    [Fact]
    public void The_readout_says_how_high_the_planet_is_from_the_moon()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(1234.5, OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var lander = Get(sim, 0);
        sim.Step(new FlightInput(SwitchTo: lander));
        sim.Step(default); // the moon's physics made, and the lander flown, the frame after the link-up begins
        var t = sim.Flight.WorldTime;
        var expected = float.RadiansToDegrees(MathF.Asin(SolarSystem.PlanetHeightFromMoon(OnTheMoon.LandingSpot, t)));
        var reading = Readout.FlightReadings(sim.Flight)["Planet height"];
        output.WriteLine($"from the landing spot at {t:0.0} s: {reading} (its sphere's place: {expected:0.0} °)");
        Assert.EndsWith(" °", reading);
        Assert.InRange(float.Parse(reading[..^2], System.Globalization.CultureInfo.InvariantCulture), expected - 1f, expected + 1f);

        // Flying the rocket on Home's pad instead, the planet is straight down.
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.Single(v => v.State == FlightState.Ready)));
        Assert.Same(Home, sim.Flight.Body);
        Assert.Equal("-90 °", Readout.FlightReadings(sim.Flight)["Planet height"]);
    }

    [Fact]
    public void Nothing_is_allocated_a_frame_with_vessels_at_several_bodies()
    {
        // Flying a lander landed on the moon (the moon's physics), a booster landed by Home's pad
        // watched (the planet's), one orbiting each body under formula. What a frame does for them,
        // part by part: their rules, the formula's step at each body, who is in which physics, the
        // look ahead, their landing lights and the sun at each body, which body each is at, each
        // body's physics found, the drawing's parts and the readout. (Each body's physics' step,
        // Worlds.Step, isn't asked, as Jolt's step under it allocates a little of its own, with or
        // without these: see TODO.md. Worlds.Step itself only walks its worlds, allocating nothing
        // but as one is released to the pool, which no steady frame does.)
        using var sim = new Simulation(Mission.FreePlay,
            world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot), LandedOnTheZone(1), InOrbit.Round(Moon, 2, 400f), InOrbit.Round(Home, 3, 2_000f)));
        var (lander, booster) = (Get(sim, 0), Get(sim, 1));
        IReadOnlyList<Vessel> watched = [booster];
        sim.Step(new FlightInput(SwitchTo: lander, Watched: watched));
        sim.Run((_, _) => new FlightInput(Watched: watched), _ => false, maxSeconds: 2);
        Assert.Equal(2, sim.Worlds.InUse);
        var flight = sim.Flight;
        var world = flight.WorldVessels;
        var camera = new ChaseCamera(null);
        var parts = new List<StagePart>(64);
        const float Dt = Simulation.FrameSeconds;
        var sum = 0.0;
        void Frame(int i)
        {
            var t = flight.WorldTime + i * (double)Dt;
            lander.TakeInput(Dt, default);
            for (var k = 0; k < world.Count; k++)
            {
                var v = world[k];
                if (v.State == FlightState.Flying && v.Guided)
                    v.Run(Dt, Vector3.Zero, null, t, v.Velocity, Scene.HoldFloorSpeed);
                v.MoveLegs(Dt);
            }
            flight.Unwatched.Advance();
            for (var k = 0; k < world.Count; k++)
                world[k].AfterPhysics(Dt, t);
            flight.LookAhead(watched);
            flight.Unwatched.Update(Dt, watched, lander.CentreOfMass);
            for (var k = 0; k < world.Count; k++)
            {
                var v = world[k];
                v.StepLandingLights(t, Dt);
                sum += Bodies.Choose(v.Body, v.CentreOfMass, t).Index + (flight.PhysicsAt(v.Body) is null ? 0 : 1)
                    + Sun.Height(v.Body, v.Position, t) + Sun.LocalSeconds(v.Body, v.Position, t);
            }
            flight.Worlds.GetParts(parts);
            for (var k = 0; k < world.Count; k++)
                if (world[k].OnFormula)
                    world[k].FormulaParts(parts);
            camera.Update(Dt, Views.Focus(lander), null, lander.Body, 0);
            sum += Readout.HeightOf(flight, GroundBody.Planet) + parts.Count;
        }
        Frame(0); // warmed up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= 120; i++)
            Frame(i);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"120 frames' worth for vessels at two bodies: {bytes} bytes");
        Assert.Equal(0, bytes);
        Assert.True(double.IsFinite(sum));
        Assert.Same(lander, flight.Controlled);
        Assert.Same(Moon, lander.Body);
    }
}
