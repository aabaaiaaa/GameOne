using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace GameOne.Rendering;

/// <summary>One vertex of any scene mesh. Layout must match the pipelines' vertex input.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MeshVertex(float3 position, float3 normal, float2 uv, float4 color, float textured)
{
    public float3 Position = position;
    public float3 Normal = normal;
    public float2 Uv = uv;
    public float4 Color = color;

    /// <summary>1 to multiply <see cref="Color"/> by the logo texture, 0 for a flat colour.</summary>
    public float Textured = textured;
}

/// <summary>A run of vertices within the shared vertex buffer.</summary>
internal readonly record struct MeshRange(int FirstVertex, int VertexCount);

/// <summary>Builds a triangle list from boxes, beams and turned (round) shapes.</summary>
internal sealed class MeshBuilder
{
    private readonly List<MeshVertex> _vertices = [];

    public MeshVertex[] ToArray() => [.. _vertices];

    /// <summary>How many vertices it has so far.</summary>
    public int Count => _vertices.Count;

    public void Vertex(Vector3 position, Vector3 normal, Vector4 color, Vector2 uv = default, float textured = 0f) =>
        _vertices.Add(new MeshVertex(F3(position), F3(normal), new float2(uv.X, uv.Y), new float4(color.X, color.Y, color.Z, color.W), textured));

