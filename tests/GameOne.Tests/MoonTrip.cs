using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// Where the moon trip's tests start (each where it is about, never flown there from the pad): the
/// satellite launcher's upper stage alone, legs in, with the fuel a real ascent leaves it
/// (<see cref="AscentFuel"/>), or the lander launcher's stack (its upper stage with the lander on,
/// the fairing gone, with <see cref="LanderAscentFuel"/> and the lander full): in a low orbit round
/// the planet with the moon a transfer's lead ahead (<see cref="LowOrbit"/>); arriving in the moon's
/// sphere (<see cref="Arriving"/>); and on a round orbit over the moon's poles
/// (<see cref="MoonOrbit"/>), where the lander alone starts too (<see cref="Lander"/>).
/// </summary>
/// <remarks>
/// The low orbit is in a plane tilted a little from the moon's own, so that the transfer arrives
/// beside the moon's orbit rather than in it, passing over the moon's pole: the capture is then
/// into an orbit over both its poles, which passes over the landing spot, 20° from its north pole
/// (<see cref="Scene.MoonLandingSpot"/>), as a player bound for it would launch into that plane. A
/// transfer in the moon's own plane would arrive round the moon's equator, 70° of latitude from the
/// spot, and would need its plane turned over the poles there before a landing.
/// </remarks>
internal static class MoonTrip
{
    private static GroundBody Moon => GroundBody.Moon;
    private static GroundBody Planet => GroundBody.Planet;

    /// <summary>
    /// The fuel (kg) in the satellite launcher's upper stage once the satellite mission's ascent
    /// is done and the satellite let go (<see cref="SatellitePilot"/>, flown by
    /// <c>MoonTripTests.Playthrough_the_satellite_missions_ascent_leaves_the_fuel_the_trip_starts_with</c>):
    /// measured once, and written here.
    /// </summary>
    public const float AscentFuel = 4527f;

    /// <summary>The world time the trip's starts are at (the low orbit's, and the moon's starts').</summary>
    public const double Start = 5000.0;

    /// <summary>How high the low orbit is over the sea: 300 m over the air's top.</summary>
    public const float LowHeight = Scene.AtmosphereHeight + 300f;

    /// <summary>How high over its highest ground the orbit round the moon is: the capture's, and the landing's start.</summary>
    public const float MoonHeight = 400f;

    /// <summary>How long after the start the transfer's burn is due (seconds): the window a little way ahead.</summary>
    public const double Wait = 10.0;

    /// <summary>
    /// The satellite launcher's upper stage alone, legs in, engine off, with <see cref="AscentFuel"/>
    /// in its tank, at <paramref name="body"/>; with <paramref name="lander"/>, the lander launcher's
    /// stack instead: its upper stage with <see cref="LanderAscentFuel"/> and the lander on, full,
    /// the fairing gone.
    /// </summary>
    private static VesselState Stage(int id, GroundBody body, Vector3 centreOfMass, Vector3 velocity, Vector3 nose, bool lander = false) =>
        lander
            ? CrossingTests.Craft(id, body, centreOfMass, velocity, nose, first: 1, last: 2, vehicle: Vehicle.LanderLauncher) with { Fuel = [0f, LanderAscentFuel, LanderFuel] }
            : CrossingTests.Craft(id, body, centreOfMass, velocity, nose) with { Fuel = [0f, AscentFuel, 0f] };

    /// <summary>The lander's fuel, full (kg).</summary>
    public static float LanderFuel => Vehicle.LanderLauncher.Stages[2].FuelMass;

