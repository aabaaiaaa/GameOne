using System.Diagnostics;

namespace GameOne.Tests;

/// <summary>
/// Waiting in a test for the tiles' worker (another thread): bounded, and backing off from a few
/// spins to sleeps, so a long wait doesn't take a whole core from the worker it is waiting on.
/// </summary>
internal static class Waits
{
    /// <summary>How many tries spin before the waits sleep.</summary>
    private const int Spins = 10;

    /// <summary>Waits until <paramref name="done"/>, for at most <paramref name="patience"/>; whether it came.</summary>
    public static bool Until(Func<bool> done, TimeSpan patience)
    {
        var clock = Stopwatch.StartNew();
        for (var tries = 0; !done(); tries++)
        {
            if (clock.Elapsed > patience)
                return done();
            Pause(tries);
        }
        return true;
    }

    /// <summary>A pause between two tries of a wait: a short spin for the first few, then a sleep of a millisecond.</summary>
    public static void Pause(int tries)
    {
        if (tries < Spins)
            Thread.SpinWait(20);
        else
            Thread.Sleep(1);
    }
}
