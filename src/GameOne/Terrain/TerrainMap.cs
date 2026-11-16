using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace GameOne;

/// <summary>
/// A body's fixed terrain (the planet's, or the moon's: <see cref="MoonGround"/>), as the generator
/// wrote it: per cube-face texel, a height, a surface kind and a region (land, or one of the named
/// seas). Each face is <c>Resolution + 2</c> texels
/// square: texel <c>t</c> has its centre at <c>a = ((t - 0.5) / Resolution) * 2 - 1</c>, so texels 0
/// and <c>Resolution + 1</c> are an apron just over the face's edge, holding the true surface
/// there so bilinear reads and patch edges meet across faces. Arrays are face-major, then row (v),
/// then column (u).
/// </summary>
internal sealed class TerrainMap
{
    private const int Version = 4; // 4: Home's elevation in the header (3 was the first above the sea; 2 above the sphere through Home's pad)
    private static readonly byte[] Magic = "GOTR"u8.ToArray();

    private readonly ushort[][] _minLevels;
    private readonly ushort[][] _maxLevels;
    private readonly int[] _levelSides;

    public TerrainMap(int resolution, float radius, float homeElevation, float minHeight, float maxHeight,
        ushort[] heights, byte[] kinds, byte[] regions, IReadOnlyList<SeaInfo> seas)
    {
        var side = resolution + 2;
        var count = 6 * side * side;
        if (heights.Length != count || kinds.Length != count || regions.Length != count)
            throw new ArgumentException($"each array must hold {count} texels");
        // The waves read a texel's sea straight from its region, so a region with no sea is refused here, not met mid-flight.
        if (regions.Max() > seas.Count)
            throw new InvalidDataException($"a region is past the map's {seas.Count} seas");
        foreach (var sea in seas)
            if (!(sea.Waves > 0f) || !float.IsFinite(sea.Waves))
                throw new InvalidDataException($"{sea.Name}'s waves ({sea.Waves}) must be above 0");

        Resolution = resolution;
        Radius = radius;
        HomeElevation = homeElevation;
        MinHeight = minHeight;
        MaxHeight = maxHeight;
        Heights = heights;
        Kinds = kinds;
        Regions = regions;
        Seas = seas;
        (_minLevels, _maxLevels, _levelSides) = BuildPyramid(heights, side);
    }

    public int Resolution { get; }
    public float Radius { get; }

    /// <summary>How high above the sea Home's pad was levelled: <see cref="Scene.HomeElevation"/> when it was made.</summary>
    public float HomeElevation { get; }

    public float MinHeight { get; }
    public float MaxHeight { get; }

    /// <summary>Elevation above the sea: <c>MinHeight + (MaxHeight - MinHeight) * h / 65535</c>.</summary>
    public ushort[] Heights { get; }

    /// <summary>A <see cref="SurfaceKind"/> per texel (on the moon, a <see cref="MoonKind"/>).</summary>
    public byte[] Kinds { get; }

    /// <summary>0 for land, else 1 + the index of the sea in <see cref="Seas"/>.</summary>
    public byte[] Regions { get; }

    /// <summary>The named seas, each with its climate, wave size and colours.</summary>
    public IReadOnlyList<SeaInfo> Seas { get; }

    private int Side => Resolution + 2;

    /// <summary>A stored height (one of <see cref="Heights"/>) in metres.</summary>
    public float Metres(ushort h) => MinHeight + (MaxHeight - MinHeight) * h / 65535f;

    /// <summary>A face coordinate in [-1, 1] as a texel coordinate (centres at integers), clamped to the map.</summary>
    private float ToTexel(float a) => Math.Clamp((a + 1f) * 0.5f * Resolution + 0.5f, 0f, Resolution + 1f);

    /// <summary>The height at a point on a face, in metres: bilinear between texel centres.</summary>
    public float HeightAt(int face, float u, float v)
    {
        Span<int> index = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        Texels(face, u, v, index, weight);
        var h = 0f;
        for (var i = 0; i < 4; i++) h += Metres(Heights[index[i]]) * weight[i];
        return h;
    }

