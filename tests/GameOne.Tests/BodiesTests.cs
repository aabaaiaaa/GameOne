using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The bodies as a list (<see cref="Bodies"/>) and the frames between them (<see cref="FrameChange"/>):
/// each body's sphere of influence worked out from the masses, a place, a velocity and a rotation
/// changed from one body's frame to another's and back exactly (to a millimetre, even far into a
/// long-played world), a point fixed on the moon carried over the planet's ground as the moon goes,
/// and the body a place is at chosen from the list: into a sphere at its radius, out only beyond it.
/// </summary>
public sealed class BodiesTests
{
    private const double Year = Scene.YearDays * (double)Scene.DaySeconds;
    private static GroundBody Planet => GroundBody.Planet;
    private static GroundBody Moon => GroundBody.Moon;

    /// <summary>A unit direction from <paramref name="random"/>, evenly over the sphere.</summary>
    private static Vector3Double Direction(Random random)
    {
        var z = random.NextDouble() * 2 - 1;
        var a = random.NextDouble() * Math.Tau;
        var s = Math.Sqrt(1 - z * z);
        return new Vector3Double(s * Math.Cos(a), s * Math.Sin(a), z);
    }

    [Fact]
    public void A_bodys_sphere_of_influence_is_worked_out_from_the_masses()
    {
        var expected = Scene.MoonDistance * Math.Pow(SolarSystem.MoonMu / (double)GroundBody.PlanetMu, 0.4);
        Assert.InRange(Moon.SphereRadius, expected - 1e-3, expected + 1e-3);
        Assert.InRange(Moon.SphereRadius, 6_300f, 6_500f);
        Assert.True(float.IsPositiveInfinity(Planet.SphereRadius));
        Assert.Same(Planet, Moon.Parent);
        Assert.Null(Planet.Parent);
    }

