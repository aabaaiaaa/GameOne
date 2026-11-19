using System.Numerics;
using Brutal.GlfwApi;

namespace GameOne;

/// <summary>Something the player asks for once per key press, rather than by holding a key.</summary>
internal enum ControlAction
{
    LaunchOrStage,
    ToggleLegs,
    FullThrottle,
    CutThrottle,
    GameMenu,
    SwitchVessel,
    Slower,
    Faster,
    ToggleControlsList,
    ToggleZoomOut,
    ToggleHud,
}

/// <summary>
/// The keyboard, turned into a <see cref="FlightInput"/> each frame. Held keys (throttle, steering)
/// are read as they are; one-off presses (launch, legs, staging) are caught as they happen, so a
/// quick tap between two frames isn't missed. (Steering with the mouse is <see cref="NavigationControl"/>.)
/// </summary>
/// <remarks>
/// <para>The keys are for flying. Everything about the game rather than the flight (restarting,
/// missions, worlds, quitting) is in the game menu, on Esc, leaving the keys free for new controls. F2 is the one key about the view rather than the flight: it hides the HUD, for a clear screenshot.</para>
/// <list type="bullet">
///   <item>Shift / Ctrl: throttle up / down. Z: full throttle. X: cut the engine.</item>
///   <item>W A S D or the arrow keys: lean the nose forward, left, back or right, in the vessel's own frame (so the same whatever the camera does). On the pad, facing the starting camera, D leans it east, for the gravity turn.</item>
///   <item>Space: light the engine (at the throttle set), then drop a stage (whenever, empty or not). G: deploy or stow the landing legs.</item>
///   <item>Tab: fly the next vessel that can be flown (a dropped stage with guidance).</item>
///   <item>Comma / full stop (they read as &lt; and &gt;): time slower / faster: 1/4, 1/2, normal, then warp doubling from 2x to 16384x (see <see cref="Flight.Rates"/>).</item>
///   <item>M: zoom all the way out, to see the planet and the predicted path round it (or back to the vessel).</item>
///   <item>H: show the controls list over the small views (or hide it again).</item>
///   <item>F2: hide the whole HUD, for a screenshot (or show it again); a menu still shows over it.</item>
///   <item>Esc: pause in the game menu, or carry on.</item>
/// </list>
/// </remarks>
internal sealed class Controls
{
    private readonly GlfwWindow _window;
    private readonly HashSet<ControlAction> _pressed = [];

    public Controls(GlfwWindow window)
    {
        _window = window;
        window.OnKey += OnKey;
    }

    private void OnKey(GlfwWindow window, GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods)
    {
        // Typing into the HUD (renaming a world) isn't flying.
        if (action != GlfwKeyAction.Press || Brutal.ImGuiApi.ImGui.GetIO().WantTextInput)
            return;
        if (ActionOf(key) is { } a)
            _pressed.Add(a);
    }

    /// <summary>The action a key press asks for, if it asks for one (the steering and throttle keys are read as held instead).</summary>
    internal static ControlAction? ActionOf(GlfwKey key) => key switch
    {
        GlfwKey.Space => ControlAction.LaunchOrStage,
        GlfwKey.G => ControlAction.ToggleLegs,
        GlfwKey.Z => ControlAction.FullThrottle,
        GlfwKey.X => ControlAction.CutThrottle,
        GlfwKey.Escape => ControlAction.GameMenu,
        GlfwKey.Tab => ControlAction.SwitchVessel,
        GlfwKey.Comma => ControlAction.Slower,
        GlfwKey.Period => ControlAction.Faster,
        GlfwKey.H => ControlAction.ToggleControlsList,
        GlfwKey.M => ControlAction.ToggleZoomOut,
        GlfwKey.F2 => ControlAction.ToggleHud,
        _ => null,
    };

    /// <summary>Whether a steering key (W A S D or the arrows) is held, for the steering rings to show.</summary>
    public bool Steering => Held(GlfwKey.W) || Held(GlfwKey.A) || Held(GlfwKey.S) || Held(GlfwKey.D)
        || Held(GlfwKey.Up) || Held(GlfwKey.Down) || Held(GlfwKey.Left) || Held(GlfwKey.Right);

    /// <summary>
    /// This frame's input, taking the presses since the last call. Steering is in
    /// <paramref name="flown"/>'s own frame (<see cref="Vessel.Right"/>, <see cref="Vessel.Forward"/>),
    /// never the camera's: W leans its nose forward, D to its right, and holding one keeps it turning.
    /// </summary>
    public FlightInput Read(Vessel flown)
    {
        var throttle = (Held(GlfwKey.LeftShift) || Held(GlfwKey.RightShift) ? 1f : 0f)
            - (Held(GlfwKey.LeftControl) || Held(GlfwKey.RightControl) ? 1f : 0f);
        var right = (Held(GlfwKey.D) || Held(GlfwKey.Right) ? 1f : 0f) - (Held(GlfwKey.A) || Held(GlfwKey.Left) ? 1f : 0f);
        var forward = (Held(GlfwKey.W) || Held(GlfwKey.Up) ? 1f : 0f) - (Held(GlfwKey.S) || Held(GlfwKey.Down) ? 1f : 0f);
        var lean = flown.Right * right + flown.Forward * forward;
        var space = Take(ControlAction.LaunchOrStage);

        return new FlightInput(
            throttle,
            lean,
            Launch: space,
            Stage: space,
            ToggleLegs: Take(ControlAction.ToggleLegs),
            FullThrottle: Take(ControlAction.FullThrottle),
            CutThrottle: Take(ControlAction.CutThrottle),
            SwitchVessel: Take(ControlAction.SwitchVessel));
    }

    /// <summary>Whether <paramref name="action"/> was pressed since it was last taken (for the keys <see cref="Read"/> leaves: the game menu, the controls list, zooming out, and time, which the game puts in the flight's input only while it isn't paused).</summary>
    public bool Take(ControlAction action) => _pressed.Remove(action);

    private bool Held(GlfwKey key) => _window.GetKey(key) == GlfwKeyAction.Press;
}
