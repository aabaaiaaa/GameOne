using System.Numerics;
using GameOne.Physics;

namespace GameOne;

internal enum FlightState
{
    /// <summary>On the pad, waiting for the player to light the engine.</summary>
    Ready,
    Flying,
    /// <summary>Came down safely and has settled, however it lies: a stop, from which it can fly again.</summary>
    Landed,
    /// <summary>
    /// Came down safely in the sea and is afloat there, clear of the seabed: a safe landing, but not
    /// on any landing site. It still bobs and rolls with the waves, and can still be lost to them.
    /// </summary>
    Floating,
    Crashed,
}

/// <summary>A sound the flight calls for, at a place in the world.</summary>
internal enum SoundCue
{
    /// <summary>The feet (or the bell) touching the ground; strength is how fast, in m/s.</summary>
    Touchdown,
    /// <summary>The legs locking into place, out or in, or the stages coming apart.</summary>
    LegsLocked,
    Explosion,
    /// <summary>Something touching the water, at its surface; strength is how fast, in m/s.</summary>
    Splash,
}

/// <summary>A sound the flight calls for: where (in <paramref name="Body"/>'s frame, the body it was made at), how strong, and what.</summary>
internal readonly record struct SoundEvent(Vector3 Position, float Strength, SoundCue Cue, GroundBody Body);

/// <summary>
/// The forced slow-down (<see cref="Flight.Slowed"/>): the physics' step at <paramref name="Scale"/>
/// of its own, and the game's clock at that or the player's own slow motion, whichever is slower
/// (<see cref="Flight.TimeScale"/>), for a witnessed fast collision, named by
/// <paramref name="Reason"/> (what is coming, and how fast it hits: <paramref name="HitSpeed"/>,
/// as predicted when it became the one slowed for); or, <paramref name="NearMiss"/>, for a near miss
/// with the player's setting on (<see cref="FlightInput.NearMisses"/>), its scale then the near
/// miss's own (<see cref="GameOne.NearMiss.ScaleAt"/>), <paramref name="HitSpeed"/> how fast it passes.
/// </summary>
/// <remarks>
/// A fast collision is slowed as a near miss is: from <see cref="Scene.SlowdownFrom"/> out its
/// speed to the eye eases evenly with its distance (its scale a straight line in game seconds) down
/// to <see cref="Scene.SlowdownPassSpeed"/>, reached <see cref="Scene.SlowdownHoldWithin"/> short
/// of the collision and held to as far past it (a pass's closest approach; for a hit, what of it
/// goes on), and never above <see cref="Least"/>, which keeps each step's travel within
/// <see cref="Scene.FastCollisionStep"/> as it hits so nothing passes through. Once over it eases
/// back over <see cref="Scene.SlowdownEaseSeconds"/> of real time.
/// </remarks>
internal readonly record struct Slowdown(float Scale, float HitSpeed, string Reason, bool NearMiss = false)
{
    /// <summary>The least scale for a collision at <paramref name="hitSpeed"/>: each step's travel then <see cref="Scene.FastCollisionStep"/> (1 for one no faster than that a step).</summary>
    public static float Least(float hitSpeed) =>
        MathF.Min(1f, Scene.FastCollisionStep / (MathF.Max(hitSpeed, 1e-3f) * Physics.RocketPhysics.StepSeconds));

    /// <summary>Its deepest scale, for a collision at <paramref name="hitSpeed"/>: <see cref="Scene.SlowdownPassSpeed"/> to the eye, or <see cref="Least"/> if that is lower.</summary>
    public static float Deepest(float hitSpeed) =>
        MathF.Min(Least(hitSpeed), Scene.SlowdownPassSpeed / MathF.Max(hitSpeed, 1e-3f));

    /// <summary>How many game seconds off a collision at <paramref name="hitSpeed"/> the slowing begins: <see cref="Scene.SlowdownFrom"/> at its speed.</summary>
    public static double Lead(float hitSpeed) => Scene.SlowdownFrom / MathF.Max(hitSpeed, 1e-3f);

    /// <summary>The game seconds before (and after) the collision it is held at its deepest: <see cref="Scene.SlowdownHoldWithin"/> at its speed.</summary>
    public static double Hold(float hitSpeed) =>
        MathF.Min(Scene.SlowdownHoldWithin, Scene.SlowdownFrom / 2f) / MathF.Max(hitSpeed, 1e-3f);

    /// <summary>
    /// The scale for a collision <paramref name="seconds"/> off (negative: since it), at
    /// <paramref name="hitSpeed"/>, two vessels' sizes touching <paramref name="touch"/> game
    /// seconds before it: its slowest reached <see cref="Hold"/> before they touch.
    /// </summary>
    public static float ScaleFor(double seconds, float hitSpeed, double touch = 0.0)
    {
        var s = Deepest(hitSpeed);
        if (s >= 1f)
            return 1f;
        var hold = Hold(hitSpeed);
        var outside = Math.Max(0.0, Math.Abs(seconds) - hold - (seconds >= 0 ? touch : 0.0));
        var k = seconds >= 0 ? (1.0 - s) / (Lead(hitSpeed) - hold) : Math.Log(1.0 / s) / Scene.SlowdownEaseSeconds;
        return (float)Math.Min(1.0, s + k * outside);
    }
}

/// <summary>
/// One thing coming at a player-controlled vessel (<see cref="Flight.ComingAt"/>):
/// <paramref name="Watched"/> (the flown one, or a small view's) hits <paramref name="With"/>
/// (<paramref name="Other"/>, set only for a vessel) in <paramref name="Seconds"/> game seconds, or
/// passes it that close (a near miss); <paramref name="At"/> is where it hits (a near miss: where
/// the other is now, as it has no collision place). <paramref name="NearMiss"/> says which: a near miss
/// misses if nothing is done, so it is shown in amber, not danger's red (<see cref="Hud.ComingColor"/>).
/// <paramref name="Hit"/> is the body it is hit at, in whose frame <paramref name="At"/> is, as it
/// was when it was found (<see cref="Collision.Body"/>: the watched one's own, or one its path
/// crosses into first); null for a near miss, at the watched one's own.
/// </summary>
internal readonly record struct Coming(Vessel Watched, SolidKind With, Vessel? Other, double Seconds, Vector3 At, bool NearMiss = false, GroundBody? Hit = null)
{
    /// <summary>The body it is hit at, in whose frame <see cref="At"/> is: what names it (<see cref="Collisions.Name(GroundBody, SolidKind, Vector3)"/>).</summary>
    public GroundBody Body => Hit ?? Watched.Body;
}

/// <summary>
/// What the pilot is asking for this frame. The game makes one from the keyboard and the mouse's
/// direction rings (see <see cref="Controls"/> and <see cref="NavigationControl"/>); tests make them
/// from an autopilot, so they can fly whole missions without a window, far faster than real time.
/// </summary>
/// <param name="Throttle">-1 to throttle down, +1 up, 0 to hold it; values in between move it more slowly.</param>
/// <param name="Lean">Which way to lean the nose, as a direction in the world; its length (up to 1) is how hard.</param>
/// <param name="Hold">A direction to turn the nose to and hold it at (the direction rings, while dragged).
/// With neither this nor a lean, nothing steers: the air turns the nose along the direction of
/// travel, and above the atmosphere it keeps pointing wherever it was.</param>
/// <param name="Launch">Light the engine of a rocket on the pad.</param>
/// <param name="Stage">Drop the bottom stage, whether or not its fuel has run out; or, before a payload is let go, jettison its fairing.</param>
/// <param name="ToggleLegs">Deploy the legs, or stow them.</param>
/// <param name="FullThrottle">Throttle straight to full.</param>
/// <param name="CutThrottle">Cut the engine.</param>
/// <param name="ToggleProgradeHold">Keep the nose pointed the way the rocket is moving, or stop doing so.</param>
/// <param name="ToggleRetrogradeHold">Keep the nose pointed against the way it is moving (to slow down, as for a landing burn), or stop doing so.</param>
/// <param name="StartAgain">Start the flown vessel's mission again ("Start the mission again"): its run abandoned if it is in progress, and a new run of that mission begun at its site, a fresh rocket on its pad; nothing else in the world changes. A menu choice, as <paramref name="Choose"/> is.</param>
/// <param name="Rewind">Bring the world back as it was at the last menu choice this session and make that choice again (see <see cref="Flight.CanRewind"/>); nothing, if none has been made.</param>
/// <param name="Choose">Start a flight of this mission (a new run), whatever is happening.</param>
/// <param name="From">The launch site to launch <paramref name="Choose"/> from: the flight's own (<see cref="Flight.Site"/>) if none.</param>
/// <param name="SwitchVessel">Fly the next vessel that can be flown (e.g. a booster with guidance, once dropped).</param>
/// <param name="SwitchTo">Fly this vessel instead, if it can be flown (e.g. one clicked in its view).</param>
/// <param name="SetTarget">Make this the flown vessel's target (its marker clicked), if it is something else that is there.</param>
/// <param name="ClearTarget">The flown vessel's target is no longer wanted.</param>
/// <param name="Plan">The flown vessel's planned burns from now on (its nodes placed, moved, changed or deleted).</param>
/// <param name="ChooseFrame">The frame of reference the player picks on the selector (see <see cref="Flight.FrameChoice"/>).</param>
/// <param name="Slower">Run time a rate slower (see <see cref="Flight.Rates"/>): taken by <see cref="Flight.RunFrame"/>.</param>
/// <param name="Faster">Run time a rate faster: taken by <see cref="Flight.RunFrame"/>.</param>
/// <param name="Watched">The vessels the player is watching besides the one flown (those in the small views).</param>
/// <param name="Camera">Where the main view's camera is, which the vessels near it in that view are witnessed by (see <see cref="Scene.MainViewWitnessed"/>); without it, the flown vessel, which the camera follows.</param>
/// <param name="NearMisses">Whether near misses past the flown vessel and the watched are slowed for and shown ahead (the player's setting, <see cref="Settings.SlowNearMisses"/>): see <see cref="Flight.NearMissWarning"/>.</param>
/// <param name="ToggleBayDoors">Open the flown vessel's bay doors, or close them (a vessel with a bay: <see cref="Vessel.HasBay"/>). No key does this.</param>
/// <param name="CarryOn">Carry on flying the vessel of this id where it is, in its run or in free play (any vessel in the world that can be flown: the carry-on screen offers <see cref="Flight.CarryOnChoices"/>): a menu choice, as <paramref name="Choose"/> is.</param>
/// <param name="CompleteRun">Complete the flown vessel's run with a bonus still open ("Complete mission"): the bonuses not done are lost.</param>
/// <param name="AbandonRun">Give up the flown vessel's run ("Abandon mission"): its vessels stay where they are, in no run.</param>
internal readonly record struct FlightInput(
    float Throttle = 0f,
    Vector3 Lean = default,
    Vector3? Hold = null,
    bool Launch = false,
    bool Stage = false,
    bool ToggleLegs = false,
    bool FullThrottle = false,
    bool CutThrottle = false,
    bool ToggleProgradeHold = false,
    bool ToggleRetrogradeHold = false,
    bool StartAgain = false,
    bool Rewind = false,
    Mission? Choose = null,
    LaunchSite? From = null,
    bool SwitchVessel = false,
    Vessel? SwitchTo = null,
    NavTarget? SetTarget = null,
    bool ClearTarget = false,
    ManoeuvreNode[]? Plan = null,
    FrameChoice? ChooseFrame = null,
    bool Slower = false,
    bool Faster = false,
    IReadOnlyList<Vessel>? Watched = null,
    Vector3? Camera = null,
    bool NearMisses = false,
    bool ToggleBayDoors = false,
    int? CarryOn = null,
    bool CompleteRun = false,
    bool AbandonRun = false);

/// <summary>
/// Something the player can carry on flying where it is (<see cref="Flight.CarryOnChoices"/>):
/// <paramref name="Vessel"/>, of <paramref name="Run"/> in progress, or of none (free play).
/// </summary>
internal sealed record CarryOnChoice(Vessel Vessel, MissionRun? Run);

/// <summary>
/// The moment of the last menu choice this session, to rewind to (<see cref="Flight.CanRewind"/>):
/// the world as it was then (<paramref name="World"/>, before a new rocket went on its pad), and
/// the choice, to make again: carrying on the vessel <paramref name="VesselId"/>
/// (<paramref name="Resumed"/>), or a flight of <paramref name="Mission"/> from <paramref name="Site"/>.
/// </summary>
internal sealed record RewindPoint(WorldState World, bool Resumed, int? VesselId, Mission Mission, LaunchSite Site);

/// <summary>
/// A brief word of a run ended while the flown vessel wasn't one of its own (<see cref="Flight.Notice"/>):
/// its <paramref name="text"/>. A class, so each notice is a new one to the HUD however alike their words.
/// </summary>
internal sealed class RunNotice(string text)
{
    public string Text { get; } = text;
}

/// <summary>
/// The flight, in the world it is flown in: the launch, then every vessel in this flight (the
/// rocket, and each part that comes away from it), the pilot's input sent to the one being flown,
/// and the sounds they call for; and every other vessel in the world, left there by earlier flights
/// (or by the world's preset), which carry on too. Each vessel's own rules (engine, fuel, steering,
/// legs, landing or crashing) are in <see cref="Vessel"/>; the physics is in <see cref="RocketPhysics"/>.
/// </summary>
/// <remarks>
/// <para>What the player is trying to do is a mission run (<see cref="MissionRun"/>): each flight
/// launched is one, and its steps and score count only its own vessels, whichever is flown. Any
/// number can be in progress at once (<see cref="Runs"/>); the flown vessel's is <see cref="Run"/>.</para>
/// <para>The pilot flies one vessel at a time (<see cref="Controlled"/>) and can switch to any other
/// that can be flown. The rest carry on with whatever they were last given: their throttle, where
/// they were pointed, prograde hold, their legs. The properties here (<see cref="Altitude"/>,
/// <see cref="Fuel"/> and so on) are the controlled vessel's.</para>
/// <para>The world carries on from flight to flight (see <see cref="WorldState"/>): choosing a mission
/// starts another flight in it, everything else staying where it is; starting again (R) undoes
/// this flight, putting the world back as it was when it started.</para>
/// </remarks>
internal sealed class Flight : IDisposable
{
    /// <summary>How far round the launch pad (beyond its edge), and how high above the tower, something is in the new rocket's way.</summary>
    private const float PadClearance = 3f, PadClearanceAbove = 30f;

    /// <summary>
    /// The most vessels a world holds: past it, the oldest go (but never the one being flown),
    /// whether a world is loaded with more, a new rocket is put on the pad, or staging adds parts.
    /// It keeps the world within the physics' bodies (one each, leaving room for the pads, the
    /// tower and the ground's tiles: see <see cref="RocketPhysics.MaxBodies"/>), and the drawing
    /// within reason; it's a debugging and performance limit for now.
    /// </summary>
    public const int Capacity = 1000;

    private readonly Worlds _worlds;
    private readonly List<SoundEvent> _sounds = [];
    private readonly List<MissionRun> _runs = []; // every run begun since the world was loaded, oldest first
    private List<MissionRun> _inProgress = [];   // those of them in progress, as last asked (see Runs)
    private readonly List<Vessel> _flownFlight = []; // the flown vessel's flight's vessels (see Vessels)
    private (Vessel? Flown, int Changes) _flownFlightOf; // what _flownFlight was gathered for
    private int _worldChanges;                    // how many times _world has had a vessel added or taken out
    private readonly List<Vessel> _world = [];    // everything in the world, this flight's included
    private readonly List<RocketControl> _controls = [];
    private WorldState _info = new();             // the world's name and so on (its vessels are live, in _world)
    private RewindPoint? _rewind;                 // the last menu choice this session, to rewind to (never saved)
    private readonly List<LogEntry> _log = [];    // the world's losses, oldest first (see Log)
    private int _nextId, _flights;

    /// <summary>The flown vessel's mission: its run's, or free play's for a vessel of no run.</summary>
    public Mission Mission => Run?.Mission ?? Mission.FreePlay;

    /// <summary>
    /// Where this flight was launched from, and the next will be (starting again, or another
    /// mission): Home unless another was picked (see <see cref="LaunchFrom"/>).
    /// </summary>
    public LaunchSite Site { get; private set; }

    /// <summary>Starts another flight of <paramref name="mission"/>, from <paramref name="site"/>, in this world: a menu choice, which the rewind comes back to (<see cref="CanRewind"/>).</summary>
    public void LaunchFrom(LaunchSite site, Mission mission) => LaunchFrom(site, mission, rewindHere: true);

