using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The orbit mission, end to end: the two-stage rocket flown into a stable orbit, kept there for a
/// whole lap of the planet, and the ways it can't get there; and an orbit round any body
/// (<see cref="Orbit"/> given its body): round the moon by the moon's pull, and round the planet
/// far from the moon one leg, the planet's circle by Kepler.
/// </summary>
public sealed class OrbitTests(ITestOutputHelper output)
{
    [Fact]
    public void An_orbit_round_the_moon_has_the_moons_period()
    {
        // 500 m up over the moon's equator, east at the circular speed among its stars: clear of
        // its craters' rims, and far from the far side's mountain.
        var moon = GroundBody.Moon;
        var r = moon.SeaRadius + 500.0;
        var position = moon.Centre + Vector3.UnitY * (float)r;
        var velocity = moon.East(position) * (float)Math.Sqrt(moon.Mu / r);
        var orbit = new Orbit(moon, position, velocity);

        var expected = Math.Tau * Math.Sqrt(r * r * r / SolarSystem.MoonMu);
        output.WriteLine($"period {orbit.Period:0.000} s, expected {expected:0.000} s; eccentricity {orbit.Eccentricity:0.000000}");
        Assert.Same(moon, orbit.Body);
        Assert.InRange(orbit.Period, expected - 0.1, expected + 0.1);
        Assert.True(orbit.Closed, "a circle 500 m up round the moon came down");
        Assert.InRange(orbit.PeriapsisDistance, r - 1, r + 1);
    }

