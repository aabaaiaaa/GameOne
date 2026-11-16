using System.Numerics;

namespace GameOne;

/// <summary>
/// The bodies' frames as one frame is drawn (<see cref="Begin"/>, at the world's time then): each
/// body's change into the scene's frame, the planet's ground frame the renderer draws in
/// (<see cref="ToScene"/>), and into the main view's, the frame of the body the flown vessel is at,
/// which its camera, the HUD's markers and the paths are in (<see cref="ToView"/>). Each is worked
/// out in double on first asking and kept for the frame, so a frame costs one change for each body
/// in use, however many vessels are there; nothing here is written for two bodies. Allocates nothing.
/// </summary>
internal sealed class BodyFrames
{
    private readonly FrameChange[] _toScene, _toView;
    private readonly int[] _sceneFor, _viewFor; // the frame each was worked out for (Begin's count)
    private readonly float[] _hideRadius;      // each body's sphere that hides what is behind it, for Behind
    private readonly Vector3Double[] _centreInView; // each body's centre in the view's frame, for Behind
    private readonly int[] _centreFor;              // the frame each was worked out for
    private int _frame;
    private int _sunFor = -1; // the frame the sun's place in the view's was worked out for (Begin's count)
    private Vector3Double _sun;

    public BodyFrames()
    {
        var count = Bodies.All.Count;
        (_toScene, _toView) = (new FrameChange[count], new FrameChange[count]);
        (_sceneFor, _viewFor) = (new int[count], new int[count]);
        _hideRadius = new float[count];
        (_centreInView, _centreFor) = (new Vector3Double[count], new int[count]);
        for (var i = 0; i < count; i++)
            _hideRadius[i] = HideRadius(Bodies.All[i]);
    }

    /// <summary>The world time this frame is drawn at.</summary>
    public double Time { get; private set; }

    /// <summary>The main view's body: the flown vessel's, whose frame its camera and the HUD are in.</summary>
    public GroundBody View { get; private set; } = GroundBody.Planet;

    /// <summary>The scene's frame: the planet's ground frame, which the renderer draws everything in (the terrain, the sea, the sites and the sky are its).</summary>
    public static GroundBody Scene => GroundBody.Planet;

    /// <summary>Starts a frame drawn at world time <paramref name="worldTime"/>, its main view at <paramref name="view"/>: the changes kept for the last are forgotten.</summary>
    public void Begin(double worldTime, GroundBody view)
    {
        _frame++;
        (Time, View) = (worldTime, view);
    }

    /// <summary>From <paramref name="body"/>'s frame to the scene's, this frame.</summary>
    public FrameChange ToScene(GroundBody body) => Kept(body, Scene, _toScene, _sceneFor);

    /// <summary>From <paramref name="body"/>'s frame to the main view's, this frame.</summary>
    public FrameChange ToView(GroundBody body) => Kept(body, View, _toView, _viewFor);

    /// <summary>
    /// The sun's centre in the main view's frame this frame, in double (<see cref="SolarSystem.SunCentreAt"/>,
    /// in the planet's ground frame, changed into the view's): what the map's sun is drawn at.
    /// </summary>
    public Vector3Double SunInView
    {
        get
        {
            if (_sunFor != _frame)
                (_sun, _sunFor) = (ToView(GroundBody.Planet).Point(SolarSystem.SunCentreAt(Time)), _frame);
            return _sun;
        }
    }

    /// <summary>A place <paramref name="p"/> in <paramref name="body"/>'s frame, in the main view's (as it is, at the view's own body).</summary>
    public Vector3 InView(GroundBody body, Vector3 p) => body == View ? p : ToView(body).Point(p).ToSingle();

    /// <summary>A place <paramref name="p"/> in <paramref name="body"/>'s frame, in the scene's, in double.</summary>
    public Vector3Double InScene(GroundBody body, Vector3 p) => body == Scene ? p : ToScene(body).Point(p);

