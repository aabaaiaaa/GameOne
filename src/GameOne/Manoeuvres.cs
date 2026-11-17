using System.Numerics;

namespace GameOne;

/// <summary>
/// A planned burn, one of a chain (<see cref="ManoeuvreChain"/>): <paramref name="Gap"/> seconds
/// after the node before it (the first, after its chain's anchor), this much speed along the way it
/// is going (prograde; negative is retrograde), out from the body it is round across that (radial
/// out; negative is in) and square to its orbit's plane (normal: the way the orbit's spin points;
/// negative is antinormal), in m/s. The pilot flies it; nothing burns it for them.
/// </summary>
/// <remarks>
/// It sits on the path the node before it plans (the first, on its chain's anchored path), and its
/// amounts are measured from how that path moves there: so changing an earlier node moves it, at
/// the same gap, onto that node's new path, keeping its amounts.
/// </remarks>
internal readonly record struct ManoeuvreNode(double Gap, float Prograde = 0f, float Radial = 0f, float Normal = 0f)
{
    /// <summary>How much speed it adds altogether, m/s.</summary>
    public float Size => MathF.Sqrt(Prograde * Prograde + Radial * Radial + Normal * Normal);

    /// <summary>Its burn as a change of velocity, for something at <paramref name="position"/> in <paramref name="body"/>'s frame moving at <paramref name="velocity"/> among its stars: measured round that body.</summary>
    public Vector3 DeltaV(GroundBody body, Vector3 position, Vector3 velocity)
    {
        var (prograde, radial, normal) = Manoeuvres.Frame(body, position, velocity);
        return prograde * Prograde + radial * Radial + normal * Normal;
    }
}

/// <summary>
/// How the next node's burn stands, for the pilot flying it (see <see cref="Vessel.Guide"/>): how
/// long until its time (negative once past), the change of velocity still needed (in the world),
/// whether the burn has gone past what was planned, and how long the rest would take at full
/// throttle (or that the stage hasn't the fuel for it).
/// </summary>
internal readonly record struct NodeGuide(double Seconds, Vector3 Left, bool Overshot, float BurnSeconds, bool Short);

/// <summary>The directions a node's burn is measured in, and the path a vessel's nodes plan.</summary>
internal static class Manoeuvres
{
    /// <summary>
    /// Prograde (the way it is moving), radial out (away from <paramref name="body"/>'s centre,
    /// square to that) and normal (square to the orbit's plane), as unit vectors, for something at
    /// <paramref name="position"/> in the body's frame moving at <paramref name="velocity"/>. Hardly
    /// moving, or straight up or down: built round up.
    /// </summary>
    public static (Vector3 Prograde, Vector3 Radial, Vector3 Normal) Frame(GroundBody body, Vector3 position, Vector3 velocity)
    {
        var up = body.Up(position);
        var prograde = velocity.LengthSquared() > 1e-4f ? Vector3.Normalize(velocity) : up;
        var normal = Vector3.Cross(position - body.Centre, velocity);
        normal = normal.LengthSquared() > 1e-2f ? Vector3.Normalize(normal) : body.North(position);
        return (prograde, Vector3.Cross(prograde, normal), normal);
    }
}

/// <summary>
/// The path a vessel's nodes plan: legs on its orbit with the engine off to each node in turn, its burn, and on, ending
/// with the path ahead after the last (a lap, or down to the ground). Each stretch between nodes is
/// patched at the spheres of influence (<see cref="Patched"/>), a leg round each body it is at, and
/// each node's burn is measured round the body it is at by then. With a chain, the first leg is
/// from its anchor (not from where the vessel is: its own path to the first node is drawn apart,
/// <see cref="Paths"/>), each later one from the burn before; with none, it is the path as it is.
/// </summary>
internal sealed class ManoeuvrePlan
{
    private readonly List<PathLeg> _legs = [];
    private readonly List<int> _nodeLegs = []; // by node: the leg that ends at it
    private readonly List<PathLeg> _stretch = new(Scene.PatchedLegs);

    /// <summary>
    /// The legs, as <see cref="Patched"/> legs: each round its body on its orbit, the engine off (in that
    /// body's frame) from its start (a world time) to its end (the next node's time or crossing;
    /// infinite for the last). With no node reached, the path as it is.
    /// </summary>
    public IReadOnlyList<PathLeg> Legs => _legs;

    /// <summary>The leg after the last node (the orbit it is on now, with none): the plan's last.</summary>
    public PathLeg Last => _legs[^1];

