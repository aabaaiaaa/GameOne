using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Text;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;

namespace GameOne;

/// <summary>Which of the HUD's menus is up: none (flying), the game menu (Esc), the missions, the world list, the settings or the world log.</summary>
internal enum MenuScreen
{
    None,
    Game,
    Missions,
    Worlds,
    Settings,
    Log,
}

/// <summary>
/// The on-screen overlay, drawn with Dear ImGui (BRUTAL's <c>Brutal.ImGui</c> bindings): the flight
/// instruments, the mission, the controls, a marker over the landing zone, and big messages for the
/// launch, a landing or a crash.
/// Its bottom-right panel credits BRUTAL as its licence asks, from the first frame onwards, while any
/// menu is up and for the first seconds of play (see <see cref="ShowsCredits"/>):
/// <list type="bullet">
///   <item>§2.1: the BRUTAL copyright notice, taken word for word from the NOTICE.txt shipped
///   next to the game (§2.3), so the HUD can never drift from the licensed wording;</item>
///   <item>§2.2: the official BRUTAL logo, shown unaltered except for scaling. (The logo on the
///   booster is decoration, wrapped round its body; this is the copy the licence's "unaltered"
///   wording applies to.)</item>
/// </list>
/// </summary>
internal sealed class Hud : IDisposable
{
    private const float FontSize = 15f;
    private const float BigFontSize = 44f;
    private const float PanelWidth = 200f; // the credits' text wraps to this, and the logo is scaled to it
    private const float CreditsFontSize = 12f; // the notice's text, smaller than the HUD's, to keep the panel small
    private const float Margin = 14f;

    private static readonly float4 Quiet = new(0.72f, 0.72f, 0.76f, 1f);
    internal static readonly float4 Good = new(0.45f, 0.92f, 0.45f, 1f);
    internal static readonly float4 Warning = new(1.0f, 0.75f, 0.2f, 1f);
    private static readonly float4 Bad = new(1.0f, 0.38f, 0.32f, 1f);
    private static readonly float4 NodeColor = new(Paths.PlanColor.R / 255f, Paths.PlanColor.G / 255f, Paths.PlanColor.B / 255f, 1f);

    /// <summary>The HUD's red for danger: the error sign's, and a collision coming at a controlled vessel (a small view's border flashing, the main view's mark, the compass's tick, the COLLISION line).</summary>
    internal static readonly ImColor8 Danger = new(235, 60, 50, 230);

    /// <summary>The HUD's amber for a warning: a near miss coming at a controlled vessel, shown where a collision is in <see cref="Danger"/>.</summary>
    internal static readonly ImColor8 Caution = new(255, 190, 50, 230);

    /// <summary>What is coming's colour: <see cref="Danger"/> for a collision, <see cref="Caution"/> for a near miss (safe if nothing is done).</summary>
    internal static ImColor8 ComingColor(in Coming coming) => coming.NearMiss ? Caution : Danger;

    private static readonly float4 DangerText = new(235 / 255f, 60 / 255f, 50 / 255f, 1f); // Danger's, for text

    /// <summary>
    /// Whether a flash at <see cref="Scene.ComingFlashHz"/> is on at <paramref name="realSeconds"/>
    /// of real time (on for the first half of each flash): by real time, not the world's, so it
    /// keeps its rate while the game is slowed for what it flashes for.
    /// </summary>
    internal static bool FlashOn(double realSeconds) => ((long)(realSeconds * 2 * Scene.ComingFlashHz) & 1) == 0;

    /// <summary>Used if NOTICE.txt is missing, e.g. when run from an unusual working layout.</summary>
    private const string FallbackNotice = """
        BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.

        This product is built with BRUTAL, a game development framework by Ahwoo Limited, used under the BRUTAL License v1.0.

        www.ahwoo.com/brutal
        """;

    private const ImGuiWindowFlags PanelFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoMove;

    private const ImGuiWindowFlags MenuFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoMove;

    private readonly ImGuiContextPtr _context;
    private readonly Paths _paths = new();
    private readonly Markers _markers;
    private readonly NodeEditor _nodes;
    private readonly ManoeuvrePlan _plan = new();
    private readonly GlfwBackend _platform;
    private readonly ImFontPtr _font;
    private readonly byte[][] _noticeLines; // UTF-8, converted once so drawing allocates nothing
    private ImTextureRef _logo;
    private bool _hasLogo;
    private float2 _logoSize;

    // For messages that show for a while after something happens.
    private double _liftedOffAt = double.MinValue;
    private bool _wasGrounded;
    private bool _wasTargetReached, _wasInOrbit;
    private double _targetReachedAt = double.MinValue, _orbitAt = double.MinValue, _stagedAt = double.MinValue;
    private double _failedAt = double.MinValue, _cameDownAt = double.MinValue;
    private bool _wasFailed, _wasDown;
    private int _lastStage;
    private bool _wasInAir = true;
    private double _leftAirAt = double.MinValue, _enteredAirAt = double.MinValue;
    private Mission? _chosen;
    private FrameChoice? _frameChosen;
    private Vessel? _lastControlled;
    private bool _hadFairing;
    private double _fairingAt = double.MinValue;

