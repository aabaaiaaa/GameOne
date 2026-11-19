#:property AllowUnsafeBlocks=true
#:property PublishAot=false
#:package System.IO.Hashing@9.0.0

// GameDriver: runs a windowed game, plays a script of key presses, mouse drags and screenshots at
// it (timed, or waiting on readings the game writes to stdout: "until Altitude >= 400"), then
// closes it cleanly and reports its exit code and output. Windows only.
//
//   dotnet run tools/GameDriver/drive.cs -- <exe> <script> [--out <dir>] [--title <window title>] [--class <window class>]
//   dotnet run tools/GameDriver/drive.cs -- --attach <title prefix> <script> [--out <dir>] [--class <window class>]
//   dotnet run tools/GameDriver/drive.cs -- --check <script> [<script> ...]
//
// With --attach it launches nothing: it waits for a window (of any process) whose title starts with
// the prefix, e.g. a test being watched, plays the script at it, and leaves it running unless the
// script says quit.
//
// With --check it only reads the scripts, saying (and exiting 2) if any is refused, and launches
// nothing. Several are checked in one run, as starting the driver is most of what a check costs;
// with more than one, each line it says starts with the script's file name.
//
// See README.md next to this file for the script format and why it works the way it does.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Scripts and the game's readings write numbers with a '.', whatever the machine's culture, so
// the driver reads and writes them that way throughout (a refusal quotes a time as the script has it).
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var positional = new List<string>();
string outDir = ".", title = "", windowClass = "GLFW30";
string? attach = null;
var checkOnly = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = args[++i]; break;
        case "--title": title = args[++i]; break;
        case "--class": windowClass = args[++i]; break;
        case "--attach": attach = args[++i]; break;
        case "--check": checkOnly = true; break;
        default: positional.Add(args[i]); break;
    }
}
if (checkOnly ? positional.Count < 1 : positional.Count != (attach is null ? 2 : 1))
{
    Console.Error.WriteLine("usage: drive.cs <exe> <script> [--out <dir>] [--title <window title>] [--class <window class>]");
    Console.Error.WriteLine("       drive.cs --attach <title prefix> <script> [--out <dir>] [--class <window class>]");
    Console.Error.WriteLine("       drive.cs --check <script> [<script> ...]");
    return 2;
}

if (checkOnly)
{
    // Every script is checked, so one run names every refused one, not just the first.
    var refused = false;
    foreach (var path in positional)
    {
        var name = positional.Count > 1 ? $"{Path.GetFileName(path)}: " : "";
        var (steps, refusal) = Read(path);
        if (refusal is not null)
        {
            Console.Error.WriteLine(name + refusal);
            refused = true;
        }
        else
            Console.WriteLine($"{name}{steps.Length} steps: ok");
    }
    return refused ? 2 : 0;
}

Directory.CreateDirectory(outDir);
var (script, why) = Read(positional[^1]);
if (why is not null)
{
    Console.Error.WriteLine(why);
    return 2;
}

// What the game reads out, by name (the latest of each), if it does: "hud <name>=<value>" lines
// on its stdout, "hud -<name>" once one has gone. They are kept out of the stdout reported at the
// end: only the latest matter, and a failed "until" lists them.
var readings = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
var output = new StringBuilder();
var started = Stopwatch.StartNew();
var failed = false;

nint window = 0;
if (attach is not null)
{
    // Someone else's window, e.g. a flight test being watched: wait up to two minutes for it.
    for (var i = 0; i < 240 && window == 0; i++)
    {
        window = FindByTitlePrefix(windowClass, attach);
        if (window == 0)
            Thread.Sleep(500);
    }
    if (window == 0)
    {
        Console.WriteLine($"no window of class '{windowClass}' with a title starting '{attach}' appeared");
        return 1;
    }
    for (var i = 0; i < 120 && !IsWindowVisible(window); i++)
        Thread.Sleep(250);
    if (Play())
        PostMessage(window, WM_CLOSE, 0, 0);
    return failed ? 1 : 0;
}

var exe = Path.GetFullPath(positional[0]);
if (title == "")
    title = Path.GetFileNameWithoutExtension(exe); // GLFW games often title their window after themselves

