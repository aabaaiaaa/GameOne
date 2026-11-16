using System.Numerics;
using Brutal.Numerics;
using Brutal.OpenAlApi;

namespace GameOne.Audio;

/// <summary>
/// The OpenAL device and context (BRUTAL's <c>Brutal.OpenAl</c>, i.e. OpenAL Soft) shared by every
/// sound in the game, and the listener, which sits at the camera.
/// </summary>
/// <remarks>Units are metres, like the rest of the scene.</remarks>
internal sealed class AudioSystem : IDisposable
{
    private readonly Device? _device;
    private readonly Context? _context;

    /// <summary>False if no audio device could be opened; the game then simply runs silently.</summary>
    public bool Enabled { get; }

    public AudioSystem()
    {
        try
        {
            _device = Alc.OpenDevice();
            _context = _device.CreateContext();
            _context.MakeCurrent();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Audio disabled: {e.Message}");
            return;
        }

        // The real speed of sound, in metres per second. The camera chases the booster, so the
        // Doppler shift is gentle: it only shows when the booster pulls away or catches up.
        Al.DistanceModel = AlDistanceModel.InverseDistanceClamped;
        Al.SpeedOfSound = 343f;

        Console.WriteLine($"Audio: {Al.GetString(AlGetString.Renderer)} {Al.GetString(AlGetString.Version)}");
        Enabled = true;
    }

    /// <summary>Puts the listener at the camera, facing where it looks. Call once per frame.</summary>
    public void UpdateListener(Vector3 position, Vector3 forward, Vector3 up)
    {
        if (!Enabled)
            return;

        Al.Listener.Position = ToFloat3(position);
        Al.Listener.Orientation = (ToFloat3(forward), ToFloat3(up));
    }

    public static float3 ToFloat3(Vector3 v) => new(v.X, v.Y, v.Z);

    public void Dispose()
    {
        if (!Enabled)
            return;

        Context.MakeCurrent(null);
        _context?.Dispose();
        _device?.Dispose();
    }
}
