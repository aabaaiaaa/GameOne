using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// What a player flying by the frames has to go on, beside the readings <see cref="Flight"/> gives
/// the HUD: the line the HUD draws and its landing mark (built as <see cref="Paths"/> draws them),
/// and where one place on the ground is from another, as the markers show them: at the body the
/// vessel is at (the planet's ground, or the moon's).
/// </summary>
internal static class FrameReadings
{
    /// <summary>The line as the HUD would draw it now for the flown vessel (with its nodes, if it has any), built into <paramref name="paths"/>.</summary>
    public static Paths Line(Flight flight, Paths paths)
    {
        var vessel = flight.Controlled;
        var plan = new ManoeuvrePlan();
        plan.Update(vessel.Body, vessel.CentreOfMass, vessel.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity), flight.WorldTime, vessel.Nodes, null);
        paths.Build(flight, plan);
        return paths;
    }

    /// <summary>
    /// How far <paramref name="place"/> is ahead of <paramref name="from"/> across the ground (round
    /// the sphere through the pad), along <paramref name="way"/> (a direction at
    /// <paramref name="from"/>; only its part across the ground counts): negative if it is behind.
    /// Round <paramref name="body"/> (the planet, by default), in its frame.
    /// </summary>
    public static float Beyond(Vector3 from, Vector3 way, Vector3 place, GroundBody? body = null)
    {
        body ??= GroundBody.Planet;
        var r = body.Up(from);
        var forward = Vector3.Normalize(Pilots.Across(way, r));
        var t = body.Up(place);
        return body.Radius * MathF.Atan2(Vector3.Dot(t, forward), Vector3.Dot(t, r));
    }

    /// <summary>How far <paramref name="place"/> is off to the left of <paramref name="way"/> from <paramref name="from"/>, across the ground (negative: to the right).</summary>
    public static float Aside(Vector3 from, Vector3 way, Vector3 place, GroundBody? body = null)
    {
        body ??= GroundBody.Planet;
        var r = body.Up(from);
        var left = Vector3.Normalize(Vector3.Cross(r, Pilots.Across(way, r)));
        return body.Radius * MathF.Asin(Math.Clamp(Vector3.Dot(body.Up(place), left), -1f, 1f));
    }
}

/// <summary>
/// Lands the flown vessel on its target on the retrograde hold and the throttle alone, as a
/// player would by the HUD: in the frame in use (the surface's, coming down), falling with the
/// engine off until braking on the hold at <see cref="BrakeAt"/> of the throttle would no longer
/// stop it short of the target (or, left any later, above it), then braking with whatever throttle the pilot reckons stops it over the
/// target (by "To target" along the way it is going, its height over the target's marker, its
/// speed by the travel markers and the air by the HUD's reading), never one that wouldn't stop
/// above it; then straight down, still on the hold (a little faster than the holds' floor, so it
/// keeps the nose against the way it moves), the legs out, and the last few metres slowly. With
/// the engine on it also steers aside, as a player would by the direction rings: holding a little
/// off retrograde to bring the target (by "To target" and its marker) onto its track (and, coming
/// straight down, under it), then back to the retrograde hold.
/// </summary>
/// <remarks>
/// Thrust on the retrograde hold is against the velocity, so it can only shorten the way down,
/// never lengthen it: whatever brings the vessel here has to leave it coming down beyond the
/// target. The pilot's reckoning of a braking is its own (a player's judgement of it), from the
/// rocket's acceleration and what the HUD shows; feedback each frame makes up for what it leaves out.
/// It reads the air as it reads the rocket's acceleration: the player knows the rocket, and the
/// air (the HUD shows Air, here). At whichever body the vessel is at, by that body's ground, pull
/// and air (none, on the moon).
/// </remarks>
internal sealed class HoldLanding
{
    public enum Phase { Fall, Brake, Down }

    /// <summary>Start braking once braking at this much of the throttle would no longer stop it short of the target.</summary>
    public float BrakeAt { get; init; } = 0.7f;

