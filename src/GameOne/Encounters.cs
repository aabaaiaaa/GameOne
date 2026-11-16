using System.Numerics;

namespace GameOne;

/// <summary>
/// Working out how a vessel's path meets its target's: where they will be closest, and the
/// stretches of its orbit where one burn would take it there (a transfer window: to a body, into
/// its sphere of influence, against its centre's orbit, <see cref="Bodies.OrbitOf"/>), or bring
/// it down on a place on the ground (a de-orbit window). With the engine off, under gravity alone, as <see cref="Orbit"/>
/// is, so it ignores the air.
/// </summary>
/// <remarks>
/// The windows are shown as stretches, never as a moment to burn: each is every place on the orbit
/// where a simple burn (along or against the way it is going, sized by a rule) would arrive close
/// enough. How wide it is comes from the physics, so a forgiving transfer shows a long stretch and
/// a fussy one a short one. They are found by trying a burn at places round the lap ahead (a
/// de-orbit window, the laps ahead: see <see cref="Deorbit"/>), then more finely round the best of
/// those, as a window can be narrower than the first spacing.
/// </remarks>
internal static class Encounters
{
    /// <summary>
    /// The closest the two come: how long from now, where each will be, and how far apart; and, of
    /// a patched path's legs, which leg it is on (whose frame the places are in: see <see cref="BodyApproach"/>).
    /// </summary>
    public readonly record struct Approach(double Seconds, Vector3 Mine, Vector3 Theirs, float Distance, int Leg = 0);

    /// <summary>A place in a window: where on the orbit (its true anomaly), and how close a burn there would bring it.</summary>
    public readonly record struct WindowPoint(double Anomaly, float Miss);

    private const int ApproachSteps = 240, FineSteps = 16, SolveSteps = 22;

    /// <summary>The places round a lap a window's burns are first tried at: the size of the <c>coarse</c> buffer the steps take.</summary>
    public const int WindowSteps = 72;

    /// <summary>
    /// The closest <paramref name="mine"/> comes to the target over the path ahead (a lap, or to its
    /// end): the target on its orbit <paramref name="theirs"/>, or, if that is null, staying at
    /// <paramref name="fixedAt"/> (a place on the ground, or a vessel standing on it). Both orbits
    /// start now; the place is carried round with the ground under them, so its marks are where
    /// it will be by then, among the stars like the orbits' points. No further than
    /// <paramref name="until"/> seconds along mine (a leg of a patched path, to where it crosses).
    /// </summary>
    public static Approach? Closest(in Orbit mine, in Orbit? theirs, Vector3 fixedAt, double until = double.PositiveInfinity)
    {
        if (mine.Radial)
            return null;
        var horizon = Math.Min(mine.PathSeconds, until);
        if (!(horizon > 0) || double.IsInfinity(horizon))
            return null;

        var they = theirs;
        var me = mine;
        Vector3 Theirs(double t) => they is { } o ? o.PositionAfter(t) : me.Body.Turned(fixedAt, t);
        float Apart(double t) => Vector3.Distance(me.PositionAfter(t), Theirs(t));

        var best = 0;
        var bestDistance = float.MaxValue;
        for (var i = 0; i <= ApproachSteps; i++)
        {
            var d = Apart(horizon * i / ApproachSteps);
            if (d < bestDistance)
                (best, bestDistance) = (i, d);
        }

        // Narrowed down between the samples either side (golden-section search).
        double lo = horizon * Math.Max(0, best - 1) / ApproachSteps, hi = horizon * Math.Min(ApproachSteps, best + 1) / ApproachSteps;
        const double golden = 0.6180339887;
        for (var i = 0; i < 40 && hi - lo > 1e-3; i++)
        {
            double a = hi - golden * (hi - lo), b = lo + golden * (hi - lo);
            if (Apart(a) < Apart(b))
                hi = b;
            else
                lo = a;
        }
        var t = (lo + hi) / 2;
        var mineAt = mine.PositionAfter(t);
        var theirsAt = Theirs(t);
        return new Approach(t, mineAt, theirsAt, Vector3.Distance(mineAt, theirsAt));
    }

