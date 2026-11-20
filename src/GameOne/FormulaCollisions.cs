using System.Numerics;

namespace GameOne;

/// <summary>
/// The collisions between vessels under formula over a window of a frame at a formula's rate
/// (<see cref="Flight.FormulaStep"/>, up to <see cref="Scene.FormulaWindowSeconds"/>), found by
/// looking back along each vessel's way across it once every vessel has been moved to its end:
/// at the physics' rates <see cref="Unwatched.FindCollisions"/> looks ahead a quarter second a
/// step, which over minutes a frame would be some thousand looks.
/// </summary>
/// <remarks>
/// <para>Each vessel's way across the window is the cubic Hermite through its place and velocity
/// at the window's two ends (<see cref="Begin"/>, then <see cref="Judge"/>), among the stars as its
/// body's ground was at the window's start: good to a few centimetres over a window of orbit,
/// whatever moved it there (Kepler, or the formula's steps), and for a burn whose thrust is the
/// same across the window (a cubic holds a steady push's way exactly): its throttle is set as a
/// window begins, and a window is cut where any burn runs out of fuel (Flight's NextCut), so no
/// way across one bends inside it. Each is boxed (its two ends, padded by
/// its reach and the most its pull bows its way off the chord, a T²/8), the boxes sorted along X
/// (sweep and prune), and for each overlapping pair at one body, not both held where they stand,
/// the closest approach of their two ways: golden-section on the gap's square, seeded at the
/// chord's closest, then Newton on the gap's rate. Where that comes within their reach, their
/// bodies are solved as at the physics' rates (<see cref="FormulaContact.Touch"/>) over the
/// <see cref="Scene.FormulaCollisionSeconds"/> round it (and back to where their reaches first
/// meet and on to where they part, if further, so a slow touch, begun long before the middles are
/// closest, is found where it begins), from each one's Hermite state at that stretch's start.</para>
/// <para>A vessel whose motion was remade inside the window (it landed or touched, was wrecked,
/// crossed into another body's sphere, or wasn't there as it began: one staged off) is left out of
/// that window's pairs: its way across it isn't one curve. Each vessel's earliest collision is
/// judged, soonest first, as <see cref="Unwatched"/> judges one (<see cref="FormulaContact.Wreck"/>),
/// at its place then. Nothing here is witnessed, so nothing here slows the game or drops the warp.</para>
/// <para>Allocation-free: sized for a full world (<see cref="Flight.Capacity"/>), grown only past it.</para>
/// </remarks>
internal sealed class FormulaCollisions(Flight flight)
{
    private readonly Flight _flight = flight;

    /// <summary>For the tests: how many collisions have been judged, in all the windows so far.</summary>
    internal int Judged { get; private set; }

    // Each vessel under formula as the window began (by its slot): which, at which body, in what
    // state, held where it stands, standing burning; its centre of mass and velocity among the
    // stars (as the ground was then), and its pull. Then as it ended, in the same frame: its
    // centre of mass and velocity, its pull, its middle from its centre of mass and its body (its
    // axis's two ends from its middle, and how wide), its reach, its box, and whether it is in the
    // window's pairs and has hit one already in it.
    private int _count;
    private double _t0;
    private Vessel?[] _vessel = new Vessel?[Flight.Capacity + 1];
    private GroundBody?[] _body = new GroundBody?[Flight.Capacity + 1];
    private FlightState[] _state = new FlightState[Flight.Capacity + 1];
    private bool[] _held = new bool[Flight.Capacity + 1];
    private bool[] _standing = new bool[Flight.Capacity + 1];
    private Vector3Double[] _at0 = new Vector3Double[Flight.Capacity + 1];
    private Vector3Double[] _moving0 = new Vector3Double[Flight.Capacity + 1];
    private Vector3[] _pull0 = new Vector3[Flight.Capacity + 1];
    private Vector3Double[] _at1 = new Vector3Double[Flight.Capacity + 1];
    private Vector3Double[] _moving1 = new Vector3Double[Flight.Capacity + 1];
    private Vector3[] _pull1 = new Vector3[Flight.Capacity + 1];
    private Vector3[] _middle = new Vector3[Flight.Capacity + 1];
    private Vector3[] _bottom = new Vector3[Flight.Capacity + 1];
    private Vector3[] _top = new Vector3[Flight.Capacity + 1];
    private float[] _width = new float[Flight.Capacity + 1];
    private float[] _size = new float[Flight.Capacity + 1];
    private Vector3[] _boxLow = new Vector3[Flight.Capacity + 1];
    private Vector3[] _boxHigh = new Vector3[Flight.Capacity + 1];
    private bool[] _met = new bool[Flight.Capacity + 1];
    // The sweep: each box's low X, sorted, with its slot beside it.
    private float[] _sweepKeys = new float[Flight.Capacity + 1];
    private int[] _sweepSlots = new int[Flight.Capacity + 1];

