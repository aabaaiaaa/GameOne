using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The small views, kept in step with the flight as the game's <see cref="GameView"/> does each
/// frame (they need no window: only drawing them does). A vessel that comes away and can be flown
/// gets a view; swapping flies it, and the two vessels change places; closing a view leaves its
/// vessel flying as it was; and every vessel can be given one.
/// </summary>
public sealed class ViewsTests
{
    private const float Dt = 1f / 60f;

    private static readonly Pilot Climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;

    /// <summary>
    /// The satellite mission, just off the pad (slow enough for a bare satellite) with every
    /// separation done: three vessels that can be flown, the satellite flown, and the fairing's two
    /// halves as debris.
    /// </summary>
    private static (Simulation Sim, Views Views, ChaseCamera Camera) ThreeVessels()
    {
        var sim = new Simulation(Mission.DeploySatellite);
        var views = new Views();
        var camera = new ChaseCamera(null);
        views.Update(Dt, sim.Flight, camera); // the first frame, as the game's: the rocket alone
        sim.Run(Climb, f => f.Ground > 30f, maxSeconds: 30);
        sim.Step(new FlightInput(Stage: true)); // the booster
        views.Update(Dt, sim.Flight, camera);
        sim.Step(new FlightInput(Stage: true)); // the fairing
        views.Update(Dt, sim.Flight, camera);
        sim.Step(new FlightInput(Stage: true)); // and the satellite
        views.Update(Dt, sim.Flight, camera);
        return (sim, views, camera);
    }

    [Fact]
    public void Each_part_that_comes_away_and_can_be_flown_gets_a_view()
    {
        var (sim, views, _) = ThreeVessels();
        using (sim)
        {
            Assert.Equal("Satellite", sim.Flight.Controlled.Name);
            Assert.Equal(["Booster", "Upper stage"], views.Small.Select(v => v.Vessel.Name));
            Assert.All(views.Small, view => Assert.NotEqual(sim.Flight.Controlled, view.Vessel));
        }
    }

    [Fact]
    public void Clicking_a_view_flies_its_vessel_and_the_one_flown_takes_its_place()
    {
        var (sim, views, camera) = ThreeVessels();
        using (sim)
        {
            var flight = sim.Flight;
            var satellite = flight.Controlled;
            var booster = views.Small[0].Vessel;

            views.Swap(views.Small[0]);
            sim.Step(new FlightInput(SwitchTo: views.TakeSwap()));
            views.Update(Dt, flight, camera);

            Assert.Same(booster, flight.Controlled);
            Assert.Same(satellite, views.Small[0].Vessel); // in the booster's place in the strip
            Assert.Equal("Upper stage", views.Small[1].Vessel.Name);
            Assert.Null(views.TakeSwap()); // asked for once, done once
        }
    }

    [Fact]
    public void A_swap_hands_each_vessels_held_shadow_place_on_with_it()
    {
        // What each view held across the middle between two places goes with its vessel: the
        // vessel that was flown takes the main view's to its small view, and the one now flown
        // brings its small view's to the main view, so neither view's lights jump at the swap.
        var (sim, views, camera) = ThreeVessels();
        using (sim)
        {
            var flight = sim.Flight;
            views.MainShadowPlace = 7;
            views.Small[0].ShadowPlace = 3;
            views.Small[0].SunShared = true;

            views.Swap(views.Small[0]);
            sim.Step(new FlightInput(SwitchTo: views.TakeSwap()));
            views.Update(Dt, flight, camera);

            Assert.Equal(3, views.MainShadowPlace);
            Assert.Equal(7, views.Small[0].ShadowPlace);
            Assert.False(views.Small[0].SunShared); // the main view's area was its own
            Assert.Equal(-1, views.Small[1].ShadowPlace); // the other view's untouched
        }
    }

    [Fact]
    public void Tab_to_a_vessel_with_no_view_starts_the_main_views_shadow_place_afresh()
    {
        // With every view closed, Tab flies a vessel that had no view, so no place held for it to
        // bring: the main view starts afresh (-1, chosen anew), and the vessel that was flown takes
        // the main view's to its new small view, at the front of the strip.
        var (sim, views, camera) = ThreeVessels();
        using (sim)
        {
            var flight = sim.Flight;
            var satellite = flight.Controlled;
            while (views.Small.Count > 0)
                views.Close(views.Small[0], flight);
            views.MainShadowPlace = 7;

            sim.Step(new FlightInput(SwitchVessel: true));
            views.Update(Dt, flight, camera);

            Assert.NotSame(satellite, flight.Controlled);
            Assert.Equal(-1, views.MainShadowPlace);
            Assert.Same(satellite, Assert.Single(views.Small).Vessel);
            Assert.Equal(7, views.Small[0].ShadowPlace);
            Assert.False(views.Small[0].SunShared);
        }
    }

