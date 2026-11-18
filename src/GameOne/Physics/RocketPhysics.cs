using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace GameOne.Physics;

/// <summary>What the flight computer asks of one craft for the next frame.</summary>
/// <param name="Craft">Which craft.</param>
/// <param name="ThrustAcceleration">How hard the engine pushes, in m/s² along the rocket's axis.</param>
/// <param name="DesiredUp">Which way the steering should point the rocket's nose.</param>
/// <param name="Steer">Whether the steering is working at all. Off while it stands on the ground,
/// so a booster that lands leaning over is free to fall over.</param>
internal readonly record struct RocketControl(Craft Craft, float ThrustAcceleration, Vector3 DesiredUp, bool Steer);

/// <summary>
/// A craft taken out of one body's world to be taken into another's across a sphere's edge
/// (<see cref="RocketPhysics.TakeOut"/>, <see cref="RocketPhysics.TakeIn"/>): the craft itself
/// (its stages, shapes, legs, fairing and parting go with it), and its motion in a body's frame:
/// its body's origin and centre of mass, attitude, velocity (its centre of mass's) and turning,
/// over that body's ground. The centre of mass and velocity are the physics' double copy
/// (<see cref="RocketPhysics.TrueCentreOfMass"/>), so a crossing rounds nothing.
/// </summary>
internal readonly record struct CraftState(Craft Craft, Vector3 Position, Vector3Double CentreOfMass, Quaternion Rotation, Vector3Double Velocity, Vector3 AngularVelocity)
{
    /// <summary>The same motion in the other frame of <paramref name="change"/> (worked out in double).</summary>
    public CraftState Changed(in FrameChange change) => this with
    {
        Position = change.Point((Vector3Double)Position).ToSingle(),
        CentreOfMass = change.Point(CentreOfMass),
        Rotation = change.Rotation(Rotation),
        Velocity = change.Velocity(CentreOfMass, Velocity),
        AngularVelocity = change.Spin((Vector3Double)AngularVelocity).ToSingle(),
    };
}

/// <summary>One stage to draw: which it is, where it is, how far its legs are out (if it has them), and whether its fairing is still round it.</summary>
internal readonly record struct StagePart(Stage Stage, Matrix4x4 Model, float LegDeploy, bool Fairing = false)
{
    /// <summary>How far out along its stroke each leg is (see <see cref="Craft.LegExtension"/>).</summary>
    public Vector4 LegExtension { get; init; } = Vector4.One;

    /// <summary>How far open its bay's doors are, 0 (shut) to 1 (open), if it has a bay (see <see cref="Craft.BayDoors"/>).</summary>
    public float BayDoors { get; init; }

    /// <summary>Where each of its bay's doors stopped on the ground, swung out from shut (radians: see <see cref="Craft.DoorStops"/>).</summary>
    public Vector2 DoorStops { get; init; } = new(RocketGeometry.DoorOpenRadians);
}

/// <summary>
/// One body in the physics world made of some of a vehicle's stages, <see cref="First"/> to
/// <see cref="Last"/>: a whole rocket, or a part that has come away from one. The flight decides
/// what it does (see <see cref="Vessel"/>); the physics only moves it.
/// </summary>
internal sealed class Craft(Vehicle vehicle, int first, int last, uint id, Shape stowed, Shape? deployed) : IDisposable
{
    public Vehicle Vehicle { get; } = vehicle;
    public int First { get; } = first;
    public int Last { get; } = last;
    internal uint Id { get; private set; } = id;
    internal Shape Stowed { get; set; } = stowed;
    internal Shape? Deployed { get; set; } = deployed;
    internal bool LegsDeployed { get; set; }
    public float LegDeploy { get; internal set; }

    /// <summary>
    /// Its colliders with its bay's doors open as ramps (see <see cref="RocketGeometry.PanelTransform"/>),
    /// its legs stowed and out, each door swung out as far as <see cref="OpenFor"/>: made only for
    /// a craft whose bottom stage has a bay (<see cref="Stage.Bay"/>), the first time its doors
    /// open (none until then).
    /// </summary>
    internal Shape? Open { get; set; }
    internal Shape? OpenDeployed { get; set; }

    /// <summary>The doors' stops (<see cref="DoorStops"/>) <see cref="Open"/> was made for.</summary>
    internal Vector2 OpenFor { get; set; }

    /// <summary>How far open its bay's doors are, 0 (shut) to 1 (open): see <see cref="RocketPhysics.SetDoors"/>.</summary>
    public float BayDoors { get; internal set; }

    /// <summary>
    /// How far out from shut (radians) each door (X: door 0, Y: door 1) swings before it is stopped
    /// by the ground under it (see <see cref="Vessel.MoveDoors"/>), or the doors' full swing,
    /// <see cref="RocketGeometry.DoorOpenRadians"/>, if nothing is in the way.
    /// </summary>
    public Vector2 DoorStops { get; internal set; } = new(RocketGeometry.DoorOpenRadians);

    /// <summary>
    /// Whether its collider has its doors open (<see cref="Open"/>): once they are fully open, and
    /// no longer from the moment they start to close, as the legs' feet are (<see cref="LegsDeployed"/>);
    /// and not while it comes down onto the ground with them open (see <see cref="RocketPhysics.SetDoors"/>).
    /// </summary>
    internal bool DoorsOpen { get; set; }

    /// <summary>The collider its body has, by its legs and doors.</summary>
    internal Shape Collider => Removed ? throw new ObjectDisposedException(nameof(Craft), "a removed craft has no collider") : (LegsDeployed, DoorsOpen) switch
    {
        (true, true) when OpenDeployed is { } both => both,
        (false, true) when Open is { } open => open,
        (true, _) when Deployed is { } feet => feet,
        _ => Stowed,
    };

    /// <summary>The shape of its legs: its bottom stage's (only those can be put out).</summary>
    internal LegGeometry Legs => Vehicle.Stages[First].LegGeometry;

    /// <summary>
    /// The craft it has just come away from (staging, or a fairing jettisoned), until they are clear
    /// of each other: they start inside each other, so until then they don't collide (with each other
    /// or any other craft), only with what doesn't move. Null once parted.
    /// </summary>
    internal Craft? PartingFrom { get; set; }

    /// <summary>How far out along its stroke each leg is (1: hanging free, 0: pushed right up), as its foot takes the weight.</summary>
    public Vector4 LegExtension { get; internal set; } = Vector4.One;

    /// <summary>
    /// Whether its legs have locked: all four feet have been within their stroke of the ground at
    /// once since it last came down, so each holds the full <see cref="Scene.LegHoldAcceleration"/>
    /// until it leaves the ground (see <see cref="RocketPhysics.Cushion"/>).
    /// </summary>
    internal bool LegsLocked { get; set; }

    /// <summary>
    /// Whether its top stage's fairing is still on (see <see cref="Stage.Fairing"/>): it is part
    /// of the collider until <see cref="RocketPhysics.JettisonFairing"/> takes it off.
    /// </summary>
    public bool FairingOn { get; internal set; }

    /// <summary>
    /// How much of it is in the water, 0 to 1: its slices' shares, each by how deep it is under the
    /// waves there (see <see cref="RocketPhysics.Float"/>). Any at all, and it is afloat. Kept as it
    /// was while it is frozen.
    /// </summary>
    public float Submerged { get; internal set; }

    /// <summary>
    /// Whether it is on something solid this step (the ground, a pad, a raft or the seabed, or
    /// resting on a craft lower down that is, layer on layer: see <c>RocketPhysics.FindGrounded</c>
    /// and <c>RocketPhysics.Weight</c>), set before it falls.
    /// </summary>
    internal bool Grounded { get; set; }

    /// <summary>
    /// Whether it is on the ground (or a pad, a raft or the seabed) itself this step, by its own
    /// clearance (<see cref="GroundClearance"/>), not by what it rests on: what freezing goes by.
    /// </summary>
    internal bool OnGround { get; set; }

    /// <summary>
    /// How far it is above the solid thing under it this step (see <c>RocketPhysics.Clearance</c>),
    /// worked out once before it falls, and what <see cref="OnGround"/> is judged by (which is what
    /// freezing goes by); infinite when it is too far up for that to be worth working out.
    /// </summary>
    internal float GroundClearance { get; set; } = float.PositiveInfinity;

    /// <summary>
    /// How high the middle of its axis is this step, above the sea, while it is near the ground but
    /// not on it: what tells, in a pile, which of two touching is lower down (see <c>RocketPhysics.FindGrounded</c>).
    /// </summary>
    internal float Level { get; set; }

    /// <summary>How tall its stack is, from the bottom of its body to its top (<see cref="RocketGeometry.StackHeight"/>; fixed, as its stages are).</summary>
    internal float Height { get; } = RocketGeometry.StackHeight(vehicle, first, last);

    /// <summary>Its length, from the bottom of its engine bell to its top (fixed, as its stages are).</summary>
    internal float Length { get; } = RocketGeometry.StackHeight(vehicle, first, last) + vehicle.Stages[first].BellHeight;

    /// <summary>Its widest radius, of all its stages (fixed, as its stages are).</summary>
    internal float Widest { get; } = WidestOf(vehicle, first, last);

    /// <summary>How far from its axis anything of it reaches: its widest radius, or, with its bay's doors open, as far as its ramps (<see cref="RocketGeometry.RampReach"/>).</summary>
    internal float WidestOpen => DoorsOpen
        ? MathF.Max(Widest, MathF.Max(RocketGeometry.RampReach(DoorStops.X), RocketGeometry.RampReach(DoorStops.Y)))
        : Widest;

    private static float WidestOf(Vehicle vehicle, int first, int last)
    {
        var radius = 0f;
        for (var i = first; i <= last; i++)
            radius = MathF.Max(radius, vehicle.Stages[i].Radius);
        return radius;
    }

    /// <summary>
    /// Where the water holds it up, while it is in it: its centre of buoyancy, the middle of its
    /// slices in the water, each by how far in it is. Its centre of mass above this, it is still
    /// to tip over; once it has, the two are together.
    /// </summary>
    public Vector3 Buoyancy { get; internal set; }

    /// <summary>Whether it has been taken out of the world (it blew up): nothing can be asked of it any more.</summary>
    public bool Removed { get; internal set; }

    /// <summary>The world (a body's physics) it is in (or was made in); none for a stand-in (<see cref="RocketPhysics.StandIn"/>).</summary>
    internal RocketPhysics? World { get; private set; }

    /// <summary>Puts it in <paramref name="world"/>.</summary>
    internal void Into(RocketPhysics world) => World = world;

    /// <summary>
    /// Makes it <paramref name="world"/>'s body <paramref name="id"/>, taken there across a
    /// sphere's edge (<see cref="RocketPhysics.TakeIn"/>): what it is (its stages, shapes, legs,
    /// fairing, parting) goes with it; what it knew of the world it left (its ground's cells, the
    /// floes near it, its contacts, its rest, where it was to come down) is forgotten, as none of
    /// that is the new world's.
    /// </summary>
    internal void Rehome(uint id, RocketPhysics world)
    {
        (Id, World, Removed, Frozen, WakingSoon, WreckedFor) = (id, world, false, false, false, null);
        (Grounded, OnGround, GroundClearance, Level, Aboard, StillFor) = (false, false, float.PositiveInfinity, 0f, null, 0f);
        FloesSought.Clear();
        (FloesSoughtAt, FloesSoughtWithin) = (new Vector3(float.NaN), 0f);
        TileCells.Clear();
        (TilesFrom, TilesTo) = (new Vector3(float.NaN), new Vector3(float.NaN));
        (GroundAhead, GroundAheadFrom) = (null, double.NaN);
        AheadCells.Clear();
        AheadAt = new Vector3(float.NaN);
        TouchesBegun.Clear();
        Array.Clear(Touches);
        FeetDown = 0;
        Kept = false; // its motion in the new frame is handed in with it (see RocketPhysics.TakeIn)
    }

    /// <summary>
    /// How long ago it was wrecked, while it is left in the world for the hit to play out (see
    /// <see cref="RocketPhysics.Wreck"/>); null if it hasn't been.
    /// </summary>
    internal float? WreckedFor { get; set; }

    /// <summary>
    /// Whether it is frozen: at rest on the ground, so taken out of the simulation (not moved,
    /// with no ground laid under it) until something wakes it (see <see cref="RocketPhysics.Freeze"/>).
    /// </summary>
    public bool Frozen { get; internal set; }

    /// <summary>
    /// Whether, frozen, it is about to be woken: something on the move is within twice its wake's
    /// reach (see <see cref="RocketPhysics.NearAMover"/>), so the ground under it is made ahead of
    /// need (by the tiles' worker: see <see cref="TerrainTiles.Update"/>), and not all in the step
    /// it wakes in. Set afresh every step.
    /// </summary>
    internal bool WakingSoon { get; set; }

    /// <summary>
    /// Whether it is still coming down in the water, not yet settled afloat (its vessel says so):
    /// until it has, it isn't frozen afloat by distance, so it settles wherever the player is.
    /// </summary>
    public bool Settling { get; set; }

    /// <summary>Where it was frozen (it hasn't moved since, unless a raft it is on carries it: see <see cref="Aboard"/>).</summary>
    internal Vector3 FrozenAt { get; set; }

    /// <summary>How it was turned when it was frozen, to put it back as it was should anything but a raft's carrying move it.</summary>
    internal Quaternion FrozenRotation { get; set; } = Quaternion.Identity;

    /// <summary>
    /// The raft it is frozen on (the drone ship's deck, say), and its pose in that raft's own frame
    /// (see <see cref="DeckPose"/>), null otherwise: a raft never stops moving, so a craft frozen
    /// on it is put back there, so far along and across it and so turned on it, each step (see
    /// <see cref="RocketPhysics.Freeze"/>).
    /// </summary>
    public (Raft Raft, Vector3 Position, Quaternion Rotation)? Aboard { get; internal set; }

    /// <summary>How long it has been all but still, in seconds (see <see cref="Scene.FreezeSeconds"/>).</summary>
    internal float StillFor { get; set; }

    /// <summary>Where it stood, and how it was turned, when that stretch of being all but still began (in a raft's frame while it is on one).</summary>
    internal Vector3 StillFrom { get; set; }
    internal Quaternion StillTurned { get; set; }

    /// <summary>
    /// The floes near it, from where it was when they were last looked for (<see cref="FloesSoughtAt"/>),
    /// a little further out than any it needs (see <see cref="RocketPhysics.UpdateFloes"/>).
    /// </summary>
    internal List<Raft> FloesSought { get; } = [];
    internal Vector3 FloesSoughtAt { get; set; } = new(float.NaN);

    /// <summary>How far out they were last looked for (its reach grows with its speed: see <see cref="RocketPhysics.UpdateFloes"/>).</summary>
    internal float FloesSoughtWithin { get; set; }

    /// <summary>
    /// The ground's cells under its stretch of ground, from where it was (<see cref="TilesFrom"/>)
    /// to where it was going (<see cref="TilesTo"/>) when they were last worked out, a little wider
    /// than it needs (see <see cref="TerrainTiles.Update"/>).
    /// </summary>
    internal HashSet<TerrainTiles.Cell> TileCells { get; } = [];
    internal Vector3 TilesFrom { get; set; } = new(float.NaN);
    internal Vector3 TilesTo { get; set; } = new(float.NaN);

    /// <summary>
    /// Where it is predicted to come down on the ground, if it is coming down fast: set by the
    /// flight's look ahead (<see cref="Collisions"/>) each step, read by the tiles' updates in the
    /// physics' next <see cref="RocketPhysics.Update"/> and cleared after it, which ask the worker
    /// for the ground there ahead of need (see <see cref="TerrainTiles.Update"/>): where (over the
    /// ground), the way it arrives, how fast, and in how many game seconds. Kept for every one of
    /// that update's steps: a step without it would want none of those cells, and drop them from
    /// the worker's list.
    /// </summary>
    internal (Vector3 At, Vector3 Arriving, float Speed, float Seconds)? GroundAhead { get; set; }

    /// <summary>The tiles' clock when its updates first read <see cref="GroundAhead"/> (NaN before), so each later step counts its seconds down.</summary>
    internal double GroundAheadFrom { get; set; } = double.NaN;

    /// <summary>The cells round where it comes down (<see cref="GroundAhead"/>), as last worked out for <see cref="AheadAt"/>.</summary>
    internal HashSet<TerrainTiles.Cell> AheadCells { get; } = [];
    internal Vector3 AheadAt { get; set; } = new(float.NaN);

    /// <summary>
    /// How it was moving and turning, and where its centre of mass was and which way it faced, just
    /// before this step (still, for one frozen then): what a contact the step began is judged by
    /// (see <see cref="RocketPhysics.TouchesBegun"/>), as Jolt's own velocities after it are what the
    /// touch made of them, and where on it the contact is (as it stood for the step).
    /// </summary>
    internal Vector3 StepVelocity { get; set; }
    internal Vector3 StepSpin { get; set; }
    internal Vector3 StepCentre { get; set; }
    internal Quaternion StepRotation { get; set; } = Quaternion.Identity;

    /// <summary>
    /// The physics' own copy of its centre of mass and velocity, in double, in its world's frame,
    /// while <see cref="Kept"/> (see <see cref="RocketPhysics.TrueCentreOfMass"/>): stepped by the
    /// game while it flies free, and handed to Jolt rounded once a step, so Jolt's float rounding
    /// is never carried from one step to the next.
    /// </summary>
    internal Vector3Double KeptCentre { get; set; }
    internal Vector3Double KeptVelocity { get; set; }

    /// <summary>
    /// Whether <see cref="KeptCentre"/> and <see cref="KeptVelocity"/> are its motion: taken from
    /// Jolt at the start of each step unless Jolt's still matches them (see <c>RocketPhysics.Resync</c>),
    /// and let go for a step it doesn't fly free; never for one frozen.
    /// </summary>
    internal bool Kept { get; set; }

    /// <summary>
    /// Jolt's centre of mass and velocity as the step's resync read them (with the thrust added
    /// since, as Jolt adds it), while <see cref="StepRead"/>: what its fall works from this step,
    /// rather than reading them from Jolt again.
    /// </summary>
    internal Vector3 ReadCentre { get; set; }
    internal Vector3 ReadVelocity { get; set; }

    /// <summary>Whether <see cref="ReadCentre"/> and <see cref="ReadVelocity"/> are Jolt's now, in this step, until its fall.</summary>
    internal bool StepRead { get; set; }

    /// <summary>What it touched over the last update's steps, oldest first (see <see cref="RocketPhysics.TouchesBegun"/>).</summary>
    internal List<Touch> TouchesBegun { get; } = [];

    /// <summary>How many live contacts it has with each kind of thing at each of its parts, by <see cref="TouchIndex"/>.</summary>
    internal int[] Touches { get; } = new int[(int)SolidKind.Craft * 3 + 3];

    internal static int TouchIndex(SolidKind with, CraftPart part) => (int)with * 3 + (int)part;

    /// <summary>
    /// Which of its feet are on the ground (or a raft) within their stroke, a bit each (see
    /// <see cref="RocketPhysics.Cushion"/>): the legs' own touch, which Jolt never sees (its feet
    /// are the collider's, where the legs are pushed right up), kept so that a foot that stays
    /// down is no new touch each step. Kept while it is frozen, as it still stands on them.
    /// </summary>
    internal int FeetDown { get; set; }

    public void Dispose()
    {
        Stowed.Dispose();
        Deployed?.Dispose();
        Open?.Dispose();
        OpenDeployed?.Dispose();
    }
}

/// <summary>
/// The rocket and the world it flies in, simulated with Jolt Physics (BRUTAL's <c>Brutal.Jolt</c>):
/// the ground (tiles laid round the crafts near it: see <see cref="TerrainTiles"/>), the pads and
/// the service tower are static, and the rafts' hulls (the drone ship's) kinematic (moved with the waves); each
/// <see cref="Craft"/> is a dynamic body, pushed by its engine and turned by its steering, which
/// settles onto its legs (or falls over) on its own. When one
/// stages, it splits in two: the bottom stage becomes a craft of its own and the rest flies on as
/// another. Any number can be in the world at once.
/// </summary>
/// <remarks>
/// <para>
/// A craft that has come to rest on the ground is frozen (see <see cref="Freeze"/>): Jolt stops
/// simulating it and it needs no tiles, so a world full of landed boosters costs next to nothing.
/// It wakes when it is flown, its legs move, it stages or jettisons, or something awake and moving
/// could soon touch it (<see cref="Scene.WakeMargin"/>), all of which happen here.
/// </para>
/// <para>
/// Gravity and drag are applied here rather than by Jolt, because they change with position:
/// gravity pulls towards its body's centre and weakens with distance from it, and drag fades out
/// with the thinning air. Jolt's own gravity and damping are off.
/// </para>
/// <para>
/// Arcade handling: thrust is applied as an acceleration rather than a force, so it doesn't depend
/// on the mass Jolt works out from the shape (which can't be overridden: see README), and the
/// steering sets how fast the rocket turns rather than applying a torque.
/// </para>
/// <para>
/// Shapes are always built as <c>XShapeSettings.Create(...).CreateShape()</c>, never with the
/// direct <c>XShape.Create(size)</c> overloads. The current Brutal.Jolt binding (and its
/// 2026.10.2 package) was generated against a newer jolt.dll than has been published, and those
/// convenience overloads (e.g. <c>BoxShape_Create_3</c>) don't exist in the shipped native. The
/// settings route is the long-standing Jolt API and is present.
/// </para>
/// </remarks>
internal sealed class RocketPhysics : IDisposable
{
    internal const float StepSeconds = 1f / 120f;

    /// <summary>
    /// Jolt's own cap on a body's speed (m/s), set far past anything flown so that it never takes
    /// hold: the ground's frame itself moves at its turn times the distance, some 1e8 m/s at the
    /// furthest a path reaches (<see cref="Orbit.MaxRange"/>); and its square still a float.
    /// </summary>
    private const float JoltTopSpeed = 1e12f;

    /// <summary>
    /// How much shorter than <see cref="StepSeconds"/> each step is, in (0, 1]: 1 but in a
    /// slow-down, which shortens the step and slows the game's clock by the same factor, so the
    /// steps a real second stay the same and a fast collision is stepped finely enough to be seen.
    /// The flight sets it as a slow-down starts, changes or ends. What is left over of a step
    /// (the accumulator) is rescaled with it, so it stays the same share of a step: a sharp drop
    /// (a touch found late) would otherwise leave many short steps' worth waiting, run in the
    /// next frame all at once, up to the most a frame may.
    /// </summary>
    public float StepScale
    {
        get => _stepScale;
        set
        {
            if (value is not (> 0f and <= 1f))
                throw new ArgumentOutOfRangeException(nameof(value), value, "a step can only be shortened");
            _accumulator *= value / _stepScale;
            _stepScale = value;
        }
    }
    private float _stepScale = 1f;

