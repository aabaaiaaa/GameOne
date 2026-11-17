using System.Numerics;
using GameOne.Physics;

namespace GameOne;

/// <summary>
/// One thing flying: the whole rocket on the pad, or a part of it that has come away. Each has its
/// own fuel, throttle, steering, legs, orbit and fate (landed or crashed), and is judged by the same
/// rules: coming down too fast (on its legs, or more gently still on its bare engine bell), leaning
/// too far, or onto the service tower all destroy it; in the sea, it floats, unless any part of it
/// hits the water too fast. A part whose stages carry no guidance (<see cref="Guided"/>) can't be flown: it just
/// falls, as debris. The physics is in <see cref="RocketPhysics"/>; this decides what to ask of it.
/// </summary>
/// <remarks>
/// <see cref="Flight"/> sends the pilot's input to the vessel being flown. The others carry on with
/// whatever they were last given (their throttle, where they were pointed, prograde hold, legs).
/// </remarks>
internal sealed class Vessel
{
    private const float TouchingClearance = 0.12f; // metres: this close to the ground counts as touching it
    internal const float AirborneClearance = 1.5f; // and this far above it as having left it
    private const float StillSpeed = 0.4f;         // m/s against the ground: slower is standing still (to land), faster has moved it (once landed)
    internal const float ImpactSpeed = 15f;         // m/s into the ground or a raft where it already touches it: a hit, not a touch (see JudgeContact)
    private const int BellRimPoints = 8;           // points round the bell's rim its lowest is found from, without legs

    private readonly Worlds _worlds;               // every body's physics, its own body's among them
    private RocketPhysics? _physics;               // its body's, while it is in the physics (and a wreck's, while its hit plays out)
    private readonly Mission _mission;

    /// <summary>
    /// The body it is at: everything it exposes (its place, velocity, attitude, heights) is in that
    /// body's frame, every read of the ground, the sea, the air and the sites it makes is of that
    /// body, and in the physics it is in that body's (<see cref="Physics"/>).
    /// </summary>
    public GroundBody Body { get; private set; }

    /// <summary>Its body's physics while it is in it; none under formula (<see cref="OnFormula"/>), nor once its body is taken out (a wreck's, its hit played out).</summary>
    public RocketPhysics? Physics => OnFormula || _craft.Removed ? null : _physics;

    private readonly List<SoundEvent> _sounds;     // the flight's, shared by all its vessels
    private readonly float[] _fuel;                // kg left in each of the vehicle's stages (only its own count)
    private Craft _craft;
    private Vector3 _aim = Vector3.UnitY;          // where the steering points the nose
    private bool _legsWanted, _wasTouching = true, _hullWasTouching, _hasBeenAirborne; // its feet or bell on the ground, and its body
    private bool _bayDoorsWanted;                  // its bay's doors wanted open (see BayDoorsWanted)
    private float _settledFor, _floatingFor, _ignitionDelay;
    private bool _touchedDown;                     // its landing has begun: touched down since it last flew clear of the ground under its own power, or settled (see JudgeTouch)
    private float? _vesselTouch;                   // and the hardest it touched another vessel in it, if it has
    private bool _lowSounded, _hullSounded;        // and whether its touchdown sound has been made for its lowest points (feet and bell) coming down, and its body's (see JudgeContact)
    private int _wetParts;                         // which of the points it touches the water at are under it (see TouchTheWater), a bit each
    private Vector3 _lastVelocity, _lastSpin;
    private bool _wasFrozen; // frozen as of the last step: see AfterPhysics
    private Vector3 _ground;                       // how fast the ground under it moved at its centre of mass, last time it was measured (see CheckContact)
    private Matrix4x4 _model = Matrix4x4.Identity; // where it was after the last physics step (kept after it is destroyed), or where the formula has it
    private Vector3 _centreOfMass;
    // Under formula (see OnFormula) its body is gone, so its attitude and turning are kept here, as
    // is where its centre of mass is in its own space (what places its origin from that).
    private Quaternion _rotation = Quaternion.Identity;
    private Vector3 _angularVelocity, _centreOffset;
    private Vector3? _burnAim;                         // the next node's burn once it has begun, among the stars (as the ground was then)
    private Vector3 _burned;                           // and how much of it the engine has given since, the same way
    private double _burnStart;                         // the world time it began: how far the ground has turned from that since
    private bool _wasLit;
    private List<PathLeg>? _toNode;                    // the patched path from the anchor to the next node, kept for Planned (made with its first node)
    private (double Now, ManoeuvreChain Chain, GroundBody Body, Vector3 Burn)? _planned; // Planned's last answer, and what it was asked
    private List<PathLeg>? _afterNode;                 // the planned path on from the next node's burn, kept for Late (made once past its time)
    private (double Now, ManoeuvreChain Chain, double At, double Until)? _afterNodeFor; // what _afterNode was worked out for, and the stretch of it looked along
    private double _at;                                // the world time its state was last brought to (AfterPhysics, a crossing)

    /// <summary>A new vessel, <paramref name="craft"/> in <paramref name="physics"/> (its body's, one of <paramref name="worlds"/>).</summary>
    internal Vessel(Worlds worlds, RocketPhysics physics, Mission mission, List<SoundEvent> sounds, Craft craft, float[] fuel, int id, int flight = 1)
        : this(worlds, physics.Body, physics, mission, sounds, craft, fuel, id, flight)
    {
    }

    private Vessel(Worlds worlds, GroundBody body, RocketPhysics? physics, Mission mission, List<SoundEvent> sounds, Craft craft, float[] fuel, int id, int flight)
    {
        (_worlds, Body, _physics, _mission, _sounds, _craft, _fuel, Id, Flight) = (worlds, body, physics, mission, sounds, craft, fuel, id, flight);
        Refresh();
        _aim = Up;
        _lastVelocity = Velocity;
        _lastSpin = craft.Removed ? Vector3.Zero : physics!.AngularVelocity(craft); // a stand-in (see the restoring constructor) has no body
        (Apoapsis, Periapsis) = Body.Orbit(CentreOfMass, Body.AmongStars(CentreOfMass, Velocity));
        _autoFrame = Frames.Auto(Body, Frame.Surface, Periapsis, onTheGround: false); // the orbit as it is, with no history
        Measure(); // its heights and tilt from the start, before its first step
    }

    /// <summary>
    /// Brings a vessel back exactly as it was when <paramref name="state"/> was captured (see
    /// <see cref="Capture"/>), at its body (<see cref="VesselState.Body"/>), in that body's physics
    /// (made if it has none: <see cref="Worlds.Need"/>); one that had landed or was floating comes
    /// back frozen (see <see cref="RocketPhysics.Freeze"/>: afloat, it wakes as the vessel being
    /// flown comes near). With <paramref name="onFormulaFrom"/>, it comes back out of the physics
    /// instead, moved by formula from that world time as one that had just left it would be (see
    /// <see cref="LeavePhysics"/>), with no body made for it (nor a physics for its body): for one
    /// that would leave the physics in the world's first step anyway (see <see cref="Unwatched.MayLoadOnFormula"/>).
    /// <paramref name="now"/> is the world's time it comes back at.
    /// </summary>
    internal Vessel(Worlds worlds, List<SoundEvent> sounds, VesselState state, double now, double? onFormulaFrom = null)
        : this(worlds, Bodies.Named(state.Body), PhysicsFor(worlds, state, onFormulaFrom), state.Mission, sounds,
            onFormulaFrom is null
                ? PhysicsFor(worlds, state, onFormulaFrom)!.Restore(state.Vehicle, state.First, state.Last, state.Position, state.Rotation, state.Velocity,
                    state.AngularVelocity, state.LegDeploy, state.FairingOn, frozen: state.State is FlightState.Landed or FlightState.Floating, state.BayDoors, state.BayDoorStops)
                : RocketPhysics.StandIn(state.Vehicle, state.First, state.Last, state.LegDeploy, state.FairingOn, state.BayDoors, state.BayDoorStops),
            (float[])state.Fuel.Clone(), state.Id, state.Flight)
    {
        State = state.State;
        Site = LaunchSites.Named(state.Site);
        _at = now;
        // The rule by state (see AutoFrame): landed or afloat is the surface frame. No path reaches
        // this with an orbit-like periapsis today (a vessel restored so is frozen, reading no
        // velocity), but the rule is by its state, not by its orbit falling.
        if (State is FlightState.Landed or FlightState.Floating)
            _autoFrame = Frame.Surface; // and it lifts off again from there
        Throttle = state.Throttle;
        _aim = state.Aim;
        ProgradeHold = state.ProgradeHold;
        RetrogradeHold = state.RetrogradeHold;
        _legsWanted = state.LegsWanted;
        LegDeploy = state.LegDeploy;
        _bayDoorsWanted = state.BayDoorsWanted;
        BayDoors = state.BayDoors;
        MaxAltitude = state.MaxAltitude;
        MaxGround = state.MaxGround;
        AchievedOrbit = state.AchievedOrbit;
        LandedOnTarget = state.LandedOnTarget;
        TouchdownSpeed = state.TouchdownSpeed;
        _touchedDown = state.TouchedDown;
        _vesselTouch = state.VesselTouch;
        _ignitionDelay = state.IgnitionDelay;
        _wasTouching = state.WasTouching;
        _hullWasTouching = state.HullWasTouching;
        _hasBeenAirborne = state.HasBeenAirborne;
        _settledFor = state.SettledFor;
        _floatingFor = state.FloatingFor;
        _wetParts = state.WetParts;
        _ground = state.Ground;
        Target = state.Target;
        View = state.View;
        Chain = state.Chain;
        _burnAim = state.BurnAim;
        _burned = state.Burned;
        _burnStart = state.BurnStart;
        if (onFormulaFrom is { } from)
            StartOnFormula(state, from);
        Measure(); // its altitude and tilt, as they were
        _craft.Settling = State == FlightState.Flying && InWater; // saved still settling in the water: kept awake, as it was
        _wasLit = EngineLit;
    }

    /// <summary>The physics <paramref name="state"/> is restored into (its body's, made if need be), or none for one restored under formula.</summary>
    private static RocketPhysics? PhysicsFor(Worlds worlds, VesselState state, double? onFormulaFrom) =>
        onFormulaFrom is null ? worlds.Need(Bodies.Named(state.Body)) : null;

    /// <summary>
    /// Puts one restored with no body (see the restoring constructor) under formula from world
    /// time <paramref name="now"/>, as <see cref="LeavePhysics"/> would have it leave the physics
    /// as saved: where <paramref name="state"/> has it, moving and turning so (landed or afloat,
    /// still, as one restored frozen is), its legs as they rest under it.
    /// </summary>
    private void StartOnFormula(VesselState state, double now)
    {
        var still = State is FlightState.Landed or FlightState.Floating;
        _model = Matrix4x4.CreateFromQuaternion(state.Rotation);
        _model.Translation = state.Position;
        // Its centre of mass where a body would have it (RocketGeometry's, as the physics gives
        // its bodies), whatever was saved: a world written by hand may say otherwise.
        _centreOffset = RocketGeometry.CentreOfMassOffset(Vehicle, Stage, TopStage);
        (_centreOfMass, _rotation) = (state.Position + Vector3.Transform(_centreOffset, state.Rotation), state.Rotation);
        (Velocity, _angularVelocity) = still ? (Vector3.Zero, Vector3.Zero) : (state.Velocity, state.AngularVelocity);
        (_lastVelocity, _lastSpin) = (Velocity, _angularVelocity);
        _formulaLegs = still && _craft.LegsDeployed ? RocketPhysics.RestingLegs(Body, Legs, _model).Extension : Vector4.One;
        OnFormula = true;
        var onItsOrbit = State == FlightState.Flying && Formula.OnItsOrbitAt(this, _centreOfMass) && !EngineLit;
        _motion = Formula.Begin(Body, _centreOfMass, Velocity, _rotation, _angularVelocity, now, onItsOrbit);
        (Apoapsis, Periapsis) = Body.Orbit(_centreOfMass, Body.AmongStars(_centreOfMass, Velocity));
        _autoFrame = Frames.Auto(Body, _autoFrame, Periapsis, still);
    }

    /// <summary>
    /// Everything needed to bring it back as it is now (see <see cref="VesselState"/>), in
    /// <paramref name="world"/>: the vessels it is saved with, where the one it is still parting
    /// from is found (it is saved by its number).
    /// </summary>
    internal VesselState Capture(IReadOnlyList<Vessel> world) => new()
    {
        Body = Body.Name,
        Site = Site.Name,
        PartingFrom = _craft.PartingFrom is { } from ? world.FirstOrDefault(v => v._craft == from && v.State != FlightState.Crashed)?.Id : null,
        Ground = _ground,
        Id = Id,
        Flight = Flight,
        Mission = _mission,
        Vehicle = Vehicle,
        First = Stage,
        Last = TopStage,
        Fuel = (float[])_fuel.Clone(),
        State = State,
        Position = Position,
        // Measured, in its own space: what its orbit is read from (the store's "in orbit"), wherever it is moved to.
        CentreOfMassOffset = Vector3.Transform(CentreOfMass - Position, Quaternion.Conjugate(Rotation)),
        Rotation = Rotation,
        Velocity = Velocity,
        AngularVelocity = AngularVelocity,
        Throttle = Throttle,
        Aim = _aim,
        ProgradeHold = ProgradeHold,
        RetrogradeHold = RetrogradeHold,
        LegsWanted = _legsWanted,
        LegDeploy = LegDeploy,
        BayDoorsWanted = _bayDoorsWanted,
        BayDoors = BayDoors,
        BayDoorStops = HasBay && _craft.DoorStops != new Vector2(RocketGeometry.DoorOpenRadians) ? _craft.DoorStops : null, // none: the full swing
        FairingOn = FairingOn,
        MaxAltitude = MaxAltitude,
        MaxGround = MaxGround,
        AchievedOrbit = AchievedOrbit,
        LandedOnTarget = LandedOnTarget,
        TouchdownSpeed = TouchdownSpeed,
        TouchedDown = _touchedDown,
        VesselTouch = _vesselTouch,
        IgnitionDelay = _ignitionDelay,
        WasTouching = _wasTouching,
        HullWasTouching = _hullWasTouching,
        HasBeenAirborne = _hasBeenAirborne,
        SettledFor = _settledFor,
        FloatingFor = _floatingFor,
        WetParts = _wetParts,
        Target = Target,
        View = View,
        Chain = Chain,
        BurnAim = _burnAim,
        Burned = _burned,
        BurnStart = _burnStart,
    };

    /// <summary>Which vessel this is in its world: each one gets the next number, and keeps it.</summary>
    public int Id { get; }

    /// <summary>Which of its world's flights it came from (1 is the first).</summary>
    public int Flight { get; }

    /// <summary>The mission it was flown on.</summary>
    public Mission Mission => _mission;

    /// <summary>The launch site its run was launched from (the parts that come off it, its own): kept after the run ends, so it can be started again there.</summary>
    public LaunchSite Site { get; internal init; } = LaunchSites.Home;

    /// <summary>
    /// The physics body it is. Under formula (<see cref="OnFormula"/>) that is the body it was when
    /// it left, taken out of the world (<see cref="Craft.Removed"/>), kept as a stand-in so what is
    /// read of it (its stages, its size) still holds; nothing of Jolt's may be asked of it.
    /// </summary>
    public Craft Craft => _craft;

    /// <summary>
    /// Whether it is out of the physics, moved by formula instead (<see cref="Formula.Advance"/>):
    /// nobody is watching it, so it needs no body (see <see cref="LeavePhysics"/> and <see cref="ReturnToPhysics"/>).
    /// </summary>
    public bool OnFormula { get; private set; }

    /// <summary>How the formula is moving it (see <see cref="Formula"/>), from where it left the physics: by Kepler, or stepped.</summary>
    internal ref FormulaMotion Motion => ref _motion;
    private FormulaMotion _motion;

    /// <summary>The orbit the formula moves it on, while it is on its orbit with its engine off (see <see cref="Formula"/>).</summary>
    internal FormulaOrbit FormulaOrbit => _motion.OnOrbit;

    /// <summary>
    /// Whether it came down under formula on a raft (the drone ship's deck, a floe: landed, or
    /// standing there burning), which moves under it: the formula can't carry it there, so it is
    /// to be in the physics, at once (see <see cref="Unwatched"/>).
    /// </summary>
    internal bool AboardByFormula { get; private set; }

    // Whether the formula has handed it back to the physics where it is (see HandBackByFormula).
    private bool _handedBack;

    /// <summary>
    /// Whether, under formula, it is to be in the physics at once, whoever is near: aboard a raft
    /// (<see cref="AboardByFormula"/>), or handed back (<see cref="HandBackByFormula"/>).
    /// </summary>
    internal bool NeedsPhysics => OnFormula && (AboardByFormula || _handedBack);

