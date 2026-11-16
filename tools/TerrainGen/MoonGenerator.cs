using System.Diagnostics;
using System.Numerics;
using GameOne;

namespace TerrainGen;

/// <summary>
/// Makes the moon's map from its layout: gentle plains of fractal noise; the big craters (a bowl
/// below the plains, a raised rim, thrown-out rock thinning away round it, and pale rays); the
/// canyon (a rough crescent: its path wandering, its width and depth swinging, ragged walls,
/// slumps and side branches); the far side's tail (a ridge rising to a mountain with a crater in its
/// top); small craters scattered by the seed; and the
/// landing spot (<see cref="Scene.MoonLandingSpot"/>) levelled. Every height is worked out from the
/// texel's own direction, all in parallel, so a layout always gives the same map. No erosion, sea
/// or regions: the moon is airless and dry.
/// </summary>
internal sealed class MoonGenerator(MoonLayout layout, Action<string> log)
{
    private const float Radius = Scene.MoonRadius;

    // Where a big crater's kinds change, as fractions of its radius from its centre: its floor,
    // then its inner wall (plain), then its pale rim out over the crest, then its rays.
    private const float FloorTo = 0.6f;
    private const float RimFrom = 0.85f;
    private const float RimTo = 1.15f;
    private const float RimWander = 0.12f;        // how far (in radii) noise moves the pale rim's outer edge in and out
    private const float BigEjectaReach = 4f;      // radii out to which a big crater's thrown-out rock lies
    private const float SmallEjectaReach = 2.5f;  // and a small one's
    private const float SmallDepth = 0.2f;        // a small crater's depth, as a share of its diameter
    private const float SmallRim = 0.04f;         // and its rim's height
    private const float RayHalfWidth = 0.07f;     // a ray's half-width where it leaves the rim, in crater radii (widening and narrowing by half along it)
    private const float ClearOf = 1.5f;           // radii round a big crater no small one's rock reaches into
    private const float CanyonStartHorn = 0.08f;  // the share of its arc over which the canyon shallows to nothing at its start's horn, ...
    private const float CanyonEndHorn = 0.16f;    // ... and at its end's: the horns uneven, one tapering longer
    private const float CanyonWidthSwing = 0.35f; // its width swings by this share of the layout's along it, ...
    private const float CanyonDepthSwing = 0.25f; // ... and its depth by this share
    private const float CanyonSlump = 0.45f;      // where a slump lies across it, this share of its depth is filled
    private const float CanyonRagged = 24f;       // metres its walls are pushed in and out by (spurs and slumped bays), at about 70 m along them
    private const float BranchWidth = 0.45f;      // a side branch's width, as a share of the canyon's where it leaves it, ...
    private const float BranchDepth = 0.55f;      // ... and its depth
    private const int BinsAFace = 64;             // the small craters are binned by cells of the cube's faces, this many each way (about 60 m), so a texel tests only those that can reach it
    private const float SiteFlats = 300f;         // metres beyond the landing spot's blend that are flats whatever the noise, so it lies in one
    private const int FlatsSamples = 20_000;      // directions the flats' noise is read at to find the level leaving their share of the ground above it

    /// <summary>The canyon's floor runs this share of its width out from its middle line each side; its walls rise from there to its rims, at half its width.</summary>
    public const float CanyonFloor = 0.3f;

    /// <summary>A crater's centre and size; its rays (for a big one) are each a bearing (degrees) and a length (in radii).</summary>
    private sealed record Crater(Vector3 Centre, float RadiusMetres, float Depth, float Rim, float Reach, (float Bearing, float Length)[] Rays, bool Pale = false)
    {
        public readonly float CosReach = MathF.Cos(RadiusMetres * Reach / Radius);
    }

    /// <summary>A side branch of the canyon: from a point on its middle line, straight out along <c>Way</c> for <c>Length</c> metres, narrowing and shallowing to nothing.</summary>
    private sealed record Branch(Vector3 Start, Vector3 Way, Vector3 Side, float Length, float Width, float Depth)
    {
        public readonly float CosReach = MathF.Cos((Length + Width) / Radius);
    }

