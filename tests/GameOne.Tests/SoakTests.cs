using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Soak tests: long stretches of play, compressed, to catch anything that grows the longer the game
/// runs (memory, OS handles, physics bodies). Leaks come from things happening, not from the clock,
/// so hours of play can be flown in minutes as long as the same things keep happening: here, every
/// mission flown over and over, landed, splashed down, put into orbit, crashed, staged and restarted.
/// </summary>
/// <remarks>
/// <para>They take minutes, so they only run when asked: <c>GAMEONE_SOAK=1</c> (see README.md).</para>
/// <list type="bullet">
///   <item><b>Headless:</b> the flight rules and Jolt physics, at full speed (~80x real time).
///   <c>GAMEONE_SOAK_MINUTES</c> sets how many minutes of play (default 120).</item>
///   <item><b>Rendered:</b> the same, drawn through the game's own view (renderer, HUD, effects,
///   audio) with vsync off, as fast as frames can be drawn, a frame every 4 steps as 4x time warp
///   would (so the flights come round sooner), and small views coming and going all through it.
///   <c>GAMEONE_SOAK_FRAMES</c> sets how many frames (default 36,000: forty minutes of play).</item>
/// </list>
/// <para>Memory is sampled as it goes, after a full garbage collection: the managed heap, the
/// process's private memory, its OS handles, and the bodies in the Jolt world. After a warm-up, each
/// must level off: the last third of the samples may sit no higher than the first third by more
/// than a small allowance. Each test prints the samples, to see the trend.</para>
/// </remarks>
public sealed class SoakTests(ITestOutputHelper output)
{
    [SoakFact]
    public void Hours_of_headless_play_leak_nothing()
    {
        var minutes = Setting("GAMEONE_SOAK_MINUTES", 120);
        using var sim = new Simulation(Mission.LaunchAndLand);
        var soak = new Soak(sim, output);

        soak.Run(until: () => sim.Time >= minutes * 60, sampleEvery: () => sim.Time >= soak.NextSampleTime);
        soak.Report($"{minutes:0} minutes of play, headless");

        soak.AssertLevel(managedMb: 2, privateMb: 48, handles: 50);
    }

