using System.Numerics;

namespace GameOne.Rendering;

/// <summary>
/// The rocket's meshes, each built in its own space (the origin at the bottom of the body, Y up it).
/// The booster: a charcoal body with BRUTAL-yellow stripes, grid fins, the engine bell, the BRUTAL
/// logo running up each side, and either a nose cone or (with an upper stage on top) an open
/// interstage. The upper stage: a shorter body under a pale payload fairing (or open on top, for a
/// satellite to sit on), with a smaller bell. The satellite: a gold box of a body with solar panels
/// and an antenna. The legs are a separate mesh, drawn four times, each swung out by <see cref="RocketGeometry.LegTransform"/>.
/// </summary>
internal static class RocketMesh
{
    /// <summary>The official white-wordmark logo, copied from Brutal/docs/branding at build time.</summary>
    public const string TexturePath = "Assets/brutal-logo-white.png";

    /// <summary>How many flat slices a round part is drawn in, all the way round (a fairing half, half of them over its half).</summary>
    internal const int Slices = 40;

    private static readonly Vector4 Charcoal = new(0.13f, 0.13f, 0.14f, 1f);
    /// <summary>BRUTAL yellow, #F1C40F.</summary>
    private static readonly Vector4 Yellow = new(241 / 255f, 196 / 255f, 15 / 255f, 1f);
    private static readonly Vector4 Grey = new(0.52f, 0.52f, 0.54f, 1f);
    private static readonly Vector4 BellMetal = new(0.30f, 0.27f, 0.25f, 1f);
    private static readonly Vector4 Soot = new(0.04f, 0.04f, 0.04f, 1f);
    private static readonly Vector4 Fairing = new(0.86f, 0.86f, 0.84f, 1f);
    private static readonly Vector4 Foil = new(0.80f, 0.62f, 0.22f, 1f);
    private static readonly Vector4 Panel = new(0.10f, 0.16f, 0.36f, 1f);

    // The logo up each side, reading from bottom to top: 8 m long, and as wide round the body as
    // the branding PNG's proportions (712 x 168) make it.
    private const float LogoBottom = 3.2f, LogoLength = 8f;

    /// <param name="nose">A nose cone on top; otherwise an open interstage, for an upper stage to sit in.</param>
    public static MeshVertex[] BuildBooster(bool nose)
    {
        var mesh = new MeshBuilder();
        var r = Scene.RocketRadius;
        var top = Scene.RocketBodyHeight;

        // The body, in bands: a grey engine skirt, charcoal tank, and a yellow stripe below the
        // interstage at the top.
        mesh.Frustum(0f, 0.6f, r, r, Slices, Grey);
        mesh.Frustum(0.6f, 1.0f, r, r, Slices, Yellow);
        mesh.Frustum(1.0f, top - 3.4f, r, r, Slices, Charcoal);
        mesh.Frustum(top - 3.4f, top - 2.8f, r, r, Slices, Yellow);
        mesh.Frustum(top - 2.8f, top, r, r, Slices, Charcoal);
        mesh.Disc(0f, r, Slices, Grey, facingUp: false);

        if (nose)
        {
            // An ogive-ish cone in three steps, with a yellow tip.
            var h = Scene.RocketNoseHeight;
            mesh.Frustum(top, top + h * 0.45f, r, r * 0.72f, Slices, Charcoal);
            mesh.Frustum(top + h * 0.45f, top + h * 0.85f, r * 0.72f, r * 0.3f, Slices, Charcoal);
            mesh.Frustum(top + h * 0.85f, top + h, r * 0.3f, 0f, Slices, Yellow);
        }
        else
        {
            // The interstage: an open sleeve, sooty inside, round the upper stage's bell, closed off
            // at the top of the tank.
            mesh.Frustum(top, top + Scene.InterstageHeight, r, r, Slices, Charcoal);
            mesh.Frustum(top, top + Scene.InterstageHeight, r * 0.97f, r * 0.97f, Slices, Soot, inward: true);
            mesh.Disc(top, r, Slices, Soot, facingUp: true);
        }

        // Grid fins near the top, folded out flat, between the legs.
        for (var i = 0; i < 4; i++)
        {
            var outward = new Vector3(MathF.Cos(i * MathF.PI / 2), 0, MathF.Sin(i * MathF.PI / 2));
            var tangent = Vector3.Cross(Vector3.UnitY, outward);
            mesh.Box(outward * (r + 0.55f) + new Vector3(0, top - 1.4f, 0), outward * 0.55f, new Vector3(0, 0.06f, 0), tangent * 0.55f, Grey);
        }

        // The engine bell, widening downwards, sooty inside.
        mesh.Frustum(-Scene.RocketBellHeight, 0f, Scene.RocketBellRadius, Scene.RocketBellRadius * 0.6f, Slices, BellMetal);
        mesh.Frustum(-Scene.RocketBellHeight, 0f, Scene.RocketBellRadius * 0.97f, Scene.RocketBellRadius * 0.58f, Slices, Soot, inward: true);

        AddLogo(mesh, facing: 0f);
        AddLogo(mesh, facing: MathF.PI);
        return mesh.ToArray();
    }

