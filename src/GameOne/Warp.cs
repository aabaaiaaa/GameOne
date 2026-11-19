namespace GameOne;

/// <summary>
/// Why the time warp dropped from a formula's rate to the physics' top rate
/// (<see cref="Scene.PhysicsTopRate"/>), for a witnessed vessel: so the physics is running as it
/// happens. Information only, never a prompt.
/// </summary>
internal enum WarpDrop
{
    /// <summary>Its way into the air of a body with air.</summary>
    Air,
    /// <summary>Into reach of the ground of a body with no air.</summary>
    Ground,
    /// <summary>At rest, set moving: launched, throttled up, staged, its legs or bay doors moved.</summary>
    SetMoving,
    /// <summary>A steering key.</summary>
    Steering,
    /// <summary>A collision coming at it.</summary>
    Collision,
    /// <summary>A near miss coming at it (with the setting on).</summary>
    NearMiss,
    /// <summary>A manoeuvre node coming up.</summary>
    Node,
    /// <summary>A burn's fuel running out.</summary>
    FuelOut,
}

/// <summary>
/// The warp dropped (<see cref="Flight.LastDrop"/>): why, for which vessel (as the log names it;
/// empty for a drop that is no vessel's), to what rate, and when, in the flight's real seconds,
/// so the HUD says so for <see cref="Scene.WarpDropShownSeconds"/>.
/// </summary>
internal readonly record struct WarpDropped(WarpDrop Reason, string Vessel, float Rate, double RealTime)
{
    /// <summary>What the time line says it dropped for: "Upper stage 3 coming into the air".</summary>
    public string Why => Reason switch
    {
        WarpDrop.Air => $"{Vessel} coming into the air",
        WarpDrop.Ground => $"{Vessel} near the ground",
        WarpDrop.SetMoving => $"{Vessel} set moving",
        WarpDrop.Steering => "steering",
        WarpDrop.Collision => $"a collision coming at {Vessel}",
        WarpDrop.NearMiss => $"a near miss coming at {Vessel}",
        WarpDrop.Node => "a node coming up",
        WarpDrop.FuelOut => $"{Vessel}'s fuel running out",
        _ => Reason.ToString(),
    };

    /// <summary>Whether it is still to be shown at <paramref name="realNow"/> (the flight's real seconds), with the rate still the one it dropped to.</summary>
    public bool ShownAt(double realNow, float playerRate) =>
        playerRate == Rate && realNow - RealTime <= Scene.WarpDropShownSeconds;
}
