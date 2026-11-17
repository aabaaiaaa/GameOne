using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// A mission's points of interest (<see cref="PointOfInterest"/>): where each is on its body's
/// ground, the markers a flight of that mission gathers for them (and no other mission's flight),
/// the markers' switch, how they show through a body, and the distance to one along the ground.
/// </summary>
public sealed class PointOfInterestTests
{
    private static GroundBody Moon => GroundBody.Moon;

    private static readonly PointOfInterest Second = new("Second point", GroundBody.Moon, 10f, 20f);

    private static readonly Mission WithPoints = new()
    {
        Name = "TEST POINTS",
        Briefing = "A mission with two points of interest.",
        Vehicle = Vehicle.ThreeStage,
        Steps = [new("Never done", _ => false)], // a mission's, so its flight is a run of it (free play's are of none)
        PointsOfInterest = [PointOfInterest.MoonLandingSpot, Second],
    };

    private static WorldState World(params VesselState[] vessels) =>
        new() { Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    private static List<Markers.Candidate> Gathered(Flight flight, Settings settings, out BodyFrames frames, List<Markers.Candidate>? into = null)
    {
        frames = new BodyFrames();
        frames.Begin(flight.WorldTime, flight.Body);
        var candidates = into ?? [];
        Markers.Gather(flight, settings, frames, candidates);
        return candidates;
    }

    [Fact]
    public void The_landing_spot_is_on_the_moons_ground_at_its_latitude_and_longitude()
    {
        var spot = PointOfInterest.MoonLandingSpot;
        Assert.Equal("Landing spot", spot.Name);
        Assert.Same(Moon, spot.Body);
        Assert.True(Vector3.Distance(OnTheMoon.LandingSpot, spot.Direction) < 1e-5f);
        Assert.Equal(Moon.Centre + spot.Direction * (Moon.SeaRadius + Moon.Height(spot.Direction)), spot.Place);
        Assert.InRange(Moon.Altitude(spot.Place) - Moon.Height(spot.Direction), -0.5f, 0.5f);
    }

    [Fact]
    public void A_missions_points_of_interest_are_among_its_markers_the_landing_spot_first()
    {
        using var sim = new Simulation(WithPoints, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var candidates = Gathered(sim.Flight, new Settings(), out var frames);
        var first = candidates.FindIndex(c => c.Name == "Landing spot");
        var second = candidates.FindIndex(c => c.Name == "Second point");
        Assert.True(first >= 0 && second > first, "the landing spot, then the second point");

        var spot = candidates[first];
        Assert.True(Vector3.Distance(spot.Position, frames.InView(Moon, PointOfInterest.MoonLandingSpot.Place)) < 1f);
        Assert.Equal(5, spot.Rank);
        Assert.True(spot.Arrow);
        Assert.Equal(NavTarget.OfPoint(0), spot.Target);
        Assert.Equal(0f, spot.Radius);
        Assert.Equal(MarkerKind.Site, spot.Kind);
        Assert.Equal(Markers.Look.Square, spot.Look);
    }

    [Fact]
    public void Another_missions_flight_has_no_points_of_interest()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        Assert.DoesNotContain(Gathered(sim.Flight, new Settings(), out _), c => c.Name == "Landing spot");
    }

    [Fact]
    public void With_markers_off_the_landing_spot_is_off_too()
    {
        using var sim = new Simulation(WithPoints, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var candidates = new List<Markers.Candidate>();
        Gathered(sim.Flight, new Settings { Markers = false }, out _, candidates);
        Assert.Empty(candidates);
        Assert.Contains(Gathered(sim.Flight, new Settings(), out _), c => c.Name == "Landing spot");
    }

    [Fact]
    public void From_the_planet_the_landing_spot_behind_the_moon_shows_faintly_through_it()
    {
        using var sim = new Simulation(WithPoints, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var spot = Gathered(sim.Flight, new Settings(), out var frames).Single(c => c.Name == "Landing spot");
        // A camera on the far side of the moon's middle from the spot: the moon is in the way.
        var middle = frames.InView(Moon, Moon.Centre);
        var camera = middle + Vector3.Normalize(middle - spot.Position) * (Moon.SeaRadius + 500f);
        Assert.True(frames.Behind(camera, spot.Position, spot.Of));
        Assert.Equal(0.45f, Markers.Through(spot, isTarget: false, frames, camera));
    }

    [Fact]
    public void A_point_of_interest_is_measured_along_its_bodys_ground()
    {
        var spot = PointOfInterest.MoonLandingSpot;
        // 100 m on from the spot along the moon's ground, towards its north.
        var axis = Vector3.Normalize(Vector3.Cross(spot.Direction, Vector3.UnitY));
        var direction = Vector3.Transform(spot.Direction, Quaternion.CreateFromAxisAngle(axis, 100f / Moon.Radius));
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, direction)));
        var lander = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        Assert.InRange(spot.DistanceFrom(lander, sim.Flight.WorldTime), 99f, 101f);
    }

