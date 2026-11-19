using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The scenes the warp is measured and tested in: a free-play flight with one vessel flown, in a
/// world of its own, at the moment the scene is about.
/// </summary>
internal static class WarpScenes
{
    /// <summary>How long after the transfer's burn the moon transfer scene is: the stage on its way, out of the low orbit, well short of the moon's sphere (some 330 s after the burn).</summary>
    public const double TransferSeconds = 120.0;

    /// <summary>How high over the air's top the low orbit is: its periapsis is clear of the air by a kilometre.</summary>
    public const float LowOrbitHeight = 1000f;

    /// <summary>A free-play flight flying <paramref name="states"/>' first (its others in the world beside it).</summary>
    public static (Simulation Sim, Vessel Flown) Flying(params VesselState[] states) =>
        Flying(new WorldState { Time = 0.0, Vessels = [.. states], NextVesselId = states.Length, Flights = 1 }, states[0].Id);

    /// <summary>A free-play flight in <paramref name="world"/>, the vessel numbered <paramref name="id"/> flown.</summary>
    public static (Simulation Sim, Vessel Flown) Flying(WorldState world, int id)
    {
        var sim = new Simulation(Mission.FreePlay, world: world);
        var flown = sim.Flight.WorldVessels.Single(v => v.Id == id);
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Step(default);
        Assert.Same(flown, sim.Flight.Controlled);
        return (sim, flown);
    }

    /// <summary>The upper stage <see cref="TransferSeconds"/> after the moon transfer's burn, on the planet's leg of the path.</summary>
    public static (Simulation Sim, Vessel Flown) MoonTransfer()
    {
        var (state, time) = MoonTrip.Transferring(0, TransferSeconds);
        var scene = Flying(MoonTrip.World(time, state), state.Id);
        Assert.Same(GroundBody.Planet, scene.Flown.Body); // still on the planet's leg, not yet in the moon's sphere
        var flown = scene.Flown;
        var legs = new List<PathLeg>(Scene.PatchedLegs);
        Patched.Legs(flown.Body, flown.CentreOfMass, flown.Body.AmongStars(flown.CentreOfMass, flown.Velocity), scene.Sim.Flight.WorldTime, legs);
        Assert.True(legs.Count > 1 && legs[1].Body == GroundBody.Moon, "the transfer's path goes on into the moon's sphere");
        return scene;
    }

    /// <summary>The upper stage on a round orbit <see cref="LowOrbitHeight"/> over the air's top.</summary>
    public static (Simulation Sim, Vessel Flown) LowOrbit() => Flying(InOrbit.Round(GroundBody.Planet, 0, LowOrbitHeight));

    /// <summary>A hundred satellites coasting round the planet, the first flown.</summary>
    public static (Simulation Sim, Vessel Flown) HundredSatellites()
    {
        var world = WorldTests.HundredSatellites();
        return Flying(world, world.Vessels[0].Id);
    }
}
