using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace GameOne;

/// <summary>
/// One part of a mission, ticked off on the HUD the first time <see cref="Done"/> says so, and
/// done from then on (see <see cref="MissionProgress"/>), judged against its run's own vessels
/// (<see cref="MissionRun.Vessels"/>), whichever vessel is flown. An optional one is a bonus: the mission
/// succeeds without it. <see cref="Lost"/>, if given, says when it can't be done any more (what it
/// needed has been destroyed): a needed step lost before it is done fails the mission.
/// <see cref="Points"/>, if given, are what it scores, worked out once, as it is done.
/// </summary>
internal sealed record MissionStep(string Label, Func<MissionRun, bool> Done, bool Optional = false, Func<MissionRun, bool>? Lost = null,
    Func<MissionRun, int>? Points = null);

/// <summary>
/// What the player is asked to do on a flight: its name and briefing, the rocket it is flown with,
/// and the steps that make it up. The flight rules (fuel, staging, steering, what counts as a crash)
/// and the controls are the same for every mission; a mission only says what success looks like.
/// </summary>
internal sealed class Mission
{
    public required string Name { get; init; }
    public required string Briefing { get; init; }
    public required Vehicle Vehicle { get; init; }
    public required MissionStep[] Steps { get; init; }

    /// <summary>The height the mission asks the rocket to reach, in metres.</summary>
    public float TargetAltitude { get; init; }

    /// <summary>
    /// Where each of the vehicle's stages should land, if it is to land somewhere (by stage, from
    /// the bottom): it counts once that stage is flying on its own, so not while it is still part of
    /// the rocket. Missing or null for a stage that isn't to land anywhere.
    /// </summary>
    public Vector3?[] LandingSites { get; init; } = [];

    /// <summary>
    /// Places that exist only for this mission, each shown by a marker in the flight's views and on
    /// the map (see <see cref="PointOfInterest"/>): information, never an instruction. Each has a name of its own
    /// in the mission.
    /// </summary>
    public PointOfInterest[] PointsOfInterest
    {
        get => _pointsOfInterest;
        init => _pointsOfInterest = value.Select(p => p.Name).Distinct().Count() == value.Length ? value
            : throw new ArgumentException("A mission's points of interest have names of their own: a label is found by its name.");
    }

    private readonly PointOfInterest[] _pointsOfInterest = [];

    /// <summary>Where a vessel made of stages <paramref name="first"/> to <paramref name="last"/> should land, if anywhere.</summary>
    public Vector3? LandingSiteFor(int first, int last) =>
        first == last && first < LandingSites.Length ? LandingSites[first] : null;

    /// <summary>Whether it asks anything of the player: one with no steps is just flying, with nothing to win, lose or score.</summary>
    public bool HasObjectives => Steps.Length > 0;

    /// <summary>Points for landing safely anywhere (a splashdown too).</summary>
    public const int SafeLandingPoints = 1000;

    /// <summary>
    /// Points for how a vessel came down on its landing site: up to 1500 for how close to the
    /// centre, 1000 for how gently and 2000 for the fuel it has left. Nothing if it isn't landed there.
    /// </summary>
    public static int TargetLandingPoints(Vessel? vessel)
    {
        if (!LandedOnTarget(vessel))
            return 0;
        var accuracy = 1f - Math.Clamp(vessel.DistanceToLandingSite / Scene.LandingTargetRadius, 0f, 1f);
        var softness = 1f - Math.Clamp(vessel.TouchdownSpeed / Scene.TouchdownLimit(vessel.Body, Scene.SafeTouchdownSpeed), 0f, 1f);
        return (int)MathF.Round(1500f * accuracy + 1000f * softness + 2000f * vessel.Fuel);
    }

    /// <summary>
    /// Points for how near a landing on the moon came down to its landing spot,
    /// <paramref name="metres"/> along the moon's ground: 3000 on the spot, falling evenly to none at
    /// <see cref="Scene.MoonSpotScoreReach"/> and beyond.
    /// </summary>
    internal static int SpotPoints(float metres) =>
        (int)MathF.Round(3000f * (1f - Math.Clamp(metres / Scene.MoonSpotScoreReach, 0f, 1f)));

    /// <summary>Points for opening a landed lander's bay doors.</summary>
    public const int BayPoints = 500;

