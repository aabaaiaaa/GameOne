using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A vessel's actions (<see cref="VesselActions"/>) and the menu a right click on the flown vessel
/// opens (<see cref="ActionMenu"/>): what each vessel offers (the lander its bay's doors and its
/// legs, a stack its bottom stage's legs, a satellite nothing), each said as what it will do now,
/// going into the flight as the legs key does; a click on the vessel on the screen, or beside it;
/// and the menu closing, nothing applied, as the flight changes under it.
/// </summary>
public sealed class VesselActionTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary><paramref name="vehicle"/>'s stages from <paramref name="first"/> up, its legs stowed, still 5 km over the landing zone, flown.</summary>
    private static (Simulation Sim, Vessel Vessel) Flown(Vehicle vehicle, int first = 0)
    {
        var zone = Scene.LandingZoneCentre;
        var state = Drops.Placed(zone, Planet.East(zone), 0f, vehicle, first: first, clearance: 5000f, mission: Mission.FreePlay, legsOut: false);
        var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        Assert.Same(vessel, sim.Flight.Controlled);
        return (sim, vessel);
    }

    private static List<VesselAction> Of(Vessel vessel)
    {
        var actions = new List<VesselAction>();
        VesselActions.Of(vessel, actions);
        return actions;
    }

    [Fact]
    public void A_lander_offers_its_bay_doors_and_its_legs()
    {
        var (sim, lander) = Flown(LanderTests.Alone);
        using var flying = sim;
        Assert.Equal(new[] { VesselAction.OpenBayDoors, VesselAction.DeployLegs }, Of(lander));
        Assert.Equal("Open bay doors", VesselActions.Label(VesselAction.OpenBayDoors));
        Assert.Equal("Deploy legs", VesselActions.Label(VesselAction.DeployLegs));
    }

    [Fact]
    public void The_booster_and_the_upper_stage_offer_their_legs()
    {
        // Each a stack (the whole launcher, then its upper stage with the lander on it): its bottom
        // stage's legs, as the legs key works them; the lander's bay isn't the stack's to open.
        foreach (var first in (ReadOnlySpan<int>)[0, 1])
        {
            var (sim, stack) = Flown(Vehicle.LanderLauncher, first);
            using var flying = sim;
            Assert.Equal(new[] { VesselAction.DeployLegs }, Of(stack));
        }
    }

    [Fact]
    public void A_satellite_offers_nothing_and_opens_no_menu()
    {
        var (sim, satellite) = Flown(Vehicle.ThreeStage, first: 2);
        using var flying = sim;
        Assert.Empty(Of(satellite));
        var menu = new ActionMenu();
        menu.Open(satellite, new Vector2(640f, 360f));
        Assert.False(menu.IsOpen);
        Assert.Null(menu.Vessel);
    }

    [Fact]
    public void Each_item_says_what_it_will_do_now()
    {
        var (sim, lander) = Flown(LanderTests.Alone);
        using var flying = sim;
        var menu = new ActionMenu();
        menu.Open(lander, new Vector2(640f, 360f));
        foreach (var action in menu.Items.ToArray())
            sim.Step(VesselActions.Into(default, action));
        menu.Keep(sim.Flight, menuUp: false, zoomedOut: false);
        Assert.True(lander.BayDoorsWanted);
        Assert.True(lander.LegsWanted);
        Assert.Equal(new[] { VesselAction.CloseBayDoors, VesselAction.StowLegs }, menu.Items);
        Assert.Equal("Close bay doors", VesselActions.Label(VesselAction.CloseBayDoors));
        Assert.Equal("Stow legs", VesselActions.Label(VesselAction.StowLegs));
    }

    [Fact]
    public void An_action_goes_through_FlightInput_as_the_legs_key_does()
    {
        Assert.Equal(new FlightInput(ToggleLegs: true), VesselActions.Into(default, VesselAction.DeployLegs));
        Assert.Equal(new FlightInput(ToggleLegs: true), VesselActions.Into(default, VesselAction.StowLegs));
        Assert.Equal(new FlightInput(ToggleBayDoors: true), VesselActions.Into(default, VesselAction.OpenBayDoors));
        Assert.Equal(new FlightInput(ToggleBayDoors: true), VesselActions.Into(default, VesselAction.CloseBayDoors));
        var held = new FlightInput(Throttle: 1f, ToggleProgradeHold: true);
        Assert.Equal(held, VesselActions.Into(held, null));
        Assert.Equal(held with { ToggleLegs = true }, VesselActions.Into(held, VesselAction.DeployLegs)); // the rest of the frame's input kept

        // Chosen on the menu and taken for the flight's input, it deploys the flown vessel's legs.
        var (sim, lander) = Flown(LanderTests.Alone);
        using var flying = sim;
        var menu = new ActionMenu();
        menu.Open(lander, new Vector2(640f, 360f));
        menu.Choose(VesselAction.DeployLegs);
        Assert.False(menu.IsOpen); // an item closes it
        menu.Keep(sim.Flight, menuUp: false, zoomedOut: false);
        sim.Step(VesselActions.Into(default, menu.TakeChosen()));
        Assert.True(lander.LegsWanted);
        Assert.Null(menu.TakeChosen()); // taken once
    }

    [Fact]
    public void The_legs_key_still_works_beside_the_menu()
    {
        var (sim, lander) = Flown(LanderTests.Alone);
        using var flying = sim;
        var menu = new ActionMenu();
        menu.Open(lander, new Vector2(640f, 360f));
        sim.Step(new FlightInput(ToggleLegs: true)); // G, with the menu open
        menu.Keep(sim.Flight, menuUp: false, zoomedOut: false);
        Assert.True(lander.LegsWanted);
        Assert.True(menu.IsOpen);
        Assert.Equal(new[] { VesselAction.OpenBayDoors, VesselAction.StowLegs }, menu.Items);
    }

    [Fact]
    public void A_click_on_the_flown_vessel_is_on_it_and_one_beside_it_is_not()
    {
        var (sim, lander) = Flown(LanderTests.Alone);
        using var flying = sim;
        var display = new Vector2(1280f, 720f);
        var camera = new ChaseCamera(null);
        camera.Update(1f / 60f, lander.CentreOfMass, null, lander.Body, 0);
        camera.Update(1f / 60f, lander.CentreOfMass, null, lander.Body, 0);
        var viewProjection = camera.ViewProjection(display);
        var (bottom, top) = lander.AxisOffsets(lander.Rotation);
        bool On(Vector2 pointer) => VesselActions.OnVessel(lander.CentreOfMass + bottom, lander.CentreOfMass + top, lander.Width, viewProjection, display, pointer);
        Assert.True(camera.ToScreen(lander.CentreOfMass, display, out var middle, out _));
        Assert.True(On(middle));
        Assert.False(On(middle + new Vector2(60f, 0f)));
        Assert.False(On(middle - new Vector2(60f, 0f)));

        // A vessel 5 km off, under a pixel wide: within the least reach it is, past it not.
        var ahead = camera.Position + camera.Forward * 5000f;
        var up = camera.Up * 1.2f;
        bool Far(Vector2 pointer) => VesselActions.OnVessel(ahead - up, ahead + up, 1.15f, viewProjection, display, pointer);
        Assert.True(camera.ToScreen(ahead, display, out var far, out _));
        Assert.True(Far(far + new Vector2(Scene.ActionMenuMinPixels - 2f, 0f)));
        Assert.False(Far(far + new Vector2(Scene.ActionMenuMinPixels + 4f, 0f)));
        // And one behind the camera is never clicked.
        var behind = camera.Position - camera.Forward * 50f;
        Assert.False(VesselActions.OnVessel(behind - up, behind + up, 1.15f, viewProjection, display, display / 2f));
    }

    [Fact]
    public void The_menu_closes_when_its_vessel_is_no_longer_the_one_flown_or_can_no_longer_act()
    {
        var at = new Vector2(640f, 360f);

        // Opened, and an item chosen on another opening, then the flight changes under it: closed,
        // and nothing given to the flight.
        void Closes(Simulation sim, Action change, bool menuUp = false, bool zoomedOut = false)
        {
            var (open, chosen) = (new ActionMenu(), new ActionMenu());
            open.Open(sim.Flight.Controlled, at);
            chosen.Open(sim.Flight.Controlled, at);
            Assert.True(open.IsOpen);
            chosen.Choose(chosen.Items[0]);
            change();
            open.Keep(sim.Flight, menuUp, zoomedOut);
            chosen.Keep(sim.Flight, menuUp, zoomedOut);
            Assert.False(open.IsOpen);
            Assert.Null(open.TakeChosen());
            if (!zoomedOut) // (zoomed out, the vessel is still flown: what was chosen for it still goes)
                Assert.Null(chosen.TakeChosen());
        }

        // Tab to another: the booster dropped from the launcher, then flown.
        {
            var (sim, _) = Flown(Vehicle.LanderLauncher);
            using var flying = sim;
            sim.Step(new FlightInput(Stage: true));
            var stack = sim.Flight.Controlled;
            Closes(sim, () =>
            {
                sim.Step(new FlightInput(SwitchVessel: true));
                Assert.NotSame(stack, sim.Flight.Controlled);
            });
        }

        // Its vessel crashed: the lander dropped onto a pad too fast.
        {
            using var sim = Drops.OnPad(1, 30f, LanderTests.Alone);
            var lander = sim.Flight.Controlled;
            Closes(sim, () =>
            {
                sim.RunToEnd(Nobody, maxSeconds: 10);
                Assert.Equal(FlightState.Crashed, lander.State);
            });
        }

        // A game menu up (Esc), and zoomed out (M).
        {
            var (sim, _) = Flown(LanderTests.Alone);
            using var flying = sim;
            Closes(sim, () => { }, menuUp: true);
            Closes(sim, () => { }, zoomedOut: true);
        }

        // The lander staged away from its upper stage: the stack flown goes on as the lander (the
        // same vessel, its stages changed), so what the menu offered (the upper stage's legs) was
        // another vessel's: it closes, and an item chosen on it is let go, not given to the lander.
        // One opened on the upper stage left behind, no longer flown, closes too.
        {
            var (sim, stack) = Flown(Vehicle.LanderLauncher, first: 1);
            using var flying = sim;
            Closes(sim, () =>
            {
                sim.Step(new FlightInput(Stage: true));
                sim.Run(Nobody, _ => false, maxSeconds: 0.5);
                var flown = sim.Flight.Controlled;
                output.WriteLine($"after staging, flown: {flown.Name} (the menu's {(ReferenceEquals(flown, stack) ? "own" : "another")})");
                Assert.Same(stack, flown);
                Assert.True(flown.HasBay); // the lander now
            });
            var upper = sim.Flight.WorldVessels.Single(v => v is { Vehicle.Name: "Lander launcher", Stage: 1, TopStage: 1 });
            var onUpper = new ActionMenu();
            onUpper.Open(upper, at);
            Assert.Equal(new[] { VesselAction.DeployLegs }, onUpper.Items); // its own, while open
            onUpper.Keep(sim.Flight, menuUp: false, zoomedOut: false);
            Assert.False(onUpper.IsOpen);
            // Opened on the lander now, it offers the lander's own.
            var onLander = new ActionMenu();
            onLander.Open(sim.Flight.Controlled, at);
            onLander.Keep(sim.Flight, menuUp: false, zoomedOut: false);
            Assert.Equal(new[] { VesselAction.OpenBayDoors, VesselAction.DeployLegs }, onLander.Items);
        }
    }

    [Fact]
    public void The_menu_opens_at_the_click_kept_on_the_screen()
    {
        var (display, size, margin) = (new Vector2(1280f, 720f), new Vector2(160f, 80f), 14f);
        Assert.Equal(new Vector2(640f, 360f), ActionMenu.Place(new Vector2(640f, 360f), size, display, margin)); // at the click
        Assert.Equal(new Vector2(1280f - 160f - 14f, 360f), ActionMenu.Place(new Vector2(1250f, 360f), size, display, margin)); // in from the right
        Assert.Equal(new Vector2(640f, 720f - 80f - 14f), ActionMenu.Place(new Vector2(640f, 700f), size, display, margin)); // up from the bottom
        Assert.Equal(new Vector2(1280f - 160f - 14f, 720f - 80f - 14f), ActionMenu.Place(new Vector2(1279f, 719f), size, display, margin)); // the corner
        Assert.Equal(new Vector2(14f, 14f), ActionMenu.Place(new Vector2(2f, 3f), size, display, margin)); // never past the top left's margin
        Assert.Equal(new Vector2(14f, 14f), ActionMenu.Place(new Vector2(100f, 50f), size, new Vector2(100f, 60f), margin)); // a screen too small for it: its top left kept
    }
}
