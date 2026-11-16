using System.Numerics;

namespace GameOne;

/// <summary>
/// The path something with its engine off will follow round a body, with nothing but that body's pull acting
/// on it: an ellipse round its centre (a hyperbola, if it is fast enough to escape), worked out
/// from where it is and how it is moving, in the body's frame. This is the path the zoomed-out
/// view draws ahead of a vessel, and it agrees with the HUD's apoapsis and periapsis
/// (<see cref="Planet.Orbit"/>). It also says where along it the vessel will be when (Kepler's
/// equation), for the closest approach and the transfer windows (<see cref="Encounters"/>). Where
/// a path leaves one body's sphere of influence for another's it is patched there, a leg round
/// each (<see cref="Patched"/>).
/// </summary>
/// <remarks>
/// <para>Places on it are given by their true anomaly: the angle round from the periapsis, in
/// radians, counted on past a lap rather than wrapped, so "ahead" is simply "more". <see cref="Now"/>
/// is where it is now, between -π and π.</para>
/// <para>It ignores the air: in the atmosphere drag pulls a vessel short of it, so a path drawn there is
/// only ever where it would go if the air let it. Worked out in doubles, as an orbit 20 km round
/// has to be found to the metre from speeds a fraction of a metre per second apart.</para>
/// <para>It is worked out among its body's stars, where an orbit is an ellipse: its velocity is
/// the stars' (<see cref="Planet.AmongStars"/>; <see cref="OverGround(GroundBody, Vector3, Vector3, bool, double, float)"/>
/// converts a vessel's), and its points are where the body's ground frame is now. The body turns
/// under it meanwhile, so wherever the path is tested against the ground, the ground is turned to
/// where it will be by then (<see cref="GroundAt"/>). Everything it reads (the centre, the pull,
/// the turn, the ground and the sea) is its body's (<see cref="Body"/>).</para>
/// </remarks>
internal readonly struct Orbit
{
    /// <summary>A path that escapes is followed this far from its body's centre, then stops.</summary>
    public const float MaxRange = 30f * GroundBody.PlanetRadius;

    private readonly GroundBody _body;   // what it goes round, in whose frame it is
    private readonly double _p;          // the semi-latus rectum: the distance at a right angle to the periapsis
    private readonly double _e;          // eccentricity: 0 a circle, under 1 an ellipse, over 1 escaping
    private readonly Vector3 _toPeriapsis, _ahead; // the orbit's plane: towards the periapsis, and a right angle on in the way it goes
    private readonly double _now;        // where it is now round the orbit (its true anomaly)
    private readonly double _meanMotion, _meanNow; // for Kepler's equation: how fast the mean anomaly runs, and where it is now
    private readonly double _end;        // where the path ahead ends: a lap on, on the terrain, or out at the edge
    private readonly bool _radial;       // moving straight up or down: no orbit to speak of, just a line
    private readonly bool _rising;
    private readonly Vector3 _position, _velocity, _up;
    private readonly double _top;        // for a straight up and down path: the distance from the centre it stops at
    private readonly double _ground;     // for a straight up and down path: the distance from the centre of the terrain it comes down on
    private readonly double _fallSeconds; // for a straight up and down path: how long until it is down there
    private readonly double _from;       // how many seconds from now this state is: the ground has turned that much more by then
    private readonly float _clearance;   // how far over the terrain the path counts as on it: the size of what follows it

    /// <summary>An orbit round <paramref name="body"/>, from <paramref name="position"/> in its frame.</summary>
    /// <param name="velocity">The velocity among the body's stars (see <see cref="OverGround(GroundBody, Vector3, Vector3, bool, double, float)"/> for one measured against its ground).</param>
    /// <param name="findEnd">False for an orbit only asked its shape (<see cref="DistanceToward"/>, <see cref="PeriapsisDistance"/>), as the transfer and de-orbit searches ask of over a thousand a plan: finding where it meets the terrain samples the ground, which they have no use for (its end is then left at <see cref="Now"/>; straight up and down, it comes down to the sea, not the terrain).</param>
    /// <param name="from">How many seconds from now <paramref name="position"/> and <paramref name="velocity"/> are (a leg of a plan that starts at a node ahead): the ground under the path is turned by that as well as by the time along it.</param>
    /// <param name="clearance">How far over the terrain (or the sea) the path counts as down on it: the radius of something round the point followed (a vessel's size, for its fast collisions: see <see cref="Collisions"/>), so a path whose point clears a ridge by less than that meets it. Its end (<see cref="Landing"/>) is then that far up.</param>
    public Orbit(GroundBody body, Vector3 position, Vector3 velocity, bool findEnd = true, double from = 0, float clearance = 0f)
    {
        _body = body;
        (_position, _velocity, _from, _clearance) = (position, velocity, from, clearance);
        var r = ToDouble(position - _body.Centre);
        var v = ToDouble(velocity);
        double mu = _body.Mu;
        var distance = r.Length();
        var h = Cross(r, v);
        var h2 = h.LengthSquared();
        _up = ToSingle(r / distance);

        // Hardly any sideways speed: it only goes up and comes down again (or keeps going, escaping).
        var energy = v.LengthSquared() / 2 - mu / distance;
        if (h2 < 1e-6 * mu * distance)
        {
            _radial = true;
            _rising = Vector3Double.Dot(r, v) > 0;
            _top = energy < 0 ? Math.Min(-mu / energy, MaxRange) : MaxRange;
            // The line never leaves its direction among the stars, but the ground turns under it as
            // it falls: the terrain it comes down on is where that direction is over the ground by
            // then. Twice round, as how long the fall takes depends on how high that terrain is.
            // Asked only its shape (findEnd false), it isn't looked for: down to the sea will do.
            _ground = (findEnd ? _body.SeaRadius + SurfaceHeight(_up) : _body.SeaRadius) + clearance;
            if (_top < MaxRange && !findEnd)
                _fallSeconds = RadialSeconds(distance, _ground);
            else if (_top < MaxRange)
                for (var i = 0; i < 2; i++)
                {
                    _fallSeconds = RadialSeconds(distance, _ground);
                    _ground = _body.SeaRadius + SurfaceHeight(_body.TurnedDirection(_up, -(_from + _fallSeconds))) + clearance;
                }
            return;
        }

        var eVector = Cross(v, h) / mu - r / distance;
        _e = eVector.Length();
        if (Math.Abs(_e - 1) < 1e-7)
            _e = 1 - 1e-7; // exactly parabolic has a Kepler's equation of its own: this near, an ellipse will do
        _p = h2 / mu;
        var normal = h / Math.Sqrt(h2);
        // A circle has no periapsis to measure from: any direction in its plane will do, so here.
        var toPeriapsis = _e > 1e-9 ? eVector / _e : r / distance;
        _toPeriapsis = ToSingle(toPeriapsis);
        _ahead = ToSingle(Cross(normal, toPeriapsis));
        _now = Math.Atan2(Vector3Double.Dot(r, Cross(normal, toPeriapsis)), Vector3Double.Dot(r, toPeriapsis));

        var a = Math.Abs(_p / (1 - _e * _e));
        _meanMotion = Math.Sqrt(mu / (a * a * a));
        _meanNow = Mean(_now);
        _end = findEnd ? End() : _now;
    }

    /// <summary>The orbit round <paramref name="body"/> of something at <paramref name="position"/> (in its frame) moving at <paramref name="groundVelocity"/> over its ground, converted to its stars' (its ground's own velocity there added).</summary>
    public static Orbit OverGround(GroundBody body, Vector3 position, Vector3 groundVelocity, bool findEnd = true, double from = 0, float clearance = 0f) =>
        new(body, position, groundVelocity + Vector3.Cross(body.Spin, position - body.Centre), findEnd, from, clearance);

    /// <summary>What it goes round: its points are in this body's frame, its velocities among its stars.</summary>
    public GroundBody Body => _body;

    /// <summary>
    /// Where the ground under the path at <paramref name="anomaly"/> will be by the time it is there,
    /// back in the ground's frame: the point turned back by the body's turn meanwhile. Not for a
    /// straight up and down path (see <see cref="Landing"/>).
    /// </summary>
    public Vector3 GroundAt(double anomaly) => Turned(At(anomaly), -(_from + TimeTo(anomaly)));

    /// <summary>A point <paramref name="p"/> in the body's frame after <paramref name="seconds"/> of its turn (see <see cref="Planet.Turned"/>).</summary>
    private Vector3 Turned(Vector3 p, double seconds) => _body.Centre + _body.TurnedDirection(p - _body.Centre, seconds);

    /// <summary>The height of the body's surface (its ground, or the calm sea over it) in a unit <paramref name="direction"/> from its centre (see <see cref="Terrain.SurfaceHeight"/>).</summary>
    private float SurfaceHeight(Vector3 direction) => _body.HasSea ? MathF.Max(_body.Height(direction), 0f) : _body.Height(direction);

    /// <summary>Where the path ahead ends, on the ground (if <see cref="EndsOnGround"/>): where it comes down, in the ground's frame.</summary>
    public Vector3 Landing => !_radial
        ? GroundAt(_end)
        : _body.Centre + _body.TurnedDirection(_up, -(_from + _fallSeconds)) * (float)_ground;

    /// <summary>Whether it is going straight up or down: then it has no orbit, and none of the places on one.</summary>
    public bool Radial => _radial;

    /// <summary>How many seconds from now it starts (the constructor's <c>from</c>): 0 for an orbit as it is now.</summary>
    public double From => _from;

    /// <summary>Where it is now (its true anomaly).</summary>
    public double Now => _now;

    /// <summary>Where the path ahead ends: a lap on (<see cref="Now"/> + 2π), on the terrain, or out at <see cref="MaxRange"/>.</summary>
    public double EndAnomaly => _end;

    /// <summary>Whether the path ahead ends on the ground.</summary>
    public bool EndsOnGround => !_radial ? Distance(_end) < _body.SeaRadius + TerrainUnder(_end) + 1 : _top < MaxRange;

    /// <summary>Whether it goes round and round, missing the ground.</summary>
    public bool Closed => !_radial && _e < 1 && !EndsOnGround;

    /// <summary>
    /// The lap after this one (of an orbit that goes round, <see cref="Closed"/>): the same orbit,
    /// starting where this one's path ends, a lap on. Its own end is found over the ground as it
    /// will have turned by then, which is why it is an orbit of its own and not this one sampled on
    /// past its end: a lap that misses every peak can meet one on the next.
    /// </summary>
    public Orbit NextLap() => new(_body, At(_end), VelocityAt(_end), from: _from + TimeTo(_end));

    /// <summary>The time a lap takes (infinite for one that escapes).</summary>
    public double Period => !_radial && _e < 1 ? 2 * Math.PI / _meanMotion : double.PositiveInfinity;

    /// <summary>How long until the path ahead ends (a lap, for one that goes round).</summary>
    public double PathSeconds => _radial ? 0 : TimeTo(_end);

    /// <summary>
    /// How long until it is where the path ahead ends (<see cref="EndAnomaly"/>; straight up and
    /// down, until it is down on the terrain, or 0 for one that escapes): with <see cref="EndsOnGround"/>,
    /// the seconds until it meets the ground or the sea.
    /// </summary>
    public double SecondsToEnd => _radial ? _fallSeconds : TimeTo(_end);

    /// <summary>The distance from its body's centre at <paramref name="anomaly"/>.</summary>
    public double Distance(double anomaly) => _p / (1 + _e * Math.Cos(anomaly));

    /// <summary>The point at <paramref name="anomaly"/>.</summary>
    public Vector3 At(double anomaly)
    {
        var distance = Distance(anomaly);
        var (sin, cos) = Math.SinCos(anomaly);
        return _body.Centre + (_toPeriapsis * (float)(cos * distance) + _ahead * (float)(sin * distance));
    }

    /// <summary>The velocity at <paramref name="anomaly"/>.</summary>
    public Vector3 VelocityAt(double anomaly)
    {
        var speed = Math.Sqrt(_body.Mu / _p);
        var (sin, cos) = Math.SinCos(anomaly);
        return _toPeriapsis * (float)(-sin * speed) + _ahead * (float)((_e + cos) * speed);
    }

    /// <summary>How long from now until it is at <paramref name="anomaly"/> (which must be ahead of <see cref="Now"/>).</summary>
    public double TimeTo(double anomaly) => (Mean(anomaly) - _meanNow) / _meanMotion;

    /// <summary>Where it will be (its true anomaly) <paramref name="seconds"/> from now.</summary>
    public double AnomalyAfter(double seconds) => True(_meanNow + _meanMotion * seconds);

    /// <summary>The point it will be at <paramref name="seconds"/> from now.</summary>
    public Vector3 PositionAfter(double seconds) => _radial ? StateAfter(seconds).Position : At(AnomalyAfter(seconds));

    /// <summary>
    /// Where it will be <paramref name="seconds"/> from now, and how it will be moving (straight up
    /// or down: along its line among the stars, by Kepler's equation for a line, <see cref="RadialAfter"/>).
    /// </summary>
    public (Vector3 Position, Vector3 Velocity) StateAfter(double seconds)
    {
        if (_radial)
        {
            var (distance, speed) = RadialAfter(seconds);
            return (((Vector3Double)_body.Centre + (Vector3Double)_up * distance).ToSingle(), _up * (float)speed);
        }
        var anomaly = AnomalyAfter(seconds);
        return (At(anomaly), VelocityAt(anomaly));
    }

    /// <summary>
    /// Straight up or down: how far from its body's centre it is <paramref name="seconds"/> from now
    /// (back too), and how fast it moves out along its line (negative falling), by Kepler's equation
    /// for a line, from its energy: falling back, r = a(1 − cos E) at t = √(a³/μ)(E − sin E);
    /// escaping, r = a(cosh H − 1) at t = √(a³/μ)(sinh H − H); just escaping, r = (3/2·√(2μ)·t)^⅔,
    /// each t from where the line meets the centre. Held short of the centre itself (it is on the
    /// ground long before).
    /// </summary>
    private (double Distance, double Speed) RadialAfter(double seconds)
    {
        double mu = _body.Mu;
        var r0 = ((Vector3Double)_position - (Vector3Double)_body.Centre).Length();
        var v0 = Vector3Double.Dot(_velocity, _up);
        var energy = v0 * v0 / 2 - mu / r0;
        const double Tiny = 1e-9;
        if (Math.Abs(energy) * r0 < 1e-9 * mu)
        {
            // Just at escape: |τ| = (2/3)·r^(3/2)/√(2μ), out after the centre, in before it.
            var root = Math.Sqrt(2 * mu);
            var tau = (v0 >= 0 ? 1 : -1) * 2.0 / 3.0 * Math.Pow(r0, 1.5) / root + seconds;
            tau = v0 >= 0 ? Math.Max(tau, Tiny) : Math.Min(tau, -Tiny);
            var r = Math.Pow(1.5 * root * Math.Abs(tau), 2.0 / 3.0);
            return (r, Math.Sign(tau) * Math.Sqrt(2 * mu / r));
        }
        if (energy < 0)
        {
            var a = -mu / (2 * energy);
            var n = Math.Sqrt(mu / (a * a * a));
            var e0 = Math.Acos(Math.Clamp(1 - r0 / a, -1, 1));
            if (v0 < 0)
                e0 = 2 * Math.PI - e0;
            var m = Math.Clamp(e0 - Math.Sin(e0) + n * seconds, Tiny, 2 * Math.PI - Tiny);
            // E − sin E rises from 0 to 2π: Newton within a bracket, halving where a step leaves it.
            var (lo, hi, e) = (0.0, 2 * Math.PI, Math.PI);
            for (var i = 0; i < 100; i++)
            {
                var f = e - Math.Sin(e) - m;
                if (Math.Abs(f) < 1e-14)
                    break;
                (lo, hi) = f < 0 ? (e, hi) : (lo, e);
                var next = e - f / Math.Max(1 - Math.Cos(e), 1e-300);
                e = next > lo && next < hi ? next : (lo + hi) / 2;
            }
            var cos = Math.Cos(e);
            return (a * (1 - cos), a * n * Math.Sin(e) / Math.Max(1 - cos, 1e-300));
        }
        {
            var a = mu / (2 * energy);
            var n = Math.Sqrt(mu / (a * a * a));
            var h0 = Math.Acosh(1 + r0 / a);
            if (v0 < 0)
                h0 = -h0;
            var m = Math.Sinh(h0) - h0 + n * seconds;
            m = v0 >= 0 ? Math.Max(m, Tiny) : Math.Min(m, -Tiny); // on its own side of the centre
            // sinh H − H rises with H, its sign H's: bracketed, then Newton within it as above.
            var (lo, hi) = (0.0, 1.0);
            while (Math.Sinh(hi) - hi < Math.Abs(m))
                hi *= 2;
            var h = hi / 2;
            for (var i = 0; i < 200; i++)
            {
                var f = Math.Sinh(h) - h - Math.Abs(m);
                if (Math.Abs(f) < 1e-14 * Math.Max(1, Math.Abs(m)))
                    break;
                (lo, hi) = f < 0 ? (h, hi) : (lo, h);
                var next = h - f / Math.Max(Math.Cosh(h) - 1, 1e-300);
                h = next > lo && next < hi ? next : (lo + hi) / 2;
            }
            h *= Math.Sign(m);
            var cosh = Math.Cosh(h) - 1;
            return (a * cosh, a * n * Math.Sinh(h) / Math.Max(cosh, 1e-300));
        }
    }

    /// <summary>
    /// Straight up or down and still rising: how long from now until it is <paramref name="distance"/>
    /// from its body's centre on the way up, if it gets there (it may fall back short of it).
    /// </summary>
    public double? RisingTo(double distance)
    {
        if (!_radial || Vector3Double.Dot(_velocity, _up) <= 0 || distance > _top)
            return null;
        var r0 = ((Vector3Double)_position - (Vector3Double)_body.Centre).Length();
        if (distance <= r0)
            return 0;
        // Out along the line the distance only grows while it rises: bracketed by its top's time
        // falling back (past it, it comes down again), else by doubling, then halved (to a
        // millisecond's tenth).
        var (lo, hi) = (0.0, 1.0);
        if (TopSeconds is { } top)
        {
            hi = top;
            if (RadialAfter(hi).Distance < distance - 1e-3)
                return null; // short of it at its top
        }
        else
            for (var doubled = 0; RadialAfter(hi).Distance < distance; doubled++)
            {
                if (doubled == 64 || RadialAfter(hi).Speed <= 0)
                    return null; // fallen back short of it (a top past the range's, falling back)
                hi *= 2;
            }
        while (hi - lo > 1e-4)
        {
            var mid = (lo + hi) / 2;
            if (RadialAfter(mid).Distance < distance)
                lo = mid;
            else
                hi = mid;
        }
        return hi;
    }

    /// <summary>Straight up or down: the farthest from its body's centre it goes (out to <see cref="MaxRange"/>, escaping); else its apoapsis's distance.</summary>
    public double HighestDistance => _radial ? _top : ApoapsisDistance;

    /// <summary>Straight up or down: the nearest to its body's centre it comes ahead (the ground's, falling back); else its periapsis's distance.</summary>
    public double LowestDistance => _radial ? (Vector3Double.Dot(_velocity, _up) >= 0 && _top >= MaxRange ? ((Vector3Double)_position - (Vector3Double)_body.Centre).Length() : _ground) : PeriapsisDistance;

    /// <summary>
    /// Fills <paramref name="points"/> with the path from <paramref name="from"/> to
    /// <paramref name="to"/> seconds from now (no further than where the path ahead ends), each
    /// point with its time from now: about <paramref name="segments"/> pieces a lap.
    /// </summary>
    public void Samples(List<(Vector3 At, double Seconds)> points, double from, double to, int segments = 256)
    {
        points.Clear();
        if (_radial)
        {
            points.Add((_position, 0));
            return;
        }
        var start = from <= 0 ? _now : AnomalyAfter(from);
        var end = double.IsFinite(to) ? Math.Min(AnomalyAfter(to), _end) : _end;
        var span = end - start;
        if (span <= 0)
            return;
        var count = Math.Max(2, (int)Math.Ceiling(segments * span / (2 * Math.PI)));
        for (var i = 0; i <= count; i++)
        {
            var anomaly = start + span * i / count;
            points.Add((At(anomaly), TimeTo(anomaly)));
        }
    }

    /// <summary>How far from its body's centre it is in <paramref name="direction"/> (in its plane), or infinity if it never goes that way.</summary>
    public double DistanceToward(Vector3 direction)
    {
        var anomaly = Math.Atan2(Vector3.Dot(direction, _ahead), Vector3.Dot(direction, _toPeriapsis));
        var denominator = 1 + _e * Math.Cos(anomaly);
        return denominator > 1e-9 ? _p / denominator : double.PositiveInfinity;
    }

    /// <summary>The first place ahead where it is at <paramref name="direction"/>'s angle round its plane.</summary>
    public double AnomalyToward(Vector3 direction)
    {
        var anomaly = Math.Atan2(Vector3.Dot(direction, _ahead), Vector3.Dot(direction, _toPeriapsis));
        while (anomaly <= _now)
            anomaly += 2 * Math.PI;
        return anomaly;
    }

    /// <summary>The lowest point's distance from its body's centre.</summary>
    public double PeriapsisDistance => _p / (1 + _e);

    /// <summary>The highest point's distance from its body's centre (infinite for one that escapes).</summary>
    public double ApoapsisDistance => _e < 1 ? _p / (1 - _e) : double.PositiveInfinity;

    /// <summary>How far from round it is: 0 a circle, under 1 an ellipse, over 1 escaping (0 straight up and down, which has no orbit).</summary>
    public double Eccentricity => _e;

    /// <summary>Its distance from its body's centre a right angle round from the periapsis (0 straight up and down).</summary>
    public double SemiLatusRectum => _p;

    /// <summary>The orbit's plane, by the way round it goes: at a right angle to it, as the right hand's thumb with its fingers the way it goes (zero straight up and down).</summary>
    public Vector3 Normal => Vector3.Cross(_toPeriapsis, _ahead);

    /// <summary>Where round its plane <paramref name="direction"/> is (its true anomaly), between -π and π: unlike <see cref="AnomalyToward"/>, not moved on ahead of <see cref="Now"/>.</summary>
    public double AngleToward(Vector3 direction) => Math.Atan2(Vector3.Dot(direction, _ahead), Vector3.Dot(direction, _toPeriapsis));

    /// <summary>The next periapsis ahead on the path, if it reaches one.</summary>
    public double? NextPeriapsis => _radial ? null : Within((Math.Floor(_now / (2 * Math.PI)) + 1) * 2 * Math.PI);

    /// <summary>The next apoapsis ahead on the path, if it has one and reaches it.</summary>
    public double? NextApoapsis => _radial || _e >= 1 ? null : Within((Math.Floor((_now - Math.PI) / (2 * Math.PI)) + 1) * 2 * Math.PI + Math.PI);

    /// <summary>Where the path ahead comes down through <paramref name="distance"/> from its body's centre, if it does.</summary>
    public double? Descending(double distance)
    {
        if (_radial)
            return null;
        var cos = (_p / distance - 1) / Math.Max(_e, 1e-12);
        if (cos < -1 || cos > 1)
            return null;
        var down = -Math.Acos(cos);
        while (down <= _now)
            down += 2 * Math.PI;
        return Within(down);
    }

    /// <summary>Where the path ahead goes up through <paramref name="distance"/> from its body's centre, if it does before its end.</summary>
    public double? Ascending(double distance)
    {
        if (_radial)
            return null;
        var cos = (_p / distance - 1) / Math.Max(_e, 1e-12);
        if (cos < -1 || cos > 1)
            return null;
        var up = Math.Acos(cos);
        if (_e >= 1)
            return up > _now ? Within(up) : null; // a hyperbola goes out once, never round again
        while (up <= _now)
            up += 2 * Math.PI;
        return Within(up);
    }

    private double? Within(double anomaly) => anomaly > _now && anomaly <= _end + 1e-9 ? anomaly : null;

    /// <summary>
    /// Fills <paramref name="points"/> with the path ahead, from where it is now: once round, for an
    /// orbit that misses the ground; as far as the ground, for one that comes down; or out to
    /// <see cref="MaxRange"/>, for one that escapes. About <paramref name="segments"/> pieces for a
    /// whole lap, fewer for less.
    /// </summary>
    public void Path(List<Vector3> points, int segments = 256)
    {
        points.Clear();
        if (_radial)
        {
            // Up to the top (if it is still rising) and back down, along the one line.
            points.Add(_position);
            if (_rising)
                points.Add(_body.Centre + _up * (float)_top);
            if (_top < MaxRange)
                points.Add(_body.Centre + _up * (float)_ground);
            return;
        }

        var span = _end - _now;
        if (span <= 0)
            return;
        var count = Math.Max(2, (int)Math.Ceiling(segments * span / (2 * Math.PI)));
        for (var i = 0; i <= count; i++)
            points.Add(At(_now + span * i / count));
    }

    /// <summary>
    /// Where on the orbit the path ahead ends: a lap on, where it meets the terrain, or out at
    /// <see cref="MaxRange"/>.
    /// </summary>
    private double End()
    {
        var end = _now + 2 * Math.PI;
        // Escaping: only out to the range (the far branch of a hyperbola is never reached).
        if (_e >= 1)
        {
            var cosOut = Math.Clamp((_p / MaxRange - 1) / _e, -1, 1);
            end = Math.Acos(cosOut);
        }
        // It comes down: the surface is never lower than its lowest (the sea, or with none the
        // lowest ground), so the path has met it by the time it is down to there (on the way down:
        // round the periapsis, it is below it).
        var lowest = _body.LowestRadius + _clearance;
        var cosGround = (_p / lowest - 1) / Math.Max(_e, 1e-12);
        if (cosGround < 1)
        {
            var ground = -Math.Acos(Math.Max(cosGround, -1));
            while (ground < _now)
                ground += 2 * Math.PI;
            if (ground <= end)
                end = ground;
        }
        // Where the path is down to the lowest surface it is in the terrain (the ground is never
        // lower), so a search that finds nothing has only float rounding to blame: end there.
        // A lap or escape end, with no crossing of it, is not down there, and the search finds no hit.
        return MeetsTerrain(end) ?? end;
    }

    /// <summary>The first place from now to <paramref name="limit"/> where the path is in the terrain (to about half a metre), or null if it never is.</summary>
    private double? MeetsTerrain(double limit)
    {
        // Only the stretch of the path below the highest peak (and a margin: the fine detail adds a
        // few metres) can touch the terrain: an arc round each periapsis. Sampling just there keeps
        // the samples close enough to catch a narrow ridge, which samples spread over a whole lap
        // (about 245 m apart on a lap of 63 km) would step over.
        var top = _body.SeaRadius + _body.Map.MaxHeight + DetailMargin + _clearance;
        var cos = (_p / top - 1) / Math.Max(_e, 1e-12);
        if (cos >= 1)
            return null; // never that low
        if (cos <= -1)
            return FirstInTerrain(_now, limit); // all of it is that low (a hop, or a low orbit): from the start
        var half = Math.Acos(cos);
        var first = (int)Math.Ceiling((_now - half) / (2 * Math.PI));
        var last = (int)Math.Floor((limit + half) / (2 * Math.PI));
        for (var lap = first; lap <= last; lap++)
        {
            var from = Math.Max(_now, lap * 2 * Math.PI - half);
            var to = Math.Min(limit, lap * 2 * Math.PI + half);
            if (from <= to && FirstInTerrain(from, to) is { } hit)
                return hit;
        }
        return null;
    }

    /// <summary>The first place from <paramref name="from"/> to <paramref name="to"/> that is in the terrain (to about half a metre), or null.</summary>
    private double? FirstInTerrain(double from, double to)
    {
        // Starting in the ground: the path is nothing but where it starts.
        if (InTerrain(from))
            return from;
        // 256 steps, or more where the stretch is long enough that they would be over 30 m apart
        // (its length is found from a few chords along it).
        const int Chords = 8;
        var length = 0.0;
        var previous = At(from);
        for (var i = 1; i <= Chords; i++)
        {
            var next = At(from + (to - from) * i / Chords);
            length += Vector3.Distance(previous, next);
            previous = next;
        }
        var steps = Math.Max(256, (int)Math.Ceiling(length / 30.0));
        var above = from;
        (int, float, float)? aboveFace = null; // where on the map `above` is, once worked out
        for (var chunk = 0; chunk < steps; chunk += ChunkSteps)
        {
            var last = Math.Min(chunk + ChunkSteps, steps);
            var chunkEnd = from + (to - from) * last / steps;
            if (ClearOfTerrain(above, ref aboveFace, chunkEnd))
            {
                above = chunkEnd;
                continue;
            }
            for (var i = chunk + 1; i <= last; i++)
            {
                var at = from + (to - from) * i / steps;
                if (InTerrain(at))
                {
                    // Between the last sample above the ground and this one in it: bisect.
                    var under = at;
                    while ((under - above) * Distance(under) > 0.5)
                    {
                        var mid = (above + under) / 2;
                        if (InTerrain(mid))
                            under = mid;
                        else
                            above = mid;
                    }
                    return under;
                }
                above = at;
            }
        }
        return null;
    }

    private const int ChunkSteps = 16; // samples tested together by ClearOfTerrain: about 500 m of path at 30 m

    /// <summary>
    /// Whether the path from <paramref name="from"/> to <paramref name="to"/> (a few hundred metres)
    /// is surely above the ground under it, without sampling the ground: its lowest point against
    /// the highest the map gets (<see cref="TerrainMap.Range"/>) over the patch of face it crosses,
    /// plus the fine detail's few metres. Most of a low path is over ground far under it, so this
    /// leaves only the stretches near the ground to sample.
    /// </summary>
    private bool ClearOfTerrain(double from, ref (int Face, float U, float V)? start, double to)
    {
        // The distance from the centre only falls towards the periapsis and rises after it, so
        // the lowest is at an end, unless the periapsis is between them.
        var lowest = Math.Min(Distance(from), Distance(to));
        if (Math.Floor(from / (2 * Math.PI)) < Math.Floor(to / (2 * Math.PI)))
            lowest = PeriapsisDistance;
        var (faceA, uA, vA) = start ??= CubeSphere.ToFace(_body.Up(GroundAt(from)));
        var end = CubeSphere.ToFace(_body.Up(GroundAt(to)));
        start = end; // the next stretch starts here, if this one is clear
        var (faceB, uB, vB) = end;
        if (faceA != faceB)
            return false; // across an edge of the map: sample it
        // Among the stars, on a face the path is a straight line in the plane of the cube, but the
        // ground turns under it meanwhile, so over the ground it bends a little: the patch round its
        // ends, its middle and its quarter points covers it.
        var (uLow, vLow, uHigh, vHigh) = (MathF.Min(uA, uB), MathF.Min(vA, vB), MathF.Max(uA, uB), MathF.Max(vA, vB));
        for (var i = 1; i < 4; i++)
        {
            var (face, u, v) = CubeSphere.ToFace(_body.Up(GroundAt(from + (to - from) * i / 4)));
            if (face != faceA)
                return false;
            (uLow, vLow, uHigh, vHigh) = (MathF.Min(uLow, u), MathF.Min(vLow, v), MathF.Max(uHigh, u), MathF.Max(vHigh, v));
        }
        var size = MathF.Max(uHigh - uLow, vHigh - vLow);
        var mapHighest = _body.Map.Range(faceA, uLow, vLow, size).Max;
        var highest = (_body.HasSea ? Math.Max(mapHighest, 0f) : mapHighest) + DetailMargin + _clearance;
        return lowest > _body.SeaRadius + highest;
    }

    private const float DetailMargin = 10f; // the fine detail on top of the map's height reaches no more than a few metres

    /// <summary>Whether the point at <paramref name="anomaly"/> is in the terrain (or under the sea's surface), as the ground will be by then.</summary>
    private bool InTerrain(double anomaly) => Distance(anomaly) < _body.SeaRadius + TerrainUnder(anomaly);

    /// <summary>The height of the terrain (or the calm sea) under the path at <paramref name="anomaly"/>, by the time it is there (<see cref="GroundAt"/>).</summary>
    private float TerrainUnder(double anomaly) => SurfaceHeight(_body.Up(GroundAt(anomaly))) + _clearance;

    /// <summary>
    /// For a straight up and down path that falls back: how long from now until it is down to
    /// <paramref name="ground"/> from the centre, from <paramref name="distance"/>. Falling from rest at
    /// the top (<c>R</c>), it takes √(R³/2μ)·(√(x(1−x)) + acos √x) to reach x·R.
    /// </summary>
    private double RadialSeconds(double distance, double ground) =>
        _rising ? FromTop(distance) + FromTop(ground) : Math.Max(0, FromTop(ground) - FromTop(distance));

    /// <summary>For a straight up and down path that falls back: how long it takes to fall from its top to <paramref name="distance"/> from the centre (see <see cref="RadialSeconds"/>).</summary>
    private double FromTop(double distance)
    {
        var top = _top;
        var u = Math.Clamp(distance / top, 0, 1);
        return Math.Sqrt(top * top * top / (2 * _body.Mu)) * (Math.Sqrt(u * (1 - u)) + Math.Acos(Math.Sqrt(u)));
    }

    /// <summary>
    /// For a straight up and down path still rising, that falls back: how long from now until it
    /// is at its top (the ground has turned that much more under it by then). Null otherwise
    /// (falling already, or escaping, with no top it reaches).
    /// </summary>
    public double? TopSeconds => _radial && _rising && _top < MaxRange ? FromTop((_position - _body.Centre).Length()) : null;

    /// <summary>The mean anomaly at true anomaly <paramref name="anomaly"/>, counted on past a lap as it is.</summary>
    private double Mean(double anomaly)
    {
        if (_e < 1)
        {
            var laps = Math.Floor((anomaly + Math.PI) / (2 * Math.PI));
            var wrapped = anomaly - laps * 2 * Math.PI;
            var eccentric = 2 * Math.Atan2(Math.Sqrt(1 - _e) * Math.Sin(wrapped / 2), Math.Sqrt(1 + _e) * Math.Cos(wrapped / 2));
            return eccentric - _e * Math.Sin(eccentric) + laps * 2 * Math.PI;
        }
        var hyperbolic = 2 * Math.Atanh(Math.Sqrt((_e - 1) / (_e + 1)) * Math.Tan(anomaly / 2));
        return _e * Math.Sinh(hyperbolic) - hyperbolic;
    }

    /// <summary>The true anomaly at mean anomaly <paramref name="mean"/>: Kepler's equation, solved by Newton's method.</summary>
    private double True(double mean)
    {
        if (_e < 1)
        {
            var laps = Math.Floor((mean + Math.PI) / (2 * Math.PI));
            var m = mean - laps * 2 * Math.PI;
            var eccentric = _e < 0.8 ? m : Math.PI * Math.Sign(m);
            for (var i = 0; i < 30; i++)
            {
                var step = (eccentric - _e * Math.Sin(eccentric) - m) / (1 - _e * Math.Cos(eccentric));
                eccentric -= step;
                if (Math.Abs(step) < 1e-12)
                    break;
            }
            return 2 * Math.Atan2(Math.Sqrt(1 + _e) * Math.Sin(eccentric / 2), Math.Sqrt(1 - _e) * Math.Cos(eccentric / 2)) + laps * 2 * Math.PI;
        }
        var hyperbolic = Math.Asinh(mean / _e);
        for (var i = 0; i < 50; i++)
        {
            var step = (_e * Math.Sinh(hyperbolic) - hyperbolic - mean) / (_e * Math.Cosh(hyperbolic) - 1);
            hyperbolic -= step;
            if (Math.Abs(step) < 1e-12)
                break;
        }
        return 2 * Math.Atan(Math.Sqrt((_e + 1) / (_e - 1)) * Math.Tanh(hyperbolic / 2));
    }

    private static Vector3Double ToDouble(Vector3 v) => v;
    private static Vector3 ToSingle(Vector3Double v) => v.ToSingle();
    private static Vector3Double Cross(Vector3Double a, Vector3Double b) => Vector3Double.Cross(a, b);
}
