using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace GameOne;

/// <summary>
/// Planning burns on the zoomed-out path: the flown vessel's manoeuvre nodes, drawn where they are
/// on it, and what the mouse does to them. A click on the path places one there (the pointer over
/// it shows where); drag a node along the path it is on to move it in time (the nodes after it
/// move with it); right-click it (or click its small x) to delete it, joining the chain round it.
/// The selected node (the last placed or clicked) has six handles, for speed along the way it is
/// going or against it, out from the planet or in, and to either side of its orbit's plane: pull
/// one out and hold it, and that speed builds the farther out it is held, stopping when it is let go.
/// The path after the burn follows as it changes (see <see cref="Paths"/>).
/// </summary>
/// <remarks>
/// What is planned goes to the flight as its input (<see cref="TakePlan"/>), as everything the
/// pilot does to a vessel does. While a node is being dragged the plan here runs ahead of the
/// vessel's by a frame, and is what is drawn.
/// </remarks>
internal sealed class NodeEditor
{
    private enum Grab
    {
        None,
        Node,
        Handle,
    }

    private const float NodePixels = 6f, GrabPixels = 9f;
    /// <summary>How near the pointer must be to a path, in pixels, to be on it.</summary>
    internal const float PathPixels = 8f;
    /// <summary>How far the pointer must move from where a node was grabbed, in pixels, before it is dragged: a click to select it (to use its handles) leaves it where it is.</summary>
    internal const float DragPixels = 4f;
    internal const float HandleRest = 44f;     // how far out the handles sit, in pixels
    internal const float HandlePull = 70f;     // and how much farther one can be pulled
    internal const float FastestRate = 40f;    // m/s added each second, pulled all the way out
    private const float HandleInnermost = 14f; // the nearest a handle is drawn to its node, in pixels, however far in it is pushed (clear of the node and its label)
    private const float HandleDeadZone = 3f;   // how far either side of its rest the pointer must be before a handle does anything

    private static readonly (byte R, byte G, byte B) ProgradeColor = (200, 255, 90), RadialColor = (90, 220, 255), NormalColor = (240, 120, 255);
    private static readonly string[] HandleNames = ["Prograde", "Retrograde", "Radial out", "Radial in", "Normal", "Antinormal"];

    private ManoeuvreChain? _pending;
    private readonly List<(Vector3 At, double Seconds)> _samples = [];
    private Vessel? _vessel;
    private int _selected = -1;
    private Grab _grab;
    private int _handle;
    private bool _changed;
    private long _last = System.Diagnostics.Stopwatch.GetTimestamp();
    private Vector2 _cursor;
    private Vector2 _grabbedAt; // where the pointer was as a node was grabbed
    private bool _dragging;     // and whether it has since moved far enough to drag it
    private bool _pressed, _rightPressed, _held;

    /// <summary>
    /// Follows the mouse from the window's own events, as the steering rings do (see
    /// <see cref="NavigationControl"/>), not ImGui's idea of the pointer: that can fall back to
    /// reading the real cursor, which a test driver's posted messages don't move.
    /// </summary>
    public NodeEditor(GlfwWindow window)
    {
        window.OnMouseButton += (_, button, action, _) =>
        {
            if (button == GlfwMouseButton.Left)
            {
                if (action == GlfwButtonAction.Press)
                    (_pressed, _held) = (true, true);
                else if (action == GlfwButtonAction.Release)
                    _held = false;
            }
            else if (button == GlfwMouseButton.Right && action == GlfwButtonAction.Press)
                _rightPressed = true;
        };
        // Kept when the pointer leaves the window, as the rings' is (Windows reports a leave after
        // every move a test driver posts, with the real cursor off the window).
        window.OnCursorPos += (_, pos) => _cursor = new Vector2((float)pos.X, (float)pos.Y);
    }

    /// <summary>The nodes as planned here: the vessel's chain, or as it is being changed (null, or with no nodes, for none).</summary>
    public ManoeuvreChain? Chain => _pending;

    private int Count => _pending?.Nodes.Length ?? 0;

    /// <summary>Whether a node's handles are drawn (see <see cref="ShowsHandles"/>): a click on the steering rings' place is then for them, not the rings.</summary>
    public bool Selected { get; private set; }

