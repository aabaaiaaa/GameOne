using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace GameOne;

/// <summary>
/// Planning burns on the zoomed-out path: the flown vessel's manoeuvre nodes, drawn where they are
/// on it, and what the mouse does to them. A click on the path places one there (the pointer over
/// it shows where); drag a node along the path to move it in time; right-click it (or click its
/// small x) to delete it. The selected node (the last placed or clicked) has six handles, for
/// speed along the way it is going or against it, out from the planet or in, and to either side of
/// its orbit's plane: pull one out and hold it, and that speed builds the farther out it is held,
/// stopping when it is let go. The path after the burn follows as it changes (see <see cref="Paths"/>).
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

    private const float NodePixels = 6f, GrabPixels = 9f, PathPixels = 8f;
    private const float HandleRest = 44f;     // how far out the handles sit, in pixels
    private const float HandlePull = 70f;     // and how much farther one can be pulled
    private const float FastestRate = 40f;    // m/s added each second, pulled all the way out
    private const double MinimumGap = 1.0;    // a node can't be placed or moved closer than this to now, seconds

    private static readonly (byte R, byte G, byte B) ProgradeColor = (200, 255, 90), RadialColor = (90, 220, 255), NormalColor = (240, 120, 255);
    private static readonly string[] HandleNames = ["Prograde", "Retrograde", "Radial out", "Radial in", "Normal", "Antinormal"];

    private readonly List<ManoeuvreNode> _pending = [];
    private readonly List<(Vector3 At, double Seconds)> _samples = [];
    private Vessel? _vessel;
    private int _selected = -1;
    private Grab _grab;
    private int _handle;
    private bool _changed;
    private long _last = System.Diagnostics.Stopwatch.GetTimestamp();
    private Vector2 _cursor;
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

    /// <summary>The nodes as planned here: the vessel's, or as they are being changed.</summary>
    public IReadOnlyList<ManoeuvreNode> Nodes => _pending;

    /// <summary>Whether the planned first node is the vessel's own next node, unchanged (so its burn, if it has begun, is still the vessel's).</summary>
    public bool FirstUnchanged => _vessel is { } vessel && vessel.Nodes.Count > 0 && _pending.Count > 0 && vessel.Nodes[0] == _pending[0];

    /// <summary>Whether the pointer is on a node, a handle or the path, or dragging: a click then is for planning, not the steering rings or the markers.</summary>
    public bool PointerOver { get; private set; }

    /// <summary>The plan, if it changed since the last call: the flown vessel's nodes from now on, for the flight's input.</summary>
    public ManoeuvreNode[]? TakePlan()
    {
        if (!_changed)
            return null;
        _changed = false;
        return [.. _pending];
    }

    /// <summary>Before the plan is worked out each frame: the flown vessel's nodes, unless they are being changed here.</summary>
    public void Follow(Vessel vessel)
    {
        if (vessel != _vessel)
            (_vessel, _selected, _grab, _changed) = (vessel, -1, Grab.None, false);
        if (_grab == Grab.None && !_changed && !_pending.SequenceEqual(vessel.Nodes))
        {
            _pending.Clear();
            _pending.AddRange(vessel.Nodes);
        }
        if (_selected >= _pending.Count)
            _selected = -1;
    }

    /// <summary>A frame with the HUD hidden: nothing drawn, nothing under the pointer or held, and a press let go.</summary>
    public void Hide()
    {
        (_pressed, _rightPressed) = (false, false);
        PointerOver = false;
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

        // Dragging: the node along its leg of the path, or a handle out from it.
        if (_grab == Grab.Node && _selected >= 0 && plan.NodeLeg(_selected) is { } nodeLeg)
        {
            var leg = plan.Legs[nodeLeg];
            var until = _selected + 1 < _pending.Count ? _pending[_selected + 1].Time - MinimumGap : double.PositiveInfinity;
            SampleLeg(leg, until - leg.Start, frame, flight.Body, now, last: false);
            if (Nearest(paths, pointer, float.MaxValue) is { } nearest)
            {
                var time = Math.Max(leg.Start + nearest.Seconds, Math.Max(now + MinimumGap, _selected > 0 ? _pending[_selected - 1].Time + MinimumGap : 0));
                _pending[_selected] = _pending[_selected] with { Time = time };
                _changed = true;
            }
        }

        var selectedAtStart = _selected; // its handles, worked out below, are drawn only if it is still the one

        // Where each node is on the screen, if the plan reaches it.
        Span<float2> onScreen = stackalloc float2[_pending.Count];
        Span<bool> shown = stackalloc bool[_pending.Count];
        for (var i = 0; i < _pending.Count; i++)
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
                var pull = _grab == Grab.Handle && _handle == k
                    ? Math.Clamp(Vector2.Dot(V2(pointer) - V2(onScreen[_selected]), V2(handleWay[k])) - HandleRest, 0f, HandlePull)
                    : 0f;
                handleAt[k] = onScreen[_selected] + handleWay[k] * (HandleRest + pull);
                if (pull > 3f)
                {
                    // Faster the farther out it is held: fine near the rest, quick at the end.
                    var rate = 0.3f + FastestRate * (pull / HandlePull) * (pull / HandlePull);
                    var add = rate * dt * (k % 2 == 0 ? 1f : -1f);
                    var node = _pending[_selected];
                    _pending[_selected] = (k / 2) switch
                    {
                        0 => node with { Prograde = node.Prograde + add },
                        1 => node with { Radial = node.Radial + add },
                        _ => node with { Normal = node.Normal + add },
                    };
                    _changed = true;
                }
                if (_grab == Grab.None && Distance(pointer, handleAt[k]) < GrabPixels)
                    (hovered, hoveredIndex) = (Hover.Handle, k);
            }
            if (_grab == Grab.None && hovered == Hover.None && Distance(pointer, DeleteAt(onScreen[_selected])) < GrabPixels)
                hovered = Hover.Delete;
        }
        if (_grab == Grab.None && hovered == Hover.None)
            for (var i = 0; i < _pending.Count; i++)
                if (shown[i] && Distance(pointer, onScreen[i]) < GrabPixels)
                    (hovered, hoveredIndex) = (Hover.Node, i);

        // Over the path, away from the nodes: where a click would place one.
        (Vector3 At, double Time)? ghost = null;
        if (_grab == Grab.None && hovered == Hover.None && interact)
            for (var i = 0; i < plan.Legs.Count && ghost is null; i++)
            {
                var leg = plan.Legs[i];
                SampleLeg(leg, leg.End - leg.Start, frame, flight.Body, now, last: i == plan.Legs.Count - 1);
                if (Nearest(paths, pointer, PathPixels) is { } point && leg.Start + point.Seconds > now + MinimumGap)
                    ghost = (point.At, leg.Start + point.Seconds);
            }
        PointerOver = interact && (_grab != Grab.None || hovered != Hover.None || ghost is not null);

        if (rightClicked && hovered == Hover.Node)
            Delete(hoveredIndex);
        else if (clicked)
        {
            switch (hovered)
            {
                case Hover.Delete:
                    Delete(_selected);
                    break;
                case Hover.Handle:
                    (_grab, _handle) = (Grab.Handle, hoveredIndex);
                    break;
                case Hover.Node:
                    (_selected, _grab) = (hoveredIndex, Grab.Node);
                    break;
                default:
                    if (ghost is { } place)
                    {
                        var node = new ManoeuvreNode(place.Time);
                        _pending.Add(node);
                        _pending.Sort((a, b) => a.Time.CompareTo(b.Time));
                        _selected = _pending.IndexOf(node);
                        _changed = true;
                    }
                    else
                        _selected = -1;
                    break;
            }
        }

        // Drawing: the nodes, the selected one's handles and delete button, and the ghost.
        var color = Paths.PlanColor;
        if (ghost is { } g && paths.InView(g.At, out var ghostAt))
            draw.AddCircle(ghostAt, 5f, Color(color, alpha * 0.9f), 0, 1.5f);
        // (Only those there were at the start of the frame: one placed or deleted just now shows from the next.)
        for (var i = 0; i < shown.Length && i < _pending.Count; i++)
        {
            if (!shown[i])
                continue;
            var at = onScreen[i];
            var hot = i == _selected || (hovered == Hover.Node && hoveredIndex == i);
            draw.AddCircleFilled(at, hot ? NodePixels + 1.5f : NodePixels, Color(color, alpha));
            draw.AddCircle(at, (hot ? NodePixels + 1.5f : NodePixels) + 1f, Color((0, 0, 0), alpha * 0.6f), 0, 1.5f);
            var node = _pending[i];
            Label(draw, at + new float2(10f, 8f), color, alpha, $"{node.Size:0.0} m/s  in {Clock(node.Time - now)}");
        }
        if (_selected >= 0 && _selected == selectedAtStart && _selected < shown.Length && _selected < _pending.Count && shown[_selected])
        {
            var at = onScreen[_selected];
            draw.AddCircle(at, NodePixels + 5f, Color(color, alpha), 0, 1.5f);
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
                    var node = _pending[_selected];
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

    private void Delete(int index)
    {
        if (index < 0 || index >= _pending.Count)
            return;
        _pending.RemoveAt(index);
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

    /// <summary>The sample (of <see cref="_samples"/>) nearest <paramref name="pointer"/> on the screen, if within <paramref name="within"/> pixels.</summary>
    private (Vector3 At, double Seconds)? Nearest(Paths paths, float2 pointer, float within)
    {
        (Vector3 At, double Seconds)? best = null;
        var bestDistance = within;
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

    private static float Distance(float2 a, float2 b) => Vector2.Distance(V2(a), V2(b));
    private static Vector2 V2(float2 v) => new(v.X, v.Y);
    private static float2 F2(Vector2 v) => new(v.X, v.Y);
}