    [Fact]
    public void An_orbit_far_from_the_moon_is_one_leg_on_the_planets_circle()
    {
        // A low circle round the planet, far inside the moon's way round it.
        var planet = GroundBody.Planet;
        var r = planet.SeaRadius + 4000f;
        var up = Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f));
        var position = planet.Centre + up * r;
        var velocity = Vector3.Normalize(Vector3.Cross(up, planet.NorthPole)) * MathF.Sqrt(planet.Mu / r);
        const double Now = 1234.5;
        var legs = new List<PathLeg>();
        Patched.Legs(planet, position, velocity, Now, legs);

        Assert.Single(legs);
        Assert.Same(planet, legs[0].Body);
        Assert.Equal(Now, legs[0].Start);
        Assert.True(double.IsPositiveInfinity(legs[0].End));
        // Worked out apart from Orbit: a circle of radius r at the circular speed, its period
        // 2 pi sqrt(r^3 / mu), its place after t the start turned about the orbit's normal by its
        // rate times t (in double, among the stars as the orbit is).
        var orbit = legs[0].Orbit;
        var period = Math.Tau * Math.Sqrt((double)r * r * r / planet.Mu);
        Assert.InRange(orbit.Period, period * (1 - 1e-5), period * (1 + 1e-5));
        Assert.True(orbit.Closed, "a circle 4 km up round the planet came down");
        Assert.InRange(orbit.Eccentricity, 0, 1e-3);
        var normal = Vector3Double.Normalize(Vector3Double.Cross((Vector3Double)(position - planet.Centre), (Vector3Double)velocity));
        var worst = 0.0;
        for (var i = 0; i <= 100; i++)
        {
            var seconds = period * i / 100;
            var turned = QuaternionDouble.FromAxisAngle(normal, Math.Tau * i / 100).Rotate((Vector3Double)(position - planet.Centre));
            worst = Math.Max(worst, ((Vector3Double)orbit.PositionAfter(seconds) - ((Vector3Double)planet.Centre + turned)).Length());
        }
        output.WriteLine($"period {orbit.Period:0.000} s, Kepler's {period:0.000} s; at worst {worst * 100:0.00} cm off the circle over a lap");
        Assert.True(worst < 0.02, $"a point {worst * 100:0.00} cm off the circle");
    }
    [PlaythroughFact(Long = true)]
    [Trait("Kind", "Playthrough")]
    [Trait("Run", "Long")]
    public void Autopilot_reaches_a_stable_orbit_and_it_stays_up_for_a_whole_lap()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var pilot = new OrbitAutopilot();

        Assert.True(sim.Run(pilot.Fly, f => f.AchievedOrbit || f.State == FlightState.Crashed, maxSeconds: 300), "never reached orbit");
        Report(sim, "in orbit");

        var flight = sim.Flight;
        Assert.Equal(FlightState.Flying, flight.State);
        Assert.Equal(1, flight.Stage);
        Assert.True(flight.Periapsis > Scene.AtmosphereHeight, $"periapsis only {flight.Periapsis:0} m");
        Assert.True(flight.Run!.Succeeded);
        Assert.True(flight.Fuel > 0f, "the upper stage ran dry getting there: the margin is too thin");

        // A lap and a bit, coasting: a stable orbit never dips back into the atmosphere (whose
        // top is above the sea, not the ground under it).
        var lowest = float.MaxValue;
        var lap = 2 * Math.PI * (Planet.SeaRadius + flight.Apoapsis) / Planet.CircularSpeed(flight.Apoapsis);
        sim.Run(pilot.Fly, f =>
        {
            lowest = MathF.Min(lowest, Planet.Altitude(f.Position));
            return f.State != FlightState.Flying;
        }, maxSeconds: lap * 1.1);
        Report(sim, "after a lap");

        Assert.Equal(FlightState.Flying, flight.State);
        Assert.True(lowest > Scene.AtmosphereHeight, $"dipped to {lowest:0} m");
    }

    [PlaythroughFact(Long = false)]
    [Trait("Kind", "Playthrough")]
    public void The_upper_stage_reaches_orbit_and_the_booster_lands_on_the_drone_ship_in_the_same_flight()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        // Drop the booster at its landing-fuel mark, and aim for a higher orbit than usual: the
        // coast up to it leaves time to fly the booster down before circularising, one at a time,
        // as a player would.
        var reserve = Vehicle.TwoStage.Stages[0].LandingReserve;
        var orbit = new OrbitAutopilot { StageAtFuel = reserve, TargetApoapsis = Scene.AtmosphereHeight + 1500f };
        var lander = Autopilot.ReturningBooster();

        // Up through separation, until the upper stage's burn ends and it coasts up to the top of its arc.
        Assert.True(sim.Run(orbit.Fly, f => f.Stage == 1 && f.Throttle == 0f, maxSeconds: 120), "the upper stage never finished its climb");
        var upper = flight.Controlled;
        var booster = flight.Vessels.Single(v => v != upper);
        Report(sim, "upper stage coasting");
        output.WriteLine($"  booster: altitude {booster.Ground:0} m, {booster.DistanceToLandingSite:0} m from the drone ship, fuel {booster.Fuel * 100:0}%");
        Assert.InRange(booster.Fuel, reserve - 0.02f, reserve);

        // Over to the booster, falling back: land it on the drone ship's deck.
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(booster, flight.Controlled);
        Assert.True(sim.Run(lander.Fly, _ => booster.State != FlightState.Flying, maxSeconds: 120), "the booster never came down");
        Report(sim, "booster down");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.LandedOnTarget, $"landed {booster.DistanceToLandingSite:0.0} m from the drone ship's centre");
        Assert.True(booster.Fuel > 0.05f, $"only {booster.Fuel * 100:0}% fuel left: the reserve is too thin");
        Assert.True(Planet.Altitude(upper.Position) > Scene.AtmosphereHeight && upper.VerticalSpeed > 0f, "the upper stage passed the top of its arc while the booster landed");

        // Back to the upper stage, to circularise at the top.
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(upper, flight.Controlled);
        Assert.True(sim.Run(orbit.Fly, f => f.AchievedOrbit || f.State != FlightState.Flying, maxSeconds: 120), "never reached orbit");
        Report(sim, "in orbit");
        Assert.True(flight.Run!.Succeeded);
        Assert.All(flight.Mission.Steps.Select((step, i) => (step, i)), x => Assert.True(flight.Run!.Progress.IsDone(x.i), $"not done: {x.step.Label}"));
        Assert.True(upper.Fuel > 0.3f, $"the upper stage only has {upper.Fuel * 100:0}% left");
        output.WriteLine($"score {flight.Run!.Score}");
    }

    [PlaythroughFact(Long = false)]
    [Trait("Kind", "Playthrough")]
    public void Landing_the_booster_off_the_target_still_succeeds_but_scores_less()
    {
        var onPad = FlyOrbitAndReturn(target: null);
        // Down safely, but off the target: on the drone ship's deck, past its target's edge. (Not
        // beside the ship: there a booster comes down in the sea.)
        var offPad = FlyOrbitAndReturn(Scene.DroneShipCentre + Planet.North(Scene.DroneShipCentre) * (Scene.LandingTargetRadius + 2f));
        var crashed = FlyOrbitAndReturn(target: null, flyBooster: false); // left alone, it falls and is destroyed
        output.WriteLine($"scores: on the pad {onPad.Score}, off it {offPad.Score}, crashed {crashed.Score}");

        Assert.Equal([true, true], onPad.Bonuses);
        Assert.Equal([true, false], offPad.Bonuses);
        Assert.Equal([false, false], crashed.Bonuses);
        Assert.NotNull(crashed.Score); // the mission succeeds whatever happens to the booster
        Assert.True(onPad.Score > offPad.Score && offPad.Score > crashed.Score, "landing on the pad should score most, and a crash least");
    }

    /// <summary>
    /// The mission with the booster brought back as a player would (see <see cref="OrbitAndReturnPilot"/>),
    /// to <paramref name="target"/> (null: the drone ship), or not flown at all. Returns the
    /// mission's score and which of its bonuses were done, once in orbit with the booster down.
    /// </summary>
    private (int? Score, bool[] Bonuses) FlyOrbitAndReturn(System.Numerics.Vector3? target, bool flyBooster = true)
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        Pilot pilot = flyBooster
            ? new OrbitAndReturnPilot(target).Fly
            : new OrbitAutopilot { StageAtFuel = Vehicle.TwoStage.Stages[0].LandingReserve, TargetApoapsis = Scene.AtmosphereHeight + 1500f }.Fly;
        var flight = sim.Flight;
        Assert.True(sim.Run(pilot, f => f.Vessels.Count > 1 && f.Vessels.All(v => v.State != FlightState.Flying || v.AchievedOrbit), maxSeconds: 300),
            "never got the upper stage into orbit with the booster down");
        Assert.True(flight.Run!.Succeeded);
        return (flight.Run!.Score, [.. flight.Mission.Steps.Select((s, i) => (s, i)).Where(x => x.s.Optional).Select(x => flight.Run!.Progress.IsDone(x.i))]);
    }

    [Fact]
    public void Without_dropping_the_booster_it_cannot_reach_orbit()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var pilot = new OrbitAutopilot { Stage = false };

        sim.RunToEnd(pilot.Fly, maxSeconds: 400);
        Report(sim, "without staging");

        Assert.False(sim.Flight.AchievedOrbit);
        Assert.Equal(0, sim.Flight.Stage);
        Assert.Equal(FlightState.Crashed, sim.Flight.State); // it falls back
        Assert.True(sim.Flight.MaxAltitude < Scene.AtmosphereHeight + 1500, $"climbed to {sim.Flight.MaxAltitude:0} m");
    }

    [Fact]
    public void The_upper_stage_lights_a_moment_after_separation()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var pilot = new OrbitAutopilot { Stage = false };
        Assert.True(sim.Run(pilot.Fly, f => f.State == FlightState.Flying && f.Ground > 500f, maxSeconds: 60));
        Assert.True(sim.Flight.EngineLit); // the booster, still burning

        sim.Step(new FlightInput(Stage: true));
        Assert.Equal(1, sim.Flight.Stage);
        Assert.Equal(StageKind.Upper, sim.Flight.CurrentStage.Kind);
        Assert.False(sim.Flight.EngineLit, "it lit while still inside the interstage");

        sim.Run((_, _) => default, _ => false, maxSeconds: Scene.StageIgnitionSeconds + 0.1);
        Assert.True(sim.Flight.EngineLit);
        Assert.False(sim.Flight.HasLegs);
    }

    [Fact]
    public void The_dropped_booster_falls_back_and_is_destroyed()
    {
        // Downrange it comes down in the sea, far too fast to splash down: it is lost there
        // (sinking, rather than blowing up as it would on the ground).
        using var sim = new Simulation(Mission.ReachOrbit);
        var pilot = new OrbitAutopilot();
        Vessel? Booster(Flight f) => f.Vessels.FirstOrDefault(v => v.Stage == 0 && v.TopStage == 0);

        sim.Run(pilot.Fly, f => Booster(f) is { State: FlightState.Crashed }, maxSeconds: 200);
        Report(sim, "booster down");

        var booster = Booster(sim.Flight);
        Assert.True(booster is { State: FlightState.Crashed }, "the dropped booster never came down");
        output.WriteLine($"the booster: {booster.CrashReason}");
        Assert.StartsWith("Hit the", booster.CrashReason); // the water, or the ground: not broken up in the air
        Assert.Equal(FlightState.Flying, sim.Flight.State); // only the booster: the rest flies on
    }

    [Fact]
    public void A_flight_that_reaches_space_without_orbit_does_not_succeed()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        // Straight up: it climbs out of the atmosphere (with this much fuel, it doesn't come back).
        var pilot = new OrbitAutopilot { TurnAltitude = 1e9f, TargetApoapsis = 1e9f };

        sim.RunToEnd(pilot.Fly, maxSeconds: 120);
        Report(sim, "straight up");

        var flight = sim.Flight;
        Assert.True(flight.MaxAltitude > Scene.AtmosphereHeight, $"only reached {flight.MaxAltitude:0} m");
        Assert.False(flight.AchievedOrbit);
        Assert.False(flight.Run!.Succeeded);
    }

    private void Report(Simulation sim, string when)
    {
        var f = sim.Flight;
        output.WriteLine(
            $"{when}: {f.State} after {sim.Time:0.0} s of flight, simulated in {sim.WallTime.TotalMilliseconds:0} ms " +
            $"({sim.Time / Math.Max(sim.WallTime.TotalSeconds, 1e-6):0}x real time)");
        output.WriteLine(
            $"  stage {f.Stage + 1} of {f.StageCount}, altitude {f.Ground:0} m (best {f.MaxAltitude:0}), " +
            $"orbit {f.Periapsis:0} to {f.Apoapsis:0} m, speed {f.Velocity.Length():0} m/s, fuel {f.Fuel * 100:0}%");
        if (f.CrashReason is { } reason)
            output.WriteLine($"  crash: {reason}");
    }

    [Fact]
    public void Straight_up_and_down_it_moves_by_keplers_equation_for_a_line()
    {
        // Straight up from 4 km over the planet's sea, among the stars: falling back (300 m/s, to 1.8 times its top's time), just
        // over escaping (by a millionth) and escaping (half as fast again), each against the same
        // line stepped finely (RK4, 1 ms), its place and speed along it a few times on: up, at its
        // top and down again for the one that falls back.
        var planet = GroundBody.Planet;
        var r0 = planet.SeaRadius + 4_000.0;
        var up = Vector3.Normalize(new Vector3(0.3f, -1f, 0.2f));
        var escape = Math.Sqrt(2 * planet.Mu / r0);
        foreach (var (name, v0, given) in new[] { ("falling back", 300.0, 0.0), ("just escaping", escape * (1 + 1e-6), 120.0), ("escaping", 1.5 * escape, 120.0) })
        {
            var orbit = new Orbit(planet, planet.Centre + up * (float)r0, up * (float)v0);
            Assert.True(orbit.Radial);
            var seconds = given > 0 ? given : 1.8 * orbit.TopSeconds!.Value; // up, over its top and well down again
            var (r, v, t) = (r0, (double)Vector3.Dot(orbit.StateAfter(0).Velocity, up), 0.0);
            const double Dt = 1e-3;
            double Pull(double d) => -planet.Mu / (d * d);
            var (worstPlace, worstSpeed, top) = (0.0, 0.0, 0.0);
            for (var k = 1; t < seconds - 1e-9; k++)
            {
                var (k1r, k1v) = (v, Pull(r));
                var (k2r, k2v) = (v + k1v * Dt / 2, Pull(r + k1r * Dt / 2));
                var (k3r, k3v) = (v + k2v * Dt / 2, Pull(r + k2r * Dt / 2));
                var (k4r, k4v) = (v + k3v * Dt, Pull(r + k3r * Dt));
                (r, v, t) = (r + (k1r + 2 * k2r + 2 * k3r + k4r) * Dt / 6, v + (k1v + 2 * k2v + 2 * k3v + k4v) * Dt / 6, k * Dt);
                top = Math.Max(top, r);
                if (k % 5_000 != 0)
                    continue;
                var (at, velocity) = orbit.StateAfter(t);
                var distance = ((Vector3Double)at - (Vector3Double)planet.Centre).Length();
                (worstPlace, worstSpeed) = (Math.Max(worstPlace, Math.Abs(distance - r)), Math.Max(worstSpeed, Math.Abs(Vector3.Dot(velocity, up) - v)));
                Assert.True(Vector3.Distance(Vector3.Normalize(at - planet.Centre), up) < 1e-5f, $"{name}: off its line at {t:0} s");
                Assert.Equal(orbit.PositionAfter(t), at);
            }
            output.WriteLine($"{name} at {v0:0.0} m/s: {seconds:0} s on {r - planet.SeaRadius:0} m up, {v:0.0} m/s; worst {worstPlace:0.000} m and {worstSpeed:0.0000} m/s off; highest {top - planet.SeaRadius:0} m");
            Assert.True(worstPlace < 0.5, $"{name}: {worstPlace:0.000} m off the line stepped finely");
            Assert.True(worstSpeed < 0.01, $"{name}: {worstSpeed:0.0000} m/s off the line stepped finely");
            if (name == "falling back")
            {
                Assert.True(v < 0 && r < top - 100, "it never came back down");
                Assert.InRange(orbit.TopSeconds!.Value, 0, seconds);
                var (atTop, atTopVelocity) = orbit.StateAfter(orbit.TopSeconds!.Value);
                Assert.True(Math.Abs(((Vector3Double)atTop - (Vector3Double)planet.Centre).Length() - top) < 0.5, "its top isn't where the line stepped finely has it");
                Assert.True(MathF.Abs(Vector3.Dot(atTopVelocity, up)) < 0.05f);
            }
        }
    }

    [Fact]
    public void Straight_up_it_is_found_rising_through_a_height_near_its_top()
    {
        // Straight up from a few heights at a few speeds (each falling back, a share of its escape speed): the time it is 97% of
        // the way up to its top is found, there to a centimetre, still rising; a height just past
        // its top is never reached.
        var planet = GroundBody.Planet;
        var up = Vector3.Normalize(new Vector3(-0.2f, -1f, 0.4f));
        foreach (var start in new[] { 3_500.0, 10_000.0, 40_000.0 })
            foreach (var share in new[] { 0.1, 0.5, 0.9 }) // of its escape speed there: falling back from low, middling and high tops
            {
                var r0 = planet.SeaRadius + start;
                var speed = share * Math.Sqrt(2 * planet.Mu / r0);
                var orbit = new Orbit(planet, planet.Centre + up * (float)r0, up * (float)speed);
                Assert.True(orbit.TopSeconds is not null, $"from {start:0} m at {speed:0} m/s: radial {orbit.Radial}, no top");
                var (topAt, _) = orbit.StateAfter(orbit.TopSeconds!.Value);
                var top = ((Vector3Double)topAt - (Vector3Double)planet.Centre).Length();
                var near = r0 + 0.97 * (top - r0);
                var when = orbit.RisingTo(near);
                Assert.True(when is not null, $"from {start:0} m at {speed:0} m/s: never found rising through {near - planet.SeaRadius:0.0} m (its top {top - planet.SeaRadius:0.0} m)");
                var (there, moving) = orbit.StateAfter(when!.Value);
                Assert.InRange(((Vector3Double)there - (Vector3Double)planet.Centre).Length(), near - 0.01, near + 0.01);
                Assert.True(Vector3.Dot(moving, up) > 0, "found falling back");
                Assert.Null(orbit.RisingTo(top + 1));
            }
    }

    [Fact]
    public void A_narrow_ellipse_is_sampled_with_no_piece_bowing_out_more_than_its_circle_s_would()
    {
        // A stage thrown high from 6 km over the pad, 200 m/s up and 80 m/s east among the stars:
        // a narrow ellipse (its eccentricity about 0.93), up to its apoapsis some 15 km over the
        // ground and back down. Sampled as the map samples a vessel's path (48 pieces a lap), no
        // piece is further from the curve than a piece of a circle its major axis across would be
        // (the radius times the piece's angle squared over eight), at the apoapsis as at the
        // periapsis. Sampled evenly in its true anomaly, the pieces round its apoapsis would each
        // bow out a good many times that: a corner.
        const int Segments = 48;
        var planet = GroundBody.Planet;
        var at = Planet.Up(Vector3.Zero) * 6000f;
        var orbit = new Orbit(planet, at, Planet.Up(at) * 200f + Planet.East(at) * 80f);
        Assert.True(orbit.EndsOnGround, "it should come down");
        var a = (orbit.PeriapsisDistance + orbit.ApoapsisDistance) / 2;
        var bound = a * Math.Pow(Math.Tau / Segments, 2) / 8;

        // How far the curve between two points of it, at true anomalies from and to, is from the chord.
        double Sag(double from, double to)
        {
            var (p, q) = (orbit.At(from), orbit.At(to));
            var worst = 0.0;
            for (var k = 1; k < 32; k++)
            {
                var c = orbit.At(from + (to - from) * k / 32);
                var along = Math.Clamp(Vector3.Dot(c - p, q - p) / (q - p).LengthSquared(), 0f, 1f);
                worst = Math.Max(worst, Vector3.Distance(c, p + (q - p) * along));
            }
            return worst;
        }
        var samples = new List<(Vector3 At, double Seconds)>();
        orbit.Samples(samples, 0, double.PositiveInfinity, Segments);
        var anomalies = samples.Select(s => orbit.AnomalyAfter(s.Seconds)).ToList();
        var sampled = Enumerable.Range(1, anomalies.Count - 1).Max(i => Sag(anomalies[i - 1], anomalies[i]));
        var (start, end) = (orbit.Now, orbit.EndAnomaly);
        var pieces = Math.Max(2, (int)Math.Ceiling(Segments * (end - start) / Math.Tau));
        var even = Enumerable.Range(1, pieces).Max(i => Sag(start + (end - start) * (i - 1) / pieces, start + (end - start) * i / pieces));
        output.WriteLine($"eccentricity {orbit.Eccentricity:0.000}, apoapsis {orbit.ApoapsisDistance - planet.SeaRadius:0} m up: {samples.Count} points, " +
            $"the worst piece {sampled:0.0} m off the curve (a circle's {bound:0.0} m); even in the true anomaly, {pieces} pieces, {even:0.0} m");
        Assert.InRange(orbit.Eccentricity, 0.85, 0.99);
        Assert.InRange(sampled, 0, bound * 1.05);
        Assert.True(even > 5 * bound, "the case should show the corners even sampling in the true anomaly makes");
        Assert.Equal(0.0, samples[0].Seconds);
        Assert.InRange(Vector3.Distance(samples[^1].At, orbit.At(end)), 0f, 0.01f);

        // A nearly parabolic one (0.99) through its periapsis, 5 km over the pad, sampled as the
        // flown path is (256 pieces a lap): where its eccentric anomaly crawls, at its periapsis,
        // its pieces are no further off the curve than even ones in its true anomaly would be.
        var low = Planet.Up(Vector3.Zero) * 5000f;
        var r = Vector3.Distance(low, planet.Centre);
        var near = new Orbit(planet, low, Planet.East(low) * (float)Math.Sqrt(planet.Mu * 1.99 / r));
        var arc = near.TimeTo(near.Now + 2);
        near.Samples(samples, 0, arc, 256);
        double NearSag(double from, double to)
        {
            var (p, q) = (near.At(from), near.At(to));
            var worst = 0.0;
            for (var k = 1; k < 32; k++)
            {
                var c = near.At(from + (to - from) * k / 32);
                var along = Math.Clamp(Vector3.Dot(c - p, q - p) / (q - p).LengthSquared(), 0f, 1f);
                worst = Math.Max(worst, Vector3.Distance(c, p + (q - p) * along));
            }
            return worst;
        }
        anomalies = samples.Select(s => near.AnomalyAfter(s.Seconds)).ToList();
        var nearSampled = Enumerable.Range(1, anomalies.Count - 1).Max(i => NearSag(anomalies[i - 1], anomalies[i]));
        var nearPieces = (int)Math.Ceiling(256 * 2 / Math.Tau);
        var nearEven = Enumerable.Range(1, nearPieces).Max(i => NearSag(near.Now + 2.0 * (i - 1) / nearPieces, near.Now + 2.0 * i / nearPieces));
        output.WriteLine($"eccentricity {near.Eccentricity:0.000} through its periapsis: {samples.Count} points, the worst piece {nearSampled:0.000} m off the curve; even in the true anomaly, {nearPieces} pieces, {nearEven:0.000} m");
        Assert.InRange(near.Eccentricity, 0.985, 0.995);
        Assert.InRange(nearSampled, 0, nearEven * 1.05 + 1e-3);
    }
}
