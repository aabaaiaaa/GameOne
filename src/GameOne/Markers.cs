using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace GameOne;

/// <summary>
/// Markers over the view for things in the distance, each with its name and how far it is from the
/// vessel being flown: the bodies (the planet and the moon), the other vessels in the world, and the
/// launch pad and landing zones. Clicking one makes it the flown vessel's target (clicking it again,
/// or anywhere else, lets it go).
/// </summary>
/// <remarks>
/// <para>A marker shows only while its object is too small on the screen to see for itself, fading
/// out as it grows; one behind the planet is hidden. Off the screen, only the target, planets and
/// moons, and the mission's landing site and points of interest get an arrow at the edge pointing the
/// way to them.</para>
/// <para>Each kind has a shape (planets and moons a ring, craft a diamond, filled for this flight's,
/// debris a dot, places a square), and some come first: the target, the mission's landing site,
/// planets and moons, then craft (this flight's, then the biggest), then places, then debris. The
/// rest are quieter (smaller, fainter, labelled only when the pointer is on them or they are near),
/// and give way where they would overlap one before them.</para>
/// <para>Which kinds show is the player's <see cref="Settings"/>; the target, the mission's landing
/// site and its points of interest (rank 5) always do, unless markers are off altogether.</para>
/// <para>The vessel coming at the flown one is marked apart, in red (amber for a near miss), by none of these rules (see
/// <see cref="DrawComing"/>).</para>
/// </remarks>
internal sealed class Markers
{
    private const float Shape = 7f;                     // pixels from a marker's middle to its edge
    private const float SeenFrom = 8f, SeenBy = 20f;    // fading out as its object grows from this radius on screen to this
    private const float NearBy = Scene.NearByDistance;  // metres: a quiet marker this close is labelled anyway (the main view's near reach)
    private const float BodySeenFrom = 0.15f, BodySeenBy = 0.3f; // a body's ring fades from this radius to this, as a share of the screen's short side
    private const float EdgeMargin = 40f;

    internal enum Look
    {
        Ring,
        Diamond,
        HollowDiamond,
        Dot,
        Square,
    }

    internal readonly record struct Candidate(
        Vector3 Position, float Radius, string Name, MarkerKind Kind, Look Look, NavTarget? Target,
        int Rank, float Size, bool Loud, bool Arrow, Rgb Color, GroundBody? Of = null, int Order = 0); // Of: the body a body's marker is on; Order: the sort's last tiebreak

    internal readonly record struct Rgb(byte R, byte G, byte B)
    {
        public ImColor8 At(float alpha) => new(R, G, B, (byte)(255 * Math.Clamp(alpha, 0f, 1f)));

        /// <summary>Halfway to white: the target's.</summary>
        public Rgb Brighter => new((byte)((R + 255) / 2), (byte)((G + 255) / 2), (byte)((B + 255) / 2));
    }

    private readonly List<Candidate> _candidates = [];
    private readonly List<(float2 Min, float2 Max)> _taken = [];
    // The markers drawn this frame, as a click finds them (MapLabels.Hit), and each one's candidate.
    private readonly List<MapLabels.Clickable> _clickable = [];
    private readonly List<Candidate> _clickableMarkers = [];
    private Candidate? _hovered;
    private MapLabels? _labels; // the labels the last Draw laid out, which a click is found on
    private readonly List<(string Name, Vector2 At)> _drawn = [];
    private NavTarget? _set;
    private bool _clear;
    private Vector2 _cursor, _pressAt;
    private bool _pressed;

