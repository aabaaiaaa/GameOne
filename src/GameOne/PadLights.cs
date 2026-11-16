using System.Numerics;

namespace GameOne;

/// <summary>What a lamp is: set in the concrete round an edge, on a floodlight mast's head, or at a tower's foot shining up it.</summary>
internal enum LampKind { Floor, Flood, Tower }

/// <summary>A lamp, in its place's frame (see <see cref="LitPlace"/>): where it is and which way it shines.</summary>
internal readonly record struct Lamp(Vector3 Position, Vector3 Aim, LampKind Kind);

/// <summary>
/// A light as the shaders take it (<see cref="PadLights.Nearest"/>), in the world: where it is and how
/// far it reaches, its colour times brightness, and its beam: lit fully within <c>CosInner</c> of
/// <c>Aim</c>, fading to nothing at <c>CosOuter</c> (below -1: all round, a lamp with no beam).
/// </summary>
internal readonly record struct PadLight(Vector3 Position, float Range, Vector3 Colour, float CosOuter, Vector3 Aim, float CosInner);

/// <summary>A floodlight mast, in its place's frame: its foot on the ground and how tall it is, standing up the frame's +Y.</summary>
internal readonly record struct Mast(Vector3 Foot, float Height);

/// <summary>
/// A lit place: a launch pad, a landing zone or a drone ship, with its lamps and masts in its own
/// frame (a pad's or zone's, fixed; a ship's, its deck's as the waves move it, about its waterline).
/// </summary>
internal sealed class LitPlace(string name, bool isPad, Vector3 origin, Quaternion frame, Raft? ship, Lamp[] lamps, Mast[] masts)
{
    /// <summary>Its top's centre, in its frame, which its floodlights aim at: a pad's or zone's (<see cref="Scene.PadTop"/> up), or a ship's deck's.</summary>
    public static Vector3 TopOf(Raft? ship) => new(0f, ship is not null ? Scene.DroneShipFreeboard : Scene.PadTop, 0f);

    public string Name { get; } = name;
    public bool IsPad { get; } = isPad;
    public Raft? Ship { get; } = ship;
    public Lamp[] Lamps { get; } = lamps;
    public Mast[] Masts { get; } = masts;

    /// <summary>Its top's centre, in its frame (see <see cref="TopOf"/>).</summary>
    public Vector3 Top { get; } = TopOf(ship);

    /// <summary>Its frame now: fixed, or its ship's as it rides the waves.</summary>
    public Quaternion Frame => Ship?.Current.Rotation ?? frame;

    /// <summary>Its frame's origin now (a pad's or zone's centre on its ground; a ship's at its waterline).</summary>
    public Vector3 Origin => Ship?.Current.Position ?? origin;

    public Vector3 ToWorld(Vector3 local) => Origin + Vector3.Transform(local, Frame);
    public Vector3 ToLocal(Vector3 world) => Vector3.Transform(world - Origin, Quaternion.Conjugate(Frame));
}

/// <summary>
/// Where every lamp and floodlight mast is: at each launch site's pad, at each landing zone (Home's
/// and the other sites'; not the test range's), and on each drone ship. Worked out once, from the
/// sites and the terrain, like <see cref="Foundations"/>: physics makes the masts solid, the
/// renderer draws them and their light. They are part of the world, not a cue: on by each place's
/// own dark (<see cref="PlaceLights"/>), warm white, the same everywhere.
/// </summary>
internal static class PadLights
{
    public static IReadOnlyList<LitPlace> All => Places.Value;

    /// <summary>The most lights the shaders take at once (<c>lightPlaces[]</c> and its kin): two or three places' worth.</summary>
    public const int MaxLights = 32;

    /// <summary>The most floodlights that cast shadows at once: the nearest place's (held a few metres: see Nearest), a mast each (the shadow map's four spot tiles).</summary>
    public const int MaxShadowed = 4;

    /// <summary>
    /// Fills <paramref name="lights"/> with the lights of the places nearest <paramref name="camera"/>
    /// (within <see cref="Scene.LightReach"/>), nearest first, as many as fit: each floodlight (a
    /// spot, a mast's two as one), one upward cone per tower (its three lamps as one), and four short lights along each
    /// place's edge (its floor lights as a wash), each place's as bright as its fraction in
    /// <paramref name="lit"/> (<see cref="PlaceLights.Lit"/>, by <see cref="All"/>'s index) makes
    /// them; a place at 0 is off and left out. Returns how many. Allocates nothing: it is called
    /// every frame.
    /// </summary>
    public static int Nearest(Vector3 camera, ReadOnlySpan<float> lit, Span<PadLight> lights) => Nearest(camera, lit, lights, out _);

