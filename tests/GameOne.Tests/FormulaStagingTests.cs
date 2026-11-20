using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A vessel under formula stages by the numbers (<see cref="Flight.StageVessel"/>): the bottom stage
/// dropped where it was in the stack, the rest going on with its own mass and fuel (so a burn after
/// gives the delta-v the rocket equation says), a fairing's halves each pushed out on its own side,
/// and nothing brought into the physics nor the warp changed. Nothing in the game stages a vessel
/// nobody flies yet (autopilots will), so these call it directly.
/// </summary>
public sealed class FormulaStagingTests(ITestOutputHelper output)
{
    /// <summary>
    /// Stages <paramref name="first"/> to <paramref name="last"/> of <paramref name="vehicle"/>
    /// (their tanks full, but the bottom stage's, empty as a stage is when it is dropped), numbered
    /// <paramref name="id"/>, coasting on a round orbit over the planet's equator at
    /// <paramref name="radius"/> from its centre, at <paramref name="angle"/> round from +X, standing
    /// up from the planet and going east, its throttle at <paramref name="throttle"/>.
    /// </summary>
    private static VesselState Coasting(int id, Vehicle vehicle, int first, int last, float radius, float angle, float throttle = 0f)
    {
        var body = GroundBody.Planet;
        var centre = body.Centre + new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * radius;
        var height = radius - body.SeaRadius;
        var stars = body.East(centre) * body.CircularSpeed(height);
        var rotation = body.UprightAt(centre);
        var offset = RocketGeometry.CentreOfMassOffset(vehicle, first, last);
        var fuel = vehicle.Stages.Select(s => s.FuelMass).ToArray();
        fuel[first] = 0f;
        return new VesselState
        {
            Id = id, Flight = 1, Mission = Mission.FreePlay, Vehicle = vehicle, First = first, Last = last, Fuel = fuel,
            FairingOn = vehicle.Stages[last].Fairing, Throttle = throttle,
            State = FlightState.Flying, Position = centre - Vector3.Transform(offset, rotation), Rotation = rotation,
            CentreOfMassOffset = offset, Velocity = body.OverGround(centre, stars), AngularVelocity = Vector3.Zero,
            Aim = Vector3.Transform(Vector3.UnitY, rotation), HasBeenAirborne = true, MaxAltitude = height,
            Body = body.Name,
        };
    }

    /// <summary>Just over the air's top, a little way round from where the flown one is: far past any handover's reach.</summary>
    private static float LowRadius => GroundBody.Planet.SeaRadius + GroundBody.Planet.OrbitFloor + 2 * WarpScenes.LowOrbitHeight;

    /// <summary>
    /// A free-play flight flying the upper stage on its low round orbit, <paramref name="stack"/>
    /// beside it in the world (numbered 1), unwitnessed; at the top rate if <paramref name="top"/>,
    /// every vessel under formula. Returns the stack's vessel.
    /// </summary>
    private static (Simulation Sim, Vessel Stack) Staged(VesselState stack, bool top = true)
    {
        var (sim, _) = WarpScenes.Flying(InOrbit.Round(GroundBody.Planet, 0, WarpScenes.LowOrbitHeight), stack);
        if (top)
        {
            FormulaRateTests.ToTheTop(sim.Flight);
            sim.Step(default);
        }
        var vessel = sim.Flight.WorldVessels.Single(v => v.Id == stack.Id);
        Assert.True(vessel.OnFormula, "the stack should be under formula");
        return (sim, vessel);
    }

