using System.Globalization;
using System.Xml.Linq;

namespace TestRun;

/// <summary>
/// One test's result: its class (fully qualified), its name (its display name without the class,
/// a theory's arguments included), how long it took, and its outcome as the TRX file says it
/// (<c>Passed</c>, <c>Failed</c>, <c>NotExecuted</c> for a skipped one).
/// </summary>
public sealed record TestTime(string Class, string Name, double Seconds, string Outcome)
{
    /// <summary>How the record names it: <c>Class.Name</c>.</summary>
    public string Key => $"{Class}.{Name}";
}

/// <summary>Reads the results file (<c>--logger trx</c>) that one <c>dotnet test</c> process writes.</summary>
public static class Trx
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>
    /// Every test result in the file, in its order. The class comes from the test's definition
    /// (its <c>TestMethod</c>'s <c>className</c>), not from cutting up the display name, which a
    /// theory's arguments (with dots in their numbers) would get wrong.
    /// </summary>
    public static IReadOnlyList<TestTime> Read(string path)
    {
        var run = XDocument.Load(path).Root!;
        var classes = run.Descendants(Ns + "UnitTest").ToDictionary(
            test => (string)test.Attribute("id")!,
            test => (string)test.Element(Ns + "TestMethod")!.Attribute("className")!);
        return [.. run.Descendants(Ns + "UnitTestResult").Select(result =>
        {
            var className = classes[(string)result.Attribute("testId")!];
            var testName = (string)result.Attribute("testName")!;
            var name = testName.StartsWith(className + ".", StringComparison.Ordinal) ? testName[(className.Length + 1)..] : testName;
            var duration = result.Attribute("duration") is { } d ? TimeSpan.Parse(d.Value, CultureInfo.InvariantCulture).TotalSeconds : 0;
            return new TestTime(className, name, duration, (string)result.Attribute("outcome")!);
        })];
    }
}