    /// <summary>
    /// The transfer window to a target on its orbit <paramref name="theirs"/>: every place on the
    /// path ahead (a lap of it) where one burn along or against the way it goes, sized so the far
    /// side of the new orbit is at the target's height there, would arrive within
    /// <paramref name="within"/> metres of the target. Put in <paramref name="window"/>, in order.
    /// Either orbit may start later than now (its <see cref="Orbit.From"/>, a leg after a node): a
    /// burn's time from now is mine's start and its time along mine, and the target is found by
    /// then along its own. If that is before the target's orbit starts, it is found by running its
    /// orbit back (Kepler's equation runs either way), so the window doesn't depend on where along
    /// the target's path its orbit was taken from.
    /// <para>All at once, read-only inspection of the window for the tests: the game works through
    /// <see cref="TransferSteps"/> a few each frame instead (see <see cref="Paths"/>).</para>
    /// </summary>
    public static void Transfer(in Orbit mine, in Orbit theirs, float within, List<WindowPoint> window) =>
        Finish(TransferSteps(mine, theirs, within, window));

    /// <summary>
    /// <see cref="Transfer"/> a burn tried at a time: each step yields <see cref="Working"/>, and the
    /// last yields 0 with a window, or -1 with none. A plan is a few hundred tries, too many to do in
    /// one frame (see <see cref="Paths"/>, which works through them a few each frame).
    /// <paramref name="coarse"/>, if given, is a buffer of <see cref="WindowSteps"/> to work in, so
    /// a plan doesn't allocate one each time.
    /// </summary>
    public static IEnumerable<int> TransferSteps(Orbit mine, Orbit theirs, float within, List<WindowPoint> window, float[]? coarse = null, double until = double.PositiveInfinity)
    {
        window.Clear();
        if (mine.Radial || theirs.Radial)
        {
            yield return -1;
            yield break;
        }
        foreach (var step in ScanSteps(mine, anomaly => TransferMiss(mine, theirs, anomaly), within, window, mine.Now, Until(mine, mine.EndAnomaly, until), coarse ??= new float[WindowSteps]))
            yield return step;
        yield return window.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// The de-orbit window to a place on the ground at <paramref name="site"/>: every place on the
    /// path ahead where one burn against the way it goes, sized to bring its lowest point
    /// <see cref="Scene.DeorbitDepth"/> below the ground, would come down within
    /// <paramref name="within"/> metres of it. Put in <paramref name="window"/>, in order, as
    /// anomalies counted on past a lap of <paramref name="mine"/>; and the last lap ahead it reaches
    /// (0 this one, 1 the next), or -1 with no window at all. The ground turns east under the orbit
    /// only a third or so as fast as the orbit goes round, so the place comes back under it only
    /// every lap and a half: with no window this lap, the next laps are looked at, up to
    /// <see cref="Scene.DeorbitLaps"/>, and the first window found is the one. One that runs on to
    /// the end of a lap is followed on into the next, so it is whole. <paramref name="mine"/> may
    /// start later than now (its <see cref="Orbit.From"/>): the ground under a burn turns by its
    /// time from now, and the site is where it is now.
    /// <para>All at once, read-only inspection of the window for the tests: the game works through
    /// <see cref="DeorbitSteps"/> a few each frame instead (see <see cref="Paths"/>).</para>
    /// </summary>
    public static int Deorbit(in Orbit mine, Vector3 site, float within, List<WindowPoint> window) =>
        Finish(DeorbitSteps(mine, site, within, window));

    /// <summary><see cref="Deorbit"/> a burn tried at a time, as <see cref="TransferSteps"/> is: the last step yields the lap.</summary>
    public static IEnumerable<int> DeorbitSteps(Orbit mine, Vector3 site, float within, List<WindowPoint> window, float[]? coarse = null, double until = double.PositiveInfinity)
    {
        window.Clear();
        if (mine.Radial || mine.PeriapsisDistance <= mine.Body.Radius - Scene.DeorbitDepth)
        {
            yield return -1; // coming down steeper than that already: where it lands is the ground mark
            yield break;
        }
        Func<double, float> miss = anomaly => DeorbitMiss(mine, site, anomaly);
        var lap = mine;
        var start = mine.Now;
        var found = -1; // the lap a window was found on that ran on to its end
        for (var i = 0; i < Scene.DeorbitLaps; i++)
        {
            // This lap, as far as it goes: its own end, which a later lap finds over the ground as
            // it will have turned by then (so one that meets a peak a lap on stops there).
            var end = Until(mine, start + (lap.EndAnomaly - lap.Now), until);
            if (end <= start)
                break; // past where the orbit is flown: no more of it to look along
            var before = window.Count;
            foreach (var step in ScanSteps(mine, miss, within, window, start, end, coarse ??= new float[WindowSteps]))
                yield return step;
            if (found >= 0)
            {
                // Only what carries on the window found on the lap before, with no gap: anything
                // further round this lap is another window.
                var keep = before;
                while (keep < window.Count && window[keep].Anomaly - window[keep - 1].Anomaly < WindowGap)
                    keep++;
                window.RemoveRange(keep, window.Count - keep);
                yield return keep > before ? i : found;
                yield break;
            }
            if (window.Count > 0)
            {
                // Running on to within a coarse step of the lap's end, it may go on into the next.
                if (window[^1].Anomaly < end - (end - start) / WindowSteps || !lap.Closed || i + 1 >= Scene.DeorbitLaps)
                {
                    yield return i;
                    yield break;
                }
                found = i;
            }
            else if (!lap.Closed)
                break;
            (lap, start) = (lap.NextLap(), end);
        }
        yield return found;
    }

    /// <summary>
    /// <paramref name="end"/> (an anomaly on <paramref name="mine"/>), or where mine is
    /// <paramref name="until"/> seconds along if that is sooner: a window is looked for only along
    /// the orbit as far as it is flown (to where it crosses a sphere's edge).
    /// </summary>
    private static double Until(in Orbit mine, double end, double until) =>
        double.IsFinite(until) ? Math.Min(end, mine.AnomalyAfter(until)) : end;

    /// <summary>What a step of <see cref="TransferSteps"/> or <see cref="DeorbitSteps"/> yields until the last.</summary>
    public const int Working = int.MinValue;

    /// <summary>Works out all of a window's steps at once: what the last yields.</summary>
    private static int Finish(IEnumerable<int> steps)
    {
        var last = -1;
        foreach (var step in steps)
            last = step;
        return last;
    }

    /// <summary>Points of a window further apart than this (radians round the orbit) are two stretches, not one.</summary>
    public const double WindowGap = 0.15;

    /// <summary>
    /// Tries a burn at places from <paramref name="start"/> to <paramref name="end"/> on
    /// <paramref name="mine"/> (no more than a lap), then more finely either side of each that came
    /// closer than its neighbours, keeping every one that would come within <paramref name="within"/>;
    /// a step (yielding <see cref="Working"/>) for each burn tried.
    /// </summary>
    private static IEnumerable<int> ScanSteps(Orbit mine, Func<double, float> miss, float within, List<WindowPoint> window, double start, double end, float[] coarse)
    {
        end = Math.Min(end, start + 2 * Math.PI);
        var step = (end - start) / WindowSteps;
        for (var i = 0; i < WindowSteps; i++)
        {
            coarse[i] = miss(start + step * (i + 0.5));
            yield return Working;
        }

        for (var i = 0; i < WindowSteps; i++)
        {
            var before = i > 0 ? coarse[i - 1] : float.PositiveInfinity;
            var after = i < WindowSteps - 1 ? coarse[i + 1] : float.PositiveInfinity;
            if (coarse[i] <= before && coarse[i] <= after && coarse[i] < within * 4f)
            {
                // Finely, from the sample before to the one after.
                for (var j = 0; j <= FineSteps; j++)
                {
                    var anomaly = start + step * (i - 0.5 + 2.0 * j / FineSteps);
                    if (anomaly <= start || anomaly > end)
                        continue; // past either end: behind it, or on a lap the line may not reach
                    var m = miss(anomaly);
                    if (m < within)
                        window.Add(new WindowPoint(anomaly, m));
                    yield return Working;
                }
            }
            else if (coarse[i] < within)
                window.Add(new WindowPoint(start + step * (i + 0.5), coarse[i]));
        }
        window.Sort((a, b) => a.Anomaly.CompareTo(b.Anomaly));
    }

    /// <summary>How close a transfer burn at <paramref name="anomaly"/> would come to the target, when it got there (infinity if it can't).</summary>
    private static float TransferMiss(in Orbit mine, in Orbit theirs, double anomaly)
    {
        if (TransferBurn(mine, theirs, anomaly) is not { } transfer)
            return float.PositiveInfinity;
        var opposite = Vector3.Normalize(mine.Body.Centre - mine.At(anomaly));
        var arrival = transfer.AnomalyToward(opposite);
        if (arrival > transfer.EndAnomaly)
            return float.PositiveInfinity; // it would hit the ground first
        var seconds = transfer.From + transfer.TimeTo(arrival); // from now
        return Vector3.Distance(transfer.At(arrival), theirs.PositionAfter(seconds - theirs.From));
    }

    /// <summary>
    /// The path after the transfer window's burn at <paramref name="anomaly"/> on <paramref name="mine"/>
    /// (see <see cref="Transfer"/>): along or against the way it goes, sized so the far side of the
    /// new orbit is at <paramref name="theirs"/>'s height there, and starting as far from now as
    /// the burn is (mine's start, and its time along mine); null if no burn that way gets there.
    /// </summary>
    public static Orbit? TransferBurn(in Orbit mine, in Orbit theirs, double anomaly)
    {
        // The burn is that far ahead, so the ground the transfer might hit has turned that much more.
        var from = mine.From + mine.TimeTo(anomaly);
        var burnAt = mine.At(anomaly);
        var velocity = mine.VelocityAt(anomaly);
        var opposite = Vector3.Normalize(mine.Body.Centre - burnAt);
        var goal = theirs.DistanceToward(opposite);
        if (double.IsInfinity(goal))
            return null;

        // How much faster (or slower) to go there, for the far side to be at the target's height.
        var escape = MathF.Sqrt(2f * mine.Body.Mu / Vector3.Distance(burnAt, mine.Body.Centre)) / velocity.Length() * 0.999f;
        var (lo, hi) = mine.DistanceToward(opposite) < goal ? (1f, escape) : (0.05f, 1f);
        if (!(hi > lo))
            return null;
        for (var step = 0; step < SolveSteps; step++)
        {
            var mid = (lo + hi) / 2;
            if (new Orbit(mine.Body, burnAt, velocity * mid, findEnd: false).DistanceToward(opposite) < goal)
                lo = mid;
            else
                hi = mid;
        }
        var transfer = new Orbit(mine.Body, burnAt, velocity * ((lo + hi) / 2), from: from);
        return Math.Abs(transfer.DistanceToward(opposite) - goal) > Scene.TransferWithin
            ? null // no burn this way gets there
            : transfer;
    }

    /// <summary>
    /// The closest a path (<paramref name="legs"/>, patched at the spheres of influence, from
    /// <paramref name="first"/> on; at world time <paramref name="now"/>) comes to
    /// <paramref name="body"/>'s centre, of every leg round it or its parent, each only over its
    /// own span (where it is flown and drawn): on a leg round it (an encounter), its periapsis, or,
    /// where that leg leaves or comes down before one, the nearer of its ends; on a leg round its
    /// parent, the closest it comes to the body's centre going round there (<see cref="Closest"/>,
    /// against <see cref="Bodies.OrbitOf"/>). Its seconds are from now, its places in the frame
    /// of the leg it is on (<see cref="Approach.Leg"/>: its body's, among its stars as they match
    /// its ground now), and its distance from the centre. Null with none.
    /// </summary>
    public static Approach? BodyApproach(GroundBody body, IReadOnlyList<PathLeg> legs, double now, int first = 0)
    {
        Approach? best = null;
        for (var i = first; i < legs.Count; i++)
        {
            var leg = legs[i];
            var span = leg.End - leg.Start; // infinite for the last: then as far as the orbit's path goes
            var startsIn = leg.Start - now;
            Approach? found = null;
            if (leg.Body == body)
                found = Encounter(body, leg, span, startsIn, i);
            else if (leg.Body == body.Parent)
            {
                var (position, velocity) = Bodies.OrbitOf(body, now).StateAfter(startsIn);
                var theirs = new Orbit(leg.Body, position, velocity, findEnd: false, from: startsIn);
                if (Closest(leg.Orbit, theirs, default, span) is { } closest)
                    found = closest with { Seconds = startsIn + closest.Seconds, Leg = i };
            }
            if (found is { } f && (best is not { } b || f.Distance < b.Distance))
                best = f;
        }
        return best;
    }

    /// <summary>The nearest <paramref name="leg"/> (round <paramref name="body"/>, the <paramref name="index"/>th) comes to its centre within its <paramref name="span"/>: its periapsis, or the nearer of its ends.</summary>
    private static Approach Encounter(GroundBody body, in PathLeg leg, double span, double startsIn, int index)
    {
        var orbit = leg.Orbit;
        if (!orbit.Radial && orbit.NextPeriapsis is { } periapsis && orbit.TimeTo(periapsis) <= span)
        {
            var at = orbit.At(periapsis);
            return new Approach(startsIn + orbit.TimeTo(periapsis), at, body.Centre, Vector3.Distance(at, body.Centre), index);
        }
        // No periapsis on this leg: it is nearest at one end, coming in or going out.
        var end = Math.Min(span, orbit.SecondsToEnd);
        if (!double.IsFinite(end))
            end = 0;
        var (start, last) = (orbit.PositionAfter(0), orbit.PositionAfter(end));
        return Vector3.Distance(start, body.Centre) <= Vector3.Distance(last, body.Centre)
            ? new Approach(startsIn, start, body.Centre, Vector3.Distance(start, body.Centre), index)
            : new Approach(startsIn + end, last, body.Centre, Vector3.Distance(last, body.Centre), index);
    }

    /// <summary>
    /// The closest a path (<paramref name="legs"/>, from <paramref name="first"/> on, at world time
    /// <paramref name="now"/>) comes to a vessel on <paramref name="theirs"/> (its orbit round the
    /// body it is at, from now): the closest of every leg round that body, each only over its own
    /// span (<see cref="Closest"/>), its seconds from now and its places in that leg's frame
    /// (<see cref="Approach.Leg"/>). Null with no leg round that body.
    /// </summary>
    public static Approach? VesselApproach(in Orbit theirs, IReadOnlyList<PathLeg> legs, double now, int first = 0)
    {
        Approach? best = null;
        for (var i = first; i < legs.Count; i++)
        {
            var leg = legs[i];
            if (leg.Body != theirs.Body || leg.Orbit.Radial)
                continue;
            var startsIn = leg.Start - now;
            var (position, velocity) = theirs.StateAfter(startsIn);
            if (Closest(leg.Orbit, new Orbit(theirs.Body, position, velocity, from: startsIn), default, leg.End - leg.Start) is { } closest
                && (best is not { } b || closest.Distance < b.Distance))
                best = closest with { Seconds = startsIn + closest.Seconds, Leg = i };
        }
        return best;
    }

    /// <summary>
    /// How far from <paramref name="site"/> (a place on the ground, as it is now) a de-orbit burn at
    /// <paramref name="anomaly"/> would bring it down (infinity if it wouldn't): measured on the
    /// ground, from where it would land on it, the ground having turned under the orbit until the
    /// burn and then all the way down.
    /// </summary>
    private static float DeorbitMiss(in Orbit mine, Vector3 site, double anomaly)
    {
        var down = DeorbitBurn(mine, anomaly);
        return down.EndsOnGround ? Vector3.Distance(down.Landing, site) : float.PositiveInfinity;
    }

    /// <summary>
    /// The path after the de-orbit window's burn at <paramref name="anomaly"/> on <paramref name="mine"/>:
    /// against the way it goes, slowed until its lowest point is <see cref="Scene.DeorbitDepth"/>
    /// below the ground, and starting as far from now as the burn is (mine's start, and its time
    /// along mine).
    /// </summary>
    public static Orbit DeorbitBurn(in Orbit mine, double anomaly)
    {
        var burnAt = mine.At(anomaly);
        var velocity = mine.VelocityAt(anomaly);
        var goal = mine.Body.Radius - Scene.DeorbitDepth;
        float lo = 0.001f, hi = 1f;
        for (var step = 0; step < SolveSteps; step++)
        {
            var mid = (lo + hi) / 2;
            if (new Orbit(mine.Body, burnAt, velocity * mid, findEnd: false).PeriapsisDistance < goal)
                lo = mid;
            else
                hi = mid;
        }
        return new Orbit(mine.Body, burnAt, velocity * ((lo + hi) / 2), from: mine.From + mine.TimeTo(anomaly));
    }
}
