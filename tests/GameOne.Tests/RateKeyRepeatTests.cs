using Xunit;

namespace GameOne.Tests;

/// <summary>A held rate key steps on (<see cref="RateKeyRepeat"/>): once on the press, then every <see cref="Scene.RateKeyRepeatSeconds"/> of real time.</summary>
public sealed class RateKeyRepeatTests
{
    private const float Frame = 1f / 60f;

    [Fact]
    public void A_press_steps_once_at_once()
    {
        var repeat = new RateKeyRepeat();
        Assert.True(repeat.Step(pressed: true, held: true, Frame));
        var steps = 0;
        for (var t = 0f; t < 0.2f; t += Frame)
            if (repeat.Step(pressed: false, held: true, Frame))
                steps++;
        Assert.Equal(0, steps);
    }

    [Fact]
    public void Held_it_steps_on_every_quarter_second()
    {
        var repeat = new RateKeyRepeat();
        var steps = repeat.Step(pressed: true, held: true, Frame) ? 1 : 0;
        for (var i = 0; i < 60; i++)
            if (repeat.Step(pressed: false, held: true, Frame))
                steps++;
        Assert.Equal(1 + 4, steps);
    }

    [Fact]
    public void Let_go_and_pressed_again_it_starts_again()
    {
        var repeat = new RateKeyRepeat();
        repeat.Step(pressed: true, held: true, Frame);
        repeat.Step(pressed: false, held: true, 0.2f);
        repeat.Step(pressed: false, held: false, Frame);
        Assert.True(repeat.Step(pressed: true, held: true, Frame));
        Assert.False(repeat.Step(pressed: false, held: true, 0.2f));
        Assert.True(repeat.Step(pressed: false, held: true, 0.06f));
    }

    [Fact]
    public void A_long_frame_steps_once_not_a_backlog()
    {
        var repeat = new RateKeyRepeat();
        repeat.Step(pressed: true, held: true, Frame);
        Assert.True(repeat.Step(pressed: false, held: true, 1f));
        Assert.False(repeat.Step(pressed: false, held: true, Frame));
    }
}