    /// <summary>As <see cref="Nearest(Vector3, ReadOnlySpan{float}, Span{PadLight})"/>; <paramref name="shadowed"/> is how many of the first cast shadows: the nearest place's floodlights (a mast each), at most <see cref="MaxShadowed"/>.</summary>
    public static int Nearest(Vector3 camera, ReadOnlySpan<float> lit, Span<PadLight> lights, out int shadowed)
    {
        var none = -1;
        return Nearest(camera, lit, lights, ref none, out shadowed);
    }

    /// <summary>
    /// As <see cref="Nearest(Vector3, ReadOnlySpan{float}, Span{PadLight}, out int)"/>, frame after frame: the place whose
    /// floods cast shadows (<paramref name="shadowPlace"/>, its index in <see cref="All"/>; -1 for
    /// none) stays the one that did last frame, first of the places, until another is nearer than
    /// it by <see cref="Scene.ShadowPlaceHysteresis"/>, so a camera midway between two (Home's
    /// pad and zone) doesn't flip the shadows from one to the other and back. A place that is off is
    /// none of the places, so as the held one goes off the next nearest lit takes the shadows that
    /// frame (or none, -1). Set to this frame's.
    /// </summary>
    public static int Nearest(Vector3 camera, ReadOnlySpan<float> lit, Span<PadLight> lights, ref int shadowPlace, out int shadowed)
    {
        shadowed = 0;
        var places = All;
        Span<(float Distance, int Index)> order = stackalloc (float, int)[places.Count];
        var near = 0;
        for (var i = 0; i < places.Count; i++)
        {
            if (lit[i] <= 0f)
                continue; // off: no light, and no shadows to hold
            var d = Vector3.Distance(camera, places[i].Origin);
            if (d <= Scene.LightReach)
                order[near++] = (d, i);
        }
        order[..near].Sort((a, b) => a.Distance.CompareTo(b.Distance));
        // Last frame's shadowing place, if still near enough, goes first.
        for (var k = 1; k < near; k++)
            if (order[k].Index == shadowPlace && order[k].Distance <= order[0].Distance + Scene.ShadowPlaceHysteresis)
            {
                var held = order[k];
                order[..k].CopyTo(order[1..(k + 1)]);
                order[0] = held;
                break;
            }
        shadowPlace = near > 0 ? order[0].Index : -1;

        var count = 0;
        foreach (var (_, index) in order[..near])
        {
            var place = places[index];
            var frame = place.Frame;
            var colour = Scene.LampColour * lit[index]; // each place by its own dark, faded in and out
            // Each mast's two floodlights as one light at its head (the shaders' cost goes by the lights).
            foreach (var mast in place.Masts)
            {
                if (count == lights.Length) return count;
                if (index == order[0].Index && shadowed < MaxShadowed)
                    shadowed++; // the shadowing place's (the first, nearest or held): its floods are the first lights
                var head = mast.Foot + new Vector3(0f, mast.Height, 0f);
                var aim = Vector3.Normalize(place.Top - head);
                lights[count++] = new PadLight(place.ToWorld(head), Scene.FloodRange, colour * (2f * Scene.FloodBrightness),
                    MathF.Cos(float.DegreesToRadians(Scene.FloodConeDegrees)), Vector3.Transform(aim, frame), MathF.Cos(float.DegreesToRadians(Scene.FloodCoreDegrees)));
            }
            if (place.IsPad)
            {
                if (count == lights.Length) return count;
                var foot = place.ToWorld(Scene.TowerOffset + new Vector3(0f, Scene.PadTop, 0f));
                lights[count++] = new PadLight(foot, Scene.TowerLightRange, colour * Scene.TowerLightBrightness,
                    MathF.Cos(float.DegreesToRadians(40f)), Vector3.Transform(Vector3.UnitY, frame), MathF.Cos(float.DegreesToRadians(25f)));
            }
            var (a, b) = place.Ship is not null ? (Scene.DroneShipHalfLength, Scene.DroneShipHalfBeam)
                : place.IsPad ? (Scene.PadHalfSize, Scene.PadHalfSize) : (Scene.LandingZoneRadius, Scene.LandingZoneRadius);
            var y = place.Ship is not null ? Scene.DroneShipFreeboard + 0.5f : Scene.PadTop + 0.5f;
            foreach (var edge in (ReadOnlySpan<Vector3>)[new(a, y, 0f), new(-a, y, 0f), new(0f, y, b), new(0f, y, -b)])
            {
                if (count == lights.Length) return count;
                lights[count++] = new PadLight(place.ToWorld(edge), Scene.FloorLightRange, colour * Scene.FloorLightBrightness, -2f, Vector3.UnitY, -1f);
            }
        }
        return count;
    }

