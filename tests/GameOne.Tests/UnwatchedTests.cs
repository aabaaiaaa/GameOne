using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Who is in the physics (<see cref="Unwatched"/>): a vessel nobody witnesses leaves it, to be
/// moved by formula, and comes back before anything witnessed could hit it, at once when it is
/// switched to, a few a step otherwise, and never swapped back and forth at the edge. Each test
/// puts a vessel under formula only by leaving it unwatched and far from anything witnessed.
/// </summary>
public sealed class UnwatchedTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>A satellite (the preset's) on a circular orbit <paramref name="altitude"/> up, <paramref name="aroundDegrees"/> round the launch's plane (tilted <paramref name="tiltDegrees"/>), numbered <paramref name="id"/>.</summary>
    internal static VesselState Satellite(int id, float altitude, float tiltDegrees, float aroundDegrees)
    {
        var template = WorldPresets.Satellites().Vessels[0];
        var vehicle = template.Vehicle;
        var (position, centre, velocity) = WorldPresets.CircularOrbit(altitude, float.DegreesToRadians(tiltDegrees),
            float.DegreesToRadians(aroundDegrees), RocketGeometry.CentreOfMassHeight(vehicle, template.First, template.Last));
        var up = Planet.Up(centre);
        var rotation = RotationTo(up);
        return template with { Id = id, Position = position, Rotation = rotation, Velocity = velocity, Aim = up, MaxAltitude = altitude, MaxGround = altitude };
    }

    /// <summary>A satellite on the far side of the planet from the preset's first, out of everything's way: the one flown while the others are left alone.</summary>
    private static VesselState FarAway(int id) => Satellite(id, 10000f, 0f, 240f);

    /// <summary>How far along a circle <paramref name="altitude"/> up a chord of <paramref name="apart"/> metres reaches, in degrees.</summary>
    private static float DegreesApart(float altitude, float apart) =>
        float.RadiansToDegrees(2f * MathF.Asin(apart / (2f * (Planet.SeaRadius + altitude))));

    internal static Quaternion RotationTo(Vector3 up)
    {
        var axis = Vector3.Cross(Vector3.UnitY, up);
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitY, up), -1f, 1f));
        return axis.LengthSquared() < 1e-10f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
    }

    private static Vessel Get(Simulation sim, int id) => sim.Flight.WorldVessels.Single(v => v.Id == id);

    /// <summary>Where <paramref name="orbit"/> has its centre of mass, and its velocity, at world time <paramref name="now"/>, in the ground's frame then.</summary>
    private static (Vector3 At, Vector3 Velocity) Along(FormulaOrbit orbit, double now)
    {
        var since = now - orbit.Since;
        var (position, stars) = orbit.Orbit.StateAfter(since);
        var at = Planet.Turned(position, -since);
        return (at, Planet.OverGround(at, Planet.TurnedDirection(stars, -since)));
    }

    [Fact]
    public void A_satellite_nobody_watches_leaves_the_physics()
    {
        using var sim = new Simulation(world: WorldPresets.Satellites());
        sim.Run(Nobody, _ => false, maxSeconds: 1);
        var satellites = sim.Flight.WorldVessels.Where(v => v != sim.Flight.Controlled).ToArray();
        Assert.Equal(4, satellites.Length);
        foreach (var satellite in satellites)
        {
            Assert.True(satellite.OnFormula, $"satellite {satellite.Id} is still in the physics");
            Assert.False(sim.Flight.Unwatched.ShouldBeInPhysics(satellite));
        }
        Assert.Equal(4, sim.Flight.Unwatched.UnderFormula);
        Assert.Equal(1, sim.Flight.Unwatched.InPhysics); // the rocket on the pad
        Assert.False(sim.Flight.Controlled.OnFormula);
    }

    [Fact]
    public void Choosing_who_is_in_the_physics_allocates_nothing()
    {
        // The satellites out of the physics and a small view's one in: each step's choice (and
        // moving those under formula) then hands nothing over, and must allocate nothing.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var watched = new[] { Get(sim, 1) };
        sim.Run((_, _) => new FlightInput(Watched: watched), _ => false, maxSeconds: 1);
        var unwatched = sim.Flight.Unwatched;
        var camera = sim.Flight.Controlled.CentreOfMass;
        unwatched.Advance(0f);
        unwatched.Update(0f, watched, camera); // warmed up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            unwatched.Advance(0f);
            unwatched.Update(0f, watched, camera);
        }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(3, unwatched.UnderFormula);
    }

    [Fact]
    public void Switching_to_a_vessel_under_formula_flies_it_in_the_physics_from_where_it_was()
    {
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var satellite = Get(sim, 0);
        Assert.True(sim.Run(Nobody, _ => satellite.OnFormula, maxSeconds: 1), "it never left the physics");
        sim.Run(Nobody, _ => false, maxSeconds: 2); // a while under formula
        var orbit = satellite.FormulaOrbit;

        sim.Step(new FlightInput(SwitchTo: satellite));
        Assert.Same(satellite, sim.Flight.Controlled);
        Assert.False(satellite.OnFormula);
        Assert.False(satellite.Craft.Removed);
        // In the physics from where the formula had it as the frame began, and flown one frame on
        // by Jolt: where the orbit has it now.
        var (at, velocity) = Along(orbit, sim.Flight.WorldTime);
        var (jump, kick) = (Vector3.Distance(satellite.CentreOfMass, at), Vector3.Distance(satellite.Velocity, velocity));
        output.WriteLine($"in the physics {jump:0.0000} m and {kick:0.00000} m/s from the formula's");
        Assert.InRange(jump, 0f, 0.01f);
        Assert.InRange(kick, 0f, 0.01f);

        // And flown on in the physics from there.
        sim.Run(Nobody, _ => false, maxSeconds: 1);
        Assert.False(satellite.OnFormula);
    }

    [Fact]
    public void A_vessel_coming_near_the_flown_one_is_back_in_the_physics_in_time()
    {
        // Two boosters closing at 500 m/s 5 km up (over the air), colliding after 20 s; flown, a
        // satellite on the far side of the planet, watching the second only after 10 s.
        var pair = FastCollisionTests.Pair(closing: 500f, altitude: 5000f, seconds: 20f);
        var hitPoint = FastCollisionTests.PairHitPoint(5000f);
        // On a circle 25 km up through the point opposite the collision, about 50 km from it.
        var flownState = Satellite(2, 25000f, 0f, 0f);
        var down = -Planet.Up(hitPoint);
        var centre = Planet.Centre + down * (Planet.SeaRadius + 25000f);
        var along = Vector3.Normalize(Vector3.Cross(down, Planet.NorthPole));
        var comHeight = RocketGeometry.CentreOfMassHeight(flownState.Vehicle, flownState.First, flownState.Last);
        flownState = flownState with
        {
            Position = centre - down * comHeight,
            Rotation = RotationTo(down),
            Aim = down,
            Velocity = Planet.OverGround(centre, along * Planet.CircularSpeed(25000f)),
            MaxAltitude = 25000f, MaxGround = 25000f,
        };
        using var sim = new Simulation(world: pair with { Vessels = [.. pair.Vessels, flownState], NextVesselId = 3 });
        var (first, second, flown) = (Get(sim, 0), Get(sim, 1), Get(sim, 2));
        sim.Step(new FlightInput(SwitchTo: flown));
        output.WriteLine($"the flown one is {Vector3.Distance(flown.CentreOfMass, first.CentreOfMass) / 1000f:0.0} km from the first");

        var watched = new[] { second };
        var start = sim.Time;
        var (wasOut, inAt, nearAt) = (false, double.NaN, double.NaN);
        sim.Run((_, _) => sim.Time - start >= 10.0 ? new FlightInput(Watched: watched) : default, f =>
        {
            var t = sim.Time - start;
            if (first.OnFormula)
                (wasOut, inAt) = (true, double.NaN);
            else if (wasOut && double.IsNaN(inAt))
                inAt = t;
            if (first.State == FlightState.Crashed || second.State == FlightState.Crashed
                || Vector3.Distance(first.CentreOfMass, second.CentreOfMass) <= Scene.HandoverDistance)
            {
                nearAt = t;
                return true;
            }
            return false;
        }, maxSeconds: 30);
        output.WriteLine($"back in the physics at {inAt:0.00} s, within {Scene.HandoverDistance} m at {nearAt:0.00} s");
        Assert.True(wasOut, "the first never left the physics");
        Assert.False(double.IsNaN(nearAt), "they never came near");
        Assert.False(double.IsNaN(inAt), "the first was still under formula as they came near");
        Assert.True(nearAt - inAt >= 1.0, $"back only {nearAt - inAt:0.00} s before they came near");
    }

    [Fact]
    public void A_vessel_at_the_edge_is_not_swapped_back_and_forth()
    {
        // Two satellites on the same circle, 1.2 times the reach apart (they don't close, so the
        // reach is the handover distance), and one far off.
        const float Altitude = 9000f;
        var apart = DegreesApart(Altitude, 1.2f * Scene.HandoverDistance);
        using var sim = new Simulation(world: new WorldState
        {
            Vessels = [Satellite(0, Altitude, 0f, 60f), Satellite(1, Altitude, 0f, 60f + apart), FarAway(2)],
            NextVesselId = 3,
        });
        var (flown, edge, far) = (Get(sim, 0), Get(sim, 1), Get(sim, 2));
        output.WriteLine($"{Vector3.Distance(flown.CentreOfMass, edge.CentreOfMass):0.0} m apart");
        var (ins, outs, was) = (0, 0, edge.OnFormula);
        bool Count(Flight _)
        {
            if (edge.OnFormula != was)
                (ins, outs, was) = edge.OnFormula ? (ins, outs + 1, true) : (ins + 1, outs, false);
            return false;
        }

        // The main view's camera is kept by the far one throughout, so the one at the edge is never
        // the main view's witnessed (as anything within NearByDistance of the camera would be):
        // only its reach from the flown one says whether it is in the physics.
        var camera = far.CentreOfMass;
        Pilot looking = (_, _) => new FlightInput(Camera: camera);

        // Flown from the start, and the one at the edge in the physics (watched for that step: far
        // from the pad, it loads under formula), inside the reach it leaves past.
        sim.Step(new FlightInput(SwitchTo: flown, Camera: camera, Watched: [edge]));
        Assert.False(edge.OnFormula);
        sim.Run(looking, f => Count(f) || edge.OnFormula, maxSeconds: 10);
        Assert.False(edge.OnFormula, "it left the physics inside the reach it should leave past");

        // Nobody near: both leave.
        sim.Step(new FlightInput(SwitchTo: far, Camera: camera));
        sim.Run(looking, Count, maxSeconds: 1);
        Assert.True(edge.OnFormula && flown.OnFormula, "nobody watches them, yet they are in the physics");

        // Flown again: back at once itself, but the other, outside the reach it comes back within, stays out.
        sim.Step(new FlightInput(SwitchTo: flown, Camera: camera));
        Assert.False(flown.OnFormula);
        sim.Run(looking, f => Count(f) || !edge.OnFormula, maxSeconds: 10);
        Assert.True(edge.OnFormula, "it came back in outside the reach");
        output.WriteLine($"{ins} in, {outs} out");
        Assert.InRange(ins, 0, 1);
        Assert.InRange(outs, 0, 1);
    }

    [Fact]
    public void Handovers_in_are_spread_over_steps()
    {
        // Twenty satellites on one circle, 10 m apart, nobody near: all leave the physics. Then
        // the main view's camera is put in their midst: all are due at once.
        const float Altitude = 9000f;
        var step = DegreesApart(Altitude, 10f);
        var satellites = Enumerable.Range(0, 20).Select(i => Satellite(i, Altitude, 0f, 60f + i * step)).ToArray();
        using var sim = new Simulation(world: new WorldState { Vessels = satellites, NextVesselId = satellites.Length });
        var flight = sim.Flight;
        var crowd = flight.WorldVessels.Where(v => v != flight.Controlled).ToArray();
        sim.Run(Nobody, _ => false, maxSeconds: 1);
        Assert.All(crowd, v => Assert.True(v.OnFormula, $"{v.Id} is still in the physics"));

        var input = new FlightInput(Camera: crowd[10].CentreOfMass);
        var (most, steps) = (0, 0);
        while (crowd.Any(v => v.OnFormula) && steps < 20)
        {
            var under = crowd.Count(v => v.OnFormula);
            flight.Update(Simulation.FrameSeconds, input); // one flight step
            steps++;
            most = Math.Max(most, under - crowd.Count(v => v.OnFormula));
        }
        output.WriteLine($"all twenty back in {steps} steps, at most {most} a step");
        Assert.All(crowd, v => Assert.False(v.OnFormula, $"{v.Id} still under formula"));
        Assert.Equal(Scene.HandoversInPerStep, most); // the budget held them back
        Assert.Equal(20 / Scene.HandoversInPerStep, steps);
    }

    [Fact]
    public void Handovers_in_go_soonest_needed_first()
    {
        // Eight satellites in a line along one circle, 250 m apart (each further than the handover
        // distance from the next, so none is kept by its neighbour), coming head on at a ninth on
        // the same circle the other way round, closing at about 440 m/s, the nearest 300 m off.
        // All leave the physics in the first step, nobody near; then the ninth is flown: all eight
        // are due at once, the nearer the sooner, and come back the nearest four first.
        const float Altitude = 9000f;
        const float Flown = 90f; // degrees round, on the circle
        var line = Enumerable.Range(0, 8)
            .Select(i => Satellite(i, Altitude, 0f, Flown - DegreesApart(Altitude, 300f + 250f * i))).ToArray();
        var headOn = Satellite(8, Altitude, 180f, -Flown); // the same place, going the other way round
        using var sim = new Simulation(world: new WorldState { Vessels = [.. line, headOn], NextVesselId = 9 });
        var flight = sim.Flight;
        var flown = Get(sim, 8);
        var crowd = line.Select(s => Get(sim, s.Id)).ToArray(); // the nearest first
        flight.Update(Simulation.FrameSeconds, default);
        Assert.All(crowd, v => Assert.True(v.OnFormula, $"{v.Id} is still in the physics"));
        output.WriteLine(string.Join(" ", crowd.Select(v => $"{Vector3.Distance(v.CentreOfMass, flight.WorldVessels.Single(w => w.Id == 8).CentreOfMass):0} m")));

        var back = new int[crowd.Length];
        for (var steps = 1; steps <= 3 && crowd.Any(v => v.OnFormula); steps++)
        {
            flight.Update(Simulation.FrameSeconds, steps == 1 ? new FlightInput(SwitchTo: flown) : default); // one flight step
            for (var i = 0; i < crowd.Length; i++)
                if (back[i] == 0 && !crowd[i].OnFormula)
                    back[i] = steps;
        }
        output.WriteLine($"the step each came back in, the nearest first: {string.Join(" ", back)}");
        flight.RunFrame(Simulation.FrameSeconds, default); // flown behind its link-up
        Assert.Same(flown, flight.Controlled);
        Assert.Equal([1, 1, 1, 1, 2, 2, 2, 2], back);
    }

    [Fact]
    public void A_vessel_the_main_view_witnessed_is_not_swapped_back_and_forth_at_its_edge()
    {
        // A satellite 1,950 m ahead of the flown one, in the main view's few (within
        // NearByDistance of its camera, which follows the flown one), drawing away at 50 m/s:
        // nothing else holds it in the physics (moving apart, its reach is the handover distance).
        // Past the view's reach it stays, until past the hysteresis times it; then it goes, once.
        const float Altitude = 9000f;
        var flownState = Satellite(0, Altitude, 0f, 60f);
        var ahead = Satellite(1, Altitude, 0f, 60f + DegreesApart(Altitude, 1950f));
        ahead = ahead with { Velocity = ahead.Velocity.ToSingle() + Vector3.Normalize(ahead.Velocity.ToSingle()) * 50f };
        using var sim = new Simulation(world: new WorldState { Vessels = [flownState, ahead], NextVesselId = 2 });
        var (flown, edge) = (Get(sim, 0), Get(sim, 1));
        sim.Step(new FlightInput(SwitchTo: flown));
        var (ins, outs, was) = (0, 0, edge.OnFormula);
        var (pastView, pastHold, outAt) = (false, false, float.NaN);
        const float Hold = Scene.HandoverHysteresis * Scene.NearByDistance;
        sim.Run(Nobody, _ =>
        {
            var apart = Vector3.Distance(flown.CentreOfMass, edge.CentreOfMass);
            if (edge.OnFormula != was)
            {
                (ins, outs, was) = edge.OnFormula ? (ins, outs + 1, true) : (ins + 1, outs, false);
                if (edge.OnFormula)
                    outAt = apart;
            }
            pastView |= apart > Scene.NearByDistance + 100f;
            if (apart < Hold)
                Assert.False(edge.OnFormula, $"it left the physics {apart:0} m off, inside the view's hold");
            pastHold = apart > Hold + 100f;
            return pastHold;
        }, maxSeconds: 60);
        output.WriteLine($"{ins} in, {outs} out; out of the physics {outAt:0} m off");
        Assert.True(pastView && pastHold, "it never drew past the view's hold: this shows nothing");
        Assert.True(edge.OnFormula, "past the view's hold, nothing watching it, it is still in the physics");
        Assert.Equal(0, ins);
        Assert.Equal(1, outs);
    }

    [Fact]
    public void A_parting_stage_and_a_settling_landing_stay_in_the_physics()
    {
        // A whole rocket in orbit, flown, drops its booster; then a satellite far off is flown,
        // leaving the two unwatched while they part.
        const float Altitude = 9000f;
        var vehicle = Vehicle.ThreeStage;
        var (position, centre, velocity) = WorldPresets.CircularOrbit(Altitude, 0f, float.DegreesToRadians(60f),
            RocketGeometry.CentreOfMassHeight(vehicle, 0, vehicle.Stages.Length - 1));
        var up = Planet.Up(centre);
        var rocket = Satellite(0, Altitude, 0f, 60f) with
        {
            First = 0, Last = vehicle.Stages.Length - 1, Position = position, Velocity = velocity, Rotation = RotationTo(up), Aim = up,
            Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * 0.1f)],
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 0, vehicle.Stages.Length - 1),
        };
        using (var sim = new Simulation(world: new WorldState { Vessels = [rocket, FarAway(1)], NextVesselId = 2 }))
        {
            var (stack, far) = (Get(sim, 0), Get(sim, 1));
            sim.Step(new FlightInput(SwitchTo: stack));
            sim.Step(new FlightInput(Stage: true));
            var dropped = sim.Flight.WorldVessels[^1]; // the newest: the booster that came away
            Assert.NotNull(dropped.Craft.PartingFrom);
            sim.Step(new FlightInput(SwitchTo: far));
            var parted = sim.Run(Nobody, _ =>
            {
                if (dropped.Craft.PartingFrom is null)
                    return true;
                Assert.True(sim.Flight.Unwatched.ShouldBeInPhysics(dropped));
                Assert.False(dropped.OnFormula, "it left the physics while still parting");
                return false;
            }, maxSeconds: 30);
            output.WriteLine($"parted after {sim.Time:0.0} s");
            Assert.True(parted, "it never finished parting");
            // Parted and nobody near: both go.
            Assert.True(sim.Run(Nobody, _ => dropped.OnFormula && stack.OnFormula, maxSeconds: 2), "they stayed in the physics once parted");
        }

        // A booster coming down in the deep sea, a satellite far off flown: watched until it is in
        // the water (nobody watching it, the formula would splash it down itself, never settling
        // it in the physics), then left alone while it settles.
        var water = Planet.Centre + WaterTests.DeepWater.Value * Planet.SeaRadius;
        var booster = Drops.Placed(water, Planet.East(water), 2f);
        using (var sim = new Simulation(world: new WorldState { Vessels = [booster, FarAway(1)], NextVesselId = 2 }))
        {
            var (landing, far) = (Get(sim, 0), Get(sim, 1));
            var watched = new[] { landing };
            sim.Step(new FlightInput(SwitchTo: far, Watched: watched));
            Assert.True(sim.Run((_, _) => new FlightInput(Watched: watched), _ => landing.Craft.Settling, maxSeconds: 5), "it never came down in the water");
            Assert.False(landing.OnFormula);
            var (settled, sawSettling) = (false, false);
            settled = sim.Run(Nobody, _ =>
            {
                if (landing.Craft.Settling)
                {
                    sawSettling = true;
                    Assert.True(sim.Flight.Unwatched.ShouldBeInPhysics(landing));
                    Assert.False(landing.OnFormula, "it left the physics while still settling");
                }
                return landing.State != FlightState.Flying;
            }, maxSeconds: 90);
            output.WriteLine($"{landing.State} after {sim.Time:0.0} s");
            Assert.True(sawSettling, "it never settled in the water");
            Assert.True(settled, $"still {landing.State}");
            Assert.Equal(FlightState.Floating, landing.State);
            // Settled afloat and at rest (frozen, as one afloat far from the flown one is), and nobody near: it goes.
            var start = sim.Time;
            Assert.True(sim.Run(Nobody, _ => landing.OnFormula, maxSeconds: 30), "it stayed in the physics once afloat");
            output.WriteLine($"out of the physics {sim.Time - start:0.0} s after it floated");
            Assert.True(landing.Craft.Frozen, "it left the physics before it was at rest");
        }
    }

    // ---- Collisions under formula: part 1's and 2's collisions, nobody watching ----

    /// <summary>When the pairs here are set to collide: 3 s in, and half a physics step more (as <see cref="FastCollisionTests"/>' probe).</summary>
    internal const float HitsAt = 3f + Physics.RocketPhysics.StepSeconds / 2f;

    /// <summary>
    /// Where the pairs here collide: 5 km over the far side of the planet from Home. Two at 4,000 m/s
    /// each reach 30 km in the handover's lead (5 s at their closing, and the hysteresis), the
    /// whole planet from anywhere near it: a collision over Home's side would keep them in the
    /// physics by the new flight's rocket on its pad. Over the far side, coming at it along the
    /// level, they close on the pad slowly enough, and draw away from it after.
    /// </summary>
    internal static readonly Vector3 Opposite = Planet.Centre - Vector3.UnitY * (Planet.SeaRadius + 5000f);

    /// <summary>
    /// <paramref name="world"/>'s first two vessels left alone, a satellite 150 km up over the
    /// other side of the planet from <paramref name="there"/> flown (switched to in the first
    /// frame, so both leave the physics in it, nobody near them); and those two. So far off for
    /// the same reach as <see cref="Opposite"/>'s.
    /// </summary>
    internal static (Simulation Sim, Vessel A, Vessel B) Unflown(WorldState world, Vector3 there)
    {
        var id = world.NextVesselId;
        var flown = PerfScenes.FarSatellite(there, id, altitude: 150000f);
        var sim = new Simulation(world: world with { Vessels = [.. world.Vessels, flown], NextVesselId = id + 1 });
        sim.Step(new FlightInput(SwitchTo: Get(sim, id)));
        return (sim, Get(sim, world.Vessels[0].Id), Get(sim, world.Vessels[1].Id));
    }

    /// <summary>
    /// Runs <paramref name="sim"/> for up to <paramref name="seconds"/> of game time (at
    /// <paramref name="pilot"/>'s input, nobody's by default), or until both are wrecked: each
    /// under formula until it is (so the formula found it, not the physics), the game never
    /// slowed for them. Returns the closest the two middles came while both were whole.
    /// </summary>
    private float RunUnwatched(Simulation sim, Vessel a, Vessel b, double seconds, Pilot? pilot = null)
    {
        var closest = float.MaxValue;
        sim.Run(pilot ?? Nobody, f =>
        {
            Assert.Null(f.Slowed);
            foreach (var v in new[] { a, b })
                if (v.State != FlightState.Crashed)
                    Assert.True(v.OnFormula, $"vessel {v.Id} came back into the physics, {v.State}");
            if (a.State != FlightState.Crashed && b.State != FlightState.Crashed)
                closest = MathF.Min(closest, Vector3.Distance(Middle(a), Middle(b)));
            return a.State == FlightState.Crashed && b.State == FlightState.Crashed;
        }, maxSeconds: seconds);
        output.WriteLine($"at {sim.Time:0.000} s: closest (both whole) {closest:0.0} m (sizes {Collisions.Size(a):0.0} and {Collisions.Size(b):0.0} m);"
            + $" {a.State} {a.CrashReason}; {b.State} {b.CrashReason}");
        return closest;
    }

    private static Vector3 Middle(Vessel v) => Vector3.Transform(new Vector3(0f, v.Height / 2f, 0f), v.Model);

    /// <summary>Both wrecked by hitting each other, within 50 m of <paramref name="hit"/>.</summary>
    private void BothWrecked(Vessel a, Vessel b, Vector3 hitPoint)
    {
        foreach (var v in new[] { a, b })
        {
            Assert.Equal(FlightState.Crashed, v.State);
            Assert.StartsWith("Hit another vessel", v.CrashReason);
            var off = Vector3.Distance(v.CrashPosition, hitPoint);
            output.WriteLine($"vessel {v.Id} wrecked {off:0.0} m from where they were to collide");
            Assert.InRange(off, 0f, 50f);
        }
    }

    [Theory]
    [InlineData(500f)]
    [InlineData(2000f)]
    [InlineData(8000f)]
    public void Two_unwatched_vessels_collide_head_on_and_both_are_wrecked(float closing)
    {
        var hitPoint = Opposite;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(closing, 5000f, HitsAt, over: hitPoint), hitPoint);
        using var _ = sim;
        Assert.True(a.OnFormula && b.OnFormula, "they didn't leave the physics");
        RunUnwatched(sim, a, b, HitsAt + 2f - sim.Time);
        BothWrecked(a, b, hitPoint);
    }

    [Fact]
    public void Two_unwatched_vessels_crossing_at_5657_are_both_wrecked()
    {
        // Each at the top speed, 4,000 m/s, at right angles: closing at 5,657 m/s.
        var hitPoint = Opposite;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(4000f * MathF.Sqrt(2f), 5000f, HitsAt, crossing: true, over: hitPoint), hitPoint);
        using var _ = sim;
        RunUnwatched(sim, a, b, HitsAt + 2f - sim.Time);
        BothWrecked(a, b, hitPoint);
    }

    [Fact]
    public void Two_unwatched_vessels_collide_head_on_at_8000_under_32x_warp()
    {
        // At 32x each frame is over half a second of game time, two checks' intervals or more,
        // each 8,000 m/s closing a 2 km stretch: none may be skipped, nor any part of one.
        const float HitsIn = 8f;
        var hitPoint = Opposite;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(8000f, 5000f, HitsIn, over: hitPoint), hitPoint);
        using var _ = sim;
        var faster = new FlightInput(Faster: true);
        while (sim.Flight.TimeScale < 32f)
            sim.Step(faster);
        Assert.Equal(32f, sim.Flight.TimeScale);
        output.WriteLine($"at 32x from {sim.Time:0.00} s");
        RunUnwatched(sim, a, b, HitsIn + 3f - sim.Time);
        Assert.Equal(32f, sim.Flight.TimeScale); // never slowed, nor dropped out of the warp, for them
        BothWrecked(a, b, hitPoint);
    }

    [Fact]
    public void A_slow_unwatched_bump_wrecks_neither()
    {
        // Two satellites on one circle 9 km up, the one behind 3 m/s faster: its size-sphere
        // reaches the other's some 5 s in, closing at about 3 m/s, under the tolerance.
        var (a, b) = Closing(3f);
        var (sim, first, second) = Unflown(new WorldState { Vessels = [a, b], NextVesselId = 2 }, a.Position.ToSingle());
        using var _ = sim;
        Assert.True(Vector3.Distance(Middle(first), Middle(second)) > Collisions.Size(first) + Collisions.Size(second) + 5f, "they start within reach");
        var closest = RunUnwatched(sim, first, second, 15);
        Assert.True(closest < Collisions.Size(first) + Collisions.Size(second), $"they never touched: {closest:0.0} m apart at the closest");
        Assert.NotEqual(FlightState.Crashed, first.State);
        Assert.NotEqual(FlightState.Crashed, second.State);
    }

    [Fact]
    public void An_unwatched_bump_over_the_tolerance_wrecks_both()
    {
        // As the slow bump, the one behind 10 m/s faster: over the tolerance (6 m/s).
        var (a, b) = Closing(10f);
        var (sim, first, second) = Unflown(new WorldState { Vessels = [a, b], NextVesselId = 2 }, a.Position.ToSingle());
        using var _ = sim;
        Assert.True(Vector3.Distance(Middle(first), Middle(second)) > Collisions.Size(first) + Collisions.Size(second) + 5f, "they start within reach");
        RunUnwatched(sim, first, second, 10);
        foreach (var v in new[] { first, second })
        {
            Assert.Equal(FlightState.Crashed, v.State);
            Assert.StartsWith("Hit another vessel at", v.CrashReason);
        }
    }

    /// <summary>
    /// Two satellites on one circle 9 km up, their middles 15 m further apart than their sizes
    /// reach, the one behind <paramref name="faster"/> m/s faster along its way.
    /// </summary>
    private static (VesselState Behind, VesselState Ahead) Closing(float faster)
    {
        const float Altitude = 9000f;
        var s = Satellite(0, Altitude, 0f, 60f);
        var (height, stage) = (RocketGeometry.StackHeight(s.Vehicle, s.First, s.Last), s.Vehicle.Stages[s.First]);
        var widest = MathF.Max(stage.Radius, stage.BellRadius);
        var size = 2f * MathF.Sqrt(height * height / 4f + widest * widest); // both sizes (Collisions.Size)
        var behind = Satellite(0, Altitude, 0f, 60f);
        behind = behind with { Velocity = behind.Velocity.ToSingle() + Vector3.Normalize(behind.Velocity.ToSingle()) * faster };
        return (behind, Satellite(1, Altitude, 0f, 60f + DegreesApart(Altitude, size + 15f)));
    }

    [Fact]
    public void Two_unwatched_vessels_that_miss_by_a_little_are_both_untouched()
    {
        // Closing at 2,000 m/s, passing with their middles two sizes and 2 m apart: just outside
        // each other's reach.
        var hitPoint = Opposite;
        var miss = 2f * FastCollisionTests.BoosterSize + 2f;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(2000f, 5000f, HitsAt, sideways: miss, over: hitPoint), hitPoint);
        using var _ = sim;
        var closest = RunUnwatched(sim, a, b, HitsAt + 1f - sim.Time);
        Assert.InRange(closest, 2f * FastCollisionTests.BoosterSize, miss + 5f); // they passed, as close as that
        Assert.Equal(FlightState.Flying, a.State);
        Assert.Equal(FlightState.Flying, b.State);
    }

    [Fact]
    public void Two_unwatched_vessels_passing_within_their_sizes_but_clear_of_each_others_bodies_are_both_untouched()
    {
        // Closing at 2,000 m/s, their middles passing 10 m apart: well within their sizes (22 m
        // together), but each body (an axis as wide as its widest, 1.85 m) clear of the other's.
        var hitPoint = Opposite;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(2000f, 5000f, HitsAt, sideways: 10f, over: hitPoint), hitPoint);
        using var _ = sim;
        var closest = RunUnwatched(sim, a, b, HitsAt + 1f - sim.Time);
        Assert.InRange(closest, 5f, 15f); // they passed within their sizes
        Assert.Equal(FlightState.Flying, a.State);
        Assert.Equal(FlightState.Flying, b.State);
    }

    /// <summary>Level ground far from Home (Drops' own), where the boosters below land with nothing in the physics near them.</summary>
    private static readonly Lazy<(Vector3 At, Vector3 Downhill)> Level = new(() =>
    {
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        return (at, downhill);
    });

    /// <summary>A booster (legs out) <paramref name="clearance"/> over the ground <paramref name="off"/> from <see cref="Level"/>, falling at <paramref name="falling"/>, its engine pushing <paramref name="push"/> times its weight.</summary>
    private static VesselState Booster(int id, Vector3 off, float clearance, float falling, float push = 0f, bool legsOut = true)
    {
        var (at, downhill) = Level.Value;
        var place = Planet.Centre + Planet.Up(at + off) * Planet.SeaRadius;
        var placed = Drops.Placed(place, downhill, falling, clearance: clearance, legsOut: legsOut, id: id);
        var stage = placed.Vehicle.Stages[placed.First];
        var weight = (Planet.Gravity(placed.Position.ToSingle()) + Planet.OutwardPull(placed.Position.ToSingle())).Length();
        return placed with { Throttle = push * weight * (stage.DryMass + placed.Fuel[placed.First]) / stage.Thrust };
    }

    /// <summary>Whether <paramref name="v"/> came down whole: landed, or standing on its engine (still flying, on the ground).</summary>
    private static bool Down(Vessel v) => v.State == FlightState.Landed || (v.State == FlightState.Flying && v.Grounded);

    [Fact]
    public void Two_unwatched_boosters_landing_15_m_apart_both_stand()
    {
        // One set down at once beside where the other comes down on its engine from 15 m up: 15 m
        // apart, within their sizes all the way down, their bodies (the feet's reach too) never
        // touching.
        var east = Planet.East(Level.Value.At);
        var first = Booster(0, Vector3.Zero, 0.5f, 1f);
        var second = Booster(1, east * 15f, 15f, 3f, push: 0.95f);
        var (sim, a, b) = Unflown(new WorldState { Vessels = [first, second], NextVesselId = 2 }, Level.Value.At);
        using var _ = sim;
        var closest = RunUnwatched(sim, a, b, 10);
        Assert.True(closest < Collisions.Size(a) + Collisions.Size(b), $"they never came within their sizes: {closest:0.0} m");
        Assert.True(Down(a), $"the first is {a.State} {a.CrashReason}");
        Assert.True(Down(b), $"the second is {b.State} {b.CrashReason}");
    }

    [Fact]
    public void An_unwatched_booster_hovering_past_one_standing_within_their_sizes_leaves_both_whole()
    {
        // One stands; the other hovers past it 2 m up at 15 m/s, 15 m to its side: their sizes
        // hit closing at some 10 m/s along the line between them, over the tolerance, but their
        // bodies never touch.
        var (at, _) = Level.Value;
        var (east, north) = (Planet.East(at), Planet.North(at));
        var standing = Booster(0, Vector3.Zero, 0.5f, 1f);
        var hovering = Booster(1, -east * 50f + north * 15f, 2f, 0f, push: 1f);
        hovering = hovering with { Velocity = Planet.East(hovering.Position.ToSingle()) * 15f };
        var (sim, a, b) = Unflown(new WorldState { Vessels = [standing, hovering], NextVesselId = 2 }, at);
        using var _ = sim;
        var closest = RunUnwatched(sim, a, b, 8);
        Assert.True(closest < Collisions.Size(a) + Collisions.Size(b), $"they never came within their sizes: {closest:0.0} m");
        Assert.True(Down(a), $"the standing one is {a.State} {a.CrashReason}");
        Assert.NotEqual(FlightState.Crashed, b.State);
    }

    [Fact]
    public void An_unwatched_booster_coming_down_onto_one_standing_wrecks_both()
    {
        // One stands; the other, its engine off and legs in, falls onto its top from 25 m over it,
        // hitting it at some 24 m/s.
        var height = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var standing = Booster(0, Vector3.Zero, 0.5f, 1f);
        var falling = Booster(1, Vector3.Zero, height + 25f, 10f, legsOut: false);
        var (sim, a, b) = Unflown(new WorldState { Vessels = [standing, falling], NextVesselId = 2 }, Level.Value.At);
        using var _ = sim;
        RunUnwatched(sim, a, b, 5);
        foreach (var v in new[] { a, b })
        {
            Assert.Equal(FlightState.Crashed, v.State);
            Assert.StartsWith("Hit another vessel at", v.CrashReason);
        }
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.15f)]
    [InlineData(0.2f)]
    [InlineData(0.3f)]
    public void Two_vessels_leaving_the_physics_just_before_they_collide_are_both_wrecked(float lead)
    {
        // Head on at 500 m/s, the first watched (so both are in the physics: the second by its
        // nearness to it) until `lead` seconds before their middles come together, then left alone: both
        // leave the physics with the collision inside the interval the last check looked at
        // without them, and it is still found.
        var hitPoint = Opposite;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(500f, 5000f, HitsAt, over: hitPoint), hitPoint);
        using var _ = sim;
        var watched = new[] { a };
        var (leftAt, wasIn) = (double.NaN, false);
        sim.Run((_, _) => sim.Time < HitsAt - lead ? new FlightInput(Watched: watched) : default, _ =>
        {
            if (sim.Time < HitsAt - lead - 0.5)
                wasIn |= !a.OnFormula && !b.OnFormula;
            if (double.IsNaN(leftAt) && a.OnFormula && b.OnFormula && sim.Time >= HitsAt - lead)
                leftAt = sim.Time;
            return a.State == FlightState.Crashed && b.State == FlightState.Crashed;
        }, maxSeconds: HitsAt + 1f - sim.Time);
        output.WriteLine($"left the physics at {leftAt:0.000} s, their middles to come together at {HitsAt:0.000} s; {a.State} {a.CrashReason}; {b.State} {b.CrashReason}");
        Assert.True(wasIn, "they were never both in the physics");
        Assert.False(double.IsNaN(leftAt), "they never both left the physics");
        foreach (var v in new[] { a, b })
        {
            Assert.Equal(FlightState.Crashed, v.State);
            Assert.StartsWith("Hit another vessel", v.CrashReason);
            Assert.True(v.OnFormula, "the physics judged it, not the formula");
        }
    }

    [Fact]
    public void An_unwatched_booster_swept_sideways_across_the_end_of_another_wrecks_both()
    {
        // One stands upright, flying east at 2,000 m/s; the other lies across its way (along the
        // north), drifting west at 50 m/s, its axis half its width over the first's top: the first's
        // side strikes the other's side just under its axis, across the first's end. Taken face on
        // (along the first's upright axis) they would close at nothing, a bump.
        var hitPoint = Opposite;
        var up = Planet.Up(hitPoint);
        var east = Planet.East(hitPoint);
        var height = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var widest = MathF.Max(Vehicle.Booster.Stages[0].Radius, Vehicle.Booster.Stages[0].BellRadius);
        var upright = FastCollisionTests.Flying(hitPoint, east, 2000f, HitsAt, 0);
        var middle = upright.Position.ToSingle() + Vector3.Transform(Vector3.UnitY, upright.Rotation) * (height / 2f);
        var standing = Planet.Up(middle);
        upright = upright with { Rotation = RotationTo(standing), Aim = standing, Position = middle - standing * (height / 2f) };
        var lying = FastCollisionTests.Flying(hitPoint + up * (height / 2f + widest / 2f), -east, 50f, HitsAt, 1, lieAcross: 90f);
        var (sim, a, b) = Unflown(new WorldState { Vessels = [upright, lying], NextVesselId = 2 }, hitPoint);
        using var _ = sim;
        RunUnwatched(sim, a, b, HitsAt + 1f - sim.Time);
        foreach (var v in new[] { a, b })
        {
            Assert.Equal(FlightState.Crashed, v.State);
            Assert.StartsWith("Hit another vessel at", v.CrashReason);
        }
    }

    [Theory]
    [InlineData("kicked aside", false)]
    [InlineData("slowed to 3 m/s", false)]
    [InlineData("slowed to 20 m/s", true)]
    public void A_collision_found_then_remade_by_a_burn_is_judged_as_the_new_motion_hits(string burn, bool wrecked)
    {
        // Head on at 500 m/s; with their collision found, a frame or two before it comes (their
        // middles within 10 m more than their noses touch at), the second's motion is remade as a
        // burn remakes it: kicked 2,000 m/s aside, or slowed to close at 3 or 20 m/s. The collision
        // found is no longer theirs: kicked aside they miss; closing at 3 m/s they bump; at 20 m/s
        // they collide at that, a little later.
        var hitPoint = Opposite;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(500f, 5000f, HitsAt, over: hitPoint), hitPoint);
        using var _ = sim;
        var unwatched = sim.Flight.Unwatched;
        var touching = a.Height + 2f * FastCollisionTests.BoosterWidest;
        Assert.True(sim.Run(Nobody, _ => unwatched.CollisionsAhead > 0 && Vector3.Distance(Middle(a), Middle(b)) < touching + 10f, maxSeconds: HitsAt),
            "their collision was never found before it came");
        Assert.Equal(FlightState.Flying, b.State);
        Assert.True(Vector3.Distance(Middle(a), Middle(b)) > touching, "they touch already");
        var closingWay = Vector3.Normalize(b.Velocity - a.Velocity);
        var velocity = burn switch
        {
            "kicked aside" => b.Velocity + Planet.North(b.CentreOfMass) * 2000f,
            "slowed to 3 m/s" => a.Velocity + closingWay * 3f,
            _ => a.Velocity + closingWay * 20f,
        };
        b.Motion = Formula.Begin(GroundBody.Planet, b.CentreOfMass, velocity, b.FormulaRotation, b.FormulaSpin, sim.Flight.WorldTime, onItsOrbit: true);
        var closest = RunUnwatched(sim, a, b, 6);
        output.WriteLine($"{burn}: {a.State} {a.CrashReason}; {b.State} {b.CrashReason}");
        if (burn != "kicked aside")
            Assert.True(closest < touching + 1f, $"they never touched: {closest:0.0} m apart at the closest");
        if (wrecked)
            foreach (var v in new[] { a, b })
            {
                Assert.Equal(FlightState.Crashed, v.State);
                Assert.StartsWith("Hit another vessel at 2", v.CrashReason); // at about 20 m/s, not 500
            }
        else
        {
            Assert.NotEqual(FlightState.Crashed, a.State);
            Assert.NotEqual(FlightState.Crashed, b.State);
        }
    }

    [Fact]
    public void Finding_collisions_under_formula_allocates_nothing()
    {
        // The pass within their sizes but clear of each other's bodies, run to just before they
        // pass: their swept boxes overlap and their sizes touch, so the pair is solved and their
        // bodies sampled each check, and nothing is wrecked (which allocates its reason).
        var hitPoint = Opposite;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(2000f, 5000f, HitsAt, sideways: 10f, over: hitPoint), hitPoint);
        using var _ = sim;
        sim.Run(Nobody, _ => Vector3.Distance(Middle(a), Middle(b)) < 300f, maxSeconds: HitsAt);
        Assert.True(a.OnFormula && b.OnFormula);
        var unwatched = sim.Flight.Unwatched;
        var now = sim.Flight.WorldTime;
        unwatched.FindCollisions(now); // warmed up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= 100; i++)
            unwatched.FindCollisions(now + i * Scene.FormulaCollisionSeconds); // a check each time, from where they are now
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(FlightState.Flying, a.State);
        Assert.Equal(FlightState.Flying, b.State);
    }

    // ---- Who is chosen, against the rule itself ----

    /// <summary>
    /// Who the rule wants in the physics, by brute force (every vessel against every one kept, pass
    /// after pass, as <see cref="Unwatched"/>'s doc says, with none of its grid): the flown one, one
    /// waiting on the pad or landed but not yet at rest, and whatever is within reach of one of
    /// those kept (the handover distance plus their closing among the stars times the lead; that
    /// times the hysteresis for one in the physics, as <paramref name="inPhysics"/> had it as the
    /// step began). The main view's camera is kept far off by the tests that ask this.
    /// </summary>
    private static HashSet<Vessel> Wanted(Flight flight, IReadOnlySet<Vessel> inPhysics)
    {
        var world = flight.WorldVessels.Where(v => v.State != FlightState.Crashed).ToArray();
        var kept = world.Where(v => v == flight.Controlled || v.State == FlightState.Ready
            || (inPhysics.Contains(v) && v.State == FlightState.Landed && !v.Craft.Frozen)).ToHashSet();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var v in world)
                if (!kept.Contains(v) && kept.Any(k => Reached(v, k, inPhysics.Contains(v))))
                    grew = kept.Add(v);
        }
        return kept;

        static bool Reached(Vessel v, Vessel k, bool inPhysics)
        {
            var apart = v.CentreOfMass - k.CentreOfMass;
            var distance = apart.Length();
            var relative = Planet.AmongStars(v.CentreOfMass, v.Velocity) - Planet.AmongStars(k.CentreOfMass, k.Velocity);
            var closing = distance > 1e-3f ? MathF.Max(0f, -Vector3.Dot(apart, relative) / distance) : relative.Length();
            var reach = Scene.HandoverDistance + closing * Scene.HandoverSeconds;
            return distance <= (inPhysics ? reach * Scene.HandoverHysteresis : reach);
        }
    }

    /// <summary><paramref name="state"/> moved by under <paramref name="most"/> metres so its centre of mass lies on a face of the 400 m cells the kept are found by (a multiple of 400 along a world axis), if one is that near; else null.</summary>
    private static VesselState? OnACellEdge(VesselState state, float most = 0.5f)
    {
        const float Cell = 400f;
        var at = state.CentreOfMass.ToSingle();
        for (var axis = 0; axis < 3; axis++)
        {
            var x = at[axis];
            var edge = MathF.Round(x / Cell) * Cell;
            if (MathF.Abs(edge - x) > most)
                continue;
            var shift = Vector3.Zero;
            shift[axis] = edge - x;
            return state with { Position = state.Position + shift };
        }
        return null;
    }

    [Fact]
    public void Who_is_chosen_for_the_physics_is_what_the_rule_chooses_by_brute_force_cell_edges_and_hysteresis_included()
    {
        // A field of boosters landed (frozen) 150 m apart, 7 by 7, the flown one in its middle: a
        // chain kept together, more than the cells round each, so each is found through the hashed
        // grid of cells, not by a test against every one kept. Off three of its corners, one more
        // each, outward from it, its centre of mass on a cell's face (a multiple of 400 m): 190 m
        // off (within the handover distance: kept), 250 m (within the hysteresis times it: kept,
        // as every vessel is put in the physics first), 360 m (beyond that from all: it
        // leaves). Each step the choice is the brute force's.
        var (at, _) = Level.Value;
        var (east, north) = (Planet.East(at), Planet.North(at));
        var states = new List<VesselState>();
        for (var i = 0; i < 7; i++)
            for (var j = 0; j < 7; j++)
                states.Add(Booster(states.Count, east * ((i - 3) * 150f) + north * ((j - 3) * 150f), 0.5f, 0f) with { State = FlightState.Landed, Velocity = Vector3.Zero });
        VesselState Edge(int id, int cornerOf, float apart)
        {
            // Out from the corner (away from the field's middle), swung about it and drawn out a
            // little until its centre of mass is within half a metre of a cell's face, then put on it.
            var corner = states[cornerOf].CentreOfMass.ToSingle();
            var outward = Vector3.Normalize(corner - states[24].CentreOfMass.ToSingle());
            var tried = new List<string>();
            for (var degrees = -30f; degrees <= 30f; degrees += 1f)
                for (var d = apart; d < apart + 4f; d += 0.5f)
                {
                    var way = Vector3.Transform(outward, Quaternion.CreateFromAxisAngle(Planet.Up(corner), float.DegreesToRadians(degrees)));
                    var placed = Booster(id, (corner - at) + way * d, 0.5f, 0f) with { State = FlightState.Landed, Velocity = Vector3.Zero };
                    if (OnACellEdge(placed, most: 1.5f) is { } edged)
                        return edged;
                    if (tried.Count < 5)
                        tried.Add($"{placed.CentreOfMass:0.0}");
                }
            throw new InvalidOperationException($"no place on a cell's face off corner {cornerOf}, as {string.Join(" ", tried)}");
        }
        var (near, held, beyond) = (Edge(49, 0, 190f), Edge(50, 6, 250f), Edge(51, 42, 360f));
        states.AddRange([near, held, beyond]);
        using var sim = new Simulation(world: new WorldState { Vessels = [.. states], NextVesselId = states.Count });
        var flight = sim.Flight;
        var flown = Get(sim, 24); // the middle
        var camera = Opposite; // far from all of them: only nearness keeps one
        // All in the physics to begin with (watched for one step: far from the pad, they load
        // under formula), as the hysteresis' case wants.
        sim.Step(new FlightInput(SwitchTo: flown, Camera: camera, Watched: [.. flight.WorldVessels]));
        Assert.All(flight.WorldVessels, v => Assert.False(v.OnFormula));
        var (nearV, heldV, beyondV) = (Get(sim, 49), Get(sim, 50), Get(sim, 51));
        float Nearest(Vessel v) => flight.WorldVessels.Where(o => o != v).Min(o => Vector3.Distance(o.CentreOfMass, v.CentreOfMass));
        output.WriteLine($"each from its nearest: {Nearest(nearV):0.0}, {Nearest(heldV):0.0}, {Nearest(beyondV):0.0} m; "
            + $"centres of mass {nearV.CentreOfMass:0.000}, {heldV.CentreOfMass:0.000}, {beyondV.CentreOfMass:0.000}");
        for (var step = 0; step < 10; step++)
        {
            var inPhysics = flight.WorldVessels.Where(v => !v.OnFormula).ToHashSet();
            sim.Step(new FlightInput(Camera: camera));
            var wanted = Wanted(flight, inPhysics);
            foreach (var v in flight.WorldVessels)
                Assert.True(wanted.Contains(v) == flight.Unwatched.ShouldBeInPhysics(v),
                    $"step {step}: vessel {v.Id} {(wanted.Contains(v) ? "wanted by the rule" : "not wanted by the rule")}, but the choice differs");
        }
        Assert.True(flight.WorldVessels.Where(v => v.Id < 49).All(v => !v.OnFormula), "the field kept together isn't");
        Assert.False(nearV.OnFormula, "the one within the handover distance, on a cell's face, left");
        Assert.False(heldV.OnFormula, "the one within the hysteresis, on a cell's face, left");
        Assert.True(beyondV.OnFormula, "the one beyond the hysteresis is still in the physics");
    }

    [Fact]
    public void One_the_main_view_held_stays_in_the_physics_dropped_from_its_few_by_rank_and_goes_past_its_hold()
    {
        // Six satellites in a line along one circle, 400 m apart (each beyond the hysteresis times
        // the handover distance from the next, so none keeps another), the last flown (so the
        // camera, moving as it does, closes on none of them). The camera by the first: it is among
        // the main view's few. Then the camera by the fifth: four are nearer it, so the first
        // drops from the few by rank, 1,600 m off, within the hold (the hysteresis times
        // NearByDistance): it stays. Then the camera 1,500 m past the fifth: past the hold, it goes.
        const float Altitude = 9000f;
        var line = Enumerable.Range(0, 6).Select(i => Satellite(i, Altitude, 0f, 60f + DegreesApart(Altitude, 400f * i))).ToArray();
        using var sim = new Simulation(world: new WorldState { Vessels = line, NextVesselId = 6 });
        var flight = sim.Flight;
        var vessels = Enumerable.Range(0, 6).Select(i => Get(sim, i)).ToArray();
        var first = vessels[0];
        sim.Step(new FlightInput(SwitchTo: vessels[5], Camera: first.CentreOfMass));
        for (var i = 0; i < 30; i++)
            sim.Step(new FlightInput(Camera: first.CentreOfMass));
        Assert.Contains(first, flight.InViewVessels.ToArray());
        Assert.False(first.OnFormula);

        for (var i = 0; i < 60; i++)
        {
            sim.Step(new FlightInput(Camera: vessels[4].CentreOfMass));
            Assert.DoesNotContain(first, flight.InViewVessels.ToArray());
            Assert.False(first.OnFormula, $"step {i}: dropped from the few by rank, {Vector3.Distance(first.CentreOfMass, vessels[4].CentreOfMass):0} m off, it left the physics");
        }

        var past = vessels[4].CentreOfMass + Vector3.Normalize(vessels[4].CentreOfMass - first.CentreOfMass) * 1500f;
        for (var i = 0; i < 10; i++)
        {
            past += vessels[4].Velocity * Simulation.FrameSeconds;
            sim.Step(new FlightInput(Camera: past));
        }
        output.WriteLine($"{Vector3.Distance(first.CentreOfMass, past):0} m from the camera at the last");
        Assert.True(first.OnFormula, "past the view's hold, nothing near it, it is still in the physics");
    }

    [Fact]
    public void One_brought_into_the_physics_brings_the_other_of_a_collision_found_with_it()
    {
        // Head on at 500 m/s under formula, their collision found and still to come: the first is
        // brought in (as a view of it would), and the second comes with it at once, whatever the
        // queue, so the physics judges the two (which, the second left waiting, would pass through
        // each other). Both are wrecked there.
        var hitPoint = Opposite;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(500f, 5000f, HitsAt, over: hitPoint), hitPoint);
        using var _ = sim;
        var unwatched = sim.Flight.Unwatched;
        Assert.True(sim.Run(Nobody, _ => unwatched.CollisionsAhead > 0 && Vector3.Distance(Middle(a), Middle(b)) < 100f, maxSeconds: HitsAt),
            "their collision was never found before it came");
        Assert.True(a.OnFormula && b.OnFormula);
        sim.Flight.BringIn(a);
        Assert.False(a.OnFormula);
        Assert.False(b.OnFormula, "the other of its collision was left under formula");
        var watched = new[] { a };
        sim.Run((_, _) => new FlightInput(Watched: watched), _ => a.State == FlightState.Crashed && b.State == FlightState.Crashed, maxSeconds: 2);
        output.WriteLine($"{a.State} {a.CrashReason}; {b.State} {b.CrashReason}");
        foreach (var v in new[] { a, b })
        {
            Assert.Equal(FlightState.Crashed, v.State);
            Assert.StartsWith("Hit another vessel", v.CrashReason);
            Assert.True(v.CrashedWatched, "the formula judged it, not the physics");
        }
    }

    [Fact]
    public void One_left_whole_by_a_collision_that_came_to_nothing_is_looked_for_again_from_then()
    {
        // Head on at 2,000 m/s, with a third trailing the second by 40 m: the first's earliest is the
        // second, so the check that finds that keeps no other of the first's. Found, the second is
        // kicked 2,000 m/s aside (as a burn remakes a motion): their collision comes to nothing, and
        // the first, left whole, is looked for again from then, so its collision with the third,
        // some 0.02 s on (likely within the interval already looked at), is found and judged.
        var hitPoint = Opposite;
        var pair = FastCollisionTests.Pair(2000f, 5000f, HitsAt, over: hitPoint);
        var second = pair.Vessels[1];
        var third = second with { Id = 2, Position = second.Position.ToSingle() - Vector3.Normalize(second.Velocity.ToSingle()) * 40f };
        var (sim, a, b) = Unflown(new WorldState { Vessels = [.. pair.Vessels, third], NextVesselId = 3 }, hitPoint);
        using var _ = sim;
        var c = Get(sim, 2);
        var unwatched = sim.Flight.Unwatched;
        var touching = a.Height + 2f * FastCollisionTests.BoosterWidest;
        Assert.True(sim.Run(Nobody, _ => unwatched.CollisionsAhead > 0 && Vector3.Distance(Middle(a), Middle(b)) < touching + 60f, maxSeconds: HitsAt),
            "their collision was never found before it came");
        Assert.True(Vector3.Distance(Middle(a), Middle(b)) > touching, "they touch already");
        Assert.Equal(1, unwatched.CollisionsAhead); // the first's with the second: the third's with the first not kept
        b.Motion = Formula.Begin(GroundBody.Planet, b.CentreOfMass, b.Velocity + Planet.North(b.CentreOfMass) * 2000f, b.FormulaRotation, b.FormulaSpin, sim.Flight.WorldTime, onItsOrbit: true);
        sim.Run(Nobody, _ =>
        {
            Assert.Null(sim.Flight.Slowed);
            return a.State == FlightState.Crashed && c.State == FlightState.Crashed;
        }, maxSeconds: 1);
        output.WriteLine($"{a.State} {a.CrashReason}; {b.State} {b.CrashReason}; {c.State} {c.CrashReason}");
        Assert.NotEqual(FlightState.Crashed, b.State);
        foreach (var v in new[] { a, c })
        {
            Assert.Equal(FlightState.Crashed, v.State);
            Assert.StartsWith("Hit another vessel", v.CrashReason);
            Assert.False(v.CrashedWatched, "the physics judged it, not the formula");
        }
    }

    [Fact]
    public void A_world_loads_those_far_from_the_pad_straight_under_formula_with_no_body_and_they_fly_on_as_in_the_physics()
    {
        // A hundred satellites round the planet, and a booster landed on Home's zone, by the new
        // rocket's pad: each far one nothing would hold in the physics loads under formula, its
        // body never made (a world of a thousand once made them all, only to take them out in its
        // first step, a frame's budget many times over); the booster by the pad loads in the
        // physics. Flown on 3 s, each far one is where it would be had it loaded in the physics
        // and been flown there.
        var zone = Scene.LandingZoneCentre;
        var world = WorldTests.ManySatellites(25);
        world = world with { Vessels = [.. world.Vessels, Drops.Placed(zone, Planet.East(zone), 0f, id: world.Vessels.Count) with { State = FlightState.Landed }], NextVesselId = world.Vessels.Count + 1 };
        using var sim = new Simulation(world: world);
        var flight = sim.Flight;
        var booster = Get(sim, world.Vessels.Count - 1);
        Assert.False(booster.OnFormula, "the booster by the pad loaded under formula");
        var far = flight.WorldVessels.Where(v => v.OnFormula).ToArray();
        output.WriteLine($"{far.Length} of {flight.WorldVessels.Count} loaded under formula");
        Assert.True(far.Length >= 50, $"only {far.Length} loaded under formula");
        Assert.All(far, v => Assert.True(v.Craft.Removed, $"{v.Id} has a body"));
        Assert.All(flight.WorldVessels.Where(v => v.Id < world.Vessels.Count), v =>
            Assert.Equal(Unwatched.MayLoadOnFormula(world.Vessels.Single(s => s.Id == v.Id), GroundBody.Planet, LaunchSites.Home.Pad, new HashSet<int>()), v.OnFormula));
        output.WriteLine($"the nearest under formula {far.Min(v => Vector3.Distance(v.CentreOfMass, LaunchSites.Home.Pad)) / 1000f:0.0} km from the pad, "
            + $"the furthest in the physics {flight.WorldVessels.Where(v => !v.OnFormula).Max(v => Vector3.Distance(v.CentreOfMass, LaunchSites.Home.Pad)) / 1000f:0.0} km");

        using var physics = new Simulation(world: world);
        var all = physics.Flight.WorldVessels.ToArray();
        foreach (var v in all)
            physics.Flight.BringIn(v);
        sim.Run(Nobody, _ => false, maxSeconds: 3);
        physics.Run((_, _) => new FlightInput(Watched: all), _ => false, maxSeconds: 3);
        var worst = far.Max(v => Vector3.Distance(v.CentreOfMass, Get(physics, v.Id).CentreOfMass));
        output.WriteLine($"3 s on, the furthest from where the physics has it: {worst:0.000} m");
        Assert.InRange(worst, 0f, 0.5f); // Kepler against Jolt's steps: some 0.1 m in 3 s (FormulaTests' own bound is 1 m in 60 s)
    }

    [Fact]
    public void One_afloat_with_an_end_over_a_floe_loads_in_the_physics_not_under_formula()
    {
        // Afloat beside a floe (the one furthest from the pad), lying with its top end over the
        // floe's edge: restored, the floe would board it (and carry it), so it may not load under
        // formula. The same afloat drawn away along its way, clear of every floe, may.
        Rafts.Time = 0.0;
        var floe = Floes.All.MaxBy(f => Vector3.Distance(f.Centre, LaunchSites.Home.Pad))!;
        floe.Set(0.0);
        var up = Planet.Up(floe.Centre);
        var lying = Quaternion.Concatenate(Planet.UprightAt(floe.Centre), Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(up, floe.Heading)), MathF.PI / 2f));
        var nose = Vector3.Transform(Vector3.UnitY, lying);
        var height = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        // Its top 2 m in over the rim, the rest of it out over the water, at the sea's level.
        var top = floe.Centre + nose * (floe.HalfLength - 2f);
        var water = Planet.Centre + up * Planet.SeaRadius;
        var at = top - nose * height;
        at += up * Vector3.Dot(water - at, up);
        var afloat = Drops.Placed(floe.Centre, floe.Heading, 0f, legsOut: false) with { State = FlightState.Floating, Position = at, Rotation = lying, Velocity = Vector3.Zero };
        Assert.False(Unwatched.MayLoadOnFormula(afloat, GroundBody.Planet, LaunchSites.Home.Pad, new HashSet<int>()), "afloat with an end over a floe, it may load under formula");

        var clear = false;
        for (var away = 10f; away <= 400f && !clear; away += 10f)
            clear = Unwatched.MayLoadOnFormula(afloat with { Position = afloat.Position - nose * away }, GroundBody.Planet, LaunchSites.Home.Pad, new HashSet<int>());
        Assert.True(clear, "drawn away from the floe, clear of every raft, it still may not load under formula");
    }

    [Fact]
    public void A_world_loaded_at_the_same_time_starts_its_choice_and_its_collisions_afresh()
    {
        // A collision under formula found and still to come; the world captured and loaded again at
        // that same time: nothing of the last world's is kept (its collision, who was chosen for the
        // physics), and the new world's two are found again and wrecked as they collide.
        var hitPoint = Opposite;
        var (sim, a, b) = Unflown(FastCollisionTests.Pair(500f, 5000f, HitsAt, over: hitPoint), hitPoint);
        using var _ = sim;
        var unwatched = sim.Flight.Unwatched;
        Assert.True(sim.Run(Nobody, _ => unwatched.CollisionsAhead > 0, maxSeconds: HitsAt), "their collision was never found");
        var flight = sim.Flight;
        var flownId = flight.Controlled.Id;
        flight.Load(flight.Capture(), flight.Mission);
        Assert.Equal(0, unwatched.CollisionsAhead);
        Assert.All(flight.WorldVessels, v => Assert.False(unwatched.ShouldBeInPhysics(v)));
        Assert.All(new[] { a, b }, v => Assert.False(unwatched.ShouldBeInPhysics(v))); // nor the last world's
        var (a2, b2) = (Get(sim, a.Id), Get(sim, b.Id));
        sim.Step(new FlightInput(SwitchTo: Get(sim, flownId)));
        RunUnwatched(sim, a2, b2, HitsAt + 2f - sim.Time);
        BothWrecked(a2, b2, hitPoint);
    }
}
