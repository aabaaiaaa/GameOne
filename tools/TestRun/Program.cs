using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TestRun;

/// <summary>
/// The test runner: builds once, splits the normal suite's classes across several
/// <c>dotnet test</c> processes run at once (Jolt is process-wide, so tests in one process run one
/// at a time), then runs any test marked <c>Run=Alone</c> on its own, checks every listed test gave
/// one result, and reports the totals, the slow tests and, with <c>--record</c>, writes this
/// machine's times into the record. See README.md.
/// </summary>
public static class Program
{
    /// <summary>
    /// The fastest shard count measured that then passed three full runs in a row alike (see
    /// <see cref="DefaultShards"/>). Measured on 22 logical cores, Debug, a full run
    /// each: one process 598.6 s (the record's serial run), 2 334.4 s, 3 256.7 s, 4 215.8 s,
    /// 6 183.3 s, 8 154.9 s; every one 640 passed, 17 skipped, none failed. Eight then ran
    /// 154.6, 157.6 and 157.1 s, alike. Past four the gain shrinks, as the longest classes
    /// (TerrainTileTests' frozen field alone is 100 s or more under load) bound the wall time.
    /// </summary>
    public const int Measured = 8;

    /// <summary>The logical cores of the machine <see cref="Measured"/> was measured on.</summary>
    public const int MeasuredOnCores = 22;

    /// <summary>
    /// How many processes share the suite unless <c>--shards</c> says: <see cref="Measured"/>,
    /// scaled down on a machine with fewer cores, never up (more than was measured is untried).
    /// </summary>
    public static int DefaultShards(int cores) => Math.Clamp(Measured * cores / MeasuredOnCores, 1, Measured);

    /// <summary>The build whose times the record keeps: what a plain <c>dotnet test</c> builds.</summary>
    private const string Configuration = "Debug";

    private const string TestProject = "tests/GameOne.Tests";
    private const string RecordFile = "tests/GameOne.Tests/TestTimes.json";

    /// <summary>
    /// Kept out of every test process: the perf and soak tests never run beside others, and the
    /// record is of headless runs only, so a variable left set in the shell mustn't reach them.
    /// <c>GAMEONE_LONG</c> too, so that a normal run is the normal suite whatever the shell has;
    /// <c>--before-merge</c> sets it again for the test processes.
    /// </summary>
    private static readonly string[] KeptOut = ["GAMEONE_PERF", "GAMEONE_PERF_REBASELINE", "GAMEONE_SOAK", "GAMEONE_WATCH", "GAMEONE_LONG"];

    /// <summary>
    /// How long one test may run before its process is stopped and the test named as hung
    /// (<c>--blame-hang-timeout</c>), rather than the run waiting for ever. Well over the longest
    /// test (the whole frozen field, about 94 s alone, and slower under a full machine's load), so
    /// that only a test that has stopped getting anywhere reaches it.
    /// </summary>
    private const string HangTimeout = "10m";

    private static readonly List<Process> Running = [];
    private static volatile bool _stopped;

