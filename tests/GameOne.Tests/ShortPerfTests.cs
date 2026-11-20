using TestRun;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The short performance tests: the physics and the flight's rules, headless (no drawing), in busy
/// scenes (<see cref="PerfScenes"/>), each warmed up and then run <see cref="ShortPerf.Runs"/>
/// times. Each prints, per part of a step, its median and 95th percentile per step and the runs'
/// spread, beside this machine's baseline (<see cref="PerfBaseline"/>) and whether it is better,
/// worse or about the same; and the game seconds run per wall second, beside the fastest the warp asks
/// of the physics (<see cref="Scene.PhysicsTopRate"/>: past it nothing is stepped by Jolt). Slower is reported, never failed; a scene that doesn't do what it should fails. With
/// <c>GAMEONE_PERF_REBASELINE=1</c> the results are written into the baseline file. They are run
/// only when working on performance (<c>GAMEONE_PERF=1</c>), never in the normal suite.
/// </summary>
public sealed class ShortPerfTests(ITestOutputHelper output)
{
    /// <summary>The fastest warp the game asks of the physics: past it the formula's rates step nothing of Jolt's.</summary>
    private const float MaxWarp = Scene.PhysicsTopRate;

    [PerfFact]
    public void A_world_of_a_hundred_satellites_coasting() => Report(ShortPerf.Measure(PerfScenes.HundredSatellites(), say: output.WriteLine));

    [PerfFact]
    public void Over_the_floe_band_with_its_floe_bodies_at_their_cap() => Report(ShortPerf.Measure(PerfScenes.FloeBand(), say: output.WriteLine));

    [PerfFact]
    public void Low_over_rough_ground_laying_and_taking_up_tiles() => Report(ShortPerf.Measure(PerfScenes.LowOverRoughGround(), say: output.WriteLine));

    /// <summary>
    /// How fast a craft can go near the ground before a frame's physics outgrows
    /// <see cref="ShortPerf.PlayableFrameMs"/>, paced at real time (see <see cref="PerfScenes.SpeedSweep"/>).
    /// Reported, never failed. It isn't one of the baseline's entries (its frames are what is read, and
    /// its own spreads say what they can show), so it is neither compared with the baseline nor written to it.
    /// </summary>
    [PerfFact]
    public void How_fast_a_craft_can_go_near_the_ground_before_a_frame_drops_under_60_fps()
    {
        var cases = PerfScenes.SpeedSweep();
        output.WriteLine($"Speed sweep ({PerfBaseline.Configuration}, paced at 60 frames a second; the physics a frame, the worker's and the step's tiles; playable is a 95th percentile under {ShortPerf.PlayableFrameMs:0} ms):");
        output.WriteLine("  case                              frames  median ms   95% ms  by worker  in step  kept  fell through  ended");
        foreach (var c in cases)
            output.WriteLine($"  {c.Name,-33} {c.Frames,6} {c.MedianMs,10:0.000} {c.P95Ms,8:0.000} {c.FromWorker,10} {c.MadeInStep,8} {c.FromKept,5}  {(c.FellThrough ? "YES" : "no"),12}  {c.Ended}");
        output.WriteLine("");
        string Fastest(IEnumerable<PerfScenes.SweepCase> of, string unit)
        {
            // The fastest speed that every slower one of its kind is playable up to (a faster one
            // that happens to pass after a slower one failed doesn't count).
            var ok = of.OrderBy(c => c.Speed).TakeWhile(c => c.P95Ms < ShortPerf.PlayableFrameMs).Select(c => c.Speed).DefaultIfEmpty(float.NaN).Max();
            return float.IsNaN(ok) ? "none" : $"{ok:0} {unit}";
        }
        output.WriteLine($"fastest playable, each with every slower speed playable too: coasting low over rough ground (inside the urgent band) {Fastest(cases.Where(c => c.Kind == "level"), "m/s")}; " +
            string.Join("; ", PerfScenes.SweepCrashAngles.Select(a => $"crash at {a:0}° {Fastest(cases.Where(c => c.Kind == "crash" && c.Degrees == a), "m/s")}")) +
            $" (95th percentile frame under {ShortPerf.PlayableFrameMs:0} ms of physics); {cases.Count(c => c.FellThrough)} of {cases.Count} cases fell through the ground (none expected)");
    }

