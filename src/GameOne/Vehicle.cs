namespace GameOne;

/// <summary>
/// What a stage is: the booster (with grid fins), the upper stage, or a satellite or a lander riding
/// on top; or half of a payload's fairing, once jettisoned (no engine, no fuel: it only falls).
/// </summary>
internal enum StageKind
{
    Booster,
    Upper,
    Satellite,
    Lander,
    Fairing,
}

/// <summary>
/// One stage of a rocket: how heavy it is empty, how much fuel it carries, and its engine.
/// </summary>
/// <param name="DryMass">Empty, in kg.</param>
/// <param name="FuelMass">Full of fuel, the fuel alone, in kg.</param>
/// <param name="Thrust">The engine at full throttle, in newtons.</param>
/// <param name="BurnSeconds">How long its fuel lasts at full throttle. This sets how much fuel the engine burns.</param>
/// <param name="Nose">Whether it has a nose cone (on an upper stage, a fairing), i.e. nothing is stacked on top of it.</param>
internal sealed record Stage(StageKind Kind, float DryMass, float FuelMass, float Thrust, float BurnSeconds, bool Nose)
{
    /// <summary>
    /// Whether it carries guidance, so it can still be flown once it has come away from the rest:
    /// a booster built to come back, say. One without is just debris once dropped (it can still be
    /// watched). A part of a rocket can be flown if any of its stages is guided.
    /// </summary>
    public bool Guided { get; init; }

    /// <summary>
    /// How much of its fuel (0 to 1) it should still have when it is dropped, to fly itself back
    /// down and land: the HUD marks it on the fuel gauge. 0 for a stage that isn't meant to come back.
    /// </summary>
    public float LandingReserve { get; init; }

    /// <summary>Whether it has landing legs (at the bottom of its body; only the bottom stage's can be put out).</summary>
    public bool Legs { get; init; }

    /// <summary>The shape of its legs, if it has them (<see cref="Legs"/>).</summary>
    public LegGeometry LegGeometry { get; init; } = LegGeometry.Rocket;

    /// <summary>
    /// Whether it rides inside a fairing (a payload on top of the stack), until the fairing is
    /// jettisoned: it adds its fairing's mass (<see cref="FairingGeometry"/>) while it is on, and
    /// without it the stage breaks up in thick, fast air.
    /// </summary>
    public bool Fairing { get; init; }

    /// <summary>
    /// The shape of its fairing: for a payload (<see cref="Fairing"/>), the one round it; for a
    /// fairing half, the fairing it is half of.
    /// </summary>
    public FairingGeometry FairingGeometry { get; init; } = FairingGeometry.Satellite;

    /// <summary>Whether it has a payload bay (the lander's, made for a rover), whose doors fold down as ramps.</summary>
    public bool Bay { get; init; }

    /// <summary>What a part that is this stage at the bottom is called on the HUD.</summary>
    public string Name => Kind switch
    {
        StageKind.Booster => "Booster",
        StageKind.Upper => "Upper stage",
        StageKind.Satellite => "Satellite",
        StageKind.Lander => "Lander",
        _ => "Fairing half",
    };

    /// <summary>Fuel burned per second at full throttle, in kg/s.</summary>
    public float FuelFlow => BurnSeconds > 0f ? FuelMass / BurnSeconds : 0f;

    /// <summary>
    /// How fast its exhaust leaves the engine, in m/s: the speed change its fuel can give it is
    /// this times the natural log of how many times heavier it is full than empty (the rocket equation).
    /// </summary>
    public float ExhaustVelocity => Thrust / FuelFlow;

    /// <summary>From the bottom of its body to the top of its nose (or interstage), in metres.</summary>
    public float Height => Kind switch
    {
        StageKind.Booster => Scene.RocketBodyHeight + (Nose ? Scene.RocketNoseHeight : Scene.InterstageHeight),
        StageKind.Upper => Scene.UpperStageHeight + (Nose ? Scene.UpperNoseHeight : 0f),
        StageKind.Satellite => Scene.SatelliteBodyHeight + Scene.SatelliteNoseHeight,
        StageKind.Lander => Scene.LanderBodyHeight + Scene.LanderAntennaHeight,
        _ => FairingGeometry.Height + FairingGeometry.NoseHeight,
    };

    /// <summary>How far out from its axis its body reaches: a square lander's, to its corners or its bay doors' folded panels (<see cref="RocketGeometry.ShutReach"/>).</summary>
    public float Radius => Kind switch
    {
        StageKind.Satellite => Scene.SatelliteRadius,
        StageKind.Lander => RocketGeometry.ShutReach,
        StageKind.Fairing => FairingGeometry.Radius,
        _ => Scene.RocketRadius,
    };

