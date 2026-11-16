using System.Numerics;

namespace GameOne;

/// <summary>
/// One leg of a patched path (<see cref="Patched.Legs"/>): on its orbit round <paramref name="Body"/>
/// on <paramref name="Orbit"/> (in that body's frame, among its stars) from world time
/// <paramref name="Start"/> (where the orbit starts) to <paramref name="End"/> (where it crosses into
/// another body's sphere of influence or out of its own, or the horizon: infinite for none).
/// </summary>
internal readonly record struct PathLeg(GroundBody Body, Orbit Orbit, double Start, double End);

/// <summary>
/// The path something with its engine off will follow across the bodies' spheres of influence: an
/// <see cref="Orbit"/> round each body it is at in turn, by that body's pull alone, patched where
/// it crosses a sphere's edge. Into a child's sphere at its <see cref="GroundBody.SphereRadius"/>,
/// out of its own only past <see cref="Scene.SphereLeaveFactor"/> times it, as
/// <see cref="Bodies.Choose(GroundBody, Vector3, double)"/> chooses a body; at the crossing the
/// state is changed into the next body's frame (<see cref="Bodies.Change"/>, in double), so the legs
/// meet. Nothing is written for any one pair of bodies: every child of the leg's body in
/// <see cref="Bodies.All"/> is looked for, and its parent left to.
/// </summary>
/// <remarks>
/// Each leg is in its body's frame as its orbits always are: its points among that body's stars,
/// in the frame that matches the body's ground at the path's <c>now</c>, and its orbit's
/// <see cref="Orbit.From"/> how far ahead of <c>now</c> it starts, so each body's ground is turned
/// on under its leg as it is under the first.
/// </remarks>
internal static class Patched
{
    /// <summary>
    /// The legs (into <paramref name="legs"/>, cleared first) of the path of something at
    /// <paramref name="position"/> in <paramref name="body"/>'s frame, moving at
    /// <paramref name="starsVelocity"/> among its stars, at world time <paramref name="now"/>: up to
    /// <see cref="Scene.PatchedLegs"/> of them, the last ending at its next crossing if it has one,
    /// or at <paramref name="horizon"/> (a world time). <paramref name="from"/> is how many seconds
    /// after <paramref name="now"/> the state is (a plan's stretch after a node), in the frame that
    /// matches the body's ground at <paramref name="now"/>, as an <see cref="Orbit"/>'s is.
    /// <paramref name="clearance"/> is each leg's orbit's (see <see cref="Orbit"/>): the size of what
    /// follows the path, so a leg ends on its body's ground that much up.
    /// Allocates nothing once <paramref name="legs"/> has room.
    /// </summary>
    public static void Legs(GroundBody body, Vector3 position, Vector3 starsVelocity, double now, List<PathLeg> legs,
        double horizon = double.PositiveInfinity, double from = 0, float clearance = 0f)
    {
        legs.Clear();
        var start = now + from;
        while (true)
        {
            var leg = new PathLeg(body, new Orbit(body, position, starsVelocity, from: start - now, clearance: clearance), start, horizon);
            var crossing = Next(leg);
            if (crossing is { } found)
                leg = leg with { End = found.Time };
            legs.Add(leg);
            if (crossing is not { } next || legs.Count == Scene.PatchedLegs)
                return;
            (position, starsVelocity) = Change(leg, next.Time, next.Into, now);
            (body, start) = (next.Into, next.Time);
        }
    }

    /// <summary>
    /// When (in world time) <paramref name="leg"/>'s orbit first crosses into a child's sphere of
    /// influence or out of its own body's, between its start and its end (and no further than its
    /// orbit's path ahead goes: a lap, down to the ground, or out to its range); null if it doesn't.
    /// </summary>
    public static double? Crossing(in PathLeg leg) => Next(leg) is { } found ? found.Time : null;