    [Fact]
    public void Tab_swaps_too()
    {
        var (sim, views, camera) = ThreeVessels();
        using (sim)
        {
            var flight = sim.Flight;
            var satellite = flight.Controlled;
            sim.Step(new FlightInput(SwitchVessel: true));
            views.Update(Dt, flight, camera);

            Assert.NotSame(satellite, flight.Controlled);
            Assert.Equal(2, views.Small.Count);
            Assert.Contains(views.Small, view => view.Vessel == satellite);
            Assert.DoesNotContain(views.Small, view => view.Vessel == flight.Controlled);
        }
    }

    [Fact]
    public void A_closed_view_leaves_its_vessel_carrying_on_and_it_can_be_added_back()
    {
        var (sim, views, camera) = ThreeVessels();
        using (sim)
        {
            var flight = sim.Flight;
            var upper = views.Small[1].Vessel;
            views.Close(views.Small[1], flight);
            Assert.Single(views.Small);

            var position = upper.Position;
            sim.Run((_, _) => default, _ => false, maxSeconds: 1); // (just off the pad: not long, or it would be down)
            views.Update(Dt, flight, camera);
            Assert.Equal(FlightState.Flying, upper.State);
            Assert.True(Vector3.Distance(upper.Position, position) > 5f, "the upper stage should still be flying, unseen");
            Assert.Single(views.Small); // closed stays closed

            Assert.True(views.CanOpen(upper));
            views.Open(upper, flight);
            Assert.Same(upper, views.Small[^1].Vessel);
            Assert.False(views.CanOpen(upper));            // one view each
            Assert.False(views.CanOpen(flight.Controlled)); // and the flown one has the main view
        }
    }

    [Fact]
    public void Debris_gets_a_view_only_when_asked_for_and_cannot_be_swapped_in()
    {
        var expendable = new Mission
        {
            Name = "TEST: EXPENDABLE BOOSTER",
            Briefing = "",
            Vehicle = Vehicle.TwoStage with { Stages = [Vehicle.TwoStage.Stages[0] with { Guided = false, LandingReserve = 0f }, Vehicle.TwoStage.Stages[1]] },
            TargetAltitude = Scene.AtmosphereHeight,
            Steps = [],
        };
        using var sim = new Simulation(expendable);
        var views = new Views();
        var camera = new ChaseCamera(null);
        views.Update(Dt, sim.Flight, camera); // the first frame, as the game's: the rocket alone
        sim.Run(Climb, f => f.Ground > 300f, maxSeconds: 30);
        sim.Step(new FlightInput(Stage: true));
        views.Update(Dt, sim.Flight, camera);
        Assert.Empty(views.Small);

        var debris = sim.Flight.Vessels.Single(v => v != sim.Flight.Controlled);
        views.Open(debris, sim.Flight);
        views.Swap(views.Small[0]);
        Assert.Null(views.TakeSwap());
    }

    [Fact]
    public void Starting_again_closes_every_view()
    {
        var (sim, views, camera) = ThreeVessels();
        using (sim)
        {
            sim.Flight.Restarted += views.Reset;
            sim.Step(new FlightInput(StartAgain: true));
            views.Update(Dt, sim.Flight, camera);
            Assert.Empty(views.Small);
        }
    }

    /// <summary>
    /// A REACH ORBIT rocket climbing, staged: its upper stage flown, its booster (guided) beside it,
    /// with the views following the flight as the game's do.
    /// </summary>
    private static (Simulation Sim, Views Views, ChaseCamera Camera, Vessel Upper, Vessel Booster) Staged()
    {
        var sim = new Simulation(Mission.ReachOrbit);
        var views = new Views();
        var camera = new ChaseCamera(null);
        views.Follow(sim.Flight);
        views.Update(Dt, sim.Flight, camera); // the first frame, as the game's: the rocket alone
        sim.Run(Climb, f => f.Ground > 30f, maxSeconds: 30);
        sim.Step(new FlightInput(Stage: true));
        views.Update(Dt, sim.Flight, camera);
        var upper = sim.Flight.Controlled;
        var booster = sim.Flight.Vessels.Single(v => v != upper);
        Assert.True(booster.Guided);
        Assert.Same(booster, Assert.Single(views.Small).Vessel);
        return (sim, views, camera, upper, booster);
    }

    [Fact]
    public void A_restored_world_and_a_carry_on_open_no_views()
    {
        var (sim, views, camera, upper, _) = Staged();
        using var _ = sim;
        var flight = sim.Flight;
        flight.Restore(flight.Capture());
        views.Update(Dt, flight, camera);
        Assert.Empty(views.Small); // behind the menu: the small views are opened again by hand

        sim.Step(new FlightInput(CarryOn: upper.Id));
        views.Update(Dt, flight, camera);
        sim.Step(default);
        views.Update(Dt, flight, camera);
        Assert.Equal(upper.Id, flight.Controlled.Id);
        Assert.Empty(views.Small);
    }

