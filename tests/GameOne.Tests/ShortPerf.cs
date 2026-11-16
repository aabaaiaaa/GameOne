using System.Diagnostics;

namespace GameOne.Tests;

/// <summary>
/// One run of a scene: its timed steps (one <see cref="StepSample"/> per <see cref="Flight"/> step,
/// a frame's worth of game time), and named stretches of them reported as scenes of their own
/// (<c>"&lt;scene&gt;: &lt;stretch&gt;"</c>). Its speed, and each stretch's, is worked out from the
/// steps alone (<see cref="ShortPerf.StepSpeed"/>). Its <paramref name="Note"/> is a line the report
/// prints under the scene's name (what the steps alone don't say, such as where the tiles came from).
/// </summary>
internal sealed record SceneRun(StepSample[] Steps, IReadOnlyDictionary<string, StepSample[]>? Stretches = null, string? Note = null);

/// <summary>
/// A short performance scene. <see cref="Run"/> builds its own <see cref="Simulation"/>, sets it up
/// untimed, then turns the timings on (afresh) for the stretch it times, checks itself with
/// <c>Assert</c>s, turns the timings off and returns what it timed.
/// </summary>
internal sealed record PerfScene(string Name, Func<SceneRun> Run);

/// <summary>
/// A part's time per step over the runs, in milliseconds: the median of the runs' medians and of
/// their 95th percentiles, the medians' spread as a fraction, and the median of the runs' means.
/// The mean is the part's share of the whole time (what decides the warp the physics allows), as
/// a median isn't for a part that does its work in a few steps: medians don't add up to the step's.
/// A baseline from before the mean was kept reads it as NaN: none to compare.
/// </summary>
internal sealed record PartPerf(double Median, double P95, double Spread, double Mean = double.NaN);

/// <summary>
/// A scene's measurement: its <see cref="Speed"/> (game seconds per second of its steps' time,
/// headless: the fastest warp the physics and rules alone allow there), and each part's time per step, keyed by
/// each <see cref="StepPart"/>'s name and <c>"Step"</c> (the step's whole). Its <paramref name="Note"/>
/// (the last run's) is for the report only, and is never written to the baseline.
/// </summary>
internal sealed record ScenePerf(string Name, PartPerf Speed, IReadOnlyDictionary<string, PartPerf> Parts, [property: System.Text.Json.Serialization.JsonIgnore] string? Note = null);

/// <summary>
/// Runs a scene to measure it: warmed up (the JIT's tiers, caches; discarded) for at least
/// <see cref="WarmUpSeconds"/>, then <see cref="Runs"/> times, combined.
/// </summary>
internal static class ShortPerf
{
    public const int Runs = 3;

    /// <summary>
    /// The most a frame's physics may take, at its 95th percentile, for the speed sweep to call a
    /// speed playable (and the paced checks to pass): within a 60 fps frame's 16.7 ms, with room
    /// for drawing on a faster machine. Set for a test machine whose load and power can't be
    /// controlled, so its frames run slower than the game's own would: where the time goes is
    /// judged by the parts' shares and multiples of Jolt's step, which hold on any machine.
    /// </summary>
    public const double PlayableFrameMs = 15;

    /// <summary>
    /// How long a scene is run, at least, before it is measured: once is enough for a long scene,
    /// but a short one run once was still being compiled to the JIT's faster tier into its first
    /// timed runs (the floe band's first timed run 8% slower than its later ones).
    /// </summary>
    public const double WarmUpSeconds = 8.0;

    /// <summary>What a part is called in <see cref="ScenePerf.Parts"/>: each <see cref="StepPart"/> by name, each <see cref="StepDetail"/> as <see cref="DetailName"/> has it, then the step's whole.</summary>
    public const string Whole = "Step";

    public static readonly string[] PartNames = [.. Enum.GetNames<StepPart>(), .. Enum.GetValues<StepDetail>().Select(DetailName), Whole];

    /// <summary>A piece's name in <see cref="ScenePerf.Parts"/>: its part's, then its own (<c>"Tiles: Samples"</c>), within the part's time.</summary>
    public static string DetailName(StepDetail detail) => $"{Timings.PartOf(detail)}: {detail}";

