using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The planet's turn (<see cref="Planet.SpinRate"/>): the length of its day, how fast the ground
/// moves among the stars, and the conversions between the ground's frame and the stars'.
/// </summary>
public sealed class SpinTests(ITestOutputHelper output)
{
    [Fact]
    public void The_turn_and_the_year_make_a_twenty_minute_day()
    {
        // To 1e-8 a day: a 1,201 s day would be 7e-7 out, so this tells them apart.
        var perDay = 1.0 / Planet.TurnSeconds - 1.0 / (Scene.DaySeconds * Scene.YearDays);
        Assert.Equal(1.0 / Scene.DaySeconds, perDay, 1e-8);
        Assert.InRange(Planet.TurnSeconds, 1107.6f, 1107.8f);
    }

    [Fact]
    public void The_ground_at_Home_moves_east_at_about_57_m_per_s()
    {
        var v = Planet.GroundSpin(Vector3.Zero);
        Assert.True(Vector3.Dot(Vector3.Normalize(v), Planet.East(Vector3.Zero)) > 0.9999f);
        Assert.InRange(v.Length(), 56f, 57f);
    }

    [Fact]
    public void The_poles_stand_still()
    {
        Assert.True(Planet.GroundSpin(Planet.Centre + Vector3.UnitZ * -Planet.SeaRadius).Length() < 1e-3f);

        // And off the axis it moves at the turn's rate times its distance from it: 1 m off, at ω.
        var off = Planet.Centre + Planet.NorthPole * Planet.SeaRadius + Vector3.UnitX;
        Assert.Equal(Planet.SpinRate, Planet.GroundSpin(off).Length(), 1e-7f);
    }

    [Fact]
    public void Turning_back_undoes_a_turn_and_a_long_turn_is_its_remainder()
    {
        var d = Planet.Up(new Vector3(300f, 0f, 200f));
        // Back by as much as it went forward, it is where it began; back alone, it moves west.
        Assert.True(Vector3.Distance(Planet.TurnedDirection(Planet.TurnedDirection(d, 100), -100), d) < 1e-6f);
        var back = Planet.TurnedDirection(d, -10) - d;
        Assert.True(Vector3.Dot(Vector3.Normalize(back), Planet.East(Planet.Centre + d)) < -0.99f);

        // A long time (1e7 s, some 9,000 turns) comes to the same as what is left of it after
        // whole turns: the angle mustn't lose its precision to the float rate.
        const double Long = 1e7;
        var remainder = Long - Math.Floor(Long / Planet.TurnSeconds) * Planet.TurnSeconds;
        var gap = Vector3.Distance(Planet.TurnedDirection(d, Long), Planet.TurnedDirection(d, remainder));
        output.WriteLine($"after {Long:0} s: {gap:0.000000} from the remainder's ({remainder:0.000} s)");
        Assert.True(gap < 1e-4f, $"{gap:0.000000} from the remainder's turn");
    }

    [Fact]
    public void Converting_there_and_back_gives_the_same_velocity()
    {
        var p = Planet.Centre + Planet.Up(new Vector3(300f, 0f, 200f)) * (Planet.SeaRadius + 4000f);
        var v = new Vector3(100f, 20f, -30f);
        var back = Planet.OverGround(p, Planet.AmongStars(p, v));
        Assert.True(Vector3.Distance(v, back) < 1e-3f);
    }

    [Fact]
    public void A_whole_turn_brings_the_ground_back()
    {
        Assert.True(Vector3.Distance(Planet.Turned(Vector3.Zero, Planet.TurnSeconds), Vector3.Zero) < 0.05f);

        var moved = Planet.Turned(Vector3.Zero, 10) - Vector3.Zero;
        var expected = Planet.GroundSpin(Vector3.Zero).Length() * 10f;
        Assert.True(Vector3.Dot(Vector3.Normalize(moved), Planet.East(Vector3.Zero)) > 0.99f);
        Assert.InRange(moved.Length(), expected * 0.99f, expected * 1.01f);
    }