    /// <summary>How high over the target the braking means to stop.</summary>
    public float StopAbove { get; init; } = 30f;

    /// <summary>Put the legs out below this, over the ground.</summary>
    public float LegsBelow { get; init; } = 1500f;

    /// <summary>How fast it comes straight down at the end, at least (just over the holds' floor) ...</summary>
    public float DownSpeed { get; init; } = 6.5f;

    /// <summary>... until this high over the ground; then the last metres at <see cref="LastSpeed"/>.</summary>
    public float FlareAt { get; init; } = 3f;

    /// <summary>
    /// Whether it cuts the engine as the braking ends, to come back up to what it needs to come
    /// straight down: where the stage pushes so much harder than the pull (the moon's, a sixth of
    /// the planet's) that the throttle, easing off only so fast, would brake on past the stop.
    /// </summary>
    public bool CutAtStop { get; init; }

    /// <summary>How fast it sinks over the last metres, in m/s.</summary>
    public float LastSpeed { get; init; } = 1.5f;

    /// <summary>
    /// If set, coming straight down it sinks no faster than braking at this much of its push
    /// (against the pull) would stop by the ground: for an engine only a little stronger than the
    /// pull (the lander's, 1.8 times its weight at the moon), which the way down's usual pace would
    /// leave falling too fast to stop. A strong one is never held back by it.
    /// </summary>
    public float? DownBrake { get; init; }

    /// <summary>
    /// How long it means to take bringing the target onto its track, in seconds, at up to
    /// <see cref="SideSpeed"/> aside. Tuned by trial: from 2 s to 12 s every coming-home flight
    /// and the hop land on target, the quicker the nearer (12 s: up to 7.9 m off the zone's
    /// middle, the 300 m south start's; 6 s: 1.4 to 2.4 m off it and 0.4 m off the pad's, the
    /// line's mark under 100 m up 2.2 to 2.7 m from where it came down; 2 s: 0.2 m). At 3 s: 0.4
    /// to 0.7 m off the zone's middle and 0.2 m off the pad's, touching down at 3.5 to 3.7 m/s
    /// (the hop 3.9) with 12 to 13% of the fuel left (the hop 71%), and the line's mark under
    /// 100 m up 1.9 to 2.1 m from where it came down (the hop's 1.8).
    /// </summary>
    public float SideSeconds { get; init; } = 3f;

    /// <summary>
    /// Tuned by trial (with <see cref="SideSeconds"/> at 6 s): at 2 m/s the 300 m south start
    /// comes down 4.9 m off the zone's middle; at 10 m/s, as at 5, 2.2 m. The hop lands 0.4 m off
    /// the pad's at any of them.
    /// </summary>
    public float SideSpeed { get; init; } = 5f;

    /// <summary>
    /// The most it holds off retrograde to steer aside (radians, about). Tuned by trial (with
    /// <see cref="SideSeconds"/> at 6 s): at 0.05 every coming-home flight lands 3 to 6.6 m off
    /// the zone's middle; at 0.5, as at 0.2, within 2.3 m.
    /// </summary>
    public float MaxSideLean { get; init; } = 0.2f;

    /// <summary>
    /// Where to land, in the frame of the body the vessel is at, if not on its target: a place a
    /// player has picked out on the ground there (the moon's landing spot, which is no site to target).
    /// </summary>
    public Vector3? Place { get; init; }

    public Phase Now { get; private set; }

    // The way it was going over the ground when it started braking, which "aside" is measured from
    // after (the way it is going turns upright as it slows).
    private Vector3 _track;

    /// <summary>When it started braking: how far "To target" was along the way it was going, and how fast it was going.</summary>
    public (double Time, float ToGo, float Speed)? BrakedAt { get; private set; }

