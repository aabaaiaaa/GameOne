using System.Numerics;

namespace GameOne;

/// <summary>
/// Where the sun's shadows are drawn for one or more views (<see cref="SunAreas"/>): a square of
/// ground, seen down the sun's rays, centred on <see cref="Centre"/> and reaching
/// <see cref="Radius"/> metres from it each way (the renderer's orthographic box).
/// </summary>
internal readonly record struct SunArea(Vector3 Centre, float Radius);

/// <summary>
/// Each frame's sun areas: one round the main view's vessel (the first), then one for each small
/// view whose vessel is not well inside an area already chosen; views close together share one.
/// Each has its own part of the shadow map (the renderer's: the main view's sharper than the
/// others, as a small view can't show finer). Sized for the main view and
/// <see cref="Scene.MaxSmallViews"/> small ones up front, so choosing allocates nothing.
/// </summary>
internal sealed class SunAreas
{
    /// <summary>The most areas in a frame: one per view, if each is apart from the rest.</summary>
    public const int Max = 1 + Scene.MaxSmallViews;

    /// <summary>How far round a vessel away from the pad its area reaches: enough for it and its legs.</summary>
    private const float VesselRadius = 30f;

    private readonly SunArea[] _areas = new SunArea[Max];
    private int _count;

    /// <summary>This frame's areas, the main view's first.</summary>
    public ReadOnlySpan<SunArea> Areas => _areas.AsSpan(0, _count);

    /// <summary>Starts a frame's choice afresh.</summary>
    public void Clear() => _count = 0;

    /// <summary>
    /// The area for a view of a vessel whose bottom stage is at <paramref name="vessel"/> (null
    /// once it has blown up), seen from <paramref name="camera"/>: its index in <see cref="Areas"/>.
    /// The first chosen in a frame (the main view's) is always its own. A later one shares an
    /// earlier area while its vessel is <see cref="Scene.SunAreaMargin"/> inside that area's edge
    /// (by <see cref="Scene.SunAreaHysteresis"/> less, if <paramref name="shared"/>: it shared last
    /// frame), and otherwise has its own, over the ground the main view's would cover there; a
    /// later view of a wreck (<paramref name="vessel"/> null) shares the area its camera's pad is
    /// well inside, if any, or else the main view's, never one of its own.
    /// <paramref name="shared"/> is the view's own, held from frame to frame, and says afterwards
    /// whether it shares.
    /// </summary>
    public int Choose(Matrix4x4? vessel, Vector3 camera, ref bool shared)
    {
        if (_count == Max)
            throw new InvalidOperationException($"more than {Max} views' sun areas chosen in a frame (SunAreas.Max)");
        var own = Around(vessel, camera);
        var at = vessel is { } model ? Middle(model) : own.Centre;
        var hold = shared ? Scene.SunAreaHysteresis : 0f;
        for (var i = 0; i < _count; i++)
            if (Vector3.Distance(at, _areas[i].Centre) <= _areas[i].Radius - Scene.SunAreaMargin + hold)
            {
                shared = true;
                return i;
            }
        shared = false;
        // A later view of a wreck (its vessel blown up: nothing of it left to cast a shadow, and
        // its camera may be far from any pad) has no area of its own, so it costs no square of
        // the map: the main view's, which reads as lit (but for the planet's shadow) away from it.
        if (vessel is null && _count > 0)
            return 0;
        _areas[_count] = own;
        return _count++;
    }

    /// <summary>
    /// The area round a vessel (as <see cref="Choose"/>'s), and, when it is near the pad of the
    /// launch site the camera is at, the pad and tower as well; in between, it eases from one to
    /// the other. Over the pad alone once the vessel has blown up.
    /// </summary>
    public static SunArea Around(Matrix4x4? vessel, Vector3 camera)
    {
        // Over the pad of the launch site where the camera is, whichever it is, as high as the tower's middle.
        var pad = LaunchSites.Nearest(camera).ToWorld(new Vector3(0, Scene.TowerHeight / 2, 0));
        var booster = vessel is { } model ? Middle(model) : pad;
        var apart = Vector3.Distance(booster, pad);
        var away = SmoothStep(150f, 250f, apart);
        return new SunArea(Vector3.Lerp((booster + pad) * 0.5f, booster, away), float.Lerp(apart / 2 + VesselRadius, VesselRadius, away));
    }

    /// <summary>The middle of a booster's body, from its bottom stage's model: what an area is centred on.</summary>
    private static Vector3 Middle(in Matrix4x4 model) => Vector3.Transform(new Vector3(0, Scene.RocketBodyHeight / 2, 0), model);

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
