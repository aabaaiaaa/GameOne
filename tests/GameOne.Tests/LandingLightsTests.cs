using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// Each vessel's landing lights (<see cref="Vessel.LandingLights"/>): on in the dark where it is
/// and near the ground, kept on landed until dawn, faded over real seconds, for every vessel in
/// the world.
/// </summary>
public sealed class LandingLightsTests
{
    private static readonly Pilot Nobody = (_, _) => default;
    private static readonly Vector3 Zone = Scene.LandingZoneCentre;

    /// <summary>The most a frame's fade can move the lights: its share of the fade's real seconds.</summary>
    private const float FrameShare = Simulation.FrameSeconds / 3f + 1e-6f;

    private static double HomeAt(double localSeconds) => Sun.WorldTimeFor(Vector3.Zero, localSeconds);

    /// <summary>A world at <paramref name="time"/> holding <paramref name="vessels"/> (numbered from 0).</summary>
    private static Simulation World(double time, params VesselState[] vessels) =>
        new(world: new WorldState { Time = time, Vessels = [.. vessels], NextVesselId = vessels.Length });

    /// <summary>A world at <paramref name="time"/> holding <paramref name="state"/>, flown (so in the physics), and the vessel.</summary>
    private static (Simulation Sim, Vessel Vessel) Flown(double time, VesselState state)
    {
        var sim = World(time, state);
        var vessel = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        return (sim, vessel);
    }

    /// <summary>A booster standing on the landing zone, landed and frozen there, as saved.</summary>
    private static VesselState Standing()
    {
        using var sim = Drops.At(Zone, Planet.East(Zone), 1f);
        var booster = sim.Flight.Controlled;
        Assert.True(sim.Run(Nobody, _ => booster.Craft.Frozen, maxSeconds: 60), $"it never froze: {booster.State}");
        return sim.Flight.Capture().Vessels.Single(v => v.Id == 0);
    }

    /// <summary>
    /// A booster falling onto the zone from 800 m at Home's <paramref name="local"/> time, its
    /// lights each frame until it is down: the frame it was first below 500 m, the frame they
    /// first reached 1 (-1 if never), and the highest they were above 500 m.
    /// </summary>
    private static (int FirstBelow, int FirstOn, float HighestAbove) Falling(double local)
    {
        var (sim, booster) = Flown(HomeAt(local), Drops.Placed(Zone, Planet.East(Zone), 0f, clearance: 800f));
        using var _ = sim;
        Assert.True(Planet.SurfaceAltitude(booster.Position) > 750f);
        var (frame, firstBelow, firstOn, highestAbove, previous) = (0, -1, -1, 0f, booster.LandingLights);
        sim.Run(Nobody, _ =>
        {
            frame++;
            var lit = booster.LandingLights;
            Assert.True(lit - previous <= FrameShare, $"up {lit - previous} in a frame");
            previous = lit;
            if (Planet.SurfaceAltitude(booster.Position) > Scene.LandingLightsBelow)
                highestAbove = MathF.Max(highestAbove, lit);
            else if (firstBelow < 0)
                firstBelow = frame;
            if (lit >= 1f && firstOn < 0)
                firstOn = frame;
            return booster.State != FlightState.Flying;
        }, maxSeconds: 60);
        Assert.True(firstBelow > 0, "it never came below the height");
        return (firstBelow, firstOn, highestAbove);
    }

    [Fact]
    public void Coming_down_in_the_dark_they_come_on_below_the_height()
    {
        var (firstBelow, firstOn, highestAbove) = Falling(0);
        Assert.Equal(0f, highestAbove);
        Assert.True(firstOn > 0, "they never came fully on");
        Assert.InRange(firstOn - firstBelow, 0, 3f / Simulation.FrameSeconds + 2);
    }

    [Fact]
    public void By_day_they_stay_off()
    {
        var (_, firstOn, highestAbove) = Falling(600);
        Assert.Equal(0f, highestAbove);
        Assert.Equal(-1, firstOn);
    }

    /// <summary>
    /// Out of the physics, so nothing moves it but this: <paramref name="vessel"/> put
    /// <paramref name="altitude"/> above what is underfoot, as it stands.
    /// </summary>
    private static void HoldAt(Vessel vessel, float altitude)
    {
        var up = Planet.Up(vessel.Position);
        var by = altitude - Planet.SurfaceAltitude(vessel.Position);
        vessel.MoveByFormula(vessel.CentreOfMass + up * by, Vector3.Zero, vessel.FormulaRotation, Vector3.Zero, orbitChanged: false);
    }

