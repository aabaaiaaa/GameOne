using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The crush legs (<see cref="Scene.LegStroke"/>, <see cref="Scene.LegHoldAcceleration"/>): a
/// touchdown is taken over a moment as the legs give, not stopped dead; it comes to rest on them,
/// held where they gave to, without bouncing; and coming down off centre, the first feet down
/// slow it, so the others touch the ground more gently. The drawn legs match.
/// </summary>
public sealed class LegTests(ITestOutputHelper output)
{
    [Fact]
    public void The_legs_give_as_they_take_a_landing_and_settle_without_bouncing()
    {
        using var sim = new Simulation();
        var pilot = new Autopilot { TouchdownSpeed = 4f };
        var flight = sim.Flight;
        var hardest = 0f;
        var last = Vector3.Zero;
        var touchedAt = double.NaN;
        var rose = 0f;
        var lowestAfter = float.MaxValue;
        Assert.True(sim.RunToEnd((f, dt) =>
        {
            var v = f.Velocity;
            if (f.Controlled.Craft.LegExtension.X < 1f || f.Controlled.Craft.LegExtension.Z < 1f)
            {
                if (double.IsNaN(touchedAt))
                    touchedAt = sim.Time;
                hardest = MathF.Max(hardest, (v - last).Length());
                // Once down, it never springs back up.
                var height = Vector3.Dot(f.Position - Planet.Centre, f.LocalUp);
                lowestAfter = MathF.Min(lowestAfter, height);
                rose = MathF.Max(rose, height - lowestAfter);
            }
            last = v;
            return pilot.Fly(f, dt);
        }));
        var e = flight.Controlled.Craft.LegExtension;
        output.WriteLine($"touchdown {flight.TouchdownSpeed:0.0} m/s; hardest change in one frame {hardest:0.00} m/s; rose {rose:0.000} m after; "
            + $"legs at rest at {e.X:0.000}, {e.Y:0.000}, {e.Z:0.000}, {e.W:0.000} of their stroke ({e.X * LegGeometry.Rocket.Stroke:0.00} m out)");
        Assert.Equal(FlightState.Landed, flight.State);
        Assert.InRange(flight.TouchdownSpeed, 3f, Scene.SafeTouchdownSpeed);
        Assert.True(hardest < flight.TouchdownSpeed * 0.5f, $"the legs should take it over a moment, not in one frame ({hardest:0.00} m/s at once)");
        Assert.True(rose < 0.02f, $"it bounced back up {rose:0.000} m");
        // On level ground they all hold alike, and they gave as they took it.
        float[] legs = [e.X, e.Y, e.Z, e.W];
        Assert.True(legs.Max() - legs.Min() < 0.05f, $"the legs are out {string.Join(", ", legs.Select(x => $"{x:0.000}"))}");
        Assert.All(legs, x => Assert.True(x < 1f, $"a leg is still at full stroke ({x:0.000})"));
    }

    [Fact]
    public void The_drawn_foot_is_its_extension_of_the_stroke_below_the_colliders()
    {
        // The collider's foot is where the leg is pushed right up; the drawn one goes straight down
        // the body from there, by the extension times the stroke, so it touches the ground where
        // Cushion measured it.
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
            foreach (var extension in (ReadOnlySpan<float>)[0f, 0.25f, 0.5f, 1f])
            {
                var drawn = Vector3.Transform(new Vector3(0, LegGeometry.Rocket.Length, 0), RocketGeometry.LegTransform(LegGeometry.Rocket, leg, 1f, extension))
                    - new Vector3(0, RocketGeometry.FootThickness, 0);
                var off = drawn - (RocketGeometry.Foot(LegGeometry.Rocket, leg) - new Vector3(0, extension * LegGeometry.Rocket.Stroke, 0));
                Assert.True(off.Length() < 0.005f, $"leg {leg} at {extension}: the drawn foot is {off} off");
            }
    }

