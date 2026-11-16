using System.Diagnostics;
using System.Numerics;

namespace GameOne;

/// <summary>
/// A vessel as the map's collision search is handed it: the body it is at (in whose frame the rest is; two at different bodies are never paired), its middle, how it moves among the stars,
/// its size (<see cref="Collisions.Size"/>), the closing speed it is wrecked over
/// (<see cref="Vessel.ImpactTolerance"/>), and whether it is on the ground (landed, afloat or on
/// the pad: held where it is on the turning ground, not followed along an orbit).
/// </summary>
internal readonly record struct SearchVessel(int Id, GroundBody Body, Vector3 Position, Vector3 StarsVelocity, float Size,
    float ImpactTolerance = Scene.SafeTouchdownSpeed, bool OnGround = false);

/// <summary>
/// A collision the map shows: vessels <paramref name="A"/> and <paramref name="B"/> (their ids)
/// within their sizes of each other at world time <paramref name="At"/>, as fast apart as
/// <paramref name="ClosingSpeed"/> there, between them at <paramref name="Where"/> (among the stars,
/// in the ground's frame as it was when the pass was asked for). <paramref name="SlotA"/> and
/// <paramref name="SlotB"/> are where the two were in the vessels the pass was handed, so the
/// flight finds them without a search.
/// </summary>
internal readonly record struct PredictedCollision(int A, int B, double At, float ClosingSpeed, Vector3 Where, int SlotA = -1, int SlotB = -1);

/// <summary>
/// Looks for the collisions between any two vessels in the world over the time ahead, each along
/// the orbit of its present state (<see cref="Orbit"/>: Kepler, coasting), on a thread of its own,
/// for the map (<see cref="Paths"/>). Only information: a pass is never waited for, nothing in the
/// flight depends on what it finds, and a vessel that burns is searched on its new orbit by the
/// next pass. The flight hands it the world as it is (<see cref="TryAsk"/>) and takes what the last
/// pass found (<see cref="TryTake"/>), as <see cref="Physics.TileWorker"/> does.
/// </summary>
/// <remarks>
/// <para>
/// A pass looks at every pair, but most are ruled out cheaply first: by the heights each orbit
/// spans (its periapsis to its apoapsis), which must overlap within their sizes; then, for orbits
/// in planes apart, by the line where the planes cross (their line of nodes): within their sizes
/// of each other, each must be within its size of the other's plane, so only on a short arc round
/// either node, and both must be on that arc at once. The heights there must agree too. What is
/// left (both on the arc together) is sampled and narrowed down as <see cref="Encounters.Closest"/>
/// does. Orbits in one plane (copies of one orbit, or near enough) have no line of nodes: there
/// the angle between the two round the plane is followed, stepping as far as it can't close in
/// (its fastest change is known from each orbit's slowest and fastest turning), so two on the
/// same orbit, which never close at all, are passed over in a step. One on the ground (landed,
/// afloat or on the pad) is held where it is as the planet turns: against one on an orbit, the
/// angle between their directions from the centre is followed the same way (the ground's turn
/// added to the orbit's in how fast it can close); two on the ground never collide. A pass over a thousand
/// satellites (the satellites preset copied round, an hour ahead: <c>CollisionSearchTests</c>)
/// takes about a second in a Debug build, a third of one in Release.
/// </para>
/// <para>
/// Each pair's earliest collision is kept, then, soonest first, a collision after either vessel's
/// earlier one that would wreck it (closing faster than its own <see cref="Vessel.ImpactTolerance"/>) is
/// dropped, as it would never come. A pair within its sizes of each other now (parting, or one
/// resting on the other) has no collision to predict. A path is followed only until it comes down
/// to its body's lowest surface (the sea's level, or with no sea its lowest ground:
/// <see cref="GroundBody.LowestRadius"/>; the ground the search doesn't sample, as the terrain is
/// the main thread's): one in the air may show a collision a little underground, which the map
/// then shows as it is.
/// </para>
/// <para>
/// Everything but <see cref="Work"/> runs on the main thread. The two share only what was asked,
/// what was found and the time a pass took, under one lock that the main thread only tries
/// (<see cref="Monitor.TryEnter(object)"/>), so it never waits; nor does the search touch
/// <see cref="Timings"/> (not thread-safe): it times its own pass. It runs below normal
/// priority, as a background thread, started on the first pass asked for (a flight whose map is
/// never drawn never has one), and waits on its lock's signal with nothing to do.
/// </para>
/// </remarks>
internal sealed class CollisionSearch : IDisposable
{
    private readonly object _lock = new();
    private readonly Thread _thread;
    // Shared, under the lock: the pass asked for (copied in, so the flight's buffer is its own
    // again at once), and what the last pass found, waiting to be taken.
    private SearchVessel[] _asked = new SearchVessel[Flight.Capacity];
    private int _askedCount;
    private double _askedNow, _askedHorizon;
    private bool _pending, _fresh, _started, _stopping;
    private List<PredictedCollision> _done = [];
    private Exception? _failed;
    private double _lastPassSeconds = double.NaN;

