# CLAUDE.md: driving the running game

What to know when running GameOne through the GameDriver, writing a script for it, or adding to
the HUD's readout. The driver itself is its own application, for any game (see [`README.md`](README.md) here
for its commands and script format); this file is GameOne's side of it.

The driver is for what the player sees and hears. It runs the game, plays a script of keys,
drags and screenshots at it, then closes it and reports the exit code, stdout and stderr. Steps
are timed, or wait on what the HUD shows (`until Altitude >= 400`, `until Menu = Missions`; a
reading with several words is quoted, `until "Orbit speed" >= 40`): prefer waiting on a reading
to guessing a time, as it flies by what a player sees and doesn't break when start-up or a frame
is slow.

```sh
dotnet run tools/GameDriver/drive.cs -- src/GameOne/bin/Debug/net10.0/GameOne.exe tools/GameDriver/examples/gameone-launch.txt --out <dir>
```

## Before and after a run

- **First, make sure the user isn't playing:** the driver finds the window by its title, so with
  the user's own game open (e.g. started with `dotnet run`), it screenshots and sends keys to
  theirs, and its `quit` closes it. Check nothing is running (`tasklist //FI "IMAGENAME eq
  GameOne.exe"`) and, if it is, ask or wait; never drive or close the user's game.
- **Keep test runs out of the user's worlds:** the game saves worlds to the user's local app data.
  Run the driver with `GAMEONE_WORLDS` set to a new, empty scratch folder, so the driver's worlds
  never appear in (or overwrite) the user's, and the clicks below are where the buttons are.
- **Check:** the exit code is 0 and stderr is empty. Look at the screenshots: a red warning sign in
  the middle of the screen means something was written to stderr in the last 5 s (`ErrorLog`).
- **Start-up:** the window is hidden until the first frame is ready. Start-up can take 5–10 s on
  this machine (endpoint security scanning, the OBS Vulkan layer); the driver waits for it.
- **Closing:** close cleanly with `WM_CLOSE` and check the exit code is 0 with nothing on stderr.

## The HUD's readout