    [Fact]
    public void A_two_stage_rocket_under_formula_stages_and_each_part_goes_on_with_its_own_mass_and_fuel()
    {
        var state = Coasting(1, Vehicle.TwoStage, 0, 1, LowRadius, MathF.PI);
        var (sim, stack) = Staged(state);
        using var _ = sim;
        var flight = sim.Flight;
        Assert.Equal(Scene.TopRate, flight.TimeScale);
        var (origin, velocity, up, centre, turning) = (stack.Position, stack.Velocity, stack.Up, stack.CentreOfMass, stack.FormulaSpin);

        Assert.Equal(1, flight.StageVessel(stack));
        var dropped = flight.WorldVessels.Single(v => v.Vehicle == Vehicle.TwoStage && v != stack);
        Assert.Equal((0, 0), (dropped.Stage, dropped.TopStage));
        Assert.Equal((1, 1), (stack.Stage, stack.TopStage));
        Assert.True(stack.OnFormula && dropped.OnFormula);
        Assert.Equal(FlightState.Flying, stack.State);
        Assert.Equal(FlightState.Flying, dropped.State);
        // Parting from each other, as the physics' stages do, should they come back into it together.
        Assert.Same(stack, dropped.PartingWith);
        Assert.Same(dropped, stack.PartingWith);

        // The same stack staged in the physics, flown, for its numbers.
        var (physics, flown) = WarpScenes.Flying(state);
        using var _p = physics;
        physics.Step(new FlightInput(Stage: true));
        Assert.Equal((1, 1), (flown.Stage, flown.TopStage));
        Assert.False(flown.OnFormula);
        output.WriteLine($"the rest: {stack.MaxAcceleration:0.0000} m/s² at full throttle, {flown.MaxAcceleration:0.0000} staged in the physics; {stack.Mass:0} kg, {stack.DeltaV:0.0} m/s");
        Assert.Equal(flown.MaxAcceleration, stack.MaxAcceleration, 1e-4);
        Assert.Equal(flown.Mass, stack.Mass, 1e-2);
        Assert.Equal(flown.DeltaV, stack.DeltaV, 1e-2);
        Assert.Equal(Vehicle.TwoStage.Stages[0].DryMass, dropped.Mass, 1e-2); // its tank empty

        // The dropped stage where the stack's bottom was, the rest a stage's height up the axis from it.
        var step = RocketGeometry.StackStep(Vehicle.TwoStage.Stages[0]);
        output.WriteLine($"dropped {Vector3.Distance(dropped.Position, origin):0.000} m from the stack's origin; the rest {Vector3.Dot(stack.Position - dropped.Position, up):0.000} m up the axis from it");
        Assert.True(Vector3.Distance(dropped.Position, origin) < 0.01f);
        Assert.True(Vector3.Distance(stack.Position, dropped.Position + up * step) < 0.01f);
        // Each with the stack's velocity where it was in it (its share of the stack's turning, as
        // one holding its attitude among the stars turns over the ground), but for the physics'
        // gentle push apart along the axis.
        Vector3 Expected(Vessel part, float push) =>
            velocity + Vector3.Cross(turning, part.CentreOfMass - centre) + up * (Scene.SeparationSpeed * push);
        output.WriteLine($"dropped {Vector3.Distance(dropped.Velocity, Expected(dropped, -0.5f)):0.0000} m/s off, the rest {Vector3.Distance(stack.Velocity, Expected(stack, 0.5f)):0.0000}; turning {turning.Length():0.00000} rad/s");
        Assert.True(Vector3.Distance(dropped.Velocity, Expected(dropped, -0.5f)) < 0.01f);
        Assert.True(Vector3.Distance(stack.Velocity, Expected(stack, 0.5f)) < 0.01f);
        Assert.True(Vector3.Distance((dropped.Velocity + stack.Velocity) / 2, velocity) < 0.1f, "on the whole, the stack's velocity");
        Assert.All(sim.Worlds.All, w => Assert.Empty(w.Crafts));

        // And on: both coasting under formula, apart.
        for (var i = 0; i < 10; i++)
            sim.Step(default);
        Assert.True(stack.OnFormula && dropped.OnFormula);
        Assert.Equal(FlightState.Flying, stack.State);
        Assert.Equal(FlightState.Flying, dropped.State);
        Assert.All(sim.Worlds.All, w => Assert.Empty(w.Crafts));
    }

    [Fact]
    public void A_burn_after_staging_under_formula_gives_the_delta_v_the_numbers_say()
    {
        // Two flights from the same place, far out where the pull is slight, each staging its
        // stack under formula at once: one's upper stage at full throttle, burning its fuel out,
        // the other's coasting. What is between them just after the burn is the burn's alone,
        // but for the pull's difference over the few tens of kilometres the burn has moved it
        // (a hundredth of a percent of it so far out). Compared any later, that difference grows
        // with the distance between them: two top-rate frames on (546 s, hundreds of kilometres
        // apart) it is some 0.4%.
        const float Radius = 300_000f;
        var (burning, burner) = Staged(Coasting(1, Vehicle.TwoStage, 0, 1, Radius, MathF.PI, throttle: 1f));
        using var _b = burning;
        var (coasting, coaster) = Staged(Coasting(1, Vehicle.TwoStage, 0, 1, Radius, MathF.PI));
        using var _c = coasting;
        burning.Flight.StageVessel(burner);
        coasting.Flight.StageVessel(coaster);
        var expected = burner.DeltaV;
        var upper = Vehicle.TwoStage.Stages[1];
        var byHand = upper.ExhaustVelocity * Math.Log((upper.DryMass + upper.FuelMass) / upper.DryMass);
        Assert.Equal(byHand, expected, 0.01 * byHand / 100);

        // A window a frame, until the tank is dry.
        burning.RealFrame = coasting.RealFrame = Scene.FormulaWindowSeconds / Scene.TopRate;
        var start = burning.Flight.WorldTime;
        while (burner.Fuel > 0f && burning.Flight.WorldTime - start < 120.0)
        {
            burning.Step(default);
            coasting.Step(default);
        }
        output.WriteLine($"dry {burning.Flight.WorldTime - start:0.0} s after staging, {Vector3.Distance(burner.CentreOfMass, coaster.CentreOfMass) / 1000f:0.0} km from the coaster");
        Assert.Equal(burning.Flight.WorldTime, coasting.Flight.WorldTime, 6);
        Assert.Equal(0f, burner.Fuel);
        Assert.True(burner.OnFormula && coaster.OnFormula);
        Assert.Same(GroundBody.Planet, burner.Body);
        Assert.All(burning.Worlds.All, w => Assert.Empty(w.Crafts));
        var body = burner.Body;
        var gained = (body.AmongStars(burner.CentreOfMass, burner.Velocity) - body.AmongStars(coaster.CentreOfMass, coaster.Velocity)).Length();
        output.WriteLine($"the burn gave {gained:0.00} m/s; the numbers say {expected:0.00} ({(gained / expected - 1) * 100:0.00}%)");
        Assert.InRange(gained, expected * 0.999f, expected * 1.001f);
    }

