using TestRun;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The short performance tests' arithmetic and their baseline file, timing nothing: runs combine
/// to their median and spread, a change shows only past the larger of the spread and 20%, a part
/// too small to time is never called better or worse, the machine's key names nothing, and a
/// missing, unreadable or other machine's baseline is never compared or overwritten. These run in
/// the normal suite; the scenes themselves (<see cref="ShortPerfTests"/>) only with <c>GAMEONE_PERF</c>.
/// </summary>
public sealed class ShortPerfMathTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));

    public ShortPerfMathTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Timings.Enabled = false;
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static ScenePerf Scene(string name, double median) =>
        new(name, new PartPerf(30, 30, 0.01), new Dictionary<string, PartPerf> { ["Step"] = new(median, median * 2, 0.02) });

    [Fact]
    public void Runs_combine_to_their_median_and_spread()
    {
        var p = ShortPerf.Combine([(1.0, 2.0, 1.5), (1.1, 2.4, 1.3), (0.9, 2.2, 1.7)]);
        Assert.Equal(1.0, p.Median, 9);
        Assert.Equal(2.2, p.P95, 9);
        Assert.Equal(0.2, p.Spread, 9);
        Assert.Equal(1.5, p.Mean, 9);
    }

    [Fact]
    public void A_spiky_part_s_mean_is_its_share_where_its_median_is_not()
    {
        // Nine steps of nothing and one of ten: the median says nothing, the mean says 1 a step.
        var (median, _, mean) = ShortPerf.Percentiles([0, 0, 0, 0, 0, 0, 0, 0, 0, 10]);
        Assert.Equal(0, median);
        Assert.Equal(1.0, mean, 9);
    }

    [Fact]
    public void A_noisy_baseline_widens_what_counts_as_about_the_same()
    {
        // 25% slower, with a quiet run beside a baseline whose runs spread 30%: chance, not a change.
        Assert.Equal(Verdict.Same, PerfBaseline.Compare(new(1.0, 2, 0.30), new(1.25, 2, 0.02)).Verdict);
        Assert.Equal(Verdict.Worse, PerfBaseline.Compare(new(1.0, 2, 0.30), new(1.35, 2, 0.02)).Verdict);
    }

    [Fact]
    public void A_baseline_from_before_the_mean_was_kept_reads_and_writes_it_as_none()
    {
        var path = Path.Combine(_folder, "PerfBaseline.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, """
            { "machines": { "m": { "Debug": { "measured": "2026-11-02T21:32:28+00:00", "commit": "b0394d3", "scenes": {
              "A": { "name": "A", "speed": { "median": 30, "p95": 30, "spread": 0.02 },
                     "parts": { "Step": { "median": 1.5, "p95": 2.4, "spread": 0.06 } } } } } } } }
            """);
        var (file, unreadable) = PerfBaseline.Read(path);
        Assert.False(unreadable);
        var step = PerfBaseline.Entry(file, "m", "Debug")!.Scenes["A"].Parts["Step"];
        Assert.Equal(1.5, step.Median, 9);
        Assert.True(double.IsNaN(step.Mean));

        // Kept beside a new scene in a rebaseline, it is written back, still with none.
        PerfBaseline.Rebaseline(path, "m", "Debug", [Scene("B", 1.0)], "abc1234");
        Assert.True(double.IsNaN(PerfBaseline.Entry(PerfBaseline.Load(path), "m", "Debug")!.Scenes["A"].Parts["Step"].Mean));
    }

    [Fact]
    public void A_change_over_twenty_percent_shows_and_one_within_it_does_not()
    {
        Assert.Equal(Verdict.Worse, PerfBaseline.Compare(new(1.0, 2, 0.03), new(1.25, 2, 0.03)).Verdict);
        Assert.Equal(0.25, PerfBaseline.Compare(new(1.0, 2, 0.03), new(1.25, 2, 0.03)).Change, 9);
        Assert.Equal(Verdict.Same, PerfBaseline.Compare(new(1.0, 2, 0.03), new(1.15, 2, 0.03)).Verdict);
        Assert.Equal(Verdict.Same, PerfBaseline.Compare(new(1.0, 2, 0.03), new(1.25, 2, 0.28)).Verdict); // within a wider spread
        Assert.Equal(Verdict.Better, PerfBaseline.Compare(new(1.0, 2, 0.03), new(0.75, 2, 0.03)).Verdict);
        Assert.Equal(Verdict.Better, PerfBaseline.Compare(new(30, 30, 0.02), new(37.5, 37.5, 0.02), higherIsBetter: true).Verdict);
        Assert.Equal(Verdict.NoBaseline, PerfBaseline.Compare(null, new(1, 1, 0)).Verdict);
    }

    [Fact]
    public void A_part_too_small_to_time_is_about_the_same()
    {
        Assert.Equal(Verdict.Same, PerfBaseline.Compare(new(0.002, 0.004, 0.5), new(0.008, 0.01, 0.5)).Verdict);
        Assert.Equal(Verdict.Same, PerfBaseline.Compare(new(0, 0, 0), new(0.005, 0.005, 0)).Verdict); // no divide by zero
    }

    [Fact]
    public void The_commit_is_the_one_checked_out_and_says_so_when_the_tree_is_changed()
    {
        // (The tree's state is whatever it is when the tests run: either form is right.)
        Assert.Matches(@"^([0-9a-f]{7,}(\+changes\??)?|unknown)$", PerfBaseline.Commit());
    }

    [Fact]
    public void Git_takes_the_changes_question_and_leaves_the_baseline_out()
    {
        // A pathspec git refuses makes Changes null, which Commit can only report as "+changes?":
        // this is what would have caught one it rejected.
        var changes = PerfBaseline.Changes();
        Assert.NotNull(changes);
        Assert.DoesNotContain("PerfBaseline.json", changes);
    }

    [Fact]
    public void The_machine_key_is_a_hash_that_names_nothing()
    {
        var key = Machine.Key();
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.Equal(key, Machine.Key());
        Assert.DoesNotContain(Environment.MachineName.ToLowerInvariant(), key);
    }

    [Fact]
    public void A_missing_or_unreadable_baseline_compares_nothing_and_rebaselining_keeps_other_entries()
    {
        var path = Path.Combine(_folder, "PerfBaseline.json");
        Assert.Empty(PerfBaseline.Load(path).Machines);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(PerfBaseline.Load(path).Machines);

        var key = Machine.Key();
        var file = new BaselineFile();
        PerfBaseline.Replace(file, "other", "Debug", [Scene("A", 5.0)], "0ther00");
        Assert.Null(PerfBaseline.Entry(file, key, "Debug"));

        PerfBaseline.Replace(file, key, "Debug", [Scene("A", 1.0)], "abc1234");
        PerfBaseline.Save(path, file);
        var loaded = PerfBaseline.Load(path);
        Assert.NotNull(PerfBaseline.Entry(loaded, "other", "Debug"));
        Assert.Equal("abc1234", PerfBaseline.Entry(loaded, key, "Debug")!.Commit);
        Assert.Equal(1.0, PerfBaseline.Entry(loaded, key, "Debug")!.Scenes["A"].Parts["Step"].Median, 9);
        Assert.Equal(TimeSpan.Zero, PerfBaseline.Entry(loaded, key, "Debug")!.Measured.Offset); // in UTC

        // Again with scene B only: A stays in this entry, and the other machine's is untouched.
        PerfBaseline.Replace(loaded, key, "Debug", [Scene("B", 2.0)], "def5678");
        PerfBaseline.Save(path, loaded);
        var again = PerfBaseline.Load(path);
        var mine = PerfBaseline.Entry(again, key, "Debug")!;
        Assert.Equal(1.0, mine.Scenes["A"].Parts["Step"].Median, 9);
        Assert.Equal(2.0, mine.Scenes["B"].Parts["Step"].Median, 9);
        Assert.Equal("def5678", mine.Commit);
        // Each scene keeps the stamp it was measured with: A abc1234's, B def5678's.
        Assert.Equal("abc1234", mine.Scenes["A"].Commit);
        Assert.Equal("def5678", mine.Scenes["B"].Commit);
        Assert.True(mine.Scenes["B"].Measured >= mine.Scenes["A"].Measured);
        Assert.Equal(TimeSpan.Zero, mine.Scenes["B"].Measured!.Value.Offset); // in UTC
        var other = PerfBaseline.Entry(again, "other", "Debug")!;
        Assert.Equal("0ther00", other.Commit);
        Assert.Equal(5.0, other.Scenes["A"].Parts["Step"].Median, 9);
        Assert.False(other.Scenes.ContainsKey("B"));

        Assert.DoesNotContain(Environment.MachineName, File.ReadAllText(path));
    }

    [Fact]
    public void A_rebaseline_never_saves_over_a_baseline_it_cannot_read()
    {
        var path = Path.Combine(_folder, "PerfBaseline.json");
        var key = Machine.Key();

        // Missing: made, with this machine's entry.
        PerfBaseline.Rebaseline(path, key, "Debug", [Scene("A", 1.0)], "abc1234");
        Assert.Equal("abc1234", PerfBaseline.Entry(PerfBaseline.Load(path), key, "Debug")!.Commit);

        // There but unreadable (a merge's conflict markers, say): refused, and left as it was.
        const string broken = "<<<<<<< HEAD\n{ \"machines\": {} }\n=======\n";
        File.WriteAllText(path, broken);
        var refused = Assert.Throws<InvalidOperationException>(() => PerfBaseline.Rebaseline(path, key, "Debug", [Scene("A", 2.0)], "def5678"));
        Assert.Contains("can't be read", refused.Message);
        Assert.Equal(broken, File.ReadAllText(path));
    }

    [Fact]
    public void A_run_says_the_baseline_could_not_be_read_rather_than_that_there_is_none()
    {
        var path = Path.Combine(_folder, "PerfBaseline.json");
        Assert.False(PerfBaseline.Read(path).Unreadable); // missing is no baseline, not a broken one
        File.WriteAllText(path, "{ not json");
        var (file, unreadable) = PerfBaseline.Read(path);
        Assert.True(unreadable);
        Assert.Empty(file.Machines);

        var note = ShortPerfTests.Note(unreadable, null, null, "Debug");
        Assert.Contains("couldn't be read", note);
        Assert.DoesNotContain("no baseline for this machine", note);
        Assert.Equal("no baseline for this machine (Debug)", ShortPerfTests.Note(false, null, null, "Debug"));
    }

    [Fact]
    public void A_run_in_another_build_configuration_is_not_compared()
    {
        var key = Machine.Key();
        var file = new BaselineFile();
        PerfBaseline.Replace(file, key, "Release", [Scene("A", 0.5)], "abc1234");
        Assert.Null(PerfBaseline.Entry(file, key, "Debug"));

        PerfBaseline.Replace(file, key, "Debug", [Scene("A", 1.0)], "abc1234");
        Assert.Equal(0.5, PerfBaseline.Entry(file, key, "Release")!.Scenes["A"].Parts["Step"].Median, 9);
        Assert.Equal(1.0, PerfBaseline.Entry(file, key, "Debug")!.Scenes["A"].Parts["Step"].Median, 9);
    }

    [Fact]
    public void A_scene_that_fails_its_checks_measures_nothing()
    {
        var runs = 0;
        var scene = new PerfScene("broken", () =>
        {
            runs++;
            Timings.Enabled = true;
            Assert.Fail("the stages are not in orbit");
            return null!;
        });
        Assert.ThrowsAny<Exception>(() => ShortPerf.Measure(scene));
        Assert.Equal(1, runs); // stops at the warm-up: the report (and the file) are never reached
        Assert.False(Timings.Enabled); // and the timings are left off
    }
}
