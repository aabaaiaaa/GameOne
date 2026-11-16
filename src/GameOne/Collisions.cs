using System.Numerics;
using GameOne.Physics;

namespace GameOne;

/// <summary>
/// A collision predicted ahead for <paramref name="Watched"/>: with what (<paramref name="With"/>,
/// and the vessel, if it is one: <paramref name="Other"/>), in how long, and how fast.
/// </summary>
/// <param name="Seconds">
/// Game seconds until it hits: for another vessel, until their closest approach (negative after
/// it, while their sizes still overlap), so a pass has one time running through it; for anything
/// else, until it reaches it.
/// </param>
/// <param name="ClosingSpeed">How fast they close now, over the ground.</param>
/// <param name="HitSpeed">How fast they are predicted to close as they hit, with the vessels' present thrust (0 when the thrust stops it short).</param>
/// <param name="At">Where it hits, over <see cref="Collision.Body"/>'s ground, in its frame (roughly: what is under the collision there, the sea or the ground, is what it is named by); for a tower or mast, the point on its middle line nearest that, so which one is told by its place.</param>
/// <param name="Touch">For another vessel, the game seconds from their sizes first touching to their closest approach (0 for anything else, hit as it is reached): the slow-down reaches its slowest that much sooner, so it is slow as they touch.</param>
/// <param name="Hit">The body whose ground it comes down on, where that is not the one <paramref name="Watched"/> is at now: one its path crosses into first (see <see cref="Patched"/>). Null for its own body's.</param>
internal readonly record struct Collision(Vessel Watched, SolidKind With, Vessel? Other, double Seconds, float ClosingSpeed, float HitSpeed, Vector3 At, double Touch = 0.0,
    GroundBody? Hit = null)
{
    /// <summary>The body it hits at, in whose frame <see cref="At"/> is: its own, or the one its path crosses into (<see cref="Hit"/>).</summary>
    public GroundBody Body => Hit ?? Watched.Body;
}

/// <summary>
/// A near miss predicted for <paramref name="Watched"/> (see <see cref="Collisions.NearestMiss"/>):
/// <paramref name="Other"/> passing it within <see cref="Scene.NearMissLengths"/> of its lengths
/// without touching, too fast to be seen at normal speed.
/// </summary>
/// <param name="Seconds">Game seconds to their closest approach (negative after it, while the slowing for it eases back).</param>
/// <param name="Gap">The gap between their outlines there (m, over 0): each body an axis as wide as itself (<see cref="Unwatched.Body"/>), turned as now.</param>
/// <param name="PassSpeed">How fast they pass each other there.</param>
/// <param name="FromSeconds">The game seconds, at normal speed, from where its slowing begins (<see cref="Scene.SlowdownFrom"/> from the watched one, middle to middle) to their closest approach.</param>
internal readonly record struct NearMiss(Vessel Watched, Vessel Other, double Seconds, float Gap, float PassSpeed, float FromSeconds)
{
    /// <summary>
    /// Its deepest scale, at the closest approach: what makes it pass at <see cref="Scene.SlowdownPassSpeed"/>
    /// to the eye, never faster than the player's rate.
    /// </summary>
    public static float Scale(in NearMiss m) => m.PassSpeed > 0f ? Math.Clamp(Scene.SlowdownPassSpeed / m.PassSpeed, 0f, 1f) : 1f;

    /// <summary>
    /// The scale with <paramref name="seconds"/> game seconds left to the closest approach (negative
    /// after it): from where the slowing begins (<see cref="Scene.SlowdownFrom"/>) its speed
    /// to the eye eases evenly with its distance, from its own down to
    /// <see cref="Scene.SlowdownPassSpeed"/> as it comes within <see cref="Scene.SlowdownHoldWithin"/>
    /// of the closest approach, with no speed held on the way; it stays at that until it is as far
    /// past, then eases back to 1 over <see cref="Scene.SlowdownEaseSeconds"/> of real time.
    /// </summary>
    /// <remarks>
    /// Its speed to the eye even with distance is its scale in a straight line in game seconds
    /// (distance at its speed). The ease back is exponential in real time, <c>s e^(k r)</c> with
    /// <c>k = ln(1/s) / ease</c>; over it the game's clock runs <c>τ = (scale − s) / k</c> game
    /// seconds, so in game seconds it is the straight line <c>s + k τ</c> too, and neither needs a
    /// real clock of its own.
    /// </remarks>
    public float ScaleAt(double seconds)
    {
        var s = Scale(this);
        if (s >= 1f || s <= 0f || FromSeconds <= 0f)
            return 1f; // passing slowly enough already: nothing to slow (NearestMiss finds none such)
        var hold = MathF.Min(Scene.SlowdownHoldWithin / PassSpeed, 0.5f * FromSeconds); // game seconds each side
        var t = (float)seconds;
        var outside = MathF.Max(0f, MathF.Abs(t) - hold);
        if (t >= 0f)
            return MathF.Min(1f, s + (1f - s) / (FromSeconds - hold) * outside);
        return MathF.Min(1f, s + MathF.Log(1f / s) / Scene.SlowdownEaseSeconds * outside);
    }
}

/// <summary>
/// Each witnessed vessel's soonest fast collision ahead (over <see cref="Scene.FastCollisionSpeed"/>
/// as it hits), within a horizon: with another vessel, the ground or the sea, a raft on it (a
/// floe, a drone ship), a service tower or a light mast. What the warning and the forced slow-down go by, so nothing passes through
/// anything unseen. One per flight: the ground's answer for each vessel is kept between its checks
/// (<see cref="Scene.GroundCheckSeconds"/>), and counted down meanwhile on the flight's clock
/// (<see cref="Flight.WorldTime"/>, read from the flight it was made for).
/// </summary>
/// <remarks>
/// <para>Every other vessel is checked each time, in one pass that allocates nothing, over what
/// was read of each once that step (<see cref="Snapshot"/>): most are ruled out by their speed
/// alone (two that can't close fast enough can't collide fast), the rest by their straight-line
/// miss less the most their pulls could bend it. Pairs are worked out
/// among the stars, where each is bent only by gravity and the air (in the ground's frame the
/// turn's sideways pull, 45 m/s² at the top speed, bends both), from their size-spheres' centres
/// (<see cref="Size"/>): both paths flown ahead together in chords (<see cref="Closest"/>, from
/// <see cref="LongestChordSeconds"/> down to <see cref="PathStepSeconds"/> as they near), as no
/// one pull between them holds the horizon long on this small a planet.</para>
/// <para>The near misses (<see cref="NearestMiss"/>, with the player's setting on) are read from
/// the same snapshot and flown by the same march, with their present thrust in their paths: another
/// vessel passing within <see cref="Scene.NearMissLengths"/> lengths, outline to outline, without
/// touching, and too fast to be seen; the deepest is what the slowing follows, the soonest still
/// ahead (<see cref="SoonestMiss"/>) what the warning names.</para>
/// <para>The ground is the path's own orbit, its size-sphere's coming within its size of the
/// terrain (<see cref="Orbit"/>, which samples the
/// terrain, so only every <see cref="Scene.GroundCheckSeconds"/>), patched across the spheres of
/// influence (<see cref="Patched.Legs"/>): any body's ground, the one the path comes down on, whether
/// the vessel is at that body yet or crosses into its pull first. The towers and masts, standing
/// still on the turning ground, are hit by the path flown ahead in the ground's frame as the
/// physics falls (<see cref="Planet.FallStep"/>), in chords of <see cref="PathStepSeconds"/>, as
/// far as the furthest of them it could reach.</para>
/// <para>How fast it hits: the closing speed as it hits (gravity's change in it included), less
/// what the vessels' thrust takes off it on the way: <c>√max(0, c² − 2 b d)</c>, <c>b</c> the
/// deceleration of their closing by thrust along the line of approach, <c>d</c> the distance to the
/// collision. So a landing burn, slowing as it falls, is no fast collision.</para>
/// </remarks>
internal sealed class Collisions
{
    /// <summary>
    /// The chords the path to a tower or mast is flown in: at the top speed each is 1 km, and the
    /// turn's sideways pull bows it from its chord by a·dt²/8, a third of a metre: well inside a
    /// mast's and a vessel's sizes. Also <see cref="Closest"/>'s shortest chord (within it, one pull
    /// between the pair is solved for their closest), and its bound on the chords it flies (the
    /// horizon in chords this short, so a degenerate pair can't march for ever).
    /// </summary>
    private const float PathStepSeconds = 0.25f;