    [Fact]
    public void High_up_they_stay_off_and_the_gap_holds()
    {
        var time = HomeAt(0);
        // Reset at 600 m (above the off height): off, and they stay off there.
        using (var sim = World(time, Drops.Placed(Zone, Planet.East(Zone), 0f, clearance: 600f)))
        {
            var v = sim.Flight.WorldVessels.Single(o => o.Id == 0);
            v.LeavePhysics(time);
            HoldAt(v, 600f);
            v.ResetLandingLights(time);
            Assert.Equal(0f, v.LandingLights);
            for (var f = 0; f < 600; f++)
                v.StepLandingLights(time, Simulation.FrameSeconds);
            Assert.Equal(0f, v.LandingLights);
        }
        // On at 400 m; held at 520 m, in the gap, they stay on; at 600 m they fade off.
        using (var sim = World(time, Drops.Placed(Zone, Planet.East(Zone), 0f, clearance: 400f)))
        {
            var v = sim.Flight.WorldVessels.Single(o => o.Id == 0);
            v.LeavePhysics(time);
            HoldAt(v, 400f);
            v.ResetLandingLights(time);
            Assert.Equal(1f, v.LandingLights);
            HoldAt(v, 520f);
            for (var f = 0; f < 600; f++)
                v.StepLandingLights(time, Simulation.FrameSeconds);
            Assert.Equal(1f, v.LandingLights);
            HoldAt(v, 600f);
            var (previous, frames) = (1f, 0);
            while (v.LandingLights > 0f && frames < 1000)
            {
                v.StepLandingLights(time, Simulation.FrameSeconds);
                Assert.True(previous - v.LandingLights <= FrameShare);
                previous = v.LandingLights;
                frames++;
            }
            Assert.Equal(0f, v.LandingLights);
            Assert.InRange(frames, 3f / Simulation.FrameSeconds - 2, 3f / Simulation.FrameSeconds + 2);
        }
    }

    [Fact]
    public void Landed_in_the_dark_they_stay_on_and_go_off_at_dawn()
    {
        var standing = Standing();
        using var sim = World(HomeAt(290), standing);
        var booster = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.Equal(1f, booster.LandingLights);
        var (previous, end) = (1f, HomeAt(340));
        sim.Run(Nobody, f =>
        {
            Assert.True(previous - booster.LandingLights <= FrameShare);
            previous = booster.LandingLights;
            return f.WorldTime >= end;
        }, maxSeconds: 60);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.Equal(0f, booster.LandingLights);
    }

    [Fact]
    public void A_fairing_half_and_a_wreck_have_none()
    {
        var time = HomeAt(0);
        var (sim, half) = Flown(time, Drops.Placed(Zone, Planet.East(Zone), 0f, vehicle: Vehicle.FairingHalf, legsOut: false, clearance: 5f));
        using (sim)
        {
            Assert.False(half.HasLandingLights);
            for (var f = 0; f < 60; f++)
            {
                sim.Step(default);
                Assert.Equal(0f, half.LandingLights);
            }
        }
        // A booster near the ground at night is lit; wrecked, it has none, from the next frame on
        // (no fade: the wreck is gone from view at once).
        var (wreckSim, booster) = Flown(time, Drops.Placed(Zone, Planet.East(Zone), 30f, clearance: 20f));
        using (wreckSim)
        {
            Assert.True(booster.HasLandingLights);
            Assert.Equal(1f, booster.LandingLights);
            Assert.True(wreckSim.Run(Nobody, _ => booster.State == FlightState.Crashed, maxSeconds: 10), $"it never crashed: {booster.State}");
            wreckSim.Step(default);
            Assert.Equal(0f, booster.LandingLights);
            for (var f = 0; f < 3f / Simulation.FrameSeconds + 2; f++)
                wreckSim.Step(default);
            Assert.Contains(booster, wreckSim.Flight.WorldVessels);
            Assert.Equal(0f, booster.LandingLights);
        }
    }

