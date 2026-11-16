using System.Diagnostics;
using System.Numerics;
using GameOne;

namespace TerrainGen;

/// <summary>
/// A place levelled flat: inside <c>Radius</c>, blending back to the terrain over <c>Blend</c>. At
/// elevation 0 (Home's pad, its landing zone, the test pads), or with <c>LevelTo</c>, at the height
/// the land had there before levelling (a launch site's pad and its landing zone, at the pad's). With
/// <c>Calm</c>, the coasts and hills calm down round it too (see <c>Calm()</c>); without, only the site
/// itself is levelled, and the country round it stays as it is. With a <c>Footprint</c> (metres
/// round its centre: what stands on it), it is levelled at the highest ground under that, and only
/// ever lowered to it, never raised: the far sites' pads and zones, which stand on foundations
/// reaching down to wherever the ground falls away.
/// </summary>
internal sealed record Site(string Name, Vector3 Direction, float Radius, float Blend, Vector3? LevelTo = null, bool Calm = true, float Footprint = 0f);

/// <summary>
/// Makes the map from the layout: the land and seas from the outlines (coasts roughened), the
/// land's rise, hills, plateaus and ranges, the seabed's shelves and basins; then erosion over the
/// whole sphere; then the icy seas' ice; then the sites levelled, and each texel's kind. Every
/// step is per texel in parallel or a fixed-order sweep, so a layout always gives the same map.
/// </summary>
internal sealed class Generator(Layout layout, IReadOnlyList<Site> sites, IReadOnlyList<Vector3> droneShips, float radius, Action<string> log)
{
    // The generator works on heights above Home's pad (its layout's numbers are), with the sea 35 m
    // below; the map it writes is above the sea (see Assemble), as every height in the game is.
    private const float SeaLevel = -Scene.HomeElevation;
    private const float MinLand = SeaLevel + 0.5f;      // land stays above the sea, so the coast is where the layout put it
    private const float MaxWater = SeaLevel - 1.5f;     // and the seabed below it
    // 1,590 m above the sea (on the generator's scale, less 35 m): under the 1,600 m above the
    // sea MapLayoutTests holds peaks to.
    private const float PeakLimit = 1590f + SeaLevel;
    private const float SlopeWidth = 1500f;             // metres from a shelf's edge down to its basin's depth
    private const float SeaBlend = 1000f;               // metres over which neighbouring seas' depths blend
    private const float IceWander = 0.25f;              // the share of its pack the sheet's edge wanders in and out
    private const float IceWavelength = 350f;           // metres along the edge: the size of its largest bights and tongues
    private const float IceFine = 14f;                  // metres the edge is broken in and out by, finer than the bights ...
    private const float IceFineWavelength = 70f;        // ... at about this size along it (and half that)
    private const float IceFootSlope = 0.3f;            // how steeply the sheet's foot slopes down under the water past its edge (metres a metre) ...
    private const float IceFootWidth = 20f;             // ... for this far out past it (6 m down: the waves are whole at 4 m), ...
    private const float IceFootDrop = 2f;               // ... and then how steeply its face drops to the seabed

    /// <summary>What a texel is in an icy sea: open water (or land), the frozen sheet, or the band of floes round it.</summary>
    private enum Ice : byte { None, Sheet, Floes }

    private readonly Noise _noise = new(layout.Seed);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Grid _grid = null!;
    private bool[] _water = null!;
    private float[] _coast = null!;     // metres to the nearest water (on land) or coast (at sea)
    private float[] _calm = null!;      // metres past the nearest site's edge
    private byte[] _region = null!;
    private float[] _depth = null!, _shelf = null!, _banks = null!;
    private float[] _height = null!;
    private float[] _drainage = null!;
    private byte[] _kind = null!;
    private Ice[] _ice = null!;
    private float[] _iceEdge = null!; // metres out past the sheet's edge (negative: in from it), near the icy seas; NaN elsewhere

    private void Step(string what) => log($"{_clock.Elapsed.TotalSeconds,6:F1}s  {what}");

    private Vector3 Metres(int i) => _grid.Directions[i] * radius;

