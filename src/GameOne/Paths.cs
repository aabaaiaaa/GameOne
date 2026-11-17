using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace GameOne;

/// <summary>
/// The predicted path ahead of the vessel being flown (its <see cref="Orbit"/>), drawn over the
/// view: not at all close by it, so the view is clean while flying, fading in as the camera pulls
/// back and full when zoomed out. The part behind a body is hidden, as the body would hide it.
/// </summary>
/// <remarks>
/// <para>On it: a dot where the vessel is; its highest and lowest points ahead, with their heights;
/// where it comes down into the air, and where it would hit the ground. With a target (see
/// <see cref="Markers"/>), the target's own path too, if it is flying; the closest the two come in
/// the lap ahead, a mark on each and the distance between; and the window to it, shaded on the
/// path: where one burn would take the vessel to it (or, for a place on the ground, bring it down
/// there), brightest where it would come closest; a de-orbit window may be a lap on (see
/// <see cref="Encounters.Deorbit"/>). Only places and stretches, never a moment to act: when to
/// burn is the pilot's to judge.</para>
/// <para>Drawn by ImGui as lines between projected points rather than by the renderer, so it is the
/// same thin line however far off it is (and costs next to nothing). The bodies are the only things
/// it is hidden by, which is worked out here: the vessels are too small to matter at that distance.</para>
/// </remarks>
internal sealed class Paths
{
    /// <summary>
    /// Paths whose window is worked on for <paramref name="workMilliseconds"/> each
    /// <see cref="Build"/> (real time; by default <see cref="Scene.WindowWorkMilliseconds"/>, as the
    /// game has it). Zero is one step of it a Build.
    /// </summary>
    public Paths(double workMilliseconds = Scene.WindowWorkMilliseconds) => _workMilliseconds = workMilliseconds;

    private static readonly (byte R, byte G, byte B) MarkColor = (225, 240, 255), AirColor = (140, 200, 255),
        GroundColor = (255, 120, 90), WindowColor = (140, 255, 150);

    /// <summary>The target's colour: its path and closest approach here, and the markers towards and away from it.</summary>
    internal static readonly (byte R, byte G, byte B) TargetColor = (255, 175, 80);

    /// <summary>The window takes a while to work out, so is started no more often than this (real seconds, from the start of one to the next): each start allocates its steps.</summary>
    private const double PlanEverySeconds = 0.2;

    /// <summary>A plan being worked out that no <see cref="Build"/> has touched for this long (real seconds: the path not drawn meanwhile) is started again when it is picked up.</summary>
    private const double PickedUpSeconds = 2 * PlanEverySeconds;

    /// <summary>The colour of the path the nodes plan, and of the nodes (see <see cref="NodeEditor"/>).</summary>
    public static readonly (byte R, byte G, byte B) PlanColor = (215, 150, 255);

    /// <summary>A mark on the line: a dot with its label (an Ap, a Pe, where it comes into the air).</summary>
    internal readonly record struct Mark(Vector3 At, (byte R, byte G, byte B) Color, string Label);

    /// <summary>A predicted collision between two vessels as drawn: a cross where they hit, labelled with their names, in how long and how fast.</summary>
    internal readonly record struct CollisionMark(Vector3 At, string Label);

    /// <summary>The stretch of <paramref name="Vessel"/>'s path into a collision, as drawn, as strongly as <paramref name="Alpha"/>.</summary>
    internal readonly record struct LeadIn(int Vessel, float Alpha, List<Vector3> Points);

    /// <summary>Pieces a collision's lead-in is drawn in: two minutes of a lap of nine or so, smooth enough at this.</summary>
    private const int LeadSegments = 24;

    private readonly List<Vector3> _line = [], _theirLine = [], _nodes = [];
    private readonly List<List<Vector3>> _legs = [], _legPool = [];
    private readonly List<Mark> _marks = [];
    // The map's collisions as drawn (see BuildCollisions): each one's cross, and the lead-ins into it
    // (their points' lists kept and handed back to the pool each Build); each cross's label, by
    // the vessels it is for, built again only when what it shows changes (once a second, as its
    // time to go does).
    private readonly List<CollisionMark> _collisionMarks = [];
    private readonly List<LeadIn> _leadIns = [];
    private readonly List<List<Vector3>> _leadPool = [];
    private readonly List<(Vessel? A, Vessel? B, long Seconds, long Speed, string Text)> _collisionLabels = [];
    private readonly List<(Vector3 At, double Seconds)> _samples = [];
    private readonly List<Encounters.WindowPoint> _window = [];
    // The last worked out: the window as points in the world (where on the orbit it is would be
    // measured from a periapsis that, on a nearly round orbit, wanders from one frame to the next),
    // as the ground was at the world time it was worked out (_plannedWorldTime), each with its
    // seconds on from then; and where each is drawn this frame.
    private readonly List<(Vector3 At, double Seconds, float Quality, bool Joined)> _windowAt = [];
    private readonly List<(Vector3 At, float Quality, bool Joined, bool NextLap)> _windowLine = [];
    private int _windowLap = -1; // the last lap ahead the window reaches (Encounters.Deorbit): 0 this one, -1 none
    private double _plannedPeriod = double.PositiveInfinity; // the lap of the orbit the window was worked out on
    private Orbit _plannedOrbit; // and that orbit, for where a stretch the vessel is part way through starts (LayWindow)
    private (Vector3 Mine, Vector3 Theirs, float Distance)? _closest;
    private string _landingLabel = "", _laterLabel = "";
    // What the labels were last built for (see GroundLabel), so they aren't built again every frame.
    private double _landingLabelFor = double.NaN, _laterLabelFor = double.NaN;
    // The Ap and Pe labels, one of each for each leg of the path after the last node (a stretch
    // is patched into no more than Scene.PatchedLegs), so legs with marks of their own don't build
    // each other's again every frame.
    private readonly string[] _apLabels = [.. Enumerable.Repeat("", Scene.PatchedLegs)], _peLabels = [.. Enumerable.Repeat("", Scene.PatchedLegs)];
    private readonly string[] _anLabels = [.. Enumerable.Repeat("", Scene.PatchedLegs)], _dnLabels = [.. Enumerable.Repeat("", Scene.PatchedLegs)];
    private readonly double[] _anLabelsFor = [.. Enumerable.Repeat(double.NaN, Scene.PatchedLegs)], _dnLabelsFor = [.. Enumerable.Repeat(double.NaN, Scene.PatchedLegs)];
    private readonly double[] _apLabelsFor = [.. Enumerable.Repeat(double.NaN, Scene.PatchedLegs)], _peLabelsFor = [.. Enumerable.Repeat(double.NaN, Scene.PatchedLegs)];
    private string _closestLabel = "";
    private double _closestLabelFor = double.NaN;
    // The path as it is, patched (with nodes: without, the plan's legs are it); a flying target's,
    // round the body it is at; a straight up and down leg's few points among the stars.
    private readonly List<PathLeg> _current = new(Scene.PatchedLegs), _theirLegs = new(Scene.PatchedLegs);
    private readonly List<Vector3> _radial = [];
    private int _firstNodeLeg = -1; // the leg the first node's burn is made at the end of (-1 none): up to it, the path as it is
    private bool _targetIsBody;
    private bool _planned, _theirsFlying;
    private long _plannedAt, _builtAt;
    private double _plannedWorldTime;
    // Whether a window is published (_windowAt and the later laps: one found or none), and the
    // target's orbit or place it was worked out against, which it is held to every frame (Build).
    private bool _published;
    private Orbit? _plannedTheirs;
    private Vector3? _plannedStaysAt;
    // Who the window is for: the target, and the vessel flown (one Paths serves every vessel, so a
    // plan for one is never finished or drawn for another). The game has one Flight, which a
    // restart or a world loaded starts again with every vessel in it a new one, so the vessel
    // stands for the flight too. Held only until the next Build, which replaces it.
    private NavTarget? _plannedFor;
    private Vessel? _plannedVessel;
    // The window being worked out (Plan, Work), into _window: the steps left, and the orbit (and
    // the target's orbit or place), world time and closeness it was started with, and the lap the
    // steps have said it reaches.
    private IEnumerator<int>? _working;
    // How long each Build works on it (real milliseconds). How much of a plan that gets through
    // depends on the machine and on how warm the code is: a fast one can finish a whole plan in
    // one Build. Zero is one step of it a Build, the same on every machine.
    private readonly double _workMilliseconds;
    private Orbit _workingOrbit;
    private Orbit? _workingTheirs;
    private Vector3? _workingStaysAt;
    private double _workingWorldTime;
    private float _workingWithin;
    private int _workingLap = -1;
    private readonly float[] _coarse = new float[Encounters.WindowSteps];
    // The laps after this one the window reaches, worked out with it (each its own orbit, found
    // against the ground as it will be: Orbit.NextLap), so not every frame: each lap's path among
    // the stars, its points with their seconds on from the plan; where each lap's points end in
    // the list; and where it comes down, if it does (on the ground, and among the stars). The
    // working ones become the drawn ones as the window does.
    private List<(Vector3 At, double Seconds)> _laterSamples = [], _workingLaterSamples = [];
    private List<(int End, Vector3? Landing, Vector3 EndAt)> _laterLaps = [], _workingLaterLaps = [];
    private Matrix4x4 _view;
    private Vector3 _camera;
    private Vector2 _display;
    private BodyFrames? _frames; // the bodies' frames as this frame is drawn (Draw's), for what is at another body and what hides it
    private float _fade;

