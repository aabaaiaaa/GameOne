using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>Each lit place's lights switch on at its own dusk and off at its dawn, faded over real seconds (<see cref="PlaceLights"/>).</summary>
public sealed class PlaceLightsTests
{
    private static readonly int Home = Enumerable.Range(0, PadLights.All.Count)
        .MinBy(i => PadLights.All[i].Origin.Length());

    private static double HomeAt(double localSeconds) => Sun.WorldTimeFor(Vector3.Zero, localSeconds);

    /// <summary>A world time at Home's morning where its sun is 0.10 high (between dusk's and dawn's heights), by bisection.</summary>
    private static double BetweenHeights() => HeightPointOne(300, 330);

    /// <summary>The same in the evening, the sun setting.</summary>
    private static double BetweenHeightsInTheEvening() => HeightPointOne(870, 930);

    /// <summary>The Home time between two of its local times (seconds of its day) where its sun is 0.10 high, rising or setting.</summary>
    private static double HeightPointOne(double from, double to)
    {
        double a = HomeAt(from), b = HomeAt(to);
        var rising = Sun.Height(GroundBody.Planet, Vector3.Zero, a) < Sun.Height(GroundBody.Planet, Vector3.Zero, b);
        Assert.True(rising ? Sun.Height(GroundBody.Planet, Vector3.Zero, a) < 0.10f && Sun.Height(GroundBody.Planet, Vector3.Zero, b) > 0.10f
                           : Sun.Height(GroundBody.Planet, Vector3.Zero, a) > 0.10f && Sun.Height(GroundBody.Planet, Vector3.Zero, b) < 0.10f);
        for (var i = 0; i < 60; i++)
        {
            var mid = (a + b) / 2;
            if ((Sun.Height(GroundBody.Planet, Vector3.Zero, mid) < 0.10f) == rising) a = mid; else b = mid;
        }
        return (a + b) / 2;
    }

    [Fact]
    public void At_Home_they_are_off_at_noon_and_on_at_midnight()
    {
        var lights = new PlaceLights();
        lights.Reset(HomeAt(600));
        Assert.Equal(0f, lights.Lit[Home]);
        lights.Reset(HomeAt(0));
        Assert.Equal(1f, lights.Lit[Home]);
    }

    [Fact]
    public void At_dusk_they_fade_on_over_the_fade_s_real_seconds_never_at_once()
    {
        var lights = new PlaceLights();
        var (start, end) = (HomeAt(870), HomeAt(930));
        lights.Reset(start);
        var (previous, firstDark, reachedOn) = (0f, -1, -1);
        var frames = (int)((end - start) / Simulation.FrameSeconds);
        for (var f = 1; f <= frames; f++)
        {
            var time = start + f * Simulation.FrameSeconds;
            lights.Step(time, Simulation.FrameSeconds);
            var lit = lights.Lit[Home];
            var height = Sun.Height(GroundBody.Planet, Vector3.Zero, time);
            if (height >= Scene.DuskSunHeight && firstDark < 0)
                Assert.Equal(0f, lit);
            if (height < Scene.DuskSunHeight && firstDark < 0)
                firstDark = f;
            Assert.True(lit - previous <= Simulation.FrameSeconds / 3f + 1e-6f);
            if (lit >= 1f && reachedOn < 0)
                reachedOn = f;
            previous = lit;
        }
        Assert.True(firstDark > 0);
        Assert.True(reachedOn > 0 && reachedOn - firstDark <= 3f / Simulation.FrameSeconds + 2);
    }

    [Fact]
    public void At_dawn_they_fade_off_the_same_way()
    {
        var lights = new PlaceLights();
        var (start, end) = (HomeAt(270), HomeAt(360));
        lights.Reset(start);
        var (previous, firstLight, reachedOff) = (1f, -1, -1);
        var frames = (int)((end - start) / Simulation.FrameSeconds);
        for (var f = 1; f <= frames; f++)
        {
            var time = start + f * Simulation.FrameSeconds;
            lights.Step(time, Simulation.FrameSeconds);
            var lit = lights.Lit[Home];
            var height = Sun.Height(GroundBody.Planet, Vector3.Zero, time);
            if (height <= Scene.DawnSunHeight && firstLight < 0)
                Assert.Equal(1f, lit);
            if (height > Scene.DawnSunHeight && firstLight < 0)
                firstLight = f;
            Assert.True(previous - lit <= Simulation.FrameSeconds / 3f + 1e-6f);
            if (lit <= 0f && reachedOff < 0)
                reachedOff = f;
            previous = lit;
        }
        Assert.True(firstLight > 0);
        Assert.True(reachedOff > 0 && reachedOff - firstLight <= 3f / Simulation.FrameSeconds + 2);
    }