    /// <summary>Whether a node's handles are drawn: one is selected, the editor is showing and that node is on the screen.</summary>
    internal static bool ShowsHandles(bool editing, int selected, ReadOnlySpan<bool> shown) =>
        editing && selected >= 0 && selected < shown.Length && shown[selected];

    /// <summary>
    /// How fast a handle adds to its speed (m/s each second, negative to take away) with the
    /// pointer <paramref name="along"/> pixels past where the handle rests, on its own axis. Out,
    /// faster the farther: fine near the rest, quick at the end. In, a share of that
    /// (<see cref="Scene.NodeHandleInwardShare"/>) the other way, so it undoes an overshoot in
    /// small steps; it goes past zero (a larger change is the opposite handle's).
    /// </summary>
    internal static float Rate(float along)
    {
        var pull = Math.Clamp(Math.Abs(along), 0f, HandlePull);
        var outward = 0.3f + FastestRate * (pull / HandlePull) * (pull / HandlePull);
        return along >= 0f ? outward : -Scene.NodeHandleInwardShare * outward;
    }

    private readonly List<(int Index, float2 At)> _drawn = [];

    /// <summary>The nodes drawn this frame, each by its place in the chain and where it is on the screen (window pixels): for the readout.</summary>
    public IReadOnlyList<(int Index, float2 At)> Drawn => _drawn;

    /// <summary>Whether the pointer is on a node, a handle or the path, or dragging: a click then is for planning, not the steering rings or the markers.</summary>
    public bool PointerOver { get; private set; }

    /// <summary>The plan, if it changed since the last call: the flown vessel's nodes from now on, for the flight's input.</summary>
    public ManoeuvreChain? TakePlan()
    {
        if (!_changed)
            return null;
        _changed = false;
        return _sent = _pending ?? ManoeuvreChain.Empty;
    }

    private ManoeuvreChain? _known, _sent; // the vessel's chain as last followed, and the last plan sent for it

    /// <summary>
    /// Whether the vessel's chain (<paramref name="now"/>) has been changed by something other than
    /// the editor since it was last followed (<paramref name="known"/>): not to it, nor to the plan
    /// the editor last sent (<paramref name="sent"/>, none if it has sent none). A node goes by
    /// itself as it is matched at its cut, or its fuel runs out.
    /// </summary>
    internal static bool ChangedElsewhere(ManoeuvreChain? now, ManoeuvreChain? known, ManoeuvreChain? sent) =>
        !Same(now, known) && (sent is null || !Same(now, sent));

    private static bool Same(ManoeuvreChain? a, ManoeuvreChain? b) =>
        a is not { Nodes.IsEmpty: false } ? b is not { Nodes.IsEmpty: false } : Equals(a, b);

    /// <summary>
    /// Before the plan is worked out each frame: the flown vessel's nodes, unless they are being
    /// changed here; a grab is let go if the vessel's chain changed meanwhile from elsewhere, so
    /// the edit held, made on the chain as it was, isn't sent back over it.
    /// </summary>
    public void Follow(Vessel vessel)
    {
        if (vessel != _vessel)
            (_vessel, _selected, _grab, _changed, _known, _sent) = (vessel, -1, Grab.None, false, vessel.Chain, null);
        if (ChangedElsewhere(vessel.Chain, _known, _sent))
            (_grab, _dragging, _changed) = (Grab.None, false, false);
        _known = vessel.Chain;
        if (_grab == Grab.None && !_changed && !Equals(_pending, vessel.Chain))
            _pending = vessel.Chain;
        if (_selected >= Count)
            _selected = -1;
    }

    /// <summary>A frame with the HUD hidden: nothing drawn, nothing under the pointer or held, and a press let go.</summary>
    public void Hide()
    {
        (_pressed, _rightPressed) = (false, false);
        PointerOver = false;
        _drawn.Clear();
        Selected = false;
        _grab = Grab.None;
    }

