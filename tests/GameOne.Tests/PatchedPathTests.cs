using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The path patched at the spheres of influence (<see cref="Patched"/>): a transfer from a low
/// orbit cut at the moon's sphere and carried on round the moon, its two legs meeting exactly; a
/// path leaving the moon cut beyond its sphere; a path only grazing the sphere (under it for less
/// than a sample) still patched in and out; the patched path against a fine march of the same
/// pulls; a node after the crossing measured round the moon, prograde and not (radial and normal
/// too), in the plan and in the burn guide of a vessel on that path; and none of it allocating,
/// the plan with a node on the moon's leg included.
/// </summary>
public sealed class PatchedPathTests(ITestOutputHelper output)
{
    private static GroundBody Planet => GroundBody.Planet;
    private static GroundBody Moon => GroundBody.Moon;
    private const double Now = 5000.0;

    /// <summary>
    /// The moon's centre and velocity at world time <paramref name="t"/>, among the planet's stars
    /// in the frame that matches the planet's ground at <paramref name="now"/> (where an
    /// <see cref="Orbit"/> round the planet from then has its points).
    /// </summary>
    private static (Vector3Double Centre, Vector3Double Velocity) MoonAmongStars(double now, double t)
    {
        var change = Bodies.Change(Moon, Planet, t);
        var centre = change.Point(Moon.Centre);
        var velocity = change.Velocity(Moon.Centre, default) + Vector3Double.Cross(Planet.Spin, centre - Planet.Centre);
        var turn = QuaternionDouble.FromAxisAngle(Planet.NorthPole, Planet.TurnRadians(t - now));
        return (Planet.Centre + turn.Rotate(centre - Planet.Centre), turn.Rotate(velocity));
    }

    /// <summary>
    /// A circle 4 km up round the planet in the moon's plane, with the test's Hohmann burn out to
    /// the moon's distance made at <see cref="Now"/>: aimed so that when it gets out there the moon
    /// is <paramref name="lead"/> radians on from it, round the way they go.
    /// </summary>
    private static (Vector3 Position, Vector3 Velocity) Transfer(double lead)
    {
        double mu = Planet.Mu, low = Planet.SeaRadius + 4000.0, high = Scene.MoonDistance;
        var a = (low + high) / 2;
        var seconds = Math.PI * Math.Sqrt(a * a * a / mu);
        var (centre, velocity) = MoonAmongStars(Now, Now);
        var normal = Vector3Double.Normalize(Vector3Double.Cross(centre - Planet.Centre, velocity));
        var there = Vector3Double.Normalize(MoonAmongStars(Now, Now + seconds).Centre - Planet.Centre);
        var apoapsis = QuaternionDouble.FromAxisAngle(normal, -lead).Rotate(there);
        var down = -apoapsis;
        var position = Planet.Centre + down * low;
        var prograde = Vector3Double.Cross(normal, down);
        var circular = Math.Sqrt(mu / low);
        var burn = Math.Sqrt(mu * (2 / low - 1 / a)) - circular;
        return (position.ToSingle(), (prograde * (circular + burn)).ToSingle());
    }

    /// <summary>The lead that brings the transfer past the moon (on round the planet after) about 1 km over its ground.</summary>
    private const double PassLead = 0.22;

    /// <summary>
    /// A leg's state at world time <paramref name="t"/>, changed into <paramref name="to"/>'s frame
    /// at that moment, its velocity among that body's stars, as the test reckons it: from the
    /// leg's frame (its body's stars, matching its ground at <see cref="Now"/>) to its ground then,
    /// across by the bodies' frame change, and up among the other's stars there.
    /// </summary>
    private static (Vector3Double Position, Vector3Double Velocity) Changed(in PathLeg leg, double t, GroundBody to)
    {
        var (p, v) = leg.Orbit.StateAfter(t - leg.Start);
        var from = leg.Body;
        var back = QuaternionDouble.FromAxisAngle(from.NorthPole, from.TurnRadians(-(t - Now)));
        var ground = (Vector3Double)from.Centre + back.Rotate(p - (Vector3Double)from.Centre);
        var overGround = back.Rotate(v) - Vector3Double.Cross(from.Spin, ground - from.Centre);
        var change = Bodies.Change(from, to, t);
        var there = change.Point(ground);
        var velocity = change.Velocity(ground, overGround) + Vector3Double.Cross(to.Spin, there - to.Centre);
        return (there, velocity);
    }

