using System.Numerics;

namespace GameOne;

/// <summary>
/// Somewhere the smoke is being pushed about: smoke within about <see cref="Radius"/> of
/// <see cref="Position"/> is shoved outwards, carried <see cref="Drag"/> along, and curled into an eddy.
/// </summary>
/// <param name="Strength">0..1, how hard; the sign is which way the eddy turns.</param>
internal readonly record struct SmokeStir(Vector3 Position, float Radius, Vector3 Drag, float Strength);

/// <summary>
/// How the booster stirs the smoke on the ground, and how thick that smoke is. Its body pushes the
/// smoke aside where it passes through and leaves a wake of eddies behind it. Its exhaust, where it
/// reaches the ground, blasts the smoke outwards in rings and thickens it into a billowing cloud,
/// which thins away slowly once the engine is off or has climbed away. An explosion throws it out
/// in one great ring.
/// </summary>
/// <remarks>
/// Purely visual: the smoke has no effect on anything. The smoke itself is drawn from the time alone
/// (see VulkanRenderer.Smoke.cs), and these stirs are applied on top in its shader.
/// </remarks>
internal sealed class SmokeStirs
{
    /// <summary>The most stirs the smoke shader takes. Must match <c>MAX_STIRS</c> in <c>Shaders.FrameResources</c>.</summary>
    public const int MaxStirs = 96;

    private sealed class Eddy
    {
        public Vector3 Position, Velocity;
        public float Age, Life, StartRadius, EndRadius, StartStrength;
        public float Spin; // +1 or -1
        public float Strength => StartStrength * MathF.Pow(1f - Math.Min(Age / Life, 1f), 2f);
        public float Radius => float.Lerp(StartRadius, EndRadius, 1f - MathF.Exp(-3f * Age / Life));
    }

    private readonly List<Eddy> _eddies = [];
    private readonly SmokeStir[] _stirs = new SmokeStir[MaxStirs];
    private int _count;
    private float _nextSpin = 1f;

    // The booster is tracked as a row of points up its length, each a little wider than it is, so
    // whatever part of it is in the smoke pushes it about.
    private const int BodyPoints = 8;
    private const float BodyPointRadius = Scene.RocketRadius * 1.8f;
    private readonly Vector3[] _bodyLast = new Vector3[BodyPoints];
    private readonly float[] _bodySinceWake = new float[BodyPoints];
    private bool _bodyTracked;

    // The exhaust: a ring thrown out from where it hits the ground this often, while it reaches.
    private const float JetRingSeconds = 0.12f;
    private float _sinceJetRing;

    /// <summary>This frame's stirs, for the renderer.</summary>
    public ReadOnlySpan<SmokeStir> Stirs => _stirs.AsSpan(0, _count);

    private readonly float[] _thickness = LaunchSites.SmokeSites.Select(_ => Scene.SmokeHaze).ToArray();

    /// <summary>
    /// How thick the smoke is at each of <see cref="LaunchSites.SmokeSites"/>, from <see cref="Scene.SmokeHaze"/>
    /// (a faint haze) to 1 (a dense cloud).
    /// </summary>
    public ReadOnlySpan<float> Thickness => _thickness;

    /// <summary>
    /// The breeze the smoke drifts on at <paramref name="p"/>, along the ground there: <see cref="Scene.SmokeWind"/>
    /// is east and south (Home's x and z), so it blows the same way across the ground wherever it is.
    /// </summary>
    public static Vector3 WindAt(Vector3 p) => Planet.East(p) * Scene.SmokeWind.X - Planet.North(p) * Scene.SmokeWind.Y;

    /// <summary>Forgets the booster's last position, e.g. when it has jumped back to the pad.</summary>
    public void Reset()
    {
        _bodyTracked = false;
        _eddies.Clear();
    }

    /// <param name="rocketModel">The rocket's transform, or null once it has blown up.</param>
    /// <param name="rocketHeight">How tall the rocket is, from the bottom of its body.</param>
    /// <param name="nozzle">The bottom of the engine bell, in the scene's frame (<see cref="BodyFrames.Scene"/>, the planet's, whose smoke this is), as the rocket's model is: at another body it is far from any smoke, and stirs none.</param>
    /// <param name="exhaust">Which way the exhaust points.</param>
    /// <param name="throttle">How hard the engine is firing, 0 when it isn't.</param>
    public void Update(float dt, Matrix4x4? rocketModel, float rocketHeight, Vector3 nozzle, Vector3 exhaust, float throttle)
    {
        // The eddies age, spread, slow and drift off with the breeze (along the ground where each is).
        foreach (var eddy in _eddies)
        {
            eddy.Age += dt;
            eddy.Velocity *= MathF.Exp(-2.5f * dt);
            var wind = WindAt(eddy.Position);
            eddy.Position += (wind + eddy.Velocity * 0.3f) * dt;
        }
        _eddies.RemoveAll(e => e.Age >= e.Life);

        _count = 0;
        if (rocketModel is { } model)
        {
            for (var i = 0; i < BodyPoints; i++)
            {
                var position = Vector3.Transform(new Vector3(0, rocketHeight * i / (BodyPoints - 1), 0), model);
                var last = _bodyTracked ? _bodyLast[i] : position;
                _bodyLast[i] = position;
                if (Planet.SurfaceAltitude(position) - BodyPointRadius > Scene.SmokeHeight)
                    continue; // this part of the rocket is clear of the smoke
                Push(position, last, dt, BodyPointRadius, ref _bodySinceWake[i]);
            }
            _bodyTracked = true;
        }

        var (blast, hit) = Jet(dt, nozzle, exhaust, throttle);

        // The exhaust thickens the smoke at a pad while it reaches the ground near it; then it slowly thins again.
        for (var site = 0; site < _thickness.Length; site++)
        {
            var near = blast > 0 && Vector3.Distance(hit, LaunchSites.SmokeSites[site]) < Scene.SmokeSiteReach; // both on the ground there
            _thickness[site] = near
                ? MathF.Min(1f, _thickness[site] + blast * dt / Scene.SmokeBuildSeconds)
                : _thickness[site] - (_thickness[site] - Scene.SmokeHaze) * MathF.Min(1f, dt / Scene.SmokeClearSeconds);
        }

        // The freshest eddies first, in case there are more than the shader takes.
        for (var i = _eddies.Count - 1; i >= 0 && _count < MaxStirs; i--)
        {
            var eddy = _eddies[i];
            Add(new SmokeStir(eddy.Position, eddy.Radius, ClampLength(eddy.Velocity * 0.15f, eddy.Radius), eddy.Strength * eddy.Spin));
        }
    }

