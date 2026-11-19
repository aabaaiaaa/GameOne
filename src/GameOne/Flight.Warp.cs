namespace GameOne;

/// <summary>The time rate: the doubling ladder, the gate that holds it to the physics' top rate, and its drops.</summary>
internal sealed partial class Flight
{
    /// <summary>
    /// The rates time can run at, from slow motion to time warp, each double the last, from 1/4 to
    /// <see cref="Scene.TopRate"/> (the player steps through them with <see cref="FlightInput.Slower"/>
    /// and <see cref="FlightInput.Faster"/>): the physics' up to <see cref="Scene.PhysicsTopRate"/>,
    /// the formula's past it (<see cref="IsFormulaRate"/>).
    /// </summary>
    public static readonly float[] Rates = Ladder();

    private static float[] Ladder()
    {
        var rates = new List<float>();
        for (var rate = 0.25f; rate <= Scene.TopRate; rate *= 2f)
            rates.Add(rate);
        return [.. rates];
    }

    /// <summary>Normal speed's place among the <see cref="Rates"/>.</summary>
    private static readonly int NormalRate = Array.IndexOf(Rates, 1f);

    /// <summary>The physics' top rate's place among the <see cref="Rates"/> (<see cref="Scene.PhysicsTopRate"/>).</summary>
    internal static readonly int PhysicsTopIndex = Array.IndexOf(Rates, Scene.PhysicsTopRate);

    /// <summary>Whether <paramref name="rate"/> is one of the formula's, past the physics' top rate.</summary>
    public static bool IsFormulaRate(float rate) => rate > Scene.PhysicsTopRate;

    private int _rate = NormalRate;

    /// <summary>The rate the player has picked (see <see cref="Rates"/>): 1 to start with, and kept from flight to flight.</summary>
    public float PlayerRate => Rates[_rate];

    /// <summary>
    /// The rate the game runs at, everything on its clock (the flight, the effects, the camera,
    /// the small views): the player's own, or the forced slow-down's while one is on
    /// (<see cref="Slowed"/>) and slower.
    /// </summary>
    public float TimeScale => Slowed is { } slowed ? MathF.Min(PlayerRate, slowed.Scale) : PlayerRate;

    /// <summary>Whether the game runs at one of the formula's rates (<see cref="IsFormulaRate"/>).</summary>
    public bool ByFormula => IsFormulaRate(TimeScale);

    /// <summary>
    /// The witnessed vessel (the flown one, a small view's, the main camera's) holding the warp to
    /// the physics' top rate, if one is: the first flying in the air or low over the ground, or not
    /// yet at rest (neither <see cref="Vessel.AtRest"/> nor <see cref="Vessel.AboveTheAir"/>; a
    /// wreck flies nowhere, and holds nothing). Worked out as each frame begins, before the rate
    /// keys are taken: while it is set, <see cref="FlightInput.Faster"/> past the physics' top rate
    /// does nothing.
    /// </summary>
    /// <remarks>One burning counts as in the air: the formula doesn't fly a witnessed vessel's burn.</remarks>
    public Vessel? WarpHeldBy { get; private set; }

    /// <summary>The warp's last drop to the physics' top rate (<see cref="DropToPhysics"/>), for the HUD to say why, a while.</summary>
    public WarpDropped? LastDrop { get; private set; }

    /// <summary>The flight's own clock of real seconds (the frames' sum), by which <see cref="LastDrop"/> is timed.</summary>
    public double RealTime => _realTime;

    /// <summary>
    /// The world time the flight is warping to (<see cref="FlightInput.WarpTo"/>: a point on the
    /// flown vessel's path), if it is: at the fastest of the <see cref="Rates"/> the gate allows
    /// (<see cref="WarpHeldBy"/>) until the clock gets there, the last frame shortened so it stops
    /// there exactly, then at normal speed. It ends early, at normal speed, on a rate key, another
    /// vessel flown, a warning that drops the warp (<see cref="Warning"/>, <see cref="NearMissWarning"/>),
    /// the vessel no longer flying (landed, afloat, wrecked or gone from the solar system), or a
    /// world loaded or a flight begun, and at the physics' top rate on a drop to it
    /// (<see cref="LastDrop"/>); while the engine burns its time is held to where the path then
    /// ends. What the path's mark shows while it runs.
    /// </summary>
    public double? WarpingTo => _warpTo?.Time;

    private (double Time, Vessel For)? _warpTo;
    private readonly List<PathLeg> _warpLegs = new(Scene.PatchedLegs);