    /// <summary>A leg's state at world time <paramref name="t"/> among its body's stars as they match its ground then.</summary>
    private static (Vector3Double Position, Vector3Double Velocity) AtOwn(in PathLeg leg, double t)
    {
        var (p, v) = leg.Orbit.StateAfter(t - leg.Start);
        var body = leg.Body;
        var back = QuaternionDouble.FromAxisAngle(body.NorthPole, body.TurnRadians(-(t - Now)));
        return ((Vector3Double)body.Centre + back.Rotate(p - (Vector3Double)body.Centre), back.Rotate(v));
    }

    [Fact]
    public void A_transfer_to_the_moon_is_patched_at_its_sphere()
    {
        var (position, velocity) = Transfer(PassLead);
        var legs = new List<PathLeg>();
        Patched.Legs(Planet, position, velocity, Now, legs);
        foreach (var leg in legs)
            output.WriteLine($"{leg.Body.Name}: {leg.Start - Now:0.000} to {leg.End - Now:0.000} s, periapsis {leg.Orbit.PeriapsisDistance:0.0} m");

        Assert.True(legs.Count >= 2, $"only {legs.Count} leg");
        Assert.Same(Planet, legs[0].Body);
        Assert.Same(Moon, legs[1].Body);
        Assert.Equal(legs[0].End, legs[1].Start);
        Assert.Equal(legs[0].End, Patched.Crossing(legs[0]));

        // Cut at the sphere: the planet leg's end that far from the moon's centre.
        var cut = legs[0].End;
        var end = legs[0].Orbit.PositionAfter(cut - legs[0].Start);
        var gap = ((Vector3Double)end - MoonAmongStars(Now, cut).Centre).Length();
        output.WriteLine($"cut {gap - Moon.SphereRadius:0.000} m from the sphere");
        Assert.InRange(gap, Moon.SphereRadius - 1, Moon.SphereRadius + 1);

        // The moon leg's start, changed back to the planet, is the planet leg's end.
        var (back, backVelocity) = Changed(legs[1], cut, Planet);
        var (expected, expectedVelocity) = AtOwn(legs[0], cut);
        var off = (back - expected).Length();
        var offVelocity = (backVelocity - expectedVelocity).Length();
        output.WriteLine($"changed back {off * 1000:0.00} mm and {offVelocity * 1000:0.000} mm/s off");
        Assert.True(off < 0.01, $"the moon leg starts {off * 1000:0.0} mm off the planet leg's end");
        Assert.True(offVelocity < 1e-3, $"the moon leg starts {offVelocity * 1000:0.00} mm/s off the planet leg's end");
    }

    [Fact]
    public void A_path_leaving_the_moon_is_patched_out_beyond_its_sphere()
    {
        // 2 km up over the moon's equator, east at half as fast again as escapes it.
        var r = Moon.SeaRadius + 2000f;
        var position = Moon.Centre + Vector3.UnitY * r;
        var velocity = Moon.East(position) * MathF.Sqrt(2 * Moon.Mu / r) * 1.5f;
        var legs = new List<PathLeg>();
        Patched.Legs(Moon, position, velocity, Now, legs);
        foreach (var leg in legs)
            output.WriteLine($"{leg.Body.Name}: {leg.Start - Now:0.000} to {leg.End - Now:0.000} s");

        Assert.True(legs.Count >= 2);
        Assert.Same(Moon, legs[0].Body);
        Assert.Same(Planet, legs[1].Body);
        Assert.Equal(legs[0].End, legs[1].Start);
        var edge = legs[0].Orbit.PositionAfter(legs[0].End - legs[0].Start);
        var distance = Vector3.Distance(edge, Moon.Centre);
        var leave = Moon.SphereRadius * Scene.SphereLeaveFactor;
        output.WriteLine($"cut {distance - leave:0.000} m from {leave:0} m");
        Assert.InRange(distance, leave - 1, leave + 1);

        var (back, backVelocity) = Changed(legs[1], legs[1].Start, Moon);
        var (start, startVelocity) = AtOwn(legs[0], legs[0].End);
        Assert.True((back - start).Length() < 0.01, $"{(back - start).Length() * 1000:0.0} mm apart across the edge");
        Assert.True((backVelocity - startVelocity).Length() < 1e-3, $"{(backVelocity - startVelocity).Length() * 1000:0.00} mm/s apart across the edge");
    }

