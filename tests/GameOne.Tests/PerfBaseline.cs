using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TestRun;

namespace GameOne.Tests;

/// <summary>How a measurement compares with its baseline.</summary>
internal enum Verdict { Better, Worse, Same, NoBaseline }

/// <summary>The baseline file: per machine (by <see cref="Machine.Key()"/>), per build configuration, an entry.</summary>
internal sealed class BaselineFile
{
    public Dictionary<string, Dictionary<string, MachineEntry>> Machines { get; set; } = [];
}

/// <summary>One machine's measurements in one build configuration: when (UTC) and on what commit it was last rebaselined, and each scene's, each with its own stamp (<see cref="ScenePerf.Commit"/>).</summary>
internal sealed record MachineEntry(DateTimeOffset Measured, string Commit, Dictionary<string, ScenePerf> Scenes);

/// <summary>
/// The short performance tests' baseline: <c>PerfBaseline.json</c>, committed beside this file.
/// On main it holds main's numbers; a branch compares with them, and writes its own only on
/// purpose (<c>GAMEONE_PERF_REBASELINE=1</c>). Numbers from different machines (or a Debug and a
/// Release build) aren't comparable, so each machine has an entry of its own per configuration,
/// keyed by a hash that names nothing, and a run compares and replaces only its own.
/// </summary>
internal static class PerfBaseline
{
    /// <summary>Below this (ms per step), a part is too small to time: never better or worse.</summary>
    public const double FloorMs = 0.01;

    /// <summary>The least change that shows, when the runs' spread is smaller: wide, as the test machine's load and power can't be controlled and its runs vary by up to this much.</summary>
    public const double Tolerance = 0.20;

    public static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static string FilePath => Path.Combine(Path.GetDirectoryName(SourcePath())!, "PerfBaseline.json");

    private static string SourcePath([CallerFilePath] string path = "") => path;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A scene kept from an older entry, with no mean (NaN), is written back as it was read.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>
    /// How <paramref name="current"/> compares with <paramref name="baseline"/>, by their medians:
    /// the change as a fraction of the baseline, and "about the same" within the largest of the
    /// two measurements' spreads and <see cref="Tolerance"/>: a noisy baseline set beside a quiet
    /// run is as uncertain as a noisy run. Both under <see cref="FloorMs"/> is too small to time,
    /// so the same. Lower is better (a time), unless <paramref name="higherIsBetter"/> (a speed).
    /// </summary>
    public static (Verdict Verdict, double Change) Compare(PartPerf? baseline, PartPerf current, bool higherIsBetter = false)
    {
        if (baseline is null)
            return (Verdict.NoBaseline, 0);
        if (baseline.Median < FloorMs && current.Median < FloorMs)
            return (Verdict.Same, 0);
        // A baseline under the floor (a part that has grown from nothing) is measured against the
        // floor, so the change is large but finite.
        var change = (current.Median - baseline.Median) / Math.Max(baseline.Median, FloorMs);
        if (Math.Abs(change) <= Math.Max(Math.Max(current.Spread, baseline.Spread), Tolerance))
            return (Verdict.Same, change);
        return ((change > 0) == higherIsBetter ? Verdict.Better : Verdict.Worse, change);
    }

    /// <summary>The file at <paramref name="path"/>, or an empty one if it is missing or unreadable: never throws.</summary>
    public static BaselineFile Load(string path) => Read(path).File;

    /// <summary>
    /// The file at <paramref name="path"/> (an empty one if it is missing or unreadable), and
    /// whether it is there but couldn't be read or parsed (a merge's conflict markers, a hand edit,
    /// an older shape): so a run can say so rather than "no baseline", and a rebaseline can refuse
    /// to save over it. Never throws.
    /// </summary>
    public static (BaselineFile File, bool Unreadable) Read(string path)
    {
        if (!File.Exists(path))
            return (new BaselineFile(), false);
        try
        {
            var file = JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(path), Json);
            return file is { Machines: not null } ? (file, false) : (new BaselineFile(), true);
        }
        catch (Exception)
        {
            return (new BaselineFile(), true);
        }
    }

    public static void Save(string path, BaselineFile file) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, Json) + Environment.NewLine);

    /// <summary>
    /// Writes <paramref name="scenes"/> into this machine's entry in the file at
    /// <paramref name="path"/> (see <see cref="Replace"/>), keeping every other entry. A file that
    /// is there but can't be read is never saved over, as that would lose every other machine's
    /// entry: it throws instead, and leaves the file as it is.
    /// </summary>
    public static void Rebaseline(string path, string machine, string configuration, IEnumerable<ScenePerf> scenes, string commit)
    {
        var (file, unreadable) = Read(path);
        if (unreadable)
            throw new InvalidOperationException(
                $"{path} is there but can't be read, so it wasn't rebaselined: saving over it would lose every other machine's entry. Mend it (or delete it on purpose) and rebaseline again.");
        Replace(file, machine, configuration, scenes, commit);
        Save(path, file);
    }

    public static MachineEntry? Entry(BaselineFile file, string machine, string configuration) =>
        file.Machines.TryGetValue(machine, out var configurations) && configurations.TryGetValue(configuration, out var entry) ? entry : null;

    /// <summary>
    /// Puts <paramref name="scenes"/> into this machine's entry for <paramref name="configuration"/>
    /// (made if missing), replacing those scenes and keeping its others, each scene put in stamped
    /// now (UTC) and <paramref name="commit"/>, as the entry is; the scenes kept keep their own
    /// stamps, so each names the commit it was measured on. No other entry is touched.
    /// </summary>
    public static void Replace(BaselineFile file, string machine, string configuration, IEnumerable<ScenePerf> scenes, string commit)
    {
        if (!file.Machines.TryGetValue(machine, out var configurations))
            file.Machines[machine] = configurations = [];
        var kept = configurations.TryGetValue(configuration, out var old) ? new Dictionary<string, ScenePerf>(old.Scenes) : [];
        var now = DateTimeOffset.UtcNow;
        foreach (var scene in scenes)
            kept[scene.Name] = scene with { Measured = now, Commit = commit };
        configurations[configuration] = new MachineEntry(now, commit, kept);
    }

    /// <summary>
    /// The commit checked out, with <c>"+changes"</c> when anything but the baseline itself is
    /// changed or new (see <see cref="Machine.Commit"/>), as a performance branch
    /// rebaselines with its change not yet committed.
    /// </summary>
    public static string Commit() => Machine.Commit(Path.GetDirectoryName(FilePath)!, BaselineFromRoot);

    /// <summary>What is changed or new in the whole working tree but the baseline itself; null if git fails.</summary>
    internal static string? Changes() => Machine.Changes(Path.GetDirectoryName(FilePath)!, BaselineFromRoot);

    /// <summary>The baseline, from the repository's root: left out of what counts as changed.</summary>
    private const string BaselineFromRoot = "tests/GameOne.Tests/PerfBaseline.json";
}