    private static float TowerRadius => TowersAndMasts.TowerRadius;

    private readonly Flight _flight;

    /// <summary>The ground's answers, one per vessel asked of lately: the flown one, the small views' and the main view's, with room to spare, the stalest given up for a new one.</summary>
    private readonly GroundAnswer[] _ground = new GroundAnswer[2 * (1 + Scene.MaxSmallViews + Scene.MainViewWitnessed)];

    // The path flown ahead to the towers and masts: each chord's end, velocity, time and the
    // distance along it, kept so a check allocates nothing.
    private Vector3[] _at = [], _velocity = [];
    private float[] _time = [], _along = [];

    private struct GroundAnswer
    {
        public Vessel? Vessel;
        public GroundBody? From; // the body the vessel was at as it was asked: crossed since, it is asked again
        public double AskedAt;
        public bool Collides;
        public double Seconds;
        public SolidKind Kind;
        public GroundBody? Body; // whose ground is hit, in whose frame At and Arriving are
        public Vector3 At, Arriving;
        public float Speed, Length;
    }

    /// <summary>The legs of the path the ground is asked along (<see cref="Ask"/>): kept, so an asking allocates nothing.</summary>
    private readonly List<PathLeg> _legs = new(Scene.PatchedLegs);

    public Collisions(Flight flight)
    {
        _flight = flight;
        Room(Scene.CollisionHorizonSeconds);
    }

    /// <summary>
    /// <paramref name="watched"/>'s soonest fast collision within <paramref name="horizon"/> game
    /// seconds, with anything in <paramref name="world"/> or the world itself, or null. The ground
    /// is asked again every <see cref="Scene.GroundCheckSeconds"/> of game time, or of real time
    /// while the game runs slower (<paramref name="scale"/>, the game's time scale), so a slowed
    /// approach is followed as closely in real time. The same check's collision asking the least
    /// slow-down scale is <see cref="Deepest"/> after it.
    /// </summary>
    public Collision? Soonest(Vessel watched, IReadOnlyList<Vessel> world, double horizon, float scale = 1f)
    {
        var started = Timings.Start();
        try
        {
            return Find(watched, world, horizon, scale);
        }
        finally
        {
            Timings.Stop(StepDetail.Collisions, started);
        }
    }

    /// <summary>The radius of <paramref name="v"/>'s size for predicting: the sphere round its middle (half its height up its axis) that holds it.</summary>
    public static float Size(Vessel v)
    {
        var half = v.Height / 2f;
        var widest = MathF.Max(v.Craft.WidestOpen, v.FairingOn ? v.Fairing.Radius : 0f);
        return MathF.Sqrt(half * half + widest * widest);
    }

    /// <summary>
    /// How long ahead of <paramref name="o"/> <paramref name="v"/> must be warned to dodge it: to
    /// turn a right angle (<see cref="TurnSeconds"/>), to thrust aside by its size and what it
    /// would hit's at its full push (its firing stage's, or with that empty the next one's:
    /// <see cref="Vessel.NextAcceleration"/>), and the player's moment (<see cref="Scene.WarningReactionSeconds"/>).
    /// Infinite with no fuel or engine to use: no dodge is possible, so it is warned of as soon as it is found.
    /// </summary>
    public static double WarningSeconds(Vessel v, Collision o)
    {
        var push = v.NextAcceleration;
        if (push <= 0f)
            return double.PositiveInfinity;
        var dodge = MathF.Sqrt(2f * (Size(v) + SizeOf(o)) / push);
        return TurnSeconds(v.TurnRate) + dodge + Scene.WarningReactionSeconds;
    }

    /// <summary>
    /// How long the steering takes to turn a vessel a right angle at <paramref name="turnRate"/>
    /// (<see cref="Vessel.TurnRate"/>): its aim turns at that rate (<c>Vessel.Steer</c>), and the
    /// body follows the aim (<c>RocketPhysics.Steer</c>): asked to turn at
    /// <see cref="RocketPhysics.SteerGain"/> × the sine of how far it is off, its turning is
    /// blended towards that at <see cref="RocketPhysics.SteerBlendRate"/> a second, while Jolt
    /// slows it at <see cref="RocketPhysics.AngularDamping"/>. Turning steadily with the aim (at the
    /// rate <c>w</c>), the blend's pull <c>k (G sin e − w)</c> must make up the damping's <c>c w</c>,
    /// so it is off by <c>e = asin(w (1 + c/k) / G)</c>: that angle behind, it reaches the right
    /// angle that much later, at that rate. (Once the aim stops, the last of <c>e</c> settles
    /// almost critically damped, within a few degrees while the dodge's burn begins.)
    /// </summary>
    public static float TurnSeconds(float turnRate)
    {
        var behind = turnRate * (1f + RocketPhysics.AngularDamping / RocketPhysics.SteerBlendRate) / RocketPhysics.SteerGain;
        return (MathF.PI / 2f + MathF.Asin(MathF.Min(1f, behind))) / turnRate;
    }

    /// <summary>
    /// What is coming, by its kind, for the warning and the slow-down to name: the one place a new
    /// kind of thing (an asteroid) brings its name, so neither assumes it is a vessel. Every body's
    /// ground is one kind, named by its body (<see cref="GroundBody.GroundName"/>: "the ground", "the
    /// moon"), the body it is hit at, whether the vessel is at it yet or its path crosses into it first.
    /// </summary>
    public static string Name(in Collision o) => Name(o.Body, o.With, o.At);

    /// <summary>What is coming, by its kind <paramref name="with"/>, hit at <paramref name="at"/> on <paramref name="body"/> (see <see cref="Name(in Collision)"/>): its ground by its own name.</summary>
    public static string Name(GroundBody body, SolidKind with, Vector3 at) => with switch
    {
        SolidKind.Craft => "another vessel",
        SolidKind.Ground => body.IsSea(body.Up(at)) ? "the water" : body.GroundName,
        SolidKind.Tower => "the service tower",
        SolidKind.Mast => "a light mast",
        SolidKind.Raft => OnAShip(at) ? "the drone ship" : "an ice floe",
        _ => "something",
    };

    /// <summary>Whether <paramref name="at"/> is on or by a drone ship: the rafts are the ships and the floes, and the ships sail where no ice is.</summary>
    private static bool OnAShip(Vector3 at)
    {
        var ships = Rafts.Ships;
        for (var i = 0; i < ships.Count; i++)
            if (ships[i].Within(at, 0f))
                return true;
        return false;
    }

    /// <summary>The radius of what <paramref name="o"/> hits, about its middle (or its middle line, for a tower or mast).</summary>
    private static float SizeOf(in Collision o) => o.With switch
    {
        SolidKind.Craft => o.Other is { } other ? Size(other) : 0f,
        SolidKind.Tower => TowersAndMasts.TowerRadius,
        SolidKind.Mast => TowersAndMasts.MastRadius,
        _ => 0f,
    };

