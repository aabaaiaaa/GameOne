using System.Diagnostics;
using System.Globalization;
using Brutal.GlfwApi;
using System.Numerics;
using GameOne.Physics;

namespace GameOne.Tests;

/// <summary>
/// Watching a flight test: with the <c>GAMEONE_WATCH</c> environment variable set, each
/// <see cref="Simulation"/> opens the game's window and shows its flight through the game's own
/// <see cref="GameView"/> (camera, HUD, effects and sound), so what the test checks can be seen.
/// <c>GAMEONE_WATCH=1</c> starts in real time; a bigger number starts that many times faster. The
/// flight itself is exactly what it is headless: the same fixed frames, the same inputs. Only how
/// often it is drawn changes.
/// </summary>
/// <remarks>
/// <para>While watching: <c>]</c> or <c>+</c> doubles the speed, <c>[</c> or <c>-</c> halves it,
/// <c>0</c> goes back to real time, and Space pauses. The camera turns with the right mouse button
/// and zooms with the wheel. Faster than frames can be drawn, frames are skipped (never one with an
/// explosion in it), and each drawn frame is given all the time since the last, so the camera and
/// effects keep up.</para>
/// <para>Once another vessel is coming at the flown one (<see cref="Flight.ComingAt"/>: a fast
/// collision, or a near miss with the setting on), the camera tracks it till the end of the test:
/// it swings round the flown vessel to keep the other in the picture behind it, a little to one
/// side, as it comes and as it goes (see <see cref="Track"/>). The mouse still tips and zooms the
/// camera then, but not turns it. This is the watcher's own, not a camera mode of the game's
/// (TODO.md's camera modes).</para>
/// <para>Closing the window stops the watching, and the test carries on headless. At the end, the
/// last frame stays up for a few seconds.</para>
/// <para>For soak tests, <see cref="ForSoak"/> makes one that draws as fast as it can (no pacing,
/// no vsync), a frame every few steps as time warp would, so a long stretch of play goes through
/// the renderer, HUD and audio in minutes; there, a failure to draw is kept (<see cref="Failure"/>)
/// for the test to fail on.</para>
/// </remarks>
internal sealed class Watcher : IDisposable
{
    private const double LingerSeconds = 3;
    private const double MinSpeed = 0.125, MaxSpeed = 512;

    private readonly GlfwWindow _window;
    private readonly GameView _view;
    private readonly string _mission;
    private readonly Stopwatch _clock = new();
    private double _speed;
    private double _time;          // seconds of flight so far (the game's clock)
    private double _real;          // real seconds of it so far: its frames, as the game's are, so a slow-down shows slowed
    private double _shownAt, _shownReal; // the flight time and real time last drawn
    private double _wallAtTime;    // the wall-clock time that _real was paced from ...
    private double _paceFrom;      // ... and the real time then (both reset whenever the speed changes)
    private readonly bool _soak;
    private bool _closed, _paused;
    private Flight? _flight;

    /// <summary>A watcher if <c>GAMEONE_WATCH</c> asks for one, else null (headless, as fast as it goes).</summary>
    public static Watcher? FromEnvironment(Mission mission)
    {
        var setting = Environment.GetEnvironmentVariable("GAMEONE_WATCH");
        if (string.IsNullOrWhiteSpace(setting) || setting == "0")
            return null;
        var speed = double.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;
        return new Watcher(mission, speed, soak: false);
    }

    /// <summary>
    /// A watcher for a soak run: frames drawn as fast as they go (one every
    /// <see cref="SoakStepsPerFrame"/> steps of the flight), with vsync off, and small
    /// views coming and going all through it: every so often a view is added of every vessel
    /// without one (as "Add view" would), and the oldest one closed.
    /// </summary>
    public static Watcher ForSoak(Mission mission) => new(mission, 1, soak: true);

    /// <summary>A soak run draws a frame for every this many steps of the flight: 4x, as in time warp.</summary>
    public const int SoakStepsPerFrame = 4;

    private long _soakSteps;

    /// <summary>How many frames were drawn with at least one small view open, and the most open at once.</summary>
    public long FramesWithViews { get; private set; }
    public int MostViews { get; private set; }

    /// <summary>How many frames have been drawn.</summary>
    public long FramesDrawn { get; private set; }

    /// <summary>How frames are presented, for reports (FIFO is vsync; immediate and mailbox don't wait).</summary>
    public string PresentMode => _view.PresentMode;

    /// <summary>What stopped the drawing, if something did (a soak run fails on it).</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Wall-clock seconds since the first frame.</summary>
    public double WallSeconds => _clock.Elapsed.TotalSeconds;

    private Watcher(Mission mission, double speed, bool soak)
    {
        if (!Glfw.Init())
            throw new Exception("Failed to initialize GLFW");
        _mission = mission.Name;
        _speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
        _soak = soak;
        _window = GameView.CreateWindow(Title);
        if (!soak)
            _window.OnKey += OnKey;
        _view = new GameView(_window, vsync: !soak);
        _view.Hud.CloseMenu(); // the test picks the mission ...
        _view.Hud.ShowControls = false; // ... and flies it: the player's keys do nothing here
        UpdateCaption();
    }