    /// <summary>
    /// Takes the mouse and draws the nodes, after the path (<paramref name="paths"/>, whose camera
    /// and fade it shares). Nothing is clickable while <paramref name="blocked"/> (a menu up, or the
    /// pointer on a marker); nothing shows until the path does.
    /// </summary>
    public void Update(ImDrawListPtr draw, Flight flight, ChaseCamera camera, ManoeuvrePlan plan, Paths paths, bool blocked)
    {
        var dt = (float)Math.Min(System.Diagnostics.Stopwatch.GetElapsedTime(_last).TotalSeconds, 0.1);
        _last = System.Diagnostics.Stopwatch.GetTimestamp();
        var (pressed, rightPressed) = (_pressed, _rightPressed);
        (_pressed, _rightPressed) = (false, false);
        PointerOver = false;
        _drawn.Clear();
        Selected = false;
        var now = flight.WorldTime;
        var frame = flight.LineFrame;
        if (paths.Fade < 0.5f || flight.Controlled.State != FlightState.Flying)
        {
            _grab = Grab.None;
            return;
        }
        var io = ImGui.GetIO();
        var pointer = new float2(_cursor.X, _cursor.Y);
        var interact = !blocked && !io.WantCaptureMouse;
        if (!_held)
            _grab = Grab.None;
        var clicked = interact && pressed;
        var rightClicked = interact && rightPressed;
        var alpha = Math.Clamp((paths.Fade - 0.5f) * 2f, 0f, 1f);

        // Dragging: the node along the path it is on (the first, the vessel's own as it is now, so
        // it snaps there; a later one, the path the one before it plans), the nodes after it
        // moving with it, once the pointer has moved off where it grabbed it (a click only selects
        // it, leaving it where it was set); or a handle out from it.
        if (_grab == Grab.Node)
            _dragging |= Dragged(_grabbedAt, V2(pointer));
        if (_grab == Grab.Node && _dragging && _pending is { } chain && _selected >= 0 && _selected < chain.Nodes.Length)
        {
            double? time = null;
            var best = float.MaxValue;
            if (_selected == 0)
                for (var i = 0; i < paths.Present.Count; i++)
                {
                    var leg = paths.Present[i];
                    SampleLeg(leg, leg.End - leg.Start, frame, flight.Body, now, last: i == paths.Present.Count - 1);
                    if (Nearest(paths, pointer, best, out var distance) is { } nearest)
                        (time, best) = (leg.Start + nearest.Seconds, distance);
                }
            else if (plan.NodeLeg(_selected) is { } nodeLeg)
            {
                // On along that path past the node too, as far as its orbit goes: the later nodes move with it.
                var leg = plan.Legs[nodeLeg];
                SampleLeg(leg, double.PositiveInfinity, frame, flight.Body, now, last: false);
                if (Nearest(paths, pointer, best, out _) is { } nearest)
                    time = leg.Start + nearest.Seconds;
            }
            if (time is { } to)
            {
                _pending = chain.MoveTo(_selected, to, flight.Controlled, now);
                _changed = true;
            }
        }

        var selectedAtStart = _selected; // its handles, worked out below, are drawn only if it is still the one

        // Where each node is on the screen, if the plan reaches it.
        var count = Count;
        Span<float2> onScreen = stackalloc float2[count];
        Span<bool> shown = stackalloc bool[count];
        for (var i = 0; i < count; i++)
            shown[i] = i < paths.Nodes.Count && paths.InView(paths.Nodes[i], out onScreen[i]);

        // The selected node's handles: each a way to add speed, pointing that way on the screen.
        Span<float2> handleAt = stackalloc float2[6];
        Span<float2> handleWay = stackalloc float2[6];
        Span<bool> handleShown = stackalloc bool[6];
        var hovered = Hover.None;
        var hoveredIndex = -1;
        if (_selected >= 0 && shown[_selected] && plan.NodeAt(_selected) is { } selected && plan.NodeLeg(_selected) is { } selectedLeg)
        {
            var (prograde, radial, normal) = Manoeuvres.Frame(plan.Legs[selectedLeg].Body, selected.Position, selected.Velocity);
            ReadOnlySpan<Vector3> ways = [prograde, -prograde, radial, -radial, normal, -normal];
            // Pointing the burn's ways (among the stars) from the node as drawn, turned and placed as its leg is.
            var seconds = plan.Legs[selectedLeg].End - now;
            var place = new LegPlace(plan.Legs[selectedLeg], flight.Body, now, last: false); // (a node's leg has the burn's after it)
            var scale = Vector3.Distance(camera.Position, paths.Nodes[_selected]) * 0.05f;
            for (var k = 0; k < 6; k++)
            {
                handleShown[k] = paths.InView(place.Point(frame, selected.Position + ways[k] * scale, seconds), out var tip)
                    && Vector2.Distance(V2(tip), V2(onScreen[_selected])) > 2f;
                if (!handleShown[k])
                    continue;
                handleWay[k] = F2(Vector2.Normalize(V2(tip) - V2(onScreen[_selected])));
                var along = _grab == Grab.Handle && _handle == k
                    ? Math.Clamp(Vector2.Dot(V2(pointer) - V2(onScreen[_selected]), V2(handleWay[k])) - HandleRest, -HandleRest, HandlePull)
                    : 0f;
                handleAt[k] = onScreen[_selected] + handleWay[k] * (HandleRest + Math.Max(along, HandleInnermost - HandleRest));
                if (Math.Abs(along) > HandleDeadZone)
                {
                    var add = Rate(along) * dt * (k % 2 == 0 ? 1f : -1f);
                    var node = _pending!.Nodes[_selected];
                    _pending = _pending.WithNode(_selected, (k / 2) switch
                    {
                        0 => node with { Prograde = node.Prograde + add },
                        1 => node with { Radial = node.Radial + add },
                        _ => node with { Normal = node.Normal + add },
                    });
                    _changed = true;
                }
                if (_grab == Grab.None && Distance(pointer, handleAt[k]) < GrabPixels)
                    (hovered, hoveredIndex) = (Hover.Handle, k);
            }
            if (_grab == Grab.None && hovered == Hover.None && Distance(pointer, DeleteAt(onScreen[_selected])) < GrabPixels)
                hovered = Hover.Delete;
        }
        if (_grab == Grab.None && hovered == Hover.None)
            for (var i = 0; i < count; i++)
                if (shown[i] && Distance(pointer, onScreen[i]) < GrabPixels)
                    (hovered, hoveredIndex) = (Hover.Node, i);

        // Over the path, away from the nodes: where a click would place one. On the vessel's own path
        // up to the first node (all of it, with none), then on the plan after the first node's burn.
        (Vector3 At, double Time)? ghost = null;
        if (_grab == Grab.None && hovered == Hover.None && interact)
        {
            var firstAt = Count > 0 ? _pending!.TimeOf(0) : double.PositiveInfinity;
            var present = paths.Present;
            for (var i = 0; i < present.Count && ghost is null && present[i].Start < firstAt; i++)
            {
                var leg = present[i];
                SampleLeg(leg, Math.Min(leg.End, firstAt) - leg.Start, frame, flight.Body, now, last: i == present.Count - 1);
                if (Nearest(paths, pointer, PathPixels) is { } point && leg.Start + point.Seconds > now + ManoeuvreChain.MinimumGap)
                    ghost = (point.At, leg.Start + point.Seconds);
            }
            for (var i = plan.NodeLeg(0) is { } firstLeg ? firstLeg + 1 : plan.Legs.Count; i < plan.Legs.Count && ghost is null; i++)
            {
                var leg = plan.Legs[i];
                SampleLeg(leg, leg.End - leg.Start, frame, flight.Body, now, last: i == plan.Legs.Count - 1);
                if (Nearest(paths, pointer, PathPixels) is { } point && leg.Start + point.Seconds > now + ManoeuvreChain.MinimumGap)
                    ghost = (point.At, leg.Start + point.Seconds);
            }
        }
        PointerOver = interact && (_grab != Grab.None || hovered != Hover.None || ghost is not null);

        if (rightClicked && hovered == Hover.Node)
            Delete(hoveredIndex, flight.Controlled, now);
        else if (clicked)
        {
            switch (hovered)
            {
                case Hover.Delete:
                    Delete(_selected, flight.Controlled, now);
                    break;
                case Hover.Handle:
                    (_grab, _handle) = (Grab.Handle, hoveredIndex);
                    break;
                case Hover.Node:
                    (_selected, _grab, _grabbedAt, _dragging) = (hoveredIndex, Grab.Node, V2(pointer), false);
                    break;
                default:
                    if (ghost is { } place)
                    {
                        var into = _pending ?? ManoeuvreChain.Empty;
                        _selected = into.SlotFor(place.Time);
                        _pending = into.Insert(place.Time, flight.Controlled, now);
                        _changed = true;
                    }
                    else
                        _selected = -1;
                    break;
            }
        }

        Selected = ShowsHandles(true, _selected == selectedAtStart ? _selected : -1, shown);

        // Drawing: the nodes, the selected one's handles and delete button, and the ghost.
        var matched = flight.Guide is { Matched: true };
        var color = Paths.PlanColor;
        if (ghost is { } g && paths.InView(g.At, out var ghostAt))
            draw.AddCircle(ghostAt, 5f, Color(color, alpha * 0.9f), 0, 1.5f);
        // (Only those there were at the start of the frame: one placed or deleted just now shows from the next.)
        for (var i = 0; i < shown.Length && i < Count; i++)
        {
            if (!shown[i])
                continue;
            var at = onScreen[i];
            _drawn.Add((i, at));
            var hot =i == _selected || (hovered == Hover.Node && hoveredIndex == i);
            var nodeColor = Paths.NodeColorOf(matched, i);
            draw.AddCircleFilled(at, hot ? NodePixels + 1.5f : NodePixels, Color(nodeColor, alpha));
            draw.AddCircle(at, (hot ? NodePixels + 1.5f : NodePixels) + 1f, Color((0, 0, 0), alpha * 0.6f), 0, 1.5f);
            var node = _pending!.Nodes[i];
            Label(draw, at + new float2(10f, 8f), nodeColor, alpha, $"{node.Size:0.0} m/s  in {Clock(_pending.TimeOf(i) - now)}");
        }
        if (_selected >= 0 && _selected == selectedAtStart && _selected < shown.Length && _selected < Count && shown[_selected])
        {
            var at = onScreen[_selected];
            draw.AddCircle(at, NodePixels + 5f, Color(Paths.NodeColorOf(matched, _selected), alpha), 0, 1.5f);
            for (var k = 0; k < 6; k++)
            {
                if (!handleShown[k])
                    continue;
                var handleColor = (k / 2) switch { 0 => ProgradeColor, 1 => RadialColor, _ => NormalColor };
                var hot = (_grab == Grab.Handle && _handle == k) || (hovered == Hover.Handle && hoveredIndex == k);
                draw.AddLine(at + handleWay[k] * (NodePixels + 4f), handleAt[k], Color(handleColor, alpha * 0.5f), 1.5f);
                if (k % 2 == 0)
                    draw.AddCircleFilled(handleAt[k], hot ? 7f : 5.5f, Color(handleColor, alpha));
                else
                    draw.AddCircle(handleAt[k], hot ? 7f : 5.5f, Color(handleColor, alpha), 0, 2f);
                if (hot)
                {
                    var node = _pending!.Nodes[_selected];
                    var amount = (k / 2) switch { 0 => node.Prograde, 1 => node.Radial, _ => node.Normal };
                    Label(draw, handleAt[k] + new float2(10f, -8f), handleColor, alpha, $"{HandleNames[k & ~1]} {amount:0.0} m/s");
                }
            }
            var x = DeleteAt(at);
            var xColor = Color(hovered == Hover.Delete ? ((byte)255, (byte)120, (byte)90) : ((byte)230, (byte)230, (byte)235), alpha);
            draw.AddLine(x + new float2(-3.5f, -3.5f), x + new float2(3.5f, 3.5f), xColor, 2f);
            draw.AddLine(x + new float2(-3.5f, 3.5f), x + new float2(3.5f, -3.5f), xColor, 2f);
        }
    }

