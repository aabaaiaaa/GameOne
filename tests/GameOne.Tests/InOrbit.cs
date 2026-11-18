using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// Saved vessels put on orbits by their centre of mass, as the game reads them: Jolt's velocity is
/// the centre of mass's, so an orbit built from the body's origin (a metre or so below it) with that
/// velocity reads a little out of true.
/// </summary>
internal static class InOrbit
{
    /// <summary>How <paramref name="vessel"/> moves among the stars, at its centre of mass.</summary>
    public static Vector3 Stars(VesselState vessel) => Planet.AmongStars(vessel.CentreOfMass.ToSingle(), vessel.Velocity.ToSingle());

    /// <summary>
    /// <paramref name="vessel"/> with its centre of mass at <paramref name="centre"/>, moving
    /// <paramref name="stars"/> among the stars, turned by <paramref name="rotation"/> (by default,
    /// standing up from the planet there) and aiming straight up.
    /// </summary>
    public static VesselState At(VesselState vessel, Vector3 centre, Vector3 stars, Quaternion? rotation = null)
    {
        var turn = rotation ?? Planet.UprightAt(centre);
        return vessel with
        {
            Position = centre - Vector3.Transform(vessel.CentreOfMassOffset, turn),
            Rotation = turn,
            Aim = Planet.Up(centre),
            Velocity = Planet.OverGround(centre, stars),
        };
    }

    /// <summary>
    /// Reach Orbit's upper stage on its own, the booster gone and half its tank left (a convenience,
    /// enough for any burn a test asks of it, not what a flight leaves in it), as a vessel of
    /// flight <paramref name="flight"/> (1, the flight a simulation starts, by default) numbered
    /// <paramref name="id"/>: over Home, <paramref name="altitude"/> above the sea, flying level east
    /// among the stars at <paramref name="share"/> of the speed that would hold it in a circle there.
    /// Under 1 it is at the top of its orbit, over 1 at the bottom: so a test that is about the
    /// upper stage in or near orbit starts there, not flown up from the pad.
    /// </summary>
    public static VesselState UpperStage(int id, float altitude, float share, int flight = 1)
    {
        var vehicle = Vehicle.TwoStage;
        var up = Planet.Up(Scene.RocketStart);
        var centre = Planet.Centre + up * (Planet.SeaRadius + altitude);
        var tanks = new float[vehicle.Stages.Length];
        tanks[1] = vehicle.Stages[1].FuelMass * 0.5f;
        return At(new VesselState
        {
            Id = id, Flight = flight, Mission = Mission.ReachOrbit, Vehicle = vehicle, First = 1, Last = 1, Fuel = tanks,
            State = FlightState.Flying, Position = centre, Rotation = Quaternion.Identity, Velocity = Vector3.Zero,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 1, 1),
            AngularVelocity = Vector3.Zero, Aim = up, MaxAltitude = altitude, MaxGround = altitude, HasBeenAirborne = true,
        }, centre, Planet.East(centre) * Planet.CircularSpeed(altitude) * share);
    }

    /// <summary>
    /// The satellite launcher's upper stage alone (legs stowed, engine off), numbered
    /// <paramref name="id"/>, on a round orbit over <paramref name="body"/>'s equator,
    /// <paramref name="height"/> over its orbit's floor (<see cref="GroundBody.OrbitFloor"/>: its
    /// air's top, or its highest ground), at the place out along +X from its centre, going east.
    /// </summary>
    public static VesselState Round(GroundBody body, int id, float height)
    {
        var vehicle = Vehicle.ThreeStage;
        const int Upper = 1;
        var centreOfMass = body.Centre + Vector3.UnitX * (body.SeaRadius + body.OrbitFloor + height);
        var stars = body.East(centreOfMass) * body.CircularSpeed(body.OrbitFloor + height);
        var rotation = body.UprightAt(centreOfMass);
        var offset = RocketGeometry.CentreOfMassOffset(vehicle, Upper, Upper);
        return new VesselState
        {
            Id = id, Flight = 1, Mission = Mission.FreePlay, Vehicle = vehicle, First = Upper, Last = Upper,
            Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * 0.5f)],
            State = FlightState.Flying, Position = centreOfMass - Vector3.Transform(offset, rotation), Rotation = rotation,
            CentreOfMassOffset = offset, Velocity = body.OverGround(centreOfMass, stars), AngularVelocity = Vector3.Zero,
            Aim = Vector3.Transform(Vector3.UnitY, rotation), HasBeenAirborne = true, MaxAltitude = body.OrbitFloor + height,
            Body = body.Name,
        };
    }

    /// <summary>The share of the circular speed at <paramref name="apoapsis"/> (for <see cref="UpperStage"/>) that makes it the top of an orbit whose bottom is <paramref name="periapsis"/> (both above the sea).</summary>
    public static float ShareAtTop(float apoapsis, float periapsis)
    {
        var (top, bottom) = (Planet.SeaRadius + apoapsis, Planet.SeaRadius + periapsis);
        return MathF.Sqrt(2f * bottom / (top + bottom));
    }
}
