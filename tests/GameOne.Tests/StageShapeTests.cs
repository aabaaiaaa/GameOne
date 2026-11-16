using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Each stage's legs and fairing by its own shape (<see cref="LegGeometry"/>, <see cref="FairingGeometry"/>):
/// the existing stages have the rocket's legs and the satellite's fairing, and a stage of another
/// shape stands on its own feet and is as big, and as heavy, as its own fairing.
/// </summary>
public sealed class StageShapeTests(ITestOutputHelper output)
{
    [Fact]
    public void The_existing_stages_have_the_rockets_legs_and_the_satellites_fairing()
    {
        // The lander's own shapes, and its launcher's, are LanderTests'.
        foreach (var vehicle in (Vehicle[])[Vehicle.Booster, Vehicle.TwoStage, Vehicle.ThreeStage, Vehicle.FairingHalf])
            foreach (var stage in vehicle.Stages)
            {
                if (stage.Kind == StageKind.Fairing)
                {
                    Assert.Same(FairingGeometry.Satellite, stage.FairingGeometry); // the satellite's fairing's half
                    continue;
                }
                Assert.Same(LegGeometry.Rocket, stage.LegGeometry);
                if (stage.Fairing)
                    Assert.Same(FairingGeometry.Satellite, stage.FairingGeometry);
            }
        Assert.Same(Vehicle.FairingHalf, Vehicle.HalfOf(FairingGeometry.Satellite));
    }

    [Fact]
    public void A_stages_feet_are_its_own_legs()
    {
        var rocket = LegGeometry.Rocket;
        var longer = rocket with { Length = 2f * rocket.Length };
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            var (theirs, ours) = (RocketGeometry.Foot(rocket, leg), RocketGeometry.Foot(longer, leg));
            Assert.True(Out(ours) > Out(theirs) + 1f, $"leg {leg}'s foot is {Out(ours):0.00} m out, the rocket's {Out(theirs):0.00} m");
            Assert.True(ours.Y < theirs.Y - 1f, $"leg {leg}'s foot is {ours.Y:0.00} m up, the rocket's {theirs.Y:0.00} m");
        }

        var vehicle = Vehicle.Booster with { Stages = [Vehicle.Booster.Stages[0] with { LegGeometry = longer }] };
        using var sim = Drops.OnPad(0, 1f, vehicle);
        var booster = sim.Flight.Controlled;
        sim.Run((_, _) => default, _ => booster.State != FlightState.Flying, 20);
        sim.Run((_, _) => default, _ => false, 3);
        Assert.Equal(FlightState.Landed, booster.State);
        var e = booster.Craft.LegExtension;
        output.WriteLine($"landed on its own legs: lean {booster.TiltDegrees:0.00}°, extensions {e.X:0.00}, {e.Y:0.00}, {e.Z:0.00}, {e.W:0.00}");
        // Each of its own feet within its stroke of the pad, where the rocket's would be well up off it.
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            var own = Planet.GroundAltitude(Vector3.Transform(RocketGeometry.Foot(longer, leg), booster.Model));
            var rockets = Planet.GroundAltitude(Vector3.Transform(RocketGeometry.Foot(rocket, leg), booster.Model));
            Assert.InRange(own, -0.1f, longer.Stroke);
            Assert.True(rockets > 2f, $"the rocket's foot {leg} would be {rockets:0.00} m up");
        }
        Assert.True(booster.Width > Out(RocketGeometry.Foot(rocket, 0)) + 1f, $"it reaches {booster.Width:0.00} m out: its feet's");
    }

    [Fact]
    public void A_fairing_is_as_big_as_its_geometry()
    {
        var fairing = new FairingGeometry(Radius: 2f, Height: 4f, NoseHeight: 3f, Mass: 500f);
        var stages = Vehicle.ThreeStage.Stages;
        var vehicle = Vehicle.ThreeStage with { Stages = [stages[0], stages[1], stages[2] with { FairingGeometry = fairing }] };
        var centre = Scene.TestPads[0].Centre;
        using (var sim = Drops.At(centre, Planet.East(centre), 0f, vehicle, first: 1, mission: Mission.DeploySatellite, flight: 1, clearance: 500f, fairingOn: true, legsOut: false))
        {
            var stack = sim.Flight.Controlled;
            Assert.True(stack.FairingOn);
            Assert.Equal(RocketGeometry.StageBase(vehicle, 1, 2) + fairing.Height + fairing.NoseHeight, stack.Height, 3);
            Assert.True(stack.Width >= fairing.Radius, $"it reaches {stack.Width:0.00} m out, inside its fairing's {fairing.Radius} m");
            var stagesMass = stack.Mass - fairing.Mass;
            var expected = 0f;
            for (var i = 1; i <= 2; i++)
                expected += vehicle.Stages[i].DryMass + 0.3f * vehicle.Stages[i].FuelMass; // as Drops fills them, its engine off
            Assert.Equal(expected, stagesMass, 1);
        }

        // Jettisoned, it comes away as two halves of its own vehicle.
        using (var sim = Drops.At(centre, Planet.East(centre), 0f, Vehicle.ThreeStage, first: 1, mission: Mission.DeploySatellite, flight: 1, clearance: 500f, fairingOn: true, legsOut: false))
        {
            Assert.True(sim.Flight.JettisonsFairing);
            sim.Step(new FlightInput(Stage: true));
            Assert.False(sim.Flight.FairingOn);
            var halves = sim.Flight.WorldVessels.Where(v => v.IsFairing).ToArray();
            Assert.Equal(2, halves.Length);
            Assert.All(halves, half => Assert.Same(Vehicle.HalfOf(FairingGeometry.Satellite), half.Vehicle));
            Assert.All(halves, half => Assert.Same(FairingGeometry.Satellite, half.CurrentStage.FairingGeometry));
            Assert.All(halves, half => Assert.Equal(FairingGeometry.Satellite.Mass / 2f, half.Mass, 3));
        }
    }

    private static float Out(Vector3 foot) => MathF.Sqrt(foot.X * foot.X + foot.Z * foot.Z);
}
