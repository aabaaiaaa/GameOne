using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The main camera on something other than the flown vessel (<see cref="CameraSubject"/>): another
/// vessel, at the flown one's body or another, a place or a body. What it is on is followed in the
/// flown vessel's body's frame, changed across in double; a vessel it is on is witnessed, as a small
/// view's is (in its body's physics, with a link-up if that isn't made); and one that is lost gives
/// the camera back to the flown vessel. (The camera needs no window: <see cref="ChaseCamera"/> without one.)
/// </summary>
public sealed class CameraSubjectTests(ITestOutputHelper output)
{
    private const float Dt = Simulation.FrameSeconds;
    private static GroundBody Moon => GroundBody.Moon;
    private static GroundBody Home => GroundBody.Planet;

    private static WorldState World(params VesselState[] vessels) =>
        new() { Time = 0.0, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    private static Vessel Get(Simulation sim, int id) => sim.Flight.WorldVessels.Single(v => v.Id == id);

    /// <summary>The upper stage on a circular orbit <paramref name="height"/> over the moon's datum, numbered <paramref name="id"/>, at world time 0.</summary>
    private static VesselState RoundTheMoon(int id, float height)
    {
        var (out_, across) = CrossingTests.Ways();
        var at = Moon.Centre + out_ * (Moon.SeaRadius + height);
        var stars = across * MathF.Sqrt(Moon.Mu / (Moon.SeaRadius + height));
        var velocity = Moon.OverGround(at, stars);
        return CrossingTests.Craft(id, Moon, at, velocity, stars);
    }

    [Fact]
    public void A_camera_on_another_vessel_keeps_it_in_its_bodys_physics()
    {
        // A satellite on the far side of the planet, loaded under formula (nothing near it is
        // witnessed); the camera put on it, so it is watched as a small view's vessel is.
        using var sim = new Simulation(Mission.FreePlay, world: World(UnwatchedTests.Satellite(1, 10000f, 0f, 240f)));
        var satellite = Get(sim, 1);
        sim.Step(default);
        Assert.True(satellite.OnFormula, "far from the pad and watched by nothing, it is under formula");

        for (var i = 0; i < 120; i++)
        {
            sim.Step(new FlightInput(Watching: satellite, Camera: Views.Focus(satellite), CameraBody: satellite.Body));
            Assert.False(satellite.OnFormula, $"frame {i}: the vessel the camera is on is in its body's physics");
        }

        // And let go once the camera is back on the flown vessel: under formula again.
        for (var i = 0; i < 60 && !satellite.OnFormula; i++)
            sim.Step(default);
        Assert.True(satellite.OnFormula);
    }

    [Fact]
    public void The_markers_distances_are_the_flown_vessels_wherever_the_camera_is()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var camera = new ChaseCamera(null);
        sim.Step(default);
        GameView.Aim(camera, sim.Flight, Dt, Dt);
        var onOwn = Markers.DistancesFrom(sim.Flight, camera);
        Assert.True(Vector3.Distance(onOwn, Views.Focus(sim.Flight.Controlled)) <= Scene.CameraMaxLag + 0.01f, "from the flown vessel, as the camera follows it");
        // On the moon, the moon's marker is still its distance from the flown vessel, not 0 m: measured
        // from where it was with the camera on the vessel, nowhere near the moon the camera looks at.
        camera.CentreOn(new CameraCentre(CentreKind.Body, Moon.Index));
        GameView.Aim(camera, sim.Flight, Dt, Dt);
        var onMoon = Markers.DistancesFrom(sim.Flight, camera);
        Assert.True(Vector3.Distance(onOwn, onMoon) <= Scene.CameraMaxLag + 0.01f, "from the same place, wherever the camera is");
        Assert.True(Vector3.Distance(onMoon, camera.Focus) > Moon.SeaRadius, "not from the moon the camera is on");
    }