    private static readonly Pilot Nobody = (_, _) => default;

    [Fact]
    public void Something_dropped_from_high_up_lands_east_of_under_it()
    {
        // The ground under it moves east more slowly than it does, 2.5 km up, and the turning
        // frame's pull (Coriolis) carries it east as it falls. On this small planet gravity 2.5 km
        // up is only two thirds of the ground's, so the fall takes about 28 s, not the 22.6 s of an
        // even 1 g, and the drift is about 253 m in a vacuum (not the even-gravity 214 m). The air
        // hardly changes it: it lengthens the fall (more drift) as much as it slows the eastward
        // speed (less), for about 252 m. So it is bounded round that: from 200 m, under which the
        // fall would be missing a good part of its drift (the even-gravity figure, say), to 300 m.
        var (at, downhill, _) = Drops.FindSlope(0f, 3f);
        using var sim = Drops.At(at, downhill, 0f, clearance: 2500f, legsOut: false);
        var flight = sim.Flight;
        var (start, from) = (flight.Position, flight.Altitude);
        sim.Run(Nobody, f => f.State != FlightState.Flying, maxSeconds: 60);
        var end = flight.State == FlightState.Crashed ? flight.CrashPosition : flight.Position;

        var moved = end - start;
        var (east, north) = (Vector3.Dot(moved, Planet.East(start)), Vector3.Dot(moved, Planet.North(start)));
        output.WriteLine($"{flight.State} after {sim.Time:0.0} s, from {from:0} m to {flight.Altitude:0} m: {east:0.0} m east, {north:0.0} m north");
        Assert.NotEqual(FlightState.Flying, flight.State);
        Assert.InRange(east, 200f, 300f);
        Assert.InRange(north, -5f, 5f);
    }

    [Fact]
    public void Left_alone_above_the_air_a_craft_keeps_its_attitude_among_the_stars()
    {
        // Above the air nothing holds a craft to the ground's turn, so among the stars it keeps
        // pointing the same way, and over the ground it turns back by the planet's turn.
        var (at, downhill, _) = Drops.FindSlope(0f, 3f);
        using var sim = Drops.At(at, downhill, 0f, clearance: 7000f, legsOut: false);
        var flight = sim.Flight;
        var (start, began) = (sim.Time, flight.Controlled.Up);
        sim.Run(Nobody, _ => false, maxSeconds: 20);
        var elapsed = sim.Time - start;
        var axis = flight.Controlled.Up;

        var amongStars = Degrees(Planet.TurnedDirection(axis, elapsed), began);
        var overGround = Degrees(axis, began);
        var expected = float.RadiansToDegrees(Planet.SpinRate * (float)elapsed);
        output.WriteLine($"after {elapsed:0.0} s, {flight.Altitude:0} m up: {amongStars:0.00}° among the stars, {overGround:0.00}° over the ground (the turn {expected:0.00}°)");
        Assert.True(flight.Altitude > Scene.AtmosphereHeight, $"down to {flight.Altitude:0} m");
        Assert.InRange(amongStars, 0f, 1f);
        Assert.InRange(overGround, expected - 1.5f, expected + 1.5f);
    }

