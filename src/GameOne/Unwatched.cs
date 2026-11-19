using System.Numerics;

namespace GameOne;

/// <summary>
/// Who is in the physics, decided each flight step (<see cref="Update"/>): the witnessed (the
/// flown vessel, the small views', the main view's near its camera), and whatever could come within
/// <see cref="Scene.HandoverDistance"/> of something in the physics, or of the main view's camera,
/// within <see cref="Scene.HandoverSeconds"/> at their closing speed now. Always in the physics
/// too: one still parting from another, one still settling in the water, and one the formula
/// can't move yet. Every other vessel leaves it (<see cref="Vessel.LeavePhysics"/>), to be moved by
/// formula (<see cref="Formula.Advance"/>) at no cost to Jolt, and comes back
/// (<see cref="Vessel.ReturnToPhysics"/>) where the formula has it, before anything in the
/// physics could hit it or the camera come on it. Chosen per body: each vessel is at its own
/// (<see cref="Vessel.Body"/>), in that body's physics or under formula there, and only what is at
/// one body is near anything at it (the main view's camera is at the flown vessel's); one handed in
/// comes back into its own body's physics, and the hand-ins a step are shared among the bodies,
/// soonest needed first, wherever each is.
/// </summary>
/// <remarks>
/// <para>The reach is a pair's: the handover distance, plus their closing speed times the lead,
/// tested only against the vessels in the physics (a few: the witnessed and what is near them),
/// never one under formula against another (their collisions are the formula's). It is grown out
/// from the witnessed, so a chain of vessels each near the next is in the physics together (what
/// the witnessed one hits can hit the next), but two unwatched near each other alone are not.</para>
/// <para>One in the physics leaves only past <see cref="Scene.HandoverHysteresis"/> times its
/// reach, so one at the edge isn't swapped in and out. Leaving is cheap (a body taken out) and not
/// held back; coming back makes a body, so at most <see cref="Scene.HandoversInPerStep"/> come back
/// a step, soonest needed first: the lead is long enough for the queue. One the player switches to
/// or watches comes back at once, outside that (<see cref="Flight.BringIn"/>). One the main view
/// witnessed stays while within the hysteresis times <see cref="Scene.NearByDistance"/> of its
/// camera, though it has dropped out of the view's few by distance or by rank.</para>
/// <para>The kept are found near each other through a grid of cells (the slow ones) and a short
/// list (the fast ones), so a crowd kept together (a field of landed boosters by the flown one)
/// costs each vessel the few cells round it, not a test against every one kept.</para>
/// </remarks>
internal sealed class Unwatched(Flight flight)
{
    private readonly Flight _flight = flight;

    // This step's choice, by each vessel's place in the world's list: whether it is to be in the
    // physics, how soon it is needed there (game seconds; 0 for one witnessed or the formula can't
    // move), and its velocity among the stars (what a pair's closing is measured by). Sized for a
    // full world and grown only past it, so a step allocates nothing.
    private bool[] _wanted = new bool[Flight.Capacity + 1];
    private float[] _due = new float[Flight.Capacity + 1];
    private Vector3[] _stars = new Vector3[Flight.Capacity + 1];

    // The vessels to be in the physics, by their place in the world's list, in the order found:
    // what each other one is measured against. (And themselves, for ShouldBeInPhysics.)
    private int[] _kept = new int[Flight.Capacity + 1];
    private Vessel?[] _keptVessels = new Vessel?[Flight.Capacity + 1];
    private int _keptCount;

    // The kept, found by where they are: each slow one (over the ground, no faster than
    // GridSpeed) in a cell GridCell on a side, hashed into one of GridBuckets lists (linked by the
    // next slot in its list, by its slot in _kept); the fast ones in a list of their own, asked of
    // every vessel (few: what is flying near the witnessed). A bucket shared by two cells only
    // costs a few more pairs measured, never one missed.
    private const float GridCell = 400f;
    private const float GridSpeed = 10f;
    private const int GridBuckets = 1024; // a power of two
    private readonly int[] _heads = new int[GridBuckets];
    // Which Update each list's head was set in: an older one is empty, so the lists need no
    // clearing (this step's number, counted on).
    private readonly int[] _headSet = new int[GridBuckets];
    private int _update;
    private int[] _nextInBucket = new int[Flight.Capacity + 1];
    private bool[] _fastSlot = new bool[Flight.Capacity + 1];
    private int[] _fast = new int[Flight.Capacity + 1];
    private int _fastCount;

    // The vessels the main view held last step and this one (witnessed near its camera, or held
    // since by the hysteresis): two lists, swapped each step. Few: those near the camera.
    private Vessel?[] _viewHeld = new Vessel?[Flight.Capacity + 1];
    private Vessel?[] _viewHeldNow = new Vessel?[Flight.Capacity + 1];
    private int _viewHeldCount, _viewHeldNowCount;

    /// <summary>How many vessels in the world are in the physics, as of the last step (wrecks aside).</summary>
    public int InPhysics { get; private set; }

    /// <summary>How many vessels in the world are out of the physics, moved by formula, as of the last step.</summary>
    public int UnderFormula { get; private set; }

    /// <summary>
    /// Whether the last step wanted <paramref name="v"/> in the physics: witnessed, or near what
    /// is (a search: for the tests, and for the log's word on one removed, which is seen if so).
    /// None since a world was loaded (<see cref="Reset"/>) until its first step chooses.
    /// </summary>
    internal bool ShouldBeInPhysics(Vessel v)
    {
        for (var k = 0; k < _keptCount; k++)
            if (_keptVessels[k] == v)
                return true;
        return false;
    }

