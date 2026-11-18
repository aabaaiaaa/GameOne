using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The moon trip flown headless through <see cref="FlightInput"/>, as a player would, by the
/// autopilots of <c>MoonPilots.cs</c>, the upper stage starting each leg where that leg is about
/// (<see cref="MoonTrip"/>), with the fuel a real ascent leaves it: from a low orbit round the
/// planet across into the moon's sphere (<see cref="TransferPilot"/>); arriving, captured into an
/// orbit round it (<see cref="CapturePilot"/>); from that orbit, down to a landing on its landing
/// spot (<see cref="MoonLandingPilot"/>); a dive into it, warned of and judged a crash; and (long)
/// the whole trip from the low orbit to the moon's ground. Low over the ground, the speed over it
/// stays under <see cref="Scene.GroundTopSpeed"/>, so the game's hold never changes the way,
/// checked after every frame of it.
/// </summary>
public sealed class MoonTripTests(ITestOutputHelper output)
{
    private static GroundBody Moon => GroundBody.Moon;

    /// <summary>A free-play flight in <paramref name="world"/>, its first vessel flown (from the frame after the switch).</summary>
    internal static (Simulation Sim, Vessel Flown) Flying(WorldState world)
    {
        var body = Bodies.Named(world.Vessels[0].Body);
        var sim = new Simulation(Mission.FreePlay, world: world, body: body);
        var flown = sim.Flight.WorldVessels.Single(v => v.Id == world.Vessels[0].Id);
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Run((_, _) => default, f => f.Controlled == flown, maxSeconds: 1);
        Assert.Same(flown, sim.Flight.Controlled);
        return (sim, flown);
    }

    /// <summary>The fuel (kg) in <paramref name="vessel"/>'s stage now burning (its lowest).</summary>
    internal static float FuelKg(Vessel vessel) => vessel.Fuel * vessel.CurrentStage.FuelMass;

    /// <summary>
    /// The fastest the flown vessel has gone over its body's ground, checked every frame; and the
    /// fastest while low over it (<see cref="GroundBody.LowOverTheGround"/>), where the game holds it
    /// to <see cref="Scene.GroundTopSpeed"/>.
    /// </summary>
    internal sealed class Fastest
    {
        public float Speed { get; private set; }
        public float Low { get; private set; }

        public void Check(Flight flight)
        {
            var (v, speed) = (flight.Controlled, flight.Controlled.Velocity.Length());
            Speed = MathF.Max(Speed, speed);
            if (v.Body.LowOverTheGround(v.CentreOfMass))
                Low = MathF.Max(Low, speed);
        }

        /// <summary>Fails if it was ever as fast as the ground's top speed low over the ground: the hold would have changed its way.</summary>
        public void NeverHeld() => Assert.True(Low < Scene.GroundTopSpeed, $"{Low:0} m/s low over the ground");
    }

    /// <summary>
    /// Flies <paramref name="pilot"/> through <paramref name="leg"/> until <paramref name="until"/>,
    /// checking the speed over the ground every frame, and failing as soon as the tank runs dry
    /// (<see cref="RanDry"/>).
    /// </summary>
    internal static bool Fly(Simulation sim, Pilot pilot, Func<Flight, bool> until, double maxSeconds, Fastest fastest, string leg) =>
        sim.Run(pilot, f =>
        {
            fastest.Check(f);
            RanDry(f.Controlled, leg);
            return until(f);
        }, maxSeconds);

    /// <summary>
    /// Fails if <paramref name="flown"/>'s tank is dry while it still flies, saying in which leg and
    /// how far short it is: coming down to the moon's ground (the landing, or a dive), at least what
    /// stopping where it is and coming down from its height would take (its speed over the ground
    /// and the fall's, by the rocket's own exhaust speed, its losses left out); elsewhere, at least
    /// the rest of that leg.
    /// </summary>
    private static void RanDry(Vessel flown, string leg)
    {
        if (flown.State != FlightState.Flying || FuelKg(flown) > 0f)
            return;
        var stage = flown.CurrentStage;
        var exhaust = stage.Thrust * stage.BurnSeconds / stage.FuelMass;
        var stop = flown.Velocity.Length() + MathF.Sqrt(2f * flown.Body.SurfaceGravity * MathF.Max(flown.Ground, 0f));
        var shortBy = flown.Mass * (MathF.Exp(stop / exhaust) - 1f);
        Assert.Fail(flown.Body == Moon && leg is "landing"
            ? $"the tank ran dry in the {leg}, {flown.Ground:0} m up at {flown.Velocity.Length():0} m/s over the ground: short by at least {shortBy:0} kg"
            : $"the tank ran dry in the {leg}: short by at least the rest of it");
    }