    [Fact]
    public void The_map_centred_on_the_body_the_flown_vessel_is_at_looks_at_it()
    {
        // The planet right-clicked in the map from the pad (as from orbit round it): the camera
        // looks at its centre from over its pole, as on any body, with nothing in it not a number.
        foreach (var body in new[] { Home, Moon })
        {
            using var sim = new Simulation(Mission.FreePlay);
            var camera = new ChaseCamera(null);
            sim.Step(default);
            GameView.Aim(camera, sim.Flight, Dt, Dt);
            camera.ToggleZoomOut(Scene.PlanetRadius);
            camera.CentreOn(new CameraCentre(CentreKind.Body, body.Index));
            for (var i = 0; i < 120; i++)
            {
                GameView.Aim(camera, sim.Flight, Dt, Dt);
                sim.Step(default);
            }
            var view = camera.ViewProjection(new Vector2(1280f, 720f));
            Assert.True(float.IsFinite(camera.Position.X) && float.IsFinite(camera.Position.Y) && float.IsFinite(camera.Position.Z), $"{body.Name}: the camera at {camera.Position}");
            Assert.True(float.IsFinite(view.M11) && float.IsFinite(view.M22) && float.IsFinite(view.M43), $"{body.Name}: its view {view}");
            var centre = sim.Flight.Body == body ? body.Centre : Bodies.Change(body, sim.Flight.Body, sim.Flight.WorldTime).Point((Vector3Double)body.Centre).ToSingle();
            Assert.True(camera.ToScreen(centre, new Vector2(1280f, 720f), out var at, out _), $"{body.Name}: its centre on the screen");
            output.WriteLine($"{body.Name}: its centre at {at}, the camera {Vector3.Distance(camera.Position, centre):0} m from it");
        }
    }

    [Fact]
    public void The_compass_tape_keeps_the_facing_it_had_on_the_flown_vessel_while_the_camera_is_elsewhere()
    {
        // Every mark on the tape is the flown vessel's, so its facing is too: the camera's while on
        // it, kept from then while the camera is on something else (another vessel, a place, a body).
        Assert.Equal(1.2f, Hud.TapeFacing(CameraCentre.Own, 1.2f, kept: 0.4f));
        Assert.Equal(0.4f, Hud.TapeFacing(new CameraCentre(CentreKind.Vessel, 3), 1.2f, kept: 0.4f));
        Assert.Equal(0.4f, Hud.TapeFacing(new CameraCentre(CentreKind.Site, 0), 1.2f, kept: 0.4f));
        Assert.Equal(0.4f, Hud.TapeFacing(new CameraCentre(CentreKind.Body, Moon.Index), 1.2f, kept: 0.4f));
    }

    [Fact]
    public void A_click_on_a_body_s_disc_finds_that_body_and_off_it_or_behind_the_camera_none()
    {
        // From beyond the moon, on the line from the planet's centre through the moon's: the moon
        // fills the middle of the screen, the planet hidden behind it.
        var frames = new BodyFrames();
        frames.Begin(0.0, Home);
        var moon = frames.InView(Moon, Moon.Centre);
        var outward = Vector3.Normalize(moon - Home.Centre);
        var eye = moon + outward * 20_000f;
        var display = new Vector2(1280f, 720f);
        var up = Vector3.Normalize(Vector3.Cross(outward, Vector3.UnitX));
        Matrix4x4 Looking(Vector3 at) => Matrix4x4.CreateLookAt(eye, at, up)
            * ChaseCamera.Projection(float.DegreesToRadians(Scene.CameraVerticalFovDegrees), display.X / display.Y, ChaseCamera.Planes.Near, ChaseCamera.Planes.Far);

        Assert.Equal(new CameraCentre(CentreKind.Body, Moon.Index), Hud.BodyDisc(frames, eye, Looking(moon), display, display / 2f));
        Assert.Null(Hud.BodyDisc(frames, eye, Looking(moon), display, new Vector2(5f, 5f))); // off every disc
        Assert.Null(Hud.BodyDisc(frames, eye, Looking(eye + outward), display, display / 2f)); // both behind the camera

        // And from beyond the sun, looking at the moon through it: the sun's sphere is in front.
        var sun = frames.SunInView.ToSingle();
        var beyond = sun + Vector3.Normalize(sun - moon) * (Scene.SunRadius * 3f);
        var throughSun = Matrix4x4.CreateLookAt(beyond, moon, Vector3.Normalize(Vector3.Cross(sun - moon, Vector3.UnitX)))
            * ChaseCamera.Projection(float.DegreesToRadians(Scene.CameraVerticalFovDegrees), display.X / display.Y, ChaseCamera.Planes.Near, ChaseCamera.Planes.Far);
        Assert.Null(Hud.BodyDisc(frames, beyond, throughSun, display, display / 2f));
    }

