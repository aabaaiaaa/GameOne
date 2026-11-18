using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// Flights set up round the planet and the moon, shared by the tests of a body as a target, the
/// plane crossings, the steering at the moon and the system map: a free-play flight of given
/// vessels, the path built as the HUD builds it, and places in the moon's plane and on a transfer to it.
/// </summary>
internal static class MoonFlights
{
    private static GroundBody Moon => GroundBody.Moon;
    private static GroundBody Planet => GroundBody.Planet;

    internal static WorldState World(double time, params VesselState[] vessels) =>
        new() { Time = time, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    /// <summary>A free-play flight in a world (at <paramref name="time"/>) of <paramref name="vessels"/>, the first flown (the frame after the switch's link-up).</summary>
    internal static (Simulation Sim, Vessel Flown) Flying(double time, params VesselState[] vessels)
    {
        var sim = new Simulation(Mission.FreePlay, world: World(time, vessels));
        var flown = sim.Flight.WorldVessels.Single(v => v.Id == vessels[0].Id);
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Step(default);
        Assert.Same(flown, sim.Flight.Controlled);
        return (sim, flown);
    }

    /// <summary>The flown vessel's plan as the HUD makes it (round the body it is at, with its nodes).</summary>
    internal static ManoeuvrePlan PlanFor(Flight flight)
    {
        var vessel = flight.Controlled;
        var plan = new ManoeuvrePlan();
        plan.Update(vessel, vessel.Chain, flight.WorldTime);
        return plan;
    }

    /// <summary>The path built for the flown vessel, frame after frame until its window is worked out.</summary>
    internal static (Paths Paths, ManoeuvrePlan Plan) Built(Flight flight)
    {
        var plan = PlanFor(flight);
        var paths = new Paths();
        do
            paths.Build(flight, plan);
        while (paths.Working);
        return (paths, plan);
    }

    /// <summary>
    /// The moon's centre and velocity at world time <paramref name="t"/>, among the planet's stars
    /// in the frame that matches the planet's ground at <paramref name="now"/>, as the test reckons it.
    /// </summary>
    internal static (Vector3Double Centre, Vector3Double Velocity) MoonAmongStars(double now, double t)
    {
        var change = Bodies.Change(Moon, Planet, t);
        var centre = change.Point(Moon.Centre);
        var velocity = change.Velocity(Moon.Centre, default) + Vector3Double.Cross(Planet.Spin, centre - Planet.Centre);
        var turn = QuaternionDouble.FromAxisAngle(Planet.NorthPole, Planet.TurnRadians(t - now));
        return (Planet.Centre + turn.Rotate(centre - Planet.Centre), turn.Rotate(velocity));
    }

    /// <summary>The way round the moon's orbit goes (its plane's normal), among the planet's stars at <paramref name="now"/>.</summary>
    internal static Vector3Double MoonNormal(double now)
    {
        var (centre, velocity) = MoonAmongStars(now, now);
        return Vector3Double.Normalize(Vector3Double.Cross(centre - Planet.Centre, velocity));
    }

    /// <summary>
    /// A circle 4 km up round the planet in the moon's plane at world time <paramref name="now"/>,
    /// the place on it <paramref name="angle"/> radians round from under the moon, the way it goes,
    /// moving at <paramref name="share"/> of the circle's speed (over 1, the bottom of an orbit
    /// reaching out towards the moon's height): its centre of mass and velocity among the stars.
    /// </summary>
    internal static (Vector3 Position, Vector3 Velocity) InTheMoonsPlane(double now, double angle, double share)
    {
        var low = Planet.SeaRadius + 4000.0;
        var normal = MoonNormal(now);
        var towards = Vector3Double.Normalize(MoonAmongStars(now, now).Centre - Planet.Centre);
        var from = QuaternionDouble.FromAxisAngle(normal, angle).Rotate(towards);
        var prograde = Vector3Double.Cross(normal, from);
        return ((Planet.Centre + from * low).ToSingle(), (prograde * Math.Sqrt(Planet.Mu / low) * share).ToSingle());
    }

    /// <summary>
    /// The test's Hohmann transfer from the 4 km circle in the moon's plane out to the moon's
    /// distance, burnt at <paramref name="now"/> and aimed so that when it gets there the moon is
    /// <paramref name="lead"/> radians on from it (0.22 passes about 1 km over its ground).
    /// </summary>
    internal static (Vector3 Position, Vector3 Velocity) Transfer(double now, double lead)
    {
        double mu = Planet.Mu, low = Planet.SeaRadius + 4000.0, high = Scene.MoonDistance;
        var a = (low + high) / 2;
        var seconds = Math.PI * Math.Sqrt(a * a * a / mu);
        var normal = MoonNormal(now);
        var there = Vector3Double.Normalize(MoonAmongStars(now, now + seconds).Centre - Planet.Centre);
        var down = -QuaternionDouble.FromAxisAngle(normal, -lead).Rotate(there);
        var prograde = Vector3Double.Cross(normal, down);
        var speed = Math.Sqrt(mu * (2 / low - 1 / a));
        return ((Planet.Centre + down * low).ToSingle(), (prograde * speed).ToSingle());
    }

    /// <summary>A vessel (the satellite launcher's upper stage) with its centre of mass at <paramref name="centre"/>, moving <paramref name="stars"/> among the planet's stars.</summary>
    internal static VesselState OnPlanet(int id, (Vector3 Position, Vector3 Velocity) state) =>
        InOrbit.At(InOrbit.Round(Planet, id, 1000f), state.Position, state.Velocity);
}
