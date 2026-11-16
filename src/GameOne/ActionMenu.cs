using System.Numerics;

namespace GameOne;

/// <summary>
/// The flown vessel's actions menu, opened by a right click on it in the main view (see
/// <see cref="ChaseCamera.TakeRightClick"/>) and drawn by the HUD as a popup where it was
/// clicked: its state, kept apart from the drawing. Its items are worked out again each frame
/// (<see cref="Keep"/>), so each says what it will do now, whatever a key has done meanwhile. It
/// closes on an item, a click elsewhere (the HUD's), a game menu, the map, its vessel no longer
/// flown or able to act, or its stages changed (staged: what it offered was another vessel's); an
/// item chosen is given to the flight only for the vessel it was chosen on, still flown, its stages
/// as they were.
/// </summary>
internal sealed class ActionMenu
{
    private readonly List<VesselAction> _items = new(4);
    private VesselAction? _chosen;
    private Vessel? _chosenFor;
    private (int First, int Last) _stages, _chosenStages; // its vessel's stages as it was opened, and as an item was chosen

    /// <summary>The vessel it is open on, or null while it is closed.</summary>
    public Vessel? Vessel { get; private set; }

    /// <summary>Whether it is open.</summary>
    public bool IsOpen => Vessel is not null;

    /// <summary>Where it was opened, in pixels from the screen's top left: where its popup goes.</summary>
    public Vector2 At { get; private set; }

    /// <summary>What its vessel can do now (<see cref="VesselActions.Of"/>), as last worked out.</summary>
    public IReadOnlyList<VesselAction> Items => _items;

    /// <summary>How many times it has been opened: the HUD opens its popup again for each.</summary>
    public int Opened { get; private set; }

    /// <summary>Opens it on <paramref name="vessel"/> at <paramref name="at"/>; or, for a vessel with no actions, closes it.</summary>
    public void Open(Vessel vessel, Vector2 at)
    {
        VesselActions.Of(vessel, _items);
        if (_items.Count == 0)
        {
            Close();
            return;
        }
        (Vessel, At, _stages) = (vessel, at, Stages(vessel));
        Opened++;
    }

    /// <summary>Closes it.</summary>
    public void Close()
    {
        Vessel = null;
        _items.Clear();
    }

    /// <summary>
    /// Once a frame, before it is drawn: closes it while a game menu is up (<paramref name="menuUp"/>)
    /// or the map is (<paramref name="zoomedOut"/>), or its vessel is no longer <paramref name="flight"/>'s
    /// flown one, has crashed, has staged since it was opened (the same vessel flown on, but now
    /// another: the lander staged away from its upper stage) or has no actions left; else works its
    /// items out again. An item chosen for a vessel no longer flown (or crashed, staged since, or
    /// under a game menu) is let go.
    /// </summary>
    public void Keep(Flight flight, bool menuUp, bool zoomedOut)
    {
        if (_chosenFor is { } chosenFor
            && (menuUp || chosenFor != flight.Controlled || chosenFor.State == FlightState.Crashed || Stages(chosenFor) != _chosenStages))
            (_chosen, _chosenFor) = (null, null);
        if (Vessel is not { } vessel)
            return;
        if (menuUp || zoomedOut || vessel != flight.Controlled || Stages(vessel) != _stages)
        {
            Close();
            return;
        }
        VesselActions.Of(vessel, _items);
        if (_items.Count == 0)
            Close();
    }

    /// <summary>Its item <paramref name="action"/> clicked: kept for the flight's next input (<see cref="TakeChosen"/>), and the menu closed.</summary>
    public void Choose(VesselAction action)
    {
        if (Vessel is null)
            return;
        (_chosen, _chosenFor, _chosenStages) = (action, Vessel, _stages);
        Close();
    }

    /// <summary>
    /// Where the popup's top left goes for a click at <paramref name="at"/>, the popup
    /// <paramref name="size"/>, on a screen <paramref name="display"/> across: at the click, but
    /// moved in from the right and bottom edges to stay <paramref name="margin"/> inside them (and
    /// never past the top or left one's margin, on a screen too small for it).
    /// </summary>
    internal static Vector2 Place(Vector2 at, Vector2 size, Vector2 display, float margin) =>
        Vector2.Max(new Vector2(margin), Vector2.Min(at, display - size - new Vector2(margin)));

    /// <summary>Which of its vehicle's stages <paramref name="vessel"/> is made of: what staging changes.</summary>
    private static (int First, int Last) Stages(Vessel vessel) => (vessel.Stage, vessel.TopStage);

    /// <summary>The item chosen since the last call, if any (for the vessel still flown: see <see cref="Keep"/>), for the flight's input.</summary>
    public VesselAction? TakeChosen()
    {
        var chosen = _chosen;
        (_chosen, _chosenFor) = (null, null);
        return chosen;
    }
}
