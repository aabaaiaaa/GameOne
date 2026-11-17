using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace GameOne;

/// <summary>
/// Steering with the mouse: two rings drawn round the rocket's centre of mass, which appear when
/// the pointer comes near it. The flat one (marked N E S W) is the heading, the compass direction
/// to point the nose; the upright one, standing along that heading, is the pitch, how far up from
/// the horizon. Each has a handle where the nose points now, and a hollow marker where the rocket is
/// actually going, in the frame in use (<see cref="Flight.FrameVelocity"/>); in the target frame,
/// markers towards the target and away from it too.
/// </summary>
/// <remarks>
/// <para>The ring nearest the pointer lights up: press the left button and drag to move its handle
/// round it. While the button is held, the rocket is asked to hold that direction
/// (<see cref="Hold"/>); it turns there only as fast as it can (see <see cref="Flight.TurnRate"/>),
/// and in thick, fast air it can't hold far off its direction of travel. Let go and the handles go
/// back to following the nose, wherever the air (or nothing, above it) turns it.</para>
/// <para>The cursor is followed from its move events, not read from the window, so a test
/// driver's posted mouse messages work too.</para>
/// </remarks>
internal sealed class NavigationControl
{
    private enum Ring
    {
        None,
        Heading,
        Pitch,
    }

    private const int Segments = 96;

    private static readonly (byte R, byte G, byte B) HeadingColor = (90, 205, 255);
    private static readonly (byte R, byte G, byte B) PitchColor = (255, 170, 60);
    private static readonly (byte R, byte G, byte B) OvershotColor = (255, 190, 50);

    private readonly Vector2[] _headingPoints = new Vector2[Segments + 1], _pitchPoints = new Vector2[Segments + 1];
    private readonly bool[] _headingFront = new bool[Segments + 1], _pitchFront = new bool[Segments + 1];
    private Vector2 _cursor;
    private bool _pressed, _released; // waiting to be judged (see Update)
    private int _pressWait;           // the Updates the press has waited
    private Vector2 _pressAt;         // where it was pressed
    private Ring _hover, _dragging;
    private float _heading = MathF.PI / 2; // compass bearing of the nose, radians clockwise from north: east to start with
    private float _pitch = MathF.PI / 2;   // its angle up from the horizon along that heading; past 90° it tips back the other way
    private float _visibility;

    // This frame's rings: where they are in the world, and on the screen.
    private bool _onScreen;
    private bool _centreShown; // the rocket's middle is on the screen, so the burn's mark can be placed (the rings may not fit)
    private Vector3 _centre, _up, _travel;
    private Vector3? _towards;
    private float _slowest; // the slowest travel its markers show (Flight.MarkerFloor)
    private Vector2 _centreOnScreen, _display;
    private float _radius;
    private ChaseCamera? _camera;

    /// <summary>The direction the pilot is holding the nose at, while dragging a ring; otherwise null.</summary>
    public Vector3? Hold { get; private set; }

    /// <summary>Whether the pilot is steering with the keys (set before <see cref="Update"/>): the rings show meanwhile, following the nose round.</summary>
    public bool KeysHeld { get; set; }

    /// <summary>Whether the pointer is on a marker (set before <see cref="Update"/>): a click there is for the marker, so the rings don't take it.</summary>
    public bool Blocked { get; set; }

    /// <summary>
    /// The next node's burn still needed, if there is a node (set before <see cref="Update"/>), and
    /// whether it has gone past, and whether the burn is matched: a diamond on each ring marks which
    /// way it points, and, whether or not the rings show, a mark in the view out at the ring's radius
    /// (green when matched). Steering there is the pilot's.
    /// </summary>
    public (Vector3 Way, bool Overshot, bool Matched)? Burn { get; set; }

    /// <summary>Whether the pointer is on a ring, or dragging one: a click then is for steering.</summary>
    public bool InUse => _hover != Ring.None || _dragging != Ring.None;

