using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The rules judged from what each vessel touched (Jolt's contacts: see <see cref="RocketPhysics.TouchesBegun"/>):
/// a touchdown on each kind of solid thing judged by how fast the touching point closed with it,
/// a skid's speed along the ground in it, with one sound for one touch; sliding onto the next
/// ground tile no new touch, but sliding hard into a pad's side a hit; the drone ship's masts
/// wrecking on a gentle touch; a fairing half told by what it came down on; a landed vessel woken
/// where it stands, or lighting its engine there, not touched down again; and one coming down past
/// where it is no longer measured from afar judged as ever.
/// </summary>
public sealed class TouchRuleTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>How fast a booster must start <see cref="Drops.Clearance"/> above something to collide it at <paramref name="touching"/>, falling.</summary>
    private static float StartSpeed(float touching) => MathF.Sqrt(touching * touching - 2f * Scene.SurfaceGravity * Drops.Clearance);

    /// <summary>
    /// A booster with its legs out, upright over <paramref name="what"/> (level ground, Home's pad,
    /// the drone ship's deck or a floe), falling so that it touches at <paramref name="touching"/>
    /// against it (a raft's own motion added), and flown.
    /// </summary>
    private static (Simulation Sim, Vessel Vessel) DroppedOn(string what, float touching)
    {
        Vector3 at;
        Raft? raft = null;
        LaunchSite? site = null;
        switch (what)
        {
            case "ground":
                (at, _, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
                break;
            case "pad":
                at = LaunchSites.Home.Pad;
                site = LaunchSites.All[1]; // flown from elsewhere, so the new flight's rocket isn't on Home's pad
                break;
            case "ship":
                Rafts.Time = 0.0;
                raft = Rafts.Ships[0];
                raft.Set(0.0); // the simulation's world starts at time 0, so the deck is lowered onto as it is then
                at = raft.Current.Position + raft.Normal * raft.Freeboard;
                break;
            case "floe":
                Rafts.Time = 0.0;
                raft = Floes.Near(Planet.Centre + IceTests.FloeBand("Boreal Sea") * Planet.SeaRadius, 300f).MaxBy(f => f.HalfLength)!;
                raft.Set(0.0);
                at = raft.Current.Position + raft.Normal * raft.Freeboard;
                break;
            default:
                throw new ArgumentException(what);
        }
        var up = Planet.Up(at);
        var state = Drops.Placed(at, Planet.East(at), StartSpeed(touching));
        if (raft is not null)
            state = state with { Velocity = state.Velocity + raft.VelocityAt(state.Position) }; // falling at that against the raft
        var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 }, site: site);
        var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        return (sim, vessel);
    }

    [Theory]
    [InlineData("ground")]
    [InlineData("pad")]
    [InlineData("ship")]
    [InlineData("floe")]
    public void A_touchdown_just_under_the_tolerance_lands_on_each_solid_thing(string what)
    {
        var (sim, vessel) = DroppedOn(what, 5.6f);
        using var _ = sim;
        sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 30);
        output.WriteLine($"on the {what}: {vessel.State} {vessel.CrashReason}, touchdown {vessel.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Landed, vessel.State);
        Assert.InRange(vessel.TouchdownSpeed, 5.3f, 6.0f);
    }

    [Theory]
    [InlineData("ground")]
    [InlineData("pad")]
    [InlineData("ship")]
    [InlineData("floe")]
    public void A_touchdown_just_over_the_tolerance_crashes_on_each_solid_thing(string what)
    {
        var (sim, vessel) = DroppedOn(what, 6.4f);
        using var _ = sim;
        sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 30);
        output.WriteLine($"on the {what}: {vessel.State} {vessel.CrashReason}, touchdown {vessel.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Crashed, vessel.State);
        Assert.StartsWith("Hit the ground at", vessel.CrashReason);
    }

    [Theory]
    [InlineData(true, 5.9f, true)]
    [InlineData(true, 6.2f, false)]
    [InlineData(false, 2.4f, true)]
    [InlineData(false, 2.7f, false)]
    public void A_touchdown_is_as_hard_as_it_was_when_read_above_the_ground(bool legsOut, float readAbove, bool lands)
    {
        // The limits' leeway (Scene.TouchdownLeewayHeight's fall, and a step's): one coming down just
        // under the round limit (6 m/s on the legs, 2.5 on the bell) as read that high lands, and
        // one just over crashes.
        var touching = MathF.Sqrt(readAbove * readAbove + 2f * Scene.SurfaceGravity * Scene.TouchdownLeewayHeight);
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        using var sim = Drops.At(at, downhill, StartSpeed(touching), legsOut: legsOut);
        var vessel = sim.Flight.Controlled;
        sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 30);
        output.WriteLine($"legs out {legsOut}, {readAbove} m/s read above (touching at {touching:0.00}): {vessel.State} {vessel.CrashReason}, touchdown {vessel.TouchdownSpeed:0.00} m/s");
        Assert.Equal(lands ? FlightState.Landed : FlightState.Crashed, vessel.State);
        if (!lands)
            Assert.Contains(legsOut ? "Hit the ground at" : "engine bell", vessel.CrashReason);
    }

    [Theory]
    [InlineData(5f, true)]   // 5.8 m/s in all
    [InlineData(6f, false)]  // 6.7 m/s in all
    public void A_skidding_touchdown_is_judged_by_its_whole_speed(float sliding, bool lands)
    {
        // Coming down at 3 m/s while sliding along the ground: the touching point's whole speed is
        // what counts, its skid in it, not only how fast it came down.
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var along = Vector3.Normalize(downhill - Planet.Up(at) * Vector3.Dot(downhill, Planet.Up(at)));
        using var sim = Drops.At(at, downhill, StartSpeed(3f), sideways: along * sliding);
        var vessel = sim.Flight.Controlled;
        sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 30);
        output.WriteLine($"sliding at {sliding} m/s: {vessel.State} {vessel.CrashReason}, touchdown {vessel.TouchdownSpeed:0.00} m/s");
        if (lands)
            Assert.Equal(FlightState.Landed, vessel.State);
        else
        {
            Assert.Equal(FlightState.Crashed, vessel.State);
            Assert.StartsWith("Hit the ground at", vessel.CrashReason);
        }
    }

    [Theory]
    [InlineData(0f, true)]   // on its feet
    [InlineData(0f, false)]  // on its bell
    [InlineData(90f, false)] // on its side
    public void A_touchdown_makes_one_touchdown_sound(float leanDegrees, bool legsOut)
    {
        // Let go 20 cm over level ground (touching it at 2 m/s): its touch is judged both from its lowest point
        // coming within reach of the ground and from Jolt's contact an update or so later, but it is
        // one touch, so one sound.
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        using var sim = Drops.At(at, downhill, 0f, leanDegrees: leanDegrees, clearance: 0.2f, legsOut: legsOut);
        var vessel = sim.Flight.Controlled;
        var sounds = new List<float>();
        sim.Run(Nobody, f =>
        {
            foreach (var sound in f.Sounds)
                if (sound.Cue == SoundCue.Touchdown)
                    sounds.Add(sound.Strength);
            return vessel.State != FlightState.Flying;
        }, maxSeconds: 30);
        output.WriteLine($"lean {leanDegrees}°, legs {(legsOut ? "out" : "stowed")}: {vessel.State} {vessel.CrashReason}, touchdown {vessel.TouchdownSpeed:0.00} m/s; sounds at {string.Join(", ", sounds.Select(t => t.ToString("0.00")))}");
        Assert.Equal(FlightState.Landed, vessel.State);
        Assert.Single(sounds);
    }

    [Theory]
    [InlineData(4f, true)]
    [InlineData(8f, false)]
    public void Coming_down_beside_a_zone_from_far_above_it_is_judged_as_ever(float rate, bool lands)
    {
        // Upright, its legs out, 200 m over the ground 13 m out from Home's landing zone's edge (east,
        // between its masts), coming down at `rate` held by its engine. High up only the ground is
        // within its reach, so it is measured from afar (Vessel.MeasureFar); lower, the zone is (a
        // plinth reaches 50 m over its top: Planet.OnlyGroundNear), and it is read closely the rest
        // of the way down. It lands, or crashes, at its descent rate as anywhere else.
        var zone = Scene.LandingZoneCentre;
        var east = Planet.East(zone);
        var at = zone + east * (Scene.LandingZoneRadius + 13f);
        at = Planet.Centre + Planet.Up(at) * (Planet.SeaRadius + Terrain.Height(Planet.Up(at)));
        var up = Planet.Up(at);
        Assert.True(Planet.OnlyGroundNear(at + up * 200f, 30f), "high up the zone is near: this shows nothing");
        Assert.False(Planet.OnlyGroundNear(at + up * 20f, 30f), "low down the zone isn't near: this shows nothing");
        using var sim = Drops.At(at, east, rate, clearance: 200f);
        var vessel = sim.Flight.Controlled;
        // Its descent held at the rate by its engine (as the autopilot does), cut as it touches.
        Pilot hold = (f, dt) =>
        {
            var hover = Scene.SurfaceGravity / f.MaxAcceleration;
            var throttle = Math.Clamp(hover + (-rate - f.VerticalSpeed) * 0.25f, 0f, 1f);
            return new FlightInput(Throttle: Pilots.ThrottleTowards(f, throttle, dt), Hold: Planet.Up(vessel.Position), CutThrottle: f.Ground < 0.2f);
        };
        var falling = 0f;
        sim.Run(hold, f =>
        {
            if (vessel.TouchdownSpeed == 0f && f.Ground > 1f)
                falling = -vessel.VerticalSpeed; // as it comes down to the ground
            return vessel.State != FlightState.Flying;
        }, maxSeconds: 120);
        output.WriteLine($"at {rate} m/s: {vessel.State} {vessel.CrashReason}, touchdown {vessel.TouchdownSpeed:0.00} m/s, coming down at {falling:0.00} m/s");
        if (lands)
            Assert.Equal(FlightState.Landed, vessel.State);
        else
        {
            Assert.Equal(FlightState.Crashed, vessel.State);
            Assert.StartsWith("Hit the ground at", vessel.CrashReason);
        }
        Assert.InRange(vessel.TouchdownSpeed, falling - 0.5f, falling + 1f); // what it came down at, and what the last metre added
    }

    [Fact]
    public void Sliding_onto_the_next_ground_tile_is_no_new_touch()
    {
        // A booster lying on level ground, along a tile's u (across) with its nose a metre short of
        // the tile's edge, landed and frozen there; woken, then pushed along its length at 4 m/s,
        // nose first, across the edge onto the next tile. The next tile's contact is a contact of
        // its own, but no new touch: its body was already on the ground.
        var (level, _, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var cell = TerrainTiles.CellOf(GroundBody.Planet, level - Planet.Centre);
        TerrainTiles.Frame(GroundBody.Planet, cell, out var across, out _);
        var edge = Vector3.Normalize(TerrainTiles.Corner(GroundBody.Planet, cell.Face, cell.I + 1, cell.J) + TerrainTiles.Corner(GroundBody.Planet, cell.Face, cell.I + 1, cell.J + 1));
        var seam = Planet.Centre + edge * (Planet.SeaRadius + Terrain.Height(edge));
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var start = seam - across * (length + 1f);
        var lying = LyingAt(start, across);
        var touchdown = lying.TouchdownSpeed;

        using var sim = new Simulation(world: new WorldState { Vessels = [lying], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        Assert.True(vessel.Craft.Frozen);
        sim.Physics.Wake(vessel.Craft);
        sim.Run(Nobody, _ => false, maxSeconds: 0.5); // its contacts live again, where it lies
        Assert.Equal(FlightState.Landed, vessel.State);

        var touches = new List<float>();
        Vector3 Nose() => vessel.Position + sim.Physics.Up(vessel.Craft) * length;
        var crossed = sim.Run(Nobody, f =>
        {
            foreach (var sound in f.Sounds)
                if (sound.Cue == SoundCue.Touchdown)
                    touches.Add(sound.Strength);
            sim.Physics.SetVelocity(vessel.Craft, across * 4f);
            return TerrainTiles.CellOf(GroundBody.Planet, Nose() - Planet.Centre) != cell && Vector3.Dot(Nose() - seam, across) > 3f;
        }, maxSeconds: 3);
        output.WriteLine($"crossed {crossed}: {vessel.State} {vessel.CrashReason}, touchdown {touchdown:0.000} then {vessel.TouchdownSpeed:0.000} m/s; touches at {string.Join(", ", touches.Select(t => t.ToString("0.00")))}");
        Assert.True(crossed, "it never crossed onto the next tile");
        Assert.NotEqual(FlightState.Crashed, vessel.State);
        Assert.Equal(touchdown, vessel.TouchdownSpeed);
        Assert.DoesNotContain(touches, t => t > 1f);
    }

    [Theory]
    [InlineData(20f, true)]
    [InlineData(3f, false)]
    public void Sliding_on_its_side_into_a_pads_side_is_judged_by_how_hard_it_hits_it(float speed, bool crashes)
    {
        // A booster lying on the ground west of the 5° test pad, its nose under a metre short of the
        // pad's high (uphill) side and pointing at it, pushed along its length into it. Its body is on
        // the ground already, so the side is no new touch; touched hard (over Vessel's ImpactSpeed)
        // it is "Hit the ground", and touched
        // slowly it is nothing: no touch and no crash.
        var pad = Scene.TestPads[0];
        var (east, up) = (Planet.East(pad.Centre), Planet.Up(pad.Centre));
        var (sin, cos) = MathF.SinCos(float.DegreesToRadians(pad.SlopeDegrees));
        var edge = pad.Centre - east * (Scene.LandingZoneRadius * cos) + up * (Scene.LandingZoneRadius * sin);
        output.WriteLine($"the pad's side stands {Planet.TerrainAltitude(edge):0.00} m over the ground");
        Assert.True(Planet.TerrainAltitude(edge) > 2f, "the pad's side is too low for a lying booster to collide");
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var bottom = pad.Centre - east * (Scene.LandingZoneRadius + 0.5f + length);
        bottom = Planet.Centre + Planet.Up(bottom) * (Planet.SeaRadius + Terrain.Height(Planet.Up(bottom)));
        var lying = LyingAt(bottom, east);
        var touchdown = lying.TouchdownSpeed;

        using var sim = new Simulation(world: new WorldState { Vessels = [lying], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        sim.Physics.Wake(vessel.Craft);
        sim.Run(Nobody, _ => false, maxSeconds: 0.5); // its contacts live again, where it lies
        Assert.Equal(FlightState.Landed, vessel.State);

        // Pushed until it touches the side (the seams it slides over are touched hardly at all into them).
        var touched = 0f;
        Vector3 Nose() => vessel.Position + sim.Physics.Up(vessel.Craft) * length;
        output.WriteLine($"its nose is {Vector3.Dot(edge - Nose(), east):0.00} m short of the side");
        sim.Run(Nobody, _ =>
        {
            foreach (var touch in sim.Physics.TouchesBegun(vessel.Craft))
                if (touch.With == SolidKind.Ground)
                    touched = MathF.Max(touched, -Vector3.Dot(touch.Closing, touch.Normal));
            if (vessel.State != FlightState.Crashed && touched < speed / 2f)
                sim.Physics.SetVelocity(vessel.Craft, east * speed);
            return vessel.State == FlightState.Crashed;
        }, maxSeconds: 3f + 3f / speed);
        output.WriteLine($"at {speed} m/s: {vessel.State} {vessel.CrashReason}, touched the ground at up to {touched:0.00} m/s into it; touchdown {touchdown:0.000} then {vessel.TouchdownSpeed:0.000} m/s");
        if (crashes)
        {
            Assert.Equal(FlightState.Crashed, vessel.State);
            Assert.StartsWith("Hit the ground at", vessel.CrashReason);
            // Touched over ImpactSpeed into the side, so the hard touch crashed it, not a skid re-touch.
            Assert.True(touched > Vessel.ImpactSpeed, $"it touched the side at only {touched:0.00} m/s into it: the crash came from something else");
        }
        else
        {
            Assert.True(touched > speed / 2f, "it never touched the pad's side: this shows nothing");
            Assert.NotEqual(FlightState.Crashed, vessel.State);
            Assert.Equal(touchdown, vessel.TouchdownSpeed);
        }
    }

    [Fact]
    public void A_fairing_half_that_comes_down_on_another_vessel_is_told_so()
    {
        // A fairing half let go 2 m over the middle of a booster lying on level ground (landed and
        // frozen there): it is taken out as it comes down on the booster, not "to the ground"; and
        // another let go beside it, on the ground.
        var (level, east, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var up = Planet.Up(level);
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var lying = LyingAt(level, east);
        var middle = lying.Position + Vector3.Transform(Vector3.UnitY, lying.Rotation) * (length / 2f);
        VesselState Half(int id, Vector3 over) => new()
        {
            Id = id, Flight = 0, Mission = Mission.DeploySatellite, Vehicle = Vehicle.FairingHalf, First = 0, Last = 0,
            Fuel = [0f], State = FlightState.Flying, Position = over, Rotation = Planet.UprightAt(over),
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(Vehicle.FairingHalf, 0, 0),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero, Aim = up, MaxAltitude = 400f, HasBeenAirborne = true,
        };
        var beside = middle + Vector3.Normalize(Vector3.Cross(up, east)) * 30f;
        var onIt = Half(1, middle + up * (Vehicle.Booster.Stages[0].Radius + 2f));
        var onGround = Half(2, Planet.Centre + Planet.Up(beside) * (Planet.SeaRadius + Terrain.Height(Planet.Up(beside)) + 2f));
        using var sim = new Simulation(Mission.DeploySatellite, world: new WorldState { Vessels = [lying, onIt, onGround], NextVesselId = 3 });
        var (booster, first, second) = (sim.Flight.WorldVessels.First(v => v.Id == 0), sim.Flight.WorldVessels.First(v => v.Id == 1), sim.Flight.WorldVessels.First(v => v.Id == 2));
        // Both watched (a fairing half can't be flown): so they come down in the physics, whose
        // judging this is, the booster under the first kept there by its nearness.
        var halves = new[] { first, second };
        sim.Step(new FlightInput(SwitchTo: first, Watched: halves));
        sim.Run((_, _) => new FlightInput(Watched: halves), _ =>
        {
            Assert.False(first.OnFormula || second.OnFormula || booster.OnFormula, "one is under formula");
            return first.State != FlightState.Flying && second.State != FlightState.Flying;
        }, maxSeconds: 5);
        output.WriteLine($"on the booster: {first.State} {first.CrashReason}; beside it: {second.State} {second.CrashReason}; the booster {booster.State} {booster.CrashReason}");
        Assert.Equal("Fell onto another vessel.", first.CrashReason);
        Assert.Equal("Fell to the ground.", second.CrashReason);
        Assert.NotEqual(FlightState.Crashed, booster.State);
    }

    /// <summary>A booster lying on its side at <paramref name="at"/>, its nose towards <paramref name="towards"/>, landed and frozen there, as saved.</summary>
    private static VesselState LyingAt(Vector3 at, Vector3 towards)
    {
        using var sim = Drops.At(at, towards, 0.5f, leanDegrees: 90f, legsOut: false);
        var lying = sim.Flight.Controlled;
        Assert.True(sim.Run(Nobody, _ => lying.Craft.Frozen, maxSeconds: 60), $"it never froze: {lying.State} {lying.CrashReason}");
        Assert.Equal(FlightState.Landed, lying.State);
        return sim.Flight.Capture().Vessels.Single(v => v.Id == 0);
    }

    [Fact]
    public void A_drone_ships_mast_touched_gently_wrecks_the_vessel()
    {
        // Upright, its legs stowed, 2 m over the deck and 0.1 m from a mast's inboard side, moving
        // into it at 0.5 m/s against the ship: a mast wrecks on any touch, however gentle.
        Rafts.Time = 0.0;
        var ship = Rafts.Ships[0];
        ship.Set(0.0);
        var mast = PadLights.All.First(p => p.Ship == ship).Masts[0];
        var inboard = new Vector3(-MathF.Sign(mast.Foot.X), 0f, 0f); // along the ship, towards its middle
        var radius = Vehicle.Booster.Stages[0].Radius;
        var local = mast.Foot + new Vector3(0f, Vehicle.Booster.Stages[0].BellHeight + 2f, 0f) + inboard * (Scene.MastHalfWidth + radius + 0.1f);
        var pose = ship.Current;
        var origin = pose.Position + Vector3.Transform(local, pose.Rotation);
        var state = Drops.Placed(origin, Planet.East(origin), 0f, legsOut: false) with
        {
            Position = origin,
            Rotation = pose.Rotation,
            Aim = Vector3.Transform(Vector3.UnitY, pose.Rotation),
            Velocity = ship.VelocityAt(origin) - Vector3.Transform(inboard, pose.Rotation) * 0.5f,
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 2);
        output.WriteLine($"{vessel.State} {vessel.CrashReason}");
        Assert.Equal("Hit a light mast.", vessel.CrashReason);
    }

    /// <summary>A booster standing on the landing zone (on its feet, or <paramref name="legsOut"/> not, its bell), landed and frozen there, as saved.</summary>
    private static VesselState Standing(bool legsOut = true)
    {
        var zone = Scene.LandingZoneCentre;
        // On its bell, let go 10 cm up: well under the bell's limit.
        using var sim = legsOut ? Drops.At(zone, Planet.East(zone), 1f) : Drops.At(zone, Planet.East(zone), 0f, clearance: 0.1f, legsOut: false);
        var booster = sim.Flight.Controlled;
        Assert.True(sim.Run(Nobody, _ => booster.Craft.Frozen, maxSeconds: 60), $"it never froze: {booster.State}");
        return sim.Flight.Capture().Vessels.Single(v => v.Id == 0);
    }

    [Fact]
    public void Woken_where_it_stands_it_is_not_touched_down_again()
    {
        // Restored landed (frozen), then woken by another tossed up beside it, its body a metre inside
        // Scene.WakeMargin of this one's, clear of its feet: its contacts with the zone begin again as
        // it wakes, but it hasn't come down on it again. The other is looked at only while it rises.
        var radius = Vehicle.Booster.Stages[0].Radius;
        var standing = Standing();
        var up = Planet.Up(standing.Position);
        var east = Planet.East(standing.Position);
        var passer = Drops.Placed(Scene.LandingZoneCentre, east, 0f, legsOut: false, id: 1) with
        {
            Position = standing.Position + east * (2f * radius + Scene.WakeMargin - 1f) + up * 2f,
            Velocity = up * 8f,
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [standing, passer], NextVesselId = 2 });
        var (below, above) = (sim.Flight.WorldVessels.First(v => v.Id == 0), sim.Flight.WorldVessels.First(v => v.Id == 1));
        sim.Step(new FlightInput(SwitchTo: above));
        var woken = false;
        sim.Run(Nobody, _ => { woken |= !below.Craft.Frozen; return false; }, maxSeconds: 1.5);
        output.WriteLine($"woken {woken}: {below.State} {below.CrashReason}, touchdown {standing.TouchdownSpeed:0.000} then {below.TouchdownSpeed:0.000} m/s");
        Assert.True(woken, "it was never woken: this shows nothing");
        Assert.Equal(FlightState.Landed, below.State);
        Assert.Equal(standing.TouchdownSpeed, below.TouchdownSpeed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lighting_its_engine_where_it_stands_it_is_not_touched_down_again(bool legsOut)
    {
        // Restored landed (frozen), on its feet or its bell, then its engine lit at a throttle just
        // up from nothing, so it sits there under its weight (TakeOff wakes it): its contacts with
        // the zone begin again in the update it wakes (its bell's a touch at a few cm/s, as gravity
        // presses it on), but it hasn't come down on it again.
        var standing = Standing(legsOut);
        using var sim = new Simulation(world: new WorldState { Vessels = [standing], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        Assert.True(vessel.Craft.Frozen);
        sim.Step(new FlightInput(Throttle: 1f));
        output.WriteLine($"{vessel.State}, throttle {vessel.Throttle:0.000}, frozen {vessel.Craft.Frozen}, engine lit {vessel.EngineLit}: touchdown {standing.TouchdownSpeed:0.000} then {vessel.TouchdownSpeed:0.000} m/s");
        Assert.Equal(FlightState.Flying, vessel.State);
        Assert.False(vessel.Craft.Frozen);
        Assert.Equal(standing.TouchdownSpeed, vessel.TouchdownSpeed);
    }
}
