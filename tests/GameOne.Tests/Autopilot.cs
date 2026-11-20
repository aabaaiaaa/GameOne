using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// Flies the booster as a player would, through <see cref="FlightInput"/> alone: it presses launch,
/// climbs at full throttle, cuts the engine, coasts over the top, then falls back and brakes with
/// the engine, steering to its target and putting the legs out on the way down. Just before
/// touchdown it holds its height until it is right over the target, then settles straight down.
/// </summary>
/// <remarks>
/// It steers as a player does with the direction rings: by holding the direction it wants the nose
/// in (<see cref="FlightInput.Hold"/>), which the rocket turns to only as fast as it can. Its
/// throttle input is analog (in-between amounts, as from a gamepad). The settings
/// change how it flies, so tests can make it get things wrong on purpose.
/// </remarks>
internal sealed class Autopilot
{
    /// <summary>Where to land. Null means the landing site the mission gives the vessel being flown.</summary>
    public Vector3? Target { get; init; }

    /// <summary>The altitude to cut the engine at on the way up (it coasts on higher).</summary>
    public float CutOffAltitude { get; init; } = Scene.TargetAltitude + 50f;

    /// <summary>Whether to put the legs out, and how high up.</summary>
    public bool DeployLegs { get; init; } = true;
    public float LegsAltitude { get; init; } = 300f;

    /// <summary>The slowest it will sink in the last few metres, in m/s. Raise it to land too hard.</summary>
    public float TouchdownSpeed { get; init; } = 1.5f;

    /// <summary>How fast it sinks on the way down for each metre it is up, per second: higher comes down more briskly, on less fuel.</summary>
    public float SinkRate { get; init; } = 0.25f;

    /// <summary>The fastest it will let itself fall, in m/s, before braking.</summary>
    public float MaxSinkSpeed { get; init; } = 60f;

    /// <summary>
    /// If above 0, it falls high up as fast as it could still stop in time using this fraction of
    /// its engine's braking (a late, hard burn, as a booster coming back does to save fuel).
    /// </summary>
    public float BrakeFraction { get; init; }

    /// <summary>If set, lean this way (across the ground) the whole flight, rather than steering for the target.</summary>
    public Vector3? FixedLean { get; init; }

    /// <summary>The most it will lean over to steer (sine of the angle).</summary>
    public float MaxLean { get; init; } = 0.35f;

    /// <summary>The fastest it will move across the ground towards the target, in m/s.</summary>
    public float ApproachSpeed { get; init; } = 12f;

    /// <summary>
    /// If it gets low (under 300 m) still further than this from the target, it gives up on the
    /// target and lands where it is, as a player short of fuel would.
    /// </summary>
    public float MaxDivert { get; init; } = float.PositiveInfinity;

    /// <summary>
    /// If set, while it falls high up (over <see cref="CorrectAbove"/> m) it works out where it
    /// would come down with its engine off and, if that is well off the target, burns leaning away
    /// from the miss until it isn't: a boost-back, as a booster coming back does. The turning
    /// ground carries a falling booster east of where it would come down on a still planet (it
    /// stays aloft longer moving east, and is pushed east as it falls), further than the braking
    /// low down can make up.
    /// </summary>
    public bool CorrectsFall { get; init; }

    /// <summary>
    /// With <see cref="CorrectsFall"/>, the height over the ground (m) the boost-back works down
    /// to: below it the fall is left as it is, and the steering and braking low down take up what
    /// is left of the miss.
    /// </summary>
    public float CorrectAbove { get; init; } = 600f;

    private Vector3? _gaveUpAt;
    private bool _correcting;

    private bool _descending;

    /// <summary>
    /// Brings back a booster dropped on the way to orbit: taken over as it falls, it burns back if
    /// it would come down off the target, falls fast, brakes late and hard to save fuel, and leans
    /// harder to lose its speed across the ground.
    /// It lands at <paramref name="target"/>, or else on the landing site the mission gives it.
    /// </summary>
    public static Autopilot ReturningBooster(Vector3? target = null) => new()
    {
        Target = target,
        CutOffAltitude = -1f, // it is coming down already
        SinkRate = 0.5f,
        MaxSinkSpeed = 30f,
        BrakeFraction = 0.6f,
        MaxLean = 0.6f,
        ApproachSpeed = 30f,
        MaxDivert = 400f,
        CorrectsFall = true,
    };