    /// <summary>
    /// How far (seconds) its crafts are behind the world time its last update ran to: what was
    /// left over of a step. Every world in use is kept at the same (<see cref="Worlds.Step"/>), so
    /// a craft taken from one into another across a sphere's edge is at the same moment in both.
    /// </summary>
    internal float Lag
    {
        get => _accumulator;
        set => _accumulator = value;
    }

    /// <summary>What <see cref="Lag"/> would be after an update of <paramref name="dt"/> from <paramref name="lag"/> at a step scale of <paramref name="stepScale"/>, worked out as <see cref="Update"/> works it, without stepping.</summary>
    internal static float LagAfter(float lag, float dt, float stepScale)
    {
        var step = StepSeconds * stepScale;
        lag = MathF.Min(lag + dt, MaxStepsPerFrame * step);
        for (; lag >= step; lag -= step)
        {
        }
        return lag;
    }

    /// <summary>
    /// How long each step is now: <see cref="StepSeconds"/> shortened by <see cref="StepScale"/>.
    /// Everything that takes or times a step goes by this; at a scale of 1 it is exactly
    /// <see cref="StepSeconds"/>, so nothing changes outside a slow-down.
    /// </summary>
    internal float StepLength => StepSeconds * _stepScale;

    /// <summary>How many steps Jolt has taken, for the tests to count them.</summary>
    internal long Steps { get; private set; }

    /// <summary>
    /// How many bodies the world can hold, the scenery included: every craft in a world, from every
    /// flight in it (<see cref="Flight.Capacity"/>), the ground's tiles (<see cref="Scene.MaxTerrainTiles"/>)
    /// and the floes' hulls (<see cref="Scene.MaxFloeBodies"/>).
    /// </summary>
    public const int MaxBodies = 4096;

    // Body pairs and contact constraints at once: two for each body (an awake craft on the ground
    // touches a few tiles; frozen and far-off ones touch nothing). Jolt takes room for all of them
    // from its temporary memory every step, and fails outright (it ends the process) if that is
    // too small, so that grows with them: at 16,384 of each, the 10 MB there was for 4,096 wasn't
    // enough; 8,192 of each fit easily in 32 MB.
    private const int MaxBodyPairs = 2 * MaxBodies, MaxContactConstraints = 2 * MaxBodies;
    private const int MaxStepsPerFrame = 8; // after a long stall, catch up a little rather than all at once

    /// <summary>How quickly the steering turns the rocket: radians per second per radian it is off (the warning's lead counts the lag this makes: see <see cref="Collisions.TurnSeconds"/>).</summary>
    internal const float SteerGain = 2.5f;
    private const float MaxTurnRate = 1.2f; // radians per second

    /// <summary>How quickly a craft's turning is brought to what the steering asks, per second (the warning's lead counts the lag: see <see cref="Collisions.TurnSeconds"/>).</summary>
    internal const float SteerBlendRate = 8f;

    /// <summary>How fast Jolt slows a craft's turning, per second (see <see cref="Fall"/> for the frame it settles in).</summary>
    internal const float AngularDamping = 0.8f;

    // Object layers: 0 never moves (the ground's tiles, pads, tower); 1 moves (the crafts), and hits
    // everything; 2 is a craft parting from another (see Craft.PartingFrom), which only hits what
    // doesn't move. A stage separates from inside the interstage (and a fairing's halves from round
    // the payload), so the two start inside each other: were they to collide at once they would be
    // flung apart. They part first, and collide like any others once clear of each other.
    private const ushort NonMoving = 0, Moving = 1, Parting = 2;

    private GroundBody _body;
    private readonly JobSystemThreadPool _jobSystem;  // every world's (JoltShared), read once here rather than each step
    private readonly TempAllocatorImpl _tempAllocator;
    private readonly BroadPhaseLayerInterfaceTable _broadPhaseLayers;
    private readonly ObjectLayerPairFilterTable _layerPairs;
    private readonly ObjectVsBroadPhaseLayerFilterTable _layerVsBroadPhase;
    private readonly PhysicsSystem _system;
    private readonly BodyInterface _bodies;

    private readonly List<uint> _staticBodies = [];   // the scenery, the ship's hull included (a floe's comes and goes: see UpdateFloes)
    private readonly Dictionary<Raft, Hull> _hulls = []; // each active raft's (see MoveRafts)
    // The floes' marks for UpdateFloes, by floe number: each is this step's (_floeStep) or stale,
    // so none is cleared. (Sets and a dictionary of floes, looked up for each floe near each
    // craft every step, were a good part of its time.)
    private int _floeStep;
    private int[] _nearAt = [], _wantedAt = [], _keptAt = []; // the step each floe was near an awake craft, wanted as a body, kept up to date
    private float[] _nearest = [];                            // and how near the nearest craft was then
    private readonly List<Raft> _floesNear = [];              // floes near an awake craft this step
    private long[] _nearKeys = new long[512];                 // those, to sort by nearness (see UpdateFloes)
    private readonly List<Raft> _floeBodiesWanted = [];
    private readonly List<Raft> _activeFloes = [];
    private readonly List<Raft> _floesGone = [];
    private const int InitialCrafts = 64;
    private readonly List<Craft> _crafts = new(InitialCrafts); // room from the start, so a craft taken in across a sphere's edge (TakeIn) finds it
    private readonly TerrainTiles _tiles;
    private readonly List<(Craft Craft, Raft? Aboard)> _waking = [];  // marked to wake this step, and the raft each was frozen on
    private readonly List<(Craft Craft, Vector3 At, Vector3 Ahead, float AheadSquared, Capsule Outline, float Bound)> _movers = []; // awake crafts on the move, where, their way ahead over Scene.WakeSeconds, their outline and how far from where they are anything of them reaches
    private readonly HashSet<Craft> _ready = [];                       // still long enough to freeze, this step (see FreezeResting)
    private readonly HashSet<Craft> _grouped = [];                     // those already put in a group, this step
    private readonly List<Craft> _group = [];                          // the awake crafts touching one ready one, through each other
    private readonly List<Craft> _low = [];                            // awake crafts near the ground but not on it, this step (see FindGrounded)
    private readonly Func<Craft, (Vector3 Position, Vector3 Velocity, Vector3 HalfLength)> _motion;
    private readonly float[] _phases = new float[Sea.WaveCount]; // the waves' this step (Sea.Phases)
    private Vector3? _player;                                     // where the vessel being flown is, this frame
    // Jolt's contacts, queued natively as they begin and end in a step and read after it (see
    // ReadContacts): which body is which craft or raft, and the contacts live now, each by its
    // bodies' and sub-shapes' IDs (all a contact's ending carries).
    private readonly ContactEventListener _listener;
    private readonly ContactEvent[] _events = new ContactEvent[256];
    private readonly Dictionary<uint, Craft> _craftBodies = new(InitialCrafts);
    private readonly Dictionary<uint, Raft> _raftBodies = [];
    private readonly Dictionary<(uint Body1, uint Body2, uint SubShape1, uint SubShape2), LiveContact> _live = [];
    private readonly List<(uint Body1, uint Body2, uint SubShape1, uint SubShape2)> _forgotten = []; // the live contacts with a body being taken out (see ForgetContacts)
    private float _accumulator;
    private double _time; // seconds simulated

    /// <summary>Every craft in the world.</summary>
    public IReadOnlyList<Craft> Crafts => _crafts;

    /// <summary>How many of the ground's tiles are in the world.</summary>
    public int TileCount => _tiles.Count;

    /// <summary>How many floes are bodies in the world (see <see cref="UpdateFloes"/>).</summary>
    public int FloeBodyCount { get; private set; }

    /// <summary>
    /// Sets up a world for <paramref name="body"/>, in its frame: its pads, towers and masts where it
    /// has the sites, its ships' hulls where it has a sea (the ground comes as it is needed).
    /// <see cref="Reset"/> puts a rocket on the pad. Jolt's process-wide state, the job system and
    /// the tile worker are every world's (<see cref="JoltShared"/>), so any number can be made at once.
    /// With <paramref name="takeRafts"/> it takes the process's rafts for its own at once, its floes
    /// none yet (<see cref="TakeRafts"/>; without, as one made off the main thread, only once put in use).
    /// </summary>
    public RocketPhysics(GroundBody body, bool takeRafts = true)
    {
        _body = body;
        (_jobSystem, _tempAllocator) = (JoltShared.JobSystem, JoltShared.TempAllocator);

        // Moving things collide with everything; non-moving things only with moving things; and
        // parting ones only with what doesn't move.
        _broadPhaseLayers = BroadPhaseLayerInterfaceTable.Create(3, 2);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(NonMoving, 0);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(Moving, 1);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(Parting, 1);
        _layerPairs = ObjectLayerPairFilterTable.Create(3);
        _layerPairs.EnableCollision(NonMoving, Moving);
        _layerPairs.EnableCollision(Moving, Moving);
        _layerPairs.EnableCollision(NonMoving, Parting);
        _layerVsBroadPhase = ObjectVsBroadPhaseLayerFilterTable.Create(_broadPhaseLayers, 2, _layerPairs, 3);

        _system = PhysicsSystem.Create();
        // Room for a world that has had many flights, and kept what they left.
        _system.Init(MaxBodies, 0, MaxBodyPairs, MaxContactConstraints, _broadPhaseLayers, _layerVsBroadPhase, _layerPairs);
        _system.SetGravity(new float3(0, 0, 0)); // applied per body, towards its body's centre
        _bodies = _system.GetBodyInterface();
        // A contact's beginning and end only (each lasting contact's every step would be most of
        // the queue), read after every step. Each sub-shape pair's contact is one, so a step can
        // begin and end more than the contact constraints Jolt holds: a world full to its capacity
        // piled on one spot (FrameTests), all woken at once, made 12,500 and then 18,500 in a
        // step. Room for nearly twice that (2 MB), and a Debug check that none is ever lost.
        _listener = new ContactEventListener(4 * MaxContactConstraints);
        _system.SetContactListener(_listener.Handle);
        // First, before any other body: the tiles' placeholders must take Jolt's first indices (see TerrainTiles).
        _tiles = new TerrainTiles(body, _bodies, NonMoving, () => _time, ForgetContacts);
        // Where a craft is, for its tiles: the middle of its stack, so one lying on the ground is
        // covered end to end, and the way to its top end, so its ends are measured from the ground too.
        _motion = craft =>
        {
            var half = Up(craft) * (craft.Height / 2);
            return (Position(craft) + half, Velocity(craft), half);
        };

        // Every launch site's pad and tower (square to the site's own frame), and the landing zones (tilted to stand level on the curve).
        // Each reaches down to its foundation's bottom, so nothing can slip between its side and the ground.
        // They are the planet's alone (HasSites), as the drone ships and floes are its sea's (HasSea).
        if (body.HasSites)
            AddSites();
        if (body.HasSea)
        {
            foreach (var ship in Rafts.Ships)
                AddHull(ship); // a ship's hull is always a body
        }
        if (takeRafts)
            TakeRafts();

        _system.OptimizeBroadPhase();
    }

    /// <summary>Asks the tile worker for nothing more of this world's, and lets the process's rafts' floes go (at a sea's body): for one released to the pool with no craft in it (<see cref="Worlds"/>), on the main thread.</summary>
    public void Idle()
    {
        _tiles.DropWants();
        var held = Rafts.HeldBy(_activeFloes);
        _activeFloes.Clear();
        if (held)
            TakeRafts(); // its floes let go too: nothing is there to stand on them
    }

    /// <summary>
    /// Makes this world <paramref name="body"/>'s instead, for a world reused from the pool
    /// (<see cref="Worlds"/>): every craft and wreck taken out, its scenery (the sites, the ships'
    /// hulls, the floes) and its ground's tiles gone, then the new body's set up as the constructor
    /// sets them (its sites where it has them, its ships' hulls where it has a sea). Jolt's world
    /// and its placeholders are kept, which is what makes reusing one cheaper than making one.
    /// </summary>
    public void Rebuild(GroundBody body)
    {
        Clear();
        foreach (var id in _staticBodies)
        {
            _bodies.RemoveBody(id);
            _bodies.DestroyBody(id);
            ForgetContacts(id);
        }
        _staticBodies.Clear();
        foreach (var (raft, hull) in _hulls)
            if (!raft.IsShip && hull.Body is { } floe) // the floes' (the ships' were scenery)
            {
                _bodies.RemoveBody(floe);
                _bodies.DestroyBody(floe);
                ForgetContacts(floe);
            }
        _hulls.Clear();
        _raftBodies.Clear();
        FloeBodyCount = 0;
        _activeFloes.Clear(); // (the process's rafts are left alone: the world in use at the sea's body has them, see TakeRafts)
        _tiles.Rebuild(body);
        _body = body;
        StepScale = 1f;
        if (body.HasSites)
            AddSites();
        if (body.HasSea)
        {
            foreach (var ship in Rafts.Ships)
                AddHull(ship);
        }
        _system.OptimizeBroadPhase();
    }

    /// <summary>
    /// Makes the process's rafts (<see cref="Rafts.Active"/>, one set for the planet's sea) this
    /// world's floes, none yet: for a world put in use at a body with a sea (<see cref="Worlds"/>), on
    /// the main thread, as a world made or rebuilt on another must leave them to the one in use.
    /// Nothing at a body with no sea.
    /// </summary>
    public void TakeRafts()
    {
        if (_body.HasSea)
            Rafts.SetActiveFloes(_activeFloes);
    }

    /// <summary>The launch sites' pads, towers and masts, and the landing zones, as bodies that don't move.</summary>
    private void AddSites()
    {
        for (var s = 0; s < LaunchSites.All.Length; s++)
        {
            var site = LaunchSites.All[s];
            AddPad(site, Foundations.PadFootings[s], Foundations.LaunchPads[s]);
            var tower = site.ToWorld(Scene.TowerOffset + new Vector3(0, Scene.TowerHeight / 2, 0));
            using var towerShape = BoxShapeSettings.Create(new float3(Scene.TowerHalfWidth, Scene.TowerHeight / 2, Scene.TowerHalfWidth));
            AddStatic(towerShape, ToFloat3(tower), site.Frame, friction: 0.5f, SolidKind.Tower);
        }
        // The floodlight masts round the pads and zones, solid as the towers are (a ship's ride its hull: see CreateHull).
        foreach (var place in PadLights.All.Where(p => p.Ship is null))
            foreach (var mast in place.Masts)
            {
                using var mastShape = BoxShapeSettings.Create(new float3(Scene.MastHalfWidth, mast.Height / 2, Scene.MastHalfWidth));
                AddStatic(mastShape, ToFloat3(place.ToWorld(mast.Foot + new Vector3(0, mast.Height / 2, 0))), place.Frame, friction: 0.5f, SolidKind.Mast);
            }
        foreach (var plinth in Foundations.LandingZonePlinth)
        {
            if (plinth.Footing is { } footing)
                AddFooting(footing, plinth.Centre, plinth.Tilt);
            else if (plinth.Slope < Plinth.LevelSlope)
            {
                using var landingZone = CylinderShapeSettings.Create(plinth.Depth / 2, Scene.LandingZoneRadius, convexRadius: 0.05f);
                AddStatic(landingZone, ToFloat3(plinth.Centre + plinth.Up * (Scene.PadTop - plinth.Depth / 2)), plinth.Tilt, friction: 0.8f, SolidKind.Ground);
            }
            else
                AddTiltedZone(plinth);
        }
    }

    /// <summary>
    /// An active raft's place in the physics: where it is (<see cref="At"/>) and where this step
    /// moves it to (<see cref="Next"/>), which is what a craft frozen on it is carried by, and its
    /// hull's body if it has one. One without a body (a floe far from any awake craft, but with
    /// one frozen aboard) is still carried on, so it carries what is on it.
    /// </summary>
    private sealed class Hull
    {
        public uint? Body;
        public DeckPose At, Next;
        public double NextAt = double.NaN; // the time Next is for
    }

    /// <summary>
    /// A hull's body for <paramref name="raft"/> at <paramref name="pose"/>: one box (or, for a
    /// disc, a cylinder) from its bottom to its top, kinematic, so Jolt moves it (each step, to
    /// where the waves have it: see <see cref="MoveRafts"/>) and what stands on it moves with it,
    /// but nothing pushes it about. It is scenery, in the layer that never moves (crafts hit it;
    /// the ground's tiles, which it never touches, don't).
    /// </summary>
    private uint CreateHull(Raft raft, DeckPose pose)
    {
        var halfHeight = (raft.Freeboard + raft.Draft) / 2;
        using ShapeSettings outline = raft.Shape == RaftShape.Box
            ? BoxShapeSettings.Create(new float3(raft.HalfLength, halfHeight, raft.HalfBeam), convexRadius: 0.05f)
            : CylinderShapeSettings.Create(halfHeight, raft.HalfLength, convexRadius: 0.05f);
        using var shape = raft.IsShip ? ShipWithMasts(raft, outline) : outline.CreateShape();
        using var settings = BodyCreationSettings.Create(shape, ToFloat3(HullCentre(raft, pose)), ToFloatQuat(pose.Rotation), MotionType.Kinematic, NonMoving);
        settings.SetFriction(0.8f);
        settings.SetRestitution(0.1f);
        settings.SetAllowSleeping(false); // it is never still
        settings.SetUserData((ulong)SolidKind.Raft); // its masts too, told from its deck by height (see KindOf)
        var body = _bodies.CreateAndAddBody(settings, Activation.Activate);
        _raftBodies[body] = raft;
        return body;
    }

    /// <summary>A drone ship's hull with its floodlight masts (<see cref="PadLights"/>) on its deck, one body, so they ride the waves with it.</summary>
    private static Shape ShipWithMasts(Raft raft, ShapeSettings hull)
    {
        var place = PadLights.All.First(p => p.Ship == raft);
        var centre = new Vector3(0f, (raft.Freeboard - raft.Draft) / 2, 0f); // the hull's, in the ship's frame (see HullCentre)
        var parts = new List<ShapeSettings>();
        using var compound = StaticCompoundShapeSettings.Create();
        compound.AddShape(new float3(0f, 0f, 0f), floatQuat.Identity, hull);
        foreach (var mast in place.Masts)
        {
            var box = BoxShapeSettings.Create(new float3(Scene.MastHalfWidth, mast.Height / 2, Scene.MastHalfWidth));
            parts.Add(box);
            compound.AddShape(ToFloat3(mast.Foot + new Vector3(0f, mast.Height / 2, 0f) - centre), floatQuat.Identity, box);
        }
        var shape = compound.CreateShape();
        foreach (var part in parts)
            part.Dispose();
        return shape;
    }

    /// <summary>Gives <paramref name="raft"/> a hull for good, as it is now (the ship's).</summary>
    private void AddHull(Raft raft)
    {
        var pose = raft.Current;
        var body = CreateHull(raft, pose);
        _staticBodies.Add(body);
        _hulls[raft] = new Hull { Body = body, At = pose, Next = pose };
    }

    /// <summary>Where <paramref name="raft"/>'s hull's centre is with it at <paramref name="pose"/>: halfway from its bottom to its top, up its own y from the waterline.</summary>
    private static Vector3 HullCentre(Raft raft, DeckPose pose) =>
        pose.Position + Vector3.Transform(new Vector3(0f, (raft.Freeboard - raft.Draft) / 2, 0f), pose.Rotation);

    /// <summary>
    /// Where <paramref name="raft"/> is as the physics has it: where its hull is (after a step, the
    /// pose the step moved it to), or, for a raft not kept here, as it was last set.
    /// </summary>
    private DeckPose At(Raft raft) => _hulls.TryGetValue(raft, out var hull) ? hull.At : raft.Current;

    /// <summary>
    /// Sets each active raft (<see cref="Rafts.Active"/>) as it is at <paramref name="time"/>
    /// (<see cref="Raft.Set(double)"/>, for everything that reads it this step) and puts its hull there,
    /// then sets it moving to where it will be a step later, so in the step Jolt carries what
    /// stands on it along. It is put there first, not only moved, so a jump in the world's clock
    /// (a world loaded, say) never sends it flying to catch up. The work is one raft's for each
    /// active one, however many there are in the world: one pose each, as a step that follows on
    /// from the last finds it where that one moved it to (see <see cref="MoveRaft"/>).
    /// </summary>
    private void MoveRafts(double time)
    {
        if (!_body.HasSea)
            return;
        var active = Rafts.Active;
        for (var i = 0; i < active.Count; i++)
            MoveRaft(active[i], time);
    }

    /// <summary>
    /// Sets <paramref name="raft"/> as it is at <paramref name="time"/>, puts its hull there and
    /// sets it moving to where it will be a step later. A step that follows on from the last (at
    /// the time the last moved it to, but for the frame clock's float rounding) finds it there
    /// already, its hull too, so that pose is used again rather than worked out afresh, and the
    /// hull isn't put where it already is: with 256 floes kept, their poses and hulls were most of
    /// a step's time.
    /// </summary>
    private void MoveRaft(Raft raft, double time)
    {
        if (_hulls.TryGetValue(raft, out var hull) && Math.Abs(time - hull.NextAt) < FollowsOn)
        {
            raft.Set(time, hull.Next);
            hull.At = hull.Next;
        }
        else
            hull = PutRaft(raft, time);
        hull.Next = raft.Pose(time + StepLength);
        hull.NextAt = time + StepLength;
        if (hull.Body is { } body)
            _bodies.MoveKinematic(body, ToFloat3(HullCentre(raft, hull.Next)), ToFloatQuat(hull.Next.Rotation), StepLength);
    }

    /// <summary>
    /// How near (seconds) a step's time must be to the time the step before moved the rafts to,
    /// to follow on from it (see <see cref="MoveRaft"/>): far more than the frame clock's float
    /// rounding, far less than a step, and too short for the waves to move anything measurably.
    /// If <see cref="StepScale"/> changes while the accumulator holds part of a step, the next
    /// step's time is no longer the one the rafts were moved to, so each is put afresh for that step.
    /// </summary>
    private const double FollowsOn = 1e-6;

    /// <summary>Sets every active raft as it is at <paramref name="time"/>, and puts its hull there (for a world loaded).</summary>
    private void PutRafts(double time)
    {
        if (!_body.HasSea)
            return; // the rafts are on the planet's sea alone
        var active = Rafts.Active;
        for (var i = 0; i < active.Count; i++)
            PutRaft(active[i], time);
    }

    /// <summary>Sets <paramref name="raft"/> as it is at <paramref name="time"/>, and puts its hull there.</summary>
    private Hull PutRaft(Raft raft, double time)
    {
        if (!_hulls.TryGetValue(raft, out var hull))
            _hulls[raft] = hull = new Hull(); // active without a body
        raft.Set(time);
        hull.At = hull.Next = raft.Current;
        hull.NextAt = double.NaN;
        if (hull.Body is { } body)
            _bodies.SetPositionAndRotation(body, ToFloat3(HullCentre(raft, hull.At)), ToFloatQuat(hull.At.Rotation), Activation.Activate);
        return hull;
    }

