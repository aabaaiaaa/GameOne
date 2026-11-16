using System.Numerics;

namespace GameOne;

/// <summary>
/// How deep the launch pads and landing zones stand. Home's pad and zone and the test range's pads,
/// on their levelled ground, are concrete reaching down from the top to below the lowest ground (or
/// seabed) under their footprint, so none floats or leaves a gap at its side. The far pads and zones,
/// often on a slope, stand on a <see cref="Footing"/>: a slab, then narrowing inward at 45° down
/// into the ground, so the downhill side is never a tall sheer wall (<see cref="PadFootings"/>,
/// <see cref="Plinth.Footing"/>). Physics, drawing and <see cref="Planet.SurfaceAltitude"/> all
/// take their depths from here, and the zones' shapes from <see cref="Plinth"/>.
/// </summary>
/// <remarks>
/// Depths are measured straight down (along <see cref="Planet.Up"/> at the pad's centre) from
/// <see cref="Scene.PadTop"/> above its centre, which for a level pad is its top, and worked out
/// once, the first time they are needed: they depend only on the map and the scene.
/// </remarks>
internal static class Foundations
{
    private const float Spacing = 1f; // metres between the ground samples under a footprint

    /// <summary>Each launch site's pad's footing (<see cref="LaunchSites.All"/>, in order): none for Home's, the first, which is one block.</summary>
    private static readonly Lazy<Footing?[]> LaunchPadFootings = new(() =>
        [.. LaunchSites.All.Select((site, s) => s == 0 ? null
            : Footing.Measure(FootingOutline.Square, Scene.PadHalfSize, site.Pad, (x, z) => site.ToWorld(new Vector3(x, 0f, z))))]);

    private static readonly Lazy<float[]> LaunchPadDepths = new(() =>
        [.. LaunchSites.All.Select((site, s) => LaunchPadFootings.Value[s]?.Depth
            ?? Depth(site.Pad, Samples(FootingOutline.Square, Scene.PadHalfSize).Select(p => site.ToWorld(new Vector3(p.X, 0f, p.Y)))))]);

    /// <summary>The far sites' landing zones (<see cref="LaunchSites.All"/> but Home, those that have one), each level on the curve where it is.</summary>
    private static IEnumerable<(Vector3 Centre, Quaternion Tilt)> FarZones =>
        LaunchSites.All.Skip(1).Where(s => s.HasLandingZone).Select(s => (s.LandingZone!.Value, s.LandingZoneTilt!.Value));

    /// <summary>
    /// Every landing zone: <see cref="Scene.LandingZones"/> (Home's, then the test pads, in that
    /// order: tests count on it), each on a drum or its tilted plinth down to below the lowest ground
    /// under its top; then the far sites' (<see cref="FarZones"/>), each on its footing.
    /// </summary>
    private static readonly Lazy<Plinth[]> LandingZonePlinths = new(() =>
        [.. Scene.LandingZones.Select(zone => new Plinth(zone.Centre, zone.Tilt, DrumDepth(zone))),
         .. FarZones.Select(zone => new Plinth(zone.Centre, zone.Tilt,
             Footing.Measure(FootingOutline.Circle, Scene.LandingZoneRadius, zone.Centre, (x, z) => OnTop(zone, x, z))))]);

    private static Vector3 OnTop((Vector3 Centre, Quaternion Tilt) zone, float x, float z) =>
        zone.Centre + Vector3.Transform(new Vector3(x, Scene.PadTop, z), zone.Tilt);

    // A drum's or tilted plinth's footprint is its top's shadow, so the points across the top and round its rim.
    private static float DrumDepth((Vector3 Centre, Quaternion Tilt) zone) =>
        Depth(zone.Centre, Samples(FootingOutline.Circle, Scene.LandingZoneRadius).Select(p => OnTop(zone, p.X, p.Y)));

    /// <summary>Metres below Home's launch pad's top that its foundation reaches.</summary>
    public static float LaunchPad => LaunchPadDepths.Value[0];

