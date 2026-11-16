using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Time warp (<see cref="Flight.Advance"/>): many normal-sized steps in one frame, so a flight
/// warped through is the same flight as one flown frame by frame; nothing that happens in the
/// steps (a crash's explosion, say) is missed; and a busy world gives up when its time is up
/// rather than stalling the frame.
/// </summary>
public sealed class WarpTests(ITestOutputHelper output)
{
    private static readonly FlightInput Climbing = new(Throttle: 1f, Lean: Vector3.UnitX * 0.3f);

    [Fact]
    public void A_warped_flight_is_the_same_flight_as_one_flown_frame_by_frame()
    {
        using var normal = new Simulation(Mission.ReachOrbit);
        using var warped = new Simulation(Mission.ReachOrbit);
        foreach (var sim in new[] { normal, warped })
        {
            sim.Step(new FlightInput(Launch: true, FullThrottle: true));
            sim.Run((_, _) => default, f => f.State == FlightState.Flying, maxSeconds: 10);
        }

        // Ten seconds of climbing: 600 frames, or 20 warped frames of 30 steps each (30x).
        for (var i = 0; i < 600; i++)
            normal.Step(Climbing);
        for (var i = 0; i < 20; i++)
            Assert.Equal(30 * Flight.WarpStepSeconds, warped.Flight.Advance(30 * Flight.WarpStepSeconds, Climbing), 5);

        var apart = Vector3.Distance(normal.Flight.Position, warped.Flight.Position);
        output.WriteLine($"{normal.Flight.Ground:0.0} m up frame by frame, {warped.Flight.Ground:0.0} m warped: {apart:0.000} m apart");
        Assert.True(apart < 0.5f, $"the warped flight ended {apart:0.00} m from the normal one");
        Assert.Equal(normal.Flight.Fuel, warped.Flight.Fuel, 3);
    }

    [Fact]
    public void A_crash_in_the_middle_of_a_warped_frame_is_still_heard()
    {
        using var sim = new Simulation();
        var flight = sim.Flight;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Run((_, _) => default, f => f.Ground > 60f, maxSeconds: 20);
        sim.Step(new FlightInput(CutThrottle: true));

        // Warped through the fall, a second of game time a frame, until it hits the ground.
        var heard = false;
        for (var i = 0; i < 60 && !heard; i++)
        {
            flight.Advance(1f, default);
            foreach (var sound in flight.Sounds)
                heard |= sound.Cue == SoundCue.Explosion;
        }
        Assert.Equal(FlightState.Crashed, flight.State);
        Assert.True(heard, "the crash's explosion should be among the warped frame's sounds");
    }

    [Fact]
    public void Out_of_time_it_stops_early_and_says_how_far_it_got()
    {
        using var sim = new Simulation();
        var advanced = sim.Flight.Advance(5f, default, budgetSeconds: 0);
        Assert.Equal(5f / MathF.Ceiling(5f / Flight.WarpStepSeconds), advanced, 6); // one step, then out of time
    }
}
