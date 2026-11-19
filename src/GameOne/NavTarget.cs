using System.Numerics;

namespace GameOne;

/// <summary>What kind of thing a <see cref="NavTarget"/> is.</summary>
internal enum TargetKind
{
    /// <summary>A vessel in the world, by its <see cref="Vessel.Id"/>.</summary>
    Vessel,
    /// <summary>A place on the ground, by its number in <see cref="Sites"/>.</summary>
    Site,
    /// <summary>
    /// A body (a planet or a moon), by its <see cref="GroundBody.Index"/> in <see cref="Bodies.All"/>:
    /// any but the one the vessel is at (one that comes to be at it, crossing into its sphere of
    /// influence, has it as a target no more).
    /// </summary>
    Body,
    /// <summary>
    /// One of the flight's mission's points of interest (<see cref="Mission.PointsOfInterest"/>), by
    /// its index there: a place on a body's ground that is built nowhere, a target as a site is.
    /// </summary>
    Point,
}

/// <summary>
/// What a vessel's pilot has picked to fly to (by double clicking its marker): another vessel, a place
/// on the ground (a site, or a mission's point of interest), or a body. Each vessel keeps its own, saved with the world. It names the thing
/// rather than holding it, so it is saved as it is, and one that has gone (destroyed, or removed to
/// make room) is simply found to be missing: vessels' numbers are never used again in a world.
/// </summary>
internal readonly record struct NavTarget(TargetKind Kind, int Id)
{
    public static NavTarget OfVessel(Vessel vessel) => new(TargetKind.Vessel, vessel.Id);

    public static NavTarget OfBody(GroundBody body) => new(TargetKind.Body, body.Index);

    public static NavTarget OfPoint(int index) => new(TargetKind.Point, index);
}

/// <summary>
/// Where a target is (<see cref="Flight.Locate"/>): its place and velocity over the ground in the
/// flown vessel's body's frame, whether it is flying (on a path of its own) rather than staying
/// put, the body it is at (a body's, itself), and its place and velocity in that body's own frame,
/// as it was found there (so nothing that works there takes it across and back).
/// </summary>
internal readonly record struct TargetPlace(Vector3 Position, Vector3 Velocity, bool Flying, GroundBody Body, Vector3 Own, Vector3 OwnVelocity);

/// <summary>
/// The places there are markers for: the launch pad, the landing zone, the drone ship, the test
/// pads, the other launch sites' pads, landing zones and first drone ships, and every site's
/// further drone ships. (Saved targets name a site by its number here, so new ones go on the end.)
/// </summary>
internal static class Sites
{
    /// <summary>The body every site is on (<see cref="GroundBody.HasSites"/>), in whose frame each is given.</summary>
    public static GroundBody Body => GroundBody.Planet;

    /// <summary>
    /// Each site: its name, its centre (a test pad's is its base's, under its tilted top; a drone
    /// ship's its waterline's on a calm sea), its top's centre, and its size. A drone ship's top is
    /// its deck's on a calm sea: the waves only move it a little from there.
    /// </summary>
    public static readonly (string Name, Vector3 Centre, Vector3 Top, float Radius)[] All =
    [
        ("Launch pad", LaunchSites.Home.Pad, LaunchSites.Home.ToWorld(new Vector3(0f, Scene.PadTop, 0f)), Scene.PadHalfSize),
        ("Landing zone", Scene.LandingZoneCentre, Level(Scene.LandingZoneCentre, Scene.PadTop), Scene.LandingZoneRadius),
        ("Drone ship", Scene.DroneShipCentre, Level(Scene.DroneShipCentre, Scene.DroneShipFreeboard), Scene.DroneShipHalfLength),
        .. Scene.TestPads.Select(pad => (pad.Name, pad.Centre, pad.Centre + Vector3.Transform(Vector3.UnitY, pad.Tilt) * Scene.PadTop, Scene.LandingZoneRadius)),
        // The other launch sites' pads, their landing zones and their first drone ships (Home's pad, zone and first ship are the first three).
        .. LaunchSites.All.Skip(1).SelectMany(SiteMarkers),
        // Every site's further drone ships, site by site, on the end: a ship added to a site whose
        // further ships are the last goes on after them (one added to an earlier site moves the
        // numbers of those after it, and a target saved on one of them then names another).
        .. LaunchSites.All.SelectMany(site => site.DroneShips.Skip(1).Select((ship, i) =>
            Ship(site == LaunchSites.Home ? $"Drone ship {i + 2}" : $"{site.Name} drone ship {i + 2}", ship))),
    ];

    private static Vector3 Level(Vector3 centre, float height) => centre + Planet.Up(centre) * height;

    private static (string, Vector3, Vector3, float) Ship(string name, Vector3 centre) =>
        (name, centre, Level(centre, Scene.DroneShipFreeboard), Scene.DroneShipHalfLength);

    private static IEnumerable<(string, Vector3, Vector3, float)> SiteMarkers(LaunchSite site)
    {
        yield return ($"{site.Name} pad", site.Pad, site.ToWorld(new Vector3(0f, Scene.PadTop, 0f)), Scene.PadHalfSize);
        if (site.LandingZone is { } zone)
            yield return ($"{site.Name} landing zone", zone, Level(zone, Scene.PadTop), Scene.LandingZoneRadius);
        if (site.DroneShips.Count > 0)
            yield return Ship($"{site.Name} drone ship", site.DroneShips[0]);
    }

    /// <summary>The top of site <paramref name="index"/>, where a marker for it goes.</summary>
    public static Vector3 Top(int index) => All[index].Top;

    // Which sites are drone ships, and which: the ship whose waterline's centre is the site's.
    private static readonly Raft?[] _rafts =
        [.. All.Select(site => Rafts.Ships.FirstOrDefault(ship => Vector3.Distance(ship.Centre, site.Centre) < 1f))];

    /// <summary>The drone ship site <paramref name="index"/> is, if it is one: it rides the waves, where every other site stays put.</summary>
    public static Raft? RaftOf(int index) => _rafts[index];

    /// <summary>
    /// The name of the site whose centre is <paramref name="centre"/> (as <see cref="Mission.LandingSites"/>
    /// holds them); for anywhere else, just "Landing site".
    /// </summary>
    public static string NameOf(Vector3 centre) => All.FirstOrDefault(site => site.Centre == centre).Name ?? "Landing site";
}