    [Fact]
    public void Above_the_air_a_craft_left_turning_settles_still_among_the_stars()
    {
        // Nothing steering it, tumbling, high above the air: its turning is pulled towards −Ω over
        // the ground (still among the stars) at the angular damping's rate, and settles there.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var at = Planet.Centre + Vector3.UnitY * (Planet.SeaRadius + 8000f);
        var up = Planet.Up(at);
        var craft = physics.AddLoose(Vehicle.Booster, at, Planet.UprightAt(at), up * 150f, new Vector3(0.3f, -0.1f, 0.2f));
        for (var t = 0f; t < 30f; t += 1f / 60)
            physics.Update(1f / 60, []);
        var off = (physics.AngularVelocity(craft) + Planet.Spin).Length();
        var altitude = Planet.Altitude(physics.CentreOfMass(craft));
        output.WriteLine($"after 30 s, {altitude:0} m up: turning {off:0.000000} rad/s off −Ω (|Ω| {Planet.SpinRate:0.000000})");
        Assert.True(altitude > Scene.AtmosphereHeight, $"down to {altitude:0} m");
        Assert.True(off < 1e-4f, $"{off:0.000000} rad/s off −Ω");
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("Polar")] // 70° S: the outward pull has its largest part along the ground there of any site, towards the equator
    public void A_booster_on_the_pad_stays_put(string site)
    {
        // On the pad it turns with the ground: the turning frame's pulls (the outward one, there)
        // mustn't slide it or lift it. Still, it would freeze within seconds and stop feeling them,
        // so it is woken each time it does: it stands on its legs for the whole minute. Each wake
        // stops it dead too, which would hide a slow creep from how far it has gone, so its speed
        // over each stretch awake (from its wake to its next freeze) is checked as well: a creep
        // would show there: measured 0.06 mm/s at Home and 0.4 mm/s at Polar. (Its speed at any one
        // moment is no measure of that: it jitters on its legs at up to 15 mm/s at Home and 5 mm/s
        // at Polar, going nowhere.)
        using var sim = new Simulation(site: LaunchSites.Named(site));
        var flight = sim.Flight;
        var start = flight.Position;
        var (wakes, from, since, creep, jitter) = (0, start, sim.Time, 0f, 0f);
        sim.Run(Nobody, f =>
        {
            if (f.Controlled.Craft.Frozen)
            {
                creep = MathF.Max(creep, Vector3.Distance(f.Position, from) / (float)(sim.Time - since));
                sim.Physics.Wake(f.Controlled.Craft);
                wakes++;
                (from, since) = (f.Position, sim.Time);
            }
            else
                jitter = MathF.Max(jitter, sim.Physics.Velocity(f.Controlled.Craft).Length());
            return false;
        }, maxSeconds: 60);
        var moved = Vector3.Distance(flight.Position, start);
        output.WriteLine($"{site}: {flight.State}, moved {moved:0.000} m, woken {wakes} times; awake, it went at most {creep * 1000f:0.000} mm/s " +
            $"between a wake and the next freeze (at any moment, up to {jitter * 1000f:0.000} mm/s)");
        Assert.Equal(FlightState.Ready, flight.State);
        Assert.True(wakes > 0, "it never froze, so no stretch awake was seen");
        Assert.True(moved < 0.05f, $"moved {moved:0.000} m");
        Assert.True(creep < 1e-3f, $"crept {creep * 1000f:0.000} mm/s between a wake and the next freeze");
    }

    [Fact]
    public void A_booster_lying_on_level_ground_far_off_the_equator_does_not_roll_towards_it()
    {
        // Lying east to west on Polar's landing zone (70° S, dead level), so the outward pull's
        // part along the ground (towards the equator, north) is across it, the way it would roll:
        // nothing in Jolt resists rolling. The ground has settled level to the planet's turn, so
        // it stays where it lies, and freezes there.
        var zone = LaunchSites.Named("Polar").LandingZone!.Value;
        using var physics = new RocketPhysics(GroundBody.Planet);
        var placed = Drops.Placed(zone, Planet.East(zone), 0f, leanDegrees: 90f, legsOut: false, clearance: 0.02f);
        var craft = physics.Restore(Vehicle.Booster, 0, 0, placed.Position, placed.Rotation, Vector3.Zero, Vector3.Zero, legDeploy: 0f, fairingOn: false);
        var start = physics.Position(craft);
        var t = 0f;
        for (; t < 60f && !craft.Frozen; t += 1f / 60)
            physics.Update(1f / 60, []);
        var moved = physics.Position(craft) - start;
        output.WriteLine($"{(craft.Frozen ? "froze" : "still awake")} after {t:0.00} s, {moved.Length():0.000} m from where it was laid " +
            $"({Vector3.Dot(moved, Planet.North(start)):0.000} m north)");
        Assert.True(moved.Length() < 0.05f, $"it moved {moved.Length():0.000} m");
        Assert.True(craft.Frozen, "it never froze");
    }