    /// <summary>A collision found in the window: its two slots, when (world time) their bodies begin to touch, and how fast they close then.</summary>
    private struct Found
    {
        public int A, B;
        public double At;
        public float Closing;
    }

    private Found[] _found = new Found[Flight.Capacity / 2 + 1];
    private int _foundCount;

    /// <summary>Golden-section's steps on a pair's closest approach, and Newton's after them (see <see cref="Closest"/>).</summary>
    private const int GoldenSteps = 8, NewtonSteps = 3;

    private static readonly double Golden = (Math.Sqrt(5) - 1) / 2;

    /// <summary>
    /// As a window begins at world time <paramref name="t0"/>: each vessel under formula, not
    /// wrecked, where it is (its place and velocity in double) and its pull, among the stars as
    /// its body's ground is now.
    /// </summary>
    public void Begin(double t0)
    {
        var started = Timings.Start();
        var world = _flight.WorldVessels;
        Fit(world.Count);
        _t0 = t0;
        var n = 0;
        for (var i = 0; i < world.Count; i++)
        {
            var v = world[i];
            if (!v.OnFormula || v.State == FlightState.Crashed)
                continue;
            var held = Collisions.Held(v);
            (_vessel[n], _body[n], _state[n], _held[n], _standing[n]) = (v, v.Body, v.State, held, v.Motion.Standing);
            (_at0[n], _moving0[n]) = (v.TrueCentreOfMass, v.TrueVelocity + (Vector3Double)v.Body.GroundSpin(v.CentreOfMass));
            _pull0[n] = Pull(v, held);
            n++;
        }
        _count = n;
        Timings.Stop(StepDetail.FormulaCollisions, started);
    }

    /// <summary>
    /// The window from <paramref name="t0"/> to <paramref name="t1"/> (world time), every vessel
    /// moved to its end: its collisions found along each vessel's way across it and judged, each
    /// vessel's earliest, soonest first (see the remarks).
    /// </summary>
    public void Judge(double t0, double t1)
    {
        var started = Timings.Start();
        var span = t1 - t0;
        if (span > 0 && t0 == _t0 && _count > 1)
            Find(span);
        _count = 0;
        Timings.Stop(StepDetail.FormulaCollisions, started);
    }

