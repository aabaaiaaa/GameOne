using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TestRun;

/// <summary>
/// Which machine a measurement was taken on, and on what commit: shared by the record of test
/// times and the short performance tests' baseline, so both key a machine alike.
/// </summary>
public static class Machine
{
    /// <summary>
    /// This machine's key: the SHA-256, in lowercase hex, of its name, its CPU's model and its
    /// number of processors. A hash so that a committed file holds no machine's name, not for
    /// security. The GPU is left out: neither the tests nor the short performance tests touch it.
    /// </summary>
    public static string Key()
    {
        var cpu = "";
        if (OperatingSystem.IsWindows())
            cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string ?? "";
        return Key(Environment.MachineName, cpu, Environment.ProcessorCount);
    }

    /// <summary>The key of a machine with this name, CPU model and number of processors (see <see cref="Key()"/>).</summary>
    public static string Key(string name, string cpu, int processors) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{name}|{cpu}|{processors}")));

    /// <summary>
    /// The commit checked out in <paramref name="repoDir"/> (<c>git rev-parse --short HEAD</c>), or
    /// <c>"unknown"</c>; with <c>"+changes"</c> after it when anything but
    /// <paramref name="excludedFromChanges"/> (the file being written, as paths from the
    /// repository's root) is changed or new in the working tree, as a branch measures with its
    /// change not yet committed: the numbers are then the commit's and those changes', not the
    /// commit's alone. When git can't say whether the tree is changed, it is <c>"+changes?"</c>:
    /// an unknown tree is never recorded as a clean one.
    /// </summary>
    public static string Commit(string repoDir, params string[] excludedFromChanges)
    {
        var head = Git(repoDir, "rev-parse --short HEAD");
        if (head is not { Length: > 0 })
            return "unknown";
        return Changes(repoDir, excludedFromChanges) switch
        {
            null => head + "+changes?",
            { Length: > 0 } => head + "+changes",
            _ => head,
        };
    }

    /// <summary>
    /// What is changed or new in the whole working tree but <paramref name="excluded"/>, as
    /// <c>git status --porcelain</c> lists it; null if git fails. "top" looks from the
    /// repository's root, not only <paramref name="repoDir"/> ("<c>:!:/</c>" is not a pathspec git takes).
    /// </summary>
    public static string? Changes(string repoDir, params string[] excluded) =>
        Git(repoDir, "status --porcelain -- \":(top)\"" + string.Concat(excluded.Select(path => $" \":(top,exclude){path}\"")));

    /// <summary>What git prints, trimmed, run in <paramref name="directory"/>; null if it fails, or takes over 10 s.</summary>
    private static string? Git(string directory, string arguments)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (git is null)
                return null;
            // Read as it runs, so the wait below is what bounds it: a git that hangs (at a prompt,
            // say) with its output still open would block a plain read for ever.
            var output = git.StandardOutput.ReadToEndAsync();
            var errors = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(10_000))
            {
                git.Kill(entireProcessTree: true);
                return null;
            }
            var printed = output.Result.Trim();
            _ = errors.Result;
            return git.ExitCode == 0 ? printed : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
