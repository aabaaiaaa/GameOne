namespace TestRun;

/// <summary>
/// How the classes are split across processes. By class, as a class's tests often share what
/// they set up; balanced by the record's times, so the processes finish about together.
/// </summary>
public static class Shards
{
    /// <summary>The seconds given a test the record has no time for (a new one, or no record at all).</summary>
    public const double DefaultSeconds = 1.0;

    /// <summary>
    /// The classes in <paramref name="count"/> shards: each class, longest first, into the shard
    /// with the least time so far (ties to the lower index, and equal classes by name, so a split
    /// is the same every run).
    /// </summary>
    public static List<List<string>> Balance(IReadOnlyDictionary<string, double> classSeconds, int count)
    {
        var shards = Enumerable.Range(0, count).Select(_ => new List<string>()).ToList();
        var totals = new double[count];
        foreach (var (name, seconds) in classSeconds.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var emptiest = Array.IndexOf(totals, totals.Min());
            shards[emptiest].Add(name);
            totals[emptiest] += seconds;
        }
        return shards;
    }

    /// <summary>
    /// The <c>dotnet test --filter</c> for these classes, and <paramref name="trait"/> beside them
    /// if given (<c>Run!=Alone</c>, say). Each class ends in a dot, so that <c>FrameTests</c>
    /// doesn't also match <c>FrameFlightTests</c>.
    /// </summary>
    public static string Filter(IEnumerable<string> classes, string? trait)
    {
        var any = string.Join("|", classes.Select(c => $"FullyQualifiedName~{c}."));
        return trait is null ? any : $"({any})&{trait}";
    }

    /// <summary>
    /// A test's display name (<c>Class.Method</c>, with a theory's arguments after it in brackets)
    /// split into its class and the rest; null if it has no class. The class is what comes before
    /// the method's dot, found before the brackets, as the arguments' numbers have dots of their own.
    /// </summary>
    public static (string Class, string Name)? Split(string test)
    {
        var head = test.IndexOf('(') is var bracket and >= 0 ? test[..bracket] : test;
        var dot = head.LastIndexOf('.');
        return dot <= 0 ? null : (test[..dot], test[(dot + 1)..]);
    }

    /// <summary>
    /// Each class's seconds: the sum of its tests' times in <paramref name="entry"/>, with
    /// <see cref="DefaultSeconds"/> for a test it has no time for. A test in the record that is
    /// no longer listed isn't counted, and one in <paramref name="skipped"/> (<c>Class.Name</c>: the
    /// long tests, in a normal run) counts nothing: the record keeps their times from a run before a
    /// merge, and counted here they would leave the shards they are in short of real work.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ClassSeconds(TimesEntry? entry, IReadOnlyDictionary<string, IReadOnlyList<string>> testsByClass, IReadOnlySet<string>? skipped = null) =>
        testsByClass.ToDictionary(
            c => c.Key,
            c => c.Value.Sum(test =>
                skipped is not null && skipped.Contains($"{c.Key}.{test}") ? 0
                : entry is not null && entry.Tests.TryGetValue($"{c.Key}.{test}", out var seconds) ? seconds
                : DefaultSeconds));
}