    public FlightInput Fly(Flight flight, float dt)
    {
        var body = flight.Body;
        var target = Place ?? flight.TargetAt!.Value.Position;
        var velocity = flight.FrameVelocity; // what the travel markers show, and the hold steers by
        var up = flight.LocalUp;
        var legs = flight.Ground < LegsBelow && !flight.LegsWanted;
        var across = Pilots.Across(velocity, up);
        var vy = Vector3.Dot(velocity, up);
        // "To target", along the way it is going, as the ground's distance: what that is at its own
        // height, which its braking is reckoned at (further, by its height over the body's radius).
        var togo = FrameReadings.Beyond(flight.CentreOfMass, across, target, body) * Vector3.Distance(flight.CentreOfMass, body.Centre) / body.Radius;
        var above = body.Altitude(flight.CentreOfMass) - body.Altitude(target) - StopAbove;
        var a = flight.MaxAcceleration;
        var g = body.SurfaceGravity;
        var drag = Scene.DragRate * body.AirDensity(body.Altitude(flight.Position)); // the HUD's Air
        float throttle;
        var stopped = false;
        switch (Now)
        {
            case Phase.Fall:
            {
                var (reach, low) = Stopping(across.Length(), vy, above, a * BrakeAt, g, drag);
                if (reach < togo && low > 0f && flight.Ground > StopAbove)
                    return new FlightInput(ToggleLegs: legs, ToggleRetrogradeHold: !flight.RetrogradeHold, CutThrottle: flight.Throttle > 0f);
                BrakedAt = (flight.WorldTime, togo, velocity.Length());
                _track = across.Length() > 1f ? Vector3.Normalize(across) : Vector3.Normalize(Pilots.Across(target - flight.CentreOfMass, up));
                Now = Phase.Brake;
                goto case Phase.Brake;
            }
            case Phase.Brake:
            {
                // The throttle whose stop is nearest over the target (either way: harder stops
                // sooner, but gives gravity less time to bend the way down), of those that would
                // stop above it; full, if none would.
                var (best, off) = (1f, float.MaxValue);
                for (var u = 0.1f; u <= 1.001f; u += 0.05f)
                {
                    var (reach, low) = Stopping(across.Length(), vy, above, a * u, g, drag);
                    if (low >= 0f && MathF.Abs(reach - togo) < off)
                        (best, off) = (u, MathF.Abs(reach - togo));
                }
                throttle = best;
                if (velocity.Length() < DownSpeed + 0.5f || across.Length() < 0.3f || above < 0f)
                {
                    Now = Phase.Down;
                    stopped = CutAtStop;
                }
                break;
            }
            default:
            {
                var pace = Math.Clamp(flight.Ground * 0.2f, DownSpeed, 40f);
                if (DownBrake is { } share)
                    pace = MathF.Max(MathF.Min(pace, MathF.Sqrt(2f * MathF.Max(a * share - g, 0f) * MathF.Max(flight.Ground - FlareAt, 0f))), DownSpeed);
                var wanted = flight.Ground > FlareAt ? -pace : -LastSpeed;
                throttle = Math.Clamp((g + (wanted - vy) * 2f) / (a * MathF.Max(Vector3.Dot(flight.Aim, up), 0.5f)), 0f, 1f);
                break;
            }
        }
        return Aside(flight, body, velocity, up, target, a * flight.Throttle,
            new FlightInput(Throttle: Pilots.ThrottleTowards(flight, throttle, dt), ToggleLegs: legs, CutThrottle: stopped || flight.Ground < 0.2f));
    }