    /// <summary>The lit place of <paramref name="site"/>'s pad.</summary>
    public static LitPlace Pad(LaunchSite site) => All[Array.IndexOf(LaunchSites.All, site)];

    // The pads first (in LaunchSites.All's order, so Pad can find them), then the zones, then the ships.
    private static readonly Lazy<LitPlace[]> Places = new(() =>
    [
        .. LaunchSites.All.Select(PadPlace),
        .. LaunchSites.All.Where(s => s.HasLandingZone).Select(ZonePlace),
        .. Rafts.Ships.Select(ShipPlace),
    ]);

    private static LitPlace PadPlace(LaunchSite site)
    {
        var half = Scene.PadHalfSize;
        var lamps = new List<Lamp>();
        // Round the square's edge, set in its top.
        var perSide = (int)MathF.Round(2f * half / Scene.FloorLampSpacing);
        for (var side = 0; side < 4; side++)
            for (var i = 0; i < perSide; i++)
            {
                var along = -half + (i + 0.5f) * 2f * half / perSide;
                var p = side switch { 0 => new Vector3(along, 0, -half), 1 => new Vector3(half, 0, along), 2 => new Vector3(-along, 0, half), _ => new Vector3(-half, 0, -along) };
                lamps.Add(new Lamp(p with { Y = Scene.PadTop }, Vector3.UnitY, LampKind.Floor));
            }
        // Three at the tower's foot, shining up it.
        for (var k = 0; k < 3; k++)
        {
            var (s, c) = MathF.SinCos(MathF.Tau * (k + 0.5f) / 3f);
            lamps.Add(new Lamp(Scene.TowerOffset + new Vector3(c, 0f, s) * (Scene.TowerHalfWidth + 1.5f) + new Vector3(0, Scene.PadTop, 0), Vector3.UnitY, LampKind.Tower));
        }
        var place = new LitPlace($"{site.Name}'s pad", true, site.Pad, site.Frame, null, [], []);
        var masts = Masts(place, half * MathF.Sqrt(2f));
        return new LitPlace(place.Name, true, site.Pad, site.Frame, null, [.. lamps, .. Floods(masts, place.Top)], masts);
    }

    private static LitPlace ZonePlace(LaunchSite site)
    {
        var (centre, tilt) = (site.LandingZone!.Value, site.LandingZoneTilt!.Value);
        var r = Scene.LandingZoneRadius;
        var count = (int)MathF.Round(MathF.Tau * r / Scene.FloorLampSpacing);
        var lamps = new List<Lamp>();
        for (var i = 0; i < count; i++)
        {
            var (s, c) = MathF.SinCos(MathF.Tau * i / count);
            lamps.Add(new Lamp(new Vector3(c * r, Scene.PadTop, s * r), Vector3.UnitY, LampKind.Floor));
        }
        var place = new LitPlace($"{site.Name}'s landing zone", false, centre, tilt, null, [], []);
        var masts = Masts(place, r);
        return new LitPlace(place.Name, false, centre, tilt, null, [.. lamps, .. Floods(masts, place.Top)], masts);
    }