    public TerrainMap Run()
    {
        _grid = new Grid(layout.Resolution, radius);
        Step($"grid: {layout.Resolution} a face, {_grid.Count:N0} texels");
        Calm();
        LandAndSea();
        Step($"land and sea: {_water.Count(w => !w) * 100.0 / _grid.Count:F0}% of texels land");
        Seas();
        Step("seas");
        Shape();
        Step("base elevation");
        Erode();
        Step("eroded");
        Freeze();
        Step("sea ice");
        Finish();
        Edges();
        Kinds();
        Step("kinds");
        return Assemble();
    }

    /// <summary>Metres from each texel to the nearest site's edge (its radius plus blend), so hills and wandering coasts calm down round the sites.</summary>
    private void Calm()
    {
        _calm = new float[_grid.Count];
        Parallel.For(0, _grid.Count, i =>
        {
            var gap = float.MaxValue;
            foreach (var s in sites.Where(s => s.Calm))
                gap = MathF.Min(gap, _grid.Arc(_grid.Directions[i], s.Direction) - s.Radius - s.Blend);
            _calm[i] = MathF.Max(gap, 0f);
        });
    }

    /// <summary>The land mask from the outlines (read through a warp, so coasts get bays and headlands), and the distance to the coast.</summary>
    private void LandAndSea()
    {
        var outlines = layout.Land.Select(l => new Outline(l)).ToArray();
        var c = layout.Coast;
        var land = new bool[_grid.Count];
        Parallel.For(0, _grid.Count, i =>
        {
            var p = Metres(i);
            var calm = Smooth(0f, c.CalmRadius, _calm[i]);
            var warp = _noise.Warp(0, p / c.WarpWavelength, 4) * (c.Warp * calm) +
                       _noise.Warp(3, p / (c.WarpWavelength * 0.23f), 3) * (c.Warp * 0.25f * calm);
            var (lat, lon) = CubeSphere.LatLon(Vector3.Normalize(p + warp));
            lat *= 180f / MathF.PI;
            lon *= 180f / MathF.PI;
            foreach (var o in outlines)
            {
                if (!o.Contains(lat, lon)) continue;
                land[i] = !land[i]; // overlapping outlines cancel, so one can cut a lake into another
            }
        });

        // Then the coast is moved in and out by fractal noise on the distance to it: bays, coves,
        // headlands and offshore islets at every scale, as a real coast has, not a smooth outline.
        _water = land.Select(l => !l).ToArray();
        var edge = Enumerable.Range(0, _grid.Count).Where(i => Touches(i, w => w != _water[i])).ToArray();
        var (fromEdge, _) = _grid.DistanceTo(edge);
        Parallel.For(0, _grid.Count, i =>
        {
            var signed = _water[i] ? -fromEdge[i] : fromEdge[i];
            if (MathF.Abs(signed) > c.Roughness * 2.5f) return;
            var calm = Smooth(0f, c.CalmRadius, _calm[i]);
            land[i] = signed + c.Roughness * calm * _noise.Fbm(33, Metres(i) / c.RoughWavelength, 6, 0.55f) > 0f;
        });

        foreach (var s in sites)
            if (!land[_grid.Nearest(s.Direction)]) throw new InvalidDataException($"the site {s.Name} is at sea");

        _water = land.Select(l => !l).ToArray();
        var shore = Enumerable.Range(0, _grid.Count).Where(i => _water[i] && Touches(i, w => !w)).ToArray();
        (_coast, _) = _grid.DistanceTo(shore);
    }

    private bool Touches(int i, Func<bool, bool> water)
    {
        for (var k = 0; k < 8; k++)
            if (water(_water[_grid.Neighbour(i, k)])) return true;
        return false;
    }

