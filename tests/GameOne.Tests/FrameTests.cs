using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The frames of reference (<see cref="Frames"/>): Auto's rule and its margin, the velocity in each
/// frame, and the velocity relative to a target in a frame; the frame in use (the player's choice,
/// through <see cref="FlightInput.ChooseFrame"/>), and the holds and travel markers by it.
/// </summary>
public sealed class FrameTests(ITestOutputHelper output)
{
    [Fact]
    public void Auto_switches_to_orbit_past_the_margin_and_back_in_the_air()
    {
        var frame = Frame.Surface;
        frame = Frames.Auto(GroundBody.Planet, frame, 3050f, false);
        Assert.Equal(Frame.Surface, frame);
        frame = Frames.Auto(GroundBody.Planet, frame, 3101f, false);
        Assert.Equal(Frame.Orbital, frame);
        frame = Frames.Auto(GroundBody.Planet, frame, 3050f, false);
        Assert.Equal(Frame.Orbital, frame);
        frame = Frames.Auto(GroundBody.Planet, frame, 2999f, false);
        Assert.Equal(Frame.Surface, frame);
    }

    [Fact]
    public void Auto_switches_exactly_at_the_air_s_top_and_the_margin_over_it()
    {
        // From Orbital, down to the air's top itself is still clear of it; anything lower drops.
        Assert.Equal(Frame.Orbital, Frames.Auto(GroundBody.Planet, Frame.Orbital, Scene.AtmosphereHeight, false));
        Assert.Equal(Frame.Surface, Frames.Auto(GroundBody.Planet, Frame.Orbital, Scene.AtmosphereHeight - 0.1f, false));
        // From Surface, the margin itself isn't past it; anything higher is.
        Assert.Equal(Frame.Surface, Frames.Auto(GroundBody.Planet, Frame.Surface, Scene.AtmosphereHeight + Scene.FrameOrbitMargin, false));
        Assert.Equal(Frame.Orbital, Frames.Auto(GroundBody.Planet, Frame.Surface, Scene.AtmosphereHeight + Scene.FrameOrbitMargin + 0.1f, false));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_periapsis_that_is_not_a_number_is_surface(float periapsis)
    {
        // Not knowing where the orbit's lowest point is, it can't be said to clear the air.
        Assert.Equal(Frame.Surface, Frames.Auto(GroundBody.Planet, Frame.Orbital, periapsis, false));
        Assert.Equal(Frame.Surface, Frames.Auto(GroundBody.Planet, Frame.Surface, periapsis, false));
    }

    [Fact]
    public void An_orbit_grazing_the_top_switches_once_each_way()
    {
        var frame = Frame.Surface;
        var seen = new List<Frame>();
        foreach (var periapsis in new[] { 2990f, 3020f, 3110f, 3040f, 3105f, 2998f, 3060f })
            seen.Add(frame = Frames.Auto(GroundBody.Planet, frame, periapsis, false));
        Assert.Equal(
            [Frame.Surface, Frame.Surface, Frame.Orbital, Frame.Orbital, Frame.Orbital, Frame.Surface, Frame.Surface],
            seen);
    }

    [Fact]
    public void Landed_or_afloat_is_always_surface()
    {
        // Whatever it had, and whatever its orbit would be: the rule by state, which Vessel also
        // keeps (its AutoFrame and the restoring constructor). No flight test: a landed vessel's
        // own periapsis never clears the air, being no higher than where it stands, and one
        // restored landed is frozen, its craft reading no velocity, so its orbit there falls too.
        Assert.Equal(Frame.Surface, Frames.Auto(GroundBody.Planet, Frame.Orbital, 5000f, onTheGround: true));
        Assert.Equal(Frame.Surface, Frames.Auto(GroundBody.Planet, Frame.Surface, 5000f, onTheGround: true));
    }

    [Fact]
    public void An_escaping_path_clear_of_the_air_is_orbital()
    {
        // Only the periapsis counts: an escaping path (its apoapsis infinite) is judged by it too.
        Assert.Equal(Frame.Orbital, Frames.Auto(GroundBody.Planet, Frame.Surface, 4000f, false));
    }

    [Fact]
    public void On_the_pad_the_surface_speed_is_nought_and_the_orbital_about_57_m_per_s_east()
    {
        using var sim = new Simulation();
        var v = sim.Flight.Controlled;
        var surface = Frames.VelocityIn(GroundBody.Planet, Frame.Surface, v.CentreOfMass, v.Velocity);
        var orbital = Frames.VelocityIn(GroundBody.Planet, Frame.Orbital, v.CentreOfMass, v.Velocity);
        Assert.True(surface.Length() < 0.01f);
        Assert.InRange(orbital.Length(), 56f, 57f);
        Assert.True(Vector3.Dot(Vector3.Normalize(orbital), Planet.East(v.Position)) > 0.999f);
    }

    [Fact]
    public void Relative_velocity_in_the_orbital_frame_ignores_the_spin()
    {
        var a = new Vector3(0f, 0f, 0f);
        var b = new Vector3(100f, 0f, 0f);
        // The same velocity among the stars at both, so over the ground each has the spin taken off it.
        var stars = new Vector3(0f, 10f, 0f);
        var (ga, gb) = (Planet.OverGround(a, stars), Planet.OverGround(b, stars));
        Assert.True(Frames.Relative(GroundBody.Planet, Frame.Orbital, a, ga, b, gb).Length() < 1e-3f);

        // Both landed: nothing moves over the ground, but the ground carries them past each other among the stars.
        Assert.True(Frames.Relative(GroundBody.Planet, Frame.Surface, a, Vector3.Zero, b, Vector3.Zero).Length() < 1e-3f);
        Assert.InRange(Frames.Relative(GroundBody.Planet, Frame.Orbital, a, Vector3.Zero, b, Vector3.Zero).Length(), 0.55f, 0.59f);
    }

    [Fact]
    public void A_satellite_starts_in_the_orbital_frame_and_a_booster_on_the_pad_in_the_surface()
    {
        using var sim = new Simulation(Mission.LaunchAndLand, world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var satellite = flight.WorldVessels.First(v => v.Flight == 0);
        Assert.Equal(Frame.Orbital, satellite.AutoFrame);
        Assert.Equal(Frame.Surface, flight.Controlled.AutoFrame);
    }

    [Fact]
    public void A_hand_picked_frame_stays_through_an_automatic_switch_point()
    {
        // A satellite (orbital by Auto), flown with Surface picked: its own pilot burns against its
        // orbit until the periapsis is in the air (Auto's switch back), then along it until it
        // clears the margin again (Auto's switch out). The frame in use stays Surface throughout.
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var satellite = flight.WorldVessels.First(v => v.Flight == 0);
        sim.Step(new FlightInput(SwitchTo: satellite, ChooseFrame: FrameChoice.Surface));
        Assert.Equal(Frame.Orbital, satellite.AutoFrame);
        Assert.Equal(FrameChoice.Surface, flight.FrameChoice);
        Assert.Equal(Frame.Surface, flight.Frame);

        var seen = new HashSet<Frame>();
        var lowered = false;
        FlightInput Burn(Flight f, bool along)
        {
            var stars = Planet.AmongStars(f.CentreOfMass, f.Velocity);
            var way = Vector3.Normalize(along ? stars : -stars);
            var lined = Vector3.Dot(f.Controlled.Up, way) > 0.999f;
            return new FlightInput(Hold: way, Throttle: lined ? 1f : -1f);
        }
        Assert.True(sim.Run((f, _) =>
        {
            seen.Add(satellite.AutoFrame);
            Assert.Equal(Frame.Surface, f.Frame);
            lowered |= satellite.Periapsis < Scene.AtmosphereHeight - 100f;
            return Burn(f, along: lowered);
        }, _ => lowered && satellite.AutoFrame == Frame.Orbital, maxSeconds: 200), "the burns never took it through both switches");
        output.WriteLine($"Auto went through {string.Join(", ", seen)}; periapsis now {satellite.Periapsis:0} m");
        Assert.Contains(Frame.Surface, seen);
        Assert.Equal(Frame.Surface, flight.Frame);

        sim.Step(new FlightInput(CutThrottle: true, ChooseFrame: FrameChoice.Auto));
        Assert.Equal(Frame.Orbital, flight.Frame);
    }

    [Fact]
    public void Target_without_a_target_is_refused_and_losing_it_falls_back_to_auto()
    {
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var satellite = flight.WorldVessels.First(v => v.Flight == 0);
        Assert.Equal(FrameChoice.Auto, flight.FrameChoice); // a new flight starts on Auto
        sim.Step(new FlightInput(ChooseFrame: FrameChoice.Orbit));
        sim.Step(new FlightInput(ChooseFrame: FrameChoice.Target));
        Assert.Equal(FrameChoice.Orbit, flight.FrameChoice); // no target: refused
        Assert.Null(flight.RelativeSpeed);

        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(satellite), ChooseFrame: FrameChoice.Target));
        Assert.Equal(FrameChoice.Target, flight.FrameChoice);
        Assert.Equal(Frame.Target, flight.Frame);
        Assert.NotNull(flight.RelativeSpeed);

        sim.Step(new FlightInput(ClearTarget: true));
        Assert.Equal(FrameChoice.Auto, flight.FrameChoice);
        Assert.Equal(flight.Controlled.AutoFrame, flight.Frame);
        Assert.Null(flight.RelativeSpeed);
        _ = flight.FrameVelocity; // nothing throws
    }