    /// <summary>
    /// <paramref name="input"/>, steering aside while the engine pushes (at <paramref name="thrust"/>):
    /// across its track, it wants to move towards the target at a speed that brings it there in
    /// <see cref="SideSeconds"/>, and holds the nose off retrograde by as much as that takes; once it
    /// needs next to nothing, it is back on the retrograde hold.
    /// </summary>
    private FlightInput Aside(Flight flight, GroundBody body, Vector3 velocity, Vector3 up, Vector3 target, float thrust, FlightInput input)
    {
        var lean = Vector3.Zero;
        if (thrust > 0.5f && _track != default)
        {
            Vector3 push;
            if (Now == Phase.Down)
            {
                // Coming straight down there is no way along for the braking to look after: it
                // steers onto the target every way (the turning ground's pull carries a fall east).
                var wanted = Pilots.Across(target - flight.CentreOfMass, up) / SideSeconds;
                if (wanted.Length() > SideSpeed)
                    wanted = Vector3.Normalize(wanted) * SideSpeed;
                push = wanted - Pilots.Across(velocity, up);
            }
            else
            {
                var left = Vector3.Normalize(Vector3.Cross(up, _track));
                var off = FrameReadings.Aside(flight.CentreOfMass, _track, target, body); // the target, to the left of the track
                push = left * (Math.Clamp(off / SideSeconds, -SideSpeed, SideSpeed) - Vector3.Dot(velocity, left));
            }
            lean = push / thrust;
            if (lean.Length() > MaxSideLean)
                lean = Vector3.Normalize(lean) * MaxSideLean;
        }
        if (lean.Length() < 0.003f)
            return input with { ToggleRetrogradeHold = !flight.RetrogradeHold };
        var back = velocity.Length() > 1f ? -Vector3.Normalize(velocity) : up;
        return input with { Hold = Vector3.Normalize(back + lean) };
    }

    /// <summary>
    /// The pilot's own reckoning of a braking on the hold: from moving <paramref name="across"/>
    /// over the ground and <paramref name="vy"/> up, <paramref name="height"/> above where it means
    /// to stop, braking at <paramref name="accel"/> against the way it moves, pulled down at
    /// <paramref name="gravity"/>, through air that
    /// slows it at <paramref name="drag"/> per second (as thick as it is here): how far it goes
    /// across the ground before it is down to <see cref="DownSpeed"/>, and how high it is then
    /// (negative: it wouldn't stop in time).
    /// </summary>
    private (float Reach, float Low) Stopping(float across, float vy, float height, float accel, float gravity, float drag)
    {
        const float step = 0.1f;
        var (x, y) = (0f, height);
        for (var t = 0f; t < 120f; t += step)
        {
            var speed = MathF.Sqrt(across * across + vy * vy);
            if (speed < DownSpeed || y < 0f)
                break;
            across -= accel * across / speed * step;
            vy -= (accel * vy / speed + gravity) * step;
            var slow = MathF.Exp(-drag * step);
            (across, vy) = (across * slow, vy * slow);
            (x, y) = (x + across * step, y + vy * step);
        }
        return (x, y);
    }
}

/// <summary>
/// Brings a stage home from orbit by the frames, as a player would with the holds, the throttle
/// and what the HUD shows: it picks the landing site as its target (to navigate by), puts the
/// retrograde hold on (on Auto, the orbital frame's, up there), waits for the site to come round to
/// within <see cref="BurnWhenAhead"/>, and burns until the line's landing mark, sweeping back
/// towards it from the far side of the planet, has come back to within <see cref="Lead"/> beyond the
/// site: the line ignores the air, which takes some of that off, and the braking the rest. While
/// it burns it holds a little off retrograde, aside, to bring the mark onto the site's line. Then,
/// Auto having turned to the surface frame, it lands by <see cref="HoldLanding"/>, steering aside
/// again low down for what the air has carried it off.
/// </summary>
internal sealed class ComingHomePilot
{
    private enum Phase { Target, Hold, Wait, Burn, Land }

    /// <summary>Where to land: a place in <see cref="Sites.All"/> (by default, the landing zone).</summary>
    public int Site { get; init; } = 1;

    /// <summary>
    /// Start the burn once the site is this close ahead, round the planet (radians). Tuned by trial:
    /// close, so it comes down steeply. From further (1 rad and more) the way down is shallow: from 4
    /// km it either crosses the Westwall range still going ~200 m/s across the ground, too fast to
    /// brake onto the zone on the hold, or (from 1.6 rad on) the mark first appears in the range,
    /// short of the zone, and never reaches it.
    /// </summary>
    public float BurnWhenAhead { get; init; } = 0.3f;

