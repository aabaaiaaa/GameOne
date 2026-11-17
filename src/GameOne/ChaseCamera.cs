using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;

namespace GameOne;

/// <summary>
/// The third-person camera: it follows the rocket from behind and a little above, trailing just
/// behind its movement so speed can be felt (but never more than <see cref="Scene.CameraMaxLag"/>).
/// Drag with the right mouse button to orbit round it (the left one steers, with the direction rings),
/// with the middle one to turn the view in place (free look: the camera stays where it is and the view
/// turns from straight down to straight up, until an orbit drag or the wheel looks at the vessel again),
/// and use the wheel to zoom (far enough out, in orbit, to see the planet curve away). A right press
/// that barely moves before it is let go is a click (<see cref="TakeRightClick"/>: the HUD opens the
/// flown vessel's actions on one on it), and turns the camera only by those few pixels. It never turns on
/// its own, so "away from the camera" (which is how the steering works) only changes when the
/// player turns it.
/// </summary>
/// <remarks>
/// Its angles are measured in the local frame where the rocket is (up away from the body it is at, east
/// and north across the ground), so it stays upright relative to the ground below, wherever round
/// that body the rocket has flown (and eased round to a new body's across a sphere of influence's edge: <see cref="Rebase"/>).
/// <para>Zoomed out (<see cref="ToggleZoomOut"/>), it looks from farther out at what it looks at when
/// near (the vessel), with angles of its own measured from the north pole of the body its vessel is at
/// (so from above the plane the orbits are flown in), to go back to it just as it was. It opens on the
/// angles and distance the player last set in it by hand (kept as its vessel's view, <see cref="Apply"/>),
/// or, if they have not, on a view of its own, fitted afresh each time (from a little to the vessel's side of
/// the pole, far enough out for its orbit).</para>
/// </remarks>
internal sealed class ChaseCamera
{
    private const float MinPitch = -0.15f, MaxPitch = 1.4f; // radians: from just below level to nearly overhead
    private const float FreeLookMaxPitch = 1.5675f;         // free look: to within a quarter of a degree of straight up or down
    private const float ZoomedOutMaxPitch = 1.55f;          // zoomed out, over the pole and below it too

    // Zoomed out, the frame its angles are in (PoleFrame): up is its body's north pole (the launch
    // goes east, so the orbits lie round it), and its frame's origin (the planet's pad) is to the
    // south. And the body's centre, which the side of the pole its vessel is on is told by.
    private Vector3 _poleUp = -Vector3.UnitZ, _poleEast = Vector3.UnitX, _poleSouth = Vector3.UnitY;
    private Vector3 _zoomCentre = GroundBody.Planet.Centre;

    private float _yaw;   // radians round the rocket: 0 = to its south, looking north
    private float _pitch = float.DegreesToRadians(Scene.CameraPitchDegrees);
    private float _distance = Scene.CameraDistance;
    private Vector3 _focus;     // what follows the vessel, lagging behind it
    private Vector3 _seen;      // what it looks at: _focus, but through a rebase's ease (see Ease)
    private Vector3 _lastFocus; // the vessel's focus at the last Update
    private Vector3 _up = Vector3.UnitY, _east = Vector3.UnitX, _south = Vector3.UnitZ;
    private bool _snap = true;
    private Vector2 _cursor;
    private Vector2 _pendingDrag, _pendingLook;
    private float _pendingZoom;

    // Free look: the view turned in place (the camera stays where it is), by these angles of the
    // direction it looks in, in the local frame: yaw as the orbit's (0 = looking south), pitch
    // up from level.
    private bool _free;
    private float _lookYaw, _lookPitch;

    private bool _zoomedOut;
    private float _out;   // 0 by the vessel, 1 zoomed out, in between on the way
    private float _outYaw, _outPitch, _outDistance;
    private bool _mapSet; // the map's angles and distance were set by hand (or brought as such): it opens on them, not on its fit of the orbit
    private Vector3 _worldUp = Vector3.UnitY;

    // Across a sphere of influence's edge (Rebase): the frames it eases from, carried into the new
    // body's frame (its own up, east and south, the zoomed-out ones and the centre), the turns from
    // the new body's frames to those (worked out at the first Update after, which knows the body),
    // and how far through the ease it is (1: none).
    private bool _easePending;
    private (Vector3 Up, Vector3 East, Vector3 South) _carried, _carriedPole;
    private Vector3 _carriedCentre, _carriedTrail;
    private Quaternion _easeTurn = Quaternion.Identity, _easePoleTurn = Quaternion.Identity;
    private Vector3 _easeCentre;
    private float _ease = 1f;

