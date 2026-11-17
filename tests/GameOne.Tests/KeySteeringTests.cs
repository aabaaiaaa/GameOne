using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The keys steer in the vessel's own frame (<see cref="Vessel.Right"/>, <see cref="Vessel.Forward"/>),
/// as <see cref="Controls"/> turns them into a lean: on the pad they are east and north, and since
/// they turn with the vessel, holding one keeps it turning, round in a loop.
/// </summary>
public sealed class KeySteeringTests(ITestOutputHelper output)
{
    [Fact]
    public void On_the_pad_right_is_east_and_forward_is_north()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var rocket = sim.Flight.Controlled;
        var p = rocket.CentreOfMass;
        Assert.True(Vector3.Dot(rocket.Right, Planet.East(p)) > 0.999f, $"right is {rocket.Right}");
        Assert.True(Vector3.Dot(rocket.Forward, Planet.North(p)) > 0.999f, $"forward is {rocket.Forward}");
    }

    [Fact]
    public void Retrograde_hold_points_the_nose_against_the_way_it_is_moving()
    {
        // A satellite in orbit, pointing straight up: retrograde is a quarter turn away, behind it.
        // (With the engine off only its thrusters turn it, so give it a while.)
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.Flight == 0)));
        sim.Step(new FlightInput(ChooseHold: HoldMode.Retrograde));
        Assert.True(flight.Holding == HoldMode.Retrograde);
        sim.Run((_, _) => default, _ => false, maxSeconds: 60);

        var off = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(flight.Controlled.Up), -Vector3.Normalize(flight.Velocity)), -1f, 1f)));
        output.WriteLine($"nose {off:0.0}° off retrograde");
        Assert.True(off < 5f, $"the nose is {off:0.0}° off retrograde");

        // One or the other: prograde hold turns it off.
        sim.Step(new FlightInput(ChooseHold: HoldMode.Prograde));
        Assert.True(flight.Holding == HoldMode.Prograde);
        Assert.False(flight.Holding == HoldMode.Retrograde);
    }

    [Fact]
    public void Choosing_a_hold_again_turns_it_off()
    {
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.Flight == 0)));
        sim.Step(new FlightInput(ChooseHold: HoldMode.Prograde));
        Assert.Equal(HoldMode.Prograde, flight.Holding);
        sim.Step(new FlightInput(ChooseHold: HoldMode.Retrograde));
        Assert.Equal(HoldMode.Retrograde, flight.Holding);
        sim.Step(new FlightInput(ChooseHold: HoldMode.Retrograde));
        Assert.Equal(HoldMode.None, flight.Holding);

        // Steering lets go of one on.
        sim.Step(new FlightInput(ChooseHold: HoldMode.Prograde));
        sim.Step(new FlightInput(Lean: Vector3.UnitX));
        Assert.Equal(HoldMode.None, flight.Holding);
    }

    [Fact]
    public void P_and_R_do_nothing_now()
    {
        // The keys are not in the actions Controls reads, so no key can choose a hold.
        Assert.DoesNotContain(Enum.GetNames<ControlAction>(), name => name.Contains("Hold", StringComparison.Ordinal));
    }

    [Fact]
    public void Holding_forward_loops_the_vessel_round()
    {
        // A satellite in orbit: no air to turn it back, so it should keep going round.
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var satellite = flight.WorldVessels.First(v => v.Flight == 0);
        sim.Step(new FlightInput(SwitchTo: satellite));
        Assert.Same(satellite, flight.Controlled);

        var turned = 0f;
        var last = satellite.Up;
        var start = satellite.Right;
        Assert.True(sim.Run((f, _) => new FlightInput(Lean: f.Controlled.Forward), f =>
        {
            var up = f.Controlled.Up;
            turned += MathF.Acos(Math.Clamp(Vector3.Dot(last, up), -1f, 1f));
            last = up;
            return turned > MathF.Tau;
        }, maxSeconds: 300), $"held forward, it turned only {float.RadiansToDegrees(turned):0} degrees");

        // Round about its own right: that axis hardly moved (it doesn't roll as it loops).
        Assert.True(Vector3.Dot(start, satellite.Right) > 0.95f, $"its right moved from {start} to {satellite.Right}");
        output.WriteLine($"one loop in {sim.Time:0.0} s");
    }
}