    public static int Main(string[] args)
    {
        if (!TryParse(args, out var shards, out var beforeMerge, out var record, out var machineKey, out var keep))
        {
            Console.Error.WriteLine("usage: TestRun [--shards N] [--before-merge] [--record] [--keep-results DIR] [--machine-key]");
            return 2;
        }
        if (machineKey)
        {
            Console.WriteLine(Machine.Key());
            return 0;
        }
        if (record && shards != DefaultShards(Environment.ProcessorCount))
        {
            // Times taken under another load aren't comparable with the record's, and the limit is
            // worked out from times at the default count: such a record would mislead every later run.
            Console.Error.WriteLine($"--record takes the record at the default process count ({DefaultShards(Environment.ProcessorCount)} here), so it can't be given --shards {shards}: run without --shards to record.");
            return 2;
        }
        if (KeepRefusal(keep) is { } refusal)
        {
            Console.Error.WriteLine(refusal);
            return 2;
        }

        var repo = RepoRoot();
        // The results go to a temporary folder deleted at the end, unless they are to be kept.
        var temp = keep ?? Path.Combine(Path.GetTempPath(), "GameOne.TestRun", Guid.NewGuid().ToString("N"));
        Console.CancelKeyPress += (_, e) =>
        {
            // Let Main carry on to its clean-up (the temp folder, the build servers) once every
            // shard has been stopped, rather than ending here and leaving test hosts running.
            e.Cancel = true;
            Stop();
        };
        try
        {
            return Run(repo, temp, shards, beforeMerge, record, kept: keep is not null);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Stopped.");
            return 1;
        }
        finally
        {
            Stop();
            try
            {
                if (keep is null && Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A test host still letting go of its results, say: the build servers are still shut down.
                Console.Error.WriteLine($"{temp} couldn't be deleted: {e.Message}");
            }
            // Not through Launch, which starts nothing once the run is stopped: the build
            // servers are shut down however it ended.
            using var shutdown = Process.Start(Info(repo, ["build-server", "shutdown"]))!;
            // Both streams read at once, so that neither fills its pipe while the other is waited on.
            var errors = shutdown.StandardError.ReadToEndAsync();
            shutdown.StandardOutput.ReadToEnd();
            shutdown.WaitForExit();
            if (shutdown.ExitCode != 0)
                Console.Error.WriteLine($"dotnet build-server shutdown exited {shutdown.ExitCode}: {errors.Result}");
        }
    }

    /// <summary>
    /// Whether a run passed: every one of <paramref name="shardsRun"/> processes gave results (a
    /// process that crashed, or whose filter matched nothing, gives none) and no test failed.
    /// </summary>
    public static bool Passed(int shardsRun, IReadOnlyList<IReadOnlyList<TestTime>> results) =>
        results.Count == shardsRun && results.All(shard => shard.Count > 0 && shard.All(test => !IsFailure(test)));

    /// <summary>Anything but passed or skipped (failed, or a TRX's error, timeout or abort) is a failure.</summary>
    private static bool IsFailure(TestTime test) => test.Outcome is not ("Passed" or "NotExecuted");

    private static int Run(string repo, string temp, int shardCount, bool beforeMerge, bool record, bool kept)
    {
        if (Exec(Info(repo, ["build", "GameOne.slnx", "--disable-build-servers", "-nodeReuse:false"]), quiet: false) != 0)
        {
            // A build stopped by Ctrl+C exits non-zero too, and is no failed build.
            if (_stopped)
                throw new OperationCanceledException();
            Console.Error.WriteLine("The build failed: no tests run.");
            return 1;
        }

        var alone = List(repo, "Run=Alone");
        var longTests = List(repo, "Run=Long");
        var listed = List(repo, null);
        if (alone is null || longTests is null || listed is null)
            return 1;
        var testsByClass = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (className, tests) in listed)
        {
            // A class is sharded for its tests that don't run alone, so a shard never holds a
            // class whose every test its filter leaves out (and so no results).
            var left = tests.Except(alone.GetValueOrDefault(className) ?? []).ToList();
            if (left.Count > 0)
                testsByClass[className] = left;
        }
        if (testsByClass.Count == 0)
        {
            Console.Error.WriteLine("dotnet test --list-tests listed no tests.");
            return 1;
        }

        var recordPath = Path.GetFullPath(Path.Combine(repo, RecordFile));
        TimesFile? times;
        try
        {
            times = TimesRecord.Load(recordPath);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"{e.Message} Balancing by the number of tests instead.");
            times = null;
        }
        var key = Machine.Key();
        var entry = times is null ? null : TimesRecord.Entry(times, key, Configuration);
        // A normal run skips the long tests, so their recorded times are no work for the shards.
        var skipped = beforeMerge ? [] : longTests.SelectMany(c => c.Value.Select(test => $"{c.Key}.{test}")).ToHashSet();
        var shards = Shards.Balance(Shards.ClassSeconds(entry, testsByClass, skipped), Math.Min(shardCount, testsByClass.Count));

        Directory.CreateDirectory(temp);
        var wall = Stopwatch.StartNew();
        var runs = shards.Select((classes, i) => Start(repo, temp, $"shard{i}", Shards.Filter(classes, "Run!=Alone"), beforeMerge)).ToList();
        var outcomes = runs.Select(r => r.Result).ToList();
        if (alone.Count > 0)
            outcomes.Add(Start(repo, temp, "alone", "Run=Alone", beforeMerge).Result);
        wall.Stop();
        if (_stopped)
            throw new OperationCanceledException();