    public FlightInput Fly(Flight flight, float dt)
    {
        switch (flight.State)
        {
            case FlightState.Ready:
                return new FlightInput(Launch: true, FullThrottle: true);
            case not FlightState.Flying:
                return default;
        }

        var target = _gaveUpAt ?? Target ?? flight.LandingSite ?? Scene.RocketStart;
        var up = flight.LocalUp;
        var offset = Pilots.Across(flight.Position - target, up);
        if (_gaveUpAt is null && _descending && flight.Ground < 300f && offset.Length() > MaxDivert)
        {
            // Too far to get there: come down where it will stop.
            _gaveUpAt = target = flight.Position + Pilots.Across(flight.Velocity, up) * 3f;
            offset = Pilots.Across(flight.Position - target, up);
        }
        var velocity = Pilots.Across(flight.Velocity, up);

        if (!_descending && flight.Ground > CutOffAltitude)
            _descending = true;
        if (CorrectsFall && _descending && _gaveUpAt is null && flight.Ground > CorrectAbove
            && Pilots.WhereItFalls(flight.CentreOfMass, flight.Velocity, Planet.Altitude(target)) is { } down)
        {
            // Started at 40 m off and carried on down to 10, so it doesn't stutter on and off; the
            // engine is cut, not throttled down, the moment it is done, or it would overdo it.
            var miss = Pilots.Across(down.Where - target, up);
            var was = _correcting;
            _correcting = miss.Length() > (_correcting ? 10f : 40f);
            if (_correcting)
            {
                // Nearly flat, holding up only half its weight: pushed up harder, it would climb,
                // staying aloft longer and moving where it comes down on ahead of the burn.
                var lift = MathF.Min(0.5f * Scene.SurfaceGravity / flight.MaxAcceleration, 1f);
                var back = up * lift - Vector3.Normalize(miss) * MathF.Sqrt(1f - lift * lift);
                var lined = Vector3.Dot(flight.Aim, back) > 0.99f;
                return new FlightInput(Hold: back, FullThrottle: lined && flight.Throttle < 1f, CutThrottle: !lined && flight.Throttle > 0f);
            }
            if (was)
                return new FlightInput(Hold: up, CutThrottle: true);
        }

        // Throttle: full on the way up; on the way down, whatever holds the descent rate it wants.
        float throttle;
        var cut = false;
        if (!_descending)
            throttle = 1f;
        else
        {
            // Sink fast high up and slowly near the ground ...
            var sink = MathF.Min(flight.Ground * SinkRate, MaxSinkSpeed);
            if (BrakeFraction > 0f)
                sink = MathF.Max(sink, MathF.Sqrt(2f * BrakeFraction * (flight.MaxAcceleration - Scene.SurfaceGravity) * MathF.Max(flight.Ground - 60f, 0f)));
            var wantedVy = -MathF.Max(sink, TouchdownSpeed);
            // ... and low down, hold at about 25 m until it's right over the target.
            if (flight.Ground < 40f && offset.Length() > 2f)
                wantedVy = (25f - flight.Ground) * 0.3f;
            var hover = Scene.SurfaceGravity / flight.MaxAcceleration;
            throttle = Math.Clamp(hover + (wantedVy - flight.VerticalSpeed) * 0.25f, 0f, 1f);
            cut = flight.Ground < 0.2f; // down: engine off, so it can settle
        }

        // Steering: head for the target at up to the approach speed, slowing as it gets there.
        Vector3 lean;
        if (FixedLean is { } fixedLean)
            lean = fixedLean;
        else
        {
            var wantedVelocity = -offset * 0.2f;
            if (wantedVelocity.Length() > ApproachSpeed)
                wantedVelocity = Vector3.Normalize(wantedVelocity) * ApproachSpeed;
            lean = (wantedVelocity - velocity) * 0.06f;
        }
        if (lean.Length() > MaxLean)
            lean = Vector3.Normalize(lean) * MaxLean;
        var aim = Vector3.Normalize(up + lean);

        var legs = DeployLegs && _descending && flight.Ground < LegsAltitude && !flight.LegsWanted;

        return new FlightInput(
            Throttle: Pilots.ThrottleTowards(flight, throttle, dt),
            Hold: aim,
            ToggleLegs: legs,
            CutThrottle: cut);
    }
}