    public NavigationControl(GlfwWindow window)
    {
        window.OnMouseButton += (_, button, action, _) =>
        {
            if (button != GlfwMouseButton.Left)
                return;
            if (action == GlfwButtonAction.Press)
                (_pressed, _pressWait, _pressAt) = (true, 0, _cursor);
            else if (action == GlfwButtonAction.Release)
                _released = true;
        };
        // The pointer as last seen, kept when it leaves the window: a test driver's posted moves leave
        // the real cursor off it, so Windows reports a leave after every one, and ending the hover
        // there would hide the rings from the driver's shots. (Rings left lit are harmless.)
        window.OnCursorPos += (_, pos) => _cursor = new Vector2((float)pos.X, (float)pos.Y);
    }

    /// <summary>
    /// Follows the rocket and the mouse: lays the rings out round the rocket as the camera sees it,
    /// works out which (if any) the pointer is over, and moves the one being dragged. Call once per
    /// frame, after the camera has moved; <paramref name="enabled"/> is false while a menu is up.
    /// </summary>
    public void Update(float dt, Flight flight, ChaseCamera camera, Vector2 display, bool enabled)
    {
        // A press is judged once ImGui has taken it in (its WantCaptureMouse at the press is for
        // wherever the pointer was before: a test driver's pointer jumps from a panel to the
        // rocket and presses within a frame, and ImGui's queue can hold the press back a frame or
        // two), as ChaseCamera does; the first Update after it goes by, as ImGui's pass before it
        // ran before the press came. A release waits for its press to be judged.
        var pressed = false;
        if (_pressed && (++_pressWait > 1 && ImGui.IsMouseClicked(ImGuiMouseButton.Left) || _pressWait > ChaseCamera.MaxJudgeWait))
        {
            pressed = !ImGui.GetIO().WantCaptureMouse;
            _pressed = false;
        }
        var released = _released && !_pressed;
        if (released)
            _released = false;

        if (!enabled || flight.State != FlightState.Flying || display.X <= 0 || display.Y <= 0)
        {
            _dragging = _hover = Ring.None;
            _visibility = 0f;
            _onScreen = _centreShown = false;
            Hold = null;
            return;
        }

        _camera = camera;
        _display = display;
        _centre = flight.CentreOfMass;
        _up = flight.Controlled.Body.Up(_centre);
        _travel = flight.FrameVelocity; // the way the holds steer by, in the frame in use
        _towards = flight.TowardsTarget;
        _slowest = flight.MarkerFloor;
        if (_dragging == Ring.None)
            FollowNose(flight.Aim);

        _onScreen = _centreShown = camera.ToScreen(_centre, display, out _centreOnScreen, out var depth);
        if (_onScreen)
        {
            _radius = Scene.NavigationRingPixels * ChaseCamera.MetresPerPixel(depth, display);
            for (var i = 0; i <= Segments; i++)
            {
                var angle = MathF.Tau * i / Segments;
                _onScreen &= Place(_centre + Planet.Towards(_centre, angle) * _radius, out _headingPoints[i], out _headingFront[i]);
                _onScreen &= Place(_centre + Direction(_heading, angle - MathF.PI) * _radius, out _pitchPoints[i], out _pitchFront[i]);
            }
        }

        // Which ring the pointer is on, if it is near the rocket (and not over a HUD window); and
        // a press grabs the ring it was pressed on, judged where it was pressed (it is judged a
        // frame or two later, and a quick flick may be well off the ring by then).
        _hover = _dragging == Ring.None ? RingAt(_cursor) : Ring.None;
        if (pressed && _dragging == Ring.None && RingAt(_pressAt) is var grabbed and not Ring.None)
            _dragging = grabbed;
        // A tap on a ring (its press and release judged together) steers once, to where it was
        // pressed, as a hold that ends at once.
        var tap = pressed && released && _dragging != Ring.None;
        if (released && !tap)
            _dragging = Ring.None;

        if (_dragging != Ring.None && _onScreen)
            Drag(tap ? _pressAt : _cursor);
        Hold = _dragging != Ring.None ? Direction(_heading, _pitch) : null;
        if (tap)
            _dragging = Ring.None;

        var near = _onScreen && !ImGui.GetIO().WantCaptureMouse && !Blocked
            && Vector2.Distance(_cursor, _centreOnScreen) < Scene.NavigationRingPixels * 1.6f;

        var shown = near || KeysHeld || _dragging != Ring.None ? 1f : 0f;
        _visibility += Math.Clamp(shown - _visibility, -dt * 4f, dt * 8f);

        bool Place(Vector3 world, out Vector2 screen, out bool front)
        {
            var visible = camera.ToScreen(world, display, out screen, out var d);
            front = d < depth;
            return visible;
        }
    }