    // A fairing half has no engine: what hangs under its body, where a bell would, is its foot's
    // cone (a wider fairing's, FairingGeometry.ConeHeight, as narrow as the rocket at its bottom),
    // so its hull is measured and its collider made down to it. One as wide as the rocket has
    // none: its lowest point is the bottom of its edge.
    public float BellHeight => Kind switch
    {
        StageKind.Booster => Scene.RocketBellHeight,
        StageKind.Upper => Scene.UpperBellHeight,
        StageKind.Satellite => Scene.SatelliteBellHeight,
        StageKind.Lander => Scene.LanderBellHeight,
        _ => FairingGeometry.ConeHeight,
    };

    public float BellRadius => Kind switch
    {
        StageKind.Booster => Scene.RocketBellRadius,
        StageKind.Upper => Scene.UpperBellRadius,
        StageKind.Satellite => Scene.SatelliteBellRadius,
        StageKind.Lander => Scene.LanderBellRadius,
        _ => FairingGeometry.ConeHeight > 0f ? Scene.RocketRadius : 0f,
    };
}

/// <summary>
/// A rocket: its stages from the bottom up. Only the bottom stage's engine can fire; staging drops
/// it, and the next stage's engine lights. (An upper stage's engine sits inside the interstage
/// until then, so it couldn't fire anyway.)
/// </summary>
internal sealed record Vehicle(string Name, Stage[] Stages)
{
    /// <summary>
    /// The booster on its own, for flying from pad to landing zone: 2.2 times its full weight at full
    /// throttle, burning for 50 s. It gets lighter as the fuel goes, so it climbs harder by the end.
    /// </summary>
    public static readonly Vehicle Booster = new("Booster",
    [
        new Stage(StageKind.Booster, DryMass: 8000f, FuelMass: 6000f, Thrust: 2.2f * 14000f * Scene.SurfaceGravity, BurnSeconds: 50f, Nose: true) { Guided = true, Legs = true },
    ]);

    /// <summary>
    /// The booster with an upper stage on top, for reaching orbit. The booster lifts the stack off at
    /// about 1.6 times its weight, with an engine as efficient as the landing booster's. It is meant to be
    /// dropped while it still has 40% of its fuel, high and fast but well short of orbit, so it has
    /// enough to come back down and land downrange. The upper stage, heavy with fuel, does most of the
    /// work: 1.5 times its own weight at full throttle for 29 s, about 790 m/s of speed change, against
    /// the 350 or so a tidy ascent needs from there. It is heavy enough that the booster alone can't
    /// lift it into orbit, even burning all its fuel, so it has to be dropped: its fuel is set for that
    /// counting the 57 m/s an eastward launch gets from the planet's turn (with about 150 kg
    /// more, the stack alone makes orbit east from Home). Its thrust is written against a stack of
    /// 22,200 kg; the stack is 21,700 kg, so 1.64 times its weight.
    /// </summary>
    public static readonly Vehicle TwoStage = new("Two-stage rocket",
    [
        new Stage(StageKind.Booster, DryMass: 8000f, FuelMass: 3700f, Thrust: 1.6f * 22200f * Scene.SurfaceGravity, BurnSeconds: 26.8f, Nose: false) { Guided = true, Legs = true, LandingReserve = 0.4f },
        new Stage(StageKind.Upper, DryMass: 2500f, FuelMass: 7500f, Thrust: 1.5f * 10000f * Scene.SurfaceGravity, BurnSeconds: 29f, Nose: true) { Guided = true },
    ]);

