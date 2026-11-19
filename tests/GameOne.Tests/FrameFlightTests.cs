using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Whole flights flown by the frames of reference, by test pilots (in FramePilots.cs) that have
/// only what the player has: the frame selector, the holds, the throttle and the direction rings,
/// and what the HUD shows in the frame in use (the travel markers' <see cref="Flight.FrameVelocity"/>,
/// the relative speed, the way to the target and "To target", the line and its landing mark).
/// </summary>
public sealed class FrameFlightTests(ITestOutputHelper output)
{
    [PlaythroughFact(Long = false)]
    [Trait("Kind", "Playthrough")]
    public void Auto_turns_to_orbit_on_the_way_up_as_it_clears_the_air()
    {
        // Auto goes by height over the ground under it: the ascent turns orbital as it climbs past
        // Auto's height (a share of the planet's radius, about the air's top) and the margin over
        // it, its periapsis still in the air, well before the orbit step is ticked, and stays
        // orbital through the circularising burn. On in a low orbit it may turn the surface's
        // again only where high ground brings it under the height, and back once past the margin.
        using var sim = new Simulation(Mission.ReachOrbit);
        var flight = sim.Flight;
        var orbit = Array.FindIndex(flight.Mission.Steps, s => s.Label.StartsWith("Reach a stable orbit"));
        Assert.Equal(FrameChoice.Auto, flight.FrameChoice);
        Assert.Equal(Frame.Surface, flight.Frame);

        var pilot = new OrbitAutopilot();
        var turns = new List<(double Time, Frame To, float Altitude, float Over, float Periapsis)>();
        var frame = flight.Frame;
        Pilot watched = (f, dt) =>
        {
            if (f.Frame != frame)
                turns.Add((f.WorldTime, f.Frame, Planet.Altitude(f.CentreOfMass), Planet.SurfaceAltitude(f.CentreOfMass), f.Periapsis));
            frame = f.Frame;
            return pilot.Fly(f, dt);
        };
        Assert.True(sim.Run(watched, f => f.Run!.Progress.IsDone(orbit), maxSeconds: 400), "the orbit step was never ticked");
        var ticked = flight.WorldTime;
        sim.Run(watched, _ => false, maxSeconds: 60); // and on in orbit, coasting: it stays orbital

        foreach (var turn in turns)
            output.WriteLine($"{turn.Time:0.0} s: to {turn.To}, {turn.Altitude:0} m up, {turn.Over:0} m over the ground under it, the periapsis {turn.Periapsis:0} m");
        output.WriteLine($"the orbit step ticked at {ticked:0.0} s; the periapsis now {flight.Periapsis:0} m; the planet's highest ground {GroundBody.Planet.MaxHeight:0} m");
        var within = GroundBody.Planet.FrameSurfaceHeight;
        var first = turns[0];
        Assert.Equal(Frame.Orbital, first.To);
        Assert.InRange(first.Over, within + Scene.FrameOrbitMargin, within + Scene.FrameOrbitMargin + 50f);
        Assert.True(first.Periapsis < Scene.AtmosphereHeight, "it turns orbital by its height, its orbit still in the air");
        Assert.True(first.Time < ticked);
        Assert.DoesNotContain(turns, t => t.Time > first.Time && t.Time < ticked); // orbital through the burn
        foreach (var turn in turns.Skip(1))
        {
            if (turn.To == Frame.Surface)
                Assert.InRange(turn.Over, within - 50f, within);
            else
                Assert.InRange(turn.Over, within + Scene.FrameOrbitMargin, within + Scene.FrameOrbitMargin + 50f);
        }
        Assert.Equal(FrameChoice.Auto, flight.FrameChoice);
    }