    /// <summary>
    /// Warms <paramref name="scene"/> up, then runs it <paramref name="runs"/> times, and returns
    /// the scene and then each of its stretches. A scene that fails its checks throws, so nothing
    /// after it (a report, a baseline written) happens; the timings are left off either way.
    /// <paramref name="say"/> is told, for each timed run, which kind of core its steps were on.
    /// </summary>
    public static IReadOnlyList<ScenePerf> Measure(PerfScene scene, int runs = Runs, Action<string>? say = null)
    {
        var results = new List<SceneRun>();
        var cores = new List<string>();
        // On the fast cores (FastCores), the warm-up too, so the JIT's and caches' warm-up is on
        // the cores the runs are timed on.
        using (FastCores.Hold())
        {
            try
            {
                var warming = Stopwatch.StartNew();
                do
                    Run(scene);
                while (warming.Elapsed.TotalSeconds < WarmUpSeconds);
                for (var i = 0; i < runs; i++)
                {
                    results.Add(Run(scene));
                    cores.Add(CoreKinds.Say());
                }
            }
            finally
            {
                Timings.Enabled = false;
            }
        }
        // Which kind of core each run's steps were on (the main thread's, between steps).
        for (var i = 0; i < cores.Count; i++)
            say?.Invoke($"{scene.Name}, run {i + 1}: {cores[i]}");

        var measured = new List<ScenePerf> { Of(scene.Name, results.Select(r => (r.Steps, StepSpeed(r.Steps)))) with { Note = results[^1].Note } };
        var stretches = results[0].Stretches?.Keys.ToArray() ?? [];
        if (results.Any(r => (r.Stretches?.Count ?? 0) != stretches.Length || stretches.Any(s => !r.Stretches!.ContainsKey(s))))
            throw new InvalidOperationException($"{scene.Name}'s runs don't all time the same stretches");
        foreach (var stretch in stretches)
            measured.Add(Of($"{scene.Name}: {stretch}", results.Select(r => (r.Stretches![stretch], StepSpeed(r.Stretches[stretch])))));
        return measured;
    }

    private static SceneRun Run(PerfScene scene)
    {
        // Each run starts with the last one's garbage gone, so a collection it didn't cause
        // doesn't land in its timed steps.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        CoreKinds.Reset();
        return scene.Run();
    }

    /// <summary>
    /// A run's speed, or a stretch's, from its steps alone: their game time over the time they
    /// took. One way for both, so a stretch's speed can be set beside its scene's: a stretch's
    /// steps needn't come one after another, so a wall clock couldn't time it; and what is outside
    /// the flight's step (the harness's loop, its pilots and checks) isn't the game's to count.
    /// What <see cref="Simulation.Step"/> does outside the step's laps (clearing the step's
    /// sounds, adding up its clock, a watcher only when watched) is left out too: measured, it was
    /// 0.26% of the steps' time in the five rockets' scene, and the harness's loop 0.3% more.
    /// </summary>
    internal static double StepSpeed(StepSample[] steps) =>
        steps.Length * (double)Simulation.FrameSeconds / (steps.Sum(s => s.TotalMs) / 1000.0);

    private static ScenePerf Of(string name, IEnumerable<(StepSample[] Steps, double Speed)> runs)
    {
        var all = runs.ToArray();
        var parts = new Dictionary<string, PartPerf>();
        foreach (var part in Enum.GetValues<StepPart>())
        {
            parts[part.ToString()] = Combine([.. all.Select(r => Percentiles(r.Steps.Select(s => s[part])))]);
            // Then its pieces, under it (within its time, not beside it: see StepDetail).
            foreach (var detail in Enum.GetValues<StepDetail>().Where(d => Timings.PartOf(d) == part))
                parts[DetailName(detail)] = Combine([.. all.Select(r => Percentiles(r.Steps.Select(s => s[detail])))]);
        }
        parts[Whole] = Combine([.. all.Select(r => Percentiles(r.Steps.Select(s => s.TotalMs)))]);
        // A run has one speed, so its "95th percentile" and mean are that speed too: they are the
        // median of the runs' speeds again, kept only so a speed is a PartPerf like the parts.
        return new ScenePerf(name, Combine([.. all.Select(r => (r.Speed, r.Speed, r.Speed))]), parts);
    }

    /// <summary>The runs combined: the median of their medians, the median of their 95th percentiles, the medians' spread, (max - min) / median (0 when the median is 0), and the median of their means.</summary>
    public static PartPerf Combine(IReadOnlyList<(double Median, double P95, double Mean)> runs)
    {
        var medians = runs.Select(r => r.Median).Order().ToArray();
        var median = MedianOf(medians);
        var p95 = MedianOf([.. runs.Select(r => r.P95).Order()]);
        var mean = MedianOf([.. runs.Select(r => r.Mean).Order()]);
        var spread = median == 0 ? 0 : (medians[^1] - medians[0]) / median;
        return new PartPerf(median, p95, spread, mean);
    }

    /// <summary>The median, 95th percentile and mean of <paramref name="values"/>, the percentiles indexed as <see cref="PerformanceTests"/> does (the sorted values' n/2 and n×0.95).</summary>
    public static (double Median, double P95, double Mean) Percentiles(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return (sorted[sorted.Length / 2], sorted[(int)(sorted.Length * 0.95)], sorted.Average());
    }

    // Of the runs: the middle one, or the mean of the middle two (an even number of runs).
    private static double MedianOf(double[] sorted) =>
        sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);
}
