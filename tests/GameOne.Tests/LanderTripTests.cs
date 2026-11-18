using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The lander launcher's ascent into the moon trip's tilted low orbit (<see cref="MoonTrip.Launch"/>,
/// <see cref="PlaneAscentPilot"/>): Home's pad passes under the trip's moving plane, every launch
/// site is surveyed for it (for the player's information: the mission is flown from Home), and the
/// lander launcher climbs into it from Home's pad with the fuel the trip starts with
/// (<see cref="MoonTrip.LanderAscentFuel"/>), its transfer window coming round within a lap with
/// the trip's plane still its own. Then the lander's trip, each leg from where it is about: the
/// stack captured round the moon, the lander let go there, flown down by the spot and its bay
/// opened; and (long) the whole trip, and the whole mission flown from the pad
/// (<see cref="LanderMissionPilot"/>), each with the fuel to spare it is flown with
/// (<see cref="UpperStageMargin"/>, <see cref="LanderMargin"/>).
/// </summary>
public sealed class LanderTripTests(ITestOutputHelper output)
{
    /// <summary>
    /// Where the lander launcher's booster came down from Home's pad in the playthrough below
    /// (flown back to where it would come down left alone, before Home had a drone ship there):
    /// measured once, and written here. <see cref="Scene.TripDroneShipCentre"/> is placed by it.
    /// </summary>
    internal static readonly Vector3 BoosterCameDown = new(1431.2f, -230.2f, 1337.5f);

    /// <summary>
    /// How far round the ground (m) from its pad the lander launcher's booster comes down on the
    /// climb into the trip's plane, and how far further off due east than the climb's heading
    /// (degrees: the ground's spin across the plane, not yet burnt away when it is dropped, carries
    /// it on): from the playthrough below (<see cref="BoosterCameDown"/>, 1,972 m out at 43.1° off
    /// due east, the climb heading 31.1°), taken to hold on any heading.
    /// </summary>
    private const float BoosterDownrange = 1972f, BoosterSwingDegrees = 12f;

    /// <summary>
    /// How near (m) a drone ship must be to where the booster comes down for it to be flown onto it:
    /// what its divert low down brings back (<see cref="Autopilot.ReturningBooster"/>'s, 400 m), and
    /// a little of its boost-back's. (Flown at Home's first drone ship, some 1.6 km off, it crashed.)
    /// </summary>
    internal const float BoosterReach = 500f;

    /// <summary>How far ahead the survey looks for each site's launches (seconds), and how many it takes: two a site, a descending and an ascending crossing as Home's come.</summary>
    private const double SurveySeconds = 3000;
    private const int SurveyLaunches = 2;

    /// <summary>The distance along the planet's sea between two places, by the angle between their ups.</summary>
    private static float Along(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Planet.Up(a), Planet.Up(b)), -1f, 1f)) * Planet.SeaRadius;

    /// <summary>The way along the plane of <paramref name="normal"/> (among <paramref name="burn"/>'s stars) from <paramref name="pad"/> at world time <paramref name="t"/>, across the ground.</summary>
    private static Vector3 Way(Vector3 pad, Vector3 normal, double t, double burn) =>
        Vector3.Normalize(Vector3.Cross(MoonTrip.AmongStars(normal, burn, t), Planet.Up(pad)));

    /// <summary>A way across the ground at <paramref name="pad"/>, in degrees off due east, north positive.</summary>
    private static float OffEast(Vector3 pad, Vector3 way) =>
        float.RadiansToDegrees(MathF.Atan2(Vector3.Dot(way, Planet.North(pad)), Vector3.Dot(way, Planet.East(pad))));

    /// <summary>Where on the calm sea's level <paramref name="metres"/> along the ground from <paramref name="pad"/> heading <paramref name="way"/> is.</summary>
    private static Vector3 Out(Vector3 pad, Vector3 way, float metres)
    {
        var up = Planet.Up(pad);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(up, way)), metres / Planet.SeaRadius);
        return Planet.Centre + Vector3.Transform(up, turn) * Planet.SeaRadius;
    }

    /// <summary>How far (degrees) <paramref name="pad"/> is from the trip's plane for the transfer burnt at <paramref name="burn"/>, at world time <paramref name="t"/>.</summary>
    private static float FromThePlane(Vector3 pad, double t, double burn) =>
        float.RadiansToDegrees(MathF.Asin(MathF.Abs(Vector3.Dot(MoonTrip.TripNormal(burn), Vector3.Normalize(MoonTrip.AmongStars(pad - Planet.Centre, t, burn))))));

    /// <summary>
    /// How far (degrees) the stack launched from <paramref name="pad"/> at <paramref name="t"/> into
    /// the plane of <paramref name="normal"/> is at the transfer's <paramref name="burn"/> from where
    /// <see cref="MoonTrip.LowOrbit"/> puts the stage at that burn: the ascent's end
    /// <see cref="MoonTrip.AscentDegrees"/> round from the pad's place at lift-off, then round on
    /// its orbit by <see cref="MoonTrip.AscentLapSeconds"/> a lap for the rest of the wait.
    /// </summary>
    private static float FromTheBurnsPlace(Vector3 pad, double t, Vector3 normal, double burn)
    {
        var p = MoonTrip.AmongStars(pad - Planet.Centre, t, burn);
        var flat = Vector3.Normalize(p - normal * Vector3.Dot(p, normal));
        var round = MoonTrip.AscentDegrees + 360.0 * (burn - t - MoonTrip.AscentSeconds) / MoonTrip.AscentLapSeconds;
        var stack = Vector3.Transform(flat, Quaternion.CreateFromAxisAngle(normal, (float)double.DegreesToRadians(round % 360.0)));
        var stage = Vector3.Normalize(MoonTrip.LowOrbit(1, burn, wait: 0).Position.ToSingle() - Planet.Centre); // nose up: its body straight over its centre of mass
        return float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(stack, stage), -1f, 1f)));
    }

    /// <summary>The moon's lap round the planet, in seconds.</summary>
    private static double MoonLap => Bodies.OrbitOf(GroundBody.Moon, Scene.ClockStart).Period;

    /// <remarks>
    /// Home's third launch (some 1,944 s) comes some 110 s before the wait for its transfer's burn
    /// goes round from the least to the next meeting, most of a lap more (some 2,055 s): tried in
    /// one step holding both the crossing and the wait's going round, as
    /// <see cref="MoonTrip.Launch"/>'s usual step could hold them at another site or time, it is
    /// found as the usual step finds it. The times move with the measured ascent: if the step no
    /// longer holds the wait's going round, it says so.
    /// </remarks>
    [Fact]
    public void A_launch_just_before_its_wait_goes_round_is_found_in_the_same_step() => InOneStep(1900, 200);

    /// <remarks>
    /// Long (some 9 s: the usual search goes on over 500 s to it), beside the quick case above:
    /// the same with Home's fourth launch (some 2,534 s), after the wait goes round, which the
    /// quick case leaves out: its step's crossing on the lap the wait goes round to.
    /// </remarks>
    [LongFact]
    [Trait("Run", "Long")]
    public void A_launch_after_its_wait_goes_round_is_found_in_the_same_step() => InOneStep(2000, 560);

    /// <summary>
    /// The launch from Home's pad <see cref="MoonTrip.Launch"/> finds in one step of
    /// <paramref name="step"/> seconds from <paramref name="after"/>, the wait going round in it, is
    /// the one its usual step finds.
    /// </summary>
    private void InOneStep(double after, double step)
    {
        var pad = (Vector3Double)(LaunchSites.Home.Pad - Planet.Centre);
        var start = MoonTrip.Meet(pad, after, MoonTrip.Wait, null);
        var end = MoonTrip.Meet(pad, after + step, MoonTrip.Wait, start.Offsets);
        output.WriteLine($"the wait {start.Wait:0.0} s at {after:0} s, {end.Wait:0.0} s at {after + step:0} s");
        Assert.True(end.Wait - start.Wait > MoonTrip.AscentLapSeconds / 2, $"the wait doesn't go round between {after:0} s and {after + step:0} s: pick the step again round it");
        var usual = MoonTrip.Launch(after) ?? throw new InvalidOperationException("no launch");
        var one = MoonTrip.Launch(after, within: step, step: step) ?? throw new InvalidOperationException($"no launch found in the one step from {after:0} s");
        output.WriteLine($"by the usual step: {usual.Time:0.000} s, its burn {usual.Burn:0.000} s; in one step: {one.Time:0.000} s, its burn {one.Burn:0.000} s");
        Assert.True(usual.Time < after + step, $"the launch at {usual.Time:0.0} s is not in the step");
        Assert.Equal(usual.Time, one.Time, 0.01);
        Assert.Equal(usual.Burn, one.Burn, 0.01);
    }

    [Fact]
    public void Home_passes_under_the_trips_moving_plane_heading_south_of_east_and_north_of_it_in_turn()
    {
        // The first two launches after the clock's start: each with the pad in the plane the trip
        // has at its burn, one up its descending crossing and one its ascending, under a moon lap apart.
        var pad = LaunchSites.Home.Pad;
        var first = MoonTrip.Launch(Scene.ClockStart) ?? throw new InvalidOperationException("no launch");
        var second = MoonTrip.Launch(first.Time + 1) ?? throw new InvalidOperationException("no second launch");
        var headings = new List<float>();
        foreach (var (time, normal, burn) in new[] { first, second })
        {
            var off = FromThePlane(pad, time, burn);
            var heading = OffEast(pad, Way(pad, normal, time, burn));
            headings.Add(heading);
            output.WriteLine($"launch at {time:0.0} s, its burn at {burn:0.0} s: the pad {off:0.0000}° from the plane, {float.RadiansToDegrees(MathF.Acos(Vector3.Dot(normal, Planet.NorthPole))):0.0}° from the equator; heading {heading:+0.0;-0.0}° off due east");
            Assert.True(off < 0.1f, $"the pad {off:0.000}° from the plane at {time:0.0} s");
            // The stack, gone round from where the ascent ends to the burn, is where the trip's own
            // start (MoonTrip.LowOrbit, found afresh) has the stage at that burn.
            var meets = FromTheBurnsPlace(pad, time, normal, burn);
            output.WriteLine($"  the stack at the burn {meets:0.000}° from the low orbit's burn's place, {burn - time - MoonTrip.AscentSeconds:0.0} s after the ascent's end");
            Assert.True(meets < 0.1f, $"the stack {meets:0.000}° from the burn's place at {burn:0.0} s");
        }
        var gap = second.Time - first.Time;
        output.WriteLine($"{gap:0} s between them, against a moon lap of {MoonLap:0} s and a planet turn of {GroundBody.Planet.TurnSeconds:0} s");
        Assert.True(headings.Min() < 0f && headings.Max() > 0f, $"headings {string.Join(", ", headings.Select(h => $"{h:+0.0;-0.0}°"))}");
        Assert.True(gap < MoonLap, $"{gap:0} s between them");
    }

    /// <remarks>
    /// Long (some 90 s: every launch is found on the patched path, the plane and the burn for each
    /// time tried), beside the quick check of Home's own launches above; it leaves out nothing of
    /// Home's but the table, and every other site, which is information for the player only.
    /// </remarks>
    [LongFact]
    [Trait("Run", "Long")]
    public void Each_launch_site_under_the_trips_moving_plane_is_surveyed()
    {
        // Each site's launches into the trip's plane as it moves (MoonTrip.Launch from its own pad,
        // the ascent reckoned as Home's) over SurveySeconds from the clock's start, each heading as
        // flown then, and where the booster comes down on it against the drone ships.
        output.WriteLine($"The booster down {BoosterDownrange:#,0} m out, {BoosterSwingDegrees:0}° further off due east than the heading; a drone ship within {BoosterReach:0} m of it in reach. Launches over {SurveySeconds:#,0} s from {Scene.ClockStart:0} s:");
        output.WriteLine("| Site | Latitude | Launch (world time) | Plane from the equator | Heading off due east | Nearest drone ship to the booster | In reach |");
        output.WriteLine("|---|---|---|---|---|---|---|");
        var homeLaunches = 0;
        foreach (var site in LaunchSites.All)
        {
            // Its own drone ships, and Home's (whose missions' landing sites they are wherever it is launched).
            Vector3[] ships = [.. site.DroneShips, .. site == LaunchSites.Home ? [] : LaunchSites.Home.DroneShips];
            var count = 0;
            for (var after = Scene.ClockStart; count < SurveyLaunches && MoonTrip.Launch(after, site, Scene.ClockStart + SurveySeconds - after) is (var time, var normal, var burn); after = time + 1)
            {
                count++;
                var offEast = OffEast(site.Pad, Way(site.Pad, normal, time, burn));
                var bearing = float.DegreesToRadians(offEast + MathF.Sign(offEast) * BoosterSwingDegrees);
                var down = Out(site.Pad, Planet.East(site.Pad) * MathF.Cos(bearing) + Planet.North(site.Pad) * MathF.Sin(bearing), BoosterDownrange);
                var nearest = ships.Length == 0 ? float.PositiveInfinity : ships.Min(s => Along(s, down));
                var tilt = float.RadiansToDegrees(MathF.Acos(Vector3.Dot(normal, Planet.NorthPole)));
                output.WriteLine($"| {site.Name} | {site.Latitude:0}° | {time:#,0} s | {tilt:0.0}° | {offEast:+0.0;-0.0}° | {(float.IsInfinity(nearest) ? "none" : $"{nearest:#,0} m")} | {(nearest <= BoosterReach ? "yes" : "no")} |");
            }
            if (count == 0)
                output.WriteLine($"| {site.Name} | {site.Latitude:0}° | never | - | - | - | - |");
            if (site == LaunchSites.Home)
                homeLaunches = count;
        }
        Assert.True(homeLaunches >= 2, $"Home has {homeLaunches} launches in {SurveySeconds:0} s");
    }

    [PlaythroughFact(Long = true)]
    [Trait("Kind", "Playthrough")]
    [Trait("Run", "Long")]
    public void Playthrough_the_lander_launcher_climbs_into_the_trips_tilted_low_orbit() =>
        ClimbsIntoTheTripsPlane(MoonTrip.Launch(Scene.ClockStart) ?? throw new InvalidOperationException("Home's pad never under the trip's plane"), onTheShip: true);

    /// <remarks>
    /// The same climb from Home's next launch, at its ascending crossing (heading north of east),
    /// which <see cref="MoonTrip.Launch"/> times by the same <see cref="MoonTrip.AscentDegrees"/>
    /// and <see cref="MoonTrip.AscentLapSeconds"/>: the trip goes on from it as well. Its booster
    /// has no drone ship in reach on that heading, so where it comes down is reported, not asserted.
    /// </remarks>
    [PlaythroughFact(Long = true)]
    [Trait("Kind", "Playthrough")]
    [Trait("Run", "Long")]
    public void Playthrough_the_lander_launcher_climbs_into_the_trips_tilted_low_orbit_at_its_ascending_crossing()
    {
        var first = MoonTrip.Launch(Scene.ClockStart) ?? throw new InvalidOperationException("Home's pad never under the trip's plane");
        ClimbsIntoTheTripsPlane(MoonTrip.Launch(first.Time + 1) ?? throw new InvalidOperationException("no second launch"), onTheShip: false);
    }

    /// <summary>
    /// The lander launcher flown from Home's pad at <paramref name="at"/> (a <see cref="MoonTrip.Launch"/>)
    /// into the trip's plane: in it, its periapsis over the air, the next transfer window within a
    /// lap with the trip's plane then within 1° of the stack's, the fuel within 5% of
    /// <see cref="MoonTrip.LanderAscentFuel"/>; with <paramref name="onTheShip"/>, the booster
    /// landed on Home's second drone ship.
    /// </summary>
    private void ClimbsIntoTheTripsPlane((double Time, Vector3 Normal, double Burn) at, bool onTheShip)
    {
        var (launch, normal, burn) = at;
        var pilot = new PlaneAscentPilot(normal, burn);
        using var sim = new Simulation(Mission.LandOnTheMoon, world: new WorldState { Time = launch });
        var pad = LaunchSites.Home.Pad;
        output.WriteLine($"launched at {launch:0.0} s, heading {OffEast(pad, Way(pad, normal, launch, burn)):+0.0;-0.0}° off due east");
        Assert.True(sim.Run(pilot.Fly, _ => pilot.Done, maxSeconds: 1200), $"never in orbit (phase {pilot.PhaseName})");

        var stack = pilot.Stack!;
        var booster = pilot.Booster!;
        var flight = sim.Flight;
        var now = flight.WorldTime;
        var stars = Planet.AmongStars(stack.CentreOfMass, stack.Velocity);
        var mine = Vector3.Normalize(Vector3.Cross(stack.CentreOfMass - Planet.Centre, stars));
        var trip = MoonTrip.AmongStars(normal, burn, now);
        var off = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(mine, trip), -1f, 1f)));
        var fuel = stack.Fuel * stack.CurrentStage.FuelMass;
        output.WriteLine($"in orbit after {sim.Time:0.0} s (AscentSeconds {MoonTrip.AscentSeconds:0}): {off:0.000}° off the trip's plane, periapsis {stack.Periapsis:0} m, apoapsis {stack.Apoapsis:0} m; "
            + $"the upper stage has {fuel:0} kg (LanderAscentFuel {MoonTrip.LanderAscentFuel:0}); the fairing {(stack.FairingOn ? "on" : "gone")}, the lander {(stack.TopStage == 2 ? "on" : "gone")}");

        // What the trip starts from: the next transfer window on the orbit the stack is on, as the
        // line shades it (TransferPilot's), and how far the trip's plane then is from the stack's.
        var orbit = new Orbit(GroundBody.Planet, stack.CentreOfMass, stars);
        var window = new List<Encounters.WindowPoint>();
        Encounters.Transfer(orbit, Bodies.OrbitOf(GroundBody.Moon, now), GroundBody.Moon.SphereRadius, window);
        Assert.NotEmpty(window);
        var toWindow = orbit.TimeTo(window.MinBy(p => p.Miss).Anomaly);
        var atWindow = now + toWindow;
        var planes = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(MoonTrip.AmongStars(mine, now, atWindow), MoonTrip.TripNormal(atWindow)), -1f, 1f)));
        // How far round from the pad's place at lift-off the ascent ended (MoonTrip.AscentDegrees), among the stars now.
        var padThen = MoonTrip.AmongStars(pad - Planet.Centre, launch, now);
        var r = Vector3.Normalize(stack.CentreOfMass - Planet.Centre);
        var round = float.RadiansToDegrees(MathF.Atan2(Vector3.Dot(Vector3.Cross(Vector3.Normalize(padThen), r), mine), Vector3.Dot(Vector3.Normalize(padThen), r)));
        output.WriteLine($"the ascent ended {(round + 360f) % 360f:0.0}° round from the pad's place at lift-off (AscentDegrees {MoonTrip.AscentDegrees:0.0}), on a lap of {orbit.Period:0.0} s (AscentLapSeconds {MoonTrip.AscentLapSeconds:0})");
        output.WriteLine($"the transfer window {toWindow:0.0} s on, at {atWindow:0.0} s against the {burn:0.0} s Launch reckoned with; the trip's plane then {planes:0.00}° from the stack's");

        var where = booster.CentreOfMass;
        var depth = -Terrain.Height(Planet.Up(where));
        output.WriteLine($"the booster {booster.State}{(booster.LandedOnTarget ? " on target" : "")}{(booster.CrashReason is { } why ? $" ({why})" : "")}, fuel {booster.Fuel * booster.CurrentStage.FuelMass:0} kg, {Along(pad, where):0} m from the pad heading {Bearing(pad, where):+0.0;-0.0}° off due east, "
            + $"{Along(Scene.TripDroneShipCentre, where):0} m from Home's second drone ship; the sea {depth:0.0} m deep there; at {Vector3Text(where)}");
        if (pilot.FallsAt is { } falls)
            output.WriteLine($"left alone it would have come down {Along(pad, falls):0} m from the pad heading {Bearing(pad, falls):+0.0;-0.0}°, {Along(falls, where):0} m from where it landed; at {Vector3Text(falls)}");
        output.WriteLine($"the drone ship's bonus: {(booster.LandedOnTarget ? "won" : "not won")}");

        Assert.True(off < 0.5f, $"{off:0.000}° off the trip's plane");
        // The trip goes on from here: its window within a lap, the plane still the stack's then.
        Assert.InRange(toWindow, 0.0, orbit.Period);
        Assert.True(planes < 1f, $"the trip's plane at the window {planes:0.00}° from the stack's");
        Assert.True(stack.Periapsis > Scene.AtmosphereHeight, $"periapsis {stack.Periapsis:0} m");
        Assert.False(stack.FairingOn);
        Assert.Equal(2, stack.TopStage);
        Assert.InRange(fuel, MoonTrip.LanderAscentFuel * 0.95f, MoonTrip.LanderAscentFuel * 1.05f);
        if (!onTheShip)
            return;
        Assert.True(booster.State is FlightState.Landed or FlightState.Floating, $"the booster {booster.State} {booster.CrashReason}");
        Assert.True(booster.LandedOnTarget, $"the booster {booster.State}, {booster.DistanceToLandingSite:0.0} m from Home's second drone ship");
        Assert.True(flight.Run!.Progress.IsDone(Array.FindIndex(Mission.LandOnTheMoon.Steps, s => s.Label == "Bonus: the booster on the drone ship")));
    }

    // ---- The lander's trip: round the moon, let go, down by the spot ----

    /// <summary>
    /// The margins the trip is flown with, what "comfortable" means here (a few mistakes forgiven):
    /// the upper stage has at least this much of its tank once round the moon, and the lander lands
    /// with at least this much of its own.
    /// </summary>
    internal const float UpperStageMargin = 0.10f, LanderMargin = 0.40f;

    /// <summary>How near the spot (m, along the moon's ground) the lander comes down.</summary>
    private const float SpotWithin = 50f;

    /// <summary>How soft its touchdown is, at least (m/s).</summary>
    private const float TouchdownUnder = 3f;

    private static GroundBody Moon => GroundBody.Moon;
    private static readonly Pilot Nobody = (_, _) => default;

    private static float Kg(Vessel vessel) => MoonTripTests.FuelKg(vessel);

    [Fact]
    public void Arriving_the_stack_is_captured_into_an_orbit_round_the_moon_with_fuel_to_spare()
    {
        var (vessel, time) = MoonTrip.Arriving(0, lander: true);
        var (sim, stack) = MoonTripTests.Flying(MoonTrip.World(time, vessel));
        using var _ = sim;
        var fastest = new MoonTripTests.Fastest();
        var pilot = new CapturePilot();
        var start = sim.Time;
        var done = MoonTripTests.Fly(sim, pilot.Fly, _ => pilot.Now == CapturePilot.Phase.Done, maxSeconds: 400, fastest, "capture");
        sim.Run(Nobody, _ => false, maxSeconds: 2); // Auto's frame follows the new orbit
        var f = sim.Flight;
        output.WriteLine($"capture: {(done ? "done" : $"not done ({pilot.Now})")} after {sim.Time - start:0} s: periapsis {f.Periapsis:0} m, apoapsis {f.Apoapsis:0} m (the highest ground {Moon.MaxHeight:0} m), period {f.OrbitLap:0} s; "
            + $"the upper stage from {MoonTrip.LanderAscentFuel:0} kg to {Kg(stack):0} kg ({stack.Fuel:P1} of its tank; the margin {UpperStageMargin:P0}), the burn {pilot.BurnBegan?.Fuel - pilot.BurnEnded?.Fuel:0} kg");
        Assert.True(done, $"never captured ({pilot.Now})");
        Assert.Same(stack, f.Controlled);
        Assert.Equal(2, stack.TopStage); // the lander still on
        Assert.Same(Moon, stack.Body);
        Assert.NotNull(f.OrbitLap); // closed, within the moon's pull
        Assert.True(f.Periapsis > Moon.MaxHeight, $"periapsis {f.Periapsis:0} m, under the highest ground ({Moon.MaxHeight:0} m)");
        // This start has the fuel the ascent leaves (the transfer's not yet spent), so the margin is
        // only touched on here: the long trip and the playthrough hold it as flown.
        Assert.True(stack.Fuel >= UpperStageMargin, $"the upper stage has {stack.Fuel:P1} of its tank");
        fastest.NeverHeld();
    }

    [Fact]
    public void In_orbit_round_the_moon_the_lander_parts_from_the_upper_stage_cleanly()
    {
        var (sim, stack) = MoonTripTests.Flying(MoonTrip.World(MoonTrip.Start, MoonTrip.MoonOrbit(0, MoonTrip.MoonHeight, lander: true)));
        using var _ = sim;
        var pilot = new LanderMissionPilot(from: LanderMissionPilot.Phase.Release);
        Assert.True(sim.Run(pilot.Fly, _ => pilot.StackAtRelease is not null, maxSeconds: 60), "never let the lander go");
        var (periapsis, apoapsis, at, offSquare) = pilot.StackAtRelease!.Value;
        // Ten seconds on, flown only as far as the release goes (the nose held while clear), nothing else.
        var touched = new List<string>();
        sim.Run((f, dt) => pilot.Now == LanderMissionPilot.Phase.Release ? pilot.Fly(f, dt) : default, f =>
        {
            foreach (var part in new[] { pilot.Lander, pilot.UpperStage })
                if (part is { OnFormula: false })
                    foreach (var touch in sim.Physics.TouchesBegun(part.Craft))
                        touched.Add($"{part.Name} touched {touch.With} by its {touch.Part} at {f.WorldTime - at:0.0} s");
            return f.WorldTime >= at + 10;
        }, maxSeconds: 12);
        var lander = sim.Flight.Controlled;
        var upper = pilot.UpperStage ?? throw new Xunit.Sdk.XunitException("no upper stage left behind");
        output.WriteLine($"the stack let go of the lander at {at:0.0} s, its nose {offSquare:0.00}° from square to its orbit: periapsis {periapsis:0.00} m, apoapsis {apoapsis:0.00} m");
        output.WriteLine($"10 s on: the lander {lander.State}, periapsis {lander.Periapsis:0.00} m, apoapsis {lander.Apoapsis:0.00} m; the upper stage {upper.State}, periapsis {upper.Periapsis:0.00} m, apoapsis {upper.Apoapsis:0.00} m; "
            + $"{Vector3.Distance(lander.CentreOfMass, upper.CentreOfMass):0.0} m apart; {(touched.Count == 0 ? "no touch" : string.Join("; ", touched))}");
        Assert.Same(pilot.Lander, lander);
        Assert.Same(stack, lander); // the flown vessel goes on as the part above
        Assert.Equal((2, 2), (lander.Stage, lander.TopStage)); // the lander alone
        Assert.Equal((1, 1), (upper.Stage, upper.TopStage));
        Assert.Empty(touched);
        Assert.Equal(FlightState.Flying, lander.State);
        Assert.Equal(FlightState.Flying, upper.State);
        // Each pushed square to the orbit, its speed grows by the push's square over twice the
        // orbit's speed: its orbit is lifted on the far side by that much, the periapsis kept.
        var radius = Vector3.Distance(lander.CentreOfMass, Moon.Centre);
        var push = Scene.SeparationSpeed / 2;
        var lift = 2f * radius * push * push / (Moon.Mu / radius);
        output.WriteLine($"the push apart ({push:0.00} m/s each, square to the orbit) lifts the far side by {lift:0.00} m");
        foreach (var part in new[] { upper, lander })
        {
            Assert.True(MathF.Abs(part.Periapsis - periapsis) < 2f, $"the {part.Name}'s periapsis {part.Periapsis:0.00} m against the stack's {periapsis:0.00} m");
            Assert.True(MathF.Abs(part.Apoapsis - (apoapsis + lift)) < 1f, $"the {part.Name}'s apoapsis {part.Apoapsis:0.00} m against the stack's {apoapsis:0.00} m and the push's lift {lift:0.00} m");
        }
    }

    /// <remarks>
    /// The release (some 15 s: turned square, held, let go, held clear) takes the spot past where a
    /// line-up begins, so the landing waits a lap with its engine off, the moon turning its track
    /// some 800 m off the spot meanwhile, and lines it up on the next pass.
    /// </remarks>
    [LongFact]
    [Trait("Run", "Long")]
    public void Released_from_the_stack_round_the_moon_the_lander_comes_down_near_the_spot_with_fuel_to_spare()
    {
        var (sim, stack) = MoonTripTests.Flying(MoonTrip.World(MoonTrip.Start, MoonTrip.MoonOrbit(0, MoonTrip.MoonHeight, lander: true)));
        using var _ = sim;
        var fastest = new MoonTripTests.Fastest();
        var pilot = new LanderMissionPilot(from: LanderMissionPilot.Phase.Release);
        var start = sim.Time;
        var down = MoonTripTests.Fly(sim, pilot.Fly, f => f.Controlled.State != FlightState.Flying, maxSeconds: 1200, fastest, "release and landing");
        var lander = sim.Flight.Controlled;
        var landing = pilot.Landing;
        output.WriteLine($"released and landed: {lander.State} {lander.CrashReason} after {sim.Time - start:0} s, touchdown {lander.TouchdownSpeed:0.00} m/s, "
            + $"{landing.Spot.DistanceFrom(lander, sim.Flight.WorldTime):0.0} m from the spot, {Kg(lander):0} kg left ({lander.Fuel:P1}); "
            + $"the track {landing.AsideBefore:0} m aside before lining up; the mark {landing.MarkAtCut:0} m beyond it at the cut");
        foreach (var (what, at) in new[] { ("line-up began", landing.LineUpBegan), ("de-orbit burn began", landing.BurnBegan), ("de-orbit burn ended", landing.BurnEnded) })
            output.WriteLine(at is { } a ? $"  {what}: {a.Time:0.0} s, {a.Fuel:0.0} kg" : $"  {what}: never");
        Assert.True(down, $"never came down ({pilot.Now}, {landing.Now})");
        Assert.Same(stack, lander);
        Assert.NotNull(pilot.UpperStage);
        Assert.Equal(FlightState.Flying, pilot.UpperStage!.State); // left on its orbit above
        LandedNearTheSpot(sim, lander);
        fastest.NeverHeld();
    }

    /// <summary>The lander flown down by <paramref name="pilot"/> in <paramref name="sim"/>, reported: whether it came down.</summary>
    private bool Land(Simulation sim, MoonLandingPilot pilot, MoonTripTests.Fastest fastest)
    {
        var start = sim.Time;
        var down = MoonTripTests.Fly(sim, pilot.Fly, f => f.Controlled.State != FlightState.Flying, maxSeconds: 600, fastest, "landing");
        var flown = sim.Flight.Controlled;
        output.WriteLine($"landing: {flown.State} {flown.CrashReason} after {sim.Time - start:0} s, touchdown {flown.TouchdownSpeed:0.00} m/s, "
            + $"{pilot.Spot.DistanceFrom(flown, sim.Flight.WorldTime):0.0} m from the spot, {Kg(flown):0} kg left ({flown.Fuel:P1} of its tank; the margin {LanderMargin:P0}); legs {(flown.LegsWanted ? "out" : "in")}; "
            + $"the track {pilot.AsideBefore:0} m aside before lining up; the mark {pilot.MarkAtCut:0} m beyond it at the cut");
        foreach (var (what, at) in new[] { ("line-up began", pilot.LineUpBegan), ("de-orbit burn began", pilot.BurnBegan), ("de-orbit burn ended", pilot.BurnEnded) })
            output.WriteLine(at is { } a ? $"  {what}: {a.Time:0.0} s, {a.Fuel:0.0} kg" : $"  {what}: never");
        return down;
    }

    /// <summary>That <paramref name="lander"/> is down softly near the spot, its legs out, with <see cref="LanderMargin"/> of its fuel or more.</summary>
    private static void LandedNearTheSpot(Simulation sim, Vessel lander)
    {
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.Same(Moon, lander.Body);
        Assert.True(lander.LegsWanted, "it landed with its legs in");
        Assert.True(lander.TouchdownSpeed < TouchdownUnder, $"touched down at {lander.TouchdownSpeed:0.00} m/s");
        var off = PointOfInterest.MoonLandingSpot.DistanceFrom(lander, sim.Flight.WorldTime);
        Assert.True(off < SpotWithin, $"it came down {off:0.0} m from the spot");
        Assert.True(lander.Fuel >= LanderMargin, $"it landed with {lander.Fuel:P1} of its fuel");
    }

    [Fact]
    public void From_its_orbit_the_lander_comes_down_near_the_spot_with_fuel_to_spare()
    {
        var (sim, lander) = MoonTripTests.Flying(MoonTrip.World(MoonTrip.Start, MoonTrip.Lander(0, MoonTrip.MoonHeight)));
        using var _ = sim;
        Assert.Equal((2, 2), (lander.Stage, lander.TopStage));
        var fastest = new MoonTripTests.Fastest();
        var pilot = new MoonLandingPilot(PointOfInterest.MoonLandingSpot);
        Assert.True(Land(sim, pilot, fastest), $"never came down ({pilot.Now}, {pilot.Landing.Now})");
        output.WriteLine($"fuel: {MoonTrip.LanderFuel:0} kg in orbit, {Kg(lander):0} kg landed ({MoonTrip.LanderFuel - Kg(lander):0} kg to land)");
        LandedNearTheSpot(sim, lander);
        fastest.NeverHeld();
    }

    [Fact]
    public void Landed_it_opens_its_bay_doors_onto_the_ground()
    {
        var spot = PointOfInterest.MoonLandingSpot;
        var state = Drops.Placed(spot.Place, Moon.East(spot.Place), 0f, Vehicle.LanderLauncher, first: 2, clearance: 0.05f, mission: Mission.FreePlay, body: Moon, fuel: 0.5f);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 }, body: Moon);
        var lander = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: lander));
        Assert.True(sim.Run(Nobody, _ => lander.State == FlightState.Landed && lander.Craft.Frozen, maxSeconds: 20), $"never settled ({lander.State} {lander.CrashReason})");
        // The landing pilot, landed: it opens the bay (through FlightInput, as the actions menu does).
        var pilot = new MoonLandingPilot(spot);
        var opened = sim.Run(pilot.Fly, _ => lander.BayDoors >= 1f, maxSeconds: Scene.LanderDoorSeconds + 5);
        sim.Run(Nobody, _ => lander.Craft.Frozen, maxSeconds: 10);
        output.WriteLine($"the doors {lander.BayDoors:0.00} open, wanted {lander.BayDoorsWanted}; the lander {lander.State}, {spot.DistanceFrom(lander, sim.Flight.WorldTime):0.0} m from the spot");
        Assert.True(opened, $"the doors only {lander.BayDoors:0.00} open");
        Assert.Equal(1f, lander.BayDoors);
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(Mission.BayOpen(lander));
    }

    /// <remarks>
    /// Long (some 15 s: some 400 s of flight on the transfer, which no start can shorten), beside
    /// <c>MoonTripTests.From_a_low_orbit_the_upper_stage_flies_into_the_moons_sphere</c>, the same
    /// with the upper stage alone: it leaves out nothing but the lander's weight.
    /// </remarks>
    [LongFact]
    [Trait("Run", "Long")]
    public void The_stack_flies_from_a_low_orbit_into_the_moons_sphere()
    {
        var (sim, stack) = MoonTripTests.Flying(MoonTrip.World(MoonTrip.Start, MoonTrip.LowOrbit(0, MoonTrip.Start, lander: true)));
        using var _ = sim;
        var fastest = new MoonTripTests.Fastest();
        var pilot = new TransferPilot();
        var crossed = MoonTripTests.Fly(sim, pilot.Fly, f => f.Body == Moon, maxSeconds: 1200, fastest, "transfer");
        output.WriteLine($"transfer: {(crossed ? "in the moon's sphere" : $"not there ({pilot.Now})")}; the upper stage from {MoonTrip.LanderAscentFuel:0} kg to {Kg(stack):0} kg (the burn {pilot.BurnBegan?.Fuel - pilot.BurnEnded?.Fuel:0} kg, the trim {pilot.BurnEnded?.Fuel - pilot.TrimEnded?.Fuel:0.0} kg); the fastest {fastest.Speed:0} m/s over the ground");
        Assert.True(crossed, $"never reached the moon's sphere ({pilot.Now})");
        var (polar, periapsis) = pilot.Trimmed ?? throw new Xunit.Sdk.XunitException("never trimmed");
        output.WriteLine($"  trimmed: the moon leg {Math.Asin(polar) * 180 / Math.PI:0.00}° off its poles, its periapsis {periapsis:0} m over the datum");
        Assert.True(pilot.TrimmedWithin, $"the trim gave up {Math.Asin(polar) * 180 / Math.PI:0.00}° off the poles, its periapsis {periapsis:0} m");
        Assert.True(periapsis > Moon.MaxHeight, $"the way round the moon comes down to {periapsis:0} m");
        Assert.Same(Moon, stack.Body);
        Assert.Equal(2, stack.TopStage);
        Assert.True(Kg(stack) > 0f, "no fuel left");
        Assert.True(fastest.Speed < 1000f, $"{fastest.Speed:0} m/s over the ground");
        fastest.NeverHeld();
    }

    /// <summary>Prints <paramref name="pilot"/>'s legs' ends, with the fuel then.</summary>
    private void Legs(LanderMissionPilot pilot)
    {
        foreach (var (leg, time, kg, fraction) in pilot.Ends)
            output.WriteLine($"  {leg} ended at {time:0.0} s: {kg:0.0} kg ({fraction:P1} of the {(leg >= LanderMissionPilot.Phase.Release ? "lander's" : "upper stage's")} tank)");
    }

    /// <summary>That the trip <paramref name="pilot"/> flew kept both margins: the upper stage's once round the moon, and the lander's landed.</summary>
    private static void Margins(LanderMissionPilot pilot)
    {
        var captured = pilot.Ends.Single(e => e.Leg == LanderMissionPilot.Phase.Capture);
        Assert.True(captured.Fraction >= UpperStageMargin, $"the upper stage has {captured.Fraction:P1} of its tank round the moon");
        var landed = pilot.Ends.Single(e => e.Leg == LanderMissionPilot.Phase.Landing);
        Assert.True(landed.Fraction >= LanderMargin, $"the lander landed with {landed.Fraction:P1} of its tank");
    }

    /// <remarks>
    /// Long (the transfer's 400 s and every leg after it), beside the quick legs above, each from
    /// where it is about: it leaves out nothing of them, flown one after the other.
    /// </remarks>
    [LongFact]
    [Trait("Run", "Long")]
    public void The_lander_trip_from_a_low_orbit_to_the_spot()
    {
        var (sim, _) = MoonTripTests.Flying(MoonTrip.World(MoonTrip.Start, MoonTrip.LowOrbit(0, MoonTrip.Start, lander: true)));
        using var __ = sim;
        var fastest = new MoonTripTests.Fastest();
        var pilot = new LanderMissionPilot(from: LanderMissionPilot.Phase.Transfer);
        var done = MoonTripTests.Fly(sim, pilot.Fly, _ => pilot.Now == LanderMissionPilot.Phase.Done, maxSeconds: 3000, fastest, "trip");
        output.WriteLine($"the trip: {(done ? "done" : $"not done ({pilot.Now})")} after {sim.Time:0} s; the fastest {fastest.Speed:0} m/s over the ground");
        Legs(pilot);
        Assert.True(done, $"never done ({pilot.Now})");
        Assert.True(sim.Flight.Controlled.BayDoors >= 1f);
        LandedNearTheSpot(sim, sim.Flight.Controlled);
        Margins(pilot);
        fastest.NeverHeld();
    }

    [PlaythroughFact(Long = true)]
    [Trait("Kind", "Playthrough")]
    [Trait("Run", "Long")]
    public void Playthrough_land_on_the_moon()
    {
        var mission = Mission.LandOnTheMoon;
        var launch = MoonTrip.Launch(Scene.ClockStart) ?? throw new InvalidOperationException("Home's pad never under the trip's plane");
        using var sim = new Simulation(mission, world: new WorldState { Time = launch.Time });
        var pilot = new LanderMissionPilot(launch);
        var fastest = new MoonTripTests.Fastest();
        // Each step's points as it is done (what the flight's progress gives it then).
        var points = new int?[mission.Steps.Length];
        void Tick(Flight f)
        {
            for (var i = 0; i < points.Length; i++)
                if (points[i] is null && f.Run!.Progress.IsDone(i))
                    points[i] = mission.Steps[i].Points?.Invoke(f.Run!) ?? 0;
        }
        var done = sim.Run(pilot.Fly, f =>
        {
            fastest.Check(f);
            Tick(f);
            return pilot.Now == LanderMissionPilot.Phase.Done;
        }, maxSeconds: 4000);
        sim.Run(Nobody, f =>
        {
            Tick(f);
            return false;
        }, maxSeconds: 1); // the last step ticked off
        var flight = sim.Flight;

        output.WriteLine($"launched at {launch.Time:0.0} s; {(done ? "done" : $"not done ({pilot.Now})")} after {sim.Time:0} s; the fastest {fastest.Speed:0} m/s over the ground");
        Legs(pilot);
        var booster = pilot.Ascent!.Booster!;
        output.WriteLine($"the booster {booster.State}{(booster.LandedOnTarget ? " on target" : "")}{(booster.CrashReason is { } why ? $" ({why})" : "")}, {booster.DistanceToLandingSite:0.0} m from Home's second drone ship, {Kg(booster):0} kg left");
        if (pilot.Lander is { } lander)
            output.WriteLine($"the lander {lander.State}, touchdown {lander.TouchdownSpeed:0.00} m/s, {PointOfInterest.MoonLandingSpot.DistanceFrom(lander, flight.WorldTime):0.0} m from the spot, {Kg(lander):0.0} kg left, the bay doors {lander.BayDoors:0.00} open");
        output.WriteLine($"the mission {(flight.Run!.Succeeded ? "succeeded" : "not succeeded")}; score {flight.Run!.Score?.ToString() ?? "none"} ({flight.Run!.Progress.Points} points):");
        for (var i = 0; i < points.Length; i++)
            output.WriteLine($"  {mission.Steps[i].Label}: {(points[i] is { } p ? $"{p} points" : "not done")}");

        Assert.True(done, $"never done ({pilot.Now})");
        Assert.True(flight.Run!.Succeeded, "the mission didn't succeed");
        Assert.Equal(flight.Run!.Progress.Points, points.Sum(p => p ?? 0));
        Assert.Equal(flight.Run!.Progress.Points, flight.Run!.Score);
        LandedNearTheSpot(sim, pilot.Lander!);
        Margins(pilot);
        fastest.NeverHeld();
    }

    /// <summary>The bearing of <paramref name="p"/> from <paramref name="pad"/>, degrees off due east, north positive.</summary>
    private static float Bearing(Vector3 pad, Vector3 p) => OffEast(pad, Pilots.Across(Planet.Up(p), Planet.Up(pad)));

    private static string Vector3Text(Vector3 v) => $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})";
}
