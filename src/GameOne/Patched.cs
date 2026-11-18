using System.Numerics;

namespace GameOne;

/// <summary>
/// One leg of a patched path (<see cref="Patched.Legs"/>): on its orbit round <paramref name="Body"/>
/// on <paramref name="Orbit"/> (in that body's frame, among its stars) from world time
/// <paramref name="Start"/> (where the orbit starts) to <paramref name="End"/> (where it crosses into
/// another body's sphere of influence or out of its own, the solar system's edge, or the horizon:
/// infinite for none). <paramref name="LeavesSystem"/>: it ends at the edge
/// (<see cref="Scene.SolarSystemEdge"/>), and nothing follows it.
/// </summary>
internal readonly record struct PathLeg(GroundBody Body, Orbit Orbit, double Start, double End, bool LeavesSystem = false);

/// <summary>
/// The path something with its engine off will follow across the bodies' spheres of influence: an
/// <see cref="Orbit"/> round each body it is at in turn, by that body's pull alone, patched where
/// it crosses a sphere's edge. Into a child's sphere at its <see cref="GroundBody.SphereRadius"/>,
/// out of its own only past <see cref="Scene.SphereLeaveFactor"/> times it, as
/// <see cref="Bodies.Choose(GroundBody, Vector3, double)"/> chooses a body; at the crossing the
/// state is changed into the next body's frame (<see cref="Bodies.Change"/>, in double), so the legs
/// meet. Nothing is written for any one pair of bodies: every child of the leg's body in
/// <see cref="Bodies.All"/> is looked for, and its parent left to. A path that reaches the solar
/// system's edge (<see cref="Scene.SolarSystemEdge"/>, round the sun) ends there, its last leg
/// marked as leaving (<see cref="PathLeg.LeavesSystem"/>), so nothing walks or searches on past it.
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
    /// at the solar system's edge if it gets there first, or at <paramref name="horizon"/> (a world time). <paramref name="from"/> is how many seconds
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
            if (LeavesAt(leg) is { } edge)
            {
                legs.Add(leg with { End = edge, LeavesSystem = true });
                return;
            }
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

        // Out past the child's distance and its sphere, it can't be in it: that stretch (out to
        // where it comes back down, if it does: a lap on for the next, a hyperbola or an escape
        // never) isn't sampled, so a path out to the solar system's edge costs no more than one
        // out to the child.
        var (gapFrom, gapTo, up, across) = Gap(orbit, leg.Start, (distance + radius) * 1.01);
        var t = leg.Start;
        var (r, w) = Relative(leg, child, reference, t);
        var inside = r.Length() < radius; // already in it: only a way in after it has left counts
        while (t < limit)
        {
            if (t >= gapFrom)
            {
                if (gapTo >= limit)
                    return null;
                t = gapTo;
                (r, w) = Relative(leg, child, reference, t);
                inside = r.Length() < radius;
                // A closed orbit goes out there again each lap: the next time it goes up through it.
                gapFrom = orbit.Closed && double.IsFinite(up)
                    ? up + Math.Ceiling(Math.Max(0, t - up) / orbit.Period) * orbit.Period
                    : double.PositiveInfinity;
                gapTo = gapFrom + across;
                continue;
            }
            var next = Math.Min(Math.Min(t + Scene.PatchSampleSeconds, limit), gapFrom);
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

    /// <summary>
    /// The first stretch (world times) from <paramref name="start"/> (the orbit's now) that the
    /// orbit is out past <paramref name="far"/> from its body's centre: from where it goes up
    /// through it (<paramref name="start"/>, if it is out there already) to where it comes back
    /// down (infinite, if it never does); and, for an orbit that goes round, when it first goes up
    /// through it and how long each stretch out there lasts, as it does again each lap. None
    /// (infinite) if it never gets that far.
    /// </summary>
    private static (double From, double To, double Up, double Across) Gap(in Orbit orbit, double start, double far)
    {
        const double Never = double.PositiveInfinity;
        if (orbit.HighestDistance <= far)
            return (Never, Never, Never, 0);
        var r0 = (orbit.PlaceAfter(0) - (Vector3Double)orbit.Body.Centre).Length();
        var down = Falling(orbit, far) is { } d ? start + d : Never;
        if (r0 >= far)
        {
            if (!orbit.Closed)
                return (start, down, Never, 0);
            var up = Rising(orbit, far) is { } u ? start + u : Never;
            return (start, down, up, double.IsFinite(up) ? down + orbit.Period - up : 0);
        }
        if (Rising(orbit, far) is not { } rising)
            return (Never, Never, Never, 0);
        return (start + rising, down, start + rising, down - (start + rising));
    }

    /// <summary>How long from the orbit's now until it next goes up through <paramref name="distance"/> from its body's centre, if it does before its end.</summary>
    private static double? Rising(in Orbit orbit, double distance) =>
        orbit.Radial ? orbit.RisingTo(distance) : orbit.Ascending(distance) is { } up ? orbit.TimeTo(up) : null;

    /// <summary>How long from the orbit's now until it next comes down through <paramref name="distance"/> from its body's centre, if it does before its end.</summary>
    private static double? Falling(in Orbit orbit, double distance) =>
        orbit.Radial ? orbit.FallingTo(distance) : orbit.Descending(distance) is { } down ? orbit.TimeTo(down) : null;

    /// <summary>How many places along the stretch where a leg could be at the edge are asked, before the crossing found between two is bisected.</summary>
    private const int EdgeSamples = 32;

    /// <summary>
    /// When (in world time) <paramref name="leg"/> first reaches the solar system's edge
    /// (<see cref="Scene.SolarSystemEdge"/> from the sun's centre, as it is then), between its start
    /// and its end (and no further than its orbit's path goes: down to the ground, or a lap with
    /// no end); null if it doesn't. Its body's centre is never further from the sun than
    /// <see cref="SolarSystem.FarthestFromSun"/>, so it can be at the edge only between that much
    /// short of the edge's radius from its body's centre and that much past it: that stretch is
    /// sampled, the place there turned back to its body's ground then and changed to the sun's
    /// frame, in double, and the crossing between two samples bisected to a millisecond. Costs a
    /// few comparisons for a leg that never gets that far.
    /// </summary>
    public static double? LeavesAt(in PathLeg leg)
    {
        var orbit = leg.Orbit;
        var edge = Scene.SolarSystemEdge;
        var sun = SolarSystem.FarthestFromSun(leg.Body, leg.Start);
        var (low, high) = (edge - sun, edge + sun);
        if (orbit.HighestDistance < low)
            return null;
        var end = leg.End;
        if (orbit.EndsOnGround)
            end = Math.Min(end, leg.Start + orbit.SecondsToEnd);
        else if (orbit.Closed)
            end = Math.Min(end, leg.Start + orbit.Period);
        var r0 = (orbit.PlaceAfter(0) - (Vector3Double)leg.Body.Centre).Length();
        if (r0 >= high)
            return leg.Start; // past it already
        double from;
        if (r0 >= low)
            from = leg.Start;
        else if (Rising(orbit, low) is { } up)
            from = leg.Start + up;
        else
            return null;
        if (from > end)
            return null;
        // To where it is surely past it, or back in under it, whichever comes first.
        var to = end;
        if (Rising(orbit, high) is { } past)
            to = Math.Min(to, leg.Start + past);
        if (Falling(orbit, low) is { } back && leg.Start + back > from)
            to = Math.Min(to, leg.Start + back);
        if (!double.IsFinite(to))
            return null;

        var before = from;
        if (FromSun(leg, before) >= edge)
            return before;
        for (var i = 1; i <= EdgeSamples; i++)
        {
            var t = from + (to - from) * i / EdgeSamples;
            if (FromSun(leg, t) < edge)
            {
                before = t;
                continue;
            }
            var after = t;
            while (after - before > 1e-3)
            {
                var mid = (before + after) / 2;
                if (mid <= before || mid >= after)
                    break; // as fine as a double's time there goes
                if (FromSun(leg, mid) >= edge)
                    after = mid;
                else
                    before = mid;
            }
            return after;
        }
        return null;
    }

    /// <summary>How far <paramref name="leg"/> is from the sun's centre at world time <paramref name="t"/>: its place then turned back from among its body's stars to its ground then, in double.</summary>
    private static double FromSun(in PathLeg leg, double t)
    {
        var body = leg.Body;
        var reference = leg.Start - leg.Orbit.From; // when the leg's frame matches its body's ground
        var back = QuaternionDouble.FromAxisAngle(body.NorthPole, body.TurnRadians(-(t - reference)));
        var ground = body.Centre + back.Rotate(leg.Orbit.PlaceAfter(t - leg.Start) - body.Centre);
        return SolarSystem.FromSun(body, ground, t);
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