    /// <summary>How strongly the path shows this frame, 0 (not at all, close by the vessel) to 1 (zoomed out).</summary>
    public float Fade => _fade;

    /// <summary>
    /// The path the vessel is on now, as drawn in the flight's line frame (<see cref="Flight.LineFrame"/>),
    /// from the last <see cref="Build"/>: each leg of it patched at the spheres of influence
    /// (<see cref="Patched"/>) round its own body, one after the other (a lap, or to where it comes
    /// down or out), or over the ground on round to a de-orbit window on a later lap.
    /// </summary>
    internal IReadOnlyList<Vector3> Line => _line;

    /// <summary>With nodes planned, each leg of the plan as drawn (the path to the first node with the engine off, then each node's on, each patched leg round its own body); none without.</summary>
    internal IReadOnlyList<IReadOnlyList<Vector3>> Legs => _legs;

    /// <summary>The marks on the path after the last node (Ap, Pe, Air: each leg's that it reaches, round its own body), as drawn.</summary>
    internal IReadOnlyList<Mark> Marks => _marks;

    /// <summary>A flying target's own path, as drawn (empty for one that stays put): a body's round its parent; a vessel's patched, round the body it is at.</summary>
    internal IReadOnlyList<Vector3> TheirLine => _theirLine;

    /// <summary>
    /// With a flying target, the closest the two come on the path after the last node, as drawn:
    /// the mark on each one's path, and the distance between; for a body, the mark on its centre
    /// and the height over its datum (an encounter's periapsis's: see <see cref="Encounters.BodyApproach"/>).
    /// </summary>
    internal (Vector3 Mine, Vector3 Theirs, float Distance)? Closest => _closest;

    /// <summary>The predicted collisions between vessels, as drawn from the last <see cref="Build"/>: each one's cross (see <see cref="Flight.MapCollisions"/>).</summary>
    internal IReadOnlyList<CollisionMark> CollisionMarks => _collisionMarks;

    /// <summary>The stretches of path into the collisions, two to each, as drawn from the last <see cref="Build"/>.</summary>
    internal IReadOnlyList<LeadIn> LeadIns => _leadIns;

    /// <summary>How many collisions' crosses the last <see cref="Draw"/> put on the screen (in view, and not behind the planet).</summary>
    internal int CollisionsDrawn { get; private set; }

    /// <summary>Where each node the plan reaches is drawn (see <see cref="NodeEditor"/>).</summary>
    internal IReadOnlyList<Vector3> Nodes => _nodes;

    /// <summary>Where the path after the last node comes down, if it does: a place on the ground, the same in either frame (the end of the surface line).</summary>
    internal Vector3? LandingMark { get; private set; }

    /// <summary>
    /// Where a later lap the line is drawn round to (for a de-orbit window on it) comes down, if it
    /// does, as drawn: over the ground, the end of the surface line; among the stars, on the
    /// ellipse, marked as the next lap's (<c>NextLap</c>).
    /// </summary>
    internal (Vector3 At, bool NextLap)? LaterLanding { get; private set; }

    /// <summary>
    /// Draws the path, its marks, and with nodes planned (<paramref name="plan"/>, worked out from
    /// where the vessel is now), the path they plan: the marks then are for that, after the last
    /// node, and the orbit as it is shows faintly.
    /// </summary>
    public void Draw(ImDrawListPtr draw, Flight flight, ChaseCamera camera, Vector2 display, ManoeuvrePlan plan, BodyFrames frames)
    {
        var vessel = flight.Controlled;
        CollisionsDrawn = 0;
        (_view, _camera, _display, _frames) = (camera.ViewProjection(display), camera.Position, display, frames);
        // The map's system, zoomed out, whatever the flown vessel is doing: faded in as the camera
        // pulls back from what it follows, as the path is.
        _fade = MapFade(camera);
        if (_fade >= 0.01f)
        {
            Map.Build(flight, frames, camera.Position);
            Map.Lay(_view, camera.Position, display);
            Map.Draw(draw, _fade, Labels);
        }
        else
            Map.Forget();
        _fade = 0f;
        if (vessel.State != FlightState.Flying)
            return;
        // By the camera's distance to the centre of mass, where the line starts, not the body's
        // origin at its foot.
        _fade = SmoothStep(Scene.PathFadeFrom, Scene.PathFadeTo, Vector3.Distance(camera.Position, vessel.CentreOfMass));
        if (_fade < 0.01f)
            return;
        Build(flight, plan);

        if (_theirsFlying)
            DrawLine(draw, _theirLine, TargetColor, 0.55f, Scene.PathWidth);
        var pathColor = ((byte)(Scene.PathColor.X * 255), (byte)(Scene.PathColor.Y * 255), (byte)(Scene.PathColor.Z * 255));
        // With a plan: the orbit as it is, faintly; over it, the path to the first node, then the plan.
        DrawLine(draw, _line, pathColor, _planned ? 0.3f : 0.9f, Scene.PathWidth);
        for (var i = 0; i < _legs.Count; i++)
            DrawLine(draw, _legs[i], i <= _firstNodeLeg ? pathColor : PlanColor, 0.9f, Scene.PathWidth);
        Window(draw);
        // The collisions: each one's lead-ins (faint for a vessel neither flown nor the target, whose
        // path is otherwise not drawn at all), then its cross over them, soonest first; one within
        // a few pixels of a cross already drawn is in its stack, which the soonest's label stands for.
        foreach (var lead in _leadIns)
            DrawLine(draw, lead.Points, Scene.CollisionColor, lead.Alpha, Scene.PathWidth);
        foreach (var collision in _collisionMarks)
        {
            if (!InView(collision.At, out var at))
                continue;
            var stacked = false;
            for (var i = 0; i < CollisionsDrawn && !stacked; i++)
                stacked = Vector2.DistanceSquared(new Vector2(at.X, at.Y), new Vector2(_crossesAt[i].X, _crossesAt[i].Y)) < Scene.CollisionMergePixels * Scene.CollisionMergePixels;
            if (stacked || !Cross(draw, collision.At, Scene.CollisionColor, collision.Label))
                continue;
            _crossesAt[CollisionsDrawn++] = at;
        }
        foreach (var mark in _marks)
            DrawMark(draw, mark.At, mark.Color, mark.Label);
        if (LandingMark is { } ground)
            Cross(draw, ground, GroundColor, _landingLabel);
        if (LaterLanding is { } later)
            Cross(draw, later.At, GroundColor, _laterLabel, later.NextLap ? Scene.NextLapWindowAlpha : 1f);
        if (_closest is { } closest)
        {
            var shownMine = InView(closest.Mine, out var a);
            var shownTheirs = InView(closest.Theirs, out var b);
            if (shownMine && shownTheirs)
                draw.AddLine(a, b, Color(TargetColor, 0.6f), 1f);
            if (shownTheirs)
                draw.AddCircle(b, 5f, Color(TargetColor, 1f), 0, 2f);
            if (shownMine)
            {
                draw.AddCircle(a, 5f, Color(TargetColor, 1f), 0, 2f);
                Label(a + new float2(9f, -8f), a, TargetColor, DistanceLabel(ref _closestLabel, ref _closestLabelFor, "Closest ", closest.Distance));
            }
        }

        // Where the vessel is on it: from out there it is far too small to see. Its centre of mass,
        // where the line starts: the orbit is that point's, as Jolt's velocity is.
        if (InView(vessel.CentreOfMass, out var here))
        {
            draw.AddCircleFilled(here, 4.5f, Color((255, 255, 255), 0.95f));
            draw.AddCircle(here, 4.5f, Color((0, 0, 0), 0.6f), 0, 1.5f);
        }
    }

    /// <summary>
    /// How strongly the map's system shows from <paramref name="camera"/>: 0 by the vessel, fading in
    /// to 1 zoomed out as it pulls back from what it follows. The lens flare is dimmed by it too
    /// (<see cref="Rendering.FrameInput.MapShown"/>), so the two go together.
    /// </summary>
    internal static float MapFade(ChaseCamera camera) =>
        camera.ZoomedOut ? SmoothStep(Scene.PathFadeFrom, Scene.PathFadeTo, Vector3.Distance(camera.Position, camera.Focus)) : 0f;

    /// <summary>What the map draws of the system, zoomed out (<see cref="GameOne.SystemMap"/>), as last drawn.</summary>
    internal SystemMap Map { get; } = new();

