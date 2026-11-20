using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Burns, the holds and the rings at a formula's rate (<see cref="Scene.TopRate"/>): a burn held
/// prograde, or along a node by the manoeuvre hold, flown by the formula as it is in the physics at
/// normal speed; a ring's click or drag turning the vessel and keeping the warp; the warp dropped
/// ahead of a burn's fuel running out (<see cref="WarpDrop.FuelOut"/>) or of a burn taking it down
/// into the air; and a burning vessel above the air holding nothing.
/// </summary>
public sealed class WarpBurnTests(ITestOutputHelper output)
{
    private static GroundBody Planet => GroundBody.Planet;

    /// <summary>
    /// <paramref name="state"/> burning at <paramref name="throttle"/>, its nose along its prograde
    /// among the stars (against it for <see cref="HoldMode.Retrograde"/>), holding
    /// <paramref name="hold"/>; with <paramref name="fuel"/> kg in its tank, if given.
    /// </summary>
    private static VesselState Burning(VesselState state, float throttle, HoldMode hold, float? fuel = null)
    {
        var com = state.CentreOfMass.ToSingle();
        var way = Vector3.Normalize(Planet.AmongStars(com, state.Velocity.ToSingle())) * (hold == HoldMode.Retrograde ? -1f : 1f);
        var rotation = UnwatchedTests.RotationTo(way);
        var tanks = (float[])state.Fuel.Clone();
        if (fuel is { } kg)
            tanks[state.First] = kg;
        return state with
        {
            Rotation = rotation, Aim = way, Position = com - Vector3.Transform(state.CentreOfMassOffset, rotation),
            Throttle = throttle, Hold = hold, Fuel = tanks,
        };
    }

    /// <summary>The height of the apoapsis over the sea of the orbit <paramref name="v"/> is on now, its engine off.</summary>
    private static double Apoapsis(Vessel v) =>
        new Orbit(v.Body, v.CentreOfMass, v.Body.AmongStars(v.CentreOfMass, v.Velocity), findEnd: false).ApoapsisDistance - v.Body.SeaRadius;

    /// <summary>The fuel in <paramref name="v"/>'s tank, kg.</summary>
    private static float Tank(Vessel v) => v.Fuel * v.CurrentStage.FuelMass;

    /// <summary>
    /// Flies the scene <paramref name="seconds"/> of game time on from now, at the fastest rate in
    /// <paramref name="frames"/> frames (the rate pressed to in frames of no time), or at normal
    /// speed in the physics, then cuts the throttle in a frame of no time; returns the time it got to.
    /// </summary>
    private static double Burn(Simulation sim, double seconds, bool top, int frames)
    {
        var flight = sim.Flight;
        var end = flight.WorldTime + seconds;
        if (top)
        {
            FormulaRateTests.ToTheTop(flight);
            sim.RealFrame = (float)(seconds / (frames * Scene.TopRate));
            for (var i = 0; i < frames; i++)
                sim.Step(default);
            Assert.Equal(Scene.TopRate, flight.PlayerRate);
            Assert.Null(flight.LastDrop);
        }
        else
        {
            sim.RealFrame = Simulation.FrameSeconds;
            while (end - flight.WorldTime > Simulation.FrameSeconds / 2)
                sim.Step(default);
            Assert.False(flight.Controlled.OnFormula);
        }
        flight.RunFrame(0f, new FlightInput(CutThrottle: true));
        return flight.WorldTime;
    }

    [Fact]
    public void A_prograde_burn_at_the_top_rate_matches_the_same_burn_at_normal_speed()
    {
        // A gentle burn (some 50 m/s), so the orbit it ends on is still closed.
        (double Apoapsis, float Used, double Time) Burnt(bool top)
        {
            var (sim, flown) = WarpScenes.Flying(Burning(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), 0.015f, HoldMode.Prograde));
            using var _ = sim;
            var fuel = Tank(flown);
            var time = Burn(sim, 120.0, top, frames: 4);
            Assert.Equal(top, flown.OnFormula);
            Assert.False(flown.EngineLit);
            return (Apoapsis(flown), fuel - Tank(flown), time);
        }