    /// <summary>
    /// Which sea each water texel is in (the nearest seed joined to it by water), and its depth,
    /// shelf and banks, blended where seas meet so their basins don't step.
    /// </summary>
    private void Seas()
    {
        var seeds = layout.Seas.Select(s =>
        {
            var at = _grid.Nearest(Degrees.Direction(s.At));
            if (!_water[at]) throw new InvalidDataException($"{s.Name}'s point [{s.At[0]}, {s.At[1]}] is on land");
            return at;
        }).ToArray();
        var byWater = new float[seeds.Length][];
        Parallel.For(0, seeds.Length, s => byWater[s] = _grid.PathDistance(seeds[s], n => _water[n]));

        _region = new byte[_grid.Count];
        _depth = new float[_grid.Count];
        _shelf = new float[_grid.Count];
        _banks = new float[_grid.Count];
        var stranded = 0;
        Parallel.For(0, _grid.Count, () => 0, (i, _, cut) =>
        {
            if (!_water[i]) return cut;
            var best = -1;
            for (var s = 0; s < seeds.Length; s++)
                if (float.IsFinite(byWater[s][i]) && (best < 0 || byWater[s][i] < byWater[best][i])) best = s;

            if (best < 0)
            {
                // Water no seed reaches (a lake inside the land): the nearest sea by straight distance.
                cut++;
                best = 0;
                for (var s = 1; s < seeds.Length; s++)
                    if (Vector3.Dot(_grid.Directions[i], _grid.Directions[seeds[s]]) > Vector3.Dot(_grid.Directions[i], _grid.Directions[seeds[best]]))
                        best = s;
                Set(i, best, [(best, 1f)]);
                return cut;
            }

            var weights = new List<(int, float)>();
            for (var s = 0; s < seeds.Length; s++)
                if (float.IsFinite(byWater[s][i]))
                    weights.Add((s, MathF.Exp(-(byWater[s][i] - byWater[best][i]) / SeaBlend)));
            Set(i, best, weights);
            return cut;
        }, cut => Interlocked.Add(ref stranded, cut));
        if (stranded > 0) log($"  {stranded:N0} water texels joined to no sea seed: given the nearest sea");

        void Set(int i, int region, List<(int Sea, float Weight)> weights)
        {
            _region[i] = (byte)(region + 1);
            float total = 0f, depth = 0f, shelf = 0f, banks = 0f;
            foreach (var (s, w) in weights)
            {
                total += w;
                depth += layout.Seas[s].Depth * w;
                shelf += layout.Seas[s].Shelf * w;
                banks += layout.Seas[s].Banks * w;
            }

            _depth[i] = depth / total;
            _shelf[i] = shelf / total;
            _banks[i] = banks / total;
        }
    }

    /// <summary>
    /// Erosion, coarse to fine: each level erodes the base elevation averaged down to its own
    /// resolution, plus the valleys the coarser levels cut, so the big valleys come from the coarse
    /// levels (broad, as their texels are) and each finer level adds the side valleys and gullies.
    /// </summary>
    private void Erode()
    {
        var iterations = layout.Erosion.Iterations;
        var n = _grid.N;
        if (iterations.Length == 0 || n % (1 << (iterations.Length - 1)) != 0)
            throw new InvalidDataException($"the resolution must halve evenly {iterations.Length - 1} times for the erosion levels");

        Grid? coarser = null;
        float[]? carved = null;
        for (var level = 0; level < iterations.Length; level++)
        {
            var grid = level == iterations.Length - 1 ? _grid : new Grid(n >> (iterations.Length - 1 - level), radius);
            var block = n / grid.N;
            var shape = new float[grid.Count];
            var water = new bool[grid.Count];
            Parallel.For(0, grid.Count, c =>
            {
                var face = c / (grid.N * grid.N);
                var j = c / grid.N % grid.N;
                var i = c % grid.N;
                float sum = 0f, wet = 0f;
                for (var dj = 0; dj < block; dj++)
                for (var di = 0; di < block; di++)
                {
                    var at = _grid.Index(face, i * block + di, j * block + dj);
                    sum += _height[at];
                    if (_water[at]) wet++;
                }

                shape[c] = sum / (block * block);
                water[c] = wet * 2f > block * block;
            });

            var height = (float[])shape.Clone();
            if (carved != null)
            {
                Parallel.For(0, grid.Count, c =>
                {
                    if (water[c]) return;
                    var (face, u, v) = CubeSphere.ToFace(grid.Directions[c]);
                    height[c] = MathF.Max(height[c] + coarser!.Sample(carved, face, u, v), MinLand);
                });
            }

            // Finer levels cut less and soften more: they add side valleys and gullies to the coarse
            // levels' valleys, not slots of their own.
            var incision = layout.Erosion.Incision * MathF.Pow(0.55f, level);
            var diffusion = MathF.Min(layout.Erosion.Diffusion * MathF.Pow(2f, level), 0.2f);
            _drainage = Erosion.Run(grid, height, water, SeaLevel, MinLand, layout.Erosion, iterations[level], incision, diffusion, s => Step(s));
            carved = new float[grid.Count];
            for (var c = 0; c < grid.Count; c++) carved[c] = water[c] ? 0f : height[c] - shape[c];
            coarser = grid;
            if (grid == _grid) _height = height;
        }
    }

