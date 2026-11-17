using System.Numerics;
using GameOne.Physics;
using GameOne.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The lander (<see cref="StageKind.Lander"/>) and its launcher (<see cref="Vehicle.LanderLauncher"/>):
/// its engine's numbers against the moon's pull and the upper stage's, the rover's mass in it, it
/// and its stowed legs inside its wider fairing, the stack's weight for its booster; landed on the
/// moon's plains on its legs, and on its bell without them; its landing lights in the moon's night;
/// and the launcher's fairing jettisoned in two halves of its own, then the lander let go.
/// </summary>
public sealed class LanderTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;
    private static GroundBody Moon => GroundBody.Moon;
    private static Stage Lander => Vehicle.LanderLauncher.Stages[2];

    /// <summary>The lander on its own, a vehicle of one stage (no fairing round it), for dropping onto the moon.</summary>
    internal static readonly Vehicle Alone = new("Lander alone", [Lander with { Fairing = false }]);

    /// <summary>The whole stack's mass, full: every stage's, and the payload's fairing.</summary>
    private static float Full(Vehicle vehicle) =>
        vehicle.Stages.Sum(s => s.DryMass + s.FuelMass) + vehicle.Stages.Where(s => s.Fairing).Sum(s => s.FairingGeometry.Mass);

    [Fact]
    public void The_landers_engine_lifts_about_1_8_times_its_full_weight_on_the_moon()
    {
        var ratio = Lander.Thrust / ((Lander.DryMass + Lander.FuelMass) * Scene.MoonSurfaceGravity);
        output.WriteLine($"thrust {Lander.Thrust:0} N, {ratio:0.000} times its full weight on the moon");
        Assert.InRange(ratio, 1.75f, 1.85f);
    }

    [Fact]
    public void The_landers_engine_is_weaker_than_the_upper_stages_but_more_efficient()
    {
        var upper = Vehicle.LanderLauncher.Stages[1];
        var dv = Lander.ExhaustVelocity * MathF.Log((Lander.DryMass + Lander.FuelMass) / Lander.DryMass);
        output.WriteLine($"lander: {Lander.Thrust:0} N, exhaust {Lander.ExhaustVelocity:0} m/s, {dv:0} m/s of speed change; upper stage: {upper.Thrust:0} N, exhaust {upper.ExhaustVelocity:0} m/s");
        Assert.True(Lander.Thrust < upper.Thrust);
        Assert.True(Lander.ExhaustVelocity > upper.ExhaustVelocity);
    }

    [Fact]
    public void The_lander_carries_the_rovers_300_kg()
    {
        Assert.Equal(300f, Scene.RoverAllowance);
        Assert.True(Lander.DryMass >= Scene.RoverAllowance + 500f, $"dry {Lander.DryMass} kg");
        Assert.Equal(StageKind.Lander, Lander.Kind);
        Assert.Equal("Lander", Lander.Name);
        Assert.True(Lander is { Guided: true, Legs: true, Bay: true, Fairing: true });
        Assert.Same(LegGeometry.Lander, Lander.LegGeometry);
        Assert.Same(FairingGeometry.Lander, Lander.FairingGeometry);
        Assert.Same(Vehicle.LanderFairingHalf, Vehicle.HalfOf(FairingGeometry.Lander));
    }

    [Fact]
    public void The_lander_and_its_stowed_legs_fit_inside_its_fairing()
    {
        var fairing = FairingGeometry.Lander;
        // The fairing is drawn in flat slices: the nearest its shell comes to the axis is the middle of one.
        var inner = fairing.Radius * MathF.Cos(MathF.PI / RocketMesh.Slices);
        var corner = new Vector2(Scene.LanderHalfWidth, Scene.LanderHalfWidth).Length();
        Assert.Equal(Scene.LanderCornerRadius, corner, 4);
        Assert.True(corner < inner, $"its corners {corner:0.000} m out, its fairing's shell {inner:0.000}");
        Assert.True(Lander.Height < fairing.Height, $"{Lander.Height} m tall, under a fairing {fairing.Height} m to its nose");

        // Every point of each stowed leg's mesh, as drawn.
        var legs = RocketMesh.BuildLeg(Lander.LegGeometry);
        var (widest, highest) = (0f, 0f);
        for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
        {
            var stowed = RocketGeometry.LegTransform(Lander.LegGeometry, leg, 0f);
            foreach (var vertex in legs)
            {
                var p = Vector3.Transform(new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), stowed);
                (widest, highest) = (MathF.Max(widest, new Vector2(p.X, p.Z).Length()), MathF.Max(highest, p.Y));
            }
        }
        output.WriteLine($"corners {corner:0.000} m out; stowed legs {widest:0.000} m out, up to {highest:0.00} m; the fairing's shell {inner:0.000} m out, {fairing.Height} m up");
        Assert.True(widest < inner, $"its stowed legs reach {widest:0.000} m out, past its fairing's {inner:0.000}");
        Assert.True(highest < fairing.Height);
    }

    [Fact]
    public void The_lander_launchers_stack_weighs_what_its_booster_is_written_for()
    {
        var (lander, satellite) = (Full(Vehicle.LanderLauncher), Full(Vehicle.ThreeStage));
        output.WriteLine($"lander launcher {lander:0} kg, satellite launcher {satellite:0} kg");
        Assert.True(lander <= 22_200f, $"{lander:0} kg");
        Assert.InRange(lander - satellite, -300f, 300f);
        // Its booster is the satellite launcher's, and its upper stage that one's, left round the moon.
        Assert.Same(Vehicle.ThreeStage.Stages[0], Vehicle.LanderLauncher.Stages[0]);
        Assert.Equal(Vehicle.ThreeStage.Stages[1] with { LandingReserve = 0f }, Vehicle.LanderLauncher.Stages[1]);
    }

    /// <summary>
    /// A world on the moon of the lander alone, upright over the landing spot (<see cref="SpotSky.Place"/>),
    /// its engine off, its lowest point (its lowest foot hanging at full stroke, with
    /// <paramref name="legsOut"/>; else its bell) <paramref name="clearance"/> over the ground,
    /// falling at <paramref name="speed"/>, flown.
    /// </summary>
    private static Simulation Dropped(float speed, float clearance, bool legsOut)
    {
        var at = SpotSky.Place;
        var state = Drops.Placed(at, Moon.East(at), speed, Alone, clearance: clearance, legsOut: legsOut, mission: Mission.FreePlay, body: Moon);
        var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 }, body: Moon);
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0)));
        return sim;
    }

    [Fact]
    public void On_its_legs_the_lander_lands_on_the_moons_plains_and_stands()
    {
        using var sim = Dropped(2f, 3f, legsOut: true);
        var lander = sim.Flight.Controlled;
        Assert.Same(Alone, lander.Vehicle);
        double? landed = null, frozen = null;
        sim.Run(Nobody, _ =>
        {
            if (landed is null && lander.State == FlightState.Landed)
                landed = sim.Time;
            if (frozen is null && lander.Craft.Frozen)
                frozen = sim.Time;
            return lander.State == FlightState.Crashed || frozen is not null;
        }, maxSeconds: 40);
        output.WriteLine($"{lander.State} {lander.CrashReason}: touchdown {lander.TouchdownSpeed:0.00} m/s, landed at {landed:0.0} s, frozen at {frozen:0.0} s, leaning {lander.TiltDegrees:0.00}°");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.True(lander.TiltDegrees < 1f, $"it leans {lander.TiltDegrees:0.00}°");
        Assert.NotNull(frozen);
        Assert.True(frozen - landed <= 10.0, $"frozen {frozen - landed:0.0} s after settling");
    }

    [Fact]
    public void A_lander_touching_down_on_stowed_legs_is_judged_on_its_bell()
    {
        foreach (var (speed, safe) in (ReadOnlySpan<(float, bool)>)[(2f, true), (4f, false)])
        {
            using var sim = Dropped(speed, 0.05f + speed * 0.05f, legsOut: false); // clear of the ground for the frame its switch is made in
            var lander = sim.Flight.Controlled;
            sim.Run(Nobody, _ => lander.State != FlightState.Flying, maxSeconds: 20);
            output.WriteLine($"at {speed} m/s: {lander.State} {lander.CrashReason}, touchdown {lander.TouchdownSpeed:0.00} m/s");
            if (safe)
                Assert.Equal(FlightState.Landed, lander.State);
            else
            {
                Assert.Equal(FlightState.Crashed, lander.State);
                Assert.Equal(lander.OnItsBell(lander.TouchdownSpeed), lander.CrashReason);
            }
        }
    }

    [Fact]
    public void The_landers_landing_lights_come_on_in_the_moons_night()
    {
        // A dark time at the spot, by the game's own rule for it: past dusk however the rule is
        // held, a minute on from the first time it is.
        var dark = 0.0;
        while (!Sun.IsDark(Moon, SpotSky.Place, dark, wasDark: false))
        {
            dark += 60.0;
            Assert.True(dark < Scene.YearDays * (double)Scene.DaySeconds, "never dark at the spot within a year");
        }
        dark += 60.0;
        Assert.True(Sun.IsDark(Moon, SpotSky.Place, dark, wasDark: false), "the spot isn't dark a minute past dusk");

        var (position, rotation) = OnTheMoon.Standing(OnTheMoon.LandingSpot, LegGeometry.Lander);
        var state = new VesselState
        {
            Id = 0, Flight = 1, Mission = Mission.FreePlay, Vehicle = Alone, First = 0, Last = 0,
            Fuel = [Lander.FuelMass * 0.5f],
            State = FlightState.Landed, Position = position, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(Alone, 0, 0),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero,
            Aim = Vector3.Transform(Vector3.UnitY, rotation), LegsWanted = true, LegDeploy = 1f, HasBeenAirborne = true, TouchedDown = true,
            Body = Moon.Name,
        };
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Time = dark, Vessels = [state], NextVesselId = 1 }, body: Moon);
        var lander = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        Assert.True(lander.HasLandingLights);
        sim.Run(Nobody, _ => lander.LandingLights >= 1f, maxSeconds: 10);
        output.WriteLine($"{lander.State} at {sim.Flight.WorldTime:0} s: its lights {lander.LandingLights:0.00}");
        Assert.Equal(FlightState.Landed, lander.State);
        Assert.Equal(1f, lander.LandingLights);
    }

    [Fact]
    public void A_lander_fairing_half_coming_down_end_first_touches_down_on_its_foots_cone()
    {
        // Its cone hangs under its body as a bell would (and as narrow as the rocket at its bottom):
        // dropped upright onto the zone, it comes down on the cone's rim, not with it drawn into the ground.
        var half = Vehicle.LanderFairingHalf.Stages[0];
        Assert.Equal(FairingGeometry.Lander.ConeHeight, half.BellHeight);
        Assert.Equal(Scene.RocketRadius, half.BellRadius);
        Assert.Equal(0f, Vehicle.FairingHalf.Stages[0].BellHeight); // the satellite's, as wide as the rocket, has none
        var zone = Scene.LandingZoneCentre;
        var state = Drops.Placed(zone, Planet.East(zone), 2f, Vehicle.LanderFairingHalf, mission: Mission.FreePlay, legsOut: false);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var vessel = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: vessel));
        var lowest = float.MaxValue;
        sim.Run(Nobody, _ =>
        {
            for (var i = 0; i < 12; i++)
                lowest = MathF.Min(lowest, Planet.GroundAltitude(Vector3.Transform(RocketGeometry.BellRim(half, i, 12), vessel.Model)));
            return vessel.State != FlightState.Flying;
        }, maxSeconds: 5);
        output.WriteLine($"{vessel.State} {vessel.CrashReason}: the cone's rim at its lowest {lowest:0.000} m over the ground");
        Assert.NotEqual(FlightState.Flying, vessel.State);
        Assert.True(lowest > -0.01f, $"the cone's rim went {-lowest:0.000} m into the ground");
    }

    /// <summary>
    /// How far out from its axis <paramref name="shape"/> (a craft's collider, in its stage's own
    /// space but for its centre of mass) is first met by a ray coming in at <paramref name="y"/>
    /// up it, from <paramref name="from"/> (a level direction), through Jolt's own ray cast.
    /// </summary>
    private static float MetAt(Brutal.JoltApi.Shape shape, float y, Vector3 from)
    {
        const float start = 10f;
        var com = shape.GetCenterOfMass();
        var origin = from * start + new Vector3(0, y, 0) - new Vector3(com.X, com.Y, com.Z);
        var ray = new Brutal.JoltApi.RayCast { Origin = new(origin.X, origin.Y, origin.Z), Direction = new(-from.X * start, -from.Y * start, -from.Z * start) };
        var hit = new Brutal.JoltApi.RayCastResult { Fraction = 1.0001f };
        using var ids = Brutal.JoltApi.SubShapeIDCreator.Create();
        Assert.True(shape.CastRay(in ray, ids, ref hit), $"nothing met coming in from {from} at {y:0.00} m");
        return start * (1f - hit.Fraction);
    }

    [Fact]
    public void The_lander_fairings_foot_cone_is_in_the_colliders_where_it_is_drawn()
    {
        // The stack, fairing on: over its foot's cone (under the lander's base, round the top of
        // the upper stage) a cylinder as wide as the cone half-way down it; under the cone, the
        // upper stage's own body.
        var zone = Scene.LandingZoneCentre;
        var cone = FairingGeometry.Lander.ConeHeight;
        var state = Drops.Placed(zone, Planet.East(zone), 0f, Vehicle.LanderLauncher, first: 1, clearance: 100f, mission: Mission.FreePlay, fairingOn: true, legsOut: false);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var stack = sim.Flight.WorldVessels.Single(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: stack));
        var foot = RocketGeometry.StageBase(Vehicle.LanderLauncher, 1, 2);
        var (over, under) = (MetAt(stack.Craft.Stowed, foot - cone / 2, Vector3.UnitX), MetAt(stack.Craft.Stowed, foot - cone - 0.2f, Vector3.UnitX));
        output.WriteLine($"the stack: over its cone met {over:0.000} m out, under it {under:0.000}");
        Assert.Equal((FairingGeometry.Lander.Radius + Scene.RocketRadius) / 2, over, 0.02f);
        Assert.Equal(Scene.RocketRadius, under, 0.02f);

        // A half, jettisoned: its shell as wide as the fairing, and under it the cone's two steps
        // in, each as wide as the cone half-way down it, from the side and from its shell's side.
        sim.Step(new FlightInput(CutThrottle: true, Stage: true));
        var half = sim.Flight.WorldVessels.First(v => v.IsFairing).Craft.Stowed;
        var r = FairingGeometry.Lander.Radius;
        foreach (var from in (ReadOnlySpan<Vector3>)[Vector3.UnitX, Vector3.UnitZ])
        {
            var (shell, upper, lower) = (MetAt(half, 1f, from), MetAt(half, -cone / 4, from), MetAt(half, -cone * 3 / 4, from));
            output.WriteLine($"a half, from {from}: its shell met {shell:0.000} m out, its cone's upper step {upper:0.000}, its lower {lower:0.000}");
            Assert.Equal(r, shell, 0.02f);
            Assert.Equal(Scene.RocketRadius + (r - Scene.RocketRadius) * 0.75f, upper, 0.02f);
            Assert.Equal(Scene.RocketRadius + (r - Scene.RocketRadius) * 0.25f, lower, 0.02f);
        }
    }

    [Fact]
    public void The_lander_launcher_jettisons_its_fairing_then_releases_the_lander()
    {
        // Its upper stage and lander, fairing on, 5 km over the planet: staged, the fairing comes
        // away as two halves of the lander's own fairing; staged again, the lander parts from the
        // upper stage without either touching the other.
        var zone = Scene.LandingZoneCentre;
        var state = Drops.Placed(zone, Planet.East(zone), 0f, Vehicle.LanderLauncher, first: 1, clearance: 5000f, mission: Mission.FreePlay, fairingOn: true, legsOut: false);
        using var sim = new Simulation(Mission.FreePlay, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var stack = sim.Flight.WorldVessels.First(v => v.Id == 0);
        sim.Step(new FlightInput(SwitchTo: stack));
        Assert.True(stack.FairingOn);
        Assert.Equal(RocketGeometry.StageBase(Vehicle.LanderLauncher, 1, 2) + FairingGeometry.Lander.Height + FairingGeometry.Lander.NoseHeight, stack.Height, 3);
        Assert.True(sim.Flight.JettisonsFairing);

        sim.Step(new FlightInput(CutThrottle: true, Stage: true)); // the fairing
        Assert.False(sim.Flight.FairingOn);
        var halves = sim.Flight.WorldVessels.Where(v => v.IsFairing).ToArray();
        Assert.Equal(2, halves.Length);
        Assert.All(halves, half => Assert.Same(Vehicle.LanderFairingHalf, half.Vehicle));
        Assert.All(halves, half => Assert.Equal(FairingGeometry.Lander.Mass / 2f, half.Mass, 3));
        sim.Run(Nobody, _ => false, maxSeconds: 1);

        sim.Step(new FlightInput(CutThrottle: true, Stage: true)); // and the lander
        var lander = sim.Flight.WorldVessels.Single(v => v is { Vehicle.Name: "Lander launcher", Stage: 2 });
        var upper = sim.Flight.WorldVessels.Single(v => v is { Vehicle.Name: "Lander launcher", Stage: 1, TopStage: 1 });
        Assert.Equal("Lander", lander.Name);
        Assert.Equal(2, lander.TopStage);
        Assert.Same(upper.Craft, lander.Craft.PartingFrom);
        var touched = false;
        var start = Vector3.Distance(lander.Position, upper.Position);
        sim.Run(Nobody, _ =>
        {
            foreach (var part in (ReadOnlySpan<Vessel>)[lander, upper])
                foreach (var touch in sim.Physics.TouchesBegun(part.Craft))
                    touched |= touch.With == SolidKind.Craft;
            return false;
        }, maxSeconds: 5);
        var apart = Vector3.Distance(lander.Position, upper.Position);
        output.WriteLine($"5 s on: lander {lander.State} {lander.CrashReason}, upper stage {upper.State} {upper.CrashReason}; {start:0.0} m apart, then {apart:0.0} m; touched {touched}");
        Assert.False(touched, "they touched");
        Assert.NotEqual(FlightState.Crashed, lander.State);
        Assert.NotEqual(FlightState.Crashed, upper.State);
        Assert.True(apart > start + 2f, $"from {start:0.0} m apart to {apart:0.0} m");
        Assert.Null(lander.Craft.PartingFrom); // clear of it by now
    }
}