    [Fact]
    public void From_another_body_it_is_the_straight_line_across_the_bodies()
    {
        // A vessel on the launch pad at the planet: far from the moon, not along any ground.
        using var sim = new Simulation(Mission.FreePlay);
        var vessel = sim.Flight.Controlled;
        var now = sim.Flight.WorldTime;
        var moonCentre = Bodies.Change(Moon, vessel.Body, now).Point((Vector3Double)Moon.Centre).ToSingle();
        var toCentre = Vector3.Distance(vessel.Position, moonCentre);
        var distance = PointOfInterest.MoonLandingSpot.DistanceFrom(vessel, now);
        Assert.True(toCentre > 3f * Moon.Radius, "the moon is well off");
        Assert.InRange(distance, toCentre - 1.1f * Moon.Radius, toCentre + 1.1f * Moon.Radius);
    }

    [Fact]
    public void A_missions_points_of_interest_have_names_of_their_own()
    {
        Assert.Throws<ArgumentException>(() => new Mission
        {
            Name = "TWICE", Briefing = "", Vehicle = Vehicle.ThreeStage, Steps = [],
            PointsOfInterest = [PointOfInterest.MoonLandingSpot, PointOfInterest.MoonLandingSpot with { Latitude = 1f }],
        });
    }
    /// <summary>
    /// A flight of <see cref="WithPoints"/> flying its new rocket at the planet, with a lander
    /// landed on the moon in its world (not switched to: a switch carries the flight on in the
    /// lander's own mission, free play).
    /// </summary>
    private static Simulation FlyingFromThePlanet()
    {
        var sim = new Simulation(WithPoints, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        Assert.Same(WithPoints, sim.Flight.Mission);
        Assert.NotEqual(0, sim.Flight.Controlled.Id);
        return sim;
    }

    [Fact]
    public void The_landing_spot_can_be_targeted_and_its_distance_is_read()
    {
        using var sim = FlyingFromThePlanet();
        var flight = sim.Flight;
        var spot = Gathered(flight, new Settings(), out _).Single(c => c.Name == "Landing spot");
        Assert.Equal(NavTarget.OfPoint(0), spot.Target);

        sim.Step(new FlightInput(SetTarget: spot.Target));
        Assert.Equal(NavTarget.OfPoint(0), flight.Target);
        var at = flight.TargetAt!.Value;
        Assert.Same(Moon, at.Body);
        Assert.False(at.Flying);
        Assert.Equal(PointOfInterest.MoonLandingSpot.Place, at.Own);
        // Across the bodies, from the vessel's centre of mass (the distance is to that, the point's from its origin).
        var vessel = flight.Controlled;
        var spotInView = Bodies.Change(Moon, vessel.Body, flight.WorldTime).Point((Vector3Double)PointOfInterest.MoonLandingSpot.Place).ToSingle();
        Assert.InRange(flight.DistanceToTarget!.Value, Vector3.Distance(vessel.CentreOfMass, spotInView) - 1f, Vector3.Distance(vessel.CentreOfMass, spotInView) + 1f);

        // The second point is a target too, by its place in the mission.
        sim.Step(new FlightInput(SetTarget: NavTarget.OfPoint(1)));
        Assert.Equal(NavTarget.OfPoint(1), flight.Target);
        Assert.Equal(Second.Place, flight.TargetAt!.Value.Own);
    }

    [Fact]
    public void A_point_past_the_sites_is_placed_on_its_own_body_not_a_sites_ship()
    {
        var points = Enumerable.Range(0, Sites.All.Length + 2).Select(i => new PointOfInterest($"Point {i}", GroundBody.Moon, i % 80, i * 3f)).ToArray();
        var many = new Mission { Name = "TEST MANY", Briefing = "", Vehicle = Vehicle.ThreeStage, Steps = [new("Never done", _ => false)], PointsOfInterest = points };
        using var sim = new Simulation(many, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        foreach (var i in new[] { 2, Sites.All.Length + 1 }) // one that is a drone ship's number, and one past every site's
        {
            sim.Step(new FlightInput(SetTarget: NavTarget.OfPoint(i)));
            Assert.Equal(NavTarget.OfPoint(i), sim.Flight.Target);
            var at = sim.Flight.TargetAt!.Value;
            Assert.Same(Moon, at.Body);
            Assert.Equal(points[i].Place, at.Own);
        }
    }

    [Fact]
    public void A_point_target_of_another_mission_is_not_found()
    {
        using var sim = FlyingFromThePlanet();
        var flight = sim.Flight;
        sim.Step(new FlightInput(SetTarget: NavTarget.OfPoint(2)));
        Assert.Null(flight.Target); // WithPoints has two
        sim.Step(new FlightInput(SetTarget: NavTarget.OfPoint(1)));
        Assert.NotNull(flight.Target);

        // The same target in a flight of a mission with no points of interest: it goes with them.
        using var other = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        other.Step(new FlightInput(SetTarget: NavTarget.OfPoint(0)));
        Assert.Null(other.Flight.Target);
    }
}
