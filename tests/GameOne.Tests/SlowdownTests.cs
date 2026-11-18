using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The forced slow-down (<see cref="Flight.Slowed"/>): as a witnessed fast collision comes near, the
/// whole game's clock and the physics' step slow together, gradually and only over the last
/// stretch, so each step's travel stays within a satellite's width and nothing passes through;
/// it eases back once the collision is over. Each is flown through <see cref="Simulation"/>, a real frame at a time
/// (<see cref="Flight.RunFrame"/>), its real time counted as frames × <see cref="Simulation.FrameSeconds"/>.
/// The worlds are <see cref="FastCollisionTests"/>' builders; "N s apart" is their middles coming together in N s.
/// </summary>
public sealed class SlowdownTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>Presses <see cref="FlightInput.Faster"/> until the player's rate is <paramref name="rate"/>.</summary>
    private static void WarpTo(Simulation sim, float rate)
    {
        while (sim.Flight.PlayerRate < rate)
            sim.Step(new FlightInput(Faster: true));
        Assert.Equal(rate, sim.Flight.PlayerRate);
    }

    /// <summary>Real seconds of <paramref name="frames"/> frames.</summary>
    private static double Real(int frames) => frames * (double)Simulation.FrameSeconds;

    [Theory]
    [InlineData(500f)]
    [InlineData(2000f)]
    [InlineData(8000f)]
    public void A_fast_pass_is_slowed_gradually_to_its_deepest(float closing)
    {
        // Head on in the vacuum, 10 m apart sideways: their sizes overlap as they pass, but their
        // bodies miss, so the pass is all approach and recession.
        var (sim, a, b) = FastCollisionTests.Fly(FastCollisionTests.Pair(closing, 5000f, 30f, sideways: 10f));
        using var _ = sim;
        var frames = new List<(float Scale, bool Closing, float Speed, float Travel)>();
        sim.Run(Nobody, f =>
        {
            var (r, u) = (Collisions.Middle(b!) - Collisions.Middle(a), b!.Velocity - a.Velocity); // their middles, as the slow-down counts the pass
            var closingNow = Vector3.Dot(r, u) < 0f;
            // The next frame's steps, at the length the flight has set for them, at this speed.
            frames.Add((f.TimeScale, closingNow, u.Length(), u.Length() * sim.Physics.StepLength));
            return !closingNow && f.Slowed is null;
        }, maxSeconds: 40);
        var pass = frames.FindIndex(x => !x.Closing);
        Assert.True(pass > 0, "they never passed");
        Assert.NotEqual(FlightState.Crashed, a.State);
        Assert.NotEqual(FlightState.Crashed, b!.State);
        Assert.Null(sim.Flight.Slowed);

        // Gradually: never quicker while they close, never slower once past.
        for (var i = 1; i < frames.Count; i++)
            if (frames[i].Closing && frames[i - 1].Closing)
                Assert.True(frames[i].Scale <= frames[i - 1].Scale * (1f + 1e-6f), $"faster again at frame {i} while closing: {frames[i - 1].Scale} to {frames[i].Scale}");
            else if (!frames[i].Closing && !frames[i - 1].Closing)
                Assert.True(frames[i].Scale >= frames[i - 1].Scale * (1f - 1e-6f), $"slower again at frame {i} once past: {frames[i - 1].Scale} to {frames[i].Scale}");

        var collision = frames[pass - 1].Speed;
        var least = frames.Min(x => x.Scale);
        var leastWanted = Slowdown.Deepest(collision);
        var slowed = Real(frames.Count(x => x.Scale < 1f));
        // Each step's travel over the real time before the pass that the scale is held at its
        // deepest (Slowdown.Hold game seconds, at that scale).
        var window = (int)(Slowdown.Hold(collision) / leastWanted / Simulation.FrameSeconds);
        var travel = frames.Skip(Math.Max(0, pass - window)).Take(Math.Min(pass, window)).Max(x => x.Travel);
        output.WriteLine($"closing {closing} (hitting at {collision:0.0} m/s): least scale {least:0.0000} (sMin {leastWanted:0.0000}), "
            + $"slowed for {slowed:0.00} s of real time, at most {travel:0.000} m a step over the last {window * Simulation.FrameSeconds:0.00} s of real time; {sim.Time:0.000} s of game time");
        Assert.InRange(least, leastWanted * 0.95f, leastWanted * 1.05f);
        Assert.True(travel <= Scene.FastCollisionStep * 1.05f, $"{travel:0.000} m a step near the pass");
    }

    [Fact]
    public void A_slow_near_miss_does_not_hide_a_fast_hit_just_behind_it()
    {
        // The flown booster overtakes another at 300 m/s, 10 m under it (their sizes overlap,
        // their bodies miss), its closest at 3 s; and while their sizes still overlap, a third
        // crosses its way at about 5,600 m/s and hits it. The near miss is the soonest collision
        // throughout (the warning's), but the step must be the hit's: slowed only for the near
        // miss (to 0.72), the hit's steps would be 33 m long.
        var reach = 2f * FastCollisionTests.BoosterSize;
        var overlap = MathF.Sqrt(reach * reach - 10f * 10f) / 300f; // how long past its closest their sizes overlap
        var hitAt = 3f + overlap / 2f;
        var pair = FastCollisionTests.Pair(5600f, 5000f, hitAt, crossing: true).Vessels;
        var up = Planet.Up(pair[0].Position.ToSingle());
        var passed = FastCollisionTests.Overtaken(pair[0], 3f, 300f, up * 10f, id: 2);
        var (sim, flown, hitter) = FastCollisionTests.Fly(new WorldState { Vessels = [pair[0], pair[1], passed], NextVesselId = 3 });
        using var _ = sim;
        var near = sim.Flight.WorldVessels.First(v => v.Id == 2);
        var (least, nearSoonest, frames) = (1f, 0, 0);
        float? hitSpeed = null;
        sim.Run(Nobody, f =>
        {
            if (flown.State != FlightState.Crashed)
            {
                least = MathF.Min(least, sim.Physics.StepScale);
                frames++;
                if (f.Warning is { } w && w.Other == near)
                    nearSoonest++;
                hitSpeed = (hitter!.Velocity - flown.Velocity).Length();
            }
            return flown.State == FlightState.Crashed && hitter!.State == FlightState.Crashed;
        }, maxSeconds: hitAt + 1.0);
        var wanted = Slowdown.Least(hitSpeed ?? 0f);
        output.WriteLine($"the near miss's sizes overlap {overlap:0.000} s past its closest; the hit at {hitAt:0.000} s, closing at {hitSpeed:0} m/s: "
            + $"the step's least scale {least:0.0000} (the hit's sMin {wanted:0.0000}, the near miss's {Slowdown.Least(300f):0.0000}); "
            + $"the near miss warned of in {nearSoonest} of {frames} frames; flown {flown.State} {flown.CrashReason}; the hitter {hitter!.State}; the passed {near.State} {near.CrashReason}");
        Assert.True(hitSpeed > 5000f, $"closing at only {hitSpeed:0} m/s: this shows less");
        Assert.True(nearSoonest > 0, "the near miss was never the soonest: this shows nothing");
        Assert.Equal(FlightState.Crashed, flown.State);
        Assert.StartsWith("Hit another vessel", flown.CrashReason);
        Assert.Equal(FlightState.Crashed, hitter.State);
        Assert.True(least <= wanted * 1.05f, $"the step only slowed to {least:0.0000}, the hit's sMin {wanted:0.0000}");
    }

    [Fact]
    public void A_fast_collision_slows_only_for_the_last_stretch()
    {
        var (sim, a, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 10f));
        using var _ = sim;
        var asking = new Collisions(sim.Flight);
        Collision? first = null;
        Assert.True(sim.Run(Nobody, f =>
        {
            var o = asking.Soonest(a, f.WorldVessels, Scene.CollisionHorizonSeconds, f.TimeScale);
            if (f.TimeScale < 1f)
            {
                first = o;
                return true;
            }
            Assert.Null(f.Slowed);
            if (o is { } m)
                Assert.True(m.Seconds > Slowdown.Lead(m.HitSpeed) + m.Touch, $"not slowed {m.Seconds:0.000} s from it, within T0 ({Slowdown.Lead(m.HitSpeed) + m.Touch:0.000} s)");
            return false;
        }, maxSeconds: 10), "never slowed");
        var collision = first!.Value;
        var lead = Slowdown.Lead(collision.HitSpeed) + collision.Touch; // from where their sizes touch
        output.WriteLine($"slowed at {sim.Time:0.000} s, {collision.Seconds:0.000} s from it (T0 {lead:0.000} s), to {sim.Flight.TimeScale:0.000}");
        // Within T0, and no more than a frame (of game time at full rate) past it: not late.
        Assert.True(collision.Seconds <= lead, $"slowed {collision.Seconds:0.000} s from it, over T0 ({lead:0.000} s)");
        Assert.True(collision.Seconds >= lead - Simulation.FrameSeconds, $"slowed only {collision.Seconds:0.000} s from it, over a frame inside T0 ({lead:0.000} s)");
    }

    [Fact]
    public void The_rate_keys_do_nothing_while_slowed_and_the_players_rate_comes_back()
    {
        var (sim, a, b) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 15f));
        using var _ = sim;
        Assert.True(sim.Run(Nobody, f => f.Warning is not null, maxSeconds: 15), "never warned of");
        WarpTo(sim, 4f); // by hand, after the warning
        Assert.True(sim.Run(Nobody, f => f.Slowed is not null, maxSeconds: 15), "never slowed");
        sim.Step(new FlightInput(Faster: true));
        Assert.NotNull(sim.Flight.Slowed);
        Assert.Equal(4f, sim.Flight.PlayerRate);
        Assert.True(sim.Flight.TimeScale < 1f);
        sim.Step(new FlightInput(Slower: true));
        sim.Step(new FlightInput(Slower: true));
        Assert.NotNull(sim.Flight.Slowed);
        Assert.Equal(4f, sim.Flight.PlayerRate);
        Assert.True(sim.Run(Nobody, f => f.Slowed is null, maxSeconds: 5), "never eased back");
        output.WriteLine($"at {sim.Time:0.000} s: {a.State} {a.CrashReason}; {b!.State}; rate {sim.Flight.PlayerRate}, time scale {sim.Flight.TimeScale}");
        Assert.Equal(FlightState.Crashed, a.State);
        Assert.Equal(4f, sim.Flight.PlayerRate);
        Assert.Equal(4f, sim.Flight.TimeScale);
        Assert.Equal(1f, sim.Physics.StepScale);
    }

    [Fact]
    public void A_crash_eases_back_over_the_ease()
    {
        var (sim, a, b) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 5f));
        using var _ = sim;
        Assert.True(sim.Run(Nobody, _ => a.State == FlightState.Crashed && b!.State == FlightState.Crashed, maxSeconds: 6), "they never touched");
        var crashedAt = sim.Flight.TimeScale;
        var frames = 0;
        Assert.True(sim.Run(Nobody, f =>
        {
            frames++;
            return f.TimeScale == 1f;
        }, maxSeconds: 2), "never eased back");
        output.WriteLine($"crashed at a scale of {crashedAt:0.0000}: back to 1 after {Real(frames):0.000} s of real time ({frames} frames); {a.CrashReason}");
        Assert.True(crashedAt < 0.5f, $"only slowed to {crashedAt} as they hit");
        Assert.True(Real(frames) <= Scene.SlowdownEaseSeconds + Simulation.FrameSeconds + 1e-6, $"{Real(frames):0.000} s to ease back");
        Assert.Null(sim.Flight.Slowed);
        Assert.Equal(1f, sim.Physics.StepScale);
    }

    [Fact]
    public void The_reason_shows_how_slow_and_what_is_coming()
    {
        // A dive at the ground: named by its kind, and the readout's scale the game's, each frame.
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Into("ground", 2000f, 3f));
        using (sim)
        {
            var seen = 0;
            sim.Run(Nobody, f =>
            {
                if (f.Slowed is { } s)
                {
                    seen++;
                    Assert.Equal($"a collision with the ground ({s.HitSpeed:#,0} m/s)", s.Reason);
                    Assert.Equal(f.TimeScale, s.Scale);
                    Assert.Equal($"{f.TimeScale:0.###}x, {s.Reason}", Readout.FlightReadings(f)["Slowed"]);
                    // The HUD's line shows the live scale, to the precision it is shown at.
                    var shown = Hud.SlowedLine(s);
                    var parts = System.Text.RegularExpressions.Regex.Match(shown, @"^SLOWED 1/(.+)x for (.+)$");
                    Assert.True(parts.Success, shown);
                    Assert.Equal(s.Reason, parts.Groups[2].Value);
                    var ratio = double.Parse(parts.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture);
                    var half = 1f / s.Scale < 9.95f ? 0.05 : 0.5; // a tenth under ten times slower, then whole
                    Assert.InRange(ratio, 1.0 / s.Scale - half - 1e-4, 1.0 / s.Scale + half + 1e-4);
                    Assert.Equal(shown, Hud.TimeLine(f.Slowed, f.PlayerRate, f.TimeScale, 1f)?.Text);
                }
                else
                    Assert.False(Readout.FlightReadings(f).ContainsKey("Slowed"));
                return flown.State == FlightState.Crashed && f.Slowed is null;
            }, maxSeconds: 6);
            output.WriteLine($"{seen} frames slowed for the ground; {flown.CrashReason}");
            Assert.True(seen > 60, $"only {seen} frames slowed");
        }

        // How slow, to a tenth while it is under ten times slower (a whole number there would read
        // "1/1x", as if not slowed at all), then whole.
        Assert.Equal($"SLOWED 1/{1.6:0.0}x for a collision with Booster 1", Hud.SlowedLine(new Slowdown(0.625f, 2000f, "a collision with Booster 1")));
        Assert.Equal($"SLOWED 1/{1.0:0.0}x for a collision with Booster 1", Hud.SlowedLine(new Slowdown(0.999f, 2000f, "a collision with Booster 1")));
        Assert.Equal("SLOWED 1/37x for a collision with Booster 1", Hud.SlowedLine(new Slowdown(1f / 37f, 2000f, "a collision with Booster 1")));
        // In the player's own slower slow motion, that too (the clock is the slower of the two);
        // never the rate keys' hint while slowed, as they do nothing then.
        Assert.Equal($"SLOWED 1/{1.6:0.0}x for a collision with Booster 1, in slow motion 1/4x", Hud.SlowedLine(new Slowdown(0.625f, 2000f, "a collision with Booster 1"), 0.25f));
        Assert.Equal("SLOWED 1/37x for a collision with Booster 1", Hud.SlowedLine(new Slowdown(1f / 37f, 2000f, "a collision with Booster 1"), 0.25f));
        Assert.Equal($"SLOWED 1/{1.6:0.0}x for a collision with Booster 1, in slow motion 1/4x",
            Hud.TimeLine(new Slowdown(0.625f, 2000f, "a collision with Booster 1"), 0.25f, 0.25f, 1f)?.Text);

        // A pair: a collision with the other, by its name, whether or not they would touch.
        var (pair, _, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 3f));
        using (pair)
        {
            Assert.True(pair.Run(Nobody, f => f.Slowed is not null, maxSeconds: 3), "never slowed");
            var s = pair.Flight.Slowed!.Value;
            output.WriteLine($"{Hud.SlowedLine(s)}");
            Assert.Equal($"a collision with {WorldLog.Named(other!)} ({s.HitSpeed:#,0} m/s)", s.Reason);
            Assert.InRange(s.HitSpeed, 1900f, 2100f);
        }
    }

    [Fact]
    public void A_vessel_in_a_small_view_slows_the_game_too()
    {
        // The flown one is the new rocket on the pad; the pair hit far off, one of them watched.
        using var sim = new Simulation(world: FastCollisionTests.Pair(2000f, 5000f, 5f));
        var watched = sim.Flight.WorldVessels.First(v => v.Id == 0);
        Assert.NotSame(watched, sim.Flight.Controlled);
        IReadOnlyList<Vessel> views = [watched];
        var slowed = false;
        sim.Run((_, _) => new FlightInput(Watched: views), f =>
        {
            Assert.Null(f.Warning); // watched, never warned of
            slowed |= f.Slowed is not null;
            return watched.State == FlightState.Crashed;
        }, maxSeconds: 6);
        output.WriteLine($"{watched.State} {watched.CrashReason}; slowed {slowed}");
        Assert.True(slowed, "the watched pair never slowed the game");
        Assert.Equal(FlightState.Crashed, watched.State);
    }

    [Fact]
    public void Two_vessels_near_the_flown_one_slow_the_game_in_no_small_view()
    {
        // The flown booster coasts 300 m over where a pair hits head on at 5,000 m/s, neither of
        // them in a small view: they are in the main view, near its camera (no camera given: the
        // flown vessel's, which the camera follows), so witnessed, and their collision slows the game.
        var hitPoint = FastCollisionTests.PairHitPoint(5000f);
        var pair = FastCollisionTests.Pair(5000f, 5000f, 3f).Vessels;
        var watching = FastCollisionTests.Flying(hitPoint + Planet.Up(hitPoint) * 300f, Planet.East(hitPoint), 50f, 3f, id: 2);
        var (sim, flown, a) = FastCollisionTests.Fly(new WorldState { Vessels = [watching, pair[0], pair[1]], NextVesselId = 3 });
        using var _ = sim;
        var b = sim.Flight.WorldVessels.First(v => v.Id == 1);
        var (slowed, least) = (false, 1f);
        sim.Run(Nobody, f =>
        {
            slowed |= f.Slowed is not null;
            if (a!.State != FlightState.Crashed)
                least = MathF.Min(least, sim.Physics.StepScale);
            return a.State == FlightState.Crashed && b.State == FlightState.Crashed && f.Slowed is null;
        }, maxSeconds: 6);
        var wanted = Slowdown.Least(5000f);
        output.WriteLine($"slowed {slowed}, the step's least scale {least:0.0000} (sMin {wanted:0.0000}); {a!.State} {a.CrashReason}; {b.State} {b.CrashReason}; the flown {flown.State}");
        Assert.True(slowed, "the pair near the flown vessel never slowed the game");
        Assert.StartsWith("Hit another vessel", a.CrashReason);
        Assert.StartsWith("Hit another vessel", b.CrashReason);
        Assert.NotEqual(FlightState.Crashed, flown.State);
        Assert.True(least <= wanted * 1.05f, $"the step only slowed to {least:0.0000}, sMin {wanted:0.0000}");
    }

    [Fact]
    public void The_main_views_vessels_are_predicted_in_turn_unless_one_is_near_a_collision()
    {
        // Five boosters stand frozen by the zone, the flown one among them: the other four are the
        // main view's witnessed. With none collision, each is predicted once in four steps (one a
        // step). Then a pair closing at 400 m/s hits 1.5 km over them, in 3 s: it is predicted every step,
        // and the held ones are predicted one a step in the places left.
        var field = TerrainTileTests.FrozenField(within: 1);
        Assert.Equal(5, field.Count);
        var (sim, flown, _) = FastCollisionTests.Fly(new WorldState { Vessels = field, NextVesselId = field.Count });
        using var _ = sim;
        sim.Step(default);
        sim.Step(default); // (each looked at once, new to the view)
        var others = sim.Flight.WorldVessels.Where(v => !double.IsNaN(sim.Flight.LastAsked(v))).ToArray();
        Assert.Equal(Scene.MainViewWitnessed, others.Length);
        var asked = new int[others.Length];
        for (var step = 0; step < 8; step++)
        {
            sim.Step(default);
            var now = 0;
            for (var i = 0; i < others.Length; i++)
                if (sim.Flight.LastAsked(others[i]) == sim.Flight.WorldTime)
                    (asked[i], now) = (asked[i] + 1, now + 1);
            Assert.Equal(1, now);
        }
        output.WriteLine($"asked in 8 steps, none collision: {string.Join(", ", asked)}");
        Assert.All(asked, n => Assert.Equal(2, n));

        var zone = Scene.LandingZoneCentre;
        var pair = FastCollisionTests.Pair(400f, Planet.Altitude(zone) + 1500f, 3f, over: zone).Vessels;
        var world = new WorldState { Vessels = [.. field, pair[0] with { Id = field.Count }, pair[1] with { Id = field.Count + 1 }], NextVesselId = field.Count + 2 };
        var (collision, collisionFlown, _) = FastCollisionTests.Fly(world);
        using var _2 = collision;
        var a = collision.Flight.WorldVessels.First(v => v.Id == field.Count);
        var b = collision.Flight.WorldVessels.First(v => v.Id == field.Count + 1);
        var held = collision.Flight.WorldVessels.Where(v => v != collisionFlown && v != a && v != b).ToArray();
        collision.Step(default);
        collision.Step(default);
        for (var step = 0; step < 20 && collision.Flight.Slowed is null; step++)
        {
            collision.Step(default);
            var time = collision.Flight.WorldTime;
            Assert.Equal(time, collision.Flight.LastAsked(a));
            Assert.Equal(time, collision.Flight.LastAsked(b));
            Assert.Equal(1, held.Count(v => collision.Flight.LastAsked(v) == time));
        }
    }

    [Fact]
    public void A_fast_one_out_of_view_closing_on_one_resting_in_view_is_slowed_for_in_time()
    {
        // Four boosters stand frozen on the zone, the main view's few (its camera 1.5 km east of
        // them; the flown vessel a satellite far off). A booster dives in from the west at
        // 4,000 m/s onto the middle one: out of the view (over 2 km from the camera) until half a
        // kilometre from it, so witnessed only by the one it is coming at, which, resting and
        // collision nothing, is asked in turn (its collision waits up to three steps to be found). The
        // slow-down must still come in time: down to its least scale before they collide.
        const float Speed = 4000f;
        var world = FastCollisionTests.Into("frozen", Speed, 3f);
        var zone = Scene.LandingZoneCentre;
        var east = Planet.East(zone);
        // Round it, 11 m off (TerrainTileTests' field): all but the one at its middle, where the
        // target stands, and the one nearest the camera, so the target is among the four.
        var field = TerrainTileTests.FrozenField(within: 1, firstId: 2)
            .OrderBy(s => Vector3.Distance(s.CentreOfMass.ToSingle(), zone)).Skip(1)
            .OrderBy(s => Vector3.Dot(s.CentreOfMass.ToSingle() - zone, east)).SkipLast(1)
            .Select((s, i) => s with { Id = 2 + i }).ToArray();
        var far = PerfScenes.FarSatellite(zone, 2 + field.Length, altitude: 150000f);
        using var sim = new Simulation(world: world with { Vessels = [.. world.Vessels, .. field, far], NextVesselId = far.Id + 1 });
        var flight = sim.Flight;
        var (fast, target) = (flight.WorldVessels.Single(v => v.Id == 0), flight.WorldVessels.Single(v => v.Id == 1));
        var camera = target.CentreOfMass + east * 1500f;
        var input = new FlightInput(Camera: camera);
        sim.Step(new FlightInput(SwitchTo: flight.WorldVessels.Single(v => v.Id == far.Id), Camera: camera));
        Assert.Contains(target, flight.InViewVessels.ToArray());
        Assert.Equal(Scene.MainViewWitnessed, flight.InViewVessels.Length);

        var (inViewAsSlowed, least, slowedAt) = ((bool?)null, 1f, double.NaN);
        sim.Run((_, _) => input, f =>
        {
            if (f.Slowed is not null && inViewAsSlowed is null)
                (inViewAsSlowed, slowedAt) = (f.InViewVessels.ToArray().Contains(fast), sim.Time);
            if (target.State != FlightState.Crashed)
                least = MathF.Min(least, sim.Physics.StepScale);
            return target.State == FlightState.Crashed && fast.State == FlightState.Crashed;
        }, maxSeconds: 6);
        var wanted = Slowdown.Least(Speed);
        output.WriteLine($"slowed from {slowedAt:0.000} s, the fast one in the view then: {inViewAsSlowed}; least scale {least:0.0000} (sMin {wanted:0.0000}); "
            + $"{fast.State} {fast.CrashReason}; {target.State} {target.CrashReason}");
        Assert.False(inViewAsSlowed ?? true, "it was never slowed for, or only once the fast one was in the view itself: this shows nothing");
        Assert.True(least <= wanted * 1.05f, $"the step only slowed to {least:0.0000} before they hit, sMin {wanted:0.0000}");
        Assert.StartsWith("Hit another vessel", target.CrashReason);
        Assert.StartsWith("Hit another vessel", fast.CrashReason);
    }

    [Fact]
    public void A_frozen_crowd_near_the_camera_does_not_keep_a_fast_pair_from_being_witnessed()
    {
        // The flown booster stands frozen among 48 others on the zone (TerrainTileTests' field),
        // all nearer the camera than a pair colliding head on at 5,000 m/s 1.5 km over it. The
        // crowd can hit nothing fast by itself, so it must not fill the main view's few places
        // (Scene.MainViewWitnessed): the moving pair is witnessed first, and slows the game.
        var field = TerrainTileTests.FrozenField(within: 4);
        var zone = Scene.LandingZoneCentre;
        var pair = FastCollisionTests.Pair(5000f, Planet.Altitude(zone) + 1500f, 3f, over: zone).Vessels;
        var world = new WorldState { Vessels = [.. field, pair[0] with { Id = field.Count }, pair[1] with { Id = field.Count + 1 }], NextVesselId = field.Count + 2 };
        var (sim, flown, _) = FastCollisionTests.Fly(world);
        using var _ = sim;
        var a = sim.Flight.WorldVessels.First(v => v.Id == field.Count);
        var b = sim.Flight.WorldVessels.First(v => v.Id == field.Count + 1);
        var nearCamera = sim.Flight.WorldVessels.Count(v => v != flown && Vector3.Distance(v.CentreOfMass, flown.CentreOfMass) < 100f);
        var (slowed, least) = (false, 1f);
        sim.Run(Nobody, f =>
        {
            slowed |= f.Slowed is not null;
            if (a.State != FlightState.Crashed)
                least = MathF.Min(least, sim.Physics.StepScale);
            return a.State == FlightState.Crashed && b.State == FlightState.Crashed && f.Slowed is null;
        }, maxSeconds: 6);
        output.WriteLine($"{nearCamera} frozen within 100 m of the flown one; slowed {slowed}, the step's least scale {least:0.0000} (sMin {Slowdown.Least(5000f):0.0000}); {a.CrashReason}; {b.CrashReason}");
        Assert.True(nearCamera > Scene.MainViewWitnessed, "the crowd can't fill the main view's places: this shows nothing");
        Assert.True(flown.Craft.Frozen || flown.State == FlightState.Landed);
        Assert.True(slowed, "the pair over the crowd never slowed the game");
        Assert.StartsWith("Hit another vessel", a.CrashReason);
        Assert.StartsWith("Hit another vessel", b.CrashReason);
    }

    [Fact]
    public void Switching_away_mid_slow_down_eases_back()
    {
        var (sim, a, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 5f));
        using var _ = sim;
        var far = sim.Flight.WorldVessels.Single(v => v.State == FlightState.Ready); // the new rocket on Home's pad
        Assert.NotSame(a, far);
        Assert.True(sim.Run(Nobody, f => f.TimeScale < 0.5f, maxSeconds: 6), "never slowed");
        output.WriteLine($"{Vector3.Distance(far.Position, a.Position):0} m apart; switched away at a scale of {sim.Flight.TimeScale:0.000}");
        Assert.True(Vector3.Distance(far.Position, a.Position) > 5000f);
        // The vessel switched to is flown a frame after the switch is asked (behind its link-up): until
        // then the slow-down for the one switched away from holds, and it is let go only once the
        // other is flown, from when it eases back.
        sim.ShowLinkUp = true;
        sim.Step(new FlightInput(SwitchTo: far));
        Assert.Same(a, sim.Flight.Controlled);
        Assert.NotNull(sim.Flight.Slowed);
        sim.Step(default);
        Assert.Same(far, sim.Flight.Controlled);
        var frames = 1;
        Assert.True(sim.Run(Nobody, f =>
        {
            if (f.Slowed is null)
                return true;
            frames++;
            return false;
        }, maxSeconds: Scene.SlowdownEaseSeconds + 1f), "never eased back");
        output.WriteLine($"eased back {Real(frames):0.000} s of real time after the vessel was flown");
        Assert.True(Real(frames) <= Scene.SlowdownEaseSeconds + Simulation.FrameSeconds + 1e-6, $"{Real(frames):0.000} s to ease back");
        Assert.Equal(1f, sim.Flight.TimeScale);
    }

    [Fact]
    public void A_pause_mid_slow_down_changes_nothing()
    {
        var (sim, a, b) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 5f));
        using var _ = sim;
        Assert.True(sim.Run(Nobody, f => f.TimeScale < 0.5f, maxSeconds: 6), "never slowed");
        var (scale, slowed, time) = (sim.Flight.TimeScale, sim.Flight.Slowed, sim.Flight.WorldTime);
        for (var i = 0; i < 60; i++)
        {
            sim.Flight.Hold();
            Assert.Equal(scale, sim.Flight.TimeScale);
            Assert.Equal(slowed, sim.Flight.Slowed);
            Assert.Equal(time, sim.Flight.WorldTime);
        }
        sim.Step(default);
        Assert.True(sim.Flight.WorldTime > time);
        Assert.NotNull(sim.Flight.Slowed);
        Assert.True(sim.Flight.TimeScale <= scale);
        Assert.True(sim.Run(Nobody, _ => a.State == FlightState.Crashed && b!.State == FlightState.Crashed, maxSeconds: 2), "they never touched");
        output.WriteLine($"{a.CrashReason}, at a scale of {sim.Flight.TimeScale:0.0000}");
    }

    [PlaythroughFact(Long = false)]
    [Trait("Kind", "Playthrough")]
    public void A_powered_landing_is_never_slowed()
    {
        // The landing burn slows it short of any fast collision: no warning and no slow-down, ever.
        using var sim = new Simulation();
        var pilot = new Autopilot();
        Assert.True(sim.RunToEnd((f, dt) =>
        {
            Assert.Null(f.Slowed);
            Assert.Null(f.Warning);
            Assert.Equal(1f, f.TimeScale);
            return pilot.Fly(f, dt);
        }), "the flight never ended");
        Assert.Equal(FlightState.Landed, sim.Flight.State);
        Assert.Null(sim.Flight.Slowed);
    }

    [Fact]
    public void A_fast_craft_wakes_a_frozen_one_in_time()
    {
        // A craft at the ground's top speed, 10 km off a booster frozen on the zone: woken by the time the
        // craft is its wake's reach away (its speed's Scene.WakeSeconds), so it is awake well
        // before it is hit.
        var (sim, flier, standing) = FastCollisionTests.Fly(FastCollisionTests.Into("frozen", Scene.GroundTopSpeed, 10000f / Scene.GroundTopSpeed));
        using var _ = sim;
        Assert.True(standing!.Craft.Frozen, "the standing one isn't frozen: this shows nothing");
        var reach = Scene.GroundTopSpeed * Scene.WakeSeconds;
        var (wokenAt, frozenBefore) = ((float?)null, false);
        sim.Run(Nobody, _ =>
        {
            var apart = Vector3.Distance(flier.CentreOfMass, standing.CentreOfMass);
            if (!standing.Craft.Frozen)
                wokenAt ??= apart;
            if (flier.State != FlightState.Crashed)
                frozenBefore = standing.Craft.Frozen;
            return flier.State == FlightState.Crashed;
        }, maxSeconds: 4);
        output.WriteLine($"woken {wokenAt:0} m off (its reach {reach:0} m); {flier.CrashReason}; standing {standing.State} {standing.CrashReason}");
        Assert.Equal(FlightState.Crashed, flier.State);
        Assert.False(frozenBefore, "still frozen the frame before it was hit");
        Assert.True(wokenAt >= reach * 0.9f, $"woken only {wokenAt:0} m off");
        Assert.Equal(FlightState.Crashed, standing.State);
    }

    [Fact]
    public void A_fast_craft_wakes_nothing_beside_or_behind_it()
    {
        // At the ground's top speed, level 1 km over a booster frozen on the zone: beside it as it passes
        // and behind it after, within the 1 km a sphere round it would reach (its speed's
        // Scene.WakeSeconds), but never within Scene.WakeMargin of its body's way ahead. It
        // wakes only what it is coming at, so the frozen one sleeps through.
        var (sim, flier, standing) = FastCollisionTests.Fly(FastCollisionTests.Over(Scene.GroundTopSpeed, 1f, 1000f));
        using var _ = sim;
        Assert.True(standing!.Craft.Frozen, "the standing one isn't frozen: this shows nothing");
        var (nearest, woken) = (float.MaxValue, false);
        // Watched, so it stays in the physics, frozen, as the flier draws off (past the main view's
        // reach, nobody watching it, it would leave the physics: see Unwatched).
        var watched = new[] { standing };
        sim.Run((_, _) => new FlightInput(Watched: watched), _ =>
        {
            Assert.False(standing.OnFormula);
            nearest = MathF.Min(nearest, Vector3.Distance(flier.CentreOfMass, standing.CentreOfMass));
            woken |= !standing.Craft.Frozen;
            return false;
        }, maxSeconds: 1.5);
        var apart = Vector3.Distance(flier.CentreOfMass, standing.CentreOfMass);
        output.WriteLine($"passed {nearest:0} m from it, {apart:0} m off at the end (a sphere's reach {Scene.GroundTopSpeed * Scene.WakeSeconds:0} m); woken {woken}; {flier.State}");
        Assert.InRange(nearest, 900f, 1100f);
        Assert.Equal(FlightState.Flying, flier.State);
        Assert.False(woken, "woken by a craft passing beside or behind it");
    }

    [Fact]
    public void A_wreck_lays_no_new_tiles()
    {
        // Into the rough slope at the ground's top speed, 10° below level: the hit throws the wreck along a
        // new way, and while it is left in the world (Scene.WreckSeconds, slowed: seconds of real
        // time) the tiles there are kept, but none is made for that new way (a kilometre's urgent
        // stretch at once, which cost a frame 12 ms).
        var (sim, flown, _) = FastCollisionTests.Fly(new WorldState { Vessels = [Drops.Crashing(Scene.GroundTopSpeed, 10f).State], NextVesselId = 1 });
        using var _ = sim;
        Assert.True(sim.Run(Nobody, _ => flown.State == FlightState.Crashed, maxSeconds: 4), "it never crashed");
        var (made, fromWorker, tiles) = (sim.Physics.Tiles.MadeInStep, sim.Physics.Tiles.FromWorker, sim.Physics.TileCount);
        Assert.True(sim.Run(Nobody, _ => flown.Craft.Removed, maxSeconds: 2), "the wreck was never taken out");
        output.WriteLine($"{flown.CrashReason}: {sim.Physics.Tiles.MadeInStep - made} tiles made in the step and {sim.Physics.Tiles.FromWorker - fromWorker} laid from the worker's while it was a wreck ({tiles} in the world as it crashed)");
        Assert.Equal(made, sim.Physics.Tiles.MadeInStep);
    }

    [Theory]
    [InlineData(2000f)]
    [InlineData(4000f)]
    public void A_dive_through_the_air_is_slowed_and_eases_back(float speed)
    {
        // The ground's prediction leaves out the air, so in the air it reads the collision early:
        // the slow-down starts sooner, and must still end.
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Into("ground", speed, 3f));
        using var _ = sim;
        var (slowedFrames, least) = (0, 1f);
        sim.Run(Nobody, f =>
        {
            if (f.TimeScale < 1f)
                slowedFrames++;
            least = MathF.Min(least, f.TimeScale);
            return flown.State == FlightState.Crashed && f.Slowed is null;
        }, maxSeconds: 6);
        output.WriteLine($"diving at {speed} m/s through the air ({Planet.AirDensity(Planet.Altitude(flown.CrashPosition)) * 100:0} % air at the ground): "
            + $"slowed {Real(slowedFrames):0.00} s of real time, least scale {least:0.0000}; {flown.CrashReason}");
        Assert.StartsWith("Hit the ground", flown.CrashReason);
        Assert.True(least < 0.5f, $"only slowed to {least}");
    }

    [Fact]
    public void A_fast_low_pass_at_rising_ground_hits_it()
    {
        // About 1,000 m/s, level, a few tens of metres over the ground and climbing ground ahead:
        // the ground's prediction (no air) has it higher and faster than the air leaves it, so
        // this is where it could read a ridge as cleared. It must hit it, never pass through.
        var (at, downhill, degrees) = Drops.FindSlope(8f, 30f);
        var up = Planet.Up(at);
        var uphill = -Vector3.Normalize(downhill - up * Vector3.Dot(downhill, up));
        // Its middle aimed 4 m over the slope, a third of a second's flight on: it sets off
        // about 330 m back down it, a few tens of metres up.
        var world = new WorldState { Vessels = [FastCollisionTests.Flying(at + up * 4f, uphill, 1000f, 1f / 3f, 0)], NextVesselId = 1 };
        var (sim, flown, _) = FastCollisionTests.Fly(world);
        using var _ = sim;
        var startedUp = flown.Ground;
        // Its middle's own path, a point, clears the slope (its body grazes it): only its size makes it a collision.
        var point = Orbit.OverGround(GroundBody.Planet, flown.CentreOfMass, flown.Velocity);
        Assert.True(!point.EndsOnGround || point.SecondsToEnd > 1.0, $"a point's path hits the slope too ({point.SecondsToEnd:0.000} s): this shows less");
        var (slowedFrames, least, lastGround) = (0, 1f, float.MaxValue);
        string? reason = null;
        sim.Run(Nobody, f =>
        {
            if (f.Slowed is { } s)
            {
                slowedFrames++;
                reason = s.Reason;
            }
            least = MathF.Min(least, f.TimeScale);
            if (flown.State != FlightState.Crashed)
                lastGround = Planet.GroundAltitude(flown.CentreOfMass);
            return flown.State == FlightState.Crashed && f.Slowed is null;
        }, maxSeconds: 3);
        output.WriteLine($"a {degrees:0.0}° slope, set off {startedUp:0} m up: slowed {Real(slowedFrames):0.00} s of real time, least {least:0.0000}, for {reason}; "
            + $"{flown.CrashReason}, its middle {lastGround:0.0} m over the ground the frame before");
        Assert.InRange(startedUp, 10f, 100f);
        Assert.Equal(FlightState.Crashed, flown.State);
        Assert.StartsWith("Hit the ground", flown.CrashReason);
        Assert.True(lastGround > -1f, $"its middle was {lastGround:0.0} m under the ground: through it");
        Assert.Contains("the ground", reason);
        Assert.True(least < 0.5f, $"only slowed to {least}");
    }

    [Fact]
    public void A_craft_on_the_drone_ship_rides_a_slow_down_without_a_hitch()
    {
        // The booster stands on the deck, flown, as a pair hits far off, watched: the step's length
        // changes as the slow-down begins and ends, and the deck (a kinematic body moved a step at a
        // time) mustn't jump under it as it does.
        var ship = Rafts.Ship;
        ship.Set(0.0);
        var deck = ship.Current.Position + ship.Normal * Scene.DroneShipFreeboard;
        var deckSpeed = ship.VelocityAt(deck);
        var deckUp = Planet.Up(deck);
        var booster = Drops.Placed(deck, Planet.East(deck), -Vector3.Dot(deckSpeed, deckUp), Vehicle.TwoStage,
            deckSpeed - deckUp * Vector3.Dot(deckSpeed, deckUp), first: 0, last: 0, mission: Mission.ReachOrbit, flight: 1, clearance: 0.01f);
        // The pair's slowing begins 1.1 s in, before the booster settles on the deck and freezes.
        var pair = FastCollisionTests.Pair(2000f, 5000f, 1.1f + (float)Slowdown.Lead(2000f)).Vessels;
        var world = new WorldState { Vessels = [booster, pair[0] with { Id = 1 }, pair[1] with { Id = 2 }], NextVesselId = 3 };
        using var sim = new Simulation(Mission.ReachOrbit, world: world);
        var flown = sim.Flight.WorldVessels.First(v => v.Id == 0);
        IReadOnlyList<Vessel> views = [sim.Flight.WorldVessels.First(v => v.Id == 1)];
        sim.Step(new FlightInput(SwitchTo: flown, Watched: views));
        Vector3 Against() => flown.Velocity - ship.VelocityAt(flown.CentreOfMass);
        var steady = 0f;
        var changing = 0f;
        var (began, ended) = (false, false);
        var wasSlowed = false;
        sim.Run((_, _) => new FlightInput(Watched: views), f =>
        {
            var slowed = f.Slowed is not null;
            var against = Against().Length();
            if (slowed != wasSlowed)
                (began, ended) = (began || slowed, ended || !slowed);
            if (flown.State == FlightState.Landed)
            {
                // The frames either side of a change of the step's length, against the rest of its stand.
                if (slowed != wasSlowed || (slowed && f.Slowed!.Value.Scale > 0.9f))
                    changing = MathF.Max(changing, against);
                else if (!slowed && f.WorldTime > 0.5)
                    steady = MathF.Max(steady, against);
            }
            wasSlowed = slowed;
            return ended;
        }, maxSeconds: 5);
        output.WriteLine($"{flown.State} {flown.CrashReason}, frozen {flown.Craft.Frozen}: against the deck at most {steady:0.0000} m/s standing, "
            + $"{changing:0.0000} m/s as the step changed; slowed {began}, back {ended}; {ship.HeightAbove(flown.Position):0.000} m over the deck");
        Assert.True(began && ended, "the step never changed");
        Assert.Equal(FlightState.Landed, flown.State);
        Assert.False(flown.Craft.Frozen, "frozen, it is carried: this shows nothing");
        Assert.True(changing <= MathF.Max(2f * steady, 0.05f), $"{changing:0.0000} m/s against the deck as the step changed, {steady:0.0000} standing");
        Assert.InRange(ship.HeightAbove(flown.Position)!.Value, -0.5f, 3f);
    }

    [Fact]
    public void Looking_ahead_mid_slow_down_allocates_nothing()
    {
        var (sim, _, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 5f));
        using var _ = sim;
        IReadOnlyList<Vessel> views = [other!];
        Assert.True(sim.Run((_, _) => new FlightInput(Watched: views), f => f.TimeScale < 0.5f, maxSeconds: 6), "never slowed");
        sim.Flight.LookAhead(views); // (anything worked out once, first)
        var scale = sim.Flight.TimeScale;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 120; i++)
            sim.Flight.LookAhead(views);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"looking ahead 120 times mid-slow-down: {bytes} bytes; the scale {scale} then {sim.Flight.TimeScale}");
        Assert.Equal(0, bytes);
        Assert.Equal(scale, sim.Flight.TimeScale); // asked again at the same time, nothing changes
    }

    [Theory]
    [InlineData("floe")]
    [InlineData("pair")]
    public void In_the_players_own_slow_motion_a_fast_collision_still_shortens_the_step(string what)
    {
        // At a quarter of normal speed by hand, the game's clock is already slower than a collision
        // would slow it to (the floe's least at 500 m/s is 0.43), but the step must still be
        // shortened for it: the clock is the slower of the two, the step the collision's.
        var world = what == "floe" ? FastCollisionTests.Into("floe", 500f, 3f) : FastCollisionTests.Pair(2000f, 5000f, 3f);
        var (sim, flown, other) = FastCollisionTests.Fly(world);
        using var _ = sim;
        sim.Step(new FlightInput(Slower: true));
        sim.Step(new FlightInput(Slower: true));
        Assert.Equal(0.25f, sim.Flight.PlayerRate);
        var frames = new List<(double Time, float Travel, float Speed)>();
        var slowedLines = 0;
        Assert.True(sim.Run(Nobody, f =>
        {
            Assert.True(f.TimeScale <= 0.25f, $"the clock at {f.TimeScale}, quicker than the player's own");
            // The HUD's time line: slowed, it says so and why (with the player's own rate while
            // that is the slower), never the rate keys' hint, as the keys do nothing then.
            var line = Hud.TimeLine(f.Slowed, f.PlayerRate, f.TimeScale, 1f);
            if (f.Slowed is { } s)
            {
                slowedLines++;
                Assert.Equal(Hud.SlowedLine(s, f.PlayerRate), line?.Text);
                Assert.DoesNotContain("( , and . )", line?.Text);
                if (f.PlayerRate < s.Scale)
                    Assert.EndsWith($"for {s.Reason}, in slow motion 1/4x", line?.Text);
            }
            else
                Assert.Equal("SLOW MOTION  1/4x   ( , and . )", line?.Text);
            var speed = (other is null ? flown.Velocity : other.Velocity - flown.Velocity).Length();
            if (flown.State != FlightState.Crashed)
                frames.Add((f.WorldTime, speed * sim.Physics.StepLength, speed));
            return flown.State == FlightState.Crashed;
        }, maxSeconds: 6), "it never touched it");
        var metAt = frames[^1].Time;
        var collision = frames[^1].Speed;
        // Over the last half of its hold the step is at its shortest for it (Slowdown.Hold of game
        // time before the collision as predicted). Half, as the crash comes before that (the bodies
        // touch before their middles' closest approach, and the ground's hit speed is predicted
        // without the air, so its stretch is shorter).
        var stretch = Slowdown.Hold(collision) / 2.0;
        var travel = frames.Where(x => metAt - x.Time <= stretch).Max(x => x.Travel);
        output.WriteLine($"{what} at a quarter speed: {flown.CrashReason}; at most {travel:0.000} m a step over the last {stretch:0.000} s of game time; "
            + $"the rate {sim.Flight.PlayerRate}, the clock {sim.Flight.TimeScale}");
        Assert.StartsWith(what == "floe" ? "Hit the ground" : "Hit another vessel", flown.CrashReason);
        Assert.True(travel <= Scene.FastCollisionStep * 1.05f, $"{travel:0.000} m a step as it hit");
        Assert.Equal(0.25f, sim.Flight.PlayerRate);
        Assert.True(slowedLines > 0, "never slowed: the time line's check shows nothing");
    }

    [Fact]
    public void A_warp_dropped_by_a_warning_during_a_slow_down_stays_dropped()
    {
        // A watched pair (8 km up) slows the game; meanwhile the flown vessel's own collision (5 km
        // up, later) is warned of, which drops the warp: once the watched pair's slow-down is
        // over, the game runs at normal speed, not at the warp it had before.
        var flownPair = FastCollisionTests.Pair(1000f, 5000f, 11.5f).Vessels;
        var watchedPair = FastCollisionTests.Pair(500f, 8000f, 4f).Vessels;
        var world = new WorldState
        {
            Vessels = [flownPair[0], flownPair[1], watchedPair[0] with { Id = 2 }, watchedPair[1] with { Id = 3 }], NextVesselId = 4,
        };
        var (sim, _, _) = FastCollisionTests.Fly(world);
        using var _ = sim;
        var watched = sim.Flight.WorldVessels.First(v => v.Id == 2);
        IReadOnlyList<Vessel> views = [watched];
        WarpTo(sim, 4f);
        Pilot watching = (_, _) => new FlightInput(Watched: views);
        Assert.True(sim.Run(watching, f => f.Slowed is not null, maxSeconds: 4), "the watched pair never slowed the game");
        Assert.Null(sim.Flight.Warning);
        Assert.True(sim.Run(watching, f => f.Warning is not null, maxSeconds: 4), "never warned of");
        output.WriteLine($"warned at {sim.Time:0.000} s while slowed ({sim.Flight.Slowed}), the rate {sim.Flight.PlayerRate}");
        Assert.NotNull(sim.Flight.Slowed);
        Assert.Equal(1f, sim.Flight.PlayerRate);
        Assert.True(sim.Run(watching, f => f.Slowed is null, maxSeconds: 4), "never eased back");
        output.WriteLine($"eased back at {sim.Time:0.000} s: {watched.State} {watched.CrashReason}; the rate {sim.Flight.PlayerRate}, the clock {sim.Flight.TimeScale}");
        Assert.Equal(1f, sim.Flight.PlayerRate);
        Assert.Equal(1f, sim.Flight.TimeScale);
    }

    [Fact]
    public void A_new_flight_mid_slow_down_ends_it()
    {
        var (sim, _, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 5f));
        using var _ = sim;
        WarpTo(sim, 2f);
        Assert.True(sim.Run(Nobody, f => f.TimeScale < 0.5f, maxSeconds: 6), "never slowed");
        sim.Step(new FlightInput(Choose: Mission.LaunchAndLand));
        output.WriteLine($"a new flight: slowed {sim.Flight.Slowed}, the rate {sim.Flight.PlayerRate}, the clock {sim.Flight.TimeScale}, the step's scale {sim.Physics.StepScale}");
        Assert.Null(sim.Flight.Slowed);
        Assert.Equal(2f, sim.Flight.PlayerRate);
        Assert.Equal(2f, sim.Flight.TimeScale);
        Assert.Equal(1f, sim.Physics.StepScale);
    }

    [Fact]
    public void A_slow_down_begun_at_half_speed_ends_at_half_speed()
    {
        var (sim, a, b) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 5f));
        using var _ = sim;
        sim.Step(new FlightInput(Slower: true));
        Assert.Equal(0.5f, sim.Flight.PlayerRate);
        Assert.True(sim.Run(Nobody, f => f.Slowed is not null, maxSeconds: 6), "never slowed");
        var began = sim.Flight.Slowed!.Value;
        // The step shortens as soon as the collision asks, though the clock is still the player's.
        output.WriteLine($"began at a step's scale of {began.Scale:0.000}, the clock {sim.Flight.TimeScale}");
        Assert.True(began.Scale > 0.5f, $"began only at {began.Scale}");
        Assert.Equal(0.5f, sim.Flight.TimeScale);
        Assert.True(sim.Physics.StepScale < 1f);
        Assert.True(sim.Run(Nobody, f => a.State == FlightState.Crashed && b!.State == FlightState.Crashed && f.Slowed is null, maxSeconds: 6), "never ended");
        output.WriteLine($"ended: the rate {sim.Flight.PlayerRate}, the clock {sim.Flight.TimeScale}, the step's scale {sim.Physics.StepScale}");
        Assert.Equal(0.5f, sim.Flight.PlayerRate);
        Assert.Equal(0.5f, sim.Flight.TimeScale);
        Assert.Equal(1f, sim.Physics.StepScale);
    }
}
