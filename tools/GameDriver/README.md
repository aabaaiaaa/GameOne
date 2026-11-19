# GameDriver

Drives a windowed game from a script, for checking changes in the running game: it launches the
game, plays timed key presses and mouse drags at its window, takes screenshots, then closes it
cleanly and reports the exit code and everything the game wrote to stdout and stderr. Windows only.

Nothing in it is specific to GameOne, so it should work for any game whose window is a normal
Win32 window. By default it looks for a GLFW window (class `GLFW30`), which is what BRUTAL's
`Brutal.Glfw` creates.

```sh
dotnet run tools/GameDriver/drive.cs -- <exe> <script> [--out <dir>] [--title <title>] [--class <class>]

# e.g.
dotnet run tools/GameDriver/drive.cs -- src/GameOne/bin/Debug/net10.0/GameOne.exe tools/GameDriver/examples/gameone-launch.txt --out shots
```

- `--out`: where screenshots go (default: the current directory).
- `--title`: the window's title (default: the exe's name).
- `--class`: the window's class (default: `GLFW30`).

The game inherits the driver's environment, so a run can set test switches for it.

## Scripts

To drive a window the driver didn't start (a flight test being watched, say), attach to it by the
start of its title instead of giving an exe; it is left running unless the script says `quit`:

```sh
dotnet run tools/GameDriver/drive.cs -- --attach "GameOne: watching" tools/GameDriver/examples/watch-orbit-test.txt --out shots
```

