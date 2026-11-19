using System.Diagnostics;
using System.Numerics;
using Brutal.GlfwApi;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// How long a frame takes to draw with several views open: the main view plus 0 to 5 small ones,
/// in the game's own 1280x720 window, through the game's own <see cref="GameView"/> (renderer, HUD,
/// effects, sound), with vsync off so frames are drawn as fast as they can be. The main view and 3
/// small ones must hold 60 fps: the median frame within 16.7 ms, so a
/// busy machine's spikes don't fail it (the 95th and 99th percentiles and the worst frame are
/// printed beside it).
/// </summary>
/// <remarks>
/// <para>It needs a GPU and a desktop, so it only runs when asked: <c>GAMEONE_PERF=1</c>.</para>
/// <para>Six views need six vessels: a test rocket of six parts (a booster, four upper stages and a
/// satellite) is launched and every stage dropped in turn, each part with guidance, as in the
/// satellite mission. Then the flight is held still (updated with no time passing, so nothing moves
/// but the engines still burn) while each number of views is drawn: a warm-up, then a few hundred
/// frames timed. With 2 frames in flight, a frame's time is set by whichever is slower, the CPU
/// recording it or the GPU drawing it.</para>
/// </remarks>
public sealed class PerformanceTests(ITestOutputHelper output)
{
    private const int WarmUpFrames = 120, TimedFrames = 600;
    private const double FrameBudgetMs = 1000.0 / 60;

    /// <summary>Six parts, all with guidance, so each one dropped is a vessel with a view of its own.</summary>
    private static readonly Mission SixParts = new()
    {
        Name = "TEST: SIX PARTS",
        Briefing = "",
        Vehicle = new Vehicle("Six-part test rocket",
        [
            // Strong enough to lift all of it, and the rest is the satellite launcher's.
            Vehicle.ThreeStage.Stages[0] with { Thrust = 4f * 50000f * Scene.SurfaceGravity },
            Vehicle.ThreeStage.Stages[1],
            Vehicle.ThreeStage.Stages[1],
            Vehicle.ThreeStage.Stages[1],
            Vehicle.ThreeStage.Stages[1],
            Vehicle.ThreeStage.Stages[2] with { Fairing = false }, // dropped like the rest, not jettisoning a fairing first
        ]),
        TargetAltitude = Scene.TargetAltitude,
        Steps = [],
    };

    private static readonly Pilot Climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;

    [PerfFact]
    public void The_main_view_and_three_small_views_hold_60_fps_at_1280x720() =>
        SixViews(null, "");

    [PerfFact]
    public void The_main_view_and_three_small_views_keep_the_frame_time_at_dusk() =>
        // The same, in a world started at Home's sunset (15:00): the sun's light through the most
        // air, the sky lit from under the horizon, and the climb and the stages take it on into
        // the dusk.
        SixViews(Sun.WorldTimeFor(Vector3.Zero, 900), " at dusk");

    /// <summary>
    /// The six-part rocket's main view and 0 to 5 small views, timed, in a new world started at
    /// <paramref name="worldTime"/> (a new world's own time if null): the main view and 3 small
    /// ones must hold 60 fps.
    /// </summary>
    private void SixViews(double? worldTime, string when)
    {
        using var sim = new Simulation(SixParts, world: worldTime is { } time ? new WorldState { Time = time } : null);
        var flight = sim.Flight;
        // Each part a little apart, then every engine lit (a dropped stage's is shut down as it comes
        // away), so there are six plumes to draw too.
        sim.Run(Climb, f => f.Ground > 200f, maxSeconds: 60);
        for (var i = 0; i < 5; i++)
        {
            sim.Step(new FlightInput(Stage: true));
            sim.Run((_, _) => default, _ => false, maxSeconds: 1);
        }
        var flown = flight.Controlled;
        foreach (var vessel in flight.Vessels.ToArray())
        {
            sim.Step(new FlightInput(SwitchTo: vessel));
            // Full, but the booster only just lit: built to lift all six, alone at full it would catch the
            // stage above it, and they would collide.
            sim.Step(vessel.Stage == 0 ? new FlightInput(Throttle: 1f) : new FlightInput(FullThrottle: true));
        }
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Run((_, _) => default, _ => false, maxSeconds: 1);
        foreach (var vessel in flight.Vessels)
            output.WriteLine($"{vessel.Name}: {vessel.State} {vessel.CrashReason}, {vessel.Ground:0} m up");
        Assert.Equal(6, flight.Vessels.Count);
        Assert.All(flight.Vessels, vessel => Assert.Equal(FlightState.Flying, vessel.State));
        Assert.All(flight.Vessels, vessel => Assert.True(vessel.EngineLit, $"the {vessel.Name.ToLowerInvariant()}'s engine isn't firing"));
        var others = flight.Vessels.Where(v => v != flight.Controlled).ToArray();
        output.WriteLine($"Home's local time {Sun.Clock(Sun.LocalSeconds(GroundBody.Planet, Vector3.Zero, flight.WorldTime))}");

        var results = TimeViews(sim, others, [0, 1, 2, 3, 4, 5]);
        var four = results.Single(r => r.Views == 4);
        Assert.True(four.Median <= FrameBudgetMs,
            $"the main view and 3 small ones{when} took a median {four.Median:0.00} ms a frame: more than {FrameBudgetMs:0.0} ms (60 fps)");
    }