    /// <summary>A place <paramref name="p"/> in the scene's frame, in <paramref name="body"/>'s.</summary>
    public Vector3 FromScene(GroundBody body, Vector3 p) => body == Scene ? p : ToScene(body).Inverse.Point(p).ToSingle();

    private FrameChange Kept(GroundBody body, GroundBody to, FrameChange[] kept, int[] keptFor)
    {
        if (body == to)
            return FrameChange.Identity;
        var i = body.Index;
        if ((uint)i >= (uint)kept.Length || Bodies.All[i] != body)
            return Bodies.Change(body, to, Time); // (a body not in the game's list: worked out each time)
        if (keptFor[i] != _frame)
            (kept[i], keptFor[i]) = (Bodies.Change(body, to, Time), _frame);
        return kept[i];
    }

    /// <summary>
    /// Whether a body (or the sun) is in the way from <paramref name="camera"/> to <paramref name="point"/>,
    /// both in the main view's frame: each body's sphere just under its lowest (the planet's sea, just
    /// under its waves' troughs; an airless body's lowest ground), so nothing on or above a body's
    /// ground is hidden by it, and a path that ends on the ground isn't hidden by the ground it ends
    /// on; and the sun's sphere (from the map's furthest zoom, what is beyond it).
    /// </summary>
    public bool Behind(Vector3 camera, Vector3 point) => Behind(camera, point, null);

    /// <summary><see cref="Behind(Vector3, Vector3)"/> for the sun's own centre (the map's sun): by the bodies alone.</summary>
    public bool BehindTheSun(Vector3 camera, Vector3 point) => Behind(camera, point, null, sun: false);

    /// <summary><see cref="Behind(Vector3, Vector3)"/>, but by any body other than <paramref name="except"/> (whose own centre the point is: a body's marker, hidden only by another body).</summary>
    public bool Behind(Vector3 camera, Vector3 point, GroundBody? except) => Behind(camera, point, except, sun: true);

    private bool Behind(Vector3 camera, Vector3 point, GroundBody? except, bool sun)
    {
        var all = Bodies.All;
        for (var i = 0; i < all.Count; i++)
            if (all[i] != except && Blocks(camera, point, CentreInView(all[i]), _hideRadius[i]))
                return true;
        return sun && Blocks(camera, point, SunInView, GameOne.Scene.SunRadius);
    }

    /// <summary><paramref name="body"/>'s centre in the main view's frame this frame, in double (kept with its change).</summary>
    private Vector3Double CentreInView(GroundBody body)
    {
        if (body == View)
            return body.Centre;
        var i = body.Index;
        if ((uint)i >= (uint)_centreInView.Length || Bodies.All[i] != body)
            return ToView(body).Point(body.Centre);
        if (_centreFor[i] != _frame)
            (_centreInView[i], _centreFor[i]) = (ToView(body).Point(body.Centre), _frame);
        return _centreInView[i];
    }

    /// <summary>The sphere of <paramref name="body"/> that hides what is behind it (see <see cref="Behind"/>).</summary>
    internal static float HideRadius(GroundBody body) =>
        body.HasSea ? body.SeaRadius - body.HighestWave - 5f : body.SeaRadius + body.Map.MinHeight - 5f;

    /// <summary>
    /// Whether the sphere of <paramref name="radius"/> round <paramref name="centre"/> is between
    /// <paramref name="camera"/> and <paramref name="point"/>. In double: from the map's furthest
    /// zoom, millions of metres out, a float's square of the distance is rounded by more than the
    /// moon's radius squared.
    /// </summary>
    internal static bool Blocks(Vector3Double camera, Vector3Double point, Vector3Double centre, double radius)
    {
        var d = point - camera;
        var f = camera - centre;
        double a = d.LengthSquared(), b = 2 * Vector3Double.Dot(f, d), c = f.LengthSquared() - radius * radius;
        var discriminant = b * b - 4 * a * c;
        if (discriminant <= 0 || a <= 0)
            return false;
        var first = (-b - Math.Sqrt(discriminant)) / (2 * a);
        return first > 0 && first < 1;
    }
}