    /// <summary>The leg's first crossing, and the body it crosses to.</summary>
    private static (double Time, GroundBody Into)? Next(in PathLeg leg)
    {
        var orbit = leg.Orbit;
        var leave = leg.Body.SphereRadius * (double)Scene.SphereLeaveFactor;
        // How far ahead it can be looked for: to the leg's end, within the orbit's path ahead (a
        // closed orbit's laps go on, so as far as the end, or a lap with none; straight up and
        // down, to the ground falling back, or out of its own sphere or to the range escaping).
        double limit;
        if (orbit.Radial)
            limit = Math.Min(leg.End, leg.Start + (orbit.EndsOnGround ? orbit.SecondsToEnd
                : orbit.RisingTo(Math.Min(leave, Orbit.MaxRange)) ?? orbit.RisingTo(Orbit.MaxRange) ?? 0));
        else
            limit = orbit.Closed
                ? double.IsFinite(leg.End) ? leg.End : leg.Start + orbit.Period
                : Math.Min(leg.End, leg.Start + orbit.SecondsToEnd);
        (double Time, GroundBody Into)? soonest = null;

        // Out of its own sphere: where its conic (or its line) goes up through the leave radius.
        if (leg.Body.Parent is { } parent
            && (orbit.Radial ? orbit.RisingTo(leave) : orbit.Ascending(leave) is { } up ? orbit.TimeTo(up) : null) is { } seconds)
        {
            var time = leg.Start + seconds;
            if (time <= limit)
                (soonest, limit) = ((time, parent), time);
        }

        // Into a child's: sampled, as the child moves on meanwhile.
        var bodies = Bodies.All;
        for (var i = 0; i < bodies.Count; i++)
            if (bodies[i].Parent == leg.Body && Enters(leg, bodies[i], limit) is { } time)
                (soonest, limit) = ((time, bodies[i]), time);
        return soonest;
    }

    /// <summary>
    /// When <paramref name="leg"/> first comes into <paramref name="child"/>'s sphere (from outside
    /// it) before <paramref name="limit"/>, if it does: sampled every
    /// <see cref="Scene.PatchSampleSeconds"/>, each sample's closest approach to the child over the
    /// next reckoned from how the two move there (so a graze shorter than a sample is found), then
    /// bisected to a millisecond.
    /// </summary>
    private static double? Enters(in PathLeg leg, GroundBody child, double limit)
    {
        var orbit = leg.Orbit;
        var body = leg.Body;
        var reference = leg.Start - orbit.From; // when the leg's frame matches its body's ground
        double radius = child.SphereRadius;
        // The bodies go round on circles, so the path can only reach the child's sphere where it is
        // as far from its body's centre as the child is, give or take the sphere.
        var (centre, _) = ChildAt(body, child, reference, leg.Start);
        var distance = (centre - body.Centre).Length();
        if (orbit.LowestDistance > distance + radius || orbit.HighestDistance < distance - radius)
            return null;
        // How far the two can bend from straight over a sample: each is pulled by the body, the
        // path at most as hard as at its periapsis (or its body's lowest surface, if that is higher:
        // it comes down there first).
        var lowest = Math.Max(orbit.LowestDistance, body.LowestRadius);
        var pull = body.Mu / (lowest * lowest) + body.Mu / (distance * distance);

        var t = leg.Start;
        var (r, w) = Relative(leg, child, reference, t);
        var inside = r.Length() < radius; // already in it: only a way in after it has left counts
        while (t < limit)
        {
            var next = Math.Min(t + Scene.PatchSampleSeconds, limit);
            var (r1, w1) = Relative(leg, child, reference, next);
            if (!inside)
            {
                if (r1.Length() < radius)
                    return Bisect(leg, child, reference, radius, t, next);
                var h = next - t;
                var along = w.LengthSquared() > 0 ? Math.Clamp(-Vector3Double.Dot(r, w) / w.LengthSquared(), 0, h) : 0;
                if ((r + w * along).Length() < radius + pull * h * h / 2)
                {
                    var (nearestAt, nearest) = Closest(leg, child, reference, t, next);
                    if (nearest < radius)
                        return Bisect(leg, child, reference, radius, t, nearestAt);
                }
            }
            else if (r1.Length() >= radius)
                inside = false;
            (t, r, w) = (next, r1, w1);
        }
        return null;
    }