    /// <summary>The middle of <paramref name="v"/>'s size-sphere now.</summary>
    internal static Vector3 Middle(Vessel v) => Vector3.Transform(new Vector3(0f, v.Height / 2f, 0f), v.Model);

    /// <summary>Its engine's push now, as an acceleration: along its nose, lit engines only.</summary>
    internal static Vector3 Thrust(Vessel v) => v.EngineLit ? v.Up * (v.Throttle * v.MaxAcceleration) : Vector3.Zero;

    /// <summary>Whether it is held by what it is on (standing, afloat, frozen, touching), so it goes round with the ground rather than falling.</summary>
    /// <remarks>
    /// Under formula its craft is a stand-in, out of the world, its <see cref="Craft.Frozen"/> as it
    /// was when it left: one landed or afloat is held by its state (<see cref="Vessel.Grounded"/>), as the formula keeps it where it is.
    /// </remarks>
    internal static bool Held(Vessel v) => v.Grounded || (!v.OnFormula && v.Craft.Frozen);

    /// <summary>
    /// Its acceleration among <paramref name="body"/>'s stars at <paramref name="at"/>, thrust apart:
    /// its pull, falling; or the ground's own turn, held (the ground's frame's outward pull, turned round).
    /// </summary>
    internal static Vector3 Fall(GroundBody body, bool held, Vector3 at) => held ? -body.OutwardPull(at) : body.Gravity(at);

    /// <summary>How fast <paramref name="body"/>'s air slows it at <paramref name="at"/>, per second (none, held: the air goes round with the ground).</summary>
    internal static float Drag(GroundBody body, bool held, Vector3 at) => held ? 0f : Scene.DragRate * body.AirDensity(body.Altitude(at));

    private Collision? Find(Vessel watched, IReadOnlyList<Vessel> world, double horizon, float scale)
    {
        (_best, _deepest) = (null, null);
        if (watched.State == FlightState.Crashed)
            return null; // a wreck is gone
        var now = _flight.WorldTime;
        var middle = Middle(watched);
        var velocity = watched.Velocity;
        var size = Size(watched);
        var thrust = Thrust(watched);
        _horizon = horizon;
        Pairs(watched, world, now, horizon, middle, velocity, size, thrust);

        // The ground, the towers and the masts stand still: only a vessel that could be going fast
        // enough by the end of the horizon, by its pulls and its thrust, can hit them fast.
        var pull = (watched.Body.Gravity(middle) + watched.Body.OutwardPull(middle)).Length();
        var fastest = velocity.Length() + (pull + thrust.Length()) * (float)horizon;
        var fast = fastest > Scene.FastCollisionSpeed;
        if (fast || MayReachFast(watched.Body, middle, velocity, pull + thrust.Length(), now, (float)horizon))
            Ground(watched, now, horizon, scale, thrust);
        if (fast)
            Solids(watched, horizon, middle, velocity, size, thrust);
        return _best;
    }

    /// <summary>
    /// Whether something at <paramref name="middle"/> in <paramref name="body"/>'s frame, moving at
    /// <paramref name="velocity"/> over its ground and pushed by up to <paramref name="push"/> where it
    /// is, could hit another body's ground fast within <paramref name="horizon"/>, along its path
    /// into that body's pull: going fast enough over that body's ground by then (its pull at its
    /// datum, and its frame's own turning pulls where it is now, the most they add), and near enough
    /// to reach its sphere of influence first (the root's, everywhere, by its highest ground). Every
    /// other body in <see cref="Bodies.All"/> is asked, by its frame's change alone, so nothing is
    /// written for any pair.
    /// </summary>
    /// <remarks>
    /// A bound, not a prediction: the pull is taken at the datum (its lowest ground's would read a
    /// sea-less body's map, made only once it is needed), a few per cent short over ground below it;
    /// the turning pulls (Coriolis and centrifugal) at its speed and place now, which they change
    /// little within the horizon. A collision this misses is one closing within a few m/s of the fast
    /// speed, which the ground's rules judge as a touch in any case.
    /// </remarks>
    internal static bool MayReachFast(GroundBody body, Vector3 middle, Vector3 velocity, float push, double now, float horizon)
    {
        var bodies = Bodies.All;
        for (var i = 0; i < bodies.Count; i++)
        {
            var other = bodies[i];
            if (other == body)
                continue;
            var change = Bodies.Change(body, other, now);
            var at = change.Point(middle).ToSingle();
            var over = change.Velocity(middle, velocity).ToSingle();
            var pulls = push + other.Mu / (other.SeaRadius * other.SeaRadius) + other.TurningPull(at, over).Length();
            var fastest = over.Length() + pulls * horizon;
            if (fastest <= Scene.FastCollisionSpeed)
                continue;
            var edge = float.IsFinite(other.SphereRadius) ? other.SphereRadius : other.SeaRadius + other.MaxHeight;
            if ((at - other.Centre).Length() - edge < fastest * horizon)
                return true;
        }
        return false;
    }

    // The soonest fast one found so far in a check, the one asking the least scale, and its
    // horizon: fields, so a check allocates nothing.
    private Collision? _best;
    private Collision? _deepest;
    private float _deepestScale;
    private double _horizon;

    /// <summary>
    /// The last check's fast collision that asks the slow-down for the least scale
    /// (<see cref="Slowdown.ScaleFor"/>), or null: what the slow-down follows, as the soonest
    /// (what <see cref="Soonest"/> returns, and the warning goes by) can hide a faster one just
    /// behind it (a slow near miss a moment before a fast hit), whose step must be the finer.
    /// </summary>
    public Collision? Deepest => _deepest;

    /// <summary>
    /// Keeps <paramref name="o"/> as the soonest, if it is fast, within the horizon and sooner than
    /// any so far; and as the deepest, if it asks for a lower scale than any so far.
    /// </summary>
    private void Consider(in Collision o)
    {
        if (o.HitSpeed <= Scene.FastCollisionSpeed || o.Seconds > _horizon)
            return;
        if (_best is not { } b || o.Seconds < b.Seconds)
            _best = o;
        var scale = Slowdown.ScaleFor(o.Seconds, o.HitSpeed, o.Touch);
        if (_deepest is null || scale < _deepestScale)
            (_deepest, _deepestScale) = (o, scale);
    }

