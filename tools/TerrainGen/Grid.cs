using System.Numerics;
using GameOne;

namespace TerrainGen;

/// <summary>
/// The map's texels without its aprons: <c>6 * N * N</c> of them, face-major, then row (v), then
/// column (u), interior texel <c>i</c> centred at <c>((i + 0.5) / N) * 2 - 1</c> (the map's texel
/// <c>i + 1</c>). Each texel has eight neighbours, found across face edges too, so everything the
/// generator does to the ground (distances, flow, erosion) runs round the whole sphere seamlessly.
/// </summary>
internal sealed class Grid
{
    // Neighbour k's step in (u, v): the four sides first, then the diagonals.
    private static readonly int[] StepU = [1, -1, 0, 0, 1, -1, 1, -1];
    private static readonly int[] StepV = [0, 0, 1, -1, 1, 1, -1, -1];

    public Grid(int resolution, float radius)
    {
        N = resolution;
        Radius = radius;
        Count = 6 * N * N;
        Directions = new Vector3[Count];
        Areas = new float[Count];
        Parallel.For(0, 6 * N, row =>
        {
            var face = row / N;
            var j = row % N;
            for (var i = 0; i < N; i++)
            {
                var at = Index(face, i, j);
                Directions[at] = CubeSphere.FromFace(face, Coord(i), Coord(j));
                // The texel's area from its corners: two triangles on the chords.
                var c00 = CubeSphere.FromFace(face, Edge(i), Edge(j));
                var c10 = CubeSphere.FromFace(face, Edge(i + 1), Edge(j));
                var c01 = CubeSphere.FromFace(face, Edge(i), Edge(j + 1));
                var c11 = CubeSphere.FromFace(face, Edge(i + 1), Edge(j + 1));
                Areas[at] = radius * radius * 0.5f *
                            (Vector3.Cross(c10 - c00, c01 - c00).Length() + Vector3.Cross(c10 - c11, c01 - c11).Length());
            }
        });
    }

    public int N { get; }
    public int Count { get; }
    public float Radius { get; }
    public Vector3[] Directions { get; }

    /// <summary>Square metres per texel.</summary>
    public float[] Areas { get; }

    public int Index(int face, int i, int j) => face * N * N + j * N + i;

    public float Coord(int i) => (i + 0.5f) / N * 2f - 1f;

    private float Edge(int i) => (float)i / N * 2f - 1f;

    /// <summary>The texel nearest a point on a face.</summary>
    public int Nearest(int face, float u, float v)
    {
        var i = Math.Clamp((int)MathF.Floor((u + 1f) * 0.5f * N), 0, N - 1);
        var j = Math.Clamp((int)MathF.Floor((v + 1f) * 0.5f * N), 0, N - 1);
        return Index(face, i, j);
    }

    public int Nearest(Vector3 direction)
    {
        var (face, u, v) = CubeSphere.ToFace(direction);
        return Nearest(face, u, v);
    }

    /// <summary>Neighbour <paramref name="k"/> (0-3 the sides, 4-7 the diagonals), across a face edge if need be.</summary>
    public int Neighbour(int index, int k)
    {
        var face = index / (N * N);
        var rest = index - face * N * N;
        var j = rest / N;
        var i = rest - j * N;
        var ni = i + StepU[k];
        var nj = j + StepV[k];
        if ((uint)ni < (uint)N && (uint)nj < (uint)N) return index + StepU[k] + StepV[k] * N;

        // Off the face, straight across an edge: the next face's edge texel in the same row. Found
        // from the point just over the edge itself, where both faces' coordinates along it agree
        // exactly (a texel further over, they drift apart by up to a texel near the corners).
        if (k < 4)
        {
            var over = 1f + 1e-4f;
            var edge = ni < 0 || ni >= N
                ? CubeSphere.FromFace(face, ni < 0 ? -over : over, Coord(nj))
                : CubeSphere.FromFace(face, Coord(ni), nj < 0 ? -over : over);
            return Nearest(edge);
        }

        // A diagonal off the face: the texel on the next face under where this one would have been.
        // (At a cube corner it can land on a texel that is already a side neighbour; harmless.)
        var found = Nearest(CubeSphere.FromFace(face, Coord(ni), Coord(nj)));
        return found == index ? Neighbour(index, k & 3) : found;
    }

    /// <summary>Metres between two texels' centres (the chord; at a texel apart it is the arc).</summary>
    public float Distance(int a, int b) => Radius * Vector3.Distance(Directions[a], Directions[b]);

    /// <summary>Metres along the ground between two directions.</summary>
    public float Arc(Vector3 a, Vector3 b) => 2f * Radius * MathF.Asin(MathF.Min(Vector3.Distance(a, b) * 0.5f, 1f));