    /// <summary>
    /// Stop the burn once the landing mark is no further than this beyond the site, in metres (and
    /// no nearer than 300 m less). Tuned by trial: the line ignores the air, which takes some
    /// hundreds of metres off on the way down, and the braking on the hold can only take more off.
    /// At 150 m every start came down about 38 m from the zone's middle, off it; from 300 m to
    /// 1,000 m all land on it, a longer lead costing fuel to brake off (15% left at 300 m, 13% at
    /// 500, 9% at 1,000): 500 has room both ways.
    /// </summary>
    public float Lead { get; init; } = 500f;

    /// <summary>
    /// How far it holds off retrograde in the burn for each metre the mark is off the site's line,
    /// to the side (radians), up to <see cref="MaxBurnLean"/>. Tuned by trial: at 0.001 the mark
    /// is still about 25 m aside when the burn is cut (the test allows 30); at 0.01 to 0.03 it
    /// swings past, ending 8 to 27 m aside; at 0.003 it is within 3 m (9 and 22 m from the
    /// starts 300 m north and south).
    /// </summary>
    public float BurnLean { get; init; } = 0.003f;

    /// <summary>
    /// Tuned by trial: at 0.03 the start 300 m south still has its mark 39 m aside at the cut, and
    /// at 0.01 79 m (the test allows 30); at 0.3 the mark swings past, ending 10 to 18 m aside.
    /// </summary>
    public float MaxBurnLean { get; init; } = 0.1f;

    public HoldLanding Landing { get; } = new();

    private Phase _phase;
    private readonly Paths _paths = new();

    /// <summary>Where it has got to, for reports.</summary>
    public string PhaseName => _phase == Phase.Land ? Landing.Now.ToString() : _phase.ToString();

    /// <summary>
    /// The burn as it was cut (set each frame of the burn the mark is seen in, so the last is the
    /// cut's): when, the frame in use, how far beyond the site the landing mark was, and how far
    /// the site was to the left of the mark (negative: to its right).
    /// </summary>
    public (double Time, Frame Frame, float MarkBeyond, float MarkAside)? AtCut { get; private set; }

    public FlightInput Fly(Flight flight, float dt)
    {
        if (flight.State != FlightState.Flying)
            return default;
        switch (_phase)
        {
            case Phase.Target:
                _phase = Phase.Hold;
                return new FlightInput(SetTarget: new NavTarget(TargetKind.Site, Site));
            case Phase.Hold:
                _phase = Phase.Wait;
                return new FlightInput(ToggleRetrogradeHold: !flight.RetrogradeHold);
            case Phase.Land:
                return Landing.Fly(flight, dt);
        }

        var site = flight.TargetAt!.Value.Position;
        var velocity = flight.FrameVelocity; // what the travel markers show, and the hold steers by
        if (_phase == Phase.Wait)
        {
            var lined = Vector3.Dot(flight.Aim, -Vector3.Normalize(velocity)) > 0.995f;
            if (lined && Pilots.AngleAhead(flight.CentreOfMass, velocity, site) < BurnWhenAhead)
                _phase = Phase.Burn;
            return default;
        }

        // The burn: until the mark (on the far side at first) has swept back to just beyond the site,
        // and aside, to put it on the site's line.
        var back = -Vector3.Normalize(velocity);
        var aim = back;
        if (FrameReadings.Line(flight, _paths).LandingMark is { } mark)
        {
            var beyond = (Pilots.AngleAhead(flight.CentreOfMass, velocity, mark) - Pilots.AngleAhead(flight.CentreOfMass, velocity, site)) * Planet.Radius;
            var aside = FrameReadings.Aside(flight.CentreOfMass, velocity, site) - FrameReadings.Aside(flight.CentreOfMass, velocity, mark);
            AtCut = (flight.WorldTime, flight.Frame, beyond, aside);
            if (beyond <= Lead && beyond > Lead - 300f)
            {
                _phase = Phase.Land;
                return new FlightInput(CutThrottle: true);
            }
            var left = Vector3.Normalize(Vector3.Cross(flight.LocalUp, Pilots.Across(velocity, flight.LocalUp)));
            aim = Vector3.Normalize(back + left * Math.Clamp(aside * BurnLean, -MaxBurnLean, MaxBurnLean));
        }
        return new FlightInput(Hold: aim, FullThrottle: flight.Throttle < 1f);
    }
}

