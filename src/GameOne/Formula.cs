using System.Numerics;
using GameOne.Physics;

namespace GameOne;

/// <summary>
/// Where a vessel's orbit under formula, its engine off, started from: its orbit among the stars
/// (as its body's ground's frame was at <paramref name="Since"/>, the world time it starts at), and
/// its attitude then and how it turns among the stars, which nothing changes while it is on that
/// orbit (one that can't be steered: one that can is turned to its aim, see <see cref="Formula"/>).
/// And where it started, in double (<paramref name="At"/>, <paramref name="Velocity"/>), and where
/// the orbit, worked out in floats, has it then (<paramref name="OrbitAt"/>, <paramref name="OrbitVelocity"/>):
/// it is moved by how far the orbit has it from there, so at its start it is where it was to more
/// than a float holds (handed back to the physics, or saved, at once), and the orbit's rounding is
/// never carried on. <paramref name="CrossesAt"/> is the world time it crosses a sphere of
/// influence's edge (<see cref="Patched.NextCrossing"/>, a hair past it, so its place then is over
/// the edge as <see cref="Bodies.Choose(GroundBody, Vector3, double)"/> reads it), infinity for none
/// before <paramref name="LookedTo"/>, the world time it was looked for up to.
/// </summary>
internal readonly record struct FormulaOrbit(Orbit Orbit, double Since, Quaternion Attitude, Vector3 Spin,
    Vector3Double At, Vector3Double Velocity, Vector3 OrbitAt, Vector3 OrbitVelocity,
    double CrossesAt = double.PositiveInfinity, double LookedTo = double.PositiveInfinity);

/// <summary>
/// How the formula is moving a vessel (<see cref="Vessel.Motion"/>): on its orbit by Kepler, its
/// engine off (<see cref="FormulaOrbit"/>), or stepped (<see cref="Stepping"/>) from its state at <see cref="Time"/>.
/// </summary>
internal struct FormulaMotion
{
    /// <summary>Whether it is stepped (in the air, or burning) rather than on its orbit by Kepler above the air.</summary>
    public bool Stepping;

    /// <summary>
    /// Whether it is lifting off from where it stood (see <see cref="Standing"/>): its feet still
    /// within their stroke of the ground at first, which is no new touch, until it is clear of it
    /// or falling back.
    /// </summary>
    public bool Rising;

    /// <summary>The orbit it is on, while it isn't stepped.</summary>
    public FormulaOrbit OnOrbit;

    /// <summary>
    /// The world time it crosses a sphere of influence's edge on its orbit (<see cref="FormulaOrbit.CrossesAt"/>),
    /// for a frame at a formula's rate to be cut at, so it is handed across at that moment;
    /// infinity while it is stepped (its crossing is asked at each window's end, as each step's is).
    /// </summary>
    public readonly double CrossesAt => Stepping ? double.PositiveInfinity : OnOrbit.CrossesAt;

    /// <summary>
    /// The stepped state: its centre of mass and velocity over the ground at world time
    /// <see cref="Time"/>, the last whole step's end; and how fast that step changed its velocity,
    /// which it is moved on by between steps (see <see cref="Formula.Advance"/>). The place and
    /// velocity are in double, as the physics keeps a craft flying free (see
    /// <see cref="RocketPhysics.TrueCentreOfMass"/>): stepped in floats, each step's rounding would
    /// lean the same way lap after lap in the turning frame, so how an orbit creeps would turn on
    /// whether anyone watches it.
    /// </summary>
    public Vector3Double At, Velocity;
    public Vector3 Change;
    public double Time;

    /// <summary>
    /// The stepped state's attitude at <see cref="Time"/> (one that can't be steered: see
    /// <see cref="Formula"/>' Tumble), which each step turns on from, and it is shown turned on
    /// from between steps by its spin, as its place is moved on by its velocity: the vessel's own
    /// attitude is what is shown, so a step can't turn on from that without turning twice.
    /// </summary>
    public Quaternion Attitude;

    /// <summary>The world time it was last moved to.</summary>
    public double Shown;

    /// <summary>
    /// Whether it came down gently with its engine lit, and stands where it touched, burning: not
    /// yet landed (or afloat, <see cref="Ending"/>), as in the physics a vessel lands only with its
    /// engine off; it lands once that goes out, or lifts off again should its thrust beat its weight.
    /// </summary>
    public bool Standing;
    public FlightState Ending;
}

/// <summary>
/// Moves a vessel out of the physics (<see cref="Vessel.OnFormula"/>) by formula rather than by
/// Jolt, as cheaply as can be: a vessel nobody watches needs only to be where it would be, not
/// to be stepped there as finely.
/// </summary>
/// <remarks>
/// <para>Each vessel is moved at its own body (<see cref="Vessel.Body"/>), by that body's ground,
/// air and pull, in its frame. Above the air with its engine off it is on a Kepler orbit (<see cref="Orbit.StateAfter"/>)
/// from the state it last changed at, worked out afresh for the time asked, never integrated, so its
/// orbit costs the same however long it is on it and never drifts. In the air, or burning, it is
/// stepped every <see cref="Scene.FormulaStepSeconds"/> (above the air and clear of the ground,
/// every <see cref="Scene.FormulaBurnStepSeconds"/>: <see cref="StepFor"/>), in double, as the physics falls it (gravity, the
/// turn's pulls and the air's drag: <see cref="GroundBody.FallStep"/>), its thrust
/// (<see cref="Vessel.Throttle"/> × <see cref="Vessel.MaxAcceleration"/>) along its
/// <see cref="Vessel.Aim"/>, which its holds turn as they do in the physics. The fuel burns in each
/// step with its thrust (<see cref="Vessel.BurnByFormula"/>), at the step's mean mass, the step cut
/// where the fuel runs out, so a burn gives the delta-v its numbers say however seldom its rules run (once
/// a window at a formula's rate, but a witnessed burner's every step: see Flight's BurnByFormula). Its attitude follows its aim at once (no turning
/// time: a simplification allowed under formula); one that can't be steered keeps turning as it was.
/// Low over the ground its speed over it is held to <see cref="Scene.GroundTopSpeed"/> each step that
/// starts or would end there, as the physics holds it; so while faster there it is stepped, not on
/// its orbit (which is asked at each flight step's place, and so can't pass over the band unasked).
/// Landed or afloat it doesn't move (each body's physics is in its ground's frame), but aboard a
/// raft, which carries it by its pose at the world's time (<see cref="Vessel.CarryByFormula"/>).</para>
/// <para>Each step is checked for its touch (<see cref="Touch"/>): the service towers and light
/// masts (<see cref="TowersAndMasts"/>, the predictions' own check, so the two can't drift), and its
/// lowest point coming onto what is under it (the ground, the calm sea, a pad's, a zone's or a
/// raft's top). It is judged once, there, by its whole speed against the physics' limits and with
/// their reasons; under them it lands (or floats), standing where it came down upright, lying
/// along the ground otherwise. No bounce, hop or tip: simpler, and allowed under formula.</para>
/// </remarks>
internal static class Formula
{
    /// <summary>
    /// How long <paramref name="v"/>'s next formula step is, where it is now and as it moves: <see cref="Scene.FormulaBurnStepSeconds"/>
    /// where, its engine off, it could be on its orbit (<see cref="OnItsOrbitAt"/>: above its body's air,
    /// clear of its highest ground and not held to the ground's top speed low over it, where nothing
    /// changes fast: no air, nothing to touch or hold it); <see cref="Scene.FormulaStepSeconds"/> otherwise.
    /// </summary>
    internal static float StepFor(Vessel v) => StepAt(v, v.CentreOfMass, v.Velocity);