    [Fact]
    public void A_path_grazing_the_sphere_is_patched_in_and_out()
    {
        // At the graze, 60 s on, 10 m inside the moon's sphere and passing it at 500 m/s across
        // the way to its centre: under the sphere about 1.4 s, less than a sample.
        const double Ahead = 60;
        var (centre, moonVelocity) = MoonAmongStars(Now, Now + Ahead);
        var outward = Vector3Double.Normalize(centre - Planet.Centre);
        var across = Vector3Double.Normalize(moonVelocity);
        var graze = centre + outward * (Moon.SphereRadius - 10.0);
        var passing = moonVelocity - across * 500.0;
        var atGraze = new Orbit(Planet, graze.ToSingle(), passing.ToSingle(), findEnd: false, from: Ahead);
        var (position, velocity) = atGraze.StateAfter(-Ahead);

        var legs = new List<PathLeg>();
        Patched.Legs(Planet, position, velocity, Now, legs);
        foreach (var leg in legs)
            output.WriteLine($"{leg.Body.Name}: {leg.Start - Now:0.000} to {leg.End - Now:0.000} s");

        // How long the planet's conic alone is under the sphere, at a millisecond.
        var under = 0.0;
        for (var s = Ahead - 3; s < Ahead + 3; s += 1e-3)
            if (((Vector3Double)legs[0].Orbit.PositionAfter(s) - MoonAmongStars(Now, Now + s).Centre).Length() < Moon.SphereRadius)
                under += 1e-3;
        output.WriteLine($"under the sphere {under:0.000} s on the planet's conic");
        Assert.InRange(under, 0.1, Scene.PatchSampleSeconds);

        Assert.True(legs.Count >= 3, $"only {legs.Count} legs");
        Assert.Same(Planet, legs[0].Body);
        Assert.Same(Moon, legs[1].Body);
        Assert.Same(Planet, legs[2].Body);
        Assert.InRange(legs[0].End - Now, Ahead - 2, Ahead);
    }

    [Fact]
    public void The_patched_path_agrees_with_a_fine_march_of_the_patched_pull()
    {
        var (position, velocity) = Transfer(PassLead);
        var legs = new List<PathLeg>();
        Patched.Legs(Planet, position, velocity, Now, legs);
        Assert.True(legs.Count >= 2 && legs[1].Body == Moon, "the transfer never reached the moon");
        var moonLeg = legs[1].Orbit;
        Assert.NotNull(moonLeg.NextPeriapsis);
        var periapsisAt = legs[1].Start + moonLeg.TimeTo(moonLeg.NextPeriapsis!.Value);

        // Verlet among the planet's stars at 0.01 s: outside the moon's sphere the planet's pull;
        // inside it (in at its radius, out past the leave factor) the moon's, with the moon's
        // centre's own fall round the planet, so it moves round the moon as the moon carries it.
        const double H = 0.01;
        double mu = Planet.Mu, moonMu = Moon.Mu;
        var planetCentre = (Vector3Double)Planet.Centre;
        var (p, v) = ((Vector3Double)position, (Vector3Double)velocity);
        var inside = false;
        var nearest = double.PositiveInfinity;
        Vector3Double Pull(Vector3Double at, double t)
        {
            var (c, _) = MoonAmongStars(Now, t);
            if (!inside)
            {
                var r = at - planetCentre;
                return r * (-mu / Math.Pow(r.Length(), 3));
            }
            var fromMoon = at - c;
            var moonFromPlanet = c - planetCentre;
            return fromMoon * (-moonMu / Math.Pow(fromMoon.Length(), 3)) + moonFromPlanet * (-mu / Math.Pow(moonFromPlanet.Length(), 3));
        }
        var time = Now;
        var pull = Pull(p, time);
        while (time < periapsisAt + 30)
        {
            p += v * H + pull * (H * H / 2);
            time += H;
            var gap = (p - MoonAmongStars(Now, time).Centre).Length();
            inside = inside ? gap < Moon.SphereRadius * (double)Scene.SphereLeaveFactor : gap < Moon.SphereRadius;
            if (inside)
                nearest = Math.Min(nearest, gap);
            var next = Pull(p, time);
            v += (pull + next) * (H / 2);
            pull = next;
        }
        output.WriteLine($"periapsis round the moon: patched {moonLeg.PeriapsisDistance:0.0} m, marched {nearest:0.0} m ({moonLeg.PeriapsisDistance - Moon.SeaRadius:0} m up)");
        Assert.InRange(moonLeg.PeriapsisDistance, nearest - 20, nearest + 20);
    }