    /// <summary>
    /// Works out everything <see cref="Draw"/> draws, where it is drawn in the flight's line frame
    /// (<see cref="Flight.LineFrame"/>): over the ground, the vessel's real track over the turning
    /// ground, at its height, ending where it comes down; among the stars, the orbit's ellipse.
    /// Every point goes through its leg's <see cref="LegPlace"/> with its time from now, so the
    /// marks and the nodes stay on the line in either, and a leg round another body (the moon's,
    /// on a path from the planet) is drawn as that body sees it, where the path comes to it.
    /// </summary>
    internal void Build(Flight flight, ManoeuvrePlan plan)
    {
        var frame = flight.LineFrame;
        var now = flight.WorldTime;
        var view = flight.Body; // the frame everything is drawn in: the flown vessel's body's
        var mine = plan.Legs[0].Orbit; // the orbit it is on now, round the body it is at
        _planned = plan.NodeLeg(0) is not null;
        var last = plan.Last;
        var final = last.Orbit;
        var finalFrom = last.Start - now; // how far ahead the last leg starts
        _firstNodeLeg = plan.NodeLeg(0) ?? -1;

        // The path as it is, patched at the spheres of influence: with no node reached, the plan's
        // legs are just that; with one, it is patched again from the orbit it is on now.
        IReadOnlyList<PathLeg> current = plan.Legs;
        if (_planned)
        {
            var (position, velocity) = mine.StateAfter(0);
            Patched.Legs(mine.Body, position, velocity, now, _current);
            current = _current;
        }
        _line.Clear();
        for (var i = 0; i < current.Count; i++)
            Fill(_line, current[i], view, now, frame, last: i == current.Count - 1);

        // The target: on a path of its own (a body round its parent, a vessel flying round the
        // body it is at), or staying put (a place, or a vessel on the ground), as the body it is at
        // has it now. A transfer window is worked out to a body going round the body the vessel
        // is at (arriving within its sphere of influence) or to a vessel round it (arriving within
        // Scene.TransferWithin), and a de-orbit window to a place on its ground: one at another
        // body (a vessel round the moon, seen from the planet) has none, but its path is drawn
        // round its own body, and the closest approach is found on the leg round that body.
        Orbit? theirs = null;
        Vector3? staysAt = null;
        var within = Scene.TransferWithin;
        GroundBody? targetBody = null; // a body targeted, or the body a vessel or place targeted is at
        var targetOwn = Vector3.Zero; // a target staying put, in its own body's frame
        Vector3? planeNormal = null; // the plane of a flying target's orbit (its normal, among the stars), round planeBody
        GroundBody? planeBody = null;
        _theirLine.Clear();
        _theirLegs.Clear();
        _targetIsBody = flight.Target is { Kind: TargetKind.Body };
        if (flight.TargetAt is { } found)
        {
            targetBody = found.Body;
            var (own, ownVelocity) = (found.Own, found.OwnVelocity);
            targetOwn = own;
            if (_targetIsBody)
            {
                if (found.Body.Parent is { } parent)
                {
                    var round = Bodies.OrbitOf(found.Body, now);
                    (planeNormal, planeBody) = (round.Normal, parent);
                    if (parent == view)
                        (theirs, within) = (round, found.Body.SphereRadius);
                    Fill(_theirLine, new PathLeg(parent, round, now, double.PositiveInfinity), view, now, frame, last: true);
                }
            }
            else if (found.Flying)
            {
                Patched.Legs(found.Body, own, found.Body.AmongStars(own, ownVelocity), now, _theirLegs);
                for (var i = 0; i < _theirLegs.Count; i++)
                    Fill(_theirLine, _theirLegs[i], view, now, frame, last: i == _theirLegs.Count - 1);
                (planeNormal, planeBody) = (_theirLegs[0].Orbit.Normal, found.Body);
                if (found.Body == view)
                    theirs = _theirLegs[0].Orbit;
            }
            else if (found.Body == view)
                staysAt = found.Position;
        }
        _theirsFlying = _theirLine.Count > 0;

        _legPool.AddRange(_legs);
        _legs.Clear();
        _nodes.Clear();
        if (_planned)
            for (var i = 0; i < plan.Legs.Count; i++)
            {
                var points = _legPool.Count > 0 ? _legPool[^1] : [];
                if (_legPool.Count > 0)
                    _legPool.RemoveAt(_legPool.Count - 1);
                points.Clear();
                Fill(points, plan.Legs[i], view, now, frame, last: i == plan.Legs.Count - 1);
                _legs.Add(points);
            }
        // Each node, by its own index, at the end of the leg its burn is made at, placed as that leg
        // is (never the last: the leg after the burn follows it).
        for (var i = 0; _planned && plan.NodeLeg(i) is { } leg; i++)
            _nodes.Add(new LegPlace(plan.Legs[leg], view, now, last: false).Point(frame, plan.NodeAt(i)!.Value.Position, plan.Legs[leg].End - now));

        // The window (one burn there would take it to the target, or down by it), worked out on
        // the orbit as it is, a little each frame, and only when it needs to be: a plan is a
        // couple of milliseconds a frame for tens of frames, which staying on the same orbit
        // would spend for nothing. For another target, vessel or flight, the last one's is no
        // longer drawn: none is, until this one's is worked out (a few frames). The one drawn is
        // held to the orbit (and the target) it was worked out on every frame: once the vessel
        // has left that (a burn), it goes at once (and its later laps' marks with it), until one
        // for this orbit comes out. On its orbit with the engine off, it is worked out again only once the scan's horizon
        // has moved on (Scene.WindowReplanLaps) or all of it is passed; in the air, every so
        // often as before, as the drag changes the orbit all the time (and the air's allowance in
        // Left would grow with the time since the plan, and hide a burn). One being worked out on
        // an orbit the vessel (or the target) is no longer on is started again on this: but no
        // more often than a plan is, as each start allocates its steps, and through a burn it
        // would be every frame. Until then it isn't worked on, as it would come out for an orbit
        // already left. One picked up again after a while not drawn (nothing working on it) is
        // started again however it looks, for the air's allowance likewise; and so, in the air,
        // is the one drawn, which goes meanwhile. Only as far as the orbit is flown: to where it
        // crosses into another body's sphere of influence (or out of its own), as the line is drawn.
        var until = current[0].End - current[0].Start;
        var due = System.Diagnostics.Stopwatch.GetElapsedTime(_plannedAt).TotalSeconds >= PlanEverySeconds;
        var picked = System.Diagnostics.Stopwatch.GetElapsedTime(_builtAt).TotalSeconds >= PickedUpSeconds;
        _builtAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var stale = false;
        if (flight.Target != _plannedFor || flight.Controlled != _plannedVessel)
        {
            ClearWindow();
            Restart(mine, theirs, staysAt, now, within, until);
            (_plannedFor, _plannedVessel) = (flight.Target, flight.Controlled);
        }
        else
        {
            // The one drawn first, whatever else is done this frame (a plan picked up and started
            // again included): picked up after a while, in the air, the allowance would hide a
            // burn made meanwhile.
            if (_published && (picked && PlannedInAir()
                || Drifted(_plannedOrbit, _plannedTheirs, _plannedStaysAt, _plannedWorldTime, mine, theirs, staysAt, now)))
                ClearWindow();
            if (_working is not null && picked)
                Restart(mine, theirs, staysAt, now, within, until);
            else if (_working is not null)
            {
                if (Drifted(_workingOrbit, _workingTheirs, _workingStaysAt, _workingWorldTime, mine, theirs, staysAt, now))
                {
                    if (due)
                        Restart(mine, theirs, staysAt, now, within, until);
                    else
                        stale = true;
                }
            }
            else if (due && Wanted(now))
                Restart(mine, theirs, staysAt, now, within, until);
        }
        if (!stale)
            Work(_workMilliseconds);
        // Over the ground each lap is a track of its own, west of the last, so a window on a later
        // lap is only on the line if the line goes on round to it: then it does, each lap over the
        // ground as it will be by then, as far as the furthest of the window still ahead (counted
        // each frame, so once the vessel is round to the window's lap, the line is one lap again).
        // Among the stars the ellipse is the same every lap, so the line stays one lap and the
        // window is marked as the next lap's instead (LayWindow). A later lap that comes down on
        // the ground (a peak it misses this lap) has its landing marked in either. This lap is
        // from now, but the later laps as planned run on from the plan's lap's end, which the line
        // is past by the time since the plan: so they join on where this lap ends (the points
        // before that would take the line back to the plan's lap's end and over the ground again
        // from there), and the line ends where they do. One that comes down this lap has none.
        LaterLanding = null;
        var laps = 0;
        if (_windowAt.Count > 0 && mine.Closed && current.Count == 1)
        {
            var furthest = _windowAt[^1].Seconds - (now - _plannedWorldTime); // seconds from now
            laps = Math.Clamp((int)(furthest / _plannedPeriod), 0, _laterLaps.Count);
        }
        var lapEnds = now - _plannedWorldTime + mine.PathSeconds; // where this lap ends, in seconds from the plan
        for (var i = 0; i < laps; i++)
        {
            var (end, landing, endAt) = _laterLaps[i];
            if (frame == Frame.Surface)
                for (var k = i == 0 ? 0 : _laterLaps[i - 1].End; k < end; k++)
                    if (_laterSamples[k].Seconds > lapEnds)
                        _line.Add(WindowPoint(frame, _laterSamples[k].At, _laterSamples[k].Seconds, _plannedWorldTime, now, _plannedOrbit.Body));
            if (landing is { } ground)
            {
                LaterLanding = frame == Frame.Surface ? (ground, false) : (WindowPoint(frame, endAt, 0, _plannedWorldTime, now, _plannedOrbit.Body), true);
                if (frame == Frame.Orbital)
                    (_laterLabel, _laterLabelFor) = ("Next lap: Ground", double.NaN);
                else
                    GroundLabel(ref _laterLabel, ref _laterLabelFor, staysAt is { } site ? Vector3.Distance(ground, site) : null);
            }
        }
        LayWindow(frame, now);

        // The marks, for the path after the last node (the path as it is, with none): each leg's
        // that it reaches before the next crossing, round its own body and placed as its leg is.
        _marks.Clear();
        var markColor = _planned ? PlanColor : MarkColor;
        var firstFinal = plan.Final;
        for (var i = firstFinal; i < plan.Legs.Count; i++)
        {
            var leg = plan.Legs[i];
            var orbit = leg.Orbit;
            if (orbit.Radial)
                continue;
            var place = new LegPlace(leg, view, now, last: i == plan.Legs.Count - 1);
            var (startsIn, span, slot) = (leg.Start - now, leg.End - leg.Start, Math.Min(i - firstFinal, _apLabels.Length - 1));
            if (orbit.NextApoapsis is { } apoapsis && orbit.TimeTo(apoapsis) <= span)
                _marks.Add(new Mark(place.Point(frame, orbit.At(apoapsis), startsIn + orbit.TimeTo(apoapsis)), markColor,
                    DistanceLabel(ref _apLabels[slot], ref _apLabelsFor[slot], "Ap ", MarkHeight(orbit, apoapsis))));
            if (orbit.NextPeriapsis is { } periapsis && orbit.TimeTo(periapsis) <= span)
                _marks.Add(new Mark(place.Point(frame, orbit.At(periapsis), startsIn + orbit.TimeTo(periapsis)), markColor,
                    DistanceLabel(ref _peLabels[slot], ref _peLabelsFor[slot], "Pe ", MarkHeight(orbit, periapsis))));
            // Where it crosses a flying target's plane, only while the target is round the same body
            // as this leg (information: the angle between the planes, never when to burn).
            if (planeNormal is { } targetNormal && planeBody == leg.Body && PlaneCrossings.Of(orbit, targetNormal) is var (ascending, descending, inclination))
            {
                var degrees = inclination * 180f / MathF.PI;
                if (orbit.Ahead(ascending) && orbit.TimeTo(ascending) <= span)
                    _marks.Add(new Mark(place.Point(frame, orbit.At(ascending), startsIn + orbit.TimeTo(ascending)), TargetColor,
                        AngleLabel(ref _anLabels[slot], ref _anLabelsFor[slot], "AN ", degrees)));
                if (orbit.Ahead(descending) && orbit.TimeTo(descending) <= span)
                    _marks.Add(new Mark(place.Point(frame, orbit.At(descending), startsIn + orbit.TimeTo(descending)), TargetColor,
                        AngleLabel(ref _dnLabels[slot], ref _dnLabelsFor[slot], "DN ", degrees)));
            }
            var body = leg.Body;
            if (body.HasAir && body.Altitude(orbit.PositionAfter(0)) > Scene.AtmosphereHeight
                && orbit.Descending(body.SeaRadius + Scene.AtmosphereHeight) is { } air && orbit.TimeTo(air) <= span)
                _marks.Add(new Mark(place.Point(frame, orbit.At(air), startsIn + orbit.TimeTo(air)), AirColor, "Air"));
        }
        // Where it comes down is a place on the ground, as it will have turned by then: marked
        // there, on the ground as it is now, not at the path's end among the stars (where the
        // surface line ends). On another body's ground it is where the line drawn round that body
        // comes down, placed as its leg is.
        LandingMark = null;
        if (final.EndsOnGround)
        {
            var place = new LegPlace(last, view, now, last: true);
            var down = final.From + final.SecondsToEnd;
            LandingMark = place.Same ? final.Landing : place.Point(frame, final.Body.Turned(final.Landing, down), down);
            // From a target staying put on the ground it comes down on (a place, or a landed vessel), how far.
            GroundLabel(ref _landingLabel, ref _landingLabelFor,
                flight.TargetAt is { Flying: false } && targetBody == last.Body ? Vector3.Distance(final.Landing, targetOwn) : null);
        }

        // The closest the path after the last node comes to the target, if it is flying (a body,
        // or a vessel on a path of its own): the closest of every leg round the body it is at
        // (each only over its own span, where it is drawn; for a body, its encounters' periapses
        // too): worked out every frame, so it follows a node's handles as they are dragged, and
        // placed as its leg is. A body's distance is shown over its datum, as its height would be.
        _closest = null;
        if (_targetIsBody && targetBody is { } targeted)
        {
            if (Encounters.BodyApproach(targeted, plan.Legs, now, firstFinal) is { } approach)
                _closest = Placed(plan, approach, view, now, frame, approach.Distance - targeted.SeaRadius);
        }
        else if (_theirLegs.Count > 0 && Encounters.VesselApproach(_theirLegs[0].Orbit, plan.Legs, now, firstFinal) is { } closest)
            _closest = Placed(plan, closest, view, now, frame, closest.Distance);

        BuildCollisions(flight, frame, now);
    }

