using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// What the moon trip's pilots have to go on, beside the HUD's readings: the flown vessel's path
/// patched across the bodies' spheres as the line drawn shows it (<see cref="Patched.Legs"/>), and
/// the leg of it round the moon.
/// </summary>
internal static class MoonReadings
{
    private static GroundBody Moon => GroundBody.Moon;

    /// <summary>The flown vessel's patched path, from its centre of mass among its body's stars, with <paramref name="push"/> (m/s, among the stars) added to its velocity (a burn the pilot reckons with).</summary>
    public static void Legs(Flight flight, List<PathLeg> legs, Vector3 push = default)
    {
        var vessel = flight.Controlled;
        Patched.Legs(vessel.Body, vessel.CentreOfMass, vessel.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity) + push, flight.WorldTime, legs);
    }

    /// <summary>The first leg of <paramref name="legs"/> round the moon, if it reaches it.</summary>
    public static PathLeg? MoonLeg(List<PathLeg> legs)
    {
        foreach (var leg in legs)
            if (leg.Body == Moon)
                return leg;
        return null;
    }

    /// <summary>
    /// How the orbit round the moon on <paramref name="leg"/> lies: the sine of its tilt from over
    /// both its poles (its plane's normal along its spin axis: 0 over both poles), and its
    /// periapsis over the moon's datum.
    /// </summary>
    public static (double Polar, double Periapsis) Round(in PathLeg leg) =>
        (Vector3.Dot(Vector3.Normalize(leg.Orbit.Normal), Moon.NorthPole), leg.Orbit.PeriapsisDistance - Moon.SeaRadius);
}

/// <summary>
/// Flies the flown vessel (the upper stage, alone or with the lander on it) from a low orbit round
/// the planet into the moon's sphere, as a player
/// would by the moon's transfer window and the path drawn (the moon targeted, its marker and the
/// line bending round it): it targets the moon and puts the prograde hold on (the orbital frame's,
/// on Auto), waits for the window (the shaded stretch: it burns at its best place, where a burn
/// comes nearest the moon), burns along the way it goes until the line's leg round the moon has its
/// periapsis under <see cref="CutBelow"/> over the moon's datum, then on its orbit some way out
/// trims the path (small burns any way: <see cref="Trim"/>) until the leg round the moon is over
/// its poles with its periapsis at <see cref="Height"/>, and goes on with its engine off and the
/// warp up until it is in the moon's sphere.
/// </summary>
internal sealed class TransferPilot
{
    public enum Phase { Target, Wait, Burn, Out, Trim, OnItsOrbit, Done }

    private static GroundBody Moon => GroundBody.Moon;

    /// <summary>The main burn ends once the moon leg's periapsis is under this over the moon's datum.</summary>
    public float CutBelow { get; init; } = 1000f;

    /// <summary>What the trim brings the moon leg's periapsis to, over the moon's datum: <see cref="MoonTrip.MoonHeight"/> over its highest ground.</summary>
    public float Height { get; init; } = GroundBody.Moon.MaxHeight + MoonTrip.MoonHeight;

    /// <summary>
    /// How long after the main burn it trims (seconds): out along the transfer, where a burn
    /// square to the orbit's plane moves where it arrives (at the burn's own place it would only
    /// tilt the plane about the line through the arrival).
    /// </summary>
    public double TrimAfter { get; init; } = 120;

    /// <summary>The trim is done once the moon leg's tilt from its poles (its sine) and its periapsis are this near.</summary>
    public (double Polar, double Height) Within { get; init; } = (0.004, 15);

    public Phase Now { get; private set; }

    /// <summary>When the main burn began and ended, the fuel then (kg), and the trim's fuel used.</summary>
    public (double Time, float Fuel)? BurnBegan { get; private set; }
    public (double Time, float Fuel)? BurnEnded { get; private set; }
    public (double Time, float Fuel)? TrimEnded { get; private set; }

