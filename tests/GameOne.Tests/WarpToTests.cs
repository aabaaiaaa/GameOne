using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A right click on the flown vessel's path warps to that point (<see cref="FlightInput.WarpTo"/>,
/// <see cref="Flight.WarpingTo"/>): the fastest rate at once, and normal speed within a step of
/// the time asked, the vessel where its path said it would be; ended early, at normal speed, by a
/// rate key, a switch, a collision warning or the vessel wrecked; no later than the path goes; and
/// the HUD's hit on the drawn line, which an object under the pointer wins over.
/// </summary>
public sealed class WarpToTests(ITestOutputHelper output)
{
    private static readonly float Fastest = Flight.Rates[^1];

    /// <summary>A free-play flight flying <paramref name="states"/>' first (its others in the world beside it).</summary>
    private static (Simulation Sim, Vessel Flown) Flying(params VesselState[] states)
    {
        var sim = new Simulation(Mission.FreePlay, world: new WorldState { Time = 0.0, Vessels = [.. states], NextVesselId = states.Length, Flights = 1 });
        var flown = sim.Flight.WorldVessels.Single(v => v.Id == states[0].Id);
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Step(default);
        Assert.Same(flown, sim.Flight.Controlled);
        return (sim, flown);
    }

    private static List<PathLeg> PathOf(Vessel vessel, double now)
    {
        var legs = new List<PathLeg>(Scene.PatchedLegs);
        Patched.Legs(vessel.Body, vessel.CentreOfMass, vessel.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity), now, legs);
        return legs;
    }

    [Fact]
    public void It_warps_at_the_fastest_rate_and_stops_within_a_step_of_the_time_where_the_path_said()
    {
        var (sim, flown) = Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f));
        using var _ = sim;
        var flight = sim.Flight;
        var now = flight.WorldTime;
        var to = now + 40.0;
        var legs = PathOf(flown, now);
        var expected = SolarEdgeTests.GroundAt(legs[0], to).ToSingle();

        sim.Step(new FlightInput(WarpTo: to));
        Assert.Equal(Fastest, flight.PlayerRate);
        Assert.Equal(to, flight.WarpingTo);
        var frames = 1;
        while (flight.WarpingTo is not null && frames < 1000)
        {
            Assert.Equal(Fastest, flight.TimeScale);
            Assert.True(flight.WorldTime < to, "still short of it while it runs");
            sim.Step(default);
            frames++;
        }
        output.WriteLine($"{frames} frames; stopped at {flight.WorldTime - to:+0.000000;-0.000000} s from the time asked; {Vector3.Distance(flown.CentreOfMass, expected):0.00} m from where the path had it");
        Assert.Null(flight.WarpingTo);
        Assert.Equal(1f, flight.PlayerRate);
        Assert.InRange(flight.WorldTime, to - Flight.WarpStepSeconds, to + 1e-4);
        Assert.InRange(frames, (int)(40.0 / (Fastest * Simulation.FrameSeconds)), (int)(40.0 / (Fastest * Simulation.FrameSeconds)) + 2);
        Assert.True(Vector3.Distance(flown.CentreOfMass, expected) < 5f, "where its path said it would be then");

        // At normal speed from there on.
        var before = flight.WorldTime;
        sim.Step(default);
        Assert.Equal(Simulation.FrameSeconds, flight.WorldTime - before, 5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_rate_key_ends_it_at_normal_speed(bool slower)
    {
        var (sim, _) = Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f));
        using var _s = sim;
        var flight = sim.Flight;
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 300.0));
        sim.Step(default);
        Assert.NotNull(flight.WarpingTo);
        sim.Step(new FlightInput(Slower: slower, Faster: !slower));
        Assert.Null(flight.WarpingTo);
        Assert.Equal(1f, flight.PlayerRate);
    }

    [Fact]
    public void A_switch_to_another_vessel_ends_it_at_normal_speed()
    {
        var other = InOrbit.Round(GroundBody.Planet, 1, 2000f);
        other = other with { Position = other.Position + new Vector3(0f, 0f, 3000f) };
        var (sim, _) = Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f), other);
        using var _s = sim;
        var flight = sim.Flight;
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 300.0));
        sim.Step(default);
        Assert.NotNull(flight.WarpingTo);
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.Single(v => v.Id == 1)));
        Assert.Equal(1, flight.Controlled.Id);
        Assert.Null(flight.WarpingTo);
        Assert.Equal(1f, flight.PlayerRate);
    }

    [Fact]
    public void Past_the_ground_it_stops_where_the_path_comes_down_and_a_warning_ends_it_first()
    {
        // Falling from 6 km at a third of the speed that would hold it there: down within the minute.
        var (sim, flown) = Flying(InOrbit.UpperStage(0, 6000f, 0.3f));
        using var _ = sim;
        var flight = sim.Flight;
        var now = flight.WorldTime;
        var down = Patched.Ends(PathOf(flown, now));
        Assert.True(double.IsFinite(down) && down > now + 15.0, $"comes down {down - now:0.0} s on");

        sim.Step(new FlightInput(WarpTo: now + 1e6));
        Assert.Equal(down, flight.WarpingTo!.Value, 0); // no later than the path goes
        Assert.Equal(Fastest, flight.PlayerRate);

        // The ground ahead is warned of, which drops the warp, and so ends it, before it is reached.
        Assert.True(sim.Run((_, _) => default, f => f.WarpingTo is null, maxSeconds: 300));
        output.WriteLine($"ended {down - flight.WorldTime:0.0} s short, warned of {flight.Warning?.With}");
        Assert.NotNull(flight.Warning);
        Assert.Equal(1f, flight.PlayerRate);
        Assert.Equal(FlightState.Flying, flown.State);

        // Asked again (that warning drops the warp only once), it runs on to where the path comes
        // down (the air has held it back: it is still a little up).
        sim.Step(new FlightInput(WarpTo: down));
        Assert.Equal(Fastest, flight.PlayerRate);
        Assert.True(sim.Run((_, _) => default, f => f.WarpingTo is null, maxSeconds: 300));
        output.WriteLine($"{flown.State} {flight.WorldTime - down:+0.00;-0.00} s from the path's end");
        Assert.Equal(1f, flight.PlayerRate);
        Assert.True(flown.State == FlightState.Crashed || Math.Abs(flight.WorldTime - down) < Flight.WarpStepSeconds);
    }

    [Fact]
    public void The_vessel_wrecked_ends_it_at_normal_speed()
    {
        // Nose down 500 m over the ground by Home, its engine driving it down: into the ground
        // before its path (with the engine off) says, and slower than a fast collision, so unwarned.
        var state = InOrbit.UpperStage(0, Scene.HomeElevation + 500f, 1f);
        var centre = state.CentreOfMass.ToSingle();
        var up = Planet.Up(centre);
        state = InOrbit.At(state, centre, Planet.AmongStars(centre, Vector3.Zero),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI) * Planet.UprightAt(centre)) with { Aim = -up };
        var (sim, flown) = Flying(state);
        using var _ = sim;
        var flight = sim.Flight;
        sim.Step(new FlightInput(FullThrottle: true));
        var down = Patched.Ends(PathOf(flown, flight.WorldTime));
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 1e6));
        Assert.Equal(Fastest, flight.PlayerRate);
        Assert.True(sim.Run((_, _) => default, f => f.WarpingTo is null, maxSeconds: 60));
        output.WriteLine($"{flown.State} ({flown.CrashReason}) {down - flight.WorldTime:0.00} s before the path's end; warned of {flight.Warning?.With.ToString() ?? "nothing"}");
        Assert.Equal(FlightState.Crashed, flown.State);
        Assert.True(flight.WorldTime < down - Flight.WarpStepSeconds, "ended by the wreck, not by getting there");
        Assert.Equal(1f, flight.PlayerRate);
    }

    [Fact]
    public void Past_the_solar_system_s_edge_it_stops_there()
    {
        // Leaving at 3 km/s from 20 km up: its path ends at the edge, days on.
        var state = InOrbit.UpperStage(0, 20000f, 1f);
        var centre = state.CentreOfMass.ToSingle();
        state = InOrbit.At(state, centre, Planet.East(centre) * 3000f + Planet.Up(centre) * 1000f);
        var (sim, flown) = Flying(state);
        using var _ = sim;
        var flight = sim.Flight;
        var legs = PathOf(flown, flight.WorldTime);
        Assert.True(legs[^1].LeavesSystem);
        sim.Step(new FlightInput(WarpTo: double.MaxValue));
        Assert.Equal(legs[^1].End, flight.WarpingTo!.Value, 0);
    }

    [Fact]
    public void A_time_behind_or_a_vessel_not_flying_warps_nowhere()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var flight = sim.Flight;
        sim.Step(default);
        sim.Step(new FlightInput(WarpTo: flight.WorldTime + 60.0)); // the new rocket, on its pad
        Assert.Null(flight.WarpingTo);
        Assert.Equal(1f, flight.PlayerRate);

        var (inOrbit, _) = Flying(InOrbit.Round(GroundBody.Planet, 0, 2000f));
        using var _s = inOrbit;
        inOrbit.Step(new FlightInput(WarpTo: inOrbit.Flight.WorldTime - 1.0));
        Assert.Null(inOrbit.Flight.WarpingTo);
        Assert.Equal(1f, inOrbit.Flight.PlayerRate);
    }

    [Fact]
    public void The_hit_is_the_nearest_point_on_the_drawn_line_within_the_figure_and_nothing_beyond()
    {
        Vector2[] points = [new(0, 0), new(100, 0), new(100, 100)];
        bool[] all = [true, true, true];
        var within = Scene.PathPixels;

        Assert.Equal(1, NodeEditor.NearestPiece(points, all, new Vector2(30, 5), within, out var t, out var distance));
        Assert.Equal(0.3f, t, 4);
        Assert.Equal(5f, distance, 4);
        // Near both pieces: the nearer.
        Assert.Equal(2, NodeEditor.NearestPiece(points, all, new Vector2(97, 20), within, out t, out distance));
        Assert.Equal(0.2f, t, 4);
        Assert.Equal(3f, distance, 4);
        // Just beyond the figure, nothing.
        Assert.Equal(-1, NodeEditor.NearestPiece(points, all, new Vector2(30, within + 0.5f), within, out _, out _));
        // Past the line's end, measured to the end itself.
        Assert.Equal(-1, NodeEditor.NearestPiece(points, all, new Vector2(-within - 0.5f, 0), within, out _, out _));
        Assert.Equal(1, NodeEditor.NearestPiece(points, all, new Vector2(-within + 0.5f, 0), within, out t, out _));
        Assert.Equal(0f, t);
        // A point not drawn (behind a body, behind the camera) has no piece to or from it.
        Assert.Equal(-1, NodeEditor.NearestPiece(points, [true, false, true], new Vector2(30, 1), within, out _, out _));
        Assert.Equal(-1, NodeEditor.NearestPiece(points, [true, false, true], new Vector2(100, 50), within, out _, out _));
    }

    [Fact]
    public void An_object_under_the_pointer_wins_over_the_path()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var vessel = sim.Flight.Controlled;
        Assert.Equal(123.0, Hud.RightClickWarp(null, null, 123.0));
        Assert.Null(Hud.RightClickWarp(new CameraCentre(CentreKind.Body, 1), null, 123.0)); // a marker: a body's, a vessel's, a site's
        Assert.Null(Hud.RightClickWarp(null, vessel, 123.0)); // a vessel, the flown one's own included
        Assert.Null(Hud.RightClickWarp(null, null, null)); // off the path
    }
}
