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
    public void An_equatorial_orbit_crosses_a_tilted_plane_where_their_planes_meet()
    {
        var own = Equatorial();
        var tilt = 30f * MathF.PI / 180f;
        // The target's plane is this one turned about Up (a line in both): the nodes are on that line.
        var targetNormal = Vector3.Transform(own.Normal, Quaternion.CreateFromAxisAngle(Up, tilt));

        var (ascending, descending, inclination) = PlaneCrossings.Of(own, targetNormal)!.Value;

        Assert.Equal(30f, inclination * 180f / MathF.PI, 0.01f);
        var up = Vector3.Normalize(own.At(ascending) - Planet.Centre);
        var down = Vector3.Normalize(own.At(descending) - Planet.Centre);
        Assert.True(MathF.Abs(MathF.Abs(up.Y) - 1f) < 1e-3f, $"ascending node at {up}");
        Assert.True(Vector3.Distance(up, -down) < 1e-3f, "the nodes are opposite");
        // Going up through the target's plane there: along the target's normal.
        var ahead = Vector3.Cross(own.Normal, up);
        Assert.True(Vector3.Dot(ahead, targetNormal) > 0f, "the ascending node is where it crosses the way the target's normal points");
        Assert.True(Vector3.Dot(Vector3.Cross(own.Normal, down), targetNormal) < 0f);
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
        var state = MoonTargetTests.InTheMoonsPlane(5000.0, 1.0, 1.0);
        // The flown vessel and a target round the same planet, in different planes.
        // The target is carried half a radian on round the orbit (clear of the flown vessel), then tilted about where the flown one is.
        var radial = Vector3.Normalize(state.Position - Planet.Centre);
        var normal = Vector3.Normalize(Vector3.Cross(radial, state.Velocity));
        var turn = Quaternion.CreateFromAxisAngle(normal, 0.5f) * Quaternion.CreateFromAxisAngle(radial, 0.4f);
        var tilted = (Position: Planet.Centre + Vector3.Transform(state.Position - Planet.Centre, turn), Velocity: Vector3.Transform(state.Velocity, turn));
        var (same, _) = MoonTargetTests.Flying(5000.0, MoonTargetTests.OnPlanet(0, state), MoonTargetTests.OnPlanet(1, tilted));
        using (same)
        {
            var flight = same.Flight;
            same.Step(new FlightInput(SetTarget: NavTarget.OfVessel(flight.WorldVessels.Single(v => v.Id == 1))));
            var (paths, _) = MoonTargetTests.Built(flight);
            Assert.Contains(paths.Marks, m => m.Label.StartsWith("AN "));
            Assert.Contains(paths.Marks, m => m.Label.StartsWith("DN "));
            Assert.All(paths.Marks.Where(m => m.Label.StartsWith("AN ") || m.Label.StartsWith("DN ")), m => Assert.Equal(Paths.TargetColor, m.Color));
        }

        var (other, _) = MoonTargetTests.Flying(5000.0, MoonTargetTests.OnPlanet(0, state), InOrbit.Round(GroundBody.Moon, 1, 400f));
        using (other)
        {
            var flight = other.Flight;
            other.Step(new FlightInput(SetTarget: NavTarget.OfVessel(flight.WorldVessels.Single(v => v.Id == 1))));
            Assert.Same(GroundBody.Moon, flight.TargetAt!.Value.Body);
            var (paths, _) = MoonTargetTests.Built(flight);
            Assert.False(HasNodeMark(paths));
        }
    }
}