    [Fact]
    public void A_pile_three_layers_high_far_off_the_equator_freezes_together_where_it_lies()
    {
        // Five boosters lying on Polar's landing zone (70° S), piled three high as a crib: two
        // lying east to west, 9 m apart; two across them, south to north; one across those, east
        // to west again. Each crosses those under it 1 m and 10 m up from the bottom of its body
        // (its centre of mass, 4.4 m up, between), and where they have no legs (stowed, these lie
        // up the side from 2.5 to 7.7 m), as a pile of crossed logs stands. (Not piled in grooves:
        // three side by side with others in the grooves between them wedge them apart, and the
        // pile falls in.) Only the bottom two are on the ground; the others rest on those under
        // them, so they must be held against the outward pull's part along the ground as the
        // ground holds the bottom ones, layer on layer, or the top one rolls towards the equator
        // and the pile never freezes.
        // The zone's top is flat, square to up at its middle, so off its middle it tilts towards
        // the middle (0.7 mrad 7 m off it), and a pile laid off the middle settles a few
        // centimetres that way before it freezes. Laid south of the middle that is north, the way
        // the equator is too; so the pile is laid twice, south of the middle and as its mirror
        // image north of it, where the tilt takes it south. What the tilt does cancels between the
        // two, and what is left of each booster's drift north is any pull towards the equator.
        var zone = LaunchSites.Named("Polar").LandingZone!.Value;
        var (east, north, up) = (Planet.East(zone), Planet.North(zone), Planet.Up(zone));
        var radius = Vehicle.Booster.Stages[0].Radius;
        const float Gap = 0.01f, Apart = 9f;
        var rise = 2f * radius + Gap; // from one layer's axes to the next one's

        (float? Froze, float[] Along, float[] North) Pile(float side)
        {
            var n = north * side; // the pile's own north: south for the mirror image
            var corner = zone - (east + n) * 10.5f;
            using var physics = new RocketPhysics(GroundBody.Planet);
            var pile = new List<Craft>();
            void Lay(Vector3 at, Vector3 towards, int layer)
            {
                var placed = Drops.Placed(at, towards, 0f, leanDegrees: 90f, legsOut: false, clearance: Gap);
                pile.Add(physics.Restore(Vehicle.Booster, 0, 0, placed.Position + up * (rise * layer), placed.Rotation, Vector3.Zero, Vector3.Zero, legDeploy: 0f, fairingOn: false));
            }
            Lay(corner, east, 0);
            Lay(corner + n * Apart, east, 0);
            Lay(corner + east * 1f - n * 1f, n, 1);
            Lay(corner + east * (1f + Apart) - n * 1f, n, 1);
            Lay(corner + n * 11f, east, 2); // across the middle ones 12 m up each, past their legs
            var start = pile.Select(physics.Position).ToArray();

            float? froze = null;
            for (var t = 0f; t < 30f && froze is null; t += 1f / 60)
            {
                physics.Update(1f / 60, []);
                var frozen = pile.Count(c => c.Frozen);
                Assert.True(frozen == 0 || frozen == pile.Count, $"at {t:0.00} s only {frozen} of the {pile.Count} froze");
                if (frozen == pile.Count)
                    froze = t;
            }
            var moved = pile.Select((c, i) => physics.Position(c) - start[i]).ToArray();
            var along = moved.Select(m => (m - up * Vector3.Dot(m, up)).Length()).ToArray();
            output.WriteLine($"{(side > 0 ? "south of the middle" : "north of it")}: {(froze is { } s ? $"froze together after {s:0.00} s" : "never froze")}; " +
                "each moved along the ground " + string.Join(", ", along.Select(a => $"{a:0.000}")) + " m (bottom to top), north " +
                string.Join(", ", moved.Select(m => $"{Vector3.Dot(m, north):0.0000}")) + " m");
            return (froze, along, moved.Select(m => Vector3.Dot(m, north)).ToArray());
        }

        var (south, mirror) = (Pile(1f), Pile(-1f));
        var equatorward = south.North.Zip(mirror.North, (a, b) => (a + b) / 2).ToArray();
        output.WriteLine("each one's drift towards the equator, the tilt's taken out: " + string.Join(", ", equatorward.Select(e => $"{e * 1000f:0.0}")) + " mm");
        Assert.NotNull(south.Froze);
        Assert.NotNull(mirror.Froze);
        // Measured: the middle two 0.07 m, the rest 0.02 m or less, all towards the zone's middle.
        // Rolling towards the equator, the top one went 13.8 m in 30 s.
        Assert.True(south.Along.Concat(mirror.Along).Max() < 0.1f, $"one moved {south.Along.Concat(mirror.Along).Max():0.000} m along the ground");
        // Measured: 4.9 mm at most, and as much the other way (-3.8 mm): what is left is how each
        // settles, not a pull. (Laid only south of the middle, the tilt alone read as 73 mm.)
        Assert.True(equatorward.Max() < 0.01f, $"one drifted {equatorward.Max() * 1000f:0.0} mm towards the equator");
    }

