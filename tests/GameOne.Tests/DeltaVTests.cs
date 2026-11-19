using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The delta-v left (<see cref="Vessel.DeltaV"/>, <see cref="Vessel.DeltaVInAll"/>), as the HUD
/// and the readout show it: what a burn in vacuum gives, none with no fuel, and every stage still
/// on a stack burnt in turn, as worked out by hand from the stage table.
/// </summary>
public sealed class DeltaVTests(ITestOutputHelper output)
{
    /// <summary>The rocket equation for one stage's burn, by hand: <paramref name="fuel"/> of it burnt with <paramref name="mass"/> on the engine.</summary>
    private static double Burn(Stage stage, double mass, double fuel) =>
        stage.Thrust / (stage.FuelMass / stage.BurnSeconds) * Math.Log(mass / (mass - fuel));

    /// <summary>The satellite launcher's upper stage alone, half its fuel left (or <paramref name="fuel"/>), on a round orbit 500 m over the planet's air, flown.</summary>
    private static Simulation InVacuum(out Vessel stage, float[]? fuel = null)
    {
        var state = InOrbit.Round(GroundBody.Planet, 0, 500f);
        if (fuel is not null)
            state = state with { Fuel = fuel };
        var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1, Flights = 1 });
        stage = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: stage));
        return sim;
    }

    [Fact]
    public void A_burn_in_vacuum_gives_the_delta_v_it_uses()
    {
        // Two flights from the same place, one burning and one coasting: what is between them at
        // the end is the burn's alone, gravity's pull the same in both (but for the few hundred
        // metres the burn moves it, far too little to matter here).
        using var burning = InVacuum(out var burner);
        using var coasting = InVacuum(out var coaster);
        Assert.True(burner.Body.AboveAir(burner.CentreOfMass), "it should start above the air");
        var before = burner.DeltaV;
        Assert.True(before > 0f);
        const double Seconds = 5;
        burning.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: Seconds);
        burning.Step(new FlightInput(CutThrottle: true));
        coasting.Run((_, _) => default, _ => false, maxSeconds: Seconds);
        coasting.Step(new FlightInput());
        Assert.True(burner.Body.AboveAir(burner.CentreOfMass), "it should still be above the air");
        var used = before - burner.DeltaV;
        var gained = (burner.Body.AmongStars(burner.CentreOfMass, burner.Velocity) - coaster.Body.AmongStars(coaster.CentreOfMass, coaster.Velocity)).Length();
        output.WriteLine($"delta-v {before:0.0} m/s before, {burner.DeltaV:0.0} after: {used:0.00} used; the burn gave {gained:0.00} m/s");
        Assert.True(used > 50f, "the burn should use a good share of it");
        Assert.InRange(gained, used * 0.99f, used * 1.01f);
    }

    [Fact]
    public void A_vessel_with_no_fuel_has_none()
    {
        using var sim = InVacuum(out var stage, fuel: new float[Vehicle.ThreeStage.Stages.Length]);
        Assert.Equal(0f, stage.DeltaV);
        Assert.Equal(0f, stage.DeltaVInAll);
        Assert.Equal(0f, sim.Flight.DeltaV);
    }

    [Fact]
    public void A_two_stage_stack_has_each_stage_burnt_in_turn()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        sim.Step(new FlightInput());
        var flight = sim.Flight;
        Assert.Equal(Vehicle.TwoStage, flight.Controlled.Vehicle);
        Assert.Equal(0, flight.Stage);
        var (booster, upper) = (Vehicle.TwoStage.Stages[0], Vehicle.TwoStage.Stages[1]);
        // The booster carries the upper stage, full, as it burns; then the upper stage on its own.
        double whole = booster.DryMass + booster.FuelMass + upper.DryMass + upper.FuelMass;
        var first = Burn(booster, whole, booster.FuelMass);
        var second = Burn(upper, upper.DryMass + upper.FuelMass, upper.FuelMass);
        output.WriteLine($"booster {first:0.0} m/s, upper stage {second:0.0}: {flight.DeltaV:0.0} and {flight.DeltaVInAll:0.0} in all");
        Assert.Equal(first, flight.DeltaV, 0.01 * first / 100);
        Assert.Equal(first + second, flight.DeltaVInAll, 0.01 * (first + second) / 100);
        Assert.True(flight.DeltaVInAll > flight.DeltaV);
    }

    [Fact]
    public void A_payload_s_fairing_is_carried_until_the_payload_is_let_go()
    {
        using var sim = new Simulation(Mission.DeploySatellite);
        sim.Step(new FlightInput());
        var flight = sim.Flight;
        Assert.Equal(Vehicle.ThreeStage, flight.Controlled.Vehicle);
        Assert.True(flight.FairingOn);
        var stages = Vehicle.ThreeStage.Stages;
        double fairing = stages[2].FairingGeometry.Mass;
        double Full(int i) => stages[i].DryMass + stages[i].FuelMass;
        var expected = Burn(stages[0], Full(0) + Full(1) + Full(2) + fairing, stages[0].FuelMass)
            + Burn(stages[1], Full(1) + Full(2) + fairing, stages[1].FuelMass)
            + Burn(stages[2], Full(2), stages[2].FuelMass);
        output.WriteLine($"{flight.DeltaVInAll:0.0} m/s in all, {expected:0.0} by hand");
        Assert.Equal(flight.Mass, Full(0) + Full(1) + Full(2) + fairing, 0.5);
        Assert.Equal(expected, flight.DeltaVInAll, 0.01 * expected / 100);
    }
}