    /// <summary>
    /// The canyon across its arc at one point along it: the bearing from its centre, its middle
    /// line's distance out from it and where that is, its width, and its floor's depth below the
    /// plains (its horns' shallowing and any slump across it included).
    /// </summary>
    public readonly record struct CanyonSection(float Bearing, float Radius, Vector3 Middle, float Width, float Depth);

    /// <summary>
    /// The far side's tail as laid out: its crest's smooth curve through the layout's points
    /// (<see cref="TailSamples"/> directions, tip first, and how far along it each is, 0 to 1), its
    /// length in metres, and the mountain's middle at its base.
    /// </summary>
    public sealed record TailLine(Vector3[] Points, float[] Along, float Length, Vector3 Summit)
    {
        /// <summary>A cap round the whole line (its middle, and the cosine of its reach), so a place far from it is passed over at once.</summary>
        public readonly Vector3 Middle = Vector3.Normalize(Points.Aggregate(Vector3.Zero, (sum, p) => sum + p));
        public float CosReach { get; init; }
    }

    private const int TailSamples = 400;
    private const float MountainFlank = 2f;       // the mountain's flanks fall as (1 - s) to this power from its summit's rim (s 0) to its foot (s 1): concave, steepest under the rim (about 50° for 800 m over 1.35 km), easing to level at its foot
    private TailLine? _tail;

    /// <summary>The far side's tail's line, made once from the layout.</summary>
    public TailLine Tail => _tail ??= MakeTail();

    private readonly Noise _noise = new(layout.Seed);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Branch[] _branches = [];

    /// <summary>The canyon's circle's centre, worked out once (it is asked for every texel near the canyon).</summary>
    private readonly Vector3 _canyonCentre = Degrees.Direction(layout.Canyon.At);

    private void Step(string what) => log($"{_clock.Elapsed.TotalSeconds,6:F1}s  {what}");

    /// <summary>What the ground is made of, laid out once from the layout (in this order, as they draw on one seeded random): the big craters, the canyon's branches, the tail, the small craters and their bins, and the landing spot and its height.</summary>
    private sealed record Features(Crater[] Big, TailLine Tail, Crater[] Small, int[][] Bins, Vector3 Site, float SiteHeight);

    private Features LayOut()
    {
        var random = new Random(layout.Seed);
        var big = layout.Craters.Select(c =>
        {
            var rays = new (float, float)[c.Rays];
            for (var k = 0; k < c.Rays; k++)
                rays[k] = ((k + 0.5f + 0.6f * (random.NextSingle() - 0.5f)) * 360f / c.Rays, 2.5f + 2f * random.NextSingle());
            return new Crater(Degrees.Direction(c.At), c.Diameter * 0.5f, c.Depth, c.RimHeight, BigEjectaReach, rays);
        }).ToArray();

        _branches = Branches();
        var tail = Tail;
        var site = CubeSphere.FromLatLon(Scene.MoonLandingSpot.Latitude * MathF.PI / 180f, Scene.MoonLandingSpot.Longitude * MathF.PI / 180f);
        var small = SmallCraters(random, big, site);
        var bins = Bin(small);
        Step($"{big.Length} big craters, the canyon with {_branches.Length} side branches, {small.Length} small craters ({small.Count(c => c.Pale)} with pale rims), the flats {layout.Flats.Share:P0} of the ground");

        // The landing spot is levelled at the plains' own height there (no crater lies on it).
        return new Features(big, tail, small, bins, site, Plains(site));
    }

    public TerrainMap Run()
    {
        var grid = new Grid(layout.Resolution, Radius);
        Step($"grid: {layout.Resolution} a face, {grid.Count:N0} texels, {MathF.PI / 2f * Radius / layout.Resolution:F1} m apart");
        var features = LayOut();
        var height = new float[grid.Count];
        var kind = new byte[grid.Count];
        Parallel.For(0, grid.Count, i =>
            (height[i], kind[i]) = Texel(features, grid.Directions[i], i / (grid.N * grid.N), i % grid.N, i / grid.N % grid.N, grid.N));
        Step("heights and kinds");

        var (metres, kinds, regions) = Aprons.Add(grid, height, kind, new byte[grid.Count]);
        var (heights, min, max) = Aprons.Quantise(metres);
        Step($"assembled: {min:F1} to {max:F1} m about the datum sphere");
        return new TerrainMap(layout.Resolution, Radius, 0f, min, max, heights, kinds, regions, []);
    }

