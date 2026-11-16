using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace GameOne.Tests;

// Inside the namespace, so that TestRun's Program is found before the game's own (top-level) one.
using TestRun;

/// <summary>
/// The test runner (<c>tools/TestRun</c>), running nothing: how it splits the classes across
/// processes and filters each one's share, when a run has passed, what it reports as slower than
/// the record, and that the record and a TRX file read back as written.
/// </summary>
public sealed class TestRunTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GameOne.Tests", Guid.NewGuid().ToString("N"));

    public TestRunTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_file_given_for_keep_results_is_refused_up_front()
    {
        var file = Path.Combine(_folder, "results.txt");
        File.WriteAllText(file, "");
        Assert.Contains("a file, not a folder", Program.KeepRefusal(file));
        Assert.Null(Program.KeepRefusal(Path.Combine(_folder, "none"))); // a folder yet to be made is fine
        Assert.Null(Program.KeepRefusal(null));
    }

    [Fact]
    public void A_class_filter_matches_that_class_alone()
    {
        var filter = Shards.Filter(["GameOne.Tests.FrameTests"], trait: null);
        Assert.Equal("FullyQualifiedName~GameOne.Tests.FrameTests.", filter); // the dot keeps FrameFlightTests out
    }

    [Fact]
    public void Classes_are_joined_and_put_beside_the_trait()
    {
        var filter = Shards.Filter(["A", "B"], trait: "Run!=Alone");
        Assert.Equal("(FullyQualifiedName~A.|FullyQualifiedName~B.)&Run!=Alone", filter);
    }

    [Fact]
    public void Classes_are_balanced_longest_first_into_the_emptiest_shard()
    {
        // Each class, longest first, into the shard with the least time so far (ties to the lower index):
        // A→0 (110), B→1 (65), C→1 (125), D→0 (168), E→1 (135).
        var shards = Shards.Balance(new Dictionary<string, double> { ["A"] = 110, ["B"] = 65, ["C"] = 60, ["D"] = 58, ["E"] = 10 }, 2);
        Assert.Equal(["A", "D"], shards[0]);
        Assert.Equal(["B", "C", "E"], shards[1]);
    }

    [Fact]
    public void With_no_record_classes_are_balanced_by_how_many_tests_they_hold()
    {
        var seconds = Shards.ClassSeconds(null, new Dictionary<string, IReadOnlyList<string>> { ["A"] = ["X", "Y", "Z"], ["B"] = ["W"] });
        Assert.Equal(3 * Shards.DefaultSeconds, seconds["A"]);
        Assert.Equal(1 * Shards.DefaultSeconds, seconds["B"]);
    }

    [Fact]
    public void A_test_new_since_the_record_is_given_the_default_time()
    {
        var entry = new TimesEntry(DateTimeOffset.UnixEpoch, "abc", 1, 10, new() { ["A.Old"] = 4.0, ["A.Gone"] = 2.0 });
        var seconds = Shards.ClassSeconds(entry, new Dictionary<string, IReadOnlyList<string>> { ["A"] = ["Old", "New"] });
        Assert.Equal(4.0 + Shards.DefaultSeconds, seconds["A"]);                            // Gone isn't counted
    }

    [Fact]
    public void A_test_this_run_skips_weighs_nothing()
    {
        // A normal run skips the long tests, so their recorded times would leave their shards
        // underloaded; under --before-merge nothing is skipped and they weigh what they took.
        var entry = new TimesEntry(DateTimeOffset.UnixEpoch, "abc", 1, 10, new() { ["A.Long"] = 30.0, ["A.Quick"] = 2.0 });
        var tests = new Dictionary<string, IReadOnlyList<string>> { ["A"] = ["Long", "Quick"] };
        Assert.Equal(2.0, Shards.ClassSeconds(entry, tests, skipped: new HashSet<string> { "A.Long" })["A"]);
        Assert.Equal(32.0, Shards.ClassSeconds(entry, tests, skipped: new HashSet<string>())["A"]);
    }

    [Fact]
    public void Every_listed_test_has_one_result_and_every_result_was_listed()
    {
        var listed = new Dictionary<string, List<string>> { ["A"] = ["X", "Y(n: 1.5)"], ["B"] = ["Z"] };
        var matched = Program.Unmatched(listed, [new("A", "X", 1, "Passed"), new("A", "Y(n: 1.5)", 0, "NotExecuted"), new("B", "Z", 1, "Passed")]);
        Assert.Empty(matched.Missing);                                                       // a skipped test's result counts
        Assert.Empty(matched.Extra);
    }

    [Fact]
    public void A_listed_test_with_no_result_and_a_result_not_listed_are_named()
    {
        // As if a class filter matched too little (A.Y) and another too much (C.W), and a test ran twice (B.Z).
        var listed = new Dictionary<string, List<string>> { ["A"] = ["X", "Y"], ["B"] = ["Z"] };
        var matched = Program.Unmatched(listed, [new("A", "X", 1, "Passed"), new("B", "Z", 1, "Passed"), new("B", "Z", 1, "Passed"), new("C", "W", 1, "Passed")]);
        Assert.Equal(["A.Y"], matched.Missing);
        Assert.Equal(["B.Z", "C.W"], matched.Extra);
    }

    [Fact]
    public void A_trx_file_of_an_unexpected_shape_is_a_problem_not_a_crash()
    {
        // A result whose test has no definition: Trx.Read throws KeyNotFoundException, not XmlException.
        var path = Path.Combine(_folder, "odd.trx");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="GameOne.Tests.FrameTests.It_holds" duration="00:00:01" outcome="Passed" />
              </Results>
            </TestRun>
            """);

        var read = Program.ReadResults(path);

        Assert.Empty(read.Tests);
        Assert.NotNull(read.Problem);
        Assert.Null(Program.ReadResults(Path.Combine(_folder, "none.trx")).Problem);       // no file is the shard's own failure, told apart
    }

    [Fact]
    public void A_shard_with_no_results_fails_the_run() =>
        Assert.False(Program.Passed(shardsRun: 3, results: [[new TestTime("A", "X", 1, "Passed")], [], [new TestTime("B", "Y", 1, "Passed")]]));

    [Fact]
    public void A_failed_test_fails_the_run() =>
        Assert.False(Program.Passed(1, [[new TestTime("A", "X", 1, "Failed")]]));

    [Fact]
    public void A_run_with_every_shard_s_tests_passed_or_skipped_passes() =>
        Assert.True(Program.Passed(2, [[new TestTime("A", "X", 1, "Passed")], [new TestTime("B", "Y", 0, "NotExecuted")]]));

    [Fact]
    public void A_listing_is_read_class_by_class()
    {
        // As dotnet test --list-tests prints it: a theory's arguments may hold dots and quotes of their own.
        var listing = Program.ParseListing("""
            Test run for C:\GameOne\tests\GameOne.Tests\bin\Debug\net10.0\GameOne.Tests.dll (.NETCoreApp,Version=v10.0)
            The following Tests are available:
                GameOne.Tests.AirTests.The_noon_sky_is_blue
                GameOne.Tests.EncounterTests.It_says_where_it_will_be_when(climb: -30, speedFactor: 0.949999988)
                GameOne.Tests.FloeTests.Landing_on_a_floe_is_not_touching_the_water(sea: "Boreal Sea")
                GameOne.Tests.FloeTests.Landing_on_a_floe_is_not_touching_the_water(sea: "Austral Sea")
            """);

        Assert.Empty(listing.Unreadable);
        Assert.Equal(["GameOne.Tests.AirTests", "GameOne.Tests.EncounterTests", "GameOne.Tests.FloeTests"], listing.ByClass.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["The_noon_sky_is_blue"], listing.ByClass["GameOne.Tests.AirTests"]);
        Assert.Equal(["It_says_where_it_will_be_when(climb: -30, speedFactor: 0.949999988)"], listing.ByClass["GameOne.Tests.EncounterTests"]);
        Assert.Equal(["Landing_on_a_floe_is_not_touching_the_water(sea: \"Boreal Sea\")", "Landing_on_a_floe_is_not_touching_the_water(sea: \"Austral Sea\")"],
            listing.ByClass["GameOne.Tests.FloeTests"]);
    }

    [Fact]
    public void A_listed_name_with_no_class_is_reported_not_dropped()
    {
        var listing = Program.ParseListing("The following Tests are available:\r\n    Orphan(n: 1.5)\r\n    GameOne.Tests.AirTests.X\r\n");
        Assert.Equal(["Orphan(n: 1.5)"], listing.Unreadable);
        Assert.Equal(["X"], listing.ByClass["GameOne.Tests.AirTests"]);
    }

    [Fact]
    public void The_shards_scale_with_the_cores_up_to_the_measured_count()
    {
        Assert.Equal(Program.Measured, Program.DefaultShards(Program.MeasuredOnCores));
        Assert.Equal(Program.Measured, Program.DefaultShards(4 * Program.MeasuredOnCores));   // never more than was measured
        Assert.Equal(1, Program.DefaultShards(1));
    }

    [Fact]
    public void The_limit_is_the_highest_that_keeps_the_wall_time_within_a_tenth_of_five_seconds()
    {
        // In two shards. At L = 5 the tests over it count Shards.DefaultSeconds (1 s): D's 20 s against A, B and
        // C together (10 s), so 20 s. B.Mid (6 s) kept from L = 6 still leaves 20 s (D against 15 s). A.Long
        // (9 s) kept from L = 9 makes A 10 s, and A, B and C's 23 s is over 22 s. So 8.
        var record = new TimesEntry(DateTimeOffset.UnixEpoch, "abc", 1, 100, new()
        {
            ["A.Long"] = 9, ["A.Short"] = 1,
            ["B.Mid"] = 6, ["B.Short"] = 1,
            ["C.X(n: 1.5)"] = 3, ["C.Y"] = 3,
            ["D.Z"] = 4, ["D.W"] = 4, ["D.V"] = 4, ["D.U"] = 4, ["D.T"] = 4,
        });
        Assert.Equal(8, Report.LimitFor(record, shards: 2, notNormal: new HashSet<string>()));

        // A long or Run=Alone test isn't in the normal suite's shards, so it moves no estimate. F's eight
        // 5 s tests left in would make 40 s at every L and fill a shard, against which A to D's 43 s at
        // L = 10 is within a tenth: so 10.
        var notNormal = new HashSet<string>();
        for (var i = 0; i < 8; i++)
            notNormal.Add($"F.T{i}");
        foreach (var test in notNormal)
            record.Tests[test] = 5;
        Assert.Equal(8, Report.LimitFor(record, shards: 2, notNormal));
        Assert.Equal(10, Report.LimitFor(record, shards: 2, notNormal: new HashSet<string>()));
    }

    [Fact]
    public void A_test_much_slower_than_its_record_is_reported_and_one_a_little_slower_is_not()
    {
        var entry = new TimesEntry(DateTimeOffset.UnixEpoch, "abc", 1, 10, new() { ["A.Slow"] = 2.0, ["A.Same"] = 2.0 });
        var slower = Report.Slower(entry, [new("A", "Slow", 4.0, "Passed"), new("A", "Same", 2.2, "Passed")]);
        Assert.Single(slower);
        Assert.Contains("A.Slow", slower[0]);
    }

    [Fact]
    public void The_machine_key_is_the_hash_of_its_name_cpu_and_processors()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("box|cpu|4")));
        Assert.Equal(expected, Machine.Key("box", "cpu", 4));
    }

    [Fact]
    public void The_record_round_trips()
    {
        var path = Path.Combine(_folder, "TestTimes.json");
        var measured = new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);
        var file = new TimesFile();
        TimesRecord.Replace(file, "other", "Debug", new TimesEntry(measured, "0ther00", 1, 5, new() { ["B.Y"] = 1.0 }));
        TimesRecord.Replace(file, "key", "Debug", new TimesEntry(measured, "abc1234", 3, 250.5, new() { ["A.X"] = 1.25, ["A.Y(n: 2)"] = 0.5 }));
        TimesRecord.Save(path, file);

        var loaded = TimesRecord.Load(path);
        var entry = TimesRecord.Entry(loaded, "key", "Debug");
        Assert.NotNull(entry);
        Assert.Equal(measured, entry.Measured);
        Assert.Equal("abc1234", entry.Commit);
        Assert.Equal(3, entry.Shards);
        Assert.Equal(250.5, entry.WallSeconds);
        Assert.Equal(new Dictionary<string, double> { ["A.X"] = 1.25, ["A.Y(n: 2)"] = 0.5 }, entry.Tests);
        Assert.NotNull(TimesRecord.Entry(loaded, "other", "Debug"));                        // the other machine's is kept
        Assert.Null(TimesRecord.Entry(loaded, "key", "Release"));
    }

    [Fact]
    public void A_trx_file_is_read_test_by_test()
    {
        var path = Path.Combine(_folder, "shard0.trx");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="GameOne.Tests.FrameTests.It_holds" duration="00:00:01.5000000" outcome="Passed" />
                <UnitTestResult testId="2" testName="GameOne.Tests.FrameTests.It_is_skipped(n: 1.5)" duration="00:00:00" outcome="NotExecuted" />
              </Results>
              <TestDefinitions>
                <UnitTest name="GameOne.Tests.FrameTests.It_holds" id="1">
                  <TestMethod className="GameOne.Tests.FrameTests" name="It_holds" />
                </UnitTest>
                <UnitTest name="GameOne.Tests.FrameTests.It_is_skipped(n: 1.5)" id="2">
                  <TestMethod className="GameOne.Tests.FrameTests" name="It_is_skipped" />
                </UnitTest>
              </TestDefinitions>
            </TestRun>
            """);

        var tests = Trx.Read(path);

        Assert.Equal(2, tests.Count);
        Assert.Equal(new TestTime("GameOne.Tests.FrameTests", "It_holds", 1.5, "Passed"), tests[0]);
        Assert.Equal(new TestTime("GameOne.Tests.FrameTests", "It_is_skipped(n: 1.5)", 0, "NotExecuted"), tests[1]);
    }

    [Fact]
    public void A_skipped_test_keeps_its_old_time_in_the_record_and_a_gone_one_does_not()
    {
        // A normal run skips the long tests; recording from it mustn't drop their times (the balancer
        // would then count them 1 s before a merge). A test no longer in the results is gone.
        var old = new TimesEntry(DateTimeOffset.UnixEpoch, "abc", 1, 10, new() { ["A.Long"] = 30.0, ["A.Ran"] = 4.0, ["A.Gone"] = 2.0 });
        var tests = TimesRecord.Times(old,
        [
            new TestTime("A", "Ran", 1.23456, "Passed"),
            new TestTime("A", "Long", 0, "NotExecuted"),
            new TestTime("A", "New skipped", 0, "NotExecuted"),
        ]);
        Assert.Equal(["A.Long", "A.Ran"], tests.Keys);                                      // in order of name
        Assert.Equal(30.0, tests["A.Long"]);
        Assert.Equal(1.235, tests["A.Ran"]);
    }

    [Fact]
    public void Every_long_test_and_no_other_is_marked_Run_Long()
    {
        // The runner finds the long tests by this trait (an attribute class cannot pass one on), to
        // weigh them nothing in a normal run, which skips them: one left unmarked would unbalance it.
        var unmarked = TestMethods().Where(m => IsLong(m) != HasTrait(m, "Run", "Long")).Select(Name);
        Assert.Empty(unmarked);
        Assert.Contains(TestMethods(), IsLong);                                             // so the check checks something
    }

    [Fact]
    public void Every_playthrough_and_no_other_test_is_marked_Kind_Playthrough()
    {
        // So that --filter Kind=Playthrough finds them all, quick and long, as the README says.
        var unmarked = TestMethods().Where(m => (m.GetCustomAttribute<PlaythroughFactAttribute>() is not null) != HasTrait(m, "Kind", "Playthrough")).Select(Name);
        Assert.Empty(unmarked);
    }

    private static IEnumerable<MethodInfo> TestMethods() =>
        typeof(TestRunTests).Assembly.GetTypes().SelectMany(t => t.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));

    private static bool IsLong(MethodInfo method) =>
        method.GetCustomAttribute<LongFactAttribute>() is not null || method.GetCustomAttribute<PlaythroughFactAttribute>() is { Long: true };

    /// <summary>A trait on the method or its class, read as xUnit reads it (its constructor's arguments).</summary>
    private static bool HasTrait(MethodInfo method, string name, string value) =>
        method.GetCustomAttributesData().Concat(method.DeclaringType!.GetCustomAttributesData())
            .Any(a => a.AttributeType == typeof(TraitAttribute) && (string?)a.ConstructorArguments[0].Value == name && (string?)a.ConstructorArguments[1].Value == value);

    private static string Name(MethodInfo method) => $"{method.DeclaringType!.FullName}.{method.Name}";

    [Fact]
    public void A_long_playthrough_is_skipped_unless_asked_for()
    {
        // The environment is read at construction, as PerfFact's is. The test sets it itself, so it
        // holds in a normal run and under --before-merge (which sets GAMEONE_LONG for the run).
        var was = Environment.GetEnvironmentVariable("GAMEONE_LONG");
        try
        {
            Environment.SetEnvironmentVariable("GAMEONE_LONG", null);
            Assert.NotNull(new PlaythroughFactAttribute { Long = true }.Skip);
            Assert.Null(new PlaythroughFactAttribute { Long = false }.Skip);

            Environment.SetEnvironmentVariable("GAMEONE_LONG", "1");
            Assert.Null(new PlaythroughFactAttribute { Long = true }.Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GAMEONE_LONG", was);
        }
    }

    [Fact]
    public void A_long_check_is_skipped_unless_asked_for()
    {
        // As the long playthroughs are, by the same switch: set and put back here, so it holds in a
        // normal run and under --before-merge alike.
        var was = Environment.GetEnvironmentVariable("GAMEONE_LONG");
        try
        {
            Environment.SetEnvironmentVariable("GAMEONE_LONG", null);
            Assert.Equal("A long test: set GAMEONE_LONG=1 to run it (see README.md).", new LongFactAttribute().Skip);
            Environment.SetEnvironmentVariable("GAMEONE_LONG", "0");
            Assert.NotNull(new LongFactAttribute().Skip);

            Environment.SetEnvironmentVariable("GAMEONE_LONG", "1");
            Assert.Null(new LongFactAttribute().Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GAMEONE_LONG", was);
        }
    }
}
