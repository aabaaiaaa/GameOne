using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace GameOne;

/// <summary>
/// What the map draws of the system, zoomed out (by <see cref="Paths"/>, under the flown vessel's
/// own path): the sun's mark, each body's path round what it goes round, each body (and the sun) no
/// smaller than <see cref="Scene.MapBodyMinPixels"/> across, and the path of every vessel flying but
/// the flown one and a vessel targeted (whose paths <see cref="Paths"/> draws, patched).
/// </summary>
/// <remarks>
/// <para>Built in three steps a frame: <see cref="Build"/> (in the main view's frame: the bodies'
/// paths, each vessel's path kept or made again), <see cref="Lay"/> (on the screen: what is hidden,
/// too small or off it left out, the rest as runs of screen points) and <see cref="Draw"/> (each
/// run one polyline).</para>
/// <para>Its cost is held down for a world of <see cref="Flight.Capacity"/> vessels:</para>
/// <list type="bullet">
/// <item>Each vessel under formula has a lap: the orbit it is on round its own body, among that
/// body's stars as they are at world time 0, a lap on (or out to where it leaves its body's pull,
/// to the first child's sphere it would enter, or down to its body's lowest surface: only its leg
/// round its own body, and only what is above the ground is drawn), in as many points as keep
/// each piece within a circle's sag of the curve, its apoapsis's pieces too (see
/// <see cref="Orbit.Samples"/>). It is
/// made again only as its orbit changes (its body, or its orbit's shape and energy). Each frame,
/// every body's laps are placed in the view by one transform for the body (every path is drawn
/// among the stars, as the flown one's is: <see cref="LegPlace"/>).</item>
/// <item>A vessel in the physics (the witnessed, and what is by them) has its path patched at the
/// spheres of influence each frame, as the flown one's is: they are few.</item>
/// <item>A line is left out whole where it is under <see cref="Scene.MapLineMinPixels"/> across or
/// wholly off the screen; it is drawn in as many points as its size on the screen needs (up to
/// <see cref="LapSegments"/>); and only the bodies (and the sun) that could hide part of it are
/// asked of each of its points.</item>
/// </list>
/// <para>Kept from frame to frame, so nothing is allocated a frame once its lists have grown to the
/// world's size.</para>
/// </remarks>
internal sealed class SystemMap
{
    /// <summary>The pieces a body's path round what it goes round is drawn in (a lap).</summary>
    public const int BodyPathSegments = 256;

    /// <summary>The most pieces a vessel's lap is drawn in (fewer as it is smaller on the screen).</summary>
    public const int LapSegments = 48;

    /// <summary>One vessel's path, as the map keeps it.</summary>
    private sealed class Lap
    {
        public Vessel? Vessel;
        public GroundBody Body = GroundBody.Planet;
        public bool Patched, Shown;
        // What its orbit was made from, among its body's stars at world time 0: its angular
        // momentum, its eccentricity vector and its energy, each a unit's worth.
        public Vector3Double Momentum, Eccentricity;
        public double Energy;
        // Under formula: its points among its body's stars at world time 0 (its ground frame
        // then), and the furthest of them from its body's centre.
        public readonly Vector3[] Stars = new Vector3[LapSegments + 1];
        public readonly float[] Seconds = new float[LapSegments + 1]; // each point's time on from when it was made
        public int Count;
        public float Reach;
        // An open one (out of its body's pull, or cut at a sphere it will enter) is drawn from where
        // the vessel is now: from the world time it was made at, its points passed since left out.
        public bool Open;
        public double MadeAt;
        // Whether it ends at the solar system's edge (its last point there, marked), under formula or patched.
        public bool LeavesSystem;
        // In the physics: its path patched, placed in the view's frame this frame, and where in it
        // each leg after the first starts (the line breaks there, at the crossing).
        public readonly List<Vector3> Placed = [];
        public readonly List<int> Starts = [];
    }

    private Lap?[] _laps = new Lap?[Flight.Capacity];
    private readonly Vector3[][] _bodyPaths;
    private readonly float[] _bodyAlpha;
    private readonly Matrix4x4[] _starsToView; // each body's stars at world time 0 into the view's frame now
    private readonly Quaternion[] _toStarsAtZero; // each body's ground frame now back to its stars at world time 0 (its ground frame then)
    private readonly List<PathLeg> _legs = new(Scene.PatchedLegs);
    private readonly List<(Vector3 At, double Seconds)> _samples = [];
    private readonly List<Vector3> _radial = new(3); // a straight up and down line's points (Orbit.Path), room for its three at most made at once: the first such lap allocates nothing
    private double _now;
    private GroundBody _flown = GroundBody.Planet;
    private BodyFrames _frames = null!;

    // What Lay put on the screen: every run's points, one after another, and the runs; the dots
    // standing for bodies too small to see; the sun's mark.
    private readonly List<float2> _screen = [];
    private readonly List<Run> _runs = [];
    private readonly List<(float2 At, (byte R, byte G, byte B) Color, int Body)> _dots = [];
    private readonly List<float2> _edges = [];

    /// <summary>Where on the screen <see cref="Lay"/> put the end of each vessel's path that ends at the solar system's edge (<see cref="Paths.EdgeIcon"/>).</summary>
    internal IReadOnlyList<float2> Edges => _edges;