    /// <summary>
    /// A field read bilinearly between texel centres at a point on a face. Within half a texel of
    /// the edge it reads the next face's texels beyond it, so a field sampled this way (an upsampled
    /// level, the map's aprons) runs on smoothly across the edge.
    /// </summary>
    public float Sample(float[] values, int face, float u, float v)
    {
        var tu = Math.Clamp((u + 1f) * 0.5f * N - 0.5f, -1f, N);
        var tv = Math.Clamp((v + 1f) * 0.5f * N - 0.5f, -1f, N);
        var i = Math.Min((int)MathF.Floor(tu), N - 1);
        var j = Math.Min((int)MathF.Floor(tv), N - 1);
        var fu = tu - i;
        var fv = tv - j;
        return (values[Texel(face, i, j)] * (1f - fu) + values[Texel(face, i + 1, j)] * fu) * (1f - fv) +
               (values[Texel(face, i, j + 1)] * (1f - fu) + values[Texel(face, i + 1, j + 1)] * fu) * fv;
    }

    /// <summary>Texel (i, j) of a face, where i and j may be one past either edge: then the neighbouring face's texel there.</summary>
    public int Texel(int face, int i, int j)
    {
        var ci = Math.Clamp(i, 0, N - 1);
        var cj = Math.Clamp(j, 0, N - 1);
        var at = Index(face, ci, cj);
        if (ci == i && cj == j) return at;
        int du = i - ci, dv = j - cj;
        for (var k = 0; k < 8; k++)
            if (StepU[k] == du && StepV[k] == dv) return Neighbour(at, k);
        throw new ArgumentOutOfRangeException(nameof(i));
    }

    /// <summary>
    /// For every texel, the distance in metres to the nearest seed texel and which seed it is: each
    /// seed's own position is spread outward (so the distance is the true one along the ground, not
    /// a path through the grid), limited to texels <paramref name="through"/> allows if given.
    /// </summary>
    public (float[] Distance, int[] Nearest) DistanceTo(IEnumerable<int> seeds, Func<int, bool>? through = null)
    {
        var distance = new float[Count];
        var nearest = new int[Count];
        Array.Fill(distance, float.PositiveInfinity);
        Array.Fill(nearest, -1);
        var heap = new Heap(1 << 16);
        foreach (var s in seeds)
        {
            distance[s] = 0f;
            nearest[s] = s;
            heap.Push(0f, s);
        }

        while (heap.TryPop(out var d, out var c))
        {
            if (d > distance[c]) continue; // a stale entry: already reached closer
            var seed = Directions[nearest[c]];
            for (var k = 0; k < 8; k++)
            {
                var n = Neighbour(c, k);
                if (through != null && !through(n)) continue;
                var nd = Arc(Directions[n], seed);
                if (nd < distance[n])
                {
                    distance[n] = nd;
                    nearest[n] = nearest[c];
                    heap.Push(nd, n);
                }
            }
        }

        return (distance, nearest);
    }

    /// <summary>
    /// Metres from one seed texel to every texel, along paths through the texels
    /// <paramref name="through"/> allows (infinite where none reaches): how far by water, say.
    /// </summary>
    public float[] PathDistance(int seed, Func<int, bool> through)
    {
        var distance = new float[Count];
        Array.Fill(distance, float.PositiveInfinity);
        var heap = new Heap(1 << 16);
        distance[seed] = 0f;
        heap.Push(0f, seed);
        while (heap.TryPop(out var d, out var c))
        {
            if (d > distance[c]) continue;
            for (var k = 0; k < 8; k++)
            {
                var n = Neighbour(c, k);
                if (!through(n)) continue;
                var nd = d + Distance(c, n);
                if (nd < distance[n])
                {
                    distance[n] = nd;
                    heap.Push(nd, n);
                }
            }
        }

        return distance;
    }
}

/// <summary>
/// A binary min-heap of (key, texel), ties broken by texel index, so everything that pops in its
/// order (distances, the flood fill) comes out the same every run.
/// </summary>
internal sealed class Heap(int capacity)
{
    private (float Key, int Index)[] _items = new (float, int)[capacity];
    private int _count;

    public int Count => _count;

    private static bool Less((float Key, int Index) a, (float Key, int Index) b) =>
        a.Key < b.Key || (a.Key == b.Key && a.Index < b.Index);

    public void Push(float key, int index)
    {
        if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        var item = (key, index);
        var at = _count++;
        while (at > 0)
        {
            var parent = (at - 1) >> 1;
            if (!Less(item, _items[parent])) break;
            _items[at] = _items[parent];
            at = parent;
        }

        _items[at] = item;
    }

    public bool TryPop(out float key, out int index)
    {
        if (_count == 0)
        {
            key = 0f;
            index = -1;
            return false;
        }

        (key, index) = _items[0];
        var last = _items[--_count];
        var at = 0;
        while (true)
        {
            var child = at * 2 + 1;
            if (child >= _count) break;
            if (child + 1 < _count && Less(_items[child + 1], _items[child])) child++;
            if (!Less(_items[child], last)) break;
            _items[at] = _items[child];
            at = child;
        }

        if (_count > 0) _items[at] = last;
        return true;
    }
}