    /// <summary>
    /// <see cref="LaunchFrom(LaunchSite, Mission)"/>, the rewind point taken there only if
    /// <paramref name="rewindHere"/> (not when a rewind makes the same choice again).
    /// </summary>
    private void LaunchFrom(LaunchSite site, Mission mission, bool rewindHere)
    {
        ChoiceMade();
        Site = site;
        StartFlight(mission, rewindHere);
    }

    /// <summary>
    /// Whether there is a moment to rewind to (<see cref="FlightInput.Rewind"/>): the last menu
    /// choice this session (a new mission, a mission started again, or something carried on), and
    /// never a switch to another vessel. None after <see cref="Restore"/> until a choice is made, so
    /// it is never before the world was loaded; never saved, as the saved world is that base anyway.
    /// </summary>
    public bool CanRewind => _rewind is not null;

    /// <summary>How far back in the world's time the rewind goes: its clock now less its clock at the rewind point (0 with none).</summary>
    public double RewindSeconds => _rewind is { } point ? WorldTime - point.World.Time : 0;

    /// <summary>Whether the rewind point's choice was something carried on (it "was resumed"), not a mission begun.</summary>
    public bool RewindResumed => _rewind?.Resumed ?? false;

    /// <summary>
    /// Starts the flown vessel's mission again ("Start the mission again"): its run, if it is in
    /// progress, is abandoned (logged as started again), and a new run of the run's mission (or,
    /// for a vessel of none, the vessel's own) is begun at its site. Its old vessels stay where
    /// they are, in no run, and nothing else in the world changes but what a new rocket on that
    /// pad clears away.
    /// </summary>
    /// <summary>
    /// What starting again (<see cref="FlightInput.StartAgain"/>) begins, and where: the flown
    /// vessel's run's mission at its site (an ended run's too, while it is shown), or for a vessel
    /// of none, its own mission and site.
    /// </summary>
    public (Mission Mission, LaunchSite Site) StartAgainOf => Run is { } run ? (run.Mission, run.Site) : (Controlled.Mission, Controlled.Site);

    private void StartAgain()
    {
        var (mission, site) = StartAgainOf;
        RunOf(Controlled)?.Abandon("Started again.", WorldLog.StartedAgain);
        LaunchFrom(site, mission);
    }

    /// <summary>
    /// Brings back the world of <paramref name="point"/> and makes its choice again, keeping the same
    /// point, so a second rewind goes back to the same moment. Nothing is written to the log: the
    /// log is rewound with the rest of the world.
    /// </summary>
    private void Rewind(RewindPoint point)
    {
        // A mission begun comes back as it was chosen: looked at from the pad, its new rocket next.
        Restore(point.World, flightStarts: !point.Resumed);
        // Something carried on is carried on again; were it not there to fly, the vessel the world
        // would be flown with, so a world is never flown unchosen (or with none, the point's mission).
        if (point.VesselId is not { } id
            || !(_world.Find(v => v.Id == id) is { } vessel && CarryOn(vessel, rewindHere: false)) && !CarryOn(Controlled, rewindHere: false))
            LaunchFrom(point.Site, point.Mission, rewindHere: false);
        _rewind = point;
    }

    /// <summary>
    /// What can be carried on where it is (see <see cref="CarryOn"/>): each run in progress that
    /// hasn't failed, oldest first, by its vessel flown last if that is still there to fly, else
    /// its first that is; then every vessel there to fly that is in no run in progress (free
    /// play's, and those of runs ended or failed), the one flown first, then by flight, newest
    /// first. Debris and wrecks can't be flown, so are never among them, nor is a run with none
    /// of its own left to fly.
    /// </summary>
    public IReadOnlyList<CarryOnChoice> CarryOnChoices()
    {
        var choices = new List<CarryOnChoice>();
        var inRuns = new HashSet<Vessel>(); // the vessels of runs in progress, as RunOf has them
        var inWorld = new HashSet<Vessel>(_world);
        foreach (var run in Runs)
        {
            if (run.Failed)
                continue;
            foreach (var v in run.Vessels)
                if (v.Flight == run.Number)
                    inRuns.Add(v);
            var vessel = run.Vessels.FirstOrDefault(v => v.Id == run.LastFlownId && Resumable(v) && inWorld.Contains(v))
                ?? run.Vessels.FirstOrDefault(v => Resumable(v) && inWorld.Contains(v));
            if (vessel is not null)
                choices.Add(new CarryOnChoice(vessel, run));
        }
        // (OrderBy is stable: within a flight, in the order they came to be.)
        foreach (var vessel in _world.Where(v => Resumable(v) && !inRuns.Contains(v)).OrderByDescending(v => v == Controlled).ThenByDescending(v => v.Flight))
            choices.Add(new CarryOnChoice(vessel, null));
        return choices;

        bool Resumable(Vessel v) => v != _placeholder && CanFly(v);
    }

    /// <summary>
    /// Carries on flying <paramref name="vessel"/> (any in the world that can be flown, not only one
    /// of <see cref="CarryOnChoices"/>) where it is, in its run (which it is now the
    /// vessel flown last of) or in free play: a menu choice (<see cref="Chosen"/>), which adds,
    /// moves and removes nothing in the world but the placeholder. It is brought into the physics
    /// and flown as a switch to it is (<see cref="SwitchTo"/>). False, and nothing done, if it
    /// can't be flown. The rewind comes back to this choice (<see cref="CanRewind"/>).
    /// </summary>
    public bool CarryOn(Vessel vessel) => CarryOn(vessel, rewindHere: true);

    /// <summary><see cref="CarryOn(Vessel)"/>, the rewind point taken there only if <paramref name="rewindHere"/> (not when a rewind makes the same choice again).</summary>
    private bool CarryOn(Vessel vessel, bool rewindHere)
    {
        if (vessel == _placeholder || !CanFly(vessel) || !_world.Contains(vessel))
            return false;
        ChoiceMade();
        if (rewindHere)
            _rewind = new RewindPoint(Capture(), Resumed: true, vessel.Id, RunOf(vessel)?.Mission ?? vessel.Mission, vessel.Site);
        _shownEnded = null; // a choice, as a switch is: a run ended shows no more
        if (vessel == Controlled)
            BringIn(vessel);
        else
            SwitchTo(vessel);
        RunOf(vessel)?.LastFlownId = vessel.Id; // only a run in progress: one ended is no one's
        CarriedOn?.Invoke(vessel);
        return true;
    }

    /// <summary>The runs in progress whose missions have objectives, oldest first (free play's are never among them).</summary>
    public IReadOnlyList<MissionRun> Runs
    {
        get
        {
            // The same list while nothing has changed (so asking allocates nothing), a new one when
            // something has (so a list handed out earlier never changes under its holder).
            // (Only a mission with objectives makes a run. _runs holds only runs in progress after
            // each step; one ended within a step is let go at its end.)
            var at = 0;
            var same = true;
            foreach (var run in _runs)
                if (run.End == RunEnd.InProgress)
                    same &= at < _inProgress.Count && _inProgress[at++] == run;
            if (same && at == _inProgress.Count)
                return _inProgress;
            var runs = new List<MissionRun>(_runs.Count);
            foreach (var run in _runs)
                if (run.End == RunEnd.InProgress)
                    runs.Add(run);
            return _inProgress = runs;
        }
    }

    /// <summary>
    /// The flown vessel's run (see <see cref="RunOf"/>); or the run it was of that has just ended
    /// (its steps and its result still shown) until another vessel is flown or something new is
    /// begun or carried on; null for a vessel of none.
    /// </summary>
    public MissionRun? Run => RunOf(Controlled) ?? (_shownEnded is { } ended && ended.Vessels.Contains(Controlled) ? ended : null);

    /// <summary>
    /// The run <paramref name="v"/> is one of the vessels of: one in progress, with objectives, that
    /// hasn't failed (one failed is ended as its steps are judged). None for a vessel of free play,
    /// or of a run that has ended, which belong to no run.
    /// </summary>
    public MissionRun? RunOf(Vessel v)
    {
        foreach (var run in _runs)
            if (run.End == RunEnd.InProgress && run.Number == v.Flight && run.Mission.HasObjectives && run.Vessels.Contains(v) && !run.Failed)
                return run;
        return null;
    }

    /// <summary>
    /// The last run of the flown vessel's to end, shown as <see cref="Run"/> while it is flown (see
    /// there), so the panel and its result stay.
    /// </summary>
    private MissionRun? _shownEnded;

    /// <summary>
    /// A brief word of a run that ended while the flown vessel wasn't one of its own ("REACH ORBIT
    /// (flight 5) complete: 1,800 pts"), for the HUD to show and fade by its own clock; the run's
    /// world-log entry stays.
    /// </summary>
    public RunNotice? Notice { get; private set; }

    /// <summary>Every run's steps by its own vessels, whichever is flown, each ended once it is over (see <see cref="MissionRun.Update"/>); those ended are let go.</summary>
    private void UpdateRuns()
    {
        for (var i = 0; i < _runs.Count; i++)
            _runs[i].Update();
        _runs.RemoveAll(static r => r.End != RunEnd.InProgress);
    }

    /// <summary>Step <paramref name="step"/> of <paramref name="run"/> has just been done: the world's log says so, with its points.</summary>
    internal void RunStepDone(MissionRun run, int step) =>
        AddToLog(RunEntry(run, WorldLog.StepDone(run.Mission.Steps[step].Label, run.Progress.PointsFor(step))));

    /// <summary>
    /// <paramref name="run"/> has just ended: the world's log says how (<paramref name="how"/>, for
    /// one abandoned), and the flown vessel shows its result if it is one of the run's, else the
    /// player is told (<see cref="Notice"/>).
    /// </summary>
    internal void RunEnded(MissionRun run, string? how)
    {
        var name = WorldLog.Named(run);
        AddToLog(RunEntry(run, run.End switch
        {
            RunEnd.Completed => WorldLog.Completed(run.Score ?? 0),
            RunEnd.Failed => WorldLog.FailedBecause(run.EndReason ?? ""),
            _ => how ?? WorldLog.Abandoned,
        }));
        if (Controlled is { } flown && run.Vessels.Contains(flown))
            _shownEnded = run;
        else
            Notice = new(run.End switch
            {
                RunEnd.Completed => $"{name} complete: {run.Score ?? 0:#,0} pts",
                RunEnd.Failed => $"{name} {WorldLog.FailedBecause(run.EndReason ?? "")}",
                _ => $"{name} abandoned",
            });
    }

    /// <summary>An entry of <paramref name="run"/>'s in the world's log: named by its flight, at its site, now, and seen.</summary>
    private LogEntry RunEntry(MissionRun run, string how) => new(WorldTime, WorldLog.Named(run), how, null, run.Site.Name, true);

    /// <summary>
    /// The flown vessel's flight's vessels: every vessel in the world with its <see cref="Vessel.Flight"/>
    /// number, in the order they came to be (the rocket that launched first).
    /// </summary>
    public IReadOnlyList<Vessel> Vessels => FlownFlight;

    private List<Vessel> FlownFlight
    {
        get
        {
            if (_flownFlightOf != (Controlled, _worldChanges))
            {
                _flownFlightOf = (Controlled, _worldChanges);
                _flownFlight.Clear();
                foreach (var vessel in _world)
                    if (vessel.Flight == Controlled.Flight)
                        _flownFlight.Add(vessel);
            }
            return _flownFlight;
        }
    }

    /// <summary>Every vessel in the world: earlier flights' and this one's, in the order they came to be.</summary>
    public IReadOnlyList<Vessel> WorldVessels => _world;

    /// <summary>The world's name, and what it is saved as (empty if it isn't).</summary>
    public string WorldName => _info.Name;
    public string WorldId => _info.Id;

    /// <summary>Seconds of play in the world: its clock, which runs whatever is going on, but stops while the game is paused.</summary>
    public double WorldTime { get; private set; }

    /// <summary>
    /// Every vessel lost in the world (by the physics, by formula, or removed to make room), newest
    /// last, the newest <see cref="Scene.WorldLogEntries"/>; saved with the world. The log screen
    /// shows it newest first.
    /// </summary>
    public IReadOnlyList<LogEntry> Log => _log;

    /// <summary>How many times <see cref="Log"/> has changed (an entry added, two joined, a world loaded): the log screen builds its lines again only when this moves on.</summary>
    internal int LogChanges { get; private set; }

    /// <summary>How many vessels were cleared off the launch pad to make room when this flight started (not counting a rocket that never launched, or never left the ground, which is just replaced: see <see cref="Vessel.NeverFlew"/>).</summary>
    public int ClearedFromPad { get; private set; }

    /// <summary>The vessel being flown: the pilot's input goes to it.</summary>
    public Vessel Controlled { get; private set; } = null!;

    /// <summary>
    /// How the flight as a whole stands: on the pad, counting down, or flying while any vessel that
    /// can be flown still is; then however the controlled one ended (landed or crashed).
    /// </summary>
    public FlightState State
    {
        get
        {
            if (Controlled.State == FlightState.Ready)
                return Controlled.State;
            foreach (var vessel in Vessels)
                if (vessel.State == FlightState.Flying && vessel.Guided)
                    return FlightState.Flying;
            return Controlled.State;
        }
    }

    // The controlled vessel's, for the HUD, the missions and the tests.
    public float Throttle => Controlled.Throttle;
    /// <summary>Which stage's engine is firing: 0 is the bottom one (the booster).</summary>
    public int Stage => Controlled.Stage;
    public int StageCount => Controlled.StageCount;
    public Stage CurrentStage => Controlled.CurrentStage;
    public bool CanStage => Controlled.CanStage;
    public bool FairingOn => Controlled.FairingOn;
    public bool JettisonsFairing => Controlled.JettisonsFairing;
    public float Fuel => Controlled.Fuel;
    public float TotalFuel => Controlled.TotalFuel;
    public float Mass => Controlled.Mass;
    public float MaxAcceleration => Controlled.MaxAcceleration;
    public float LegDeploy => Controlled.LegDeploy;
    public bool LegsWanted => Controlled.LegsWanted;
    public bool HasLegs => Controlled.HasLegs;
    public float BayDoors => Controlled.BayDoors;
    public bool BayDoorsWanted => Controlled.BayDoorsWanted;
    public bool HasBay => Controlled.HasBay;
    public Vector3 Aim => Controlled.Aim;
    public bool ProgradeHold => Controlled.ProgradeHold;
    public bool RetrogradeHold => Controlled.RetrogradeHold;
    public float TurnRate => Controlled.TurnRate;
    public float Altitude => Controlled.Altitude;
    public float Ground => Controlled.Ground;
    public float MaxAltitude => Controlled.MaxAltitude;
    public bool ReachedTarget => Controlled.ReachedTarget;
    public Vector3 Position => Controlled.Position;
    public Vector3 CentreOfMass => Controlled.CentreOfMass;
    public Vector3 Velocity => Controlled.Velocity;
    public Vector3 LocalUp => Controlled.LocalUp;
    public float VerticalSpeed => Controlled.VerticalSpeed;
    public float HorizontalSpeed => Controlled.HorizontalSpeed;
    public float OrbitSpeed => Controlled.OrbitSpeed;
    public float TiltDegrees => Controlled.TiltDegrees;
    public float Apoapsis => Controlled.Apoapsis;
    public float Periapsis => Controlled.Periapsis;
    public double OrbitPeriod => Controlled.OrbitPeriod;
    /// <summary>Whether the flown vessel's orbit would take it out of its body's pull before its highest point (see <see cref="Vessel.LeavesItsPull"/>).</summary>
    public bool LeavesItsPull => Controlled.LeavesItsPull;
    /// <summary>
    /// The lap of the flown vessel's orbit the HUD shows (<see cref="OrbitPeriod"/>): none while it
    /// would escape or leave its body's pull first, or only falls back (its periapsis deep under the ground).
    /// </summary>
    public double? OrbitLap => double.IsFinite(OrbitPeriod) && !LeavesItsPull && Periapsis > -Body.Radius * 0.9f ? OrbitPeriod : null;
    /// <summary>The speed among the stars a round orbit round the flown vessel's body needs where it is, or at the body's orbit floor if it is lower (<see cref="GroundBody.OrbitFloor"/>).</summary>
    public float OrbitSpeedNeeded => Body.CircularSpeed(MathF.Max(Body.Altitude(Position), Body.OrbitFloor));
    public bool OrbitStable => Controlled.OrbitStable;
    public bool AchievedOrbit => Controlled.AchievedOrbit;
    public Vector3? LandingSite => Controlled.LandingSite;
    public float DistanceToLandingSite => Controlled.DistanceToLandingSite;