    private static LitPlace ShipPlace(Raft ship)
    {
        var (halfLength, halfBeam, deck) = (Scene.DroneShipHalfLength, Scene.DroneShipHalfBeam, Scene.DroneShipFreeboard);
        var lamps = new List<Lamp>();
        // Along its deck's edges.
        for (var x = -halfLength + 1f; x <= halfLength - 1f; x += Scene.FloorLampSpacing)
        {
            lamps.Add(new Lamp(new Vector3(x, deck, -halfBeam + 0.3f), Vector3.UnitY, LampKind.Floor));
            lamps.Add(new Lamp(new Vector3(x, deck, halfBeam - 0.3f), Vector3.UnitY, LampKind.Floor));
        }
        for (var z = -halfBeam + 1f; z <= halfBeam - 1f; z += Scene.FloorLampSpacing)
        {
            lamps.Add(new Lamp(new Vector3(-halfLength + 0.3f, deck, z), Vector3.UnitY, LampKind.Floor));
            lamps.Add(new Lamp(new Vector3(halfLength - 0.3f, deck, z), Vector3.UnitY, LampKind.Floor));
        }
        // Four short masts at its deck's very corners (the deck is its own: nothing else stands there).
        Mast[] masts = [.. new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) }.Select(c =>
            new Mast(new Vector3(c.Item1 * (halfLength - 0.6f), deck, c.Item2 * (halfBeam - 0.6f)), Scene.ShipMastHeight))];
        // Named as its marker is ("Drone ship", "Drone ship 2", "Sentinel drone ship"), so the readout's place is the one a script clicks.
        var name = Sites.All.First(site => site.Centre == ship.Centre).Name;
        return new LitPlace(name, false, ship.Centre, Quaternion.Identity, ship, [.. lamps, .. Floods(masts, LitPlace.TopOf(ship))], masts);
    }

    /// <summary>
    /// Up to four masts round <paramref name="place"/>, on its diagonals, <see cref="Scene.MastOut"/>
    /// beyond <paramref name="reach"/> (its concrete's furthest), each only where the ground there is
    /// land, near the place's level and not steep: a site whose ground falls away has fewer, and is darker.
    /// </summary>
    private static Mast[] Masts(LitPlace place, float reach)
    {
        var level = Planet.Altitude(place.ToWorld(Vector3.Zero));
        var masts = new List<Mast>();
        for (var k = 0; k < 4; k++)
        {
            var (s, c) = MathF.SinCos(MathF.PI / 4f + k * MathF.PI / 2f);
            var d = Planet.Up(place.ToWorld(new Vector3(c, 0f, s) * (reach + Scene.MastOut)));
            if (Terrain.IsSea(d))
                continue;
            var ground = Terrain.Height(d);
            if (MathF.Abs(ground - level) > Scene.MastLevelTolerance)
                continue;
            if (Vector3.Dot(Terrain.Normal(d), d) < MathF.Cos(float.DegreesToRadians(Scene.MastMaxSlopeDegrees)))
                continue;
            masts.Add(new Mast(place.ToLocal(Planet.Centre + d * (Planet.SeaRadius + ground)), Scene.MastHeight));
        }
        return [.. masts];
    }

    /// <summary>Two floodlights on each mast's head, aimed down at the place's <paramref name="top"/>'s centre.</summary>
    private static IEnumerable<Lamp> Floods(Mast[] masts, Vector3 top)
    {
        foreach (var mast in masts)
        {
            var head = mast.Foot + new Vector3(0f, mast.Height, 0f);
            var aim = Vector3.Normalize(top - head);
            var across = Vector3.Normalize(Vector3.Cross(aim, Vector3.UnitY)) * 0.4f;
            yield return new Lamp(head + across, aim, LampKind.Flood);
            yield return new Lamp(head - across, aim, LampKind.Flood);
        }
    }
}

/// <summary>
/// A vessel's landing lights this frame (<see cref="Vessel.LandingLights"/>): where they are (its
/// bell's bottom), which way they shine (down its axis, <c>-Up</c>), how far on (0 to 1), and whose
/// they are (its <see cref="Vessel.Id"/>: what its far glow twinkles by, which stays the same as it moves).
/// </summary>
internal readonly record struct VesselLight(Vector3 At, Vector3 Down, float Lit, int Id = 0);

/// <summary>
/// The lights one or more views are lit by this frame (<see cref="LightSets"/>): the lit place
/// nearest them whose floods cast shadows (<see cref="Place"/>, its index in
/// <see cref="PadLights.All"/>; -1 for none near), that place's and its neighbours' lights
/// (<see cref="PadLights.Nearest(Vector3, ReadOnlySpan{float}, Span{PadLight}, ref int, out int)"/>), then the
/// nearest lit vessels' landing lights (<see cref="LightSets.Choose"/>), how many of the first cast
/// shadows, and which block of the shadow map's floodlight tiles holds their shadows (-1 for none:
/// a set with no floods costs no shadows).
/// </summary>
internal readonly record struct LightSet(int Place, ReadOnlyMemory<PadLight> Lights, int Shadowed, int Block);

