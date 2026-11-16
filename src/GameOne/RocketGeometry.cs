using System.Numerics;

namespace GameOne;

/// <summary>
/// The booster's shape where more than one part of the game needs it: the physics (its collider),
/// the flight rules (which parts touch the ground) and the renderer (where to draw the legs).
/// Everything is in the booster's own space: the origin is the bottom of the body, Y runs up it.
/// </summary>
internal static class RocketGeometry
{
    public const int LegCount = 4;

    /// <summary>Which way leg <paramref name="leg"/> faces outwards, round the body (legs at 45°, between the fins).</summary>
    public static Vector3 LegOutward(int leg)
    {
        var angle = MathF.PI / 4 + leg * MathF.Tau / LegCount;
        return new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
    }

    /// <summary>
    /// Leg <paramref name="leg"/> of <paramref name="legs"/>'s transform, from leg space (the hinge at the origin, the leg along
    /// +Y) into the stage's space, <paramref name="deploy"/> of the way (0..1) from stowed to deployed,
    /// and, deployed, <paramref name="extension"/> of its stroke (0..1) out from where it is pushed
    /// right up (where the collider's feet are): in the air it hangs at full stroke, and it stands
    /// as far out as its foot's own ground needs (see <see cref="LegGeometry.Stroke"/>). Out along
    /// its stroke, the foot is straight down the body from where it is pushed right up, where the
    /// physics measures its ground: the leg swings a little further down to point at it, and is
    /// drawn longer to reach it, as a crush leg's stroke lengthens it. (Swung further down alone,
    /// as a hinge, far enough to drop the full stroke, the foot would swing well inwards, away
    /// from the ground the physics measured.)
    /// </summary>
    public static Matrix4x4 LegTransform(LegGeometry legs, int leg, float deploy, float extension = 0f)
    {
        var outward = LegOutward(leg);
        var hingeAxis = Vector3.Cross(Vector3.UnitY, outward); // the leg swings out and down about this
        var (sin, cos) = MathF.SinCos(float.DegreesToRadians(legs.DeployedDegrees));
        var (across, along) = (legs.Length * sin, legs.Length * cos - legs.Stroke * Math.Clamp(extension, 0f, 1f));
        var eased = Ease(deploy);
        var swing = Quaternion.CreateFromAxisAngle(hingeAxis, MathF.Atan2(across, along) * eased);
        var stretch = 1f + (MathF.Sqrt(across * across + along * along) / legs.Length - 1f) * eased;
        var hinge = outward * legs.HingeRadius + new Vector3(0, legs.HingeHeight, 0);
        // The leg's own X is its outward direction, so its foot pad's flat face lines up.
        var face = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.Atan2(outward.Z, outward.X));
        return Matrix4x4.CreateScale(1f, stretch, 1f) * Matrix4x4.CreateFromQuaternion(face) * Matrix4x4.CreateFromQuaternion(swing) * Matrix4x4.CreateTranslation(hinge);
    }

    /// <summary>The bottom of leg <paramref name="leg"/>'s foot, of <paramref name="legs"/>, deployed and pushed right up (where the collider's foot is), in the stage's space (worked out once, by <see cref="LegGeometry.Foot"/>).</summary>
    public static Vector3 Foot(LegGeometry legs, int leg) => legs.Foot(leg);

    /// <summary>How thick the feet's pads are.</summary>
    public const float FootThickness = 0.15f;

    /// <summary>Points on the bottom rim of <paramref name="stage"/>'s engine bell, in its own space.</summary>
    public static Vector3 BellRim(Stage stage, int i, int count)
    {
        var angle = i * MathF.Tau / count;
        return new Vector3(MathF.Cos(angle) * stage.BellRadius, -stage.BellHeight, MathF.Sin(angle) * stage.BellRadius);
    }

    /// <summary>
    /// How far up a stack the next stage's own origin (the bottom of its body) sits above this
    /// stage's: a booster carries the next stage on its interstage, which hides that stage's bell;
    /// an upper stage carries it on top of its body, in place of its fairing.
    /// </summary>
    public static float StackStep(Stage stage) => stage.Kind == StageKind.Booster
        ? Scene.RocketBodyHeight + Scene.InterstageHeight
        : Scene.UpperStageHeight;

    /// <summary>
    /// Where each of <paramref name="vehicle"/>'s stages from <paramref name="first"/> up sits in the
    /// stack's space (whose origin is the bottom of stage <paramref name="first"/>'s body): the
    /// height of each stage's own origin.
    /// </summary>
    public static float StageBase(Vehicle vehicle, int first, int stage)
    {
        var y = 0f;
        for (var i = first; i < stage; i++)
            y += StackStep(vehicle.Stages[i]);
        return y;
    }

    /// <summary>From the bottom of stage <paramref name="first"/>'s body to the top of stage <paramref name="last"/> (by default, the top of the stack).</summary>
    public static float StackHeight(Vehicle vehicle, int first, int? last = null)
    {
        var top = last ?? vehicle.Stages.Length - 1;
        return StageBase(vehicle, first, top) + vehicle.Stages[top].Height;
    }

    /// <summary>
    /// How far up its own axis a stack's centre of mass sits, above the bottom of stage
    /// <paramref name="first"/>'s body: low, near the heavy engines at the bottom, as a nearly empty
    /// rocket's is. The physics gives its collider this one, and the flight rules judge its stance by it.
    /// </summary>
    public static float CentreOfMassHeight(Vehicle vehicle, int first, int last) => 0.2f * StackHeight(vehicle, first, last);

    /// <summary>
    /// Where a stack's centre of mass is from its origin, in its own space: <see cref="CentreOfMassHeight"/>
    /// up its axis, and for a fairing half on its own, out to the side its shell is on (+Z, half its
    /// radius: the physics moves the centre of mass only down the axis, never across it).
    /// </summary>
    public static Vector3 CentreOfMassOffset(Vehicle vehicle, int first, int last)
    {
        var bottom = vehicle.Stages[first];
        var side = first == last && bottom.Kind == StageKind.Fairing ? bottom.Radius / 2 : 0f;
        return new(0, CentreOfMassHeight(vehicle, first, last), side);
    }

    /// <summary>
    /// Whether a stack, leaned by <paramref name="lean"/> from upright (from its own space into one
    /// whose +Y is straight up), has its centre of mass over what it stands on: its four feet with
    /// <paramref name="legsOut"/> (its bottom stage's legs), or else its bottom engine bell's rim. Past that, it can only fall.
    /// </summary>
    public static bool OverFeet(Vehicle vehicle, int first, int last, bool legsOut, Quaternion lean)
    {
        var count = legsOut ? LegCount : 8;
        Span<Vector2> polygon = stackalloc Vector2[count];
        for (var i = 0; i < count; i++)
        {
            var local = legsOut ? Foot(vehicle.Stages[first].LegGeometry, i) : BellRim(vehicle.Stages[first], i, count);
            var p = Vector3.Transform(local, lean);
            polygon[i] = new Vector2(p.X, p.Z);
        }
        var c = Vector3.Transform(new Vector3(0, CentreOfMassHeight(vehicle, first, last), 0), lean);
        var centre = new Vector2(c.X, c.Z);

        // Inside a convex polygon: on the same side of every edge.
        var sign = 0f;
        for (var i = 0; i < count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % count]);
            var cross = (b.X - a.X) * (centre.Y - a.Y) - (b.Y - a.Y) * (centre.X - a.X);
            if (cross == 0f)
                continue;
            if (sign != 0f && MathF.Sign(cross) != MathF.Sign(sign))
                return false;
            sign = cross;
        }
        return true;
    }

    /// <summary>How many bay doors a stage with a bay (<see cref="Stage.Bay"/>: the lander's) has: one on each of its X faces.</summary>
    public const int DoorCount = 2;

    /// <summary>How many panels each bay door's ramp is (<see cref="Scene.LanderRampPanels"/>): the door itself first, then those folded against it.</summary>
    public const int PanelCount = Scene.LanderRampPanels;

    /// <summary>How long each bay door's ramp is, unfolded: its panels end to end, from the bay floor's edge to its far edge.</summary>
    public const float RampLength = PanelCount * Scene.LanderDoorHeight;

    /// <summary>
    /// How far the bay doors swing out from shut, fully open: past level, until on level ground,
    /// the lander's legs out at full stroke, each ramp's far edge (<see cref="RampLength"/> from
    /// its hinge) is <see cref="Scene.LanderRampClearance"/> above the ground, so they are ramps
    /// from the bay's floor down to it.
    /// </summary>
    public static readonly float DoorOpenRadians = MathF.Acos(
        (Foot(LegGeometry.Lander, 0).Y - LegGeometry.Lander.Stroke + Scene.LanderRampClearance - Scene.LanderBayFloor) / RampLength);

    /// <summary>
    /// Bay door <paramref name="door"/>'s transform, from door space (its hinge along its bottom
    /// edge, through the origin along Z; the door up +Y from it, across Z, its outer face at X = 0
    /// and its thickness on -X, the bay's side) into the lander's space, swung
    /// <paramref name="radians"/> out from shut. Door 0 is the +X face's, door 1 the -X face's;
    /// each hinges on the bay floor's edge and swings outwards. It is the first of its ramp's
    /// panels (see <see cref="PanelAt"/>), and with them unfolded straight, its whole ramp's.
    /// </summary>
    public static Matrix4x4 DoorAt(int door, float radians) =>
        Matrix4x4.CreateRotationZ(-radians)
        * Matrix4x4.CreateTranslation(Scene.LanderHalfWidth, Scene.LanderBayFloor, 0f)
        * Matrix4x4.CreateRotationY(door * MathF.PI);

    /// <summary>
    /// How far a bay door is swung out, <paramref name="open"/> of the way from shut to fully open
    /// at <paramref name="stop"/> (where it comes down onto the ground, or the full swing: see
    /// <see cref="Craft.DoorStops"/>): a door stopping short swings the slower, as far eased, so it
    /// comes to rest on the ground as the other locks, and closes from there without a pause.
    /// </summary>
    public static float DoorAngle(float open, float stop) => stop * Ease(open);

    /// <summary>
    /// How far each fold between a door's panels is from straight, <paramref name="open"/> of the
    /// way from shut (folded flat, half a turn) to fully open (straight): eased as the door's
    /// swing is, so the two go together (see <see cref="PanelTransform"/>).
    /// </summary>
    public static float FoldAngle(float open) => MathF.PI * (1f - Ease(open));

    /// <summary>
    /// Panel <paramref name="panel"/> of bay door <paramref name="door"/>'s ramp, its transform from
    /// panel space (as door space: see <see cref="DoorAt"/>) into the lander's, <paramref name="open"/>
    /// of the way from shut to fully open at <paramref name="stop"/>. The door swings out
    /// (<see cref="DoorAngle"/>) as its panels unfold (<see cref="FoldAngle"/>), together, so each
    /// fold opens as the door lowers: panel 2 keeps the door's own slant, carried out by panel 1,
    /// which swings out and up from hanging down the door's outside, and fully open they are one
    /// straight ramp at the stop. On level ground none comes down below the straight ramp's far
    /// edge, nor back into the body (pinned by the bay doors' tests).
    /// </summary>
    public static Matrix4x4 PanelTransform(int door, int panel, float open, float stop) =>
        PanelAt(door, panel, DoorAngle(open, stop), FoldAngle(open));

    /// <summary>
    /// Panel <paramref name="panel"/> of bay door <paramref name="door"/>'s ramp (0 the door), its
    /// transform (as <see cref="PanelTransform"/>), the door swung <paramref name="radians"/> out
    /// and each fold <paramref name="fold"/> from straight. Shut (folded half a turn), they lie flat
    /// against the door's outside, concertina-wise, each a panel's thickness further out: panel 1
    /// hinged along the door's far edge on its outer face, folded down it; panel 2 along panel 1's
    /// far edge on its inner face (folded, the outer side), folded back up it; and so on.
    /// </summary>
    public static Matrix4x4 PanelAt(int door, int panel, float radians, float fold)
    {
        var (h, t) = (Scene.LanderDoorHeight, Scene.LanderDoorThickness);
        var local = Matrix4x4.Identity;
        // From the panel's own space out through each panel nearer the door, into the door's.
        for (var p = panel; p > 0; p--)
            local *= p % 2 == 1
                ? Matrix4x4.CreateRotationZ(-fold) * Matrix4x4.CreateTranslation(0f, h, 0f) // hinged on the outer face: folds outwards
                : Matrix4x4.CreateTranslation(t, 0f, 0f) * Matrix4x4.CreateRotationZ(fold) * Matrix4x4.CreateTranslation(-t, h, 0f); // on the inner face
        return local * DoorAt(door, radians);
    }

    /// <summary>Half the width of each panel of a door's ramp, across the bay: the bay's.</summary>
    public const float PanelHalfWidth = Scene.LanderBayHalfWidth;

    /// <summary>How far out past the lander's X faces its bay doors reach shut: the panels folded flat against each door, their thickness.</summary>
    public const float FoldedDepth = (PanelCount - 1) * Scene.LanderDoorThickness;

    /// <summary>How far from its axis the lander reaches with its bay doors shut: its corners, or the folded panels' outer corners.</summary>
    public static readonly float ShutReach = MathF.Max(Scene.LanderCornerRadius, new Vector2(Scene.LanderHalfWidth + FoldedDepth, PanelHalfWidth).Length());

    /// <summary>How far from the lander's axis a bay door's ramp, straight and swung <paramref name="radians"/> out, reaches: its far edge's corners.</summary>
    public static float RampReach(float radians) =>
        new Vector2(Scene.LanderHalfWidth + RampLength * MathF.Sin(radians), PanelHalfWidth).Length();

    /// <summary>The middle of bay door <paramref name="door"/>'s ramp's far edge, straight and swung <paramref name="radians"/> out (its lowest, past level), in the lander's space.</summary>
    public static Vector3 RampTipAt(int door, float radians) => Vector3.Transform(new Vector3(0f, RampLength, 0f), DoorAt(door, radians));

    /// <summary>The middle of bay door <paramref name="door"/>'s ramp's far edge, fully open (the lowest of it), in the lander's space: where the ramp meets the ground.</summary>
    public static Vector3 RampTip(int door) => RampTipAt(door, DoorOpenRadians);

    /// <summary>Starts and finishes gently, for the legs' swing and the doors'.</summary>
    public static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