    /// <summary>
    /// Follows the mouse from the window's own events, as the steering rings and the nodes do (see
    /// <see cref="NavigationControl"/>), not ImGui's idea of the pointer: that can fall back to
    /// reading the real cursor, which a test driver's posted messages don't move.
    /// </summary>
    /// <remarks>
    /// The hover stays where the pointer was last seen, even once it has left the window: a test
    /// driver's posted moves leave the real cursor off the window, so Windows reports a leave after
    /// every one of them, and ending the hover there would leave nothing lit in its screenshots. A
    /// marker left lit after the pointer has gone is harmless. A click is judged where the button
    /// went down, not where the pointer has got to by the frame.
    /// </remarks>
    public Markers(GlfwWindow window)
    {
        window.OnMouseButton += (_, button, action, _) =>
        {
            if (button == GlfwMouseButton.Left && action == GlfwButtonAction.Press)
                (_pressed, _pressAt) = (true, _cursor);
        };
        window.OnCursorPos += (_, pos) => _cursor = new Vector2((float)pos.X, (float)pos.Y);
    }

    /// <summary>
    /// The markers drawn on the screen this frame (not the arrows at its edge), each by its name and
    /// where its middle is, in pixels from the middle of the screen (right and down), the first drawn first.
    /// </summary>
    public IReadOnlyList<(string Name, Vector2 At)> Drawn => _drawn;

    /// <summary>
    /// The coming mark on the vessel coming at the flown one drawn this frame (see <see cref="DrawComing"/>):
    /// its name and where its middle is, in pixels from the middle of the screen, as <see cref="Drawn"/>; or none.
    /// </summary>
    public (string Name, Vector2 At)? ComingMark { get; private set; }

    /// <summary>
    /// The vessel coming at the flown one (<see cref="Flight.ComingAt"/>), if what is coming is a
    /// vessel: what the main view's coming mark and the compass's tick are on. The ground, the
    /// sea, a raft, the tower and the masts are named by the COLLISION line but never marked.
    /// </summary>
    internal static Vessel? ComingVessel(Flight flight) => flight.ComingAt(flight.Controlled)?.Other;

    /// <summary>
    /// Draws the coming mark on the vessel coming at the flown one (<see cref="ComingVessel"/>), at
    /// its centre of mass, with its name, the seconds to the collision and its distance. It is a
    /// warning, not a navigation marker, so none of the markers' rules hide it: drawn with markers
    /// off or its kind not shown, never fading as its vessel grows on the screen (it is wanted most
    /// as it comes close), never hidden behind the planet (it would be gone just as the vessel
    /// passes close over the horizon). Off the screen it is not drawn: the compass's tick gives its
    /// bearing. Drawn last by <see cref="Draw"/>, which leaves that vessel's own marker out, so it is
    /// over everything and the only mark on it; so it is clicked as that marker would be, and the
    /// pointer on it is on a marker (<paramref name="pointed"/>; <paramref name="pressedOn"/> its
    /// vessel, when the press was on it).
    /// </summary>
    private void DrawComing(ImDrawListPtr draw, Flight flight, ChaseCamera camera, Vector2 display, Vector2 pointer, Vector2? press, BodyFrames frames,
        out bool pointed, out NavTarget? pressedOn)
    {
        (ComingMark, pointed, pressedOn) = (null, false, null);
        if (flight.ComingAt(flight.Controlled) is not { Other: { } other } coming)
            return;
        var place = frames.InView(other.Body, other.CentreOfMass);
        var clip = Vector4.Transform(new Vector4(place, 1f), camera.ViewProjection(display));
        if (clip.W <= 1e-4f)
            return; // behind the camera
        var at = new Vector2(clip.X / clip.W + 1f, 1f - clip.Y / clip.W) * display * 0.5f;
        if (at.X < 0 || at.Y < 0 || at.X > display.X || at.Y > display.Y)
            return;
        var name = WorldLog.Named(other);
        var label = $"{name}  {coming.Seconds:0.0} s  {Distance(Vector3.Distance(camera.Focus, place))}";
        var color = Hud.ComingColor(coming);
        DrawShape(draw, F2(at), Shape, Look.Diamond, color, 1f);
        var text = ImGui.CalcTextSize(label);
        var labelMin = new float2(at.X + Shape + 6f, at.Y - text.Y / 2);
        Label(draw, labelMin, color, label, 1f);
        ComingMark = (name, at - display * 0.5f);
        pointed = On(pointer, at, Shape, labelMin, labelMin + text);
        if (press is { } p && On(p, at, Shape, labelMin, labelMin + text))
            pressedOn = NavTarget.OfVessel(other);
    }

