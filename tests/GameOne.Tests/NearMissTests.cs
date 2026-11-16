using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Near misses predicted (<see cref="Collisions.NearestMiss"/>): another vessel passing within one
/// of the watched vessel's lengths, outline to outline, without touching, and faster than
/// <see cref="Scene.SlowdownPassSpeed"/>; and the eased scale that slows it, from
/// <see cref="Scene.SlowdownFrom"/> out, to pass at that speed to the eye
/// (<see cref="NearMiss.ScaleAt"/>). The worlds
/// are <see cref="FastCollisionTests"/>' builders: two boosters head on, their middles passing
/// <c>sideways</c> apart 5 s after the start.
/// </summary>
public sealed class NearMissTests(ITestOutputHelper output)
{
    /// <summary>Wider apart than both size-spheres (so no collision), but well within a length outline to outline.</summary>
    private static readonly float Beside = 2f * FastCollisionTests.BoosterSize + 1f;

    private static NearMiss? Nearest(Simulation sim, Vessel watched) =>
        new Collisions(sim.Flight).NearestMiss(watched, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds);

    [Fact]
    public void A_pass_within_a_length_fast_enough_is_a_near_miss()
    {
        Assert.True(Beside - 2f * FastCollisionTests.BoosterWidest < FastCollisionTests.BoosterHeight);
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: Beside));
        using var _ = sim;
        var m = Nearest(sim, flown);
        output.WriteLine($"{m}, scale {(m is { } n ? NearMiss.Scale(n) : float.NaN)}");
        Assert.NotNull(m);
        Assert.Same(flown, m.Value.Watched);
        Assert.Same(other, m.Value.Other);
        Assert.InRange(m.Value.Seconds, 4.8, 5.2);
        Assert.InRange(m.Value.Gap, 0f, FastCollisionTests.BoosterHeight);
        Assert.InRange(m.Value.PassSpeed, 950f, 1050f);
        Assert.InRange(m.Value.FromSeconds, Scene.SlowdownFrom / 1025f, Scene.SlowdownFrom / 975f); // √(SlowdownFrom² − sideways²) / 1000: its way in
        Assert.InRange(NearMiss.Scale(m.Value), Scene.SlowdownPassSpeed / 1050f, Scene.SlowdownPassSpeed / 950f); // 1,000 m/s passing at SlowdownPassSpeed to the eye
    }

    [Fact]
    public void A_pass_just_gone_is_kept_while_its_slowing_eases_back()
    {
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: Beside));
        using var _ = sim;
        sim.Run((_, _) => default, _ => sim.Time > 5.2, maxSeconds: 6);
        var m = Nearest(sim, flown);
        output.WriteLine($"at {sim.Time:0.000} s: {m}");
        Assert.NotNull(m);
        Assert.Same(other, m.Value.Other);
        Assert.InRange(m.Value.Seconds, -0.4, 0.0);
        Assert.True(m.Value.ScaleAt(m.Value.Seconds) < 1f);
        sim.Run((_, _) => default, _ => sim.Time > 7.5, maxSeconds: 3); // well past: eased back
        Assert.Null(Nearest(sim, flown));
    }

    [Fact]
    public void The_next_pass_easing_in_wins_over_one_just_gone_easing_back()
    {
        // Pair's two pass at 5 s; a third, on the second's line and faster (700 m/s, closing at
        // 1,200 m/s), passes at about 6.2 s, its slowing beginning SlowdownFrom out while the
        // first's still eases back from its pass. Frame by frame (so it holds whatever the tuning,
        // so long as the two overlap): just after the first's pass it is the deeper; then, while
        // it still eases back, the third comes to ask the lower scale and is the one, though not
        // the soonest.
        var world = FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: Beside);
        var hitPoint = FastCollisionTests.PairHitPoint(5000f);
        var east = Planet.East(hitPoint);
        var across = Vector3.Normalize(Vector3.Cross(Planet.Up(hitPoint), 2f * east)) * (Beside / 2f);
        var third = FastCollisionTests.Flying(hitPoint + across, -east, 700f, 7f, 2);
        var (sim, flown, first) = FastCollisionTests.Fly(world with { Vessels = [.. world.Vessels, third], NextVesselId = 3 });
        using var _ = sim;
        var next = sim.Flight.WorldVessels.First(v => v.Id == 2);
        sim.Run((_, _) => default, _ => sim.Time > 5.0, maxSeconds: 6);
        var (firstDeeper, nextDeeper) = (0, 0);
        var firstAlone = new List<Vessel> { flown, first! };
        sim.Run((_, _) => default, _ =>
        {
            var m = Nearest(sim, flown);
            var firstOwn = new Collisions(sim.Flight).NearestMiss(flown, firstAlone, Scene.CollisionHorizonSeconds);
            if (m is { } d && d.Other == first && d.Seconds < 0.0)
                firstDeeper++;
            if (m is { } n && n.Other == next && firstOwn is { Seconds: < 0.0 } gone && gone.ScaleAt(gone.Seconds) < 1f)
            {
                Assert.True(n.Seconds > 0.0, "the third, still coming");
                Assert.True(n.ScaleAt(n.Seconds) <= gone.ScaleAt(gone.Seconds), "the third the deeper");
                nextDeeper++;
            }
            return sim.Time > 6.1;
        }, maxSeconds: 2);
        output.WriteLine($"frames with the first gone the deeper: {firstDeeper}; with the third the one while the first eased back: {nextDeeper}");
        Assert.True(firstDeeper > 0, "the first, just gone, was never the deeper");
        Assert.True(nextDeeper > 0, "the third never took over while the first eased back");
    }

    [Fact]
    public void A_pass_inside_each_others_size_spheres_that_misses_is_a_near_miss()
    {
        // Side by side, head on, their middles 3 m further apart than their widths: their
        // size-spheres overlap (a collision's reach), but their outlines never touch.
        var sideways = 2f * FastCollisionTests.BoosterWidest + 3f;
        Assert.True(sideways < 2f * FastCollisionTests.BoosterSize);
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: sideways));
        using var _ = sim;
        var m = Nearest(sim, flown);
        output.WriteLine($"{m}");
        Assert.NotNull(m);
        Assert.Same(other, m.Value.Other);
        Assert.InRange(m.Value.Seconds, 4.8, 5.2);
        Assert.InRange(m.Value.Gap, 2f, 4f);
    }

    [Fact]
    public void A_slow_pass_is_no_near_miss()
    {
        // Passing slower than NearMissSlowAbove: seen without slowing.
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(0.8f * Scene.NearMissSlowAbove, 5000f, 5f, sideways: Beside));
        using var _ = sim;
        Assert.Null(Nearest(sim, flown));
    }

    [Fact]
    public void A_pass_just_faster_than_the_trigger_is_a_near_miss()
    {
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1.2f * Scene.NearMissSlowAbove, 5000f, 5f, sideways: Beside));
        using var _ = sim;
        var m = Nearest(sim, flown);
        Assert.NotNull(m);
        Assert.Same(other, m.Value.Other);
        Assert.True(NearMiss.Scale(m.Value) < 1f);
    }

    [Fact]
    public void A_pass_further_than_a_length_is_no_near_miss()
    {
        var sideways = FastCollisionTests.BoosterHeight + 2f * FastCollisionTests.BoosterWidest + 10f;
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: sideways));
        using var _ = sim;
        Assert.Null(Nearest(sim, flown));
    }

    [Fact]
    public void A_touch_is_a_collision_not_a_near_miss()
    {
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: 0f));
        using var _ = sim;
        Assert.Null(Nearest(sim, flown));
    }

    [Theory]
    [InlineData("ground", 45f)]
    [InlineData("tower", 30f)] // (flown level, its way to the tower hits the ground first)
    [InlineData("mast", 0f)]
    public void The_ground_and_the_towers_are_never_near_misses(string what, float diveDegrees)
    {
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Into(what, 1000f, 5f, diveDegrees));
        using var _ = sim;
        Assert.NotNull(new Collisions(sim.Flight).Soonest(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds)); // it is coming at it
        Assert.Null(Nearest(sim, flown));
    }

    [Fact]
    public void Predicting_near_misses_allocates_nothing()
    {
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: Beside));
        using var _ = sim;
        var collisions = new Collisions(sim.Flight);
        Assert.NotNull(collisions.NearestMiss(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds)); // (anything worked out once, first)
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            collisions.NearestMiss(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void The_near_miss_slowing_eases_in_holds_and_eases_back()
    {
        // From where it begins, the scale falls evenly with the distance (a straight line in game
        // seconds) to its deepest SlowdownHoldWithin short of the closest approach, no speed held on
        // the way; it holds that to as far past, then eases back over SlowdownEaseSeconds.
        var m = new NearMiss(null!, null!, 5.0, 10f, 1000f, 2f);
        var s = NearMiss.Scale(m);
        Assert.Equal(Scene.SlowdownPassSpeed / 1000f, s, 1e-4f);
        var hold = Scene.SlowdownHoldWithin / 1000f; // game seconds within SlowdownHoldWithin, each side
        Assert.Equal(1f, m.ScaleAt(m.FromSeconds), 1e-4f);
        Assert.Equal((1f + s) / 2f, m.ScaleAt((m.FromSeconds + hold) / 2f), 1e-4f); // halfway in, halfway down
        Assert.Equal(s, m.ScaleAt(hold), 1e-4f); // at its slowest from there ...
        Assert.True(m.ScaleAt(hold + 0.01) > s, "slowing still, before");
        Assert.Equal(s, m.ScaleAt(0.0), 1e-4f);
        Assert.Equal(s, m.ScaleAt(-hold), 1e-4f); // ... till as far past
        Assert.True(m.ScaleAt(-hold - 0.01) > s, "eased back after");
        Assert.Equal(1f, m.ScaleAt(-hold - (1f - s) / (MathF.Log(1f / s) / Scene.SlowdownEaseSeconds)), 1e-4f);
        Assert.Equal(1f, m.ScaleAt(10.0));
        // Passing no faster than that: nothing to slow, the player's rate, never a stopped clock.
        Assert.Equal(1f, (m with { PassSpeed = Scene.SlowdownPassSpeed }).ScaleAt(0.0));
    }

    // ---- The setting: the near miss's slow-down and its warning (Flight.Slowed, Flight.NearMissWarning) ----

    private static readonly Pilot Nobody = (_, _) => default;
    private static readonly Pilot Slowing = (_, _) => new FlightInput(NearMisses: true);

    /// <summary>The near-miss world: Pair's two head on at 1,000 m/s, passing 5 s from the start beside each other (<see cref="Beside"/>), within a length but never within each other's sizes.</summary>
    internal static WorldState NearMissPass(float closing, float seconds = 5f) => FastCollisionTests.Pair(closing, 5000f, seconds, sideways: Beside);

    /// <summary>Real seconds of <paramref name="frames"/> frames.</summary>
    private static double Real(int frames) => frames * (double)Simulation.FrameSeconds;

    /// <summary>The game's scale each frame: the slow-down's, or 1.</summary>
    private static float ScaleOf(Flight f) => f.Slowed?.Scale ?? 1f;

    /// <summary>The largest frame-to-frame change of scale in <paramref name="scales"/>, as a ratio (1 for none).</summary>
    private static float Jump(IReadOnlyList<float> scales)
    {
        var most = 1f;
        for (var i = 1; i < scales.Count; i++)
            most = MathF.Max(most, MathF.Max(scales[i] / scales[i - 1], scales[i - 1] / scales[i]));
        return most;
    }

    [Fact]
    public void With_the_setting_off_a_near_miss_never_slows_the_game()
    {
        var (sim, flown, other) = FastCollisionTests.Fly(NearMissPass(1000f));
        using var _ = sim;
        Assert.NotNull(Nearest(sim, flown)); // it is one
        sim.Run((_, _) => new FlightInput(NearMisses: false), f =>
        {
            Assert.Null(f.Slowed);
            Assert.Null(f.NearMissWarning);
            return false;
        }, maxSeconds: 8);
        Assert.NotEqual(FlightState.Crashed, flown.State);
        Assert.NotEqual(FlightState.Crashed, other!.State);
    }

    [Fact]
    public void With_the_setting_on_a_near_miss_is_slowed_so_its_approach_lasts_as_set() => SlowedNearMiss();

    /// <summary>
    /// The near miss for the user to watch and judge by feel: run it with
    /// <c>GAMEONE_WATCH=1</c> (<c>--filter "FullyQualifiedName~NearMissTests.A_near_miss_to_watch"</c>)
    /// to see the 1,000 m/s pass beside the flown booster, the setting on, from its warning ahead
    /// through its slow-down to its end. How near is near (<see cref="Scene.NearMissLengths"/>),
    /// where its slowing begins (<see cref="Scene.SlowdownFrom"/>), how fast it passes to the eye
    /// (<see cref="Scene.SlowdownPassSpeed"/>) and how it eases back
    /// (<see cref="Scene.SlowdownEaseSeconds"/>) are read from <see cref="Scene"/>: change one there
    /// and watch it again. Its checks are the slowed near miss's own, so a feel tried that breaks
    /// the slowing's rules fails here too; a reach made shorter than this pass's gap, outline to
    /// outline, makes it no near miss at all (nothing to watch), and fails at once.
    /// </summary>
    [Fact]
    public void A_near_miss_to_watch() => SlowedNearMiss();

    /// <summary>
    /// The same near miss at the most two crafts can close at (each at <see cref="Scene.TopSpeed"/>,
    /// head on), for the user to watch how short its slowed way in is from
    /// <see cref="Scene.SlowdownFrom"/>: they start 2 s apart, not 5, so neither starts halfway
    /// round the planet.
    /// </summary>
    [Fact]
    public void A_top_speed_near_miss_to_watch() => SlowedNearMiss(2f * Scene.TopSpeed, 2f);

    /// <summary>
    /// The 1,000 m/s pass beside the flown booster, the setting on, flown to the end of its
    /// slow-down: slowed for the near miss alone, about as deep as <see cref="NearMiss.Scale"/>
    /// asks, its way in from where the slowing begins lasting the real time that follows from it,
    /// eased back within about <see cref="Scene.SlowdownEaseSeconds"/>, with no jump.
    /// </summary>
    private void SlowedNearMiss(float closing = 1000f, float seconds = 5f)
    {
        var (sim, flown, other) = FastCollisionTests.Fly(NearMissPass(closing, seconds));
        using var _ = sim;
        var predicted = Nearest(sim, flown)!.Value;
        var wanted = NearMiss.Scale(predicted);
        // Real seconds of its way in: eased down in a straight line in game seconds, τ ln(1/s) / (1 − s)
        // over its τ, then held at its slowest within SlowdownHoldWithin.
        var holdIn = MathF.Min(Scene.SlowdownHoldWithin / predicted.PassSpeed, 0.5f * predicted.FromSeconds);
        var wantedIn = MathF.Log(1f / wanted) * (predicted.FromSeconds - holdIn) / (1f - wanted) + holdIn / wanted;
        var scales = new List<float>();
        var (coming, near, left, least, slowedNearMiss) = (0, 0, -1, 1f, true);
        var over = false;
        sim.Run(Slowing, f =>
        {
            scales.Add(ScaleOf(f));
            if (f.Slowed is { } s)
            {
                slowedNearMiss &= s.NearMiss;
                least = MathF.Min(least, s.Scale);
            }
            var apart = Vector3.Distance(Collisions.Middle(flown), Collisions.Middle(other!));
            var closing = Vector3.Dot(Collisions.Middle(other!) - Collisions.Middle(flown), other!.Velocity - flown.Velocity) < 0f;
            if (closing && f.Slowed is not null)
                coming++; // its way in, slowed
            if (apart <= 100f)
                near++; // beside it, in and out (for the report: what the eye has of the pass)
            if (!closing && left < 0 && coming > 0)
                left = scales.Count; // passed
            over = left >= 0 && f.Slowed is null;
            return over;
        }, maxSeconds: 15);
        var after = scales.Count - left;
        output.WriteLine($"least {least:0.000} (wanted {wanted:0.000}); slowed from {Scene.SlowdownFrom} m in over {Real(coming):0.00} s real " +
            $"(wanted {wantedIn:0.00}); within 100 m for {Real(near):0.00} s real; eased back {Real(after):0.00} s real after passing; the most jump x{Jump(scales):0.000}");
        Assert.True(over, "never eased back");
        Assert.True(slowedNearMiss, "slowed for something else");
        Assert.InRange(least, wanted * 0.9f, wanted * 1.1f);
        Assert.InRange(Real(coming), wantedIn * 0.9, wantedIn * 1.1);
        var held = Scene.SlowdownHoldWithin / Scene.SlowdownPassSpeed; // real seconds held at its slowest after the pass
        Assert.True(Real(after) - held <= Scene.SlowdownEaseSeconds * 1.5, $"eased back over {Real(after) - held:0.00} s real after its hold");
        Assert.True(Jump(scales) <= 1.5f, $"a jump of x{Jump(scales):0.000}");
        Assert.NotEqual(FlightState.Crashed, flown.State);
    }

    [Theory]
    [InlineData(0.8f * Scene.NearMissSlowAbove, false)] // passing slower than NearMissSlowAbove: seen without slowing
    [InlineData(1000f, true)] // further than a length outline to outline
    public void A_slow_pass_or_one_further_than_a_length_never_slows(float closing, bool far)
    {
        var sideways = far ? FastCollisionTests.BoosterHeight + 2f * FastCollisionTests.BoosterWidest + 10f : Beside;
        var (sim, _, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(closing, 5000f, 5f, sideways: sideways));
        using var _ = sim;
        sim.Run(Slowing, f =>
        {
            Assert.Null(f.Slowed);
            Assert.Null(f.NearMissWarning);
            return false;
        }, maxSeconds: 7);
    }

    [Fact]
    public void A_near_miss_past_a_small_views_vessel_slows_the_game()
    {
        // As SlowdownTests' A_vessel_in_a_small_view_slows_the_game_too: the flown one is the new
        // rocket on the pad; the pair pass far off, one of them watched.
        using var sim = new Simulation(world: NearMissPass(1000f));
        var watched = sim.Flight.WorldVessels.First(v => v.Id == 0);
        Assert.NotSame(watched, sim.Flight.Controlled);
        IReadOnlyList<Vessel> views = [watched];
        var slowed = false;
        sim.Run((_, _) => new FlightInput(Watched: views, NearMisses: true), f =>
        {
            Assert.Null(f.NearMissWarning); // watched, never warned of
            slowed |= f.Slowed is { NearMiss: true };
            return slowed;
        }, maxSeconds: 6);
        Assert.True(slowed, "the watched vessel's near miss never slowed the game");
    }

    [Fact]
    public void A_fast_collision_and_a_near_miss_together_slow_by_the_deeper()
    {
        // Pair's pass inside each other's sizes at 2,000 m/s (sizes overlapping, bodies missing), and a third booster
        // crossing under the flown one at 1,000 m/s a length below, at the same moment.
        var world = FastCollisionTests.Pair(2000f, 5000f, 5f, sideways: 10f);
        var hitPoint = FastCollisionTests.PairHitPoint(5000f);
        var (up, east, north) = (Planet.Up(hitPoint), Planet.East(hitPoint), Planet.North(hitPoint));
        var across = Vector3.Normalize(Vector3.Cross(up, 2f * east)) * 5f;
        var third = FastCollisionTests.Flying(hitPoint - across - up * Beside, north, 1000f, 5f, 2);
        world = world with { Vessels = [.. world.Vessels, third], NextVesselId = 3 };

        // Each frame's game time and scale: the runs are compared at the same moment of the flight,
        // not the same frame, as the near miss slowing sooner puts the one run behind the other.
        List<(double Time, float Scale)> Run(bool nearMisses, List<float>? missScales, List<bool>? forNearMiss)
        {
            var (sim, flown, _) = FastCollisionTests.Fly(world);
            using var _ = sim;
            var scales = new List<(double, float)>();
            var asking = new Collisions(sim.Flight);
            sim.Run((_, _) => new FlightInput(NearMisses: nearMisses), f =>
            {
                scales.Add((f.WorldTime, ScaleOf(f)));
                missScales?.Add(asking.NearestMiss(flown, f.WorldVessels, Scene.CollisionHorizonSeconds) is { } m ? m.ScaleAt(m.Seconds) : 1f);
                forNearMiss?.Add(f.Slowed is { NearMiss: true });
                return false;
            }, maxSeconds: 7);
            return scales;
        }

        var forced = Run(false, null, null);
        var (missed, followed) = (new List<float>(), new List<bool>());
        var both = Run(true, missed, followed);
        var (deeper, nearMissDeeper) = (0, 0);
        var at = 0;
        for (var i = 0; i < both.Count; i++)
        {
            while (at + 1 < forced.Count && forced[at + 1].Time <= both[i].Time)
                at++;
            var alone = forced[at].Scale; // the forced run's, at this moment of the flight (their paths differ a little, by their steps: hence 5%)
            Assert.True(both[i].Scale <= alone * 1.05f + 1e-5f, $"frame {i}: {both[i].Scale} over the forced run's {alone}");
            Assert.True(both[i].Scale <= missed[i] * 1.001f + 1e-5f, $"frame {i}: {both[i].Scale} over the near miss's {missed[i]}");
            // The near miss asking less, and the slow-down following it: deeper than forced alone.
            if (followed[i] && both[i].Scale < alone * 0.999f)
                deeper++;
            if (missed[i] < 1f && missed[i] < alone)
                nearMissDeeper++;
        }
        output.WriteLine($"frames {forced.Count} and {both.Count}; least {forced.Min(x => x.Scale):0.0000} forced, {both.Min(x => x.Scale):0.0000} both, {missed.Min():0.0000} the near miss's; " +
            $"{deeper} frames slowed for the near miss deeper than the forced run's, the near miss deeper in {nearMissDeeper}");
        Assert.True(forced.Min(x => x.Scale) < 1f, "the forced run never slowed");
        Assert.True(missed.Min() < 1f, "no near miss");
        Assert.True(deeper > 0, "the near miss never made it deeper than the forced one alone");
    }

    [Fact]
    public void A_near_miss_is_shown_ahead_and_drops_the_warp_once()
    {
        var (sim, flown, other) = FastCollisionTests.Fly(NearMissPass(1000f, seconds: 20f));
        using var _ = sim;
        Assert.Null(sim.Flight.NearMissWarning); // no input asked for it yet
        // Warped before it is predicted (the setting off till then), then the setting on.
        while (sim.Flight.PlayerRate < 8f)
            sim.Step(new FlightInput(Faster: true));
        Assert.Null(sim.Flight.Slowed);
        // Shown from the moment it is predicted: on the very step a check of its own first finds it.
        var asking = new Collisions(sim.Flight);
        var (predicted, frames) = (false, 0);
        Assert.True(sim.Run(Slowing, f =>
        {
            if (frames++ == 0)
                return false; // (asked before the first frame, the setting still off)
            asking.NearestMiss(flown, f.WorldVessels, Scene.CollisionHorizonSeconds);
            predicted = asking.SoonestMiss is not null;
            Assert.Equal(predicted, f.NearMissWarning is not null);
            return predicted;
        }, maxSeconds: 20), "never shown");
        var shown = sim.Flight.NearMissWarning!.Value;
        output.WriteLine($"shown at {sim.Time:0.000} s: {shown}; rate {sim.Flight.PlayerRate}");
        Assert.Same(flown, shown.Watched);
        Assert.Same(other, shown.Other);
        Assert.Equal(1f, sim.Flight.PlayerRate); // the warp dropped
        Assert.Null(sim.Flight.Slowed); // ahead of its slowing
        Assert.True(shown.Seconds > 19.0, $"shown only {shown.Seconds:0.00} s ahead"); // as soon as it is within the horizon
        Assert.Equal(Hud.NearMissLine(shown), Readout.FlightReadings(sim.Flight)["Near miss"]);

        // Warped again by hand while it is shown: not dropped again.
        sim.Step(new FlightInput(Faster: true, NearMisses: true));
        sim.Step(new FlightInput(Faster: true, NearMisses: true));
        Assert.NotNull(sim.Flight.NearMissWarning);
        Assert.Equal(4f, sim.Flight.PlayerRate);
        sim.Run(Slowing, f => f.Slowed is not null, maxSeconds: 20);
        Assert.Equal(4f, sim.Flight.PlayerRate);
        Assert.True(sim.Flight.Slowed!.Value.NearMiss);
    }

    [Fact]
    public void A_close_pass_that_misses_is_one_warning_and_drops_the_warp_once()
    {
        // Inside each other's sizes (a fast collision by their sizes) but their outlines apart (a near
        // miss), as A_pass_inside_each_others_size_spheres_that_misses_is_a_near_miss: one event,
        // so one line (the COLLISION one, once it is warned of) and one drop of the warp.
        var sideways = 2f * FastCollisionTests.BoosterWidest + 3f;
        var (sim, flown, other) = FastCollisionTests.Fly(FastCollisionTests.Pair(1000f, 5000f, 20f, sideways: sideways));
        using var _ = sim;
        while (sim.Flight.PlayerRate < 8f)
            sim.Step(new FlightInput(Faster: true));
        Assert.True(sim.Run(Slowing, f => f.NearMissWarning is not null, maxSeconds: 20), "never shown");
        Assert.Null(sim.Flight.Warning); // the near miss shown first, long before the collision's lead
        Assert.Equal(1f, sim.Flight.PlayerRate); // its drop
        Assert.Equal(sim.Flight.NearMissWarning, sim.Flight.NearMissShown);
        // Warped again by hand: the fast collision's warning, when it comes, is the same event.
        sim.Step(new FlightInput(Faster: true, NearMisses: true));
        sim.Step(new FlightInput(Faster: true, NearMisses: true));
        Assert.Equal(4f, sim.Flight.PlayerRate);
        Assert.True(sim.Run(Slowing, f => f.Warning is not null, maxSeconds: 20), "never warned of");
        output.WriteLine($"warned at {sim.Time:0.000} s: {sim.Flight.Warning}; the near miss {sim.Flight.NearMissWarning}");
        Assert.Same(other, sim.Flight.Warning!.Value.Other);
        Assert.Equal(4f, sim.Flight.PlayerRate); // not dropped again
        Assert.NotNull(sim.Flight.NearMissWarning); // still predicted (and slowed for)...
        Assert.Null(sim.Flight.NearMissShown); // ...but not shown beside its COLLISION line
        var readings = Readout.FlightReadings(sim.Flight);
        Assert.True(readings.ContainsKey("Warning"));
        Assert.False(readings.ContainsKey("Near miss"));
        Assert.NotEqual(FlightState.Crashed, flown.State);
    }

    [Fact]
    public void A_pass_that_closes_into_a_close_pass_is_one_slow_down()
    {
        // The flown one lies across its way, its nose to the other, its engine pushing it in: a
        // pass 1.3 lengths apart sideways (a near miss) closing to 0.85 (within each other's sizes,
        // so a fast collision by their sizes too, but their outlines still apart) by its closest.
        var h = FastCollisionTests.BoosterHeight;
        var (start, end) = (1.3f * h, 0.85f * h);
        var world = FastCollisionTests.Pair(1000f, 5000f, 5f, sideways: start, lieAcross: 90f);
        // Towards where the other passes (Pair's across, at the collision: 5 km apart on this small
        // planet, the line between them now is no guide to it).
        var hitPoint = FastCollisionTests.PairHitPoint(5000f);
        var across = Vector3.Normalize(Vector3.Cross(Planet.Up(hitPoint), Planet.East(hitPoint)));
        Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitY, world.Vessels[0].Rotation), across) > 0.9f, "its nose is not towards the other");
        var (sim, flown, other) = FastCollisionTests.Fly(world);
        using var _ = sim;
        var throttle = 2f * (start - end) / (5f * 5f) / flown.MaxAcceleration;
        output.WriteLine($"length {h:0.0} m, sizes {2f * FastCollisionTests.BoosterSize:0.0} m; throttle {throttle:0.000} of {flown.MaxAcceleration:0.0} m/s²");
        var scales = new List<float>();
        var (closest, reasons) = (float.MaxValue, new HashSet<string>());
        // How often the deeper of the two switches between them: once in as the forced one takes
        // over, once back, and a spare; more is flicker between the reasons.
        var (wasNearMiss, switches) = ((bool?)null, 0);
        sim.Run((f, _) => new FlightInput(Throttle: f.Controlled.Throttle < throttle ? 1f : 0f, NearMisses: true), f =>
        {
            scales.Add(ScaleOf(f));
            if (f.Slowed is { } s)
            {
                reasons.Add($"{(s.NearMiss ? "near miss" : "forced")}: {s.Reason}");
                if (wasNearMiss is { } was && was != s.NearMiss)
                    switches++;
                wasNearMiss = s.NearMiss;
            }
            closest = MathF.Min(closest, Vector3.Distance(Collisions.Middle(other!), Collisions.Middle(flown)));
            return sim.Time > 5.5 && f.Slowed is null;
        }, maxSeconds: 10);
        var first = scales.FindIndex(s => s < 1f);
        var last = scales.FindLastIndex(s => s < 1f);
        // Rises then falls again, each by more than HeldRise's 5%.
        var (dips, rising, low) = (0, false, 1f);
        for (var i = first + 1; first >= 0 && i <= last; i++)
        {
            low = rising ? low : MathF.Min(low, scales[i - 1]);
            if (!rising && scales[i] > low * 1.05f)
                (rising, low) = (true, scales[i]);
            else if (rising && scales[i] < low / 1.05f)
                (dips, rising, low) = (dips + 1, false, scales[i]);
            else if (rising)
                low = MathF.Max(low, scales[i]);
        }
        output.WriteLine($"closest middles {closest:0.0} m; slowed frames {first} to {last}, least {scales.Min():0.000}; dips again {dips}; the reason switched {switches} times; {string.Join("; ", reasons)}");
        Assert.True(first >= 0, "never slowed");
        Assert.True(closest < 2f * FastCollisionTests.BoosterSize, $"it never closed within their sizes ({closest:0.0} m)");
        Assert.Contains(reasons, r => r.StartsWith("near miss"));
        Assert.NotEqual(FlightState.Crashed, flown.State);
        for (var i = first; i <= last; i++)
            Assert.True(scales[i] < 1f, $"not slowed at frame {i}, between {first} and {last}");
        Assert.True(dips < 2, $"it rose and fell again {dips} times");
        Assert.True(switches <= 3, $"the reason switched {switches} times");
    }

    [Fact]
    public void A_near_miss_18_s_out_is_predicted_at_once()
    {
        // Within the horizon from the start: the paths flown ahead, not one pull for the whole way
        // (which, on this small a planet, found a pass 20 s out only 11.6 s off).
        var (sim, flown, other) = FastCollisionTests.Fly(NearMissPass(1000f, seconds: 18f));
        using var _ = sim;
        var collisions = new Collisions(sim.Flight);
        collisions.NearestMiss(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds);
        var m = collisions.SoonestMiss;
        output.WriteLine($"at {sim.Time:0.000} s: {m}");
        Assert.NotNull(m);
        Assert.Same(other, m.Value.Other);
        Assert.InRange(m.Value.Seconds, 17.8 - sim.Time, 18.2 - sim.Time);
        Assert.InRange(m.Value.Gap, 0f, FastCollisionTests.BoosterHeight);
        // The gap as the pass itself will have it, 5 s off (A_pass_within_a_length_fast_enough_is_a_near_miss's 20.7 m).
        Assert.InRange(m.Value.Gap, 19f, 22.5f);
    }

    [Fact]
    public void The_rate_keys_do_nothing_while_a_near_miss_slows_the_game()
    {
        var (sim, _, _) = FastCollisionTests.Fly(NearMissPass(1000f, seconds: 12f));
        using var _ = sim;
        Assert.True(sim.Run(Slowing, f => f.NearMissWarning is not null, maxSeconds: 12), "never shown");
        while (sim.Flight.PlayerRate < 4f) // by hand, after it is shown
            sim.Step(new FlightInput(Faster: true, NearMisses: true));
        Assert.True(sim.Run(Slowing, f => f.Slowed is not null, maxSeconds: 12), "never slowed");
        Assert.True(sim.Flight.Slowed!.Value.NearMiss);
        sim.Step(new FlightInput(Faster: true, NearMisses: true));
        Assert.True(sim.Flight.Slowed is { NearMiss: true });
        Assert.Equal(4f, sim.Flight.PlayerRate);
        Assert.True(sim.Flight.TimeScale < 1f);
        sim.Step(new FlightInput(Slower: true, NearMisses: true));
        sim.Step(new FlightInput(Slower: true, NearMisses: true));
        Assert.True(sim.Flight.Slowed is { NearMiss: true });
        Assert.Equal(4f, sim.Flight.PlayerRate);
        Assert.True(sim.Run(Slowing, f => f.Slowed is null, maxSeconds: 5), "never eased back");
        Assert.Equal(4f, sim.Flight.PlayerRate);
        Assert.Equal(4f, sim.Flight.TimeScale);
        Assert.Equal(1f, sim.Physics.StepScale);
    }

    [Fact]
    public void The_warning_names_the_pass_coming_not_one_easing_back()
    {
        // As The_next_pass_easing_in_wins_over_one_just_gone_easing_back: Pair's two pass at 5 s,
        // a third, faster, about 1.2 s later. Once the first is past (and still easing back) the warning names
        // the third, still coming, or nothing; never the one gone.
        var world = NearMissPass(1000f);
        var hitPoint = FastCollisionTests.PairHitPoint(5000f);
        var east = Planet.East(hitPoint);
        var across = Vector3.Normalize(Vector3.Cross(Planet.Up(hitPoint), 2f * east)) * (Beside / 2f);
        var third = FastCollisionTests.Flying(hitPoint + across, -east, 700f, 7f, 2);
        var (sim, flown, first) = FastCollisionTests.Fly(world with { Vessels = [.. world.Vessels, third], NextVesselId = 3 });
        using var _ = sim;
        var next = sim.Flight.WorldVessels.First(v => v.Id == 2);
        var asking = new Collisions(sim.Flight);
        var (easingBack, namedNext) = (0, 0);
        sim.Run(Slowing, f =>
        {
            var deepest = asking.NearestMiss(flown, f.WorldVessels, Scene.CollisionHorizonSeconds);
            if (deepest is { } d && d.Other == first && d.Seconds < -0.05) // the first gone (past the step its warning was last told in), still easing back
            {
                easingBack++;
                Assert.False(asking.SoonestMiss is { } s && s.Other == first, "the soonest coming is the one gone");
                if (f.NearMissWarning is { } w)
                {
                    Assert.Same(next, w.Other);
                    Assert.True(w.Seconds >= 0.0);
                    namedNext++;
                }
            }
            if (f.NearMissWarning is { } shown)
                Assert.True(shown.Seconds >= 0.0, $"named a pass gone: {shown}");
            return sim.Time > 6.5;
        }, maxSeconds: 8);
        output.WriteLine($"frames with the first easing back: {easingBack}, the warning naming the next in {namedNext}");
        Assert.True(easingBack > 0, "the first was never easing back as the deepest");
        Assert.True(namedNext > 0, "the warning never named the next while the first eased back");
    }

    [Fact]
    public void Looking_ahead_for_near_misses_mid_slow_down_allocates_nothing()
    {
        var (sim, _, other) = FastCollisionTests.Fly(NearMissPass(1000f));
        using var _ = sim;
        IReadOnlyList<Vessel> views = [other!];
        Assert.True(sim.Run((_, _) => new FlightInput(Watched: views, NearMisses: true), f => f.Slowed is { NearMiss: true, Scale: < 0.5f }, maxSeconds: 6),
            "never slowed for the near miss");
        sim.Flight.LookAhead(views, nearMisses: true); // (anything worked out once, first)
        var scale = sim.Flight.TimeScale;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 120; i++)
            sim.Flight.LookAhead(views, nearMisses: true);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"looking ahead 120 times mid-slow-down: {bytes} bytes; the scale {scale} then {sim.Flight.TimeScale}");
        Assert.Equal(0, bytes);
        Assert.Equal(scale, sim.Flight.TimeScale); // asked again at the same time, nothing changes
    }

    [Fact]
    public void The_setting_turned_off_midway_eases_back()
    {
        var (sim, flown, _) = FastCollisionTests.Fly(NearMissPass(1000f));
        using var _ = sim;
        var wanted = NearMiss.Scale(Nearest(sim, flown)!.Value);
        Assert.True(sim.Run(Slowing, f => f.Slowed is { } s && s.Scale <= wanted * 1.02f, maxSeconds: 6), "never at its least");
        var scales = new List<float> { ScaleOf(sim.Flight) };
        var frames = 0;
        sim.Run(Nobody, f =>
        {
            scales.Add(ScaleOf(f));
            frames++;
            return f.Slowed is null;
        }, maxSeconds: 5);
        output.WriteLine($"turned off at {scales[0]:0.000} (least {wanted:0.000}): eased back in {Real(frames):0.00} s real; the most jump x{Jump(scales):0.000}");
        Assert.Null(sim.Flight.Slowed);
        Assert.Null(sim.Flight.NearMissWarning);
        Assert.True(Real(frames) <= Scene.SlowdownEaseSeconds * 1.5, $"eased back over {Real(frames):0.00} s real");
        Assert.True(Jump(scales) <= 1.5f, $"a jump of x{Jump(scales):0.000}");
    }

    [Fact]
    public void The_near_miss_lines_say_what_and_when()
    {
        var (sim, a, b) = FastCollisionTests.Fly(NearMissPass(1000f));
        using var _ = sim;
        Assert.Equal(1, b!.Id);
        Assert.Equal("Booster", b.Name);
        // In the HUD's own culture, as the rest of it is: "1,000 m/s" here.
        Assert.Equal($"NEAR MISS: Booster 1 in {3.2:0.0} s ({1000f:#,0} m/s)", Hud.NearMissLine(new NearMiss(a, b, 3.2, 20f, 1000f, 0.6f)));
        Assert.Equal($"NEAR MISS: Booster 1 in {0.0:0.0} s ({1000f:#,0} m/s)", Hud.NearMissLine(new NearMiss(a, b, -0.1, 20f, 1000f, 0.6f)));
        Assert.Equal($"SLOWED 1/{3.4f:0.0}x for a near miss with Booster 1",
            Hud.SlowedLine(new Slowdown(1f / 3.4f, 1000f, "a near miss with Booster 1", NearMiss: true)));
        // The flight's own words, and the readout's.
        Assert.True(sim.Run(Slowing, f => f.Slowed is not null, maxSeconds: 6), "never slowed");
        Assert.Equal("a near miss with Booster 1", sim.Flight.Slowed!.Value.Reason);
        Assert.NotNull(sim.Flight.NearMissWarning);
        var readings = Readout.FlightReadings(sim.Flight);
        Assert.Equal(Hud.NearMissLine(sim.Flight.NearMissWarning!.Value), readings["Near miss"]);
        Assert.Equal($"{sim.Flight.Slowed.Value.Scale:0.###}x, a near miss with Booster 1", readings["Slowed"]);
        Assert.False(Readout.FlightReadings(sim.Flight).ContainsKey("Warning")); // no fast collision
    }
}