    private void Find(double span)
    {
        var t = (float)span;
        var n = 0;
        for (var k = 0; k < _count; k++)
        {
            var v = _vessel[k]!;
            _vessel[k] = null; // nothing kept past the window
            var body = _body[k]!;
            var held = Collisions.Held(v);
            if (!v.OnFormula || v.State == FlightState.Crashed || v.State != _state[k] || v.Body != body || held != _held[k] || v.Motion.Standing != _standing[k])
                continue; // its motion remade inside the window: its way across it isn't one curve
            // As it ends, among the stars as the ground was as it began: turned on by the window's turn.
            var turn = QuaternionDouble.FromAxisAngle(body.NorthPole, body.TurnRadians(span));
            var centre = (Vector3Double)body.Centre;
            var at = v.TrueCentreOfMass;
            var at1 = centre + turn.Rotate(at - centre);
            var moving1 = turn.Rotate(v.TrueVelocity + (Vector3Double)body.GroundSpin(v.CentreOfMass));
            var rotation = v.Rotation;
            var (bottom, top, width) = Unwatched.Body(v);
            var middle = body.TurnedDirection(v.MiddleOffset(rotation), span);
            var size = MathF.Max(Collisions.Size(v), MathF.Max(bottom.Length(), top.Length()) + width);
            var pull1 = body.TurnedDirection(Pull(v, held), span);

            // Moved down to slot n (n <= k), its start kept beside it.
            (_vessel[n], _body[n], _held[n]) = (v, body, held);
            (_at0[n], _moving0[n], _pull0[n]) = (_at0[k], _moving0[k], _pull0[k]);
            (_at1[n], _moving1[n], _pull1[n], _middle[n]) = (at1, moving1, pull1, middle);
            (_bottom[n], _top[n], _width[n], _size[n], _met[n]) = (body.TurnedDirection(bottom, span), body.TurnedDirection(top, span), width, size, false);
            // Its box: its two ends, and the most its pull bows its way off the line between them.
            var (start, end) = (_at0[n].ToSingle(), at1.ToSingle());
            var bow = MathF.Max(_pull0[n].Length(), pull1.Length()) * t * t / 8f;
            var pad = new Vector3(size + middle.Length() + bow);
            (_boxLow[n], _boxHigh[n]) = (Vector3.Min(start, end) - pad, Vector3.Max(start, end) + pad);
            (_sweepKeys[n], _sweepSlots[n]) = (_boxLow[n].X, n);
            n++;
        }
        if (n >= 2)
            Pairs(n, span);
        for (var k = 0; k < n; k++)
            (_vessel[k], _body[k]) = (null, null); // nothing kept past the window
    }

    /// <summary>The <paramref name="n"/> slots in the window's pairs (<paramref name="span"/> long): swept and pruned, solved, and each one's earliest judged.</summary>
    private void Pairs(int n, double span)
    {
        Array.Sort(_sweepKeys, _sweepSlots, 0, n);
        _foundCount = 0;
        for (var s = 0; s < n; s++)
        {
            var i = _sweepSlots[s];
            for (var s2 = s + 1; s2 < n && _sweepKeys[s2] <= _boxHigh[i].X; s2++)
            {
                var j = _sweepSlots[s2];
                if (_body[i] != _body[j] || (_held[i] && _held[j]) // two at different bodies never meet (their places are in two frames)
                    || _boxLow[i].Y > _boxHigh[j].Y || _boxLow[j].Y > _boxHigh[i].Y
                    || _boxLow[i].Z > _boxHigh[j].Z || _boxLow[j].Z > _boxHigh[i].Z)
                    continue;
                Solve(i, j, span);
            }
        }

        // Each vessel's earliest: the found in order of time (few, so sorted in place), each judged
        // unless one of its two has hit another sooner.
        for (var k = 1; k < _foundCount; k++)
        {
            var (f, m) = (_found[k], k - 1);
            for (; m >= 0 && _found[m].At > f.At; m--)
                _found[m + 1] = _found[m];
            _found[m + 1] = f;
        }
        for (var k = 0; k < _foundCount; k++)
        {
            var f = _found[k];
            if (_met[f.A] || _met[f.B])
                continue;
            (_met[f.A], _met[f.B]) = (true, true);
            Judged++;
            var s = (f.At - _t0) / span;
            FormulaContact.Wreck(_vessel[f.A]!, GroundAt(f.A, s, span, f.At), _vessel[f.B]!, GroundAt(f.B, s, span, f.At), f.Closing);
        }
    }

