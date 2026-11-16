using System.Numerics;
using GameOne.Physics;

namespace GameOne;

/// <summary>
/// What is coming at each player-controlled vessel, the flown one and each small view's (see
/// <see cref="Flight.ComingAt"/>): kept once a step from what the flight's look ahead already asked
/// (each one's soonest fast collision and soonest near miss), so it costs no prediction of its own.
/// Each is kept a step to the next, so a collision warned of stays coming though its lead may
/// change. Fixed arrays, so a step allocates nothing.
/// </summary>
internal sealed class Comings
{
    // What is coming now and the step before, swapped each step; how many of each.
    private Coming[] _now = new Coming[1 + Scene.MaxSmallViews], _before = new Coming[1 + Scene.MaxSmallViews];
    private int _count, _beforeCount;

    // Each small view's vessel's soonest fast collision and soonest near miss coming this step, by
    // its place in the watched list. The game's list is the small views', which Views holds to
    // Scene.MaxSmallViews, so none is past the end; one past it (a list a test made longer) still
    // slows the game, as the flight asks of it, but is not kept here.
    private readonly Collision?[] _collisions = new Collision?[Scene.MaxSmallViews];
    private readonly NearMiss?[] _misses = new NearMiss?[Scene.MaxSmallViews];

    /// <summary>Everything coming at a controlled vessel now, one each at most.</summary>
    public ReadOnlySpan<Coming> All => _now.AsSpan(0, _count);

    /// <summary>What is coming at <paramref name="controlled"/> now, or null.</summary>
    public Coming? At(Vessel controlled)
    {
        // A few at most (the flown vessel and the small views'), so a search, not a lookup.
        for (var i = 0; i < _count; i++)
            if (_now[i].Watched == controlled)
                return _now[i];
        return null;
    }

    /// <summary>Forgets everything coming (a new world: none of its vessels are these).</summary>
    public void Clear()
    {
        Array.Clear(_now);
        Array.Clear(_before);
        (_count, _beforeCount) = (0, 0);
        Begin();
    }

    /// <summary>Starts a step's asking: no small view's collision or near miss is known yet (with the setting off, none comes).</summary>
    public void Begin()
    {
        Array.Clear(_collisions);
        Array.Clear(_misses);
    }

    /// <summary>The soonest fast collision of the small view's vessel at <paramref name="index"/> in the watched list, this step.</summary>
    public void SetCollision(int index, in Collision? soonest)
    {
        if (index < _collisions.Length)
            _collisions[index] = soonest;
    }

    /// <summary>The soonest near miss still coming at the small view's vessel at <paramref name="index"/> in the watched list, this step.</summary>
    public void Miss(int index, in NearMiss? soonest)
    {
        if (index < _misses.Length)
            _misses[index] = soonest;
    }

    /// <summary>
    /// Keeps what is coming at <paramref name="flown"/> (from its <paramref name="soonest"/> fast
    /// collision and its near miss coming, <paramref name="flownMiss"/>) and at each
    /// <paramref name="watched"/> one (from what <see cref="SetCollision"/> and <see cref="Miss"/> were
    /// given this step), each by the same rule (see <see cref="Keep(Vessel, in Collision?, in NearMiss?)"/>).
    /// </summary>
    public void Keep(Vessel flown, in Collision? soonest, in NearMiss? flownMiss, IReadOnlyList<Vessel>? watched)
    {
        (_now, _before, _beforeCount) = (_before, _now, _count);
        _count = 0;
        // The flown one by the same rule as the watched, not by its Warning's: so what is coming
        // at it may begin a moment before the COLLISION line, where its slowing begins first.
        Keep(flown, soonest, flownMiss);
        if (watched is not null)
            for (var i = 0; i < watched.Count && i < _collisions.Length; i++) // by index: a foreach over the interface would box its enumerator every step
                if (watched[i] != flown)
                    Keep(watched[i], _collisions[i], _misses[i]);
        Array.Clear(_before); // holding no vessel no longer coming
        _beforeCount = 0;
    }

    /// <summary>
    /// Keeps the sooner of <paramref name="collision"/> (<paramref name="v"/>'s soonest fast collision,
    /// if it is warned of: within its lead, <see cref="Collisions.WarningSeconds"/>, slowed for, or
    /// coming the step before) and <paramref name="miss"/> (its near miss coming), each only while
    /// its other is neither wrecked nor gone, as what is coming at <paramref name="v"/>.
    /// </summary>
    private void Keep(Vessel v, in Collision? collision, in NearMiss? miss)
    {
        if (_count == _now.Length || At(v) is not null)
            return; // (a vessel in two small views is kept once)
        Coming? coming = null;
        if (collision is { } m && (m.Other is not { } o || !Lost(o))
            && (WasComing(v, m) || m.Seconds <= Collisions.WarningSeconds(v, m) || Slowdown.ScaleFor(m.Seconds, m.HitSpeed, m.Touch) < 1f))
            coming = new Coming(v, m.With, m.Other, m.Seconds, m.At, Hit: m.Body);
        if (miss is { } n && !Lost(n.Other) && (coming is not { } c || n.Seconds < c.Seconds))
            coming = new Coming(v, SolidKind.Craft, n.Other, n.Seconds, n.Other.CentreOfMass, NearMiss: true);
        if (coming is { } now)
            _now[_count++] = now;
    }

    /// <summary>Whether <paramref name="collision"/> was what was coming at <paramref name="v"/> the step before (see <see cref="SameThing"/>).</summary>
    private bool WasComing(Vessel v, in Collision collision)
    {
        for (var i = 0; i < _beforeCount; i++)
            if (_before[i] is var before && before.Watched == v
                && SameThing(before.Other, before.With, before.Body, before.At, collision.Other, collision.With, collision.Body, collision.At))
                return true;
        return false;
    }

    /// <summary>
    /// Whether one thing hit (<paramref name="other"/>, or <paramref name="kind"/> at
    /// <paramref name="at"/> on <paramref name="body"/>) is <paramref name="nowOther"/> (or
    /// <paramref name="nowKind"/> at <paramref name="nowAt"/> on <paramref name="nowBody"/>): the
    /// same vessel; or for anything else, the same kind of thing at the same body, hit within
    /// <see cref="Scene.SameSolidRadius"/> of where it was in that body's frame (whatever the
    /// vessel's size, so a large one doesn't run two together; and whichever body the vessel is at
    /// meanwhile). The warnings' rule as well as this.
    /// </summary>
    internal static bool SameThing(Vessel? other, SolidKind kind, GroundBody? body, Vector3 at, Vessel? nowOther, SolidKind nowKind, GroundBody? nowBody, Vector3 nowAt) =>
        nowOther is { } vessel
            ? other == vessel
            : other is null && kind == nowKind && body == nowBody && Vector3.Distance(at, nowAt) <= Scene.SameSolidRadius;

    /// <summary>Whether <paramref name="other"/> is wrecked or removed from the world.</summary>
    internal static bool Lost(Vessel other) =>
        other.State == FlightState.Crashed || (other.Craft.Removed && !other.OnFormula); // under formula its body is out, but it is still there
}
