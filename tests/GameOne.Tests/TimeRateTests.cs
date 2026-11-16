using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The time rate is the flight's (<see cref="Flight.RunFrame"/>, <see cref="Flight.PlayerRate"/>,
/// <see cref="Flight.TimeScale"/>), and the physics' fixed step can be shortened
/// (<see cref="Physics.Worlds.StepScale"/>, every body's, <see cref="Physics.RocketPhysics.StepScale"/>): a shortened step slowed with the game's clock
/// takes as many steps a real second as a normal one, and a flight comes out the same.
/// </summary>
public sealed class TimeRateTests(ITestOutputHelper output)
{
    [Fact]
    public void The_rate_keys_step_through_the_rates()
    {
        using var sim = new Simulation();
        var flight = sim.Flight;
        Assert.Equal(1f, flight.PlayerRate);
        foreach (var expected in new[] { 2f, 4f, 8f, 16f, 32f, 32f })
        {
            flight.RunFrame(Simulation.FrameSeconds, new FlightInput(Faster: true));
            Assert.Equal(expected, flight.PlayerRate);
            Assert.Equal(expected, flight.TimeScale);
        }
        for (var i = 0; i < 9; i++)
            flight.RunFrame(Simulation.FrameSeconds, new FlightInput(Slower: true));
        Assert.Equal(0.25f, flight.PlayerRate);
        Assert.Equal(Flight.Rates[0], flight.PlayerRate);
    }

    [Fact]
    public void A_frame_in_warp_runs_its_rate_of_game_time()
    {
        using var sim = new Simulation();
        var flight = sim.Flight;
        for (var i = 0; i < 5; i++)
            flight.RunFrame(Simulation.FrameSeconds, new FlightInput(Faster: true));
        Assert.Equal(32f, flight.PlayerRate);

        var before = flight.WorldTime;
        var advanced = flight.RunFrame(Simulation.FrameSeconds, default);
        output.WriteLine($"a warped frame at 32x: {advanced:0.000000} s, the world's clock {flight.WorldTime - before:0.000000} s");
        Assert.Equal(32f * Simulation.FrameSeconds, advanced, 4);
        Assert.Equal(advanced, flight.WorldTime - before, 4);
    }

    [Fact]
    public void A_frame_at_normal_speed_is_an_update()
    {
        using var updated = new Simulation();
        using var framed = new Simulation();
        var start = new FlightInput(Launch: true, FullThrottle: true);
        updated.Flight.Update(Simulation.FrameSeconds, start);
        var advanced = framed.Flight.RunFrame(Simulation.FrameSeconds, start);
        Assert.Equal(Simulation.FrameSeconds, advanced);
        for (var i = 1; i < 60; i++)
        {
            updated.Flight.Update(Simulation.FrameSeconds, default);
            framed.Flight.RunFrame(Simulation.FrameSeconds, default);
        }

        Assert.Equal(updated.Flight.WorldTime, framed.Flight.WorldTime);
        var apart = System.Numerics.Vector3.Distance(updated.Flight.Position, framed.Flight.Position);
        output.WriteLine($"{updated.Flight.Ground:0.000} m up updated, {framed.Flight.Ground:0.000} m framed: {apart:0.000000} m apart");
        Assert.True(apart < 1e-4f, $"the framed flight ended {apart} m from the updated one");
    }

    [Fact]
    public void A_shortened_step_takes_as_many_steps_a_real_second()
    {
        long normal, shortened;
        double normalTime, shortenedTime;
        using (var sim = new Simulation())
        {
            var (steps, time) = (sim.Physics.Steps, sim.Flight.WorldTime);
            for (var i = 0; i < 60; i++)
                sim.Flight.Update(Simulation.FrameSeconds, default);
            (normal, normalTime) = (sim.Physics.Steps - steps, sim.Flight.WorldTime - time);
        }
        using (var sim = new Simulation())
        {
            sim.Worlds.StepScale = 0.1f;
            var (steps, time) = (sim.Physics.Steps, sim.Flight.WorldTime);
            for (var i = 0; i < 60; i++)
                sim.Flight.Update(0.1f * Simulation.FrameSeconds, default);
            (shortened, shortenedTime) = (sim.Physics.Steps - steps, sim.Flight.WorldTime - time);
        }

        output.WriteLine($"a real second: {normal} steps over {normalTime:0.0000} s at 1, {shortened} over {shortenedTime:0.0000} s at 0.1");
        Assert.Equal(120, normal);
        Assert.Equal(120, shortened);
        Assert.Equal(1.0, normalTime, 4);
        Assert.Equal(0.1, shortenedTime, 4);
    }