/// <summary>
/// Which lit places' lights are on: each place's own dark (<see cref="Sun.IsDark"/> at its origin, by
/// its own time of day, held between dusk and dawn) and how far on its lights are, faded over
/// <see cref="Scene.PadLightFadeSeconds"/> of real time. Indexed as <see cref="PadLights.All"/>.
/// Made once; stepping allocates nothing.
/// </summary>
internal sealed class PlaceLights
{
    private readonly float[] _lit = new float[PadLights.All.Count];
    private readonly bool[] _dark = new bool[PadLights.All.Count];

    /// <summary>Each place's lights as a fraction, 0 (off) to 1 (fully on).</summary>
    public ReadOnlySpan<float> Lit => _lit;

    /// <summary>Whether the place at <paramref name="place"/> (its index in <see cref="PadLights.All"/>) is held dark.</summary>
    public bool IsDark(int place) => _dark[place];

    /// <summary>
    /// Every place straight to where its dark puts its lights, with no fade (a world just loaded or
    /// restarted). Between dusk's and dawn's heights a place whose sun is rising is still dark (the
    /// night's lights not yet off) and one whose sun is setting is not (the day's not yet over), as
    /// continuous play would have it.
    /// </summary>
    public void Reset(double worldTime)
    {
        var places = PadLights.All;
        for (var i = 0; i < places.Count; i++)
        {
            var rising = Sun.Height(GroundBody.Planet, places[i].Origin, worldTime + 1.0) > Sun.Height(GroundBody.Planet, places[i].Origin, worldTime);
            _dark[i] = Sun.IsDark(GroundBody.Planet, places[i].Origin, worldTime, wasDark: rising);
            _lit[i] = _dark[i] ? 1f : 0f;
        }
    }

    /// <summary>Each place's dark at <paramref name="worldTime"/>, then its lights a step of <paramref name="realSeconds"/> nearer on (dark) or off.</summary>
    public void Step(double worldTime, float realSeconds)
    {
        var places = PadLights.All;
        var step = realSeconds / Scene.PadLightFadeSeconds;
        for (var i = 0; i < places.Count; i++) // by index: a foreach over the list would allocate
        {
            _dark[i] = Sun.IsDark(GroundBody.Planet, places[i].Origin, worldTime, _dark[i]);
            _lit[i] = _dark[i] ? MathF.Min(1f, _lit[i] + step) : MathF.Max(0f, _lit[i] - step);
        }
    }
}

/// <summary>
/// Each frame's light sets: one per distinct lit place the views are at, so views at the same place
/// share one set and one block of floodlight shadows, and only places apart cost more (a view near
/// no place has a set of its own, of the vessels near it, if any, and no shadows). Chosen for
/// the main view first (its set and block are the first), then for each small view. Everything is
/// sized for the main view and <see cref="Scene.MaxSmallViews"/> small ones up front, so choosing
/// allocates nothing.
/// </summary>
internal sealed class LightSets
{
    /// <summary>The most sets in a frame: one per view, if each is at a place of its own.</summary>
    public const int Max = 1 + Scene.MaxSmallViews;

    private readonly PadLight[] _lights = new PadLight[Max * PadLights.MaxLights]; // each set's, MaxLights apart
    private readonly LightSet[] _sets = new LightSet[Max];
    private int _count;

    /// <summary>This frame's sets, the main view's first.</summary>
    public ReadOnlySpan<LightSet> Sets => _sets.AsSpan(0, _count);

    /// <summary>How many of the shadow map's floodlight blocks this frame's sets use.</summary>
    public int Blocks { get; private set; }

    /// <summary>Starts a frame's choice afresh.</summary>
    public void Clear() => (_count, Blocks) = (0, 0);

