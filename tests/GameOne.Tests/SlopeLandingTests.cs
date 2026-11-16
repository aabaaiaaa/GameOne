using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Standing on a slope: the legs hold the weight (up to <see cref="Scene.LegHoldAcceleration"/>
/// each), grip the ground (<see cref="Scene.FootFriction"/>) and each reach their own ground within
/// the stroke (<see cref="Scene.LegStroke"/>), so a booster stands across a gentle slope near
/// upright, and stays put on a steeper one rather than sliding down it. On the test range's pads
/// and on real ground. Each drop has leg 0 pointing downhill and leg 2 uphill (see <see cref="Drops"/>).
/// </summary>
public sealed class SlopeLandingTests(ITestOutputHelper output)
{
    private const int Downhill = 0, Uphill = 2;

    /// <summary>Lets the dropped vessel come down, for up to <paramref name="seconds"/>, and logs how it stood.</summary>
    private Vessel Land(Simulation sim, string where, float slope, double seconds = 20)
    {
        var vessel = sim.Flight.Controlled;
        sim.Run((_, _) => default, _ => vessel.State != FlightState.Flying, seconds);
        var e = vessel.Craft.LegExtension;
        output.WriteLine($"{where}: slope {slope:0.0}°, {vessel.State} {vessel.CrashReason}; lean {vessel.TiltDegrees:0.00}°; "
            + $"extensions {e.X:0.00} (downhill), {e.Y:0.00}, {e.Z:0.00} (uphill), {e.W:0.00}; touchdown {vessel.TouchdownSpeed:0.00} m/s");
        return vessel;
    }

    /// <summary>How far it leans now, from its pose (a frozen vessel's <see cref="Vessel.TiltDegrees"/> isn't measured again).</summary>
    private static float Lean(Vessel v) => float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(v.Up, v.LocalUp), -1f, 1f)));

    /// <summary>How far it moves over <paramref name="seconds"/> more.</summary>
    private float Drift(Simulation sim, Vessel vessel, double seconds)
    {
        var from = vessel.Position;
        sim.Run((_, _) => default, _ => false, seconds);
        var moved = Vector3.Distance(vessel.Position, from);
        output.WriteLine($"moved {moved * 1000:0.0} mm over {seconds} s more ({(vessel.Craft.Frozen ? "frozen" : "awake")})");
        return moved;
    }

    [Fact]
    public void It_stands_on_the_5_degree_pad_on_legs_of_their_own_length()
    {
        using var sim = Drops.OnPad(0, 2f);
        var booster = Land(sim, Scene.TestPads[0].Name, Scene.TestPads[0].SlopeDegrees);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.TiltDegrees < 2f, $"it leans {booster.TiltDegrees:0.0}°: within the stroke, the legs should take the slope up");
        var e = booster.Craft.LegExtension;
        Assert.True(e[Downhill] - e[Uphill] > 0.4f, $"the downhill leg is out {e[Downhill]:0.00}, the uphill one {e[Uphill]:0.00}");
        Assert.True(Drift(sim, booster, 10) < 0.05f);
    }

    [Fact]
    public void It_stays_put_on_the_15_degree_pad_rather_than_sliding_down_it()
    {
        // The legs push square to the pad's top, so the slope pushes it downhill: its feet's grip
        // holds it (tan 15° = 0.27, inside Scene.FootFriction). With no grip it slides off.
        using var sim = Drops.OnPad(2, 2f);
        var booster = Land(sim, Scene.TestPads[2].Name, Scene.TestPads[2].SlopeDegrees);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(Drift(sim, booster, 20) < 0.1f);
    }

    [Fact]
    public void Falling_over_from_upright_on_a_steep_pad_it_crashes_on_the_hit()
    {
        // tan 45° = 1, past Scene.FootFriction: the grip can't hold it, and it goes over downhill.
        // Falling over from upright, a tall booster's side touches the ground too fast: that hit
        // (not its lean) is what wrecks it.
        Assert.True(Scene.FootFriction < 1f);
        using var sim = Drops.OnPad(5, 2f);
        var booster = sim.Flight.Controlled;
        Vector3? touched = null;
        var slid = 0f;
        var up = Planet.Up(Scene.TestPads[5].Centre);
        sim.Run((_, _) => default, _ =>
        {
            var e = booster.Craft.LegExtension;
            if (touched is null && MathF.Min(MathF.Min(e.X, e.Y), MathF.Min(e.Z, e.W)) < 1f)
                touched = booster.Position;
            if (touched is { } from && booster.State != FlightState.Crashed)
            {
                var travel = booster.Position - from;
                slid = MathF.Max(slid, (travel - up * Vector3.Dot(travel, up)).Length());
            }
            return booster.State != FlightState.Flying;
        }, maxSeconds: 20);
        output.WriteLine($"{Scene.TestPads[5].Name}: {booster.State} {booster.CrashReason}; slid {slid:0.00} m along the ground from the first touch; lean {booster.TiltDegrees:0.0}°");
        Assert.Equal(FlightState.Crashed, booster.State);
        Assert.StartsWith("Hit the ground at", booster.CrashReason);
        Assert.True(booster.TouchdownSpeed > Scene.SafeTouchdownSpeed, $"hit at {booster.TouchdownSpeed:0.0} m/s");
    }

    [Fact]
    public void On_the_25_degree_pad_it_comes_to_rest_leaning_and_that_counts_as_landed()
    {
        using var sim = Drops.OnPad(4, 2f);
        var booster = Land(sim, Scene.TestPads[4].Name, Scene.TestPads[4].SlopeDegrees);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.TiltDegrees > 15f, $"it leans only {booster.TiltDegrees:0.00}°: this shows nothing");
    }

    /// <summary>
    /// Runs it awake (not frozen, so its legs keep working) for <paramref name="seconds"/>, and
    /// returns how far its lean strayed from <paramref name="from"/>, and how far it moved.
    /// </summary>
    private (float Strayed, float Moved) StandAwake(Simulation sim, Vessel vessel, float from, double seconds)
    {
        var most = 0f;
        var start = vessel.Position;
        sim.Run((_, _) =>
        {
            if (vessel.Craft.Frozen)
                sim.Physics.Wake(vessel.Craft);
            return default;
        }, _ =>
        {
            most = MathF.Max(most, MathF.Abs(Lean(vessel) - from));
            return false;
        }, seconds);
        var e = vessel.Craft.LegExtension;
        var moved = Vector3.Distance(vessel.Position, start);
        output.WriteLine($"over {seconds} s awake: {vessel.State}, lean {Lean(vessel):0.00}° (strayed at most {most:0.00}°), moved {moved * 1000:0.0} mm, "
            + $"legs {(vessel.Craft.LegsLocked ? "locked" : "unlocked")}, extensions {e.X:0.00} (downhill), {e.Y:0.00}, {e.Z:0.00} (uphill), {e.W:0.00}");
        return (most, moved);
    }

    [Fact]
    public void Once_all_four_feet_are_down_the_legs_lock_and_it_stands_leaning_on_the_25_degree_pad()
    {
        using var sim = Drops.OnPad(4, 2f);
        var booster = Land(sim, Scene.TestPads[4].Name, Scene.TestPads[4].SlopeDegrees);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.Craft.LegsLocked);
        Assert.True(StandAwake(sim, booster, Lean(booster), 20).Strayed < 0.5f);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.Craft.LegsLocked);
    }

    [Fact]
    public void Restored_standing_on_the_25_degree_pad_its_legs_are_still_locked()
    {
        // Leaning there, its uphill foot can be out of its stroke: the pose alone wouldn't say
        // the legs had locked, but having landed, they had.
        using var sim = Drops.OnPad(4, 2f);
        var landed = Land(sim, Scene.TestPads[4].Name, Scene.TestPads[4].SlopeDegrees);
        Assert.Equal(FlightState.Landed, landed.State);
        var lean = Lean(landed);
        var directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorldStore(directory);
            store.Save(sim.Flight.Capture() with { Id = "lock", Name = "Lock" });
            using var again = new Simulation(Mission.LaunchAndLand, world: store.Load("lock"));
            var booster = again.Flight.WorldVessels.Single(v => v.Id == landed.Id);
            var uphill = Planet.SurfaceAltitude(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, Uphill), booster.Model));
            output.WriteLine($"restored: lean {Lean(booster):0.00}° (landed at {lean:0.00}°), uphill foot {uphill:0.00} m up (stroke {LegGeometry.Rocket.Stroke} m)");
            Assert.True(booster.Craft.LegsLocked);
            Assert.True(StandAwake(again, booster, lean, 10).Strayed < 0.5f);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>How far a stack leans towards the gap between two legs (in 0.5° steps) before its centre of mass is past its feet.</summary>
    private static float TippingLean(Vehicle vehicle, int first, int last)
    {
        // Leg 0 faces out at 45° round the body, so +X is midway between legs 0 and 3.
        for (var degrees = 0f; degrees <= 90f; degrees += 0.5f)
            if (!RocketGeometry.OverFeet(vehicle, first, last, legsOut: true, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -float.DegreesToRadians(degrees))))
                return degrees;
        return float.NaN;
    }

    [Fact]
    public void A_taller_vessel_tips_at_a_smaller_lean()
    {
        var (booster, upper) = (Vehicle.Booster, Vehicle.ThreeStage);
        const int upperStage = 1; // the satellite launcher's upper stage, on its own once the satellite is off
        Assert.True(upper.Stages[upperStage].Legs);
        var (boosterHeight, upperHeight) = (RocketGeometry.StackHeight(booster, 0, 0), RocketGeometry.StackHeight(upper, upperStage, upperStage));
        var (boosterTips, upperTips) = (TippingLean(booster, 0, 0), TippingLean(upper, upperStage, upperStage));
        output.WriteLine($"booster: {boosterHeight:0.0} m tall, tips at {boosterTips:0.0}°; upper stage: {upperHeight:0.0} m tall, tips at {upperTips:0.0}°");
        Assert.True(RocketGeometry.OverFeet(booster, 0, 0, legsOut: true, Quaternion.Identity));
        Assert.NotEqual(boosterHeight, upperHeight);
        var (taller, shorter) = boosterHeight > upperHeight ? (boosterTips, upperTips) : (upperTips, boosterTips);
        Assert.True(taller < shorter, $"the taller tips at {taller:0.0}°, the shorter at {shorter:0.0}°");
    }

    [Fact]
    public void A_vessel_standing_on_the_10_degree_pad_is_saved_and_restored_on_its_legs()
    {
        using var sim = Drops.OnPad(1, 2f);
        var landed = Land(sim, Scene.TestPads[1].Name, Scene.TestPads[1].SlopeDegrees);
        Assert.Equal(FlightState.Landed, landed.State);
        var (position, rotation, lean, extension) = (landed.Position, Quaternion.CreateFromRotationMatrix(landed.Model), Lean(landed), landed.Craft.LegExtension);

        var directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorldStore(directory);
            store.Save(sim.Flight.Capture() with { Id = "slope", Name = "Slope" });
            using var again = new Simulation(Mission.LaunchAndLand, world: store.Load("slope"));
            var booster = again.Flight.WorldVessels.Single(v => v.Id == landed.Id);
            Assert.Equal(FlightState.Landed, booster.State);
            Assert.True(booster.Craft.LegsLocked);

            // Where it stood, as it stood, on legs as far out as they held it.
            var shift = Vector3.Distance(booster.Position, position);
            var turned = float.RadiansToDegrees(2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.CreateFromRotationMatrix(booster.Model), rotation)), 0f, 1f)));
            var e = booster.Craft.LegExtension;
            output.WriteLine($"restored: moved {shift * 1000:0.0} mm, turned {turned:0.00}°, lean {lean:0.00}° landed and {Lean(booster):0.00}° restored, "
                + $"extensions {extension} landed and {e} restored");
            Assert.True(shift < 0.05f);
            Assert.True(turned < 0.5f);
            Assert.InRange(Lean(booster) - lean, -0.5f, 0.5f);
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
                Assert.InRange(e[leg] - extension[leg], -0.05f, 0.05f);

            var (strayed, moved) = StandAwake(again, booster, lean, 10);
            Assert.Equal(FlightState.Landed, booster.State);
            Assert.True(strayed < 0.5f);
            Assert.True(moved < 0.05f);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void With_a_foot_over_the_pad_edge_it_lands_or_falls_and_never_hangs()
    {
        // Leg 2 (west, uphill) reaches a metre past the 5° pad's rim, over its plinth's high side,
        // further above the ground there than the stroke: only three feet can be down at first, so
        // the legs don't lock. Whatever it then does (settle on three, tip until a foot finds the
        // ground below, or go over), it must end up landed, or wrecked by how hard it came down.
        var pad = Scene.TestPads[0];
        var east = Planet.East(pad.Centre);
        var foot = RocketGeometry.Foot(LegGeometry.Rocket, Uphill);
        var reach = MathF.Sqrt(foot.X * foot.X + foot.Z * foot.Z);
        var at = pad.Centre - east * (Scene.LandingZoneRadius + 1f - reach);
        using var sim = Drops.At(at, east, 2f);
        var booster = sim.Flight.Controlled;
        var locked = false;
        sim.Run((_, _) => default, _ =>
        {
            locked |= booster.Craft.LegsLocked;
            return booster.State != FlightState.Flying;
        }, 30);
        var e = booster.Craft.LegExtension;
        output.WriteLine($"{pad.Name}, a foot over its high edge: {booster.State} {booster.CrashReason}; lean {booster.TiltDegrees:0.00}°; "
            + $"extensions {e.X:0.00} (downhill), {e.Y:0.00}, {e.Z:0.00} (uphill), {e.W:0.00}; {(locked ? "locked" : "never locked")}");
        Assert.True(booster.State == FlightState.Landed || booster.CrashReason!.StartsWith("Hit the ground at"), $"{booster.State} {booster.CrashReason}");
        Assert.False(locked);
    }

    [Fact]
    public void A_hard_touchdown_on_the_15_degree_pad_is_not_a_crash()
    {
        Assert.True(5f < Scene.SafeTouchdownSpeed);
        using var sim = Drops.OnPad(2, 5f);
        var booster = Land(sim, Scene.TestPads[2].Name, Scene.TestPads[2].SlopeDegrees);
        Assert.Equal(FlightState.Landed, booster.State);
    }

    [Fact]
    public void Coming_down_with_sideways_speed_the_feet_stop_it()
    {
        var zone = Scene.LandingZoneCentre;
        var east = Planet.East(zone);
        using var sim = Drops.At(zone, east, 2f, sideways: east * 1.5f);
        var booster = sim.Flight.Controlled;
        Vector3? touched = null;
        sim.Run((_, _) => default, _ =>
        {
            var e = booster.Craft.LegExtension;
            if (touched is null && MathF.Min(MathF.Min(e.X, e.Y), MathF.Min(e.Z, e.W)) < 1f)
                touched = booster.Position;
            return booster.State != FlightState.Flying;
        }, maxSeconds: 20);
        Assert.NotNull(touched);
        var up = Planet.Up(zone);
        var travel = booster.Position - touched.Value;
        var along = (travel - up * Vector3.Dot(travel, up)).Length();
        output.WriteLine($"{booster.State} {booster.CrashReason}; lean {booster.TiltDegrees:0.00}°; travelled {along:0.00} m along the ground from the first touch; touchdown {booster.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(along < 1.5f, $"it slid {along:0.00} m");
    }

    [Fact]
    public void It_stands_across_real_ground_that_slopes_gently()
    {
        var (at, downhill, degrees) = Drops.FindSlope(5f, 8f);
        using var sim = Drops.At(at, downhill, 2f);
        var booster = Land(sim, $"real ground at {at}", degrees);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.TiltDegrees < degrees - 3f, $"it leans {booster.TiltDegrees:0.0}° on a {degrees:0.0}° slope");
    }

    [Fact]
    public void It_stays_put_on_real_ground_that_slopes_more()
    {
        var (at, downhill, degrees) = Drops.FindSlope(12f, 18f);
        using var sim = Drops.At(at, downhill, 2f);
        var booster = Land(sim, $"real ground at {at}", degrees);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(Drift(sim, booster, 20) < 0.1f);
    }
}