    [Fact]
    public void Switching_vessels_keeps_the_choice_and_target_needs_the_new_ones_own()
    {
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var booster = flight.Controlled;
        var (first, second) = (flight.WorldVessels[0], flight.WorldVessels[1]);
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(first), ChooseFrame: FrameChoice.Target));
        Assert.Equal(FrameChoice.Target, flight.FrameChoice);

        // Its own target: still Target, by it.
        sim.Step(new FlightInput(SwitchTo: second));
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(first), ChooseFrame: FrameChoice.Target));
        sim.Step(new FlightInput(SwitchTo: booster));
        Assert.Equal(FrameChoice.Target, flight.FrameChoice);
        sim.Step(new FlightInput(SwitchTo: second));
        Assert.Equal(FrameChoice.Target, flight.FrameChoice);

        // None of its own: Auto. A frame picked by hand goes with the player to the next.
        sim.Step(new FlightInput(SwitchTo: first));
        Assert.Equal(FrameChoice.Auto, flight.FrameChoice);
        sim.Step(new FlightInput(ChooseFrame: FrameChoice.Surface));
        sim.Step(new FlightInput(SwitchTo: booster));
        Assert.Equal(FrameChoice.Surface, flight.FrameChoice);

        // A new flight starts on Auto.
        sim.Step(new FlightInput(StartAgain: true));
        Assert.Equal(FrameChoice.Auto, flight.FrameChoice);
    }

    [Fact]
    public void The_holds_follow_the_frame_in_use_switch_and_all()
    {
        // The 60° satellite: its velocity over the ground is off its orbit's (about 6° there), as
        // the ground turns across its path.
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var satellite = flight.WorldVessels.First(v => v.Id == 2);
        sim.Step(new FlightInput(SwitchTo: satellite, ChooseHold: HoldMode.Prograde));
        Assert.Equal(Frame.Orbital, flight.Frame);
        sim.Run((_, _) => default, _ => false, maxSeconds: 30);
        var toStars = Degrees(satellite.Up, Planet.AmongStars(satellite.CentreOfMass, satellite.Velocity));
        output.WriteLine($"settled in Orbit: the nose {toStars:0.00}° off the orbit's way");
        Assert.InRange(toStars, 0f, 1f);

        // Surface picked: the nose turns to the way it goes over the ground, once, by the angle between.
        var apart = Degrees(satellite.Velocity, Planet.AmongStars(satellite.CentreOfMass, satellite.Velocity));
        var start = flight.WorldTime;
        sim.Step(new FlightInput(ChooseFrame: FrameChoice.Surface));
        Assert.Equal(Frame.Surface, flight.Frame);
        // The turn, measured among the stars (the aim is kept there above the air, the ground turning under it).
        var (swept, before, backwards) = (0f, Planet.TurnedDirection(satellite.Aim, flight.WorldTime - start), 0f);
        var (last, settled) = (float.MaxValue, false);
        Assert.True(sim.Run((f, _) =>
        {
            var aim = Planet.TurnedDirection(satellite.Aim, f.WorldTime - start);
            swept += Degrees(aim, before);
            before = aim;
            var off = Degrees(satellite.Aim, satellite.Velocity);
            if (off > last + 1e-3f)
                backwards = MathF.Max(backwards, off - last);
            last = off;
            settled = off < 0.2f;
            return default;
        }, _ => settled, maxSeconds: 20), "the nose never came round to the ground's way");
        output.WriteLine($"{apart:0.00}° between the two; the aim swept {swept:0.00}° in {flight.WorldTime - start:0.0} s, " +
            $"backing off by at most {backwards:0.000}°; the nose {Degrees(satellite.Up, satellite.Velocity):0.00}° off");
        Assert.True(apart > 3f, $"the two ways should be apart ({apart:0.0}°)");
        Assert.InRange(swept, apart - 1f, apart + 1f);
        Assert.InRange(backwards, 0f, 0.05f);
        sim.Run((_, _) => default, _ => false, maxSeconds: 5);
        Assert.InRange(Degrees(satellite.Up, satellite.Velocity), 0f, 1f);
    }

    [Fact]
    public void A_vessel_nobody_flies_holds_by_its_own_auto()
    {
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var booster = flight.Controlled;
        // The satellite whose way over the ground is furthest off its orbit's, so the two holds differ most.
        var satellite = flight.WorldVessels.Where(v => v.Flight == 0)
            .MaxBy(v => Degrees(v.Velocity, Planet.AmongStars(v.CentreOfMass, v.Velocity)))!;
        // In a small view (watched, but not flown), so it is the physics that turns its nose to
        // its hold (nobody watching it, it would leave the physics: see Unwatched).
        var watched = new[] { satellite };
        sim.Step(new FlightInput(SwitchTo: satellite, ChooseHold: HoldMode.Retrograde));
        sim.Step(new FlightInput(SwitchTo: booster, ChooseFrame: FrameChoice.Surface, Watched: watched));
        Assert.Equal(Frame.Surface, flight.Frame);
        bool InThePhysics(Flight _)
        {
            Assert.False(satellite.OnFormula);
            return false;
        }
        sim.Run((_, _) => new FlightInput(Watched: watched), InThePhysics, maxSeconds: 30);
        var worst = 0f;
        sim.Run((_, _) =>
        {
            worst = MathF.Max(worst, Degrees(satellite.Up, -Planet.AmongStars(satellite.CentreOfMass, satellite.Velocity)));
            return new FlightInput(Watched: watched);
        }, InThePhysics, maxSeconds: 10);
        var fromGround = Degrees(satellite.Up, -satellite.Velocity);
        output.WriteLine($"the satellite's nose within {worst:0.00}° of its orbit's retrograde, {fromGround:0.0}° off the ground's");
        Assert.InRange(worst, 0f, 1f);
        Assert.True(fromGround > 3f);
    }

    [Fact]
    public void Matched_in_orbit_the_target_frame_reads_near_nought_and_retrograde_with_a_burn_kills_it()
    {
        // Two satellites in the same orbit, 60 m apart, the one ahead 3 m/s faster among the stars.
        var a = WorldPresets.Satellites().Vessels[0];
        var stars = InOrbit.Stars(a);
        var along = Vector3.Normalize(stars);
        var b = InOrbit.At(a, a.CentreOfMass.ToSingle() + along * 60f, stars + along * 3f, a.Rotation) with { Id = 1 };
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [a, b], NextVesselId = 2 });
        var flight = sim.Flight;
        var (mine, theirs) = (flight.WorldVessels[0], flight.WorldVessels[1]);
        sim.Step(new FlightInput(SwitchTo: mine));
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(theirs), ChooseFrame: FrameChoice.Target));
        Assert.Equal(Frame.Target, flight.Frame);
        output.WriteLine($"relative speed {flight.RelativeSpeed:0.000} m/s");
        Assert.InRange(flight.RelativeSpeed!.Value, 2.9f, 3.1f);

        // Retrograde hold turns the nose round by itself (the target frame's floor is well under
        // 3 m/s), from upright, square to the way it moves relative to the other.
        var start = flight.WorldTime;
        var from = Degrees(mine.Up, -flight.FrameVelocity);
        sim.Step(new FlightInput(ChooseHold: HoldMode.Retrograde));
        Assert.True(sim.Run((_, _) => default, f => Degrees(f.Controlled.Up, -f.FrameVelocity) < 1f, maxSeconds: 30),
            $"the hold never brought the nose round (still {Degrees(mine.Up, -flight.FrameVelocity):0.0}° off)");
        output.WriteLine($"the hold turned the nose from {from:0.0}° off retrograde to under 1° in {flight.WorldTime - start:0.0} s");
        Assert.True(from > 45f);

        // Then the throttle, in proportion to the relative speed, does the rest, the hold keeping the nose on it.
        var worst = 0f;
        Assert.True(sim.Run((f, dt) =>
        {
            if (f.RelativeSpeed > 1f)
                worst = MathF.Max(worst, Degrees(f.Controlled.Up, -f.FrameVelocity));
            var wanted = Math.Clamp(f.RelativeSpeed!.Value / (2f * f.MaxAcceleration), 0f, 1f);
            return new FlightInput(Throttle: Math.Clamp((wanted - f.Throttle) / (Scene.ThrottleRate * dt), -1f, 1f));
        }, f => f.RelativeSpeed < 0.5f, maxSeconds: 60 - (flight.WorldTime - start)), $"still {flight.RelativeSpeed:0.00} m/s apart");
        output.WriteLine($"matched to {flight.RelativeSpeed:0.000} m/s in {flight.WorldTime - start:0.0} s; the nose within {worst:0.00}° of retrograde over 1 m/s");
        Assert.True(mine.Hold == HoldMode.Retrograde);
        Assert.InRange(worst, 0f, 3f);
        Assert.Equal(FrameChoice.Target, flight.FrameChoice);
    }

    [Fact]
    public void The_travel_markers_show_the_frame_velocity()
    {
        // The markers draw Flight.FrameVelocity; the holds steer by the same, so a held nose settles
        // on it, in each frame.
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var satellite = flight.WorldVessels.First(v => v.Id == 3);
        sim.Step(new FlightInput(SwitchTo: satellite, SetTarget: NavTarget.OfVessel(flight.WorldVessels[1]), ChooseHold: HoldMode.Prograde));
        var ways = new List<Vector3>();
        foreach (var choice in new[] { FrameChoice.Surface, FrameChoice.Orbit, FrameChoice.Target })
        {
            sim.Step(new FlightInput(ChooseFrame: choice));
            sim.Run((_, _) => default, _ => false, maxSeconds: 20);
            var off = Degrees(satellite.Up, flight.FrameVelocity);
            output.WriteLine($"{flight.Frame}: the held nose {off:0.00}° off the markers' {flight.FrameVelocity}");
            Assert.InRange(off, 0f, 1f);
            ways.Add(flight.FrameVelocity);
        }
        // The three are different ways, so each frame's hold went its own.
        Assert.True(Degrees(ways[0], ways[1]) > 3f && Degrees(ways[1], ways[2]) > 3f && Degrees(ways[0], ways[2]) > 3f);
        var target = flight.Locate(flight.Target!.Value)!.Value;
        var relative = Frames.Relative(GroundBody.Planet, Frame.Orbital, satellite.CentreOfMass, satellite.Velocity, target.Position, target.Velocity);
        Assert.True(Vector3.Distance(relative, flight.FrameVelocity) < 1e-3f);
        Assert.True(MathF.Abs(relative.Length() - flight.RelativeSpeed!.Value) < 1e-3f);
    }

    [Fact]
    public void A_target_that_crashes_or_is_removed_falls_back_to_auto()
    {
        // A booster falling hard onto a test pad from 30 m, targeted from the rocket on the launch pad.
        var pad = Scene.TestPads[0].Centre;
        var falling = Drops.Placed(pad, Planet.East(pad), 30f, clearance: 30f);
        using (var sim = new Simulation(world: new WorldState { Vessels = [falling], NextVesselId = 1 }))
        {
            var flight = sim.Flight;
            var booster = flight.WorldVessels.First(v => v.Id == 0);
            sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(booster), ChooseFrame: FrameChoice.Target));
            Assert.Equal(Frame.Target, flight.Frame);
            Assert.True(sim.Run((_, _) => default, _ => booster.State == FlightState.Crashed, maxSeconds: 10), "it never crashed");
            // On the very step it is destroyed.
            Assert.Null(flight.Target);
            Assert.Equal(FrameChoice.Auto, flight.FrameChoice);
            Assert.Equal(flight.Controlled.AutoFrame, flight.Frame);
            Assert.Null(flight.RelativeSpeed);
        }

        // A world full to its capacity: a stage dropped just after launch makes room by removing the
        // oldest, the target.
        var landed = Drops.Placed(pad, Planet.East(pad), 0f) with { State = FlightState.Landed };
        var full = Enumerable.Range(0, Flight.Capacity - 1).Select(i => landed with { Id = i }).ToList();
        using (var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = full, NextVesselId = full.Count }))
        {
            var flight = sim.Flight;
            var oldest = flight.WorldVessels[0];
            sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(oldest), ChooseFrame: FrameChoice.Target));
            Assert.Equal(FrameChoice.Target, flight.FrameChoice);
            sim.Step(new FlightInput(Launch: true));
            sim.Step(new FlightInput(Stage: true));
            Assert.DoesNotContain(oldest, flight.WorldVessels);
            Assert.Null(flight.Target);
            Assert.Equal(FrameChoice.Auto, flight.FrameChoice);
        }
    }

    [Fact]
    public void A_drone_ship_target_moves_with_the_waves()
    {
        using var sim = new Simulation();
        var flight = sim.Flight;
        var ship = new NavTarget(TargetKind.Site, 2);
        sim.Step(new FlightInput(SetTarget: ship));
        var raft = Rafts.Now(Rafts.Ship);
        var (position, velocity, _, _, _, _) = flight.Locate(ship)!.Value;
        var deck = Vector3.Transform(new Vector3(0f, raft.Freeboard, 0f), raft.Current.Model);
        output.WriteLine($"the deck {Vector3.Distance(position, Sites.Top(2)):0.00} m from its calm-sea top, moving at {velocity.Length():0.000} m/s");
        Assert.Equal(deck, position);
        Assert.Equal(raft.VelocityAt(deck), velocity);
        Assert.True(Vector3.Distance(position, Sites.Top(2)) < 3f);

        // Over a while, as the frame reads it each step: the velocity is how the deck's middle
        // really moves (each second, where it got to against where its velocities, step by step,
        // would have taken it: over a single step the rounding of a float position kilometres out
        // is a sizeable share of the movement), and the waves move it by more than any rounding could.
        var (from, carried, lastVelocity, lastTime, since) = (position, Vector3.Zero, velocity, flight.WorldTime, 0.0);
        var (fastest, worst) = (0f, 0f);
        sim.Run((f, _) =>
        {
            var (at, moving, _, _, _, _) = f.TargetAt!.Value;
            var seconds = (float)(f.WorldTime - lastTime);
            if (seconds <= 0f)
                return default; // asked before the first step
            (carried, since) = (carried + (moving + lastVelocity) / 2 * seconds, since + seconds);
            if (since >= 1.0)
            {
                worst = MathF.Max(worst, Vector3.Distance((at - from) / (float)since, carried / (float)since));
                (from, carried, since) = (at, Vector3.Zero, 0.0);
            }
            fastest = MathF.Max(fastest, moving.Length());
            (lastVelocity, lastTime) = (moving, f.WorldTime);
            return default;
        }, _ => false, maxSeconds: 20);
        output.WriteLine($"over 20 s: the deck at up to {fastest:0.000} m/s; its velocity at most {worst:0.0000} m/s off how it moved, a second at a time");
        // Measured: up to 0.078 m/s, and off by 0.0003 m/s at most (the rounding that's left).
        Assert.InRange(fastest, 0.04f, 1f);
        Assert.InRange(worst, 0f, fastest * 0.05f);
    }

    [Fact]
    public void Prograde_hold_in_the_surface_frame_keeps_the_nose_along_the_ground_track()
    {
        // A booster above the air going north a little faster than a circle there among the stars,
        // its nose that way: Auto would have it orbital (its periapsis, where it is, clears the
        // margin), and over the ground it goes west of north too (the ground turns east under it),
        // so the two frames' progrades are well apart. Surface picked, the hold turns the nose to
        // the way over the ground, and keeps it there.
        var start = Drops.Placed(Vector3.Zero, Planet.North(Vector3.Zero), 0f, leanDegrees: 90f, clearance: 3500f, legsOut: false);
        var centre = start.CentreOfMass.ToSingle();
        var placed = InOrbit.At(start, centre, Planet.North(centre) * Planet.CircularSpeed(Planet.Altitude(centre)) * 1.02f, start.Rotation);
        using var sim = new Simulation(world: new WorldState { Vessels = [placed], NextVesselId = 1 });
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.Id == 0)));
        Assert.Equal(Frame.Orbital, flight.Frame);
        sim.Step(new FlightInput(ChooseHold: HoldMode.Prograde, ChooseFrame: FrameChoice.Surface));
        Assert.Equal(Frame.Surface, flight.Frame);
        var apart = Degrees(flight.Velocity, Planet.AmongStars(flight.CentreOfMass, flight.Velocity));
        sim.Run((_, _) => default, _ => false, maxSeconds: 10); // the nose comes round
        var (worst, fromStars) = (0f, float.MaxValue);
        sim.Run((f, _) =>
        {
            Assert.Equal(Frame.Orbital, f.Controlled.AutoFrame);
            worst = MathF.Max(worst, Degrees(f.Controlled.Up, f.Velocity));
            fromStars = MathF.Min(fromStars, Degrees(f.Controlled.Up, Planet.AmongStars(f.CentreOfMass, f.Velocity)));
            return default;
        }, _ => false, maxSeconds: 10);
        output.WriteLine($"the two progrades {apart:0.0}° apart; the nose within {worst:0.00}° of the way over the ground, " +
            $"at least {fromStars:0.0}° off the way among the stars; {flight.Altitude:0} m up");
        Assert.Equal(FlightState.Flying, flight.State);
        Assert.True(apart > 10f, $"the two progrades should be well apart ({apart:0.0}°)");
        Assert.InRange(worst, 0f, 2f);
        Assert.True(fromStars > apart - 2f);
    }

    [Fact]
    public void A_sub_orbital_path_in_the_surface_frame_is_drawn_as_its_own_ellipse_among_the_stars()
    {
        // A booster above the air, going east at 150 m/s and climbing at 60 m/s: its periapsis is
        // under the ground, so Auto has it in the surface frame. Its line is still the orbit's own
        // conic among the stars, every point on it (as the orbit has it in the frame that matches
        // the ground now), not the track over the turning ground, which by its end (some minutes
        // on, the ground turning at about 50 m/s here) is kilometres off it.
        using var sim = Drops.At(Vector3.Zero, Planet.East(Vector3.Zero), -60f, sideways: Planet.East(Vector3.Zero) * 150f, clearance: 3500f);
        var flight = sim.Flight;
        sim.Step(new FlightInput(ChooseFrame: FrameChoice.Auto));
        Assert.Equal(Frame.Surface, flight.Frame);
        var orbit = Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity);
        Assert.True(orbit.EndsOnGround, "it should come down");
        var line = Built(flight).Line;
        var worst = line.Max(p => Vector3.Distance(p, orbit.At(orbit.AnomalyToward(p - Planet.Centre))));
        var start = Vector3.Distance(line[0], flight.CentreOfMass);
        var end = Vector3.Distance(line[^1], Planet.Turned(orbit.Landing, orbit.SecondsToEnd));
        output.WriteLine($"{line.Count} points, at most {worst:0.000} m off the conic; it starts {start:0.00} m from the vessel and ends {end:0.00} m from where the landing's ground will be by then");
        Assert.InRange(worst, 0f, 0.5f);
        Assert.InRange(start, 0f, 0.5f);
        Assert.InRange(end, 0f, 2f);
        sim.Step(new FlightInput(ChooseFrame: FrameChoice.Orbit));
        var orbital = Built(flight).Line;
        Assert.Equal(line.Count, orbital.Count);
        Assert.InRange(line.Zip(orbital).Max(p => Vector3.Distance(p.First, p.Second)), 0f, 10f); // the same line in every frame, but for the step between
    }

    [Fact]
    public void Round_the_moon_the_path_in_the_surface_frame_closes_on_itself()
    {
        // A stage in a low round orbit of the moon, flown there with Surface picked: its line is one
        // closed lap round the moon among its stars (the moon turns once a lap, so over its ground
        // the lap's end would be a long way round from its start).
        var moon = GroundBody.Moon;
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState
        {
            Time = 3456.7, Vessels = [InOrbit.Round(moon, 0, 400f)], NextVesselId = 1, Flights = 1,
        });
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.Single(v => v.Id == 0), ChooseFrame: FrameChoice.Surface));
        Assert.Same(moon, flight.Body);
        Assert.Equal(Frame.Surface, flight.Frame);
        var orbit = Orbit.OverGround(moon, flight.CentreOfMass, flight.Velocity);
        Assert.True(orbit.Closed, "it should go round");
        var line = Built(flight).Line;
        var worst = line.Max(p => Vector3.Distance(p, orbit.At(orbit.AnomalyToward(p - moon.Centre))));
        var shut = Vector3.Distance(line[0], line[^1]);
        var overGround = Vector3.Distance(line[0], moon.Turned(line[^1], -orbit.Period)); // where a track over the ground would end
        output.WriteLine($"{line.Count} points, at most {worst:0.000} m off the conic; its ends {shut:0.00} m apart (over the ground they would be {overGround:0} m apart)");
        Assert.InRange(worst, 0f, 0.5f);
        Assert.InRange(shut, 0f, 2f);
        Assert.True(overGround > 1000f, "the moon should turn well under it in a lap");
    }

    [Fact]
    public void Above_the_air_a_hop_follows_its_line_among_the_stars_and_near_the_ground_its_landing_mark_is_where_it_comes_down()
    {
        // A booster above the air, going east at 150 m/s and climbing at 60 m/s: for the whole of its
        // 80 s or so up there (12 km of track), its centre of mass's real path among the stars is the
        // line built at the start. A few metres' bound: the line is sampled 256 pieces a lap, and
        // the physics steps in floats. (Built from the body's origin with the centre of mass's
        // velocity, the ground's turn would be added for the wrong point, and the path drift off
        // the line by about 0.1 m/s: nearly 9 m by the end.)
        using (var high = Drops.At(Vector3.Zero, Planet.East(Vector3.Zero), -60f, sideways: Planet.East(Vector3.Zero) * 150f, clearance: 3500f))
        {
            high.Step(new FlightInput(ChooseFrame: FrameChoice.Surface));
            var (off, seconds) = TrackOff(high, Built(high.Flight).Line);
            output.WriteLine($"hop: at most {off:0.00} m off the line over {seconds:0.0} s above the air");
            Assert.True(seconds > 70, $"it was above the air for only {seconds:0.0} s");
            Assert.InRange(off, 0f, 3f);
        }

        // Low down, the landing can only be judged near the end: the path ignores the air, whose
        // drag slows a hop through it by tens of metres a second. Built about 2 s before it comes
        // down, its landing mark is within 20 m of there. Not closer: the path follows the
        // booster's centre of mass to the ground, and on a drag-slowed, shallow descent the hull
        // hits about 10 m above that.
        using var sim = Drops.At(Vector3.Zero, Planet.East(Vector3.Zero), 0f, sideways: Planet.East(Vector3.Zero) * 150f, clearance: 400f);
        var flight = sim.Flight;
        sim.Step(new FlightInput(ChooseFrame: FrameChoice.Surface));
        var marks = new List<(double Time, Vector3 Mark)>();
        Assert.True(sim.Run((_, _) => default, f =>
        {
            if (Built(f).LandingMark is { } mark)
                marks.Add((f.WorldTime, mark));
            return f.Controlled.State != FlightState.Flying;
        }, maxSeconds: 120), "it never came down");
        var down = flight.Controlled.Position;
        var landing = marks.MinBy(e => Math.Abs(flight.WorldTime - 2 - e.Time)).Mark;
        output.WriteLine($"hop: the landing mark built 2 s before is {AcrossGround(landing, down):0.0} m from where it comes down");
        Assert.InRange(AcrossGround(landing, down), 0f, 20f);
    }

    [Fact]
    public void Straight_up_and_down_the_line_s_top_is_among_the_stars()
    {
        // A booster above the air going straight up among the stars at 100 m/s: its path is a
        // line, and its top is where the vessel will be among the stars once it has climbed there,
        // ten seconds or so on, the ground having turned some hundreds of metres under it
        // meanwhile (so over the ground, where it is then, it is that far from the line's top).
        var start = Drops.Placed(Vector3.Zero, Planet.East(Vector3.Zero), 0f, clearance: 3500f, legsOut: false);
        var placed = InOrbit.At(start, start.CentreOfMass.ToSingle(), Planet.Up(start.CentreOfMass.ToSingle()) * 100f);
        using var sim = new Simulation(world: new WorldState { Vessels = [placed], NextVesselId = 1 });
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.Id == 0), ChooseFrame: FrameChoice.Surface));
        Assert.True(Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity).Radial, "it should go straight up and down");
        var line = Built(flight).Line;
        Assert.Equal(3, line.Count); // where it is, the top, and where it comes down
        var (top, highest, at, built) = (Vector3.Zero, float.MinValue, 0.0, flight.WorldTime);
        sim.Run((f, _) =>
        {
            if (Planet.Altitude(f.CentreOfMass) > highest)
                (top, highest, at) = (f.CentreOfMass, Planet.Altitude(f.CentreOfMass), f.WorldTime);
            return default;
        }, f => Planet.Altitude(f.CentreOfMass) < highest - 50f, maxSeconds: 60);
        var off = Vector3.Distance(Planet.Turned(top, at - built), line[1]); // among the stars as they were when it was built
        var overGround = Vector3.Distance(top, line[1]);
        output.WriteLine($"the top {highest:0} m up, {at - built:0.0} s on: {off:0.00} m from the line's among the stars ({overGround:0} m over the ground)");
        Assert.True(overGround > 100f, "the ground should have turned well under it meanwhile");
        Assert.InRange(off, 0f, 5f);
    }

    [Fact]
    public void Above_the_air_a_de_orbit_follows_its_line_and_its_landing_mark_is_on_the_ground_it_comes_down_on()
    {
        // A satellite burned against its orbit until its periapsis is 2 km under the ground: then
        // Auto has it in the surface frame, and its line comes down. Above the air its real path
        // among the stars is that line (a few metres' bound: the line is sampled 256 pieces a lap,
        // and the physics steps in floats). Its landing mark is the place on the ground it comes
        // down on, as the ground is now; the line ends where that will have turned to by then.
        // Below, the air (which the line ignores) takes over, and it breaks up in the thick of it:
        // no test of the line there.
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var satellite = flight.WorldVessels.First(v => v.Flight == 0);
        sim.Step(new FlightInput(SwitchTo: satellite));
        Assert.True(sim.Run((f, _) =>
        {
            if (f.Periapsis < -2000f)
                return new FlightInput(CutThrottle: true);
            var way = -Vector3.Normalize(Planet.AmongStars(f.CentreOfMass, f.Velocity));
            return new FlightInput(Hold: way, Throttle: Vector3.Dot(f.Controlled.Up, way) > 0.999f ? 1f : -1f);
        }, f => f.Periapsis < -2000f && f.Throttle == 0f, maxSeconds: 300), "the burn never brought the periapsis down");
        Assert.Equal(Frame.Surface, flight.Frame);

        var paths = Built(flight);
        var orbit = Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity);
        Assert.NotNull(paths.LandingMark);
        var (mark, end) = (Vector3.Distance(orbit.Landing, paths.LandingMark.Value), Vector3.Distance(paths.Line[^1], Planet.Turned(paths.LandingMark.Value, orbit.SecondsToEnd)));
        output.WriteLine($"de-orbit: the landing mark {mark:0.00} m from where it comes down; the line ends {end:0.00} m from where that ground will be by then");
        Assert.InRange(mark, 0f, 1f);
        Assert.InRange(end, 0f, 2f);
        var (off, seconds) = TrackOff(sim, paths.Line);
        output.WriteLine($"de-orbit: at most {off:0.00} m off the line over {seconds:0} s above the air");
        Assert.True(seconds > 30, $"it was above the air for only {seconds:0} s");
        Assert.InRange(off, 0f, 3f);
    }

    /// <summary>
    /// Flies the flown vessel on with no input while it is above the air (for <paramref name="forSeconds"/>
    /// at most): the farthest it is from <paramref name="line"/> (built now, among the stars as they
    /// are now: each place over the ground turned on by the ground's turn since), and for how long.
    /// </summary>
    private static (float Off, double Seconds) TrackOff(Simulation sim, IReadOnlyList<Vector3> line, double forSeconds = 300)
    {
        var start = sim.Flight.WorldTime;
        var worst = 0f;
        sim.Run((_, _) => default, f =>
        {
            if (f.Altitude <= Scene.AtmosphereHeight || f.Controlled.State != FlightState.Flying)
                return true;
            worst = MathF.Max(worst, DistanceToLine(Planet.Turned(f.CentreOfMass, f.WorldTime - start), [line]));
            return false;
        }, maxSeconds: forSeconds);
        return (worst, sim.Flight.WorldTime - start);
    }

    [Fact]
    public void In_orbit_the_line_is_the_ellipse_in_every_frame()
    {
        // A satellite: its line closes on itself, a lap among the stars, and is the same line in
        // every frame (but for the vessel's moving on a step between).
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var satellite = flight.WorldVessels.First(v => v.Flight == 0);
        sim.Step(new FlightInput(SwitchTo: satellite, SetTarget: NavTarget.OfVessel(flight.WorldVessels.First(v => v != satellite)), ChooseFrame: FrameChoice.Orbit));
        var orbital = Built(flight).Line;
        var gap = Vector3.Distance(orbital[0], orbital[^1]);
        output.WriteLine($"the line closes to {gap:0.0} m");
        Assert.InRange(gap, 0f, 50f);
        foreach (var choice in new[] { FrameChoice.Surface, FrameChoice.Target, FrameChoice.Auto })
        {
            sim.Step(new FlightInput(ChooseFrame: choice));
            var line = Built(flight).Line;
            var apart = line.Max(p => DistanceToLine(p, [orbital]));
            output.WriteLine($"{choice} ({flight.Frame}): {line.Count} points, at most {apart:0.00} m off the Orbit frame's line");
            Assert.Equal(orbital.Count, line.Count);
            Assert.InRange(apart, 0f, 2f);
        }
    }

    [Fact]
    public void Every_point_on_the_line_moves_with_the_frame()
    {
        // A satellite, as it is (Ap and Pe), targeting the next one up (the window shaded on its
        // line, and the closest the two come, a mark on each one's line), then with a node bringing
        // it down into the air (Ap, Air and the node, on the plan's legs): in each frame, every
        // mark is on the line drawn.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var (satellite, next) = (flight.WorldVessels[0], flight.WorldVessels[1]);
        sim.Step(new FlightInput(SwitchTo: satellite));
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(next)));
        foreach (var choice in new[] { FrameChoice.Orbit, FrameChoice.Surface })
        {
            sim.Step(new FlightInput(ChooseFrame: choice, Plan: ManoeuvreChain.Empty));
            var paths = Built(flight);
            Assert.Equal(2, paths.Marks.Count);
            foreach (var mark in paths.Marks)
                Assert.InRange(DistanceToLine(mark.At, [paths.Line]), 0f, 2f);
            Assert.True(paths.WindowLine.Count > 2, "no window to the next satellite");
            var window = paths.WindowLine.Max(p => DistanceToLine(p.At, [paths.Line]));
            var closest = paths.Closest ?? throw new Xunit.Sdk.XunitException("no closest approach marked");
            var (mine, theirs) = (DistanceToLine(closest.Mine, [paths.Line]), DistanceToLine(closest.Theirs, [paths.TheirLine]));
            output.WriteLine($"{choice}: {paths.WindowLine.Count} window points, at most {window:0.00} m off the line; " +
                $"the closest approach's marks {mine:0.00} m off it and {theirs:0.00} m off the target's");
            Assert.InRange(window, 0f, 2f);
            Assert.InRange(mine, 0f, 2f);
            Assert.InRange(theirs, 0f, 2f);

            sim.Step(new FlightInput(Plan: NodeTests.Chain(flight.Controlled, flight.WorldTime, flight.WorldTime + 200, new ManoeuvreNode(0, Prograde: -150f))));
            paths = Built(flight);
            Assert.Single(paths.Nodes);
            Assert.Contains(paths.Marks, m => m.Label == "Air");
            var worst = 0f;
            foreach (var point in paths.Marks.Select(m => m.At).Concat(paths.Nodes))
                worst = MathF.Max(worst, DistanceToLine(point, paths.Legs));
            output.WriteLine($"{choice}: {paths.Marks.Count} marks and the node, at most {worst:0.00} m off the line");
            Assert.InRange(worst, 0f, 2f);
        }
    }

    [Fact]
    public void The_window_drawn_later_has_none_of_the_points_already_passed()
    {
        // The lowest satellite to the next: the window comes round a minute or two in. Drawn some
        // way into it (as a fast warp gets between plans), only what is still ahead is drawn.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var (low, high) = (flight.WorldVessels[0], flight.WorldVessels[1]);
        sim.Step(new FlightInput(SwitchTo: low));
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(high)));
        var paths = Built(flight);
        var planned = paths.PlannedWindow;
        Assert.True(planned.Count > 2, "no window to the next satellite");
        var plannedAt = flight.WorldTime;
        Assert.Equal(planned.Count, paths.WindowLine.Count); // drawn as it is planned, nothing passed yet

        // Half way through it, between two of its points: drawn from there, where the vessel is
        // then (the line starts there too), not from the next point on.
        var later = (planned[0].Seconds + planned[^1].Seconds) / 2;
        later = (planned.Last(p => p.Seconds < later).Seconds + planned.First(p => p.Seconds >= later).Seconds) / 2;
        paths.LayWindow(plannedAt + later);
        var ahead = planned.Where(p => p.Seconds >= later).ToList();
        output.WriteLine($"{planned.Count} points from {planned[0].Seconds:0} to {planned[^1].Seconds:0} s; {paths.WindowLine.Count} drawn {later:0.0} s on");
        Assert.InRange(ahead.Count, 1, planned.Count - 1);
        Assert.True(ahead[0].Joined, "half way through it fell in a gap between two stretches, not part way through one");
        Assert.Equal(ahead.Count + 1, paths.WindowLine.Count);
        Assert.Equal(Paths.WindowPoint(ahead[0].At, plannedAt, plannedAt + later), paths.WindowLine[1].At);
        Assert.False(paths.WindowLine[0].Joined); // where the shading starts
        Assert.True(paths.WindowLine[1].Joined);
        var orbit = Orbit.OverGround(GroundBody.Planet, low.CentreOfMass, low.Velocity);
        var there = Paths.WindowPoint(orbit.StateAfter(later).Position, plannedAt, plannedAt + later);
        var off = Vector3.Distance(there, paths.WindowLine[0].At);
        var between = planned[planned.Count - ahead.Count - 1].Quality;
        output.WriteLine($"the shading starts {off:0.00} m from where the vessel is then, at quality {paths.WindowLine[0].Quality:0.000} " +
            $"(the points either side {between:0.000} and {ahead[0].Quality:0.000})");
        Assert.InRange(off, 0f, 2f);
        Assert.InRange(paths.WindowLine[0].Quality, MathF.Min(between, ahead[0].Quality) - 1e-4f, MathF.Max(between, ahead[0].Quality) + 1e-4f);
    }

    /// <summary>The Satellites world, with its first satellite moved on round its orbit by <paramref name="radians"/>.</summary>
    private static WorldState SatelliteMovedOn(float radians)
    {
        var world = WorldPresets.Satellites();
        var first = world.Vessels[0];
        var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(first.Position.ToSingle() - Planet.Centre, first.Velocity.ToSingle())), radians);
        Vector3 Turned(Vector3 v) => Vector3.Transform(v, turn);
        return world with
        {
            Vessels = [first with
            {
                Position = Planet.Centre + Turned(first.Position.ToSingle() - Planet.Centre),
                Velocity = Turned(first.Velocity.ToSingle()),
                Rotation = Quaternion.Concatenate(first.Rotation, turn),
                Aim = Turned(first.Aim),
            }, .. world.Vessels.Skip(1)],
        };
    }

    [Fact]
    public void In_every_frame_a_second_lap_window_is_marked_next_lap()
    {
        // Among the stars the ellipse is the same every lap, so the line stays one lap and a
        // window on the next is shaded on it, marked as the next lap's; this lap's isn't. The
        // surface frame draws the line among the stars too, so the same.
        foreach (var choice in new[] { FrameChoice.Orbit, FrameChoice.Surface })
        {
            using var sim = new Simulation(world: WorldPresets.Satellites());
            var (_, paths) = WindowOnLap(sim, choice, 1);
            var orbit = Orbit.OverGround(GroundBody.Planet, sim.Flight.CentreOfMass, sim.Flight.Velocity);
            var end = Vector3.Distance(paths.Line[^1], orbit.PositionAfter(orbit.Period));
            output.WriteLine($"{choice}: the line ends {end:0.00} m from where the orbit is a lap on");
            Assert.InRange(end, 0f, 5f); // a lap: among the stars, back where it started
            Assert.All(paths.WindowLine, p => Assert.True(p.NextLap));
            Assert.InRange(paths.WindowLine.Max(p => DistanceToLine(p.At, [paths.Line])), 0f, 2f);

            using var later = new Simulation(world: SatelliteMovedOn(MathF.PI)); // half a lap on: this lap's
            (_, paths) = WindowOnLap(later, choice, 0);
            Assert.All(paths.WindowLine, p => Assert.False(p.NextLap));
        }
    }

    [Fact]
    public void A_next_lap_mark_goes_point_by_point_as_the_vessel_comes_round()
    {
        // Drawn later than it was worked out (as a fast warp gets between plans), each point of a
        // next lap's window is marked by how far ahead it is then: once the vessel is round to
        // within a lap of a point, it is this lap's.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var (_, paths) = WindowOnLap(sim, FrameChoice.Orbit, 1);
        var planned = paths.PlannedWindow;
        var plannedAt = sim.Flight.WorldTime;
        var period = Orbit.OverGround(GroundBody.Planet, sim.Flight.CentreOfMass, sim.Flight.Velocity).Period;
        var middle = (planned[0].Seconds + planned[^1].Seconds) / 2;
        paths.LayWindow(plannedAt + middle - period); // half of it within a lap
        var marked = paths.WindowLine.Count(p => p.NextLap);
        output.WriteLine($"{planned.Count} points, {planned[0].Seconds:0} to {planned[^1].Seconds:0} s on (a lap {period:0} s); drawn {middle - period:0} s on, {marked} still the next lap's");
        Assert.Equal(planned.Count, paths.WindowLine.Count); // none passed
        Assert.Equal(planned.Count(p => p.Seconds - (middle - period) >= period), marked);
        Assert.InRange(marked, 1, planned.Count - 1);
        paths.LayWindow(plannedAt + planned[^1].Seconds - period + 1);
        Assert.All(paths.WindowLine, p => Assert.False(p.NextLap));
    }

    [Fact]
    public void A_later_lap_that_comes_down_has_its_landing_marked()
    {
        // An orbit that dips low: clear of the ground this lap, it comes down on high ground the
        // next (the ground having turned under it), after a window on that lap. Its landing is
        // marked, on the ground it comes down on (where the next lap's orbit has it), in every
        // frame alike.
        var (world, site) = DipsOntoHighGroundNextLap();
        using var sim = new Simulation(world: world);
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.Id == 0), ChooseFrame: FrameChoice.Surface));
        sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, site), ChooseFrame: FrameChoice.Surface));
        var paths = Built(flight);
        Assert.Equal(1, paths.WindowLap);
        Assert.Null(paths.LandingMark); // this lap doesn't come down
        var orbit = Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity);
        var next = orbit.NextLap();
        Assert.True(next.EndsOnGround);
        var later = paths.LaterLanding!.Value;
        output.WriteLine($"to {Sites.All[site].Name}: the later landing is {Vector3.Distance(later, next.Landing):0.00} m from where the next lap comes down, {Planet.SurfaceAltitude(later):0.0} m over the ground");
        Assert.InRange(Vector3.Distance(later, next.Landing), 0f, 5f);
        Assert.InRange(Planet.SurfaceAltitude(later), -5f, 20f);

        sim.Step(new FlightInput(ChooseFrame: FrameChoice.Orbit));
        paths = Built(flight);
        Assert.InRange(Vector3.Distance(paths.LaterLanding!.Value, later), 0f, 5f);
    }

    /// <summary>
    /// The Satellites world with its first satellite on a low ellipse instead (6 km up at the top,
    /// at 0.3 of the map's highest at the bottom), placed round its orbit so that this lap it misses
    /// the ground, the next it comes down on it, and the drone ship's de-orbit window is all on the
    /// next lap, before that; and that site. (Found by trying tops, bottoms and places round the
    /// orbit: if the terrain or the sites change, look again.)
    /// </summary>
    private static (WorldState World, int Site) DipsOntoHighGroundNextLap()
    {
        const float Height = 6000f, Low = 0.3f;
        const int Around = 64, Site = 2; // of 72 places round; the drone ship
        var world = WorldPresets.Satellites();
        var first = world.Vessels[0];
        var normal = Vector3.Normalize(Vector3.Cross(first.CentreOfMass.ToSingle() - Planet.Centre, InOrbit.Stars(first)));
        var top = Planet.SeaRadius + Height;
        var turn = Quaternion.CreateFromAxisAngle(normal, Around * MathF.Tau / 72);
        var up = Vector3.Normalize(Vector3.Transform(first.CentreOfMass.ToSingle() - Planet.Centre, turn));
        var at = Planet.Centre + up * top;
        Vector3 Stars(float bottom) => Vector3.Normalize(Vector3.Cross(normal, up)) * MathF.Sqrt(2 * Planet.Mu * bottom / (top * (top + bottom)));
        var bottom = Planet.SeaRadius + Low * Terrain.Map.MaxHeight;
        var stars = Stars(bottom);

        // As said, with a margin (the vessel's orbit, once flown, is from its first step, a little on).
        foreach (var d in (float[])[-20f, 0f, 20f])
        {
            var near = new Orbit(GroundBody.Planet, at, Stars(bottom + d));
            Assert.True(near.Closed && near.NextLap().EndsOnGround, "this lap should miss the ground and the next come down on it");
        }
        var orbit = new Orbit(GroundBody.Planet, at, stars);
        var window = new List<Encounters.WindowPoint>();
        Assert.Equal(1, Encounters.Deorbit(orbit, Sites.Top(Site), Scene.DeorbitWithin, window));
        Assert.True(window.Count > 2 && window[0].Anomaly > orbit.Now + 2 * Math.PI + 0.1, "the window should be all on the next lap");
        Assert.True(orbit.TimeTo(window[^1].Anomaly) < orbit.PathSeconds + orbit.NextLap().PathSeconds - 30, "the window should be before it comes down");
        return (world with
        {
            Vessels = [InOrbit.At(first, at, stars, Quaternion.Concatenate(first.Rotation, turn)), .. world.Vessels.Skip(1)],
        }, Site);
    }

    [Fact]
    public void A_window_is_worked_out_a_little_each_frame_and_comes_out_the_same()
    {
        // A plan over two laps is tens of milliseconds, so it is worked out a little each frame:
        // for a new target, with no window drawn until it is done (not the last target's). On the
        // same orbit at the same time, it comes out as it did.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        WindowOnLap(sim, FrameChoice.Surface, 1);
        var flight = sim.Flight;
        var plan = PlanFor(flight);
        var paths = new Paths(workMilliseconds: 0); // one step a Build: half done after the first, on any machine
        var (frames, longest) = (0, 0.0);
        do
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            paths.Build(flight, plan);
            longest = Math.Max(longest, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (++frames == 1)
                Assert.True(paths.Working && paths.PlannedWindow.Count == 0 && paths.WindowLine.Count == 0, "nothing drawn until it is worked out");
        } while (paths.Working && frames < 10000);
        var once = paths.PlannedWindow.ToList();
        output.WriteLine($"a new target's window worked out over {frames} frames, the longest {longest:0.00} ms");
        Assert.True(frames > 1, "it should take more than one frame");
        Assert.NotEmpty(once);
        Assert.Equal(1, paths.WindowLap);

        Assert.Equal(once, Built(flight).PlannedWindow); // worked out afresh, the same
    }

    [Fact]
    public void Coasting_on_an_unchanged_orbit_the_window_is_not_worked_out_again()
    {
        // Worked out on an orbit the vessel stays on (coasting, above the air), the window holds:
        // it isn't worked out again, however long between frames in real time, until the scan's
        // horizon has moved on by a tenth of a lap (Scene.WindowReplanLaps); then it is, with the
        // last one drawn meanwhile.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        WindowOnLap(sim, FrameChoice.Surface, 1);
        var flight = sim.Flight;
        var paths = Built(flight);
        var planned = paths.PlannedWindow.ToList();
        var start = flight.WorldTime;
        var lap = Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity).Period;
        void CoastTo(double until) => sim.Run((_, _) => default, f => f.WorldTime >= until, maxSeconds: until - flight.WorldTime + 1);
        const int Looks = 8;
        for (var i = 1; i <= Looks; i++)
        {
            CoastTo(start + 0.9 * Scene.WindowReplanLaps * lap * i / Looks);
            Thread.Sleep(210); // past the time between plans each time
            paths.Build(flight, PlanFor(flight));
            Assert.False(paths.Working, $"{flight.WorldTime - start:0} s on (a lap is {lap:0} s): worked out again on the same orbit");
            Assert.Equal(planned, paths.PlannedWindow);
        }

        CoastTo(start + 1.1 * Scene.WindowReplanLaps * lap);
        Thread.Sleep(210);
        paths.Build(flight, PlanFor(flight));
        output.WriteLine($"a lap of {lap:0} s; not worked out again over {0.9 * Scene.WindowReplanLaps * lap:0} s, then again at {flight.WorldTime - start:0} s");
        Assert.True(paths.Working, "once the horizon has moved on, it should be worked out again");
        Assert.Equal(planned, paths.PlannedWindow); // the last one still drawn meanwhile
    }

    [Fact]
    public void Coasting_at_32x_the_window_holds_without_flickering()
    {
        // Under a fast warp each frame is many steps of the flight (Flight.Advance), so a frame
        // goes half a second of world time: the window drawn is still held, every frame, over a
        // tenth of a lap, never hidden and worked out again. How far the flight strays from the
        // orbit planned on is shown against the drift bound.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        WindowOnLap(sim, FrameChoice.Surface, 1);
        var flight = sim.Flight;
        var paths = Built(flight);
        var planned = paths.PlannedWindow.ToList();
        var (orbit, start) = (Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity), flight.WorldTime);
        var until = start + 0.95 * Scene.WindowReplanLaps * orbit.Period;
        var (frames, metres, speed) = (0, 0f, 0f);
        while (flight.WorldTime < until)
        {
            flight.Advance(32 * Simulation.FrameSeconds, default);
            paths.Build(flight, PlanFor(flight));
            frames++;
            var since = flight.WorldTime - start;
            var (position, velocity) = orbit.StateAfter(since);
            var (here, moving) = Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity).StateAfter(0);
            metres = MathF.Max(metres, Vector3.Distance(Planet.Turned(position, -since), here));
            speed = MathF.Max(speed, Vector3.Distance(Planet.TurnedDirection(velocity, -since), moving));
            Assert.False(paths.Working, $"{since:0} s on: worked out again on the same orbit");
            Assert.Equal(planned, paths.PlannedWindow);
            Thread.Sleep(3); // so that a replan would be due now and then
        }
        output.WriteLine($"{frames} frames at 32x over {flight.WorldTime - start:0} s: strayed at most {metres:0.00} m and {speed:0.000} m/s from the orbit planned on (bounds {Scene.WindowDriftMetres} m, {Scene.WindowDriftSpeed} m/s)");
    }

    [Fact]
    public void Switching_vessels_with_the_same_target_never_shows_one_s_window_on_the_other()
    {
        // One path serves every vessel. Two satellites with the same place targeted: the first's
        // window, worked out or half worked out, is never drawn for the second, nor finished for
        // it: the second's is started afresh, and comes out as its own. And a new flight is a
        // fresh start too.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var (a, b) = (flight.WorldVessels.First(v => v.Id == 0), flight.WorldVessels.First(v => v.Id == 1));
        var pad = new NavTarget(TargetKind.Site, 0);
        void Fly(Vessel vessel) => sim.Step(new FlightInput(SwitchTo: vessel, SetTarget: pad, ChooseFrame: FrameChoice.Surface));

        Fly(b);
        var theirOwn = Built(flight).PlannedWindow.ToList();
        Fly(a);
        var aOwn = Built(flight).PlannedWindow.ToList();
        Assert.NotEmpty(aOwn);
        Assert.NotEqual(aOwn, theirOwn);

        // The first's worked out, then the second flown.
        var paths = Built(flight, null);
        Assert.Equal(aOwn, paths.PlannedWindow);
        Fly(b);
        paths.Build(flight, PlanFor(flight));
        Assert.Empty(paths.PlannedWindow);
        Assert.Empty(paths.WindowLine);
        Assert.Equal(Built(flight).PlannedWindow, Built(flight, null, paths).PlannedWindow); // as its own, worked out now

        // The first's half worked out, then the second flown.
        Fly(a);
        paths = new Paths(workMilliseconds: 0); // one step a Build: half done after the first, on any machine
        paths.Build(flight, PlanFor(flight));
        Assert.True(paths.Working);
        Fly(b);
        Assert.Equal(Built(flight).PlannedWindow, Built(flight, null, paths).PlannedWindow);

        // Another flight, the same vessel and target: started afresh.
        using var other = new Simulation(world: WorldPresets.Satellites());
        other.Step(new FlightInput(SwitchTo: other.Flight.WorldVessels.First(v => v.Id == 1), SetTarget: pad, ChooseFrame: FrameChoice.Surface));
        paths.Build(other.Flight, PlanFor(other.Flight));
        Assert.Empty(paths.PlannedWindow);
    }

    [Fact]
    public void A_plan_half_worked_out_on_an_orbit_since_left_is_started_again()
    {
        // A window half worked out, then a burn (as if zoomed in meanwhile, so nothing worked on
        // it): the next frame starts it again on the orbit the vessel is on now, which it comes
        // out as. Coasting, it carries on.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        WindowOnLap(sim, FrameChoice.Surface, 1);
        var flight = sim.Flight;
        var paths = new Paths(workMilliseconds: 0); // one step a Build: half done after the first, on any machine
        paths.Build(flight, PlanFor(flight));
        var started = paths.WorkingSince;
        Assert.NotNull(started);

        for (var i = 0; i < 5; i++)
            sim.Step(default); // coasting a few frames (quick in real time: not a plan left a while)
        paths.Build(flight, PlanFor(flight));
        Assert.Equal(started, paths.WorkingSince);

        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 2);
        sim.Step(new FlightInput(CutThrottle: true));
        Thread.Sleep(250); // past the time between plans (no more often than that, as each start allocates)
        paths.Build(flight, PlanFor(flight));
        output.WriteLine($"started at {started:0.0} s; after the burn, again at {paths.WorkingSince:0.0} s");
        Assert.Equal(flight.WorldTime, paths.WorkingSince);
        Assert.Equal(Built(flight).PlannedWindow, Built(flight, null, paths).PlannedWindow);
    }

    [Fact]
    public void A_plan_on_an_orbit_in_the_thin_air_is_worked_out_though_the_air_slows_it()
    {
        // In the top of the air the vessel is slowed a little all the time: more, over a plan's
        // frames, than a burn's worth by the drift check alone (more again under warp). That isn't
        // the vessel leaving its orbit for another, so the plan isn't started again for it, and
        // comes out.
        const float Height = 2300f;
        using var sim = new Simulation(world: InTheThinAir(Height));
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.Id == 0), ChooseFrame: FrameChoice.Surface));
        sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, 0), ChooseFrame: FrameChoice.Surface));
        var orbit = Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity);
        Assert.True(orbit.PeriapsisDistance > Planet.Radius - Scene.DeorbitDepth, "not already coming down: a window to look for");
        var paths = new Paths();
        var (frames, speed) = (0, flight.Velocity.Length());
        do
        {
            paths.Build(flight, PlanFor(flight));
            flight.Advance(10 * Simulation.FrameSeconds, default); // 10x warp
        } while (paths.Working && ++frames < 1000);
        output.WriteLine($"{Planet.Altitude(flight.CentreOfMass):0} m up, slowed {speed - flight.Velocity.Length():0.00} m/s over {frames} frames; worked out: {!paths.Working}");
        Assert.False(paths.Working, "the plan should come out");
        Assert.InRange(frames, 1, 400);
    }

    [Fact]
    public void A_plan_in_the_air_picked_up_after_a_while_is_started_again()
    {
        // The air's allowance is for the drag over the frames a plan takes. A plan left half done
        // while the path wasn't drawn, and picked up a minute on, would allow a minute's drag, and
        // hide a burn as big meanwhile: so it is started again.
        using var sim = new Simulation(world: InTheThinAir(2300f));
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.Id == 0), ChooseFrame: FrameChoice.Surface));
        sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, 0), ChooseFrame: FrameChoice.Surface));
        var paths = new Paths(workMilliseconds: 0); // one step a Build: half done after the first, on any machine
        paths.Build(flight, PlanFor(flight));
        var started = paths.WorkingSince;
        Assert.NotNull(started);

        Thread.Sleep(450); // the path not drawn meanwhile
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 0.5); // a burn the minute's drag would hide
        sim.Step(new FlightInput(CutThrottle: true));
        flight.Advance(60f, default);
        paths.Build(flight, PlanFor(flight));
        output.WriteLine($"started at {started:0.0} s; picked up {flight.WorldTime - started:0} s on, started again at {paths.WorkingSince:0.0} s");
        Assert.Equal(flight.WorldTime, paths.WorkingSince);
    }

    [Fact]
    public void A_window_drawn_in_the_air_goes_when_its_plan_is_picked_up_after_a_while()
    {
        // A window drawn, and a replan being worked out (in the air, one is every so often),
        // then the path not drawn for a while: a burn meanwhile, and a minute's drag, which the
        // air's allowance would hide. Picked up again, the window drawn goes that frame, as the
        // replan is started again, rather than being drawn for an orbit left.
        using var sim = new Simulation(world: InTheThinAir(2300f));
        var flight = sim.Flight;
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.Id == 0), ChooseFrame: FrameChoice.Surface));
        Paths? found = null;
        for (var i = 0; i < Sites.All.Length && found is null; i++)
        {
            sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, i), ChooseFrame: FrameChoice.Surface));
            if (Built(flight) is { PlannedWindow.Count: > 2 } built)
                found = built;
        }
        var paths = found ?? throw new Xunit.Sdk.XunitException("no site has a window from this orbit");
        Thread.Sleep(250); // past the time between plans: in the air, it is worked out again
        paths.Build(flight, PlanFor(flight));
        Assert.True(paths.Working, "in the air, a replan should start");
        Assert.NotEmpty(paths.PlannedWindow); // the last one drawn meanwhile

        Thread.Sleep(450); // the path not drawn meanwhile
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 0.5);
        sim.Step(new FlightInput(CutThrottle: true));
        flight.Advance(60f, default);
        paths.Build(flight, PlanFor(flight));
        Assert.Equal(flight.WorldTime, paths.WorkingSince); // started again
        Assert.Empty(paths.PlannedWindow);
        Assert.Empty(paths.WindowLine);
    }

    [Fact]
    public void A_transfer_window_to_a_target_in_the_air_is_worked_out_again_every_so_often()
    {
        // Coasting above the air, a window holds until the horizon moves on; but a target in
        // the top of the air is slowed all the time, so the window to it is worked out again
        // every so often, as one for a vessel in the air is.
        using var sim = new Simulation(world: InTheThinAir(2300f, which: 1));
        var flight = sim.Flight;
        var (mine, theirs) = (flight.WorldVessels.First(v => v.Id == 0), flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: mine, ChooseFrame: FrameChoice.Orbit));
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(theirs), ChooseFrame: FrameChoice.Orbit));
        Assert.True(Planet.Altitude(mine.CentreOfMass) > Scene.AtmosphereHeight + 1000f && Planet.Altitude(theirs.CentreOfMass) < Scene.AtmosphereHeight);
        var paths = Built(flight);
        for (var i = 0; i < 5; i++)
            sim.Step(default);
        Thread.Sleep(250); // past the time between plans
        paths.Build(flight, PlanFor(flight));
        Assert.True(paths.Working, "a target in the air should have its window worked out again");
    }

    /// <summary>The Satellites world with satellite <paramref name="which"/> (the first by default) on a round orbit <paramref name="height"/> up instead, in the top of the air.</summary>
    private static WorldState InTheThinAir(float height, int which = 0)
    {
        var world = WorldPresets.Satellites();
        var vessel = world.Vessels[which];
        var up = Vector3.Normalize(vessel.CentreOfMass.ToSingle() - Planet.Centre);
        var at = Planet.Centre + up * (Planet.SeaRadius + height);
        var ahead = InOrbit.Stars(vessel);
        ahead = Vector3.Normalize(ahead - Vector3.Dot(ahead, up) * up);
        var stars = ahead * MathF.Sqrt(Planet.Mu / (Planet.SeaRadius + height));
        var vessels = world.Vessels.ToList();
        vessels[which] = InOrbit.At(vessel, at, stars, vessel.Rotation);
        return world with { Vessels = [.. vessels] };
    }

    [Fact]
    public void A_burn_starts_the_plan_again_no_more_than_every_so_often()
    {
        // Each start allocates (the plan's steps), so during a burn, when the plan being worked
        // out is left behind every frame, it is started again only every so often (in real time),
        // and in between not worked on (it would come out for an orbit already left). The window
        // drawn from before, for the orbit left, goes the frame the burn has taken the vessel off
        // it (checked every frame, not only as a plan starts), and one comes back once the burn
        // is over.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        WindowOnLap(sim, FrameChoice.Surface, 1);
        var flight = sim.Flight;
        var paths = Built(flight); // just worked out: not due again for a while
        Assert.NotEmpty(paths.PlannedWindow);
        var (plannedOrbit, plannedAt) = (Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity), flight.WorldTime);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (starts, last, left) = (0, (double?)null, (int?)null);
        for (var i = 0; i < 120; i++)
        {
            sim.Step(new FlightInput(FullThrottle: true));
            sim.Step(new FlightInput(FullThrottle: true)); // 2x warp: a burn's worth each frame
            paths.Build(flight, PlanFor(flight));
            if (paths.WorkingSince != last)
                (starts, last) = (starts + 1, paths.WorkingSince);
            // From the frame the burn has taken it a burn's worth off the orbit planned on (by
            // the game's own test), that orbit has been left.
            left ??= Paths.Left(plannedOrbit, Orbit.OverGround(GroundBody.Planet, flight.CentreOfMass, flight.Velocity), flight.WorldTime - plannedAt) ? i : null;
            if (left is not null)
            {
                Assert.Empty(paths.PlannedWindow); // neither the old one nor one worked out for an orbit already left
                Assert.Empty(paths.WindowLine);
                Assert.Null(paths.LaterLanding);
            }
            Thread.Sleep(6); // about a frame's pace, so that it is due again a few times
        }
        var allowed = (int)(clock.Elapsed.TotalSeconds / 0.2) + 1;
        output.WriteLine($"120 frames of burning over {clock.Elapsed.TotalSeconds:0.00} s: the window hidden from frame {left}; started {starts} times (at most {allowed})");
        Assert.InRange(left ?? int.MaxValue, 0, 2); // a burn's worth within a frame or two
        Assert.InRange(starts, 2, allowed);

        // The burn over: one for the orbit it is on now comes out.
        sim.Step(new FlightInput(CutThrottle: true));
        var frames = 0;
        do
        {
            sim.Step(default);
            paths.Build(flight, PlanFor(flight));
            Thread.Sleep(2);
        } while (paths.PlannedWindow.Count == 0 && ++frames < 2000);
        output.WriteLine($"after the burn, a window again {frames} frames on");
        Assert.NotEmpty(paths.PlannedWindow);
    }

    [Fact]
    public void A_target_s_own_burn_starts_a_half_done_transfer_plan_again()
    {
        // A transfer is worked out against the target's orbit as well as the vessel's: the target
        // burning (flown meanwhile, then left) has left the one it was being worked out against.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var (mine, theirs) = (flight.WorldVessels.First(v => v.Id == 0), flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: mine, ChooseFrame: FrameChoice.Orbit));
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(theirs), ChooseFrame: FrameChoice.Orbit));
        var paths = new Paths(workMilliseconds: 0); // one step a Build: half done after the first, on any machine
        paths.Build(flight, PlanFor(flight));
        var started = paths.WorkingSince;
        Assert.NotNull(started);

        sim.Step(new FlightInput(SwitchTo: theirs));
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, maxSeconds: 2);
        sim.Step(new FlightInput(CutThrottle: true));
        sim.Step(new FlightInput(SwitchTo: mine));
        Thread.Sleep(250); // past the time between plans
        paths.Build(flight, PlanFor(flight));
        output.WriteLine($"started at {started:0.00} s; after the target's burn, again at {paths.WorkingSince:0.00} s");
        Assert.Equal(flight.WorldTime, paths.WorkingSince);
        Assert.Equal(Built(flight).PlannedWindow, Built(flight, null, paths).PlannedWindow);
    }

    [Fact]
    public void A_rewind_or_a_world_loaded_starts_the_plan_afresh()
    {
        // The game has one flight, which a rewind or a world loaded starts again: every vessel
        // in it is a new one then, so the window worked out for the one flown before is never
        // drawn for its namesake now.
        using var sim = new Simulation(world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var pad = new NavTarget(TargetKind.Site, 0);
        void FlySatellite() => sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.First(v => v.Id == 0), SetTarget: pad, ChooseFrame: FrameChoice.Surface));

        FlySatellite();
        var paths = Built(flight, null, new Paths(workMilliseconds: 0)); // one step a Build: half done again after the rewind, on any machine
        Assert.NotEmpty(paths.PlannedWindow);
        sim.Step(new FlightInput(Rewind: true));
        FlySatellite();
        paths.Build(flight, PlanFor(flight));
        Assert.True(paths.Working);
        Assert.Empty(paths.PlannedWindow);
        Assert.Equal(Built(flight).PlannedWindow, Built(flight, null, paths).PlannedWindow);

        flight.Load(WorldPresets.Satellites(), flight.Mission);
        FlySatellite();
        paths.Build(flight, PlanFor(flight));
        Assert.True(paths.Working);
        Assert.Empty(paths.PlannedWindow);
        Assert.Equal(Built(flight).PlannedWindow, Built(flight, null, paths).PlannedWindow);
    }

    /// <summary>
    /// The first satellite and site (flown and targeted, in turn, in <paramref name="frame"/>) whose
    /// de-orbit window is on lap <paramref name="lap"/>, and the path built for it.
    /// </summary>
    internal static (string Which, Paths Paths) WindowOnLap(Simulation sim, FrameChoice frame, int lap)
    {
        foreach (var satellite in sim.Flight.WorldVessels.Where(v => v.Flight == 0).ToList())
        {
            sim.Step(new FlightInput(SwitchTo: satellite, ChooseFrame: frame));
            for (var i = 0; i < Sites.All.Length; i++)
            {
                sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, i), ChooseFrame: frame));
                var paths = Built(sim.Flight);
                if (paths.WindowLine.Count > 2 && paths.WindowLap == lap)
                    return ($"satellite {satellite.Id} to {Sites.All[i].Name}", paths);
            }
        }
        throw new Xunit.Sdk.XunitException($"no satellite's window to any site is on lap {lap}");
    }

    /// <summary>The path as <see cref="Paths"/> would draw it now for the flown vessel, with its nodes.</summary>
    internal static Paths Built(Flight flight) => Built(flight, null);

    /// <summary>
    /// <see cref="Built(Flight)"/>, built frame after frame (as the game would) until its window is
    /// worked out, on a new path (or <paramref name="into"/>): each frame's time added to
    /// <paramref name="frameMs"/>, if given.
    /// </summary>
    internal static Paths Built(Flight flight, List<double>? frameMs, Paths? into = null)
    {
        var plan = PlanFor(flight);
        var paths = into ?? new Paths();
        do
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            paths.Build(flight, plan);
            frameMs?.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        } while (paths.Working);
        return paths;
    }

    /// <summary>The flown vessel's plan as it is now (its nodes, if any).</summary>
    internal static ManoeuvrePlan PlanFor(Flight flight)
    {
        var vessel = flight.Controlled;
        var plan = new ManoeuvrePlan();
        plan.Update(vessel, vessel.Chain, flight.WorldTime);
        return plan;
    }

    /// <summary>How far apart two places are across the ground (along the sphere through the pad).</summary>
    private static float AcrossGround(Vector3 a, Vector3 b) =>
        Planet.Radius * MathF.Acos(Math.Clamp(Vector3.Dot(Planet.Up(a), Planet.Up(b)), -1f, 1f));

    private static float DistanceToLine(Vector3 p, IReadOnlyList<IReadOnlyList<Vector3>> lines)
    {
        var best = float.MaxValue;
        foreach (var line in lines)
            for (var i = 1; i < line.Count; i++)
            {
                var (a, b) = (line[i - 1], line[i]);
                var t = (b - a).LengthSquared() > 1e-6f ? Math.Clamp(Vector3.Dot(p - a, b - a) / (b - a).LengthSquared(), 0f, 1f) : 0f;
                best = MathF.Min(best, Vector3.Distance(p, a + (b - a) * t));
            }
        return best;
    }

    [Fact]
    public void Nothing_is_targeted_by_default()
    {
        using var sim = new Simulation();
        Assert.Null(sim.Flight.Target);
        Assert.Null(sim.Flight.DistanceToTarget);
        Assert.True(sim.Run(new Autopilot().Fly, f => f.Ground > 100f, maxSeconds: 60));
        Assert.Null(sim.Flight.Target);
        Assert.Null(sim.Flight.DistanceToTarget);

        using var staged = new Simulation(Mission.ReachOrbit);
        Assert.True(staged.Run(new OrbitAutopilot().Fly, f => f.Vessels.Count > 1, maxSeconds: 120));
        Assert.Null(staged.Flight.Target);
        Assert.Null(staged.Flight.DistanceToTarget);
        Assert.All(staged.Flight.Vessels, v => Assert.Null(v.Target));
    }

    [Fact]
    public void To_target_is_the_straight_line_distance_to_the_target()
    {
        using var sim = new Simulation();
        var flight = sim.Flight;
        sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, 1))); // the landing zone, from the pad
        Assert.NotNull(flight.DistanceToTarget);
        Assert.Equal(Vector3.Distance(flight.CentreOfMass, Sites.Top(1)), flight.DistanceToTarget!.Value, 0.1f);

        using var orbit = new Simulation(world: WorldPresets.Satellites());
        var other = orbit.Flight.WorldVessels.First(v => v.Flight == 0);
        orbit.Step(new FlightInput(SetTarget: NavTarget.OfVessel(other)));
        var mine = orbit.Flight.Controlled;
        Assert.NotSame(other, mine);
        Assert.Equal(Vector3.Distance(mine.CentreOfMass, other.CentreOfMass), orbit.Flight.DistanceToTarget!.Value, 0.1f);

        orbit.Step(new FlightInput(ClearTarget: true));
        Assert.Null(orbit.Flight.DistanceToTarget);
    }

    [Fact]
    public void The_readout_shows_the_target_To_target_and_the_relative_speed_as_the_HUD_does()
    {
        // As the instruments: To target with a target picked, the relative speed in the target frame.
        using var sim = new Simulation();
        var flight = sim.Flight;
        var shown = Readout.FlightReadings(flight);
        Assert.DoesNotContain("To target", shown.Keys);
        Assert.DoesNotContain("Relative speed", shown.Keys);
        Assert.Equal("none", shown["Target"]);

        sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, 1)));
        shown = Readout.FlightReadings(flight);
        Assert.Equal($"{flight.DistanceToTarget!.Value:0} m", shown["To target"]);
        Assert.Equal("Landing zone", shown["Target"]); // by its marker's name
        Assert.DoesNotContain("Relative speed", shown.Keys);

        sim.Step(new FlightInput(ChooseFrame: FrameChoice.Target));
        shown = Readout.FlightReadings(flight);
        Assert.Equal($"{flight.RelativeSpeed!.Value:0.0} m/s", shown["Relative speed"]);
        Assert.Equal("Target", shown["Frame"]);

        sim.Step(new FlightInput(ClearTarget: true));
        shown = Readout.FlightReadings(flight);
        Assert.DoesNotContain("To target", shown.Keys);
        Assert.DoesNotContain("Relative speed", shown.Keys);
        Assert.Equal("none", shown["Target"]);
    }

    [Fact]
    public void The_readout_shows_the_world_time_and_the_local_time_as_the_HUD_does()
    {
        // As the top of the flight panel: the world's clock (Home's day and time), and the time of
        // day where the flown vessel is, by its longitude. Polar isn't on Home's meridian, so they differ.
        using var sim = new Simulation(world: new WorldState { Time = 777 }, site: LaunchSites.Named("Polar"));
        var flight = sim.Flight;
        Assert.Equal(777, flight.WorldTime);
        var shown = Readout.FlightReadings(flight);
        Assert.Equal(Sun.WorldClock(777), shown["World time"]);
        Assert.Equal(Sun.Clock(Sun.LocalSeconds(GroundBody.Planet, flight.Position, 777)), shown["Local time"]);
        // Polar's longitude puts its clock ahead of Home's by that part of a turn (3.5° of 360° of a day).
        var site = LaunchSites.Named("Polar");
        var ahead = Sun.LocalSeconds(GroundBody.Planet, flight.Position, 777) - Sun.LocalSeconds(GroundBody.Planet, Vector3.Zero, 777);
        ahead -= Math.Round(ahead / Scene.DaySeconds) * Scene.DaySeconds; // (the nearer way round)
        Assert.Equal(site.Longitude / 360.0 * Scene.DaySeconds, ahead, 0.5);
        Assert.NotEqual(shown["World time"][^5..], shown["Local time"]);
        output.WriteLine($"World time {shown["World time"]}, Local time {shown["Local time"]} at Polar");
    }

    [Fact]
    public void A_target_never_changes_the_score()
    {
        (bool OnTarget, int Points) Land(bool target)
        {
            var zone = Scene.LandingZoneCentre;
            var other = Drops.Placed(Planet.SurfacePoint(300f, 300f), Vector3.UnitX, 0f, id: 1) with { State = FlightState.Landed };
            var mine = Drops.Placed(zone, Planet.East(zone), 1f);
            using var sim = new Simulation(world: new WorldState { Vessels = [mine, other], NextVesselId = 2 });
            sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0),
                SetTarget: target ? NavTarget.OfVessel(sim.Flight.WorldVessels.First(v => v.Id == 1)) : null));
            Assert.Equal(target, sim.Flight.DistanceToTarget is not null);
            Assert.True(sim.RunToEnd((_, _) => new FlightInput(), maxSeconds: 60));
            return (sim.Flight.LandedOnTarget, Mission.TargetLandingPoints(sim.Flight.Controlled));
        }
        var with = Land(true);
        var without = Land(false);
        Assert.True(with.OnTarget);
        Assert.True(with.Points > 0);
        Assert.Equal(without, with);
    }

    private static float Degrees(Vector3 a, Vector3 b) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)));
}