        var reads = outcomes.Select(o => ReadResults(o.Trx)).ToList();
        var results = reads.Select(r => r.Tests).ToList();
        var all = results.SelectMany(r => r).ToList();
        // Every listed test (the long ones too, a skipped one's result is NotExecuted) has one
        // result, and nothing else does: a class filter that matched too little or too much
        // would otherwise lose a test, or run one twice, unseen.
        var matched = Unmatched(listed, all);
        var passed = Passed(shards.Count + (alone.Count > 0 ? 1 : 0), results) && outcomes.All(o => o.ExitCode == 0)
            && reads.All(r => r.Problem is null) && matched.Missing.Count == 0 && matched.Extra.Count == 0;

        foreach (var (outcome, read) in outcomes.Zip(reads))
        {
            Console.WriteLine(Invariant($"{outcome.Name}: {outcome.Seconds:F1} s, {read.Tests.Count} tests"));
            if (read.Problem is not null)
                Console.WriteLine($"{outcome.Name}'s results can't be read ({read.Problem}), so it counts as failed.");
            if (outcome.ExitCode != 0 || read.Tests.Count == 0)
                Console.WriteLine($"{outcome.Name} exited {outcome.ExitCode}:{Environment.NewLine}{outcome.Output}");
        }
        Console.WriteLine();
        if (kept)
            Console.WriteLine($"Results kept in {temp}");
        // Shown only when there is a mismatch; a crashed shard's whole share would be a long list, so shortened.
        if (matched.Missing.Count > 0 || matched.Extra.Count > 0)
        {
            Print("Listed but with no result", Shortened(matched.Missing));
            Print("A result but not listed (or more than one)", Shortened(matched.Extra));
        }
        Print("Slower than the record", Report.Slower(entry, all));
        Print(Invariant($"Over the limit ({Report.LimitSeconds} s)"), Report.OverLimit(all, Report.LimitSeconds));
        Print("Failed", [.. all.Where(IsFailure).Select(t => $"{t.Key} ({t.Outcome})")]);
        Console.WriteLine(Invariant($"Passed: {all.Count(t => t.Outcome == "Passed")}, failed: {all.Count(IsFailure)}, skipped: {all.Count(t => t.Outcome == "NotExecuted")}, wall time {wall.Elapsed.TotalSeconds:F1} s in {outcomes.Count} processes"));