    [SoakFact]
    public void Hours_of_play_into_one_world_saved_and_loaded_after_every_flight_leak_nothing()
    {
        // Mission after mission into the same world, which fills up with what they leave (landed
        // boosters, stages in orbit, satellites), saved and loaded back after every flight as if
        // the game were closed and opened again; every few flights it is started over. Memory is
        // sampled each time it starts over: it should come back to the same each time.
        var minutes = Setting("GAMEONE_SOAK_MINUTES", 120);
        var directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", "soak-" + Guid.NewGuid().ToString("N"));
        try
        {
            var playlist = new Playlist { OneWorld = true, Store = new WorldStore(directory) };
            using var sim = new Simulation(Mission.LaunchAndLand);
            var soak = new Soak(sim, output, playlist: playlist);
            var sampled = 0;
            soak.Run(until: () => sim.Time >= minutes * 60, sampleEvery: () =>
            {
                if (playlist.Emptied == sampled)
                    return false;
                sampled = playlist.Emptied;
                return true;
            });
            soak.Report($"{minutes:0} minutes of play into one world, saved and loaded after every flight " +
                $"(up to {playlist.MostVessels} vessels in it; started over {playlist.Emptied} times)");

            Assert.True(playlist.MostVessels >= 5, $"the world only ever held {playlist.MostVessels} vessels: it didn't fill up");
            soak.AssertLevel(managedMb: 2, privateMb: 48, handles: 50);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [SoakFact]
    public void Forty_minutes_of_rendered_play_with_views_coming_and_going_leaks_nothing()
    {
        // Everything drawn (the renderer, HUD, effects and audio), with small views coming and
        // going all through it. Into one world, too (started over every few flights), so the HUD's
        // lists of a world with more in it than this flight are drawn all through it, and there is
        // more to give views to.
        var frames = (long)Setting("GAMEONE_SOAK_FRAMES", 36_000);
        using var watcher = Watcher.ForSoak(Mission.LaunchAndLand);
        using var sim = new Simulation(Mission.LaunchAndLand, watcher);
        var soak = new Soak(sim, output, watcher, new Playlist { OneWorld = true });

        soak.Run(until: () => watcher.FramesDrawn >= frames || watcher.Failure is not null,
            sampleEvery: () => watcher.FramesDrawn >= soak.NextSampleFrame);
        soak.Report($"{watcher.FramesDrawn:N0} frames drawn ({watcher.PresentMode}), " +
            $"{watcher.FramesDrawn / Math.Max(watcher.WallSeconds, 1e-3):0} fps; small views open in " +
            $"{watcher.FramesWithViews * 100.0 / Math.Max(watcher.FramesDrawn, 1):0}% of them, up to {watcher.MostViews} at once");

        Assert.True(watcher.Failure is null, $"drawing failed after {watcher.FramesDrawn:N0} frames: {watcher.Failure}");
        Assert.True(watcher.FramesDrawn >= frames, $"only {watcher.FramesDrawn:N0} frames were drawn (was the window closed?)");
        Assert.True(watcher.MostViews >= 2, $"at most {watcher.MostViews} small views were ever open: the soak didn't exercise them");
        // As tight as headless, as it draws a third of the frames it once did (108,000, a frame
        // a step), so a leak that grows with each frame still shows: over those, nothing grew by
        // more than 2 MB.
        soak.AssertLevel(managedMb: 2, privateMb: 48, handles: 50);
    }

    private static double Setting(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : fallback;
}

/// <summary>A <see cref="FactAttribute"/> that only runs with <c>GAMEONE_SOAK</c> set: soak tests take minutes.</summary>
public sealed class SoakFactAttribute : FactAttribute
{
    public SoakFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GAMEONE_SOAK") is null or "" or "0")
            Skip = "A soak test, minutes long: set GAMEONE_SOAK=1 to run it (see README.md).";
    }
}

/// <summary>One memory sample during a soak.</summary>
internal readonly record struct SoakSample(double GameSeconds, double WallSeconds, long Frames, int Flights,
    double ManagedMb, double PrivateMb, int Handles, int Bodies, int Tiles);

/// <summary>
/// Flies a soak: the playlist of flights, over and over, sampling memory as it goes, then judges
/// whether anything kept growing.
/// </summary>
internal sealed class Soak
{
    private const double SampleSeconds = 120;   // headless: a sample every this much play
    private const long SampleFrames = 1_500;     // rendered: and every this many frames
    private const double WarmUpShare = 0.2;      // the first fifth of the samples are warm-up, not judged

    private readonly Simulation _sim;
    private readonly ITestOutputHelper _output;
    private readonly Watcher? _watcher;
    private readonly List<SoakSample> _samples = [];
    private readonly HashSet<int> _bodiesOnThePad = [];
    private int _mostTiles;
    private readonly List<int> _tilesOnThePad = [];
    private double? _countTilesAt;

    /// <summary>
    /// The most tiles there should be round a fresh rocket on the pad, alone in its world, once the
    /// last flight's have had time to go: the pad's own handful (about a dozen).
    /// </summary>
    private const int PadTiles = 30;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly Playlist _playlist;

    public double NextSampleTime { get; private set; }
    public long NextSampleFrame { get; private set; }

    /// <param name="playlist">What to fly: by default, the course, each flight in a fresh, empty world.</param>
    public Soak(Simulation sim, ITestOutputHelper output, Watcher? watcher = null, Playlist? playlist = null)
    {
        (_sim, _output, _watcher, _playlist) = (sim, output, watcher, playlist ?? new Playlist());
        // Every fresh rocket on the pad of an empty world should leave the Jolt world exactly as
        // big as the last, the ground's tiles aside (they come and go with where things are).
        // (In a world with other things in it, the count depends on them.)
        sim.Flight.Restarted += () =>
        {
            if (sim.Flight.WorldVessels.Count == 1)
            {
                _bodiesOnThePad.Add(sim.Physics.BodyCount - sim.Physics.TileCount);
                // Its ground, once the last flight's tiles should have gone (and before it has
                // left the pad: the playlist waits there that long, see Playlist.Fly).
                _countTilesAt = sim.Time + Scene.TerrainTileLingerSeconds + 0.5;
            }
        };
    }

    /// <summary>Flies the playlist until <paramref name="until"/>, sampling whenever <paramref name="sampleEvery"/> says it's due.</summary>
    public void Run(Func<bool> until, Func<bool> sampleEvery)
    {
        Sample();
        _sim.Run(_playlist.Fly, _ =>
        {
            _mostTiles = Math.Max(_mostTiles, _sim.Physics.TileCount);
            if (_countTilesAt is { } at && _sim.Time >= at)
            {
                if (_sim.Flight.WorldVessels.Count == 1)
                    _tilesOnThePad.Add(_sim.Physics.TileCount);
                _countTilesAt = null;
            }
            if (sampleEvery())
                Sample();
            return until();
        }, maxSeconds: double.MaxValue);
        Sample();
    }

    private void Sample()
    {
        // A full collection first, so only what is still referenced counts.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        _samples.Add(new SoakSample(
            _sim.Time,
            _wall.Elapsed.TotalSeconds,
            _watcher?.FramesDrawn ?? 0,
            _playlist.Flights,
            GC.GetTotalMemory(forceFullCollection: true) / 1048576.0,
            process.PrivateMemorySize64 / 1048576.0,
            process.HandleCount,
            _sim.Physics.BodyCount,
            _sim.Physics.TileCount));
        NextSampleTime = _sim.Time + SampleSeconds;
        NextSampleFrame = (_watcher?.FramesDrawn ?? 0) + SampleFrames;
    }

