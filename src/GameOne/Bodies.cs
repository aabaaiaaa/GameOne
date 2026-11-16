using System.Numerics;

namespace GameOne;

/// <summary>
/// Where a body's frame is in its parent's ground frame at a time (<see cref="GroundBody.PoseIn"/>):
/// its centre, its orientation (its own axes into its parent's), its centre's velocity and its
/// angular velocity over its parent's ground, in double.
/// </summary>
internal readonly record struct BodyPose(Vector3Double Centre, QuaternionDouble Orientation, Vector3Double Velocity, Vector3Double Spin);

/// <summary>
/// A change from one body's frame to another's at a moment (<see cref="Bodies.Change"/>): a place
/// in the first is <see cref="Rotation"/>-turned and moved by the first's origin's place in the
/// second, and as the first frame moves and turns in the second (its origin's velocity, its
/// angular velocity), a velocity or spin there gains that motion too. Worked out in double, places
/// and vectors alike (made floats only by their users near their own frame's origin), so a place
/// changed there and back lands on itself to well within a millimetre at the bodies' distances.
/// </summary>
internal readonly struct FrameChange
{
    private readonly QuaternionDouble _turn;
    private readonly Vector3Double _offset, _velocity, _spin;

    /// <param name="turn">What turns a direction in the first frame into the second.</param>
    /// <param name="offset">The first frame's origin in the second.</param>
    /// <param name="velocity">How fast the first frame's origin moves in the second.</param>
    /// <param name="spin">The first frame's angular velocity in the second.</param>
    public FrameChange(QuaternionDouble turn, Vector3Double offset, Vector3Double velocity, Vector3Double spin) =>
        (_turn, _offset, _velocity, _spin) = (turn, offset, velocity, spin);

    /// <summary>No change: a frame to itself.</summary>
    public static FrameChange Identity => new(QuaternionDouble.Identity, default, default, default);

    /// <summary>Whether this changes nothing (a frame to itself).</summary>
    public bool IsIdentity => _turn == QuaternionDouble.Identity && _offset == default && _velocity == default && _spin == default;

    /// <summary>A place in the first frame, in the second.</summary>
    public Vector3Double Point(Vector3Double p) => _offset + _turn.Rotate(p);

    /// <summary>A direction (or any vector carried by the frame's turn alone: a force, an offset) in the first frame, in the second.</summary>
    public Vector3Double Direction(Vector3Double d) => _turn.Rotate(d);

    /// <summary>
    /// A velocity <paramref name="groundVelocity"/> over the first frame's ground at <paramref name="p"/>
    /// (a place in the first frame), over the second's: the first's origin's motion and its turning
    /// carry the place along too.
    /// </summary>
    public Vector3Double Velocity(Vector3Double p, Vector3Double groundVelocity)
    {
        var offset = _turn.Rotate(p);
        return _velocity + Vector3Double.Cross(_spin, offset) + _turn.Rotate(groundVelocity);
    }

    /// <summary>An orientation in the first frame (what turns its own axes into it), in the second.</summary>
    public Quaternion Rotation(Quaternion q) => QuaternionDouble.Concatenate(QuaternionDouble.From(q), _turn).ToSingle();

    /// <summary>
    /// A model (a turn and a place, as a stage's drawn) in the first frame, in the second, its place
    /// given from <paramref name="from"/> (a place in the second): changed and taken from there in
    /// double before it is made a float, so a model tens of kilometres from the second's origin
    /// keeps its millimetres when it is near <paramref name="from"/>.
    /// </summary>
    public Matrix4x4 Model(in Matrix4x4 model, Vector3Double from)
    {
        var place = Point(new Vector3Double(model.M41, model.M42, model.M43)) - from;
        var turned = model;
        turned.Translation = Vector3.Zero;
        if (_turn != QuaternionDouble.Identity)
            turned *= Matrix4x4.CreateFromQuaternion(_turn.ToSingle());
        turned.Translation = place.ToSingle();
        return turned;
    }

    /// <summary>An angular velocity over the first frame's ground, over the second's.</summary>
    public Vector3Double Spin(Vector3Double spin) => _spin + _turn.Rotate(spin);

    /// <summary>The change back, from the second frame to the first.</summary>
    public FrameChange Inverse
    {
        get
        {
            var back = _turn.Conjugate;
            var offset = -back.Rotate(_offset);
            var spin = -back.Rotate(_spin);
            return new FrameChange(back, offset, -back.Rotate(_velocity) + Vector3Double.Cross(spin, offset), spin);
        }
    }

    /// <summary>This change, then <paramref name="next"/> (from this one's second frame on to a third).</summary>
    public FrameChange Then(in FrameChange next) =>
        new(QuaternionDouble.Normalize(QuaternionDouble.Concatenate(_turn, next._turn)), next.Point(_offset),
            next.Velocity(_offset, _velocity), next.Spin(_spin));

    /// <summary>From <paramref name="body"/>'s frame to its parent's at world time <paramref name="t"/>, by its pose there (<see cref="GroundBody.PoseIn"/>).</summary>
    public static FrameChange ToParent(GroundBody body, double t)
    {
        var pose = body.PoseIn(t);
        // The body's frame's origin is not its centre: it is on its ground, -Centre from it.
        var centre = new FrameChange(QuaternionDouble.Identity, -(Vector3Double)body.Centre, default, default);
        return centre.Then(new FrameChange(pose.Orientation, pose.Centre, pose.Velocity, pose.Spin));
    }
}

