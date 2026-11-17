using System.Numerics;
using System.Text.Json.Serialization;

namespace GameOne;

/// <summary>
/// One vessel as it is at a moment: everything needed to bring it back exactly as it was, into
/// the physics and the flight rules (see <see cref="Vessel.Capture"/>). A world is made of these
/// when it is saved, or when a flight starts (so that flight can be undone).
/// </summary>
internal sealed record VesselState
{
    /// <summary>Its number in its world: unique there, for as long as the world lasts.</summary>
    public required int Id { get; init; }

    /// <summary>Which of its world's flights it came from (1 is the first).</summary>
    public required int Flight { get; init; }

    /// <summary>The mission it was flown on: where its stages should land, how high it had to go.</summary>
    public required Mission Mission { get; init; }

    public required Vehicle Vehicle { get; init; }

    /// <summary>Which of the vehicle's stages it is: from <see cref="First"/> (its bottom, the one whose engine fires) to <see cref="Last"/>.</summary>
    public required int First { get; init; }
    public required int Last { get; init; }

    /// <summary>Fuel left in each of the vehicle's stages, kg (only its own count).</summary>
    public required float[] Fuel { get; init; }

    public required FlightState State { get; init; }

    /// <summary>Its origin (the bottom of its bottom stage's body), in its body's frame (<see cref="Body"/>): where the body is put back.</summary>
    public required Vector3 Position { get; init; }

    /// <summary>
    /// Where its centre of mass is from its origin, in its own space (as <see cref="Vessel.Capture"/>
    /// measured it). Kept in its own space rather than the world's, so a copy moved or turned
    /// (<c>with { Position = ... }</c>) carries its centre of mass with it.
    /// </summary>
    public required Vector3 CentreOfMassOffset { get; init; }

    /// <summary>
    /// Its centre of mass, in its body's frame: where <see cref="Velocity"/> is (Jolt's velocity is the
    /// centre of mass's), so its orbit is read from here, not from <see cref="Position"/>. Not used
    /// to put it back (the body's shape and <see cref="Rotation"/> place it from the origin).
    /// </summary>
    [JsonIgnore]
    public Vector3 CentreOfMass => Position + Vector3.Transform(CentreOfMassOffset, Rotation);

    public required Quaternion Rotation { get; init; }
    public required Vector3 Velocity { get; init; }
    public required Vector3 AngularVelocity { get; init; }

    // Its last inputs, which it carries on with.
    public float Throttle { get; init; }
    public required Vector3 Aim { get; init; }
    public bool ProgradeHold { get; init; }
    public bool RetrogradeHold { get; init; }
    public bool LegsWanted { get; init; }
    public float LegDeploy { get; init; }
    public bool BayDoorsWanted { get; init; }
    public float BayDoors { get; init; }    // how far open its bay's doors are, 0 to 1 (a vessel with a bay: see Vessel.HasBay)
    public Vector2? BayDoorStops { get; init; } // where each stopped on the ground (Craft.DoorStops); none, their full swing
    public bool FairingOn { get; init; }
    public NavTarget? Target { get; init; }
    public CameraView? View { get; init; } // how it was last looked at (see Vessel.View)
    public ManoeuvreChain? Chain { get; init; } // its planned burns (see Vessel.Chain), none with null
    public Vector3? BurnAim { get; init; }
    public Vector3 Burned { get; init; }
    public double BurnStart { get; init; }

    // What has happened to it so far.
    public float MaxAltitude { get; init; }
    public float MaxGround { get; init; }
    public bool AchievedOrbit { get; init; }
    public bool LandedOnTarget { get; init; }
    public float TouchdownSpeed { get; init; }
    /// <summary>
    /// Whether its landing has begun: it has touched down since it last flew clear of the ground
    /// under its own power, or settled. Its <see cref="TouchdownSpeed"/> is the hardest touch since (see <see cref="Vessel"/>).
    /// </summary>
    public bool TouchedDown { get; init; }
    /// <summary>The hardest it touched another vessel in that landing, if it has: one of its touches, should it come to rest on another.</summary>
    public float? VesselTouch { get; init; }
    public float IgnitionDelay { get; init; }
    public bool WasTouching { get; init; }
    public bool HullWasTouching { get; init; }
    public bool HasBeenAirborne { get; init; }
    public float SettledFor { get; init; }
    public float FloatingFor { get; init; }
    /// <summary>Which of the points it is watched at for touching the water were under it, one bit each (see <see cref="Vessel"/>).</summary>
    public int WetParts { get; init; }