// The game inherits this environment, so a script can be run with test switches set; and it is
// asked to read out what its HUD shows, for "until" to wait on.
var start = new ProcessStartInfo(exe)
{
    WorkingDirectory = Path.GetDirectoryName(exe)!,
    RedirectStandardError = true,
    RedirectStandardOutput = true,
};
start.Environment["GAMEDRIVER_READOUT"] = "1";
var game = new Process { StartInfo = start };
game.OutputDataReceived += (_, e) =>
{
    if (e.Data is not { } line)
        return;
    if (line.StartsWith("hud ", StringComparison.Ordinal))
    {
        var reading = line[4..];
        if (reading.StartsWith('-'))
            readings.TryRemove(reading[1..], out string? _);
        else if (reading.IndexOf('=') is var equals and > 0)
            readings[reading[..equals]] = reading[(equals + 1)..];
    }
    else
        lock (output)
            output.AppendLine(line);
};
game.Start();
game.BeginOutputReadLine();
var stderr = game.StandardError.ReadToEndAsync();

// Find the window by class and title: the process's MainWindowHandle can be its console instead.
for (var i = 0; i < 120 && window == 0 && !game.HasExited; i++)
{
    Thread.Sleep(500);
    window = FindWindow(windowClass, title);
}
if (window == 0)
{
    Console.WriteLine($"no window of class '{windowClass}' titled '{title}' appeared");
    if (!game.HasExited)
        game.Kill();
    return 1;
}
for (var i = 0; i < 120 && !IsWindowVisible(window); i++)
    Thread.Sleep(250);

Play();

PostMessage(window, WM_CLOSE, 0, 0);
if (!game.WaitForExit(15000))
{
    Console.WriteLine("did not exit within 15 s of WM_CLOSE: killed");
    game.Kill();
}
game.WaitForExit(); // and for the last of its output
Console.WriteLine($"exit: {(game.HasExited ? game.ExitCode : -1)}");
Console.WriteLine("stdout:");
Console.WriteLine(output.ToString());
Console.WriteLine("stderr:");
Console.WriteLine(stderr.Result);
if (failed)
    return 1;
return game.HasExited ? game.ExitCode : 1;

// Plays the script at the window, in time. Returns whether it asked to quit (or an "until"
// failed: then the run fails too).
bool Play()
{
    var clock = Stopwatch.StartNew();
    foreach (var step in script)
    {
        var at = double.Parse(step[0], System.Globalization.CultureInfo.InvariantCulture);
        while (clock.Elapsed.TotalSeconds < at)
            Thread.Sleep(5);
        if (!IsWindow(window))
            return false; // the game has gone

        switch (step[1])
        {
            case "until":
                if (!Until(step))
                {
                    failed = true;
                    Screenshot(Path.Combine(outDir, "failed.png"));
                    return true;
                }
                clock.Restart(); // the steps after it count from now
                break;
            case "down": Key(step[2], down: true); break;
            case "up": Key(step[2], down: false); break;
            case "tap":
                Key(step[2], down: true);
                Thread.Sleep(60);
                Key(step[2], down: false);
                break;
            case "click" or "dclick" or "rclick" or "move" or "drag" when step.Length > 2 && step[2] == "marker":
                if (!AtMarker(step))
                {
                    failed = true;
                    Screenshot(Path.Combine(outDir, "failed.png"));
                    return true;
                }
                break;
            case "drag":
                Drag(int.Parse(step[2]), int.Parse(step[3]), Button.Left,
                    step.Length > 5 ? int.Parse(step[4]) : 0, step.Length > 5 ? int.Parse(step[5]) : 0);
                break;
            case "mdrag":
                Drag(int.Parse(step[2]), int.Parse(step[3]), Button.Middle,
                    step.Length > 5 ? int.Parse(step[4]) : 0, step.Length > 5 ? int.Parse(step[5]) : 0);
                break;
            case "rdrag":
                Drag(int.Parse(step[2]), int.Parse(step[3]), Button.Right,
                    step.Length > 5 ? int.Parse(step[4]) : 0, step.Length > 5 ? int.Parse(step[5]) : 0);
                break;
            case "move": Mouse(WM_MOUSEMOVE, 0, int.Parse(step[2]), int.Parse(step[3])); break;
            case "press": Mouse(WM_LBUTTONDOWN, MK_LBUTTON, int.Parse(step[2]), int.Parse(step[3])); break;
            case "release": Mouse(WM_LBUTTONUP, 0, int.Parse(step[2]), int.Parse(step[3])); break;
            case "click" or "rclick":
                Click(int.Parse(step[2]), int.Parse(step[3]), step[1] == "rclick" ? Button.Right : Button.Left);
                break;
            case "dclick":
                Mouse(WM_MOUSEMOVE, 0, int.Parse(step[2]), int.Parse(step[3]));
                Thread.Sleep(80);
                DoubleClick(int.Parse(step[2]), int.Parse(step[3]));
                break;
            case "scroll": Scroll(int.Parse(step[2])); break;
            case "shot": Screenshot(Path.Combine(outDir, (step.Length > 2 ? step[2] : $"t{at}") + ".png")); break;
            case "readings": SaveReadings(Path.Combine(outDir, (step.Length > 2 ? step[2] : $"t{at}") + ".txt")); break;
            case "quit": return true;
            default: Console.WriteLine($"unknown command '{step[1]}'"); break;
        }
    }
    return false;
}