    /// <summary>A closest approach on one of <paramref name="plan"/>'s legs (its places in that leg's frame) as drawn: each place as its leg is placed, and the distance shown.</summary>
    private static (Vector3 Mine, Vector3 Theirs, float Distance) Placed(ManoeuvrePlan plan, in Encounters.Approach approach, GroundBody view, double now, Frame frame, float shown)
    {
        var place = new LegPlace(plan.Legs[approach.Leg], view, now, last: approach.Leg == plan.Legs.Count - 1);
        return (place.Point(frame, approach.Mine, approach.Seconds), place.Point(frame, approach.Theirs, approach.Seconds), shown);
    }

    /// <summary>
    /// The predicted collisions between any two vessels in the world (<see cref="Flight.MapCollisions"/>,
    /// kept coming while the map is drawn: <see cref="Flight.LookForCollisions"/>), as drawn in
    /// <paramref name="frame"/>: a cross where each is, and the stretch of each moving vessel's
    /// path it covers in the <see cref="Scene.CollisionLeadSeconds"/> before (from now, if that is
    /// sooner), so a faster pair's line is longer. Only the soonest <see cref="Scene.CollisionsShown"/>,
    /// each laid once a pass (<see cref="LayCollisions"/>) and only turned to where it is drawn each
    /// frame, as the window is (<see cref="WindowPoint"/>), so a frame's cost is bounded however
    /// many there are. Not drawn: one already passed; one with a vessel wrecked since; one whose
    /// two vessels, on their orbits as they are now, would be further apart then than their sizes
    /// and <see cref="Scene.CollisionDriftMetres"/> (a dodge since the pass); and none from a pass
    /// asked over <see cref="Scene.CollisionStaleSeconds"/> ago (the map shut a while), until the
    /// next comes. The flown vessel's lead-in is as strong as its path, the target's as its path
    /// (0.55), any other's, whose path isn't drawn otherwise, fainter (<see cref="Scene.OtherPathAlpha"/>).
    /// Information only, as every mark on the map: where and when, never what to do about it.
    /// </summary>
    private void BuildCollisions(Flight flight, Frame frame, double now)
    {
        foreach (var lead in _leadIns)
            _leadPool.Add(lead.Points);
        _leadIns.Clear();
        _collisionMarks.Clear();
        flight.LookForCollisions();
        if (flight.CollisionPass != _laidPass)
            LayCollisions(flight);
        if (flight.CollisionsAge > Scene.CollisionStaleSeconds)
            return;
        var askedAt = flight.CollisionsAskedAt;
        for (var n = 0; n < _laid.Count; n++)
        {
            var laid = _laid[n];
            var (a, b, collision) = (laid.A, laid.B, laid.Collision);
            var ahead = collision.At - now;
            if (ahead <= 0 || a.State == FlightState.Crashed || b.State == FlightState.Crashed)
                continue;
            if (Vector3.Distance(LiveAt(a, ahead), LiveAt(b, ahead)) > laid.Reach + Scene.CollisionDriftMetres)
                continue;
            // The pair's body's frame (where the search had them) into the main view's, this frame.
            var toView = _frames is { } frames ? frames.ToView(laid.Body) : FrameChange.Identity;
            for (var side = 0; side < 2; side++)
            {
                if (!(side == 0 ? laid.LeadA : laid.LeadB))
                    continue; // on the ground: no path to draw
                var v = side == 0 ? a : b;
                var points = _leadPool.Count > 0 ? _leadPool[^1] : [];
                if (_leadPool.Count > 0)
                    _leadPool.RemoveAt(_leadPool.Count - 1);
                points.Clear();
                var first = (2 * n + side) * (LeadSegments + 1);
                for (var k = first; k <= first + LeadSegments; k++)
                {
                    var (at, seconds) = _laidPoints[k];
                    if (!Passed(seconds, askedAt, now))
                        points.Add(toView.Point(WindowPoint(frame, at, seconds, askedAt, now, laid.Body)).ToSingle());
                }
                var alpha = v == flight.Controlled ? 0.9f
                    : flight.Target is { Kind: TargetKind.Vessel } target && target.Id == v.Id ? 0.55f
                    : Scene.OtherPathAlpha;
                _leadIns.Add(new LeadIn(v.Id, alpha, points));
            }
            _collisionMarks.Add(new CollisionMark(toView.Point(WindowPoint(frame, collision.Where, collision.At - askedAt, askedAt, now, laid.Body)).ToSingle(),
                CollisionLabel(n, a, b, ahead, collision.ClosingSpeed)));
        }
    }

