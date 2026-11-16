using System.Numerics;

namespace GameOne;

/// <summary>
/// The air's light worked out ahead for the GPU (<see cref="Air"/> is the reference): two layers of
/// <see cref="SunAngles"/> by <see cref="Heights"/> RGBA16F texels, built once at start. Layer 0 is
/// the transmittance towards a sun at a cosine from straight up, from a height above the sea (what
/// every path to the sun in the shaders is dimmed by); layer 1 the sky's light on an upward face
/// (<see cref="Air.SkyLight"/>). Rows are heights, 0 to <see cref="Scene.AtmosphereHeight"/> evenly.
/// The sky's columns are the sun's cosine from up, the root of it on each side of 0 spread evenly
/// from -1 to 1, a texel's centre on each end. The transmittance has a sharp edge where the sun
/// drops below the height's horizon (nothing gets through the sea), and its lower half falls away
/// steeply to it, so there the columns follow how low the ray's closest approach to the sea is (the
/// same for every height, so the edge lines up from row to row): <see cref="LowColumns"/> columns
/// from 0 (just over the sea) to the level, by a curve that spreads the columns out near both ends (about the square root at each) of that clearance as a part of the height,
/// then the rest from the level up to straight up by the root of the cosine. A sun under the horizon
/// is told by the lookup itself (<see cref="Horizon"/>), not the table. The shaders'
/// <c>airTransmittance</c> and <c>airSkyLight</c> make the lookup <see cref="Look"/> does, so change one and change the other.
/// </summary>
internal static class AirTables
{
    public const int Heights = 32;
    public const int SunAngles = 64;

    /// <summary>How many columns of the transmittance's go from the sea's edge to the level (the level is the last).</summary>
    internal const int LowColumns = 24;

    /// <summary>The least height (m) the closest approach is measured against, for an eye lower than it.</summary>
    internal const float RoomFloor = 40f;

    /// <summary>A ray this much (m) over the sea at its closest stands for the sea's edge: one exactly along it counts as meeting the sea.  (Column 0 is made at this, though the lookup maps a clearance of 0 to it: a hundredth of a column off, harmless.)</summary>
    private const float EdgeClearance = 0.05f;

    private static readonly Lazy<Half[]> Built = new(Build);

    public static Half[] Build()
    {
        var texels = new Half[2 * Heights * SunAngles * 4];
        var up = GroundBody.Planet.Up(Vector3.Zero);
        var east = GroundBody.Planet.East(Vector3.Zero);
        // Each row is its own work: the sky's light is the slow part (64 marches a texel).
        Parallel.For(0, Heights, row =>
        {
            var aboveSea = Scene.AtmosphereHeight * row / (Heights - 1);
            var at = GroundBody.Planet.Centre + up * (GroundBody.PlanetSeaRadius + aboveSea);
            for (var column = 0; column < SunAngles; column++)
            {
                var eye = LowEye(aboveSea, column);
                var low = MathF.Max(TransmittanceSunAt(aboveSea, column), Horizon(eye) + 1e-4f);
                var sun = up * low + east * MathF.Sqrt(MathF.Max(1f - low * low, 0f));
                // (Under the level the ray goes down from a height with room under it: see LowEye.)
                var from = GroundBody.Planet.Centre + up * (GroundBody.PlanetSeaRadius + eye);
                Put(texels, 0, row, column, Air.Transmittance(from, sun));
                var sunUp = SkyColumnSun(column);
                Put(texels, 1, row, column, Air.SkyLight(aboveSea, sunUp));
            }
        });
        return texels;
    }

    /// <summary>The cosine from up of the lowest sun that is over the sea's horizon, from <paramref name="aboveSea"/>: 0 at the sea, more negative the higher.</summary>
    public static float Horizon(float aboveSea)
    {
        var ratio = GroundBody.PlanetSeaRadius / (GroundBody.PlanetSeaRadius + MathF.Max(aboveSea, 0f));
        return -MathF.Sqrt(MathF.Max(1f - ratio * ratio, 0f));
    }

    /// <summary>The sky's column <paramref name="column"/>: the sun's cosine from up it stands for.</summary>
    private static float SkyColumnSun(int column)
    {
        var s = 2f * column / (SunAngles - 1) - 1f;
        return s * MathF.Abs(s);
    }

    private static float SkyColumn(float sunUp) =>
        (MathF.CopySign(MathF.Sqrt(MathF.Min(MathF.Abs(sunUp), 1f)), sunUp) + 1f) / 2f * (SunAngles - 1);

    /// <summary>The height a ray's closest approach is measured against: the eye's own, but not under <see cref="RoomFloor"/> (near the sea there is little room under the level, and the rows either side of a low eye then agree on what the columns mean).</summary>
    private static float Room(float aboveSea) => MathF.Max(aboveSea, RoomFloor);