    /// <summary>The upper stage: a short body with yellow bands, a pale fairing over the payload (with <paramref name="nose"/>), and a small bell.</summary>
    public static MeshVertex[] BuildUpperStage(bool nose)
    {
        var mesh = new MeshBuilder();
        var r = Scene.RocketRadius;
        var top = Scene.UpperStageHeight;
        mesh.Frustum(0f, 0.4f, r, r, Slices, Grey);
        mesh.Frustum(0.4f, top - 0.6f, r, r, Slices, Charcoal);
        mesh.Frustum(top - 0.6f, top, r, r, Slices, Yellow);
        mesh.Disc(0f, r, Slices, Grey, facingUp: false);

        if (nose)
        {
            // The fairing: an ogive in three steps, with a yellow tip.
            var h = Scene.UpperNoseHeight;
            mesh.Frustum(top, top + h * 0.45f, r, r * 0.75f, Slices, Fairing);
            mesh.Frustum(top + h * 0.45f, top + h * 0.85f, r * 0.75f, r * 0.3f, Slices, Fairing);
            mesh.Frustum(top + h * 0.85f, top + h, r * 0.3f, 0f, Slices, Yellow);
        }
        else
            mesh.Disc(top, r, Slices, Grey, facingUp: true);

        mesh.Frustum(-Scene.UpperBellHeight, 0f, Scene.UpperBellRadius, Scene.UpperBellRadius * 0.55f, Slices, BellMetal);
        mesh.Frustum(-Scene.UpperBellHeight, 0f, Scene.UpperBellRadius * 0.97f, Scene.UpperBellRadius * 0.53f, Slices, Soot, inward: true);
        return mesh.ToArray();
    }

    /// <summary>
    /// The satellite: a squat gold body with a solar panel on each of four sides, an antenna mast
    /// with a dish on top, and a little engine bell underneath.
    /// </summary>
    public static MeshVertex[] BuildSatellite()
    {
        var mesh = new MeshBuilder();
        var r = Scene.SatelliteRadius;
        var top = Scene.SatelliteBodyHeight;
        mesh.Frustum(0f, top, r, r, 8, Foil); // eight-sided
        mesh.Disc(top, r, 8, Foil, facingUp: true);
        mesh.Disc(0f, r, 8, Grey, facingUp: false);
        for (var side = 0; side < 4; side++)
        {
            var (s, c) = MathF.SinCos(side * MathF.PI / 2);
            var outward = new Vector3(c, 0, s);
            var across = new Vector3(-s, 0, c);
            mesh.Box(outward * (r * 0.95f) + new Vector3(0, top / 2, 0), across * (r * 0.38f), new Vector3(0, top * 0.4f, 0), outward * 0.03f, Panel);
        }
        var mast = Scene.SatelliteNoseHeight;
        mesh.Frustum(top, top + mast * 0.7f, 0.06f, 0.06f, 8, Grey);
        mesh.Frustum(top + mast * 0.55f, top + mast, 0.05f, 0.45f, Slices, Fairing); // the dish, facing up
        mesh.Frustum(-Scene.SatelliteBellHeight, 0f, Scene.SatelliteBellRadius, Scene.SatelliteBellRadius * 0.55f, Slices, BellMetal);
        return mesh.ToArray();
    }

