using System.Numerics;

namespace GameOne;

/// <summary>
/// The steering rings' angles, round the body a vessel is at: a compass heading (radians clockwise
/// from that body's north) and a pitch (up from its horizon along that heading; past 90 degrees it
/// tips back the other way). The rings are drawn and read by these, and the rings' hold steers by
/// them, so the nose goes where the ring says at any body.
/// </summary>
internal static class RingAngles
{
    /// <summary>The way the nose points for <paramref name="heading"/> and <paramref name="pitch"/> at <paramref name="at"/> round <paramref name="body"/>.</summary>
    public static Vector3 Direction(GroundBody body, Vector3 at, float heading, float pitch) =>
        body.Towards(at, heading) * MathF.Cos(pitch) + body.Up(at) * MathF.Sin(pitch);

    /// <summary>
    /// The heading and pitch of <paramref name="aim"/> at <paramref name="at"/> round <paramref name="body"/>;
    /// pointing straight up or down, its heading is <paramref name="heldHeading"/>.
    /// </summary>
    public static (float Heading, float Pitch) Of(GroundBody body, Vector3 at, Vector3 aim, float heldHeading)
    {
        var up = body.Up(at);
        var upward = Vector3.Dot(aim, up);
        var across = aim - up * upward;
        var heading = across.Length() > 0.02f ? body.Bearing(at, across) : heldHeading;
        return (heading, MathF.Atan2(upward, Vector3.Dot(aim, body.Towards(at, heading))));
    }
}