    /// <summary>
    /// The frame rate held through a fast collision's slow-down (<see cref="PerfScenes.FastCollisions"/>),
    /// paced at real time as the speed sweep is: slowed, the game runs the same steps a real second,
    /// each shorter, so each slowed frame's physics must stay under <see cref="ShortPerf.PlayableFrameMs"/>
    /// at its 95th percentile, and every one of them under it but at most one frame a case, and
    /// that one under <see cref="JitterAllowance"/> times it (the OS's jitter: a thread put off
    /// its core for a moment costs a frame a few ms; a frame further over is the scene's own
    /// cost, which comes back run after run, so it fails), each over printed by its case, frame,
    /// ms and where its time went. Not a baseline entry (as the sweep
    /// isn't). The frames before the slow-down, at full speed, are printed beside them, for the
    /// speed sweep's sake: they are not what is held here.
    /// </summary>
    [PerfFact]
    public void A_fast_pass_holds_60_fps()
    {
        var cases = PerfScenes.FastCollisions();
        output.WriteLine($"Fast collisions ({PerfBaseline.Configuration}, paced at 60 frames a second; each frame's physics, ms; playable is a 95th percentile, and every frame but one, under {ShortPerf.PlayableFrameMs:0} ms):");
        output.WriteLine("  case                                                                 before: frames  95% ms  max ms | slowed: frames  median ms  95% ms  max ms  least scale  real s  Collision  Tiles  made in step  ended");
        foreach (var c in cases)
            output.WriteLine($"  {c.Name,-68} {c.BeforeFrames,14} {c.BeforeP95Ms,7:0.000} {c.BeforeMaxMs,7:0.000} | {c.SlowedFrames,14} {c.SlowedMedianMs,10:0.000} {c.SlowedP95Ms,7:0.000} {c.SlowedMaxMs,7:0.000} {c.LeastScale,12:0.0000} {c.SlowedRealSeconds,7:0.00} {c.CollisionShare,9:0.0%} {c.TilesShare,6:0.0%} {c.MadeInStep,13}  {c.Ended}");
        var over = cases.Where(c => c.Collides).SelectMany(c => c.SlowedOver.Select(o => (c.Name, o.Frame, o.Ms, o.Where))).ToList();
        foreach (var (name, frame, ms, where) in over)
            output.WriteLine($"  over {ShortPerf.PlayableFrameMs:0} ms, slowed: {name}, frame {frame}: {ms:0.000} ms ({where})");
        Assert.All(cases.Where(c => c.Collides), c =>
        {
            Assert.True(c.SlowedFrames > 0, $"{c.Name}: never slowed");
            Assert.True(c.SlowedP95Ms < ShortPerf.PlayableFrameMs, $"{c.Name}: a slowed frame's physics {c.SlowedP95Ms:0.000} ms at its 95th percentile");
            // Its worst frame under it too, but for one a little over (see the summary): a
            // thread put off its core for a moment costs a frame a few ms, not half again of
            // the limit, so a frame that far over is the scene's own cost, run after run.
            Assert.True(c.SlowedOver.Count <= 1 && c.SlowedMaxMs < JitterAllowance * ShortPerf.PlayableFrameMs,
                $"{c.Name}: {c.SlowedOver.Count} slowed frames over {ShortPerf.PlayableFrameMs:0} ms (its worst {c.SlowedMaxMs:0.000} ms, one under {JitterAllowance * ShortPerf.PlayableFrameMs:0} ms let pass): "
                + string.Join("; ", c.SlowedOver.Select(o => $"frame {o.Frame}: {o.Ms:0.000} ms ({o.Where})")));
        });
    }