    /// <summary>
    /// The lander: a square body framed by a dark post at each corner (its legs hinge on them) and
    /// rails round its top. Its payload bay is at its foot, open at both X faces (where its doors
    /// go): a floor, gold-foiled walls on the Z faces, lined dark inside, and a roof, the deck, over
    /// it. On the deck four white tanks, between them a stand for the antenna on top, and the engine
    /// bell underneath.
    /// </summary>
    public static MeshVertex[] BuildLander()
    {
        var mesh = new MeshBuilder();
        var w = Scene.LanderHalfWidth;
        var top = Scene.LanderBodyHeight;
        var floor = Scene.LanderBayFloor;
        var roof = floor + Scene.LanderBayHeight;
        var inside = Scene.LanderBayHalfWidth;
        const float deck = 0.1f; // the roof's thickness
        var wall = w - inside;

        // The bay: its floor, its two walls (the Z faces, gold outside and lined dark inside) and its roof.
        mesh.Box(new Vector3(0, floor / 2, 0), new Vector3(w, 0, 0), new Vector3(0, floor / 2, 0), new Vector3(0, 0, w), Grey);
        foreach (var side in (ReadOnlySpan<float>)[1f, -1f])
        {
            mesh.Box(new Vector3(0, (floor + roof) / 2, side * (w - wall / 2)), new Vector3(w, 0, 0), new Vector3(0, (roof - floor) / 2, 0), new Vector3(0, 0, wall / 2), Foil);
            var z = side * (inside - 0.005f);
            mesh.Quad(new Vector3(-w, floor, z), new Vector3(w, floor, z), new Vector3(w, roof, z), new Vector3(-w, roof, z), new Vector3(0, 0, -side), Charcoal);
        }
        mesh.Box(new Vector3(0, roof + deck / 2, 0), new Vector3(w, 0, 0), new Vector3(0, deck / 2, 0), new Vector3(0, 0, w), Grey);

        // The tanks on the deck, each a cylinder with a domed top.
        var tankTop = top - 0.05f;
        foreach (var (x, z) in (ReadOnlySpan<(float, float)>)[(0.55f, 0.55f), (-0.55f, 0.55f), (-0.55f, -0.55f), (0.55f, -0.55f)])
        {
            mesh.Frustum(roof + deck, tankTop - 0.2f, 0.45f, 0.45f, 16, Fairing, x: x, z: z);
            mesh.Frustum(tankTop - 0.2f, tankTop, 0.45f, 0.25f, 16, Fairing, x: x, z: z);
            mesh.Frustum(tankTop, tankTop, 0.25f, 0f, 16, Fairing, x: x, z: z);
        }

        // The frame: a post up each corner, and rails round the top.
        foreach (var (x, z) in (ReadOnlySpan<(float, float)>)[(w, w), (-w, w), (-w, -w), (w, -w)])
        {
            var post = new Vector3(x, 0, z) * 0.97f;
            mesh.Box(post + new Vector3(0, top / 2, 0), new Vector3(0.06f, 0, 0), new Vector3(0, top / 2, 0), new Vector3(0, 0, 0.06f), Charcoal);
        }
        foreach (var side in (ReadOnlySpan<float>)[1f, -1f])
        {
            mesh.Box(new Vector3(side * w * 0.97f, top - 0.04f, 0), new Vector3(0.05f, 0, 0), new Vector3(0, 0.04f, 0), new Vector3(0, 0, w), Yellow);
            mesh.Box(new Vector3(0, top - 0.04f, side * w * 0.97f), new Vector3(w, 0, 0), new Vector3(0, 0.04f, 0), new Vector3(0, 0, 0.05f), Yellow);
        }

        // The antenna's stand, between the tanks, and the antenna on it.
        mesh.Frustum(roof + deck, top, 0.12f, 0.12f, 8, Grey);
        var mast = Scene.LanderAntennaHeight;
        mesh.Frustum(top, top + mast * 0.8f, 0.04f, 0.04f, 8, Grey);
        mesh.Frustum(top + mast * 0.6f, top + mast, 0.04f, 0.2f, Slices, Fairing); // the dish, facing up

        // The engine bell, widening downwards, sooty inside.
        mesh.Frustum(-Scene.LanderBellHeight, 0f, Scene.LanderBellRadius, Scene.LanderBellRadius * 0.55f, Slices, BellMetal);
        mesh.Frustum(-Scene.LanderBellHeight, 0f, Scene.LanderBellRadius * 0.97f, Scene.LanderBellRadius * 0.53f, Slices, Soot, inward: true);
        return mesh.ToArray();
    }