/// <summary>
/// A hop from the pad to a place nearby, flown in the surface frame by the line: it picks the place
/// as its target and the surface frame, launches leaning towards the place, and burns until the
/// line's landing mark is <see cref="Lead"/> beyond it (the line ignores the air, which takes some
/// of that off on the way, and the braking the rest); while it climbs it burns again, leaning
/// towards the place, should the mark fall short of it. Then it lands by <see cref="HoldLanding"/>.
/// </summary>
internal sealed class HopPilot
{
    private enum Phase { Target, Launch, Boost, Coast, Land }

    /// <summary>Where to land: a place in <see cref="Sites.All"/>.</summary>
    public required int Site { get; init; }

    /// <summary>How far it leans towards the place on the way up (the tangent of the angle off upright).</summary>
    public float Lean { get; init; } = 0.5f;

    /// <summary>How far it leans aside for each metre the landing mark is off to the side of the place.</summary>
    public float SideLean { get; init; } = 0.01f;

    /// <summary>
    /// How far beyond the place it puts the landing mark, in metres. Tuned by trial: the hop goes
    /// through thick air at ~70 m/s, which takes a lot off (with 150 m it came down 38 m short), and
    /// on the retrograde hold the braking can only take more off, never put any back.
    /// </summary>
    public float Lead { get; init; } = 300f;

    /// <summary>It doesn't steer by the line until this high over the ground: off the pad, it is going nowhere yet.</summary>
    public float SteerAbove { get; init; } = 50f;

    public HoldLanding Landing { get; } = new() { StopAbove = 20f, LegsBelow = 200f };

    private Phase _phase;
    private readonly Paths _paths = new();

    public string PhaseName => _phase == Phase.Land ? Landing.Now.ToString() : _phase.ToString();

    /// <summary>How far beyond the place the landing mark was when the engine was last cut on the way up.</summary>
    public float? MarkAtCut { get; private set; }

    public FlightInput Fly(Flight flight, float dt)
    {
        switch (_phase)
        {
            case Phase.Target:
                _phase = Phase.Launch;
                return new FlightInput(SetTarget: new NavTarget(TargetKind.Site, Site), ChooseFrame: FrameChoice.Surface);
            case Phase.Launch:
                if (flight.State == FlightState.Ready)
                    return new FlightInput(Launch: true, FullThrottle: true);
                _phase = Phase.Boost;
                break;
            case Phase.Land:
                return flight.State == FlightState.Flying ? Landing.Fly(flight, dt) : default;
        }
        if (flight.State != FlightState.Flying)
            return default;

        var place = flight.TargetAt!.Value.Position;
        var up = flight.LocalUp;
        var towards = Vector3.Normalize(Pilots.Across(place - flight.CentreOfMass, up)); // its marker, from here
        if (Vector3.Dot(flight.FrameVelocity, up) < 0f)
        {
            _phase = Phase.Land; // over the top: coming down
            return new FlightInput(Hold: -Vector3.Normalize(flight.FrameVelocity), CutThrottle: true);
        }
        // Leaning towards the place, and off to the side by as much as the mark is off to the other.
        var (beyond, aside) = (float.NegativeInfinity, 0f);
        if (flight.Ground > SteerAbove && FrameReadings.Line(flight, _paths).LandingMark is { } mark)
        {
            beyond = FrameReadings.Beyond(flight.CentreOfMass, towards, mark) - FrameReadings.Beyond(flight.CentreOfMass, towards, place);
            aside = FrameReadings.Aside(flight.CentreOfMass, towards, mark) - FrameReadings.Aside(flight.CentreOfMass, towards, place);
        }
        var left = Vector3.Normalize(Vector3.Cross(up, towards));
        var aim = Vector3.Normalize(up + towards * Lean - left * Math.Clamp(aside * SideLean, -Lean, Lean));
        if (_phase == Phase.Boost && beyond >= Lead)
        {
            (_phase, MarkAtCut) = (Phase.Coast, beyond);
            return new FlightInput(Hold: aim, CutThrottle: true);
        }
        return _phase == Phase.Boost
            ? new FlightInput(Hold: aim, FullThrottle: flight.Throttle < 1f)
            : new FlightInput(Hold: aim);
    }
}

