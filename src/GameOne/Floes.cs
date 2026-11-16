using System.Numerics;

namespace GameOne;

/// <summary>
/// The sea ice's floes: discs of ice over the icy seas' floe bands (<see cref="SurfaceKind.FloeField"/>),
/// each a <see cref="Raft"/> (see <see cref="Scene.FloeSpacing"/> and the rest for their numbers).
/// Laid out once, from the map, on a jittered grid: the same floes every run and in every world,
/// so none is saved. Which floes are near a point, and which one a point is over.
/// </summary>
/// <remarks>
/// <para>
/// The grid's cells are squares of a cube face (<see cref="CubeSphere"/>, equal-angle),
/// <see cref="Scene.FloeSpacing"/> across at a face's middle, keyed (face, i, j). Each cell has a
/// floe or none, its size and where it lies in the cell all from a hash of the cell, so a cell's
/// floe is worked out from nothing but the cell. A floe is kept <see cref="Gap"/> clear of its
/// cell's edges (each a great circle, so that is measured exactly), so it lies wholly in its cell
/// and no two overlap.
/// </para>
/// <para>
/// Both floe bands lie round a pole, in the middle of a cube face, far from its edges (the layout
/// checks it), so the grid has no seam anywhere near a floe, and a face's floes are all there is
/// to search for a point on it.
/// </para>
/// </remarks>
internal static class Floes
{
    /// <summary>How far (metres) a floe is kept clear of its cell's edges: two floes are at least twice this apart.</summary>
    private const float Gap = 1f;

    /// <summary>A cell's width in face coordinates: <see cref="Scene.FloeSpacing"/> at a face's middle, where a face coordinate's unit is an eighth of a circle.</summary>
    private static readonly float CellSize = Scene.FloeSpacing / (GroundBody.PlanetRadius * MathF.PI / 4f);

    /// <summary>One face's floes, by cell, over the cells that might hold one (a box round its bands).</summary>
    private sealed class Grid(int i0, int j0, int width, int height)
    {
        public readonly int I0 = i0, J0 = j0, Width = width, Height = height;
        public readonly Raft?[] Cells = new Raft?[width * height];

        public Raft? At(int i, int j)
        {
            var (x, y) = (i - I0, j - J0);
            return x < 0 || y < 0 || x >= Width || y >= Height ? null : Cells[y * Width + x];
        }
    }

    private static readonly Lazy<(Raft[] All, Grid?[] Faces, float Cover)> Layout = new(Build);

    /// <summary>Every floe, in order of its <see cref="Raft.Id"/> (from 1: the drone ships are 0 and down).</summary>
    public static IReadOnlyList<Raft> All => Layout.Value.All;

    /// <summary>How many floes there are in the world.</summary>
    public static int Count => Layout.Value.All.Length;

    /// <summary>
    /// How much of the floe bands the floes cover, on average: their area over the bands' (see
    /// <see cref="Build"/>). What the band's water is tinted by far off, where the floes aren't
    /// drawn (see <see cref="Scene.FloeDrawDistance"/>).
    /// </summary>
    public static float Cover => Layout.Value.Cover;

    /// <summary>The layout worked out afresh (new rafts), as <see cref="All"/> was.</summary>
    public static IReadOnlyList<Raft> Lay() => Build().All;

    /// <summary>The grid's cell <paramref name="direction"/> (a unit vector) is in.</summary>
    public static (int Face, int I, int J) CellOf(Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        return (face, (int)MathF.Floor((u + 1f) / CellSize), (int)MathF.Floor((v + 1f) / CellSize));
    }

    /// <summary>The floes whose discs come within <paramref name="distance"/> of <paramref name="p"/>, cell by cell.</summary>
    public static IEnumerable<Raft> Near(Vector3 p, float distance)
    {
        var found = new List<Raft>();
        Near(p, distance, found);
        return found;
    }

