using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The satellite mission, end to end: three vessels from one rocket, each flown in turn. The
/// booster and upper stage both land, and the satellite raises itself to its orbit, 10 km up.
/// </summary>
public sealed class SatelliteTests(ITestOutputHelper output, SatelliteTests.OnTheZoneFlight onTheZone) : IClassFixture<SatelliteTests.OnTheZoneFlight>
{
    /// <summary>
    /// The satellite mission flown once with the plain pilot (landing on the zone), shared by the
    /// two tests that need that flight: the first of them to run flies it, the other reads it. The
    /// class's tests run one after another in one process, so there is no race, and xUnit disposes
    /// the flight's simulation after the last of them.
    /// </summary>
    public sealed class OnTheZoneFlight : IDisposable
    {
        private readonly object _gate = new();
        private Flown? _flown;

        /// <summary>What the flight left: the simulation (the flight, at its end), the score taken the moment it ended, and what was watched on the way.</summary>
        internal sealed record Flown(Simulation Sim, int? Score, int LitInBand, bool DoneLit);

        internal Flown Get(Func<Flown> fly)
        {
            lock (_gate)
                return _flown ??= fly();
        }

        public void Dispose() => _flown?.Sim.Dispose();
    }

    private static readonly Pilot Climb = (f, _) => f.State == FlightState.Ready ? new FlightInput(Launch: true, FullThrottle: true) : default;

    [PlaythroughFact(Long = true)]
    [Trait("Kind", "Playthrough")]
    [Trait("Run", "Long")]
    public void The_satellite_reaches_orbit_and_the_booster_and_upper_stage_both_land()
    {
        var (sim, _, litInBand, doneLit) = FlyTheMissionOnTheZone();
        var flight = sim.Flight;
        output.WriteLine($"frames burning with its orbit already in the band, before the step was done: {litInBand}");
        Assert.True(litInBand > 0, "its orbit never came into the band with the engine still burning: this shows nothing");
        Assert.False(doneLit, "\"Raise its orbit\" was done with the engine still burning");
        Assert.Equal(3, flight.Vessels.Count(v => v.Guided));
        Assert.Equal(2, flight.Vessels.Count(v => v.Name == "Fairing half"));
        Assert.True(flight.Run!.Succeeded, "the mission didn't succeed");
        Assert.All(flight.Mission.Steps.Select((step, i) => (step, i)), x => Assert.True(flight.Run!.Progress.IsDone(x.i), $"not done: {x.step.Label}"));
        var upper = flight.Vessels.Single(v => v is { Stage: 1, TopStage: 1 });
        Assert.True(upper.Fuel > 0.05f, $"the upper stage landed with only {upper.Fuel * 100:0}% fuel: too little to spare");

        // Orbits run east, so a return from orbit comes in from the west, over the Westwall range
        // (its crest ~2.5 km west of the pad). That it can be done is all this asks: whatever
        // stops it landing on the zone (the mountains or anything else) fails it.
        output.WriteLine($"{upper.State} {upper.CrashReason}, {upper.DistanceToLandingSite:0.0} m off, touchdown {upper.TouchdownSpeed:0.0} m/s");
        Assert.Equal(FlightState.Landed, upper.State);
        Assert.True(upper.LandedOnTarget, $"it came down {upper.DistanceToLandingSite:0} m off the zone's centre");
        Assert.Equal(Scene.LandingZoneCentre, upper.LandingSite);

        // The satellite stays where it was put: a whole lap longer, coasting, inside the band.
        var satellite = flight.Vessels.Single(v => v.Stage == 2);
        Assert.True(satellite.Fuel > 0.2f, $"the satellite used all but {satellite.Fuel * 100:0}% of its fuel: too little to spare");
        var (lowest, highest) = (float.MaxValue, float.MinValue);
        sim.Run((_, _) => default, _ =>
        {
            // Above the sea, as the orbit is: the ground under it rises and falls.
            lowest = MathF.Min(lowest, Planet.Altitude(satellite.Position));
            highest = MathF.Max(highest, Planet.Altitude(satellite.Position));
            return false;
        }, maxSeconds: 600);
        output.WriteLine($"satellite over a lap: {lowest:0} to {highest:0} m");
        Assert.Equal(FlightState.Flying, satellite.State);
        Assert.InRange(lowest, Scene.SatelliteOrbitLow, Scene.SatelliteOrbitHigh);
        Assert.InRange(highest, Scene.SatelliteOrbitLow, Scene.SatelliteOrbitHigh);
    }