    /// <summary>
    /// The four texels a bilinear read at a point uses (array indices) and their weights, which sum
    /// to 1. The order is (0,0), (+u,0), (0,+v), (+u,+v).
    /// </summary>
    public void Texels(int face, float u, float v, Span<int> index, Span<float> weight)
    {
        var tu = ToTexel(u);
        var tv = ToTexel(v);
        var i = Math.Min((int)tu, Resolution);
        var j = Math.Min((int)tv, Resolution);
        var fu = tu - i;
        var fv = tv - j;
        var origin = face * Side * Side + j * Side + i;
        index[0] = origin;
        index[1] = origin + 1;
        index[2] = origin + Side;
        index[3] = origin + Side + 1;
        weight[0] = (1f - fu) * (1f - fv);
        weight[1] = fu * (1f - fv);
        weight[2] = (1f - fu) * fv;
        weight[3] = fu * fv;
    }

    /// <summary>The region of the nearest texel to a point.</summary>
    public byte RegionAt(int face, float u, float v)
    {
        var i = (int)MathF.Round(ToTexel(u));
        var j = (int)MathF.Round(ToTexel(v));
        return Regions[face * Side * Side + j * Side + i];
    }

    /// <summary>
    /// The lowest and highest the surface gets over the square patch from (<paramref name="u0"/>,
    /// <paramref name="v0"/>) and <paramref name="size"/> across, in face coordinates: conservative,
    /// from a min/max pyramid, so it can only be wider than the truth.
    /// </summary>
    public (float Min, float Max) Range(int face, float u0, float v0, float size)
    {
        // Every texel a bilinear read inside the patch can touch.
        var i0 = Math.Min((int)ToTexel(u0), Resolution);
        var i1 = Math.Min((int)ToTexel(u0 + size) + 1, Side - 1);
        var j0 = Math.Min((int)ToTexel(v0), Resolution);
        var j1 = Math.Min((int)ToTexel(v0 + size) + 1, Side - 1);

        // The coarsest level whose cells are no bigger than the patch, so a patch scans few cells.
        var span = Math.Max(i1 - i0, j1 - j0);
        var level = 0;
        while (level + 1 < _levelSides.Length && (2 << level) <= span) level++;

        var side = _levelSides[level];
        var mins = _minLevels[level];
        var maxs = _maxLevels[level];
        ushort lo = ushort.MaxValue, hi = 0;
        for (var j = j0 >> level; j <= j1 >> level; j++)
        {
            for (var i = i0 >> level; i <= i1 >> level; i++)
            {
                var at = face * side * side + j * side + i;
                if (mins[at] < lo) lo = mins[at];
                if (maxs[at] > hi) hi = maxs[at];
            }
        }

        return (Metres(lo), Metres(hi));
    }

    /// <summary>Levels of 2x2-merged minima and maxima, down to a single cell a face.</summary>
    private static (ushort[][] Mins, ushort[][] Maxs, int[] Sides) BuildPyramid(ushort[] heights, int side)
    {
        var mins = new List<ushort[]> { heights };
        var maxs = new List<ushort[]> { heights };
        var sides = new List<int> { side };
        while (sides[^1] > 1)
        {
            var from = sides[^1];
            var to = (from + 1) / 2;
            var lo = new ushort[6 * to * to];
            var hi = new ushort[6 * to * to];
            var fromMin = mins[^1];
            var fromMax = maxs[^1];
            for (var face = 0; face < 6; face++)
            {
                for (var j = 0; j < to; j++)
                {
                    for (var i = 0; i < to; i++)
                    {
                        ushort a = ushort.MaxValue, b = 0;
                        for (var dj = 0; dj < 2; dj++)
                        {
                            for (var di = 0; di < 2; di++)
                            {
                                var sj = Math.Min(j * 2 + dj, from - 1);
                                var si = Math.Min(i * 2 + di, from - 1);
                                var at = face * from * from + sj * from + si;
                                if (fromMin[at] < a) a = fromMin[at];
                                if (fromMax[at] > b) b = fromMax[at];
                            }
                        }

                        lo[face * to * to + j * to + i] = a;
                        hi[face * to * to + j * to + i] = b;
                    }
                }
            }

            mins.Add(lo);
            maxs.Add(hi);
            sides.Add(to);
        }

        return (mins.ToArray(), maxs.ToArray(), sides.ToArray());
    }