    /// <summary>The first moment between <paramref name="outside"/> and <paramref name="inside"/> (to a millisecond) that the leg is within <paramref name="radius"/> of the child's centre.</summary>
    private static double Bisect(in PathLeg leg, GroundBody child, double reference, double radius, double outside, double inside)
    {
        while (inside - outside > 1e-3)
        {
            var mid = (outside + inside) / 2;
            if (Relative(leg, child, reference, mid).Offset.Length() < radius)
                inside = mid;
            else
                outside = mid;
        }
        return inside;
    }

    /// <summary>Where between <paramref name="from"/> and <paramref name="to"/> (a sample apart) the leg is nearest the child's centre, and how near: a golden-section search, as a pass that short is one dip.</summary>
    private static (double At, double Distance) Closest(in PathLeg leg, GroundBody child, double reference, double from, double to)
    {
        const double Golden = 0.6180339887498949;
        var (a, b) = (from, to);
        var (c, d) = (b - Golden * (b - a), a + Golden * (b - a));
        var (fc, fd) = (Relative(leg, child, reference, c).Offset.Length(), Relative(leg, child, reference, d).Offset.Length());
        while (b - a > 1e-3)
        {
            if (fc < fd)
            {
                (b, d, fd) = (d, c, fc);
                c = b - Golden * (b - a);
                fc = Relative(leg, child, reference, c).Offset.Length();
            }
            else
            {
                (a, c, fc) = (c, d, fd);
                d = a + Golden * (b - a);
                fd = Relative(leg, child, reference, d).Offset.Length();
            }
        }
        return fc < fd ? (c, fc) : (d, fd);
    }

    /// <summary>The leg's place and velocity at world time <paramref name="t"/> less the child's centre's, among the leg's body's stars.</summary>
    private static (Vector3Double Offset, Vector3Double Velocity) Relative(in PathLeg leg, GroundBody child, double reference, double t)
    {
        var (p, v) = leg.Orbit.StateAfter(t - leg.Start);
        var (centre, velocity) = ChildAt(leg.Body, child, reference, t);
        return ((Vector3Double)p - centre, (Vector3Double)v - velocity);
    }

    /// <summary>
    /// <paramref name="child"/>'s centre and its velocity at world time <paramref name="t"/>, among
    /// <paramref name="body"/>'s stars (its parent's) in the frame that matches its ground at
    /// <paramref name="reference"/>: where it is over that ground then, turned on by the ground's turn since.
    /// </summary>
    private static (Vector3Double Centre, Vector3Double Velocity) ChildAt(GroundBody body, GroundBody child, double reference, double t)
    {
        var change = FrameChange.ToParent(child, t);
        var centre = change.Point(child.Centre);
        var velocity = change.Velocity(child.Centre, default) + Vector3Double.Cross(body.Spin, centre - body.Centre);
        var turn = QuaternionDouble.FromAxisAngle(body.NorthPole, body.TurnRadians(t - reference));
        return (body.Centre + turn.Rotate(centre - body.Centre), turn.Rotate(velocity));
    }

    /// <summary>
    /// The leg's state at world time <paramref name="t"/>, changed into <paramref name="into"/>'s
    /// frame: back to its body's ground then, across by the frame change between the two, and on
    /// among <paramref name="into"/>'s stars in the frame that matches its ground at
    /// <paramref name="now"/>, as the next leg's orbit is.
    /// </summary>
    private static (Vector3 Position, Vector3 Velocity) Change(in PathLeg leg, double t, GroundBody into, double now)
    {
        var body = leg.Body;
        var (p, v) = leg.Orbit.StateAfter(t - leg.Start);
        var back = QuaternionDouble.FromAxisAngle(body.NorthPole, body.TurnRadians(-(t - now)));
        var ground = body.Centre + back.Rotate((Vector3Double)p - body.Centre);
        var overGround = back.Rotate(v) - Vector3Double.Cross(body.Spin, ground - body.Centre);
        var change = Bodies.Change(body, into, t);
        var there = change.Point(ground);
        var velocity = change.Velocity(ground, overGround) + Vector3Double.Cross(into.Spin, there - into.Centre);
        var on = QuaternionDouble.FromAxisAngle(into.NorthPole, into.TurnRadians(t - now));
        return ((into.Centre + on.Rotate(there - into.Centre)).ToSingle(), on.Rotate(velocity).ToSingle());
    }
}
