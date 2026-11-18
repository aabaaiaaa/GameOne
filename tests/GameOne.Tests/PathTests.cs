using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The predicted path ahead of a vessel (<see cref="Orbit"/>): once round for an orbit, as far as
/// the ground for one that comes down, out to the edge for one that escapes; agreeing with the
/// HUD's apoapsis and periapsis; and where a vessel in orbit really goes. And the camera that zooms
/// out to see it, and back.
/// </summary>
public sealed class PathTests(ITestOutputHelper output)
{
    private static readonly Vector3 Up = Vector3.UnitY, East = Vector3.UnitX;

    /// <summary>A point above the pad, <paramref name="aboveSea"/> above the sea.</summary>
    private static Vector3 Above(float aboveSea) => Planet.Centre + Up * (Planet.SeaRadius + aboveSea);

    private static List<Vector3> PathOf(Vector3 position, Vector3 velocity)
    {
        var points = new List<Vector3>();
        new Orbit(GroundBody.Planet, position, velocity).Path(points);
        return points;
    }

    private static float Distance(Vector3 p) => Vector3.Distance(p, Planet.Centre);

    /// <summary>Whether <paramref name="p"/> is on the terrain (or the calm sea, where there is no land: a path ends on its surface), to a metre.</summary>
    private static bool OnTerrain(Vector3 p) => MathF.Abs(Distance(p) - Planet.SeaRadius - Terrain.SurfaceHeight(Planet.Up(p))) < 1f;

    [Fact]
    public void A_label_on_the_half_way_point_between_two_steps_does_not_flip()
    {
        // An AN at 12.35° (in tenths, 123.5) built as 12.3°: the angle wavering either side of the
        // half-way point keeps it; only once it is well into the next step is it built again.
        Assert.True(Paths.StillShows(123, 123.49));
        Assert.True(Paths.StillShows(123, 123.51));
        Assert.True(Paths.StillShows(123, 122.45));
        Assert.False(Paths.StillShows(123, 123.7));
        Assert.False(Paths.StillShows(123, 122.3));
        Assert.False(Paths.StillShows(double.NaN, 123), "nothing shown yet");
    }

    [Fact]
    public void Chevrons_are_spaced_along_a_line_and_point_its_way()
    {
        var line = new[] { new Vector2(10f, 20f), new Vector2(610f, 20f) };
        var into = new (Vector2 At, Vector2 Way)[16];

        var n = Paths.Chevrons(line, 120f, into);

        Assert.Equal(5, n);
        for (var i = 0; i < n; i++)
        {
            Assert.True(Vector2.Distance(into[i].Way, Vector2.UnitX) < 1e-3f);
            Assert.Equal(10f + 60f + 120f * i, into[i].At.X, 1e-2f);
        }

        // Round a corner each one points along the piece it is on.
        var bent = new[] { new Vector2(0f, 0f), new Vector2(100f, 0f), new Vector2(100f, 300f) };
        n = Paths.Chevrons(bent, 120f, into);
        Assert.Equal(3, n);
        Assert.True(Vector2.Distance(into[0].Way, Vector2.UnitX) < 1e-3f);
        Assert.True(Vector2.Distance(into[1].Way, Vector2.UnitY) < 1e-3f);
    }