    private static float StepAt(Vessel v, Vector3 at, Vector3 velocity) =>
        OnItsOrbitAt(v, at, velocity) ? Scene.FormulaBurnStepSeconds : Scene.FormulaStepSeconds;

    /// <summary>
    /// The next step's length from the stepped state <paramref name="m"/> (<see cref="StepAt"/>, at
    /// its place), cut where its fuel runs out burning (no shorter than one of the physics' steps,
    /// for a tank all but dry), so no step's thrust is carried past it.
    /// </summary>
    private static float NextStep(Vessel v, in FormulaMotion m)
    {
        var step = StepAt(v, m.At.ToSingle(), m.Velocity.ToSingle());
        var fuel = v.FuelSecondsFromStep;
        return fuel < step ? MathF.Max((float)fuel, RocketPhysics.StepSeconds) : step;
    }

    /// <summary>
    /// How far on, in seconds of its velocity, a step of <paramref name="seconds"/> reads its pull
    /// and air from where it is (see <see cref="Step"/>): half of how much longer its step is than
    /// the physics'. That is the mean of where the physics' own steps across one of the formula's
    /// (twelve of <see cref="RocketPhysics.StepSeconds"/> in <see cref="Scene.FormulaStepSeconds"/>)
    /// read theirs, the first where it is and each after one of its steps on, so under a pull that
    /// changes along the way (an orbit's, bending) the formula falls as the physics does; under
    /// a steady one, where it is read changes nothing.
    /// </summary>
    private static double Lead(float seconds) => ((double)seconds - RocketPhysics.StepSeconds) / 2;

    /// <summary>
    /// How far apart along a step what is under it is read (metres): the smallest floe's width
    /// (as <see cref="Vessel.FormulaLowest"/> reads a hull), so neither a floe nor a ridge that
    /// narrow is stepped over. (The terrain's finest detail, 6 m across, is centimetres high.)
    /// </summary>
    private const float GroundSampleMetres = 2f * Scene.FloeMinRadius;

    /// <summary>
    /// How far past where it floats a vessel's plunge into the water can take it, as a multiple of
    /// its draft: a booster floating 9.15 m deep plunges 10.8 m from rest and 12.9 m hitting the
    /// water at 14 m/s, the splashdown's limit about (SplashdownTests), so 1.41 times; with room.
    /// In water shallower than that its plunge reaches the seabed (see <see cref="Judge"/>).
    /// </summary>
    private const float PlungeReach = 1.5f;

    /// <summary>
    /// How far past a sphere's edge its crossing on its orbit is taken (seconds): the crossing
    /// search's own tolerance, so the place there is over the edge, never a rounding short of it.
    /// Should <see cref="Bodies.Choose(GroundBody, Vector3, double)"/> still keep it at its body
    /// then, it is asked again once past (see <see cref="Advance"/>), and crosses at a later
    /// window's end: a fallback, a window late at worst.
    /// </summary>
    private const double CrossingMargin = 1e-3;

    /// <summary>
    /// Its orbit round <paramref name="body"/> with its engine off, from this state (in its frame),
    /// at world time <paramref name="now"/> (its velocity and turning over the ground, as the vessel
    /// has them); with its next crossing looked for (<see cref="Crossing"/>) if
    /// <paramref name="crossings"/>: for one that is to fly it, not one only to be stepped.
    /// </summary>
    public static FormulaOrbit Start(GroundBody body, Vector3Double centreOfMass, Vector3Double velocity, Quaternion rotation, Vector3 angularVelocity, double now, bool crossings = true)
    {
        // Among the stars: the orbit takes the stars' velocity, and above the air a craft keeps its
        // attitude among them (see RocketPhysics' Fall), so its turning is kept as it is there.
        var (at, moving) = (centreOfMass.ToSingle(), velocity.ToSingle());
        var stars = body.AmongStars(at, moving);
        var orbit = new Orbit(body, at, stars, findEnd: false);
        var (orbitAt, orbitVelocity) = OnOrbitAfter(orbit, 0);
        var (crossesAt, lookedTo) = crossings ? Crossing(body, at, stars, now) : (double.PositiveInfinity, double.PositiveInfinity);
        return new(orbit, now, rotation, angularVelocity + body.Spin, centreOfMass, velocity, orbitAt, orbitVelocity, crossesAt, lookedTo);
    }