    [Fact]
    public void A_vessel_under_formula_switches_too()
    {
        // Far from the pad, landed on level ground: loaded under formula, nobody near it.
        var at = Planet.SurfacePoint(Drops.LevelGround.X, Drops.LevelGround.Z);
        var landed = Drops.Placed(at, Planet.East(at), 0f) with { State = FlightState.Landed };
        // A world starting 20 s of game time before its own dusk (its sun at the dusk height),
        // near 14:40 at Home.
        double a = Sun.WorldTimeFor(at, 870), b = Sun.WorldTimeFor(at, 930);
        Assert.True(Sun.Height(GroundBody.Planet, at, a) > Scene.DuskSunHeight && Sun.Height(GroundBody.Planet, at, b) < Scene.DuskSunHeight);
        for (var i = 0; i < 60; i++)
        {
            var mid = (a + b) / 2;
            if (Sun.Height(GroundBody.Planet, at, mid) > Scene.DuskSunHeight) a = mid; else b = mid;
        }
        using var sim = World(a - 20.0, landed);
        var v = sim.Flight.WorldVessels.Single(o => o.Id == 0);
        Assert.True(v.OnFormula);
        Assert.Equal(0f, v.LandingLights);
        // Under the warp, as the rate keys raise it, one rate a frame.
        for (var i = 0; i < 5; i++)
            sim.Step(new FlightInput(Faster: true));
        Assert.Equal(32f, sim.Flight.TimeScale);
        Assert.True(sim.Run(Nobody, _ => v.LandingLights >= 1f, maxSeconds: 600), $"{v.LandingLights}");
        Assert.True(v.OnFormula);
        Assert.Equal(FlightState.Landed, v.State);
    }

    [Fact]
    public void A_world_loaded_at_night_has_its_landed_vessels_lit_at_once()
    {
        var standing = Standing();
        using var sim = World(HomeAt(0), standing);
        var booster = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.Equal(1f, booster.LandingLights);
    }

    [Fact]
    public void A_stage_parting_near_the_ground_at_night_starts_lit()
    {
        var (sim, rocket) = Flown(HomeAt(0), Drops.Placed(Zone, Planet.East(Zone), 0f, vehicle: Vehicle.TwoStage, legsOut: false, clearance: 300f));
        using var _ = sim;
        Assert.Equal(1f, rocket.LandingLights);
        var before = sim.Flight.WorldVessels.ToHashSet();
        sim.Step(new FlightInput(Stage: true));
        var lower = sim.Flight.WorldVessels.Single(v => !before.Contains(v));
        Assert.Equal(0, lower.Stage);
        Assert.Equal(1f, lower.LandingLights);
        Assert.Equal(1f, rocket.LandingLights); // the upper stage flies on, lit
    }

    [Fact]
    public void Stepping_every_vessel_allocates_nothing()
    {
        var time = HomeAt(0);
        var boosters = Enumerable.Range(0, 50).Select(i =>
        {
            var at = Planet.SurfacePoint(Drops.LevelGround.X + 30f * (i % 10), Drops.LevelGround.Z + 30f * (i / 10));
            return Drops.Placed(at, Planet.East(at), 0f, id: i) with { State = FlightState.Landed };
        }).ToArray();
        // A few flying low over the ground (below LandingLightsBelow), so the terrain's lookup
        // for the near height is counted too.
        var flyers = Enumerable.Range(0, 5).Select(i =>
        {
            var at = Planet.SurfacePoint(Drops.LevelGround.X - 400f - 60f * i, Drops.LevelGround.Z);
            return Drops.Placed(at, Planet.East(at), 0f, clearance: 100f + 70f * i, id: 50 + i);
        }).ToArray();
        using var sim = World(time, [.. boosters, .. flyers]);
        var world = sim.Flight.WorldVessels;
        Assert.True(world.Count > 55);
        var flying = world.Where(v => v.Id >= 50 && v.Id < 55).ToArray();
        Assert.Equal(5, flying.Length);
        Assert.All(flying, v =>
        {
            Assert.Equal(FlightState.Flying, v.State);
            Assert.InRange(Planet.SurfaceAltitude(v.Position), 50f, Scene.LandingLightsBelow - 10f);
        });
        void StepAll(double t)
        {
            for (var i = 0; i < world.Count; i++)
                world[i].StepLandingLights(t, Simulation.FrameSeconds);
        }
        StepAll(time); // warm-up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 0; f < 100; f++)
            StepAll(time + f * Simulation.FrameSeconds);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.All(flying, v => Assert.Equal(1f, v.LandingLights)); // lit, so asked the ground under them
    }

    [Fact]
    public void The_readout_shows_the_flown_vessels_landing_lights()
    {
        using var sim = new Simulation(world: new WorldState { Time = HomeAt(0) });
        Assert.Equal("1.00", Readout.FlightReadings(sim.Flight)["Landing lights"]);
    }
}