    // The collisions shown, laid out for the pass they came from (see LayCollisions): each one's two
    // vessels, the collision, how near counts (their sizes together), and whether each has a lead-in;
    // and the lead-ins' points, among the stars as at the pass's asking with their seconds on from
    // then, LeadSegments + 1 for each side of each (A's then B's), whether drawn or not.
    private readonly record struct Laid(Vessel A, Vessel B, PredictedCollision Collision, float Reach, bool LeadA, bool LeadB, GroundBody Body);
    private readonly List<Laid> _laid = new(Scene.CollisionsShown);
    private readonly List<(Vector3 At, double Seconds)> _laidPoints = new(Scene.CollisionsShown * 2 * (LeadSegments + 1));
    private int _laidPass = -1;
    private readonly float2[] _crossesAt = new float2[Scene.CollisionsShown];

    /// <summary>
    /// Lays out the soonest <see cref="Scene.CollisionsShown"/> collisions of a pass just taken (the
    /// flight's <see cref="Flight.CollisionPass"/> moved on), once: each moving vessel's lead-in along
    /// the orbit it was handed to the search on (<see cref="Flight.CollisionState"/>), so a burn since
    /// shows as the drift that hides it, not a line that bends.
    /// </summary>
    private void LayCollisions(Flight flight)
    {
        _laid.Clear();
        _laidPoints.Clear();
        _laidPass = flight.CollisionPass;
        var askedAt = flight.CollisionsAskedAt;
        // Indexed: a foreach over the IReadOnlyList would box its enumerator, once a pass.
        var collisions = flight.MapCollisions;
        for (var m = 0; m < collisions.Count; m++)
        {
            var collision = collisions[m];
            if (_laid.Count >= Scene.CollisionsShown)
                break;
            var (a, b) = (flight.CollisionVessel(collision.SlotA), flight.CollisionVessel(collision.SlotB));
            if (a.State == FlightState.Crashed || b.State == FlightState.Crashed)
                continue;
            ref readonly var stateA = ref flight.CollisionState(collision.SlotA);
            ref readonly var stateB = ref flight.CollisionState(collision.SlotB);
            var aheadOfAsking = collision.At - askedAt;
            Lay(stateA, aheadOfAsking);
            Lay(stateB, aheadOfAsking);
            _laid.Add(new Laid(a, b, collision, stateA.Size + stateB.Size, !stateA.OnGround, !stateB.OnGround, stateA.Body));
        }
    }

    /// <summary>One side's lead-in points (see <see cref="LayCollisions"/>): along its orbit, or, on the ground, where it stays (never drawn).</summary>
    private void Lay(in SearchVessel state, double ahead)
    {
        // Only its shape: finding where it meets the terrain would sample the ground, for a line two minutes long.
        var orbit = state.OnGround ? default : new Orbit(state.Body, state.Position, state.StarsVelocity, findEnd: false);
        var from = Math.Max(0, ahead - Scene.CollisionLeadSeconds);
        for (var k = 0; k <= LeadSegments; k++)
        {
            var seconds = from + (ahead - from) * k / LeadSegments;
            _laidPoints.Add((state.OnGround ? state.Position : orbit.PositionAfter(seconds), seconds));
        }
    }

    /// <summary>Where <paramref name="v"/> will be <paramref name="ahead"/> seconds from now, as it is moving now: on its orbit, or on the ground where it is.</summary>
    private static Vector3 LiveAt(Vessel v, double ahead) => v.State == FlightState.Flying
        ? Orbit.OverGround(v.Body, v.CentreOfMass, v.Velocity, findEnd: false).PositionAfter(ahead)
        : v.Body.Turned(v.CentreOfMass, ahead);

    /// <summary>
    /// The <paramref name="slot"/>th cross's label: the two vessels' names, in how long and how
    /// fast they close; built again only when what it shows changes (the vessels, to the second,
    /// and the metre a second), not as a new string every frame.
    /// </summary>
    private string CollisionLabel(int slot, Vessel a, Vessel b, double ahead, float speed)
    {
        var (seconds, metres) = ((long)Math.Ceiling(ahead), (long)MathF.Round(speed));
        while (_collisionLabels.Count <= slot)
            _collisionLabels.Add((null, null, -1, -1, ""));
        var shown = _collisionLabels[slot];
        if (shown.A == a && shown.B == b && shown.Seconds == seconds && shown.Speed == metres)
            return shown.Text;
        var time = seconds < 60 ? $"{seconds} s" : $"{seconds / 60}:{seconds % 60:00}";
        var text = $"{WorldLog.Named(a)} and {WorldLog.Named(b)}  in {time}  {metres:#,0} m/s";
        _collisionLabels[slot] = (a, b, seconds, metres, text);
        return text;
    }

    /// <summary>
    /// Adds to <paramref name="points"/> <paramref name="leg"/>'s path, from its start to its end
    /// (the last leg's a lap, or to where it comes down or out), as drawn in <paramref name="frame"/>
    /// in <paramref name="view"/>'s frame at <paramref name="now"/>: round its own body, placed as
    /// that body is where the path comes to it, or point by point if it is not the
    /// <paramref name="last"/> (<see cref="LegPlace"/>).
    /// </summary>
    private void Fill(List<Vector3> points, in PathLeg leg, GroundBody view, double now, Frame frame, bool last)
    {
        var orbit = leg.Orbit;
        var place = new LegPlace(leg, view, now, last);
        var startsIn = leg.Start - now;
        if (orbit.Radial)
        {
            // Straight up and down among the stars: its few points, each over the ground as it
            // will be by the time it is there: where it starts, its top (if it is still rising
            // to one), and down where it lands. (An escaping one's far end, which it never
            // reaches, is over the ground as it is when it starts.)
            orbit.Path(_radial);
            var top = orbit.TopSeconds; // then the second point is the top
            for (var i = 0; i < _radial.Count; i++)
            {
                var seconds = startsIn + (i == 1 && top is { } t ? t : 0);
                points.Add(frame == Frame.Surface && orbit.EndsOnGround && i == _radial.Count - 1
                    ? place.Ground(orbit.Landing, startsIn + orbit.SecondsToEnd)
                    : place.Point(frame, _radial[i], seconds));
            }
            return;
        }
        orbit.Samples(_samples, 0, leg.End - leg.Start);
        foreach (var (at, seconds) in _samples)
            points.Add(place.Point(frame, at, startsIn + seconds));
    }

    /// <summary>
    /// A landing mark's label, "Ground", or with <paramref name="metres"/> from a target, how far:
    /// built again only when what it shows changes (<paramref name="shownFor"/>, see
    /// <see cref="ShownAs"/>; infinity for none), not as a new string every frame.
    /// </summary>
    private static void GroundLabel(ref string label, ref double shownFor, float? metres)
    {
        var key = metres is { } m ? ShownAs(m) : double.PositiveInfinity;
        if (key == shownFor)
            return;
        label = metres is { } d ? $"Ground  {Markers.Distance(d)} from target" : "Ground";
        shownFor = key;
    }

    /// <summary>
    /// <paramref name="prefix"/> and a distance, as <see cref="Markers.Distance"/> shows it (an
    /// Ap's or Pe's height, the closest approach): built again only when what it shows changes
    /// (<paramref name="shownFor"/>, see <see cref="ShownAs"/>), not as a new string every frame.
    /// </summary>
    private static string DistanceLabel(ref string label, ref double shownFor, string prefix, float metres)
    {
        var key = ShownAs(metres);
        if (key != shownFor)
            (label, shownFor) = (prefix + Markers.Distance(metres), key);
        return label;
    }

    /// <summary><paramref name="prefix"/> and an angle in degrees to a tenth (a node's plane angle): built again only when what it shows changes, as <see cref="DistanceLabel"/>'s is.</summary>
    private static string AngleLabel(ref string label, ref double shownFor, string prefix, float degrees)
    {
        var key = Math.Round(degrees, 1);
        if (key != shownFor)
            (label, shownFor) = ($"{prefix}{key:0.0}°", key);
        return label;
    }

