using System.Numerics;

namespace GameOne;

/// <summary>
/// Which vessels are on the screen: the one being flown fills it (the main view), and others can
/// have small views of their own, in a strip (see <see cref="Hud"/> for where it goes). Each small
/// view has a camera of its own, following its vessel from where the main camera last saw it.
/// </summary>
/// <remarks>
/// <para>A vessel that comes away from the rocket and can be flown gets a view straight away; debris
/// only when the player adds one. Closing a view leaves its vessel as it was: it carries on with its
/// last inputs, it just isn't drawn.</para>
/// <para>Swapping (clicking a small view, or Tab) flies the other vessel: the two change places,
/// the one that was flown taking the small view's place in the strip. The swap itself is the
/// flight's (<see cref="FlightInput.SwitchTo"/>): this follows whatever the flight is flying.</para>
/// </remarks>
internal sealed class Views
{
    /// <summary>A small view: the vessel it shows, its camera, and where the HUD put it on the screen (in pixels).</summary>
    public sealed class SmallView(Vessel vessel)
    {
        public Vessel Vessel { get; } = vessel;
        public ChaseCamera Camera { get; } = new(null);

        /// <summary>The body whose frame its camera is in: its vessel's, rebased to the new one's as the vessel crosses (see <see cref="Update"/>).</summary>
        public GroundBody Body { get; set; } = vessel.Body;

        public Vector2 Min { get; set; }
        public Vector2 Size { get; set; }

        /// <summary>
        /// The lit place whose floods lit its vessel last frame (<see cref="PadLights.All"/>' index, -1
        /// for none), its own, so it is held across the middle between two as the main view's is
        /// (<see cref="LightSets.Choose"/>). A field, to be passed by reference; a new view starts afresh.
        /// </summary>
        public int ShadowPlace = -1;

        /// <summary>
        /// Whether its vessel shared another view's sun area last frame (<see cref="SunAreas.Choose"/>),
        /// so it keeps sharing it a little past the edge. A field too; a new view starts with its own.
        /// </summary>
        public bool SunShared;
    }

    /// <summary>
    /// The main view's own <see cref="SmallView.ShadowPlace"/>, held from frame to frame as a small
    /// view's is (from its camera, <see cref="LightSets.Choose"/>). It is here, not with the camera,
    /// so a swap can hand it on with its vessel. (The main view needs no <see cref="SmallView.SunShared"/>:
    /// its sun area is always its own, the first.)
    /// </summary>
    public int MainShadowPlace = -1;

    private readonly List<SmallView> _small = [];
    private Vessel? _main;
    private int _seen; // how many of the flown vessel's flight's vessels have been looked at, to open views for new ones
    private int? _seenOf; // which flight's those are (Vessel.Flight): none since a reset
    private int _removedForRoom; // the flight's count of vessels removed to make room, when last looked at
    private Vessel? _swapTo;
    private Vessel? _carryingOn; // the vessel carried on, until it is flown (see CarryOn)

    /// <summary>The small views, in the strip's order (left to right).</summary>
    public IReadOnlyList<SmallView> Small => _small;

    /// <summary>Whether another small view can be opened.</summary>
    public bool Full => _small.Count >= Scene.MaxSmallViews;

    /// <summary>
    /// Follows <paramref name="flight"/>: back to the main view alone whenever a flight starts or a
    /// world is brought back, and whenever something is carried on (<see cref="CarryOn"/>).
    /// </summary>
    public void Follow(Flight flight)
    {
        flight.Restarted += Reset;
        flight.CarriedOn += CarryOn;
    }

    /// <summary>
    /// <paramref name="vessel"/> carried on (<see cref="Flight.CarriedOn"/>): the main view alone, the
    /// vessel in it once it is flown (after its link-up, if it has one), with nothing swapped into
    /// the strip. The small views are opened again by hand.
    /// </summary>
    public void CarryOn(Vessel vessel)
    {
        Reset();
        _carryingOn = vessel;
    }

    /// <summary>Back to the main view alone, for a fresh flight.</summary>
    public void Reset()
    {
        _small.Clear();
        _main = null;
        MainShadowPlace = -1;
        _seen = 0;
        _seenOf = null;
        _swapTo = null;
        _carryingOn = null;
    }