    private enum Hover
    {
        None,
        Node,
        Handle,
        Delete,
    }

    private void Delete(int index, Vessel vessel, double now)
    {
        if (_pending is not { } chain || index < 0 || index >= chain.Nodes.Length)
            return;
        _pending = chain.Delete(index, vessel, now);
        _selected = -1;
        _grab = Grab.None;
        _changed = true;
    }

    /// <summary>
    /// Fills <see cref="_samples"/> with <paramref name="leg"/>'s path, up to <paramref name="to"/>
    /// seconds along it, where <see cref="Paths"/> draws it in <paramref name="frame"/>, each with
    /// its seconds along the leg: so a click on the line, over the ground or among the stars, maps
    /// back to the moment on the orbit it was drawn for. A leg round another body than
    /// <paramref name="view"/>'s (the flown vessel's) is placed as the path draws it (<see cref="LegPlace"/>).
    /// </summary>
    private void SampleLeg(in PathLeg leg, double to, Frame frame, GroundBody view, double now, bool last)
    {
        var place = new LegPlace(leg, view, now, last);
        leg.Orbit.Samples(_samples, 0, to);
        for (var i = 0; i < _samples.Count; i++)
            _samples[i] = (place.Point(frame, _samples[i].At, leg.Start - now + _samples[i].Seconds), _samples[i].Seconds);
    }