    [Fact]
    public void Chevrons_go_by_the_whole_line_count_only_on_the_screen_and_follow_its_order()
    {
        var into = new (Vector2 At, Vector2 Way)[Paths.MaxChevrons];

        // A line shorter than half the spacing has none.
        Assert.Equal(0, Paths.Chevrons([new Vector2(0f, 0f), new Vector2(50f, 0f)], 120f, into));

        // The cap is spent only on the screen: a line far longer than the cap's worth, mostly off
        // the screen to its left, still has its part on the screen filled.
        var display = new Vector2(1280f, 720f);
        var along = 0f;
        var n = Paths.Chevrons([new Vector2(-200_000f, 300f), new Vector2(1200f, 300f)], 120f, into, ref along, display);
        Assert.True(n >= 10, $"only {n} chevrons on the screen");
        for (var i = 0; i < n; i++)
            Assert.InRange(into[i].At.X, 0f, display.X);

        // In two stretches (a body hiding the middle), the second's are where the whole line's are.
        var whole = new (Vector2 At, Vector2 Way)[16];
        var all = Paths.Chevrons([new Vector2(0f, 0f), new Vector2(1000f, 0f)], 120f, whole);
        along = 0f;
        var first = Paths.Chevrons([new Vector2(0f, 0f), new Vector2(300f, 0f)], 120f, into, ref along);
        along += 250f; // the gap, by its ends
        var second = Paths.Chevrons([new Vector2(550f, 0f), new Vector2(1000f, 0f)], 120f, into.AsSpan(first), ref along);
        var expected = whole.Take(all).Where(c => c.At.X <= 300f || c.At.X >= 550f).Select(c => c.At.X).ToArray();
        Assert.Equal(expected, into.Take(first + second).Select(c => c.At.X).ToArray(), new FloatComparer(1e-2f));

        // Round a circle flown anticlockwise on the screen, each points the way it is flown.
        var circle = Enumerable.Range(0, 65).Select(k => new Vector2(640f + 300f * MathF.Cos(k * MathF.Tau / 64), 360f + 300f * MathF.Sin(k * MathF.Tau / 64))).ToArray();
        n = Paths.Chevrons(circle, 120f, into);
        Assert.True(n > 10);
        for (var i = 0; i < n; i++)
        {
            var fromMiddle = into[i].At - new Vector2(640f, 360f);
            Assert.True(Vector2.Dot(into[i].Way, new Vector2(-fromMiddle.Y, fromMiddle.X)) > 0f, $"chevron {i} points against the way");
        }
    }

    private sealed class FloatComparer(float within) : IEqualityComparer<float>
    {
        public bool Equals(float a, float b) => MathF.Abs(a - b) <= within;
        public int GetHashCode(float f) => 0;
    }