    /// <summary>A run of <see cref="Count"/> screen points from <see cref="First"/>, drawn as one line; <see cref="Vessel"/> is its vessel's id, -1 for a body's path (<see cref="Body"/>).</summary>
    internal readonly record struct Run(int First, int Count, (byte R, byte G, byte B) Color, float Alpha, int Vessel, int Body);

    public SystemMap()
    {
        var count = Bodies.All.Count;
        _bodyPaths = new Vector3[count][];
        for (var i = 0; i < count; i++)
            _bodyPaths[i] = new Vector3[BodyPathSegments + 1];
        _bodyAlpha = new float[count];
        _starsToView = new Matrix4x4[count];
        _toStarsAtZero = new Quaternion[count];
    }

    /// <summary>How many times a vessel's lap has been made (or made again, its orbit changed) since this was made. For the tests.</summary>
    internal int Remakes { get; private set; }

    /// <summary>Where the sun's centre is in the main view's frame, as last built.</summary>
    internal Vector3 SunAt { get; private set; }

    /// <summary>Each body's path as last built (by <see cref="GroundBody.Index"/>), in the main view's frame.</summary>
    internal ReadOnlySpan<Vector3> BodyPath(int body) => _bodyPaths[body];

    /// <summary>How strongly each body's path is drawn, as last built: faded in as the camera pulls back from the body (<see cref="Scene.MapBodyPathFadeFrom"/>).</summary>
    internal float BodyPathAlpha(int body) => _bodyAlpha[body];

    /// <summary>The runs <see cref="Lay"/> put on the screen.</summary>
    internal IReadOnlyList<Run> Runs => _runs;

    /// <summary>The screen points of <see cref="Runs"/>.</summary>
    internal IReadOnlyList<float2> Screen => _screen;

    /// <summary>The dots <see cref="Lay"/> drew for bodies too small to see (by <see cref="GroundBody.Index"/>; -1 the sun).</summary>
    internal IReadOnlyList<(float2 At, (byte R, byte G, byte B) Color, int Body)> Dots => _dots;

    /// <summary>The sun's mark as <see cref="Lay"/> put it on the screen: its place and its ring's radius; null where it is hidden or behind the camera.</summary>
    internal (float2 At, float Ring)? SunMark { get; private set; }

    /// <summary>
    /// Works out the system for <paramref name="flight"/> in its main view's frame (its body's), the
    /// bodies as <paramref name="frames"/> has them this frame, seen from <paramref name="camera"/>:
    /// the sun's place; each body's path round what it goes round (<see cref="Bodies.Path"/>), faded
    /// in as the camera pulls back from the body (by the path's size, so the planet's, a straight
    /// line through it close by, shows only once its curve can be seen; the moon's from the closest
    /// zoom); each body's stars at world time 0 placed in the view; and every vessel's path, kept or
    /// made again.
    /// </summary>
    public void Build(Flight flight, BodyFrames frames, Vector3 camera)
    {
        _frames = frames;
        _flown = flight.Body;
        _now = flight.WorldTime;
        var view = flight.Body;
        var now = flight.WorldTime;
        SunAt = frames.SunInView.ToSingle();

        var bodies = Bodies.All;
        for (var b = 0; b < bodies.Count; b++)
        {
            var body = bodies[b];
            var centre = frames.InView(body, body.Centre);
            var radius = body.Parent is { } parent ? Vector3.Distance(centre, frames.InView(parent, parent.Centre)) : Scene.SunDistance;
            _bodyAlpha[b] = Scene.MapBodyPathAlpha * SmoothStep(Scene.MapBodyPathFadeFrom * radius, Scene.MapBodyPathFadeTo * radius, Vector3.Distance(camera, centre));
            if (_bodyAlpha[b] > 0f)
                Bodies.Path(body, view, now, _bodyPaths[b]);
            _starsToView[b] = StarsToView(body, frames.ToView(body), now);
            _toStarsAtZero[b] = Quaternion.CreateFromAxisAngle(body.NorthPole, body.TurnAngle(now));
        }

        var targeted = flight.Target is { Kind: TargetKind.Vessel } target ? target.Id : -1;
        var world = flight.WorldVessels;
        if (_laps.Length < world.Count)
            Array.Resize(ref _laps, world.Count); // (a world past its capacity for a moment, staging: rare)
        for (var i = 0; i < world.Count; i++)
        {
            var v = world[i];
            var lap = _laps[i] ??= new Lap();
            lap.Shown = v != flight.Controlled && v.Id != targeted && v.State == FlightState.Flying;
            if (!lap.Shown)
                continue;
            if (v.Physics is not null)
            {
                (lap.Vessel, lap.Body, lap.Patched) = (v, v.Body, true);
                PlacePatched(lap, v, view, now);
            }
            else if (lap.Patched || lap.Vessel != v || lap.Body != v.Body || Changed(lap, v))
                MakeLap(lap, v, now);
        }
        for (var i = world.Count; i < _laps.Length && _laps[i] is { } spare; i++)
            spare.Shown = false;
    }

