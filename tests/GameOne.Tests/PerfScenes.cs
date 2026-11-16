using System.Diagnostics;
using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The short performance tests' scenes, each loading a part of the physics: a world of a hundred
/// vessels (the rules and the contact and nearness work for many), the floe band with its floe
/// bodies at their cap (the water), boosters flying low over rough ground (the ground's
/// tiles), and five rockets launched together to orbit (thrust, steering, the air and nearness,
/// for five at once). Each starts from a world
/// it writes, set up untimed, then times a stretch of steps and checks it did what it should: a
/// scene that doesn't is a broken test, and fails.
/// </summary>
internal static class PerfScenes
{
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>
    /// The world of a hundred satellites the windowed test draws (<see cref="WorldTests.HundredSatellites"/>),
    /// and a rocket on the pad, coasting for 2400 steps (40 s of game time).
    /// </summary>
    public static PerfScene HundredSatellites() => new("A hundred satellites coasting", () =>
    {
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldTests.HundredSatellites());
        sim.Step(default); // the loaded world's first step, untimed: setting up, not coasting
        var run = Time(sim, 2400);

        var flight = sim.Flight;
        Assert.Equal(101, flight.WorldVessels.Count);
        Assert.All(flight.WorldVessels.Where(v => v.Flight == 0), v =>
        {
            Assert.Equal(FlightState.Flying, v.State);
            Assert.True(v.OrbitStable, $"vessel {v.Id} is no longer in a stable orbit");
        });
        return run;
    });

    /// <summary>
    /// The windowed floe case's physics (<see cref="PerformanceTests"/>), set up the same way in a
    /// scene of its own: four boosters 40 m over the Austral Sea's floe band at its noon, and 24
    /// more crossing it 100 m up, run untimed until the physics holds as many floe bodies as it
    /// may (<see cref="Scene.MaxFloeBodies"/>), then 120 steps timed. (The four have their engines
    /// off and come down on the ice about three seconds in, so the stretch can't be longer: a run
    /// sets it up and times it <see cref="ShortRepeats"/> times instead.)
    /// </summary>
    public static PerfScene FloeBand() => new("Over the floe band", () => Repeated(FloeBandOnce));

    private static SceneRun FloeBandOnce()
    {
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

        var centre = Planet.Centre + IceTests.SheetMiddle("Austral Sea") * Planet.SeaRadius;
        var outFrom = Planet.Radius * Planet.AngleBetween(centre, at);
        for (var k = 0; k < 24; k++)
        {
            var p = Planet.Centre + IceTests.Travel(Planet.Up(centre), k * 15f, outFrom) * Planet.SeaRadius;
            var up = Planet.Up(p);
            var across = Vector3.Normalize(Vector3.Cross(up, Vector3.Normalize(p - centre)));
            // 100 m up, within the floes' reach but clear above the four, as in the windowed case.
            sim.Physics.AddLoose(Vehicle.Booster, p + up * 100f, Planet.UprightAt(p), across * 40f, Vector3.Zero);
        }
        sim.Run(Nobody, _ => sim.Physics.FloeBodyCount >= Scene.MaxFloeBodies, maxSeconds: 2);
        var run = Time(sim, 120);

        Assert.Equal(Scene.MaxFloeBodies, sim.Physics.FloeBodyCount);
        Assert.Equal(1 + states.Length + 24, sim.Physics.Crafts.Count); // none of them came down while it was timed
        return run;
    }

    /// <summary>
    /// Six boosters (legs stowed, engines off) thrown up and out at 20 m/s up and 80 m/s across,
    /// 60 degrees apart, from 60 m over a rough slope (<see cref="Drops.FindSlope"/>), the first
    /// flown: 300 steps (5 s) timed, inside their 6 s in the air, so the ground's tiles are laid
    /// ahead of them and taken up behind all the while. As they would come down after 6 s, a run
    /// throws them <see cref="ShortRepeats"/> times.
    /// </summary>
    public static PerfScene LowOverRoughGround() => new("Low over rough ground", () =>
    {
        // Where the tiles came from, summed over the repeats: the worker's, made in the step, or kept shapes.
        var from = new long[3];
        var run = Repeated(() => LowOverRoughGroundOnce(from));
        return run with { Note = $"tiles: {from[0]} by the worker, {from[1]} made in the step, {from[2]} from kept shapes" };
    });

    private static SceneRun LowOverRoughGroundOnce(long[] from)
    {
        var slope = RoughSlope.Value;
        var (east, north) = (Planet.East(slope), Planet.North(slope));
        var states = new VesselState[6];
        for (var i = 0; i < states.Length; i++)
        {
            var (s, c) = MathF.SinCos(MathF.Tau * i / states.Length);
            var across = east * c + north * s;
            // Each starts 30 m out from the slope along its own way, as six from one place would
            // start inside each other; flying apart, no two ever hit. (Placed sets its height.)
            states[i] = Drops.Placed(slope + across * 30f, across, speed: -20f, sideways: across * 80f, clearance: 60f, legsOut: false, id: i);
        }
        using var sim = new Simulation(Mission.LaunchAndLand, world: new WorldState { Vessels = states, NextVesselId = states.Length });
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0)));

        int most = 0, falls = 0, last = sim.Physics.TileCount;
        var run = Time(sim, 300, () =>
        {
            var tiles = sim.Physics.TileCount;
            most = Math.Max(most, tiles);
            if (tiles < last)
                falls++;
            last = tiles;
        });

        var tiles = sim.Physics.Tiles;
        (from[0], from[1], from[2]) = (from[0] + tiles.FromWorker, from[1] + tiles.MadeInStep, from[2] + tiles.FromKept);
        Assert.All(sim.Flight.WorldVessels.Where(v => v.Id < states.Length), v => Assert.Equal(FlightState.Flying, v.State));
        Assert.True(most > 6, $"at most {most} tiles were laid");
        Assert.True(falls > 0, "no tiles were taken up");
        Assert.True(ShortPerf.Percentiles(run.Steps.Select(s => s[StepPart.Tiles])).Median > 0, "the tiles took no time");
        return run;
    }

    /// <summary>
    /// How far from the pad's centre (east, north, south and west) the four rockets beside it
    /// stand in <see cref="FiveRockets"/>: as near as they can be. At 20 m (just outside the
    /// 19 m a new flight clears round the pad) the west one flies into the service tower 3 s up;
    /// at 23 m all five reach orbit (and at 27 m).
    /// </summary>
    public const float FiveRocketsOffset = 23f;

    /// <summary>How long the five rockets may take to all be in orbit, from the first launch, before the scene is broken.</summary>
    private const double FiveRocketsMaxSeconds = 120;

    /// <summary>
    /// The user's busy scene: five two-stage rockets launched together and flown to orbit, each
    /// by a pilot of its own (<see cref="OrbitAutopilot"/>), one flown a step, in turn. Four stand
    /// landed round Home's pad (<see cref="FiveRocketsOffset"/> east, north, south and west of its
    /// centre) and one on it. They are launched a step apart (as near together as they can be, as
    /// one vessel is switched to a step), eastmost first, so each is behind the one before in
    /// place and time; then flown in turn until every pilot is done. Only the flown vessel gets
    /// input: the others keep their throttle and their holds, so each flies on as it was left.
    /// The boosters, dropped, are left to fall. Timed from the first launch to all done, with the
    /// steps that end with all five boosters burning reported too. <paramref name="say"/> is told
    /// how each run ended: when, and how far apart the five were.
    /// </summary>
    public static PerfScene FiveRockets(Action<string>? say = null) => new("Five rockets to orbit", () =>
    {
        var (east, north) = (Planet.East(Vector3.Zero), Planet.North(Vector3.Zero));
        Vector3[] places = [east, north, -north, -east]; // ids 0 to 3: E, N, S, W
        VesselState[] states = [.. places.Select((way, i) =>
        {
            var off = way * FiveRocketsOffset;
            // Put down Flying, just over the ground, and left to settle to Landed.
            return Drops.Placed(Planet.SurfacePoint(off.X, off.Z), east, 0f, Vehicle.TwoStage, mission: Mission.ReachOrbit,
                flight: 0, clearance: 0.05f, legsOut: false, fuel: 1f, id: i);
        })];
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = states, NextVesselId = states.Length });
        var flight = sim.Flight;
        var pad = flight.Controlled;
        for (var i = 0; i < 180; i++)
            sim.Step(default);
        Assert.Equal(5, flight.WorldVessels.Count);
        Assert.Equal(0, flight.ClearedFromPad); // none stands where a new flight clears the pad
        Assert.All(flight.WorldVessels.Where(v => v != pad), v => Assert.Equal(FlightState.Landed, v.State));
        Assert.Equal(FlightState.Ready, pad.State); // launched by Launch, as only the one on the pad is

        var placed = flight.WorldVessels.Where(v => v != pad).OrderBy(v => v.Id).ToArray();
        // Each pilot is bound to its vessel, which is its upper stage once the booster drops away.
        var rockets = new (string Name, Vessel Vessel)[] { ("E", placed[0]), ("N", placed[1]), ("pad", pad), ("S", placed[2]), ("W", placed[3]) }
            .Select(r => (r.Name, r.Vessel, Pilot: new OrbitAutopilot { LightWhenLanded = true, HoldAlongWhenDone = true })).ToArray();

        // Between the timed steps nothing is allocated (no LINQ, no messages built unless they
        // fail): garbage made there would be collected inside a step, and charged to it.
        var burning = new List<int>((int)(FiveRocketsMaxSeconds / Simulation.FrameSeconds) + rockets.Length);
        var steps = 0;
        bool AllBurning()
        {
            foreach (var r in rockets)
                if (r.Vessel.State != FlightState.Flying || r.Vessel.Stage != 0 || r.Vessel.Throttle <= 0f)
                    return false;
            return true;
        }
        bool AllDone()
        {
            foreach (var r in rockets)
                if (!r.Pilot.Done)
                    return false;
            return true;
        }
        void Fly(Vessel vessel, OrbitAutopilot pilot)
        {
            sim.Step(pilot.Fly(vessel, sim.Flight.WorldTime) with { SwitchTo = vessel });
            CoreKinds.Note();
            if (AllBurning())
                burning.Add(steps);
            steps++;
            // A rocket lost is a broken scene: said at once, with why, rather than once the time is up.
            foreach (var (name, each, _) in rockets)
                if (each.State == FlightState.Crashed)
                    Assert.Fail($"{name} crashed {steps * Simulation.FrameSeconds:0.0} s after the first launch, on stage {each.Stage}, {each.Altitude:0} m up: {each.CrashReason}");
        }

        StepSample[] timed;
        Timings.Enabled = true;
        try
        {
            foreach (var (_, vessel, pilot) in rockets)
                Fly(vessel, pilot);
            for (var turn = 0; !AllDone(); turn++)
            {
                if (steps * (double)Simulation.FrameSeconds > FiveRocketsMaxSeconds)
                    Assert.Fail($"after {FiveRocketsMaxSeconds:0} s not all five are in orbit: " +
                        string.Join(", ", rockets.Select(r => $"{r.Name} {r.Vessel.State}, stage {r.Vessel.Stage}, periapsis {r.Vessel.Periapsis:0} m{(r.Pilot.Done ? " (done)" : "")}")));
                var (_, vessel, pilot) = rockets[turn % rockets.Length];
                Fly(vessel, pilot);
            }
            timed = [.. Timings.Steps];
        }
        finally
        {
            Timings.Enabled = false;
        }
        // 120 s is 7200 steps, well within what the timings keep.
        Assert.Equal(steps, timed.Length);

        Assert.All(rockets, r =>
        {
            Assert.True(r.Vessel.CrashReason is null, $"{r.Name}: {r.Vessel.CrashReason}");
            Assert.Equal(FlightState.Flying, r.Vessel.State);
            Assert.Equal(1, r.Vessel.Stage);
            Assert.True(r.Vessel.Periapsis > Scene.AtmosphereHeight, $"{r.Name}'s periapsis is {r.Vessel.Periapsis:0} m: not clear of the air");
        });
        Assert.NotEmpty(burning);
        var apart = (from i in Enumerable.Range(0, rockets.Length)
                     from j in Enumerable.Range(i + 1, rockets.Length - i - 1)
                     select (Pair: $"{rockets[i].Name}-{rockets[j].Name}", Metres: Vector3.Distance(rockets[i].Vessel.CentreOfMass, rockets[j].Vessel.CentreOfMass))).ToArray();
        say?.Invoke($"all five in orbit {steps * Simulation.FrameSeconds:0.0} s after the first launch ({steps} steps, {burning.Count} with all five boosters burning); " +
            $"apart {apart.Min(a => a.Metres):0} to {apart.Max(a => a.Metres):0} m: {string.Join(", ", apart.Select(a => $"{a.Pair} {a.Metres:0}"))}");
        return new SceneRun(timed, new Dictionary<string, StepSample[]> { ["all five boosters burning"] = [.. burning.Select(i => timed[i])] });
    });

    /// <summary>
    /// Fifty boosters (legs out) coming down on their engines onto level ground, in a grid 30 m
    /// apart, nobody watching them (the flown vessel a satellite on the far side of the planet, 25 km
    /// up): each from 15 m up, falling at 3 m/s, its engine pushing 0.95 of its weight as it starts
    /// (the fuel it burns lightens it, by some 0.12 m/s² a second, so it slows as it comes down),
    /// touching down at about 4 m/s some 4 s in and standing there burning. 600 steps (10 s) timed from the first, the hand-out included:
    /// in the air, then on the ground. Not a baseline entry: it measures the formula (under it,
    /// they are moved and their touchdowns judged by it), and the same scene in the physics (on a
    /// tree whose formula can't fly them, or with them watched) is the comparison.
    /// </summary>
    public static PerfScene UnwatchedLandings() => new("Fifty boosters landing unwatched", () =>
    {
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var (east, north) = (Planet.East(at), Planet.North(at));
        var states = new List<VesselState>();
        for (var i = 0; i < 50; i++)
        {
            var place = Planet.Centre + Planet.Up(at + (east * (i % 10 - 4.5f) + north * (i / 10 - 2f)) * 30f) * Planet.SeaRadius;
            var placed = Drops.Placed(place, downhill, 3f, clearance: 15f, id: i);
            var stage = placed.Vehicle.Stages[placed.First];
            var weight = (Planet.Gravity(placed.Position) + Planet.OutwardPull(placed.Position)).Length();
            states.Add(placed with { Throttle = 0.95f * weight * (stage.DryMass + placed.Fuel[placed.First]) / stage.Thrust });
        }
        states.Add(FarSatellite(at, states.Count));
        using var sim = new Simulation(Mission.LaunchAndLand, world: new WorldState { Vessels = states, NextVesselId = states.Count });
        var boosters = sim.Flight.WorldVessels.Where(v => v.Flight == 0 && v.Id < 50).ToArray();
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.Single(v => v.Id == 50))); // untimed: setting up
        var run = Time(sim, 600);
        var down = boosters.Count(b => b.State == FlightState.Landed || (b.State == FlightState.Flying && b.Grounded));
        Assert.All(boosters, b => Assert.NotEqual(FlightState.Crashed, b.State));
        Assert.True(down == boosters.Length, $"only {down} of {boosters.Length} came down");
        return run with { Note = $"{down} of {boosters.Length} down, {boosters.Count(b => b.Craft.Removed)} out of the physics at the end" };
    });

    /// <summary>A satellite on a circle <paramref name="altitude"/> up (25 km: some 50 km from it) over the point opposite <paramref name="there"/>.</summary>
    internal static VesselState FarSatellite(Vector3 there, int id, float altitude = 25000f)
    {
        var template = WorldPresets.Satellites().Vessels[0];
        var down = -Planet.Up(there);
        var centre = Planet.Centre + down * (Planet.SeaRadius + altitude);
        var along = Vector3.Normalize(Vector3.Cross(down, Planet.NorthPole));
        var comHeight = RocketGeometry.CentreOfMassHeight(template.Vehicle, template.First, template.Last);
        // Its nose down (from +Y): straight down from the top of the planet (over Home) is half a turn about any axis across it.
        var axis = Vector3.Cross(Vector3.UnitY, down);
        var rotation = axis.LengthSquared() < 1e-10f
            ? (down.Y > 0f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI))
            : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitY, down), -1f, 1f)));
        return template with
        {
            Id = id, Position = centre - down * comHeight, Rotation = rotation, Aim = down,
            Velocity = Planet.OverGround(centre, along * Planet.CircularSpeed(altitude)), MaxAltitude = altitude, MaxGround = altitude,
        };
    }

    /// <summary>
    /// How many times a short scene's stretch is set up and timed in one run. One alone (120 or
    /// 300 steps) varied by 10-20% from run to run in a part; four, put together,
    /// give a run enough steps for its medians to hold well within the spread the tests are to see past.
    /// </summary>
    public const int ShortRepeats = 4;

    /// <summary>
    /// <paramref name="once"/>'s stretch, set up and timed <see cref="ShortRepeats"/> times (each
    /// in a simulation of its own, as only one can exist at a time), as one run: the steps one
    /// after another.
    /// </summary>
    private static SceneRun Repeated(Func<SceneRun> once)
    {
        var runs = Enumerable.Range(0, ShortRepeats).Select(_ => once()).ToArray();
        // Putting stretches together isn't done here: none of the short scenes has any (the one
        // that has, the five rockets', is long enough to run once).
        Assert.All(runs, r => Assert.Null(r.Stretches));
        return new SceneRun([.. runs.SelectMany(r => r.Steps)]);
    }

    /// <summary>One case of the speed sweep: what flew, and how its frames went. It fell through if it went more than a metre into the ground or was ever near it with no tile under it (<see cref="Drops.TileUnder"/>).</summary>
    internal sealed record SweepCase(string Name, string Kind, float Speed, float Degrees, int Frames, double MedianMs, double P95Ms,
        long FromWorker, long MadeInStep, long FromKept, bool FellThrough, string Ended);

    /// <summary>
    /// The speeds the sweep coasts low over the rough slope at (m/s), each in a low arc inside the
    /// urgent band (<see cref="Drops.Level"/>): low orbit is about 313, and faster isn't skimming
    /// the ground for long; the top speed's pass, rising away from it as it goes, is the costliest
    /// low pass there is, and hits nothing, so it is never slowed for.
    /// </summary>
    public static readonly float[] SweepLevelSpeeds = [50f, 100f, 200f, 300f, Scene.TopSpeed];

    /// <summary>The speeds (m/s) and angles below level the sweep crashes at, as the crash theory in <see cref="TerrainTileTests"/>, up to the top speed (each over a fast collision's speed slowed for as it comes).</summary>
    public static readonly float[] SweepCrashSpeeds = [100f, 300f, 1000f, 3000f, Scene.TopSpeed];
    public static readonly float[] SweepCrashAngles = [90f, 45f, 10f];

    /// <summary>
    /// The speed sweep: how fast a craft can go near the ground before the physics of a frame
    /// outgrows <see cref="ShortPerf.PlayableFrameMs"/>. Each case is flown paced at real time (the
    /// loop waits out each 1/60 s frame before the next step, never short-changing a frame that ran
    /// long), so the worker has only the time the game would give it. Nothing in the game paces it.
    /// A first case is flown and thrown away, so the first timed one isn't the JIT's. A frame is
    /// one flight step (a frame's worth of game time, two of Jolt's steps).
    /// </summary>
    public static List<SweepCase> SpeedSweep()
    {
        var cases = new List<SweepCase>();
        using (FastCores.Hold())
        {
            try
            {
                Fly("warm-up", "level", 100f, 0f, Drops.Level(100f), 180);
                foreach (var speed in SweepLevelSpeeds)
                    cases.Add(Fly($"level {speed:0} m/s", "level", speed, 0f, Drops.Level(speed), 300));
                foreach (var angle in SweepCrashAngles)
                    foreach (var speed in SweepCrashSpeeds)
                        cases.Add(Fly($"crash {speed:0} m/s at {angle:0}°", "crash", speed, angle, Drops.Crashing(speed, angle).State, 600));
            }
            finally
            {
                Timings.Enabled = false;
            }
        }
        return cases;

        SweepCase Fly(string name, string kind, float speed, float degrees, VesselState state, int most)
        {
            using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
            var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
            sim.Step(new FlightInput(SwitchTo: vessel)); // untimed: it is flown, and the world's first step
            var (lowest, bare) = (float.MaxValue, 0);
            var frame = TimeSpan.FromSeconds(Simulation.FrameSeconds);
            var clock = Stopwatch.StartNew();
            Timings.Enabled = true;
            var frames = 0;
            while (frames < most && vessel.State == FlightState.Flying)
            {
                var began = clock.Elapsed;
                sim.Step(default);
                frames++;
                lowest = MathF.Min(lowest, Drops.Lowest(vessel));
                if (!Drops.TileUnder(sim, vessel))
                    bare++;
                WaitOut(clock, began + frame);
            }
            var seconds = frames * Simulation.FrameSeconds;
            var ended = kind == "level"
                ? (vessel.State == FlightState.Flying ? $"coasted {seconds:0.0} s, still flying" : $"coasted {seconds:0.0} s, then {vessel.State}")
                : (vessel.State == FlightState.Flying ? "still flying" : $"{vessel.State} {vessel.CrashReason}".Trim());
            var steps = Timings.Steps.ToArray();
            Timings.Enabled = false;
            var (median, p95, _) = ShortPerf.Percentiles(steps.Select(s => s.TotalMs));
            var tiles = sim.Physics.Tiles;
            return new SweepCase($"Speed sweep: {name}", kind, speed, degrees, frames, median, p95,
                tiles.FromWorker, tiles.MadeInStep, tiles.FromKept, lowest < -1f || bare > 0, ended);
        }
    }

    /// <summary>
    /// Waits to <paramref name="end"/> on <paramref name="clock"/>, the frame's end: it sleeps most
    /// of the wait, then spins the last of it. A frame that ran long is not made up for: the next
    /// one starts when it ends.
    /// </summary>
    private static void WaitOut(Stopwatch clock, TimeSpan end)
    {
        while (clock.Elapsed < end)
        {
            if (end - clock.Elapsed > TimeSpan.FromMilliseconds(2))
                Thread.Sleep(1);
            else
                Thread.SpinWait(50);
        }
    }

    /// <summary>
    /// One fast collision flown paced at real time, as the speed sweep is: its frames before the
    /// slow-down (the collision still far, at the player's rate) and during it (each frame's physics,
    /// its least scale, its real seconds as frames, and the shares of its frames' physics that
    /// predicting the collisions, <see cref="StepDetail.Collisions"/>, and the ground's tiles took), and
    /// each slowed frame whose physics was over <see cref="ShortPerf.PlayableFrameMs"/> (its number and ms).
    /// </summary>
    internal sealed record PacedCollision(string Name, bool Collides, int Frames, int BeforeFrames, double BeforeP95Ms, double BeforeMaxMs,
        int SlowedFrames, double SlowedMedianMs, double SlowedP95Ms, double SlowedMaxMs, float LeastScale,
        double CollisionShare, double TilesShare, long MadeInStep, string Ended, IReadOnlyList<(int Frame, double Ms, string Where)> SlowedOver)
    {
        public double SlowedRealSeconds => SlowedFrames * (double)Simulation.FrameSeconds;
    }

    /// <summary>
    /// The fast collisions <see cref="ShortPerfTests"/>' paced check flies, each over its whole
    /// slow-down: two boosters head on at twice the top speed closing, in the vacuum at 5 km,
    /// passing 10 m apart and colliding; and one into the rough slope at the top speed, low and
    /// shallow (10° below level: the ground's tiles laid fastest) and steeply (45°); and one at
    /// the top speed into the field of 49 boosters frozen on the zone, which its wake wakes
    /// together mid-slow-down; and a near miss the setting slows for (<see cref="NearMissTests.NearMissPass"/>,
    /// flown with <see cref="FlightInput.NearMisses"/> on). Each starts a little before its slow-down would.
    /// </summary>
    public static List<PacedCollision> FastCollisions()
    {
        var closing = 2f * Scene.TopSpeed;
        var cases = new List<PacedCollision>();
        using (FastCores.Hold())
        {
            try
            {
                Fly("warm-up", FastCollisionTests.Pair(closing, 5000f, 3f, sideways: 10f));
                cases.Add(Fly($"a pass head on, {closing:#,0} m/s closing at 5 km, 10 m apart", FastCollisionTests.Pair(closing, 5000f, 3f, sideways: 10f)));
                cases.Add(Fly($"a collision head on, {closing:#,0} m/s closing at 5 km", FastCollisionTests.Pair(closing, 5000f, 3f)));
                cases.Add(Fly($"into the rough slope at {Scene.TopSpeed:#,0} m/s, 10° below level", new WorldState { Vessels = [Drops.Crashing(Scene.TopSpeed, 10f).State], NextVesselId = 1 }));
                cases.Add(Fly($"into the rough slope at {Scene.TopSpeed:#,0} m/s, 45° below level", new WorldState { Vessels = [Drops.Crashing(Scene.TopSpeed, 45f).State], NextVesselId = 1 }));
                // A dive at the top speed into the field of 49 boosters frozen on the zone
                // (TerrainTileTests' field), at the one nearest its centre: the wake reaches the
                // field along the dive's way and wakes the lot together, mid-slow-down, so what
                // that costs a frame is measured.
                var field = TerrainTileTests.FrozenField(within: 4, firstId: 1);
                var zone = Scene.LandingZoneCentre;
                var centre = field.MinBy(b => Vector3.Distance(b.Position, zone))!;
                var middle = FastCollisionTests.Middle(centre.Position, centre.Rotation);
                var dive = Vector3.Normalize(Planet.East(middle) - Planet.Up(middle));
                cases.Add(Fly($"a dive at {Scene.TopSpeed:#,0} m/s, 45° below level, into a field of {field.Count} frozen boosters",
                    new WorldState { Vessels = [FastCollisionTests.Flying(middle, dive, Scene.TopSpeed, 3f, 0), .. field], NextVesselId = field.Count + 1 }));
                // And a pass that hits nothing, never slowed for: level at the top speed 30 m over
                // the floe band, the seabed's tiles and the floes laid as fast as they ever are, at
                // full speed. Its frames are all "before" ones, printed for the record.
                var band = Planet.Centre + IceTests.FloeBand("Boreal Sea") * Planet.SeaRadius;
                var east = Planet.East(band);
                cases.Add(Fly($"level 30 m over the floe band at {Scene.TopSpeed:#,0} m/s (hits nothing)",
                    new WorldState { Vessels = [Drops.Placed(band, east, 0f, legsOut: false, sideways: east * Scene.TopSpeed, clearance: 30f)], NextVesselId = 1 }, collides: false, most: 180));
                // And a near miss slowed by the setting (Flight.Slowed for a NearMiss, flown with it
                // on): Pair's two passing beside each other at 1,000 m/s, within a length but never
                // touching, its slow-down eased in and out by its own time, the predictor's chorded
                // look ahead at work over the whole of it.
                cases.Add(Fly("near miss 1000", NearMissTests.NearMissPass(1000f), collides: true, most: 1200, input: new FlightInput(NearMisses: true)));
            }
            finally
            {
                Timings.Enabled = false;
            }
        }
        return cases;

        PacedCollision Fly(string name, WorldState world, bool collides = true, int most = 1200, FlightInput input = default)
        {
            var (sim, flown, other) = FastCollisionTests.Fly(world);
            using var _ = sim;
            var frame = TimeSpan.FromSeconds(Simulation.FrameSeconds);
            var clock = Stopwatch.StartNew();
            var (before, slowed) = (new List<StepSample>(), new List<StepSample>());
            var (least, seen, frames) = (1f, false, 0);
            var over = new List<(int Frame, double Ms, string Where)>();
            Timings.Enabled = true;
            // To the slow-down's end (eased back after the collision), or most frames (20 s of real time).
            while (frames < most && !(seen && sim.Flight.Slowed is null))
            {
                var began = clock.Elapsed;
                // Whether this frame is slowed, and how slow, is how the flight stands as it begins:
                // its clock is set then, both read at that one moment.
                var (isSlowed, scale) = (sim.Flight.Slowed is not null, sim.Flight.TimeScale);
                var madeBefore = sim.Physics.Tiles.MadeInStep;
                sim.Step(input);
                frames++;
                var step = Timings.Steps[^1];
                (isSlowed ? slowed : before).Add(step);
                if (isSlowed)
                {
                    (seen, least) = (true, MathF.Min(least, scale));
                    if (step.TotalMs >= ShortPerf.PlayableFrameMs)
                        over.Add((frames, step.TotalMs, Where(step, sim, sim.Physics.Tiles.MadeInStep - madeBefore, "")));
                }
                WaitOut(clock, began + frame);
            }
            Timings.Enabled = false;
            var (_, beforeP95, _) = before.Count > 0 ? ShortPerf.Percentiles(before.Select(s => s.TotalMs)) : (0, 0, 0);
            var (median, p95, _) = slowed.Count > 0 ? ShortPerf.Percentiles(slowed.Select(s => s.TotalMs)) : (0, 0, 0);
            var total = slowed.Sum(s => s.TotalMs);
            var ended = $"{flown.State} {flown.CrashReason}".Trim() + (other is null ? "" : $"; the other {other.State} {other.CrashReason}".TrimEnd());
            return new PacedCollision(name, collides, frames, before.Count, beforeP95, before.Count > 0 ? before.Max(s => s.TotalMs) : 0,
                slowed.Count, median, p95, slowed.Count > 0 ? slowed.Max(s => s.TotalMs) : 0, least,
                total > 0 ? slowed.Sum(s => s[StepDetail.Collisions]) / total : 0, total > 0 ? slowed.Sum(s => s[StepPart.Tiles]) / total : 0,
                sim.Physics.Tiles.MadeInStep, ended, over);
        }
    }

    // Where a frame's time went, for one over the limit: its parts and their pieces of half a
    // millisecond or more, how many crafts were awake in the physics after it and how many were
    // under formula, the tiles in the world and how many it made itself (from the terrain), and
    // anything else the scene knows of it (<paramref name="more"/>), so its cause is named.
    private static string Where(StepSample step, Simulation sim, long made, string more)
    {
        var (awake, formula) = Counts(sim);
        return Where(step, awake, formula, made, more);
    }

    /// <summary><see cref="Where(StepSample, Simulation, long, string)"/>, from counts taken as the frame ended (see <see cref="Counts"/>): for a paced loop, which words it only once it is over.</summary>
    private static string Where(StepSample step, int awake, int formula, long made, string more)
    {
        var parts = Enum.GetValues<StepPart>().Where(p => step[p] >= 0.5).Select(p => $"{p} {step[p]:0.0}");
        var details = Enum.GetValues<StepDetail>().Where(d => step[d] >= 0.5).Select(d => $"{d} {step[d]:0.0}");
        return $"{string.Join(", ", parts.Concat(details))}; {awake} awake, {formula} under formula, {step.Tiles} tiles, {made} made in the step{more}";
    }

    /// <summary>How many crafts are awake in the physics and how many under formula: counted by index, allocating nothing (see the paced crowd's loop).</summary>
    private static (int Awake, int Formula) Counts(Simulation sim)
    {
        var (awake, formula, world) = (0, 0, sim.Flight.WorldVessels);
        for (var i = 0; i < world.Count; i++)
        {
            var v = world[i];
            if (v.OnFormula)
                formula++;
            else if (v.State != FlightState.Crashed && !v.Craft.Frozen)
                awake++;
        }
        return (awake, formula);
    }

    /// <summary>
    /// One case of the paced crowd (<see cref="Crowd"/>): its frames' physics (median, 95th
    /// percentile and worst, ms), how many vessels came into the physics and left it over them, the
    /// most in it at once, the shares of the step that the rules and, within them, the formula (its
    /// motion and its collisions) and the predictions (<see cref="StepDetail.Collisions"/>, the near
    /// misses' included) took, the map's own frame (its <see cref="Paths.Build"/>, ms, when
    /// open), and each frame whose physics was over <see cref="ShortPerf.PlayableFrameMs"/>, and
    /// the worst frame (each with where its time went, its handovers and the garbage collector's
    /// collections through it).
    /// </summary>
    internal sealed record PacedCrowd(string Name, int Frames, double MedianMs, double P95Ms, double MaxMs, int In, int Out, int MostInPhysics,
        double RulesShare, double FormulaShare, double CollisionShare, double MapP95Ms, double MapMaxMs, string Ended, IReadOnlyList<(int Frame, double Ms, string Where)> Over,
        (int Frame, double Ms, string Where) Worst);

    /// <summary>The flown vessel's height in the crowd (<see cref="CrowdFlownPast"/>): halfway between the satellites preset's two rings in the launch's plane, 9 and 10 km up.</summary>
    private const float FlownPastAltitude = 9500f;

    /// <summary>
    /// The satellites preset copied round the planet 250 times (<see cref="WorldTests.ManySatellites"/>:
    /// a thousand satellites, each of the launch's plane's two rings 250 satellites some 500 m
    /// apart), the last of them made the one flown: a satellite going round the other way, between
    /// those two rings, 500 m from each, so it passes a satellite of each about every 0.4 s, closing
    /// at some 590 m/s (and the tilted rings' where they cross its way). Each one ahead comes into
    /// the physics some 3 km out (the handover's reach at that closing speed) and leaves it once
    /// behind, so the crowd is handed in and out all the while. Last, so the room made for the
    /// flight's own rocket on the pad is made from the first satellite, not it.
    /// </summary>
    internal static WorldState CrowdFlownPast()
    {
        var world = WorldTests.ManySatellites(250);
        var vessels = world.Vessels.ToList();
        var sat = vessels[^1];
        var comHeight = RocketGeometry.CentreOfMassHeight(sat.Vehicle, sat.First, sat.Last);
        // Between two of the rings' satellites (they are 1.44° apart, from 60° round), going backwards.
        var (position, centre, velocity) = WorldPresets.CircularOrbit(FlownPastAltitude, 0f, float.DegreesToRadians(65f), comHeight);
        var up = Planet.Up(centre);
        vessels[^1] = sat with
        {
            Position = position,
            Velocity = Planet.OverGround(centre, -Planet.AmongStars(centre, velocity)),
            Rotation = WorldPresets.RotationTo(up),
            Aim = up,
            MaxAltitude = FlownPastAltitude,
            MaxGround = FlownPastAltitude,
        };
        return world with { Vessels = vessels };
    }

    /// <summary>
    /// The paced crowd: the flown vessel flown past a thousand satellites
    /// (<see cref="CrowdFlownPast"/>), paced at real time as the fast collisions are, each frame's
    /// physics timed while the crowd is handed into the physics and out of it (at most
    /// <see cref="Scene.HandoversInPerStep"/> in a step). Twice: with the map closed, and open,
    /// the map's collision search working on its thread beside the step throughout (and the map's
    /// own frame timed); and a third time with the map closed and near misses slowed for
    /// (<see cref="FlightInput.NearMisses"/> on), as then each step also looks for the flown
    /// vessel's nearest miss over the whole world (<see cref="Collisions.NearestMiss"/>), whose
    /// looser reach sends satellites of the crowd to the same chorded march the collisions'
    /// prediction runs. Each first flies <see cref="CrowdWarmFrames"/> frames untimed: the
    /// world's first step puts the crowd out of the physics at once, and the JIT's first calls are
    /// not what is held.
    /// </summary>
    public static List<PacedCrowd> Crowd()
    {
        var cases = new List<PacedCrowd>();
        using (FastCores.Hold())
        {
            try
            {
                cases.Add(Fly("flown past a thousand satellites, the map closed", map: false));
                cases.Add(Fly("flown past a thousand satellites, the map open (its search at work)", map: true));
                cases.Add(Fly("flown past a thousand satellites, the map closed, near misses on", map: false, nearMisses: true));
            }
            finally
            {
                Timings.Enabled = false;
            }
        }
        return cases;

        PacedCrowd Fly(string name, bool map, bool nearMisses = false)
        {
            var input = new FlightInput(NearMisses: nearMisses);
            var world = CrowdFlownPast();
            var flownId = world.Vessels[^1].Id;
            using var sim = new Simulation(world: world);
            var flight = sim.Flight;
            var flown = flight.WorldVessels.Single(v => v.Id == flownId);
            sim.Step(new FlightInput(SwitchTo: flown, NearMisses: nearMisses));
            var passBefore = flight.CollisionPass;
            var (paths, plan) = (map ? new Paths() : null, new ManoeuvrePlan());
            var mapFrames = new List<double>();
            var frame = TimeSpan.FromSeconds(Simulation.FrameSeconds);
            var clock = Stopwatch.StartNew();
            var was = new bool[flight.WorldVessels.Count];
            var (ins, outs, most) = (0, 0, 0);
            var steps = new List<StepSample>(CrowdFrames);
            // Each frame over the limit, and the worst, kept as numbers in the loop (worded once it
            // is over: words and the LINQ that finds them make garbage a later timed frame would pay for).
            var over = new List<Noted>(CrowdFrames);
            var worst = default(Noted);
            for (var frames = 0; frames < CrowdWarmFrames + CrowdFrames; frames++)
            {
                var timed = frames >= CrowdWarmFrames;
                if (frames == CrowdWarmFrames)
                {
                    Timings.Enabled = true;
                    for (var i = 0; i < was.Length; i++)
                        was[i] = flight.WorldVessels[i].OnFormula;
                }
                var began = clock.Elapsed;
                var collections = GC.CollectionCount(0); // the garbage collector's, through the step (any thread's)
                sim.Step(input);
                if (paths is not null)
                {
                    // The map's frame, as the game draws it zoomed out: it asks the search for passes.
                    var start = Stopwatch.GetTimestamp();
                    plan.Update(GroundBody.Planet, flight.CentreOfMass, Planet.AmongStars(flight.CentreOfMass, flight.Velocity), flight.WorldTime, [], null);
                    paths.Build(flight, plan);
                    if (timed)
                        mapFrames.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                if (timed)
                {
                    var step = Timings.Steps[^1];
                    steps.Add(step);
                    var (cameIn, wentOut) = (0, 0);
                    for (var i = 0; i < was.Length; i++)
                    {
                        var now = flight.WorldVessels[i].OnFormula;
                        if (now && !was[i])
                            wentOut++;
                        else if (!now && was[i])
                            cameIn++;
                        was[i] = now;
                    }
                    (ins, outs, most) = (ins + cameIn, outs + wentOut, Math.Max(most, flight.Unwatched.InPhysics));
                    var gcs = GC.CollectionCount(0) - collections;
                    if (step.TotalMs >= ShortPerf.PlayableFrameMs || step.TotalMs > worst.Step.TotalMs)
                    {
                        var (awake, formula) = Counts(sim);
                        var at = new Noted(frames - CrowdWarmFrames + 1, step, awake, formula, cameIn, wentOut, gcs);
                        if (step.TotalMs >= ShortPerf.PlayableFrameMs)
                            over.Add(at);
                        if (step.TotalMs > worst.Step.TotalMs)
                            worst = at;
                    }
                }
                WaitOut(clock, began + frame);
            }
            Timings.Enabled = false;
            Assert.Equal(was.Length, flight.WorldVessels.Count); // none gone: the counts are by place in the list
            // With the map open, its search really was at work (passes taken), so this case times
            // more than the map closed does.
            if (map)
                Assert.True(flight.CollisionPass > passBefore, "the map's search took no pass");
            var (median, p95, _) = ShortPerf.Percentiles(steps.Select(s => s.TotalMs));
            var total = steps.Sum(s => s.TotalMs);
            mapFrames.Sort();
            return new PacedCrowd(name, steps.Count, median, p95, steps.Max(s => s.TotalMs), ins, outs, most,
                steps.Sum(s => s[StepPart.Rules]) / total, steps.Sum(s => s[StepDetail.Formula] + s[StepDetail.FormulaCollisions]) / total,
                steps.Sum(s => s[StepDetail.Collisions]) / total,
                mapFrames.Count > 0 ? mapFrames[(int)(mapFrames.Count * 0.95)] : 0, mapFrames.Count > 0 ? mapFrames[^1] : 0,
                $"{flown.State} {flown.CrashReason}".Trim() + $"; {flight.WorldVessels.Count(v => v.State == FlightState.Crashed)} wrecked",
                [.. over.Select(Worded)], Worded(worst));
        }

        static (int Frame, double Ms, string Where) Worded(Noted n) =>
            (n.Frame, n.Step.TotalMs, Where(n.Step, n.Awake, n.Formula, 0, $", {n.In} came in and {n.Out} went out, {n.Collections} collections"));
    }

    /// <summary>A paced crowd's frame noted in its loop (see <see cref="Crowd"/>): its number, its step, and what was in and out of the physics through it.</summary>
    private readonly record struct Noted(int Frame, StepSample Step, int Awake, int Formula, int In, int Out, int Collections);

    /// <summary>
    /// The paced crowd's frames: untimed first (see <see cref="Crowd"/>), then timed (20 s of real
    /// time). The untimed are 12 s: the collisions under formula are checked four times a game
    /// second, so the JIT takes some 8 s to tier them up, and the frame it swaps the checks in
    /// costs 3 to 5 ms of its own once a process (with 3 s untimed, it came in the timed frames).
    /// </summary>
    private const int CrowdWarmFrames = 720, CrowdFrames = 1200;

    // Searched for once: the search is the same every time, and isn't what is timed.
    private static readonly Lazy<Vector3> RoughSlope = new(() => Drops.FindSlope(15f, 35f).At);

    /// <summary>
    /// <paramref name="steps"/> steps of <paramref name="sim"/>, with no input, timed: the timings
    /// turned on afresh for them and off after (<paramref name="each"/> is called after every step).
    /// </summary>
    private static SceneRun Time(Simulation sim, int steps, Action? each = null)
    {
        Assert.True(steps <= Timings.StepCapacity, "more steps than the timings keep");
        Timings.Enabled = true;
        try
        {
            for (var i = 0; i < steps; i++)
            {
                sim.Step(default);
                CoreKinds.Note();
                each?.Invoke();
            }
            var timed = Timings.Steps.ToArray();
            Assert.Equal(steps, timed.Length);
            return new SceneRun(timed);
        }
        finally
        {
            Timings.Enabled = false;
        }
    }
}