    public Vector3 Position { get; private set; }
    public Vector3 Target { get; private set; }

    /// <summary>Whether the view is turned away from the vessel (free look), until an orbit drag or the wheel looks at it again.</summary>
    public bool LookingFree => _free;

    /// <summary>What it follows: the middle of the vessel, even while zoomed out and looking elsewhere.</summary>
    public Vector3 Focus => _seen;

    /// <summary>Whether it is zoomed out (or on its way there), rather than by the vessel.</summary>
    public bool ZoomedOut => _zoomedOut;

    public Vector3 Forward => Vector3.Normalize(Target - Position);

    /// <summary>
    /// Its near and far planes (<see cref="Planes"/>): what the renderer draws its view with, and
    /// <see cref="ViewProjection"/> projects by.
    /// </summary>
    public (float Near, float Far) Clip => Planes;

    /// <summary>Which way is up for the camera: up from the ground below it (zoomed out, the north pole).</summary>
    public Vector3 WorldUp => _worldUp;

    /// <summary>The camera's up, for the audio listener.</summary>
    public Vector3 Up => Vector3.Normalize(Vector3.Cross(Vector3.Cross(Forward, _worldUp), Forward));

    /// <summary>The way the camera faces, flattened onto the ground: what "away from the camera" means to the steering.</summary>
    public Vector3 GroundForward => -(_east * MathF.Sin(_yaw) + _south * MathF.Cos(_yaw));

    /// <summary>The camera's right, flattened onto the ground.</summary>
    public Vector3 GroundRight => _east * MathF.Cos(_yaw) - _south * MathF.Sin(_yaw);

    /// <param name="window">The window whose mouse turns and zooms it, or null for a camera nothing turns (a small view's).</param>
    public ChaseCamera(GlfwWindow? window)
    {
        _drags = [_orbitMouse, _lookMouse];
        if (window is null)
            return;
        // The cursor is followed from its move events rather than read from the window when the
        // button goes down: that would be the system's cursor, which a test driver's posted
        // mouse messages don't move.
        // A press or a turn of the wheel over a HUD panel is the panel's, which ImGui's
        // WantCaptureMouse tells, but only once ImGui has taken the event in: at the press it is
        // still for wherever the pointer was before (a test driver's pointer jumps from a panel to
        // the view and presses within a frame), and ImGui's queue can hold the press back a frame
        // or two more (one button change a frame). So each is held, and the drag after a press
        // gathered, until ImGui's pass has taken it in, and judged then (Judge).
        _mouse = true;
        window.OnMouseButton += (_, button, action, _) =>
        {
            if (TurnFor(button) is { } turn)
                Button(turn, action == GlfwButtonAction.Press);
        };
        window.OnCursorPos += (_, pos) => Pointer(new Vector2((float)pos.X, (float)pos.Y));
        window.OnScroll += (_, offset) => _zoom += (float)offset.Y;
    }

    /// <summary>How many frames a press or the wheel waits at most for ImGui to take it in (it should within two or three).</summary>
    internal const int MaxJudgeWait = 10;

    // What the mouse did that waits for ImGui to judge it (see the constructor): a press (and the
    // drag since, and whether it has been let go meanwhile, and where), and the wheel, each with
    // the Updates it has waited.
    private readonly bool _mouse; // whether it has a window whose mouse turns it
    private sealed class MouseDrag(MouseTurn turn)
    {
        public readonly MouseTurn Turn = turn;
        public bool Press, Dragging, Up;
        public Vector2 Held, Moved; // the drag before the press was judged, and since
        public Vector2 UpAt;        // where it was let go, if before it was judged
        public float Path;          // how far the pointer has moved since the press, all told
        public int Wait;
    }

    private readonly MouseDrag _orbitMouse = new(MouseTurn.Orbit), _lookMouse = new(MouseTurn.Look);
    private readonly MouseDrag[] _drags; // both, for the loops over them (kept: nothing is allocated a frame)
    private float _zoom;
    private int _zoomWait;
    private Vector2? _rightClick;

    /// <summary>
    /// A button that turns it (<see cref="TurnFor"/>) going down or up, as the window's events
    /// give it. Down starts a press, held for judging (<see cref="JudgeMouse"/>); up ends the drag,
    /// and a right one whose path was short enough (<see cref="IsClick"/>) is a click there
    /// (<see cref="TakeRightClick"/>). One let go before it is judged is judged all the same, and ended then.
    /// </summary>
    internal void Button(MouseTurn turn, bool down)
    {
        var drag = turn == MouseTurn.Orbit ? _orbitMouse : _lookMouse;
        if (down)
        {
            (drag.Press, drag.Dragging, drag.Up) = (true, false, false);
            (drag.Held, drag.Path, drag.Wait) = (Vector2.Zero, 0f, 0);
        }
        else if (drag.Press)
            (drag.Up, drag.UpAt) = (true, _cursor);
        else if (drag.Dragging)
        {
            drag.Dragging = false;
            LetGo(drag, _cursor);
        }
    }