    /// <summary>The first leg after the last node the plan reaches (0 with none reached): where the path the nodes plan comes out begins.</summary>
    public int Final => _nodeLegs.Count > 0 ? _nodeLegs[^1] + 1 : 0;

    /// <summary>Whether the legs are a chain's (from its anchor), not the path as it is.</summary>
    public bool Chained { get; private set; }

    /// <summary>
    /// The legs with no nodes: the path of something at <paramref name="position"/> in
    /// <paramref name="body"/>'s frame, moving at <paramref name="velocity"/> among its stars, at
    /// world time <paramref name="now"/>.
    /// </summary>
    public void Update(GroundBody body, Vector3 position, Vector3 velocity, double now)
    {
        Chained = false;
        _legs.Clear();
        _nodeLegs.Clear();
        Patched.Legs(body, position, velocity, now, _stretch);
        Add(_stretch);
    }

    /// <summary>The legs <paramref name="vessel"/> would have drawn at world time <paramref name="now"/>: <paramref name="chain"/>'s (with no first burn begun), or with none, its path as it is.</summary>
    public void Update(Vessel vessel, ManoeuvreChain? chain, double now)
    {
        if (chain is { Nodes.IsEmpty: false })
            Update(chain, now, null);
        else
            Update(vessel.Body, vessel.CentreOfMass, vessel.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity), now);
    }

    /// <summary>
    /// The legs <paramref name="chain"/> plans, as at world time <paramref name="now"/>: from its
    /// anchor to its first node, then from each burn to the next node, and on after the last.
    /// <paramref name="firstLeft"/>, if given, is the first node's burn in place of its amounts, as
    /// a change of velocity in the anchor's body's frame (as it matches its ground now). A node
    /// beyond where the path comes down (or escapes, or past the legs a stretch is followed for)
    /// isn't reached.
    /// </summary>
    /// <remarks>
    /// The whole plan is among the stars, each leg in its body's frame as it matches that body's
    /// ground now: each burn is measured from how the path moves among the stars of the body it
    /// is at, and a leg's points are where they are in that frame. Only a leg's ground has turned
    /// on by its start, which is what its orbit's <c>from</c> is (see <see cref="Orbit"/>); the
    /// anchor is turned from its own time's frame into now's (<see cref="ManoeuvreChain.StartAt"/>).
    /// It depends on the chain and the time alone, never on where the vessel is now, so a node
    /// stays where it was set.
    /// </remarks>
    public void Update(ManoeuvreChain chain, double now, Vector3? firstLeft)
    {
        Chained = true;
        _legs.Clear();
        _nodeLegs.Clear();
        var nodes = chain.Nodes;
        var body = chain.Body;
        var first = body;
        var (position, velocity) = chain.StartAt(now);
        var start = chain.Anchor.Time;
        var time = start;
        for (var i = 0; i < nodes.Length; i++)
        {
            time += nodes[i].Gap;
            var at = Math.Max(time, start);
            Patched.Legs(body, position, velocity, now, _stretch, horizon: at, from: start - now);
            var last = _stretch[^1];
            if (last.End < at || (!last.Orbit.Closed && at - last.Start > last.Orbit.PathSeconds))
                break; // it never gets there
            Add(_stretch);
            _nodeLegs.Add(_legs.Count - 1);
            (body, (position, velocity)) = (last.Body, last.Orbit.StateAfter(at - last.Start));
            velocity += i == 0 && firstLeft is { } left
                ? Bodies.Change(first, body, now).Direction(left).ToSingle()
                : nodes[i].DeltaV(body, position, velocity);
            start = at;
        }
        Patched.Legs(body, position, velocity, now, _stretch, from: start - now);
        Add(_stretch);
    }

    private void Add(List<PathLeg> legs) => _legs.AddRange(legs);

    /// <summary>Which leg ends at node <paramref name="index"/> (the one its burn is made at the end of), if the plan reaches it.</summary>
    public int? NodeLeg(int index) => index < _nodeLegs.Count ? _nodeLegs[index] : null;

    /// <summary>Where node <paramref name="index"/> is (the end of its leg, in that leg's body's frame), and how the vessel moves there before its burn, if the plan reaches it.</summary>
    public (Vector3 Position, Vector3 Velocity)? NodeAt(int index) =>
        NodeLeg(index) is { } leg ? _legs[leg].Orbit.StateAfter(_legs[leg].End - _legs[leg].Start) : null;
}
