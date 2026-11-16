using System.Diagnostics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The GameDriver's script check (<c>drive.cs --check</c>: parses a script and exits, launching
/// nothing). It runs the driver as a process, as a developer does, so the tests don't depend on
/// the driver's insides (it is a separate application).
/// </summary>
public sealed class DriverScriptTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "GameDriver", "drive.cs")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("tools/GameDriver/drive.cs not found above the test's folder");
        }
    }

    /// <summary>Runs the driver's check on <paramref name="script"/>: its exit code and what it said on stderr.</summary>
    private static (int Exit, string Error) Check(string script)
    {
        var (exit, _, error) = CheckAll(script);
        return (exit, error);
    }

    /// <summary>Runs the driver's check on all of <paramref name="scripts"/> in one run: its exit code and what it said on stdout and stderr.</summary>
    private static (int Exit, string Output, string Error) CheckAll(params string[] scripts)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            // No build servers or reused MSBuild nodes left running after the tests (CLAUDE.md).
            ArgumentList = { "run", "--disable-build-servers", Path.Combine(Root, "tools", "GameDriver", "drive.cs"), "--", "--check" },
            Environment = { ["MSBUILDDISABLENODEREUSE"] = "1" },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var script in scripts)
            start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }

    private static (int Exit, string Error) CheckText(string text)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text);
            return Check(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_script_whose_times_go_back_is_refused()
    {
        Assert.Equal(2, CheckText("2 shot\n1 shot\n").Exit);
        // Also before an "until" that waits from a time earlier than the step above.
        var (exit, error) = CheckText("2 shot\n1 until Altitude >= 4\n");
        Assert.Equal(2, exit);
        Assert.Contains("step 2", error);
    }

    [Fact]
    public void A_script_that_goes_forward_or_is_straight_after_an_until_is_accepted()
    {
        Assert.Equal(0, CheckText("1 shot\n2 until Altitude >= 4\n0 until Menu = None\n1 shot\n1 shot\n").Exit);
    }

    [Fact]
    public void A_right_click_is_a_command_and_an_unknown_one_is_refused()
    {
        // A right click at a place, and on the flown vessel by its reading, as a script opens its actions.
        Assert.Equal(0, CheckText("1 rclick 0 0\n2 rclick marker \"Flown vessel\"\n3 click marker \"Action: Deploy legs\"\n").Exit);
        var (exit, error) = CheckText("1 shot\n2 rclik 0 0\n");
        Assert.Equal(2, exit);
        Assert.Contains("step 2", error);
        Assert.Contains("unknown command 'rclik'", error);
    }

    // The child's culture can't be set from here on Windows (it is the user's), so this checks
    // the times are quoted as written; the driver pins the invariant culture at its start so they
    // stay that way on a machine that writes 2,5.
    [Fact]
    public void The_refusal_quotes_both_times_as_the_script_writes_them()
    {
        var (_, error) = CheckText("2.5 shot\n1.5 shot\n");
        Assert.Contains("at 1.5 s", error);
        Assert.Contains("(2.5 s)", error);
    }

    [Fact]
    public void Every_example_script_parses()
    {
        // All in one run of the driver, as its start-up is most of what a check costs: it says each
        // script's result on a line starting with its name.
        var scripts = Directory.GetFiles(Path.Combine(Root, "tools", "GameDriver", "examples"), "*.txt");
        Assert.True(scripts.Length > 1);
        var (exit, output, error) = CheckAll(scripts);
        foreach (var script in scripts)
            Assert.True(output.Contains($"{Path.GetFileName(script)}: ") && !error.Contains($"{Path.GetFileName(script)}: "),
                $"{Path.GetFileName(script)}: {error}");
        Assert.Equal(0, exit);
    }

    [Fact]
    public void The_example_scripts_click_the_mission_menu_and_the_world_list_by_their_markers()
    {
        // The mission menu's and the world list's buttons move with their size, the screen's and
        // (the world list's) how many worlds are saved, and their lists scroll when they don't fit,
        // so every click on one, from its "until Menu = Missions" (or Worlds) to the next wait, is
        // by one of its markers: a mission's, a site's or "Change world"; a preset's New, a world's
        // Play or Delete, or the confirmation's. And every click of one of those waits for its
        // screen first, never timed from the start.
        Assert.Equal(0, CheckText("0 until Menu = Missions\n0.5 click marker \"Site: Home\"\n1 click marker \"Mission: LAND ON THE MOON\"\n0 until Menu = None\n").Exit);
        Assert.Equal(0, CheckText("0 until Menu = Worlds\n0.5 click marker \"Delete: Empty world\"\n1 click marker \"Yes, delete it\"\n2 click marker \"New: Empty world\"\n").Exit);
        string[] missions = ["marker \"Mission: ", "marker \"Site: ", "marker \"Change world\""];
        string[] worlds = ["marker \"New: ", "marker \"Play: ", "marker \"Delete: ", "marker \"Yes, delete it\"", "marker \"Yes, reset it\"", "marker \"No\""];
        var scripts = Directory.GetFiles(Path.Combine(Root, "tools", "GameDriver", "examples"), "*.txt");
        var (missionClicks, worldClicks) = (0, 0);
        foreach (var script in scripts)
        {
            string? screen = null;
            foreach (var line in File.ReadLines(script).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0))
            {
                var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 1 && words[1] == "until")
                    screen = line.EndsWith("until Menu = Missions", StringComparison.Ordinal) ? "Missions"
                        : line.EndsWith("until Menu = Worlds", StringComparison.Ordinal) ? "Worlds" : null;
                else if (words.Length > 1 && words[1] == "click")
                {
                    var marker = words.Length > 3 && words[2] == "marker";
                    var of = marker && missions.Any(line.Contains) ? "Missions" : marker && worlds.Any(line.Contains) ? "Worlds" : null;
                    if (screen == "Missions")
                        missionClicks++;
                    else if (screen == "Worlds")
                        worldClicks++;
                    Assert.True(screen == of,
                        screen is not null ? $"{Path.GetFileName(script)}: \"{line}\" clicks the {screen} screen by a place, not one of its markers"
                            : $"{Path.GetFileName(script)}: \"{line}\" clicks the {of} screen without waiting for it (until Menu = {of})");
                }
            }
        }
        Assert.True(missionClicks > 10, $"only {missionClicks} clicks on the mission menu found");
        Assert.True(worldClicks > 10, $"only {worldClicks} clicks on the world list found");
    }

    [Fact]
    public void Of_several_scripts_checked_together_each_refused_one_is_named()
    {
        // Checked in one run, a script refused among others is named, the others still checked, and
        // the run refused.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var (good, bad) = (Path.Combine(dir.FullName, "good.txt"), Path.Combine(dir.FullName, "bad.txt"));
            File.WriteAllText(good, "1 shot\n2 shot\n");
            File.WriteAllText(bad, "2 shot\n1 shot\n");
            var missing = Path.Combine(dir.FullName, "missing.txt");
            var (exit, output, error) = CheckAll(bad, missing, good);
            Assert.Equal(2, exit);
            Assert.Contains("missing.txt: no such script file", error);
            Assert.Contains("bad.txt: step 2", error);
            Assert.Contains("good.txt: 2 steps: ok", output);
            Assert.DoesNotContain("good.txt", error);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
