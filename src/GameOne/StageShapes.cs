using System.Numerics;

namespace GameOne;

/// <summary>
/// A stage's landing legs (<see cref="Stage.LegGeometry"/>): four, hinged round the bottom of its
/// body (see <see cref="RocketGeometry.LegTransform"/>), stowed folded up its side and deployed
/// swung down and out, so their feet reach out past its engine bell and below it. The renderer
/// finds a stage's leg mesh by the instance the stage holds (one of the static shapes here, on a
/// vehicle of <see cref="Vehicle.All"/>), never by its numbers: a copy, even an equal one, has none.
/// </summary>
/// <param name="HingeRadius">How far out from the stage's axis each leg's hinge is, in metres.</param>
/// <param name="HingeHeight">How far up the body, from its bottom, the hinges are.</param>
/// <param name="Length">Each leg's length, from its hinge to its foot.</param>
/// <param name="DeployedDegrees">How far each leg swings deployed, from pointing straight up the body.</param>
/// <param name="Stroke">How far each crush leg hangs below where it is pushed right up (see <see cref="Scene.LegStroke"/>).</param>
internal sealed record LegGeometry(float HingeRadius, float HingeHeight, float Length, float DeployedDegrees, float Stroke)
{
    /// <summary>The rocket's: the booster's and the upper stage's legs.</summary>
    public static readonly LegGeometry Rocket = new(Scene.RocketRadius + 0.1f, Scene.LegHingeHeight, Scene.LegLength, Scene.LegDeployedDegrees, Scene.LegStroke);

    /// <summary>
    /// The lander's: one at each corner of its square body (the legs face out at 45°, along its
    /// diagonals), hinged just out from it, short, stowed up the corner to the top of the body;
    /// small feet, so stowed they fit inside its fairing.
    /// </summary>
    public static readonly LegGeometry Lander = new(Scene.LanderCornerRadius + Scene.LanderLegHingeOut, Scene.LanderLegHingeHeight,
        Scene.LanderLegLength, Scene.LanderLegDeployedDegrees, Scene.LanderLegStroke)
    {
        FootHalfWidth = Scene.LanderFootHalfWidth,
        HalfDepth = Scene.LanderLegHalfDepth, HalfWidth = Scene.LanderLegHalfWidth,
        StrutInward = Scene.LanderStrutInward, StrutHalfWidth = Scene.LanderStrutHalfWidth,
    };

    /// <summary>Half the width of each foot's square pad (its collider's and its mesh's).</summary>
    public float FootHalfWidth { get; init; } = Scene.FootHalfWidth;

    /// <summary>Half the leg's depth as drawn, out from the body (leg space X).</summary>
    public float HalfDepth { get; init; } = 0.14f;

    /// <summary>Half the leg's width as drawn, across (leg space Z).</summary>
    public float HalfWidth { get; init; } = 0.11f;

    /// <summary>How far in from the leg's middle (towards the body) its strut, like a damper, is drawn.</summary>
    public float StrutInward { get; init; } = 0.2f;

    /// <summary>Half the strut's depth and width as drawn.</summary>
    public float StrutHalfWidth { get; init; } = 0.05f;

    // Worked out once, the first time asked: the physics and the flight rules ask for every foot
    // of every craft with its legs out, every step, and building the leg's transform each time
    // cost more than the rest. Left out of the copy a `with` makes, so a changed one works its own out.
    private Vector3[]? _feet;

    private LegGeometry(LegGeometry original)
    {
        (HingeRadius, HingeHeight, Length, DeployedDegrees, Stroke, FootHalfWidth) = (original.HingeRadius, original.HingeHeight, original.Length, original.DeployedDegrees, original.Stroke, original.FootHalfWidth);
        (HalfDepth, HalfWidth, StrutInward, StrutHalfWidth) = (original.HalfDepth, original.HalfWidth, original.StrutInward, original.StrutHalfWidth);
    }

    /// <summary>The bottom of leg <paramref name="leg"/>'s foot, deployed and pushed right up (where the collider's foot is), in the stage's space.</summary>
    public Vector3 Foot(int leg) => (_feet ??= [.. Enumerable.Range(0, RocketGeometry.LegCount).Select(i =>
        Vector3.Transform(new Vector3(0, Length, 0), RocketGeometry.LegTransform(this, i, 1f)) - new Vector3(0, RocketGeometry.FootThickness, 0))])[leg];

    // Equal by its shape alone, not by whether its feet have been worked out yet.
    public bool Equals(LegGeometry? other) => other is not null
        && (HingeRadius, HingeHeight, Length, DeployedDegrees, Stroke, FootHalfWidth) == (other.HingeRadius, other.HingeHeight, other.Length, other.DeployedDegrees, other.Stroke, other.FootHalfWidth)
        && (HalfDepth, HalfWidth, StrutInward, StrutHalfWidth) == (other.HalfDepth, other.HalfWidth, other.StrutInward, other.StrutHalfWidth);

    public override int GetHashCode() => HashCode.Combine(HashCode.Combine(HingeRadius, HingeHeight, Length, DeployedDegrees, Stroke, FootHalfWidth),
        HalfDepth, HalfWidth, StrutInward, StrutHalfWidth);
}

/// <summary>
/// A payload's fairing (<see cref="Stage.FairingGeometry"/>), round it on top of the stage under
/// it until jettisoned: a cylinder, and a narrower nose on it. It comes away in two halves, each
/// its own vehicle (<see cref="Vehicle.HalfOf"/>). That half and the renderer's mesh are found by
/// the instance a stage holds (one of the static shapes here, on a vehicle of <see cref="Vehicle.All"/>),
/// never by its numbers: a copy, even an equal one, has neither.
/// </summary>
/// <param name="Radius">The cylinder's radius, in metres.</param>
/// <param name="Height">The cylinder's height, the straight part over the payload.</param>
/// <param name="NoseHeight">The nose's height, on top of the cylinder.</param>
/// <param name="Mass">Both halves, in kg.</param>
internal sealed record FairingGeometry(float Radius, float Height, float NoseHeight, float Mass)
{
    /// <summary>The satellite's: as wide as the rocket, with a nose like the upper stage's own.</summary>
    public static readonly FairingGeometry Satellite = new(Scene.RocketRadius, Scene.FairingHeight, Scene.FairingNoseHeight, Scene.FairingMass);

    /// <summary>
    /// How far its foot's cone hangs under it: one wider than the rocket closes onto the stage
    /// under it with a cone in at 45° to the rocket's radius (none for one as wide as the rocket).
    /// </summary>
    public float ConeHeight => MathF.Max(0f, Radius - Scene.RocketRadius);

    /// <summary>The lander's: wider than the rocket, round its square body and stowed legs.</summary>
    public static readonly FairingGeometry Lander = new(Scene.LanderFairingRadius, Scene.LanderFairingHeight, Scene.LanderFairingNoseHeight, Scene.LanderFairingMass);
}
