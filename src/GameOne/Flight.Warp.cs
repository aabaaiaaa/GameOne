namespace GameOne;

/// <summary>The time rate: the doubling ladder, the gate that holds it to the physics' top rate, and its drops.</summary>
internal sealed partial class Flight
{
    /// <summary>
    /// The rates time can run at, from slow motion to time warp, each double the last, from 1/4 to
    /// <see cref="Scene.TopRate"/> (the player steps through them with <see cref="FlightInput.Slower"/>
    /// and <see cref="FlightInput.Faster"/>): the physics' up to <see cref="Scene.PhysicsTopRate"/>,
    /// the formula's past it (<see cref="IsFormulaRate"/>).
    /// </summary>
    public static readonly float[] Rates = Ladder();

    private static float[] Ladder()
    {
        var rates = new List<float>();
        for (var rate = 0.25f; rate <= Scene.TopRate; rate *= 2f)
            rates.Add(rate);
        return [.. rates];
    }

    /// <summary>Normal speed's place among the <see cref="Rates"/>.</summary>
    private static readonly int NormalRate = Array.IndexOf(Rates, 1f);

    /// <summary>The physics' top rate's place among the <see cref="Rates"/> (<see cref="Scene.PhysicsTopRate"/>).</summary>
    internal static readonly int PhysicsTopIndex = Array.IndexOf(Rates, Scene.PhysicsTopRate);

    /// <summary>Whether <paramref name="rate"/> is one of the formula's, past the physics' top rate.</summary>
    public static bool IsFormulaRate(float rate) => rate > Scene.PhysicsTopRate;

    private int _rate = NormalRate;

    /// <summary>The rate the player has picked (see <see cref="Rates"/>): 1 to start with, and kept from flight to flight.</summary>
    public float PlayerRate => Rates[_rate];

    /// <summary>
    /// The rate the game runs at, everything on its clock (the flight, the effects, the camera,
    /// the small views): the player's own, or the forced slow-down's while one is on
    /// (<see cref="Slowed"/>) and slower.
    /// </summary>
    public float TimeScale => Slowed is { } slowed ? MathF.Min(PlayerRate, slowed.Scale) : PlayerRate;

    /// <summary>Whether the game runs at one of the formula's rates (<see cref="IsFormulaRate"/>).</summary>
    public bool ByFormula => IsFormulaRate(TimeScale);

    /// <summary>
    /// The witnessed vessel (the flown one, a small view's, the main camera's) holding the warp to
    /// the physics' top rate, if one is: the first flying in the air or low over the ground, or not
    /// yet at rest (neither <see cref="Vessel.AtRest"/> nor <see cref="Vessel.AboveTheAir"/>; a
    /// wreck flies nowhere, and holds nothing). Worked out as each frame begins, before the rate
    /// keys are taken: while it is set, <see cref="FlightInput.Faster"/> past the physics' top rate
    /// does nothing.
    /// </summary>
    /// <remarks>One burning counts as in the air: the formula doesn't fly a witnessed vessel's burn.</remarks>
    public Vessel? WarpHeldBy { get; private set; }

    /// <summary>The warp's last drop to the physics' top rate (<see cref="DropToPhysics"/>), for the HUD to say why, a while.</summary>
    public WarpDropped? LastDrop { get; private set; }

    /// <summary>The flight's own clock of real seconds (the frames' sum), by which <see cref="LastDrop"/> is timed.</summary>
    public double RealTime => _realTime;

    /// <summary>
    /// The world time the flight is warping to (<see cref="FlightInput.WarpTo"/>: a point on the
    /// flown vessel's path), if it is: at the fastest of the <see cref="Rates"/> the gate allows
    /// (<see cref="WarpHeldBy"/>) until the clock gets there, the last frame shortened so it stops
    /// there exactly, then at normal speed. It ends early, at normal speed, on a rate key, another
    /// vessel flown, a warning that drops the warp (<see cref="Warning"/>, <see cref="NearMissWarning"/>),
    /// the vessel no longer flying (landed, afloat, wrecked or gone from the solar system), or a
    /// world loaded or a flight begun, and at the physics' top rate on a drop to it
    /// (<see cref="LastDrop"/>); while the engine burns its time is held to where the path then
    /// ends. What the path's mark shows while it runs.
    /// </summary>
    public double? WarpingTo => _warpTo?.Time;

    private (double Time, Vessel For)? _warpTo;
    private readonly List<PathLeg> _warpLegs = new(Scene.PatchedLegs);

    /// <summary>How near the time warped to the clock must be to have got there: far within a step, as the frame's steps' float sum misses it by.</summary>
    private const double ArrivedSeconds = 1e-4;