    /// <summary>
    /// What a left press does to the target (see <see cref="Click"/>), by what it is on, the first
    /// of: the coming mark (<paramref name="coming"/>), a marker or its label
    /// (<paramref name="onMarker"/>, targeting <paramref name="marker"/> if it can be), a body (its
    /// path in the map or its disc, <paramref name="body"/>); else nothing, letting go of the
    /// target where <paramref name="emptyLetsGo"/>.
    /// </summary>
    internal static (NavTarget? Set, bool Clear) Pressed(NavTarget? coming, bool onMarker, NavTarget? marker, NavTarget? body, NavTarget? current, bool emptyLetsGo) =>
        coming is { } c ? Click(onMarker: true, c, current, emptyLetsGo: false)
        : onMarker ? Click(onMarker: true, marker, current, emptyLetsGo: false)
        : body is { } b ? Click(onMarker: true, b, current, emptyLetsGo: false)
        : Click(onMarker: false, null, current, emptyLetsGo);

    /// <summary>
    /// What a click does to the flown vessel's target (<paramref name="current"/>): on a marker
    /// (<paramref name="onMarker"/>) for <paramref name="clicked"/>, it is made the target, or let
    /// go if it already is (a marker for nothing to target, the body the flown vessel is at, does nothing); off them
    /// all, the target is let go if <paramref name="emptyLetsGo"/> (markers shown, and not a click on
    /// the steering rings).
    /// </summary>
    internal static (NavTarget? Set, bool Clear) Click(bool onMarker, NavTarget? clicked, NavTarget? current, bool emptyLetsGo) =>
        onMarker
            ? clicked is { } c ? current == c ? (null, true) : (c, false) : (null, false)
            : (null, emptyLetsGo && current is not null);

    /// <summary>Where the pointer was last seen (from the window's own events: see the constructor), in pixels from the screen's top left.</summary>
    public Vector2 Pointer => _cursor;

    /// <summary>Whether the pointer was over a marker last frame: a click there is for it, not the steering rings.</summary>
    public bool PointerOver { get; private set; }

    /// <summary>What was clicked since the last call: a new target, or none wanted. For the flight's input.</summary>
    public (NavTarget? Set, bool Clear) TakeClick()
    {
        var click = (_set, _clear);
        (_set, _clear) = (null, false);
        return click;
    }

