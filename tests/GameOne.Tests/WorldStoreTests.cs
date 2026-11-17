using System.Numerics;
using System.Reflection;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Saving and loading worlds (<see cref="WorldStore"/>), in a folder of the test's own: a world
/// comes back from its file exactly as it was saved, and flies on the same; the world list shows
/// what is saved (and copes with a file it can't read); worlds are made from presets, renamed,
/// reset and deleted.
/// </summary>
public sealed class WorldStoreTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));
    private WorldStore Store => new(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// A world worth saving: something in orbit, something falling back, a booster landed, and a
    /// rocket on the pad, with targets: the one in orbit has the landing zone, and the one on the
    /// pad has the one in orbit.
    /// </summary>
    private static WorldState Busy()
    {
        using var sim = new Simulation(Mission.LaunchAndLand);
        Assert.True(sim.RunToEnd(new Autopilot().Fly));
        sim.Step(new FlightInput(Choose: Mission.ReachOrbit));
        var orbit = new OrbitAutopilot();
        Assert.True(sim.Run(orbit.Fly, f => f.AchievedOrbit, maxSeconds: 300));
        var orbiter = sim.Flight.Controlled;
        sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, 1),
            Plan: NodeTests.Chain(orbiter, sim.Flight.WorldTime, sim.Flight.WorldTime + 400, new ManoeuvreNode(0, 5f, -1f, 2f), new ManoeuvreNode(500, -3f))));
        sim.Step(new FlightInput(Choose: Mission.DeploySatellite));
        sim.Step(new FlightInput(SetTarget: NavTarget.OfVessel(orbiter)));
        Assert.NotNull(sim.Flight.Target);
        return sim.Flight.Capture() with { Id = "busy", Name = "A busy world", Log = [new LogEntry(12.5, "Booster 2", "Hit the ground at 20.0 m/s", 20.5f, "Home", true), new LogEntry(90.25, "Sat-3 and Booster 2", "removed to make room", null, "412 km up", false)] };
    }

    [Fact]
    public void A_saved_world_loads_back_exactly_as_it_was()
    {
        var world = Busy();
        Assert.Equal(4, world.Vessels.Count);
        // And a lander landed on the moon, so its body is among the fields compared, with the
        // planet as its target; and the first vessel (at the planet) with the moon as its; and the
        // lander, its bay's doors half open, so they are among them too.
        var moon = NavTarget.OfBody(GroundBody.Moon);
        world = world with
        {
            Vessels = [world.Vessels[0] with { Target = moon }, .. world.Vessels.Skip(1), BayHalfOpen(world.NextVesselId),
                OnTheMoon.Lander(world.NextVesselId + 1, OnTheMoon.LandingSpot) with { Target = NavTarget.OfBody(GroundBody.Planet) }],
            NextVesselId = world.NextVesselId + 2,
        };
        Store.Save(world);
        var back = Store.Load("busy");
        Assert.Equal(GroundBody.Moon.Name, back.Vessels[^1].Body);
        Assert.Equal(moon, back.Vessels[0].Target);
        Assert.Equal(new NavTarget(TargetKind.Body, GroundBody.Planet.Index), back.Vessels[^1].Target);
        // Loaded, each still has its body targeted.
        using (var sim = new Simulation(Mission.FreePlay, world: back))
        {
            Assert.Equal(moon, sim.Flight.TargetOf(sim.Flight.WorldVessels.Single(v => v.Id == back.Vessels[0].Id)));
            Assert.Equal(NavTarget.OfBody(GroundBody.Planet), sim.Flight.TargetOf(sim.Flight.WorldVessels.Single(v => v.Id == back.Vessels[^1].Id)));
        }

        Assert.Equal(world.Name, back.Name);
        Assert.Equal(world.Preset, back.Preset);
        Assert.Equal(world.Time, back.Time);
        Assert.Equal(world.Flights, back.Flights);
        Assert.Equal(world.NextVesselId, back.NextVesselId);
        Assert.Equal(2, world.Log.Count);
        Assert.Equal(world.Log, back.Log); // (records: each field compared)
        AssertSameVessels(world, back);
        Assert.False(File.Exists(Path.Combine(_directory, "busy.json.saving")), "the save should leave no half-written file");
    }

    [Fact]
    public void A_point_target_round_trips()
    {
        var lander = OnTheMoon.Lander(0, OnTheMoon.LandingSpot) with { Target = NavTarget.OfPoint(1) };
        Store.Save(new WorldState { Id = "points", Name = "Points", Vessels = [lander], NextVesselId = 1, Flights = 1 });
        Assert.Equal(new NavTarget(TargetKind.Point, 1), Store.Load("points").Vessels.Single().Target);
        Assert.Contains("\"Point\"", File.ReadAllText(Path.Combine(_directory, "points.json"))); // by name, as the other kinds are
    }

    /// <summary>
    /// The lander launcher's lander, numbered <paramref name="id"/>, standing on the moon by the
    /// landing spot with its bay's doors half open and opening, one of them stopped short on the ground.
    /// </summary>
    private static VesselState BayHalfOpen(int id)
    {
        var vehicle = Vehicle.LanderLauncher;
        const int Lander = 2;
        // 20 m off the spot, clear of the upper stage standing on it.
        var (position, rotation) = OnTheMoon.Standing(Vector3.Normalize(OnTheMoon.LandingSpot + new Vector3(0f, 0f, 20f / GroundBody.Moon.SeaRadius)), vehicle.Stages[Lander].LegGeometry);
        return new VesselState
        {
            Id = id, Flight = 1, Mission = Mission.FreePlay, Vehicle = vehicle, First = Lander, Last = Lander,
            Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * 0.5f)],
            State = FlightState.Landed, Position = position, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, Lander, Lander),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero,
            Aim = Vector3.Transform(Vector3.UnitY, rotation), LegsWanted = true, LegDeploy = 1f, HasBeenAirborne = true, TouchedDown = true,
            BayDoorsWanted = true, BayDoors = 0.5f, BayDoorStops = new Vector2(RocketGeometry.DoorOpenRadians, 2.2f),
            Body = GroundBody.Moon.Name,
            Site = "Highcrest", // launched from another site than Home, so the site is among the fields compared
        };
    }

    [Fact]
    public void A_view_centred_elsewhere_round_trips()
    {
        var world = Busy();
        var view = new CameraView(true, 1f, 0.3f, 60f, 2f, 0.9f, 90_000f, true, new CameraCentre(CentreKind.Vessel, 7));
        world = world with { Vessels = [world.Vessels[0] with { View = view }, .. world.Vessels.Skip(1)] };
        Store.Save(world);
        var back = Store.Load("busy");
        Assert.Equal(view, back.Vessels[0].View);
        AssertSameVessels(world, back);
    }

    [Fact]
    public void Runs_and_the_flown_vessel_round_trip()
    {
        var run = new RunState
        {
            Mission = Mission.LaunchAndLand, Site = "Highcrest", Number = 2,
            Done = [true, false, true], Points = [0, 0, 1250], LastFlown = 4, BegunAt = 123.5,
        };
        var world = new WorldState { Id = "runs", Name = "Runs", Flights = 2, NextVesselId = 5, Runs = [run], Flown = 4 };
        Store.Save(world);
        var back = Store.Load("runs");
        Assert.Equal(4, back.Flown);
        var loaded = Assert.Single(back.Runs);
        foreach (var property in typeof(RunState).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var (saved, got) = (property.GetValue(run), property.GetValue(loaded));
            if (saved is bool[] done)
                Assert.Equal(done, (bool[])got!);
            else if (saved is int[] points)
                Assert.Equal(points, (int[])got!);
            else
                Assert.True(Equals(saved, got), $"the run's {property.Name}: saved {saved}, loaded {got}");
        }
    }

    [Fact]
    public void A_chain_round_trips()
    {
        // A vessel round the moon with a chain anchored there, and one with none.
        var chain = new ManoeuvreChain(new ChainAnchor(GroundBody.Moon.Name, 123.25, new Vector3(1.5f, -2f, 3f), new Vector3(400f, 5f, -6f)),
            [new ManoeuvreNode(0, 5f, -1f, 2f), new ManoeuvreNode(500.5, Normal: -3f)]);
        WorldState world;
        using (var sim = new Simulation(Mission.FreePlay, world: new WorldState
        {
            Time = 100.0, NextVesselId = 2, Flights = 1, Vessels = [InOrbit.Round(GroundBody.Moon, 0, 400f), InOrbit.Round(GroundBody.Moon, 1, 900f)],
        }))
            world = sim.Flight.Capture() with { Id = "chain", Name = "A chain" };
        world = world with { Vessels = [world.Vessels[0] with { Chain = chain }, .. world.Vessels.Skip(1)] };
        Store.Save(world);
        var back = Store.Load("chain");
        Assert.Equal(chain, back.Vessels[0].Chain);
        Assert.Equal(chain.TimeOf(1), back.Vessels[0].Chain!.TimeOf(1));
        Assert.Null(back.Vessels[1].Chain);
    }

    /// <summary>Every field of every vessel in <paramref name="back"/> is as it was in <paramref name="world"/>.</summary>
    private static void AssertSameVessels(WorldState world, WorldState back)
    {
        Assert.Equal(world.Vessels.Count, back.Vessels.Count);
        var properties = typeof(VesselState).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Contains(properties, p => p.Name == nameof(VesselState.CentreOfMassOffset)); // saved, so compared
        for (var i = 0; i < world.Vessels.Count; i++)
            foreach (var property in properties)
            {
                var (saved, loaded) = (property.GetValue(world.Vessels[i]), property.GetValue(back.Vessels[i]));
                if (saved is float[] fuel)
                    Assert.Equal(fuel, (float[])loaded!);
                else
                    Assert.True(Equals(saved, loaded), $"vessel {world.Vessels[i].Id}'s {property.Name}: saved {saved}, loaded {loaded}");
            }
    }

    [Fact]
    public void A_floating_vessel_saves_and_comes_back_floating()
    {
        // A booster splashed down in deep water, saved as it floats.
        var at = Planet.Centre + WaterTests.DeepWater.Value * Planet.SeaRadius;
        WorldState world;
        using (var sim = Drops.At(at, Planet.East(at), 4f))
        {
            var floater = sim.Flight.WorldVessels.First(v => v.Id == 0);
            Assert.True(sim.Run((_, _) => default, _ => floater.State != FlightState.Flying, maxSeconds: 90));
            Assert.Equal(FlightState.Floating, floater.State);
            world = sim.Flight.Capture() with { Id = "afloat", Name = "Afloat" };
        }
        Store.Save(world);
        var back = Store.Load("afloat");
        AssertSameVessels(world, back);

        using var again = new Simulation(Mission.LaunchAndLand, world: back);
        var vessel = again.Flight.WorldVessels.Single(v => v.Id == 0);
        Assert.Equal(FlightState.Floating, vessel.State);
        again.Run((_, _) => default, _ => false, maxSeconds: 10);
        var above = Planet.Altitude(vessel.CentreOfMass);
        output.WriteLine($"10 s after it was loaded, {Vector3.Distance(vessel.Position, again.Flight.Controlled.Position):0} m from the vessel being flown: {vessel.State}, its centre of mass {above:+0.00;-0.00} m about sea level");
        Assert.Equal(FlightState.Floating, vessel.State);
        Assert.False(vessel.Craft.Frozen, "it was loaded frozen, and never woke with the vessel being flown near it");
        Assert.True(MathF.Abs(above) < 10f, $"it is {above:0.0} m from the surface");
    }

    [Fact]
    public void A_vessel_saved_while_it_settles_in_the_water_comes_back_settling_and_floats_with_nobody_near()
    {
        // Splashed down far from the launch pad (in the Austral Sea) and saved a few seconds in,
        // before it has settled: loaded again, with only the new rocket on the pad being flown,
        // kilometres off, it is still coming down in the water, awake, and goes on to float.
        var (at, _) = SplashdownTests.RoughWater.Value;
        WorldState world;
        using (var sim = Drops.At(at, Planet.East(at), 6f))
        {
            var settling = sim.Flight.WorldVessels.First(v => v.Id == 0);
            sim.Run((_, _) => default, _ => false, maxSeconds: 6);
            Assert.Equal(FlightState.Flying, settling.State);
            Assert.True(settling.Craft.Submerged > 0f);
            world = sim.Flight.Capture() with { Id = "settling", Name = "Settling" };
        }
        Store.Save(world);
        var back = Store.Load("settling");
        AssertSameVessels(world, back);

        using var again = new Simulation(Mission.LaunchAndLand, world: back);
        var vessel = again.Flight.WorldVessels.Single(v => v.Id == 0);
        Assert.Equal(FlightState.Flying, vessel.State);
        Assert.False(vessel.Craft.Frozen, "it came back frozen");
        var away =Vector3.Distance(vessel.Position, again.Flight.Controlled.Position);
        Assert.True(away > Scene.FloatFreezeDistance, $"it is only {away:0} m from the vessel being flown");
        var froze = false;
        var floated = again.Run((_, _) => default, _ =>
        {
            froze |= vessel.State == FlightState.Flying && vessel.Craft.Frozen;
            return vessel.State != FlightState.Flying;
        }, maxSeconds: 120);
        output.WriteLine($"{away:0} m from the vessel being flown: {vessel.State} after {again.Time:0.0} s, leaning {vessel.TiltDegrees:0}°; frozen while settling: {froze}");
        Assert.False(froze, "it was frozen before it had settled");
        Assert.True(floated);
        Assert.Equal(FlightState.Floating, vessel.State);
    }

    [Fact]
    public void A_loaded_world_flies_on_just_as_the_one_that_was_saved()
    {
        var world = Busy();
        Store.Save(world);
        var back = Store.Load("busy");

        static Dictionary<int, Vector3> TwentySecondsOn(WorldState world)
        {
            using var sim = new Simulation(Mission.LaunchAndLand, world: world);
            sim.Run((_, _) => default, _ => false, maxSeconds: 20);
            return sim.Flight.WorldVessels.Where(v => v.Flight < world.Flights + 1).ToDictionary(v => v.Id, v => v.Position);
        }
        var (a, b) = (TwentySecondsOn(world), TwentySecondsOn(back));
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        foreach (var (id, position) in a)
        {
            output.WriteLine($"vessel {id}: {Vector3.Distance(position, b[id]):0.000} m apart");
            Assert.True(Vector3.Distance(position, b[id]) < 0.01f);
        }
    }

    [Fact]
    public void The_world_list_shows_what_is_saved_the_latest_first_and_copes_with_a_file_it_can_t_read()
    {
        var store = Store;
        Assert.Empty(store.List()); // no folder yet: no worlds
        var empty = store.Create(WorldPresets.EmptyId);
        Thread.Sleep(20);
        store.Save(Busy());
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ not a world");

        var worlds = store.List();
        foreach (var w in worlds)
            output.WriteLine($"{w.Name}: {w.Contents} {w.Error}");
        Assert.Equal(3, worlds.Count);
        var busy = worlds.Single(w => w.Id == "busy");
        Assert.Equal("A busy world", busy.Name);
        Assert.Contains("1 upper stage in orbit", busy.Contents);
        Assert.Contains("1 booster landed", busy.Contents);
        Assert.Contains("1 booster flying", busy.Contents); // Reach Orbit's, falling back
        Assert.Contains("1 satellite launcher on the pad", busy.Contents);
        Assert.True(worlds.ToList().IndexOf(busy) < worlds.ToList().FindIndex(w => w.Id == empty.Id), "the latest played should come first");
        Assert.Equal("nothing yet", worlds.Single(w => w.Id == empty.Id).Contents);
        Assert.NotNull(worlds.Single(w => w.Id == "broken").Error);
    }

    [Fact]
    public void A_world_flown_in_free_play_saves_and_loads()
    {
        WorldState world;
        using (var sim = new Simulation(Mission.FreePlay))
        {
            Assert.True(sim.Run((f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default, f => f.Ground > 200f, maxSeconds: 60));
            sim.Step(new FlightInput(Stage: true));
            world = sim.Flight.Capture() with { Id = "free", Name = "Free play" };
        }
        Assert.Equal(2, world.Vessels.Count);
        Store.Save(world);
        var back = Store.Load("free");
        Assert.All(back.Vessels, v => Assert.Same(Mission.FreePlay, v.Mission));
        Assert.Equal(world.Vessels.Select(v => (v.Id, v.Vehicle)), back.Vessels.Select(v => (v.Id, v.Vehicle)));
        using var again = new Simulation(Mission.FreePlay, world: back);
        var ids = back.Vessels.Select(v => v.Id).ToHashSet();
        Assert.Equal(2, again.Flight.WorldVessels.Count(v => ids.Contains(v.Id) && v.Mission == Mission.FreePlay));
    }

    [Theory]
    [InlineData(0, false)] // the booster dropped
    [InlineData(1, true)]  // the fairing jettisoned off the upper stage and satellite
    public void A_world_saved_just_after_staging_comes_back_with_the_parts_still_parting(int first, bool fairingOn)
    {
        // The satellite launcher (from stage first up) high over the landing zone, staged and saved
        // a frame later, while the parts are still inside each other: loaded, each is still parting
        // from the same one (touching only what doesn't move), so they come apart as they would have
        // if it had never been saved, not pushed by each other.
        var zone = Scene.LandingZoneCentre;
        var state = Drops.Placed(zone, Planet.East(zone), 0f, Vehicle.ThreeStage, first: first, clearance: 1000f, mission: Mission.DeploySatellite, fairingOn: fairingOn);
        WorldState world;
        Dictionary<int, int?> parting;
        Dictionary<int, Vector3> unsaved;
        static int? IdOf(Flight flight, Craft? craft) => craft is null ? null : flight.WorldVessels.First(v => v.Craft == craft).Id;
        using (var sim = new Simulation(Mission.DeploySatellite, world: new WorldState { Vessels = [state], NextVesselId = 1 }))
        {
            sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0)));
            sim.Step(new FlightInput(CutThrottle: true, Stage: true));
            var parts = sim.Flight.WorldVessels.Where(v => v.State != FlightState.Ready).ToArray();
            parting = parts.ToDictionary(v => v.Id, v => IdOf(sim.Flight, v.Craft.PartingFrom));
            Assert.Equal(2, parts.Count(p => p.Craft.PartingFrom is not null)); // still inside each other (a payload whose fairing came off parts from neither half: they each part from it)
            world = sim.Flight.Capture() with { Id = "staged", Name = "Staged" };
            sim.Run((_, _) => default, _ => false, maxSeconds: 0.5);
            unsaved = parts.ToDictionary(v => v.Id, v => v.Velocity);
        }
        Store.Save(world);
        var back = Store.Load("staged");
        AssertSameVessels(world, back);

        using var again = new Simulation(Mission.DeploySatellite, world: back);
        var loaded = again.Flight.WorldVessels.Where(v => parting.ContainsKey(v.Id)).ToArray();
        Assert.Equal(parting.Count, loaded.Length);
        foreach (var v in loaded)
        {
            output.WriteLine($"vessel {v.Id} ({v.Name}): parting from {parting[v.Id]} when saved, from {IdOf(again.Flight, v.Craft.PartingFrom)?.ToString() ?? "nothing"} loaded");
            Assert.Equal(parting[v.Id], IdOf(again.Flight, v.Craft.PartingFrom));
        }
        again.Run((_, _) => default, _ => false, maxSeconds: 0.5);
        foreach (var v in loaded)
        {
            var off = (v.Velocity - unsaved[v.Id]).Length();
            output.WriteLine($"vessel {v.Id} ({v.Name}): 0.5 s on, {off:0.0000} m/s off the run that wasn't saved; {v.State} {v.CrashReason}");
            Assert.NotEqual(FlightState.Crashed, v.State);
            Assert.True(off < 0.01f, $"vessel {v.Id} was pushed {off:0.000} m/s off how it would have flown");
        }
    }

    [Fact]
    public void A_world_saved_by_a_newer_version_of_the_game_is_refused()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "future.json"), $$"""{ "Version": {{WorldStore.FormatVersion + 1}}, "World": { "Name": "From the future" } }""");
        Assert.Throws<InvalidDataException>(() => Store.Load("future"));
        Assert.NotNull(Store.List().Single().Error);
    }

    [Fact]
    public void A_vessel_saved_in_orbit_loads_back_on_the_same_orbit()
    {
        // A satellite a few seconds into its orbit, saved and loaded back: its orbit, read from the
        // centre of mass saved with it (and from the vessel brought back), is the one it was on.
        WorldState world;
        float apoapsis, periapsis;
        using (var sim = new Simulation(Mission.LaunchAndLand, world: WorldPresets.Satellites()))
        {
            var satellite = sim.Flight.WorldVessels.Single(v => v.Id == 0);
            sim.Run((_, _) => default, _ => false, maxSeconds: 5);
            (apoapsis, periapsis) = (satellite.Apoapsis, satellite.Periapsis);
            world = sim.Flight.Capture() with { Id = "orbit", Name = "In orbit" };
        }
        Store.Save(world);
        var back = Store.Load("orbit");
        AssertSameVessels(world, back);

        var saved = back.Vessels.Single(v => v.Id == 0);
        var (ap, pe) = Planet.Orbit(saved.CentreOfMass, Planet.AmongStars(saved.CentreOfMass, saved.Velocity));
        using var again = new Simulation(Mission.LaunchAndLand, world: back);
        var vessel = again.Flight.WorldVessels.Single(v => v.Id == 0);
        output.WriteLine($"saved Ap {apoapsis:0.000} Pe {periapsis:0.000}; from the file Ap {ap:0.000} Pe {pe:0.000}; loaded Ap {vessel.Apoapsis:0.000} Pe {vessel.Periapsis:0.000}");
        Assert.InRange(MathF.Abs(ap - apoapsis), 0f, 0.05f);
        Assert.InRange(MathF.Abs(pe - periapsis), 0f, 0.05f);
        Assert.InRange(MathF.Abs(vessel.Apoapsis - apoapsis), 0f, 0.05f);
        Assert.InRange(MathF.Abs(vessel.Periapsis - periapsis), 0f, 0.05f);
    }

    [Fact]
    public void A_world_saved_with_vessels_under_formula_loads_back_as_it_was()
    {
        // Two satellites out of the physics, coasting by formula a while, saved: each is saved where
        // the formula has it (its body is gone), and loads back there, in the physics.
        WorldState world;
        var formula = new Dictionary<int, (Vector3 At, Quaternion Rotation)>();
        using (var sim = new Simulation(Mission.LaunchAndLand, world: WorldPresets.Satellites()))
        {
            var flight = sim.Flight;
            var left = flight.WorldVessels.Where(v => v.Id is 0 or 2).ToArray();
            foreach (var v in left)
                v.LeavePhysics(flight.WorldTime);
            var last = flight.WorldTime;
            sim.Run((_, _) => default, f =>
            {
                foreach (var v in left)
                    Formula.Advance(v, (float)(f.WorldTime - last), f.WorldTime);
                last = f.WorldTime;
                return false;
            }, maxSeconds: 30);
            Assert.All(left, v => Assert.True(v.OnFormula));
            world = flight.Capture() with { Id = "formula", Name = "Under formula" };
            foreach (var v in left)
                formula[v.Id] = (v.CentreOfMass, Quaternion.CreateFromRotationMatrix(v.Model));
        }
        foreach (var (id, (at, rotation)) in formula)
        {
            var saved = world.Vessels.Single(v => v.Id == id);
            Assert.InRange(Vector3.Distance(saved.CentreOfMass, at), 0f, 0.01f);
            Assert.InRange(MathF.Abs(Quaternion.Dot(saved.Rotation, rotation)), 0.99999f, 1.00001f);
        }
        Store.Save(world);
        var back = Store.Load("formula");
        AssertSameVessels(world, back);

        using var again = new Simulation(Mission.LaunchAndLand, world: back);
        foreach (var (id, (at, _)) in formula)
        {
            // Loaded into the physics, or (far from the new rocket's pad, nothing to hold it there)
            // straight under formula: where it was, either way, and its body there once brought in.
            var vessel = again.Flight.WorldVessels.Single(v => v.Id == id);
            Assert.InRange(Vector3.Distance(vessel.CentreOfMass, at), 0f, 0.01f);
            again.Flight.BringIn(vessel);
            Assert.False(vessel.OnFormula);
            Assert.InRange(Vector3.Distance(vessel.CentreOfMass, at), 0f, 0.01f);
        }
    }

    [Fact]
    public void The_centre_of_mass_a_vessel_saves_is_where_its_body_has_it()
    {
        // Measured from the physics as it is captured: for whole rockets, stages and a fairing half
        // alike, the one the body is given (RocketGeometry), whatever it was loaded with.
        var busy = Busy();
        var half = WorldPresets.Satellites().Vessels[0] with
        {
            Id = 9, Vehicle = Vehicle.FairingHalf, First = 0, Last = 0, Fuel = [0f], CentreOfMassOffset = Vector3.Zero,
            Rotation = Quaternion.Concatenate(WorldPresets.Satellites().Vessels[0].Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f)),
        };
        using var sim = new Simulation(Mission.LaunchAndLand, world: busy with { Vessels = [.. busy.Vessels, half], NextVesselId = 10 });
        var captured = sim.Flight.Capture();
        Assert.Contains(captured.Vessels, v => v.Vehicle == Vehicle.FairingHalf);
        Assert.Contains(captured.Vessels, v => v.First == 0 && v.Last == 0 && v.Vehicle != Vehicle.FairingHalf); // a stage on its own
        foreach (var v in captured.Vessels)
        {
            var given = RocketGeometry.CentreOfMassOffset(v.Vehicle, v.First, v.Last);
            // With its legs out its shape has feet, which the physics leaves out of the centre of
            // mass's offset on purpose (RocketPhysics.CreateShape): 0.058 m lower for a booster, a
            // small aid to standing that the steep pads' stance is tuned with. Hence the 0.07 m.
            var legsOut = sim.Flight.WorldVessels.Single(w => w.Id == v.Id).Craft.LegsDeployed;
            var apart = Vector3.Distance(v.CentreOfMassOffset, given);
            output.WriteLine($"vessel {v.Id} ({v.Vehicle.Name}, stages {v.First} to {v.Last}{(legsOut ? ", legs out" : "")}): offset {v.CentreOfMassOffset}, given {given}, {apart:0.0000} m apart");
            Assert.True(apart < (legsOut ? 0.07f : 0.01f), $"vessel {v.Id}'s centre of mass is {apart:0.000} m from its body's");
        }
    }

    [Fact]
    public void A_vessel_s_saved_centre_of_mass_moves_and_turns_with_it()
    {
        // Kept from its origin in its own space, so a copy put somewhere else (as the tests and the
        // busy worlds make them) has its centre of mass where its body is, never left behind.
        var satellite = WorldPresets.Satellites().Vessels[0];
        var height = RocketGeometry.CentreOfMassHeight(satellite.Vehicle, satellite.First, satellite.Last);
        var from = satellite.CentreOfMass - satellite.Position;
        Assert.True(MathF.Abs(from.Length() - height) < 0.001f);

        var moved = satellite with { Position = satellite.Position + new Vector3(100f, -20f, 5f) };
        Assert.True(Vector3.Distance(moved.CentreOfMass - moved.Position, from) < 0.001f, "moved, it should keep its centre of mass where it was on it");

        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
        var turned = moved with { Rotation = Quaternion.Concatenate(moved.Rotation, turn) };
        Assert.True(Vector3.Distance(turned.CentreOfMass - turned.Position, Vector3.Transform(from, turn)) < 0.001f, "turned, its centre of mass should turn with it");
    }

    [Fact]
    public void The_world_list_calls_an_orbit_just_clear_of_the_air_in_orbit()
    {
        // Its centre of mass on a circle just above the air: its body's origin is below that, where
        // the velocity is too slow for a circle, so read from there the orbit would dip into the air.
        var satellite = WorldPresets.Satellites().Vessels[0];
        var (position, centre, velocity) = WorldPresets.CircularOrbit(Scene.AtmosphereHeight + 0.1f, 0f, 1f,
            RocketGeometry.CentreOfMassHeight(satellite.Vehicle, satellite.First, satellite.Last));
        var orbiting = satellite with { Position = position, Rotation = Planet.UprightAt(position), Velocity = velocity };
        Assert.True(Vector3.Distance(centre, orbiting.CentreOfMass) < 0.01f, $"its centre of mass is {Vector3.Distance(centre, orbiting.CentreOfMass):0.000} m off the circle");
        var (fromCentre, fromOrigin) = (Planet.Orbit(centre, Planet.AmongStars(centre, velocity)).Periapsis, Planet.Orbit(position, Planet.AmongStars(position, velocity)).Periapsis);
        output.WriteLine($"Pe {fromCentre - Scene.AtmosphereHeight:+0.000;-0.000} m above the air from the centre of mass, {fromOrigin - Scene.AtmosphereHeight:+0.000;-0.000} m from the origin");
        // The case is one that tells the two apart: read from the origin, it would say "flying".
        Assert.True(fromCentre > Scene.AtmosphereHeight, "from the centre of mass the orbit should clear the air");
        Assert.True(fromOrigin <= Scene.AtmosphereHeight, "from the origin the orbit should dip into the air, or the test can't tell the two readings apart");
        Assert.Equal("1 satellite in orbit", WorldStore.Describe(WorldPresets.Empty() with { Vessels = [orbiting] }));
    }

    [Fact]
    public void Worlds_are_made_from_presets_and_can_be_renamed_reset_and_deleted()
    {
        var store = Store;
        var first = store.Create(WorldPresets.EmptyId);
        var second = store.Create(WorldPresets.EmptyId);
        Assert.Equal("Empty world", first.Name);
        Assert.Equal("Empty world 2", second.Name); // a name of its own
        Assert.NotEqual(first.Id, second.Id);

        store.Rename(first.Id, "My world");
        Assert.Equal("My world", store.Load(first.Id).Name);

        // Fly something into it, then reset it: back to the preset, keeping its name.
        using (var sim = new Simulation(Mission.LaunchAndLand, world: store.Load(first.Id)))
        {
            sim.RunToEnd(new Autopilot().Fly);
            store.Save(sim.Flight.Capture());
        }
        Assert.NotEmpty(store.Load(first.Id).Vessels);
        store.Reset(first.Id);
        var reset = store.Load(first.Id);
        Assert.Empty(reset.Vessels);
        Assert.Equal("My world", reset.Name);
        Assert.Equal(0, reset.Flights);

        store.Delete(second.Id);
        Assert.Equal([first.Id], store.List().Select(w => w.Id));
    }
}