    [Fact]
    public void A_node_after_the_crossing_is_measured_round_the_moon()
    {
        var (position, velocity) = Transfer(PassLead);
        var legs = new List<PathLeg>();
        Patched.Legs(Planet, position, velocity, Now, legs);
        var node = new ManoeuvreNode(legs[0].End + 30, Prograde: 10f);
        var plan = new ManoeuvrePlan();
        plan.Update(Planet, position, velocity, Now, [node], null);
        foreach (var leg in plan.Legs)
            output.WriteLine($"{leg.Body.Name}: {leg.Start - Now:0.000} to {leg.End - Now:0.000} s");

        Assert.True(plan.Legs.Count >= 3, $"only {plan.Legs.Count} legs");
        Assert.Equal(new[] { Planet, Moon, Moon }, plan.Legs.Take(3).Select(l => l.Body));
        Assert.Equal(1, plan.NodeLeg(0));
        Assert.Equal(node.Time, plan.Legs[1].End, 6);
        var (at, before) = plan.NodeAt(0)!.Value;
        Assert.True(Vector3.Distance(at, Moon.Centre) < Moon.SphereRadius * Scene.SphereLeaveFactor, "the node isn't round the moon");

        // The burn is along the way it goes round the moon, not round the planet.
        var (start, after) = plan.Legs[2].Orbit.StateAfter(0);
        Assert.True(Vector3.Distance(start, at) < 1e-3f);
        var burn = after - before;
        var expected = Vector3.Normalize(before) * 10f;
        output.WriteLine($"burn {burn}, expected {expected}");
        Assert.True(Vector3.Distance(burn, expected) < 0.01f, $"the burn is {Vector3.Distance(burn, expected):0.000} m/s off prograde round the moon");
    }

    [Fact]
    public void A_radial_and_normal_node_after_the_crossing_is_measured_round_the_moon()
    {
        // Radial out and normal (with a little prograde) are measured from the moon's centre and
        // the orbit's plane round it: round the planet they would point elsewhere.
        var (position, velocity) = Transfer(PassLead);
        var legs = new List<PathLeg>();
        Patched.Legs(Planet, position, velocity, Now, legs);
        var node = new ManoeuvreNode(legs[0].End + 30, Prograde: 2f, Radial: 6f, Normal: -8f);
        var plan = new ManoeuvrePlan();
        plan.Update(Planet, position, velocity, Now, [node], null);
        Assert.Equal(1, plan.NodeLeg(0));
        Assert.Same(Moon, plan.Legs[1].Body);
        var (at, before) = plan.NodeAt(0)!.Value;
        var (_, after) = plan.Legs[2].Orbit.StateAfter(0);
        var burn = after - before;

        var (prograde, radial, normal) = Manoeuvres.Frame(Moon, at, before);
        var expected = prograde * 2f + radial * 6f + normal * -8f;
        output.WriteLine($"burn {burn}, round the moon {expected}");
        Assert.True(Vector3.Distance(burn, expected) < 0.01f, $"the burn is {Vector3.Distance(burn, expected):0.000} m/s off its directions round the moon");
        // Radial out is from the moon's centre, square to the way it goes; normal is square to its plane round the moon.
        Assert.True(Vector3.Dot(radial, Moon.Up(at)) > 0.1f, "radial out isn't away from the moon");
        Assert.True(MathF.Abs(Vector3.Dot(normal, Vector3.Normalize(at - Moon.Centre))) < 1e-4f);
        Assert.True(MathF.Abs(Vector3.Dot(normal, Vector3.Normalize(before))) < 1e-4f);

        // Measured round the planet (the same place and motion, changed into its frame), the burn
        // would point well away from this: so a burn measured round the wrong body is caught.
        var change = Bodies.Change(Moon, Planet, Now);
        var atPlanet = change.Point((Vector3Double)at).ToSingle();
        var beforePlanet = Planet.AmongStars(atPlanet, change.Velocity((Vector3Double)at, (Vector3Double)Moon.OverGround(at, before)).ToSingle());
        var wrong = Bodies.Change(Planet, Moon, Now).Direction((Vector3Double)node.DeltaV(Planet, atPlanet, beforePlanet)).ToSingle();
        output.WriteLine($"round the planet it would be {wrong}, {Vector3.Distance(wrong, expected):0.0} m/s off");
        Assert.True(Vector3.Distance(wrong, expected) > 1f);
    }