    [Fact]
    public void Carrying_on_another_flights_vessel_puts_nothing_in_the_strip()
    {
        var (sim, views, camera, upper, _) = Staged();
        using var _ = sim;
        var flight = sim.Flight;
        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand, From: LaunchSites.All.Single(s => s.Name == "Highcrest")));
        views.Update(Dt, flight, camera);
        Assert.Empty(views.Small); // a new flight: its rocket alone

        sim.Step(new FlightInput(CarryOn: upper.Id));
        views.Update(Dt, flight, camera);
        Assert.Same(upper, flight.Controlled);
        Assert.Empty(views.Small); // not the rocket flown before, swapped in

        // A rewind to that carry-on comes back with the strip empty too.
        sim.Step(new FlightInput(Rewind: true));
        views.Update(Dt, flight, camera);
        sim.Step(default);
        views.Update(Dt, flight, camera);
        Assert.Equal(upper.Id, flight.Controlled.Id);
        Assert.Empty(views.Small);
    }

    [Fact]
    public void Carrying_on_a_vessel_at_the_moon_keeps_the_strip_empty_through_its_link_up()
    {
        var lander = OnTheMoon.Lander(0, OnTheMoon.LandingSpot);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Time = 0.0, Vessels = [lander], NextVesselId = 1, Flights = 1 });
        var views = new Views();
        var camera = new ChaseCamera(null);
        var flight = sim.Flight;
        views.Follow(flight);
        views.Update(Dt, flight, camera);
        var moonLander = flight.WorldVessels.Single(v => v.Id == lander.Id);
        Assert.NotSame(moonLander, flight.Controlled);

        // The moon's physics isn't made, so the switch to the lander waits behind a link-up: the
        // rocket flown meanwhile or after it must not be swapped into the strip.
        sim.Step(new FlightInput(CarryOn: moonLander.Id));
        views.Update(Dt, flight, camera);
        Assert.Empty(views.Small);
        var linked = flight.LinkUp.On;
        var frames = 0;
        while (flight.LinkUp.On && frames++ < 600)
        {
            sim.Step(default);
            views.Update(Dt, flight, camera);
            linked |= flight.LinkUp.On;
            Assert.Empty(views.Small);
        }
        Assert.True(linked, "the carry-on went through a link-up");
        Assert.False(flight.LinkUp.On);
        Assert.Same(moonLander, flight.Controlled);
        Assert.Empty(views.Small);
    }

    [Fact]
    public void The_strips_buttons_go_below_the_instruments_however_tall_they_are()
    {
        var display = new Vector2(1280f, 720f);
        const float available = 1000f, gap = 8f, buttons = 28f, margin = 14f;

        // A short panel: the views their full size along the bottom, the buttons just over them.
        var (size, top, bottom) = Hud.StripPlace(display, 2, available, instrumentsBottom: 200f);
        Assert.Equal(MathF.Min(Scene.SmallViewMaxHeight, display.Y * 0.28f), size.Y, 3);
        Assert.Equal(display.Y - margin - size.Y, top, 3);
        Assert.Equal(top - gap, bottom, 3);
        Assert.Equal(size, Hud.StripPlace(display, 2, available, instrumentsBottom: null).ViewSize);

        // A tall one (warp, a target, the small views' lines): the buttons below its bottom, and the
        // views under them, smaller to fit, still along the bottom and clear of the buttons.
        foreach (var panel in (ReadOnlySpan<float>)[480f, 520f, 560f])
        {
            (size, top, bottom) = Hud.StripPlace(display, 3, available, panel);
            Assert.True(bottom - buttons >= panel + gap - 1e-3f, $"the buttons' top {bottom - buttons} under a panel to {panel}");
            Assert.True(top >= bottom + gap - 1e-3f, $"views at {top} under buttons to {bottom}");
            Assert.Equal(display.Y - margin, top + size.Y, 3);
            Assert.Equal(size.X, size.Y * 4f / 3f, 3);
        }

        // A panel reaching nearly to the bottom (a small window): the views at their least, the
        // buttons just over them (on the panel's last lines), never over the views.
        (size, top, bottom) = Hud.StripPlace(display, 3, available, 640f);
        Assert.Equal(60f, size.Y, 3);
        Assert.Equal(display.Y - margin - 60f, top, 3);
        Assert.Equal(top - gap, bottom, 3);

        // No views: the buttons at the bottom, or below the panel.
        Assert.Equal(display.Y - margin, Hud.StripPlace(display, 0, available, 300f).ButtonsBottom, 3);
    }
}