    /// <summary>
    /// Hands it back to the physics, under formula, where the formula has it now: for what comes
    /// next, the formula can't give the physics' outcome (see <see cref="Formula"/>' Judge). It
    /// comes back that step, outside the hand-ins' budget (see <see cref="Unwatched"/>).
    /// </summary>
    internal void HandBackByFormula() => _handedBack = true;

    /// <summary>
    /// Whether, handed back by the formula (see <see cref="HandBackByFormula"/>), it is still
    /// playing out in the physics what the formula couldn't (flying yet: not landed, afloat or
    /// wrecked), so it stays there meanwhile: the waves can lift its lowest part out of the water
    /// for a moment, when nothing else (touching, settling in the water) would keep it there. Over
    /// once the physics has it landed, afloat or wrecked (see <see cref="AfterPhysics"/>), so one
    /// flying off again later isn't held in by it.
    /// </summary>
    internal bool PlayingOut => !OnFormula && _playingOut && State == FlightState.Flying;
    private bool _playingOut;

    // How far out along its stroke each leg is, drawn under formula: hanging free in flight, and as
    // it stands once down (the stand-in's is as it was when it left, which its legs swinging out
    // or its landing since would belie).
    private Vector4 _formulaLegs = Vector4.One;

    /// <summary>Its attitude, and how fast it turns about which axis (radians per second): its body's, or the formula's.</summary>
    internal Quaternion Rotation => OnFormula ? _rotation : _physics!.Rotation(_craft);
    private Vector3 AngularVelocity => OnFormula ? _angularVelocity : _physics!.AngularVelocity(_craft);

    /// <summary>Its attitude and turning under formula (see <see cref="Formula"/>).</summary>
    internal Quaternion FormulaRotation => _rotation;
    internal Vector3 FormulaSpin => _angularVelocity;

    /// <summary>
    /// Takes it out of the physics, to be moved by formula from world time <paramref name="now"/>:
    /// its body is taken out of the world (so it costs Jolt nothing), and it keeps where it is, its
    /// velocity, attitude and turning, which the formula carries on from.
    /// </summary>
    internal void LeavePhysics(double now)
    {
        if (OnFormula || _craft.Removed || State == FlightState.Crashed)
            return; // a wreck's body plays its hit out in the physics (see RocketPhysics.Wreck)
        Refresh();
        (_rotation, _angularVelocity) = (_physics!.Rotation(_craft), _physics!.AngularVelocity(_craft));
        _centreOffset = Vector3.Transform(_centreOfMass - Position, Quaternion.Conjugate(_rotation));
        _formulaLegs = State is FlightState.Landed or FlightState.Floating ? _craft.LegExtension : Vector4.One;
        _physics!.Remove(_craft);
        _physics = null; // its body's world may be released, and reused for another, while it is out of it
        OnFormula = true;
        (AboardByFormula, _handedBack, _playingOut) = (false, false, false);
        // On its orbit above the air and clear of the ground, its engine off; stepped otherwise (see Formula).
        var onItsOrbit = State == FlightState.Flying && Formula.OnItsOrbitAt(this, _centreOfMass) && !EngineLit;
        _motion = Formula.Begin(Body, _centreOfMass, Velocity, _rotation, _angularVelocity, now, onItsOrbit);
        // The same all along its orbit, so worked out once here, not each step (see AfterPhysics).
        (Apoapsis, Periapsis) = Body.Orbit(_centreOfMass, Body.AmongStars(_centreOfMass, Velocity));
    }

    /// <summary>
    /// Brings it back into the physics at world time <paramref name="now"/>: a new body in its
    /// body's physics (made if it has none, <see cref="Worlds.Need"/>) where the
    /// formula has it then, moving and turning as the formula has it, so nothing jumps. Landed or
    /// afloat it comes back frozen, as one loaded so does (see <see cref="RocketPhysics.Restore"/>).
    /// A wreck never comes back: it is gone.
    /// </summary>
    internal void ReturnToPhysics(double now)
    {
        if (!OnFormula || State == FlightState.Crashed)
            return;
        Formula.Advance(this, 0f, now);
        if (State == FlightState.Crashed)
            return; // brought to now, it touched something on the way, and is gone
        _physics = _worlds.Need(Body);
        _craft = _physics.Restore(Vehicle, Stage, TopStage, Position, _rotation, Velocity, _angularVelocity, LegDeploy, FairingOn,
            frozen: State is FlightState.Landed or FlightState.Floating, BayDoors, _craft.DoorStops);
        OnFormula = false;
        (_playingOut, AboardByFormula, _handedBack) = (_handedBack, false, false);
        Refresh();
        // Its touches are judged from how it moves now, as one just woken is (see AfterPhysics).
        (_lastVelocity, _lastSpin) = (Velocity, _angularVelocity);
    }

    /// <summary>
    /// Hands it across a sphere of influence's edge to <paramref name="body"/>, at world time
    /// <paramref name="now"/> (the moment its motion is at: its world's, in the physics), by
    /// <paramref name="change"/> (from its body's frame to <paramref name="body"/>'s then): its
    /// place, velocity, attitude and turning, its aim and its burn begun (<see cref="BurnLeft"/>,
    /// among the stars as the ground was as it began), all changed into the new frame, so nothing
    /// jumps. In the physics, its craft is taken out of its body's world and into the new body's
    /// (<see cref="RocketPhysics.TakeOut"/>, <see cref="RocketPhysics.TakeIn"/>: made ahead of need
    /// as a rule, else made now); under formula it carries on under formula there, from here. A
    /// wreck never crosses.
    /// </summary>
    internal void CrossTo(GroundBody body, in FrameChange change, double now)
    {
        if (State == FlightState.Crashed || body == Body)
            return;
        var from = Body;
        var centre = (Vector3Double)_centreOfMass;
        _lastVelocity = change.Velocity(centre, (Vector3Double)_lastVelocity).ToSingle();
        _lastSpin = change.Spin((Vector3Double)_lastSpin).ToSingle();
        _ground = change.Direction((Vector3Double)_ground).ToSingle();
        if (OnFormula)
        {
            Body = body;
            var at = change.Point(centre).ToSingle();
            var velocity = change.Velocity(centre, (Vector3Double)Velocity).ToSingle();
            var (rotation, spin) = (change.Rotation(_rotation), change.Spin((Vector3Double)_angularVelocity).ToSingle());
            MoveByFormula(at, velocity, rotation, spin, orbitChanged: true);
            // Begun again from here, at the new body: on its orbit there if it can be, else stepped.
            var held = _motion.Held;
            var onItsOrbit = State == FlightState.Flying && Formula.OnItsOrbitAt(this, at) && !EngineLit;
            _motion = Formula.Begin(body, at, velocity, rotation, spin, now, onItsOrbit);
            _motion.Held = held;
        }
        else if (!_craft.Removed)
        {
            var taken = _physics!.TakeOut(_craft);
            Body = body;
            _physics = _worlds.Need(body); // made ahead of need as a rule (see Flight), so found
            _craft = _physics.TakeIn(taken.Changed(change));
            Refresh();
            (Apoapsis, Periapsis) = Body.Orbit(CentreOfMass, Body.AmongStars(CentreOfMass, Velocity));
        }
        else
            Body = body;
        _aim = Vector3.Normalize(change.Direction((Vector3Double)_aim).ToSingle());
        if (_burnAim is { } burnAim)
        {
            var then = Bodies.Change(from, body, _burnStart);
            (_burnAim, _burned) = (then.Direction((Vector3Double)burnAim).ToSingle(), then.Direction((Vector3Double)_burned).ToSingle());
        }
        _keptHull = null; // measured in the frame it left
        _autoFrame = Frames.Auto(Body, _autoFrame, Periapsis, onTheGround: false);
        _at = now;
        // Its heights and tilt over its new body's ground (far from it at a sphere's edge, as a rule).
        if (Body.Altitude(_centreOfMass) - Reach > Body.MaxHeight + 4f * Reach + AirborneClearance)
            MeasureFar();
        else
            Measure();
        // Its best altitude is over the body it is at: begun again from here.
        MaxAltitude = Altitude;
    }

    /// <summary>
    /// Where the formula has it now (see <see cref="Formula.Advance"/>): its centre of mass, its
    /// velocity over the ground, its attitude and turning; <paramref name="orbitChanged"/> if its
    /// orbit is no longer the one it was last measured on (it has been stepped).
    /// </summary>
    internal void MoveByFormula(Vector3 centreOfMass, Vector3 velocity, Quaternion rotation, Vector3 angularVelocity, bool orbitChanged)
    {
        (_centreOfMass, Velocity, _rotation, _angularVelocity) = (centreOfMass, velocity, rotation, angularVelocity);
        _model = Matrix4x4.CreateFromQuaternion(rotation);
        _model.Translation = OriginFor(centreOfMass, rotation);
        if (orbitChanged)
            (Apoapsis, Periapsis) = Body.Orbit(_centreOfMass, Body.AmongStars(_centreOfMass, Velocity));
        // Its heights and tilt, for the HUD, the map and its best altitude. High up, nothing is
        // near it but the ground far below, so a few lookups do (see MeasureFar); nearer, the
        // heights its touchdown would be measured by (see Measure).
        if (Body.Altitude(centreOfMass) - Reach > Body.MaxHeight + 4f * Reach + AirborneClearance)
            MeasureFar();
        else
            Measure();
    }

    /// <summary>Turns it under formula, where it is (an unsteered one tumbling on: see <see cref="Formula"/>).</summary>
    internal void TurnByFormula(Quaternion rotation, Vector3 angularVelocity) => (_rotation, _angularVelocity) = (rotation, angularVelocity);

    /// <summary>Where its origin is with its centre of mass at <paramref name="centreOfMass"/>, turned <paramref name="rotation"/>.</summary>
    internal Vector3 OriginFor(Vector3 centreOfMass, Quaternion rotation) => centreOfMass - Vector3.Transform(_centreOffset, rotation);

    /// <summary>From its centre of mass to the middle of its size-sphere (<see cref="Collisions.Size"/>), turned <paramref name="rotation"/>.</summary>
    internal Vector3 MiddleOffset(Quaternion rotation) => Vector3.Transform(new Vector3(0f, Height / 2f, 0f) - _centreOffset, rotation);

    /// <summary>From its centre of mass to the two ends of its axis (its bell's bottom, and its top), turned <paramref name="rotation"/>.</summary>
    internal (Vector3 Bottom, Vector3 Top) AxisOffsets(Quaternion rotation) =>
        (Vector3.Transform(new Vector3(0f, -CurrentStage.BellHeight, 0f) - _centreOffset, rotation),
         Vector3.Transform(new Vector3(0f, Height, 0f) - _centreOffset, rotation));

    /// <summary>How far out from its axis anything of it reaches: its widest (its fairing's, while on; its ramps', with its doors open), or its feet, its legs out.</summary>
    internal float Width
    {
        get
        {
            var widest = MathF.Max(_craft.WidestOpen, FairingOn ? Fairing.Radius : 0f);
            if (!HasLegs || LegDeploy <= 0f)
                return widest;
            var foot = RocketGeometry.Foot(Legs, 0);
            return MathF.Max(widest, MathF.Sqrt(foot.X * foot.X + foot.Z * foot.Z));
        }
    }

    /// <summary>
    /// Under formula, how far its lowest point is above what is under it (the ground, the calm sea,
    /// a pad's, a zone's or a raft's top: <see cref="Planet.SurfaceAltitude"/>), with its centre of
    /// mass at <paramref name="centreOfMass"/> turned <paramref name="rotation"/>, and where that
    /// point is: its feet hanging at full stroke (its legs out: as <see cref="Measure"/> takes them,
    /// the first to touch the ground is the touchdown), its bell's rim's lowest point, and its hull's
    /// lowest down its length (so one coming down on its side is found by its side): leaning more
    /// than a little, at points no further apart than the smallest floe is wide, so one lying over
    /// a floe, its ends out over the sea, finds the floe (as <see cref="HullMayTouch"/>'s spans do);
    /// near upright, at its two ends only, the rest being higher. The few points, not the
    /// physics' many: the formula judges one touch, by its whole speed. Far from anything (its
    /// centre of mass more than four times its reach up, as <see cref="HullMayTouch"/> takes the
    /// ground's steepest rise), it is that clear, read from that one point.
    /// </summary>
    internal (float Clearance, Vector3 At) FormulaLowest(Vector3 centreOfMass, Quaternion rotation)
    {
        var clear = Body.SurfaceAltitude(centreOfMass);
        if (clear > 4f * Reach)
            return (clear - Reach, centreOfMass);
        var model = Matrix4x4.CreateFromQuaternion(rotation);
        model.Translation = OriginFor(centreOfMass, rotation);
        var axis = Vector3.Transform(Vector3.UnitY, rotation);
        var down = -Body.Up(centreOfMass);
        var across = down - axis * Vector3.Dot(down, axis);
        across = across.LengthSquared() > 1e-8f ? Vector3.Normalize(across) : Vector3.Zero; // upright: every point round a ring is as low
        var (lowest, at) = (float.MaxValue, centreOfMass);
        void Try(Vector3 point, float below)
        {
            var clearance = Body.SurfaceAltitude(point) - below;
            if (clearance < lowest)
                (lowest, at) = (clearance, point);
        }
        if (HasLegs && LegDeploy >= 1f)
            for (var i = 0; i < RocketGeometry.LegCount; i++)
                Try(Vector3.Transform(RocketGeometry.Foot(Legs, i), model), Legs.Stroke);
        var stage = CurrentStage;
        Try(Vector3.Transform(new Vector3(0f, -stage.BellHeight, 0f), model) + across * stage.BellRadius, 0f);
        var height = Height;
        var spans = Vector3.Dot(axis, -down) > MathF.Cos(float.DegreesToRadians(Scene.FormulaUprightDegrees))
            ? 1 : Math.Max(1, (int)MathF.Ceiling(height / (2f * Scene.FloeMinRadius)));
        for (var i = 0; i <= spans; i++)
        {
            var y = MathF.Min(height * i / spans, height - 1e-3f);
            Try(Vector3.Transform(new Vector3(0f, y, 0f), model) + across * RadiusAt(y), 0f);
        }
        return (lowest, at);
    }

    /// <summary>
    /// Its stages to draw, under formula, added to <paramref name="parts"/>: one a stage, from its
    /// own <see cref="Model"/>, as the physics draws one in it (see <see cref="RocketPhysics.GetParts"/>),
    /// its legs as far out as <see cref="LegDeploy"/> has them, its bay's doors as far open as <see cref="BayDoors"/>.
    /// </summary>
    internal void FormulaParts(List<StagePart> parts)
    {
        if (State != FlightState.Crashed)
            RocketPhysics.AddParts(parts, Vehicle, Stage, TopStage, _model, LegDeploy, BayDoors, _craft.DoorStops, FairingOn, _formulaLegs);
    }

    /// <summary>
    /// Lands it under formula (see <see cref="Formula"/>), as a physics landing would leave it: at
    /// rest with its origin at <paramref name="position"/>, turned <paramref name="rotation"/>, in
    /// <paramref name="state"/> (landed or afloat), its touchdown the hardest of its landing's
    /// touches (this one at <paramref name="speed"/>, and any before it: a bounce in the physics
    /// before it left, or where it stood burning), its landing's touches over, and on target by the
    /// same test as the physics' (landed, within <see cref="Scene.LandingTargetRadius"/> of its landing site).
    /// </summary>
    internal void LandByFormula(Vector3 position, Quaternion rotation, float speed, FlightState state)
    {
        SetDownByFormula(position, rotation, speed, state);
        State = state;
        (_touchedDown, _vesselTouch, _lowSounded, _hullSounded) = (false, null, false, false); // settled: its landing's touches are over
        (_settledFor, _floatingFor) = (0f, 0f);
        LandedOnTarget = state == FlightState.Landed && LandingSite is not null && DistanceToLandingSite <= Scene.LandingTargetRadius;
        AboardByFormula = Body.HasSea && Rafts.Under(FormulaLowest(_centreOfMass, rotation).At) is not null;
    }

    /// <summary>
    /// Sets it down under formula where it touched, its engine still lit (see
    /// <see cref="FormulaMotion.Standing"/>): at rest with its origin at <paramref name="position"/>,
    /// turned <paramref name="rotation"/>, touching down at <paramref name="speed"/> (one of its
    /// landing's touches), but flying still (in the physics it lands only with its engine off).
    /// Standing on a raft, it is aboard it as one landed there is: the raft moves under it, so it is
    /// to be in the physics, burning or not (see <see cref="AboardByFormula"/>).
    /// </summary>
    internal void StandByFormula(Vector3 position, Quaternion rotation, float speed)
    {
        SetDownByFormula(position, rotation, speed, FlightState.Landed);
        AboardByFormula = Body.HasSea && Rafts.Under(FormulaLowest(_centreOfMass, rotation).At) is not null;
    }

    /// <summary>
    /// Lifting off under formula from where it stood (see <see cref="FormulaMotion.Rising"/>): off
    /// the ground, though still in the same landing until it is clear of it.
    /// </summary>
    internal void LiftOffByFormula() => (_wasTouching, _hullWasTouching) = (false, false);