    /// <summary>
    /// When the orbit from <paramref name="at"/> moving <paramref name="stars"/> among its body's stars
    /// at world time <paramref name="now"/> crosses a sphere's edge (<see cref="Patched.NextCrossing"/>,
    /// <see cref="CrossingMargin"/> past it), looked for <see cref="Scene.MapCollisionHorizonSeconds"/>
    /// ahead (the map's own look ahead: an hour, so a long coast is asked again seldom), and to when.
    /// </summary>
    private static (double CrossesAt, double LookedTo) Crossing(GroundBody body, Vector3 at, Vector3 stars, double now)
    {
        var lookedTo = now + Scene.MapCollisionHorizonSeconds;
        return (Patched.NextCrossing(body, at, stars, now, lookedTo) + CrossingMargin, lookedTo);
    }

    /// <summary>
    /// How near its body's centre <paramref name="v"/>'s centre of mass may come on its orbit
    /// (<see cref="OnItsOrbitAt"/>): above the air (a body with air) and its reach clear of the
    /// body's highest ground, its fine detail included, whichever is higher.
    /// </summary>
    internal static double FloorRadius(Vessel v)
    {
        var body = v.Body;
        var clear = body.MaxHeight + v.Reach + Scene.FineDetailMargin;
        return body.SeaRadius + (body.HasAir ? Math.Max(Scene.AtmosphereHeight, clear) : clear);
    }

    /// <summary>
    /// The world time <paramref name="orbit"/> next comes down to <paramref name="floorRadius"/> from
    /// its body's centre (<see cref="Orbit.FallingTo"/>), however long after its start; null if it
    /// never comes that low (its periapsis is above it).
    /// </summary>
    internal static double? ComesDownAt(in FormulaOrbit orbit, double floorRadius) =>
        orbit.Orbit.FallingTo(floorRadius) is { } seconds ? orbit.Since + seconds : null;

    /// <summary>
    /// The world time burning <paramref name="v"/> comes down to its floor (<see cref="FloorRadius"/>:
    /// into the air, or within reach of an airless body's ground), if it does within
    /// <paramref name="horizon"/> seconds of <paramref name="now"/> (its state's time); null if not.
    /// A look ahead, not its flight: from where it is, among the stars as its body's ground is now,
    /// its thrust kept on at its throttle while its fuel lasts (<see cref="Vessel.FuelSeconds"/>), along
    /// its velocity among the stars held prograde, against it retrograde (whatever frame the hold is
    /// in: the target's or the surface's velocity isn't asked), else along its aim as it is (the rings', a
    /// node's), pointed there at once with no turning time, stepped every <see cref="Scene.FormulaBurnStepSeconds"/>
    /// by its body's pull alone (above its air there is nothing else), which is near enough over the
    /// seconds a drop's lead looks ahead.
    /// </summary>
    internal static double? BurnComesDownAt(Vessel v, double now, double horizon)
    {
        var body = v.Body;
        var floor = FloorRadius(v);
        var centre = (Vector3Double)body.Centre;
        var p = v.TrueCentreOfMass - centre;
        var u = v.TrueVelocity + Vector3Double.Cross(body.Spin, p);
        var mass = (double)v.Mass;
        var thrust = (double)(v.Throttle * v.CurrentStage.Thrust);
        var flow = (double)(v.Throttle * v.CurrentStage.FuelFlow);
        var lasts = v.FuelSeconds;
        Vector3Double aim = v.Aim;
        // Out of reach within the horizon even falling straight down at its fall's and thrust's whole pull: none.
        var most = body.Mu / (floor * floor) + thrust / Math.Max(1.0, mass - flow * Math.Min(horizon, lasts));
        var r = p.Length();
        if (r - floor > u.Length() * horizon + most * horizon * horizon / 2)
            return null;

        Vector3Double Pushed(Vector3Double at, Vector3Double moving, double t)
        {
            var d = at.Length();
            var pull = at * (-body.Mu / (d * d * d));
            if (t >= lasts)
                return pull;
            var way = v.Hold switch
            {
                HoldMode.Prograde => Vector3Double.Normalize(moving),
                HoldMode.Retrograde => -Vector3Double.Normalize(moving),
                _ => aim,
            };
            return pull + way * (thrust / (mass - flow * t));
        }

        var step = (double)Scene.FormulaBurnStepSeconds;
        for (var t = 0.0; t < horizon; t += step)
        {
            var h = Math.Min(step, horizon - t);
            var half = u + Pushed(p, u, t) * (h / 2);
            var next = p + half * h;
            var moved = half + Pushed(next, half, t + h) * (h / 2);
            var reached = next.Length();
            if (reached <= floor)
                return now + t + h * Math.Clamp((r - floor) / Math.Max(r - reached, 1e-9), 0.0, 1.0);
            (p, u, r) = (next, moved, reached);
        }
        return null;
    }

    /// <summary>
    /// How the formula starts on a vessel at <paramref name="body"/> leaving the physics at world time
    /// <paramref name="now"/> in this state (in the body's frame): on its orbit if it can (flying above
    /// the air, its engine off), else stepped from here.
    /// </summary>
    public static FormulaMotion Begin(GroundBody body, Vector3Double centreOfMass, Vector3Double velocity, Quaternion rotation, Vector3 angularVelocity, double now, bool onItsOrbit) =>
        new()
        {
            Stepping = !onItsOrbit,
            OnOrbit = Start(body, centreOfMass, velocity, rotation, angularVelocity, now, crossings: onItsOrbit),
            At = centreOfMass, Velocity = velocity, Change = body.Gravity(centreOfMass.ToSingle()), Time = now, Shown = now, Attitude = rotation,
        };