    /// <summary>Metres below each launch site's pad's top that its foundation reaches, at its deepest (<see cref="LaunchSites.All"/>, in order).</summary>
    public static IReadOnlyList<float> LaunchPads => LaunchPadDepths.Value;

    /// <summary>
    /// Each launch site's pad's footing, in the site's frame (<see cref="LaunchSites.All"/>, in
    /// order): null for Home's, one block <see cref="LaunchPad"/> deep.
    /// </summary>
    public static IReadOnlyList<Footing?> PadFootings => LaunchPadFootings.Value;

    /// <summary>Metres below <see cref="Scene.PadTop"/> above each landing zone's centre (see <see cref="LandingZonePlinth"/>, in order) that its foundation reaches, at its deepest.</summary>
    public static IReadOnlyList<float> LandingZones => LandingZoneDepths.Value;

    private static readonly Lazy<float[]> LandingZoneDepths = new(() => [.. LandingZonePlinths.Value.Select(p => p.Depth)]);

    /// <summary>Each landing zone's shape, top and plinth or footing (see <see cref="LandingZonePlinths"/>, in order).</summary>
    public static IReadOnlyList<Plinth> LandingZonePlinth => LandingZonePlinths.Value;

    /// <summary>
    /// The depth of a pad standing at <paramref name="centre"/>: from <see cref="Scene.PadTop"/>
    /// above it down to the lowest ground straight under any of <paramref name="footprint"/>, plus
    /// <see cref="Scene.FoundationMargin"/>.
    /// </summary>
    private static float Depth(Vector3 centre, IEnumerable<Vector3> footprint)
    {
        var up = Planet.Up(centre);
        var lowest = Scene.PadTop; // the top of the base, if the ground is never lower
        foreach (var point in footprint)
            lowest = MathF.Min(lowest, GroundUnder(centre, up, point));
        return Scene.PadTop - lowest + Scene.FoundationMargin;
    }

    /// <summary>
    /// How high the ground straight under <paramref name="point"/> (along <paramref name="up"/>, the
    /// way up at <paramref name="centre"/>) is over <paramref name="centre"/>: the curve bends it up
    /// from a flat top.
    /// </summary>
    internal static float GroundUnder(Vector3 centre, Vector3 up, Vector3 point)
    {
        var under = point - up * Vector3.Dot(point - centre, up);
        var direction = Planet.Up(under);
        var ground = Planet.Centre + direction * (Planet.SeaRadius + Terrain.Height(direction));
        return Vector3.Dot(ground - centre, up);
    }

    /// <summary>The points across a top of <paramref name="outline"/>, <see cref="Spacing"/> apart, and round its edge, in its frame (x, z): a pad's, a zone's or a footing's footprint.</summary>
    internal static IEnumerable<Vector2> Samples(FootingOutline outline, float half)
    {
        var steps = (int)MathF.Ceiling(half / Spacing);
        for (var i = -steps; i <= steps; i++)
        for (var j = -steps; j <= steps; j++)
        {
            var p = new Vector2(i, j) * Spacing;
            if (Footing.Across(outline, p.X, p.Y) <= half)
                yield return p;
        }
        // Its edge (which the grid only touches here and there).
        var round = (int)MathF.Ceiling(10f * half / Spacing); // a zone's rim every 2°
        for (var k = 0; k < round; k++)
        {
            var (s, c) = MathF.SinCos(MathF.Tau * k / round);
            yield return new Vector2(c, s) * (half / Footing.Across(outline, c, s));
        }
    }
}

/// <summary>Which shape a <see cref="Footing"/>'s top is: a pad's square or a zone's circle.</summary>
internal enum FootingOutline { Square, Circle }