    /// <summary>The pointer moved to <paramref name="cursor"/> (pixels from the window's top left), as the window's events give it: a held press's drag.</summary>
    internal void Pointer(Vector2 cursor)
    {
        var moved = cursor - _cursor;
        foreach (var drag in _drags)
            if (drag.Dragging)
                (drag.Moved, drag.Path) = (drag.Moved + moved, drag.Path + moved.Length());
            else if (drag.Press && !drag.Up)
                (drag.Held, drag.Path) = (drag.Held + moved, drag.Path + moved.Length());
        _cursor = cursor;
    }

    /// <summary>A drag on the view let go at <paramref name="at"/>: a right one moved less than the slop all told is a click there.</summary>
    private void LetGo(MouseDrag drag, Vector2 at)
    {
        if (drag.Turn == MouseTurn.Orbit && IsClick(drag.Path))
            _rightClick = at;
    }

    /// <summary>
    /// Whether a right press whose pointer moved <paramref name="path"/> pixels all told, from going
    /// down to letting go, is a click rather than a drag: under <see cref="Scene.RightClickSlopPixels"/>.
    /// It is the length moved, not how far from where it began, so a drag out and back is a drag.
    /// </summary>
    internal static bool IsClick(float path) => path < Scene.RightClickSlopPixels;

    /// <summary>
    /// The right click on the view since the last call, if there was one: where it was let go, in
    /// pixels from the window's top left (not on a panel: judged as a drag is). The drag it was
    /// has turned the camera by its few pixels, as any right drag does.
    /// </summary>
    public Vector2? TakeRightClick()
    {
        var click = _rightClick;
        _rightClick = null;
        return click;
    }

    /// <summary>
    /// Takes a waiting press (as a drag) and wheel once ImGui's last pass has taken them in (or
    /// they have waited too long), unless ImGui took them for itself: the pointer on a panel. The
    /// first Update after one goes by: ImGui's pass before it ran before the event came.
    /// </summary>
    private void Judge()
    {
        if (!_mouse)
            return;
        var io = ImGui.GetIO();
        JudgeMouse(ImGui.IsMouseClicked(ButtonFor(MouseTurn.Orbit)), ImGui.IsMouseClicked(ButtonFor(MouseTurn.Look)), io.WantCaptureMouse);
        if (_zoom != 0f && (++_zoomWait > 1 && io.MouseWheel != 0f || _zoomWait > MaxJudgeWait))
        {
            if (!io.WantCaptureMouse)
                Zoom(_zoom);
            (_zoom, _zoomWait) = (0f, 0);
        }
    }

    /// <summary>
    /// The presses' half of <see cref="Judge"/>, given what ImGui's last pass said: whether it has
    /// taken in a right press (<paramref name="orbitClicked"/>) or a middle one
    /// (<paramref name="lookClicked"/>), and whether it wants the mouse (<paramref name="wantCapture"/>).
    /// Each press waiting is judged (<see cref="JudgePress"/>) as a drag of the view or not; one let
    /// go before that is ended now, a click if its path was short enough. Then the drag gathered is turned.
    /// </summary>
    internal void JudgeMouse(bool orbitClicked, bool lookClicked, bool wantCapture)
    {
        foreach (var drag in _drags)
        {
            if (drag.Press && ++drag.Wait is var wait
                && JudgePress(wait, drag.Turn == MouseTurn.Orbit ? orbitClicked : lookClicked, wantCapture) is { } dragging)
            {
                drag.Dragging = dragging;
                if (drag.Dragging)
                    drag.Moved += drag.Held;
                (drag.Press, drag.Held) = (false, Vector2.Zero);
                if (drag.Up && drag.Dragging)
                {
                    drag.Dragging = false;
                    LetGo(drag, drag.UpAt);
                }
                drag.Up = false;
            }
            if (drag.Moved != Vector2.Zero)
            {
                Turn(drag.Turn, drag.Moved);
                drag.Moved = Vector2.Zero;
            }
        }
    }

    /// <summary>What a mouse drag does to the camera: orbits the vessel, or turns the view in place (free look).</summary>
    internal enum MouseTurn { Orbit, Look }

