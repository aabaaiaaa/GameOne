using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;

namespace GameOne;

/// <summary>
/// Where a chain of nodes starts: how something on the path its first node sits on is moving at
/// world time <paramref name="Time"/>, before any burn, round the body named <paramref name="Body"/>
/// (<see cref="GroundBody.Name"/>). <paramref name="Position"/> is in that body's frame as it
/// matches its ground at <paramref name="Time"/> (the place over the ground it is at then), and
/// <paramref name="StarsVelocity"/> how it moves among the body's stars, in the same axes.
/// </summary>
/// <remarks>
/// Kept in the frame of its own time, not of when it was taken, so it is the same whenever it is
/// planned from: a plan at another time turns it by the body's turn between (<see cref="ManoeuvreChain.StartAt"/>).
/// </remarks>
internal readonly record struct ChainAnchor(string Body, double Time, Vector3 Position, Vector3 StarsVelocity);

/// <summary>
/// A vessel's planned burns, fixed once set: the first on the path the vessel was on when it was
/// set (its <see cref="Anchor"/>), each later one on the path the node before it plans, each
/// keeping its amounts and its <see cref="ManoeuvreNode.Gap"/> after the one before. So whatever
/// the vessel does meanwhile, the nodes and the paths they plan stay where they were set; changing
/// one redoes the chain after it, each later node keeping its gap and amounts.
/// </summary>
/// <remarks>
/// Immutable: every change gives a new chain. Equal chains are those with the same anchor and the
/// same nodes in turn. A chain with no nodes is no plan (<see cref="Empty"/>): a vessel given one
/// has none.
/// </remarks>
internal sealed record ManoeuvreChain(ChainAnchor Anchor, ImmutableArray<ManoeuvreNode> Nodes)
{
    /// <summary>The closest a node may be to now (the first) or to the node before it, seconds.</summary>
    public const double MinimumGap = 1.0;

    /// <summary>No nodes: given as a plan, it clears the vessel's.</summary>
    public static ManoeuvreChain Empty { get; } = new(default, []);

    /// <summary>When node <paramref name="i"/> is, in world time: the anchor's time and every gap up to it.</summary>
    public double TimeOf(int i)
    {
        var time = Anchor.Time;
        for (var k = 0; k <= i; k++)
            time += Nodes[k].Gap;
        return time;
    }

    /// <summary>
    /// The chain of <paramref name="nodes"/> anchored on <paramref name="v"/>'s path as it is at
    /// world time <paramref name="now"/> (its orbit with the engine off, patched at the spheres of
    /// influence), where that has it at world time <paramref name="time"/>: the first node's
    /// <see cref="ManoeuvreNode.Gap"/> is from then (0 puts it there).
    /// </summary>
    public static ManoeuvreChain AnchoredOn(Vessel v, double time, ImmutableArray<ManoeuvreNode> nodes, double now) =>
        AnchoredOn(v.Body, v.CentreOfMass, v.Body.AmongStars(v.CentreOfMass, v.Velocity), time, nodes, now);

    /// <summary>
    /// As <see cref="AnchoredOn(Vessel, double, ImmutableArray{ManoeuvreNode}, double)"/>, on the
    /// path of something at <paramref name="position"/> in <paramref name="body"/>'s frame moving at
    /// <paramref name="starsVelocity"/> among its stars at world time <paramref name="now"/>.
    /// </summary>
    public static ManoeuvreChain AnchoredOn(GroundBody body, Vector3 position, Vector3 starsVelocity, double time, ImmutableArray<ManoeuvreNode> nodes, double now)
    {
        var legs = new List<PathLeg>(Scene.PatchedLegs);
        Patched.Legs(body, position, starsVelocity, now, legs, horizon: time);
        var last = legs[^1];
        var (at, moving) = last.Orbit.StateAfter(time - last.Start);
        // From the frame matching the ground now into the one matching it at the anchor's time.
        var back = now - time;
        return new ManoeuvreChain(new ChainAnchor(last.Body.Name, time, last.Body.Turned(at, back), last.Body.TurnedDirection(moving, back)), nodes);
    }

    /// <summary>The body the anchor is round (worked out from its name, so not saved: the name is).</summary>
    [JsonIgnore]
    public GroundBody Body => Bodies.Named(Anchor.Body);