    [PerfFact]
    public void A_world_of_a_hundred_vessels_holds_60_fps_too()
    {
        // A world that has had many flights: a hundred satellites round the planet, every one of
        // them drawn in every view, and a rocket on the pad.
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldTests.HundredSatellites());
        var others = sim.Flight.WorldVessels.Where(v => v != sim.Flight.Controlled).ToArray();
        Assert.Equal(100, others.Length);

        var results = TimeViews(sim, others, [0, 3]);
        var four = results.Single(r => r.Views == 4);
        Assert.True(four.Median <= FrameBudgetMs,
            $"with a hundred vessels, the main view and 3 small ones took a median {four.Median:0.00} ms a frame: more than {FrameBudgetMs:0.0} ms (60 fps)");
    }

    [PerfFact]
    public void A_hundred_landed_vessels_lit_at_night_hold_60_fps_too()
    {
        // Home's midnight, a hundred boosters landed, every one's landing lights on (they load
        // lit), and a rocket on the pad: twelve by the landing zone, within 500 m of the flown
        // vessel, so each view has its four ground lights, and the rest 6.7 km off on the level
        // ground, so those are far glows only. Every one is drawn in every view.
        var midnight = Sun.WorldTimeFor(Vector3.Zero, 0);
        var near = Enumerable.Range(0, 12).Select(i => (X: Scene.LandingZoneCentre.X + 40f + 30f * (i % 4), Z: Scene.LandingZoneCentre.Z + 40f * (i / 4)));
        var far = Enumerable.Range(0, 88).Select(i => (X: Drops.LevelGround.X + 60f * (i % 10), Z: Drops.LevelGround.Z + 60f * (i / 10)));
        var states = near.Concat(far).Select((p, i) =>
        {
            var at = Planet.SurfacePoint(p.X, p.Z);
            return Drops.Placed(at, Planet.East(at), 0f, id: i) with { State = FlightState.Landed };
        }).ToArray();
        using var sim = new Simulation(Mission.LaunchAndLand, world: new WorldState { Time = midnight, Vessels = states, NextVesselId = states.Length });
        var others = sim.Flight.WorldVessels.Where(v => v != sim.Flight.Controlled).ToArray();
        Assert.Equal(100, others.Length);
        Assert.All(others, v => Assert.Equal(1f, v.LandingLights));
        output.WriteLine($"{others.Count(v => Vector3.Distance(v.Position, sim.Flight.Controlled.Position) < Scene.LandingLightReach)} of them within {Scene.LandingLightReach:0} m of the flown rocket");

        var results = TimeViews(sim, others, [0, 3]);
        var four = results.Single(r => r.Views == 4);
        Assert.True(four.Median <= FrameBudgetMs,
            $"with a hundred landed vessels lit at night, the main view and 3 small ones took a median {four.Median:0.00} ms a frame: more than {FrameBudgetMs:0.0} ms (60 fps)");
    }

    [PerfFact]
    public void Over_the_floe_band_the_main_view_and_three_small_views_hold_60_fps_too()
    {
        // The ice at its busiest. Four boosters hovering 40 m over the Austral Sea's floe band (lit: the
        // world starts at the band's local noon), where the most floes are near enough to draw, each posed
        // every frame; then twenty-four more crossing the band above them, so the physics holds as many
        // floe bodies as it may (Scene.MaxFloeBodies), and its steps are timed too.
        var band = IceTests.FloeBand("Austral Sea");
        var at = Planet.Centre + band * Planet.SeaRadius;
        var noon = Sun.WorldTimeFor(at, Scene.DaySeconds / 2);
        VesselState state;
        using (var one = Drops.At(at, Planet.East(at), 0f, clearance: 40f))
            state = one.Flight.WorldVessels.First(v => v.Id == 0).Capture(one.Flight.WorldVessels);
        var along = Planet.North(at);
        VesselState[] states = [.. Enumerable.Range(0, 4).Select(i => state with { Id = i, Position = state.Position + along * (60f * i) })];
        using var sim = new Simulation(Mission.LaunchAndLand, world: new WorldState { Time = noon, Vessels = states, NextVesselId = states.Length });
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0)));
        var others = sim.Flight.WorldVessels.Where(v => v != sim.Flight.Controlled).ToArray();
        output.WriteLine($"{Floes.Near(state.Position.ToSingle(), Scene.FloeDrawDistance).Count()} floes within {Scene.FloeDrawDistance:0} m of the flown booster");

        var results = TimeViews(sim, others, [0, 3]);
        var four = results.Single(r => r.Views == 4);
        Assert.True(four.Median <= FrameBudgetMs,
            $"over the floe band, the main view and 3 small ones took a median {four.Median:0.00} ms a frame: more than {FrameBudgetMs:0.0} ms (60 fps)");

        // The physics over the band, with the floe bodies at their cap: a frame's steps, timed.
        // (The four boosters over the band have their engines off: they fall, and would come down
        // on the ice about three seconds in, leaving only the rocket on the pad, near no floe. The
        // frames timed end before then.)
        var centre = Planet.Centre + IceTests.SheetMiddle("Austral Sea") * Planet.SeaRadius;
        var outFrom = Planet.Radius * Planet.AngleBetween(centre, at);
        for (var k = 0; k < 24; k++)
        {
            var p = Planet.Centre + IceTests.Travel(Planet.Up(centre), k * 15f, outFrom) * Planet.SeaRadius;
            var up = Planet.Up(p);
            var across = Vector3.Normalize(Vector3.Cross(up, Vector3.Normalize(p - centre)));
            // 100 m up, within the floes' reach (Scene.FloeReach) but clear above the four (40 to 62 m up),
            // which they would otherwise fly into: vessels collide.
            sim.Physics.AddLoose(Vehicle.Booster, p + up * 100f, Planet.UprightAt(p), across * 40f, Vector3.Zero);
        }
        sim.Run((_, _) => default, _ => sim.Physics.FloeBodyCount >= Scene.MaxFloeBodies, maxSeconds: 2);
        var busy = TimeSteps(sim, (_, _) => default, _ => false, 120);
        var (bodies, crafts) = (sim.Physics.FloeBodyCount, sim.Physics.Crafts.Count);
        output.WriteLine($"physics with {bodies} floe bodies and {crafts} crafts: median {busy.Median:0.00} ms, 95% {busy.P95:0.00} ms, worst {busy.Worst:0.00} ms a frame");
        sim.Dispose(); // one physics world at a time

        // And as a player would be there: one booster coming down slowly onto a floe, flown down
        // from 40 m over it, every frame of its descent timed until it has landed.
        Rafts.Time = 0.0; // the floe as it is at the start of the drop's world
        var floe = Floes.Near(at, 300f).MaxBy(f => f.HalfLength)!;
        floe.Set(0.0);
        var top = floe.Current.Position + floe.Normal * floe.Freeboard;
        using var drop = Drops.At(top, Planet.East(top), 3f, clearance: 40f);
        var lander = new Autopilot { Target = top, CutOffAltitude = 0f };
        var landing = TimeSteps(drop, lander.Fly, f => f.State != FlightState.Flying, 60 * 60);
        var booster = drop.Flight.Controlled;
        output.WriteLine($"one booster landing on a floe {floe.HalfLength:0.0} m round: {booster.State} after {drop.Time:0.0} s at {booster.TouchdownSpeed:0.0} m/s, " +
            $"with {drop.Physics.FloeBodyCount} floe bodies; physics median {landing.Median:0.00} ms, 95% {landing.P95:0.00} ms, worst {landing.Worst:0.00} ms a frame");

        Assert.Equal(Scene.MaxFloeBodies, bodies);
        Assert.Equal(1 + states.Length + 24, crafts); // none of them came down while it was timed
        Assert.True(busy.Median <= FrameBudgetMs / 2,
            $"with the floe bodies at their cap, a frame's physics took a median {busy.Median:0.00} ms: more than half a 60 fps frame ({FrameBudgetMs / 2:0.0} ms)");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(landing.Median <= FrameBudgetMs / 2,
            $"landing on a floe, a frame's physics took a median {landing.Median:0.00} ms: more than half a 60 fps frame ({FrameBudgetMs / 2:0.0} ms)");
    }

    [PerfFact]
    public void The_main_view_and_three_small_views_at_four_lit_places_hold_60_fps_at_Homes_noon() =>
        FourLitPlaces(600, "Home's noon");

    [PerfFact]
    public void The_main_view_and_three_small_views_at_four_lit_places_hold_60_fps_at_Homes_midnight() =>
        FourLitPlaces(0, "Home's midnight");

    private void FourLitPlaces(double homeSeconds, string when)
    {
        // The shadow map at its fullest for four views: each at a lit place of its own, so each has
        // its own light set, block of floodlight shadows and sun area. The rocket on Home's pad, and
        // a booster on Desert's pad, on Highcrest's and on Sentinel's drone ship, with the world
        // started (Sun.WorldTimeFor) at Home's local time given. Which places are then by day and
        // which at night depends on where they sit round the planet, so it is printed, not asserted.
        // Timed with the main view at Home and again at Highcrest (the rocket at Home then one of
        // the small views).
        var desert = LaunchSites.Named("Desert");
        var highcrest = LaunchSites.Named("Highcrest");
        var ship = LaunchSites.Named("Sentinel").DroneShips[0];
        var time = Sun.WorldTimeFor(Vector3.Zero, homeSeconds);
        var sun = Sun.Direction(GroundBody.Planet, time);
        string Side(Vector3 up) => Vector3.Dot(up, sun) > 0f ? "day" : "night";
        Assert.Equal(homeSeconds == 600 ? "day" : "night", Side(Vector3.UnitY)); // the two runs really do split
        output.WriteLine($"at {when}: Home {Side(Vector3.UnitY)}, Desert {Side(Planet.Up(desert.ToWorld(Vector3.Zero)))}, " +
            $"Highcrest {Side(Planet.Up(highcrest.ToWorld(Vector3.Zero)))}, Sentinel's ship {Side(Planet.Up(ship))}");
        VesselState[] states =
        [
            Drops.Placed(desert.ToWorld(Vector3.Zero), Planet.East(desert.ToWorld(Vector3.Zero)), 0f, seabed: true, id: 0),
            Drops.Placed(highcrest.ToWorld(Vector3.Zero), Planet.East(highcrest.ToWorld(Vector3.Zero)), 0f, seabed: true, id: 1),
            Drops.Placed(ship, Planet.East(ship), 0f, clearance: Scene.DroneShipFreeboard + Drops.Clearance, id: 2), // on its deck
        ];
        using var sim = new Simulation(Mission.LaunchAndLand, world: new WorldState { Time = time, Vessels = states, NextVesselId = states.Length });
        var flight = sim.Flight;
        var rocket = flight.Controlled;
        var (onDesert, onHighcrest, onShip) = (flight.WorldVessels.First(v => v.Id == 0), flight.WorldVessels.First(v => v.Id == 1), flight.WorldVessels.First(v => v.Id == 2));

        // Each at a place of its own: four sets with a block each, four sun areas.
        var sets = new LightSets();
        var allLit = Enumerable.Repeat(1f, PadLights.All.Count).ToArray(); // every place on: the cost at night
        var areas = new SunAreas();
        sets.Clear();
        areas.Clear();
        foreach (var vessel in new[] { rocket, onDesert, onHighcrest, onShip })
        {
            var (place, shared) = (-1, false);
            sets.Choose(Views.Focus(vessel), allLit, [], ref place);
            areas.Choose(vessel.Model, Views.Focus(vessel), ref shared);
        }
        Assert.Equal(4, sets.Blocks);
        Assert.Equal(4, areas.Areas.Length);

        var atHome = TimeViews(sim, [onDesert, onHighcrest, onShip], [3]).Single(r => r.Views == 4);
        sim.Step(new FlightInput(SwitchTo: onHighcrest));
        Assert.Same(onHighcrest, flight.Controlled);
        var atHighcrest = TimeViews(sim, [rocket, onDesert, onShip], [3]).Single(r => r.Views == 4);
        Assert.True(atHome.Median <= FrameBudgetMs,
            $"at four lit places at {when}, the main view (at Home) and 3 small ones took a median {atHome.Median:0.00} ms a frame: more than {FrameBudgetMs:0.0} ms (60 fps)");
        Assert.True(atHighcrest.Median <= FrameBudgetMs,
            $"at four lit places at {when}, the main view (at Highcrest) and 3 small ones took a median {atHighcrest.Median:0.00} ms a frame: more than {FrameBudgetMs:0.0} ms (60 fps)");
    }

    [PerfFact]
    public void Zoomed_out_over_the_ground_with_a_de_orbit_window_on_the_next_lap_it_holds_60_fps_too()
    {
        // The path at its costliest: zoomed out, over the ground, with a place targeted whose
        // de-orbit window is on the next lap, so the line runs round a second lap (worked out with the
        // plan) and every plan looks over this lap and the next for the window.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var (which, _) = FrameTests.WindowOnLap(sim, FrameChoice.Surface, 1);
        output.WriteLine($"{which}: the window on the next lap");

        // A new target's plan, from a new path each time: worked out over some frames, each
        // working on it for about Scene.WindowWorkMilliseconds, besides the path itself.
        var firstFrames = new List<double>();
        var frames = new int[20];
        for (var i = 0; i < frames.Length; i++)
        {
            var before = firstFrames.Count;
            Assert.Equal(1, FrameTests.Built(sim.Flight, firstFrames).WindowLap);
            frames[i] = firstFrames.Count - before;
        }
        Array.Sort(frames);
        var (firstMedian, firstP99, firstWorst) = Percentiles(firstFrames);
        output.WriteLine($"a new target's plan: over {frames[frames.Length / 2]} frames (median; {frames[0]} to {frames[^1]}), {firstFrames.Sum() / frames.Length:0.0} ms in all; a frame's path median {firstMedian:0.00} ms, 99% {firstP99:0.00} ms, worst {firstWorst:0.00} ms");
        Assert.True(firstMedian <= FrameBudgetMs / 2,
            $"a new target's plan's frames took a median {firstMedian:0.00} ms: more than half a 60 fps frame ({FrameBudgetMs / 2:0.0} ms)");

        // The path each frame once it is worked out, coasting, over the ground and among the
        // stars: the later laps are worked out with the plan, not each frame, and on the orbit it
        // was worked out on the plan is only held to it each frame, not worked out again (until
        // the horizon moves on: Scene.WindowReplanLaps of a lap, longer than this).
        foreach (var choice in new[] { FrameChoice.Orbit, FrameChoice.Surface })
        {
            sim.Step(new FlightInput(ChooseFrame: choice));
            var steady = new List<double>();
            var paths = FrameTests.Built(sim.Flight, null);
            var plan = FrameTests.PlanFor(sim.Flight);
            var replans = 0;
            for (var clock = Stopwatch.StartNew(); clock.Elapsed.TotalSeconds < 2;)
            {
                var start = Stopwatch.GetTimestamp();
                paths.Build(sim.Flight, plan); // a frame: a replan, if one starts, is worked on a little
                steady.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                replans += paths.Working ? 1 : 0;
                sim.Step(default); // coasting on
                plan = FrameTests.PlanFor(sim.Flight);
                Thread.Sleep(1); // at about a frame's pace, so a replan would be due as in play
            }
            var (median, p99, worst) = Percentiles(steady);
            output.WriteLine($"{choice}: the path each frame ({steady.Count} over 2 s, {replans} of them working on a replan), median {median:0.00} ms, 99% {p99:0.00} ms, worst {worst:0.00} ms");
            Assert.True(median <= FrameBudgetMs / 2, $"{choice}: the frames' paths took a median {median:0.00} ms: more than half a 60 fps frame ({FrameBudgetMs / 2:0.0} ms)");
        }

        var others = sim.Flight.WorldVessels.Where(v => v != sim.Flight.Controlled).ToArray();
        var results = TimeViews(sim, others, [0, 3], zoomOut: true);
        var four = results.Single(r => r.Views == 4);
        Assert.True(four.Median <= FrameBudgetMs,
            $"zoomed out with a next lap's de-orbit window, the main view and 3 small ones took a median {four.Median:0.00} ms a frame: more than {FrameBudgetMs:0.0} ms (60 fps)");
    }

    /// <summary>
    /// The room left between the hulls of the five upper stages side by side in orbit
    /// (<see cref="FiveUpperStagesInOrbit"/>), in metres: as close as they can be with none
    /// touching.
    /// </summary>
    private const float Gap = 1.5f;

    [PerfFact]
    public void Five_upper_stages_together_in_orbit_in_the_main_view_hold_60_fps()
    {
        // Five upper stages in one orbit, side by side along it, all in the main view (five hulls
        // drawn, and casting shadows, close together in one view), with the timings panel shown;
        // then with 3 small views, and a small view of each of the other four.
        using var sim = new Simulation(Mission.ReachOrbit, world: FiveUpperStagesInOrbit(out var normal));
        var flight = sim.Flight;
        var stages = flight.WorldVessels.Where(v => v.Id < 5).OrderBy(v => v.Id).ToArray();
        Assert.Equal(5, stages.Length);
        var flown = stages[2];
        sim.Step(new FlightInput(SwitchTo: flown));
        Assert.Same(flown, flight.Controlled);
        sim.Run((_, _) => default, _ => false, maxSeconds: 30);

        var radius = Vehicle.TwoStage.Stages[1].Radius;
        Assert.All(stages, s =>
        {
            Assert.Equal(FlightState.Flying, s.State);
            Assert.True(s.Periapsis > Scene.AtmosphereHeight, $"stage {s.Id}'s periapsis is {s.Periapsis:0} m: not clear of the air");
        });
        for (var i = 0; i < stages.Length; i++)
            for (var j = i + 1; j < stages.Length; j++)
            {
                // How far apart their axes are: what is left of the line between their middles off the first's axis.
                var d = stages[j].CentreOfMass - stages[i].CentreOfMass;
                var axes = (d - stages[i].Up * Vector3.Dot(d, stages[i].Up)).Length();
                Assert.True(axes >= 2f * radius + 0.5f, $"stages {stages[i].Id} and {stages[j].Id} are {axes:0.00} m apart, axis to axis: nearly touching");
            }
        output.WriteLine($"after a 30 s coast, neighbours {Vector3.Distance(stages[1].CentreOfMass, stages[2].CentreOfMass):0.00} m apart (a gap of {Gap} m between hulls {2f * radius:0.0} m across)");

        var others = stages.Where(s => s != flown).ToArray();
        var results = TimeViews(sim, others, [3, 4],
            prepare: view =>
            {
                // Looking across the line of five, from a little above it.
                var c = flown.CentreOfMass;
                var yaw = MathF.Atan2(Vector3.Dot(normal, Planet.East(c)), Vector3.Dot(normal, -Planet.North(c)));
                view.Camera.Place(yaw, 0.25f, Scene.CameraDistance);
            },
            warmedUp: (view, display) => Assert.All(stages, s =>
                Assert.True(view.Camera.ToScreen(s.CentreOfMass, display, out var at, out _)
                    && at.X >= 0f && at.Y >= 0f && at.X <= display.X && at.Y <= display.Y,
                    $"stage {s.Id} isn't on the screen")),
            timings: true);
        var four = results.Single(r => r.Views == 4);
        Assert.True(four.Median <= FrameBudgetMs, $"five upper stages in the main view, and 3 small ones, took a median {four.Median:0.00} ms a frame: more than {FrameBudgetMs:0.0} ms (60 fps)");
    }

    /// <summary>
    /// Five of the two-stage rocket's upper stages (half their fuel) in one circular orbit 3500 m
    /// up, eastward over Home's pad, side by side along it: each the middle one's orbit turned
    /// about its normal by its place along it, so they keep their places (matched orbits, as
    /// <see cref="FrameFlightTests"/>' rendezvous starts), <see cref="Gap"/> between hulls.
    /// Ids 0 to 4, along the way they go; <paramref name="normal"/> is the orbit's normal.
    /// </summary>
    private static WorldState FiveUpperStagesInOrbit(out Vector3 normal)
    {
        const float altitude = 3500f;
        var vehicle = Vehicle.TwoStage;
        var tanks = new float[vehicle.Stages.Length];
        tanks[1] = vehicle.Stages[1].FuelMass * 0.5f;
        var up = Planet.Up(Vector3.Zero);
        var r = Planet.SeaRadius + altitude;
        var centre = Planet.Centre + up * r;
        var stars = Planet.East(centre) * Planet.CircularSpeed(altitude);
        var stage = InOrbit.At(new VesselState
        {
            Id = 0, Flight = 0, Mission = Mission.ReachOrbit, Vehicle = vehicle, First = 1, Last = 1, Fuel = tanks,
            State = FlightState.Flying, Position = centre, Rotation = Quaternion.Identity, Velocity = Vector3.Zero,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 1, 1),
            AngularVelocity = Vector3.Zero, Aim = Vector3.UnitY,
            MaxAltitude = altitude, MaxGround = altitude, AchievedOrbit = true, HasBeenAirborne = true,
        }, centre, stars);
        var n = Vector3.Normalize(Vector3.Cross(centre - Planet.Centre, stars));
        var spacing = 2f * vehicle.Stages[1].Radius + Gap;
        VesselState[] states = [.. Enumerable.Range(-2, 5).Select(k =>
        {
            var along = Quaternion.CreateFromAxisAngle(n, k * spacing / r);
            return InOrbit.At(stage, Planet.Centre + Vector3.Transform(centre - Planet.Centre, along), Vector3.Transform(stars, along)) with { Id = k + 2 };
        })];
        normal = n;
        return new WorldState { Vessels = states, NextVesselId = states.Length };
    }

    /// <summary>The laps the low pass is found over: more than the frames below fly (some 450 s at most, the last lander 90 s on).</summary>
    private const int LowPassLaps = 3;

    /// <summary>How far ahead along the low pass's track the three landers in the small views start, in seconds.</summary>
    private static readonly double[] LowPassAhead = [30.0, 60.0, 90.0];

    [PerfFact]
    public void A_very_low_pass_over_the_moon_holds_60_fps()
    {
        // The moon's ground streamed in fast under the camera: the lander alone on the very low
        // orbit found from the moon's height map (LowPass, LowPass.Clearance over the highest
        // ground its track crosses), flown in the physics with its engine off, the main view on
        // it, and three more landers on the same track ahead of it, each in a small view of its
        // own over ground of its own: every view's patches chosen, and every lander's tiles laid,
        // as it goes. Flown at normal speed, then with the highest warp asked for (timed at
        // whatever the game leaves it at, printed), each frame timed whole (the flight's steps and
        // the drawing).
        var moon = GroundBody.Moon;
        var time = MoonTrip.Start;
        VesselState[] states = [LowPass.Start(0, moon, time, LowPassLaps), .. LowPassAhead.Select((s, i) => LowPass.Start(i + 1, moon, time, LowPassLaps, s))];
        var (normal, highest, radius, lap) = LowPass.Find(moon, time, LowPassLaps);
        output.WriteLine($"the low pass over {LowPassLaps} laps: {radius - moon.SeaRadius:0.0} m over the moon's datum, its track's highest ground {highest:0.0} m, a lap {lap:0} s; " +
            $"normal ({normal.X:0.000}, {normal.Y:0.000}, {normal.Z:0.000}); searched in {LowPass.SearchTime(moon, LowPassLaps).TotalSeconds:0.00} s");
        var (sim, flown) = MoonTripTests.Flying(MoonTrip.World(time, states));
        using var _ = sim;
        // The landers alone: the flight's new rocket waits on Home's pad beside them.
        var landers = sim.Flight.WorldVessels.Where(v => states.Any(s => s.Id == v.Id)).ToArray();
        var others = landers.Where(v => v != flown).ToArray();
        Assert.Equal(LowPassAhead.Length, others.Length);

        var results = FlyViews(sim, others, [1f, Scene.PhysicsTopRate]); // the fastest it allows, low over the moon
        foreach (var vessel in landers)
            output.WriteLine($"lander {vessel.Id}: {vessel.State} {vessel.CrashReason}, {vessel.Ground:0.0} m over the ground");
        Assert.All(landers, v => Assert.Equal(FlightState.Flying, v.State));
        foreach (var (rate, median, _, _) in results)
            Assert.True(median <= FrameBudgetMs,
                $"the very low pass over the moon with {rate:0.##}x asked, the main view and 3 small ones took a median {median:0.00} ms a frame: more than {FrameBudgetMs:0.0} ms (60 fps)");
    }

    /// <summary>
    /// Flies <paramref name="sim"/>'s flight in the game's window, with a small view of each of
    /// <paramref name="others"/>, with each of <paramref name="rates"/> asked for (the player's,
    /// stepped up or down to it as the keys would, and timed at whatever the game leaves it), a frame of 60 fps's real time at a time (in warp within the game's budget for
    /// its steps), and times each frame whole: the flight's steps and the drawing. Prints, for each
    /// rate, the median, 95th percentile and worst frame, the warp the game left and reached, and the frames' parts
    /// (the tiles' and their pieces among the steps', and the frame's, where the terrain patches
    /// are chosen and drawn).
    /// </summary>
    private List<(float Rate, double Median, double P95, double Worst)> FlyViews(Simulation sim, Vessel[] others, float[] rates)
    {
        var flight = sim.Flight;
        Assert.True(Glfw.Init(), "GLFW couldn't start (no desktop?)");
        var window = GameView.CreateWindow("GameOne: performance test");
        var results = new List<(float Rate, double Median, double P95, double Worst)>();
        try
        {
            using var view = new GameView(window, vsync: false);
            view.Hud.CloseMenu();
            view.Follow(flight);
            foreach (var vessel in others)
                view.Views.Open(vessel, flight);
            var watched = new List<Vessel>();
            var time = 0.0;
            double Frame(FlightInput input)
            {
                Glfw.PollEvents();
                watched.Clear();
                foreach (var small in view.Views.Small)
                    watched.Add(small.Vessel);
                var start = Stopwatch.GetTimestamp();
                var dt = flight.RunFrame(Simulation.FrameSeconds, input with { Watched = watched }, Flight.WarpBudgetSeconds);
                view.Present(dt, time += dt, flight, paused: false, Simulation.FrameSeconds);
                return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            // A longer warm-up first, so the GPU has clocked up before anything is timed.
            for (var i = 0; i < 4 * WarmUpFrames; i++)
            {
                Frame(default);
                // The flight's new rocket on Home's pad is given a view of its own, as this
                // flight's guided vessels are: only the views asked for are timed.
                foreach (var small in view.Views.Small.Where(s => !others.Contains(s.Vessel)).ToArray())
                    view.Views.Close(small, flight);
            }
            Assert.Equal(others.Length, view.Views.Small.Count);

            foreach (var rate in rates)
            {
                // Asked for, as the keys would; the game may leave it lower (a warning drops the
                // warp), and the rate it leaves is the one timed, printed with it.
                for (var i = 0; i < Flight.Rates.Length && flight.PlayerRate != rate; i++)
                    Frame(new FlightInput(Faster: flight.PlayerRate < rate, Slower: flight.PlayerRate > rate));
                Timings.Enabled = false;
                Timings.Enabled = true;
                for (var i = 0; i < WarmUpFrames; i++)
                    Frame(default);
                var times = new double[TimedFrames];
                var (least, most) = (float.PositiveInfinity, 0f);
                for (var i = 0; i < times.Length; i++)
                {
                    times[i] = Frame(default);
                    (least, most) = (MathF.Min(least, flight.TimeScale), MathF.Max(most, flight.TimeScale));
                }
                var frames = Timings.Frames.Skip(Timings.Frames.Count - TimedFrames).ToArray();
                Array.Sort(times);
                results.Add((rate, times[times.Length / 2], times[(int)(times.Length * 0.95)], times[^1]));
                var reached = frames.Select(f => (double)f.WarpReached).Order().ToArray();
                output.WriteLine($"  {rate:0.##}x asked: 99% {times[(int)(times.Length * 0.99)]:0.00} ms, {times.Count(t => t > FrameBudgetMs)} of {times.Length} frames over {FrameBudgetMs:0.0} ms; " +
                    $"the game left it at {least:0.##}x to {most:0.##}x ({flight.PlayerRate:0.##}x at the end), reached {reached[reached.Length / 2]:0.#}x (median); " +
                    $"{frames.Average(f => f.Tiles):0} ground tiles laid (mean)");
                PrintParts(frames);
                output.WriteLine("    the tiles (median ms of the frames' steps): " + string.Join(", ",
                    new[] { $"{StepPart.Tiles} {Median(frames.Select(f => f[StepPart.Tiles])):0.000}" }.Concat(
                        Enum.GetValues<StepDetail>().Where(d => Timings.PartOf(d) == StepPart.Tiles).Select(d => $"{d} {Median(frames.Select(f => f[d])):0.000}"))));
                output.WriteLine($"    the terrain patches: chosen in {FramePart.Record} {Median(frames.Select(f => f[FramePart.Record])):0.000} ms, drawn in the GPU's {GpuPass.Scene} " +
                    $"{Median(frames.Select(f => f[GpuPass.Scene])):0.000} and {GpuPass.SmallViews} {Median(frames.Select(f => f[GpuPass.SmallViews])):0.000} ms (median)");
            }
            output.WriteLine($"Frame times at {window.FramebufferSize.X}x{window.FramebufferSize.Y} ({view.PresentMode}), {flight.WorldVessels.Count} vessels, {TimedFrames} frames each:");
        }
        finally
        {
            Timings.Enabled = false;
            window.Dispose();
            Glfw.Terminate();
        }

        output.WriteLine("  rate   median ms   95% ms   worst ms   fps (median)");
        foreach (var (rate, median, p95, worst) in results)
            output.WriteLine($"{rate,5:0.##}x {median,10:0.00} {p95,8:0.00} {worst,10:0.00} {1000 / median,14:0}");
        return results;
    }

    /// <summary>The median of <paramref name="values"/>, those that are numbers (NaN if none are).</summary>
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[sorted.Length / 2];
    }

    /// <summary>The median, 99th percentile and worst of <paramref name="times"/>.</summary>
    private static (double Median, double P99, double Worst) Percentiles(List<double> times)
    {
        var sorted = times.Order().ToArray();
        return (sorted[sorted.Length / 2], sorted[(int)(sorted.Length * 0.99)], sorted[^1]);
    }

    /// <summary>
    /// Times each frame of <paramref name="sim"/>'s flight (<see cref="Simulation.Step"/>), flown by
    /// <paramref name="pilot"/>, for <paramref name="frames"/> frames or until <paramref name="until"/>:
    /// the median, 95th percentile and worst frame.
    /// </summary>
    private static (double Median, double P95, double Worst) TimeSteps(Simulation sim, Pilot pilot, Func<Flight, bool> until, int frames)
    {
        var steps = new List<double>();
        while (steps.Count < frames && !until(sim.Flight))
        {
            var input = pilot(sim.Flight, Simulation.FrameSeconds);
            var start = Stopwatch.GetTimestamp();
            sim.Step(input);
            steps.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        steps.Sort();
        return (steps[steps.Count / 2], steps[(int)(steps.Count * 0.95)], steps[^1]);
    }

    /// <summary>
    /// Times frames of <paramref name="sim"/>'s flight, held still, in the game's window, with each
    /// number of small views in <paramref name="smallViews"/> (of the first of <paramref name="others"/>),
    /// and prints them: the median, 95th percentile and worst frame for each. With
    /// <paramref name="zoomOut"/>, the camera zoomed out, so the path is drawn (close by the vessel
    /// it isn't). <paramref name="prepare"/> sets the view up once it follows the flight, and
    /// <paramref name="warmedUp"/> checks it after the first warm-up (given the framebuffer's
    /// size). With <paramref name="timings"/>, the timings panel is shown and the timings are on,
    /// afresh for each number of views, and each one's median frame is printed part by part, on
    /// the CPU and the GPU.
    /// </summary>
    private List<(int Views, double Median, double P95, double Worst)> TimeViews(Simulation sim, Vessel[] others, int[] smallViews, bool zoomOut = false,
        Action<GameView>? prepare = null, Action<GameView, Vector2>? warmedUp = null, bool timings = false)
    {
        var flight = sim.Flight;
        Assert.True(Glfw.Init(), "GLFW couldn't start (no desktop?)");
        var window = GameView.CreateWindow("GameOne: performance test");
        var results = new List<(int Views, double Median, double P95, double Worst)>();
        try
        {
            using var view = new GameView(window, vsync: false);
            view.Hud.CloseMenu();
            view.Follow(flight);
            if (zoomOut)
                view.ToggleZoomOut(flight); // zoomed all the way out by the end of the warm-up
            if (timings)
                view.Hud.Settings = view.Hud.Settings with { ShowTimings = true };
            prepare?.Invoke(view);
            var time = 0.0;
            double Frame()
            {
                Glfw.PollEvents();
                flight.Update(0f, default); // held still: no time passes, so nothing moves (or sounds again)
                var start = Stopwatch.GetTimestamp();
                view.Present(1f / 60, time += 1.0 / 60, flight, paused: false);
                return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            // The views see every vessel there is, and open a view for each new one; and a longer
            // warm-up first, so the GPU has clocked up before anything is timed.
            for (var i = 0; i < 4 * WarmUpFrames; i++)
                Frame();
            var framebuffer = window.FramebufferSize;
            warmedUp?.Invoke(view, new Vector2(framebuffer.X, framebuffer.Y));

            foreach (var small in smallViews)
            {
                foreach (var each in view.Views.Small.ToArray())
                    view.Views.Close(each, flight);
                foreach (var vessel in others.Take(small))
                    view.Views.Open(vessel, flight);
                // Turned on afresh before the warm-up, so the panel has its averages by the
                // frames timed, and is drawn then as it would be in play.
                Timings.Enabled = false;
                Timings.Enabled = timings;
                for (var i = 0; i < WarmUpFrames; i++)
                    Frame();
                Assert.Equal(small, view.Views.Small.Count);
                var times = new double[TimedFrames];
                for (var i = 0; i < times.Length; i++)
                    times[i] = Frame();
                Array.Sort(times);
                results.Add((1 + small, times[times.Length / 2], times[(int)(times.Length * 0.95)], times[^1]));
                // Spikes or sustained slowness: the 99th percentile and how many frames ran over a 60 fps frame.
                output.WriteLine($"  {1 + small} views: 99% {times[(int)(times.Length * 0.99)]:0.00} ms, {times.Count(t => t > FrameBudgetMs)} of {times.Length} frames over {FrameBudgetMs:0.0} ms");
                if (timings)
                    PrintParts(Timings.Frames.Skip(Timings.Frames.Count - TimedFrames).ToArray());
            }

            output.WriteLine($"Frame times at {framebuffer.X}x{framebuffer.Y} ({view.PresentMode}), {flight.WorldVessels.Count} vessels, {TimedFrames} frames each:");
        }
        finally
        {
            Timings.Enabled = false;
            window.Dispose();
            Glfw.Terminate();
        }

        output.WriteLine("  views   median ms   95% ms   worst ms   fps (median)");
        foreach (var (views, median, p95, worst) in results)
            output.WriteLine($"{views,7} {median,11:0.00} {p95,8:0.00} {worst,10:0.00} {1000 / median,14:0}");
        return results;
    }

    /// <summary>The median of each part of <paramref name="frames"/>, on the CPU and the GPU (of the frames with the GPU's times: they come a frame or two late, or not at all).</summary>
    private void PrintParts(FrameSample[] frames)
    {
        static double Median(IEnumerable<double> values)
        {
            var sorted = values.Where(v => !double.IsNaN(v)).Order().ToArray();
            return sorted.Length == 0 ? double.NaN : sorted[sorted.Length / 2];
        }
        output.WriteLine($"    CPU (median ms of {frames.Length} frames): " + string.Join(", ",
            Enum.GetValues<FramePart>().Select(part => $"{part} {Median(frames.Select(f => f[part])):0.000}")));
        output.WriteLine($"    GPU (median ms of the {frames.Count(f => !double.IsNaN(f.GpuMs))} frames with its times): " + string.Join(", ",
            Enum.GetValues<GpuPass>().Select(pass => $"{pass} {Median(frames.Select(f => f[pass])):0.000}")) +
            $", whole {Median(frames.Select(f => f.GpuMs)):0.000}");
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that only runs with <c>GAMEONE_PERF</c> set: the windowed tests need a
/// GPU and a desktop, and the short ones (<see cref="ShortPerfTests"/>, headless) are run only when
/// working on performance, never as part of the normal suite.
/// </summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GAMEONE_PERF") is null or "" or "0")
            Skip = "A performance test: set GAMEONE_PERF=1 to run it (see README.md).";
    }
}