    /// <summary>
    /// Moves <paramref name="v"/> to where the formula has it at world time
    /// <paramref name="worldTime"/>, <paramref name="dt"/> on from the last time it was asked (0
    /// only brings it to that time), judging its touch on the way. Nothing is done for a vessel in
    /// the physics, or for one wrecked; one landed or afloat is only carried by a raft it is aboard.
    /// <paramref name="toTheEnd"/> (a window's end at a formula's rate), a stepped one's last step
    /// is cut short to end at <paramref name="worldTime"/>, so nothing of its burn is owed past it
    /// and the window's rules (a throttle or an aim changed) take effect from there exactly.
    /// </summary>
    public static void Advance(Vessel v, float dt, double worldTime, bool toTheEnd = false)
    {
        if (!v.OnFormula || v.State == FlightState.Crashed)
            return; // a wreck is gone
        ref var m = ref v.Motion;
        // Aboard a raft (landed, afloat or standing on it burning), carried with it.
        if (v.State is FlightState.Landed or FlightState.Floating || m.Standing)
            v.CarryByFormula(worldTime);
        if (v.State != FlightState.Flying)
            return; // landed, afloat (or on the pad), it stays where it is, or where its raft has it
        if (m.Standing)
        {
            Stand(v, ref m, worldTime);
            return;
        }
        if (!m.Stepping)
        {
            // Stepped from the moment it falls to its floor, if that comes by now (into the air, or
            // within reach of the ground): a dip in and out again between two asks, however far
            // apart (a window of minutes at a formula's rate), is flown through, not passed over.
            var since = m.Shown;
            if (!v.EngineLit)
            {
                if (ComesDownAt(m.OnOrbit, FloorRadius(v)) is { } down && down <= worldTime)
                    since = Math.Max(down, m.Shown);
                else
                {
                    var (at, velocity) = Along(m.OnOrbit, worldTime);
                    if (OnItsOrbitAt(v, at.ToSingle(), velocity.ToSingle()))
                    {
                        ShowOnOrbit(v, ref m, at, velocity, worldTime);
                        // Asked again from here: its look ahead about to run out (within a window,
                        // so it always covers the next whole window, and a crossing just past its
                        // end is still cut at: see Flight's NextCut), or its crossing passed with
                        // none made (the body chosen there still this one: a window late, at worst).
                        if (worldTime > m.OnOrbit.CrossesAt || m.OnOrbit.LookedTo - worldTime < Scene.FormulaWindowSeconds)
                        {
                            var place = at.ToSingle();
                            var (crossesAt, lookedTo) = Crossing(v.Body, place, v.Body.AmongStars(place, velocity.ToSingle()), worldTime);
                            m.OnOrbit = m.OnOrbit with { CrossesAt = crossesAt, LookedTo = lookedTo };
                        }
                        return;
                    }
                }
            }
            // Lit, come down to its floor (stepped from that moment), or not on its orbit at the
            // time asked for another reason (held to the ground's top speed low over the ground,
            // which OnItsOrbitAt tells only there): stepped on from when it was last moved to.
            var (from, moving) = Along(m.OnOrbit, since);
            (m.Stepping, m.At, m.Velocity, m.Change, m.Time, m.Attitude) = (true, from, moving, v.Body.Gravity(from.ToSingle()), since, v.FormulaRotation);
        }

        var stepped = false;
        while (true)
        {
            var seconds = NextStep(v, m);
            var last = false;
            if (m.Time + seconds > worldTime + 1e-9)
            {
                if (!toTheEnd || worldTime - m.Time <= 1e-6)
                    break;
                (seconds, last) = ((float)(worldTime - m.Time), true); // the last, short, to end at the window's end
            }
            if (Step(v, ref m, seconds))
                return; // it touched: landed, afloat or wrecked, there
            stepped = true;
            if (last)
                m.Time = worldTime; // there, not a float's rounding short of it

            // Above the air with its engine off (and not about to come down into it), it goes on its orbit from here.
            if (!v.EngineLit && OnItsOrbitAt(v, m.At.ToSingle(), m.Velocity.ToSingle()) && OnItsOrbitAt(v, (m.At + m.Velocity * StepAt(v, m.At.ToSingle(), m.Velocity.ToSingle())).ToSingle(), m.Velocity.ToSingle()))
            {
                (m.Stepping, m.OnOrbit) = (false, Start(v.Body, m.At, m.Velocity, v.FormulaRotation, v.FormulaSpin, m.Time));
                var (at, velocity) = Along(m.OnOrbit, worldTime);
                ShowOnOrbit(v, ref m, at, velocity, worldTime, orbitChanged: true);
                return;
            }
        }
        // Between steps, it is moved on from the last by its velocity, changing as that step's did;
        // and one that can't be steered turned on from the last by its spin, so it is drawn
        // turning smoothly, not a step's turn at a time (a tenth of a second's).
        var r = worldTime - m.Time;
        var rotation = v.Guided ? Follow(v, worldTime - m.Shown) : Turned(m.Attitude, v.FormulaSpin, (float)r);
        var spin = v.Guided ? GuidedSpin(v.Body, m.At.ToSingle()) : v.FormulaSpin;
        Vector3Double change = m.Change;
        v.MoveByFormula(m.At + m.Velocity * r + change * (r * r / 2), m.Velocity + change * r, rotation, spin, orbitChanged: stepped);
        m.Shown = worldTime;
    }

    /// <summary>
    /// One standing where it touched, its engine lit (see <see cref="FormulaMotion.Standing"/>): once
    /// the engine is out, it has landed (or is afloat), its touchdown as it touched; on the ground,
    /// pushing up harder than its weight holds it down, it lifts off, flown on from rest.
    /// </summary>
    private static void Stand(Vessel v, ref FormulaMotion m, double worldTime)
    {
        if (!v.EngineLit)
        {
            v.LandByFormula(v.Position, v.FormulaRotation, v.TouchdownSpeed, m.Ending);
            m.Standing = false;
            return;
        }
        v.BurnByFormula((float)(worldTime - m.Shown)); // burning where it stands, since it was last asked (or since its touch's step's burn ended)
        var at = v.CentreOfMass;
        var up = v.Body.Up(at);
        var weight = -Vector3.Dot(v.Body.Gravity(at) + v.Body.OutwardPull(at), up);
        if (m.Ending == FlightState.Landed && v.Throttle * v.MaxAcceleration * Vector3.Dot(v.Aim, up) > weight)
        {
            (m.Standing, m.Rising, m.Stepping, m.At, m.Velocity, m.Change, m.Time, m.Attitude) = (false, true, true, at, Vector3.Zero, Vector3.Zero, worldTime, v.FormulaRotation);
            v.LiftOffByFormula();
        }
        m.Shown = worldTime;
    }

