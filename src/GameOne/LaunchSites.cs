using System.Numerics;

namespace GameOne;

/// <summary>
/// A place to launch from: a launch pad, levelled into the ground, with a service tower beside it
/// in a colour of its own; and, at some, a landing zone nearby or drone ships out at sea. Each is
/// laid out as Home's is, in the pad's own frame (see <see cref="Frame"/>), so what is Home's
/// (the tower's place, the rocket's on the pad, the zone's and the ships') is found at any site the
/// same way round. A class, not a record: a site is one fixed place (equal only to itself), and
/// what is worked out from where it is (its frame, its height on the map) is kept, not copied.
/// </summary>
internal sealed class LaunchSite
{
    /// <param name="latitude">Degrees, north positive; Home is at 0, 0 (as <c>tools/TerrainGen/layout.json</c> gives places).</param>
    /// <param name="longitude">Degrees, east positive.</param>
    /// <param name="towerColour">The tower's girders' colour (display values).</param>
    /// <param name="droneShips">
    /// Its drone ships' centres, any number, each given as Home's is (a place round Home's pad, out
    /// at sea, from <see cref="Scene"/>), in order: the first is the missions' downrange.
    /// </param>
    public LaunchSite(string name, float latitude, float longitude, Vector3 towerColour, bool hasLandingZone, IReadOnlyList<Vector3> droneShips)
    {
        (Name, Latitude, Longitude, TowerColour, HasLandingZone) = (name, latitude, longitude, towerColour, hasLandingZone);
        Up = CubeSphere.FromLatLon(float.DegreesToRadians(latitude), float.DegreesToRadians(longitude));
        // Its frame needs only its direction (no map: TerrainGen uses it, to make the map).
        var onTheSphere = Planet.Centre + Up * Planet.Radius;
        var (east, north) = (Planet.East(onTheSphere), Planet.North(onTheSphere));
        var m = new Matrix4x4(east.X, east.Y, east.Z, 0f, Up.X, Up.Y, Up.Z, 0f, -north.X, -north.Y, -north.Z, 0f, 0f, 0f, 0f, 1f);
        Frame = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        DroneShips = [.. droneShips.Select(ship => AsHomes(ship, Planet.SeaRadius))];
        _elevation = new(() => IsHome ? Scene.HomeElevation : Highest(Up, Scene.PadHalfSize, (x, z) => MathF.Max(MathF.Abs(x), MathF.Abs(z)) <= Scene.PadHalfSize));
        _zoneElevation = new(() => LandingZoneDirection is not { } d ? Elevation
            : IsHome ? Planet.Altitude(Scene.LandingZoneCentre)
            : Highest(d, Scene.LandingZoneRadius, (x, z) => x * x + z * z <= Scene.LandingZoneRadius * Scene.LandingZoneRadius));
    }

    public string Name { get; }
    public float Latitude { get; }
    public float Longitude { get; }
    public Vector3 TowerColour { get; }
    public bool HasLandingZone { get; }

    public override string ToString() => Name;

    /// <summary>Straight up from the planet's centre through the pad.</summary>
    public Vector3 Up { get; }

    /// <summary>
    /// The height of the ground the pad stands on, above the sea: Home's is <see cref="Scene.HomeElevation"/>;
    /// another's is the highest ground under its square (the generator levels the ground there down
    /// to it, never up, so the pad's foundation lifts it clear wherever the ground falls away).
    /// </summary>
    public float Elevation => _elevation.Value;
    private readonly Lazy<float> _elevation;

    /// <summary>Its landing zone's ground, likewise: the highest under its disc, its own height, not the pad's (Home's is its zone's, at the pad's level).</summary>
    public float ZoneElevation => _zoneElevation.Value;
    private readonly Lazy<float> _zoneElevation;