    [Fact]
    public void The_upper_stage_leaves_the_satellite_too_low_and_its_own_engine_has_to_raise_it()
    {
        using var sim = new Simulation(Mission.DeploySatellite);
        var pilot = new SatellitePilot();
        var flight = sim.Flight;
        sim.Run(pilot.Fly, _ => pilot.PhaseName == "Wait", maxSeconds: 600);
        var satellite = flight.Vessels.Single(v => v is { Stage: 2, TopStage: 2 });
        var raise = Array.FindIndex(flight.Mission.Steps, s => s.Label.StartsWith("Raise"));
        output.WriteLine($"satellite left at {satellite.Periapsis:0} to {satellite.Apoapsis:0} m");
        Assert.True(satellite.OrbitStable, "the upper stage should leave the satellite in orbit");
        Assert.False(flight.Run!.Progress.IsDone(raise), "the upper stage alone shouldn't get the satellite high enough");
    }

    [PlaythroughFact(Long = true)]
    [Trait("Kind", "Playthrough")]
    [Trait("Run", "Long")]
    public void Landing_the_upper_stage_away_from_the_landing_zone_still_succeeds_but_scores_less()
    {
        // The flight on the zone is the cover test's, flown once for both.
        var onPad = FlyTheMissionOnTheZone().Score;
        int? offPad;

        // Down safely, but 1.2 km north of the pad, on level ground. (Not short of it, to the west:
        // there the foothills of the range rise steeply from ~800 m out, too steep to stand on.)
        var home = Scene.LandingZoneCentre + Planet.North(Scene.LandingZoneCentre) * 1200f;
        using (var sim = FlyTheMission(new SatellitePilot(home)))
        {
            var flight = sim.Flight;
            offPad = flight.Run!.Score;
            Assert.True(flight.Run!.Succeeded, "a safe landing anywhere should do");
            Assert.False(flight.Run!.Progress.IsDone(flight.Mission.Steps.Length - 1), "it came down on the landing zone after all");
        }
        output.WriteLine($"scores: on the pad {onPad}, off it {offPad}");
        Assert.True(onPad > offPad, "landing on the pad should score more");
    }

    [Fact]
    public void Releasing_the_satellite_leaves_the_upper_stage_behind_with_its_legs()
    {
        using var sim = new Simulation(Mission.DeploySatellite);
        var flight = sim.Flight;
        // Just off the pad, still slow enough that the bare satellite doesn't mind the thick air.
        sim.Run(Climb, f => f.Ground > 30f, maxSeconds: 30);
        sim.Step(new FlightInput(Stage: true)); // the booster
        sim.Run((_, _) => default, _ => false, maxSeconds: 0.5);
        sim.Step(new FlightInput(Stage: true)); // the fairing
        sim.Step(new FlightInput(Stage: true)); // and the satellite

        Assert.Equal(3, flight.Vessels.Count(v => v.Guided));
        Assert.Equal("Satellite", flight.Controlled.Name); // the pilot flies on with the satellite
        Assert.False(flight.CanStage);
        var upper = flight.Vessels.Single(v => v is { Stage: 1, TopStage: 1 });
        Assert.Equal("Upper stage", upper.Name);
        Assert.True(upper.HasLegs);
        Assert.True(upper.Guided);
        Assert.Equal(Scene.LandingZoneCentre, upper.LandingSite);
        Assert.Null(flight.Controlled.LandingSite);        // the satellite isn't to land anywhere
    }