/// <summary>
/// The bodies as a list (<see cref="All"/>: for now the planet and the moon), each in its own
/// frame (fixed to its ground, as the planet's is), each but the planet going round a parent
/// (<see cref="GroundBody.Parent"/>): changes between any two frames, composed through the parents
/// (<see cref="Change"/>), and which body a place is at (<see cref="Choose(GroundBody, Vector3, double)"/>),
/// by the spheres of influence. Nothing here is written for exactly two bodies: every question
/// names the bodies it asks about.
/// </summary>
internal static class Bodies
{
    /// <summary>
    /// Every body, by <see cref="GroundBody.Index"/>: the planet, then the moon. Made on first use,
    /// after the solar system the moon reads; read-only (no cast reaches an array behind it to
    /// change a body game-wide), made once, so reading it allocates nothing.
    /// </summary>
    public static IReadOnlyList<GroundBody> All => LazyAll.Value;

    private static readonly Lazy<IReadOnlyList<GroundBody>> LazyAll = new(() => Array.AsReadOnly<GroundBody>([GroundBody.Planet, GroundBody.Moon]));

    /// <summary>Whether one of the game's bodies is called <paramref name="name"/>.</summary>
    public static bool Has(string name)
    {
        var all = All;
        for (var i = 0; i < all.Count; i++)
            if (all[i].Name == name)
                return true;
        return false;
    }

    /// <summary>The body called <paramref name="name"/> (<see cref="GroundBody.Name"/>).</summary>
    public static GroundBody Named(string name)
    {
        var all = All;
        for (var i = 0; i < all.Count; i++)
            if (all[i].Name == name)
                return all[i];
        throw new ArgumentException($"no body is called {name}", nameof(name));
    }

    /// <summary>How many parents up <paramref name="body"/> is from the root (the planet's 0).</summary>
    private static int Depth(GroundBody body)
    {
        var depth = 0;
        for (var b = body.Parent; b is not null; b = b.Parent)
            depth++;
        return depth;
    }

    /// <summary>
    /// The change from <paramref name="from"/>'s frame to <paramref name="to"/>'s at world time
    /// <paramref name="t"/>: up through the parents to the two's nearest common one and down again.
    /// No change from a body to itself (<see cref="FrameChange.Identity"/>).
    /// </summary>
    public static FrameChange Change(GroundBody from, GroundBody to, double t)
    {
        if (from == to)
            return FrameChange.Identity;
        var (a, b) = (from, to);
        var (up, down) = (FrameChange.Identity, FrameChange.Identity); // from's frame to a's; to's frame to b's
        var (da, db) = (Depth(a), Depth(b));
        for (; da > db; da--)
            (up, a) = (up.Then(FrameChange.ToParent(a, t)), a.Parent!);
        for (; db > da; db--)
            (down, b) = (down.Then(FrameChange.ToParent(b, t)), b.Parent!);
        while (a != b)
        {
            (up, a) = (up.Then(FrameChange.ToParent(a, t)), a.Parent!);
            (down, b) = (down.Then(FrameChange.ToParent(b, t)), b.Parent!);
        }
        return up.Then(down.Inverse);
    }

