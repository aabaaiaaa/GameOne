using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>Where a target's plane crosses a path: the ascending and descending nodes and the angle between the planes, as marks on the path.</summary>
public sealed class PlaneCrossingTests
{
    private static readonly Vector3 Up = Vector3.UnitY, East = Vector3.UnitX;
    private static GroundBody Planet => GroundBody.Planet;

    private static Orbit Equatorial(float height = 4000f) =>
        new(Planet, Planet.Centre + Up * (Planet.SeaRadius + height), East * Planet.CircularSpeed(height));

    [Fact]
    public void An_orbit_crosses_a_plane_tilted_about_a_line_of_its_own_where_the_line_meets_it()
    {
        var own = Equatorial(); // in the plane of Up and East
        var tilt = 30f * MathF.PI / 180f;
        // The target's plane is this one turned about East (a line in both): the nodes are due east and west.
        var targetNormal = Vector3.Transform(own.Normal, Quaternion.CreateFromAxisAngle(East, tilt));

        var (ascending, descending, inclination) = PlaneCrossings.Of(own, targetNormal)!.Value;

        Assert.Equal(30f, inclination * 180f / MathF.PI, 0.01f);
        var ascendingAt = Vector3.Normalize(own.At(ascending) - Planet.Centre);
        var descendingAt = Vector3.Normalize(own.At(descending) - Planet.Centre);
        // Turning the normal by +30° about East tips the plane's far side down: it crosses it going up
        // (along the target's normal) at the west node, and down at the east one.
        Assert.True(Vector3.Distance(ascendingAt, -East) < 1e-3f, $"ascending node at {ascendingAt}");
        Assert.True(Vector3.Distance(descendingAt, East) < 1e-3f, $"descending node at {descendingAt}");
        Assert.True(Vector3.Dot(Vector3.Cross(own.Normal, ascendingAt), targetNormal) > 0f, "it crosses the way the target's normal points at the ascending node");
        Assert.True(Vector3.Dot(Vector3.Cross(own.Normal, descendingAt), targetNormal) < 0f);
    }

    [Fact]
    public void The_same_plane_has_no_crossings()
    {
        var own = Equatorial();
        Assert.Null(PlaneCrossings.Of(own, own.Normal));
        Assert.Null(PlaneCrossings.Of(own, -own.Normal));
        var nearly = Vector3.Transform(own.Normal, Quaternion.CreateFromAxisAngle(Up, 0.005f * MathF.PI / 180f));
        Assert.Null(PlaneCrossings.Of(own, nearly));
    }

    private static bool HasNodeMark(Paths paths) => paths.Marks.Any(m => m.Label.StartsWith("AN ") || m.Label.StartsWith("DN "));

    [Fact]
    public void A_target_in_a_tilted_orbit_at_the_same_body_gets_AN_and_DN_marks_and_one_at_another_body_none()
    {
        var state = MoonFlights.InTheMoonsPlane(5000.0, 1.0, 1.0);
        // The flown vessel and a target round the same planet, in different planes.
        // The target is carried half a radian on round the orbit (clear of the flown vessel), then tilted about where the flown one is.
        var radial = Vector3.Normalize(state.Position - Planet.Centre);
        var normal = Vector3.Normalize(Vector3.Cross(radial, state.Velocity));
        var turn = Quaternion.CreateFromAxisAngle(normal, 0.5f) * Quaternion.CreateFromAxisAngle(radial, 0.4f);
        var tilted = (Position: Planet.Centre + Vector3.Transform(state.Position - Planet.Centre, turn), Velocity: Vector3.Transform(state.Velocity, turn));
        var (same, _) = MoonFlights.Flying(5000.0, MoonFlights.OnPlanet(0, state), MoonFlights.OnPlanet(1, tilted));
        using (same)
        {
            var flight = same.Flight;
            same.Step(new FlightInput(SetTarget: NavTarget.OfVessel(flight.WorldVessels.Single(v => v.Id == 1))));
            var (paths, _) = MoonFlights.Built(flight);
            Assert.Contains(paths.Marks, m => m.Label.StartsWith("AN "));
            Assert.Contains(paths.Marks, m => m.Label.StartsWith("DN "));
            Assert.All(paths.Marks.Where(m => m.Label.StartsWith("AN ") || m.Label.StartsWith("DN ")), m => Assert.Equal(Paths.TargetColor, m.Color));
        }

        var (other, _) = MoonFlights.Flying(5000.0, MoonFlights.OnPlanet(0, state), InOrbit.Round(GroundBody.Moon, 1, 400f));
        using (other)
        {
            var flight = other.Flight;
            other.Step(new FlightInput(SetTarget: NavTarget.OfVessel(flight.WorldVessels.Single(v => v.Id == 1))));
            Assert.Same(GroundBody.Moon, flight.TargetAt!.Value.Body);
            var (paths, _) = MoonFlights.Built(flight);
            Assert.False(HasNodeMark(paths));
        }
    }

    /// <summary>How far <paramref name="p"/> is from the drawn <paramref name="line"/> (its nearest segment).</summary>
    private static float OffLine(IReadOnlyList<Vector3> line, Vector3 p)
    {
        var best = float.MaxValue;
        for (var i = 1; i < line.Count; i++)
        {
            var (a, b) = (line[i - 1], line[i]);
            var ab = b - a;
            var t = ab.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            best = MathF.Min(best, Vector3.Distance(p, a + ab * t));
        }
        return best;
    }

