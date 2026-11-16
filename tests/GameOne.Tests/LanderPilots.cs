using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// Flies the lander launcher from the pad into a low orbit in a given plane (the moon trip's,
/// <see cref="MoonTrip.Launch"/>), as a player would, one vessel at a time: up by
/// <see cref="OrbitAutopilot"/> turning over along the plane, dropping the booster at its
/// landing-fuel mark; then over to the booster (Tab), flown back by
/// <see cref="Autopilot.ReturningBooster"/>, as <see cref="SatellitePilot"/> does; then back to
/// the stack, jettisoning the fairing once 2.5 km up, and on into the orbit; and last, at the
/// orbit's next crossing of the plane, a burn square to it that turns the orbit into it (the
/// ground's spin across the plane at the pad carries the climb out of it by some 2°, more than the
/// turn, mostly upright then, can steer away). It is done once in the plane with its periapsis
/// over the air and its engine off.
/// </summary>
internal sealed class PlaneAscentPilot
{
    private enum Phase { Ascent, Booster, Orbit, Trim, Done }

    /// <summary>How high the fairing is jettisoned, over the sea: above the thick air, as the mission's briefing says.</summary>
    public const float FairingAltitude = 2500f;

    /// <summary>How near the plane (degrees, the orbit's tilt from it) is near enough to need no burn square to it.</summary>
    public const float WithinDegrees = 0.1f;

    private readonly OrbitAutopilot _orbit;
    private readonly Autopilot _booster = Autopilot.ReturningBooster(); // to the mission's landing site
    private Phase _phase;
    private bool _trimming;
    private float _trimSign; // the way out of the plane its speed was as the trim was lit

    /// <param name="normal">The plane's normal, among the stars matching the planet's ground at world time <paramref name="normalAt"/>.</param>
    public PlaneAscentPilot(Vector3 normal, double normalAt)
    {
        _orbit = new()
        {
            StageAtFuel = Vehicle.LanderLauncher.Stages[0].LandingReserve,
            TargetApoapsis = Scene.AtmosphereHeight + 1500f, // the climb up to it leaves time to land the booster
            Normal = normal,
            NormalAt = normalAt,
        };
    }

    /// <summary>The booster once dropped, and the stack (the upper stage with the lander on it) it was dropped from.</summary>
    public Vessel? Booster { get; private set; }
    public Vessel? Stack { get; private set; }

    /// <summary>Where the booster would have come down to the calm sea's level as it was dropped, left alone with its engine off.</summary>
    public Vector3? FallsAt { get; private set; }

    /// <summary>Whether it is in orbit in the plane: the periapsis over the air, its engine off.</summary>
    public bool Done => _phase == Phase.Done;

    /// <summary>Where it has got to, for reports.</summary>
    public string PhaseName => _phase.ToString();

    public FlightInput Fly(Flight flight, float dt)
    {
        switch (_phase)
        {
            case Phase.Ascent:
                if (flight.Stage == 1 && flight.Throttle == 0f)
                {
                    Stack = flight.Controlled;
                    Booster = flight.Vessels.Single(v => v.Stage == 0);
                    FallsAt = Pilots.WhereItFalls(Booster.CentreOfMass, Booster.Velocity, 0f)?.Where;
                    _phase = Phase.Booster;
                    return new FlightInput(SwitchVessel: true);
                }
                return _orbit.Fly(flight, dt);

            case Phase.Booster:
                if (flight.Controlled != Booster)
                    return new FlightInput(SwitchVessel: true);
                if (Booster.State == FlightState.Flying)
                    return _booster.Fly(flight, dt);
                _phase = Phase.Orbit;
                return new FlightInput(SwitchVessel: true);

            case Phase.Orbit:
                if (flight.Controlled != Stack)
                    return new FlightInput(SwitchVessel: true);
                if (flight.JettisonsFairing && Planet.Altitude(flight.Position) > FairingAltitude)
                    return _orbit.Fly(flight, dt) with { Stage = true };
                if (_orbit.Done && flight.Throttle == 0f && flight.Periapsis > Scene.AtmosphereHeight)
                    _phase = Phase.Trim;
                return _orbit.Fly(flight, dt);

            case Phase.Trim:
                return Trim(flight);

            default:
                return new FlightInput(CutThrottle: flight.Throttle > 0f);
        }
    }

