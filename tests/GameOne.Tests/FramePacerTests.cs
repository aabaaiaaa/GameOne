using System.Diagnostics;
using Xunit;

namespace GameOne.Tests;

/// <summary>The settings' 60 and 30 fps caps: frames a period apart on average, none waited for uncapped or at the screen's rate.</summary>
public sealed class FramePacerTests
{
    [Theory]
    [InlineData((int)FrameCap.Fps60, 1.0 / 60.0)]
    [InlineData((int)FrameCap.Fps30, 1.0 / 30.0)]
    public void A_capped_rate_spaces_frames_a_period_apart(int capped, double period)
    {
        var cap = (FrameCap)capped; // (by number: the test project sees the game's types, but a public test can't take an internal one)
        using var pacer = new FramePacer { Cap = cap };
        Assert.Equal(period, FramePacer.PeriodOf(cap));
        pacer.Wait(); // the first starts the schedule, at once
        const int frames = 30;
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < frames; i++)
            pacer.Wait();
        var mean = Stopwatch.GetElapsedTime(start).TotalSeconds / frames;
        // Never early (the schedule is kept), and on a busy machine only a little late on average.
        Assert.InRange(mean, period * 0.98, period * 1.15);
    }

    [Theory]
    [InlineData((int)FrameCap.Uncapped)]
    [InlineData((int)FrameCap.ScreenRate)]
    public void Uncapped_or_at_the_screens_rate_it_never_waits(int capped)
    {
        var cap = (FrameCap)capped; // (by number: the test project sees the game's types, but a public test can't take an internal one)
        using var pacer = new FramePacer { Cap = cap };
        Assert.Null(FramePacer.PeriodOf(cap));
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 100; i++)
            pacer.Wait();
        Assert.True(Stopwatch.GetElapsedTime(start).TotalMilliseconds < 50);
    }

    [Fact]
    public void A_frame_late_by_more_than_a_period_starts_the_schedule_again_with_no_burst()
    {
        using var pacer = new FramePacer { Cap = FrameCap.Fps60 };
        pacer.Wait();
        Thread.Sleep(100); // a stall: six frames' worth
        pacer.Wait(); // late: at once, and the schedule starts again from here
        var start = Stopwatch.GetTimestamp();
        pacer.Wait(); // so the next waits its whole period, not caught up on
        Assert.True(Stopwatch.GetElapsedTime(start).TotalSeconds > 1.0 / 60.0 * 0.9);
    }
}