    /// <summary>
    /// For deploying a satellite, with every part coming back but the satellite. The booster is the
    /// two-stage rocket's, under a stack just as heavy, so it flies the same and comes back to the
    /// same drone ship downrange. The upper stage has legs and a more efficient engine, to go on
    /// from leaving the satellite in orbit to coming back down and landing: about 1,050 m/s of
    /// speed change with the satellite, against the 350 or so it needs to get there; it should still
    /// have its landing-fuel mark's worth once the satellite is away. The satellite has a small
    /// engine of its own, to raise its orbit to where the mission wants it: about 120 m/s, against
    /// the 40 to 50 that a circular orbit at 10 km takes from where the upper stage leaves it. The
    /// satellite rides inside a fairing until it is jettisoned; the upper stage is that much lighter,
    /// so the stack weighs the same as the orbit rocket's.
    /// </summary>
    public static readonly Vehicle ThreeStage = new("Satellite launcher",
    [
        TwoStage.Stages[0],
        new Stage(StageKind.Upper, DryMass: 2500f - FairingGeometry.Satellite.Mass, FuelMass: 6500f, Thrust: 1.5f * 10000f * Scene.SurfaceGravity, BurnSeconds: 44.2f, Nose: false) { Guided = true, Legs = true, LandingReserve = 0.5f },
        new Stage(StageKind.Satellite, DryMass: 923f, FuelMass: 77f, Thrust: 0.3f * 1000f * Scene.SurfaceGravity, BurnSeconds: 39.4f, Nose: true) { Guided = true, Fairing = true, FairingGeometry = FairingGeometry.Satellite },
    ]);

    /// <summary>Half of a satellite's fairing, once jettisoned: no engine, no fuel, no guidance. It just falls.</summary>
    public static readonly Vehicle FairingHalf = new("Fairing half",
    [
        new Stage(StageKind.Fairing, DryMass: FairingGeometry.Satellite.Mass / 2, FuelMass: 0f, Thrust: 0f, BurnSeconds: 0f, Nose: true) { FairingGeometry = FairingGeometry.Satellite },
    ]);

    // The lander's masses, in kg: empty (the rover its bay is made for counted in), and its fuel.
    private const float LanderDryMass = 600f + Scene.RoverAllowance, LanderFuelMass = 250f;

    /// <summary>
    /// For landing on the moon: the satellite launcher's booster and upper stage, and a lander in
    /// place of the satellite. The upper stage is the satellite launcher's but with no landing
    /// reserve: it carries the lander across to the moon and into an orbit round it, and is left
    /// there. The lander rides under a wider fairing (its bay doesn't fit inside the rocket's), and
    /// with it the stack is some 250 kg heavier than the satellite launcher's, still inside the
    /// 22,200 kg the booster's thrust is written for. The lander is flown down on its own engine:
    /// about 1.8 times its full weight at the moon's surface, less than the upper stage's and more
    /// efficient (an exhaust speed of about 1,500 m/s, against its 1,000), some 370 m/s of speed
    /// change, about three times a landing's from an orbit round the moon. Its dry mass counts the
    /// rover its bay is made for (<see cref="Scene.RoverAllowance"/>). It lands on four legs, which
    /// must be put out for the landing.
    /// </summary>
    public static readonly Vehicle LanderLauncher = new("Lander launcher",
    [
        TwoStage.Stages[0],
        ThreeStage.Stages[1] with { LandingReserve = 0f },
        new Stage(StageKind.Lander, DryMass: LanderDryMass, FuelMass: LanderFuelMass, Thrust: 1.8f * (LanderDryMass + LanderFuelMass) * Scene.MoonSurfaceGravity, BurnSeconds: 111f, Nose: true)
        {
            Guided = true, Legs = true, Bay = true, Fairing = true, LegGeometry = LegGeometry.Lander, FairingGeometry = FairingGeometry.Lander,
        },
    ]);

    /// <summary>Half of the lander's fairing, once jettisoned: as <see cref="FairingHalf"/>, of the lander's wider fairing.</summary>
    public static readonly Vehicle LanderFairingHalf = new("Lander fairing half",
    [
        new Stage(StageKind.Fairing, DryMass: FairingGeometry.Lander.Mass / 2, FuelMass: 0f, Thrust: 0f, BurnSeconds: 0f, Nose: true) { FairingGeometry = FairingGeometry.Lander },
    ]);

    /// <summary>Every vehicle, for finding one by name (a saved world names each vessel's). Names must be unique.</summary>
    public static readonly Vehicle[] All = [Booster, TwoStage, ThreeStage, FairingHalf, LanderLauncher, LanderFairingHalf];

    /// <summary>
    /// The vehicle a half of <paramref name="fairing"/> is, once jettisoned (one of <see cref="All"/>):
    /// matched as the instance its stages hold (<see cref="Stage.FairingGeometry"/>), never by its
    /// numbers, so a copy of one (made by <c>with</c>, even an equal one) has no half.
    /// </summary>
    public static Vehicle HalfOf(FairingGeometry fairing) =>
        Array.Find(All, v => v.Stages[0].Kind == StageKind.Fairing && ReferenceEquals(v.Stages[0].FairingGeometry, fairing))
        ?? throw new ArgumentException("No fairing half is made of this fairing.", nameof(fairing));
}