    /// <summary>
    /// Flown clear of the ground under its own power under formula (by <see cref="AirborneClearance"/>):
    /// what it comes down on next is a landing of its own, as in the physics (see <see cref="JudgeContact"/>).
    /// </summary>
    internal void FlownClearByFormula()
    {
        (_touchedDown, _vesselTouch, _lowSounded, _hullSounded) = (false, null, false, false);
        (_wasTouching, _hullWasTouching) = (false, false);
    }

    /// <summary>
    /// Where it comes to rest under formula, and how: still, its touch at <paramref name="speed"/>
    /// one of its landing's (see <see cref="JudgeTouch"/>), what of it touches as it lies (its feet or
    /// bell, or its body), its legs as far out as each foot is from what it stands on; and, as its
    /// landing begins (the first touch of it: one sound a landing, as the physics makes it), the
    /// sound of it coming down, on the ground or in the water as <paramref name="on"/> says.
    /// </summary>
    private void SetDownByFormula(Vector3 position, Quaternion rotation, float speed, FlightState on)
    {
        var begins = !_touchedDown;
        _model = Matrix4x4.CreateFromQuaternion(rotation);
        _model.Translation = position;
        (_rotation, _angularVelocity, Velocity) = (rotation, Vector3.Zero, Vector3.Zero);
        _centreOfMass = position + Vector3.Transform(_centreOffset, rotation);
        var (lowest, lowestAt, _) = Measure();
        JudgeTouch(speed);
        _hasBeenAirborne = true;
        _wasTouching = lowest < TouchingClearance;
        _hullWasTouching = !_wasTouching;
        (Apoapsis, Periapsis) = Body.Orbit(_centreOfMass, Body.AmongStars(_centreOfMass, Velocity));
        if (HasLegs && LegDeploy >= 1f)
        {
            Span<float> extension = stackalloc float[RocketGeometry.LegCount];
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
                extension[leg] = Math.Clamp(Body.GroundAltitude(Vector3.Transform(RocketGeometry.Foot(Legs, leg), _model)) / Legs.Stroke, 0f, 1f);
            _formulaLegs = new Vector4(extension[0], extension[1], extension[2], extension[3]);
        }
        if (begins)
            _sounds.Add(new SoundEvent(lowestAt, speed, on == FlightState.Floating ? SoundCue.Splash : SoundCue.Touchdown, Body));
    }

    /// <summary>
    /// Wrecks it under formula (see <see cref="Formula"/>), its centre of mass at
    /// <paramref name="at"/>, for <paramref name="reason"/>: as <see cref="Crash"/> wrecks one in the
    /// physics (its throttle cut, where it blew up, the explosion unless <paramref name="explode"/>
    /// is false), its touchdown at <paramref name="touchdown"/> if it came down too hard; by hitting
    /// <paramref name="hit"/>'s body, if it was another vessel (see <see cref="HitCraft"/>).
    /// </summary>
    internal void WreckByFormula(string reason, Vector3 at, bool explode = true, float? touchdown = null, Craft? hit = null)
    {
        _centreOfMass = at;
        _model.Translation = OriginFor(at, _rotation);
        if (touchdown is { } speed)
            TouchdownSpeed = speed;
        HitCraft = hit;
        Crash(reason, explode);
    }

    /// <summary>
    /// Whether, a payload bare of its fairing, it would break up at <paramref name="at"/> moving
    /// <paramref name="velocity"/>: flying too fast through thick air (<see cref="Scene.FairingMaxAirPressure"/>).
    /// </summary>
    internal bool BreaksUpIn(Vector3 at, Vector3 velocity) =>
        Vehicle.Stages[TopStage].Fairing && !FairingOn && !IsFairing
        && Body.AirDensity(Body.Altitude(at)) * velocity.LengthSquared() > Scene.FairingMaxAirPressure;

    /// <summary>What breaking up so is told as (see <see cref="BreaksUpIn"/>).</summary>
    internal string BreakUpReason =>
        $"The {Vehicle.Stages[TopStage].Name.ToLowerInvariant()} broke up: it flew too fast through thick air without its fairing. "
        + "Jettison the fairing once above the thick air (about 2 km).";

    // The reasons it is wrecked by, the physics' and the formula's alike, so the two read the same.
    internal string TowerReason => IsFairing ? "Hit the service tower." : "Flew into the service tower.";
    internal const string MastReason = "Hit a light mast.";
    internal static string HitAnotherVessel(float speed) => $"Hit another vessel at {speed:0.0} m/s.";
    /// <summary>What hitting <paramref name="body"/>'s ground at <paramref name="speed"/> is told as, by the ground's name there ("Hit the ground", "Hit the moon").</summary>
    internal static string HitTheGround(float speed, GroundBody body) => $"Hit {body.GroundName} at {speed:0.0} m/s. Touch down slower than {Scene.SafeTouchdownSpeed:0} m/s.";
    internal string OnItsBell(float speed) =>
        $"Came down on its engine bell at {speed:0.0} m/s: without legs, touch down slower than {Scene.SafeBellTouchdownSpeed:0.0} m/s"
        + (HasLegs ? ", or deploy them (G)." : ".");
    internal static string HitTheWater(float speed) => $"Hit the water at {speed:0.0} m/s. Splash down slower than {Scene.SafeSplashdownSpeed:0} m/s.";
    /// <summary>What a fairing half coming down is told as: in the sea, on a raft (the drone ship, or the ice), or on the ground.</summary>
    internal static string FairingFell(bool sea, Raft? raft) =>
        sea ? "Fell into the sea." : raft is { IsShip: true } ? "Fell onto the drone ship." : raft is not null ? "Fell onto the ice." : "Fell to the ground.";

    public Vehicle Vehicle => _craft.Vehicle;

    /// <summary>Which of the vehicle's stages is at its bottom (the one whose engine fires), and at its top.</summary>
    public int Stage => _craft.First;
    public int TopStage => _craft.Last;
    public int StageCount => Vehicle.Stages.Length;
    public Stage CurrentStage => Vehicle.Stages[Stage];

    /// <summary>Whether it has a stage to drop.</summary>
    public bool CanStage => _craft.Last > _craft.First;

    /// <summary>Whether it can be flown: one of its stages carries guidance. Otherwise it is debris.</summary>
    public bool Guided
    {
        get
        {
            for (var i = Stage; i <= TopStage; i++)
                if (Vehicle.Stages[i].Guided)
                    return true;
            return false;
        }
    }

    /// <summary>Whether it is a part that has come away from a rocket, rather than the whole rocket as it launched.</summary>
    public bool CameAway => !(Stage == 0 && TopStage == StageCount - 1);

    /// <summary>What it is called on the HUD: the rocket's name while it is whole, else its bottom stage's.</summary>
    public string Name => NameOf(Vehicle, Stage, TopStage);

    /// <summary>What a vessel of stages <paramref name="first"/> to <paramref name="last"/> of <paramref name="vehicle"/> is called (see <see cref="Name"/>): asked of one saved too, which is never brought to life (a world loaded with more than it holds).</summary>
    internal static string NameOf(Vehicle vehicle, int first, int last) =>
        first == 0 && last == vehicle.Stages.Length - 1 ? vehicle.Name : vehicle.Stages[first].Name;

    /// <summary>From the bottom of its body to the top of its nose (or its fairing's, while that is on), in metres.</summary>
    public float Height => FairingOn
        ? RocketGeometry.StageBase(Vehicle, Stage, TopStage) + Fairing.Height + Fairing.NoseHeight
        : RocketGeometry.StackHeight(Vehicle, Stage, TopStage);

    /// <summary>Whether its payload's fairing is still on, round its top stage (see <see cref="Stage.Fairing"/>).</summary>
    public bool FairingOn => _craft.FairingOn;

    /// <summary>Whether staging now would jettison the fairing, rather than drop a stage: it comes off before the payload is let go.</summary>
    public bool JettisonsFairing => FairingOn && TopStage == Stage + 1;

    /// <summary>Whether it is half of a fairing that has come away: no engine, nothing to fly.</summary>
    internal bool IsFairing => CurrentStage.Kind == StageKind.Fairing;

    /// <summary>Whether it has landing lights: its bottom stage is a booster, an upper stage, a satellite or a lander (a fairing half has none).</summary>
    public bool HasLandingLights => CurrentStage.Kind is StageKind.Booster or StageKind.Upper or StageKind.Satellite or StageKind.Lander;

    /// <summary>
    /// Its landing lights as a fraction, 0 (off) to 1 (fully on): on while it is dark where it is
    /// and it is near the ground (see <see cref="StepLandingLights"/>), never saved (worked out
    /// again from where it is and the world's time).
    /// </summary>
    public float LandingLights { get; private set; }

    // The landing lights' held states: dark where it is (Sun.IsDark's), near the ground (by the
    // two heights), and whether they have been set at all since it was made.
    private bool _lightsDark, _lightsNear, _lightsSet;

    /// <summary>Whether it can have its landing lights on at all: it has them and isn't wrecked (one that can't has none, at once).</summary>
    private bool CanLight => HasLandingLights && State != FlightState.Crashed;

    /// <summary>Whether its landing lights are wanted on, by the held states: it can have them, and it is dark and near the ground.</summary>
    private bool LandingLightsWanted => CanLight && _lightsDark && _lightsNear;

    /// <summary>
    /// Whether it is near the ground for its lights (its body's): landed or afloat it is; otherwise below
    /// <see cref="Scene.LandingLightsBelow"/> above what is underfoot, or, already near, not above
    /// <see cref="Scene.LandingLightsOffAbove"/>.
    /// </summary>
    private bool NearForLights(bool wasNear)
    {
        if (State is FlightState.Landed or FlightState.Floating)
            return true;
        var limit = wasNear ? Scene.LandingLightsOffAbove : Scene.LandingLightsBelow;
        // High above the highest ground (and its fine detail), it is far without the terrain's
        // lookup, a few microseconds for each of the world's vessels a frame.
        if (Body.Altitude(Position) > Body.MaxHeight + Scene.FineDetailMargin + limit)
            return false;
        var altitude = Body.SurfaceAltitude(Position);
        return wasNear ? altitude <= limit : altitude < limit;
    }

    /// <summary>How high the sun is where it is, over its body's horizon (<see cref="Sun.Height"/>).</summary>
    private float SunHeight(double worldTime) => Sun.Height(Body, Position, worldTime);

    /// <summary>Whether it is dark where it is, by its body's sky (<see cref="Sun.IsDark(GroundBody, Vector3, double, bool)"/>).</summary>
    private bool IsDark(double worldTime, bool wasDark) => Sun.IsDark(Body, Position, worldTime, wasDark);

    /// <summary>
    /// Its landing lights straight to where they should be at <paramref name="worldTime"/>, with no
    /// fade (a world loaded, or a vessel just made). Between dusk's and dawn's heights it is still
    /// dark while its sun is rising, and not yet while it is setting, as continuous play would have it.
    /// </summary>
    internal void ResetLandingLights(double worldTime)
    {
        _lightsSet = true;
        if (!CanLight)
        {
            LandingLights = 0f;
            return;
        }
        var rising = SunHeight(worldTime + 1.0) > SunHeight(worldTime);
        _lightsDark = IsDark(worldTime, wasDark: rising);
        _lightsNear = NearForLights(wasNear: false);
        LandingLights = LandingLightsWanted ? 1f : 0f;
    }

    /// <summary>
    /// Its held dark and near states at <paramref name="worldTime"/>, then its landing lights a
    /// step of <paramref name="realSeconds"/> nearer on or off (a full swing in
    /// <see cref="Scene.PadLightFadeSeconds"/>, so neither the warp nor slow motion changes it).
    /// One never set since it was made starts straight where it should be instead; one that can't
    /// have them (a fairing half, a wreck) has none at once, with no sun or terrain asked.
    /// </summary>
    internal void StepLandingLights(double worldTime, float realSeconds)
    {
        if (!CanLight)
        {
            _lightsSet = true;
            LandingLights = 0f;
            return;
        }
        if (!_lightsSet)
        {
            ResetLandingLights(worldTime);
            return;
        }
        _lightsDark = IsDark(worldTime, _lightsDark);
        _lightsNear = NearForLights(_lightsNear);
        var step = realSeconds / Scene.PadLightFadeSeconds;
        LandingLights = LandingLightsWanted ? MathF.Min(1f, LandingLights + step) : MathF.Max(0f, LandingLights - step);
    }

    public FlightState State { get; internal set; }
    public float Throttle { get; private set; }

    /// <summary>Fuel left in the firing stage, 1 (full) to 0 (empty: or it never had any).</summary>
    public float Fuel => CurrentStage.FuelMass > 0f ? _fuel[Stage] / CurrentStage.FuelMass : 0f;

    /// <summary>Fuel left in all its stages, as a fraction of what they started with.</summary>
    public float TotalFuel
    {
        get
        {
            float left = 0f, full = 0f;
            for (var i = Stage; i <= TopStage; i++)
            {
                left += _fuel[i];
                full += Vehicle.Stages[i].FuelMass;
            }
            return full > 0 ? left / full : 0f;
        }
    }

    /// <summary>Its mass now, in kg: its stages and the fuel in them, and its fairing while that is on.</summary>
    public float Mass
    {
        get
        {
            var mass = FairingOn ? Fairing.Mass : 0f;
            for (var i = Stage; i <= TopStage; i++)
                mass += Vehicle.Stages[i].DryMass + _fuel[i];
            return mass;
        }
    }

    /// <summary>How hard the engine would push at full throttle, as an acceleration, now: it rises as the fuel burns.</summary>
    public float MaxAcceleration => CurrentStage.Thrust / Mass;

    /// <summary>
    /// How hard it could push next, at full throttle, as an acceleration: its firing stage's while
    /// that has fuel; with that empty, the next stage's, the empty one dropped (one key away, so a
    /// dodge is still possible: see <see cref="Collisions.WarningSeconds"/>); 0 with neither (or with
    /// staging only jettisoning the fairing).
    /// </summary>
    public float NextAcceleration
    {
        get
        {
            if (_fuel[Stage] > 0f && CurrentStage.Thrust > 0f)
                return MaxAcceleration;
            if (!CanStage || JettisonsFairing)
                return 0f;
            var next = Vehicle.Stages[Stage + 1];
            return _fuel[Stage + 1] > 0f && next.Thrust > 0f ? next.Thrust / (Mass - CurrentStage.DryMass - _fuel[Stage]) : 0f;
        }
    }

    /// <summary>How far the legs are out, 0 (stowed) to 1 (deployed).</summary>
    public float LegDeploy { get; private set; }
    public bool LegsWanted => _legsWanted;
    public bool HasLegs => CurrentStage.Legs;

    /// <summary>The shape of its legs (its bottom stage's: see <see cref="HasLegs"/>).</summary>
    internal LegGeometry Legs => CurrentStage.LegGeometry;

    /// <summary>Whether it has a payload bay with doors (its bottom stage's, as its legs are: the lander's).</summary>
    public bool HasBay => CurrentStage.Bay;

    /// <summary>How far open its bay's doors are, 0 (shut) to 1 (open, as ramps down to the ground: see <see cref="RocketGeometry.DoorOpenRadians"/>).</summary>
    public float BayDoors { get; private set; }

    /// <summary>Whether its bay's doors are wanted open (<see cref="FlightInput.ToggleBayDoors"/>): they swing towards it (<see cref="MoveDoors"/>).</summary>
    public bool BayDoorsWanted => _bayDoorsWanted;

    /// <summary>The shape of its top stage's fairing, round it while that is on (see <see cref="FairingOn"/>).</summary>
    internal FairingGeometry Fairing => Vehicle.Stages[TopStage].FairingGeometry;

    /// <summary>Where the steering is pointing the nose. The rocket turns to follow it.</summary>
    public Vector3 Aim => _aim;

    /// <summary>Whether the steering is keeping the nose pointed the way it is moving.</summary>
    public bool ProgradeHold { get; private set; }

    /// <summary>Whether the steering is keeping the nose pointed against the way it is moving (for slowing down).</summary>
    public bool RetrogradeHold { get; private set; }

    /// <summary>What its pilot has picked to fly to, if anything (see <see cref="GameOne.Flight.TargetOf"/>, which checks it is still there).</summary>
    public NavTarget? Target { get; internal set; }

    /// <summary>How the camera last looked at it (set from the flown vessel's view each step, see <see cref="FlightInput.View"/>), or none if it has not been looked at.</summary>
    public CameraView? View { get; internal set; }

    /// <summary>
    /// Its pilot's planned burns, the next one first, fixed once set (see <see cref="ManoeuvreChain"/>);
    /// null with none. The next stays until the path flown matches the one it plans and the engine
    /// is off (see <see cref="Matched"/>), however long past its time; a node never matched stays
    /// until it is deleted.
    /// </summary>
    public ManoeuvreChain? Chain { get; private set; }

