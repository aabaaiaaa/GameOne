using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Writes the moon trip as saved worlds for the GameDriver
/// (<c>tools/GameDriver/examples/gameone-moon-trip.txt</c>), to look at the trip in the running
/// game: Task 12's starts (<see cref="MoonTrip"/>) as they are, and a flight from the low orbit
/// flown by the trip's autopilots (<c>MoonPilots.cs</c>) captured along the way
/// (<see cref="Flight.Capture"/>). Each world has the upper stage flown in it (the first vessel)
/// and a satellite on a round orbit at the planet, for a small view there. Each is first flown a
/// few seconds here, to show it holds as written. The lander's worlds, for
/// <c>gameone-lander.txt</c>, are written by a test of their own here, into a folder of their own
/// (<see cref="Writes_the_lander_worlds"/>). Only with <c>GAMEONE_WRITE_MOON_WORLD</c> set to
/// the folder to write them into (the driver's <c>GAMEONE_WORLDS</c>), as <see cref="MoonWorld"/>;
/// otherwise skipped.
/// </summary>
/// <remarks>
/// The worlds are written (and so last played) in the order the driver's script plays them, the
/// first the oldest: the world list shows the newest first, and a world played is then the
/// newest, so the one to play next is always at the list's foot.
/// </remarks>
public sealed class MoonTripWorlds(ITestOutputHelper output)
{
    private static GroundBody Moon => GroundBody.Moon;
    private static GroundBody Planet => GroundBody.Planet;

    /// <summary>The stage's id in every world; the satellite at the planet's is <see cref="SatelliteId"/>.</summary>
    private const int StageId = 0, SatelliteId = 1;

    /// <summary>How far ahead the transfer window is in the low orbit's world (seconds): time for the script to fly the stage and open the map first.</summary>
    private const double WindowAhead = 90;

    /// <summary>The satellite at the planet: on a round orbit 9 km up, for a small view there.</summary>
    private static VesselState AtThePlanet() => UnwatchedTests.Satellite(SatelliteId, 9000f, 0f, 60f);

    /// <summary>A world at <paramref name="time"/> with <paramref name="stage"/> and the satellite at the planet.</summary>
    private static WorldState World(double time, VesselState stage) => MoonTrip.World(time, stage, AtThePlanet());

    /// <summary>
    /// <paramref name="world"/> flown a few seconds with nothing done, its stage still as it was
    /// (<paramref name="state"/>) at <paramref name="body"/>; then written as <paramref name="id"/>.
    /// </summary>
    private void Write(WorldState world, string id, string name, FlightState state = FlightState.Flying)
    {
        var body = Bodies.Named(world.Vessels[0].Body);
        using (var sim = new Simulation(Mission.FreePlay, world: world, body: body))
        {
            var vessel = sim.Flight.WorldVessels.Single(v => v.Id == StageId);
            sim.Step(new FlightInput(SwitchTo: vessel));
            sim.Run((_, _) => default, _ => false, maxSeconds: 5);
            Assert.Equal(state, vessel.State);
            Assert.Same(body, vessel.Body);
            var satellite = sim.Flight.WorldVessels.Single(v => v.Id == SatelliteId);
            Assert.Same(Planet, satellite.Body);
            output.WriteLine($"{id}: world time {sim.Flight.WorldTime:0} s; after 5 s at the {vessel.Body.Name}, {vessel.State}, {vessel.Altitude:0} m up, {vessel.Ground:0} m over the ground, "
                + $"{vessel.Velocity.Length():0.0} m/s over the ground, throttle {vessel.Throttle:0.00}, target {vessel.Target}; "
                + $"the sun {Sun.Height(body, vessel.CentreOfMass, sim.Flight.WorldTime):0.00} high there, landing lights {vessel.LandingLights:0.00}");
        }
        var saved = DriverWorlds.Write(world, id, name, output);
        Assert.Equal(world.Vessels[0].Body, saved.Vessels[0].Body);
    }

    /// <summary>The flight's world now, with only the stage and the satellite in it (not the new rocket on the pad), the stage first.</summary>
    private static WorldState Captured(Simulation sim)
    {
        var world = sim.Flight.Capture();
        return world with
        {
            Vessels = [world.Vessels.Single(v => v.Id == StageId), world.Vessels.Single(v => v.Id == SatelliteId)],
            NextVesselId = SatelliteId + 1, Flights = 1, Log = [],
        };
    }