    /// <summary>How the moon leg lay as the trim ended (its tilt from the poles, its periapsis over the datum), and whether that was within <see cref="Within"/> (or it gave up).</summary>
    public (double Polar, double Periapsis)? Trimmed { get; private set; }
    public bool TrimmedWithin { get; private set; }

    private readonly List<PathLeg> _legs = [];
    private readonly List<Encounters.WindowPoint> _window = [];
    private double _burnAt, _cutAt;

    /// <summary>How the moon leg lies with <paramref name="push"/> added to the velocity now; null if the path misses the moon.</summary>
    private (double Polar, double Periapsis)? Arrival(Flight flight, Vector3 push = default)
    {
        MoonReadings.Legs(flight, _legs, push);
        return MoonReadings.MoonLeg(_legs) is { } leg ? MoonReadings.Round(leg) : null;
    }

    /// <summary>The fuel (kg) in the flown vessel's stage now burning (its lowest): what its burns use.</summary>
    private static float FuelKg(Flight flight) => flight.Fuel * flight.CurrentStage.FuelMass;

    public FlightInput Fly(Flight flight, float dt)
    {
        if (flight.Controlled.State != FlightState.Flying)
            return default;
        if (flight.Body == Moon)
        {
            Now = Phase.Done;
            return flight.PlayerRate > 1f ? new FlightInput(Slower: true, CutThrottle: flight.Throttle > 0f) : default;
        }
        var vessel = flight.Controlled;
        var stars = flight.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity);
        switch (Now)
        {
            case Phase.Target:
                Now = Phase.Wait;
                return new FlightInput(SetTarget: NavTarget.OfBody(Moon), ChooseHold: flight.Holding == HoldMode.Prograde ? null : HoldMode.Prograde);
            case Phase.Wait:
            {
                if (_burnAt == 0)
                {
                    // The window, as the line shades it: its best place, where a burn comes nearest the moon.
                    var mine = new Orbit(flight.Body, vessel.CentreOfMass, stars);
                    Encounters.Transfer(mine, Bodies.OrbitOf(Moon, flight.WorldTime), Moon.SphereRadius, _window);
                    if (_window.Count == 0)
                        throw new InvalidOperationException("no window to the moon on this orbit");
                    var best = _window.MinBy(p => p.Miss);
                    _burnAt = flight.WorldTime + mine.TimeTo(best.Anomaly);
                }
                var lined = Vector3.Dot(flight.Controlled.Up, Vector3.Normalize(flight.FrameVelocity)) > 0.995f;
                if (flight.WorldTime >= _burnAt && lined)
                {
                    Now = Phase.Burn;
                    BurnBegan = (flight.WorldTime, FuelKg(flight));
                    return new FlightInput(FullThrottle: true);
                }
                return default;
            }
            case Phase.Burn:
                if (Arrival(flight) is { } arrival && arrival.Periapsis < CutBelow)
                {
                    Now = Phase.Out;
                    BurnEnded = (flight.WorldTime, FuelKg(flight));
                    _cutAt = flight.WorldTime;
                    return new FlightInput(CutThrottle: true, Faster: true);
                }
                return default;
            case Phase.Out:
                // On its orbit, the warp up, to where the trim is.
                if (flight.WorldTime < _cutAt + TrimAfter)
                    return new FlightInput(Faster: flight.PlayerRate < Flight.Rates[^1] && flight.WorldTime < _cutAt + TrimAfter - 20);
                if (flight.PlayerRate > 1f)
                    return new FlightInput(Slower: true);
                Now = Phase.Trim;
                return default;
            case Phase.Trim:
                return Trim(flight, dt, stars);
            default:
                return new FlightInput(Faster: flight.PlayerRate < Flight.Rates[^1], CutThrottle: flight.Throttle > 0f);
        }
    }

    /// <summary>
    /// The trim, as a player would by the line: the push (among the stars) that would bring the
    /// moon leg's tilt from its poles to 0 and its periapsis to <see cref="Height"/>, the smallest
    /// that does, by the pilot's own reckoning (how each way of pushing moves the leg, tried a
    /// little each way along, across and out); then the nose turned along it and that much burnt,
    /// by what the engine gives, once lined up; then looked at again, and pushed again if still
    /// off, up to <see cref="Tries"/> times.
    /// </summary>
    private FlightInput Trim(Flight flight, float dt, Vector3 stars)
    {
        // What the throttle gave over the frame just flown counts against the push.
        _pushed += flight.MaxAcceleration * flight.Throttle * dt;
        if (_push is { } wanted && _pushed < wanted.Length())
        {
            var way = Vector3.Normalize(wanted);
            var lined = Vector3.Dot(flight.Controlled.Up, way) > 0.998f;
            // Sized so a frame pushes no more than half of what is left.
            var throttle = lined ? Math.Clamp((wanted.Length() - _pushed) * 0.5f / (flight.MaxAcceleration * dt), 0.01f, 1f) : 0f;
            return new FlightInput(Hold: way, Throttle: Pilots.ThrottleTowards(flight, throttle, dt), CutThrottle: !lined && flight.Throttle > 0f);
        }
        if (_push is not null)
        {
            // Burnt: the engine off, and the line looked at again next frame.
            _push = null;
            return new FlightInput(CutThrottle: true);
        }

        if (Arrival(flight) is not { } now)
            throw new InvalidOperationException("the trim lost the moon");
        var (e1, e2) = (now.Polar, now.Periapsis - Height);
        TrimmedWithin = Math.Abs(e1) < Within.Polar && Math.Abs(e2) < Within.Height;
        if (TrimmedWithin || _tries == Tries)
            return TrimDone(flight, now);
        _tries++;
        var up = flight.Body.Up(flight.CentreOfMass);
        var along = Vector3.Normalize(stars);
        var across = Vector3.Normalize(Vector3.Cross(up, along));
        var outward = Vector3.Cross(along, across);
        Span<Vector3> ways = [along, across, outward];
        const float h = 0.05f;
        Span<double> polar = stackalloc double[3], height = stackalloc double[3];
        for (var i = 0; i < 3; i++)
        {
            var tried = Arrival(flight, ways[i] * h) ?? throw new InvalidOperationException("the trim's try lost the moon");
            (polar[i], height[i]) = ((tried.Polar - now.Polar) / h, (tried.Periapsis - now.Periapsis) / h);
        }
        // The least push that answers both: J^T (J J^T)^-1 (-e).
        double a = 0, b = 0, c = 0;
        for (var i = 0; i < 3; i++)
            (a, b, c) = (a + polar[i] * polar[i], b + polar[i] * height[i], c + height[i] * height[i]);
        var det = a * c - b * b;
        if (!(Math.Abs(det) > 1e-12))
            return TrimDone(flight, now); // the line no longer answers a push: given up, as TrimmedWithin says
        var (y1, y2) = ((c * -e1 - b * -e2) / det, (a * -e2 - b * -e1) / det);
        var push = Vector3.Zero;
        for (var i = 0; i < 3; i++)
            push += ways[i] * (float)(polar[i] * y1 + height[i] * y2);
        // No more than MostPush at a time: the line bends too far for one reckoning to carry it.
        if (push.Length() > MostPush)
            push *= MostPush / push.Length();
        (_push, _pushed) = (push, 0f);
        return new FlightInput(Hold: Vector3.Normalize(push));
    }

    private FlightInput TrimDone(Flight flight, (double Polar, double Periapsis) now)
    {
        Now = Phase.OnItsOrbit;
        TrimEnded = (flight.WorldTime, FuelKg(flight));
        Trimmed = now;
        return new FlightInput(CutThrottle: flight.Throttle > 0f, Faster: true);
    }

    /// <summary>How many times the trim looks at the line and pushes, at most.</summary>
    public int Tries { get; init; } = 12;

    /// <summary>The most it pushes on one reckoning (m/s).</summary>
    public float MostPush { get; init; } = 2f;

    private Vector3? _push;
    private float _pushed;
    private int _tries;
}

