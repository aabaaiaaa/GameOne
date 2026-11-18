namespace GameOne;

/// <summary>
/// The communications link-up shown as any vessel is switched to or carried on, and over a small
/// view given to a vessel at a body whose physics isn't made (<see cref="Flight"/>): pure flavour,
/// covering the moment the physics may take to make, as if a connection to the vessel were being
/// established. The picture breaks up into static while it connects (<see cref="Connecting"/>),
/// then clears over the last <see cref="Scene.LinkUpClearShare"/> of it (<see cref="Static"/>):
/// <see cref="Scene.LinkUpSeconds"/> of real time in all, however soon the vessel is flown in its
/// body's physics (<see cref="Ready"/>), so every link-up is the same length; longer only if that
/// takes longer. It tells the player nothing to do. Its state and clock are its own (real seconds,
/// handed in), so it is tested without the HUD, which only draws it (<see cref="Hud"/>).
/// </summary>
internal sealed class LinkUp
{
    private static double ClearSeconds => Scene.LinkUpSeconds * Scene.LinkUpClearShare;

    private double _began = double.NaN, _ready = double.NaN, _now;
    private int _frame;

    /// <summary>The vessel the link is to, while one is shown.</summary>
    public Vessel? Vessel { get; private set; }

    /// <summary>Whether it is shown: from the switch until the picture has cleared.</summary>
    public bool On => Vessel is not null;

    /// <summary>
    /// Whether the vessel is still waiting to be flown (its body's physics to be made, a frame after
    /// the link-up is first drawn, never in the frame it was asked in): see <see cref="Due"/>.
    /// </summary>
    public bool Waiting => On && double.IsNaN(_ready);

    /// <summary>Whether the link is still being made (the picture broken up), rather than clearing.</summary>
    public bool Connecting => On && _now < ConnectedAt;

    /// <summary>How broken up the picture is: 1 while connecting, easing to 0 as it clears; 0 when not shown.</summary>
    public float Static => !On ? 0f : Connecting ? 1f : (float)Math.Clamp((ConnectedAt + ClearSeconds - _now) / ClearSeconds, 0.0, 1.0);

    /// <summary>The readout's reading (<c>Link</c>): "connecting", then "connected" as the picture clears; none when not shown.</summary>
    public string? Reading => !On ? null : Connecting ? "connecting" : "connected";

    // When it is connected (the picture starting to clear): its length less its clearing, or later,
    // once the vessel is flown, if that is later.
    private double ConnectedAt => double.IsNaN(_ready) ? double.PositiveInfinity : Math.Max(_ready, _began + Scene.LinkUpSeconds - ClearSeconds);

    /// <summary>Starts a link-up to <paramref name="vessel"/> at real time <paramref name="now"/>, in frame <paramref name="frame"/> (the flight's count of them).</summary>
    public void Begin(Vessel vessel, double now, int frame) =>
        (Vessel, _began, _ready, _now, _frame) = (vessel, now, double.NaN, now, frame);

    /// <summary>Whether, waiting, its vessel may be flown in frame <paramref name="frame"/>: any frame after the one it began in, so it is drawn first.</summary>
    public bool Due(int frame) => Waiting && frame > _frame;

    /// <summary>The vessel is flown in its body's physics now, at real time <paramref name="now"/>: it connects at its set time, or from now if that has gone.</summary>
    public void Ready(double now) => (_ready, _now) = (now, now);

    /// <summary>Gives up the link-up at once (its vessel lost, or another switched to).</summary>
    public void Cancel() => (Vessel, _began, _ready) = (null, double.NaN, double.NaN);

    /// <summary>Moves it on to real time <paramref name="now"/>: once cleared, it is over.</summary>
    public void Step(double now)
    {
        _now = now;
        if (On && !double.IsNaN(_ready) && now >= ConnectedAt + ClearSeconds)
            Cancel();
    }
}