    [MoonWorldFact]
    public void Writes_the_moon_trip_worlds()
    {
        // Task 12's starts: the low orbit's with its window further ahead than the tests', to look at it shaded before it comes.
        var low = MoonTrip.LowOrbit(StageId, MoonTrip.Start, WindowAhead) with { Target = NavTarget.OfBody(Moon) };
        var (arriving, time) = MoonTrip.Arriving(StageId);

        // The trip flown from the low orbit, captured along the way.
        using var sim = new Simulation(Mission.FreePlay, world: World(MoonTrip.Start, MoonTrip.LowOrbit(StageId, MoonTrip.Start)), body: Planet);
        var stage = sim.Flight.WorldVessels.Single(v => v.Id == StageId);
        sim.Step(new FlightInput(SwitchTo: stage));
        sim.Run((_, _) => default, f => f.Controlled == stage, maxSeconds: 1);
        var transfer = new TransferPilot();
        Assert.True(sim.Run(transfer.Fly, _ => transfer.BurnEnded is not null && stage.Throttle == 0f, maxSeconds: 120), $"the transfer burn never ended ({transfer.Now})");
        var burnt = Captured(sim);
        Assert.True(sim.Run(transfer.Fly, f => f.Body == Moon, maxSeconds: 1200), $"never reached the moon's sphere ({transfer.Now})");
        var capture = new CapturePilot();
        Assert.True(sim.Run(capture.Fly, _ => capture.Now == CapturePilot.Phase.Done, maxSeconds: 400), $"never captured ({capture.Now})");
        var landing = new MoonLandingPilot();
        // The way down: its de-orbit burn just done, falling towards the spot with its engine off (the landing's hold yet to brake it).
        Assert.True(sim.Run(landing.Fly, _ => landing.Now == MoonLandingPilot.Phase.Land && stage.Throttle == 0f, maxSeconds: 600), $"the de-orbit burn never ended ({landing.Now})");
        var wayDown = Captured(sim);
        Assert.True(sim.Run(landing.Fly, _ => stage.State != FlightState.Flying, maxSeconds: 300), $"never came down ({landing.Now})");
        Assert.Equal(FlightState.Landed, stage.State);
        Assert.True(sim.Run((_, _) => default, _ => stage.Craft.Frozen, maxSeconds: 10), "it never froze");
        var landed = Captured(sim);

        // In the order the script plays them (see the remarks above).
        Write(World(MoonTrip.Start, low), "moon-trip-low-orbit", "The moon trip: a low orbit, the moon targeted");
        Write(burnt, "moon-trip-burnt", "The moon trip: the transfer burn done");
        Write(World(time, arriving), "moon-trip-arriving", "The moon trip: arriving at the moon");
        Write(World(MoonTrip.Start, MoonTrip.MoonOrbit(StageId, MoonTrip.MoonHeight)), "moon-trip-moon-orbit", "The moon trip: in orbit round the moon");
        Write(wayDown, "moon-trip-way-down", "The moon trip: on the way down to the moon");
        Write(landed, "moon-trip-landed", "The moon trip: landed on the moon", FlightState.Landed);
    }