    /// <summary>
    /// The heights (metres, before they are quantised) and kinds of every <paramref name="every"/>th
    /// row of each face (rows 0, every, 2 every, ...), each row's texels in turn, worked out as
    /// <see cref="Run"/> works out every texel: so a test can check part of the map against the
    /// shipped one quickly.
    /// </summary>
    internal (float[] Heights, byte[] Kinds) Rows(int every)
    {
        var n = layout.Resolution;
        var features = LayOut();
        var rows = (n + every - 1) / every;
        var height = new float[6 * rows * n];
        var kind = new byte[6 * rows * n];
        Parallel.For(0, 6 * rows, row =>
        {
            int face = row / rows, j = row % rows * every;
            for (var i = 0; i < n; i++)
                (height[row * n + i], kind[row * n + i]) =
                    Texel(features, CubeSphere.FromFace(face, (i + 0.5f) / n * 2f - 1f, (j + 0.5f) / n * 2f - 1f), face, i, j, n);
        });
        return (height, kind);
    }

    /// <summary>The ground's height (metres from the datum sphere) and kind in direction <paramref name="d"/>, texel (<paramref name="i"/>, <paramref name="j"/>) of <paramref name="face"/> on a grid <paramref name="n"/> a face.</summary>
    private (float Height, byte Kind) Texel(Features f, Vector3 d, int face, int i, int j, int n)
    {
        var h = Plains(d);
        var k = MoonKind.Plain;
        foreach (var crater in f.Big)
        {
            var dot = Vector3.Dot(d, crater.Centre);
            if (dot < crater.CosReach) continue;
            var r = Arc(d, crater.Centre) / crater.RadiusMetres;
            h += Profile(crater, r);
            var rimTo = RimTo + RimWander * _noise.Fbm(11, d * (Radius / 120f), 2);
            if (r < FloorTo) k = MoonKind.Floor;
            else if (r >= RimFrom && r < rimTo) k = MoonKind.Rim;
            else if (r >= rimTo && k == MoonKind.Plain && OnARay(crater, d, r)) k = MoonKind.Ray;
        }

        var (cut, floor) = CanyonCut(d);
        h -= cut;
        if (floor) k = MoonKind.Floor;

        var (rise, tailKind) = TailRise(f.Tail, d);
        h += rise;
        if (tailKind is { } summitKind) k = summitKind;

        // Only the small craters binned to this texel's cell can reach it; each adds its own
        // profile to what is there, so a younger one's bowl cuts an older one's rim.
        var cell = (face * BinsAFace + j * BinsAFace / n) * BinsAFace + i * BinsAFace / n;
        foreach (var c in f.Bins[cell])
        {
            var crater = f.Small[c];
            if (Vector3.Dot(d, crater.Centre) < crater.CosReach) continue;
            var r = Arc(d, crater.Centre) / crater.RadiusMetres;
            h += Profile(crater, r);
            if (k == MoonKind.Plain && crater.Pale && r >= RimFrom && r < RimTo) k = MoonKind.SmallRim;
        }

        var level = 1f - Smooth(Scene.MoonLandingSpotRadius, Scene.MoonLandingSpotRadius + layout.Site.Blend, Arc(d, f.Site));
        return (h + (f.SiteHeight - h) * level, (byte)(level > 0f ? MoonKind.Plain : k));
    }

    /// <summary>The plains' gentle swells in <paramref name="d"/>, in metres.</summary>
    private float Plains(Vector3 d) => layout.Plains.Relief * _noise.Fbm(0, d * (Radius / layout.Plains.Wavelength), 3, 0.45f);