    [Fact]
    public void A_right_click_finds_a_vessel_seen_at_another_body()
    {
        // Flying at the planet, the camera beside a stage round the moon: the main view is in the
        // planet's frame, so the stage is found by where it is changed into it.
        using var sim = new Simulation(Mission.FreePlay, world: World(RoundTheMoon(1, 3000f)));
        var stage = Get(sim, 1);
        sim.Step(default);
        Assert.Same(Home, sim.Flight.Body);
        var frames = new BodyFrames();
        frames.Begin(sim.Flight.WorldTime, Home);
        var seen = frames.InView(Moon, stage.CentreOfMass);
        var outward = Vector3.Normalize(seen - frames.InView(Moon, Moon.Centre));
        var eye = seen + outward * 60f; // looking down at it, the moon behind it
        var display = new Vector2(1280f, 720f);
        var viewProjection = Matrix4x4.CreateLookAt(eye, seen, Vector3.Normalize(Vector3.Cross(outward, Vector3.UnitX)))
            * ChaseCamera.Projection(float.DegreesToRadians(Scene.CameraVerticalFovDegrees), display.X / display.Y, ChaseCamera.Planes.Near, ChaseCamera.Planes.Far);

        Assert.Same(stage, Hud.VesselClicked(sim.Flight, eye, viewProjection, display, display / 2f, frames));
        Assert.Null(Hud.VesselClicked(sim.Flight, eye, viewProjection, display, new Vector2(10f, 10f), frames));
    }

