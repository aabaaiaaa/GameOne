using System.Numerics;

namespace GameOne;

/// <summary>A ball of fire to draw: glowing, see-through and additive.</summary>
/// <param name="Heat">1 white-hot .. 0 cooled to a dull red.</param>
internal readonly record struct Fireball(Vector3 Centre, float Radius, float Brightness, float Heat, float Seed);

/// <summary>A cloud of smoke to draw (from an explosion, or an engine's trail), using the floor smoke's look.</summary>
/// <param name="Ground">How far the ground below it is from the planet's centre: it fades into the ground there (0: none under it to fade into, as at another body).</param>
/// <param name="Squash">How tall it is for its width: less than 1 is a flatter, spreading puff.</param>
internal readonly record struct SmokeCloud(Vector3 Centre, float Radius, float Opacity, float Seed, Vector3 Color, float Ground, float Squash = 0.85f);

/// <summary>
/// Explosions (the rocket, or a dropped stage hitting the ground): a cluster of fireballs that
/// swell, rise and cool, a flash that lights up everything around, and a pall of dark smoke that
/// billows up and drifts off on the breeze. And splashes (something hitting the water): a burst of
/// white spray, thrown up and out, falling back and thinning as it spreads. All of it is worked out
/// from the time since each explosion or splash, so there is nothing to simulate. Each is kept in
/// the frame of the body it happened at, and drawn in the scene's (<see cref="BodyFrames.Scene"/>),
/// so one on the moon stays where it was on the moon's ground however the moon moves. At a body
/// with no air an explosion is its flash and fireballs alone, which don't rise: no pall of smoke.
/// </summary>
internal sealed class Effects
{
    private const float FireballSeconds = 1.8f;
    private const float CloudSeconds = 16f;
    private const float SpraySeconds = 3.5f;

    private readonly record struct Burst(Vector3 Offset, float Delay, float MaxRadius, float Seed);

    private readonly Burst[] _fireballs;
    private readonly Burst[] _clouds;
    private readonly Burst[] _spray;
    private const int MaxExplosions = 4; // the oldest makes way, by which time it has all but gone
    private const int MaxSplashes = 6;   // likewise
    private readonly Fireball[] _fireballDraws;
    private readonly SmokeCloud[] _cloudDraws;
    private int _fireballCount, _cloudCount;
    private readonly List<(GroundBody Body, Vector3 At, double Time, float Ground)> _explosions = [];
    private readonly List<(GroundBody Body, Vector3 At, double Time, float Ground)> _splashes = [];

    public Effects()
    {
        var random = new Random(3);
        float Range(float min, float max) => min + (float)random.NextDouble() * (max - min);
        Vector3 Scatter(float spread) => new(Range(-spread, spread), Range(-spread * 0.6f, spread), Range(-spread, spread));

        _fireballs = new Burst[9];
        for (var i = 0; i < _fireballs.Length; i++)
            _fireballs[i] = new Burst(Scatter(i == 0 ? 0f : 5f), i == 0 ? 0f : Range(0.02f, 0.35f), Range(7f, 13f), Range(0f, 100f));
        _clouds = new Burst[12];
        for (var i = 0; i < _clouds.Length; i++)
            _clouds[i] = new Burst(Scatter(7f), Range(0.15f, 0.9f), Range(13f, 24f), Range(0f, 100f));
        _spray = new Burst[10];
        for (var i = 0; i < _spray.Length; i++)
            _spray[i] = new Burst(Scatter(4f), Range(0f, 0.2f), Range(3f, 7f), Range(0f, 100f));

        _fireballDraws = new Fireball[_fireballs.Length * MaxExplosions];
        _cloudDraws = new SmokeCloud[_clouds.Length * MaxExplosions + _spray.Length * MaxSplashes];
    }

    /// <summary>This frame's fireballs, in the scene's frame.</summary>
    public ReadOnlySpan<Fireball> Fireballs => _fireballDraws.AsSpan(0, _fireballCount);

    /// <summary>This frame's smoke and spray, in the scene's frame.</summary>
    public ReadOnlySpan<SmokeCloud> Clouds => _cloudDraws.AsSpan(0, _cloudCount);

    /// <summary>Where the brightest explosion's flash is (in the scene's frame), and its colour times brightness (zero once they have died away).</summary>
    public (Vector3 Position, Vector3 Light) Flash { get; private set; }

    /// <summary>An explosion at <paramref name="at"/>, in <paramref name="body"/>'s frame.</summary>
    public void Explode(GroundBody body, Vector3 at, double time)
    {
        if (_explosions.Count == MaxExplosions)
            _explosions.RemoveAt(0);
        // The ground under it, hills and the calm sea included, found once rather than every frame for every cloud.
        _explosions.Add((body, at, time, GroundRadius(body, at)));
    }

    /// <summary>A splash at <paramref name="at"/> (in <paramref name="body"/>'s frame), on the water's surface.</summary>
    public void Splash(GroundBody body, Vector3 at, double time)
    {
        if (_splashes.Count == MaxSplashes)
            _splashes.RemoveAt(0);
        _splashes.Add((body, at, time, GroundRadius(body, at)));
    }

    public void Clear()
    {
        _explosions.Clear();
        _splashes.Clear();
    }