/// <summary>
/// Captures the flown vessel (the upper stage, alone or with the lander on it) into an orbit round
/// the moon, as a player would by the orbit
/// readouts (the HUD's periapsis and the period, round the moon there) and the retrograde hold:
/// arrived in the moon's sphere, it puts the retrograde hold on (the orbital frame's, on Auto:
/// round the moon's centre), goes on with the warp up to near the periapsis, and burns against the
/// way it goes, centred on the periapsis, until it is going as fast as a round orbit there would
/// (the HUD's "Orbit speed needed"), the throttle eased as it gets there.
/// </summary>
internal sealed class CapturePilot
{
    public enum Phase { Hold, Wait, Burn, Done }

    private static GroundBody Moon => GroundBody.Moon;

    public Phase Now { get; private set; }

    /// <summary>When the burn began and ended, and the fuel then (kg).</summary>
    public (double Time, float Fuel)? BurnBegan { get; private set; }
    public (double Time, float Fuel)? BurnEnded { get; private set; }

    private static float FuelKg(Flight flight) => flight.Fuel * flight.CurrentStage.FuelMass;

    public FlightInput Fly(Flight flight, float dt)
    {
        if (flight.Controlled.State != FlightState.Flying || flight.Body != Moon)
            return default;
        var vessel = flight.Controlled;
        var stars = Moon.AmongStars(vessel.CentreOfMass, vessel.Velocity);
        var radius = Vector3.Distance(vessel.CentreOfMass, Moon.Centre);
        var round = MathF.Sqrt(Moon.Mu / radius);
        switch (Now)
        {
            case Phase.Hold:
                Now = Phase.Wait;
                return new FlightInput(ChooseHold: flight.Holding == HoldMode.Retrograde ? null : HoldMode.Retrograde);
            case Phase.Wait:
            {
                var orbit = new Orbit(Moon, vessel.CentreOfMass, stars);
                var toPeriapsis = orbit.NextPeriapsis is { } p ? orbit.TimeTo(p) : 0;
                // The burn takes about this long: half of it before the periapsis.
                var periapsisSpeed = orbit.VelocityAt(0).Length();
                var burn = (periapsisSpeed - MathF.Sqrt(Moon.Mu / (float)orbit.PeriapsisDistance)) / flight.MaxAcceleration;
                var lined = Vector3.Dot(flight.Controlled.Up, -Vector3.Normalize(flight.FrameVelocity)) > 0.995f;
                if (toPeriapsis > 20)
                    return new FlightInput(Faster: flight.PlayerRate < Flight.Rates[^1] && toPeriapsis > 40);
                if (flight.PlayerRate > 1f)
                    return new FlightInput(Slower: true);
                if (toPeriapsis <= burn / 2 && lined)
                {
                    Now = Phase.Burn;
                    BurnBegan = (flight.WorldTime, FuelKg(flight));
                    goto case Phase.Burn;
                }
                return default;
            }
            case Phase.Burn:
            {
                var left = stars.Length() - round;
                if (left <= 0.02f)
                {
                    Now = Phase.Done;
                    BurnEnded = (flight.WorldTime, FuelKg(flight));
                    return new FlightInput(CutThrottle: true);
                }
                var throttle = Math.Clamp(left * 0.5f / (flight.MaxAcceleration * dt), 0.01f, 1f);
                return new FlightInput(Throttle: Pilots.ThrottleTowards(flight, throttle, dt));
            }
            default:
                return default;
        }
    }
}