    /// <summary>
    /// The straight-line distance from the flown vessel's centre of mass to its picked target (as
    /// <see cref="TargetAt"/> has it: a vessel's centre of mass, a place's top, the drone ship's
    /// deck, a body's centre; at another body, across the bodies), the same on the ground, in the air and in orbit; null with no target. Nothing is
    /// targeted unless the player picks it. Scoring never reads this: it goes by the mission's own
    /// landing site (<see cref="DistanceToLandingSite"/>), so a target can't change a score.
    /// </summary>
    public float? DistanceToTarget => TargetAt is { } at ? Vector3.Distance(CentreOfMass, at.Position) : null;
    public float TouchdownSpeed => Controlled.TouchdownSpeed;
    public bool LandedOnTarget => Controlled.LandedOnTarget;
    public string? CrashReason => Controlled.CrashReason;
    public Vector3 CrashPosition => Controlled.CrashPosition;
    public bool EngineLit => Controlled.EngineLit;

    /// <summary>Sounds called for during the last <see cref="Update"/>.</summary>
    public ReadOnlySpan<SoundEvent> Sounds => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_sounds);

    /// <summary>
    /// How many flights have started (a new one, the same one again, or in another world): what a
    /// cache of the world's vessels is keyed by beside the world's time, as a restart or a load
    /// comes back at the time it was captured, in the same list, maybe with as many in it.
    /// </summary>
    internal int Starts { get; private set; }

    /// <summary>Raised when a flight starts (a new one, the same one again, or in another world), for anything else that needs resetting.</summary>
    public event Action? Restarted;

    /// <summary>Raised when the pilot switches to flying another vessel.</summary>
    public event Action? Switched;

    /// <summary>Raised when a vessel is carried on (<see cref="CarryOn"/>, a rewind to one too), with that vessel: it may be flown only after its link-up.</summary>
    public event Action<Vessel>? CarriedOn;

    /// <summary>
    /// Raised when what the flown vessel's readings, frames and the camera are in changes to
    /// another body's frame: the flown vessel crossing a sphere of influence's edge, or one at
    /// another body switched to. With the change from the old frame to the new, at that moment,
    /// for anything kept in the old one (the camera, the paths' caches) to be changed across.
    /// </summary>
    public event Action<FrameChange>? ViewRebased;

    /// <summary>
    /// The communications link-up, while a vessel at a body whose physics isn't made yet is being
    /// switched to (see <see cref="LinkUp"/>): what the HUD draws over the main view, and the
    /// readout's <c>Link</c>.
    /// </summary>
    public LinkUp LinkUp { get; } = new();

    /// <summary>
    /// The link-ups over the small views (see <see cref="OpenView"/>): one shown over a view given to
    /// a vessel at a body whose physics isn't made, as <see cref="LinkUp"/> is over the main view,
    /// at most one a view. Made once, so opening a view allocates nothing for it.
    /// </summary>
    private readonly LinkUp[] _viewLinks = [.. Enumerable.Range(0, Scene.MaxSmallViews).Select(_ => new LinkUp())];

    /// <summary>The small views' link-ups (see <see cref="OpenView"/>), each on while it is shown.</summary>
    public ReadOnlySpan<LinkUp> ViewLinks => _viewLinks;

    /// <summary>The link-up over <paramref name="vessel"/>'s small view, while one is shown (see <see cref="OpenView"/>); else null.</summary>
    public LinkUp? ViewLink(Vessel vessel)
    {
        foreach (var link in _viewLinks)
            if (link.Vessel == vessel)
                return link;
        return null;
    }

    // The witnessed for a step (see Witnessed): the watched less any whose view's link-up is still waiting.
    private readonly List<Vessel> _witnessed = new(Scene.MaxSmallViews);

    private int _frames;     // real frames run (RunFrame), for the link-up's frame after
    private double _realTime; // and their real seconds, its clock

    /// <param name="worlds">The physics at each body in use, made as each is needed: every vessel is in its own body's, or under formula there.</param>
    /// <param name="world">The world to fly in: by default, an empty one that isn't saved.</param>
    /// <param name="site">Where to launch from: Home, by default.</param>
    public Flight(Worlds worlds, Mission mission, WorldState? world = null, LaunchSite? site = null)
    {
        _worlds = worlds;
        _collisions = new Collisions(this);
        Unwatched = new Unwatched(this);
        Site = site ?? LaunchSites.Home;
        Load(world ?? new WorldState(), mission);
    }

    /// <summary>The physics at <paramref name="body"/>, if it has one (something is, or was lately, in it there), else null.</summary>
    public RocketPhysics? PhysicsAt(GroundBody body) => _worlds.At(body);

    /// <summary>The physics at each body in use.</summary>
    internal Worlds Worlds => _worlds;

    /// <summary>The body the flown vessel is at: what its readings, its frames and the camera are in.</summary>
    public GroundBody Body => Controlled.Body;

    /// <summary>The body the launch sites are on, and the new rocket is put on its pad at (<see cref="GroundBody.HasSites"/>).</summary>
    private static GroundBody SitesBody => Sites.Body;

    /// <summary>
    /// Brings <paramref name="world"/> to life (whatever world was here before is gone: capture it
    /// first to keep it) and starts a flight of <paramref name="mission"/> in it, from
    /// <paramref name="site"/> (or the site flown from last): brought to life as <see cref="Restore"/>
    /// brings it, but looked at from the pad, where the new rocket goes, and that flight chosen.
    /// </summary>
    public void Load(WorldState world, Mission mission, LaunchSite? site = null)
    {
        Site = site ?? Site;
        Revive(world, flightStarts: true);
        Chosen = true;
        StartFlight(mission);
    }

    /// <summary>
    /// Brings <paramref name="world"/> to life as it was saved, starting no flight (whatever world
    /// was here before is gone: capture it first to keep it): its runs in progress go on, and the
    /// vessel flown is the one it was saved flying (or, if that one isn't there to fly, the newest
    /// that can be flown). Nothing in it is added, moved or removed. A world with nothing that can
    /// be flown has a rocket of free play put on the current <see cref="Site"/>'s pad meanwhile,
    /// behind the menu, which goes again, never saved, as a choice is made (<see cref="Chosen"/>).
    /// </summary>
    public void Restore(WorldState world) => Restore(world, flightStarts: false);

    /// <summary>
    /// <see cref="Restore(WorldState)"/>; with <paramref name="flightStarts"/>, looked at from the pad
    /// (a new rocket going on it next, as a rewind to a mission begun puts it), as <see cref="Load"/> does.
    /// </summary>
    private void Restore(WorldState world, bool flightStarts)
    {
        Starts++;
        LinkUp.Cancel(); // a vessel being switched to, or its link clearing, is the last world's
        foreach (var link in _viewLinks)
            link.Cancel(); // and so is a view's
        var before = Controlled;
        var flown = Revive(world, flightStarts);
        Chosen = false;
        if (flown is not null)
            Controlled = flown;
        else
            PutPlaceholder();
        _rewind = null; // nothing chosen in this world yet, to rewind to
        if (before is not null && before.Body != Controlled.Body)
            Rebase(Bodies.Change(before.Body, Controlled.Body, WorldTime));
        Begun();
    }

    /// <summary>
    /// Whether the player has chosen what to fly since the world was brought to life (a new
    /// mission, or one carried on): false after <see cref="Restore"/> until then, while the world
    /// is as it was saved (and is saved so: see <see cref="Capture"/>).
    /// </summary>
    public bool Chosen { get; private set; }

    /// <summary>The vessel the world was saved flying (its <see cref="WorldState.Flown"/>), as last brought to life, if any: whether or not it could be flown.</summary>
    public int? SavedFlown { get; private set; }

    /// <summary>The rocket of free play behind the menu after <see cref="Restore"/> of a world with nothing that can be flown, until a choice is made; never saved.</summary>
    private Vessel? _placeholder;

    /// <summary>
    /// Puts <see cref="_placeholder"/> on the current site's pad and flies it, in no run, beside
    /// whatever is on the pad (nothing is cleared for it: it may overlap debris there, but it is only
    /// ever behind a paused menu). It takes the world's next flight number and vessel id without
    /// counting them, so the world's numbers are as they were without it.
    /// </summary>
    private void PutPlaceholder()
    {
        var pad = _worlds.Need(SitesBody);
        var mission = Mission.FreePlay;
        _placeholder = new Vessel(_worlds, pad, mission, _sounds, pad.Launch(mission.Vehicle, Site), [.. mission.Vehicle.Stages.Select(s => s.FuelMass)], _nextId, _flights + 1) { Site = Site };
        _placeholder.ReadyToLaunch();
        _placeholder.ResetLandingLights(WorldTime);
        _world.Add(_placeholder);
        _worldChanges++;
        Controlled = _placeholder;
    }

    /// <summary>
    /// A choice has been made of what to fly (a new mission, or one carried on): the placeholder, if
    /// there is one, goes, unlogged (the world's numbers never counted it).
    /// </summary>
    private void ChoiceMade()
    {
        Chosen = true;
        if (_placeholder is not { } placeholder)
            return;
        _placeholder = null;
        placeholder.Physics?.Remove(placeholder.Craft);
        TakeOut(_world.IndexOf(placeholder));
        ForgetCollisions(); // one the map may show is gone
    }

    /// <summary>
    /// Brings <paramref name="world"/>'s vessels, runs, clock and log to life, in place of whatever
    /// world was here, and returns the vessel to fly in it (see <see cref="Restore"/>), if there is
    /// one; the vessel flown is left as it was, for the caller to choose. Unless
    /// <paramref name="flightStarts"/> (a new rocket going on the pad, where the first step looks),
    /// that vessel is where the first step looks, and comes back into the physics.
    /// </summary>
    private Vessel? Revive(WorldState world, bool flightStarts)
    {
        _placeholder = null; // it was the last world's
        _worlds.Clear();
        _world.Clear();
        _worldChanges++;
        _runs.Clear(); // the runs were the last world's
        (_shownEnded, Notice) = (null, null);
        ForgetCollisions(); // the map's were another world's
        Unwatched.Reset(); // nor are the formula's collisions, or who was chosen for the physics, this one's
        _askedAheadAt = double.NaN;
        _coming.Clear(); // nor is anything coming at that world's vessels
        // A world saved with more than it holds loses debris first, then vessels of no run in
        // progress, then runs' vessels, the oldest first in each (they are saved oldest first), never
        // the one to be flown (the one it was saved flying, or if that one isn't there to fly, the
        // newest that can be).
        var flown = world.Vessels.FirstOrDefault(s => s.Id == world.Flown && CanFly(s)) ?? world.Vessels.LastOrDefault(CanFly);
        SavedFlown = world.Flown;
        var goes = Excess(world, flown);
        RemovedForRoom = goes.Count;
        _info = world with { Vessels = [], Log = [], Runs = [], Flown = null };
        // (Wrecks are never saved, so none loaded is waiting to be logged.)
        _log.Clear();
        _log.AddRange(world.Log.TakeLast(Scene.WorldLogEntries));
        LogChanges++;
        WorldTime = world.Time;
        Lights.Reset(WorldTime); // each place starts as its own time of day has it
        // Each that goes is written to the log as any removed for room is, never seen (nothing has
        // been chosen for the physics in this world yet).
        foreach (var dropped in world.Vessels.Where(goes.Contains))
        {
            if (dropped.State != FlightState.Crashed && dropped.Vehicle.Stages[dropped.First].Kind != StageKind.Fairing)
                AddToLog(new LogEntry(WorldTime, WorldLog.Named(dropped), WorldLog.RemovedForRoom, null, WorldLog.Where(Bodies.Named(dropped.Body), dropped.CentreOfMass), false));
        }
        _bodiesAt = WorldTime;
        // The sites' body's physics now, for the new rocket on its pad, and the sea as it is now in
        // each, for the vessels restored into it.
        _worlds.Need(SitesBody);
        var inUse = _worlds.All;
        for (var i = 0; i < inUse.Count; i++)
            inUse[i].WavesAt(WorldTime);
        // Each vessel is restored into the physics, however it was moving when saved, and the first
        // steps send out those nobody watches (see Unwatched); but one far from where the first
        // step looks (the vessel to fly, or with none, the pad a new rocket goes on) that nothing
        // would hold there comes back under formula at once, with no body made only to be taken
        // out in the first step (for a world of a thousand, a frame's budget many times over: see
        // Unwatched.MayLoadOnFormula). The vessel to fly is always brought back into the physics.
        _flights = world.Flights;
        _nextId = world.NextVesselId;
        var kept = world.Vessels.Where(s => !goes.Contains(s)).ToArray();
        var (focusBody, focus) = flightStarts || flown is null ? (SitesBody, Site.Pad) : (Bodies.Named(flown.Body), flown.CentreOfMass);
        var partedFrom = world.Vessels.Where(s => s.PartingFrom is not null).Select(s => s.PartingFrom!.Value).ToHashSet();
        foreach (var state in kept)
            _world.Add(new Vessel(_worlds, _sounds, state, WorldTime,
                (flightStarts || !ReferenceEquals(state, flown)) && Unwatched.MayLoadOnFormula(state, focusBody, focus, partedFrom) ? WorldTime : null));
        _worldChanges++;
        // Parts saved still parting from each other go on parting, once both are back (in the one
        // physics: parts still parting are never loaded under formula, and are at one body).
        foreach (var state in kept)
            if (state.PartingFrom is { } from && _world.FirstOrDefault(v => v.Id == from) is { Physics: { } physics } other
                && _world.First(v => v.Id == state.Id) is { } part && part.Physics == physics)
                physics.Part(part.Craft, other.Craft);
        _nextId = Math.Max(_nextId, _world.Count == 0 ? 0 : _world.Max(v => v.Id) + 1);
        // Each vessel's landing lights start as its own place and time have them (nothing saved).
        for (var i = 0; i < _world.Count; i++)
            _world[i].ResetLandingLights(WorldTime);
        // Its runs in progress go on, each with the vessels of its flight number.
        foreach (var saved in world.Runs)
        {
            var run = new MissionRun(this, saved);
            foreach (var vessel in _world)
                if (vessel.Flight == saved.Number)
                    run.Add(vessel);
            _runs.Add(run);
        }
        return flown is null ? null : _world.First(v => v.Id == flown.Id);
    }

    /// <summary>
    /// The world as it is now, everything in one piece (wrecks are gone), to save it or bring it
    /// back later (see <see cref="Restore"/>). This flight's vessels are in it too, as they are,
    /// with its runs in progress and the vessel flown. Before a choice is made after a restore, the
    /// placeholder rocket isn't in it, nor counted: it is the world as it was restored.
    /// </summary>
    public WorldState Capture() => _info with
    {
        Time = WorldTime,
        Flights = _flights,
        NextVesselId = _nextId,
        Vessels = [.. _world.Where(v => v.State != FlightState.Crashed && v != _placeholder).Select(v => v.Capture(_world))],
        Log = [.. _log],
        Runs = [.. Runs.Select(r => r.Capture())], // (every Capture comes after an UpdateRuns with nothing stepped between: a run failed has ended by then, so isn't saved)
        // (Not one taken out, as the one in a new rocket's way on its pad is while it starts.)
        Flown = Controlled is { } flown && flown != _placeholder && _world.Contains(flown) ? flown.Id : null,
    };

    /// <summary>Renames the world (it is saved as the same file).</summary>
    public void Rename(string name) => _info = _info with { Name = name };

    /// <summary>Advances the flight by <paramref name="dt"/> seconds. Call once per frame.</summary>
    public void Update(float dt, in FlightInput input)
    {
        _sounds.Clear();
        Step(dt, input);
    }

    /// <summary>
    /// Holds the flight still for a frame, as while a menu is up: no time passes and no step is
    /// taken (one of no time would judge every vessel's contact again and change nothing), and
    /// <see cref="Sounds"/> is emptied, so the last step's aren't played again.
    /// </summary>
    public void Hold() => _sounds.Clear();

    /// <summary>
    /// The rates time can run at, from slow motion to time warp (the player steps through them
    /// with <see cref="FlightInput.Slower"/> and <see cref="FlightInput.Faster"/>).
    /// </summary>
    public static readonly float[] Rates = [0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f, 32f];

    /// <summary>Normal speed's place among the <see cref="Rates"/>.</summary>
    private static readonly int NormalRate = Array.IndexOf(Rates, 1f);

    private int _rate = NormalRate;

    /// <summary>The rate the player has picked (see <see cref="Rates"/>): 1 to start with, and kept from flight to flight.</summary>
    public float PlayerRate => Rates[_rate];

    /// <summary>
    /// The rate the game runs at, everything on its clock (the flight, the effects, the camera,
    /// the small views): the player's own, or the forced slow-down's while one is on
    /// (<see cref="Slowed"/>) and slower.
    /// </summary>
    public float TimeScale => Slowed is { } slowed ? MathF.Min(PlayerRate, slowed.Scale) : PlayerRate;

    /// <summary>
    /// The flown vessel's soonest fast collision (see <see cref="Collisions"/>), once it is within its
    /// lead (<see cref="Collisions.WarningSeconds"/>: in time to dodge it, or at once with no dodge
    /// possible), and until it has hit or is no longer one: what the HUD warns of. Looked for every
    /// step. Only the flown vessel's: the vessels in the small views are watched, never warned of.
    /// </summary>
    public Collision? Warning { get; private set; }

    private readonly Collisions _collisions;

    /// <summary>
    /// A collision (or a near miss) warned of: with which vessel, or what kind of thing where (at
    /// which body, in whose frame the place is, so it holds as the flown vessel crosses into
    /// another's); and when it was last the one warned of.
    /// </summary>
    private struct Warned
    {
        public Vessel? Other;
        public SolidKind With;
        public GroundBody? Body;
        public Vector3 At;
        public double Seen;
    }

    /// <summary>
    /// The collisions warned of, each dropping the warp only once, so warping again by hand isn't
    /// undone. Each is forgotten once it is no longer the soonest and has hit or is behind, or has
    /// not been the soonest for <see cref="Scene.WarnedGraceSeconds"/> (so a collision that is no
    /// fast one for a few steps isn't new when it is again); all of them as another vessel is
    /// flown. Room for a few, so no step allocates.
    /// </summary>
    private readonly Warned[] _warned = new Warned[4];
    private int _warnedCount;
    private Vessel? _warnedFor;

    /// <summary>
    /// Looks ahead for the fast collisions of the flown vessel, of each one <paramref name="watched"/>
    /// (the small views') and of the few nearest the main view's <paramref name="camera"/> (the
    /// flown vessel's place without it: see <see cref="InView"/>), after the step's motion (once a
    /// step: the collisions' snapshot is the world at this time). It warns of the flown vessel's
    /// soonest: a collision not warned of before drops the time warp to normal speed
    /// (<see cref="Advance"/> then stops the frame there, so it isn't run on past it at the old
    /// rate). And it slows the game for any of them (<see cref="Slow"/>). With
    /// <paramref name="nearMisses"/> (the player's setting), the near misses of the flown vessel and
    /// each watched one too: the flown one's soonest shown ahead (<see cref="NearMissWarning"/>),
    /// and the game slowed for the deepest. Then it keeps what is coming at each of the flown and
    /// the watched (<see cref="ComingAt"/>). Asked again at the same time, it changes nothing (the
    /// tests count what it costs so).
    /// </summary>
    internal void LookAhead(IReadOnlyList<Vessel>? watched = null, Vector3? camera = null, bool nearMisses = false)
    {
        var soonest = _collisions.Soonest(Controlled, _world, Scene.CollisionHorizonSeconds, TimeScale);
        Warn(soonest);
        // The soonest is warned of, but the slow-down follows the collision asking the least scale,
        // which a slower one just before it can hide (see Collisions.Deepest).
        var flown = _collisions.Deepest;
        _coming.Begin();
        var miss = nearMisses ? NearMisses(watched) : null;
        if (!nearMisses)
            NearMissWarning = null;
        InView(camera ?? Controlled.CentreOfMass, watched);
        Slow(flown, watched, miss);
        _coming.Keep(Controlled, soonest, NearMissWarning, watched);
    }

    /// <summary>
    /// What is coming at <paramref name="controlled"/>, the flown vessel or a small view's (as last
    /// looked ahead: see <see cref="LookAhead"/>), or null: the sooner of its fast collision, from
    /// its warning on (within its lead, <see cref="Collisions.WarningSeconds"/>, or once the game is
    /// slowed for it: the flown one's too, so it may begin a moment before <see cref="Warning"/>,
    /// where its slowing begins first) until it has hit or passed, and,
    /// with the player's setting on, its near miss still coming (from the moment it is predicted,
    /// as <see cref="NearMissWarning"/>). Null once over, no longer predicted, or the other wrecked
    /// or gone. What the small views flash for and the main view marks.
    /// </summary>
    public Coming? ComingAt(Vessel controlled) => _coming.At(controlled);

    /// <summary>Everything coming at a controlled vessel now, one each at most (see <see cref="ComingAt"/>): for the readout.</summary>
    internal ReadOnlySpan<Coming> Comings => _coming.All;

    /// <summary>What is coming at each controlled vessel, kept each step (see <see cref="ComingAt"/>).</summary>
    private readonly GameOne.Comings _coming = new();

    /// <summary>
    /// The flown vessel's soonest near miss still coming, while the player's setting is on
    /// (<see cref="FlightInput.NearMisses"/>), from the moment it is predicted (shown ahead, with
    /// no wait for a lead, as a near miss needs no dodge) until its closest
    /// approach: what the HUD's NEAR MISS line names. Not one still being eased back from after it
    /// has gone, while the next is coming. Only the flown vessel's, as <see cref="Warning"/>.
    /// </summary>
    public NearMiss? NearMissWarning { get; private set; }

    /// <summary>
    /// The near miss the HUD's NEAR MISS line (and the readout) names: <see cref="NearMissWarning"/>,
    /// but none while <see cref="Warning"/> is of the same vessel. A pass inside each other's sizes
    /// that misses outline to outline is both a collision (by their sizes) and a near miss, one event: the forced
    /// slow-down's, so only its COLLISION line is shown.
    /// </summary>
    public NearMiss? NearMissShown =>
        NearMissWarning is { } m && Warning is { Other: { } other } && other == m.Other ? null : NearMissWarning;

    /// <summary>
    /// The near misses shown, by the other vessel, each dropping the warp only once, and when each
    /// was last the one shown: forgotten as <see cref="_warned"/>'s are (gone, moving apart, or
    /// not shown for <see cref="Scene.WarnedGraceSeconds"/>), all of them as another vessel is flown.
    /// </summary>
    private readonly Warned[] _missesShown = new Warned[4];
    private int _missesShownCount;

    /// <summary>
    /// The near misses of the flown vessel and of each one <paramref name="watched"/>: shows the
    /// flown one's soonest still coming (<see cref="NearMissWarning"/>), and returns the deepest of
    /// all (<see cref="Collisions.NearestMiss"/>: the one asking the least scale now), or null.
    /// Those asked of are witnessed, so in the physics; one passing them may still be under formula
    /// while far off (it is handed in as it nears: see <see cref="Unwatched"/>), and is predicted
    /// from where the formula has it.
    /// </summary>
    private NearMiss? NearMisses(IReadOnlyList<Vessel>? watched)
    {
        var deepest = _collisions.NearestMiss(Controlled, _world, Scene.CollisionHorizonSeconds);
        ShowMiss(_collisions.SoonestMiss);
        var least = deepest is { } d ? d.ScaleAt(d.Seconds) : 1f;
        if (watched is not null)
            for (var i = 0; i < watched.Count; i++) // by index: a foreach over the interface would box its enumerator every step
                if (watched[i] != Controlled)
                {
                    var m = _collisions.NearestMiss(watched[i], _world, Scene.CollisionHorizonSeconds);
                    _coming.Miss(i, _collisions.SoonestMiss); // what is coming at it (see ComingAt)
                    if (m is { } miss && miss.ScaleAt(miss.Seconds) < least)
                        (deepest, least) = (miss, miss.ScaleAt(miss.Seconds));
                }
        return deepest;
    }

    /// <summary>
    /// Shows <paramref name="soonest"/>, the flown vessel's soonest near miss coming: a new one
    /// drops the time warp to normal speed, once (in time for its ease-in, which begins under a
    /// second of game time before it), as a collision's warning does.
    /// </summary>
    private void ShowMiss(in NearMiss? soonest)
    {
        Warned? shown = soonest is { } m ? new Warned { Other = m.Other, With = SolidKind.Craft, Seen = WorldTime } : null;
        Forget(_missesShown, ref _missesShownCount, shown);
        NearMissWarning = soonest;
        if (shown is { } now)
            Mark(_missesShown, ref _missesShownCount, now, _warned, _warnedCount);
    }

    /// <summary>
    /// The vessels in the main view near its camera, witnessed besides the flown one and the small
    /// views' (<see cref="InView"/>): those on the move first, each by nearness, at most
    /// <see cref="Scene.MainViewWitnessed"/>; with each its rank (its distance squared, and more for one held).
    /// Kept in a fixed array, so choosing them allocates nothing.
    /// </summary>
    private readonly Vessel[] _inView = new Vessel[Scene.MainViewWitnessed];
    private readonly float[] _inViewApart = new float[Scene.MainViewWitnessed];
    private int _inViewCount;

    /// <summary>
    /// Chooses the vessels the main view witnesses (<see cref="_inView"/>): those not wrecked
    /// within <see cref="Scene.NearByDistance"/> of its camera at <paramref name="camera"/>, those on
    /// the move first and then those held by what they are on, each nearest first, but the flown
    /// one and any <paramref name="watched"/> (already witnessed).
    /// Witnessed means any vessel shown in the main view or a small view: the main view
    /// draws every vessel, but one far off is a marker, not seen hitting anything.
    /// </summary>
    private void InView(Vector3 camera, IReadOnlyList<Vessel>? watched)
    {
        _inViewCount = 0;
        const float Reach = Scene.NearByDistance * Scene.NearByDistance;
        for (var i = 0; i < _world.Count; i++)
        {
            var v = _world[i];
            if (v == Controlled || v.State == FlightState.Crashed || v.Body != Controlled.Body)
                continue; // the camera is at the flown vessel's body, in its frame
            var apart = Vector3.DistanceSquared(v.CentreOfMass, camera);
            if (apart > Reach)
                continue;
            // One held by what it is on (frozen, landed, afloat) can hit nothing fast by itself,
            // and what comes at it fast is on the move, and witnessed as that: so every one on the
            // move comes before any held one, whatever their nearness, and a crowd at rest by the
            // camera (a field of landed boosters) never keeps a fast pair beyond it unwitnessed.
            // Held ones take any places left, nearest first (cheap: too slow to ask the ground of).
            if (v.Grounded || v.Craft.Frozen)
                apart += 2f * Reach;
            if ((_inViewCount == _inView.Length && apart >= _inViewApart[^1]) || IsWatched(v, watched))
                continue;
            // Into its place by its rank, the last dropped once full.
            var at = Math.Min(_inViewCount, _inView.Length - 1);
            while (at > 0 && _inViewApart[at - 1] > apart)
            {
                (_inView[at], _inViewApart[at]) = (_inView[at - 1], _inViewApart[at - 1]);
                at--;
            }
            (_inView[at], _inViewApart[at]) = (v, apart);
            _inViewCount = Math.Min(_inViewCount + 1, _inView.Length);
        }
    }

    /// <summary>
    /// What the last prediction for a main-view vessel found (<see cref="Slow"/>): when it was made
    /// and whether it found a fast collision. Two fixed arrays swapped each step, so it allocates nothing.
    /// </summary>
    private readonly record struct InViewMemo(Vessel Vessel, double AskedAt, bool Collides);
    private InViewMemo[] _memo = new InViewMemo[Scene.MainViewWitnessed];
    private InViewMemo[] _memoNext = new InViewMemo[Scene.MainViewWitnessed];
    private int _memoCount;

    private int MemoOf(Vessel v)
    {
        for (var i = 0; i < _memoCount; i++)
            if (_memo[i].Vessel == v)
                return i;
        return -1;
    }

    /// <summary>The world time the main view's prediction for <paramref name="v"/> was last made, or NaN if it is not in the view.</summary>
    internal double LastAsked(Vessel v) => MemoOf(v) is var at and >= 0 ? _memo[at].AskedAt : double.NaN;

    /// <summary>The vessels the main view witnesses near its camera, as the last look ahead chose them (see <see cref="InView"/>).</summary>
    internal ReadOnlySpan<Vessel> InViewVessels => _inView.AsSpan(0, _inViewCount);

    internal static bool IsWatched(Vessel v, IReadOnlyList<Vessel>? watched)
    {
        if (watched is null)
            return false;
        for (var i = 0; i < watched.Count; i++) // by index: a foreach over the interface would box its enumerator
            if (watched[i] == v)
                return true;
        return false;
    }

    /// <summary>Warns of <paramref name="soonest"/>, the flown vessel's soonest fast collision, once it is within its lead (see <see cref="Warning"/>).</summary>
    private void Warn(in Collision? soonest)
    {
        if (_warnedFor != Controlled)
        {
            (_warnedFor, _warnedCount, _missesShownCount) = (Controlled, 0, 0);
            Array.Clear(_missesShown); // holding no vessel no longer shown
        }
        Forget(_warned, ref _warnedCount, soonest is { } o ? Of(o) : null);
        if (soonest is not { } collision)
        {
            Warning = null;
            return;
        }
        var known = WarnedOf(collision);
        // Once warned of, it stays shown until it has hit or passed, though its lead may change.
        if (known < 0 && collision.Seconds > Collisions.WarningSeconds(Controlled, collision))
        {
            Warning = null;
            return;
        }
        Warning = collision;
        // Followed where it goes: the place a solid is hit at moves as its path is predicted again.
        Mark(_warned, ref _warnedCount, Of(collision), _missesShown, _missesShownCount);
    }

    /// <summary><paramref name="collision"/> as warned of now.</summary>
    private Warned Of(in Collision collision) => new() { Other = collision.Other, With = collision.With, Body = collision.Body, At = collision.At, Seen = WorldTime };

    /// <summary>
    /// Which of <paramref name="list"/>'s first <paramref name="count"/> <paramref name="warned"/>
    /// is, or -1: the same vessel; or for anything else, the same kind of thing hit within
    /// <see cref="Scene.SameSolidRadius"/> of where it was last (whatever the flown vessel's size,
    /// so a large one doesn't run two together).
    /// </summary>
    private static int IndexOf(Warned[] list, int count, in Warned warned)
    {
        for (var i = 0; i < count; i++)
            if (Same(list[i], warned))
                return i;
        return -1;
    }

    private int WarnedOf(in Collision collision) => IndexOf(_warned, _warnedCount, Of(collision));

    private static bool Same(in Warned warned, in Warned now) =>
        GameOne.Comings.SameThing(warned.Other, warned.With, warned.Body, warned.At, now.Other, now.With, now.Body, now.At);

    /// <summary>
    /// Keeps <paramref name="now"/> in <paramref name="list"/> as warned of at this time; a new one
    /// (the oldest going to make room) drops the time warp to normal speed, so each does only once,
    /// unless it is already in <paramref name="also"/> (the other of the collisions warned of and the
    /// near misses shown): a collision by their sizes that misses outline to outline is both, one event, so its
    /// second warning doesn't undo a warp the player set again after its first.
    /// </summary>
    private void Mark(Warned[] list, ref int count, in Warned now, Warned[] also, int alsoCount)
    {
        var known = IndexOf(list, count, now);
        if (known < 0)
        {
            if (count == list.Length) // the oldest goes
            {
                Array.Copy(list, 1, list, 0, list.Length - 1);
                count--;
            }
            known = count++;
            if (PlayerRate > 1f && IndexOf(also, alsoCount, now) < 0)
                _rate = NormalRate;
        }
        list[known] = now;
    }

    /// <summary>
    /// Forgets those warned of in <paramref name="list"/>, but for <paramref name="kept"/> (the one
    /// warned of now), that have hit (the other wrecked or gone), are behind (<see cref="Gone"/>),
    /// or have not been warned of for <see cref="Scene.WarnedGraceSeconds"/>, so one coming again
    /// later is a collision of its own. The one warned of is kept even past a pass's closest
    /// approach, while their sizes still overlap.
    /// </summary>
    private void Forget(Warned[] list, ref int count, in Warned? kept)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            ref var warned = ref list[i];
            if (kept is { } now && Same(warned, now))
                continue;
            if (Gone(warned) || WorldTime - warned.Seen > Scene.WarnedGraceSeconds)
            {
                Array.Copy(list, i + 1, list, i, count - i - 1);
                list[--count] = default; // holding no vessel no longer warned of
            }
        }
    }

    /// <summary>
    /// Whether what <paramref name="warned"/> was of is over for the flown vessel: another vessel
    /// wrecked, removed, or moving apart from it; a place behind it, as it moves over the ground
    /// there (another body's, the flown vessel's place and velocity changed into its frame).
    /// </summary>
    private bool Gone(in Warned warned)
    {
        var (at, velocity) = (Controlled.CentreOfMass, Controlled.Velocity);
        if (warned.Other is { } other)
            return GameOne.Comings.Lost(other) || Vector3.Dot(other.CentreOfMass - at, other.Velocity - velocity) > 0f;
        if (warned.Body is { } body && body != Controlled.Body)
        {
            var change = Bodies.Change(Controlled.Body, body, WorldTime);
            (at, velocity) = (change.Point(at).ToSingle(), change.Velocity(at, velocity).ToSingle());
        }
        return Vector3.Dot(warned.At - at, velocity) < 0f;
    }

    /// <summary>
    /// The forced slow-down, while one is on: for a witnessed fast collision (the flown vessel's, a
    /// small view's, or one near the main view's camera), the physics' step shortened by its scale (<see cref="Worlds.StepScale"/>, every body's),
    /// so each step's travel shrinks to within <see cref="Scene.FastCollisionStep"/> as it hits and
    /// nothing passes through anything; and the whole game's clock (<see cref="TimeScale"/>) slowed
    /// to it, unless the player's own slow motion is slower still. Slowed by the same scale, the
    /// steps a real second (and the frame rate) stay as they are. The step is the collision's, the
    /// clock the slower of the two: in the player's own slow motion a collision still shortens the
    /// step. See <see cref="Slowdown"/> for how deep it goes and when; and <see cref="Slow"/>. With
    /// the player's setting on, a near miss slows it too (<see cref="Slowdown.NearMiss"/>), by its
    /// own ease, where it asks for less than the fast collisions do.
    /// </summary>
    public Slowdown? Slowed { get; private set; }

    /// <summary>The slowest the slow-down has gone, which its ease back starts from (see <see cref="Slow"/>).</summary>
    private float _slowest;

    /// <summary>The world's time the slow-down was last looked at, so a look at the same time changes nothing.</summary>
    private double _slowedAt;


    /// <summary>What the reason was last made for (the vessel, or the kind of thing, and its name), so it is made only when that changes, not every step.</summary>
    private (Vessel? Other, SolidKind With, string Name) _reasonFor;

    /// <summary>
    /// Slows the game for the fast collisions asking the least scale (<see cref="Collisions.Deepest"/>,
    /// not the soonest, which can hide a faster one just behind it): the flown vessel's
    /// (<paramref name="flown"/>), each <paramref name="watched"/> one's and each the main view
    /// witnesses (<see cref="InView"/>). Its scale is the least any of them asks for
    /// (<see cref="Slowdown.ScaleFor"/>), followed down as they near; it only rises again by
    /// easing back, log-linearly over <see cref="Scene.SlowdownEaseSeconds"/> of real time from
    /// the slowest (or as slowly as a pass recedes, if slower), once its collision is over: hit (a
    /// crash, so nothing is coming), passed, or no longer witnessed (another vessel flown). It
    /// begins as any collision asks for a scale under 1, whatever the player's rate, and ends once
    /// eased back to 1. The rate keys do nothing meanwhile, so the player's rate is as it was
    /// (but for a warp a warning dropped). The physics' step is set only as it begins, changes
    /// and ends (never while none is on). Nothing is allocated, but a reason's words as what is
    /// slowed for changes. And <paramref name="miss"/>, the deepest near miss with the setting on
    /// (see <see cref="EaseMiss"/>): the game is slowed by whichever asks for the less.
    /// </summary>
    private void Slow(in Collision? flown, IReadOnlyList<Vessel>? watched, in NearMiss? miss)
    {
        var (least, deepest) = (1f, (Collision?)null);
        Deepest(flown, ref least, ref deepest);
        if (watched is not null)
            for (var i = 0; i < watched.Count; i++) // by index: a foreach over the interface would box its enumerator every step
                if (watched[i] != Controlled)
                {
                    var soonest = _collisions.Soonest(watched[i], _world, Scene.CollisionHorizonSeconds, TimeScale);
                    _coming.SetCollision(i, soonest); // what is coming at it (see ComingAt)
                    Deepest(_collisions.Deepest, ref least, ref deepest);
                }
        // And the main view's, near its camera (see InView): predicted in turn, to cut what a
        // crowd costs a step. One is asked at once if it is new to the view or the last time found a
        // collision (so one is never kept waiting, nor forgotten between turns); the rest, with
        // none in sight, are asked one a step, the one asked longest ago. One asked already at this
        // time (a look ahead repeated) leaves the turn done, so asking again changes nothing.
        var (turn, oldest, turnDone) = (-1, double.MaxValue, false);
        for (var i = 0; i < _inViewCount; i++)
        {
            var at = MemoOf(_inView[i]);
            if (at < 0 || _memo[at].Collides)
                continue;
            if (_memo[at].AskedAt >= WorldTime)
                turnDone = true;
            else if (_memo[at].AskedAt < oldest)
                (turn, oldest) = (i, _memo[at].AskedAt);
        }
        var kept = 0;
        for (var i = 0; i < _inViewCount; i++)
        {
            var at = MemoOf(_inView[i]);
            if (at >= 0 && !_memo[at].Collides && (i != turn || turnDone))
            {
                _memoNext[kept++] = _memo[at]; // not its turn: what it last found stands (nothing)
                continue;
            }
            var soonest = _collisions.Soonest(_inView[i], _world, Scene.CollisionHorizonSeconds, TimeScale);
            Deepest(_collisions.Deepest, ref least, ref deepest);
            _memoNext[kept++] = new InViewMemo(_inView[i], WorldTime, soonest is not null || _collisions.Deepest is not null);
        }
        (_memo, _memoNext) = (_memoNext, _memo);
        _memoCount = kept;

        var since = WorldTime - _slowedAt;
        _slowedAt = WorldTime;
        _forced = Forced(least, deepest, since);
        EaseMiss(miss, since);

        // The deeper of the two: each runs on its own, so the forced one is just as it would be
        // alone, and the near miss's is its own ease, never the forced one's holds. Both ease to
        // the same speed to the eye, so for a pass that is both they ask for nearly the same: the
        // scale is always the deeper, but what it is named for changes only once the other is
        // deeper by more than ReasonMargin, so the reason shown doesn't flicker between them.
        var was = Slowed;
        var now = _forced;
        var forcedScale = now?.Scale ?? 1f;
        var missNamed = was is { NearMiss: true }
            ? _missScale < 1f && _missScale <= forcedScale * ReasonMargin
            : _missScale * ReasonMargin < forcedScale;
        var scale = MathF.Min(_missScale, forcedScale);
        if (missNamed || (now is null && _missScale < 1f))
            now = new Slowdown(scale, _missPassSpeed, _missReason, NearMiss: true);
        else if (now is { } forced && scale < forced.Scale)
            now = forced with { Scale = scale };
        Slowed = now;
        if (now is { } on)
        {
            if (was?.Scale != on.Scale)
                _worlds.StepScale = on.Scale;
        }
        else if (was is not null)
            _worlds.StepScale = 1f;
    }

    /// <summary>
    /// The forced slow-down for the fast collisions alone (see <see cref="Slow"/>): what it is now,
    /// from what it was (<see cref="_forced"/>), with <paramref name="least"/> the least scale any
    /// asks for (<paramref name="deepest"/>'s), <paramref name="since"/> game seconds after the last look.
    /// </summary>
    private Slowdown? Forced(float least, in Collision? deepest, double since)
    {
        if (_forced is not { } on)
        {
            if (least >= 1f || deepest is not { } first)
                return null;
            _slowest = least;
            return new Slowdown(least, first.HitSpeed, Reason(first));
        }

        float scale;
        var reason = on;
        if (least <= on.Scale)
        {
            // Deepening (or holding) as it nears: what it is slowed for is the deepest.
            scale = least;
            _slowest = MathF.Min(_slowest, least);
            if (deepest is { } nearest)
                reason = on with { HitSpeed = Same(nearest) ? on.HitSpeed : nearest.HitSpeed, Reason = Reason(nearest) };
        }
        else if (deepest is { } coming && Same(coming) && least <= on.Scale * HeldRise
                 && (FromClosest(coming) > 0.0 || -FromClosest(coming) <= Slowdown.Hold(coming.HitSpeed)))
            // Still coming, or at its closest, only predicted a little slower (the air slowing it,
            // the prediction's own noise): held, so it never quickens on the way to its collision.
            // Much later than it was (a dodge nearly made), it eases rather than holding.
            scale = on.Scale;
        else
        {
            // Over, or receding: eased back, the real time since the last look at the game's
            // clock then, at no more than ln(1/slowest) a real second in the log. (The clock ran
            // at the game's scale, which a near miss may have made slower than this one's.)
            var real = Math.Max(0.0, since) / TimeScale;
            var eased = on.Scale * MathF.Exp((float)(MathF.Log(1f / _slowest) * real / Scene.SlowdownEaseSeconds));
            scale = MathF.Min(least, eased);
        }
        return scale >= 1f ? null : reason with { Scale = scale };
    }

    /// <summary>The forced slow-down for the fast collisions alone, which <see cref="Slowed"/> is unless a near miss asks for less.</summary>
    private Slowdown? _forced;

    // The near miss's slowing (see EaseMiss): its scale now (1 for none), how fast it eases
    // back (k, a game second: NearMiss.ScaleAt's), how fast it passes and why, and for which
    // vessel the reason was made, so it is made only when that changes.
    private float _missScale = 1f;
    private float _missEase;
    private float _missPassSpeed;
    private string _missReason = "";
    private Vessel? _missReasonFor;

    /// <summary>
    /// The near miss's slowing, <paramref name="since"/> game seconds after the last look: the
    /// deepest near miss's own scale (<see cref="NearMiss.ScaleAt"/>, which eases itself in and
    /// back), but never rising faster than that ease from where it was, so a near miss no longer
    /// predicted (dodged, gone, its other wrecked, or the setting turned off) eases back from the
    /// scale it was at by its own rate, never at a jump.
    /// </summary>
    private void EaseMiss(in NearMiss? miss, double since)
    {
        var scale = _missScale < 1f ? MathF.Min(1f, _missScale + _missEase * (float)Math.Max(0.0, since)) : 1f;
        if (miss is { } m && m.ScaleAt(m.Seconds) is var asked && asked <= scale && asked < 1f)
        {
            scale = asked;
            var s = NearMiss.Scale(m);
            _missEase = MathF.Log(1f / s) / Scene.SlowdownEaseSeconds; // as ScaleAt's own k
            _missPassSpeed = m.PassSpeed;
            if (_missReasonFor != m.Other)
                (_missReasonFor, _missReason) = (m.Other, $"a near miss with {WorldLog.Named(m.Other)}");
        }
        _missScale = scale;
    }

    /// <summary>How much deeper the other of the two slow-downs must be before the reason shown changes to it (see <see cref="Slow"/>).</summary>
    private const float ReasonMargin = 1.01f;

    /// <summary>How much higher than the scale now a collision still coming may ask for and be held at the scale now (see <see cref="Slow"/>): its prediction's noise and the air's slowing, a step at a time.</summary>
    private const float HeldRise = 1.05f;

    /// <summary>Keeps <paramref name="o"/> as the deepest, if it asks for a lower scale than <paramref name="least"/>.</summary>
    private static void Deepest(in Collision? o, ref float least, ref Collision? deepest)
    {
        if (o is not { } collision)
            return;
        var scale = Slowdown.ScaleFor(FromClosest(collision), collision.HitSpeed, collision.Touch);
        if (scale < least)
            (least, deepest) = (scale, collision);
    }

    /// <summary>
    /// Game seconds to <paramref name="o"/> (negative: since it), as the slow-down counts them: to
    /// its collision while coming, but once two vessels' sizes have touched, to or since their closest
    /// approach (their sizes are only spheres, touching well before they pass), so the hold at the
    /// slowest runs from there.
    /// </summary>
    private static double FromClosest(in Collision o)
    {
        if (o.Seconds >= 0.0 || o.Other is not { } other)
            return o.Seconds;
        var (r, u) = (Collisions.Middle(other) - Collisions.Middle(o.Watched), other.Velocity - o.Watched.Velocity);
        var uu = u.LengthSquared();
        return uu > 1e-6f ? -Vector3.Dot(r, u) / uu : o.Seconds;
    }

    /// <summary>Whether <paramref name="o"/> is what the reason was last made for.</summary>
    private bool Same(in Collision o) =>
        _reasonFor.Other == o.Other && _reasonFor.With == o.With && ReferenceEquals(_reasonFor.Name, Collisions.Name(o));

    /// <summary>
    /// Why the game is slowed, in the HUD's words: a collision with what is coming (another vessel
    /// by the log's name, else by its kind: <see cref="Collisions.Name"/>); and how fast it hits, as predicted when it
    /// became what is slowed for (made again only when that changes, so a step makes no words).
    /// </summary>
    private string Reason(in Collision o)
    {
        if (_forced is { } on && Same(o))
            return on.Reason;
        var name = Collisions.Name(o);
        _reasonFor = (o.Other, o.With, name);
        return $"a collision with {(o.Other is { } other ? WorldLog.Named(other) : name)} ({o.HitSpeed:#,0} m/s)";
    }

    /// <summary>
    /// A frame of <paramref name="realSeconds"/> of real time: takes the rate keys, then runs that
    /// times <see cref="TimeScale"/> of game time, frame by frame (<see cref="Update"/>) at normal
    /// speed or slower, in normal steps in warp (<see cref="Advance"/>, within
    /// <paramref name="budgetSeconds"/> of real time). Returns the game seconds it advanced.
    /// </summary>
    public float RunFrame(float realSeconds, in FlightInput input, double budgetSeconds = double.PositiveInfinity)
    {
        _frames++;
        _realTime += realSeconds;
        // A vessel switched to at a body with no physics last frame: its physics made now, behind
        // the link-up drawn since, and the vessel flown in it.
        if (LinkUp.Due(_frames))
        {
            var vessel = LinkUp.Vessel!;
            if (vessel != Controlled && CanFly(vessel) && _world.Contains(vessel))
            {
                _worlds.Need(vessel.Body);
                Fly(vessel);
                LinkUp.Ready(_realTime);
            }
            else
                LinkUp.Cancel();
        }
        // And each small view given to a vessel at a body with no physics, once drawn with its
        // link-up (the frame after it was opened: see OpenView): that body's physics made now,
        // behind the static on the screen, and the vessel brought into it.
        foreach (var link in _viewLinks)
            if (link.Due(_frames))
            {
                var vessel = link.Vessel!;
                if (vessel.State != FlightState.Crashed && _world.Contains(vessel))
                {
                    _worlds.Need(vessel.Body);
                    BringIn(vessel);
                    link.Ready(_realTime);
                }
                else
                    link.Cancel();
            }
        // Slowed, the game's rate is the slow-down's, not the player's to change.
        if (input.Slower && Slowed is null)
            _rate = Math.Max(0, _rate - 1);
        if (input.Faster && Slowed is null)
            _rate = Math.Min(Rates.Length - 1, _rate + 1);
        // Slowed, the step is shortened by the slow-down's scale (see Slow): at that clock this is
        // the same steps a real second, each that much shorter; under the player's own slower
        // slow motion, fewer of them, as slow motion always is.
        var dt = realSeconds * TimeScale;
        if (TimeScale > 1f)
            dt = Advance(dt, input, budgetSeconds);
        else
            Update(dt, input);
        // The lights (the places' and each vessel's landing lights) fade by the frame's real
        // seconds, whatever the warp, and are lit as the frame's own end has the sun.
        Lights.Step(WorldTime, realSeconds);
        for (var i = 0; i < _world.Count; i++) // by index: a foreach over the list would allocate
            _world[i].StepLandingLights(WorldTime, realSeconds);
        LinkUp.Step(_realTime);
        foreach (var link in _viewLinks)
            link.Step(_realTime);
        return dt;
    }

    /// <summary>The longest step <see cref="Advance"/> takes: a frame's worth, as at normal speed.</summary>
    public const float WarpStepSeconds = 1f / 60f;

    /// <summary>
    /// How much of each frame's real time the game's frame loop gives the flight's steps in warp
    /// (<see cref="RunFrame"/>'s budget), in seconds, of a 60 fps frame's 16.7 ms: drawing takes
    /// 1.5 or so. Past it, the world runs slower than asked (the HUD says so) rather than the
    /// frames stalling.
    /// </summary>
    public const double WarpBudgetSeconds = 0.012;

    /// <summary>
    /// Advances the flight by <paramref name="dt"/> seconds of game time, for time warp: in steps
    /// no longer than <see cref="WarpStepSeconds"/>, so everything (steering, landings, crashes)
    /// happens just as it would frame by frame at normal speed. One-off inputs (staging, toggles,
    /// switching) are taken in the first step only; held ones (throttle, steering) in every step.
    /// It stops early once <paramref name="budgetSeconds"/> of real time has gone, so a busy world
    /// can't stall the frame, or once <see cref="TimeScale"/> changes in a step, so the rest of
    /// the frame isn't run at a rate no longer asked for. <see cref="Sounds"/> holds every step's.
    /// Returns the game time it advanced.
    /// </summary>
    public float Advance(float dt, in FlightInput input, double budgetSeconds = double.PositiveInfinity)
    {
        _sounds.Clear();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var scale = TimeScale;
        var steps = Math.Max(1, (int)MathF.Ceiling(dt / WarpStepSeconds - 1e-4f));
        var each = dt / steps;
        var held = input with
        {
            Launch = false, Stage = false, ToggleLegs = false, FullThrottle = false, CutThrottle = false,
            ToggleProgradeHold = false, ToggleRetrogradeHold = false, StartAgain = false, Rewind = false, Choose = null, From = null,
            SwitchVessel = false, SwitchTo = null, SetTarget = null, ClearTarget = false, Plan = null, ChooseFrame = null,
            Slower = false, Faster = false, ToggleBayDoors = false, CarryOn = null, CompleteRun = false, AbandonRun = false,
        };
        for (var i = 0; i < steps; i++)
        {
            Step(each, i == 0 ? input : held);
            // A warning given in warp drops out of it (see LookAhead), and the frame's remaining
            // steps must not run on past it at the old rate.
            if (TimeScale != scale || System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds >= budgetSeconds)
                return each * (i + 1);
        }
        return dt;
    }

    /// <summary>One step of <see cref="Update"/>, adding to <see cref="Sounds"/>.</summary>
    private void Step(float dt, in FlightInput input)
    {
        Timings.BeginStep();
        _bodiesAt = WorldTime; // until the physics steps them on to the step's end
        WorldTime += dt;

        if (input.Choose is { } chosen)
            LaunchFrom(input.From ?? Site, chosen);
        else if (input.CarryOn is { } carried && _world.Find(v => v.Id == carried) is { } carriedOn)
            CarryOn(carriedOn);
        else if (input.StartAgain)
            StartAgain();
        else if (input.Rewind && _rewind is { } point)
            Rewind(point);
        if (input.CompleteRun)
            RunOf(Controlled)?.Complete();
        if (input.AbandonRun)
            RunOf(Controlled)?.Abandon("Abandoned.");

        if (input.SwitchTo is { } chosenVessel)
            SwitchTo(chosenVessel);
        else if (input.SwitchVessel)
            SwitchToNext();

        if (input.ClearTarget)
            Controlled.Target = null;
        if (input.SetTarget is { } target && Find(target, Controlled).Found)
            Controlled.Target = target;
        if (input.Plan is { } plan)
            Controlled.Replan(plan, WorldTime);
        ResolveTarget(); // the target as the input left it: picked, let go, or another vessel's own
        // Target can only be picked with a target: otherwise the choice stays as it was.
        if (input.ChooseFrame is { } choice && (choice != FrameChoice.Target || Target is not null))
            FrameChoice = choice;
        KeepFrame(); // a vessel switched to without a target of its own, or its target let go

        // Every vessel in the world flies: the controlled one as the pilot asks, the rest as they
        // were left. (Staging adds a vessel to the lists as it goes; it starts flying next frame.)
        for (int i = 0, count = _world.Count; i < count; i++)
        {
            var vessel = _world[i];
            if (!vessel.Guided)
                continue;
            if (vessel == Controlled)
            {
                // The one flown takes the pilot's input whatever it is doing: on the pad (the
                // throttle it will light at), landed or afloat (a landing is a stop, not the end
                // of it), or flying.
                if (vessel.State == FlightState.Crashed)
                    continue;
                vessel.TakeInput(dt, input);
                // Space lights the first stage at once, at the throttle set. That press is the
                // launch alone: Space is staging too (see Controls), and mustn't drop a stage as well.
                var launched = vessel.State == FlightState.Ready && input.Launch;
                if (launched)
                    vessel.Ignite();
                // Landed or afloat, throttling up is all it takes to fly again; staging works there too.
                vessel.TakeOff();
                if (input.Stage && !launched)
                {
                    // Its run asked before it parts: until the part is added, the run may seem to
                    // have lost what the part is.
                    var run = RunOf(vessel);
                    foreach (var dropped in vessel.Separate(_nextId))
                    {
                        _nextId++;
                        dropped.ResetLandingLights(WorldTime); // its lights start where they should be, not faded in
                        _world.Add(dropped);
                        _worldChanges++;
                        run?.Add(dropped); // a part is its vessel's run's, if it has one
                    }
                }
                if (vessel.State != FlightState.Flying)
                    continue;
                vessel.Run(dt, input.Lean, input.Hold, WorldTime, FrameVelocity, HoldFloor);
            }
            else if (vessel.State == FlightState.Flying)
                vessel.Run(dt, Vector3.Zero, null, WorldTime, Frames.VelocityIn(vessel.Body, vessel.AutoFrame, vessel.CentreOfMass, vessel.Velocity), Scene.HoldFloorSpeed);
        }
        MakeRoom();

        foreach (var vessel in _world)
        {
            vessel.MoveLegs(dt);
            vessel.MoveDoors(dt);
        }

        _controls.Clear();
        foreach (var vessel in _world)
            if (vessel.Control is { } control)
                _controls.Add(control);
        Timings.Lap(StepPart.Rules);
        // Each body's physics with a craft in it, in turn: each takes its own crafts' controls, and
        // the flown vessel's place only at its own body.
        _worlds.Step(dt, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_controls), Controlled.Body, Controlled.Position, WorldTime);
        _bodiesAt = WorldTime;
        // Those under formula moved on to now too, so everything after (their rules, the target,
        // the look ahead) sees every vessel where it is at this time.
        Unwatched.Advance(dt);

        foreach (var vessel in _world)
            vessel.AfterPhysics(dt, WorldTime);
        // Across a sphere's edge, each handed to the body it is at now (its touches judged first
        // in the world it was in), and the bodies those in the physics will soon be at asked for ahead.
        Cross(WorldTime);
        AskAhead(WorldTime);
        ResolveTarget(); // where it has moved to, or whether it has been destroyed or removed
        KeepFrame(); // and Auto may have switched
        var witnessed = Witnessed(input.Watched);
        LookAhead(witnessed, input.Camera, input.NearMisses); // charged to the rules, as everything after the physics is
        // Who is in the physics for the next step, once the look ahead has chosen the main view's
        // witnessed: handed out and in.
        Unwatched.Update(dt, witnessed, input.Camera ?? Controlled.CentreOfMass);
        LogLosses(); // after the formula's wrecks and collisions too, not only the physics'
        UpdateRuns();
        Timings.EndStep(_worlds.TileCount); // charges the rules after the physics
    }

    // The flown vessel's target and where it is, looked up once a step (see ResolveTarget): the
    // frame, its velocity, the holds and the HUD read it many times a frame, and a vessel target
    // is found by searching the whole world.
    private NavTarget? _target;
    private TargetPlace _targetAt = new(default, default, false, GroundBody.Planet, default, default);

    /// <summary>
    /// Looks the flown vessel's target up as it is now: after the input (which can pick one, let it
    /// go or switch vessels), after the physics (which moves it, or destroys it), and as a flight
    /// starts. Nothing changes either in between.
    /// </summary>
    private void ResolveTarget()
    {
        _target = TargetOf(Controlled);
        if (_target is { } target && Locate(target) is { } at)
            _targetAt = at;
    }

    /// <summary>
    /// Falls back to Auto if Target is chosen and the flown vessel has no target (any more).
    /// </summary>
    private void KeepFrame()
    {
        if (FrameChoice == FrameChoice.Target && _target is null)
            FrameChoice = FrameChoice.Auto;
    }

    /// <summary>
    /// The frame of reference the player has picked: Auto (the flown vessel's own,
    /// <see cref="Vessel.AutoFrame"/>) or one picked by hand, which stays until the player changes
    /// it, whatever Auto would do. Target only while the flown vessel has a target; once it hasn't
    /// (let go, destroyed, or a vessel switched to without one of its own), Auto. It is the
    /// player's, not the vessel's (switching vessels keeps it), and isn't saved: how the player is
    /// looking, as the camera is. A new flight starts on Auto.
    /// </summary>
    public FrameChoice FrameChoice { get; private set; }

    /// <summary>The frame in use for the flown vessel: the choice, with Auto resolved to its own.</summary>
    public Frame Frame => FrameChoice switch
    {
        FrameChoice.Surface => Frame.Surface,
        FrameChoice.Orbit => Frame.Orbital,
        FrameChoice.Target when _target is not null => Frame.Target,
        _ => Controlled.AutoFrame,
    };

    /// <summary>The frame the line is drawn in: the frame in use, or in Target the one Auto would use.</summary>
    public Frame LineFrame => Frame == Frame.Target ? Controlled.AutoFrame : Frame;

    /// <summary>
    /// The flown vessel's velocity in the frame in use: over the ground, among the stars, or
    /// relative to its target (measured in its own <see cref="Vessel.AutoFrame"/>). The holds steer
    /// by it and the travel markers show it, so a held nose and its marker always agree.
    /// </summary>
    public Vector3 FrameVelocity => Frame == Frame.Target
        ? Frames.Relative(Controlled.Body, Controlled.AutoFrame, CentreOfMass, Velocity, _targetAt.Position, _targetAt.Velocity)
        : Frames.VelocityIn(Controlled.Body, LineFrame, CentreOfMass, Velocity);

    /// <summary>
    /// Below this speed in the frame in use the holds leave the nose where it is: over the ground
    /// or round an orbit <see cref="Scene.HoldFloorSpeed"/>; relative to a target
    /// <see cref="Scene.TargetHoldFloorSpeed"/>, but only where that speed is free of the ground's
    /// noise (see <see cref="QuietTarget"/>).
    /// </summary>
    public float HoldFloor => QuietTarget ? Scene.TargetHoldFloorSpeed : Scene.HoldFloorSpeed;

    /// <summary>
    /// The slowest travel the travel markers show (on the rings and the heading tape): down to the
    /// holds' floor relative to a quiet target (see <see cref="QuietTarget"/>), so a held nose and
    /// its marker agree; otherwise <see cref="Scene.MarkerFloorSpeed"/>.
    /// </summary>
    public float MarkerFloor => QuietTarget ? Scene.TargetHoldFloorSpeed : Scene.MarkerFloorSpeed;

    /// <summary>
    /// In the target frame, whether the relative velocity is free of the ground's noise: the
    /// target is flying, or the flown vessel is in orbit (Auto would have it orbital). Against a
    /// place, a landed vessel or the drone ship, low down, it is the vessel's own velocity over the
    /// ground (less the deck's rocking), which is just what the surface's floor is there to ignore.
    /// </summary>
    private bool QuietTarget => Frame == Frame.Target && (_targetAt.Flying || Controlled.AutoFrame == Frame.Orbital);

    /// <summary>The flown vessel's speed relative to its target, in the target frame; otherwise none.</summary>
    public float? RelativeSpeed => Frame == Frame.Target ? FrameVelocity.Length() : null;

    /// <summary>In the target frame, the way from the flown vessel to its target (not normalised); otherwise none.</summary>
    public Vector3? TowardsTarget => Frame == Frame.Target ? _targetAt.Position - CentreOfMass : null;

    /// <summary>
    /// Hands the controls to the next of the flown vessel's flight's vessels (<see cref="Vessels"/>)
    /// that can be flown and hasn't been destroyed, if there is one. (Other flights' vessels are
    /// reached through their views.)
    /// </summary>
    private void SwitchToNext()
    {
        var vessels = FlownFlight;
        var at = vessels.IndexOf(Controlled);
        for (var step = 1; step <= vessels.Count; step++)
            if (SwitchTo(vessels[(at + step) % vessels.Count]))
                return;
    }

    /// <summary>
    /// Hands the controls to <paramref name="vessel"/>, if it is another one in the world that can
    /// be flown, back in the physics first if it was out of it (so it is flown there from the first
    /// frame). At a body whose physics isn't made, the link-up is shown first, and the physics made
    /// and the vessel flown in the next frame, behind it (<see cref="LinkUp"/>): so making a body's
    /// physics never stalls the frame the switch was asked in. True if it did, or will.
    /// </summary>
    private bool SwitchTo(Vessel vessel)
    {
        if (vessel == Controlled || !CanFly(vessel) || !_world.Contains(vessel))
            return false;
        if (_worlds.At(vessel.Body) is null)
        {
            LinkUp.Begin(vessel, _realTime, _frames);
            return true;
        }
        if (LinkUp.On)
            LinkUp.Cancel(); // another switched to meanwhile, at a body with its physics made: none over it
        Fly(vessel);
        return true;
    }

    /// <summary>Flies <paramref name="vessel"/> from now, in its body's physics (made: see <see cref="SwitchTo"/>); at another body, the view is rebased to its frame.</summary>
    private void Fly(Vessel vessel)
    {
        var from = Controlled.Body;
        BringIn(vessel);
        Controlled = vessel;
        _shownEnded = null; // another vessel flown: the run ended shows no more
        if (RunOf(vessel) is { } run)
            run.LastFlownId = vessel.Id;
        Switched?.Invoke();
        if (vessel.Body != from)
            Rebase(Bodies.Change(from, vessel.Body, WorldTime));
    }

    /// <summary>
    /// The flown vessel's frame has changed to another body's by <paramref name="change"/>:
    /// <see cref="ViewRebased"/> raised for what is kept in the old one. (What was warned of is
    /// kept by the body it is at, in that body's frame, so it needs no change.)
    /// </summary>
    private void Rebase(in FrameChange change) => ViewRebased?.Invoke(change);

    /// <summary>
    /// Hands each vessel across a sphere of influence's edge that has crossed one: the body it is
    /// at now by <see cref="Bodies.Choose(GroundBody, Vector3, double)"/> (into a child's sphere at
    /// its radius, out to a parent's only past <see cref="Scene.SphereLeaveFactor"/> times it), from
    /// the body it was at to whichever that is, by <see cref="Vessel.CrossTo"/> at the moment its
    /// motion is at (in the physics, its world's, <see cref="Worlds.Lag"/> behind). Each vessel
    /// crosses by itself, the moment its own place says so: two side by side at the edge are at two
    /// bodies once one has crossed, and never touch until the other has too. Nothing here is written
    /// for two bodies. Each body's place is worked out once a step for each of the two times
    /// motion is at (<see cref="Bodies.PlacesIn"/>), and a vessel nearer its body's centre than any
    /// edge comes then (<see cref="Bodies.ClearWithin"/>: a landed or afloat one always is, and most
    /// in orbit) is not asked at all, so a crowded world at warp costs little here.
    /// </summary>
    internal void Cross(double now)
    {
        var bodies = Bodies.All;
        Span<Vector3Double> placesNow = stackalloc Vector3Double[bodies.Count];
        Span<Vector3Double> placesLagged = stackalloc Vector3Double[bodies.Count];
        Span<double> clearNow = stackalloc double[bodies.Count];
        Span<double> clearLagged = stackalloc double[bodies.Count];
        var (madeNow, madeLagged) = (false, false);
        var asked = 0;
        for (var i = 0; i < _world.Count; i++)
        {
            var v = _world[i];
            if (v.State == FlightState.Crashed)
                continue;
            var lagged = v.Physics is not null;
            var at = MotionTime(v, now);
            var places = lagged ? placesLagged : placesNow;
            var clear = lagged ? clearLagged : clearNow;
            ref var made = ref lagged ? ref madeLagged : ref madeNow;
            if (!made)
            {
                Bodies.PlacesIn(bodies, at, places);
                for (var b = 0; b < bodies.Count; b++)
                    clear[b] = Bodies.ClearWithin(bodies, places, b);
                made = true;
            }
            if (((Vector3Double)v.CentreOfMass - v.Body.Centre).Length() < clear[v.Body.Index])
                continue;
            asked++;
            var into = Bodies.Choose(bodies, places, v.Body, v.CentreOfMass, at);
            if (into == v.Body)
                continue;
            var change = Bodies.Change(v.Body, into, at);
            var underFormula = v.OnFormula;
            v.CrossTo(into, change, at);
            if (underFormula)
                Unwatched.Crossed(v, at);
            if (v == Controlled)
                Rebase(change);
        }
        CrossAsked = asked;
    }

    /// <summary>
    /// How many vessels the last step's <see cref="Cross"/> asked which body they are at: those not
    /// wrecked and not nearer their body's centre than any edge (the rest need no asking).
    /// </summary>
    internal int CrossAsked { get; private set; }

    /// <summary>The world time <paramref name="v"/>'s motion is at: the flight's under formula, its world's in the physics (<see cref="Worlds.Lag"/> behind).</summary>
    private double MotionTime(Vessel v, double now) => v.Physics is null ? now : now - _worlds.Lag;

    /// <summary>How often (game seconds) the paths of the vessels in the physics are looked along for a body they will soon be at (see <see cref="AskAhead"/>): a small share of the lead.</summary>
    private const double AheadAskSeconds = Scene.PhysicsAheadSeconds / 20.0;

    private readonly List<PathLeg> _aheadLegs = new(Scene.PatchedLegs);
    private double _askedAheadAt = double.NaN;

    /// <summary>
    /// Asks for the physics of each body a vessel flying in the physics (the witnessed, and what is
    /// in it near them: a stage let go, a fairing half) will be at within
    /// <see cref="Scene.PhysicsAheadSeconds"/> along its path (times the rate in warp,
    /// <see cref="TimeScale"/>, so the worker has as long in real time), across every sphere it crosses
    /// (<see cref="Patched.Legs"/>), ahead of need (<see cref="Worlds.Ahead"/>: made on the tile
    /// worker's idle time), so the crossing finds it made and the step never waits on one. Every
    /// <see cref="AheadAskSeconds"/> of world time (<paramref name="now"/>); allocates nothing but
    /// as a body is first asked for. One under formula crosses under formula, needing no physics there.
    /// </summary>
    internal void AskAhead(double now)
    {
        if (now >= _askedAheadAt && now < _askedAheadAt + AheadAskSeconds)
            return;
        _askedAheadAt = now;
        var horizon = now + Scene.PhysicsAheadSeconds * Math.Max(1f, TimeScale);
        for (var i = 0; i < _world.Count; i++)
        {
            var v = _world[i];
            if (v.Physics is null || v.State != FlightState.Flying)
                continue;
            Patched.Legs(v.Body, v.CentreOfMass, v.Body.AmongStars(v.CentreOfMass, v.Velocity), now, _aheadLegs, horizon);
            for (var k = 1; k < _aheadLegs.Count; k++)
                _worlds.Ahead(_aheadLegs[k].Body);
        }
    }

    /// <summary>Who is in the physics, and who is moved by formula (see <see cref="GameOne.Unwatched"/>).</summary>
    internal Unwatched Unwatched { get; }

    /// <summary>Each lit place's lights, on from its own dusk to its own dawn, faded by real time (see <see cref="PlaceLights"/>): reset by a load, stepped by every frame.</summary>
    public PlaceLights Lights { get; } = new();

    // The map's collisions (see LookForCollisions): the search, on its own thread; the world as it is
    // handed to it, and the vessels it stands for, slot by slot (kept, so the handing allocates
    // nothing), for the pass out and for the one whose collisions are shown, with when that one was
    // asked (world time, and the real clock's stamp); what the last pass found; and whether a
    // pass is out (and if so, whether it was asked of a world since changed, to be thrown away).
    private readonly CollisionSearch _collisionSearch = new();
    private SearchVessel[] _askStates = new SearchVessel[Capacity], _passStates = new SearchVessel[Capacity];
    private Vessel[] _askVessels = new Vessel[Capacity], _passVessels = new Vessel[Capacity];
    private double _askedAt, _passAt;
    private long _askedStamp, _passStamp;
    private List<PredictedCollision> _mapCollisions = [], _mapCollisionsTaken = [];
    private bool _collisionAsked, _collisionStale;
    // Whether the world has changed since the last pass was asked (other than by its clock), and
    // who was flown then: see LookForCollisions.
    private bool _askAgain = true;
    private Vessel? _askedFor;

    /// <summary>
    /// The collisions between any two vessels in the world the map shows, soonest first, as the last
    /// pass of the search found them (see <see cref="CollisionSearch"/>; <see cref="LookForCollisions"/>
    /// keeps it going): only information, which nothing in the flight acts on. Each one's two
    /// vessels, and how they were handed to the search, are <see cref="CollisionVessel"/> and
    /// <see cref="CollisionState"/> by its slots.
    /// </summary>
    public IReadOnlyList<PredictedCollision> MapCollisions => _mapCollisions;

    /// <summary>How many passes' collisions have been taken (or forgotten): <see cref="MapCollisions"/> is another list each time it changes.</summary>
    internal int CollisionPass { get; private set; }

    /// <summary>The world time the pass whose collisions are shown was asked at: what its stars-frame points are measured from.</summary>
    internal double CollisionsAskedAt => _passAt;

    /// <summary>How long ago (real seconds) the pass whose collisions are shown was asked.</summary>
    internal double CollisionsAge => System.Diagnostics.Stopwatch.GetElapsedTime(_passStamp).TotalSeconds;

    /// <summary>The vessel in <paramref name="slot"/> of the pass whose collisions are shown (a collision's <see cref="PredictedCollision.SlotA"/> or <see cref="PredictedCollision.SlotB"/>).</summary>
    internal Vessel CollisionVessel(int slot) => _passVessels[slot];

    /// <summary>How the vessel in <paramref name="slot"/> was handed to that pass: where it was, and how it moved.</summary>
    internal ref readonly SearchVessel CollisionState(int slot) => ref _passStates[slot];

    /// <summary>
    /// While the map is drawn (<see cref="Paths"/>, each frame): takes what the search's last pass
    /// found, and once it has, hands it the world as it is now for the next, so a pass is always
    /// out while the map is up and the world moves on, and none is while it isn't, nor while the
    /// world stands still (paused, under a menu). Never waits on the search: a pass not
    /// finished yet, or its lock held just now, is taken a frame later. Each flying vessel is
    /// searched along the orbit of its present state, burning or not (the next pass takes its new
    /// one); each landed, afloat or on the pad is held where it is on the turning ground (so any
    /// two in the world are searched); a wreck is left out. A lap of the flown vessel's orbit
    /// ahead, but no further than <see cref="Scene.MapCollisionHorizonSeconds"/> (all of that, for one
    /// with no lap).
    /// </summary>
    internal void LookForCollisions()
    {
        if (_collisionSearch.TryTake(_mapCollisionsTaken))
        {
            _collisionAsked = false;
            if (_collisionStale)
                _collisionStale = false; // asked of the world before it changed: thrown away
            else
            {
                (_mapCollisions, _mapCollisionsTaken) = (_mapCollisionsTaken, _mapCollisions);
                (_passStates, _askStates) = (_askStates, _passStates);
                (_passVessels, _askVessels) = (_askVessels, _passVessels);
                (_passAt, _passStamp) = (_askedAt, _askedStamp);
                CollisionPass++;
            }
        }
        if (_collisionAsked)
            return;
        // Nothing has moved since the last was asked (paused, under a menu): a pass over the same
        // world would find the same, and keep a core busy for nothing. Asked again once the world's
        // clock moves on, its vessels change (ForgetCollisions) or another is flown (whose lap is
        // the horizon).
        if (!_askAgain && WorldTime == _askedAt && Controlled == _askedFor)
            return;
        var count = 0;
        foreach (var v in _world)
        {
            if (v.State == FlightState.Crashed || count >= _askStates.Length)
                continue;
            _askVessels[count] = v;
            _askStates[count++] = new SearchVessel(v.Id, v.Body, Collisions.Middle(v), v.Body.AmongStars(v.CentreOfMass, v.Velocity), Collisions.Size(v),
                v.ImpactTolerance, OnGround: v.State != FlightState.Flying);
        }
        var lap = Orbit.OverGround(Controlled.Body, Controlled.CentreOfMass, Controlled.Velocity, findEnd: false).Period;
        var horizon = double.IsFinite(lap) ? Math.Min(lap, Scene.MapCollisionHorizonSeconds) : Scene.MapCollisionHorizonSeconds;
        _collisionAsked = _collisionSearch.TryAsk(_askStates.AsSpan(0, count), WorldTime, horizon);
        if (_collisionAsked)
            (_askedAt, _askedStamp, _askedFor, _askAgain) = (WorldTime, System.Diagnostics.Stopwatch.GetTimestamp(), Controlled, false);
    }

    /// <summary>
    /// The world's vessels changed other than by moving (a world loaded, a new flight's clearing,
    /// some removed to make room): the collisions shown go, and a pass out, asked of the world as it
    /// was, is thrown away as it comes, as its slots no longer stand for the same vessels.
    /// </summary>
    private void ForgetCollisions()
    {
        _mapCollisions.Clear();
        _collisionStale = _collisionAsked;
        _askAgain = true; // whatever the clock says
        CollisionPass++;
    }

    /// <summary>Whether the map's collision search's thread is running: read-only inspection for the tests.</summary>
    internal bool CollisionSearchRunning => _collisionSearch.Running;

    /// <summary>How long the map's collision search's last pass took (real seconds): read-only inspection for the tests.</summary>
    internal double CollisionSearchLastPassSeconds => _collisionSearch.LastPassSeconds;

    /// <summary>Stops the map's collision search (its thread), waiting for a pass in hand to finish.</summary>
    public void Dispose() => _collisionSearch.Dispose();

    /// <summary>
    /// The world time the physics' bodies are at: the flight's, but during a step before the
    /// physics has stepped them, the step's start. A vessel brought back is made where the formula
    /// has it then, so the physics steps it on from there with the rest.
    /// </summary>
    private double _bodiesAt;

    /// <summary>
    /// Brings <paramref name="vessel"/> back into the physics at once, if it is out of it, where the
    /// formula has it (outside <see cref="Unwatched"/>'s budget): for a vessel switched to, or given
    /// a view, which must be in the physics before it is flown or drawn close up. The other of a
    /// collision under formula it is in comes with it (see <see cref="Unwatched.Return"/>).
    /// </summary>
    public void BringIn(Vessel vessel)
    {
        if (vessel.OnFormula && _world.Contains(vessel))
            Unwatched.Return(vessel, _bodiesAt);
    }

    /// <summary>
    /// What a new small view of <paramref name="vessel"/> needs of the flight (<see cref="Views.Open"/>):
    /// the vessel back in the physics, so the view shows the physics' (<see cref="BringIn"/>). At a
    /// body whose physics isn't made, as for a switch (<see cref="SwitchTo"/>), the view's link-up is
    /// shown over it first (<see cref="ViewLink"/>), and the physics made and the vessel brought in
    /// behind it once the view has been drawn with it. The HUD opens a view after drawing the others
    /// (its "Add view" list is under them), so a view opened in a frame is first drawn in the next,
    /// and its link-up is due in the one after that: so making a body's physics never stalls a frame
    /// before its static is on the screen. Meanwhile the vessel is left under formula, witnessed by
    /// nothing (see <see cref="Witnessed"/>); a view closed meanwhile lets its link-up go
    /// (<see cref="CloseView"/>), and nothing is made for it.
    /// </summary>
    public void OpenView(Vessel vessel)
    {
        if (!vessel.OnFormula || !_world.Contains(vessel) || _worlds.At(vessel.Body) is not null)
        {
            BringIn(vessel);
            return;
        }
        if (ViewLink(vessel) is not null)
            return; // its link-up is shown already
        foreach (var link in _viewLinks)
            if (!link.On)
            {
                // Due a frame later than a switch's: the view is first drawn next frame (see above).
                link.Begin(vessel, _realTime, _frames + 1);
                return;
            }
        // Every link-up taken (only while one still clears over a vessel whose view was swapped
        // into the main view): brought in now, uncovered.
        BringIn(vessel);
    }

    /// <summary>
    /// <paramref name="vessel"/>'s small view is closed (<see cref="Views.Close"/>): its link-up, if
    /// it has one, let go at once, so its body's physics isn't made for a view no longer shown (the
    /// vessel stays under formula), and no reading or static is left for a view opened again.
    /// </summary>
    public void CloseView(Vessel vessel) => ViewLink(vessel)?.Cancel();

    /// <summary>
    /// The vessels the player watches for a step (<paramref name="watched"/>: the small views'), but
    /// for any whose view's link-up still waits for its body's physics: <see cref="Unwatched"/> and the
    /// look ahead leave that one where it is, under formula, until its link-up's frame brings it in
    /// (<see cref="OpenView"/>), so its body's physics is never made in a step meanwhile.
    /// </summary>
    private IReadOnlyList<Vessel>? Witnessed(IReadOnlyList<Vessel>? watched)
    {
        var waiting = false;
        foreach (var link in _viewLinks)
            waiting |= link.Waiting;
        if (watched is null || !waiting)
            return watched;
        _witnessed.Clear();
        for (var i = 0; i < watched.Count; i++)
            if (ViewLink(watched[i]) is not { Waiting: true })
                _witnessed.Add(watched[i]);
        return _witnessed;
    }

    /// <summary>
    /// Whether the pilot could fly <paramref name="vessel"/>: it has guidance, and hasn't been
    /// destroyed. (A rocket still on the pad counts: having gone to look at an earlier flight's
    /// satellite before launching, the pilot can come back to it.)
    /// </summary>
    public static bool CanFly(Vessel vessel) => vessel.Guided && vessel.State != FlightState.Crashed;

    /// <summary>Whether the vessel <paramref name="state"/> saves could be flown, as <see cref="CanFly(Vessel)"/> asks of a live one.</summary>
    private static bool CanFly(VesselState state) =>
        state.State != FlightState.Crashed && state.Vehicle.Stages[state.First..(state.Last + 1)].Any(s => s.Guided);

    /// <summary>How the flown vessel's next node's burn stands, if it has a node.</summary>
    public NodeGuide? Guide => Controlled.Guide(WorldTime);

    /// <summary>The flown vessel's target, if it has one that is still there (as of the last step).</summary>
    public NavTarget? Target => _target;

    /// <summary>Where the flown vessel's target is (as <see cref="Locate"/> found it at the last step), if it has one: read every frame by the path, so not searched for again.</summary>
    public TargetPlace? TargetAt => _target is not null ? _targetAt : null;

    /// <summary>
    /// <paramref name="vessel"/>'s target, if it has one that is still there: a vessel that has
    /// been destroyed or removed is no target any more, and is forgotten.
    /// </summary>
    public NavTarget? TargetOf(Vessel vessel)
    {
        if (vessel.Target is { } target && !Find(target, vessel).Found)
            vessel.Target = null;
        return vessel.Target;
    }

    /// <summary>
    /// Where <paramref name="target"/> is now and how fast it is moving, if it is there, and whether
    /// it is flying (so on an orbit of its own) rather than staying put: a place on the ground, or
    /// a vessel standing on it. A vessel is where its centre of mass is, the point Jolt's velocity
    /// is for: an orbit or a velocity among the stars from its origin would add the ground's turn
    /// for the wrong point. The velocity is over the ground, as everything in the world is moved:
    /// an orbit is worked out among the stars, so one for it takes it through
    /// <see cref="Orbit.OverGround"/>. A drone ship rides the waves, so it is its deck's middle as
    /// the waves have it now, moving as the deck there does. A body is its centre, flying (on its
    /// path round its parent). All of it in the flown vessel's body's frame, over that body's
    /// ground, whatever body the target is at (changed across: see <see cref="Across"/>), and
    /// with the body it is at (a body's, itself).
    /// </summary>
    public TargetPlace? Locate(NavTarget target)
    {
        var (vessel, index, found) = Find(target, null);
        if (!found)
            return null;
        if (vessel is not null)
            return Across(vessel.Body, vessel.CentreOfMass, vessel.Velocity, vessel.State == FlightState.Flying);
        if (target.Kind == TargetKind.Body)
            return Across(Bodies.All[index], Bodies.All[index].Centre, Vector3.Zero, true);
        if (Sites.RaftOf(index) is { } raft)
        {
            var ship = Rafts.Now(raft);
            var deck = Vector3.Transform(new Vector3(0f, ship.Freeboard, 0f), ship.Current.Model);
            return Across(Sites.Body, deck, ship.VelocityAt(deck), false);
        }
        return Across(Sites.Body, Sites.Top(index), Vector3.Zero, false);
    }

    /// <summary>
    /// A target's place and velocity over the ground of <paramref name="body"/> (the body it is at,
    /// or a body's own centre), in the flown vessel's body's frame now: changed across the bodies
    /// (<see cref="Bodies.Change"/>, in double), so the distance to it, the speed relative to it
    /// and the way to it are measured as for one at the same body.
    /// </summary>
    private TargetPlace Across(GroundBody body, Vector3 position, Vector3 velocity, bool flying)
    {
        var view = Controlled.Body;
        if (body == view)
            return new(position, velocity, flying, body, position, velocity);
        var change = Bodies.Change(body, view, WorldTime);
        return new(change.Point(position).ToSingle(), change.Velocity(position, velocity).ToSingle(), flying, body, position, velocity);
    }

    /// <summary>
    /// What <paramref name="target"/> is, if it is there and isn't <paramref name="from"/> itself
    /// (nor, for a body, the body <paramref name="from"/> is at): the vessel, or the site's or
    /// body's number. A tuple rather than an object, so a number isn't boxed: this is asked every step.
    /// </summary>
    private (Vessel? Vessel, int Index, bool Found) Find(NavTarget target, Vessel? from)
    {
        switch (target.Kind)
        {
            case TargetKind.Vessel:
                foreach (var vessel in _world)
                    if (vessel.Id == target.Id)
                        return vessel != from && vessel.State != FlightState.Crashed ? (vessel, -1, true) : (null, -1, false);
                return (null, -1, false);
            case TargetKind.Site:
                return target.Id >= 0 && target.Id < Sites.All.Length ? (null, target.Id, true) : (null, -1, false);
            case TargetKind.Body:
                return target.Id >= 0 && target.Id < Bodies.All.Count && Bodies.All[target.Id] != from?.Body ? (null, target.Id, true) : (null, -1, false);
            default:
                return (null, -1, false);
        }
    }

    /// <summary>Whether there is another of the flown vessel's flight's vessels the pilot could switch to (with Tab).</summary>
    public bool CanSwitch
    {
        get
        {
            foreach (var vessel in Vessels)
                if (vessel != Controlled && CanFly(vessel))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Starts another flight of <paramref name="mission"/> in this world, a new run, leaving
    /// everything in it where it is, but for wrecks (gone) and anything in the new rocket's way on
    /// its site's pad (cleared away): a fresh rocket on the pad. Other runs go on, their rockets
    /// waiting on other pads included; one whose every vessel was cleared away is abandoned. With
    /// <paramref name="rewindHere"/>, the rewind point is taken just before the new rocket goes on
    /// the pad (see <see cref="CanRewind"/>).
    /// </summary>
    private void StartFlight(Mission mission, bool rewindHere = true)
    {
        Starts++;
        LinkUp.Cancel(); // a vessel being switched to, or its link clearing, is the last flight's
        foreach (var link in _viewLinks)
            link.Cancel(); // and so is a view's
        // Wrecks are gone, bodies and all: one still playing out its hit (see RocketPhysics.Wreck)
        // goes now, as the menu that chose this mission paused the game, and one wrecked on the
        // pad would be left lying unseen inside the new rocket.
        _worlds.FlushWrecks();
        for (var i = _world.Count - 1; i >= 0; i--)
            if (_world[i].State == FlightState.Crashed)
                TakeOut(i);
        ClearedFromPad = 0;
        for (var i = _world.Count - 1; i >= 0; i--)
            if (InTheWay(_world[i]))
            {
                // A rocket that was never launched is just replaced, as is one launched that never
                // left the ground (at zero throttle); anything else in the new rocket's way is
                // news, and the log's, as any removed for room is (the message saying so is soon
                // gone; the log keeps it).
                if (!_world[i].NeverFlew)
                {
                    ClearedFromPad++;
                    LogRemoved(_world[i], WorldLog.ClearedFromPad);
                }
                _world[i].Physics?.Remove(_world[i].Craft);
                var run = RunOf(_world[i]);
                TakeOut(i);
                // A run whose last vessel was in the way (its rocket waiting on this pad) is given up.
                if (run is { Vessels.Count: 0 })
                    run.Abandon("Its rocket was cleared from the pad.");
            }
        // A run left with no vessels by the wrecks cleared away fails now, so it isn't saved again.
        UpdateRuns();
        ForgetCollisions(); // wrecks gone, the pad cleared, and a new rocket on it
        if (rewindHere)
            _rewind = new RewindPoint(Capture(), Resumed: false, VesselId: null, mission, Site); // a rewind comes back to this, before the new rocket

        _flights++;
        var pad = _worlds.Need(SitesBody);
        var craft = pad.Launch(mission.Vehicle, Site);
        Controlled = new Vessel(_worlds, pad, mission, _sounds, craft, [.. mission.Vehicle.Stages.Select(s => s.FuelMass)], _nextId++, _flights) { Site = Site };
        Controlled.ReadyToLaunch();
        Controlled.ResetLandingLights(WorldTime);
        _shownEnded = null; // something new begun
        // Free play makes no run: its vessels belong to none, with nothing to judge or log.
        if (mission.HasObjectives)
        {
            var begun = new MissionRun(this, mission, Site, _flights, WorldTime) { LastFlownId = Controlled.Id };
            begun.Add(Controlled);
            // A vessel's flight number is what links it to its run: any already in the world with
            // this one's (put there with it) are the run's too, after its rocket.
            foreach (var vessel in _world)
                if (vessel.Flight == _flights)
                    begun.Add(vessel);
            _runs.Add(begun);
            AddToLog(RunEntry(begun, WorldLog.Begun));
        }
        _world.Add(Controlled);
        _worldChanges++;
        MakeRoom();
        Begun();
    }

    /// <summary>
    /// What a flight begun, or a world restored, starts afresh: the frame of reference, the target
    /// found again, and nothing warned of or slowed for; then <see cref="Restarted"/>.
    /// </summary>
    private void Begun()
    {
        FrameChoice = FrameChoice.Auto;
        ResolveTarget();
        // A new rocket, or a world brought back, until its first step looks ahead: what was warned of may be gone.
        (Warning, _warnedFor, _warnedCount) = (null, null, 0);
        Array.Clear(_warned); // holding no vessel of the world gone
        (NearMissWarning, _missesShownCount, _missReasonFor) = (null, 0, null);
        Array.Clear(_missesShown);
        // Nor is anything slowed for: what was may be gone, and the new flight starts at the
        // player's own rate (untouched by the slow-down) and full steps.
        (_forced, _missScale) = (null, 1f);
        if (Slowed is not null)
        {
            Slowed = null;
            _worlds.StepScale = 1f;
        }
        Restarted?.Invoke();
    }

    /// <summary>
    /// How soon a vessel goes to make room: debris (nothing guided) first, then a vessel of no run
    /// in progress, then a run's.
    /// </summary>
    private static int RoomRank(bool debris, bool inRun) => debris ? 0 : inRun ? 2 : 1;

    /// <summary>
    /// The vessels of a saved <paramref name="world"/> that go to bring it within its
    /// <see cref="Capacity"/>: debris first, then those of no run in progress, then runs' vessels,
    /// the oldest first within each, never <paramref name="flown"/>, the one to be flown. (A saved
    /// world's runs are only ever runs in progress, so a vessel's flight number alone says whether
    /// it is a run's.)
    /// </summary>
    private static HashSet<VesselState> Excess(WorldState world, VesselState? flown)
    {
        var goes = new HashSet<VesselState>(ReferenceEqualityComparer.Instance);
        var excess = world.Vessels.Count - Capacity;
        if (excess <= 0)
            return goes;
        var runNumbers = world.Runs.Select(r => r.Number).ToHashSet();
        foreach (var state in world.Vessels
            .Where(s => !ReferenceEquals(s, flown))
            .OrderBy(s => RoomRank(!Enumerable.Range(s.First, s.Last - s.First + 1).Any(i => s.Vehicle.Stages[i].Guided), runNumbers.Contains(s.Flight)))
            .Take(excess))
            goes.Add(state);
        return goes;
    }

    /// <summary>Takes the vessel at <paramref name="i"/> out of the world's list, and out of its run (its body is the caller's to see to).</summary>
    private void TakeOut(int i)
    {
        // (A run failed earlier in this step is no one's to RunOf, so keeps the vessel in its list:
        // it ends at the step's UpdateRuns all the same.)
        RunOf(_world[i])?.Remove(_world[i]);
        _world.RemoveAt(i);
        _worldChanges++;
    }

    /// <summary>How many vessels have been removed to keep the world within its <see cref="Capacity"/>, since it was loaded.</summary>
    public int RemovedForRoom { get; private set; }

    /// <summary>
    /// Takes the world back down to its <see cref="Capacity"/>, removing debris first, then vessels
    /// of no run in progress, then runs' vessels, the oldest first within each, but never the one
    /// being flown.
    /// </summary>
    private void MakeRoom()
    {
        while (_world.Count > Capacity)
        {
            var (i, rank) = (-1, int.MaxValue);
            for (var j = 0; j < _world.Count && rank > 0; j++)
            {
                if (_world[j] == Controlled)
                    continue;
                var r = RoomRank(!_world[j].Guided, RunOf(_world[j]) is not null);
                if (r < rank)
                    (i, rank) = (j, r);
            }
            if (i < 0)
                break;
            var vessel = _world[i];
            if (vessel.State != FlightState.Crashed) // a wreck's body is gone, or going by itself
            {
                LogRemoved(vessel, WorldLog.RemovedForRoom);
                vessel.Physics?.Remove(vessel.Craft);
            }
            else
                LogLoss(vessel, null); // a wreck lost its place before its loss was written
            TakeOut(i);
            RemovedForRoom++;
            ForgetCollisions(); // one the map may show is gone
        }
    }

    /// <summary>
    /// Writes each vessel newly crashed to the log, once (a wreck stays in the world until a new
    /// flight clears it, so each is marked as written). Checked once a step after everything that
    /// can wreck one: the physics' judging, and the formula's wrecks and collisions. Two wrecked by
    /// one collision (each other's) are one entry, though the physics may judge the second a step or
    /// more after the first (its own contact's beginning reported later): it is joined to the
    /// first's entry then. A step with nothing lost allocates nothing.
    /// </summary>
    private void LogLosses()
    {
        for (var i = 0; i < _world.Count; i++)
        {
            var vessel = _world[i];
            if (vessel.State != FlightState.Crashed || vessel.LossLogged)
                continue;
            var partner = HitAnother(vessel) ? Partner(vessel) : null;
            if (partner is { LossLogged: true })
            {
                // Written already: joined to its entry only if that names it alone. One that hit
                // two (a booster falling onto two touching ones) is in one pair's entry, and a
                // third joined to it would put the other out of the log: it is written on its own.
                if (Alone(partner))
                    Join(partner, vessel);
                else
                    LogLoss(vessel, null);
            }
            else
                LogLoss(vessel, partner);
        }
    }

    /// <summary>Whether <paramref name="v"/>'s entry in the log is its own, naming it alone (no other's loss written to it: the other may have gone from the world since, so the entry itself says).</summary>
    private static bool Alone(Vessel v) => v.LoggedAs is { } entry && entry.What == WorldLog.Named(v);

    private static bool HitAnother(Vessel v) => v.CrashReason?.StartsWith("Hit another vessel", StringComparison.Ordinal) ?? false;

    /// <summary>
    /// The other vessel lost in the same collision as <paramref name="v"/>, if there is one: wrecked
    /// by hitting another vessel too, and the one either actually hit (<see cref="Vessel.HitCraft"/>:
    /// not merely one wrecked near it), written already or not. A fairing half never is: it is not logged.
    /// </summary>
    private Vessel? Partner(Vessel v)
    {
        for (var i = 0; i < _world.Count; i++)
        {
            var other = _world[i];
            if (other != v && other.State == FlightState.Crashed && !other.IsFairing && HitAnother(other)
                && ((v.HitCraft is not null && other.Craft == v.HitCraft) || (other.HitCraft is not null && other.HitCraft == v.Craft)))
                return other;
        }
        return null;
    }

    private void LogLoss(Vessel vessel, Vessel? partner)
    {
        vessel.LossLogged = true;
        if (partner is not null)
            partner.LossLogged = true;
        if (vessel.IsFairing) // a fairing half coming down is debris, not a loss worth the player's log
            return;
        var entry = LossEntry(WorldTime, vessel, partner);
        vessel.LoggedAs = entry;
        if (partner is not null)
            partner.LoggedAs = entry;
        AddToLog(entry);
    }

    /// <summary>
    /// <paramref name="second"/>, wrecked by the collision <paramref name="first"/> was and written
    /// before it: the two made one entry where the first's is (when the first was lost), or, should
    /// that have gone from the log meanwhile, the second's own.
    /// </summary>
    private void Join(Vessel first, Vessel second)
    {
        for (var i = _log.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_log[i], first.LoggedAs))
            {
                second.LossLogged = true;
                var joined = LossEntry(_log[i].Time, first, second);
                (_log[i], first.LoggedAs, second.LoggedAs) = (joined, joined, joined);
                LogChanges++;
                return;
            }
        LogLoss(second, null);
    }

    /// <summary>The entry for <paramref name="vessel"/> lost at <paramref name="time"/>, with <paramref name="partner"/> if the two were lost to one collision.</summary>
    private static LogEntry LossEntry(double time, Vessel vessel, Vessel? partner) =>
        new(time,
            partner is null ? WorldLog.Named(vessel) : $"{WorldLog.Named(vessel)} and {WorldLog.Named(partner)}",
            WorldLog.How(vessel.CrashReason),
            // How fast it hit what destroyed it, where its reason says (a collision's closing speed,
            // a touch's; of two judged a step apart, the harder); else how fast it was going.
            partner is null
                ? WorldLog.SpeedIn(vessel.CrashReason) ?? vessel.CrashSpeed
                : MathF.Max(WorldLog.SpeedIn(vessel.CrashReason) ?? vessel.CrashSpeed, WorldLog.SpeedIn(partner.CrashReason) ?? partner.CrashSpeed),
            WorldLog.Where(vessel.Body, vessel.CrashPosition),
            vessel.CrashedWatched || (partner?.CrashedWatched ?? false));

    /// <summary>
    /// Writes <paramref name="vessel"/>, whole and about to be taken out of the world, to the log,
    /// <paramref name="how"/> (a fairing half aside, as for a loss): with no speed, as nothing hit
    /// it, and seen only if the last step chose it for the physics (see <see cref="LogEntry"/>):
    /// on the first step after a load every vessel is still in the physics, nobody having chosen.
    /// </summary>
    private void LogRemoved(Vessel vessel, string how)
    {
        if (!vessel.IsFairing)
            AddToLog(new LogEntry(WorldTime, WorldLog.Named(vessel), how, null, WorldLog.Where(vessel.Body, vessel.CentreOfMass), Unwatched.ShouldBeInPhysics(vessel)));
    }

    private void AddToLog(LogEntry entry)
    {
        if (_log.Count >= Scene.WorldLogEntries)
            _log.RemoveAt(0);
        _log.Add(entry);
        LogChanges++;
    }

    /// <summary>Whether <paramref name="vessel"/> is where a new rocket on the site's pad would hit it: on the pad, or over it no higher than a little above the tower (at the sites' body).</summary>
    private bool InTheWay(Vessel vessel)
    {
        if (vessel.Body != SitesBody)
            return false;
        // In the pad's own frame: round its centre, with up along +Y.
        var p = Site.ToLocal(vessel.CentreOfMass);
        var reach = Scene.PadHalfSize + PadClearance;
        return MathF.Abs(p.X) < reach && MathF.Abs(p.Z) < reach && p.Y > -reach && p.Y < Scene.TowerHeight + PadClearanceAbove;
    }
}