/// <summary>
/// Flies the two-stage rocket into orbit, as a player would, through <see cref="FlightInput"/>
/// alone: a gravity turn (climbing straight up off the pad, then tipping over east more and more
/// as it climbs) at full throttle, dropping the booster when it runs dry (or sooner), until the top of its
/// path is above the atmosphere; then a coast up to that top; then a burn along its direction of
/// travel until the bottom of its orbit is above the atmosphere too.
/// </summary>
internal sealed class OrbitAutopilot
{
    private enum Phase
    {
        Ascent,
        Coast,
        Circularise,
        Done,
    }

    /// <summary>By this altitude the gravity turn has tipped it right over, flying flat.</summary>
    public float TurnAltitude { get; init; } = 2500f;

    /// <summary>Climb until the top of the path is this high.</summary>
    public float TargetApoapsis { get; init; } = Scene.AtmosphereHeight + 400f;

    /// <summary>Burn at the top until the bottom of the orbit is this high.</summary>
    public float TargetPeriapsis { get; init; } = Scene.AtmosphereHeight + 150f;

    /// <summary>Whether to drop the booster. Without it, the upper stage can never fire.</summary>
    public bool Stage { get; init; } = true;

    /// <summary>Drop the booster once it is down to this much fuel (0 to 1): by default, when it runs dry.</summary>
    public float StageAtFuel { get; init; }

    /// <summary>
    /// Whether it lights a rocket standing landed (or afloat) somewhere other than the pad, as
    /// <c>Launch</c> lights only the one on the pad: full throttle lifts it off (the vessel's
    /// take-off).
    /// </summary>
    public bool LightWhenLanded { get; init; }

    /// <summary>
    /// Whether, once in orbit, it holds along the orbit as it does coasting up to the top, rather
    /// than straight up: as a player leaves a stage in orbit, so one done while others are still
    /// flown in turn isn't swung round to point up the moment it is done.
    /// </summary>
    public bool HoldAlongWhenDone { get; init; }

    /// <summary>
    /// The plane to climb into: its normal (the right hand's way round the orbit) among the stars
    /// that match the planet's ground at world time <see cref="NormalAt"/>. The turn tips it over
    /// along that plane, and every burn after steers what is left of its speed out of the plane
    /// away. Null (the default) climbs due east, into whatever plane that gives.
    /// </summary>
    public Vector3? Normal { get; init; }

    /// <summary>The world time whose ground's stars <see cref="Normal"/> is given among.</summary>
    public double NormalAt { get; init; }

    /// <summary>How soon (seconds) it steers to be back in <see cref="Normal"/>'s plane from wherever it is out of it.</summary>
    public float PlaneSeconds { get; init; } = 30f;

    /// <summary>Whether it has got there: the bottom of the orbit is above <see cref="TargetPeriapsis"/>.</summary>
    public bool Done => _phase == Phase.Done;

    private Phase _phase;

    /// <summary>
    /// Flies the flown vessel (<see cref="Flight"/>'s properties are its). It goes by that vessel's
    /// own state, not <see cref="Flight.State"/>, which reads Flying while any guided vessel of the
    /// flight flies: landed, afloat or crashed, the flown one is flown no further (or, with
    /// <see cref="LightWhenLanded"/>, lit again).
    /// </summary>
    public FlightInput Fly(Flight flight, float dt) => Fly(flight.Controlled, flight.WorldTime);