/// <summary>
/// Lands the flown vessel (the upper stage, or the lander) from an orbit round a body on a
/// <see cref="PointOfInterest"/> there (by default the moon's landing spot,
/// <see cref="PointOfInterest.MoonLandingSpot"/>), as a player would by the line's landing mark,
/// the retrograde hold and what the HUD shows (the spot's marker: it is no site to target): it
/// puts the retrograde hold
/// on (on Auto, the orbital frame's round the moon); with the spot some way ahead
/// (<see cref="LineUpWhenAhead"/>) it burns square to its orbit until its track over the ground
/// (the moon turning under it) passes over the spot; it waits for the spot to come round to within
/// <see cref="BurnWhenAhead"/>, and burns against the way it goes round the moon (among its stars)
/// until the line's landing mark, sweeping back towards it, has come to within its lead (<see cref="Lead"/>)
/// beyond the spot; then, Auto having turned to the surface frame, it
/// brakes by the surface frame's retrograde hold and comes down on the spot, putting its legs out
/// on the way (<see cref="HoldLanding"/>, by the body's pull and with no air); landed, a vessel
/// with a bay opens its doors (<see cref="FlightInput.ToggleBayDoors"/>, as the vessel's actions
/// menu does).
/// </summary>
internal sealed class MoonLandingPilot
{
    public enum Phase { Hold, LineUp, Wait, Burn, Land }