    /// <summary>
    /// The highest ground, as the game reads it, at the points of a 1 m grid round
    /// <paramref name="centre"/> (a direction), out to <paramref name="reach"/> either way (the
    /// footprint's half size or radius), that are <paramref name="inside"/> (metres east and south of it).
    /// </summary>
    private float Highest(Vector3 centre, float reach, Func<float, float, bool> inside)
    {
        var onTheSphere = Planet.Centre + centre * Planet.Radius;
        var (east, south) = (Planet.East(onTheSphere), -Planet.North(onTheSphere));
        var highest = float.MinValue;
        var steps = (int)MathF.Ceiling(reach);
        for (var x = (float)-steps; x <= steps; x += 1f)
        for (var z = (float)-steps; z <= steps; z += 1f)
            if (inside(x, z))
                highest = MathF.Max(highest, Terrain.Height(Vector3.Normalize(centre * Planet.Radius + east * x + south * z)));
        return highest;
    }

    /// <summary>The centre of the ground under the pad (Home's is the world's origin).</summary>
    public Vector3 Pad => Planet.Centre + Up * (Planet.SeaRadius + Elevation);

    /// <summary>
    /// The pad's frame: its +X east, +Y up and -Z north, as the world's axes are at Home, where it
    /// is no turn at all. (Not <see cref="Planet.UprightAt"/>, whose shortest turn from +Y leaves its
    /// +X pointing some other way than east, away from Home.)
    /// </summary>
    public Quaternion Frame { get; }

    /// <summary>The pad's frame and place, as a model matrix (row vectors, as everywhere).</summary>
    public Matrix4x4 Model => Matrix4x4.CreateFromQuaternion(Frame) * Matrix4x4.CreateTranslation(Pad);

    /// <summary>A point given in the pad's frame (as Home's are given in the world's), in the world.</summary>
    public Vector3 ToWorld(Vector3 local) => Pad + Vector3.Transform(local, Frame);

    /// <summary>A point in the world, in the pad's frame.</summary>
    public Vector3 ToLocal(Vector3 world) => Vector3.Transform(world - Pad, Quaternion.Conjugate(Frame));

    /// <summary>Where a fresh rocket's body base starts: standing on its engine bell on the pad.</summary>
    public Vector3 RocketStart => ToWorld(Scene.RocketStart);

    /// <summary>The foot of the service tower's centre.</summary>
    public Vector3 TowerFoot => ToWorld(Scene.TowerOffset);

    /// <summary>
    /// Its landing zone's centre, if it has one: where Home's is from its pad, the same way round,
    /// on its own ground (<see cref="ZoneElevation"/>).
    /// </summary>
    public Vector3? LandingZone => LandingZoneDirection is { } d ? (IsHome ? Scene.LandingZoneCentre : Planet.Centre + d * (Planet.SeaRadius + ZoneElevation)) : null;

    /// <summary>The direction of its landing zone's centre from the planet's centre, if it has one (no map needed).</summary>
    public Vector3? LandingZoneDirection => HasLandingZone ? Planet.Up(AsHomes(Scene.LandingZoneCentre, Planet.Radius)) : null;

    /// <summary>How its landing zone is turned to stand level on the planet's curve, if it has one.</summary>
    public Quaternion? LandingZoneTilt => LandingZone is not { } zone ? null
        : IsHome ? Planet.UprightAt(zone)
        // The pad's own frame, carried over the curve to the zone (turned about the line square to
        // both ups): level there, and square to the pad, as Home's is (UprightAt alone turns it).
        : Quaternion.Concatenate(Frame, Carry(Up, Planet.Up(zone)));

    /// <summary>The turn that carries <paramref name="from"/> to <paramref name="to"/> (two ups), the shortest way.</summary>
    private static Quaternion Carry(Vector3 from, Vector3 to)
    {
        var axis = Vector3.Cross(from, to);
        var sin = axis.Length();
        return sin < 1e-7f ? Quaternion.Identity
            : Quaternion.CreateFromAxisAngle(axis / sin, MathF.Atan2(sin, Vector3.Dot(from, to)));
    }