    /// <summary>A flat quad, corners in order round its edge.</summary>
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector4 color)
    {
        Vertex(a, normal, color); Vertex(b, normal, color); Vertex(c, normal, color);
        Vertex(a, normal, color); Vertex(c, normal, color); Vertex(d, normal, color);
    }

    /// <summary>A box centred on <paramref name="centre"/>, with its three half-axes given as vectors (so it can be turned any way).</summary>
    public void Box(Vector3 centre, Vector3 x, Vector3 y, Vector3 z, Vector4 color)
    {
        foreach (var (axis, u, v) in (ReadOnlySpan<(Vector3, Vector3, Vector3)>)[(x, y, z), (y, z, x), (z, x, y)])
        foreach (var sign in (ReadOnlySpan<float>)[1f, -1f])
        {
            var face = centre + axis * sign;
            Quad(face - u - v, face + u - v, face + u + v, face - u + v, Vector3.Normalize(axis) * sign, color);
        }
    }

    /// <summary>A square girder from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public void Beam(Vector3 from, Vector3 to, float halfWidth, Vector4 color)
    {
        var along = (to - from) * 0.5f;
        var direction = Vector3.Normalize(along);
        var side = Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var other = Vector3.Cross(direction, side);
        Box((from + to) * 0.5f, side * halfWidth, along, other * halfWidth, color);
    }

    /// <summary>
    /// A round shape turned about the Y axis: a cone frustum from radius <paramref name="r0"/> at height
    /// <paramref name="y0"/> to <paramref name="r1"/> at <paramref name="y1"/>, smoothly shaded.
    /// Its normals face out, or in if <paramref name="inward"/> (for the inside of the engine bell).
    /// <paramref name="turn"/> is how much of the way round it goes, from +X towards +Z: 0.5 for a
    /// half (a fairing half's shell), its <paramref name="slices"/> spread over that. Its axis is at
    /// <paramref name="x"/>, <paramref name="z"/> (by default, the Y axis itself).
    /// </summary>
    public void Frustum(float y0, float y1, float r0, float r1, int slices, Vector4 color, bool inward = false, float turn = 1f, float x = 0f, float z = 0f)
    {
        var slope = r0 - r1; // the normal tilts up as the shape narrows
        for (var i = 0; i < slices; i++)
        {
            var (s0, c0) = MathF.SinCos(MathF.Tau * turn * i / slices);
            var (s1, c1) = MathF.SinCos(MathF.Tau * turn * (i + 1) / slices);
            Vector3 N(float c, float s) => Vector3.Normalize(new Vector3(c * (y1 - y0), slope, s * (y1 - y0))) * (inward ? -1 : 1);
            Vector3 P(float c, float s, float y, float r) => new(x + c * r, y, z + s * r);

            Vertex(P(c0, s0, y0, r0), N(c0, s0), color); Vertex(P(c1, s1, y0, r0), N(c1, s1), color); Vertex(P(c1, s1, y1, r1), N(c1, s1), color);
            Vertex(P(c0, s0, y0, r0), N(c0, s0), color); Vertex(P(c1, s1, y1, r1), N(c1, s1), color); Vertex(P(c0, s0, y1, r1), N(c0, s0), color);
        }
    }

    /// <summary>A flat disc at height <paramref name="y"/>, facing up or down.</summary>
    public void Disc(float y, float radius, int slices, Vector4 color, bool facingUp)
    {
        var normal = facingUp ? Vector3.UnitY : -Vector3.UnitY;
        for (var i = 0; i < slices; i++)
        {
            var (s0, c0) = MathF.SinCos(MathF.Tau * i / slices);
            var (s1, c1) = MathF.SinCos(MathF.Tau * (i + 1) / slices);
            Vertex(new Vector3(0, y, 0), normal, color);
            Vertex(new Vector3(c0 * radius, y, s0 * radius), normal, color);
            Vertex(new Vector3(c1 * radius, y, s1 * radius), normal, color);
        }
    }

    private static float3 F3(Vector3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>
/// The world's meshes: the pads, the service tower, the drone ship and the floes. (The rocket is in <see cref="RocketMesh"/>;
/// the terrain is built by its vertex shader, <see cref="Shaders.PlanetVertex"/>.)
/// </summary>
internal static class Meshes
{
    /// <summary>
    /// A launch pad, in its site's frame (drawn with <see cref="LaunchSite.Model"/>), its scorch
    /// marks painted on by the scene shader: Home's one block <paramref name="depth"/> deep, as its
    /// collider is; a far one on its <paramref name="footing"/> (<see cref="BuildFooting"/>). Each
    /// vertex's UV on the top is where it is across the pad, so the concrete is painted the same on
    /// every pad.
    /// </summary>
    public static MeshVertex[] BuildPad(Footing? footing, float depth)
    {
        if (footing is not null)
            return BuildFooting(footing);
        var mesh = new MeshBuilder();
        var h = depth / 2;
        mesh.Box(new Vector3(0, Scene.PadTop - h, 0), new Vector3(Scene.PadHalfSize, 0, 0), new Vector3(0, h, 0), new Vector3(0, 0, Scene.PadHalfSize), Vector4.One);
        var vertices = mesh.ToArray();
        foreach (ref var vertex in vertices.AsSpan())
            vertex.Uv = new float2(vertex.Position.X, vertex.Position.Z);
        return vertices;
    }

    /// <summary>
    /// A landing zone (<paramref name="plinth"/>): a round concrete slab, in the zone's own frame (it is
    /// drawn turned by its tilt and moved out to its centre), its target painted on by the scene
    /// shader, on its plinth: sides running straight down from the slab's rim to the foundation's
    /// bottom, which on a tilted zone makes the top's shadow, an ellipse. Each top vertex's UV is
    /// where it is across the slab, so the target is painted the same on every zone, however
    /// tilted; each side vertex's is how far round the rim and how high it is, so the concrete's
    /// slabs stand square on the side. A far zone stands on its footing instead
    /// (<see cref="Plinth.Footing"/>, <see cref="BuildFooting"/>).
    /// </summary>
    public static MeshVertex[] BuildLandingZone(Plinth plinth)
    {
        if (plinth.Footing is { } footing)
            return BuildFooting(footing);
        var mesh = new MeshBuilder();
        const int slices = 64;
        var r = Scene.LandingZoneRadius;
        var up = Vector3.Transform(plinth.Up, Quaternion.Inverse(plinth.Tilt)); // the sides' way, in the zone's frame
        var round = RoundInSlabs(r);
        for (var i = 0; i < slices; i++)
        {
            var (s0, c0) = MathF.SinCos(MathF.Tau * i / slices);
            var (s1, c1) = MathF.SinCos(MathF.Tau * (i + 1) / slices);
            Vector3 Rim(float c, float s) => new(c * r, Scene.PadTop, s * r);
            // Out from the side: across the rim's tangent and the sides' way.
            Vector3 N(float c, float s) => Vector3.Normalize(Vector3.Cross(up, new Vector3(-s, 0f, c)));
            void Side(Vector3 p, float c, float s, int slice) =>
                mesh.Vertex(p, N(c, s), Vector4.One, new Vector2(round * slice / slices, Vector3.Dot(p, up)));
            Vector3 top0 = Rim(c0, s0), top1 = Rim(c1, s1);
            Vector3 bottom0 = plinth.Under(top0), bottom1 = plinth.Under(top1);
            Side(bottom0, c0, s0, i); Side(bottom1, c1, s1, i + 1); Side(top1, c1, s1, i + 1);
            Side(bottom0, c0, s0, i); Side(top1, c1, s1, i + 1); Side(top0, c0, s0, i);
        }
        var sides = slices * 6;
        mesh.Disc(Scene.PadTop, r, slices, Vector4.One, facingUp: true);
        var vertices = mesh.ToArray();
        foreach (ref var vertex in vertices.AsSpan(sides))
            vertex.Uv = new float2(vertex.Position.X, vertex.Position.Z);
        return vertices;
    }

    /// <summary>
    /// How far round a rim of radius <paramref name="r"/> its side's U runs (see <see cref="InSlabs"/>).
    /// </summary>
    private static float RoundInSlabs(float r) => InSlabs(MathF.Tau * r);

    /// <summary>
    /// How far round an outline <paramref name="length"/> long its side's U runs: the nearest whole
    /// number of 4 m slabs (the concrete's grid) to its length (at least one), so where it wraps is
    /// a slab's joint, not a seam.
    /// </summary>
    private static float InSlabs(float length) => 4f * MathF.Max(1f, MathF.Round(length / 4f));

    /// <summary>
    /// A far pad or far zone on its <paramref name="footing"/>, in its frame (a pad's site's, a
    /// zone's own), drawn as the footing is: the top (a square or a disc), the slab's straight sides
    /// <see cref="Footing.Slab"/> down, then the taper narrowing in at 45° (under a square, four flat
    /// faces, an upside-down pyramid frustum; under a disc, a smooth cone frustum) to its bottom,
    /// <see cref="Footing.Depth"/> down, in the ground. (Its collider, <see cref="Footing.Boxes"/>,
    /// steps in to follow the same taper.) The top's and the bottom's UVs are where each point is
    /// across them, so the scene shader paints the top as it does any pad's or zone's. The sides'
    /// and the taper's V runs down them in metres along the concrete; their U runs round the
    /// outline, the same way round at every depth: the top's edge's length in whole slabs
    /// (<see cref="InSlabs"/>) all the way round, so the wrap is a slab's joint at every depth and U
    /// is continuous over the square's corners. So on the slab's sides the concrete's slabs are
    /// about square, and down the taper they narrow as it does, as a turned cone's panels do
    /// (keeping them square would mean fewer slabs round each ring lower down, so a seam where the
    /// count changes).
    /// </summary>
    private static MeshVertex[] BuildFooting(Footing footing)
    {
        var mesh = new MeshBuilder();
        var square = footing.Outline == FootingOutline.Square;
        float half = footing.Half, narrowest = footing.HalfWidthAt(footing.Depth);
        // A disc's rim in as many segments as a drum's, so it reads round; each of a square's sides in
        // about a slab's width at the top, so the U along its faces (a triangle's, interpolated) bends
        // its joints by little where the taper narrows.
        var perSide = (int)MathF.Max(1f, MathF.Round(2f * half / 4f));
        var segments = square ? 4 * perSide : 64;
        float top = Scene.PadTop, slabBottom = Scene.PadTop - footing.Slab, bottom = Scene.PadTop - footing.Depth;
        var round = InSlabs(square ? 8f * half : MathF.Tau * half);
        var slant = MathF.Sqrt(2f) * (footing.Depth - footing.Slab); // the taper's length down its face
        for (var k = 0; k < segments; k++)
        {
            // The way out of segment k at its end e (0 or 1): square to the square's side it is on;
            // round a disc, out through that point of its rim, so the cone is smoothly shaded.
            Vector3 Out(int e)
            {
                var (s, c) = square ? MathF.SinCos(MathF.Tau * (k / perSide) / 4) : MathF.SinCos(MathF.Tau * (k + e) / segments);
                return new Vector3(c, 0f, s);
            }
            // Segment k's end e at reach a from the middle and height y (on a square, that far along
            // its side from corner to corner; a point of a disc's rim), and its U: that far round.
            (Vector3 At, float U) End(int e, float a, float y)
            {
                var u = round * (k + e) / segments;
                if (!square)
                    return ((Out(e) * a) with { Y = y }, u);
                Vector3 Corner(int corner)
                {
                    var (s, c) = MathF.SinCos(MathF.Tau * (corner - 0.5f) / 4);
                    return new Vector3(c, 0f, s) * (a * MathF.Sqrt(2f));
                }
                var side = k / perSide;
                var along = (float)(k % perSide + e) / perSide;
                return (Vector3.Lerp(Corner(side), Corner(side + 1), along) with { Y = y }, u);
            }
            // A band of segment k's side, from its upper edge (height y0, reach a0) down to its lower
            // (y1, a1), its V v0 to v1.
            void Band(float y0, float a0, float v0, float y1, float a1, float v1, Vector3 n0, Vector3 n1)
            {
                var (p00, u00) = End(0, a0, y0); var (p10, u10) = End(1, a0, y0);
                var (p01, u01) = End(0, a1, y1); var (p11, u11) = End(1, a1, y1);
                void V(Vector3 p, Vector3 n, float u, float v) => mesh.Vertex(p, n, Vector4.One, new Vector2(u, v));
                V(p01, n0, u01, v1); V(p11, n1, u11, v1); V(p10, n1, u10, v0);
                V(p01, n0, u01, v1); V(p10, n1, u10, v0); V(p00, n0, u00, v0);
            }
            // A slice of the top or the bottom, fanned from the middle (its UVs are set below).
            void Fan(float y, float a, Vector3 n)
            {
                mesh.Vertex(new Vector3(0f, y, 0f), n, Vector4.One);
                mesh.Vertex(End(0, a, y).At, n, Vector4.One);
                mesh.Vertex(End(1, a, y).At, n, Vector4.One);
            }

            Fan(top, half, Vector3.UnitY);
            // The slab's side, straight down; V its height, as a drum's side's is.
            Band(top, half, top, slabBottom, half, slabBottom, Out(0), Out(1));
            // The taper, facing out and down at 45°; V going on down from the slab's, in metres along it.
            if (footing.Depth > footing.Slab)
                Band(slabBottom, half, slabBottom, bottom, narrowest, slabBottom - slant,
                    Vector3.Normalize(Out(0) - Vector3.UnitY), Vector3.Normalize(Out(1) - Vector3.UnitY));
            if (narrowest > 0f) // the taper's bottom, unless it comes to a point (on flat ground, the slab's)
                Fan(bottom, narrowest, -Vector3.UnitY);
        }
        var vertices = mesh.ToArray();
        foreach (ref var vertex in vertices.AsSpan())
            if (MathF.Abs(vertex.Normal.Y) > 0.99f) // the top and the bottom: where each point is across them
                vertex.Uv = new float2(vertex.Position.X, vertex.Position.Z);
        return vertices;
    }

    /// <summary>
    /// The drone ship (<see cref="Rafts.Ship"/>), in its own frame (x along its length, y up, z
    /// across, its origin the waterline's centre; it is drawn with its <see cref="Raft.Model"/>):
    /// the hull box the physics moves, from its keel <see cref="Scene.DroneShipDraft"/> under the
    /// waterline to its deck <see cref="Scene.DroneShipFreeboard"/> over it, so a booster drawn
    /// standing on the deck stands where it collides. Its sides and ends are dark steel, red under
    /// the waterline (which shows as the waves pass); its deck's top is painted by the scene shader,
    /// the target in its middle, each of its vertices' UV where it is across the deck, as a landing
    /// zone's are.
    /// </summary>
    public static MeshVertex[] BuildDroneShip()
    {
        var mesh = new MeshBuilder();
        Vector3 along = new(Scene.DroneShipHalfLength, 0f, 0f), across = new(0f, 0f, Scene.DroneShipHalfBeam);
        var steel = new Vector4(0.30f, 0.31f, 0.33f, 1f);
        var red = new Vector4(0.42f, 0.12f, 0.08f, 1f);
        void Face(Vector3 middle, Vector3 u, Vector3 v, Vector3 normal, Vector4 color) =>
            mesh.Quad(middle - u - v, middle + u - v, middle + u + v, middle - u + v, normal, color);

        foreach (var (bottom, top, color) in (ReadOnlySpan<(float, float, Vector4)>)[(-Scene.DroneShipDraft, 0f, red), (0f, Scene.DroneShipFreeboard, steel)])
        {
            var middle = new Vector3(0f, (bottom + top) / 2, 0f);
            var up = new Vector3(0f, (top - bottom) / 2, 0f);
            foreach (var sign in (ReadOnlySpan<float>)[1f, -1f])
            {
                Face(middle + across * sign, along, up, Vector3.UnitZ * sign, color); // a side
                Face(middle + along * sign, across, up, Vector3.UnitX * sign, color); // an end
            }
        }
        Face(new Vector3(0f, -Scene.DroneShipDraft, 0f), along, across, -Vector3.UnitY, red);
        const int hull = (2 * 4 + 1) * 6; // the two bands' sides and ends, and the bottom: six vertices a face
        Face(new Vector3(0f, Scene.DroneShipFreeboard, 0f), along, across, Vector3.UnitY, Vector4.One);
        var vertices = mesh.ToArray();
        foreach (ref var vertex in vertices.AsSpan(hull))
            vertex.Uv = new float2(vertex.Position.X, vertex.Position.Z);
        return vertices;
    }

    /// <summary>
    /// A floe (see <see cref="Floes"/>): a disc of ice with its side, radius 1, its top at y = 0
    /// and its bottom at y = −1, in a floe's own frame (x and z across it, y up). One mesh for
    /// every floe, each drawn scaled to its radius and thickness and lifted to its freeboard
    /// (<see cref="FloeModel"/>), so it is the cylinder its hull is. Its rim's points are on the
    /// circle, so the drawn disc never reaches past the collided one. Its top is white and its
    /// side a little bluer (the scene shader gives both the ice's colour); a floe tilted by the
    /// waves shows its side, and the side under the waterline as the waves pass. Each vertex's UV
    /// is where it is across the disc (the rim's are 1 out), for the scene shader's wet rim.
    /// </summary>
    public static MeshVertex[] BuildFloe()
    {
        const int slices = 24; // the rim's chords fall short of the circle by at most 1% (0.1 m on the largest floe)
        var mesh = new MeshBuilder();
        mesh.Frustum(-1f, 0f, 1f, 1f, slices, new Vector4(0.84f, 0.92f, 1f, 1f));
        mesh.Disc(0f, 1f, slices, Vector4.One, facingUp: true);
        mesh.Disc(-1f, 1f, slices, new Vector4(0.84f, 0.92f, 1f, 1f), facingUp: false);
        var vertices = mesh.ToArray();
        foreach (ref var vertex in vertices.AsSpan())
            vertex.Uv = new float2(vertex.Position.X, vertex.Position.Z); // where it is across the disc: the shader's wet rim
        return vertices;
    }

    /// <summary>
    /// The model <paramref name="floe"/>'s mesh (<see cref="BuildFloe"/>) is drawn with at
    /// <paramref name="pose"/>: scaled to its radius round and its thickness (freeboard and draft)
    /// tall, lifted so its top is its freeboard over the waterline, and posed as the floe is. The
    /// scale is the same across it and only stretches it up, so each of its normals stays
    /// straight out of its face (the scene shader normalises them).
    /// </summary>
    public static Matrix4x4 FloeModel(Raft floe, DeckPose pose) =>
        Matrix4x4.CreateScale(floe.HalfLength, floe.Freeboard + floe.Draft, floe.HalfLength)
        * Matrix4x4.CreateTranslation(0f, floe.Freeboard, 0f)
        * pose.Model;

    /// <summary>
    /// The service tower's girders, in its site's frame: a lattice, four corner posts tied together at
    /// each level with crossed braces on every face. White, to be drawn tinted with its site's
    /// colour (<see cref="LaunchSite.TowerColour"/>); its deck is <see cref="BuildTowerDeck"/>.
    /// </summary>
    public static MeshVertex[] BuildTower()
    {
        var mesh = new MeshBuilder();
        var girder = Vector4.One; // the site's colour, as it is drawn
        var c = Scene.TowerOffset; // in the pad's frame: each site's is drawn with its pad's model
        var w = Scene.TowerHalfWidth - 0.15f;
        var height = Scene.TowerHeight;
        const int levels = 8;

        ReadOnlySpan<Vector2> corners = [new(-w, -w), new(w, -w), new(w, w), new(-w, w)];
        Vector3 At(Vector2 corner, float y) => c + new Vector3(corner.X, y, corner.Y);

        foreach (var corner in corners)
            mesh.Beam(At(corner, 0), At(corner, height), 0.16f, girder);
        for (var level = 0; level < levels; level++)
        {
            float y0 = height * level / levels, y1 = height * (level + 1) / levels;
            for (var i = 0; i < corners.Length; i++)
            {
                var (a, b) = (corners[i], corners[(i + 1) % corners.Length]);
                mesh.Beam(At(a, y1), At(b, y1), 0.1f, girder);
                // Crossed braces, alternating which way round, as on a real lattice tower.
                mesh.Beam(At(level % 2 == 0 ? a : b, y0), At(level % 2 == 0 ? b : a, y1), 0.06f, girder);
                mesh.Beam(At(level % 2 == 0 ? b : a, y0), At(level % 2 == 0 ? a : b, y1), 0.06f, girder);
            }
        }
        return mesh.ToArray();
    }

    /// <summary>
    /// A lit place's fittings (<see cref="PadLights"/>), in its frame: its floodlight masts and their
    /// lamps' housings, and the tower lights' boxes, in dark steel. Its lamps' faces are <see cref="BuildLamps"/>.
    /// </summary>
    public static MeshVertex[] BuildFittings(LitPlace place)
    {
        var mesh = new MeshBuilder();
        var steel = new Vector4(0.32f, 0.33f, 0.35f, 1f);
        foreach (var mast in place.Masts)
        {
            var h = mast.Height / 2;
            mesh.Box(mast.Foot + new Vector3(0, h, 0), new Vector3(Scene.MastHalfWidth, 0, 0), new Vector3(0, h, 0), new Vector3(0, 0, Scene.MastHalfWidth), steel);
            // A crossbar at its head, carrying its two lamps.
            mesh.Box(mast.Foot + new Vector3(0, mast.Height - 0.2f, 0), new Vector3(0.6f, 0, 0), new Vector3(0, 0.12f, 0), new Vector3(0, 0, 0.6f), steel);
        }
        foreach (var lamp in place.Lamps)
            if (lamp.Kind == LampKind.Flood)
                mesh.Box(lamp.Position - lamp.Aim * 0.2f, new Vector3(0.3f, 0, 0), new Vector3(0, 0.3f, 0), new Vector3(0, 0, 0.3f), steel);
            else if (lamp.Kind == LampKind.Tower)
                mesh.Box(lamp.Position + new Vector3(0, 0.15f, 0), new Vector3(0.35f, 0, 0), new Vector3(0, 0.15f, 0), new Vector3(0, 0, 0.35f), steel);
        return mesh.ToArray();
    }

    /// <summary>A lit place's lamps' faces, in its frame, in the lamps' colour: drawn as lamps (<c>MaterialKind.Lamp</c>), shining as far as their place's lights are on.</summary>
    public static MeshVertex[] BuildLamps(LitPlace place)
    {
        var mesh = new MeshBuilder();
        foreach (var lamp in place.Lamps)
        {
            var (centre, size) = lamp.Kind switch
            {
                LampKind.Floor => (lamp.Position + new Vector3(0, 0.02f, 0), new Vector3(0.18f, 0.03f, 0.18f)), // set flush in the concrete
                LampKind.Flood => (lamp.Position + lamp.Aim * 0.12f, new Vector3(0.24f, 0.24f, 0.24f)),
                _ => (lamp.Position + new Vector3(0, 0.32f, 0), new Vector3(0.25f, 0.03f, 0.25f)), // a tower light's, looking up
            };
            mesh.Box(centre, new Vector3(size.X, 0, 0), new Vector3(0, size.Y, 0), new Vector3(0, 0, size.Z), new Vector4(Scene.LampColour, 1f));
        }
        return mesh.ToArray();
    }

    /// <summary>The service tower's deck on top, grey whatever colour its girders are (see <see cref="BuildTower"/>).</summary>
    public static MeshVertex[] BuildTowerDeck()
    {
        var mesh = new MeshBuilder();
        var grey = new Vector4(0.45f, 0.45f, 0.47f, 1f);
        var w = Scene.TowerHalfWidth - 0.15f;
        mesh.Box(Scene.TowerOffset + new Vector3(0, Scene.TowerHeight + 0.15f, 0), new Vector3(w + 0.6f, 0, 0), new Vector3(0, 0.15f, 0), new Vector3(0, 0, w + 0.6f), grey);
        return mesh.ToArray();
    }
}