    /// <summary>
    /// The burn square to the plane, where the orbit crosses it: pointed against the speed it has
    /// out of the plane, lit as it comes to the crossing (half the burn's time short of it), and cut
    /// once that speed is gone.
    /// </summary>
    private FlightInput Trim(Flight flight)
    {
        var vessel = flight.Controlled;
        var normal = Planet.TurnedDirection(_orbit.Normal!.Value, _orbit.NormalAt - flight.WorldTime);
        var stars = Planet.AmongStars(vessel.CentreOfMass, vessel.Velocity);
        var r = vessel.CentreOfMass - Planet.Centre;
        float off = Vector3.Dot(r, normal), outOf = Vector3.Dot(stars, normal);
        if (_trimming)
        {
            // Burning against the speed out of the plane it had as it was lit: done once that is
            // gone, or the moment it has gone past (the speed out of the plane the other way).
            if (MathF.Abs(outOf) < 0.05f || MathF.Sign(outOf) != _trimSign)
            {
                _phase = Phase.Done;
                return new FlightInput(Hold: -_trimSign * normal, CutThrottle: true);
            }
            return new FlightInput(Hold: -_trimSign * normal);
        }
        var aim = outOf > 0f ? -normal : normal;
        var tilt = MathF.Sqrt(MathF.Pow(outOf / stars.Length(), 2) + MathF.Pow(off / r.Length(), 2));
        if (tilt < MathF.Sin(float.DegreesToRadians(WithinDegrees)))
        {
            _phase = Phase.Done;
            return default;
        }
        var lined = Vector3.Dot(flight.Aim, aim) > 0.995f;
        var halfBurn = MathF.Abs(outOf) / flight.MaxAcceleration / 2f;
        var coming = off * outOf < 0f && MathF.Abs(off) <= MathF.Abs(outOf) * halfBurn + 0.5f;
        _trimming = lined && coming;
        _trimSign = MathF.Sign(outOf);
        return new FlightInput(Hold: aim, FullThrottle: _trimming);
    }
}

/// <summary>
/// Flies the moon landing mission (<see cref="Mission.LandOnTheMoon"/>) as a player would, leg by
/// leg, one vessel at a time: up from Home's pad into the trip's plane (<see cref="PlaneAscentPilot"/>,
/// the booster flown back meanwhile); across to the moon from the next window on the orbit the
/// ascent leaves it on (<see cref="TransferPilot"/>); captured into an orbit round it
/// (<see cref="CapturePilot"/>); the lander let go (<see cref="Release"/>); and the lander flown
/// down by the spot (<see cref="MoonLandingPilot"/>, at <see cref="PointOfInterest.MoonLandingSpot"/>),
/// its legs out, and its bay opened once it is down. Each leg's end is kept with the fuel then
/// (<see cref="Ends"/>), for the report.
/// </summary>
internal sealed class LanderMissionPilot
{
    public enum Phase { Ascent, Transfer, Capture, Release, Landing, Done }

    /// <summary>
    /// How long it holds the nose square to the orbit, lined up, before it lets the lander go
    /// (seconds): so it has stopped turning, and the push apart is all across the orbit.
    /// </summary>
    public const float SteadySeconds = 3f;

    /// <summary>
    /// How long after it lets go it keeps the lander's nose where it is (seconds), before the
    /// landing turns it about: some 7 m apart by then, at the push apart's 1.5 m/s, clear of the
    /// upper stage's top.
    /// </summary>
    public const float ClearSeconds = 5f;

    /// <param name="launch">The launch the ascent is flown for (<see cref="MoonTrip.Launch"/>); none for a flight that starts later on.</param>
    /// <param name="from">The leg it starts at: the ascent from the pad, or a later one where the test starts.</param>
    public LanderMissionPilot((double Time, Vector3 Normal, double Burn)? launch = null, Phase from = Phase.Ascent)
    {
        if (from == Phase.Ascent)
            Ascent = launch is var (_, normal, burn) ? new PlaneAscentPilot(normal, burn) : throw new ArgumentException("the ascent needs its launch", nameof(launch));
        Now = from;
    }

    public PlaneAscentPilot? Ascent { get; }
    public TransferPilot Transfer { get; } = new();
    public CapturePilot Capture { get; } = new();
    public MoonLandingPilot Landing { get; } = new();