    [Fact]
    public void Held_between_the_two_heights_they_keep_their_state()
    {
        var time = BetweenHeights();
        var lights = new PlaceLights();
        lights.Reset(HomeAt(0));
        for (var f = 0; f < 600; f++)
            lights.Step(time, Simulation.FrameSeconds);
        Assert.Equal(1f, lights.Lit[Home]);
        Assert.True(lights.IsDark(Home));
        lights.Reset(HomeAt(600));
        for (var f = 0; f < 600; f++)
            lights.Step(time, Simulation.FrameSeconds);
        Assert.Equal(0f, lights.Lit[Home]);
        Assert.False(lights.IsDark(Home));
    }

    [Fact]
    public void Each_place_switches_at_its_own_dark()
    {
        var noon = HomeAt(600);
        Assert.True(Sun.Height(GroundBody.Planet, Vector3.Zero, noon) > 0.5f);
        var night = Enumerable.Range(0, PadLights.All.Count)
            .Where(i => Sun.Height(GroundBody.Planet, PadLights.All[i].Origin, noon) < 0f).ToList();
        Assert.NotEmpty(night);
        var lights = new PlaceLights();
        lights.Reset(noon);
        Assert.Equal(0f, lights.Lit[Home]);
        Assert.Equal(1f, lights.Lit[night[0]]);
    }

    [Fact]
    public void A_world_loaded_between_the_heights_has_what_continuous_play_would()
    {
        var lights = new PlaceLights();
        lights.Reset(BetweenHeights()); // just after sunrise: the night's lights are still on
        Assert.Equal(1f, lights.Lit[Home]);
        Assert.True(lights.IsDark(Home));
        lights.Reset(BetweenHeightsInTheEvening()); // just after the day's: still off
        Assert.Equal(0f, lights.Lit[Home]);
        Assert.False(lights.IsDark(Home));
    }

    [Fact]
    public void The_pad_lights_reading_starts_with_the_fraction_then_names_the_place()
    {
        using var sim = new Simulation(world: new WorldState { Time = Sun.WorldTimeFor(Vector3.Zero, 0) });
        var reading = Readout.FlightReadings(sim.Flight)["Pad lights"];
        Assert.StartsWith("1.00, ", reading);
        Assert.Contains(PadLights.All[Home].Name, reading);
    }

    [Fact]
    public void Stepping_allocates_nothing()
    {
        var lights = new PlaceLights();
        lights.Reset(HomeAt(0));
        lights.Step(HomeAt(0), Simulation.FrameSeconds);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            lights.Step(HomeAt(0) + i * 0.1, Simulation.FrameSeconds);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void A_world_loaded_at_night_starts_with_its_lights_on()
    {
        using var sim = new Simulation(world: new WorldState { Time = Sun.WorldTimeFor(Vector3.Zero, 0) });
        Assert.Equal(1f, sim.Flight.Lights.Lit[Home]);
        sim.Step(default);
        Assert.Equal(1f, sim.Flight.Lights.Lit[Home]);
    }

    [Fact]
    public void Under_the_warp_the_fade_still_takes_its_real_seconds()
    {
        using var sim = new Simulation(world: new WorldState { Time = HomeAt(880) });
        var lights = sim.Flight.Lights;
        Assert.Equal(0f, lights.Lit[Home]);
        // The warp as the rate keys raise it, one rate a frame.
        for (var i = 0; i < 5; i++)
            sim.Step(new FlightInput(Faster: true));
        Assert.Equal(32f, sim.Flight.TimeScale);
        var limit = 20000;
        while (lights.Lit[Home] <= 0f && limit-- > 0)
            sim.Step(default);
        Assert.True(lights.Lit[Home] > 0f);
        var frames = 0;
        while (lights.Lit[Home] < 1f && limit-- > 0)
        {
            sim.Step(default);
            frames++;
        }
        Assert.Equal(1f, lights.Lit[Home]);
        Assert.Equal(32f, sim.Flight.TimeScale);
        Assert.True(frames >= 3f / Simulation.FrameSeconds - 2, $"{frames} frames at {sim.Flight.TimeScale}x");
    }

    [Fact]
    public void The_dark_test_has_a_gap_between_dusk_and_dawn()
    {
        var time = BetweenHeights();
        Assert.True(Sun.IsDark(GroundBody.Planet, Vector3.Zero, time, wasDark: true));
        Assert.False(Sun.IsDark(GroundBody.Planet, Vector3.Zero, time, wasDark: false));
    }
}