    /// <summary>How near the time warped to the clock must be to have got there: far within a step, as the frame's steps' float sum misses it by.</summary>
    private const double ArrivedSeconds = 1e-4;

    /// <summary>
    /// Begins warping to <paramref name="time"/> (see <see cref="WarpingTo"/>), no later than the
    /// flown vessel's path goes with its engine off (down to the ground, out to the edge), and only
    /// for a time still ahead of a vessel flying.
    /// </summary>
    private void BeginWarpTo(double time)
    {
        var vessel = Controlled;
        if (vessel.State != FlightState.Flying || !(time > WorldTime))
            return;
        time = Math.Min(time, PathEnd(vessel));
        if (!(time > WorldTime))
            return;
        _warpTo = (time, vessel);
        _rate = WarpHeldBy is null ? Rates.Length - 1 : PhysicsTopIndex;
    }

    /// <summary>When <paramref name="vessel"/>'s path, with its engine off from now, ends (<see cref="Patched.Ends"/>): infinity for one that goes round for ever.</summary>
    private double PathEnd(Vessel vessel)
    {
        Patched.Legs(vessel.Body, vessel.CentreOfMass, vessel.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity), WorldTime, _warpLegs);
        return Patched.Ends(_warpLegs);
    }

    /// <summary>
    /// While the vessel warped for burns, the warp's time held again (once a frame) to where its
    /// path now ends: a burn that brings it down sooner doesn't warp it on past that.
    /// </summary>
    private void HoldWarpToPath()
    {
        if (_warpTo is { } warp && warp.For == Controlled && Controlled.EngineLit)
        {
            var end = PathEnd(Controlled);
            if (end < warp.Time)
                _warpTo = (Math.Max(end, WorldTime), warp.For);
        }
    }

    /// <summary>
    /// Drops the time warp to normal speed, ending a warp to a point (<see cref="WarpingTo"/>) with
    /// it: the warnings' drop at the physics' rates.
    /// </summary>
    private void DropWarp()
    {
        _rate = NormalRate;
        _warpTo = null;
    }

    /// <summary>
    /// Drops the time warp from a formula's rate to the physics' top rate, for
    /// <paramref name="reason"/> (of the vessel <paramref name="of"/>, if it is a vessel's), ending a
    /// warp to a point with it, and keeps the drop as <see cref="LastDrop"/>; at the physics' rates
    /// it changes nothing. It never goes back up by itself: only the rate keys and a warp to a point raise it.
    /// </summary>
    private void DropToPhysics(WarpDrop reason, Vessel? of)
    {
        if (_rate <= PhysicsTopIndex)
            return;
        _rate = PhysicsTopIndex;
        _warpTo = null;
        LastDrop = new WarpDropped(reason, of is null ? "" : WorldLog.Named(of), Scene.PhysicsTopRate, _realTime);
    }

    /// <summary>
    /// The gate, as a frame begins (<see cref="WarpHeldBy"/>, of the flown vessel, the small views'
    /// <paramref name="watched"/> and the main camera's <paramref name="watching"/>, each counted
    /// from the frame it is watched in, its link-up still waiting or not), and the warp dropped to
    /// the physics' top rate if it is at a formula's while a vessel holds it: into the air, or near
    /// the ground of a body with none. The flown vessel at rest set moving by <paramref name="input"/>
    /// (launched, or its engine lit, with fuel to light it) drops it too, so no frame at a formula's
    /// rate runs its take-off; flying, the throttle drops nothing (a lit engine holds it, as the
    /// formula doesn't fly a witnessed vessel's burn).
    /// </summary>
    private void Gate(IReadOnlyList<Vessel>? watched, Vessel? watching, in FlightInput input)
    {
        var flown = Controlled;
        WarpHeldBy = Holds(flown) ? flown : null;
        for (var i = 0; WarpHeldBy is null && watched is not null && i < watched.Count; i++) // by index: a foreach over the interface would box its enumerator
            if (Holds(watched[i]))
                WarpHeldBy = watched[i];
        if (WarpHeldBy is null && watching is not null && Holds(watching))
            WarpHeldBy = watching;
        if (WarpHeldBy is { } held)
            DropToPhysics(held.Body.HasAir ? WarpDrop.Air : WarpDrop.Ground, held);
        else if (flown.AtRest && flown.Fuel > 0f && (input.Launch || input.FullThrottle || input.Throttle > 0f))
            DropToPhysics(WarpDrop.SetMoving, flown);
    }

    /// <summary>Whether <paramref name="v"/>, witnessed, holds the warp (see <see cref="WarpHeldBy"/>).</summary>
    private static bool Holds(Vessel v) =>
        v.State != FlightState.Crashed && !v.AtRest && !(v.AboveTheAir && !v.EngineLit);

    /// <summary>
    /// Whether every vessel has been taken out of the physics for the formula's rates
    /// (<see cref="EnterFormula"/>), and not yet given back to it (<see cref="LeaveFormula"/>).
    /// </summary>
    private bool _underFormula;

    /// <summary>How many windows the last frame at a formula's rate was cut into (<see cref="AdvanceByFormula"/>).</summary>
    internal int FormulaWindows { get; private set; }

    /// <summary>
    /// A frame at a formula's rate: <paramref name="dt"/> seconds of game time, every vessel (the
    /// flown one too) moved by formula, nothing of Jolt's stepped, in windows of at most
    /// <see cref="Scene.FormulaWindowSeconds"/> (<see cref="FormulaStep"/>), each cut short at the
    /// earliest event in it (<see cref="NextCut"/>). One-off inputs are taken in the first window
    /// only, held ones in every window, as <see cref="Advance"/> takes them; a frame of no time is
    /// still one window, of no time, so its inputs are taken, and a menu's choice is taken in a
    /// first window of no time. It stops early at a window's end once
    /// <paramref name="budgetSeconds"/> of real time has gone, and once the rate is no longer a
    /// formula's (a drop, a menu's choice, a warp to a point there): the vessels are given back to
    /// the physics then (<see cref="LeaveFormula"/>), and the rest of the frame isn't run. Returns
    /// the game time it advanced.
    /// </summary>
    private float AdvanceByFormula(float dt, in FlightInput input, double budgetSeconds)
    {
        _sounds.Clear();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var held = HeldOf(input);
        var (done, windows) = (0.0, 0);
        // A menu's choice (a mission begun, one carried on, started again or rewound) is taken in a
        // window of no time: the world it leaves (one brought back, a new rocket on its pad) is in
        // the physics, back at normal speed, and no window's rules may run on it unstepped.
        var choice = input.Choose is not null || input.CarryOn is not null || input.StartAgain || input.Rewind;
        do
        {
            var to = windows == 0 && choice ? WorldTime : NextCut(WorldTime, WorldTime + Math.Min(Scene.FormulaWindowSeconds, dt - done));
            var each = (float)(to - WorldTime);
            FormulaStep(each, windows == 0 ? input : held);
            done += each;
            windows++;
            if (!ByFormula)
            {
                LeaveFormula(input);
                break;
            }
        }
        while (dt - done > LeftOverSeconds && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds < budgetSeconds);
        FormulaWindows = windows;
        return (float)done;
    }

    /// <summary>What of a frame at a formula's rate may be left unrun, as the windows' float lengths sum to it within a hair: far within <see cref="ArrivedSeconds"/>.</summary>
    private const double LeftOverSeconds = 1e-5;

    /// <summary>
    /// The earliest moment in (<paramref name="from"/>, <paramref name="to"/>] a frame at a formula's
    /// rate must be cut at, so what comes then is taken at its moment: the warp to a point's time,
    /// and the earliest crossing of a sphere's edge by a vessel on its orbit
    /// (<see cref="FormulaMotion.CrossesAt"/>), so it is handed across then (<see cref="Cross"/>, at
    /// the window's end), never a window late; else <paramref name="to"/>. (A stepped vessel's
    /// crossing is asked at each window's end.)
    /// </summary>
    private double NextCut(double from, double to)
    {
        if (_warpTo is { } warp && warp.Time > from && warp.Time < to)
            to = warp.Time;
        for (var i = 0; i < _world.Count; i++)
        {
            var v = _world[i];
            if (v.OnFormula && v.State == FlightState.Flying && v.Motion.CrossesAt is var crosses && crosses > from && crosses < to)
                to = crosses;
        }
        return to;
    }

    /// <summary>
    /// One window of a frame at a formula's rate (<see cref="AdvanceByFormula"/>): what a step does
    /// after the input (<see cref="Step"/>), but that every vessel is moved by formula and nothing of
    /// Jolt's is stepped: the vessels' rules once, their moves and the collisions between them along
    /// their ways across the window (<see cref="FormulaCollisions"/>: each one's start kept, then
    /// the window judged once all are at its end), their legs and doors, then their crossings, the
    /// edge, the bodies asked ahead, the target, the losses and the runs. Not the look ahead nor the
    /// handover (nothing is in the physics to hand over), nor <see cref="Unwatched.FindCollisions"/>
    /// (its quarter-second looks ahead are the physics' rates').
    /// </summary>
    private void FormulaStep(float dt, in FlightInput input)
    {
        Timings.BeginStep();
        var from = WorldTime;
        WorldTime += dt;
        _bodiesAt = WorldTime; // nothing in the physics is behind: it isn't stepped
        TakeChoices(input);
        RunVessels(dt, input);
        Rafts.Time = WorldTime; // the rafts' poses, which the physics sets as it steps, at the window's end
        FormulaCollisions.Begin(from);
        Unwatched.Move();
        FormulaCollisions.Judge(from, WorldTime);
        Timings.Lap(StepPart.Rules);
        foreach (var vessel in _world)
            vessel.AfterPhysics(dt, WorldTime);
        Cross(WorldTime);
        LeaveTheSystem(WorldTime);
        var watching = input.Watching is { } on && _world.Contains(on) ? on : null;
        AskAhead(WorldTime, Witnessed(input.Watched, watching));
        ResolveTarget();
        KeepFrame();
        LogLosses();
        UpdateRuns();
        KeepWarpTo();
        Timings.EndStep(_worlds.TileCount);
    }

    /// <summary>
    /// Takes every vessel out of the physics for the formula's rates, each under formula from the
    /// moment its motion is at (its world's, <see cref="Worlds.Lag"/> behind the clock), and every
    /// wreck's body out at once (nothing is stepped to play it out); then nothing is behind the
    /// clock (<see cref="Worlds.ResetLag"/>). The worlds themselves are kept, not stepped, so not
    /// released: the drop finds them made.
    /// </summary>
    private void EnterFormula()
    {
        var at = WorldTime - _worlds.Lag;
        _worlds.FlushWrecks();
        for (var i = 0; i < _world.Count; i++)
            _world[i].LeavePhysics(at);
        _worlds.ResetLag();
        _underFormula = true;
    }

    /// <summary>Whether anything not wrecked is in the physics (at a formula's rate, to be taken out of it: <see cref="EnterFormula"/>).</summary>
    private bool AnyInPhysics()
    {
        for (var i = 0; i < _world.Count; i++)
            if (!_world[i].OnFormula && _world[i].State != FlightState.Crashed)
                return true;
        return false;
    }

    /// <summary>
    /// Gives the vessels back to the physics as the rate drops from a formula's: the flown vessel
    /// first, then each witnessed (by <paramref name="input"/>: the small views' and the main
    /// camera's), each where the formula has it now; the rest come back as the handover
    /// (<see cref="Unwatched.Update"/>) wants them, as at the physics' rates. The collisions under
    /// formula carry on from now (<see cref="Unwatched.CarryOnFrom"/>), not afresh.
    /// </summary>
    private void LeaveFormula(in FlightInput input)
    {
        _underFormula = false;
        _bodiesAt = WorldTime;
        Unwatched.Return(Controlled, WorldTime);
        var watching = input.Watching is { } on && _world.Contains(on) ? on : null;
        var witnessed = Witnessed(input.Watched, watching);
        for (var i = 0; witnessed is not null && i < witnessed.Count; i++) // by index: a foreach over the interface would box its enumerator
            if (_world.Contains(witnessed[i]))
                Unwatched.Return(witnessed[i], WorldTime);
        Unwatched.CarryOnFrom(WorldTime);
    }

    /// <summary>
    /// A flight begun, a world brought back or a vessel carried on at a formula's rate comes back
    /// at normal speed, flown in the physics; at the physics' rates the rate is kept, as it is
    /// from flight to flight.
    /// </summary>
    private void BackToThePhysics()
    {
        if (_rate > PhysicsTopIndex)
            DropWarp();
    }

    /// <summary>
    /// Ends a warp to a point at normal speed whose vessel is no longer the one flown, or is about
    /// to be (a switch's link-up begun: the vessel is flown only once it is through), or is no
    /// longer flying: landed, settled afloat, wrecked or gone from the system.
    /// </summary>
    private void KeepWarpTo()
    {
        if (_warpTo is { } warp && (warp.For != Controlled || LinkUp.Waiting || warp.For.State != FlightState.Flying))
            DropWarp();
    }
}