    /// <summary>
    /// Every other vessel not wrecked, as the two size-spheres' centres move among the stars:
    /// each bent by its fall, their closest approach found by flying both ahead from the line's
    /// (<see cref="Closest"/>), as no one pull between them follows a path the horizon long on
    /// this small a planet. Read from the
    /// world's snapshot (<see cref="Snapshot"/>), shared by every vessel asked of in a step.
    /// </summary>
    private void Pairs(Vessel watched, IReadOnlyList<Vessel> world, double now, double horizon, Vector3 middle, Vector3 velocity, float size,
        Vector3 thrust)
    {
        Snapshot(world, now);
        var h = (float)horizon;
        var heldW = Held(watched);
        var atW = watched.CentreOfMass; // where its velocity is for, as each other's is in the snapshot
        var starsW = watched.Body.AmongStars(atW, velocity);
        var dragW = Drag(watched.Body, heldW, atW);
        var fallW = Fall(watched.Body, heldW, atW) - velocity * dragW;
        var pushW = thrust.Length();
        for (var i = 0; i < _count; i++)
        {
            ref readonly var other = ref _world[i];
            if (other.Vessel == watched || other.Crashed || other.Body != watched.Body) // two at different bodies never meet
                continue;
            // Among the stars their closing is u + a t as the pull between them (gravity and the air)
            // bends it, so it can be no faster than |u| + |a| h by the horizon, and their thrust adds at most its push:
            // under the threshold, it can't be colliding fast. (Over the ground they close at u plus the
            // turn across their gap, Ω × r: for two that collide, r is about −u t, so that adds across
            // u and over the ground they close at least as fast; only as they hit are the two the same.)
            var u = other.Stars - starsW;
            var a = other.Fall - fallW;
            var pulled = a.Length() + pushW + other.Push;
            if (u.Length() + pulled * h <= Scene.FastCollisionSpeed)
                continue;

            var closing = (other.Velocity - velocity).Length();
            var r = other.Middle - middle;
            var uu = u.LengthSquared();
            var line = uu > 1e-6f ? -Vector3.Dot(r, u) / uu : 0f;
            var reach = size + other.Size;
            // The nearest the line comes within the horizon, less the most the pull between them
            // (about what it is now) can bend it by then.
            var nearest = Math.Clamp(line, 0f, h);
            if ((r + u * nearest).Length() - a.Length() * h * h / 2f > reach)
                continue;

            // Their closest approach, both paths flown ahead by their pulls (gravity, and the air's
            // drag), not their thrust, which only slows the collision (below): over the horizon on this
            // small a planet the pull between them changes too much along the way for any one pull
            // to follow (see Closest), so a hit 20 s off was found only 15 s off.
            var t = Closest(watched.Body, middle, starsW, heldW, Vector3.Zero, other.Middle, other.Stars, other.Held, Vector3.Zero, MathF.Min(line, h), h,
                out var apart, out var rel, out var whereW);
            if (t > h)
                continue;
            var miss = apart.Length();
            if (miss >= reach || (t < 0f && r.Length() >= reach))
                continue; // passing wide, or past and apart already

            var relSpeed = rel.Length();
            var toHit = t > 0f ? MathF.Max(0f, relSpeed * t - MathF.Sqrt(reach * reach - miss * miss)) : 0f;
            var slowing = relSpeed > 1e-3f ? -Vector3.Dot(other.Thrust - thrust, rel / relSpeed) : 0f;
            var hitSpeed = MathF.Sqrt(MathF.Max(0f, relSpeed * relSpeed - 2f * slowing * toHit));
            // Where it hits: its own way among the stars, turned back over the ground as it will be by then.
            var at = t > 0f ? watched.Body.Turned(whereW, -t) : middle;
            var touch = relSpeed > 1e-3f ? MathF.Sqrt(reach * reach - miss * miss) / relSpeed : 0f;
            Consider(new Collision(watched, SolidKind.Craft, other.Vessel, t, closing, hitSpeed, at, touch));
        }
    }

    /// <summary>
    /// <paramref name="watched"/>'s deepest near miss within <paramref name="horizon"/> game seconds
    /// with another vessel in <paramref name="world"/> (the one asking the least
    /// <see cref="NearMiss.ScaleAt"/> now, then the soonest: so it is both what the slowing
    /// follows and, all being at the player's rate ahead of their ease-in, what is coming first),
    /// or null: one passing within
    /// <see cref="Scene.NearMissLengths"/> of its own lengths (<see cref="Vessel.Height"/>), outline
    /// to outline, without touching (a touch is a collision, <see cref="Soonest"/>'s), and passing
    /// faster than <see cref="Scene.NearMissSlowAbove"/>, its slowing beginning
    /// <see cref="Scene.SlowdownFrom"/> out, however long its way in then takes in real time (no
    /// time limit). One already past stays while the slowing for it still eases back
    /// (<see cref="NearMiss.ScaleAt"/> under 1). Only vessels: the ground, the sea, the rafts, the
    /// towers and the masts are never near misses. The same check's soonest pass still ahead is
    /// <see cref="SoonestMiss"/> after it. Allocates nothing.
    /// </summary>
    /// <remarks>
    /// Read from the same snapshot as <see cref="Pairs"/>, and solved alike among the stars (their
    /// closest approach by <see cref="Closest"/>), but with their present thrust in their paths,
    /// as a pass a length off is bent by a burn where a collision's sizes hide it. At the closest
    /// approach their bodies are each an axis as wide as itself (<see cref="Unwatched.Body"/>), turned
    /// as now: over the seconds ahead a vessel turns little, and a near miss is told soon enough to
    /// be asked again each step.
    /// </remarks>
    public NearMiss? NearestMiss(Vessel watched, IReadOnlyList<Vessel> world, double horizon)
    {
        var started = Timings.Start();
        try
        {
            return Miss(watched, world, horizon);
        }
        finally
        {
            Timings.Stop(StepDetail.Collisions, started);
        }
    }

    /// <summary>
    /// The last <see cref="NearestMiss"/>'s soonest pass still ahead (its closest approach not yet
    /// reached), or null: what the warning names (<see cref="Flight.NearMissWarning"/>), as the
    /// deepest may be one just gone, still easing back, while the next is coming.
    /// </summary>
    public NearMiss? SoonestMiss => _soonestMiss;

    private NearMiss? _soonestMiss;

    private NearMiss? Miss(Vessel watched, IReadOnlyList<Vessel> world, double horizon)
    {
        _soonestMiss = null;
        if (watched.State == FlightState.Crashed)
            return null; // a wreck is gone
        Snapshot(world, _flight.WorldTime);
        var h = (float)horizon;
        var within = Scene.NearMissLengths * watched.Height;
        // Slowed only passing faster than NearMissSlowAbove (and than SlowdownPassSpeed, which it slows to).
        var slowest = MathF.Max(Scene.NearMissSlowAbove, Scene.SlowdownPassSpeed);
        var from = Scene.SlowdownFrom;
        var middle = Middle(watched);
        var velocity = watched.Velocity;
        var thrust = Thrust(watched);
        var heldW = Held(watched);
        var atW = watched.CentreOfMass;
        var starsW = watched.Body.AmongStars(atW, velocity);
        var dragW = Drag(watched.Body, heldW, atW);
        var fallW = Fall(watched.Body, heldW, atW) - velocity * dragW;
        var pushW = thrust.Length();
        var reachW = Reach(watched);
        var (bottomW, topW, widthW) = Unwatched.Body(watched);
        NearMiss? best = null;
        var bestScale = 1f;
        for (var i = 0; i < _count; i++)
        {
            ref readonly var other = ref _world[i];
            if (other.Vessel == watched || other.Crashed || other.Body != watched.Body) // two at different bodies never meet
                continue;
            // As in Pairs: they can pass no faster than |u| + |a| h by the horizon, thrust and all.
            var u = other.Stars - starsW;
            var a = other.Fall - fallW;
            if (u.Length() + (a.Length() + pushW + other.Push) * h <= slowest)
                continue;
            // Their middles must pass within a length of outlines plus each one's reach from its middle.
            var r = other.Middle - middle;
            var uu = u.LengthSquared();
            var line = uu > 1e-6f ? -Vector3.Dot(r, u) / uu : 0f;
            var near = within + reachW + other.Reach;
            var t = Math.Clamp(line, -h, h);
            if ((r + u * t).Length() - (a.Length() + pushW + other.Push) * h * h / 2f > near)
                continue;

            // Their closest approach, both paths flown ahead with their pulls and thrust: a near
            // miss's gap is a few metres in hundreds a pull bends the pass by over the horizon,
            // which no one pull for the whole way (as Pairs takes) can follow on this small a planet.
            t = Closest(watched.Body, middle, starsW, heldW, thrust, other.Middle, other.Stars, other.Held, other.Thrust, t, h, out var apart, out var passing, out _);
            if (t > h)
                continue;
            var miss = apart.Length();
            if (miss >= near)
                continue;
            var (bottom, top, width) = Unwatched.Body(other.Vessel);
            var gap = TowersAndMasts.Closest(bottomW, topW, apart + bottom, apart + top).Apart - widthW - width;
            if (gap <= 0f || gap >= within)
                continue; // touching (a collision's), or wider than a length
            var speed = passing.Length();
            if (speed <= 1e-3f)
                continue;
            if (speed <= slowest)
                continue; // passing slowly enough to be seen: nothing to slow
            var fromSeconds = MathF.Sqrt(MathF.Max(0f, from * from - miss * miss)) / speed; // its way in from where the slowing begins
            var m = new NearMiss(watched, other.Vessel, t, gap, speed, fromSeconds);
            var scale = m.ScaleAt(t);
            if (t < 0f && scale >= 1f)
                continue; // past, and eased back already
            if (t >= 0f && (_soonestMiss is not { } soonest || t < soonest.Seconds))
                _soonestMiss = m;
            // The one asking the least scale now, then the soonest: a pass just gone, still easing
            // back, must not hide the next one's ease-in (as Deepest for the collisions).
            if (best is { } b && (scale > bestScale || (scale == bestScale && t >= b.Seconds)))
                continue;
            (best, bestScale) = (m, scale);
        }
        return best;
    }