    /// <summary>
    /// Adds to <paramref name="found"/> the floes whose discs come within <paramref name="distance"/>
    /// of <paramref name="p"/>, cell by cell, row by row (the same order every time): the cells as
    /// far round as that, from how wide a cell is there.
    /// </summary>
    public static void Near(Vector3 p, float distance, List<Raft> found)
    {
        var up = Planet.Up(p);
        var (face, u, v) = CubeSphere.ToFace(up);
        if (Layout.Value.Faces[face] is not { } grid)
            return;
        var (i, j) = ((int)MathF.Floor((u + 1f) / CellSize), (int)MathF.Floor((v + 1f) / CellSize));
        var across = CubeSphere.FromFace(face, u + CellSize, v) - up;
        var along = CubeSphere.FromFace(face, u, v + CellSize) - up;
        var reach = distance + Scene.FloeMaxRadius;
        var ni = (int)MathF.Ceiling(reach / (0.9f * across.Length() * Planet.Radius)) + 1;
        var nj = (int)MathF.Ceiling(reach / (0.9f * along.Length() * Planet.Radius)) + 1;
        for (var y = Math.Max(j - nj, grid.J0); y <= Math.Min(j + nj, grid.J0 + grid.Height - 1); y++)
            for (var x = Math.Max(i - ni, grid.I0); x <= Math.Min(i + ni, grid.I0 + grid.Width - 1); x++)
                if (grid.At(x, y) is { } floe && Vector3.Distance(p, floe.Centre) - floe.HalfLength <= distance)
                    found.Add(floe);
    }

    /// <summary>
    /// Whether a point within <paramref name="distance"/> of <paramref name="p"/> could be over a
    /// floe (see <see cref="Under"/>), however the waves have tilted and lifted it: by
    /// <see cref="Under"/>'s own first, rough look (its height over the floe's calm waterline, and
    /// how far out past its rim), widened by the distance, so high over the band it is false; or
    /// with <paramref name="above"/>, no higher than that over its calm waterline (beside its side, say).
    /// Nothing is found (or kept) for it.
    /// </summary>
    public static bool AnyNear(Vector3 p, float distance, float above = Rough.Above)
    {
        if (OutOfReach(p, above + distance, Rough.Below + distance, Scene.FloeMaxRadius + Rough.Lean * (MathF.Max(above, Rough.Below) + 2f * distance) + Rough.Heave + distance))
            return false;
        var up = Planet.Up(p);
        var (face, u, v) = CubeSphere.ToFace(up);
        if (Layout.Value.Faces[face] is not { } grid)
            return false;
        var reach = distance + Rough.Heave + Rough.Lean * (above + distance);
        var (i, j) = ((int)MathF.Floor((u + 1f) / CellSize), (int)MathF.Floor((v + 1f) / CellSize));
        var across = CubeSphere.FromFace(face, u + CellSize, v) - up;
        var along = CubeSphere.FromFace(face, u, v + CellSize) - up;
        var ni = (int)MathF.Ceiling((reach + Scene.FloeMaxRadius) / (0.9f * across.Length() * Planet.Radius)) + 1;
        var nj = (int)MathF.Ceiling((reach + Scene.FloeMaxRadius) / (0.9f * along.Length() * Planet.Radius)) + 1;
        for (var y = Math.Max(j - nj, grid.J0); y <= Math.Min(j + nj, grid.J0 + grid.Height - 1); y++)
            for (var x = Math.Max(i - ni, grid.I0); x <= Math.Min(i + ni, grid.I0 + grid.Width - 1); x++)
            {
                if (grid.At(x, y) is not { } floe)
                    continue;
                var offset = p - floe.Centre;
                var height = Vector3.Dot(offset, Planet.Up(floe.Centre));
                if (height > above + distance || height < -Rough.Below - distance)
                    continue;
                var rim = floe.HalfLength + Rough.Lean * (MathF.Abs(height) + distance) + Rough.Heave + distance;
                if (offset.LengthSquared() - height * height <= rim * rim)
                    return true;
            }
        return false;
    }

    /// <summary>Whether a point within <paramref name="distance"/> of <paramref name="p"/> could be beside a floe's side, or in it (see <see cref="AnyNear"/>): no higher than its top can be lifted.</summary>
    public static bool AnyBeside(Vector3 p, float distance) => AnyNear(p, distance, Scene.FloeFreeboard + Rough.Heave);

