using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The map zoomed out (<see cref="SystemMap"/>): the whole system on the screen all the way out (the
/// sun, every body, the planet's whole path round the sun), each body's path round what it goes
/// round in the frame the map is drawn in, from either body, faded in by its size; the sun's disc
/// where the sun is from a camera millions of metres out, and the camera never in the sun; what
/// is hidden behind the sun and the bodies; and every vessel flying drawn round its own body: a
/// lap among its stars under formula (out to the first sphere it would enter), patched in the physics.
/// </summary>
public sealed class SystemMapTests(ITestOutputHelper output)
{
    private static readonly Vector2 Screen = new(1280f, 720f);

    /// <summary>Times through a year (and so the sun all round the planet), each a little into a moon's lap more.</summary>
    private static IEnumerable<double> ThroughTheYear()
    {
        for (var k = 0; k < 12; k++)
            yield return k * (Scene.DaySeconds * Scene.YearDays / 12) + k * 97.0;
    }

    /// <summary>
    /// A camera following something at <paramref name="focus"/> (in <paramref name="body"/>'s
    /// frame), zoomed out and then by <paramref name="clicks"/> of the wheel (out, as positive) at
    /// world time <paramref name="t"/>, its angles dragged by <paramref name="drag"/> pixels (as the
    /// right drag turns it there).
    /// </summary>
    internal static ChaseCamera ZoomedOut(GroundBody body, Vector3 focus, Vector2 drag, double t, float clicks = 200f)
    {
        const float dt = 1f / 60f;
        var camera = new ChaseCamera(null);
        camera.Update(dt, focus, dt, body, t);
        camera.ToggleZoomOut(2f * body.Radius);
        for (var i = 0; i < 90; i++) // past ZoomOutSeconds
            camera.Update(dt, focus, dt, body, t);
        camera.Zoom(-clicks);
        camera.DragOrbit(drag);
        camera.Update(dt, focus, dt, body, t);
        return camera;
    }

    [Fact]
    public void Zoomed_all_the_way_out_the_whole_system_fits()
    {
        // From the planet (a vessel over Home) and from the moon (one landed on it), at the start's
        // angles and turned right over the pole and down to the orbits' plane, through a year: the
        // sun's whole disc, every body's centre and the planet's whole path round the sun on a
        // 1280 x 720 screen.
        var (planet, moon) = (GroundBody.Planet, GroundBody.Moon);
        var path = new Vector3[SystemMap.BodyPathSegments + 1];
        var (tightest, tightestPath) = (float.PositiveInfinity, float.PositiveInfinity);
        foreach (var (name, body, focus) in new[]
        {
            ("over Home", planet, new Vector3(0f, 3000f, 0f)),
            ("on the moon", moon, OnTheMoon.Standing(OnTheMoon.LandingSpot).Position),
        })
            foreach (var drag in new[] { Vector2.Zero, new Vector2(400f, 2000f), new Vector2(-900f, -2000f) })
                foreach (var t in ThroughTheYear())
                {
                    var camera = ZoomedOut(body, focus, drag, t);
                    Assert.Equal(Scene.ZoomOutMaxDistance, Vector3.Distance(camera.Position, camera.Focus), Scene.ZoomOutMaxDistance * 1e-4f);
                    var sun = Bodies.Change(planet, body, t).Point(SolarSystem.SunCentreAt(t)).ToSingle();
                    Assert.True(camera.ToScreen(sun, Screen, out var at, out var depth), $"{name} at {t:0} s the sun is behind the camera");
                    var radius = Scene.SunRadius / ChaseCamera.MetresPerPixel(depth, Screen);
                    var room = MathF.Min(MathF.Min(at.X - radius, Screen.X - at.X - radius), MathF.Min(at.Y - radius, Screen.Y - at.Y - radius));
                    Assert.True(room > 0f, $"{name} at {t:0} s, dragged {drag}, the sun's disc is off the screen at {at} ({radius:0.0} px across)");
                    tightest = MathF.Min(tightest, room);
                    foreach (var other in Bodies.All)
                    {
                        Assert.True(camera.ToScreen(Bodies.CentreIn(other, body, t).ToSingle(), Screen, out var seen, out _), $"{other.Name} is behind the camera");
                        Assert.InRange(seen.X, 0f, Screen.X);
                        Assert.InRange(seen.Y, 0f, Screen.Y);
                    }
                    Bodies.Path(planet, body, t, path);
                    foreach (var p in path)
                    {
                        Assert.True(camera.ToScreen(p, Screen, out var on, out _), "the planet's path is behind the camera");
                        var edge = MathF.Min(MathF.Min(on.X, Screen.X - on.X), MathF.Min(on.Y, Screen.Y - on.Y));
                        Assert.True(edge > 0f, $"{name} at {t:0} s, dragged {drag}, the planet's path runs off the screen at {on}");
                        tightestPath = MathF.Min(tightestPath, edge);
                    }
                }
        output.WriteLine($"zoomed all the way out ({Scene.ZoomOutMaxDistance / 1000f:0} km), the sun's disc is at least {tightest:0} px inside the screen's edge, the planet's path {tightestPath:0} px");
    }

    [Fact]
    public void Zoomed_out_the_camera_is_never_in_the_sun()
    {
        // Turned down to the orbits' plane (the sun's) and all the way round in small steps, at
        // zooms a tenth of a click apart through the sun's distance: the camera, which without its
        // clearance would pass through the sun, keeps the sun's clearance from its centre, and
        // comes right up to it (so the sweep reaches it).
        var planet = GroundBody.Planet;
        var focus = new Vector3(0f, 3000f, 0f);
        const double T = 1500;
        var sun = SolarSystem.SunCentreAt(T);
        const float dt = 1f / 60f;
        var level = -float.DegreesToRadians(Scene.ZoomOutPitchDegrees) / Scene.CameraRadiansPerDragPixel;
        var camera = ZoomedOut(planet, focus, new Vector2(0f, level), T, 0f);
        var startsAt = Vector3.Distance(camera.Position, planet.Centre);
        camera.Zoom(-MathF.Log((float)(0.97 * Scene.SunDistance / startsAt)) / MathF.Log(1f / 0.9f)); // to just short of the sun's distance
        camera.Update(dt, focus, dt, planet, T);
        var nearest = double.PositiveInfinity;
        const int Yaws = 720;
        var step = MathF.Tau / Yaws / Scene.CameraRadiansPerDragPixel;
        for (var zoom = 0; zoom < 60; zoom++)
        {
            camera.Zoom(-0.01f * 5f);
            for (var yaw = 0; yaw < Yaws; yaw++)
            {
                camera.DragOrbit(new Vector2(step, 0f));
                camera.Update(dt, focus, dt, planet, T);
                nearest = Math.Min(nearest, ((Vector3Double)camera.Position - sun).Length());
            }
        }
        output.WriteLine($"the camera came within {nearest / Scene.SunRadius:0.000} of the sun's radii of its centre");
        Assert.True(nearest >= Scene.ZoomOutSunClearance * Scene.SunRadius * 0.999, $"the camera came within {nearest:0} m of the sun's centre");
        Assert.True(nearest < Scene.ZoomOutSunClearance * Scene.SunRadius * 1.01, "the sweep never came up to the sun");
    }

    /// <summary>Each point of <paramref name="path"/>'s distance from <paramref name="centre"/>, at worst off <paramref name="radius"/>.</summary>
    private static double WorstOff(ReadOnlySpan<Vector3> path, Vector3Double centre, double radius)
    {
        var worst = 0.0;
        foreach (var p in path)
            worst = Math.Max(worst, Math.Abs(((Vector3Double)p - centre).Length() - radius));
        return worst;
    }

    [Fact]
    public void The_planets_path_goes_round_the_sun_and_each_bodys_round_its_parent()
    {
        // In the planet's frame, through a year: the planet's path round the sun (Bodies.Path for
        // the root, SolarSystem.PlanetPath's) its distance from the sun all round, to a metre,
        // starting and ending at its centre; the moon's its distance from the planet, starting and
        // ending at the moon's centre now; each among the stars as they match the ground now, so a
        // quarter of a lap on is where the body will be then, turned back by the ground's turn meanwhile.
        var (planet, moon) = (GroundBody.Planet, GroundBody.Moon);
        var path = new Vector3[257];
        var (worstSun, worstMoon) = (0.0, 0.0);
        foreach (var t in ThroughTheYear())
        {
            var sun = SolarSystem.SunCentreAt(t);
            Assert.True(Vector3.Distance(Vector3.Normalize((sun - planet.Centre).ToSingle()), SolarSystem.SunDirection(t)) < 1e-6f, "the sun is where its direction has it");
            SolarSystem.PlanetPath(t, path);
            worstSun = Math.Max(worstSun, WorstOff(path, sun, Scene.SunDistance));
            Assert.True(Vector3.Distance(path[0], planet.Centre) < 1f && Vector3.Distance(path[^1], planet.Centre) < 1f, "the planet's path starts and ends at its centre");
            var asBodies = new Vector3[path.Length];
            Bodies.Path(planet, planet, t, asBodies);
            Assert.All(Enumerable.Range(0, path.Length), i => Assert.True(Vector3.Distance(asBodies[i], path[i]) < 1f));
            Assert.True(((Vector3Double)path[64] - sun).Length() > Scene.SunDistance * 0.99, "a quarter of a lap on is round the sun");

            Bodies.Path(moon, planet, t, path);
            worstMoon = Math.Max(worstMoon, WorstOff(path, planet.Centre, Scene.MoonDistance));
            var centre = Bodies.CentreIn(moon, planet, t);
            Assert.True(((Vector3Double)path[0] - centre).Length() < 1 && ((Vector3Double)path[^1] - centre).Length() < 1, "the moon's path starts and ends at its centre");
            var quarter = SolarSystem.MoonLapSeconds / 4;
            var then = planet.Turned(Bodies.CentreIn(moon, planet, t + quarter).ToSingle(), quarter);
            Assert.True(Vector3.Distance(path[64], then) < 1f, $"a quarter of the moon's lap on, its path is {Vector3.Distance(path[64], then):0.00} m from where it will be among the stars");
        }
        output.WriteLine($"the planet's path is within {worstSun:0.000} m of the sun's distance; the moon's within {worstMoon * 1000:0.0} mm of its distance from the planet");
        Assert.True(worstSun < 1, $"the planet's path is {worstSun:0.000} m off its distance");
        Assert.True(worstMoon < 1, $"the moon's path is {worstMoon:0.000} m off its distance");
    }

    /// <summary>A simulation of <paramref name="vessels"/> at world time <paramref name="time"/>, beside the new rocket on Home's pad, a step taken.</summary>
    private static Simulation WorldWith(double time, params VesselState[] vessels)
    {
        var sim = new Simulation(Mission.FreePlay, world: new WorldState
        {
            Time = time, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1,
        });
        sim.Step(default);
        return sim;
    }

    [Fact]
    public void Seen_from_the_moon_the_paths_pass_through_the_bodies()
    {
        // Flying a lander on the moon, the map (zoomed all the way out) draws each body's path in the
        // moon's frame: the planet's through the planet's centre, round the sun at its distance; the
        // moon's through the moon's own centre, round the planet at its; and the sun's mark where the
        // sun is.
        using var sim = WorldWith(2345.6, OnTheMoon.Lander(0, OnTheMoon.LandingSpot));
        var lander = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: lander));
        sim.Step(default);
        var flight = sim.Flight;
        Assert.Same(GroundBody.Moon, flight.Body);
        var (planet, moon, t) = (GroundBody.Planet, GroundBody.Moon, flight.WorldTime);
        var frames = new BodyFrames();
        frames.Begin(t, moon);
        var camera = ZoomedOut(moon, lander.CentreOfMass, Vector2.Zero, t);
        var map = new SystemMap();
        map.Build(flight, frames, camera.Position);

        var sun = Bodies.Change(planet, moon, t).Point(SolarSystem.SunCentreAt(t));
        Assert.True(((Vector3Double)map.SunAt - sun).Length() < 1, "the sun's mark is where the sun is");
        var planetCentre = Bodies.CentreIn(planet, moon, t);
        foreach (var body in Bodies.All)
        {
            var points = map.BodyPath(body.Index);
            var (round, radius, through) = body == planet
                ? (sun, (double)Scene.SunDistance, planetCentre)
                : (planetCentre, (double)Scene.MoonDistance, (Vector3Double)moon.Centre);
            var off = WorstOff(points, round, radius);
            var miss = ((Vector3Double)points[0] - through).Length();
            output.WriteLine($"{body.Name}'s path: {off:0.000} m off its distance at worst, {miss * 1000:0.0} mm from its centre at its start; drawn at {map.BodyPathAlpha(body.Index):0.00}");
            Assert.True(off < 1, $"{body.Name}'s path is {off:0.000} m off its distance");
            Assert.True(miss < 1, $"{body.Name}'s path passes {miss:0.000} m from its centre");
            Assert.Equal(Scene.MapBodyPathAlpha, map.BodyPathAlpha(body.Index), 1e-3f); // all the way out, faded all the way in
        }
    }

    [Fact]
    public void Close_by_the_planets_path_is_not_drawn_and_the_moons_is()
    {
        // At the closest zoom over Home (16 km out) the planet's path round the sun would be a
        // straight line through the planet: it is faded out (by the camera's distance from the
        // planet, against the path's size), while the moon's is drawn in full; by 600 km out the
        // planet's is in full too. At the moon's closest zoom its own path is in full.
        using var sim = WorldWith(1234.5, InOrbit.Round(GroundBody.Planet, 0, 2000f));
        var flight = sim.Flight;
        var (planet, moon) = (GroundBody.Planet, GroundBody.Moon);
        var frames = new BodyFrames();
        frames.Begin(flight.WorldTime, planet);
        var map = new SystemMap();
        foreach (var (distance, planetAlpha) in new[] { (Scene.ZoomOutMinDistance, 0f), (150_000f, 0f), (600_000f, Scene.MapBodyPathAlpha) })
        {
            map.Build(flight, frames, planet.Centre + Vector3.Normalize(new Vector3(0.2f, 0.3f, -1f)) * distance);
            output.WriteLine($"{distance / 1000:0} km out: the planet's path at {map.BodyPathAlpha(planet.Index):0.00}, the moon's at {map.BodyPathAlpha(moon.Index):0.00}");
            Assert.Equal(planetAlpha, map.BodyPathAlpha(planet.Index), 1e-3f);
            Assert.Equal(Scene.MapBodyPathAlpha, map.BodyPathAlpha(moon.Index), 1e-3f);
        }
        // From 16 km over the moon (in the moon's frame), its path in full.
        var at = Bodies.Change(moon, planet, flight.WorldTime).Inverse.Point(planet.Centre).ToSingle();
        frames.Begin(flight.WorldTime, moon);
        map.Build(flight, frames, moon.Centre + Vector3.Normalize(at - moon.Centre) * Scene.ZoomOutMinDistance);
        Assert.Equal(Scene.MapBodyPathAlpha, map.BodyPathAlpha(moon.Index), 1e-3f);
    }

    /// <summary>A view and projection from <paramref name="camera"/> towards <paramref name="target"/>, as the HUD's (<see cref="ChaseCamera.ViewProjection"/>).</summary>
    private static Matrix4x4 Looking(Vector3 camera, Vector3 target)
    {
        var (near, far) = ChaseCamera.Planes;
        var up = MathF.Abs(Vector3.Dot(Vector3.Normalize(target - camera), Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        return Matrix4x4.CreateLookAt(camera, target, up) * ChaseCamera.Projection(float.DegreesToRadians(Scene.CameraVerticalFovDegrees), Screen.X / Screen.Y, near, far);
    }

    [Fact]
    public void What_is_behind_the_sun_or_a_body_is_hidden()
    {
        // From out past the sun, looking back through it at the planet: the planet's dot (and the
        // moon's, near it) is hidden by the sun, and so is the planet's path where it is behind the
        // sun's disc; moved aside so the planet is clear of it, the dot is drawn. From the planet's
        // night side, the sun straight behind the planet, the sun's mark is hidden by the planet.
        using var sim = WorldWith(1234.5, InOrbit.Round(GroundBody.Planet, 0, 2000f));
        var flight = sim.Flight;
        var planet = GroundBody.Planet;
        var t = flight.WorldTime;
        var frames = new BodyFrames();
        frames.Begin(t, planet);
        var map = new SystemMap();
        var sun = SolarSystem.SunCentreAt(t).ToSingle();
        var away = Vector3.Normalize(sun - planet.Centre);
        var across = Vector3.Normalize(Vector3.Cross(away, planet.NorthPole));

        var beyond = sun + away * 4_000_000f;
        map.Build(flight, frames, beyond);
        map.Lay(Looking(beyond, planet.Centre), beyond, Screen);
        Assert.DoesNotContain(map.Dots, d => d.Body == planet.Index);
        Assert.NotNull(map.SunMark);

        var aside = beyond + across * 1_500_000f;
        map.Build(flight, frames, aside);
        map.Lay(Looking(aside, planet.Centre), aside, Screen);
        Assert.Contains(map.Dots, d => d.Body == planet.Index);

        var night = planet.Centre - away * 200_000f;
        map.Build(flight, frames, night);
        map.Lay(Looking(night, planet.Centre), night, Screen);
        Assert.Null(map.SunMark);
        var beside = night + across * 60_000f;
        map.Build(flight, frames, beside);
        map.Lay(Looking(beside, sun), beside, Screen);
        Assert.NotNull(map.SunMark);
    }

    [Fact]
    public void Far_out_the_suns_disc_is_where_the_sun_is_from_the_camera()
    {
        // The sky's sun (FrameInput.SunDisc, from the main view's camera in double) from Home's pad
        // and from the map's furthest zoom at the planet and at the moon: its way and its angular
        // radius as the sun's true place has them from there, within 0.01°. From the pad it is the
        // planet's sun but for the planet's radius over the sun's distance (0.2°); far out it is
        // far from it.
        var planet = GroundBody.Planet;
        var frames = new BodyFrames();
        var worst = 0.0;
        foreach (var t in ThroughTheYear())
            foreach (var (name, body, focus) in new[]
            {
                ("Home's pad", planet, new Vector3(0f, 20f, 0f)),
                ("the furthest zoom at the planet", planet, new Vector3(0f, 3000f, 0f)),
                ("the furthest zoom at the moon", GroundBody.Moon, OnTheMoon.Standing(OnTheMoon.LandingSpot).Position),
            })
            {
                frames.Begin(t, body);
                var camera = new ChaseCamera(null);
                camera.Update(1f / 60f, focus, null, body, t);
                if (name != "Home's pad")
                    camera = ZoomedOut(body, focus, new Vector2(300f, 500f), t);
                var (at, _, _) = SceneInput.InScene(frames, body, camera);
                var disc = SceneInput.SunFrom(frames, at);
                // The truth, apart: the sun's place from the planet's centre by its direction, less the camera.
                var toSun = (Vector3Double)planet.Centre + (Vector3Double)SolarSystem.SunDirection(t) * Scene.SunDistance - at;
                var way = Vector3Double.Normalize(toSun);
                var drawn = Vector3Double.Normalize(new Vector3(disc.X, disc.Y, disc.Z));
                var off = double.RadiansToDegrees(Math.Atan2(Vector3Double.Cross(way, drawn).Length(), Vector3Double.Dot(way, drawn)));
                var size = double.RadiansToDegrees(Math.Abs(disc.W - Math.Asin(Scene.SunRadius / toSun.Length())));
                Assert.True(off < 0.01 && size < 0.01, $"from {name} at {t:0} s the sun's disc is {off:0.0000}° off and its size {size:0.0000}° off");
                worst = Math.Max(worst, Math.Max(off, size));
                var fromPlanet = double.RadiansToDegrees(Math.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(new Vector3(disc.X, disc.Y, disc.Z)), SolarSystem.SunDirection(t)), -1f, 1f)));
                if (name == "Home's pad")
                    Assert.True(fromPlanet < 0.25 && Math.Abs(disc.W - SolarSystem.SunAngularRadius) < 2e-4f, $"from the pad the sun is {fromPlanet:0.000}° from the planet's");
                else
                    Assert.True(fromPlanet > 1, $"from {name} the sun is only {fromPlanet:0.000}° from the planet's way to it");
            }
        output.WriteLine($"the sun's disc within {worst:0.000000}° of where the sun is, its way and its size");
    }

    [Fact]
    public void On_the_map_a_vessel_orbiting_the_moon_is_drawn_round_the_moon()
    {
        // Flying the new rocket on Home's pad, a stage on a round orbit of the moon under formula is
        // drawn on the map round the moon where the moon is now (in the planet's frame), a closed
        // loop from where it is (among the stars, whatever the line's frame); a stage in orbit of the
        // planet round the planet.
        var (planet, moon) = (GroundBody.Planet, GroundBody.Moon);
        using var sim = WorldWith(3456.7, InOrbit.Round(moon, 0, 400f), InOrbit.Round(planet, 1, 2000f));
        var flight = sim.Flight;
        Assert.Same(planet, flight.Body);
        var t = flight.WorldTime;
        var frames = new BodyFrames();
        frames.Begin(t, planet);
        var map = new SystemMap();
        map.Build(flight, frames, planet.Centre + new Vector3(0f, 0f, -1f) * 100_000f);
        var points = new List<Vector3>();
        foreach (var (id, body) in new[] { (0, moon), (1, planet) })
        {
            var v = flight.WorldVessels.Single(w => w.Id == id);
            Assert.Same(body, v.Body);
            Assert.True(v.OnFormula);
            Assert.True(map.PathOf(v, points), $"vessel {id} has no path drawn");
            Assert.False(map.IsPatched(v));
            var change = Bodies.Change(body, planet, t);
            var centre = change.Point(body.Centre);
            var radius = (v.CentreOfMass - body.Centre).Length();
            var off = WorstOff(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points), centre, radius);
            var start = ((Vector3Double)points[0] - change.Point(v.CentreOfMass)).Length();
            var shut = Vector3.Distance(points[0], points[^1]);
            output.WriteLine($"vessel {id} round {body.Name} ({flight.LineFrame}): {points.Count} points, {off:0.00} m off its orbit's radius round it, starting {start:0.00} m from it, its ends {shut:0.00} m apart");
            Assert.True(off < 5, $"vessel {id}'s path is {off:0.00} m off its round orbit of {body.Name}");
            Assert.True(start < 5, $"vessel {id}'s path starts {start:0.00} m from it");
            Assert.True(shut < 5, $"vessel {id}'s lap is open by {shut:0.00} m");
        }
        // The flown one's path is the flown path's own, not the map's others'.
        Assert.False(map.PathOf(flight.Controlled, points));
    }

    /// <summary>
    /// A stage on its way into the moon's sphere from outside it (in the planet's frame at world time
    /// <paramref name="t"/>): <paramref name="side"/> of its sphere's radius out from the moon's
    /// centre along <paramref name="from"/>, moving in at 250 m/s as the moon moves, passing
    /// <paramref name="miss"/> metres to one side of its centre.
    /// </summary>
    private static VesselState IntoTheMoon(int id, double t, Vector3 from, float side, float miss)
    {
        var (planet, moon) = (GroundBody.Planet, GroundBody.Moon);
        var centre = Bodies.CentreIn(moon, planet, t).ToSingle();
        var moonStars = planet.AmongStars(centre, SolarSystem.MoonVelocity(t).ToSingle());
        var beside = Vector3.Normalize(Vector3.Cross(from, planet.NorthPole));
        var at = centre + from * (moon.SphereRadius * side) + beside * miss;
        return InOrbit.At(InOrbit.Round(planet, id, 2000f), at, moonStars - from * 250f);
    }

    [Fact]
    public void A_vessel_in_the_physics_is_drawn_patched_and_one_under_formula_only_round_its_own_body()
    {
        // Three stages on their way into the moon's sphere, one flown, one beside it (so in the
        // physics, witnessed), one far off on the other side (under formula). The one beside the flown
        // one is drawn patched, its path going on round the moon as Patched.Legs has it, its last leg
        // the moon's; the one under formula only along its leg round the planet: its lap ends where it
        // would cross into the moon's sphere, the moon's sphere's radius from where the moon will be then.
        var (planet, moon) = (GroundBody.Planet, GroundBody.Moon);
        const double T = 2222.2;
        var centre = Bodies.CentreIn(moon, planet, T).ToSingle();
        var out1 = Vector3.Normalize(Vector3.Cross(planet.NorthPole, centre - planet.Centre));
        using var sim = WorldWith(T,
            IntoTheMoon(0, T, out1, 1.3f, 600f), IntoTheMoon(1, T, out1, 1.3f, 640f), IntoTheMoon(2, T, -out1, 1.6f, 900f));
        var flight = sim.Flight;
        var (flown, beside, far) = (flight.WorldVessels.Single(v => v.Id == 0), flight.WorldVessels.Single(v => v.Id == 1), flight.WorldVessels.Single(v => v.Id == 2));
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Step(default);
        sim.Step(default);
        Assert.Same(flown, flight.Controlled);
        Assert.Same(planet, beside.Body);
        Assert.NotNull(beside.Physics);
        Assert.True(far.OnFormula);
        var t = flight.WorldTime;
        var frames = new BodyFrames();
        frames.Begin(t, planet);
        var map = new SystemMap();
        map.Build(flight, frames, planet.Centre + new Vector3(0f, 0f, -1f) * 100_000f);
        var points = new List<Vector3>();

        Assert.True(map.PathOf(beside, points));
        Assert.True(map.IsPatched(beside));
        var legs = new List<PathLeg>();
        Patched.Legs(planet, beside.CentreOfMass, planet.AmongStars(beside.CentreOfMass, beside.Velocity), t, legs);
        Assert.True(legs.Count >= 2 && legs[^1].Body == moon, "the one beside the flown one goes into the moon's sphere");
        var last = new LegPlace(legs[^1], planet, t);
        var samples = new List<(Vector3 At, double Seconds)>();
        legs[^1].Orbit.Samples(samples, 0, legs[^1].End - legs[^1].Start, SystemMap.LapSegments);
        var end = last.Point(flight.LineFrame, samples[^1].At, legs[^1].Start - t + samples[^1].Seconds);
        output.WriteLine($"the witnessed one: {points.Count} points over {legs.Count} legs, its end {Vector3.Distance(points[^1], end):0.00} m from its last leg's, round the moon");
        Assert.True(Vector3.Distance(points[^1], end) < 1f, "its path ends as its patched path round the moon does");
        // One stretch a leg: the line breaks at each crossing, where the last leg starts as it is placed.
        var starts = map.StartsOf(beside);
        Assert.Equal(legs.Count - 1, starts.Count);
        var start = last.Point(flight.LineFrame, samples[0].At, legs[^1].Start - t + samples[0].Seconds);
        Assert.True(Vector3.Distance(points[starts[^1]], start) < 1f, "its last leg starts a stretch of its own");
        // And as laid on the screen: no run of it goes across a leg's start (each of its runs is
        // its points in a row, by where each point is on the screen, all within one leg).
        var eye = planet.Centre + new Vector3(0f, 0f, -1f) * 100_000f;
        var viewProjection = Looking(eye, planet.Centre);
        map.Lay(viewProjection, eye, Screen);
        var onScreen = points.Select(p => Vector4.Transform(new Vector4(p, 1f), viewProjection))
            .Select(c => c.W > 1e-4f ? new Vector2((c.X / c.W + 1f) * Screen.X * 0.5f, (1f - c.Y / c.W) * Screen.Y * 0.5f) : new Vector2(float.NaN)).ToList();
        var runs = map.Runs.Where(r => r.Vessel == beside.Id).ToList();
        Assert.NotEmpty(runs);
        var laidAcross = 0;
        foreach (var run in runs)
        {
            var screen = map.Screen[run.First];
            var k = onScreen.FindIndex(p => Vector2.Distance(p, new Vector2(screen.X, screen.Y)) < 0.01f);
            Assert.True(k >= 0, "a run's first point is one of its path's");
            for (var j = 1; j < run.Count; j++)
                Assert.True(Vector2.Distance(onScreen[k + j], new Vector2(map.Screen[run.First + j].X, map.Screen[run.First + j].Y)) < 0.01f, "a run's points are its path's in a row");
            laidAcross += starts.Count(s => k < s && s < k + run.Count);
        }
        output.WriteLine($"laid in {runs.Count} run(s), {laidAcross} across any of its {starts.Count} leg start(s)");
        Assert.Equal(0, laidAcross);

        Assert.True(map.PathOf(far, points));
        Assert.False(map.IsPatched(far));
        var leg = new PathLeg(planet, Orbit.OverGround(planet, far.CentreOfMass, far.Velocity), t, double.PositiveInfinity);
        var crossing = Patched.Crossing(leg) ?? throw new Xunit.Sdk.XunitException("the one under formula never reaches the moon's sphere");
        var moonThen = planet.Turned(Bodies.CentreIn(moon, planet, crossing).ToSingle(), crossing - t); // among the stars as they are now
        var short_ = Vector3.Distance(points[^1], moonThen) - moon.SphereRadius;
        output.WriteLine($"the one under formula: {points.Count} points, ending {short_:0.0} m outside the moon's sphere where the moon will be as it crosses, {crossing - t:0.0} s on");
        Assert.InRange(short_, -0.02f * moon.SphereRadius, 0.02f * moon.SphereRadius);

        // A second on, its lap kept (its orbit unchanged), its line starts where it is now, not where
        // it was as the lap was made.
        var remakes = map.Remakes;
        for (var i = 0; i < 60; i++)
            sim.Step(default);
        frames.Begin(flight.WorldTime, planet);
        map.Build(flight, frames, planet.Centre + new Vector3(0f, 0f, -1f) * 100_000f);
        Assert.Equal(remakes, map.Remakes);
        Assert.True(map.PathOf(far, points));
        output.WriteLine($"a second on, its line starts {Vector3.Distance(points[0], far.CentreOfMass):0.00} m from it, its next point {Vector3.Distance(points[1], far.CentreOfMass):0.0} m on");
        Assert.True(Vector3.Distance(points[0], far.CentreOfMass) < 1f, "its line starts where it was, not where it is");
        Assert.True(Vector3.Dot(points[1] - points[0], points[2] - points[1]) > 0f, "its line goes back to where it was first, the stretch it has flown since");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_vessel_escaping_under_formula_has_its_path_end_at_the_solar_systems_edge(bool straightUp)
    {
        var planet = GroundBody.Planet;
        var from = planet.Centre + Vector3.Normalize(new Vector3(0.5f, -0.3f, 0.6f)) * (planet.SeaRadius + 20000f);
        var way = straightUp ? planet.Up(from) : planet.East(from);
        using var sim = WorldWith(777.7, InOrbit.At(InOrbit.Round(planet, 0, 2000f), from, way * 1500f));
        var flight = sim.Flight;
        var v = flight.WorldVessels.Single(w => w.Id == 0);
        Assert.True(v.OnFormula);
        var frames = new BodyFrames();
        frames.Begin(flight.WorldTime, planet);
        var map = new SystemMap();
        map.Build(flight, frames, planet.Centre + new Vector3(0f, 0f, -1f) * 100_000f);
        var points = new List<Vector3>();
        Assert.True(map.PathOf(v, points));
        var end = SolarSystem.FromSun(planet, points[^1], flight.WorldTime); // the map's points are among the stars, a long way on
        output.WriteLine($"{points.Count} points, ending {end / 1e3:#,0} km from the sun");
        Assert.InRange(end, 0.99 * Scene.SolarSystemEdge, 1.01 * Scene.SolarSystemEdge);
        Assert.True(points.Count <= SystemMap.LapSegments + 2);

        // Burning (its orbit changing every step), its lap is made again, out to the edge, every
        // frame: allocating nothing.
        var camera = planet.Centre + new Vector3(0f, 0f, -1f) * 100_000f;
        using var burning = WorldWith(777.7, InOrbit.At(InOrbit.Round(planet, 0, 2000f), from, way * 1500f) with { Throttle = 1f });
        var burner = burning.Flight.WorldVessels.Single(w => w.Id == 0);
        Assert.True(burner.OnFormula);
        var remade = new SystemMap();
        for (var i = 0; i < 3; i++)
        {
            burning.Step(default);
            frames.Begin(burning.Flight.WorldTime, planet);
            remade.Build(burning.Flight, frames, camera);
        }
        var bytes = 0L;
        for (var i = 0; i < 10; i++)
        {
            burning.Step(default);
            frames.Begin(burning.Flight.WorldTime, planet);
            var remakes = remade.Remakes;
            var before = GC.GetAllocatedBytesForCurrentThread();
            remade.Build(burning.Flight, frames, camera);
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(remakes + 1, remade.Remakes);
        }
        Assert.True(remade.PathOf(burner, points));
        Assert.InRange(SolarSystem.FromSun(planet, points[^1], burning.Flight.WorldTime), 0.99 * Scene.SolarSystemEdge, 1.01 * Scene.SolarSystemEdge);
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void A_vessel_going_straight_up_has_its_line_drawn()
    {
        // A stage going straight up among the stars far from the flown one (under formula): its map
        // path is the line it goes up and down along, out from the planet's centre.
        var planet = GroundBody.Planet;
        var from = planet.Centre + Vector3.Normalize(new Vector3(0.5f, -0.3f, 0.6f)) * (planet.SeaRadius + 3000f);
        using var sim = WorldWith(777.7, InOrbit.At(InOrbit.Round(planet, 0, 2000f), from, planet.Up(from) * 250f));
        var flight = sim.Flight;
        var v = flight.WorldVessels.Single(w => w.Id == 0);
        Assert.True(v.OnFormula);
        var frames = new BodyFrames();
        frames.Begin(flight.WorldTime, planet);
        var map = new SystemMap();
        map.Build(flight, frames, planet.Centre + new Vector3(0f, 0f, -1f) * 100_000f);
        var points = new List<Vector3>();
        Assert.True(map.PathOf(v, points), "a vessel going straight up has no path drawn");
        Assert.True(points.Count >= 2);
        var up = Vector3.Normalize(points[0] - planet.Centre);
        foreach (var p in points)
            Assert.True(Vector3.Cross(up, Vector3.Normalize(p - planet.Centre)).Length() < 1e-3f, "its line is straight out from the planet's centre");
        var top = points.Max(p => Vector3.Distance(p, planet.Centre)) - planet.SeaRadius;
        output.WriteLine($"{points.Count} points, up to {top:0} m over the sea");
        Assert.True(top > 3000f, "its line goes up");
    }

    [Fact]
    public void A_bodys_marker_says_how_far_its_surface_is()
    {
        // A body's marker is on its centre, but its label is the distance to its surface over its
        // datum, as the readout's height over a body: standing on the moon, the moon's is the lander's
        // height over the moon's datum (not the moon's radius, 2.5 km), and from the pad the planet's
        // is the pad's over the sea (not the planet's radius, 10 km).
        using var sim = WorldWith(2345.6, OnTheMoon.Lander(0, OnTheMoon.LandingSpot));
        var flight = sim.Flight;
        var frames = new BodyFrames();
        var candidates = new List<Markers.Candidate>();
        float Label(string name)
        {
            frames.Begin(flight.WorldTime, flight.Body);
            Markers.Gather(flight, new Settings(), frames, candidates);
            return Markers.LabelDistance(candidates.Single(c => c.Name == name), Views.Focus(flight.Controlled));
        }
        var (planet, moon) = (GroundBody.Planet, GroundBody.Moon);
        var fromPad = Label(planet.Name);
        var padHeight = planet.Altitude(Views.Focus(flight.Controlled));
        output.WriteLine($"from the pad the planet's label says {Markers.Distance(fromPad)} (its focus {padHeight:0.0} m over the sea)");
        Assert.Equal(padHeight, fromPad, 0.5f);

        var lander = flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: lander));
        sim.Step(default);
        Assert.Same(moon, flight.Body);
        var onMoon = Label(moon.Name);
        var landerHeight = moon.Altitude(Views.Focus(lander));
        output.WriteLine($"on the moon the moon's label says {Markers.Distance(onMoon)} (its focus {landerHeight:0.0} m over its datum)");
        Assert.Equal(landerHeight, onMoon, 0.5f);
        Assert.True(onMoon < 1000f, "the moon's label is its centre's distance");
    }

    [Fact]
    public void A_burn_makes_a_lap_again_and_its_floats_do_not()
    {
        // A stage under formula on its orbit with its engine off keeps the lap the map made for it,
        // frame after frame, though its place and velocity are floats moved on by its orbit each
        // step; one burning (its engine lit under formula, its orbit changing every step) has its
        // lap made again as its orbit changes.
        var planet = GroundBody.Planet;
        var camera = planet.Centre + new Vector3(0f, 0f, -1f) * 100_000f;
        int Remakes(VesselState state, out Vessel vessel)
        {
            using var sim = WorldWith(1111.1, state);
            vessel = sim.Flight.WorldVessels.Single(v => v.Id == 0);
            Assert.True(vessel.OnFormula);
            var frames = new BodyFrames();
            var map = new SystemMap();
            for (var i = 0; i < 120; i++)
            {
                sim.Step(default);
                frames.Begin(sim.Flight.WorldTime, planet);
                map.Build(sim.Flight, frames, camera);
            }
            return map.Remakes;
        }
        var engineOff = Remakes(InOrbit.Round(planet, 0, 2000f), out _);
        var burning = Remakes(InOrbit.Round(planet, 0, 2000f) with { Throttle = 1f }, out var burner);
        output.WriteLine($"over 120 frames: the stage with its engine off made {engineOff} time(s), the one burning {burning}");
        Assert.Equal(1, engineOff);
        Assert.True(burning > 20, $"the burning stage's lap was made only {burning} times in 120 frames");
    }

    [Fact]
    public void A_lap_behind_the_planet_is_drawn_only_where_it_is_seen()
    {
        // A stage on a round orbit of the planet, the camera 40 km out: from over the pole the whole
        // lap is in view; from its orbit's plane, its far half is behind the planet, so fewer of its
        // points are put on the screen, and it is cut where it goes behind the planet's limb.
        var planet = GroundBody.Planet;
        using var sim = WorldWith(1111.1, InOrbit.Round(planet, 0, 2000f));
        var flight = sim.Flight;
        var v = flight.WorldVessels.Single(w => w.Id == 0);
        var frames = new BodyFrames();
        frames.Begin(flight.WorldTime, planet);
        var map = new SystemMap();
        (int Points, int Runs) Laid(Vector3 camera)
        {
            map.Build(flight, frames, camera);
            map.Lay(Looking(camera, planet.Centre), camera, Screen);
            var runs = map.Runs.Where(r => r.Vessel == v.Id).ToList();
            return (runs.Sum(r => r.Count), runs.Count);
        }
        var overThePole = Laid(planet.Centre + Vector3.Normalize(planet.NorthPole) * 40_000f);
        var inThePlane = Laid(planet.Centre + Vector3.Normalize(Vector3.Cross(planet.NorthPole, v.CentreOfMass - planet.Centre)) * 40_000f);
        output.WriteLine($"over the pole: {overThePole.Points} points in {overThePole.Runs} run(s); in its plane: {inThePlane.Points} in {inThePlane.Runs}");
        Assert.True(overThePole.Points > 0 && inThePlane.Points > 0);
        Assert.True(inThePlane.Points < overThePole.Points, "the part behind the planet is drawn too");
        Assert.True(inThePlane.Runs >= 2, "it isn't cut where it goes behind the planet");
    }

    [Fact]
    public void Building_the_map_allocates_nothing()
    {
        // Once its lists have grown, a frame of the map (each body's path, the sun, every other
        // vessel's, laid on the screen) allocates nothing.
        using var sim = WorldWith(3456.7, InOrbit.Round(GroundBody.Moon, 0, 400f), InOrbit.Round(GroundBody.Planet, 1, 2000f));
        var flight = sim.Flight;
        var frames = new BodyFrames();
        var map = new SystemMap();
        var camera = GroundBody.Planet.Centre + new Vector3(0f, 0f, -1f) * 100_000f;
        var view = Looking(camera, GroundBody.Planet.Centre);
        void Frame()
        {
            frames.Begin(flight.WorldTime, flight.Body);
            map.Build(flight, frames, camera);
            map.Lay(view, camera, Screen);
        }
        Frame();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 60; i++)
            Frame();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{map.Runs.Count} runs, {map.Screen.Count} screen points: {allocated} bytes over 60 frames");
        Assert.True(map.Runs.Count > 0);
        Assert.Equal(0, allocated);
    }

    /// <summary>The map laid for a flight over Home from the farthest zoom, 1280 x 720.</summary>
    private static (Simulation Sim, SystemMap Map) LaidOverHome()
    {
        var sim = WorldWith(1234.5, InOrbit.Round(GroundBody.Planet, 0, 2000f));
        var flight = sim.Flight;
        var frames = new BodyFrames();
        frames.Begin(flight.WorldTime, flight.Body);
        var camera = ZoomedOut(flight.Body, new Vector3(0f, 3000f, 0f), Vector2.Zero, flight.WorldTime);
        var map = new SystemMap();
        map.Build(flight, frames, camera.Position);
        map.Lay(camera.ViewProjection(Screen), camera.Position, Screen);
        return (sim, map);
    }

    [Fact]
    public void Clicking_the_moons_orbit_line_targets_the_moon()
    {
        // On the middle of a piece of the moon's drawn path (and a few pixels off it) the click
        // is the moon's; the flown vessel's own body (the planet) never counts, even on its line.
        var (sim, map) = LaidOverHome();
        using var _ = sim;
        var moon = GroundBody.Moon;
        var runs = map.Runs.Where(r => r.Body == moon.Index && r.Count >= 2).ToList();
        Assert.NotEmpty(runs);
        var run = runs.OrderByDescending(r => r.Count).First();
        var (a, b) = (map.Screen[run.First + run.Count / 2 - 1], map.Screen[run.First + run.Count / 2]);
        var middle = new Vector2((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);
        Assert.Same(moon, map.BodyLineAt(middle, 8f));
        Assert.Same(moon, map.BodyLineAt(middle + new Vector2(3f, 3f), 8f));
        // The point the readout gives a script to click on the line is the line's.
        Assert.True(map.LinePoint(moon) is { } point && map.BodyLineAt(new Vector2(point.X, point.Y), 1f) == moon);
        Assert.Null(new SystemMap().LinePoint(moon));

        foreach (var planetRun in map.Runs.Where(r => r.Body == GroundBody.Planet.Index))
            for (var i = planetRun.First; i < planetRun.First + planetRun.Count; i++)
                Assert.NotSame(GroundBody.Planet, map.BodyLineAt(new Vector2(map.Screen[i].X, map.Screen[i].Y), 8f));
    }

    [Fact]
    public void Only_a_body_s_line_drawn_strongly_enough_takes_a_click_and_none_once_the_map_is_forgotten()
    {
        var (moon, planet) = (GroundBody.Moon, GroundBody.Planet);
        SystemMap.Run Line(int body, float alpha, int vessel = -1) => new(0, 2, (0, 0, 0), alpha, vessel, body);
        Assert.True(SystemMap.Clickable(Line(moon.Index, Scene.MapBodyPathAlpha), planet));
        Assert.True(SystemMap.Clickable(Line(moon.Index, Scene.MapLineClickAlpha), planet));
        Assert.False(SystemMap.Clickable(Line(moon.Index, Scene.MapLineClickAlpha * 0.5f), planet), "barely faded in");
        Assert.False(SystemMap.Clickable(Line(moon.Index, Scene.MapBodyPathAlpha), moon), "the flown vessel's own body");
        Assert.False(SystemMap.Clickable(Line(moon.Index, Scene.MapBodyPathAlpha, vessel: 3), planet), "a vessel's lap");
        Assert.False(SystemMap.Clickable(Line(-1, Scene.MapBodyPathAlpha), planet), "no body's");

        // Laid, then forgotten (the map off the screen): nothing to point at.
        var (sim, map) = LaidOverHome();
        using var _ = sim;
        Assert.NotNull(map.LinePoint(moon));
        var at = map.LinePoint(moon)!.Value;
        Assert.Same(moon, map.BodyLineAt(new Vector2(at.X, at.Y), 1f));
        map.Forget();
        Assert.Null(map.LinePoint(moon));
        Assert.Null(map.BodyLineAt(new Vector2(at.X, at.Y), 8f));
        Assert.Null(map.SunMark);
    }

    [Fact]
    public void A_sphere_hides_what_is_behind_it_and_nothing_in_front_or_beside()
    {
        // As a body's path is hidden where it passes behind a body (each point by SystemMap.Covers).
        var centre = new Vector3(0f, 0f, -1000f);
        const float radius = 100f;
        var nearest = (centre.Length() - radius) * (centre.Length() - radius);
        Assert.True(SystemMap.Covers(new Vector3(0f, 0f, -2000f), centre, radius, nearest), "straight behind");
        Assert.True(SystemMap.Covers(new Vector3(50f, 0f, -1500f), centre, radius, nearest), "behind, off its middle");
        Assert.False(SystemMap.Covers(new Vector3(0f, 0f, -500f), centre, radius, nearest), "in front");
        Assert.False(SystemMap.Covers(new Vector3(300f, 0f, -2000f), centre, radius, nearest), "beside it, past its edge");
        Assert.False(SystemMap.Covers(new Vector3(0f, 0f, 2000f), centre, radius, nearest), "behind the camera");
        // Millions of metres out, a point just past the limb is still told from one just inside it.
        var far = new Vector3(0f, 0f, -5e6f);
        var farNearest = (far.Length() - 2500f) * (far.Length() - 2500f);
        Assert.True(SystemMap.Covers(new Vector3(0f, 2400f, -1e7f), far, 2500f, farNearest));
        Assert.False(SystemMap.Covers(new Vector3(0f, 5200f, -1e7f), far, 2500f, farNearest));
    }

    [Fact]
    public void A_click_off_every_line_hits_nothing()
    {
        var (sim, map) = LaidOverHome();
        using var _ = sim;
        Assert.Null(map.BodyLineAt(new Vector2(-500f, -500f), 8f));
        Assert.Null(map.BodyLineAt(new Vector2(Screen.X * 4f, Screen.Y * 4f), 8f));
        var empty = new SystemMap();
        Assert.Null(empty.BodyLineAt(new Vector2(10f, 10f), 8f));
    }
}