    /// <summary>
    /// Flies <paramref name="vessel"/>, reading nothing but it (and, for <see cref="Normal"/>, the
    /// world's time now, <paramref name="worldTime"/>): so a pilot each can fly several rockets in
    /// turn, each its own, whichever is flown.
    /// </summary>
    public FlightInput Fly(Vessel vessel, double worldTime)
    {
        switch (vessel.State)
        {
            case FlightState.Ready:
                return new FlightInput(Launch: true, FullThrottle: true);
            case FlightState.Landed or FlightState.Floating when LightWhenLanded:
                return new FlightInput(FullThrottle: true);
            case not FlightState.Flying:
                return default;
        }

        var up = vessel.LocalUp;
        var east = Planet.East(vessel.Position);
        var stars = Planet.AmongStars(vessel.CentreOfMass, vessel.Velocity); // the orbit's velocity, which the burns at the top shape
        var throttle = 0f;
        var aim = up;
        // The plane's normal in the ground's frame now (the ground has turned under the stars since NormalAt).
        var normal = Normal is { } n ? Planet.TurnedDirection(n, NormalAt - worldTime) : (Vector3?)null;
        // With a plane to keep to, the speed it has out of the plane past what would bring it back
        // into it in PlaneSeconds (it is carried out of it as it climbs, before the ground's spin
        // across it is burnt away).
        var outOf = normal is { } o ? Vector3.Dot(stars, o) + Vector3.Dot(vessel.CentreOfMass - Planet.Centre, o) / PlaneSeconds : 0f;
        // Along the way it goes, and against that speed out of the plane as much again.
        var along = Vector3.Normalize(Pilots.Across(normal is { } m ? stars - 2f * m * outOf : stars, up));

        switch (_phase)
        {
            case Phase.Ascent:
                // The turn is reckoned from the height over Home's pad, where it starts upright; it
                // tips over east or, with a plane, towards the orbit's speed along the plane from
                // the speed it has now (the ground's spin at the pad first), so what it has out of
                // the plane is burnt away as it climbs.
                var tilt = MathF.PI / 2 * MathF.Sqrt(Math.Clamp((Planet.Altitude(vessel.Position) - Scene.HomeElevation) / TurnAltitude, 0f, 1f));
                var over = east;
                if (normal is { } plane)
                {
                    var circle = MathF.Sqrt(Planet.Mu / (Planet.SeaRadius + TargetPeriapsis));
                    var wanted = Vector3.Normalize(Vector3.Cross(plane, up)) * circle - Pilots.Across(stars, up) - plane * (outOf - Vector3.Dot(stars, plane));
                    over = Vector3.Normalize(Pilots.Across(wanted, up));
                }
                aim = up * MathF.Cos(tilt) + over * MathF.Sin(tilt);
                throttle = 1f;
                if (vessel.Apoapsis > TargetApoapsis)
                    _phase = Phase.Coast;
                break;
            case Phase.Coast:
                aim = along;
                if (vessel.VerticalSpeed < 2f || Planet.Altitude(vessel.Position) > vessel.Apoapsis - 30f)
                    _phase = Phase.Circularise;
                break;
            case Phase.Circularise:
                aim = along;
                throttle = 1f;
                if (vessel.Periapsis > TargetPeriapsis)
                    _phase = Phase.Done;
                break;
            case Phase.Done when HoldAlongWhenDone:
                aim = along;
                break;
        }

        return new FlightInput(
            Hold: aim,
            FullThrottle: throttle > 0f && vessel.Throttle < 1f,
            CutThrottle: throttle == 0f && vessel.Throttle > 0f,
            Stage: Stage && vessel.CurrentStage.Kind == StageKind.Booster && vessel.CanStage && vessel.Fuel <= StageAtFuel);
    }
}

