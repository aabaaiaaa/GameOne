using System.Diagnostics;
using System.Text;

namespace GameOne;

/// <summary>
/// Counts the errors logged, so the HUD can show that something went wrong (and a test can see
/// it): every line written to <see cref="Console.Error"/> once <see cref="Install"/> has been
/// called, whoever wrote it, the game or a library. The lines still go to the console as before;
/// this only notes when each one was written.
/// </summary>
internal static class ErrorLog
{
    /// <summary>How long an error counts as recent, for the HUD's sign.</summary>
    public const double RecentSeconds = 5;

    private static readonly Lock Gate = new();
    private static readonly Queue<long> Times = new();
    private static int _total;
    private static bool _installed;

    /// <summary>Puts a counting writer in front of <see cref="Console.Error"/> (once; calling it again does nothing).</summary>
    public static void Install()
    {
        lock (Gate)
        {
            if (_installed)
                return;
            _installed = true;
            Console.SetError(TextWriter.Synchronized(new Counting(Console.Error)));
        }
    }

    /// <summary>Notes an error now. (Writing a line to <see cref="Console.Error"/> does this.)</summary>
    public static void Note()
    {
        lock (Gate)
        {
            Times.Enqueue(Stopwatch.GetTimestamp());
            _total++;
        }
    }

    /// <summary>How many errors in the last <see cref="RecentSeconds"/>.</summary>
    public static int Recent
    {
        get
        {
            lock (Gate)
            {
                while (Times.Count > 0 && Stopwatch.GetElapsedTime(Times.Peek()).TotalSeconds > RecentSeconds)
                    Times.Dequeue();
                return Times.Count;
            }
        }
    }

    /// <summary>How many errors since the game started.</summary>
    public static int Total
    {
        get
        {
            lock (Gate)
                return _total;
        }
    }

    /// <summary>Passes everything on to the console's own error writer, noting each line as it ends.</summary>
    private sealed class Counting(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            if (value == '\n')
                Note();
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            if (value is null)
                return;
            foreach (var c in value)
                if (c == '\n')
                    Note();
        }

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            Note();
        }

        public override void Flush() => inner.Flush();
    }
}