One step per line, `<seconds> <command> [arguments]`, with `#` starting a comment and `"double
quotes"` keeping an argument with spaces together. Times count from when the window first becomes
visible (once the game has drawn its first frame), or from when the last `until` was met, and
steps run in order. A time is not a delay from the step before: `1 click ...` then `2 shot` takes
the shot a second after the click, and `1 click ...` then `1 shot` takes it the moment after, before
the game has drawn what the click did. So a script with a step timed before the one above it is
refused before it starts, naming the step, as is one with a command it doesn't know (a typo
would otherwise be skipped, and the steps after it act on a game that never did it) (`drive.cs --check <script> [<script> ...]` only checks scripts this way, launching nothing: several in one run, each line it says then starting with the script's file name, exiting 2 if any is refused). See `examples/` (`gameone-readings.txt` waits on readings throughout; `gameone-day.txt` on the clock). `gameone-ice.txt` plays the ice world written by the
test named in its header, clicking its Play by the game's marker for it.

| Command | |
|---|---|
| `down <key>` / `up <key>` | Press or release a key, for keys that are held. |
| `tap <key>` | Press and release a key (held for 60 ms). |
| `drag <dx> <dy> [<x> <y>]` | Drag with the left button by (dx, dy) pixels, from (x, y) pixels off the middle of the window (default: the middle). |
| `rdrag <dx> <dy> [<x> <y>]` | The same with the right button. |
| `mdrag <dx> <dy> [<x> <y>]` | The same with the middle button. |
| `move <x> <y>` | Move the pointer to (x, y) pixels off the middle of the window, e.g. to hover over something. |
| `press <x> <y>` / `release <x> <y>` | The left button down / up there: with `move`s between, a drag held as long as the script likes. |
| `click <x> <y>` | Move there, then press and release the left button, a few frames apart (as a UI button wants), at (x, y) pixels off the middle. |
| `click marker <label>` | Click a marker the game draws over its view, wherever it is at that moment, by the reading `Marker <label>` (see below; a label with spaces in `"quotes"`): waits up to 5 s for it to be on the screen; if it never is, it lists the markers shown, saves `failed.png`, closes the game and the run fails. |
| `rclick <x> <y>` / `rclick marker <label>` | The same with the right button: the pointer moved there first, then the right button down and up a few frames apart, not moving (a right-click menu, say). |
| `dclick <x> <y>` / `dclick marker <label>` | A double click with the left button, as GameOne picks a target (by the marker: waiting for it as `click marker` does): the pointer moved there first, then two presses 80 ms apart, within the double click time. |
| `move marker <label>` | Move the pointer onto a marker, wherever it is (to hover over it), waiting for it as `click marker` does. |
| `drag marker <label> <dx> <dy>` | Drag a marker with the left button by (dx, dy) pixels, from wherever it is once the pointer has arrived on it (as `drag`, over about 0.4 s), waiting for it as `click marker` does. |
| `scroll <notches>` | Turn the mouse wheel: positive away from you, negative towards you. |
| `until <name> <test> <value> [within <s>]` | Wait until the game's reading `<name>` passes the test (see below), for up to `<s>` seconds (default 60). Not met in time: it says what the reading was, saves `failed.png`, closes the game and the run fails (exit 1). |
| `shot [name]` | Save a screenshot as `<name>.png` (default `t<seconds>.png`). |
| `readings [name]` | Save every reading the game shows now (see below) as `<name>.txt` (default `t<seconds>.txt`), a `<name>=<value>` line each, by name: the numbers as text, read from the game's stream rather than off a screenshot, so it doesn't matter where on the screen the game draws them. |
| `quit` | Close the game now, rather than after the last step. |

Keys: letters, digits, `f1`–`f12`, `[` `]` `-` `=` `,` `.`, `space`, `enter`, `escape`, `tab`, `backspace`, `left` `right`
`up` `down`, and `lshift` `rshift` `lctrl` `rctrl` `lalt` `ralt` (`shift`, `ctrl` and `alt` mean
the left ones).

## Readings: waiting on what the game shows

A timed script is brittle: start-up and slow frames move things, and a flight never goes quite
the same way twice. So a game can read out what its HUD shows, and a script wait on it. This is
the driver's whole interface with a game, and it is optional: the driver starts the game with
`GAMEDRIVER_READOUT=1`, and a game that supports it writes a line to stdout whenever a reading
changes, `hud <name>=<value>` (e.g. `hud Altitude=4120 m`), and `hud -<name>` once it is no
longer shown. The driver keeps the latest of each, and leaves those lines out of the stdout it
reports at the end. A game that writes none can still be driven by time; only `until` needs them.

```
0    until Menu = Missions                 # wait for the mission menu before clicking in it
0.3  click marker "Mission: LAUNCH AND LAND"   # its button, by the game's reading of where it is
0    until "Climb past 400 m" = [x] within 30
0    tap x                                 # straight away, once it is ticked
```

The tests: `<` `<=` `>` `>=` compare the reading's first number (`Altitude <= 350` matches
`350 m`). A clock, `m:ss` or `h:mm:ss` (`09:15`, `1:02:03`), is read as one number, of seconds, so
clock readings compare in order: `"Local time" >= 09:00` passes at `09:15`, not at `08:59`. Only
the clock in a reading is read so: `Day 3, 07:42` is 462 s, not 3, so a clock that wraps round
(past midnight) starts again from 0, and a wait for the next day is a text test (`"World time" has
"Day 2"`). `=` and `!=` compare the first number too when the value is a plain number (a clock
isn't: `= 09:15` compares the text), and otherwise compare the text,
ignoring case (`Menu = Game`); `has` looks for the text anywhere in it (`"Message text" has
fuel`). A reading that isn't shown passes no test. Readings come from the game's stdout, so they
need the driver to start the game: not with `--attach`. When an `until` fails, the driver lists
every reading the game had at that moment, so a script can be fixed without guessing.

**Markers:** something drawn over a moving view (a marker on a far-off object, say) is never in
the same place twice, so a script can't click it at a fixed offset. A game that reads out where
each one is, as `Marker <label>` = `<x> <y>` (pixels off the middle of the window, as the
script's clicks are; only while it is on the screen), lets a script click it by name:

```
0    dclick marker "Drone ship"            # double clicked to target it, wherever it is now
0    until Target = "Drone ship" within 5
0    rclick marker "Flown vessel"          # a right click on it, wherever it is
```

The game has to take the click from the window's own mouse events for this, as for any posted
click (see below).

## How it works, and its limits

- **Keys are posted window messages** (`WM_KEYDOWN` / `WM_KEYUP`), so the game needn't be focused and
  you can keep working. GLFW works out which key was pressed from the scan code in the message, not
  the virtual key, so the driver looks scan codes up with `MapVirtualKey`. A game that reads the
  keyboard some other way (raw input, DirectInput) won't see them.
- **The mouse is posted messages too.** A game that follows the pointer from its move events (GLFW's
  cursor-position callback, as GameOne does) sees exactly what the script does. One that asks GLFW
  where the cursor is (`glfwGetCursorPos`) gets the real cursor, which the driver doesn't move.
  Keep the real cursor off the window while a script runs, or its moves will mix in.
  With the real cursor off it, Windows reports the pointer leaving after every posted move, and
  Dear ImGui's GLFW backend then puts its pointer off the screen and, while the window is focused,
  where GLFW last saw the cursor (which BRUTAL refills from the real cursor when the window is
  repainted): a game whose UI is ImGui should tell ImGui where the pointer was last seen in the
  move events itself, as GameOne's `Hud` does, or a posted click can land where the real cursor
  is.
- **Screenshots** use `PrintWindow` with `PW_RENDERFULLCONTENT`, which captures a Vulkan swapchain.
  Copying from the screen gives a white or black window. Now and then (once in about 70 shots, seen
  from orbit at time warp) a shot catches the window as a new frame replaces the last: a straight
  horizontal seam across it, with the picture above and below it from frames a moment apart (where
  the two differ). It isn't in the game's frames: take the shot again.
- **Closing** posts `WM_CLOSE` and waits up to 15 s. A game that hasn't exited by then is killed,
  and the driver reports that.
- **Timing** is wall-clock, so a slow frame shifts what the game sees by that much, and a script
  runs at real speed: wait on readings where it matters. A script reacts to them but has no
  loops, so for anything that needs continuous control (flying a landing, say), test the
  game's logic headless instead, as GameOne's `tests/GameOne.Tests` does with an autopilot. It is
  exact, repeatable, and many times faster than real time. Use the driver for what can only be
  seen or heard.
