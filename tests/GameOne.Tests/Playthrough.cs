using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The switch for long tests (over the test record's limit, <c>Report.LimitSeconds</c>): they only
/// run with <c>GAMEONE_LONG</c> set, which the test runner's <c>--before-merge</c> does. Long
/// playthroughs (<see cref="PlaythroughFactAttribute"/>) and long checks that aren't playthroughs
/// (<see cref="LongFactAttribute"/>) share it.
/// </summary>
internal static class LongTests
{
    /// <summary>Whether long tests run: <c>GAMEONE_LONG</c> is set (and not 0).</summary>
    public static bool Wanted => Environment.GetEnvironmentVariable("GAMEONE_LONG") is not (null or "" or "0");

    /// <summary>The skip a long test is given when they aren't wanted.</summary>
    public const string Skip = "A long test: set GAMEONE_LONG=1 to run it (see README.md).";
}

/// <summary>
/// A <see cref="FactAttribute"/> for a playthrough: a test that flies a whole mission from the pad,
/// as a player would (launch to landing, or to orbit and back). A long one only runs with
/// <c>GAMEONE_LONG</c> set (see <see cref="LongTests"/>); a quick one always runs. Both name what
/// they are, so the playthroughs can be found, and each carries <c>[Trait("Kind", "Playthrough")]</c>
/// (an attribute class cannot pass a trait on). Nothing reads that: it is a hook for a filter, to
/// pick the playthroughs out (<c>--filter Kind=Playthrough</c>). A long one also carries
/// <c>[Trait("Run", "Long")]</c>, by which the test runner knows a normal run skips it. A test in
/// <c>TestRunTests</c> checks both traits are where they belong.
/// </summary>
public sealed class PlaythroughFactAttribute : FactAttribute
{
    private bool _long;

    /// <summary>
    /// Whether this playthrough takes longer than the limit. A property, not a constructor argument,
    /// so a test reads <c>[PlaythroughFact(Long = true)]</c>; the skip is decided as it is set, since
    /// it is set after the constructor has run.
    /// </summary>
    public bool Long
    {
        get => _long;
        init
        {
            _long = value;
            if (value && !LongTests.Wanted)
                Skip = LongTests.Skip;
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for a long check that isn't a playthrough: one whose point takes
/// longer than the limit to show (a whole field of crafts at once, say), kept beside a quicker test
/// of the same rule in the normal suite. It only runs with <c>GAMEONE_LONG</c> set (see <see cref="LongTests"/>),
/// and carries <c>[Trait("Run", "Long")]</c> as a long playthrough does.
/// </summary>
public sealed class LongFactAttribute : FactAttribute
{
    public LongFactAttribute()
    {
        if (!LongTests.Wanted)
            Skip = LongTests.Skip;
    }
}