    /// <summary>
    /// When two moving among the stars, from <paramref name="pW"/> at <paramref name="vW"/> and
    /// <paramref name="pO"/> at <paramref name="vO"/> (each held or falling, with its thrust), come
    /// closest within <paramref name="horizon"/> game seconds either way (its sign <paramref name="estimate"/>'s,
    /// their straight lines' closest): how far the second is from the first then
    /// (<paramref name="apart"/>) and how fast it passes it (<paramref name="passing"/>); beyond
    /// the horizon (over it) if they don't within it, or if it can't be told (a NaN in a pull);
    /// and where the first is then, among the stars (<paramref name="at"/>). Allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>Both are flown ahead (velocity Verlet, each by its own pull where it is: gravity, or
    /// the ground's turn held, the air's drag over the ground, and its thrust), until the closest
    /// falls within the shortest chord, <see cref="PathStepSeconds"/>, which the pull between them
    /// there finds (<see cref="Approach"/>). Over 20 s on a planet 20 km across a path turns a long
    /// way round it, so the pull between two changes along their way (shrinking as they near,
    /// turning as they go round), and the one pull taken half-way bends a pass 20 s off by tens of
    /// metres wrong: more than a near miss's gap.</para>
    /// <para>Each chord is half the time to the closest as it looks from there, at most
    /// <see cref="LongestChordSeconds"/>, so a pass 20 s off takes a dozen chords, not eighty
    /// (it is asked for every pair near enough, every step), and the chords shorten as it nears,
    /// where the gap is told. Never more than half the way: a chord can't step past the closest.</para>
    /// </remarks>
    internal static float Closest(GroundBody body, Vector3 pW, Vector3 vW, bool heldW, Vector3 thrustW, Vector3 pO, Vector3 vO, bool heldO, Vector3 thrustO,
        float estimate, float horizon, out Vector3 apart, out Vector3 passing, out Vector3 at)
    {
        var way = estimate > 0f || (estimate == 0f && Vector3.Dot(pO - pW, vO - vW) < 0f) ? 1f : -1f;
        var (aW, aO) = (Pull(body, heldW, pW, vW, thrustW), Pull(body, heldO, pO, vO, thrustO));
        // Every chord is at least the shortest, so the horizon is crossed within this many: the
        // count bounds the march even should the time itself go wrong.
        var chords = horizon / PathStepSeconds + 1f;
        var n = 0;
        for (var t = 0f; ; n++)
        {
            var (r, u, a) = (pO - pW, vO - vW, aO - aW);
            // The closest by the pull between them here: within the shortest chord (or behind this
            // point, as only the first can find it), or the horizon passed. Asked as "not within",
            // so a NaN (from a pull, or Approach on a degenerate pair) ends the march, beyond the
            // horizon, rather than marching for ever.
            var tau = Approach(r, u, a, 0f);
            if (tau * way <= PathStepSeconds || !(MathF.Abs(t) < horizon) || !(n < chords))
            {
                apart = r + u * tau + a * (tau * tau / 2f);
                passing = u + a * tau;
                at = pW + vW * tau + aW * (tau * tau / 2f);
                return MathF.Abs(t + tau) <= horizon ? t + tau : horizon + 1f;
            }
            var dt = way * Math.Clamp(tau * way / 2f, PathStepSeconds, LongestChordSeconds);
            t += dt;
            pW += vW * dt + aW * (dt * dt / 2f);
            pO += vO * dt + aO * (dt * dt / 2f);
            var (nextW, nextO) = (Pull(body, heldW, pW, vW + aW * dt, thrustW), Pull(body, heldO, pO, vO + aO * dt, thrustO));
            vW += (aW + nextW) * (dt / 2f);
            vO += (aO + nextO) * (dt / 2f);
            (aW, aO) = (nextW, nextO);
        }
    }

    /// <summary>
    /// The longest chord <see cref="Closest"/> flies a pair's paths in: 2 s is a hundredth of a
    /// coast's turn round this planet (about 200 s near the ground), over which the pull turns by
    /// under 4°, so Verlet's error over the horizon is centimetres, against a near miss's metres.
    /// </summary>
    private const float LongestChordSeconds = 2f;

    /// <summary>Its acceleration among <paramref name="body"/>'s stars at <paramref name="at"/> going at <paramref name="stars"/>: its fall (see <see cref="Fall"/>), the air's drag on its speed over the ground, and its <paramref name="thrust"/>.</summary>
    private static Vector3 Pull(GroundBody body, bool held, Vector3 at, Vector3 stars, Vector3 thrust) =>
        Fall(body, held, at) - body.OverGround(at, stars) * Drag(body, held, at) + thrust;

    /// <summary>How far <paramref name="v"/>'s outline reaches from its middle, however it is turned: along its axis to its bell's end, and its width out from that.</summary>
    private static float Reach(Vessel v) => v.Height / 2f + v.CurrentStage.BellHeight + v.Width;

    /// <summary>
    /// When two whose gap is <c>r(t) = r + u t + a t²/2</c> (<paramref name="r"/> apart now,
    /// closing at <paramref name="u"/>, the pull between them <paramref name="a"/>) come closest:
    /// refined from <paramref name="t"/> (their straight lines' closest, say) by Newton's steps on
    /// d/dt |r(t)|² = 0. The last step of the witnessed vessels' pairs here (<see cref="Closest"/>,
    /// within its shortest chord), and the whole of the collisions of those under formula
    /// (<see cref="Unwatched.FindCollisions"/>): those are solved over intervals of
    /// <see cref="Scene.FormulaCollisionSeconds"/> only, a few tenths of a second over which one pull
    /// between them holds (gravity bows a coast's 0.25 s by under 8 cm), where a pair witnessed
    /// is looked for over the whole horizon, along which it doesn't.
    /// </summary>
    internal static float Approach(Vector3 r, Vector3 u, Vector3 a, float t)
    {
        for (var k = 0; k < 2; k++)
        {
            var rt = r + u * t + a * (t * t / 2f);
            var vt = u + a * t;
            var slope = vt.LengthSquared() + Vector3.Dot(rt, a);
            if (slope <= 1e-6f)
                break;
            t -= Vector3.Dot(rt, vt) / slope;
        }
        return t;
    }

    /// <summary>What the pairs' check reads of each vessel in the world, taken once a step.</summary>
    private struct Seen
    {
        public Vessel Vessel;
        public bool Crashed, Held; // Crashed: a wreck (out of every prediction)
        public GroundBody Body; // the body it is at: its places are in that body's frame
        public Vector3 At, Middle, Velocity, Stars, Fall, Thrust;
        public float Push, Size, Drag, Reach;
    }