/// <summary>
/// Flies the orbit mission and its bonus, one vessel at a time as a player would: the rocket up,
/// dropping the booster at its landing-fuel mark, until the upper stage is coasting up to the top
/// of its arc; then over to the booster (Tab), to land it downrange; then back to the upper stage,
/// to circularise.
/// </summary>
internal sealed class OrbitAndReturnPilot
{
    private readonly OrbitAutopilot _orbit = new()
    {
        StageAtFuel = Vehicle.TwoStage.Stages[0].LandingReserve,
        // Higher than usual: the coast up to it leaves time to land the booster.
        TargetApoapsis = Scene.AtmosphereHeight + 1500f,
    };
    private readonly Autopilot _lander;
    private Vessel? _booster;

    /// <param name="target">Where to land the booster: by default, on the drone ship.</param>
    public OrbitAndReturnPilot(Vector3? target = null) => _lander = Autopilot.ReturningBooster(target);

    public FlightInput Fly(Flight flight, float dt)
    {
        if (_booster is null)
        {
            if (flight.Stage == 1 && flight.Throttle == 0f)
            {
                _booster = flight.Vessels.Single(v => v != flight.Controlled);
                return new FlightInput(SwitchVessel: true);
            }
            return _orbit.Fly(flight, dt);
        }
        if (flight.Controlled == _booster)
            return _booster.State == FlightState.Flying ? _lander.Fly(flight, dt) : new FlightInput(SwitchVessel: true);
        return _orbit.Fly(flight, dt);
    }
}

/// <summary>
/// Flies the satellite mission as a player would, one vessel at a time: up as for orbit, landing
/// the booster downrange while the upper stage coasts; then jettisoning the fairing once above the
/// atmosphere, circularising, releasing the satellite,
/// and flying the upper stage on round the planet until the launch site is coming up; then a burn
/// against its direction of travel, just long enough that it would come down there, and a landing.
/// Then back to the satellite, to raise its orbit: a burn along its direction of travel until the
/// far side of its orbit is high enough, a coast up to there, and a burn there to round it out.
/// </summary>
internal sealed class SatellitePilot
{
    private enum Phase
    {
        Ascent,
        Booster,
        Orbit,
        Release,
        Wait,
        Deorbit,
        Fall,
        Land,
        Raise,
        Transfer,
        Circularise,
        Done,
    }

    /// <summary>Raise the satellite's orbit until its highest point is this high ...</summary>
    public float RaiseTo { get; init; } = (Scene.SatelliteOrbitLow + Scene.SatelliteOrbitHigh) / 2;

    /// <summary>... then, at that highest point, burn until its lowest point is this high.</summary>
    public float CirculariseTo { get; init; } = Scene.SatelliteOrbitLow + 600f;

    private readonly OrbitAutopilot _orbit = new()
    {
        StageAtFuel = Vehicle.ThreeStage.Stages[0].LandingReserve,
        TargetApoapsis = Scene.AtmosphereHeight + 1500f, // the coast up to it leaves time to land the booster
    };
    private readonly Autopilot _booster;
    private readonly Autopilot _upper;
    private readonly Vector3 _home;
    private Phase _phase;
    private Vessel? _boosterVessel, _upperVessel;

    /// <summary>Don't start the burn down until home is this close ahead, round the planet (radians).</summary>
    public float DeorbitAngle { get; init; } = 0.5f;

    /// <param name="home">Where to land the upper stage: by default, the landing zone by the launch pad.</param>
    /// <param name="booster">Where to land the booster: by default, on the drone ship.</param>
    public SatellitePilot(Vector3? home = null, Vector3? booster = null)
    {
        _home = home ?? Scene.LandingZoneCentre;
        _upper = Autopilot.ReturningBooster(_home);
        _booster = Autopilot.ReturningBooster(booster);
    }

    /// <summary>Where it has got to, for reports.</summary>
    public string PhaseName => _phase.ToString();