    /// <summary>The elevation before erosion.</summary>
    private void Shape()
    {
        var crests = layout.Ranges.Select(r => new Crest(r, radius)).ToArray();
        _height = new float[_grid.Count];
        Parallel.For(0, _grid.Count, i => _height[i] = _water[i] ? Seabed(i) : Land(i, crests));
    }

    private float Land(int i, Crest[] crests)
    {
        var p = Metres(i);
        var d = _coast[i];
        var r = layout.Relief;
        var c = layout.Coast;
        var coastCalm = Smooth(0f, c.CalmRadius, _calm[i]);
        var hillCalm = Smooth(0f, r.CalmRadius, _calm[i]);

        // The shore: a rise from the beach inland, steeper (a cliff) along some stretches.
        var rise = r.CoastRise * (0.65f + 0.35f * _noise.Fbm(7, p / 4000f, 2)) * (1f - MathF.Exp(-d / r.CoastRiseLength));
        var cliffShare = Smooth(0.45f - 0.9f * c.Cliffs, 0.55f - 0.9f * c.Cliffs, _noise.Fbm(6, p / 3000f, 3)) * coastCalm;
        var cliff = cliffShare * c.CliffHeight * (0.7f + 0.3f * _noise.Fbm(32, p / 900f, 2)) * Smooth(0f, 70f, d);

        // Hills and plateaus, inland, read through a warp so they wander rather than sit on a lattice.
        var wp = p + _noise.Warp(8, p / (r.HillWavelength * 1.6f), 3) * (r.HillWavelength * 0.35f);
        var inland = Smooth(150f, 2500f, d) * hillCalm;
        var hills = r.Hills * (0.5f + 0.5f * _noise.Fbm(11, wp / r.HillWavelength, 5)) * inland;
        var plateau = r.Plateaus * Smooth(0.15f, 0.4f, _noise.Fbm(12, wp / r.PlateauWavelength, 3)) * inland;

        // Ranges: a ridge along each crest line (itself wandering), its height varying into peaks
        // and saddles along it, falling to the sea over at least a kilometre.
        var mountains = 0f;
        foreach (var crest in crests)
        {
            var w = crest.Range.Width;
            var cp = p + _noise.Warp(15, p / (w * 2.5f), 3) * (w * 0.45f);
            if (crest.Nearest(Vector3.Normalize(cp), radius) is not { } near) continue;
            var (angle, along) = near;
            var s = angle * radius;
            var width = w * (0.8f + 0.25f * _noise.Fbm(34, cp / (w * 2f), 2));
            if (s >= width) continue;
            var q = 1f - s / width;
            var profile = 0.5f * q * q * (3f - 2f * q) + 0.5f * q * q;
            var ends = 0.55f + 0.45f * Smooth(0f, w, along) * Smooth(0f, w, crest.Length - along);
            var peaks = Math.Clamp(0.82f + 0.25f * _noise.Fbm(18, cp / (w * 1.2f), 3), 0.55f, 1f);
            // Spurs: side ridges branching down the flanks, so water off them gathers and spreads
            // as it does on a real range rather than running straight down a plane.
            var spurs = _noise.Ridged(19, (cp + _noise.Warp(35, cp / w, 2) * (w * 0.3f)) / (w * 0.6f), 4);
            var flank = 4f * q * (1f - q);
            mountains = MathF.Max(mountains, crest.Range.Height * ends * peaks * (0.78f * profile + 0.32f * spurs * flank));
        }

        mountains *= Smooth(0f, 1200f, d);

        var relief = hills + plateau + mountains;
        // Roughness for the water to organise into drainage: in proportion to the relief, plus a little everywhere.
        var rough = r.Roughness * relief * _noise.Fbm(13, wp / 1500f, 5) + 1.5f * _noise.Fbm(14, p / 220f, 3);
        return MathF.Max(MinLand + rise + cliff + relief + rough, MinLand);
    }