    private Seen[] _world = [];
    private int _count;
    private IReadOnlyList<Vessel>? _worldList;
    private double _worldAt = double.NaN;
    private int _worldStarts = -1;

    /// <summary>
    /// Reads every vessel in <paramref name="world"/> once for the step at <paramref name="now"/>:
    /// the flown vessel and each witnessed one (the small views', the main view's) are asked of in the same step, and nothing moves
    /// between, so the thousand vessels' reads (their own objects, their crafts') are made once,
    /// and each vessel asked of then passes over these alone. Kept by the flight's starts too
    /// (<see cref="Flight.Starts"/>): a restart or load comes back at the time it was captured,
    /// in the same list and maybe as many, but every vessel in it new.
    /// </summary>
    private void Snapshot(IReadOnlyList<Vessel> world, double now)
    {
        if (now == _worldAt && world == _worldList && world.Count == _count && _flight.Starts == _worldStarts)
            return;
        (_worldAt, _worldList, _count, _worldStarts) = (now, world, world.Count, _flight.Starts);
        if (_world.Length < _count)
            _world = new Seen[Math.Max(_count, 2 * _world.Length)]; // grown only as the world grows past it
        for (var i = 0; i < _count; i++)
        {
            var v = world[i];
            ref var seen = ref _world[i];
            seen.Vessel = v;
            seen.Body = v.Body;
            seen.Crashed = v.State == FlightState.Crashed;
            if (seen.Crashed)
                continue;
            seen.Held = Held(v);
            seen.At = v.CentreOfMass;
            seen.Middle = Middle(v);
            seen.Velocity = v.Velocity;
            seen.Stars = v.Body.AmongStars(seen.At, seen.Velocity);
            seen.Drag = Drag(v.Body, seen.Held, seen.At);
            seen.Fall = Fall(v.Body, seen.Held, seen.At) - seen.Velocity * seen.Drag;
            seen.Thrust = Thrust(v);
            seen.Push = seen.Thrust.Length();
            seen.Size = Size(v);
            seen.Reach = Reach(v); // the near misses' (see NearestMiss)
        }
    }

    /// <summary>
    /// The ground or the sea under the path, or a raft on it (a floe, a drone ship): where its
    /// orbit ends, asked again every <see cref="Scene.GroundCheckSeconds"/> (of real time while
    /// slowed) and counted down between.
    /// </summary>
    private void Ground(Vessel watched, double now, double horizon, float scale, Vector3 thrust)
    {
        ref var answer = ref Answer(watched);
        var every = Scene.GroundCheckSeconds * Math.Min(1f, scale);
        if (answer.Vessel != watched || answer.From != watched.Body || now < answer.AskedAt || now - answer.AskedAt >= every)
            answer = Ask(watched, now, now + horizon + every);
        if (!answer.Collides)
            return;
        var seconds = Math.Max(0, answer.Seconds - (now - answer.AskedAt));
        if (seconds > horizon)
            return;
        // Another body's ground, along its path into that body's pull: its thrust and its closing
        // speed taken into that body's frame, where the way there is.
        var body = answer.Body!;
        var (push, closing) = (thrust, watched.Velocity.Length());
        if (body != watched.Body)
        {
            var change = Bodies.Change(watched.Body, body, now);
            push = change.Direction(thrust).ToSingle();
            closing = (float)change.Velocity(Middle(watched), watched.Velocity).Length();
        }
        // What is left of the way there: its length as asked, by the share of its time left (the
        // speed along it changes little between two checks).
        var toHit = answer.Seconds > 1e-6 ? answer.Length * (float)(seconds / answer.Seconds) : 0f;
        var slowing = -Vector3.Dot(push, answer.Arriving);
        var hitSpeed = MathF.Sqrt(MathF.Max(0f, answer.Speed * answer.Speed - 2f * slowing * toHit));
        // Going fast, its ground is only where it comes down (see TerrainTiles.Update): handed
        // over whatever speed it hits at, so a landing burn from high speed has its ground too.
        // Only its own body's: another's ground is laid in that body's physics once it is there.
        if (!watched.OnFormula && body == watched.Body && watched.Velocity.Length() > Scene.FastCollisionSpeed)
            watched.Craft.GroundAhead = (answer.At, answer.Arriving, answer.Speed, (float)seconds);
        Consider(new Collision(watched, answer.Kind, null, seconds, closing, hitSpeed, answer.At, Hit: body == watched.Body ? null : body));
    }

    /// <summary>
    /// Asks the ground under <paramref name="watched"/>'s path, up to world time
    /// <paramref name="until"/>: where and when it comes down, on what, how fast and which way it
    /// arrives, and how far it goes to get there. The path is patched across the spheres of
    /// influence (<see cref="Patched.Legs"/>), so it comes down on the ground of whichever body it
    /// is at by then, its own or one it crosses into first: the first leg that ends on its body's
    /// ground before it crosses out. Each leg is its size-sphere's (its middle's, with
    /// <see cref="Size"/> as the orbits' clearance), as the pairs' are: a point's path would clear a
    /// ridge, or a level pass over the ground, that its body hits.
    /// </summary>
    /// <remarks>
    /// The path starts at the body the vessel's place is at (<see cref="Bodies.Choose(GroundBody, Vector3, double)"/>),
    /// not always the one it is at: a vessel's body changes once a step (<see cref="Flight.Cross"/>),
    /// so one just inside another's sphere may be asked first, and a path from its own body never
    /// enters a sphere it is already in.
    /// </remarks>
    private GroundAnswer Ask(Vessel watched, double now, double until)
    {
        var from = Middle(watched);
        var size = Size(watched);
        var answer = new GroundAnswer { Vessel = watched, From = watched.Body, AskedAt = now };
        var (body, velocity) = (Bodies.Choose(watched.Body, from, now), watched.Velocity);
        if (body != watched.Body)
        {
            var change = Bodies.Change(watched.Body, body, now);
            (from, velocity) = (change.Point(from).ToSingle(), change.Velocity(from, velocity).ToSingle());
        }
        Patched.Legs(body, from, body.AmongStars(from, velocity), now, _legs, horizon: until, clearance: size);
        // The legs before the one that comes down, each by its length over its own body's ground:
        // an approximation of the way's length towards the ground hit (that body's ground moves
        // over another's: the planet's, 40 km out, at a couple of hundred metres a second), close
        // for the leg of a second or two the gap from the moon's sphere to its ground leaves.
        var length = 0f;
        for (var i = 0; i < _legs.Count; i++)
        {
            var leg = _legs[i];
            var down = leg.Start + leg.Orbit.SecondsToEnd;
            if (leg.Orbit.EndsOnGround && down <= leg.End)
            {
                Down(ref answer, leg, now, size);
                answer.Length += length;
                return answer;
            }
            if (i < _legs.Count - 1) // crossed out of its body's pull first: on into the next
                length += Along(leg, leg.End, now);
        }
        return answer;
    }

