using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A body as a target, and every leg of a path drawn round its own body: the moon targeted as a
/// vessel is (its centre, in the view's frame, flying), its marker among the others (and the
/// readout's), the speed in the Target frame relative to its centre and the holds following that;
/// a vessel at another body located and measured across the bodies; from a low orbit the moon's
/// window a stretch of the orbit, whose best burn takes the path into its sphere; on course, the
/// line bending round the moon where the path comes to it, with no gap at the crossing; the
/// closest approach the moon leg's periapsis (or, with no encounter, the closest the two orbits
/// come); and an orbit round the moon a closed loop round it, flown there or seen from the planet.
/// </summary>
public sealed class MoonTargetTests(ITestOutputHelper output)
{
    private static GroundBody Moon => GroundBody.Moon;
    private static GroundBody Planet => GroundBody.Planet;
    private const double Start = 5000.0;

    private static WorldState World(double time, params VesselState[] vessels) =>
        new() { Time = time, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    /// <summary>A free-play flight in a world (at <paramref name="time"/>) of <paramref name="vessels"/>, the first flown (the frame after the switch's link-up).</summary>
    private static (Simulation Sim, Vessel Flown) Flying(double time, params VesselState[] vessels)
    {
        var sim = new Simulation(Mission.FreePlay, world: World(time, vessels));
        var flown = sim.Flight.WorldVessels.Single(v => v.Id == vessels[0].Id);
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Step(default);
        Assert.Same(flown, sim.Flight.Controlled);
        return (sim, flown);
    }

    /// <summary>The flown vessel's plan as the HUD makes it (round the body it is at, with its nodes).</summary>
    private static ManoeuvrePlan PlanFor(Flight flight)
    {
        var vessel = flight.Controlled;
        var plan = new ManoeuvrePlan();
        plan.Update(vessel.Body, vessel.CentreOfMass, vessel.Body.AmongStars(vessel.CentreOfMass, vessel.Velocity), flight.WorldTime, vessel.Nodes, null);
        return plan;
    }

    /// <summary>The path built for the flown vessel, frame after frame until its window is worked out.</summary>
    private static (Paths Paths, ManoeuvrePlan Plan) Built(Flight flight)
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
    private static (Vector3Double Centre, Vector3Double Velocity) MoonAmongStars(double now, double t)
    {
        var change = Bodies.Change(Moon, Planet, t);
        var centre = change.Point(Moon.Centre);
        var velocity = change.Velocity(Moon.Centre, default) + Vector3Double.Cross(Planet.Spin, centre - Planet.Centre);
        var turn = QuaternionDouble.FromAxisAngle(Planet.NorthPole, Planet.TurnRadians(t - now));
        return (Planet.Centre + turn.Rotate(centre - Planet.Centre), turn.Rotate(velocity));
    }

    /// <summary>The way round the moon's orbit goes (its plane's normal), among the planet's stars at <paramref name="now"/>.</summary>
    private static Vector3Double MoonNormal(double now)
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
    private static (Vector3 Position, Vector3 Velocity) InTheMoonsPlane(double now, double angle, double share)
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
    private static (Vector3 Position, Vector3 Velocity) Transfer(double now, double lead)
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
    private static VesselState OnPlanet(int id, (Vector3 Position, Vector3 Velocity) state) =>
        InOrbit.At(InOrbit.Round(Planet, id, 1000f), state.Position, state.Velocity);

    /// <summary>
    /// A point of a leg round another body, where the test reckons it is drawn among the planet's
    /// stars (matching its ground at <paramref name="now"/>), placed as that body is at world time
    /// <paramref name="then"/> (a last leg's start; a pass's own point's time): the leg's body's
    /// stars then, turned back to its ground then, across to the planet's ground then, and up
    /// among the planet's stars as they match its ground now.
    /// </summary>
    private static Vector3Double AmongPlanetStars(in PathLeg leg, Vector3 starsPoint, double now, double then)
    {
        var body = leg.Body;
        var since = then - now;
        var back = QuaternionDouble.FromAxisAngle(body.NorthPole, body.TurnRadians(-since));
        var ground = (Vector3Double)body.Centre + back.Rotate(starsPoint - (Vector3Double)body.Centre);
        var there = Bodies.Change(body, Planet, then).Point(ground);
        var up = QuaternionDouble.FromAxisAngle(Planet.NorthPole, Planet.TurnRadians(since));
        return Planet.Centre + up.Rotate(there - Planet.Centre);
    }

    /// <summary>The closest two line points in a row come to <paramref name="p"/>, each: so a line that reaches it from both sides has two within a hair of it.</summary>
    private static float PairAt(IReadOnlyList<Vector3> line, Vector3 p)
    {
        var best = float.MaxValue;
        for (var i = 1; i < line.Count; i++)
            best = MathF.Min(best, MathF.Max(Vector3.Distance(line[i - 1], p), Vector3.Distance(line[i], p)));
        return best;
    }

    private static float DistanceToLine(Vector3 p, IReadOnlyList<Vector3> line)
    {
        var best = float.MaxValue;
        for (var i = 1; i < line.Count; i++)
        {
            var (a, b) = (line[i - 1], line[i]);
            var t = (b - a).LengthSquared() > 1e-6f ? Math.Clamp(Vector3.Dot(p - a, b - a) / (b - a).LengthSquared(), 0f, 1f) : 0f;
            best = MathF.Min(best, Vector3.Distance(p, a + (b - a) * t));
        }
        return best;
    }

    private static float Degrees(Vector3 a, Vector3 b) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)));

    [Fact]
    public void The_moon_can_be_targeted_as_a_vessel_is()
    {
        var (sim, flown) = Flying(Start, OnPlanet(0, InTheMoonsPlane(Start, 1.0, 1.0)));
        using (sim)
        {
            var flight = sim.Flight;
            // The body it is at can't be its target; any other can, as a vessel or a place is.
            sim.Step(new FlightInput(SetTarget: NavTarget.OfBody(Planet)));
            Assert.Null(flight.Target);
            var moon = NavTarget.OfBody(Moon);
            sim.Step(new FlightInput(SetTarget: moon));
            Assert.Equal(moon, flight.Target);
            Assert.Equal(new NavTarget(TargetKind.Body, Moon.Index), moon);
            Assert.Equal("Moon", Readout.FlightReadings(flight)["Target"]);

            // Its centre, in the flown vessel's body's frame (the planet's), flying.
            var at = flight.TargetAt!.Value;
            var centre = Bodies.CentreIn(Moon, Planet, flight.WorldTime);
            var off = (at.Position - centre).Length();
            output.WriteLine($"the moon's centre located {off * 1000:0.0} mm off, {Vector3.Distance(flight.CentreOfMass, at.Position):0} m away");
            Assert.True(off < 1, $"{off:0.00} m off the moon's centre");
            Assert.True(at.Flying);
            Assert.Same(Moon, at.Body);
            Assert.Equal(Vector3.Distance(flight.CentreOfMass, at.Position), flight.DistanceToTarget!.Value, 3);

            // Its velocity over the planet's ground, as its centre moves there (a second either side, in double).
            var now = flight.WorldTime;
            var moved = ((Bodies.CentreIn(Moon, Planet, now + 1) - Bodies.CentreIn(Moon, Planet, now - 1)) / 2).ToSingle();
            var velocityOff = Vector3.Distance(moved, at.Velocity);
            output.WriteLine($"its velocity {at.Velocity.Length():0.00} m/s over the ground, {velocityOff * 1000:0.0} mm/s off its motion");
            Assert.True(velocityOff < 0.01f, $"{velocityOff:0.000} m/s off");

            // Let go, as any target is.
            sim.Step(new FlightInput(ClearTarget: true));
            Assert.Null(flight.Target);
            Assert.Same(flown, flight.Controlled);
        }
    }

    [Fact]
    public void In_the_target_frame_the_speed_is_relative_to_the_moons_centre_and_the_holds_follow_it()
    {
        var (sim, flown) = Flying(Start, OnPlanet(0, InTheMoonsPlane(Start, 1.0, 1.0)));
        using (sim)
        {
            var flight = sim.Flight;
            sim.Step(new FlightInput(SetTarget: NavTarget.OfBody(Moon), ChooseFrame: FrameChoice.Target));
            Assert.Equal(Frame.Target, flight.Frame);
            Assert.Equal(Frame.Orbital, flown.AutoFrame);
            Assert.True(flight.HoldFloor < Scene.HoldFloorSpeed, "a flying target's frame is a quiet one");

            // Its velocity among the planet's stars, less the moon's centre's there, as the test reckons it.
            var now = flight.WorldTime;
            var (_, moonStars) = MoonAmongStars(now, now);
            var expected = Planet.AmongStars(flight.CentreOfMass, flight.Velocity) - moonStars.ToSingle();
            var off = Vector3.Distance(flight.FrameVelocity, expected);
            output.WriteLine($"relative to the moon's centre {flight.RelativeSpeed:0.00} m/s, {off * 1000:0.00} mm/s off the test's");
            Assert.True(off < 0.01f, $"{off:0.000} m/s off");
            Assert.Equal(flight.FrameVelocity.Length(), flight.RelativeSpeed!.Value, 4);
            Assert.True(Vector3.Distance(flight.TowardsTarget!.Value, Bodies.CentreIn(Moon, Planet, now).ToSingle() - flight.CentreOfMass) < 1f);

            // The prograde hold turns the nose to that velocity, and keeps it there.
            var from = Degrees(flown.Up, flight.FrameVelocity);
            sim.Step(new FlightInput(ToggleProgradeHold: true));
            Assert.True(sim.Run((_, _) => default, f => Degrees(f.Controlled.Up, f.FrameVelocity) < 1f, maxSeconds: 40),
                $"the hold never brought the nose round (still {Degrees(flown.Up, flight.FrameVelocity):0.0}° off)");
            var away = Degrees(flown.Up, Planet.AmongStars(flight.CentreOfMass, flight.Velocity));
            output.WriteLine($"the hold turned the nose from {from:0.0}° off the moon-relative prograde to under 1°, {away:0.0}° off the orbit's own");
            Assert.True(away > 3f, "the moon-relative way should differ from the orbit's");
            sim.Run((_, _) => default, _ => false, maxSeconds: 5);
            Assert.InRange(Degrees(flown.Up, flight.FrameVelocity), 0f, 1f);
        }
    }

    [Fact]
    public void A_vessel_at_another_body_is_located_and_measured_across_the_bodies()
    {
        var lander = OnTheMoon.Lander(1, OnTheMoon.LandingSpot);
        var (sim, _) = Flying(Start, OnPlanet(0, InTheMoonsPlane(Start, 1.0, 1.0)), lander);
        using (sim)
        {
            var flight = sim.Flight;
            var there = flight.WorldVessels.Single(v => v.Id == 1);
            Assert.Same(Moon, there.Body);
            sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(there), ChooseFrame: FrameChoice.Target));
            var at = flight.TargetAt!.Value;
            Assert.Same(Moon, at.Body);
            Assert.False(at.Flying);
            var expected = Bodies.Change(Moon, Planet, flight.WorldTime).Point(there.CentreOfMass);
            var off = (at.Position - expected).Length();
            output.WriteLine($"the lander located {off * 1000:0.0} mm off, {flight.DistanceToTarget:0} m away");
            Assert.True(off < 1, $"{off:0.00} m off");
            Assert.Equal(Vector3.Distance(flight.CentreOfMass, at.Position), flight.DistanceToTarget!.Value, 3);

            // It stands still on the moon, so over the planet's ground it moves as the moon's ground there does.
            // (A second either side, in double.)
            var now = flight.WorldTime;
            var moved = ((Bodies.Change(Moon, Planet, now + 1).Point(there.CentreOfMass) - Bodies.Change(Moon, Planet, now - 1).Point(there.CentreOfMass)) / 2).ToSingle();
            var velocityOff = Vector3.Distance(moved, at.Velocity);
            output.WriteLine($"its velocity {at.Velocity.Length():0.00} m/s over the planet's ground, {velocityOff * 1000:0.0} mm/s off its motion");
            Assert.True(velocityOff < 0.01f, $"{velocityOff:0.000} m/s off");

            // In Target the speed is relative to it, among the planet's stars (its own frame's, by Auto).
            var place = at;
            var relative = Planet.AmongStars(flight.CentreOfMass, flight.Velocity) - Planet.AmongStars(place.Position, place.Velocity);
            Assert.Equal(Frame.Target, flight.Frame);
            Assert.True(Vector3.Distance(flight.FrameVelocity, relative) < 0.01f);
        }
    }

    [Fact]
    public void From_a_low_orbit_the_moons_window_is_a_stretch_not_a_moment()
    {
        var (sim, _) = Flying(Start, OnPlanet(0, InTheMoonsPlane(Start, 0.0, 1.0)));
        using (sim)
        {
            var flight = sim.Flight;
            sim.Step(new FlightInput(SetTarget: NavTarget.OfBody(Moon)));
            var now = flight.WorldTime;
            var mine = Orbit.OverGround(Planet, flight.CentreOfMass, flight.Velocity);
            var theirs = Bodies.OrbitOf(Moon, now);
            Assert.Equal(Scene.MoonDistance, Vector3.Distance(theirs.PositionAfter(0), Planet.Centre), 1f);
            var window = new List<Encounters.WindowPoint>();
            Encounters.Transfer(mine, theirs, Moon.SphereRadius, window);
            Assert.NotEmpty(window);

            // The longest stretch of it (points no further apart than a window's gap).
            var (longest, first) = (0.0, 0);
            for (var i = 1; i <= window.Count; i++)
                if (i == window.Count || window[i].Anomaly - window[i - 1].Anomaly >= Encounters.WindowGap)
                {
                    longest = Math.Max(longest, window[i - 1].Anomaly - window[first].Anomaly);
                    first = i;
                }
            output.WriteLine($"{window.Count} points; the longest stretch {double.RadiansToDegrees(longest):0.0}° of the orbit");
            Assert.True(double.RadiansToDegrees(longest) >= 3, $"a stretch of only {double.RadiansToDegrees(longest):0.00}°");

            // The game's path works out the same window, by the moon's sphere.
            // Each point where the test's is, and as bright as a miss within the sphere makes it.
            var (paths, _) = Built(flight);
            var planned = paths.PlannedWindow;
            Assert.Equal(window.Count, planned.Count);
            for (var i = 0; i < window.Count; i++)
            {
                Assert.True(Vector3.Distance(planned[i].At, mine.At(window[i].Anomaly)) < 1f, $"point {i} off the test's");
                Assert.Equal(1f - window[i].Miss / Moon.SphereRadius, planned[i].Quality, 3);
            }
            Assert.NotEmpty(paths.WindowLine);

            // Its best point's burn gives a path into the moon's sphere: a leg round the moon.
            var best = window.MinBy(p => p.Miss);
            var transfer = Encounters.TransferBurn(mine, theirs, best.Anomaly)!.Value;
            var (position, velocity) = transfer.StateAfter(0);
            var legs = new List<PathLeg>();
            Patched.Legs(Planet, position, velocity, now, legs, from: transfer.From);
            foreach (var leg in legs)
                output.WriteLine($"{leg.Body.Name}: {leg.Start - now:0} to {leg.End - now:0} s, periapsis {leg.Orbit.PeriapsisDistance - leg.Body.SeaRadius:0} m over its datum");
            Assert.Contains(legs, leg => leg.Body == Moon);
        }
    }

    [Fact]
    public void On_course_the_path_is_drawn_bending_round_the_moon()
    {
        var (sim, _) = Flying(Start, OnPlanet(0, Transfer(Start, 0.22)));
        using (sim)
        {
            var flight = sim.Flight;
            sim.Step(new FlightInput(SetTarget: NavTarget.OfBody(Moon)));
            Assert.Equal(Frame.Orbital, flight.LineFrame);
            var now = flight.WorldTime;
            var (paths, plan) = Built(flight);
            var legs = plan.Legs;
            Assert.True(legs.Count >= 2);
            Assert.Same(Planet, legs[0].Body);
            Assert.Same(Moon, legs[1].Body);
            Assert.Empty(paths.Legs); // no nodes: the path as it is is the line

            // A pass by the moon (a leg after it, back round the planet): every point of the moon
            // leg, as the test places it round the moon as it is when the path is there, is on the line.
            Assert.True(legs.Count >= 3, "a pass, out of the moon's sphere again");
            Assert.Same(Planet, legs[2].Body);
            var moonLeg = legs[1];
            var samples = new List<(Vector3 At, double Seconds)>();
            moonLeg.Orbit.Samples(samples, 0, moonLeg.End - moonLeg.Start);
            var placed = samples.Select(s => AmongPlanetStars(moonLeg, s.At, now, moonLeg.Start + s.Seconds).ToSingle()).ToList();
            var line = paths.Line;
            var worst = placed.Max(p => DistanceToLine(p, line));
            var spacing = placed.Zip(placed.Skip(1), Vector3.Distance).Max();
            output.WriteLine($"{placed.Count} moon-leg points, all within {worst:0.000} m of the line; spaced up to {spacing:0} m");
            Assert.True(worst < 50f, $"the moon leg is {worst:0} m off the line");

            // It meets the planet's legs either side within a centimetre, and the line has no gap at either crossing:
            // a point from each side within a centimetre of it, in a row.
            var into = legs[0].Orbit.PositionAfter(legs[0].End - legs[0].Start);
            var outOf = legs[2].Orbit.PositionAfter(0);
            output.WriteLine($"the moon leg starts {Vector3.Distance(into, placed[0]) * 1000:0.0} mm from the planet leg's end and ends " +
                $"{Vector3.Distance(outOf, placed[^1]) * 1000:0.0} mm from the next's start; on the line, {PairAt(line, into) * 1000:0.0} and {PairAt(line, outOf) * 1000:0.0} mm");
            Assert.True(Vector3.Distance(into, placed[0]) < 0.01f, $"the legs meet {Vector3.Distance(into, placed[0]) * 1000:0.0} mm apart");
            Assert.True(Vector3.Distance(outOf, placed[^1]) < 0.01f, $"the exit leg starts {Vector3.Distance(outOf, placed[^1]) * 1000:0.0} mm from the moon leg's end");
            Assert.True(PairAt(line, into) < 0.01f, $"a gap at the crossing in: {PairAt(line, into) * 1000:0.0} mm");
            Assert.True(PairAt(line, outOf) < 0.01f, $"a gap at the crossing out: {PairAt(line, outOf) * 1000:0.0} mm");

            // It bends round the moon: in its sphere, round the moon's centre as it is then.
            var (closest, inside) = (float.MaxValue, true);
            for (var i = 0; i < placed.Count; i++)
            {
                var (centre, _) = MoonAmongStars(now, moonLeg.Start + samples[i].Seconds);
                var distance = (float)((Vector3Double)placed[i] - centre).Length();
                closest = MathF.Min(closest, distance);
                inside &= distance < Scene.SphereLeaveFactor * Moon.SphereRadius + 1;
            }
            output.WriteLine($"the moon leg comes within {closest - Moon.SeaRadius:0} m of the moon's datum");
            Assert.InRange(closest, Moon.SeaRadius, Moon.SeaRadius + 3000f);
            Assert.True(inside);
        }
    }

    [Fact]
    public void The_closest_approach_to_the_moon_is_the_moon_legs_periapsis()
    {
        var (sim, _) = Flying(Start, OnPlanet(0, Transfer(Start, 0.22)));
        using (sim)
        {
            var flight = sim.Flight;
            sim.Step(new FlightInput(SetTarget: NavTarget.OfBody(Moon)));
            var now = flight.WorldTime;
            var (paths, plan) = Built(flight);
            var moonLeg = plan.Legs[1];
            Assert.Same(Moon, moonLeg.Body);
            var approach = Encounters.BodyApproach(Moon, plan.Legs, now)!.Value;
            var periapsis = moonLeg.Orbit.PeriapsisDistance;
            output.WriteLine($"closest {approach.Distance - Moon.SeaRadius:0.0} m over the moon's datum in {approach.Seconds:0} s; the moon leg's periapsis {periapsis - Moon.SeaRadius:0.0} m");
            Assert.Equal(1, approach.Leg);
            Assert.Equal(periapsis, approach.Distance, 1.0);

            // Drawn on the moon leg's Pe mark, its distance over the moon's datum.
            var closest = paths.Closest!.Value;
            Assert.Equal(approach.Distance - Moon.SeaRadius, closest.Distance, 1.0);
            var pe = moonLeg.Orbit.NextPeriapsis!.Value;
            var peAt = moonLeg.Start + moonLeg.Orbit.TimeTo(pe); // a pass: placed as the moon is then
            var mark = AmongPlanetStars(moonLeg, moonLeg.Orbit.At(pe), now, peAt).ToSingle();
            Assert.True(Vector3.Distance(closest.Mine, mark) < 1f, $"the closest mark {Vector3.Distance(closest.Mine, mark):0.00} m off the periapsis");
            Assert.Contains(paths.Marks, m => m.Label.StartsWith("Pe ") && Vector3.Distance(m.At, mark) < 1f);
            Assert.True(Vector3.Distance(closest.Theirs, AmongPlanetStars(moonLeg, Moon.Centre, now, peAt).ToSingle()) < 1f);
        }

        // With no encounter (a low circle, far from it), the closest the two orbits come.
        var (low, _) = Flying(Start, OnPlanet(0, InTheMoonsPlane(Start, 1.0, 1.0)));
        using (low)
        {
            var flight = low.Flight;
            low.Step(new FlightInput(SetTarget: NavTarget.OfBody(Moon)));
            var now = flight.WorldTime;
            var (paths, plan) = Built(flight);
            Assert.DoesNotContain(plan.Legs, leg => leg.Body == Moon);
            var expected = Encounters.Closest(plan.Legs[0].Orbit, Bodies.OrbitOf(Moon, now), default)!.Value;
            var approach = Encounters.BodyApproach(Moon, plan.Legs, now)!.Value;
            output.WriteLine($"no encounter: closest {approach.Distance:0} m from the moon's centre, as the orbits' {expected.Distance:0} m");
            Assert.Equal(expected.Distance, approach.Distance, 1.0);
            Assert.Equal(expected.Seconds, approach.Seconds, 0.01);
            Assert.Equal(expected.Distance - Moon.SeaRadius, paths.Closest!.Value.Distance, 1.0);
        }
    }

    [Fact]
    public void An_orbit_round_the_moon_is_drawn_as_a_closed_loop_round_it()
    {
        // Flown at the moon: the loop round its centre.
        var (sim, flown) = Flying(Start, InOrbit.Round(Moon, 0, 400f), OnPlanet(1, InTheMoonsPlane(Start, 1.0, 1.0)));
        using (sim)
        {
            var flight = sim.Flight;
            Assert.Same(Moon, flight.Body);
            Assert.Equal(Frame.Orbital, flight.LineFrame);
            var (paths, _) = Built(flight);
            var line = paths.Line;
            var ends = Vector3.Distance(line[0], line[^1]);
            var radius = Moon.SeaRadius + Moon.OrbitFloor + 400f;
            var off = line.Max(p => MathF.Abs(Vector3.Distance(p, Moon.Centre) - radius));
            output.WriteLine($"flown at the moon: {line.Count} points, the ends {ends * 1000:0.0} mm apart, all within {off:0.00} m of its radius");
            Assert.True(ends < 1f, $"the loop's ends {ends:0.00} m apart");
            Assert.True(off < 5f);
        }

        // Seen from the planet, as a target: its path round its own body, a loop round the moon where it is now.
        var (fromPlanet, _) = Flying(Start, OnPlanet(1, InTheMoonsPlane(Start, 1.0, 1.0)), InOrbit.Round(Moon, 0, 400f));
        using (fromPlanet)
        {
            var flight = fromPlanet.Flight;
            Assert.Same(Planet, flight.Body);
            var orbiter = flight.WorldVessels.Single(v => v.Id == 0);
            fromPlanet.Step(new FlightInput(SetTarget: NavTarget.OfVessel(orbiter)));
            Assert.Same(Moon, flight.TargetAt!.Value.Body);
            var (paths, _) = Built(flight);
            var theirs = paths.TheirLine;
            Assert.NotEmpty(theirs);
            var centre = Bodies.CentreIn(Moon, Planet, flight.WorldTime);
            var radius = Moon.SeaRadius + Moon.OrbitFloor + 400f;
            var ends = Vector3.Distance(theirs[0], theirs[^1]);
            var off = theirs.Max(p => (float)Math.Abs(((Vector3Double)p - centre).Length() - radius));
            output.WriteLine($"seen from the planet: the ends {ends * 1000:0.0} mm apart, all within {off:0.00} m of its radius round the moon");
            Assert.True(ends < 1f, $"the loop's ends {ends:0.00} m apart");
            Assert.True(off < 5f, $"{off:0.0} m off the loop round the moon");
            Assert.True(Vector3.Distance(theirs[0], flight.TargetAt!.Value.Position) < 1f, "it starts where the vessel is");
        }
    }

    [Fact]
    public void The_bodies_are_among_the_readouts_markers()
    {
        var (sim, _) = Flying(Start, OnPlanet(0, InTheMoonsPlane(Start, 1.0, 1.0)), InOrbit.Round(Moon, 1, 400f));
        using (sim)
        {
            var flight = sim.Flight;
            var frames = new BodyFrames();
            var candidates = new List<Markers.Candidate>();
            void Gather()
            {
                frames.Begin(flight.WorldTime, flight.Body);
                Markers.Gather(flight, new Settings(), frames, candidates);
            }

            // At the planet: every body has its marker by its name, where it is in the view's
            // frame; the moon's is clicked to target it, the planet's (the body it is at) targets nothing.
            Gather();
            foreach (var body in Bodies.All)
            {
                var marker = candidates.Single(c => c.Name == body.Name);
                Assert.Equal(MarkerKind.Body, marker.Kind);
                Assert.True(Vector3.Distance(marker.Position, Bodies.CentreIn(body, Planet, flight.WorldTime).ToSingle()) < 1f);
            }
            Assert.Null(candidates.Single(c => c.Name == Planet.Name).Target);
            var moon = candidates.Single(c => c.Name == Moon.Name).Target;
            Assert.Equal(NavTarget.OfBody(Moon), moon);
            Assert.Equal((moon, false), Markers.Click(onMarker: true, moon, flight.Target, emptyLetsGo: true));
            sim.Step(new FlightInput(SetTarget: moon));
            Assert.Equal(moon, flight.Target);
            Assert.Equal(Moon.Name, Readout.FlightReadings(flight)["Target"]);
            Gather();
            Assert.Equal(moon, candidates[0].Target); // the target first

            // At the moon, the other way about.
            var orbiter = flight.WorldVessels.Single(v => v.Id == 1);
            sim.Step(new FlightInput(SwitchTo: orbiter));
            sim.Step(default);
            Assert.Same(Moon, flight.Body);
            Gather();
            Assert.Null(candidates.Single(c => c.Name == Moon.Name).Target);
            Assert.Equal(NavTarget.OfBody(Planet), candidates.Single(c => c.Name == Planet.Name).Target);
            sim.Step(new FlightInput(SetTarget: NavTarget.OfBody(Moon)));
            Assert.Null(flight.Target); // not the body it is at
        }
    }

    [Fact]
    public void Crossing_into_the_targeted_bodys_sphere_lets_it_go()
    {
        // 2 km outside the moon's sphere, coming in at 100 m/s (among the moon's stars) to pass 3.5 km from its centre.
        var change = Bodies.Change(Moon, Planet, Start);
        var (towards, across) = (Vector3.UnitX, Vector3.UnitZ);
        var there = Moon.Centre + towards * (Moon.SphereRadius + 2000f);
        var stars = Vector3.Normalize(Moon.Centre + across * 3500f - there) * 100f;
        var place = change.Point(there);
        var overGround = change.Velocity(there, Moon.OverGround(there, stars));
        var state = OnPlanet(0, (place.ToSingle(), Planet.AmongStars(place.ToSingle(), overGround.ToSingle())));
        var (sim, flown) = Flying(Start, state);
        using (sim)
        {
            var flight = sim.Flight;
            Assert.Same(Planet, flight.Body);
            sim.Step(new FlightInput(SetTarget: NavTarget.OfBody(Moon), ChooseFrame: FrameChoice.Target, ToggleProgradeHold: true));
            Assert.Equal(Frame.Target, flight.Frame);
            var paths = new Paths();
            paths.Build(flight, PlanFor(flight));

            Assert.True(sim.Run((_, _) => default, f => f.Body == Moon, maxSeconds: 40), "it never crossed into the moon's sphere");
            output.WriteLine($"crossed {Vector3.Distance(flight.CentreOfMass, Moon.Centre):0} m from the moon's centre at {flight.WorldTime - Start:0.0} s");
            // The body it is now at is no target: let go, the frame back to Auto, the hold going on by Auto's.
            Assert.Null(flight.Target);
            Assert.Null(flown.Target);
            Assert.Equal(FrameChoice.Auto, flight.FrameChoice);
            Assert.NotEqual(Frame.Target, flight.Frame);
            Assert.True(flown.ProgradeHold);
            Assert.Equal(Frames.VelocityIn(Moon, flown.AutoFrame, flight.CentreOfMass, flight.Velocity), flight.FrameVelocity);
            // No window, closest approach or target's path drawn for it any more.
            do
                paths.Build(flight, PlanFor(flight));
            while (paths.Working);
            Assert.Empty(paths.PlannedWindow);
            Assert.Empty(paths.WindowLine);
            Assert.Null(paths.Closest);
            Assert.Empty(paths.TheirLine);
        }
    }

    [Fact]
    public void Building_the_path_with_a_body_or_a_vessel_at_another_body_targeted_allocates_nothing()
    {
        // On course for the moon, with a node, the moon targeted; then a vessel round the moon targeted.
        var (sim, _) = Flying(Start, OnPlanet(0, Transfer(Start, 0.22)), InOrbit.Round(Moon, 1, 400f));
        using (sim)
        {
            var flight = sim.Flight;
            sim.Step(new FlightInput(SetTarget: NavTarget.OfBody(Moon), Plan: [new ManoeuvreNode(flight.WorldTime + 60, Prograde: 2f)]));
            var orbiter = flight.WorldVessels.Single(v => v.Id == 1);
            foreach (var target in new[] { NavTarget.OfBody(Moon), NavTarget.OfVessel(orbiter) })
            {
                sim.Step(new FlightInput(SetTarget: target));
                Assert.Equal(target, flight.Target);
                var (paths, plan) = Built(flight);
                Assert.NotNull(plan.NodeLeg(0));
                Assert.NotEmpty(paths.TheirLine);
                for (var i = 0; i < 5; i++)
                    paths.Build(flight, plan); // warm
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 10; i++)
                    paths.Build(flight, plan);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                output.WriteLine($"{target.Kind} targeted: {allocated} B over 10 builds");
                Assert.Equal(0, allocated);
            }
        }
    }

    [Fact]
    public void A_vessel_targets_closest_approach_is_found_on_whichever_leg_round_its_body_comes_closest()
    {
        // The pass by the moon: round the planet, the moon, and the planet again.
        var (position, velocity) = Transfer(Start, 0.22);
        var legs = new List<PathLeg>();
        Patched.Legs(Planet, position, velocity, Start, legs);
        Assert.True(legs.Count >= 3);
        Assert.Same(Planet, legs[2].Body);

        // A vessel round the planet flying the leg out after the pass (its orbit from now, Kepler's
        // run back): nearest on that later leg, where the two are one, not on the first.
        var exit = legs[2];
        var (back, backVelocity) = exit.Orbit.StateAfter(Start - exit.Start);
        var theirs = new Orbit(Planet, back, backVelocity);
        var approach = Encounters.VesselApproach(theirs, legs, Start)!.Value;
        var onFirst = Encounters.Closest(legs[0].Orbit, theirs, default, legs[0].End - legs[0].Start)!.Value;
        output.WriteLine($"closest on leg {approach.Leg}: {approach.Distance:0.00} m in {approach.Seconds:0} s; on the first leg alone {onFirst.Distance:0} m");
        Assert.Equal(2, approach.Leg);
        Assert.True(approach.Distance < 1f, $"{approach.Distance:0.0} m");
        Assert.True(onFirst.Distance > 100f);
        Assert.InRange(approach.Seconds, exit.Start - Start, exit.End - Start);

        // Each leg only over its own span: the first leg's orbit carried on past the crossing (never
        // flown) is not looked along.
        var whole = Encounters.Closest(legs[0].Orbit, theirs, default)!.Value;
        output.WriteLine($"along the first leg's whole orbit {whole.Distance:0} m in {whole.Seconds:0} s; it crosses at {legs[0].End - Start:0} s");
        Assert.True(whole.Seconds > legs[0].End - legs[0].Start, "the test needs the whole orbit's closest past the crossing");
        var firstOnly = Encounters.VesselApproach(theirs, legs.Take(2).ToList(), Start)!.Value;
        Assert.Equal(0, firstOnly.Leg);
        Assert.True(firstOnly.Seconds <= legs[0].End - Start + 1e-6, $"found {firstOnly.Seconds:0} s on, past the crossing");
        Assert.Equal(onFirst.Distance, firstOnly.Distance, 0.01);

        // A vessel round a body the path has no leg round: none.
        var low = new List<PathLeg>();
        var (lowAt, lowVelocity) = InTheMoonsPlane(Start, 1.0, 1.0);
        Patched.Legs(Planet, lowAt, lowVelocity, Start, low);
        Assert.DoesNotContain(low, leg => leg.Body == Moon);
        var r = Moon.SeaRadius + Moon.OrbitFloor + 400f;
        var roundTheMoon = new Orbit(Moon, Moon.Centre + Vector3.UnitX * r, Moon.East(Moon.Centre + Vector3.UnitX * r) * Moon.CircularSpeed(Moon.OrbitFloor + 400f));
        Assert.Null(Encounters.VesselApproach(roundTheMoon, low, Start));
    }

    [Fact]
    public void A_bodys_marker_is_hidden_behind_another_body_unless_it_is_the_target()
    {
        var (sim, _) = Flying(Start, OnPlanet(0, InTheMoonsPlane(Start, 1.0, 1.0)));
        using (sim)
        {
            var flight = sim.Flight;
            var frames = new BodyFrames();
            frames.Begin(flight.WorldTime, flight.Body);
            var candidates = new List<Markers.Candidate>();
            Markers.Gather(flight, new Settings(), frames, candidates);
            var moon = candidates.Single(c => c.Name == Moon.Name);
            var planet = candidates.Single(c => c.Name == Planet.Name);

            // From the far side of the planet from the moon, 500 m over its sea: the planet is in the way.
            var away = Vector3.Normalize(Planet.Centre - moon.Position);
            var camera = Planet.Centre + away * (Planet.SeaRadius + 500f);
            Assert.True(frames.Behind(camera, moon.Position, Moon));
            Assert.Null(Markers.Through(moon, isTarget: false, frames, camera));
            Assert.Equal(0.45f, Markers.Through(moon, isTarget: true, frames, camera));
            // The planet's own, on its centre, is not hidden by the planet itself; nor the moon's from the moon's side.
            Assert.Equal(1f, Markers.Through(planet, isTarget: false, frames, camera));
            var near = Planet.Centre - away * (Planet.SeaRadius + 500f);
            Assert.Equal(1f, Markers.Through(moon, isTarget: false, frames, near));
            // Anything else behind the planet is hidden, but the target and those with an arrow, faint.
            var site = candidates.First(c => c.Kind == MarkerKind.Site && !c.Arrow);
            Assert.Null(Markers.Through(site with { Position = moon.Position }, isTarget: false, frames, camera));
        }
    }

    [Fact]
    public void A_window_is_worked_out_only_as_far_as_the_orbit_is_flown_to_a_crossing()
    {
        // Coming up on the moon's sphere from outside it, on an orbit round the planet that carries
        // on past the crossing: the moon targeted.
        var change = Bodies.Change(Moon, Planet, Start);
        var there = Moon.Centre + Vector3.UnitX * (Moon.SphereRadius + 2000f);
        var stars = Vector3.Normalize(Moon.Centre + Vector3.UnitZ * 3500f - there) * 100f;
        var place = change.Point(there);
        var overGround = change.Velocity(there, Moon.OverGround(there, stars));
        var (sim, _) = Flying(Start, OnPlanet(0, (place.ToSingle(), Planet.AmongStars(place.ToSingle(), overGround.ToSingle()))));
        using (sim)
        {
            var flight = sim.Flight;
            sim.Step(new FlightInput(SetTarget: NavTarget.OfBody(Moon)));
            var now = flight.WorldTime;
            var (paths, plan) = Built(flight);
            var first = plan.Legs[0];
            Assert.Same(Moon, plan.Legs[1].Body);
            var span = first.End - first.Start;

            // Along the whole orbit there are window points past the crossing; along what is flown, none.
            var whole = new List<Encounters.WindowPoint>();
            Encounters.Transfer(first.Orbit, Bodies.OrbitOf(Moon, now), Moon.SphereRadius, whole);
            var past = whole.Count(p => first.Orbit.TimeTo(p.Anomaly) > span);
            output.WriteLine($"crossing in {span:0.0} s; along its whole orbit {whole.Count} window points, {past} past the crossing; the game's {paths.PlannedWindow.Count}");
            Assert.True(past > 0, "the test needs window points past the crossing");
            Assert.All(paths.PlannedWindow, p => Assert.True(p.Seconds <= span + 1e-6, $"a window point {p.Seconds:0.0} s on, past the crossing at {span:0.0} s"));
            var bounded = new List<Encounters.WindowPoint>();
            foreach (var _ in Encounters.TransferSteps(first.Orbit, Bodies.OrbitOf(Moon, now), Moon.SphereRadius, bounded, until: span)) { }
            Assert.All(bounded, p => Assert.True(first.Orbit.TimeTo(p.Anomaly) <= span + 1e-6));
            Assert.Equal(bounded.Count, paths.PlannedWindow.Count);
        }
    }

    [Fact]
    public void The_moons_ring_shows_zoomed_out_with_the_landing_spot_on_it()
    {
        // Flying the moon mission in orbit, the camera zoomed out to fit the moon's orbit on a
        // 1280 x 720 screen: the moon is a few tens of pixels across, its landing spot (drawn
        // first) on it, and its ring is drawn all the same: not faded away, not given way.
        using var sim = new Simulation(Mission.LandOnTheMoon, world: World(Start, InOrbit.Round(Planet, 0, 2000f)));
        sim.Step(default);
        var flight = sim.Flight;
        var flown = flight.WorldVessels.Single(v => v.Id == 0);
        var frames = new BodyFrames();
        frames.Begin(flight.WorldTime, flight.Body);
        var candidates = new List<Markers.Candidate>();
        Markers.Gather(flight, new Settings(), frames, candidates);

        var screen = new Vector2(1280f, 720f);
        var camera = SystemMapTests.ZoomedOut(Planet, flown.CentreOfMass, Vector2.Zero, flight.WorldTime, clicks: 7f);
        var taken = new List<(Brutal.Numerics.float2 Min, Brutal.Numerics.float2 Max)>();
        var drawn = new List<string>();
        foreach (var marker in candidates)
        {
            var fade = Markers.Fade(marker, camera.Position, camera.Forward, screen);
            var onScreen = camera.ToScreen(marker.Position, screen, out var at, out _);
            if (fade < 0.02f || !onScreen)
                continue;
            var (min, max) = Markers.Box(at, marker.Loud);
            var gaveWay = Markers.GivesWay(marker, taken, min, max);
            output.WriteLine($"{marker.Name}: fade {fade:0.00} at {at}, {(gaveWay ? "gives way" : "drawn")}");
            if (gaveWay)
                continue;
            taken.Add((min, max));
            drawn.Add(marker.Name);
            if (marker.Kind == MarkerKind.Body && marker.Name == Moon.Name)
                Assert.True(fade > 0.5f, $"the moon's ring is faded to {fade:0.00}");
        }
        Assert.Contains("Landing spot", drawn);
        Assert.Contains(Moon.Name, drawn);
    }
}