    [Fact]
    public void Staging_jettisons_the_fairing_in_two_halves_before_the_satellite_is_let_go()
    {
        using var sim = new Simulation(Mission.DeploySatellite);
        var flight = sim.Flight;
        sim.Run(Climb, f => f.Ground > 30f, maxSeconds: 30);
        Assert.True(flight.FairingOn);
        Assert.False(flight.JettisonsFairing); // with the booster still on, Space drops the booster
        sim.Step(new FlightInput(Stage: true));
        Assert.True(flight.FairingOn);
        sim.Run((_, _) => default, _ => false, maxSeconds: 0.5);

        var mass = flight.Mass;
        var height = flight.Controlled.Height;
        Assert.True(flight.JettisonsFairing);
        sim.Step(new FlightInput(Stage: true));
        Assert.False(flight.FairingOn);
        Assert.Equal(Scene.FairingMass, mass - flight.Mass, 1f);
        Assert.True(flight.Controlled.Height < height, "without its fairing the stack should be shorter");
        Assert.Equal("Upper stage", flight.Controlled.Name); // still flying the upper stage, satellite on top

        var halves = flight.Vessels.Where(v => v.Name == "Fairing half").ToArray();
        Assert.Equal(2, halves.Length);
        Assert.All(halves, half => Assert.False(half.Guided));
        Assert.All(halves, half => Assert.Null(half.LandingSite));

        // They drift apart, fall, and are gone once down: no explosion.
        sim.Run((_, _) => default, _ => false, maxSeconds: 1);
        Assert.True(Vector3.Distance(halves[0].Position, halves[1].Position) > 2f, "the halves should come apart");
        var exploded = false;
        sim.Run((_, _) => default, f =>
        {
            foreach (var sound in f.Sounds)
                exploded |= sound.Cue == SoundCue.Explosion && halves.Any(h => Vector3.Distance(h.CrashPosition, sound.Position) < 1f);
            return halves.All(h => h.State == FlightState.Crashed);
        }, maxSeconds: 60);
        Assert.All(halves, half => Assert.Equal(FlightState.Crashed, half.State));
        Assert.False(exploded, "a fairing half has nothing in it to blow up");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_fairing_collides_until_it_is_jettisoned(bool fairingOn)
    {
        // The upper stage and satellite, upside down just over the launch pad: it comes to rest on
        // the tip of the fairing if that is on, or on the satellite's antenna, lower, if it is off.
        var stack = Vehicle.ThreeStage with { Stages = Vehicle.ThreeStage.Stages[1..] };
        var fairingTop = RocketGeometry.StageBase(stack, 0, 1) + Scene.FairingHeight + Scene.FairingNoseHeight;
        var antennaTop = RocketGeometry.StackHeight(stack, 0);
        using var physics = new RocketPhysics(GroundBody.Planet);
        physics.Reset(Vehicle.Booster, LaunchSites.Home);
        physics.Remove(physics.Crafts[0]);
        var upsideDown = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        var craft = physics.AddLoose(stack, new Vector3(0, Scene.PadTop + fairingTop + 0.5f, 0), upsideDown, Vector3.Zero, Vector3.Zero);
        Assert.True(craft.FairingOn);
        if (!fairingOn)
            physics.JettisonFairing(craft);
        for (var i = 0; i < 90; i++)
            physics.Update(1f / 60, []);

        var rest = physics.Position(craft).Y - Scene.PadTop; // its origin is its top now
        output.WriteLine($"fairing {(fairingOn ? "on" : "off")}: rests {rest:0.00} m up (fairing tip {fairingTop:0.00}, antenna {antennaTop:0.00})");
        Assert.Equal(fairingOn ? fairingTop : antennaTop, rest, 0.4f);
    }

    [Fact]
    public void Jettisoned_too_low_and_fast_the_satellite_breaks_up_in_the_air()
    {
        using var sim = new Simulation(Mission.DeploySatellite);
        var flight = sim.Flight;
        sim.Run(Climb, f => f.Ground > 1200f, maxSeconds: 60); // about where the booster is dropped
        sim.Step(new FlightInput(Stage: true)); // the booster
        sim.Run((_, _) => default, _ => false, maxSeconds: 0.5);
        var stack = flight.Controlled;
        output.WriteLine($"jettisoned at {flight.Ground:0} m, {flight.Velocity.Length():0} m/s");
        sim.Step(new FlightInput(Stage: true)); // the fairing, far too soon
        sim.Step(default);
        Assert.Equal(FlightState.Crashed, stack.State);
        Assert.Contains("fairing", stack.CrashReason);
    }

    /// <summary>
    /// The satellite mission flown with the plain pilot (landing on the zone), by whichever test
    /// asks first, the score taken as the flight ends (before anything flies on). Watched as it
    /// goes: "Raise its orbit" isn't done while the satellite's engine is still burning, even with
    /// its orbit already inside the band (the burn's last moments), only once the engine is off.
    /// </summary>
    private OnTheZoneFlight.Flown FlyTheMissionOnTheZone() => onTheZone.Get(() =>
    {
        var raise = Array.FindIndex(Mission.DeploySatellite.Steps, s => s.Label.StartsWith("Raise"));
        var (litInBand, doneLit, wasDone) = (0, false, false);
        var sim = FlyTheMission(new SatellitePilot(), f =>
        {
            if (f.Vessels.FirstOrDefault(v => v is { Stage: 2, TopStage: 2 }) is not { } satellite)
                return;
            var done = f.Run!.Progress.IsDone(raise);
            if (!wasDone && satellite is { EngineLit: true, Periapsis: >= Scene.SatelliteOrbitLow, Apoapsis: <= Scene.SatelliteOrbitHigh })
                litInBand++;
            doneLit |= done && !wasDone && satellite.EngineLit;
            wasDone = done;
        });
        return new OnTheZoneFlight.Flown(sim, sim.Flight.Run!.Score, litInBand, doneLit);
    });

    /// <summary>Flies the mission with <paramref name="pilot"/> until the booster and upper stage are both down (or something crashed), showing <paramref name="watch"/> the flight after each frame.</summary>
    private Simulation FlyTheMission(SatellitePilot pilot, Action<Flight>? watch = null)
    {
        var sim = new Simulation(Mission.DeploySatellite);
        var flight = sim.Flight;
        var next = 0.0;
        var phase = "";
        sim.Run(pilot.Fly, f =>
        {
            watch?.Invoke(f);
            if (pilot.PhaseName != phase || sim.Time >= next)
            {
                phase = pilot.PhaseName;
                next = sim.Time + 30;
                output.WriteLine($"{sim.Time,6:0.0} s  {phase,-8} " + string.Join("  |  ", f.Vessels.Select(v =>
                    $"{v.Name}: {v.State} {v.Ground:0} m, fuel {v.Fuel * 100:0}%, {v.DistanceToLandingSite:0} m off")));
            }
            return pilot.PhaseName == "Done" || f.Vessels.Any(v => v is { Guided: true, State: FlightState.Crashed });
        }, maxSeconds: 1200);

        foreach (var vessel in flight.Vessels)
            output.WriteLine($"{vessel.Name}: {vessel.State} {vessel.CrashReason}, fuel {vessel.Fuel * 100:0}%, {vessel.DistanceToLandingSite:0.0} m off, touchdown {vessel.TouchdownSpeed:0.0} m/s");
        output.WriteLine($"after {sim.Time:0} s; score {flight.Run!.Score}");
        for (var i = 0; i < flight.Mission.Steps.Length; i++)
            output.WriteLine($"  [{(flight.Run!.Progress.IsDone(i) ? "x" : " ")}] {flight.Mission.Steps[i].Label}");
        return sim;
    }
}