    // The search's own (its thread's alone).
    private SearchVessel[] _work = new SearchVessel[Flight.Capacity];
    private Course[] _paths = new Course[Flight.Capacity];
    private double[] _wreckedAt = new double[Flight.Capacity];
    private List<PredictedCollision> _found = [];
    private readonly List<(int I, int J, double Seconds, float Speed, Vector3 Where)> _pairs = [];
    private static readonly Comparison<(int I, int J, double Seconds, float Speed, Vector3 Where)> Sooner = (a, b) => a.Seconds.CompareTo(b.Seconds);

    public CollisionSearch() =>
        _thread = new Thread(Work) { Name = "Map collisions", IsBackground = true, Priority = ThreadPriority.BelowNormal };

    /// <summary>How long the last pass took, in real seconds (NaN before the first).</summary>
    public double LastPassSeconds => Volatile.Read(ref _lastPassSeconds);

    /// <summary>Whether its thread is running (started by a pass asked for, until <see cref="Dispose"/>).</summary>
    internal bool Running => _thread.IsAlive;

    /// <summary>
    /// Asks for a pass over <paramref name="vessels"/> as they are at world time
    /// <paramref name="now"/>, <paramref name="horizon"/> seconds ahead: copied in, so the span is
    /// the caller's again at once. A pass asked for and not yet started is replaced by this one.
    /// Returns false, having asked nothing, if the search holds the lock just now: ask again later.
    /// </summary>
    public bool TryAsk(ReadOnlySpan<SearchVessel> vessels, double now, double horizon)
    {
        if (!Monitor.TryEnter(_lock))
            return false;
        try
        {
            if (_stopping || _failed is not null)
                return true;
            if (_asked.Length < vessels.Length)
                _asked = new SearchVessel[vessels.Length]; // a world past its capacity for a moment (staging): rare
            vessels.CopyTo(_asked);
            (_askedCount, _askedNow, _askedHorizon, _pending) = (vessels.Length, now, horizon, true);
            if (!_started)
            {
                _started = true;
                _thread.Start();
            }
            Monitor.Pulse(_lock);
            return true;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>
    /// Takes what the last pass found into <paramref name="into"/> (emptied first), soonest first,
    /// if a pass has finished since the last taken. Throws, here on the main thread, an exception
    /// the search hit (it searches no more after it). Returns false, having taken nothing, if no
    /// pass has finished since, or if the search holds the lock just now.
    /// </summary>
    public bool TryTake(List<PredictedCollision> into)
    {
        if (!Monitor.TryEnter(_lock))
            return false;
        try
        {
            if (_failed is { } failed)
                throw new InvalidOperationException($"The map's collision search failed: {failed.Message}", failed);
            if (!_fresh)
                return false;
            into.Clear();
            into.AddRange(_done);
            _fresh = false;
            return true;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private void Work()
    {
        while (true)
        {
            int count;
            double now, horizon;
            lock (_lock)
            {
                while (!_stopping && !_pending)
                    Monitor.Wait(_lock);
                if (_stopping)
                    return;
                count = _askedCount;
                if (_work.Length < count)
                    (_work, _paths, _wreckedAt) = (new SearchVessel[count], new Course[count], new double[count]);
                Array.Copy(_asked, _work, count);
                (now, horizon, _pending) = (_askedNow, _askedHorizon, false);
            }
            try
            {
                var clock = Stopwatch.StartNew();
                Search(count, now, horizon);
                var seconds = clock.Elapsed.TotalSeconds;
                lock (_lock)
                {
                    (_done, _found) = (_found, _done);
                    _fresh = true;
                    Volatile.Write(ref _lastPassSeconds, seconds);
                }
            }
            catch (Exception e)
            {
                lock (_lock)
                {
                    _failed = e;
                    return;
                }
            }
        }
    }

    /// <summary>Stops the search and waits for its thread to end (a pass in hand is given up at its next vessel).</summary>
    public void Dispose()
    {
        bool started;
        lock (_lock)
        {
            if (_stopping)
                return;
            (_stopping, started) = (true, _started);
            Monitor.PulseAll(_lock);
        }
        if (started)
            _thread.Join();
    }

    // ---- The search (its thread's) ----

    /// <summary>
    /// A vessel's path as the search follows it: its orbit, its heights' span, its plane, how fast
    /// it turns round it at its slowest and fastest, and how far ahead it is followed (to the
    /// horizon, or until it is down to its body's lowest surface, <see cref="GroundBody.LowestRadius"/>).
    /// Not <see cref="Ok"/>: not followed at all (straight up or down, which has no orbit to speak
    /// of, or under that already).
    /// </summary>
    private struct Course
    {
        public GroundBody Body; // what it goes round (or stands on), in whose frame it is
        public Orbit Orbit;
        public bool Ok;
        public double Periapsis, Apoapsis, End, SlowestTurn, FastestTurn;
        public Vector3 Normal;
        /// <summary>On the ground: <see cref="Point"/> carried round by its body's turn, its heights' span the one height.</summary>
        public bool Fixed;
        public Vector3 Point;
    }

    /// <summary>Where <paramref name="c"/> is <paramref name="t"/> seconds from now, among the stars (the ground's frame as it is now).</summary>
    private static Vector3 At(in Course c, double t) => c.Fixed ? c.Body.Turned(c.Point, t) : c.Orbit.PositionAfter(t);

    /// <summary>Where <paramref name="c"/> is <paramref name="t"/> seconds from now and how it moves, among the stars.</summary>
    private static (Vector3 Position, Vector3 Velocity) State(in Course c, double t)
    {
        if (!c.Fixed)
            return c.Orbit.StateAfter(t);
        var at = c.Body.Turned(c.Point, t);
        return (at, c.Body.GroundSpin(at));
    }

    /// <summary>Seconds apart a stretch's samples are, at most, before it is narrowed down: two closing at 8,000 m/s are 16 km apart a sample, and the closest is between the samples either side.</summary>
    private const double SampleSeconds = 2.0;

    /// <summary>
    /// Orbits whose planes are this close (one no further out of the other's, at its lowest, than
    /// this many times their sizes: a few metres) are taken as in one plane: the arcs round their
    /// nodes would be 30° either side or more, and nearly in one plane the line of nodes itself
    /// is ill-found, where the angle between them round the plane needs neither.
    /// </summary>
    private const double OnePlane = 2.0;

    private void Search(int count, double now, double horizon)
    {
        _found.Clear();
        _pairs.Clear();
        for (var i = 0; i < count; i++)
            _paths[i] = Prepare(_work[i], horizon);
        for (var i = 0; i < count; i++)
        {
            if (Volatile.Read(ref _stopping))
                return; // closing: Dispose waits for this, so it gives up the pass at once
            ref readonly var a = ref _paths[i];
            if (!a.Ok)
                continue;
            for (var j = i + 1; j < count; j++)
            {
                ref readonly var b = ref _paths[j];
                if (!b.Ok || a.Body != b.Body)
                    continue; // vessels at different bodies never meet
                var reach = _work[i].Size + _work[j].Size;
                // Their heights must overlap within their sizes, or they never come that close.
                if (a.Periapsis > b.Apoapsis + reach || b.Periapsis > a.Apoapsis + reach)
                    continue;
                // Within their sizes now: parting, or one on the other. Nothing to predict.
                if (Vector3.DistanceSquared(_work[i].Position, _work[j].Position) < reach * reach)
                    continue;
                if (Earliest(a, b, reach) is { } hit)
                    _pairs.Add((i, j, hit.Seconds, hit.Speed, hit.Where));
            }
        }

        // Soonest first, each vessel's collisions after the one that would wreck it dropped.
        _pairs.Sort(Sooner);
        for (var i = 0; i < count; i++)
            _wreckedAt[i] = double.PositiveInfinity;
        foreach (var (i, j, seconds, speed, where) in _pairs)
        {
            if (seconds > _wreckedAt[i] || seconds > _wreckedAt[j])
                continue;
            _found.Add(new PredictedCollision(_work[i].Id, _work[j].Id, now + seconds, speed, where, i, j));
            // Each judged by its own tolerance, as the physics judges a hit.
            if (speed > _work[i].ImpactTolerance)
                _wreckedAt[i] = Math.Min(_wreckedAt[i], seconds);
            if (speed > _work[j].ImpactTolerance)
                _wreckedAt[j] = Math.Min(_wreckedAt[j], seconds);
        }
    }

    private static Course Prepare(in SearchVessel v, double horizon)
    {
        if (v.OnGround)
        {
            var height = (double)Vector3.Distance(v.Position, v.Body.Centre);
            return new Course { Body = v.Body, Ok = true, Fixed = true, Point = v.Position, Periapsis = height, Apoapsis = height, End = horizon };
        }
        var orbit = new Orbit(v.Body, v.Position, v.StarsVelocity, findEnd: false);
        var lowestSurface = (double)v.Body.LowestRadius; // the sea's level, or with no sea its lowest ground (the moon's plains and craters lie below its datum)
        if (orbit.Radial || Vector3.Distance(v.Position, v.Body.Centre) < lowestSurface)
            return default;
        double p = orbit.SemiLatusRectum, e = orbit.Eccentricity;
        var path = new Course
        {
            Body = v.Body,
            Orbit = orbit,
            Ok = true,
            Periapsis = orbit.PeriapsisDistance,
            Apoapsis = orbit.ApoapsisDistance,
            End = horizon,
            Normal = Vector3.Normalize(orbit.Normal),
        };
        // Followed only until it is down to its body's lowest surface, if it comes down.
        if (path.Periapsis < lowestSurface && e > 1e-9)
        {
            var down = -Math.Acos(Math.Clamp((p / lowestSurface - 1) / e, -1, 1));
            if (e < 1)
                while (down <= orbit.Now)
                    down += Math.Tau;
            if (down > orbit.Now)
                path.End = Math.Min(path.End, orbit.TimeTo(down));
        }
        // How fast it turns round its plane (its angular momentum over its distance squared), at
        // its fastest where it is followed: at its periapsis, or its body's lowest surface if it
        // comes down there first (a periapsis deep in the body would bound it many times too fast).
        var momentum = Math.Sqrt(v.Body.Mu * p);
        var lowest = Math.Max(path.Periapsis, lowestSurface);
        path.FastestTurn = momentum / (lowest * lowest);
        path.SlowestTurn = e < 1 ? momentum / (path.Apoapsis * path.Apoapsis) : 0;
        path.Ok = path.End > 0;
        return path;
    }

    /// <summary>The first time ahead <paramref name="a"/> and <paramref name="b"/> are within <paramref name="reach"/> of each other, and how fast they close and where, if they are.</summary>
    private static (double Seconds, float Speed, Vector3 Where)? Earliest(in Course a, in Course b, float reach)
    {
        double? found;
        if (a.Fixed && b.Fixed)
            return null; // both held on the turning ground: they never move nearer
        else if (a.Fixed || b.Fixed)
            found = a.Fixed ? OverGround(b, a, reach) : OverGround(a, b, reach);
        else
        {
            var across = Vector3.Cross(a.Normal, b.Normal);
            var sine = across.Length();
            var lowest = Math.Min(a.Periapsis, b.Periapsis);
            found = sine * lowest < OnePlane * reach
                ? InOnePlane(a, b, reach)
                : AtNodes(a, b, reach, Vector3.Normalize(across), sine);
        }
        if (found is not { } seconds)
            return null;
        var (atA, movingA) = State(a, seconds);
        var (atB, movingB) = State(b, seconds);
        return (seconds, Vector3.Distance(movingA, movingB), (atA + atB) / 2);
    }

    /// <summary>
    /// For one on an orbit (<paramref name="moving"/>) and one held on the turning ground
    /// (<paramref name="held"/>, whose height its orbit spans, or they would have been ruled out):
    /// the first collision, followed by the angle between their directions from the centre, as
    /// <see cref="InOnePlane"/> follows its angle. Within their sizes of each other they are
    /// within that much of one direction; until then the angle can't close faster than the orbit
    /// turns at its fastest and the ground at its own rate together, so it steps on as far as
    /// that couldn't close it. Near the same direction, the stretch round it is narrowed down.
    /// </summary>
    private static double? OverGround(in Course moving, in Course held, float reach)
    {
        var end = moving.End;
        var closing = (moving.FastestTurn + moving.Body.SpinRate) * 1.05 + 1e-12;
        // They hit at the held one's height, so the orbit's periapsis counts only down to its
        // body's lowest surface, as its fastest turn does: one deep in the body would widen the
        // angle many times over, and narrow down every step near it for nothing.
        var within = 1.05 * reach / Math.Min(Math.Max(moving.Periapsis, moving.Body.LowestRadius), held.Periapsis);
        var t = 0.0;
        for (var steps = 0; t <= end && steps < 100_000; steps++)
        {
            var (atA, movingA) = State(moving, t);
            var (atB, movingB) = State(held, t);
            var (toA, toB) = (atA - moving.Body.Centre, atB - moving.Body.Centre);
            var apart = Math.Atan2(Vector3.Cross(toA, toB).Length(), Vector3.Dot(toA, toB));
            if (apart > 1.5 * within)
            {
                t += (apart - within) / closing; // at least half the reach's angle on: never creeping
                continue;
            }
            // Near the same direction: the stretch it takes them to pass, from how fast they part now.
            var rate = (movingA - movingB).Length() / toA.Length();
            var half = Math.Clamp(3 * within / Math.Max(rate, 1e-12), 0.05, 600);
            if (Closest(moving, held, Math.Max(0, t - half), Math.Min(end, t + half)) is var (seconds, distance) && distance < reach)
                return seconds;
            t += half;
        }
        return null;
    }

    /// <summary>
    /// For orbits in planes apart: the first collision round either node (<paramref name="node"/>
    /// and its opposite), where both are on the arc round it within their sizes of the other's
    /// plane together, and their heights there agree within their sizes and how much each
    /// changes over its arc.
    /// </summary>
    private static double? AtNodes(in Course a, in Course b, float reach, Vector3 node, float sine)
    {
        double? first = null;
        for (var side = 0; side < 2; side++)
        {
            var d = side == 0 ? node : -node;
            var (fromA, toA, heightA, slackA) = Arc(a, d, reach, sine);
            var (fromB, toB, heightB, slackB) = Arc(b, d, reach, sine);
            if (double.IsNaN(fromA) || double.IsNaN(fromB))
                continue; // one never goes that way (escaping, the other way)
            if (Math.Abs(heightA - heightB) > reach + slackA + slackB)
                continue;
            var passesA = new Passes(a, fromA, toA);
            var passesB = new Passes(b, fromB, toB);
            var gotA = passesA.Next(out var startA, out var endA);
            var gotB = passesB.Next(out var startB, out var endB);
            // Both on the arc at once: their passes overlap (walked together, soonest first).
            while (gotA && gotB && (first is null || Math.Min(startA, startB) < first))
            {
                var (from, to) = (Math.Max(startA, startB), Math.Min(endA, endB));
                if (from <= to && Closest(a, b, from, to) is var (seconds, apart) && apart < reach)
                {
                    first = first is { } f ? Math.Min(f, seconds) : seconds;
                    break;
                }
                if (endA < endB)
                    gotA = passesA.Next(out startA, out endA);
                else
                    gotB = passesB.Next(out startB, out endB);
            }
        }
        return first;
    }

    /// <summary>
    /// The arc of <paramref name="path"/>'s orbit round <paramref name="d"/> (a direction in its
    /// plane) where it can be within <paramref name="reach"/> of a plane at <paramref name="sine"/>
    /// to its own through the same line: from and to (true anomalies), and how much its height
    /// can change over it, and its height at <paramref name="d"/>. NaN if it never goes that way.
    /// </summary>
    private static (double From, double To, double Height, double Slack) Arc(in Course path, Vector3 d, float reach, float sine)
    {
        ref readonly var orbit = ref path.Orbit;
        var at = orbit.AngleToward(d);
        // Out of the other's plane by its height times the sine of the angle from the node times the planes' sine.
        var half = Math.Asin(Math.Min(1.0, reach / (path.Periapsis * sine)));
        double e = orbit.Eccentricity, p = orbit.SemiLatusRectum;
        if (e >= 1)
        {
            var asymptote = Math.Acos(-1 / e);
            if (Math.Abs(at) >= asymptote)
                return (double.NaN, double.NaN, double.NaN, 0);
        }
        // Its height's change over the arc: its rate there (r² e sin θ / p) over the arc, and a
        // margin for the rate's own change; over a wide arc (planes nearly one), not worth bounding.
        var r = orbit.Distance(at);
        var slack = half < 0.05 ? Math.Abs(r * r * e * Math.Sin(at) / p) * half + 2 * r * e * half * half + 0.01 : double.PositiveInfinity;
        return (at - half, at + half, r, slack);
    }

    /// <summary>
    /// The times a path is on an arc of its orbit (true anomalies <c>from</c> to <c>to</c>), each
    /// pass in turn from now to its end: a lap apart round an ellipse, once on an escape.
    /// </summary>
    private struct Passes
    {
        // The first pass's start and end (seconds from now: its start before now, for an arc it is
        // on already), each pass after a lap later: Kepler's equation solved twice, not every lap.
        private readonly double _start, _end, _period, _until;
        private readonly bool _once;
        private int _lap;
        private bool _done;

        public Passes(in Course path, double from, double to)
        {
            ref readonly var orbit = ref path.Orbit;
            var e = orbit.Eccentricity;
            var now = orbit.Now;
            _until = path.End;
            if (e >= 1)
            {
                // One branch only, from now to the asymptote.
                var asymptote = Math.Acos(-1 / e) - 1e-6;
                (from, to, _once, _period) = (Math.Max(from, -asymptote), Math.Min(to, asymptote), true, double.PositiveInfinity);
                _done = to <= now || to <= from;
                if (_done)
                    return;
            }
            else
            {
                // The first lap whose arc isn't all behind.
                var shift = Math.Ceiling((now - to) / Math.Tau) * Math.Tau;
                (from, to, _once, _period) = (from + shift, to + shift, false, orbit.Period);
            }
            // A start behind now (on the arc already) is before now: Next starts it now. A short arc
            // (round a node of planes well apart, the most common by far) is crossed at its middle's
            // rate (its angular momentum over its distance squared there), a hundredth wider and a
            // millisecond either side, as the rate changes over it by far less: Kepler's equation
            // solved once, not twice.
            var middle = (from + to) / 2;
            var half = (to - from) / 2;
            if (half < 0.05)
            {
                var r = orbit.Distance(middle);
                var across = half * r * r / Math.Sqrt(orbit.Body.Mu * orbit.SemiLatusRectum) * 1.01 + 1e-3;
                var at = orbit.TimeTo(middle);
                (_start, _end) = (at - across, at + across);
            }
            else
                (_start, _end) = (orbit.TimeTo(from), orbit.TimeTo(to));
        }

        /// <summary>The next pass's start and end, seconds from now, if there is one before the end.</summary>
        public bool Next(out double start, out double end)
        {
            (start, end) = (0, 0);
            if (_done)
                return false;
            var shift = _lap == 0 ? 0 : _lap * _period;
            start = Math.Max(0, _start + shift);
            if (start > _until)
            {
                _done = true;
                return false;
            }
            end = Math.Min(_end + shift, _until);
            _lap++;
            _done = _once;
            return true;
        }
    }

    /// <summary>
    /// For orbits in one plane (or near enough): the first collision, followed by the angle between
    /// the two round <paramref name="a"/>'s plane. Within their sizes of each other, they are within
    /// that much of the same direction from the centre; until then, the angle can't close faster
    /// than the two turn at their most apart (each orbit's slowest and fastest turning, the other's
    /// the other way round if it goes round backwards), so it steps on as far as that couldn't
    /// close it. Near the same direction, the stretch round it is narrowed down.
    /// </summary>
    private static double? InOnePlane(in Course a, in Course b, float reach)
    {
        var end = Math.Min(a.End, b.End);
        var same = Vector3.Dot(a.Normal, b.Normal) > 0;
        var closing = (same ? Math.Max(a.FastestTurn - b.SlowestTurn, b.FastestTurn - a.SlowestTurn) : a.FastestTurn + b.FastestTurn) * 1.05 + 1e-12;
        var within = 1.05 * reach / Math.Min(a.Periapsis, b.Periapsis); // radians: the same direction, within their sizes
        var (orbitA, orbitB) = (a.Orbit, b.Orbit);
        var centre = a.Body.Centre;
        var x = Vector3.Normalize(orbitA.PositionAfter(0) - centre);
        var y = Vector3.Cross(a.Normal, x);
        double Angle(Vector3 at) => Math.Atan2(Vector3.Dot(at - centre, y), Vector3.Dot(at - centre, x));
        double Apart(double t)
        {
            var gap = Math.Abs(Angle(orbitA.PositionAfter(t)) - Angle(orbitB.PositionAfter(t)));
            return gap > Math.PI ? Math.Tau - gap : gap;
        }

        // Two on one orbit (or as good as) are as far apart round it all the while: passed over at once.
        if (Apart(0) - closing * end > within)
            return null;
        var t = 0.0;
        for (var steps = 0; t <= end && steps < 100_000; steps++)
        {
            var apart = Apart(t);
            if (apart > 1.5 * within)
            {
                t += (apart - within) / closing; // at least half the reach's angle on: never creeping
                continue;
            }
            // Near the same direction: the stretch it takes them to pass, from how fast the angle closes now.
            var (atA, movingA) = a.Orbit.StateAfter(t);
            var (atB, movingB) = b.Orbit.StateAfter(t);
            var rate = Math.Abs(Turning(a.Body, atA, movingA, a.Normal) - Turning(a.Body, atB, movingB, a.Normal));
            var half = Math.Clamp(3 * within / Math.Max(rate, 1e-12), 0.05, 600);
            if (Closest(a, b, Math.Max(0, t - half), Math.Min(end, t + half)) is var (seconds, distance) && distance < reach)
                return seconds;
            t += half;
        }
        return null;
    }

    /// <summary>How fast something at <paramref name="at"/> moving at <paramref name="moving"/> turns round the planet's centre in the plane of <paramref name="normal"/> (radians a second, the plane's way positive).</summary>
    private static double Turning(GroundBody body, Vector3 at, Vector3 moving, Vector3 normal)
    {
        var r = at - body.Centre;
        return Vector3.Dot(Vector3.Cross(r, moving), normal) / r.LengthSquared();
    }

    /// <summary>
    /// The closest <paramref name="a"/> and <paramref name="b"/> come from <paramref name="from"/>
    /// to <paramref name="to"/> seconds from now, and when: sampled (no more than
    /// <see cref="SampleSeconds"/> apart), then narrowed down between the samples either side of
    /// the closest, as <see cref="Encounters.Closest"/> does. Close by, two coasting move apart
    /// all but in straight lines (the same pull on both), so where their straight lines come
    /// closest is found first (a few steps of it, each from their states then), which hits the
    /// time in two or three tries where a golden-section search takes twenty; one that doesn't
    /// settle (slow, on curving paths) is narrowed down by the golden section instead.
    /// </summary>
    private static (double Seconds, float Distance) Closest(in Course a, in Course b, double from, double to)
    {
        var span = to - from;
        double lo = from, hi = to, bestAt = (from + to) / 2;
        var bestDistance = float.MaxValue;
        if (span > SampleSeconds)
        {
            // Sampled, unless it is all within a sample's spacing (a pass round a node, most often).
            var samples = (int)Math.Ceiling(span / SampleSeconds);
            var best = 0;
            for (var i = 0; i <= samples; i++)
            {
                var d = Apart(a, b, from + span * i / samples);
                if (d < bestDistance)
                    (best, bestDistance) = (i, d);
            }
            (lo, hi, bestAt) = (from + span * Math.Max(0, best - 1) / samples, from + span * Math.Min(samples, best + 1) / samples, from + span * best / samples);
        }

        var t = bestAt;
        for (var i = 0; i < 5; i++)
        {
            var (atA, movingA) = State(a, t);
            var (atB, movingB) = State(b, t);
            var (gap, closing) = (atB - atA, movingB - movingA);
            var distance = gap.Length();
            if (distance < bestDistance)
                (bestAt, bestDistance) = (t, distance);
            var speed2 = closing.LengthSquared();
            if (speed2 < 1e-6f)
                break;
            var next = Math.Clamp(t - Vector3.Dot(gap, closing) / speed2, lo, hi);
            if (Math.Abs(next - t) < 1e-3)
                return (bestAt, bestDistance); // where it would go next is within a millisecond: as good
            t = next;
        }

        const double golden = 0.6180339887;
        for (var i = 0; i < 40 && hi - lo > 1e-3; i++)
        {
            double l = hi - golden * (hi - lo), r = lo + golden * (hi - lo);
            if (Apart(a, b, l) < Apart(a, b, r))
                hi = r;
            else
                lo = l;
        }
        t = (lo + hi) / 2;
        var end = Apart(a, b, t);
        return end < bestDistance ? (t, end) : (bestAt, bestDistance);
    }

    /// <summary>How far apart the two are <paramref name="t"/> seconds from now.</summary>
    private static float Apart(in Course a, in Course b, double t) => Vector3.Distance(At(a, t), At(b, t));
}