    /// <summary>
    /// Forgets everything of the world before (called as a world is loaded, <see cref="Flight.Load"/>):
    /// who was chosen for the physics and who the main view held, and the collisions under formula
    /// still to come and how far ahead they were looked for, so the new world's first step starts
    /// afresh from its own time, not from the last world's window (which a world loaded at about
    /// the same time would otherwise carry on), and no old vessel is held on to.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_keptVessels, 0, _keptCount);
        Array.Clear(_viewHeld, 0, _viewHeldCount);
        Array.Clear(_viewHeldNow);
        Array.Clear(_collisions, 0, _collisionCount);
        Array.Clear(_found, 0, _foundCount);
        (_keptCount, _fastCount, _viewHeldCount, _viewHeldNowCount, _collisionCount, _foundCount) = (0, 0, 0, 0, 0, 0);
        (_checkedTo, _lastFound, _joined, _againFrom) = (double.NaN, double.NaN, false, double.PositiveInfinity);
        (InPhysics, UnderFormula) = (0, 0);
    }

    /// <summary>
    /// Whether <paramref name="state"/>, being loaded into a world whose new rocket stands on
    /// <paramref name="pad"/> at <paramref name="padBody"/> (the flown vessel, which the camera follows), may come back straight
    /// under formula, with no body (see <see cref="Vessel"/>'s restoring constructor):
    /// it would leave the physics in the world's first step anyway, and making its body only to
    /// take it out then would cost that step, for a world of a thousand, far over a frame's budget.
    /// So only one that nothing would hold in the physics (<see cref="MustStay"/>'s cases, judged
    /// from what was saved): flying clear of the ground and the water, or landed or afloat (it
    /// comes back still, as one restored frozen does), but not landed over the sea, nor afloat with
    /// any of it over a raft (either may be aboard one, which carries it), not parting from another or another parting from it
    /// (<paramref name="partedFrom"/>), and at another body than the pad's or further from the pad
    /// than the first step's reach, the camera's view's hold and the handover's from what is there
    /// (as far as their closing could take it in the lead), with room. Anything the first step wants
    /// after all (one near something brought in) comes back as any wanted one does. Its body is its
    /// own (<see cref="VesselState.Body"/>), whose ground and sea it is judged by.
    /// </summary>
    internal static bool MayLoadOnFormula(VesselState state, GroundBody padBody, Vector3 pad, IReadOnlySet<int> partedFrom)
    {
        var body = GameOne.Bodies.Named(state.Body);
        if (state.PartingFrom is not null || partedFrom.Contains(state.Id))
            return false;
        var at = state.CentreOfMass.ToSingle();
        switch (state.State)
        {
            case FlightState.Landed when body.IsSea(body.Up(at)):
                return false;
            case FlightState.Floating when body.HasSea && OverARaft(state):
                return false; // restored, a raft would board it (and the physics carry it): see RocketPhysics.Restore
            case FlightState.Landed or FlightState.Floating:
                break;
            case FlightState.Flying:
                // Clear of the ground and the water by its own length and more, so neither touching
                // nor settling in the water (both of which the physics plays out).
                if (body.SurfaceAltitude(at) < 2f * RocketGeometry.StackHeight(state.Vehicle, state.First, state.Last) + 50f)
                    return false;
                break;
            default:
                return false; // on the pad, waiting to launch
        }
        if (body != padBody)
            return true; // nothing is near it in the first step: the new rocket is at another body
        var closing = (body.AmongStars(at, state.Velocity.ToSingle()) - body.AmongStars(pad, Vector3.Zero)).Length();
        var reach = Scene.HandoverHysteresis * (Scene.NearByDistance + Scene.HandoverDistance + closing * Scene.HandoverSeconds);
        return Vector3.DistanceSquared(at, pad) > 4f * reach * reach; // twice it: room for a chain of a few near each other
    }

    /// <summary>
    /// Whether any of <paramref name="state"/>'s body is over a raft (the drone ship's deck or a
    /// floe, as the rafts are at the world's time: Flight's Load sets the waves first): points
    /// down its axis, from its bell's bottom to its top, and round each as far out as its widest
    /// reaches (its feet's, its legs out), a few metres apart, so one lying in the water with only
    /// an end or a side over a floe's edge is found, as the physics' own low points would find it.
    /// </summary>
    private static bool OverARaft(VesselState state)
    {
        var (vehicle, first, last) = (state.Vehicle, state.First, state.Last);
        var bottom = -vehicle.Stages[first].BellHeight;
        var top = RocketGeometry.StackHeight(vehicle, first, last);
        var widest = 0f;
        for (var i = first; i <= last; i++)
            widest = MathF.Max(widest, MathF.Max(vehicle.Stages[i].Radius, vehicle.Stages[i].BellRadius));
        if (vehicle.Stages[first].Legs && state.LegDeploy > 0f)
        {
            var foot = RocketGeometry.Foot(vehicle.Stages[first].LegGeometry, 0);
            widest = MathF.Max(widest, MathF.Sqrt(foot.X * foot.X + foot.Z * foot.Z));
        }
        const int Along = 6;
        for (var k = 0; k <= Along; k++)
        {
            var y = bottom + (top - bottom) * k / Along;
            foreach (var (x, z) in (ReadOnlySpan<(float, float)>)[(0f, 0f), (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f)])
                if (Rafts.Under(state.Position.ToSingle() + Vector3.Transform(new Vector3(x * widest, y, z * widest), state.Rotation)) is not null)
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Brings <paramref name="v"/> back into the physics at world time <paramref name="now"/>, if
    /// it is under formula, and with it the other of any collision under formula it is in, still to
    /// come, whatever the budget (see <see cref="Update"/>): the physics judges their touches from now, and
    /// one left under formula would wait its turn in the queue for a body, which, the collision
    /// within that wait, the other would pass through.
    /// </summary>
    internal void Return(Vessel v, double now)
    {
        if (!v.OnFormula)
            return;
        v.ReturnToPhysics(now);
        for (var k = 0; k < _collisionCount; k++)
        {
            var other = _collisions[k].A == v ? _collisions[k].B : _collisions[k].B == v ? _collisions[k].A : null;
            if (other is not null && Whole(other))
            {
                other.ReturnToPhysics(now);
                return; // each in one at most
            }
        }
    }

    /// <summary>
    /// Moves every vessel under formula on to the flight's world time (<see cref="Move"/>): in
    /// the step just after the physics, so the vessels' rules, the target and the look ahead see
    /// every vessel where it is now, as those in the physics are (see <see cref="Update"/>); then
    /// finds and judges their collisions with each other (<see cref="FindCollisions"/>). Each timed
    /// apart (<see cref="StepDetail.Formula"/>, <see cref="StepDetail.FormulaCollisions"/>).
    /// </summary>
    public void Advance()
    {
        Move();
        FindCollisions(_flight.WorldTime);
    }

    /// <summary>
    /// Moves every vessel under formula on to the flight's world time (each from the last time it
    /// was moved to), with no look for their collisions: <see cref="Advance"/>'s first half, and
    /// the whole of a window at a formula's rate. Timed as <see cref="StepDetail.Formula"/>.
    /// </summary>
    public void Move()
    {
        var started = Timings.Start();
        var world = _flight.WorldVessels;
        var now = _flight.WorldTime;
        for (var i = 0; i < world.Count; i++)
            if (world[i].OnFormula)
                Formula.Advance(world[i], 0f, now); // at its own body, by that body's ground, air and pull
        Timings.Stop(StepDetail.Formula, started);
    }

    /// <summary>
    /// At the end of the step, the vessels under formula moved on to its time already
    /// (<see cref="Advance"/>, after the physics) and the main view's witnessed chosen
    /// (<see cref="Flight.LookAhead"/>): chooses who is to be in the physics (the witnessed: the
    /// flown one, <paramref name="watched"/> and the main view's near <paramref name="camera"/>; and
    /// what could come near them, or near the camera, soon), and hands the others out and those
    /// wanted back in, within the budget. <paramref name="dt"/> is the step's (unused: the reach
    /// goes by speeds and the lead, not steps). The camera is at <paramref name="cameraBody"/>, in its
    /// frame, moving over its ground at <paramref name="cameraVelocity"/> (as what it is on moves):
    /// by default the flown vessel's body, moving as the flown vessel does, which it follows.
    /// </summary>
    public void Update(float dt, IReadOnlyList<Vessel>? watched, Vector3 camera, GroundBody? cameraBody = null, Vector3? cameraVelocity = null)
    {
        var world = _flight.WorldVessels;
        var now = _flight.WorldTime;
        var count = world.Count;
        Fit(count);
        _update++;
        (_keptCount, _fastCount) = (0, 0);

        // Those that must be in the physics, whatever is near them.
        var flown = _flight.Controlled;
        var atBody = cameraBody ?? flown.Body; // the camera's
        var inView = _flight.InViewVessels;
        _viewHeldNowCount = 0;
        const float ViewHold = Scene.HandoverHysteresis * Scene.NearByDistance;
        for (var i = 0; i < count; i++)
        {
            var v = world[i];
            (_wanted[i], _due[i]) = (false, float.PositiveInfinity);
            if (v.State == FlightState.Crashed)
                continue; // a wreck plays its hit out in the physics, and is gone (see RocketPhysics.Wreck)
            _stars[i] = v.Body.AmongStars(v.CentreOfMass, v.Velocity);
            // The main view's: its few now, and one it held last step, still in the physics and
            // within the hysteresis times its reach, so one at its edge (by distance, or by rank
            // among the few) isn't handed out and back in as it crosses it.
            var viewed = Contains(inView, v)
                || (!v.OnFormula && v.Body == atBody && Vector3.DistanceSquared(v.CentreOfMass, camera) <= ViewHold * ViewHold && WasViewHeld(v));
            if (viewed)
                _viewHeldNow[_viewHeldNowCount++] = v;
            if (viewed || v == flown || Flight.IsWatched(v, watched) || MustStay(v))
                Keep(i, v, 0f);
        }
        (_viewHeld, _viewHeldNow, _viewHeldCount) = (_viewHeldNow, _viewHeld, _viewHeldNowCount);

        // Then what could come near them, grown out from them, pass by pass until one finds none
        // (one found in a pass may come after a vessel near it in the list). The camera moves over
        // the ground as what it is on does (among the stars, as the ground turns where the camera
        // is); it is asked in the first pass only (it doesn't move between passes). Only what is at
        // the camera's body is near it.
        var cameraStars = atBody.AmongStars(camera, cameraVelocity ?? flown.Velocity);
        for (var pass = 0; ; pass++)
        {
            var before = _keptCount;
            for (var i = 0; i < count; i++)
            {
                var v = world[i];
                if (_wanted[i] || v.State == FlightState.Crashed)
                    continue;
                var due = float.PositiveInfinity;
                var near = pass == 0 && Near(v, _stars[i], atBody, camera, cameraStars, 0f, ref due);
                near |= NearKept(v, _stars[i], ref due);
                if (near)
                    Keep(i, v, due);
            }
            if (_keptCount == before)
                break;
        }

        // Out: everything not wanted (cheap, so never held back). The collisions under formula are
        // looked for again at once, for any that left (see FindCollisions).
        for (var i = 0; i < count; i++)
            if (!_wanted[i] && !world[i].OnFormula && world[i].State != FlightState.Crashed)
            {
                world[i].LeavePhysics(now);
                _joined |= world[i].OnFormula;
            }

        // In, each into its own body's physics (made for it if that has none): the flown one and
        // the watched at once (a view of it, or flying it, can't wait). Both
        // are brought in as they are switched to or given a view (Flight.BringIn); this is the
        // backstop for any watched another way (the tests' FlightInput.Watched). So does one the
        // formula needs the physics for (Vessel.NeedsPhysics): come down on a raft (the deck
        // heaves and the floe drifts from under it from that step, so behind the queue it would be
        // restored off the deck, not on it), or handed back where it stands. The rest a few
        // a step, soonest needed first. Each brings the other of a collision under formula it is in
        // with it (see Return). The few a step are every body's together, soonest first.
        for (var i = 0; i < count; i++)
            if (_wanted[i] && world[i].OnFormula && (world[i] == flown || Flight.IsWatched(world[i], watched) || world[i].NeedsPhysics))
                Return(world[i], now);
        for (var n = 0; n < Scene.HandoversInPerStep; n++)
        {
            var (soonest, best) = (-1, float.PositiveInfinity);
            for (var i = 0; i < count; i++)
                if (_wanted[i] && world[i].OnFormula && (soonest < 0 || _due[i] < best))
                    (soonest, best) = (i, _due[i]);
            if (soonest < 0)
                break;
            Return(world[soonest], now);
        }

        var (inPhysics, underFormula) = (0, 0);
        for (var i = 0; i < count; i++)
            if (world[i].OnFormula)
                underFormula++;
            else if (world[i].State != FlightState.Crashed)
                inPhysics++;
        (InPhysics, UnderFormula) = (inPhysics, underFormula);
    }

    // ---- Collisions under formula ----

    // How far ahead the last check looked (game time), and the time of the last call: each check
    // takes up where the last left off, so a step that runs past the end of the interval looked
    // at (a long frame, a warp's steps) is checked back to it, and none is skipped. Whether any
    // vessel left the physics since (its collisions in what was looked at already were the
    // physics' to find, so it is checked at once, back to the step it left in).
    private double _checkedTo = double.NaN, _lastFound = double.NaN;
    private bool _joined;

    /// <summary>
    /// <paramref name="v"/>, under formula, has crossed a sphere's edge (<see cref="Vessel.CrossTo"/>)
    /// at world time <paramref name="now"/>: what was looked at for it was in the frame it left, so
    /// a collision still to come with one now at another body is dropped (it can't be: their places
    /// are in two frames), and it is checked again at once from now, in its new body's, with
    /// everything there (it was left out of the checks while that collision was to come).
    /// </summary>
    internal void Crossed(Vessel v, double now)
    {
        for (var k = _collisionCount - 1; k >= 0; k--)
        {
            var m = _collisions[k];
            if ((m.A == v || m.B == v) && m.A.Body != m.B.Body)
            {
                _collisions[k] = _collisions[--_collisionCount];
                _againFrom = Math.Min(_againFrom, now);
            }
        }
        _joined = true;
    }

    // The earliest time a collision that has come left one of its two whole (found not to collide
    // after all, a bump, or the other back in the physics): that vessel was left out of the checks
    // while the collision was to come, and kept only its earliest collision of the check that found
    // it, so its other touches from then on are looked for again (at once, back to then). None:
    // infinity.
    private double _againFrom = double.PositiveInfinity;

    /// <summary>
    /// The collisions under formula carry on from world time <paramref name="time"/>, as the
    /// formula's rates give way to the physics' (<see cref="Flight.LeaveFormula"/>): the next check
    /// looks from there, with nothing from before it kept (what came in the formula's windows was
    /// theirs), and not afresh as after a world loaded, however long the jump in the clock.
    /// </summary>
    internal void CarryOnFrom(double time)
    {
        for (var k = _collisionCount - 1; k >= 0; k--)
            if (_collisions[k].At <= time)
                _collisions[k] = _collisions[--_collisionCount];
        (_checkedTo, _lastFound, _joined, _againFrom) = (time, time, true, double.PositiveInfinity);
    }

    /// <summary>The longest a check looks back (game seconds): a jump in the world's time past this (a world loaded) starts afresh from now, not from far back.</summary>
    private const double MostBehind = 1.0;

    // Each vessel under formula as a check reads it, by its slot in this check: its place in the
    // world's list, its middle (over the ground), its velocity among the stars and its pull (both
    // in the stars' frame matching the ground at the check), its reach (its size-sphere, or its
    // body's furthest point with its feet's reach, if further), its body (its axis's two ends
    // from its middle, and how wide it is), whether it is held where it stands, its swept box over
    // the interval, and whether it has hit one already in this check. Sized for a full world (and
    // grown only past it), so a check allocates nothing.
    private int[] _place = new int[Flight.Capacity + 1];
    private Vector3[] _middle = new Vector3[Flight.Capacity + 1];
    private Vector3[] _moving = new Vector3[Flight.Capacity + 1];
    private Vector3[] _pull = new Vector3[Flight.Capacity + 1];
    private float[] _size = new float[Flight.Capacity + 1];
    private Vector3[] _bottom = new Vector3[Flight.Capacity + 1];
    private Vector3[] _top = new Vector3[Flight.Capacity + 1];
    private float[] _width = new float[Flight.Capacity + 1];
    private GroundBody?[] _bodyOf = new GroundBody?[Flight.Capacity + 1];
    private bool[] _standing = new bool[Flight.Capacity + 1];
    private Vector3[] _boxLow = new Vector3[Flight.Capacity + 1];
    private Vector3[] _boxHigh = new Vector3[Flight.Capacity + 1];
    private bool[] _met = new bool[Flight.Capacity + 1];
    // The sweep: each box's low X, sorted, with its slot beside it.
    private float[] _sweepKeys = new float[Flight.Capacity + 1];
    private int[] _sweepSlots = new int[Flight.Capacity + 1];

    /// <summary>A collision found ahead: the two (and their slots in the check that found it), when (world time) their bodies first touch, and how fast they close then.</summary>
    private struct Found
    {
        public Vessel A, B;
        public int SlotA, SlotB;
        public double At;
        public float Closing;
    }

    // A check's collisions as found, before each vessel's earliest is kept; and the collisions kept
    // and not yet come (each vessel in one at most). Grown, never written past, should a crowd
    // ever touch more pairs at once than a full world's half.
    private Found[] _found = new Found[Flight.Capacity / 2 + 1];
    private int _foundCount;
    private Found[] _collisions = new Found[Flight.Capacity / 2 + 1];
    private int _collisionCount;

    /// <summary>
    /// Finds the collisions between vessels under formula over the next
    /// <see cref="Scene.FormulaCollisionSeconds"/> of game time, once that much has passed since the
    /// last check (or at once, when one has just left the physics), and wrecks those whose
    /// collision has come by <paramref name="worldTime"/>: each, closing faster than its
    /// <see cref="Vessel.ImpactTolerance"/> as their bodies begin to touch, is wrecked as part 1
    /// wrecks a vessel hit by another. Under it, a bump: nothing changes (no bounce under formula).
    /// Timed as <see cref="StepDetail.FormulaCollisions"/>.
    /// </summary>
    /// <remarks>
    /// <para>Each vessel's reach (its size-sphere, <see cref="Collisions.Size"/>, or further if its
    /// feet reach further) is swept over the interval along its path (its motion and pull now,
    /// among the stars), boxed, and the boxes sorted along one axis (sweep and prune), so only
    /// pairs whose boxes overlap are solved: their closest approach as <see cref="Collisions"/>
    /// solves a pair (<see cref="Collisions.Approach"/>), and, within their reach, their bodies
    /// over the stretch they are: each an axis as wide as itself (<see cref="Vessel.Width"/>), as
    /// the formula checks the towers and masts (<see cref="TowersAndMasts.Closest"/>), sampled no
    /// further apart than half their widths so neither passes through the other between. Only a
    /// contact can be a hit, and only its beginning (CLAUDE.md, "Vessels collide"): two touching
    /// already as a check begins are in a contact begun before, judged then (or a bump), not again.
    /// The touch is judged by their closing then, along the line between the two bodies' nearest
    /// points. Each vessel keeps only its earliest collision in a check. Two both held where they
    /// stand can't close, and aren't solved.</para>
    /// <para>The interval is game time, so in a warp's run of steps it is as long, and each step
    /// (a sixtieth of a second at most in warp) asks; a step past the interval's end is checked
    /// back to it, from where the vessels are now, so nothing between two checks goes unlooked at.
    /// A collision is kept until its time comes, so each is wrecked where and when it hits (moved
    /// back along its way from where it is, within a step), not up to a quarter second early, and
    /// only if their bodies still touch then (see <see cref="Collide"/>).</para>
    /// <para>None of this is witnessed, so nothing here slows the game (<see cref="Flight.Slowed"/>
    /// is the witnessed vessels' alone).</para>
    /// </remarks>
    internal void FindCollisions(double worldTime)
    {
        var started = Timings.Start();
        if (double.IsNaN(_lastFound) || worldTime < _lastFound || worldTime - _lastFound > MostBehind)
            (_checkedTo, _joined, _collisionCount, _againFrom) = (worldTime, false, 0, double.PositiveInfinity); // a world (re)loaded: afresh from now
        // The collisions come first, so one hit now (or found not to be) is not left out of the
        // check after it as still to come.
        Collide(worldTime);
        if (worldTime >= _checkedTo || _joined || _againFrom <= worldTime)
        {
            var from = _joined ? Math.Min(_checkedTo, _lastFound) : Math.Min(_checkedTo, worldTime);
            from = Math.Max(Math.Min(from, _againFrom), worldTime - MostBehind);
            Check(worldTime, (float)Math.Min(0.0, from - worldTime), Scene.FormulaCollisionSeconds);
            (_checkedTo, _joined, _againFrom) = (worldTime + Scene.FormulaCollisionSeconds, false, double.PositiveInfinity);
        }
        _lastFound = worldTime;
        Timings.Stop(StepDetail.FormulaCollisions, started);
    }

    /// <summary>For the tests: how many collisions found are still to come.</summary>
    internal int CollisionsAhead => _collisionCount;

    /// <summary>
    /// The collisions between vessels under formula from <paramref name="low"/> to
    /// <paramref name="high"/> seconds from <paramref name="now"/> (the low end at or before
    /// now), each vessel's earliest kept to be judged as its time comes (<see cref="Collide"/>).
    /// </summary>
    private void Check(double now, float low, float high)
    {
        var world = _flight.WorldVessels;
        Fit(world.Count); // staging may have added to it since the last Update
        var span = high - low;
        var n = 0;
        for (var i = 0; i < world.Count; i++)
        {
            var v = world[i];
            if (!v.OnFormula || v.State == FlightState.Crashed || InACollision(v))
                continue;
            var held = Collisions.Held(v);
            var (at, velocity) = (v.CentreOfMass, v.Velocity);
            var middle = Collisions.Middle(v);
            var moving = v.Body.AmongStars(at, velocity);
            var pull = Collisions.Fall(v.Body, held, at) - velocity * Collisions.Drag(v.Body, held, at) + Collisions.Thrust(v);
            var (bottom, top, width) = Body(v);
            var size = MathF.Max(Collisions.Size(v), MathF.Max(bottom.Length(), top.Length()) + width);
            // Its box over the interval: its two ends, and the most its pull bows the path off the
            // line between them (a t²/8 over the span), as wide as its reach.
            var (start, end) = (middle + moving * low + pull * (low * low / 2f), middle + moving * high + pull * (high * high / 2f));
            var pad = new Vector3(size + pull.Length() * span * span / 8f);
            (_place[n], _middle[n], _moving[n], _pull[n], _size[n], _standing[n], _met[n]) = (i, middle, moving, pull, size, held, false);
            (_bottom[n], _top[n], _width[n], _bodyOf[n]) = (bottom, top, width, v.Body);
            (_boxLow[n], _boxHigh[n]) = (Vector3.Min(start, end) - pad, Vector3.Max(start, end) + pad);
            (_sweepKeys[n], _sweepSlots[n]) = (_boxLow[n].X, n);
            n++;
        }
        if (n < 2)
            return;
        Array.Sort(_sweepKeys, _sweepSlots, 0, n);
        _foundCount = 0;
        for (var s = 0; s < n; s++)
        {
            var i = _sweepSlots[s];
            for (var s2 = s + 1; s2 < n && _sweepKeys[s2] <= _boxHigh[i].X; s2++)
            {
                var j = _sweepSlots[s2];
                if (_bodyOf[i] != _bodyOf[j] || (_standing[i] && _standing[j]) // two at different bodies never meet (their places are in two frames)
                    || _boxLow[i].Y > _boxHigh[j].Y || _boxLow[j].Y > _boxHigh[i].Y
                    || _boxLow[i].Z > _boxHigh[j].Z || _boxLow[j].Z > _boxHigh[i].Z)
                    continue;
                Solve(world, i, j, now, low, high);
            }
        }

        // Each vessel's earliest: the found in order of time (few, so sorted in place), each kept
        // unless one of its two has hit another sooner.
        for (var k = 1; k < _foundCount; k++)
        {
            var (f, m) = (_found[k], k - 1);
            for (; m >= 0 && _found[m].At > f.At; m--)
                _found[m + 1] = _found[m];
            _found[m + 1] = f;
        }
        for (var k = 0; k < _foundCount; k++)
        {
            var f = _found[k];
            if (_met[f.SlotA] || _met[f.SlotB])
                continue;
            (_met[f.SlotA], _met[f.SlotB]) = (true, true);
            Keep(ref _collisions, ref _collisionCount, f);
        }
    }

    /// <summary>
    /// <paramref name="v"/>'s body as the collisions take it: its axis's two ends (its bell's bottom,
    /// and its top) from the middle of its size-sphere, as it is turned now, and how far out from
    /// the axis anything of it reaches (<see cref="Vessel.Width"/>: its feet, its legs out). Turned
    /// by its live attitude, its body's or the formula's: the near misses (<see cref="Collisions.NearestMiss"/>)
    /// ask it of vessels in the physics, whose formula attitude is as it was when they last left it.
    /// </summary>
    internal static (Vector3 Bottom, Vector3 Top, float Width) Body(Vessel v)
    {
        var rotation = v.Rotation;
        var (bottom, top) = v.AxisOffsets(rotation);
        var middle = v.MiddleOffset(rotation);
        return (bottom - middle, top - middle, v.Width);
    }

    /// <summary>
    /// Whether slots <paramref name="i"/> and <paramref name="j"/>'s bodies begin to touch between
    /// <paramref name="low"/> and <paramref name="high"/> seconds from <paramref name="now"/>
    /// (<see cref="FormulaContact.Touch"/>), closing faster than either can take; if so, found (<see cref="_found"/>).
    /// </summary>
    private void Solve(IReadOnlyList<Vessel> world, int i, int j, double now, float low, float high)
    {
        var (touch, closing) = FormulaContact.Touch(_middle[j] - _middle[i], _moving[j] - _moving[i], _pull[j] - _pull[i], _size[i] + _size[j], low, high,
            _bottom[i], _top[i], _width[i], _bottom[j], _top[j], _width[j]);
        if (float.IsNaN(touch))
            return;
        var (vi, vj) = (world[_place[i]], world[_place[j]]);
        if (closing <= MathF.Min(vi.ImpactTolerance, vj.ImpactTolerance))
            return; // a bump: nothing changes
        Keep(ref _found, ref _foundCount, new Found { A = vi, B = vj, SlotA = i, SlotB = j, At = now + touch, Closing = closing });
    }

    /// <summary>Adds <paramref name="f"/> to <paramref name="list"/>, growing it (never written past) should it be full.</summary>
    private static void Keep(ref Found[] list, ref int count, in Found f)
    {
        if (count == list.Length)
            Array.Resize(ref list, 2 * list.Length);
        list[count++] = f;
    }

    /// <summary>Whether <paramref name="v"/> is in a collision found already, still to come.</summary>
    private bool InACollision(Vessel v)
    {
        for (var k = 0; k < _collisionCount; k++)
            if (_collisions[k].A == v || _collisions[k].B == v)
                return true;
        return false;
    }

    /// <summary>
    /// Each collision found whose time has come by <paramref name="now"/>, the two moved back along
    /// their ways to it from where they are now (within a step): if their bodies touch there (as
    /// found, within <see cref="FormulaContact.Slack"/>), each of the two closing faster than its tolerance is
    /// wrecked with the physics' reason, there. If not, one's motion was remade since it was
    /// found (a burn lit, a landing): judged now instead if their bodies touch now, judged by their
    /// closing now; else they missed after all. One no longer under formula (brought back, the
    /// physics judges it now), wrecked already or gone from the world meanwhile, isn't judged. One left
    /// whole by it is looked for again from the collision's time (<see cref="_againFrom"/>).
    /// </summary>
    private void Collide(double now)
    {
        for (var k = _collisionCount - 1; k >= 0; k--)
        {
            var m = _collisions[k];
            if (m.At > now)
                continue;
            _collisions[k] = _collisions[--_collisionCount];
            Judge(m, now);
            if (Whole(m.A) || Whole(m.B))
                _againFrom = Math.Min(_againFrom, m.At);
        }
    }

    /// <summary>Judges <paramref name="m"/>, come by <paramref name="now"/>, if it still is one (see <see cref="Collide"/>).</summary>
    private void Judge(in Found m, double now)
    {
        if (!Whole(m.A) || !Whole(m.B) || m.A.Body != m.B.Body)
            return; // (one crossed into another body's sphere since, away from the other: their places are in two frames)
        var since = (float)(now - m.At);
        var ((bottomA, topA, widthA), (bottomB, topB, widthB)) = (Body(m.A), Body(m.B));
        var within = widthA + widthB;
        var relative = m.B.Velocity - m.A.Velocity;
        var gapNow = Collisions.Middle(m.B) - Collisions.Middle(m.A);
        var (then, closing) = (gapNow - relative * since, m.Closing);
        if (FormulaContact.Bodies(bottomA, topA, then, bottomB, topB, relative, widthA, widthB).Apart >= within + FormulaContact.Slack)
        {
            (var apart, closing) = FormulaContact.Bodies(bottomA, topA, gapNow, bottomB, topB, relative, widthA, widthB);
            if (apart >= within)
                return; // missed, the motion it was found from remade
            since = 0f;
        }
        FormulaContact.Wreck(m.A, m.A.CentreOfMass - m.A.Velocity * since, m.B, m.B.CentreOfMass - m.B.Velocity * since, closing);
    }

    private bool Whole(Vessel v)
    {
        if (!v.OnFormula || v.State == FlightState.Crashed)
            return false;
        var world = _flight.WorldVessels;
        for (var i = 0; i < world.Count; i++)
            if (world[i] == v)
                return true;
        return false;
    }

    private bool WasViewHeld(Vessel v)
    {
        for (var k = 0; k < _viewHeldCount; k++)
            if (_viewHeld[k] == v)
                return true;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="v"/> must stay in the physics whoever is near: still parting from
    /// what it came off (the two are told apart only by their bodies: see Craft.PartingFrom),
    /// handed back by the formula and still playing that out (Vessel.PlayingOut),
    /// still settling in the water, or doing what the formula doesn't move: on the pad, on the
    /// ground (or another vessel) but not yet at rest (landed and frozen), aboard a raft (which
    /// carries it). The formula flies the rest (Formula.Advance): on its orbit with its engine off, in the air, burning,
    /// and its touch, judged itself. Under formula its stand-in body's flags are as it left
    /// (flying: aboard nothing, parting from nothing), so only one the formula itself hands
    /// back must come back (Vessel.NeedsPhysics): come down on a raft, for the raft to carry it, or
    /// one whose plunge onto a shallow seabed the physics is to play.
    /// </summary>
    private static bool MustStay(Vessel v)
    {
        if (v.OnFormula)
            return v.NeedsPhysics;
        if (v.Craft.PartingFrom is not null || v.Craft.Settling || v.PlayingOut)
            return true;
        return v.State switch
        {
            // Flying clear of everything, the formula flies it; on something (its feet, bell or
            // body on the ground, or touching another vessel), the physics settles it there.
            FlightState.Flying => v.Grounded || v.Touching,
            // Landed or afloat it stays where it is in the ground's frame: once the physics has it
            // at rest (frozen), so it goes as it would stay, and not on a raft, which moves. (A raft
            // carries only one frozen aboard it, but one landed on it may not be frozen yet.)
            FlightState.Landed or FlightState.Floating => !v.Craft.Frozen || v.Craft.Aboard is not null,
            _ => true, // on the pad, waiting to launch
        };
    }

    /// <summary>
    /// Whether <paramref name="v"/> is within reach of any kept so far (see <see cref="Near"/>):
    /// every fast one, and the slow ones in the cells it could reach them in, or every slow one if
    /// they are fewer than those cells (as for one fast itself, which reaches far).
    /// </summary>
    private bool NearKept(Vessel v, Vector3 stars, ref float due)
    {
        var world = _flight.WorldVessels;
        var near = false;
        for (var f = 0; f < _fastCount; f++)
        {
            var k = _kept[_fast[f]];
            near |= Near(v, stars, world[k].Body, world[k].CentreOfMass, _stars[k], _due[k], ref due);
            if (Enough(v, near, due))
                return true;
        }
        var slow = _keptCount - _fastCount;
        if (slow == 0)
            return near;
        // The furthest a slow one could be and still be in reach: their closing among the stars is
        // at most their speeds over the ground together, and the ground's turn times how far apart
        // they are (Near's test: distance <= hysteresis × (handover distance + closing × lead)).
        const float Over = Scene.HandoverHysteresis * Scene.HandoverSeconds;
        var reach = Scene.HandoverHysteresis * Scene.HandoverDistance + Over * (v.Velocity.Length() + GridSpeed);
        reach /= MathF.Max(0.1f, 1f - Over * v.Body.SpinRate); // the ground's turn: how much faster than over the ground two a metre apart can close among the stars
        // The cells its reach overlaps (two a side, for one slow itself), unless the kept are fewer.
        reach = MathF.Min(reach, 64f * GridCell);
        var p = v.CentreOfMass;
        var (lo, hi) = (CellOf(p - new Vector3(reach)), CellOf(p + new Vector3(reach)));
        if ((long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1) >= slow)
        {
            for (var slot = 0; slot < _keptCount; slot++)
                if (!_fastSlot[slot])
                {
                    var k = _kept[slot];
                    near |= Near(v, stars, world[k].Body, world[k].CentreOfMass, _stars[k], _due[k], ref due);
                    if (Enough(v, near, due))
                        return true;
                }
            return near;
        }
        for (var x = lo.X; x <= hi.X; x++)
            for (var y = lo.Y; y <= hi.Y; y++)
                for (var z = lo.Z; z <= hi.Z; z++)
                    for (var slot = Head(Bucket(x, y, z)); slot >= 0; slot = _nextInBucket[slot])
                    {
                        var k = _kept[slot];
                        near |= Near(v, stars, world[k].Body, world[k].CentreOfMass, _stars[k], _due[k], ref due);
                        if (Enough(v, near, due))
                            return true;
                    }
        return near;
    }

    /// <summary>
    /// Whether what is known of <paramref name="v"/> already settles it: within reach of one, and
    /// either in the physics (it stays: how soon it is needed is nothing to it) or needed at once.
    /// So in a crowd kept together each stops at the first near it, not at every one.
    /// </summary>
    /// <remarks>
    /// Stopping early leaves one in the physics with the due of the first near it found, not the
    /// soonest of all: later than it could be. That is harmless by design. Its own due orders
    /// nothing (it is in the physics already); one under formula kept through it only goes a
    /// little later in the queue for a body (its due is that one's plus its own), never out of
    /// it, and the lead (<see cref="Scene.HandoverSeconds"/>) has room for the queue. Finding the
    /// soonest would cost every pair in a crowd kept together, which this is here to save.
    /// </remarks>
    private static bool Enough(Vessel v, bool near, float due) => near && (!v.OnFormula || due <= 0f);

    private int Head(int bucket) => _headSet[bucket] == _update ? _heads[bucket] : -1;

    /// <summary>
    /// Whether <paramref name="v"/> (moving <paramref name="stars"/> among the stars) is within reach
    /// of something at <paramref name="atBody"/>, at <paramref name="at"/> moving <paramref name="atStars"/>
    /// (never, at another body than its own): the handover distance
    /// plus their closing speed times the lead, or, in the physics already, that times the
    /// hysteresis. If so, <paramref name="due"/> is kept the sooner of itself and how soon they
    /// could be within the handover distance, after the other is needed in the physics itself
    /// (<paramref name="atDue"/>: 0 for the witnessed; for one kept only by its nearness to
    /// another, that one's), so one kept by a chain is never needed sooner than the chain is.
    /// </summary>
    private static bool Near(Vessel v, Vector3 stars, GroundBody atBody, Vector3 at, Vector3 atStars, float atDue, ref float due)
    {
        if (atBody != v.Body)
            return false;
        var apart = v.CentreOfMass - at;
        var distance = apart.Length();
        var relative = stars - atStars;
        // Closing is how fast the distance shrinks (moving apart, nothing); on top of the other,
        // however it moves.
        var closing = distance > 1e-3f ? MathF.Max(0f, -Vector3.Dot(apart, relative) / distance) : relative.Length();
        var reach = Scene.HandoverDistance + closing * Scene.HandoverSeconds;
        if (distance > (v.OnFormula ? reach : reach * Scene.HandoverHysteresis))
            return false;
        var soon = distance <= Scene.HandoverDistance ? 0f
            : closing > 0f ? (distance - Scene.HandoverDistance) / closing
            : Scene.HandoverSeconds; // inside the hysteresis' margin, coming no nearer
        due = MathF.Min(due, atDue + soon);
        return true;
    }

    private void Keep(int i, Vessel v, float due)
    {
        (_wanted[i], _due[i]) = (true, due);
        var slot = _keptCount++;
        (_kept[slot], _keptVessels[slot]) = (i, v);
        _fastSlot[slot] = v.Velocity.LengthSquared() > GridSpeed * GridSpeed;
        if (_fastSlot[slot])
            _fast[_fastCount++] = slot;
        else
        {
            var (x, y, z) = CellOf(v.CentreOfMass);
            var bucket = Bucket(x, y, z);
            (_nextInBucket[slot], _heads[bucket], _headSet[bucket]) = (Head(bucket), slot, _update);
        }
    }

    private static (int X, int Y, int Z) CellOf(Vector3 p) =>
        ((int)MathF.Floor(p.X / GridCell), (int)MathF.Floor(p.Y / GridCell), (int)MathF.Floor(p.Z / GridCell));

    private static int Bucket(int x, int y, int z) => ((x * 73856093) ^ (y * 19349663) ^ (z * 83492791)) & (GridBuckets - 1);

    private static bool Contains(ReadOnlySpan<Vessel> vessels, Vessel v)
    {
        foreach (var each in vessels)
            if (each == v)
                return true;
        return false;
    }

    /// <summary>Room for <paramref name="count"/> vessels: a full world fits already, so this only grows past one (never in a step of a world within its capacity).</summary>
    private void Fit(int count)
    {
        if (count <= _wanted.Length)
            return;
        Array.Resize(ref _wanted, count);
        Array.Resize(ref _due, count);
        Array.Resize(ref _stars, count);
        Array.Resize(ref _kept, count);
        Array.Resize(ref _keptVessels, count);
        Array.Resize(ref _nextInBucket, count);
        Array.Resize(ref _fastSlot, count);
        Array.Resize(ref _fast, count);
        Array.Resize(ref _viewHeld, count);
        Array.Resize(ref _viewHeldNow, count);
        Array.Resize(ref _place, count);
        Array.Resize(ref _middle, count);
        Array.Resize(ref _moving, count);
        Array.Resize(ref _pull, count);
        Array.Resize(ref _size, count);
        Array.Resize(ref _standing, count);
        Array.Resize(ref _boxLow, count);
        Array.Resize(ref _boxHigh, count);
        Array.Resize(ref _met, count);
        Array.Resize(ref _sweepKeys, count);
        Array.Resize(ref _sweepSlots, count);
        Array.Resize(ref _bottom, count);
        Array.Resize(ref _top, count);
        Array.Resize(ref _width, count);
        Array.Resize(ref _bodyOf, count);
    }
}

/// <summary>
/// The body-to-body solve the collisions between vessels under formula share, the physics' rates'
/// (<see cref="Unwatched.FindCollisions"/>) and the formula's (<see cref="FormulaCollisions"/>):
/// when two bodies, each an axis as wide as itself, begin to touch, how fast they close then, and
/// how they are wrecked by it.
/// </summary>
internal static class FormulaContact
{
    /// <summary>
    /// How much further apart than touching two bodies may be, moved back to their collision's time
    /// from where they are as it comes, and still be hit as found: what a formula step's own
    /// bending (its pull's change over the interval, under a centimetre) and the halving leave,
    /// with room to spare. Further, the motion of one has been remade since (a burn lit, a
    /// landing), and the collision found from it is no longer theirs. And how much further apart
    /// than their reaches two may come at their closest along their ways over a window at a
    /// formula's rate and still be solved: what those ways' own error leaves.
    /// </summary>
    internal const float Slack = 1f;

    /// <summary>The most times a pair's bodies are sampled across the stretch their sizes overlap: some 60 at most for the vessels there are (two sizes over half their widths).</summary>
    private const int MostSamples = 256;

    /// <summary>
    /// When two bodies begin to touch between <paramref name="low"/> and <paramref name="high"/>
    /// seconds on, the second's middle <paramref name="r"/> from the first's, moving
    /// <paramref name="u"/> and pulled <paramref name="a"/> relative to it (all among the stars), and
    /// how fast they close then; NaN if they don't (or touch already at <paramref name="low"/>: a
    /// contact begun before, judged as it began). Their reaches (<paramref name="reach"/> together)
    /// overlapping nowhere in the stretch, their bodies can't touch; within it, their bodies (each
    /// its axis's two ends from its middle, and how wide it is) are sampled no further apart than
    /// half their widths, so neither passes through the other between, and the moment halved for.
    /// The closing is along the line between their nearest points, as the physics judges a hit by
    /// the closing at the contact (see <see cref="Bodies"/>).
    /// </summary>
    internal static (float At, float Closing) Touch(Vector3 r, Vector3 u, Vector3 a, float reach, float low, float high,
        Vector3 bottomA, Vector3 topA, float widthA, Vector3 bottomB, Vector3 topB, float widthB)
    {
        var none = (float.NaN, 0f);
        var reach2 = reach * reach;
        Vector3 Gap(float t) => r + u * t + a * (t * t / 2f);

        // Their closest within the stretch, from their lines' closest (Collisions' solve).
        var uu = u.LengthSquared();
        var line = uu > 1e-6f ? -Vector3.Dot(r, u) / uu : low;
        var closest = Math.Clamp(Collisions.Approach(r, u, a, Math.Clamp(line, low, high)), low, high);
        if (Gap(closest).LengthSquared() > reach2)
            return none;
        // The stretch within reach: from the first moment they are (at the start, already, or
        // halved for on the way in: the gap shrinks all the way to the closest) to the last (the
        // end, or halved for on the way out).
        var (first, last) = (low, high);
        if (Gap(low).LengthSquared() > reach2)
            first = Halve(r, u, a, reach2, low, closest);
        if (Gap(high).LengthSquared() > reach2)
            last = Halve(r, u, a, reach2, high, closest);

        // Their bodies across it.
        var within = widthA + widthB;
        if (first <= low && Bodies(bottomA, topA, Gap(first), bottomB, topB, u + a * first, widthA, widthB).Apart < within)
            return none; // touching as the stretch begins: a contact begun before, judged as it began
        var speed = MathF.Max((u + a * first).Length(), (u + a * last).Length());
        var samples = Math.Clamp((int)MathF.Ceiling((last - first) * speed / (within / 2f)), 1, MostSamples);
        var (before, touch) = (first, float.NaN);
        for (var k = 0; k <= samples; k++)
        {
            var t = first + (last - first) * k / samples;
            if (Bodies(bottomA, topA, Gap(t), bottomB, topB, u, widthA, widthB).Apart < within)
            {
                // The moment they begin to touch, halved for between the sample before and this.
                var (outside, inside) = (before, t);
                for (var h = 0; h < 16 && k > 0; h++)
                {
                    var mid = (outside + inside) / 2f;
                    if (Bodies(bottomA, topA, Gap(mid), bottomB, topB, u, widthA, widthB).Apart < within)
                        inside = mid;
                    else
                        outside = mid;
                }
                touch = inside;
                break;
            }
            before = t;
        }
        if (float.IsNaN(touch))
            return none; // within each other's reach, but their bodies pass clear
        return (touch, Bodies(bottomA, topA, Gap(touch), bottomB, topB, u + a * touch, widthA, widthB).Closing);
    }

    /// <summary>The moment between <paramref name="outside"/> (out of reach) and <paramref name="inside"/> (within it) the gap <c>r + u t + a t²/2</c> crosses the reach.</summary>
    private static float Halve(Vector3 r, Vector3 u, Vector3 a, float reach2, float outside, float inside)
    {
        for (var k = 0; k < 24; k++)
        {
            var mid = (outside + inside) / 2f;
            var gap = r + u * mid + a * (mid * mid / 2f);
            if (gap.LengthSquared() > reach2)
                outside = mid;
            else
                inside = mid;
        }
        return inside;
    }

    /// <summary>
    /// How far apart two bodies' axes are (see <see cref="Unwatched.Body"/>), the second's middle
    /// <paramref name="gap"/> from the first's; and how fast they close, moving
    /// <paramref name="relative"/> (the second's less the first's), along the way the two collide
    /// there, their widths <paramref name="widthA"/> and <paramref name="widthB"/>: one's end
    /// hitting the other (a nose into a side, two noses together) face on, along that one's axis,
    /// as the physics' flat ends touch; else side to side, along the line between the axes'
    /// nearest points.
    /// </summary>
    internal static (float Apart, float Closing) Bodies(Vector3 bottomA, Vector3 topA, Vector3 gap, Vector3 bottomB, Vector3 topB, Vector3 relative,
        float widthA, float widthB)
    {
        var within = widthA + widthB;
        var (b0, b1) = (gap + bottomB, gap + topB);
        var (along, apart) = TowersAndMasts.Closest(bottomA, topA, b0, b1);
        var nearA = Vector3.Lerp(bottomA, topA, along);
        var d = b1 - b0;
        var acrossB = Math.Clamp(Vector3.Dot(nearA - b0, d) / MathF.Max(d.LengthSquared(), 1e-9f), 0f, 1f);
        var nearB = b0 + d * acrossB;
        var line = nearB - nearA;
        if (Face(bottomA, topA, along, nearB, b0, b1, widthB, within) is { } outA)
            line = outA; // the first's end into the second: from the first, out of its end
        else if (Face(b0, b1, acrossB, nearA, bottomA, topA, widthA, within) is { } outB)
            line = -outB; // the second's end into the first: towards the second, into its end
        else if (line.LengthSquared() < 1e-6f)
            line = gap; // their axes crossing: the line between their middles stands in for it
        var length = line.Length();
        return (apart, length > 1e-6f ? -Vector3.Dot(line, relative) / length : relative.Length());

        // Whether the end of the axis p0 to p1 nearest the other (at along) hits it face on: the
        // other's nearest point within their widths of the axis's line, and the other's body (its
        // axis q0 to q1 and as far out from it as `width`, which reaches along the end's way out
        // by width times the sine of the angle between them) wholly beyond the end's face. So two
        // side by side, along each other, hit side to side whichever end the nearest points fall
        // at, and so does one swept sideways across the other's end: its side is what strikes.
        // If so, the way out of that end.
        static Vector3? Face(Vector3 p0, Vector3 p1, float along, Vector3 near, Vector3 q0, Vector3 q1, float width, float within)
        {
            if (along > 1e-4f && along < 1f - 1e-4f)
                return null;
            var axis = p1 - p0;
            var length = axis.Length();
            if (length < 1e-6f)
                return null;
            var (end, outward) = along >= 0.5f ? (p1, axis / length) : (p0, -axis / length);
            var other = q1 - q0;
            var lengthOther = other.Length();
            var cos = lengthOther > 1e-6f ? Vector3.Dot(other / lengthOther, outward) : 0f;
            var reach = width * MathF.Sqrt(MathF.Max(0f, 1f - cos * cos));
            if (MathF.Min(Vector3.Dot(q0 - end, outward), Vector3.Dot(q1 - end, outward)) < reach)
                return null;
            var between = near - end;
            return (between - outward * Vector3.Dot(between, outward)).Length() <= within ? outward : null;
        }
    }

    /// <summary>
    /// Two vessels under formula touching, closing at <paramref name="closing"/>: each closing faster
    /// than its <see cref="Vessel.ImpactTolerance"/> is wrecked as part 1 wrecks a vessel hit by
    /// another, its centre of mass where it was then (<paramref name="atA"/>, <paramref name="atB"/>),
    /// by the other's body; under it, a bump: nothing changes (no bounce under formula).
    /// </summary>
    internal static void Wreck(Vessel a, Vector3 atA, Vessel b, Vector3 atB, float closing)
    {
        if (closing > a.ImpactTolerance)
            a.WreckByFormula(Vessel.HitAnotherVessel(closing), atA, hit: b.Craft);
        if (closing > b.ImpactTolerance)
            b.WreckByFormula(Vessel.HitAnotherVessel(closing), atB, hit: a.Craft);
    }
}