    /// <summary>
    /// A crater's height at <paramref name="r"/> radii from its centre: a parabolic bowl from its
    /// floor up to its rim's crest at 1, then its thrown-out rock thinning as the cube of the
    /// distance out to its reach (where it is 0).
    /// </summary>
    private static float Profile(Crater crater, float r)
    {
        if (r < 1f) return -crater.Depth + (crater.Depth + crater.Rim) * r * r;
        if (r >= crater.Reach) return 0f;
        var end = 1f / (crater.Reach * crater.Reach * crater.Reach);
        return crater.Rim * (1f / (r * r * r) - end) / (1f - end);
    }

    /// <summary>Whether <paramref name="d"/>, <paramref name="r"/> radii out, is on one of the crater's rays: each narrowing to its end, broken up along its way by noise.</summary>
    private bool OnARay(Crater crater, Vector3 d, float r)
    {
        var bearing = Bearing(crater.Centre, d);
        foreach (var (rayBearing, length) in crater.Rays)
        {
            if (r >= length) continue;
            var off = MathF.IEEERemainder(bearing - rayBearing, 360f) * MathF.PI / 180f;
            if (MathF.Abs(off) > MathF.PI / 2f) continue;
            var across = MathF.Abs(r * MathF.Sin(off));
            var narrowing = 1f - (r - RimTo) / (length - RimTo);
            var width = RayHalfWidth * narrowing * (1f + 0.5f * _noise.Fbm(12, d * (Radius / 150f), 2));
            if (across < width && _noise.Fbm(10, d * (Radius / 50f), 2) > -0.15f) return true;
        }

        return false;
    }

    /// <summary>The tail's line: a Catmull-Rom curve through the layout's points, on the sphere.</summary>
    private TailLine MakeTail()
    {
        var t = layout.Tail;
        var points = t.Path.Select(Degrees.Direction).ToArray();
        var line = new Vector3[TailSamples];
        for (var i = 0; i < TailSamples; i++)
        {
            var u = (float)i / (TailSamples - 1) * (points.Length - 1);
            var seg = Math.Min((int)u, points.Length - 2);
            var f = u - seg;
            var p0 = points[Math.Max(seg - 1, 0)];
            var p1 = points[seg];
            var p2 = points[seg + 1];
            var p3 = points[Math.Min(seg + 2, points.Length - 1)];
            line[i] = Vector3.Normalize(0.5f * (2f * p1 + (p2 - p0) * f + (2f * p0 - 5f * p1 + 4f * p2 - p3) * f * f + (3f * p1 - p0 - 3f * p2 + p3) * f * f * f));
        }

        var along = new float[TailSamples];
        for (var i = 1; i < TailSamples; i++) along[i] = along[i - 1] + Arc(line[i - 1], line[i]);
        var length = along[^1];
        for (var i = 0; i < TailSamples; i++) along[i] /= length;
        var shape = new TailLine(line, along, length, line[^1]);
        var reach = line.Max(p => Arc(p, shape.Middle)) + t.BaseWidth + t.Wander + t.MountainRadius;
        return shape with { CosReach = MathF.Cos(reach / Radius) };
    }

    /// <summary>The nearest point of the tail's line to <paramref name="d"/>: the metres to it (signed, by which side) and how far along the line it is (0 at the tip, 1 at the base).</summary>
    private static (float Across, float Signed, float Along) TailNearest(TailLine tail, Vector3 d)
    {
        var best = float.MaxValue;
        var signed = 0f;
        var along = 0f;
        for (var i = 0; i + 1 < tail.Points.Length; i++)
        {
            var a = tail.Points[i];
            var v = tail.Points[i + 1] - a;
            var s = Math.Clamp(Vector3.Dot(d - a, v) / v.LengthSquared(), 0f, 1f);
            var q = a + v * s;
            var off = d - q;
            var gap = off.LengthSquared();
            if (gap >= best) continue;
            best = gap;
            signed = MathF.Sign(Vector3.Dot(off, Vector3.Cross(q, v))) * MathF.Sqrt(gap) * Radius;
            along = tail.Along[i] + (tail.Along[i + 1] - tail.Along[i]) * s;
        }

        return (MathF.Sqrt(best) * Radius, signed, along);
    }