    /// <summary>
    /// Panel <paramref name="panel"/> of one of the lander's bay doors' ramps (0 the door itself),
    /// in panel space (see <see cref="RocketGeometry.PanelTransform"/>): a gold-foiled panel
    /// (the bay's width), its hinge along its bottom
    /// edge, its outer face at X = 0 and its thickness inwards; on its inner face, the ramp's grey
    /// deck once it is open, and on the last a yellow strip along its far edge (where it meets the
    /// ground). Folded shut, the last panel's foil is outermost.
    /// </summary>
    public static MeshVertex[] BuildLanderPanel(int panel)
    {
        var mesh = new MeshBuilder();
        var (h, t, w) = (Scene.LanderDoorHeight, Scene.LanderDoorThickness, RocketGeometry.PanelHalfWidth);
        mesh.Box(new Vector3(-t / 2, h / 2, 0), new Vector3(t / 2, 0, 0), new Vector3(0, h / 2, 0), new Vector3(0, 0, w), Foil);
        var x = -t - 0.005f; // just proud of the inner face
        var deck = panel == RocketGeometry.PanelCount - 1 ? h - 0.12f : h;
        mesh.Quad(new Vector3(x, 0, w * 0.95f), new Vector3(x, deck, w * 0.95f), new Vector3(x, deck, -w * 0.95f), new Vector3(x, 0, -w * 0.95f), -Vector3.UnitX, Grey);
        if (deck < h)
            mesh.Quad(new Vector3(x, deck, w), new Vector3(x, h, w), new Vector3(x, h, -w), new Vector3(x, deck, -w), -Vector3.UnitX, Yellow);
        return mesh.ToArray();
    }

    /// <summary>
    /// Half of a payload's fairing, of <paramref name="shape"/>: a pale shell round the +Z side of
    /// the axis, straight up past the payload and then an ogive with a yellow tip, like the upper
    /// stage's own fairing; a yellow band at its foot. One wider than the rocket (the lander's) has a
    /// short cone under its foot, in at 45° to the rocket's radius, closing it onto the stage under
    /// it. Two of them, the second turned half a turn, make the whole fairing.
    /// </summary>
    public static MeshVertex[] BuildFairingHalf(FairingGeometry shape)
    {
        var mesh = new MeshBuilder();
        var r = shape.Radius;
        var top = shape.Height;
        var h = shape.NoseHeight;
        const int slices = Slices / 2;
        if (r > Scene.RocketRadius)
            mesh.Frustum(Scene.RocketRadius - r, 0f, Scene.RocketRadius, r, slices, Fairing, turn: 0.5f);
        mesh.Frustum(0f, 0.3f, r, r, slices, Yellow, turn: 0.5f);
        mesh.Frustum(0.3f, top, r, r, slices, Fairing, turn: 0.5f);
        mesh.Frustum(top, top + h * 0.45f, r, r * 0.75f, slices, Fairing, turn: 0.5f);
        mesh.Frustum(top + h * 0.45f, top + h * 0.85f, r * 0.75f, r * 0.3f, slices, Fairing, turn: 0.5f);
        mesh.Frustum(top + h * 0.85f, top + h, r * 0.3f, 0f, slices, Yellow, turn: 0.5f);
        return mesh.ToArray();
    }

