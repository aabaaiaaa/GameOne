using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A collision or near miss coming at a witnessed vessel, warned of at a formula's rate
/// (<see cref="Collisions.AheadOfTheWarp"/>): the warp drops to the physics' top rate
/// <see cref="Scene.CollisionHorizonSeconds"/> and <see cref="Scene.WarpDropLeadSeconds"/> ahead
/// of it, so the physics' look ahead has it in time, and its warning there drops it to normal
/// speed, its slow-down played out in full; a near miss only with the setting on; once each other
/// vessel; and never for two side by side on one orbit.
/// </summary>
public sealed class WarpWarningTests(ITestOutputHelper output)
{
    /// <summary>How far into the world's time the pairs here meet: some two frames at the top rate.</summary>
    private const float MeetIn = 600f;

    /// <summary>How fast the pairs here close, head on.</summary>
    private const float Closing = 2000f;

    /// <summary>Two boosters head on, <see cref="MeetIn"/> s of travel apart, closing at <see cref="Closing"/>, the first flown, at the top rate.</summary>
    private static (Simulation Sim, Vessel Flown, Vessel Other) HeadOn(float sideways = 0f)
    {
        var (sim, flown, other) = FastCollisionTests.Fly(FormulaCollisionTests.Meeting(Closing, sideways, MeetIn));
        FormulaRateTests.ToTheTop(sim.Flight);
        return (sim, flown, other!);
    }

    /// <summary>Runs frames until the warp drops (at most <paramref name="frames"/>), the setting as <paramref name="nearMisses"/>; false if it never did.</summary>
    private static bool UntilDropped(Simulation sim, bool nearMisses = false, int frames = 10)
    {
        for (var i = 0; i < frames; i++)
        {
            sim.Step(new FlightInput(NearMisses: nearMisses));
            if (sim.Flight.LastDrop is not null)
                return true;
        }
        return false;
    }

    [Fact]
    public void A_collision_first_seen_at_the_top_rate_drops_to_the_physics_top_rate_then_to_normal_speed_and_its_slow_down_plays_out()
    {
        var (sim, flown, other) = HeadOn();
        using var _ = sim;
        var flight = sim.Flight;
        Assert.True(UntilDropped(sim));
        var ahead = MeetIn - flight.WorldTime;
        output.WriteLine($"dropped {ahead:0.00} s before the meeting: {flight.LastDrop!.Value.Why}");
        Assert.Equal(WarpDrop.Collision, flight.LastDrop!.Value.Reason);
        Assert.Equal(WorldLog.Named(flown), flight.LastDrop.Value.Vessel);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.InRange(ahead, Scene.CollisionHorizonSeconds, Scene.CollisionHorizonSeconds + Scene.WarpDropLeadSeconds + 0.01);
        Assert.Null(flight.Warning);

        // In the physics now: its warning drops it to normal speed (today's drop), and its slow-down plays out.
        Assert.True(sim.Run((_, _) => default, f => f.Warning is not null, maxSeconds: 30));
        output.WriteLine($"warned {MeetIn - flight.WorldTime:0.00} s before it");
        Assert.Equal(1f, flight.PlayerRate);
        var least = 1f;
        sim.Run((_, _) => default, f =>
        {
            if (f.Slowed is { } slowed)
                least = MathF.Min(least, slowed.Scale);
            return flown.State == FlightState.Crashed && other.State == FlightState.Crashed;
        }, maxSeconds: 30);
        output.WriteLine($"slowed to {least:0.0000} (least {Slowdown.Least(Closing):0.0000}); {flown.CrashReason}");
        Assert.True(least <= Slowdown.Least(Closing), "slowed down to its least scale as it hit");
        Assert.Equal(FlightState.Crashed, flown.State);
        Assert.Equal(FlightState.Crashed, other.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_near_miss_first_seen_at_the_top_rate_drops_it_only_with_the_setting_on(bool setting)
    {
        // Their middles a little wider apart than both sizes: well within a length, outline to outline.
        var (sim, flown, other) = HeadOn(sideways: 2f * FastCollisionTests.BoosterSize + 1f);
        using var _ = sim;
        var flight = sim.Flight;
        var dropped = UntilDropped(sim, setting);
        output.WriteLine($"{(dropped ? $"dropped {MeetIn - flight.WorldTime:0.00} s before the pass: {flight.LastDrop!.Value.Why}" : "never dropped")}");
        Assert.Equal(setting, dropped);
        if (setting)
        {
            Assert.Equal(WarpDrop.NearMiss, flight.LastDrop!.Value.Reason);
            Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
            Assert.InRange(MeetIn - flight.WorldTime, Scene.CollisionHorizonSeconds, Scene.CollisionHorizonSeconds + Scene.WarpDropLeadSeconds + 0.01);
            Assert.True(sim.Run((_, _) => new FlightInput(NearMisses: true), f => f.NearMissWarning is not null, maxSeconds: 30));
            Assert.Same(other, flight.NearMissWarning!.Value.Other);
            Assert.Equal(1f, flight.PlayerRate);
        }
        else
        {
            Assert.True(flight.WorldTime > MeetIn, "the warp ran on past the pass");
            Assert.Equal(Scene.TopRate, flight.PlayerRate);
        }
        Assert.Equal(FlightState.Flying, flown.State);
        Assert.Equal(FlightState.Flying, other.State);
    }

    [Fact]
    public void Raised_again_after_the_drop_the_same_vessel_does_not_drop_it_again()
    {
        var (sim, flown, other) = HeadOn();
        using var _ = sim;
        var flight = sim.Flight;
        Assert.True(UntilDropped(sim));
        var drop = flight.LastDrop!.Value;
        FormulaRateTests.ToTheTop(flight);
        for (var i = 0; i < 3; i++)
            sim.Step(default);
        output.WriteLine($"{flight.WorldTime - MeetIn:0.0} s past the meeting at {flight.PlayerRate}x; {flown.State} {flown.CrashReason}");
        Assert.Equal(drop, flight.LastDrop);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.True(flight.WorldTime > MeetIn, "the warp ran on past the meeting");
        // Under formula, the collision judged as the formula judges it.
        Assert.Equal(FlightState.Crashed, flown.State);
        Assert.Equal(FlightState.Crashed, other.State);
    }

    [Fact]
    public void Two_satellites_side_by_side_on_one_orbit_never_drop_it()
    {
        const float Altitude = 9000f;
        var first = UnwatchedTests.Satellite(0, Altitude, 0f, 60f);
        var second = UnwatchedTests.Satellite(1, Altitude, 0f, 60f + UnwatchedTests.DegreesApart(Altitude, 300f));
        var (sim, flown) = WarpScenes.Flying(first, second);
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        for (var i = 0; i < 120; i++)
            sim.Step(new FlightInput(NearMisses: true));
        output.WriteLine($"{flight.WorldTime:0} s on at {flight.PlayerRate}x; {flown.State}");
        Assert.Null(flight.LastDrop);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
    }
}
