namespace GameOne.Rendering;

/// <summary>
/// Where the sea meets the ground far off: the map's heights clamped to ±<see cref="Reach"/> about
/// the sea level, with a mip chain (<see cref="FaceLevels"/>), R16_SFLOAT, a layer per face, read
/// by the planet's vertex shader at its squares' corners, trilinearly, at the level for their
/// spacing (<see cref="Level"/>), so a corner far off says how much of what it stands for is water
/// rather than which of the two its one point is (which runs the waterline along the squares, in
/// steps, where a sheet of ice meets deep water within a texel). The sign of what it reads is
/// whether the water is drawn there; the seabed's depth push follows it (<see cref="Depth"/>).
/// </summary>
/// <remarks>
/// Clamped, so land and water count alike however high or deep they are: an even mix of the two
/// reads 0, and the waterline falls in the middle of what is mixed. Averaged unclamped, the sheet's
/// 0.4 m of freeboard against the metres of water beside it would read as water until almost all
/// of a corner's block was ice, drowning the sheet's edge by most of a square (and a sheet a few
/// squares across altogether). The clamp is the sheet's freeboard, the least height land stands
/// above the sea, so it keeps every waterline's crossing where the heights put it: a shore's
/// slope clamped at ±0.4 m still crosses where it did. The values are within ±0.4 m, so half
/// floats hold them to a fraction of a millimetre, a format Vulkan requires to filter linearly.
/// </remarks>
internal sealed class Waterline
{
    private Waterline(int side, float[][] levels)
    {
        Side = side;
        _levels = levels;
    }

    private readonly float[][] _levels;

    /// <summary>The clamp about the sea level, in metres: the sheets' freeboard.</summary>
    public const float Reach = Scene.PackIceFreeboard;

    /// <summary>
    /// How far the seabed's depth push ramps up over, in metres of depth, near: in its first 2 m,
    /// so a few metres of water show clearly apart from the seabed under it. Far off it ramps over
    /// <see cref="Reach"/>, all the depth the clamped heights have.
    /// </summary>
    public const float NearPushDepth = 2f;

    /// <summary>The base level's side, in texels (the map's resolution).</summary>
    public int Side { get; }

    /// <summary>How many levels, down to 1 by 1.</summary>
    public int Levels => _levels.Length;

    /// <summary>The planet's waterline from <paramref name="map"/>, whose resolution must be a power of two.</summary>
    public static Waterline Build(TerrainMap map)
    {
        int n = map.Resolution, mapSide = n + 2;
        var levels = FaceLevels.Build(n, 1, (face, into) =>
        {
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                    into[j * n + i] = Math.Clamp(map.Metres(map.Heights[(face * mapSide + j + 1) * mapSide + i + 1]), -Reach, Reach);
        }, "the waterline's map");
        // As the GPU holds them, so the C#'s reads (Sample) are the shader's.
        foreach (var level in levels)
            for (var t = 0; t < level.Length; t++)
                level[t] = (float)(Half)level[t];
        return new Waterline(n, levels);
    }

    /// <summary>Texel (<paramref name="i"/>, <paramref name="j"/>) of a face at a level, in metres.</summary>
    public float Texel(int level, int face, int i, int j)
    {
        var side = Side >> level;
        return _levels[level][(face * side + j) * side + i];
    }

    /// <summary>Every level, base first, each its six faces in turn, row (v) after row, as R16_SFLOAT: as the upload copies it.</summary>
    public byte[] Pixels()
    {
        var bytes = new byte[2 * _levels.Sum(l => l.Length)];
        var at = 0;
        foreach (var level in _levels)
            foreach (var value in level)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(at), (Half)value);
                at += 2;
            }
        return bytes;
    }

    /// <summary>A texel's size on the ground, in metres: a face's quarter turn of the sea's sphere over the map's resolution, as the vertex shader turns metres into the face's coordinates.</summary>
    public static float TexelMetres(int resolution) => GroundBody.PlanetSeaRadius * 0.7853982f * 2f / resolution;

    /// <summary>
    /// The level read at corners <paramref name="spacing"/> metres apart (the GLSL's
    /// <c>waterlineLevel</c>): the one whose texels are half a square, so each corner averages the
    /// half square round it. That is as fine as averaging can be and still leave no corner of a
    /// square the waterline crosses all one or the other; coarser, the sheet's tongues and bights
    /// are smoothed off further than they need be. 0 or below (squares two texels apart or
    /// closer) the corners read the heights themselves, as all near ground does.
    /// </summary>
    public static float Level(float spacing, int resolution) => MathF.Log2(spacing / TexelMetres(resolution)) - 1f;

    /// <summary>
    /// What the GPU's trilinear read of the levels gives at (<paramref name="u"/>, <paramref name="v"/>)
    /// on a face (in [-1, 1]) at level <paramref name="level"/>: each level's texel i centred at
    /// (i + 0.5) / side over [0, 1], clamped to the face's edge, two levels blended by the fraction.
    /// </summary>
    public float Sample(int face, float u, float v, float level)
    {
        level = Math.Clamp(level, 0f, Levels - 1);
        var lo = (int)MathF.Floor(level);
        var hi = Math.Min(lo + 1, Levels - 1);
        var f = level - lo;
        return Bilinear(lo) * (1f - f) + Bilinear(hi) * f;

        float Bilinear(int k)
        {
            var side = Side >> k;
            var (i0, i1, j0, j1, x, y) = FaceLevels.Bilinear(u, v, side);
            float T(int a, int b) => _levels[k][(face * side + b) * side + a];
            return FaceLevels.Mix(T(i0, j0), T(i1, j0), T(i0, j1), T(i1, j1), x, y);
        }
    }

    /// <summary>
    /// The vertex shader's waterline at a corner (its <c>waterline</c>): sea level less the ground's
    /// height there as the water is drawn by (above 0, water), and the depth the seabed's push ramps
    /// up over. Near (<see cref="Level"/> at 0 or below) it is <paramref name="depth"/>, the sea level
    /// less the ground's own height, with the push over its first <see cref="NearPushDepth"/>, as all
    /// near ground has it; further, over one level, it blends to the clamped levels' and
    /// <see cref="Reach"/>.
    /// </summary>
    public (float Depth, float PushOver) Depth(int face, float u, float v, float spacing, float depth)
    {
        var level = Level(spacing, Side);
        if (level <= 0f)
            return (depth, NearPushDepth);
        var t = MathF.Min(level, 1f);
        return (depth + (-Sample(face, u, v, level) - depth) * t, NearPushDepth + (Reach - NearPushDepth) * t);
    }
}