    private string Title => _soak ? "GameOne: soak run" : $"GameOne: watching a flight test ({_mission}, {Speed})";

    private string Speed => _paused ? "paused" : _speed >= 1 ? $"{_speed:0.##}x" : $"1/{1 / _speed:0.##}x";

    private void OnKey(GlfwWindow window, GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods)
    {
        if (action is not (GlfwKeyAction.Press or GlfwKeyAction.Repeat))
            return;
        switch (key)
        {
            case GlfwKey.RightBracket or GlfwKey.Equal or GlfwKey.KpAdd:
                SetSpeed(_speed * 2);
                break;
            case GlfwKey.LeftBracket or GlfwKey.Minus or GlfwKey.KpSubtract:
                SetSpeed(_speed / 2);
                break;
            case GlfwKey.Number0 or GlfwKey.Kp0:
                SetSpeed(1);
                break;
            case GlfwKey.Space when action == GlfwKeyAction.Press:
                _paused = !_paused;
                Repace();
                UpdateCaption();
                break;
        }
    }

    private void SetSpeed(double speed)
    {
        _speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
        Repace();
        UpdateCaption();
    }

    /// <summary>Paces the flight from now on, from where it is, so a change of speed doesn't make it jump or race to catch up.</summary>
    private void Repace()
    {
        _wallAtTime = _clock.Elapsed.TotalSeconds;
        _paceFrom = _real;
    }

    private void UpdateCaption()
    {
        if (_soak)
        {
            if (FramesDrawn % 30 == 0) // the caption's text goes through the HUD too: no need every frame
                _view.Hud.Caption = $"SOAK RUN   frame {FramesDrawn:N0}   {FramesDrawn / Math.Max(WallSeconds, 1e-3):0} fps   flight time {FormatTime(_time)}";
            return;
        }
        _window.Title = Title;
        _view.Hud.Caption = $"WATCHING A FLIGHT TEST   {Speed}   flight time {FormatTime(_time)}      "
            + "[ / ]  slower / faster      0  real time      Space  pause";
    }

    private static string FormatTime(double seconds) => $"{(int)(seconds / 60)}:{seconds % 60:00.0}";

    /// <summary>Gives <paramref name="vessel"/> a small view, as "Add view" would, once the flight is shown (from its first frame).</summary>
    public void Open(Vessel vessel)
    {
        if (_flight is { } flight)
            _view.Views.Open(vessel, flight);
    }