    /// <summary>
    /// The floe whose top <paramref name="p"/> is over (see <see cref="Raft.HeightAbove"/>), and how
    /// high above it, or null. Only floes in its cell and the eight round it can be: a floe lies in
    /// its own cell, and over a tilted one a point can lean out over the next. Each is first
    /// measured against the calm sea, and only one near enough is set as it is now
    /// (<see cref="Rafts.Now"/>) and measured exactly; the first found is it (they never overlap).
    /// </summary>
    public static (Raft Raft, float Height)? Under(Vector3 p)
    {
        if (OutOfReach(p, Rough.Above, Rough.Below, Scene.FloeMaxRadius + Rough.Lean * Rough.Above + Rough.Heave))
            return null;
        var up = Planet.Up(p);
        var (face, u, v) = CubeSphere.ToFace(up);
        if (Layout.Value.Faces[face] is not { } grid)
            return null;
        var (i, j) = ((int)MathF.Floor((u + 1f) / CellSize), (int)MathF.Floor((v + 1f) / CellSize));
        for (var k = 0; k < 9; k++)
        {
            // Its own cell first, then those round it.
            var (di, dj) = k == 0 ? (0, 0) : Round[k - 1];
            if (grid.At(i + di, j + dj) is not { } floe)
                continue;
            var offset = p - floe.Centre;
            var height = Vector3.Dot(offset, Planet.Up(floe.Centre));
            if (height > Rough.Above || height < -Rough.Below)
                continue;
            var rim = floe.HalfLength + Rough.Lean * MathF.Abs(height) + Rough.Heave;
            if (offset.LengthSquared() - height * height > rim * rim)
                continue;
            if (Rafts.Now(floe).HeightAbove(p) is { } above)
                return (floe, above);
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="p"/> is too high over the sea, or too deep under it, for any floe's
    /// first look (in <see cref="Under"/> or <see cref="AnyNear"/>) to take it: more than
    /// <paramref name="above"/> over the calm waterline of every floe whose rim it is within
    /// <paramref name="rim"/> of (across), or more than <paramref name="below"/> under it. Told
    /// from its altitude alone, before its face and cells are found: a point's height over a
    /// floe's calm waterline is never more than its altitude, and less only by the sea's curve
    /// between them (for a floe that near, by under rim² over the sea's radius). Most points
    /// looked for over the band are high over it (down a craft's axis, at its feet, as it flies
    /// over), and finding the cells round each cost more than the rest.
    /// </summary>
    private static bool OutOfReach(Vector3 p, float above, float below, float rim)
    {
        var altitude = Planet.Altitude(p);
        return altitude < -below || altitude - rim * rim / Planet.SeaRadius - 0.5f > above;
    }

    /// <summary>The eight cells round a cell.</summary>
    private static readonly (int, int)[] Round = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    /// <summary>
    /// How far a floe's top can be from where it lies on a calm sea, for <see cref="Under"/>'s first
    /// look: how far over its calm waterline a point over it can be (the 50 m a raft reaches, and
    /// the waves' heave on top), and below it (its draft and the heave), and how far out past its
    /// rim (its tilt, under 0.2 of a radian, leaning a point that high out, and the heave).
    /// </summary>
    private static class Rough
    {
        public const float Above = 50f + Scene.FloeFreeboard + 6f, Below = Scene.FloeThickness + 6f, Lean = 0.2f, Heave = 2f;
    }

    /// <summary>
    /// Where each floe-band texel of a face lies across its band, from the sheet (0) out to open
    /// water (1): how far it is from the nearest sheet texel, over that plus how far from the
    /// nearest open water's (both by a chamfer distance over the texels, in texel steps), over a box
    /// of the face round its bands; read between texel centres bilinearly, so it changes smoothly
    /// and not in the texels' steps. The floes are densest by the sheet and thin out across it.
    /// </summary>
    private sealed class Across
    {
        private readonly int _s0, _t0, _width, _height, _resolution;
        private readonly float[] _f;

        public Across(TerrainMap map, int face, int s0, int t0, int s1, int t1)
        {
            var side = map.Resolution + 2;
            (_s0, _t0, _width, _height, _resolution) = (s0, t0, s1 - s0 + 1, t1 - t0 + 1, map.Resolution);
            var count = _width * _height;
            var (width, height) = (_width, _height);
            SurfaceKind KindAt(int x, int y) => (SurfaceKind)map.Kinds[face * side * side + (t0 + y) * side + s0 + x];
            float[] From(Func<SurfaceKind, bool> source)
            {
                var d = new float[count];
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        d[y * width + x] = source(KindAt(x, y)) ? 0f : float.MaxValue / 4f;
                const float diagonal = 1.41421356f;
                for (var y = 0; y < height; y++) // forward: from above and the left
                    for (var x = 0; x < width; x++)
                    {
                        var i = y * width + x;
                        if (x > 0) d[i] = MathF.Min(d[i], d[i - 1] + 1f);
                        if (y > 0) d[i] = MathF.Min(d[i], d[i - width] + 1f);
                        if (x > 0 && y > 0) d[i] = MathF.Min(d[i], d[i - width - 1] + diagonal);
                        if (x < width - 1 && y > 0) d[i] = MathF.Min(d[i], d[i - width + 1] + diagonal);
                    }
                for (var y = height - 1; y >= 0; y--) // and back: from below and the right
                    for (var x = width - 1; x >= 0; x--)
                    {
                        var i = y * width + x;
                        if (x < width - 1) d[i] = MathF.Min(d[i], d[i + 1] + 1f);
                        if (y < height - 1) d[i] = MathF.Min(d[i], d[i + width] + 1f);
                        if (x < width - 1 && y < height - 1) d[i] = MathF.Min(d[i], d[i + width + 1] + diagonal);
                        if (x > 0 && y < height - 1) d[i] = MathF.Min(d[i], d[i + width - 1] + diagonal);
                    }
                return d;
            }

            var sheet = From(k => k == SurfaceKind.PackIce);
            var open = From(k => k is not (SurfaceKind.PackIce or SurfaceKind.FloeField));
            _f = new float[count];
            for (var i = 0; i < count; i++)
                _f[i] = sheet[i] == 0f ? 0f : open[i] == 0f ? 1f : sheet[i] / (sheet[i] + open[i]);
        }

        /// <summary>How far across its band (0 by the sheet, 1 at open water) the point at (<paramref name="u"/>, <paramref name="v"/>) on the face is.</summary>
        public float At(float u, float v)
        {
            var (x, y) = ((u + 1f) * 0.5f * _resolution + 0.5f - _s0, (v + 1f) * 0.5f * _resolution + 0.5f - _t0);
            var (x0, y0) = ((int)MathF.Floor(x), (int)MathF.Floor(y));
            var (fx, fy) = (x - x0, y - y0);
            float F(int i, int j) => _f[Math.Clamp(j, 0, _height - 1) * _width + Math.Clamp(i, 0, _width - 1)];
            return float.Lerp(float.Lerp(F(x0, y0), F(x0 + 1, y0), fx), float.Lerp(F(x0, y0 + 1), F(x0 + 1, y0 + 1), fx), fy);
        }
    }

    /// <summary>
    /// Lays the floes out: every cell under a floe-band texel of the map, face by face, row by
    /// row, given its floe or none (<see cref="Cell"/>), numbered in that order from 1. And how
    /// much of the bands they cover (<see cref="Cover"/>): their area over the band texels'.
    /// </summary>
    private static (Raft[] All, Grid?[] Faces, float Cover) Build()
    {
        var map = GroundBody.Planet.Map; // the floes are the planet's sea's
        var (resolution, side) = (map.Resolution, map.Resolution + 2);
        var all = new List<Raft>();
        var faces = new Grid?[6];
        var cells = new SortedSet<(int J, int I)>();
        var bandArea = 0.0;
        for (var face = 0; face < 6; face++)
        {
            // The cells any part of a floe-band texel is in (a texel is narrower than a cell, so its corners find them all).
            cells.Clear();
            var half = 1f / resolution;
            var (s0, t0, s1, t1) = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
            for (var t = 1; t <= resolution; t++)
                for (var s = 1; s <= resolution; s++)
                {
                    if ((SurfaceKind)map.Kinds[face * side * side + t * side + s] != SurfaceKind.FloeField)
                        continue;
                    var (u, v) = ((s - 0.5f) / resolution * 2f - 1f, (t - 0.5f) / resolution * 2f - 1f);
                    for (var corner = 0; corner < 4; corner++)
                    {
                        var (cu, cv) = (u + ((corner & 1) == 0 ? -half : half), v + ((corner & 2) == 0 ? -half : half));
                        cells.Add(((int)MathF.Floor((cv + 1f) / CellSize), (int)MathF.Floor((cu + 1f) / CellSize)));
                    }
                    (s0, t0, s1, t1) = (Math.Min(s0, s), Math.Min(t0, t), Math.Max(s1, s), Math.Max(t1, t));
                    var (a, b, c) = (CubeSphere.FromFace(face, u - half, v - half), CubeSphere.FromFace(face, u + half, v - half), CubeSphere.FromFace(face, u - half, v + half));
                    bandArea += Vector3.Cross(b - a, c - a).Length() * GroundBody.PlanetRadius * GroundBody.PlanetRadius;
                }
            if (cells.Count == 0)
                continue;

            var (i0, i1) = (cells.Min(c => c.I), cells.Max(c => c.I));
            var (j0, j1) = (cells.Min(c => c.J), cells.Max(c => c.J));
            // Far from the face's edges, so that nothing near a floe is on another face (see the
            // remarks): nothing within a craft's reach at the top speed (RocketPhysics.UpdateFloes).
            // A design limit, not a broken map: a higher Scene.TopSpeed reaches further, so it says
            // which speed asked for how much room, and how much there is.
            var reach = Scene.FloeReach + Scene.TopSpeed * Scene.FloeReachSeconds;
            var room = (int)MathF.Ceiling((reach + 2f * Scene.FloeSpacing) / Scene.FloeSpacing / 0.7f);
            var last = (int)MathF.Floor(2f / CellSize) - 1;
            var margin = Math.Min(Math.Min(i0, j0), Math.Min(last - i1, last - j1));
            if (margin < room)
                throw new InvalidDataException(
                    $"a floe band is {margin} cells (about {margin * Scene.FloeSpacing:0} m) from cube face {face}'s edge, but a craft at the top speed "
                    + $"(Scene.TopSpeed, {Scene.TopSpeed:0} m/s) takes up floes {reach:0} m round it (Scene.FloeReach + the top speed × Scene.FloeReachSeconds), "
                    + $"which needs {room} cells (about {room * Scene.FloeSpacing:0} m): lower the top speed or FloeReachSeconds, or lay the bands further from the edge");

            // The bands' texels, and two more all round: the sheets inside them, and open water past them.
            var across = new Across(map, face, Math.Max(s0 - 2, 0), Math.Max(t0 - 2, 0), Math.Min(s1 + 2, resolution + 1), Math.Min(t1 + 2, resolution + 1));
            var grid = faces[face] = new Grid(i0, j0, i1 - i0 + 1, j1 - j0 + 1);
            foreach (var (j, i) in cells)
                if (Cell(face, i, j, all.Count + 1, across) is { } floe)
                {
                    grid.Cells[(j - j0) * grid.Width + (i - i0)] = floe;
                    all.Add(floe);
                }
        }

        var ice = all.Sum(f => Math.PI * f.HalfLength * f.HalfLength);
        return ([.. all], faces, bandArea > 0.0 ? (float)(ice / bandArea) : 0f);
    }

    /// <summary>
    /// The floe in cell (<paramref name="i"/>, <paramref name="j"/>) of <paramref name="face"/>, if
    /// it has one, numbered <paramref name="id"/>. From the cell's hash: its radius, skewed towards
    /// the small (from <see cref="Scene.FloeMinRadius"/> to <see cref="Scene.FloeMaxRadius"/> by
    /// the cube of an even draw: many small floes, a few large), and where in the cell its centre
    /// is, kept far enough from the cell's edges for its disc and <see cref="Gap"/> (so a small one
    /// has more of the cell to wander in); it is shrunk to fit if the cell is narrower there than
    /// at a face's middle, and there is none if that would leave it too small. Whether it has one
    /// at all depends on where it is: up to <see cref="Scene.FloeCover"/> of the cells have one by
    /// the sheet, thinning out across the band (<paramref name="across"/>) to none at open water,
    /// and the whole clustered, denser and sparser, by noise <see cref="Scene.FloeClusterSize"/>
    /// across; the band ends where it thins out, on a line broken by finer noise, not along the
    /// texels. There is none, either, if its centre is off the floe band, or if under any of it
    /// (its centre, and eight points round its rim) the ground is less than
    /// <see cref="Scene.FloeFootClearance"/> under the sea: the sheet's foot, at its edge. Its heading (its x) is
    /// along the face's u.
    /// </summary>
    private static Raft? Cell(int face, int i, int j, int id, Across across)
    {
        var seed = Mix(((ulong)(uint)face << 42) ^ ((ulong)(uint)i << 21) ^ (uint)j);
        float Draw(int k) => (Mix(seed + (ulong)k * 0x9E3779B97F4A7C15UL) >> 40) / (float)(1 << 24); // in [0, 1)
        var skew = Draw(1);
        var radius = Scene.FloeMinRadius + (Scene.FloeMaxRadius - Scene.FloeMinRadius) * skew * skew * skew;

        var (u0, v0) = (-1f + i * CellSize, -1f + j * CellSize);
        var (u1, v1) = (u0 + CellSize, v0 + CellSize);
        var (um, vm) = (u0 + CellSize / 2f, v0 + CellSize / 2f);
        var wide = Vector3.Distance(CubeSphere.FromFace(face, u0, vm), CubeSphere.FromFace(face, u1, vm)) * GroundBody.PlanetRadius;
        var tall = Vector3.Distance(CubeSphere.FromFace(face, um, v0), CubeSphere.FromFace(face, um, v1)) * GroundBody.PlanetRadius;
        var (mu, mv) = (MathF.Min((radius + Gap) / wide, 0.5f), MathF.Min((radius + Gap) / tall, 0.5f));
        var (u, v) = (u0 + CellSize * (mu + (1f - 2f * mu) * Draw(2)), v0 + CellSize * (mv + (1f - 2f * mv) * Draw(3)));
        var direction = CubeSphere.FromFace(face, u, v);

        // Whether the cell has one: by the sheet, thinning across the band, in clusters.
        var p = direction * GroundBody.PlanetRadius;
        var f = across.At(u, v) + Fringe * Terrain.Noise(p / FringeSize + new Vector3(41.7f, 13.1f, 7.9f));
        if (f >= 1f - Fringe)
            return null;
        var cluster = Math.Clamp(0.75f + 0.8f * Terrain.Noise(p / Scene.FloeClusterSize + new Vector3(3.3f, 27.1f, 11.9f)), 0.1f, 1f);
        if (Draw(0) >= Scene.FloeCover * MathF.Pow(1f - MathF.Max(f, 0f), 0.6f) * cluster)
            return null;

        // How far it is to each edge: a line of one face coordinate is a great circle, so the
        // distance to it is the angle off its plane.
        var clear = float.MaxValue;
        foreach (var (a, b) in (ReadOnlySpan<((float, float), (float, float))>)[((u0, v0), (u0, v1)), ((u1, v0), (u1, v1)), ((u0, v0), (u1, v0)), ((u0, v1), (u1, v1))])
        {
            var plane = Vector3.Normalize(Vector3.Cross(CubeSphere.FromFace(face, a.Item1, a.Item2), CubeSphere.FromFace(face, b.Item1, b.Item2)));
            clear = MathF.Min(clear, GroundBody.PlanetRadius * MathF.Asin(MathF.Abs(Vector3.Dot(direction, plane))));
        }
        radius = MathF.Min(radius, clear - Gap);
        if (radius < Scene.FloeMinRadius)
            return null;

        var du = CubeSphere.FromFace(face, u + 1e-3f, v) - CubeSphere.FromFace(face, u - 1e-3f, v);
        var heading = Vector3.Normalize(du - direction * Vector3.Dot(du, direction));
        var side = Vector3.Cross(heading, direction);
        if (Terrain.Kind(direction) != SurfaceKind.FloeField || Terrain.PlanetHeight(direction) > -Scene.FloeFootClearance)
            return null;
        for (var k = 0; k < 8; k++)
        {
            var (s, c) = MathF.SinCos(k * MathF.PI / 4f);
            if (Terrain.PlanetHeight(Vector3.Normalize(direction * GroundBody.PlanetRadius + (heading * c + side * s) * radius)) > -Scene.FloeFootClearance)
                return null;
        }

        var centre = GroundBody.Planet.Centre + direction * GroundBody.PlanetSeaRadius;
        return new Raft(id, RaftShape.Disc, centre, heading, radius, radius, Scene.FloeFreeboard, Scene.FloeThickness - Scene.FloeFreeboard);
    }

    /// <summary>How far (across the band, 0 to 1) the band's outer end is moved in and out by noise, and the noise's size in metres: so the floes end on a broken line, not the texels'.</summary>
    private const float Fringe = 0.12f, FringeSize = 60f;

    /// <summary>A 64-bit hash's mixing (SplitMix64's finaliser).</summary>
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
