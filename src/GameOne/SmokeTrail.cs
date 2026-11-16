using System.Numerics;

namespace GameOne;

/// <summary>
/// The smoke an engine leaves behind, which lingers: a trail of puffs along its path through the
/// thick air, each swelling, thinning and drifting off on the breeze over a minute and a half; and,
/// where the exhaust reaches the ground (on the pad, at lift-off or a landing), low, wide puffs
/// that pour out across it in every direction and pile up into a billowing bank. Every engine in
/// the world makes it, more with more throttle and thicker air, and none above about 2 km.
/// </summary>
/// <remarks>
/// Purely visual. Each puff is placed where it was made and then worked out from its age, so the
/// only state is the puffs themselves (at most <see cref="MaxPuffs"/>: the oldest make way). The
/// trail is laid along the path the nozzle took since the last frame, not just where it is now, so
/// a fast rocket (or time warp) leaves an unbroken line. It is the planet's air's smoke, all of it in the
/// scene's frame (<see cref="BodyFrames.Scene"/>, the planet's ground frame), so an engine is given to
/// it there wherever it is; a vessel at another body (whose air, if any, isn't the trail's) makes none.
/// </remarks>
internal sealed class SmokeTrail
{
    public const int MaxPuffs = 1400;

    private const float TrailSeconds = 90f, GroundSeconds = 70f;
    private const float TrailSpacing = 2.5f;      // metres between trail puffs along the path
    // Or seconds, when it is hardly moving (hovering): not often, as puffs piled up in one place
    // only thicken it a little but cost a lot to draw, every one over the same pixels.
    private const float TrailEvery = 2f;
    private const float FadeFrom = 1500f, FadeTo = 2000f; // altitude: no trail above this
    private const float GroundReach = 45f;        // how far below the nozzle the exhaust still reaches the ground
    private const float GroundPuffsPerSecond = 22f;

    private struct Puff
    {
        public Vector3 Start, Push, Up;
        public double Born;
        public float Life, StartRadius, EndRadius, Opacity, Seed, Ground, PushSeconds, Rise, Squash;
    }

    private readonly Puff[] _puffs = new Puff[MaxPuffs];
    private int _next, _count;
    private readonly SmokeCloud[] _draws = new SmokeCloud[MaxPuffs];
    private int _drawCount;
    private readonly Dictionary<Vessel, (Vector3 Nozzle, double Time)> _last = [];
    private readonly Random _random = new(11);

    /// <summary>This frame's puffs, for the renderer (drawn as smoke clouds).</summary>
    public ReadOnlySpan<SmokeCloud> Clouds => _draws.AsSpan(0, _drawCount);

    /// <summary>How many puffs there are, for the tests.</summary>
    public int Count => _count;

    public void Clear()
    {
        _count = _next = 0;
        _drawCount = 0;
        _last.Clear();
    }