/// <summary>
/// A far pad's or far zone's foundation, in its own frame (y up its sides, the top
/// <see cref="Scene.PadTop"/> up): under the top, a straight-sided slab <see cref="Slab"/> thick;
/// below it, narrowing inward at 45° all round (each metre down, one metre in), never past the
/// slab's edges: an upside-down pyramid frustum under a square pad, a cone frustum under a round
/// zone. It runs down until it is in the ground all across (and <see cref="Scene.FoundationMargin"/>
/// more), or to its point under a sheer cliff, so on the downhill side its slope goes into the hill
/// and on flat ground the slab alone reaches into it. The collider is <see cref="Boxes"/>, stepping
/// in to follow it (Jolt here takes only boxes and cylinders); <see cref="Planet.SurfaceAltitude"/>
/// reads <see cref="UnderAt"/>, the smooth shape, within a step of them.
/// </summary>
internal sealed class Footing
{
    private Footing(FootingOutline outline, float half, float depth)
    {
        (Outline, Half, Depth) = (outline, half, depth);
        var boxes = new List<Column> { new(0f, Slab, half) };
        var steps = (int)MathF.Ceiling((depth - Slab) / Scene.FoundationStep - 1e-4f);
        if (steps > 1 && depth - Slab - (steps - 1) * Scene.FoundationStep < Scene.FoundationShortestStep)
            steps--; // the last step's sliver goes into the one above it
        for (var i = 0; i < steps; i++)
        {
            var top = Slab + i * Scene.FoundationStep;
            var bottom = i == steps - 1 ? depth : top + Scene.FoundationStep;
            // As wide as the taper halfway down it: half a step out past it at its bottom, half in at its top.
            boxes.Add(new Column(top, bottom, HalfWidthAt((top + bottom) / 2f)));
        }
        Boxes = boxes;
    }

    /// <summary>
    /// The footing under a top of <paramref name="outline"/>, <paramref name="half"/> across from
    /// its middle each way (a square's half-size, a circle's radius), standing at
    /// <paramref name="centre"/>, its frame's point (x, z) being <paramref name="place"/>(x, z) in
    /// the world (at any height: it is the ground straight under it that counts).
    /// </summary>
    /// <remarks>
    /// At each point of the top, <c>a</c> in from the middle, the taper reaches down to
    /// <c>Slab + half - a</c>; the taper's level at depth <c>d</c> is in the ground once every point
    /// it reaches there has its ground at least that high, so each point asks for <c>d</c> to be at
    /// least the lesser of the two, and the deepest it asks is where the taper is all in the ground.
    /// </remarks>
    public static Footing Measure(FootingOutline outline, float half, Vector3 centre, Func<float, float, Vector3> place)
    {
        var up = Planet.Up(centre);
        var slab = Scene.FoundationSlab;
        var inGround = float.NegativeInfinity;
        foreach (var p in Foundations.Samples(outline, half))
        {
            var reaches = slab + half - Across(outline, p.X, p.Y);
            var groundDepth = Scene.PadTop - Foundations.GroundUnder(centre, up, place(p.X, p.Y));
            inGround = MathF.Max(inGround, MathF.Min(reaches, groundDepth));
        }
        // On ground about level with the top, the slab alone reaches the margin into it.
        return new Footing(outline, half, Math.Clamp(inGround + Scene.FoundationMargin, slab, slab + half));
    }

    /// <summary>The top's shape.</summary>
    public FootingOutline Outline { get; }

    /// <summary>How far the top reaches from its middle: a square's half-size, a circle's radius.</summary>
    public float Half { get; }

    /// <summary>How thick the straight-sided slab under the top is (<see cref="Scene.FoundationSlab"/>).</summary>
    public float Slab => Scene.FoundationSlab;

    /// <summary>How far below the top the taper reaches: in the ground all across, or to its point.</summary>
    public float Depth { get; }

    /// <summary>
    /// The collider: the slab, the whole top across, then the taper as boxes (round ones, cylinders,
    /// under a round top), each <see cref="Scene.FoundationStep"/> tall (the last to <see cref="Depth"/>,
    /// taking in a remainder shorter than <see cref="Scene.FoundationShortestStep"/>), stacked down it, top first.
    /// </summary>
    public IReadOnlyList<Column> Boxes { get; }