    private float Seabed(int i)
    {
        var p = Metres(i);
        var d = _coast[i];
        // Read the distance from the coast unevenly, so shelves and slopes wander rather than ring the land.
        d *= MathF.Max(1f + 0.45f * _noise.Fbm(36, p / 2500f, 3), 0.3f);
        var depth = _depth[i] * (1f + 0.2f * _noise.Fbm(20, p / 5000f, 3));
        var shelf = _shelf[i] * MathF.Max(0.3f, 1f + 0.6f * _noise.Fbm(29, p / 6000f, 3));
        var shelfDepth = MathF.Min(24f, depth * 0.5f) * (1f + 0.3f * _noise.Fbm(30, p / 3000f, 2));
        float floor;
        if (d < shelf)
            floor = SeaLevel - 1.5f - (shelfDepth - 1.5f) * MathF.Pow(d / shelf, 0.8f);
        else
            floor = SeaLevel - shelfDepth - (depth - shelfDepth) * Smooth(shelf, shelf + SlopeWidth, d);

        floor += 3f * _noise.Fbm(21, p / 800f, 3) * Smooth(0f, 300f, d);
        // Banks: broad shallows out in the basin.
        var bank = _banks[i] * Smooth(0.2f, 0.5f, _noise.Fbm(22, p / 3500f, 4)) * Smooth(shelf * 0.5f, shelf * 1.5f, d);
        floor += (SeaLevel - 7f - 6f * _noise.Fbm(23, p / 1200f, 2) - floor) * bank;
        return MathF.Min(floor, MaxWater);
    }

