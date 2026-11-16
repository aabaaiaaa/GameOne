using System.Numerics;

namespace GameOne.Rendering;

/// <summary>
/// The sea's colours as a map: each place's shallow and deep colours (each sea's
/// <see cref="SeaInfo.Shallow"/> and <see cref="SeaInfo.Deep"/>), blended across the borders between
/// seas by how far each sea is (<see cref="Scene.SeaColorBlend"/>), so two seas meet over about
/// 2.2 km (from 10% to 90% of the one's, 1.1 km either side) rather than on the line where the map's regions change (which shows
/// from afar as straight-edged wedges of colour). R16G16B16A16_SFLOAT, twelve layers (the faces'
/// shallow colours, then their deep ones), with a mip chain (<see cref="FaceLevels"/>) averaged in
/// linear light, read filtered by the water's <c>waterColor</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each sea's weight is <c>exp(-d / Scene.SeaColorBlend)</c>, d its distance through the water
/// (the sea's own water at 0; another's from the border between their cells), as the generator blends the seas' depths: its weights are by the
/// difference of two seas' distances from their seeds, which changes twice as fast as the distance
/// to the border, over its 1 km. The distances are worked out on a coarser grid than the map
/// (<see cref="Coarser"/> levels: 61 m texels), which the blend's width is many of, by a
/// shortest-path search out from each sea through the cells with any of the seas' water in them,
/// eight neighbours a cell (across the faces' edges too), as far as a weight still counts
/// (<see cref="Reach"/> blends' widths). So the distances are along the water, not across land
/// (the generator's are too), and a little longer than straight ones at slants between the
/// grid's eight ways (at most 8%).
/// </para>
/// <para>
/// The map is at that coarser grid's size too: the colours change over hundreds of metres, which
/// its texels are a few of, so read magnified they blend as well as finer ones would. Cells with
/// no sea in them (land, and water below the sea level inland, which counts no sea: the map's
/// region 0) take the colours of the sea cells nearest them, so a filtered read at a shore blends
/// only the sea's colours, as the water keeps its sea's colour right up to the shore.
/// </para>
/// </remarks>
internal sealed class SeaColors
{
    private SeaColors(int side, float[][] levels)
    {
        Side = side;
        _levels = levels;
    }

    /// <summary>Each level's six faces, each texel its shallow then its deep colour, linear light.</summary>
    private readonly float[][] _levels;

    /// <summary>How many levels coarser than the map the colours are kept at: 1024 texels a face down to 256, 61 m each.</summary>
    public const int Coarser = 2;

    /// <summary>How many blend widths out a sea's weight still counts (e^-8, under a ten-thousandth of a colour's difference).</summary>
    public const float Reach = 8f;

    /// <summary>The base level's side, in texels.</summary>
    public int Side { get; }

    /// <summary>How many levels, down to 1 by 1.</summary>
    public int Levels => _levels.Length;

