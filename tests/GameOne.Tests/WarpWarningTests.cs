using System.Numerics;
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

    private static readonly Pilot Nobody = (_, _) => default;

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
    public void A_collision_dropped_for_holds_the_warp_at_the_physics_top_rate_until_it_has_hit()
    {
        var (sim, flown, other) = HeadOn();
        using var _ = sim;
        var flight = sim.Flight;
        Assert.True(UntilDropped(sim));
        // Faster pressed at once, and every few frames after: never past the physics' top rate
        // while the collision is coming, so its slow-down plays out in full.
        var (least, frames, highest) = (1f, 0, 0f);
        Assert.True(sim.Run((_, _) => new FlightInput(Faster: frames++ % 10 == 0), f =>
        {
            highest = MathF.Max(highest, f.PlayerRate);
            if (f.Slowed is { } slowed)
                least = MathF.Min(least, slowed.Scale);
            return flown.State == FlightState.Crashed && other.State == FlightState.Crashed;
        }, maxSeconds: 60));
        output.WriteLine($"at most {highest}x; slowed to {least:0.0000} (least {Slowdown.Least(Closing):0.0000}); {flown.CrashReason}");
        Assert.True(highest <= Scene.PhysicsTopRate, $"warped at {highest}x with the collision coming");
        Assert.True(least <= Slowdown.Least(Closing), "slowed down to its least scale as it hit");
    }

    [Fact]
    public void A_collision_dropped_for_says_why_the_warp_is_held_while_it_holds()
    {
        var (sim, flown, other) = HeadOn();
        using var _ = sim;
        var flight = sim.Flight;
        Assert.Null(flight.CollisionHeld);
        Assert.True(UntilDropped(sim));
        var said = $"{WorldLog.Named(flown)} with {WorldLog.Named(other)}";
        // From the drop (some 24 s ahead) through the physics' own warning to the hit, the hold's
        // reason is the collision's, in the readout and on the time line once the drop's own line
        // has gone (as it goes at once with a rate key), however soon the warning shows.
        var (frames, warned) = (0, false);
        Assert.True(sim.Run(Nobody, f =>
        {
            frames++;
            warned |= f.Warning is not null;
            Assert.Equal(said, f.CollisionHeld);
            Assert.Equal(said, Readout.FlightReadings(f).GetValueOrDefault("Collision hold"));
            Assert.Equal($"a collision coming at {said}", f.WarpHeld);
            if (f.PlayerRate >= Scene.PhysicsTopRate && f.Slowed is null)
                Assert.Equal($"WARP {Scene.PhysicsTopRate:0}x  (held: a collision coming at {said})   ( , and . )",
                    Hud.TimeLine(f.Slowed, f.PlayerRate, f.TimeScale, f.TimeScale, f.WarpHeld, dropped: null, f.RealTime)?.Text);
            return f.Warning is not null && f.Slowed is not null;
        }, maxSeconds: 60));
        output.WriteLine($"held for the collision {frames} frames, warned {warned}: {said}");
        Assert.True(warned);
    }

    [Fact]
    public void A_collision_dropped_for_and_dodged_lets_the_warp_go()
    {
        var (sim, flown, other) = HeadOn();
        using var _ = sim;
        var flight = sim.Flight;
        Assert.True(UntilDropped(sim));
        Assert.NotNull(flight.CollisionHeld);
        // Dodged by leaning and burning hard: once the collision is no longer predicted the hold
        // lets go (nothing said for it), and Faster climbs past the physics' top rate again.
        var frames = 0;
        Assert.True(sim.Run((_, _) => new FlightInput(Lean: flown.Right, FullThrottle: true), f => ++frames > 0 && f.CollisionHeld is null, maxSeconds: 20));
        output.WriteLine($"let go {frames} frames after the drop, {flight.WorldTime:0.0} s, {Vector3.Distance(flown.CentreOfMass, other.CentreOfMass):0} m apart");
        Assert.False(Collisions.StillComing(flown, other, Scene.CollisionHorizonSeconds + 2f * Scene.WarpDropLeadSeconds));
        Assert.Null(Readout.FlightReadings(flight).GetValueOrDefault("Collision hold"));
        sim.Step(new FlightInput(Faster: true));
        Assert.True(flight.PlayerRate > Scene.PhysicsTopRate, $"still held at {flight.PlayerRate}x after the dodge");
    }

    [Fact]
    public void Raised_again_after_a_near_miss_s_drop_the_same_vessel_does_not_drop_it_again()
    {
        var (sim, flown, other) = HeadOn(sideways: 2f * FastCollisionTests.BoosterSize + 1f);
        using var _ = sim;
        var flight = sim.Flight;
        Assert.True(UntilDropped(sim, nearMisses: true));
        var drop = flight.LastDrop!.Value;
        Assert.Equal(WarpDrop.NearMiss, drop.Reason);
        FormulaRateTests.ToTheTop(flight);
        for (var i = 0; i < 3; i++)
            sim.Step(new FlightInput(NearMisses: true));
        output.WriteLine($"{flight.WorldTime - MeetIn:0.0} s past the pass at {flight.PlayerRate}x; {flown.State}");
        Assert.Equal(drop, flight.LastDrop);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.True(flight.WorldTime > MeetIn, "the warp ran on past the pass");
        Assert.Equal(FlightState.Flying, flown.State);
        Assert.Equal(FlightState.Flying, other.State);
    }

    [Fact]
    public void An_other_dropped_for_coming_at_one_witnessed_vessel_is_still_asked_of_for_another()
    {
        // The two head on, and a satellite far off: the other dropped for as it came at the
        // satellite is still found coming at the booster; dropped for coming at the booster, it isn't.
        var meeting = FormulaCollisionTests.Meeting(Closing, 0f, MeetIn);
        var world = meeting with { Vessels = [.. meeting.Vessels, UnwatchedTests.Satellite(2, 10000f, 0f, 240f)], NextVesselId = 3 };
        var (sim, flown, other) = FastCollisionTests.Fly(world);
        using var _ = sim;
        var flight = sim.Flight;
        var far = flight.WorldVessels.Single(v => v.Id == 2);
        FormulaRateTests.ToTheTop(flight);
        Assert.True(UntilDropped(sim)); // in the physics, its look ahead's horizon from the meeting
        var collisions = new Collisions(flight);
        var (now, horizon) = (flight.WorldTime, MeetIn - flight.WorldTime + 1.0);
        (Vessel?, Vessel?)[] forTheFar = [(far, other)], forTheFlown = [(flown, other)];
        var found = collisions.AheadOfTheWarp(flown, flight.WorldVessels, now, horizon, false, forTheFar);
        output.WriteLine($"dropped for the satellite: {found}");
        Assert.Same(other, found?.Other);
        Assert.Null(collisions.AheadOfTheWarp(flown, flight.WorldVessels, now, horizon, false, forTheFlown));
    }

    [Fact]
    public void Two_drifting_side_by_side_within_their_sizes_drop_it_once_however_often_it_is_raised()
    {
        // Two satellites on one circle, their middles closer than their sizes reach (their bodies
        // clear of each other), going alike: closing at next to nothing, now a little, now not.
        const float Altitude = 9000f;
        var first = UnwatchedTests.Satellite(0, Altitude, 0f, 60f);
        var (sim, flown) = WarpScenes.Flying(first);
        var (sizes, clear) = (2f * Collisions.Size(flown), 2f * flown.Width + 0.5f);
        sim.Dispose();
        var apart = (sizes + clear) / 2f;
        output.WriteLine($"middles {apart:0.00} m apart: sizes {sizes:0.00} m, bodies clear past {clear:0.00} m");
        Assert.True(apart < sizes && apart > clear, "within their sizes, their bodies clear of each other");
        var second = UnwatchedTests.Satellite(1, Altitude, 0f, 60f + UnwatchedTests.DegreesApart(Altitude, apart));
        // Nudged up by 2 mm/s: its gap to the first wavers by a third of a metre each lap, closing then opening.
        second = second with { Velocity = second.Velocity.ToSingle() + GroundBody.Planet.Up(second.Position.ToSingle()) * 0.002f };
        (sim, flown) = WarpScenes.Flying(first, second);
        using var _ = sim;
        var flight = sim.Flight;
        var drops = 0;
        WarpDropped? last = null;
        for (var cycle = 0; cycle < 8; cycle++)
        {
            for (var k = 0; k < Flight.Rates.Length; k++)
                flight.RunFrame(0f, new FlightInput(Faster: true));
            sim.Step(default);
            if (flight.LastDrop is { } drop && drop != last)
                (drops, last) = (drops + 1, drop);
        }
        output.WriteLine($"{drops} drops in 8 raises; {flight.WorldTime:0} s on at {flight.PlayerRate}x");
        Assert.True(drops <= 1, $"dropped {drops} times");
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
