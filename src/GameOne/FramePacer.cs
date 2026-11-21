using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameOne;

/// <summary>
/// The 60 and 30 fps caps (<see cref="FrameCap"/>): before each frame, waits out what is left of
/// the frame before it, by a schedule of frame starts a period apart, so the rate holds on average
/// whatever each frame took. A frame late by less than a period is caught up on (the next starts
/// at once); one late by more starts the schedule again from now, so a stall (a world loading)
/// isn't followed by a burst of frames. Windows' sleep is only as fine as its timer (up to
/// 15.6 ms, a whole frame at 60), so the wait is a high-resolution waitable timer to within a
/// millisecond of the start, then a spin for the rest; elsewhere, or where that timer can't be
/// made, sleeps of a millisecond then the spin. The wait is between frames, in none of them: the
/// timings' parts stay each frame's own work, and their frame rate counts the wait.
/// </summary>
internal sealed unsafe partial class FramePacer : IDisposable
{
    /// <summary>How long before a frame's start the timer's wait ends and the spin takes over: the timer's own lateness, with room.</summary>
    private const double SpinSeconds = 0.001;

    /// <summary>The high-resolution timer (Windows), or 0 where there is none.</summary>
    private nint _timer;

    /// <summary>When the next frame may start, in <see cref="Stopwatch"/> ticks; 0 with no schedule (uncapped, or the first capped frame).</summary>
    private long _next;

    public FramePacer()
    {
        if (OperatingSystem.IsWindows())
            _timer = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    /// <summary>The cap kept to: only <see cref="FrameCap.Fps60"/> and <see cref="FrameCap.Fps30"/> wait (the screen's rate is the swapchain's).</summary>
    public FrameCap Cap { get; set; }

    /// <summary>The seconds between frames under <paramref name="cap"/>, or null for none of the pacer's own.</summary>
    public static double? PeriodOf(FrameCap cap) => cap switch
    {
        FrameCap.Fps60 => 1.0 / 60.0,
        FrameCap.Fps30 => 1.0 / 30.0,
        _ => null,
    };

    /// <summary>Waits until this frame may start (at once uncapped, or on time).</summary>
    public void Wait()
    {
        if (PeriodOf(Cap) is not { } period)
        {
            _next = 0;
            return;
        }
        var ticks = (long)(period * Stopwatch.Frequency);
        var now = Stopwatch.GetTimestamp();
        if (_next == 0 || now - _next > ticks)
            _next = now;
        else
            WaitUntil(_next);
        _next += ticks;
    }

    private void WaitUntil(long start)
    {
        var spin = (long)(SpinSeconds * Stopwatch.Frequency);
        while (true)
        {
            var left = start - Stopwatch.GetTimestamp();
            if (left <= spin)
                break;
            if (_timer != 0)
            {
                // Relative due times are negative, in 100 ns units.
                var due = -(long)((left - spin) * 10_000_000.0 / Stopwatch.Frequency);
                if (due < 0 && SetWaitableTimer(_timer, &due, 0, 0, 0, 0) != 0)
                {
                    WaitForSingleObject(_timer, Infinite);
                    continue;
                }
            }
            Thread.Sleep(1);
        }
        while (Stopwatch.GetTimestamp() < start)
            Thread.SpinWait(20);
    }

    public void Dispose()
    {
        if (_timer != 0)
            CloseHandle(_timer);
        _timer = 0;
    }

    private const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003, Infinite = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    private static partial int SetWaitableTimer(nint timer, long* dueTime, int period, nint completion, nint argument, int resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