    /// <summary>Whether <paramref name="lander"/> is down on the moon: landed there (settled), not flying or afloat, and not on another body.</summary>
    internal static bool LanderDown([NotNullWhen(true)] Vessel? lander) =>
        lander is { State: FlightState.Landed } && lander.Body == PointOfInterest.MoonLandingSpot.Body;

    /// <summary>Whether <paramref name="lander"/> is down on the moon (<see cref="LanderDown"/>) with its bay doors fully open.</summary>
    internal static bool BayOpen(Vessel? lander) => LanderDown(lander) && lander.BayDoors >= 1f;

    /// <summary>Stage <paramref name="stage"/> once it is flying on its own (dropped, or left once the rest has gone), if it is.</summary>
    private static Vessel? Alone(MissionRun run, int stage) =>
        run.Vessels.FirstOrDefault(v => v.Stage == stage && v.TopStage == stage && v.StageCount > 1);

    /// <summary>The booster once it has been dropped, if it has been.</summary>
    private static Vessel? DroppedBooster(MissionRun run) => Alone(run, 0);

    /// <summary>The part above the booster once it has been dropped, if it has been.</summary>
    private static Vessel? UpperStage(MissionRun run) => run.Vessels.FirstOrDefault(v => v.Stage > 0);

    /// <summary>
    /// Whether the mission rocket's stage <paramref name="stage"/> has been destroyed: no vessel of
    /// this run that has it is still in one piece. (Only the rocket's own parts: a fairing half
    /// is a vehicle of its own.)
    /// </summary>
    private static bool Gone(MissionRun run, int stage) =>
        !run.Vessels.Any(v => v.Vehicle == run.Mission.Vehicle && v.State != FlightState.Crashed && v.Stage <= stage && v.TopStage >= stage);

    /// <summary>Whether it has come down safely anywhere: landed, or floating in the sea.</summary>
    private static bool Landed([NotNullWhen(true)] Vessel? vessel) => vessel is { State: FlightState.Landed or FlightState.Floating };
    private static bool LandedOnTarget([NotNullWhen(true)] Vessel? vessel) => vessel is { State: FlightState.Landed, LandedOnTarget: true };

    /// <summary>
    /// Launch from the pad, climb past the target altitude, then fly the booster across to the
    /// landing zone and set it down there, as a reusable booster does.
    /// </summary>
    public static readonly Mission LaunchAndLand = new()
    {
        Name = "LAUNCH AND LAND",
        Briefing = $"Climb past {Scene.TargetAltitude:0} m, then fly the booster to the landing zone and set it down there.",
        Vehicle = Vehicle.Booster,
        TargetAltitude = Scene.TargetAltitude,
        LandingSites = [Scene.LandingZoneCentre],
        Steps =
        [
            new("Launch", f => f.Vessels[0].State != FlightState.Ready), // this run's rocket, not whatever is flown
            new($"Climb past {Scene.TargetAltitude:0} m", f => f.Vessels[0].ReachedTarget), // the rocket, not whatever is flown
            // The rocket launched, not whatever is flown: an earlier flight's booster on the zone isn't this one's landing.
            new("Land on the landing zone", f => LandedOnTarget(f.Vessels[0]), Lost: f => Gone(f, 0),
                Points: f => SafeLandingPoints + TargetLandingPoints(f.Vessels[0])),
        ],
    };

