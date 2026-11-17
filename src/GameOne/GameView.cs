using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using GameOne.Audio;
using GameOne.Physics;
using GameOne.Rendering;

namespace GameOne;

/// <summary>
/// Shows a flight: the chase camera, the direction rings, the effects and smoke, the sound and the
/// HUD, rendered into a window each frame. It never changes the flight, so whatever drives the
/// flight (the game's own loop with the player's input, or the flight tests' harness with an
/// autopilot, to watch a test) can show it the same way.
/// </summary>
internal sealed class GameView : IDisposable
{
    private readonly GlfwWindow _window;
    private readonly SmokeStirs _smokeStirs = new();
    private readonly Effects _effects = new();
    private readonly SmokeTrail _trail = new();
    private readonly List<SmokeCloud> _clouds = []; // the explosions' and the trails', for the renderer
    private readonly BodyFrames _frames = new(); // each body's frame into the scene's and the main view's, once a frame
    private readonly SceneInput _scene = new();  // every vessel as drawn, in the scene's frame
    private GroundBody _cameraBody = GroundBody.Planet; // the body whose frame the main camera is in
    private readonly List<ViewInput> _viewInputs = [];
    private readonly List<Raft> _floesNear = [];
    private readonly HashSet<Raft> _floesSeen = [];
    private readonly List<Matrix4x4> _floeModels = []; // the floes to draw, for the renderer
    private readonly Matrix4x4[] _shipModels = new Matrix4x4[Rafts.Ships.Count]; // and the drone ships
    private readonly LightSets _lightSets = new(); // each view's lights (shared by views at one place), chosen afresh each frame
    private readonly SunAreas _sunAreas = new(); // each view's sun shadows' area (shared by views close together), chosen afresh each frame
    private readonly VulkanRenderer _renderer;
    private readonly AudioSystem _audio;
    private readonly EngineSound _engine;
    private readonly ImpactSounds _sounds;

    public ChaseCamera Camera { get; }
    public NavigationControl Navigation { get; }
    public Hud Hud { get; }

    /// <summary>The small views of other vessels, beside the main one.</summary>
    public Views Views { get; } = new();