    /// <summary>
    /// Whether slots <paramref name="i"/> and <paramref name="j"/>'s bodies begin to touch in the
    /// window, <paramref name="span"/> long, closing faster than either can take; if so, found.
    /// </summary>
    private void Solve(int i, int j, double span)
    {
        // The second's middle from the first's along their two Hermite ways, at s (0 to 1) of the window.
        var offset = (Vector3Double)(_middle[j] - _middle[i]);
        var (d0, d1) = (_at0[j] - _at0[i] + offset, _at1[j] - _at1[i] + offset);
        var (v0, v1) = ((_moving0[j] - _moving0[i]) * span, (_moving1[j] - _moving1[i]) * span); // per the window's length
        var reach = _size[i] + _size[j];
        var s = Closest(d0, v0, d1, v1);
        if (Gap(d0, v0, d1, v1, s).Length() >= reach + FormulaContact.Slack)
            return;

        // The stretch round their closest, back to where their reaches first meet and on to where
        // they part if further (each within the window), so a slow touch is found where it begins.
        var half = Scene.FormulaCollisionSeconds / 2.0 / span;
        var reach2 = (double)reach * reach;
        var (low, high) = (Math.Max(0, s - half), Math.Min(1, s + half));
        if (Gap(d0, v0, d1, v1, low).LengthSquared() <= reach2)
            low = Edge(d0, v0, d1, v1, reach2, 0, low);
        if (Gap(d0, v0, d1, v1, high).LengthSquared() <= reach2)
            high = Edge(d0, v0, d1, v1, reach2, 1, high);

        // Their bodies over it, from each one's Hermite state at its start, and their pulls there.
        var r = Gap(d0, v0, d1, v1, low).ToSingle();
        var u = (Rate(d0, v0, d1, v1, low) / span).ToSingle();
        var a = Vector3.Lerp(_pull0[j], _pull1[j], (float)low) - Vector3.Lerp(_pull0[i], _pull1[i], (float)low);
        var (touch, closing) = FormulaContact.Touch(r, u, a, reach, 0f, (float)((high - low) * span),
            _bottom[i], _top[i], _width[i], _bottom[j], _top[j], _width[j]);
        if (float.IsNaN(touch))
            return;
        var (vi, vj) = (_vessel[i]!, _vessel[j]!);
        if (closing <= MathF.Min(vi.ImpactTolerance, vj.ImpactTolerance))
            return; // a bump: nothing changes
        if (_foundCount == _found.Length)
            Array.Resize(ref _found, 2 * _found.Length);
        _found[_foundCount++] = new Found { A = i, B = j, At = _t0 + low * span + touch, Closing = closing };
    }

    /// <summary>
    /// Where (0 to 1 of the window) the gap along the Hermite way through <paramref name="d0"/>,
    /// <paramref name="v0"/> (its rate times the window's length) to <paramref name="d1"/>,
    /// <paramref name="v1"/> is least: golden-section on its square over the window, then Newton
    /// on the gap's rate along it from the better of that and the chord's closest; the least of the three.
    /// </summary>
    private static double Closest(Vector3Double d0, Vector3Double v0, Vector3Double d1, Vector3Double v1)
    {
        var chord = d1 - d0;
        var cc = chord.LengthSquared();
        var seed = cc > 1e-12 ? Math.Clamp(-Vector3Double.Dot(d0, chord) / cc, 0, 1) : 0;
        double F(double s) => Gap(d0, v0, d1, v1, s).LengthSquared();

        var (lo, hi) = (0.0, 1.0);
        var (x1, x2) = (hi - Golden * (hi - lo), lo + Golden * (hi - lo));
        var (f1, f2) = (F(x1), F(x2));
        for (var k = 0; k < GoldenSteps; k++)
        {
            if (f1 < f2)
            {
                (hi, x2, f2) = (x2, x1, f1);
                x1 = hi - Golden * (hi - lo);
                f1 = F(x1);
            }
            else
            {
                (lo, x1, f1) = (x1, x2, f2);
                x2 = lo + Golden * (hi - lo);
                f2 = F(x2);
            }
        }
        var golden = (lo + hi) / 2;
        var (best, fBest) = (seed, F(seed));
        if (F(golden) < fBest)
            (best, fBest) = (golden, F(golden));
        var newton = best;
        for (var k = 0; k < NewtonSteps; k++)
        {
            var (g, rate, bend) = (Gap(d0, v0, d1, v1, newton), Rate(d0, v0, d1, v1, newton), Bend(d0, v0, d1, v1, newton));
            var slope = rate.LengthSquared() + Vector3Double.Dot(g, bend);
            if (!(slope > 1e-12))
                break;
            newton = Math.Clamp(newton - Vector3Double.Dot(g, rate) / slope, 0, 1);
        }
        return F(newton) < fBest ? newton : best;
    }