    public FlightInput Fly(Flight flight, float dt)
    {
        switch (_phase)
        {
            case Phase.Ascent:
                if (flight.Stage == 1 && flight.Throttle == 0f)
                {
                    _boosterVessel = flight.Vessels.Single(v => v.Stage == 0);
                    _phase = Phase.Booster;
                    return new FlightInput(SwitchVessel: true);
                }
                return _orbit.Fly(flight, dt);

            case Phase.Booster:
                if (flight.Controlled != _boosterVessel)
                    return new FlightInput(SwitchVessel: true);
                if (_boosterVessel.State == FlightState.Flying)
                    return _booster.Fly(flight, dt);
                _phase = Phase.Orbit;
                return new FlightInput(SwitchVessel: true);

            case Phase.Orbit:
                if (flight.Controlled.Stage != 1)
                    return new FlightInput(SwitchVessel: true);
                if (flight.JettisonsFairing && Planet.Altitude(flight.Position) > Scene.AtmosphereHeight)
                    return _orbit.Fly(flight, dt) with { Stage = true }; // clear of the air: the fairing can go
                if (flight.AchievedOrbit)
                {
                    _phase = Phase.Release;
                    return new FlightInput(Stage: true, CutThrottle: true);
                }
                return _orbit.Fly(flight, dt);

            case Phase.Release:
                // The satellite flies on as the vessel being flown; the upper stage is the part left behind.
                _upperVessel = flight.Vessels.Single(v => v is { Stage: 1, TopStage: 1 });
                _phase = Phase.Wait;
                return new FlightInput(SwitchVessel: true);

            case Phase.Wait or Phase.Deorbit or Phase.Fall or Phase.Land:
                if (flight.Controlled != _upperVessel)
                    return new FlightInput(SwitchVessel: true);
                if (_upperVessel!.State != FlightState.Flying)
                {
                    _phase = Phase.Raise;
                    return new FlightInput(SwitchVessel: true);
                }
                return FlyUpper(flight, dt);

            default:
                if (flight.Controlled.CurrentStage.Kind != StageKind.Satellite)
                    return new FlightInput(SwitchVessel: true);
                return FlySatellite(flight);
        }
    }

    /// <summary>The satellite's two burns: one along its direction of travel to lift the far side of its orbit, one at the top to round it out.</summary>
    private FlightInput FlySatellite(Flight flight)
    {
        var up = flight.LocalUp;
        var stars = Planet.AmongStars(flight.CentreOfMass, flight.Velocity); // the orbit's velocity
        Vector3 aim;
        switch (_phase)
        {
            case Phase.Raise:
                aim = Vector3.Normalize(stars);
                if (flight.Apoapsis >= RaiseTo)
                    _phase = Phase.Transfer;
                break;
            case Phase.Transfer:
                aim = Vector3.Normalize(Pilots.Across(stars, up));
                if (flight.VerticalSpeed < 1f && Planet.Altitude(flight.Position) > flight.Apoapsis - 200f)
                    _phase = Phase.Circularise;
                break;
            case Phase.Circularise:
                aim = Vector3.Normalize(Pilots.Across(stars, up));
                if (flight.Periapsis >= CirculariseTo)
                    _phase = Phase.Done;
                break;
            default:
                aim = Vector3.Normalize(stars);
                break;
        }
        // Burning in the phase it is in now (so the engine is cut the moment a burn is done), and
        // only once pointing the right way, or it would bend the orbit off.
        var lined = Vector3.Dot(flight.Aim, aim) > 0.995f;
        var firing = _phase is Phase.Raise or Phase.Circularise && lined;
        return new FlightInput(Hold: aim, FullThrottle: firing && flight.Throttle < 1f, CutThrottle: !firing && flight.Throttle > 0f);
    }