    /// <summary>The planet's sea colours from <paramref name="map"/>, whose resolution must be a power of two.</summary>
    public static SeaColors Build(TerrainMap map)
    {
        const string what = "the sea's colour map";
        FaceLevels.Count(map.Resolution, what);
        var seas = map.Seas.Count;
        if (seas > 16)
            throw new InvalidDataException($"the sea's colour map keeps at most 16 seas a cell, not {seas}");
        var block = 1 << Coarser;
        var side = map.Resolution >> Coarser;
        var count = 6 * side * side;

        // Which seas each cell has water of (a bit a sea), from the map's regions under it.
        var mapSide = map.Resolution + 2;
        var present = new ushort[count];
        Parallel.For(0, count, c =>
        {
            int face = c / (side * side), j = c / side % side, i = c % side;
            ushort mask = 0;
            for (var y = 0; y < block; y++)
                for (var x = 0; x < block; x++)
                {
                    var region = map.Regions[(face * mapSide + j * block + y + 1) * mapSide + i * block + x + 1];
                    if (region != 0) mask |= (ushort)(1 << (region - 1));
                }
            present[c] = mask;
        });

        var (centres, neighbours) = Grid(side);

        // Each sea's distance to every sea cell within reach, out through the sea cells.
        var reach = Reach * Scene.SeaColorBlend;
        var distances = new float[seas][];
        Parallel.For(0, seas, s =>
        {
            var d = new float[count];
            Array.Fill(d, float.PositiveInfinity);
            var queue = new PriorityQueue<int, float>();
            var bit = 1 << s;
            for (var c = 0; c < count; c++)
                if ((present[c] & bit) != 0)
                {
                    d[c] = 0f;
                    // Only its cells by another sea's (or land's) start the search: inside, nothing is nearer.
                    for (var k = 0; k < 8; k++)
                        if ((present[neighbours[8 * c + k]] & bit) == 0)
                        {
                            queue.Enqueue(c, 0f);
                            break;
                        }
                }
            while (queue.TryDequeue(out var c, out var at))
            {
                if (at > d[c]) continue;
                for (var k = 0; k < 8; k++)
                {
                    var n = neighbours[8 * c + k];
                    if (present[n] == 0) continue; // through the water only
                    // Out of the sea's own cells, half a step: the border lies between the two cells, so
                    // the cells either side of it are as far from it, and their colours differ by
                    // as little as any two cells' further out.
                    var step = GroundBody.PlanetSeaRadius * Arc(centres[c], centres[n]);
                    var further = at + (at == 0f && (present[c] & bit) != 0 ? 0.5f * step : step);
                    if (further < d[n] && further <= reach)
                    {
                        d[n] = further;
                        queue.Enqueue(n, further);
                    }
                }
            }
            distances[s] = d;
        });

        // Each sea cell's colours, blended by the seas' weights; then the cells with none, from the
        // filled cells round them, ring by ring out from the water.
        var shallow = map.Seas.Select(s => GroundColors.ToLinear(s.Shallow)).ToArray();
        var deep = map.Seas.Select(s => GroundColors.ToLinear(s.Deep)).ToArray();
        var colors = new Vector3[2 * count];
        var filled = new bool[count];
        Parallel.For(0, count, c =>
        {
            if (present[c] == 0) return;
            Vector3 a = Vector3.Zero, b = Vector3.Zero;
            var total = 0f;
            for (var s = 0; s < seas; s++)
            {
                if (!float.IsFinite(distances[s][c])) continue;
                var w = MathF.Exp(-distances[s][c] / Scene.SeaColorBlend);
                a += shallow[s] * w;
                b += deep[s] * w;
                total += w;
            }
            // Never 0: a cell with water has a sea of its own at distance 0, as every region is one
            // of the map's seas (TerrainMap refuses a map with any other).
            colors[2 * c] = a / total;
            colors[2 * c + 1] = b / total;
            filled[c] = true;
        });
        Fill(colors, filled, neighbours);

        var levels = FaceLevels.Build(side, 6, (face, into) =>
        {
            for (var t = 0; t < side * side; t++)
            {
                var c = face * side * side + t;
                var (a, b) = (colors[2 * c], colors[2 * c + 1]);
                (into[6 * t], into[6 * t + 1], into[6 * t + 2]) = (a.X, a.Y, a.Z);
                (into[6 * t + 3], into[6 * t + 4], into[6 * t + 5]) = (b.X, b.Y, b.Z);
            }
        }, what);
        return new SeaColors(side, levels);
    }

    /// <summary>A cell's shallow and deep colours at a level, as display values (as the GPU holds them, to a half float's precision).</summary>
    public (Vector3 Shallow, Vector3 Deep) Texel(int level, int face, int i, int j)
    {
        var side = Side >> level;
        var at = 6 * ((face * side + j) * side + i);
        var l = _levels[level];
        return (Held(new Vector3(l[at], l[at + 1], l[at + 2])), Held(new Vector3(l[at + 3], l[at + 4], l[at + 5])));
    }

    /// <summary>
    /// The colours at a direction, as the base level's filtered read (the texels' centres at
    /// (i + 0.5) / side, clamped at the face's edge, blended bilinearly in display values).
    /// </summary>
    public (Vector3 Shallow, Vector3 Deep) At(Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        var (i0, i1, j0, j1, x, y) = FaceLevels.Bilinear(u, v, Side);
        var (s00, d00) = Texel(0, face, i0, j0);
        var (s10, d10) = Texel(0, face, i1, j0);
        var (s01, d01) = Texel(0, face, i0, j1);
        var (s11, d11) = Texel(0, face, i1, j1);
        return (FaceLevels.Mix(s00, s10, s01, s11, x, y), FaceLevels.Mix(d00, d10, d01, d11, x, y));
    }