    /// <summary>
    /// How high the far side's tail stands over the plains in <paramref name="d"/>, and the kind
    /// of ground where it sets one (the summit crater's pale rim and its floor). The ridge's crest
    /// rises from its tip to its base and widens with it, a rounded hump across, its line wandering
    /// and its crest and flanks a little uneven; the mountain is a cone with concave flanks, its top
    /// hollowed by its crater. Where the two meet, the higher stands.
    /// </summary>
    private (float Rise, MoonKind? Kind) TailRise(TailLine tail, Vector3 d)
    {
        if (Vector3.Dot(d, tail.Middle) < tail.CosReach) return (0f, null);
        var t = layout.Tail;
        // Far from every 16th point of the line by more than the ridge's widest reach and the
        // points' spacing, nothing of the ridge is here; and past the mountain's foot, nothing of it.
        var coarse = float.MaxValue;
        for (var i = 0; i < tail.Points.Length; i += 16) coarse = MathF.Min(coarse, Vector3.DistanceSquared(d, tail.Points[i]));
        var clear = (0.5f * t.BaseWidth + t.Wander + 16f * tail.Length / (TailSamples - 1)) / Radius;
        if (coarse > clear * clear && Vector3.Dot(d, tail.Summit) < MathF.Cos(t.MountainRadius / Radius)) return (0f, null);
        var rough = _noise.Fbm(33, d * (Radius / 80f), 3);

        var mountain = 0f;
        MoonKind? kind = null;
        var r = Arc(d, tail.Summit);
        var rc = t.CraterDiameter * 0.5f;
        if (r < t.MountainRadius)
        {
            if (r >= rc)
            {
                var s = (r - rc) / (t.MountainRadius - rc);
                // Its flanks furrowed broadly: a finer roughness on a slope this tall would be all spikes.
                mountain = t.MountainHeight * MathF.Pow(1f - s, MountainFlank) * (1f + 0.06f * _noise.Fbm(34, d * (Radius / 250f), 3));
            }
            else
            {
                mountain = t.MountainHeight - t.CraterDepth * (1f - r * r / (rc * rc));
            }

            if (r < 0.75f * rc) kind = MoonKind.Floor;
            else if (r < 1.25f * rc) kind = MoonKind.Rim;
        }

        var (_, signed, along) = TailNearest(tail, d);
        var across = MathF.Abs(signed + t.Wander * AlongTail(31, along, 8f));
        var half = 0.5f * (t.TipWidth + (t.BaseWidth - t.TipWidth) * along);
        var ridge = 0f;
        if (across < half)
        {
            var x = across / half;
            var crest = (t.TipHeight + (t.BaseHeight - t.TipHeight) * MathF.Pow(along, 1.5f)) * (1f + 0.2f * AlongTail(32, along, 12f));
            ridge = crest * (1f - x * x) * (1f - x * x) * (1f + 0.15f * rough) * Smooth(0f, 0.03f, along);
        }

        return ridge > mountain ? (ridge, null) : (mountain, kind);
    }

    /// <summary>Noise along the tail's line, as <see cref="AlongArc"/> is along the canyon's.</summary>
    private float AlongTail(int field, float along, float cycles) => AlongArc(field, along, cycles);

    /// <summary>Fractal noise along the canyon's arc (<paramref name="along"/> 0 to 1), about <paramref name="cycles"/> swings over its length, in [-1, 1]: noise read along a line seldom swings past half its range, so it is doubled (and clamped) to use the whole of it.</summary>
    private float AlongArc(int field, float along, float cycles) => Math.Clamp(2f * _noise.Fbm(field, new Vector3(along * cycles, 0.37f, 0.71f), 3), -1f, 1f);

    /// <summary>
    /// The canyon at <paramref name="along"/> its arc (0 at the start's horn, 1 at the end's): its
    /// middle line wandering in and out from the layout's circle by up to about its <c>wander</c>,
    /// its width and depth swinging along it, a few slumps filling it part way, and its horns
    /// shallowing to nothing, the end's over a longer stretch than the start's.
    /// </summary>
    public CanyonSection CanyonAt(float along)
    {
        var c = layout.Canyon;
        var bearing = c.StartBearing + along * (c.EndBearing - c.StartBearing);
        var radius = c.Radius + c.Wander * AlongArc(20, along, 6f);
        var width = c.Width * (1f + CanyonWidthSwing * AlongArc(21, along, 10f));
        var depth = c.Depth * (1f + CanyonDepthSwing * AlongArc(22, along, 9f))
                    * (1f - CanyonSlump * Smooth(0.3f, 0.6f, AlongArc(24, along, 5f)))
                    * Smooth(0f, CanyonStartHorn, along) * Smooth(0f, CanyonEndHorn, 1f - along);
        return new CanyonSection(bearing, radius, Out(_canyonCentre, bearing, radius), width, depth);
    }