// "readings [<name>]": writes every reading the game shows now to <name>.txt in the out folder, a
// "<name>=<value>" line each, by name: the numbers a shot would show, as text, wherever the game
// happens to draw them (a panel that moves about to keep out of the way is read here, not off a
// screenshot).
void SaveReadings(string path)
{
    if (attach is not null)
        Console.WriteLine("readings: the game's readings come from its stdout, so only when the driver starts it (not with --attach)");
    var lines = readings.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}={r.Value}");
    File.WriteAllLines(path, lines);
}

// "until <name> <op> <value> [within <seconds>]": waits until the game's reading <name> passes the
// test (as a number, its first number, for < <= > >=; = and != compare numbers when both are,
// otherwise the text, ignoring case; "has" looks for the text in it), for up to <seconds> (60 by
// default). Returns whether it did; if not, says what the reading was.
bool Until(string[] step)
{
    if (step.Length < 5)
        throw new ArgumentException($"until wants a name, a test and a value: '{string.Join(' ', step)}'");
    var (name, op, wanted) = (step[2], step[3], step[4]);
    var within = step.Length > 6 && step[5] == "within" ? double.Parse(step[6], System.Globalization.CultureInfo.InvariantCulture) : 60;
    if (attach is not null)
    {
        Console.WriteLine("until: the game's readings come from its stdout, so only when the driver starts it (not with --attach)");
        return false;
    }
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < within)
    {
        if (!IsWindow(window))
            break;
        if (readings.TryGetValue(name, out var value) && Passes(value, op, wanted))
        {
            Console.WriteLine($"{started.Elapsed.TotalSeconds,8:0.0} s  until {name} {op} {wanted}: {value}");
            return true;
        }
        Thread.Sleep(5);
    }
    Console.WriteLine($"until {name} {op} {wanted}: not within {within} s; it read {(readings.TryGetValue(name, out var last) ? $"'{last}'" : "nothing (not shown)")}");
    Console.WriteLine("the game's readings then:");
    foreach (var (shown, value) in readings.OrderBy(r => r.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {shown} = {value}");
    return false;
}

// A click with the left or right button at (x, y) pixels off the middle of the window, not moving:
// over it first, for a few frames, as a UI (ImGui) wants to see the pointer arrive before the
// button goes down, then the button down and up a few frames apart.
void Click(int x, int y, Button button)
{
    var (held, downMessage, upMessage) = button == Button.Right ? (MK_RBUTTON, WM_RBUTTONDOWN, WM_RBUTTONUP) : (MK_LBUTTON, WM_LBUTTONDOWN, WM_LBUTTONUP);
    Mouse(WM_MOUSEMOVE, 0, x, y);
    Thread.Sleep(80);
    Mouse(downMessage, held, x, y);
    Thread.Sleep(80);
    Mouse(upMessage, 0, x, y);
}

// A double click with the left button at (x, y), the pointer already there: two presses 80 ms
// apart, well within the double click time (ImGui's 0.3 s, which GameOne goes by), each its own
// frame or two at 60 fps.
void DoubleClick(int x, int y)
{
    for (var i = 0; i < 2; i++)
    {
        if (i > 0)
            Thread.Sleep(40);
        Mouse(WM_LBUTTONDOWN, MK_LBUTTON, x, y);
        Thread.Sleep(40);
        Mouse(WM_LBUTTONUP, 0, x, y);
    }
}

// "click marker <label>" (or "rclick marker <label>", with the right button, or "dclick marker
// <label>", a double click with the left, as GameOne picks a target): clicks a marker over
// the game's view (something it shows over a moving scene, so it is never in the same place twice),
// wherever it is at that moment, as the game reads it out: "Marker <label>" = "<x> <y>", in pixels
// off the middle of the window, while it is on the screen. It waits up to 5 s for the marker to be
// shown, and follows it if it moves between the pointer arriving and the button going down.
// "move marker <label>" only moves the pointer onto it (to hover there), and "drag marker <label>
// <dx> <dy>" drags it with the left button by (dx, dy) pixels from where it is once the pointer
// has arrived. Returns whether it was there (false too for a step without one label, or once the
// game has closed).
bool AtMarker(string[] step)
{
    var command = step[1];
    var drag = command == "drag";
    if (step.Length != (drag ? 6 : 4) || drag && (!int.TryParse(step[4], out _) || !int.TryParse(step[5], out _)))
    {
        var example = drag ? $"{command} marker \"Node 1\" 40 0" : $"{command} marker \"Drone ship\"";
        Console.WriteLine(step.Length < 4
            ? $"{command} marker: which marker? Give its label, e.g. {example}"
            : $"{command} marker: a label with spaces must be quoted{(drag ? ", then the drag's dx and dy" : "")}: {example}");
        return false;
    }
    var label = step[3];
    var name = $"Marker {label}";
    if (attach is not null)
    {
        Console.WriteLine($"{command} marker: the game's readings come from its stdout, so only when the driver starts it (not with --attach)");
        return false;
    }
    var clock = Stopwatch.StartNew();
    (int X, int Y)? At()
    {
        if (!readings.TryGetValue(name, out var value))
            return null;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) ? (x, y) : null;
    }
    while (At() is null && clock.Elapsed.TotalSeconds < 5 && IsWindow(window))
        Thread.Sleep(5);
    if (!IsWindow(window))
    {
        Console.WriteLine($"{command} marker \"{label}\": the game closed while waiting for the marker");
        return false;
    }
    if (At() is not { } at)
    {
        Console.WriteLine($"{command} marker \"{label}\": not on the screen within 5 s");
        Console.WriteLine("the markers shown then:");
        foreach (var (shown, value) in readings.Where(r => r.Key.StartsWith("Marker ", StringComparison.Ordinal)).OrderBy(r => r.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {shown} = {value}");
        return false;
    }
    Mouse(WM_MOUSEMOVE, 0, at.X, at.Y);
    Thread.Sleep(80);
    at = At() ?? at;
    Mouse(WM_MOUSEMOVE, 0, at.X, at.Y); // where it is now, if it has moved
    if (drag)
    {
        Thread.Sleep(80);
        Drag(int.Parse(step[4]), int.Parse(step[5]), Button.Left, at.X, at.Y);
    }
    else if (command == "dclick")
        DoubleClick(at.X, at.Y);
    else if (command != "move")
    {
        var (held, downMessage, upMessage) = command == "rclick" ? (MK_RBUTTON, WM_RBUTTONDOWN, WM_RBUTTONUP) : (MK_LBUTTON, WM_LBUTTONDOWN, WM_LBUTTONUP);
        Mouse(downMessage, held, at.X, at.Y);
        Thread.Sleep(80);
        Mouse(upMessage, 0, at.X, at.Y);
    }
    Console.WriteLine($"{started.Elapsed.TotalSeconds,8:0.0} s  {command} marker \"{label}\" at {at.X} {at.Y}");
    return true;
}

static bool Passes(string value, string op, string wanted)
{
    var number = FirstNumber(value);
    var target = FirstNumber(wanted);
    var numeric = number is not null && target is not null;
    return op switch
    {
        "<" => numeric && number < target,
        "<=" => numeric && number <= target,
        ">" => numeric && number > target,
        ">=" => numeric && number >= target,
        // A plain number is compared as one ("Altitude = 0" matches "0 m"); anything else as text.
        "=" or "==" => numeric && double.TryParse(wanted, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? number == target
            : string.Equals(value.Trim(), wanted, StringComparison.OrdinalIgnoreCase),
        "!=" => !Passes(value, "=", wanted),
        "has" => value.Contains(wanted, StringComparison.OrdinalIgnoreCase),
        _ => throw new ArgumentException($"until: unknown test '{op}' (< <= > >= = != has)"),
    };
}

// The first number in a reading or a test's value. A clock ("m:ss" or "h:mm:ss", as "09:15" or
// "Day 3, 07:42" end in) is one number, of seconds, so clocks compare in order: "09:15" >= "09:00",
// "08:59" isn't. (Only the time is read as the clock: "Day 3, 07:42" is 462, not 3.)
static double? FirstNumber(string text)
{
    var invariant = System.Globalization.CultureInfo.InvariantCulture;
    var clock = System.Text.RegularExpressions.Regex.Match(text, @"\d+(:\d\d){1,2}(?!\d)");
    if (clock.Success)
        return clock.Value.Split(':').Aggregate(0.0, (seconds, part) => seconds * 60 + double.Parse(part, invariant));
    var match = System.Text.RegularExpressions.Regex.Match(text, @"-?\d+(\.\d+)?");
    return match.Success ? double.Parse(match.Value, invariant) : null;
}

// The script at path: "<seconds> <command> [arguments]" per line; # starts a comment, and "quotes"
// keep an argument with spaces in one. Times count from when the window first becomes visible, i.e.
// once the game has drawn its first frame, or from when the last "until" was met. With why it is
// refused, if it is: a step with no command or one it doesn't know (a typo, skipped when played,
// would leave the rest of the script acting on a game that never did it); or a step timed before the one above it (since the last "until") runs straight
// after it, not that long after: such a script was written as if each time were a delay from the
// step before, and would crowd its steps together (a shot taken the moment after its click, before
// the game has drawn what the click did). So it is refused, saying where, rather than played wrong.
static (string[][] Steps, string? Refusal) Read(string path)
{
    // Said as a refusal, not thrown, so a missing file among several is named and the rest still checked.
    if (!File.Exists(path))
        return ([], $"no such script file ({path})");
    var script = File.ReadAllLines(path)
        .Select(line => line.Split('#')[0].Trim())
        .Where(line => line.Length > 0)
        .Select(Words)
        .ToArray();
    var since = 0.0;
    for (var i = 0; i < script.Length; i++)
    {
        if (!double.TryParse(script[i][0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at))
            return (script, $"step {i + 1} ('{string.Join(' ', script[i])}'): its time isn't a number");
        if (script[i].Length < 2 || !IsCommand(script[i][1]))
            return (script, $"step {i + 1} ('{string.Join(' ', script[i])}'): {(script[i].Length < 2 ? "no command" : $"unknown command '{script[i][1]}'")}");
        var until = script[i].Length > 1 && script[i][1] == "until";
        // "0 until ...": straight after the step above, waiting from then. Any other time is held
        // to the step above like a step's (the until's wait counts from it), so it can't go back.
        if (at < since && !(until && at == 0))
            return (script, $"step {i + 1} ('{string.Join(' ', script[i])}') is at {at} s, before the step above it ({since} s):"
                + " times count from the start, or from the last \"until\", not from the step before");
        since = until ? 0.0 : at;
    }
    return (script, null);
}

// The commands a script's steps may give (see README.md), so a mistyped one is refused by the check
// rather than skipped when played.
static bool IsCommand(string word) => word is "until" or "down" or "up" or "tap" or "drag" or "rdrag" or "mdrag"
    or "move" or "press" or "release" or "click" or "dclick" or "rclick" or "scroll" or "shot" or "readings" or "quit";

// A script line's words: split on spaces, except inside "double quotes".
static string[] Words(string line)
{
    var words = new List<string>();
    var word = new StringBuilder();
    var quoted = false;
    foreach (var c in line)
    {
        if (c == '"')
            quoted = !quoted;
        else if (c == ' ' && !quoted)
        {
            if (word.Length > 0)
                words.Add(word.ToString());
            word.Clear();
        }
        else
            word.Append(c);
    }
    if (word.Length > 0)
        words.Add(word.ToString());
    return [.. words];
}

// A key press or release. GLFW works out which key it was from the scan code in lParam, not from
// wParam, so the scan code has to be right; the arrow keys and right-hand modifiers are "extended".
void Key(string name, bool down)
{
    var (vk, extended) = VirtualKey(name);
    var scanCode = MapVirtualKey(vk, MAPVK_VK_TO_VSC);
    var lParam = 1 | (scanCode << 16) | (extended ? 1 << 24 : 0) | (down ? 0 : (1 << 30) | (1 << 31));
    PostMessage(window, down ? WM_KEYDOWN : WM_KEYUP, vk, lParam);
}

// A drag with the left, right or middle button by (dx, dy) pixels, over about 0.4 s, starting from
// (fromX, fromY) pixels off the middle of the window. The pointer is moved to the start first, for
// games that follow it by its move messages. (GLFW's own cursor-position query reads the real
// cursor, which posted messages don't move.)
void Drag(int dx, int dy, Button button, int fromX, int fromY)
{
    var (cx, cy) = Middle();
    cx += fromX;
    cy += fromY;
    var (held, downMessage, upMessage) = button switch
    {
        Button.Right => (MK_RBUTTON, WM_RBUTTONDOWN, WM_RBUTTONUP),
        Button.Middle => (MK_MBUTTON, WM_MBUTTONDOWN, WM_MBUTTONUP),
        _ => (MK_LBUTTON, WM_LBUTTONDOWN, WM_LBUTTONUP),
    };
    PostMessage(window, WM_MOUSEMOVE, 0, Point(cx, cy));
    Thread.Sleep(30);
    PostMessage(window, downMessage, held, Point(cx, cy));
    for (var k = 1; k <= 20; k++)
    {
        Thread.Sleep(20);
        PostMessage(window, WM_MOUSEMOVE, held, Point(cx + dx * k / 20, cy + dy * k / 20));
    }
    PostMessage(window, upMessage, 0, Point(cx + dx, cy + dy));
}

// One mouse message at (x, y) pixels off the middle of the window: a move, or the left button
// going down or up, so a script can hover, and hold a drag for as long as it likes.
void Mouse(uint message, int keys, int x, int y)
{
    var (cx, cy) = Middle();
    PostMessage(window, message, keys, Point(cx + x, cy + y));
}

(int X, int Y) Middle()
{
    GetClientRect(window, out var client);
    return ((client.Right - client.Left) / 2, (client.Bottom - client.Top) / 2);
}

// Turns the mouse wheel by <notches> clicks: positive away from you (usually zoom in), negative towards.
void Scroll(int notches)
{
    GetClientRect(window, out var client);
    for (var k = 0; k < Math.Abs(notches); k++)
    {
        PostMessage(window, WM_MOUSEWHEEL, (nint)((Math.Sign(notches) * WHEEL_DELTA) << 16), Point((client.Right - client.Left) / 2, (client.Bottom - client.Top) / 2));
        Thread.Sleep(15);
    }
}

// The window's contents (without its title bar) as a PNG. PrintWindow with PW_RENDERFULLCONTENT
// is what captures a Vulkan swapchain; copying from the screen gives a blank window.
void Screenshot(string path)
{
    GetClientRect(window, out var rect);
    int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
    var screen = GetDC(0);
    var memory = CreateCompatibleDC(screen);
    var bitmap = CreateCompatibleBitmap(screen, width, height);
    var old = SelectObject(memory, bitmap);
    PrintWindow(window, memory, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
    var pixels = new byte[width * height * 4];
    var info = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
    GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0);
    SelectObject(memory, old);
    DeleteObject(bitmap);
    DeleteDC(memory);
    ReleaseDC(0, screen);
    WritePng(path, width, height, pixels);
}

static nint Point(int x, int y) => (y << 16) | (x & 0xFFFF);

static (int Vk, bool Extended) VirtualKey(string name) => name.ToLowerInvariant() switch
{
    var c when c.Length == 1 && char.IsAsciiLetterOrDigit(c[0]) => (char.ToUpperInvariant(c[0]), false),
    var f when f.Length >= 2 && f[0] == 'f' && int.TryParse(f[1..], out var n) && n is >= 1 and <= 12 => (0x6F + n, false),
    "space" => (0x20, false),
    "enter" => (0x0D, false),
    "escape" or "esc" => (0x1B, false),
    "tab" => (0x09, false),
    "backspace" => (0x08, false),
    "lshift" or "shift" => (0xA0, false),
    "rshift" => (0xA1, false),
    "lctrl" or "ctrl" => (0xA2, false),
    "rctrl" => (0xA3, true),
    "lalt" or "alt" => (0xA4, false),
    "ralt" => (0xA5, true),
    "left" => (0x25, true),
    "up" => (0x26, true),
    "right" => (0x27, true),
    "down" => (0x28, true),
    "[" or "lbracket" => (0xDB, false),
    "]" or "rbracket" => (0xDD, false),
    "-" or "minus" => (0xBD, false),
    "=" or "equals" => (0xBB, false),
    "," or "comma" => (0xBC, false),
    "." or "period" => (0xBE, false),
    _ => throw new ArgumentException($"unknown key '{name}'"),
};

static void WritePng(string path, int width, int height, byte[] bgra)
{
    // Unfiltered RGB rows, zlib-compressed: the smallest valid PNG writer.
    var raw = new MemoryStream();
    for (var y = 0; y < height; y++)
    {
        raw.WriteByte(0);
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            raw.WriteByte(bgra[i + 2]);
            raw.WriteByte(bgra[i + 1]);
            raw.WriteByte(bgra[i]);
        }
    }
    var compressed = new MemoryStream();
    using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        raw.WriteTo(zlib);

    using var file = File.Create(path);
    file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
    void Chunk(string type, byte[] data)
    {
        file.Write(BigEndian(data.Length));
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        file.Write(typed);
        file.Write(BigEndian((int)System.IO.Hashing.Crc32.HashToUInt32(typed)));
    }
    var header = new byte[13];
    BigEndian(width).CopyTo(header, 0);
    BigEndian(height).CopyTo(header, 4);
    header[8] = 8; // bits per channel
    header[9] = 2; // RGB
    Chunk("IHDR", header);
    Chunk("IDAT", compressed.ToArray());
    Chunk("IEND", []);

    static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}

partial class Program
{
    enum Button { Left, Right, Middle }
    const uint WM_CLOSE = 0x0010, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEWHEEL = 0x020A;
    const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    const int WHEEL_DELTA = 120;
    const int MK_LBUTTON = 1, MK_RBUTTON = 2, MK_MBUTTON = 0x10;
    const uint MAPVK_VK_TO_VSC = 0;
    const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindow(string className, string title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint window, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, System.Text.StringBuilder text, int length);
    delegate bool EnumWindowsProc(nint window, nint state);

    // A window of this class whose title starts with the prefix (titles that change, e.g. with a speed in them).
    static nint FindByTitlePrefix(string windowClass, string prefix)
    {
        nint found = 0;
        EnumWindows((w, _) =>
        {
            var text = new System.Text.StringBuilder(512);
            GetClassName(w, text, text.Capacity);
            if (text.ToString() != windowClass)
                return true;
            text.Clear();
            GetWindowText(w, text, text.Capacity);
            if (!text.ToString().StartsWith(prefix, StringComparison.Ordinal))
                return true;
            found = w;
            return false;
        }, 0);
        return found;
    }
    [DllImport("user32.dll")] static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint window, out RECT rect);

    [DllImport("user32.dll")] static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int MapVirtualKey(int code, uint mapType);
    [DllImport("user32.dll")] static extern nint GetDC(nint window);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    struct RECT { public int Left, Top, Right, Bottom; }
    struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant; }
}