    /// <summary>
    /// Its drone ships' centres (none, one or more), each out at sea where the one it was given as
    /// is from Home's pad, at its waterline on a calm sea.
    /// </summary>
    public IReadOnlyList<Vector3> DroneShips { get; }

    /// <summary>
    /// Where <paramref name="home"/> (a place round Home's pad) is round this one: turned about the
    /// planet's centre as Home's frame is to this one's, at <paramref name="radius"/> from the centre.
    /// At Home itself, it is <paramref name="home"/> exactly: what the missions and markers compare
    /// their landing sites with must be the very same point, not one turned by no turn at all.
    /// </summary>
    private Vector3 AsHomes(Vector3 home, float radius) => IsHome
        ? home
        : Planet.Centre + Vector3.Normalize(Vector3.Transform(home - Planet.Centre, Frame)) * radius;

    private bool IsHome => Latitude == 0f && Longitude == 0f;
}

/// <summary>The launch sites, round the planet. Home first: what the missions there are now are flown from.</summary>
internal static class LaunchSites
{
    /// <remarks>
    /// Placed from a survey of the map (each one's comment says where).
    /// Each is levelled into the
    /// map at its own height by <c>tools/TerrainGen</c>, which reads them from here: after changing
    /// one, regenerate the map. New ones go on the end (the list is by name elsewhere, but its order
    /// is the menu's).
    /// </remarks>
    public static readonly LaunchSite[] All =
    [
        new("Home", 0f, 0f, new Vector3(0.62f, 0.16f, 0.10f), hasLandingZone: true, droneShips: [Scene.DroneShipCentre, Scene.TripDroneShipCentre]),
        new("Polar", -70f, 3.5f, new Vector3(0.85f, 0.86f, 0.88f), hasLandingZone: true, droneShips: []),     // Southreach, near the south pole
        new("Highcrest", 11f, 75f, new Vector3(0.85f, 0.68f, 0.12f), hasLandingZone: true, droneShips: []),   // up in Eastland's range
        new("Desert", 19f, -29f, new Vector3(0.82f, 0.42f, 0.16f), hasLandingZone: true, droneShips: []),     // Westland's interior
        new("Coral Isle", -15f, 168f, new Vector3(0.12f, 0.62f, 0.56f), hasLandingZone: false, droneShips: []), // an island on the far side
        new("Northreach", 65f, -170f, new Vector3(0.20f, 0.40f, 0.82f), hasLandingZone: false, droneShips: []), // its cold north coast
        new("Sentinel", 20f, -131f, new Vector3(0.20f, 0.58f, 0.26f), hasLandingZone: false, droneShips: [Scene.DroneShipCentre]), // in the Western Ocean
    ];

    /// <summary>The site at the world's origin, by the coast of Westland.</summary>
    public static LaunchSite Home => All[0];

    /// <summary>
    /// Where smoke lies, and where the engine can stir up a cloud: every site's pad, and its landing
    /// zone if it has one, in site order (so Home's pad and zone first). Not the drone ships: the
    /// smoke would lie on the sea round them.
    /// </summary>
    public static Vector3[] SmokeSites => _smokeSites.Value;

    /// <summary>The site called <paramref name="name"/> (as the player's settings keep their pick), or Home if there is none by that name.</summary>
    public static LaunchSite Named(string? name)
    {
        foreach (var site in All) // a loop, not a search with a lambda: it is asked every frame, and that would allocate
            if (site.Name == name)
                return site;
        return Home;
    }

    /// <summary>The site whose pad is nearest <paramref name="p"/>.</summary>
    public static LaunchSite Nearest(Vector3 p)
    {
        var (best, closest) = (All[0], float.MaxValue);
        foreach (var site in All)
            if (Vector3.DistanceSquared(site.Pad, p) is var d && d < closest)
                (best, closest) = (site, d);
        return best;
    }

    private static readonly Lazy<Vector3[]> _smokeSites = new(() =>
        [.. All.SelectMany(site => site.LandingZone is { } zone ? new[] { site.Pad, zone } : [site.Pad])]);
}