    /// <summary>
    /// Whether <paramref name="v"/>, its engine off, can be on its orbit with its centre of mass at
    /// <paramref name="at"/>: above its body's air and clear of everything under it (nothing of it
    /// within its reach of its body's highest ground, its fine detail included: <see cref="Scene.FineDetailMargin"/>),
    /// so nothing on the way can be touched. On the
    /// planet the air's top is far over its ground; at a body with no air (the moon) only height
    /// over its ground keeps it clear: lower, it is stepped, and its touch judged.
    /// </summary>
    /// <remarks>
    /// Nor while moving at <paramref name="velocity"/> it is held to the ground's top speed there
    /// (<see cref="Held"/>: low over a body with no air, the moon's), which its orbit knows nothing of.
    /// </remarks>
    internal static bool OnItsOrbitAt(Vessel v, Vector3 at, Vector3 velocity) =>
        Clear(v, at) && !Held(v.Body, at, velocity);

    /// <summary>Whether <paramref name="v"/>'s centre of mass at <paramref name="at"/> is above its body's air and nothing of it within reach of its highest ground, its fine detail included (see <see cref="OnItsOrbitAt"/>).</summary>
    private static bool Clear(Vessel v, Vector3 at) =>
        v.Body.AboveAir(at) && v.Body.Altitude(at) - v.Reach > v.Body.MaxHeight + Scene.FineDetailMargin;

    /// <summary>
    /// Whether something at <paramref name="at"/> moving at <paramref name="velocity"/> (over the
    /// ground) is faster than the ground's top speed low over the ground (<see cref="Scene.GroundTopSpeed"/>,
    /// <see cref="GroundBody.LowOverTheGround"/>), so a step holds it to that, as the physics does.
    /// </summary>
    private static bool Held(GroundBody body, Vector3 at, Vector3Double velocity) =>
        velocity.LengthSquared() > (double)Scene.GroundTopSpeed * Scene.GroundTopSpeed && body.LowOverTheGround(at);

    /// <summary>
    /// Where the formula has <paramref name="v"/>'s centre of mass and how it moves at world time
    /// <paramref name="time"/>, near its last move (within a window), without moving it: on its orbit
    /// by Kepler, stepped moved on from its last step as its moves between steps are (see
    /// <see cref="Advance"/>); landed, afloat, standing or in the physics, where it is now.
    /// </summary>
    internal static (Vector3 At, Vector3 Velocity) StateAt(Vessel v, double time)
    {
        ref var m = ref v.Motion;
        if (!v.OnFormula || v.State != FlightState.Flying || m.Standing)
            return (v.CentreOfMass, v.Velocity);
        if (!m.Stepping)
        {
            var (at, velocity) = Along(m.OnOrbit, time);
            return (at.ToSingle(), velocity.ToSingle());
        }
        var r = time - m.Time;
        Vector3Double change = m.Change;
        return ((m.At + m.Velocity * r + change * (r * r / 2)).ToSingle(), (m.Velocity + change * r).ToSingle());
    }

    /// <summary>Where <paramref name="orbit"/> has its centre of mass and velocity at world time <paramref name="time"/>, in its body's ground's frame then.</summary>
    private static (Vector3Double At, Vector3Double Velocity) Along(in FormulaOrbit orbit, double time)
    {
        var (at, velocity) = OnOrbitAfter(orbit.Orbit, time - orbit.Since);
        return (orbit.At + ((Vector3Double)at - orbit.OrbitAt), orbit.Velocity + ((Vector3Double)velocity - orbit.OrbitVelocity));
    }

    /// <summary>Where <paramref name="orbit"/> has its centre of mass and velocity <paramref name="since"/> seconds on from its start, in its body's ground's frame then, in floats.</summary>
    private static (Vector3 At, Vector3 Velocity) OnOrbitAfter(Orbit orbit, double since)
    {
        var body = orbit.Body;
        var (position, stars) = orbit.StateAfter(since);
        // The orbit's points are among the stars as the ground's frame was at its start: turned
        // back into the ground's frame now, as every position and velocity in the game is.
        var at = body.Turned(position, -since);
        return (at, body.OverGround(at, body.TurnedDirection(stars, -since)));
    }

    /// <summary>Moves <paramref name="v"/> to where its orbit has it at <paramref name="worldTime"/>, turned as it turns there (or to its aim, steered).</summary>
    private static void ShowOnOrbit(Vessel v, ref FormulaMotion m, Vector3Double at, Vector3Double velocity, double worldTime, bool orbitChanged = false)
    {
        Quaternion rotation;
        Vector3 spin;
        if (v.Guided)
            (rotation, spin) = (Follow(v, worldTime - m.Shown), -v.Body.Spin);
        else
        {
            // It turns at its own spin among the stars (the angle wrapped in double, as a long
            // time on its orbit would otherwise lose the float's precision), then back by the ground's turn.
            var orbit = m.OnOrbit;
            var since = worldTime - orbit.Since;
            var attitude = orbit.Attitude;
            var rate = orbit.Spin.Length();
            if (rate > 1e-9f)
                attitude = Quaternion.Normalize(Quaternion.Concatenate(attitude,
                    Quaternion.CreateFromAxisAngle(orbit.Spin / rate, (float)Math.IEEERemainder(rate * since, Math.Tau))));
            rotation = Quaternion.Normalize(Quaternion.Concatenate(attitude, Quaternion.CreateFromAxisAngle(v.Body.NorthPole, v.Body.TurnAngle(-since))));
            spin = v.Body.TurnedDirection(orbit.Spin, -since) - v.Body.Spin;
        }
        v.MoveByFormula(at, velocity, rotation, spin, orbitChanged);
        m.Shown = worldTime;
    }

    /// <summary>
    /// A steered vessel's attitude, <paramref name="seconds"/> on: its nose turned to its aim at once,
    /// as the physics' steering would turn it, given the time. Above the air it is first turned back
    /// by the ground's turn, as a craft there keeps its attitude among the stars (and its aim is
    /// kept there too: see Vessel's Steer), so its roll about its nose goes as the physics' does.
    /// </summary>
    private static Quaternion Follow(Vessel v, double seconds)
    {
        var rotation = v.FormulaRotation;
        if (v.Body.AboveAir(v.CentreOfMass) && seconds != 0)
            rotation = Quaternion.Concatenate(rotation, Quaternion.CreateFromAxisAngle(v.Body.NorthPole, v.Body.TurnAngle(-seconds)));
        return Quaternion.Normalize(Quaternion.Concatenate(rotation, Turn(Vector3.Transform(Vector3.UnitY, rotation), v.Aim)));
    }