    /// <summary>
    /// A distance as <see cref="Markers.Distance"/> shows it, as a number: whole metres under
    /// 10 km (negative heights too), and above that tenths of a kilometre, put far beyond any
    /// height in metres, so the two never share a value.
    /// </summary>
    private static double ShownAs(float metres) => metres < 10000f
        ? Math.Round(metres, MidpointRounding.AwayFromZero)
        : 1e12 + Math.Round(metres / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>No window drawn (nor its later laps), until the next is worked out.</summary>
    private void ClearWindow()
    {
        _windowAt.Clear();
        _windowLap = -1;
        _laterLaps.Clear();
        _laterSamples.Clear();
        _published = false;
    }

    /// <summary>
    /// The flown vessel's frame has changed to another body's
    /// (<see cref="Flight.ViewRebased"/>), so nothing kept in the old one is drawn in the new. The
    /// line, its marks, the nodes and the closest approach are laid afresh each frame in the new;
    /// the collisions' lead-ins are kept in their pair's body's frame and changed into the view's
    /// as they are drawn; what is kept from frame to frame in the old frame is the window and its
    /// later laps, worked out round the old body's orbit: dropped, and worked out afresh round the
    /// new one's (as for another vessel flown), so no stretch of it is drawn where it no longer is.
    /// </summary>
    public void Rebase()
    {
        ClearWindow();
        _plannedVessel = null; // the next Build starts a plan for the vessel as it is now
    }

    /// <summary><see cref="Plan"/>, counted as a start for the throttle on starts (<see cref="PlanEverySeconds"/>).</summary>
    private void Restart(in Orbit mine, in Orbit? theirs, Vector3? staysAt, double now, float within, double until)
    {
        Plan(mine, theirs, staysAt, now, within, until);
        _plannedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// With nothing being worked out, whether the window should be worked out again at world time
    /// <paramref name="now"/>, on an orbit the one drawn still holds to: none is drawn (it was
    /// left); its orbit, or a flying target's, dips into the air (the drag changes it all the
    /// time, and a burn would hide in the allowance for it, which grows with the time since the
    /// plan: <see cref="Left"/>); the scan's horizon
    /// has moved on by <see cref="Scene.WindowReplanLaps"/> of a lap (of the path, for one that
    /// comes down; every so often for one with no end); or all of it is passed.
    /// </summary>
    private bool Wanted(double now)
    {
        if (!_published)
            return true;
        var orbit = _plannedOrbit;
        if (orbit.Radial)
            return false; // straight up and down: no window to look for, until it is on another
        if (PlannedInAir())
            return true;
        var since = now - _plannedWorldTime;
        var lap = orbit.Closed ? orbit.Period : orbit.PathSeconds;
        if (!double.IsFinite(lap) || since >= Scene.WindowReplanLaps * lap)
            return true;
        return _windowAt.Count > 0 && Passed(_windowAt[^1].Seconds, _plannedWorldTime, now);
    }

    /// <summary>Whether the orbit the window drawn was worked out on, or the flying target's then, dips into the air.</summary>
    private bool PlannedInAir() => InAir(_plannedOrbit) || _plannedTheirs is { } theirs && InAir(theirs);

    /// <summary>Whether <paramref name="orbit"/> dips into the air, which slows it all the time.</summary>
    private static bool InAir(in Orbit orbit) => orbit.Body.HasAir && !orbit.Radial && orbit.PeriapsisDistance - orbit.Body.SeaRadius < Scene.AtmosphereHeight;

    /// <summary>
    /// Starts working out the window to the target, on <paramref name="mine"/> as it is at world
    /// time <paramref name="now"/>: a burn tried at a time (<see cref="Encounters.TransferSteps"/>,
    /// <see cref="Encounters.DeorbitSteps"/>), worked through by <see cref="Work"/>. The last one
    /// worked out is drawn meanwhile. A transfer arrives within <paramref name="within"/> of a
    /// flying target (<see cref="Scene.TransferWithin"/> of a vessel, a body's sphere of influence);
    /// a de-orbit within <see cref="Scene.DeorbitWithin"/> of a place. Burns are tried no further
    /// than <paramref name="until"/> seconds along <paramref name="mine"/> (where it crosses a
    /// sphere's edge; infinite for none).
    /// </summary>
    private void Plan(in Orbit mine, in Orbit? theirs, Vector3? staysAt, double now, float within, double until)
    {
        _working?.Dispose();
        _working = null;
        (_workingOrbit, _workingTheirs, _workingStaysAt, _workingWorldTime, _workingLap) = (mine, theirs, staysAt, now, -1);
        _workingLaterLaps.Clear();
        _workingLaterSamples.Clear();
        _workingWithin = theirs is not null ? within : Scene.DeorbitWithin;
        if (!mine.Radial && theirs is { } orbit)
            _working = PlanSteps(Encounters.TransferSteps(mine, orbit, _workingWithin, _window, _coarse, until)).GetEnumerator();
        else if (!mine.Radial && staysAt is { } place)
            _working = PlanSteps(Encounters.DeorbitSteps(mine, place, _workingWithin, _window, _coarse, until)).GetEnumerator();
        else
        {
            _window.Clear();
            Publish(); // no window to look for
        }
    }

    /// <summary>
    /// The window's steps, then the laps after this one it reaches, a step each (finding where a
    /// lap reaches the ground can take a while too).
    /// </summary>
    private IEnumerable<int> PlanSteps(IEnumerable<int> window)
    {
        foreach (var step in window)
        {
            if (step != Encounters.Working)
                _workingLap = step;
            yield return Encounters.Working;
        }
        var lap = _workingOrbit;
        for (var i = 0; i < _workingLap && lap.Closed; i++)
        {
            lap = lap.NextLap();
            lap.Samples(_samples, 0, double.PositiveInfinity);
            foreach (var (at, seconds) in _samples)
                _workingLaterSamples.Add((at, lap.From + seconds)); // seconds on from the plan, as the window's are
            _workingLaterLaps.Add((_workingLaterSamples.Count, lap.EndsOnGround ? lap.Landing : null, lap.At(lap.EndAnomaly)));
            yield return Encounters.Working;
        }
    }

    /// <summary>
    /// Whether the vessel, or the target, is no longer on the orbit (or at the place) a window was
    /// worked out (or started) on at world time <paramref name="at"/>: where that orbit would have
    /// it now is further off its own than a burn's worth, or the place has moved.
    /// </summary>
    private static bool Drifted(in Orbit planned, in Orbit? plannedTheirs, Vector3? plannedStaysAt, double at,
        in Orbit mine, in Orbit? theirs, Vector3? staysAt, double now)
    {
        var since = now - at;
        if (since < 0 || Left(planned, mine, since))
            return true;
        if (theirs.HasValue != plannedTheirs.HasValue || staysAt.HasValue != plannedStaysAt.HasValue)
            return true;
        if (theirs is { } orbit && Left(plannedTheirs!.Value, orbit, since))
            return true;
        return staysAt is { } place && Vector3.Distance(place, plannedStaysAt!.Value) > Scene.WindowDriftMetres;
    }

    /// <summary>
    /// Whether something on <paramref name="planned"/> <paramref name="since"/> seconds ago is on
    /// <paramref name="now"/> instead, an orbit of its own: further off where the planned one would
    /// have it than a burn's worth, beyond what the air could have done meanwhile. (Internal, as
    /// the tests judge by it when an orbit has been left.)
    /// </summary>
    internal static bool Left(in Orbit planned, in Orbit now, double since)
    {
        if (now.Radial || planned.Radial)
            return now.Radial != planned.Radial;
        var (position, velocity) = planned.StateAfter(since);
        var (here, moving) = now.StateAfter(0);
        // In the top of the air the drag slows it all the time, by more over a plan's frames
        // (more again under warp) than a burn's worth: that isn't a burn, and starting the plan
        // again for it, it would never come out. So the most the air could have slowed it is
        // allowed for, twice over (for what the orbit makes of that: a lower one runs ahead), in
        // its speed and in the distance that speed carries it.
        var slowing = AirSlowing(planned, since) * since;
        // Among the stars, where the ground's frame was then: turned back into this frame's.
        return Vector3.Distance(planned.Body.Turned(position, -since), here) > Scene.WindowDriftMetres + 2 * slowing * since
            || Vector3.Distance(planned.Body.TurnedDirection(velocity, -since), moving) > Scene.WindowDriftSpeed + 2 * slowing;
    }

    /// <summary>
    /// The most the air slows something on <paramref name="orbit"/> over its first
    /// <paramref name="seconds"/> (m/s each second, drag being in proportion to the speed through the air
    /// and how thick that is, as <see cref="Physics.RocketPhysics"/> applies it): as at the lowest it is
    /// in that time, its periapsis if it passes it, at its speed among the stars there plus the
    /// ground's (the air turns with it), the most its speed through the air can be.
    /// </summary>
    private static float AirSlowing(in Orbit orbit, double seconds)
    {
        var (from, to) = (orbit.Now, orbit.AnomalyAfter(seconds));
        var lowest = Math.Floor(to / Math.Tau) > Math.Floor(from / Math.Tau) ? Math.Ceiling(from / Math.Tau) * Math.Tau // a periapsis passed
            : orbit.Distance(from) < orbit.Distance(to) ? from : to;
        var distance = (float)orbit.Distance(lowest);
        var body = orbit.Body;
        var height = distance - body.SeaRadius;
        if (!body.HasAir || height >= Scene.AtmosphereHeight)
            return 0f;
        return Scene.DragRate * body.AirDensity(height) * (orbit.VelocityAt(lowest).Length() + body.Spin.Length() * distance);
    }

    /// <summary>
    /// Works on the window being worked out for <paramref name="milliseconds"/> at most, and
    /// once it is done, draws it from then on. A plan over two laps is a few hundred burns tried,
    /// some tens of milliseconds: all at once, every plan would stall a frame.
    /// </summary>
    private void Work(double milliseconds)
    {
        if (_working is null)
            return;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_working.MoveNext())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= milliseconds)
                return;
        }
        _working.Dispose();
        _working = null;
        Publish();
    }

    /// <summary>The window just worked out, for drawing until the next: as points in the world, with their seconds on from when it was started, and the laps after this one it reaches.</summary>
    private void Publish()
    {
        var mine = _workingOrbit;
        (_laterLaps, _workingLaterLaps) = (_workingLaterLaps, _laterLaps);
        (_laterSamples, _workingLaterSamples) = (_workingLaterSamples, _laterSamples);
        _windowAt.Clear();
        (_windowLap, _plannedWorldTime, _plannedOrbit) = (_workingLap, _workingWorldTime, mine);
        (_plannedTheirs, _plannedStaysAt, _published) = (_workingTheirs, _workingStaysAt, true);
        _plannedPeriod = mine.Closed ? mine.Period : double.PositiveInfinity;
        for (var i = 0; i < _window.Count; i++)
        {
            var joined = i > 0 && _window[i].Anomaly - _window[i - 1].Anomaly < Encounters.WindowGap; // a gap: two stretches, not one
            _windowAt.Add((mine.At(_window[i].Anomaly), mine.TimeTo(_window[i].Anomaly), 1f - _window[i].Miss / _workingWithin, joined));
        }
    }

    // The window's points on the screen this frame (Window), whether each is in view, the way
    // across its line there (Across) and its way out's vote on the screen (OuterSide).
    private readonly List<(Vector2 At, bool Shown)> _windowScreen = [];
    private readonly List<Vector2> _windowAcross = [], _windowOutward = [];

    /// <summary>
    /// The window, shaded beside the path on its outer side (away from the body it goes round), so it
    /// highlights that stretch of the orbit without covering the line: brighter and wider where a
    /// burn would come closer. One on the next lap, shaded on this lap's ellipse among the stars, is
    /// fainter and narrower, and labelled, so it is never taken for this lap's. The outer side is
    /// one side of the line for the whole window (<see cref="OuterSide"/>, by each point's way out
    /// from the body's centre in the world, seen on the screen), so it never zigzags across the
    /// line where the orbit is seen near edge-on, nor needs the body's centre in front of the camera.
    /// </summary>
    private void Window(ImDrawListPtr draw)
    {
        _windowScreen.Clear();
        if (_windowLine.Count < 2)
            return; // no window (or no stretch of one) to shade
        foreach (var point in _windowLine)
        {
            var shown = InView(point.At, out var at);
            _windowScreen.Add((new Vector2(at.X, at.Y), shown));
        }
        var body = _plannedOrbit.Body; // worked out with the window, round the body it is on
        var centre = _frames is { } frames ? frames.InView(body, body.Centre) : body.Centre;
        _windowAcross.Clear();
        _windowOutward.Clear();
        for (var i = 0; i < _windowScreen.Count; i++)
        {
            var at = _windowScreen[i].At;
            var before = i > 0 && _windowLine[i].Joined && _windowScreen[i - 1].Shown ? _windowScreen[i - 1].At : at;
            var after = i + 1 < _windowScreen.Count && _windowLine[i + 1].Joined && _windowScreen[i + 1].Shown ? _windowScreen[i + 1].At : at;
            _windowAcross.Add(Across(before, after));
            // A step out from the body's centre through the point, in the world, as the screen has it.
            var point = _windowLine[i].At;
            var outward = Vector2.Zero;
            if (_windowScreen[i].Shown && Project(point + (point - centre) * 0.01f, out var o))
                outward = new Vector2(o.X, o.Y) - at;
            _windowOutward.Add(outward);
        }
        var side = OuterSide(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_windowAcross),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_windowOutward));
        var best = -1f;
        var bestAt = default(float2);
        for (var i = 1; i < _windowLine.Count; i++)
        {
            var (from, to) = (_windowLine[i - 1], _windowLine[i]);
            if (!to.Joined || !_windowScreen[i - 1].Shown || !_windowScreen[i].Shown)
                continue;
            var quality = (from.Quality + to.Quality) / 2;
            var (width, alpha) = to.NextLap
                ? (3f + 3f * quality, (0.3f + 0.6f * quality) * Scene.NextLapWindowAlpha)
                : (7f + 5f * quality, 0.3f + 0.6f * quality);
            // Beside the line, its inner edge just clear of the line's own.
            var aside = Scene.PathWidth / 2f + width / 2f;
            var a = _windowScreen[i - 1].At + _windowAcross[i - 1] * (side * aside);
            var b = _windowScreen[i].At + _windowAcross[i] * (side * aside);
            draw.AddLine(new float2(a.X, a.Y), new float2(b.X, b.Y), Color(WindowColor, alpha), width);
            if (to.NextLap && quality > best)
                (best, bestAt) = (quality, new float2(b.X, b.Y));
        }
        if (best >= 0f)
            Label(bestAt + new float2(9f, 4f), bestAt, WindowColor, "Next lap");
    }

    /// <summary>
    /// The unit way across a line on the screen running from <paramref name="before"/> to
    /// <paramref name="after"/>, a quarter turn from its run (x into y: on the screen, whose y is
    /// down, to its right). None where the line has no length there.
    /// </summary>
    internal static Vector2 Across(Vector2 before, Vector2 after)
    {
        var along = after - before;
        return along.LengthSquared() < 1e-6f ? Vector2.Zero : Vector2.Normalize(new Vector2(-along.Y, along.X));
    }

    /// <summary>
    /// Which side of a line is its outer one, for the whole of it: 1 the side its
    /// <paramref name="across"/> ways point to, -1 the other, by the votes of its points' ways out on
    /// the screen (<paramref name="outward"/>, each from the body's centre through the point in the
    /// world, as seen: so one seen end-on, near edge-on, weighs little), summed along the across
    /// ways; 0 where none says (drawn over the line). An orbit's outer side is the same side of it
    /// as it runs all the way round, so one side holds for the whole window.
    /// </summary>
    internal static float OuterSide(ReadOnlySpan<Vector2> across, ReadOnlySpan<Vector2> outward)
    {
        var votes = 0f;
        for (var i = 0; i < across.Length; i++)
            votes += Vector2.Dot(across[i], outward[i]);
        return votes > 0f ? 1f : votes < 0f ? -1f : 0f;
    }

    /// <summary>
    /// Where a point of the window, worked out at world time <paramref name="plannedAt"/> and
    /// <paramref name="secondsFromPlan"/> on from then, is drawn at <paramref name="now"/> in
    /// <paramref name="frame"/>. Among the stars the orbit stays put, but the ground's frame the
    /// line is drawn in each frame has turned since (by hundreds of metres between plans in a fast
    /// warp): so the point is turned back by that. Over the ground it is the place under it then,
    /// which stays put however long ago it was planned. It isn't found again on the orbit as it is
    /// now, as a nearly round orbit's anomalies are measured from a periapsis that wanders. In
    /// <paramref name="body"/>'s frame (the body the orbit is round; the planet's by default), turned by its turn.
    /// </summary>
    internal static Vector3 WindowPoint(Frame frame, Vector3 at, double secondsFromPlan, double plannedAt, double now, GroundBody? body = null) =>
        (body ?? GroundBody.Planet).Turned(at, frame == Frame.Surface ? -secondsFromPlan : -(now - plannedAt));

    /// <summary>
    /// Where the last worked-out window is drawn at <paramref name="now"/> in <paramref name="frame"/>:
    /// only what is still ahead. Under a fast warp the vessel goes a long way between plans, and a
    /// point it has passed would be drawn over the ground behind it, off the line (which starts now).
    /// A stretch the vessel is part way through starts now, where the vessel is, between the
    /// points either side (not at the next point on, up to a point's gap ahead). Among the stars,
    /// each point a lap or more ahead now is marked as the next lap's: a window across the lap's
    /// end is marked from there on, and one the vessel has come round to (under a fast warp,
    /// between plans) is this lap's again. Over the ground the line goes on round to it, so it
    /// needs no mark.
    /// </summary>
    internal void LayWindow(Frame frame, double now)
    {
        _windowLine.Clear();
        var since = now - _plannedWorldTime;
        for (var i = 0; i < _windowAt.Count; i++)
        {
            var (at, seconds, quality, joined) = _windowAt[i];
            if (Passed(seconds, _plannedWorldTime, now))
                continue;
            if (joined && i > 0 && Passed(_windowAt[i - 1].Seconds, _plannedWorldTime, now))
            {
                // Part way through it: from now, where the orbit planned on has it now (the
                // points are seconds apart, so a straight line between them would cut inside the
                // orbit by metres), shaded as between the two.
                var (_, beforeSeconds, beforeQuality, _) = _windowAt[i - 1];
                var t = (float)((since - beforeSeconds) / (seconds - beforeSeconds));
                _windowLine.Add((WindowPoint(frame, _plannedOrbit.StateAfter(since).Position, since, _plannedWorldTime, now, _plannedOrbit.Body),
                    float.Lerp(beforeQuality, quality, t), false, false));
            }
            var nextLap = frame == Frame.Orbital && seconds - since >= _plannedPeriod;
            _windowLine.Add((WindowPoint(frame, at, seconds, _plannedWorldTime, now, _plannedOrbit.Body), quality, joined, nextLap));
        }
    }

    /// <summary>The window as last worked out (each point with its seconds on from then): read-only inspection for the tests.</summary>
    internal IReadOnlyList<(Vector3 At, double Seconds, float Quality, bool Joined)> PlannedWindow => _windowAt;

    /// <summary>The window as drawn now (<see cref="LayWindow"/>): read-only inspection for the tests.</summary>
    internal IReadOnlyList<(Vector3 At, float Quality, bool Joined, bool NextLap)> WindowLine => _windowLine;

    /// <summary>The last lap ahead the window as last worked out reaches (-1 with none): read-only inspection for the tests.</summary>
    internal int WindowLap => _windowLap;

    /// <summary>Whether a window is being worked out (<see cref="Work"/>): read-only inspection for the tests.</summary>
    internal bool Working => _working is not null;

    /// <summary>The world time the window being worked out was started at (null with none): read-only inspection for the tests.</summary>
    internal double? WorkingSince => _working is not null ? _workingWorldTime : null;

    /// <summary>Whether a point of the window, <paramref name="secondsFromPlan"/> on from world time <paramref name="plannedAt"/>, is behind the vessel by <paramref name="now"/>.</summary>
    internal static bool Passed(double secondsFromPlan, double plannedAt, double now) => secondsFromPlan < now - plannedAt;

    private void DrawLine(ImDrawListPtr draw, List<Vector3> points, (byte R, byte G, byte B) color, float alpha, float width) =>
        DrawLine(draw, points, 0, points.Count, color, alpha, width);

    /// <summary>The <paramref name="count"/> points of <paramref name="points"/> from <paramref name="first"/> as a line, but where a body hides them, with its chevrons.</summary>
    private void DrawLine(ImDrawListPtr draw, List<Vector3> points, int first, int count, (byte R, byte G, byte B) color, float alpha, float width)
    {
        var line = Color(color, alpha);
        var hasLast = false;
        var last = default(float2);
        _stretch.Clear();
        for (var i = first; i < first + count; i++)
        {
            var shown = InView(points[i], out var at);
            if (shown && hasLast)
                draw.AddLine(last, at, line, width);
            if (shown)
                _stretch.Add(new Vector2(at.X, at.Y));
            else
                DrawChevrons(draw, _stretch, _chevrons, Color(color, alpha * Scene.ChevronAlpha));
            (hasLast, last) = (shown, at);
        }
        DrawChevrons(draw, _stretch, _chevrons, Color(color, alpha * Scene.ChevronAlpha));
    }

    private readonly List<Vector2> _stretch = [];
    private readonly (Vector2 At, Vector2 Way)[] _chevrons = new (Vector2, Vector2)[MaxChevrons];

    /// <summary>The most chevrons a line is given (<see cref="Chevrons"/>): a line many screens long gets no more.</summary>
    internal const int MaxChevrons = 64;

    /// <summary>
    /// Places chevrons along <paramref name="line"/> (a polyline on the screen, in the order it is
    /// flown), one every <paramref name="every"/> pixels with the first half that in, each pointing
    /// along the line there, into <paramref name="into"/>: how many were placed.
    /// </summary>
    internal static int Chevrons(ReadOnlySpan<Vector2> line, float every, Span<(Vector2 At, Vector2 Way)> into)
    {
        var placed = 0;
        var next = every / 2f; // the length along the line the next one is at
        var walked = 0f;
        for (var i = 1; i < line.Length && placed < into.Length; i++)
        {
            var step = line[i] - line[i - 1];
            var length = step.Length();
            if (length < 1e-4f)
                continue;
            var way = step / length;
            while (next <= walked + length && placed < into.Length)
            {
                into[placed++] = (line[i - 1] + way * (next - walked), way);
                next += every;
            }
            walked += length;
        }
        return placed;
    }

    /// <summary>The chevrons of <paramref name="stretch"/> (<see cref="Chevrons"/>), each two short lines back from its tip; the stretch is emptied.</summary>
    internal static void DrawChevrons(ImDrawListPtr draw, List<Vector2> stretch, (Vector2 At, Vector2 Way)[] into,
        ImColor8 c)
    {
        var n = stretch.Count >= 2 ? Chevrons(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stretch), Scene.ChevronEveryPixels, into) : 0;
        stretch.Clear();
        if (n == 0)
            return;
        var s = Scene.ChevronPixels;
        for (var k = 0; k < n; k++)
        {
            var (at, way) = into[k];
            var side = new Vector2(-way.Y, way.X);
            var tip = at + way * (s / 2f);
            var a = tip - way * s + side * s;
            var b = tip - way * s - side * s;
            draw.AddLine(new float2(tip.X, tip.Y), new float2(a.X, a.Y), c, Scene.ChevronWidth);
            draw.AddLine(new float2(tip.X, tip.Y), new float2(b.X, b.Y), c, Scene.ChevronWidth);
        }
    }

    /// <summary>The height an Ap or Pe mark at <paramref name="at"/> on <paramref name="orbit"/> gives: above the sea, as the HUD's apoapsis and periapsis are.</summary>
    internal static float MarkHeight(Orbit orbit, double at) => (float)orbit.Distance(at) - orbit.Body.SeaRadius;

    private void DrawMark(ImDrawListPtr draw, Vector3 point, (byte R, byte G, byte B) color, string label)
    {
        if (!InView(point, out var at))
            return;
        draw.AddCircleFilled(at, 3.5f, Color(color, 1f));
        Label(at + new float2(7f, -8f), at, color, label);
    }

    /// <summary>A cross at <paramref name="point"/>, labelled; whether it was in view to draw.</summary>
    private bool Cross(ImDrawListPtr draw, Vector3 point, (byte R, byte G, byte B) color, string label, float alpha = 1f)
    {
        if (!InView(point, out var at))
            return false;
        var c = Color(color, alpha);
        draw.AddLine(at + new float2(-5f, -5f), at + new float2(5f, 5f), c, 2.5f);
        draw.AddLine(at + new float2(-5f, 5f), at + new float2(5f, -5f), c, 2.5f);
        Label(at + new float2(9f, -8f), at, color, label); // the label at full strength: its words say if it is the next lap's
        return true;
    }

    /// <summary>A label at <paramref name="at"/> for the mark at <paramref name="anchor"/>, drawn with the others over the view (<see cref="Labels"/>).</summary>
    private void Label(float2 at, float2 anchor, (byte R, byte G, byte B) color, string text) =>
        Labels.Add(text, at, anchor, color, _fade, 0.8f * _fade);

    /// <summary>The labels over the view this frame: the path's, the map's and the markers', laid out and drawn together by the HUD once all are in.</summary>
    internal MapLabels Labels { get; } = new();

    private ImColor8 Color((byte R, byte G, byte B) color, float alpha) =>
        new(color.R, color.G, color.B, (byte)(255 * Math.Clamp(alpha * _fade, 0f, 1f)));

    /// <summary>Where <paramref name="point"/> (in the main view's frame) is on the screen, if it is in front of the camera, whatever is in the way.</summary>
    private bool Project(Vector3 point, out float2 at)
    {
        at = default;
        var clip = Vector4.Transform(new Vector4(point, 1f), _view);
        if (clip.W <= 1e-4f)
            return false;
        at = new float2((clip.X / clip.W + 1f) * _display.X * 0.5f, (1f - clip.Y / clip.W) * _display.Y * 0.5f);
        return true;
    }

    /// <summary>Where <paramref name="point"/> (in the main view's frame) is on the screen, if it is in front of the camera and not behind a body (as the camera was for this frame's <see cref="Draw"/>).</summary>
    public bool InView(Vector3 point, out float2 at)
    {
        at = default;
        if (_frames is { } frames ? frames.Behind(_camera, point) : BehindPlanet(_camera, point))
            return false;
        return Project(point, out at);
    }

    /// <summary>Whether the planet is in the way between <paramref name="camera"/> and <paramref name="point"/>.</summary>
    public static bool BehindPlanet(Vector3 camera, Vector3 point)
    {
        // Where the line from the camera to the point first meets the sea's sphere, just under the
        // waves' troughs: nothing on the sea or the land (which is above it) is hidden by it, and a
        // path that ends on the ground isn't hidden by the ground it ends on.
        var d = point - camera;
        var f = camera - Planet.Centre;
        var radius = Planet.SeaRadius - Sea.Highest - 5f;
        float a = d.LengthSquared(), b = 2f * Vector3.Dot(f, d), c = f.LengthSquared() - radius * radius;
        var discriminant = b * b - 4f * a * c;
        if (discriminant <= 0f)
            return false;
        var first = (-b - MathF.Sqrt(discriminant)) / (2f * a);
        return first > 0f && first < 1f;
    }

    private static float SmoothStep(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