    /// <summary>The farthest (metres) the canyon's ground reaches from the layout's circle, its wander, widest swing and ragged walls included.</summary>
    private float CanyonReach => layout.Canyon.Wander + layout.Canyon.Width * (1f + CanyonWidthSwing) * 0.5f + CanyonRagged * 1.6f;

    /// <summary>
    /// How deep the canyon and its side branches cut into the ground in <paramref name="d"/>, in
    /// metres, and whether that is on a floor. Their walls are pushed in and out by noise, into
    /// spurs and slumped bays, so they are ragged rather than an even curve.
    /// </summary>
    private (float Cut, bool Floor) CanyonCut(Vector3 d)
    {
        var c = layout.Canyon;
        var centre = _canyonCentre;
        var fromCentre = Arc(d, centre);
        var near = MathF.Abs(fromCentre - c.Radius) < CanyonReach;
        foreach (var branch in _branches)
            near |= Vector3.Dot(d, branch.Start) >= branch.CosReach;
        if (!near) return (0f, false);

        var ragged = CanyonRagged * _noise.Fbm(23, d * (Radius / 70f), 3);
        var cut = 0f;
        var floor = false;
        var bearing = Bearing(centre, d);
        while (bearing < c.StartBearing) bearing += 360f;
        if (bearing <= c.EndBearing)
        {
            var section = CanyonAt((bearing - c.StartBearing) / (c.EndBearing - c.StartBearing));
            var across = MathF.Max(MathF.Abs(fromCentre - section.Radius) + ragged, 0f);
            cut = section.Depth * (1f - Smooth(CanyonFloor * section.Width, 0.5f * section.Width, across));
            floor = across < CanyonFloor * section.Width && section.Depth > 0.3f * c.Depth;
        }

        foreach (var branch in _branches)
        {
            if (Vector3.Dot(d, branch.Start) < branch.CosReach) continue;
            var v = d - branch.Start;
            var x = Vector3.Dot(v, branch.Way) * Radius;
            if (x < -0.5f * branch.Width || x > branch.Length) continue;
            var t = Math.Clamp(x / branch.Length, 0f, 1f);
            var width = branch.Width * (1f - 0.4f * t);
            var across = MathF.Max(MathF.Abs(Vector3.Dot(v, branch.Side) * Radius) + ragged, 0f);
            var branchCut = branch.Depth * Smooth(1f, 0.6f, t) * (1f - Smooth(CanyonFloor * width, 0.5f * width, across));
            if (branchCut > cut)
            {
                cut = branchCut;
                floor = across < CanyonFloor * width && t < 0.6f;
            }
        }

        return (cut, floor);
    }

    /// <summary>
    /// The canyon's side branches, placed by the seed: spread along its middle stretch, each leaving
    /// its outer wall (away from the craters) at a slant, 180 to 400 m long.
    /// </summary>
    private Branch[] Branches()
    {
        var c = layout.Canyon;
        var centre = _canyonCentre;
        var random = new Random(layout.Seed + 1);
        var branches = new Branch[c.Branches];
        for (var b = 0; b < c.Branches; b++)
        {
            var section = CanyonAt(0.2f + 0.6f * (b + 0.5f) / c.Branches + 0.08f * (random.NextSingle() - 0.5f));
            var start = section.Middle;
            var outward = Vector3.Normalize(start * Vector3.Dot(start, centre) - centre);
            var slant = (random.NextSingle() - 0.5f) * 70f * MathF.PI / 180f;
            var way = Vector3.Normalize(outward * MathF.Cos(slant) + Vector3.Cross(start, outward) * MathF.Sin(slant));
            branches[b] = new Branch(start, way, Vector3.Cross(start, way), 180f + 220f * random.NextSingle(),
                section.Width * BranchWidth, section.Depth * BranchDepth);
        }

        return branches;
    }