    /// <summary>
    /// The lander's worlds, for <c>tools/GameDriver/examples/gameone-lander.txt</c> (a folder of
    /// their own: <c>--filter "FullyQualifiedName~Writes_the_lander_worlds"</c>), each with the
    /// satellite at the planet: the stack round the moon (<see cref="MoonTrip.MoonOrbit"/>, the
    /// lander on), 60° short of the spot; the lander alone on its way down, its de-orbit burn just
    /// done; and the lander landed on the spot, its legs out and its doors shut. The last two are
    /// captured from the stack flown on by the mission's pilot from the release
    /// (<see cref="LanderMissionPilot"/>), so the upper stage left round the moon is in them too.
    /// They are by day at the spot with the planet in its sky (<see cref="FairDay"/>).
    /// </summary>
    [MoonWorldFact]
    public void Writes_the_lander_worlds()
    {
        var stack = MoonTrip.MoonOrbit(StageId, MoonTrip.MoonHeight, lander: true);
        var fair = FairDay(stack.Position);
        using var sim = new Simulation(Mission.FreePlay, world: World(fair, stack), body: Moon);
        var lander = sim.Flight.WorldVessels.Single(v => v.Id == StageId);
        sim.Step(new FlightInput(SwitchTo: lander));
        sim.Run((_, _) => default, f => f.Controlled == lander, maxSeconds: 1);
        var pilot = new LanderMissionPilot(from: LanderMissionPilot.Phase.Release);
        // The way down: the lander alone, its de-orbit burn just done, falling towards the spot with its engine off (the landing's hold yet to brake it).
        Assert.True(sim.Run(pilot.Fly, _ => pilot.Landing.Now == MoonLandingPilot.Phase.Land && lander.Throttle == 0f, maxSeconds: 900),
            $"the de-orbit burn never ended ({pilot.Now}, {pilot.Landing.Now})");
        var upper = pilot.UpperStage ?? throw new Xunit.Sdk.XunitException("no upper stage left behind");
        var wayDown = Captured(sim, upper.Id);
        // Landed: the pilot let go of as it comes down, before it opens the doors.
        Assert.True(sim.Run(pilot.Fly, _ => lander.State != FlightState.Flying, maxSeconds: 300), $"never came down ({pilot.Landing.Now})");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(sim.Run((_, _) => default, _ => lander.Craft.Frozen, maxSeconds: 10), "it never froze");
        Assert.True(lander.LegsWanted, "landed with its legs in");
        Assert.False(lander.BayDoorsWanted, "its doors were opened");
        Assert.Equal(FlightState.Flying, upper.State);
        var t = sim.Flight.WorldTime;
        output.WriteLine($"landed at {t:0} s ({t - fair:0} s after the stack's world), {PointOfInterest.MoonLandingSpot.DistanceFrom(lander, t):0.0} m from the spot; "
            + $"the sun up there {SpotSky.Lit(t, true)}, the planet in its sky {SpotSky.PlanetSeen(t)}");
        Assert.True(SpotSky.Lit(t, true) && SpotSky.PlanetSeen(t), "landed with the sun down or the planet out of its sky");
        var landed = Captured(sim, upper.Id);

        // In the order the script plays them (see the remarks above).
        Write(World(fair, stack), "lander-moon-orbit", "The lander: in orbit round the moon");
        Write(wayDown, "lander-way-down", "The lander: on the way down to the moon");
        Write(landed, "lander-landed", "The lander: landed at the spot", FlightState.Landed);
    }

    /// <summary>
    /// <see cref="Captured(Simulation)"/> with the vessel numbered <paramref name="also"/> (the upper
    /// stage left round the moon) kept too, after the stage.
    /// </summary>
    private static WorldState Captured(Simulation sim, int also)
    {
        var world = sim.Flight.Capture();
        return world with
        {
            Vessels = [world.Vessels.Single(v => v.Id == StageId), world.Vessels.Single(v => v.Id == also), world.Vessels.Single(v => v.Id == SatelliteId)],
            NextVesselId = Math.Max(also, SatelliteId) + 1, Flights = 1, Log = [],
        };
    }

    /// <summary>
    /// How long after the stack's world the lander is down on the spot, near enough (seconds): the
    /// release takes the spot past where the line-up begins, so it waits a lap first.
    /// </summary>
    private const double DownAfter = 570;

    /// <summary>
    /// The fair time at the spot (<see cref="SpotSky.FairTime"/>) within a year of the trip's start
    /// with the sun highest over both the stack at <paramref name="stack"/> then and the spot as the
    /// lander comes down (<see cref="DownAfter"/> on), the lower of the two the higher, with the
    /// planet still in the spot's sky then: the first fair time is the spot's dawn, with the stack,
    /// 60° back round the moon, in its night, and the two are never both much over 12° up.
    /// </summary>
    private static double FairDay(Vector3 stack)
    {
        var year = Scene.YearDays * (double)Scene.DaySeconds;
        var (best, height) = (double.NaN, float.MinValue);
        for (var t = SpotSky.FairTime(MoonTrip.Start); t < MoonTrip.Start + year; t = SpotSky.FairTime(t))
        {
            var lower = MathF.Min(Sun.Height(Moon, stack, t), Sun.Height(Moon, SpotSky.Place, t + DownAfter));
            if (lower > height && SpotSky.PlanetSeen(t + DownAfter))
                (best, height) = (t, lower);
        }
        return best;
    }
}