    /// <summary>Creates the ImGui context and hooks it up to the window's input. Create before the renderer.</summary>
    public Hud(GlfwWindow window)
    {
        _context = ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.IniFilename = default; // no imgui.ini: the HUD has nothing to remember

        _font = AddFont(io.Fonts);
        ApplyStyle();

        // ImGui's pointer is the one last seen in the window's cursor-move events, as the 3D view's
        // is (CLAUDE.md, "Input quirks"), not what the backend makes of it. With the window
        // focused and the pointer not over it (a test driver's posted moves, which leave the real
        // cursor where it is: Windows reports a leave after each), the backend puts the pointer
        // off the screen at the leave and, from the next frame, at the window's CursorPos, which
        // BRUTAL keeps from the last move but refills from the real cursor whenever the window is
        // repainted. So a press could be judged off the screen (a slow frame between the move and
        // it) or where the real cursor is (after a repaint, until a move to somewhere new; a
        // repeated move to the same spot gives none): a lost click. Told here, ahead of the
        // backend's own handlers (so subscribed before it is made), each button and wheel event
        // is judged where the pointer was last seen; and after the backend's frame starts, the
        // pointer is put back there for hover while the window has the focus. A player's pointer
        // is over the window when it clicks, so this is where the backend would have put it
        // anyway; one leaving the window (still focused) leaves the HUD's hover where it was, as
        // the 3D view's does.
        window.OnCursorPos += (_, pos) => _pointer = new Vector2((float)pos.X, (float)pos.Y);
        window.OnMouseButton += (_, _, _, _) => PointerToImGui();
        window.OnScroll += (_, _) => PointerToImGui();
        _window = window;
        _platform = new GlfwBackend(window, GlfwBackend.ClientApi.Vulkan);
        _nodes = new NodeEditor(window);
        _markers = new Markers(window);

        var notice = File.Exists(NoticePath) ? File.ReadAllText(NoticePath) : FallbackNotice;
        _noticeLines = notice
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Encoding.UTF8.GetBytes)
            .ToArray();
    }

    /// <summary>Where the pointer was last seen in the window's cursor-move events (none yet: the backend's own until then).</summary>
    private Vector2? _pointer;

    /// <summary>The window, to ask whether it has the focus.</summary>
    private readonly GlfwWindow _window;

    /// <summary>Tells ImGui the pointer is where it was last seen (see the constructor).</summary>
    private void PointerToImGui()
    {
        if (_pointer is { } p)
            ImGui.GetIO().AddMousePosEvent(p.X, p.Y);
    }

    private static string NoticePath => Path.Combine(AppContext.BaseDirectory, "NOTICE.txt");

    /// <summary>The official logo image, registered with the renderer. <paramref name="size"/> is its size in pixels.</summary>
    public void SetLogo(ImTextureRef logo, float2 size)
    {
        _logo = logo;
        _logoSize = size;
        _hasLogo = true;
    }

    /// <summary>A clean system UI font if there is one, else ImGui's built-in ProggyClean.</summary>
    internal static ImFontPtr AddFont(ImFontAtlasPtr fonts)
    {
        ReadOnlySpan<string> candidates =
        [
            @"C:\Windows\Fonts\segoeui.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/System/Library/Fonts/SFNS.ttf",
        ];
        foreach (var path in candidates)
            if (File.Exists(path))
                return fonts.AddFontFromFileTTF(path, FontSize);

        return fonts.AddFontDefault();
    }

    /// <summary>The HUD's look: its font size, rounding, borders and padding.</summary>
    internal static void ApplyStyle()
    {
        var style = ImGui.GetStyle();
        style.FontSizeBase = FontSize;
        style.WindowRounding = 6f;
        style.WindowBorderSize = 0f;
        style.WindowPadding = new float2(12, 10);
    }

    /// <summary>
    /// Which menu is up, if any: the game is paused while one is. The world list is up when the
    /// game starts, and picking a world goes on to the missions; after that, Esc opens the game
    /// menu, and the missions and the world list are reached from there.
    /// </summary>
    public MenuScreen Screen { get; private set; } = MenuScreen.Worlds;

    /// <summary>Whether any menu is up, pausing the game.</summary>
    public bool MenuOpen => Screen != MenuScreen.None;

    /// <summary>Closes whichever menu is up, back to flying.</summary>
    public void CloseMenu() => Screen = MenuScreen.None;

    public void OpenMissions() => Screen = MenuScreen.Missions;

    public void OpenWorlds() => Screen = MenuScreen.Worlds;

    /// <summary>
    /// Esc: the game menu from anywhere (flying, the missions, the world list), or out of it again,
    /// back to flying; or, when there is no world yet to fly in (<paramref name="haveWorld"/>), back
    /// to the world list.
    /// </summary>
    public void PressEsc(bool haveWorld) =>
        Screen = Screen != MenuScreen.Game ? MenuScreen.Game : haveWorld ? MenuScreen.None : MenuScreen.Worlds;

    /// <summary>The saved worlds to list (the game reads them from its <see cref="WorldStore"/> when they may have changed).</summary>
    public IReadOnlyList<WorldSummary> Worlds
    {
        get => _worlds;
        set
        {
            _worlds = value;
            _worldRows = [.. value.Select(WorldRowText.Of)];
        }
    }

    private IReadOnlyList<WorldSummary> _worlds = [];

    /// <summary>The player's settings (the game reads them from its <see cref="WorldStore"/>), as changed in the settings screen.</summary>
    public Settings Settings { get; set; } = new();

    /// <summary>Whether the settings were changed since the last call, for the game to save them.</summary>
    public bool TakeSettingsChanged()
    {
        var changed = _settingsChanged;
        _settingsChanged = false;
        return changed;
    }

    private bool _settingsChanged;

    /// <summary>What was clicked on the markers since the last call: a new target, or none wanted. For the flight's input.</summary>
    public (NavTarget? Set, bool Clear) TakeTargetClick() => _markers.TakeClick();

    /// <summary>The flown vessel's actions menu (a right click on it in the main view), drawn as a popup where it was clicked.</summary>
    private readonly ActionMenu _actions = new();

    /// <summary>The popup's opening last shown (<see cref="ActionMenu.Opened"/>): a new one opens it again where it was clicked.</summary>
    private int _actionsShown;

    /// <summary>The open popup's title: its vessel's name, escaped once as it opens.</summary>
    private string _actionsTitle = "";

    /// <summary>
    /// How large the actions popup is with what it shows now (its title, a separator, its items),
    /// as ImGui fits a window round them: the widest line and the window's padding across; down,
    /// the padding, a line for the title and each item, and the spacing between each of them and
    /// round the separator (which adds none of its own). Checked against ImGui's own in the game:
    /// the same, to the pixel.
    /// </summary>
    private Vector2 ActionsSize()
    {
        var style = ImGui.GetStyle();
        var width = ImGui.CalcTextSize(_actionsTitle).X;
        var items = _actions.Items;
        for (var i = 0; i < items.Count; i++)
            width = MathF.Max(width, ImGui.CalcTextSize(VesselActions.Label(items[i])).X);
        var lines = items.Count + 1;
        var height = lines * ImGui.GetFontSize() + lines * style.ItemSpacing.Y;
        return new Vector2(MathF.Ceiling(width + 2f * style.WindowPadding.X), MathF.Ceiling(height + 2f * style.WindowPadding.Y));
    }

    /// <summary>Each action's item as the readout names it (made once: the menu is drawn every frame it is open).</summary>
    private static readonly string[] ActionButtons = [.. Enum.GetValues<VesselAction>().Select(a => "Action: " + VesselActions.Label(a))];

    /// <summary>The item clicked on the flown vessel's actions menu since the last call, if any (only for the vessel it was chosen on, still flown): for the flight's input (<see cref="VesselActions.Into"/>).</summary>
    public VesselAction? TakeAction() => _actions.TakeChosen();

    /// <summary>Whether the flown vessel's actions menu is open: for the readout.</summary>
    public bool ActionsOpen => _actions.IsOpen;

    /// <summary>
    /// Where the flown vessel's centre of mass is on the main view this frame (pixels from the middle
    /// of the screen), while it is on the screen and not zoomed out: for the readout, so a script
    /// right-clicks it wherever it is.
    /// </summary>
    public Vector2? FlownVesselAt { get; private set; }

    /// <summary>The markers drawn on the screen this frame, by name and where each is (pixels from the middle of the screen): for the readout.</summary>
    public IReadOnlyList<(string Name, Vector2 At)> MarkersDrawn => _markers.Drawn;

    /// <summary>The coming mark on the vessel coming at the flown one drawn this frame, as <see cref="MarkersDrawn"/>; or none.</summary>
    public (string Name, Vector2 At)? ComingMark => _markers.ComingMark;

    /// <summary>
    /// The strip's buttons drawn this frame ("Add view", "Controls"; the folded list's "Controls"
    /// too), the open actions menu's items ("Action: Open bay doors"), and the mission menu's
    /// ("Mission: FREE PLAY", "Site: Home", "Change world"), by name and where each one's middle is (pixels from the middle of the screen): for the
    /// readout, as they move with the instruments' panel (<see cref="StripPlace"/>) and the mission menu's height.
    /// </summary>
    public IReadOnlyList<(string Name, Vector2 At)> ButtonsDrawn => _buttons;

    /// <summary>The vessels listed in the open "Add view" list this frame, and where each entry's middle is (as <see cref="ButtonsDrawn"/>): for the readout.</summary>
    public IReadOnlyList<(Vessel Vessel, Vector2 At)> ViewsOffered => _offered;

    private readonly List<(string Name, Vector2 At)> _buttons = [];
    private readonly List<(Vessel Vessel, Vector2 At)> _offered = [];

    /// <summary>The middle of the item just drawn, in pixels from the middle of the screen.</summary>
    private static Vector2 ItemMiddle()
    {
        var (min, max, display) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetIO().DisplaySize);
        return new Vector2((min.X + max.X - display.X) / 2f, (min.Y + max.Y - display.Y) / 2f);
    }

    /// <summary>How many predicted collisions' crosses the map drew on the screen this frame: for the readout.</summary>
    public int CollisionsDrawn => _paths.CollisionsDrawn;

    /// <summary>Whether the pointer was over a marker, or the path or a node to plan with, last frame, so a click there isn't for the steering rings.</summary>
    public bool PointerOnMarker => _markers.PointerOver || _nodes.PointerOver;

    /// <summary>The flown vessel's frame has changed to another body's (<see cref="Flight.ViewRebased"/>): what the paths keep from frame to frame in the old one is dropped (<see cref="Paths.Rebase"/>), so the change itself isn't needed.</summary>
    public void Rebase(FrameChange _) => _paths.Rebase();

    /// <summary>The flown vessel's nodes, if they were placed, moved, changed or deleted since the last call: for the flight's input.</summary>
    public ManoeuvreNode[]? TakePlan() => _nodes.TakePlan();

    /// <summary>Whether the game menu's Quit has been clicked: the game saves and closes.</summary>
    public bool QuitAsked { get; private set; }

    private bool _restart;

    /// <summary>Whether the game menu's "Restart flight" was clicked since the last call, for the flight to undo itself.</summary>
    public bool TakeRestart()
    {
        var restart = _restart;
        _restart = false;
        return restart;
    }

    private WorldRequest? _worldRequest;
    private string? _renaming, _confirming;
    private bool _focusName;
    private WorldRequestKind _confirmKind;
    private readonly byte[] _nameBuffer = new byte[64];

    /// <summary>What the player asked of the world list since the last call, if anything, for the game to do.</summary>
    public WorldRequest? TakeWorldRequest()
    {
        var request = _worldRequest;
        _worldRequest = null;
        return request;
    }

    /// <summary>Picks a mission from the menu, and closes the menu.</summary>
    public void ChooseMission(Mission mission)
    {
        _chosen = mission;
        CloseMenu();
    }

    /// <summary>The frame of reference picked on the selector since the last call, if one was, for the flight's input.</summary>
    public FrameChoice? TakeFrameChoice()
    {
        var chosen = _frameChosen;
        _frameChosen = null;
        return chosen;
    }

    /// <summary>The mission picked since the last call, if one was, for the flight to start.</summary>
    public Mission? TakeChoice()
    {
        var chosen = _chosen;
        _chosen = null;
        return chosen;
    }

    /// <summary>A line to show at the bottom of the screen, above everything, or null for none: for whatever is driving the view to say what it is doing.</summary>
    public string? Caption { get; set; }

    /// <summary>How fast game time runs (the flight's <see cref="Flight.TimeScale"/>, given each frame): 1, less in slow motion, more in warp. Shown while it isn't 1.</summary>
    public float TimeScale { get; set; } = 1f;

    private float _running = 1f;

    /// <summary>
    /// In time warp, how fast the world actually ran this frame (it may not keep up with
    /// <see cref="TimeScale"/> in a busy world). Smoothed for showing.
    /// </summary>
    public float RunningScale
    {
        get => _running;
        set => _running += (value - _running) * 0.1f;
    }

    /// <summary>The big message in the middle of the screen last frame, if any: its title and lines (for the <see cref="Readout"/>).</summary>
    public (string? Title, List<(string Text, float4 Color)>? Lines) LastMessage { get; private set; }

    /// <summary>Whether to list the player's controls (bottom left): not when something else is flying.</summary>
    public bool ShowControls { get; set; } = true;

    /// <summary>
    /// Whether the controls list is open: folded to one line ("Controls (H)") until H, or a click
    /// on that line, opens it. Once the rocket has come apart, the open list takes the small
    /// views' place in the bottom left, as they took its.
    /// </summary>
    private bool _controlsOpen;

    /// <summary>Whether the controls list is open (see <see cref="ToggleControls"/>), for the <see cref="Readout"/>.</summary>
    public bool ControlsOpen => _controlsOpen;

    /// <summary>How tall the open controls list is, at the size it is placed at this frame (see <see cref="OpenListPlace"/>), to lay the strip's buttons out above it.</summary>
    private float _controlsHeight = 230f;

    /// <summary>Opens the controls list (in place of the small views, if there are any), or folds it again.</summary>
    public void ToggleControls() => _controlsOpen = !_controlsOpen;

    /// <summary>
    /// Whether the HUD is hidden (F2, <see cref="ToggleHidden"/>), for a screenshot of the world
    /// alone: nothing is drawn over the main view (the credits, small views and markers too) but a
    /// menu, which shows in full while it is up, and the error sign. Not saved: each run starts with
    /// it shown, so the credits are on the screen before play begins, as BRUTAL's licence asks.
    /// </summary>
    public bool Hidden { get; private set; }

    /// <summary>Hides the HUD, or shows it again (see <see cref="Hidden"/>).</summary>
    public void ToggleHidden() => Hidden = !Hidden;

    /// <summary>When play first began this run (no menu up), as a <see cref="System.Diagnostics.Stopwatch"/> timestamp: 0 until it has.</summary>
    private long _playingSince;

    /// <summary>Whether the credits panel is drawn this frame (see <see cref="ShowsCredits"/>), for the small views' room too.</summary>
    private bool _creditsShown = true;

    /// <summary>
    /// Whether the credits panel shows: while a menu is up (the world list the game starts on among
    /// them, so it is on the screen before play begins, as BRUTAL's licence asks of the logo, and
    /// in the game menu whenever the notice is wanted), and for <see cref="Scene.CreditsSeconds"/>
    /// of real time once play first begins (<paramref name="playingFor"/>, null until it has); then
    /// it gives its room back.
    /// </summary>
    internal static bool ShowsCredits(bool menuOpen, double? playingFor) =>
        menuOpen || playingFor is not { } seconds || seconds < Scene.CreditsSeconds;

    /// <summary>
    /// Starts an ImGui frame and lays out the HUD, small views and all (where each goes, and what
    /// is clicked on them, are put in <paramref name="views"/>), each body's place as <paramref name="frames"/> has
    /// it this frame (the markers, the paths and the small views' marks are in the view's frame). Follow with <see cref="ImGui.Render"/>.
    /// </summary>
    public void Build(double time, Flight flight, ChaseCamera camera, NavigationControl navigation, Views views, BodyFrames frames)
    {
        // Every string handed to ImGui (each $"..." here) is written into BRUTAL's shared 8 MB
        // ImString arena, which nothing empties on its own: without this it fills up after a few
        // minutes of frames and throws. ImGui has copied last frame's text into its draw lists,
        // so it is safe to empty now.
        ImString.SharedStorage.Clear();
        _buttons.Clear();
        _offered.Clear();
        _platform.NewFrame();
        // Over the backend's guess (see the constructor), while the window has the focus: once
        // the player has gone to another window, the backend's pointer off it ends the HUD's hover
        // and tooltips there.
        if (_window.GetAttribute(GlfwWindowAttribute.Focused) != 0)
            PointerToImGui();
        ImGui.NewFrame();

        // Flying another vessel isn't news in itself: its stage, height and orbit are where it was
        // left, so nothing is announced as though it just happened.
        if (flight.Controlled != _lastControlled)
        {
            _lastControlled = flight.Controlled;
            _lastStage = flight.Stage;
            _wasTargetReached = flight.ReachedTarget;
            _wasInOrbit = flight.AchievedOrbit;
            _wasInAir = !flight.Controlled.Body.AboveAir(flight.Position);
            _hadFairing = flight.FairingOn;
            (_wasGrounded, _liftedOffAt) = (flight.Controlled.Grounded, double.MinValue); // no "LIFT-OFF" for one already flying
            _frameSeen = null; // nor its frame lit as a change
            (_wasDown, _cameDownAt) = (Down(flight.Controlled), double.MinValue); // nor one already down as just come down
        }
        if (_hadFairing && !flight.FairingOn)
            _fairingAt = time;
        _hadFairing = flight.FairingOn;
        // Lift-off: the moment it leaves the ground (the pad, or wherever it landed) under power, not
        // the moment Space is pressed or a landed one is staged.
        if (_wasGrounded && !flight.Controlled.Grounded && flight.Controlled.EngineLit)
            _liftedOffAt = time;
        _wasGrounded = flight.Controlled.Grounded;
        if (flight.ReachedTarget && !_wasTargetReached)
            _targetReachedAt = time;
        _wasTargetReached = flight.ReachedTarget;
        if (flight.AchievedOrbit && !_wasInOrbit)
            _orbitAt = time;
        _wasInOrbit = flight.AchievedOrbit;
        var failed = flight.Mission.Failed(flight);
        if (failed && !_wasFailed)
            _failedAt = time;
        _wasFailed = failed;
        var down = Down(flight.Controlled);
        if (down && !_wasDown)
            _cameDownAt = time;
        _wasDown = down;
        if (flight.Stage > _lastStage)
            _stagedAt = time;
        _lastStage = flight.Stage;
        var inAir = !flight.Controlled.Body.AboveAir(flight.Position);
        if (inAir != _wasInAir && flight.State == FlightState.Flying)
            (inAir ? ref _enteredAirAt : ref _leftAirAt) = time;
        _wasInAir = inAir;

        var display = ImGui.GetIO().DisplaySize;
        FlownVesselAt = null;
        if (display.X <= 0 || display.Y <= 0)
            return; // minimised: nothing to lay out
        if (Hidden && !MenuOpen)
        {
            // Nothing to show the actions on, nor to click: a right click is let go, not kept.
            _actions.Close();
            camera.TakeRightClick();
            HiddenFrame(views, display);
            return;
        }
        if (!MenuOpen && _playingSince == 0)
            _playingSince = System.Diagnostics.Stopwatch.GetTimestamp();
        _creditsShown = ShowsCredits(MenuOpen, _playingSince == 0 ? null : System.Diagnostics.Stopwatch.GetElapsedTime(_playingSince).TotalSeconds);
        // The path, as the flown vessel's nodes plan it (as they are being changed, if they are);
        // then the nodes over it, and the markers (and over them the coming mark on what is coming,
        // whatever their settings), which give way to whichever has the pointer.
        var vessel = flight.Controlled;
        _nodes.Follow(vessel);
        var firstLeft = _nodes.FirstUnchanged && vessel.BurnBegun ? vessel.BurnLeft(flight.WorldTime) : null;
        _plan.Update(vessel.Body, vessel.CentreOfMass, vessel.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity), flight.WorldTime, _nodes.Nodes, firstLeft);
        _paths.Labels.Clear();
        _paths.Draw(ImGui.GetBackgroundDrawList(), flight, camera, new Vector2(display.X, display.Y), _plan, frames);
        _nodes.Update(ImGui.GetBackgroundDrawList(), flight, camera, _plan, _paths, blocked: MenuOpen || _markers.PointerOver || navigation.InUse);
        _markers.Draw(ImGui.GetBackgroundDrawList(), flight, camera, new Vector2(display.X, display.Y), Settings, navigation.InUse || _nodes.PointerOver, MenuOpen, frames, _paths.Labels);
        // The path's and the markers' labels together, over them all; a pile under the pointer
        // spread, but not from under a panel or a menu.
        _paths.Labels.Draw(ImGui.GetBackgroundDrawList(), MenuOpen || ImGui.GetIO().WantCaptureMouse ? null : _markers.Pointer, new Vector2(display.X, display.Y));
        navigation.Draw(ImGui.GetBackgroundDrawList());
        if (!camera.ZoomedOut) // which way the camera faces over the ground means nothing from out there
            Compass(flight, camera, display);
        // The flight takes a frame picked on the selector only when it isn't paused, so one picked
        // just as a menu came up would wait, and land after the pause, or in the next flight past
        // its start on Auto. While a menu is up the selector is off, and any such choice let go.
        if (MenuOpen)
            _frameChosen = null;
        Instruments(flight);
        _openList = OpenListPlace(flight, display);
        MissionPanel(flight, display);
        if (Settings.ShowTimings)
            TimingsPanel(display);
        else
            _timingsRect = RectangleF.Empty;
        var strip = Strip(flight, views, display, frames);
        LinkUp(flight.LinkUp, display, strip);
        Actions(flight, camera, new Vector2(display.X, display.Y));
        LastMessage = default;
        // (Kept for the timings panel's next frame, as each is drawn after it: empty unless drawn.)
        _messageRect = _controlsRect = _captionRect = RectangleF.Empty;
        switch (Screen)
        {
            case MenuScreen.Game:
                GameMenu(flight, display);
                break;
            case MenuScreen.Missions:
                Menu(flight, display);
                break;
            case MenuScreen.Worlds:
                WorldList(flight, display);
                break;
            case MenuScreen.Log:
                LogScreen(flight, display);
                break;
            case MenuScreen.Settings:
                SettingsScreen(display);
                break;
            default:
                Message(time, flight, display);
                break;
        }
        // Not while a menu is up: the game is paused, the keys are for flying, and the list (or its
        // folded line, bottom left) would lie over a tall menu's foot and take its clicks.
        if (ShowControls && !MenuOpen && _controlsOpen)
            ControlsHelp(display);
        else if (ShowControls && !MenuOpen && strip is null)
            ControlsFolded(display); // (with the small views up, their row has the button)
        if (_creditsShown)
            Credits(display);
        else
            _creditsRect = RectangleF.Empty;
        if (Caption is { } caption)
            CaptionLine(caption, display, strip ?? display.Y - Margin);
        ErrorSign(display);
    }

    /// <summary>
    /// A frame with the HUD hidden: no small views drawn, nothing over the main view to point at or
    /// click (a click on a marker, a node or a view is let go, not kept for when it shows again),
    /// and nothing said in the middle; only the error sign, so an error still isn't missed.
    /// </summary>
    private void HiddenFrame(Views views, float2 display)
    {
        foreach (var view in views.Small)
            view.Size = Vector2.Zero;
        _stripRect = RectangleF.Empty;
        _markers.Hide();
        _paths.Labels.Forget();
        _nodes.Hide();
        LastMessage = default;
        _messageRect = _controlsRect = _captionRect = _timingsRect = _creditsRect = RectangleF.Empty;
        ErrorSign(display);
    }

    /// <summary>
    /// The flown vessel's actions menu: kept or closed as the flight has changed (<see cref="ActionMenu.Keep"/>),
    /// opened by a right click on the vessel in the main view (the camera's, so not on a panel or a
    /// small view; not under a game menu or zoomed out), and drawn as a popup where it was clicked,
    /// its items recorded among the buttons for the readout. ImGui closes the popup on a click
    /// elsewhere, and the menu closes with it.
    /// </summary>
    private void Actions(Flight flight, ChaseCamera camera, Vector2 display)
    {
        var vessel = flight.Controlled;
        if (!camera.ZoomedOut && camera.ToScreen(vessel.CentreOfMass, display, out var screen, out _)
            && screen.X >= 0f && screen.Y >= 0f && screen.X <= display.X && screen.Y <= display.Y)
            FlownVesselAt = screen - display / 2f;
        _actions.Keep(flight, MenuOpen, camera.ZoomedOut);
        if (camera.TakeRightClick() is { } click && !MenuOpen && !camera.ZoomedOut)
        {
            var (bottom, top) = vessel.AxisOffsets(vessel.Rotation);
            if (VesselActions.OnVessel(vessel.CentreOfMass + bottom, vessel.CentreOfMass + top, vessel.Width, camera.ViewProjection(display), display, click))
                _actions.Open(vessel, click); // none for a vessel with no actions
            else
                _actions.Close();
        }
        if (_actions.IsOpen)
        {
            if (_actionsShown != _actions.Opened)
            {
                ImGui.OpenPopup("##actions"u8);
                _actionsShown = _actions.Opened;
                _actionsTitle = Escape(_actions.Vessel!.Name); // (TextColored's is a printf format)
            }
            // At the click, but kept on the screen by its size (ImGui keeps no window placed by
            // hand on it), so one opened by the right or bottom edge opens in from it: its size
            // worked out from what it shows, as ImGui fits it, so its first frame is placed right
            // (ImGui's own size for it is known only once it has been drawn).
            var place = ActionMenu.Place(_actions.At, ActionsSize(), display, Margin);
            ImGui.SetNextWindowPos(new float2(place.X, place.Y));
            if (ImGui.BeginPopup("##actions"u8))
            {
                ImGui.TextColored(Quiet, _actionsTitle);
                ImGui.Separator();
                var items = _actions.Items;
                for (var i = 0; i < items.Count; i++)
                {
                    var action = items[i];
                    var chosen = ImGui.Selectable(VesselActions.Label(action));
                    _buttons.Add((ActionButtons[(int)action], ItemMiddle()));
                    if (chosen)
                    {
                        _actions.Choose(action); // (which empties its items: the last one drawn)
                        break;
                    }
                }
                ImGui.EndPopup();
            }
            else
                _actions.Close(); // a click elsewhere
        }
        else if (ImGui.BeginPopup("##actions"u8))
        {
            ImGui.CloseCurrentPopup(); // closed by the flight changing under it (see ActionMenu.Keep)
            ImGui.EndPopup();
        }
    }

    private uint _linkFrames; // the link-ups' static, a new pattern each time one is drawn

    /// <summary>
    /// The communications link-up over the main view (above the small views' row at
    /// <paramref name="strip"/>), while <paramref name="link"/> is on (see <see cref="GameOne.LinkUp"/>,
    /// whose state this only draws): the picture broken up into static, torn bands across it,
    /// "ESTABLISHING LINK" and the vessel's name, all clearing as it connects. Flavour only: it
    /// says nothing to do.
    /// </summary>
    private void LinkUp(LinkUp link, float2 display, float? strip)
    {
        if (link.Vessel is not null)
            LinkStatic(ImGui.GetBackgroundDrawList(), link, new float2(0, 0), new float2(display.X, strip ?? display.Y), 64, 36, 1.6f, ++_linkFrames);
    }

    /// <summary>
    /// <paramref name="link"/>'s picture broken up over the rectangle from <paramref name="min"/> to
    /// <paramref name="max"/> (the main view's, or a small view's under its label), into
    /// <paramref name="draw"/>: a grid of <paramref name="columns"/> by <paramref name="rows"/> grey
    /// specks, each <paramref name="frame"/>'s own, torn bands across it, and the title (at
    /// <paramref name="titleScale"/> times the HUD's text) and the vessel's name, all as faded as
    /// it has cleared.
    /// </summary>
    private void LinkStatic(ImDrawListPtr draw, LinkUp link, float2 min, float2 max, int columns, int rows, float titleScale, uint frame)
    {
        var shown = link.Static;
        if (link.Vessel is not { } vessel || shown <= 0f)
            return;
        var size = max - min;
        draw.AddRectFilled(min, max, new ImColor8(6, 8, 10, (byte)(210 * shown)));
        // The static: a grid of grey specks, each frame's own.
        var (w, h) = (size.X / columns, size.Y / rows);
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var noise = Noise(frame, (uint)(row * columns + column));
                var grey = (byte)(noise & 0xFF);
                var alpha = (byte)(((noise >> 8) & 0x9F) * shown);
                draw.AddRectFilled(min + new float2(column * w, row * h), min + new float2((column + 1) * w, (row + 1) * h), new ImColor8(grey, grey, grey, alpha));
            }
        // A few torn bands across it, where the picture slips.
        for (var band = 0u; band < 5; band++)
        {
            var noise = Noise(frame, 10_000 + band);
            var y = min.Y + noise % (uint)Math.Max(1f, size.Y);
            var thick = 2 + (noise >> 20) % 9;
            draw.AddRectFilled(new float2(min.X, y), new float2(max.X, MathF.Min(y + thick, max.Y)), new ImColor8(200, 210, 220, (byte)(90 * shown)));
        }
        var text = new ImColor8(170, 230, 200, (byte)(255 * shown));
        var title = link.Connecting ? "ESTABLISHING LINK" : "LINK ESTABLISHED";
        var titleSize = ImGui.CalcTextSize(title) * titleScale;
        var middle = min + size / 2;
        draw.AddText(_font, FontSize * titleScale, middle - new float2(titleSize.X / 2, titleSize.Y), text, title);
        var name = vessel.Name;
        var nameSize = ImGui.CalcTextSize(name);
        draw.AddText(middle + new float2(-nameSize.X / 2, 6f), text, name);

        // A hash of the frame and a speck's number: the static's pattern, allocating nothing.
        static uint Noise(uint frame, uint i)
        {
            var x = frame * 0x9E3779B9u ^ i * 0x85EBCA6Bu;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            return x ^ (x >> 16);
        }
    }

    /// <summary>
    /// The middle of the screen, over everything, while anything has been logged as an error in
    /// the last <see cref="ErrorLog.RecentSeconds"/>: a warning sign and how many. No detail (that
    /// is on the console), just enough that an error isn't missed, and that a run of them shows.
    /// </summary>
    private void ErrorSign(float2 display)
    {
        var count = ErrorLog.Recent;
        if (count == 0)
            return;
        var draw = ImGui.GetForegroundDrawList();
        var centre = new float2(display.X / 2, display.Y / 2);
        const float size = 34f;
        var red = Danger;
        var top = centre + new float2(0, -size);
        var left = centre + new float2(-size * 1.15f, size * 0.85f);
        var right = centre + new float2(size * 1.15f, size * 0.85f);
        draw.AddTriangleFilled(top, right, left, red);
        draw.AddTriangle(top, right, left, new ImColor8(255, 255, 255, 230), 2f);
        var mark = ImGui.CalcTextSize("!") * 2f;
        draw.AddText(_font, FontSize * 2f, centre + new float2(-mark.X / 2, -mark.Y * 0.35f), new ImColor8(255, 255, 255, 255), "!");
        var label = count == 1 ? "1 error" : $"{count} errors";
        var labelSize = ImGui.CalcTextSize(label);
        var labelAt = centre + new float2(-labelSize.X / 2, size + 8f);
        draw.AddRectFilled(labelAt - new float2(8, 3), labelAt + labelSize + new float2(8, 3), new ImColor8(20, 20, 24, 220), 4f);
        draw.AddText(labelAt, new ImColor8(255, 120, 110, 255), label);
    }

    /// <summary>Bottom middle, between the controls and the credits (or over the small views): the <see cref="Caption"/>.</summary>
    private void CaptionLine(string caption, float2 display, float bottom)
    {
        ImGui.SetNextWindowPos(new float2(display.X / 2, bottom), ImGuiCond.Always, new float2(0.5f, 1f));
        ImGui.SetNextWindowBgAlpha(0.7f);
        if (ImGui.Begin("##caption"u8, PanelFlags))
        {
            ImGui.TextColored(Warning, Escape(caption));
            _captionRect = ThisWindow();
        }
        ImGui.End();
    }

    /// <summary>
    /// The middle of the screen, on Esc, with the game paused: what is being played, and
    /// everything about the game rather than the flight (restart, missions, worlds, quit).
    /// Before any world is picked there is nothing to resume or restart, so it offers the world
    /// list and Quit only.
    /// </summary>
    private void GameMenu(Flight flight, float2 display)
    {
        switch (GameMenuWindow(display, _font, flight.WorldId.Length > 0 ? flight.WorldName : null, flight.Mission.Name))
        {
            case GameMenuChoice.Resume:
                CloseMenu();
                break;
            case GameMenuChoice.Restart:
                _restart = true;
                CloseMenu();
                break;
            case GameMenuChoice.Missions:
                OpenMissions();
                break;
            case GameMenuChoice.Log:
                Screen = MenuScreen.Log;
                break;
            case GameMenuChoice.Worlds:
                OpenWorlds();
                break;
            case GameMenuChoice.Settings:
                Screen = MenuScreen.Settings;
                break;
            case GameMenuChoice.Quit:
                QuitAsked = true;
                break;
        }
    }

    /// <summary>What was clicked in the game menu this frame, if anything.</summary>
    internal enum GameMenuChoice { None, Resume, Restart, Missions, Log, Worlds, Settings, Quit }

    /// <summary>
    /// The game menu's window (see <see cref="GameMenu"/>) on a screen <paramref name="display"/> in
    /// size, its title in <paramref name="font"/>: the world being played (<paramref name="world"/>,
    /// none before one is picked) and its <paramref name="mission"/>, wrapped to the menu's width so
    /// a long name never widens it over the credits. Returns what was clicked.
    /// </summary>
    internal static GameMenuChoice GameMenuWindow(float2 display, ImFontPtr font, string? world, string mission)
    {
        var (width, across) = MenuAcross(display, GameMenuWidth, GameMenuWidth);
        var choice = GameMenuChoice.None;
        ImGui.SetNextWindowPos(new float2(across, display.Y * 0.45f), ImGuiCond.Always, new float2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.85f);
        if (ImGui.Begin(GameMenuName, MenuFlags))
        {
            ImGui.PushFont(font, 30f);
            Centred("PAUSED", new float4(1, 1, 1, 1));
            ImGui.PopFont();
            ImGui.Separator();
            if (world is not null)
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
                ImGui.TextColored(Quiet, Escape($"World:    {world}"));
                ImGui.TextColored(Quiet, Escape($"Mission:  {mission}"));
                ImGui.PopTextWrapPos();
                ImGui.Spacing();
                if (ImGui.Button("Resume (Esc)"u8, new float2(width, 30)))
                    choice = GameMenuChoice.Resume;
                if (ImGui.Button("Restart flight"u8, new float2(width, 30)))
                    choice = GameMenuChoice.Restart;
                if (ImGui.Button("Missions..."u8, new float2(width, 30)))
                    choice = GameMenuChoice.Missions;
                if (ImGui.Button("World log..."u8, new float2(width, 30)))
                    choice = GameMenuChoice.Log;
            }
            if (ImGui.Button(world is not null ? "Change world..."u8 : "Choose a world... (Esc)"u8, new float2(width, 30)))
                choice = GameMenuChoice.Worlds;
            if (ImGui.Button("Settings..."u8, new float2(width, 30)))
                choice = GameMenuChoice.Settings;
            ImGui.Spacing();
            if (ImGui.Button("Quit game"u8, new float2(width, 30)))
                choice = GameMenuChoice.Quit;
        }
        ImGui.End();
        return choice;
    }

    /// <summary>The game menu's window's name.</summary>
    internal static ReadOnlySpan<byte> GameMenuName => "##game"u8;

    /// <summary>
    /// The middle of the screen, from the game menu: every vessel lost in the world, newest first
    /// (when, which, how, how fast, where), the ones nobody saw dimmer. Scrolls; "Close" goes back
    /// to the game menu. Its lines are built once each time the log changes (<see cref="LogLines"/>),
    /// not each frame: up to <see cref="Scene.WorldLogEntries"/> strings a frame, while it is up.
    /// </summary>
    // The log screen's lines, oldest first, as of the flight's LogChanges they were built at.
    private readonly List<(string Text, bool Seen)> _logLines = [];
    private Flight? _logLinesOf;
    private int _logLinesAt = -1;

    /// <summary>
    /// The log's entries as the log screen prints them (escaped for <c>TextColored</c>), oldest
    /// first, built again only when the log has changed: when, which, how, how fast (none for one
    /// removed, which nothing hit), where, and whether it was seen.
    /// </summary>
    internal IReadOnlyList<(string Text, bool Seen)> LogLines(Flight flight)
    {
        if (flight == _logLinesOf && flight.LogChanges == _logLinesAt)
            return _logLines;
        (_logLinesOf, _logLinesAt) = (flight, flight.LogChanges);
        _logLines.Clear();
        foreach (var e in flight.Log)
            _logLines.Add((Escape(LogLine(e)), e.Witnessed));
        return _logLines;
    }

    /// <summary>One entry of the log as its screen words it (see <see cref="LogLines"/>).</summary>
    internal static string LogLine(LogEntry e) =>
        $"{Sun.WorldClock(e.Time)}  {e.What}: {e.How}{(e.Speed is { } speed ? $", {speed:0} m/s" : "")}, {e.Where}{(e.Witnessed ? "" : " (unseen)")}";

    private void LogScreen(Flight flight, float2 display)
    {
        var lines = LogLines(flight);
        var (width, across) = MenuAcross(display, LogWidth, LogWidth);
        ImGui.SetNextWindowPos(new float2(across, display.Y * 0.45f), ImGuiCond.Always, new float2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.85f);
        if (ImGui.Begin("##log"u8, MenuFlags))
        {
            ImGui.PushFont(_font, 30f);
            Centred("WORLD LOG", new float4(1, 1, 1, 1));
            ImGui.PopFont();
            ImGui.Separator();
            var log = flight.Log;
            if (log.Count == 0)
                ImGui.TextColored(Quiet, "Nothing has been lost in this world."u8);
            if (ImGui.BeginChild("##logList"u8, new float2(width, Math.Min(360f, display.Y * 0.5f))))
            {
                // Wrapped to the list's width, which is narrower on a small screen (beside the credits).
                ImGui.PushTextWrapPos(0f);
                for (var i = lines.Count - 1; i >= 0; i--)
                    ImGui.TextColored(lines[i].Seen ? new float4(1, 1, 1, 1) : Quiet, lines[i].Text);
                ImGui.PopTextWrapPos();
            }
            ImGui.EndChild();
            ImGui.Spacing();
            if (ImGui.Button("Close"u8, new float2(width, 26)))
                Screen = MenuScreen.Game;
        }
        ImGui.End();
    }

    /// <summary>
    /// The middle of the screen, from the game menu: the player's settings, the same in every
    /// world. So far, which markers show (all of them, or none, and each kind), the timings panel,
    /// whether frames are capped at the screen's rate, and whether near misses are slowed.
    /// </summary>
    private void SettingsScreen(float2 display)
    {
        var (width, across) = MenuAcross(display, SettingsWidth, SettingsWidth);
        ImGui.SetNextWindowPos(new float2(across, display.Y * 0.45f), ImGuiCond.Always, new float2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.85f);
        if (ImGui.Begin("##settings"u8, MenuFlags))
        {
            ImGui.PushFont(_font, 30f);
            Centred("SETTINGS", new float4(1, 1, 1, 1));
            ImGui.PopFont();
            ImGui.Separator();
            var s = Settings;
            var (all, bodies, thisFlight, earlier, debris, sites, timings, capped, nearMisses) =
                (s.Markers, s.BodyMarkers, s.ThisFlightMarkers, s.EarlierFlightMarkers, s.DebrisMarkers, s.SiteMarkers, s.ShowTimings, s.CapFrameRate, s.SlowNearMisses);
            var changed = ImGui.Checkbox("Markers"u8, ref all);
            ImGui.Indent();
            ImGui.BeginDisabled(!all);
            changed |= ImGui.Checkbox("Planets and moons"u8, ref bodies);
            changed |= ImGui.Checkbox("This flight's vessels"u8, ref thisFlight);
            changed |= ImGui.Checkbox("Earlier flights' vessels"u8, ref earlier);
            changed |= ImGui.Checkbox("Debris (fairing halves)"u8, ref debris);
            changed |= ImGui.Checkbox("Launch pad and landing zones"u8, ref sites);
            ImGui.EndDisabled();
            ImGui.Unindent();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(Quiet, "Your target and the mission's landing site are always marked, unless markers are off."u8);
            ImGui.PopTextWrapPos();
            ImGui.Separator();
            changed |= ImGui.Checkbox("Show timings"u8, ref timings);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(Quiet, "Where each frame's time goes: the physics, the flight's rules, drawing and the GPU."u8);
            ImGui.PopTextWrapPos();
            changed |= ImGui.Checkbox("Cap frames at the screen's rate"u8, ref capped);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(Quiet, "Waits for the display (vsync): cooler and no tearing. Off, it draws as fast as it can."u8);
            ImGui.PopTextWrapPos();
            changed |= ImGui.Checkbox("Slow motion for near misses"u8, ref nearMisses);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(Quiet, "Slows the game as another vessel passes yours or a small view's within about its length, too fast to see."u8);
            ImGui.PopTextWrapPos();
            if (changed)
            {
                Settings = s with
                {
                    Markers = all, BodyMarkers = bodies, ThisFlightMarkers = thisFlight,
                    EarlierFlightMarkers = earlier, DebrisMarkers = debris, SiteMarkers = sites,
                    ShowTimings = timings, CapFrameRate = capped, SlowNearMisses = nearMisses,
                };
                _settingsChanged = true;
            }
            ImGui.Spacing();
            if (ImGui.Button("Close"u8, new float2(width, 26)))
                CloseMenu();
        }
        ImGui.End();
    }

    /// <summary>
    /// The mission menu's "Launch from" row: a button for each launch site, in its tower's colour (the
    /// one picked bright and outlined, the rest dim), and a line on the one picked. The pick is the player's
    /// (<see cref="Settings.LaunchSite"/>): it stays until they pick another, whatever mission they choose.
    /// </summary>
    private void SiteRow(float width, double worldTime)
    {
        var picked = LaunchSites.Named(Settings.LaunchSite);
        ImGui.TextColored(Quiet, "Launch from:"u8);
        var right = ImGui.GetCursorPosX() + width;
        for (var i = 0; i < LaunchSites.All.Length; i++)
        {
            var site = LaunchSites.All[i];
            var size = ImGui.CalcTextSize(site.Name).X + 2 * ImGui.GetStyle().FramePadding.X;
            if (i > 0 && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + size < ImGui.GetWindowPos().X + right)
                ImGui.SameLine();
            var isPicked = site == picked;
            var colour = site.TowerColour * (isPicked ? 1f : 0.45f);
            var hovered = site.TowerColour * (isPicked ? 1f : 0.65f);
            // Dark text on a light fill (Polar's white, Highcrest's yellow), so the pick stays legible.
            var light = Vector3.Dot(colour, new Vector3(0.299f, 0.587f, 0.114f)) > 0.55f;
            ImGui.PushStyleColor(ImGuiCol.Button, new float4(colour.X, colour.Y, colour.Z, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new float4(hovered.X, hovered.Y, hovered.Z, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, light ? new float4(0.08f, 0.08f, 0.08f, 1f) : new float4(1, 1, 1, 1));
            ImGui.PushStyleColor(ImGuiCol.Border, new float4(1, 1, 1, 1));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, isPicked ? 1.5f : 0f);
            if (ImGui.SmallButton($"{site.Name}###site{i}") && !isPicked)
            {
                Settings = Settings with { LaunchSite = site.Name };
                _settingsChanged = true;
            }
            _buttons.Add((SiteButtons[i], ItemMiddle()));
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
        }
        // Always two lines' room, wrapped or not, so the menu (centred on the screen) keeps still as
        // the pick changes, and the GameDriver's offsets hold whichever site is picked.
        var top = ImGui.GetCursorPosY();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        _siteLines ??= [.. LaunchSites.All.SelectMany(s => new[] { SiteLine(s, daylight: true), SiteLine(s, daylight: false) })];
        var line = 2 * Array.IndexOf(LaunchSites.All, picked) + (InDaylight(picked, worldTime) ? 0 : 1);
        ImGui.TextColored(Quiet, Escape(_siteLines[line]));
        ImGui.PopTextWrapPos();
        var room = 2 * ImGui.GetTextLineHeightWithSpacing() - (ImGui.GetCursorPosY() - top) - ImGui.GetStyle().ItemSpacing.Y;
        if (room > 0f)
            ImGui.Dummy(new float2(0f, room));
    }

    /// <summary>Each site's button as the readout names it, by its place in <see cref="LaunchSites.All"/> (made once: the menu is drawn every frame it is up).</summary>
    private static readonly string[] SiteButtons = [.. LaunchSites.All.Select(s => "Site: " + s.Name)];

    /// <summary>
    /// Each site's line by day and by night (its two in turn), made the first time the row is shown
    /// (its height is read from the terrain): only the daylight changes as the sun moves, so the
    /// row picks one of the two each frame rather than making its text afresh.
    /// </summary>
    private static string[]? _siteLines;

    /// <summary>Whether the sun is up over <paramref name="site"/> at <paramref name="worldTime"/>.</summary>
    private static bool InDaylight(LaunchSite site, double worldTime) => Vector3.Dot(site.Up, Sun.Direction(GroundBody.Planet, worldTime)) > 0f;

    /// <summary>
    /// What a launch site is, in a line: how high it is, how far north or south, what it has to land
    /// on, and whether it is day or night there at <paramref name="worldTime"/>. (What is there, not
    /// which to pick.)
    /// </summary>
    public static string SiteLine(LaunchSite site, double worldTime) => SiteLine(site, InDaylight(site, worldTime));

    private static string SiteLine(LaunchSite site, bool daylight)
    {
        var height = MathF.Abs(site.Elevation) < 5f ? "sea level" : $"{site.Elevation:0} m up";
        var latitude = MathF.Abs(site.Latitude) < 0.5f ? "on the equator" : $"{MathF.Abs(site.Latitude):0}°{(site.Latitude > 0 ? "N" : "S")}";
        var ships = site.DroneShips.Count switch { 0 => null, 1 => "a drone ship out at sea", var n => $"{n} drone ships out at sea" };
        var landing = (site.HasLandingZone, ships) switch
        {
            (true, { } s) => $"A landing zone, and {s}.",
            (true, null) => "A landing zone.",
            (false, { } s) => $"{char.ToUpperInvariant(s[0])}{s[1..]}.",
            _ => "Nothing to land on.",
        };
        var light = daylight ? "In daylight." : "At night.";
        return $"{site.Name}: {height}, {latitude}. {landing} {light}";
    }

    /// <summary>
    /// The middle of the screen, after a world is picked or from the game menu: every mission,
    /// with its briefing, as buttons. Choosing one launches its rocket into the world. It is as wide
    /// as the screen leaves it (up to <see cref="MenuWidest"/>) beside the credits panel, which it
    /// never covers (the licence's notice and logo stay in view): centred where that leaves it
    /// room, else to the left of the credits. The missions sit in a region of their own that
    /// scrolls only when they don't all fit on the screen (the world, the sites and Close stay in
    /// view), starting at the top each time the menu opens. Each mission's button, each site's
    /// and "Change world" are read out as markers ("Mission: FREE PLAY", "Site: Home":
    /// <see cref="ButtonsDrawn"/>), a mission's only while its middle is in view, so a script
    /// clicks them by name wherever they are.
    /// </summary>
    private void Menu(Flight flight, float2 display)
    {
        var style = ImGui.GetStyle();
        var scrollbar = style.ScrollbarSize + style.ItemSpacing.X;
        var (width, across) = MenuAcross(display, MenuWidest, MenuNarrowest, scrollbar); // (room kept for a scrollbar)
        ImGui.SetNextWindowPos(new float2(across, MenuMiddle(display.Y, _menuHeight)), ImGuiCond.Always, new float2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.85f);
        if (ImGui.Begin("##missions"u8, MenuFlags))
        {
            ImGui.PushFont(_font, 30f);
            Centred("CHOOSE A MISSION", new float4(1, 1, 1, 1));
            ImGui.PopFont();
            ImGui.Separator();
            if (WorldLine(flight.WorldName, width))
                OpenWorlds();
            _buttons.Add(("Change world", ItemMiddle()));
            ImGui.Spacing();
            SiteRow(width, flight.WorldTime);
            ImGui.Spacing();

            // The missions' region: as tall as they are at this width (worked out from their text,
            // so it is right from the first frame), or as the screen leaves room for under what is
            // above it and over what is below it (the gap, the line, the note and Close), with a
            // scrollbar beside them then.
            var spacing = style.ItemSpacing.Y;
            var below = spacing + 1f + spacing + ImGui.CalcTextSize(MenuNote, false, width).Y + spacing + CloseHeight + style.WindowPadding.Y;
            var (height, scrolls) = ListRegion(MissionsHeight(width), display.Y, ImGui.GetCursorPosY(), below, MissionHeight);
            if (ImGui.BeginChild("##missionList"u8, new float2(width + (scrolls ? scrollbar : 0f), height)))
            {
                // Opened afresh (not drawn the frame before): from the top.
                if (_menuFrame != ImGui.GetFrameCount() - 1)
                    ImGui.SetScrollY(0f);
                _menuFrame = ImGui.GetFrameCount();
                var (top, bottom) = (ImGui.GetWindowPos().Y, ImGui.GetWindowPos().Y + ImGui.GetWindowHeight());
                for (var i = 0; i < GameOne.Mission.All.Length; i++)
                {
                    var mission = GameOne.Mission.All[i];
                    var current = mission == flight.Mission ? "   (current)" : "";
                    if (ImGui.Button($"{mission.Name}{current}###mission{i}", new float2(width, MissionHeight)))
                        ChooseMission(mission);
                    // Read out while its middle, where a script clicks it, is in the region's view.
                    var centre = (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) / 2f;
                    if (centre >= top && centre <= bottom)
                        _buttons.Add((MissionButtons[i], ItemMiddle()));
                    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
                    ImGui.PushStyleColor(ImGuiCol.Text, Quiet);
                    ImGui.TextWrapped(MissionBriefings[i]);
                    ImGui.PopStyleColor();
                    ImGui.PopTextWrapPos();
                    if (i < GameOne.Mission.All.Length - 1)
                    {
                        ImGui.Spacing();
                        ImGui.Spacing();
                    }
                }
            }
            ImGui.EndChild();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(Quiet, MenuNote);
            ImGui.PopTextWrapPos();
            if (ImGui.Button("Close"u8, new float2(width, CloseHeight)))
                CloseMenu();
            _menuHeight = ImGui.GetWindowHeight();
        }
        ImGui.End();
    }

    /// <summary>
    /// How tall the missions are at <paramref name="width"/>, as the region lays them out: each
    /// button, its briefing wrapped to that width and the spacing after it, as ImGui spaces items.
    /// </summary>
    private static float MissionsHeight(float width)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var height = 0f;
        for (var i = 0; i < MissionTexts.Length; i++)
            height += MissionHeight + spacing + ImGui.CalcTextSize(MissionTexts[i], false, width).Y + spacing
                + (i < MissionTexts.Length - 1 ? 2f * spacing : 0f);
        return height - spacing;
    }

    /// <summary>
    /// The mission menu's first line: the <paramref name="world"/> being played, wrapped short of
    /// the "Change world" button at the right of a menu <paramref name="width"/> wide, so a long name
    /// neither runs under the button nor widens the menu over the credits. Returns whether the
    /// button was clicked (the last item drawn).
    /// </summary>
    internal static bool WorldLine(string world, float width)
    {
        var style = ImGui.GetStyle();
        var button = ImGui.CalcTextSize("Change world"u8).X + 2 * style.FramePadding.X;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - button - style.ItemSpacing.X);
        ImGui.Text($"World:  {world}");
        ImGui.PopTextWrapPos();
        ImGui.SameLine();
        // At the right of the buttons below: by their width, not the window's (which is sized to
        // fit what is in it, so it would grow to fit the button, which would move further out...).
        ImGui.SetCursorPosX(style.WindowPadding.X + width - button);
        return ImGui.SmallButton("Change world"u8);
    }

    /// <summary>
    /// How wide a menu screen's content is and where its middle goes across the screen, so that it
    /// never covers the credits panel (the licence's logo and notice stay in view on every screen):
    /// centred if that leaves it at least <paramref name="narrowest"/> wide beside them (as wide as
    /// that allows, up to <paramref name="widest"/>), else in the room to their left (as wide as that
    /// allows, up to <paramref name="widest"/>), a gap of <see cref="CreditsGap"/> from them either
    /// way. <paramref name="extra"/> is anything the window holds beside its content's width (a
    /// scrollbar's room). Its content is then laid out to that width.
    /// </summary>
    private static (float Width, float Across) MenuAcross(float2 display, float widest, float narrowest, float extra = 0f) =>
        MenuAcross(display.X, 2f * ImGui.GetStyle().WindowPadding.X, widest, narrowest, extra);

    /// <summary><see cref="MenuAcross(float2, float, float, float)"/> on a screen <paramref name="displayWidth"/> wide, for windows padded <paramref name="padding"/> in all across.</summary>
    internal static (float Width, float Across) MenuAcross(float displayWidth, float padding, float widest, float narrowest, float extra = 0f)
    {
        var clear = CreditsLeft(displayWidth) - CreditsGap;
        var centredRoom = 2f * (clear - displayWidth / 2f) - padding - extra;
        return centredRoom >= narrowest
            ? (MathF.Floor(MathF.Min(widest, centredRoom)), displayWidth / 2f)
            : (MathF.Floor(MathF.Min(widest, clear - Margin - padding - extra)), (Margin + clear) / 2f);
    }

    /// <summary>
    /// Where a menu screen <paramref name="height"/> tall (as last drawn: the window is sized to what
    /// it holds, and ImGui doesn't show one sized so on the frame it is first made, so that one is
    /// never seen) has its middle on a screen <paramref name="displayHeight"/> high: a little above
    /// the screen's, or lower if it is too tall for that, so it stays on the screen.
    /// </summary>
    internal static float MenuMiddle(float displayHeight, float height) =>
        Math.Clamp(displayHeight * 0.45f, Margin + height / 2f, MathF.Max(Margin + height / 2f, displayHeight - Margin - height / 2f));

    /// <summary>
    /// How tall a menu screen's list (the missions, the saved worlds) is, and whether it scrolls: as
    /// tall as its <paramref name="content"/>, or as the screen <paramref name="displayHeight"/> high
    /// leaves room for under what is above it in its window (<paramref name="above"/>, from the
    /// window's top) and over what is below it (<paramref name="below"/>, to its foot), so the whole
    /// window fits on the screen; never under <paramref name="least"/> (one row).
    /// </summary>
    internal static (float Height, bool Scrolls) ListRegion(float content, float displayHeight, float above, float below, float least)
    {
        var room = displayHeight - 2f * Margin - above - below;
        return (MathF.Max(MathF.Min(content, room), least), content > room);
    }

    /// <summary>The credits panel's left edge on a screen <paramref name="displayWidth"/> wide (it sits in the bottom right corner, <see cref="CreditsWidth"/> wide).</summary>
    internal static float CreditsLeft(float displayWidth) => displayWidth - Margin - CreditsWidth;

    /// <summary>The menu screens' content widths (the mission menu's at its widest), for laying each out with <see cref="MenuAcross(float2, float, float, float)"/>.</summary>
    internal const float GameMenuWidth = 280f, LogWidth = 640f, SettingsWidth = 340f, WorldListWidth = 560f;

    // The menu screens' gap from the credits panel; the mission menu's content's width, at least and
    // at most (the briefings wrap to fewer lines the wider it is), and its buttons' heights.
    internal const float CreditsGap = 10f, MenuNarrowest = 460f, MenuWidest = 740f;
    private const float MissionHeight = 34f, CloseHeight = 26f;

    private const string MenuNote = "Choosing one puts a fresh rocket on the pad of the site picked above: everything already in the world stays where it is.";

    /// <summary>Each mission's button as the readout names it, by its place in <see cref="Mission.All"/> (made once: the menu is drawn every frame it is up).</summary>
    private static readonly string[] MissionButtons = [.. GameOne.Mission.All.Select(m => "Mission: " + m.Name)];

    /// <summary>Each mission's briefing on one line, as measured (made once).</summary>
    private static readonly string[] MissionTexts = [.. GameOne.Mission.All.Select(m => m.Briefing.Replace('\n', ' '))];

    /// <summary>Each mission's briefing as shown: escaped, as <c>TextWrapped</c> is a printf format (made once).</summary>
    private static readonly string[] MissionBriefings = [.. MissionTexts.Select(Escape)];

    // The mission menu's height when last drawn, for placing it, and the frame it was last drawn
    // (so its list starts at the top each time it opens).
    private float _menuHeight;
    private int _menuFrame = -2;

    /// <summary>
    /// The middle of the screen, when the game starts or from the game menu: every saved world
    /// (the last played first, and marked), each with what is in it and buttons to play it, rename
    /// it, reset it to its preset or delete it (those two asked to confirm); and new worlds from
    /// each preset. Playing one (or making one) goes on to the mission menu. The saved worlds sit in
    /// a region of their own that scrolls when they don't all fit on the screen (the new worlds'
    /// buttons and Close stay in view), opening at the top each time. Each world's Play (or Carry
    /// on) and Delete, the confirmation's "Yes, delete it" and "No", and each preset's New are read
    /// out as markers ("Play: Empty world", "Delete: Empty world", "New: Empty world":
    /// <see cref="ButtonsDrawn"/>), a row's only while its middle is in view.
    /// </summary>
    private void WorldList(Flight flight, float2 display)
    {
        var style = ImGui.GetStyle();
        var scrollbar = style.ScrollbarSize + style.ItemSpacing.X;
        // The rows' region: as tall as they were last drawn (a row's height follows its text and
        // what it is asking: renaming, confirming), or as the screen leaves room for under what is
        // above it and over the new worlds and Close (both as last drawn), with a scrollbar beside them
        // then. Worked out before the width, which leaves the scrollbar its room this frame, so the
        // window is never wider than the room beside the credits. A list that has just changed
        // settles in a frame.
        var (height, scrolls) = Worlds.Count == 0 ? (0f, false)
            : ListRegion(_worldRowsHeight, display.Y, _worldListAbove, _worldListBelow, 3f * ImGui.GetFrameHeightWithSpacing());
        var (width, across) = MenuAcross(display, WorldListWidth, WorldListWidth, scrolls ? scrollbar : 0f);
        ImGui.SetNextWindowPos(new float2(across, MenuMiddle(display.Y, _worldListHeight)), ImGuiCond.Always, new float2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.88f);
        if (ImGui.Begin("##worlds"u8, MenuFlags))
        {
            ImGui.PushFont(_font, 30f);
            Centred("CHOOSE A WORLD", new float4(1, 1, 1, 1));
            ImGui.PopFont();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(Quiet, "A world keeps everything flown in it, from one mission to the next: satellites left in orbit, boosters standing where they landed. It is saved as you play."u8);
            ImGui.PopTextWrapPos();
            ImGui.Separator();
            ImGui.Spacing();

            if (Worlds.Count == 0)
                ImGui.TextColored(Quiet, "No saved worlds yet: make one below."u8);
            else
            {
                _worldListAbove = ImGui.GetCursorPosY();
                if (ImGui.BeginChild("##worldList"u8, new float2(width + (scrolls ? scrollbar : 0f), height)))
                {
                    // Opened afresh (not drawn the frame before): from the top.
                    if (_worldListFrame != ImGui.GetFrameCount() - 1)
                        ImGui.SetScrollY(0f);
                    _worldListFrame = ImGui.GetFrameCount();
                    (_rowsTop, _rowsBottom) = (ImGui.GetWindowPos().Y, ImGui.GetWindowPos().Y + ImGui.GetWindowHeight());
                    if (_renaming is null && _confirming is null)
                        _revealed = null; // (nothing asking: the next ask is shown whole again)
                    for (var i = 0; i < Worlds.Count; i++)
                    {
                        var world = Worlds[i];
                        ImGui.PushID(world.Id);
                        WorldRow(flight, world, i, width);
                        ImGui.PopID();
                        if (i < Worlds.Count - 1)
                            ImGui.Spacing();
                    }
                    _worldRowsHeight = ImGui.GetCursorPosY() - style.ItemSpacing.Y;
                }
                ImGui.EndChild();
                ImGui.Spacing();
            }

            var below = ImGui.GetCursorPosY();
            ImGui.Separator();
            ImGui.Text("New world"u8);
            for (var i = 0; i < WorldPresets.All.Length; i++)
            {
                ImGui.PushID(WorldPresets.All[i].Id);
                if (ImGui.Button(NewWorldButtons[i].Label, new float2(200, 26)))
                    _worldRequest = new WorldRequest(WorldRequestKind.New, WorldPresets.All[i].Id);
                _buttons.Add((NewWorldButtons[i].Marker, ItemMiddle()));
                ImGui.SameLine();
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - 210);
                ImGui.TextColored(Quiet, NewWorldButtons[i].Description);
                ImGui.PopTextWrapPos();
                ImGui.PopID();
            }
            if (flight.WorldId.Length > 0)
            {
                ImGui.Separator();
                if (ImGui.Button("Close"u8, new float2(width, 26)))
                    CloseMenu();
            }
            _worldListBelow = ImGui.GetCursorPosY() - below - style.ItemSpacing.Y + style.WindowPadding.Y;
            _worldListHeight = ImGui.GetWindowHeight();
        }
        ImGui.End();
    }

    /// <summary>Each preset's New button: its label, its marker for the readout and its description, escaped (made once).</summary>
    private static readonly (string Label, string Marker, string Description)[] NewWorldButtons =
        [.. WorldPresets.All.Select(p => ($"New:  {p.Name}", $"New: {p.Name}", Escape(p.Description)))];

    // The world list's measures when last drawn, for laying out this frame: its rows' height, what is
    // above and under them, the window's height, and the frame it was last drawn (so its rows start
    // at the top each time it opens); and the rows' region on the screen this frame, for reading out
    // only the buttons in view.
    private float _worldRowsHeight, _worldListAbove, _worldListBelow = 120f, _worldListHeight, _rowsTop, _rowsBottom;
    private int _worldListFrame = -2;

    /// <summary>
    /// The first frame a row asks something (a rename, a confirmation: <see cref="_renaming"/>,
    /// <see cref="_confirming"/>), the rows' region scrolls to show it whole, its buttons at the
    /// foot, as the row grows a line and a row at the foot of a scrolled list would be cut.
    /// </summary>
    private void RevealRow()
    {
        var asking = _renaming ?? _confirming;
        if (asking is not null && asking != _revealed)
            ImGui.SetScrollHereY(1f);
        _revealed = asking;
    }

    private string? _revealed;

    /// <summary>Adds the button just drawn in a saved world's row to the readout's, as <paramref name="name"/>, if its middle is in the rows' view.</summary>
    private void RowButton(string name)
    {
        var middle = ItemMiddle();
        var centre = (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) / 2f;
        if (centre >= _rowsTop && centre <= _rowsBottom)
            _buttons.Add((name, middle));
    }

    /// <summary>
    /// A saved world's row's text, made as the list is set (<see cref="Worlds"/>), not each frame:
    /// its name as shown (escaped, as <c>TextColored</c> is a printf format), plain and with each
    /// tag; what is in it, or why it can't be read; and its Play and Delete as the readout names them.
    /// </summary>
    private readonly record struct WorldRowText(string Name, string Playing, string Latest, string Details, string Play, string Delete)
    {
        public static WorldRowText Of(WorldSummary world)
        {
            var name = Escape(world.Name);
            var details = world.Error is { } error
                ? Escape($"Can't be read: {error}")
                : Escape($"{world.Contents}.  {world.VesselCount} of {Flight.Capacity} vessels, from \"{WorldPresets.All.FirstOrDefault(p => p.Id == world.Preset).Name ?? world.Preset}\", {world.Flights} flight{(world.Flights == 1 ? "" : "s")}, last played {world.LastPlayed:d MMM yyyy HH:mm}.");
            return new(name, name + "   (playing now)", name + "   (last played)", details, "Play: " + world.Name, "Delete: " + world.Name);
        }
    }

    /// <summary>Each saved world's row's text, by its place in <see cref="Worlds"/>.</summary>
    private WorldRowText[] _worldRows = [];

    /// <summary>One saved world in the list (the <paramref name="index"/>th, the first the last played): its name, what is in it, and what can be done with it.</summary>
    private void WorldRow(Flight flight, WorldSummary world, int index, float width)
    {
        var latest = index == 0;
        var playing = world.Id == flight.WorldId;
        var text = _worldRows[index];
        // Both wrapped to the list's width, so a long name shows whole.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextColored(latest || playing ? Good : new float4(1, 1, 1, 1), playing ? text.Playing : latest ? text.Latest : text.Name);
        ImGui.TextColored(world.Error is null ? Quiet : Bad, text.Details);
        ImGui.PopTextWrapPos();

        if (_renaming == world.Id)
        {
            if (_focusName)
            {
                ImGui.SetKeyboardFocusHere(); // ready to type into straight away
                _focusName = false;
            }
            ImGui.SetNextItemWidth(260);
            var done = ImGui.InputText("##name", _nameBuffer, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            ImGui.SameLine();
            done |= ImGui.SmallButton("OK"u8);
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"u8))
                _renaming = null;
            else if (done)
            {
                var end = Array.IndexOf(_nameBuffer, (byte)0); // the text ends at its first zero
                var name = Encoding.UTF8.GetString(_nameBuffer, 0, end < 0 ? _nameBuffer.Length : end).Trim();
                if (name.Length > 0)
                    _worldRequest = new WorldRequest(WorldRequestKind.Rename, world.Id, name);
                _renaming = null;
            }
            RevealRow();
            return;
        }
        if (_confirming == world.Id)
        {
            var what = _confirmKind == WorldRequestKind.Delete
                ? "Delete this world? Everything in it goes, for good."
                : "Reset this world to its preset? Everything flown in it goes.";
            ImGui.TextColored(Warning, what);
            var deleting = _confirmKind == WorldRequestKind.Delete;
            var yes = ImGui.SmallButton(deleting ? "Yes, delete it"u8 : "Yes, reset it"u8);
            RowButton(deleting ? "Yes, delete it" : "Yes, reset it");
            if (yes)
            {
                _worldRequest = new WorldRequest(_confirmKind, world.Id);
                _confirming = null;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("No"u8))
                _confirming = null;
            RowButton("No");
            RevealRow();
            return;
        }

        if (world.Error is null)
        {
            if (ImGui.SmallButton(playing ? "Carry on"u8 : "Play"u8))
                _worldRequest = new WorldRequest(WorldRequestKind.Play, world.Id);
            RowButton(text.Play); // (Carry on too: it plays that world on)
            ImGui.SameLine();
            if (ImGui.SmallButton("Rename"u8))
            {
                _renaming = world.Id;
                _focusName = true;
                Array.Clear(_nameBuffer);
                var bytes = Encoding.UTF8.GetBytes(world.Name);
                Array.Copy(bytes, _nameBuffer, Math.Min(bytes.Length, _nameBuffer.Length - 1));
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"u8))
                (_confirming, _confirmKind) = (world.Id, WorldRequestKind.Reset);
            ImGui.SameLine();
        }
        if (ImGui.SmallButton("Delete"u8))
            (_confirming, _confirmKind) = (world.Id, WorldRequestKind.Delete);
        RowButton(text.Delete);
    }

    /// <summary>
    /// Top left: how high, how fast, how much throttle and fuel, the stage, the legs, and (when going
    /// for orbit) the orbit it is on.
    /// </summary>
    private void Instruments(Flight flight)
    {
        ImGui.SetNextWindowPos(new float2(Margin, Margin), ImGuiCond.Always, new float2(0, 0));
        ImGui.SetNextWindowBgAlpha(0.5f);
        // Takes no input, as the other readings' panels: a camera drag or the wheel over it goes to
        // the view (ChaseCamera goes by WantCaptureMouse). The frame selector's buttons, in it, are
        // a small window of their own over it (FrameButtonsWindow), so only they take the mouse.
        var shown = ImGui.Begin("##instruments"u8, PanelFlags);
        if (shown)
        {
            // Whose instruments these are: the vessel being flown.
            ImGui.Text(flight.Controlled.Name.ToUpperInvariant());
            if (flight.CanSwitch)
            {
                ImGui.SameLine();
                ImGui.TextColored(Quiet, "   Tab: fly another"u8);
            }
            // A fast collision coming, on a line of its own up by the vessel's name, over the time
            // line, which still shows how time runs (warped again by hand, or in slow motion).
            if (flight.Warning is { } collision)
                ImGui.TextColored(DangerText, Escape(WarningLine(collision)));
            // And a near miss the setting will slow for, shown ahead as a fast collision is (but not
            // one that is that fast collision too: see Flight.NearMissShown).
            if (flight.NearMissShown is { } m)
                ImGui.TextColored(Warning, Escape(NearMissLine(m)));
            if (TimeLine(flight.Slowed, flight.PlayerRate, TimeScale, _running) is { } time)
                ImGui.TextColored(time.Good ? Good : Warning, Escape(time.Text));
            // The clocks, in every frame: the world's (Home's day and time of day) and the time of
            // day where the vessel is (by its longitude), each in minutes and seconds of the day.
            ImGui.Text(Sun.WorldClock(flight.WorldTime));
            ImGui.Text($"Local {Sun.Clock(Sun.LocalSeconds(flight.Body, flight.Position, flight.WorldTime))}");
            ImGui.Separator();
            FrameSelector(flight);
            ImGui.Text($"Altitude      {flight.Altitude,7:0} m");
            // Over the ground: what a hop or a landing goes by; quiet once in orbit, where it isn't.
            if (flight.Controlled.OrbitStable)
                ImGui.TextColored(Quiet, $"Ground        {flight.Ground,7:0} m");
            else
                ImGui.Text($"Ground        {flight.Ground,7:0} m");
            // The speeds are those of the frame in use: over the ground, among the stars, or against the target.
            if (flight.Frame == Frame.Surface)
            {
                ImGui.Text($"Vertical      {flight.VerticalSpeed,7:0.0} m/s");
                ImGui.Text($"Horizontal    {flight.HorizontalSpeed,7:0.0} m/s");
            }
            ImGui.Text($"Tilt          {flight.TiltDegrees,7:0} °");
            Atmosphere(flight);
            if (flight.RelativeSpeed is { } relative)
                ImGui.Text($"Relative speed{relative,7:0.0} m/s");
            if (flight.DistanceToTarget is { } toTarget)
                ImGui.Text($"To target     {toTarget,7:0} m");
            // The orbit, unless it is only there to land: then just once it is up where orbits matter.
            if (flight.LandingSite is null || flight.Apoapsis > flight.Body.OrbitFloor)
            {
                // The orbit round the body it is at, over that body's datum: green once each end of
                // it is clear of the body's orbit floor (its air, or with none its highest ground).
                var floor = flight.Body.OrbitFloor;
                // Its top past where it would leave the body's pull is never reached on this orbit: "leaves its pull".
                var top = !float.IsFinite(flight.Apoapsis) ? "escaping" : flight.LeavesItsPull ? "leaves its pull" : $"{flight.Apoapsis,7:0} m";
                ImGui.TextColored(flight.Apoapsis > floor ? Good : Quiet, $"Apoapsis      {top}");
                ImGui.TextColored(flight.Periapsis > floor ? Good : Quiet,
                    flight.Periapsis > -flight.Body.Radius * 0.9f ? $"Periapsis     {flight.Periapsis,7:0} m" : "Periapsis     (falls back)");
                // A lap of it, in minutes and seconds (see Flight.OrbitLap: none it won't fly).
                if (flight.OrbitLap is { } period)
                {
                    var lap = (long)Math.Round(period);
                    ImGui.TextColored(Quiet, $"Period        {lap / 60,4}:{lap % 60:00}");
                }
            }
            if (flight.Frame == Frame.Orbital)
            {
                // Among the stars, as the orbit is worked out: over the ground it is less going east
                // (the ground's turn helps) and more going west.
                ImGui.Text($"Orbit speed   {flight.OrbitSpeed,7:0} m/s");
                ImGui.TextColored(Quiet, $"Orbit speed   {flight.OrbitSpeedNeeded,7:0} m/s needed");
            }
            // The next node's burn: when, how much is still needed (amber once past what was
            // planned), and how long the rest takes at full throttle. When to start and stop is the pilot's.
            if (flight.Guide is { } guide)
            {
                ImGui.Spacing();
                ImGui.TextColored(NodeColor, $"Node          {NodeEditor.Clock(guide.Seconds),7}");
                ImGui.TextColored(guide.Overshot ? Warning : NodeColor, $"Delta-v left  {guide.Left.Length(),7:0.0} m/s{(guide.Overshot ? "  past it" : "")}");
                ImGui.TextColored(NodeColor, guide.Short ? "Burn          more than the fuel left" : $"Burn          {guide.BurnSeconds,7:0.0} s at full");
            }
            ImGui.Spacing();
            ImGui.Text("Throttle"u8);
            ImGui.ProgressBar(flight.Throttle, new float2(200, 0), $"{flight.Throttle * 100:0}%");
            var stageName = flight.CurrentStage.Name.ToLowerInvariant();
            ImGui.Text(flight.StageCount > 1 ? $"Fuel: {stageName} (stage {flight.Stage + 1} of {flight.StageCount})" : "Fuel");
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, flight.Fuel < 0.15f ? Bad : new float4(0.85f, 0.7f, 0.15f, 1f));
            ImGui.ProgressBar(flight.Fuel, new float2(200, 0), $"{flight.Fuel * 100:0}%");
            ImGui.PopStyleColor();
            var reserve = flight.CanStage ? flight.CurrentStage.LandingReserve : 0f;
            if (reserve > 0f)
            {
                // The landing-fuel mark: drop the stage here and it has just enough to come back down.
                var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
                var x = min.X + (max.X - min.X) * reserve;
                ImGui.GetWindowDrawList().AddLine(new float2(x, min.Y - 3f), new float2(x, max.Y + 3f), new ImColor8(90, 205, 255), 2f);
            }
            if (flight.JettisonsFairing && flight.State == FlightState.Flying)
            {
                // Too soon, and the bare payload breaks up in the air: not only at the speed it is
                // going now, but at the speed it will be going soon (slow off the pad, it soon won't be).
                const float climbingSpeed = 300f;
                var speed = MathF.Max(flight.Velocity.Length(), climbingSpeed);
                var pressure = flight.Controlled.Body.AirDensity(flight.Controlled.Body.Altitude(flight.Position)) * speed * speed;
                if (pressure > Scene.FairingMaxAirPressure * 0.8f)
                    ImGui.TextColored(Warning, "SPACE: jettison the fairing, once above the thick air (~2 km)"u8);
                else
                    ImGui.TextColored(Good, "SPACE: jettison the fairing now"u8);
            }
            else if (flight.CanStage && flight.State == FlightState.Flying)
            {
                var separate = Separating(flight);
                if (flight.Fuel <= 0f)
                    ImGui.TextColored(Warning, $"SPACE: {separate} (empty)");
                else if (reserve > 0f && flight.Fuel <= reserve)
                    ImGui.TextColored(Good, $"SPACE: {separate} now, to land the {stageName}");
                else
                    ImGui.TextColored(Quiet, reserve > 0f ? $"SPACE: {separate} (blue mark: landing fuel)" : $"SPACE: {separate} now");
            }
            ImGui.Spacing();
            if (flight.HasLegs)
            {
                var (legs, color) = flight.LegDeploy switch
                {
                    >= 1f => ("Legs deployed", Good),
                    <= 0f => ("Legs stowed", Quiet),
                    _ => (flight.LegsWanted ? "Legs deploying..." : "Legs stowing...", Warning),
                };
                ImGui.TextColored(color, legs);
            }
            if (flight.HasBay)
            {
                var (doors, color) = flight.BayDoors switch
                {
                    >= 1f => ("Bay doors open", Good),
                    <= 0f => ("Bay doors closed", Quiet),
                    _ => (flight.BayDoorsWanted ? "Bay doors opening..." : "Bay doors closing...", Warning),
                };
                ImGui.TextColored(color, doors);
            }
            ImGui.TextColored(flight.ProgradeHold ? Good : Quiet, flight.ProgradeHold ? "Prograde hold: on" : "Prograde hold: off (P)");
            ImGui.TextColored(flight.RetrogradeHold ? Good : Quiet, flight.RetrogradeHold ? "Retrograde hold: on" : "Retrograde hold: off (R)");
            _instrumentsRect = ThisWindow();
        }
        else
            _instrumentsRect = RectangleF.Empty;
        ImGui.End();
        // Over the room the instruments kept for them this frame: with the instruments not drawn
        // (collapsed or clipped), there is none, and the buttons would float where it last was.
        if (shown)
            FrameButtonsWindow(flight);
    }

    /// <summary>
    /// The frame of reference's selector, in the instruments: four buttons (see
    /// <see cref="FrameButtonsWindow"/>), whose room it keeps, and the frame in use, lit for a few
    /// seconds after it changes (Auto turning it over, say, is information, not a prompt). Not
    /// lit for the frame a flight starts in, or a vessel is taken over in: that is no change.
    /// </summary>
    private void FrameSelector(Flight flight)
    {
        // Real time: the world's clock stops in a pause and runs by in a warp, where Auto changes frames.
        if (flight.Frame != _frameSeen)
        {
            if (_frameSeen is not null)
                _frameChangedAt = Stopwatch.GetTimestamp();
            _frameSeen = flight.Frame;
        }
        _frameButtonsAt = ImGui.GetCursorScreenPos();
        ImGui.Dummy(_frameButtonsSize);
        ImGui.SameLine();
        var inUse = flight.Frame switch { Frame.Orbital => "ORBIT", Frame.Target => "TARGET", _ => "SURFACE" };
        var changed = Stopwatch.GetElapsedTime(_frameChangedAt).TotalSeconds < FrameLitSeconds;
        ImGui.TextColored(changed ? Good : Quiet, $"  {inUse}");
    }

    /// <summary>
    /// The selector's buttons (the chosen one shown pressed; Target only with a target), in a
    /// window of their own over the room the instruments keep for them: the instruments take no
    /// input, so a drag or the wheel over them reaches the view, and only the buttons take the
    /// mouse. Its size is kept for the next frame's room.
    /// </summary>
    private void FrameButtonsWindow(Flight flight)
    {
        ImGui.SetNextWindowPos(_frameButtonsAt);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0, 0));
        if (ImGui.Begin("##frame-buttons"u8, (PanelFlags & ~ImGuiWindowFlags.NoInputs) | ImGuiWindowFlags.NoBackground))
        {
            ImGui.PopStyleVar();
            FrameButtonRow(flight);
            _frameButtonsSize = ImGui.GetWindowSize();
        }
        else
            ImGui.PopStyleVar();
        ImGui.End();
    }

    /// <summary>The four buttons, in a row: a click picks that frame (for the flight's input, <see cref="TakeFrameChoice"/>).</summary>
    private void FrameButtonRow(Flight flight)
    {
        // Off while a menu is up (see Build, which also lets go of a choice made then).
        ImGui.BeginDisabled(MenuOpen);
        for (var i = 0; i < FrameButtons.Length; i++)
        {
            var (label, choice) = FrameButtons[i];
            if (i > 0)
                ImGui.SameLine();
            var disabled = choice == FrameChoice.Target && flight.Target is null;
            var chosen = flight.FrameChoice == choice;
            ImGui.BeginDisabled(disabled);
            if (chosen)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new float4(0.2f, 0.45f, 0.75f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new float4(0.25f, 0.52f, 0.85f, 1f));
            }
            if (ImGui.SmallButton(label))
                _frameChosen = choice;
            if (chosen)
                ImGui.PopStyleColor(2);
            ImGui.EndDisabled();
        }
        ImGui.EndDisabled();
    }

    private static readonly (string Label, FrameChoice Choice)[] FrameButtons =
    [
        ("Auto###frameAuto", FrameChoice.Auto), ("Surface###frameSurface", FrameChoice.Surface),
        ("Orbit###frameOrbit", FrameChoice.Orbit), ("Target###frameTarget", FrameChoice.Target),
    ];

    // The frame in use last frame: none at the start, and again as another vessel is flown (a new
    // flight, a world loaded or a switch), so the frame it starts in isn't lit as a change.
    private Frame? _frameSeen;
    private long _frameChangedAt = Stopwatch.GetTimestamp() - 100 * Stopwatch.Frequency;
    // Where the instruments keep room for the selector's buttons this frame, and how big their
    // window was last frame (a guess until it is first drawn).
    private float2 _frameButtonsAt;
    private float2 _frameButtonsSize = new(220f, 16f);

    /// <summary>How long (real seconds) the frame in use stays lit after it changes.</summary>
    private const double FrameLitSeconds = 3.0;

    /// <summary>
    /// How thick the air is where the rocket is, as a share of the air at the ground, and whether
    /// it has left the atmosphere (where there is no drag, and nothing turns the nose); at a body
    /// with no air, that it has none.
    /// </summary>
    private static void Atmosphere(Flight flight)
    {
        var body = flight.Body;
        var height = body.Altitude(flight.Position);
        var air = body.AirDensity(height);
        ImGui.Spacing();
        if (!body.HasAir)
        {
            ImGui.Text($"Air           {0,7:0.0} %");
            ImGui.TextColored(Quiet, "NO AIR: no drag"u8);
        }
        else if (air > 0f)
        {
            ImGui.Text($"Air           {air * 100,7:0.0} %");
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new float4(0.35f, 0.6f, 0.95f, 1f));
            ImGui.ProgressBar(air, new float2(200, 6), ""u8);
            ImGui.PopStyleColor();
            ImGui.TextColored(Quiet, $"In the atmosphere: space in {Scene.AtmosphereHeight - height:0} m");
        }
        else
        {
            ImGui.Text($"Air           {0,7:0.0} %");
            ImGui.TextColored(Good, "ABOVE THE ATMOSPHERE: no drag"u8);
        }
    }

    /// <summary>Top right: the parts of the mission, ticked off as they are done, and a button for the mission menu.</summary>
    private void MissionPanel(Flight flight, float2 display)
    {
        ImGui.SetNextWindowPos(new float2(display.X - Margin, Margin), ImGuiCond.Always, new float2(1, 0));
        ImGui.SetNextWindowBgAlpha(0.5f);
        if (ImGui.Begin("##mission"u8, PanelFlags & ~ImGuiWindowFlags.NoInputs))
        {
            ImGui.Text(flight.Mission.Name);
            ImGui.SameLine();
            ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX() + 12f, ImGui.GetWindowWidth() - 120f));
            if (ImGui.SmallButton("Menu (Esc)"u8))
                Screen = MenuScreen.Game;
            ImGui.Separator();
            var steps = flight.Mission.Steps;
            for (var i = 0; i < steps.Length; i++)
            {
                var done = flight.Progress.IsDone(i);
                var lost = GameOne.Mission.IsLost(i, flight);
                ImGui.TextColored(done ? Good : lost ? Bad : Quiet, Escape($"{(done ? "[x]" : lost ? "[!]" : "[ ]")}  {steps[i].Label}"));
            }
            if (flight.Mission.Failed(flight))
                ImGui.TextColored(Bad, "MISSION FAILED"u8);
            ImGui.TextColored(Quiet, $"Best altitude {flight.MaxAltitude:0} m");
            if (flight.Mission.Score(flight) is { } points)
                ImGui.TextColored(Good, $"Score {points}");
            Vessels(flight);
            _missionRect = ThisWindow();
        }
        else
            _missionRect = RectangleF.Empty;
        ImGui.End();
    }

    /// <summary>
    /// How many lines the timings panel has, whole and shortened (without the GPU's passes and the
    /// world's counts): one, "measuring...", until there is a summary; then, whole, thirteen with the
    /// GPU's times or eleven without (its "n/a" in place of the passes), and nine shortened.
    /// </summary>
    internal static (int Whole, int Shortened) TimingsLineCounts(TimingsSummary? summary) =>
        summary is null ? (1, 1) : double.IsNaN(summary.GpuMs) ? (11, 9) : (13, 9);

    /// <summary>
    /// Where the timings panel goes this frame, and how many of its lines: the first of these
    /// places where it is on the screen and clear (by <see cref="StripGap"/>) of the credits, the
    /// mission panel and everything in <paramref name="avoid"/>, or null if none is (a very small
    /// window), or while the credits haven't been laid out yet (the first frame).
    /// <list type="number">
    ///   <item>Bottom left, where the controls list opens (folded to its button most of the time),
    ///   over the button: whole, then shortened. Not with the list open or the small views up there,
    ///   as the instruments leave no room over them.</item>
    ///   <item>At the right edge, under the mission panel: whole, then shortened (without the GPU's
    ///   passes and the world's counts).</item>
    ///   <item>Beside the mission panel, to its left, as high as it is clear: at the top, or
    ///   under whatever is in the way there (the compass, a briefing); whole, then shortened.</item>
    ///   <item>Beside the credits, to their left, at the bottom: whole, then shortened.</item>
    /// </list>
    /// A panel of n lines is <c>n * lineStep + frame</c> tall (as ImGui fits a panel round its
    /// lines: see <see cref="ControlsListSize"/>), worked out before it is drawn rather than as
    /// last drawn, which would be out of date the first frame its lines change; it is drawn in
    /// the place's rectangle, its top-right corner at the place's and never larger.
    /// </summary>
    internal static (RectangleF Place, int Lines)? PlaceTimings(Vector2 display, float width, int whole, int shortened, float lineStep, float frame,
        RectangleF mission, RectangleF credits, ReadOnlySpan<RectangleF> avoid)
    {
        if (credits.IsEmpty)
            return null; // where the credits will be isn't known yet (the first frame): wait for them
        ReadOnlySpan<int> counts = [whole, shortened];

        // Bottom left, where the controls list opens (folded to its button most of the time):
        // over whatever is down there, the button or a caption, as low as it is clear.
        foreach (var lines in counts)
        {
            var place = new RectangleF(Margin, display.Y - Margin - Height(lines), width, Height(lines));
            for (var tries = 0; tries <= avoid.Length + 1; tries++)
            {
                if (Fits(place, mission, credits, avoid))
                    return (place, lines);
                var higher = float.MinValue; // the lowest bottom that clears what it is in now
                foreach (var r in avoid)
                    if (Near(place, r) && r.Top - StripGap < place.Bottom)
                        higher = MathF.Max(higher, r.Top - StripGap);
                if (higher == float.MinValue || higher - place.Height < Margin)
                    break; // off the screen, or no clear place under the instruments
                place.Y = higher - place.Height;
            }
        }

        // Under the mission panel.
        var right = display.X - Margin;
        var under = mission.IsEmpty ? Margin : mission.Bottom + StripGap;
        foreach (var lines in counts)
            if (Fits(new RectangleF(right - width, under, width, Height(lines)), mission, credits, avoid))
                return (new RectangleF(right - width, under, width, Height(lines)), lines);

        // Beside it, to its left: at the top, or just under something in the way there.
        if (!mission.IsEmpty)
        {
            var besideRight = mission.Left - StripGap;
            foreach (var lines in counts)
            {
                var place = new RectangleF(besideRight - width, Margin, width, Height(lines));
                // Each obstacle's bottom is a place to try, highest first: at most a few moves down.
                for (var tries = 0; tries <= avoid.Length + 1; tries++)
                {
                    if (Fits(place, mission, credits, avoid))
                        return (place, lines);
                    var lower = float.MaxValue;
                    foreach (var r in avoid)
                        if (Near(place, r) && r.Bottom + StripGap > place.Top)
                            lower = MathF.Min(lower, r.Bottom + StripGap);
                    if (Near(place, credits) && credits.Bottom + StripGap > place.Top)
                        lower = MathF.Min(lower, credits.Bottom + StripGap);
                    if (lower == float.MaxValue)
                        break; // off the screen, not in anything's way
                    place.Y = lower;
                }
            }
        }

        // Beside the credits, to their left, at the bottom.
        foreach (var lines in counts)
        {
            var place = new RectangleF(credits.Left - StripGap - width, credits.Bottom - Height(lines), width, Height(lines));
            if (Fits(place, mission, credits, avoid))
                return (place, lines);
        }
        return null;

        float Height(int lines) => MathF.Ceiling(lines * lineStep + frame);

        bool Fits(RectangleF place, RectangleF mission, RectangleF credits, ReadOnlySpan<RectangleF> avoid)
        {
            if (place.Left < Margin - 0.5f || place.Top < Margin - 0.5f || place.Right > display.X - Margin + 0.5f || place.Bottom > display.Y - Margin + 0.5f)
                return false;
            if (Near(place, mission) || Near(place, credits))
                return false;
            foreach (var r in avoid)
                if (Near(place, r))
                    return false;
            return true;
        }

        // Within StripGap of a panel (not quite touching it: the panels stand StripGap apart).
        static bool Near(RectangleF place, RectangleF panel) =>
            !panel.IsEmpty && RectangleF.Inflate(place, StripGap - 1f, StripGap - 1f).IntersectsWith(panel);
    }

    /// <summary>The panels and rows the timings panel keeps clear of, as last drawn (empty while one isn't drawn).</summary>
    private RectangleF _messageRect, _stripRect, _controlsRect, _captionRect;

    /// <summary>How wide the timings panel was last drawn (its numbers change its width a little).</summary>
    private float _timingsWidth;

    /// <summary>
    /// While <see cref="GameOne.Settings.ShowTimings"/> is on: where each frame's time goes, in
    /// milliseconds a frame, from <see cref="Timings.Summary"/> (a one-second average). Bottom left
    /// over the folded controls where it fits, else under or beside the mission panel or beside the
    /// credits, shortened (no GPU passes, no world's counts) where only that fits (see
    /// <see cref="PlaceTimings"/>), so it shows whenever there is room anywhere for it, and never
    /// covers the credits or the other panels. It is for the player to look at: it moves as the
    /// other panels come and go, so a script reads its numbers from the readout
    /// (<see cref="Readout.ReadTimings"/>), whole, never off the screen.
    /// </summary>
    private void TimingsPanel(float2 display)
    {
        var (whole, brief) = TimingsLineCounts(Timings.Summary);
        ImGui.PushFont(_font, CreditsFontSize);
        var style = ImGui.GetStyle();
        // As wide as it was last drawn, or as its widest line would be with typical numbers (from
        // the first frame), whichever is wider: it is placed by that, and kept to it.
        var width = MathF.Ceiling(MathF.Max(_timingsWidth,
            ImGui.CalcTextSize("Vessels: 100 awake, 100 frozen, 10 afloat;  100 ground tiles laid"u8).X + 2f * style.WindowPadding.X));
        var compass = new RectangleF(display.X / 2 - CompassWidth / 2, Margin, CompassWidth, CompassBottom - Margin);
        // The credits are drawn after this panel, so their rect is last frame's: re-anchored to
        // this frame's corner (their size doesn't change with the window's), so the frame after a
        // resize doesn't place the panel against where they were. None yet is none yet.
        var credits = _creditsRect.IsEmpty ? _creditsRect
            : new RectangleF(display.X - Margin - _creditsRect.Width, display.Y - Margin - _creditsRect.Height, _creditsRect.Width, _creditsRect.Height);
        var placed = PlaceTimings(new Vector2(display.X, display.Y), width, whole, brief,
            ImGui.GetTextLineHeightWithSpacing(), 2f * style.WindowPadding.Y - style.ItemSpacing.Y,
            _missionRect, credits, [_instrumentsRect, compass, _messageRect, _stripRect, _controlsRect, _captionRect]);
        ImGui.PopFont();
        if (placed is not { } p)
        {
            _timingsRect = RectangleF.Empty;
            return;
        }
        var (place, lines) = p;
        var shortened = lines < whole;
        // By the corner nearest the screen's edge, so a panel narrower than its place still lines
        // up with the panels above or below it: its left at the left edge, else its right.
        if (place.Left <= Margin + 0.5f)
            ImGui.SetNextWindowPos(new float2(place.Left, place.Top), ImGuiCond.Always, new float2(0, 0));
        else
            ImGui.SetNextWindowPos(new float2(place.Right, place.Top), ImGuiCond.Always, new float2(1, 0));
        // And never larger than its place, whatever its lines come to (clipped there, should the
        // count above ever fall behind the lines drawn), so it can't reach the credits or anything else.
        ImGui.SetNextWindowSizeConstraints(new float2(0, 0), new float2(place.Width, place.Height));
        ImGui.SetNextWindowBgAlpha(0.5f);
        if (ImGui.Begin("##timings"u8, PanelFlags))
        {
            ImGui.PushFont(_font, CreditsFontSize);
            ImGui.BeginGroup(); // (its lines as one item: the widest line's width, for placing it next frame)
            // The quiet lines are plain Text in the quiet colour, not TextColored, which is a printf
            // format: a line that came to hold a % (a part's share, say) would print garbage.
            if (Timings.Summary is not { } t)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Quiet);
                ImGui.Text("Timings: measuring..."u8);
                ImGui.PopStyleColor();
            }
            else
            {
                var physics = t[StepPart.Jolt] + t[StepPart.Tiles] + t[StepPart.Water] + t[StepPart.Contact] + t[StepPart.Rest];
                ImGui.Text($"Frame {t.FrameMs:0.00} ms  ({t.Fps:0} fps)   ms a frame:");
                ImGui.PushStyleColor(ImGuiCol.Text, Quiet);
                ImGui.Text($"Warp {t.WarpAsked:0.##}x asked, {t.WarpReached:0.#}x reached");
                ImGui.Text($"Physics {physics:0.00}, {t.PhysicsSteps:0.#} steps a frame");
                ImGui.Text($"  Jolt {t[StepPart.Jolt]:0.00}  ground tiles {t[StepPart.Tiles]:0.00}  water and rafts {t[StepPart.Water]:0.00}");
                ImGui.Text($"  contact and nearness {t[StepPart.Contact]:0.00}  the rest {t[StepPart.Rest]:0.00}");
                ImGui.Text($"The flight's rules {t[StepPart.Rules]:0.00}");
                ImGui.Text($"Drawing: prepare {t[FramePart.Prepare]:0.00}  HUD {t[FramePart.Hud]:0.00}  shadows {t[FramePart.Shadows]:0.00}");
                ImGui.Text($"  record {t[FramePart.Record]:0.00}  submit {t[FramePart.Submit]:0.00}  waiting for the GPU {t[FramePart.Wait]:0.00}");
                if (double.IsNaN(t.GpuMs))
                    ImGui.Text("GPU n/a"u8);
                else
                {
                    ImGui.Text($"GPU {t.GpuMs:0.00}");
                    if (!shortened) // (on two lines, to keep the panel narrow: clear of a briefing across the top)
                    {
                        ImGui.Text($"  shadows {t[GpuPass.Shadows]:0.00}  scene {t[GpuPass.Scene]:0.00}  effects {t[GpuPass.Effects]:0.00}");
                        ImGui.Text($"  small views {t[GpuPass.SmallViews]:0.00}  HUD {t[GpuPass.Hud]:0.00}");
                    }
                }
                if (!shortened) // (on two lines, as above: the vessels in the physics, then those out of it)
                {
                    ImGui.Text($"Vessels: {t.Awake} awake, {t.Frozen} frozen, {t.Afloat} afloat;  {t.Tiles} ground tiles laid");
                    ImGui.Text($"  {t.UnderFormula} under formula (out of the physics)");
                }
                ImGui.PopStyleColor();
            }
            ImGui.EndGroup();
            // Only ever wider, so it isn't moved about by its numbers' widths from frame to frame.
            _timingsWidth = MathF.Max(_timingsWidth, ImGui.GetItemRectSize().X + 2f * ImGui.GetStyle().WindowPadding.X);
            ImGui.PopFont();
            _timingsRect = ThisWindow();
        }
        else
            _timingsRect = RectangleF.Empty;
        ImGui.End();
    }

    /// <summary>
    /// Once there is more than the rocket: this flight's vessels, which one is being flown, how each
    /// is doing, and which are just debris (they can't be flown); then, collapsed, everything else
    /// in the world (left by earlier flights, or there from the start).
    /// </summary>
    private static void Vessels(Flight flight)
    {
        if (flight.WorldVessels.Count < 2)
            return;
        ImGui.Spacing();
        ImGui.Text("Vessels"u8);
        ImGui.Separator();
        foreach (var vessel in flight.Vessels)
            VesselLine(flight, vessel);
        var others = flight.WorldVessels.Count - flight.Vessels.Count;
        if (others > 0 && ImGui.TreeNode($"Also in this world: {others}###others"))
        {
            foreach (var vessel in OthersByKind(flight))
                VesselLine(flight, vessel);
            ImGui.TreePop();
        }
    }

    private static void VesselLine(Flight flight, Vessel vessel)
    {
        var flown = vessel == flight.Controlled;
        var (state, color) = Status(vessel, brief: true);
        if (flown && color == Quiet)
            color = new float4(1, 1, 1, 1);
        var kind = vessel.Guided ? "" : "  (debris)";
        ImGui.TextColored(color, Escape($"{(flown ? ">" : " ")} {vessel.Name}{kind}   {state}"));
    }

    /// <summary>The world's vessels that aren't this flight's, by kind: satellites and landers, then stages, then debris.</summary>
    private static IEnumerable<Vessel> OthersByKind(Flight flight) => flight.WorldVessels
        .Where(v => v.Flight != flight.FlightNumber)
        .OrderBy(v => !v.Guided ? 2 : v.CurrentStage.Kind is StageKind.Satellite or StageKind.Lander ? 0 : 1);

    /// <summary>How <paramref name="vessel"/> is doing, in a few words, and a colour for it. <paramref name="brief"/> leaves out all but its height while it flies.</summary>
    internal static (string Text, float4 Color) Status(Vessel vessel, bool brief) => vessel.State switch
    {
        FlightState.Crashed when vessel.CurrentStage.Kind == StageKind.Fairing =>
            (vessel.Body.IsSea(vessel.Body.Up(vessel.Position)) ? "sunk" : "on the ground", Quiet), // it is taken out of the world where it came down
        FlightState.Crashed => ("destroyed", Bad),
        FlightState.Landed => (!brief && vessel is { LandedOnTarget: true, LandingSite: { } site } ? $"landed on the {SiteName(site)}" : "landed", Good),
        FlightState.Floating => ("floating", Good),
        _ when vessel is { AchievedOrbit: true, OrbitStable: true } =>
            (brief ? "in orbit" : $"in orbit, {vessel.Periapsis:0} to {vessel.Apoapsis:0} m", Good),
        _ when brief => ($"{vessel.Altitude:0} m", Quiet),
        _ => ($"{vessel.Altitude:0} m, {(vessel.VerticalSpeed < 0 ? "falling" : "rising")} {MathF.Abs(vessel.VerticalSpeed):0} m/s, fuel {vessel.Fuel * 100:0}%", Quiet),
    };

    /// <summary>How wide the credits panel is (its text wraps to <see cref="PanelWidth"/>, inside the window's padding), to keep clear of it.</summary>
    private const float CreditsWidth = PanelWidth + 2 * 12f;

    private const ImGuiWindowFlags SmallViewFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoBringToFrontOnFocus;

    /// <summary>
    /// Bottom left, once the rocket has come apart: the small views, side by side (4:3, as big as
    /// <see cref="Scene.SmallViewMaxHeight"/>, smaller to fit between the left edge and the credits),
    /// and above them, buttons to add a view and to show the controls (in place of the views,
    /// which are left undrawn meanwhile). Each view is labelled with its vessel and how it is
    /// doing, and has a button to close it; clicking it flies that vessel. Returns how far up the
    /// strip reaches (for the caption to sit above it), or null if there is no strip.
    /// </summary>
    private float? Strip(Flight flight, Views views, float2 display, BodyFrames frames)
    {
        _stripRect = RectangleF.Empty; // the views and the buttons over them, as they are drawn
        if (flight.WorldVessels.Count < 2)
            return null;

        const float gap = StripGap, header = 40f;
        if (_controlsOpen)
        {
            foreach (var view in views.Small)
                view.Size = Vector2.Zero; // not drawn
            return StripButtons(flight, views, _openList.At.Y - _controlsHeight - gap, _openList.At.X);
        }
        var small = views.Small;
        var available = display.X - 2 * Margin - (_creditsShown ? CreditsWidth + 10f : 0f);
        var (size, top, buttonsBottom) = StripPlace(new Vector2(display.X, display.Y), small.Count, available,
            _instrumentsRect.IsEmpty ? null : _instrumentsRect.Bottom);
        var (width, height) = (size.X, size.Y);

        Views.SmallView? closed = null;
        for (var i = 0; i < small.Count; i++)
        {
            var view = small[i];
            var vessel = view.Vessel;
            view.Min = new Vector2(Margin + i * (width + gap), top);
            view.Size = new Vector2(width, height);
            var min = new float2(view.Min.X, view.Min.Y);
            var max = min + new float2(width, height);
            var flyable = Flight.CanFly(vessel);

            ImGui.SetNextWindowPos(min);
            ImGui.SetNextWindowSize(new float2(width, height));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0, 0));
            if (ImGui.Begin($"##view{vessel.Id}", SmallViewFlags))
            {
                _stripRect = Union(_stripRect, ThisWindow());
                // A dark band behind the label, so it reads against bright sky, and a frame round it
                // all: flashing while anything is coming at its vessel (red, or amber for a near miss), to draw the player's eye
                // to it (switching to it gives the main view's detail); nothing is drawn inside it.
                var draw = ImGui.GetWindowDrawList();
                draw.AddRectFilled(min, new float2(max.X, min.Y + header), new ImColor8(0, 0, 0, 120));
                var frame = flight.ComingAt(vessel) is { } coming && FlashOn(ImGui.GetTime()) ? ComingColor(coming)
                    : flyable ? new ImColor8(235, 235, 240, 200) : new ImColor8(140, 140, 150, 160);
                draw.AddRect(min, max, frame, 0f, default, 1.5f);

                var (status, color) = Status(vessel, brief: false);
                ImGui.SetCursorPos(new float2(6, 3));
                ImGui.Text(vessel.Guided ? vessel.Name : $"{vessel.Name} (debris)");
                ImGui.SetCursorPos(new float2(6, 20));
                ImGui.TextColored(color, Escape(status));
                ImGui.SetCursorPos(new float2(width - 24f, 3));
                if (ImGui.SmallButton($"x##close{vessel.Id}"))
                    closed = view;

                // The rest of it is one big button: click to fly this vessel.
                ImGui.SetCursorPos(new float2(0, header));
                if (ImGui.InvisibleButton($"##fly{vessel.Id}", new float2(width, MathF.Max(1f, height - header))) && flyable)
                    views.Swap(view);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Escape(!flyable ? "Debris: it can't be flown"
                        : vessel.Flight == flight.FlightNumber ? $"Click (or Tab) to fly the {vessel.Name.ToLowerInvariant()}"
                        : $"Click to fly the {vessel.Name.ToLowerInvariant()} (from an earlier flight: Tab only goes round this one's)"));

                // Its landing site, if it has one and it is in sight (a site is the planet's: in its camera's frame, its vessel's body's).
                if (vessel is { State: FlightState.Flying, LandingSite: { } site }
                    && view.Camera.ToScreen(frames.FromScene(view.Body, site + Planet.Up(site) * Scene.PadTop), view.Size, out var at, out _)
                    && at.X > 0 && at.Y > header && at.X < width && at.Y < height)
                {
                    var p = min + new float2(at.X, at.Y);
                    draw.AddCircle(p, 7f, new ImColor8(242, 196, 15), 0, 2f);
                }

                // Given to a vessel at a body whose physics isn't made: the link-up over it, under
                // its label, while that physics is made behind it (Flight.OpenView).
                if (flight.ViewLink(vessel) is { } link)
                    LinkStatic(draw, link, new float2(min.X, min.Y + header), max, 24, 14, 1.1f, ++_linkFrames);
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }
        if (closed is not null)
            views.Close(closed, flight);
        return StripButtons(flight, views, buttonsBottom, Margin);
    }

    /// <summary>
    /// Where the strip goes on a screen <paramref name="display"/> pixels across with
    /// <paramref name="count"/> small views, <paramref name="available"/> pixels wide for them: each
    /// view's size (4:3, as big as <see cref="Scene.SmallViewMaxHeight"/>, smaller to fit), the
    /// views' top (along the bottom of the screen) and the bottom of the buttons' row over them.
    /// The buttons go below the instruments' panel, whose bottom is <paramref name="instrumentsBottom"/>
    /// (null while it isn't drawn), however many lines it has: where the panel reaches down past
    /// where they would be, they move down with it, and the views under them are made smaller to fit,
    /// down to <see cref="MinSmallViewHeight"/>. Under a panel taller still (a small window), the
    /// views stay that size and the buttons just over them, on the panel's last lines: the views
    /// and their buttons are never drawn over each other.
    /// </summary>
    internal static (Vector2 ViewSize, float ViewsTop, float ButtonsBottom) StripPlace(Vector2 display, int count, float available, float? instrumentsBottom)
    {
        const float gap = StripGap;
        var height = MathF.Min(Scene.SmallViewMaxHeight, display.Y * 0.28f); // below the main vessel's rings
        var width = height * 4f / 3f;
        if (count > 0 && count * width + (count - 1) * gap > available)
        {
            width = MathF.Max(40f, (available - (count - 1) * gap) / count);
            height = width * 0.75f;
        }
        var bottom = display.Y - Margin;
        var buttonsBottom = count > 0 ? bottom - height - gap : bottom;
        if (instrumentsBottom is { } under && buttonsBottom - StripButtonsHeight < under + gap)
        {
            buttonsBottom = MathF.Min(under + gap + StripButtonsHeight, count > 0 ? bottom - MinSmallViewHeight - gap : bottom);
            if (count > 0)
            {
                height = MathF.Max(MinSmallViewHeight, MathF.Min(height, bottom - buttonsBottom - gap));
                width = height * 4f / 3f;
            }
        }
        return (new Vector2(width, height), count > 0 ? bottom - height : bottom, buttonsBottom);
    }

    /// <summary>The least a small view is made, to fit under the strip's buttons below a tall instruments' panel: its label and a little of the view.</summary>
    private const float MinSmallViewHeight = 60f;

    /// <summary>The gap between the small views (or the open controls list) and the strip's buttons over them.</summary>
    private const float StripGap = 8f;

    /// <summary>How tall the strip's buttons' row is, from its top to the bottom it is put above.</summary>
    private const float StripButtonsHeight = 28f;

    /// <summary>The panels the open controls list keeps clear of, as last drawn (empty while one isn't drawn).</summary>
    private RectangleF _instrumentsRect, _missionRect, _creditsRect, _timingsRect;

    /// <summary>The open controls list's bottom-left corner, and its text's size (of the HUD's): see <see cref="OpenListPlace"/>.</summary>
    private (Vector2 At, float Scale) _openList = (new Vector2(Margin, 0f), 1f);

    /// <summary>
    /// How large the open controls list is with its text at <paramref name="scale"/> (of the
    /// HUD's), measured from its lines as ImGui fits a panel round them (see
    /// <see cref="ControlsHelp"/>): so it is placed by its true size from the first frame it is
    /// open, not by the size it was last drawn at.
    /// </summary>
    private static Vector2 ControlsListSize(ImFontPtr font, float scale)
    {
        ImGui.PushFont(font, FontSize * scale);
        var width = 0f;
        for (var i = 0; i < ControlLineCount; i++)
            width = MathF.Max(width, ImGui.CalcTextSize(ControlLine(i)).X);
        var style = ImGui.GetStyle();
        var height = ControlLineCount * ImGui.GetTextLineHeightWithSpacing() - style.ItemSpacing.Y;
        ImGui.PopFont();
        return new Vector2(MathF.Ceiling(width + 2f * style.WindowPadding.X), MathF.Ceiling(height + 2f * style.WindowPadding.Y));
    }

    /// <summary>How many lines the open controls list has (see <see cref="ControlLine"/>).</summary>
    private const int ControlLineCount = 20;

    /// <summary>The open controls list's line <paramref name="i"/>: the controls, the same for every mission.</summary>
    private static ReadOnlySpan<byte> ControlLine(int i) => i switch
    {
        0 => "Shift / Ctrl    throttle up / down"u8,
        1 => "Z / X           full throttle / cut engine"u8,
        2 => "Left drag       on the rocket's rings: steer (held while held)"u8,
        3 => "W / S           lean the nose forward / back (its own frame)"u8,
        4 => "A / D           lean it left / right (D: east, off the pad)"u8,
        5 => "Right drag      orbit the rocket;  wheel: zoom"u8,
        6 => "Right click     on the vessel: its actions (bay doors, legs)"u8,
        7 => "Middle drag     look around: turn the view in place"u8,
        8 => "M               zoom right out to the planet and path (and back)"u8,
        9 => "Click a marker  make it your target (again, or elsewhere: none)"u8,
        10 => "Click the path  plan a burn there (zoomed out); pull its handles"u8,
        11 => "Space           launch, then drop a stage (any time)"u8,
        12 => "G               landing legs"u8,
        13 => "P / R           prograde / retrograde hold (with / against travel)"u8,
        14 => "Frame buttons   Auto, Surface, Orbit, Target: what speeds and holds go by"u8,
        15 => "Tab             fly another vessel (or click its view)"u8,
        16 => ", / .           time: slower / faster (1/4x to 32x warp)"u8,
        17 => "H               this list: open or fold it (over the small views)"u8,
        18 => "F2              hide the HUD, for a screenshot (and back)"u8,
        19 => "Esc             pause: restart, missions, worlds, quit"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };

    /// <summary>The sizes of text the open controls list tries, largest first, to find room clear of the other panels.</summary>
    private static ReadOnlySpan<float> OpenListScales => [1f, 0.85f, 0.7f];

    /// <summary>The smallest rectangle round both, either of which may be empty.</summary>
    private static RectangleF Union(RectangleF a, RectangleF b) => a.IsEmpty ? b : b.IsEmpty ? a : RectangleF.Union(a, b);

    /// <summary>The window being drawn's place on the screen, between its Begin and End.</summary>
    private static RectangleF ThisWindow()
    {
        var (at, size) = (ImGui.GetWindowPos(), ImGui.GetWindowSize());
        return new RectangleF(at.X, at.Y, size.X, size.Y);
    }

    /// <summary>
    /// Where the open controls list (and the strip's buttons over it) goes, as its bottom-left
    /// corner, and how large its text is: the first of these where it stays on the screen and
    /// clear of the other panels (the instruments, the mission, the timings, the credits, the compass). At the
    /// bottom left, under the instruments (as in a large window); beside them, at the bottom (at
    /// 1280x720 the list leaves the instruments too little room, above the air with an orbit shown
    /// or, with the strip's buttons over it, even on the pad); beside them, above the credits
    /// (where it would reach the credits). Each is as far left as it can be, so it covers no more
    /// of the view's middle than it must; and if none has room, the same again with smaller text
    /// (a small window). In a window too small even for that, at the bottom left, smallest.
    /// </summary>
    private (Vector2 At, float Scale) OpenListPlace(Flight flight, float2 display)
    {
        var buttons = flight.WorldVessels.Count >= 2 ? StripGap + StripButtonsHeight : 0f; // the strip's buttons, when there is a strip
        var bottom = display.Y - Margin;
        // Beside the instruments, or at the left edge while they aren't drawn.
        var beside = _instrumentsRect.IsEmpty ? Margin : _instrumentsRect.Right + StripGap;
        var compass = new RectangleF(display.X / 2 - CompassWidth / 2, Margin, CompassWidth, CompassBottom - Margin);
        ReadOnlySpan<Vector2> places =
        [
            new(Margin, bottom),
            new(beside, bottom),
            new(beside, _creditsRect.IsEmpty ? bottom : _creditsRect.Top - StripGap),
        ];
        foreach (var scale in OpenListScales)
        {
            // Its size with text this size, measured now (not as last drawn, which the first frame
            // after H would place it by, at whatever size it had before).
            var size = ControlsListSize(_font, scale);
            var (width, height) = (size.X, size.Y + buttons);
            foreach (var place in places)
            {
                var list = new RectangleF(place.X, place.Y - height, width, height);
                var onScreen = list.Left >= Margin - 0.5f && list.Top >= Margin - 0.5f
                    && list.Right <= display.X - Margin + 0.5f && list.Bottom <= bottom + 0.5f;
                var room = RectangleF.Inflate(list, StripGap - 1f, StripGap - 1f);
                if (onScreen && !Overlaps(_instrumentsRect) && !Overlaps(_missionRect) && !Overlaps(_timingsRect) && !Overlaps(_creditsRect) && !room.IntersectsWith(compass))
                {
                    _controlsHeight = size.Y;
                    return (place, scale);
                }

                bool Overlaps(RectangleF panel) => !panel.IsEmpty && room.IntersectsWith(panel);
            }
        }
        _controlsHeight = ControlsListSize(_font, OpenListScales[^1]).Y;
        return (new Vector2(Margin, bottom), OpenListScales[^1]);
    }

    /// <summary>
    /// The strip's buttons, just above <paramref name="bottom"/>, from <paramref name="left"/>
    /// across: add a view (of any vessel in the world without one), and the controls list. Returns
    /// how far up they reach.
    /// </summary>
    private float StripButtons(Flight flight, Views views, float bottom, float left)
    {
        var buttonsTop = bottom - StripButtonsHeight;
        ImGui.SetNextWindowPos(new float2(left, buttonsTop));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0, 2));
        if (ImGui.Begin("##strip"u8, (PanelFlags & ~ImGuiWindowFlags.NoInputs) | ImGuiWindowFlags.NoBackground))
        {
            if (ImGui.SmallButton(views.Full ? "Views full"u8 : "Add view"u8) && !views.Full)
                ImGui.OpenPopup("##add-view"u8);
            if (!views.Full)
                _buttons.Add(("Add view", ItemMiddle()));
            // The list opens upwards from the button, clear of the views below it.
            ImGui.SetNextWindowPos(new float2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMin().Y - 4f), ImGuiCond.Appearing, new float2(0, 1));
            if (ImGui.BeginPopup("##add-view"u8))
            {
                ImGui.TextColored(Quiet, "Add a view of:"u8);
                var any = false;
                foreach (var vessel in flight.Vessels.Concat(OthersByKind(flight))) // this flight's first
                {
                    if (!views.CanOpen(vessel))
                        continue;
                    any = true;
                    var kind = vessel.Guided ? "" : " (debris)";
                    // "###": its ID is the vessel's alone, not the label's, whose height changes every frame.
                    // In full (e.g. its orbit), so several of a kind can be told apart.
                    if (ImGui.Selectable($"{vessel.Name}{kind}:  {Status(vessel, brief: false).Text}###add{vessel.Id}"))
                        views.Open(vessel, flight);
                    _offered.Add((vessel, ItemMiddle()));
                }
                if (!any)
                    ImGui.TextColored(Quiet, "Every vessel has a view."u8);
                ImGui.EndPopup();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton(_controlsOpen ? "Back to the views (H)"u8 : "Controls (H)"u8))
                ToggleControls();
            _buttons.Add(("Controls", ItemMiddle()));
            _stripRect = Union(_stripRect, ThisWindow());
        }
        ImGui.End();
        ImGui.PopStyleVar();
        return buttonsTop - 6f;
    }

    /// <summary>Whether a vessel has come down and stays down (landed or afloat): its message is said only while that is new.</summary>
    private static bool Down(Vessel vessel) => vessel.State is FlightState.Landed or FlightState.Floating;

    /// <summary>What staging does now: drops the bottom stage, or (from an upper stage) jettisons the fairing, then releases the satellite or the lander on top.</summary>
    private static string Separating(Flight flight) =>
        flight.JettisonsFairing ? "jettison the fairing"
        : flight.Controlled.Vehicle.Stages[flight.Stage + 1].Kind switch
        {
            StageKind.Satellite => "release the satellite",
            StageKind.Lander => "release the lander",
            _ => $"drop the {flight.CurrentStage.Name.ToLowerInvariant()}",
        };

    /// <summary>
    /// The message's title (and its colour) once the vessel being flown has come down, landed,
    /// afloat or lost, with what follows it added to <paramref name="lines"/>; <paramref name="another"/>
    /// is the way to fly on, if there is another vessel to.
    /// </summary>
    internal static (string Title, float4 Color) Ending(Flight flight, string? another, List<(string Text, float4 Color)> lines)
    {
        var vessel = flight.Controlled;
        string title;
        var color = Good;
        switch (vessel.State)
        {
            case FlightState.Landed:
                // Off target, or short of the height, only means something for a vessel the mission
                // wanted landed somewhere: anything else has just landed.
                if (flight.LandingSite is not { } site)
                {
                    title = "LANDED";
                    lines.Add(($"Touchdown {flight.TouchdownSpeed:0.0} m/s,  {flight.Fuel * 100:0}% fuel left", Quiet));
                }
                else
                {
                    var target = flight.ReachedTarget || vessel.CameAway; // a part that came away had no height to reach
                    (title, color) = flight.LandedOnTarget
                        ? (target ? "LANDED ON TARGET" : "LANDED", Good)
                        : ("LANDED OFF TARGET", Warning);
                    if (!target)
                        lines.Add(($"But it never reached {flight.Mission.TargetAltitude:0} m: climb higher next time.", Warning));
                    else if (!flight.LandedOnTarget)
                        lines.Add(($"It needs to come down within {Scene.LandingTargetRadius:0} m of the {SiteName(site)}'s centre.", Warning));
                    lines.Add(($"Touchdown {flight.TouchdownSpeed:0.0} m/s,  {flight.DistanceToLandingSite:0.0} m from the centre,  {flight.Fuel * 100:0}% fuel left", Quiet));
                }
                break;
            case FlightState.Floating:
                // Safe, wherever it is; but no landing site is in the sea, so a vessel the mission
                // wanted landed somewhere has come down off target, as one landed beside it has.
                if (flight.LandingSite is not { } floatingSite)
                {
                    title = "SPLASHDOWN";
                    lines.Add(($"Splashdown {flight.TouchdownSpeed:0.0} m/s,  {flight.Fuel * 100:0}% fuel left", Quiet));
                }
                else
                {
                    (title, color) = ("SPLASHDOWN OFF TARGET", Warning);
                    lines.Add(($"It needs to come down within {Scene.LandingTargetRadius:0} m of the {SiteName(floatingSite)}'s centre.", Warning));
                    lines.Add(($"Splashdown {flight.TouchdownSpeed:0.0} m/s,  {flight.DistanceToLandingSite:0.0} m from the centre,  {flight.Fuel * 100:0}% fuel left", Quiet));
                }
                break;
            default:
                (title, color) = ($"{vessel.Name.ToUpperInvariant()} LOST", Bad);
                lines.Add((flight.CrashReason ?? "", Quiet));
                lines.Add((another ?? "To try again: Restart flight, in the menu (Esc).", new float4(1, 1, 1, 1)));
                return (title, color);
        }
        if (flight.Mission.Score(flight) is { } score)
            lines.Add(($"Score  {score}", new float4(1, 1, 1, 1)));
        lines.Add((another ?? "To fly again: Restart flight, in the menu (Esc).", new float4(1, 1, 1, 1)));
        return (title, color);
    }

    /// <summary>A landing site's name, as it goes in a sentence ("the drone ship").</summary>
    private static string SiteName(Vector3 site) => Sites.NameOf(site).ToLowerInvariant();

    /// <summary>The middle of the screen: what's happening now, and what to do next.</summary>
    private void Message(double time, Flight flight, float2 display)
    {
        string? title = null;
        var titleColor = new float4(1, 1, 1, 1);
        List<(string Text, float4 Color)> lines = [];

        // What happens to the vessel being flown (the flight goes on while any other still flies).
        var vessel = flight.Controlled;
        var another = flight.CanSwitch ? "Press Tab to fly another vessel, or restart from the menu (Esc)." : null;
        switch (vessel.State)
        {
            case FlightState.Ready:
                title = flight.Mission.Name;
                foreach (var line in flight.Mission.Briefing.Split('\n'))
                    lines.Add((line, Quiet));
                if (flight.ClearedFromPad > 0)
                    lines.Add((flight.ClearedFromPad == 1
                        ? "Something left on the launch pad has been cleared away, to make room."
                        : $"{flight.ClearedFromPad} things left on the launch pad have been cleared away, to make room.", Warning));
                lines.Add(("SPACE lights the engine, at the throttle set.", new float4(1, 1, 1, 1)));
                lines.Add(("Another mission, or another world: the menu (Esc).", Quiet));
                break;
            case FlightState.Flying:
                var landing = flight.LandingSite is not null;
                var lifted = time - _liftedOffAt;
                if (time - _stagedAt < 2) // first: staging a landed vessel makes it flying, but it hasn't lifted off
                    title = "STAGE SEPARATION";
                else if (lifted < 2)
                    title = "LIFT-OFF";
                else if (time - _orbitAt < 6)
                {
                    title = "ORBIT ACHIEVED";
                    titleColor = Good;
                    lines.Add(($"A stable orbit, {flight.Periapsis:0} to {flight.Apoapsis:0} m up. Fly on, or restart from the menu (Esc).", Good));
                    if (flight.Mission.Score(flight) is { } orbitScore)
                        lines.Add(($"Score  {orbitScore}", new float4(1, 1, 1, 1)));
                }
                else if (time - _fairingAt < 2)
                    title = "FAIRING JETTISONED";
                else if (time - _leftAirAt < 5)
                {
                    title = "ABOVE THE ATMOSPHERE";
                    titleColor = Good;
                    lines.Add(("No air: no drag, and nothing turns the nose now but your steering.", Good));
                    if (!landing && !flight.AchievedOrbit && flight.Mission.HasObjectives)
                        lines.Add(($"Now burn sideways, along your direction of travel, until the periapsis is above {Scene.AtmosphereHeight:0} m.", Good));
                }
                else if (time - _enteredAirAt < 4)
                {
                    title = "ENTERING THE ATMOSPHERE";
                    titleColor = Warning;
                    lines.Add(("The air is back: drag slows you, and turns the nose along your direction of travel.", Warning));
                }
                else if (flight.LandingSite is { } site && time - _targetReachedAt < 4)
                {
                    title = "TARGET ALTITUDE";
                    lines.Add(($"Now fly it to the {SiteName(site)} (the yellow marker) and set it down.", Good));
                }
                if (flight.Fuel <= 0f && flight.CanStage)
                    lines.Add(($"{flight.CurrentStage.Name} empty: press SPACE to {Separating(flight)}", Warning));
                else if (flight.CanStage && flight.Fuel <= flight.CurrentStage.LandingReserve)
                    lines.Add(($"Landing fuel left in the {flight.CurrentStage.Name.ToLowerInvariant()}: {Separating(flight)} now (SPACE), and it can fly back down (Tab)", Good));
                else if (flight.Fuel <= 0f)
                    lines.Add(("Out of fuel!", Bad));
                else if (flight.Fuel < 0.15f)
                    lines.Add(("Fuel low", Warning));
                if (landing && !flight.LegsWanted && (flight.ReachedTarget || vessel.CameAway) && flight.VerticalSpeed < 0 && flight.Ground < 250)
                    lines.Add(("Deploy the landing legs: G", Warning));
                if (TooFastLine(vessel.Body, flight.Ground, flight.VerticalSpeed, vessel.MaxGround,
                        -vessel.Body.Height(vessel.Body.Up(flight.Position)), vessel.Height) is { } tooFast)
                    lines.Add((tooFast, Bad));
                // How to get to orbit, for a mission that asks for it: with no objectives, nothing is asked.
                if (!landing && flight.Mission.HasObjectives && flight.Stage == 0 && lifted > 3 && lifted < 9 && flight.TiltDegrees < 5)
                {
                    lines.Add(("Tip over east as you climb: orbit is sideways, not up. Hover over the rocket and drag", Quiet));
                    lines.Add(("its orange ring a little towards E (or tap D), then let go: the air carries the turn on.", Quiet));
                }
                break;
            case FlightState.Landed or FlightState.Floating when time - _cameDownAt < Scene.CameDownMessageSeconds:
            case FlightState.Crashed:
                (title, titleColor) = Ending(flight, another, lines);
                break;
        }

        // The mission failing (a needed vessel destroyed) is news for a while, and stays said
        // over the wreck; flying on is still allowed.
        if (flight.Mission.Failed(flight) && (time - _failedAt < 6 || vessel.State == FlightState.Crashed))
        {
            var lost = flight.Mission.Steps.Where((step, i) => !step.Optional && GameOne.Mission.IsLost(i, flight)).First();
            if (title is null)
                (title, titleColor) = ("MISSION FAILED", Bad);
            else
                lines.Insert(0, ("MISSION FAILED", Bad));
            lines.Insert(title == "MISSION FAILED" ? 0 : 1, ($"\"{lost.Label}\" can't be done now: what it needed was destroyed.", Bad));
        }

        LastMessage = (title, lines);
        if (title is null && lines.Count == 0)
            return;

        // Below the compass.
        ImGui.SetNextWindowPos(new float2(display.X / 2, CompassBottom + 10f), ImGuiCond.Always, new float2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(title is null ? 0.35f : 0.45f);
        if (ImGui.Begin("##message"u8, PanelFlags))
        {
            if (title is not null)
            {
                ImGui.PushFont(_font, BigFontSize);
                Centred(title, titleColor);
                ImGui.PopFont();
            }
            foreach (var (text, color) in lines)
                Centred(text, color);
            _messageRect = ThisWindow();
        }
        ImGui.End();
    }

    private static void Centred(string text, float4 color)
    {
        var width = ImGui.CalcTextSize(text).X;
        var available = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (available - width) / 2));
        ImGui.TextColored(color, Escape(text));
    }

    /// <summary>
    /// The "Too fast!" line, judged for the body a vessel is coming down on (<paramref name="body"/>),
    /// or none: within <see cref="Scene.TooFastBelow"/> of what is under it, having been up off it
    /// further than that (<paramref name="maxGround"/>), and falling faster than a landing there
    /// survives. Where the body has a sea and the water under it (<paramref name="depth"/> deep) is
    /// deep enough to float in (about 1/<see cref="Scene.WaterBuoyancy"/> of its
    /// <paramref name="height"/>, upright), that is the water's, a splashdown's; else the ground's,
    /// by that body's pull (<see cref="Scene.TouchdownLimit"/>). Worded for what it would come down
    /// on, by the body's own name for its ground.
    /// </summary>
    internal static string? TooFastLine(GroundBody body, float ground, float verticalSpeed, float maxGround, float depth, float height)
    {
        if (ground >= Scene.TooFastBelow || maxGround <= Scene.TooFastBelow)
            return null;
        var water = body.HasSea && depth > height / Scene.WaterBuoyancy;
        var safe = water ? Scene.SafeSplashdownSpeed : Scene.TouchdownLimit(body, Scene.SafeTouchdownSpeed);
        if (verticalSpeed >= -safe)
            return null;
        return water ? "Too fast to splash down! Throttle up to slow down" : body.TooFastToTouchDown;
    }

    /// <summary>
    /// The warning's line (<see cref="Flight.Warning"/>), shown and read out in the same words:
    /// what is coming, by its kind, in how long and how fast it closes now; never which way to go,
    /// or whether a dodge is still possible. Past the closest approach, still overlapping, it is 0 s.
    /// </summary>
    internal static string WarningLine(in Collision o) =>
        $"COLLISION: {Collisions.Name(o)} in {Math.Max(0.0, o.Seconds):0.0} s ({o.ClosingSpeed:#,0} m/s)";

    /// <summary>
    /// The near miss's line (<see cref="Flight.NearMissWarning"/>), shown and read out in the same
    /// words: which vessel is passing, in how long to its closest and how fast it passes; never
    /// which way it passes, or which way to go.
    /// </summary>
    internal static string NearMissLine(in NearMiss m) =>
        $"NEAR MISS: {WorldLog.Named(m.Other)} in {Math.Max(0.0, m.Seconds):0.0} s ({m.PassSpeed:#,0} m/s)";

    /// <summary>
    /// The time line while the game is slowed for a fast collision (<see cref="Flight.Slowed"/>): how
    /// slow it runs now (made every frame, so it shows it deepening and easing back, for a sense of
    /// time) and what for, and the player's own slow motion when that is slower
    /// (<paramref name="playerRate"/>, the clock then); in the HUD's own culture, as the rest of it.
    /// </summary>
    /// <remarks>
    /// Under ten times slower, to a tenth (as it starts and eases back: "SLOWED 1/1.6x"), as a whole
    /// number read "1/1x" there, which looks like no slowing at all; a whole number past it
    /// ("SLOWED 1/37x").
    /// </remarks>
    internal static string SlowedLine(in Slowdown s, float playerRate = 1f)
    {
        var slower = 1f / s.Scale;
        var line = $"SLOWED 1/{slower.ToString(slower < 9.95f ? "0.0" : "0")}x for {s.Reason}";
        // The player's own slow motion slower still runs the clock (see Flight.TimeScale), so it is said too.
        return playerRate < s.Scale ? $"{line}, in slow motion 1/{1f / playerRate:0}x" : line;
    }

    /// <summary>
    /// The time line, under the vessel's name, and whether it is good news (a steady warp) rather
    /// than a warning; none at normal speed.
    /// </summary>
    internal static (string Text, bool Good)? TimeLine(Slowdown? slowed, float playerRate, float timeScale, float running)
    {
        // Slowed for a fast collision, the time line says so and why, whatever the player's own
        // rate (with it, if slower): never the rate keys' hint, as they do nothing then.
        if (slowed is { } s)
            return (SlowedLine(s, playerRate), false);
        if (timeScale < 1f)
            return ($"SLOW MOTION  1/{1f / timeScale:0}x   ( , and . )", false);
        if (timeScale > 1f && running < timeScale * 0.9f)
            return ($"WARP {timeScale:0}x  (running {running:0}x)   ( , and . )", false);
        if (timeScale > 1f)
            return ($"WARP {timeScale:0}x   ( , and . )", true);
        return null;
    }

    /// <summary>ImGui's coloured text is a printf format, so a literal % has to be written %%.</summary>
    private static string Escape(string text) => text.Replace("%", "%%");

    /// <summary>
    /// Top middle: a compass tape showing which way the camera faces, with markers for where the
    /// nose points (blue), where the rocket is going in the frame in use (white ring), in the target
    /// frame the way to the target and away from it (orange, with a cross and an X) and, when
    /// landing, the landing zone (yellow); and the bearing a vessel coming at the flown one comes
    /// from (a red tick down from the top edge), so it can be found behind the camera.
    /// </summary>
    private static void Compass(Flight flight, ChaseCamera camera, float2 display)
    {
        const float width = CompassWidth, height = 30f, shown = CompassShown;
        var draw = ImGui.GetBackgroundDrawList();
        var middle = display.X / 2;
        var left = middle - width / 2;
        var perDegree = width / shown;
        var bearings = CompassFor(flight, camera.Focus, camera.GroundForward);
        var facing = float.RadiansToDegrees(bearings.Facing);

        draw.AddRectFilled(new float2(left, Margin), new float2(left + width, Margin + height), new ImColor8(0, 0, 0, 115), 6f);
        var tick = new ImColor8(220, 220, 225, 200);
        for (var d = MathF.Ceiling((facing - shown / 2) / 10f) * 10f; d <= facing + shown / 2; d += 10f)
        {
            var x = middle + (d - facing) * perDegree;
            var bearing = ((int)MathF.Round(d) % 360 + 360) % 360;
            var major = bearing % 30 == 0;
            draw.AddLine(new float2(x, Margin + height - (major ? 10f : 5f)), new float2(x, Margin + height), tick, 1f);
            if (!major)
                continue;
            var label = bearing switch { 0 => "N", 90 => "E", 180 => "S", 270 => "W", _ => $"{bearing}" };
            var size = ImGui.CalcTextSize(label);
            var cardinal = bearing % 90 == 0;
            draw.AddText(new float2(x - size.X / 2, Margin + 2f), cardinal ? new ImColor8(255, 255, 255) : tick, label);
        }

        // Markers, held at the tape's ends when they are off it.
        if (bearings.Nose is { } nose)
            Mark(nose, new ImColor8(90, 205, 255), filled: true);
        if (bearings.Travel is { } travel)
            Mark(travel, new ImColor8(255, 255, 255), filled: false);
        if (bearings.Target is { } target)
        {
            TargetMark(target, away: false);
            TargetMark(target + MathF.PI, away: true);
        }
        if (bearings.Site is { } site)
            Mark(site, new ImColor8(242, 196, 15), filled: true);
        if (bearings.Coming is { } from && flight.ComingAt(flight.Controlled) is { } coming)
            DangerMark(from, ComingColor(coming));

        // Which way the camera faces, under the middle.
        draw.AddTriangleFilled(new float2(middle - 6f, Margin + height + 7f), new float2(middle + 6f, Margin + height + 7f),
            new float2(middle, Margin + height), new ImColor8(255, 255, 255));
        var readout = $"{(int)MathF.Round(facing) % 360:000}°";
        var readoutSize = ImGui.CalcTextSize(readout);
        draw.AddText(new float2(middle - readoutSize.X / 2, Margin + height + 8f), new ImColor8(255, 255, 255), readout);

        void Mark(float radians, ImColor8 color, bool filled)
        {
            var x = middle + TapeOffset(float.RadiansToDegrees(radians), facing) * perDegree;
            var y = Margin + height - 12f;
            if (filled)
                draw.AddTriangleFilled(new float2(x - 6f, y - 8f), new float2(x + 6f, y - 8f), new float2(x, y + 2f), color);
            else
                draw.AddCircle(new float2(x, y - 3f), 5f, color, 0, 2f);
        }

        void TargetMark(float radians, bool away)
        {
            var (r, g, b) = Paths.TargetColor;
            var color = new ImColor8(r, g, b);
            var at = new float2(middle + TapeOffset(float.RadiansToDegrees(radians), facing) * perDegree, Margin + height - 15f);
            const float size = 3f;
            var (one, two) = away ? (new float2(size, size), new float2(size, -size)) : (new float2(size, 0), new float2(0, size));
            draw.AddCircle(at, 6f, color, 0, 2f);
            draw.AddLine(at - one, at + one, color, 1.5f);
            draw.AddLine(at - two, at + two, color, 1.5f);
        }

        // Down from the top edge, apart from the marks along the bottom, outlined so it reads over
        // the tape's labels.
        void DangerMark(float radians, ImColor8 color)
        {
            var x = middle + TapeOffset(float.RadiansToDegrees(radians), facing) * perDegree;
            var (one, two, tip) = (new float2(x - 7f, Margin), new float2(x + 7f, Margin), new float2(x, Margin + 13f));
            draw.AddTriangleFilled(one, two, tip, color);
            draw.AddTriangle(one, two, tip, new ImColor8(0, 0, 0, 170), 1f);
        }
    }

    /// <summary>
    /// The compass's bearings, in radians from north by the flown vessel's body's north and up
    /// (<see cref="GroundBody.Bearing"/>): which way the camera faces (from <paramref name="focus"/>
    /// along <paramref name="groundForward"/>), and each mark it has now (none where it isn't shown).
    /// </summary>
    internal readonly record struct CompassBearings(float Facing, float? Nose, float? Travel, float? Target, float? Site, float? Coming);

    /// <summary>The compass's bearings now (see <see cref="CompassBearings"/>), as the tape draws them.</summary>
    internal static CompassBearings CompassFor(Flight flight, Vector3 focus, Vector3 groundForward)
    {
        var body = flight.Body;
        var position = flight.Position;
        var up = flight.LocalUp;
        // Where the nose points, once it leans.
        float? nose = flight.TiltDegrees > 1f ? body.Bearing(position, flight.Aim - up * Vector3.Dot(flight.Aim, up)) : null;
        // The way it is going as the holds steer by it (in the frame in use), so a held nose sits on it.
        var velocity = flight.FrameVelocity;
        float? travel = (velocity - up * Vector3.Dot(velocity, up)).Length() > flight.MarkerFloor && flight.State == FlightState.Flying
            ? body.Bearing(position, velocity) : null;
        // In the target frame, the way to the target (and away from it, opposite), as on the rings.
        float? target = flight.TowardsTarget is { } towards && (towards - up * Vector3.Dot(towards, up)).Length() > 0.5f
            ? body.Bearing(position, towards) : null;
        // The mission's landing site, where its body's frame has it (from another body, across the bodies).
        float? site = flight.Controlled.LandingSiteHere is { } at && flight.DistanceToLandingSite > 3f
            ? body.Bearing(position, at - position) : null;
        // Coming from (nearly) straight above or below, it has no bearing to speak of: a tick would
        // swing across the tape from frame to frame, so none (as the target's marks).
        float? coming = flight.ComingAt(flight.Controlled) is { Other: { } comingVessel }
            && comingVessel.CentreOfMass - position is var from && (from - up * Vector3.Dot(from, up)).Length() > 0.5f
            ? body.Bearing(position, from) : null;
        return new CompassBearings(body.Bearing(focus, groundForward), nose, travel, target, site, coming);
    }

    /// <summary>The compass tape's span, in degrees across it.</summary>
    private const float CompassShown = 160f;

    /// <summary>
    /// How far along the compass tape, in degrees from its middle (right positive), a mark at
    /// <paramref name="bearingDegrees"/> sits with the camera facing <paramref name="facingDegrees"/>:
    /// the shorter way round, held at the tape's ends when it is off it, so a mark behind is still
    /// on the tape, at the end nearer the way to turn.
    /// </summary>
    internal static float TapeOffset(float bearingDegrees, float facingDegrees) =>
        Math.Clamp((bearingDegrees - facingDegrees + 540f) % 360f - 180f, -CompassShown / 2, CompassShown / 2);

    /// <summary>Where the compass's readout ends, for laying out what goes under it.</summary>
    private const float CompassBottom = Margin + 30f + 26f;

    /// <summary>How wide the compass's tape is.</summary>
    private const float CompassWidth = 440f;

    /// <summary>Bottom left (or wherever it is clear of the other panels: <see cref="OpenListPlace"/>): the controls, the same for every mission.</summary>
    private void ControlsHelp(float2 display)
    {
        ImGui.SetNextWindowPos(new float2(_openList.At.X, _openList.At.Y), ImGuiCond.Always, new float2(0, 1));
        ImGui.SetNextWindowBgAlpha(0.4f);
        if (ImGui.Begin("##controls"u8, PanelFlags))
        {
            ImGui.PushFont(_font, FontSize * _openList.Scale); // smaller only where it has no room otherwise (OpenListPlace)
            // (Placed by its size as ControlsListSize measures these lines: draw them as plain lines of text, as it counts them.)
            for (var i = 0; i < ControlLineCount; i++)
                ImGui.TextColored(Quiet, ControlLine(i));
            ImGui.PopFont();
            _controlsRect = ThisWindow();
        }
        ImGui.End();
    }

    /// <summary>Bottom left, while the list is folded: one line that opens it (as H does).</summary>
    private void ControlsFolded(float2 display)
    {
        ImGui.SetNextWindowPos(new float2(Margin, display.Y - Margin), ImGuiCond.Always, new float2(0, 1));
        ImGui.SetNextWindowBgAlpha(0.4f);
        if (ImGui.Begin("##controls-folded"u8, PanelFlags & ~ImGuiWindowFlags.NoInputs))
        {
            if (ImGui.SmallButton("Controls (H)"u8))
                ToggleControls();
            _buttons.Add(("Controls", ItemMiddle()));
            _controlsRect = ThisWindow();
        }
        ImGui.End();
    }

    /// <summary>Bottom right: the BRUTAL credits the licence asks for.</summary>
    private void Credits(float2 display)
    {
        // Pivot (1, 1) anchors the panel's bottom-right to that point.
        ImGui.SetNextWindowPos(new float2(display.X - Margin, display.Y - Margin), ImGuiCond.Always, new float2(1, 1));
        ImGui.SetNextWindowBgAlpha(0.55f);
        if (ImGui.Begin("##brutal-credits"u8, PanelFlags))
        {
            if (_hasLogo)
            {
                // Scaled uniformly to the panel width: the licence allows scaling, nothing else.
                var scale = PanelWidth / _logoSize.X;
                ImGui.Image(_logo, _logoSize * scale);
                ImGui.Spacing();
            }

            // The first line is the copyright notice, at full strength; the rest are quieter.
            // In a smaller font than the HUD's: every word of it still shows, in less room.
            ImGui.PushFont(_font, CreditsFontSize);
            ImGui.PushTextWrapPos(PanelWidth);
            for (var i = 0; i < _noticeLines.Length; i++)
            {
                if (i > 0)
                    ImGui.PushStyleColor(ImGuiCol.Text, Quiet);
                ImGui.TextWrapped(_noticeLines[i]);
                if (i > 0)
                    ImGui.PopStyleColor();
            }
            ImGui.PopTextWrapPos();
            ImGui.PopFont();
            _creditsRect = ThisWindow();
        }
        else
            _creditsRect = RectangleF.Empty;
        ImGui.End();
    }

    public void Dispose()
    {
        _platform.Dispose();
        ImGui.DestroyContext(_context);
    }
}