    [Fact]
    public void The_burn_guide_measures_a_node_on_the_moons_leg_round_the_moon_and_allocates_nothing()
    {
        // A vessel on the transfer at the planet, its node on the moon's leg: the guide's burn is
        // the plan's, measured round the moon there and changed into the planet's frame now.
        var (position, velocity) = Transfer(PassLead);
        var state = InOrbit.At(InOrbit.UpperStage(0, 4000f, 1f), position, velocity);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Time = Now, Vessels = [state], NextVesselId = 1, Flights = 1 });
        var flight = sim.Flight;
        Assert.Equal(Now, flight.WorldTime);
        var v = flight.WorldVessels.Single(w => w.Id == 0);
        Assert.Same(Planet, v.Body);
        var stars = Planet.AmongStars(v.CentreOfMass, v.Velocity);
        var legs = new List<PathLeg>();
        Patched.Legs(Planet, v.CentreOfMass, stars, Now, legs);
        Assert.Same(Moon, legs[1].Body);
        var node = new ManoeuvreNode(legs[0].End + 30, Prograde: 2f, Radial: 6f, Normal: -8f);
        v.Replan([node], Now);

        var plan = new ManoeuvrePlan();
        plan.Update(Planet, v.CentreOfMass, stars, Now, [node], null);
        Assert.Same(Moon, plan.Legs[plan.NodeLeg(0)!.Value].Body);
        var (_, before) = plan.NodeAt(0)!.Value;
        var (_, after) = plan.Legs[plan.NodeLeg(0)!.Value + 1].Orbit.StateAfter(0);
        var expected = Bodies.Change(Moon, Planet, Now).Direction((Vector3Double)(after - before)).ToSingle();
        var guide = v.Guide(Now)!.Value;
        output.WriteLine($"the guide's burn {guide.Left}, the plan's {expected}, {guide.Seconds:0.0} s to go");
        Assert.True(Vector3.Distance(guide.Left, expected) < 0.01f, $"the guide is {Vector3.Distance(guide.Left, expected):0.000} m/s off the plan's burn round the moon");
        Assert.InRange(guide.Left.Length(), node.Size - 0.01f, node.Size + 0.01f);
        // Measured on the planet's orbit alone, as though it never crossed, it would be off by metres a second.
        var (p, u) = Orbit.OverGround(Planet, v.CentreOfMass, v.Velocity, findEnd: false).StateAfter(node.Time - Now);
        Assert.True(Vector3.Distance(node.DeltaV(Planet, p, u), expected) > 1f);

        // The guide is asked every frame: once warm it allocates nothing.
        for (var i = 0; i < 3; i++)
            _ = v.Guide(Now);
        Assert.Equal(0, Allocated(() =>
        {
            for (var i = 0; i < 10; i++)
                _ = v.Guide(Now + i);
        }));
    }

    [Fact]
    public void A_plan_with_a_node_on_the_moons_leg_allocates_nothing_once_warm()
    {
        // The HUD's plan is worked out again every frame, patched at the crossing and burned on the moon's leg.
        var (position, velocity) = Transfer(PassLead);
        var legs = new List<PathLeg>();
        Patched.Legs(Planet, position, velocity, Now, legs);
        ManoeuvreNode[] nodes = [new(legs[0].End + 30, Prograde: 2f, Radial: 6f, Normal: -8f), new(legs[0].End + 400, Prograde: -5f)];
        var plan = new ManoeuvrePlan();
        var left = new Vector3(1f, 2f, 3f);
        for (var i = 0; i < 3; i++)
        {
            plan.Update(Planet, position, velocity, Now, nodes, null);
            plan.Update(Planet, position, velocity, Now, nodes, left);
        }
        Assert.Equal(Moon, plan.Legs[plan.NodeLeg(0)!.Value].Body);

        Assert.Equal(0, Allocated(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                plan.Update(Planet, position, velocity, Now, nodes, null);
                plan.Update(Planet, position, velocity, Now, nodes, left);
                _ = plan.NodeAt(0);
                _ = plan.NodeLeg(1);
            }
        }));
    }

    /// <summary>
    /// The least bytes the current thread allocates in <paramref name="work"/> over a few tries. The
    /// thread's count moves by a whole allocation chunk (some 8 KB) when a collection another
    /// thread starts lands in the window, which happens with the suite's tests running together,
    /// though nothing was allocated; an allocation made on every run shows in every try.
    /// </summary>
    private static long Allocated(Action work)
    {
        var least = long.MaxValue;
        for (var tries = 0; tries < 5 && least != 0; tries++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            work();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return least;
    }

    [Fact]
    public void Patching_allocates_nothing_once_warm()
    {
        var (position, velocity) = Transfer(PassLead);
        var leaving = Moon.Centre + Vector3.UnitY * (Moon.SeaRadius + 2000f);
        var escaping = Moon.East(leaving) * MathF.Sqrt(2 * Moon.Mu / (Moon.SeaRadius + 2000f)) * 1.5f;
        var legs = new List<PathLeg>(Scene.PatchedLegs);
        for (var i = 0; i < 3; i++)
        {
            Patched.Legs(Planet, position, velocity, Now, legs);
            Patched.Legs(Moon, leaving, escaping, Now, legs);
        }

        Assert.Equal(0, Allocated(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                Patched.Legs(Planet, position, velocity, Now, legs);
                _ = Patched.Crossing(legs[0]);
                Patched.Legs(Moon, leaving, escaping, Now, legs);
            }
        }));
    }

    [Fact]
    public void A_path_straight_out_of_the_moons_sphere_topping_just_past_its_leaving_edge_is_patched_there()
    {
        // Straight out from the moon's sphere's radius, fast enough to top out 1 to 5% past the
        // radius it is left at: patched into the planet's leg where it goes up through that radius.
        var leave = Moon.SphereRadius * (double)Scene.SphereLeaveFactor;
        var up = Vector3.Normalize(new Vector3(0.4f, 0.1f, -0.9f));
        var at = Moon.Centre + up * Moon.SphereRadius;
        var r0 = (double)Moon.SphereRadius;
        foreach (var past in new[] { 1.01, 1.02, 1.05 })
        {
            var top = leave * past;
            var speed = (float)Math.Sqrt(2 * Moon.Mu * (1 / r0 - 1 / top));
            var legs = new List<PathLeg>();
            Patched.Legs(Moon, at, up * speed, 0.0, legs);
            var orbit = new Orbit(Moon, at, up * speed);
            var when = orbit.RisingTo(leave);
            output.WriteLine($"topping {past:0.00} times the leaving radius ({speed:0.00} m/s out): {legs.Count} legs, the first to {legs[0].End:0.000} s (up through it at {when:0.000} s, its top at {orbit.TopSeconds:0.000} s)");
            Assert.NotNull(when);
            Assert.True(legs.Count >= 2, "not patched out of the moon's sphere"); // (and back in, falling back from its top)
            Assert.Same(Moon, legs[0].Body);
            Assert.Same(GroundBody.Planet, legs[1].Body);
            Assert.InRange(legs[0].End, when!.Value - 1e-3, when.Value + 1e-3);
        }
    }
}