    /// <summary>
    /// The set lighting a view at <paramref name="position"/> (the main view's camera, or a small
    /// view's vessel), its index in <see cref="Sets"/>: a new one unless a view already chosen this
    /// frame is at the same place, each place as far on as <paramref name="lit"/> (this frame's
    /// <see cref="PlaceLights.Lit"/>) has it. <paramref name="shadowPlace"/> is the view's own, held
    /// from frame to frame as
    /// <see cref="PadLights.Nearest(Vector3, ReadOnlySpan{float}, Span{PadLight}, ref int, out int)"/> holds it,
    /// so each view keeps its place across the middle between two.
    /// After the places' lights (at most <see cref="PadLights.MaxLights"/> less
    /// <see cref="Scene.LandingLightsLit"/>, so the vessels always have room) come the landing
    /// lights of the nearest <see cref="Scene.LandingLightsLit"/> of <paramref name="vessels"/>
    /// within <see cref="Scene.LandingLightReach"/> of <paramref name="position"/> (nearest first;
    /// one at 0 is off and left out), each a cone down its axis, unshadowed.
    /// </summary>
    public int Choose(Vector3 position, ReadOnlySpan<float> lit, ReadOnlySpan<VesselLight> vessels, ref int shadowPlace)
    {
        if (_count == Max)
            throw new InvalidOperationException($"more than {Max} views' lights chosen in a frame (LightSets.Max)");
        // Into the next set's room; left there, unused, if an earlier set is at the same place.
        var start = _count * PadLights.MaxLights;
        var count = PadLights.Nearest(position, lit, _lights.AsSpan(start, PadLights.MaxLights - Scene.LandingLightsLit), ref shadowPlace, out var shadowed);
        // A view at the same place as one chosen before it shares that view's set: the lights as
        // gathered from the first view's position, not its own, the vessels' as its neighbouring
        // places' are. That is meant (views at one place share one set and one block of floodlight
        // shadows, so only places apart cost more). The place itself, its floods first, is the same
        // either way; only which neighbours and vessels fill the set's last room (nearest the first
        // view) can differ. A view near no place (-1) never shares: what lights it is only the
        // vessels near it, which another view far off has none of.
        if (shadowPlace >= 0)
            for (var i = 0; i < _count; i++)
                if (_sets[i].Place == shadowPlace)
                    return i;
        count += NearestVessels(position, vessels, _lights.AsSpan(start + count, Scene.LandingLightsLit));
        _sets[_count] = new LightSet(shadowPlace, _lights.AsMemory(start, count), shadowed, shadowed > 0 ? Blocks++ : -1);
        return _count++;
    }

    private static readonly float CosLandingCone = MathF.Cos(float.DegreesToRadians(Scene.LandingLightConeDegrees));
    private static readonly float CosLandingCore = MathF.Cos(float.DegreesToRadians(Scene.LandingLightCoreDegrees));

    /// <summary>
    /// Fills <paramref name="lights"/> with the landing lights of the nearest of <paramref name="vessels"/>
    /// to <paramref name="position"/> that are on and within <see cref="Scene.LandingLightReach"/>,
    /// nearest first, as many as fit; returns how many. One pass keeping the nearest few in order
    /// (no sort of them all: there can be a thousand), allocating nothing.
    /// </summary>
    private static int NearestVessels(Vector3 position, ReadOnlySpan<VesselLight> vessels, Span<PadLight> lights)
    {
        if (lights.IsEmpty)
            return 0;
        Span<(float Distance, int Index)> nearest = stackalloc (float, int)[lights.Length];
        var found = 0;
        for (var i = 0; i < vessels.Length; i++)
        {
            if (vessels[i].Lit <= 0f)
                continue;
            var d = Vector3.DistanceSquared(position, vessels[i].At);
            if (d > Scene.LandingLightReach * Scene.LandingLightReach || (found == nearest.Length && d >= nearest[found - 1].Distance))
                continue;
            // Into its place among those kept, the furthest dropped once they are full.
            var k = found < nearest.Length ? found++ : found - 1;
            for (; k > 0 && nearest[k - 1].Distance > d; k--)
                nearest[k] = nearest[k - 1];
            nearest[k] = (d, i);
        }
        for (var k = 0; k < found; k++)
        {
            var vessel = vessels[nearest[k].Index];
            lights[k] = new PadLight(vessel.At, Scene.LandingLightRange, Scene.LandingLightColour * (Scene.LandingLightBrightness * vessel.Lit),
                CosLandingCone, vessel.Down, CosLandingCore);
        }
        return found;
    }
}