    /// <summary>
    /// The transform from <paramref name="body"/>'s ground frame at world time 0 (its stars then) into
    /// the view's frame at <paramref name="now"/> (by <paramref name="toView"/>, its change into the
    /// view this frame): turned on by its turn since, then changed across. Worked out in double, as
    /// the map of four points it is, and made a float only as a whole.
    /// </summary>
    private static Matrix4x4 StarsToView(GroundBody body, in FrameChange toView, double now)
    {
        var turn = QuaternionDouble.FromAxisAngle(body.NorthPole, body.TurnRadians(-now));
        var centre = (Vector3Double)body.Centre;
        var change = toView;
        Vector3Double At(Vector3Double p) => change.Point(centre + turn.Rotate(p - centre));
        var origin = At(default);
        var (x, y, z) = (At(new Vector3Double(1, 0, 0)) - origin, At(new Vector3Double(0, 1, 0)) - origin, At(new Vector3Double(0, 0, 1)) - origin);
        return new Matrix4x4(
            (float)x.X, (float)x.Y, (float)x.Z, 0f,
            (float)y.X, (float)y.Y, (float)y.Z, 0f,
            (float)z.X, (float)z.Y, (float)z.Z, 0f,
            (float)origin.X, (float)origin.Y, (float)origin.Z, 1f);
    }

    /// <summary>
    /// <paramref name="v"/>'s orbit's shape and energy now, among its body's stars at world time 0:
    /// its angular momentum, its eccentricity vector and its energy.
    /// </summary>
    private static (Vector3Double Momentum, Vector3Double Eccentricity, double Energy) Elements(Vessel v, Quaternion toStarsAtZero)
    {
        var body = v.Body;
        var r = (Vector3Double)Vector3.Transform(v.CentreOfMass - body.Centre, toStarsAtZero);
        var s = (Vector3Double)Vector3.Transform(body.AmongStars(v.CentreOfMass, v.Velocity), toStarsAtZero);
        double mu = body.Mu;
        var h = Vector3Double.Cross(r, s);
        var distance = r.Length();
        return (h, Vector3Double.Cross(s, h) / mu - r / distance, s.LengthSquared() / 2 - mu / distance);
    }

    /// <summary>
    /// Whether <paramref name="v"/>'s orbit has changed since <paramref name="lap"/> was made: its
    /// shape or its energy moved by more than a part in ten thousand (a burn, the air's drag), as
    /// a float's rounding of its place and velocity never does.
    /// </summary>
    private bool Changed(Lap lap, Vessel v)
    {
        var (h, e, energy) = Elements(v, _toStarsAtZero[v.Body.Index]);
        const double Share = 1e-4;
        var scale = Math.Sqrt(v.Body.Mu * v.Body.SeaRadius); // an angular momentum a low orbit's size
        return (h - lap.Momentum).Length() > Share * Math.Max(lap.Momentum.Length(), scale)
            || (e - lap.Eccentricity).Length() > Share
            || Math.Abs(energy - lap.Energy) > Share * Math.Max(Math.Abs(lap.Energy), v.Body.Mu / v.Body.SeaRadius * 1e-2);
    }

    /// <summary>
    /// Makes <paramref name="lap"/> for <paramref name="v"/> (under formula) as its orbit is now: a lap
    /// round its body from where it is, or out to where it leaves its body's pull (its sphere's leaving
    /// edge, or <see cref="Orbit.MaxRange"/> round the root), or to where it would first enter a
    /// child's sphere (looked for only where its farthest point reaches that far out), or to the
    /// solar system's edge (<see cref="Patched.LeavesAt"/>), or to where it comes down to its
    /// body's lowest surface (<see cref="GroundBody.LowestRadius"/>), whichever is first: only its leg
    /// round its own body. Straight up and down, its line. Among its body's stars at world time 0.
    /// </summary>
    private void MakeLap(Lap lap, Vessel v, double now)
    {
        var body = v.Body;
        (lap.Vessel, lap.Body, lap.Patched) = (v, body, false);
        (lap.Momentum, lap.Eccentricity, lap.Energy) = Elements(v, _toStarsAtZero[body.Index]);
        Remakes++;
        (lap.Count, lap.Reach, lap.Open, lap.MadeAt, lap.LeavesSystem) = (0, 0f, false, now, false);
        var orbit = Orbit.OverGround(body, v.CentreOfMass, v.Velocity, findEnd: false);
        // Where it reaches the solar system's edge, if it can get that far (looked for only then:
        // along its whole path ahead, which only an orbit made to find its end has). Its points are
        // placed among the stars at world time 0, not at the edge's own time, so its far end lies
        // about 0.04% off the edge, which doesn't show at the map's scale.
        var leaves = orbit.HighestDistance >= Scene.SolarSystemEdge - SolarSystem.FarthestFromSun(body, now)
            ? Patched.LeavesAt(new PathLeg(body, Orbit.OverGround(body, v.CentreOfMass, v.Velocity), now, double.PositiveInfinity))
            : null;
        if (orbit.Radial)
        {
            orbit.Path(_radial);
            if (leaves is { } edgeAt)
            {
                // Out along its line to the edge, where it ends.
                (lap.Open, lap.LeavesSystem) = (true, true);
                Add(lap, body, _radial[0], 0f);
                Add(lap, body, orbit.PlaceAfter(edgeAt - now).ToSingle(), (float)(edgeAt - now));
                return;
            }
            foreach (var p in _radial)
                Add(lap, body, p, 0f);
            return;
        }
        var reach = body.Parent is null ? Orbit.MaxRange : body.SphereRadius * Scene.SphereLeaveFactor;
        var from = orbit.Now;
        var to = from + Math.Tau;
        if (orbit.Eccentricity >= 1 || orbit.ApoapsisDistance > reach)
        {
            var cos = (orbit.SemiLatusRectum / reach - 1) / orbit.Eccentricity;
            if (cos >= 1)
                return; // already out past it
            to = Math.Acos(Math.Max(cos, -1));
            while (to < from)
                to += Math.Tau;
            lap.Open = true;
        }
        // (The crossing is looked for along its whole path ahead, which only an orbit made to find its end has.)
        if (ReachesAChild(body, orbit, now)
            && Patched.Crossing(new PathLeg(body, Orbit.OverGround(body, v.CentreOfMass, v.Velocity), now, double.PositiveInfinity)) is { } crossing)
        {
            to = Math.Min(to, Unwrapped(orbit.AnomalyAfter(crossing - now), from));
            lap.Open = true;
        }
        if (leaves is { } edge && Unwrapped(orbit.AnomalyAfter(edge - now), from) is var atEdge && atEdge < to)
            (to, lap.Open, lap.LeavesSystem) = (atEdge, true, true);
        // Down to its body's lowest surface, where every path ends at the latest (its sea, or with
        // none its lowest ground; the terrain isn't sampled for a lap): a stage falling back is
        // drawn from where it is to where it comes down, not on round under the ground and back up
        // the way it came, a second strand beside the first.
        if (DownAt(orbit, body.LowestRadius, from) is { } down && down < to)
            (to, lap.Open) = (down, true);
        // Even in its spacing (see Orbit.Samples), so its pieces bow out from it no more at a
        // narrow ellipse's apoapsis than at its periapsis.
        var (first, last) = (orbit.Spacing(from), orbit.Spacing(to));
        for (var k = 0; k <= LapSegments; k++)
        {
            var anomaly = k == 0 ? from : k == LapSegments ? to : orbit.AnomalyAtSpacing(first + (last - first) * k / LapSegments);
            Add(lap, body, orbit.At(anomaly), (float)orbit.TimeTo(anomaly));
        }
    }