    /// <summary>
    /// Makes smoke from <paramref name="vessel"/>'s engine, at <paramref name="time"/> (game seconds),
    /// <paramref name="dt"/> after the last frame: <paramref name="nozzle"/> and <paramref name="exhaust"/>
    /// (the way the exhaust goes) as it is now, at <paramref name="throttle"/> (0 when it isn't firing).
    /// Call for each vessel each frame, then <see cref="Update"/>. Only a vessel at the scene's body
    /// makes any, and only by that body's air: the trail's places are the scene's frame, whose air it reads.
    /// </summary>
    public void Emit(Vessel vessel, Vector3 nozzle, Vector3 exhaust, float throttle, float bellRadius, double time, float dt)
    {
        var body = vessel.Body;
        var altitude = body.Altitude(nozzle); // the air's, which thins with height above the sea, not the ground
        var strength = body == BodyFrames.Scene && body.HasAir
            ? throttle * body.AirDensity(altitude) * (1f - SmoothStep(FadeFrom, FadeTo, altitude))
            : 0f;
        if (strength < 0.01f)
        {
            _last.Remove(vessel); // a new trail starts when it fires again
            return;
        }

        // Along the path since the last frame, a puff every few metres (or every so often, hovering).
        if (!_last.TryGetValue(vessel, out var last) || Vector3.Distance(last.Nozzle, nozzle) > 400f)
            last = (nozzle, time - TrailEvery); // first frame (or a jump): one puff here
        var path = nozzle - last.Nozzle;
        var length = path.Length();
        var count = Math.Clamp((int)MathF.Max(length / TrailSpacing, (float)(time - last.Time) / TrailEvery), 0, 80);
        for (var i = 1; i <= count; i++)
        {
            var f = (float)i / count;
            var at = last.Nozzle + path * f;
            var up = Planet.Up(at);
            Add(new Puff
            {
                Start = at + exhaust * (bellRadius * 1.5f),
                Push = exhaust * 6f + Jitter(1.5f),
                PushSeconds = 2f,
                Up = up,
                Born = float.Lerp((float)last.Time, (float)time, f),
                Life = TrailSeconds * (0.8f + 0.4f * (float)_random.NextDouble()),
                StartRadius = 2f + bellRadius * 3f,
                EndRadius = 20f + 10f * (float)_random.NextDouble(),
                Opacity = 0.8f * MathF.Min(1f, strength * 1.3f),
                Seed = (float)_random.NextDouble() * 100f,
                // The ground under it, hills and the calm sea included (worked out once, as it is made).
                Ground = Effects.GroundRadius(at),
                Rise = 0.25f,
                Squash = 0.85f,
            });
        }
        if (count > 0)
            _last[vessel] = (nozzle, time);

        // Where the exhaust hits the ground: smoke pouring out across it, thicker the closer it is.
        // The ground is the terrain's (as for SmokeStirs), so a landing on a plateau or the sea makes it too.
        // (On a tilted test pad, measured along the top's normal.)
        var localUp = Planet.Up(nozzle);
        var (clearance, normal) = Planet.Surface(nozzle);
        var down = -Vector3.Dot(exhaust, normal);
        if (down > 0.4f && clearance < GroundReach * down)
        {
            var reach = MathF.Max(clearance, 0f) / down;
            // Where the exhaust meets the ground, taken as flat under the nozzle, then put on the
            // ground there (along up, or a tilted pad's normal), as on a slope that isn't quite where the line ends.
            var hit = nozzle + exhaust * reach;
            var (above, under) = Planet.Surface(hit);
            var impact = hit - under * above;
            var ground = Vector3.Distance(impact, Planet.Centre);
            var close = 1f - reach / GroundReach;
            var puffs = Math.Min(30, (int)MathF.Floor(GroundPuffsPerSecond * throttle * close * dt + (float)_random.NextDouble()));
            for (var i = 0; i < puffs; i++)
            {
                // Out along the ground in any direction, fast at first, slowing as it spreads.
                var angle = (float)_random.NextDouble() * MathF.Tau;
                var east = Planet.East(impact);
                var north = Planet.North(impact);
                var outward = east * MathF.Cos(angle) + north * MathF.Sin(angle);
                Add(new Puff
                {
                    Start = impact + localUp * 1.5f,
                    Push = outward * (14f + 18f * (float)_random.NextDouble()) + localUp * 1.5f,
                    PushSeconds = 3f,
                    Up = localUp,
                    Born = time - _random.NextDouble() * dt,
                    Life = GroundSeconds * (0.7f + 0.6f * (float)_random.NextDouble()),
                    StartRadius = 3f,
                    EndRadius = 13f + 7f * (float)_random.NextDouble(),
                    Opacity = 0.6f * MathF.Min(1f, throttle * close * 1.5f),
                    Seed = (float)_random.NextDouble() * 100f,
                    Ground = ground,
                    Rise = 0.12f,
                    Squash = 0.55f,
                });
            }
        }
    }

    /// <summary>Ages every puff to <paramref name="time"/> and lays out this frame's, dropping those that have thinned away.</summary>
    public void Update(double time)
    {
        _drawCount = 0;
        for (var i = 0; i < _count; i++)
        {
            ref var puff = ref _puffs[i];
            var age = (float)(time - puff.Born);
            if (age < 0f || age > puff.Life)
                continue;
            var life = age / puff.Life;
            var grow = 1f - MathF.Exp(-age / (puff.Life * 0.06f)); // most of its swelling in the first few seconds
            var centre = puff.Start
                + puff.Push * (puff.PushSeconds * (1f - MathF.Exp(-age / puff.PushSeconds)))
                + puff.Up * (puff.Rise * age)
                + SmokeStirs.WindAt(puff.Start) * age;
            var opacity = puff.Opacity * MathF.Min(1f, age / 0.4f) * MathF.Pow(1f - life, 1.5f);
            _draws[_drawCount++] = new SmokeCloud(
                centre,
                float.Lerp(puff.StartRadius, puff.EndRadius, grow),
                opacity,
                puff.Seed,
                Scene.SmokeColor,
                puff.Ground,
                puff.Squash);
        }
    }

    private void Add(in Puff puff)
    {
        _puffs[_next] = puff;
        _next = (_next + 1) % MaxPuffs;
        _count = Math.Min(_count + 1, MaxPuffs);
    }

    private Vector3 Jitter(float size) => new(
        size * (2f * (float)_random.NextDouble() - 1f),
        size * (2f * (float)_random.NextDouble() - 1f),
        size * (2f * (float)_random.NextDouble() - 1f));

    private static float SmoothStep(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