    /// <summary>Creates GLFW's window for the game, hidden until <see cref="Present"/> has drawn its first frame. Call <see cref="Glfw.Init"/> first.</summary>
    public static GlfwWindow CreateWindow(string title)
    {
        VulkanRenderer.ApplyWindowHints();
        Glfw.WindowHint(GlfwWindowHint.Visible, 0); // shown once the first frame is ready, so no blank flash
        var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo
        {
            Title = title,
            Size = new int2(1280, 720),
        });
        // No smaller than the HUD is laid out for: below 800x600 its panels meet (the compass
        // over the instruments, and no room for the open controls list anywhere). No largest.
        window.SetSizeLimits(new int2(800, 600), new int2(DontCare, DontCare));
        return window;
    }

    /// <summary>GLFW's "no limit" for a window size limit (GLFW_DONT_CARE).</summary>
    private const int DontCare = -1;

    /// <summary>How frames are presented (vsync or not), for reports.</summary>
    public string PresentMode => _renderer.ActivePresentMode.ToString();

    /// <summary>Whether frames wait for the display's refresh: changed, from the next frame (see <see cref="Settings.CapFrameRate"/>).</summary>
    public bool VSync
    {
        get => _renderer.VSync;
        set => _renderer.VSync = value;
    }


    /// <summary>Each drone ship's model as it is now, into <see cref="_shipModels"/> (filled in place: nothing is allocated each frame).</summary>
    private ReadOnlySpan<Matrix4x4> ShipModels()
    {
        for (var i = 0; i < _shipModels.Length; i++)
            _shipModels[i] = Rafts.Ships[i].Model;
        return _shipModels;
    }

    /// <param name="vsync">Wait for the display's refresh. Off (for soak runs) draws every frame as fast as it can.</param>
    public GameView(GlfwWindow window, bool vsync = true)
    {
        _window = window;
        Camera = new ChaseCamera(window);
        Navigation = new NavigationControl(window);
        // The HUD owns the ImGui context, which the renderer's ImGui backend needs, so it is
        // created first and disposed last.
        Hud = new Hud(window);
        _renderer = new VulkanRenderer(window, vsync);
        _audio = new AudioSystem();
        _engine = new EngineSound(_audio);
        _sounds = new ImpactSounds(_audio);
        Hud.SetLogo(_renderer.LogoTexture, _renderer.LogoTextureSize);
    }

    /// <summary>How a vessel never flown is looked at: the chase view at its starting distance and pitch, centred on itself.</summary>
    internal static CameraView DefaultView { get; } = new(false, 0f, float.DegreesToRadians(Scene.CameraPitchDegrees), Scene.CameraDistance,
        0f, float.DegreesToRadians(Scene.ZoomOutPitchDegrees), Scene.ZoomOutMinDistance, false, CameraCentre.Own);

    /// <summary>Starts showing <paramref name="flight"/>, resetting the camera and effects whenever it starts again.</summary>
    public void Follow(Flight flight)
    {
        flight.Restarted += () =>
        {
            Camera.Snap();
            _smokeStirs.Reset();
            _effects.Clear();
            _trail.Clear();
        };
        Views.Follow(flight); // the small views reset with the flight, and as something is carried on
        flight.ViewTaken += vessel => Camera.Apply(vessel.View ?? DefaultView); // straight to the vessel now being flown (which may be far off), looked at as it was left
        flight.ViewRebased += Hud.Rebase; // what the HUD keeps from frame to frame in the flown vessel's frame (the paths' window), dropped
    }

    /// <summary>
    /// What M does to what the camera is on (<paramref name="centre"/>) and whether it shows the map
    /// (<paramref name="map"/>). From the map, centred on a vessel, a site or a point of interest, it
    /// gives the chase view of it; centred on a body (or the flown vessel), the chase view of the
    /// flown vessel. From the chase view, the map with the same centre.
    /// </summary>
    internal static (CameraCentre Centre, bool Map) Toggled(CameraCentre centre, bool map) =>
        !map ? (centre, true) : centre.Kind == CentreKind.Body ? (CameraCentre.Own, false) : (centre, false);

    /// <summary>
    /// The vessel the main camera is on now, if it isn't the flown one (<see cref="Watching"/>, but
    /// as of this moment rather than the last frame drawn): so a camera just put on a vessel is
    /// watched, and its link-up begun, before the frame that first draws it.
    /// </summary>
    public Vessel? WatchingNow(Flight flight)
    {
        var centre = CameraSubject.Kept(flight, Camera.Centre);
        return centre.Kind == CentreKind.Vessel ? CameraSubject.VesselOf(flight, centre) : null;
    }

    /// <summary>
    /// Zooms the camera all the way out, far enough to see the whole of the flown vessel's orbit
    /// (or as far as it goes, if it is escaping) round the body it is at, or back to the chase view,
    /// of what the camera is on (<see cref="Toggled"/>).
    /// </summary>
    public void ToggleZoomOut(Flight flight)
    {
        var vessel = flight.Controlled;
        var body = vessel.Body;
        var reach = MathF.Max(1.5f * body.Radius, Vector3.Distance(vessel.Position, body.Centre));
        if (vessel.State == FlightState.Flying)
            reach = MathF.Max(reach, body.SeaRadius + MathF.Min(vessel.Apoapsis, Orbit.MaxRange));
        var (centre, _) = Toggled(Camera.Centre, Camera.ZoomedOut);
        if (centre != Camera.Centre)
            Camera.CentreOn(centre); // from a body's map, to the flown vessel's chase view
        Camera.ToggleZoomOut(reach);
    }

    /// <summary>
    /// Draws one frame of the flight as it is now, <paramref name="dt"/> seconds of game time after
    /// the last (<paramref name="realDt"/> of real time, if that is different, as in slow motion or
    /// time warp), at <paramref name="time"/> seconds since the first. Call after each
    /// <see cref="Flight.Update"/> (its sounds are this frame's). The camera, the engine's sound and
    /// the rings follow the vessel being flown; every vessel is drawn, and the small views show
    /// others. <paramref name="paused"/> silences the engines and hides the rings.
    /// </summary>
    /// <remarks>
    /// Each vessel, its camera and the HUD are in the frame of the body it is at; the renderer draws
    /// in the scene's (the planet's ground frame): each body's change into it is worked out once
    /// for the frame (<see cref="BodyFrames"/>), and every vessel drawn changed by its body's
    /// (<see cref="SceneInput"/>). The main camera is in the flown vessel's body's frame, changed
    /// across (<see cref="ChaseCamera.Rebase"/>) as that body changes, at the frame's own time, so it
    /// keeps its place and view.
    /// </remarks>
    public void Present(float dt, double time, Flight flight, bool paused, float? realDt = null)
    {
        // Where the frame's time goes, while the timings are on (each lap is one bool read when they're off).
        Timings.BeginFrame();
        var vessel = flight.Controlled;
        var exploded = vessel.State == FlightState.Crashed;
        _frames.Begin(flight.WorldTime, flight.Body);
        foreach (var sound in flight.Sounds)
            if (sound.Cue == SoundCue.Explosion)
            {
                _effects.Explode(sound.Body, sound.Position, time);
                if (sound.Body.HasSites) // (the ground smoke is the sites')
                    _smokeStirs.Blast(_frames.InScene(sound.Body, sound.Position).ToSingle());
            }
            else if (sound.Cue == SoundCue.Splash)
                _effects.Splash(sound.Body, sound.Position, time);
        _effects.Update(time, _frames);

        // The camera follows the middle of the rocket, or after a crash, where it blew up, or
        // whatever else it has been put on: in the flown vessel's body's frame, by its subject's up
        // and clear of its subject's ground, changed across first if the flown vessel's body has
        // changed (a crossing, or a vessel at another body flown).
        if (_cameraBody != flight.Body)
        {
            Camera.Rebase(Bodies.Change(_cameraBody, flight.Body, flight.WorldTime));
            _cameraBody = flight.Body;
        }
        (Watching, _subjectBody) = Aim(Camera, flight, dt, realDt);
        _subjectAt = flight.WorldTime;
        CameraVelocity = Camera.Centre.Kind == CentreKind.Own ? null : Watching?.Velocity ?? Vector3.Zero;
        Views.Update(dt, flight, Camera, realDt);

        // Every vessel as drawn, in the scene's frame; and every engine firing, for the plumes and the smoke they leave behind.
        _scene.Gather(flight, _frames, Camera, paused);
        var engines = _scene.Engines;
        if (!paused)
            for (var i = 0; i < engines.Count; i++)
            {
                var e = engines[i];
                _trail.Emit(e.Vessel, e.Nozzle, e.Exhaust, e.Throttle, e.BellRadius, time, dt);
            }
        _trail.Update(time);
        _clouds.Clear();
        _clouds.AddRange(_effects.Clouds);
        _clouds.AddRange(_trail.Clouds);

        // The vessel being flown as it is this frame: where its engine is and how hard it is firing
        // (in its body's frame, the camera's and the listener's; and in the scene's, for the smoke it stirs and its flame's light).
        var (nozzle, _, throttle, air) = SceneInput.Engine(vessel, paused);
        var flown = _scene.Flown;
        var model = _frames.ToScene(vessel.Body).Model(vessel.Model, Vector3Double.Zero);

        var size = _window.Size;
        Navigation.Blocked = Hud.PointerOnMarker; // a click on a marker (or a node) is for it, not the rings
        Navigation.Burn = flight.Guide is { } guide ? (guide.Left, guide.Overshot) : null;
        Navigation.Update(dt, flight, Camera, new Vector2(size.X, size.Y), enabled: !paused && !Hud.Hidden && Camera.Centre.Kind == CentreKind.Own); // (hidden, the rings can't be seen, so they take no drag; nor on what the camera is on if not the flown vessel)
        _smokeStirs.Update(dt, exploded ? null : model, vessel.Height, flown.Nozzle, flown.Exhaust, throttle);

        // Sound: the listener at the camera, the engine at the nozzle, and the flight's one-off sounds.
        _audio.UpdateListener(Camera.Position, Camera.Forward, Camera.Up);
        _engine.Update(nozzle, flight.Velocity, Camera.Position, throttle, air);
        _sounds.Play(flight.Sounds, dt, _frames);

        // The HUD lays out the small views (and handles clicks on them), so they are drawn where it put them.
        Timings.FrameLap(FramePart.Prepare);
        Hud.Build(time, flight, Camera, Navigation, Views, _frames);
        ImGui.Render();
        Timings.FrameLap(FramePart.Hud);
        _viewInputs.Clear();
        // Each view lit by the place nearest it: the main view's from its camera (its set the
        // first), each small view's from its vessel, each held across the middle between two by
        // its own shadow place (the main view's kept with the views, so a swap hands it on). And
        // each with the sun's shadows round its own vessel: the main view's area first, then each
        // small view's, or the area of one before it that its vessel is well inside. The sun (and
        // the moon) is where the world's clock has it, worked out once for the frame (FillSky): the
        // shadows, the sky and the flare all go by the one direction. The lights go by each place's
        // own dark, as far on as the flight's lights have stepped them (Flight.Lights). All in the
        // scene's frame.
        var vesselLights = _scene.Lights;
        var camera = _scene.CameraPosition;
        _lightSets.Clear();
        _lightSets.Choose(camera, flight.Lights.Lit, vesselLights, ref Views.MainShadowPlace);
        _sunAreas.Clear();
        var mainShared = false;
        _sunAreas.Choose(MainSunArea(flight, exploded ? null : model), camera, ref mainShared);
        var screen = new Vector2(size.X, size.Y);
        if (screen.X > 0 && screen.Y > 0)
            for (var i = 0; i < Views.Small.Count; i++) // by index: a foreach over the interface would box its enumerator every frame
            {
                var view = Views.Small[i];
                if (view.Size is not { X: > 0, Y: > 0 }) // the HUD leaves out any it has no room for
                    continue;
                var (at, looking, up) = SceneInput.InScene(_frames, view.Body, view.Camera);
                var toScene = _frames.ToScene(view.Vessel.Body);
                var focus = toScene.Point(Views.Focus(view.Vessel)).ToSingle();
                var viewModel = view.Vessel.State == FlightState.Crashed ? (Matrix4x4?)null : toScene.Model(view.Vessel.Model, Vector3Double.Zero);
                _viewInputs.Add(new ViewInput(at, looking, up, view.Min / screen, view.Size / screen,
                    _lightSets.Choose(focus, flight.Lights.Lit, vesselLights, ref view.ShadowPlace),
                    _sunAreas.Choose(viewModel, at, ref view.SunShared),
                    view.Camera.Clip, SceneInput.MoonFrom(_frames, at), SceneInput.SunFrom(_frames, at)));
            }

        GatherFloes();

        // One point light: an explosion's flash while it lasts, otherwise the flame, which flickers.
        var (lightPosition, light) = _effects.Flash;
        if (light == Vector3.Zero && throttle > 0f)
        {
            var flicker = 0.85f + 0.15f * MathF.Sin((float)time * 53f) * MathF.Sin((float)time * 31f);
            (lightPosition, light) = (flown.Nozzle + flown.Exhaust * 3f, Scene.FlameColor * (2.5f * throttle * flicker));
        }

        var frame = new FrameInput
        {
            Time = time,
            CameraPosition = camera,
            CameraTarget = _scene.CameraTarget,
            CameraUp = _scene.CameraUp,
            CameraClip = Camera.Clip,
            SunAreas = _sunAreas.Areas,
            DroneShips = ShipModels(),
            Floes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_floeModels),
            Parts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_scene.Parts),
            Plumes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_scene.Plumes),
            PointLightPosition = lightPosition,
            PointLight = light,
            LightSets = _lightSets.Sets,
            PlaceLit = flight.Lights.Lit,
            VesselGlows = vesselLights,
            Fireballs = _effects.Fireballs,
            Clouds = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_clouds),
            Stirs = _smokeStirs.Stirs,
            SmokeThickness = _smokeStirs.Thickness,
            Views = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_viewInputs),
            MoonFromCamera = _scene.MoonFromCamera,
            SunDisc = _scene.SunDisc,
            MapShown = Paths.MapFade(Camera),
        };
        FillSky(ref frame, flight.WorldTime);
        Timings.FrameLap(FramePart.Prepare);
        _renderer.RenderFrame(frame, ImGui.GetDrawData());
        _window.Visible = true; // no-op after the first frame

        // The world as it is this frame, and the warp asked and reached (game time over real time;
        // none while paused: dt still runs at the warp then, though nothing moves).
        if (Timings.Enabled)
            Timings.CountWorld(flight.Worlds, flight.Unwatched.UnderFormula);
        Timings.EndFrame(Hud.TimeScale, paused ? 0f : realDt is { } real && real > 0f ? dt / real : Hud.TimeScale);
    }

    /// <summary>
    /// The vessel the main camera is on, if it isn't the flown one, as of the last frame drawn
    /// (<see cref="Aim"/>): watched, so witnessed (<see cref="FlightInput.Watching"/>).
    /// </summary>
    public Vessel? Watching { get; private set; }

    /// <summary>
    /// How fast what the main camera is on moves over its body's ground, as of the last frame drawn
    /// (<see cref="FlightInput.CameraVelocity"/>): the watched vessel's, none for a place or a body;
    /// null on the flown vessel (the flight knows its own).
    /// </summary>
    public Vector3? CameraVelocity { get; private set; }

    /// <summary>
    /// What the main view's sun shadows are drawn round, in the scene's frame: what its camera is on.
    /// The flown vessel's <paramref name="flown"/> model (null once it has blown up), another
    /// vessel's, or for a place or a body, a model standing at the camera's focus.
    /// </summary>
    private Matrix4x4? MainSunArea(Flight flight, Matrix4x4? flown)
    {
        if (Camera.Centre.Kind == CentreKind.Own)
            return flown;
        if (Watching is { } watched)
            return _frames.ToScene(watched.Body).Model(watched.Model, Vector3Double.Zero);
        return Matrix4x4.CreateTranslation(_frames.ToScene(flight.Body).Point(Camera.Focus).ToSingle());
    }

    /// <summary>
    /// Where the main camera is, as of the last frame drawn, in the frame of the body what it is on
    /// is at (<see cref="FlightInput.Camera"/> and <see cref="FlightInput.CameraBody"/>): the vessels
    /// near it there are the main view's witnessed. The camera itself is in the flown vessel's body's
    /// frame, changed back across in double for one on something at another body.
    /// </summary>
    public (GroundBody Body, Vector3 Position) CameraPlace => _subjectBody == _cameraBody
        ? (_cameraBody, Camera.Position)
        : (_subjectBody, Bodies.Change(_cameraBody, _subjectBody, _subjectAt).Point(Camera.Position).ToSingle());

    private GroundBody _subjectBody = GroundBody.Planet; // the body what the camera is on is at, as of the last frame drawn
    private double _subjectAt;                            // and that frame's world time

    /// <summary>
    /// Points <paramref name="camera"/> (in the flown vessel's body's frame) at what it is on, for a
    /// frame <paramref name="dt"/> on (<paramref name="realDt"/> of real time): its centre kept, or
    /// given back to the flown vessel if what it was on is gone (<see cref="CameraSubject.Kept"/>),
    /// then that followed, its focus changed into the camera's frame (<see cref="CameraSubject.Seen"/>).
    /// Returns the vessel it is on if that isn't the flown one (the main view's watched), and the
    /// body what it is on is at.
    /// </summary>
    internal static (Vessel? Watching, GroundBody Body) Aim(ChaseCamera camera, Flight flight, float dt, float? realDt)
    {
        var centre = CameraSubject.Kept(flight, camera.Centre);
        if (centre != camera.Centre)
            camera.CentreOn(centre);
        // (Kept has found it there, so it is seen.)
        var (body, focus, across, _) = CameraSubject.Seen(flight, centre)!.Value;
        camera.Update(dt, focus, realDt, flight.Body, flight.WorldTime, body, across);
        return (centre.Kind == CentreKind.Vessel ? CameraSubject.VesselOf(flight, centre) : null, body);
    }

    /// <summary>
    /// The sky's bodies at <paramref name="worldTime"/>, into <paramref name="input"/>: the world's
    /// time, the sun's direction, how far the stars have turned and the moon (its size, its
    /// orientation and the light it gives back; its place is the camera's, <see cref="FrameInput.MoonFromCamera"/>),
    /// each from the world's clock (<see cref="SolarSystem"/>).
    /// </summary>
    internal static void FillSky(ref FrameInput input, double worldTime)
    {
        input.WorldTime = worldTime;
        input.SunDirection = SolarSystem.SunDirection(worldTime); // the planet's ground's frame, the renderer's
        input.StarTurn = Planet.TurnAngle(worldTime);
        input.MoonRadius = Scene.MoonRadius;
        input.MoonOrientation = SolarSystem.MoonOrientation(worldTime);
        input.MoonShine = SolarSystem.MoonShine(worldTime);
    }

    /// <summary>
    /// The floes to draw (see <see cref="Scene.FloeDrawDistance"/>): those near the main view's
    /// camera and each small view's, once each, each posed as it is at the rafts' time
    /// (<see cref="Rafts.PoseNow"/>), so a floe is drawn where the physics has it, and a booster
    /// standing on one is drawn standing on it. Into lists kept from frame to frame, so nothing is
    /// allocated.
    /// </summary>
    private void GatherFloes()
    {
        _floeModels.Clear();
        _floesSeen.Clear();
        Near(_scene.CameraPosition);
        foreach (var view in _viewInputs)
            Near(view.CameraPosition);

        void Near(Vector3 camera)
        {
            // Higher than that over the sea, every floe is out of range (and the cells needn't be searched).
            if (Planet.Altitude(camera) > Scene.FloeDrawDistance)
                return;
            _floesNear.Clear();
            Floes.Near(camera, Scene.FloeDrawDistance, _floesNear);
            foreach (var floe in _floesNear)
                if (_floesSeen.Add(floe))
                {
                    // Its id in the model's M14, which a model that only moves, turns and scales leaves
                    // 0: the shaders take it out, and give each floe its own look by it.
                    var model = Meshes.FloeModel(floe, Rafts.PoseNow(floe));
                    model.M14 = floe.Id;
                    _floeModels.Add(model);
                }
        }
    }

    public void Dispose()
    {
        _sounds.Dispose();
        _engine.Dispose();
        _audio.Dispose();
        _renderer.Dispose();
        Hud.Dispose();
    }
}