    /// <summary>
    /// Every level, base first, each its twelve layers in turn (the six faces' shallow colours, then
    /// their deep ones), row (v) after row, RGBA a texel as half floats, display values: as the upload copies it.
    /// </summary>
    public byte[] Pixels()
    {
        var bytes = new byte[_levels.Sum(l => l.Length / 6 * 2 * 4 * 2)];
        var at = 0;
        for (var k = 0; k < Levels; k++)
        {
            var side = Side >> k;
            var l = _levels[k];
            for (var colour = 0; colour < 2; colour++)
                for (var face = 0; face < 6; face++)
                    for (var t = 0; t < side * side; t++)
                    {
                        var from = 6 * (face * side * side + t) + 3 * colour;
                        var c = GroundColors.ToDisplay(new Vector3(l[from], l[from + 1], l[from + 2]));
                        foreach (var value in (ReadOnlySpan<float>)[c.X, c.Y, c.Z, 1f])
                        {
                            BitConverter.TryWriteBytes(bytes.AsSpan(at), (Half)value);
                            at += 2;
                        }
                    }
        }
        return bytes;
    }

    private static Vector3 Held(Vector3 linear)
    {
        var c = GroundColors.ToDisplay(linear);
        return new Vector3((float)(Half)c.X, (float)(Half)c.Y, (float)(Half)c.Z);
    }

    /// <summary>The angle between two unit directions, in radians.</summary>
    private static float Arc(Vector3 a, Vector3 b) => 2f * MathF.Asin(Math.Min(Vector3.Distance(a, b) * 0.5f, 1f));

    /// <summary>
    /// The cells of a grid <paramref name="side"/> a face: each one's centre's direction, and its
    /// eight neighbours (a cell index each, in a cell's 8 slots), across the faces' edges where it
    /// is at one (the cell the direction just over the edge falls in).
    /// </summary>
    private static (Vector3[] Centres, int[] Neighbours) Grid(int side)
    {
        var count = 6 * side * side;
        var centres = new Vector3[count];
        var neighbours = new int[8 * count];
        float Coordinate(int i) => (i + 0.5f) / side * 2f - 1f;
        Parallel.For(0, count, c =>
        {
            int face = c / (side * side), j = c / side % side, i = c % side;
            centres[c] = CubeSphere.FromFace(face, Coordinate(i), Coordinate(j));
            var k = 0;
            for (var dj = -1; dj <= 1; dj++)
                for (var di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int a = i + di, b = j + dj;
                    if (a >= 0 && a < side && b >= 0 && b < side)
                        neighbours[8 * c + k++] = (face * side + b) * side + a;
                    else
                    {
                        var (f, u, v) = CubeSphere.ToFace(CubeSphere.FromFace(face, Coordinate(a), Coordinate(b)));
                        int x = Math.Clamp((int)((u + 1f) * 0.5f * side), 0, side - 1), y = Math.Clamp((int)((v + 1f) * 0.5f * side), 0, side - 1);
                        neighbours[8 * c + k++] = (f * side + y) * side + x;
                    }
                }
        });
        return (centres, neighbours);
    }

    /// <summary>The cells not yet filled take the mean of their filled neighbours, ring by ring out from those filled.</summary>
    private static void Fill(Vector3[] colors, bool[] filled, int[] neighbours)
    {
        var count = filled.Length;
        var ring = new List<int>();
        for (var c = 0; c < count; c++)
            if (!filled[c] && Enumerable.Range(0, 8).Any(k => filled[neighbours[8 * c + k]]))
                ring.Add(c);
        while (ring.Count > 0)
        {
            var values = new (Vector3, Vector3)[ring.Count];
            for (var r = 0; r < ring.Count; r++)
            {
                var c = ring[r];
                Vector3 a = Vector3.Zero, b = Vector3.Zero;
                var n = 0;
                for (var k = 0; k < 8; k++)
                {
                    var m = neighbours[8 * c + k];
                    if (!filled[m]) continue;
                    a += colors[2 * m];
                    b += colors[2 * m + 1];
                    n++;
                }
                values[r] = (a / n, b / n);
            }
            var next = new HashSet<int>();
            for (var r = 0; r < ring.Count; r++)
            {
                var c = ring[r];
                (colors[2 * c], colors[2 * c + 1]) = values[r];
                filled[c] = true;
            }
            foreach (var c in ring)
                for (var k = 0; k < 8; k++)
                {
                    var m = neighbours[8 * c + k];
                    if (!filled[m]) next.Add(m);
                }
            ring = [.. next];
        }
    }
}