    /// <param name="spot">Where to land; by default the moon's landing spot.</param>
    public MoonLandingPilot(PointOfInterest? spot = null)
    {
        Spot = spot ?? PointOfInterest.MoonLandingSpot;
        Landing = new()
        {
            Place = Spot.Place, LegsBelow = 600f, StopAbove = 60f, DownSpeed = 3f, FlareAt = 6f, LastSpeed = 1f,
            MaxSideLean = 0.6f, SideSpeed = 8f, CutAtStop = true, DownBrake = 0.8f,
        };
    }

    /// <summary>Where it lands.</summary>
    public PointOfInterest Spot { get; }

    private GroundBody Body => Spot.Body;

    /// <summary>
    /// Line its track up with the spot once the spot is this close ahead, round the moon
    /// (radians): 90°, where a burn square to the orbit moves the track over the spot the most, so a
    /// track far off (a lap waited, the moon turning under it: some 800 m) is lined up within the
    /// pass, before the de-orbit burn. A spot
    /// already within <see cref="BurnWhenAhead"/> as it begins (a lander released late) waits the
    /// lap, its engine off.
    /// </summary>
    public float LineUpWhenAhead { get; init; } = 1.57f;

    /// <summary>The track is lined up once it passes within this of the spot (metres).</summary>
    public float LinedWithin { get; init; } = 2f;

    /// <summary>
    /// Start the de-orbit burn once the spot is this close ahead, round the moon (radians): some
    /// 50°, so the way down comes onto the ground steeply, clear of the hills along the way.
    /// </summary>
    public float BurnWhenAhead { get; init; } = 0.85f;

    /// <summary>
    /// Stop the burn once the landing mark is no further than its lead beyond the spot: braking on
    /// the hold only shortens the way down, so the mark is left beyond it, by as far as braking from
    /// this speed (m/s) at the vessel's push would take. A gentler push brakes for longer and from
    /// higher, against the pull as much as the speed along, so it shortens the way down by more:
    /// some 300 m for the upper stage (some 21 m/s² with the fuel it arrives with), some 2.2 km for
    /// the lander (3 m/s²); tried for the lander at leads from 300 m to 2.5 km, it came down 318 m
    /// short of the spot at 300 m, 15 m short at 1.2 km, and on the spot from 1.5 km on.
    /// </summary>
    public float LeadSpeed { get; init; } = 114f;

    /// <summary>The lead (m) for a vessel whose push at full throttle is <paramref name="acceleration"/> (m/s²): see <see cref="LeadSpeed"/>.</summary>
    public float Lead(float acceleration) => LeadSpeed * LeadSpeed / (2f * MathF.Max(acceleration, 1e-3f));