    private float? _flatsLevel;

    /// <summary>The flats' low noise in <paramref name="d"/>: the flats are where it is over <see cref="FlatsLevel"/>.</summary>
    private float FlatsNoise(Vector3 d) => _noise.Fbm(40, d * (Radius / layout.Flats.Wavelength), 2);

    /// <summary>The level of the flats' noise that leaves their share of the ground above it, read at a spread of directions evenly over the sphere (a Fibonacci lattice).</summary>
    private float FlatsLevel => _flatsLevel ??= MakeFlatsLevel();

    private float MakeFlatsLevel()
    {
        var values = new float[FlatsSamples];
        var turn = MathF.PI * (3f - MathF.Sqrt(5f));
        for (var i = 0; i < FlatsSamples; i++)
        {
            var z = 1f - 2f * (i + 0.5f) / FlatsSamples;
            var ring = MathF.Sqrt(1f - z * z);
            values[i] = FlatsNoise(new Vector3(ring * MathF.Cos(turn * i), ring * MathF.Sin(turn * i), z));
        }

        Array.Sort(values);
        return values[(int)((1f - layout.Flats.Share) * (FlatsSamples - 1))];
    }

    /// <summary>
    /// Whether <paramref name="d"/> is in one of the flats, the patches of smooth plains between the
    /// craters (where few and only the smallest fall): by the flats' low noise, and all round the
    /// landing spot.
    /// </summary>
    public bool InFlats(Vector3 d)
    {
        var site = CubeSphere.FromLatLon(Scene.MoonLandingSpot.Latitude * MathF.PI / 180f, Scene.MoonLandingSpot.Longitude * MathF.PI / 180f);
        return Arc(d, site) < Scene.MoonLandingSpotRadius + layout.Site.Blend + SiteFlats || FlatsNoise(d) > FlatsLevel;
    }

    /// <summary>
    /// The small craters, scattered by the seed: uniform over the sphere, their sizes as craters'
    /// are (many more small than large: the count larger than a size falls as its square), each kept
    /// clear of the big craters and their thrown-out rock, the canyon and its branches, the tail, and
    /// the landing spot. In the flats only the smallest fall, and only a share of those; in the near
    /// side's middle (if the layout keeps it clear) none of the larger. Those over the layout's
    /// <c>paleOver</c> have faintly pale rims.
    /// </summary>
    private Crater[] SmallCraters(Random random, Crater[] big, Vector3 site)
    {
        var s = layout.SmallCraters;
        var c = layout.Canyon;
        var flats = layout.Flats;
        var canyonCentre = _canyonCentre;
        var nearFace = Degrees.Direction([0f, 0f]);
        var shrink = 1f - (s.Smallest / s.Largest) * (s.Smallest / s.Largest);
        var found = new List<Crater>();
        for (var tries = 0; found.Count < s.Count && tries < s.Count * 20; tries++)
        {
            var z = 2f * random.NextSingle() - 1f;
            var around = 2f * MathF.PI * random.NextSingle();
            var ring = MathF.Sqrt(1f - z * z);
            var at = new Vector3(ring * MathF.Cos(around), ring * MathF.Sin(around), z);
            var diameter = s.Smallest / MathF.Sqrt(1f - random.NextSingle() * shrink);
            var keep = random.NextSingle();
            var reach = diameter * 0.5f * SmallEjectaReach;
            if (InFlats(at) && (diameter > flats.Largest || keep >= flats.Density)) continue;
            if (s.NearFace is { } face && diameter > face.Largest && Arc(at, nearFace) < face.Within * MathF.PI / 180f * Radius) continue;
            if (big.Any(b => Arc(at, b.Centre) < b.RadiusMetres * ClearOf + reach)) continue;
            var bearing = Bearing(canyonCentre, at);
            while (bearing < c.StartBearing - 5f) bearing += 360f;
            if (bearing <= c.EndBearing + 5f && MathF.Abs(Arc(at, canyonCentre) - c.Radius) < CanyonReach + reach) continue;
            if (_branches.Any(b => Arc(at, b.Start) < b.Length + b.Width + reach)) continue;
            if (Arc(at, Tail.Summit) < layout.Tail.MountainRadius + reach) continue;
            if (Vector3.Dot(at, Tail.Middle) >= Tail.CosReach && TailNearest(Tail, at).Across < layout.Tail.BaseWidth + layout.Tail.Wander + reach) continue;
            if (Arc(at, site) < Scene.MoonLandingSpotRadius + layout.Site.Blend + reach) continue;
            found.Add(new Crater(at, diameter * 0.5f, SmallDepth * diameter, SmallRim * diameter, SmallEjectaReach, [], diameter > s.PaleOver));
        }

        return [.. found];
    }