    [Fact]
    public void Chevrons_allocate_nothing()
    {
        var line = new[] { new Vector2(0f, 0f), new Vector2(600f, 0f), new Vector2(600f, 600f) };
        var into = new (Vector2 At, Vector2 Way)[16];
        Paths.Chevrons(line, 120f, into);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            Paths.Chevrons(line, 120f, into);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_circular_orbit_goes_once_round_at_its_height_and_back_to_the_start()
    {
        var start = Above(5000f);
        var path = PathOf(start, East * Planet.CircularSpeed(5000f));

        Assert.True(path.Count > 100);
        Assert.All(path, p => Assert.InRange(Distance(p), Planet.SeaRadius + 4990f, Planet.SeaRadius + 5010f));
        Assert.True(Vector3.Distance(path[0], start) < 1f);
        Assert.True(Vector3.Distance(path[^1], start) < 1f, "it should close up, a lap round");
        // The first step is the way it is moving.
        Assert.True(Vector3.Dot(path[1] - path[0], East) > 0f);
    }

    [Fact]
    public void An_elliptical_path_reaches_the_apoapsis_and_periapsis_the_HUD_shows()
    {
        var start = Above(4000f);
        var velocity = East * Planet.CircularSpeed(4000f) * 1.1f + Up * 15f;
        var (apoapsis, periapsis) = Planet.Orbit(start, velocity);
        var path = PathOf(start, velocity);

        var highest = path.Max(Distance) - Planet.SeaRadius;
        var lowest = path.Min(Distance) - Planet.SeaRadius;
        output.WriteLine($"orbit {periapsis:0} to {apoapsis:0} m; path {lowest:0} to {highest:0} m");
        Assert.InRange(highest, apoapsis - 15f, apoapsis + 1f); // between the points the path is sampled at
        Assert.InRange(lowest, periapsis - 1f, periapsis + 15f);
    }

    [Fact]
    public void The_paths_Ap_and_Pe_marks_give_the_heights_the_HUD_shows()
    {
        // Both from the sea: a Pe mark from the sphere read 35 m high, clear of the air when it wasn't.
        var start = Above(2980f);
        var velocity = East * Planet.CircularSpeed(2980f) * 1.05f;
        var (apoapsis, periapsis) = Planet.Orbit(start, velocity);
        var orbit = new Orbit(GroundBody.Planet, start, velocity);
        Assert.Equal(apoapsis, Paths.MarkHeight(orbit, orbit.NextApoapsis!.Value), 1f);
        Assert.Equal(periapsis, Paths.MarkHeight(orbit, orbit.NextPeriapsis!.Value), 1f);
    }

    [Fact]
    public void Something_on_the_sea_in_sight_is_not_behind_the_planet()
    {
        // The drone ship's deck, 2.4 km off, seen from 500 m over the pad: in plain sight, so its
        // marker is drawn (it was hidden by a sphere 15 m above the sea). The far side is hidden.
        var camera = Above(500f);
        Assert.False(Paths.BehindPlanet(camera, LaunchSites.Home.DroneShips[0] + Planet.Up(Scene.DroneShipCentre) * Scene.DroneShipFreeboard));
        Assert.False(Paths.BehindPlanet(camera, Planet.Centre + Planet.Up(Scene.DroneShipCentre) * Planet.SeaRadius));
        Assert.True(Paths.BehindPlanet(camera, Planet.Centre - Up * Planet.SeaRadius));
    }

    [Fact]
    public void A_path_that_comes_down_ends_on_the_ground_ahead()
    {
        // A hop: up and east, well short of orbit.
        // The path is among the stars, so each point is tested against the ground as it will be by
        // then (turned back by the time to it), and it comes down on the ground there (Landing).
        var start = Above(800f);
        var orbit = new Orbit(GroundBody.Planet, start, East * 120f + Up * 80f);
        var path = PathOf(start, East * 120f + Up * 80f);
        var timed = new List<(Vector3 At, double Seconds)>();
        orbit.Samples(timed, 0, double.PositiveInfinity);

        Assert.True(OnTerrain(orbit.Landing), $"it ends {Distance(orbit.Landing) - Planet.SeaRadius:0} m up");
        Assert.True(Vector3.Dot(path[^1] - start, East) > 1000f, "it should come down downrange, to the east");
        Assert.All(timed, p => Assert.True(Distance(p.At) >= Planet.SeaRadius + Terrain.SurfaceHeight(Planet.Up(Planet.Turned(p.At, -p.Seconds))) - 1f, "it goes underground"));
        // Over the top of its arc on the way.
        Assert.True(path.Max(Distance) > Distance(start) + 100f);
    }

    /// <summary>The highest point within 8 km west of the pad, found along the surface every 5 m: its direction and height.</summary>
    private static (Vector3 Direction, float Height) HighestWest()
    {
        var (peakDirection, peak) = (Up, float.MinValue);
        for (var along = 0f; along <= 8000f; along += 5f)
        {
            var angle = along / Planet.Radius;
            var direction = Up * MathF.Cos(angle) - East * MathF.Sin(angle);
            var height = Terrain.SurfaceHeight(direction);
            if (height > peak)
                (peakDirection, peak) = (direction, height);
        }
        return (peakDirection, peak);
    }

    [Fact]
    public void A_straight_fall_over_a_peak_ends_on_it()
    {
        // Let go over the ground, it still moves east among the stars a little faster than the peak
        // under it, so it comes down a little east of it, on whatever is there: where a fine
        // integration of the same fall in the ground's frame comes down (SpinTests.FallToGround).
        var (direction, peak) = HighestWest();
        Assert.True(peak > 200f);
        var start = Planet.Centre + direction * (Planet.SeaRadius + peak + 400f);
        var orbit = Orbit.OverGround(GroundBody.Planet, start, Vector3.Zero);
        var top = Planet.Centre + direction * (Planet.SeaRadius + peak);
        var (integrated, seconds) = SpinTests.FallToGround(start, Vector3.Zero);
        var landing = orbit.Landing;
        output.WriteLine($"peak {peak:0} m; lands {Vector3.Distance(landing, top):0.00} m from it, {Vector3.Distance(landing, integrated):0.00} m from " +
            $"where the integrated fall does ({Vector3.Distance(integrated, top):0.00} m from it, after {seconds:0.0} s)");

        Assert.True(orbit.EndsOnGround);
        Assert.InRange(Planet.Altitude(landing) - Terrain.SurfaceHeight(Planet.Up(landing)), -1f, 1f);
        // Measured 0.03 m (17 m from the peak). Against the even-1 g theory's drift this had 1.8 m
        // to spare, from the theory's own error, not the path's; this leaves 6 m.
        Assert.True(Vector3.Distance(landing, integrated) < 6f, $"{Vector3.Distance(landing, integrated):0.00} m from the integrated landing");
    }

    [Fact]
    public void A_straight_fall_asked_only_its_shape_does_not_look_for_the_ground()
    {
        // findEnd false (the window searches' orbits, a thousand a plan) skips the terrain: let go
        // at rest among the stars over the mountains, it comes down to the sea, not the ground the
        // full path finds there (the ground having turned under it meanwhile).
        var (direction, peak) = HighestWest();
        var start = Planet.Centre + direction * (Planet.SeaRadius + peak + 400f);
        var shape = new Orbit(GroundBody.Planet, start, Vector3.Zero, findEnd: false);
        var full = new Orbit(GroundBody.Planet, start, Vector3.Zero);
        var ground = Terrain.SurfaceHeight(Planet.Up(full.Landing));
        output.WriteLine($"the full path comes down {Planet.Altitude(full.Landing):0.0} m up, on ground {ground:0.0} m up; the shape alone {Planet.Altitude(shape.Landing):0.00} m up");
        Assert.True(shape.Radial && full.Radial);
        Assert.InRange(Planet.Altitude(shape.Landing), -0.5f, 0.5f);
        Assert.True(OnTerrain(full.Landing));
        Assert.True(ground > 50f, $"the ground under the full path is only {ground:0} m up: it doesn't tell the two apart");
    }

    [Fact]
    public void The_test_pilots_fall_prediction_follows_the_path_round_the_planet()
    {
        // Pilots.WhereItFalls steps the fall in the ground's frame as the physics does, and long
        // predictions are where a plain step drifts. Over the air (no drag), a third of the way
        // round from the top of an orbit down to 3.5 km (146 s), it should come down where the path, worked out
        // exactly among the stars, says it will be by then.
        var start = Above(9000f);
        var stars = East * Planet.CircularSpeed(9000f) * 0.88f;
        var orbit = new Orbit(GroundBody.Planet, start, stars);
        const float downTo = 3500f;
        var anomaly = orbit.Descending(Planet.SeaRadius + downTo);
        Assert.NotNull(anomaly);
        var expected = orbit.GroundAt(anomaly.Value);
        var predicted = Pilots.WhereItFalls(start, Planet.OverGround(start, stars), downTo);
        Assert.NotNull(predicted);
        var off = Vector3.Distance(predicted.Value.Where, expected);
        output.WriteLine($"{orbit.TimeTo(anomaly.Value):0} s and {anomaly.Value - orbit.Now:0.00} rad round: {off:0.0} m from the path's point");
        Assert.True(off < 10f, $"{off:0.0} m off the path"); // measured 1.4 m (a plain step: 38 m); a step overshoots 3.5 km by up to its drop
    }

    [Fact]
    public void A_path_over_the_mountains_ends_on_them()
    {
        var (peakDirection, peak) = HighestWest();
        var start = Planet.Centre + peakDirection * (Planet.SeaRadius + peak + 400f);
        var orbit = Orbit.OverGround(GroundBody.Planet, start, -Planet.East(start) * 40f);

        var end = orbit.Landing;
        var terrain = Terrain.SurfaceHeight(Planet.Up(end));
        output.WriteLine($"peak {peak:0} m; ends {Planet.Altitude(end):0.0} m up over terrain at {terrain:0.0} m");
        Assert.True(orbit.EndsOnGround);
        Assert.InRange(Planet.Altitude(end) - terrain, -1f, 1f);
        Assert.True(terrain > 200f, $"it came down on terrain only {terrain:0} m up");
    }

    [Fact]
    public void Falling_back_the_path_ends_where_it_hits_not_on_the_far_side()
    {
        // Already coming down, westward: the path ahead is only the rest of the way down.
        var start = Above(2000f);
        var path = PathOf(start, -East * 150f - Up * 60f);
        Assert.True(path.Max(Distance) <= Distance(start) + 1f);
        Assert.True(Vector3.Dot(path[^1] - start, East) < 0f);
        Assert.True(OnTerrain(new Orbit(GroundBody.Planet, start, -East * 150f - Up * 60f).Landing)); // on the ground as it is by then
    }

    [Fact]
    public void An_escaping_path_goes_out_to_the_edge_and_stops()
    {
        var start = Above(5000f);
        var escape = MathF.Sqrt(2f * Planet.Mu / Distance(start));
        var path = PathOf(start, East * escape * 1.2f);
        Assert.InRange(Distance(path[^1]), Orbit.MaxRange - 10f, Orbit.MaxRange + 10f);
        Assert.True(path.Zip(path.Skip(1)).All(pair => Distance(pair.Second) >= Distance(pair.First) - 0.01f), "it should only ever go outward");
    }

    [Fact]
    public void Straight_up_the_path_goes_to_the_top_and_back_down()
    {
        var start = Above(500f);
        var path = PathOf(start, Up * 100f);
        // Where it stops: all its speed traded for height, against gravity weakening as it climbs.
        var top = -Planet.Mu / (100f * 100f / 2f - Planet.Mu / Distance(start)) - Planet.Radius;
        Assert.InRange(path.Max(Distance) - Planet.Radius, top - 1f, top + 1f);
        // Straight up and down among the stars, it comes down on whatever ground has turned under it by then.
        Assert.True(OnTerrain(new Orbit(GroundBody.Planet, start, Up * 100f).Landing));
    }

    [Fact]
    public void A_vessel_in_orbit_follows_its_predicted_path()
    {
        // Reach Orbit's upper stage in the orbit its autopilot puts it in (its top as it climbs to,
        // its bottom as it burns to), started there: the climb is OrbitTests' playthrough's.
        var pilot = new OrbitAutopilot();
        var upper = InOrbit.UpperStage(0, pilot.TargetApoapsis, InOrbit.ShareAtTop(pilot.TargetApoapsis, pilot.TargetPeriapsis));
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [upper], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        Assert.True(vessel.OrbitStable, $"not in orbit: {vessel.Periapsis:0} to {vessel.Apoapsis:0} m");
        var points = new List<Vector3>();
        Orbit.OverGround(GroundBody.Planet, vessel.CentreOfMass, vessel.Velocity).Path(points);
        var start = sim.Flight.WorldTime;

        // Coasting for a third of a lap, it should stay on the path drawn for it. The path is fixed
        // among the stars and the vessel is flown on the turning ground, so they are compared in
        // one frame: the path turned back by the time gone by, with the ground.
        var lap = 2 * Math.PI * (Planet.SeaRadius + vessel.Apoapsis) / Planet.CircularSpeed(vessel.Apoapsis);
        var furthest = 0f;
        var path = new List<Vector3>(points.Count);
        sim.Run((_, _) => default, f =>
        {
            path.Clear();
            foreach (var point in points)
                path.Add(Planet.Turned(point, -(f.WorldTime - start)));
            furthest = MathF.Max(furthest, DistanceToPath(f.Controlled.CentreOfMass, path));
            return false;
        }, maxSeconds: lap / 3);
        output.WriteLine($"at most {furthest:0.0} m off the path over a third of a lap");
        Assert.True(furthest < 10f, $"it strayed {furthest:0} m from the path");
    }

    [Theory]
    [InlineData(6.4)]  // one plan's 0.2 real seconds at 32x
    [InlineData(32.0)]
    [InlineData(120.0)]
    public void The_window_stays_on_the_path_as_the_ground_turns_between_plans(double elapsed)
    {
        // An inclined orbit, off the pad: the window is worked out on it, then drawn later on the
        // path as it is then, which is worked out afresh in the ground's frame as it is then.
        var start = Above(5000f);
        var heading = Vector3.Normalize(East + Planet.North(start));
        var planned = new Orbit(GroundBody.Planet, start, heading * Planet.CircularSpeed(5000f) * 1.02f);
        var point = planned.At(planned.Now + 1.5);

        var (position, velocity) = planned.StateAfter(elapsed);
        var now = new Orbit(GroundBody.Planet, Planet.Turned(position, -elapsed), Planet.TurnedDirection(velocity, -elapsed));
        var drawn = Paths.WindowPoint(Frame.Orbital, point, planned.TimeTo(planned.Now + 1.5), plannedAt: 100.0, now: 100.0 + elapsed);
        var onPath = now.At(now.AnomalyToward(drawn - Planet.Centre));
        var off = Vector3.Distance(drawn, onPath);
        output.WriteLine($"{elapsed:0.0} s after the plan: drawn {off:0.00} m off the path");
        Assert.True(off < 3f, $"the window was drawn {off:0} m off the path");
    }

    private static float DistanceToPath(Vector3 p, List<Vector3> path)
    {
        var best = float.MaxValue;
        for (var i = 1; i < path.Count; i++)
        {
            var (a, b) = (path[i - 1], path[i]);
            var t = Math.Clamp(Vector3.Dot(p - a, b - a) / (b - a).LengthSquared(), 0f, 1f);
            best = MathF.Min(best, Vector3.Distance(p, a + (b - a) * t));
        }
        return best;
    }

    [Fact]
    public void Zooming_out_looks_at_the_vessel_from_far_enough_to_see_the_orbit_and_comes_back()
    {
        var camera = new ChaseCamera(null);
        var focus = Above(4000f);
        const float frame = 1f / 60f;
        for (var i = 0; i < 10; i++)
            camera.Update(frame, focus, null, GroundBody.Planet, 0);
        var (before, beforeTarget) = (camera.Position, camera.Target);

        var reach = Planet.Radius + 4000f;
        camera.ToggleZoomOut(reach);
        for (var i = 0; i < 120; i++)
            camera.Update(frame, focus, null, GroundBody.Planet, 0);
        Assert.True(camera.ZoomedOut);
        Assert.True(Vector3.Distance(camera.Target, camera.Focus) < 1f, "it should look at the vessel");
        var half = float.DegreesToRadians(Scene.CameraVerticalFovDegrees) / 2;
        Assert.True(Vector3.Distance(camera.Position, camera.Target) * MathF.Tan(half) > reach, "the orbit doesn't fit on the screen");
        Assert.True(Vector3.Distance(camera.Focus, focus) < 0.1f, "it should still follow the vessel underneath");

        camera.ToggleZoomOut(reach);
        for (var i = 0; i < 120; i++)
            camera.Update(frame, focus, null, GroundBody.Planet, 0);
        Assert.False(camera.ZoomedOut);
        Assert.True(Vector3.Distance(camera.Position, before) < 0.01f && Vector3.Distance(camera.Target, beforeTarget) < 0.01f,
            "it should come back to just where it was");
    }

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, -60f)] // dragged to tip the camera down, still above the plane
    public void The_map_looks_from_above_the_orbits_plane(bool onTheMoon, float dragY)
    {
        const float frame = 1f / 60f;
        var body = onTheMoon ? GroundBody.Moon : GroundBody.Planet;
        var focus = onTheMoon ? OnTheMoon.Standing(OnTheMoon.LandingSpot).Position : Above(4000f);
        var camera = new ChaseCamera(null);
        camera.Update(frame, focus, null, body, 0);
        camera.ToggleZoomOut(body.Radius + 4000f);
        for (var i = 0; i < 120; i++)
            camera.Update(frame, focus, null, body, 0);
        if (dragY != 0f)
        {
            camera.DragOrbit(new Vector2(0f, dragY));
            camera.Update(frame, focus, null, body, 0);
        }
        var pole = Vector3.Normalize(body.NorthPole);
        Assert.True(Vector3.Dot(Vector3.Normalize(camera.Position - camera.Focus), pole) > 0.2f, "the map should look down from the north pole's side");
    }

    [Fact]
    public void A_vessel_never_flown_opens_the_map_on_the_fit_of_its_orbit()
    {
        const float frame = 1f / 60f;
        var focus = Above(4000f);
        Vector3 Opened(ChaseCamera camera)
        {
            camera.Update(frame, focus, null, GroundBody.Planet, 0);
            camera.ToggleZoomOut(Planet.Radius + 4000f);
            for (var i = 0; i < 120; i++)
                camera.Update(frame, focus, null, GroundBody.Planet, 0);
            return camera.Position;
        }
        var fresh = Opened(new ChaseCamera(null));
        var given = new ChaseCamera(null);
        given.Apply(GameView.DefaultView);
        Assert.True(Vector3.Distance(fresh, Opened(given)) < 1f, "the default view opens the map as a camera never set does");
    }

    [Fact]
    public void A_map_not_set_by_hand_is_fitted_afresh_to_each_orbit()
    {
        const float frame = 1f / 60f;
        var focus = Above(4000f);
        var camera = new ChaseCamera(null);
        camera.Update(frame, focus, null, GroundBody.Planet, 0);
        float OpenedAt(float reach)
        {
            camera.ToggleZoomOut(reach);
            for (var i = 0; i < 120; i++)
                camera.Update(frame, focus, null, GroundBody.Planet, 0);
            var far = Vector3.Distance(camera.Position, camera.Focus);
            camera.ToggleZoomOut(reach);
            for (var i = 0; i < 120; i++)
                camera.Update(frame, focus, null, GroundBody.Planet, 0);
            return far;
        }
        var near = OpenedAt(Planet.Radius + 4000f);
        Assert.True(OpenedAt(Planet.Radius + 400_000f) > near * 2f, "a larger orbit should be fitted farther out");
    }

    [Fact]
    public void A_map_zoomed_by_hand_to_the_least_distance_keeps_its_view()
    {
        const float frame = 1f / 60f;
        var focus = Above(4000f);
        var camera = new ChaseCamera(null);
        camera.Update(frame, focus, null, GroundBody.Planet, 0);
        camera.ToggleZoomOut(Planet.Radius + 400_000f);
        for (var i = 0; i < 120; i++)
            camera.Update(frame, focus, null, GroundBody.Planet, 0);
        camera.Zoom(500f); // in, as far as it goes
        camera.Update(frame, focus, null, GroundBody.Planet, 0);
        Assert.Equal(Scene.ZoomOutMinDistance, Vector3.Distance(camera.Position, camera.Focus), 1f);
        camera.ToggleZoomOut(Planet.Radius + 400_000f);
        for (var i = 0; i < 120; i++)
            camera.Update(frame, focus, null, GroundBody.Planet, 0);
        camera.ToggleZoomOut(Planet.Radius + 400_000f);
        for (var i = 0; i < 120; i++)
            camera.Update(frame, focus, null, GroundBody.Planet, 0);
        Assert.Equal(Scene.ZoomOutMinDistance, Vector3.Distance(camera.Position, camera.Focus), 1f);
    }

    [Fact]
    public void The_map_opens_on_the_view_its_vessel_left()
    {
        const float frame = 1f / 60f;
        var reach = Planet.Radius + 4000f;
        var focus = Above(4000f);
        var camera = new ChaseCamera(null);
        camera.Update(frame, focus, null, GroundBody.Planet, 0);
        // Opens the map, lets it settle, optionally turns it by hand, and gives where it looks from; then closes it again.
        Vector3 Open(Action? byHand = null)
        {
            camera.ToggleZoomOut(reach);
            for (var i = 0; i < 120; i++)
                camera.Update(frame, focus, null, GroundBody.Planet, 0);
            if (byHand is not null)
            {
                byHand();
                for (var i = 0; i < 5; i++)
                    camera.Update(frame, focus, null, GroundBody.Planet, 0);
            }
            var from = camera.Position;
            camera.ToggleZoomOut(reach);
            for (var i = 0; i < 120; i++)
                camera.Update(frame, focus, null, GroundBody.Planet, 0);
            return from;
        }

        var own = Open();
        Assert.True(Vector3.Distance(own, Open()) < 1f, "opened again, untouched: the same view");

        // Turned and zoomed by hand: opened on again.
        var set = Open(() => { camera.DragOrbit(new Vector2(300f, -120f)); camera.Zoom(-6f); });
        Assert.True(Vector3.Distance(set, own) > 1000f, "the drag and the wheel moved it");
        Assert.True(Vector3.Distance(set, Open()) < 1f, "opened again on the view set by hand");

        // Another camera given that vessel's view (as a switch back to it does) opens on it, not on its own.
        var view = camera.View;
        var other = new ChaseCamera(null);
        other.Update(frame, focus, null, GroundBody.Planet, 0);
        other.Apply(view with { Map = false });
        for (var i = 0; i < 5; i++)
            other.Update(frame, focus, null, GroundBody.Planet, 0);
        other.ToggleZoomOut(reach);
        for (var i = 0; i < 120; i++)
            other.Update(frame, focus, null, GroundBody.Planet, 0);
        Assert.True(Vector3.Distance(set, other.Position) < 1f, "a view brought back opens the map as it was left");
    }

    [Fact]
    public void Zooming_out_takes_the_same_real_time_in_time_warp()
    {
        var camera = new ChaseCamera(null);
        var focus = Above(4000f);
        camera.Update(1f / 60, focus, null, GroundBody.Planet, 0);
        camera.ToggleZoomOut(Planet.Radius + 4000f);
        // One frame at 32x warp: half a second of game time, but only a sixtieth of real time.
        camera.Update(32f / 60, focus, 1f / 60, GroundBody.Planet, 0);
        Assert.True(Vector3.Distance(camera.Position, focus) < 1000f, "it jumped most of the way out in one frame");
    }
}