    [Fact]
    public void A_sharp_drop_of_the_step_runs_no_backlog_of_short_steps()
    {
        // Most of a whole step is left over from a frame, then the step drops twenty-fold (a
        // collision found late): the part left is that much of a short step, not twenty of them,
        // so the next frame runs only its own game time's steps, not the most a frame may (8).
        using var sim = new Simulation();
        sim.Flight.Update(0.99f * Physics.RocketPhysics.StepSeconds, default);
        sim.Worlds.StepScale = 0.05f;
        var steps = sim.Physics.Steps;
        var dt = 0.05f * Simulation.FrameSeconds;
        sim.Flight.Update(dt, default);
        var taken = sim.Physics.Steps - steps;
        var most = (int)(dt / sim.Physics.StepLength + 1e-3f) + 1;
        output.WriteLine($"{taken} steps of {sim.Physics.StepLength * 1000f:0.000} ms in a frame of {dt * 1000f:0.000} ms of game time (at most {most})");
        Assert.InRange(taken, 1, most);
    }

    [Fact]
    public void A_rise_of_the_step_loses_no_step_and_doubles_none()
    {
        // Most of a short step is left over (0.9 of one at a tenth), then the step is put back to
        // its full length (a slow-down ending): what is left stays 0.9 of a step, so each frame
        // of normal speed then runs its own two steps, none lost or run twice, a real second its 120.
        using var sim = new Simulation();
        sim.Worlds.StepScale = 0.1f;
        sim.Flight.Update(0.9f * sim.Physics.StepLength, default);
        sim.Worlds.StepScale = 1f;
        var perFrame = new List<long>();
        for (var i = 0; i < 60; i++)
        {
            var steps = sim.Physics.Steps;
            sim.Flight.Update(Simulation.FrameSeconds, default);
            perFrame.Add(sim.Physics.Steps - steps);
        }
        output.WriteLine($"after the rise: {perFrame.Sum()} steps in 60 frames, {perFrame.Min()} to {perFrame.Max()} a frame");
        Assert.All(perFrame, n => Assert.Equal(2, n));
        Assert.Equal(120, perFrame.Sum());
    }

    [Fact]
    public void A_fall_is_the_same_with_shortened_steps()
    {
        var normal = TouchdownFrom100m(stepScale: 1f);
        var shortened = TouchdownFrom100m(stepScale: 0.25f);
        output.WriteLine($"touchdown from 100 m: {normal:0.000} m/s in normal steps, {shortened:0.000} m/s in quarter steps");
        Assert.True(normal > 30f, $"a fall from 100 m should come down fast ({normal:0.00} m/s)");
        Assert.Equal(normal, shortened, 0.05f);
    }

    /// <summary>
    /// The touchdown speed of a booster dropped from 100 m with its legs out, its steps scaled by
    /// <paramref name="stepScale"/> and the game's clock with them (as a slow-down runs it): each
    /// frame is that much game time, so it takes 1/<paramref name="stepScale"/> times the frames.
    /// </summary>
    private static float TouchdownFrom100m(float stepScale)
    {
        var pad = Scene.TestPads[0].Centre;
        using var sim = Drops.At(pad, Planet.East(pad), 0f, clearance: 100f);
        sim.Worlds.StepScale = stepScale;
        var flight = sim.Flight;
        for (var i = 0; i < 20 * 60 / stepScale && flight.State == FlightState.Flying; i++)
            flight.Update(stepScale * Simulation.FrameSeconds, default);
        Assert.NotEqual(FlightState.Flying, flight.State);
        return flight.TouchdownSpeed;
    }
}