    /// <summary>A flown vessel and a target in a plane tilted <paramref name="tilt"/> radians from its own, half a radian on round the orbit (clear of it).</summary>
    private static (Simulation Sim, Orbit Own) FlyingBeside((Vector3 Position, Vector3 Velocity) state, float tilt)
    {
        var radial = Vector3.Normalize(state.Position - Planet.Centre);
        var normal = Vector3.Normalize(Vector3.Cross(radial, state.Velocity));
        var turn = Quaternion.CreateFromAxisAngle(normal, 0.5f) * Quaternion.CreateFromAxisAngle(radial, tilt);
        var other = (Position: Planet.Centre + Vector3.Transform(state.Position - Planet.Centre, turn), Velocity: Vector3.Transform(state.Velocity, turn));
        var (sim, _) = MoonFlights.Flying(5000.0, MoonFlights.OnPlanet(0, state), MoonFlights.OnPlanet(1, other));
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(sim.Flight.WorldVessels.Single(v => v.Id == 1))));
        return (sim, new Orbit(Planet, state.Position, state.Velocity));
    }

    /// <summary>Every node mark lies on the drawn path, over some tilts of the target's plane; returns how many nodes were off the path ahead (none marked) over them.</summary>
    private int NodesLieOnThePath((Vector3 Position, Vector3 Velocity) state)
    {
        var skipped = 0;
        foreach (var tilt in new[] { 0.4f, 1.2f, 2.0f, 2.8f, -0.7f, -1.5f, -2.4f })
        {
            var (sim, _) = FlyingBeside(state, tilt);
            using (sim)
            {
                var flight = sim.Flight;
                var (paths, plan) = MoonFlights.Built(flight);
                var leg = plan.Legs[0];
                var normal = flight.WorldVessels.Single(v => v.Id == 1) is var target
                    ? new Orbit(Planet, target.CentreOfMass, Planet.AmongStars(target.CentreOfMass, target.Velocity)).Normal
                    : default;
                if (PlaneCrossings.Of(leg.Orbit, normal) is var (an, dn, _))
                    skipped += (leg.Orbit.Ahead(an) ? 0 : 1) + (leg.Orbit.Ahead(dn) ? 0 : 1);
                var ahead = PlaneCrossings.Of(leg.Orbit, normal) is var (a2, d2, _) ? (leg.Orbit.Ahead(a2) ? 1 : 0) + (leg.Orbit.Ahead(d2) ? 1 : 0) : 0;
                var nodes = paths.Marks.Where(m => m.Label.StartsWith("AN ") || m.Label.StartsWith("DN ")).ToList();
                Assert.True(nodes.Count <= ahead, $"tilt {tilt}: {nodes.Count} node marks for {ahead} nodes on the path ahead");
                foreach (var m in nodes)
                    Assert.True(OffLine(paths.Line, m.At) < 2f, $"tilt {tilt}: {m.Label} is {OffLine(paths.Line, m.At):0.0} m off the drawn path");
            }
        }
        return skipped;
    }

    [Fact]
    public void A_node_behind_an_escaping_path_is_not_marked()
    {
        // Outbound on a hyperbola: the nodes behind it are on the branch coming in, which isn't drawn.
        var start = Planet.Centre + Up * (Planet.SeaRadius + 1500f);
        var skipped = NodesLieOnThePath((start, East * Planet.CircularSpeed(1500f) * 1.6f + Up * 20f));
        Assert.True(skipped > 0, "no node was behind it: the case checks nothing");
    }

    [Fact]
    public void A_node_beyond_where_a_path_comes_down_is_not_marked()
    {
        // A hop that lands a short way on: the rest of its orbit is under the ground.
        var start = Planet.Centre + Up * (Planet.SeaRadius + 800f);
        var skipped = NodesLieOnThePath((start, East * 120f + Up * 80f));
        Assert.True(skipped > 0, "no node was past the landing: the case checks nothing");
    }

    [Fact]
    public void Nodes_on_a_leg_that_starts_later_are_on_the_line_drawn_for_it()
    {
        // A transfer to the moon, the target a vessel round the moon: the marks on the moon's leg, which begins later.
        var (sim, _) = MoonFlights.Flying(5000.0, MoonFlights.OnPlanet(0, MoonFlights.Transfer(5000.0, 0.22)), InOrbit.Round(GroundBody.Moon, 1, 400f));
        using (sim)
        {
            var flight = sim.Flight;
            sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(flight.WorldVessels.Single(v => v.Id == 1))));
            var (paths, plan) = MoonFlights.Built(flight);
            Assert.Same(GroundBody.Moon, plan.Legs[1].Body);
            var nodes = paths.Marks.Where(m => m.Label.StartsWith("AN ") || m.Label.StartsWith("DN ")).ToList();
            Assert.NotEmpty(nodes);
            foreach (var m in nodes)
                Assert.True(OffLine(paths.Line, m.At) < 20f, $"{m.Label} is {OffLine(paths.Line, m.At):0.0} m off the drawn path");
        }
    }
}