    /// <summary>
    /// The frame rate while a crowd is handed into the physics and out of it
    /// (<see cref="PerfScenes.Crowd"/>: the flown vessel flown past a thousand satellites, with the
    /// map closed and open, and closed with near misses slowed for), paced at real time: each
    /// case's frames, set beside <see cref="ShortPerf.PlayableFrameMs"/> by the fast pass's rule
    /// (<see cref="A_fast_pass_holds_60_fps"/>) and said to hold it or not, each over printed by
    /// its case, frame, ms and where its time went. It reports, never fails, on its times: on the
    /// test machine they move with a load that can't be controlled, where the parts' shares
    /// (most of it the formula's and the predictions') hold. It fails only if the scene didn't do
    /// what it is for. The handovers are spread over steps
    /// (<see cref="Scene.HandoversInPerStep"/>) so that none costs a frame its budget. Not a
    /// baseline entry (as the fast pass isn't).
    /// </summary>
    [PerfFact]
    public void A_crowd_handed_in_and_out_reports_its_frame_rate()
    {
        var cases = PerfScenes.Crowd();
        output.WriteLine($"A crowd handed in and out ({PerfBaseline.Configuration}, paced at 60 frames a second; each frame's physics, ms; playable is a 95th percentile, and every frame but one, under {ShortPerf.PlayableFrameMs:0} ms):");
        output.WriteLine("  case                                                                frames  median ms  95% ms  max ms  came in  went out  most in  rules  formula  Collision  map 95% ms  map max ms  ended");
        foreach (var c in cases)
            output.WriteLine($"  {c.Name,-67} {c.Frames,6} {c.MedianMs,10:0.000} {c.P95Ms,7:0.000} {c.MaxMs,7:0.000} {c.In,8} {c.Out,9} {c.MostInPhysics,8} {c.RulesShare,6:0.0%} {c.FormulaShare,8:0.0%} {c.CollisionShare,9:0.0%} {c.MapP95Ms,11:0.000} {c.MapMaxMs,11:0.000}  {c.Ended}");
        foreach (var c in cases)
            output.WriteLine($"  worst: {c.Name}, frame {c.Worst.Frame}: {c.Worst.Ms:0.000} ms ({c.Worst.Where})");
        foreach (var c in cases)
            foreach (var (frame, ms, where) in c.Over)
                output.WriteLine($"  over {ShortPerf.PlayableFrameMs:0} ms: {c.Name}, frame {frame}: {ms:0.000} ms ({where})");
        foreach (var c in cases)
        {
            var holds = c.P95Ms < ShortPerf.PlayableFrameMs && c.Over.Count <= 1 && c.MaxMs < JitterAllowance * ShortPerf.PlayableFrameMs;
            output.WriteLine($"  {c.Name}: {(holds ? "holds" : "doesn't hold")} {ShortPerf.PlayableFrameMs:0} ms (95th percentile {c.P95Ms:0.000} ms, {c.Over.Count} over, worst {c.MaxMs:0.000} ms)");
        }
        Assert.All(cases, c =>
        {
            // The scene did what it is for: the crowd handed in and out, and nothing hit.
            Assert.True(c.In >= 20 && c.Out >= 20, $"{c.Name}: {c.In} came into the physics and {c.Out} left it: too few to show the handovers' cost");
            Assert.StartsWith($"{FlightState.Flying}; 0 wrecked", c.Ended);
        });
    }

    /// <summary>
    /// Fifty boosters coming down and landing with nobody watching (<see cref="PerfScenes.UnwatchedLandings"/>):
    /// the formula's cost in the air, burning and at the touch. Each part's time and share of the
    /// step, as the others print them. Not a baseline entry (its comparison is the same scene in
    /// the physics, not an earlier run of itself), so it is neither compared with the baseline nor
    /// written to it.
    /// </summary>
    [PerfFact]
    public void Fifty_boosters_landing_unwatched()
    {
        foreach (var scene in ShortPerf.Measure(PerfScenes.UnwatchedLandings(), say: output.WriteLine))
        {
            output.WriteLine($"{scene.Name} ({PerfBaseline.Configuration}, {ShortPerf.Runs} runs after a warm-up):");
            if (scene.Note is { } note)
                output.WriteLine($"  {note}");
            output.WriteLine("  part                median ms   95% ms  mean ms  share   spread");
            var whole = scene.Parts.GetValueOrDefault(ShortPerf.Whole)?.Mean ?? double.NaN;
            foreach (var (part, now) in scene.Parts)
                output.WriteLine($"  {part,-18} {now.Median,10:0.000} {now.P95,8:0.000} {now.Mean,8:0.000} {now.Mean / whole,6:0%} {now.Spread,8:0.0%}");
            output.WriteLine($"  {scene.Speed.Median:0.0}x real time (spread {scene.Speed.Spread:0.0%})");
        }
    }

    /// <summary>How far over <see cref="ShortPerf.PlayableFrameMs"/> the one slowed frame a case may go that is let pass as the OS's jitter (see <see cref="A_fast_pass_holds_60_fps"/>).</summary>
    private const double JitterAllowance = 1.5;

    [PerfFact]
    public void Five_rockets_launched_together_all_reach_orbit()
    {
        // How each run ended, said once for as many runs as end alike (the flights are the same
        // every run, unless something has made them differ).
        string? last = null;
        Report(ShortPerf.Measure(PerfScenes.FiveRockets(line =>
        {
            if (line != last)
                output.WriteLine(last = line);
        }), say: output.WriteLine));
    }

    private void Report(IReadOnlyList<ScenePerf> scenes)
    {
        var (machine, configuration) = (Machine.Key(), PerfBaseline.Configuration);
        var (file, unreadable) = PerfBaseline.Read(PerfBaseline.FilePath);
        var entry = PerfBaseline.Entry(file, machine, configuration);
        if (unreadable)
            output.WriteLine($"WARNING: {Note(unreadable, entry, null, configuration)}\n");
        foreach (var scene in scenes)
        {
            var before = entry?.Scenes.GetValueOrDefault(scene.Name);
            output.WriteLine($"{scene.Name} ({configuration}, {ShortPerf.Runs} runs after a warm-up):");
            if (scene.Note is { } note)
                output.WriteLine($"  {note}");
            // The verdict is by the medians. The mean is the part's share of the whole time, its
            // share of the step's beside it: a part whose mean is well over its median does its
            // work in spikes, which its median alone would understate.
            // A part's pieces ("Tiles: Samples") come under it, their times within its own.
            output.WriteLine("  part                median ms   95% ms  mean ms  share   spread   baseline ms    change   verdict");
            var whole = scene.Parts.GetValueOrDefault(ShortPerf.Whole)?.Mean ?? double.NaN;
            foreach (var (part, now) in scene.Parts)
            {
                var then = before?.Parts.GetValueOrDefault(part);
                var (verdict, change) = PerfBaseline.Compare(then, now);
                output.WriteLine($"  {part,-18} {now.Median,10:0.000} {now.P95,8:0.000} {now.Mean,8:0.000} {now.Mean / whole,6:0%} {now.Spread,8:0.0%} " +
                    $"{(then is null ? "-" : then.Median.ToString("0.000")),13} {(then is null ? "-" : change.ToString("+0.0%;-0.0%")),9}   {Say(verdict)}");
            }

            var speed = PerfBaseline.Compare(before?.Speed, scene.Speed, higherIsBetter: true);
            output.WriteLine($"  {scene.Speed.Median:0.0}x real time (the warp asks up to {MaxWarp:0}x), " +
                (before is null ? "no baseline" : $"baseline {before.Speed.Median:0.0}x: {Say(speed.Verdict)}") +
                $" (spread {scene.Speed.Spread:0.0%})");

            // What would show: the largest of the spreads of the parts big enough to time and of
            // the speed's, this run's and the baseline's (as the verdicts take both), or the tolerance.
            var spread = scene.Parts.Where(p => p.Value.Median >= PerfBaseline.FloorMs)
                .SelectMany(p => new[] { p.Value.Spread, before?.Parts.GetValueOrDefault(p.Key)?.Spread ?? 0 })
                .Append(scene.Speed.Spread).Append(before?.Speed.Spread ?? 0).Max();
            output.WriteLine($"  a change of more than {Math.Max(spread, PerfBaseline.Tolerance):0.0%} would show; " +
                Note(unreadable, entry, before, configuration));
            output.WriteLine("");
        }

        if (Environment.GetEnvironmentVariable("GAMEONE_PERF_REBASELINE") == "1")
        {
            // Read again, as another test may have rebaselined since; it refuses an unreadable file.
            var commit = PerfBaseline.Commit();
            PerfBaseline.Rebaseline(PerfBaseline.FilePath, machine, configuration, scenes, commit);
            output.WriteLine($"rebaselined: {PerfBaseline.FilePath} ({configuration}, {commit})");
        }
    }

    /// <summary>What a scene was compared with: nothing (the file unreadable, or no entry or scene for this machine), or the entry's commit and date.</summary>
    internal static string Note(bool unreadable, MachineEntry? entry, ScenePerf? before, string configuration) =>
        unreadable ? $"{Path.GetFileName(PerfBaseline.FilePath)} is there but couldn't be read: nothing was compared (mend it before rebaselining)"
        : entry is null ? $"no baseline for this machine ({configuration})"
        : before is null ? $"this scene isn't in this machine's baseline ({configuration})"
        : $"baseline measured on {before.Commit ?? entry.Commit}, {before.Measured ?? entry.Measured:yyyy-MM-dd HH:mm} UTC";

    private static string Say(Verdict verdict) => verdict switch
    {
        Verdict.Better => "better",
        Verdict.Worse => "worse",
        Verdict.Same => "about the same",
        _ => "-",
    };
}