    /// <summary><paramref name="body"/>'s centre in <paramref name="frame"/>'s frame at world time <paramref name="t"/>.</summary>
    public static Vector3Double CentreIn(GroundBody body, GroundBody frame, double t) =>
        Change(body, frame, t).Point(body.Centre);

    /// <summary>
    /// <paramref name="body"/>'s centre's <see cref="Orbit"/> round its parent from world time
    /// <paramref name="now"/>: where its centre is in the parent's frame then, moving as it does
    /// among the parent's stars. Kepler's, by the parent's pull alone, as any orbit round the
    /// parent is: what a transfer to it and the closest approach to it are worked out against, as
    /// a flying vessel's orbit is. Only a body with a parent goes round one (not the root).
    /// </summary>
    public static Orbit OrbitOf(GroundBody body, double now)
    {
        var parent = body.Parent ?? throw new ArgumentException($"{body.Name} goes round nothing", nameof(body));
        var change = FrameChange.ToParent(body, now);
        var centre = change.Point(body.Centre);
        var overGround = change.Velocity(body.Centre, default);
        return Orbit.OverGround(parent, centre.ToSingle(), overGround.ToSingle());
    }

    /// <summary>
    /// <paramref name="body"/>'s path round what it goes round, a whole lap from where its centre is
    /// at <paramref name="now"/> round to there again, into <paramref name="points"/> (the first and
    /// the last its centre now), in <paramref name="view"/>'s frame at <paramref name="now"/>: round
    /// its parent by its orbit there (<see cref="OrbitOf"/>), or for the root (the planet) round the
    /// sun (<see cref="SolarSystem.PlanetPath"/>); among its parent's stars as they match the parent's
    /// ground now (as an orbit among the stars is drawn), changed into the view's frame. Each
    /// point is worked out and changed in double, made a float only in the view's frame. A body's
    /// orbit is closed. Allocates nothing.
    /// </summary>
    public static void Path(GroundBody body, GroundBody view, double now, Span<Vector3> points)
    {
        var last = Math.Max(points.Length - 1, 1);
        if (body.Parent is not { } parent)
        {
            var toView = Change(body, view, now);
            for (var i = 0; i < points.Length; i++)
                points[i] = toView.Point(SolarSystem.PlanetPathAt(now, (double)i / last)).ToSingle();
            return;
        }
        var orbit = OrbitOf(body, now);
        var change = Change(parent, view, now);
        var from = orbit.AnomalyAfter(0);
        for (var i = 0; i < points.Length; i++)
            points[i] = change.Point(orbit.At(from + Math.Tau * i / last)).ToSingle();
        // Its centre now exactly at the ends (the orbit's own point there is a float's rounding off it).
        if (points.Length > 0)
            points[0] = points[^1] = CentreIn(body, view, now).ToSingle();
    }

    /// <summary>
    /// Which of the game's bodies (<see cref="All"/>) a place <paramref name="p"/> in
    /// <paramref name="current"/>'s frame is at: <see cref="Choose(IReadOnlyList{GroundBody}, GroundBody, Vector3, double)"/>.
    /// </summary>
    public static GroundBody Choose(GroundBody current, Vector3 p, double t) => Choose(All, current, p, t);

    /// <summary>
    /// Which body of <paramref name="bodies"/> a place <paramref name="p"/> in
    /// <paramref name="current"/>'s frame is at, at world time <paramref name="t"/>:
    /// <see cref="Choose(IReadOnlyList{GroundBody}, ReadOnlySpan{Vector3Double}, GroundBody, Vector3, double)"/>
    /// with each body's place worked out here (<see cref="PlacesIn"/>).
    /// </summary>
    public static GroundBody Choose(IReadOnlyList<GroundBody> bodies, GroundBody current, Vector3 p, double t)
    {
        Span<Vector3Double> places = stackalloc Vector3Double[bodies.Count];
        PlacesIn(bodies, t, places);
        return Choose(bodies, places, current, p, t);
    }