    /// <summary>
    /// The exhaust, if it reaches the ground: a strong push outwards where it hits, and every so often
    /// a ring of smoke thrown out from there. Returns how hard it is hitting the ground (0..1), and where.
    /// </summary>
    private (float Strength, Vector3 Hit) Jet(float dt, Vector3 nozzle, Vector3 exhaust, float throttle)
    {
        _sinceJetRing += dt;
        var (height, normal) = Planet.Surface(nozzle); // on a tilted test pad, along the top's normal
        var down = -Vector3.Dot(exhaust, normal);
        if (throttle <= 0f || down < 0.2f)
            return default; // off, or pointing too far sideways to reach the ground

        // Follow the exhaust down to the ground (or the pad, if that's what it hits). Close by, the
        // ground is near enough flat.
        var distance = height / down;
        if (distance > Scene.JetReach)
            return default;
        var hit = nozzle + exhaust * distance;

        var strength = throttle * (1f - distance / Scene.JetReach);
        var up = Planet.Up(hit);
        Add(new SmokeStir(hit + up * 2f, 5f + distance * 0.3f, Vector3.Zero, strength));
        if (_sinceJetRing >= JetRingSeconds)
        {
            _sinceJetRing = 0f;
            AddEddy(new Eddy
            {
                Position = hit + up * 1.5f,
                Velocity = up * (8f * strength),
                Life = Scene.SmokeWakeSeconds,
                StartRadius = 5f,
                EndRadius = 18f + 20f * strength,
                StartStrength = 0.4f + 0.6f * strength,
            });
        }
        return (strength, hit);
    }

    /// <summary>A blast at <paramref name="at"/>: the smoke thrown out in one great ring, and thickened.</summary>
    public void Blast(Vector3 at)
    {
        // On the ground under it (wherever on the planet: not at the world's Y = 0, which is Home's ground).
        var up = Planet.Up(at);
        AddEddy(new Eddy
        {
            Position = at + up * (2f - Planet.SurfaceAltitude(at)),
            Velocity = up * 15f,
            Life = Scene.SmokeWakeSeconds * 1.6f,
            StartRadius = 8f,
            EndRadius = 60f,
            StartStrength = 1f,
        });
        for (var site = 0; site < _thickness.Length; site++)
            if (Vector3.Distance(at, LaunchSites.SmokeSites[site]) < Scene.SmokeSiteReach)
                _thickness[site] = MathF.Max(_thickness[site], 0.6f);
    }

    /// <summary>
    /// Something <paramref name="radius"/> across, now at <paramref name="position"/> and last frame at
    /// <paramref name="last"/>, pushing through the smoke: it parts the smoke round it even when
    /// still, and more so when it moves, and leaves a wake each time it has moved <see cref="Scene.SmokeWakeSpacing"/>.
    /// </summary>
    private void Push(Vector3 position, Vector3 last, float dt, float radius, ref float sinceWake)
    {
        var velocity = dt > 0 ? (position - last) / dt : Vector3.Zero;
        var speed = velocity.Length();
        var push = Math.Clamp(speed / Scene.SmokeStirFullSpeed, 0f, 1f);
        Add(new SmokeStir(position, radius, ClampLength(velocity * 0.1f, radius), 0.4f + 0.6f * push));

        sinceWake += Vector3.Distance(position, last);
        if (speed > Scene.SmokeStirMinSpeed && sinceWake >= Scene.SmokeWakeSpacing)
        {
            sinceWake = 0f;
            AddEddy(new Eddy
            {
                Position = position,
                Velocity = velocity,
                Life = Scene.SmokeWakeSeconds,
                StartRadius = radius * 1.15f,
                EndRadius = radius * 3.2f,
                StartStrength = 0.4f + 0.6f * push,
            });
        }
    }

    private void AddEddy(Eddy eddy)
    {
        // Neighbouring eddies turn opposite ways, as in a real wake.
        eddy.Spin = _nextSpin;
        _nextSpin = -_nextSpin;

        // Keep within what the shader can take (less the booster and its exhaust themselves): when
        // full, the faintest makes way, which is all but gone anyway.
        if (_eddies.Count >= MaxStirs - BodyPoints - 1)
        {
            var faintest = 0;
            for (var i = 1; i < _eddies.Count; i++)
                if (_eddies[i].Strength < _eddies[faintest].Strength)
                    faintest = i;
            _eddies.RemoveAt(faintest);
        }
        _eddies.Add(eddy);
    }

    private void Add(SmokeStir stir)
    {
        if (_count < MaxStirs)
            _stirs[_count++] = stir;
    }

    private static Vector3 ClampLength(Vector3 v, float max)
    {
        var length = v.Length();
        return length > max ? v * (max / length) : v;
    }
}