    /// <summary>How a steered vessel turns over the ground, held to its aim: still among the stars above the air (turning back at the ground's turn), still over the ground in it.</summary>
    private static Vector3 GuidedSpin(GroundBody body, Vector3 at) => body.AboveAir(at) ? -body.Spin : Vector3.Zero;

    /// <summary>The least turn that takes direction <paramref name="from"/> to <paramref name="to"/> (both of unit length).</summary>
    internal static Quaternion Turn(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 1f - 1e-7f)
            return Quaternion.Identity;
        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-12f) // turned right round: about any axis across it
            axis = Vector3.Cross(from, MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(dot, -1f, 1f)));
    }

    /// <summary>
    /// One step of <paramref name="seconds"/> (see <see cref="NextStep"/>), from the stepped state: its thrust along
    /// its aim, then its fall as the physics falls it (<see cref="GroundBody.FallStep"/>, a kick), then
    /// the drift. The physics kicks and drifts every <see cref="RocketPhysics.StepSeconds"/>, which
    /// puts it ahead of the true path by its acceleration times that step times the time flown, a
    /// half; a coarser kick and drift would put it further ahead still, by its own step. So the
    /// drift is the step's mean velocity plus the physics' own lead: under a steady pull (a burn,
    /// a fall) the two step to the same place, whatever their steps. True if it touched.
    /// </summary>
    /// <remarks>
    /// That drift is a plain kick and drift of the place its <see cref="Lead"/> on by the velocity (the
    /// place plus the lead, moved by the velocity the kick leaves), so the pull and the air are
    /// read there, which is also where the physics' own steps read it on the whole (see
    /// <see cref="Lead"/>): read at the place itself, the kick would hang on the velocity as well
    /// as the place, and an orbit stepped so would gain some 34 m of its size a lap low over the
    /// moon (a plain kick and drift holds it, as the physics' does). Under a steady pull where it
    /// is read changes nothing. In double, as the physics' copy is stepped.
    /// </remarks>
    private static bool Step(Vessel v, ref FormulaMotion m, float seconds)
    {
        var (p, u) = (m.At, m.Velocity);
        var next = u;
        if (v.EngineLit)
        {
            // Pushed at the step's mean mass, for as much of it as the fuel lasts.
            var (thrust, before) = (v.Throttle * v.CurrentStage.Thrust, v.Mass);
            var lasted = v.BurnByFormula(seconds);
            var pushed = thrust / ((before + v.Mass) / 2f) * seconds * lasted;
            next += (Vector3Double)v.Aim * pushed;
            v.PushedByFormula(pushed);
        }
        var read = p + u * Lead(seconds);
        var drag = Scene.DragRate * v.Body.AirDensity(v.Body.Altitude(read.ToSingle()));
        next += v.Body.FallStep(next, v.Body.Pull(read), drag, seconds);
        // Low over the ground, held as the physics holds it (see RocketPhysics' Fall): if either
        // end of the step is, so it is held no later than the physics would (at most a step sooner).
        if (Held(v.Body, p.ToSingle(), next) || Held(v.Body, (p + u * seconds).ToSingle(), next))
            next *= Scene.GroundTopSpeed / next.Length();
        var change = next - u;
        var end = p + (u + next) * (seconds / 2.0) + change * (RocketPhysics.StepSeconds / 2.0);
        if (Touch(v, ref m, seconds, p.ToSingle(), u.ToSingle(), end.ToSingle(), next.ToSingle()))
            return true;
        (m.At, m.Velocity, m.Change, m.Time) = (end, next, (change / seconds).ToSingle(), m.Time + seconds);
        if (!v.Guided)
            Tumble(v, ref m, end.ToSingle(), seconds);
        return false;
    }

    /// <summary>
    /// One that can't be steered turns on as it was, a step's worth from the step before's
    /// attitude (<see cref="FormulaMotion.Attitude"/>): slowed by the damping the physics gives
    /// (<see cref="RocketPhysics.AngularDamping"/>), towards still over the ground in the air, or
    /// among the stars above it (see RocketPhysics' Fall).
    /// </summary>
    private static void Tumble(Vessel v, ref FormulaMotion m, Vector3 at, float seconds)
    {
        var settled = GuidedSpin(v.Body, at);
        var spin = settled + (v.FormulaSpin - settled) * MathF.Exp(-RocketPhysics.AngularDamping * seconds);
        m.Attitude = Turned(m.Attitude, spin, seconds);
        v.TurnByFormula(m.Attitude, spin);
    }

    /// <summary><paramref name="attitude"/> turned on <paramref name="seconds"/> at <paramref name="spin"/> (radians a second, about its axis).</summary>
    private static Quaternion Turned(Quaternion attitude, Vector3 spin, float seconds)
    {
        var rate = spin.Length();
        return rate * seconds > 1e-9f
            ? Quaternion.Normalize(Quaternion.Concatenate(attitude, Quaternion.CreateFromAxisAngle(spin / rate, rate * seconds)))
            : attitude;
    }

    /// <summary>
    /// Whether the step from <paramref name="p0"/> (moving <paramref name="u0"/>) to
    /// <paramref name="p1"/> (moving <paramref name="u1"/>), centre of mass, touches anything:
    /// thick air too fast for a bare payload, a tower or mast, or what is under it; if so, judged
    /// where it first does (<see cref="Judge"/>), and true.
    /// </summary>
    private static bool Touch(Vessel v, ref FormulaMotion m, float seconds, Vector3 p0, Vector3 u0, Vector3 p1, Vector3 u1)
    {
        if (v.BreaksUpIn(p1, u1))
        {
            v.WreckByFormula(v.BreakUpReason, p1);
            return true;
        }
        var rotation = v.Guided ? v.FormulaRotation : m.Attitude; // as of the step's start, not as last shown
        var reach = v.Reach;

        // The towers and masts, only where it is low enough to reach one.
        var (tower, kind) = (2f, SolidKind.Ground);
        var size = Collisions.Size(v);
        var middle = v.MiddleOffset(rotation);
        if (v.Body.HasSites && MathF.Min(v.Body.Altitude(p0 + middle), v.Body.Altitude(p1 + middle)) - size <= TowersAndMasts.Highest)
            (tower, kind) = Solids(v, p0, p1, middle, rotation, size);

        // What is under it, only where it is low enough to touch it (nothing of it lower than its
        // reach below its centre of mass; no ground or top higher than the map's highest). Read
        // along the step at points no further apart than GroundSampleMetres, not at its end alone,
        // so a fast, low one doesn't step over a ridge narrower than its step (at 1,000 m/s a
        // step is 100 m): high over everything, each point is one read (see FormulaLowest).
        var ground = 2f;
        if (MathF.Min(v.Body.Altitude(p0), v.Body.Altitude(p1)) - reach <= v.Body.MaxHeight)
        {
            var samples = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(p0, p1) / GroundSampleMetres));
            var before = float.NaN;
            for (var k = 1; k <= samples; k++)
            {
                var (lowest, _) = v.FormulaLowest(Vector3.Lerp(p0, p1, (float)k / samples), rotation);
                if (lowest <= 0f && !m.Rising)
                {
                    if (k == 1)
                        before = v.FormulaLowest(p0, rotation).Clearance;
                    ground = (k - 1 + (before > 0f ? before / (before - lowest) : 0f)) / samples;
                    break;
                }
                before = lowest;
            }
            // Lifting off its stand: clear of the ground by AirborneClearance under its own power,
            // what it comes down on next is a landing of its own; back down before that (falling
            // again), the same landing (as the physics has it: see Vessel's JudgeContact).
            if (m.Rising && ground > 1f)
            {
                if (before > Vessel.AirborneClearance && v.EngineLit)
                {
                    m.Rising = false;
                    v.FlownClearByFormula();
                }
                else if (Vector3.Dot(u1, v.Body.Up(p1)) <= 0f)
                    m.Rising = false;
            }
        }
        else if (m.Rising)
        {
            m.Rising = false;
            v.FlownClearByFormula();
        }
        if (tower > 1f && ground > 1f)
            return false;

        var f = MathF.Min(tower, ground);
        var at = Vector3.Lerp(p0, p1, f);
        var velocity = Vector3.Lerp(u0, u1, f);
        var burntTo = m.Time + seconds; // the step's fuel was burnt to its end (see Step)
        m.Time += f * seconds;
        if (tower <= ground)
        {
            v.MoveByFormula(at, velocity, rotation, v.FormulaSpin, orbitChanged: false);
            v.WreckByFormula(kind == SolidKind.Tower ? v.TowerReason : Vessel.MastReason, at, explode: !v.IsFairing);
        }
        else
            Judge(v, at, velocity, rotation);
        // Standing there burning, it burns on from where the step's burn ended (see Stand), not
        // from when it was last asked: so nothing between is burnt twice.
        if (m.Standing)
            m.Shown = burntTo;
        return true;
    }

    /// <summary>
    /// The first tower or mast the step from <paramref name="p0"/> to <paramref name="p1"/> (centre
    /// of mass) hits, and how far along it (2 if none): its size-sphere's centre's path coming
    /// within its size and theirs (<see cref="TowersAndMasts.Chord"/>, as the predictions find
    /// them), then its body itself, its axis as wide as its widest (or its feet's reach, its legs
    /// out), coming within their reach of it along the rest of the step, sampled no further apart
    /// than half that, so it can't pass between. (Its size-sphere alone would have one landing on
    /// Home's pad hit the tower beside it.)
    /// </summary>
    private static (float Along, SolidKind Kind) Solids(Vessel v, Vector3 p0, Vector3 p1, Vector3 middle, Quaternion rotation, float size)
    {
        var (m0, m1) = (p0 + middle, p1 + middle);
        var (centre, half) = ((m0 + m1) / 2f, Vector3.Distance(m0, m1) / 2f);
        var (bottom, top) = v.AxisOffsets(rotation);
        var width = v.Width;
        var places = PadLights.All;
        var (best, kind) = (2f, SolidKind.Ground);
        for (var p = 0; p < places.Count; p++)
        {
            if (Vector3.Distance(places[p].Origin, centre) > half + TowersAndMasts.PlaceReach(p) + size + TowersAndMasts.TowerRadius)
                continue; // nowhere near this place: passed over whole
            for (var i = 0; i < TowersAndMasts.Count(p); i++)
            {
                var (what, foot, end, radius) = TowersAndMasts.Solid(p, i);
                if (!TowersAndMasts.Chord(m0, m1, foot, end, size + radius, out var inside) || inside >= best)
                    continue;
                var within = width + radius;
                var samples = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(p0, p1) * (1f - inside) / (within / 2f)));
                for (var j = 0; j <= samples; j++)
                {
                    var along = inside + (1f - inside) * j / samples;
                    if (along >= best)
                        break;
                    var c = Vector3.Lerp(p0, p1, along);
                    if (TowersAndMasts.Closest(c + bottom, c + top, foot, end).Apart < within)
                    {
                        (best, kind) = (along, what);
                        break;
                    }
                }
            }
        }
        return (best, kind);
    }

    /// <summary>
    /// Its touch on what is under it, its centre of mass at <paramref name="at"/> moving
    /// <paramref name="velocity"/>, turned <paramref name="rotation"/>: judged once, by its whole
    /// speed against what it hit's (a raft's deck moves). In the sea (no raft under it), by
    /// <see cref="Scene.SafeSplashdownSpeed"/>; on the ground or a top, within
    /// <see cref="Scene.FormulaUprightDegrees"/> of upright by its feet's limit (its legs out) or its
    /// bell's (in), or by its hull's otherwise; each with the physics' leeway
    /// (<see cref="Scene.TouchdownLimit"/>) and reason. A fairing half is taken out on any touch, as
    /// in the physics. Under the limit it is landed, or afloat: upright, standing where it came
    /// down; otherwise lying along what it came down on. In water too shallow to float it in (as
    /// the physics floats it), the seabed is ground, and the ground's rules hold there.
    /// </summary>
    private static void Judge(Vessel v, Vector3 at, Vector3 velocity, Quaternion rotation)
    {
        var (_, point) = v.FormulaLowest(at, rotation);
        var raft = v.Body.HasSea ? Rafts.Under(point)?.Raft : null;
        var speed = (velocity - (raft?.VelocityAt(point) ?? Vector3.Zero)).Length();
        // In the water: the calm sea over the seabed, no raft between; and how deep it is there.
        var depth = raft is null ? v.Body.GroundAltitude(point) - v.Body.SurfaceAltitude(point) : 0f;
        var sea = depth > 0.01f;
        v.MoveByFormula(at, velocity, rotation, v.FormulaSpin, orbitChanged: false);
        if (v.IsFairing)
        {
            v.WreckByFormula(Vessel.FairingFell(sea, raft), at, explode: false);
            return;
        }
        var up = v.Body.Up(at);
        var nose = Vector3.Transform(Vector3.UnitY, rotation);
        var upright = Vector3.Dot(nose, up) >= MathF.Cos(float.DegreesToRadians(Scene.FormulaUprightDegrees));
        var legs = v.HasLegs && v.LegDeploy >= 1f;
        if (sea)
        {
            // Its first part into the water, by its whole speed (as the physics judges a splashdown).
            if (speed > Scene.SafeSplashdownSpeed)
            {
                v.WreckByFormula(Vessel.HitTheWater(speed), at, explode: false, touchdown: speed);
                return;
            }
            // How deep it floats, as it came in: the physics holds a vessel up by
            // Scene.WaterBuoyancy times its weight wholly under (see RocketPhysics' Float), each
            // slice by how much of it is under, so it floats with a WaterBuoyancy'th of its height
            // (as it lies: its length upright, its width lying down) under the water, its feet (out)
            // hanging lower still. Its plunge takes it further, up to PlungeReach times that.
            var along = MathF.Abs(Vector3.Dot(nose, up));
            var extent = (v.Height + v.CurrentStage.BellHeight) * along + 2f * v.CurrentStage.Radius * MathF.Sqrt(MathF.Max(0f, 1f - along * along));
            var below = MathF.Max(0f, v.Body.Altitude(at + v.AxisOffsets(rotation).Bottom) - v.CurrentStage.BellRadius * MathF.Sqrt(MathF.Max(0f, 1f - along * along))
                - v.Body.Altitude(point));
            if (depth < PlungeReach * (extent / Scene.WaterBuoyancy + below))
            {
                // Its plunge reaching the seabed: it comes onto it at a share of its speed into the
                // water (Scene.FormulaSeabedSpeedShare), judged there by the ground's
                // rules (the seabed is ground), landed there upright or lying as it came down.
                var onSeabed = speed * Scene.FormulaSeabedSpeedShare;
                if (HitsTheGround(v, onSeabed, upright, legs) is { } hit)
                {
                    v.WreckByFormula(hit, at, touchdown: onSeabed);
                    return;
                }
                Land(v, at, rotation, nose, up, onSeabed, FlightState.Landed, standing: upright, legs, seabed: true);
                return;
            }
            Land(v, at, rotation, nose, up, speed, FlightState.Floating, standing: false, legs);
            return;
        }
        if (HitsTheGround(v, speed, upright, legs) is { } reason)
        {
            v.WreckByFormula(reason, at, touchdown: speed);
            return;
        }
        Land(v, at, rotation, nose, up, speed, FlightState.Landed, standing: upright, legs);
    }

    /// <summary>
    /// Whether coming down on the ground (or the seabed) at <paramref name="speed"/> wrecks it, and
    /// why, as the physics tells it (see Vessel's JudgeContact): over the feet's (and the hull's)
    /// limit it hit the ground, however it came down; on its bare bell, upright, gentler still.
    /// </summary>
    private static string? HitsTheGround(Vessel v, float speed, bool upright, bool legs) =>
        speed > Scene.TouchdownLimit(v.Body, Scene.SafeTouchdownSpeed) ? Vessel.HitTheGround(speed, v.Body)
        : upright && !legs && speed > Scene.TouchdownLimit(v.Body, Scene.SafeBellTouchdownSpeed) ? v.OnItsBell(speed)
        : null;

    /// <summary>
    /// Lands it (or floats it) where it touched: <paramref name="standing"/>, turned upright and on
    /// its feet (half-way up their stroke, as the legs give under it) or its bell; otherwise lying
    /// along what it came down on, its hull on it (or, afloat, its axis at the calm sea, half in).
    /// On the <paramref name="seabed"/>, lowered through the water onto it. Its engine still lit, it
    /// is set down so but not yet landed (see <see cref="FormulaMotion.Standing"/>). Either way as of
    /// its touch's time (<see cref="FormulaMotion.Time"/>), by which a raft it is on boards it.
    /// </summary>
    private static void Land(Vessel v, Vector3 at, Quaternion rotation, Vector3 nose, Vector3 up, float speed, FlightState state, bool standing, bool legs, bool seabed = false)
    {
        Vector3 axis;
        if (standing)
            axis = up;
        else
        {
            axis = nose - up * Vector3.Dot(nose, up);
            axis = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : v.Body.East(at);
        }
        var turned = Quaternion.Normalize(Quaternion.Concatenate(rotation, Turn(nose, axis)));
        // Afloat lying down, its lowest point its draft lying down under the calm sea (see Judge).
        var target = state == FlightState.Floating ? -2f * v.CurrentStage.Radius / Scene.WaterBuoyancy : standing && legs ? -v.Legs.Stroke / 2f : 0f;
        // Lowered (or raised) onto it: twice, as over a slope moving it moves what is under it.
        for (var i = 0; i < 2; i++)
            at -= up * (v.FormulaLowest(at, turned, solid: seabed).Clearance - target);
        ref var m = ref v.Motion;
        if (v.EngineLit)
        {
            (m.Standing, m.Ending, m.Stepping) = (true, state, false);
            v.StandByFormula(v.OriginFor(at, turned), turned, speed);
        }
        else
            v.LandByFormula(v.OriginFor(at, turned), turned, speed, state);
    }
}
