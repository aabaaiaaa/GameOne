using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The light averaged at the high rates (<see cref="AveragedLight"/>): from
/// <see cref="Scene.AveragedLightRate"/> up the day passes too fast to show, so the light eases to
/// its average over <see cref="Scene.AveragedLightEaseSeconds"/> of real time, and back as the warp
/// drops; meanwhile the pad and landing lights hold at their place's dark share of a day
/// (<see cref="Sun.DarkShare"/>).
/// </summary>
public sealed class AveragedLightTests(ITestOutputHelper output)
{
    private const float Frame = Simulation.FrameSeconds;

    private static readonly int Home = Enumerable.Range(0, PadLights.All.Count)
        .MinBy(i => PadLights.All[i].Origin.Length());

    private static double HomeAt(double localSeconds) => Sun.WorldTimeFor(Vector3.Zero, localSeconds);

    /// <summary><paramref name="light"/> stepped at <paramref name="rate"/> for <paramref name="seconds"/> of real time, a 60 fps frame at a time.</summary>
    private static void Run(AveragedLight light, float rate, float seconds)
    {
        for (var i = 0; i < (int)Math.Round(seconds / Frame); i++)
            light.Step(rate, Frame);
    }

    [Fact]
    public void Above_256x_the_light_eases_to_averaged_and_back_as_the_warp_drops()
    {
        var light = new AveragedLight();
        Assert.Equal(0f, light.Weight);
        Run(light, Scene.AveragedLightRate, 0.25f);
        Assert.Equal(0.5f, light.Weight, 1e-4f);
        Run(light, Scene.AveragedLightRate, 0.25f);
        Assert.Equal(1f, light.Weight, 1e-4f);
        Run(light, Scene.TopRate, 1f);
        Assert.Equal(1f, light.Weight);
        Run(light, Scene.AveragedLightRate / 2, 0.25f);
        Assert.Equal(0.5f, light.Weight, 1e-4f);
        Run(light, Scene.AveragedLightRate / 2, 0.25f);
        Assert.Equal(0f, light.Weight, 1e-4f);
        Run(light, 1f, 1f);
        Assert.Equal(0f, light.Weight);

        // Reset straight to where the rate puts it, with no ease.
        light.Reset(Scene.TopRate);
        Assert.Equal(1f, light.Weight);
        light.Reset(1f);
        Assert.Equal(0f, light.Weight);
    }

    [Fact]
    public void Home_s_dark_share_is_its_night_s_share_of_the_day()
    {
        var home = Sun.DarkShare(GroundBody.Planet, Vector3.Zero, HomeAt(600));
        output.WriteLine($"Home's dark share {home:0.000}");
        Assert.InRange(home, 0.35f, 0.65f);
        // The same at any time of its day: a share of the whole day, not of the moment.
        foreach (var local in new[] { 0.0, 300.0, 900.0 })
            Assert.Equal(home, Sun.DarkShare(GroundBody.Planet, Vector3.Zero, HomeAt(local)), 1e-3f);

        // The moon's landing spot, by the moon's own day.
        var spot = PointOfInterest.MoonLandingSpot.Place;
        var moon = Sun.DarkShare(GroundBody.Moon, spot, 0);
        output.WriteLine($"the moon's landing spot's dark share {moon:0.000}");
        Assert.InRange(moon, 0.35f, 0.65f);
    }

    [Fact]
    public void A_pad_s_lights_hold_at_its_dark_share_while_averaged()
    {
        var lights = new PlaceLights();
        var start = HomeAt(600);
        lights.Reset(start);
        // A second of real time at the top rate: some fourteen of the planet's days.
        for (var f = 1; f <= 60; f++)
        {
            var time = start + f * Frame * Scene.TopRate;
            lights.Step(time, Frame, averaged: 1f);
            for (var i = 0; i < PadLights.All.Count; i++)
            {
                var place = PadLights.All[i].Origin;
                Assert.Equal(Sun.DarkShare(GroundBody.Planet, place, time), lights.Lit[i], 1e-3f);
            }
        }
        Assert.InRange(lights.Lit[Home], 0.35f, 0.65f);
    }

    [Fact]
    public void A_landing_light_holds_at_its_dark_share_near_the_ground_while_averaged()
    {
        var zone = Scene.LandingZoneCentre;
        var standing = Drops.Placed(zone, Planet.East(zone), 0f) with { State = FlightState.Landed };
        var start = HomeAt(600);
        using var sim = new Simulation(world: new WorldState { Time = start, Vessels = [standing], NextVesselId = 1 });
        var booster = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        booster.LeavePhysics(start);
        booster.ResetLandingLights(start);
        Assert.Equal(0f, booster.LandingLights); // noon: off
        for (var f = 1; f <= 60; f++)
        {
            var time = start + f * Frame * Scene.TopRate;
            booster.StepLandingLights(time, Frame, averaged: 1f);
            Assert.Equal(Sun.DarkShare(booster.Body, booster.Position, time), booster.LandingLights, 1e-3f);
        }
        Assert.InRange(booster.LandingLights, 0.35f, 0.65f);
    }

    [Fact]
    public void At_the_top_rate_the_flight_s_light_is_averaged_and_the_drop_brings_the_true_light_back()
    {
        var (sim, _) = WarpScenes.LowOrbit();
        using var __ = sim;
        var flight = sim.Flight;
        Assert.Equal(0f, flight.Light.Weight);
        FormulaRateTests.ToTheTop(flight);
        for (var i = 0; i < 30; i++)
            sim.Step(default);
        Assert.Equal(1f, flight.Light.Weight, 1e-4f);
        Assert.Equal(Sun.DarkShare(GroundBody.Planet, PadLights.All[Home].Origin, flight.WorldTime), flight.Lights.Lit[Home], 1e-3f);

        // Down to normal speed: the true light back within the ease, and the pad's lights fading
        // to its own dark or light within their fade.
        while (flight.PlayerRate > 1f)
            flight.RunFrame(0f, new FlightInput(Slower: true));
        for (var i = 0; i < 30; i++)
            sim.Step(default);
        Assert.Equal(0f, flight.Light.Weight, 1e-4f);
        for (var i = 0; i < (Scene.PadLightFadeSeconds + 0.1f) / Frame; i++)
            sim.Step(default);
        Assert.Equal(flight.Lights.IsDark(Home) ? 1f : 0f, flight.Lights.Lit[Home]);
    }
}