    /// <summary>
    /// The icy seas' ice, round each one's centre (its <c>at</c>): out to the sheet's edge, the water
    /// is frozen solid: ground <see cref="Scene.PackIceFreeboard"/> above sea level, with no seabed
    /// under it (nothing can reach one: the ice never breaks), and land in every way (no sea's
    /// region, so no waves or water there); then the band of floes, still water over the seabed,
    /// marked so the game damps the waves there. The sheet's edge is <c>pack</c> out on each
    /// bearing from the centre, moved in and out by up to a quarter of that by noise along it, so
    /// it has bights and tongues rather than being a circle, and broken by finer noise (some
    /// <see cref="IceFine"/> metres in and out) so it is ragged along them; the band is the next
    /// <c>floes</c> out on the same bearing. How far each texel near it is out past the edge is
    /// kept (<c>_iceEdge</c>), for <see cref="Edges"/>.
    /// </summary>
    private void Freeze()
    {
        _ice = new Ice[_grid.Count];
        _iceEdge = new float[_grid.Count];
        Array.Fill(_iceEdge, float.NaN);
        foreach (var sea in layout.Seas)
        {
            if (sea.Ice is not { } ice) continue;
            var centre = Degrees.Direction(sea.At);
            var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, -1), centre)); // as Planet.East
            var north = Vector3.Cross(centre, east);

            // The edge's distance on a bearing (radians clockwise from north): the noise is read
            // where the edge would be with no wander, so it varies along the edge at its own size.
            var circle = ice.Pack / radius;
            float Edge(float bearing)
            {
                var q = centre * MathF.Cos(circle) + (north * MathF.Cos(bearing) + east * MathF.Sin(bearing)) * MathF.Sin(circle);
                return ice.Pack * (1f + IceWander * Math.Clamp(_noise.Fbm(40, q * radius / IceWavelength, 4), -1f, 1f))
                    + IceFine * Math.Clamp(_noise.Fbm(41, q * radius / IceFineWavelength, 2), -1f, 1f);
            }

            var reach = ice.Pack * (1f + IceWander) + IceFine + ice.Floes;
            int sheet = 0, floes = 0;
            Parallel.For(0, _grid.Count, () => (0, 0), (i, _, counts) =>
            {
                var d = _grid.Directions[i];
                if (!_water[i] || _ice[i] != Ice.None) return counts;
                var s = _grid.Arc(d, centre);
                if (s > reach) return counts;
                var across = d - centre * Vector3.Dot(d, centre);
                var edge = Edge(MathF.Atan2(Vector3.Dot(across, east), Vector3.Dot(across, north)));
                _iceEdge[i] = s - edge;
                if (s < edge)
                {
                    _ice[i] = Ice.Sheet;
                    _height[i] = SeaLevel + Scene.PackIceFreeboard;
                    _water[i] = false;
                    _region[i] = 0;
                    return (counts.Item1 + 1, counts.Item2);
                }

                if (s >= edge + ice.Floes) return counts;
                _ice[i] = Ice.Floes;
                return (counts.Item1, counts.Item2 + 1);
            }, counts =>
            {
                Interlocked.Add(ref sheet, counts.Item1);
                Interlocked.Add(ref floes, counts.Item2);
            });

            var edges = Enumerable.Range(0, 720).Select(b => Edge(b * MathF.PI / 360f)).ToArray();
            log($"  {sea.Name}: the sheet reaches {edges.Min():F0} to {edges.Max():F0} m (pack {ice.Pack:F0}), {sheet:N0} texels; " +
                $"the floes {ice.Floes:F0} m on, {floes:N0} texels");
        }
    }

    /// <summary>
    /// The sheets' edges where the curve is, not where the texels are. A sheet texel stands at
    /// <see cref="Scene.PackIceFreeboard"/> and the water beside it metres down, so the game's
    /// heights, read bilinearly between texel centres, would cross the sea level close to the
    /// sheet texels' centres: the waterline would run in straight chords from texel to texel, a
    /// tooth a texel, wherever the edge crosses the grid at a slant. So each water texel beside
    /// the sheet is lifted until, between it and each sheet texel next to it, the heights cross
    /// the sea level where the edge's curve does: a fraction (how far in the sheet texel is) of
    /// (how far in plus how far out this one is) of the way across. With h for the sheet's
    /// freeboard, that is h times how far out over how far in below the sea, the mean of what
    /// each sheet neighbour asks, never deeper than the seabed was and always just under the
    /// sea. So the waterline follows the edge's noise below a texel's size. Past it the ice goes
    /// on under the water as a foot sloping down (<see cref="IceFootSlope"/>, for
    /// <see cref="IceFootWidth"/>), its face then dropping to the seabed (<see cref="IceFootDrop"/>,
    /// no steeper than the map's steep slopes), rather than a cliff on the texel grid: and the
    /// foot gently enough that the waves, which die away in the shallows
    /// (<see cref="Scene.SeaWaveFullDepth"/>), rise smoothly off it. Each
    /// sheet neighbour's ask is held to no steeper than that slope too, so where the edge's curve
    /// is right by a sheet texel's centre the waterline is up to a metre or so out from it.
    /// </summary>
    private void Edges()
    {
        var lifted = new float[_grid.Count];
        Array.Fill(lifted, float.NaN);
        // How far each texel near a sheet is from its nearest texel, less half a texel: about how
        // far out past the edge it is, measured across it (along the bearing, the edge's tongues
        // would make it change from texel to texel in steps), for the foot.
        var (near, _) = _grid.DistanceTo(Enumerable.Range(0, _grid.Count).Where(i => _ice[i] == Ice.Sheet), i => !float.IsNaN(_iceEdge[i]));
        var half = 0.5f * MathF.PI * radius / (2f * _grid.N);
        Parallel.For(0, _grid.Count, i =>
        {
            if (!_water[i] || float.IsNaN(_iceEdge[i])) return;
            var outside = MathF.Max(_iceEdge[i], 0f);
            var (sum, count) = (0f, 0);
            for (var k = 0; k < 8; k++)
            {
                var n = _grid.Neighbour(i, k);
                if (_ice[n] != Ice.Sheet) continue;
                var inside = MathF.Max(-_iceEdge[n], 0.05f);
                sum += MathF.Max(SeaLevel - Scene.PackIceFreeboard * outside / inside, SeaLevel - IceFootSlope * (outside + inside));
                count++;
            }
            var past = MathF.Max(near[i] - half, 0f);
            var foot = count > 0 ? sum / count : SeaLevel - IceFootSlope * MathF.Min(past, IceFootWidth) - IceFootDrop * MathF.Max(past - IceFootWidth, 0f);
            if (foot > _height[i])
                lifted[i] = MathF.Min(foot, SeaLevel - 0.02f);
        });
        var edges = 0;
        for (var i = 0; i < _grid.Count; i++)
            if (!float.IsNaN(lifted[i]))
            {
                _height[i] = lifted[i];
                edges++;
            }
        Step($"sheets' edges: {edges:N0} texels lifted to the curve and the ice's foot");
    }

    /// <summary>The highest ground (before any levelling) within <paramref name="metres"/> of <paramref name="centre"/>, across the ground.</summary>
    private float Highest(Vector3 centre, float metres)
    {
        var highest = float.MinValue;
        for (var i = 0; i < _grid.Count; i++)
            if (!_water[i] && _grid.Arc(_grid.Directions[i], centre) <= metres)
                highest = MathF.Max(highest, _height[i]);
        return highest > float.MinValue ? highest : _height[_grid.Nearest(centre)];
    }

    /// <summary>The sites levelled, and the peaks kept under the limit.</summary>
    private void Finish()
    {
        // The drone ships ride the sea there (from LaunchSites), so they have to be at sea, and in
        // water deep enough for their keels in the roughest troughs (Scene.DroneShipMinDepth).
        foreach (var ship in droneShips)
        {
            var at = _grid.Nearest(ship);
            if (!_water[at]) throw new InvalidDataException($"a drone ship's place ({ship}) is on land");
            if (SeaLevel - _height[at] < Scene.DroneShipMinDepth)
                throw new InvalidDataException($"a drone ship's place ({ship}) is in {SeaLevel - _height[at]:0.0} m of sea, under Scene.DroneShipMinDepth's {Scene.DroneShipMinDepth} m");
        }
        // Each site's level: 0, or the land's own height where it says, as it was before any levelling
        // (with a footprint, its highest under it).
        var levels = sites.Select(s => s.Footprint > 0f ? Highest(s.Direction, s.Footprint)
            : s.LevelTo is { } at ? _height[_grid.Nearest(at)] : 0f).ToArray();
        foreach (var (s, level) in sites.Zip(levels))
            if (s.LevelTo is not null || s.Footprint > 0f)
                log($"         {s.Name} levelled at {level + Scene.HomeElevation:0.0} m above the sea");
        Parallel.For(0, _grid.Count, i =>
        {
            if (_ice[i] == Ice.Sheet) return; // the sea ice stands where Freeze put it, not as land is shaped
            var dir = _grid.Directions[i];
            var h = _height[i];
            if (_water[i])
                h = MathF.Min(h, MaxWater);
            else
            {
                for (var k = 0; k < sites.Count; k++)
                {
                    var s = _grid.Arc(dir, sites[k].Direction);
                    var levelled = levels[k] + (h - levels[k]) * Smooth(sites[k].Radius, sites[k].Radius + sites[k].Blend, s);
                    h = sites[k].Footprint > 0f ? MathF.Min(h, levelled) : levelled; // a footprint's only ever lowers
                }

                // Peaks above the limit are rounded off rather than cut flat.
                const float knee = PeakLimit - 150f;
                if (h > knee) h = knee + 150f * MathF.Tanh((h - knee) / 150f);
                h = MathF.Max(h, MinLand);
            }

            _height[i] = h;
        });
    }

    /// <summary>
    /// What the ground is at each texel: the sea ice; sites; seabed (under the floes, their own
    /// kind, which damps the waves); rock where steep; snow towards the poles and
    /// on high peaks; beaches at the water's edge; deserts near the equator well inland; and grass
    /// where it is wetter (near the sea, in the valleys, away from the tropics), scrub elsewhere.
    /// </summary>
    private void Kinds()
    {
        _kind = new byte[_grid.Count];
        var rock = MathF.Tan(35f * MathF.PI / 180f);
        var desert = layout.Deserts;
        var snow = layout.Snow;
        Parallel.For(0, _grid.Count, i =>
        {
            SurfaceKind kind;
            var p = Metres(i);
            var wp = p + _noise.Warp(8, p / 3000f, 3) * 900f;
            var h = _height[i];
            var lat = MathF.Abs(CubeSphere.LatLon(_grid.Directions[i]).Latitude) * 180f / MathF.PI;
            if (_ice[i] == Ice.Sheet) kind = SurfaceKind.PackIce;
            else if (_water[i]) kind = _ice[i] == Ice.Floes ? SurfaceKind.FloeField : SurfaceKind.Seabed;
            else if (sites.Any(s => _grid.Arc(_grid.Directions[i], s.Direction) <= s.Radius)) kind = SurfaceKind.Site;
            else if (Slope(i) > rock) kind = SurfaceKind.Rock;
            else if (lat + 3f * _noise.Fbm(24, p / 3000f, 3) + MathF.Max(h, 0f) / 120f >= snow.MinLatitude ||
                     h >= snow.MinHeight + 60f * _noise.Fbm(24, p / 700f, 3))
                kind = SurfaceKind.Snow;
            else if (h < SeaLevel + 2.5f && _coast[i] < 120f && Slope(i) < 0.2f) kind = SurfaceKind.Beach;
            else if (lat + 9f * MathF.Max(0.5f + 0.5f * _noise.Fbm(25, wp / 3000f, 3), 0f) <= desert.MaxLatitude &&
                     _coast[i] >= desert.MinDistanceFromSea + 500f * MathF.Max(0.5f + 0.5f * _noise.Fbm(26, p / 2000f, 3), 0f) &&
                     h <= desert.MaxHeight + 80f * _noise.Fbm(26, p / 900f, 2) &&
                     _noise.Fbm(27, wp / 3500f, 4) > 0.9f * (0.5f - desert.Coverage))
                kind = SurfaceKind.Desert;
            else
            {
                var wet = 1f - Smooth(200f, 2500f, _coast[i] * (1f + 0.6f * _noise.Fbm(31, wp / 2000f, 3))) + lat / 45f + Smooth(2e4f, 3e5f, _drainage[i]) +
                          0.35f * _noise.Fbm(28, p / 2500f, 3);
                kind = wet > 0.75f ? SurfaceKind.Grass : SurfaceKind.Scrub;
            }

            _kind[i] = (byte)kind;
        });
    }

    /// <summary>The ground's slope at a texel (rise over run), from its four sides.</summary>
    private float Slope(int i)
    {
        var e = _grid.Neighbour(i, 0);
        var w = _grid.Neighbour(i, 1);
        var n = _grid.Neighbour(i, 2);
        var s = _grid.Neighbour(i, 3);
        var gu = (_height[e] - _height[w]) / _grid.Distance(e, w);
        var gv = (_height[n] - _height[s]) / _grid.Distance(n, s);
        return MathF.Sqrt(gu * gu + gv * gv);
    }

    /// <summary>The map: each face with its apron (the true surface just over the edge, read from the next face), heights quantised so 0 is exact.</summary>
    private TerrainMap Assemble()
    {
        var n = _grid.N;
        var (metres, kinds, regions) = Aprons.Add(_grid, _height, _kind, _region);
        var (heights, min, max) = Aprons.Quantise(metres);

        double water = 0.0, frozen = 0.0, total = 0.0;
        for (var i = 0; i < _grid.Count; i++)
        {
            total += _grid.Areas[i];
            if (_water[i]) water += _grid.Areas[i];
            if (_ice[i] == Ice.Sheet) frozen += _grid.Areas[i];
        }

        // Written above the sea: the same levels, every one 35 m higher.
        (min, max) = (min + Scene.HomeElevation, max + Scene.HomeElevation);
        Step($"assembled: {min:F1} to {max:F1} m above the sea, {water / total * 100:F0}% of the surface sea, {frozen / total * 100:F2}% frozen sea");
        return new TerrainMap(n, radius, Scene.HomeElevation, min, max, heights, kinds, regions,
            layout.Seas.Select(s => s.Info()).ToList());
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
