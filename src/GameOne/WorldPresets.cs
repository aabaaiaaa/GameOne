using System.Numerics;

namespace GameOne;

/// <summary>
/// The worlds a new world can be made from: templates, never changed themselves. Playing one makes
/// a saved world of its own from it (see <see cref="WorldStore"/>), which can be reset back to it.
/// </summary>
internal static class WorldPresets
{
    public const string EmptyId = "empty", SatellitesId = "satellites";

    /// <summary>Every preset, as the world list offers them: its id, its name and what it is.</summary>
    public static readonly (string Id, string Name, string Description)[] All =
    [
        (EmptyId, "Empty world", "Nothing but the launch pad, the landing zones and the tower."),
        (SatellitesId, "Satellites in orbit", "Four satellites already in orbit, 9 to 10 km up; two share a plane, with a transfer from one to the other soon. Each can be flown."),
    ];

    /// <summary>A fresh world from preset <paramref name="id"/> (not saved: no id of its own yet).</summary>
    public static WorldState Make(string id) => id switch
    {
        EmptyId => Empty(),
        SatellitesId => Satellites(),
        _ => throw new ArgumentException($"There is no preset \"{id}\".", nameof(id)),
    };

    /// <summary>An empty world: nothing in it but the launch pad, the landing zones and the tower.</summary>
    public static WorldState Empty() => new() { Name = "Empty world", Preset = EmptyId };

    /// <summary>
    /// Four satellites like the satellite mission's, already in circular orbits 9 to 10 km up and
    /// spread round the planet. The first two share the launch's (eastward) plane, 9 and 10 km up,
    /// the higher a little ahead: far enough that one burn from the lower, about a minute and a
    /// half into the flight, takes it up to the higher one (a transfer window to practise on). The
    /// other two are in planes of their own (tilted 60° and 90°), so they cross the sky at other
    /// times and angles. Each has guidance and a full tank, so it can be flown.
    /// </summary>
    public static WorldState Satellites()
    {
        (float Altitude, float TiltDegrees, float AroundDegrees)[] orbits =
        [
            (9000f, 0f, 60f),
            (10000f, 0f, 72f),
            (9600f, 60f, 240f),
            (10000f, 90f, 330f),
        ];
        var vehicle = Vehicle.ThreeStage;
        var satellite = vehicle.Stages.Length - 1;
        var vessels = new List<VesselState>();
        for (var i = 0; i < orbits.Length; i++)
        {
            var (altitude, tilt, around) = orbits[i];
            var (position, _, velocity) = CircularOrbit(altitude, float.DegreesToRadians(tilt), float.DegreesToRadians(around),
                RocketGeometry.CentreOfMassHeight(vehicle, satellite, satellite));
            var up = Vector3.Normalize(position - Planet.Centre); // standing up from the planet
            var fuel = new float[vehicle.Stages.Length];
            fuel[satellite] = vehicle.Stages[satellite].FuelMass;
            vessels.Add(new VesselState
            {
                Id = i,
                Flight = 0, // there from the start, before any flight
                Mission = Mission.DeploySatellite,
                Vehicle = vehicle,
                First = satellite,
                Last = satellite,
                Fuel = fuel,
                State = FlightState.Flying,
                Position = position,
                CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, satellite, satellite),
                Rotation = RotationTo(up),
                Velocity = velocity,
                AngularVelocity = Vector3.Zero,
                Aim = up,
                MaxAltitude = altitude,
                MaxGround = altitude,
                AchievedOrbit = true,
                HasBeenAirborne = true,
            });
        }
        return new WorldState { Name = "Satellites in orbit", Preset = SatellitesId, NextVesselId = orbits.Length, Vessels = vessels };
    }

    /// <summary>
    /// Where something is in a circular orbit <paramref name="altitude"/> above the sea, and how it moves:
    /// in a plane tilted <paramref name="tilt"/> (radians) from the launch's (which runs east over
    /// the pad), about the line through the pad, and <paramref name="around"/> radians round from there.
    /// The orbit is circular among the stars, so its velocity is that less the ground's turn there,
    /// as a vessel's is kept (over the ground). It is the centre of mass that is put on the circle
    /// (Jolt's velocity is the centre of mass's): the origin, which is what places the body, is
    /// <paramref name="centreOfMassHeight"/> below it, for a vessel standing up from the planet.
    /// </summary>
    internal static (Vector3 Position, Vector3 CentreOfMass, Vector3 Velocity) CircularOrbit(float altitude, float tilt, float around, float centreOfMassHeight)
    {
        var pad = Vector3.UnitY; // the pad is on top of the planet
        var across = Planet.East(Vector3.Zero) * MathF.Cos(tilt) + Planet.North(Vector3.Zero) * MathF.Sin(tilt);
        var radius = Planet.SeaRadius + altitude;
        var (sin, cos) = MathF.SinCos(around);
        var outwards = pad * cos + across * sin;
        var centre = Planet.Centre + outwards * radius;
        var circular = (-pad * sin + across * cos) * Planet.CircularSpeed(altitude);
        return (centre - outwards * centreOfMassHeight, centre, Planet.OverGround(centre, circular));
    }

    /// <summary>The rotation that turns a stage's own up (+Y) to point along <paramref name="up"/>.</summary>
    internal static Quaternion RotationTo(Vector3 up)
    {
        var axis = Vector3.Cross(Vector3.UnitY, up);
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitY, up), -1f, 1f));
        return axis.LengthSquared() < 1e-10f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
    }
}