The driver's interface is a stream: started with `GAMEDRIVER_READOUT=1` (the driver sets it), a
game writes `hud <name>=<value>` lines to stdout as its readings change. GameOne's side is all in
`src/GameOne/Readout.cs`, which the main loop calls after each frame (only when asked: normal play
makes no readout at all). It reads the flight and the HUD's state (the menu up, the big message:
`Hud.LastMessage`), and gives each reading the name and format the HUD shows it with. Something
new the HUD shows that a script might fly by (a node's delta-v, say) is added there, not in the
HUD's drawing.

Its readings: `Menu` (`Worlds`, `Missions`, `Game`, `Settings`, `Log`, `CarryOn`, `TryAgain` or `None`), `Must choose` (`yes` while a world just picked has nothing chosen in it: Esc and Close leave neither the carry-on screen nor the missions), `Carry on 1`, `Carry on 2`... (the carry-on screen's choices in its order, each its button's text and its line, `Carry on: LAUNCH AND LAND · flight 1 · 1 of 4 / Booster: 412 m, rising 31 m/s, fuel 70%, at the planet (last flown)`), `Notice` (a mission ended while another vessel was flown, while its word shows: `REACH ORBIT (flight 5) complete: 1,800 pts`), `Run` (the flown vessel's run by the log's name, `LAUNCH AND LAND (flight 1)`, with `, complete`, `, failed` or `, abandoned` once ended, until another vessel is flown; `none` for a vessel of none), `Runs in progress` (how many) and `Run <name>` for each (`1 of 4 steps, 0 pts`), `Can rewind` (how far back the rewind goes, in whole seconds of the world's time, or `no`), `Controls` (the
controls list: `folded` or `open`), `HUD` (`hidden`, only while F2 has hidden it: no other HUD reading is then on the screen), `Time` (in slow motion or warp), `Warp` (while the rate isn't 1, which kind of rate runs it: `16384x by formula` or `8x in the physics`), `Warp reached` (in warp, how fast the world really ran, whole: `8x`), `Warp held` (the witnessed vessel holding the warp to the physics' top rate, by the log's name, `Booster 1`, while one flies in the air or low over the ground, or isn't yet at rest), `Warp dropped` (while the time line says why the warp dropped to the physics' top rate: `<reason> <vessel> to <rate>x`, `Air Upper stage 3 to 8x`, the reason one of `Air`, `Ground`, `SetMoving`, `Steering`, `Collision`, `NearMiss`, `Node`, `FuelOut`), `World time` (`Day 3,
07:42`) and `Local time` (`09:15`, where the flown vessel is; the driver reads a clock as one
number of seconds, so `until "Local time" >= 09:00` waits for 09:00), `Pad lights` (how far the
nearest place's lights have faded on, then its name, `1.00, Home's pad`; a script compares the
fraction, `until "Pad lights" >= 1`; from another body, the nearest across the bodies),
`Landing lights` (the flown vessel's own, a fraction alone, `1.00`:
`until "Landing lights" >= 1`), `Body` (the body the flown vessel is at, by its name:
`Planet` or `Moon`; every reading of heights, speeds, tilt, air, orbit, local time and frame is
that body's: on the moon its own), and for each body by its name, `Moon`, `Planet` (the distance
from the flown vessel to that body's datum, the zero of its heights, straight to its centre:
`31250 m`; the body it is at reads its own altitude) and `Moon height`, `Planet height` (its
centre's angle above the flown vessel's horizon, as `Tilt` is given, `23 °`, negative below it:
the body it is at is `-90 °`; from the moon the planet rises and sets), `Vessel`, `Vessel id` (its id, as the log names it), `Position` (where it is in its body's frame, `x y z` in metres, to the centimetre), `Altitude`,
`Vertical`, `Horizontal`, `Tilt`, `Air`, `Ground` (the height over what is under it; `Altitude`
is above the sea, or on a body with none its datum), `Target` (the picked target, by its
marker's name, a body's its own (`Moon`), or `none`; `vessel <id>` for a vessel gone from the world since the flight last
looked), `To target` (the straight line to it), `Frame` (in use), `Frame choice`, `Relative
speed` (to the target), `Apoapsis` (`escaping`, or `leaves its pull` when its top is past where
it would leave its body's pull, out of the moon's into the planet's), `Periapsis`, `Period` (a
lap of the orbit, in seconds, where the HUD shows minutes and seconds; not while it escapes,
leaves its body's pull or only falls back), `Orbit speed needed` (a round orbit's speed where
it is, or at the body's orbit floor if lower), `Orbit speed` (among the stars; shown with the orbit), `Throttle`, `Fuel`, `Delta-v` (the firing stage's fuel left as a speed change in vacuum, `780 m/s`), `Delta-v in all` (with stages above it: every stage still on it burnt in turn), `Stage`, `Legs` (a vessel with legs: `stowed`, `deploying`, `deployed` or `stowing`), `Bay doors` (a vessel with a bay, the lander: `closed`, `opening`, `open` or `closing`), `Hold` (the hold on: `None`, `Prograde`,
`Retrograde`, `Manoeuvre` or `Rings`, the rings' hold a click on a ring sets), `Site` (the launch site, by name), `State` (the flown vessel's `FlightState`: `Ready`,
`Flying`, `Landed`, `Floating` or `Crashed`), `Link` (the communications link-up over the main view, shown as any vessel is switched to or carried on: `connecting`, then `connected` as the picture clears, 1 s of real time from the click, `Scene.LinkUpSeconds`, longer only if the physics takes longer; shown only while it is, so `until Link = connected` waits for the vessel to be flown there), `Link <vessel>` (a small view's own link-up, by the log's name, `Link Upper stage 0`: the same, shown as a view is given to a vessel at a body whose physics isn't made, which it is two frames after the click, the view drawn with its static in the one between), `Camera` (what the main camera is on: `Own`, `Vessel 7`, `Body Moon`, `Site Home pad` or `Point <name>`; right click on a site, vessel or marker in the map, or on another vessel in the chase view, puts it there, M from the map gives its chase view, and `Marker Reset camera` (in the strip, only while it is not `Own`) puts it back), `View` (the main view: `map`, zoomed all the way out, or `chase`), `Camera link` (the same over the main view, as its camera is put on a vessel at a body whose physics isn't made), `Warning` (a fast collision coming, in the HUD's
words: `COLLISION: another vessel in 4.2 s (2,400 m/s)`; shown only while it is), `Near miss` (with "Slow motion for near misses" on, the flown vessel's near miss coming, in the HUD's words: `NEAR MISS: Booster 1 in 3.2 s (1,000 m/s)`; shown from the moment it is foreseen until its closest approach, but not while `Warning` is of the same vessel: a collision by their sizes that misses is one event, its COLLISION line), `Slowed` (the slow-down, for a fast collision or, with the setting on, a near miss: its scale and reason, `0.027x, a collision with Booster 1 (7,915 m/s)` or `0.29x, a near miss with Booster 1`; shown only while it is; the scale is the physics step's, and `Time` the game's clock, the slower of it and the player's own rate), `Coming at <vessel>` (what is coming at each player-controlled vessel, the flown one and each small view's, by the log's names: `Coming at Booster 0` = `Booster 1 in 8.7 s`, or `the ground in 4.9 s`, and a near miss's with `, a near miss` after it; what a small view's border flashes for, red, or amber for a near miss, shown only while it is), `Coming mark` (the main view's mark, red or amber for a near miss, on a vessel coming at the flown one, while it is on the screen: its name and where, `Booster 1 203 23`), `Mission`, each mission step by its label (`[x]`,
`[ ]` or `[!]`), `Mission failed`, `Best altitude` (over the body the flown vessel is at: begun
again as it crosses into another), `Score`, `Message` (the big title) and
`Message text`; `Collisions` (how many predicted collisions' crosses the map drew this frame: 0 unless
zoomed out with some in view; they are looked for a lap of the flown vessel's orbit ahead, so
none with the rocket on the pad: fly something in orbit first, as `Add view` below lets a script);
and with a manoeuvre node, `Node` (seconds to it, where the HUD shows minutes and
seconds), `Delta-v left`, `Burn`, `Node matched` (`yes` while the path flown matches the planned
one, so cutting the engine lets the node go), `Node times` (each node's world time in seconds,
in the chain's order, `1234.5 1890.0`: they change only as the chain is edited, so a script
reads a drag's effect on the nodes after the one dragged), and `Nodes` (how many); `Path marks`
(the labels of the flown vessel's path's marks on the screen, in the HUD's words, `Ap 4483 m /
Pe 3442 m / AN 30.0° / DN 30.0°`: the AN and DN, where a target vessel's or body's plane
crosses the path, with the angle between the planes); while "Show timings" is on, every number
of the timings panel, whole, as `Timings <part>` (`Timings frame`, `Timings Jolt`, `Timings GPU
scene`, `Timings awake`..., `Timings under formula` (the vessels out of the physics), and the
tiles', the contact's and the rules' pieces: `Readout.ReadTimings`), so a
script saves them (`readings <name>`) or waits on them rather than reading the panel, which moves
about to keep clear of the other panels; and each marker drawn on the screen this frame, `Marker
<name>` = `x y` (pixels from the window's middle, as the driver's clicks are; the first drawn of
two with one name), for `click marker` and `dclick marker`; the red mark is listed among them too, by the log's name
(`Marker Booster 1`, which no ordinary marker has), as it is clicked as its vessel's marker. Every
body's marker is among them by the body's name (`Marker Planet`, `Marker Moon`), while it is
small enough on the screen to be marked; any but the flown vessel's own body's targets that body
(double clicked, as every target is: see "Markers" below).
The flown mission's points of interest are among them by name too, LAND ON THE MOON's `Marker
Landing spot` (from the planet as well as round the moon), a target as a site is.
What is drawn over the view to click or drag that isn't a marker reads out the same way, only
while it is on the screen (`Hud.PointsDrawn`): the flown vessel's path's marks, `Marker Path Ap`,
`Path Pe`, `Path AN`, `Path DN`, `Path Air` (the point on the path each marks: a click there,
zoomed out, places a node, and `rclick marker "Path Ap"` warps to it: `Warp to` = `1234.56 in 210.3 s`, the world time it stops at and how long to go, while it runs, with `Marker Path Warp` on the mark where it stops, and `World seconds` the world's clock in seconds to read the stop by); the nodes, `Marker Node 1`, `Node 2`... (dragged with `drag marker
"Node 1" 40 0`); each body's path on the map, `Marker Moon line` (a point on the line away from
the body: a double click there targets it); and, while the steering rings show, `Marker Heading ring`
and `Marker Pitch ring` (a point on each, clear of the other: `move marker "Flown vessel"` shows
them, then `click marker "Heading ring"` sets the rings' hold). The hold buttons are `Marker Hold
Prograde`, `Marker Hold Retrograde` and `Marker Hold Manoeuvre`, and the frame buttons `Marker
Frame Auto`, `Frame Surface`, `Frame Orbit` and `Frame Target`.
The strip's buttons read out the same way, as they move with the instruments' panel: `Marker Add
view` and `Marker Controls` (the folded list's "Controls (H)" too, though neither it nor the open
list is drawn while a menu is up), and while the "Add view" list
is open each entry by its vessel's name, `Marker Add view: Satellite` (the first listed of two with
one name), so a script gives a view with `click marker "Add view"` then `click marker "Add view:
Upper stage"`, wherever they are. The mission menu's buttons read out so too (`Marker Mission:
FREE PLAY`, `Marker Site: Home`, `Marker Change world`, and in a world just picked with something to carry on, `Marker Back`), the carry-on screen's (`Marker Carry on: <mission>` for a run, the first listed of two of one mission; `Marker Carry on: <vessel>` by the log's name, `Carry on: Booster 0`, for a vessel of no run; `Marker New mission`, `Marker Back to worlds`), the game menu's, by their labels without dots (`Marker Resume`, `Marker Restart flight`, `Marker Abandon mission`, then `Marker Confirm abandon`, `Marker Missions`, `Marker World log`, `Marker Change world`, `Marker Settings`, `Marker Quit game`), the try-again window's (`Marker Start again`, `Marker Rewind`, then `Marker Confirm rewind`, `Marker Cancel`) and the mission panel's `Marker Complete mission`, and the world list's (`Marker New: Empty
world`, `Marker Play: <a world's name>`, `Marker Delete: <a world's name>`, `Marker Yes, delete
it`): see "Where things are on the screen".
The flown vessel's actions: `Marker Flown vessel` (its centre of
mass on the main view, while it is on the screen and not zoomed out), so `rclick marker "Flown
vessel"` opens its menu (a vessel with none, a satellite, opens nothing); `Actions` = `open` while
the menu is; and its items as markers by their labels, `Marker Action: Open bay doors` (`Close
bay doors`, `Deploy legs`, `Stow legs`, each as it will do now), so a script clicks one with
`click marker "Action: Open bay doors"` and waits on `until "Bay doors" = open`. A right click
there is the driver's still press (`rclick`): an `rdrag` turns the camera, never opens it.

## Where things are on the screen

The game starts on the world list, and menus are only reached by the mouse (and Esc for the game
menu), so scripts `click` their buttons, at offsets from the window's middle. With an empty
`GAMEONE_WORLDS` folder (1280x720, the size the window opens at; it can't be made smaller than
800x600, the smallest the HUD is laid out for: `GameView.CreateWindow`). Every menu screen keeps
clear of the BRUTAL credits panel in the bottom right (the licence's logo and notice:
`Hud.MenuAcross`): at 1280x720 each is centred, at the places below; on a narrower window one that
would reach the panel moves into the room to its left, narrower if it must, so its places move
too (the world list and the mission menu are clicked by markers, so that doesn't matter there;
the game menu, narrow enough, stays centred, its buttons about 6 px lower than below, as it is
centred on the smaller height):

- **The world list:** clicked by its markers, never by a place: each preset's New, `Marker New:
  Empty world` and `Marker New: Satellites in orbit` (with no saved worlds, about `-175 4` and
  `-175 34` at 1280x720); each saved world's Play (Carry on for the one being played) and Delete by the
  world's name, `Marker Play: <its name>` and `Marker Delete: <its name>` (the first listed of two
  with one name), and while a row asks, `Marker Yes, delete it` (or `Yes, reset it`) and `Marker
  No`. The saved worlds sit in a region that scrolls when they don't all fit on the screen,
  opening at the top each time, the New buttons and Close below it always in view; a row's
  markers show only while its button's middle is in view, so with many worlds a script `scroll`s
  over the list first. Every example script clicks it so, after `until Menu = Worlds`.
- **The mission menu:** clicked by its markers, never by a place: its buttons move with its
  size, which follows the screen's (as wide as the room beside the credits panel allows, which it
  never covers: centred at 1280x720, to the left of the credits at 800x600). At 1280x720 every
  mission is in view; where they don't all fit (800x600) its missions sit in a region that
  scrolls, opening at the top each time. Each mission's button, while its middle is in view, is
  `Marker Mission: <its name>` (`click marker "Mission: LAUNCH AND LAND"`, `"Mission: REACH
  ORBIT"`, `"Mission: DEPLOY A SATELLITE"`, `"Mission: LAND ON THE MOON"`, `"Mission: FREE
  PLAY"`); one scrolled out of view has none, so there a script moves onto the list and
  `scroll`s it in first. The "Launch from" row's sites are `Marker Site: <its name>` (`"Site:
  Home"`, `"Site: Polar"`, `"Site: Highcrest"`, `"Site: Desert"`, `"Site: Coral Isle"`, `"Site:
  Northreach"`, `"Site: Sentinel"`), and "Change world" is `Marker Change world`. Every example
  script clicks it so, after `until Menu = Missions`.
- **The game menu** (with a world): Resume `0 -100`, Restart flight `0 -65` (the left half of
  that row while the flown vessel's mission is in progress, "Abandon mission" the right half: click
  either by its marker; "Abandon mission" asks to be confirmed, a line and "Confirm abandon" under
  that row, which moves the buttons above and below it), "Missions..." `0 -31`, "World log..." `0 3`, "Change world..." `0 37`,
  Settings `0 71`, Quit game `0 109` (the menu is centred on its height, so a button added to it
  moves the others). Restart flight opens the try-again window (`Menu = TryAgain`): click its
  buttons by their markers.
- **The carry-on screen** (`Menu = CarryOn`, after a world with something to carry on is picked):
  clicked by its markers, never by a place; its choices sit in a region that scrolls, as the
  world list's rows do.
- **The world log** (`Menu = Log`): its entries from the top, newest first, one a line; Close at
  its foot, `0 170` (within a few pixels: 168 with an entry, 177 with none).
- **The settings screen:** the "Show timings" checkbox `-160 -16`, "Cap frames at the screen's
  rate" `-160 43` and "Slow motion for near misses" `-160 102` (`gameone-timings.txt` turns timings on, `gameone-timings-crowded.txt` shows the
  panel beside a long mission list, and `gameone-frame-cap.txt` reads the frame rate capped and
  not).
- **Flying:** the folded controls list's "Controls (H)" `-580 329`; the frame selector's buttons
  (top left of the instruments, under the clocks), Auto `-598 -267`, Surface `-551 -267`, Orbit
  `-503 -267` and Target `-458 -267` (about 19 px lower for each line over them under the
  vessel's name: the "COLLISION" warning, and the time line (slow motion, warp or slowed), each
  one, so about 38 px lower with both; off while a menu is up). "Add view" (`-600 328` while there are no small views) opens a list of
  vessels above it (a click elsewhere only closes it): its last entry is at `y 308`, each above it
  19 px higher (the Satellites world's first of four at `-540 250`); and the first view's middle,
  clicked to fly it, `-492 250`. That is how a script flies something already in orbit (the
  Satellites world's), as a scripted ascent can't reach one.
- **A world made for a look:** a test can write one into the scratch folder before the run (as
  `IceWorld` does for `gameone-ice.txt`), and the script plays it by its name (`click marker
  "Play: Sea ice: over the Austral Sea's edge"`).
  `MoonWorld` writes one for `gameone-moon.txt`: two landers landed on the moon (earlier flights'
  vessels, so Tab doesn't reach them: each is flown by giving it a view and clicking that). With
  the new rocket flown, "Add view" lists them at `y 289` and `308`; with one view up, "Add view"
  is at `-600 119` and its list's one entry at `-560 99`, and the second view's middle at
  `-216 250`. The strip's buttons are kept below the instruments' panel (`Hud.StripPlace`), so
  with a panel reaching lower they are lower and the views under them smaller (in the trip's low
  orbit with the moon targeted, its "To target" line, "Add view" is at `-600 134`): click them
  by the readout (`click marker "Add view"`, above), as `gameone-moon-trip.txt` does. Flying a lander there, `Body` reads `Moon` and the HUD's numbers are the moon's
  (altitude over its datum, the ground under it, tilt, speeds, orbit, local time, the compass's
  north); `Planet height` is the planet's height in the moon's sky. `MoonTripWorlds` (the same
  switch) writes the moon trip for `gameone-moon-trip.txt`, six worlds, each the satellite
  launcher's upper stage alone, an earlier flight's (flown as the landers are), and a satellite
  9 km over the planet for a small view there (listed first in "Add view", the stage at
  `y 308`): `moon-trip-low-orbit` (300 m over the air, the moon targeted, its window 90 s
  ahead), `moon-trip-burnt` (the transfer burn just done, on its way to the moon),
  `moon-trip-arriving` (just inside the moon's sphere, on its way round the moon over its
  poles), `moon-trip-moon-orbit` (a round orbit 400 m over the moon's highest ground, over its
  poles and the landing spot), `moon-trip-way-down` (the de-orbit burn just done, falling
  towards the spot with its engine off: with no autopilot it crashes) and `moon-trip-landed`
  (at the landing spot by night, its landing lights on). They are written oldest first in the
  order the script plays them; it plays each by its name (`click marker "Play: The moon trip: a
  low orbit, the moon targeted"`) and deletes it after (`"Delete: ..."`, then `"Yes, delete
  it"`). They are written by their own test, `Writes_the_moon_trip_worlds` (filter on it, so the
  folder holds only those six); `Writes_the_lander_worlds` (the same switch, another folder)
  writes `gameone-lander.txt`'s three, each with the satellite 9 km over the planet too, by day at
  the landing spot with the planet in its sky (`SpotSky.FairTime`, the one of those with the sun
  highest over both the stack and the spot as the lander comes down): `lander-moon-orbit` (the
  lander launcher's stack, the upper stage with the lander on, named "Upper stage" in "Add view",
  on `moon-trip-moon-orbit`'s orbit 60° short of the spot), `lander-way-down` (the lander alone,
  "Lander", its de-orbit burn just done: with no autopilot it crashes) and `lander-landed` (the
  lander on the spot, its legs out, its doors shut), both flown on from that world's release by
  the mission's pilot, so the upper stage it was let go of is on its orbit above in them. The script plays them with LAND ON THE MOON
  chosen, as the spot's marker (`Marker Landing spot`) is that mission's. A
  small view given to a vessel at a body with no physics shows the link-up over it (`Link
  <vessel>`), the physics made behind it once the view has been drawn with it (two frames after
  the click); closing the view first lets it go. Every switch (Tab, a click on a view) shows the
  main view's `Link` all the same. The main camera put on a vessel at a body with no physics
  shows its own over the main view (`Camera link`), the physics made behind it the frame after.
- **Going on to a new world:** the mission menu's "Change world"; in the world list, the world
  just played's `click marker "Delete: Empty world"` and then `"Yes, delete it"`, which leaves the
  list empty, and a New (`gameone-sky.txt` does this). The time warp and the camera's heading carry on into the next world: a script there
  doesn't step the warp up again, and turns the camera back to north before it leaves.

## Writing a script

- **Wait for a menu, then move, then click:** `until Menu = <screen>` (Worlds, Missions, Game,
  Settings, Log, None) waits for a menu to be up before clicking it. A click on a menu that has only
  just appeared can be missed: `move` onto the button a moment before clicking it.
- **Times:** a step's time counts from the last `until`, not from the step before (the driver
  refuses a script whose times go back): a `shot` timed the same as the click before it is taken
  before the game has drawn what the click did, which once looked like a menu that wouldn't open
  and a view that wouldn't fly. After a turn (`rdrag`, `mdrag`), wait 4 s before a `shot`: the camera
  follows a drag with a lag, and a shot sooner catches it mid-turn.
- **The pointer, for the camera:** the wheel zooms only with the pointer over the view (over a
  panel, the mission's steps say, it is the panel's), and the steering rings show while the
  pointer is over the flown vessel's middle, where a drag from the window's middle leaves it: park
  the pointer clear of both (`move 350 200` at 1280x720) and start drags there (`rdrag 0 60 350
  200`), as `gameone-lander.txt` does. The camera comes no nearer than its least distance, however
  far it is scrolled in; F2 hides the HUD for a clear shot (and with it every marker: show it again
  before a `click marker`).
- **Markers:** a script picks a target as a player does, by double clicking its marker: `dclick
  marker "<name>"` double clicks it wherever it is then (markers move with the camera), by the
  readout's `Marker <name>`. A single click (`click marker`) on a marker, or any click off them
  all, never changes the target. A click on a marker reaches the path, the nodes and the
  steering rings under it too (a `click marker` on one lying on the path places a node there),
  and a `dclick marker` that picks takes back what its first press did to the plan (a node
  placed, or deleted by its x), so it leaves the plan as it was. A double click on the path or a
  node away from any marker picks nothing. An `rclick marker` is the marker's, never deleting a
  node under it. A
  marker shows only while its object is too small to see for itself, so from the pad the landing
  zone (320 m off) has none: zoom out (M) first, and the drone ship's shows from the air, looking
  east (see `gameone-terrain.txt`). A click on a marker under a panel or the big message is the
  panel's. A vessel coming at the flown one has the red mark instead of its own
  marker, whatever the markers' settings: double click it as `dclick marker "Booster 1"` (its name
  and id, as `Coming mark` gives it), which targets that vessel, or lets it go if it is the target.
  The moon is targeted by `dclick marker "Moon"` (from the planet; the planet by `"Planet"` from
  the moon): `Target` then reads `Moon`, `To target` the straight line to its centre, and once
  zoomed out the map shows its window and closest approach, as for a vessel.
- **The map, the camera and the nodes** (`gameone-map-and-camera.txt`, then
  `gameone-map-and-camera-carry-on.txt` in the same folder): LAND ON THE MOON flown to a low orbit
  by readings (a short lean east, the Prograde hold, staging, cut at the apoapsis wanted, burn
  again from it until the periapsis clears the air); in the map the moon targeted by `dclick
  marker "Moon line"` and the AN and DN read from `Path marks`; the camera put on the moon and
  then the landing spot by right clicks, M for the spot's chase view, and `Marker Reset camera`;
  two nodes placed by clicking `Path AN` and `Path DN`, the first dragged (`drag marker "Node 1"`)
  and `Node times` read before and after; the Manoeuvre hold; a click on the heading ring; then
  quit, and the second script carries the flight on and reads `Camera`, `View`, `Hold`, `Nodes` and
  `Node times` again. The marks move round the screen as the vessel goes round (the map follows
  it), and one under a panel or the small view on the left is the panel's: wait for it to be clear
  (`until "Marker Path AN" > -220`, its x). The landing spot's marker shows only from the moon's
  side it is on: centred on the moon and zoomed in, turn the map (`rdrag 300 0 350 200`) and wait
  for it (`until "Marker Landing spot" != none`, which passes once the reading is there at all).
- **Precise flying:** a script can react to readings (cut the engine once the climb step is
  ticked, catch a fall at a height), but it has no loops, so it can't fly a landing. Test those in
  the flight tests, not through the driver.

## How the input gets in (if a click or key goes astray)

Keys are posted messages with scan codes, because GLFW maps keys from the scan code. The game
follows the pointer from cursor-move events (never `window.CursorPos`: BRUTAL keeps it from those
events but refills it from the real cursor whenever the window is repainted), so the driver's
posted `move` / `press` / `drag` land where the script says. ImGui's pointer is fed from those
events too (`Hud`: before each button and wheel event, and over the backend's own each frame), as
the backend alone would put it off the screen at each leave and, with the window focused, at
`window.CursorPos`: that was the driver's lost clicks (a click after a repaint landing where the
real cursor was). Anything clicked in the 3D view (the steering rings, the nodes, the markers)
takes the GLFW mouse events itself, gated by ImGui's `WantCaptureMouse` (a click on a panel is the
panel's), read once ImGui has taken the press in (`ChaseCamera` and `NavigationControl` hold a
press until its `IsMouseClicked`, as at the press it is still for where the pointer was before).
Their hover stays where the pointer was last seen: with the real cursor off the window, Windows
reports a leave after every posted move, so leave events aren't used (hover shots stay lit).

## Without the driver

- **Screenshots by hand:** use `PrintWindow` with `PW_RENDERFULLCONTENT`. GDI `CopyFromScreen`
  captures a white window for Vulkan swapchains. Find the window with `FindWindow("GLFW30",
  "GameOne")`, not `MainWindowHandle`, which can return the console.
- **Audio:** run with `ALSOFT_CONF` pointing at an ini with `[general] drivers=wave` and
  `[wave] file=...` to record the exact mixed output to a WAV for analysis.