    /// <summary>The sample (of <see cref="_samples"/>) nearest <paramref name="pointer"/> on the screen, if within <paramref name="within"/> pixels, and how far it is.</summary>
    private (Vector3 At, double Seconds)? Nearest(Paths paths, float2 pointer, float within) => Nearest(paths, pointer, within, out _);

    private (Vector3 At, double Seconds)? Nearest(Paths paths, float2 pointer, float within, out float bestDistance)
    {
        (Vector3 At, double Seconds)? best = null;
        bestDistance = within;
        var hasLast = false;
        var last = default(float2);
        var lastSample = default((Vector3 At, double Seconds));
        foreach (var sample in _samples)
        {
            var visible = paths.InView(sample.At, out var at);
            if (visible && hasLast)
            {
                // Along the piece from the last point: how far, and where (its time in between).
                var ab = V2(at) - V2(last);
                var t = ab.LengthSquared() > 1e-6f ? Math.Clamp(Vector2.Dot(V2(pointer) - V2(last), ab) / ab.LengthSquared(), 0f, 1f) : 0f;
                var distance = Vector2.Distance(V2(pointer), V2(last) + ab * t);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (Vector3.Lerp(lastSample.At, sample.At, t), lastSample.Seconds + (sample.Seconds - lastSample.Seconds) * t);
                }
            }
            (hasLast, last, lastSample) = (visible, at, sample);
        }
        return best;
    }

    /// <summary>A time as minutes and seconds (hours too, if it is that far off); before now, with a minus.</summary>
    public static string Clock(double seconds)
    {
        var sign = seconds < 0 ? "-" : "";
        var whole = (long)Math.Round(Math.Abs(seconds));
        return whole >= 3600
            ? $"{sign}{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}"
            : $"{sign}{whole / 60}:{whole % 60:00}";
    }

    private static float2 DeleteAt(float2 node) => node + new float2(14f, -14f);

    private static void Label(ImDrawListPtr draw, float2 at, (byte R, byte G, byte B) color, float alpha, string text)
    {
        var shadow = Color((0, 0, 0), alpha * 0.8f);
        foreach (var offset in (ReadOnlySpan<float2>)[new(-1, 0), new(1, 0), new(0, -1), new(0, 1)])
            draw.AddText(at + offset, shadow, text);
        draw.AddText(at, Color(color, alpha), text);
    }

    private static ImColor8 Color((byte R, byte G, byte B) color, float alpha) =>
        new(color.R, color.G, color.B, (byte)(255 * Math.Clamp(alpha, 0f, 1f)));

    /// <summary>Whether a node grabbed with the pointer at <paramref name="grabbedAt"/> is being dragged with it at <paramref name="pointer"/>: once it has moved more than <see cref="DragPixels"/> away.</summary>
    internal static bool Dragged(Vector2 grabbedAt, Vector2 pointer) => Vector2.Distance(grabbedAt, pointer) > DragPixels;

    private static float Distance(float2 a, float2 b) => Vector2.Distance(V2(a), V2(b));
    private static Vector2 V2(float2 v) => new(v.X, v.Y);
    private static float2 F2(Vector2 v) => new(v.X, v.Y);
}