    [Fact]
    public void Two_crafts_touching_that_rise_off_the_ground_together_are_not_held_grounded()
    {
        // One booster lying on Home's landing zone and another lying on it, both thrown straight
        // up at 15 m/s: they rise together, still touching, and once clear of the ground neither is
        // on anything solid, so both feel the whole of the turning frame's outward pull again.
        // Neither may count as grounded by touching the other.
        var zone = Scene.LandingZoneCentre;
        var up = Planet.Up(zone);
        using var physics = new RocketPhysics(GroundBody.Planet);
        var placed = Drops.Placed(zone, Planet.East(zone), 0f, leanDegrees: 90f, legsOut: false, clearance: 0.02f);
        var radius = Vehicle.Booster.Stages[0].Radius;
        var lower = physics.Restore(Vehicle.Booster, 0, 0, placed.Position, placed.Rotation, up * 15f, Vector3.Zero, legDeploy: 0f, fairingOn: false);
        var upper = physics.Restore(Vehicle.Booster, 0, 0, placed.Position + up * (2f * radius + 0.05f), placed.Rotation, up * 15f, Vector3.Zero, legDeploy: 0f, fairingOn: false);
        physics.Update(1f / 60, []);
        Assert.True(lower.Grounded && upper.Grounded, "on the zone, one on the other, both should start grounded");
        for (var t = 0f; t < 0.5f; t += 1f / 60)
            physics.Update(1f / 60, []);
        var height = Planet.GroundAltitude(physics.CentreOfMass(lower));
        output.WriteLine($"after 0.5 s the lower one is {height:0.0} m up; grounded: lower {lower.Grounded}, upper {upper.Grounded}");
        Assert.True(height > 3f, $"only {height:0.0} m up");
        // Within touching distance as what rests on what is told (FindGrounded), not merely by Jolt's
        // contacts: lying the same way (their axes still parallel, and their origins side by side
        // across them), their gap is their origins' distance apart less both radii.
        var (lowerAxis, upperAxis) = (physics.Up(lower), physics.Up(upper));
        var apart = physics.Position(upper) - physics.Position(lower);
        Assert.True(Vector3.Dot(lowerAxis, upperAxis) > 0.999f, $"they no longer lie the same way: {float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(lowerAxis, upperAxis), -1f, 1f))):0.0}° apart");
        Assert.True(MathF.Abs(Vector3.Dot(apart, lowerAxis)) < 0.1f, $"one has slid {Vector3.Dot(apart, lowerAxis):0.00} m along the other");
        var gap = apart.Length() - 2f * radius;
        Assert.True(gap < 0.5f, $"they should still be touching: {gap:0.00} m apart");
        Assert.False(lower.Grounded, "the lower one is held grounded by the one on it");
        Assert.False(upper.Grounded, "the upper one is held grounded by the one under it");
    }