    /// <summary>What a drag with <paramref name="button"/> does: the right orbits, the middle looks; any other none.</summary>
    internal static MouseTurn? TurnFor(GlfwMouseButton button) => button switch
    {
        GlfwMouseButton.Right => MouseTurn.Orbit,
        GlfwMouseButton.Middle => MouseTurn.Look,
        _ => null,
    };

    /// <summary>The button ImGui knows the drag that does <paramref name="turn"/> by (the same as <see cref="TurnFor"/>'s).</summary>
    internal static ImGuiMouseButton ButtonFor(MouseTurn turn) => turn == MouseTurn.Orbit ? ImGuiMouseButton.Right : ImGuiMouseButton.Middle;

    /// <summary>
    /// A press that has waited <paramref name="wait"/> Updates, judged: none yet (ImGui has not
    /// taken it in, <paramref name="clicked"/> false, and it has not waited past
    /// <see cref="MaxJudgeWait"/>; the first Update never judges it, as ImGui's pass before it ran
    /// before the press came), else whether it drags the camera: only if ImGui doesn't want the
    /// mouse (<paramref name="wantCapture"/>: the pointer on a panel).
    /// </summary>
    internal static bool? JudgePress(int wait, bool clicked, bool wantCapture) =>
        wait > 1 && clicked || wait > MaxJudgeWait ? !wantCapture : null;

    /// <summary>A drag of <paramref name="pixels"/> doing <paramref name="turn"/> (<see cref="DragOrbit"/> or <see cref="DragLook"/>).</summary>
    internal void Turn(MouseTurn turn, Vector2 pixels)
    {
        if (turn == MouseTurn.Orbit)
            DragOrbit(pixels);
        else
            DragLook(pixels);
    }

    /// <summary>The right drag, by <paramref name="pixels"/>: orbits the vessel (and looks at it again).</summary>
    internal void DragOrbit(Vector2 pixels)
    {
        _pendingDrag += pixels;
        _free = false;
    }

    /// <summary>The middle drag, by <paramref name="pixels"/>: turns the view in place (free look).</summary>
    internal void DragLook(Vector2 pixels) => _pendingLook += pixels;

    /// <summary>The wheel, by <paramref name="clicks"/> (up is in): zooms, and looks at the vessel again.</summary>
    internal void Zoom(float clicks)
    {
        _pendingZoom += clicks;
        _free = false;
    }

    /// <summary>From the world to clip space, for a screen <paramref name="display"/> pixels across, as the renderer draws it.</summary>
    public Matrix4x4 ViewProjection(Vector2 display)
    {
        var (near, far) = Planes;
        return Matrix4x4.CreateLookAt(Position, Target, WorldUp)
            * Projection(float.DegreesToRadians(Scene.CameraVerticalFovDegrees), display.X / display.Y, near, far);
    }

    /// <summary>
    /// A camera's perspective projection with reversed depth, as the renderer's depth buffer has it
    /// (1 at the near plane, falling to 0 at the far one), for row vectors, clip space's Y up (the
    /// renderer flips it for Vulkan). With the far plane at infinity (<see cref="Planes"/>) the
    /// depth is the near plane's distance over the distance in front: a float's relative step is
    /// the same at any size, so a D32F depth buffer tells a few millimetres apart at 10 km and a
    /// few metres at the sun, however near the near plane is.
    /// </summary>
    public static Matrix4x4 Projection(float verticalFov, float aspect, float near, float far)
    {
        var y = 1f / MathF.Tan(verticalFov / 2f);
        // Depth is z over w in clip space, w the distance in front (-z in view space): 1 at the
        // near plane, 0 at the far one; with that at infinity, near over the distance.
        var (scale, offset) = float.IsPositiveInfinity(far) ? (0f, near) : (near / (far - near), near * far / (far - near));
        return new Matrix4x4(
            y / aspect, 0f, 0f, 0f,
            0f, y, 0f, 0f,
            0f, 0f, scale, -1f,
            0f, 0f, offset, 0f);
    }

    /// <summary>
    /// The near and far planes every camera draws with, at any zoom from the pad out to the map's
    /// furthest (the sun and the planet's path round it): the far one at infinity, so nothing is
    /// cut off however far out it is, and the near one <see cref="Scene.CameraNearPlane"/>. As the
    /// depth is reversed (<see cref="Projection"/>), its precision follows the distance at every
    /// zoom, so the planes need nothing from the zoom or from where the bodies are.
    /// </summary>
    public static (float Near, float Far) Planes => (Scene.CameraNearPlane, float.PositiveInfinity);

