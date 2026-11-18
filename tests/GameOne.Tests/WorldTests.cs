using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The world a flight is flown in, which carries on from flight to flight: what earlier flights
/// left stays where it is (and keeps flying) when another starts; a mission counts only its own
/// flight; starting again undoes the flight; wrecks are gone, and the launch pad is cleared for
/// the new rocket; and the world can be captured and brought back exactly.
/// </summary>
public sealed class WorldTests(ITestOutputHelper output)
{
    private static readonly Pilot Climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;
    private static readonly Pilot Coast = (_, _) => default;

    /// <summary>Flies Reach Orbit's autopilot until the upper stage is in a stable orbit: the booster is left falling.</summary>
    private static void FlyToOrbit(Simulation sim)
    {
        var pilot = new OrbitAutopilot();
        Assert.True(sim.Run(pilot.Fly, f => f.AchievedOrbit, maxSeconds: 300), "the autopilot should reach orbit");
    }

    [Fact]
    public void Choosing_a_mission_starts_another_flight_and_leaves_the_last_one_s_vessels_where_they_are()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        FlyToOrbit(sim);
        var upper = flight.Controlled;
        Assert.Equal(1, flight.Run!.Number);

        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        Assert.Equal(2, flight.Run!.Number);
        Assert.Single(flight.Vessels); // this flight: the new rocket on the pad
        Assert.Equal(FlightState.Ready, flight.Controlled.State);
        Assert.Contains(upper, flight.WorldVessels);
        Assert.Equal(1, upper.Flight);

