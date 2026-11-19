using Brutal.GlfwApi;
using GameOne;
using GameOne.Physics;

// Anything written to stderr from here on is counted, for the HUD's error sign.
ErrorLog.Install();

if (!Glfw.Init())
    throw new Exception("Failed to initialize GLFW");

var window = GameView.CreateWindow("GameOne");
var controls = new Controls(window);
var store = WorldStore.Default();

// The settings first: the view is made with the frame cap they ask for.
var settings = store.LoadSettings();
using (var view = new GameView(window, vsync: settings.CapFrameRate))
using (var worlds = new Worlds()) // each body's physics, made as a vessel there needs one
{
    // Until a world is picked from the list, an empty one that isn't saved sits behind it, at the
    // launch site the player last picked.
    using var flight = new Flight(worlds, Mission.LaunchAndLand, site: LaunchSites.Named(settings.LaunchSite));
    view.Follow(flight);
    var hud = view.Hud;
    hud.Worlds = store.List();
    hud.Settings = settings;
    Timings.Enabled = settings.ShowTimings;
    // What the HUD shows, written out for the GameDriver, when it asks.
    var readout = Readout.On ? new Readout() : null;

    // The world is saved whenever a flight starts in it (a new one, again, or a rewind), once
    // something is chosen to fly in a world just picked, every so often while it is played, before
    // another is picked, and when the game closes. Not while a world just picked has nothing chosen
    // in it yet: it is still as it was saved, and the placeholder put on its pad isn't to be kept.
    const double saveEverySeconds = 30;
    var saveNow = false;
    var lastSave = Glfw.Time;
    flight.Restarted += () => saveNow = true;
    void Save()
    {
        if (flight.WorldId.Length == 0 || !flight.Chosen)
            return; // not a saved world, or nothing chosen in it yet (see above)
        try
        {
            store.Save(flight.Capture());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Couldn't save the world: {e.Message}");
        }
        lastSave = Glfw.Time;
    }

    // What the player asked of the world list. A world picked (or made, or reset) comes back as it
    // was saved, and the player chooses what to fly in it: something to carry on, or a new mission.
    void DoWorldRequest(WorldRequest request)
    {
        try
        {
            switch (request.Kind)
            {
                case WorldRequestKind.Play:
                    Save();
                    flight.Restore(store.Load(request.Id));
                    hud.OpenCarryOn(flight);
                    break;
                case WorldRequestKind.New:
                    Save();
                    flight.Restore(store.Create(request.Id));
                    hud.OpenCarryOn(flight);
                    break;
                case WorldRequestKind.Rename when request.Id == flight.WorldId:
                    flight.Rename(request.Text);
                    if (flight.Chosen)
                        Save();
                    else
                        store.Rename(request.Id, request.Text); // not saved while nothing is chosen in it: renamed on disk, as it stands there
                    break;
                case WorldRequestKind.Rename:
                    store.Rename(request.Id, request.Text);
                    break;
                case WorldRequestKind.Reset when request.Id == flight.WorldId:
                    flight.Restore(store.Reset(request.Id));
                    hud.OpenCarryOn(flight);
                    break;
                case WorldRequestKind.Reset:
                    store.Reset(request.Id);
                    break;
                case WorldRequestKind.Delete:
                    store.Delete(request.Id);
                    if (request.Id == flight.WorldId)
                        flight.Restore(WorldPresets.Empty()); // gone: back to an unsaved one (the world list stays up), until another is picked
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"World list: couldn't {request.Kind.ToString().ToLowerInvariant()} \"{request.Id}\": {e.Message}");
        }
        hud.Worlds = store.List();
    }

    // Time from the first frame, not from Glfw.Init: start-up (device creation, shader
    // compilation) takes long enough that animations would otherwise jump ahead. Game time runs
    // slower than that in slow motion and faster in time warp, for everything: the flight, the
    // effects, the camera. Comma and full stop step through the rates, which are the flight's
    // (Flight.TimeScale).
    var last = Glfw.Time;
    var time = 0.0;
    var watched = new List<Vessel>(); // the small views' vessels, refilled each frame rather than made anew
    while (!window.ShouldClose && !hud.QuitAsked)
    {
        Glfw.PollEvents();

        var now = Glfw.Time;
        // Real time since the last frame (no more than a tenth of a second: after a stall, the
        // game carries on rather than leaping ahead), then the game time it asks for (paused,
        // what the views run by; flown, what the flight ran).
        var real = (float)Math.Min(now - last, 0.1);
        var dt = real * flight.TimeScale;
        last = now;

        // The menus: while one is up the game is paused. Esc opens the game menu (or closes it);
        // what was clicked in one last frame (a mission, a world, a restart) is done now.
        // Steering: the keys in the flown vessel's own frame, or a ring held by the mouse (which
        // shows while the keys are used too, following the nose).
        view.Navigation.KeysHeld = controls.Steering;
        var (ringHeading, ringPitch) = view.Navigation.TakeClick();
        var input = controls.Read(flight.Controlled) with { Hold = view.Navigation.Hold, RingHeading = ringHeading, RingPitch = ringPitch };
        if (controls.Take(ControlAction.GameMenu))
            hud.PressEsc(haveWorld: flight.WorldId.Length > 0 && flight.Chosen); // (one with nothing chosen in it is picked again, not flown)
        if (controls.Take(ControlAction.ToggleControlsList))
            hud.ToggleControls();
        if (controls.Take(ControlAction.ToggleZoomOut))
            view.ToggleZoomOut(flight);
        if (controls.Take(ControlAction.ToggleHud))
            hud.ToggleHidden();
        if (hud.TakeWorldRequest() is { } request)
            DoWorldRequest(request);
        if (hud.TakeSettingsChanged())
        {
            Timings.Enabled = hud.Settings.ShowTimings; // the same as before restarts nothing
            view.VSync = hud.Settings.CapFrameRate;     // nor makes the swapchain again
            try
            {
                store.SaveSettings(hud.Settings);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Couldn't save the settings: {e.Message}");
            }
        }
        // A marker double clicked last frame: the flown vessel's new target (or none).
        var (target, clearTarget) = hud.TakeTargetClick();
        var paused = hud.MenuOpen;
        // A small view clicked last frame flies its vessel.
        var swap = view.Views.TakeSwap();
        if (paused)
            flight.Hold();
        else
        {
            // The rate keys are taken only here, so one pressed while paused stays waiting and
            // changes the rate on the first frame after the pause.
            var rateKeys = controls.RateKeys(real);
            watched.Clear();
            var small = view.Views.Small;
            for (var i = 0; i < small.Count; i++) // by index: a foreach over the interface would box its enumerator every frame
                watched.Add(small[i].Vessel);
            // An item clicked on the flown vessel's actions menu goes in as its key would; not with
            // a switch to another vessel in the same input, which the flight takes first.
            var action = hud.TakeAction();
            var switching = swap is not null || input.SwitchVessel;
            var choice = hud.TakeChoice();
            var wasChosen = flight.Chosen;
            var given = VesselActions.Into(input, switching ? null : action) with
            {
                Choose = choice, From = LaunchSites.Named(hud.Settings.LaunchSite), CarryOn = hud.TakeCarryOn(),
                StartAgain = hud.TakeStartAgain(), Rewind = hud.TakeRewind(), CompleteRun = hud.TakeCompleteRun(), AbandonRun = hud.TakeAbandonRun(),
                SwitchTo = swap, SetTarget = target, ClearTarget = clearTarget,
                Plan = hud.TakePlan(), ChooseFrame = hud.TakeFrameChoice(), ChooseHold = hud.TakeHoldChoice(),
                Slower = rateKeys.Slower, Faster = rateKeys.Faster, Watched = watched,
                // Last frame's: what the main view showed, at the body what the camera is on is at, and the vessel it is on.
                Camera = view.CameraPlace.Position, CameraBody = view.CameraPlace.Body, CameraVelocity = view.CameraVelocity, Watching = view.WatchingNow(flight),
                NearMisses = hud.Settings.SlowNearMisses, WarpTo = hud.TakeWarpTo(),
                View = view.Camera.View,
            };
            // In warp, as many normal steps as fit in the budget, and however fast that turned out to be.
            dt = flight.RunFrame(real, given, Flight.WarpBudgetSeconds);
            if (flight.Chosen && !wasChosen)
                saveNow = true; // something carried on in a world just picked: saved as it now is
            if (flight.TimeScale > 1f)
                hud.RunningScale = real > 0f ? dt / real : flight.TimeScale;
        }
        hud.TimeScale = flight.TimeScale;
        // Who holds the warp to the physics' top rate, and why it last dropped there, for the time line.
        hud.WarpHeldBy = flight.WarpHeldBy is { } held ? WorldLog.Named(held) : null;
        hud.WarpHeldAirless = flight.WarpHeldBy is { Body.HasAir: false };
        hud.WarpDropped = flight.LastDrop;
        hud.RealTime = flight.RealTime;
        time += dt;

        if (saveNow || (!paused && now - lastSave > saveEverySeconds))
        {
            saveNow = false;
            Save();
        }

        view.Present(dt, time, flight, paused, real);
        readout?.Write(flight, hud);
    }
    Save();
}

Glfw.Terminate();