    /// <summary>Writes the map as gzip: header, the seas (each one's kind, name, wave size and colours), then heights, kinds and regions.</summary>
    public void Save(string path)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var w = new BinaryWriter(gzip, Encoding.UTF8);
        w.Write(Magic);
        w.Write(Version);
        w.Write(Resolution);
        w.Write(Radius);
        w.Write(HomeElevation);
        w.Write(MinHeight);
        w.Write(MaxHeight);
        w.Write(Seas.Count);
        foreach (var sea in Seas)
        {
            w.Write((byte)sea.Kind);
            w.Write(sea.Name); // BinaryWriter's string: length-prefixed UTF-8
            w.Write(sea.Waves);
            Write(w, sea.Shallow);
            Write(w, sea.Deep);
        }

        var heights = new byte[Heights.Length * 2];
        for (var i = 0; i < Heights.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(heights.AsSpan(i * 2), Heights[i]);
        w.Write(heights);
        w.Write(Kinds);
        w.Write(Regions);
    }

    /// <summary>Reads the planet's map, refusing one made for another planet radius or Home's pad at another height.</summary>
    public static TerrainMap Load(string path) => Load(path, Scene.PlanetRadius, Scene.HomeElevation);

    /// <summary>
    /// Reads a map, refusing one made for a body of another <paramref name="radius"/> or levelled
    /// for another <paramref name="homeElevation"/> (Home's pad's above the sea on the planet; 0,
    /// the datum sphere itself, on the moon). The refusals name the file, so they say whose map is
    /// out of date.
    /// </summary>
    public static TerrainMap Load(string path, float radius, float homeElevation)
    {
        var file = Path.GetFileName(path);
        using var stream = File.OpenRead(path);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var r = new BinaryReader(gzip, Encoding.UTF8);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException($"{file} is not a terrain map");
        var version = r.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"{file} is terrain map version {version}, not {Version}: regenerate it with tools/TerrainGen");
        var resolution = r.ReadInt32();
        var madeFor = r.ReadSingle();
        if (MathF.Abs(madeFor - radius) >= 0.5f)
            throw new InvalidDataException(
                $"{file} was made for a radius of {madeFor} m; this build's is {radius} m: regenerate it with tools/TerrainGen");
        // Its heights are above the datum (the sea, on the planet), but it was levelled for this height above it.
        var elevation = r.ReadSingle();
        if (MathF.Abs(elevation - homeElevation) >= 0.01f)
            throw new InvalidDataException(
                $"{file} was made for an elevation of {elevation} m; this build's is {homeElevation} m: regenerate it with tools/TerrainGen");
        var min = r.ReadSingle();
        var max = r.ReadSingle();
        var seaCount = r.ReadInt32();
        var seas = new List<SeaInfo>(seaCount);
        for (var i = 0; i < seaCount; i++)
        {
            var kind = (SeaKind)r.ReadByte();
            var name = r.ReadString();
            var waves = r.ReadSingle();
            var shallow = ReadVector(r);
            seas.Add(new SeaInfo(name, kind, waves, shallow, ReadVector(r)));
        }

        var count = 6 * (resolution + 2) * (resolution + 2);
        var raw = r.ReadBytes(count * 2);
        var kinds = r.ReadBytes(count);
        var regions = r.ReadBytes(count);
        if (raw.Length != count * 2 || kinds.Length != count || regions.Length != count)
            throw new InvalidDataException($"{file} is truncated");
        var heights = new ushort[count];
        for (var i = 0; i < count; i++)
            heights[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2));
        return new TerrainMap(resolution, madeFor, elevation, min, max, heights, kinds, regions, seas);
    }

    private static void Write(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 ReadVector(BinaryReader r)
    {
        var x = r.ReadSingle();
        var y = r.ReadSingle();
        return new Vector3(x, y, r.ReadSingle());
    }
}