    /// <summary>
    /// The logo on a strip wrapped round the body, centred on the side facing <paramref name="facing"/>
    /// radians round from +Z, standing just proud of the surface so it doesn't flicker into it.
    /// </summary>
    private static void AddLogo(MeshBuilder mesh, float facing)
    {
        const int segments = 16;
        var radius = Scene.RocketRadius + 0.015f;
        var arc = LogoLength * (168f / 712f) / radius; // radians round the body
        var white = Vector4.One;

        // Reading bottom to top, so the image's left edge is at the bottom and its top edge is on
        // the left as you face it (towards lower angles).
        Vector3 P(float angle, float y) => new(MathF.Sin(angle) * radius, y, MathF.Cos(angle) * radius);
        Vector3 N(float angle) => new(MathF.Sin(angle), 0, MathF.Cos(angle));
        for (var i = 0; i < segments; i++)
        {
            float v0 = (float)i / segments, v1 = (float)(i + 1) / segments;
            float a0 = facing + (v0 - 0.5f) * arc, a1 = facing + (v1 - 0.5f) * arc;
            float y0 = LogoBottom, y1 = LogoBottom + LogoLength;
            mesh.Vertex(P(a0, y0), N(a0), white, new Vector2(0, v0), 1f);
            mesh.Vertex(P(a1, y0), N(a1), white, new Vector2(0, v1), 1f);
            mesh.Vertex(P(a1, y1), N(a1), white, new Vector2(1, v1), 1f);
            mesh.Vertex(P(a0, y0), N(a0), white, new Vector2(0, v0), 1f);
            mesh.Vertex(P(a1, y1), N(a1), white, new Vector2(1, v1), 1f);
            mesh.Vertex(P(a0, y1), N(a0), white, new Vector2(1, v0), 1f);
        }
    }

    /// <summary>
    /// The two boxes of a landing leg of <paramref name="shape"/> as drawn (<see cref="BuildLeg"/>),
    /// in leg space, each its centre and half its size: the leg, and a slimmer strut alongside,
    /// like its damper, each as wide as the shape says (what else must clear a leg, the lander's
    /// bay doors, is tested against them).
    /// </summary>
    public static ((Vector3 Centre, Vector3 Half) Leg, (Vector3 Centre, Vector3 Half) Strut) LegBoxes(LegGeometry shape) =>
        ((new Vector3(0, shape.Length / 2, 0), new Vector3(shape.HalfDepth, shape.Length / 2, shape.HalfWidth)),
         (new Vector3(-shape.StrutInward, shape.Length * 0.4f, 0), new Vector3(shape.StrutHalfWidth, shape.Length * 0.38f, shape.StrutHalfWidth)));

    /// <summary>
    /// One landing leg of <paramref name="shape"/> in leg space: the hinge at the origin and the leg running up +Y (as it lies
    /// when stowed), with its outward side towards +X. The yellow foot pad is angled so that it sits
    /// flat on the ground once the leg has swung out.
    /// </summary>
    public static MeshVertex[] BuildLeg(LegGeometry shape)
    {
        var mesh = new MeshBuilder();
        var length = shape.Length;
        var (leg, strut) = LegBoxes(shape);
        mesh.Box(leg.Centre, new Vector3(leg.Half.X, 0, 0), new Vector3(0, leg.Half.Y, 0), new Vector3(0, 0, leg.Half.Z), Grey);
        mesh.Box(strut.Centre, new Vector3(strut.Half.X, 0, 0), new Vector3(0, strut.Half.Y, 0), new Vector3(0, 0, strut.Half.Z), Charcoal);

        // Undo the deployed swing (about -Z in leg space), so the pad is level when deployed.
        var level = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.DegreesToRadians(shape.DeployedDegrees));
        var padUp = Vector3.Transform(Vector3.UnitY, level);
        var padOut = Vector3.Transform(Vector3.UnitX, level);
        var thickness = RocketGeometry.FootThickness;
        var pad = shape.FootHalfWidth;
        mesh.Box(new Vector3(0, length, 0) - padUp * (thickness / 2), padOut * pad, padUp * (thickness / 2), new Vector3(0, 0, pad), Yellow);
        return mesh.ToArray();
    }
}
