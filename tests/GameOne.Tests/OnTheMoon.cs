using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// Vessels standing on the moon, in its own frame (<see cref="GroundBody.Moon"/>: its directions
/// from its centre are its moon-fixed ones), for a world that starts with one landed there: no more
/// than a vessel landed anywhere is (<see cref="VesselState.Body"/> the moon's), so it rests, freezes
/// and is moved under formula by the moon's own rules.
/// </summary>
internal static class OnTheMoon
{
    private static GroundBody Moon => GroundBody.Moon;

    /// <summary>The moon-fixed direction of <see cref="Scene.MoonLandingSpot"/>.</summary>
    internal static Vector3 LandingSpot =>
        CubeSphere.FromLatLon(float.DegreesToRadians(Scene.MoonLandingSpot.Latitude), float.DegreesToRadians(Scene.MoonLandingSpot.Longitude));

    /// <summary>
    /// Where a vessel stands upright on its bottom stage's legs (out) on the moon's ground in
    /// moon-fixed <paramref name="direction"/>, in the moon's frame: its origin, its lowest foot half
    /// its stroke into the ground (so each leg stands as far out as its own ground there needs, as
    /// a landed vessel's do), and its attitude.
    /// </summary>
    internal static (Vector3 Position, Quaternion Rotation) Standing(Vector3 direction, LegGeometry? legs = null)
    {
        legs ??= LegGeometry.Rocket;
        var up = Vector3.Normalize(direction);
        var axis = Vector3.Cross(Vector3.UnitY, up);
        var sin = axis.Length();
        var rotation = sin < 1e-6f
            ? (up.Y > 0f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI))
            : Quaternion.CreateFromAxisAngle(axis / sin, MathF.Atan2(sin, up.Y));
        var position = Moon.Centre + up * (Moon.SeaRadius + Moon.Height(up));
        // Lowered onto it a few times over, as the ground under each foot differs from that under its middle.
        for (var i = 0; i < 3; i++)
        {
            var lowest = float.PositiveInfinity;
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
                lowest = MathF.Min(lowest, Moon.GroundAltitude(position + Vector3.Transform(RocketGeometry.Foot(legs, leg), rotation)));
            position -= up * (lowest - legs.Stroke / 2f);
        }
        return (position, rotation);
    }

    /// <summary>
    /// A lander landed on the moon: the satellite launcher's upper stage alone (the one with legs),
    /// standing on them in moon-fixed <paramref name="moonFixedDirection"/> (<see cref="Standing"/>),
    /// still, numbered <paramref name="id"/>, at the moon (its state in the moon's frame).
    /// </summary>
    internal static VesselState Lander(int id, Vector3 moonFixedDirection)
    {
        var vehicle = Vehicle.ThreeStage;
        const int Upper = 1;
        var (position, rotation) = Standing(moonFixedDirection, vehicle.Stages[Upper].LegGeometry);
        return new VesselState
        {
            Id = id, Flight = 1, Mission = Mission.FreePlay, Vehicle = vehicle, First = Upper, Last = Upper,
            Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * 0.5f)],
            State = FlightState.Landed, Position = position, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, Upper, Upper),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero,
            Aim = Vector3.Transform(Vector3.UnitY, rotation), LegsWanted = true, LegDeploy = 1f, HasBeenAirborne = true, TouchedDown = true,
            Body = Moon.Name,
        };
    }
}
