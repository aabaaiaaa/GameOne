using System.Numerics;
using GameOne.Physics;
using GameOne.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The lander's bay doors (<see cref="Vessel.BayDoors"/>, <see cref="FlightInput.ToggleBayDoors"/>):
/// they open and close in their time; landed, open, they reach down to the ground as ramps, on
/// level ground and on a slope, without lifting the lander; they open in flight and at touchdown
/// as any part is judged; a vessel without a bay has none; and their collider and how far open
/// they are survive the formula and a save.
/// </summary>
public sealed class BayDoorTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;
    private static GroundBody Moon => GroundBody.Moon;
    private static readonly FlightInput Toggle = new(ToggleBayDoors: true);

    /// <summary>The lander alone over the landing spot, its legs out and its engine off, its lowest foot <paramref name="clearance"/> over the ground, falling at <paramref name="speed"/>, flown.</summary>
    /// <remarks>With <paramref name="saved"/>, it is the lander launcher's own lander (the test vehicle isn't one a world can be saved with).</remarks>
    private static (Simulation Sim, Vessel Lander) OverTheSpot(float speed, float clearance, bool saved = false)
    {
        var at = SpotSky.Place;
        var state = saved
            ? Drops.Placed(at, Moon.East(at), speed, Vehicle.LanderLauncher, first: 2, clearance: clearance, mission: Mission.FreePlay, body: Moon, fuel: 0.5f)
            : Drops.Placed(at, Moon.East(at), speed, LanderTests.Alone, clearance: clearance, mission: Mission.FreePlay, body: Moon, fuel: 0.5f);
        var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 }, body: Moon);
        var lander = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: lander));
        return (sim, lander);
    }

    /// <summary>Runs until <paramref name="lander"/> has landed and frozen (at rest, as one landed is), or crashed.</summary>
    private static void Settle(Simulation sim, Vessel lander, double maxSeconds = 40) =>
        sim.Run(Nobody, _ => lander.State == FlightState.Crashed || lander.State == FlightState.Landed && lander.Craft.Frozen, maxSeconds);

    /// <summary>Opens or closes the flown vessel's doors, and runs until they are there and it has settled again (or <paramref name="maxSeconds"/>).</summary>
    private static void MoveDoors(Simulation sim, Vessel lander, double maxSeconds = 30)
    {
        sim.Step(Toggle);
        var target = lander.BayDoorsWanted ? 1f : 0f;
        sim.Run(Nobody, _ => lander.State == FlightState.Crashed || lander.BayDoors == target && lander.Craft.Frozen, maxSeconds);
    }

    /// <summary>The angle between two attitudes' up axes, in degrees: how far it has turned from one to the other.</summary>
    private static float AngleBetween(Quaternion a, Quaternion b) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Transform(Vector3.UnitY, a), Vector3.Transform(Vector3.UnitY, b)), -1f, 1f)));

    [Fact]
    public void The_bay_doors_open_in_the_doors_time_and_close_again()
    {
        var (sim, lander) = OverTheSpot(0f, 300f);
        using var _ = sim;
        Assert.True(lander.HasBay);
        Assert.Equal(0f, lander.BayDoors);
        foreach (var target in (ReadOnlySpan<float>)[1f, 0f])
        {
            var start = sim.Time;
            sim.Step(Toggle);
            Assert.Equal(target == 1f, lander.BayDoorsWanted);
            double? halfway = null;
            sim.Run(Nobody, _ =>
            {
                if (halfway is null && MathF.Abs(lander.BayDoors - target) <= 0.5f)
                    halfway = sim.Time - start;
                // The collider has the ramps only once they are fully open, and not as soon as they start to close.
                Assert.Equal(lander.BayDoors >= 1f, lander.Craft.DoorsOpen);
                return lander.BayDoors == target;
            }, maxSeconds: 10);
            var took = sim.Time - start;
            output.WriteLine($"to {target}: half-way at {halfway:0.00} s, there at {took:0.00} s");
            Assert.Equal(target, lander.BayDoors);
            Assert.InRange(took, 0.9 * Scene.LanderDoorSeconds, Scene.LanderDoorSeconds + 0.1);
            Assert.Equal(target == 1f, lander.Craft.DoorsOpen);
        }
    }

    [Fact]
    public void Landed_on_level_ground_the_open_doors_reach_down_to_it_as_ramps()
    {
        var (sim, lander) = OverTheSpot(0.3f, 0.2f);
        using var _ = sim;
        Settle(sim, lander);
        Assert.Equal(FlightState.Landed, lander.State);
        var (before, tilt) = (lander.Position, lander.Rotation);
        MoveDoors(sim, lander);
        Assert.Equal(1f, lander.BayDoors);
        Assert.True(lander.Craft.DoorsOpen);
        sim.Run(Nobody, _ => false, maxSeconds: 3); // and a while longer, at rest there
        var moved = Vector3.Distance(before, lander.Position);
        output.WriteLine($"{lander.State}: legs out {lander.Craft.LegExtension:0.00}; open {RocketGeometry.DoorOpenRadians * 180f / MathF.PI:0.0}° from shut; moved {moved * 100:0.00} cm, turned {AngleBetween(tilt, lander.Rotation):0.000}°");
        for (var door = 0; door < RocketGeometry.DoorCount; door++)
        {
            var tip = Moon.GroundAltitude(Vector3.Transform(RocketGeometry.RampTip(door), lander.Model));
            output.WriteLine($"door {door}: its ramp's tip {tip:0.000} m over the ground");
            Assert.InRange(tip, 0f, 0.2f);
        }
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(moved < 0.01f, $"it moved {moved:0.000} m");
    }

    [Fact]
    public void On_a_slope_the_doors_open_onto_the_ground_without_lifting_or_wrecking_the_lander()
    {
        // On the planet's 10° test pad: its pull is six times the moon's, the harder case for the legs and the ramps.
        using var sim = Drops.OnPad(1, 1f, LanderTests.Alone);
        var lander = sim.Flight.Controlled;
        Settle(sim, lander);
        Assert.Equal(FlightState.Landed, lander.State);
        var (landed, tilt) = (lander.Position, lander.TiltDegrees);
        output.WriteLine($"landed: leaning {tilt:0.00}°, legs out {lander.Craft.LegExtension:0.00}");
        foreach (var target in (ReadOnlySpan<float>)[1f, 0f])
        {
            MoveDoors(sim, lander);
            // The doors leave it frozen where it stands: woken (as something coming near would),
            // it stands on its new collider, ramps and all, in the physics.
            sim.Physics.Wake(lander.Craft);
            sim.Run(Nobody, _ => lander.State == FlightState.Crashed || lander.Craft.Frozen, maxSeconds: 20);
            Assert.True(lander.Craft.Frozen, "woken, it never came to rest again");
            var moved = Vector3.Distance(landed, lander.Position);
            // Each ramp's tip as it is (stopped on the ground, if it came down onto it), and at the doors' full swing.
            var tips = string.Join(", ", Enumerable.Range(0, RocketGeometry.DoorCount).Select(door =>
            {
                var stop = lander.Craft.DoorStops[door];
                var tip = Vector3.Transform(RocketGeometry.RampTipAt(door, RocketGeometry.DoorAngle(lander.BayDoors, stop)), lander.Model);
                return $"{Planet.GroundAltitude(tip):0.000} (stopped at {float.RadiansToDegrees(stop):0.0}°; at the full swing {Planet.GroundAltitude(Vector3.Transform(RocketGeometry.RampTip(door), lander.Model)):0.000})";
            }));
            output.WriteLine($"doors {target}: {lander.State} {lander.CrashReason}, moved {moved * 100:0.00} cm, leaning {lander.TiltDegrees:0.00}°; the ramps' tips {tips} m over the ground");
            Assert.Equal(target, lander.BayDoors);
            Assert.Equal(FlightState.Landed, lander.State);
            Assert.True(moved < 0.05f, $"it moved {moved:0.000} m");
            Assert.True(lander.TiltDegrees - tilt < 0.5f, $"it leans {lander.TiltDegrees:0.00}°, from {tilt:0.00}°");
            for (var door = 0; target == 1f && door < RocketGeometry.DoorCount; door++)
            {
                // Open, each ramp reaches down to the ground (or rests on it), and isn't in it.
                var tip = Vector3.Transform(RocketGeometry.RampTipAt(door, RocketGeometry.DoorAngle(1f, lander.Craft.DoorStops[door])), lander.Model);
                Assert.InRange(Planet.GroundAltitude(tip), -0.01f, 0.2f);
            }
        }
    }

    [Fact]
    public void Touching_down_with_the_doors_open_is_judged_as_any_landing()
    {
        var (sim, lander) = OverTheSpot(2f, 30f);
        using var _ = sim;
        sim.Step(Toggle);
        // Down at 2 m/s on its engine, cut as its feet reach the ground.
        sim.Run((flight, dt) =>
        {
            if (lander.Craft.FeetDown != 0 || lander.State != FlightState.Flying)
                return new FlightInput(CutThrottle: true);
            var wanted = (Scene.MoonSurfaceGravity + 2f * (-2f - flight.VerticalSpeed)) / flight.MaxAcceleration;
            return new FlightInput(Throttle: Pilots.ThrottleTowards(flight, Math.Clamp(wanted, 0f, 1f), dt));
        }, _ => lander.State != FlightState.Flying, maxSeconds: 60);
        output.WriteLine($"{lander.State} {lander.CrashReason}: doors {lander.BayDoors:0.00}, touchdown {lander.TouchdownSpeed:0.00} m/s");
        Assert.Equal(1f, lander.BayDoors);
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(lander.TouchdownSpeed < 2.5f, $"touched down at {lander.TouchdownSpeed:0.00} m/s");
    }

    [Fact]
    public void Opened_in_flight_the_doors_open_and_the_lander_flies_on()
    {
        var height = 2000f;
        var centreOfMass = Moon.Centre + Vector3.UnitX * (Moon.SeaRadius + Moon.OrbitFloor + height);
        var stars = Moon.East(centreOfMass) * Moon.CircularSpeed(Moon.OrbitFloor + height);
        var rotation = Moon.UprightAt(centreOfMass);
        var offset = RocketGeometry.CentreOfMassOffset(LanderTests.Alone, 0, 0);
        var state = new VesselState
        {
            Id = 0, Flight = 1, Mission = Mission.FreePlay, Vehicle = LanderTests.Alone, First = 0, Last = 0,
            Fuel = [LanderTests.Alone.Stages[0].FuelMass * 0.5f],
            State = FlightState.Flying, Position = centreOfMass - Vector3.Transform(offset, rotation), Rotation = rotation,
            CentreOfMassOffset = offset, Velocity = Moon.OverGround(centreOfMass, stars), AngularVelocity = Vector3.Zero,
            Aim = Vector3.Transform(Vector3.UnitY, rotation), HasBeenAirborne = true, MaxAltitude = Moon.OrbitFloor + height,
            Body = Moon.Name,
        };
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 }, body: Moon);
        var lander = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: lander));
        var (apoapsis, periapsis) = (lander.Apoapsis, lander.Periapsis);
        sim.Step(Toggle);
        sim.Run(Nobody, _ => lander.BayDoors >= 1f, maxSeconds: 10);
        sim.Run(Nobody, _ => false, maxSeconds: 2);
        output.WriteLine($"{lander.State}: doors {lander.BayDoors:0.00}; apoapsis {apoapsis:0.0} -> {lander.Apoapsis:0.0} m, periapsis {periapsis:0.0} -> {lander.Periapsis:0.0} m");
        Assert.Equal(FlightState.Flying, lander.State);
        Assert.True(lander.Craft.DoorsOpen);
        // Its collider's centre of mass where the shut one's is: the ramps don't change how it flies.
        var (shut, open) = (lander.Craft.Stowed.GetCenterOfMass(), lander.Craft.Open!.Value.GetCenterOfMass());
        output.WriteLine($"centre of mass shut {shut.Y:0.0000} m up, open {open.Y:0.0000}");
        Assert.InRange(open.Y - shut.Y, -0.01f, 0.01f);
        Assert.InRange(lander.Apoapsis - apoapsis, -1f, 1f);
        Assert.InRange(lander.Periapsis - periapsis, -1f, 1f);
    }

    [Fact]
    public void A_vessel_without_a_bay_has_no_doors_to_open()
    {
        using var sim = Drops.OnPad(0, 1f);
        var booster = sim.Flight.Controlled;
        Assert.False(booster.HasBay);
        sim.Step(Toggle);
        sim.Run(Nobody, _ => false, maxSeconds: Scene.LanderDoorSeconds + 1);
        Assert.Equal(0f, booster.BayDoors);
        Assert.False(booster.BayDoorsWanted);
        Assert.False(booster.Craft.DoorsOpen);
        Assert.Null(booster.Craft.Open);
        Assert.Null(sim.Flight.Capture().Vessels.Single(v => v.Id == booster.Id).BayDoorStops); // saves no stops
    }

    [Fact]
    public void The_doors_swinging_on_level_ground_leave_the_frozen_lander_frozen()
    {
        var (sim, lander) = OverTheSpot(0.3f, 0.2f);
        using var _ = sim;
        Settle(sim, lander);
        Assert.True(lander.Craft.Frozen);
        Assert.Null(lander.Craft.Open); // its open colliders not made until its doors first open
        var steps = 0;
        var frames = new List<double>();
        double? made = null; // the frame its open colliders were made in, its time
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var wanted in (ReadOnlySpan<bool>)[true, false])
        {
            sim.Step(Toggle);
            clock.Restart();
            sim.Run(Nobody, _ =>
            {
                steps++;
                var ms = clock.Elapsed.TotalMilliseconds;
                if (made is null && lander.Craft.Open is not null)
                    made = ms;
                else
                    frames.Add(ms);
                Assert.True(lander.Craft.Frozen, $"woken at {lander.BayDoors:0.00} open");
                clock.Restart();
                return lander.BayDoors == (wanted ? 1f : 0f);
            }, maxSeconds: 10);
            Assert.Equal(wanted ? 1f : 0f, lander.BayDoors);
        }
        frames.Sort();
        output.WriteLine($"frozen through {steps} frames of the doors' swing, open and shut; the frame they locked open in (its open colliders made) {made:0.00} ms, the others' median {frames[frames.Count / 2]:0.00} ms");
        Assert.NotNull(lander.Craft.Open);
    }

    [Fact]
    public void Doors_opened_before_touchdown_are_raised_onto_the_ground_as_the_lander_lands()
    {
        // On the planet's 10° pad, where the legs give the most; the lander can't hover in its
        // pull (1.8 times its weight is the moon's), so it comes down from the drops' 0.3 m with
        // its doors already open, against the same drop with them shut.
        var centre = Scene.TestPads[1].Centre;
        Vector3 Landed(bool open)
        {
            var state = Drops.Placed(centre, Planet.East(centre), 1f, LanderTests.Alone);
            if (open)
                state = state with { BayDoors = 1f, BayDoorsWanted = true };
            using var sim = new Simulation(Mission.LaunchAndLand, world: new WorldState { Vessels = [state], NextVesselId = 1 });
            var lander = sim.Flight.WorldVessels.Single(v => v.Id == 0);
            sim.Step(new FlightInput(SwitchTo: lander));
            var swung = Swings(lander);
            sim.Run(Nobody, _ =>
            {
                swung.Next();
                return lander.State == FlightState.Crashed || lander.State == FlightState.Landed && lander.Craft.Frozen && lander.Craft.DoorsOpen == open;
            }, maxSeconds: 40);
            Assert.Equal(FlightState.Landed, lander.State);
            Assert.True(lander.Craft.Frozen);
            if (open)
            {
                output.WriteLine($"its doors swung at most {swung.Most:0.0000} rad a frame, {swung.Moved:0.000} rad in all, against the doors' pace of {Pace:0.0000}");
                Assert.True(swung.Moved * RocketGeometry.RampLength > 0.08f, "no ramp's tip swung onto the ground: this shows nothing");
                Assert.True(lander.Craft.DoorsOpen);
                for (var door = 0; door < RocketGeometry.DoorCount; door++)
                {
                    var stop = lander.Craft.DoorStops[door];
                    var tip = Planet.GroundAltitude(Vector3.Transform(RocketGeometry.RampTipAt(door, stop), lander.Model));
                    var full = Planet.GroundAltitude(Vector3.Transform(RocketGeometry.RampTip(door), lander.Model));
                    output.WriteLine($"door {door}: stopped at {float.RadiansToDegrees(stop):0.0}°, its tip {tip:0.000} m over the ground (at the full swing {full:0.000})");
                    Assert.InRange(tip, -0.01f, 0.2f);
                }
            }
            output.WriteLine($"doors {(open ? "open" : "shut")}: {lander.State}, touchdown {lander.TouchdownSpeed:0.00} m/s, leaning {lander.TiltDegrees:0.00}°");
            return lander.Position;
        }
        var (shut, open) = (Landed(open: false), Landed(open: true));
        var lifted = Vector3.Dot(open - shut, Planet.Up(centre));
        output.WriteLine($"with its doors open it stands {lifted * 100:0.00} cm higher, {Vector3.Distance(open, shut) * 100:0.00} cm from where it does shut");
        Assert.True(lifted < 0.05f, $"lifted {lifted:0.000} m");
    }

    /// <summary>The most a door may swing in a frame, open: the doors' pace (their full swing in <see cref="Scene.LanderDoorSeconds"/>), a frame's worth.</summary>
    private static float Pace => RocketGeometry.DoorOpenRadians / Scene.LanderDoorSeconds * Simulation.FrameSeconds;

    /// <summary>Follows <paramref name="lander"/>'s doors as drawn, frame by frame (<see cref="Swing.Next"/>), asserting neither ever jumps: swings faster than the doors' pace (and their easing's steepest, 1.5 times it).</summary>
    private static Swing Swings(Vessel lander) => new(lander);

    private sealed class Swing(Vessel lander)
    {
        private Vector2? _last;
        public float Most { get; private set; }
        public float Moved { get; private set; }

        public void Next()
        {
            var now = new Vector2(RocketGeometry.DoorAngle(lander.BayDoors, lander.Craft.DoorStops.X), RocketGeometry.DoorAngle(lander.BayDoors, lander.Craft.DoorStops.Y));
            if (_last is { } last)
            {
                var step = MathF.Max(MathF.Abs(now.X - last.X), MathF.Abs(now.Y - last.Y));
                (Most, Moved) = (MathF.Max(Most, step), Moved + step);
                Assert.True(step <= 1.5f * Pace + 1e-5f, $"a door jumped {step:0.0000} rad in a frame");
            }
            _last = now;
        }
    }

    [Fact]
    public void A_lander_hopping_with_its_doors_open_comes_down_and_lands_again_on_its_legs()
    {
        // Landed at the spot, its doors open, it lifts off on its engine, rises a few metres, and comes down again.
        var (sim, lander) = OverTheSpot(0.3f, 0.2f);
        using var _ = sim;
        Settle(sim, lander);
        MoveDoors(sim, lander);
        Assert.True(lander.Craft.DoorsOpen);
        var landed = lander.Position;
        var swung = Swings(lander);
        var (rose, rampsOutComing, rampsInFlying) = (0f, false, false);
        sim.Run((flight, dt) =>
        {
            swung.Next();
            rose = MathF.Max(rose, flight.Ground);
            if (lander.State == FlightState.Flying && flight.Ground > 1.5f && flight.VerticalSpeed > 0f)
                rampsInFlying |= lander.Craft.DoorsOpen; // clear of the ground, its ramps are back
            if (rose > 1.5f && lander.State == FlightState.Flying && !lander.Craft.DoorsOpen && flight.VerticalSpeed <= 0f)
                rampsOutComing = true; // and coming down onto it, out again
            return rose < 3f ? new FlightInput(Throttle: Pilots.ThrottleTowards(flight, 0.7f, dt)) : new FlightInput(CutThrottle: true);
        }, _ => rose >= 3f && lander.State == FlightState.Landed && lander.Craft.Frozen, maxSeconds: 60);
        output.WriteLine($"{lander.State}: rose {rose:0.0} m, touchdown {lander.TouchdownSpeed:0.00} m/s; ramps in clear of the ground {rampsInFlying}, out coming down {rampsOutComing}; " +
            $"{Vector3.Distance(landed, lander.Position):0.00} m from where it lifted off; its doors swung at most {swung.Most:0.0000} rad a frame");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(rampsInFlying, "its ramps never came back into its collider in flight");
        Assert.True(rampsOutComing, "its ramps stayed in its collider as it came down");
        Assert.True(lander.Craft.DoorsOpen, "landed again, its ramps are not back in its collider");
        for (var door = 0; door < RocketGeometry.DoorCount; door++)
        {
            var tip = Moon.GroundAltitude(Vector3.Transform(RocketGeometry.RampTipAt(door, lander.Craft.DoorStops[door]), lander.Model));
            output.WriteLine($"door {door}: its ramp's tip {tip:0.000} m over the ground");
            Assert.InRange(tip, -0.01f, 0.2f);
        }
    }

    [Fact]
    public void A_lander_landed_under_formula_with_its_doors_open_and_handed_back_settles_with_its_ramps_on_the_ground()
    {
        // Nobody watching it (a satellite far off is flown), it comes down under formula onto the
        // planet's 10° pad with its doors open; then it is handed back to the physics.
        var centre = Scene.TestPads[1].Centre;
        var state = Drops.Placed(centre, Planet.East(centre), 1f, LanderTests.Alone) with { BayDoors = 1f, BayDoorsWanted = true };
        var (sim, lander, watch) = FormulaTests.Start(state, watched: false);
        using var _ = sim;
        FormulaTests.Fly(sim, lander, watch, 20, v => v.State != FlightState.Flying);
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(lander.OnFormula);
        var stops = lander.Craft.DoorStops;
        output.WriteLine($"landed under formula: its doors stopped at {float.RadiansToDegrees(stops.X):0.0}°, {float.RadiansToDegrees(stops.Y):0.0}° (their full swing {float.RadiansToDegrees(RocketGeometry.DoorOpenRadians):0.0}°)");
        // Any door that would be in the ground swinging up onto it, there: its stop still a frame, and the ramps back in.
        var last = new Vector2(float.NaN);
        sim.Run((_, _) => default, _ =>
        {
            var still = lander.Craft.DoorStops == last;
            last = lander.Craft.DoorStops;
            return still && lander.Craft.DoorsOpen;
        }, maxSeconds: 10);
        output.WriteLine($"then at {float.RadiansToDegrees(last.X):0.0}°, {float.RadiansToDegrees(last.Y):0.0}°");
        var at = lander.Position;
        lander.ReturnToPhysics(sim.Flight.WorldTime);
        Assert.False(lander.OnFormula);
        Assert.True(lander.Craft.DoorsOpen, "handed back, its collider has no ramps");
        for (var door = 0; door < RocketGeometry.DoorCount; door++)
        {
            var tip = Planet.GroundAltitude(Vector3.Transform(RocketGeometry.RampTipAt(door, lander.Craft.DoorStops[door]), lander.Model));
            output.WriteLine($"door {door}: stopped at {float.RadiansToDegrees(lander.Craft.DoorStops[door]):0.0}°, its ramp's tip {tip:0.000} m over the ground");
            Assert.True(tip > -0.01f, "a ramp is in the ground"); // the formula sets it down upright (it never tips): over a slope, the upslope ramp rests on it, the other hangs over it
        }
        // Woken in the physics (watched, so it stays there), it settles onto the slope on its legs, and lands again: each ramp
        // is on the ground, none in it.
        sim.Physics.Wake(lander.Craft);
        var swung = Swings(lander);
        Vessel[] watched = [lander];
        sim.Run((_, _) => { swung.Next(); return new FlightInput(Watched: watched); }, _ => lander.State == FlightState.Crashed || lander.Craft.Frozen && lander.Craft.DoorsOpen, maxSeconds: 20);
        output.WriteLine($"woken: {lander.State}, moved {Vector3.Distance(at, lander.Position) * 100:0.00} cm, leaning {lander.TiltDegrees:0.00}°; its doors swung at most {swung.Most:0.0000} rad a frame");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(lander.Craft.DoorsOpen);
        for (var door = 0; door < RocketGeometry.DoorCount; door++)
        {
            var tip = Planet.GroundAltitude(Vector3.Transform(RocketGeometry.RampTipAt(door, lander.Craft.DoorStops[door]), lander.Model));
            output.WriteLine($"door {door}: stopped at {float.RadiansToDegrees(lander.Craft.DoorStops[door]):0.0}°, its ramp's tip {tip:0.000} m over the ground");
            Assert.InRange(tip, -0.01f, 0.2f);
        }
    }

    [Fact]
    public void Open_its_outline_reaches_out_to_its_ramps()
    {
        // Two landers on their bells (legs stowed) on the landing zone, 9 m apart along the first's
        // X (its +X door's side, its ramp reaching nearly 6 m out): how near the physics takes them to be, the first's doors shut and open.
        var zone = Scene.LandingZoneCentre;
        var first = Drops.Placed(zone, Planet.East(zone), 0f, LanderTests.Alone, legsOut: false, clearance: 0.02f, mission: Mission.FreePlay);
        var second = first with { Id = 1, Position = first.Position + Vector3.Transform(Vector3.UnitX * 9f, first.Rotation) };
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [first, second], NextVesselId = 2 });
        var (a, b) = (sim.Flight.WorldVessels.Single(v => v.Id == 0), sim.Flight.WorldVessels.Single(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: a));
        var shut = sim.Physics.Apart(a.Craft, b.Craft, 10f);
        sim.Step(Toggle);
        sim.Run(Nobody, _ => a.BayDoors >= 1f, maxSeconds: 10);
        var open = sim.Physics.Apart(a.Craft, b.Craft, 10f);
        var reach = MathF.Max(RocketGeometry.RampReach(a.Craft.DoorStops.X), RocketGeometry.RampReach(a.Craft.DoorStops.Y));
        output.WriteLine($"apart: shut {shut:0.000} m, open {open:0.000} m; its ramps reach {reach:0.000} m out (stopped at {float.RadiansToDegrees(a.Craft.DoorStops.X):0.0}°, {float.RadiansToDegrees(a.Craft.DoorStops.Y):0.0}°)");
        Assert.True(a.Craft.DoorsOpen);
        Assert.Equal(reach - RocketGeometry.ShutReach, shut - open, 0.05f);
    }

    /// <summary>Whether a ray straight down its own axis onto the middle of the lander's +X ramp, fully open (in its own space), meets <paramref name="shape"/>: whether that collider has the ramp.</summary>
    private static bool HasRamp(Brutal.JoltApi.Shape shape)
    {
        var tip = RocketGeometry.RampTip(0);
        var hinge = new Vector3(Scene.LanderHalfWidth, Scene.LanderBayFloor, 0f);
        var middle = (tip + hinge) / 2f;
        var com = shape.GetCenterOfMass();
        var origin = middle + new Vector3(0f, 1f, 0f) - new Vector3(com.X, com.Y, com.Z);
        var ray = new Brutal.JoltApi.RayCast { Origin = new(origin.X, origin.Y, origin.Z), Direction = new(0f, -2f, 0f) };
        var hit = new Brutal.JoltApi.RayCastResult { Fraction = 1.0001f };
        using var ids = Brutal.JoltApi.SubShapeIDCreator.Create();
        return shape.CastRay(in ray, ids, ref hit);
    }

    /// <summary>How far out along its +X axis, from its axis, a ray in towards it from 3 m out at <paramref name="height"/> first meets <paramref name="shape"/> (in its own space), if it does.</summary>
    private static float? ReachAlongX(Brutal.JoltApi.Shape shape, float height)
    {
        const float from = 3f;
        var com = shape.GetCenterOfMass();
        var origin = new Vector3(from, height, 0f) - new Vector3(com.X, com.Y, com.Z);
        var ray = new Brutal.JoltApi.RayCast { Origin = new(origin.X, origin.Y, origin.Z), Direction = new(-from, 0f, 0f) };
        var hit = new Brutal.JoltApi.RayCastResult { Fraction = 1.0001f };
        using var ids = Brutal.JoltApi.SubShapeIDCreator.Create();
        return shape.CastRay(in ray, ids, ref hit) ? from * (1f - hit.Fraction) : null;
    }

    [Fact]
    public void Shut_its_collider_reaches_out_to_the_panels_folded_on_its_doors()
    {
        // A lander on its side, its doors shut, touches with its X faces where they are drawn: out to the folded panels.
        var (sim, lander) = OverTheSpot(0.3f, 0.2f);
        using var _ = sim;
        Settle(sim, lander);
        Assert.False(lander.OnFormula);
        var height = Scene.LanderBayFloor + Scene.LanderDoorHeight / 2f;
        var reach = ReachAlongX(lander.Craft.Collider, height);
        output.WriteLine($"its shut collider reaches {reach:0.000} m out along X, the folded panels {Scene.LanderHalfWidth + RocketGeometry.FoldedDepth:0.000}");
        Assert.False(lander.Craft.DoorsOpen);
        Assert.NotNull(reach);
        Assert.Equal(Scene.LanderHalfWidth + RocketGeometry.FoldedDepth, reach.Value, 0.01f);
    }

    [Fact]
    public void With_its_doors_open_a_lander_is_as_wide_as_its_ramps_reach()
    {
        var (sim, lander) = OverTheSpot(0.3f, 0.2f);
        using var _ = sim;
        Settle(sim, lander);
        var (width, reach, size) = (lander.Width, lander.Reach, Collisions.Size(lander));
        MoveDoors(sim, lander);
        Assert.True(lander.Craft.DoorsOpen);
        var ramps = MathF.Max(RocketGeometry.RampReach(lander.Craft.DoorStops.X), RocketGeometry.RampReach(lander.Craft.DoorStops.Y));
        output.WriteLine($"ramps reach {ramps:0.00} m; width {width:0.00} -> {lander.Width:0.00}");
        Assert.True(lander.Width >= ramps);
        Assert.True(lander.Width > width);
        Assert.True(lander.Reach > reach);
        Assert.True(Collisions.Size(lander) > size);
        // Under formula (its stand-in, as an unwatched landed lander has), as wide.
        lander.LeavePhysics(sim.Flight.WorldTime);
        Assert.True(lander.OnFormula);
        Assert.True(lander.Width >= ramps, $"under formula {lander.Width:0.00} m wide");
    }

    [Fact]
    public void A_removed_crafts_collider_is_refused()
    {
        var (sim, lander) = OverTheSpot(0.3f, 0.2f);
        using var _ = sim;
        Settle(sim, lander);
        lander.LeavePhysics(sim.Flight.WorldTime);
        Assert.True(lander.Craft.Removed);
        Assert.Throws<ObjectDisposedException>(() => lander.Craft.Collider);
    }

    [Fact]
    public void Under_formula_and_back_the_open_doors_keep_their_collider()
    {
        var (sim, lander) = OverTheSpot(0.3f, 0.2f);
        using var _ = sim;
        Settle(sim, lander);
        MoveDoors(sim, lander);
        Assert.True(lander.Craft.DoorsOpen);
        Assert.True(HasRamp(lander.Craft.Collider));
        Assert.False(HasRamp(lander.Craft.Deployed!.Value), "the shut collider has a ramp: this shows nothing");
        var at = lander.Position;
        lander.LeavePhysics(sim.Flight.WorldTime);
        Assert.True(lander.OnFormula);
        Assert.Equal(1f, lander.BayDoors);
        lander.ReturnToPhysics(sim.Flight.WorldTime);
        Assert.False(lander.OnFormula);
        Assert.True(lander.Craft.DoorsOpen, "back in the physics, its collider has its doors shut");
        Assert.True(HasRamp(lander.Craft.Collider));
        sim.Run(Nobody, _ => false, maxSeconds: 3);
        output.WriteLine($"{lander.State}: moved {Vector3.Distance(at, lander.Position) * 100:0.00} cm");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(Vector3.Distance(at, lander.Position) < 0.01f);
    }

    [Fact]
    public void A_lander_saved_with_its_doors_opening_loads_and_finishes_opening()
    {
        var (sim, lander) = OverTheSpot(0.3f, 0.2f, saved: true);
        Settle(sim, lander);
        sim.Step(Toggle);
        sim.Run(Nobody, _ => lander.BayDoors >= 0.5f, maxSeconds: 10);
        var saved = lander.BayDoors;
        using var back = sim.Reloaded();
        var loaded = back.Flight.WorldVessels.Single(v => v.Id == 0);
        output.WriteLine($"saved {saved:0.000} open, loaded {loaded.BayDoors:0.000}, wanted {loaded.BayDoorsWanted}");
        Assert.Equal(saved, loaded.BayDoors);
        Assert.True(loaded.BayDoorsWanted);
        back.Step(new FlightInput(SwitchTo: loaded));
        back.Run(Nobody, _ => loaded.BayDoors >= 1f, maxSeconds: Scene.LanderDoorSeconds);
        Assert.Equal(1f, loaded.BayDoors);
        Assert.True(loaded.Craft.DoorsOpen);
        Assert.Equal(FlightState.Landed, loaded.State);
    }

    /// <summary>A box, as its centre and its three half-edges (each its direction and half its length), in the lander's space.</summary>
    private readonly record struct Block(Vector3 Centre, Vector3 X, Vector3 Y, Vector3 Z)
    {
        /// <summary>The box <paramref name="half"/> its size round <paramref name="centre"/> in some space, carried into the lander's by <paramref name="into"/>.</summary>
        public static Block Of(Vector3 centre, Vector3 half, Matrix4x4 into) => new(Vector3.Transform(centre, into),
            Vector3.TransformNormal(new Vector3(half.X, 0, 0), into), Vector3.TransformNormal(new Vector3(0, half.Y, 0), into), Vector3.TransformNormal(new Vector3(0, 0, half.Z), into));

        public IEnumerable<Vector3> Corners()
        {
            foreach (var x in (int[])[-1, 1])
                foreach (var y in (int[])[-1, 1])
                    foreach (var z in (int[])[-1, 1])
                        yield return Centre + x * X + y * Y + z * Z;
        }

        private float Radius(Vector3 axis) => MathF.Abs(Vector3.Dot(X, axis)) + MathF.Abs(Vector3.Dot(Y, axis)) + MathF.Abs(Vector3.Dot(Z, axis));

        /// <summary>
        /// How far apart two boxes are along the axis that best separates them (their faces' and
        /// edges' crossings): over 0, they don't overlap (and are at least that far apart); under,
        /// they overlap by at least that much along every such axis.
        /// </summary>
        public static float Gap(Block a, Block b)
        {
            Span<Vector3> edges = [a.X, a.Y, a.Z, b.X, b.Y, b.Z];
            var gap = float.MinValue;
            for (var i = 0; i < 6; i++)
                gap = MathF.Max(gap, Along(a, b, edges[i]));
            for (var i = 0; i < 3; i++)
                for (var j = 3; j < 6; j++)
                    gap = MathF.Max(gap, Along(a, b, Vector3.Cross(edges[i], edges[j])));
            return gap;
        }

        private static float Along(Block a, Block b, Vector3 axis)
        {
            if (axis.LengthSquared() < 1e-10f)
                return float.MinValue;
            axis = Vector3.Normalize(axis);
            return MathF.Abs(Vector3.Dot(b.Centre - a.Centre, axis)) - a.Radius(axis) - b.Radius(axis);
        }
    }

    /// <summary>Panel <paramref name="panel"/> of door <paramref name="door"/>'s ramp, <paramref name="open"/> of the way open to <paramref name="stop"/> (or the full swing), as a box in the lander's space.</summary>
    private static Block Panel(int door, int panel, float open, float? stop = null)
    {
        var (h, t) = (Scene.LanderDoorHeight, Scene.LanderDoorThickness);
        return Block.Of(new Vector3(-t / 2, h / 2, 0), new Vector3(t / 2, h / 2, RocketGeometry.PanelHalfWidth),
            RocketGeometry.PanelTransform(door, panel, open, stop ?? RocketGeometry.DoorOpenRadians));
    }

    /// <summary>The lander's legs as drawn (each its leg and its strut: <see cref="RocketMesh.LegBoxes"/>), stowed or out at full stroke, as boxes in its space.</summary>
    private static IEnumerable<Block> Legs(bool deployed)
    {
        var shape = LegGeometry.Lander;
        var (leg, strut) = RocketMesh.LegBoxes(shape);
        for (var i = 0; i < RocketGeometry.LegCount; i++)
        {
            var into = RocketGeometry.LegTransform(shape, i, deployed ? 1f : 0f, deployed ? 1f : 0f);
            yield return Block.Of(leg.Centre, leg.Half, into);
            yield return Block.Of(strut.Centre, strut.Half, into);
        }
    }

    /// <summary>The level ground under the lander standing on it with its legs out at full stroke, as a height in its space.</summary>
    private static float LevelGround => RocketGeometry.Foot(LegGeometry.Lander, 0).Y - LegGeometry.Lander.Stroke;

    [Fact]
    public void Fully_open_on_level_ground_each_door_is_one_straight_ramp_down_to_it()
    {
        var below = float.RadiansToDegrees(RocketGeometry.DoorOpenRadians) - 90f;
        var floor = Scene.LanderBayFloor - LevelGround;
        for (var door = 0; door < RocketGeometry.DoorCount; door++)
        {
            var tip = RocketGeometry.RampTip(door);
            var clearance = tip.Y - LevelGround;
            var slope = float.RadiansToDegrees(MathF.Asin((Scene.LanderBayFloor - tip.Y) / RocketGeometry.RampLength));
            output.WriteLine($"door {door}: {RocketGeometry.RampLength:0.0} m long, the floor {floor:0.000} m over the ground, its tip {clearance:0.000} m over it, " +
                $"{slope:0.00}° below level ({below:0.00}° by its swing, {float.RadiansToDegrees(RocketGeometry.DoorOpenRadians):0.0}° from shut)");
            // A check that the tip, the ramp's length and the swing agree: DoorOpenRadians is solved
            // for this very ground. The ramps' tips against a lander truly landed are the landing
            // spot's test (Landed_on_level_ground_the_open_doors_reach_down_to_it_as_ramps).
            Assert.Equal(Scene.LanderRampClearance, clearance, 0.001f);
            Assert.Equal(below, slope, 0.01f);
            Assert.InRange(slope, 12f, 16f);
            // Each panel where the straight ramp's next stretch is: the door's own transform, a panel's length further along it.
            var ramp = RocketGeometry.DoorAt(door, RocketGeometry.DoorOpenRadians);
            for (var panel = 0; panel < RocketGeometry.PanelCount; panel++)
            {
                var wanted = Matrix4x4.CreateTranslation(0f, panel * Scene.LanderDoorHeight, 0f) * ramp;
                var got = RocketGeometry.PanelTransform(door, panel, 1f, RocketGeometry.DoorOpenRadians);
                foreach (var corner in (ReadOnlySpan<Vector3>)[new(0, 0, 1), new(-Scene.LanderDoorThickness, Scene.LanderDoorHeight, -1)])
                    Assert.True(Vector3.Distance(Vector3.Transform(corner, wanted), Vector3.Transform(corner, got)) < 1e-4f, $"door {door}'s panel {panel} is off the straight ramp");
            }
        }
    }

    [Fact]
    public void Shut_the_doors_panels_lie_inside_the_fairing_and_clear_of_the_stowed_legs()
    {
        // The fairing is drawn in flat slices: the nearest its shell comes to the axis is the middle of one.
        var inner = FairingGeometry.Lander.Radius * MathF.Cos(MathF.PI / RocketMesh.Slices);
        var stowed = Legs(deployed: false).ToArray();
        for (var panel = 0; panel < RocketGeometry.PanelCount; panel++)
        {
            var (widest, nearest) = (0f, float.MaxValue);
            for (var door = 0; door < RocketGeometry.DoorCount; door++)
            {
                var block = Panel(door, panel, 0f);
                foreach (var corner in block.Corners())
                {
                    widest = MathF.Max(widest, new Vector2(corner.X, corner.Z).Length());
                    Assert.InRange(corner.Y, 0f, Scene.LanderBodyHeight);
                }
                nearest = MathF.Min(nearest, stowed.Min(leg => Block.Gap(block, leg)));
            }
            output.WriteLine($"panel {panel}: out to {widest:0.000} m from the axis (the fairing's shell {inner:0.000}); {nearest * 100:0.0} cm from the stowed legs");
            Assert.True(widest < inner - 0.1f, $"panel {panel} reaches {widest:0.000} m out, within 0.1 m of the fairing's {inner:0.000}");
            Assert.True(nearest > 0.02f, $"panel {panel} is {nearest * 100:0.0} cm from the stowed legs");
        }
    }

    [Fact]
    public void On_level_ground_the_doors_unfold_clear_of_the_ground_the_body_and_the_legs()
    {
        var body = Block.Of(new Vector3(0, Scene.LanderBodyHeight / 2, 0), new Vector3(Scene.LanderHalfWidth, Scene.LanderBodyHeight / 2, Scene.LanderHalfWidth), Matrix4x4.Identity);
        var legs = Legs(deployed: true).ToArray();
        const int samples = 400;
        var lowest = new float[RocketGeometry.PanelCount];
        var fromBody = new float[RocketGeometry.PanelCount];
        var fromLegs = new float[RocketGeometry.PanelCount];
        Array.Fill(lowest, float.MaxValue);
        Array.Fill(fromBody, float.MaxValue);
        Array.Fill(fromLegs, float.MaxValue);
        var fromEachOther = float.MaxValue;
        for (var i = 0; i <= samples; i++)
        {
            var open = (float)i / samples;
            for (var door = 0; door < RocketGeometry.DoorCount; door++)
                for (var panel = 0; panel < RocketGeometry.PanelCount; panel++)
                {
                    var block = Panel(door, panel, open);
                    lowest[panel] = MathF.Min(lowest[panel], block.Corners().Min(c => c.Y) - LevelGround);
                    if (panel > 0) // the door is hinged on the body's floor
                        fromBody[panel] = MathF.Min(fromBody[panel], Block.Gap(block, body));
                    fromLegs[panel] = MathF.Min(fromLegs[panel], legs.Min(leg => Block.Gap(block, leg)));
                    for (var other = panel + 2; other < RocketGeometry.PanelCount; other++) // those not hinged to it
                        fromEachOther = MathF.Min(fromEachOther, Block.Gap(block, Panel(door, other, open)));
                }
        }
        for (var panel = 0; panel < RocketGeometry.PanelCount; panel++)
            output.WriteLine($"panel {panel}: at its lowest {lowest[panel]:0.000} m over the ground; nearest the body {(panel > 0 ? $"{fromBody[panel] * 100:0.0} cm" : "(hinged on it)")}, the legs {fromLegs[panel] * 100:0.0} cm");
        output.WriteLine($"panels not hinged together at least {fromEachOther * 100:0.0} cm apart");
        for (var panel = 0; panel < RocketGeometry.PanelCount; panel++)
        {
            Assert.True(lowest[panel] > 0f, $"panel {panel} comes {lowest[panel]:0.000} m into the ground");
            // Shut, the panel folded onto the door lies flush on it, the body's face.
            Assert.True(panel == 0 || fromBody[panel] > -0.001f, $"panel {panel} comes {-fromBody[panel] * 100:0.0} cm into the body");
            Assert.True(fromLegs[panel] > 0.02f, $"panel {panel} comes within {fromLegs[panel] * 100:0.0} cm of a leg");
        }
        Assert.True(fromEachOther > 0f);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(10f)]
    [InlineData(15f)]
    public void Stopped_short_on_a_slope_a_door_unfolds_clear_of_it(float degrees)
    {
        // The lander upright (as the formula sets one down) over ground rising at degrees across its
        // +X door's side, level ground's height under its axis: that door stops where its straight
        // ramp's tip meets the slope, short of the full swing.
        var (sin, cos) = MathF.SinCos(float.DegreesToRadians(degrees));
        var up = new Vector3(-sin, cos, 0f); // the slope's own up
        float Over(Vector3 p) => Vector3.Dot(p - new Vector3(0f, LevelGround, 0f), up);
        var (clear, touching) = (0f, RocketGeometry.DoorOpenRadians);
        Assert.True(Over(RocketGeometry.RampTipAt(0, touching)) < 0f, "the full swing is clear of the slope: this shows nothing");
        for (var i = 0; i < 30; i++)
        {
            var middle = (clear + touching) / 2f;
            if (Over(RocketGeometry.RampTipAt(0, middle)) < 0f)
                touching = middle;
            else
                clear = middle;
        }
        var stop = clear;
        const int samples = 400;
        var lowest = new float[RocketGeometry.PanelCount];
        Array.Fill(lowest, float.MaxValue);
        for (var i = 0; i <= samples; i++)
            for (var panel = 0; panel < RocketGeometry.PanelCount; panel++)
                lowest[panel] = MathF.Min(lowest[panel], Panel(0, panel, (float)i / samples, stop).Corners().Min(Over));
        output.WriteLine($"a {degrees}° slope: stopped at {float.RadiansToDegrees(stop):0.0}° (the full swing {float.RadiansToDegrees(RocketGeometry.DoorOpenRadians):0.0}°); " +
            $"each panel at its lowest {string.Join(", ", lowest.Select(l => $"{l:0.000}"))} m over the slope");
        // The last panel's far edge comes down onto the slope, as the stop is found; nothing goes into it on the way.
        for (var panel = 0; panel < RocketGeometry.PanelCount; panel++)
            Assert.True(lowest[panel] > -0.001f, $"panel {panel} comes {-lowest[panel]:0.000} m into the slope");
    }
}