    /// <summary>Prints every sample, and how much each measure grew over the judged part of the run.</summary>
    public void Report(string what)
    {
        var last = _samples[^1];
        _output.WriteLine($"Soak: {what}. {last.Flights} flights, {last.GameSeconds / 60:0} minutes of play in {last.WallSeconds:0} s " +
            $"({last.GameSeconds / Math.Max(last.WallSeconds, 1e-3):0}x real time).");
        _output.WriteLine($"Pad body counts seen: {string.Join(", ", _bodiesOnThePad)}");
        _output.WriteLine("  play     wall    frames  flights  managed MB  private MB  handles  bodies  tiles");
        foreach (var s in _samples)
            _output.WriteLine($"{s.GameSeconds / 60,5:0} m {s.WallSeconds,6:0} s {s.Frames,9} {s.Flights,8} {s.ManagedMb,11:0.00} {s.PrivateMb,11:0.0} {s.Handles,8} {s.Bodies,7} {s.Tiles,6}");
        _output.WriteLine($"Most tiles at once: {_mostTiles} (at most {Scene.MaxTerrainTiles}); round a fresh rocket on the pad: {string.Join(", ", _tilesOnThePad.Distinct())}");
        var (early, late) = Thirds();
        _output.WriteLine($"Growth, judged part (median of the last third less the first): managed {Median(late, s => s.ManagedMb) - Median(early, s => s.ManagedMb):+0.00;-0.00} MB, " +
            $"private {Median(late, s => s.PrivateMb) - Median(early, s => s.PrivateMb):+0.0;-0.0} MB, " +
            $"handles {Median(late, s => s.Handles) - Median(early, s => s.Handles):+0;-0}");
    }

    /// <summary>
    /// Fails if anything kept growing: the last third of the judged samples may sit no more than
    /// the allowance above the first third (by their medians, so one noisy sample doesn't decide
    /// it), and every fresh rocket must leave the same number of bodies in the physics world.
    /// </summary>
    public void AssertLevel(double managedMb, double privateMb, double handles)
    {
        Assert.True(_samples.Count >= 6, $"only {_samples.Count} samples: run the soak longer");
        Assert.True(_playlist.Flights >= 8, $"only {_playlist.Flights} flights flown: run the soak longer");
        Assert.True(_bodiesOnThePad.Count == 1, $"the physics world holds a different number of bodies after a restart: {string.Join(", ", _bodiesOnThePad)}");
        // The ground's tiles are let go: once a flight is over and the world emptied, they come
        // back down to the handful round the fresh rocket on the pad, every time. (Tiles left
        // behind by any flight would pile up here.)
        if (_tilesOnThePad.Count > 0)
            Assert.True(_tilesOnThePad.Max() <= PadTiles,
                $"round a fresh rocket on the pad, alone in its world: {string.Join(", ", _tilesOnThePad.Distinct())} tiles (at most {PadTiles}): tiles left behind?");
        var (early, late) = Thirds();
        Grows("managed memory (MB)", s => s.ManagedMb, managedMb);
        Grows("private memory (MB)", s => s.PrivateMb, privateMb);
        Grows("OS handles", s => s.Handles, handles);

        void Grows(string what, Func<SoakSample, double> measure, double allowance)
        {
            var growth = Median(late, measure) - Median(early, measure);
            Assert.True(growth <= allowance, $"{what} grew by {growth:0.##} over the run (allowed {allowance}): a leak?");
        }
    }

    private (SoakSample[] Early, SoakSample[] Late) Thirds()
    {
        var judged = _samples.Skip((int)Math.Ceiling(_samples.Count * WarmUpShare)).ToArray();
        var third = Math.Max(1, judged.Length / 3);
        return (judged[..third], judged[^third..]);
    }