    /// <summary>
    /// Keeps the views in step with <paramref name="flight"/>, after its update: a swap if it is
    /// flying another vessel now, a view for each new vessel that can be flown, and every small
    /// view's camera moved on <paramref name="dt"/> seconds (<paramref name="realDt"/> of real time, by which a
    /// camera's ease across a sphere's edge goes; the game time if null). <paramref name="mainCamera"/> is the
    /// main view's, which a vessel's small view starts from when it leaves the main view.
    /// </summary>
    public void Update(float dt, Flight flight, ChaseCamera mainCamera, float? realDt = null)
    {
        var flown = flight.Controlled;
        if (_carryingOn is { } carried)
        {
            // Flown now, or the switch to it let go: either way, no swap.
            if (flown == carried || flight.LinkUp.Vessel != carried)
                _carryingOn = null;
        }
        else if (_main is { } previous && previous != flown)
        {
            // The vessel now flown leaves the strip, and the one that was takes its place there.
            // Each takes what its view held across the middle between two places with it: the
            // vessel that was flown the main view's, the one now flown its small view's (if it had
            // one), so neither view's lights and shadows jump. The main view's sun area was its
            // own, so the vessel leaving it starts with its own too.
            var at = _small.FindIndex(view => view.Vessel == flown);
            var left = new SmallView(previous) { ShadowPlace = MainShadowPlace };
            // The main camera has been given the vessel now flown's view by then, so the one
            // leaving it looks as it was left (its own view, kept each frame), not like that.
            if (previous.View is { } kept)
                left.Camera.Place(kept.Yaw, kept.Pitch, kept.Distance);
            else
                left.Camera.LookLike(mainCamera);
            MainShadowPlace = at >= 0 ? _small[at].ShadowPlace : -1;
            if (at >= 0)
                _small[at] = left;
            else
                _small.Insert(0, left);
        }
        _main = flown;

        // A vessel the world has let go of (to stay within its capacity) loses its view, and
        // this flight's list may be shorter for it.
        if (flight.RemovedForRoom != _removedForRoom)
        {
            _removedForRoom = flight.RemovedForRoom;
            _small.RemoveAll(view => !flight.WorldVessels.Contains(view.Vessel));
            _seen = Math.Min(_seen, flight.Vessels.Count);
        }

        // Flying a vessel of another flight, or after a reset, the flight's vessels are there
        // already: only parts coming off from now on are new. (A new flight's only vessel is its
        // rocket, flown; a world brought back, or something carried on, opens no views by itself.)
        if (_seenOf != flown.Flight)
        {
            _seen = flight.Vessels.Count;
            _seenOf = flown.Flight;
        }
        for (; _seen < flight.Vessels.Count; _seen++)
            if (flight.Vessels[_seen] is { Guided: true } vessel && vessel != flown)
                Open(vessel, flight);
        if (_small.Count > Scene.MaxSmallViews)
        {
            // A view dropped from a full strip lets its link-up go, as one closed by hand does.
            for (var i = Scene.MaxSmallViews; i < _small.Count; i++)
                flight.CloseView(_small[i].Vessel);
            _small.RemoveRange(Scene.MaxSmallViews, _small.Count - Scene.MaxSmallViews);
        }

        // Each camera in its vessel's body's frame: one whose vessel has crossed a sphere's edge is
        // changed across first, at the moment it is drawn, so it keeps its place and view.
        var now = flight.WorldTime;
        for (var i = 0; i < _small.Count; i++)
        {
            var view = _small[i];
            if (view.Vessel.Body != view.Body)
            {
                view.Camera.Rebase(Bodies.Change(view.Body, view.Vessel.Body, now));
                view.Body = view.Vessel.Body;
            }
            view.Camera.Update(dt, Focus(view.Vessel), realDt, view.Body, now);
        }
    }

    /// <summary>Where a camera following <paramref name="vessel"/> looks: the middle of it, or where it blew up.</summary>
    public static Vector3 Focus(Vessel vessel) => vessel.State == FlightState.Crashed
        ? vessel.CrashPosition
        : Vector3.Transform(new Vector3(0, vessel.Height * 0.45f, 0), vessel.Model);

    /// <summary>Whether <paramref name="vessel"/> (any in the world) could be given a small view: it isn't being flown, hasn't one already, and is still in one piece.</summary>
    public bool CanOpen(Vessel vessel) =>
        !Full && vessel != _main && vessel.State != FlightState.Crashed && _small.TrueForAll(view => view.Vessel != vessel);

    /// <summary>
    /// Gives <paramref name="vessel"/> a small view, at the end of the strip, if it can have one:
    /// back in <paramref name="flight"/>'s physics first if it was out of it, so what the view shows
    /// is the physics' (see <see cref="Flight.OpenView"/>: at a body whose physics isn't made, a
    /// frame later, behind the view's link-up).
    /// </summary>
    public void Open(Vessel vessel, Flight flight)
    {
        if (!CanOpen(vessel))
            return;
        flight.OpenView(vessel);
        var view = new SmallView(vessel);
        view.Camera.Snap();
        _small.Add(view);
    }

    /// <summary>Closes <paramref name="view"/>. Its vessel carries on as it was; a link-up over the view is let go (<see cref="Flight.CloseView"/>).</summary>
    public void Close(SmallView view, Flight flight)
    {
        _small.Remove(view);
        flight.CloseView(view.Vessel);
    }

    /// <summary>Asks to fly the vessel in <paramref name="view"/> (see <see cref="TakeSwap"/>).</summary>
    public void Swap(SmallView view)
    {
        if (Flight.CanFly(view.Vessel))
            _swapTo = view.Vessel;
    }

    /// <summary>The vessel the player asked to fly since the last call, if they did, for the flight's next input.</summary>
    public Vessel? TakeSwap()
    {
        var swap = _swapTo;
        _swapTo = null;
        return swap;
    }
}