        // The upper stage carries on round its orbit while the new rocket sits on the pad.
        var position = upper.Position;
        sim.Run(Coast, _ => false, maxSeconds: 30);
        Assert.Equal(FlightState.Flying, upper.State);
        Assert.True(upper.OrbitStable);
        Assert.True(Vector3.Distance(position, upper.Position) > 1000f, "the upper stage should still be going round");
        Assert.Equal(FlightState.Ready, flight.State); // and the flight is this one's: on the pad
    }

    [Fact]
    public void A_mission_counts_only_its_own_flight()
    {
        using var sim = new Simulation(Mission.LaunchAndLand);
        var flight = sim.Flight;
        Assert.True(sim.RunToEnd(new Autopilot().Fly));
        Assert.True(flight.Run!.Succeeded);
        var first = flight.Controlled;

        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        Assert.Contains(first, flight.WorldVessels); // still standing on the landing zone ...
        Assert.Equal(FlightState.Landed, first.State);
        Assert.False(flight.Run!.Succeeded); // ... but it was the last flight that landed it
        Assert.Null(flight.Run!.Score);
    }

    [Fact]
    public void A_rewind_undoes_this_flight_and_puts_the_world_back_as_it_was()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        FlyToOrbit(sim);
        sim.Step(new FlightInput(Choose: Mission.ReachOrbit));
        var before = flight.WorldVessels.Where(v => v.Flight == 1).Select(v => (v.Id, v.Position)).ToArray();
        var time = flight.WorldTime;

        // A second flight that stages, then is undone.
        sim.Run(Climb, f => f.Ground > 300f, maxSeconds: 30);
        sim.Step(new FlightInput(Stage: true));
        Assert.Equal(2, flight.Vessels.Count);
        sim.Step(new FlightInput(Rewind: true));

        Assert.Equal(2, flight.Run!.Number); // the same flight, again
        Assert.Single(flight.Vessels);
        Assert.Equal(FlightState.Ready, flight.Controlled.State);
        Assert.Equal(time, flight.WorldTime, 0.05);
        var after = flight.WorldVessels.Where(v => v.Flight == 1).Select(v => (v.Id, v.Position)).ToArray();
        Assert.Equal(before.Select(b => b.Id), after.Select(a => a.Id));
        for (var i = 0; i < before.Length; i++)
            Assert.True(Vector3.Distance(before[i].Position, after[i].Position) < 0.01f, $"vessel {before[i].Id} isn't back where it was");
        Assert.DoesNotContain(flight.WorldVessels, v => v.Flight == 2 && v != flight.Controlled);
    }

    [Fact]
    public void Wrecks_are_gone_when_the_next_flight_starts()
    {
        using var sim = new Simulation(Mission.LaunchAndLand);
        var flight = sim.Flight;
        sim.RunToEnd(new Autopilot { TouchdownSpeed = 12f }.Fly);
        Assert.Equal(FlightState.Crashed, flight.Controlled.State);
        var wreck = flight.Controlled;

        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        Assert.DoesNotContain(wreck, flight.WorldVessels);
        Assert.Single(flight.WorldVessels);
    }

    [Fact]
    public void A_rocket_wrecked_on_the_pad_is_gone_at_once_when_a_new_flight_is_chosen()
    {
        // Up a little and back down onto the pad, too hard: wrecked where the new rocket will
        // stand. A mission chosen at once (the menu pauses the game, so no steps pass between)
        // must find nothing of it there: no body left to shove the new rocket or wreck it.
        using var sim = new Simulation(Mission.LaunchAndLand);
        var flight = sim.Flight;
        var crafts = sim.Physics.Crafts.Count;
        var first = flight.Controlled;
        sim.Run(Climb, f => f.Ground > 25f, maxSeconds: 20);
        sim.Step(new FlightInput(CutThrottle: true));
        Assert.True(sim.Run(Coast, _ => first.State == FlightState.Crashed, maxSeconds: 20), "the rocket never came down");
        output.WriteLine($"wrecked: {first.CrashReason}");
        Assert.True(Vector3.Distance(first.Position, Scene.RocketStart) < 10f, "it should come down on the pad");

        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        var rocket = flight.Controlled;
        Assert.Equal(crafts, sim.Physics.Crafts.Count); // the wreck's body is gone already
        var start = rocket.Position;
        sim.Run(Coast, _ => false, maxSeconds: 2);
        output.WriteLine($"new rocket {rocket.State} {rocket.CrashReason}, moved {Vector3.Distance(start, rocket.Position):F3} m");
        Assert.Equal(FlightState.Ready, rocket.State);
        Assert.True(Vector3.Distance(start, rocket.Position) < 0.05f, "the new rocket was pushed");
    }

    [Fact]
    public void Anything_left_on_the_launch_pad_is_cleared_away_for_the_new_rocket()
    {
        using var sim = new Simulation(Mission.LaunchAndLand);
        var flight = sim.Flight;
        var unlaunched = flight.Controlled; // never launched: still standing on the pad
        sim.Step(new FlightInput(Choose: Mission.ReachOrbit));
        Assert.DoesNotContain(unlaunched, flight.WorldVessels);
        Assert.True(unlaunched.Craft.Removed);
        Assert.Equal(0, flight.ClearedFromPad); // just replaced: nothing to tell

        // A booster that flew and came back down on the launch pad itself is in the way.
        using var back = new Simulation(Mission.LaunchAndLand);
        Assert.True(back.RunToEnd(new Autopilot { Target = Scene.RocketStart }.Fly));
        Assert.Equal(FlightState.Landed, back.Flight.Controlled.State);
        var landed = back.Flight.Controlled;
        back.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        Assert.DoesNotContain(landed, back.Flight.WorldVessels);
        Assert.Equal(1, back.Flight.ClearedFromPad);

        // Whereas a booster that landed on its landing zone, off the pad, stays.
        using var landing = new Simulation(Mission.LaunchAndLand);
        Assert.True(landing.RunToEnd(new Autopilot().Fly));
        landing.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        Assert.Equal(0, landing.Flight.ClearedFromPad);
        Assert.Equal(2, landing.Flight.WorldVessels.Count);
    }

    [Fact]
    public void A_rocket_launched_at_zero_throttle_that_never_left_the_pad_is_just_replaced()
    {
        // Space at zero throttle: launched, but its engine never lit and it never left the pad.
        // The next flight's rocket replaces it, as one never launched, with nothing to tell.
        using var sim = new Simulation(Mission.LaunchAndLand);
        var flight = sim.Flight;
        var sitting = flight.Controlled;
        sim.Step(new FlightInput(Launch: true, CutThrottle: true));
        sim.Run((_, _) => default, _ => false, 3);
        Assert.Equal(FlightState.Flying, sitting.State);
        Assert.False(sitting.EngineLit);
        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        Assert.DoesNotContain(sitting, flight.WorldVessels);
        Assert.Equal(0, flight.ClearedFromPad);
    }

    [Fact]
    public void Tab_cycles_the_flown_vessel_s_flight_s_vessels_and_another_flight_s_is_flown_through_its_view()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        FlyToOrbit(sim);
        var upper = flight.Controlled;
        sim.Step(new FlightInput(Choose: Mission.ReachOrbit));
        var rocket = flight.Controlled;

        Assert.False(flight.CanSwitch);
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(rocket, flight.Controlled); // Tab: nothing else in this flight

        sim.Step(new FlightInput(SwitchTo: upper));
        Assert.Same(upper, flight.Controlled); // a click on its view
        Assert.All(flight.Vessels, v => Assert.Equal(upper.Flight, v.Flight)); // its own flight's now
        Assert.DoesNotContain(rocket, flight.Vessels);
        var vessels = flight.Vessels.ToArray();
        var at = Array.IndexOf(vessels, upper);
        var next = Enumerable.Range(1, vessels.Length).Select(step => vessels[(at + step) % vessels.Length]).First(v => v != upper && Flight.CanFly(v));
        Assert.True(flight.CanSwitch);
        sim.Step(new FlightInput(SwitchVessel: true));
        Assert.Same(next, flight.Controlled); // Tab stays in its flight, flying the next of it
        Assert.Equal(upper.Flight, flight.Controlled.Flight);
    }

    [Fact]
    public void The_satellites_preset_s_satellites_hold_their_orbits_among_the_stars_9_to_10_km_up()
    {
        var preset = WorldPresets.Satellites();
        using var sim = new Simulation(Mission.LaunchAndLand, world: preset);
        var flight = sim.Flight;
        var satellites = flight.WorldVessels.Where(v => v.Flight == 0).ToArray();
        Assert.Equal(4, satellites.Length);
        Assert.All(satellites, s => Assert.True(Flight.CanFly(s) && s.Fuel == 1f, $"{s.Name} {s.Id} should be flyable, with a full tank"));
        Assert.Equal(0, flight.ClearedFromPad);
        // The others watched (as in small views), so it is the physics' orbits that are checked:
        // nobody watching them, they would coast by formula (see Unwatched), exact by its making.
        var watched = satellites[1..];
        sim.Step(new FlightInput(SwitchTo: satellites[0], Watched: watched));
        Assert.Same(satellites[0], flight.Controlled);

        // The first two share a plane (for the transfer between them); the others each have their own.
        var normals = satellites.Select(s => Vector3.Normalize(Vector3.Cross(s.CentreOfMass - Planet.Centre, Planet.AmongStars(s.CentreOfMass, s.Velocity)))).ToArray();
        for (var i = 0; i < normals.Length; i++)
            for (var j = i + 1; j < normals.Length; j++)
            {
                if (i == 0 && j == 1)
                    Assert.True(Vector3.Dot(normals[i], normals[j]) > 0.9999f, "satellites 0 and 1 should share a plane");
                else
                    Assert.True(MathF.Abs(Vector3.Dot(normals[i], normals[j])) < 0.9f, $"satellites {i} and {j} share a plane");
            }

        // A whole lap of the highest one (the longest), so each goes all the way round at least
        // once, and they all stay where they were put. Worked out among the stars, an orbit is the
        // same all the way, though the ground it is simulated over turns under it (the turning
        // frame's pulls, Planet.TurningPull, keep it on the same ellipse among the stars). The Ap
        // and Pe wander a little either way as the step's errors come and go, but the size of the
        // orbit (its semi-major axis, their mean) mustn't creep: a step that loses a little energy
        // every time (as a plain Euler kick of the turning pulls did, about 3 m a lap) would bring
        // anything left in orbit down over a few hundred laps. Each one's creep is read as it
        // finishes its own lap, where the wander within a lap has come back round (within a lap the
        // lowest's semi-major axis wanders by up to 0.06 m, and is back within 0.012 m at its end).
        var count = satellites.Length;
        var periods = satellites.Select(s => Orbit.OverGround(GroundBody.Planet, s.CentreOfMass, s.Velocity).Period).ToArray();
        var semiMajor = satellites.Select(s => (s.Apoapsis + s.Periapsis) / 2).ToArray();
        var drift = new float?[count];
        // Each one's size as the physics' double copy has it too (its float Ap and Pe read it only to millimetres).
        double TrueSize(Vessel s)
        {
            var physics = s.Physics!;
            var r = physics.TrueCentreOfMass(s.Craft) - (Vector3Double)Planet.Centre;
            var v = physics.TrueVelocity(s.Craft) + Vector3Double.Cross((Vector3Double)Planet.Spin, r);
            return 1 / (2 / r.Length() - v.LengthSquared() / Planet.Mu);
        }
        var trueSize = satellites.Select(TrueSize).ToArray();
        var trueDrift = new double[count];
        var lowest = new float[count].Select(_ => float.MaxValue).ToArray();
        var highest = new float[count].Select(_ => float.MinValue).ToArray();
        var (apoapsis, periapsis) = (satellites[0].Apoapsis, satellites[0].Periapsis);
        var (apoapsisOff, periapsisOff) = (0f, 0f); // the furthest the lowest's strays from where it started
        var start = sim.Time;
        sim.Run((_, _) => new FlightInput(Watched: watched), _ =>
        {
            Assert.All(satellites, s => Assert.False(s.OnFormula));
            for (var i = 0; i < count; i++)
            {
                var s = satellites[i];
                // Above the sea, as the orbit is: the ground under it rises and falls.
                (lowest[i], highest[i]) = (MathF.Min(lowest[i], Planet.Altitude(s.Position)), MathF.Max(highest[i], Planet.Altitude(s.Position)));
                if (drift[i] is null && sim.Time - start >= periods[i])
                {
                    drift[i] = (s.Apoapsis + s.Periapsis) / 2 - semiMajor[i];
                    trueDrift[i] = TrueSize(s) - trueSize[i];
                }
            }
            apoapsisOff = MathF.Max(apoapsisOff, MathF.Abs(satellites[0].Apoapsis - apoapsis));
            periapsisOff = MathF.Max(periapsisOff, MathF.Abs(satellites[0].Periapsis - periapsis));
            return drift.All(d => d is not null);
        }, maxSeconds: periods.Max() + 1);
        for (var i = 0; i < count; i++)
            output.WriteLine($"satellite {satellites[i].Id}: {lowest[i]:0.0} to {highest[i]:0.0} m over its lap of {periods[i]:0} s; the semi-major axis drifted {drift[i]:0.000} m in it as read, {trueDrift[i]:0.000000} m by the copy");
        output.WriteLine($"the lowest's Ap strayed up to {apoapsisOff:0.00} m, its Pe {periapsisOff:0.00} m");
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(FlightState.Flying, satellites[i].State);
            Assert.InRange(lowest[i], 8900f, 10100f);
            // Circular: nearly the same height all the way round (measured 1.2 to 2.3 m, the body's
            // origin turning about its centre of mass among them; 150 m was the old, looser mark).
            Assert.InRange(highest[i] - lowest[i], 0f, 10f);
        }
        // The lowest's Ap and Pe are watched for about one lap (its slow drift, over many, is the drift check's).
        Assert.InRange(apoapsisOff, 0f, 10f); // measured 0.5 m
        Assert.InRange(periapsisOff, 0f, 10f); // and 0.4 m
        // The orbits' sizes hold, every one, tilted or not. A craft flying free is stepped in
        // double by the physics' own copy (RocketPhysics.TrueCentreOfMass) and Jolt handed it
        // rounded once a step, so no float rounding is carried from step to step: by the copy,
        // every one's size is back within 0.0001 m at its lap's end (measured; the tilted ones
        // wander up to 0.45 m within it, and come back), held to 0.001 m. As the vessels' Ap and
        // Pe read it (worked out from Jolt's floats, to a few millimetres: measured -0.008 to
        // 0.003 m, all of it the reading's), held to 0.01 m. (Stepped in Jolt's floats, the 60° one creeps
        // 0.2 m a lap, steadily, the same way: each step's rounding leans one way in the turning
        // frame. A plain Euler kick of the turning pulls costs the lowest 3.15 m a lap and the 60°
        // one 0.35 m: either shows here.)
        for (var i = 0; i < count; i++)
        {
            Assert.InRange(trueDrift[i], -0.001, 0.001);
            Assert.InRange(drift[i]!.Value, -0.01f, 0.01f);
        }
    }

    [Fact]
    public void The_satellites_preset_s_orbits_are_circles()
    {
        // Jolt's velocity is the centre of mass's, so it is the centre of mass that is put on the
        // circle: with the body's origin there instead, the orbit reads a metre out of round.
        using var sim = new Simulation(Mission.LaunchAndLand, world: WorldPresets.Satellites());
        foreach (var s in sim.Flight.WorldVessels.Where(v => v.Flight == 0))
        {
            output.WriteLine($"satellite {s.Id}: Ap {s.Apoapsis:0.000} m, Pe {s.Periapsis:0.000} m, {s.Apoapsis - s.Periapsis:0.000} m apart");
            Assert.InRange(s.Apoapsis - s.Periapsis, 0f, 0.5f);
        }
    }

    [Fact]
    public void In_the_satellites_preset_a_transfer_window_from_the_lowest_to_the_next_comes_round_early_in_the_first_lap()
    {
        var preset = WorldPresets.Satellites();
        var (low, high) = (preset.Vessels[0], preset.Vessels[1]);
        var mine = Orbit.OverGround(GroundBody.Planet, low.CentreOfMass, low.Velocity);
        var window = new List<Encounters.WindowPoint>();
        Encounters.Transfer(mine, Orbit.OverGround(GroundBody.Planet, high.CentreOfMass, high.Velocity), Scene.TransferWithin, window);
        Assert.NotEmpty(window);
        var best = window.MinBy(p => p.Miss);
        var (from, to, at) = (mine.TimeTo(window[0].Anomaly), mine.TimeTo(window[^1].Anomaly), mine.TimeTo(best.Anomaly));
        output.WriteLine($"window {from:0} to {to:0} s in, best {best.Miss:0} m at {at:0} s");
        Assert.True(best.Miss < 300f);
        // Soon enough to wait for, but not so soon it has gone by the time the player has picked
        // the satellite and its target.
        Assert.InRange(at, 60, 150);
    }

    /// <summary>A busy world: the satellites preset's four, copied round their orbits 25 times over.</summary>
    internal static WorldState HundredSatellites() => ManySatellites(25);

    /// <summary>The satellites preset's four, copied <paramref name="copies"/> times, spread evenly round the planet.</summary>
    internal static WorldState ManySatellites(int copies)
    {
        var preset = WorldPresets.Satellites();
        var many = new List<VesselState>();
        for (var copy = 0; copy < copies; copy++)
        {
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, copy * MathF.Tau / copies); // about the planet's centre (it is below the pad, on -Y)
            foreach (var s in preset.Vessels)
                many.Add(s with
                {
                    Id = many.Count,
                    Position = Planet.Centre + Vector3.Transform(s.Position - Planet.Centre, turn),
                    Rotation = Quaternion.Concatenate(s.Rotation, turn), // still standing up from the planet, so the centre of mass is where it says
                    Velocity = Vector3.Transform(s.Velocity, turn),
                });
        }
        return preset with { Vessels = many, NextVesselId = many.Count };
    }

    /// <summary>
    /// Reach Orbit's upper stage (numbered <paramref name="id"/>), a vessel of the flight a
    /// simulation starts, as the orbit autopilot leaves it at the top of its climb: 50 m over the
    /// top it climbs to, at 97% of the speed that would hold it there, so its lowest point is down
    /// in the air (about 1.9 km up) until it burns. Its half tank is InOrbit.UpperStage's, a
    /// convenience, not what the climb would leave.
    /// </summary>
    private static VesselState UpperStageAtTheTop(int id) => InOrbit.UpperStage(id, new OrbitAutopilot().TargetApoapsis + 50f, 0.97f);

    [Fact]
    public void A_world_can_hold_a_hundred_vessels_and_still_put_another_in_orbit()
    {
        // This flight's upper stage among them, at the top of its climb, the booster dropped, its
        // lowest point still in the air: the burn there is what puts it in orbit. The climb from the
        // pad to here is Reach Orbit's own (OrbitTests' playthrough), and launching and staging in a
        // world fuller still is A_full_world_lets_its_oldest_vessels_go_to_make_room's: each step of
        // a busy world is slow, so it starts where it is about.
        var world = HundredSatellites();
        using var sim = new Simulation(Mission.ReachOrbit, world: world with { Vessels = [.. world.Vessels, UpperStageAtTheTop(world.NextVesselId)], NextVesselId = world.NextVesselId + 1 });
        var flight = sim.Flight;
        Assert.Equal(102, flight.WorldVessels.Count); // and the new rocket on the pad
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.Single(v => v.Id == world.NextVesselId)));
        Assert.False(flight.AchievedOrbit);
        var pilot = new OrbitAutopilot();
        Assert.True(sim.Run(pilot.Fly, f => f.AchievedOrbit, maxSeconds: 300), "the flight should reach orbit among them all");
        Assert.All(flight.WorldVessels.Where(v => v.Flight == 0), s => Assert.True(s.OrbitStable));
        output.WriteLine($"{flight.WorldVessels.Count} vessels, {sim.Physics.BodyCount} bodies, in {sim.WallTime.TotalSeconds:0.0} s");
    }

    [Fact]
    public void A_full_world_lets_its_oldest_vessels_go_to_make_room()
    {
        // Loaded with more than it holds: the newest are kept, less one for the new rocket.
        var world = ManySatellites(Flight.Capacity / 4 + 2);
        using var sim = new Simulation(Mission.ReachOrbit, world: world);
        var flight = sim.Flight;
        Assert.Equal(Flight.Capacity, flight.WorldVessels.Count);
        Assert.Contains(flight.Controlled, flight.WorldVessels);
        var kept = flight.WorldVessels.Where(v => v.Flight == 0).Select(v => v.Id);
        Assert.Equal(Enumerable.Range(world.Vessels.Count - (Flight.Capacity - 1), Flight.Capacity - 1), kept);
        Assert.Equal(world.Vessels.Count - (Flight.Capacity - 1), flight.RemovedForRoom);

        // Staging adds a vessel, so the oldest goes for it; the one being flown stays. Staged just
        // off the pad: how high it is has nothing to do with the room, and each step of a full
        // world is slow.
        Assert.True(sim.Run(Climb, f => f.Ground > 2f, maxSeconds: 60), "the rocket should lift off");
        var oldest = flight.WorldVessels[0];
        var removed = flight.RemovedForRoom;
        sim.Step(new FlightInput(Stage: true));
        Assert.Equal(Flight.Capacity, flight.WorldVessels.Count);
        Assert.DoesNotContain(oldest, flight.WorldVessels);
        Assert.Equal(removed + 1, flight.RemovedForRoom);
        Assert.Equal(2, flight.Vessels.Count); // this flight's booster and upper stage
        Assert.All(flight.Vessels, v => Assert.Contains(v, flight.WorldVessels));
        output.WriteLine($"{flight.WorldVessels.Count} vessels, {sim.Physics.BodyCount} bodies, in {sim.WallTime.TotalSeconds:0.0} s");
    }

    /// <summary>A mission whose one step is never done nor lost, so a run of it stays in progress for as long as it has a vessel.</summary>
    private static readonly Mission Holding = new() { Name = "Holding", Briefing = "", Vehicle = Vehicle.Booster, Steps = [new("Held", _ => false)] };

    /// <summary>A world of <paramref name="vessels"/> satellites (the first of flight 1, the rest of flight <paramref name="others"/>), with runs in progress for flights 1 and 2.</summary>
    private static WorldState WithRuns(int vessels, int others)
    {
        var many = ManySatellites(Flight.Capacity / 4);
        Assert.True(many.Vessels.Count >= vessels);
        var steps = Holding.Steps.Length;
        RunState Run(int number, int flown) => new()
        {
            Mission = Holding, Site = LaunchSites.Home.Name, Number = number, LastFlown = flown,
            Done = new bool[steps], Points = new int[steps],
        };
        return many with
        {
            Vessels = [.. many.Vessels.Take(vessels).Select(v => v with { Flight = v.Id == 0 ? 1 : others })],
            NextVesselId = vessels, Flights = 2, Runs = [Run(1, 0), Run(2, 1)],
        };
    }

    [Fact]
    public void Making_room_takes_debris_before_vessels()
    {
        // Old satellites, then newer fairing halves, up to one short of the cap (the new rocket
        // makes it): staging adds one more, and a half goes, not the oldest satellite.
        var sats = ManySatellites(Flight.Capacity / 4).Vessels.Take(Flight.Capacity - 4).ToList();
        var halves = Enumerable.Range(0, 3).Select(i => sats[i] with
        {
            Id = sats.Count + i, Vehicle = Vehicle.FairingHalf, First = 0, Last = 0, Fuel = [0f],
            LegDeploy = 0f, LegsWanted = false, FairingOn = false,
        });
        var world = ManySatellites(1) with { Vessels = [.. sats, .. halves], NextVesselId = Flight.Capacity - 1 };
        using var sim = new Simulation(Mission.ReachOrbit, world: world);
        var flight = sim.Flight;
        Assert.Equal(Flight.Capacity, flight.WorldVessels.Count);
        Assert.True(sim.Run(Climb, f => f.Ground > 2f, maxSeconds: 60), "the rocket should lift off");
        var (oldest, removed) = (flight.WorldVessels[0], flight.RemovedForRoom);
        sim.Step(new FlightInput(Stage: true));
        Assert.Equal(Flight.Capacity, flight.WorldVessels.Count);
        Assert.Contains(oldest, flight.WorldVessels);
        Assert.Equal(removed + 1, flight.RemovedForRoom);
        Assert.Equal(2, flight.WorldVessels.Count(v => v.Vehicle == Vehicle.FairingHalf));
    }

    [Fact]
    public void Making_room_takes_a_runs_vessels_last()
    {
        // Run 1's one vessel is the oldest in the world. While vessels of no run remain, they go
        // and it stays; when every other is another run's, it goes first, and run 1 has none left.
        var count = Flight.Capacity - 1;
        using (var sim = new Simulation(Mission.ReachOrbit, world: WithRuns(count, others: 0)))
        {
            var flight = sim.Flight;
            Assert.True(sim.Run(Climb, f => f.Ground > 2f, maxSeconds: 60), "the rocket should lift off");
            var kept = flight.WorldVessels.Single(v => v.Id == 0);
            var next = flight.WorldVessels.Single(v => v.Id == 1);
            sim.Step(new FlightInput(Stage: true));
            Assert.Contains(kept, flight.WorldVessels);
            Assert.DoesNotContain(next, flight.WorldVessels);
            sim.Step(default);
            Assert.Equal(RunEnd.InProgress, flight.Runs.Single(r => r.Number == 1).End);
        }
        using (var sim = new Simulation(Mission.ReachOrbit, world: WithRuns(count, others: 2)))
        {
            var flight = sim.Flight;
            var run = flight.Runs.Single(r => r.Number == 1);
            Assert.True(sim.Run(Climb, f => f.Ground > 2f, maxSeconds: 60), "the rocket should lift off");
            var oldest = flight.WorldVessels.Single(v => v.Id == 0);
            sim.Step(new FlightInput(Stage: true));
            Assert.DoesNotContain(oldest, flight.WorldVessels);
            sim.Step(default);
            Assert.Equal(RunEnd.Failed, run.End);
        }
    }

    [Fact]
    public void A_captured_world_comes_back_exactly_and_carries_on_the_same()
    {
        // A world with something in orbit, something falling, and a rocket on the pad.
        using var sim = new Simulation(Mission.ReachOrbit);
        FlyToOrbit(sim);
        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        var world = sim.Flight.Capture();
        Assert.Equal(3, world.Vessels.Count);

        using (var copy = new Simulation(Mission.LaunchAndLand, world: world))
        {
            // Bringing it back starts another flight, which replaces the old rocket on the pad.
            var flight = copy.Flight;
            Assert.Equal(0, flight.ClearedFromPad);
            Assert.Equal(world.Vessels.Count, flight.WorldVessels.Count); // the same, but a new rocket for the old
            Assert.Equal(world.Flights + 1, flight.Run!.Number);
            Assert.Equal(world.Time, flight.WorldTime);
            foreach (var state in world.Vessels.Where(s => s.Flight == 1))
            {
                var vessel = flight.WorldVessels.Single(v => v.Id == state.Id);
                Assert.Equal(state.Position, vessel.Position);
                Assert.Equal(state.Velocity, vessel.Velocity);
                Assert.Equal(state.Fuel, vessel.Capture(flight.WorldVessels).Fuel);
                Assert.Equal(state.State, vessel.State);
            }
            Assert.DoesNotContain(flight.WorldVessels, v => v.Id == flight.Controlled.Id && v != flight.Controlled);
        }

        // And carries on just as the original would have: each vessel 20 s on, in both.
        static Dictionary<int, Vector3> After(Simulation s)
        {
            s.Run(Coast, _ => false, maxSeconds: 20);
            return s.Flight.WorldVessels.Where(v => v.Flight == 1).ToDictionary(v => v.Id, v => v.Position);
        }
        using var original = new Simulation(Mission.LaunchAndLand, world: world);
        using var again = new Simulation(Mission.LaunchAndLand, world: world);
        var (a, b) = (After(original), After(again));
        foreach (var (id, position) in a)
        {
            output.WriteLine($"vessel {id}: {Vector3.Distance(position, b[id]):0.000} m apart after 20 s");
            Assert.True(Vector3.Distance(position, b[id]) < 0.5f);
        }
    }
}
