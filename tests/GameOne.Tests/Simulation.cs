using System.Diagnostics;
using GameOne.Physics;

namespace GameOne.Tests;

/// <summary>A pilot: decides each frame's input from how the flight is going, given the frame's length.</summary>
internal delegate FlightInput Pilot(Flight flight, float dt);

/// <summary>
/// The game's flight without the game: the real <see cref="Flight"/> rules over the real Jolt
/// <see cref="RocketPhysics"/>, stepped in fixed frames as fast as they will go. No window,
/// rendering or audio, so a two-minute mission takes well under a second, and every run is the same.
/// Set <c>GAMEONE_WATCH=1</c> to watch it instead, in the game's window in real time (see <see cref="Watcher"/>).
/// </summary>
/// <remarks>
/// Each body in use has its physics, as in the game (<see cref="Worlds"/>): each vessel is at the
/// body its state names (<see cref="VesselState.Body"/>), in that body's frame. <see cref="Physics"/>
/// is the one body's a test is about (the planet, unless it asks for another). Jolt's process-wide
/// state is set up once for the process and every world shares it (<see cref="JoltShared"/>), so
/// simulations can exist side by side, each stepped in turn on the test's thread. The tests don't
/// run in parallel (see AssemblyInfo.cs).
/// </remarks>
internal sealed class Simulation : IDisposable
{
    /// <summary>The frame length the game would see at 60 frames a second.</summary>
    public const float FrameSeconds = 1f / 60f;

    private readonly Worlds _worlds = new();
    private readonly GroundBody _body;
    private readonly Watcher? _watcher;

    public Flight Flight { get; }

    /// <summary>Seconds of flight simulated so far: game time, which runs slower or faster than the frames at another <see cref="Flight.TimeScale"/>.</summary>
    public double Time { get; private set; }

    /// <summary>How long the simulating has taken, in real time.</summary>
    public TimeSpan WallTime { get; private set; }

    /// <summary>
    /// The physics at the body the test is about (the planet unless it asked for another), for the
    /// tests to look into: there while anything is in it there (the planet's always, from the new
    /// rocket on its pad).
    /// </summary>
    public RocketPhysics Physics => Flight.PhysicsAt(_body) ?? throw new InvalidOperationException($"{_body.Name} has no physics now");

    /// <summary>The physics at every body in use.</summary>
    public Worlds Worlds => _worlds;

    /// <param name="mission">The mission to fly: the landing one if not given.</param>
    /// <param name="watcher">What shows it, if anything: by default, a <see cref="Watcher"/> if <c>GAMEONE_WATCH</c> asks for one.</param>
    /// <param name="world">The world to fly it in: by default, an empty one.</param>
    /// <param name="site">Where to launch from: Home, by default.</param>
    /// <param name="body">The body the test is about, whose physics <see cref="Physics"/> is: the planet, by default.</param>
    public Simulation(Mission? mission = null, Watcher? watcher = null, WorldState? world = null, LaunchSite? site = null, GroundBody? body = null)
    {
        mission ??= Mission.LaunchAndLand;
        _watcher = watcher ?? Watcher.FromEnvironment(mission);
        _body = body ?? GroundBody.Planet;
        Flight = new Flight(_worlds, mission, world, site);
    }

    /// <summary>
    /// Flies with <paramref name="pilot"/>, a real frame at a time as the game does
    /// (<see cref="Flight.RunFrame"/>), until <paramref name="until"/> is true or
    /// <paramref name="maxSeconds"/> of flight (game time) have passed. Returns whether <paramref name="until"/> came true.
    /// </summary>
    public bool Run(Pilot pilot, Func<Flight, bool> until, double maxSeconds = 300)
    {
        var clock = Stopwatch.StartNew();
        var end = Time + maxSeconds;
        var done = until(Flight);
        while (!done && Time < end)
        {
            // The pilot is told the frame's game time, as its rules (a throttle's rate, a hold's
            // gain) run on the game's clock: at a rate other than 1 that isn't the real frame.
            var input = pilot(Flight, FrameSeconds * Flight.TimeScale);
            var dt = Flight.RunFrame(FrameSeconds, input);
            Time += dt;
            _watcher?.Show(Flight, dt, FrameSeconds);
            done = until(Flight);
        }
        WallTime += clock.Elapsed;
        return done;
    }

    /// <summary>Runs until the flight is over: landed, floating or crashed.</summary>
    public bool RunToEnd(Pilot pilot, double maxSeconds = 300) =>
        Run(pilot, f => f.State is FlightState.Landed or FlightState.Floating or FlightState.Crashed, maxSeconds);

    /// <summary>Gives <paramref name="vessel"/> a small view in the window, if the test is watched (the flight's own small views are its input's <see cref="FlightInput.Watched"/>).</summary>
    public void OpenView(Vessel vessel) => _watcher?.Open(vessel);

    /// <summary>One real frame with this input (<see cref="Flight.RunFrame"/>).</summary>
    public void Step(FlightInput input)
    {
        var dt = Flight.RunFrame(FrameSeconds, input);
        Time += dt;
        _watcher?.Show(Flight, dt, FrameSeconds);
    }

    /// <summary>
    /// This simulation's world, saved to a file (in a folder of its own, as the game saves one) and
    /// loaded into a fresh simulation for the same body, which starts a new flight of the same
    /// mission in it. This one is closed first; disposing it again does nothing.
    /// </summary>
    public Simulation Reloaded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorldStore(directory);
            store.Save(Flight.Capture() with { Id = "reloaded", Name = "Reloaded" });
            var (mission, body) = (Flight.Mission, _body);
            Dispose();
            return new Simulation(mission, world: store.Load("reloaded"), body: body);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watcher?.Dispose(); // first: it shows the flight to the end
        Flight.Dispose(); // its map's collision search, so no thread is left running after a test
        _worlds.Dispose();
    }
}