    /// <summary>
    /// How fast the ground under it was moving when it was last measured (a raft's deck: see
    /// <see cref="Vessel.GroundVelocity"/>), so its first step after loading is judged against what it was on.
    /// </summary>
    public Vector3 Ground { get; init; }

    /// <summary>
    /// The vessel it was still parting from (just staged, or a fairing half just off its payload:
    /// see <see cref="Physics.Craft.PartingFrom"/>), by its <see cref="Id"/>, or null: so a world
    /// saved just after staging comes back with the parts still coming apart, not pushing each other.
    /// </summary>
    public int? PartingFrom { get; init; }

    /// <summary>
    /// The body it is at, by name (<see cref="GroundBody.Name"/>, found in <see cref="Bodies.All"/>):
    /// <see cref="Position"/>, <see cref="Rotation"/>, <see cref="Velocity"/> and
    /// <see cref="AngularVelocity"/> are in that body's frame (over its ground), and it is brought
    /// back into that body's physics, or under formula there.
    /// </summary>
    public string Body { get; init; } = GroundBody.Planet.Name;

    /// <summary>The launch site its run was launched from, by name (<see cref="LaunchSite.Name"/>): kept after its run ends, so it can be started again there.</summary>
    public string Site { get; init; } = LaunchSites.Home.Name;
}

/// <summary>
/// A world as it is at a moment: its name, its clock, how many flights have been launched into it,
/// and every vessel in it that is still in one piece. It is what is saved; a <see cref="Flight"/>
/// brings it to life (<see cref="Flight.Load"/>) and captures it again (<see cref="Flight.Capture"/>).
/// </summary>
/// <remarks>
/// The world carries on from flight to flight: whatever is left at the end of one (in orbit,
/// landed, lying on the ground, even still falling) is still there in the next, when a fresh rocket
/// appears on the pad. Its clock only runs while it is being played.
/// </remarks>
internal sealed record WorldState
{
    /// <summary>What it is saved as (a file name, without the extension); empty for a world that isn't saved.</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "Empty world";

    /// <summary>Which preset it was made from (see <see cref="WorldPresets"/>), to reset it back to.</summary>
    public string Preset { get; init; } = WorldPresets.EmptyId;

    public DateTime Created { get; init; }
    public DateTime LastPlayed { get; init; }

    /// <summary>Seconds of play in it: its clock, which stops while it isn't being played.</summary>
    public double Time { get; init; }

    /// <summary>How many flights have been launched into it.</summary>
    public int Flights { get; init; }

    /// <summary>The number the next vessel in it will get.</summary>
    public int NextVesselId { get; init; }

    public IReadOnlyList<VesselState> Vessels { get; init; } = [];

    /// <summary>Every vessel lost in it, oldest first, the newest <see cref="Scene.WorldLogEntries"/> kept (see <see cref="WorldLog"/>). A world saved without one has none.</summary>
    public IReadOnlyList<LogEntry> Log { get; init; } = [];

    /// <summary>Its mission runs in progress (those with objectives: free play has nothing to keep), oldest first. Each one's vessels are those with its flight number.</summary>
    public IReadOnlyList<RunState> Runs { get; init; } = [];

    /// <summary>The <see cref="VesselState.Id"/> of the vessel being flown, if one was: what carrying on resumes.</summary>
    public int? Flown { get; init; }
}

/// <summary>
/// A mission run in progress as it is at a moment (see <see cref="MissionRun"/>): everything about
/// it but its vessels, which are the world's with its <see cref="Number"/> as their flight.
/// </summary>
internal sealed record RunState
{
    public required Mission Mission { get; init; }

    /// <summary>The launch site it was launched from, by name (<see cref="LaunchSite.Name"/>).</summary>
    public required string Site { get; init; }

    /// <summary>Its flight number in its world.</summary>
    public required int Number { get; init; }

    /// <summary>Which of its mission's steps are done, one each, in the steps' order.</summary>
    public required bool[] Done { get; init; }

    /// <summary>The points each step was given when it was done (none if it isn't), in the steps' order.</summary>
    public required int[] Points { get; init; }

    /// <summary>The <see cref="VesselState.Id"/> of its vessel flown last.</summary>
    public int LastFlown { get; init; }

    /// <summary>The world's time when it began.</summary>
    public double BegunAt { get; init; }
}