    /// <summary>Sets the handles to where the nose points. With the nose straight up, the heading stays as it was.</summary>
    private void FollowNose(Vector3 aim)
    {
        var upward = Vector3.Dot(aim, _up);
        var across = aim - _up * upward;
        if (across.Length() > 0.02f)
            _heading = Planet.Bearing(_centre, across);
        _pitch = MathF.Atan2(upward, Vector3.Dot(aim, Planet.Towards(_centre, _heading)));
    }

    /// <summary>Moves the dragged ring's handle to the point on it nearest <paramref name="at"/> (the pointer), snapping to straight up and the compass points.</summary>
    private void Drag(Vector2 at)
    {
        if (_dragging == Ring.Heading)
        {
            var (_, along) = Nearest(_headingPoints, at);
            _heading = Snap(MathF.Tau * along / Segments, MathF.PI / 2, 2f);
        }
        else
        {
            var (_, along) = Nearest(_pitchPoints, at);
            _pitch = Snap(MathF.Tau * along / Segments - MathF.PI, MathF.PI, Scene.NavigationUpSnapDegrees, onlyUp: true);
        }
    }

    /// <summary>
    /// Snaps <paramref name="angle"/> to the nearest multiple of <paramref name="step"/> (or with
    /// <paramref name="onlyUp"/>, only to straight up) when it is within <paramref name="degrees"/> of it.
    /// </summary>
    private static float Snap(float angle, float step, float degrees, bool onlyUp = false)
    {
        var target = onlyUp ? MathF.PI / 2 : MathF.Round(angle / step) * step;
        return MathF.Abs(angle - target) < float.DegreesToRadians(degrees) ? target % MathF.Tau : angle;
    }

    /// <summary>The way the nose points for a heading and a pitch.</summary>
    private Vector3 Direction(float heading, float pitch) =>
        Planet.Towards(_centre, heading) * MathF.Cos(pitch) + _up * MathF.Sin(pitch);