    /// <summary>
    /// Fills <paramref name="answer"/> for <paramref name="leg"/> coming down on its body's ground
    /// (or the sea, or a raft): when, where and on what, how fast and which way it arrives over the
    /// ground, all in the leg's body's frame; and the leg's way there.
    /// </summary>
    private static void Down(ref GroundAnswer answer, in PathLeg leg, double now, float size)
    {
        var (body, orbit) = (leg.Body, leg.Orbit);
        var seconds = leg.Start + orbit.SecondsToEnd - now;
        (answer.Collides, answer.Seconds, answer.Body) = (true, seconds, body);
        answer.At = orbit.Landing - body.Up(orbit.Landing) * size; // on the ground (or the sea, or a raft) under its middle as they hit
        // Over a floe or a drone ship (as it is now: it moves little before the collision), it is
        // the raft that is hit, not the water under it.
        answer.Kind = body.HasSea && Rafts.Under(answer.At) is not null ? SolidKind.Raft : SolidKind.Ground;
        if (orbit.Radial)
        {
            // Straight up and down among the stars: its fall's speed there (by its energy), and
            // the ground's own turn across it; along the line down to where it comes down.
            var (start, stars) = orbit.StateAfter(0);
            var at = body.Turned(start, -(leg.Start - now)); // where the leg starts, over the ground then
            var (r0, r1) = ((start - body.Centre).Length(), (answer.At - body.Centre).Length());
            var falling = MathF.Max(0f, stars.LengthSquared() + 2f * body.Mu * (1f / r1 - 1f / r0));
            answer.Speed = MathF.Sqrt(falling + body.GroundSpin(answer.At).LengthSquared());
            answer.Length = Vector3.Distance(at, answer.At);
            answer.Arriving = answer.Length > 1e-3f ? (answer.At - at) / answer.Length : Vector3.Zero;
            return;
        }
        // Its velocity over the ground as it arrives, turned back with the ground to where it is
        // hit; and the way's length over the ground.
        var arriving = body.OverGround(orbit.At(orbit.EndAnomaly), orbit.VelocityAt(orbit.EndAnomaly));
        answer.Speed = arriving.Length();
        answer.Arriving = answer.Speed > 1e-3f ? body.TurnedDirection(arriving / answer.Speed, -seconds) : Vector3.Zero;
        answer.Length = Along(leg, now + seconds, now);
    }

    /// <summary>
    /// The length of <paramref name="leg"/>'s way over its body's ground from its start to world time
    /// <paramref name="until"/>, from chords along it, each turned back to where the ground is by then
    /// (its points are in the frame that matches the ground at <paramref name="now"/>).
    /// </summary>
    private static float Along(in PathLeg leg, double until, double now)
    {
        const int Chords = 8;
        var (body, orbit) = (leg.Body, leg.Orbit);
        var seconds = until - leg.Start;
        if (!(seconds > 0) || !double.IsFinite(seconds))
            return 0f;
        var previous = body.Turned(orbit.PositionAfter(0), -(leg.Start - now));
        var length = 0f;
        for (var i = 1; i <= Chords; i++)
        {
            var t = seconds * i / Chords;
            var next = body.Turned(orbit.PositionAfter(t), -(leg.Start + t - now));
            length += Vector3.Distance(previous, next);
            previous = next;
        }
        return length;
    }

    /// <summary>The ground's answer kept for <paramref name="v"/>, or the stalest one to give up for it.</summary>
    private ref GroundAnswer Answer(Vessel v)
    {
        var stalest = 0;
        for (var i = 0; i < _ground.Length; i++)
        {
            if (_ground[i].Vessel == v)
                return ref _ground[i];
            if (_ground[i].Vessel is null || (_ground[stalest].Vessel is not null && _ground[i].AskedAt < _ground[stalest].AskedAt))
                stalest = i;
        }
        _ground[stalest] = default;
        return ref _ground[stalest];
    }

    /// <summary>
    /// The service towers and light masts it could reach within the horizon: each a vertical
    /// segment, hit by the path flown ahead (its size-sphere's centre) coming within its size and
    /// theirs of it (<see cref="TowersAndMasts"/>, the check the formula hits them by too). They
    /// stand in places (a pad, a zone, a ship), so a place the path passes nowhere near is passed
    /// over whole, by the box round the path and then chord by chord.
    /// </summary>
    private void Solids(Vessel watched, double horizon, Vector3 middle, Vector3 velocity, float size, Vector3 thrust)
    {
        if (!watched.Body.HasSites)
            return; // the towers and masts are the planet's alone (in its frame)
        var h = (float)horizon;
        var pull = (watched.Body.Gravity(middle) + watched.Body.OutwardPull(middle)).Length() + 2f * watched.Body.SpinRate * Scene.TopSpeed;
        var reach = velocity.Length() * h + pull * h * h / 2f + size + TowerRadius;
        var places = PadLights.All;
        var furthest = -1f;
        for (var p = 0; p < places.Count; p++)
        {
            var d = Vector3.Distance(places[p].Origin, middle);
            if (d <= reach + TowersAndMasts.PlaceReach(p))
                furthest = MathF.Max(furthest, d + TowersAndMasts.PlaceReach(p));
        }
        if (furthest < 0f)
            return;

        var chords = FlyAhead(watched.Body, middle, velocity, horizon, furthest + size + TowerRadius);
        var (low, high) = (_at[0], _at[0]);
        for (var k = 1; k <= chords; k++)
            (low, high) = (Vector3.Min(low, _at[k]), Vector3.Max(high, _at[k]));
        for (var p = 0; p < places.Count; p++)
        {
            var origin = places[p].Origin;
            var within = TowersAndMasts.PlaceReach(p) + size + TowerRadius;
            if (Vector3.Distance(origin, middle) > reach + TowersAndMasts.PlaceReach(p)
                || Vector3.Clamp(origin, low - new Vector3(within), high + new Vector3(within)) != origin)
                continue;
            var (first, last) = (-1, -1);
            for (var k = 0; k < chords; k++)
                if (Vector3.Distance((_at[k] + _at[k + 1]) / 2f, origin) <= Vector3.Distance(_at[k], _at[k + 1]) / 2f + within)
                {
                    if (first < 0)
                        first = k;
                    last = k;
                }
            if (first < 0)
                continue;
            for (var i = 0; i < TowersAndMasts.Count(p); i++)
            {
                var (kind, foot, top, radius) = TowersAndMasts.Solid(p, i);
                Collide(watched, kind, foot, top, size + radius, first, last, velocity, thrust);
            }
        }
    }

    /// <summary>
    /// Flies the path ahead from <paramref name="from"/> as the physics falls (gravity, the turn's
    /// pulls, the air), chord by chord, until <paramref name="horizon"/> or until it is
    /// <paramref name="far"/> from where it began: how many chords it flew.
    /// </summary>
    private int FlyAhead(GroundBody body, Vector3 from, Vector3 velocity, double horizon, float far)
    {
        Room(horizon);
        var (p, v) = (from, velocity);
        _at[0] = p;
        _velocity[0] = v;
        _time[0] = _along[0] = 0f;
        var n = 0;
        while (_time[n] < horizon - 1e-4 && n + 1 < _at.Length)
        {
            var dt = MathF.Min(PathStepSeconds, (float)horizon - _time[n]);
            var drag = Scene.DragRate * body.AirDensity(body.Altitude(p));
            v += body.FallStep(v, body.Gravity(p) + body.OutwardPull(p), drag, dt);
            p += v * dt;
            n++;
            _at[n] = p;
            _velocity[n] = v;
            _time[n] = _time[n - 1] + dt;
            _along[n] = _along[n - 1] + v.Length() * dt;
            if (Vector3.Distance(p, from) > far)
                break;
        }
        return n;
    }

    /// <summary>Room for a path to <paramref name="horizon"/>: made once, and again only for a longer horizon.</summary>
    private void Room(double horizon)
    {
        var points = (int)Math.Ceiling(horizon / PathStepSeconds) + 2;
        if (_at.Length >= points)
            return;
        _at = new Vector3[points];
        _velocity = new Vector3[points];
        _time = new float[points];
        _along = new float[points];
    }