    /// <summary>
    /// The next node's burn still needed, as a change of velocity in the world (the ground's frame,
    /// now). Before its time: until the burn begins (the engine lighting), the node's amounts
    /// measured where its chain has it, from how its planned path moves there; from then on, fixed
    /// among the stars as it was when it began, less what the engine has given since. From its time
    /// on, how its planned path moves at its point nearest the vessel, less how the vessel moves
    /// (see <see cref="Late"/>), so a burn begun late, or still going, is measured against where the
    /// plan goes. Null without a node.
    /// </summary>
    /// <remarks>
    /// A burn changes the orbit, which is among the stars, so that is where it is fixed: over the
    /// ground it turns back by the planet's turn while it lasts (as a craft's attitude does above
    /// the air: see <see cref="Steer"/>). It is kept as it was at ignition and turned to now each
    /// time it is asked for, never turned frame by frame, so nothing turns it twice.
    /// </remarks>
    public Vector3? BurnLeft(double now) =>
        Chain is not { } chain ? null
        : now >= chain.TimeOf(0) ? Late(chain, now)
        : _burnAim is { } aim ? Body.TurnedDirection(aim - _burned, -(now - _burnStart))
        : Planned(chain, now);

    /// <summary>Whether the next node's burn has begun (so it is fixed among the stars: see <see cref="BurnLeft"/>).</summary>
    public bool BurnBegun => _burnAim is not null;

    /// <summary>
    /// Whether the path flown matches the one <paramref name="chain"/>'s next node plans, with
    /// <paramref name="left"/> still needed (<see cref="BurnLeft"/>) at world time
    /// <paramref name="now"/>: under its <see cref="ManoeuvreNode.MatchTolerance"/>, once its burn
    /// has begun or its time has come. Before either, what is left is only the node's amounts, which
    /// a node with hardly any burn would match without being flown at all.
    /// </summary>
    private bool Matched(ManoeuvreChain chain, Vector3 left, double now) =>
        (_burnAim is not null || now >= chain.TimeOf(0)) && left.Length() < chain.Nodes[0].MatchTolerance;

    /// <summary>How the next node's burn stands, for the pilot (see <see cref="NodeGuide"/>); null without a node.</summary>
    public NodeGuide? Guide(double now)
    {
        if (BurnLeft(now) is not { } left)
            return null;
        // Past it: what is left points against the burn as it began (both turned to now).
        var overshot = _burnAim is { } aim && Vector3.Dot(left, Body.TurnedDirection(aim, -(now - _burnStart))) < 0f;
        // At full throttle, from the rocket equation: the fuel it takes, at the rate it burns.
        var stage = CurrentStage;
        var needed = 0f;
        var seconds = float.PositiveInfinity;
        if (stage.FuelFlow > 0f)
        {
            needed = Mass * (1f - MathF.Exp(-left.Length() / stage.ExhaustVelocity));
            seconds = needed / stage.FuelFlow;
        }
        return new NodeGuide(Chain!.TimeOf(0) - now, left, overshot, seconds, needed > _fuel[Stage] || stage.FuelFlow <= 0f, Matched(Chain!, left, now));
    }

    /// <summary>
    /// The pilot's new plan: this chain (one with no nodes, none). If the next node has changed
    /// (its amounts, its time or its anchor) while its burn was under way, the rest of the burn is
    /// the new one's, measured from now.
    /// </summary>
    internal void Replan(ManoeuvreChain chain, double now)
    {
        var next = Chain is { } old ? (old.Anchor, old.TimeOf(0), old.Nodes[0]) : default((ChainAnchor, double, ManoeuvreNode)?);
        Chain = chain.Nodes.IsEmpty ? null : chain;
        if (Chain is not { } planned)
            (_burnAim, _burned, _burnStart) = (null, Vector3.Zero, 0);
        else if ((planned.Anchor, planned.TimeOf(0), planned.Nodes[0]) != next && _burnAim is not null)
            (_burnAim, _burned, _burnStart) = (Planned(planned, now), Vector3.Zero, now);
    }

    /// <summary>
    /// <paramref name="chain"/>'s first node's burn, where the chain has it (its anchored path,
    /// patched at the spheres of influence, as its plan is: <see cref="ManoeuvrePlan"/>), measured
    /// round the body it is at there, from how that path moves among its stars; changed into this
    /// vessel's own body's frame, as that matches its ground now.
    /// </summary>
    private Vector3 Planned(ManoeuvreChain chain, double now)
    {
        // Asked several times a frame (the guide is read by the view, the HUD and the readout), and
        // the same at the same time, chain and body: worked out once.
        if (_planned is { } kept && kept.Now == now && ReferenceEquals(kept.Chain, chain) && kept.Body == Body)
            return kept.Burn;
        var burn = PlannedAfresh(chain, now);
        _planned = (now, chain, Body, burn);
        return burn;
    }

    private Vector3 PlannedAfresh(ManoeuvreChain chain, double now)
    {
        var (body, position, velocity, _) = AtNode(chain, now);
        var burn = chain.Nodes[0].DeltaV(body, position, velocity);
        return body == Body ? burn : Bodies.Change(body, Body, now).Direction((Vector3Double)burn).ToSingle();
    }

    /// <summary>
    /// Where <paramref name="chain"/>'s first node is on its anchored path (patched at the spheres
    /// of influence) and how that path moves there among the stars, before its burn: round the body
    /// it is at by then, in that body's frame as it matches its ground at world time
    /// <paramref name="now"/>; and its time (no earlier than its anchor's).
    /// </summary>
    private (GroundBody Body, Vector3 Position, Vector3 Velocity, double At) AtNode(ManoeuvreChain chain, double now)
    {
        var legs = _toNode ??= new List<PathLeg>(Scene.PatchedLegs);
        var (start, at) = (chain.Anchor.Time, Math.Max(chain.TimeOf(0), chain.Anchor.Time));
        var (anchored, moving) = chain.StartAt(now);
        Patched.Legs(chain.Body, anchored, moving, now, legs, horizon: at, from: start - now);
        var last = legs[^1];
        var (position, velocity) = last.Orbit.StateAfter(at - last.Start);
        return (last.Body, position, velocity, at);
    }