    [Fact]
    public void A_fairing_under_formula_comes_off_in_two_halves_each_pushed_out_on_its_side()
    {
        // The satellite launcher's upper stage with the satellite in its fairing.
        var (sim, payload) = Staged(Coasting(1, Vehicle.ThreeStage, 1, 2, LowRadius, MathF.PI));
        using var _ = sim;
        var flight = sim.Flight;
        Assert.True(payload.FairingOn);
        var (velocity, up, mass) = (payload.Velocity, payload.Up, payload.Mass);

        Assert.Equal(2, flight.StageVessel(payload));
        Assert.False(payload.FairingOn);
        Assert.Equal((1, 2), (payload.Stage, payload.TopStage)); // the satellite still on it: only the fairing came off
        Assert.Equal(mass - Vehicle.ThreeStage.Stages[2].FairingGeometry.Mass, payload.Mass, 1e-2);
        var halves = flight.WorldVessels.Where(v => v.IsFairing).ToArray();
        Assert.Equal(2, halves.Length);
        var pushes = new Vector3[2];
        for (var i = 0; i < 2; i++)
        {
            var half = halves[i];
            Assert.True(half.OnFormula);
            Assert.Equal(FlightState.Flying, half.State);
            Assert.Same(payload, half.PartingWith);
            pushes[i] = half.Velocity - payload.Velocity;
            output.WriteLine($"half {i}: pushed {pushes[i].Length():0.000} m/s, {Vector3.Dot(pushes[i], up):0.000} along the axis");
            Assert.InRange(pushes[i].Length(), Scene.FairingSeparationSpeed - 0.1f, Scene.FairingSeparationSpeed + 0.1f);
            Assert.True(MathF.Abs(Vector3.Dot(pushes[i], up)) < 0.1f, "pushed out sideways, not along the axis");
        }
        Assert.True(Vector3.Dot(Vector3.Normalize(pushes[0]), Vector3.Normalize(pushes[1])) < -0.99f, "the two pushed out on opposite sides");
        Assert.True(Vector3.Distance(payload.Velocity, velocity) < 1e-3f, "the payload goes on as it was");
        Assert.All(sim.Worlds.All, w => Assert.Empty(w.Crafts));
    }