    /// <summary>After a step, each active raft is where the step moved it (<see cref="Hull.Next"/>).</summary>
    private void RaftsMoved()
    {
        var active = Rafts.Active;
        for (var i = 0; i < active.Count; i++)
            if (_hulls.TryGetValue(active[i], out var hull))
                hull.At = hull.Next;
    }

    /// <summary>
    /// How much further than <see cref="Scene.FloeReach"/> an awake craft can be from a floe before
    /// its body goes, so one hovering at the edge of reach doesn't add and remove it over and over.
    /// </summary>
    internal const float FloeLetGo = 10f;

    /// <summary>
    /// The square of how far <paramref name="q"/> is from the way from <paramref name="p"/> to
    /// <paramref name="p"/> + <paramref name="ahead"/>, given <paramref name="ahead"/>'s own square
    /// (<paramref name="aheadSquared"/>): squared, so a floe further than any needed is passed over
    /// without a square root (most of those looked for, as they are looked for with room to spare).
    /// </summary>
    private static float FromWaySquared(Vector3 q, Vector3 p, Vector3 ahead, float aheadSquared)
    {
        var along = aheadSquared > 1e-9f ? Math.Clamp(Vector3.Dot(q - p, ahead) / aheadSquared, 0f, 1f) : 0f;
        return Vector3.DistanceSquared(q, p + ahead * along);
    }

    /// <summary>How far (metres) a craft goes before the floes round it are looked for again (see <see cref="UpdateFloes"/>).</summary>
    private const float FloeSeekSlack = 4f;

    /// <summary>
    /// Chooses the floes kept up to date this step (<see cref="Rafts.Active"/>), at
    /// <paramref name="time"/>, and which of them are bodies. A floe is a body while an awake craft
    /// is within <see cref="Scene.FloeReach"/> of its rim, or of the way the craft goes in
    /// <see cref="Scene.FloeReachSeconds"/> from there, and keeps it until none is within
    /// <see cref="FloeLetGo"/> more, the nearest first, at most <see cref="Scene.MaxFloeBodies"/>
    /// of them; so a craft flying over the floes takes them up ahead and lets them go behind, and
    /// many crafts among them never make more bodies than that. A floe with a craft frozen aboard
    /// is kept up to date too (to carry it), body or not. Any other floe leaves: its body and hull
    /// go. One newly kept is put where it is now and set moving, as <see cref="MoveRafts"/> did the rest.
    /// </summary>
    private void UpdateFloes(double time)
    {
        if (!_body.HasSea)
            return; // no floes off the planet's sea
        if (_nearAt.Length <= Floes.Count)
        {
            var size = Floes.Count + 1; // numbered from 1
            (_nearAt, _wantedAt, _keptAt, _nearest) = (new int[size], new int[size], new int[size], new float[size]);
        }
        var step = ++_floeStep;

        // How near the nearest awake craft is to each floe within reach of one.
        _floesNear.Clear();
        foreach (var craft in _crafts)
        {
            if (craft.Frozen || craft.Removed)
                continue;
            var p = Position(craft);
            // Its reach: FloeReach round the way it goes in FloeReachSeconds (as the tiles' urgency
            // reaches along its way), so a fast one has the floes ahead as bodies before it gets
            // there, but not those beside and behind it a circle that far round would take up (at
            // the ground's top speed, the cap's 256 floe bodies at once, where the way ahead holds a
            // few dozen). Its speed is taken as no more than the ground's top speed: one faster is
            // above Scene.GroundTopSpeedHeight, where that reach finds no floe, and is held to it
            // as it comes lower (see Fall), so no reach is ever further than Floes made room for.
            var moving = Velocity(craft);
            if (moving.LengthSquared() > Scene.GroundTopSpeed * Scene.GroundTopSpeed)
                moving *= Scene.GroundTopSpeed / moving.Length();
            var ahead = moving * Scene.FloeReachSeconds;
            var reach = Scene.FloeReach + ahead.Length(); // the furthest from it that is within reach of its way
            if (_body.Altitude(p) > reach + FloeLetGo + Scene.FloeMaxRadius)
                continue; // far above the sea, and so far above every floe
            // The floes round it are looked for again only once it has gone FloeSeekSlack from
            // where they were last looked for, or its reach has grown that much, so many further
            // out, so they are always all there are within reach (as many as looking afresh every
            // step would find, in the same order): looking through the grid round every craft each
            // step was most of this.
            var found = craft.FloesSought;
            if (!(Vector3.DistanceSquared(p, craft.FloesSoughtAt) <= FloeSeekSlack * FloeSeekSlack)
                || reach + FloeLetGo + FloeSeekSlack > craft.FloesSoughtWithin)
            {
                found.Clear();
                craft.FloesSoughtWithin = reach + FloeLetGo + 2f * FloeSeekSlack;
                Floes.Near(p, craft.FloesSoughtWithin, found);
                craft.FloesSoughtAt = p;
            }
            var aheadSquared = ahead.LengthSquared();
            foreach (var floe in found)
            {
                var most = Scene.FloeReach + FloeLetGo + floe.HalfLength;
                var fromWay = FromWaySquared(floe.Centre, p, ahead, aheadSquared);
                if (fromWay > most * most)
                    continue; // further than any it needs (one of those looked for with room to spare)
                var near = MathF.Max(0f, MathF.Sqrt(fromWay) - floe.HalfLength);
                if (near > Scene.FloeReach && !(_hulls.TryGetValue(floe, out var had) && had.Body is not null))
                    continue; // only a body already can be kept that far off
                var id = floe.Id;
                if (_nearAt[id] != step)
                {
                    (_nearAt[id], _nearest[id]) = (step, near);
                    _floesNear.Add(floe);
                }
                else if (near < _nearest[id])
                    _nearest[id] = near;
            }
        }

        // The nearest are bodies, up to the cap (in order of nearness, then of number, so it is the same every run).
        _floeBodiesWanted.Clear();
        if (_floesNear.Count > Scene.MaxFloeBodies)
        {
            // Sorted by one key each, nearness then number: a distance (never negative) orders
            // as its float's bits do, so the two make one long, sorted as plain numbers are.
            // (The number is the floe's place in Floes.All, from 1, so the key alone says which it is.)
            var count = _floesNear.Count;
            if (_nearKeys.Length < count)
                _nearKeys = new long[count * 2];
            for (var i = 0; i < count; i++)
            {
                var id = _floesNear[i].Id;
                _nearKeys[i] = ((long)BitConverter.SingleToInt32Bits(_nearest[id]) << 32) | (uint)id;
            }
            Array.Sort(_nearKeys, 0, count);
            var all = Floes.All;
            for (var i = 0; i < Scene.MaxFloeBodies; i++)
                _floeBodiesWanted.Add(all[(int)(uint)_nearKeys[i] - 1]);
        }
        else
            _floeBodiesWanted.AddRange(_floesNear);
        foreach (var floe in _floeBodiesWanted)
            _wantedAt[floe.Id] = step;

        // Those, and any carrying a frozen craft, are kept up to date.
        _activeFloes.Clear();
        foreach (var floe in _floeBodiesWanted)
        {
            _keptAt[floe.Id] = step;
            _activeFloes.Add(floe);
        }
        foreach (var craft in _crafts)
            if (craft.Frozen && craft.Aboard is { Raft: { IsShip: false } floe } && _keptAt[floe.Id] != step)
            {
                _keptAt[floe.Id] = step;
                _activeFloes.Add(floe);
            }
        _activeFloes.Sort(static (a, b) => a.Id.CompareTo(b.Id)); // walked in the same order every run

        // Any other floe leaves; one no longer wanted as a body loses it (its hull taken out).
        _floesGone.Clear();
        foreach (var (raft, hull) in _hulls)
        {
            if (raft.IsShip)
                continue; // the ship stays
            if (_keptAt[raft.Id] != step)
                _floesGone.Add(raft);
            if (hull.Body is { } body && _wantedAt[raft.Id] != step)
            {
                _bodies.RemoveBody(body);
                _bodies.DestroyBody(body);
                ForgetContacts(body);
                _raftBodies.Remove(body);
                hull.Body = null;
                FloeBodyCount--;
            }
        }
        foreach (var raft in _floesGone)
            _hulls.Remove(raft);

        // One newly kept is set and moved now; one newly a body gets its hull where it is, moving as the rest.
        foreach (var floe in _activeFloes)
        {
            if (!_hulls.TryGetValue(floe, out var hull))
            {
                MoveRaft(floe, time);
                hull = _hulls[floe];
            }
            if (hull.Body is null && _wantedAt[floe.Id] == step)
            {
                var body = CreateHull(floe, hull.At);
                _bodies.MoveKinematic(body, ToFloat3(HullCentre(floe, hull.Next)), ToFloatQuat(hull.Next.Rotation), StepLength);
                hull.Body = body;
                FloeBodyCount++;
            }
        }
        Rafts.SetActiveFloes(_activeFloes);
    }

    /// <summary>
    /// The raft any of <paramref name="craft"/>'s lower points (see <see cref="LowPoints"/>) is
    /// over, or null: not only its base, so one lying across a deck's edge, its base out past it,
    /// is aboard too (frozen as anything else, it would hang there as the deck heaved). One lying
    /// across two (two floes) is aboard the one under the first of those points over either,
    /// the same every time.
    /// </summary>
    private Raft? RaftUnder(Craft craft)
    {
        Span<Vector3> points = stackalloc Vector3[LowPointCount];
        if (!_body.HasSea)
            return null;
        foreach (var point in points[..LowPoints(craft, points)])
            if (Rafts.Under(point) is { } under)
                return under.Raft;
        return null;
    }

    /// <summary><paramref name="p"/> and <paramref name="rotation"/> in the frame of a raft at <paramref name="deck"/>.</summary>
    private static (Vector3 Position, Quaternion Rotation) OnDeck(DeckPose deck, Vector3 p, Quaternion rotation)
    {
        var inverse = Quaternion.Inverse(deck.Rotation);
        return (Vector3.Transform(p - deck.Position, inverse), Quaternion.Normalize(inverse * rotation));
    }

    /// <summary><paramref name="craft"/> aboard <paramref name="raft"/>, frozen at <paramref name="p"/> turned <paramref name="rotation"/>, as the raft is now.</summary>
    private void Board(Craft craft, Raft raft, Vector3 p, Quaternion rotation)
    {
        var (position, turned) = OnDeck(At(raft), p, rotation);
        craft.Aboard = (raft, position, turned);
    }

    /// <summary>
    /// Each craft frozen on a raft put where the raft has carried it, as it is now
    /// (<see cref="Craft.Aboard"/>). Frozen, no craft may be moved by Jolt either: should a moving
    /// hull have woken one by touching it (one afloat beside it as much as one aboard), it is
    /// stopped again at once, as <see cref="UpdateTiles"/> does for one woken by a tile taken out.
    /// With no gravity, buoyancy or drag acting on a frozen craft, one not aboard is also put back
    /// as it was frozen, or it would drift while its drawn pose stayed put.
    /// </summary>
    private void Carry()
    {
        foreach (var craft in _crafts)
        {
            if (!craft.Frozen)
                continue;
            if (craft.Aboard is { } aboard)
            {
                var deck = At(aboard.Raft);
                var at = deck.Position + Vector3.Transform(aboard.Position, deck.Rotation);
                _bodies.SetPositionAndRotation(craft.Id, ToFloat3(at), ToFloatQuat(Quaternion.Normalize(deck.Rotation * aboard.Rotation)), Activation.DontActivate);
                craft.FrozenAt = at;
            }
            if (_bodies.IsActive(craft.Id))
            {
                // Touched by another craft (one coming down on it, or pushing it, too slowly to
                // have woken it by coming near), it is woken properly: it can be pushed, knocked
                // over or wrecked. Its ground is laid before the next step. (Jolt began a contact
                // with it in the step that woke it: one between two awake bodies, read just now.)
                if (Touching(craft, SolidKind.Craft))
                {
                    MarkAwake(craft);
                    continue;
                }
                if (craft.Aboard is null)
                    _bodies.SetPositionAndRotation(craft.Id, ToFloat3(craft.FrozenAt), ToFloatQuat(craft.FrozenRotation), Activation.DontActivate);
                _bodies.SetLinearVelocity(craft.Id, new float3(0, 0, 0));
                _bodies.SetAngularVelocity(craft.Id, new float3(0, 0, 0));
                _bodies.DeactivateBody(craft.Id);
            }
        }
    }

    /// <summary>
    /// A tilted landing zone (a test pad), as one body: its top, a thin tilted slab that is all a
    /// rocket stands on; and under it, its plinth. Jolt can't be given a convex hull (see
    /// CLAUDE.md), and a cylinder can't be squashed to the plinth's ellipse, so the plinth is a row
    /// of upright boxes across the slope, inside the drawn plinth and reaching up into the slab
    /// (<see cref="Plinth.Columns"/>, which says how closely they fit). The slab's uphill edge
    /// stands out past the wall by its thickness times the slope's sine, a
    /// lip at the top of the wall: left, as only something coming at that wall from outside touches it.
    /// </summary>
    private void AddTiltedZone(Plinth plinth)
    {
        const float slab = Plinth.SlabThickness;
        var (sin, cos) = MathF.SinCos(plinth.Slope);
        var parts = new List<ShapeSettings>();
        using var compound = StaticCompoundShapeSettings.Create();
        var top = CylinderShapeSettings.Create(slab / 2, Scene.LandingZoneRadius, convexRadius: 0.05f);
        parts.Add(top);
        var slabCentre = new Vector3(sin, cos, 0f) * (Scene.PadTop - slab / 2);
        compound.AddShape(ToFloat3(slabCentre), ToFloatQuat(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -plinth.Slope)), top);

        foreach (var (x0, x1, halfAlong, high) in plinth.Columns)
        {
            var halfHeight = (high - plinth.Bottom) / 2;
            var halfWidth = (x1 - x0) / 2;
            // The narrowest columns, at the ends, are only centimetres wide: the rounding must fit in them.
            var column = BoxShapeSettings.Create(new float3(halfWidth, halfHeight, halfAlong), convexRadius: MathF.Min(0.05f, halfWidth / 2));
            parts.Add(column);
            compound.AddShape(new float3((x0 + x1) / 2, plinth.Bottom + halfHeight, 0f), floatQuat.Identity, column);
        }

