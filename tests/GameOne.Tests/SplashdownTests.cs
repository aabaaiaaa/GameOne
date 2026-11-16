using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Coming down in the sea (see <see cref="Vessel"/>'s contact rules): a vessel that touches the
/// water no faster than <see cref="Scene.SafeSplashdownSpeed"/> splashes down and floats, faster
/// it is lost; tipping over in the water is floating, judged by how fast each part touches it; a
/// splashdown counts as landing safely, but not on a landing zone; and standing on the seabed in
/// shallow water is landing.
/// </summary>
public sealed class SplashdownTests(ITestOutputHelper output)
{
    /// <summary>The deep water <see cref="WaterTests"/> floats things on, in the Sunrise Sea.</summary>
    private static Vector3 DeepWater => Planet.Centre + WaterTests.DeepWater.Value * Planet.SeaRadius;

    /// <summary>A booster (or the stages given), falling at <paramref name="speed"/> onto the deep water, as <see cref="Drops.At"/> drops one.</summary>
    private static Simulation Drop(float speed, Vehicle? vehicle = null, int first = 0, int? last = null, Mission? mission = null, int flight = 0, float clearance = Drops.Clearance, bool fairingOn = false) =>
        Drops.At(DeepWater, Planet.East(DeepWater), speed, vehicle, first: first, last: last, mission: mission, flight: flight, clearance: clearance, fairingOn: fairingOn);

    private static Vessel Dropped(Simulation sim) => sim.Flight.WorldVessels.First(v => v.Id == 0);

    private static readonly Pilot Nobody = (_, _) => default;

    [Fact]
    public void Afloat_its_engine_lifts_it_out_of_the_water()
    {
        using var sim = Drop(3f);
        var vessel = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 90));
        Assert.Equal(FlightState.Floating, vessel.State);
        sim.Run((_, _) => new FlightInput(FullThrottle: true), _ => false, 4);
        output.WriteLine($"{vessel.State} {vessel.CrashReason}, {vessel.Ground:0.0} m up");
        Assert.Equal(FlightState.Flying, vessel.State);
        Assert.True(vessel.Ground > 3f, $"only {vessel.Ground:0.00} m up");
    }

    [Fact]
    public void Staged_afloat_both_parts_float()
    {
        using var sim = Drop(3f, Vehicle.TwoStage);
        var rocket = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => rocket.State != FlightState.Flying, maxSeconds: 90));
        Assert.Equal(FlightState.Floating, rocket.State);
        sim.Step(new FlightInput(CutThrottle: true, Stage: true));
        var parts = sim.Flight.WorldVessels.Where(v => v.Vehicle == Vehicle.TwoStage && v.State != FlightState.Ready).ToArray(); // not a rocket on the pad
        Assert.Equal(2, parts.Length);
        sim.Run(Nobody, _ => false, maxSeconds: 30);
        output.WriteLine(string.Join("; ", parts.Select(p => $"stage {p.Stage}: {p.State} {p.CrashReason}")));
        Assert.All(parts, p => Assert.NotEqual(FlightState.Crashed, p.State));
    }

    [Fact]
    public void A_gentle_splashdown_floats()
    {
        // 10 m/s: on the ground it would crash.
        Assert.True(10f > Scene.SafeTouchdownSpeed);
        using var sim = Drop(10f);
        var vessel = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 90));
        output.WriteLine($"{vessel.State} after {sim.Time:0.0} s, splashing down at {vessel.TouchdownSpeed:0.0} m/s; {vessel.CrashReason}");
        Assert.Equal(FlightState.Floating, vessel.State);
        Assert.Equal(FlightState.Floating, sim.Flight.State);
        // Launch and Land wanted it on the landing zone: in the sea, it is off target, as beside the zone.
        List<(string Text, Brutal.Numerics.float4 Color)> lines = [];
        var (title, color) = Hud.Ending(sim.Flight, null, lines);
        Assert.Equal("SPLASHDOWN OFF TARGET", title);
        Assert.Equal(Hud.Warning, color);
        Assert.Contains(lines, l => l.Text.Contains($"within {Scene.LandingTargetRadius:0} m of the landing zone") && l.Color == Hud.Warning);

        // Floating, the waves alone move and turn it less than it has to settle to.
        float through = 0f, turning = 0f;
        sim.Run(Nobody, _ =>
        {
            through = MathF.Max(through, (vessel.Velocity - sim.Physics.FlowAt(vessel.CentreOfMass)).Length());
            turning = MathF.Max(turning, sim.Physics.AngularVelocity(vessel.Craft).Length());
            return false;
        }, maxSeconds: 60);
        output.WriteLine($"over 60 s afloat, it moved through the water at up to {through:0.00} m/s and turned at up to {turning:0.000} rad/s");
        Assert.Equal(FlightState.Floating, vessel.State);
    }

    [Fact]
    public void A_splashdown_where_no_landing_site_was_wanted_is_just_a_splashdown()
    {
        // Free play wants nothing landed anywhere.
        using var sim = Drop(4f, Mission.FreePlay.Vehicle, first: 1, last: 1, mission: Mission.FreePlay);
        var vessel = Dropped(sim);
        Assert.Null(vessel.LandingSite);
        Assert.True(sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 90));
        Assert.Equal(FlightState.Floating, vessel.State);
        List<(string Text, Brutal.Numerics.float4 Color)> lines = [];
        var (title, color) = Hud.Ending(sim.Flight, null, lines);
        Assert.Equal("SPLASHDOWN", title);
        Assert.Equal(Hud.Good, color);
        Assert.DoesNotContain(lines, l => l.Text.Contains("landing zone"));
    }

    [Fact]
    public void A_fairing_half_lost_in_the_sea_is_listed_as_sunk()
    {
        // Falling at 5 m/s, a little above the calm sea (it has no legs to put out).
        var at = DeepWater;
        var up = Planet.Up(at);
        var vehicle = Vehicle.FairingHalf;
        var state = new VesselState
        {
            Id = 0, Flight = 0, Mission = Mission.DeploySatellite, Vehicle = vehicle, First = 0, Last = 0,
            Fuel = [0f], State = FlightState.Flying, Position = at + up * 2f, Rotation = Planet.UprightAt(at),
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 0, 0),
            Velocity = -up * 5f, AngularVelocity = Vector3.Zero, Aim = up, MaxAltitude = 400f, HasBeenAirborne = true,
        };
        using var sim = new Simulation(Mission.DeploySatellite, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var half = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => half.State != FlightState.Flying, maxSeconds: 10));
        Assert.Equal("Fell into the sea.", half.CrashReason);
        Assert.Equal("sunk", Hud.Status(half, brief: true).Text);
    }

    /// <summary>
    /// Points of <paramref name="vessel"/> watched for touching the water: its feet hanging at full
    /// stroke (or its bell's rim), and the lowest point round its body at intervals up its length.
    /// </summary>
    private static IEnumerable<Vector3> Parts(Vessel vessel)
    {
        var model = vessel.Model;
        if (vessel.HasLegs && vessel.LegDeploy >= 1f)
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
                yield return Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg) - Vector3.UnitY * LegGeometry.Rocket.Stroke, model);
        var axis = vessel.Up;
        var bell = vessel.CurrentStage.BellHeight;
        const int rings = 16;
        for (var i = 0; i <= rings; i++)
        {
            var point = Vector3.Transform(new Vector3(0, -bell + (vessel.Height + bell) * i / rings, 0), model);
            var down = -Planet.Up(point);
            var across = down - axis * Vector3.Dot(down, axis);
            yield return across.LengthSquared() > 1e-6f ? point + Vector3.Normalize(across) * Scene.RocketRadius : point;
        }
    }

    /// <summary>
    /// Flies <paramref name="sim"/> for up to <paramref name="seconds"/> (until <paramref name="vessel"/>
    /// is destroyed, or <paramref name="until"/> says it has seen enough), watching each of its
    /// <see cref="Parts"/>, measured apart from the game's own
    /// rules: the fastest any part touched the water at (going under it from above, at the speed it had
    /// the frame before), as judged (the first part in by its whole speed, any later one only by how
    /// fast it comes down side-on) and by its whole speed; and the deepest any part went under.
    /// </summary>
    private static (float Judged, float Fastest, float Deepest) Watch(Simulation sim, Vessel vessel, float seconds, Action<float>? each = null, Func<bool>? until = null)
    {
        var physics = sim.Physics;
        bool[]? wasUnder = null;
        Vector3 velocity = vessel.Velocity, spin = physics.AngularVelocity(vessel.Craft), axis = vessel.Up;
        float judged = 0f, fastest = 0f, deepest = float.MinValue;
        sim.Run((f, dt) =>
        {
            var parts = Parts(vessel).ToArray();
            var depths = parts.Select(p => physics.DepthUnder(p) ?? float.MinValue).ToArray();
            var under = depths.Select(d => d > 0f).ToArray();
            if (wasUnder is not null)
            {
                var anyIn = wasUnder.Any(b => b);
                for (var i = 0; i < parts.Length; i++)
                    if (under[i] && !wasUnder[i])
                    {
                        // Once some of it is in, only what comes down side-on counts: the rest slides in after it.
                        var moving = velocity + Vector3.Cross(spin, parts[i] - vessel.CentreOfMass);
                        fastest = MathF.Max(fastest, moving.Length());
                        if (anyIn)
                            moving -= axis * Vector3.Dot(moving, axis);
                        judged = MathF.Max(judged, moving.Length());
                    }
            }
            deepest = MathF.Max(deepest, depths.Max());
            each?.Invoke(depths.Max());
            wasUnder = under;
            if (!vessel.Craft.Removed)
                (velocity, spin, axis) = (vessel.Velocity, physics.AngularVelocity(vessel.Craft), vessel.Up);
            return default;
        }, _ => vessel.State == FlightState.Crashed || until?.Invoke() == true, maxSeconds: seconds);
        return (judged, fastest, deepest);
    }

    [Fact]
    public void Tipping_over_in_the_water_is_judged_by_how_fast_it_hits_it()
    {
        // The satellite launcher's upper stage, on its own (the short one), and Launch and Land's booster (the tall one).
        foreach (var (name, vehicle, stage, mustFloat) in new[] { ("the upper stage", Vehicle.ThreeStage, 1, true), ("the booster", Vehicle.Booster, 0, false) })
        {
            using var sim = Drop(6f, vehicle, first: stage, last: stage);
            var vessel = Dropped(sim);
            Assert.True(vessel.HasLegs && vessel.LegDeploy >= 1f);
            // It isn't floating until it has settled: not while it is still tipping over.
            // Watched for half a minute once it floats: long enough to see it go on tipping had it
            // been called floating too soon (it may still turn at up to Scene.FloatSettledTurnRate
            // as it settles), and no longer, as every second afloat is slow to step.
            float? floatingAt = null, leanThen = null;
            var (judged, fastest, _) = Watch(sim, vessel, 90f, _ =>
            {
                if (floatingAt is null && vessel.State == FlightState.Floating)
                    (floatingAt, leanThen) = ((float)sim.Time, vessel.TiltDegrees);
            }, until: () => sim.Time >= floatingAt + 30f);
            output.WriteLine($"{name}: the fastest any part touched the water at was {judged:0.0} m/s as judged (side-on, after the first), {fastest:0.0} m/s in all; " +
                             $"floating after {floatingAt:0.0} s, leaning {leanThen:0}° then; it ends {vessel.State}, leaning {vessel.TiltDegrees:0}°; {vessel.CrashReason}");
            if (judged <= Scene.SafeSplashdownSpeed)
            {
                Assert.Equal(FlightState.Floating, vessel.State);
                Assert.True(vessel.TiltDegrees > 60f, $"{name} leans only {vessel.TiltDegrees:0}°");
                Assert.True(leanThen > 60f, $"{name} was floating while it still stood at {leanThen:0}°");
                Assert.True(MathF.Abs(vessel.TiltDegrees - leanThen!.Value) < 15f, $"{name} was floating at {leanThen:0}°, before it had finished tipping (to {vessel.TiltDegrees:0}°)");
            }
            else
            {
                Assert.Equal(FlightState.Crashed, vessel.State);
                Assert.StartsWith("Hit the water at", vessel.CrashReason);
            }
            if (mustFloat)
                Assert.Equal(FlightState.Floating, vessel.State);
        }
    }

    [Fact]
    public void The_faster_it_splashes_down_the_deeper_it_plunges()
    {
        // From rest (set on the water) it sinks in to about where it floats upright; faster, the
        // speed takes it clearly deeper. Not in proportion, nor by half as much again: from rest it
        // still falls through its own depth in the water, reaching about 9 m/s there, against about
        // 17 m/s for one that touches it at 14 m/s, and even undamped that would only take it 1.44
        // times as deep. So the mark is 2 m deeper, a fifth of how deep it floats upright.
        var (before, fromRest) = (-1f, 0f);
        foreach (var speed in new[] { 0f, 3f, 8f, 14f })
        {
            using var sim = Drop(speed, clearance: speed == 0f ? 0f : Drops.Clearance); // from rest, its feet on the calm sea
            var vessel = Dropped(sim);
            // Once it has gone under and started back up, its lowest part stays under: it never leaps clear.
            float deepestSoFar = 0f, clearest = float.MaxValue;
            var rising = false;
            var (floatingAt, upright) = (float.NaN, float.NaN);
            var (_, _, deepest) = Watch(sim, vessel, 60f, under =>
            {
                if (float.IsNaN(upright) && sim.Time >= 7)
                    upright = under; // settled upright, before it starts to tip
                if (under < deepestSoFar - 0.5f)
                    rising = true;
                deepestSoFar = MathF.Max(deepestSoFar, under);
                if (rising)
                    clearest = MathF.Min(clearest, under);
            }, until: () =>
            {
                // Back up and settled: that it then stays afloat is A_gentle_splashdown_floats'.
                if (vessel.State == FlightState.Floating)
                    floatingAt = (float)sim.Time;
                return vessel.State == FlightState.Floating;
            });
            output.WriteLine($"at {speed} m/s (touching the water at {vessel.TouchdownSpeed:0.0}) it plunged {deepest:0.00} m under, floating upright at {upright:0.00} m by 7 s; on the way back up its lowest part stayed {clearest:0.00} m under at the least; " +
                             $"{vessel.State} after {floatingAt:0.0} s, {vessel.Craft.Submerged:0.00} submerged");
            Assert.True(deepest > before, $"at {speed} m/s it plunged only {deepest:0.00} m, no deeper than {before:0.00} m");
            Assert.True(rising, "it never came back up");
            Assert.True(clearest > 0f, $"it leapt clear of the water, its lowest part {-clearest:0.00} m above it");
            // It came to float within the minute (the watch ends as it does). That it then stays
            // afloat, a minute on and more, is no longer watched here: A_gentle_splashdown_floats
            // (and its roughest sea's twin) covers it.
            Assert.Equal(FlightState.Floating, vessel.State);
            before = deepest;
            if (speed == 0f)
                fromRest = deepest;
        }
        Assert.True(before >= fromRest + 2f, $"at 14 m/s it plunged {before:0.00} m, not clearly deeper than the {fromRest:0.00} m it sinks in from rest");
    }

    [Fact]
    public void A_hard_splashdown_destroys_it_and_it_sinks()
    {
        using var sim = Drop(20f);
        var vessel = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 10));
        output.WriteLine($"{vessel.State}: {vessel.CrashReason}");
        Assert.Equal(FlightState.Crashed, vessel.State);
        Assert.StartsWith("Hit the water at", vessel.CrashReason);
        Assert.Contains($"slower than {Scene.SafeSplashdownSpeed:0} m/s", vessel.CrashReason);
        sim.Run(Nobody, _ => false, maxSeconds: Scene.WreckSeconds + 0.05); // left for its hit to play out, then gone
        Assert.True(vessel.Craft.Removed, "its body is still in the world");
    }

    [Fact]
    public void A_splashdown_counts_as_landing_safely_but_not_on_the_landing_zone()
    {
        // A whole rocket of this flight's over the water: it drops its booster there, and the
        // pilot flies its upper stage on and away, kilometres off, so the booster comes down on
        // its own and settles there with nobody near (and still counts, without going back).
        foreach (var (mission, safely, onZone) in new[]
                 {
                     (Mission.ReachOrbit, "Bonus: land the booster safely", "Bonus: land it on the drone ship"),
                     (Mission.DeploySatellite, "Land the booster", "Bonus: the booster on the drone ship"),
                 })
        {
            using var sim = Drop(4f, mission.Vehicle, mission: mission, flight: 1, fairingOn: mission.Vehicle.Stages[^1].Fairing); // as it flies, so the rest can fly on through the air
            sim.Step(new FlightInput(Stage: true));
            sim.Step(new FlightInput(FullThrottle: true));
            var booster = sim.Flight.Vessels.Single(v => v.Stage == 0 && v.TopStage == 0);
            var furthest = 0f;
            var ended = sim.Run(Nobody, f =>
            {
                furthest = MathF.Max(furthest, Vector3.Distance(f.Controlled.Position, booster.Position));
                return booster.State != FlightState.Flying;
            }, maxSeconds: 90);
            output.WriteLine($"{mission.Name}: the booster is {booster.State} after {sim.Time:0.0} s, splashing down at {booster.TouchdownSpeed:0.0} m/s, leaning {booster.TiltDegrees:0}°, {booster.Craft.Submerged:0.00} submerged; " +
                             $"the vessel being flown got {furthest:0} m from it");
            Assert.Same(booster, sim.Flight.Vessels.Single(v => v.Stage == 0 && v.TopStage == 0));
            Assert.NotSame(booster, sim.Flight.Controlled);
            Assert.True(furthest > Scene.FloatFreezeDistance, $"the vessel being flown only got {furthest:0} m from the booster");
            Assert.True(ended, "the booster is still flying");
            Assert.Equal(FlightState.Floating, booster.State);
            Assert.True(mission.Steps.Single(s => s.Label == safely).Done(sim.Flight), $"not done: {safely}");
            Assert.False(mission.Steps.Single(s => s.Label == onZone).Done(sim.Flight), $"done: {onZone}");
        }

        using (var sim = Drop(4f))
        {
            var booster = Dropped(sim);
            Assert.True(sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 90));
            Assert.Equal(FlightState.Floating, booster.State);
            Assert.False(Mission.LaunchAndLand.Steps.Single(s => s.Label == "Land on the landing zone").Done(sim.Flight));
        }
    }

    /// <summary>
    /// The nearest spot offshore, east of the pad on the equator, where the sea is at least 20 m
    /// deep everywhere within 30 m of it: deep enough for a booster to float in however it lies,
    /// and near enough to the pad to fly to from it (and for a floater there to be kept awake by
    /// the flights from the pad, as the soak wants). On the calm sea.
    /// </summary>
    internal static readonly Lazy<Vector3> Offshore = new(() =>
    {
        static float Deep(Vector3 p) => -Terrain.Height(Planet.Up(p));
        for (var metres = 300f; metres < 2000f; metres += 10f)
        {
            var p = Planet.Centre + CubeSphere.FromLatLon(0f, metres / Scene.PlanetRadius) * Planet.Radius;
            var deep = Deep(p) >= 20f;
            for (var k = 0; k < 16 && deep; k++)
            {
                var (sin, cos) = MathF.SinCos(MathF.Tau * k / 16);
                for (var r = 10f; r <= 30f && deep; r += 10f)
                    deep = Deep(p + (Planet.East(p) * cos + Planet.North(p) * sin) * r) >= 20f;
            }
            if (deep)
                return Planet.Centre + Planet.Up(p) * Planet.SeaRadius;
        }
        throw new InvalidOperationException("no deep water within 2 km east of the pad");
    });

    [PlaythroughFact(Long = false)]
    [Trait("Kind", "Playthrough")]
    public void A_booster_flown_from_the_pad_out_to_sea_splashes_down_and_floats()
    {
        // As the soak flies it (Playlist): the whole flight, from the pad to a splashdown offshore.
        var at = Offshore.Value;
        output.WriteLine($"offshore {Planet.Radius * Planet.AngleBetween(Vector3.Zero, at):0} m east of the pad, the sea {-Terrain.Height(Planet.Up(at)):0} m deep");
        using var sim = new Simulation(Mission.LaunchAndLand);
        Assert.True(sim.RunToEnd(new Autopilot { Target = at }.Fly), $"it is still {sim.Flight.State}");
        output.WriteLine($"{sim.Flight.State} after {sim.Time:0} s, {sim.Flight.DistanceToLandingSite:0} m from the zone, splashing down at {sim.Flight.TouchdownSpeed:0.0} m/s; {sim.Flight.CrashReason}");
        Assert.Equal(FlightState.Floating, sim.Flight.State);
        Assert.True(Vector3.Distance(sim.Flight.Position, Vector3.Zero) < Scene.FloatFreezeDistance, "it came down too far from the pad to be kept awake from it");
        Assert.Equal("SPLASHDOWN OFF TARGET", Hud.Ending(sim.Flight, null, []).Title);
    }

    /// <summary>
    /// A spot near the coast, east of the pad on the equator, where the sea is about 3 m deep, and
    /// within 2.5 to 3.5 m deep everywhere within 10 m of it (where a booster's feet would stand).
    /// </summary>
    internal static readonly Lazy<Vector3> ShallowWater = new(() =>
    {
        static float Deep(Vector3 d) => -Terrain.Height(d);
        for (var metres = 200f; metres < 8000f; metres += 2f)
        {
            var d = CubeSphere.FromLatLon(0f, metres / Scene.PlanetRadius);
            if (Deep(d) is < 2.75f or > 3.25f)
                continue;
            var p = Planet.Centre + d * Planet.Radius;
            var even = true;
            for (var k = 0; k < 16 && even; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 16);
                for (var r = 4f; r <= 10f && even; r += 3f)
                    even = Deep(Planet.Up(p + (Planet.East(p) * c + Planet.North(p) * s) * r)) is >= 2.5f and <= 3.5f;
            }
            if (even)
                return Planet.Centre + d * Planet.SeaRadius;
        }
        throw new InvalidOperationException("no even, shallow water east of the pad");
    });

    [Fact]
    public void Standing_in_shallow_water_it_is_landed_not_floating()
    {
        var at = ShallowWater.Value;
        output.WriteLine($"{Planet.Radius * Planet.AngleBetween(Vector3.Zero, at):0} m east of the pad, the sea {-Terrain.Height(Planet.Up(at)):0.00} m deep");
        using var sim = Drops.At(at, Planet.East(at), 2f, seabed: true); // onto the seabed at 2 m/s, the water round its feet
        var vessel = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 30));
        output.WriteLine($"{vessel.State} after {sim.Time:0.0} s, {vessel.Craft.Submerged:0.00} submerged, leaning {vessel.TiltDegrees:0.0}°; {vessel.CrashReason}");
        Assert.Equal(FlightState.Landed, vessel.State);
        Assert.True(vessel.Craft.Submerged > 0f, "it isn't in the water");
        var feet = Enumerable.Range(0, RocketGeometry.LegCount)
            .Max(leg => Planet.GroundAltitude(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg), vessel.Model)));
        Assert.True(feet < LegGeometry.Rocket.Stroke, $"a foot is {feet:0.00} m above the seabed: it isn't standing on it");

        var changed = false;
        sim.Run(Nobody, _ => (changed |= vessel.State != FlightState.Landed), maxSeconds: 20);
        Assert.False(changed, $"it became {vessel.State}");
    }

    [Fact]
    public void A_shallow_seabed_is_ground()
    {
        // In about 3 m of water: touching the water is gentle enough, but the seabed is judged as
        // the ground is: coming down too fast wrecks it, and set down gently, leaning, it has landed.
        var at = ShallowWater.Value;
        // Falling in from above the water at 10 m/s; and set down on the seabed at 2 m/s, leaning.
        foreach (var (speed, lean, onTheSeabed, reason) in new[] { (10f, 0f, false, "Hit the ground at"), (2f, 25f, true, null) })
        {
            using var sim = Drops.At(at, Planet.East(at), speed, leanDegrees: lean, seabed: onTheSeabed);
            var vessel = Dropped(sim);
            Assert.True(sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 30));
            output.WriteLine($"at {speed} m/s, leaning {lean}°: {vessel.State} after {sim.Time:0.0} s; {vessel.CrashReason}");
            if (reason is null)
                Assert.Equal(FlightState.Landed, vessel.State);
            else
            {
                Assert.Equal(FlightState.Crashed, vessel.State);
                Assert.StartsWith(reason, vessel.CrashReason);
            }
        }
    }

    /// <summary>
    /// The roughest sea on the map (the Austral Sea), at a spot deep enough (more than 100 m, all
    /// round it) and far enough from the floes round its ice for its waves to be at their full size,
    /// found on a grid of latitudes and longitudes.
    /// </summary>
    internal static readonly Lazy<(Vector3 At, SeaInfo Sea)> RoughWater = new(() =>
    {
        var seas = Terrain.Map.Seas;
        var roughest = seas.Select((s, i) => (s, i)).MaxBy(x => x.s.Waves);
        var region = (byte)(roughest.i + 1);
        for (var lat = -89f; lat <= 89f; lat += 1f)
            for (var lon = -180f; lon < 180f; lon += 1f)
            {
                var d = CubeSphere.FromLatLon(float.DegreesToRadians(lat), float.DegreesToRadians(lon));
                var (face, u, v) = CubeSphere.ToFace(d);
                if (Terrain.Map.RegionAt(face, u, v) != region || Sea.WaveScale(d) < 0.99f * roughest.s.Waves)
                    continue;
                var p = Planet.Centre + d * Planet.Radius;
                var deep = true;
                for (var k = 0; k < 16 && deep; k++)
                {
                    var (s, c) = MathF.SinCos(MathF.Tau * k / 16);
                    var round = Planet.Up(p + (Planet.East(p) * c + Planet.North(p) * s) * 80f);
                    // Deep, and clear of the floes round the sea ice, which damp the waves.
                    deep = Terrain.Height(round) < -100f && Sea.WaveScale(round) >= 0.99f * roughest.s.Waves;
                }
                if (deep)
                    return (Planet.Centre + d * Planet.SeaRadius, roughest.s);
            }
        throw new InvalidOperationException($"no deep water in the {roughest.s.Name}");
    });

    [Fact]
    public void A_gentle_splashdown_in_the_roughest_sea_scores_its_splashdown_not_the_waves()
    {
        // Set down at 0.5 m/s in the roughest sea, it settles afloat with its waves heaving it,
        // rocking parts of it back under the water and out again: none of that is a touch, as the
        // water moves with them. Its touchdown stays its splashdown's.
        var (at, sea) = RoughWater.Value;
        using var sim = Drops.At(at, Planet.East(at), 0.5f);
        var vessel = Dropped(sim);
        float? first = null;
        Assert.True(sim.Run(Nobody, _ =>
        {
            if (vessel.TouchdownSpeed > 0f)
                first ??= vessel.TouchdownSpeed;
            return vessel.State != FlightState.Flying;
        }, maxSeconds: 120), "it never settled");
        output.WriteLine($"in the {sea.Name} (waves {sea.Waves} m): {vessel.State} after {sim.Time:0.0} s; splashdown {first:0.00} m/s, touchdown {vessel.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Floating, vessel.State);
        Assert.NotNull(first);
        Assert.True(vessel.TouchdownSpeed < first.Value + 0.5f, $"its touchdown rose from {first:0.00} to {vessel.TouchdownSpeed:0.00} m/s, afloat in the waves");
    }

    [Fact]
    public void Tipping_over_in_the_water_its_side_slapping_it_is_one_of_its_touches()
    {
        // Set down upright on its legs from just over the Sunrise Sea's deep water, its feet touch it
        // barely moving; then it tips over onto its side, which slaps the water much harder: that is
        // its touchdown, judged against the water's own movement, side-on.
        using var sim = Drop(0f, clearance: 0.05f);
        var vessel = Dropped(sim);
        float? first = null;
        Assert.True(sim.Run(Nobody, _ =>
        {
            if (vessel.TouchdownSpeed > 0f)
                first ??= vessel.TouchdownSpeed;
            return vessel.State != FlightState.Flying;
        }, maxSeconds: 120), "it never settled");
        output.WriteLine($"{vessel.State} after {sim.Time:0.0} s, leaning {vessel.TiltDegrees:0}°; splashdown {first:0.00} m/s, touchdown {vessel.TouchdownSpeed:0.00} m/s; waves {Sea.WaveScale(Planet.Up(DeepWater)):0.0}");
        Assert.Equal(FlightState.Floating, vessel.State);
        Assert.True(vessel.TiltDegrees > 45f, "it never tipped over");
        // Splashdown about 0.06 m/s, the slap of its side about 1.25: 0.5 over is about halfway.
        Assert.True(vessel.TouchdownSpeed > first + 0.5f, $"its tip onto its side ({vessel.TouchdownSpeed:0.00} m/s) didn't count over its splashdown ({first:0.00})");
    }

    [Fact]
    public void A_gentle_splashdown_floats_in_the_roughest_sea_too()
    {
        // Its waves rock and carry a floating booster harder, but it still settles.
        var (at, sea) = RoughWater.Value;
        using var sim = Drops.At(at, Planet.East(at), 6f);
        var vessel = Dropped(sim);
        var ended = sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 120);
        output.WriteLine($"in the {sea.Name} (waves {sea.Waves} m): {vessel.State} after {sim.Time:0.0} s, leaning {vessel.TiltDegrees:0}°; {vessel.CrashReason}");
        Assert.True(ended, "it never settled");
        Assert.Equal(FlightState.Floating, vessel.State);

        float through = 0f, turning = 0f;
        sim.Run(Nobody, _ =>
        {
            through = MathF.Max(through, (vessel.Velocity - sim.Physics.FlowAt(vessel.CentreOfMass)).Length());
            turning = MathF.Max(turning, sim.Physics.AngularVelocity(vessel.Craft).Length());
            return false;
        }, maxSeconds: 60);
        output.WriteLine($"over 60 s afloat, it moved through the water at up to {through:0.00} m/s and turned at up to {turning:0.000} rad/s " +
                         $"(it settles under {Scene.FloatSettledSpeed * sea.Waves:0.00} m/s and {Scene.FloatSettledTurnRate * sea.Waves:0.000} rad/s there)");
        Assert.Equal(FlightState.Floating, vessel.State);
    }

    /// <summary>
    /// A booster with its legs stowed, still, laid on its side over <paramref name="at"/> (its
    /// middle there, its nose east), the lowest of its body <paramref name="clearance"/> above the
    /// seabed (or, with <paramref name="onTheSea"/>, above the calm sea), flown.
    /// </summary>
    private static Simulation LaidOnItsSide(Vector3 at, float clearance, bool onTheSea = false)
    {
        var vehicle = Vehicle.Booster;
        var up = Planet.Up(at);
        var east = Planet.East(at);
        var rotation = Quaternion.Concatenate(Planet.UprightAt(at), Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(up, east)), MathF.PI / 2));
        var (bell, height) = (vehicle.Stages[0].BellHeight, RocketGeometry.StackHeight(vehicle, 0, 0));
        var position = at - east * (height / 2) + up * 10f;
        for (var i = 0; i < 4; i++)
        {
            var model = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            var lowest = Enumerable.Range(0, 17).Min(k =>
            {
                var y = -bell + (height + bell) * k / 16;
                var p = Vector3.Transform(new Vector3(0, y, 0), model) - Planet.Up(position) * (y < 0 ? vehicle.Stages[0].BellRadius : vehicle.Stages[0].Radius);
                return onTheSea ? Planet.Altitude(p) : Planet.GroundAltitude(p);
            });
            position -= Planet.Up(position) * (lowest - clearance);
        }
        var state = new VesselState
        {
            Id = 0, Flight = 0, Mission = Mission.LaunchAndLand, Vehicle = vehicle, First = 0, Last = 0,
            Fuel = [vehicle.Stages[0].FuelMass * 0.3f],
            State = FlightState.Flying, Position = position, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 0, 0),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero,
            Aim = east, MaxAltitude = 400f, HasBeenAirborne = true,
        };
        var sim = new Simulation(Mission.LaunchAndLand, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        sim.Step(new FlightInput(SwitchTo: sim.Flight.WorldVessels.First(v => v.Id == 0)));
        return sim;
    }

    [Fact]
    public void Laid_on_its_side_in_shallow_water_it_comes_to_no_harm()
    {
        // In about 3 m of water, about as deep as it reaches lying down (it lies tail low, as it
        // floats): it rests on the seabed or floats just clear of it, and either way it is down
        // safely, as it stays for half a minute more (not wrecked by the seabed coming and going).
        var at = ShallowWater.Value;
        using var sim = LaidOnItsSide(at, 0.5f);
        var vessel = Dropped(sim);
        sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 60);
        output.WriteLine($"{vessel.State} after {sim.Time:0.0} s, leaning {vessel.TiltDegrees:0}°; {vessel.CrashReason}");
        // Once down, it stays down: the seabed coming and going under it in the waves' troughs
        // mustn't throw it back to flying, over and over (it may go from floating to landed).
        var changes = new List<string>();
        var last = vessel.State;
        sim.Run(Nobody, _ =>
        {
            if (vessel.State != last)
                changes.Add($"{sim.Time:0.0} s: {last = vessel.State}");
            return false;
        }, maxSeconds: 30);
        output.WriteLine($"then {string.Join(", ", changes)}; {vessel.State} {vessel.CrashReason}");
        Assert.True(vessel.State is FlightState.Landed or FlightState.Floating, $"{vessel.State} {vessel.CrashReason}");
        Assert.DoesNotContain(changes, c => c.EndsWith("Flying"));
    }

    /// <summary>How deep a booster lying afloat (with its legs stowed, tail low) reaches under the calm sea: its lowest point, settled in deep water.</summary>
    private static readonly Lazy<float> LyingDraft = new(() =>
    {
        using var sim = LaidOnItsSide(DeepWater, 0f, onTheSea: true);
        var vessel = Dropped(sim);
        var draft = 0f;
        sim.Run(Nobody, _ =>
        {
            if (sim.Time > 20)
                draft = MathF.Max(draft, Parts(vessel).Max(p => -Planet.Altitude(p)));
            return false;
        }, maxSeconds: 40);
        return draft;
    });

    [Fact]
    public void Lying_afloat_in_water_a_little_deeper_than_it_draws_it_floats()
    {
        // A spot near the coast where the water is between 0.75 and 2 m deeper than the booster
        // reaches down lying afloat, all along where it lies (east and west of the spot) and a
        // little to either side.
        var draft = LyingDraft.Value;
        static float Deep(Vector3 p) => -Terrain.Height(Planet.Up(p));
        Vector3? found = null;
        for (var metres = 200f; metres < 8000f && found is null; metres += 2f)
        {
            var p = Planet.Centre + CubeSphere.FromLatLon(0f, metres / Scene.PlanetRadius) * Planet.Radius;
            var (east, north) = (Planet.East(p), Planet.North(p));
            var ok = true;
            for (var x = -15f; x <= 15f && ok; x += 1.5f)
                for (var z = -4f; z <= 4f && ok; z += 2f)
                    ok = Deep(p + east * x + north * z) is var d && d >= draft + 0.75f && d <= draft + 2f;
            if (ok)
                found = Planet.Centre + Planet.Up(p) * Planet.SeaRadius;
        }
        Assert.NotNull(found);
        var at = found.Value;
        using var sim = LaidOnItsSide(at, 0f, onTheSea: true);
        var vessel = Dropped(sim);
        var nearest = float.MaxValue;
        sim.Run(Nobody, _ =>
        {
            if (!vessel.Craft.Removed)
                nearest = MathF.Min(nearest, Parts(vessel).Min(p => Planet.GroundAltitude(p)));
            return false;
        }, maxSeconds: 60);
        output.WriteLine($"lying afloat it reaches {draft:0.00} m under the calm sea; in {Deep(at):0.00} m of water {Planet.Radius * Planet.AngleBetween(Vector3.Zero, at):0} m east of the pad, " +
                         $"it came within {nearest:0.00} m of the seabed and is {vessel.State}, leaning {vessel.TiltDegrees:0}°; {vessel.CrashReason}");
        Assert.Equal(FlightState.Floating, vessel.State);
    }
}
