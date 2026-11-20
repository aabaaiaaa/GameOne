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
        return (sim, Disposed(sim, () => Fly(sim, id)));
    }

    /// <summary>What <paramref name="make"/> makes of <paramref name="sim"/>, which is disposed if that fails (an assert in it), so a failed scene leaks no flight.</summary>
    private static T Disposed<T>(Simulation sim, Func<T> make)
    {
        try
        {
            return make();
        }
        catch
        {
            sim.Dispose();
            throw;
        }
    }

    /// <summary>The vessel numbered <paramref name="id"/> switched to and flown in <paramref name="sim"/>'s flight.</summary>
    private static Vessel Fly(Simulation sim, int id)
    {
        var flown = sim.Flight.WorldVessels.Single(v => v.Id == id);
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Step(default);
        Assert.Same(flown, sim.Flight.Controlled);
        return flown;
    }

    /// <summary>
    /// <paramref name="sim"/>'s flight begun again in <paramref name="world"/> (loaded afresh into
    /// the same flight, as a world picked from the menu is), the vessel numbered <paramref name="id"/>
    /// flown: for a scene measured over more frames than it lasts at the top rate (the moon
    /// transfer's comes down to the moon's ground, and drops the warp, within two real seconds).
    /// </summary>
    public static Vessel Again(Simulation sim, WorldState world, int id)
    {
        sim.Flight.Load(world, Mission.FreePlay);
        return Fly(sim, id);
    }

    /// <summary>The world <see cref="MoonTransfer"/> is flown in, and its vessel flown.</summary>
    public static (WorldState World, int Id) MoonTransferWorld()
    {
        var (state, time) = MoonTrip.Transferring(0, TransferSeconds);
        return (MoonTrip.World(time, state), state.Id);
    }

    /// <summary>The upper stage <see cref="TransferSeconds"/> after the moon transfer's burn, on the planet's leg of the path.</summary>
    public static (Simulation Sim, Vessel Flown) MoonTransfer()
    {
        var (world, id) = MoonTransferWorld();
        var scene = Flying(world, id);
        return Disposed(scene.Sim, () =>
        {
            Assert.Same(GroundBody.Planet, scene.Flown.Body); // still on the planet's leg, not yet in the moon's sphere
            var flown = scene.Flown;
            var legs = new List<PathLeg>(Scene.PatchedLegs);
            Patched.Legs(flown.Body, flown.CentreOfMass, flown.Body.AmongStars(flown.CentreOfMass, flown.Velocity), scene.Sim.Flight.WorldTime, legs);
            Assert.True(legs.Count > 1 && legs[1].Body == GroundBody.Moon, "the transfer's path goes on into the moon's sphere");
            return scene;
        });
    }

    /// <summary>The upper stage on a round orbit <see cref="LowOrbitHeight"/> over the air's top.</summary>
    public static (Simulation Sim, Vessel Flown) LowOrbit() => Flying(InOrbit.Round(GroundBody.Planet, 0, LowOrbitHeight));

    /// <summary>A hundred satellites coasting round the planet, the first flown.</summary>
    public static (Simulation Sim, Vessel Flown) HundredSatellites()
    {
        var world = WorldTests.HundredSatellites();
        return Flying(world, world.Vessels[0].Id);
    }

    /// <summary>
    /// A full world (<see cref="Flight.Capacity"/>, less room for free play's rocket): the
    /// satellites preset's four a copy, the polar orbit's copies raised apart
    /// (<see cref="WorldTests.ManySatellites"/>), so it never wrecks itself, and the one flown 500 m
    /// over the highest of them, across all their planes: every window asks after the whole world
    /// (Collisions), with nothing coming.
    /// </summary>
    public static (Simulation Sim, Vessel Flown) FullWorld()
    {
        const int Copies = Flight.Capacity / 4 - 1;
        var ring = WorldTests.ManySatellites(Copies, polarApart: true);
        var highest = 10000f + WorldTests.PolarApart * (Copies - 1); // the preset's polar orbit, 10 km up, raised for each copy after the first
        var flown = UnwatchedTests.Satellite(ring.NextVesselId, highest + 500f, 45f, 0f);
        var world = ring with { Vessels = [.. ring.Vessels, flown], NextVesselId = ring.NextVesselId + 1 };
        return Flying(world, flown.Id);
    }
}