        AddStatic(compound, ToFloat3(plinth.Centre), plinth.Frame, friction: 0.8f, SolidKind.Ground);
        foreach (var part in parts)
            part.Dispose();
    }

    /// <summary>
    /// A launch pad: Home's one box, <paramref name="depth"/> deep; a far one on its
    /// <paramref name="footing"/> (<see cref="AddFooting"/>).
    /// </summary>
    private void AddPad(LaunchSite site, Footing? footing, float depth)
    {
        if (footing is not null)
        {
            AddFooting(footing, site.Pad, site.Frame);
            return;
        }
        using var pad = BoxShapeSettings.Create(new float3(Scene.PadHalfSize, depth / 2, Scene.PadHalfSize));
        AddStatic(pad, ToFloat3(site.ToWorld(new Vector3(0f, Scene.PadTop - depth / 2, 0f))), site.Frame, friction: 0.8f, SolidKind.Ground);
    }

    /// <summary>
    /// A far pad's or far zone's <paramref name="footing"/> (in the frame <paramref name="frame"/>
    /// about <paramref name="centre"/>), as one body: its <see cref="Footing.Boxes"/>, the slab
    /// across the whole top (one surface, with no seams for a foot to catch on) and the steps
    /// under it following its taper in, boxes under a square pad and cylinders under a round zone.
    /// </summary>
    private void AddFooting(Footing footing, Vector3 centre, Quaternion frame)
    {
        var parts = new List<ShapeSettings>();
        using var compound = StaticCompoundShapeSettings.Create();
        foreach (var box in footing.Boxes)
        {
            var halfHeight = (box.Depth - box.Top) / 2;
            // The last step, near the taper's point, can be only centimetres across or tall: the rounding must fit in it.
            var rounding = MathF.Min(0.05f, MathF.Min(halfHeight, box.Half) / 2);
            ShapeSettings part = footing.Outline == FootingOutline.Square
                ? BoxShapeSettings.Create(new float3(box.Half, halfHeight, box.Half), convexRadius: rounding)
                : CylinderShapeSettings.Create(halfHeight, box.Half, convexRadius: rounding);
            parts.Add(part);
            compound.AddShape(new float3(0f, Scene.PadTop - box.Top - halfHeight, 0f), floatQuat.Identity, part);
        }
        AddStatic(compound, ToFloat3(centre), frame, friction: 0.8f, SolidKind.Ground);
        foreach (var part in parts)
            part.Dispose();
    }

    /// <summary>A piece of scenery that never moves, of <paramref name="kind"/> (what a craft touching it touched: see <see cref="TouchesBegun"/>).</summary>
    private void AddStatic(ShapeSettings shapeSettings, float3 centre, Quaternion rotation, float friction, SolidKind kind)
    {
        using var shape = shapeSettings.CreateShape();
        using var settings = BodyCreationSettings.Create(shape, centre, ToFloatQuat(rotation), MotionType.Static, NonMoving);
        settings.SetFriction(friction);
        settings.SetRestitution(0.1f);
        settings.SetUserData((ulong)kind);
        _staticBodies.Add(_bodies.CreateAndAddBody(settings, Activation.DontActivate));
    }

    /// <summary>
    /// The collider for <paramref name="vehicle"/>'s stages <paramref name="first"/> to
    /// <paramref name="last"/>, in the stack's own space: each stage's body and nose (or interstage), the bottom stage's engine bell
    /// and, once a booster's legs are out, a pad under each foot. With its legs stowed it stands on
    /// the narrow bell alone, so it tips over easily; with them deployed it has a wide, steady stance.
    /// With <paramref name="withFairing"/>, the top stage's fairing round it too: a cylinder of its
    /// radius, and a narrower one for its nose. A lander's body is a box, its corners out to its radius.
    /// </summary>
    /// <remarks>
    /// The centre of mass is moved down low, near the heavy engines at the bottom, as a nearly empty
    /// rocket's is, which makes it as stable on its legs as it should be. The shapes with and
    /// without the fairing get the same centre of mass. The legs-out one's is lower on purpose
    /// (0.058 m for a booster): see the comment where the feet are added.
    /// </remarks>
    private static Shape CreateShape(Vehicle vehicle, int first, int last, bool withFeet, bool withFairing, Vector2? doorsOpen = null)
    {
        var parts = new List<ShapeSettings>();
        using var compound = StaticCompoundShapeSettings.Create();
        var weightedY = 0f;
        var volume = 0f;

        void Cylinder(float y0, float y1, float radius)
        {
            var half = (y1 - y0) / 2;
            var part = CylinderShapeSettings.Create(half, radius, convexRadius: MathF.Min(0.1f, radius * 0.2f));
            compound.AddShape(new float3(0, y0 + half, 0), floatQuat.Identity, part);
            parts.Add(part);
            var v = MathF.PI * radius * radius * (y1 - y0);
            weightedY += v * (y0 + half);
            volume += v;
        }

        void Box(Vector3 half, Vector3 centre, Quaternion? turned = null, float convexRadius = 0.05f)
        {
            var part = BoxShapeSettings.Create(ToFloat3(half), convexRadius);
            compound.AddShape(ToFloat3(centre), ToFloatQuat(turned ?? Quaternion.Identity), part);
            parts.Add(part);
            var v = 8 * half.X * half.Y * half.Z;
            weightedY += v * centre.Y;
            volume += v;
        }

        for (var i = first; i <= last; i++)
        {
            var stage = vehicle.Stages[i];
            var y = RocketGeometry.StageBase(vehicle, first, i);
            var r = stage.Radius;
            if (i == first && stage.BellHeight > 0f && stage.Kind != StageKind.Fairing)
                Cylinder(y - stage.BellHeight, y, stage.BellRadius); // only the bottom bell is out in the open
            switch (stage.Kind)
            {
                case StageKind.Booster:
                    Cylinder(y, y + Scene.RocketBodyHeight, r);
                    if (stage.Nose)
                        Cylinder(y + Scene.RocketBodyHeight, y + Scene.RocketBodyHeight + Scene.RocketNoseHeight, r * 0.55f);
                    else
                        Cylinder(y + Scene.RocketBodyHeight, y + Scene.RocketBodyHeight + Scene.InterstageHeight, r);
                    break;
                case StageKind.Upper:
                    Cylinder(y, y + Scene.UpperStageHeight, r);
                    if (stage.Nose)
                        Cylinder(y + Scene.UpperStageHeight, y + Scene.UpperStageHeight + Scene.UpperNoseHeight, r * 0.55f);
                    break;
                case StageKind.Satellite:
                    Cylinder(y, y + Scene.SatelliteBodyHeight, r);
                    Cylinder(y + Scene.SatelliteBodyHeight, y + stage.Height, 0.15f); // the antenna mast
                    break;
                case StageKind.Lander:
                    // Its square body, bay and all (the doors closed are its sides), and the antenna mast.
                    Box(new Vector3(Scene.LanderHalfWidth, Scene.LanderBodyHeight / 2, Scene.LanderHalfWidth), new Vector3(0, y + Scene.LanderBodyHeight / 2, 0));
                    Cylinder(y + Scene.LanderBodyHeight, y + stage.Height, 0.1f);
                    // Its bay's doors open (each swung out as far as doorsOpen says), each unfolded
                    // into one straight ramp down from the floor's edge, a thin box the ramp's
                    // length; or shut, the panels folded against each door outside its face, a box
                    // as deep as them. Counted in its volume as the rest, so the centre of mass
                    // stays where it is.
                    var rampsOut = doorsOpen is not null && i == first;
                    for (var side = -1; !rampsOut && side <= 1; side += 2)
                        Box(new Vector3(RocketGeometry.FoldedDepth / 2, Scene.LanderDoorHeight / 2, RocketGeometry.PanelHalfWidth),
                            new Vector3(side * (Scene.LanderHalfWidth + RocketGeometry.FoldedDepth / 2), y + Scene.LanderBayFloor + Scene.LanderDoorHeight / 2, 0), convexRadius: 0.02f);
                    for (var door = 0; doorsOpen is { } swung && rampsOut && door < RocketGeometry.DoorCount; door++)
                    {
                        var hinge = RocketGeometry.DoorAt(door, door == 0 ? swung.X : swung.Y) * Matrix4x4.CreateTranslation(0, y, 0);
                        var turned = Quaternion.CreateFromRotationMatrix(hinge);
                        var t = Scene.LanderDoorThickness;
                        Box(new Vector3(t / 2, RocketGeometry.RampLength / 2, RocketGeometry.PanelHalfWidth), Vector3.Transform(new Vector3(-t / 2, RocketGeometry.RampLength / 2, 0), hinge), turned, convexRadius: t / 4);
                    }
                    break;
                default:
                {
                    // A fairing half: a curved shell on one side of the axis (+Z), as a slab; and
                    // under it its foot's cone if it has one (its BellHeight, narrowing to its
                    // BellRadius), as slabs stepping in with it, each as wide as the cone half-way
                    // down it, so a half on its side lies on the cone as drawn.
                    Box(new Vector3(r, stage.Height / 2, r / 2), new Vector3(0, y + stage.Height / 2, r / 2));
                    const int coneSteps = 2;
                    var cone = stage.BellHeight;
                    for (var step = 0; cone > 0f && step < coneSteps; step++)
                    {
                        var across = stage.BellRadius + (r - stage.BellRadius) * (step + 0.5f) / coneSteps;
                        var bottom = y - cone + cone * step / coneSteps;
                        Box(new Vector3(across, cone / coneSteps / 2, across / 2), new Vector3(0, bottom + cone / coneSteps / 2, across / 2));
                    }
                    break;
                }
            }
        }

        if (withFairing)
        {
            var y = RocketGeometry.StageBase(vehicle, first, last);
            var fairing = vehicle.Stages[last].FairingGeometry;
            if (fairing.ConeHeight > 0f)
                Cylinder(y - fairing.ConeHeight, y, (fairing.Radius + Scene.RocketRadius) / 2); // its foot's cone, as wide as it is half-way down
            Cylinder(y, y + fairing.Height, fairing.Radius);
            Cylinder(y + fairing.Height, y + fairing.Height + fairing.NoseHeight, fairing.Radius * 0.55f);
        }

        // The feet are left out of this on purpose, so with the legs out the centre of mass sits a
        // little lower than with them stowed (0.058 m for a booster): a small aid to standing,
        // which the steep pads' stance is tuned with. Counting the feet (so swapping shapes shifts
        // nothing) tipped the 25° pad stand: it strayed 6.7° over 20 s, against the tests' 0.5°.
        // RocketGeometry.CentreOfMassOffset gives the geometric value, without this.
        // (It ends up low down whatever the parts, so the shapes with and without the fairing share it.)
        var centreOfMass = weightedY / volume;
        if (withFeet)
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
            {
                var foot = RocketGeometry.Foot(vehicle.Stages[first].LegGeometry, leg) + new Vector3(0, RocketGeometry.FootThickness / 2, 0);
                var pad = vehicle.Stages[first].LegGeometry.FootHalfWidth;
                var part = BoxShapeSettings.Create(new float3(pad, RocketGeometry.FootThickness / 2, pad), convexRadius: 0.02f);
                compound.AddShape(ToFloat3(foot), floatQuat.Identity, part);
                parts.Add(part);
            }

        using var compoundShape = compound.CreateShape();
        foreach (var part in parts)
            part.Dispose(); // the compound holds its own references

        var low = RocketGeometry.CentreOfMassHeight(vehicle, first, last);
        using var lowered = OffsetCenterOfMassShapeSettings.Create(new float3(0, low - centreOfMass, 0), compoundShape);
        return lowered.CreateShape();
    }

    /// <param name="fairing">Whether its top stage's fairing is on: by default, if that stage has one.</param>
    /// <param name="frozen">Whether it comes into the world frozen (still, and left out of the simulation).</param>
    /// <param name="partingFrom">The craft it has just come away from, if it has: it parts from it before they can collide (see <see cref="Craft.PartingFrom"/>).</param>
    private Craft AddCraft(Vehicle vehicle, int first, int last, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity,
        bool? fairing = null, bool frozen = false, Craft? partingFrom = null)
    {
        if (frozen)
            (velocity, angularVelocity) = (Vector3.Zero, Vector3.Zero);
        var hasLegs = vehicle.Stages[first].Legs;
        var fairingOn = fairing ?? vehicle.Stages[last].Fairing;
        var stowed = CreateShape(vehicle, first, last, withFeet: false, fairingOn);
        var deployed = hasLegs ? CreateShape(vehicle, first, last, withFeet: true, fairingOn) : null;
        var id = AddBody(stowed, position, rotation, velocity, angularVelocity, frozen, partingFrom is not null);
        var craft = new Craft(vehicle, first, last, id, stowed, deployed)
        {
            FairingOn = fairingOn, Frozen = frozen, FrozenAt = position, FrozenRotation = rotation, PartingFrom = partingFrom,
        };
        craft.Into(this);
        _crafts.Add(craft);
        _craftBodies[id] = craft;
        return craft;
    }

    /// <summary>A craft's body, of <paramref name="shape"/>, put in the world moving so: its id.</summary>
    private uint AddBody(Shape shape, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity, bool frozen, bool parting)
    {
        using var settings = BodyCreationSettings.Create(shape, ToFloat3(position), ToFloatQuat(rotation), MotionType.Dynamic, parting ? Parting : Moving);
        settings.SetFriction(0.9f);
        settings.SetRestitution(0.05f);
        settings.SetLinearDamping(0f);  // drag is applied per step, by how thick the air is
        settings.SetAngularDamping(AngularDamping);
        // Never reached, so Jolt never clamps: the world is its ground's turning frame, which itself
        // moves at the turn times the distance from the axis (past Scene.GroundTopSpeed some 700 km
        // out from the planet's), so a cap of Jolt's would take hold of anything far out among the
        // stars and pull it off its path. The game holds the speed low over the ground itself (see Fall).
        settings.SetMaxLinearVelocity(JoltTopSpeed);
        settings.SetAllowSleeping(false); // gravity, the engine and steering act on it every step
        settings.SetMotionQuality(MotionQuality.LinearCast); // a fast fall mustn't pass through the pad
        settings.SetLinearVelocity(ToFloat3(velocity));
        settings.SetAngularVelocity(ToFloat3(angularVelocity));
        settings.SetUserData((ulong)SolidKind.Craft);
        return _bodies.CreateAndAddBody(settings, frozen ? Activation.DontActivate : Activation.Activate);
    }

    /// <summary>
    /// Takes <paramref name="craft"/> out of this world to be taken into another's
    /// (<see cref="TakeIn"/>), across a sphere's edge: its body taken out (and its contacts
    /// forgotten), but the craft itself and its shapes kept, with its motion as it is now, in this
    /// world's frame. Anything frozen resting on it is woken, as in <see cref="Remove(Craft)"/>.
    /// </summary>
    public CraftState TakeOut(Craft craft)
    {
        var state = new CraftState(craft, Position(craft), TrueCentreOfMass(craft), Rotation(craft), TrueVelocity(craft), AngularVelocity(craft));
        Remove(craft, wakeWhatRestsOnIt: true, keep: true);
        return state;
    }

    /// <summary>
    /// Takes the craft of <paramref name="state"/> (taken out of another world by
    /// <see cref="TakeOut"/>, its motion changed into this one's frame) into this world: a body of
    /// its own shapes (with its feet if its legs are out and its ramps if its doors are open, its mass as without them, as
    /// <see cref="Restore"/> has it), moving and turning so, still parting from what it was
    /// parting from if that crosses too (each vessel crosses on its own: one left in the other
    /// world can touch nothing here, and <see cref="EndParting"/> lets it go). The same craft, now this world's,
    /// its double copy of its motion carried across (see <see cref="TrueCentreOfMass"/>) and Jolt handed it rounded.
    /// </summary>
    public Craft TakeIn(in CraftState state)
    {
        var craft = state.Craft;
        var id = AddBody(craft.Stowed, state.Position, state.Rotation, state.Velocity.ToSingle(), state.AngularVelocity, frozen: false, craft.PartingFrom is not null);
        craft.Rehome(id, this);
        if (craft.LegsDeployed || craft.DoorsOpen)
            _bodies.SetShape(id, craft.Collider, updateMassProperties: false, Activation.Activate);
        (craft.KeptCentre, craft.KeptVelocity, craft.Kept) = (state.CentreOfMass, state.Velocity, true);
        PutCentre(craft, state.CentreOfMass);
        _crafts.Add(craft);
        _craftBodies[id] = craft;
        return craft;
    }
    /// <summary>
    /// A new craft that is all of <paramref name="vehicle"/>, loose in the world: e.g. half of a
    /// jettisoned fairing, parting from <paramref name="partingFrom"/> (the payload it came off).
    /// </summary>
    public Craft AddLoose(Vehicle vehicle, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity, Craft? partingFrom = null) =>
        AddCraft(vehicle, 0, vehicle.Stages.Length - 1, position, rotation, velocity, angularVelocity, partingFrom: partingFrom);

    /// <summary>
    /// Puts <paramref name="craft"/> back to parting from <paramref name="from"/> (see
    /// <see cref="Craft.PartingFrom"/>): for a world loaded just after staging, so the two go on
    /// coming apart as they were, not colliding while still inside each other.
    /// </summary>
    internal void Part(Craft craft, Craft from)
    {
        if (craft.Removed || from.Removed)
            return;
        craft.PartingFrom = from;
        _bodies.SetObjectLayer(craft.Id, Parting);
    }

    /// <summary>
    /// Each craft parting from another that is now clear of it (or whose other has gone) joins the
    /// moving crafts, to collide with them all from now on.
    /// </summary>
    private void EndParting()
    {
        foreach (var craft in _crafts)
        {
            if (craft.PartingFrom is not { } other || craft.Removed)
                continue;
            // (One gone into another body's world, across a sphere's edge without it, is parted from: it can touch nothing here.)
            if (!other.Removed && other.World == this && Apart(craft, other, 0f) < 0f)
                continue;
            craft.PartingFrom = null;
            _bodies.SetObjectLayer(craft.Id, Moving);
        }
    }

    /// <summary>
    /// A craft's shape, near enough to tell what it touches by: its axis, from the bottom of its
    /// engine bell to its top, as wide as its widest radius; and, with its legs out, its feet
    /// (the collider's pads, reaching well out past its sides), each taken as a ball as wide as
    /// its pad (whose corners, 0.41 of its half-width further out, 0.19 m for the rocket's, are
    /// inside the margins touching is told by).
    /// The feet are where the collider has them: it has its feet only once the legs are all the
    /// way out (<see cref="Craft.LegsDeployed"/>: see <see cref="PutLegs"/>), so swinging out or in
    /// they aren't there to touch anything, and once there they are at full deploy.
    /// </summary>
    private readonly struct Capsule
    {
        public readonly Vector3 Bottom, Top;
        public readonly float Radius;
        public readonly bool HasFeet;
        /// <summary>How wide each foot is taken to be: half its pad's width (<see cref="LegGeometry.FootHalfWidth"/>).</summary>
        public readonly float FootRadius;
        /// <summary>How far out from its axis anything of it reaches: its radius, or its feet's.</summary>
        public readonly float Reach;
        private readonly Vector3 _foot0, _foot1, _foot2, _foot3;

        public Capsule(Vector3 bottom, Vector3 top, float radius, in Matrix4x4 model, bool feet, LegGeometry legs, float bell)
        {
            (Bottom, Top, Radius, HasFeet, Reach, FootRadius) = (bottom, top, radius, feet, radius, legs.FootHalfWidth);
            (_foot0, _foot1, _foot2, _foot3) = (Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);
            if (!feet)
                return;
            (_foot0, _foot1, _foot2, _foot3) = (FootAt(legs, 0, model), FootAt(legs, 1, model), FootAt(legs, 2, model), FootAt(legs, 3, model));
            var foot = FootCentre(legs, 0); // all four are as far out, and as low
            var below = MathF.Max(0f, -bell - foot.Y);
            Reach = MathF.Max(radius, MathF.Sqrt(foot.X * foot.X + foot.Z * foot.Z + below * below) + FootRadius);
        }

        public Vector3 Foot(int leg) => leg switch { 0 => _foot0, 1 => _foot1, 2 => _foot2, _ => _foot3 };

        private static Vector3 FootCentre(LegGeometry legs, int leg) => RocketGeometry.Foot(legs, leg) + new Vector3(0, RocketGeometry.FootThickness / 2, 0);
        private static Vector3 FootAt(LegGeometry legs, int leg, in Matrix4x4 model) => Vector3.Transform(FootCentre(legs, leg), model);
    }

    /// <summary>
    /// How far apart two crafts are, roughly (see <see cref="Capsule"/>): their bodies' axes less
    /// both their widest radii, or a foot's ball and the other's body or feet, whichever are
    /// nearer. Below 0, they may be touching. Exact enough below <paramref name="margin"/>, which
    /// is what it is asked about; at or over it, it may be anything at or over it (one whose feet
    /// can't come within the margin of the other isn't looked at more closely).
    /// </summary>
    internal float Apart(Craft a, Craft b, float margin) => Apart(Outline(a), Outline(b), margin);

    private static float Apart(in Capsule a, in Capsule b, float margin)
    {
        var axes = SegmentDistance(a.Bottom, a.Top, b.Bottom, b.Top);
        var apart = axes - a.Radius - b.Radius;
        if (!a.HasFeet && !b.HasFeet)
            return apart;
        var nearest = axes - a.Reach - b.Reach; // nothing of either is any nearer the other than this
        if (nearest >= margin)
            return nearest;
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            if (a.HasFeet)
                apart = MathF.Min(apart, SegmentDistance(a.Foot(leg), a.Foot(leg), b.Bottom, b.Top) - a.FootRadius - b.Radius);
            if (b.HasFeet)
                apart = MathF.Min(apart, SegmentDistance(b.Foot(leg), b.Foot(leg), a.Bottom, a.Top) - b.FootRadius - a.Radius);
            if (a.HasFeet && b.HasFeet)
                for (var other = 0; other < RocketGeometry.LegCount; other++)
                    apart = MathF.Min(apart, Vector3.Distance(a.Foot(leg), b.Foot(other)) - a.FootRadius - b.FootRadius);
        }
        return apart;
    }

    /// <summary>
    /// A craft's shape, as touching is told by (see <see cref="Capsule"/>), as it is now: with its
    /// bay's doors open, its axis as wide as its ramps reach (<see cref="RocketGeometry.RampReach"/>),
    /// the whole way up (a little more than it is, above them).
    /// </summary>
    private Capsule Outline(Craft craft)
    {
        var model = Model(craft);
        var bell = craft.Vehicle.Stages[craft.First].BellHeight;
        var height = craft.Height;
        var radius = craft.WidestOpen;
        return new Capsule(Vector3.Transform(new Vector3(0, -bell, 0), model), Vector3.Transform(new Vector3(0, height, 0), model), radius,
            model, craft.LegsDeployed, craft.Legs, bell);
    }

    /// <summary>
    /// The crafts wrecked (see <see cref="Wreck"/>) still left in the world for their hit to play out.
    /// </summary>
    private readonly List<Craft> _wrecks = [];

    /// <summary>
    /// Wrecks <paramref name="craft"/>: it is taken out of the world (see <see cref="Remove"/>), but
    /// only after <see cref="Scene.WreckSeconds"/>, while a hit it took part in plays out. A hit can
    /// wreck it in its first moment, while the push it gives what it hit goes on into the next;
    /// taken away at once, it would leave that only the first moment's. Nothing flies it meanwhile.
    /// </summary>
    public void Wreck(Craft craft)
    {
        if (craft.Removed || craft.WreckedFor is not null)
            return;
        craft.WreckedFor = 0f;
        _wrecks.Add(craft);
    }

    /// <summary>Takes out of the world the wrecks whose hits have played out, a step on.</summary>
    private void ClearWrecks()
    {
        for (var i = _wrecks.Count - 1; i >= 0; i--)
        {
            var wreck = _wrecks[i];
            wreck.WreckedFor += StepLength;
            if (wreck.Removed || wreck.WreckedFor >= Scene.WreckSeconds)
            {
                _wrecks.RemoveAt(i);
                Remove(wreck);
            }
        }
    }

    /// <summary>
    /// Takes every wreck out of the world now, its hit played out or not: for a new flight, whose
    /// rocket must find nothing of an old one where it stands (the menu that starts one pauses the
    /// game, so no steps would pass to clear them by themselves).
    /// </summary>
    public void FlushWrecks()
    {
        foreach (var wreck in _wrecks)
            Remove(wreck);
        _wrecks.Clear();
    }

    /// <summary>The closest two points of segments <paramref name="p0"/>-<paramref name="p1"/> and <paramref name="q0"/>-<paramref name="q1"/> are this far apart.</summary>
    private static float SegmentDistance(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        var (d1, d2, r) = (p1 - p0, q1 - q0, p0 - q0);
        var (a, e, f) = (Vector3.Dot(d1, d1), Vector3.Dot(d2, d2), Vector3.Dot(d2, r));
        float s, t;
        if (a <= 1e-6f && e <= 1e-6f)
            return r.Length();
        if (a <= 1e-6f)
            (s, t) = (0f, Math.Clamp(f / e, 0f, 1f));
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-6f)
                (s, t) = (Math.Clamp(-c / a, 0f, 1f), 0f);
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom > 1e-6f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                    (s, t) = (Math.Clamp(-c / a, 0f, 1f), 0f);
                else if (t > 1f)
                    (s, t) = (Math.Clamp((b - c) / a, 0f, 1f), 1f);
            }
        }
        return Vector3.Distance(p0 + d1 * s, q0 + d2 * t);
    }

    /// <summary>
    /// Takes <paramref name="craft"/> out of the world, e.g. when it has blown up. Anything frozen
    /// resting on it (or against it) is woken, as what held it is gone.
    /// </summary>
    public void Remove(Craft craft) => Remove(craft, wakeWhatRestsOnIt: true);

    /// <param name="keep">Whether the craft and its shapes are kept, to be taken into another world (<see cref="TakeOut"/>).</param>
    private void Remove(Craft craft, bool wakeWhatRestsOnIt, bool keep = false)
    {
        if (craft.Removed)
            return;
        var capsule = Outline(craft);
        _bodies.RemoveBody(craft.Id);
        _bodies.DestroyBody(craft.Id);
        ForgetContacts(craft.Id); // at once: Jolt reports their end only in its next step, and none for a frozen one
        _craftBodies.Remove(craft.Id);
        craft.TouchesBegun.Clear();
        if (!keep)
        {
            craft.Dispose();
            craft.Removed = true;
        }
        _crafts.Remove(craft);
        if (!wakeWhatRestsOnIt)
            return;
        var reach = Vector3.Distance(capsule.Bottom, capsule.Top) / 2f + capsule.Reach;
        var middle = (capsule.Bottom + capsule.Top) / 2f;
        foreach (var other in _crafts)
        {
            // Only frozen ones within reach are measured (their pose is kept: no query for those far off).
            if (!other.Frozen || Vector3.Distance(other.FrozenAt, middle) > reach + RocketGeometry.StackHeight(other.Vehicle, other.First, other.Last) + 10f)
                continue;
            if (Apart(Outline(other), capsule, TouchMargin) < TouchMargin)
                MarkAwake(other);
        }
    }

    /// <summary>How close another craft has to be (see <see cref="Apart"/>) to be touching, roughly (metres).</summary>
    private const float TouchMargin = 0.5f;

    /// <summary>Clears the world of crafts and puts a fresh <paramref name="vehicle"/> on <paramref name="site"/>'s pad, upright and still, with its legs stowed.</summary>
    public Craft Reset(Vehicle vehicle, LaunchSite site)
    {
        Clear();
        return Launch(vehicle, site);
    }

    /// <summary>Takes every craft out of the world.</summary>
    public void Clear()
    {
        while (_crafts.Count > 0)
            Remove(_crafts[^1], wakeWhatRestsOnIt: false);
        _wrecks.Clear(); // (taken out with the rest)
        _accumulator = 0f;
    }

    /// <summary>Puts a fresh <paramref name="vehicle"/> on <paramref name="site"/>'s pad, upright and still, with its legs stowed, leaving every other craft where it is.</summary>
    public Craft Launch(Vehicle vehicle, LaunchSite site) =>
        AddCraft(vehicle, 0, vehicle.Stages.Length - 1, site.RocketStart, site.Frame, Vector3.Zero, Vector3.Zero);

    /// <summary>
    /// Sets the sea's waves, and the rafts riding them, as they are at the world's time
    /// <paramref name="worldTime"/> (seconds), until the next <see cref="Update"/>: for a world
    /// being loaded, so a craft restored frozen (see <see cref="Restore"/>) is found in the water,
    /// or on the deck or a floe, as it is then. The floes kept are chosen afresh (none, for a world
    /// cleared to be loaded: one restored aboard a floe brings it back at the first step).
    /// </summary>
    public void WavesAt(double worldTime)
    {
        if (!_body.HasSea)
            return;
        Sea.Phases(worldTime, _phases);
        Rafts.Time = worldTime;
        UpdateFloes(worldTime);
        PutRafts(worldTime);
    }

    /// <summary>
    /// How far <paramref name="p"/> is under the sea's waves as they are this step (negative above
    /// them), or null where there is no sea under it: over land, or over a raft (on a floe or the
    /// drone ship's deck, or in its hull: see <see cref="Rafts.Under"/>). A floe's top is only its
    /// freeboard over the calm sea, and lies flat where the waves under its rim don't, so a point
    /// on it is often under the water's height there; but it is on the ice, not in the sea.
    /// </summary>
    public float? DepthUnder(Vector3 p)
    {
        var direction = _body.Up(p);
        if (!_body.HasSea || _body.Height(direction) >= 0f || Rafts.Under(p) is not null)
            return null;
        return Sea.Height(direction, _phases) - _body.Altitude(p);
    }

    /// <summary>How the water is moving at the surface over <paramref name="p"/>, as the waves are this step (see <see cref="Sea.Flow"/>).</summary>
    public Vector3 FlowAt(Vector3 p) => _body.HasSea ? Sea.Flow(_body.Up(p), _phases) : Vector3.Zero;

    /// <summary>How the water at the sea's surface over <paramref name="p"/> moves this step: its flow along the ground, and its rise and fall (<see cref="Sea.Rise"/>).</summary>
    public Vector3 WaterAt(Vector3 p)
    {
        if (!_body.HasSea)
            return Vector3.Zero;
        var up = _body.Up(p);
        return Sea.Flow(up, _phases) + up * Sea.Rise(up, _phases);
    }

    /// <summary>
    /// Brings back a craft as it was (e.g. from a saved world): <paramref name="vehicle"/>'s stages
    /// <paramref name="first"/> to <paramref name="last"/>, where it was, moving and turning as it was,
    /// its legs as far out as they were, its bay's doors as far open and its fairing on or off. With <paramref name="frozen"/>,
    /// it comes back frozen (still: see <see cref="Freeze"/>), as a landed one should, so a world of
    /// them loads without laying the ground under every one.
    /// </summary>
    public Craft Restore(Vehicle vehicle, int first, int last, Vector3 position, Quaternion rotation, Vector3 velocity,
        Vector3 angularVelocity, float legDeploy, bool fairingOn, bool frozen = false, float bayDoors = 0f, Vector2? doorStops = null)
    {
        var craft = AddCraft(vehicle, first, last, position, rotation, velocity, angularVelocity, fairingOn, frozen);
        PutLegs(craft, legDeploy, frozen ? Activation.DontActivate : Activation.Activate);
        PutDoors(craft, bayDoors, doorStops ?? craft.DoorStops, frozen ? Activation.DontActivate : Activation.Activate);
        if (frozen)
        {
            RestLegs(craft);
            craft.Submerged = Float(craft, push: false); // in the waves as they are now (WavesAt), so one frozen afloat wakes as the player comes
            if (RaftUnder(craft) is { } raft)
                Board(craft, raft, position, rotation); // the raft is as it was when it was saved (WavesAt), so it is carried on from there
        }
        craft.LegsLocked = LockedAsItStands(craft, frozen);
        return craft;
    }

    /// <summary>
    /// A craft as a vessel under formula keeps it (see <see cref="Vessel.OnFormula"/>): taken out
    /// already (<see cref="Craft.Removed"/>), so nothing of Jolt's is ever asked of it, but carrying
    /// what is read of a body (its stages, its size, its legs, bay doors and fairing). For one restored straight
    /// onto the formula as a world loads, which never has a body until it comes back
    /// (<see cref="Vessel.ReturnToPhysics"/>): making a thousand bodies, only to take them out again
    /// in the world's first step, would cost that frame far over its budget.
    /// </summary>
    public static Craft StandIn(Vehicle vehicle, int first, int last, float legDeploy, bool fairingOn, float bayDoors = 0f, Vector2? doorStops = null) =>
        new(vehicle, first, last, uint.MaxValue, null!, null)
        {
            Removed = true, LegDeploy = legDeploy, LegsDeployed = legDeploy >= 1f && vehicle.Stages[first].Legs,
            BayDoors = bayDoors, DoorsOpen = bayDoors >= 1f && vehicle.Stages[first].Bay, FairingOn = fairingOn,
            DoorStops = doorStops ?? new(RocketGeometry.DoorOpenRadians),
        };

    /// <summary>How far out each of <paramref name="legs"/> of one resting as <paramref name="model"/> places it is from where its foot stands (see <see cref="RestLegs"/>), and which feet are down (a bit each).</summary>
    internal static (Vector4 Extension, int Down) RestingLegs(GroundBody body, LegGeometry legs, Matrix4x4 model)
    {
        Span<float> extension = stackalloc float[RocketGeometry.LegCount];
        var down = 0;
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            var gap = body.GroundAltitude(Vector3.Transform(RocketGeometry.Foot(legs, leg), model));
            extension[leg] = Math.Clamp(gap / legs.Stroke, 0f, 1f);
            if (gap < legs.Stroke)
                down |= 1 << leg;
        }
        return (new Vector4(extension[0], extension[1], extension[2], extension[3]), down);
    }

    /// <summary>
    /// Whether a restored <paramref name="craft"/>'s legs were locked, from how it stands: they
    /// are with all four feet within their stroke. One that had landed (<paramref name="frozen"/>)
    /// on its legs had all four down to come to rest, even if the lean it settled at has since
    /// lifted one out of reach, so its legs are locked too; unlocked, it would fall over on waking.
    /// The lock isn't saved, so a save needs nothing more than the pose.
    /// </summary>
    private bool LockedAsItStands(Craft craft, bool frozen)
    {
        if (!craft.LegsDeployed)
            return false;
        if (frozen)
            return true;
        var model = Model(craft);
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
            if (_body.GroundAltitude(Vector3.Transform(RocketGeometry.Foot(craft.Legs, leg), model)) >= craft.Legs.Stroke)
                return false;
        return true;
    }

    /// <summary>
    /// Sets how far out each of a frozen <paramref name="craft"/>'s legs is from where its feet
    /// stand, as <see cref="Cushion"/> would, without the push. Cushion never runs for a frozen
    /// craft, so one restored frozen would otherwise keep its legs at full stroke, drawn a whole
    /// stroke (<see cref="LegGeometry.Stroke"/>) into the ground. It isn't saved: the pose it rests in is, and
    /// that gives the same extension.
    /// </summary>
    private void RestLegs(Craft craft)
    {
        if (!craft.LegsDeployed)
            return;
        var (extension, down) = RestingLegs(_body, craft.Legs, Model(craft));
        craft.LegExtension = extension;
        craft.FeetDown = down; // standing on them, as Cushion would have found it: woken, they are no new touch
    }

    /// <summary>
    /// Freezes <paramref name="craft"/>: stops it dead and takes it out of Jolt's simulation
    /// (<c>DeactivateBody</c>), and out of the gravity, drag and legs worked out here. It needs no
    /// ground under it from now on (nothing moves it), so its tiles are let go like any others.
    /// <see cref="Update"/> freezes a craft that has been all but still on the ground for a moment.
    /// One on a raft (the drone ship's deck) is kept where it is on the raft instead (<see cref="Craft.Aboard"/>),
    /// and carried with it each step (see <see cref="Carry"/>).
    /// </summary>
    public void Freeze(Craft craft)
    {
        if (craft.Removed || craft.Frozen)
            return;
        _bodies.SetLinearVelocity(craft.Id, new float3(0, 0, 0));
        _bodies.SetAngularVelocity(craft.Id, new float3(0, 0, 0));
        _bodies.DeactivateBody(craft.Id);
        craft.Frozen = true;
        craft.FrozenAt = Position(craft);
        craft.FrozenRotation = Rotation(craft);
        if (RaftUnder(craft) is { } raft)
            Board(craft, raft, craft.FrozenAt, Rotation(craft));
    }

    /// <summary>
    /// Wakes a frozen <paramref name="craft"/>: lays the ground under it first, then puts it back
    /// in the simulation (<c>ActivateBody</c>), so it never moves without the ground it stands on.
    /// </summary>
    public void Wake(Craft craft)
    {
        if (MarkAwake(craft))
            WakeMarked();
    }

    /// <summary>
    /// Takes <paramref name="craft"/> off the frozen list, to wake with the others marked in the
    /// same step (see <see cref="WakeMarked"/>). False if it wasn't frozen.
    /// </summary>
    private bool MarkAwake(Craft craft)
    {
        if (craft.Removed || !craft.Frozen)
            return false;
        craft.Frozen = false;
        craft.StillFor = 0f;
        _waking.Add((craft, craft.Aboard?.Raft));
        craft.Aboard = null;
        return true;
    }

    /// <summary>
    /// Wakes every craft marked: the tiles for all of them laid at once (one update however many
    /// there are, so a field of frozen boosters woken together costs one pass, not one each), then
    /// each put back in the simulation. One frozen on a raft was being carried with it, so it
    /// wakes moving as the raft does there, not still while the raft comes up into it.
    /// </summary>
    private void WakeMarked()
    {
        if (_waking.Count == 0)
            return;
        UpdateTiles();
        foreach (var (craft, aboard) in _waking)
        {
            if (craft.Removed)
                continue;
            if (aboard is { } raft)
            {
                _bodies.SetLinearVelocity(craft.Id, ToFloat3(raft.VelocityAt(CentreOfMass(craft)))); // Jolt's velocity is the centre of mass's
                _bodies.SetAngularVelocity(craft.Id, ToFloat3(raft.AngularVelocity));
            }
            _bodies.ActivateBody(craft.Id);
        }
        _waking.Clear();
    }

    /// <summary>Whether the ground's tile for <paramref name="cell"/> is in the world.</summary>
    internal bool HasTile(TerrainTiles.Cell cell) => _tiles.Has(cell);

    /// <summary>Whether body <paramref name="id"/> is in the world: false for an old ID of a slot made again since (its sequence number differs).</summary>
    internal bool IsInWorld(uint id) => _bodies.IsAdded(id);

    /// <summary>How many times the ground's tiles have been brought up to date (once a step, however many crafts woke in it, and once more for each craft woken between steps by its legs or a jettison).</summary>
    internal long TileUpdates => _tiles.Updates;

    /// <summary>Whether Jolt is simulating <paramref name="craft"/> (it isn't while it is frozen).</summary>
    public bool IsActive(Craft craft) => !craft.Removed && _bodies.IsActive(craft.Id);

    /// <summary>How fast a craft is turning, about which axis (radians per second).</summary>
    public Vector3 AngularVelocity(Craft craft)
    {
        var w = _bodies.GetAngularVelocity(craft.Id);
        return new Vector3(w.X, w.Y, w.Z);
    }

    /// <summary>
    /// Splits <paramref name="craft"/>'s bottom stage off: it becomes a craft of its own, pushed
    /// gently back, and the stages above fly on as another from where they were, pushed gently
    /// forward. <paramref name="craft"/> itself is gone afterwards. Returns null if it is a single stage.
    /// In flight the two part before they can collide (see <see cref="Craft.PartingFrom"/>); <paramref name="atRest"/>
    /// (landed or afloat) there is no parting them, as the push apart is soon undone by its weight, so
    /// the rest is set just clear of the dropped stage instead (its bell lifted out of the interstage),
    /// to stand on it, collide with it, or tip off it.
    /// </summary>
    public (Craft Dropped, Craft Above)? Separate(Craft craft, bool atRest = false)
    {
        if (craft.Removed || craft.Last <= craft.First)
            return null;

        var model = Model(craft);
        var rotation = Rotation(craft);
        var velocity = Velocity(craft);
        var w = _bodies.GetAngularVelocity(craft.Id);
        var spin = new Vector3(w.X, w.Y, w.Z);
        var up = Up(craft);
        var bottom = Vector3.Transform(Vector3.Zero, model);
        var nextBase = Vector3.Transform(new Vector3(0, RocketGeometry.StackStep(craft.Vehicle.Stages[craft.First]), 0), model);
        var (legDeploy, legsDeployed, bayDoors) = (craft.LegDeploy, craft.LegsDeployed, craft.BayDoors);
        // Frozen aboard a raft, its body is still while the raft carries it: the parts start moving
        // as the raft does where each is (as one woken there does: see WakeMarked), not still.
        var aboard = craft.Frozen ? craft.Aboard?.Raft : null;
        var centre = CentreOfMass(craft);

        Remove(craft);
        if (atRest)
            nextBase += up * (craft.Vehicle.Stages[craft.First + 1].BellHeight + 0.05f); // its bell out of the interstage
        // In flight the two part from each other before they can collide (the one gone stands in, for the moment).
        // The velocities given here only start them: each is set below, where it goes on from (the raft's, or the whole's).
        var dropped = AddCraft(craft.Vehicle, craft.First, craft.First, bottom, rotation,
            velocity - up * (Scene.SeparationSpeed / 2), spin, partingFrom: atRest ? null : craft);
        // Its legs and bay doors (the bottom stage's) go with it, as they were.
        dropped.LegDeploy = legDeploy;
        dropped.LegsDeployed = legsDeployed && dropped.Deployed is not null;
        (dropped.BayDoors, dropped.DoorStops) = (bayDoors, craft.DoorStops);
        dropped.DoorsOpen = bayDoors >= 1f && dropped.Vehicle.Stages[dropped.First].Bay;
        if (dropped.DoorsOpen)
            MakeOpenShapes(dropped); // a new craft: none to replace
        if (dropped.LegsDeployed || dropped.DoorsOpen)
            _bodies.SetShape(dropped.Id, dropped.Collider, updateMassProperties: false, Activation.Activate);
        var rest = AddCraft(craft.Vehicle, craft.First + 1, craft.Last, nextBase, rotation,
            velocity + up * (Scene.SeparationSpeed / 2), spin,
            fairing: craft.FairingOn, partingFrom: atRest ? null : dropped); // its fairing jettisoned already, or still on
        if (!atRest)
            dropped.PartingFrom = rest;
        if (aboard is { } raft)
            foreach (var (part, push) in (ReadOnlySpan<(Craft, float)>)[(dropped, -0.5f), (rest, 0.5f)])
            {
                // Jolt's velocity is the centre of mass's, so the raft's is taken there (each body as built: the dropped one's feet included).
                _bodies.SetLinearVelocity(part.Id, ToFloat3(raft.VelocityAt(CentreOfMass(part)) + up * (Scene.SeparationSpeed * push)));
                _bodies.SetAngularVelocity(part.Id, ToFloat3(raft.AngularVelocity));
            }
        else
            foreach (var (part, push) in (ReadOnlySpan<(Craft, float)>)[(dropped, -0.5f), (rest, 0.5f)])
                GoOnAsPartOf(part, centre, velocity, spin, up * (Scene.SeparationSpeed * push));
        // Anything still parting from what came apart goes on parting from the part it came off:
        // a stage dropped earlier from the bottom one (below it), a fairing half from the top (round it).
        foreach (var other in _crafts)
            if (other.PartingFrom == craft)
                other.PartingFrom = other.Vehicle == craft.Vehicle && other.Last < craft.First ? dropped : rest;
        return (dropped, rest);
    }

    /// <summary>
    /// Sets <paramref name="part"/>, just come off a whole whose centre of mass was at
    /// <paramref name="centre"/> moving at <paramref name="velocity"/> and which was turning at
    /// <paramref name="spin"/>, going on as it moved as part of that whole, with
    /// <paramref name="push"/> added: Jolt's velocity is the centre of mass's, and the whole's
    /// turning moved the part's centre of mass besides (a vessel holding its attitude among the
    /// stars turns in the ground's frame with the body's turn). Shared out so, the turning's
    /// shares cancel over the parts, so the whole's momentum is kept.
    /// </summary>
    public void GoOnAsPartOf(Craft part, Vector3 centre, Vector3 velocity, Vector3 spin, Vector3 push) =>
        _bodies.SetLinearVelocity(part.Id, ToFloat3(velocity + Vector3.Cross(spin, CentreOfMass(part) - centre) + push));

    /// <summary>
    /// Takes <paramref name="craft"/>'s fairing off its collider (the halves are crafts of their own,
    /// see <see cref="AddLoose"/>). Its mass and how it turns are kept as they were, so it carries on
    /// smoothly; its fairing's mass less (the geometry's `Mass`) is the flight's to count (thrust is an acceleration).
    /// </summary>
    public void JettisonFairing(Craft craft)
    {
        if (craft.Removed || !craft.FairingOn)
            return;
        Wake(craft);
        craft.FairingOn = false;
        var (oldStowed, oldDeployed, oldOpen, oldOpenDeployed) = (craft.Stowed, craft.Deployed, craft.Open, craft.OpenDeployed);
        craft.Stowed = CreateShape(craft.Vehicle, craft.First, craft.Last, withFeet: false, withFairing: false);
        craft.Deployed = oldDeployed is null ? null : CreateShape(craft.Vehicle, craft.First, craft.Last, withFeet: true, withFairing: false);
        (craft.Open, craft.OpenDeployed) = (null, null);
        if (oldOpen is not null)
            MakeOpenShapes(craft); // (none to replace: the old ones are freed below)
        _bodies.SetShape(craft.Id, craft.Collider, updateMassProperties: false, Activation.Activate);
        oldStowed.Dispose();
        oldDeployed?.Dispose();
        oldOpen?.Dispose();
        oldOpenDeployed?.Dispose();
    }

    /// <summary>
    /// Makes <paramref name="craft"/>'s colliders with its bay's doors open (<see cref="Craft.Open"/>,
    /// <see cref="Craft.OpenDeployed"/>), each door as far out as its stop (<see cref="Craft.DoorStops"/>),
    /// its fairing on or off as it is (its bottom stage has a bay). Its body is left as it is:
    /// whoever asks sets its collider, then frees the ones these replace (returned), once the body
    /// is off them. Two compound shapes, made the first time a craft's doors open (see
    /// <see cref="PutDoors"/>), never every frame.
    /// </summary>
    private static (Shape? Open, Shape? OpenDeployed) MakeOpenShapes(Craft craft)
    {
        var replaced = (craft.Open, craft.OpenDeployed);
        craft.OpenFor = craft.DoorStops;
        craft.Open = CreateShape(craft.Vehicle, craft.First, craft.Last, withFeet: false, craft.FairingOn, craft.OpenFor);
        craft.OpenDeployed = craft.Vehicle.Stages[craft.First].Legs
            ? CreateShape(craft.Vehicle, craft.First, craft.Last, withFeet: true, craft.FairingOn, craft.OpenFor) : null;
        return replaced;
    }

    /// <summary>
    /// Swaps in the collider with or without the feet, for when the legs have swung out or in.
    /// Legs moving wake a frozen craft.
    /// </summary>
    public void SetLegs(Craft craft, float deploy)
    {
        if (craft.Removed)
            return;
        if (deploy != craft.LegDeploy)
            Wake(craft);
        PutLegs(craft, deploy, Activation.Activate);
    }

    private void PutLegs(Craft craft, float deploy, Activation activation)
    {
        craft.LegDeploy = deploy;
        var deployed = deploy >= 1f;
        if (deployed == craft.LegsDeployed || craft.Deployed is null)
            return;
        craft.LegsDeployed = deployed;
        // Keep the mass and inertia as they were: the feet barely change them, and the rocket
        // shouldn't handle differently because its legs are out.
        _bodies.SetShape(craft.Id, craft.Collider, updateMassProperties: false, activation);
    }

    /// <summary>
    /// Sets how far open <paramref name="craft"/>'s bay doors are (0 shut to 1 open), and where each
    /// stops on the ground (<paramref name="stops"/>, see <see cref="Craft.DoorStops"/>; by default
    /// where they were), and swaps in the collider with its ramps once they are fully open
    /// (<see cref="Craft.DoorsOpen"/>), and back the moment they start to close, as the legs' feet
    /// are. Unlike the legs, the doors moving leave a frozen craft frozen: they stop where they
    /// meet the ground (see <see cref="Vessel.MoveDoors"/>), so nothing it stands on changes, and
    /// waking it on a slope only lets it creep.
    /// </summary>
    /// <remarks>
    /// Without <paramref name="ramps"/>, the ramps are left out of the collider however open the
    /// doors are: while it comes down onto the ground (see <see cref="Vessel.MoveDoors"/>), so it
    /// lands on its legs, not propped on a ramp at a swing the ground under it was never measured for.
    /// </remarks>
    public void SetDoors(Craft craft, float open, Vector2? stops = null, bool ramps = true)
    {
        if (craft.Removed)
            return;
        PutDoors(craft, open, stops ?? craft.DoorStops, craft.Frozen ? Activation.DontActivate : Activation.Activate, ramps);
    }

    private void PutDoors(Craft craft, float open, Vector2 stops, Activation activation, bool ramps = true)
    {
        craft.BayDoors = open;
        craft.DoorStops = stops;
        if (!craft.Vehicle.Stages[craft.First].Bay)
            return;
        var doorsOpen = open >= 1f && ramps;
        // The open shapes are made the first time the doors open, and again whenever they stop elsewhere.
        var remade = doorsOpen && (craft.Open is null || craft.OpenFor != stops);
        if (doorsOpen == craft.DoorsOpen && !remade)
            return;
        var (oldOpen, oldBoth) = remade ? MakeOpenShapes(craft) : default;
        craft.DoorsOpen = doorsOpen;
        // The mass and inertia kept as they were, as for the legs: the ramps are counted in the
        // shape's volume, so its centre of mass stays put.
        _bodies.SetShape(craft.Id, craft.Collider, updateMassProperties: false, activation);
        oldOpen?.Dispose(); // only once the body is off them
        oldBoth?.Dispose();
    }

    /// <summary>
    /// Advances the simulation by however many fixed steps fit in <paramref name="dt"/>: every craft
    /// falls, and those in <paramref name="controls"/> are pushed and steered as asked. Before each
    /// step, frozen crafts given thrust or come near are woken, and the ground's tiles are laid
    /// round the awake ones (made in the step only where a craft could reach them within
    /// <see cref="Scene.TerrainTileUrgentSeconds"/>; the rest by the tiles' worker, ahead of need); after it, those come to rest are frozen. <paramref name="player"/> is
    /// where the vessel being flown is, which crafts afloat freeze and wake by (without it, all of
    /// them are far off); <paramref name="worldTime"/> is the world's time at the end of
    /// <paramref name="dt"/>, which the waves and the rafts run by, as they are drawn (without it, the time simulated here).
    /// </summary>
    public void Update(float dt, ReadOnlySpan<RocketControl> controls, Vector3? player = null, double? worldTime = null)
    {
        _player = player;
        foreach (var craft in _crafts)
            craft.TouchesBegun.Clear(); // this update's touches only
        _accumulator = MathF.Min(_accumulator + dt, MaxStepsPerFrame * StepLength);
        // The laps are all on this thread, round Jolt's step and never inside its callbacks:
        // Timings' statics aren't thread-safe. Each charges the code just above it.
        Timings.Lap(StepPart.Rest);
        var stepped = false;
        for (; _accumulator >= StepLength; _accumulator -= StepLength)
        {
            var time = worldTime is { } end ? end - _accumulator : _time;
            foreach (var craft in _crafts)
                Resync(craft, stepRead: true); // anything that moved it since the last step, its copy follows; Fall reuses this read, so nothing but the thrust may change a craft's velocity between here and Fall
            Timings.Lap(StepPart.Rest);
            if (_body.HasSea)
            {
                Sea.Phases(time, _phases); // once a step, for every craft
                Rafts.Time = time;
                MoveRafts(time);           // and the rafts riding them, before anything reads it
            }
            Timings.Lap(StepPart.Water);
            var started = Timings.Start();
            FindMovers();
            Timings.Stop(StepDetail.Movers, started);
            foreach (var control in controls)
                if (control.Craft.World == this && control.ThrustAcceleration > 0 && !MarkAwake(control.Craft))
                    control.Craft.StillFor = 0f; // under thrust it isn't at rest, however still it stands
            started = Timings.Start();
            MarkNear();
            MarkNearThePlayer();
            Timings.Stop(StepDetail.Near, started);
            Timings.Lap(StepPart.Contact);
            if (_waking.Count > 0)
                WakeMarked(); // the tiles laid with them
            else
                UpdateTiles();
            Timings.Lap(StepPart.Tiles);
            UpdateFloes(time); // the floes near the awake crafts, those just woken among them
            Timings.Lap(StepPart.Water);

            foreach (var control in controls)
            {
                if (control.Craft.World != this || control.Craft.Removed || control.Craft.Frozen)
                    continue; // another body's, or gone, or held still
                var up = Up(control.Craft);
                if (control.ThrustAcceleration > 0)
                {
                    var push = up * (control.ThrustAcceleration * StepLength);
                    _bodies.AddLinearVelocity(control.Craft.Id, ToFloat3(push));
                    if (control.Craft.StepRead)
                        control.Craft.ReadVelocity += push; // as Jolt adds it (in floats), so the fall's read is Jolt's (Jolt never clamps it: see JoltTopSpeed)
                    if (control.Craft.Kept)
                        control.Craft.KeptVelocity += (Vector3Double)up * (control.ThrustAcceleration * (double)StepLength);
                }
                if (control.Steer)
                    Steer(control.Craft, up, control.DesiredUp);
            }
            Timings.Lap(StepPart.Rest);
            started = Timings.Start();
            FindGrounded(); // before any falls: see Weight
            Timings.Stop(StepDetail.Grounded, started);
            Timings.Lap(StepPart.Contact);
            foreach (var craft in _crafts)
            {
                if (craft.Frozen)
                    continue;
                Fall(craft);
                Timings.Lap(StepPart.Rest); // gravity apart from the water
                craft.Submerged = Float(craft, push: true);
                Timings.Lap(StepPart.Water);
                Cushion(craft);
                Timings.Lap(StepPart.Water);
            }
            foreach (var craft in _crafts)
                HandOver(craft);
            Timings.Lap(StepPart.Rest);
            started = Timings.Start();
            KeepStepMotion();
            Timings.Stop(StepDetail.Touches, started);
            Timings.Lap(StepPart.Contact);

            _system.Update(StepLength, 1, _tempAllocator, _jobSystem);
            Timings.Lap(StepPart.Jolt);
            Timings.CountPhysicsStep();
            _time += StepLength;
            Steps++;
            started = Timings.Start();
            ReadContacts(); // first, before anything is taken out of the world or moved on
            Timings.Stop(StepDetail.Touches, started);
            started = Timings.Start();
            ClearWrecks(); // those whose hits have played out
            Timings.Stop(StepDetail.Wrecks, started);
            Timings.Lap(StepPart.Contact);
            RaftsMoved(); // the step has moved them there
            Timings.Lap(StepPart.Water);
            started = Timings.Start();
            EndParting();
            Timings.Stop(StepDetail.Wrecks, started);
            Timings.Lap(StepPart.Contact);
            Carry();
            Timings.Lap(StepPart.Water);
            started = Timings.Start();
            FindMovers();
            Timings.Stop(StepDetail.Movers, started);
            started = Timings.Start();
            FreezeResting();
            Timings.Stop(StepDetail.Freezing, started);
            Timings.Lap(StepPart.Contact);
            stepped = true;
        }
        if (stepped)
            foreach (var craft in _crafts)
                (craft.GroundAhead, craft.GroundAheadFrom) = (null, double.NaN); // the look ahead sets it again
    }

    /// <summary>
    /// What <paramref name="craft"/> touched over the last <see cref="Update"/>'s steps, oldest first:
    /// each contact Jolt began in them with something solid, if the two were closing (see
    /// <see cref="Touch"/>), and each of its feet its legs set on the ground or a raft (see
    /// <see cref="FeetTouch"/>: the legs hold it up themselves, so Jolt never sees them land).
    /// Emptied at the start of each update; empty for a craft taken out.
    /// </summary>
    public IReadOnlyList<Touch> TouchesBegun(Craft craft) => craft.TouchesBegun;

    /// <summary>Whether <paramref name="craft"/> has a live contact with something of kind <paramref name="with"/>.</summary>
    public bool Touching(Craft craft, SolidKind with)
    {
        var touches = craft.Touches;
        for (var part = CraftPart.Foot; part <= CraftPart.Hull; part++)
            if (touches[Craft.TouchIndex(with, part)] > 0)
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="craft"/> has a live contact with something of kind <paramref name="with"/> at its <paramref name="part"/>.</summary>
    public bool Touching(Craft craft, SolidKind with, CraftPart part) => craft.Touches[Craft.TouchIndex(with, part)] > 0;

    /// <summary>Whether <paramref name="craft"/> is touching anything at all: a live contact with anything, or its feet down by its legs (see <see cref="Craft.FeetDown"/>).</summary>
    public bool TouchingAnything(Craft craft)
    {
        if (craft.FeetDown > 0)
            return true;
        foreach (var touches in craft.Touches)
            if (touches > 0)
                return true;
        return false;
    }

    /// <summary>How many contacts are live, for tests: none should outlive a body taken out.</summary>
    internal int LiveContacts => _live.Count;

    /// <summary>How many contact events Jolt's queue has had to drop, for tests: none should ever be (see <see cref="ReadContacts"/>).</summary>
    internal uint DroppedContacts => _listener.Dropped;

    /// <summary>
    /// A live contact, for each of its two bodies that is a craft: which, what it is touching
    /// there and at which of its parts (its ending carries only the IDs, so this is kept from its beginning).
    /// </summary>
    private readonly record struct LiveContact(Craft? Craft1, SolidKind With1, CraftPart Part1, Craft? Craft2, SolidKind With2, CraftPart Part2)
    {
        public void Count(int by)
        {
            if (Craft1 is { } a)
                a.Touches[Craft.TouchIndex(With1, Part1)] += by;
            if (Craft2 is { } b)
                b.Touches[Craft.TouchIndex(With2, Part2)] += by;
        }
    }

    /// <summary>
    /// Keeps each craft's motion as it is just before the step (see <see cref="Craft.StepVelocity"/>):
    /// what its contacts beginning in the step touched at, as after it Jolt has already pushed them apart.
    /// </summary>
    private void KeepStepMotion()
    {
        foreach (var craft in _crafts)
        {
            if (craft.Frozen)
            {
                (craft.StepVelocity, craft.StepSpin) = (Vector3.Zero, Vector3.Zero); // its centre is read as it is (see PartOf)
                continue;
            }
            craft.StepVelocity = Velocity(craft);
            craft.StepSpin = AngularVelocity(craft);
            craft.StepCentre = CentreOfMass(craft);
            craft.StepRotation = Rotation(craft);
        }
    }

    /// <summary>
    /// Reads the contacts Jolt began and ended in the step just taken (all of them: what doesn't
    /// fit the array waits for the next go round), into each craft's touches and the live contacts.
    /// </summary>
    private void ReadContacts()
    {
        uint remaining;
        do
        {
            var count = _listener.Drain(_events, out remaining);
            for (var i = 0; i < count; i++)
                ReadContact(in _events[i]);
        }
        while (remaining > 0);
        // A lost beginning is a touch missed, and a lost end a contact live for ever: the queue
        // has room for more than the busiest step seen (see where it is made), so this is a bug.
        Debug.Assert(_listener.Dropped == 0, "Jolt's contact queue dropped events");
    }

    private void ReadContact(in ContactEvent e)
    {
        var key = (e.Body1, e.Body2, e.SubShape1, e.SubShape2);
        if (e.Kind == ContactEventKind.Removed)
        {
            if (_live.Remove(key, out var ended))
                ended.Count(-1);
            return;
        }
        if (e.Kind != ContactEventKind.Added)
            return;
        var craft1 = (SolidKind)e.UserData1 == SolidKind.Craft ? _craftBodies.GetValueOrDefault(e.Body1) : null;
        var craft2 = (SolidKind)e.UserData2 == SolidKind.Craft ? _craftBodies.GetValueOrDefault(e.Body2) : null;
        if (craft1 is null && craft2 is null)
            return; // nothing of ours (no two pieces of scenery collide)
        var point = new Vector3(e.Point.X, e.Point.Y, e.Point.Z);
        var normal = new Vector3(e.Normal.X, e.Normal.Y, e.Normal.Z); // from body 1 towards body 2
        var (kind1, raft1) = KindOf(e.Body1, (SolidKind)e.UserData1, point);
        var (kind2, raft2) = KindOf(e.Body2, (SolidKind)e.UserData2, point);
        var part1 = craft1 is null ? default : PartOf(craft1, point);
        var part2 = craft2 is null ? default : PartOf(craft2, point);
        // Each judged before the contact is counted live, so its own touch isn't one it had already.
        if (craft1 is not null)
            BeginTouch(craft1, part1, kind2, craft2, raft2, point, -normal);
        if (craft2 is not null)
            BeginTouch(craft2, part2, kind1, craft1, raft1, point, normal);
        var contact = new LiveContact(craft1, kind2, part1, craft2, kind1, part2);
        if (_live.TryAdd(key, contact))
            contact.Count(1);
    }

    /// <summary>
    /// What body <paramref name="body"/>, of kind <paramref name="kind"/>, is where it was touched at
    /// <paramref name="point"/>, and the raft if it is one. A drone ship's masts are in its hull's
    /// body (so they ride the waves with it), so a touch on it more than a little above its deck,
    /// in its own frame, is on a mast.
    /// </summary>
    private (SolidKind Kind, Raft? Raft) KindOf(uint body, SolidKind kind, Vector3 point)
    {
        if (kind != SolidKind.Raft || !_raftBodies.TryGetValue(body, out var raft))
            return (kind, null);
        if (raft.IsShip)
        {
            var deck = At(raft); // as it was for the step (it is moved on after: see RaftsMoved)
            var y = Vector3.Transform(point - deck.Position, Quaternion.Inverse(deck.Rotation)).Y;
            if (y > raft.Freeboard + DeckTouch)
                return (SolidKind.Mast, raft);
        }
        return (SolidKind.Raft, raft);
    }

    /// <summary>How far above a drone ship's deck a touch is on a mast rather than the deck (metres): over the 2 cm Jolt begins a contact at.</summary>
    private const float DeckTouch = 0.05f;

    /// <summary>
    /// Which of <paramref name="craft"/>'s parts <paramref name="point"/> is on, in its own space
    /// (its origin at the bottom of its body), as it stood for the step: its feet (with its legs
    /// out, further from its axis than its body, and no higher than their pads), its bell (below
    /// its body, and within the bell's reach of its axis), or the rest of it (a booster lying on
    /// its side touches with the rim of its body's bottom, not its bell).
    /// </summary>
    private CraftPart PartOf(Craft craft, Vector3 point)
    {
        // Measured from its centre of mass as it was for the step (which is fixed in it), and
        // turned as it was then, so a craft that covered metres in the step, or turned, is measured
        // where the contact began, not where it ended.
        var rotation = Rotation(craft);
        var centre = CentreOfMass(craft);
        var ownCentre = Vector3.Transform(centre - Position(craft), Quaternion.Inverse(rotation));
        var (stepCentre, stepRotation) = craft.Frozen ? (centre, rotation) : (craft.StepCentre, craft.StepRotation);
        var local = Vector3.Transform(point - stepCentre, Quaternion.Inverse(stepRotation)) + ownCentre;
        var across = local.X * local.X + local.Z * local.Z;
        var bottom = craft.Vehicle.Stages[craft.First];
        // Its feet are the only part of it further out than its body (but its bay's ramps, open),
        // but Jolt's point can be a little off the surface, so a touch on its body's side high
        // above them is still its body.
        if (craft.LegsDeployed && across > Square(bottom.Radius) && local.Y < RocketGeometry.Foot(craft.Legs, 0).Y + RocketGeometry.FootThickness + FootReach
            && (!craft.DoorsOpen || NearAFoot(craft.Legs, local)))
            return CraftPart.Foot;
        return local.Y < BellTop && across <= Square(bottom.BellRadius + BellReach) ? CraftPart.Bell : CraftPart.Hull;
    }

    /// <summary>Whether <paramref name="local"/> (in a craft's own space) is over one of its feet's pads, as near as a touch on it can be: its ramps (a hull's) reach out as far.</summary>
    private static bool NearAFoot(LegGeometry legs, Vector3 local)
    {
        var reach = legs.FootHalfWidth + FootReach;
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            var foot = RocketGeometry.Foot(legs, leg);
            if (MathF.Abs(local.X - foot.X) < reach && MathF.Abs(local.Z - foot.Z) < reach)
                return true;
        }
        return false;
    }

    /// <summary>How far above its feet's pads a touch out past its body is still on its feet (metres): over the pads' rounding and Jolt's point being off the surface.</summary>
    private const float FootReach = 0.1f;

    /// <summary>How high in a craft's own space a touch is still on its bell (metres): its body starts at 0.</summary>
    private const float BellTop = 0.05f;

    /// <summary>How far past its bell's rim, from its axis, a touch is still on the bell (metres): Jolt's point can be a little off the surface, and the body is wider by more.</summary>
    private const float BellReach = 0.1f;

    private static float Square(float x) => x * x;

    /// <summary>
    /// <paramref name="craft"/> touching <paramref name="with"/> (the craft <paramref name="other"/>
    /// or the raft <paramref name="raft"/>, if it is one) at its <paramref name="part"/>, at
    /// <paramref name="point"/>, <paramref name="normal"/> pointing out of what it touched into it:
    /// a touch, if they were closing as the step began.
    /// </summary>
    private static void BeginTouch(Craft craft, CraftPart part, SolidKind with, Craft? other, Raft? raft, Vector3 point, Vector3 normal)
    {
        // The other's velocity there: a craft's as it was (a frozen one's still), a raft's (the
        // ship's masts moving with it) as the waves had it for the step; the rest never move.
        var theirs = other is not null ? StepVelocityAt(other, point) : raft?.VelocityAt(point) ?? Vector3.Zero;
        var closing = StepVelocityAt(craft, point) - theirs;
        if (Vector3.Dot(closing, normal) >= 0f)
            return; // parting, or sliding past: Jolt begins a contact a little before they touch
        craft.TouchesBegun.Add(new Touch(with, other, raft, part, point, normal, closing, IsNewTouch(craft, with, part)));
    }

    /// <summary>
    /// Whether <paramref name="craft"/> touching <paramref name="with"/> at its <paramref name="part"/>
    /// is a new touch (<see cref="Touch.Fresh"/>): on the ground or a raft, only where it had no
    /// live contact with either. Its feet and its bell are one place to touch down (its lowest
    /// points together, as a landing has always been judged), so on its feet its bell coming
    /// down is no new touch, nor the other way round; its body is a place of its own. Its feet
    /// touch by its legs (<see cref="Craft.FeetDown"/>) as well as by Jolt's contacts.
    /// </summary>
    internal static bool IsNewTouch(Craft craft, SolidKind with, CraftPart part)
    {
        if (with is not (SolidKind.Ground or SolidKind.Raft))
            return true;
        return part == CraftPart.Hull
            ? Untouched(craft, CraftPart.Hull)
            : craft.FeetDown == 0 && Untouched(craft, CraftPart.Foot) && Untouched(craft, CraftPart.Bell); // its legs' own touch too (see FeetTouch)
    }

    /// <summary>Whether <paramref name="craft"/> has no live contact with the ground or a raft at its <paramref name="part"/>.</summary>
    private static bool Untouched(Craft craft, CraftPart part) =>
        craft.Touches[Craft.TouchIndex(SolidKind.Ground, part)] + craft.Touches[Craft.TouchIndex(SolidKind.Raft, part)] == 0;

    /// <summary>How <paramref name="craft"/> was moving at <paramref name="point"/> as the step began (see <see cref="Craft.StepVelocity"/>).</summary>
    private static Vector3 StepVelocityAt(Craft craft, Vector3 point) =>
        craft.StepVelocity + Vector3.Cross(craft.StepSpin, point - craft.StepCentre);

    /// <summary>
    /// Forgets every live contact with body <paramref name="body"/> in it, as the game takes it out
    /// of the world (a craft, a tile or a floe's hull), so nothing read between steps finds it
    /// still touching: Jolt reports a contact's end only in its next step, and none when a
    /// sleeping body goes. (A craft frozen has its contacts ended by Jolt in the step after.)
    /// </summary>
    private void ForgetContacts(uint body)
    {
        _forgotten.Clear();
        foreach (var (key, _) in _live)
            if (key.Body1 == body || key.Body2 == body)
                _forgotten.Add(key);
        foreach (var key in _forgotten)
            if (_live.Remove(key, out var gone))
                gone.Count(-1);
    }

    /// <summary>
    /// The ground's tiles, for every awake craft as it is now. Jolt mustn't wake a frozen craft by
    /// itself either (it would drop through where its tile was): should taking a tile out ever
    /// do so, it is stopped again at once.
    /// </summary>
    private void UpdateTiles()
    {
        var removals = _tiles.Removals;
        _tiles.Update(_crafts, _motion, _stepScale); // urgency in steps: see TerrainTiles.Update
        if (_tiles.Removals == removals)
            return;
        foreach (var craft in _crafts)
            if (craft.Frozen && _bodies.IsActive(craft.Id))
            {
                _bodies.SetLinearVelocity(craft.Id, new float3(0, 0, 0));
                _bodies.SetAngularVelocity(craft.Id, new float3(0, 0, 0));
                _bodies.DeactivateBody(craft.Id);
            }
    }

    /// <summary>
    /// Which awake crafts are on the move, faster than <see cref="Scene.WakeSpeed"/>, and where:
    /// <see cref="_movers"/>, for waking and freezing. One at rest (a rocket waiting on the pad, say)
    /// disturbs nothing, and nor does one just settling on its legs, or a field of boosters woken
    /// together would keep each other awake, and wake the next ones along. Its speed is against the
    /// ground under it (<see cref="GroundBody.GroundVelocity"/>), so one standing on a heaving raft
    /// isn't on the move only because the raft is.
    /// </summary>
    private void FindMovers()
    {
        _movers.Clear();
        foreach (var craft in _crafts)
        {
            if (craft.Frozen)
                continue;
            var position = Position(craft);
            var moving = Velocity(craft) - _body.GroundVelocity(position);
            if (moving.LengthSquared() >= Scene.WakeSpeed * Scene.WakeSpeed)
            {
                var ahead = moving * Scene.WakeSeconds;
                _movers.Add((craft, position, ahead, ahead.LengthSquared(), Outline(craft), Bound(craft)));
            }
        }
    }

    /// <summary>Marks every frozen craft an awake one on the move could reach soon (see <see cref="NearAMover"/>), to wake.</summary>
    private void MarkNear()
    {
        foreach (var craft in _crafts)
        {
            craft.WakingSoon = false;
            if (!craft.Frozen || _movers.Count == 0)
                continue;
            if (NearAMover(craft, craft.FrozenAt))
                MarkAwake(craft);
            else
                // Within twice the reach (the way ahead twice as long, and twice as wide), it will
                // wake soon: its ground is made ahead of need, so a field of them woken together
                // lays shapes made already rather than making them all in that step (a dive at
                // the ground's top speed into 49 boosters frozen on the zone made 8 to 11 a step, 10 ms).
                craft.WakingSoon = NearAMover(craft, craft.FrozenAt, reach: 2f);
        }
    }

    /// <summary>Marks every frozen craft afloat that the vessel being flown is within <see cref="Scene.FloatWakeDistance"/> of, to wake.</summary>
    private void MarkNearThePlayer()
    {
        foreach (var craft in _crafts)
            if (craft.Frozen && craft.Submerged > 0f && NearThePlayer(craft.FrozenAt, Scene.FloatWakeDistance))
                MarkAwake(craft);
    }

    /// <summary>Whether the vessel being flown is within <paramref name="distance"/> of <paramref name="p"/>.</summary>
    private bool NearThePlayer(Vector3 p, float distance) =>
        _player is { } player && Vector3.DistanceSquared(player, p) < distance * distance;

    /// <summary>
    /// Whether a craft on the move other than <paramref name="craft"/> (standing at
    /// <paramref name="p"/>) could touch it soon: whether its body, swept along its way ahead over
    /// the next <see cref="Scene.WakeSeconds"/>, comes within <see cref="Scene.WakeMargin"/> of
    /// <paramref name="craft"/>'s, the gap between their shapes (see <see cref="SweptWithin"/>),
    /// whatever their sizes, so a fast one wakes what it is coming at in time (its tiles
    /// laid, its contacts live) to hit it, whatever its speed, and nothing it only passes: not the
    /// rest of a field it dives into, nor those beside a landing. <paramref name="reach"/> scales
    /// the way ahead and the margin alike (see <see cref="Craft.WakingSoon"/>).
    /// </summary>
    private bool NearAMover(Craft craft, Vector3 p, float reach = 1f)
    {
        var margin = reach * Scene.WakeMargin;
        var bound = Bound(craft);
        Capsule? outline = null;
        foreach (var (mover, at, ahead, aheadSquared, sweep, moverBound) in _movers)
        {
            if (mover == craft)
                continue;
            // Nothing of either reaches further than its bound from where it is: most are passed over here.
            var within = moverBound + bound + margin;
            if (FromWaySquared(p, at, ahead * reach, aheadSquared * reach * reach) >= within * within)
                continue;
            outline ??= Outline(craft);
            var o = outline.Value;
            if (SweptWithin(o, sweep, ahead * reach, margin))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="mover"/>, swept along <paramref name="way"/>, comes within
    /// <paramref name="margin"/> of <paramref name="still"/>, each as it is shaped: its body an axis
    /// as wide as its widest stage, and its feet, out, each a ball where it is (a foot stands out
    /// from its body only near its bottom, so taking it as reaching round the whole of it would
    /// wake those beside a row passed over).
    /// </summary>
    private static bool SweptWithin(in Capsule still, in Capsule mover, Vector3 way, float margin)
    {
        var axes = SweptApart(still.Bottom, still.Top, mover.Bottom, mover.Top, way);
        if (axes - still.Radius - mover.Radius < margin)
            return true;
        if (axes - still.Reach - mover.Reach >= margin)
            return false; // nothing of either, feet and all, is further from its axis than its reach
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            if (still.HasFeet && SweptApart(still.Foot(leg), still.Foot(leg), mover.Bottom, mover.Top, way) - still.FootRadius - mover.Radius < margin)
                return true;
            if (!mover.HasFeet)
                continue;
            // The mover's foot swept is the segment it runs along.
            var (from, to) = (mover.Foot(leg), mover.Foot(leg) + way);
            if (SegmentDistance(from, to, still.Bottom, still.Top) - mover.FootRadius - still.Radius < margin)
                return true;
            if (still.HasFeet)
                for (var other = 0; other < RocketGeometry.LegCount; other++)
                    if (SegmentDistance(from, to, still.Foot(other), still.Foot(other)) - mover.FootRadius - still.FootRadius < margin)
                        return true;
        }
        return false;
    }

    /// <summary>How far from where <paramref name="craft"/> is (the bottom of its body) anything of it reaches.</summary>
    private static float Bound(Craft craft) => craft.Length + MathF.Max(craft.Widest, FeetOut(craft.Legs));

    /// <summary>How far out from its axis <paramref name="legs"/>' feet reach, their pads' width included.</summary>
    private static float FeetOut(LegGeometry legs) => new Vector2(RocketGeometry.Foot(legs, 0).X, RocketGeometry.Foot(legs, 0).Z).Length() + legs.FootHalfWidth;

    /// <summary>
    /// How near the segment <paramref name="q0"/> to <paramref name="q1"/> comes to the segment
    /// <paramref name="b"/> to <paramref name="t"/> swept along <paramref name="ahead"/> (the
    /// parallelogram it covers on its way).
    /// </summary>
    private static float SweptApart(Vector3 q0, Vector3 q1, Vector3 b, Vector3 t, Vector3 ahead)
    {
        var nearest = MathF.Min(
            MathF.Min(SegmentDistance(q0, q1, b, t), SegmentDistance(q0, q1, b + ahead, t + ahead)),
            MathF.Min(SegmentDistance(q0, q1, b, b + ahead), SegmentDistance(q0, q1, t, t + ahead)));
        // Within the parallelogram's face: an end over it, or the segment through it.
        var u = t - b;
        var normal = Vector3.Cross(u, ahead);
        var area = normal.Length();
        if (area < 1e-6f)
            return nearest; // a line (along its own axis, or still): its edges are all of it
        normal /= area;
        var (d0, d1) = (Vector3.Dot(q0 - b, normal), Vector3.Dot(q1 - b, normal));
        if (Over(q0 - d0 * normal))
            nearest = MathF.Min(nearest, MathF.Abs(d0));
        if (Over(q1 - d1 * normal))
            nearest = MathF.Min(nearest, MathF.Abs(d1));
        if (d0 * d1 < 0f && Over(q0 + (q1 - q0) * (d0 / (d0 - d1))))
            return 0f;
        return nearest;

        // Whether a point in the face's plane is within it: b + s u + w ahead, s and w from 0 to 1.
        bool Over(Vector3 x)
        {
            var r = x - b;
            var (uu, ua, aa) = (Vector3.Dot(u, u), Vector3.Dot(u, ahead), Vector3.Dot(ahead, ahead));
            var (ru, ra) = (Vector3.Dot(r, u), Vector3.Dot(r, ahead));
            var det = uu * aa - ua * ua;
            var s = (ru * aa - ra * ua) / det;
            var w = (ra * uu - ru * ua) / det;
            return s is >= 0f and <= 1f && w is >= 0f and <= 1f;
        }
    }

    /// <summary>
    /// Freezes each craft that has moved less than <see cref="Scene.FreezeSpeed"/> and turned less
    /// than <see cref="Scene.FreezeTurnRate"/> over the last <see cref="Scene.FreezeSeconds"/>, on
    /// the ground. That is measured from where it stood when that stretch began, not from its
    /// speed at each step: a booster whose feet can't all touch uneven ground rocks a little on
    /// them for a long while, going nowhere, and its moments of speed shouldn't keep it awake.
    /// The clock doesn't run either while something on the move could soon touch it (see
    /// <see cref="NearAMover"/>: that would only wake it again): it freezes that long after the
    /// last has gone by, so one isn't frozen and woken over and over as another flies about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A craft afloat is never still, as the waves keep it moving, so it goes by distance instead:
    /// it never freezes while the vessel being flown is within <see cref="Scene.FloatFreezeDistance"/>,
    /// and further off it freezes where it is once it has been afloat that far off for as long,
    /// however it moves (see <see cref="MarkNearThePlayer"/> for its waking, nearer: the gap between
    /// the two keeps one held near the edge from freezing and waking over and over). One still
    /// settling in the water (<see cref="Craft.Settling"/>) isn't frozen however far off it is.
    /// </para>
    /// <para>
    /// One on a raft (the drone ship's deck or a floe: see <see cref="RaftUnder"/>) is never still in the world, as the
    /// raft never stops moving, so its stillness is measured in the raft's own frame instead
    /// (<see cref="Craft.StillFrom"/> and <see cref="Craft.StillTurned"/> are on the raft while it
    /// is over it): still on the raft, it freezes there, and is carried with it (see <see cref="Carry"/>).
    /// </para>
    /// </remarks>
    private void FreezeResting()
    {
        const float drift = Scene.FreezeSpeed * Scene.FreezeSeconds, turn = Scene.FreezeTurnRate * Scene.FreezeSeconds;
        foreach (var craft in _crafts)
        {
            if (craft.Frozen)
                continue;
            var at = Position(craft);
            var (position, rotation) = RaftUnder(craft) is { } raft ? OnDeck(At(raft), at, Rotation(craft)) : (at, Rotation(craft));
            var afloat = craft.Submerged > 0f;
            var still = craft.StillFor > 0f
                && (afloat ? !craft.Settling && !NearThePlayer(at, Scene.FloatFreezeDistance)
                    : Vector3.Distance(position, craft.StillFrom) < drift
                      && 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(rotation, craft.StillTurned)), 0f, 1f)) < turn)
                && !NearAMover(craft, at);
            if (!still)
                (craft.StillFor, craft.StillFrom, craft.StillTurned) = (StepLength, position, rotation); // a fresh stretch, from here
            else
                craft.StillFor += StepLength;
            // On the ground, or resting on another craft (lying across others, say), by Jolt's live
            // contacts: what it rests on is awake too (one touching a frozen craft wakes it: see
            // Carry), so the contact is held, as Jolt ends only a frozen craft's.
            if (craft.StillFor >= Scene.FreezeSeconds && (afloat || craft.OnGround || Touching(craft, SolidKind.Craft))) // OnGround: by its clearance before this step's Jolt update
                _ready.Add(craft);
        }
        // Crafts touching each other, directly or through others (a chain, or a pile), freeze
        // together or not at all: one frozen while another touching it is still awake is woken
        // again at once by the touch (see Carry), and each such wake stops it dead and disturbs
        // what rests on it, so a pile could freeze and wake by turns for ever. So each ready
        // craft's group (every awake craft touching it, through each other) is gathered, and
        // freezes only if every one in it is ready. A ready craft touching nothing awake is a
        // group of one, found by the one look round it that it always needed.
        foreach (var craft in _ready)
        {
            if (_grouped.Contains(craft))
                continue;
            if (GatherGroup(craft))
                foreach (var member in _group)
                    Freeze(member);
        }
        _ready.Clear();
        _grouped.Clear();
    }

    /// <summary>
    /// Gathers into <see cref="_group"/> every awake craft touching <paramref name="first"/>,
    /// directly or through each other (marking each in <see cref="_grouped"/>), and says whether
    /// all of them are ready to freeze (in <see cref="_ready"/>). Not one it is still parting from:
    /// those don't collide.
    /// </summary>
    private bool GatherGroup(Craft first)
    {
        _group.Clear();
        _group.Add(first);
        _grouped.Add(first);
        var allReady = true;
        for (var i = 0; i < _group.Count; i++) // the group grows as it is gone through
        {
            var craft = _group[i];
            var capsule = Outline(craft);
            foreach (var other in _crafts)
                if (!other.Frozen && !_grouped.Contains(other) && other.PartingFrom != craft && craft.PartingFrom != other
                    && Apart(capsule, Outline(other), TouchMargin) < TouchMargin)
                {
                    _group.Add(other);
                    _grouped.Add(other);
                    allReady &= _ready.Contains(other);
                }
        }
        return allReady;
    }

    /// <summary>How many points <see cref="LowPoints"/> gives at most: five down its axis, and its feet.</summary>
    private const int LowPointCount = 5 + RocketGeometry.LegCount;

    /// <summary>
    /// The points <paramref name="craft"/> rests on whichever way it lies, into
    /// <paramref name="points"/>: five down its axis, from the bottom of its engine bell to its top
    /// (always the first five), then its feet if its legs are out. Returns how many there are.
    /// </summary>
    private int LowPoints(Craft craft, Span<Vector3> points)
    {
        var model = Model(craft);
        var bell = craft.Vehicle.Stages[craft.First].BellHeight;
        var height = craft.Height;
        var count = 0;
        for (var i = 0; i <= 4; i++)
            points[count++] = Vector3.Transform(new Vector3(0, -bell + (height + bell) * i / 4f, 0), model);
        if (craft.LegsDeployed)
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
                points[count++] = Vector3.Transform(RocketGeometry.Foot(craft.Legs, leg), model);
        return count;
    }

    /// <summary>
    /// How far <paramref name="craft"/> is above the solid thing under it (the ground, a pad, the
    /// drone ship's deck, or the seabed), roughly: its lowest point down its axis (from the bottom
    /// of its engine bell to its top) less its width, or a foot if its legs are out (see
    /// <see cref="LowPoints"/>). Lying down or standing up, it is about 0 on the ground.
    /// </summary>
    private float Clearance(Craft craft)
    {
        Span<Vector3> points = stackalloc Vector3[LowPointCount];
        var count = LowPoints(craft, points);
        var radius = Widest(craft);
        var lowest = float.MaxValue;
        for (var i = 0; i < count; i++)
            lowest = MathF.Min(lowest, _body.GroundAltitude(points[i]) - (i < 5 ? radius : 0f)); // its axis is its width above what it lies on; a foot is on it
        return lowest;
    }

    /// <summary>
    /// What pulls <paramref name="craft"/> at <paramref name="p"/> where it is (gravity and the
    /// turning frame's outward pull, <see cref="GroundBody.OutwardPull"/>), as it falls and as the water
    /// holds it up (see <see cref="Float"/>). On something solid (<see cref="Craft.Grounded"/>) the
    /// outward pull is only its part straight up: off the equator the rest of it lies along level
    /// ground, towards the equator, and the ground is a sphere, not the shape a turning planet's
    /// would have settled in, level to gravity and the turn together. Nothing in Jolt resists
    /// rolling, so a booster lying on its side would roll off towards the equator however level
    /// the ground (about 0.1 m/s² at 70°, on Polar's zone), and never be still enough to freeze.
    /// So the ground is taken as having settled level to the turn, as the sea's push is balanced
    /// against both together. Flying, and in orbit, it feels the whole pull.
    /// </summary>
    private Vector3 Weight(Craft craft, Vector3 p)
    {
        var outward = _body.OutwardPull(p);
        if (craft.Grounded)
        {
            var up = _body.Up(p);
            outward = up * Vector3.Dot(outward, up);
        }
        return _body.Gravity(p) + outward;
    }

    /// <summary>
    /// Which awake crafts are on something solid this step (<see cref="Craft.Grounded"/>, which
    /// <see cref="Weight"/> goes by), worked out for them all before any falls. One is on the ground
    /// (or a pad, a raft or the seabed) by its own clearance (<see cref="Craft.OnGround"/>: within
    /// <see cref="Scene.FreezeClearance"/> of it, judged by <see cref="Clearance"/>, its hull or its
    /// feet, as <see cref="FreezeResting"/> judges it); or it rests on a craft lower down that is
    /// frozen or grounded itself, as one lying across others in a pile, layer on layer, which would
    /// otherwise be pushed along them by the pull the ones under it no longer feel. Grounding only
    /// passes down to up (from what is lower along Up to what lies on it), so it can't go round in a
    /// loop: two touching that rise off the ground together don't hold each other grounded. The
    /// crafts near the ground but not on it are gone through from the lowest up, so every layer of
    /// a pile is judged in the same step. Each one's clearance is kept (<see cref="Craft.GroundClearance"/>),
    /// and whether it is on the ground by it (<see cref="Craft.OnGround"/>) is what
    /// <see cref="FreezeResting"/> goes by. Only one near enough to the
    /// ground under it is looked at closely, so one flying or in orbit costs a single height lookup
    /// (two crafts touching in orbit keep the whole pull).
    /// </summary>
    /// <remarks>
    /// A craft near the ground but not on it (one resting on others, or in the last stretch of a
    /// landing) looks at every other craft to see what it rests on. Most are ruled out by how far
    /// their origins are apart, before any closer look; the rest are a craft or two within reach.
    /// </remarks>
    private void FindGrounded()
    {
        _low.Clear();
        foreach (var craft in _crafts)
        {
            if (craft.Frozen)
                continue;
            var reach = 2f * (craft.Length + craft.Widest) + Scene.FreezeClearance; // room for a few lying on each other
            craft.GroundClearance = _body.GroundAltitude(CentreOfMass(craft)) < reach ? Clearance(craft) : float.PositiveInfinity;
            craft.OnGround = craft.GroundClearance < Scene.FreezeClearance;
            craft.Grounded = craft.OnGround;
            if (!craft.OnGround && !float.IsPositiveInfinity(craft.GroundClearance))
            {
                craft.Level = Level(Outline(craft));
                _low.Add(craft);
            }
        }
        if (_low.Count == 0)
            return;
        _low.Sort(ByLevel);
        foreach (var craft in _low)
            craft.Grounded = RestsOnGrounded(craft);
    }

    private static readonly Comparison<Craft> ByLevel = (a, b) => a.Level.CompareTo(b.Level);

    /// <summary>How high the middle of a craft's axis is, above the sea: what "lower down" goes by in a pile.</summary>
    private float Level(in Capsule capsule) => _body.Altitude((capsule.Bottom + capsule.Top) / 2f);

    /// <summary>
    /// Whether <paramref name="craft"/> touches a craft lower down than it that is frozen or
    /// grounded (see <see cref="FindGrounded"/>: any lower than it have been judged this step).
    /// </summary>
    private bool RestsOnGrounded(Craft craft)
    {
        var capsule = Outline(craft);
        var origin = Position(craft);
        foreach (var other in _crafts)
        {
            if (other == craft || !(other.Frozen || other.Grounded) || other.PartingFrom == craft || craft.PartingFrom == other)
                continue;
            var near = craft.Length + craft.Widest + other.Length + other.Widest + TouchMargin;
            if (Vector3.DistanceSquared(other.Frozen ? other.FrozenAt : Position(other), origin) > near * near)
                continue; // too far off to touch (each origin is at one end of its craft)
            var under = Outline(other);
            if (Level(under) < craft.Level && Apart(capsule, under, TouchMargin) < TouchMargin) // lower down: what it rests on
                return true;
        }
        return false;
    }

    /// <summary>
    /// Gravity towards its body's centre, the turning frame's pulls (<see cref="GroundBody.TurningPull"/>:
    /// the world is fixed to the turning ground), and drag from however much air there is. The air
    /// is at rest over the ground, so the drag is on the velocity over the ground, as Jolt has it.
    /// Above the air, its turning is also pulled towards the stars' frame (still among them, so
    /// turning back at <see cref="GroundBody.Spin"/> over the ground) at the rate <see cref="AngularDamping"/>
    /// slows it, so the damping settles it there and not on the ground's frame; in the air the
    /// damping settles it on the air's, which is the ground's.
    /// </summary>
    /// <remarks>
    /// Jolt then moves it by the velocity this leaves, so a step is a kick and then a drift: the
    /// kick is <see cref="GroundBody.FallStep"/> (a Boris step, for why see there), so that whatever
    /// predicts a fall can step it the same way.
    /// <para>
    /// On something solid (<see cref="Craft.Grounded"/>) it feels the outward pull only straight
    /// up (see <see cref="Weight"/>), and its velocity is turned as it is, not half-kicked: the
    /// half kick there is gravity the ground is about to take away again, and turning it pushed a
    /// resting craft sideways by |Ω| g dt² a step (about 4e-6 m/s). Standing, its contact took that
    /// away, but a booster lying in the groove between two others rocked on it, never still
    /// enough to freeze, once the outward pull no longer pressed it to one side.
    /// </para>
    /// </remarks>
    private void Fall(Craft craft)
    {
        // As this step's resync read them (and the thrust since), unless it was woken since.
        var (position, velocity) = craft.StepRead ? (craft.ReadCentre, craft.ReadVelocity) : (CentreOfMass(craft), Velocity(craft));
        craft.StepRead = false;
        var altitude = _body.Altitude(position);
        var drag = Scene.DragRate * _body.AirDensity(altitude);
        var change = _body.FallStep(velocity, Weight(craft, position), drag, StepLength, noHalfKick: craft.Grounded); // see the remarks for why not on the ground
        // Low over the ground, its speed over it is held to the ground's top speed (Scene.GroundTopSpeed):
        // set here, after the thrust and the fall, so Jolt steps it at no more than that.
        var after = velocity + change;
        var held = after.LengthSquared() > Scene.GroundTopSpeed * Scene.GroundTopSpeed && _body.LowOverTheGround(position);
        if (held)
            _bodies.SetLinearVelocity(craft.Id, ToFloat3(after * (Scene.GroundTopSpeed / after.Length())));
        else
            _bodies.AddLinearVelocity(craft.Id, ToFloat3(change));
        if (craft.Kept)
        {
            // The same kick, of its double copy, while it may be flying free (see HandOver);
            // Jolt's own is what one that turns out not to goes on from, as it always has.
            if (craft.Grounded || craft.WreckedFor is not null || TouchingAnything(craft))
                craft.Kept = false;
            else
            {
                var at = craft.KeptCentre;
                var thick = Scene.DragRate * _body.AirDensity(_body.Altitude(at.ToSingle()));
                craft.KeptVelocity += _body.FallStep(craft.KeptVelocity, _body.Pull(at), thick, StepLength);
                if (held)
                    craft.KeptVelocity *= Scene.GroundTopSpeed / craft.KeptVelocity.Length(); // as Jolt's, so the two go on together
            }
        }
        if (_body.AboveAir(position))
        {
            var w = _bodies.GetAngularVelocity(craft.Id);
            var turning = new Vector3(w.X, w.Y, w.Z) - _body.Spin * (1f - MathF.Exp(-AngularDamping * StepLength));
            _bodies.SetAngularVelocity(craft.Id, ToFloat3(turning));
        }
    }

    /// <summary>
    /// <paramref name="craft"/>'s centre of mass, from the physics' own copy in double while it has
    /// one (<see cref="Craft.Kept"/>), else Jolt's. Jolt is built in single precision: stepping a
    /// float place and velocity, each step's rounding is carried into the next, and in a body's
    /// turning frame it leans the same way lap after lap, so an orbit would creep (a part in 100,000 a
    /// lap, tilted). So while a craft flies free (no live contact, not in the water, its legs not
    /// holding it) the game steps the copy itself, as Jolt would (the fall's kick, the thrust, the
    /// drift), and hands Jolt the copy rounded once a step (<see cref="HandOver"/>): rounded once,
    /// with nothing carried over. Anything else that moves it (a contact, the water, the legs, a
    /// stage parting, a raft, freezing and waking, or any set) is Jolt's: its result is the truth,
    /// and the copy is taken from it again (<see cref="Resync"/>). What the crossing, the edge's
    /// check, the handover to formula and saving take; every other read may read Jolt's float,
    /// which is the copy rounded.
    /// </summary>
    public Vector3Double TrueCentreOfMass(Craft craft)
    {
        Resync(craft);
        return craft.Kept ? craft.KeptCentre : CentreOfMass(craft);
    }

    /// <summary><paramref name="craft"/>'s velocity (its centre of mass's), as <see cref="TrueCentreOfMass"/> has its place.</summary>
    public Vector3Double TrueVelocity(Craft craft)
    {
        Resync(craft);
        return craft.Kept ? craft.KeptVelocity : Velocity(craft);
    }

    /// <summary>
    /// Gives <paramref name="craft"/>, just made (restored from a saved world, or back from the
    /// formula), the place and velocity it had in double: its copy (see <see cref="TrueCentreOfMass"/>),
    /// and Jolt handed them rounded, so it goes on from them with nothing lost to its floats. Only
    /// if Jolt made it there to its rounding (its origin and attitude, and its body's own centre of
    /// mass, put it within a few float steps of <paramref name="centre"/>); otherwise (a world
    /// written by hand, its centre of mass somewhere its body's isn't) it is Jolt's, as it was
    /// made. None for one frozen.
    /// </summary>
    public void Keep(Craft craft, Vector3Double centre, Vector3Double velocity)
    {
        if (craft.Removed || craft.Frozen || !Within(CentreOfMass(craft), centre, PlaceSteps) || !Within(Velocity(craft), velocity, VelocitySteps))
            return;
        (craft.KeptCentre, craft.KeptVelocity, craft.Kept) = (centre, velocity, true);
        PutCentre(craft, centre);
        _bodies.SetLinearVelocity(craft.Id, ToFloat3(velocity.ToSingle()));
    }

    /// <summary>
    /// How many of a float's steps (at its size) Jolt's place may be from the copy's, and still be
    /// the copy rounded and drifted by Jolt (each a step within half of one); and its velocity
    /// (which Jolt leaves as it was handed, if nothing touched it).
    /// </summary>
    private const double PlaceSteps = 4, VelocitySteps = 2; // Flight.LeaveTheSystem's skip margin allows for PlaceSteps: raise one, check the other

    /// <summary>
    /// Keeps <paramref name="craft"/>'s copy if Jolt's centre of mass and velocity are still the
    /// copy's as far as their rounding goes; otherwise (a contact in the step, any set since, or a
    /// craft with none) takes the copy from Jolt. None for one frozen (Jolt holds it still) or gone.
    /// With <paramref name="stepRead"/> (at a step's start), what it read is kept for the step's
    /// fall (<see cref="Craft.ReadCentre"/>).
    /// </summary>
    private void Resync(Craft craft, bool stepRead = false)
    {
        if (craft.Removed || craft.Frozen)
        {
            (craft.Kept, craft.StepRead) = (false, false);
            return;
        }
        var (centre, velocity) = (CentreOfMass(craft), Velocity(craft));
        if (stepRead)
            (craft.ReadCentre, craft.ReadVelocity, craft.StepRead) = (centre, velocity, true);
        if (craft.Kept && Within(centre, craft.KeptCentre, PlaceSteps) && Within(velocity, craft.KeptVelocity, VelocitySteps))
            return;
        (craft.KeptCentre, craft.KeptVelocity, craft.Kept) = (centre, velocity, true);
    }

    /// <summary>
    /// Whether each part of <paramref name="jolt"/> is within <paramref name="steps"/> of a float's
    /// steps of <paramref name="kept"/>, at the size of <paramref name="kept"/>. A set that moves it
    /// by less than that is taken into the copy, deliberately: it can't be told from rounding, and
    /// does nothing a float could show. A touch never relies on this: touching anything lets the
    /// copy go by itself (see Fall and HandOver). The steps are at the copy's own size, so a slow
    /// craft flying free keeps its copy step after step as a fast one does (a booster falling from
    /// rest, or drifting at 5 m/s, is never taken from Jolt again), though only at speed, or far
    /// out, is there rounding for it to keep out.
    /// </summary>
    private static bool Within(Vector3 jolt, Vector3Double kept, double steps)
    {
        var size = (float)kept.Length();
        var most = steps * (MathF.BitIncrement(size) - size);
        return Math.Abs(jolt.X - kept.X) <= most && Math.Abs(jolt.Y - kept.Y) <= most && Math.Abs(jolt.Z - kept.Z) <= most;
    }

    /// <summary>
    /// Hands Jolt <paramref name="craft"/>'s copy, rounded, for the step it is about to take, and
    /// moves the copy on by the drift Jolt will make, if it flew free this step: no live contact,
    /// not in the water, its legs not holding it (all as found this step, before Jolt's). One that
    /// didn't lets its copy go, and is Jolt's until it does again (see <see cref="Resync"/>).
    /// </summary>
    private void HandOver(Craft craft)
    {
        if (!craft.Kept)
            return;
        if (craft.Frozen || craft.Removed || craft.Submerged > 0f || TouchingAnything(craft))
        {
            craft.Kept = false;
            return;
        }
        PutCentre(craft, craft.KeptCentre);
        _bodies.SetLinearVelocity(craft.Id, ToFloat3(craft.KeptVelocity.ToSingle()));
        craft.KeptCentre += craft.KeptVelocity * StepLength;
    }

    /// <summary>
    /// Moves <paramref name="craft"/> so that its centre of mass is <paramref name="centre"/>
    /// rounded, as near as Jolt can put it: Jolt is given its body's origin, and works out its
    /// centre of mass from that in floats, so the origin is put right once or twice by what that
    /// missed. Its attitude and turning are left as they are.
    /// </summary>
    private void PutCentre(Craft craft, Vector3Double centre)
    {
        var target = centre.ToSingle();
        var now = CentreOfMass(craft);
        if (now == target)
            return;
        var origin = Position(craft);
        for (var put = 0; put < 2 && now != target; put++)
        {
            origin += target - now;
            _bodies.SetPosition(craft.Id, ToFloat3(origin), Activation.DontActivate);
            now = CentreOfMass(craft);
        }
    }

    /// <summary>
    /// The sea on <paramref name="craft"/>, in <see cref="Scene.WaterSamples"/> slices down its
    /// length, from the bottom of its engine bell to its top, each a piece of its cylinder. Each
    /// is in the water by how far its middle is under the waves (<see cref="Sea.Height"/>) over
    /// how tall the slice stands (its length upright, its width lying down), so one under the water
    /// to its top is wholly in; one over a raft is on it, not in the water (see <see cref="DepthUnder"/>). With <paramref name="push"/>, each slice in the water is pushed up
    /// by its share of <see cref="Scene.WaterBuoyancy"/>, and slowed by its share of
    /// <see cref="Scene.WaterDrag"/>, on its own movement (the craft's, and its turning, there)
    /// through the water, which the waves carry along (<see cref="Sea.Flow"/>). Each push is at
    /// its own slice, with the craft taken as a solid rod as in <see cref="Cushion"/>, so the waves
    /// rock it, it floats upright or lies down as the pushes have it, and the water slows its
    /// turning as well as its movement. Returns how much of it is in the water.
    /// </summary>
    private float Float(Craft craft, bool push)
    {
        if (!_body.HasSea)
            return 0f; // no water to float in
        var model = Model(craft);
        var bell = craft.Vehicle.Stages[craft.First].BellHeight;
        var height = craft.Height;
        var bottom = _body.Altitude(Vector3.Transform(new Vector3(0, -bell, 0), model));
        var top = _body.Altitude(Vector3.Transform(new Vector3(0, height, 0), model));
        if (MathF.Min(bottom, top) - Widest(craft) > Sea.Highest)
            return 0f; // nowhere near the water

        var axis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, model));
        var centre = CentreOfMass(craft);
        var up = _body.Up(centre);
        var v = _bodies.GetLinearVelocity(craft.Id);
        var w = _bodies.GetAngularVelocity(craft.Id);
        var (velocity, spin) = (new Vector3(v.X, v.Y, v.Z), new Vector3(w.X, w.Y, w.Z));
        var before = velocity;
        var gyration = Gyration(height);
        const int slices = Scene.WaterSamples;
        var length = (height + bell) / slices;
        var upright = MathF.Abs(Vector3.Dot(axis, up));
        // A whole slice's push, this step: against its weight as the turning ground has it (see
        // Fall), gravity and the outward pull together. Off the equator the outward pull has a part
        // along the sea, towards the equator, which a push straight up would leave unbalanced: a
        // craft afloat would drift metres a minute (the sea is a sphere, not the shape a turning
        // ocean would settle in, so nothing else stands against it). On the seabed in the
        // shallows its weight has the outward pull only straight up (see Weight), and so does this.
        var lift = -Weight(craft, centre) * (Scene.WaterBuoyancy * StepLength / slices);
        var slow = (1f - MathF.Exp(-Scene.WaterDrag * StepLength)) / slices;                    // and the share of its movement it takes
        var submerged = 0f;
        var buoyed = Vector3.Zero;
        for (var i = 0; i < slices; i++)
        {
            var y = -bell + length * (i + 0.5f);
            var point = Vector3.Transform(new Vector3(0, y, 0), model);
            var direction = _body.Up(point);
            var surface = Sea.Height(direction, _phases);
            var depth = surface - _body.Altitude(point);
            var tall = upright * length + MathF.Sqrt(MathF.Max(1f - upright * upright, 0f)) * 2f * RadiusAt(craft, y);
            var share = Math.Clamp(depth / tall + 0.5f, 0f, 1f);
            if (share <= 0f || _body.Height(direction) >= surface || Rafts.Under(point) is not null)
                continue; // out of the water, over dry land, or over a raft (lying on a floe: see DepthUnder)
            submerged += share / slices;
            buoyed += point * share;
            if (!push)
                continue;

            var arm = point - centre;
            var lifted = lift * share;
            velocity += lifted;
            spin += Across(Vector3.Cross(arm, lifted), axis) / gyration;
            var through = velocity + Vector3.Cross(spin, arm) - Sea.Flow(direction, _phases);
            var slowed = -through * (slow * share);
            velocity += slowed;
            spin += Across(Vector3.Cross(arm, slowed), axis) / gyration;
        }
        if (push && submerged > 0f
            && (DepthUnder(Vector3.Transform(new Vector3(0, -bell, 0), model)) > 0f || DepthUnder(Vector3.Transform(new Vector3(0, height, 0), model)) > 0f))
        {
            // Its ends: the water pushes back on an end driven along its length into it, however
            // little of it is in yet, which is what stops it plunging far past where it floats.
            // Along its length it is pushed through its centre of mass, so this doesn't turn it.
            var along = Vector3.Dot(velocity - Sea.Flow(up, _phases), axis); // through the water, as the waves carry it
            velocity -= axis * (along * (1f - MathF.Exp(-Scene.WaterEndDrag * StepLength)));
        }
        if (push && submerged > 0f)
        {
            _bodies.AddLinearVelocity(craft.Id, ToFloat3(velocity - before)); // as gravity is given (Add, not Set)
            _bodies.SetAngularVelocity(craft.Id, ToFloat3(spin));
        }
        if (submerged > 0f)
            craft.Buoyancy = buoyed / (submerged * slices);
        return submerged;
    }

    /// <summary>How wide <paramref name="craft"/>'s stage is at <paramref name="y"/> up its axis (its engine bell's, below the body).</summary>
    private static float RadiusAt(Craft craft, float y)
    {
        var stages = craft.Vehicle.Stages;
        if (y < 0f && stages[craft.First].BellHeight > 0f)
            return stages[craft.First].BellRadius;
        for (var i = craft.First; i < craft.Last; i++)
            if (y < RocketGeometry.StageBase(craft.Vehicle, craft.First, i + 1))
                return stages[i].Radius;
        return stages[craft.Last].Radius;
    }

    /// <summary>The radius of <paramref name="craft"/>'s widest stage.</summary>
    private static float Widest(Craft craft) => craft.Widest;

    /// <summary>The rod a craft <paramref name="height"/> long is taken as, to turn it: its radius of gyration across it, squared.</summary>
    private static float Gyration(float height) => (3f * Scene.RocketRadius * Scene.RocketRadius + height * height) / 12f;

    /// <summary>
    /// The crush legs (see <see cref="Scene.LegStroke"/>). The collider's feet are where the legs are
    /// pushed right up; within the stroke above that, a foot is on its own ground, and where it is
    /// moving down, that point of the craft is pushed: its speed and its turning both change, as a
    /// push there would change them (with the craft taken as a solid rod), so a foot down first
    /// turns it onto the others. Each leg holds: it stops its foot dead, unless that would change
    /// the craft's speed by more than <see cref="Scene.LegHoldAcceleration"/>, when it pushes that
    /// hard and gives. (Until all the feet are down, it holds only
    /// <see cref="Scene.LegSettleHold"/>: see below.) So the craft stands where its feet touched the
    /// ground, each leg as far out as its own ground needs, rather than sinking to the end of the
    /// stroke. The push is along the ground's normal under the foot (<see cref="GroundBody.Ground"/>:
    /// a pad's top, the drone ship's deck, or the terrain's slope, the seabed's under the sea: never the water), so on a slope it pushes the craft downhill too, and
    /// the foot's grip (<see cref="Scene.FootFriction"/>), across that normal, is what holds it
    /// there; too steep for the grip, and it slides. It only ever pushes, never pulls or springs
    /// back out, so it can't bounce. A foot's speed and slide are against the ground under it
    /// (<see cref="GroundBody.GroundVelocity"/>), so on the moving deck it holds the craft to the deck.
    /// </summary>
    private void Cushion(Craft craft)
    {
        if (!craft.LegsDeployed || craft.Removed)
        {
            craft.LegExtension = Vector4.One;
            craft.LegsLocked = false;
            craft.FeetDown = 0;
            return;
        }
        var model = Model(craft);
        var legs = craft.Legs;
        var axis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, model));
        var centre = CentreOfMass(craft);
        var v = _bodies.GetLinearVelocity(craft.Id);
        var w = _bodies.GetAngularVelocity(craft.Id);
        var (velocity, spin) = (new Vector3(v.X, v.Y, v.Z), new Vector3(w.X, w.Y, w.Z));
        var before = velocity;
        var height = craft.Height;
        var gyration = Gyration(height);
        Span<float> extension = stackalloc float[RocketGeometry.LegCount];
        Span<Vector3> arms = stackalloc Vector3[RocketGeometry.LegCount], normals = stackalloc Vector3[RocketGeometry.LegCount];
        Span<Vector3> grounds = stackalloc Vector3[RocketGeometry.LegCount]; // how fast the ground under each foot moves (the deck's)
        Span<bool> touching = stackalloc bool[RocketGeometry.LegCount];
        var down = 0;
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            var foot = Vector3.Transform(RocketGeometry.Foot(legs, leg), model);
            var gap = _body.GroundAltitude(foot);
            extension[leg] = Math.Clamp(gap / legs.Stroke, 0f, 1f);
            touching[leg] = gap < legs.Stroke;
            if (!touching[leg])
                continue;
            (arms[leg], normals[leg], grounds[leg]) = (foot - centre, _body.Ground(foot).Normal, _body.GroundVelocity(foot)); // only read for a foot that is down
            down++;
        }
        craft.LegExtension = new Vector4(extension[0], extension[1], extension[2], extension[3]);
        FeetTouch(craft, touching, arms, normals, grounds, centre, velocity, spin);
        if (down == 0)
        {
            craft.LegsLocked = false; // off the ground: they unlock, to settle afresh next time down
            return;
        }
        if (down >= Scene.LegsToHold)
            craft.LegsLocked = true;

        // Each foot on the ground, in turn, a few times over: a push at one foot moves the others
        // (lifting some), so each leg's push this step is worked out as a total, put right on each
        // pass, and held between nothing (it never pulls) and its budget (past which it gives). In
        // one pass, the last legs' pushes would lift the first feet, which no leg then pulled
        // back, and a booster at rest would creep up off its feet. The grip is a total too, up
        // to the friction's share of its leg's push.
        //
        // The legs share the load, as linked legs would: until enough feet are within their stroke
        // of the ground (Scene.LegsToHold), each one down holds only Scene.LegSettleHold, so the
        // first feet down give rather than tipping the craft onto the others, until those reach
        // their own ground. Then every one holds the full Scene.LegHoldAcceleration, and they
        // lock: they go on holding it while any foot is down, even if leaning lifts one out of
        // its stroke, as otherwise the rest would drop back to settling and crush, and it would
        // roll over its downhill foot. Nothing steers it level: within the stroke it ends near
        // upright, and past it, it leans the rest of the way.
        var budget = (craft.LegsLocked ? Scene.LegHoldAcceleration : Scene.LegSettleHold) * StepLength; // the most a leg can slow the craft in a step
        Span<float> pushes = stackalloc float[RocketGeometry.LegCount];
        Span<Vector3> grips = stackalloc Vector3[RocketGeometry.LegCount];
        for (var pass = 0; pass < LegPasses; pass++)
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
            {
                if (!touching[leg])
                    continue; // off the ground
                var (arm, normal, ground) = (arms[leg], normals[leg], grounds[leg]);
                // Held, or giving: its foot stopped dead within the budget, else slowed by it. Dead
                // against the ground under it, that is: on the drone ship's deck, its foot moves with the deck.
                var speed = -Vector3.Dot(velocity + Vector3.Cross(spin, arm) - ground, normal);
                var push = Math.Clamp(pushes[leg] + speed / Give(arm, normal, axis, gyration, out var lever), 0f, budget);
                velocity += normal * (push - pushes[leg]);
                spin += lever * ((push - pushes[leg]) / gyration);
                pushes[leg] = push;

                // The grip: the foot's slide along the ground stopped, as far as the friction
                // allows. Pushed there, as the push is, so it stops the foot rather than the centre.
                var slide = velocity + Vector3.Cross(spin, arm) - ground;
                slide -= normal * Vector3.Dot(slide, normal);
                var grip = grips[leg];
                var sliding = slide.Length();
                if (sliding > 1e-6f)
                    grip -= slide / Give(arm, slide / sliding, axis, gyration, out _);
                var most = Scene.FootFriction * push;
                if (grip.Length() > most)
                    grip *= most / grip.Length();
                var change = grip - grips[leg];
                velocity += change;
                spin += Across(Vector3.Cross(arm, change), axis) / gyration;
                grips[leg] = grip;
            }
        _bodies.AddLinearVelocity(craft.Id, ToFloat3(velocity - before)); // as thrust and gravity are given (Add, not Set)
        _bodies.SetAngularVelocity(craft.Id, ToFloat3(spin));
    }

    /// <summary>
    /// Each foot of <paramref name="craft"/>'s that has just come onto the ground (or a raft)
    /// within its stroke, as <see cref="Cushion"/> finds them (<paramref name="touching"/>, with
    /// each one's arm from <paramref name="centre"/>, the ground's normal and the ground's
    /// velocity under it), is a touch at its <see cref="CraftPart.Foot"/>: the legs hold the
    /// craft up themselves, so Jolt's contacts never see a landing on them. Judged at
    /// <paramref name="velocity"/> and <paramref name="spin"/>, as it moved before the legs pushed,
    /// and only if it was closing; a foot that has left the ground is off it again
    /// (<see cref="Craft.FeetDown"/>).
    /// </summary>
    private void FeetTouch(Craft craft, ReadOnlySpan<bool> touching, ReadOnlySpan<Vector3> arms, ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector3> grounds, Vector3 centre, Vector3 velocity, Vector3 spin)
    {
        var down = 0;
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
            if (touching[leg])
                down |= 1 << leg;
        craft.FeetDown &= down; // those that have left
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            if ((down & ~craft.FeetDown & (1 << leg)) == 0)
                continue; // not down, or down already
            var (foot, normal) = (centre + arms[leg], normals[leg]);
            var closing = velocity + Vector3.Cross(spin, arms[leg]) - grounds[leg];
            if (Vector3.Dot(closing, normal) < 0f)
            {
                var raft = _body.HasSea ? Rafts.Under(foot)?.Raft : null;
                var with = raft is null ? SolidKind.Ground : SolidKind.Raft;
                // Where its pad, hanging out along its stroke, touches the ground: straight under the foot.
                var point = foot - normal * MathF.Max(0f, _body.GroundAltitude(foot));
                // Judged before its own touch is counted (as Jolt's are), so only the first foot down is new.
                craft.TouchesBegun.Add(new Touch(with, null, raft, CraftPart.Foot, point, normal, closing, IsNewTouch(craft, with, CraftPart.Foot)));
            }
            craft.FeetDown |= 1 << leg;
        }
    }

    /// <summary>How many times over <see cref="Cushion"/> puts each leg's push right, each step.</summary>
    private const int LegPasses = 6;

    /// <summary>
    /// How much a push along <paramref name="direction"/> at <paramref name="arm"/> from the centre
    /// changes the speed there, per unit of push: its own share, and what the turning adds; and
    /// <paramref name="lever"/>, the turning it gives per unit of push (times the gyration).
    /// </summary>
    private static float Give(Vector3 arm, Vector3 direction, Vector3 axis, float gyration, out Vector3 lever)
    {
        lever = Across(Vector3.Cross(arm, direction), axis);
        return 1f + lever.LengthSquared() / gyration;
    }

    /// <summary>
    /// Only turning across the rod, which is all its gyration is for: with the ground's up a
    /// little off the craft's (on a slope, or a lean), or a foot gripping, a push would otherwise
    /// have a part about its own axis too, which every step added to, spinning a booster standing
    /// at rest slowly round on its feet (and so never still enough to freeze).
    /// </summary>
    private static Vector3 Across(Vector3 turn, Vector3 axis) => turn - axis * Vector3.Dot(turn, axis);

    /// <summary>
    /// Turns <paramref name="craft"/> towards <paramref name="desiredUp"/>, faster the further off it
    /// is, and stops it rolling about its own axis. Blended in rather than set outright, so it is smooth.
    /// </summary>
    private void Steer(Craft craft, Vector3 up, Vector3 desiredUp)
    {
        // cross() of the two is the axis to turn about, scaled by how far there is to turn.
        var turn = Vector3.Cross(up, desiredUp) * SteerGain;
        // Pointing the opposite way, cross() is nothing: turn about any sideways axis.
        if (Vector3.Dot(up, desiredUp) < 0f && turn.Length() < 0.5f)
            turn = Vector3.Normalize(Vector3.Cross(up, MathF.Abs(up.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ)) * MaxTurnRate;
        if (turn.Length() > MaxTurnRate)
            turn = Vector3.Normalize(turn) * MaxTurnRate;
        var w = _bodies.GetAngularVelocity(craft.Id);
        var current = new Vector3(w.X, w.Y, w.Z);
        var blended = Vector3.Lerp(current, turn, 1f - MathF.Exp(-StepLength * SteerBlendRate));
        _bodies.SetAngularVelocity(craft.Id, ToFloat3(blended));
    }

    /// <summary>The body this world is for: its ground, sea, air, sites and pull are the ones read, in its frame.</summary>
    public GroundBody Body => _body;

    /// <summary>How many bodies the Jolt world holds: the static scenery, the ground's tiles (<see cref="TileCount"/>) and the crafts, but not the tiles' placeholders (bodies never in the world). For soak tests, to see none are leaked.</summary>
    public int BodyCount => (int)_system.GetNumBodies() - (Scene.MaxTerrainTiles - TileCount);

    /// <summary>The ground's tiles, for tests.</summary>
    internal TerrainTiles Tiles => _tiles;

    /// <summary>A craft's origin (the bottom of its bottom stage's body), in metres.</summary>
    public Vector3 Position(Craft craft)
    {
        var p = _bodies.GetPosition(craft.Id);
        return new Vector3(p.X, p.Y, p.Z);
    }

    /// <summary>A craft's centre of mass, which it turns about.</summary>
    public Vector3 CentreOfMass(Craft craft)
    {
        var p = _bodies.GetCenterOfMassPosition(craft.Id);
        return new Vector3(p.X, p.Y, p.Z);
    }

    public Quaternion Rotation(Craft craft)
    {
        var r = _bodies.GetRotation(craft.Id);
        return new Quaternion(r.X, r.Y, r.Z, r.W);
    }

    /// <summary>Which way a craft's nose points.</summary>
    public Vector3 Up(Craft craft) => Vector3.Transform(Vector3.UnitY, Rotation(craft));

    public Vector3 Velocity(Craft craft)
    {
        var v = _bodies.GetLinearVelocity(craft.Id);
        return new Vector3(v.X, v.Y, v.Z);
    }

    /// <summary>Sets an awake craft's velocity (its centre of mass's), as a push would leave it: a frozen one is held still.</summary>
    public void SetVelocity(Craft craft, Vector3 velocity)
    {
        if (!craft.Removed && !craft.Frozen)
            _bodies.SetLinearVelocity(craft.Id, ToFloat3(velocity));
    }

    /// <summary>From a craft's own space (its bottom stage's) into the world.</summary>
    public Matrix4x4 Model(Craft craft)
    {
        // The turn, then the move: the same as multiplying by a translation, without the multiply
        // (several times a step for every craft).
        var model = Matrix4x4.CreateFromQuaternion(Rotation(craft));
        model.Translation = Position(craft);
        return model;
    }

    /// <summary>Every stage of every craft, to draw; a wreck's none, as it is gone from the scene (its fireball is drawn) while its body plays its hit out.</summary>
    public void GetParts(List<StagePart> parts)
    {
        parts.Clear();
        AddParts(parts);
    }

    /// <summary>Every stage of every craft here, as <see cref="GetParts"/> has them, added to <paramref name="parts"/> (each world's in turn: see <see cref="Worlds.GetParts"/>).</summary>
    public void AddParts(List<StagePart> parts)
    {
        foreach (var craft in _crafts)
        {
            // Left in the world for Scene.WreckSeconds of game time, it would be drawn flying on
            // past its fireball, whole: for a moment at normal speed, but for seconds as the game
            // is slowed for a fast collision (0.4 s at a tenth of the speed is 4 s).
            if (craft.WreckedFor is not null)
                continue;
            AddParts(parts, craft.Vehicle, craft.First, craft.Last, Model(craft), craft.LegDeploy, craft.BayDoors, craft.DoorStops, craft.FairingOn, craft.LegExtension);
        }
    }

    /// <summary>
    /// <paramref name="vehicle"/>'s stages <paramref name="first"/> to <paramref name="last"/>, placed
    /// by <paramref name="model"/> (the bottom stage's), added to <paramref name="parts"/> to draw:
    /// for a craft here, and for a vessel out of the physics from its own state (see
    /// <see cref="Vessel.FormulaParts"/>), so the two are drawn alike.
    /// </summary>
    internal static void AddParts(List<StagePart> parts, Vehicle vehicle, int first, int last, Matrix4x4 model, float legDeploy, float bayDoors, Vector2 doorStops, bool fairingOn, Vector4 legExtension)
    {
        for (var i = first; i <= last; i++)
        {
            var stageModel = Matrix4x4.CreateTranslation(0, RocketGeometry.StageBase(vehicle, first, i), 0) * model;
            parts.Add(new StagePart(vehicle.Stages[i], stageModel, i == first ? legDeploy : 0f, i == last && fairingOn)
            {
                LegExtension = legExtension,
                BayDoors = i == first ? bayDoors : 0f, // only the bottom stage's open (as its legs)
                DoorStops = doorStops,
            });
        }
    }

    private static float3 ToFloat3(Vector3 v) => new(v.X, v.Y, v.Z);

    private static floatQuat ToFloatQuat(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    public void Dispose()
    {
        _tiles.StopWorker(); // first: it makes shapes on its own thread, which mustn't outlive Jolt's world
        while (_crafts.Count > 0)
            Remove(_crafts[^1], wakeWhatRestsOnIt: false);
        _tiles.Dispose();
        foreach (var id in _staticBodies)
        {
            _bodies.RemoveBody(id);
            _bodies.DestroyBody(id);
        }
        foreach (var (raft, hull) in _hulls)
            if (!raft.IsShip && hull.Body is { } floe) // the floes' (the ships' are scenery)
            {
                _bodies.RemoveBody(floe);
                _bodies.DestroyBody(floe);
            }
        // The process's rafts let go only if they are this world's: one made ahead off the main
        // thread, or another body's, never took them, and mustn't take them from the world in use.
        var held = Rafts.HeldBy(_activeFloes);
        _activeFloes.Clear();
        if (_body.HasSea && held)
            Rafts.SetActiveFloes(_activeFloes);

        _system.SetContactListener(default); // off the system before it goes (it has no finalizer for that reason)
        _listener.Dispose();
        _system.Dispose();
        _layerVsBroadPhase.Dispose();
        _layerPairs.Dispose();
        _broadPhaseLayers.Dispose();
    }
}