    [Fact]
    public void A_vessel_on_the_pad_moves_east_among_the_stars()
    {
        using var sim = new Simulation();
        var speed = sim.Flight.Controlled.OrbitSpeed;
        output.WriteLine($"orbit speed on the pad {speed:0.00} m/s");
        Assert.InRange(speed, 56f, 57f);
    }

    /// <summary>
    /// Where something let go at <paramref name="start"/> moving at <paramref name="groundVelocity"/>
    /// over the ground comes down on the terrain, and how long it takes: a small integration of the
    /// fall in the ground's turning frame (inverse-square gravity, the Coriolis and outward pulls, no
    /// air; Runge-Kutta, fourth order, 5 ms steps), which shares nothing with <see cref="Orbit"/>'s
    /// ellipse and turned ground but the planet.
    /// </summary>
    internal static (Vector3 At, double Seconds) FallToGround(Vector3 start, Vector3 groundVelocity)
    {
        var (p, v, t) = (start, groundVelocity, 0.0);
        static Vector3 Pull(Vector3 p, Vector3 v) => Planet.Gravity(p) + Planet.TurningPull(p, v);
        static float Above(Vector3 p) => Planet.Altitude(p) - Terrain.SurfaceHeight(Planet.Up(p));
        const float Dt = 0.005f;
        while (true)
        {
            var (k1p, k1v) = (v, Pull(p, v));
            var (k2p, k2v) = (v + k1v * (Dt / 2), Pull(p + k1p * (Dt / 2), v + k1v * (Dt / 2)));
            var (k3p, k3v) = (v + k2v * (Dt / 2), Pull(p + k2p * (Dt / 2), v + k2v * (Dt / 2)));
            var (k4p, k4v) = (v + k3v * Dt, Pull(p + k3p * Dt, v + k3v * Dt));
            var next = p + (k1p + 2 * k2p + 2 * k3p + k4p) * (Dt / 6);
            var nextV = v + (k1v + 2 * k2v + 2 * k3v + k4v) * (Dt / 6);
            if (Above(next) < 0)
            {
                var part = Above(p) / (Above(p) - Above(next)); // to the ground, between the steps
                return (Vector3.Lerp(p, next, part), t + Dt * part);
            }
            (p, v, t) = (next, nextV, t + Dt);
        }
    }

    [Fact]
    public void A_path_dropped_straight_down_comes_down_east_of_under_it()
    {
        // Let go 2,000 m over Home, it moves east among the stars as the ground under it does, but
        // faster (it is further out), so its path, worked out among the stars with the ground turned
        // under it, comes down east of under it. The vacuum theory's ⅓ ω g t³ (about 153 m) takes an
        // even 1 g; on this small planet gravity thins with height, so the fall is longer and the
        // drift larger. So the path is checked against a small integration of the same fall in the
        // ground's turning frame (inverse-square gravity, the Coriolis and outward pulls, no air),
        // which shares nothing with Orbit's ellipse and turned ground but the planet.
        var start = Planet.Centre + Vector3.UnitY * (Planet.SeaRadius + 2000f);
        var orbit = Orbit.OverGround(GroundBody.Planet, start, Vector3.Zero);
        var under = Planet.Centre + Vector3.UnitY * (Planet.SeaRadius + Terrain.SurfaceHeight(Vector3.UnitY));
        var east = Planet.East(under);

        var (p, t) = FallToGround(start, Vector3.Zero);

        var integrated = Vector3.Dot(p - under, east);
        var drift = Vector3.Dot(orbit.Landing - under, east);
        var north = Vector3.Dot(orbit.Landing - under, Planet.North(under));
        var even = Planet.SpinRate * Scene.SurfaceGravity * MathF.Pow(MathF.Sqrt(2 * 2000f / Scene.SurfaceGravity), 3) / 3;
        output.WriteLine($"path: {drift:0.0} m east, {north:0.0} m north; integrated: {integrated:0.0} m east after {t:0.0} s, " +
            $"{Vector3.Distance(orbit.Landing, p):0.00} m from the path's; even 1 g theory {even:0.0} m");
        Assert.True(orbit.EndsOnGround);
        Assert.InRange(drift, integrated * 0.99f, integrated * 1.01f);
        Assert.True(Vector3.Distance(orbit.Landing, p) < 2f, $"{Vector3.Distance(orbit.Landing, p):0.00} m from the integrated landing");
        Assert.InRange(north, -5f, 5f); // Home is on the equator: the turn carries it east only
    }

