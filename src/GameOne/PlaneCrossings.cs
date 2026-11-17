using System.Numerics;

namespace GameOne;

/// <summary>
/// Where a path's plane crosses a target's: the two nodes on the path and the angle between the
/// planes, shown as marks on the path. Information only (the pilot decides what to do with it).
/// </summary>
internal static class PlaneCrossings
{
    /// <summary>The planes closer than this (in radians, 0.01°) are one: their line of nodes is lost in the rounding.</summary>
    private const float SamePlane = 0.01f * MathF.PI / 180f;

    /// <summary>
    /// Where <paramref name="own"/> crosses the plane at a right angle to <paramref name="targetNormal"/>
    /// (a plane through the same body's centre, in the same stars' frame): the true anomalies of its
    /// ascending node (where it crosses going the way the normal points) and its descending node,
    /// and the angle between the planes in radians. Null if they are within 0.01° of each other or
    /// of facing opposite ways (no line where they cross), or either has no plane (straight up and down).
    /// </summary>
    public static (float Ascending, float Descending, float Inclination)? Of(in Orbit own, Vector3 targetNormal)
    {
        var normal = own.Normal;
        if (normal.LengthSquared() < 1e-12f || targetNormal.LengthSquared() < 1e-12f)
            return null;
        normal = Vector3.Normalize(normal);
        targetNormal = Vector3.Normalize(targetNormal);
        var line = Vector3.Cross(targetNormal, normal);
        var sine = line.Length();
        if (sine < MathF.Sin(SamePlane))
            return null;
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(normal, targetNormal), -1f, 1f));
        return ((float)own.AnomalyToward(line), (float)own.AnomalyToward(-line), angle);
    }
}