    /// <summary>
    /// The eye's height for the transmittance's column <paramref name="column"/>: its own at and over the level, and under it (for an eye lower than <see cref="RoomFloor"/>, which has little room
    /// under the level) one that comes down to its own as the column nears the level, so the rows either side of a low eye agree on what a column means. (Which sun a column stands for is by <see cref="Room"/>'s geometry, as the lookup's is; only the light is from this eye, the sun held over its horizon.)
    /// </summary>
    private static float LowEye(float aboveSea, int column)
    {
        if (column >= LowColumns - 1 || aboveSea >= RoomFloor)
            return aboveSea;
        var z = column / (float)(LowColumns - 1);
        return aboveSea + (RoomFloor - aboveSea) * (1f - MathF.Pow(z, 0.75f));
    }

    /// <summary>The sun's cosine from up that the transmittance's column <paramref name="column"/> stands for, from <paramref name="aboveSea"/>.</summary>
    private static float TransmittanceSunAt(float aboveSea, int column)
    {
        if (column >= LowColumns - 1)
        {
            var w = (column - (LowColumns - 1)) / (float)(SunAngles - LowColumns);
            return w * w;
        }
        // Under the level: the ray's closest approach to the centre, from just over the sea up to the level's own height.
        var z = column / (double)(LowColumns - 1);
        var share = z * z / (z * z + (1 - z) * (1 - z));
        var (r, rSea) = (GroundBody.PlanetSeaRadius + (double)Room(aboveSea), (double)GroundBody.PlanetSeaRadius);
        var closest = Math.Min(rSea + EdgeClearance + Room(aboveSea) * share, r);
        return (float)-Math.Sqrt(Math.Max(1 - closest * closest / (r * r), 0));
    }

    /// <summary>The transmittance's column (as a texel position) of a sun at the cosine <paramref name="sunUp"/> from up at <paramref name="aboveSea"/> (above the horizon).</summary>
    private static float TransmittanceColumn(float aboveSea, float sunUp)
    {
        if (sunUp >= 0f)
            return (LowColumns - 1) + MathF.Sqrt(MathF.Min(sunUp, 1f)) * (SunAngles - LowColumns);
        var r = GroundBody.PlanetSeaRadius + Room(aboveSea);
        var clearance = r * MathF.Sqrt(MathF.Max(1f - sunUp * sunUp, 0f)) - GroundBody.PlanetSeaRadius;
        // Above the air only the chord round the closest approach is air, the one the top row describes, so the share is of the air's height at most.
        var share = Math.Clamp(clearance / MathF.Min(Room(aboveSea), Scene.AtmosphereHeight), 0f, 1f);
        return share >= 1f ? LowColumns - 1 : MathF.Sqrt(share / (1f - share)) / (1f + MathF.Sqrt(share / (1f - share))) * (LowColumns - 1);
    }

    private static void Put(Half[] texels, int layer, int row, int column, Vector3 c)
    {
        var i = ((layer * Heights + row) * SunAngles + column) * 4;
        (texels[i], texels[i + 1], texels[i + 2], texels[i + 3]) = ((Half)c.X, (Half)c.Y, (Half)c.Z, Half.One);
    }

    /// <summary>The table's value at a height above the sea and a sun cosine, as the shaders' bilinear lookup (clamped to the edges) reads it.</summary>
    public static Vector3 Look(int layer, float aboveSea, float sunUp)
    {
        if (layer == 0 && sunUp < Horizon(aboveSea))
            return Vector3.Zero; // the sea is in the way
        var texels = Built.Value;
        // In texel space (a texel's centre at i; the shader's coordinate is (i + 0.5) / size).
        var x = Math.Clamp(layer == 0 ? TransmittanceColumn(aboveSea, sunUp) : SkyColumn(sunUp), 0f, SunAngles - 1);
        var y = Math.Clamp(aboveSea / Scene.AtmosphereHeight, 0f, 1f) * (Heights - 1);
        var (x0, y0) = ((int)MathF.Floor(x), (int)MathF.Floor(y));
        var (x1, y1) = (Math.Min(x0 + 1, SunAngles - 1), Math.Min(y0 + 1, Heights - 1));
        var (fx, fy) = (x - x0, y - y0);
        Vector3 At(int column, int row)
        {
            var i = ((layer * Heights + row) * SunAngles + column) * 4;
            return new Vector3((float)texels[i], (float)texels[i + 1], (float)texels[i + 2]);
        }
        return Vector3.Lerp(Vector3.Lerp(At(x0, y0), At(x1, y0), fx), Vector3.Lerp(At(x0, y1), At(x1, y1), fx), fy);
    }
}