/// <summary>
/// Reaches another vessel in orbit by the target frame alone, as a player would: it picks the other
/// as its target and the target frame, then by the relative velocity (the travel markers), the
/// way towards the target and "To target", it heads for it at up to <see cref="ClosingSpeed"/>,
/// slowing as it gets there to stop <see cref="StopAt"/> off: it points the nose (by the direction
/// rings) along the change of relative velocity it wants, and burns, in proportion, only once lined up.
/// </summary>
internal sealed class RendezvousPilot
{
    /// <summary>The vessel to reach.</summary>
    public required Vessel Target { get; init; }

    /// <summary>The fastest it closes on the target, in m/s.</summary>
    public float ClosingSpeed { get; init; } = 10f;

    /// <summary>
    /// How hard it slows as it closes: the closing speed it wants is this much per second for each
    /// metre to go. Tuned by trial, from 2 km (the test's start then): at 0.03 or 0.04 it closes
    /// sooner (260 to 280 s) but comes in harder, at 0.5 m/s relative in the minute after (the
    /// test's limit); at 0.01 it takes 530 s of the 600 allowed; at 0.02, 325 s and at most
    /// 0.37 m/s (from 1 km, 228 s and 0.36 m/s).
    /// </summary>
    public float Approach { get; init; } = 0.02f;

    /// <summary>
    /// How far off the target it means to stop, in metres. Tuned by trial against the test's 10 to
    /// 50 m: at 35 m it holds 41 to 50 m off; at 20 or 30 m it comes in at up to 0.5 m/s, the
    /// test's limit; at 40 m and more it sits at the 50 m edge.
    /// </summary>
    public float StopAt { get; init; } = 35f;

    /// <summary>It leaves the relative velocity be once it is this close to what it wants, in m/s.</summary>
    public float Slack { get; init; } = 0.1f;

    private bool _picked;

    public FlightInput Fly(Flight flight, float dt)
    {
        if (!_picked)
        {
            _picked = true;
            return new FlightInput(SetTarget: NavTarget.OfVessel(Target), ChooseFrame: FrameChoice.Target);
        }
        if (flight.State != FlightState.Flying || flight.TowardsTarget is not { } towards || flight.DistanceToTarget is not { } distance)
            return default;

        var relative = flight.FrameVelocity; // the travel markers, in the target frame
        var closing = Math.Clamp(Approach * (distance - StopAt), -ClosingSpeed, ClosingSpeed);
        var change = Vector3.Normalize(towards) * closing - relative;
        if (change.Length() < Slack)
            return new FlightInput(CutThrottle: flight.Throttle > 0f);
        var way = Vector3.Normalize(change);
        var lined = Vector3.Dot(flight.Aim, way) > 0.99f;
        var throttle = lined ? Math.Clamp(change.Length() / flight.MaxAcceleration, 0.05f, 1f) : 0f;
        return new FlightInput(Hold: way, Throttle: Pilots.ThrottleTowards(flight, throttle, dt));
    }
}
