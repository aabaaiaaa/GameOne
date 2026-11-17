using System.Numerics;

namespace GameOne;

/// <summary>
/// A place on a body's ground that exists only for a mission (<see cref="Mission.PointsOfInterest"/>):
/// shown by a marker over the view and on the map (see <see cref="Markers.Gather"/>), never built
/// on the ground, and a target as a site is (<see cref="TargetKind.Point"/>, by its index in the mission's list), never an instruction.
/// </summary>
internal sealed record PointOfInterest(string Name, GroundBody Body, float Latitude, float Longitude)
{
    /// <summary>Where the mission's landers are to come down on the moon (<see cref="Scene.MoonLandingSpot"/>).</summary>
    public static PointOfInterest MoonLandingSpot { get; } =
        new("Landing spot", GroundBody.Moon, Scene.MoonLandingSpot.Latitude, Scene.MoonLandingSpot.Longitude);

    /// <summary>Its direction from its body's centre, body-fixed.</summary>
    public Vector3 Direction => CubeSphere.FromLatLon(float.DegreesToRadians(Latitude), float.DegreesToRadians(Longitude));

    /// <summary>On its ground, in its body's frame.</summary>
    public Vector3 Place
    {
        get
        {
            var direction = Direction;
            return Body.Centre + direction * (Body.SeaRadius + Body.Height(direction));
        }
    }

    /// <summary>
    /// How far <paramref name="vessel"/> is from it at world time <paramref name="now"/>: along its
    /// body's ground at that body; from another body, the straight line across the bodies.
    /// </summary>
    public float DistanceFrom(Vessel vessel, double now) =>
        vessel.Body == Body
            ? Body.Radius * Body.AngleBetween(vessel.Position, Place)
            : Vector3.Distance(vessel.Position, Bodies.Change(Body, vessel.Body, now).Point((Vector3Double)Place).ToSingle());
}