        if (record)
        {
            if (!passed)
                Console.Error.WriteLine("The run didn't pass, so the record is left as it was.");
            else if (times is null)
                Console.Error.WriteLine($"{recordPath} can't be read, so it isn't saved over (that would lose every other machine's entry).");
            else
            {
                // Taken at the default count (see README.md), as runs are compared with it and the
                // limit is worked out from it in times under the same load. In order of name, so
                // that one record's diff from the last is test by test.
                var tests = TimesRecord.Times(entry, all);
                TimesRecord.Replace(times, key, Configuration,
                    new TimesEntry(DateTimeOffset.UtcNow, Machine.Commit(repo, RecordFile), shards.Count, Math.Round(wall.Elapsed.TotalSeconds, 1), tests));
                TimesRecord.Save(recordPath, times);
                Console.WriteLine($"Recorded: {recordPath} ({Configuration})");
            }
        }
        return passed ? 0 : 1;
    }

    /// <summary>
    /// Why <paramref name="keep"/> can't hold this run's results, or null if it can. Told up front,
    /// as a file there would otherwise only fail later, from Directory.CreateDirectory, mid-run.
    /// </summary>
    public static string? KeepRefusal(string? keep)
    {
        if (keep is null)
            return null;
        if (File.Exists(keep))
            return $"--keep-results {keep}: that is a file, not a folder, so no results can be kept in it.";
        // A shard that left no results would otherwise be read from an earlier run's file of the same name.
        if (Directory.Exists(keep) && Directory.EnumerateFileSystemEntries(keep).Any())
            return $"--keep-results {keep}: the folder isn't empty, so another run's results would be mixed with this one's.";
        return null;
    }

    private static bool TryParse(string[] args, out int shards, out bool beforeMerge, out bool record, out bool machineKey, out string? keep)
    {
        (shards, beforeMerge, record, machineKey, keep) = (DefaultShards(Environment.ProcessorCount), false, false, false, null);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--shards" when i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out shards) && shards > 0:
                    i++;
                    break;
                case "--keep-results" when i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]):
                    // Made full here, as the test processes run in the repo's folder, which needn't be this one.
                    keep = Path.GetFullPath(args[++i]);
                    break;
                case "--before-merge": beforeMerge = true; break;
                case "--record": record = true; break;
                case "--machine-key": machineKey = true; break;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>The folder holding <c>GameOne.slnx</c>, found upwards from where the runner was built.</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GameOne.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("GameOne.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The tests <c>dotnet test --list-tests</c> lists (matching <paramref name="filter"/>, if
    /// given), by class; null, with the reason printed, if it failed or listed a name it can't
    /// place in a class, as either would leave tests out of the run unseen.
    /// </summary>
    private static Dictionary<string, List<string>>? List(string repo, string? filter)
    {
        List<string> args = ["test", TestProject, "--no-build", "--disable-build-servers", "-nodeReuse:false", "--list-tests"];
        if (filter is not null)
            args.AddRange(["--filter", filter]);
        var output = new StringBuilder();
        var exitCode = Exec(TestInfo(repo, args), quiet: true, output);
        if (exitCode != 0)
        {
            if (_stopped)
                throw new OperationCanceledException();
            Console.Error.WriteLine($"dotnet {string.Join(' ', args)} exited {exitCode}, so no tests run:{Environment.NewLine}{output}");
            return null;
        }
        var listing = ParseListing(output.ToString());
        if (listing.Unreadable.Count > 0)
        {
            Console.Error.WriteLine("These listed tests have no class to shard them by, so no tests run:");
            foreach (var test in listing.Unreadable)
                Console.Error.WriteLine("  " + test);
            return null;
        }
        return listing.ByClass;
    }

    /// <summary>What <c>dotnet test --list-tests</c> listed: the tests by class, and any name with no class.</summary>
    public sealed record Listing(Dictionary<string, List<string>> ByClass, List<string> Unreadable);

    /// <summary>
    /// The tests in <c>dotnet test --list-tests</c>'s <paramref name="output"/>: each indented line
    /// after "The following Tests are available:" is a display name (see <see cref="Shards.Split"/>).
    /// </summary>
    public static Listing ParseListing(string output)
    {
        var listing = new Listing([], []);
        var listed = false;
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("The following Tests are available:", StringComparison.Ordinal))
                listed = true;
            else if (listed && line.StartsWith("    ", StringComparison.Ordinal))
            {
                var test = line.Trim();
                if (Shards.Split(test) is not { } split)
                {
                    listing.Unreadable.Add(test);
                    continue;
                }
                if (!listing.ByClass.TryGetValue(split.Class, out var tests))
                    listing.ByClass[split.Class] = tests = [];
                tests.Add(split.Name);
            }
        }
        return listing;
    }

    private sealed record Outcome(string Name, string Trx, int ExitCode, double Seconds, string Output);

    /// <summary>Starts one test process on <paramref name="filter"/>'s tests, its results written to <c>name.trx</c> in <paramref name="temp"/>.</summary>
    private static Task<Outcome> Start(string repo, string temp, string name, string filter, bool beforeMerge)
    {
        // No dump of a hung test host: one of a Jolt process is large, and the hung test's name,
        // which blame gives without it, is what is wanted.
        var start = TestInfo(repo, ["test", TestProject, "--no-build", "--disable-build-servers", "-nodeReuse:false",
            "--filter", filter, "--logger", $"trx;LogFileName={name}.trx", "--results-directory", temp,
            "--blame-hang-timeout", HangTimeout, "--blame-hang-dump-type", "none"]);
        if (beforeMerge)
            start.Environment["GAMEONE_LONG"] = "1";

        var output = new StringBuilder();
        var clock = Stopwatch.StartNew();
        var process = Launch(start, output);
        return Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            clock.Stop();
            lock (Running)
                Running.Remove(process);
            var outcome = new Outcome(name, Path.Combine(temp, $"{name}.trx"), process.ExitCode, clock.Elapsed.TotalSeconds, output.ToString());
            process.Dispose();
            return outcome;
        });
    }

    /// <summary>Runs <paramref name="start"/> to its end, its output shown unless <paramref name="quiet"/>; its exit code.</summary>
    private static int Exec(ProcessStartInfo start, bool quiet, StringBuilder? output = null)
    {
        if (!quiet)
            start.RedirectStandardOutput = start.RedirectStandardError = false;
        using var process = Launch(start, output ?? new StringBuilder());
        process.WaitForExit();
        lock (Running)
            Running.Remove(process);
        return process.ExitCode;
    }

    /// <summary>What the results lack of the listing (<see cref="Missing"/>) and have beyond it (<see cref="Extra"/>), each <c>Class.Name</c> in order.</summary>
    public sealed record Matched(List<string> Missing, List<string> Extra);

    /// <summary>
    /// The listing, class by class, against the results: a listed test with no result is missing,
    /// and a result for a test not listed, or a second result for one, is extra. By whole display
    /// name, a theory's arguments and all, as the listing and the TRX file both give it.
    /// </summary>
    public static Matched Unmatched(IReadOnlyDictionary<string, List<string>> listed, IEnumerable<TestTime> results)
    {
        var left = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (className, tests) in listed)
            foreach (var test in tests)
                left[$"{className}.{test}"] = left.GetValueOrDefault($"{className}.{test}") + 1;
        var extra = new List<string>();
        foreach (var result in results)
        {
            if (left.GetValueOrDefault(result.Key) > 0)
                left[result.Key]--;
            else
                extra.Add(result.Key);
        }
        var missing = left.SelectMany(t => Enumerable.Repeat(t.Key, t.Value));
        return new([.. missing.Order(StringComparer.Ordinal)], [.. extra.Order(StringComparer.Ordinal)]);
    }

    /// <summary>What one process's results file held, and why it couldn't be read if it couldn't.</summary>
    public sealed record Read(IReadOnlyList<TestTime> Tests, string? Problem);

    /// <summary>
    /// The results in <paramref name="path"/>; none if a process left no file (its exit code and
    /// output tell why), and none with the problem if the file can't be read: cut short, say, or of
    /// a shape <see cref="Trx.Read"/> doesn't expect. Any exception is caught, so that one odd file
    /// fails its shard with the reason rather than ending the runner before it reports.
    /// </summary>
    public static Read ReadResults(string path)
    {
        if (!File.Exists(path))
            return new([], null);
        try
        {
            return new(Trx.Read(path), null);
        }
        catch (Exception e)
        {
            return new([], $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>At most 20 of <paramref name="lines"/>, and how many more.</summary>
    private static List<string> Shortened(List<string> lines) =>
        lines.Count <= 20 ? lines : [.. lines.Take(20), $"and {lines.Count - 20} more"];

    private static ProcessStartInfo Info(string repo, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>
    /// A <c>dotnet test</c> with the tests' environment: no MSBuild nodes left behind, and none of
    /// <see cref="KeptOut"/>, whether it runs tests or only lists them.
    /// </summary>
    private static ProcessStartInfo TestInfo(string repo, IEnumerable<string> args)
    {
        var start = Info(repo, args);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var variable in KeptOut)
            start.Environment.Remove(variable);
        return start;
    }

    private static Process Launch(ProcessStartInfo start, StringBuilder output)
    {
        var process = new Process { StartInfo = start };
        if (start.RedirectStandardOutput)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        }
        lock (Running)
        {
            if (_stopped)
                throw new OperationCanceledException("The run was stopped.");
            process.Start();
            Running.Add(process);
        }
        if (start.RedirectStandardOutput)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        return process;
    }

    /// <summary>Stops every process still running, with its children (the test hosts), and starts no more.</summary>
    private static void Stop()
    {
        lock (Running)
        {
            _stopped = true;
            foreach (var process in Running)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                    // It had already exited, or was exiting as it was killed.
                }
            }
        }
    }

    private static void Print(string heading, IReadOnlyList<string> lines)
    {
        Console.WriteLine(lines.Count == 0 ? $"{heading}: none" : $"{heading}:");
        foreach (var line in lines)
            Console.WriteLine("  " + line);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