    /// <summary>
    /// The first chord of the path flown ahead (of chords <paramref name="first"/> to <paramref name="last"/>) to come within <paramref name="reach"/> of the
    /// segment from <paramref name="foot"/> to <paramref name="top"/> (see <see cref="TowersAndMasts.Chord"/>):
    /// the collision with it, if any.
    /// </summary>
    private void Collide(Vessel watched, SolidKind with, Vector3 foot, Vector3 top, float reach, int first, int last,
        Vector3 velocity, Vector3 thrust)
    {
        for (var k = first; k <= last; k++)
        {
            var (a, b) = (_at[k], _at[k + 1]);
            if (!TowersAndMasts.Chord(a, b, foot, top, reach, out var inside))
                continue;
            var (t0, t1) = (_time[k], _time[k + 1]);
            var seconds = t0 + inside * (t1 - t0);
            var way = Vector3.Lerp(_velocity[k], _velocity[k + 1], inside);
            var speed = way.Length();
            var toHit = _along[k] + (_along[k + 1] - _along[k]) * inside;
            var slowing = speed > 1e-3f ? -Vector3.Dot(thrust, way / speed) : 0f;
            var hitSpeed = MathF.Sqrt(MathF.Max(0f, speed * speed - 2f * slowing * toHit));
            // Where: the point on its middle line nearest where it is hit, so a tower or mast is
            // told by its own place (the flight tells two masts apart by it), not by the path's.
            var hit =Vector3.Lerp(a, b, inside);
            var line = top - foot;
            var on = foot + line * Math.Clamp(Vector3.Dot(hit - foot, line) / MathF.Max(line.LengthSquared(), 1e-9f), 0f, 1f);
            Consider(new Collision(watched, with, null, seconds, velocity.Length(), hitSpeed, on));
            return; // its first coming within reach is the collision: on past it, it is the same one
        }
    }
}

/// <summary>
/// The service towers and light masts, as what moves hits them: the predictions'
/// (<see cref="Collisions"/>, along the path flown ahead) and the formula's (<see cref="Formula"/>,
/// along each of its steps), one check for both, so the two can't drift. Each is a vertical
/// segment, hit by a path's chord coming within a reach of it. They stand in places
/// (<see cref="PadLights.All"/>: a pad, a zone, a ship), so a place nowhere near is passed over
/// whole, by how far its towers and masts reach from its origin (<see cref="PlaceReach"/>).
/// </summary>
internal static class TowersAndMasts
{
    /// <summary>A service tower's reach about its middle line: its box's half width to its corners.</summary>
    public static readonly float TowerRadius = Scene.TowerHalfWidth * MathF.Sqrt(2f);

    /// <summary>A light mast's reach about its middle line.</summary>
    public const float MastRadius = Scene.MastHalfWidth;

    // Worked out once, on first use (the places are made from the sites and the terrain).
    private static readonly Lazy<float[]> Reaches = new(PlaceReaches);
    private static readonly Lazy<float> Top = new(HighestTop);

    /// <summary>How far from its origin place <paramref name="place"/>'s towers and masts reach (<see cref="PadLights.All"/>' order), in its own frame, so fixed.</summary>
    public static float PlaceReach(int place) => Reaches.Value[place];

    /// <summary>
    /// The highest any of them stands above the sea (a ship's masts with the room its deck rises
    /// on the highest waves): a path higher than this, by more than its own reach, hits none.
    /// </summary>
    public static float Highest => Top.Value;

    /// <summary>How many there are at place <paramref name="place"/>: its tower (the pads come first, in the sites' order: each has its site's tower), and its masts.</summary>
    public static int Count(int place) => (place < LaunchSites.All.Length ? 1 : 0) + PadLights.All[place].Masts.Length;

    /// <summary>The <paramref name="i"/>th of them at place <paramref name="place"/> (its tower first, if it has one): what it is, its middle line's foot and top as the place is now, and its reach about that line.</summary>
    public static (SolidKind Kind, Vector3 Foot, Vector3 Top, float Radius) Solid(int place, int i)
    {
        var at = PadLights.All[place];
        var up = Vector3.Transform(Vector3.UnitY, at.Frame);
        if (place < LaunchSites.All.Length)
        {
            if (i == 0)
            {
                var foot = at.ToWorld(Scene.TowerOffset);
                return (SolidKind.Tower, foot, foot + up * Scene.TowerHeight, TowerRadius);
            }
            i--;
        }
        var mast = at.Masts[i];
        var mastFoot = at.ToWorld(mast.Foot);
        return (SolidKind.Mast, mastFoot, mastFoot + up * mast.Height, MastRadius);
    }

    private static float[] PlaceReaches()
    {
        var places = PadLights.All;
        var reaches = new float[places.Count];
        for (var p = 0; p < places.Count; p++)
        {
            var r = p < LaunchSites.All.Length ? Scene.TowerOffset.Length() + Scene.TowerHeight : 0f;
            foreach (var mast in places[p].Masts)
                r = MathF.Max(r, mast.Foot.Length() + mast.Height);
            reaches[p] = r;
        }
        return reaches;
    }

    private static float HighestTop()
    {
        var places = PadLights.All;
        var highest = float.MinValue;
        for (var p = 0; p < places.Count; p++)
            highest = MathF.Max(highest, Planet.Altitude(places[p].Origin) + PlaceReach(p) + (places[p].Ship is null ? 0f : Sea.Highest));
        return highest;
    }

    /// <summary>
    /// Whether the chord <paramref name="a"/> to <paramref name="b"/> comes within
    /// <paramref name="reach"/> of the segment <paramref name="foot"/> to <paramref name="top"/>,
    /// and if so where along it (0 to 1, <paramref name="inside"/>) it first does.
    /// </summary>
    public static bool Chord(Vector3 a, Vector3 b, Vector3 foot, Vector3 top, float reach, out float inside)
    {
        inside = 0f;
        var height = Vector3.Distance(foot, top);
        if (Vector3.Distance((a + b) / 2f, (foot + top) / 2f) > Vector3.Distance(a, b) / 2f + height / 2f + reach)
            return false;
        var (along, miss) = Closest(a, b, foot, top);
        if (miss >= reach)
            return false;
        // Where along the chord it first comes within reach: its distance from the segment only
        // falls towards the closest point (a point's distance from a segment is convex along a
        // line), so halving finds it, at any angle of approach: across the segment, slanting, or
        // along it onto its end. Within reach from the chord's start, it began there.
        if (Apart(a, foot, top) < reach)
            return true;
        var outside = 0f;
        inside = along;
        for (var i = 0; i < 16; i++)
        {
            var mid = (outside + inside) / 2f;
            if (Apart(Vector3.Lerp(a, b, mid), foot, top) < reach)
                inside = mid;
            else
                outside = mid;
        }
        return true;
    }

    /// <summary>How far <paramref name="p"/> is from the segment <paramref name="q0"/> to <paramref name="q1"/>.</summary>
    public static float Apart(Vector3 p, Vector3 q0, Vector3 q1)
    {
        var d = q1 - q0;
        var t = Math.Clamp(Vector3.Dot(p - q0, d) / MathF.Max(d.LengthSquared(), 1e-9f), 0f, 1f);
        return Vector3.Distance(p, q0 + d * t);
    }

    /// <summary>
    /// The closest two segments come, <paramref name="p0"/> to <paramref name="p1"/> and
    /// <paramref name="q0"/> to <paramref name="q1"/>: how far along the first (0 to 1), and how far apart.
    /// </summary>
    public static (float Along, float Apart) Closest(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        var d1 = p1 - p0;
        var d2 = q1 - q0;
        var r = p0 - q0;
        var a = d1.LengthSquared();
        var e = d2.LengthSquared();
        var f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-9f)
        {
            s = 0f;
            t = e > 1e-9f ? Math.Clamp(f / e, 0f, 1f) : 0f;
        }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-9f)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denominator = a * e - b * b;
                s = denominator > 1e-9f ? Math.Clamp((b * f - c * e) / denominator, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Math.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }
        return (s, Vector3.Distance(p0 + d1 * s, q0 + d2 * t));
    }
}