    /// <summary>
    /// Begins warping to <paramref name="time"/> (see <see cref="WarpingTo"/>), no later than the
    /// flown vessel's path goes with its engine off (down to the ground, out to the edge), and only
    /// for a time still ahead of a vessel flying.
    /// </summary>
    private void BeginWarpTo(double time)
    {
        var vessel = Controlled;
        if (vessel.State != FlightState.Flying || !(time > WorldTime))
            return;
        time = Math.Min(time, PathEnd(vessel));
        if (!(time > WorldTime))
            return;
        _warpTo = (time, vessel);
        _rate = WarpHeldBy is null ? Rates.Length - 1 : PhysicsTopIndex;
    }

    /// <summary>When <paramref name="vessel"/>'s path, with its engine off from now, ends (<see cref="Patched.Ends"/>): infinity for one that goes round for ever.</summary>
    private double PathEnd(Vessel vessel)
    {
        Patched.Legs(vessel.Body, vessel.CentreOfMass, vessel.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity), WorldTime, _warpLegs);
        return Patched.Ends(_warpLegs);
    }

    /// <summary>
    /// While the vessel warped for burns, the warp's time held again (once a frame) to where its
    /// path now ends: a burn that brings it down sooner doesn't warp it on past that.
    /// </summary>
    private void HoldWarpToPath()
    {
        if (_warpTo is { } warp && warp.For == Controlled && Controlled.EngineLit)
        {
            var end = PathEnd(Controlled);
            if (end < warp.Time)
                _warpTo = (Math.Max(end, WorldTime), warp.For);
        }
    }

    /// <summary>
    /// Drops the time warp to normal speed, ending a warp to a point (<see cref="WarpingTo"/>) with
    /// it: the warnings' drop at the physics' rates.
    /// </summary>
    private void DropWarp()
    {
        _rate = NormalRate;
        _warpTo = null;
    }

    /// <summary>
    /// Drops the time warp from a formula's rate to the physics' top rate, for
    /// <paramref name="reason"/> (of the vessel <paramref name="of"/>, if it is a vessel's), ending a
    /// warp to a point with it, and keeps the drop as <see cref="LastDrop"/>; at the physics' rates
    /// it changes nothing. It never goes back up by itself: only the rate keys and a warp to a point raise it.
    /// </summary>
    private void DropToPhysics(WarpDrop reason, Vessel? of)
    {
        if (_rate <= PhysicsTopIndex)
            return;
        _rate = PhysicsTopIndex;
        _warpTo = null;
        LastDrop = new WarpDropped(reason, of is null ? "" : WorldLog.Named(of), Scene.PhysicsTopRate, _realTime);
    }

    /// <summary>
    /// The gate, as a frame begins (<see cref="WarpHeldBy"/>, of the flown vessel, the small views'
    /// <paramref name="watched"/> and the main camera's <paramref name="watching"/>, each counted
    /// from the frame it is watched in, its link-up still waiting or not), and the warp dropped to
    /// the physics' top rate if it is at a formula's while a vessel holds it: into the air, or near
    /// the ground of a body with none. The flown vessel at rest set moving by <paramref name="input"/>
    /// (launched, or its engine lit, with fuel to light it) drops it too, so no frame at a formula's
    /// rate runs its take-off; flying, the throttle drops nothing (a lit engine holds it, as the
    /// formula doesn't fly a witnessed vessel's burn).
    /// </summary>
    private void Gate(IReadOnlyList<Vessel>? watched, Vessel? watching, in FlightInput input)
    {
        var flown = Controlled;
        WarpHeldBy = Holds(flown) ? flown : null;
        for (var i = 0; WarpHeldBy is null && watched is not null && i < watched.Count; i++) // by index: a foreach over the interface would box its enumerator
            if (Holds(watched[i]))
                WarpHeldBy = watched[i];
        if (WarpHeldBy is null && watching is not null && Holds(watching))
            WarpHeldBy = watching;
        if (WarpHeldBy is { } held)
            DropToPhysics(held.Body.HasAir ? WarpDrop.Air : WarpDrop.Ground, held);
        else if (flown.AtRest && flown.Fuel > 0f && (input.Launch || input.FullThrottle || input.Throttle > 0f))
            DropToPhysics(WarpDrop.SetMoving, flown);
    }

    /// <summary>Whether <paramref name="v"/>, witnessed, holds the warp (see <see cref="WarpHeldBy"/>).</summary>
    private static bool Holds(Vessel v) =>
        v.State != FlightState.Crashed && !v.AtRest && !(v.AboveTheAir && !v.EngineLit);

    /// <summary>
    /// Ends a warp to a point at normal speed whose vessel is no longer the one flown, or is about
    /// to be (a switch's link-up begun: the vessel is flown only once it is through), or is no
    /// longer flying: landed, settled afloat, wrecked or gone from the system.
    /// </summary>
    private void KeepWarpTo()
    {
        if (_warpTo is { } warp && (warp.For != Controlled || LinkUp.Waiting || warp.For.State != FlightState.Flying))
            DropWarp();
    }
}
