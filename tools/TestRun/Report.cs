using System.Globalization;

namespace TestRun;

/// <summary>
/// What a run says beside its totals: the tests much slower than the record, and the tests over
/// the limit. It only reports: a run never fails because a test got slower.
/// </summary>
public static class Report
{
    /// <summary>
    /// A normal test should take no longer than this (Debug, headless): <see cref="LimitFor"/> on
    /// the record in eight processes (the record taken at the default count, eight,
    /// on commit 6adeac3, 156.8 s). With every test over L brought down to a second, the longest
    /// shard would be 48.7 s at L = 5, 51.2 s at 6, 53.2 s at 7 (9.1% over 5's), 54.9 s at 8
    /// (12.6%), 55.6 s at 9 and 57.7 s at 10: so 7. The serial record (ba6c528) gave 9, but
    /// <see cref="OverLimit"/> judges a parallel run's times, slower under load, so the limit is
    /// taken from the same kind of times. The 7 was set once, from the measured parallel record
    /// (within the 5 to 10 s asked for), and isn't recomputed at each retake: <see cref="LimitFor"/>
    /// on a later record giving another value (6 s on the record of 4d30681) is expected, and no
    /// reason to change it.
    /// </summary>
    public const double LimitSeconds = 7;

    /// <summary>
    /// Slower by at least this share of the record, and by at least <see cref="SlowerBySeconds"/>,
    /// is reported. Both, as the sub-second tests' noise and the machine's spread from run to run
    /// (a full serial suite took 582 to 799 s across five identical runs) would otherwise flood it.
    /// </summary>
    public const double SlowerByShare = 0.5;

    /// <inheritdoc cref="SlowerByShare"/>
    public const double SlowerBySeconds = 1.0;

    /// <summary>
    /// The limit the record allows in <paramref name="shards"/> processes: for each whole L from 5
    /// to 10 s, the suite's wall time if every test over L were brought down to
    /// <see cref="Shards.DefaultSeconds"/> (converted, or made a long playthrough), estimated as the
    /// longest shard once the record so changed is balanced by class; the highest L whose
    /// estimate is within 10% of L = 5's. A higher limit leaves more tests as they are, so it is
    /// worth having only while the parallel run hardly slows for it. The tests in
    /// <paramref name="notNormal"/> (<c>Class.Name</c>: the long ones and those run alone, which the
    /// record holds too) are left out, as the limit is the normal suite's and they aren't in its shards.
    /// </summary>
    public static double LimitFor(TimesEntry record, int shards, IReadOnlySet<string> notNormal)
    {
        double Estimate(double limit)
        {
            var byClass = new Dictionary<string, double>();
            foreach (var (test, seconds) in record.Tests)
            {
                if (notNormal.Contains(test))
                    continue;
                var className = Shards.Split(test)?.Class ?? test;
                byClass[className] = byClass.GetValueOrDefault(className) + (seconds > limit ? Shards.DefaultSeconds : seconds);
            }
            var totals = Shards.Balance(byClass, shards).Select(shard => shard.Sum(c => byClass[c]));
            return totals.DefaultIfEmpty(0).Max();
        }

        var lowest = Estimate(LowestLimit);
        var chosen = LowestLimit;
        for (var limit = LowestLimit + 1; limit <= HighestLimit; limit++)
            if (Estimate(limit) <= lowest * (1 + LimitAllowance))
                chosen = limit;
        return chosen;
    }

    /// <summary>The range <see cref="LimitFor"/> picks from, and how much longer than at the lowest the suite may take.</summary>
    private const int LowestLimit = 5, HighestLimit = 10;

    /// <inheritdoc cref="LowestLimit"/>
    private const double LimitAllowance = 0.1;

    /// <summary>Each test of <paramref name="run"/> much slower than in <paramref name="record"/>, slowest first.</summary>
    public static IReadOnlyList<string> Slower(TimesEntry? record, IReadOnlyList<TestTime> run)
    {
        if (record is null)
            return [];
        return [.. run
            .Select(test => (test, was: record.Tests.TryGetValue(test.Key, out var seconds) ? seconds : (double?)null))
            .Where(t => t.was is { } was && t.test.Seconds >= was * (1 + SlowerByShare) && t.test.Seconds - was >= SlowerBySeconds)
            .OrderByDescending(t => t.test.Seconds - t.was!.Value)
            .Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.test.Key}: {t.test.Seconds:F1} s (record {t.was:F1} s)"))];
    }

    /// <summary>Each test of <paramref name="run"/> that took longer than <paramref name="limitSeconds"/>, slowest first.</summary>
    public static IReadOnlyList<string> OverLimit(IReadOnlyList<TestTime> run, double limitSeconds) =>
        [.. run
            .Where(test => test.Seconds > limitSeconds)
            .OrderByDescending(test => test.Seconds)
            .Select(test => string.Create(CultureInfo.InvariantCulture, $"{test.Key}: {test.Seconds:F1} s"))];
}
