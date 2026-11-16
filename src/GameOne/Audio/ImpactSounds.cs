using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.OpenAlApi;

namespace GameOne.Audio;

/// <summary>
/// The flight's one-off sounds, played in 3D where they happen: the heavy thud of a touchdown (louder
/// the harder it lands), the clank of the legs locking, the boom of an explosion, and the splash of
/// something hitting the water (louder the faster).
/// </summary>
/// <remarks>
/// Like the engine, every sound is synthesised in code at start-up (a few decaying tones plus a
/// burst of noise for the strike), in three slightly different takes per kind so repeats don't sound
/// mechanical. They play on a small pool of OpenAL sources; if every source is busy, the one that
/// has been playing longest is reused.
/// </remarks>
internal sealed class ImpactSounds : IDisposable
{
    private const int SampleRate = 48000;
    private const int VoiceCount = 8;
    private const int TakesPerKind = 3;

    private readonly bool _enabled;
    private readonly AlSource[] _voices = new AlSource[VoiceCount];
    private readonly double[] _voiceStarted = new double[VoiceCount];
    private readonly Dictionary<SoundCue, AlBuffer[]> _takes = [];
    private readonly Random _random = new(99);
    private double _clock;

    public ImpactSounds(AudioSystem audio)
    {
        if (!audio.Enabled)
            return;

        for (var i = 0; i < VoiceCount; i++)
        {
            var voice = _voices[i] = Al.GenSource();
            // Full volume within about the camera's distance from the booster, then a gentle falloff.
            voice.ReferenceDistance = 50f;
            voice.RolloffFactor = 1f;
        }

        foreach (var kind in Enum.GetValues<SoundCue>())
        {
            var takes = new AlBuffer[TakesPerKind];
            for (var t = 0; t < TakesPerKind; t++)
            {
                takes[t] = Al.GenBuffer();
                var pcm = Synthesise(kind, seed: (uint)(t * 7919 + (int)kind * 104729 + 1));
                takes[t].SetData(AlFormat.Mono16, MemoryMarshal.AsBytes(pcm.AsSpan()), SampleRate);
            }
            _takes[kind] = takes;
        }

        _enabled = true;
    }

    /// <summary>Plays one sound per event, each where it was made as the main view's frame (the listener's) has it (<paramref name="frames"/>). Call once per frame with that frame's events.</summary>
    public void Play(ReadOnlySpan<SoundEvent> events, float dt, BodyFrames frames)
    {
        _clock += dt;
        if (!_enabled)
            return;

        foreach (var impact in events)
        {
            // A touchdown's strength is how fast it came down: 0.5 m/s is a gentle settle, the
            // fastest safe landing a heavy thump; a splash's likewise, against the fastest safe
            // splashdown. The others always play at full strength.
            var strength = impact.Cue switch
            {
                SoundCue.Touchdown => MathF.Pow(Math.Clamp((impact.Strength - 0.3f) / Scene.SafeTouchdownSpeed, 0.15f, 1f), 0.8f),
                SoundCue.Splash => MathF.Pow(Math.Clamp(impact.Strength / Scene.SafeSplashdownSpeed, 0.15f, 1f), 0.8f),
                _ => 1f,
            };

            var takes = _takes[impact.Cue];
            var voiceIndex = PickVoice();
            var voice = _voices[voiceIndex];
            voice.Stop();
            voice.Buffer = takes[_random.Next(takes.Length)];
            voice.Position = AudioSystem.ToFloat3(frames.InView(impact.Body, impact.Position));
            voice.Gain = strength * KindGain(impact.Cue);
            // A little random pitch, and harder hits slightly higher, so no two sound the same.
            voice.Pitch = (0.94f + 0.12f * (float)_random.NextDouble()) * (0.97f + 0.06f * strength);
            voice.Play();
            _voiceStarted[voiceIndex] = _clock;
        }
    }

    /// <summary>A free voice if there is one, else the one that started longest ago.</summary>
    private int PickVoice()
    {
        var oldest = 0;
        for (var i = 0; i < VoiceCount; i++)
        {
            if (_voices[i].State != AlSourceState.Playing)
                return i;
            if (_voiceStarted[i] < _voiceStarted[oldest])
                oldest = i;
        }
        return oldest;
    }

    private static float KindGain(SoundCue kind) => kind switch
    {
        SoundCue.Touchdown => 0.9f,
        SoundCue.LegsLocked => 0.45f,
        SoundCue.Splash => 0.8f,
        _ => 1f, // the explosion: the loudest
    };

    /// <summary>One take of an impact: a few exponentially decaying partials plus a strike transient.</summary>
    private static short[] Synthesise(SoundCue kind, uint seed)
    {
        // (frequency Hz, amplitude, decay seconds) per partial; noise burst (amplitude, decay, brightness 0..1).
        var (partials, noiseAmp, noiseDecay, noiseBright, length) = kind switch
        {
            // Touchdown: tonnes of booster settling onto its legs, a deep thud with a metallic creak in it.
            SoundCue.Touchdown => (
                new[] { (62f, 0.7f, 0.25f), (118f, 0.45f, 0.16f), (430f, 0.12f, 0.09f), (1130f, 0.05f, 0.05f) },
                0.45f, 0.02f, 0.25f, 0.9f),
            // Legs locking: a hard, ringing metal clank.
            SoundCue.LegsLocked => (
                new[] { (820f, 0.45f, 0.06f), (1370f, 0.3f, 0.045f), (2210f, 0.18f, 0.03f), (330f, 0.25f, 0.05f) },
                0.5f, 0.004f, 0.8f, 0.3f),
            // Splash: a dull slap on the water, then a long hiss of spray falling back.
            SoundCue.Splash => (
                new[] { (70f, 0.5f, 0.12f), (140f, 0.25f, 0.08f) },
                0.9f, 0.5f, 0.6f, 1.6f),
            // Explosion: a sharp crack, then a long, rolling, low boom.
            _ => (
                new[] { (38f, 0.8f, 0.9f), (55f, 0.6f, 0.7f), (91f, 0.35f, 0.45f) },
                1.6f, 0.45f, 0.18f, 3.2f),
        };

        var rng = new Random((int)seed);
        var jitter = 1f + 0.04f * ((float)rng.NextDouble() * 2 - 1); // each take tuned slightly differently
        var frames = (int)(length * SampleRate);
        var pcm = new short[frames];
        var low = 0f;
        var noiseState = seed | 1;

        for (var i = 0; i < frames; i++)
        {
            var t = i / (float)SampleRate;
            var sample = 0f;
            foreach (var (hz, amp, decay) in partials)
                sample += amp * MathF.Exp(-t / decay) * MathF.Sin(MathF.Tau * hz * jitter * t);

            // Strike: noise that dies away in a few milliseconds, low-passed for duller materials.
            noiseState ^= noiseState << 13;
            noiseState ^= noiseState >> 17;
            noiseState ^= noiseState << 5;
            var white = noiseState / (float)uint.MaxValue * 2f - 1f;
            low += (0.05f + 0.9f * noiseBright) * (white - low);
            sample += noiseAmp * MathF.Exp(-t / noiseDecay) * low;

            // A 2 ms fade-in avoids a click at the very start.
            var attack = MathF.Min(1f, t / 0.002f);
            pcm[i] = (short)(MathF.Tanh(sample * attack) * 28000f);
        }

        return pcm;
    }

    public void Dispose()
    {
        if (!_enabled)
            return;

        foreach (var voice in _voices)
        {
            voice.Stop();
            voice.Delete();
        }
        foreach (var takes in _takes.Values)
        foreach (var buffer in takes)
            buffer.Delete();
    }
}
