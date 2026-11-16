namespace GameOne;

/// <summary>The kinds of marker there are, each switched on or off in the settings.</summary>
internal enum MarkerKind
{
    /// <summary>Planets and moons.</summary>
    Body,
    /// <summary>This flight's vessels that can be flown.</summary>
    ThisFlight,
    /// <summary>Earlier flights' vessels that can be flown.</summary>
    EarlierFlight,
    /// <summary>What can't be flown: fairing halves, and the like.</summary>
    Debris,
    /// <summary>The launch pad and the landing zones.</summary>
    Site,
}

/// <summary>
/// The player's settings: the same in every world, saved beside them (see <see cref="WorldStore.SaveSettings"/>).
/// So far, which markers show, the launch site last picked, whether the timings panel shows, whether
/// frames are capped, and whether near misses are slowed.
/// </summary>
internal sealed record Settings
{
    /// <summary>Whether there are any markers at all.</summary>
    public bool Markers { get; init; } = true;

    public bool BodyMarkers { get; init; } = true;
    public bool ThisFlightMarkers { get; init; } = true;
    public bool EarlierFlightMarkers { get; init; } = true;
    public bool DebrisMarkers { get; init; } = true;
    public bool SiteMarkers { get; init; } = true;

    /// <summary>The launch site last picked in the mission menu, by name (see <see cref="LaunchSites.Named"/>): Home until another is.</summary>
    public string LaunchSite { get; init; } = "Home";

    /// <summary>Whether the timings panel shows (and <see cref="Timings"/> records): where each frame's time goes. Off until asked for.</summary>
    public bool ShowTimings { get; init; }

    /// <summary>
    /// Whether frames wait for the display's refresh (vsync), so the game runs at the screen's
    /// rate at most. Off (the default) draws as fast as it can: in
    /// development, how high the rate goes and where it dips is worth seeing, and a cap would hide it.
    /// On, the GPU idles between frames (cooler, quieter, a laptop's battery) and nothing tears.
    /// </summary>
    public bool CapFrameRate { get; init; }

    /// <summary>
    /// "Slow motion for near misses": whether the game slows as another vessel passes the flown one
    /// or a small view's within about its length, too fast to be seen (see
    /// <see cref="Collisions.NearestMiss"/>, <see cref="FlightInput.NearMisses"/>). Off by default:
    /// fun to have, but some playthroughs want it off. The forced slow-down
    /// for a fast collision is not the player's to turn off.
    /// </summary>
    public bool SlowNearMisses { get; init; }

    /// <summary>Whether markers of <paramref name="kind"/> show.</summary>
    public bool Shows(MarkerKind kind) => Markers && kind switch
    {
        MarkerKind.Body => BodyMarkers,
        MarkerKind.ThisFlight => ThisFlightMarkers,
        MarkerKind.EarlierFlight => EarlierFlightMarkers,
        MarkerKind.Debris => DebrisMarkers,
        _ => SiteMarkers,
    };
}
