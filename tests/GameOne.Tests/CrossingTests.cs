using System.Diagnostics;
using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Crossing a sphere of influence's edge (<see cref="Vessel.CrossTo"/>, chosen each step by
/// <see cref="Bodies.Choose(GroundBody, Vector3, double)"/>): a vessel handed from one body's physics
/// or formula to the other's with no jump or pause, where the prediction said, with no flip-flop at
/// the edge, each vessel by itself; and switching to a vessel
/// at a body whose physics isn't made, behind the communications link-up (<see cref="LinkUp"/>).
/// </summary>
public sealed class CrossingTests(ITestOutputHelper output)
{
    private static GroundBody Moon => GroundBody.Moon;
    private static GroundBody Home => GroundBody.Planet;
    private static readonly Pilot Nobody = (_, _) => default;
    private static float Edge => Moon.SphereRadius;
    private static float LeaveEdge => Moon.SphereRadius * Scene.SphereLeaveFactor;

    /// <summary>How fast the craft here come at the moon, among its stars (m/s).</summary>
    private const float Speed = 100f;

    // ---- Where the tests start ----

    /// <summary>
    /// The stages <paramref name="first"/> to <paramref name="last"/> of <paramref name="vehicle"/>
    /// (the satellite launcher's upper stage alone by default), numbered <paramref name="id"/>, its engine off and legs stowed, at
    /// <paramref name="body"/>, its centre of mass at <paramref name="centreOfMass"/> moving at
    /// <paramref name="velocity"/> over that body's ground (its frame), its nose along <paramref name="nose"/>,
    /// each stage's tank <paramref name="fuel"/> full.
    /// </summary>
    internal static VesselState Craft(int id, GroundBody body, Vector3 centreOfMass, Vector3 velocity, Vector3 nose, int first = 1, int last = 1, float fuel = 0.5f, Vehicle? vehicle = null)
    {
        vehicle ??= Vehicle.ThreeStage;
        var rotation = Formula.Turn(Vector3.UnitY, Vector3.Normalize(nose));
        var offset = RocketGeometry.CentreOfMassOffset(vehicle, first, last);
        return new VesselState
        {
            Id = id, Flight = 1, Mission = Mission.FreePlay, Vehicle = vehicle, First = first, Last = last,
            Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * fuel)],
            State = FlightState.Flying, Position = centreOfMass - Vector3.Transform(offset, rotation), Rotation = rotation,
            CentreOfMassOffset = offset, Velocity = velocity, AngularVelocity = Vector3.Zero, FairingOn = false,
            Aim = Vector3.Normalize(nose), HasBeenAirborne = true, MaxAltitude = 1_000f, Body = body.Name,
        };
    }

    /// <summary>
    /// The way from the moon's centre towards the planet's, in the moon's frame at world time 0,
    /// and a way square to it: what the starts here are laid out along.
    /// </summary>
    internal static (Vector3 Out, Vector3 Across) Ways()
    {
        var toPlanet = Vector3.Normalize((Bodies.CentreIn(Home, Moon, 0.0) - (Vector3Double)Moon.Centre).ToSingle());
        return (toPlanet, Vector3.Normalize(Vector3.Cross(toPlanet, Moon.NorthPole)));
    }

    /// <summary>
    /// A place <paramref name="offset"/> from the moon's centre, moving at <paramref name="stars"/>
    /// among the moon's stars, at world time <paramref name="t"/>: in <paramref name="body"/>'s frame (its place, and
    /// its velocity over its ground).
    /// </summary>
    internal static (Vector3 At, Vector3 Velocity) FromMoon(Vector3 offset, Vector3 stars, GroundBody body, double t = 0.0)
    {
        var at = Moon.Centre + offset;
        var change = Bodies.Change(Moon, body, t);
        return (change.Point(at).ToSingle(), change.Velocity(at, Moon.OverGround(at, stars)).ToSingle());
    }

    /// <summary>
    /// The upper stage at the planet, coming at the moon's sphere at <see cref="Speed"/> past its
    /// side (its line <paramref name="aside"/> from the moon's centre), to cross into it about <paramref name="seconds"/> after world time 0.
    /// </summary>
    private static VesselState Approaching(int id, float seconds, float aside = 4_500f)
    {
        var (out_, across) = Ways();
        var into = MathF.Sqrt(Edge * Edge - aside * aside); // where on the line in it comes into the sphere
        var (at, velocity) = FromMoon(out_ * (into + seconds * Speed) + across * aside, -out_ * Speed, Home);
        return Craft(id, Home, at, velocity, velocity);
    }

    /// <summary>The upper stage at the moon, going out of its sphere at <see cref="Speed"/>, to cross out of it about <paramref name="seconds"/> after world time 0.</summary>
    private static VesselState Leaving(int id, float seconds)
    {
        var (out_, _) = Ways();
        var at = Moon.Centre + out_ * (LeaveEdge - seconds * Speed);
        var velocity = Moon.OverGround(at, out_ * Speed); // straight out: on a line, by Kepler's equation for one
        return Craft(id, Moon, at, velocity, velocity);
    }

    private static WorldState World(params VesselState[] vessels) =>
        new() { Time = 0.0, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    private static Vessel Get(Simulation sim, int id) => sim.Flight.WorldVessels.Single(v => v.Id == id);

    // ---- Following one across ----

    /// <summary>One frame of a vessel followed: its place and velocity in the planet's frame at the moment its motion is at, its body, whether the moon had a physics before the frame, and how long the frame took.</summary>
    private readonly record struct Sample(double Time, Vector3Double At, Vector3Double Velocity, GroundBody Body, bool MoonMade, double Milliseconds);

    /// <summary>Where <paramref name="v"/> is now, in the planet's frame (see <see cref="Sample"/>).</summary>
    private static (double Time, Vector3Double At, Vector3Double Velocity) InPlanetFrame(Simulation sim, Vessel v)
    {
        var t = v.Physics is null ? sim.Flight.WorldTime : sim.Flight.WorldTime - sim.Worlds.Lag;
        var change = Bodies.Change(v.Body, Home, t);
        Vector3Double at = v.CentreOfMass;
        return (t, change.Point(at), change.Velocity(at, v.Velocity));
    }

    /// <summary>Flies <paramref name="pilot"/> frame by frame for up to <paramref name="seconds"/>, following <paramref name="v"/>, until <paramref name="frames"/> frames after its body first changes.</summary>
    private static List<Sample> Follow(Simulation sim, Vessel v, double seconds, Pilot pilot, int frames = 30)
    {
        var samples = new List<Sample>();
        var (t0, at0, velocity0) = InPlanetFrame(sim, v);
        samples.Add(new Sample(t0, at0, velocity0, v.Body, sim.Flight.PhysicsAt(Moon) is not null, 0));
        var start = v.Body;
        var after = -1;
        var clock = new Stopwatch();
        while (sim.Time < seconds && (after < 0 || samples.Count < after + frames))
        {
            var made = sim.Flight.PhysicsAt(Moon) is not null;
            clock.Restart();
            sim.Step(pilot(sim.Flight, Simulation.FrameSeconds * sim.Flight.TimeScale));
            var ms = clock.Elapsed.TotalMilliseconds;
            var (t, at, velocity) = InPlanetFrame(sim, v);
            samples.Add(new Sample(t, at, velocity, v.Body, made, ms));
            if (after < 0 && v.Body != start)
                after = samples.Count - 1;
        }
        return samples;
    }

    private static int FirstChange(List<Sample> samples) => samples.FindIndex(s => s.Body != samples[0].Body);

    private static int Changes(List<Sample> samples)
    {
        var changes = 0;
        for (var i = 1; i < samples.Count; i++)
            changes += samples[i].Body != samples[i - 1].Body ? 1 : 0;
        return changes;
    }

    /// <summary>
    /// Across frame <paramref name="k"/>, in the planet's frame: its place within 0.5 m of where the
    /// frame before's velocity carries it, and its velocity within 0.05 m/s of where the frames
    /// before's change of it carries it; and the worst of every other frame, for comparison.
    /// </summary>
    private void AssertContinuous(List<Sample> samples, int k, string what)
    {
        (double Place, double Speed) Off(int i)
        {
            var (a, b, c) = (samples[i - 2], samples[i - 1], samples[i]);
            var dt = c.Time - b.Time;
            var place = (c.At - (b.At + (b.Velocity + c.Velocity) * (dt / 2))).Length();
            var change = (b.Velocity - a.Velocity) * (dt / (b.Time - a.Time));
            return (place, (c.Velocity - (b.Velocity + change)).Length());
        }
        var (worstPlace, worstSpeed) = (0.0, 0.0);
        for (var i = 2; i < samples.Count; i++)
            if (i != k)
                (worstPlace, worstSpeed) = (Math.Max(worstPlace, Off(i).Place), Math.Max(worstSpeed, Off(i).Speed));
        var (place, speed) = Off(k);
        output.WriteLine($"{what}: across the crossing {place * 1000:0.0} mm and {speed * 1000:0.00} mm/s off; every other frame at worst {worstPlace * 1000:0.0} mm and {worstSpeed * 1000:0.00} mm/s");
        Assert.True(place < 0.5, $"{what}: the place jumped {place:0.000} m across the crossing");
        Assert.True(speed < 0.05, $"{what}: the velocity jumped {speed:0.0000} m/s across the crossing");
    }

    /// <summary>No frame from the crossing's at <paramref name="k"/> took over 1.5 times its neighbours' (the middle of the ten each side).</summary>
    private void AssertNoPause(List<Sample> samples, int k)
    {
        var near = new List<double>();
        for (var i = Math.Max(1, k - 10); i <= Math.Min(samples.Count - 1, k + 10); i++)
            if (i != k)
                near.Add(samples[i].Milliseconds);
        near.Sort();
        var middle = near[near.Count / 2];
        output.WriteLine($"the crossing's frame took {samples[k].Milliseconds:0.000} ms, its neighbours' middle {middle:0.000} ms");
        Assert.True(samples[k].Milliseconds <= 1.5 * middle, $"the crossing's frame took {samples[k].Milliseconds:0.000} ms, against {middle:0.000} ms");
    }

    /// <summary>A crossing of the moon's sphere flown once in a world of its own, so the code it runs is compiled before a test times one.</summary>
    private static void WarmUp()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(Approaching(0, 0.3f)));
        var v = Get(sim, 0);
        sim.Step(new FlightInput(SwitchTo: v));
        sim.Run(Nobody, _ => v.Body == Moon, maxSeconds: 3);
        sim.Run(Nobody, _ => false, maxSeconds: 0.2);
    }

    [Fact]
    [Trait("Run", "Alone")] // its frame's time is weighed against its neighbours' in real time: beside the other shards, their load would weigh in
    public void Crossing_into_the_moons_sphere_keeps_the_path_continuous()
    {
        WarmUp();
        using var sim = new Simulation(Mission.FreePlay, world: World(Approaching(0, 30f)));
        var v = Get(sim, 0);
        sim.Step(new FlightInput(SwitchTo: v));
        Assert.Same(v, sim.Flight.Controlled);
        Assert.Same(sim.Flight.PhysicsAt(Home), v.Physics);
        var samples = Follow(sim, v, 45, Nobody);
        var k = FirstChange(samples);
        Assert.True(k > 0, "it never crossed");
        output.WriteLine($"crossed at {samples[k].Time:0.000} s");
        Assert.Same(Moon, v.Body);
        Assert.Equal(1, Changes(samples));
        // In the moon's physics, made ahead: there the step before (never made in the crossing's step).
        Assert.True(samples[k].MoonMade, "the moon's physics wasn't made ahead of the crossing");
        Assert.Same(sim.Flight.PhysicsAt(Moon), v.Physics);
        Assert.False(v.OnFormula);
        AssertContinuous(samples, k, "into the moon's physics");
        AssertNoPause(samples, k);
    }

    [Fact]
    [Trait("Run", "Alone")] // its frame's time is weighed against its neighbours' in real time: beside the other shards, their load would weigh in
    public void Crossing_out_of_the_sphere_keeps_the_path_continuous_too()
    {
        WarmUp();
        using var sim = new Simulation(Mission.FreePlay, world: World(Leaving(0, 5f)));
        var v = Get(sim, 0);
        sim.Step(new FlightInput(SwitchTo: v)); // the link-up: the moon's physics made the frame after
        sim.Step(default);
        Assert.Same(v, sim.Flight.Controlled);
        Assert.Same(sim.Flight.PhysicsAt(Moon), v.Physics);
        var samples = Follow(sim, v, 15, Nobody);
        var k = FirstChange(samples);
        Assert.True(k > 0, "it never crossed");
        Assert.Same(Home, v.Body);
        Assert.Equal(1, Changes(samples));
        Assert.Same(sim.Flight.PhysicsAt(Home), v.Physics);
        AssertContinuous(samples, k, "out into the planet's physics");
        AssertNoPause(samples, k);
    }

    [Fact]
    public void Crossing_under_formula_keeps_the_path_continuous()
    {
        // Nobody watches it (the rocket on Home's pad is flown): it loads under formula, crosses
        // under formula, and never has a physics made for it.
        using var sim = new Simulation(Mission.FreePlay, world: World(Approaching(0, 3f)));
        var v = Get(sim, 0);
        Assert.True(v.OnFormula);
        var samples = Follow(sim, v, 10, Nobody);
        var k = FirstChange(samples);
        Assert.True(k > 0, "it never crossed");
        Assert.Same(Moon, v.Body);
        Assert.Equal(1, Changes(samples));
        Assert.True(v.OnFormula);
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        AssertContinuous(samples, k, "under formula");
        // And back out again, the same way.
        using var back = new Simulation(Mission.FreePlay, world: World(Leaving(0, 2f)));
        var w = Get(back, 0);
        Assert.True(w.OnFormula);
        var outward = Follow(back, w, 6, Nobody);
        var j = FirstChange(outward);
        Assert.True(j > 0, "it never crossed out");
        Assert.Same(Home, w.Body);
        Assert.True(w.OnFormula);
        AssertContinuous(outward, j, "out under formula");
    }

    [Fact]
    public void Crossing_begins_the_best_altitude_again_over_the_new_body_and_a_height_reached_stays_reached()
    {
        // Reach Orbit's 3,000 m reached at the planet (tens of km up there); across into the moon's
        // pull its best altitude is over the moon's datum, from where it crossed, and still past it.
        using var sim = new Simulation(Mission.FreePlay, world: World(Approaching(0, 1f) with { Mission = Mission.ReachOrbit }));
        var v = Get(sim, 0);
        sim.Step(default);
        Assert.Same(Home, v.Body);
        var best = v.MaxAltitude;
        Assert.True(best > Scene.AtmosphereHeight && v.ReachedTarget);
        float? crossedAt = null;
        for (var i = 0; i < 60 * 4 && crossedAt is null; i++)
        {
            sim.Step(default);
            if (v.Body == Moon)
                crossedAt = v.Altitude;
        }
        Assert.NotNull(crossedAt);
        output.WriteLine($"best altitude at the planet {best:0} m; over the moon from its crossing {v.MaxAltitude:0} m (its altitude there {crossedAt:0} m)");
        Assert.Equal(crossedAt.Value, v.MaxAltitude, 3);
        Assert.True(v.MaxAltitude < best);
        Assert.True(v.ReachedTarget, $"{Mission.ReachOrbit.TargetAltitude:0} m no longer reached at {v.MaxAltitude:0} m over the moon");
        // Falling on in towards the moon, it stays the best from where it crossed.
        for (var i = 0; i < 60; i++)
            sim.Step(default);
        Assert.Equal(crossedAt.Value, v.MaxAltitude, 3);
        Assert.True(v.Altitude < v.MaxAltitude);
    }

    [Fact]
    public void The_flight_crosses_where_the_prediction_said()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(Approaching(0, 20f)));
        var v = Get(sim, 0);
        sim.Step(new FlightInput(SwitchTo: v));
        var now = sim.Flight.WorldTime;
        var legs = new List<PathLeg>();
        Patched.Legs(Home, v.CentreOfMass, Home.AmongStars(v.CentreOfMass, v.Velocity), now, legs);
        Assert.True(legs.Count > 1 && legs[1].Body == Moon, "no crossing into the moon's sphere predicted");
        var when = legs[0].End;
        var (stars, _) = legs[0].Orbit.StateAfter(when - now);
        var there = Home.Turned(stars, -(when - now)); // among the stars as the ground was at now: back over the ground then
        var samples = Follow(sim, v, 40, Nobody, frames: 2);
        var k = FirstChange(samples);
        Assert.True(k > 0, "it never crossed");
        var (before, after) = (samples[k - 1], samples[k]);
        var share = (when - before.Time) / (after.Time - before.Time);
        var at = before.At + (after.At - before.At) * Math.Clamp(share, 0, 1);
        var off = (at - there).Length();
        output.WriteLine($"predicted {when:0.000} s, crossed in the frame to {after.Time:0.000} s; {off:0.00} m from the predicted place");
        Assert.InRange(after.Time - when, -0.1, 0.1);
        Assert.True(off < 5, $"it crossed {off:0.00} m from where the prediction said");
    }

    [Fact]
    public void A_landed_vessel_is_never_asked_which_body_it_is_at()
    {
        // Landed on the moon and on the planet's landing zone, and the new rocket on its pad: each
        // nearer its body's centre than any edge, so the crossing asks none of them, frame after
        // frame, in the physics or under formula, at warp too.
        var zone = LaunchSites.Home.LandingZone!.Value;
        var landed = Drops.Placed(zone, Planet.East(zone), 0f, clearance: -1.5f * Scene.LegStroke, id: 1)
            with { State = FlightState.Landed, Velocity = Vector3.Zero, TouchedDown = true };
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot), landed));
        var (lander, booster) = (Get(sim, 0), Get(sim, 1));
        var frames = 0;
        sim.Run(Nobody, flight =>
        {
            Assert.Equal(0, flight.CrossAsked);
            return ++frames >= 120;
        }, maxSeconds: 10);
        for (var i = 0; i < 5; i++)
            sim.Step(new FlightInput(Faster: true));
        frames = 0;
        sim.Run(Nobody, flight =>
        {
            Assert.Equal(0, flight.CrossAsked);
            return ++frames >= 60;
        }, maxSeconds: 600);
        Assert.Equal(32f, sim.Flight.PlayerRate);
        Assert.Same(Moon, lander.Body);
        Assert.Same(Home, booster.Body);
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.Equal(FlightState.Landed, booster.State);
    }

    /// <summary>
    /// A vessel at the planet coming straight at the moon's middle at 1,000 m/s among its stars,
    /// starting 4 km outside its sphere (far enough to be left unasked at first): flown (in the
    /// physics) with <paramref name="flown"/>, else under formula.
    /// </summary>
    private void ReachesTheEdge(bool flown)
    {
        var (out_, across) = Ways();
        var (at, velocity) = FromMoon(out_ * (Edge + 4_000f), -out_ * 1_000f, Home);
        using var sim = new Simulation(Mission.FreePlay, world: World(Craft(0, Home, at, velocity, across)));
        var v = Get(sim, 0);
        if (flown)
            sim.Step(new FlightInput(SwitchTo: v));
        Assert.Equal(!flown, v.OnFormula);
        var unasked = 0;
        Assert.True(sim.Run(Nobody, flight =>
        {
            var (t, place, _) = InPlanetFrame(sim, v);
            var fromMoon = (place - Bodies.CentreIn(Moon, Home, t)).Length();
            if (v.Body == Home)
            {
                unasked += flight.CrossAsked == 0 ? 1 : 0;
                // Never a step inside the sphere still at the planet: it crosses the step it is in.
                Assert.True(fromMoon >= Edge - 0.05, $"{Edge - fromMoon:0.00} m inside the sphere, still at the planet");
                return false;
            }
            Assert.True(fromMoon < Edge, $"at the moon {fromMoon - Edge:0.00} m outside its sphere");
            return true;
        }, maxSeconds: 10), "it never crossed");
        output.WriteLine($"{(flown ? "in the physics" : "under formula")}: left unasked {unasked} frames, then crossed on time");
        Assert.True(unasked > 0, "it was asked from the start: the bound skipped nothing");
    }

    [Fact]
    public void A_vessel_reaching_the_edge_in_the_physics_crosses_the_step_it_is_inside() => ReachesTheEdge(flown: true);

    [Fact]
    public void A_vessel_reaching_the_edge_under_formula_crosses_the_step_it_is_inside() => ReachesTheEdge(flown: false);

    /// <summary>
    /// Flies <paramref name="state"/> (flown) for <paramref name="seconds"/>: how often its body
    /// changed out of the moon's and into it, how far it was from the moon's centre at each change,
    /// and the farthest and nearest it went.
    /// </summary>
    private static (int Outs, int Ins, List<float> At, float Farthest, float Nearest) Graze(VesselState state, double seconds)
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(state));
        var v = Get(sim, 0);
        sim.Step(new FlightInput(SwitchTo: v));
        sim.Step(default); // flown: at the moon, the frame after the link-up begins
        var (outs, ins, was) = (0, 0, v.Body);
        var (at, farthest, nearest) = (new List<float>(), 0f, float.PositiveInfinity);
        sim.Run(Nobody, _ =>
        {
            var centre = v.Body == Moon ? Moon.Centre : Bodies.CentreIn(Moon, v.Body, sim.Flight.WorldTime).ToSingle();
            var distance = Vector3.Distance(v.CentreOfMass, centre);
            (farthest, nearest) = (MathF.Max(farthest, distance), MathF.Min(nearest, distance));
            if (v.Body != was)
            {
                (outs, ins, was) = v.Body == Home ? (outs + 1, ins, v.Body) : (outs, ins + 1, v.Body);
                at.Add(distance);
            }
            return false;
        }, maxSeconds: seconds);
        return (outs, ins, at, farthest, nearest);
    }

    /// <summary>Round the moon, its apoapsis <paramref name="apoapsis"/> from its centre, 60 s short of its top.</summary>
    private static VesselState RoundTheMoon(double apoapsis)
    {
        var periapsis = (double)Moon.SeaRadius + 1_500;
        var (out_, across) = Ways();
        var a = (apoapsis + periapsis) / 2;
        var top = Moon.Centre + out_ * (float)apoapsis;
        var speedAtTop = (float)Math.Sqrt(Moon.Mu * (2 / apoapsis - 1 / a));
        var orbit = new Orbit(Moon, top, across * speedAtTop);
        var (p, stars) = orbit.StateAfter(orbit.Period - 60);
        return Craft(0, Moon, p, Moon.OverGround(p, stars), stars);
    }

    [Fact]
    public void A_vessel_out_past_the_sphere_but_short_of_its_leaving_edge_stays_at_the_moon()
    {
        // Its apoapsis half-way from the sphere's radius to where it is left: past the radius it
        // stays, as the hysteresis has it (it would leave were it left at the radius).
        var (outs, ins, _, farthest, _) = Graze(RoundTheMoon(Edge * (1 + (Scene.SphereLeaveFactor - 1) / 2)), 120);
        output.WriteLine($"out to {farthest:0.0} m (its sphere {Edge:0.0} m, left past {LeaveEdge:0.0} m): out {outs}, in {ins}");
        Assert.True(farthest > Edge + 100f, $"it went out only to {farthest:0.0} m: never past the sphere");
        Assert.Equal(0, outs);
        Assert.Equal(0, ins);
    }

    [Fact]
    public void A_vessel_grazing_the_leaving_edge_leaves_once_and_never_comes_back()
    {
        // Its apoapsis just past where the sphere is left: it leaves once, through it, and is
        // never back within the sphere's radius after.
        var (outs, ins, at, farthest, _) = Graze(RoundTheMoon(LeaveEdge + 2.0), 120);
        output.WriteLine($"out to {farthest:0.0} m (left past {LeaveEdge:0.0} m): out {outs}, in {ins}, at {string.Join(", ", at.Select(d => d.ToString("0.0")))} m");
        Assert.Equal(1, outs);
        Assert.Equal(0, ins);
        Assert.True(at[0] >= LeaveEdge - 1f, $"it left {at[0]:0.0} m out, short of the leaving edge");
    }

    [Fact]
    public void A_vessel_grazing_in_from_the_planet_enters_once_and_leaves_only_past_the_leaving_edge()
    {
        // At the planet, passing the moon 30 m inside its sphere's radius: in once, at the radius,
        // and out (if at all in the 120 s) only past the leaving edge, not as it comes back over the radius.
        var (outs, ins, at, _, nearest) = Graze(Approaching(0, 5f, aside: Edge - 30f), 120);
        output.WriteLine($"nearest {nearest:0.0} m (its sphere {Edge:0.0} m, left past {LeaveEdge:0.0} m): in {ins}, out {outs}, at {string.Join(", ", at.Select(d => d.ToString("0.0")))} m");
        Assert.True(nearest < Edge, "it never came within the sphere");
        Assert.Equal(1, ins);
        Assert.InRange(outs, 0, 1);
        Assert.True(at[0] <= Edge + 1f, $"it went in {at[0]:0.0} m out, outside the sphere");
        if (outs == 1)
            Assert.True(at[1] >= LeaveEdge - 1f, $"it left {at[1]:0.0} m out, short of the leaving edge");
    }

    /// <summary>Two upper stages 3 m apart along the moon's way out, the first 1 m inside its sphere, the second 2 m outside, each moving in at 10 m/s among the moon's stars.</summary>
    private static WorldState SideBySide()
    {
        var (out_, across) = Ways();
        var (a, va) = FromMoon(out_ * (Edge - 1f), -out_ * 10f, Home);
        var (b, vb) = FromMoon(out_ * (Edge + 2f), -out_ * 10f, Home);
        return World(Craft(0, Home, a, va, across), Craft(1, Home, b, vb, across));
    }

    /// <summary>
    /// Two side by side, a few metres apart, one just inside the sphere: each changes body at its
    /// own step, the first when its place is inside, the second when its own is; they are never in
    /// the same physics while at two bodies. <paramref name="watched"/>: flown and in a small view
    /// (in the physics); else under formula, nobody watching.
    /// </summary>
    private void CrossEach(bool watched)
    {
        using var sim = new Simulation(Mission.FreePlay, world: SideBySide());
        var (a, b) = (Get(sim, 0), Get(sim, 1));
        IReadOnlyList<Vessel>? views = watched ? [b] : null;
        if (watched)
            sim.Step(new FlightInput(SwitchTo: a, Watched: views));
        var (crossedA, crossedB, steps) = (-1, -1, 0);
        Assert.True(sim.Run((_, _) => new FlightInput(Watched: views), _ =>
        {
            steps++;
            Assert.Equal(!watched, a.OnFormula);
            Assert.Equal(!watched, b.OnFormula);
            if (a.Body != b.Body)
            {
                Assert.True(a.Physics is null || a.Physics != b.Physics, "at two bodies in the same physics");
            }
            if (crossedA < 0 && a.Body == Moon)
                crossedA = steps;
            if (crossedB < 0 && b.Body == Moon)
                crossedB = steps;
            return crossedB >= 0;
        }, maxSeconds: 5), "the second never crossed");
        output.WriteLine($"{(watched ? "in the physics" : "under formula")}: the first crossed at step {crossedA}, the second at {crossedB}");
        Assert.True(crossedA >= 0 && crossedA < crossedB, "the first didn't cross before the second");
        Assert.Same(Moon, a.Body);
        Assert.Same(Moon, b.Body);
        if (watched)
            Assert.Same(a.Physics, b.Physics);
    }

    [Fact]
    public void Two_craft_side_by_side_each_cross_at_their_own_step() => CrossEach(watched: true);

    [Fact]
    public void Two_craft_side_by_side_under_formula_each_cross_at_their_own_step() => CrossEach(watched: false);

    [Fact]
    public void Switching_to_a_lander_on_the_moon_changes_the_view_and_keeps_the_warnings_right()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var lander = Get(sim, 0);
        var pad = sim.Flight.Controlled;
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        var rebased = 0;
        sim.Flight.ViewRebased += _ => rebased++;
        // The warp up, on the pad.
        sim.Step(new FlightInput(Faster: true));
        sim.Step(new FlightInput(Faster: true));
        var rate = sim.Flight.PlayerRate;
        Assert.Equal(4f, rate);

        sim.Step(new FlightInput(SwitchTo: lander));
        sim.Step(default); // made, and flown, the frame after (see the link-up's test)
        Assert.Same(lander, sim.Flight.Controlled);
        Assert.Same(sim.Flight.PhysicsAt(Moon), lander.Physics);
        Assert.Equal(1, rebased);
        Assert.Same(Moon, sim.Flight.Body);
        void Right(string where)
        {
            Assert.Equal(Frame.Surface, sim.Flight.Frame);
            Assert.True(sim.Flight.FrameVelocity.Length() < 0.1f, $"{where}: {sim.Flight.FrameVelocity.Length():0.00} m/s over its ground");
            Assert.Null(sim.Flight.Warning);
            Assert.True(sim.Flight.Comings.IsEmpty, $"{where}: something is coming");
            Assert.Equal(rate, sim.Flight.PlayerRate); // no warning dropped the warp
        }
        Right("on the moon");
        sim.Run(Nobody, _ => { Right("on the moon"); return false; }, maxSeconds: 4);

        sim.Step(new FlightInput(SwitchTo: pad));
        Assert.Same(pad, sim.Flight.Controlled);
        Assert.Equal(2, rebased);
        Assert.Same(Home, sim.Flight.Body);
        Right("back on the pad");
        sim.Run(Nobody, _ => { Right("back on the pad"); return false; }, maxSeconds: 4);
    }

    [Fact]
    public void Switching_to_a_body_with_no_physics_shows_the_link_being_made()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        sim.ShowLinkUp = true;
        var lander = Get(sim, 0);
        var pad = sim.Flight.Controlled;
        var link = sim.Flight.LinkUp;
        string? Reading() => Readout.FlightReadings(sim.Flight).GetValueOrDefault("Link");

        // The click's frame: the link-up begins, drawn first; nothing made yet.
        sim.Step(new FlightInput(SwitchTo: lander));
        Assert.True(link.On);
        Assert.Equal("connecting", Reading());
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        Assert.Same(pad, sim.Flight.Controlled);

        var (frames, madeAt, connectedAt) = (1, -1, -1);
        var readings = new List<string>();
        while (link.On && frames < 600)
        {
            sim.Step(default);
            frames++;
            if (madeAt < 0 && sim.Flight.PhysicsAt(Moon) is not null)
                madeAt = frames;
            if (Reading() is { } reading && (readings.Count == 0 || readings[^1] != reading))
                readings.Add(reading);
            if (connectedAt < 0 && Reading() == "connected")
            {
                connectedAt = frames;
                Assert.NotNull(sim.Flight.PhysicsAt(Moon)); // made by the time it connects
                Assert.Same(lander, sim.Flight.Controlled);
            }
        }
        var seconds = (frames - 1) * Simulation.FrameSeconds; // the real time from the click's frame to its last
        output.WriteLine($"made in frame {madeAt}, connected in frame {connectedAt}, shown for {frames} frames ({seconds:0.000} s real); readings {string.Join(", ", readings)}");
        Assert.Equal(2, madeAt); // the frame after it was first drawn
        // Its fixed length, though the physics was made at once: no shorter, nor a frame longer.
        Assert.InRange(seconds, Scene.LinkUpSeconds - 1e-4, Scene.LinkUpSeconds + Simulation.FrameSeconds + 1e-4);
        Assert.Equal(["connecting", "connected"], readings);
        Assert.Null(Reading());
        Assert.Same(lander, sim.Flight.Controlled);
        Assert.Same(sim.Flight.PhysicsAt(Moon), lander.Physics);

        // To a body whose physics is made: a link-up all the same, over as soon as its length has gone.
        sim.Step(new FlightInput(SwitchTo: pad));
        Assert.True(link.On);
        Assert.Equal("connecting", Reading());
        sim.Run(Nobody, _ => !link.On, maxSeconds: 3);
        Assert.Same(pad, sim.Flight.Controlled);
        Assert.Null(Reading());
    }

    [Fact]
    public void A_view_given_to_a_vessel_at_a_body_with_no_physics_shows_the_link_being_made_and_makes_it_once_the_view_is_drawn_with_it()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var lander = Get(sim, 0);
        var views = new Views();
        string? Reading() => Readout.FlightReadings(sim.Flight).GetValueOrDefault($"Link {WorldLog.Named(lander)}");
        sim.Step(default);
        Assert.True(lander.OnFormula, "the lander loads under formula, far from the pad");
        Assert.Null(sim.Flight.PhysicsAt(Moon));

        // Frame N: the HUD draws the small views, then its strip's "Add view" (under them) opens this
        // one: its link-up begun over it, nothing made yet and the lander left under formula.
        views.Open(lander, sim.Flight);
        Assert.Contains(views.Small, view => view.Vessel == lander);
        var link = sim.Flight.ViewLink(lander);
        Assert.NotNull(link);
        Assert.True(link.Waiting);
        Assert.Equal(1f, link.Static);
        Assert.Equal("connecting", Reading());
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        Assert.True(lander.OnFormula);
        Assert.False(sim.Flight.LinkUp.On); // the main view's own is for a switch: none

        // Frame N+1: its run, the lander watched (as the view's vessel is), makes nothing and leaves
        // the lander under formula; then the HUD draws the new view for the first time, with the
        // static over it (still waiting, connecting): the frame on the screen while the next makes it.
        sim.Step(new FlightInput(Watched: [lander]));
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        Assert.True(lander.OnFormula);
        Assert.True(link.Waiting);
        Assert.Equal(1f, link.Static);
        Assert.Equal("connecting", Reading());

        // Frame N+2: the moon's physics made behind that static, the lander brought into it once.
        sim.Step(new FlightInput(Watched: [lander]));
        var moon = sim.Flight.PhysicsAt(Moon);
        Assert.NotNull(moon);
        Assert.False(lander.OnFormula);
        Assert.Same(moon, lander.Physics);
        var craft = lander.Craft;
        var readings = new List<string> { "connecting" };
        var frames = 2;
        while (link.On && frames < 600)
        {
            sim.Step(new FlightInput(Watched: [lander]));
            frames++;
            Assert.Same(craft, lander.Craft); // never handed out and in again
            Assert.False(lander.OnFormula);
            if (Reading() is { } reading && readings[^1] != reading)
                readings.Add(reading);
        }
        var seconds = frames * Simulation.FrameSeconds; // the real time since the click, made after the frame before these
        output.WriteLine($"shown for {frames} frames after the click ({seconds:0.000} s real); readings {string.Join(", ", readings)}");
        Assert.Equal(["connecting", "connected"], readings);
        Assert.InRange(seconds, Scene.LinkUpSeconds - 1e-4, Scene.LinkUpSeconds + Simulation.FrameSeconds + 1e-4); // its fixed length
        Assert.Null(Reading());
        Assert.Null(sim.Flight.ViewLink(lander));
        Assert.Same(moon, lander.Physics);
    }

    [Fact]
    public void A_view_closed_while_its_link_up_waits_lets_it_go_and_nothing_is_made_for_it()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        var lander = Get(sim, 0);
        var views = new Views();
        string? Reading() => Readout.FlightReadings(sim.Flight).GetValueOrDefault($"Link {WorldLog.Named(lander)}");
        sim.Step(default);
        Assert.True(lander.OnFormula);

        // Opened, drawn once with its static, then closed (its close button) before its link-up is due.
        views.Open(lander, sim.Flight);
        sim.Step(new FlightInput(Watched: [lander]));
        Assert.True(sim.Flight.ViewLink(lander) is { Waiting: true });
        views.Close(views.Small.Single(view => view.Vessel == lander), sim.Flight);
        Assert.Null(sim.Flight.ViewLink(lander));
        Assert.Null(Reading());

        // Nothing made for it, the lander left under formula, however long on.
        for (var i = 0; i < 60; i++)
            sim.Step(default);
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        Assert.True(lander.OnFormula);
        Assert.Null(Reading());

        // Opened again: a link-up of its own from the start, nothing left over from the last.
        views.Open(lander, sim.Flight);
        var link = sim.Flight.ViewLink(lander);
        Assert.NotNull(link);
        Assert.True(link.Waiting);
        Assert.Equal(1f, link.Static);
        Assert.Equal("connecting", Reading());

        // And one closed while its picture clears: let go at once, no reading or static left.
        sim.Step(new FlightInput(Watched: [lander]));
        sim.Step(new FlightInput(Watched: [lander]));
        Assert.NotNull(sim.Flight.PhysicsAt(Moon));
        Assert.True(link.On);
        views.Close(views.Small.Single(view => view.Vessel == lander), sim.Flight);
        Assert.False(link.On);
        Assert.Null(Reading());
    }

    [Fact]
    public void A_view_dropped_from_a_full_strip_while_its_link_up_waits_lets_it_go()
    {
        // Five satellites round the planet and a lander on the moon: four satellites' views and the
        // lander's fill the strip, then a switch to the fifth satellite (no view) puts the pad's
        // rocket into the strip first, and the newest, the lander's, is dropped.
        var satellites = Enumerable.Range(1, 5).Select(id => UnwatchedTests.Satellite(id, 10000f, 0f, 40f * id)).ToArray();
        using var sim = new Simulation(Mission.FreePlay, world: World([OnTheMoon.Lander(0, OnTheMoon.LandingSpot), .. satellites]));
        sim.ShowLinkUp = true;
        var lander = Get(sim, 0);
        var views = new Views();
        var camera = new ChaseCamera(null);
        sim.Step(default);
        views.Update(Simulation.FrameSeconds, sim.Flight, camera);
        for (var id = 1; id <= 4; id++)
            views.Open(Get(sim, id), sim.Flight);
        sim.Step(new FlightInput(SwitchTo: Get(sim, 5))); // its link-up begun: it is flown (and the strip changes with it) the frame after
        views.Open(lander, sim.Flight);
        Assert.True(views.Full);
        Assert.True(sim.Flight.ViewLink(lander) is { Waiting: true });

        sim.Step(default);
        views.Update(Simulation.FrameSeconds, sim.Flight, camera);
        Assert.DoesNotContain(views.Small, view => view.Vessel == lander);
        Assert.Null(sim.Flight.ViewLink(lander));

        for (var i = 0; i < 60; i++)
            sim.Step(default);
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        Assert.True(lander.OnFormula);
    }

    [Fact]
    public void A_view_given_to_a_vessel_at_a_body_whose_physics_is_made_shows_no_link_up()
    {
        // Far round the planet from the pad, under formula: the planet's physics is made (the pad's
        // rocket is in it), so the view brings it in at once.
        using var sim = new Simulation(Mission.FreePlay, world: World(UnwatchedTests.Satellite(0, 10000f, 0f, 240f)));
        var satellite = Get(sim, 0);
        sim.Step(default);
        Assert.True(satellite.OnFormula);
        Assert.NotNull(sim.Flight.PhysicsAt(Home));
        var views = new Views();
        views.Open(satellite, sim.Flight);
        Assert.Null(sim.Flight.ViewLink(satellite));
        Assert.False(satellite.OnFormula);
        Assert.Same(sim.Flight.PhysicsAt(Home), satellite.Physics);
        Assert.DoesNotContain(Readout.FlightReadings(sim.Flight).Keys, key => key.StartsWith("Link"));
    }

    [Fact]
    public void The_link_up_runs_on_real_time_for_its_fixed_length_or_until_ready_if_later()
    {
        using var sim = new Simulation(Mission.FreePlay);
        var vessel = sim.Flight.Controlled;
        var clear = Scene.LinkUpSeconds * Scene.LinkUpClearShare;

        // Ready at once: connecting for its length less the clearing, then clearing, over at its length.
        var link = new LinkUp();
        link.Begin(vessel, 10.0, frame: 5);
        Assert.True(link.Waiting && !link.Due(5) && link.Due(6));
        link.Ready(10.02);
        link.Step(10.0 + Scene.LinkUpSeconds - clear - 0.01);
        Assert.True(link.Connecting);
        Assert.Equal(1f, link.Static);
        link.Step(10.0 + Scene.LinkUpSeconds - clear / 2);
        Assert.Equal("connected", link.Reading);
        Assert.InRange(link.Static, 0.4f, 0.6f);
        link.Step(10.0 + Scene.LinkUpSeconds - 0.001);
        Assert.True(link.On); // shown to the end of its length, though ready at once
        link.Step(10.0 + Scene.LinkUpSeconds + 0.001);
        Assert.False(link.On);
        Assert.Null(link.Reading);

        // A physics slow to make: connecting until it is, however long.
        link.Begin(vessel, 0.0, frame: 1);
        link.Step(2.0);
        Assert.True(link.Connecting);
        Assert.Equal("connecting", link.Reading);
        link.Ready(2.5);
        link.Step(2.5 + clear - 0.01);
        Assert.Equal("connected", link.Reading);
        link.Step(2.5 + clear + 0.001);
        Assert.False(link.On);
    }

    [Fact]
    public void Nothing_is_allocated_a_frame_crossing_and_after()
    {
        // Flying the upper stage across into the moon's sphere (in the physics, its moon's physics
        // made ahead), with two side by side under formula crossing beside it: a frame's
        // parts, crossing and after, as the flight's step has them, at the frame's own time (each
        // body's physics' step, Worlds.Step, counted apart: Jolt's step under it allocates a little
        // of its own; see TODO.md). A crossing flown first in a world of its own: Jolt's first
        // removal of a body in a process allocates once (32 B), which no frame repeats.
        WarmUp();
        var (out_, across) = Ways();
        var (a, va) = FromMoon(-across * (Edge - 0.5f), across * 10f, Home);
        var (b, vb) = FromMoon(-across * (Edge + 8f), across * 10f, Home);
        using var sim = new Simulation(Mission.FreePlay, world: World(Approaching(0, 1.6f), Craft(1, Home, a, va, out_), Craft(2, Home, b, vb, out_)));
        var flown = Get(sim, 0);
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Run(Nobody, _ => sim.Flight.PhysicsAt(Moon) is not null, maxSeconds: 1); // asked ahead, and made
        Assert.NotNull(sim.Flight.PhysicsAt(Moon));
        var flight = sim.Flight;
        var world = flight.WorldVessels;
        var controls = new List<RocketControl>(16);
        const float Dt = Simulation.FrameSeconds;
        var (jolt, steps) = (0L, 0);
        var start = flight.WorldTime;
        void Frame(int i)
        {
            var t = start + (i + 1) * (double)Dt;
            for (var k = 0; k < world.Count; k++)
            {
                var v = world[k];
                if (v.State == FlightState.Flying && v.Guided)
                    v.Run(Dt, Vector3.Zero, null, t, v.Velocity, Scene.HoldFloorSpeed);
                v.MoveLegs(Dt);
            }
            controls.Clear();
            for (var k = 0; k < world.Count; k++)
                if (world[k].Control is { } control)
                    controls.Add(control);
            var before = GC.GetAllocatedBytesForCurrentThread();
            flight.Worlds.Step(Dt, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(controls), flown.Body, flown.Position, t);
            (jolt, steps) = (jolt + GC.GetAllocatedBytesForCurrentThread() - before, steps + 1);
            for (var k = 0; k < world.Count; k++)
                Formula.Advance(world[k], Dt, t);
            for (var k = 0; k < world.Count; k++)
                world[k].AfterPhysics(Dt, t);
            flight.Cross(t);
            flight.AskAhead(t);
            flight.LookAhead();
            flight.Unwatched.Update(Dt, null, flown.CentreOfMass);
        }
        Frame(0); // warmed up
        (jolt, steps) = (0L, 0);
        var bodiesBefore = (flown.Body, world[2].Body);
        var all = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= 120; i++)
            Frame(i);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - all - jolt;
        output.WriteLine($"120 frames crossing and after: {bytes} bytes (each body's physics' steps apart: {jolt} bytes in {steps})");
        Assert.Equal((Home, Home), bodiesBefore);
        Assert.Same(Moon, flown.Body);
        Assert.Same(flight.PhysicsAt(Moon), flown.Physics);
        Assert.Same(Moon, world[1].Body);
        Assert.Same(Moon, world[2].Body);
        Assert.Equal(0, bytes);

        // And the link-up's own path through the frame (RunFrame: its clock, the frame it is due
        // in, the switch it waits on, its clearing), frame by frame as the game runs it: a switch to
        // a lander on the moon, whose physics isn't made. Each frame's bytes, the click's and the
        // link-up's while it is shown, against as many frames with no link-up, where the same
        // bodies' physics are stepped: Jolt's step allocates its own a little (the same each
        // step), so the frames of the link-up allocate no more than those without it. The frame
        // it is due in makes the moon's physics (behind the link-up), and is left out.
        using var linked = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        linked.ShowLinkUp = true;
        var lander = Get(linked, 0);
        var pad = linked.Flight.Controlled;
        long Frame1(FlightInput input)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            linked.Step(input);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        for (var i = 0; i < 30; i++)
            Frame1(default); // warmed up
        var plain = Frame1(default);
        var click = Frame1(new FlightInput(SwitchTo: lander));
        Assert.True(linked.Flight.LinkUp.Waiting);
        Frame1(default); // due: the moon's physics made, the lander flown
        Assert.Same(lander, linked.Flight.Controlled);
        var (during, frames) = (0L, 0);
        while (linked.Flight.LinkUp.On)
            (during, frames) = (during + Frame1(default), frames + 1);
        var without = 0L;
        for (var i = 0; i < frames; i++)
            without += Frame1(default);
        output.WriteLine($"through RunFrame: the click's frame {click} bytes (one without, {plain}); the link-up's {frames} frames after {during} bytes, as many after it {without} bytes");
        Assert.True(click <= plain, $"the click's frame allocated {click} bytes, against {plain} without");
        Assert.True(during <= without, $"the link-up's frames allocated {during} bytes, against {without} without it");
        _ = pad;
    }

    [Fact]
    public void Any_switch_or_new_flight_ends_the_link_up()
    {
        using var sim = new Simulation(Mission.FreePlay, world: World(OnTheMoon.Lander(0, OnTheMoon.LandingSpot)));
        sim.ShowLinkUp = true;
        var lander = Get(sim, 0);
        var pad = sim.Flight.Controlled;
        var link = sim.Flight.LinkUp;

        // Switched back to the pad (its physics made) while the link to the lander clears: its own
        // link-up replaces that one (every switch has one), and the pad is flown the frame after.
        sim.Step(new FlightInput(SwitchTo: lander));
        sim.Run(Nobody, _ => link.On && !link.Connecting, maxSeconds: 2);
        Assert.Equal("connected", link.Reading);
        sim.Step(new FlightInput(SwitchTo: pad));
        Assert.True(link.Waiting);
        Assert.Same(pad, link.Vessel);
        sim.Step(default);
        Assert.Same(pad, sim.Flight.Controlled);
        sim.Run(Nobody, _ => !link.On, maxSeconds: 3);
        Assert.False(link.On);

        // A new flight started while it is still waiting (the lander flown in that frame, then the
        // new rocket): none over the new rocket.
        sim.Run(Nobody, _ => false, maxSeconds: Scene.PhysicsIdleSeconds + 1); // the moon's physics released, nothing in it
        Assert.Null(sim.Flight.PhysicsAt(Moon));
        sim.Step(new FlightInput(SwitchTo: lander));
        Assert.True(link.Waiting);
        sim.Step(new FlightInput(Choose: Mission.FreePlay));
        Assert.False(link.On);
        Assert.Same(Home, sim.Flight.Body);
        Assert.NotSame(lander, sim.Flight.Controlled);
        Assert.Null(Readout.FlightReadings(sim.Flight).GetValueOrDefault("Link"));
    }

    [Fact]
    public void Crossing_under_formula_drops_its_collisions_at_the_body_it_left_and_finds_those_at_the_new()
    {
        // Two under formula at the planet, one behind the other on a line into the moon's sphere:
        // the first 0.1 m outside it at 100 m/s, the second 60 m further out at 400 m/s, closing
        // at 300 m/s, to hit some 20 m inside it. The collision found at the planet is dropped as
        // the first crosses (in the first step, the other 60 m further out: their places are
        // then in two frames); once the second crosses too, it is found again at the moon, and both
        // are wrecked there, as they would be at one body.
        var (out_, _) = Ways();
        var (a, va) = FromMoon(out_ * (Edge + 0.1f), -out_ * 100f, Home);
        var (b, vb) = FromMoon(out_ * (Edge + 60.1f), -out_ * 400f, Home);
        using var sim = new Simulation(Mission.FreePlay, world: World(Craft(0, Home, a, va, -out_), Craft(1, Home, b, vb, -out_)));
        var (first, second) = (Get(sim, 0), Get(sim, 1));
        Assert.True(first.OnFormula && second.OnFormula);
        sim.Step(default);
        Assert.Same(Moon, first.Body);
        Assert.Same(Home, second.Body);
        Assert.Equal(0, sim.Flight.Unwatched.CollisionsAhead);
        Assert.True(sim.Run(Nobody, _ => first.State == FlightState.Crashed && second.State == FlightState.Crashed, maxSeconds: 1), "they never hit at the moon");
        output.WriteLine($"{first.CrashReason} / {second.CrashReason}, at {sim.Time:0.000} s");
        Assert.Same(Moon, second.Body);
        Assert.StartsWith("Hit another vessel", first.CrashReason);
        Assert.StartsWith("Hit another vessel", second.CrashReason);
    }

    [Fact]
    public void Every_switch_shows_the_link_up_even_where_the_physics_is_made()
    {
        using var sim = new Simulation(Mission.FreePlay);
        sim.ShowLinkUp = true;
        sim.Step(new FlightInput(Launch: true, FullThrottle: true));
        sim.Run((_, _) => new FlightInput(FullThrottle: true), f => f.Ground > 200f, maxSeconds: 60);
        sim.Step(new FlightInput(Stage: true));
        var first = sim.Flight.Controlled;
        var second = sim.Flight.WorldVessels.First(v => v != first && v.State != FlightState.Crashed);
        Assert.NotNull(sim.Flight.PhysicsAt(first.Body));

        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.True(sim.Flight.LinkUp.On);
        var frames = 0;
        while (sim.Flight.LinkUp.On && frames++ < 600)
            sim.Step(default);
        Assert.Same(second, sim.Flight.Controlled);
        Assert.False(sim.Flight.LinkUp.On);
    }

    // ---- At the formula's rates ----

    /// <summary>
    /// The leg into the moon's sphere of <paramref name="v"/>'s path from where its motion is now (in
    /// the physics, its world's, behind the clock by the step's leftover), and that world time.
    /// </summary>
    private static (PathLeg Leg, double Since) MoonLegOf(Simulation sim, Vessel v, double horizon = double.PositiveInfinity)
    {
        var since = MotionTime(sim, v);
        var (at, velocity) = (v.TrueCentreOfMass.ToSingle(), v.TrueVelocity.ToSingle());
        var legs = new List<PathLeg>(Scene.PatchedLegs);
        Patched.Legs(v.Body, at, v.Body.AmongStars(at, velocity), since, legs, horizon);
        Assert.True(legs.Count > 1 && legs[1].Body == Moon, "its path should go into the moon's sphere");
        return (legs[1], since);
    }

    private static double MotionTime(Simulation sim, Vessel v) => v.Physics is null ? sim.Flight.WorldTime : sim.Flight.WorldTime - sim.Worlds.Lag;

    /// <summary>
    /// <paramref name="v"/>, at the moment its motion is at, within a metre of where
    /// <paramref name="leg"/> (of a path from world time <paramref name="since"/>) has it then, over
    /// its body's ground: so it was handed across at the leg's start, not a window's end later.
    /// </summary>
    private void AssertOnTheLeg(Simulation sim, Vessel v, PathLeg leg, double since)
    {
        Assert.Same(leg.Body, v.Body);
        var t = MotionTime(sim, v);
        var (stars, _) = leg.Orbit.StateAfter(t - leg.Start);
        var expected = (Vector3Double)leg.Body.Turned(stars, -(t - since)); // among its stars as its ground was at since: back over its ground at t
        var off = (v.TrueCentreOfMass - expected).Length();
        output.WriteLine($"crossing at {leg.Start:0.000} s; at {t:0.000} s, {t - leg.Start:0.0} s on, {off:0.000} m from the leg's place");
        Assert.True(off < 1.0, $"{off:0.000} m from where the leg has it");
    }

    [Fact]
    public void A_crossing_into_the_moon_s_sphere_at_the_top_rate_is_handed_across_at_its_moment_and_the_warp_goes_on()
    {
        var (sim, flown) = WarpScenes.MoonTransfer();
        using var _ = sim;
        var flight = sim.Flight;
        var (leg, since) = MoonLegOf(sim, flown);
        FormulaRateTests.ToTheTop(flight);
        for (var i = 0; i < 10 && flown.Body != Moon; i++)
            sim.Step(default);
        Assert.Same(Moon, flown.Body);
        Assert.True(flown.OnFormula);
        Assert.Equal(Scene.TopRate, flight.PlayerRate);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
        AssertOnTheLeg(sim, flown, leg, since);
    }

    [Fact]
    public void A_satellite_under_formula_crossing_at_the_top_rate_crosses_at_its_moment()
    {
        // The transfer nobody watches, the flown satellite in a low orbit round the planet.
        var (state, time) = MoonTrip.Transferring(0, WarpScenes.TransferSeconds);
        var (sim, _) = WarpScenes.Flying(MoonTrip.World(time, InOrbit.Round(Home, 1, WarpScenes.LowOrbitHeight), state), 1);
        using var _s = sim;
        var flight = sim.Flight;
        var transfer = Get(sim, 0);
        Assert.True(transfer.OnFormula);
        var (leg, since) = MoonLegOf(sim, transfer);
        FormulaRateTests.ToTheTop(flight);
        for (var i = 0; i < 10 && transfer.Body != Moon; i++)
            sim.Step(default);
        Assert.Same(Moon, transfer.Body);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
        AssertOnTheLeg(sim, transfer, leg, since);
    }

    [Fact]
    public void A_crossing_just_past_where_its_orbit_was_looked_along_to_is_still_handed_across_at_its_moment()
    {
        // The upper stage at the planet, nobody watching it, its crossing into the moon's sphere
        // just past where its formula orbit is first looked along to (a load's start, plus
        // Scene.MapCollisionHorizonSeconds): its state there put a hair outside the sphere, coming
        // in at Speed past the moon's side, and run back by Kepler round the planet to world time 0.
        const double Past = 0.3;
        var crosses = Scene.MapCollisionHorizonSeconds + Past;
        var (out_, across) = Ways();
        const float Aside = 4_500f;
        var into = MathF.Sqrt(Edge * Edge - Aside * Aside);
        var (there, moving) = FromMoon(out_ * (into + 0.5f) + across * Aside, -out_ * Speed, Home, crosses);
        var orbit = new Orbit(Home, there, Home.AmongStars(there, moving), findEnd: false);
        var (back, stars) = orbit.StateAfter(-crosses); // among the stars as the ground was then
        var at = Home.Turned(back, crosses);
        var start = Craft(0, Home, at, Home.OverGround(at, Home.TurnedDirection(stars, crosses)), -out_);
        var (sim, _) = WarpScenes.Flying(World(InOrbit.Round(Home, 1, WarpScenes.LowOrbitHeight), start), 1);
        using var _s = sim;
        var flight = sim.Flight;
        var v = Get(sim, 0);
        Assert.True(v.OnFormula);
        var lookedTo = v.FormulaOrbit.LookedTo;
        var (leg, since) = MoonLegOf(sim, v, horizon: sim.Flight.WorldTime + 2 * Scene.MapCollisionHorizonSeconds);
        output.WriteLine($"looked along to {lookedTo:0.000} s; crosses at {leg.Start:0.000} s");
        Assert.InRange(leg.Start, lookedTo, lookedTo + 1.0); // in the slot just past the look's end
        FormulaRateTests.ToTheTop(flight);
        // Whole top-rate frames to within one of it, then one ending 30 s past it (it passes
        // through the sphere's side within a frame).
        while (leg.Start + 30.0 - flight.WorldTime > Scene.TopRate * Simulation.FrameSeconds)
            sim.Step(default);
        sim.RealFrame = (float)((leg.Start + 30.0 - flight.WorldTime) / Scene.TopRate);
        sim.Step(default);
        Assert.Same(Moon, v.Body);
        Assert.Equal(Scene.TopRate, flight.TimeScale);
        AssertOnTheLeg(sim, v, leg, since);
    }

    [Fact]
    public void A_warp_to_past_the_moon_s_edge_crosses_on_the_way_and_stops_on_its_time()
    {
        var (sim, flown) = WarpScenes.MoonTransfer();
        using var _ = sim;
        var flight = sim.Flight;
        var (leg, since) = MoonLegOf(sim, flown);
        var to = leg.Start + 30.0;
        sim.Step(new FlightInput(WarpTo: to));
        for (var i = 0; i < 10 && flight.WarpingTo is not null; i++)
            sim.Step(default);
        Assert.Null(flight.WarpingTo);
        output.WriteLine($"warped to {flight.WorldTime:0.000000} s, asked {to:0.000000} s");
        Assert.Equal(to, flight.WorldTime, 3);
        Assert.Equal(1f, flight.PlayerRate);
        AssertOnTheLeg(sim, flown, leg, since);
    }
}