    private void Report(string what, (double Time, float Fuel)? at) =>
        output.WriteLine(at is { } a ? $"  {what}: {a.Time:0.0} s, {a.Fuel:0} kg" : $"  {what}: never");

    /// <summary>The transfer from the low orbit to the moon's sphere, flown in <paramref name="sim"/>.</summary>
    private TransferPilot Transfer(Simulation sim, Fastest fastest)
    {
        var pilot = new TransferPilot();
        var start = sim.Time;
        var crossed = Fly(sim, pilot.Fly, f => f.Body == Moon, maxSeconds: 1200, fastest, "transfer");
        output.WriteLine($"transfer: {(crossed ? "in the moon's sphere" : $"not there ({pilot.Now})")} after {sim.Time - start:0} s, {FuelKg(sim.Flight.Controlled):0} kg left, the fastest {fastest.Speed:0} m/s over the ground");
        Report("burn began", pilot.BurnBegan);
        Report("burn ended", pilot.BurnEnded);
        Report("trim ended", pilot.TrimEnded);
        Assert.True(crossed, $"never reached the moon's sphere ({pilot.Now})");
        // The trim came within its bounds (it gives up after its tries), and the way round the moon clears its ground.
        var (polar, periapsis) = pilot.Trimmed ?? throw new Xunit.Sdk.XunitException("never trimmed");
        output.WriteLine($"  trimmed: the moon leg {Math.Asin(polar) * 180 / Math.PI:0.00}° off its poles, its periapsis {periapsis:0} m over the datum");
        Assert.True(pilot.TrimmedWithin, $"the trim gave up {Math.Asin(polar) * 180 / Math.PI:0.00}° off the poles, its periapsis {periapsis:0} m");
        Assert.True(periapsis > Moon.MaxHeight, $"the way round the moon comes down to {periapsis:0} m, under its highest ground ({Moon.MaxHeight:0} m)");
        return pilot;
    }

    [Fact]
    public void From_a_low_orbit_the_upper_stage_flies_into_the_moons_sphere()
    {
        var (sim, flown) = Flying(MoonTrip.World(MoonTrip.Start, MoonTrip.LowOrbit(0, MoonTrip.Start)));
        using var _ = sim;
        var fastest = new Fastest();
        var start = sim.Time;
        Transfer(sim, fastest);
        Assert.Same(Moon, flown.Body);
        Assert.True(sim.Time - start <= 1200);
        Assert.True(FuelKg(flown) > 0f, "no fuel left");
        Assert.True(fastest.Speed < 1000f, $"{fastest.Speed:0} m/s over the ground");
        fastest.NeverHeld();
    }

    /// <summary>The capture into an orbit round the moon, flown in <paramref name="sim"/> from in its sphere.</summary>
    private CapturePilot Capture(Simulation sim, Fastest fastest)
    {
        var pilot = new CapturePilot();
        var start = sim.Time;
        var done = Fly(sim, pilot.Fly, _ => pilot.Now == CapturePilot.Phase.Done, maxSeconds: 400, fastest, "capture");
        var f = sim.Flight;
        output.WriteLine($"capture: {(done ? "done" : $"not done ({pilot.Now})")} after {sim.Time - start:0} s: periapsis {f.Periapsis:0} m, apoapsis {f.Apoapsis:0} m, period {f.OrbitLap:0} s, frame {f.Frame}, {FuelKg(f.Controlled):0} kg left");
        Report("burn began", pilot.BurnBegan);
        Report("burn ended", pilot.BurnEnded);
        Assert.True(done, $"never captured ({pilot.Now})");
        return pilot;
    }

    [Fact]
    public void Arriving_it_is_captured_into_an_orbit_round_the_moon()
    {
        var (vessel, time) = MoonTrip.Arriving(0);
        var (sim, flown) = Flying(MoonTrip.World(time, vessel));
        using var _ = sim;
        Assert.Same(Moon, flown.Body);
        var fastest = new Fastest();
        Capture(sim, fastest);
        sim.Run((_, _) => default, _ => false, maxSeconds: 2); // Auto's frame follows the new orbit
        var f = sim.Flight;
        Assert.Same(Moon, flown.Body);
        Assert.NotNull(f.OrbitLap); // closed, within the moon's pull
        Assert.True(f.Periapsis > Moon.MaxHeight + 100f, $"periapsis {f.Periapsis:0} m");
        Assert.Equal(FrameChoice.Auto, f.FrameChoice);
        Assert.Equal(Frame.Orbital, f.Frame);
        fastest.NeverHeld();
    }

    /// <summary>The landing from an orbit round the moon, flown in <paramref name="sim"/>.</summary>
    private MoonLandingPilot Land(Simulation sim, Fastest fastest)
    {
        var pilot = new MoonLandingPilot();
        var start = sim.Time;
        var down = Fly(sim, pilot.Fly, f => f.Controlled.State != FlightState.Flying, maxSeconds: 600, fastest, "landing");
        var flown = sim.Flight.Controlled;
        var spot = pilot.Landing.Place!.Value;
        output.WriteLine($"landing: {flown.State} {flown.CrashReason} after {sim.Time - start:0} s, touchdown {flown.TouchdownSpeed:0.00} m/s, "
            + $"{Moon.AngleBetween(flown.Position, spot) * Moon.Radius:0.0} m from the spot, {FuelKg(flown):0} kg left; the track {pilot.AsideBefore:0} m aside before lining up; the mark {pilot.MarkAtCut:0} m beyond it at the cut");
        Report("line-up began", pilot.LineUpBegan);
        Report("burn began", pilot.BurnBegan);
        Report("burn ended", pilot.BurnEnded);
        Assert.True(down, $"never came down ({pilot.Now}, {pilot.Landing.Now})");
        return pilot;
    }

    /// <summary>That <paramref name="flown"/> is landed on the moon, softly (under 2 m/s), on the levelled plains round its landing spot, and freezes there.</summary>
    private static void Landed(Simulation sim, Vessel flown)
    {
        Assert.Equal(FlightState.Landed, flown.State);
        Assert.Same(Moon, flown.Body);
        Assert.True(flown.TouchdownSpeed < 2f && flown.TouchdownSpeed < Scene.SafeTouchdownSpeed, $"touched down at {flown.TouchdownSpeed:0.00} m/s");
        var off = Moon.AngleBetween(flown.Position, MoonLandingTests.LandingSpot()) * Moon.Radius;
        Assert.True(off < Scene.MoonLandingSpotRadius, $"it came down {off:0} m from the spot, off its levelled plains");
        Assert.True(sim.Run((_, _) => default, _ => flown.Craft.Frozen, maxSeconds: 10), "it never froze");
    }

    [Fact]
    public void From_an_orbit_round_the_moon_it_lands()
    {
        var (sim, flown) = Flying(MoonTrip.World(MoonTrip.Start, MoonTrip.MoonOrbit(0, MoonTrip.MoonHeight)));
        using var _ = sim;
        var fastest = new Fastest();
        var fuel = FuelKg(flown);
        Land(sim, fastest);
        output.WriteLine($"fuel: {fuel:0} kg in orbit, {FuelKg(flown):0} kg landed ({fuel - FuelKg(flown):0} kg to land)");
        Landed(sim, flown);
        fastest.NeverHeld();
    }

    [Fact]
    public void Diving_into_the_moon_is_warned_of_and_judged_a_crash()
    {
        var (vessel, time) = MoonTrip.Arriving(0);
        var (sim, flown) = Flying(MoonTrip.World(time, vessel));
        using var _ = sim;
        // Straight at the moon's middle, the engine full on, until closing on its ground far faster
        // than a fast collision's: then the engine off, as in a dive.
        const float Dive = 300f;
        Pilot dive = (f, _) =>
        {
            var down = Vector3.Normalize(Moon.Centre - f.CentreOfMass);
            var lined = Vector3.Dot(f.Controlled.Up, down) > 0.99f;
            var fast = Vector3.Dot(f.Controlled.Velocity, down) > Dive;
            return new FlightInput(Hold: down, FullThrottle: lined && !fast && f.Throttle < 1f, CutThrottle: (fast || !lined) && f.Throttle > 0f);
        };
        Collision? warned = null;
        var fastest = new Fastest();
        Fly(sim, dive, f =>
        {
            warned ??= f.Warning;
            return flown.State != FlightState.Flying;
        }, maxSeconds: 60, fastest, "dive");
        output.WriteLine($"warned: {(warned is { } w ? $"{Collisions.Name(w)} in {w.Seconds:0.0} s, hitting at {w.HitSpeed:0} m/s" : "never")}; {flown.State}: {flown.CrashReason}");
        Assert.NotNull(warned);
        Assert.Equal("the moon", Collisions.Name(warned.Value));
        Assert.True(warned.Value.HitSpeed > Scene.FastCollisionSpeed);
        Assert.Equal(FlightState.Crashed, flown.State);
        Assert.StartsWith("Hit the moon at", flown.CrashReason);
        var entry = Assert.Single(sim.Flight.Log);
        output.WriteLine($"logged: {entry}");
        Assert.Equal(WorldLog.Named(flown), entry.What);
        Assert.Equal("Hit the moon", entry.How);
        Assert.Equal("on the moon", entry.Where);
    }

    [LongFact]
    [Trait("Run", "Long")]
    public void The_whole_trip_from_a_low_orbit_to_the_moons_ground()
    {
        var (sim, flown) = Flying(MoonTrip.World(MoonTrip.Start, MoonTrip.LowOrbit(0, MoonTrip.Start)));
        using var _ = sim;
        var fastest = new Fastest();
        var fuel = new List<(string Stage, float Kg)> { ("in the low orbit", FuelKg(flown)) };
        Transfer(sim, fastest);
        fuel.Add(("in the moon's sphere", FuelKg(flown)));
        Capture(sim, fastest);
        fuel.Add(("in orbit round the moon", FuelKg(flown)));
        Land(sim, fastest);
        fuel.Add(("landed", FuelKg(flown)));
        output.WriteLine("fuel: " + string.Join(", ", fuel.Select(x => $"{x.Stage} {x.Kg:0} kg")) + $"; the fastest over the ground {fastest.Speed:0} m/s; {sim.Time:0} s of game time");
        Assert.True(FuelKg(flown) > 0f, "ran out of fuel: " + string.Join(", ", fuel.Select(x => $"{x.Stage} {x.Kg:0} kg")));
        Landed(sim, flown);
        fastest.NeverHeld();
    }

    [PlaythroughFact(Long = true)]
    [Trait("Kind", "Playthrough")]
    [Trait("Run", "Long")]
    public void Playthrough_the_satellite_missions_ascent_leaves_the_fuel_the_trip_starts_with()
    {
        var pilot = new SatellitePilot();
        using var sim = new Simulation(Mission.DeploySatellite);
        Assert.True(sim.Run(pilot.Fly, _ => pilot.PhaseName == "Wait", maxSeconds: 1200), $"the satellite was never let go (phase {pilot.PhaseName})");
        var upper = sim.Flight.Vessels.Single(v => v is { Stage: 1, TopStage: 1 });
        var fuel = FuelKg(upper);
        output.WriteLine($"after {sim.Time:0} s the upper stage has {fuel:0} kg ({upper.Fuel * 100:0.0}%), periapsis {upper.Periapsis:0} m, apoapsis {upper.Apoapsis:0} m; the trip starts with {MoonTrip.AscentFuel:0} kg");
        Assert.InRange(fuel, MoonTrip.AscentFuel * 0.95f, MoonTrip.AscentFuel * 1.05f);
    }
}
