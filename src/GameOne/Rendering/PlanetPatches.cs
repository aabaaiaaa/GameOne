using System.Numerics;

namespace GameOne.Rendering;

/// <summary>
/// A square of one of the cube's faces to draw the terrain in: from (<see cref="U0"/>, <see cref="V0"/>)
/// and <see cref="Size"/> across, in face coordinates (each face is [-1, 1] each way, so a whole
/// face is size 2, level 0). <see cref="Water"/>: the ground somewhere in it may be under sea
/// level, so the sea's surface is drawn over it too.
/// </summary>
internal readonly record struct PlanetPatch(int Face, float U0, float V0, float Size, int Level, bool Water);

/// <summary>
/// A body whose ground is drawn in patches (<see cref="PlanetPatches.Select(in PatchBody, Vector3, float, List{PlanetPatch}, bool, Matrix4x4?)"/>):
/// the planet, or the moon up close.
/// </summary>
/// <param name="Centre">Its centre, in the ground's frame (world space).</param>
/// <param name="Radius">The sphere a patch's side is measured on (the planet's through Home's pad, the moon's datum).</param>
/// <param name="Datum">How far its heights' zero is from its centre (the planet's calm sea, the moon's datum).</param>
/// <param name="Map">Its baked map, read only through its min/max pyramid.</param>
/// <param name="ToBody">What turns a direction in the ground's frame into its own axes, its map's (the identity for the planet, whose axes they are).</param>
/// <param name="DetailReach">The most its fine detail raises or lowers the ground from the map's height.</param>
/// <param name="HasSea">Whether ground under its heights' zero has water over it, up to <paramref name="HighestWave"/>.</param>
/// <param name="HighestWave">The highest its waves reach above the zero (and as low below it).</param>
/// <param name="MaxLevel">How many times a face of it is split at most, under the camera.</param>
/// <param name="Relief">How much of its heights is drawn (1 all; 0 its datum's sphere, as the sky draws the moon).</param>
internal readonly record struct PatchBody(
    Vector3 Centre, float Radius, float Datum, TerrainMap Map, Quaternion ToBody, float DetailReach,
    bool HasSea, float HighestWave, int MaxLevel, float Relief)
{
    /// <summary>The planet, as every view draws it.</summary>
    public static PatchBody Planet => new(
        GroundBody.Planet.Centre, GroundBody.Planet.Radius, GroundBody.Planet.SeaRadius, GroundBody.Planet.Map, Quaternion.Identity,
        PlanetPatches.DetailReach, true, GroundBody.Planet.HighestWave, Scene.TerrainMaxLevel, 1f);

    /// <summary>
    /// The moon, its centre at <paramref name="centre"/> and turned by <paramref name="orientation"/>
    /// (<see cref="SolarSystem.MoonOrientation"/>: its own axes into the ground's frame), its
    /// relief drawn by <paramref name="relief"/> (<see cref="MoonRelief"/>).
    /// </summary>
    public static PatchBody Moon(Vector3 centre, Quaternion orientation, float relief) => new(
        centre, Scene.MoonRadius, Scene.MoonRadius, MoonGround.Map, Quaternion.Conjugate(orientation),
        MoonDetailReach, false, 0f, Scene.MoonPatchMaxLevel, relief);

    /// <summary>The most the moon's fine detail raises or lowers its ground (its octaves all added up).</summary>
    public static readonly float MoonDetailReach = Scene.MoonDetail.Sum(o => o.Amplitude);

    /// <summary>Whether a view whose camera is <paramref name="distance"/> from the moon's centre draws it as a mesh (else the sky's ray-cast alone draws it).</summary>
    public static bool DrawsMoon(float distance) => distance < Scene.MoonMeshReach;

    /// <summary>
    /// How much of the moon's relief its mesh draws from <paramref name="distance"/> from its
    /// centre: all of it within <see cref="Scene.MoonReliefWithin"/>, easing (smoothly, with the
    /// distance) to none at <see cref="Scene.MoonMeshReach"/>, where the mesh is the datum's sphere
    /// the sky's ray-cast draws, so the handover from one to the other doesn't show.
    /// </summary>
    public static float MoonRelief(float distance)
    {
        var t = Math.Clamp((Scene.MoonMeshReach - distance) / (Scene.MoonMeshReach - Scene.MoonReliefWithin), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Whether it is turned from the ground's frame at all (the planet is not: its patches are worked out as they always were).</summary>
    public bool Turned => ToBody != Quaternion.Identity;

    /// <summary>
    /// The lowest anything drawn of it reaches from its centre: the sea's troughs, or with no sea its
    /// lowest ground. The horizon is that sphere's edge (a sphere inside the ground hides no more than the ground does).
    /// </summary>
    public float Lowest => HasSea ? Datum - HighestWave : Datum + MathF.Min(0f, (Map.MinHeight - DetailReach) * Relief);
}

/// <summary>
/// Which patches a view draws the terrain in: the one place that chooses them (the drawing takes
/// whatever list it is given). A simple level of detail, on purpose: each face is a quadtree, and
/// a patch splits in four while the camera is nearer to it than its side times a ratio, so the
/// squares look much the same size on the screen near and far. Patches of different sizes meet
/// with cracks, which their skirts hide (see <c>Shaders.PlanetVertex</c>).
/// </summary>
/// <remarks>
/// It runs for every view every frame, so it allocates nothing beyond the list it fills, and
/// reads the map only through its min/max pyramid (<see cref="TerrainMap.Range"/>).
/// </remarks>
internal static class PlanetPatches
{
    // Each face's outward direction and its across and up directions: CubeSphere's (which keeps them private).
    private static readonly Vector3[] Normals =
    [
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    ];

    private static readonly Vector3[] Acrosses =
    [
        new(0, 0, -1), new(0, 0, 1), new(1, 0, 0), new(1, 0, 0), new(1, 0, 0), new(-1, 0, 0),
    ];

    private static readonly Vector3[] Ups =
    [
        new(0, 1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1), new(0, 1, 0), new(0, 1, 0),
    ];

    /// <summary>
    /// The most the fine detail can raise or lower the ground from the map's height (the largest
    /// kind's octaves all added up), so a patch's range from the map can be widened to the truth.
    /// </summary>
    public static readonly float DetailReach = Scene.TerrainDetail.Max(octaves => octaves.Sum(o => o.Amplitude));

    /// <summary>
    /// The lowest and highest elevations drawn in a patch, its skirt aside: the ground's range from
    /// the map widened by the detail, and, if it has water (the ground somewhere in it is under sea
    /// level), the sea's surface from its lowest trough to its highest crest.
    /// </summary>
    public static (float Low, float High, bool Water) Heights(TerrainMap map, int face, float u0, float v0, float size) =>
        Heights(PatchBody.Planet with { Map = map }, face, u0, v0, size);

    /// <summary>
    /// <see cref="Heights(TerrainMap, int, float, float, float)"/> for <paramref name="body"/>: its
    /// map's range and detail scaled by the relief drawn, and water only where it has a sea.
    /// </summary>
    public static (float Low, float High, bool Water) Heights(in PatchBody body, int face, float u0, float v0, float size)
    {
        var (min, max) = body.Map.Range(face, u0, v0, size);
        min = (min - body.DetailReach) * body.Relief;
        max = (max + body.DetailReach) * body.Relief;
        var water = body.HasSea && min < 0f;
        if (!water)
            return (min, max, false);
        return (MathF.Min(min, -body.HighestWave), MathF.Max(max, body.HighestWave), true);
    }

    /// <summary>
    /// Fills <paramref name="into"/> (cleared first) with the patches to draw from
    /// <paramref name="camera"/> (world space), split while the camera is nearer than
    /// <paramref name="lodRatio"/> sides. With <paramref name="cull"/>, patches wholly below the
    /// camera's horizon are left out, and, given the view's <paramref name="viewProjection"/>
    /// (row vectors, world space), those wholly outside its sides.
    /// </summary>
    public static void Select(Vector3 camera, float lodRatio, List<PlanetPatch> into, bool cull = true, Matrix4x4? viewProjection = null) =>
        Select(PatchBody.Planet, camera, lodRatio, into, cull, viewProjection);

    /// <summary>
    /// <see cref="Select(Vector3, float, List{PlanetPatch}, bool, Matrix4x4?)"/> for
    /// <paramref name="body"/>: the camera turned into its own axes, so the patches are its map's
    /// (the drawing turns each one's directions out again), and the frustum's test turned back.
    /// </summary>
    public static void Select(in PatchBody body, Vector3 camera, float lodRatio, List<PlanetPatch> into, bool cull = true, Matrix4x4? viewProjection = null)
    {
        into.Clear();
        var fromCentre = camera - body.Centre;
        if (body.Turned)
            fromCentre = Vector3.Transform(fromCentre, body.ToBody);
        var r = fromCentre.Length();
        var direction = fromCentre / r;

        // Nothing drawn is lower than the sea's surface (the seabed is under the water), or with no
        // sea its lowest ground, so that sphere is what the horizon is the edge of.
        var lowest = body.Lowest;
        var view = new View(body, fromCentre, direction, r - body.Datum, lodRatio, cull,
            MathF.Acos(lowest / MathF.Max(r, lowest)), lowest, cull && viewProjection.HasValue, Frustum.Of(viewProjection ?? default));

        for (var face = 0; face < 6; face++)
        {
            // Where the camera is in this face's coordinates, if the face went on round the
            // planet: the nearest point of a patch is near this clamped into it.
            var along = Vector3.Dot(direction, Normals[face]);
            var cu = MathF.Atan2(Vector3.Dot(direction, Acrosses[face]), along) * 4f / MathF.PI;
            var cv = MathF.Atan2(Vector3.Dot(direction, Ups[face]), along) * 4f / MathF.PI;
            Visit(view, face, cu, cv, -1f, -1f, 2f, 0, into);
        }
    }

    /// <summary>What every patch's choice needs to know about the camera.</summary>
    private readonly record struct View(
        PatchBody Body, Vector3 FromCentre, Vector3 Direction, float Altitude, float LodRatio, bool Cull, float Horizon, float Lowest,
        bool UseFrustum, Frustum Frustum);

    /// <summary>
    /// The four sides of a view's frustum, as planes facing in (xyz the normal, w the offset), in
    /// world space. (Not the near and far planes: the near one is right at the camera, and the
    /// horizon is nearer than the far one.)
    /// </summary>
    private readonly record struct Frustum(Vector4 Left, Vector4 Right, Vector4 Bottom, Vector4 Top)
    {
        public static Frustum Of(in Matrix4x4 m)
        {
            // Row vectors: clip = (p, 1) * m, so each clip coordinate is p dotted with a column.
            var x = new Vector4(m.M11, m.M21, m.M31, m.M41);
            var y = new Vector4(m.M12, m.M22, m.M32, m.M42);
            var w = new Vector4(m.M14, m.M24, m.M34, m.M44);
            return new Frustum(Plane(w + x), Plane(w - x), Plane(w + y), Plane(w - y));
        }

        private static Vector4 Plane(Vector4 p)
        {
            var length = new Vector3(p.X, p.Y, p.Z).Length();
            return length > 0f ? p / length : p;
        }

        /// <summary>Whether a sphere is wholly outside one of the sides.</summary>
        public bool Excludes(Vector3 centre, float radius) =>
            Outside(Left, centre, radius) || Outside(Right, centre, radius) || Outside(Bottom, centre, radius) || Outside(Top, centre, radius);

        private static bool Outside(Vector4 plane, Vector3 centre, float radius) =>
            plane.X * centre.X + plane.Y * centre.Y + plane.Z * centre.Z + plane.W < -radius;
    }

    private static void Visit(in View view, int face, float cu, float cv, float u0, float v0, float size, int level, List<PlanetPatch> into)
    {
        var body = view.Body;
        var (min, max, water) = Heights(body, face, u0, v0, size);
        var side = size * body.Radius * MathF.PI / 4f;

        if (view.Cull)
        {
            // The patch as a cap round its centre reaching its farthest corner (its edges are
            // great circles, so no point of it is farther), from its lowest (its skirt's foot) to
            // its highest (a wave's crest, if it has water).
            var half = size * 0.5f;
            var centre = CubeSphere.FromFace(face, u0 + half, v0 + half);
            var reach = MathF.Max(
                MathF.Max(Angle(centre, CubeSphere.FromFace(face, u0, v0)), Angle(centre, CubeSphere.FromFace(face, u0 + size, v0))),
                MathF.Max(Angle(centre, CubeSphere.FromFace(face, u0, v0 + size)), Angle(centre, CubeSphere.FromFace(face, u0 + size, v0 + size))));
            var top = body.Datum + max;
            if (BelowHorizon(view, centre, reach, top))
                return;
            if (view.UseFrustum)
            {
                // A sphere round it: every point is within the cap's chord of the centre line,
                // and within half the range of heights of the middle one.
                var bottom = body.Datum + min - Scene.TerrainSkirt * side;
                var radius = 2f * top * MathF.Sin(reach * 0.5f) + (top - bottom) * 0.5f;
                var middle = centre * ((top + bottom) * 0.5f);
                if (body.Turned)
                    middle = Vector3.Transform(middle, Quaternion.Conjugate(body.ToBody));
                if (view.Frustum.Excludes(body.Centre + middle, radius))
                    return;
            }
        }

        // The patch's nearest point to the camera: across it, the camera's own place clamped into
        // it; up and down, the camera's height clamped into the patch's range of heights.
        if (level < body.MaxLevel)
        {
            var nearest = CubeSphere.FromFace(face, Math.Clamp(cu, u0, u0 + size), Math.Clamp(cv, v0, v0 + size))
                * (body.Datum + Math.Clamp(view.Altitude, min, max));
            if (Vector3.Distance(view.FromCentre, nearest) < side * view.LodRatio)
            {
                var half = size * 0.5f;
                Visit(view, face, cu, cv, u0, v0, half, level + 1, into);
                Visit(view, face, cu, cv, u0 + half, v0, half, level + 1, into);
                Visit(view, face, cu, cv, u0, v0 + half, half, level + 1, into);
                Visit(view, face, cu, cv, u0 + half, v0 + half, half, level + 1, into);
                return;
            }
        }

        into.Add(new PlanetPatch(face, u0, v0, size, level, water));
    }

    /// <summary>
    /// Whether all of a patch (a cap round <paramref name="centre"/>, <paramref name="reach"/>
    /// radians across, up to <paramref name="top"/> from the planet's centre) is hidden behind the
    /// body's curve: none of the cap could show.
    /// </summary>
    private static bool BelowHorizon(in View view, Vector3 centre, float reach, float top)
    {
        var away = Angle(view.Direction, centre) - reach;
        // How far round the planet the camera sees, and a peak of the patch's height is seen from.
        var over = MathF.Acos(MathF.Min(view.Lowest / top, 1f));
        return away > view.Horizon + over;
    }

    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f));
}