    /// <summary>
    /// Shows the flight <paramref name="dt"/> game seconds on, a frame of <paramref name="realDt"/>
    /// real seconds later, keeping to the watching speed by the real time, as the game does: a
    /// slow-down (the game's clock slowed) shows slowed, and the warp fast. Paced by the game's
    /// clock instead, every slow-down would show at normal speed.
    /// </summary>
    public void Show(Flight flight, float dt, float realDt)
    {
        if (_closed)
            return;
        if (_flight is null)
        {
            _view.Follow(flight);
            _clock.Start();
        }
        _flight = flight;
        _time += dt;
        _real += realDt;
        if (_soak)
        {
            // A frame for every few steps, straight away, as time warp draws: everything is still
            // drawn every frame, and the flights (staging, crashes, restarts) come round sooner.
            if (++_soakSteps % SoakStepsPerFrame == 0)
                Present((float)(_time - _shownAt), (float)(_real - _shownReal));
            return;
        }

        var exploding = false;
        foreach (var sound in flight.Sounds)
            exploding |= sound.Cue == SoundCue.Explosion;
        if (Ahead() < -realDt && !exploding && !_paused)
            return; // behind (going faster than frames can be drawn): skip this one

        // Early: wait until this moment is due, still answering keys (a change of speed re-paces it).
        while (!_closed && !_paused && Ahead() > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(Ahead(), 0.02)));
            Glfw.PollEvents();
        }
        Present((float)(_time - _shownAt), (float)(_real - _shownReal));

        // While paused, keep drawing the same moment (so the camera can still be turned).
        while (_paused && !_closed)
            Present(0f, 0f);
    }

    /// <summary>How long until the moment the flight has reached should show, on the wall clock (negative if overdue).</summary>
    private double Ahead() => _wallAtTime + (_real - _paceFrom) / _speed - _clock.Elapsed.TotalSeconds;

    /// <summary>Draws the flight as it is, <paramref name="dt"/> seconds of flight and <paramref name="realDt"/> real seconds after the last frame drawn.</summary>
    private void Present(float dt, float realDt)
    {
        Glfw.PollEvents();
        if (_window.ShouldClose)
        {
            Close();
            return;
        }
        (_shownAt, _shownReal) = (_time, _real);
        FramesDrawn++;
        if (!_paused)
            UpdateCaption();
        try
        {
            if (!_soak)
                Track();
            _view.Present(dt, _time, _flight!, paused: false, realDt);
            if (_soak)
                ChurnViews();
        }
        catch (Exception e)
        {
            // Watching is only a view: if drawing fails (the GPU lost while the screen sleeps,
            // say), stop showing it rather than failing the test, which flies on headless.
            Console.Error.WriteLine($"Watching stopped: {e.GetType().Name}: {e.Message}");
            Failure = e;
            Close();
        }
    }

    // The vessel the camera tracks (the first seen coming at the flown one), the yaw it is
    // swinging to it at, and the wall-clock time it last swung, for an even swing in real time.
    private Vessel? _tracked;
    private float _trackYaw;
    private double _trackedAt = -1;

    /// <summary>
    /// How far to one side of straight behind the flown vessel the tracked one is kept, so the
    /// flown one doesn't hide it as it comes straight on.
    /// </summary>
    private static readonly float TrackSide = float.DegreesToRadians(25f);

    /// <summary>How fast the camera's swing closes on where it wants to be, per real second: quick enough to keep up with a pass slowed for, smooth enough not to whip round.</summary>
    private const float TrackRate = 4f;

    /// <summary>
    /// Turns the camera to keep the tracked vessel in the picture (see the remarks): put on the
    /// far side of the flown vessel from it, so the camera looks past the flown one at it, at the
    /// camera's own tip and distance (the mouse's), swinging there smoothly in real time. As the
    /// other passes, where it is swings through the side, so the camera swings round with it and
    /// sees it go. Not while zoomed out.
    /// </summary>
    private void Track()
    {
        var flight = _flight!;
        if (_tracked is null && flight.ComingAt(flight.Controlled) is { Other: { } other })
            _tracked = other;
        var camera = _view.Camera;
        var now = _clock.Elapsed.TotalSeconds;
        var realDt = (float)(now - _trackedAt);
        if (_tracked is null || camera.ZoomedOut)
        {
            _trackedAt = -1; // (zoomed out and back: start the swing again from where it is)
            return;
        }

        var focus = Views.Focus(flight.Controlled);
        var (up, east, south) = (Planet.Up(focus), Planet.East(focus), -Planet.North(focus));
        var away = focus - Collisions.Middle(_tracked); // from the tracked one past the flown one
        var flat = away - up * Vector3.Dot(away, up);
        if (flat.LengthSquared() < 1e-6f)
            return; // straight above or below: keep the swing where it is
        var wanted = MathF.Atan2(Vector3.Dot(flat, east), Vector3.Dot(flat, south)) + TrackSide;

        // The camera's own tip and distance, as the mouse left them (read back from where it is).
        var offset = camera.Position - camera.Focus;
        var distance = offset.Length();
        var pitch = distance > 0f ? MathF.Asin(Math.Clamp(Vector3.Dot(offset / distance, up), -1f, 1f)) : float.DegreesToRadians(Scene.CameraPitchDegrees);
        if (_trackedAt < 0)
        {
            // The first frame tracking: start from where the camera is, so it swings there, not jumps.
            var level = offset - up * Vector3.Dot(offset, up);
            _trackYaw = level.LengthSquared() > 1e-6f ? MathF.Atan2(Vector3.Dot(level, east), Vector3.Dot(level, south)) : wanted;
            realDt = 0f;
        }
        _trackedAt = now;
        // The shorter way round, eased in real time, so the camera is as smooth slowed as not.
        var turn = (float)Math.IEEERemainder(wanted - _trackYaw, MathF.Tau);
        _trackYaw += turn * (1f - MathF.Exp(-TrackRate * realDt));
        camera.Place(_trackYaw, pitch, distance > 0f ? distance : Scene.CameraDistance);
    }

    /// <summary>For a soak: a view of every vessel without one now and then, and the oldest view closed less often.</summary>
    private void ChurnViews()
    {
        var views = _view.Views;
        if (FramesDrawn % 90 == 0)
            foreach (var vessel in _flight!.Vessels)
                views.Open(vessel, _flight); // only if it can have one
        if (FramesDrawn % 240 == 0 && views.Small.Count > 0)
            views.Close(views.Small[0], _flight!);
        if (views.Small.Count > 0)
            FramesWithViews++;
        MostViews = Math.Max(MostViews, views.Small.Count);
    }

    private void Close()
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            _view.Dispose();
            _window.Dispose();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Closing the watch window: {e.GetType().Name}: {e.Message}");
        }
        Glfw.Terminate();
    }

    public void Dispose()
    {
        // Keep the end in view for a moment: the flight stands still, the effects play out.
        if (!_closed && _flight is not null && !_soak)
        {
            _paused = false;
            var end = _clock.Elapsed.TotalSeconds + LingerSeconds;
            while (!_closed && _clock.Elapsed.TotalSeconds < end)
            {
                _time += 1.0 / 60;
                _real += 1.0 / 60;
                Present(1f / 60, 1f / 60);
            }
        }
        Close();
    }
}
