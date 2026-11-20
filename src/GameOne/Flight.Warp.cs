using System.Numerics;
using GameOne.Physics;

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
    /// wreck flies nowhere, and holds nothing; one burning above the air holds nothing either, as
    /// the formula flies its burn: see <see cref="BurnByFormula"/>). Worked out as each frame begins, before the rate
    /// keys are taken: while it is set, <see cref="FlightInput.Faster"/> past the physics' top rate
    /// does nothing.
    /// </summary>
    public Vessel? WarpHeldBy { get; private set; }

    /// <summary>
    /// Who holds the warp and why, for the HUD's time line: <see cref="WarpHeldBy"/>, named as the
    /// log names it, and what it is doing (<see cref="HeldDoing"/>); else a fast collision coming
    /// at a witnessed vessel (<see cref="CollisionHeld"/>), by the vessel and what it would hit
    /// ("a collision coming at Satellite 1 with Satellite 2"); or none. Made only as any of it
    /// changes, so a frame held allocates nothing for it.
    /// </summary>
    public string? WarpHeld
    {
        get
        {
            if (WarpHeldBy is { } held)
            {
                var doing = HeldDoing(held);
                if (_warpHeld.Of != held || !ReferenceEquals(_warpHeld.Doing, doing))
                    _warpHeld = (held, doing, $"{WorldLog.Named(held)} {doing}");
                return _warpHeld.Line;
            }
            if (CollisionHeld is not { } collision)
                return null;
            if (!ReferenceEquals(_warpHeld.Doing, collision))
                _warpHeld = (null, collision, $"a collision coming at {collision}");
            return _warpHeld.Line;
        }
    }

    private (Vessel? Of, string? Doing, string? Line) _warpHeld;

    /// <summary>
    /// While a fast collision coming at a witnessed vessel holds the warp to the physics' top rate
    /// (see <see cref="CollisionHolds"/>), that vessel and what it would hit, as the log and the
    /// warnings name them ("Satellite 1 with Satellite 2", "Booster 0 with the ground"): the
    /// readout's <c>Collision hold</c>, and the time line's held reason (<see cref="WarpHeld"/>).
    /// Made only as the collision changes, so a frame held allocates nothing for it.
    /// </summary>
    public string? CollisionHeld => _collisionHeld is { } hold ? HeldLine(ref _collisionHeldLine, hold) : null;

    private (Vessel? Of, Vessel? Other, string? What, string? Line) _collisionHeldLine;

    /// <summary>
    /// <see cref="CollisionHeld"/>'s line for <paramref name="hold"/>, kept in <paramref name="kept"/>
    /// and made again only as the vessel, the other or what names the place hit changes
    /// (<see cref="Collisions.Name(GroundBody, SolidKind, Vector3)"/>, one of a few fixed names,
    /// asked each time: the ground's way down moving out over the sea is "the water", onto the
    /// moon "the moon"), so a frame held allocates nothing for it.
    /// </summary>
    internal static string HeldLine(ref (Vessel? Of, Vessel? Other, string? What, string? Line) kept,
        (Vessel Of, Vessel? Other, GroundBody Body, SolidKind With, Vector3 At) hold)
    {
        var what = hold.Other is null ? Collisions.Name(hold.Body, hold.With, hold.At) : null;
        if (kept.Line is null || kept.Of != hold.Of || kept.Other != hold.Other || !ReferenceEquals(kept.What, what))
            kept = (hold.Of, hold.Other, what, $"{WorldLog.Named(hold.Of)} with {what ?? WorldLog.Named(hold.Other!)}");
        return kept.Line!;
    }

    /// <summary>
    /// What <paramref name="held"/>, holding the warp, is doing: not yet at rest (landed or afloat
    /// but still settling, or touching the ground or in the water as it flies), else flying in the
    /// air, or low over the ground of a body with none.
    /// </summary>
    internal static string HeldDoing(Vessel held) =>
        held.State != FlightState.Flying || held.Grounded || (!held.OnFormula && held.InWater) ? "not yet at rest"
        : held.Body.HasAir ? "flying in the air"
        : "flying low over the ground";

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
    /// for a time still ahead of a vessel flying: at the physics' top rate if <paramref name="held"/>
    /// (a witnessed vessel holds the warp, or it dropped there as this frame began).
    /// </summary>
    private void BeginWarpTo(double time, bool held)
    {
        var vessel = Controlled;
        if (vessel.State != FlightState.Flying || !(time > WorldTime))
            return;
        time = Math.Min(time, PathEnd(vessel));
        if (!(time > WorldTime))
            return;
        _warpTo = (time, vessel);
        _rate = held ? PhysicsTopIndex : Rates.Length - 1;
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
    /// <paramref name="reason"/> (of the vessel <paramref name="of"/>: the flown one for a key's), ending a
    /// warp to a point with it, and keeps the drop as <see cref="LastDrop"/>; at the physics' rates
    /// it changes nothing. It never goes back up by itself: only the rate keys and a warp to a point raise it.
    /// </summary>
    private void DropToPhysics(WarpDrop reason, Vessel of)
    {
        if (_rate <= PhysicsTopIndex)
            return;
        _rate = PhysicsTopIndex;
        _warpTo = null;
        LastDrop = new WarpDropped(reason, WorldLog.Named(of), Scene.PhysicsTopRate, _realTime);
    }

    /// <summary>
    /// A switch asked to <paramref name="next"/> (<see cref="SwitchTarget"/>), as the frame that takes
    /// it begins: any warp, the physics' rates' or a formula's, drops to normal speed, ending a warp
    /// to a point with it, and kept as <see cref="LastDrop"/> (<see cref="WarpDrop.Switched"/>), so
    /// the frame runs no formula's window past the switch, nothing is carried on by formula after
    /// it, and the vessel is flown where it was. Slow motion is left as it is. Like every drop it
    /// never climbs back by itself.
    /// </summary>
    private void DropForSwitch(Vessel next)
    {
        if (_rate <= NormalRate && _warpTo is null)
            return;
        _rate = Math.Min(_rate, NormalRate);
        _warpTo = null;
        LastDrop = new WarpDropped(WarpDrop.Switched, WorldLog.Named(next), Rates[_rate], _realTime);
    }

    /// <summary>
    /// The gate, as a frame begins: who holds the warp (<see cref="WarpHeldBy"/>), of the flown
    /// vessel and the rest of the witnessed (<paramref name="witnessed"/>, <see cref="WarpWitnessed"/>),
    /// each counted from the frame it is watched in (a view opened, the camera moved, a switch
    /// asked: each takes effect between frames), its link-up still waiting or not; and whether a
    /// fast collision coming at one holds it (<see cref="CollisionHolds"/>). What drops the warp for
    /// it, and for the input, is <see cref="DropFor"/>'s.
    /// </summary>
    private void Gate(IReadOnlyList<Vessel>? witnessed)
    {
        var flown = Controlled;
        WarpHeldBy = Holds(flown) ? flown : null;
        for (var i = 0; WarpHeldBy is null && witnessed is not null && i < witnessed.Count; i++) // by index: a foreach over the interface would box its enumerator
            if (Holds(witnessed[i]))
                WarpHeldBy = witnessed[i];
        _collisionHeld = CollisionHolds(witnessed);
    }

    /// <summary>Whether a fast collision coming at a witnessed vessel holds the warp to the physics' top rate this frame (see <see cref="CollisionHolds"/>).</summary>
    private bool HeldForCollision => _collisionHeld is not null;

    /// <summary>The fast collision holding the warp this frame, if one is (see <see cref="CollisionHolds"/>): the vessel it comes at, the other (none for the ground, the sea, a raft, the tower or a mast), and what names that.</summary>
    private (Vessel Of, Vessel? Other, GroundBody Body, SolidKind With, Vector3 At)? _collisionHeld;

    /// <summary>How far ahead a collision the warp dropped for is still looked for while it holds the warp (see <see cref="CollisionHolds"/>): the physics' horizon and the drop's lead, as far as the drop put it, and the lead again to spare.</summary>
    private const float HeldCollisionSeconds = Scene.CollisionHorizonSeconds + 2f * Scene.WarpDropLeadSeconds;

    /// <summary>
    /// The fast collision coming at the flown vessel or another of the <paramref name="witnessed"/>
    /// that holds the warp, if one does: warned of by the physics (<see cref="Warning"/>, or a small
    /// view's coming, <see cref="ComingAt"/>), or one the warp dropped for (<see cref="_warpWarned"/>)
    /// still predicted (<see cref="Collisions.StillComing"/>, within <see cref="HeldCollisionSeconds"/>)
    /// and closing faster than <see cref="Scene.FastCollisionSpeed"/>. While one is, the warp goes
    /// no faster than the physics' top rate, so it is never passed by formula and its slow-down
    /// plays out in full; the time line says why (<see cref="WarpHeld"/>). A pair dropped for that
    /// is no longer predicted (dodged) holds nothing and is forgotten as dropped for, so coming
    /// again it drops the warp again. A near miss holds nothing: it drops the warp once, and may be
    /// warped past by hand.
    /// </summary>
    private (Vessel Of, Vessel? Other, GroundBody Body, SolidKind With, Vector3 At)? CollisionHolds(IReadOnlyList<Vessel>? witnessed)
    {
        if (Warning is { } warning)
            return (warning.Watched, warning.Other, warning.Body, warning.With, warning.At);
        foreach (var coming in _coming.All)
            if (!coming.NearMiss)
                return (coming.Watched, coming.Other, coming.Body, coming.With, coming.At);
        for (var i = _warpWarnedCount - 1; i >= 0; i--)
        {
            var ((of, other), collision) = (_warpWarned[i], _warpWarnedCollision[i]);
            if (!collision || (of != Controlled && !IsWatched(of!, witnessed))
                || (other!.Velocity - of!.Velocity).Length() <= Scene.FastCollisionSpeed)
                continue; // (a slow one is kept as dropped for, holding nothing: two drifting side by side drop it once)
            if (!Collisions.StillComing(of, other, HeldCollisionSeconds))
            {
                ForgetWarpWarned(i); // dodged: coming again, it drops the warp again
                continue;
            }
            return (of, other, of.Body, SolidKind.Craft, other.CentreOfMass);
        }
        return null;
    }

    /// <summary>
    /// The witnessed vessels but the flown one, for the warp (its gate, its drops and the rules run
    /// along each one's moves at a formula's rate): the small views' <paramref name="watched"/>, the
    /// main camera's <paramref name="watching"/> and the vessel a switch's link-up waits on, each
    /// whether its link-up still waits or not (at a formula's rate nothing is in the physics, so
    /// nothing is made for it meanwhile, as <see cref="Witnessed"/> guards against at the
    /// physics' rates). None and an empty list are alike.
    /// </summary>
    private IReadOnlyList<Vessel>? WarpWitnessed(IReadOnlyList<Vessel>? watched, Vessel? watching)
    {
        var pending = LinkUp is { Waiting: true, Vessel: { } to } && to != Controlled && _world.Contains(to) && !IsWatched(to, watched) ? to : null;
        var camera = watching is not null && watching != Controlled && watching != pending && !IsWatched(watching, watched) ? watching : null;
        if (pending is null && camera is null)
            return watched;
        _warpWitnessed.Clear();
        for (var i = 0; watched is not null && i < watched.Count; i++) // by index: a foreach over the interface would box its enumerator
            _warpWitnessed.Add(watched[i]);
        if (pending is not null)
            _warpWitnessed.Add(pending);
        if (camera is not null)
            _warpWitnessed.Add(camera);
        return _warpWitnessed;
    }

    private readonly List<Vessel> _warpWitnessed = new(Scene.MaxSmallViews + 2);

    /// <summary>
    /// What drops the warp as a frame begins, after the gate (<see cref="Gate"/>) and before the rate
    /// keys and the frame's advance, so the input is then taken in the physics: a witnessed vessel
    /// holding it (<see cref="WarpHeldBy"/>: into the air, or near the ground of a body with none);
    /// any of the flown vessel's actions (launched, staged, its legs or bay doors moved), whatever
    /// it is doing, as the formula stages nothing and moves no legs; its throttle raised while it is
    /// at rest with fuel to light it, so no frame at a formula's rate runs its take-off (flying, the
    /// throttle drops nothing); and a steering key, so the keys always steer as in the physics. Null
    /// for none. The drop is for the vessel holding it if it is that, else the flown one. (A switch
    /// drops any warp to normal speed before this is asked: <see cref="DropForSwitch"/>.)
    /// Asked at every rate: at a formula's it drops the warp; at the physics' it keeps the rate key
    /// and a warp to a point in the same frame from taking it past them, so the input still runs in
    /// the physics.
    /// </summary>
    private (WarpDrop Reason, Vessel Of)? DropFor(in FlightInput input)
    {
        if (WarpHeldBy is { } held)
            return (held.Body.HasAir ? WarpDrop.Air : WarpDrop.Ground, held);
        var flown = Controlled;
        if (input.Launch || input.Stage || input.ToggleLegs || input.ToggleBayDoors)
            return (WarpDrop.SetMoving, flown);
        if (flown.AtRest && flown.Fuel > 0f && (input.FullThrottle || input.Throttle > 0f))
            return (WarpDrop.SetMoving, flown);
        if (input.Lean.LengthSquared() > SteeringLean * SteeringLean)
            return (WarpDrop.Steering, flown);
        return null;
    }

    /// <summary>The vessel <paramref name="input"/> asks to be flown (<see cref="FlightInput.SwitchTo"/>, or the next one, <see cref="FlightInput.SwitchVessel"/>), if one will be: as <see cref="TakeChoices"/> will find it.</summary>
    private Vessel? SwitchTarget(in FlightInput input) =>
        input.SwitchTo is { } chosen ? (Flyable(chosen) ? chosen : null)
        : input.SwitchVessel ? NextToFly()
        : null;

    /// <summary>How long a lean (<see cref="FlightInput.Lean"/>) must be to be a steering key's (see <see cref="DropFor"/>): any at all, past a float's noise.</summary>
    private const float SteeringLean = 1e-4f;

    /// <summary>Whether <paramref name="v"/>, witnessed, holds the warp (see <see cref="WarpHeldBy"/>).</summary>
    private static bool Holds(Vessel v) =>
        v.State != FlightState.Crashed && !v.AtRest && !v.AboveTheAir;

    /// <summary>
    /// Whether every vessel has been taken out of the physics for the formula's rates
    /// (<see cref="EnterFormula"/>), and not yet given back to it (<see cref="LeaveFormula"/>).
    /// </summary>
    private bool _underFormula;

    /// <summary>How many windows the last frame was cut into at a formula's rate (<see cref="AdvanceByFormula"/>): 0 for a frame at the physics' rates.</summary>
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
        var watching = input.Watching is { } on && _world.Contains(on) ? on : null;
        do
        {
            _cutFor = null;
            Timings.BeginStep(); // the window's cut is asked within its step, counted in its rules
            var to = windows == 0 && choice ? WorldTime : NextCut(WorldTime, WorldTime + Math.Min(Scene.FormulaWindowSeconds, dt - done), WarpWitnessed(input.Watched, watching), input.NearMisses);
            var each = (float)(to - WorldTime);
            FormulaStep(each, windows == 0 ? input : held);
            done += each;
            windows++;
            // Cut at a drop's moment: dropped there, and the rest of the frame not run.
            TakeCut();
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

    /// <summary>The drop a window was cut for (<see cref="_cutFor"/>), if one was: dropped, its node or other vessel kept as dropped for.</summary>
    private void TakeCut()
    {
        if (_cutFor is not { } cut)
            return;
        if (cut.Node is { } node)
            _nodesDropped.Add((cut.Of, node));
        if (cut.Other is { } other)
        {
            WarpWarned(cut.Of, other, cut.Reason == WarpDrop.Collision);
            if (cut.Reason == WarpDrop.Collision && (other.Velocity - cut.Of.Velocity).Length() > Scene.FastCollisionSpeed)
                _collisionHeld ??= (cut.Of, other, cut.Of.Body, SolidKind.Craft, other.CentreOfMass); // held from the drop on, as the next frame's gate finds it
        }
        DropToPhysics(cut.Reason, cut.Of);
    }

    /// <summary>
    /// As a formula's rate is taken with vessels still in the physics (<see cref="EnterFormula"/>
    /// to come), whether a drop is due already (<see cref="NextCut"/>'s, cut at once: a node, a
    /// collision or near miss, the air or the fuel within its lead): if so it is dropped now, and the
    /// frame runs in the physics, so the whole bubble isn't handed out to the formula and straight
    /// back for a window of no time.
    /// </summary>
    private bool DropDueNow(in FlightInput input)
    {
        var watching = input.Watching is { } on && _world.Contains(on) ? on : null;
        _cutFor = null;
        NextCut(WorldTime, WorldTime, WarpWitnessed(input.Watched, watching), input.NearMisses);
        if (_cutFor is null)
            return false;
        TakeCut();
        return true;
    }

    /// <summary>What of a frame at a formula's rate may be left unrun, as the windows' float lengths sum to it within a hair: far within <see cref="ArrivedSeconds"/>.</summary>
    private const double LeftOverSeconds = 1e-5;

    /// <summary>
    /// The earliest moment in (<paramref name="from"/>, <paramref name="to"/>] a frame at a formula's
    /// rate must be cut at, so what comes then is taken at its moment: the warp to a point's time,
    /// and the earliest crossing of a sphere's edge by a vessel on its orbit
    /// (<see cref="FormulaMotion.CrossesAt"/>), so it is handed across then (<see cref="Cross"/>, at
    /// the window's end), never a window late, and likewise its reaching the solar system's edge
    /// (<see cref="FormulaMotion.LeavesAt"/>), and the moment any burn under formula runs out of
    /// fuel; else <paramref name="to"/>. (A stepped vessel's
    /// crossing is asked at each window's end.) And a drop's moment (<see cref="DropCut"/>,
    /// <see cref="NodeCut"/>), for the flown vessel and each of the rest of the
    /// <paramref name="witnessed"/> (<see cref="WarpWitnessed"/>, as the gate counts them), kept as
    /// <see cref="_cutFor"/> if the cut is there: <paramref name="from"/>
    /// itself for one already due, a window of no time. A collision or, with
    /// <paramref name="nearMisses"/> (the player's setting), a near miss coming at one is among them
    /// (<see cref="ComingCut"/>).
    /// </summary>
    private double NextCut(double from, double to, IReadOnlyList<Vessel>? witnessed, bool nearMisses)
    {
        if (_warpTo is { } warp && warp.Time > from && warp.Time < to)
            to = warp.Time;
        for (var i = 0; i < _world.Count; i++)
        {
            var v = _world[i];
            if (!v.OnFormula || v.State != FlightState.Flying)
                continue;
            if (v.Motion.CrossesAt is var crosses && crosses > from && crosses < to)
                to = crosses;
            if (v.Motion.LeavesAt is var leaves && leaves > from && leaves < to)
                to = leaves; // lost at the solar system's edge then (LeaveTheSystem, at the window's end), never a window late
            // And where a burn runs out of fuel, so no way across a window bends there (its
            // collisions' Hermite way is one curve: see FormulaCollisions).
            if (v.EngineLit && !v.Motion.Standing && from + v.FuelSeconds is var dry && dry > from && dry < to)
                to = dry;
        }
        var flown = Controlled;
        // Looked for over the window and, past it, as far as the drop's moment could be from its end.
        var horizon = to - from + Scene.CollisionHorizonSeconds + Scene.WarpDropLeadSeconds;
        DropCut(flown, from, ref to);
        ComingCut(flown, from, horizon, nearMisses, ref to);
        for (var i = 0; witnessed is not null && i < witnessed.Count; i++) // by index: a foreach over the interface would box its enumerator
        {
            DropCut(witnessed[i], from, ref to);
            ComingCut(witnessed[i], from, horizon, nearMisses, ref to);
        }
        NodeCut(flown, from, ref to);
        return to;
    }

    /// <summary>
    /// The drop a frame at a formula's rate is cut for in the window being run (see <see cref="NextCut"/>):
    /// why, for which vessel, the node it is for and the other vessel coming at it, if one is.
    /// </summary>
    private (WarpDrop Reason, Vessel Of, ManoeuvreChain? Node, Vessel? Other)? _cutFor;

    /// <summary>
    /// Cuts the frame for a collision or near miss coming at witnessed <paramref name="v"/>
    /// (<see cref="Collisions.AheadOfTheWarp"/>, within <paramref name="horizon"/> of
    /// <paramref name="from"/>, from another vessel not yet dropped for): at its moment less
    /// <see cref="Scene.CollisionHorizonSeconds"/> and <see cref="Scene.WarpDropLeadSeconds"/>, so the
    /// physics' look ahead (its horizon) has it from the drop's moment on, at the physics' top rate,
    /// and its own warning there drops the warp to normal speed in time for its slow-down; if that
    /// is by <paramref name="to"/>.
    /// </summary>
    private void ComingCut(Vessel v, double from, double horizon, bool nearMisses, ref double to)
    {
        if (_collisions.AheadOfTheWarp(v, _world, from, horizon, nearMisses, _warpWarned.AsSpan(0, _warpWarnedCount)) is not { } coming)
            return;
        var at = Math.Max(from, coming.At - Scene.CollisionHorizonSeconds - Scene.WarpDropLeadSeconds);
        if (at <= to)
            (to, _cutFor) = (at, (coming.NearMiss ? WarpDrop.NearMiss : WarpDrop.Collision, v, null, coming.Other));
    }

    /// <summary>
    /// Each other vessel whose collision or near miss coming at a witnessed one has dropped the warp
    /// (see <see cref="ComingCut"/>), with the witnessed vessel it came at: each pair only once, so
    /// raising the warp again by hand isn't undone, while the same other coming at another witnessed
    /// vessel still drops it. Each is forgotten as it is over (<see cref="ForgetWarpWarned"/>). Room
    /// for a few, the oldest going, as <see cref="_warned"/>'s, so no window allocates.
    /// </summary>
    private readonly (Vessel? Of, Vessel? Other)[] _warpWarned = new (Vessel?, Vessel?)[4];
    private readonly bool[] _warpWarnedCollision = new bool[4]; // each pair's: a collision, not a near miss (see CollisionHolds)
    private int _warpWarnedCount;

    /// <summary>Keeps <paramref name="other"/> as having dropped the warp coming at <paramref name="of"/> (see <see cref="_warpWarned"/>).</summary>
    private void WarpWarned(Vessel of, Vessel other, bool collision)
    {
        if (_warpWarnedCount == _warpWarned.Length) // the oldest goes
        {
            Array.Copy(_warpWarned, 1, _warpWarned, 0, _warpWarned.Length - 1);
            Array.Copy(_warpWarnedCollision, 1, _warpWarnedCollision, 0, _warpWarnedCollision.Length - 1);
            _warpWarnedCount--;
        }
        _warpWarnedCollision[_warpWarnedCount] = collision;
        _warpWarned[_warpWarnedCount++] = (of, other);
    }

    /// <summary>
    /// Forgets, as every frame begins and whatever the rate, each pair that dropped the warp
    /// (<see cref="_warpWarned"/>) whose coming is over: either wrecked or gone from the world, the
    /// two at different bodies, or moving apart (passed, or turned away) beyond
    /// <see cref="Collisions.WarpReach"/>: their sizes, or a near miss's reach if that is larger,
    /// whatever the near-miss setting, so with it off a collision found within their sizes is
    /// forgotten only further out than that. So one coming again later drops
    /// it again. Two drifting side by side within their reach stay one coming however their
    /// closing wavers about nothing, so it drops the warp once.
    /// </summary>
    private void ForgetWarpWarned()
    {
        for (var i = _warpWarnedCount - 1; i >= 0; i--)
        {
            var (of, other) = (_warpWarned[i].Of!, _warpWarned[i].Other!);
            var over = !_world.Contains(other) || !_world.Contains(of) || GameOne.Comings.Lost(other) || GameOne.Comings.Lost(of)
                || other.Body != of.Body
                || (Vector3.Distance(Collisions.Middle(other), Collisions.Middle(of)) > Collisions.WarpReach(of, other)
                    && Vector3.Dot(other.CentreOfMass - of.CentreOfMass, other.Velocity - of.Velocity) > 0f);
            if (over)
                ForgetWarpWarned(i);
        }
    }

    /// <summary>Forgets the <paramref name="i"/>th pair that dropped the warp (see <see cref="_warpWarned"/>).</summary>
    private void ForgetWarpWarned(int i)
    {
        Array.Copy(_warpWarned, i + 1, _warpWarned, i, _warpWarnedCount - i - 1);
        Array.Copy(_warpWarnedCollision, i + 1, _warpWarnedCollision, i, _warpWarnedCount - i - 1);
        _warpWarned[--_warpWarnedCount] = default; // holding no vessel no longer warned of
    }

    /// <summary>
    /// Cuts the frame <see cref="Scene.WarpDropLeadSeconds"/> ahead of witnessed <paramref name="v"/>'s
    /// way, on its orbit, coming down to its floor (<see cref="Formula.FloorRadius"/>: into the air,
    /// or within reach of an airless body's ground), if that is by <paramref name="to"/>. Burning, of
    /// its burn's way down to it (<see cref="Formula.BurnComesDownAt"/>, looked along as far as the
    /// drop's moment could be from the window's end) and of its fuel running out
    /// (<see cref="WarpDrop.FuelOut"/>), whichever is sooner. One still in the physics (asked as the
    /// formula's rates are taken: <see cref="DropDueNow"/>) by the orbit it is on now.
    /// </summary>
    private void DropCut(Vessel v, double from, ref double to)
    {
        if (v.State != FlightState.Flying)
            return;
        var standing = v.OnFormula && v.Motion.Standing;
        if (v.EngineLit && !standing)
        {
            var dry = from + v.FuelSeconds - Scene.WarpDropLeadSeconds;
            var low = Formula.BurnComesDownAt(v, from, to - from + Scene.WarpDropLeadSeconds) is { } d ? d - Scene.WarpDropLeadSeconds : double.PositiveInfinity;
            var (when, why) = low <= dry ? (low, v.Body.HasAir ? WarpDrop.Air : WarpDrop.Ground) : (dry, WarpDrop.FuelOut);
            var cut = Math.Max(from, when);
            if (cut <= to)
                (to, _cutFor) = (cut, (why, v, null, null));
            return;
        }
        if (v.OnFormula && (v.Motion.Stepping || standing))
            return; // stepped unlit (in the air), or standing, it holds the warp at the frame's start: no way on its orbit to cut at
        var floor = Formula.FloorRadius(v);
        var down = v.OnFormula
            ? Formula.ComesDownAt(v.Motion.OnOrbit, floor)
            : new Orbit(v.Body, v.CentreOfMass, v.Body.AmongStars(v.CentreOfMass, v.Velocity), findEnd: false).FallingTo(floor) is { } seconds ? from + seconds : null;
        if (down is not { } comes)
            return;
        var at = Math.Max(from, comes - Scene.WarpDropLeadSeconds);
        if (at <= to)
            (to, _cutFor) = (at, (v.Body.HasAir ? WarpDrop.Air : WarpDrop.Ground, v, null, null));
    }

    /// <summary>
    /// The nodes that have dropped the warp once (see <see cref="NodeCut"/>), by the vessel whose
    /// chain it is, each forgotten as soon as that vessel's chain is no longer the one kept
    /// (<see cref="ForgetNodesDropped"/>): a node deleted, or flown, and made again drops it again.
    /// </summary>
    private readonly List<(Vessel Of, ManoeuvreChain Chain)> _nodesDropped = [];

    /// <summary>
    /// Forgets each node dropped for whose vessel's chain has changed since (or which is gone from
    /// the world), as every frame begins, whatever the rate: a chain changed at the physics' rates
    /// is forgotten as surely as one changed at a formula's.
    /// </summary>
    private void ForgetNodesDropped()
    {
        for (var i = _nodesDropped.Count - 1; i >= 0; i--)
            if (!_nodesDropped[i].Chain.Equals(_nodesDropped[i].Of.Chain) || !_world.Contains(_nodesDropped[i].Of))
                _nodesDropped.RemoveAt(i);
    }

    /// <summary>Whether <paramref name="v"/>'s <paramref name="chain"/> has dropped the warp for its next node (see <see cref="_nodesDropped"/>).</summary>
    private bool NodeDropped(Vessel v, ManoeuvreChain chain)
    {
        foreach (var (of, kept) in _nodesDropped)
            if (of == v && kept.Equals(chain))
                return true;
        return false;
    }

    /// <summary>The next node's drop moment, worked out once for its vessel, stage and chain (see <see cref="NodeCut"/>): another vessel or stage burns it for another time.</summary>
    private (Vessel Of, int Stage, ManoeuvreChain Chain, double At)? _nodeCutAt;

    /// <summary>
    /// Cuts the frame for the flown vessel <paramref name="v"/>'s next node, still to come and not
    /// yet dropped for: <see cref="Scene.WarpDropLeadSeconds"/> ahead of half its burn before its
    /// time (the burn's length as its guide works it out, <see cref="NodeGuide.BurnSeconds"/>; none
    /// for an engine that can't burn it), if that is by <paramref name="to"/>.
    /// </summary>
    private void NodeCut(Vessel v, double from, ref double to)
    {
        if (v.Chain is not { } chain || NodeDropped(v, chain))
            return;
        var time = chain.TimeOf(0);
        if (!(time > from))
            return; // its time has come: nothing coming up to drop for
        if (_nodeCutAt is not { } kept || kept.Of != v || kept.Stage != v.Stage || !kept.Chain.Equals(chain))
        {
            var burn = v.Guide(from)?.BurnSeconds ?? 0f;
            kept = (v, v.Stage, chain, time - (float.IsFinite(burn) ? burn / 2.0 : 0.0) - Scene.WarpDropLeadSeconds);
            _nodeCutAt = kept;
        }
        var at = Math.Max(from, kept.At);
        if (at <= to)
            (to, _cutFor) = (at, (WarpDrop.Node, v, chain, null));
    }

    /// <summary>
    /// One window of a frame at a formula's rate (<see cref="AdvanceByFormula"/>): what a step does
    /// after the input (<see cref="Step"/>), but that every vessel is moved by formula and nothing of
    /// Jolt's is stepped: the vessels' rules once (the witnessed's along their moves instead: <see cref="BurnByFormula"/>), their moves (each stepped one's last step ending at the window's end) and the collisions between them along
    /// their ways across the window (<see cref="FormulaCollisions"/>: each one's start kept, then
    /// the window judged once all are at its end), their legs and doors, then their crossings, the
    /// edge, the bodies asked ahead, the target, the losses and the runs. Not the look ahead nor the
    /// handover (nothing is in the physics to hand over), nor <see cref="Unwatched.FindCollisions"/>
    /// (its quarter-second looks ahead are the physics' rates').
    /// </summary>
    private void FormulaStep(float dt, in FlightInput input)
    {
        var from = WorldTime;
        WorldTime += dt;
        _bodiesAt = WorldTime; // nothing in the physics is behind: it isn't stepped
        TakeChoices(input);
        var watching = input.Watching is { } on && _world.Contains(on) ? on : null;
        // The witnessed as the gate and the cuts count them (a view's or a switch's link-up still
        // waiting among them), so each one that holds or drops the warp is ruled along its moves.
        var witnessed = WarpWitnessed(input.Watched, watching);
        RunVessels(dt, input, witnessed);
        RaftsAt(WorldTime); // nothing in the physics steps them
        FormulaCollisions.Begin(from);
        // The witnessed first, each moved with its rules (see BurnByFormula), then everything else.
        BurnByFormula(Controlled, WorldTime, input);
        for (var i = 0; witnessed is not null && i < witnessed.Count; i++) // by index: a foreach over the interface would box its enumerator
            if (witnessed[i] != Controlled)
                BurnByFormula(witnessed[i], WorldTime, input);
        Unwatched.Move(toTheEnd: true);
        FormulaCollisions.Judge(from, WorldTime);
        Timings.Lap(StepPart.Rules);
        foreach (var vessel in _world)
            vessel.AfterPhysics(dt, WorldTime);
        Cross(WorldTime);
        LeaveTheSystem(WorldTime);
        AskAhead(WorldTime, witnessed);
        ResolveTarget();
        KeepFrame();
        LogLosses();
        UpdateRuns();
        KeepWarpTo();
        Timings.EndStep(_worlds.TileCount);
    }

    /// <summary>
    /// The rafts' time and the ships' poses at world time <paramref name="time"/> (the floes as each
    /// is asked of, <see cref="Rafts.Now"/>), which the physics at a body with a sea sets as it
    /// steps: for a step or a window where none does, so a touch on a deck or a floe under formula
    /// is judged as it is then.
    /// </summary>
    private static void RaftsAt(double time)
    {
        Rafts.Time = time;
        for (var i = 0; i < Rafts.Ships.Count; i++) // by index: a foreach over the interface would box its enumerator
            Rafts.Now(Rafts.Ships[i]);
    }

    /// <summary>
    /// Whether <paramref name="v"/>'s rules run along its moves in a window at a formula's rate
    /// (<see cref="BurnByFormula"/>) rather than with the rest's as the window begins: the flown
    /// vessel's and the <paramref name="witnessed"/>'s, under formula.
    /// </summary>
    private bool RuledApart(Vessel v, IReadOnlyList<Vessel>? witnessed) =>
        ByFormula && v.OnFormula && (v == Controlled || IsWatched(v, witnessed));

    /// <summary>
    /// Moves witnessed <paramref name="v"/> (see <see cref="RuledApart"/>) by formula to world time
    /// <paramref name="to"/>, the window's end, with its rules (<see cref="Vessel.Run"/>: its holds,
    /// the rings, its nodes, and for the flown one <paramref name="held"/>'s ring drag, its target
    /// located as it is then: <see cref="LocateTargetAt"/>) run after each move,
    /// for the time it moved, so its aim is turned as from where it now is: burning, every formula
    /// step (<see cref="Formula.StepFor"/>), the step after each pointed as the rules last left it,
    /// so neither the thrust nor the aim is carried past what they were; coasting (on its orbit, its
    /// aim nothing to its way), once for the whole window. A step ends where its fuel runs out
    /// (see <see cref="Formula"/>), and the last at <paramref name="to"/>, so nothing is owed past
    /// the window. Its attitude is then shown as the rules left its aim. In a window of no time its
    /// rules still run, once, of no time (an engine cut there is judged against its node, as in the
    /// physics). Standing where it came down burning (<see cref="FormulaMotion.Standing"/>: never at
    /// a formula's rate while witnessed, as the gate holds the warp for it, but kept safe), it is
    /// moved to <paramref name="to"/> at once, and its rules run once. A vessel that can't be
    /// steered, or isn't flying under formula, is left to <see cref="Unwatched.Move"/>.
    /// </summary>
    private void BurnByFormula(Vessel v, double to, in FlightInput held)
    {
        if (!v.Guided || !ByFormula)
            return;
        var flown = v == Controlled;
        do
        {
            if (!v.OnFormula || v.State != FlightState.Flying)
                return; // it touched something on the way: landed, afloat or wrecked there (or never flew)
            var at = v.Motion.Shown;
            var end = v.EngineLit && !v.Motion.Standing ? Math.Min(to, at + Formula.StepFor(v)) : to;
            Formula.Advance(v, end, toTheEnd: true);
            if (!v.OnFormula || v.State != FlightState.Flying)
                return;
            if (v.Motion.Behind)
                return; // shown at its crossing, short of the window's end: handed across there, and ruled on from the next window
            var dt = (float)Math.Max(0.0, end - at);
            if (flown)
            {
                LocateTargetAt(end); // so a hold in the target's frame steers by the target as it is then
                v.Run(dt, held.Lean, held.Hold, end, FrameVelocity, HoldFloor, witnessed: true);
            }
            else
                v.Run(dt, Vector3.Zero, null, end, Frames.VelocityIn(v.Body, v.AutoFrame, v.CentreOfMass, v.Velocity), Scene.HoldFloorSpeed, witnessed: true);
            Formula.Advance(v, end); // shown turned to the aim its rules left (no time on)
        }
        while (to - v.Motion.Shown > LeftOverSeconds);
    }

    /// <summary>
    /// The flown vessel's target as it is at world time <paramref name="time"/> within the window
    /// (<see cref="TargetAt"/>, as <see cref="ResolveTarget"/> has it at each step's end): a vessel
    /// where the formula has it then (<see cref="Formula.StateAt"/>, not moving it), a place (a
    /// ship's deck, a site, a point, a body's centre) where it is then (<see cref="Locate"/>). For the rules run along the flown
    /// vessel's moves (<see cref="BurnByFormula"/>), so a hold in the target's frame
    /// (<see cref="FrameVelocity"/>, <see cref="HoldFloor"/>) isn't a window behind.
    /// </summary>
    private void LocateTargetAt(double time)
    {
        if (_target is not { } target)
            return;
        var (vessel, _, found) = Find(target, null);
        if (!found)
            return;
        if (vessel is not null)
        {
            var (at, moving) = Formula.StateAt(vessel, time);
            _targetAt = Across(vessel.Body, at, moving, vessel.State == FlightState.Flying, time);
        }
        else if (Locate(target, time) is { } place)
            _targetAt = place;
    }

    /// <summary>
    /// Takes every vessel out of the physics for the formula's rates, each under formula from the
    /// moment its motion is at (its world's, <see cref="Worlds.Lag"/> behind the clock), and every
    /// wreck's body out at once (nothing is stepped to play it out); then nothing is behind the
    /// clock (<see cref="Worlds.ResetLag"/>). The worlds themselves are kept, not stepped, so not
    /// released: the drop finds them made. The physics' look ahead is not asked at a formula's rate,
    /// so what it last warned of and found coming (<see cref="Warning"/>, <see cref="NearMissWarning"/>,
    /// <see cref="ComingAt"/>) is let go, never left shown frozen; what comes is the cuts'
    /// (<see cref="ComingCut"/>), and the physics' asks again as the rate drops.
    /// </summary>
    private void EnterFormula()
    {
        Warning = null;
        NearMissWarning = null;
        _coming.Clear();
        var at = WorldTime - _worlds.Lag;
        _worlds.FlushWrecks();
        for (var i = 0; i < _world.Count; i++)
            _world[i].LeavePhysics(at, _world);
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
