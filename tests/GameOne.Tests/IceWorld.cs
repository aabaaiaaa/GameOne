using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Writes a saved world for looking at the sea ice in the running game (the GameDriver's
/// <c>tools/GameDriver/examples/gameone-ice.txt</c>): the icy seas are some 14 km from the pad,
/// further than a script can fly, so the world starts where the ice is. It holds one booster,
/// hovering 150 m over the Austral Sea's frozen sheet at its edge, its engine at the throttle that holds
/// it there and fuel to spare, its legs out; the floe band lies beyond the edge. It is saved
/// through a <see cref="WorldStore"/>, so it is a world like any other, and nothing is added to the
/// game for it. Only with <c>GAMEONE_WRITE_ICE_WORLD</c> set to the folder to write it into (the
/// driver's <c>GAMEONE_WORLDS</c>); otherwise skipped.
/// </summary>
public sealed class IceWorld(ITestOutputHelper output)
{
    /// <summary>How high over the ice the booster hovers.</summary>
    private const float Height = 150f;

    /// <summary>
    /// Which icy sea: the Austral. The world starts at its local noon (<see cref="Sun.WorldTimeFor"/>),
    /// so the sun is as high over the ice as it gets there.
    /// </summary>
    private const string IcySea = "Austral Sea";

    [IceWorldFact]
    public void Writes_the_ice_world()
    {
        var folder = Environment.GetEnvironmentVariable(IceWorldFactAttribute.Variable)!;
        var edge = IceTests.SheetEdge(IcySea);
        var ground = Planet.Centre + edge * (Planet.SeaRadius + Terrain.Height(edge));
        var up = Planet.Up(ground);
        var rotation = Planet.UprightAt(ground);
        var noon = Sun.WorldTimeFor(ground, Scene.DaySeconds / 2);
        Assert.True(Vector3.Dot(up, Sun.Direction(GroundBody.Planet, noon)) > 0.3f, $"the sun is too low over {IcySea} to see the ice by");
        var vehicle = Vehicle.Booster;
        VesselState Booster(float throttle) => new()
        {
            Id = 0, Flight = 1, Mission = Mission.LaunchAndLand, Vehicle = vehicle, First = 0, Last = vehicle.Stages.Length - 1,
            Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * 0.6f)],
            State = FlightState.Flying, Position = ground + up * Height, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 0, vehicle.Stages.Length - 1),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero, Throttle = throttle,
            Aim = up, LegsWanted = true, LegDeploy = 1f, MaxAltitude = Height, HasBeenAirborne = true,
        };
        WorldState World(float throttle) => new() { Time = noon, Vessels = [Booster(throttle)], NextVesselId = 1, Flights = 1 };

        // The throttle that holds it up: its weight over its engine's full thrust.
        float hover;
        using (var sim = new Simulation(world: World(0f)))
        {
            var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
            hover = Planet.Gravity(booster.Position).Length() / booster.MaxAcceleration;
        }

        // It does hover: left alone for a few seconds it stays about where it is.
        using (var sim = new Simulation(world: World(hover)))
        {
            var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
            sim.Step(new FlightInput(SwitchTo: booster));
            sim.Run((_, _) => default, _ => false, maxSeconds: 5);
            output.WriteLine($"at throttle {hover:0.000}: {booster.State}, {booster.Ground:0.0} m up after 5 s, at {Vector3.Dot(booster.Velocity, up):0.00} m/s");
            Assert.Equal(FlightState.Flying, booster.State);
            Assert.True(booster.EngineLit);
            Assert.InRange(booster.Ground, Height - 10f, Height + 10f);
        }

        var store = new WorldStore(folder);
        var now = DateTime.Now;
        store.Save(World(hover) with { Id = "ice", Name = "Sea ice: over the Austral Sea's edge", Created = now, LastPlayed = now });
        var saved = store.Load("ice");
        Assert.Single(saved.Vessels);
        Assert.Equal(hover, saved.Vessels[0].Throttle);
        output.WriteLine($"written to {Path.Combine(folder, "ice")}: a booster {Height} m over the {IcySea}'s sheet at its edge");
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that only runs with <c>GAMEONE_WRITE_ICE_WORLD</c> set (to the
/// folder to write the ice world into): it is for the GameDriver, not a test of the game.
/// </summary>
public sealed class IceWorldFactAttribute : FactAttribute
{
    public const string Variable = "GAMEONE_WRITE_ICE_WORLD";

    public IceWorldFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is null or "")
            Skip = $"Writes a world for the GameDriver: set {Variable} to the folder to write it into.";
    }
}
