namespace GameOne;

/// <summary>What a view is centred on (see <see cref="CameraCentre"/>).</summary>
internal enum CentreKind
{
    /// <summary>Its own vessel, in the chase view or the map.</summary>
    Own,
    /// <summary>A vessel, by id.</summary>
    Vessel,
    /// <summary>A body, by <see cref="GroundBody.Index"/>.</summary>
    Body,
    /// <summary>A launch or landing site, by its index in <c>Sites.All</c>.</summary>
    Site,
    /// <summary>One of the flown vessel's mission's <c>PointsOfInterest</c>, by index.</summary>
    Point,
}

/// <summary>What a view is centred on: <paramref name="Id"/> is a vessel id, a body's index, a site's index or a point of interest's index, by <paramref name="Kind"/>.</summary>
internal readonly record struct CameraCentre(CentreKind Kind, int Id)
{
    /// <summary>Its own vessel, in the chase view or the map.</summary>
    public static CameraCentre Own => new(CentreKind.Own, 0);
}

/// <summary>
/// How a vessel is looked at: the chase view's angles and distance, the map's (and whether
/// <paramref name="MapSet"/>: the player has turned or zoomed the map by hand, so it opens on
/// those, not on the fit of the orbit), whether it is the map that is shown, and what it is
/// centred on. Kept with the vessel, so switching back to it, or carrying on in a saved world,
/// finds the view as it was left.
/// </summary>
internal sealed record CameraView(bool Map, float Yaw, float Pitch, float Distance, float MapYaw, float MapPitch, float MapDistance, bool MapSet, CameraCentre Centre);