    private static double Median(SoakSample[] samples, Func<SoakSample, double> measure)
    {
        var values = samples.Select(measure).Order().ToArray();
        return values.Length == 0 ? 0 : values[values.Length / 2];
    }
}

/// <summary>
/// What a soak flies: every mission, done right and done wrong, over and over, so everything the
/// game does (launches, landings, splashdowns, orbits, staging, flying two vessels, debris, crashes, restarts)
/// keeps happening. Each flight ends after a few seconds of being over, or after a time limit, and
/// the next is chosen.
/// </summary>
internal sealed class Playlist
{
    private const double SettleSeconds = 3;    // over (landed, afloat, crashed, in orbit), it stays a moment so the effects play out
    private const double PadSeconds = Scene.TerrainTileLingerSeconds + 1.0;
    private const double MaxFlightSeconds = 900; // the satellite mission (some 790 s): the upper stage waits most of a lap to come home, then the satellite climbs

    /// <summary>
    /// Off the landing zone, 60 m out along each way across the pad's plane: on the level ground
    /// round it (within 2 degrees there), clear of its masts on the diagonals and of one another. Set
    /// before <see cref="Course"/> (statics are set in the order written), so it is set before
    /// anything there reads it.
    /// </summary>
    private static readonly Vector3[] ZoneNeighbours =
    [
        Scene.LandingZoneCentre + new Vector3(60f, 0f, 0f),
        Scene.LandingZoneCentre + new Vector3(0f, 0f, 60f),
        Scene.LandingZoneCentre + new Vector3(0f, 0f, -60f),
        Scene.LandingZoneCentre + new Vector3(-60f, 0f, 0f),
    ];

    // Each pilot is made on the pad of the world it flies in, so a landing aimed at a site that a
    // vessel from an earlier flight still stands on (a booster on the zone or the drone ship) is
    // flown beside it instead, as a player would: never down on top of it.
    private static readonly (string Name, Mission Mission, Func<Flight, Pilot> Pilot, Func<Flight, bool> Over)[] Course =
    [
        ("land on target (beside it, if taken)", Mission.LaunchAndLand, f => new Autopilot { Target = BesideTheZone(f) }.Fly, Ended),
        // Offshore, near enough to the pad that it floats there, awake, through the flights after it in the same world.
        ("splash down offshore", Mission.LaunchAndLand, _ => new Autopilot { Target = SplashdownTests.Offshore.Value }.Fly, Ended),
        ("reach orbit", Mission.ReachOrbit, _ => new OrbitAutopilot().Fly, f => f.AchievedOrbit || Ended(f)),
        ("land on the bell too fast (crash)", Mission.LaunchAndLand,
            f => new Autopilot { DeployLegs = false, TouchdownSpeed = 4f, Target = BesideTheZone(f) }.Fly, Ended),
        ("orbit without staging (falls back)", Mission.ReachOrbit, _ => new OrbitAutopilot { Stage = false }.Fly, Ended),
        ("orbit, and land the booster downrange", Mission.ReachOrbit, f => new OrbitAndReturnPilot(BesideTheShip(f)).Fly,
            f => f.Vessels.Count > 1 && f.Vessels.All(v => v.State != FlightState.Flying || v.AchievedOrbit) || Ended(f)),
        ("deploy a satellite, land the rest", Mission.DeploySatellite, f => new SatellitePilot(BesideTheZone(f), BesideTheShip(f)).Fly,
            f => f.Run!.Succeeded && f.Vessels.All(v => v.State != FlightState.Flying || v.Stage == 2) || Ended(f)),
        ("land off target", Mission.LaunchAndLand, f => new Autopilot { Target = Clear(f, ZoneNeighbours) }.Fly, Ended),
    ];

    /// <summary>How far a landing keeps from anything already standing where it comes down (on the ground: a toppled booster's length and more).</summary>
    private const float GroundClearance = 30f;

    /// <summary>How far past the drone ship's deck a vessel still counts as in the way of a landing on it (a booster's legs reach some 5 m out).</summary>
    private const float DeckClearance = 13f;

    /// <summary>The landing zone, or beside it if a vessel from an earlier flight stands on it.</summary>
    private static Vector3 BesideTheZone(Flight f) => Clear(f, [Scene.LandingZoneCentre, .. ZoneNeighbours]);

