using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A vessel out of the physics (<see cref="Vessel.LeavePhysics"/>), moved by formula
/// (<see cref="Formula.Advance"/>): coasting on its orbit by Kepler, back into the physics where
/// the formula had it with no jump (<see cref="Vessel.ReturnToPhysics"/>), still drawn, and
/// taking no body meanwhile. The tests take it out and bring it back themselves, and advance it
/// after each frame as the flight's step will.
/// </summary>
public sealed class FormulaTests(ITestOutputHelper output)
{
    /// <summary>
    /// The satellites preset's first, 9,000 m up, the flown vessel a launch away on the pad; every
    /// vessel in the physics to begin with (those far from the pad load under formula: see
    /// Unwatched.MayLoadOnFormula), as these send it out themselves.
    /// </summary>
    private static (Simulation Sim, Vessel Satellite) World()
    {
        var sim = new Simulation(world: WorldPresets.Satellites());
        foreach (var v in sim.Flight.WorldVessels)
            sim.Flight.BringIn(v);
        return (sim, sim.Flight.WorldVessels.Single(v => v.Id == 0));
    }

    /// <summary>
    /// <paramref name="seconds"/> of game time, the vessel under formula advanced after each frame.
    /// The others are watched, so it is the only one out of the physics (nobody watching them,
    /// the flight would send them out too: see <see cref="Unwatched"/>).
    /// </summary>
    private static void Coast(Simulation sim, Vessel v, double seconds)
    {
        var last = sim.Flight.WorldTime;
        var others = Others(sim, v);
        sim.Run((_, _) => new FlightInput(Watched: others), f =>
        {
            Formula.Advance(v, (float)(f.WorldTime - last), f.WorldTime);
            last = f.WorldTime;
            return false;
        }, seconds);
    }

    /// <summary>Every vessel in the world but <paramref name="v"/>.</summary>
    private static Vessel[] Others(Simulation sim, Vessel v) => [.. sim.Flight.WorldVessels.Where(o => o != v)];

    /// <summary>Where the orbit from <paramref name="at"/> (moving <paramref name="starsVelocity"/> among the stars) has it <paramref name="since"/> seconds on, back in the ground's frame then.</summary>
    private static (Vector3 At, Vector3 Velocity) Predicted(Vector3 at, Vector3 starsVelocity, double since)
    {
        var (position, velocity) = new Orbit(GroundBody.Planet, at, starsVelocity).StateAfter(since);
        var there = Planet.Turned(position, -since);
        return (there, Planet.OverGround(there, Planet.TurnedDirection(velocity, -since)));
    }

    // The flight's step costs about 0.35 ms on its own (the rocket on the pad, its predictions), so
    // 600 s of it is some 15 s, over the limit: the long coasts below are long tests, run before a
    // merge, and these quicker ones check the same rules in the normal suite, leaving out only
    // the length of the coast (a good share of a lap, over which a mistake in the frames' turn
    // would grow).

    [Fact]
    public void A_vessel_out_of_the_physics_coasts_on_its_orbit() => CoastsOnItsOrbit(60);

    [LongFact]
    [Trait("Run", "Long")]
    public void A_vessel_out_of_the_physics_coasts_on_its_orbit_for_600_s() => CoastsOnItsOrbit(600);

    [Fact]
    public void A_vessel_comes_back_into_the_physics_where_the_formula_had_it() => ComesBack(30);

    [LongFact]
    [Trait("Run", "Long")]
    public void A_vessel_comes_back_into_the_physics_where_the_formula_had_it_after_300_s() => ComesBack(300);

    private void CoastsOnItsOrbit(double seconds)
    {
        var (sim, v) = World();
        using var _ = sim;
        var flight = sim.Flight;
        var (at, starsVelocity, start) = (v.CentreOfMass, Planet.AmongStars(v.CentreOfMass, v.Velocity), flight.WorldTime);
        v.LeavePhysics(flight.WorldTime);
        Coast(sim, v, seconds);

        Assert.True(v.OnFormula);
        var since = flight.WorldTime - start;
        var (expected, expectedVelocity) = Predicted(at, starsVelocity, since);
        var (off, offSpeed) = (Vector3.Distance(v.CentreOfMass, expected), Vector3.Distance(v.Velocity, expectedVelocity));
        output.WriteLine($"{since:0.0} s on, {Vector3.Distance(at, v.CentreOfMass):0} m from where it left: {off:0.0000} m and {offSpeed:0.00000} m/s off the orbit");
        Assert.True(Vector3.Distance(at, v.CentreOfMass) > 1000f, "it should have gone round its orbit");
        Assert.InRange(off, 0f, 0.01f);
        Assert.InRange(offSpeed, 0f, 0.001f);
        Assert.InRange(MathF.Abs(Planet.Altitude(v.CentreOfMass) - 9000f), 0f, 1f); // its circle, 9 km up
    }

    private void ComesBack(double coastSeconds)
    {
        var (sim, v) = World();
        using var _ = sim;
        var flight = sim.Flight;
        var (at, starsVelocity, start) = (v.CentreOfMass, Planet.AmongStars(v.CentreOfMass, v.Velocity), flight.WorldTime);
        v.LeavePhysics(flight.WorldTime);
        Coast(sim, v, coastSeconds);
        var (was, wasMoving, wasTurned) = (v.CentreOfMass, v.Velocity, Quaternion.CreateFromRotationMatrix(v.Model));

        v.ReturnToPhysics(flight.WorldTime);
        Assert.False(v.OnFormula);
        Assert.False(v.Craft.Removed);
        Assert.InRange(MathF.Abs(Quaternion.Dot(sim.Physics.Rotation(v.Craft), wasTurned)), 0.99999f, 1.00001f); // facing as it was
        var (jump, kick) = (Vector3.Distance(v.CentreOfMass, was), Vector3.Distance(v.Velocity, wasMoving));
        output.WriteLine($"back in the physics {jump:0.0000} m and {kick:0.00000} m/s from where the formula had it");
        Assert.InRange(jump, 0f, 0.01f);
        Assert.InRange(kick, 0f, 0.01f);

        // Flown on by Jolt, it keeps to the same orbit, in gross agreement: watched, so the flight
        // keeps it in the physics.
        var watched = new[] { v };
        sim.Run((_, _) => new FlightInput(Watched: watched), _ =>
        {
            Assert.False(v.OnFormula, "it left the physics again though watched");
            return false;
        }, maxSeconds: 60);
        var (expected, _) = Predicted(at, starsVelocity, flight.WorldTime - start);
        var off = Vector3.Distance(v.CentreOfMass, expected);
        output.WriteLine($"60 s on in the physics, {off:0.000} m off the orbit's prediction");
        Assert.InRange(off, 0f, 1f);
    }

    [Fact]
    public void A_vessel_out_of_the_physics_is_still_drawn()
    {
        var (sim, v) = World();
        using var _ = sim;
        var parts = new List<StagePart>();
        sim.Physics.GetParts(parts);
        var inPhysics = parts.Count;
        v.LeavePhysics(sim.Flight.WorldTime);
        Coast(sim, v, 5);

        sim.Physics.GetParts(parts);
        Assert.Equal(inPhysics - (v.TopStage - v.Stage + 1), parts.Count); // its body is gone from the physics' drawing
        v.FormulaParts(parts);
        Assert.Equal(inPhysics, parts.Count); // and it is drawn from its own state: one part a stage
        Assert.Equal(v.CurrentStage, parts[^1].Stage);
        Assert.Equal(v.Model, parts[^1].Model);
    }