    /// <summary>
    /// How hard the de-orbit burn pushes (m/s²): gently, so the landing mark sweeps back a few
    /// metres a frame and the cut comes within the lead (a tenth of the upper stage's push with the
    /// fuel it arrives with; the lander's at some two thirds of its throttle).
    /// </summary>
    public float BurnAcceleration { get; init; } = 2f;

    /// <summary>
    /// The landing on the hold, slower at the end than on the planet (a sixth of its pull, and no
    /// air turning the nose to keep it against the way it goes), to touch down under 2 m/s; and,
    /// as holding itself up there takes a little of the thrust, leaning further aside to steer; and
    /// the engine cut as the braking stops (<see cref="HoldLanding.CutAtStop"/>). The legs go out
    /// 600 m over the ground (<see cref="HoldLanding.LegsBelow"/>).
    /// </summary>
    public HoldLanding Landing { get; }

    public Phase Now { get; private set; }

    /// <summary>When the line-up began, when the de-orbit burn began and ended, and the fuel then (kg).</summary>
    public (double Time, float Fuel)? LineUpBegan { get; private set; }
    public (double Time, float Fuel)? BurnBegan { get; private set; }
    public (double Time, float Fuel)? BurnEnded { get; private set; }

    /// <summary>How far its track passed from the spot before it was lined up (metres).</summary>
    public float? AsideBefore { get; private set; }

    /// <summary>How far beyond the spot the landing mark was as the burn was cut (metres; negative: short of it).</summary>
    public float? MarkAtCut { get; private set; }

    private readonly Paths _paths = new();

    private static float FuelKg(Flight flight) => flight.Fuel * flight.CurrentStage.FuelMass;

    /// <summary>
    /// Where its track over the ground (the moon turning under its orbit) passes the spot: how far
    /// off, to the side its orbit's plane faces (along its normal, its position by its velocity),
    /// in metres; and that normal, as the frame matches the moon's ground now. By the pilot's
    /// own reckoning, along the orbit's next half lap.
    /// </summary>
    private (float Aside, Vector3 Normal) TrackPast(Flight flight, Vector3 spot)
    {
        var vessel = flight.Controlled;
        var orbit = new Orbit(Body, vessel.CentreOfMass, Body.AmongStars(vessel.CentreOfMass, vessel.Velocity));
        var to = Body.Up(spot);
        var (best, bestAt) = (-2f, 0.0);
        for (var s = 0.0; s < orbit.Period / 2; s += 1.0)
        {
            var under = Body.Up(Body.Turned(orbit.PositionAfter(s), -s));
            var near = Vector3.Dot(under, to);
            if (near > best)
                (best, bestAt) = (near, s);
        }
        var normal = Vector3.Normalize(orbit.Normal);
        var turned = Body.TurnedDirection(normal, -bestAt);
        return (Body.Radius * MathF.Asin(Math.Clamp(Vector3.Dot(to, turned), -1f, 1f)), normal);
    }