    [Theory, InlineData(0.0), InlineData(200 * 12 * 1200.0)]
    public void Frame_changes_round_trip_to_a_millimetre_even_in_a_long_played_world(double t)
    {
        var random = new Random(7);
        var there = Bodies.Change(Moon, Planet, t);
        var back = Bodies.Change(Planet, Moon, t);
        for (var i = 0; i < 1000; i++)
        {
            var p = (Vector3Double)Moon.Centre + Direction(random) * (random.NextDouble() * 8_000);
            var onPlanet = there.Point(p);
            Assert.True((back.Point(onPlanet) - p).Length() < 1e-3, $"a point came back {(back.Point(onPlanet) - p).Length()} m off");
            // The same place as SolarSystem's own (float) moon axes put it, to their rounding.
            var fromMoon = (Vector3Double)SolarSystem.FromMoon((p - Moon.Centre).ToSingle(), t);
            Assert.True((onPlanet - fromMoon).Length() < 0.02, $"{(onPlanet - fromMoon).Length()} m from FromMoon's");

            var q = Quaternion.Normalize(new Quaternion((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f,
                (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f));
            var turned = back.Rotation(there.Rotation(q));
            Assert.True(1 - Math.Abs(Quaternion.Dot(turned, q)) < 1e-6, $"a rotation came back off by {1 - Math.Abs(Quaternion.Dot(turned, q))}");
        }
        foreach (var body in Bodies.All)
            Assert.True(Bodies.Change(body, body, t).IsIdentity, $"{body.Name} to itself is no change");
        Assert.False(there.IsIdentity);
    }

    [Fact]
    public void A_point_fixed_on_the_moon_moves_over_the_planets_ground_as_the_moon_carries_it()
    {
        // Against the double frame change's own central difference: SolarSystem.FromMoon's float
        // answer rounds to about 4 mm at the moon's distance, 0.1 m/s over 0.04 s.
        const double h = 0.01;
        var random = new Random(11);
        foreach (var t in new[] { 0.0, 345.6, Year, 200 * Year })
        {
            var now = Bodies.Change(Moon, Planet, t);
            for (var i = 0; i < 100; i++)
            {
                var p = (Vector3Double)Moon.Centre + Direction(random) * (random.NextDouble() * 8_000);
                var moved = (Bodies.Change(Moon, Planet, t + h).Point(p) - Bodies.Change(Moon, Planet, t - h).Point(p)) / (2 * h);
                var velocity = now.Velocity(p, default);
                Assert.True((velocity - moved).Length() < 1e-3, $"at {t} s: {(velocity - moved).Length()} m/s off the motion");
            }
            // The moon's own spin over the planet's ground, against its orientation's change.
            var spin = now.Spin(default);
            var (before, after) = (SolarSystem.MoonOrientationAt(t - h), SolarSystem.MoonOrientationAt(t + h));
            var d = after * before.Conjugate;
            // d is the turn over 2h: its vector part sin(angle / 2) along the axis, so over h it is the angle's rate.
            var turn = new Vector3Double(d.X, d.Y, d.Z) * (Math.Sign(d.W) / h);
            Assert.True((spin - turn).Length() < 1e-7, $"at {t} s the moon's spin is {(spin - turn).Length()} rad/s off its turn");
        }
    }

    [Fact]
    public void A_velocity_changed_there_and_back_is_unchanged()
    {
        var random = new Random(5);
        foreach (var t in new[] { 0.0, 1199.0, Year, 100 * Year, 200 * Year })
        {
            var (there, back) = (Bodies.Change(Moon, Planet, t), Bodies.Change(Planet, Moon, t));
            for (var i = 0; i < 200; i++)
            {
                var p = (Vector3Double)Moon.Centre + Direction(random) * (random.NextDouble() * 8_000);
                var v = Direction(random) * (random.NextDouble() * 500);
                var returned = back.Velocity(there.Point(p), there.Velocity(p, v));
                Assert.True((returned - v).Length() < 1e-3, $"at {t} s a velocity came back {(returned - v).Length()} m/s off");
                var spin = Direction(random) * random.NextDouble();
                Assert.True((back.Spin(there.Spin(spin)) - spin).Length() < 1e-9);
                Assert.True((there.Inverse.Point(there.Point(p)) - p).Length() < 1e-3);
            }
        }
    }

    [Fact]
    public void A_vessel_enters_a_sphere_at_its_radius_and_leaves_only_beyond_it()
    {
        var random = new Random(3);
        var r = (double)Moon.SphereRadius;
        foreach (var t in new[] { 0.0, 600.0, Year, 200 * Year })
        {
            var centre = Bodies.CentreIn(Moon, Planet, t);
            for (var i = 0; i < 20; i++)
            {
                var d = Direction(random);
                Assert.Same(Moon, Bodies.Choose(Planet, (centre + d * (0.99 * r)).ToSingle(), t));
                Assert.Same(Planet, Bodies.Choose(Planet, (centre + d * (1.01 * r)).ToSingle(), t));
                Assert.Same(Moon, Bodies.Choose(Moon, ((Vector3Double)Moon.Centre + d * (1.05 * r)).ToSingle(), t));
                Assert.Same(Planet, Bodies.Choose(Moon, ((Vector3Double)Moon.Centre + d * (1.11 * r)).ToSingle(), t));
            }
        }
    }

    [Fact]
    public void Choosing_a_body_goes_by_the_list()
    {
        Assert.Equal([Planet, Moon], Bodies.All);
        Assert.False(Bodies.All is GroundBody[], "the game's list hands out the array behind it");
        Assert.True(Bodies.All is System.Collections.Generic.IList<GroundBody> { IsReadOnly: true });
        Assert.Same(Bodies.All, Bodies.All);
        for (var i = 0; i < Bodies.All.Count; i++)
            Assert.Equal(i, Bodies.All[i].Index);
        Assert.Same(Moon, Bodies.Named("Moon"));
        Assert.Same(Planet, Bodies.Named("Planet"));
        Assert.Equal("the ground", Planet.GroundName);
        Assert.Equal("the moon", Moon.GroundName);

        // A second moon, still across the planet from where the moon is at time 0.
        const double t = 0;
        var other = (Vector3Double)Planet.Centre * 2 - Bodies.CentreIn(Moon, Planet, t);
        var second = GroundBody.Circling("Second moon", "the second moon", 2, Planet, Scene.MoonDistance, Scene.MoonRadius,
            SolarSystem.MoonMu / 2, _ => new BodyPose(other, QuaternionDouble.Identity, default, default));
        IReadOnlyList<GroundBody> bodies = [Planet, Moon, second];
        Assert.InRange(second.SphereRadius, 4_000f, Moon.SphereRadius - 1_000f);

        var moonCentre = Bodies.CentreIn(Moon, Planet, t);
        var d = new Vector3Double(0, 0, 1);
        Assert.Same(second, Bodies.Choose(bodies, Planet, (other + d * (0.9 * second.SphereRadius)).ToSingle(), t));
        Assert.Same(Planet, Bodies.Choose(bodies, Planet, (other + d * (1.01 * second.SphereRadius)).ToSingle(), t));
        Assert.Same(Moon, Bodies.Choose(bodies, Planet, (moonCentre + d * (0.9 * Moon.SphereRadius)).ToSingle(), t));
        // Past its sphere's leaving edge, the second moon gives way to the planet; a place in the
        // moon's sphere, asked in the second moon's frame, goes out to the planet and into the moon.
        Assert.Same(Planet, Bodies.Choose(bodies, second, ((Vector3Double)second.Centre + d * (1.2 * second.SphereRadius)).ToSingle(), t));
        var inMoonsSphere = Bodies.Change(Planet, second, t).Point(moonCentre + d * (0.5 * Moon.SphereRadius));
        Assert.Same(Moon, Bodies.Choose(bodies, second, inMoonsSphere.ToSingle(), t));
        // The game's own list has no second moon to find.
        Assert.Same(Planet, Bodies.Choose(Planet, (other + d * (0.9 * second.SphereRadius)).ToSingle(), t));
    }

    [Fact]
    public void A_place_nearer_its_body_than_any_edge_keeps_its_body_and_the_bound_reaches_the_nearest_edge()
    {
        // With two moons, at several times: every place inside a body's clear distance is chosen as
        // at that body (so it needs no asking), and the bound is within a few metres of the nearest
        // edge, so it leaves little unskipped. Choosing with the places worked out ahead gives what
        // choosing alone does.
        var random = new Random(5);
        foreach (var t in new[] { 0.0, 600.0, Year, 200 * Year })
        {
            var other = (Vector3Double)Planet.Centre * 2 - Bodies.CentreIn(Moon, Planet, t);
            var second = GroundBody.Circling("Second moon", "the second moon", 2, Planet, Scene.MoonDistance, Scene.MoonRadius,
                SolarSystem.MoonMu / 2, _ => new BodyPose(other, QuaternionDouble.Identity, default, default));
            IReadOnlyList<GroundBody> bodies = [Planet, Moon, second];
            Span<Vector3Double> places = stackalloc Vector3Double[bodies.Count];
            Bodies.PlacesIn(bodies, t, places);
            for (var b = 0; b < bodies.Count; b++)
            {
                var body = bodies[b];
                var clear = Bodies.ClearWithin(bodies, places, b);
                var nearestEdge = body.Parent is null ? double.PositiveInfinity : body.SphereRadius * (double)Scene.SphereLeaveFactor;
                for (var c = 0; c < bodies.Count; c++)
                    if (bodies[c].Parent == body)
                        nearestEdge = Math.Min(nearestEdge, (places[c] - body.Centre).Length() - bodies[c].SphereRadius);
                Assert.InRange(nearestEdge - clear, 0.0, 2.0);
                for (var i = 0; i < 200; i++)
                {
                    var p = ((Vector3Double)body.Centre + Direction(random) * (clear * Math.Sqrt(random.NextDouble()))).ToSingle();
                    if (((Vector3Double)p - body.Centre).Length() >= clear)
                        continue;
                    Assert.Same(body, Bodies.Choose(bodies, places, body, p, t));
                    Assert.Same(body, Bodies.Choose(bodies, body, p, t));
                }
                // Just past it, towards the nearest child or out, the two ways of choosing agree too.
                for (var i = 0; i < 50; i++)
                {
                    var p = ((Vector3Double)body.Centre + Direction(random) * (clear + 10.0)).ToSingle();
                    Assert.Same(Bodies.Choose(bodies, body, p, t), Bodies.Choose(bodies, places, body, p, t));
                }
            }
        }
    }
}