    /// <summary>Lays out this frame's fireballs, smoke and spray at <paramref name="time"/>, each in the scene's frame as <paramref name="frames"/> has its body this frame.</summary>
    public void Update(double time, BodyFrames frames)
    {
        _fireballCount = _cloudCount = 0;
        Flash = default;
        Expire(_explosions, time - (CloudSeconds + 2));
        foreach (var (body, at, start, ground) in _explosions)
            Add(body, frames.ToScene(body), at, (float)(time - start), ground);
        Expire(_splashes, time - (SpraySeconds + 1));
        foreach (var (body, at, start, ground) in _splashes)
            AddSpray(body, frames.ToScene(body), at, (float)(time - start), ground);
    }

    /// <summary>How far the ground (or the calm sea) under <paramref name="p"/> is from the planet's centre: what a cloud there fades into (see <see cref="SmokeCloud.Ground"/>).</summary>
    internal static float GroundRadius(Vector3 p) => Vector3.Distance(p, Planet.Centre) - Planet.SurfaceAltitude(p);

    /// <summary>
    /// <see cref="GroundRadius(Vector3)"/> for a place in <paramref name="body"/>'s frame: the smoke
    /// fades into the scene's body's ground (the planet's, the renderer's frame); at another, none.
    /// </summary>
    private static float GroundRadius(GroundBody body, Vector3 p) => body == BodyFrames.Scene ? GroundRadius(p) : 0f;

    /// <summary>Takes out those that began before <paramref name="before"/> (a loop, not RemoveAll, so no delegate is made every frame).</summary>
    private static void Expire(List<(GroundBody Body, Vector3 At, double Time, float Ground)> list, double before)
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (list[i].Time < before)
                list.RemoveAt(i);
    }

    /// <summary>The breeze at <paramref name="p"/> in <paramref name="body"/>'s frame: the smoke's (<see cref="SmokeStirs.WindAt"/>) in air, none without.</summary>
    private static Vector3 Wind(GroundBody body, Vector3 p) => body == BodyFrames.Scene && body.HasAir ? SmokeStirs.WindAt(p) : Vector3.Zero;

    /// <summary>A splash's spray, <paramref name="t"/> seconds on: each puff thrown up and outwards, falling back as it spreads and thins.</summary>
    private void AddSpray(GroundBody body, FrameChange toScene, Vector3 at, float t, float ground)
    {
        var up = body.Up(at);
        foreach (var burst in _spray)
        {
            var age = t - burst.Delay;
            if (age < 0 || age > SpraySeconds)
                continue;
            var life = age / SpraySeconds;
            var grow = 1f - MathF.Exp(-age * 2.5f);
            var across = burst.Offset - up * Vector3.Dot(burst.Offset, up); // thrown out across the water
            var rise = MathF.Max(0f, 9f * age - 4.9f * age * age);          // and up, falling back as water does
            var opacity = MathF.Min(1f, age / 0.08f) * MathF.Pow(1f - life, 1.5f) * 0.85f;
            _cloudDraws[_cloudCount++] = new SmokeCloud(
                toScene.Point(at + across * (0.4f + grow) + up * rise).ToSingle(),
                burst.MaxRadius * (0.35f + 0.65f * grow),
                opacity,
                burst.Seed,
                new Vector3(0.93f, 0.95f, 0.97f),
                ground,
                Squash: 0.7f);
        }
    }

    private void Add(GroundBody body, FrameChange toScene, Vector3 at, float t, float ground)
    {
        var up = body.Up(at);
        var flash = 4f * MathF.Exp(-t * 2.5f);
        if (flash > Flash.Light.X)
            Flash = (toScene.Point(at + up * 4f).ToSingle(), new Vector3(1.0f, 0.70f, 0.40f) * flash);

        // Without air (the moon) there is nothing for hot gas to rise through, and nothing to make a
        // pall of smoke billow: the flash of the burning propellant alone, its fireballs staying
        // where they burst, and no smoke.
        var rises = body.HasAir ? 5f : 0f;
        foreach (var burst in _fireballs)
        {
            var age = t - burst.Delay;
            if (age < 0 || age > FireballSeconds)
                continue;
            var life = age / FireballSeconds;
            var radius = burst.MaxRadius * (1f - MathF.Exp(-age * 4f));
            var flare = MathF.Min(1f, age / 0.06f); // bursts into being, rather than popping
            _fireballDraws[_fireballCount++] = new Fireball(
                toScene.Point(at + burst.Offset + up * (age * rises)).ToSingle(), // hot gas rises, in air
                radius,
                flare * MathF.Pow(1f - life, 1.6f),
                MathF.Exp(-age * 1.8f),
                burst.Seed);
        }
        if (!body.HasAir)
            return;

        var wind = Wind(body, at);
        foreach (var burst in _clouds)
        {
            var age = t - burst.Delay;
            if (age < 0 || age > CloudSeconds)
                continue;
            var life = age / CloudSeconds;
            var grow = 1f - MathF.Exp(-age / 2.5f);
            var rise = 14f * (1f - MathF.Exp(-age / 3f)) + age * 0.6f; // shoots up, then keeps lifting slowly
            var opacity = MathF.Min(1f, age / 0.5f) * MathF.Pow(1f - life, 1.5f) * 0.9f;
            // Black and sooty at first, greying as it thins.
            var color = Vector3.Lerp(new Vector3(0.10f, 0.09f, 0.09f), new Vector3(0.42f, 0.42f, 0.44f), MathF.Min(1f, age / 8f));
            _cloudDraws[_cloudCount++] = new SmokeCloud(
                toScene.Point(at + burst.Offset * (0.5f + grow) + up * rise + wind * age).ToSingle(),
                burst.MaxRadius * (0.3f + 0.7f * grow),
                opacity,
                burst.Seed,
                color,
                ground);
        }
    }
}