    public Phase Now { get; private set; }

    /// <summary>
    /// Each leg's end: the world time, and the fuel then (kg) in the stage the flown vessel burns
    /// (the upper stage until the lander is let go, then the lander's) and as a fraction of its tank.
    /// </summary>
    public List<(Phase Leg, double Time, float Kg, float Fraction)> Ends { get; } = [];

    /// <summary>The stack as it was let go of the lander: its periapsis and apoapsis (m), the world time, and how far (degrees) its nose was from square to its orbit.</summary>
    public (float Periapsis, float Apoapsis, double Time, float OffSquare)? StackAtRelease { get; private set; }

    /// <summary>The upper stage, once the lander is let go, and the lander.</summary>
    public Vessel? UpperStage { get; private set; }
    public Vessel? Lander { get; private set; }

    private double _linedSince = double.NaN;

    private void End(Flight flight, Phase next)
    {
        Ends.Add((Now, flight.WorldTime, flight.Fuel * flight.CurrentStage.FuelMass, flight.Fuel));
        Now = next;
    }

    public FlightInput Fly(Flight flight, float dt)
    {
        switch (Now)
        {
            case Phase.Ascent:
                if (!Ascent!.Done)
                    return Ascent.Fly(flight, dt);
                End(flight, Phase.Transfer);
                goto case Phase.Transfer;
            case Phase.Transfer:
                if (Transfer.Now != TransferPilot.Phase.Done)
                    return Transfer.Fly(flight, dt);
                End(flight, Phase.Capture);
                goto case Phase.Capture;
            case Phase.Capture:
                if (Capture.Now != CapturePilot.Phase.Done)
                    return Capture.Fly(flight, dt);
                End(flight, Phase.Release);
                goto case Phase.Release;
            case Phase.Release:
                return Release(flight, dt);
            case Phase.Landing:
                if (!(flight.Controlled.State == FlightState.Landed && flight.BayDoors >= 1f))
                    return Landing.Fly(flight, dt);
                End(flight, Phase.Done);
                return default;
            default:
                return default;
        }
    }

    /// <summary>
    /// The lander let go, as a player would with care: the engine off and the warp down, the nose
    /// turned square to the orbit round the moon (along its normal) and held there
    /// <see cref="SteadySeconds"/>, so the push apart (<see cref="Scene.SeparationSpeed"/>) tilts
    /// each orbit a little and barely changes its size or shape; then staged (SPACE), and the
    /// lander's nose held there another <see cref="ClearSeconds"/>, clear of the upper stage.
    /// </summary>
    private FlightInput Release(Flight flight, float dt)
    {
        var vessel = flight.Controlled;
        var body = flight.Body; // the body it is round (the moon, on this trip)
        var stars = body.AmongStars(vessel.CentreOfMass, vessel.Velocity);
        var normal = Vector3.Normalize(Vector3.Cross(vessel.CentreOfMass - body.Centre, stars));
        if (StackAtRelease is { } released)
        {
            // The upper stage left behind: the newest of the lander launcher's upper stages alone.
            UpperStage ??= flight.WorldVessels.Where(v => v.Vehicle == Vehicle.LanderLauncher && v is { Stage: 1, TopStage: 1 }).MaxBy(v => v.Id);
            if (flight.WorldTime < released.Time + ClearSeconds)
                return new FlightInput(Hold: normal);
            End(flight, Phase.Landing);
            return Landing.Fly(flight, dt);
        }
        if (flight.PlayerRate > 1f || flight.Throttle > 0f)
            return new FlightInput(Hold: normal, Slower: flight.PlayerRate > 1f, CutThrottle: flight.Throttle > 0f);
        if (Vector3.Dot(vessel.Up, normal) < 0.9995f)
        {
            _linedSince = double.NaN;
            return new FlightInput(Hold: normal);
        }
        if (double.IsNaN(_linedSince))
            _linedSince = flight.WorldTime;
        if (flight.WorldTime < _linedSince + SteadySeconds)
            return new FlightInput(Hold: normal);
        StackAtRelease = (vessel.Periapsis, vessel.Apoapsis, flight.WorldTime, float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(vessel.Up, normal), -1f, 1f))));
        Lander = vessel; // the flown vessel goes on as the part above: the lander
        return new FlightInput(Hold: normal, Stage: true);
    }
}