    // Started where it is put, and moved 30 m and 300 m north or south, and 3 km along the way:
    // the pilot's own steering (the burn's aside, then low down) takes up the difference aside,
    // and when it burns (by the site's angle ahead and the mark's lead) the difference along.
    // (Moved 30 m along, it came down as unmoved: the burn cut 0.2 s sooner or later, the mark
    // then within 2 m of the unmoved one's, and the braking and the landing the same, so it
    // proved nothing the 3 km cases don't.)
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(30f, 0f)]
    [InlineData(-30f, 0f)]
    [InlineData(300f, 0f)]
    [InlineData(-300f, 0f)]
    [InlineData(0f, 3000f)]
    [InlineData(0f, -3000f)]
    public void Coming_home_by_the_surface_frame_lands_on_the_zone(float north, float along)
    {
        using var sim = new Simulation(Mission.DeploySatellite, world: new WorldState { Vessels = [UpperStageInOrbit(north, along)], NextVesselId = 1 });
        var flight = sim.Flight;
        var upper = flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: upper));
        Assert.Equal(Frame.Orbital, flight.Frame);

        var pilot = new ComingHomePilot();
        var zone = Scene.LandingZoneCentre;
        var (phase, frames) = ("", new List<(double Time, Frame Frame)> { (flight.WorldTime, flight.Frame) });
        var marks = new Marks();
        Assert.True(sim.RunToEnd((f, dt) =>
        {
            if (f.Frame != frames[^1].Frame)
                frames.Add((f.WorldTime, f.Frame));
            if (pilot.PhaseName != phase)
            {
                phase = pilot.PhaseName;
                output.WriteLine($"{f.WorldTime:0.0} s: {phase}, in {f.Frame}: {f.Ground:0} m up, {f.FrameVelocity.Length():0.0} m/s, " +
                    $"the zone {FrameReadings.Beyond(f.CentreOfMass, f.FrameVelocity, zone):0} m ahead, fuel {f.Fuel * 100:0}%");
            }
            if (pilot.PhaseName is "Fall" or "Brake" or "Down")
                marks.Look(f);
            return pilot.Fly(f, dt);
        }, maxSeconds: 400), "it never came down");

        var cut = pilot.AtCut!.Value;
        output.WriteLine($"the burn cut at {cut.Time:0.0} s, in {cut.Frame}, the landing mark {cut.MarkBeyond:0} m beyond the zone and {-cut.MarkAside:0.0} m to its left; " +
            $"braking from {pilot.Landing.BrakedAt?.Speed:0} m/s, {pilot.Landing.BrakedAt?.ToGo:0} m short of it");
        output.WriteLine($"frames: {string.Join(", ", frames.Select(x => $"{x.Frame} from {x.Time:0.0} s"))}");
        output.WriteLine($"{upper.State} {upper.CrashReason} at {flight.WorldTime:0.0} s, at {upper.TouchdownSpeed:0.0} m/s, {upper.DistanceToLandingSite:0.0} m from the zone's centre " +
            $"({FrameReadings.Beyond(zone, Planet.East(zone), upper.Position):0} m east, {FrameReadings.Aside(zone, Planet.East(zone), upper.Position):0} m north), fuel {upper.Fuel * 100:0}% left");
        var (hundred, settled) = marks.Report(output, upper.Position);
        if (marks.Before(pilot.Landing.BrakedAt!.Value.Time) is { } fell)
        {
            var lead = FrameReadings.Beyond(fell.Position, fell.Way, fell.Mark) - FrameReadings.Beyond(fell.Position, fell.Way, zone);
            output.WriteLine($"  as it started braking the mark was {lead:0} m beyond the zone: the air took {cut.MarkBeyond - lead:0} m off in the fall");
        }
        Assert.Equal(Frame.Orbital, cut.Frame); // the burn above the air: Auto turns surface only once it is down in it
        Assert.InRange(cut.MarkBeyond, 200f, 500f);
        Assert.InRange(MathF.Abs(cut.MarkAside), 0f, 30f); // the burn steered it onto the zone's line
        Assert.Equal([Frame.Orbital, Frame.Surface], frames.Select(x => x.Frame));
        Assert.Equal(FrameChoice.Auto, flight.FrameChoice);
        Assert.Equal(FlightState.Landed, upper.State);
        Assert.True(upper.LandedOnTarget, $"it came down {upper.DistanceToLandingSite:0.0} m from the zone's centre");
        // The line's mark, low down, is where it came down, and already near it 100 m up.
        Assert.InRange(hundred, 0f, MarkAt100);
        Assert.InRange(settled, 0f, MarkSettles);
    }

    [Fact]
    public void A_rendezvous_by_the_target_frame_closes_to_50_m_and_holds()
    {
        // Two satellites in the same circular orbit, 9 km up, one 1 km ahead of the other: far
        // enough that the pilot closes at its fastest (RendezvousPilot.ClosingSpeed) for a while
        // before it slows (from 2 km that stretch is only longer, by 150 s).
        var a = WorldPresets.Satellites().Vessels[0];
        var normal = Vector3.Normalize(Vector3.Cross(a.CentreOfMass.ToSingle() - Planet.Centre, InOrbit.Stars(a)));
        var ahead = Quaternion.CreateFromAxisAngle(normal, 1000f / (a.CentreOfMass.ToSingle() - Planet.Centre).Length());
        var b = InOrbit.At(a, Planet.Centre + Vector3.Transform(a.CentreOfMass.ToSingle() - Planet.Centre, ahead), Vector3.Transform(InOrbit.Stars(a), ahead)) with { Id = 1 };
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [a, b], NextVesselId = 2 });
        var flight = sim.Flight;
        var (mine, theirs) = (flight.WorldVessels.First(v => v.Id == 0), flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: mine));
        Assert.Equal(1000f, Vector3.Distance(mine.CentreOfMass, theirs.CentreOfMass), 5f);

        var pilot = new RendezvousPilot { Target = theirs };
        var start = flight.WorldTime;
        var closest = float.MaxValue;
        var fuel = mine.Fuel;
        Assert.True(sim.Run((f, dt) =>
        {
            closest = MathF.Min(closest, Vector3.Distance(mine.CentreOfMass, theirs.CentreOfMass));
            return pilot.Fly(f, dt);
        }, f => f.DistanceToTarget < 50f && f.RelativeSpeed < 0.5f, maxSeconds: 600),
            $"still {flight.DistanceToTarget:0} m apart at {flight.RelativeSpeed:0.00} m/s");
        var took = flight.WorldTime - start;
        output.WriteLine($"in {took:0} s: {flight.DistanceToTarget:0.0} m apart at {flight.RelativeSpeed:0.000} m/s, never closer than {closest:0.0} m; " +
            $"{(fuel - mine.Fuel) * 100:0}% of the tank burned");
        Assert.Equal(Frame.Target, flight.Frame);

        // And it holds there, a minute on.
        var (near, far, fastest) = (float.MaxValue, 0f, 0f);
        sim.Run((f, dt) =>
        {
            (near, far) = (MathF.Min(near, f.DistanceToTarget!.Value), MathF.Max(far, f.DistanceToTarget!.Value));
            fastest = MathF.Max(fastest, f.RelativeSpeed!.Value);
            return pilot.Fly(f, dt);
        }, _ => false, maxSeconds: 60);
        output.WriteLine($"the minute after: {near:0.0} to {far:0.0} m apart, at most {fastest:0.00} m/s; at the end {flight.DistanceToTarget:0.0} m at {flight.RelativeSpeed:0.000} m/s");
        Assert.True(took < 600);
        Assert.All([mine, theirs], v => Assert.Equal(FlightState.Flying, v.State));
        Assert.InRange(MathF.Min(closest, near), 10f, 50f);
        Assert.InRange(far, 0f, 50f);
        Assert.InRange(fastest, 0f, 0.5f);
        Assert.InRange(flight.RelativeSpeed!.Value, 0f, 0.25f);
    }

    /// <summary>
    /// Against a place the relative velocity is the vessel's own over the ground, with all the
    /// ground's noise, so the target frame's holds have the surface's floor there. A booster set
    /// down slowly on the landing zone, targeted, in the target frame and on the retrograde hold,
    /// drifting a little east: below that floor the nose stays upright (with the target's low floor it
    /// chased the drift, leaning 6.8° over).
    /// </summary>
    [Fact]
    public void Landing_on_a_targeted_place_in_the_target_frame_keeps_the_nose_up()
    {
        var zone = Scene.LandingZoneCentre;
        using var sim = Drops.At(zone, Planet.East(zone), speed: 3f, sideways: Planet.East(zone) * 0.8f, clearance: 40f);
        var flight = sim.Flight;
        sim.Step(new FlightInput(SetTarget: new NavTarget(TargetKind.Site, 1), ChooseFrame: FrameChoice.Target, ChooseHold: HoldMode.Retrograde));
        Assert.Equal(Frame.Target, flight.Frame);
        Assert.True(flight.Holding == HoldMode.Retrograde);

        var tilt = 0f;
        Assert.True(sim.RunToEnd((f, dt) =>
        {
            tilt = MathF.Max(tilt, f.TiltDegrees);
            var up = f.LocalUp;
            var vy = Vector3.Dot(f.Velocity, up);
            var wanted = f.Ground > 3f ? -2.5f : -1f;
            var throttle = Math.Clamp((Scene.SurfaceGravity + (wanted - vy) * 2f) / (f.MaxAcceleration * MathF.Max(Vector3.Dot(f.Aim, up), 0.5f)), 0f, 1f);
            return new FlightInput(Throttle: Pilots.ThrottleTowards(f, throttle, dt), CutThrottle: f.Ground < 0.2f);
        }, maxSeconds: 60), "it never came down");
        output.WriteLine($"{flight.State} {flight.CrashReason} at {flight.TouchdownSpeed:0.0} m/s; at most {tilt:0.0}° over; the hold {(flight.Holding == HoldMode.Retrograde ? "on" : "off")}");
        Assert.Equal(FlightState.Landed, flight.State);
        Assert.InRange(tilt, 0f, 3f); // measured 0.1°
    }

    [PlaythroughFact(Long = false)]
    [Trait("Kind", "Playthrough")]
    public void A_hop_steered_by_the_surface_line_comes_down_on_a_test_pad()
    {
        // The nearest test pad to Home's pad (the 5° one, 500 m north).
        var site = Enumerable.Range(0, Sites.All.Length).Where(i => Sites.All[i].Name.StartsWith("Test pad"))
            .MinBy(i => Vector3.Distance(Sites.All[i].Centre, Scene.RocketStart));
        using var sim = new Simulation();
        var flight = sim.Flight;
        var pilot = new HopPilot { Site = site };
        var phase = "";
        var marks = new Marks();
        Assert.True(sim.RunToEnd((f, dt) =>
        {
            if (pilot.PhaseName != phase)
            {
                phase = pilot.PhaseName;
                output.WriteLine($"{f.WorldTime:0.0} s: {phase}, in {f.Frame}: {f.Ground:0} m up, {f.FrameVelocity.Length():0.0} m/s, To target {f.DistanceToTarget:0} m");
            }
            if (pilot.PhaseName is "Coast" or "Fall" or "Brake" or "Down")
                marks.Look(f);
            return pilot.Fly(f, dt);
        }, maxSeconds: 180), "it never came down");
        var pad = Sites.All[site];
        var off = Planet.Radius * Planet.AngleBetween(flight.Position, pad.Top);
        output.WriteLine($"the mark {pilot.MarkAtCut:0} m beyond at the cut; {flight.State} {flight.CrashReason} at {flight.WorldTime:0.0} s, " +
            $"at {flight.TouchdownSpeed:0.0} m/s, {off:0.0} m from the middle of {pad.Name} (radius {pad.Radius:0} m), fuel {flight.Fuel * 100:0}% left");
        var (hundred, settled) = marks.Report(output, flight.Position);
        Assert.Equal(Frame.Surface, flight.Frame);
        Assert.Equal(FlightState.Landed, flight.State);
        Assert.InRange(hundred, 0f, MarkAt100); // the line's mark, 100 m up, is already near where it came down
        Assert.InRange(settled, 0f, MarkSettles); // and low down, is where it came down
        // The pad isn't the mission's landing site, so LandedOnTarget doesn't count it: on it is within its radius.
        Assert.InRange(off, 0f, pad.Radius);
    }

    /// <summary>
    /// How near where it came down the line's landing mark is, built low down (within
    /// <see cref="Marks.LowDown"/> of the ground) after the cut: the line, followed, leads there.
    /// Measured: within ~1 m on both flights; the bound allows for the last mark being built a
    /// moment before touchdown, still moving.
    /// </summary>
    private const float MarkSettles = 5f;

    /// <summary>
    /// How near where it came down the line's landing mark is at the first look under 100 m up:
    /// settling as it comes down, not only right at the end. Measured: 1.9 to 2.0 m coming home,
    /// 1.8 m on the hop (7 to 13 m at 300 m up); the bound allows for 2.7 m, measured with the
    /// pilots' side correction slower, twice over.
    /// </summary>
    private const float MarkAt100 = 6f;

    /// <summary>The line's landing marks as a flight comes down, seen every quarter of a second, and how far each is from where it came down.</summary>
    private sealed class Marks
    {
        public const float LowDown = 30f;
        private readonly Paths _paths = new();
        private readonly List<(double Time, float Ground, Vector3 Mark, Vector3 Position, Vector3 Way)> _seen = [];

        public void Look(Flight flight)
        {
            if (flight.State == FlightState.Flying && (int)(flight.WorldTime * 60) % 15 == 0
                && FrameReadings.Line(flight, _paths).LandingMark is { } mark)
                _seen.Add((flight.WorldTime, flight.Ground, mark, flight.CentreOfMass, flight.FrameVelocity));
        }

        /// <summary>The last mark seen before <paramref name="time"/>, with where the vessel was and the way it was going.</summary>
        public (Vector3 Mark, Vector3 Position, Vector3 Way)? Before(double time) =>
            _seen.LastOrDefault(x => x.Time < time) is { Mark: var mark } seen && mark != default ? (mark, seen.Position, seen.Way) : null;

        /// <summary>Writes how far the marks were from <paramref name="down"/> as it came down; returns the first one's under 100 m up, and the last low-down one's.</summary>
        public (float Hundred, float Low) Report(ITestOutputHelper output, Vector3 down)
        {
            float Off(Vector3 mark) => Planet.Radius * Planet.AngleBetween(mark, down);
            Assert.NotEmpty(_seen);
            // The first after the cut, then the first under each height.
            foreach (var at in new[] { _seen[0] }.Concat(new[] { 1000f, 300f, 100f, LowDown }.Select(h => _seen.FirstOrDefault(x => x.Ground < h))))
                if (at.Mark != default)
                    output.WriteLine($"  the mark at {at.Time:0.0} s, {at.Ground:0} m up: {Off(at.Mark):0.0} m from where it came down");
            var hundred = _seen.FirstOrDefault(x => x.Ground < 100f);
            var low = _seen.LastOrDefault(x => x.Ground < LowDown);
            Assert.NotEqual(default, hundred.Mark);
            Assert.NotEqual(default, low.Mark);
            return (Off(hundred.Mark), Off(low.Mark));
        }
    }

    /// <summary>
    /// The satellite mission's upper stage on its own, left in orbit 4 km up with half its fuel,
    /// engine off and nose up, 0.9 radians (9 km) round from the landing zone, its orbit through the
    /// zone heading east (the way a launch from the pad goes): moved <paramref name="north"/> and
    /// <paramref name="along"/> (back along the orbit) from there. The ground turns under the orbit
    /// in the meantime, so even unmoved its track doesn't pass over the zone by the time it comes
    /// down: the pilot steers onto it.
    /// </summary>
    private static VesselState UpperStageInOrbit(float north, float along)
    {
        const float altitude = 4000f, zoneAhead = 0.9f;
        var vehicle = Vehicle.ThreeStage;
        var zone = Planet.Up(Scene.LandingZoneCentre);
        var east = Planet.East(Scene.LandingZoneCentre);
        var r = Planet.SeaRadius + altitude;
        var (sin, cos) = MathF.SinCos(zoneAhead + along / r);
        var way = zone * sin + east * cos; // along the orbit, there
        var centre = Planet.Centre + (zone * cos - east * sin) * r; // its centre of mass, on the circle
        var aside = Quaternion.CreateFromAxisAngle(way, -north / r); // north, about the way it goes
        centre = Planet.Centre + Vector3.Transform(centre - Planet.Centre, aside);
        var stars = Vector3.Transform(way, aside) * Planet.CircularSpeed(altitude);
        var tanks = new float[vehicle.Stages.Length];
        tanks[1] = vehicle.Stages[1].FuelMass * 0.5f;
        return InOrbit.At(new VesselState
        {
            Id = 0, Flight = 0, Mission = Mission.DeploySatellite, Vehicle = vehicle, First = 1, Last = 1, Fuel = tanks,
            State = FlightState.Flying, Position = centre, Rotation = Quaternion.Identity, Velocity = Vector3.Zero,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 1, 1),
            AngularVelocity = Vector3.Zero, Aim = Vector3.UnitY,
            MaxAltitude = altitude, MaxGround = altitude, AchievedOrbit = true, HasBeenAirborne = true,
        }, centre, stars);
    }
}
