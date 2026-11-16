using System.Text.Encodings.Web;
using System.Text.Json;

namespace TestRun;

/// <summary>
/// One machine's run in one build configuration: when (UTC), on what commit, in how many shards,
/// its wall time, and each executed test's seconds by <c>Class.Name</c>.
/// </summary>
public sealed record TimesEntry(DateTimeOffset Measured, string Commit, int Shards, double WallSeconds, Dictionary<string, double> Tests);

/// <summary>The record file: per machine (by <see cref="Machine.Key()"/>), per build configuration, an entry.</summary>
public sealed class TimesFile
{
    public Dictionary<string, Dictionary<string, TimesEntry>> Machines { get; set; } = [];
}

/// <summary>
/// The record of test times: <c>tests/GameOne.Tests/TestTimes.json</c>, committed, laid out as
/// <c>PerfBaseline.json</c> is. Times from different machines (or builds) aren't comparable, so
/// each machine has an entry of its own per configuration, keyed by a hash that names nothing,
/// and a run replaces only its own.
/// </summary>
public static class TimesRecord
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A file to read and diff, never put in a page: a commit's "+changes" and a theory's
        // quoted arguments are kept as they are rather than escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // The working tree's line ends, as every other file in it has (git stores them as LF).
        NewLine = Environment.NewLine,
    };

    /// <summary>
    /// The file at <paramref name="path"/>, or an empty one if it is missing. A file that is there
    /// but can't be read throws, so that it is never saved over: that would lose every other
    /// machine's entry.
    /// </summary>
    public static TimesFile Load(string path)
    {
        if (!File.Exists(path))
            return new TimesFile();
        try
        {
            return JsonSerializer.Deserialize<TimesFile>(File.ReadAllText(path), Json) ?? new TimesFile();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} is there but can't be read: mend it (or delete it on purpose).", e);
        }
    }

    public static void Save(string path, TimesFile file) => File.WriteAllText(path, JsonSerializer.Serialize(file, Json) + Environment.NewLine);

    /// <summary>
    /// The seconds to record for a run's <paramref name="results"/>, in order of name (so one
    /// record's diff from the last is test by test): each executed test's own. A skipped test keeps
    /// the time <paramref name="old"/> has for it, so a normal run (which skips the long tests)
    /// doesn't drop what a <c>--before-merge</c> run recorded; one in neither has none, and a test
    /// no longer in the results is gone.
    /// </summary>
    public static Dictionary<string, double> Times(TimesEntry? old, IEnumerable<TestTime> results)
    {
        var tests = new Dictionary<string, double>();
        foreach (var test in results.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (test.Outcome != "NotExecuted")
                tests[test.Key] = Math.Round(test.Seconds, 3);
            else if (old is not null && old.Tests.TryGetValue(test.Key, out var before))
                tests[test.Key] = before;
        }
        return tests;
    }

    public static TimesEntry? Entry(TimesFile f, string machine, string configuration) =>
        f.Machines.TryGetValue(machine, out var configurations) && configurations.TryGetValue(configuration, out var entry) ? entry : null;

    /// <summary>Puts <paramref name="entry"/> in as this machine's for <paramref name="configuration"/>, whole: no other entry is touched.</summary>
    public static void Replace(TimesFile f, string machine, string configuration, TimesEntry entry)
    {
        if (!f.Machines.TryGetValue(machine, out var configurations))
            f.Machines[machine] = configurations = [];
        configurations[configuration] = entry;
    }
}