        var (physics, formula) = (Burnt(top: false), Burnt(top: true));
        output.WriteLine($"apoapsis {physics.Apoapsis:0.0} m at 1x, {formula.Apoapsis:0.0} m at the top rate; fuel used {physics.Used:0.000} kg, {formula.Used:0.000} kg; at {physics.Time:0.000} s, {formula.Time:0.000} s");
        Assert.True(physics.Apoapsis > WarpScenes.LowOrbitHeight + Scene.AtmosphereHeight + 1000.0, "the burn should have raised the apoapsis");
        Assert.InRange(formula.Apoapsis, physics.Apoapsis * 0.995, physics.Apoapsis * 1.005);
        Assert.InRange(formula.Used, physics.Used * 0.995f, physics.Used * 1.005f);
        Assert.Equal(physics.Time, formula.Time, 3);
    }

    [Fact]
    public void A_burn_along_a_node_at_the_top_rate_matches_the_same_burn_at_normal_speed()
    {
        // A node of 20 m/s prograde, where it is now (so none comes up to drop the warp for), flown
        // by the manoeuvre hold at a twentieth of the throttle (some 15 s).
        (double Apoapsis, float Used, float Left, double Time) Burnt(bool top)
        {
            var (sim, flown) = WarpScenes.Flying(Burning(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), 0.05f, HoldMode.None));
            using var _ = sim;
            var flight = sim.Flight;
            flight.RunFrame(0f, new FlightInput(Plan: ManoeuvreChain.AnchoredOn(flown, flight.WorldTime, [new ManoeuvreNode(0.0, Prograde: 20f)], flight.WorldTime)));
            flight.RunFrame(0f, new FlightInput(ChooseHold: HoldMode.Manoeuvre));
            Assert.Equal(HoldMode.Manoeuvre, flown.Hold);
            var fuel = Tank(flown);
            var time = Burn(sim, 15.0, top, frames: 3);
            Assert.Equal(top, flown.OnFormula);
            var left = flight.Guide?.Left.Length() ?? 0f;
            return (Apoapsis(flown), fuel - Tank(flown), left, time);
        }

        var (physics, formula) = (Burnt(top: false), Burnt(top: true));
        output.WriteLine($"apoapsis {physics.Apoapsis:0.0} m at 1x, {formula.Apoapsis:0.0} m at the top rate; fuel used {physics.Used:0.000} kg, {formula.Used:0.000} kg; "
            + $"left {physics.Left:0.000} m/s, {formula.Left:0.000} m/s; at {physics.Time:0.000} s, {formula.Time:0.000} s");
        Assert.True(physics.Left < 2f, $"the burn should have flown nearly all the node ({physics.Left:0.00} m/s left)");
        Assert.InRange(formula.Apoapsis, physics.Apoapsis * 0.995, physics.Apoapsis * 1.005);
        Assert.InRange(formula.Used, physics.Used * 0.995f, physics.Used * 1.005f);
        Assert.InRange(formula.Left, 0f, physics.Left + 0.5f);
        Assert.Equal(physics.Time, formula.Time, 3);
    }

    [Fact]
    public void A_ring_click_at_the_top_rate_turns_the_vessel_to_it_and_keeps_the_warp()
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        sim.Step(default);
        Assert.True(flown.OnFormula);
        // Along the nose's heading, a right angle below its pitch: 90° off the nose.
        var (heading, pitch) = RingAngles.Of(flown.Body, flown.CentreOfMass, flown.Aim, 0f);
        pitch -= MathF.PI / 2f;
        Assert.InRange(Vector3.Dot(flown.Aim, RingAngles.Direction(flown.Body, flown.CentreOfMass, heading, pitch)), -0.01f, 0.01f);

        sim.Step(new FlightInput(RingHeading: heading, RingPitch: pitch));
        var ring = RingAngles.Direction(flown.Body, flown.CentreOfMass, flown.RingAim.Heading, flown.RingAim.Pitch);
        output.WriteLine($"{flight.PlayerRate}x, {Vector3.Dot(flown.Aim, ring):0.000000} along the ring's way, holding {flown.Hold}");
        Assert.Equal(HoldMode.Rings, flown.Hold);
        Assert.Equal((heading, pitch), flown.RingAim);
        Assert.True(Vector3.Dot(flown.Aim, ring) > 0.9999f);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Null(flight.LastDrop);
        Assert.True(flown.OnFormula);
    }

    [Fact]
    public void A_ring_drag_at_the_top_rate_keeps_the_warp_too()
    {
        var (sim, flown) = WarpScenes.LowOrbit();
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        sim.Step(default);
        var way = flown.Right; // across the nose
        for (var i = 0; i < 2; i++)
            sim.Step(new FlightInput(Hold: way));
        output.WriteLine($"{flight.PlayerRate}x, {Vector3.Dot(flown.Aim, way):0.000000} along the drag's way");
        Assert.True(Vector3.Dot(flown.Aim, way) > 0.9999f);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Null(flight.LastDrop);
        Assert.True(flown.OnFormula);
    }

    [Fact]
    public void A_burn_s_fuel_running_out_drops_the_warp_ahead_of_it()
    {
        // 40 kg left at a fiftieth of the throttle: some 14 s of burning, 60 s a frame.
        var (sim, flown) = WarpScenes.Flying(Burning(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), 0.02f, HoldMode.Prograde, fuel: 40f));
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        sim.RealFrame = (float)(60.0 / Scene.TopRate);
        Assert.True(flown.FuelSeconds > Scene.WarpDropLeadSeconds + 5.0);
        sim.Step(default);
        output.WriteLine($"{flight.PlayerRate}x, dropped for {flight.LastDrop?.Reason}, {flown.FuelSeconds:0.000} s of fuel left");
        Assert.Equal(new WarpDropped(WarpDrop.FuelOut, WorldLog.Named(flown), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.False(flown.OnFormula);
        Assert.True(flown.EngineLit);
        Assert.InRange(flown.FuelSeconds, Scene.WarpDropLeadSeconds - Scene.PhysicsTopRate / 60.0, Scene.WarpDropLeadSeconds + Scene.PhysicsTopRate / 60.0);
    }

    [Fact]
    public void A_burn_taking_it_down_into_the_air_drops_the_warp_ahead_of_the_air()
    {
        // Held retrograde at a tenth of the throttle, from a kilometre over the air: 10 s a frame.
        var (sim, flown) = WarpScenes.Flying(Burning(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight), 0.1f, HoldMode.Retrograde));
        using var _ = sim;
        var flight = sim.Flight;
        FormulaRateTests.ToTheTop(flight);
        sim.RealFrame = (float)(10.0 / Scene.TopRate);
        var frames = 0;
        while (flight.LastDrop is null && frames++ < 100)
            sim.Step(default);
        var dropped = flight.WorldTime;
        Assert.Equal(new WarpDropped(WarpDrop.Air, WorldLog.Named(flown), Scene.PhysicsTopRate, flight.RealTime), flight.LastDrop);
        Assert.Equal(Scene.PhysicsTopRate, flight.PlayerRate);
        Assert.False(flown.OnFormula);
        Assert.True(flown.EngineLit);

        // Burning on in the physics, at its rate: into the air the lead on, about.
        var floor = Formula.FloorRadius(flown);
        Assert.True((flown.CentreOfMass - flown.Body.Centre).Length() > floor);
        sim.RealFrame = Simulation.FrameSeconds;
        while ((flown.CentreOfMass - flown.Body.Centre).Length() > floor && flight.WorldTime - dropped < 30.0)
            sim.Step(default);
        output.WriteLine($"dropped {frames} frames on, {flight.WorldTime - dropped:0.000} s before it came down to its floor");
        Assert.InRange(flight.WorldTime - dropped, Scene.WarpDropLeadSeconds - 0.5, Scene.WarpDropLeadSeconds + 0.5);
    }

    [Fact]
    public void A_burning_vessel_in_a_small_view_lets_the_top_rate_in_above_the_air()
    {
        var (sim, flown) = WarpScenes.Flying(InOrbit.Round(Planet, 0, WarpScenes.LowOrbitHeight),
            Burning(InOrbit.Round(Planet, 1, WarpScenes.LowOrbitHeight + 1000f), 0.015f, HoldMode.Prograde));
        using var _ = sim;
        var flight = sim.Flight;
        var burner = flight.WorldVessels.Single(v => v.Id == 1);
        flight.OpenView(burner);
        FormulaRateTests.ToTheTop(flight, watched: [burner]);
        var fuel = Tank(burner);
        sim.Step(new FlightInput(Watched: [burner]));
        output.WriteLine($"{flight.PlayerRate}x, held by {(flight.WarpHeldBy is { } h ? WorldLog.Named(h) : "none")}, {fuel - Tank(burner):0.00} kg burnt");
        Assert.Null(flight.WarpHeldBy);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Null(flight.LastDrop);
        Assert.True(burner.OnFormula && flown.OnFormula);
        Assert.True(burner.EngineLit);
        Assert.True(Tank(burner) < fuel, "it should have burnt fuel");
    }
}