    /// <summary>
    /// How far <paramref name="at"/> (the pointer) is from a ring on the screen, and where along it the nearest point is,
    /// in segments (fractional) from its start.
    /// </summary>
    private (float Distance, float Along) Nearest(Vector2[] points, Vector2 at)
    {
        var best = (Distance: float.MaxValue, Along: 0f);
        for (var i = 0; i < Segments; i++)
        {
            var a = points[i];
            var ab = points[i + 1] - a;
            var t = ab.LengthSquared() > 1e-6f ? Math.Clamp(Vector2.Dot(at - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            var distance = Vector2.Distance(at, a + ab * t);
            if (distance < best.Distance)
                best = (distance, i + t);
        }
        return best;
    }

    /// <summary>
    /// The ring at <paramref name="at"/> on the screen, if it is near the rocket (and not over a
    /// HUD window, nor on a marker), within grabbing distance of one; else none.
    /// </summary>
    private Ring RingAt(Vector2 at)
    {
        if (!_onScreen || ImGui.GetIO().WantCaptureMouse || Blocked
            || Vector2.Distance(at, _centreOnScreen) >= Scene.NavigationRingPixels * 1.6f)
            return Ring.None;
        var (toHeading, _) = Nearest(_headingPoints, at);
        var (toPitch, _) = Nearest(_pitchPoints, at);
        if (MathF.Min(toHeading, toPitch) >= Scene.NavigationGrabPixels)
            return Ring.None;
        return toHeading <= toPitch ? Ring.Heading : Ring.Pitch;
    }

    /// <summary>Draws the rings, their handles and the direction-of-travel markers, while they are showing.</summary>
    public void Draw(ImDrawListPtr draw)
    {
        // The burn's mark, out at the rings' radius along the burn still needed: shown with the rings or without.
        if (_centreShown && Burn is { } mark && mark.Way.Length() > 0.01f)
            Diamond(draw, _centre + Vector3.Normalize(mark.Way) * _radius, mark.Matched ? Scene.NodeMatchedColor : Paths.PlanColor);
        if (!_onScreen || _visibility < 0.01f)
            return;

        var headingHot = _hover == Ring.Heading || _dragging == Ring.Heading;
        var pitchHot = _hover == Ring.Pitch || _dragging == Ring.Pitch;
        DrawRing(draw, _headingPoints, _headingFront, HeadingColor, headingHot);
        DrawRing(draw, _pitchPoints, _pitchFront, PitchColor, pitchHot);

        // The compass points round the heading ring, and the top of the pitch ring.
        ReadOnlySpan<string> points = ["N", "E", "S", "W"];
        for (var i = 0; i < 4; i++)
            Label(draw, _centre + Planet.Towards(_centre, i * MathF.PI / 2) * (_radius * 1.18f), points[i], HeadingColor, 0.9f);
        Label(draw, _centre + _up * (_radius * 1.15f), "UP", PitchColor, 0.9f);

        // Where the rocket is going: a hollow marker on each ring.
        var acrossTravel = _travel - _up * Vector3.Dot(_travel, _up);
        if (acrossTravel.Length() > _slowest)
            Marker(draw, _centre + Planet.Towards(_centre, Planet.Bearing(_centre, acrossTravel)) * _radius);
        if (_travel.Length() > 2f * _slowest)
        {
            var heading = Planet.Towards(_centre, _heading);
            var inPlane = heading * Vector3.Dot(_travel, heading) + _up * Vector3.Dot(_travel, _up);
            if (inPlane.Length() > _slowest)
                Marker(draw, _centre + Vector3.Normalize(inPlane) * _radius);
        }

        // In the target frame, which way the target is (a ring with a cross) and the way away from
        // it (with an X): beside the relative velocity's marker, whether it is closing on the
        // target or sliding past it.
        if (_towards is { } towards && towards.Length() > 0.01f)
        {
            var way = Vector3.Normalize(towards);
            TargetMarks(draw, way, away: false);
            TargetMarks(draw, -way, away: true);
        }

        // Which way the next node's burn points: a diamond on each ring, as for the way it is going.
        if (Burn is { } burn && burn.Way.Length() > 0.01f)
        {
            var color = burn.Overshot ? OvershotColor : Paths.PlanColor;
            var across = burn.Way - _up * Vector3.Dot(burn.Way, _up);
            if (across.Length() > 0.01f * burn.Way.Length())
                Diamond(draw, _centre + Planet.Towards(_centre, Planet.Bearing(_centre, across)) * _radius, color);
            var heading = Planet.Towards(_centre, _heading);
            var inPlane = heading * Vector3.Dot(burn.Way, heading) + _up * Vector3.Dot(burn.Way, _up);
            if (inPlane.Length() > 0.01f * burn.Way.Length())
                Diamond(draw, _centre + Vector3.Normalize(inPlane) * _radius, color);
        }

        // The handles, where the nose points; a line from the centre out to the pitch handle is the nose.
        var nose = _centre + Direction(_heading, _pitch) * _radius;
        if (Screen(nose, out var noseAt) && Screen(_centre + Planet.Towards(_centre, _heading) * _radius, out var headingAt))
        {
            draw.AddLine(F2(_centreOnScreen), F2(noseAt), Color(PitchColor, 0.5f), 1.5f);
            draw.AddCircleFilled(F2(headingAt), headingHot ? 8f : 6f, Color(HeadingColor, 1f));
            draw.AddCircleFilled(F2(noseAt), pitchHot ? 8f : 6f, Color(PitchColor, 1f));
        }
        draw.AddCircleFilled(F2(_centreOnScreen), 3f, Color((255, 255, 255), 0.8f));

        // While dragging, what it is set to, by the pointer.
        if (_dragging != Ring.None)
        {
            var text = _dragging == Ring.Heading
                ? $"Heading {float.RadiansToDegrees(_heading):000}°"
                : $"Pitch {float.RadiansToDegrees(MathF.Abs(_pitch) > MathF.PI / 2 ? MathF.Sign(_pitch) * MathF.PI - _pitch : _pitch):0}°"
                  + (MathF.Abs(_pitch) > MathF.PI / 2 ? " (tipped back)" : "");
            Outlined(draw, F2(_cursor + new Vector2(16f, 10f)), Color((255, 255, 255), 1f), text);
        }
    }

    /// <summary>One ring: its near half bright, its far half (behind the rocket's middle) faint.</summary>
    private void DrawRing(ImDrawListPtr draw, Vector2[] points, bool[] front, (byte R, byte G, byte B) color, bool hot)
    {
        var strength = hot ? 1f : 0.55f;
        var thickness = hot ? 3f : 1.6f;
        for (var i = 0; i < Segments; i++)
            draw.AddLine(F2(points[i]), F2(points[i + 1]), Color(color, strength * (front[i] ? 1f : 0.4f)), thickness);
    }

    private void Marker(ImDrawListPtr draw, Vector3 world)
    {
        if (!Screen(world, out var at))
            return;
        var white = Color((255, 255, 255), 0.95f);
        draw.AddCircle(F2(at), 6f, white, 0, 2f);
        draw.AddCircleFilled(F2(at), 1.5f, white);
    }

    /// <summary>A towards-target (or with <paramref name="away"/>, away-from-target) marker on each ring, for the unit direction <paramref name="way"/>.</summary>
    private void TargetMarks(ImDrawListPtr draw, Vector3 way, bool away)
    {
        var across = way - _up * Vector3.Dot(way, _up);
        if (across.Length() > 0.01f)
            TargetMark(draw, _centre + Planet.Towards(_centre, Planet.Bearing(_centre, across)) * _radius, away);
        var heading = Planet.Towards(_centre, _heading);
        var inPlane = heading * Vector3.Dot(way, heading) + _up * Vector3.Dot(way, _up);
        if (inPlane.Length() > 0.01f)
            TargetMark(draw, _centre + Vector3.Normalize(inPlane) * _radius, away);
    }

    private void TargetMark(ImDrawListPtr draw, Vector3 world, bool away)
    {
        if (!Screen(world, out var at))
            return;
        var color = Color(Paths.TargetColor, 0.95f);
        const float size = 4f;
        draw.AddCircle(F2(at), 7f, color, 0, 2f);
        var (a, b) = away ? (new Vector2(size, size), new Vector2(size, -size)) : (new Vector2(size, 0), new Vector2(0, size));
        draw.AddLine(F2(at - a), F2(at + a), color, 1.5f);
        draw.AddLine(F2(at - b), F2(at + b), color, 1.5f);
    }

    private void Diamond(ImDrawListPtr draw, Vector3 world, (byte R, byte G, byte B) color)
    {
        if (!Screen(world, out var at))
            return;
        const float size = 7f;
        var (top, right, bottom, left) = (F2(at + new Vector2(0, -size)), F2(at + new Vector2(size, 0)), F2(at + new Vector2(0, size)), F2(at + new Vector2(-size, 0)));
        draw.AddQuadFilled(top, right, bottom, left, Color(color, 0.95f));
        draw.AddQuad(top, right, bottom, left, Color((0, 0, 0), 0.7f), 1.5f);
    }

    private void Label(ImDrawListPtr draw, Vector3 world, string text, (byte R, byte G, byte B) color, float strength)
    {
        if (!Screen(world, out var at))
            return;
        var size = ImGui.CalcTextSize(text);
        Outlined(draw, new float2(at.X - size.X / 2, at.Y - size.Y / 2), Color(color, strength), text);
    }

    /// <summary>Where a point near the rings is on the screen, as this frame's camera sees it.</summary>
    private bool Screen(Vector3 world, out Vector2 at)
    {
        at = default;
        return _camera is { } camera && camera.ToScreen(world, _display, out at, out _);
    }

    private ImColor8 Color((byte R, byte G, byte B) color, float strength) =>
        new(color.R, color.G, color.B, (byte)(255 * Math.Clamp(strength * _visibility, 0f, 1f)));

    private void Outlined(ImDrawListPtr draw, float2 at, ImColor8 color, string text)
    {
        var shadow = new ImColor8(0, 0, 0, (byte)(200 * _visibility));
        foreach (var offset in (ReadOnlySpan<float2>)[new(-1, 0), new(1, 0), new(0, -1), new(0, 1)])
            draw.AddText(at + offset, shadow, text);
        draw.AddText(at, color, text);
    }

    private static float2 F2(Vector2 v) => new(v.X, v.Y);
}