    private FlightInput FlyUpper(Flight flight, float dt)
    {
        var retrograde = -Vector3.Normalize(Planet.AmongStars(flight.CentreOfMass, flight.Velocity)); // against the orbit, among the stars
        switch (_phase)
        {
            case Phase.Wait:
                // Turn tail first, and wait for home to come round.
                if (Pilots.AngleAhead(flight.CentreOfMass, flight.Velocity, _home) < DeorbitAngle)
                    _phase = Phase.Deorbit;
                return new FlightInput(Hold: retrograde);

            case Phase.Deorbit:
                // Burn until it would come down at home (it falls the rest of the way).
                var lined = Vector3.Dot(flight.Aim, retrograde) > 0.995f;
                if (Pilots.HowFarItFalls(flight.CentreOfMass, flight.Velocity) <= Pilots.AngleAhead(flight.CentreOfMass, flight.Velocity, _home))
                {
                    _phase = Phase.Fall;
                    return new FlightInput(Hold: retrograde, CutThrottle: true);
                }
                return new FlightInput(Hold: retrograde, FullThrottle: lined && flight.Throttle < 1f, CutThrottle: !lined && flight.Throttle > 0f);

            case Phase.Fall:
                if (flight.Ground < 2000f && flight.VerticalSpeed < 0f)
                    _phase = Phase.Land;
                return new FlightInput(Hold: retrograde);

            default:
                return _upper.Fly(flight, dt);
        }
    }
}

/// <summary>What the autopilots share: turning wanted settings into inputs.</summary>
internal static class Pilots
{
    /// <summary>The part of <paramref name="v"/> across the ground, without its part along <paramref name="up"/>.</summary>
    public static Vector3 Across(Vector3 v, Vector3 up) => v - up * Vector3.Dot(v, up);

    /// <summary>The throttle input that moves it to <paramref name="wanted"/>, as fast as the throttle can move.</summary>
    public static float ThrottleTowards(Flight flight, float wanted, float dt) =>
        Math.Clamp((wanted - flight.Throttle) / (Scene.ThrottleRate * dt), -1f, 1f);

    /// <summary>
    /// How far round <paramref name="body"/> (the planet, by default) <paramref name="target"/> is
    /// ahead of <paramref name="position"/>, in the direction of travel, in its frame: radians, 0 to 2 pi.
    /// </summary>
    public static float AngleAhead(Vector3 position, Vector3 velocity, Vector3 target, GroundBody? body = null)
    {
        var centre = (body ?? GroundBody.Planet).Centre;
        var r = Vector3.Normalize(position - centre);
        var t = Vector3.Normalize(target - centre);
        var normal = Vector3.Normalize(Vector3.Cross(r, velocity));
        var forward = Vector3.Cross(normal, r);
        var angle = MathF.Atan2(Vector3.Dot(t, forward), Vector3.Dot(t, r));
        return angle < 0f ? angle + MathF.Tau : angle;
    }

    /// <summary>
    /// How far round the planet (radians) something at <paramref name="position"/> moving at
    /// <paramref name="velocity"/> (over the ground) would go before it hit the ground with its
    /// engine off; infinite if it doesn't come down within a lap (it is in orbit, or near enough).
    /// </summary>
    public static float HowFarItFalls(Vector3 position, Vector3 velocity) =>
        WhereItFalls(position, velocity, Scene.HomeElevation)?.Travelled ?? float.PositiveInfinity; // down to the height of Home's pad

    /// <summary>
    /// Where something at <paramref name="position"/> moving at <paramref name="velocity"/> over
    /// the ground would come down to <paramref name="downTo"/> above the sea with its engine off,
    /// and how far round the planet it went (radians), through gravity, the turning ground's pulls
    /// and the air as the physics has them, in the ground's frame; null if it doesn't come down
    /// within a lap. Each step is the physics' own (<see cref="Planet.FallStep"/>, a kick and a
    /// drift), only longer: a plain step drifted tens of metres over a long fall.
    /// </summary>
    public static (Vector3 Where, float Travelled)? WhereItFalls(Vector3 position, Vector3 velocity, float downTo)
    {
        const float step = 0.1f;
        var travelled = 0f;
        while (travelled < MathF.Tau)
        {
            var drag = Scene.DragRate * Planet.AirDensity(Planet.Altitude(position));
            velocity += Planet.FallStep(velocity, Planet.Gravity(position) + Planet.OutwardPull(position), drag, step);
            var next = position + velocity * step;
            travelled += MathF.Acos(Math.Clamp(Vector3.Dot(Planet.Up(position), Planet.Up(next)), -1f, 1f));
            position = next;
            if (Planet.Altitude(position) <= downTo)
                return (position, travelled);
        }
        return null;
    }
}
