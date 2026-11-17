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
        for (var i = 0; i < 120; i++)
        {
            var watching = GameView.Aim(camera, sim.Flight, Dt, Dt).Watching;
            Assert.Same(stage, watching);
            var seen = CameraSubject.Seen(sim.Flight, camera.Centre);
            Assert.NotNull(seen);
            var fed = seen.Value.Focus;
            // Where it truly is in the planet's frame, in double throughout.
            var truly = Bodies.Change(Moon, Home, sim.Flight.WorldTime).Point((Vector3Double)Views.Focus(stage));
            Assert.True(((Vector3Double)fed - truly).Length() < 0.01, $"frame {i}: fed {(Vector3Double)fed - truly} from where it is");
            if (lastFed is { } before)
                worst = Math.Max(worst, ((Vector3Double)(fed - before) - (truly - lastTrue)).Length());
            Assert.True(Vector3.Distance(camera.Focus, fed) <= Scene.CameraMaxLag + 0.01f, $"frame {i}: the camera keeps up with it");
            (lastFed, lastTrue) = (fed, truly);
            sim.Step(new FlightInput(Watching: watching, Camera: camera.Position, CameraBody: sim.Flight.Body));
        }
        output.WriteLine($"the worst frame's step off the stage's own motion: {worst * 1000:0.000} mm");
        Assert.True(worst < 0.01, $"stepped {worst * 1000:0.0} mm off the stage's own motion");
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

        // The camera taken off it before a second link-up is due lets that go: nothing is left over.
        sim.Step(default);
        Assert.False(sim.Flight.WatchLink.On);
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
}
