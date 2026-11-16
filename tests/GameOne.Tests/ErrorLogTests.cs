using Xunit;

namespace GameOne.Tests;

/// <summary>The error count behind the HUD's error sign: every line written to stderr counts.</summary>
public sealed class ErrorLogTests
{
    [Fact]
    public void Lines_written_to_stderr_are_counted_as_recent_errors()
    {
        ErrorLog.Install();
        ErrorLog.Install(); // a second time changes nothing: each line still counts once
        var total = ErrorLog.Total;
        var recent = ErrorLog.Recent;

        Console.Error.WriteLine("ErrorLogTests: an error, on purpose");
        Console.Error.Write("ErrorLogTests: and another, written in pieces");
        Console.Error.Write('\n');

        Assert.Equal(total + 2, ErrorLog.Total);
        Assert.True(ErrorLog.Recent >= recent + 2, $"{ErrorLog.Recent} recent errors, from {recent}");
    }
}