    /// <summary>
    /// The true anomaly (just after <paramref name="from"/>, within a lap) where <paramref name="orbit"/>
    /// comes down through <paramref name="radius"/> from its body's centre, if its periapsis is under it
    /// and it is above it now.
    /// </summary>
    private static double? DownAt(in Orbit orbit, double radius, double from)
    {
        if (orbit.PeriapsisDistance >= radius || orbit.Eccentricity <= 0)
            return null;
        var cos = (orbit.SemiLatusRectum / radius - 1) / orbit.Eccentricity;
        if (cos >= 1 || orbit.SemiLatusRectum / (1 + orbit.Eccentricity * Math.Cos(from)) < radius)
            return null; // never up to it, or under it already
        var down = Math.Tau - Math.Acos(Math.Max(cos, -1)); // on the way in to the periapsis
        return Unwrapped(down, from);
    }

    /// <summary>An anomaly <paramref name="anomaly"/> given as it comes (to a turn), moved by whole turns to just after <paramref name="from"/>.</summary>
    private static double Unwrapped(double anomaly, double from)
    {
        while (anomaly < from)
            anomaly += Math.Tau;
        while (anomaly > from + Math.Tau)
            anomaly -= Math.Tau;
        return anomaly;
    }

    /// <summary>
    /// Whether <paramref name="orbit"/>'s farthest point reaches out to any child of <paramref name="body"/>'s
    /// sphere of influence at its nearest to the body (its orbit's periapsis less its sphere's
    /// radius), so it may cross into it at some time.
    /// </summary>
    private static bool ReachesAChild(GroundBody body, in Orbit orbit, double now)
    {
        var all = Bodies.All;
        for (var i = 0; i < all.Count; i++)
            if (all[i].Parent == body && orbit.ApoapsisDistance >= Bodies.OrbitOf(all[i], now).PeriapsisDistance - all[i].SphereRadius)
                return true;
        return false;
    }

    /// <summary>Adds a point of <paramref name="body"/>'s frame now to <paramref name="lap"/>, among its stars at world time 0.</summary>
    private void Add(Lap lap, GroundBody body, Vector3 p, float seconds)
    {
        var fromCentre = p - body.Centre;
        lap.Seconds[lap.Count] = seconds;
        lap.Stars[lap.Count++] = body.Centre + Vector3.Transform(fromCentre, _toStarsAtZero[body.Index]);
        lap.Reach = MathF.Max(lap.Reach, fromCentre.Length());
    }

    /// <summary>
    /// <paramref name="v"/>'s path (in the physics) patched at the spheres of influence from where it
    /// is now, each leg placed in the view's frame as the flown path's are
    /// (<see cref="LegPlace"/>: round its own body as it is now), each after the first where the
    /// line breaks.
    /// </summary>
    private void PlacePatched(Lap lap, Vessel v, GroundBody view, double now)
    {
        lap.Placed.Clear();
        lap.Starts.Clear();
        lap.Reach = 0f;
        var body = v.Body;
        Patched.Legs(body, v.CentreOfMass, body.AmongStars(v.CentreOfMass, v.Velocity), now, _legs);
        for (var i = 0; i < _legs.Count; i++)
        {
            var leg = _legs[i];
            var place = new LegPlace(leg, view, now);
            if (i > 0)
                lap.Starts.Add(lap.Placed.Count);
            if (leg.Orbit.Radial)
            {
                leg.Orbit.Path(_radial);
                if (leg.LeavesSystem)
                {
                    // Out along its line to the solar system's edge, where it ends.
                    lap.Placed.Add(place.Point(_radial[0]));
                    lap.Placed.Add(place.Point(leg.Orbit.PlaceAfter(leg.End - leg.Start).ToSingle()));
                    continue;
                }
                foreach (var p in _radial)
                    lap.Placed.Add(place.Point(p));
                continue;
            }
            leg.Orbit.Samples(_samples, 0, leg.End - leg.Start, LapSegments);
            foreach (var (at, _) in _samples)
                lap.Placed.Add(place.Point(at));
        }
        lap.LeavesSystem = _legs[^1].LeavesSystem;
    }