    /// <summary>
    /// The next node's burn still needed from its time on: how the path it plans (from its burn,
    /// patched at the spheres of influence) moves at its point nearest where the vessel is, less how
    /// the vessel moves, both among the stars of the body that stretch of path is round, changed
    /// into the vessel's own body's frame. A vessel that hasn't burned yet is still short of the
    /// whole burn; one that has flown it, by the guide or late, is left with only how far off it is.
    /// </summary>
    /// <remarks>
    /// The nearest point is looked for from the node's time on, no further than twice as far past
    /// it as now (and a second): one late by some time is near where the plan was that long after
    /// its burn. On a closed orbit, no further than a lap, which has every point of it. It is found by Newton's steps along each stretch, starting from where the plan is
    /// now; the path is smooth and the vessel near it, so a few are enough. The path is worked out
    /// once a step (it depends on the chain and the time alone); the nearest point every time it is
    /// asked, as the vessel moves between.
    /// </remarks>
    private Vector3 Late(ManoeuvreChain chain, double now)
    {
        var legs = _afterNode ??= new List<PathLeg>(Scene.PatchedLegs);
        if (_afterNodeFor is not { } kept || kept.Now != now || !ReferenceEquals(kept.Chain, chain))
        {
            var (body, position, velocity, at) = AtNode(chain, now);
            velocity += chain.Nodes[0].DeltaV(body, position, velocity);
            var until = now + (now - at) + 1.0;
            // A closed orbit comes round to every point of it within a lap.
            if (new Orbit(body, position, velocity, from: at - now) is { Closed: true } orbit)
                until = Math.Min(until, at + orbit.Period);
            Patched.Legs(body, position, velocity, now, legs, horizon: until, from: at - now);
            kept = (now, chain, at, until);
            _afterNodeFor = kept;
        }

        var (nearest, left) = (double.PositiveInfinity, Vector3.Zero);
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            var (from, to) = (Math.Max(leg.Start, kept.At), Math.Min(leg.End, kept.Until));
            if (to < from)
                continue;
            // The vessel in the frame of the body this stretch is round.
            var (place, moving) = (CentreOfMass, Velocity);
            if (leg.Body != Body)
            {
                var change = Bodies.Change(Body, leg.Body, now);
                (place, moving) = (change.Point((Vector3Double)CentreOfMass).ToSingle(), change.Velocity((Vector3Double)CentreOfMass, (Vector3Double)Velocity).ToSingle());
            }
            var stars = leg.Body.AmongStars(place, moving);
            var t = Math.Clamp(now, from, to);
            for (var step = 0; step < 6; step++)
            {
                var (p, v) = leg.Orbit.StateAfter(t - leg.Start);
                var speed = v.LengthSquared();
                if (speed < 1e-6f)
                    break;
                var next = Math.Clamp(t + Vector3.Dot(place - p, v) / speed, from, to);
                if (Math.Abs(next - t) < 1e-6)
                    break;
                t = next;
            }
            var (on, planned) = leg.Orbit.StateAfter(t - leg.Start);
            var distance = Vector3.Distance(on, place);
            if (distance >= nearest)
                continue;
            nearest = distance;
            left = leg.Body == Body ? planned - stars : Bodies.Change(leg.Body, Body, now).Direction((Vector3Double)(planned - stars)).ToSingle();
        }
        return left;
    }

    /// <summary>
    /// The next node, as the engine runs: lighting it begins the burn (fixing it among the stars),
    /// and what it gives is counted against it (turned to how the ground was when it began); once
    /// the path flown matches the one it plans with the engine off (<see cref="Matched"/>), it goes,
    /// and the next is anchored afresh on the path it is on now, at its own time
    /// (<see cref="ManoeuvreChain.Delete"/>), keeping its amounts.
    /// </summary>
    private void FollowNodes(float dt, double now)
    {
        var lit = EngineLit;
        if (Chain is { } chain)
        {
            if (lit && !_wasLit && _burnAim is null)
                (_burnAim, _burnStart) = (Planned(chain, now), now);
            if (lit && _burnAim is not null)
                _burned += Body.TurnedDirection(Up, now - _burnStart) * (Throttle * MaxAcceleration * dt);
            if (!lit && (_burnAim is not null || now >= chain.TimeOf(0)) && BurnLeft(now) is { } left && Matched(chain, left, now))
            {
                var rest = chain.Delete(0, this, now);
                Chain = rest.Nodes.IsEmpty ? null : rest;
                (_burnAim, _burned, _burnStart) = (null, Vector3.Zero, 0);
            }
        }
        _wasLit = lit;
    }

    /// <summary>The height of its lowest point above whatever is under it (never below 0).</summary>
    public float Ground { get; private set; }
    /// <summary>
    /// The height of its lowest point above its body's heights' zero, the sea on the planet (<see cref="GroundBody.Altitude"/>): the altitude the
    /// player is shown, and what the atmosphere's top and orbits are measured by. Below the sea it is negative.
    /// </summary>
    public float Altitude { get; private set; }
    /// <summary>The highest <see cref="Altitude"/> it has flown at: the HUD's best altitude, and what the mission's height to reach is judged by.</summary>
    public float MaxAltitude { get; private set; }
    /// <summary>The highest <see cref="Ground"/> it has flown at: whether it has been up off the ground, wherever it flew from (the "Too fast!" cue goes by it).</summary>
    public float MaxGround { get; private set; }

    public bool ReachedTarget => MaxAltitude >= _mission.TargetAltitude;

    /// <summary>Its origin (the bottom of its bottom stage's body).</summary>
    public Vector3 Position => _model.Translation;
    /// <summary>
    /// Its centre of mass: the point <see cref="Velocity"/> is for, so the one every orbit, plan and
    /// velocity among the stars is worked out from (<see cref="Planet.AmongStars"/> adds the
    /// ground's turn where the point is: from the origin, with this velocity, it would be off by
    /// the turn across the gap, about 0.1 m/s for a booster).
    /// </summary>
    public Vector3 CentreOfMass => _centreOfMass;
    /// <summary>How fast its centre of mass moves over the ground (Jolt's linear velocity).</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>
    /// How fast what it is on was moving at its centre of mass when it was last measured: a raft's
    /// (the drone ship's deck, a floe) if its lowest point is over one, else nothing (the ground
    /// stands still). What its stops and standing still are measured against (see <see cref="CheckContact"/>).
    /// </summary>
    internal Vector3 GroundVelocity => _ground;

    /// <summary>From its own space (its bottom stage's) into the world.</summary>
    public Matrix4x4 Model => _model;

    /// <summary>Which way its nose points.</summary>
    public Vector3 Up => Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, _model));

    /// <summary>
    /// Its own right and forward, across its body, for the keys to steer by (D leans it right, W
    /// forward), so they turn with it: hold one and it loops round. On the pad, facing the
    /// starting camera (which looks north), right is east and forward is north.
    /// </summary>
    public Vector3 Right => Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, _model));
    public Vector3 Forward => Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, _model));

    /// <summary>Straight up where it is: away from the planet's centre.</summary>
    public Vector3 LocalUp => Body.Up(Position);

    /// <summary>How fast it is climbing (negative when falling), and moving across the ground.</summary>
    public float VerticalSpeed => Vector3.Dot(Velocity, LocalUp);
    public float HorizontalSpeed => (Velocity - LocalUp * VerticalSpeed).Length();

    /// <summary>How fast it is moving among the stars, as an orbit goes: even standing on the pad it moves with the ground's turn.</summary>
    public float OrbitSpeed => Body.AmongStars(CentreOfMass, Velocity).Length();

    /// <summary>How far it leans from upright, in degrees.</summary>
    public float TiltDegrees { get; private set; }

    /// <summary>The highest and lowest points of the orbit it is on round its body (its centre of mass's), as altitudes above that body's datum.</summary>
    public float Apoapsis { get; private set; }
    public float Periapsis { get; private set; }

    /// <summary>
    /// Whether its orbit's highest point is past where it would leave its body's pull (its sphere
    /// of influence's leaving edge: <see cref="GroundBody.SphereRadius"/> times
    /// <see cref="Scene.SphereLeaveFactor"/>), so it never flies a lap of it: out of the moon's
    /// pull into the planet's, say. Never round a body with no parent (its sphere has no edge).
    /// </summary>
    public bool LeavesItsPull => float.IsFinite(Apoapsis) && Apoapsis + Body.SeaRadius > Body.SphereRadius * Scene.SphereLeaveFactor;

    /// <summary>How long a lap of that orbit takes, in seconds, by its body's pull (infinite once it would escape).</summary>
    public double OrbitPeriod => float.IsFinite(Apoapsis)
        ? Math.Tau * Math.Sqrt(Math.Pow(((double)Apoapsis + Periapsis) / 2 + Body.SeaRadius, 3) / Body.Mu)
        : double.PositiveInfinity;

    /// <summary>
    /// The frame Auto chooses for it now (see <see cref="Frames.Auto"/>); it keeps its last, for the
    /// margin. Landed or afloat it is the surface's by rule, whatever its orbit would be: restored
    /// that way, frozen (when it isn't measured again), or on the very step it comes down. That is
    /// the rule by state: no path reaches it today with an orbit-like periapsis (on the ground its
    /// periapsis is no higher than where it stands), but it doesn't lean on that.
    /// </summary>
    public Frame AutoFrame => State is FlightState.Landed or FlightState.Floating ? Frame.Surface : _autoFrame;
    private Frame _autoFrame;

    /// <summary>
    /// Whether its orbit stays above its body's orbit floor all the way round (<see cref="GroundBody.OrbitFloor"/>:
    /// the air, or with none the highest ground), so it would never come down, and it stays on it,
    /// a lap after another: not escaping, nor leaving its body's pull before its top (<see cref="LeavesItsPull"/>).
    /// </summary>
    public bool OrbitStable => Periapsis > Body.OrbitFloor && float.IsFinite(Apoapsis) && !LeavesItsPull;

    /// <summary>Whether it has been in a stable orbit with its engine off.</summary>
    public bool AchievedOrbit { get; private set; }

    /// <summary>
    /// Where the mission wants it to land, if anywhere (see <see cref="Mission.LandingSites"/>): only
    /// a part of the mission's own rocket, not a fairing half. In the frame of the body the sites
    /// are on (<see cref="Sites.Body"/>), wherever it is: see <see cref="LandingSiteHere"/>.
    /// </summary>
    public Vector3? LandingSite => Vehicle == _mission.Vehicle ? _mission.LandingSiteFor(Stage, TopStage) : null;

    /// <summary>
    /// Its <see cref="LandingSite"/> in its own body's frame, as its state was last brought to: the
    /// site itself at the sites' body; at another, changed across the bodies (<see cref="Bodies.Change"/>).
    /// </summary>
    public Vector3? LandingSiteHere => LandingSite is not { } site ? null
        : Body == Sites.Body ? site
        : Bodies.Change(Sites.Body, Body, _at).Point((Vector3Double)site).ToSingle();

    /// <summary>
    /// How far it is from the mission's landing site (not its picked target), 0 if it has none: at
    /// the sites' body across the ground and round it; at another, where the ground under it is
    /// none of the site's, the straight line to it across the bodies (<see cref="LandingSiteHere"/>),
    /// so it is never landed on it there.
    /// </summary>
    public float DistanceToLandingSite => LandingSite is not { } site ? 0f
        : Body == Sites.Body ? Body.Radius * Body.AngleBetween(Position, site)
        : Vector3.Distance(Position, LandingSiteHere!.Value);

    /// <summary>
    /// How hard it came down, in m/s: the hardest touch of its landing, from its first touch on
    /// (since it last flew clear of the ground under its own power) to settling, on the ground
    /// (its feet, bell or body) or in the water; and, come to rest on another vessel, its touch on
    /// that. What the landing's softness is scored by: a gentle first touch doesn't hide a hard
    /// tip onto its side after it, and a gentle hop or bounce doesn't soften a hard one.
    /// </summary>
    public float TouchdownSpeed { get; private set; }

    /// <summary>
    /// Whether it is still as it stood on the pad: never launched, or launched but in one piece and
    /// never off the ground (at zero throttle, its engine never lit). A new flight's rocket
    /// replacing it clears nothing worth telling.
    /// </summary>
    public bool NeverFlew => State == FlightState.Ready || (State == FlightState.Flying && !CameAway && !_hasBeenAirborne);
    /// <summary>Whether it landed on its landing site, rather than somewhere else.</summary>
    public bool LandedOnTarget { get; private set; }
    public string? CrashReason { get; private set; }
    /// <summary>Where it blew up, if it has.</summary>
    public Vector3 CrashPosition { get; private set; }
    /// <summary>How fast it was going when it blew up (m/s), if it has.</summary>
    public float CrashSpeed { get; private set; }
    /// <summary>Whether it was in the physics, not moved by formula, when it blew up: whether anyone could have seen it.</summary>
    public bool CrashedWatched { get; private set; }
    /// <summary>Whether its loss is in the world's log already (so it is written once, however long the wreck stays).</summary>
    internal bool LossLogged { get; set; }

    /// <summary>Its loss's entry in the world's log, once written (none for a fairing half): one wrecked later by the same touch is joined to it (see Flight's LogLosses).</summary>
    internal LogEntry? LoggedAs { get; set; }

    /// <summary>
    /// Wrecked by hitting another vessel, that one's body as it was hit (its stand-in under
    /// formula: see <see cref="Craft"/>): the vessel actually hit, by which the log makes the two
    /// one entry, though the second may be wrecked a step or more after the first.
    /// </summary>
    internal Craft? HitCraft { get; private set; }

    /// <summary>
    /// The most its speed can change at once in a hit with another vessel (m/s) before it is
    /// destroyed. For now the ground's limit, for every vessel; it is asked of the vessel so that,
    /// once vessels are made of parts, each part can judge its own.
    /// </summary>
    public float ImpactTolerance => Scene.SafeTouchdownSpeed;

    /// <summary>Whether the engine is firing.</summary>
    public bool EngineLit => State == FlightState.Flying && Throttle > 0f && _fuel[Stage] > 0f && _ignitionDelay <= 0f;

    /// <summary>
    /// Whether it is down: on the pad, landed or afloat, or touching the ground by its feet, bell or
    /// body (as of the last step). Lit and no longer down, it has lifted off.
    /// </summary>
    public bool Grounded => State is FlightState.Ready or FlightState.Landed or FlightState.Floating || _wasTouching || _hullWasTouching;

    /// <summary>Whether its body touches anything now (the ground, a raft, a tower or mast, another vessel), in the physics; under formula, nothing (it has no body).</summary>
    internal bool Touching => !OnFormula && !_craft.Removed && _physics!.TouchingAnything(_craft);

    /// <summary>
    /// How fast the steering can turn it now, in radians per second: the engine's gimbal does most
    /// of it, so it turns fastest at full throttle, and with the engine off only small thrusters are left.
    /// </summary>
    public float TurnRate => Scene.TiltRate * (EngineLit
        ? Scene.ThrusterAuthority + (1f - Scene.ThrusterAuthority) * Throttle
        : Scene.ThrusterAuthority);

    /// <summary>A rocket put on the pad starts at <see cref="Scene.LaunchThrottle"/>, until the player sets another.</summary>
    internal void ReadyToLaunch()
    {
        State = FlightState.Ready;
        Throttle = Scene.LaunchThrottle;
    }

    /// <summary>
    /// Launched from the pad: the engine lights at once, at whatever the throttle is set to. It
    /// lifts off when that beats its weight; at less, it sits on the pad burning (or, at nothing,
    /// unlit) until the throttle comes up.
    /// </summary>
    internal void Ignite() => State = FlightState.Flying;

    /// <summary>
    /// Landed or afloat, wherever that is: with its throttle up and fuel in the tank, its engine
    /// lights and it is flying again (it lifts off once the thrust beats its weight). True if it did.
    /// </summary>
    internal bool TakeOff()
    {
        if (State is not (FlightState.Landed or FlightState.Floating) || Throttle <= 0f || _fuel[Stage] <= 0f)
            return false;
        _physics!.Wake(_craft);
        (State, _settledFor, _floatingFor) = (FlightState.Flying, 0f, 0f);
        return true;
    }

    /// <summary>The pilot's one-off and throttle inputs, for the vessel being flown.</summary>
    internal void TakeInput(float dt, in FlightInput input)
    {
        Throttle = Math.Clamp(Throttle + Math.Clamp(input.Throttle, -1f, 1f) * Scene.ThrottleRate * dt, 0f, 1f);
        if (input.FullThrottle)
            Throttle = 1f;
        if (input.CutThrottle)
            Throttle = 0f;
        if (input.ToggleLegs && HasLegs)
            _legsWanted = !_legsWanted;
        if (input.ToggleBayDoors && HasBay)
            _bayDoorsWanted = !_bayDoorsWanted;
        // Prograde and retrograde hold are one or the other: turning one on turns the other off.
        if (input.ToggleProgradeHold)
            (ProgradeHold, RetrogradeHold) = (!ProgradeHold, false);
        if (input.ToggleRetrogradeHold)
            (RetrogradeHold, ProgradeHold) = (!RetrogradeHold, false);
    }

    /// <summary>
    /// A frame of flying: the next engine's ignition delay, the next node's burn, burning fuel, and
    /// the steering (with <paramref name="lean"/> and <paramref name="hold"/> from the pilot, or
    /// none if it isn't being flown). <paramref name="now"/> is the world's time. The holds steer by
    /// <paramref name="holdVelocity"/>, its velocity in the frame in use (see <see cref="GameOne.Flight.FrameVelocity"/>),
    /// and leave the nose be below <paramref name="holdFloor"/> (see <see cref="GameOne.Flight.HoldFloor"/>).
    /// </summary>
    internal void Run(float dt, Vector3 lean, Vector3? hold, double now, Vector3 holdVelocity, float holdFloor)
    {
        _ignitionDelay = MathF.Max(0f, _ignitionDelay - dt);
        // Under formula no node is flown, nor passed and let go: they wait for its pilot (the
        // user: no nodes flown under formula), as the throttle and holds go on as they were.
        if (!OnFormula)
            FollowNodes(dt, now);
        if (EngineLit)
            _fuel[Stage] = MathF.Max(0f, _fuel[Stage] - Throttle * CurrentStage.FuelFlow * dt);
        Steer(dt, lean, hold, holdVelocity, holdFloor);
    }

    /// <summary>Whether any of it is in the water (as it was after the last physics step).</summary>
    private bool InWater => _wetParts != 0 || _craft.Submerged > 0f;

    /// <summary>What it asks of the physics this frame, if anything (a vessel that can't be flown asks nothing).</summary>
    internal RocketControl? Control
    {
        get
        {
            if (State != FlightState.Flying || !Guided || OnFormula || _craft.Removed)
                return null; // under formula it has no body to push or turn: the formula moves it
            var thrust = EngineLit ? Throttle * MaxAcceleration : 0f;
            // No steering while it stands on the ground, unless the engine is lifting it off again:
            // otherwise a booster that has landed leaning over would be held up when it should fall.
            // Nor in the water: held upright there, a tall one would never lie down in it.
            var steer = (!_wasTouching && !_hullWasTouching && !InWater) || Throttle > Scene.SurfaceSteeringThrottle;
            return new RocketControl(_craft, thrust, _aim, steer);
        }
    }

    /// <summary>
    /// Staging (flying, landed or afloat): drops the bottom stage, if it has one to drop: that becomes a vessel of its own,
    /// and this one flies on as the stages above it. Their next engine lights a moment later, once the
    /// stages are clear of each other, at whatever the throttle is set to. Before the payload on top
    /// is let go, though, its fairing comes off (see <see cref="JettisonFairing"/>). Returns the
    /// vessels that came away (none if nothing did), numbered from <paramref name="id"/>.
    /// </summary>
    internal Vessel[] Separate(int id)
    {
        if (State is not (FlightState.Flying or FlightState.Landed or FlightState.Floating) || OnFormula)
            return []; // under formula it has no body to come apart (it never stages itself: only the one flown does, in the physics)
        if (JettisonsFairing)
            return JettisonFairing(id);
        if (_physics!.Separate(_craft, atRest: State is FlightState.Landed or FlightState.Floating) is not var (droppedCraft, rest))
            return [];
        // Landed or afloat, it comes apart all the same: both parts are judged again, until they settle.
        State = FlightState.Flying;

        var fuel = (float[])_fuel.Clone();
        _craft = rest;
        var dropped = new Vessel(_worlds, _physics!, _mission, _sounds, droppedCraft, fuel, id, Flight)
        {
            State = FlightState.Flying,
            _at = _at, // the new vessel's time, from this one's: it was brought to the same moment
            Site = Site,
            LegDeploy = LegDeploy,
            _legsWanted = _legsWanted,
            BayDoors = BayDoors, // its bay, if it has one, is the bottom stage's too
            _bayDoorsWanted = _bayDoorsWanted,
            _hasBeenAirborne = true,
            _wasTouching = false,
            MaxAltitude = Altitude,
            MaxGround = Ground,
        };
        _physics!.SetLegs(droppedCraft, LegDeploy);
        _ignitionDelay = Scene.StageIgnitionSeconds;
        _legsWanted = false;
        LegDeploy = 0f;
        _bayDoorsWanted = false;
        BayDoors = 0f;
        Refresh();
        _sounds.Add(new SoundEvent(Position, 1f, SoundCue.LegsLocked, Body));
        return [dropped];
    }

    /// <summary>
    /// The fairing comes off in two halves, each pushed away to its own side and tumbling outwards,
    /// as vessels of their own (debris: they only fall). The payload is bare from now on.
    /// </summary>
    private Vessel[] JettisonFairing(int id)
    {
        _physics!.JettisonFairing(_craft);
        Refresh(); // that woke it: frozen aboard a raft, it now moves as the raft does, and so do the halves
        var rotation = _physics!.Rotation(_craft);
        var spin = _physics!.Up(_craft);
        // How the whole moved as they came off: each half goes on as it moved as part of it (its
        // share of the turning too, as stages part), and turns as it did, its tumble besides.
        var (centre, turning) = (_physics!.CentreOfMass(_craft), _physics!.AngularVelocity(_craft));
        var bottom = Vector3.Transform(new Vector3(0, RocketGeometry.StageBase(Vehicle, Stage, TopStage), 0), _model);
        var halves = new Vessel[2];
        var half = GameOne.Vehicle.HalfOf(Fairing);
        for (var i = 0; i < 2; i++)
        {
            // Each half's shell is on its own +Z side; the second is the first turned half a turn.
            var turned = rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * MathF.PI); // the half turn first, in its own space
            var outward = Vector3.Transform(Vector3.UnitZ, turned);
            var tumble = Vector3.Cross(spin, outward) * 0.8f; // its top falls away first
            var craft = _physics!.AddLoose(half, bottom, turned, Velocity + outward * Scene.FairingSeparationSpeed, turning + tumble, partingFrom: _craft);
            _physics!.GoOnAsPartOf(craft, centre, Velocity, turning, outward * Scene.FairingSeparationSpeed);
            halves[i] = new Vessel(_worlds, _physics!, _mission, _sounds, craft, [0f], id + i, Flight)
            {
                State = FlightState.Flying,
                _at = _at, // each half's time, from this vessel's
                Site = Site,
                _hasBeenAirborne = true,
                _wasTouching = false,
                MaxAltitude = Altitude,
                MaxGround = Ground,
            };
        }
        _sounds.Add(new SoundEvent(bottom, 1f, SoundCue.LegsLocked, Body));
        return halves;
    }

    /// <summary>
    /// Steering: turns the aim towards a held direction, or the way the pilot leans, or along the
    /// direction of travel if prograde hold is on (or against it, retrograde: both by
    /// <paramref name="holdVelocity"/>, its velocity in the frame in use, so a held nose turns as
    /// the frame does, switch and all), only as fast as <see cref="TurnRate"/> allows. Either hold
    /// lets go when the pilot steers.
    /// Then the air has its say (see <see cref="Weathervane"/>), so in thick, fast air it can only be
    /// leaned so far off the direction of travel. Above the air the aim is kept among the stars, so
    /// over the ground it turns back by the planet's turn: a craft left alone there keeps its
    /// attitude among the stars, as its physics does (see <see cref="RocketPhysics"/>' Fall). In
    /// the air it is kept over the ground, which the air turns with.
    /// </summary>
    private void Steer(float dt, Vector3 lean, Vector3? hold, Vector3 holdVelocity, float holdFloor)
    {
        if (Body.AboveAir(_centreOfMass))
            _aim = Body.TurnedDirection(_aim, -dt);
        var amount = MathF.Min(lean.Length(), 1f);
        if (hold is { } direction && direction.LengthSquared() > 1e-8f)
        {
            ProgradeHold = RetrogradeHold = false; // the pilot has taken over
            TurnAimTowards(direction, TurnRate * dt);
        }
        else if (amount > 1e-4f)
        {
            ProgradeHold = RetrogradeHold = false;
            TurnAimTowards(lean, TurnRate * amount * dt);
        }
        else if (ProgradeHold && holdVelocity.Length() > holdFloor)
            TurnAimTowards(holdVelocity, TurnRate * dt);
        else if (RetrogradeHold && holdVelocity.Length() > holdFloor) // slower than that, the nose stays where it is
            TurnAimTowards(-holdVelocity, TurnRate * dt);

        Weathervane(dt);
    }

    /// <summary>
    /// The air turns the rocket to fly straight through it, as an arrow's flights do: nose first
    /// along its direction of travel, or tail first if it is falling backwards (as a booster
    /// coming back down does), whichever it is closer to. It turns harder the thicker the air, the
    /// faster the rocket and the more side-on it is. This is what makes a gravity turn: tip the
    /// rocket over a little, let go, and the air keeps its nose along the curving path. Above the
    /// atmosphere nothing turns it.
    /// </summary>
    private void Weathervane(float dt)
    {
        var speed = Velocity.Length();
        if (speed < 1f)
            return;
        var flow = Velocity / speed;
        if (Vector3.Dot(_aim, flow) < 0f)
            flow = -flow;
        var sideOn = Vector3.Cross(_aim, flow).Length();
        var pressure = Body.AirDensity(Body.Altitude(Position)) * speed * speed;
        TurnAimTowards(flow, MathF.Min(Scene.AeroStability * pressure * sideOn, Scene.AeroMaxTurnRate) * dt);
    }

    /// <summary>Turns the aim towards <paramref name="direction"/> by up to <paramref name="angle"/> radians.</summary>
    private void TurnAimTowards(Vector3 direction, float angle)
    {
        var target = Vector3.Normalize(direction);
        var between = MathF.Acos(Math.Clamp(Vector3.Dot(_aim, target), -1f, 1f));
        if (between < 1e-5f)
            return;
        var side = target - _aim * Vector3.Dot(target, _aim);
        if (side.LengthSquared() < 1e-10f)
            side = Vector3.Cross(_aim, MathF.Abs(_aim.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ);
        var step = MathF.Min(angle, between);
        _aim = Vector3.Normalize(_aim * MathF.Cos(step) + Vector3.Normalize(side) * MathF.Sin(step));
    }

    /// <summary>Swings the legs towards where they are wanted, and swaps the collider once they lock.</summary>
    internal void MoveLegs(float dt)
    {
        var target = _legsWanted ? 1f : 0f;
        if (LegDeploy == target || State == FlightState.Crashed)
            return;

        var step = dt / Scene.LegDeploySeconds;
        LegDeploy = LegDeploy < target ? MathF.Min(target, LegDeploy + step) : MathF.Max(target, LegDeploy - step);

        // The feet only take its weight once the legs are fully out, and stop as soon as they fold.
        // Under formula they swing all the same (drawn so: FormulaParts), with no body to set.
        Physics?.SetLegs(_craft, LegDeploy);
        if (LegDeploy == target)
            _sounds.Add(new SoundEvent(Vector3.Transform(new Vector3(0, Legs.HingeHeight, 0), _model), 1f, SoundCue.LegsLocked, Body));
    }

    /// <summary>
    /// Swings its bay's doors towards where they are wanted (open or shut, over
    /// <see cref="Scene.LanderDoorSeconds"/>), and swaps the collider with the ramps in once they
    /// are fully open, out again as they start to close (see <see cref="RocketPhysics.SetDoors"/>).
    /// Each door swings to its stop (<see cref="Craft.DoorStops"/>): where its ramp comes down
    /// onto the ground, found as they begin to open, or their full swing if nothing is in the way.
    /// On a slope, or with its legs pushed up by its weight or a firm touchdown, a ramp at the full
    /// swing would be in the ground, and lift the lander off it. Doors opened before it comes down
    /// leave the ramps out of the collider from its touchdown until it has landed, so it comes
    /// down on its legs as with them shut; then their stops are found again, and a door that would
    /// be in the ground swings up onto it, at the doors' own pace (their full swing in
    /// <see cref="Scene.LanderDoorSeconds"/>), never in a jump; the ramps go back in once both are
    /// there. They are found again, from the full swing, each time it comes to rest landed too:
    /// one set down upright under formula on a slope and handed back leans onto it on its legs,
    /// and a ramp raised for the upright pose comes back down onto the ground. They lock with a
    /// clunk, open or shut.
    /// </summary>
    internal void MoveDoors(float dt)
    {
        if (State == FlightState.Crashed)
            return;
        var stops = _craft.DoorStops;
        var wanted = _stopsWanted ?? stops;
        var resting = State == FlightState.Landed && (OnFormula || _craft.Frozen);
        if (State == FlightState.Landed && (!_doorsLanded || resting && !_doorsRested) && BayDoors > 0f)
            wanted = FindStops(new Vector2(RocketGeometry.DoorOpenRadians), onlyLower: true); // just landed or come to rest: a ramp that would be in the ground raised onto it, the others at the full swing
        (_doorsLanded, _doorsRested) = (State == FlightState.Landed, resting);
        var target = _bayDoorsWanted ? 1f : 0f;
        var moved = BayDoors != target;
        if (moved)
        {
            if (BayDoors == 0f)
                stops = wanted = FindStops(new Vector2(RocketGeometry.DoorOpenRadians), onlyLower: false); // beginning to open: where each will come to rest
            var step = dt / Scene.LanderDoorSeconds;
            BayDoors = BayDoors < target ? MathF.Min(target, BayDoors + step) : MathF.Max(target, BayDoors - step);
        }
        // A stop found again (as it lands) is swung to at the doors' pace, so neither door jumps.
        var most = RocketGeometry.DoorOpenRadians / Scene.LanderDoorSeconds * dt;
        stops = new Vector2(Toward(stops.X, wanted.X, most), Toward(stops.Y, wanted.Y, most));
        _stopsWanted = stops == wanted ? null : wanted;
        // The ramps are out while it comes down onto the ground, and while a door swings to its new stop.
        var ramps = !(State == FlightState.Flying && _touchedDown) && _stopsWanted is null;
        if (!moved && stops == _craft.DoorStops && (BayDoors >= 1f && ramps) == _craft.DoorsOpen)
            return;
        // Under formula they swing all the same (drawn so: FormulaParts), with no body to set.
        if (Physics is { } physics)
            physics.SetDoors(_craft, BayDoors, stops, ramps);
        else
            (_craft.BayDoors, _craft.DoorStops, _craft.DoorsOpen) = (BayDoors, stops, BayDoors >= 1f && ramps && HasBay);
        if (moved && BayDoors == target)
            _sounds.Add(new SoundEvent(Vector3.Transform(new Vector3(0, Scene.LanderBayFloor, 0), _model), 1f, SoundCue.LegsLocked, Body));
    }

    // Whether its doors' stops have been found again since it last landed (see MoveDoors). Not
    // saved: a vessel loaded landed has them looked at once more, from the pose it was saved in,
    // which finds any ramp still to be raised (a save made while one swung to its new stop).
    private bool _doorsLanded;

    // Whether it was at rest landed (frozen, or under formula) as its doors were last moved, so
    // their stops are found again as it next comes to rest (see MoveDoors). Not saved, as _doorsLanded.
    private bool _doorsRested;

    // The stops its doors are swinging to, while either is still on its way (see MoveDoors); none once there.
    private Vector2? _stopsWanted;

    private static float Toward(float from, float to, float most) => from + Math.Clamp(to - from, -most, most);

    /// <summary>
    /// Each door's stop, as it stands now: where its ramp, straight and swung out from shut, first meets the
    /// ground, up to <paramref name="from"/>'s (or the full swing); with <paramref name="onlyLower"/>,
    /// only those in the ground now are moved, and a hair over it (so one resting on it stays).
    /// </summary>
    private Vector2 FindStops(Vector2 from, bool onlyLower)
    {
        var stops = from;
        for (var door = 0; door < RocketGeometry.DoorCount; door++)
            if (DoorEdgeClearance(door, from[door]) < (onlyLower ? -0.005f : 0f))
                stops[door] = DoorMeetsGround(door, from[door]);
        return stops;
    }

    /// <summary>
    /// How far bay door <paramref name="door"/>'s ramp, straight and swung <paramref name="radians"/>
    /// out, is over the ground at its lowest: along its length, every half panel out to its far
    /// edge, each across it at its two ends and its middle (a hump under the middle of a ramp
    /// 4.8 m long holds it up as its far edge would).
    /// </summary>
    private float DoorEdgeClearance(int door, float radians)
    {
        const int along = 2 * RocketGeometry.PanelCount;
        var model = RocketGeometry.DoorAt(door, radians) * _model;
        var lowest = float.MaxValue;
        for (var i = 1; i <= along; i++)
            foreach (var z in (ReadOnlySpan<float>)[-RocketGeometry.PanelHalfWidth, 0f, RocketGeometry.PanelHalfWidth])
                lowest = MathF.Min(lowest, Body.GroundAltitude(Vector3.Transform(new Vector3(0f, RocketGeometry.RampLength * i / along, z), model)));
        return lowest;
    }

    /// <summary>How far out bay door <paramref name="door"/> swings before its ramp (straight) meets the ground, given that it does by <paramref name="below"/> (radians from shut).</summary>
    private float DoorMeetsGround(int door, float below)
    {
        var (clear, touching) = (0f, below);
        for (var i = 0; i < 16; i++) // to well under a millimetre at its edge
        {
            var middle = (clear + touching) / 2f;
            if (DoorEdgeClearance(door, middle) < 0f)
                touching = middle;
            else
                clear = middle;
        }
        return clear;
    }

    /// <summary>After the physics has moved it to world time <paramref name="now"/>: where it is now, its orbit, and whether it has landed or crashed.</summary>
    internal void AfterPhysics(float dt, double now)
    {
        _at = now;
        if (State == FlightState.Crashed)
            return; // a wreck isn't judged any more: it's gone (its body soon after: see RocketPhysics.Wreck)
        if (OnFormula)
        {
            // Its body is out of the world, so there is nothing of Jolt's to read, and nothing it
            // touched to judge: it is where the formula has it (Formula.Advance). Its apoapsis and
            // periapsis are its orbit's, the same all along it (worked out as it left: see LeavePhysics).
            if (State == FlightState.Flying)
            {
                MaxAltitude = MathF.Max(MaxAltitude, Altitude);
                MaxGround = MathF.Max(MaxGround, Ground);
                if (OrbitStable && !EngineLit)
                    AchievedOrbit = true;
            }
            _autoFrame = Frames.Auto(Body, _autoFrame, Periapsis, State is FlightState.Landed or FlightState.Floating);
            return;
        }
        if (State is FlightState.Landed or FlightState.Floating && _craft.Frozen)
        {
            // Nothing has moved it, so it is as it was: no need to measure it again. Unless it is
            // frozen aboard a raft (the drone ship's deck, or a floe), which carries it: then it is where the raft has it.
            if (_craft.Aboard is not null)
                Refresh();
            _wasFrozen = true;
            return;
        }
        Refresh();
        var woken = _wasFrozen;
        if (_wasFrozen)
        {
            // Just woken: its last velocity and spin are from before it froze, which say nothing of
            // this step, so its touches and its touching the water are judged from how it moves now.
            (_lastVelocity, _wasFrozen) = (Velocity, false);
            if (!_craft.Removed)
                _lastSpin = _physics!.AngularVelocity(_craft);
        }
        Velocity = _physics!.Velocity(_craft);
        (Apoapsis, Periapsis) = Body.Orbit(CentreOfMass, Body.AmongStars(CentreOfMass, Velocity));
        if (State == FlightState.Flying && OrbitStable && !EngineLit)
            AchievedOrbit = true;
        Timings.Lap(StepPart.Rules);
        var started = Timings.Start();
        CheckContact(dt, woken);
        // Handed back by the formula, it has played out what the formula couldn't once it is
        // landed, afloat or wrecked: flying again later (its engine relit), it is like any other.
        if (State != FlightState.Flying)
            _playingOut = false;
        Timings.Stop(StepDetail.Vessels, started);
        Timings.Lap(StepPart.Contact);
        // After the contact, so a vessel that has just come down is in the surface frame from that
        // step, and lifts off again from it.
        _autoFrame = Frames.Auto(Body, _autoFrame, Periapsis, State is FlightState.Landed or FlightState.Floating);
        _lastVelocity = Velocity;
        if (!_craft.Removed)
            _lastSpin = _physics!.AngularVelocity(_craft);
        // Coming down in the water, it is kept awake until it has settled to floating (or is lost),
        // however far off the vessel being flown is: only then may it freeze afloat.
        _craft.Settling = State == FlightState.Flying && InWater;
    }

    /// <summary>Reads where its body is now, kept for after it has been destroyed.</summary>
    private void Refresh()
    {
        if (_craft.Removed)
            return;
        _model = _physics!.Model(_craft);
        _centreOfMass = _physics!.CentreOfMass(_craft);
        Velocity = _physics!.Velocity(_craft);
    }

    /// <summary>
    /// Whether it is on the ground and how it got there: landed (however it lies) or crashed. What
    /// it touched is Jolt's contacts (see <see cref="RocketPhysics.TouchesBegun"/>): each touchdown, and
    /// the tower and the masts, judged where it touched and by how fast the two closed there.
    /// Where its lowest points and its hull are says whether it is still on the ground or has left
    /// it, and what it stands on, and judges its body (and its bell, without legs) rocking down
    /// about a contact it already has, which Jolt gives no new contact for. <paramref name="woken"/>: it was frozen until this update.
    /// </summary>
    /// <remarks>
    /// Each contact is judged by what it is with. Touching the water, each part of it is judged by
    /// how fast that part was moving (<see cref="Scene.SafeSplashdownSpeed"/>), the first the
    /// splashdown and any later one (its nose, as it tips over) by how fast it comes down side-on
    /// (see <see cref="TouchTheWater"/>). The seabed is ground: touching it, or lying on it, the
    /// ground's rules hold (the touchdown limits). Clear of it, in the
    /// water, those are left out, so lying on its side in the water is floating. Resting on
    /// a shallow seabed, it lands as on the ground; afloat clear of it, and settled, it floats.
    /// A hit by another vessel (Jolt's contacts: see <see cref="RocketPhysics.TouchesBegun"/>) is held
    /// until the touchdown in the same step is judged (see <see cref="JudgeContact"/>): wrecked by
    /// that, it was the ground's.
    /// </remarks>

    private void CheckContact(float dt, bool woken)
    {
        (_heldHit, _heldHitCraft) = (null, null);
        JudgeContact(dt, woken);
        if (_heldHit is { } hit && State != FlightState.Crashed)
        {
            HitCraft = _heldHitCraft;
            Crash(HitAnotherVessel(hit));
        }
    }

    /// <summary>A hit by another vessel this step, held while the touchdown it came with is judged (see <see cref="CheckContact"/>), and that one's body (the hardest hit).</summary>
    private float? _heldHit;
    private Craft? _heldHitCraft;

    /// <summary><see cref="CheckContact"/>, but for a hit held over a touchdown (<see cref="_heldHit"/>).</summary>
    private void JudgeContact(float dt, bool woken)
    {
        var touches = _physics!.TouchesBegun(_craft);
        // Its hull against the ground: only looked at once some of it could be near the ground
        // (the points are many), so one high up costs next to nothing; worked out once a step, for all below.
        var (hullMayTouch, plain, farOff) = HullMayTouch();
        // Its lowest points against the ground, likewise: measured closely only near the ground,
        // or touching anything (Jolt knows better than any reckoning from afar). Far from it, and
        // touching nothing, nothing of it can be within AirborneClearance, so every rule below
        // reads the same from MeasureFar's few lookups: in orbit, the eight points read as
        // anywhere (pads, zones, rafts) were most of a craft's contact.
        var far = farOff && touches.Count == 0 && !_physics!.TouchingAnything(_craft);
        var (lowest, lowestAt, legsOut) = far ? MeasureFar() : Measure();
        var (hullClearance, hullAt) = hullMayTouch ? KeptHullContact(plain) : (float.MaxValue, Position);
        // Its movement against the ground under it, which on a raft (the drone ship's deck, a floe) moves too:
        // standing still is measured against that. What it is on
        // is what is under its lowest point of all, its feet or bell or its hull's (lying across
        // the deck's edge, its base out past it, that is its body on the deck): a raft if that
        // point is over one, else the still ground (never a raft that only some other part of it
        // is over, on solid ground beside it). The raft's velocity is taken at the centre of mass,
        // the point Velocity is for. (A touchdown is judged against what it touched: see Touch.Closing.)
        var raft = far || !Body.HasSea ? null : Rafts.Under(hullClearance < lowest ? hullAt : lowestAt)?.Raft; // far: only the ground is near (see HullMayTouch)
        var ground = raft?.VelocityAt(_centreOfMass) ?? Vector3.Zero;
        _ground = ground;
        var water = TouchTheWater(legsOut);
        var inWater = InWater;
        if (State == FlightState.Flying)
        {
            MaxAltitude = MathF.Max(MaxAltitude, Altitude);
            MaxGround = MathF.Max(MaxGround, Ground);
        }

        // Touching another vessel faster than it can take, whatever it was doing (flying, landed,
        // afloat, or waiting on the pad): judged by how fast the two closed where they touched, as
        // Jolt's contact began, whatever their masses, so both are wrecked (the other judges its
        // own touch the same). Only a contact can be a hit, so a hard manoeuvre beside another
        // never is. The hit is held until the touchdown has been judged (see CheckContact): a hard
        // touchdown in the same step is the ground's, judged by the touchdown limits. So is any
        // crash judged after this in the step (a touchdown, the tower, a mast, the water, the
        // fairing's air): it takes the hit's place, with its own reason; only the ground's hard
        // touch where it already touched (ImpactSpeed) gives way to the hit.
        if (!IsFairing)
        {
            var (hit, with) = (0f, (Craft?)null);
            foreach (var touch in touches)
                if (touch.With == SolidKind.Craft && touch.ClosingSpeed > hit)
                    (hit, with) = (touch.ClosingSpeed, touch.Other);
            if (hit > ImpactTolerance)
                (_heldHit, _heldHitCraft) = (hit, with);
        }

        switch (State)
        {
            case FlightState.Landed when inWater && lowest > AirborneClearance && water.Seabed >= TouchingClearance:
                // Landed in shallow water, it stands there until the water lifts it clear of the seabed.
                State = FlightState.Floating;
                LandedOnTarget = false;
                return;
            case FlightState.Landed:
                // A landing is a stop, not an end: moved (pushed, its legs stowed), it is flying again
                // (as one whose engine lights is: see TakeOff), judged by the same rules as anything
                // else that moves, until it settles once more.
                if ((Velocity - ground).Length() < StillSpeed)
                    return;
                State = FlightState.Flying;
                _settledFor = 0f;
                break;
            case FlightState.Floating:
                if (!Splash(water)) // the waves can still roll it over too hard
                    return;
                // Lifted out of the water (its engine lighting is TakeOff's), the water no longer holds it: it is flying
                // again, as a landed one moved. Resting on the seabed instead (a wave's trough
                // bringing it down onto it only for a moment doesn't count), it has landed there:
                // straight from floating, not back through flying, or the waves coming and going
                // in shallow water would throw it back and forth between the two.
                if (!InWater)
                {
                    State = FlightState.Flying;
                    _floatingFor = 0f;
                    break;
                }
                var resting = (lowest < TouchingClearance || water.Seabed < TouchingClearance) && (Velocity - ground).Length() < StillSpeed;
                _settledFor = resting ? _settledFor + dt : 0f;
                if (_settledFor >= Scene.LandedSettleSeconds)
                    (State, LandedOnTarget) = (FlightState.Landed, false);
                return;
            case not FlightState.Flying:
                return;
        }
        // Its touchdowns are judged while it flies (from the update it starts to move in, landed or
        // afloat, so a touch in that update counts), but not as it wakes: Jolt ends a frozen craft's
        // contacts, and begins them again as it wakes, though it never left what it stands on
        // (lifting off, or pushed from where it stood, it is still on it).
        var judgingTouchdowns = State == FlightState.Flying && !woken;

        if (IsFairing)
        {
            // A fairing half has nothing to land on, and nothing to blow up: once it is down
            // (it has touched anything at all, or is in the sea), it is just taken out of the world,
            // told by what it came down on (the first thing it touched).
            if (water.First)
                _sounds.Add(new SoundEvent(water.At, water.Speed, SoundCue.Splash, Body));
            if (inWater)
                Crash(FairingFell(sea: true, null), explode: false);
            else if (touches.Count > 0)
                Crash(touches[0] switch
                {
                    { With: SolidKind.Craft } => "Fell onto another vessel.",
                    { With: SolidKind.Raft } => FairingFell(sea: false, touches[0].Raft),
                    { With: SolidKind.Tower } => TowerReason,
                    { With: SolidKind.Mast } => MastReason,
                    _ => FairingFell(sea: false, null),
                }, explode: false);
            return;
        }

        // A payload whose fairing has come off can't take thick, fast air.
        if (BreaksUpIn(Position, Velocity))
        {
            Crash(BreakUpReason);
            return;
        }

        // The service tower and the light masts wreck it on any touch, however gentle.
        foreach (var touch in touches)
            if (touch.With == SolidKind.Tower)
            {
                Crash(TowerReason);
                return;
            }
        foreach (var touch in touches)
            if (touch.With == SolidKind.Mast)
            {
                Crash(MastReason);
                return;
            }

        if (!Splash(water))
            return;

        // On the solid ground (a shallow seabed included), by its feet or bell or lying on it, the
        // ground's rules hold, in the water or out of it: only clear of the ground is the water's
        // limit all that counts. (Its hull: hullClearance, above.)
        var touching = lowest < TouchingClearance;
        var hull = hullClearance < TouchingClearance;
        var contact = touching || hull;
        var aground = contact || water.Seabed < TouchingClearance; // on it, not just near: a wave's trough mustn't ground it

        if (lowest > AirborneClearance && !hull)
        {
            _hasBeenAirborne = true;
            // Flying clear of the ground under its own power, whatever it comes down on next is a
            // landing of its own. (A bounce, a fall or a hop lower than that is still the same one.)
            if (EngineLit)
                (_touchedDown, _vesselTouch, _lowSounded, _hullSounded) = (false, null, false, false);
        }
        // Touching another vessel rather than the ground: the hardest such touch in the landing is
        // kept, as one of its touches should it come to rest on another (see JudgeTouch): each at how
        // fast the two closed where they touched (one over the tolerance was a hit, above). Not while
        // it is on the ground, where its touchdown is the ground's, or in the water, the water's.
        if (!contact && !inWater && _hasBeenAirborne)
            foreach (var touch in touches)
                if (touch.With == SolidKind.Craft && touch.ClosingSpeed <= ImpactTolerance)
                    _vesselTouch = MathF.Max(_vesselTouch ?? 0f, touch.ClosingSpeed);

        // Touchdowns: each part as it touches the ground or a raft (its feet or bell, and its body,
        // whichever way up it is: see Touch.Fresh), so one that stands on its feet and then
        // falls over is judged again as its side comes down, but sliding from one ground tile onto
        // the next is no new touch. Judged on how fast that point closed with what it touched, just
        // before it hit (a skid along it counts): a tall one falling over by its nose, not its middle.
        // (_wasTouching and _hullWasTouching: whether its lowest points, and its body's, were on the ground at the last check.)
        var hullDown = hull && !_hullWasTouching;
        var bellDown = touching && !_wasTouching && !legsOut; // its lowest point, without legs, is its bell's rim
        _wasTouching = touching;
        _hullWasTouching = hull;
        if (judgingTouchdowns && _hasBeenAirborne)
        {
            var (hardest, bell) = (0f, 0f);
            // Its body coming down, and (its legs not out) its bell, are also judged as the lowest
            // point of each comes onto the ground (HullContact's, and Measure's bell rim), at how
            // fast that point was moving against what is under it: a part rocking down about a
            // contact already held gives Jolt no new contact (it reports one contact for each
            // piece of it, carrying on, its point at the pivot), so a body tipping onto its side,
            // or onto its bell from its body's rim, far end first, would go unjudged. (Its feet
            // are the touches' alone: see RocketPhysics.FeetTouch.)
            float Speed(Vector3 point) => (_lastVelocity + Vector3.Cross(_lastSpin, point - _centreOfMass) - (raft?.VelocityAt(point) ?? Vector3.Zero)).Length();
            // One touch is often judged twice (its lowest point coming within reach of the ground,
            // then Jolt's contact an update or so later), and its body's rings take in its bell, so
            // it makes one sound an update, at its hardest touch, and none for a part's contact
            // once that part's lowest point has made one this landing: one touch, one sound.
            var (lowSounded, hullSounded) = (_lowSounded, _hullSounded);
            var (loudest, soundAt) = (-1f, Vector3.Zero);
            void Sound(Vector3 at, float speed)
            {
                if (speed > loudest)
                    (loudest, soundAt) = (speed, at);
            }
            if (hullDown)
            {
                var speed = Speed(hullAt);
                JudgeTouch(speed);
                Sound(hullAt, speed);
                (hardest, _hullSounded) = (speed, true);
            }
            if (bellDown)
            {
                var speed = Speed(lowestAt);
                JudgeTouch(speed);
                Sound(lowestAt, speed);
                (hardest, bell, _lowSounded) = (MathF.Max(hardest, speed), speed, true);
            }
            foreach (var touch in touches)
            {
                if (!touch.Fresh || touch.With is not (SolidKind.Ground or SolidKind.Raft))
                    continue;
                var speed = touch.ClosingSpeed;
                JudgeTouch(speed);
                if (!(touch.Part == CraftPart.Hull ? hullSounded : lowSounded))
                    Sound(touch.Point, speed);
                hardest = MathF.Max(hardest, speed);
                if (touch.Part == CraftPart.Bell)
                    bell = MathF.Max(bell, speed);
            }
            if (loudest >= 0f)
                _sounds.Add(new SoundEvent(soundAt, loudest, SoundCue.Touchdown, Body));

            // On its legs, or (gently: there is nothing to soften it) on its engine bell, as it stands on the pad.
            if (hardest > Scene.TouchdownLimit(Body, Scene.SafeTouchdownSpeed))
            {
                TouchdownSpeed = hardest;
                Crash(HitTheGround(hardest, Body));
                return;
            }
            if (!legsOut && bell > Scene.TouchdownLimit(Body, Scene.SafeBellTouchdownSpeed))
            {
                TouchdownSpeed = bell;
                Crash(OnItsBell(bell));
                return;
            }

            // Touching the ground or a raft hard where that part is on it already (sliding on its side
            // or its legs into a pad's, a footing's or a zone's side, the drone ship's hull or a
            // floe's edge) is no new touch, so no touchdown, but it is a hit: touched faster than
            // ImpactSpeed straight into it (as the sudden stop it once was judged by), it is the
            // ground's. Only the speed into it counts, so sliding over a tile's seam (touched side-on) never
            // is. A hit by another vessel held this step keeps its own reason (see CheckContact).
            if (_heldHit is null)
                foreach (var touch in touches)
                    if (!touch.Fresh && touch.With is SolidKind.Ground or SolidKind.Raft
                        && -Vector3.Dot(touch.Closing, touch.Normal) > ImpactSpeed)
                    {
                        TouchdownSpeed = touch.ClosingSpeed;
                        Crash(HitTheGround(TouchdownSpeed, Body));
                        return;
                    }
        }

        // Landed: still against what it rests on (the ground, or another vessel), with the engine off, for a moment, however it
        // lies (on its feet, its bell or its side: vessels come in many shapes, so which way up it
        // is isn't judged; how hard it came down already was). Floating: afloat clear of the
        // seabed with the engine off, and settled there, moving with the water, hardly turning,
        // and done tipping over (an upright float, top-heavy, only balances for a while), for as long.
        var still = _hasBeenAirborne && !EngineLit && (Velocity - ground).Length() < StillSpeed;
        // On the ground, or on something else that holds it up (another vessel: nothing but
        // being held up keeps it that still for that long), not afloat.
        var settled = still && (contact || _craft.Submerged <= 0f);
        var afloat = !aground && _craft.Submerged > 0f && _hasBeenAirborne && !EngineLit && SettledAfloat();
        _settledFor = settled ? _settledFor + dt : 0f;
        _floatingFor = afloat ? _floatingFor + dt : 0f;

        if (_settledFor >= Scene.LandedSettleSeconds)
        {
            State = FlightState.Landed;
            // At rest on another vessel, not on the ground: it came down on that one, so its touch
            // on it is one of its touches (one too gentle to tell, under StillSpeed, as soft as can be).
            if (!contact)
                JudgeTouch(_vesselTouch ?? 0f);
            LandedOnTarget = LandingSite is not null && DistanceToLandingSite <= Scene.LandingTargetRadius;
            (_touchedDown, _vesselTouch, _lowSounded, _hullSounded) = (false, null, false, false); // settled: its landing's touches are over
        }
        else if (_floatingFor >= Scene.LandedSettleSeconds)
            (State, _touchedDown, _vesselTouch, _lowSounded, _hullSounded) = (FlightState.Floating, false, null, false, false);
    }

    /// <summary>One of its landing's touches, at <paramref name="speed"/>: its touchdown is the hardest (see <see cref="TouchdownSpeed"/>).</summary>
    private void JudgeTouch(float speed)
    {
        TouchdownSpeed = _touchedDown ? MathF.Max(TouchdownSpeed, speed) : speed;
        _touchedDown = true;
    }

    /// <summary>
    /// Whether, afloat, it has settled: moving through the water and turning no faster than the
    /// waves alone keep it (<see cref="Scene.FloatSettledSpeed"/> and
    /// <see cref="Scene.FloatSettledTurnRate"/>, for waves up to a metre high, and in proportion in
    /// rougher seas), and done tipping over. The water holds it up along its axis (a rod: see
    /// <see cref="RocketPhysics.Float"/>), so with its centre of mass above its centre of buoyancy
    /// it can only be still to tip over; at rest the two are together.
    /// </summary>
    private bool SettledAfloat()
    {
        var waves = Body.HasSea ? MathF.Max(1f, Sea.WaveScale(Body.Up(_centreOfMass))) : 1f;
        return (Velocity - _physics!.FlowAt(_centreOfMass)).Length() < Scene.FloatSettledSpeed * waves
            && AngularVelocity.Length() < Scene.FloatSettledTurnRate * waves
            && Vector3.Dot(_centreOfMass - _craft.Buoyancy, LocalUp) < Scene.FloatTopHeavy;
    }

    /// <summary>
    /// Judges a touch with the water (see <see cref="TouchTheWater"/>): a part that touched it faster
    /// than <see cref="Scene.SafeSplashdownSpeed"/> wrecks it, and it sinks; otherwise the first
    /// part in is the splashdown, and its speed the touchdown's. Either way, there is a splash
    /// (but not for a part merely dipping back in with the waves). False if it was lost.
    /// </summary>
    private bool Splash((float Speed, Vector3 At, bool First, float Seabed, float Against) water)
    {
        // As one of its touches (see JudgeTouch): the first part in, its splashdown, by its whole speed;
        // any after it only by how hard it slaps the water side-on, against the water's own flow,
        // so its tipping over counts but the waves rocking it back under don't. Not afloat.
        if (State == FlightState.Flying && (water.First || water.Speed > 0f))
            JudgeTouch(water.First && !_touchedDown ? water.Speed : water.Against);
        if (water.First || water.Speed >= Scene.SplashSpeed)
            _sounds.Add(new SoundEvent(water.At, water.Speed, SoundCue.Splash, Body));
        if (water.Speed <= Scene.SafeSplashdownSpeed)
            return true;
        TouchdownSpeed = water.Speed;
        // It breaks up rather than blowing up: the water puts out any fire, and it sinks out of sight.
        Crash(HitTheWater(water.Speed), explode: false);
        return false;
    }

    /// <summary>
    /// The water at the points it is watched at: its feet hanging at full stroke (or its bell's
    /// rim), as <see cref="Measure"/> finds its lowest, and up its body to its top, the lowest point
    /// round it at each of <see cref="Scene.WaterSamples"/> places, whichever way it lies. Which are
    /// under the waves is kept (<see cref="_wetParts"/>); one over a raft (a floe, the deck) is on
    /// it, never in the water (see <see cref="RocketPhysics.DepthUnder"/>). Returns how fast the fastest of those
    /// that have just gone under touched the water (as they were moving the frame before, and after
    /// the first, only across its length; 0 if none did), where on its surface, whether none of it
    /// was in the water before (a splashdown), and how far the lowest of its body's points under
    /// the water is above the seabed; and how hard the hardest of those that have just gone under
    /// slapped it side-on, across its length and against the water's own flow (0 if none did).
    /// </summary>
    private (float Speed, Vector3 At, bool First, float Seabed, float Against) TouchTheWater(bool legsOut)
    {
        var was = _wetParts;
        _wetParts = 0;
        // All of it above the highest wave of any sea (as each point below would find, one by one): none of it is in the water.
        var bell = CurrentStage.BellHeight;
        var height = Height;
        if (!Body.HasSea || Body.Altitude(Vector3.Transform(new Vector3(0, (height - bell) / 2f, 0), _model)) > Sea.Highest + Reach)
            return (0f, Vector3.Zero, false, float.MaxValue, 0f);
        var model = _model;
        var axis = Up;
        var bottoms = legsOut ? RocketGeometry.LegCount : BellRimPoints;
        var (fastest, at, seabed, against) = (0f, Vector3.Zero, float.MaxValue, 0f);
        for (var i = 0; i < BellRimPoints + Scene.WaterSamples; i++)
        {
            Vector3 point;
            if (i < BellRimPoints)
            {
                if (i >= bottoms)
                    continue;
                point = Vector3.Transform(legsOut ? RocketGeometry.Foot(Legs, i) - Vector3.UnitY * Legs.Stroke : RocketGeometry.BellRim(CurrentStage, i, bottoms), model);
            }
            else
            {
                var y = -bell + (height + bell) * (i - BellRimPoints + 1) / Scene.WaterSamples;
                point = Vector3.Transform(new Vector3(0, y, 0), model);
                var down = -Body.Up(point);
                var across = down - axis * Vector3.Dot(down, axis);
                if (across.LengthSquared() > 1e-6f)
                    point += Vector3.Normalize(across) * RadiusAt(y);
            }
            if (Body.Altitude(point) > Sea.Highest)
                continue; // above the highest wave of any sea
            if (_physics!.DepthUnder(point) is not { } depth || depth <= 0f)
                continue;
            _wetParts |= 1 << i;
            if (i >= BellRimPoints)
                seabed = MathF.Min(seabed, Body.GroundAltitude(point)); // its body, lying on the seabed
            if ((was & (1 << i)) != 0)
                continue;
            // Once some of it is in, the rest slides in after it where it moves along its length
            // (plunging straight in, nose or tail first): only what slaps the water side-on counts.
            var moving = _lastVelocity + Vector3.Cross(_lastSpin, point - _centreOfMass);
            if (was != 0)
                moving -= axis * Vector3.Dot(moving, axis);
            var speed = moving.Length();
            if (speed >= fastest)
                (fastest, at) = (speed, point + Body.Up(point) * depth);
            // Against the water itself, which moves with the waves, and only side-on: what a touch
            // after the first is counted by (see Splash), so a part the waves rise round isn't one.
            var slap = moving - _physics!.WaterAt(point);
            against = MathF.Max(against, (slap - axis * Vector3.Dot(slap, axis)).Length());
        }
        // And its bottom end, where the rings above don't reach: the bell's rim, and the edge of the
        // body above it. Lying down tail low, as a booster floats, that end is what rests on the seabed.
        foreach (var (y, radius) in stackalloc (float, float)[] { (-bell, CurrentStage.BellRadius), (0f, CurrentStage.Radius) })
        {
            var point = Vector3.Transform(new Vector3(0, y, 0), model);
            var down = -Body.Up(point);
            var across = down - axis * Vector3.Dot(down, axis);
            if (across.LengthSquared() > 1e-6f)
                point += Vector3.Normalize(across) * radius;
            if (Body.Altitude(point) <= Sea.Highest && _physics!.DepthUnder(point) > 0f)
                seabed = MathF.Min(seabed, Body.GroundAltitude(point));
        }
        return (fastest, at, was == 0 && _wetParts != 0, seabed, against);
    }

    /// <summary>
    /// Whether any of its hull could be near what is under it, so is worth reading closely (see
    /// <see cref="HullContact"/>), from what is under points down its axis, however deep the gap
    /// under the rest (lying across the deck's side, its middle can be over sea far deeper than
    /// it is long). Where there is nothing but the ground near it (<see cref="Planet.OnlyGroundNear"/>,
    /// returned in <c>Plain</c>: read quickly, from the terrain alone), the ground rises
    /// no more steeply than three in one (as HullContact takes it): its middle clear by more than
    /// twice its length (and its radius) says so alone, so one high up costs a lookup or two; and
    /// otherwise its two ends and its middle, which every point of it is within a quarter of its
    /// length of, all clear by more than its length and radius, say none of it is near. Near a
    /// pad, a zone or a raft, whose sides are walls and the smallest a floe only
    /// <see cref="Scene.FloeMinRadius"/> round, the points are no further apart than such a floe is
    /// wide, so one can't lie unseen between them.
    /// </summary>
    /// <returns>
    /// That; whether only the ground is near; and whether nothing of it at all (its feet, swinging
    /// or out at full stroke, included) can be within <see cref="AirborneClearance"/> of the
    /// ground (<see cref="MeasureFar"/> is then enough): only the ground near, and its middle clear by
    /// more than four times its <see cref="Reach"/> (the reach itself straight down, and three in
    /// one across it) and that clearance.
    /// </returns>
    private (bool MayTouch, bool Plain, bool Far) HullMayTouch()
    {
        var bell = CurrentStage.BellHeight;
        var length = Height + bell;
        var bottom = Vector3.Transform(new Vector3(0, -bell, 0), _model);
        var top = Vector3.Transform(new Vector3(0, Height, 0), _model);
        var middle = (bottom + top) / 2f;
        var radius = _craft.Widest;
        var near = length + radius + TouchingClearance;
        var reach = Reach;
        // Asked over its whole reach (its feet's too), not only its hull's: what is plain for that is plain for its hull.
        if (Body.OnlyGroundNear(middle, reach))
        {
            var centre = Body.TerrainAltitude(middle);
            if (centre > 4f * reach + AirborneClearance)
                return (false, true, true);
            if (centre > 2f * length + radius + TouchingClearance)
                return (false, true, false);
            return (centre < near || Body.TerrainAltitude(bottom) < near || Body.TerrainAltitude(top) < near, true, false);
        }
        if (Body.BesideASide(middle, length / 2f + radius))
            return (true, false, false);
        var spans = Math.Max(2, (int)MathF.Ceiling(length / (2f * Scene.FloeMinRadius)));
        for (var i = 0; i <= spans; i++)
            if (Body.GroundAltitude(Vector3.Lerp(bottom, top, (float)i / spans)) < near)
                return (true, false, false);
        return (false, false, false);
    }

    /// <summary>
    /// How far from the middle of its axis anything of it reaches (metres): its ends and its widest
    /// radius (its fairing's, while it is on), or, if it has legs, a foot anywhere in their swing
    /// and at the bottom of its stroke (from the hinge, by the leg's length and the stroke).
    /// </summary>
    internal float Reach
    {
        get
        {
            var bell = CurrentStage.BellHeight;
            var radius = MathF.Max(_craft.WidestOpen, FairingOn ? Fairing.Radius : 0f);
            var reach = (Height + bell) / 2f + radius;
            if (HasLegs)
                reach = MathF.Max(reach, MathF.Abs((Height - bell) / 2f - Legs.HingeHeight) + radius + Legs.Length + Legs.Stroke);
            return reach;
        }
    }

    /// <summary>
    /// <see cref="HullContact"/>, but kept while it is landed and hasn't moved or tilted since it
    /// was measured (by <see cref="HullKeptWithin"/> anywhere along it): the ground under it
    /// doesn't move, so neither has its hull's lowest point, and a crowd standing awake (woken
    /// round a vessel coming down among them) costs a measure each only as each moves. Not near a
    /// raft, which moves under or beside it however still it stands.
    /// </summary>
    private (float Clearance, Vector3 At) KeptHullContact(bool plain)
    {
        var position = Position;
        var up = Up;
        var length = Height + CurrentStage.BellHeight;
        if (State == FlightState.Landed && _keptHull is { } kept
            && Vector3.Distance(position, kept.From) < HullKeptWithin
            && Vector3.Distance(up, kept.Up) * length < HullKeptWithin
            && !(Body.HasSea && Rafts.AnyNear(position, Reach + length)))
            return kept.Hull;
        var hull = HullContact(plain);
        _keptHull = State == FlightState.Landed ? (position, up, hull) : null;
        return hull;
    }

    /// <summary>Metres: moved or tilted less than this anywhere along it since its hull was measured, a landed vessel's measure is kept (see <see cref="KeptHullContact"/>).</summary>
    private const float HullKeptWithin = 0.001f;

    /// <summary>Its hull's last measure while landed (see <see cref="KeptHullContact"/>), and where it stood and which way it pointed then.</summary>
    private (Vector3 From, Vector3 Up, (float Clearance, Vector3 At) Hull)? _keptHull;

    /// <summary>
    /// How far the lowest point of its hull (its engine bell and body, end to end and all round)
    /// is above the solid thing under it (<see cref="Planet.GroundAltitude"/>): about 0 when it
    /// lies against the ground on its body, however it lies, even across an edge with its ends
    /// out over the drop, or against a pad's, a zone's or a raft's side. Read in rings half a
    /// metre apart down its length, twelve points round each, so a ring's sampled lowest is within
    /// 5 cm of its true lowest. Where what is under a ring is near enough a plane (anywhere but
    /// beside such a side: see <see cref="Planet.BesideASide"/>), only the few points round its
    /// lowest are read; and a ring whose middle is well clear of it (by four times its radius:
    /// only ground rising more steeply than three in one, within its radius, could come up to its
    /// side from there) is only read there, and taken as that less its radius, which is clear of
    /// it too: so one lying in deep water, not yet settled afloat, costs one lookup a ring. (Not
    /// one lookup for the whole hull: lying across the sea ice's edge, its middle can be over deep
    /// water while an end lies on the ice.) With <paramref name="plain"/> (nothing but the ground
    /// near it: see <see cref="Planet.OnlyGroundNear"/>) each point is read from the terrain
    /// alone. (Read in full, twelve points a ring each read as anywhere, a booster lying on the
    /// ground cost 1.1 ms a step; on the ground now about 0.15 ms.) Returns that, and the point
    /// it was found at.
    /// </summary>
    private (float Clearance, Vector3 At) HullContact(bool plain)
    {
        float Below(Vector3 p) => plain ? Body.TerrainAltitude(p) : Body.GroundAltitude(p);
        const float spacing = 0.5f;
        const int round = 12;
        const float clear = 4f; // radii
        var model = _model;
        var axis = Up;
        var a = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
        var b = Vector3.Cross(axis, a);
        var bell = CurrentStage.BellHeight;
        var length = Height + bell;
        var rings = (int)MathF.Ceiling(length / spacing);
        var (lowest, at) = (float.MaxValue, Position);
        for (var i = 0; i <= rings; i++)
        {
            var y = -bell + length * i / rings;
            var centre = Vector3.Transform(new Vector3(0, y, 0), model);
            var radius = RadiusAt(y);
            Vector3 Round(int n)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * n / round);
                return centre + (a * c + b * s) * radius;
            }

            // Beside a pad's, a zone's or a raft's side (a wall: a point inside it reads as under
            // its top), the clearance round a ring doesn't rise steadily from its lowest, and its
            // middle's says nothing of its side's: there all twelve are read, each as anywhere.
            // Jolt stops it a little short of a side (by the shapes' convex radius: 4 cm, into the
            // drone ship's), so where it has been stopped by one (moving into it no faster than
            // StillSpeed), a point facing sideways is also read a touch further out: a side that
            // near is touching it. In the water, though, a side beside it doesn't hold it up: only
            // what is under it does (the deck's top, a floe's, the seabed), so afloat against one
            // it can still settle afloat; then only the points facing down are read, and those
            // facing a side it is moving into faster than StillSpeed (a blow into it, judged).
            if (!plain && Body.BesideASide(centre, radius + TouchingClearance))
            {
                var up = Body.Up(centre);
                var afloat = _craft.Submerged > 0f;
                // The ground's velocity of the step before (CheckContact sets this step's only once
                // it knows what it is on, from this): what _lastVelocity was measured against.
                var groundBefore = _ground;
                for (var k = 0; k < round; k++)
                {
                    var point = Round(k);
                    var outward = (point - centre) / radius;
                    var facing = Vector3.Dot(outward, up);
                    var still = Vector3.Dot(Velocity - groundBefore, outward) < StillSpeed;
                    // Afloat, a blow into a side is judged by how it was moving as it touched it (Jolt
                    // has already stopped it, or bounced it off, by now).
                    if (afloat && still && Vector3.Dot(_lastVelocity - groundBefore, outward) < StillSpeed && facing > -0.7f)
                        continue;
                    var above = Body.GroundAltitude(point);
                    if (!afloat && still && above > TouchingClearance && MathF.Abs(facing) < 0.7f
                        && Body.GroundAltitude(point + outward * TouchingClearance) < 0f)
                        above = 0f;
                    if (above < lowest)
                        (lowest, at) = (above, point);
                }
                continue;
            }

            // Elsewhere what is under it is near enough a plane: the ground, read from the terrain
            // alone where nothing else is near, or the top of a pad, a zone or a raft.
            var middle = Below(centre);
            if (middle > clear * radius + TouchingClearance)
            {
                if (middle - radius < lowest)
                    (lowest, at) = (middle - radius, centre - Body.Up(centre) * radius);
                continue;
            }
            // Its lowest of the twelve round it. Across a ring (a few metres) the ground is near
            // enough a plane (the finest of its detail is 6 m across, or on the pack ice 3 m and
            // 8 cm high), so the ring's clearance rises steadily both ways round from its lowest.
            // So it is found by starting at the point straight down from the middle (the lowest on
            // level ground) and going round while the next is lower: three to six lookups, not twelve.
            var down = -Body.Up(centre);
            var at0 = (int)MathF.Round(MathF.Atan2(Vector3.Dot(down, b), Vector3.Dot(down, a)) / (MathF.Tau / round));
            float Read(int n, out Vector3 point)
            {
                point = Round(n);
                return Below(point);
            }
            var j = at0;
            var best = Read(j, out var bestAt);
            var after = Read(j + 1, out var afterAt);
            var before = Read(j - 1, out var beforeAt);
            var step = after < best && after <= before ? 1 : before < best ? -1 : 0;
            if (step != 0)
                (best, bestAt, j) = step > 0 ? (after, afterAt, j + 1) : (before, beforeAt, j - 1);
            for (var n = 2; step != 0 && n < round / 2; n++)
            {
                var further = Read(j + step, out var furtherAt);
                if (further >= best)
                    break;
                (best, bestAt, j) = (further, furtherAt, j + step);
            }
            if (best < lowest)
                (lowest, at) = (best, bestAt);
        }

        return (lowest, at);
    }

    /// <summary>
    /// How wide it is <paramref name="y"/> up its axis: its bell's radius below its body, else that
    /// of the stage (or the fairing, and over its foot's cone, the cone's half-way width) there.
    /// </summary>
    private float RadiusAt(float y)
    {
        if (y < 0f)
            return CurrentStage.BellRadius;
        if (FairingOn && y >= RocketGeometry.StageBase(Vehicle, Stage, TopStage) - Fairing.ConeHeight)
            return y < RocketGeometry.StageBase(Vehicle, Stage, TopStage) ? (Fairing.Radius + Scene.RocketRadius) / 2 : Fairing.Radius;
        for (var i = Stage; i < TopStage; i++)
            if (y < RocketGeometry.StageBase(Vehicle, Stage, i + 1))
                return Vehicle.Stages[i].Radius;
        return FairingOn ? Fairing.Radius : Vehicle.Stages[TopStage].Radius;
    }

    /// <summary>
    /// How it stands now: how far it leans (<see cref="TiltDegrees"/>), and its lowest point (a foot
    /// if the legs are out, otherwise the rim of the bottom engine bell): above the solid ground
    /// under it (the seabed, under the sea), which it stands on, returned, with where that point
    /// is; and above the calm sea over the sea, which sets <see cref="Altitude"/>.
    /// </summary>
    private (float Lowest, Vector3 LowestAt, bool LegsOut) Measure()
    {
        var up = Vector3.TransformNormal(Vector3.UnitY, _model);
        TiltDegrees = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(up, LocalUp), -1f, 1f)));

        var legsOut = HasLegs && LegDeploy >= 1f;
        float lowest = float.MaxValue, above = float.MaxValue, sea = float.MaxValue;
        var lowestAt = Position;
        var count = legsOut ? RocketGeometry.LegCount : BellRimPoints;
        // With the legs out, their feet hanging at full stroke: the first to touch the ground is
        // the touchdown (judged then, before the legs give), though the craft then stands a little
        // lower, as far as the legs gave in taking it (see LegGeometry.Stroke): the feet are on the
        // ground for as long as any is within its stroke of it.
        for (var i = 0; i < count; i++)
        {
            var local = legsOut ? RocketGeometry.Foot(Legs, i) : RocketGeometry.BellRim(CurrentStage, i, count);
            var point = Vector3.Transform(local, _model);
            var stroke = legsOut ? Legs.Stroke : 0f;
            var ground = Body.GroundAltitude(point);
            if (ground - stroke < lowest)
                (lowest, lowestAt) = (ground - stroke, point);
            // The calm sea, where it is above the solid thing under it (as Body.SurfaceAltitude measures).
            var aboveSea = Body.Altitude(point);
            above = MathF.Min(above, MathF.Min(ground, aboveSea) - stroke);
            sea = MathF.Min(sea, aboveSea - stroke);
        }
        Ground = MathF.Max(0f, above);
        Altitude = sea;
        return (lowest, lowestAt, legsOut);
    }

    /// <summary>
    /// <see cref="Measure"/>, for one far from the ground with only the ground near (see
    /// <see cref="HullMayTouch"/>): its height above the ground is the terrain's alone (nothing
    /// else is near), read under each of its feet with its legs out (they are metres apart, and
    /// on a slope the lowest over the ground needn't be the lowest of all), and without them
    /// under its bell's rim's lowest point above the calm sea only (the ground under points a
    /// bell's width apart is all but the same, and the sea's height is a distance from the
    /// planet's centre, so it costs no terrain read). Its tilt and <see cref="Altitude"/> are as
    /// Measure has them; <see cref="Ground"/> is the lowest of those heights above the ground.
    /// </summary>
    private (float Lowest, Vector3 LowestAt, bool LegsOut) MeasureFar()
    {
        var up = Vector3.TransformNormal(Vector3.UnitY, _model);
        TiltDegrees = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(up, LocalUp), -1f, 1f)));

        var legsOut = HasLegs && LegDeploy >= 1f;
        var (sea, lowest) = (float.MaxValue, float.MaxValue);
        var lowestAt = Position;
        var count = legsOut ? RocketGeometry.LegCount : BellRimPoints;
        var stroke = legsOut ? Legs.Stroke : 0f;
        for (var i = 0; i < count; i++)
        {
            var point = Vector3.Transform(legsOut ? RocketGeometry.Foot(Legs, i) : RocketGeometry.BellRim(CurrentStage, i, count), _model);
            var aboveSea = Body.Altitude(point) - stroke;
            if (legsOut)
            {
                var ground = Body.TerrainAltitude(point) - stroke;
                if (ground < lowest)
                    (lowest, lowestAt) = (ground, point);
            }
            else if (aboveSea < sea)
                lowestAt = point;
            sea = MathF.Min(sea, aboveSea);
        }
        if (!legsOut)
            lowest = Body.TerrainAltitude(lowestAt);
        Ground = MathF.Max(0f, MathF.Min(lowest, sea));
        Altitude = sea;
        return (lowest, lowestAt, legsOut);
    }

    /// <summary>Destroys it: it blows up (unless <paramref name="explode"/> is false: a fairing half has nothing in it to), and its body is taken out of the world (once the hit it was in, if any, has played out: see <see cref="RocketPhysics.Wreck"/>).</summary>
    private void Crash(string reason, bool explode = true)
    {
        State = FlightState.Crashed;
        CrashReason = reason;
        // For the world log (see Flight): how fast it was going as it was lost and whether the
        // physics had it. In the physics the hit has already slowed it, so the step before's speed
        // counts too; under formula its velocity is still the one it arrived with.
        CrashedWatched = !OnFormula;
        CrashSpeed = OnFormula ? Velocity.Length() : MathF.Max(Velocity.Length(), _lastVelocity.Length());
        Throttle = 0f;
        CrashPosition = Vector3.Transform(new Vector3(0, CurrentStage.Height * 0.35f, 0), _model);
        if (explode)
            _sounds.Add(new SoundEvent(CrashPosition, 1f, SoundCue.Explosion, Body));
        _physics?.Wreck(_craft); // gone from the scene now, from the world once the hit it was in has played out (none under formula: nothing to play out)
    }
}