    /// <summary>The anchor's state in its body's frame as that matches the ground at world time <paramref name="now"/> (as a plan's legs are: see <see cref="Patched"/>).</summary>
    public (Vector3 Position, Vector3 StarsVelocity) StartAt(double now)
    {
        var body = Body;
        var on = Anchor.Time - now;
        return (body.Turned(Anchor.Position, on), body.TurnedDirection(Anchor.StarsVelocity, on));
    }

    /// <summary>Node <paramref name="i"/> as <paramref name="n"/>: its amounts and gap. The chain after it follows from its new trajectory, each later node keeping its own gap and amounts.</summary>
    public ManoeuvreChain WithNode(int i, ManoeuvreNode n) => this with { Nodes = Nodes.SetItem(i, n) };

    /// <summary>Where a node at world time <paramref name="time"/> would go: before the first node later than it.</summary>
    public int SlotFor(double time)
    {
        var at = Anchor.Time;
        for (var i = 0; i < Nodes.Length; i++)
            if ((at += Nodes[i].Gap) > time)
                return i;
        return Nodes.Length;
    }

    /// <summary>
    /// A node with no burn added at world time <paramref name="time"/>, on the path the chain plans
    /// there; the node after it keeps its time. Before every node (or into a chain with none), it is
    /// the first, anchored on <paramref name="v"/>'s path as it is at <paramref name="now"/>, and the
    /// old first goes on that path after it.
    /// </summary>
    public ManoeuvreChain Insert(double time, Vessel v, double now)
    {
        var slot = SlotFor(time);
        if (slot == 0)
        {
            ImmutableArray<ManoeuvreNode> nodes = Nodes.IsEmpty ? [new ManoeuvreNode(0)]
                : Nodes.SetItem(0, Nodes[0] with { Gap = TimeOf(0) - time }).Insert(0, new ManoeuvreNode(0));
            return AnchoredOn(v, time, nodes, now);
        }
        var gap = time - TimeOf(slot - 1);
        var later = slot < Nodes.Length ? Nodes.SetItem(slot, Nodes[slot] with { Gap = Nodes[slot].Gap - gap }) : Nodes;
        return this with { Nodes = later.Insert(slot, new ManoeuvreNode(gap)) };
    }

    /// <summary>
    /// Node <paramref name="i"/> taken out, the chain joined round it: the node after a middle one
    /// goes onto the path of the one before it, keeping its time; after the first, the second is the
    /// first, anchored on <paramref name="v"/>'s path as it is at <paramref name="now"/> at its time.
    /// The last taken out leaves <see cref="Empty"/>.
    /// </summary>
    public ManoeuvreChain Delete(int i, Vessel v, double now)
    {
        if (Nodes.Length == 1)
            return Empty;
        if (i == 0)
            return AnchoredOn(v, TimeOf(1), Nodes.RemoveAt(0).SetItem(0, Nodes[1] with { Gap = 0 }), now);
        var rest = i + 1 < Nodes.Length ? Nodes.SetItem(i + 1, Nodes[i + 1] with { Gap = Nodes[i + 1].Gap + Nodes[i].Gap }) : Nodes;
        return this with { Nodes = rest.RemoveAt(i) };
    }

    /// <summary>
    /// Node <paramref name="i"/> moved to world time <paramref name="time"/>, and every node after it
    /// with it, keeping their gaps; it keeps its amounts. It can't go within <see cref="MinimumGap"/>
    /// of the node before it, or for the first of <paramref name="now"/>. The first is anchored
    /// afresh on <paramref name="v"/>'s path as it is at <paramref name="now"/>, so it snaps onto the
    /// orbit the vessel is on now; a later one stays on the path of the one before it.
    /// </summary>
    public ManoeuvreChain MoveTo(int i, double time, Vessel v, double now)
    {
        if (i == 0)
            return AnchoredOn(v, Math.Max(time, now + MinimumGap), Nodes.SetItem(0, Nodes[0] with { Gap = 0 }), now);
        var before = TimeOf(i - 1);
        return WithNode(i, Nodes[i] with { Gap = Math.Max(time, before + MinimumGap) - before });
    }

    public bool Equals(ManoeuvreChain? other) =>
        other is not null && Anchor == other.Anchor && Nodes.AsSpan().SequenceEqual(other.Nodes.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Anchor);
        foreach (var node in Nodes)
            hash.Add(node);
        return hash.ToHashCode();
    }
}
