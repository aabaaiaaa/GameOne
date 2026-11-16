using System.Diagnostics;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Where an <see cref="Orbit"/> says something will be, and when (checked against gravity stepped
/// through by hand, and the real physics); its highest and lowest points ahead, and where it comes
/// down into the air; and <see cref="Encounters"/>: the closest approach to a target, and the
/// windows where one burn would take the vessel to it, or bring it down by a place on the ground.
/// </summary>
public sealed class EncounterTests(ITestOutputHelper output)
{
    private static readonly Vector3 Up = Vector3.UnitY, East = Vector3.UnitX;

    private static Vector3 Above(float aboveSea) => Planet.Centre + Up * (Planet.SeaRadius + aboveSea);

    /// <summary>A point <paramref name="altitude"/> above the sea, <paramref name="angle"/> radians east round the planet from above the pad.</summary>
    private static Vector3 Round(float altitude, double angle) =>
        Planet.Centre + (Up * (float)Math.Cos(angle) + East * (float)Math.Sin(angle)) * (Planet.SeaRadius + altitude);

    /// <summary>The circular orbit's velocity at <paramref name="at"/>, going east (clockwise seen from the north).</summary>
    private static Vector3 Circular(Vector3 at)
    {
        var up = Vector3.Normalize(at - Planet.Centre);
        var east = Vector3.Normalize(Vector3.Cross(-Vector3.UnitZ, up));
        return east * MathF.Sqrt(Planet.Mu / Vector3.Distance(at, Planet.Centre));
    }

    /// <summary>Gravity alone, stepped through finely (fourth-order Runge-Kutta, in doubles): where it really goes.</summary>
    private static (Vector3 Position, Vector3 Velocity) Coast(Vector3 position, Vector3 velocity, double seconds)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(seconds) / 0.01));
        var h = seconds / steps;
        double[] s = [position.X, position.Y - Planet.Centre.Y, position.Z, velocity.X, velocity.Y, velocity.Z];
        static double[] F(double[] y)
        {
            var r = Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
            var k = -Planet.Mu / (r * r * r);
            return [y[3], y[4], y[5], k * y[0], k * y[1], k * y[2]];
        }
        static double[] Add(double[] a, double[] b, double t) => [.. a.Select((x, i) => x + b[i] * t)];
        for (var i = 0; i < steps; i++)
        {
            var k1 = F(s);
            var k2 = F(Add(s, k1, h / 2));
            var k3 = F(Add(s, k2, h / 2));
            var k4 = F(Add(s, k3, h));
            s = [.. s.Select((x, j) => x + h / 6 * (k1[j] + 2 * k2[j] + 2 * k3[j] + k4[j]))];
        }
        return (new Vector3((float)s[0], (float)(s[1] + Planet.Centre.Y), (float)s[2]), new Vector3((float)s[3], (float)s[4], (float)s[5]));
    }

    [Theory]
    [InlineData(0.0f, 1.0f)]   // a circle
    [InlineData(10f, 1.12f)]   // an ellipse
    [InlineData(-30f, 0.95f)]  // one coming down
    [InlineData(20f, 1.6f)]    // escaping
    public void It_says_where_it_will_be_when(float climb, float speedFactor)
    {
        var start = Above(4000f);
        var velocity = East * Planet.CircularSpeed(4000f) * speedFactor + Up * climb;
        var orbit = new Orbit(GroundBody.Planet, start, velocity);
        foreach (var seconds in new[] { 10.0, 60.0, 150.0 })
        {
            if (seconds > orbit.PathSeconds)
                continue;
            var (expected, _) = Coast(start, velocity, seconds);
            var predicted = orbit.PositionAfter(seconds);
            output.WriteLine($"after {seconds} s: {Vector3.Distance(predicted, expected):0.000} m off");
            Assert.True(Vector3.Distance(predicted, expected) < 1f, $"{Vector3.Distance(predicted, expected):0.0} m off after {seconds} s");
            Assert.InRange(orbit.TimeTo(orbit.AnomalyAfter(seconds)), seconds - 1e-3, seconds + 1e-3);
        }
    }

    [Fact]
    public void A_vessel_in_orbit_is_where_its_orbit_says_a_minute_on()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        Assert.True(sim.Run(new OrbitAutopilot().Fly, f => f.AchievedOrbit, maxSeconds: 300));
        var vessel = sim.Flight.Controlled;
        // The orbit is among the stars; the vessel is flown on the ground, which turns a minute's
        // worth under it by then: so where the orbit says, turned back with the ground.
        var predicted = Planet.Turned(Orbit.OverGround(GroundBody.Planet, vessel.CentreOfMass, vessel.Velocity).PositionAfter(60), -60);
        sim.Run((_, _) => default, _ => false, maxSeconds: 60);
        output.WriteLine($"{Vector3.Distance(vessel.CentreOfMass, predicted):0.0} m off after a minute");
        Assert.True(Vector3.Distance(vessel.CentreOfMass, predicted) < 15f);
    }

    [Fact]
    public void The_highest_and_lowest_points_ahead_and_where_it_comes_into_the_air()
    {
        var start = Above(5000f);
        var velocity = East * Planet.CircularSpeed(5000f) * 0.85f; // falling away from the top of a lower orbit, which dips underground
        var orbit = new Orbit(GroundBody.Planet, start, velocity);
        var (apoapsis, periapsis) = Planet.Orbit(start, velocity);

        Assert.Null(orbit.NextApoapsis); // it is at its highest now: the next one is a lap on, past where it hits the ground
        Assert.True(orbit.EndsOnGround);
        Assert.Null(orbit.NextPeriapsis); // underground
        var air = orbit.Descending(Planet.SeaRadius + Scene.AtmosphereHeight);
        Assert.NotNull(air);
        Assert.InRange(Vector3.Distance(orbit.At(air.Value), Planet.Centre) - Planet.SeaRadius, Scene.AtmosphereHeight - 1f, Scene.AtmosphereHeight + 1f);
        Assert.True(air.Value < orbit.EndAnomaly);

        var high = new Orbit(GroundBody.Planet, start, East * Planet.CircularSpeed(5000f) * 1.05f); // at its lowest now
        Assert.NotNull(high.NextApoapsis);
        Assert.InRange(high.Distance(high.NextApoapsis!.Value) - Planet.SeaRadius, Planet.Orbit(start, East * Planet.CircularSpeed(5000f) * 1.05f).Apoapsis - 1f,
            Planet.Orbit(start, East * Planet.CircularSpeed(5000f) * 1.05f).Apoapsis + 1f);
        Assert.NotNull(high.NextPeriapsis); // a lap on
        output.WriteLine($"falling orbit {periapsis:0} to {apoapsis:0} m, into the air at anomaly {air:0.00}");
    }

    [Fact]
    public void The_closest_approach_finds_where_two_paths_meet()
    {
        // Mine: a circle at 5 km. Theirs: an ellipse that passes through where I'll be 200 s on, at
        // the same moment (worked out backwards from there).
        var mineStart = Above(5000f);
        var mine = new Orbit(GroundBody.Planet, mineStart, Circular(mineStart));
        var meet = mine.PositionAfter(200);
        var (theirStart, theirVelocity) = Coast(meet, Circular(meet) * 1.08f + Vector3.Normalize(meet - Planet.Centre) * 20f, -200);
        var theirs = new Orbit(GroundBody.Planet, theirStart, theirVelocity);

        var closest = Encounters.Closest(mine, theirs, default);
        Assert.NotNull(closest);
        output.WriteLine($"closest {closest.Value.Distance:0.0} m, {closest.Value.Seconds:0.0} s on");
        Assert.True(closest.Value.Distance < 5f);
        Assert.InRange(closest.Value.Seconds, 199, 201);
    }

    [Fact]
    public void The_closest_approach_to_a_place_on_the_ground_follows_it_round_as_the_ground_turns()
    {
        // A place (on a mast as high as the orbit, say) that the ground carries under where I'll be
        // 200 s on: it is there then, not where it is now.
        var mineStart = Above(5000f);
        var mine = new Orbit(GroundBody.Planet, mineStart, Circular(mineStart));
        var place = Planet.Turned(mine.PositionAfter(200), -200);

        var closest = Encounters.Closest(mine, null, place);
        Assert.NotNull(closest);
        output.WriteLine($"closest {closest.Value.Distance:0.0} m, {closest.Value.Seconds:0.0} s on");
        Assert.True(closest.Value.Distance < 5f);
        Assert.InRange(closest.Value.Seconds, 199, 201);
    }

    [Fact]
    public void The_transfer_window_is_where_one_burn_would_reach_the_target()
    {
        // Mine: a circle at 4 km. The target: a circle at 8 km, placed so that a transfer starting
        // a radian ahead of me (a Hohmann transfer: half an ellipse out to its height) arrives just
        // as it gets there.
        const float low = 4000f, high = 8000f;
        var mineStart = Round(low, 0);
        var mine = new Orbit(GroundBody.Planet, mineStart, Circular(mineStart));
        var burnAngle = 1.0;
        var burnTime = burnAngle / (Planet.CircularSpeed(low) / (Planet.SeaRadius + low));
        var a = Planet.SeaRadius + (low + high) / 2;
        var transferTime = Math.PI * Math.Sqrt(a * a * a / Planet.Mu);
        var targetSpeed = Planet.CircularSpeed(high) / (Planet.SeaRadius + high);
        var targetStart = Round(high, burnAngle + Math.PI - targetSpeed * (burnTime + transferTime));
        var theirs = new Orbit(GroundBody.Planet, targetStart, Circular(targetStart));

        var window = new List<Encounters.WindowPoint>();
        Encounters.Transfer(mine, theirs, Scene.TransferWithin, window);
        var clock = Stopwatch.StartNew(); // timed once it has warmed up: it is worked out every frame
        Encounters.Transfer(mine, theirs, Scene.TransferWithin, window);
        output.WriteLine($"worked out in {clock.Elapsed.TotalMilliseconds:0.00} ms; {window.Count} points");
        Assert.NotEmpty(window);
        var best = window.MinBy(p => p.Miss);
        output.WriteLine($"best {best.Miss:0} m at {best.Anomaly - mine.Now:0.000} rad ahead; from {window[0].Anomaly - mine.Now:0.00} to {window[^1].Anomaly - mine.Now:0.00}");
        Assert.InRange(best.Anomaly - mine.Now, burnAngle - 0.06, burnAngle + 0.06);
        Assert.True(best.Miss < 200f);
        // Not the whole lap: a stretch round the right place.
        Assert.All(window, p => Assert.InRange(p.Anomaly - mine.Now, burnAngle - 0.6, burnAngle + 0.6));
    }

    /// <summary>
    /// Where something coasting from <paramref name="position"/> at <paramref name="velocity"/>
    /// (among the stars), <paramref name="from"/> seconds from now, comes down on the terrain, in
    /// the ground's frame: gravity alone stepped through (<see cref="Coast"/>), each point tested
    /// against the ground as it will have turned by then, the crossing found to a few millimetres.
    /// </summary>
    private static Vector3 FlyDown(Vector3 position, Vector3 velocity, double from)
    {
        static Vector3 Ground(Vector3 at, double seconds) => Planet.Turned(at, -seconds);
        static float Above(Vector3 ground) => Planet.Altitude(ground) - Terrain.SurfaceHeight(Planet.Up(ground));
        const double Chunk = 1.0;
        var t = from;
        for (var i = 0; i < 20000; i++)
        {
            var (next, nextVelocity) = Coast(position, velocity, Chunk);
            if (Above(Ground(next, t + Chunk)) < 0)
            {
                double lo = 0, hi = Chunk; // the crossing is in this chunk: halved down to it
                while (hi - lo > 1e-4)
                {
                    var mid = (lo + hi) / 2;
                    if (Above(Ground(Coast(position, velocity, mid).Position, t + mid)) < 0)
                        hi = mid;
                    else
                        lo = mid;
                }
                return Ground(Coast(position, velocity, hi).Position, t + hi);
            }
            (position, velocity, t) = (next, nextVelocity, t + Chunk);
        }
        throw new InvalidOperationException("it never came down");
    }

    [Fact]
    public void The_de_orbit_window_is_where_a_burn_would_bring_it_down_by_the_place()
    {
        // Two radians east of the pad. The ground turns east under the orbit a third as fast as the
        // orbit goes round, so the place comes back under it only every lap and a half or so: from
        // some places in orbit (just past it) the window is on the next lap (see below), and the
        // start is put where it is on this one.
        var mineStart = Round(5000f, 2.0);
        var mine = new Orbit(GroundBody.Planet, mineStart, Circular(mineStart));
        var site = Sites.Top(2); // the drone ship, a little east of the pad
        var window = new List<Encounters.WindowPoint>();
        Assert.Equal(0, Encounters.Deorbit(mine, site, Scene.DeorbitWithin, window));
        Assert.NotEmpty(window);
        var best = window.MinBy(p => p.Miss);
        // The burn comes most of a lap on (round to the place, which has moved on east by then).
        var ahead = best.Anomaly - mine.Now;
        output.WriteLine($"{window.Count} points, best {best.Miss:0} m, {ahead:0.00} rad ahead");
        Assert.True(best.Miss < 100f);
        Assert.True(window.Count >= 3, "a stretch, not a single point");
        Assert.InRange(ahead, Math.PI * 0.8, 2 * Math.PI);
        Assert.True(window.Max(p => p.Anomaly) - window.Min(p => p.Anomaly) < 0.5, "it should be a short stretch, not half the orbit");

        // A burn in the middle of it comes down by the place as it is by then, the ground having
        // turned under the orbit all the way round and down. The burn is the window's own
        // (Encounters.DeorbitBurn); it is checked to be the size the window says (its lowest point
        // DeorbitDepth under the ground), then flown: gravity stepped through, not the ellipse.
        var middle = (window.Min(p => p.Anomaly) + window.Max(p => p.Anomaly)) / 2;
        var burn = Encounters.DeorbitBurn(mine, middle);
        Assert.InRange(burn.PeriapsisDistance, Planet.Radius - Scene.DeorbitDepth - 1, Planet.Radius - Scene.DeorbitDepth + 1);
        var burnSeconds = mine.TimeTo(middle);
        var flown = FlyDown(mine.At(middle), burn.StateAfter(0).Velocity, burnSeconds);
        output.WriteLine($"a burn in the middle, flown, comes down {Vector3.Distance(flown, site):0} m from it " +
            $"({Vector3.Distance(flown, burn.Landing):0.00} m from where its orbit says)");
        Assert.True(Vector3.Distance(flown, site) < Scene.DeorbitWithin);
        Assert.True(Vector3.Distance(flown, burn.Landing) < 5f, "where the window says it comes down should be where it does");

        // Coming down already, steeper than that: no window, just where it lands.
        var falling = new Orbit(GroundBody.Planet, mineStart, Circular(mineStart) * 0.5f);
        Assert.Equal(-1, Encounters.Deorbit(falling, site, Scene.DeorbitWithin, window));
        Assert.Empty(window);
    }

    [Fact]
    public void A_de_orbit_window_always_shows_within_two_laps()
    {
        // The place comes back under the orbit only every lap and a half or so, so from anywhere
        // round it a window comes within two laps (Scene.DeorbitLaps). Just past the place, it has
        // gone by for this lap: the window is on the next.
        var site = Sites.Top(2);
        var up = Vector3.Normalize(site - Planet.Centre);
        var siteAngle = Math.Atan2(Vector3.Dot(up, East), Vector3.Dot(up, Up));
        var window = new List<Encounters.WindowPoint>();
        for (var i = 0; i < 12; i++)
        {
            var start = Round(5000f, siteAngle + 0.1 + i * Math.PI / 6);
            var mine = new Orbit(GroundBody.Planet, start, Circular(start));
            var lap = Encounters.Deorbit(mine, site, Scene.DeorbitWithin, window);
            output.WriteLine($"{0.1 + i * Math.PI / 6:0.00} rad past the place: {window.Count} points on lap {lap}" +
                (window.Count > 0 ? $", best {window.Min(p => p.Miss):0} m, {window[0].Anomaly - mine.Now:0.00} rad ahead" : ""));
            Assert.NotEmpty(window);
            Assert.InRange(lap, 0, 1);
            Assert.All(window, p => Assert.InRange(p.Anomaly - mine.Now, 2 * Math.PI * lap, 2 * Math.PI * (lap + 1)));
            Assert.True(window.Min(p => p.Miss) < 100f);
            if (i == 0)
                Assert.Equal(1, lap);
        }
    }

    [Fact]
    public void A_leg_starting_later_finds_the_same_windows_shifted_by_its_start()
    {
        // A leg of a plan starts some way ahead (at a node), and the ground will have turned by
        // then. On the same orbit, a leg starting a while on finds the same windows as the orbit
        // as it is now: the same burns, at the same times from now.
        var window = new List<Encounters.WindowPoint>();
        void Same(Orbit mine, double later, Action<Orbit, List<Encounters.WindowPoint>> find, string what)
        {
            var (position, velocity) = mine.StateAfter(later);
            var leg = new Orbit(GroundBody.Planet, position, velocity, from: later);
            find(mine, window);
            Assert.NotEmpty(window);
            var now = window.MinBy(p => p.Miss);
            find(leg, window);
            Assert.NotEmpty(window);
            var then = window.MinBy(p => p.Miss);
            var (secondsNow, secondsThen) = (mine.TimeTo(now.Anomaly), later + leg.TimeTo(then.Anomaly));
            var apart = Vector3.Distance(mine.At(now.Anomaly), leg.At(then.Anomaly));
            output.WriteLine($"{what}: best {now.Miss:0} m at {secondsNow:0.0} s from now; from a leg {later:0} s on, best {then.Miss:0} m at {secondsThen:0.0} s ({apart:0} m apart)");
            // The two are sampled on grids offset by the leg's start, so as close as a fine step.
            var step = mine.Period / 500;
            Assert.InRange(secondsThen - secondsNow, -step, step);
            Assert.True(then.Miss < 200f);
        }

        var deorbitStart = Round(5000f, 2.0);
        Same(new Orbit(GroundBody.Planet, deorbitStart, Circular(deorbitStart)), 120, (o, w) => Encounters.Deorbit(o, Sites.Top(2), Scene.DeorbitWithin, w), "de-orbit");

        // The transfer window's test's orbits (a burn a radian ahead), with the leg starting half way there.
        const float low = 4000f, high = 8000f;
        var mineStart = Round(low, 0);
        var burnAngle = 1.0;
        var burnTime = burnAngle / (Planet.CircularSpeed(low) / (Planet.SeaRadius + low));
        var a = Planet.SeaRadius + (low + high) / 2;
        var transferTime = Math.PI * Math.Sqrt(a * a * a / Planet.Mu);
        var targetSpeed = Planet.CircularSpeed(high) / (Planet.SeaRadius + high);
        var targetStart = Round(high, burnAngle + Math.PI - targetSpeed * (burnTime + transferTime));
        var theirs = new Orbit(GroundBody.Planet, targetStart, Circular(targetStart));
        var mineNow = new Orbit(GroundBody.Planet, mineStart, Circular(mineStart));
        Same(mineNow, burnTime / 2, (o, w) => Encounters.Transfer(o, theirs, Scene.TransferWithin, w), "transfer");

        // The target's orbit taken from later along its path finds the same window: from half way
        // to the burn, and from after the transfer arrives (the target then found by running its
        // orbit back).
        Encounters.Transfer(mineNow, theirs, Scene.TransferWithin, window);
        var baseline = mineNow.TimeTo(window.MinBy(p => p.Miss).Anomaly);
        foreach (var theirsFrom in new[] { burnTime / 2, burnTime + transferTime + 100 })
        {
            var (position, velocity) = theirs.StateAfter(theirsFrom);
            Encounters.Transfer(mineNow, new Orbit(GroundBody.Planet, position, velocity, from: theirsFrom), Scene.TransferWithin, window);
            Assert.NotEmpty(window);
            var best = window.MinBy(p => p.Miss);
            output.WriteLine($"the target's orbit from {theirsFrom:0} s on: best {best.Miss:0} m at {mineNow.TimeTo(best.Anomaly):0.0} s (from its orbit now, {baseline:0.0} s)");
            Assert.InRange(mineNow.TimeTo(best.Anomaly) - baseline, -0.5, 0.5);
            Assert.True(best.Miss < 200f);
        }
    }

    [Fact]
    public void A_de_orbit_window_across_the_lap_s_end_is_whole()
    {
        // Started where the window runs across the end of the lap ahead: it is followed on into
        // the next, one stretch with no gap, on the next lap (so the line is drawn round to it).
        var site = Sites.Top(2);
        var up = Vector3.Normalize(site - Planet.Centre);
        var siteAngle = Math.Atan2(Vector3.Dot(up, East), Vector3.Dot(up, Up));
        var window = new List<Encounters.WindowPoint>();
        // The start whose window is most evenly across the lap's end (its middle nearest it).
        var (past, off) = (0.0, double.MaxValue);
        for (var p = 0.30; p < 0.90; p += 0.01)
        {
            var at = Round(5000f, siteAngle + p);
            var orbit = new Orbit(GroundBody.Planet, at, Circular(at));
            Encounters.Deorbit(orbit, site, Scene.DeorbitWithin, window);
            if (window.Count > 0 && Math.Abs((window[0].Anomaly + window[^1].Anomaly) / 2 - (orbit.Now + 2 * Math.PI)) is var o && o < off)
                (past, off) = (p, o);
        }
        var start = Round(5000f, siteAngle + past);
        var mine = new Orbit(GroundBody.Planet, start, Circular(start));
        var lap = Encounters.Deorbit(mine, site, Scene.DeorbitWithin, window);
        var lapEnd = mine.Now + 2 * Math.PI;
        var gap = window.Zip(window.Skip(1), (a, b) => b.Anomaly - a.Anomaly).Max();
        output.WriteLine($"{past:0.00} rad past the place: {window.Count} points from {window[0].Anomaly - mine.Now:0.000} to {window[^1].Anomaly - mine.Now:0.000} rad ahead (the lap ends at {2 * Math.PI:0.000}), widest gap {gap:0.000} rad, lap {lap}");
        Assert.Equal(1, lap);
        Assert.True(gap < Encounters.WindowGap, "one stretch, not two");
        Assert.True(window.Count(p => p.Anomaly < lapEnd) >= 3, "the part on this lap");
        Assert.True(window.Count(p => p.Anomaly > lapEnd) >= 3, "the part on the next");
    }

    [Fact]
    public void The_window_is_shaded_on_the_orbits_outer_side()
    {
        // A circle 100 px round a body at (500, 400) on the screen, run either way round, a window
        // on each eighth of it: the shading is offset away from the body all along it.
        var centre = new Vector2(500f, 400f);
        Vector2 On(float a) => centre + 100f * new Vector2(MathF.Cos(a), MathF.Sin(a));
        for (var k = 0; k < 8; k++)
            foreach (var way in (ReadOnlySpan<float>)[1f, -1f])
            {
                Span<Vector2> across = stackalloc Vector2[9], outward = stackalloc Vector2[9];
                for (var i = 0; i < 9; i++)
                {
                    var angle = (k + way * i / 8f) * MathF.Tau / 8;
                    across[i] = Paths.Across(On(angle - way * 0.05f), On(angle + way * 0.05f));
                    outward[i] = (On(angle) - centre) * 0.01f;
                    Assert.Equal(1f, across[i].Length(), 3);
                }
                var side = Paths.OuterSide(across, outward);
                for (var i = 0; i < 9; i++)
                    Assert.True(Vector2.Dot(across[i] * side, Vector2.Normalize(outward[i])) > 0.99f, $"eighth {k}, point {i}: shaded inward");
            }
        Assert.Equal(Vector2.Zero, Paths.Across(centre, centre)); // no length: none
    }

    [Fact]
    public void The_window_seen_near_edge_on_is_shaded_on_one_side_by_the_most_of_its_ways_out()
    {
        // A straight stretch on the screen (an orbit seen near edge-on), its points' ways out from
        // the body mostly a little one side of it but some, seen end-on, a little the other: one
        // side for the whole stretch, the most's, never a zigzag; with no say at all, over the line.
        Span<Vector2> across = stackalloc Vector2[10], outward = stackalloc Vector2[10];
        for (var i = 0; i < 10; i++)
        {
            across[i] = Paths.Across(new Vector2(100f + 10f * i, 300f), new Vector2(120f + 10f * i, 300f));
            outward[i] = new Vector2(3f, i % 3 == 0 ? -0.4f : 1f); // mostly down the screen (y down), some up
        }
        Assert.Equal(new Vector2(0f, 1f), across[0]); // a quarter turn from its run, x into y: down the screen
        Assert.Equal(1f, Paths.OuterSide(across, outward));
        outward.Clear();
        Assert.Equal(0f, Paths.OuterSide(across, outward));
    }
}
