using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.Numerics;
using Brutal.OpenAlApi;

namespace GameOne.Audio;

/// <summary>
/// The booster's engine: a synthesised roar played through OpenAL (BRUTAL's <c>Brutal.OpenAl</c>,
/// i.e. OpenAL Soft) from the nozzle, so it moves with the booster.
/// </summary>
/// <remarks>
/// <para>
/// The roar is three layers of noise: a deep rumble you feel more than hear, a broad roaring band,
/// and a crackle of sharp pops, which is what makes a big rocket engine sound like one. All three
/// grow with the throttle, the crackle most of all, and they fade out quickly when it cuts.
/// </para>
/// <para>
/// OpenAL handles loudness and stereo position (and a little Doppler). The synthesis handles
/// distance too: a low-pass filter closes as the camera pulls back, so far off you hear only the
/// rumble. The thinning air muffles it too: in space it would be silent, but a muffled rumble is
/// kept, as if felt through the rocket. The sound is generated in small chunks and queued, rather
/// than being a looped file, so it can follow the throttle smoothly and there is no audio asset to
/// source or license.
/// </para>
/// </remarks>
internal sealed class EngineSound : IDisposable
{
    private const int SampleRate = 48000;
    private const int ChunkFrames = 1024;  // ~21 ms per buffer
    private const int ChunkCount = 6;      // ~128 ms queued: survives a slow frame without gaps

    private readonly AlSource _source;
    private readonly AlBuffer[] _buffers = [];
    private readonly short[] _pcm = new short[ChunkFrames];

    // Targets set each frame, followed smoothly per sample so changes never click.
    private float _targetThrottle, _targetClarity, _targetAir;
    private float _throttle, _clarity, _air = 1f;

    // Synth state.
    private uint _noise = 0x9E3779B9;
    private float _brown, _roarLow, _roarBand, _crackle, _crackleHp, _clarityLp;

    private bool Enabled { get; }

    public EngineSound(AudioSystem audio)
    {
        if (!audio.Enabled)
            return;

        _source = Al.GenSource();
        _source.ReferenceDistance = 25f;
        _source.RolloffFactor = 1f;
        _buffers = Al.GenBuffers(ChunkCount);
        foreach (var buffer in _buffers)
            Fill(buffer);
        _source.QueueBuffers(_buffers);
        _source.Play();
        Enabled = true;
    }

    /// <summary>
    /// Moves the source to the nozzle and tops up the queue with freshly synthesised audio.
    /// Call once per frame, after <see cref="AudioSystem.UpdateListener"/>.
    /// </summary>
    /// <param name="throttle">How hard the engine is firing, 0 (off) to 1.</param>
    /// <param name="air">How thick the air is round the engine, 1 at the surface to 0 in space.</param>
    public void Update(Vector3 nozzle, Vector3 velocity, Vector3 camera, float throttle, float air)
    {
        if (!Enabled)
            return;

        _source.Position = AudioSystem.ToFloat3(nozzle);
        _source.Velocity = AudioSystem.ToFloat3(velocity);
        _targetThrottle = throttle;
        _targetAir = MathF.Sqrt(Math.Clamp(air, 0f, 1f)); // the sound carries until the air is very thin
        _targetClarity = Math.Clamp(1f - (Vector3.Distance(nozzle, camera) - 20f) / 250f, 0f, 1f) * _targetAir;

        // Refill every buffer the source has finished with.
        Span<AlBuffer> done = stackalloc AlBuffer[1];
        for (var processed = _source.BuffersProcessed; processed > 0; processed--)
        {
            _source.UnqueueBuffers(done);
            Fill(done[0]);
            _source.QueueBuffers(done);
        }

        // If a long stall (e.g. dragging the window) drained the queue, the source stops: restart it.
        if (_source.State != AlSourceState.Playing)
            _source.Play();
    }

    /// <summary>Synthesises one chunk of mono 16-bit audio into <paramref name="buffer"/>.</summary>
    private void Fill(AlBuffer buffer)
    {
        const float dt = 1f / SampleRate;
        // Per-sample smoothing towards the targets: ~60 ms, quick enough for a crisp cut-off.
        var follow = 1f - MathF.Exp(-dt / 0.06f);

        for (var i = 0; i < _pcm.Length; i++)
        {
            _throttle += (_targetThrottle - _throttle) * follow;
            _clarity += (_targetClarity - _clarity) * follow;
            _air += (_targetAir - _air) * follow;

            var white = NextNoise();

            // Rumble: brown noise (integrated white noise), deep and shapeless.
            _brown = 0.997f * _brown + 0.05f * white;

            // Roar: noise with the lows and the highs taken out, leaving a broad band.
            _roarLow += 0.25f * (white - _roarLow);
            _roarBand += 0.03f * (_roarLow - _roarBand);
            var roar = _roarLow - _roarBand;

            // Crackle: sparse, random sharp pops, more of them (and louder) at higher throttle.
            if ((_noise & 0xFFFF) < 400 * _throttle * _throttle)
                _crackle = (NextNoise() > 0 ? 1f : -1f) * (0.5f + 0.5f * _throttle);
            _crackle *= 0.86f;
            _crackleHp += 0.3f * (_crackle - _crackleHp);
            var crackle = _crackle - _crackleHp;

            var mix = 1.1f * _brown + (0.4f + 0.8f * _throttle) * roar + 1.2f * crackle;

            // Distance: a one-pole low-pass from ~250 Hz (far off) to ~6 kHz (close by).
            var cutoffHz = 250f + 5750f * _clarity * _clarity;
            var a = 1f - MathF.Exp(-MathF.Tau * cutoffHz * dt);
            _clarityLp += a * (mix - _clarityLp);

            var level = _throttle * (0.5f + 0.5f * _throttle) * (0.3f + 0.7f * _air);
            var sample = MathF.Tanh(_clarityLp * level * 1.4f); // soft clip, never harsh
            _pcm[i] = (short)(sample * 30000f);
        }

        buffer.SetData(AlFormat.Mono16, MemoryMarshal.AsBytes(_pcm.AsSpan()), SampleRate);
    }

    /// <summary>Uniform noise in [-1, 1) from a xorshift generator: cheap and allocation-free.</summary>
    private float NextNoise()
    {
        _noise ^= _noise << 13;
        _noise ^= _noise >> 17;
        _noise ^= _noise << 5;
        return _noise / (float)uint.MaxValue * 2f - 1f;
    }

    public void Dispose()
    {
        if (!Enabled)
            return;

        _source.Stop();
        _source.Delete();
        foreach (var buffer in _buffers)
            buffer.Delete();
    }
}