    /// <summary>
    /// Each of <paramref name="bodies"/>' centres in its parent's frame at world time
    /// <paramref name="t"/> (<see cref="GroundBody.PlaceIn"/>) into <paramref name="places"/>, by its
    /// place in the list; the root's left zero (it goes round nothing). Worked out once for every
    /// place asked about at that time (<see cref="Choose(IReadOnlyList{GroundBody}, ReadOnlySpan{Vector3Double}, GroundBody, Vector3, double)"/>,
    /// <see cref="ClearWithin"/>).
    /// </summary>
    public static void PlacesIn(IReadOnlyList<GroundBody> bodies, double t, Span<Vector3Double> places)
    {
        for (var i = 0; i < bodies.Count; i++)
            places[i] = bodies[i].Parent is null ? default : bodies[i].PlaceIn(t);
    }

    /// <summary>
    /// Which body of <paramref name="bodies"/> a place <paramref name="p"/> in
    /// <paramref name="current"/>'s frame is at, at world time <paramref name="t"/>, each body's
    /// centre in its parent's frame then given (<paramref name="places"/>, from <see cref="PlacesIn"/>):
    /// out to each parent in turn only once past the body's <see cref="GroundBody.SphereRadius"/> times
    /// <see cref="Scene.SphereLeaveFactor"/>, then into the innermost child's sphere it is within
    /// (at its radius), so a place near an edge keeps the body it had. Allocates nothing; a child's
    /// sphere is tested by its centre's place alone (no orientation: a distance is the same in
    /// either frame), and a place is changed into another frame only as it goes into or out of one.
    /// </summary>
    public static GroundBody Choose(IReadOnlyList<GroundBody> bodies, ReadOnlySpan<Vector3Double> places, GroundBody current, Vector3 p, double t)
    {
        var (at, place) = (current, (Vector3Double)p);
        while (at.Parent is { } parent && (place - at.Centre).Length() > at.SphereRadius * (double)Scene.SphereLeaveFactor)
            (place, at) = (FrameChange.ToParent(at, t).Point(place), parent);
        while (true)
        {
            var (into, nearest) = ((GroundBody?)null, double.PositiveInfinity);
            for (var i = 0; i < bodies.Count; i++)
            {
                var child = bodies[i];
                if (child.Parent != at)
                    continue;
                var share = (place - places[i]).Length() / child.SphereRadius;
                if (share < 1 && share < nearest)
                    (into, nearest) = (child, share);
            }
            if (into is null)
                return at;
            (at, place) = (into, FrameChange.ToParent(into, t).Inverse.Point(place));
        }
    }

    /// <summary>
    /// How far from the centre of <paramref name="bodies"/>' <paramref name="index"/>th body, in its
    /// frame, no sphere's edge comes at the time <paramref name="places"/> are for
    /// (<see cref="PlacesIn"/>): short of its own leaving edge (none for the root) and of every
    /// child's sphere (its centre's distance less its radius), less a metre for the rounding. A
    /// place nearer its body's centre than this is chosen as at that body, whatever else
    /// (<see cref="Choose(IReadOnlyList{GroundBody}, ReadOnlySpan{Vector3Double}, GroundBody, Vector3, double)"/>
    /// goes neither out nor in, as no child's centre is nearer it than the child's radius): a bound
    /// that is never wrong, for what need not be asked.
    /// </summary>
    public static double ClearWithin(IReadOnlyList<GroundBody> bodies, ReadOnlySpan<Vector3Double> places, int index)
    {
        var body = bodies[index];
        var clear = body.Parent is null ? double.PositiveInfinity : body.SphereRadius * (double)Scene.SphereLeaveFactor;
        for (var i = 0; i < bodies.Count; i++)
            if (bodies[i].Parent == body)
                clear = Math.Min(clear, (places[i] - body.Centre).Length() - bodies[i].SphereRadius);
        return clear - 1.0;
    }
}
