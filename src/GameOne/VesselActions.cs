using System.Numerics;

namespace GameOne;

/// <summary>Something the flown vessel can be asked to do from its actions menu (<see cref="ActionMenu"/>), each said as what it will do now.</summary>
internal enum VesselAction { OpenBayDoors, CloseBayDoors, DeployLegs, StowLegs }

/// <summary>
/// A vessel's actions: what it can do now (its bay's doors, its legs), what each is called, how
/// one goes into the flight (through <see cref="FlightInput"/>, as a key would), and whether a
/// click on the screen is on the vessel.
/// </summary>
internal static class VesselActions
{
    /// <summary>
    /// Fills <paramref name="actions"/> with what <paramref name="vessel"/> can do now: its bay's
    /// doors first (opened or closed, by what is wanted of them now), then its legs (its bottom
    /// stage's, as the legs key's); none for a crashed vessel.
    /// </summary>
    public static void Of(Vessel vessel, List<VesselAction> actions)
    {
        actions.Clear();
        if (vessel.State == FlightState.Crashed)
            return;
        if (vessel.HasBay)
            actions.Add(vessel.BayDoorsWanted ? VesselAction.CloseBayDoors : VesselAction.OpenBayDoors);
        if (vessel.HasLegs)
            actions.Add(vessel.LegsWanted ? VesselAction.StowLegs : VesselAction.DeployLegs);
    }

    /// <summary>What the menu calls <paramref name="action"/>.</summary>
    public static string Label(VesselAction action) => action switch
    {
        VesselAction.OpenBayDoors => "Open bay doors",
        VesselAction.CloseBayDoors => "Close bay doors",
        VesselAction.DeployLegs => "Deploy legs",
        VesselAction.StowLegs => "Stow legs",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary><paramref name="input"/> with <paramref name="action"/> in it (if any): the toggle it is, as the legs key's is.</summary>
    public static FlightInput Into(in FlightInput input, VesselAction? action) => action switch
    {
        VesselAction.OpenBayDoors or VesselAction.CloseBayDoors => input with { ToggleBayDoors = true },
        VesselAction.DeployLegs or VesselAction.StowLegs => input with { ToggleLegs = true },
        _ => input,
    };

    /// <summary>
    /// Whether <paramref name="pointer"/> (pixels from the top left of a screen
    /// <paramref name="display"/> across) is on a vessel whose axis runs from
    /// <paramref name="bottom"/> to <paramref name="top"/>, <paramref name="radius"/> metres round,
    /// seen through <paramref name="viewProjection"/>: within its radius of its axis on the screen,
    /// or within <see cref="Scene.ActionMenuMinPixels"/> if that is smaller. A part of the axis
    /// behind the camera is cut off at it.
    /// </summary>
    public static bool OnVessel(Vector3 bottom, Vector3 top, float radius, Matrix4x4 viewProjection, Vector2 display, Vector2 pointer)
    {
        const float near = 1e-3f;
        var a = Vector4.Transform(new Vector4(bottom, 1f), viewProjection);
        var b = Vector4.Transform(new Vector4(top, 1f), viewProjection);
        if (a.W <= near && b.W <= near)
            return false;
        // Clip space is straight in the world's, so an end behind the camera is moved along the axis to just in front.
        if (a.W <= near)
            a = Vector4.Lerp(a, b, (near - a.W) / (b.W - a.W));
        else if (b.W <= near)
            b = Vector4.Lerp(b, a, (near - b.W) / (a.W - b.W));
        Vector2 Screen(Vector4 c) => new Vector2(c.X / c.W + 1f, 1f - c.Y / c.W) * display * 0.5f;
        var (from, to) = (Screen(a), Screen(b));
        var along = to - from;
        var t = along.LengthSquared() > 0f ? Math.Clamp(Vector2.Dot(pointer - from, along) / along.LengthSquared(), 0f, 1f) : 0f;
        // Its radius in pixels where the pointer is nearest its axis: the projection's vertical
        // scale is the length of the view-projection's Y column (the view only turns and moves).
        var w = a.W + (b.W - a.W) * t;
        var scale = new Vector3(viewProjection.M12, viewProjection.M22, viewProjection.M32).Length();
        var pixels = radius * scale / w * display.Y * 0.5f;
        return Vector2.Distance(pointer, from + along * t) <= MathF.Max(pixels, Scene.ActionMenuMinPixels);
    }
}