    [Theory]
    [InlineData("on level ground")]
    [InlineData("aboard the drone ship")]
    public void Staged_at_rest_under_formula_the_rest_stands_on_the_dropped_stage_and_both_stay_still(string where)
    {
        // The two-stage rocket standing on its legs, nobody watching, at the top rate. Staged, as
        // the physics stages one at rest: the upper stage set on the booster, its bell lifted out
        // of the interstage, no parting; and as the formula keeps a vessel at rest, both stay
        // landed where they are (aboard the deck, both carried by it).
        var aboard = where == "aboard the drone ship";
        Vector3 at;
        if (aboard)
        {
            Rafts.Ship.Set(0.0);
            at = Rafts.Ship.Current.Position + Rafts.Ship.Normal * Scene.DroneShipFreeboard;
        }
        else
            at = FormulaTests.Level.Value.At;
        var state = Drops.Placed(at, Planet.East(at), 0f, Vehicle.TwoStage, first: 0, last: 1, mission: Mission.ReachOrbit, flight: 1,
            clearance: -1.5f * Scene.LegStroke, id: 1) with { State = FlightState.Landed, Velocity = Vector3.Zero, TouchedDown = true };
        var (sim, stack) = Staged(state);
        using var _ = sim;
        var flight = sim.Flight;
        Assert.Equal(FlightState.Landed, stack.State);
        if (aboard)
            Assert.Same(Rafts.Ship, stack.AboardUnderFormula?.Raft);
        var (origin, up, velocity) = (stack.Position, stack.Up, stack.Velocity);

        Assert.Equal(1, flight.StageVessel(stack));
        var dropped = flight.WorldVessels.Single(v => v.Vehicle == Vehicle.TwoStage && v != stack);
        Assert.Equal((0, 0), (dropped.Stage, dropped.TopStage));
        Assert.Equal((1, 1), (stack.Stage, stack.TopStage));
        Assert.Equal(FlightState.Landed, dropped.State);
        Assert.Equal(FlightState.Landed, stack.State);
        Assert.True(stack.OnFormula && dropped.OnFormula);
        Assert.Null(stack.PartingWith);
        Assert.Null(dropped.PartingWith);
        var lift = RocketGeometry.StackStep(Vehicle.TwoStage.Stages[0]) + Vehicle.TwoStage.Stages[1].BellHeight + 0.05f;
        output.WriteLine($"dropped {Vector3.Distance(dropped.Position, origin):0.000} m from the stack's origin; the rest {Vector3.Dot(stack.Position - dropped.Position, up):0.000} m up the axis ({lift:0.000} asked)");
        Assert.True(Vector3.Distance(dropped.Position, origin) < 0.01f);
        Assert.True(Vector3.Distance(stack.Position, dropped.Position + up * lift) < 0.01f);
        Assert.True(Vector3.Distance(dropped.Velocity, velocity) < 0.01f && Vector3.Distance(stack.Velocity, velocity) < 0.01f, "as still as the stack was");
        if (aboard)
        {
            // The rest, a stage's height up, is over the deck too.
            Assert.Same(Rafts.Ship, dropped.AboardUnderFormula?.Raft);
            Assert.Same(Rafts.Ship, stack.AboardUnderFormula?.Raft);
        }
        var (droppedAt, restAt) = (dropped.Position, stack.Position);

        for (var i = 0; i < 10; i++)
        {
            sim.Step(default);
            Assert.All(sim.Worlds.All, w => Assert.Empty(w.Crafts));
        }
        Assert.Equal(Scene.TopRate, flight.TimeScale);
        Assert.Equal(FlightState.Landed, dropped.State);
        Assert.Equal(FlightState.Landed, stack.State);
        if (aboard)
        {
            Rafts.Ship.Set(flight.WorldTime);
            var (low, high) = (Rafts.Ship.HeightAbove(dropped.Position), Rafts.Ship.HeightAbove(stack.Position));
            output.WriteLine($"{flight.WorldTime:0} s: the booster {low:0.00} m over the deck, the rest {high:0.00}");
            Assert.InRange(low ?? float.NaN, -0.5f, 3f);
            Assert.InRange((high - low) ?? float.NaN, lift - 0.5f, lift + 0.5f); // still standing on it
        }
        else
        {
            Assert.True(Vector3.Distance(dropped.Position, droppedAt) < 0.01f);
            Assert.True(Vector3.Distance(stack.Position, restAt) < 0.01f);
        }
    }

    [Fact]
    public void Staging_under_formula_never_brings_the_physics_in_nor_changes_the_warp()
    {
        // At the top rate, and at the physics' top rate with the stacks far from anything watched.
        foreach (var top in (ReadOnlySpan<bool>)[true, false])
        {
            var (sim, stack) = Staged(Coasting(1, Vehicle.TwoStage, 0, 1, LowRadius, MathF.PI), top);
            using var _ = sim;
            var flight = sim.Flight;
            if (!top)
            {
                for (var i = 0; i < Flight.Rates.Length && flight.PlayerRate < Scene.PhysicsTopRate; i++)
                    flight.RunFrame(0f, new FlightInput(Faster: true));
                Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
            }
            var rate = flight.PlayerRate;
            var worlds = sim.Worlds.All.Count;
            var crafts = sim.Worlds.All.Sum(w => w.Crafts.Count);
            Assert.Equal(1, flight.StageVessel(stack));
            Assert.Equal(1, stack.Stage);
            for (var i = 0; i < 60; i++)
            {
                sim.Step(default);
                Assert.Equal(rate, flight.PlayerRate);
                Assert.Equal(rate, flight.TimeScale);
                Assert.Null(flight.LastDrop);
                Assert.Equal(worlds, sim.Worlds.All.Count);
                Assert.Equal(crafts, sim.Worlds.All.Sum(w => w.Crafts.Count));
                Assert.All(flight.WorldVessels.Where(v => v.Id != 0), v => Assert.True(v.OnFormula, $"{v.Name} came into the physics"));
            }
            output.WriteLine($"x{rate}: {flight.WorldVessels.Count} vessels, {crafts} crafts in the physics throughout");
        }
    }
}