    [Fact]
    public void A_landed_booster_loaded_from_a_save_stands_on_its_feet_with_its_legs_out_as_they_held_it()
    {
        using var sim = new Simulation();
        Assert.True(sim.RunToEnd(new Autopilot().Fly));
        var landed = sim.Flight.Controlled;
        Assert.Equal(FlightState.Landed, landed.State);
        var before = landed.Craft.LegExtension;

        // Saved and loaded back through the store, as a world is between sessions.
        var directory = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorldStore(directory);
            store.Save(sim.Flight.Capture() with { Id = "legs", Name = "Legs" });
            using var again = new Simulation(Mission.LaunchAndLand, world: store.Load("legs"));
            var booster = again.Flight.WorldVessels.Single(v => v.Id == landed.Id);
            Assert.True(booster.Craft.Frozen);
            var e = booster.Craft.LegExtension;
            output.WriteLine($"legs at {before} when landed, {e} loaded");
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
            {
                // The drawn foot is its stroke's extension below the collider's.
                var gap = Planet.SurfaceAltitude(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg), booster.Model));
                var drawn = gap - e[leg] * LegGeometry.Rocket.Stroke;
                output.WriteLine($"leg {leg}: collider foot {gap * 1000:0.0} mm up, drawn foot {drawn * 1000:0.0} mm");
                Assert.InRange(drawn, -0.01f, 0.01f);
                Assert.InRange(e[leg] - before[leg], -0.02f, 0.02f); // as far out as they held it

            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Coming_down_off_centre_the_first_legs_down_give_until_the_others_reach_the_ground()
    {
        // A booster with its legs out, leaning 8°, coming down at 3 m/s with its lowest foot (at
        // full stroke) half a metre above the landing zone; the engine off.
        var vehicle = Vehicle.Booster;
        var tilt = Quaternion.CreateFromAxisAngle(Planet.East(Scene.LandingZoneCentre), float.DegreesToRadians(8f)) * Scene.LandingZoneTilt;
        var up = Planet.Up(Scene.LandingZoneCentre);
        var position = Scene.LandingZoneCentre + up * 10f;
        var lowest = Enumerable.Range(0, RocketGeometry.LegCount)
            .Min(leg => Planet.SurfaceAltitude(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg), Matrix4x4.CreateFromQuaternion(tilt) * Matrix4x4.CreateTranslation(position))));
        position -= up * (lowest - LegGeometry.Rocket.Stroke - 0.5f);
        var aim = Vector3.Transform(Vector3.UnitY, tilt);
        var state = new VesselState
        {
            Id = 0, Flight = 0, Mission = Mission.LaunchAndLand, Vehicle = vehicle, First = 0, Last = 0,
            Fuel = [vehicle.Stages[0].FuelMass * 0.3f],
            State = FlightState.Flying, Position = position, Rotation = tilt, Velocity = -up * 3f, AngularVelocity = Vector3.Zero,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 0, 0),
            Aim = aim, LegsWanted = true, LegDeploy = 1f, MaxAltitude = 400f, HasBeenAirborne = true,
        };
        using var sim = new Simulation(Mission.LaunchAndLand, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: booster)); // flown, so a watched run follows it

        // How far above the first feet's ground the last feet start (both at full stroke).
        var gaps = Enumerable.Range(0, RocketGeometry.LegCount)
            .Select(leg => Planet.SurfaceAltitude(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg), booster.Model))).ToArray();
        var higher = gaps.Max() - gaps.Min();

        // How fast each foot is coming down as it touches the ground (its leg first giving), and how
        // far out the legs already down still are as the last one touches it.
        var touched = new float?[RocketGeometry.LegCount];
        var stillOut = float.NaN;
        sim.Run((_, _) => default, _ =>
        {
            var craft = booster.Craft;
            var centre = sim.Physics.CentreOfMass(craft);
            var spin = sim.Physics.AngularVelocity(craft);
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
            {
                if (touched[leg] is not null || craft.LegExtension[leg] >= 1f)
                    continue;
                var foot = Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg), booster.Model);
                touched[leg] = -Vector3.Dot(booster.Velocity + Vector3.Cross(spin, foot - centre), Planet.Up(foot));
                if (touched.All(s => s is not null) && float.IsNaN(stillOut))
                    stillOut = Enumerable.Range(0, RocketGeometry.LegCount).Min(l => craft.LegExtension[l]);
            }
            return booster.State != FlightState.Flying;
        }, maxSeconds: 10);

        var first = touched.Max() ?? float.NaN;
        var lastDown = touched.Min() ?? float.NaN;
        // Falling freely the last stretch, the last feet would come down this fast.
        var unslowed = MathF.Sqrt(first * first + 2f * Scene.SurfaceGravity * higher);
        output.WriteLine($"feet touched the ground at {string.Join(", ", touched.Select(s => s is { } x ? $"{x:0.00}" : "never"))} m/s "
            + $"(the last {higher:0.00} m higher: {unslowed:0.00} m/s unslowed); the first legs still {stillOut:0.00} out then; "
            + $"{booster.State}, {booster.CrashReason}; leaning {booster.TiltDegrees:0.00}°");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.All(touched, s => Assert.NotNull(s));
        // The legs share the load (Scene.LegsToHold): the first feet down give, at the settle hold,
        // until the others reach the ground, rather than holding and turning the craft hard onto
        // them. They still slow it, so the others come down softer, and well under the speed they
        // would have reached unslowed; and they don't run out of stroke before the others are down.
        Assert.True(lastDown < first, $"the last feet down ({lastDown:0.00} m/s) should come down softer than the first ({first:0.00} m/s)");
        Assert.True(lastDown < unslowed * 0.8f, $"the last feet down ({lastDown:0.00} m/s) should come down well under the {unslowed:0.00} m/s they would reach unslowed");
        Assert.True(stillOut > 0f, "the first legs ran out of stroke before the others were down");
    }
}