    /// <summary>A world at <paramref name="time"/> holding <paramref name="vessels"/>, the first to be flown.</summary>
    public static WorldState World(double time, params VesselState[] vessels) =>
        new() { Time = time, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    /// <summary>
    /// A planet-frame vector at world time <paramref name="then"/> (its place in the planet's frame
    /// then, or its velocity among the stars) as it is among the stars that match the planet's
    /// ground at <paramref name="now"/>.
    /// </summary>
    private static Vector3Double StarsAt(Vector3Double v, double then, double now) =>
        QuaternionDouble.FromAxisAngle(Planet.NorthPole, Planet.TurnRadians(then - now)).Rotate(v);

    /// <summary>
    /// The transfer as the starts reckon it, burnt at world time <paramref name="burn"/>: the
    /// circle <see cref="LowHeight"/> up round the planet whose Hohmann transfer (one burn along the
    /// way it goes) has its far side <paramref name="aboveN"/> metres off the moon's orbit's plane
    /// (towards its north) and <paramref name="outR"/> metres further out than the moon's centre as
    /// it gets there: the circle's plane, the place on it the burn is at, and the speed it leaves at,
    /// among the stars matching the planet's ground at <paramref name="burn"/>.
    /// </summary>
    private static (Vector3Double Normal, Vector3Double At, double Speed) Aimed(double burn, double aboveN, double outR)
    {
        var moon = Bodies.OrbitOf(Moon, burn);
        var north = Vector3Double.Normalize((Vector3Double)moon.Normal);
        double mu = Planet.Mu, low = Planet.SeaRadius + LowHeight;
        var far = (Vector3Double)moon.PositionAfter(0) - (Vector3Double)Planet.Centre;
        for (var i = 0; i < 4; i++)
        {
            var a = (low + far.Length()) / 2;
            var seconds = Math.PI * Math.Sqrt(a * a * a / mu);
            var centre = (Vector3Double)moon.PositionAfter(seconds) - (Vector3Double)Planet.Centre;
            far = centre + north * aboveN + Vector3Double.Normalize(centre) * outR;
        }
        var aim = Vector3Double.Normalize(far);
        var normal = Vector3Double.Normalize(north - aim * Vector3Double.Dot(north, aim));
        var semi = (low + far.Length()) / 2;
        return (normal, -aim, Math.Sqrt(mu * (2 / low - 1 / semi)));
    }

    /// <summary>
    /// The moon leg of the path after <see cref="Aimed"/>'s burn, given at once: how near the
    /// orbit round the moon comes to being over its poles (the sine of its tilt from them: 0 over
    /// both poles), and its periapsis over the moon's datum; null if the path never reaches the moon.
    /// </summary>
    private static (double Polar, double Periapsis)? Arrival(double burn, double aboveN, double outR, List<PathLeg> legs)
    {
        var (normal, at, speed) = Aimed(burn, aboveN, outR);
        var low = Planet.SeaRadius + LowHeight;
        var position = (Vector3Double)Planet.Centre + at * low;
        var velocity = Vector3Double.Cross(normal, at) * speed;
        Patched.Legs(Planet, position.ToSingle(), velocity.ToSingle(), burn, legs);
        foreach (var leg in legs)
            if (leg.Body == Moon)
                return (Vector3.Dot(Vector3.Normalize(leg.Orbit.Normal), Moon.NorthPole), leg.Orbit.PeriapsisDistance - Moon.SeaRadius);
        return null;
    }

    /// <summary>
    /// The far side's offsets (see <see cref="Aimed"/>) whose burn at <paramref name="burn"/> takes
    /// the path round the moon over its poles with its periapsis <paramref name="periapsis"/> over
    /// its datum, found by Newton's method on the patched path from a pass 5 km to its south and
    /// 700 m short of it: the side whose periapsis a burn along the way it goes brings down through
    /// 1 km first, as the transfer's burn is cut (<see cref="TransferPilot.CutBelow"/>), so the trim
    /// after it is small.
    /// </summary>
    private static (double AboveN, double OutR) Solve(double burn, double periapsis, (double AboveN, double OutR)? from = null)
    {
        var legs = new List<PathLeg>();
        var (n, r) = from ?? (-5000.0, -700.0);
        const double h = 10;
        for (var i = 0; i < 30; i++)
        {
            var f = Arrival(burn, n, r, legs) ?? throw new InvalidOperationException($"no moon leg at ({n:0}, {r:0})");
            var (e1, e2) = (f.Polar, f.Periapsis - periapsis);
            if (Math.Abs(e1) < 1e-4 && Math.Abs(e2) < 0.5)
                return (n, r);
            var fn = Arrival(burn, n + h, r, legs)!.Value;
            var fr = Arrival(burn, n, r + h, legs)!.Value;
            double a = (fn.Polar - f.Polar) / h, b = (fr.Polar - f.Polar) / h;
            double c = (fn.Periapsis - f.Periapsis) / h, d = (fr.Periapsis - f.Periapsis) / h;
            var det = a * d - b * c;
            var dn = -(d * e1 - b * e2) / det;
            var dr = -(-c * e1 + a * e2) / det;
            var most = Math.Max(Math.Abs(dn), Math.Abs(dr));
            if (most > 1000)
                (dn, dr) = (dn * 1000 / most, dr * 1000 / most);
            (n, r) = (n + dn, r + dr);
        }
        throw new InvalidOperationException($"the transfer over the moon's poles was not found (last at {n:0}, {r:0})");
    }

    /// <summary>
    /// The plane of the low orbit (<see cref="LowOrbit"/>'s) for a transfer burnt at world time
    /// <paramref name="burn"/>: its normal (the right hand's way round the orbit), among the stars
    /// matching the planet's ground at <paramref name="burn"/>.
    /// </summary>
    public static Vector3 TripNormal(double burn) => TripNormal(burn, null).Normal;

    /// <summary>
    /// <see cref="TripNormal(double)"/>, its far side's offsets found from <paramref name="from"/>
    /// (a near burn's, to be found sooner); with the burn's place on the low orbit (a direction
    /// from the planet's centre, among the same stars) and those offsets.
    /// </summary>
    private static (Vector3 Normal, Vector3 At, (double, double) Offsets) TripNormal(double burn, (double, double)? from)
    {
        var offsets = Solve(burn, periapsis: 1000, from); // where the transfer's burn is cut (TransferPilot.CutBelow)
        var (normal, at, _) = Aimed(burn, offsets.AboveN, offsets.OutR);
        return (normal.ToSingle(), at.ToSingle(), offsets);
    }

    /// <summary>
    /// How long the lander launcher takes from lifting off Home's pad to a low orbit in the trip's
    /// plane, its periapsis over the air with its engine off (<see cref="PlaneAscentPilot"/>, flown
    /// by <c>LanderTripTests.Playthrough_the_lander_launcher_climbs_into_the_trips_tilted_low_orbit</c>):
    /// measured once, and written here.
    /// </summary>
    public const double AscentSeconds = 229;

    /// <summary>
    /// How far round the orbit (degrees, among the stars) that ascent ends from the pad's place as
    /// it lifted off, measured by the same playthrough and written here: where on the low orbit it
    /// starts to wait for the transfer.
    /// </summary>
    /// <remarks>
    /// This, <see cref="AscentSeconds"/> and <see cref="AscentLapSeconds"/> are measured from the
    /// launch at Home's descending crossing (heading south of east, the one the trip flies on), and
    /// serve the ascending crossing's too: flown from there
    /// (<c>..._at_its_ascending_crossing</c>), the ascent ends 222.1 s on, 218.3° round, on a lap
    /// of 321.4 s, which by the same time after lift-off puts the stack 0.9° (under a second) from
    /// where these do; its transfer window came 1.1 s from the burn <see cref="Launch"/> reckoned,
    /// the trip's plane then 0.03° from the stack's (the descending's: 6 s, 0.14°; the bound 1°).
    /// </remarks>
    public const double AscentDegrees = 225.1;

    /// <summary>
    /// How long a lap of the orbit that ascent leaves the stack on takes (seconds), measured by the
    /// same playthrough and written here: what its wait for the transfer's place is reckoned by.
    /// </summary>
    public const double AscentLapSeconds = 325.5;

    /// <summary>
    /// The fuel (kg) in the lander launcher's upper stage once that ascent is done (the lander on,
    /// the fairing gone), measured by the same playthrough and written here.
    /// </summary>
    public const float LanderAscentFuel = 4279f;

    /// <summary>How far ahead of its <c>after</c> <see cref="Launch"/> looks (seconds): several times the longest it can be between two.</summary>
    private const double LaunchSearchSeconds = 20_000;

    /// <summary>
    /// How far apart (seconds) <see cref="Launch"/> tries times: on a lap's wait carried on across
    /// it, the pad's height over the plane changes sign no more than once in it, and for a pad near
    /// the plane (Home's) the wait moves by some 20 s, under <see cref="SteadyWait"/>; a step it moves
    /// by more in is halved until it doesn't.
    /// </summary>
    public const double LaunchStep = 60;

    /// <summary>
    /// How far (seconds) a lap's wait may move across a step of <see cref="Launch"/>'s for the
    /// crossing in it to be found on that lap alone: a quarter of a lap, well short of the half lap
    /// that tells a lap's wait from the next's.
    /// </summary>
    private const double SteadyWait = AscentLapSeconds / 4;

    /// <summary>
    /// A launch tried at some time, its wait for the transfer's burn found on a given lap
    /// (<see cref="Meet"/>): the pad's height over the plane of the trip burnt then (a fraction of
    /// its distance from the planet's centre, north of the plane positive), that plane's normal,
    /// the burn, the wait from the ascent's end to it, and the far side's offsets it was found with.
    /// </summary>
    public readonly record struct Reach(double Off, Vector3 Normal, double Burn, double Wait, (double, double) Offsets);

    /// <summary>
    /// The first world time after <paramref name="after"/> that a launch from <paramref name="site"/>'s
    /// pad (Home's by default) climbs into the plane the trip has when the stack comes round to the
    /// transfer's burn: the pad in the plane of the trip burnt at <c>Burn</c>, where <c>Burn</c> is
    /// <see cref="AscentSeconds"/> on, then as long as the stack takes from where the ascent ends
    /// (<see cref="AscentDegrees"/> round from the pad, by <see cref="AscentLapSeconds"/>) to meet
    /// the burn's place the first time after <see cref="Wait"/> (it moves on meanwhile, so that can
    /// be more than a lap); that plane's normal (<see cref="TripNormal(double)"/>, among the stars
    /// matching the ground at <c>Burn</c>), and <c>Burn</c>. Null if the pad never passes under it
    /// within <paramref name="within"/> seconds, tried every <paramref name="step"/> seconds.
    /// The plane follows the moon round (a lap in some 1,600 s) the same way as the ground turns,
    /// and each launch's is the plane at its own burn, which moves on by up to a lap of the low
    /// orbit as the wait for it does; so a pad on the equator passes under it at its descending and
    /// ascending crossings in turn (Home's first four after the clock's start, at 901, 1,386, 1,944 and 2,534 s, 485 to 590 s apart), not
    /// twice a turn of the planet.
    /// </summary>
    /// <remarks>
    /// The wait shortens as the pad goes round, and where it would fall under <see cref="Wait"/> it
    /// goes round to the next meeting, most of a lap more (for a pad far off the plane, whose place
    /// in it can swing the other way, it can lengthen and go round back): there the pad's height
    /// over the plane jumps. A step the wait goes round in is tried on both laps, each carried on
    /// across the whole step (found from half a lap under <see cref="Wait"/> and half a lap over
    /// it), and a crossing on either is the launch only where that lap's meeting is the first after
    /// <see cref="Wait"/>, the sooner taken; so a crossing in the same step as a jump is found, and
    /// the jump is never taken for one.
    /// </remarks>
    public static (double Time, Vector3 Normal, double Burn)? Launch(double after, LaunchSite? site = null, double within = LaunchSearchSeconds, double step = LaunchStep)
    {
        var pad = (Vector3Double)((site ?? LaunchSites.Home).Pad - Planet.Centre);
        var (t0, r0) = (after, Meet(pad, after, Wait, null));
        for (var t1 = after + step; t1 <= after + within; t1 += step)
        {
            var r1 = Meet(pad, t1, Wait, r0.Offsets);
            if (Search(pad, t0, r0, t1, r1) is { } found)
                return found;
            (t0, r0) = (t1, r1);
        }
        return null;
    }

    /// <summary>
    /// The first launch between <paramref name="t0"/> and <paramref name="t1"/> (the launch's own
    /// reaches there <paramref name="r0"/> and <paramref name="r1"/>), or null: on the launch's own
    /// lap if its wait is steady across the step; else on the laps half a lap under and over
    /// <see cref="Wait"/> (the wait goes round in the step: up, or down for a pad far off the
    /// plane whose place in it swings the other way), the sooner; else, neither steady (a pad's
    /// place in the plane swinging fast), in each half of the step in turn.
    /// </summary>
    private static (double Time, Vector3 Normal, double Burn)? Search(Vector3Double pad, double t0, Reach r0, double t1, Reach r1)
    {
        if (Math.Abs(r1.Wait - r0.Wait) < SteadyWait)
            return Crossing(pad, t0, r0, t1, r1, Wait);
        double before = Wait - AscentLapSeconds / 2, later = Wait + AscentLapSeconds / 2;
        var (n0, n1) = (Meet(pad, t0, before, r0.Offsets), Meet(pad, t1, before, r1.Offsets));
        var (f0, f1) = (Meet(pad, t0, later, r0.Offsets), Meet(pad, t1, later, r1.Offsets));
        if (Math.Abs(n1.Wait - n0.Wait) < SteadyWait && Math.Abs(f1.Wait - f0.Wait) < SteadyWait)
        {
            var (nearer, further) = (Crossing(pad, t0, n0, t1, n1, before), Crossing(pad, t0, f0, t1, f1, later));
            return (nearer, further) is ({ } a, { } b) ? (a.Time <= b.Time ? a : b) : nearer ?? further;
        }
        if (t1 - t0 < 0.5)
            throw new InvalidOperationException($"the wait for the transfer's burn after a launch moves by a lap or more between {t0:0.000} s and {t1:0.000} s ({r0.Wait:0.0} s to {r1.Wait:0.0} s): the pad's place in the plane swings too fast to follow");
        var tm = (t0 + t1) / 2;
        var rm = Meet(pad, tm, Wait, r0.Offsets);
        return Search(pad, t0, r0, tm, rm) ?? Search(pad, tm, rm, t1, r1);
    }

    /// <summary>
    /// The launch at which the pad's height over the plane on the lap found from
    /// <paramref name="from"/> (see <see cref="Meet"/>; its wait steady across the step, as
    /// <see cref="Search"/> asks) changes sign between <paramref name="t0"/>
    /// and <paramref name="t1"/> (<paramref name="r0"/> and <paramref name="r1"/> the reaches
    /// there), found to a millisecond by false position (the Illinois way); null if it doesn't, or
    /// if that lap's meeting then isn't the first after <see cref="Wait"/> (not the launch's own).
    /// </summary>
    private static (double Time, Vector3 Normal, double Burn)? Crossing(Vector3Double pad, double t0, Reach r0, double t1, Reach r1, double from)
    {
        if (Math.Sign(r0.Off) == Math.Sign(r1.Off))
            return null;
        var (a, fa, b, fb) = (t0, r0.Off, t1, r1.Off);
        var (time, reach, side) = (t0, r0, 0);
        for (var i = 0; i < 60 && b - a > 1e-3 && reach.Off != 0; i++)
        {
            time = Math.Clamp((a * fb - b * fa) / (fb - fa), a, b);
            reach = Meet(pad, time, from, reach.Offsets);
            if (Math.Sign(reach.Off) == Math.Sign(fa))
            {
                (a, fa) = (time, reach.Off);
                if (side == -1) fb /= 2; // the same end kept twice: the other's height halved, so it moves too
                side = -1;
            }
            else
            {
                (b, fb) = (time, reach.Off);
                if (side == 1) fa /= 2;
                side = 1;
            }
        }
        // The launch's own wait then is the first meeting after Wait: this lap's only if it is that one.
        var own = from == Wait ? reach : Meet(pad, time, Wait, reach.Offsets);
        return Math.Abs(own.Wait - reach.Wait) < 1 ? (time, own.Normal, own.Burn) : null;
    }

    /// <summary>
    /// A launch from <paramref name="pad"/> (from the planet's centre, in its frame) at world time
    /// <paramref name="t"/>, its stack waiting from the ascent's end until it first meets the
    /// transfer's burn's place after <paramref name="from"/> seconds: the stack goes round from
    /// <see cref="AscentDegrees"/> past the pad, a lap in <see cref="AscentLapSeconds"/>, and the
    /// burn's place (<see cref="TripNormal(double)"/>'s, for a burn then) moves on with the moon far
    /// slower, so the gap between them closes steadily and the meeting is found by the secant on
    /// it, starting from the burn's place the first time round past <paramref name="from"/>'s. Its
    /// result depends on its arguments alone; <paramref name="seed"/> (a near time's offsets) only
    /// starts the far side's offsets nearer, which settle within <see cref="Solve"/>'s tolerance
    /// whatever it is. It throws if the meeting doesn't settle.
    /// </summary>
    public static Reach Meet(Vector3Double pad, double t, double from, (double, double)? seed)
    {
        var motion = Math.Tau / AscentLapSeconds;
        var (w, phi) = (from, 0.0);
        (double W, double Gap)? last = null;
        for (var i = 0; i < 20; i++)
        {
            var burn = t + AscentSeconds + w;
            var (normal, at, offsets) = TripNormal(burn, seed);
            seed = offsets;
            var n = (Vector3Double)normal;
            var p = StarsAt(pad, t, burn);
            var flat = Vector3Double.Normalize(p - n * Vector3Double.Dot(p, n));
            var ends = QuaternionDouble.FromAxisAngle(n, double.DegreesToRadians(AscentDegrees)).Rotate(flat);
            var angle = Math.Atan2(Vector3Double.Dot(Vector3Double.Cross(ends, (Vector3Double)at), n), Vector3Double.Dot(ends, (Vector3Double)at));
            // How far round the burn's place is ahead of the ascent's end: the first time it is
            // past `from`'s, then carried on from the last (it moves far less than a turn between).
            phi = i == 0
                ? angle + Math.Tau * Math.Ceiling((from * motion - angle) / Math.Tau)
                : angle + Math.Tau * Math.Round((phi - angle) / Math.Tau);
            var gap = phi / motion - w; // how much longer the stack has to go round to it
            if (Math.Abs(gap) < 1e-3)
                return new Reach(Vector3Double.Dot(n, p) / pad.Length(), normal, burn, w, offsets);
            // The first step as if the burn's place went round with the moon (the gap closing by
            // the stack's lap over the moon's), then the secant on the gaps found.
            var next = last is { } l && l.Gap != gap
                ? w - gap * (w - l.W) / (gap - l.Gap)
                : w + gap / (1 - AscentLapSeconds / Bodies.OrbitOf(Moon, burn).Period);
            (last, w) = ((w, gap), next);
        }
        throw new InvalidOperationException($"the wait for the transfer's burn after a launch at {t:0.000} s did not settle (from {from:0.0} s, last tried {w:0.000} s): the burn's place moves round nearly as fast as the stack");
    }

    /// <summary>
    /// <paramref name="v"/>, a direction or place from the planet's centre among the stars matching
    /// its ground at world time <paramref name="then"/>, among those matching it at <paramref name="now"/>.
    /// </summary>
    public static Vector3 AmongStars(Vector3 v, double then, double now) => StarsAt(v, then, now).ToSingle();

    /// <summary>
    /// The upper stage (<see cref="Stage"/>), numbered <paramref name="id"/>, on a circle
    /// <see cref="LowHeight"/> up round the planet at world time <paramref name="worldTime"/>, with
    /// the moon a transfer's lead ahead: <paramref name="wait"/> seconds (by default <see cref="Wait"/>) short of where one burn along
    /// the way it goes would take it to the moon, passing over its poles. Its plane is tilted from
    /// the moon's orbit's (see the remarks above), by some 7°. With <paramref name="lander"/>, the
    /// lander launcher's stack (see <see cref="Stage"/>).
    /// </summary>
    public static VesselState LowOrbit(int id, double worldTime, double wait = Wait, bool lander = false)
    {
        var burn = worldTime + wait;
        var (n, r) = Solve(burn, periapsis: 1000); // where the transfer's burn is cut (TransferPilot.CutBelow)
        var (normal, at, _) = Aimed(burn, n, r);
        double low = Planet.SeaRadius + LowHeight, speed = Math.Sqrt(Planet.Mu / low);
        var angle = -speed / low * wait;
        var from = QuaternionDouble.FromAxisAngle(normal, angle).Rotate(at);
        // Among the stars at the burn, then at the start (the planet's ground turns under it meanwhile).
        var centre = StarsAt(from * low, burn, worldTime) + (Vector3Double)Planet.Centre;
        var stars = StarsAt(Vector3Double.Cross(normal, from) * speed, burn, worldTime);
        var c = centre.ToSingle();
        var velocity = Planet.OverGround(c, stars.ToSingle());
        return Stage(id, Planet, c, velocity, Planet.Up(c), lander);
    }

    /// <summary>
    /// The upper stage (<see cref="Stage"/>), numbered <paramref name="id"/>, <see cref="Inside"/>
    /// metres inside the moon's sphere on the way in from the low orbit's transfer (burnt at once
    /// at the window, as trimmed: round the moon over its poles, its periapsis
    /// <see cref="MoonHeight"/> over its highest ground), its nose along the way it goes; and the
    /// world time it is there. With <paramref name="lander"/>, the lander launcher's stack (see
    /// <see cref="Stage"/>).
    /// </summary>
    public static (VesselState Vessel, double Time) Arriving(int id, bool lander = false)
    {
        var burn = Start + Wait;
        var (n, r) = Solve(burn, periapsis: Moon.MaxHeight + MoonHeight);
        var (normal, at, speed) = Aimed(burn, n, r);
        var low = Planet.SeaRadius + LowHeight;
        var legs = new List<PathLeg>();
        Patched.Legs(Planet, ((Vector3Double)Planet.Centre + at * low).ToSingle(), (Vector3Double.Cross(normal, at) * speed).ToSingle(), burn, legs);
        var leg = legs.First(l => l.Body == Moon);
        var anomaly = leg.Orbit.Descending(Moon.SphereRadius - Inside) ?? throw new InvalidOperationException("the transfer never comes that far into the moon's sphere");
        var seconds = leg.Orbit.TimeTo(anomaly);
        var (place, stars) = leg.Orbit.StateAfter(seconds);
        // Among the moon's stars as they matched its ground at the burn; turned to its ground when it is there.
        var time = leg.Start + seconds;
        var back = -(time - burn);
        var centre = Moon.Turned(place, back);
        var velocity = Moon.OverGround(centre, Moon.TurnedDirection(stars, back));
        return (Stage(id, Moon, centre, velocity, velocity, lander), time);
    }

    /// <summary>How far inside the moon's sphere <see cref="Arriving"/> is, in metres.</summary>
    public const float Inside = 200f;

    /// <summary>
    /// The upper stage (<see cref="Stage"/>), numbered <paramref name="id"/>, on a round orbit
    /// <paramref name="height"/> over the moon's highest ground, over both its poles, in the plane
    /// through its north pole and <see cref="Scene.MoonLandingSpot"/>, going north on the far side
    /// and over the pole to the spot: 60° short of the spot (just before a landing lines its track
    /// up with it), its nose along the way it goes; in the moon's frame (whatever the world's time).
    /// With <paramref name="lander"/>, the lander launcher's stack (see <see cref="Stage"/>).
    /// </summary>
    public static VesselState MoonOrbit(int id, float height, bool lander = false)
    {
        var (centre, velocity) = RoundTheMoon(height);
        return Stage(id, Moon, centre, velocity, velocity, lander);
    }

    /// <summary>
    /// The lander alone, numbered <paramref name="id"/>, full, legs in and engine off, its centre of
    /// mass where <see cref="MoonOrbit"/> puts the stack's, moving as it does, its nose along the way
    /// it goes: as it is once let go of the upper stage there.
    /// </summary>
    public static VesselState Lander(int id, float height)
    {
        var (centre, velocity) = RoundTheMoon(height);
        return CrossingTests.Craft(id, Moon, centre, velocity, velocity, first: 2, last: 2, vehicle: Vehicle.LanderLauncher) with { Fuel = [0f, 0f, LanderFuel] };
    }

    /// <summary><see cref="MoonOrbit"/>'s place, and its velocity over the moon's ground.</summary>
    private static (Vector3 Centre, Vector3 Velocity) RoundTheMoon(float height)
    {
        var pole = Moon.NorthPole;
        var spot = OnTheMoon.LandingSpot;
        var towards = Vector3.Normalize(spot - pole * Vector3.Dot(spot, pole)); // the spot's way from the pole, square to it
        var (latitude, _) = Scene.MoonLandingSpot;
        // Round the plane from "towards", up through the pole: the spot is at its latitude; it is flown downwards.
        var angle = float.DegreesToRadians(latitude) + float.DegreesToRadians(60f);
        var up = towards * MathF.Cos(angle) + pole * MathF.Sin(angle);
        var way = towards * MathF.Sin(angle) - pole * MathF.Cos(angle); // the way the angle falls
        var radius = Moon.SeaRadius + Moon.MaxHeight + height;
        var centre = Moon.Centre + up * radius;
        return (centre, Moon.OverGround(centre, way * MathF.Sqrt(Moon.Mu / radius)));
    }
}