    public FlightInput Fly(Flight flight, float dt)
    {
        if (flight.Body != Body)
            return default;
        // The flown vessel's own state: the flight's stays flying while any guided vessel of it is
        // (the upper stage, left on its orbit).
        if (flight.Controlled.State == FlightState.Landed)
            return new FlightInput(ToggleBayDoors: flight.Controlled.HasBay && !flight.BayDoorsWanted);
        if (flight.Controlled.State != FlightState.Flying)
            return default;
        var spot = Landing.Place!.Value;
        if (Now == Phase.Hold)
        {
            Now = Phase.LineUp;
            return new FlightInput(ChooseHold: flight.Holding == HoldMode.Retrograde ? null : HoldMode.Retrograde);
        }
        if (Now == Phase.Land)
            return Landing.Fly(flight, dt);

        // The orbit's velocity, among the moon's stars (the orbital frame's): once the burn turns Auto to the
        // surface frame, its retrograde would have the ground's turning across it, and push the track off the spot.
        var velocity = Body.AmongStars(flight.CentreOfMass, flight.Controlled.Velocity);
        var ahead = Pilots.AngleAhead(flight.CentreOfMass, velocity, spot, Body);
        if (Now == Phase.LineUp)
        {
            // Waiting for the spot to come round (a lap, if it has passed: a line-up the pass was too
            // short for carries on at the next), with its engine off.
            if (ahead > LineUpWhenAhead + 0.3f || ahead < BurnWhenAhead + 0.1f && LineUpBegan is null)
                return new FlightInput(Faster: flight.PlayerRate < Flight.Rates[^1] && flight.Throttle == 0f, CutThrottle: flight.Throttle > 0f);
            if (flight.PlayerRate > 1f)
                return new FlightInput(Slower: true);
            if (ahead > LineUpWhenAhead)
                return default;
            LineUpBegan ??= (flight.WorldTime, FuelKg(flight));
            var (aside, normal) = TrackPast(flight, spot);
            AsideBefore ??= aside;
            if (MathF.Abs(aside) < LinedWithin)
            {
                Now = Phase.Wait;
                return new FlightInput(CutThrottle: true, ChooseHold: flight.Holding == HoldMode.Retrograde ? null : HoldMode.Retrograde);
            }
            var way = normal * MathF.Sign(aside);
            var lined = Vector3.Dot(flight.Controlled.Up, way) > 0.995f;
            // Gently, easing off as the track nears the spot: a metre of it is about 6 mm/s here.
            var throttle = lined ? Math.Clamp(MathF.Abs(aside) * 0.01f / (flight.MaxAcceleration * dt), 0.005f, 0.2f) : 0f;
            return new FlightInput(Hold: way, Throttle: Pilots.ThrottleTowards(flight, throttle, dt), CutThrottle: !lined && flight.Throttle > 0f);
        }
        if (Now == Phase.Wait)
        {
            var lined = Vector3.Dot(flight.Controlled.Up, -Vector3.Normalize(velocity)) > 0.995f;
            if (ahead > BurnWhenAhead + 0.3f)
                return new FlightInput(Faster: flight.PlayerRate < Flight.Rates[^1]);
            if (flight.PlayerRate > 1f)
                return new FlightInput(Slower: true);
            if (!(lined && ahead < BurnWhenAhead))
                return default;
            Now = Phase.Burn;
            BurnBegan = (flight.WorldTime, FuelKg(flight));
        }

        if (FrameReadings.Line(flight, _paths).LandingMark is { } mark)
        {
            var beyond = (Pilots.AngleAhead(flight.CentreOfMass, velocity, mark, Body) - ahead) * Body.Radius;
            MarkAtCut = beyond;
            // Cut as soon as the mark is no further than the lead beyond the spot. It first shows
            // where the way down first meets the ground, by the periapsis (half a lap on from the
            // burn: some 5.8 km beyond the spot from the moon orbit), and sweeps back a few metres a
            // frame at this throttle, so it comes into the lead from beyond. Should it ever show short
            // (beyond negative), the burn is cut at once too, rather than burnt on into the
            // ground: the braking can only shorten the way down, and the landing shows how far off.
            if (beyond <= Lead(flight.MaxAcceleration))
            {
                Now = Phase.Land;
                BurnEnded = (flight.WorldTime, FuelKg(flight));
                return new FlightInput(CutThrottle: true);
            }
        }
        // Gently: a few m/s is all it takes. By the push, not the throttle: the upper stage pushes
        // some seven times as hard as the lander.
        var gently = Math.Clamp(BurnAcceleration / MathF.Max(flight.MaxAcceleration, 1e-3f), 0.01f, 1f);
        return new FlightInput(Hold: -Vector3.Normalize(velocity), Throttle: Pilots.ThrottleTowards(flight, gently, dt));
    }
}