    /// <summary>How far from the middle (x, z) is, as a top of <paramref name="outline"/> measures it: the larger of |x| and |z| on a square, the distance on a circle.</summary>
    public static float Across(FootingOutline outline, float x, float z) =>
        outline == FootingOutline.Square ? MathF.Max(MathF.Abs(x), MathF.Abs(z)) : MathF.Sqrt(x * x + z * z);

    /// <summary>How far from the middle the foundation reaches <paramref name="depth"/> below the top: the top's own reach through the slab, then a metre less each metre down.</summary>
    public float HalfWidthAt(float depth) => Half - MathF.Max(0f, depth - Slab);

    /// <summary>
    /// How far below the top the foundation's underside is under (<paramref name="x"/>,
    /// <paramref name="z"/>) in its frame (the slab's under its edge, deeper in down the taper, to
    /// <see cref="Depth"/>), or null if that is outside the top.
    /// </summary>
    public float? UnderAt(float x, float z)
    {
        var across = Across(Outline, x, z);
        return across > Half ? null : MathF.Min(Depth, Slab + Half - across);
    }
}

/// <summary>
/// One of a <see cref="Footing"/>'s <see cref="Footing.Boxes"/>, in its frame: centred under the
/// top, <see cref="Half"/> across from its middle each way (a square box under a pad, a cylinder of
/// that radius under a zone), from <see cref="Top"/> down to <see cref="Depth"/> metres below
/// <see cref="Scene.PadTop"/>.
/// </summary>
internal readonly record struct Column(float Top, float Depth, float Half);

/// <summary>
/// A landing zone's shape: a round top, <see cref="Scene.LandingZoneRadius"/> across, tilted by
/// <see cref="Tilt"/>, on a plinth whose sides run straight down (along <see cref="Planet.Up"/> at
/// its centre) to its foundation's bottom, <see cref="Depth"/> below <see cref="Scene.PadTop"/>
/// over the centre. A level zone's plinth is a cylinder; a tilted one's is the top's shadow cast
/// straight down, an ellipse, so it fills everything between the top and the ground and nothing
/// overhangs. A far zone stands on its <see cref="Footing"/> instead: a slab, then a cone frustum
/// down into the ground.
/// </summary>
internal sealed class Plinth
{
    public Plinth(Vector3 centre, Quaternion tilt, float depth)
    {
        Centre = centre;
        Tilt = tilt;
        Depth = depth;
        Up = Planet.Up(centre);
        Normal = Vector3.Transform(Vector3.UnitY, tilt);
        Top = centre + Normal * Scene.PadTop;
        _inverse = Quaternion.Inverse(tilt);
        _upDotNormal = Vector3.Dot(Up, Normal);
        Slope = MathF.Atan2(Vector3.Cross(Up, Normal).Length(), _upDotNormal);
        // As far from the top's centre as anything over it or in its plinth can be (with room to spare).
        _reach = 2f * Scene.LandingZoneRadius + 50f + Depth;
    }

    /// <summary>A far zone, level on the curve, on <paramref name="footing"/> (in the zone's frame).</summary>
    public Plinth(Vector3 centre, Quaternion tilt, Footing footing) : this(centre, tilt, footing.Depth) => Footing = footing;

    private readonly Quaternion _inverse;
    private readonly float _upDotNormal, _reach;

    /// <summary>A zone tilted less than this (radians) is level: it stands on a drum, or on its <see cref="Footing"/>.</summary>
    public const float LevelSlope = 1e-4f;

    /// <summary>The base's centre: the top is <see cref="Scene.PadTop"/> above it, along <see cref="Normal"/>.</summary>
    public Vector3 Centre { get; }
    public Quaternion Tilt { get; }
    /// <summary>How far below <see cref="Scene.PadTop"/> over the centre its foundation reaches, at its deepest.</summary>
    public float Depth { get; }
    /// <summary>Which way the plinth's sides run.</summary>
    public Vector3 Up { get; }
    /// <summary>Which way the top faces.</summary>
    public Vector3 Normal { get; }
    /// <summary>The top's centre.</summary>
    public Vector3 Top { get; }
    /// <summary>A far zone's foundation, in its frame: a slab, then a cone frustum (see <see cref="GameOne.Footing"/>); or null, for a zone on a drum or a tilted plinth.</summary>
    public Footing? Footing { get; }