    /// <summary>
    /// What <paramref name="click"/> is on, of the markers drawn this frame (a spread pile's labels
    /// too, as a click for a target finds them), as the camera's centre: a body, a vessel, a site
    /// or a point of interest. Null off them all.
    /// </summary>
    internal CameraCentre? At(Vector2 click)
    {
        if (_labels is not { } labels)
            return null;
        var (index, _) = labels.Hit(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_clickable), click);
        return index >= 0 ? CentreOf(_clickableMarkers[index]) : null;
    }

    /// <summary>What the camera is centred on to be on <paramref name="marker"/>'s object: its body, vessel or site, or a point of interest (a place, by its order in the mission).</summary>
    internal static CameraCentre CentreOf(in Candidate marker) =>
        marker.Of is { } body ? new CameraCentre(CentreKind.Body, body.Index)
        : marker.Target is { Kind: TargetKind.Vessel } vessel ? new CameraCentre(CentreKind.Vessel, vessel.Id)
        : marker.Target is { Kind: TargetKind.Site } site ? new CameraCentre(CentreKind.Site, site.Id)
        : new CameraCentre(CentreKind.Point, marker.Order);

    /// <summary>A frame with the HUD hidden: nothing drawn, nothing under the pointer, and a press let go.</summary>
    public void Hide()
    {
        _pressed = false;
        PointerOver = false;
        _hovered = null;
        _drawn.Clear();
    }

    /// <summary>
    /// Lays out and draws this frame's markers (with markers off, none; their labels into
    /// <paramref name="labels"/>, drawn with the path's), then the coming mark on the
    /// vessel coming at the flown one (whatever the settings: see <see cref="DrawComing"/>), and takes
    /// a click on one (or off them all, unless it was on the steering rings, <paramref name="ringsInUse"/>,
    /// or markers are off). Nothing is clickable while <paramref name="menuOpen"/>.
    /// </summary>
    public void Draw(ImDrawListPtr draw, Flight flight, ChaseCamera camera, Vector2 display, Settings settings, bool ringsInUse, bool menuOpen, BodyFrames frames, MapLabels labels, SystemMap? map = null)
    {
        var pressed = _pressed;
        _pressed = false;
        PointerOver = false;
        _hovered = null;
        _labels = labels;
        _clickable.Clear();
        _clickableMarkers.Clear();
        _drawn.Clear();
        if (display.X <= 0 || display.Y <= 0)
        {
            ComingMark = null;
            return;
        }
        Gather(flight, settings, frames, _candidates); // none with markers off: but the coming mark, below

        var view = camera.ViewProjection(display);
        var io = ImGui.GetIO();
        var (pointer, pressAt) = (_cursor, _pressAt);
        var from = DistancesFrom(flight, camera);
        _taken.Clear();
        foreach (var marker in _candidates)
        {
            // How big its object is on the screen: none once it can be seen for itself.
            var fade = Fade(marker, camera.Position, camera.Forward, display);
            if (fade < 0.02f)
                continue;

            // Off the screen: an arrow the way to it, for the few that get one, even if the planet
            // is in the way (it is still the way to turn, and it may be over the horizon).
            var clip = Vector4.Transform(new Vector4(marker.Position, 1f), view);
            var at = clip.W > 1e-4f ? new Vector2(clip.X / clip.W + 1f, 1f - clip.Y / clip.W) * display * 0.5f : Vector2.Zero;
            var distance = Vector3.Distance(from, marker.Position);
            var label = $"{marker.Name}  {Distance(LabelDistance(marker, from))}";
            var isTarget = marker.Target is { } t && flight.Target == t;
            var rgb = isTarget ? marker.Color.Brighter : marker.Color;
            if (clip.W <= 1e-4f || at.X < EdgeMargin || at.Y < EdgeMargin || at.X > display.X - EdgeMargin || at.Y > display.Y - EdgeMargin)
            {
                if (marker.Arrow)
                    EdgeArrow(draw, clip, display, rgb.At(1f), label);
                continue;
            }
            if (Through(marker, isTarget, frames, camera.Position) is not { } through)
                continue;
            fade *= through;

            // Giving way: one that would sit on one before it isn't drawn at all.
            var size = marker.Loud ? Shape : Shape * 0.75f;
            var (min, max) = Box(at, marker.Loud);
            if (GivesWay(marker, _taken, min, max))
                continue;
            _taken.Add((min, max));
            _drawn.Add((marker.Name, at - display * 0.5f));

            var text = ImGui.CalcTextSize(label);
            var labelMin = new float2(at.X + size + 6f, at.Y - text.Y / 2);
            var labelMax = labelMin + text;
            // Its label is pointed at and clicked where it was last drawn: spread with a pile, there
            // (what a click is on is chosen once all are laid out, below).
            var spreadAt = labels.SpreadAt(marker.Target, marker.Name);
            var (hitMin, hitMax) = spreadAt is { } spread ? (F2(spread.Min), F2(spread.Max)) : (labelMin, labelMax);
            _clickable.Add(new MapLabels.Clickable(at, size, new Vector2(hitMin.X, hitMin.Y), new Vector2(hitMax.X, hitMax.Y), spreadAt is not null));
            _clickableMarkers.Add(marker);
            var pointed = On(pointer, at, size, hitMin, hitMax);
            var labelled = (marker.Loud || pointed || distance < NearBy) && (pointed || !Overlaps(labelMin, labelMax));

            var alpha = fade * (marker.Loud || pointed ? 1f : 0.6f);
            var color = rgb.At(alpha);
            DrawShape(draw, F2(at), size, marker.Look, color, alpha);
            if (isTarget)
                draw.AddRect(F2(at - new Vector2(size + 5f)), F2(at + new Vector2(size + 5f)), color, 2f, default, 1.5f);
            if (labelled)
            {
                // Drawn with the path's labels, a body's over them (spread with a pile, clicked where it is drawn).
                labels.Add(label, labelMin, F2(at), (rgb.R, rgb.G, rgb.B), alpha, 200f / 255f * alpha, over: marker.Kind == MarkerKind.Body,
                    target: marker.Target, name: marker.Name);
                _taken.Add((labelMin, labelMax));
            }
        }

        // Last, over them all, and so first for a click.
        DrawComing(draw, flight, camera, display, pointer, pressed ? pressAt : null, frames, out var onComing, out var pressedComing);

        // What the pointer and the press are on (a spread pile's labels over anything under them:
        // a click there is on them, never off them all).
        var clickable = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_clickable);
        var (hoverIndex, hoverOnPile) = labels.Hit(clickable, pointer);
        if (hoverIndex >= 0)
            _hovered = _clickableMarkers[hoverIndex];
        var (pressIndex, pressOnPile) = pressed ? labels.Hit(clickable, pressAt) : (-1, false);
        Candidate? pressedOn = pressIndex >= 0 ? _clickableMarkers[pressIndex] : null;
        PointerOver = _hovered is not null || onComing || hoverOnPile;
        if (menuOpen || io.WantCaptureMouse || !pressed)
            return;
        // A press on none of them, nor on the nodes or the steering rings (ringsInUse), is on a body's
        // path in the map, or on a body's disc (whether its marker shows or not: one filling the
        // screen has faded its ring), if that is what it is on; never the flown vessel's own body.
        var elsewhere = pressedComing is null && pressedOn is null && !ringsInUse && !pressOnPile;
        var onLine = elsewhere && map?.BodyLineAt(pressAt, NodeEditor.PathPixels) is { } line ? NavTarget.OfBody(line) : (NavTarget?)null;
        var onDisc = elsewhere && onLine is null && Hud.BodyDisc(frames, camera.Position, camera.ViewProjection(display), display, pressAt) is { } disc
            && Bodies.All[disc.Id] != flight.Body
            ? NavTarget.OfBody(Bodies.All[disc.Id])
            : (NavTarget?)null;
        var (set, clear) = Pressed(pressedComing, pressedOn is not null, pressedOn?.Target, onLine ?? onDisc, flight.Target,
            emptyLetsGo: settings.Markers && !ringsInUse && !pressOnPile);
        if (set is not null)
            _set = set;
        _clear |= clear; // held until TakeClick, never unset by a later frame
    }

    /// <summary>
    /// How strongly <paramref name="marker"/> shows, for what may be in the way from
    /// <paramref name="camera"/>: in full if nothing is; behind a body, not at all (null), except
    /// those found from anywhere (the target, the mission's landing site), which show faintly
    /// through it. A body's marker, on its own centre, is hidden only by another body in front of
    /// it; a body has an arrow off the screen, but behind another body it is hidden as anything
    /// is, unless it is the target (<paramref name="isTarget"/>). (Internal, for the tests: the
    /// drawing needs a window.)
    /// </summary>
    internal static float? Through(in Candidate marker, bool isTarget, BodyFrames frames, Vector3 camera)
    {
        if (!frames.Behind(camera, marker.Position, marker.Of))
            return 1f;
        return marker.Arrow && (marker.Kind != MarkerKind.Body || isTarget) ? 0.45f : null;
    }

    /// <summary>Whether a point is on a marker's shape or its label (a static helper, so the loop
    /// makes no closure each frame).</summary>
    private static bool On(Vector2 p, Vector2 at, float size, float2 labelMin, float2 labelMax)
        => Vector2.Distance(p, at) < size + 5f
            || (p.X >= labelMin.X && p.X <= labelMax.X && p.Y >= labelMin.Y && p.Y <= labelMax.Y);

    /// <summary>
    /// Everything that could have a marker, into <paramref name="candidates"/>, most important
    /// first, each where it is in the main view's frame (<paramref name="frames"/>: a vessel at
    /// another body, the planet seen from the moon). Every body has one (by its name), clicked to
    /// target it as any other marker is, but for the body the flown vessel is at, which can't be
    /// a target. With markers off (<see cref="Settings.Markers"/>) there are none. (Internal, as the tests
    /// read what is gathered: the drawing needs a window.)
    /// </summary>
    internal static void Gather(Flight flight, Settings settings, BodyFrames frames, List<Candidate> candidates)
    {
        candidates.Clear();
        if (!settings.Markers)
            return;
        var target = flight.Target;
        var flown = flight.Controlled;
        var comingVessel = ComingVessel(flight); // marked by DrawComing instead: one mark on it, not two

        var bodies = Bodies.All;
        for (var i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];
            Add(new Candidate(frames.InView(body, body.Centre), body.Radius, body.Name, MarkerKind.Body, Look.Ring,
                body == flown.Body ? null : NavTarget.OfBody(body),
                Rank: 4, Size: body.Radius, Loud: true, Arrow: true, new Rgb(225, 232, 255), Of: body));
        }

        foreach (var vessel in flight.WorldVessels)
        {
            if (vessel == flown || vessel == comingVessel || vessel.State == FlightState.Crashed)
                continue;
            var craft = Flight.CanFly(vessel);
            var thisFlight = vessel.Flight == flown.Flight; // the flown vessel's flight's (Flight.Vessels)
            var kind = !craft ? MarkerKind.Debris : thisFlight ? MarkerKind.ThisFlight : MarkerKind.EarlierFlight;
            Add(new Candidate(frames.InView(vessel.Body, vessel.Position), MathF.Max(vessel.Height, 2f) / 2, vessel.Name, kind,
                !craft ? Look.Dot : thisFlight ? Look.Diamond : Look.HollowDiamond, NavTarget.OfVessel(vessel),
                Rank: craft ? 3 : 1, Size: vessel.Height + (thisFlight ? 1000f : 0f), Loud: thisFlight && craft, Arrow: false,
                !craft ? new Rgb(170, 170, 178) : new Rgb(120, 200, 255)));
        }

        // The mission's landing site, while the flown vessel has one to come down on, is marked as
        // the landing zone always was: yellow, and found from anywhere.
        var landing = flown.State is FlightState.Landed or FlightState.Floating or FlightState.Crashed ? null : flown.LandingSite;
        for (var i = 0; i < Sites.All.Length; i++)
        {
            var (name, centre, _, radius) = Sites.All[i];
            var mission = landing is { } site && Vector3.Distance(site, centre) < 1f;
            Add(new Candidate(frames.InView(GroundBody.Planet, Sites.Top(i)), radius, name, MarkerKind.Site, Look.Square, new NavTarget(TargetKind.Site, i), // (the sites are the planet's)
                Rank: mission ? 5 : 2, Size: radius, Loud: mission, Arrow: mission,
                mission ? new Rgb(242, 196, 15) : new Rgb(215, 215, 205)));
        }

        // The flight's own mission's points of interest: found from anywhere like its landing site, on
        // the ground of their body, with no size (so never faded as the vessel nears), each a target;
        // the mission's order is kept among them by the sort (Candidate.Order); their names are unique
        // in a mission, as a label spread is found by its name (Mission.PointsOfInterest).
        var points = flight.Mission.PointsOfInterest;
        for (var i = 0; i < points.Length; i++)
            Add(new Candidate(frames.InView(points[i].Body, points[i].Place), 0f, points[i].Name, MarkerKind.Site, Look.Square, NavTarget.OfPoint(i),
                Rank: 5, Size: 0f, Loud: true, Arrow: true, new Rgb(242, 196, 15), Order: i));

        // The target comes before everything, whatever its kind, and is found from anywhere.
        for (var i = 0; i < candidates.Count; i++)
            if (candidates[i].Target is { } t && t == target)
                candidates[i] = candidates[i] with { Rank = 6, Loud = true, Arrow = true };
        candidates.Sort((a, b) => a.Rank != b.Rank ? b.Rank.CompareTo(a.Rank) : a.Size != b.Size ? b.Size.CompareTo(a.Size) : a.Order.CompareTo(b.Order));

        void Add(in Candidate candidate)
        {
            var always = candidate.Rank == 5 || (candidate.Target is { } t && t == target);
            if (always || settings.Shows(candidate.Kind))
                candidates.Add(candidate);
        }
    }

    private bool Overlaps(float2 min, float2 max) => Overlaps(_taken, min, max);

    private static bool Overlaps(List<(float2 Min, float2 Max)> taken, float2 min, float2 max)
    {
        foreach (var (takenMin, takenMax) in taken)
            if (min.X < takenMax.X && max.X > takenMin.X && min.Y < takenMax.Y && max.Y > takenMin.Y)
                return true;
        return false;
    }

    /// <summary>
    /// How strongly <paramref name="marker"/> shows for how big its object is on the screen (1 in
    /// full, 0 not at all): it fades as the object grows to be seen for itself. A body's ring is the
    /// way to point at the body, so it stays through the sizes a body is small on the screen (the
    /// moon from its orbit) and goes only as the body fills a good part of it. (Internal, as the
    /// tests read it: the drawing needs a window.)
    /// </summary>
    internal static float Fade(in Candidate marker, Vector3 cameraPosition, Vector3 forward, Vector2 display)
    {
        var toIt = marker.Position - cameraPosition;
        var depth = Vector3.Dot(toIt, forward);
        var pixels = marker.Radius / ChaseCamera.MetresPerPixel(MathF.Max(MathF.Abs(depth), toIt.Length() * 0.1f), display);
        var (from, by) = marker.Kind == MarkerKind.Body
            ? (BodySeenFrom * MathF.Min(display.X, display.Y), BodySeenBy * MathF.Min(display.X, display.Y))
            : (SeenFrom, SeenBy);
        return 1f - SmoothStep(from, by, pixels);
    }

    /// <summary>The box a marker drawn at <paramref name="at"/> takes on the screen. (Internal for the tests.)</summary>
    internal static (float2 Min, float2 Max) Box(Vector2 at, bool loud)
    {
        var size = loud ? Shape : Shape * 0.75f;
        return (F2(at - new Vector2(size + 2f)), F2(at + new Vector2(size + 2f)));
    }

    /// <summary>
    /// Whether <paramref name="marker"/>, whose box is <paramref name="min"/> to <paramref name="max"/>,
    /// isn't drawn for sitting on one before it. A body's ring never gives way: a marker on the
    /// body (a landing spot on its ground) is not to take its place, and only labels are spread
    /// (<see cref="MapLabels"/>). (Internal for the tests.)
    /// </summary>
    internal static bool GivesWay(in Candidate marker, List<(float2 Min, float2 Max)> taken, float2 min, float2 max) =>
        marker.Kind != MarkerKind.Body && Overlaps(taken, min, max);

    private static void DrawShape(ImDrawListPtr draw, float2 at, float size, Look look, ImColor8 color, float alpha)
    {
        var outline = new ImColor8(0, 0, 0, (byte)(170 * alpha));
        switch (look)
        {
            case Look.Ring:
                draw.AddCircle(at, size + 1f, outline, 0, 3.5f);
                draw.AddCircle(at, size, color, 0, 2f);
                break;
            case Look.Diamond:
            case Look.HollowDiamond:
                var (top, right, bottom, left) = (at + new float2(0, -size), at + new float2(size, 0), at + new float2(0, size), at + new float2(-size, 0));
                if (look == Look.Diamond)
                    draw.AddQuadFilled(top, right, bottom, left, color);
                draw.AddQuad(top, right, bottom, left, look == Look.Diamond ? outline : color, look == Look.Diamond ? 1f : 2f);
                break;
            case Look.Dot:
                draw.AddCircleFilled(at, size * 0.45f, color, 0);
                break;
            case Look.Square:
                var half = new float2(size * 0.8f, size * 0.8f);
                draw.AddRect(at - half, at + half, outline, 0f, default, 3.5f);
                draw.AddRect(at - half, at + half, color, 0f, default, 2f);
                break;
        }
    }

    /// <summary>An arrow at the edge of the screen, the way to turn to see it, with its label.</summary>
    private static void EdgeArrow(ImDrawListPtr draw, Vector4 clip, Vector2 display, ImColor8 color, string label)
    {
        // Behind the camera, the projection flips it, so flip it back.
        var centre = display * 0.5f;
        var direction = new Vector2(clip.X, -clip.Y) * (clip.W < 0 ? -1f : 1f);
        if (direction.LengthSquared() < 1e-6f)
            direction = new Vector2(0, 1);
        direction = Vector2.Normalize(direction);
        var reach = MathF.Min(MathF.Abs((centre.X - EdgeMargin) / direction.X), MathF.Abs((centre.Y - EdgeMargin) / direction.Y));
        var tip = centre + direction * reach;
        var side = new Vector2(-direction.Y, direction.X);
        var back = tip - direction * 22f;
        draw.AddTriangleFilled(F2(tip), F2(back + side * 11f), F2(back - side * 11f), color);
        var width = ImGui.CalcTextSize(label).X;
        var textAt = back - direction * 30f - new Vector2(width / 2, 8f);
        Label(draw, new float2(Math.Clamp(textAt.X, 8f, display.X - width - 8f), Math.Clamp(textAt.Y, 8f, display.Y - 24f)), color, label, 1f);
    }

    /// <summary>Outlined in black, so it reads against bright sky, haze or the planet.</summary>
    private static void Label(ImDrawListPtr draw, float2 at, ImColor8 color, string text, float alpha)
    {
        var shadow = new ImColor8(0, 0, 0, (byte)(200 * alpha));
        foreach (var offset in (ReadOnlySpan<float2>)[new(-1, 0), new(1, 0), new(0, -1), new(0, 1)])
            draw.AddText(at + offset, shadow, text);
        draw.AddText(at, color, text);
    }

    /// <summary>
    /// Where the markers' distances are measured from, in the main view's frame: the flown vessel,
    /// as the camera follows it (its focus) while it is on it, and where it would follow it (<see
    /// cref="Views.Focus"/>) while the camera is on something else, so a body watched isn't
    /// labelled 0 m away.
    /// </summary>
    internal static Vector3 DistancesFrom(Flight flight, ChaseCamera camera) =>
        camera.Centre.Kind == CentreKind.Own ? camera.Focus : Views.Focus(flight.Controlled);

    /// <summary>
    /// How far a marker's label says it is from <paramref name="from"/> (the flown vessel: see
    /// <see cref="DistancesFrom"/>): to its place, or for a body's marker (on its centre) to its
    /// surface, over its datum (as the readout's height over a body is), never under 0.
    /// </summary>
    internal static float LabelDistance(in Candidate marker, Vector3 from)
    {
        var distance = Vector3.Distance(from, marker.Position);
        return marker.Of is { } body ? MathF.Max(distance - body.SeaRadius, 0f) : distance;
    }

    public static string Distance(float metres) => metres < 10000f ? $"{metres:0} m" : $"{metres / 1000f:0.0} km";

    private static float2 F2(Vector2 v) => new(v.X, v.Y);

    private static float SmoothStep(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