    [Fact]
    public void One_that_cant_be_steered_turns_smoothly_under_formula_between_its_steps()
    {
        // A fairing half (nothing steers it) tumbling at 1 rad/s 2 km up in the air, unwatched: the
        // formula steps it every 0.1 s (Scene.FormulaStepSeconds), six frames, but it is drawn
        // turning every frame by its spin, as its place is moved on, not a step's turn at once
        // and then still (it once was).
        var at = Level.Value.At;
        var up = Planet.Up(at);
        var placed = Drops.Placed(at, Planet.East(at), 0f, legsOut: false);
        var state = placed with
        {
            Vehicle = Vehicle.FairingHalf, First = 0, Last = 0, Fuel = [0f], LegDeploy = 0f, LegsWanted = false, FairingOn = false,
            Position = placed.Position + up * 2000f, Velocity = Planet.East(at) * 50f, AngularVelocity = Planet.North(at) * 1f,
        };
        var (sim, v, watch) = Start(state, watched: false);
        using var _ = sim;
        Assert.False(v.Guided);
        Fly(sim, v, watch, 0.5);
        Assert.True(v.OnFormula && v.State == FlightState.Flying);
        var turns = new List<float>();
        var (last, started) = (v.FormulaRotation, false);
        sim.Run((_, _) => default, _ =>
        {
            var now = v.FormulaRotation;
            if (started) // (Run asks once before its first frame: no turn yet)
                turns.Add(2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(last, now)), 0f, 1f)));
            (last, started) = (now, true);
            return false;
        }, maxSeconds: 1.0);
        output.WriteLine($"{turns.Count} frames: each turned {turns.Min():0.0000} to {turns.Max():0.0000} rad (spin now {v.FormulaSpin.Length():0.000} rad/s, slowed by the air's damping)");
        Assert.True(v.OnFormula, "it came back into the physics");
        Assert.True(turns.Count >= 50 && turns.Average() > 0.002f, "it barely turned: this shows nothing");
        // Each frame turned, and about as far as the frame before (its spin slows only gradually):
        // a step's turn at once would be six frames' worth, with none in the frames between.
        for (var i = 1; i < turns.Count; i++)
            Assert.True(turns[i] > 0.5f * turns[i - 1] && turns[i] < 2f * turns[i - 1], $"frame {i} turned {turns[i]:0.0000} rad, the one before {turns[i - 1]:0.0000}");
    }

    [Fact]
    public void A_vessel_out_of_the_physics_takes_no_body()
    {
        var (sim, v) = World();
        using var _ = sim;
        var bodies = sim.Physics.BodyCount;
        v.LeavePhysics(sim.Flight.WorldTime);
        Assert.Equal(bodies - 1, sim.Physics.BodyCount);
        v.ReturnToPhysics(sim.Flight.WorldTime);
        Assert.Equal(bodies, sim.Physics.BodyCount);
    }

    // The formula in the air and burning, and the touch. Each flies the same vessel the same way
    // twice: once watched (FlightInput.Watched, so in the physics, but not flown: it flies by its
    // own throttle and holds, as one under formula does), and once unwatched, a satellite some
    // 50 km off flown; and compares where each ends, or how. The flight launches from another site
    // where a test is at Home's, so nothing stands on its pad to keep what comes near in the physics.

    /// <summary>The flown one: a satellite 25 km up over the point opposite <paramref name="there"/>, some 50 km from it, nothing near.</summary>
    private static VesselState FarFrom(Vector3 there, int id) => PerfScenes.FarSatellite(there, id);

    /// <summary>
    /// A world of <paramref name="state"/> (vessel 0) and the far satellite, flown: <paramref name="watched"/>,
    /// it is in the physics from the first step; otherwise it leaves the physics at the end of it.
    /// </summary>
    internal static (Simulation Sim, Vessel Vessel, Vessel[]? Watch) Start(VesselState state, bool watched, LaunchSite? site = null)
    {
        var sim = new Simulation(world: new WorldState { Vessels = [state with { Id = 0 }, FarFrom(state.Position, 1)], NextVesselId = 2 }, site: site);
        var v = sim.Flight.WorldVessels.Single(x => x.Id == 0);
        Vessel[]? watch = watched ? [v] : null;
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.Single(x => x.Id == 1), Watched: watch));
        return (sim, v, watch);
    }

    /// <summary>
    /// Flies <paramref name="v"/> on until <paramref name="until"/> or <paramref name="seconds"/>,
    /// asserting it stays where it should be all the while: watched, in the physics; unwatched,
    /// under formula (wrecked or not).
    /// </summary>
    internal static void Fly(Simulation sim, Vessel v, Vessel[]? watch, double seconds, Func<Vessel, bool>? until = null)
    {
        sim.Run((_, _) => new FlightInput(Watched: watch), _ =>
        {
            if (watch is null)
                Assert.True(v.OnFormula, $"it came back into the physics ({v.State}) though nobody watches it");
            else
                Assert.False(v.OnFormula, "it left the physics though watched");
            return until?.Invoke(v) ?? false;
        }, seconds);
    }

    /// <summary><paramref name="state"/> turned to point its nose along <paramref name="direction"/> about its centre of mass, its aim with it.</summary>
    private static VesselState Pointed(VesselState state, Vector3 direction)
    {
        var comHeight = RocketGeometry.CentreOfMassHeight(state.Vehicle, state.First, state.Last);
        var com = state.Position + Vector3.Transform(Vector3.UnitY, state.Rotation) * comHeight;
        direction = Vector3.Normalize(direction);
        return state with { Rotation = UnwatchedTests.RotationTo(direction), Aim = direction, Position = com - direction * comHeight };
    }

    /// <summary>Over the deep sea, out of every site's way.</summary>
    private static Vector3 Sea => Planet.Centre + WaterTests.DeepWater.Value * Planet.SeaRadius;

    /// <summary>
    /// A spot on the calm sea where the water is <paramref name="deep"/> metres deep (within a
    /// quarter of a metre), and within half a metre of that everywhere within 10 m of it (where a
    /// booster's feet would come down), searched on a grid of latitudes and longitudes, the same
    /// place every run.
    /// </summary>
    private static Vector3 WaterOf(float deep)
    {
        static float Deep(Vector3 d) => -Terrain.Height(d);
        for (var lat = 0f; lat < 1.4f; lat = lat <= 0f ? 0.004f - lat : -lat) // 0, then out north and south by turns
            for (var lon = -MathF.PI; lon < MathF.PI; lon += 0.0004f)
            {
                var d = CubeSphere.FromLatLon(lat, lon);
                if (MathF.Abs(Deep(d) - deep) > 0.25f)
                    continue;
                var p = Planet.Centre + d * Planet.Radius;
                var even = true;
                for (var k = 0; k < 16 && even; k++)
                {
                    var (s, c) = MathF.SinCos(MathF.Tau * k / 16);
                    for (var r = 4f; r <= 10f && even; r += 3f)
                        even = MathF.Abs(Deep(Planet.Up(p + (Planet.East(p) * c + Planet.North(p) * s) * r)) - deep) <= 0.5f;
                }
                if (even && Rafts.Under(Planet.Centre + d * Planet.SeaRadius) is null)
                    return Planet.Centre + d * Planet.SeaRadius;
            }
        throw new InvalidOperationException($"no even water {deep} m deep");
    }

    // An upright booster's draft is its length over Scene.WaterBuoyancy: 9.2 m (see Formula's Judge).
    private static readonly Lazy<Vector3> SevenAndAHalfMetres = new(() => WaterOf(7.5f));
    private static readonly Lazy<Vector3> TenMetres = new(() => WaterOf(10f));
    // Past the formula's PlungeReach for an upright booster with its legs out (1.5 × its 9.2 m draft and its feet's hang).
    private static readonly Lazy<Vector3> EighteenMetres = new(() => WaterOf(18f));

    /// <summary>Level ground, out of every site's way (see <see cref="Drops.LevelGround"/>).</summary>
    private static readonly Lazy<(Vector3 At, Vector3 Downhill)> Level = new(() =>
    {
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        return (at, downhill);
    });

    /// <summary>
    /// One of the flights the formula is compared with Jolt on: the vessel, how long it flies
    /// (<paramref name="lap"/>: a whole lap of its orbit, for the coast), and how near the two must end.
    /// </summary>
    private static (VesselState State, double Seconds, float Within) Flown(string flight, bool lap = false)
    {
        var satellite = UnwatchedTests.Satellite(0, 9000f, 0f, 60f);
        var full = (float[])satellite.Fuel.Clone();
        full[satellite.First] = satellite.Vehicle.Stages[satellite.First].FuelMass;
        switch (flight)
        {
            case "coast in orbit":
            {
                var r = Planet.SeaRadius + 9000.0;
                var period = Math.Tau * Math.Sqrt(r * r * r / Planet.Mu);
                return (satellite, lap ? period : 60, 5f);
            }
            case "burn in orbit":
            {
                // Pointed prograde from the start, holding it, burning its whole tank (39 s).
                var comHeight = RocketGeometry.CentreOfMassHeight(satellite.Vehicle, satellite.First, satellite.Last);
                var com = satellite.Position + Vector3.Transform(Vector3.UnitY, satellite.Rotation) * comHeight;
                var prograde = Planet.AmongStars(com, satellite.Velocity);
                return (Pointed(satellite, prograde) with { Throttle = 1f, Hold = HoldMode.Prograde, Fuel = full }, 30, 20f);
            }
            case "fall through the air":
                // A booster, its engine off, falling 2.5 km over the sea, moving east at 40 m/s.
                return (Drops.Placed(Sea, Planet.East(Sea), 0f, sideways: Planet.East(Sea) * 40f, clearance: 2500f, legsOut: false), 15, 50f);
            case "burn through the air":
                // Falling at 60 m/s 2 km over the sea, its engine at a third, holding retrograde.
                return (Drops.Placed(Sea, Planet.East(Sea), 60f, sideways: Planet.East(Sea) * 20f, clearance: 2000f, legsOut: false)
                    with { Throttle = 0.35f, Hold = HoldMode.Retrograde }, 10, 50f);
            default:
                throw new ArgumentException(flight);
        }
    }

    /// <summary>Where <paramref name="state"/> ends, flown <paramref name="seconds"/> watched or not: its centre of mass and its attitude.</summary>
    private static (Vector3 At, Quaternion Rotation, Vector3 Up, Vessel Vessel) Ends(VesselState state, double seconds, bool watched)
    {
        var (sim, v, watch) = Start(state, watched);
        using var _ = sim;
        Fly(sim, v, watch, seconds);
        Assert.Equal(FlightState.Flying, v.State);
        return (v.CentreOfMass, Quaternion.CreateFromRotationMatrix(v.Model), v.Up, v);
    }

    /// <summary>The angle between two attitudes, in degrees.</summary>
    private static float Degrees(Quaternion a, Quaternion b) =>
        float.RadiansToDegrees(2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))))));

    [Theory]
    [InlineData("coast in orbit")]
    [InlineData("burn in orbit")]
    [InlineData("fall through the air")]
    [InlineData("burn through the air")]
    public void The_formula_ends_near_where_Jolt_does(string flight) => EndsNear(flight, lap: false);

    // The coast's bound is a lap's: the quick one above coasts a minute, this one the whole lap.
    [LongFact]
    [Trait("Run", "Long")]
    public void The_formula_coasts_a_lap_near_where_Jolt_does() => EndsNear("coast in orbit", lap: true);

    private void EndsNear(string flight, bool lap)
    {
        var (state, seconds, within) = Flown(flight, lap);
        var jolt = Ends(state, seconds, watched: true);
        var formula = Ends(state, seconds, watched: false);
        var gap = Vector3.Distance(jolt.At, formula.At);
        var moved = Vector3.Distance(jolt.At, state.Position);
        output.WriteLine($"{flight}, {seconds:0} s: {gap:0.00} m apart at the end (moved {moved:0} m); "
            + $"noses {float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(jolt.Up, formula.Up), -1f, 1f))):0.00}° apart, attitudes {Degrees(jolt.Rotation, formula.Rotation):0.00}°");
        Assert.InRange(gap, 0f, within);
    }

    [Theory]
    [InlineData("coast in orbit")]
    [InlineData("burn in orbit")]
    public void The_formula_turns_the_vessel_as_Jolt_does(string flight)
    {
        // Its attitude under formula follows its aim at once, and Jolt's turns its body to the aim:
        // a coast's keeps its attitude among the stars, a held burn's turns with its velocity.
        var (state, seconds, _) = Flown(flight);
        var jolt = Ends(state, seconds, watched: true);
        var formula = Ends(state, seconds, watched: false);
        var (noses, attitudes) = (float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(jolt.Up, formula.Up), -1f, 1f))), Degrees(jolt.Rotation, formula.Rotation));
        output.WriteLine($"{flight}, {seconds:0} s: noses {noses:0.000}° apart, attitudes {attitudes:0.000}°");
        Assert.InRange(noses, 0f, 1f);
        Assert.InRange(attitudes, 0f, 2f);
    }

    /// <summary>One of the touches the formula is compared with Jolt on.</summary>
    internal static VesselState Touching(string flight)
    {
        // Each comes down a few frames on, so it is under formula before it touches (the first
        // frame is the physics', before anything is handed out).
        var (ground, downhill) = Level.Value;
        return flight switch
        {
            "landing on its legs" => Drops.Placed(ground, downhill, 3f),
            "landing on its bell" => Drops.Placed(ground, downhill, 1.5f, legsOut: false, clearance: 0.15f),
            "too fast onto the ground" => Drops.Placed(ground, downhill, 12f, clearance: 3f),
            "a splashdown" => Drops.Placed(Sea, Planet.East(Sea), 5f, legsOut: false),
            "too fast into the sea" => Drops.Placed(Sea, Planet.East(Sea), 25f, legsOut: false, clearance: 5f),
            // Coming down on its engine at nine tenths of its weight, with fuel for some 4 s more:
            // it touches down burning, stands there, and lands once the tank is dry.
            "landing with its engine lit" => OnItsEngine(Drops.Placed(ground, downhill, 2f, fuel: 0.02f), 0.9f),
            // On its side, gently: its body's limit, and it lies where it came down.
            "coming down lying" => Drops.Placed(ground, downhill, 3f, leanDegrees: 90f, legsOut: false),
            // Into about 3 m of water (SplashdownTests.ShallowWater): gentle enough for the water,
            // but the seabed is ground there, and it comes down on it too hard.
            "too fast into shallow water" => Drops.Placed(SplashdownTests.ShallowWater.Value, Planet.East(SplashdownTests.ShallowWater.Value), 10f, clearance: 1f),
            // Lying down onto the same water, gently: it floats in it, clear of the seabed.
            "lying down onto shallow water" => Drops.Placed(SplashdownTests.ShallowWater.Value, Planet.East(SplashdownTests.ShallowWater.Value), 1f,
                leanDegrees: 90f, legsOut: false),
            // Upright on its legs, gently, into water shallower than it floats in (its plunge, slowed
            // by the water, brings it down on the seabed gently enough), and into water just deeper
            // than that (its plunge past where it floats touches the seabed, and it floats).
            "gently into water 7.5 m deep" => Drops.Placed(SevenAndAHalfMetres.Value, Planet.East(SevenAndAHalfMetres.Value), 2f),
            "gently into water 10 m deep" => Drops.Placed(TenMetres.Value, Planet.East(TenMetres.Value), 2f),
            "gently into water 18 m deep" => Drops.Placed(EighteenMetres.Value, Planet.East(EighteenMetres.Value), 2f),
            _ => throw new ArgumentException(flight),
        };
    }

    /// <summary><paramref name="state"/> with its engine set to push <paramref name="weights"/> times its weight where it is.</summary>
    private static VesselState OnItsEngine(VesselState state, float weights)
    {
        var stage = state.Vehicle.Stages[state.First];
        var mass = stage.DryMass + state.Fuel[state.First];
        var weight = (Planet.Gravity(state.Position) + Planet.OutwardPull(state.Position)).Length();
        return state with { Throttle = weights * weight * mass / stage.Thrust };
    }

    /// <summary>How <paramref name="state"/>'s flight ends, watched or not (it is given 90 s to settle).</summary>
    private static (FlightState State, string? Reason, float Touchdown, bool OnTarget, Vessel Vessel) Outcome(VesselState state, bool watched, LaunchSite? site = null)
    {
        var (sim, v, watch) = Start(state, watched, site);
        using var _ = sim;
        Fly(sim, v, watch, 90, x => x.State != FlightState.Flying);
        return (v.State, v.CrashReason, v.TouchdownSpeed, v.LandedOnTarget, v);
    }

    /// <summary>A crash reason's start, before its numbers ("Hit the ground", "Hit the water").</summary>
    private static string? Why(string? reason) => reason is null ? null : reason.Split(" at ")[0];

    [Theory]
    [InlineData("landing on its legs", "Landed")]
    [InlineData("landing on its bell", "Landed")]
    [InlineData("too fast onto the ground", "Crashed")]
    [InlineData("a splashdown", "Floating")]
    [InlineData("too fast into the sea", "Crashed")]
    [InlineData("landing with its engine lit", "Landed")]
    [InlineData("coming down lying", "Landed")]
    [InlineData("lying down onto shallow water", "Floating")]
    [InlineData("gently into water 18 m deep", "Floating")] // just past the plunge's reach: the formula's, under formula throughout
    public void The_formula_gives_the_same_outcome_at_the_touch_as_Jolt(string flight, string ending)
    {
        var outcome = Enum.Parse<FlightState>(ending); // (the game's own type, so not a test's parameter)
        var state = Touching(flight);
        var jolt = Outcome(state, watched: true);
        var formula = Outcome(state, watched: false);
        output.WriteLine($"{flight}: Jolt {jolt.State} at {jolt.Touchdown:0.00} m/s ({jolt.Reason}), {jolt.Vessel.TotalFuel:0.000} fuel left; "
            + $"the formula {formula.State} at {formula.Touchdown:0.00} m/s ({formula.Reason}), {formula.Vessel.TotalFuel:0.000} fuel left");
        Assert.Equal(outcome, jolt.State);
        Assert.Equal(outcome, formula.State);
        Assert.Equal(Why(jolt.Reason), Why(formula.Reason));
        if (state.Throttle > 0f) // landed only once its engine was out: its tank dry
            Assert.All([jolt.Vessel, formula.Vessel], v => Assert.Equal(0f, v.TotalFuel));
    }

    [Theory]
    [InlineData("too fast into shallow water", "Crashed")]
    [InlineData("gently into water 7.5 m deep", "Landed")]
    [InlineData("gently into water 10 m deep", "Landed")]
    public void A_plunge_onto_a_shallow_seabed_is_handed_to_the_physics_and_ends_as_in_it(string flight, string ending)
    {
        // The seabed within its plunge's reach: the formula flies it down to the water, and hands it
        // back to the physics there, which plays its plunge (see Formula's Judge). (In 10 m of
        // water, past the 9.2 m a booster's hull floats in, its feet hanging under it reach the seabed.)
        var outcome = Enum.Parse<FlightState>(ending);
        var state = Touching(flight);
        var jolt = Outcome(state, watched: true);
        var (sim, v, watch) = Start(state, watched: false);
        using var _ = sim;
        var (backAt, lowestThen, outAgain) = (double.NaN, float.NaN, false);
        FlyFreely(sim, watch, 90, () =>
        {
            if (double.IsNaN(backAt) && !v.OnFormula)
                (backAt, lowestThen) = (sim.Time, v.FormulaLowest(v.CentreOfMass, Quaternion.CreateFromRotationMatrix(v.Model)).Clearance);
            outAgain |= !double.IsNaN(backAt) && v.OnFormula && v.State == FlightState.Flying;
            return v.State != FlightState.Flying;
        });
        output.WriteLine($"{flight}: Jolt {jolt.State} at {jolt.Touchdown:0.00} m/s ({jolt.Reason}); handed back at {backAt:0.00} s, its lowest point {lowestThen:0.00} m over the water, "
            + $"then {v.State} at {v.TouchdownSpeed:0.00} m/s ({v.CrashReason})");
        Assert.False(double.IsNaN(backAt), "it was never handed back");
        Assert.InRange(lowestThen, -1f, 0.5f); // where it touched the water, not before
        Assert.False(outAgain, "it left the physics again before its plunge ended");
        Assert.Equal(outcome, jolt.State);
        Assert.Equal(outcome, v.State);
        Assert.Equal(Why(jolt.Reason), Why(v.CrashReason));
    }

    [Fact]
    public void One_handed_back_that_has_landed_leaves_the_physics_again_once_it_flies_off()
    {
        // Handed back on touching 7.5 m of water, it lands on the seabed in the physics. Then flown
        // up and away at full throttle, and left to fly on alone: it is a vessel like any other
        // again, so once clear of the water and the ground, nobody watching, it leaves the physics.
        var (sim, v, watch) = Start(Touching("gently into water 7.5 m deep"), watched: false);
        using var _ = sim;
        var far = sim.Flight.WorldVessels.Single(x => x.Id == 1);
        Assert.True(sim.Run((_, _) => default, _ => v.State != FlightState.Flying, maxSeconds: 60), "it never came down");
        Assert.Equal(FlightState.Landed, v.State);
        Assert.False(v.OnFormula);
        sim.Step(new FlightInput(SwitchTo: v, FullThrottle: true));
        Assert.True(sim.Run((_, _) => default, _ => v.Ground > 30f, maxSeconds: 10), $"it never flew up: {v.State}, {v.Ground:0.0} m up");
        sim.Step(new FlightInput(SwitchTo: far));
        var start = sim.Time;
        var left = sim.Run((_, _) => default, _ => v.OnFormula, maxSeconds: 5);
        output.WriteLine($"flown off to {v.Ground:0} m, {v.State}; left alone, out of the physics after {sim.Time - start:0.00} s: {left}");
        Assert.True(left, "it stayed in the physics, nobody watching it, flying clear of everything");
    }

    [Fact]
    public void A_burn_under_formula_runs_out_of_fuel_and_coasts_on()
    {
        // A satellite in orbit at full throttle with 1% of its fuel, unwatched.
        var satellite = UnwatchedTests.Satellite(0, 9000f, 0f, 60f);
        var fuel = (float[])satellite.Fuel.Clone();
        fuel[satellite.First] = satellite.Vehicle.Stages[satellite.First].FuelMass * 0.01f;
        var (sim, v, watch) = Start(satellite with { Throttle = 1f, Fuel = fuel }, watched: false);
        using var _ = sim;
        Assert.True(v.EngineLit);
        Fly(sim, v, watch, 5, x => x.TotalFuel <= 0f);
        Assert.Equal(0f, v.TotalFuel);
        Assert.False(v.EngineLit);
        // The formula steps on a moment past the burn's end before it coasts: from then on, the
        // orbit its state then is on.
        Fly(sim, v, watch, 1);
        var (at, starsVelocity, start) = (v.CentreOfMass, Planet.AmongStars(v.CentreOfMass, v.Velocity), sim.Flight.WorldTime);
        Fly(sim, v, watch, 30);
        Assert.True(float.IsFinite(v.Velocity.X) && float.IsFinite(v.Velocity.Y) && float.IsFinite(v.Velocity.Z), $"its velocity is {v.Velocity}");
        Assert.True(float.IsFinite(v.CentreOfMass.Length()));
        Assert.Equal(FlightState.Flying, v.State);
        var (expected, _) = Predicted(at, starsVelocity, sim.Flight.WorldTime - start);
        var off = Vector3.Distance(v.CentreOfMass, expected);
        output.WriteLine($"30 s after the burn, {off:0.000} m off the orbit it ended on");
        Assert.InRange(off, 0f, 0.5f);
    }

    [Fact]
    public void A_vessel_under_formula_into_the_tower_is_wrecked_as_in_the_physics()
    {
        // A booster, legs stowed, upright 20 m over the top of Home's tower, falling at 20 m/s.
        var home = LaunchSites.Home;
        var state = Drops.Placed(home.Pad, Planet.East(home.Pad), 20f, legsOut: false) with
        {
            Position = home.TowerFoot + home.Up * (Scene.TowerHeight + 20f),
            Rotation = home.Frame,
            Aim = home.Up,
        };
        var away = LaunchSites.All[1];
        var jolt = Outcome(state, watched: true, site: away);
        var formula = Outcome(state, watched: false, site: away);
        output.WriteLine($"Jolt: {jolt.State} ({jolt.Reason}); the formula: {formula.State} ({formula.Reason})");
        Assert.Equal(FlightState.Crashed, jolt.State);
        Assert.Equal(FlightState.Crashed, formula.State);
        Assert.Contains("service tower", jolt.Reason);
        Assert.Equal(jolt.Reason, formula.Reason);
    }

    [Theory]
    [InlineData("a zone's")]
    [InlineData("the drone ship's")]
    public void A_vessel_under_formula_into_a_mast_is_wrecked_as_in_the_physics(string whose)
    {
        // A booster, legs stowed, upright with its bell 5 m over the head of a light mast, falling at
        // 20 m/s onto it (as the tower's case): a landing zone's corner mast (nothing else there),
        // or one of the drone ship's, which rides the waves with its deck (over where its head will
        // be as the booster comes down on it, a quarter second on; the formula judges it where it
        // is as each step comes).
        const float Falling = 20f, Over = 5f;
        Rafts.Time = 0.0;
        var place = PadLights.All.First(p => whose == "a zone's" ? !p.IsPad && p.Ship is null : p.Ship is not null);
        place.Ship?.Set(Over / Falling);
        var mast = place.Masts[0];
        var head = place.ToWorld(mast.Foot + new Vector3(0f, mast.Height, 0f));
        place.Ship?.Set(0.0); // the simulation's world starts at time 0
        var up = Planet.Up(head);
        var state = Drops.Placed(head, Planet.East(head), Falling, legsOut: false);
        state = state with { Position = head + up * (Over + state.Vehicle.Stages[state.First].BellHeight) };
        var away = LaunchSites.All[1]; // launched from there: no new rocket on Home's pad, by Home's zone, to keep it in the physics
        var jolt = Outcome(state, watched: true, site: away);
        var formula = Outcome(state, watched: false, site: away);
        output.WriteLine($"{whose} mast ({place.Name}): Jolt {jolt.State} ({jolt.Reason}) {Vector3.Distance(jolt.Vessel.CrashPosition, head):0.0} m from its head; "
            + $"the formula {formula.State} ({formula.Reason}) {Vector3.Distance(formula.Vessel.CrashPosition, head):0.0} m");
        Assert.Equal(FlightState.Crashed, jolt.State);
        Assert.Equal(FlightState.Crashed, formula.State);
        Assert.Equal(Vessel.MastReason, jolt.Reason);
        Assert.Equal(jolt.Reason, formula.Reason);
        Assert.True(formula.Vessel.CrashedWatched == false, "the formula's was seen: the physics judged it");
        Assert.InRange(Vector3.Distance(formula.Vessel.CrashPosition, jolt.Vessel.CrashPosition), 0f, 5f);
    }

    [Fact]
    public void A_vessel_under_formula_lands_on_a_pad_as_in_the_physics()
    {
        // The same booster coming down upright on its legs at 3 m/s onto Home's pad, by its tower.
        var home = LaunchSites.Home;
        var state = Drops.Placed(home.Pad, Planet.East(home.Pad), 3f);
        var away = LaunchSites.All[1];
        var jolt = Outcome(state, watched: true, site: away);
        var formula = Outcome(state, watched: false, site: away);
        output.WriteLine($"Jolt: {jolt.State} at {jolt.Touchdown:0.00} m/s, on target {jolt.OnTarget}, lowest {Drops.Lowest(jolt.Vessel):0.00} m;"
            + $" the formula: {formula.State} at {formula.Touchdown:0.00} m/s, on target {formula.OnTarget}, lowest {Drops.Lowest(formula.Vessel):0.00} m");
        Assert.Equal(FlightState.Landed, jolt.State);
        Assert.Equal(FlightState.Landed, formula.State);
        Assert.Equal(jolt.OnTarget, formula.OnTarget);
        // On the pad's deck, upright on its feet.
        Assert.InRange(Planet.Radius * Planet.AngleBetween(formula.Vessel.Position, home.Pad), 0f, Scene.PadHalfSize);
        Assert.InRange(Drops.Lowest(formula.Vessel), 0f, Scene.LegStroke);
        Assert.InRange(formula.Vessel.TiltDegrees, 0f, 1f);
    }

    [Fact]
    public void A_landed_vessel_under_formula_stays_where_it_is()
    {
        var (ground, downhill) = Level.Value;
        var (sim, v, watch) = Start(Drops.Placed(ground, downhill, 3f), watched: false);
        using var _ = sim;
        Fly(sim, v, watch, 10, x => x.State != FlightState.Flying);
        Assert.Equal(FlightState.Landed, v.State);
        var (model, ground0) = (v.Model, v.Ground);
        Fly(sim, v, watch, 20);
        Assert.Equal(FlightState.Landed, v.State);
        Assert.Equal(model, v.Model);
        Assert.Equal(Vector3.Zero, v.Velocity);
        Assert.Equal(ground0, v.Ground);
    }

    /// <summary>
    /// Flown <paramref name="seconds"/> (or until <paramref name="until"/>), watched or not, but with
    /// no check that it stays in or out of the physics: what it did, frame by frame, is the caller's.
    /// </summary>
    private static void FlyFreely(Simulation sim, Vessel[]? watch, double seconds, Func<bool> until) =>
        sim.Run((_, _) => new FlightInput(Watched: watch), _ => until(), seconds);

    [Fact]
    public void Touching_down_on_more_thrust_than_its_weight_it_lifts_off_as_in_the_physics()
    {
        // Coming down at 2 m/s from just over level ground, its engine pushing 1.2 times its
        // weight with fuel for some 3 s: it touches down as it slows, then climbs away (the
        // physics' engine lifts it off), and comes down again once dry, too hard.
        var (ground, downhill) = Level.Value;
        var state = OnItsEngine(Drops.Placed(ground, downhill, 2f, fuel: 0.02f), 1.2f);
        (FlightState State, string? Reason, float Highest, int Sounds) Fly(bool watched)
        {
            var (sim, v, watch) = Start(state, watched);
            using var _ = sim;
            var (touched, highest, sounds) = (false, 0f, 0);
            FlyFreely(sim, watch, 30, () =>
            {
                foreach (var sound in sim.Flight.Sounds)
                    if (sound.Cue == SoundCue.Touchdown && Vector3.Distance(sound.Position, v.CentreOfMass) < 40f)
                        sounds++;
                touched |= v.TouchdownSpeed > 0f;
                if (touched)
                    highest = MathF.Max(highest, v.Ground);
                return v.State != FlightState.Flying;
            });
            return (v.State, v.CrashReason, highest, sounds);
        }
        var jolt = Fly(watched: true);
        var formula = Fly(watched: false);
        output.WriteLine($"Jolt: {jolt.State} ({jolt.Reason}), up to {jolt.Highest:0.0} m after its first touch, {jolt.Sounds} touchdown sounds; "
            + $"the formula: {formula.State} ({formula.Reason}), up to {formula.Highest:0.0} m, {formula.Sounds} touchdown sounds");
        Assert.True(jolt.Highest > 3f, "the physics never lifted it off: this shows nothing");
        Assert.True(formula.Highest > 3f, $"under formula it rose only {formula.Highest:0.0} m after touching down: it never lifted off");
        Assert.Equal(jolt.State, formula.State);
        Assert.Equal(Why(jolt.Reason), Why(formula.Reason));
        // One: its first touchdown. None for each step it stood burning, and none for the hit
        // that wrecked it (that one an explosion: see Vessel.WreckByFormula).
        Assert.Equal(1, formula.Sounds);
    }

    /// <summary>
    /// A narrow peak on land, out of every site's way: a point on its top, and a heading across it,
    /// away from Home, along which the straight line 1.5 m under its top touches the ground only
    /// within 10 m of it (far narrower than a formula step at 1,000 m/s), clears it beyond that,
    /// and by 5 m beyond 40 m, from 400 m before to 400 m after.
    /// </summary>
    private static readonly Lazy<(Vector3 Top, Vector3 Heading)> Peak = new(() =>
    {
        var rng = new Random(23);
        float Height(Vector3 p) => Terrain.Height(Planet.Up(p));
        for (var tries = 0; tries < 20000; tries++)
        {
            var d = Vector3.Normalize(new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1));
            if (Terrain.IsSea(d))
                continue;
            // Up to its top, 4 m at a time.
            var p = Planet.Centre + d * Planet.SeaRadius;
            for (var climb = 0; climb < 2000; climb++)
            {
                var (east, north) = (Planet.East(p), Planet.North(p));
                var (best, at) = (Height(p), p);
                for (var k = 0; k < 8; k++)
                {
                    var (s, c) = MathF.SinCos(MathF.Tau * k / 8);
                    var q = p + (east * c + north * s) * 4f;
                    if (Height(q) > best)
                        (best, at) = (Height(q), q);
                }
                if (at == p)
                    break;
                p = at;
            }
            var up = Planet.Up(p);
            var top = Planet.Centre + up * (Planet.SeaRadius + Height(p));
            if (Height(p) < 30f || Sites.All.Any(site => Vector3.Distance(site.Centre, top) < site.Radius + 1000f)
                || Vector3.Distance(LaunchSites.Home.Pad, top) < 2000f)
                continue;
            for (var k = 0; k < 16; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 16);
                var heading = Planet.East(top) * c + Planet.North(top) * s;
                if (Vector3.Dot(heading, LaunchSites.Home.Pad - top) > 0f)
                    continue; // towards Home: it would close on the rocket on its pad, and be kept in the physics
                var line = top - up * 1.5f;
                var fits = true;
                for (var x = -400f; x <= 400f && fits; x += 1f)
                {
                    var q = line + heading * x;
                    var clear = Planet.Altitude(q) - Height(q);
                    fits = MathF.Abs(x) <= 10f || (clear > 0f && (MathF.Abs(x) <= 40f || clear > 5f));
                }
                if (fits)
                    return (top, heading);
            }
        }
        throw new InvalidOperationException("no narrow peak to fly at");
    });

    [Fact]
    public void A_vessel_under_formula_flown_fast_and_low_at_a_ridge_narrower_than_a_step_hits_it_as_in_the_physics()
    {
        // A booster lying along its way, at 1,000 m/s (a 100 m formula step), its underside 1.5 m
        // under a peak's top, from 350 m back: only the peak's top, 20 m across at most, stands in
        // its way. Handed out at the end of its first frame, its steps end some 33 m before the top
        // and 67 m past it, its 27 m length clear of the ground at both: read at its steps' ends
        // alone, it would pass through the top unharmed.
        const float Speed = 1000f;
        var (top, heading) = Peak.Value;
        var up = Planet.Up(top);
        var start = top - up * 1.5f + up * Vehicle.Booster.Stages[0].Radius - heading * 350f; // its centre of mass, on its axis
        var comHeight = RocketGeometry.CentreOfMassHeight(Vehicle.Booster, 0, 0);
        var state = Drops.Placed(top, heading, 0f, legsOut: false) with
        {
            Position = start - heading * comHeight, Rotation = UnwatchedTests.RotationTo(heading), Aim = heading, Velocity = heading * Speed,
        };
        output.WriteLine($"a peak {Terrain.Height(up):0} m up, flown at {Speed:0} m/s: {Speed * Scene.FormulaStepSeconds:0} m a formula step");
        (FlightState State, string? Reason, bool Out, Vector3 At) Fly(bool watched)
        {
            var (sim, v, watch) = Start(state, watched);
            using var _ = sim;
            var under = true;
            // Until 350 m past the top: further on, the ground rises again.
            FlyFreely(sim, watch, 0.7, () =>
            {
                under &= watched || v.OnFormula;
                return v.State == FlightState.Crashed;
            });
            return (v.State, v.CrashReason, under, v.CrashPosition);
        }
        var jolt = Fly(watched: true);
        var formula = Fly(watched: false);
        float Along(Vector3 at) => Vector3.Dot(at - top, heading);
        output.WriteLine($"Jolt: {jolt.State} ({jolt.Reason}) {Along(jolt.At):0.0} m along from the top; the formula: {formula.State} ({formula.Reason}) {Along(formula.At):0.0} m along, under formula throughout {formula.Out}");
        Assert.Equal(FlightState.Crashed, jolt.State);
        Assert.True(formula.Out, "it came back into the physics: this shows nothing of the formula");
        Assert.Equal(jolt.State, formula.State);
        Assert.Equal(Why(jolt.Reason), Why(formula.Reason));
        Assert.InRange(Vector3.Distance(jolt.At, formula.At), 0f, Speed * Scene.FormulaStepSeconds); // wrecked by the same peak, within a step
    }

    /// <summary>
    /// <paramref name="state"/> coming down on <paramref name="raft"/>, once watched and once not:
    /// both land on it, as the physics lands it; unwatched, it is judged by the formula, and comes
    /// back into the physics on the frame it lands, aboard (the raft carries it from there), not
    /// held behind the hand-ins' budget while the raft moves from under it.
    /// </summary>
    private void LandsAboard(VesselState state, Raft raft)
    {
        var (jolt, joltOnTarget) = (FlightState.Flying, false);
        {
            var (sim, v, watch) = Start(state, watched: true);
            using var _ = sim;
            FlyFreely(sim, watch, 60, () => v.State != FlightState.Flying);
            (jolt, joltOnTarget) = (v.State, v.LandedOnTarget);
        }
        {
            var (sim, v, watch) = Start(state, watched: false);
            using var _ = sim;
            // The frame it touched (its landing begun: for one still burning, before it has
            // landed), the frame it landed, and the frame it was back in the physics.
            var (touchedAt, landedAt, backAt, frame, outUntilTouched) = (-1, -1, -1, 0, true);
            FlyFreely(sim, watch, 60, () =>
            {
                frame++;
                if (touchedAt < 0 && v.TouchdownSpeed > 0f)
                    touchedAt = frame;
                if (landedAt < 0 && v.State != FlightState.Flying)
                    landedAt = frame;
                if (touchedAt < 0)
                    outUntilTouched &= v.OnFormula;
                if (backAt < 0 && !v.OnFormula)
                    backAt = frame;
                return landedAt >= 0 && frame >= landedAt + 300; // and 5 s on, aboard
            });
            output.WriteLine($"Jolt: {jolt}, on target {joltOnTarget}; the formula: {v.State} {v.CrashReason}, touchdown {v.TouchdownSpeed:0.00} m/s, on target {v.LandedOnTarget}; "
                + $"touched down on frame {touchedAt}, back in the physics on frame {backAt} ({backAt - touchedAt} waited), landed on frame {landedAt}; "
                + $"aboard {v.Craft.Aboard?.Raft == raft}, frozen {v.Craft.Frozen}, {raft.HeightAbove(v.Position):0.00} m over its top");
            Assert.Equal(FlightState.Landed, jolt);
            Assert.True(outUntilTouched, "it was in the physics before it touched down: the formula didn't judge it");
            Assert.Equal(FlightState.Landed, v.State);
            Assert.Equal(joltOnTarget, v.LandedOnTarget);
            Assert.Equal(touchedAt, backAt); // back at once, outside the budget, burning or not
            Assert.False(v.OnFormula);
            Assert.Same(raft, v.Craft.Aboard?.Raft);
            Assert.InRange(raft.HeightAbove(v.Position) ?? float.NaN, -0.5f, 3f); // on its top, carried with it
        }
    }

    [Fact]
    public void A_vessel_under_formula_touching_down_on_the_deck_with_its_engine_lit_comes_back_aboard_and_lands_there()
    {
        // As below, but coming down on its engine at nine tenths of its weight, with fuel for some
        // 4 s more: it stands burning on the moving deck, in the physics, and lands there once dry.
        Rafts.Ship.Set(0.0);
        var at = Rafts.Ship.Current.Position + Rafts.Ship.Normal * Scene.DroneShipFreeboard;
        LandsAboard(OnItsEngine(Drops.Placed(at, Planet.East(at), 2f, Vehicle.TwoStage, first: 0, last: 0, mission: Mission.ReachOrbit, flight: 1,
            fuel: 0.02f), 0.9f), Rafts.Ship);
    }

    [Fact]
    public void A_vessel_under_formula_lands_on_the_drone_ships_moving_deck_and_comes_back_aboard()
    {
        // The Reach Orbit mission's booster onto the middle of its drone ship's deck at 2 m/s.
        Rafts.Ship.Set(0.0);
        var at = Rafts.Ship.Current.Position + Rafts.Ship.Normal * Scene.DroneShipFreeboard;
        LandsAboard(Drops.Placed(at, Planet.East(at), 2f, Vehicle.TwoStage, first: 0, last: 0, mission: Mission.ReachOrbit, flight: 1), Rafts.Ship);
    }

    [Fact]
    public void A_vessel_under_formula_lands_on_a_floe_and_comes_back_aboard()
    {
        var floe = FloeTests.BigFloe();
        var at = FloeTests.Top(floe);
        LandsAboard(Drops.Placed(at, Planet.East(at), 1.5f), floe);
    }

    [Fact]
    public void Stepping_a_vessel_by_formula_allocates_nothing()
    {
        // A booster falling 80 m over Home's pad, burning: low enough that every step checks the
        // tower and masts and measures what is under it. Three seconds of steps, by hand.
        var home = LaunchSites.Home;
        var state = Drops.Placed(home.Pad, Planet.East(home.Pad), 5f, clearance: 80f) with { Throttle = 0.2f };
        var (sim, v, _) = Start(state, watched: false, site: LaunchSites.All[1]);
        using var _ = sim;
        Assert.True(v.OnFormula && v.EngineLit);
        var t = sim.Flight.WorldTime;
        Formula.Advance(v, 0.1f, t += 0.1); // warmed up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 30; i++)
            Formula.Advance(v, 0.1f, t += 0.1);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(FlightState.Flying, v.State);
        Assert.True(v.Ground < 70f, $"it is still {v.Ground:0} m up: it never came near the ground");
    }

    [Fact]
    public void Stepping_fast_and_low_lifting_off_and_coming_down_on_a_seabed_by_formula_allocates_nothing()
    {
        // Three by hand, each with a twin flown first (the warm-up, which also grows the flight's
        // sound list as far as the touchdowns need), the twins apart (in the first step, in the
        // physics, one inside the other would keep both there): at 1,000 m/s 30 m over the sea (the
        // ground read at many points a step); touching down on more thrust than its weight
        // (standing, then lifting off: Rising); and gently into 7.5 m of water (handed back).
        var (ground, downhill) = Level.Value;
        var sea = Sea;
        var fast = Drops.Placed(sea, Planet.East(sea), 0f, legsOut: false, clearance: 30f) with
        {
            Rotation = UnwatchedTests.RotationTo(Planet.East(sea)), Aim = Planet.East(sea), Velocity = Planet.East(sea) * 1000f,
        };
        var lifting = OnItsEngine(Drops.Placed(ground, downhill, 2f, fuel: 0.02f), 1.2f);
        var wet = Drops.Placed(SevenAndAHalfMetres.Value, Planet.East(SevenAndAHalfMetres.Value), 2f, legsOut: false);
        VesselState Apart(VesselState s, int id, float metres) => s with { Id = id, Position = s.Position + Planet.North(s.Position) * metres };
        VesselState[] states = [Apart(fast, 0, 0f), Apart(fast, 1, 100f), Apart(lifting, 2, 0f), Apart(lifting, 3, 40f), Apart(wet, 4, -4f), Apart(wet, 5, 4f),
            FarFrom(sea, 6)];
        using var sim = new Simulation(world: new WorldState { Vessels = states, NextVesselId = states.Length });
        var vessels = Enumerable.Range(0, 6).Select(id => sim.Flight.WorldVessels.Single(x => x.Id == id)).ToArray();
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.Single(x => x.Id == 6)));
        Assert.All(vessels, v => Assert.True(v.OnFormula));
        var t = sim.Flight.WorldTime;
        var rose = false;
        void Fly(int first, double from)
        {
            for (var i = 1; i <= 30; i++)
                for (var k = first; k < vessels.Length; k += 2)
                {
                    Formula.Advance(vessels[k], 0.1f, from + 0.1 * i);
                    rose |= k == 3 && vessels[k].Motion.Rising;
                }
        }
        Fly(0, t); // the warm-up
        var before = GC.GetAllocatedBytesForCurrentThread();
        Fly(1, t);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"fast: {vessels[1].State}; lifting: {vessels[3].State}, rose {rose}; wet: {vessels[5].State}, handed back {vessels[5].NeedsPhysics}; {allocated} bytes");
        Assert.True(rose, "the lifting one never lifted off its stand: this shows nothing of it");
        Assert.True(vessels[5].NeedsPhysics, "the wet one was never handed back: this shows nothing of it");
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void A_vessel_under_formula_flies_no_node_and_never_stages()
    {
        // A whole three-stage rocket in orbit, its first stage's tank empty, a burn planned 5 s on.
        var vehicle = Vehicle.ThreeStage;
        var (position, centre, velocity) = WorldPresets.CircularOrbit(9000f, 0f, float.DegreesToRadians(60f),
            RocketGeometry.CentreOfMassHeight(vehicle, 0, vehicle.Stages.Length - 1));
        var up = Planet.Up(centre);
        var rocket = UnwatchedTests.Satellite(0, 9000f, 0f, 60f) with
        {
            Vehicle = vehicle, First = 0, Last = vehicle.Stages.Length - 1, Position = position, Velocity = velocity,
            Rotation = UnwatchedTests.RotationTo(up), Aim = up, Throttle = 1f,
            Fuel = [.. vehicle.Stages.Select((s, i) => i == 0 ? 0f : s.FuelMass * 0.5f)],
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 0, vehicle.Stages.Length - 1),
            // Anchored where it is now (only that the node is kept is asked, not where it is).
            Chain = new ManoeuvreChain(new ChainAnchor(GroundBody.Planet.Name, 5.0, centre, Planet.AmongStars(centre, velocity)), [new ManoeuvreNode(0, Prograde: 20f)]),
        };
        var (sim, v, watch) = Start(rocket, watched: false);
        using var _ = sim;
        Fly(sim, v, watch, 10);
        Assert.True(sim.Flight.WorldTime > 5.0);
        Assert.Single(v.Chain!.Nodes);
        Assert.Equal(5.0, v.Chain.TimeOf(0));
        Assert.Equal(0, v.Stage);
        Assert.False(v.EngineLit);
        Assert.Equal(FlightState.Flying, v.State);
    }

    [Fact]
    public void Under_formula_one_going_straight_up_above_the_air_rises_and_falls_back()
    {
        // The upper stage alone, its engine off, 4 km over the sea on the far side of the planet
        // from the pad (so under formula from the start), going straight up at 150 m/s among the
        // stars: by Kepler's equation for a line it rises to its top and falls back, where the
        // orbit has it, never standing still.
        var vehicle = Vehicle.ThreeStage;
        var up = Vector3.Normalize(new Vector3(0.2f, -1f, 0.1f));
        var at = Planet.Centre + up * (Planet.SeaRadius + 4_000f);
        var stars = up * 150f;
        var rotation = Formula.Turn(Vector3.UnitY, up);
        var offset = RocketGeometry.CentreOfMassOffset(vehicle, 1, 1);
        var state = new VesselState
        {
            Id = 0, Flight = 1, Mission = Mission.FreePlay, Vehicle = vehicle, First = 1, Last = 1,
            Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * 0.5f)], State = FlightState.Flying,
            Position = at - Vector3.Transform(offset, rotation), Rotation = rotation, CentreOfMassOffset = offset,
            Velocity = Planet.OverGround(at, stars), AngularVelocity = Vector3.Zero, Aim = up, HasBeenAirborne = true, MaxAltitude = 4_000f,
        };
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1, Flights = 1 });
        var v = sim.Flight.WorldVessels.Single(x => x.Id == 0);
        Assert.True(v.OnFormula);
        var top = new Orbit(GroundBody.Planet, at, stars).TopSeconds!.Value;
        var (highest, highestAt) = (0f, 0.0);
        var (worst, start) = (0f, sim.Flight.WorldTime);
        sim.Run((_, _) => default, f =>
        {
            var since = f.WorldTime - start;
            var altitude = Planet.Altitude(v.CentreOfMass);
            if (altitude > highest)
                (highest, highestAt) = (altitude, since);
            if (Planet.AboveAir(v.CentreOfMass))
                worst = MathF.Max(worst, Vector3.Distance(v.CentreOfMass, Predicted(at, stars, since).At));
            return since > top && altitude < 3_500f;
        }, maxSeconds: 2 * top + 10);
        output.WriteLine($"highest {highest:0} m at {highestAt:0.0} s (its top at {top:0.0} s), {worst:0.000} m at worst from its orbit above the air; down to {Planet.Altitude(v.CentreOfMass):0} m at {sim.Time:0.0} s");
        Assert.True(v.OnFormula);
        Assert.InRange(highestAt, top - 0.5, top + 0.5);
        Assert.True(highest > 4_000f + 500f, $"it rose only to {highest:0} m");
        Assert.True(Planet.Altitude(v.CentreOfMass) < 3_500f, "it never fell back");
        Assert.True(worst < 1f, $"{worst:0.000} m from where its orbit has it");
    }
}