    /// <summary>How far the top is tilted from level, in radians.</summary>
    public float Slope { get; }

    /// <summary>How far the foundation's bottom is above the centre (negative: below it), along <see cref="Up"/>.</summary>
    public float Bottom => Scene.PadTop - Depth;

    /// <summary>Whether a point within <paramref name="distance"/> of <paramref name="p"/> could be over the zone or inside its plinth (see <see cref="HeightAbove"/>).</summary>
    public bool Within(Vector3 p, float distance) => Vector3.Distance(p, Top) <= _reach + distance;

    /// <summary>
    /// Whether a point within <paramref name="distance"/> of <paramref name="p"/> could be beside the
    /// plinth's side, or in it: across the ground (square to <see cref="Up"/>, which its sides run
    /// along), within the top's radius and the distance of its centre (the plinth is the top's
    /// shadow cast down, and a footing tucked in under it, so no wider); no higher than its top;
    /// and no lower than its foundation's bottom. (Not <see cref="Within"/>: that reaches 50 m over
    /// the top as well, for what is over it.)
    /// </summary>
    public bool Beside(Vector3 p, float distance)
    {
        var d = p - Top;
        if (Vector3.Dot(d, Normal) >= distance || Vector3.Dot(p - Centre, Up) <= Bottom - distance)
            return false;
        var along = Vector3.Dot(d, Up);
        var far = Scene.LandingZoneRadius + distance;
        return d.LengthSquared() - along * along <= far * far;
    }

    /// <summary>
    /// The height of <paramref name="p"/> above the top, along its normal, if it is over the zone
    /// (straight above or below the top, and lower than 50 m over it) or inside its plinth or
    /// footing (when it is negative); or null if it is clear of it.
    /// </summary>
    public float? HeightAbove(Vector3 p)
    {
        var d = p - Top;
        if (d.LengthSquared() > _reach * _reach)
            return null; // nowhere near (most calls, from anywhere but here)
        var above = Vector3.Dot(d, Normal);
        if (above >= 50f || Vector3.Dot(p - Centre, Up) <= Bottom)
            return null;
        // Where on the top's plane p is straight above or below, in the zone's own frame.
        var local = Vector3.Transform(d - Up * (above / _upDotNormal), _inverse);
        if (local.X * local.X + local.Z * local.Z > Scene.LandingZoneRadius * Scene.LandingZoneRadius)
            return null;
        // On a footing, below the slab: in the concrete only above the taper's underside there
        // (further out, it is under the slab, in the air or the hill). Within the rim, as p is by
        // here, the footing is always under it.
        var height = Vector3.Dot(p - Centre, Up);
        if (Footing is { } footing && height <= Scene.PadTop - footing.UnderAt(local.X, local.Z)!.Value)
            return null;
        return above;
    }

    /// <summary>The bottom of the plinth's side under the top's rim at <paramref name="rim"/> (both in the zone's own frame).</summary>
    public Vector3 Under(Vector3 rim)
    {
        var up = Vector3.Transform(Up, _inverse);
        return rim - up * (Vector3.Dot(rim, up) - Bottom);
    }

    // The collider of a tilted zone (see RocketPhysics.AddTiltedZone), worked out here, beside the
    // shape it has to fit, so that the tests can measure the fit.

    /// <summary>How thick the collider's top slab is, along the top's normal.</summary>
    public const float SlabThickness = 0.3f;

    /// <summary>
    /// The most the collider's columns may leave of the plinth uncovered: no point of the drawn
    /// wall is further than this (roughly: see <see cref="Columns"/>) from a column.
    /// </summary>
    public const float ColumnFit = 0.1f;