    /// <summary>
    /// The drone ship's middle, or, with a vessel from an earlier flight on its deck, the end of
    /// the deck furthest from it (the deck's length runs east, 26 m either side of the middle).
    /// </summary>
    private static Vector3? BesideTheShip(Flight f)
    {
        var centre = Scene.DroneShipCentre;
        var east = Planet.East(centre);
        var ends = new[] { centre + east * 15f, centre - east * 15f };
        var others = Others(f, centre, Scene.DroneShipHalfLength + DeckClearance).ToArray();
        if (others.Length == 0)
            return null; // the mission's own landing site: the ship's middle
        return ends.MaxBy(end => others.Min(v => Vector3.Distance(v.Position, end)));
    }

    /// <summary>The first of <paramref name="spots"/> with no vessel from an earlier flight within <see cref="GroundClearance"/> of it.</summary>
    private static Vector3 Clear(Flight f, Vector3[] spots)
    {
        foreach (var spot in spots)
            if (!Others(f, spot, GroundClearance).Any())
                return spot;
        throw new InvalidOperationException($"every spot by the landing zone is taken: {string.Join(", ", spots)}");
    }

    /// <summary>The planet's vessels within <paramref name="reach"/> of <paramref name="place"/>, but this flight's own.</summary>
    private static IEnumerable<Vessel> Others(Flight f, Vector3 place, float reach) =>
        f.WorldVessels.Where(v => v.Body == GroundBody.Planet && !f.Vessels.Contains(v) && Vector3.Distance(v.Position, place) < reach);

    private int _next;
    private Func<Flight, Pilot>? _make;
    private Pilot? _pilot;
    private Func<Flight, bool> _over = Ended;
    private double _flying, _overFor;

    /// <summary>How many flights have been started.</summary>
    public int Flights { get; private set; }

    private static bool Ended(Flight f) => f.State is FlightState.Landed or FlightState.Floating or FlightState.Crashed;

    public FlightInput Fly(Flight flight, float dt)
    {
        _flying += dt;
        if (_pilot is not null && _over(flight))
            _overFor += dt;
        if (_make is null || _overFor >= SettleSeconds || _flying >= MaxFlightSeconds)
            return Next(flight);
        // A moment on the pad first (the engine lights the moment Space is pressed), for the pad's
        // ground to be counted with the rocket still on it (see Soak).
        if (flight.State == FlightState.Ready && _flying < PadSeconds)
            return default;
        // Made in the world it flies in, with this flight's rocket on the pad, so it sees what is left there.
        _pilot ??= _make(flight);
        return _pilot(flight, dt);
    }

    /// <summary>
    /// With <see cref="OneWorld"/>, flights are flown into the same world, which fills up as they
    /// go, until it is started over every <see cref="FlightsPerWorld"/> flights; otherwise each
    /// starts in a fresh, empty world, so the world stays the same size.
    /// </summary>
    public bool OneWorld { get; init; }

    /// <summary>With <see cref="OneWorld"/>: how many flights fill the world before it is started over, empty.</summary>
    public int FlightsPerWorld { get; init; } = 6;

    /// <summary>
    /// With <see cref="OneWorld"/>, where the world is saved after every flight and loaded back
    /// from, as if the game had been closed and opened again; without one, the next mission is
    /// just chosen, as from the menu.
    /// </summary>
    public WorldStore? Store { get; init; }

    /// <summary>How many times the world has been started over (with <see cref="OneWorld"/>).</summary>
    public int Emptied { get; private set; }

    /// <summary>The most vessels the world has held, at the start of a flight.</summary>
    public int MostVessels { get; private set; }

    /// <summary>Starts the next flight on the list: a fresh rocket on the pad.</summary>
    private FlightInput Next(Flight flight)
    {
        var (_, mission, pilot, over) = Course[_next];
        _next = (_next + 1) % Course.Length;
        _make = pilot;
        _pilot = null;
        _over = over;
        _flying = _overFor = 0;
        Flights++;
        MostVessels = Math.Max(MostVessels, flight.WorldVessels.Count);
        if (!OneWorld)
        {
            flight.Load(WorldPresets.Empty(), mission);
            return default;
        }
        if (Flights % FlightsPerWorld == 0)
        {
            Emptied++;
            flight.Load(WorldPresets.Empty() with { Id = "soak" }, mission); // started over
            return default;
        }
        if (Store is { } store)
        {
            store.Save(flight.Capture() with { Id = "soak" });
            flight.Load(store.Load("soak"), mission);
            return default;
        }
        return new FlightInput(Choose: mission); // everything from earlier flights stays
    }
}