    /// <summary>
    /// Where <paramref name="world"/> appears on the screen, in pixels from its top left, and how far
    /// in front of the camera it is. False if it is behind the camera.
    /// </summary>
    public bool ToScreen(Vector3 world, Vector2 display, out Vector2 screen, out float depth)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), ViewProjection(display));
        depth = clip.W;
        if (clip.W <= 1e-4f)
        {
            screen = default;
            return false;
        }
        screen = new Vector2(clip.X / clip.W + 1f, 1f - clip.Y / clip.W) * display * 0.5f;
        return true;
    }

    /// <summary>How many metres one pixel covers, at <paramref name="distance"/> metres from the camera.</summary>
    public static float MetresPerPixel(float distance, Vector2 display) =>
        distance * 2f * MathF.Tan(float.DegreesToRadians(Scene.CameraVerticalFovDegrees) / 2) / display.Y;

    /// <summary>Jumps straight to the rocket next time, rather than flying there, e.g. after a restart.</summary>
    public void Snap() => (_snap, _easePending, _ease) = (true, false, 1f);

    /// <summary>Looks from the same angle and distance as <paramref name="other"/>, e.g. when a vessel moves from the main view to a small one.</summary>
    public void LookLike(ChaseCamera other)
    {
        (_yaw, _pitch, _distance) = (other._yaw, other._pitch, other._distance);
        (_free, _snap) = (false, true);
    }

    /// <summary>How it is set up now: the chase and map angles and distances, and whether it is the map.</summary>
    internal CameraView View => new(_zoomedOut, _yaw, _pitch, _distance, _outYaw, _outPitch, _outDistance, _mapSet, CameraCentre.Own);

    /// <summary>
    /// Sets up as <paramref name="view"/> says and snaps there, the map (if it shows it) fully out at
    /// once, so a view brought back lands as it was rather than zooming.
    /// </summary>
    internal void Apply(CameraView view)
    {
        Place(view.Yaw, view.Pitch, view.Distance);
        (_outYaw, _outPitch, _outDistance) = (view.MapYaw, Math.Clamp(view.MapPitch, -ZoomedOutMaxPitch, ZoomedOutMaxPitch),
            Math.Clamp(view.MapDistance, Scene.ZoomOutMinDistance, Scene.ZoomOutMaxDistance));
        _mapSet = view.MapSet;
        _zoomedOut = view.Map;
        _out = _zoomedOut ? 1f : 0f;
    }

    /// <summary>Looks from these angles and this far, as a drag and the wheel would leave it: for a view set up without the mouse.</summary>
    internal void Place(float yaw, float pitch, float distance)
    {
        _yaw = yaw;
        _pitch = Math.Clamp(pitch, MinPitch, MaxPitch);
        _distance = Math.Clamp(distance, Scene.CameraMinDistance, Scene.CameraMaxDistance);
        (_free, _snap) = (false, true);
    }

    /// <summary>
    /// Zooms all the way out, to look at the whole of its vessel's body and the orbits round it, far enough to
    /// take in <paramref name="reach"/> metres from its centre (the flown vessel's orbit), or from the
    /// angle and distance the player last set it to by hand (its view's, see <see cref="Apply"/>); or, if it is zoomed
    /// out already, back to the vessel, at the distance and angle it was at before.
    /// </summary>
    public void ToggleZoomOut(float reach)
    {
        _zoomedOut = !_zoomedOut;
        _free = false;
        if (!_zoomedOut)
            return;
        if (_mapSet)
            return;
        // Else starting a little to the vessel's side of the pole, so it is towards the bottom of the screen.
        var outward = _seen - _zoomCentre;
        _outYaw = MathF.Atan2(Vector3.Dot(outward, _poleEast), Vector3.Dot(outward, _poleSouth));
        _outPitch = float.DegreesToRadians(Scene.ZoomOutPitchDegrees);
        var fit = reach * Scene.ZoomOutMargin / MathF.Tan(float.DegreesToRadians(Scene.CameraVerticalFovDegrees) / 2);
        _outDistance = Math.Clamp(fit, Scene.ZoomOutMinDistance, Scene.ZoomOutMaxDistance);
    }

    /// <summary>
    /// Follows <paramref name="focus"/> (the middle of the rocket). Call once per frame, with the
    /// game time <paramref name="dt"/> since the last (for following the vessel) and the real time
    /// <paramref name="realDt"/> (for zooming out and back, and a rebase's ease, which take as long whatever the time
    /// warp; the game time if null). <paramref name="body"/> is the body the vessel is at:
    /// <paramref name="focus"/> is in its frame, and up, east and north are its
    /// there, and the camera keeps clear of its ground; zoomed out, it keeps clear of the sun where
    /// it is at <paramref name="worldTime"/> (<see cref="Scene.ZoomOutSunClearance"/>).
    /// </summary>
    public void Update(float dt, Vector3 focus, float? realDt, GroundBody body, double worldTime)
    {
        Judge();
        TurnLook();
        // As if grabbing the world and turning it: drag right and the scene turns right (the camera
        // swings round to the left); drag down and it tips towards you (the camera rises).
        if (_zoomedOut)
        {
            _outYaw -= _pendingDrag.X * Scene.CameraRadiansPerDragPixel;
            _outPitch = Math.Clamp(_outPitch + _pendingDrag.Y * Scene.CameraRadiansPerDragPixel, -ZoomedOutMaxPitch, ZoomedOutMaxPitch);
            _outDistance = Math.Clamp(_outDistance * MathF.Pow(0.9f, _pendingZoom), Scene.ZoomOutMinDistance, Scene.ZoomOutMaxDistance);
            _mapSet |= _pendingDrag != Vector2.Zero || _pendingZoom != 0f;
        }
        else
        {
            _yaw -= _pendingDrag.X * Scene.CameraRadiansPerDragPixel;
            _pitch = Math.Clamp(_pitch + _pendingDrag.Y * Scene.CameraRadiansPerDragPixel, MinPitch, MaxPitch);
            _distance = Math.Clamp(_distance * MathF.Pow(0.9f, _pendingZoom), Scene.CameraMinDistance, Scene.CameraMaxDistance);
        }
        _pendingDrag = Vector2.Zero;
        _pendingZoom = 0f;
        _out = Math.Clamp(_out + (_zoomedOut ? 1f : -1f) * (realDt ?? dt) / Scene.ZoomOutSeconds, 0f, 1f);

        _focus = _snap ? focus : Vector3.Lerp(_focus, focus, 1f - MathF.Exp(-Scene.CameraFollowRate * dt));
        _snap = false;
        // The lag grows with speed (at 160 m/s it would trail 16 m behind, more than a stage is
        // long), and staging moves the middle of the rocket in a jump: either way, keep it close.
        var behind = focus - _focus;
        if (behind.LengthSquared() > Scene.CameraMaxLag * Scene.CameraMaxLag)
            _focus = focus - Vector3.Normalize(behind) * Scene.CameraMaxLag;

        _lastFocus = focus;
        _seen = Seen(focus);
        (_up, _east, _south) = (body.Up(_seen), body.East(_seen), -body.North(_seen));
        (_poleUp, _poleEast, _poleSouth) = PoleFrame(body);
        _zoomCentre = body.Centre;
        Ease(realDt ?? dt);

        var (sinYaw, cosYaw) = MathF.SinCos(_yaw);
        var (sinPitch, cosPitch) = MathF.SinCos(_pitch);
        var offset = (_east * sinYaw + _south * cosYaw) * cosPitch + _up * sinPitch;
        var position = KeepClear(body, _seen + offset * _distance);

        Position = position;
        Target = _seen;
        if (_free)
        {
            var (sinLook, cosLook) = MathF.SinCos(_lookYaw);
            var (sinLift, cosLift) = MathF.SinCos(_lookPitch);
            Target = position + ((_east * sinLook + _south * cosLook) * cosLift + _up * sinLift) * _distance;
        }
        _worldUp = _up;
        if (_out > 0f)
        {
            ZoomedOutView(position);
            Position = OutsideTheSun(body, worldTime, Position);
        }
    }

    /// <summary>
    /// <paramref name="position"/> (in <paramref name="body"/>'s frame), or if it is within
    /// <see cref="Scene.ZoomOutSunClearance"/> of the sun's radii of its centre at
    /// <paramref name="worldTime"/>, moved straight out from it to there: so the map's camera, which
    /// can be turned and zoomed out past the sun, is never in it.
    /// </summary>
    private static Vector3 OutsideTheSun(GroundBody body, double worldTime, Vector3 position)
    {
        var sun = Bodies.Change(GroundBody.Planet, body, worldTime).Point(SolarSystem.SunCentreAt(worldTime));
        var away = (Vector3Double)position - sun;
        var clear = (double)Scene.ZoomOutSunClearance * Scene.SunRadius;
        var distance = away.Length();
        return distance >= clear || distance <= 0 ? position : (sun + away * (clear / distance)).ToSingle();
    }

    /// <summary>Zoomed out, or on the way: where it looks from and at, from where it would be by the vessel (<paramref name="position"/>).</summary>
    private void ZoomedOutView(Vector3 position)
    {
        // It always looks at the vessel. The distance changes by the same factor each moment (so
        // the zoom feels even, from metres to tens of kilometres), and the direction turns from
        // the chase view's to the map's with it.
        var (sinOutYaw, cosOutYaw) = MathF.SinCos(_outYaw);
        var (sinOutPitch, cosOutPitch) = MathF.SinCos(_outPitch);
        var outOffset = (_poleEast * sinOutYaw + _poleSouth * cosOutYaw) * cosOutPitch + _poleUp * sinOutPitch;
        var t = _out * _out * (3f - 2f * _out);
        var near = Vector3.Distance(position, _seen);
        var distance = near * MathF.Pow(_outDistance / near, t);
        var direction = Vector3.Normalize(Vector3.Lerp((position - _seen) / near, outOffset, t));
        Target = _seen;
        Position = _seen + direction * distance;
        _worldUp = Vector3.Normalize(Vector3.Lerp(_up, _poleUp, t));
    }

    /// <summary>
    /// The frame its angles are in zoomed out at <paramref name="body"/>: up its north pole, south
    /// towards its frame's origin (the planet's pad, the moon's 0° 0°) across the pole, east square to both.
    /// </summary>
    private static (Vector3 Up, Vector3 East, Vector3 South) PoleFrame(GroundBody body)
    {
        var up = Vector3.Normalize(body.NorthPole);
        var south = Vector3.UnitY - up * Vector3.Dot(up, Vector3.UnitY);
        south = south.LengthSquared() > 1e-6f ? Vector3.Normalize(south) : Vector3.Normalize(Vector3.UnitZ - up * Vector3.Dot(up, Vector3.UnitZ));
        return (up, Vector3.Cross(up, south), south);
    }

    /// <summary>
    /// Keeps its place and view across a change of the frame it is in (its vessel crossing a sphere
    /// of influence's edge, or one flown at another body: <see cref="Flight.ViewRebased"/>), by
    /// <paramref name="change"/> from the old frame to the new, at the moment it is next drawn: where
    /// it is and what it looks at, changed across. So is how far it trails behind the vessel, held
    /// as it was and eased into the new frame's own (the vessel moves differently over each body's
    /// ground, so its trail differs); and the way up it goes by (and, zoomed out, its pole and the
    /// centre it looks at), carried across and eased to the new body's, so the view rolls round to
    /// the new ground's up rather than in one frame: both over <see cref="Scene.CameraBodyEaseSeconds"/>
    /// of real time. One about to snap (to a vessel just switched to) has nothing to keep, and takes
    /// the new body's at once.
    /// </summary>
    public void Rebase(in FrameChange change)
    {
        var c = change;
        Vector3 At(Vector3 p) => c.Point(p).ToSingle();
        Vector3 Way(Vector3 d) => Vector3.Normalize(c.Direction(d).ToSingle());
        var trail = c.Direction(_lastFocus - _seen).ToSingle();
        (_seen, _lastFocus, Position, Target) = (At(_seen), At(_lastFocus), At(Position), At(Target));
        _focus = _seen; // the follower starts again from what it looks at
        (_up, _east, _south, _worldUp) = (Way(_up), Way(_east), Way(_south), Way(_worldUp));
        (_poleUp, _poleEast, _poleSouth, _zoomCentre) = (Way(_poleUp), Way(_poleEast), Way(_poleSouth), At(_zoomCentre));
        if (_snap)
        {
            (_easePending, _ease) = (false, 1f);
            return;
        }
        (_carried, _carriedPole, _carriedCentre, _carriedTrail) = ((_up, _east, _south), (_poleUp, _poleEast, _poleSouth), _zoomCentre, trail);
        _easePending = true;
    }

    /// <summary>How much of a rebase's ease is still to go (see <see cref="Rebase"/>): 1 just after it, easing smoothly to 0.</summary>
    private float EaseLeft => _easePending ? 1f : _ease >= 1f ? 0f : 1f - _ease * _ease * (3f - 2f * _ease);

    /// <summary>
    /// What it looks at for the vessel at <paramref name="focus"/>: the follower (<see cref="_focus"/>),
    /// but through a rebase's ease the trail carried across from the old frame, eased into the new
    /// frame's follower's.
    /// </summary>
    private Vector3 Seen(Vector3 focus)
    {
        var left = EaseLeft;
        return left > 0f ? Vector3.Lerp(_focus, focus - _carriedTrail, left) : _focus;
    }

    /// <summary>
    /// Through a rebase's ease (see <see cref="Rebase"/>), the frames just worked out for the new
    /// body turned part of the way back to those carried across, the ease <paramref name="realDt"/> on:
    /// all of the way at the first frame after, none of it after <see cref="Scene.CameraBodyEaseSeconds"/>.
    /// </summary>
    private void Ease(float realDt)
    {
        if (_easePending)
        {
            _easeTurn = Between((_up, _east, _south), _carried);
            _easePoleTurn = Between((_poleUp, _poleEast, _poleSouth), _carriedPole);
            _easeCentre = _carriedCentre - _zoomCentre;
            (_easePending, _ease) = (false, 0f);
        }
        if (_ease >= 1f)
            return;
        var left = EaseLeft;
        var turn = Quaternion.Slerp(Quaternion.Identity, _easeTurn, left);
        var poleTurn = Quaternion.Slerp(Quaternion.Identity, _easePoleTurn, left);
        (_up, _east, _south) = (Vector3.Transform(_up, turn), Vector3.Transform(_east, turn), Vector3.Transform(_south, turn));
        (_poleUp, _poleEast, _poleSouth) = (Vector3.Transform(_poleUp, poleTurn), Vector3.Transform(_poleEast, poleTurn), Vector3.Transform(_poleSouth, poleTurn));
        _zoomCentre += _easeCentre * left;
        _ease = MathF.Min(1f, _ease + realDt / Scene.CameraBodyEaseSeconds);
    }

    /// <summary>The turn that takes the frame <paramref name="from"/> (up, east and south) onto <paramref name="to"/>.</summary>
    private static Quaternion Between((Vector3 Up, Vector3 East, Vector3 South) from, (Vector3 Up, Vector3 East, Vector3 South) to)
    {
        // Each frame's axes as a matrix's rows: a vector's place in the one, put back by the other.
        static Matrix4x4 Rows((Vector3 Up, Vector3 East, Vector3 South) f) => new(
            f.East.X, f.East.Y, f.East.Z, 0f,
            f.Up.X, f.Up.Y, f.Up.Z, 0f,
            f.South.X, f.South.Y, f.South.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(Rows(from)) * Rows(to)));
    }

    /// <summary>
    /// Applies the middle drag gathered: the first turns the view away from the vessel from where
    /// it looks now (the orbit's own direction, reversed), each after it turns it further. Not
    /// while zoomed out, where there is nothing to look round from.
    /// </summary>
    private void TurnLook()
    {
        var turn = _pendingLook;
        _pendingLook = Vector2.Zero;
        if (turn == Vector2.Zero || _zoomedOut)
            return;
        if (!_free)
            (_free, _lookYaw, _lookPitch) = (true, _yaw + MathF.PI, -_pitch);
        // As looking round in person: drag right to look right, up to look up.
        _lookYaw -= turn.X * Scene.CameraRadiansPerDragPixel;
        _lookPitch = Math.Clamp(_lookPitch - turn.Y * Scene.CameraRadiansPerDragPixel, -FreeLookMaxPitch, FreeLookMaxPitch);
    }

    /// <summary>
    /// <paramref name="position"/>, lifted if need be to keep <see cref="Scene.CameraGroundClearance"/>
    /// from <paramref name="body"/>'s ground (or a pad) and its waves: it looks up at the rocket from just above them
    /// instead. Not only from the ground straight under it: on a steep mountainside the slope rising
    /// beside it is much nearer than that, near enough for the near plane to cut into the hill, so
    /// the ground is sampled in rings round it too, and it rises until each sample is that far off.
    /// Over the sea it keeps clear of the highest its waves reach there (half their size, crest to
    /// trough), whatever the moment, as the camera keeps no clock.
    /// </summary>
    private static Vector3 KeepClear(GroundBody body, Vector3 position)
    {
        var up = body.Up(position);
        var (east, north) = (body.East(position), body.North(position));
        var crest = body.HasSea ? Sea.WaveScale(up) * 0.5f : 0f;
        const float c = Scene.CameraGroundClearance;
        var lift = c + crest - body.SurfaceAltitude(position);
        for (var ring = 1; ring <= 3; ring++)
        {
            // A sample d across from it, its height h over what is under it: the camera must be
            // sqrt(c² - d²) above that ground, so c from it.
            var d = c * ring / 3f;
            var over = MathF.Sqrt(c * c - d * d);
            for (var i = 0; i < 8; i++)
            {
                var (sin, cos) = MathF.SinCos(i * MathF.Tau / 8f);
                var h = body.SurfaceAltitude(position + (east * cos + north * sin) * d);
                lift = MathF.Max(lift, over + crest - h);
            }
        }
        return lift > 0f ? position + up * lift : position;
    }
}