    /// <summary>
    /// The plinth's own frame, for its collider: x straight across the slope, downhill; y up its
    /// sides (<see cref="Up"/>); z along the slope. Its origin is <see cref="Centre"/>. In it the
    /// top's normal is (sin, cos, 0) of the slope.
    /// </summary>
    public Quaternion Frame
    {
        get
        {
            var downhill = Vector3.Normalize(Normal - Up * Vector3.Dot(Normal, Up));
            var along = Vector3.Cross(downhill, Up);
            return Quaternion.CreateFromRotationMatrix(new Matrix4x4(
                downhill.X, downhill.Y, downhill.Z, 0f,
                Up.X, Up.Y, Up.Z, 0f,
                along.X, along.Y, along.Z, 0f,
                0f, 0f, 0f, 1f));
        }
    }

    /// <summary>The top's height at <paramref name="x"/> across the slope, in <see cref="Frame"/>.</summary>
    public float TopAt(float x) => (Scene.PadTop - x * MathF.Sin(Slope)) / MathF.Cos(Slope);

    /// <summary>
    /// The upright boxes that fill a tilted zone's plinth under its slab, in <see cref="Frame"/>: each
    /// from <c>X0</c> to <c>X1</c> across the slope (X1 downhill), <c>HalfAlong</c> either way along it,
    /// and from <see cref="Bottom"/> up to <c>Top</c>.
    /// </summary>
    /// <remarks>
    /// The plinth is an ellipse in plan, its half-width along the slope <c>f(u)</c> at <c>u</c> across
    /// it from its middle. Each box takes the half-width at its outer edge, so it is inside the
    /// ellipse (its outer corners on it). Between two boxes the wall is left uncovered in a sliver,
    /// at most about <c>du dz / sqrt(du² + dz²)</c> deep (the step's corner from the chord): the
    /// boxes are made as wide as keeps that under <see cref="ColumnFit"/>, so narrow where the wall
    /// turns, near the ends, and the last ends <see cref="ColumnFit"/> short of each end. Each box's
    /// top is just under the top at its downhill side; at its uphill side the top is higher by its
    /// width times the slope's tangent, so the boxes are kept narrow enough that it still reaches
    /// into the slab there, leaving no slot under it.
    /// </remarks>
    public IReadOnlyList<(float X0, float X1, float HalfAlong, float Top)> Columns => _columns ??= MakeColumns();

    private (float, float, float, float)[]? _columns;

    private (float, float, float, float)[] MakeColumns()
    {
        var (sin, cos) = MathF.SinCos(Slope);
        var r = Scene.LandingZoneRadius;
        float middle = Scene.PadTop * sin, across = r * cos;
        float F(float u) => r * MathF.Sqrt(MathF.Max(0f, 1f - u * u / (across * across)));
        const float under = 0.05f; // how far each box's top is under the top, at its downhill side
        var widest = MathF.Min(2f, (SlabThickness / cos - under - 0.02f) / MathF.Tan(Slope)); // the tops 2 cm into the slab, at least

        // The edges out from the middle, the same on each side.
        var edges = new List<float> { 0f };
        var end = across - ColumnFit;
        while (edges[^1] < end)
        {
            var u0 = edges[^1];
            var width = MathF.Min(widest, end - u0);
            while (true)
            {
                var dz = F(u0) - F(u0 + width);
                if (width * dz / MathF.Sqrt(width * width + dz * dz) <= ColumnFit || width < 1e-3f)
                    break;
                width *= 0.8f;
            }
            edges.Add(MathF.Min(u0 + width, end));
        }

        var columns = new List<(float, float, float, float)>();
        for (var k = 0; k + 1 < edges.Count; k++)
            foreach (var side in (ReadOnlySpan<float>)[1f, -1f])
            {
                float a = middle + side * edges[k], b = middle + side * edges[k + 1];
                var (x0, x1) = (MathF.Min(a, b), MathF.Max(a, b));
                columns.Add((x0, x1, F(edges[k + 1]), TopAt(x1) - under));
            }
        return [.. columns];
    }
}