    /// <summary>
    /// For each cell of the cube's faces (<see cref="BinsAFace"/> each way, face-major, then row,
    /// then column, as the grid's texels), the small craters (by index, oldest first) whose
    /// thrown-out rock can reach into it: those reaching within the cell's farthest corner of its
    /// middle. A cell's edges are great circles, so no point of it is farther from its middle than
    /// a corner.
    /// </summary>
    private static int[][] Bin(Crater[] craters)
    {
        var bins = new int[6 * BinsAFace * BinsAFace][];
        var angles = craters.Select(c => c.RadiusMetres * c.Reach / Radius).ToArray();
        Parallel.For(0, bins.Length, cell =>
        {
            var face = cell / (BinsAFace * BinsAFace);
            var row = cell / BinsAFace % BinsAFace;
            var column = cell % BinsAFace;
            float Edge(int n) => (float)n / BinsAFace * 2f - 1f;
            var middle = CubeSphere.FromFace(face, (column + 0.5f) / BinsAFace * 2f - 1f, (row + 0.5f) / BinsAFace * 2f - 1f);
            var corners = new[]
            {
                CubeSphere.FromFace(face, Edge(column), Edge(row)), CubeSphere.FromFace(face, Edge(column + 1), Edge(row)),
                CubeSphere.FromFace(face, Edge(column), Edge(row + 1)), CubeSphere.FromFace(face, Edge(column + 1), Edge(row + 1)),
            };
            var size = corners.Max(corner => MathF.Acos(Math.Clamp(Vector3.Dot(corner, middle), -1f, 1f)));
            var inside = new List<int>();
            for (var c = 0; c < craters.Length; c++)
                if (Vector3.Dot(middle, craters[c].Centre) >= MathF.Cos(MathF.Min(angles[c] + size * 1.01f, MathF.PI)))
                    inside.Add(c);
            bins[cell] = [.. inside];
        });
        return bins;
    }

    /// <summary>Metres along the ground between two unit directions.</summary>
    internal static float Arc(Vector3 a, Vector3 b) => Radius * MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b));

    /// <summary>The compass bearing (degrees, 0 north, 90 east, in [0, 360)) from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static float Bearing(Vector3 from, Vector3 to)
    {
        var (east, north) = Across(from);
        var bearing = MathF.Atan2(Vector3.Dot(to, east), Vector3.Dot(to, north)) * 180f / MathF.PI;
        return bearing < 0f ? bearing + 360f : bearing;
    }

    /// <summary>The direction <paramref name="metres"/> along the ground from <paramref name="from"/> on a compass bearing (degrees).</summary>
    internal static Vector3 Out(Vector3 from, float bearing, float metres)
    {
        var (east, north) = Across(from);
        var b = bearing * MathF.PI / 180f;
        var angle = metres / Radius;
        return Vector3.Normalize(from * MathF.Cos(angle) + (north * MathF.Cos(b) + east * MathF.Sin(b)) * MathF.Sin(angle));
    }

    /// <summary>East and north along the ground at <paramref name="at"/> (not a pole), the moon's north being (0, 0, -1) as the planet's is.</summary>
    internal static (Vector3 East, Vector3 North) Across(Vector3 at)
    {
        var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, -1), at));
        return (east, Vector3.Cross(at, east));
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