    private static float Degrees(Vector3 a, Vector3 b) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)));

    [Fact]
    public void A_burn_guides_by_the_stars()
    {
        // The lowest satellite, with a node half a minute ahead to raise its far side to 10 km.
        // The planet turns about 10° under it meanwhile; the burn it guides by is the orbit's,
        // fixed among the stars, so over the ground it turns back by that turn and among the
        // stars it stays put before the burn; through it, it is that less what the engine has
        // given. Held prograde, the nose sweeps through the node's direction as the orbit carries
        // it round (about 0.7° in the second's burn), so what is left truly turns away from the
        // aim as it shrinks (1.8° with a tenth of it left): the guide is checked against that,
        // the engine's part found from the satellite's velocity against the orbit it would have
        // coasted on from ignition, not from anything the guide keeps.
        using var sim = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        var flight = sim.Flight;
        var satellite = flight.WorldVessels.First(v => v.Flight == 0);
        sim.Step(new FlightInput(SwitchTo: satellite));
        float r1 = Planet.SeaRadius + 9000f, r2 = Planet.SeaRadius + 10000f;
        var raise = MathF.Sqrt(Planet.Mu / r1) * (MathF.Sqrt(2f * r2 / (r1 + r2)) - 1f);
        sim.Step(new FlightInput(Plan: NodeTests.Chain(satellite, flight.WorldTime, flight.WorldTime + 30, new ManoeuvreNode(0, Prograde: raise)), ChooseHold: HoldMode.Prograde));
        var plan = new ManoeuvrePlan();
        plan.Update(satellite, satellite.Chain, flight.WorldTime);
        var planned = plan.Last.Orbit;
        var plannedTop = planned.Distance(planned.NextApoapsis!.Value) - Planet.SeaRadius;

        // Every vector is compared among the stars, in the frame that matched the ground at the start.
        var (start, aim) = (flight.WorldTime, flight.Guide!.Value.Left);
        Vector3 AmongStars(Vector3 groundVelocity, double now) =>
            Planet.TurnedDirection(Planet.AmongStars(satellite.CentreOfMass, groundVelocity), now - start);
        var coast = (Orbit: Orbit.OverGround(GroundBody.Planet, satellite.CentreOfMass, satellite.Velocity, findEnd: false), At: start);
        var (wandered, lowest) = (0f, float.MaxValue);
        Assert.True(sim.Run((f, _) =>
        {
            if (f.Guide is not { } guide)
                return default;
            var now = f.WorldTime;
            if (!f.EngineLit && !satellite.BurnBegun)
                coast = (Orbit.OverGround(GroundBody.Planet, satellite.CentreOfMass, satellite.Velocity, findEnd: false), now); // the last moment before the engine
            var given = AmongStars(satellite.Velocity, now)
                - Planet.TurnedDirection(coast.Orbit.StateAfter(now - coast.At).Velocity, coast.At - start);
            var left = aim - given;
            // While any real part of the burn is still to come (at the end, what is left is only
            // the rounding of the cut-off, pointing anywhere).
            if (left.Length() > raise / 10)
                wandered = MathF.Max(wandered, Degrees(Planet.TurnedDirection(guide.Left, now - start), left));
            lowest = MathF.Min(lowest, guide.Left.Length());
            if (f.EngineLit)
                return guide.Overshot || guide.Left.Length() < 0.05f ? new FlightInput(CutThrottle: true) : default;
            return guide.Seconds <= guide.BurnSeconds / 2 && lowest > 0.05f ? new FlightInput(FullThrottle: true) : default;
        }, _ => satellite.Chain is null, maxSeconds: 60), "the node should go once matched and cut");
        var top = satellite.Apoapsis;
        output.WriteLine($"over {flight.WorldTime - start:0.0} s (the planet turning {float.RadiansToDegrees(Planet.SpinRate * (float)(flight.WorldTime - start)):0.0}°) " +
            $"the guide among the stars was at most {wandered:0.000}° off what was left; delta-v left down to {lowest:0.000} m/s; top planned {plannedTop:0.0} m, flown {top:0.0} m");
        Assert.InRange(wandered, 0f, 0.5f);
        Assert.True(lowest < 1f, $"the burn should use up the delta-v left (it got to {lowest:0.000} m/s)");
        Assert.InRange(top, plannedTop - 30, plannedTop + 30);
    }

    [Fact]
    public void In_auto_the_holds_follow_the_ground_on_a_hop_and_the_stars_in_orbit()
    {
        // On a hop the frame in use is the surface's, even above the air (its path comes back down
        // into it): the holds steer by the way it moves over the ground, which the air turns with.
        var (at, downhill, _) = Drops.FindSlope(0f, 3f);
        foreach (var clearance in new[] { 1000f, 4000f })
        {
            using var sim = Drops.At(at, downhill, 50f, clearance: clearance, legsOut: false);
            var vessel = sim.Flight.Controlled;
            output.WriteLine($"{Planet.Altitude(vessel.CentreOfMass):0} m up: {sim.Flight.Frame}, hold {sim.Flight.FrameVelocity}, ground {vessel.Velocity}");
            Assert.Equal(Frame.Surface, sim.Flight.Frame);
            Assert.Equal(vessel.Velocity, sim.Flight.FrameVelocity);
        }

        // In orbit, the orbital frame: by its orbit, among the stars.
        using var orbit = new Simulation(Mission.ReachOrbit, world: WorldPresets.Satellites());
        var satellite = orbit.Flight.WorldVessels.First(v => v.Flight == 0);
        orbit.Step(new FlightInput(SwitchTo: satellite));
        Assert.Equal(Frame.Orbital, orbit.Flight.Frame);
        Assert.Equal(Planet.AmongStars(satellite.CentreOfMass, satellite.Velocity), orbit.Flight.FrameVelocity);
    }

    [Fact]
    public void The_stars_turn_the_way_the_sky_shader_turns_them()
    {
        // The sky's GLSL turns the ray about k = (0, 0, -1) by the angle it is handed (Atmosphere.z,
        // Planet.TurnAngle) with Rodrigues' formula: d cos(a) + (k x d) sin(a) + k (k . d) (1 - cos(a)).
        // This is that line in C#, so a flipped sign in either place fails here.
        static Vector3 Shader(Vector3 d, float angle)
        {
            var k = new Vector3(0f, 0f, -1f);
            return d * MathF.Cos(angle) + Vector3.Cross(k, d) * MathF.Sin(angle) + k * Vector3.Dot(k, d) * (1f - MathF.Cos(angle));
        }
        Assert.Equal(new Vector3(0f, 0f, -1f), Planet.NorthPole);
        var d = Vector3.Normalize(new Vector3(0.3f, 0.5f, -0.8f));
        foreach (var seconds in new[] { 10.0, 300.0, 1e7 })
        {
            var angle = Planet.TurnAngle(seconds);
            Assert.InRange(angle, -MathF.PI, MathF.PI);
            var off = Degrees(Shader(d, angle), Planet.TurnedDirection(d, seconds));
            Assert.True(off < 0.001f, $"{seconds} s: {off} degrees apart");
        }
    }
}