    /// <summary>The most a float place near <paramref name="p"/> is off it for its rounding: a step of a float at its largest component, each way.</summary>
    private static double Rounding(Vector3Double p)
    {
        var largest = (float)Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)));
        return Math.Sqrt(3.0) * (MathF.BitIncrement(largest) - largest);
    }

    [Fact]
    public void A_camera_on_a_vessel_at_the_moon_follows_it_without_jitter()
    {
        // Flying at the planet (the rocket on the pad), the camera on a stage round the moon: the
        // main view is in the planet's frame, so what it follows is changed across, 40 km out.
        using var sim = new Simulation(Mission.FreePlay, world: World(RoundTheMoon(1, 3000f)));
        var stage = Get(sim, 1);
        var centre = new CameraCentre(CentreKind.Vessel, stage.Id);
        var camera = new ChaseCamera(null);
        camera.CentreOn(centre);
        sim.Step(default);
        Assert.Same(Home, sim.Flight.Body);
        Assert.Same(Moon, stage.Body);

        Vector3? lastFed = null;
        Vector3Double lastTrue = default;
        var worst = 0.0;
        // And the camera's own target and place, frame to frame: each frame's step against the one
        // before (a jitter is a step out of line with its neighbours), beside the stage's own.
        var (targets, places, truths) = (new List<Vector3>(), new List<Vector3>(), new List<Vector3Double>());
        for (var i = 0; i < 120; i++)
        {
            var watching = GameView.Aim(camera, sim.Flight, Dt, Dt).Watching;
            Assert.Same(stage, watching);
            var seen = CameraSubject.Seen(sim.Flight, camera.Centre);
            Assert.NotNull(seen);
            var fed = seen.Value.Focus;
            // Where it truly is in the planet's frame, in double throughout.
            var truly = Bodies.Change(Moon, Home, sim.Flight.WorldTime).Point((Vector3Double)Views.Focus(stage));
            Assert.True(((Vector3Double)fed - truly).Length() <= Rounding(truly), $"frame {i}: fed {(Vector3Double)fed - truly} from where it is");
            if (lastFed is { } before)
                worst = Math.Max(worst, ((Vector3Double)(fed - before) - (truly - lastTrue)).Length());
            Assert.True(Vector3.Distance(camera.Focus, fed) <= Scene.CameraMaxLag + 0.01f, $"frame {i}: the camera keeps up with it");
            (lastFed, lastTrue) = (fed, truly);
            targets.Add(camera.Target);
            places.Add(camera.Position);
            truths.Add(truly);
            sim.Step(new FlightInput(Watching: watching, Camera: camera.Position, CameraBody: sim.Flight.Body));
        }
        // Each place is a float, rounded once (Rounding); a step between two, twice; a kink, of three, four times.
        var rounding = Rounding(truths[^1]);
        output.WriteLine($"the worst frame's step off the stage's own motion: {worst * 1000:0.000} mm (a float's rounding there: {rounding * 1000:0.000} mm)");
        Assert.True(worst <= 2 * rounding, $"stepped {worst * 1000:0.0} mm off the stage's own motion");

        // Past the first few frames (the follower settling from its snap), each frame's step of the
        // camera's target and place is in line with its neighbours', as the stage's own is.
        double Kink(Func<int, Vector3Double> at, int i) => (at(i + 1) - at(i) * 2.0 + at(i - 1)).Length();
        var (worstTarget, worstPlace) = (0.0, 0.0);
        for (var i = 10; i < truths.Count - 1; i++)
        {
            var own = Kink(k => truths[k], i);
            worstTarget = Math.Max(worstTarget, Math.Abs(Kink(k => targets[k], i) - own));
            worstPlace = Math.Max(worstPlace, Math.Abs(Kink(k => places[k], i) - own));
        }
        output.WriteLine($"the camera's worst kink frame to frame, off the stage's own: target {worstTarget * 1000:0.000} mm, place {worstPlace * 1000:0.000} mm");
        Assert.True(worstTarget <= 4 * rounding, $"its target kinked {worstTarget * 1000:0.0} mm");
        Assert.True(worstPlace <= 4 * rounding, $"its place kinked {worstPlace * 1000:0.0} mm");
    }

    private static float Degrees(Vector3 a, Vector3 b) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)));

    [Fact]
    public void A_camera_on_a_vessel_crossing_into_the_moons_sphere_eases_round_to_the_moons_up()
    {
        // The upper stage at the planet coming at the moon's sphere at 100 m/s past its side (where
        // the planet's up and the moon's are far apart), to cross in about a second, watched while
        // the rocket on the pad is flown, so the main view stays in the planet's frame. One camera
        // is on it as the game's is; the other follows the same focus by the planet's up throughout,
        // to show where the view would have been.
        var (out_, side) = CrossingTests.Ways();
        const float Aside = 4_500f, Speed = 100f;
        var into = MathF.Sqrt(Moon.SphereRadius * Moon.SphereRadius - Aside * Aside);
        var (at, velocity) = CrossingTests.FromMoon(out_ * (into + 1f * Speed) + side * Aside, -out_ * Speed, Home);
        using var sim = new Simulation(Mission.FreePlay, world: World(CrossingTests.Craft(1, Home, at, velocity, velocity)));
        var stage = Get(sim, 1);
        var (crosses, stays) = (new ChaseCamera(null), new ChaseCamera(null));
        crosses.CentreOn(new CameraCentre(CentreKind.Vessel, stage.Id));
        sim.Step(default);

        void Frame()
        {
            var watching = GameView.Aim(crosses, sim.Flight, Dt, Dt).Watching;
            stays.Update(Dt, CameraSubject.Seen(sim.Flight, crosses.Centre)!.Value.Focus, Dt, Home, sim.Flight.WorldTime);
            sim.Step(new FlightInput(Watching: watching, Camera: crosses.Position, CameraBody: Home));
        }
        for (var i = 0; i < 600 && stage.Body == Home; i++)
            Frame();
        Assert.Same(Moon, stage.Body);
        Assert.Same(Home, sim.Flight.Body);
        var ups = Degrees(Home.Up(stays.Focus), Bodies.Change(Moon, Home, sim.Flight.WorldTime).Direction(Moon.Up(Views.Focus(stage))).ToSingle());
        Assert.True(ups > 60f, $"the two ups are {ups:0.0} degrees apart");

        // The frame it is first followed at the moon: where the planet's camera is, its view unturned.
        var (lastUp, lastPlace) = (crosses.WorldUp, crosses.Position);
        Frame();
        var off = Vector3.Distance(stays.Position, crosses.Position);
        var turned = Degrees(stays.Forward, crosses.Forward);
        Assert.True(off < 0.05f, $"{off * 1000:0.0} mm from where it would have been");
        Assert.True(turned < 0.1f, $"looking {turned:0.000} degrees away from where it would have");

        // Then its up eases round to the moon's, never more than 5 degrees in a frame, and its place
        // with it, until it is the moon's.
        var (worst, worstMove) = (Degrees(lastUp, crosses.WorldUp), 0f);
        var frames = (int)MathF.Ceiling(Scene.CameraBodyEaseSeconds / Dt) + 1;
        for (var i = 0; i < frames; i++)
        {
            (lastUp, lastPlace) = (crosses.WorldUp, crosses.Position);
            var focusBefore = crosses.Focus;
            Frame();
            worst = MathF.Max(worst, Degrees(lastUp, crosses.WorldUp));
            // How far its place moved besides following the stage: its turn round the stage at the view's distance.
            worstMove = MathF.Max(worstMove, Vector3.Distance(crosses.Position - lastPlace, crosses.Focus - focusBefore));
        }
        output.WriteLine($"its up turned {worst:0.00} degrees at most in a frame, its place {worstMove:0.00} m besides the stage's own");
        Assert.True(worst < 5f, $"its up turned {worst:0.00} degrees in a frame");
        Assert.True(worstMove < Scene.CameraDistance * MathF.Sin(float.DegreesToRadians(5f)), $"its place jumped {worstMove:0.00} m in a frame");
        var moonUp = Bodies.Change(Moon, Home, sim.Flight.WorldTime).Direction(Moon.Up(Views.Focus(stage))).ToSingle();
        var still = Degrees(crosses.WorldUp, moonUp);
        Assert.True(still < 0.1f, $"its up is still {still:0.000} degrees off the moon's");
    }

    [Fact]
    public void A_camera_on_a_vessel_that_is_lost_goes_back_to_the_flown_one()
    {
        // A stage falling fast onto the ground beside the pad: lost as it hits.
        var down = Home.Up(Scene.LandingZoneCentre);
        var falling = CrossingTests.Craft(1, Home, Scene.LandingZoneCentre + down * 40f, -down * 60f, down);
        using var sim = new Simulation(Mission.FreePlay, world: World(falling));
        var stage = Get(sim, 1);
        var centre = new CameraCentre(CentreKind.Vessel, stage.Id);
        var camera = new ChaseCamera(null);
        camera.CentreOn(centre);
        sim.Step(default);

        Assert.NotNull(CameraSubject.Resolve(sim.Flight, centre));
        Assert.Equal(centre, CameraSubject.Kept(sim.Flight, centre));
        for (var i = 0; i < 300 && stage.State != FlightState.Crashed; i++)
        {
            GameView.Aim(camera, sim.Flight, Dt, Dt);
            sim.Step(new FlightInput(Watching: stage, Camera: camera.Position, CameraBody: Home));
        }
        Assert.Equal(FlightState.Crashed, stage.State);

        Assert.Null(CameraSubject.Resolve(sim.Flight, centre));
        Assert.Equal(CameraCentre.Own, CameraSubject.Kept(sim.Flight, centre));
        Assert.Null(GameView.Aim(camera, sim.Flight, Dt, Dt).Watching);
        Assert.Equal(CameraCentre.Own, camera.Centre);
        Assert.True(Vector3.Distance(camera.Target, Views.Focus(sim.Flight.Controlled)) < 0.01f, "back on the flown vessel at once");

        // And one removed from the world (no such vessel) is lost as well; the flown one is its own.
        var gone = new CameraCentre(CentreKind.Vessel, 999);
        Assert.Null(CameraSubject.Resolve(sim.Flight, gone));
        Assert.Equal(CameraCentre.Own, CameraSubject.Kept(sim.Flight, gone));
        Assert.Equal(CameraCentre.Own, CameraSubject.Kept(sim.Flight, new CameraCentre(CentreKind.Vessel, sim.Flight.Controlled.Id)));
    }

    [Fact]
    public void Watching_a_vessel_whose_bodys_physics_isnt_made_shows_its_link_up()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var lander = Get(sim, 0);
        string? Reading() => Readout.FlightReadings(sim.Flight).GetValueOrDefault("Camera link");
        sim.Step(default);
        Assert.True(lander.OnFormula, "the lander loads under formula, far from the pad");
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        FlightInput Watch() => new(Watching: lander, Camera: Views.Focus(lander), CameraBody: Moon);

        // The frame the camera is put on it: its link-up begun, nothing made yet and the lander
        // left under formula (it is drawn with its static over the main view at the end of this frame).
        sim.Step(Watch());
        var link = sim.Flight.WatchLink;
        Assert.Same(lander, link.Vessel);
        Assert.True(link.Waiting);
        Assert.Equal(1f, link.Static);
        Assert.Equal("connecting", Reading());
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        Assert.True(lander.OnFormula);
        Assert.False(sim.Flight.LinkUp.On); // the main view's own is for a switch: none

        // The next: the moon's physics made behind that static, the lander brought into it, and kept there.
        sim.Step(Watch());
        var moon = sim.Flight.PhysicsAt(Moon);
        Assert.NotNull(moon);
        Assert.False(lander.OnFormula);
        Assert.Same(moon, lander.Physics);
        var frames = 2;
        while (link.On && frames < 600)
        {
            sim.Step(Watch());
            frames++;
            Assert.False(lander.OnFormula);
        }
        Assert.False(link.On);
        Assert.Null(Reading());
        Assert.InRange(frames * Simulation.FrameSeconds, Scene.LinkUpSeconds - 1e-4, Scene.LinkUpSeconds + Simulation.FrameSeconds + 1e-4);
    }

    [Fact]
    public void A_camera_taken_off_a_vessel_before_its_link_up_is_due_lets_it_go()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var lander = Get(sim, 0);
        sim.Step(default);
        Assert.True(lander.OnFormula);

        // Put on it: its link-up begun. Taken off it in the next frame, before that is due: let go at
        // once, nothing made for it, and the lander left under formula however long on.
        sim.Step(new FlightInput(Watching: lander, Camera: Views.Focus(lander), CameraBody: Moon));
        Assert.True(sim.Flight.WatchLink.Waiting);
        sim.Step(default);
        Assert.False(sim.Flight.WatchLink.On);
        Assert.Null(Readout.FlightReadings(sim.Flight).GetValueOrDefault("Camera link"));
        for (var i = 0; i < 60; i++)
            sim.Step(default);
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        Assert.True(lander.OnFormula);
    }

    [Fact]
    public void A_place_view_looks_at_the_place_from_above_and_aside()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var pad = new CameraCentre(CentreKind.Site, 0);
        var camera = new ChaseCamera(null);
        camera.CentreOn(pad);
        for (var i = 0; i < 3; i++)
            Assert.Null(GameView.Aim(camera, sim.Flight, Dt, Dt).Watching);

        var top = Sites.Top(0);
        Assert.True(Vector3.Distance(camera.Target, top) < 1f, $"looking at {camera.Target}, the pad's top at {top}");
        var offset = camera.Position - top;
        var up = Home.Up(top);
        var above = Vector3.Dot(offset, up);
        var aside = (offset - up * above).Length();
        output.WriteLine($"{above:0.0} m above the pad's top, {aside:0.0} m aside");
        Assert.InRange(above, Scene.PlaceViewHeight - 2f, Scene.PlaceViewHeight + 2f);
        Assert.InRange(aside, Scene.PlaceViewAside - 2f, Scene.PlaceViewAside + 2f);

        // Dragged and zoomed, it turns round the pad and keeps looking at it.
        camera.DragOrbit(new Vector2(200f, 0f));
        camera.Zoom(3f);
        GameView.Aim(camera, sim.Flight, Dt, Dt);
        Assert.True(Vector3.Distance(camera.Target, top) < 1f);
        Assert.True(Vector3.Distance(camera.Position, top) < offset.Length() - 1f, "zoomed in on it");
    }

    [Fact]
    public void Each_kind_of_subject_resolves_to_its_focus_in_its_own_body()
    {
        using var sim = new Simulation(Mission.LandOnTheMoon);
        var flight = sim.Flight;
        sim.Step(default);

        var own = CameraSubject.Resolve(flight, CameraCentre.Own);
        Assert.Equal((flight.Controlled.Body, Views.Focus(flight.Controlled), false), own);
        Assert.Equal((Moon, Moon.Centre, false), CameraSubject.Resolve(flight, new CameraCentre(CentreKind.Body, Moon.Index)));
        Assert.Equal((Sites.Body, Sites.Top(1), true), CameraSubject.Resolve(flight, new CameraCentre(CentreKind.Site, 1)));
        var spot = flight.Mission.PointsOfInterest[0];
        Assert.Equal((spot.Body, spot.Place, true), CameraSubject.Resolve(flight, new CameraCentre(CentreKind.Point, 0)));

        // Nothing there by those numbers: gone.
        Assert.Null(CameraSubject.Resolve(flight, new CameraCentre(CentreKind.Body, Bodies.All.Count)));
        Assert.Null(CameraSubject.Resolve(flight, new CameraCentre(CentreKind.Site, Sites.All.Length)));
        Assert.Null(CameraSubject.Resolve(flight, new CameraCentre(CentreKind.Point, flight.Mission.PointsOfInterest.Length)));
    }

    [Fact]
    public void A_centre_is_described_as_the_readout_says_it()
    {
        using var sim = new Simulation(Mission.LandOnTheMoon);
        var flight = sim.Flight;
        sim.Step(default);
        var spot = flight.Mission.PointsOfInterest[0];

        Assert.Equal("Own", CameraSubject.Describe(flight, CameraCentre.Own));
        Assert.Equal("Vessel 7", CameraSubject.Describe(flight, new CameraCentre(CentreKind.Vessel, 7)));
        Assert.Equal($"Body {Moon.Name}", CameraSubject.Describe(flight, new CameraCentre(CentreKind.Body, Moon.Index)));
        Assert.Equal($"Site {Sites.All[1].Name}", CameraSubject.Describe(flight, new CameraCentre(CentreKind.Site, 1)));
        Assert.Equal($"Point {spot.Name}", CameraSubject.Describe(flight, new CameraCentre(CentreKind.Point, 0)));

        // A number naming nothing is said by its kind alone.
        Assert.Equal("Body", CameraSubject.Describe(flight, new CameraCentre(CentreKind.Body, Bodies.All.Count)));
        Assert.Equal("Site", CameraSubject.Describe(flight, new CameraCentre(CentreKind.Site, -1)));
        Assert.Equal("Point", CameraSubject.Describe(flight, new CameraCentre(CentreKind.Point, flight.Mission.PointsOfInterest.Length)));
    }

    [Fact]
    public void A_marker_gives_the_centre_of_what_it_marks()
    {
        Markers.Candidate Marker(MarkerKind kind, NavTarget? target, GroundBody? of = null, int order = 0) =>
            new(Vector3.Zero, 1f, "x", kind, Markers.Look.Square, target, 0, 0f, false, false, new Markers.Rgb(0, 0, 0), of, order);

        Assert.Equal(new CameraCentre(CentreKind.Body, Moon.Index), Markers.CentreOf(Marker(MarkerKind.Body, NavTarget.OfBody(Moon), Moon)));
        // The body the flown vessel is at has no target, but is still a body.
        Assert.Equal(new CameraCentre(CentreKind.Body, Home.Index), Markers.CentreOf(Marker(MarkerKind.Body, null, Home)));
        Assert.Equal(new CameraCentre(CentreKind.Vessel, 12), Markers.CentreOf(Marker(MarkerKind.ThisFlight, new NavTarget(TargetKind.Vessel, 12))));
        Assert.Equal(new CameraCentre(CentreKind.Site, 3), Markers.CentreOf(Marker(MarkerKind.Site, new NavTarget(TargetKind.Site, 3))));
        Assert.Equal(new CameraCentre(CentreKind.Point, 2), Markers.CentreOf(Marker(MarkerKind.Site, null, order: 2)));
    }

    [Fact]
    public void Right_clicks_centre_by_the_rules()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(UnwatchedTests.Satellite(1, 10000f, 0f, 0f), UnwatchedTests.Satellite(2, 10000f, 0f, 90f)));
        var flown = sim.Flight.Controlled;
        var other = sim.Flight.WorldVessels.First(v => v != flown);
        var site = new CameraCentre(CentreKind.Site, 2);
        var body = new CameraCentre(CentreKind.Body, Moon.Index);
        var onOther = new CameraCentre(CentreKind.Vessel, other.Id);
        var elsewhere = new CameraCentre(CentreKind.Site, 1);

        // In the map: a marker or a vessel clicked is the centre, the own vessel gives Own, off them all nothing.
        Assert.Equal(site, Hud.RightClickCentre(true, CameraCentre.Own, site, null, flown));
        Assert.Equal(body, Hud.RightClickCentre(true, elsewhere, body, null, flown));
        Assert.Equal(onOther, Hud.RightClickCentre(true, CameraCentre.Own, null, other, flown));
        Assert.Equal(CameraCentre.Own, Hud.RightClickCentre(true, elsewhere, null, flown, flown));
        Assert.Equal(CameraCentre.Own, Hud.RightClickCentre(true, elsewhere, new CameraCentre(CentreKind.Vessel, flown.Id), null, flown));
        Assert.Null(Hud.RightClickCentre(true, elsewhere, null, null, flown));

        // In the chase view: another vessel is the centre; the own vessel recentres while the camera is elsewhere,
        // and gives nothing (the actions menu opens) while it is on it; a marker there is nothing.
        Assert.Equal(onOther, Hud.RightClickCentre(false, CameraCentre.Own, null, other, flown));
        Assert.Equal(onOther, Hud.RightClickCentre(false, elsewhere, null, other, flown));
        Assert.Equal(CameraCentre.Own, Hud.RightClickCentre(false, onOther, null, flown, flown));
        Assert.Equal(CameraCentre.Own, Hud.RightClickCentre(false, elsewhere, null, flown, flown));
        Assert.Null(Hud.RightClickCentre(false, CameraCentre.Own, null, flown, flown));
        Assert.Null(Hud.RightClickCentre(false, CameraCentre.Own, site, null, flown));
        Assert.Null(Hud.RightClickCentre(false, elsewhere, null, null, flown));
    }

    [Fact]
    public void M_from_a_centred_map_goes_by_the_rules()
    {
        var vessel = new CameraCentre(CentreKind.Vessel, 7);
        var site = new CameraCentre(CentreKind.Site, 3);
        var spot = new CameraCentre(CentreKind.Point, 0);
        var body = new CameraCentre(CentreKind.Body, 1);

        // From the map: the chase view of a vessel, a site or a point of interest; a body gives the own vessel's.
        Assert.Equal((vessel, false), GameView.Toggled(vessel, map: true));
        Assert.Equal((site, false), GameView.Toggled(site, map: true));
        Assert.Equal((spot, false), GameView.Toggled(spot, map: true));
        Assert.Equal((CameraCentre.Own, false), GameView.Toggled(body, map: true));
        Assert.Equal((CameraCentre.Own, false), GameView.Toggled(CameraCentre.Own, map: true));

        // From the chase view: the map with the same centre.
        foreach (var centre in new[] { CameraCentre.Own, vessel, site, spot })
            Assert.Equal((centre, true), GameView.Toggled(centre, map: false));
    }
}