    /// <summary>
    /// Fly the two-stage rocket into a stable orbit: one whose lowest point is above the atmosphere,
    /// so it never comes back down. The booster alone can't get there, so it has to be dropped (when
    /// the player chooses). As a bonus, it can be flown back down and landed, if it is dropped with
    /// enough fuel left: anywhere safely, or better, on the drone ship downrange. The points are
    /// 2000 for the orbit, up to 2000 more for the upper stage's fuel left when it is reached, and the booster's
    /// landing (<see cref="SafeLandingPoints"/>, and <see cref="TargetLandingPoints"/> on the ship). A booster that crashes scores nothing, but the
    /// mission still succeeds.
    /// </summary>
    public static readonly Mission ReachOrbit = new()
    {
        Name = "REACH ORBIT",
        Briefing = "Climb out of the atmosphere and into a stable orbit: tip over east as you climb,\n"
            + $"drop the booster, and burn sideways until your orbit's lowest point is above {Scene.AtmosphereHeight:0} m.\n"
            + $"Bonus: drop the booster at the landing-fuel mark ({Vehicle.TwoStage.Stages[0].LandingReserve * 100:0}%), then fly it (Tab) and land it: anywhere safe scores, the drone ship downrange scores most.",
        Vehicle = Vehicle.TwoStage,
        TargetAltitude = Scene.AtmosphereHeight,
        LandingSites = [Scene.DroneShipCentre],
        Steps =
        [
            new("Launch", f => f.Vessels[0].State != FlightState.Ready), // this run's rocket, not whatever is flown
            new("Drop the booster", f => f.Vessels.Count > 1, Lost: f => Gone(f, 1)),
            new($"Climb above the atmosphere ({Scene.AtmosphereHeight:0} m)", f => f.Vessels.Any(v => v.Stage > 0 && v.ReachedTarget), Lost: f => Gone(f, 1)),
            new($"Reach a stable orbit (lowest point above {Scene.AtmosphereHeight:0} m)", f => f.Vessels.Any(v => v.Stage > 0 && v.AchievedOrbit), Lost: f => Gone(f, 1),
                Points: f => 2000 + (int)MathF.Round(2000f * (UpperStage(f)?.Fuel ?? 0f))),
            new("Bonus: land the booster safely", f => Landed(DroppedBooster(f)), Optional: true, Points: _ => SafeLandingPoints),
            new("Bonus: land it on the drone ship", f => LandedOnTarget(DroppedBooster(f)), Optional: true,
                Points: f => TargetLandingPoints(DroppedBooster(f))),
        ],
    };

    /// <summary>
    /// Put a satellite into a circular orbit about 10 km up, and bring back everything else: land
    /// the booster (it comes down downrange, as on the way to orbit) and, once the satellite is away,
    /// the upper stage too. The upper stage leaves the satellite in a low orbit; the satellite's own
    /// engine has to raise it, with two burns: one along its direction of travel to lift the far
    /// side of its orbit, then another at the top to round it out. Each landing counts anywhere
    /// safe; on its landing site it scores more: the booster's is the drone ship downrange, the upper
    /// stage's the landing zone by the launch pad, most of a lap round from where it leaves the
    /// satellite. Points: 2000 for the satellite's orbit, up to 1000 more for its fuel left then, and
    /// each landing (<see cref="SafeLandingPoints"/>, and <see cref="TargetLandingPoints"/> on its site).
    /// </summary>
    public static readonly Mission DeploySatellite = new()
    {
        Name = "DEPLOY A SATELLITE",
        Briefing = $"Put the satellite into a circular orbit {Scene.SatelliteOrbitLow / 1000:0} to {Scene.SatelliteOrbitHigh / 1000:0} km up, and land the rest:\n"
            + "drop the booster at its landing-fuel mark and land it (Tab), jettison the fairing (SPACE) once above the thick air (about 2 km),\n"
            + "reach orbit, release the satellite (SPACE again),\n"
            + "then fly the upper stage (Tab) back down on its legs. On a landing site scores most: the drone ship downrange for the booster, the landing zone by the launch pad for the upper stage.\n"
            + "The satellite's own engine raises its orbit: burn along its direction of travel until the far side is high enough, then again at the top to round it out.",
        Vehicle = Vehicle.ThreeStage,
        TargetAltitude = Scene.AtmosphereHeight,
        LandingSites = [Scene.DroneShipCentre, Scene.LandingZoneCentre],
        Steps =
        [
            new("Launch", f => f.Vessels[0].State != FlightState.Ready), // this run's rocket, not whatever is flown
            new("Land the booster", f => Landed(DroppedBooster(f)), Lost: f => Gone(f, 0), Points: _ => SafeLandingPoints),
            new("Release the satellite", f => Alone(f, 2) is not null, Lost: f => Gone(f, 2)),
            // With its engine off: passing through the band mid-burn isn't the orbit it is left on.
            new($"Raise its orbit: lowest and highest points {Scene.SatelliteOrbitLow / 1000:0} to {Scene.SatelliteOrbitHigh / 1000:0} km up",
                f => Alone(f, 2) is { State: FlightState.Flying, EngineLit: false, Periapsis: >= Scene.SatelliteOrbitLow, Apoapsis: <= Scene.SatelliteOrbitHigh },
                Lost: f => Gone(f, 2), Points: f => 2000 + (int)MathF.Round(1000f * (Alone(f, 2)?.Fuel ?? 0f))),
            new("Land the upper stage", f => Landed(Alone(f, 1)), Lost: f => Gone(f, 1), Points: _ => SafeLandingPoints),
            new("Bonus: the booster on the drone ship", f => LandedOnTarget(DroppedBooster(f)), Optional: true,
                Points: f => TargetLandingPoints(DroppedBooster(f))),
            new("Bonus: the upper stage on the landing zone by the pad", f => LandedOnTarget(Alone(f, 1)), Optional: true,
                Points: f => TargetLandingPoints(Alone(f, 1))),
        ],
    };