    /// <summary>Where between <paramref name="outside"/> and <paramref name="inside"/> (within reach) the gap's square crosses <paramref name="reach2"/>, halved for; <paramref name="outside"/> itself if it is within reach too.</summary>
    private static double Edge(Vector3Double d0, Vector3Double v0, Vector3Double d1, Vector3Double v1, double reach2, double outside, double inside)
    {
        if (Gap(d0, v0, d1, v1, outside).LengthSquared() <= reach2)
            return outside;
        for (var k = 0; k < 24; k++)
        {
            var mid = (outside + inside) / 2;
            if (Gap(d0, v0, d1, v1, mid).LengthSquared() > reach2)
                outside = mid;
            else
                inside = mid;
        }
        return outside;
    }

    /// <summary>The cubic Hermite through <paramref name="p0"/>, <paramref name="m0"/> to <paramref name="p1"/>, <paramref name="m1"/> (its rates per the whole span), at <paramref name="s"/> of the way.</summary>
    private static Vector3Double Gap(Vector3Double p0, Vector3Double m0, Vector3Double p1, Vector3Double m1, double s)
    {
        var (s2, s3) = (s * s, s * s * s);
        return p0 * (2 * s3 - 3 * s2 + 1) + m0 * (s3 - 2 * s2 + s) + p1 * (3 * s2 - 2 * s3) + m1 * (s3 - s2);
    }

    /// <summary>Its rate (per the whole span) at <paramref name="s"/>.</summary>
    private static Vector3Double Rate(Vector3Double p0, Vector3Double m0, Vector3Double p1, Vector3Double m1, double s)
    {
        var s2 = s * s;
        return p0 * (6 * s2 - 6 * s) + m0 * (3 * s2 - 4 * s + 1) + p1 * (6 * s - 6 * s2) + m1 * (3 * s2 - 2 * s);
    }

    /// <summary>How its rate changes (per the whole span, squared) at <paramref name="s"/>.</summary>
    private static Vector3Double Bend(Vector3Double p0, Vector3Double m0, Vector3Double p1, Vector3Double m1, double s) =>
        p0 * (12 * s - 6) + m0 * (6 * s - 4) + p1 * (6 - 12 * s) + m1 * (6 * s - 2);

    /// <summary>Slot <paramref name="k"/>'s centre of mass at <paramref name="s"/> of the window (world time <paramref name="at"/>), along its Hermite way, in its body's ground's frame.</summary>
    private Vector3 GroundAt(int k, double s, double span, double at)
    {
        var body = _body[k]!;
        var stars = Gap(_at0[k], _moving0[k] * span, _at1[k], _moving1[k] * span, s);
        var centre = (Vector3Double)body.Centre;
        return (centre + QuaternionDouble.FromAxisAngle(body.NorthPole, body.TurnRadians(-(at - _t0))).Rotate(stars - centre)).ToSingle();
    }

    /// <summary>What pulls <paramref name="v"/> now among the stars: its fall (or held, the ground's turn), the air's drag and its thrust, as <see cref="Unwatched"/>' check reads it.</summary>
    private static Vector3 Pull(Vessel v, bool held)
    {
        var at = v.CentreOfMass;
        return Collisions.Fall(v.Body, held, at) - v.Velocity * Collisions.Drag(v.Body, held, at) + Collisions.Thrust(v);
    }

    /// <summary>Grows the slots for <paramref name="count"/> vessels, should the world have grown past a full one.</summary>
    private void Fit(int count)
    {
        if (count <= _vessel.Length)
            return;
        count = Math.Max(count, 2 * _vessel.Length);
        Array.Resize(ref _vessel, count);
        Array.Resize(ref _body, count);
        Array.Resize(ref _state, count);
        Array.Resize(ref _held, count);
        Array.Resize(ref _standing, count);
        Array.Resize(ref _at0, count);
        Array.Resize(ref _moving0, count);
        Array.Resize(ref _pull0, count);
        Array.Resize(ref _at1, count);
        Array.Resize(ref _moving1, count);
        Array.Resize(ref _pull1, count);
        Array.Resize(ref _middle, count);
        Array.Resize(ref _bottom, count);
        Array.Resize(ref _top, count);
        Array.Resize(ref _width, count);
        Array.Resize(ref _size, count);
        Array.Resize(ref _boxLow, count);
        Array.Resize(ref _boxHigh, count);
        Array.Resize(ref _met, count);
        Array.Resize(ref _sweepKeys, count);
        Array.Resize(ref _sweepSlots, count);
    }
}