    /// <summary>
    /// Puts the system on the screen as seen through <paramref name="viewProjection"/> from
    /// <paramref name="camera"/> (both in the main view's frame), <paramref name="display"/> pixels
    /// across: each body's path and every vessel's, as runs of the points not hidden by a body or
    /// the sun; the bodies' dots; the sun's mark. A vessel's lap under
    /// <see cref="Scene.MapLineMinPixels"/> across or wholly off the screen is left out unasked.
    /// </summary>
    public void Lay(in Matrix4x4 viewProjection, Vector3 camera, Vector2 display)
    {
        _display = display;
        _screen.Clear();
        _runs.Clear();
        _dots.Clear();
        _edges.Clear();
        SunMark = null;
        var pathColor = ((byte)(Scene.PathColor.X * 255), (byte)(Scene.PathColor.Y * 255), (byte)(Scene.PathColor.Z * 255));
        var bodies = Bodies.All;
        for (var b = 0; b < bodies.Count; b++)
            if (_bodyAlpha[b] > 0f)
                LayLine(_bodyPaths[b], viewProjection, camera, display, Scene.MapBodyColor, _bodyAlpha[b], -1, b);

        for (var i = 0; i < _laps.Length && _laps[i] is { } lap; i++)
        {
            if (!lap.Shown)
                continue;
            if (lap.Patched)
                LayLine(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lap.Placed), viewProjection, camera, display, pathColor, Scene.MapOtherPathAlpha, lap.Vessel!.Id, -1,
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lap.Starts));
            else if (lap.Count > 1)
                LayLap(lap, viewProjection, camera, display, pathColor);
            // Its end at the solar system's edge, where it can be seen.
            if (lap.LeavesSystem && (lap.Patched ? lap.Placed.Count > 0 : lap.Count > 0))
            {
                var end = lap.Patched ? lap.Placed[^1] : Vector3.Transform(lap.Stars[lap.Count - 1], _starsToView[lap.Body.Index]);
                if (!_frames.Behind(camera, end) && Project(end, viewProjection, display, out var at))
                    _edges.Add(at);
            }
        }

        for (var b = 0; b < bodies.Count; b++)
        {
            var body = bodies[b];
            var centre = _frames.InView(body, body.Centre);
            if (!_frames.Behind(camera, centre, body))
                Dot(centre, body.Radius, viewProjection, camera, display, Scene.MapBodyColor, b);
        }
        if (!_frames.BehindTheSun(camera, SunAt) && Project(SunAt, viewProjection, display, out var sun))
        {
            var across = Dot(SunAt, Scene.SunRadius, viewProjection, camera, display, Scene.MapSunColor, -1);
            SunMark = (sun, MathF.Max(across / 2f + 4f, 7f));
        }
    }

    /// <summary>
    /// A line of points in the view's frame (a body's path, a patched one), each hidden by any body
    /// or the sun, as runs; a patched one's run also ends at each of <paramref name="starts"/> (a
    /// leg's first point, after a crossing), so nothing joins the legs either side.
    /// </summary>
    private void LayLine(ReadOnlySpan<Vector3> points, in Matrix4x4 viewProjection, Vector3 camera, Vector2 display,
        (byte R, byte G, byte B) color, float alpha, int vessel, int body, ReadOnlySpan<int> starts = default)
    {
        var first = _screen.Count;
        var nextStart = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (nextStart < starts.Length && starts[nextStart] == i)
            {
                nextStart++;
                first = EndRun(first, color, alpha, vessel, body);
            }
            if (!_frames.Behind(camera, p) && Project(p, viewProjection, display, out var at))
                _screen.Add(at);
            else
                first = EndRun(first, color, alpha, vessel, body);
        }
        EndRun(first, color, alpha, vessel, body);
    }

    /// <summary>
    /// A vessel's lap under formula: left out whole if it is too small or off the screen; else its
    /// points (as many as its size there needs) placed by its body's transform this frame, each
    /// hidden only by the spheres (its body's, another's, the sun's) that could cover part of it.
    /// </summary>
    private void LayLap(Lap lap, in Matrix4x4 viewProjection, Vector3 camera, Vector2 display, (byte R, byte G, byte B) color)
    {
        var body = lap.Body;
        var centre = _frames.InView(body, body.Centre);
        var distance = Vector3.Distance(camera, centre);
        var pixels = distance > lap.Reach ? lap.Reach / ChaseCamera.MetresPerPixel(distance, display) : float.PositiveInfinity;
        // Too small to see, or no bigger than its body's dot.
        if (2f * pixels < MathF.Max(Scene.MapLineMinPixels, Scene.MapBodyMinPixels))
            return;
        // Wholly off the screen: its sphere's circle there, widened for the perspective (off the view's
        // axis by an angle, a sphere is drawn stretched away from the middle by up to one over its
        // cosine squared), so one coming onto the screen at its edge isn't left out.
        if (float.IsFinite(pixels))
        {
            var clip = Vector4.Transform(new Vector4(centre, 1f), viewProjection);
            var cosine = clip.W / distance;
            var reach = cosine > 0.2f ? pixels / (cosine * cosine) : float.PositiveInfinity;
            if (OnScreen(clip, display, out var middle)
                && (middle.X + reach < 0f || middle.X - reach > display.X || middle.Y + reach < 0f || middle.Y - reach > display.Y))
                return;
        }

        // The spheres that could hide part of it: those in the way of its sphere (round its body's
        // centre, out to its reach), by their angles from the camera and their distances.
        Span<Vector4> hiders = stackalloc Vector4[Bodies.All.Count + 1];
        Span<float> nearest = stackalloc float[Bodies.All.Count + 1]; // each one's nearest point's distance, squared
        var count = 0;
        var toLine = centre - camera;
        var lineAngle = distance > lap.Reach ? MathF.Asin(lap.Reach / distance) : MathF.PI;
        var bodies = Bodies.All;
        for (var b = 0; b <= bodies.Count; b++)
        {
            var (at, radius) = b < bodies.Count ? (_frames.InView(bodies[b], bodies[b].Centre), BodyFrames.HideRadius(bodies[b])) : (SunAt, Scene.SunRadius);
            var toHider = at - camera;
            var away = toHider.Length();
            if (away <= radius || away - radius > distance + lap.Reach)
                continue; // round the camera (never: the camera is clear of every body), or beyond all of it
            var apart = MathF.Atan2(Vector3.Cross(toHider, toLine).Length(), Vector3.Dot(toHider, toLine));
            if (apart < MathF.Asin(radius / away) + lineAngle)
                (hiders[count], nearest[count++]) = (new Vector4(toHider, radius), (away - radius) * (away - radius));
        }

        // As many pieces as keep each within MapLineSagPixels of the curve on the screen (a piece of a
        // circle r pixels round, of 1/n of it, bows out r pi^2 / 2n^2 from its chord).
        var segments = Math.Clamp((int)MathF.Ceiling(MathF.PI * MathF.Sqrt(pixels / (2f * Scene.MapLineSagPixels))), 8, LapSegments);
        var stride = Math.Max(1, LapSegments / segments);
        if (lap.Count < LapSegments + 1)
            stride = 1; // (a straight up and down line: its few points)
        // Its body's stars at world time 0 straight to the camera, and on to the screen.
        var fromCamera = _starsToView[body.Index];
        var toClip = fromCamera * viewProjection;
        fromCamera.Translation -= camera;
        // Off the screen, only the point each side of it is kept (so the line runs out to its edge),
        // and no body is asked whether it hides the rest.
        // An open one from where the vessel is now: its points passed since it was made left out.
        var start = StartOf(lap);
        var now = lap.Open ? NowAt(lap) : default;
        var first = _screen.Count;
        var (added, waiting) = (false, (float2?)null);
        for (var k = lap.Open ? start - 1 : 0; ; k = k < start ? start : Math.Min(k + stride, lap.Count - 1))
        {
            var star = k < start ? now : lap.Stars[k];
            var last = k == lap.Count - 1;
            if (!OnScreen(Vector4.Transform(new Vector4(star, 1f), toClip), display, out var at))
            {
                (first, added, waiting) = (EndRun(first, color, Scene.MapOtherPathAlpha, lap.Vessel!.Id, -1), false, null);
                if (last)
                    break;
                continue;
            }
            var off = at.X < 0f || at.X > display.X || at.Y < 0f || at.Y > display.Y;
            if (off && !added)
            {
                waiting = at;
                if (last)
                    break;
                continue;
            }
            var toPoint = Vector3.Transform(star, fromCamera);
            var hidden = false;
            for (var h = 0; h < count && !hidden; h++)
                hidden = Covers(toPoint, new Vector3(hiders[h].X, hiders[h].Y, hiders[h].Z), hiders[h].W, nearest[h]);
            if (hidden)
                (first, added, waiting) = (EndRun(first, color, Scene.MapOtherPathAlpha, lap.Vessel!.Id, -1), false, null);
            else if (!off)
            {
                if (waiting is { } from)
                    _screen.Add(from);
                _screen.Add(at);
                (added, waiting) = (true, null);
            }
            else
            {
                _screen.Add(at);
                (first, added, waiting) = (EndRun(first, color, Scene.MapOtherPathAlpha, lap.Vessel!.Id, -1), false, at);
            }
            if (last)
                break;
        }
        EndRun(first, color, Scene.MapOtherPathAlpha, lap.Vessel!.Id, -1);
    }

    /// <summary>The first of <paramref name="lap"/>'s points still ahead of its vessel: for an open one, those passed since it was made left out (a closed one is drawn whole).</summary>
    private int StartOf(Lap lap)
    {
        var start = 0;
        if (lap.Open)
            while (start < lap.Count - 1 && lap.Seconds[start] < _now - lap.MadeAt)
                start++;
        return start;
    }

    /// <summary>Where <paramref name="lap"/>'s vessel is now, among its body's stars at world time 0, as the lap's points are.</summary>
    private Vector3 NowAt(Lap lap) =>
        lap.Body.Centre + Vector3.Transform(lap.Vessel!.CentreOfMass - lap.Body.Centre, _toStarsAtZero[lap.Body.Index]);

    /// <summary>
    /// Whether a sphere of <paramref name="radius"/> round <paramref name="toCentre"/> hides a point
    /// at <paramref name="toPoint"/> (both from the camera), <paramref name="nearest"/> the square of
    /// the distance to the sphere's nearest point. Nearer than that, it can't (no root taken: most
    /// points); else by the ray's closest approach to the centre, from a cross product (a difference
    /// of products, not of squares of distances), so it keeps its metres in a float millions of
    /// metres out.
    /// </summary>
    internal static bool Covers(Vector3 toPoint, Vector3 toCentre, float radius, float nearest)
    {
        var squared = toPoint.LengthSquared();
        if (squared <= nearest)
            return false;
        var along = Vector3.Dot(toCentre, toPoint);
        if (along <= 0f)
            return false;
        var across = Vector3.Cross(toCentre, toPoint).LengthSquared(); // the closest approach squared, times the point's distance squared
        if (across >= radius * radius * squared)
            return false;
        var distance = MathF.Sqrt(squared);
        return distance > along / distance - MathF.Sqrt(radius * radius - across / squared);
    }

    /// <summary>Ends the run of screen points from <paramref name="first"/> (a line of two or more), and gives where the next starts.</summary>
    private int EndRun(int first, (byte R, byte G, byte B) color, float alpha, int vessel, int body)
    {
        var count = _screen.Count - first;
        if (count >= 2)
            _runs.Add(new Run(first, count, color, alpha, vessel, body));
        else if (count == 1)
            _screen.RemoveAt(_screen.Count - 1);
        return _screen.Count;
    }

    /// <summary>
    /// A body of <paramref name="radius"/> at <paramref name="centre"/> (in the view's frame), as a dot
    /// <see cref="Scene.MapBodyMinPixels"/> across where its own disc would be smaller; how many pixels
    /// across its disc is (0 if it is behind the camera).
    /// </summary>
    private float Dot(Vector3 centre, float radius, in Matrix4x4 viewProjection, Vector3 camera, Vector2 display, (byte R, byte G, byte B) color, int body)
    {
        if (!Project(centre, viewProjection, display, out var at))
            return 0f;
        var across = 2f * radius / ChaseCamera.MetresPerPixel(Vector3.Distance(camera, centre), display);
        if (across < Scene.MapBodyMinPixels)
            _dots.Add((at, color, body));
        return across;
    }

    /// <summary>Where <paramref name="point"/> (in the view's frame) is on the screen, if it is in front of the camera.</summary>
    private static bool Project(Vector3 point, in Matrix4x4 viewProjection, Vector2 display, out float2 at) =>
        OnScreen(Vector4.Transform(new Vector4(point, 1f), viewProjection), display, out at);

    /// <summary>Where a point at <paramref name="clip"/> in clip space is on the screen, if it is in front of the camera.</summary>
    private static bool OnScreen(Vector4 clip, Vector2 display, out float2 at)
    {
        at = default;
        if (clip.W <= 1e-4f)
            return false;
        at = new float2((clip.X / clip.W + 1f) * display.X * 0.5f, (1f - clip.Y / clip.W) * display.Y * 0.5f);
        return true;
    }

    /// <summary>
    /// Whether a click can be on <paramref name="run"/>: a body's path, not a vessel's, of a body
    /// other than the flown vessel's (<paramref name="flown"/>), drawn at least
    /// <see cref="Scene.MapLineClickAlpha"/> strongly (a line that has barely faded in can't be seen to click).
    /// </summary>
    internal static bool Clickable(in Run run, GroundBody? flown) =>
        run.Body >= 0 && run.Vessel < 0 && Bodies.All[run.Body] != flown && run.Alpha >= Scene.MapLineClickAlpha;

    /// <summary>Leaves nothing laid: the map isn't on the screen, so there is nothing to point at.</summary>
    public void Forget()
    {
        _screen.Clear();
        _runs.Clear();
        _dots.Clear();
        _edges.Clear();
        SunMark = null;
    }

    /// <summary>
    /// The body whose path (as <see cref="Lay"/> last laid it) passes within <paramref name="within"/> pixels of
    /// <paramref name="pointer"/>, the nearest if several; null if none does. The body the flown vessel is at
    /// never counts: it can't be a target.
    /// </summary>
    internal GroundBody? BodyLineAt(Vector2 pointer, float within)
    {
        GroundBody? best = null;
        var bestDistance = within;
        foreach (var run in _runs)
        {
            if (!Clickable(run, _flown))
                continue;
            for (var i = run.First + 1; i < run.First + run.Count; i++)
            {
                var (a, b) = (new Vector2(_screen[i - 1].X, _screen[i - 1].Y), new Vector2(_screen[i].X, _screen[i].Y));
                var ab = b - a;
                var t = ab.LengthSquared() > 1e-6f ? Math.Clamp(Vector2.Dot(pointer - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
                var distance = Vector2.Distance(pointer, a + ab * t);
                if (distance < bestDistance)
                    (bestDistance, best) = (distance, Bodies.All[run.Body]);
            }
        }
        return best;
    }

    /// <summary>
    /// A point on <paramref name="body"/>'s path as <see cref="Lay"/> last laid it (window pixels):
    /// the middle of its longest piece, away from the body itself, so a click there is on the line
    /// rather than on its marker. Null if none of it is laid.
    /// </summary>
    internal float2? LinePoint(GroundBody body)
    {
        var longest = -1;
        for (var i = 0; i < _runs.Count; i++)
            if (_runs[i].Vessel < 0 && _runs[i].Body == body.Index && _runs[i].Count > 1 && _runs[i].Alpha >= Scene.MapLineClickAlpha
                && (longest < 0 || _runs[i].Count > _runs[longest].Count))
                longest = i;
        return longest < 0 ? null : _screen[_runs[longest].First + _runs[longest].Count / 2];
    }

    /// <summary>
    /// Draws what <see cref="Lay"/> left, as strongly as <paramref name="fade"/>: each run one
    /// polyline with its glows (spaced <paramref name="every"/> and as far on as
    /// <paramref name="phase"/>, the path's this frame, so the two go by one reading of the clock),
    /// the dots, the sun's ring, and its name into <paramref name="labels"/> (a body's: the others
    /// give way to it).
    /// </summary>
    public void Draw(ImDrawListPtr draw, float fade, MapLabels labels, float every, double phase)
    {
        var screen = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_screen);
        var walk = new Paths.GlowWalk(); // along the whole line a run is a stretch of (see Paths.Glows), its hidden gaps and breaks by their ends
        for (var r = 0; r < _runs.Count; r++)
        {
            var run = _runs[r];
            var points = screen.Slice(run.First, run.Count);
            draw.AddPolyline(points, Color(run.Color, run.Alpha * fade), ImDrawFlags.None, Scene.PathWidth);
            var stretch = System.Runtime.InteropServices.MemoryMarshal.Cast<float2, Vector2>(points); // the same two floats: no copy
            if (r > 0 && _runs[r - 1] is var before && before.Vessel == run.Vessel && before.Body == run.Body && screen[before.First + before.Count - 1] is var end)
                walk.Along += Vector2.Distance(new Vector2(end.X, end.Y), stretch[0]); // the same line on past a gap
            else
                walk = new Paths.GlowWalk();
            Paths.DrawGlows(draw, stretch, _glows, run.Color, run.Alpha * fade, ref walk, every, phase, _display);
        }
        foreach (var (at, color, _) in _dots)
            draw.AddCircleFilled(at, Scene.MapBodyMinPixels / 2f, Color(color, fade));
        foreach (var at in _edges)
        {
            Paths.EdgeIcon(draw, at, Scene.MapOtherPathAlpha * fade);
            labels.Add(Paths.EdgeLabel, at + new float2(10f, -8f), at, Paths.EdgeColor, Scene.MapOtherPathAlpha * fade, 0.8f * Scene.MapOtherPathAlpha * fade);
        }
        if (SunMark is { } mark)
        {
            draw.AddCircle(mark.At, mark.Ring, Color(Scene.MapSunColor, 0.9f * fade), 0, 1.5f);
            labels.Add("Sun", mark.At + new float2(mark.Ring + 4f, -8f), mark.At, Scene.MapSunColor, fade, 0.8f * fade, over: true);
        }
    }

    private Vector2 _display; // the screen as last laid, so a glow off it costs none of a line's share
    private readonly (Vector2 At, double Along)[] _glows = new (Vector2, double)[Paths.MaxGlows];

    private static ImColor8 Color((byte R, byte G, byte B) color, float alpha) =>
        new(color.R, color.G, color.B, (byte)(255 * Math.Clamp(alpha, 0f, 1f)));

    private static float SmoothStep(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// <paramref name="v"/>'s path as the map last kept it, in the main view's frame this frame (a
    /// lap under formula placed by its body's transform; in the physics, its patched path), into
    /// <paramref name="points"/>; false if it isn't drawn. For the tests.
    /// </summary>
    internal bool PathOf(Vessel v, List<Vector3> points)
    {
        points.Clear();
        for (var i = 0; i < _laps.Length && _laps[i] is { } lap; i++)
        {
            if (lap.Vessel != v || !lap.Shown)
                continue;
            if (lap.Patched)
                points.AddRange(lap.Placed);
            else
            {
                // As Lay has it: an open one from where the vessel is now.
                var start = StartOf(lap);
                if (lap.Open)
                    points.Add(Vector3.Transform(NowAt(lap), _starsToView[lap.Body.Index]));
                for (var k = start; k < lap.Count; k++)
                    points.Add(Vector3.Transform(lap.Stars[k], _starsToView[lap.Body.Index]));
            }
            return points.Count > 0;
        }
        return false;
    }

    /// <summary>Where in its <see cref="PathOf"/> each leg after the first of <paramref name="v"/>'s patched path starts (the line breaks there), as last built; empty for a kept lap. For the tests.</summary>
    internal IReadOnlyList<int> StartsOf(Vessel v)
    {
        for (var i = 0; i < _laps.Length && _laps[i] is { } lap; i++)
            if (lap.Vessel == v && lap.Shown && lap.Patched)
                return lap.Starts;
        return [];
    }

    /// <summary>Whether <paramref name="v"/>'s path is patched (it is in the physics) rather than its kept lap, as last built. For the tests.</summary>
    internal bool IsPatched(Vessel v)
    {
        for (var i = 0; i < _laps.Length && _laps[i] is { } lap; i++)
            if (lap.Vessel == v && lap.Shown)
                return lap.Patched;
        return false;
    }
}