    /// <summary>
    /// Land a lander on the moon, as near its landing spot (<see cref="PointOfInterest.MoonLandingSpot"/>,
    /// the mission's point of interest) as the player can, and open its bay doors once it is down.
    /// The lander launcher's booster is the satellite launcher's, landed as there for a bonus; its
    /// upper stage carries the lander to the moon and into an orbit round it, and is left there
    /// (losing it once the lander is away costs nothing); the lander comes down on its own engine
    /// and legs. Losing the lander fails it. Points: the landing (<see cref="SafeLandingPoints"/>
    /// and <see cref="SpotPoints"/> for its nearness to the spot, measured once it has settled), the
    /// bay (<see cref="BayPoints"/>), and the booster's bonuses as <see cref="ReachOrbit"/>'s, on
    /// Home's second drone ship (<see cref="Scene.TripDroneShipCentre"/>), out where the booster
    /// comes down on the climb into the trip's tilted plane.
    /// </summary>
    public static readonly Mission LandOnTheMoon = new()
    {
        Name = "LAND ON THE MOON",
        Briefing = $"Land the lander on the moon, as near the landing spot ({MathF.Abs(Scene.MoonLandingSpot.Latitude):0}° {(Scene.MoonLandingSpot.Latitude >= 0f ? "N" : "S")} on its near side) as you can, and open its bay doors once it is down (right-click it).\n"
            + "Drop the booster at its landing-fuel mark and land it (Tab) for a bonus; jettison the fairing (SPACE) above the thick air (about 2 km).\n"
            + "The upper stage takes the lander to the moon and into an orbit round it; release it (SPACE) and bring it down on its own engine and legs.",
        Vehicle = Vehicle.LanderLauncher,
        TargetAltitude = Scene.AtmosphereHeight,
        LandingSites = [Scene.TripDroneShipCentre], // the booster's: Home's drone ship out on the climb's heading into the trip's tilted plane
        PointsOfInterest = [PointOfInterest.MoonLandingSpot],
        Steps =
        [
            new("Launch", f => f.Vessels[0].State != FlightState.Ready), // this run's rocket, not whatever is flown
            new("Land the lander on the moon", f => LanderDown(Alone(f, 2)), Lost: f => Gone(f, 2),
                Points: f => SafeLandingPoints + SpotPoints(PointOfInterest.MoonLandingSpot.DistanceFrom(Alone(f, 2)!, f.WorldTime))),
            new("Open its bay doors", f => BayOpen(Alone(f, 2)), Lost: f => Gone(f, 2), Points: _ => BayPoints),
            new("Bonus: land the booster safely", f => Landed(DroppedBooster(f)), Optional: true, Points: _ => SafeLandingPoints),
            new("Bonus: the booster on the drone ship", f => LandedOnTarget(DroppedBooster(f)), Optional: true,
                Points: f => TargetLandingPoints(DroppedBooster(f))),
        ],
    };

    /// <summary>
    /// No objectives, for trying things out without a mission's steps, titles or score: flown with
    /// the satellite launcher because staging it gives every smaller part to fly too. It has no
    /// landing sites, so nothing is its target: every landing zone and test pad is just a marker
    /// the player can pick.
    /// </summary>
    public static readonly Mission FreePlay = new()
    {
        Name = "FREE PLAY",
        Briefing = "No objectives: the satellite launcher, to fly as you like. Stage it (SPACE) to fly its parts (Tab); click a pad's marker to make it your target.",
        Vehicle = Vehicle.ThreeStage,
        Steps = [],
    };

    /// <summary>Every mission, in the order the missions menu lists them.</summary>
    public static readonly Mission[] All = [LaunchAndLand, ReachOrbit, DeploySatellite, LandOnTheMoon, FreePlay];
}
