# GameOne

A game built directly on [BRUTAL](https://github.com/ahwootech/BRUTAL).

## Layout

```
BrutalOne/
├── Brutal/    BRUTAL source checkout, branch `local-build` (adds csproj/slnx files)
└── GameOne/   this repo
    ├── GameOne.slnx           game + referenced BRUTAL projects, for debugging
    ├── Directory.Build.props  defines $(BrutalRoot) = ../Brutal/
    └── src/GameOne/
```

BRUTAL is referenced as **source projects** (`ProjectReference`) instead of the
`Ahwoo.BRUTAL.*` NuGet packages, so you can step into framework code, set breakpoints
in it, and edit it with an incremental rebuild.

Upstream BRUTAL doesn't ship project files yet, so the `local-build` branch of `../Brutal`
adds hand-written ones that follow the published package dependencies. Native binaries
(e.g. `glfw3.dll`) are not in the repo, so they are taken from the matching NuGet package
using `PackageDownload` (see `../Brutal/Directory.Build.targets`).

## Build and run

```sh
dotnet run --project src/GameOne
```

Or open `GameOne.slnx` in Visual Studio / Rider and press F5.

To test:

- `dotnet test GameOne.slnx` flies whole missions headless with autopilots, about 75x real time
  (`tests/GameOne.Tests`). It covers landing on target, reaching a stable orbit (and staying in it
  for a lap), and each way of failing.
- `tools/GameDriver` plays a script of key presses, mouse drags and screenshots at the running game,
  for checking how it looks (see its README).

An arcade rocket game in the third person, on a tiny planet. It starts on the world list: pick a
world (or make one), then carry on something in it or pick a new mission. Esc pauses the game at
any time, in the game menu: resume, restart the flight (start the mission again, or rewind),
abandon the mission, pick another mission (which puts a fresh rocket on the pad), change world,
settings, or quit. The keys are for flying; everything about the game rather than the flight is in
that menu.

**Markers and targets.** Things in the distance have markers with their name and how far they are
from you: other vessels (a filled diamond for this flight's, an outline for earlier flights', a dot
for debris such as fairing halves), the launch pad, landing zones and test pads (squares; the mission's
landing site in yellow), and the planet and the moon (a ring, by its name). A marker shows only while its object is
too small to see for itself, and behind the planet it is hidden. The most important come first and
the rest give way where they would overlap; the quieter ones (earlier flights', places, debris) are
fainter and only labelled when the pointer is on them or they are within 2 km. Click a marker to
make it your **target**: it turns brighter with a box round it and, off the screen, an arrow at the
edge points the way to it (as it does for the mission's landing site); behind the planet it shows
faintly through it. Click it again, or anywhere that isn't a marker or the steering rings, to let
it go. Each vessel has its own target, kept when you switch away and back and saved with the
world; a target that is destroyed is let go. Which kinds of marker show is in Settings (in the game
menu), kept for you across every world. A vessel coming at the one you fly (from its warning until
it has hit or passed) is marked in red instead (amber for a near miss, which misses if nothing is done), with its name, the seconds to the collision and its
distance: a warning, not a marker, so it shows whatever the settings say, and neither fades as it
comes close nor hides behind the planet.

**The path.** Zoomed out (M), the path ahead is marked: its highest and lowest points (Ap, Pe, with
their heights), where it comes down into the air, and an X where it would hit the ground. Where
labels pile on each other (a closest approach by a Pe, or a crowd far out), hold the pointer over
the pile to spread them apart, each with a thin line to its own mark; far out, a body's name is
drawn over the labels under it, which fade. With a target, its own path is drawn too (orange), and:

- **Closest approach:** where the two of you will be nearest over the lap ahead, a ring on each
  path and the distance between. It moves as you burn.
- **Transfer window** (to a vessel): a green stretch of your orbit, shaded beside it on its outer
  side (away from the body), where one burn along (or against)
  your direction of travel, sized to reach the target's height on the far side, would arrive within
  1 km of it. Brighter and wider where it would come closer. Two orbits close in height drift into
  line slowly, so there may be no window this lap: wait and it comes round.
- **Transfer window** (to the moon): the same, arriving within its sphere of influence (about
  6.4 km of its centre), where its pull takes over. Once the path reaches it, the part inside is
  drawn round the moon: a pass bending round it and out again, as the moon moves on meanwhile,
  joining the path either side; an orbit round it a loop, or a way down a line onto its ground,
  round the moon as it is when you get there; each with its own Pe. The closest approach is then that
  Pe, over the moon's datum; with no encounter, the closest your orbit comes to its centre's path.
  The moon can't be your target once you are in its sphere (nor the planet while you are at it).
- **De-orbit window** (to a place on the ground): where a burn against your direction of travel,
  enough to bring your lowest point 1 km below the ground, would bring you down within 500 m of it;
  the ground X then says how far from the target you'd come down. The air isn't counted, so you'll
  come down a little short. The ground turns under the orbit, so a place comes round under it only
  every lap and a half or so: with no window this lap, the next lap's is shown. Over the ground the
  path then goes on round a second lap, with the window on it; in the Orbit frame (where every lap
  is the same ellipse) it is shaded fainter on the orbit and marked "Next lap". If that next lap
  would come down on high ground, where it does is marked too (in the Orbit frame, faintly, as the
  next lap's).

Nothing tells you when to burn: the window shows where it would work, and the rest is yours.

**Collisions ahead.** Zoomed out, the map also shows where any two vessels in the world are going to
collide (yours or not, flying, or landed and afloat where they stand), over the lap ahead of you, up
to an hour: a red cross where they collide, with both names, in how long and how fast they will close,
and a red line along each one's path into it, its last two minutes (so a faster pair's is longer),
faint for a vessel that is neither yours nor your target. The soonest twenty are shown; crosses
that would stack on the screen show as the soonest of them. Change an orbit and a collision it
dodges goes. It is information only: what to do about it is yours.

**Planning burns (manoeuvre nodes).** Zoomed out, the pointer on the path shows a small ring: click
to place a node there, a burn planned for that moment. The selected node (the last placed or
clicked) has six handles: prograde and retrograde (green), radial out and in (blue), normal and
antinormal (pink); filled ones add that way, hollow ones the other. Pull one out from the node and
hold it: speed builds that way, faster the farther out you hold it, and stops when you let go.
The path after the burn (purple) follows as you pull, with its own Ap and Pe, and the closest
approach to your target is worked out on it; the orbit you are on now shows faintly beneath. Drag
a node along the path it is on to move it in time (the nodes after it move with it; it can't go
before the one before it); right-click it, or click its small x, to delete it (the one after it
goes onto the path of the one before). Plan several and each is planned on the path the one
before leaves.

Nodes are a chain, fixed once set: the first is where it was placed on your path, and each later
one on the path the one before plans, keeping its amounts and its time after that one. Whatever
you do meanwhile, the nodes and the paths they plan stay where they were (your own path to the
first is drawn as it is now); changing a node redoes the ones after it, and dragging the first
puts it back on the path you are on now. You fly it yourself. Your instruments show the next one: the
time to it, the delta-v still needed (amber, "past it", once you've burned more than planned),
and how long that takes at full throttle (or that the stage hasn't the fuel). A purple diamond on
each steering ring shows which way the burn points, fixed in space once you light the engine;
steering there is up to you. Most burns go half before the node and half after. From the node's
time on, the delta-v still needed is measured against the path it plans, at its point nearest
you, so a late burn still counts. While what is still needed is within a share of the burn (3%,
held between 1 and 5 m/s), your path matches the planned one: cut the engine then and the node
goes, and the next is put on the path you are on then, at its time, keeping its burn. Cut it
before it matches and the node stays, however long past its time, until it is matched or you
delete it. Each vessel keeps its own nodes, saved with the world.

**Worlds.** A world keeps everything flown in it, from one mission to the next: satellites left in
orbit, boosters standing where they landed, stages still falling. The next mission's rocket appears
on the launch pad among them (anything left on the pad itself is cleared away to make room), and
its steps and score count only its own flight. Worlds are saved as you play (when a mission starts,
every 30 s and on quitting) and come back exactly as they were left. The world list shows your
saved worlds, the last played first, with what is in each; play, rename, reset to its preset, or
delete one, or make a new one from a preset. A world holds at most 1,000 vessels: past that, the
oldest go to make room (never the one being flown).

Vessels nobody is watching (not flown, not in a view, and nothing near them that is) are moved
more simply than the ones you can see, at a fraction of the cost: along their orbits, falling
through the air, burning, landing or wrecking by the same limits, and colliding with each other with the
same outcome (both wrecked if they hit harder than a vessel can take). Each is back in the full
physics, where it was, before you could see it or anything watched could reach it.

**The world log.** "World log..." in the game menu lists every vessel the world has lost, newest
first (the last 300): when, which vessel (or the two that collided), how (hit another vessel, the
ground, the tower...), how fast, where (a site's name within 5 km, else its height, in the sea or
on the ground), and whether anyone saw it. One removed to make room, or cleared from the launch pad
for the new rocket, is listed too (with no speed: nothing hit it). It is saved with
the world, with each mission begun, each step done (with its points) and how each ended (completed
with its score, failed and why, or abandoned; "Restart flight" (starting the mission again) logs the
old run as abandoned); a rewind takes the log back with the rest of the world.

- **Empty world:** nothing but the launch pad, the landing zones and the tower.
- **Satellites in orbit:** four satellites already in circular orbits 9 to 10 km up, each of which
  can be flown (give one a view with Add view, then click it). The first two share the launch's
  plane, 9 and 10 km up: fly the lower with the higher as its target and a transfer window to it
  comes round from about a minute in, for a couple of minutes (after that, not for a couple of
  hours, as they drift slowly back into line). The other two are in planes of their own.

Earlier flights' vessels are listed under "Also in this world" on the mission panel, and in Add
view; Tab only goes round the flown vessel's flight's.

**Carrying on.** A world picked from the list comes back exactly as it was saved, and before
anything can be flown in it, a screen titled with its name offers what can be carried on there:
each mission in progress ("Carry on: LAND ON THE MOON · flight 7 · 3 of 6", green, with
what its vessel is doing), then every vessel that can be flown and belongs to no mission (in free
play), then "New mission..." (the mission menu) and "Back to worlds". Carrying one on flies that
vessel where it is: nothing is added, moved or cleared. A world with nothing to carry on goes
straight to the mission menu. Until something is chosen, Esc and Close leave neither screen.
The mission panel shows the flown vessel's own mission (Tab to a vessel of another and it shows
that one's; a vessel of none shows no mission), and once every needed step is done with a bonus
still open, a "Complete mission" button there ends it (a bonus not done is lost). A mission that
ends while another vessel is flown says so for a few seconds at the top of the screen.

**Trying again.** "Restart flight" (in the game menu) offers two choices, each saying what it
does: "Start the mission again" begins a new run of the flown vessel's mission at its site, with a
fresh rocket on its pad (the run being flown is abandoned; its old vessels stay where they are, in
no run, and nothing else in the world changes but what is cleared off that pad); and "Rewind 12
min 40 s, to when this mission began" (or "was resumed"), which brings the whole world back as it
was at the last choice made from the menu this session (a mission begun, started again or carried
on), never further back than the world was picked. It asks to be confirmed first: everything since
then is undone, other vessels, other missions and the world log. "Abandon mission" (beside it,
while the flown vessel's mission is in progress) gives the mission up once confirmed ("Confirm
abandon", under it), its vessels left where they are.

**Errors.** If anything goes wrong (a world that can't be saved, say), a red warning sign shows in
the middle of the screen with the number of errors in the last 5 seconds. The details are on the
console.

Any mission can be launched from any of seven launch sites round the planet, picked in the mission
menu's "Launch from" row (the pick is kept, in the player's settings, until another is picked):
Home, by the coast of Westland, where the missions' landing zone and drone ships are; Polar, near
the south pole, for polar orbits; Highcrest, 1,200 m up in Eastland's range, in thin air; Desert,
in Westland's interior; Coral Isle, an island on the far side; Northreach, on a cold northern
coast by the frozen Boreal Sea; and Sentinel, in the Western Ocean. Each has a pad and a service
tower in its own colour; Home, Polar, Highcrest and Desert have a landing zone nearby, Home two
drone ships out at sea (one downrange to the east, one south-east for the moon trip's booster) and
Sentinel one. A mission's landing sites are Home's wherever it is launched
from. A site's place is part of the challenge, by design: from Highcrest, 1,200 m up, "Climb past
400 m" is done as the booster leaves the pad, and the thin air there makes orbit cheaper. Later
missions may only be started from certain sites.

Away from Home, each pad and zone stands at the highest ground under it, on a concrete foundation
reaching down to the rest: on a slope (Polar, Northreach) it stands proud at the slope's edge, on a
3 m concrete slab whose underside then narrows in at 45° into the mountain (a pyramid under a pad, a
cone under a zone, upside down), so its downhill side is no tall sheer wall, and its zone, a few
hundred metres off, at its own height. Every pad, landing zone and drone ship is lit, from its own
dusk to its own dawn (they fade on and off over a few seconds): floor lights round its edge,
floodlights on 20 m masts round it (as many as the ground there leaves room for: a site on a steep
slope has fewer, and is darker and harder), and lights up each tower. From far off in the dark its lamps are points of light to find it by. The
floodlights of the lit place nearest each view cast shadows, from several sides, so a vessel on the
pad casts several faint ones. The masts are solid: hitting one wrecks a vessel.

Every booster, upper stage, satellite and lander has landing lights, which come on by themselves (there is
no key for them) when it is dark where it is and it is within 500 m of the ground, fading on over a
few seconds, and stay on while it stands landed or afloat in the dark, until its dawn; high up, by
day and wrecked it has none, and a fairing half never does. They sit at the base, aimed down the
vessel's axis in a wide cone, in a cool white against the pads' warm lamps. Each view lights the ground under the
four nearest lit vessels within 500 m (the rest, and every one far off, show as a point of light to
find them by), so a night side scattered with landed vessels shows them all.

Four missions, and free play:

1. **Launch and land:** launch a booster from its pad, climb past 400 m, then fly it across to a
   landing zone about 320 m away and set it down on its legs, as a reusable booster does. A yellow
   marker (or an arrow at the screen's edge) shows where the landing zone is.
2. **Reach orbit:** fly a two-stage rocket into a stable orbit, one whose lowest point is above the
   atmosphere (3 km), so it never comes back down. Tip over east as you climb, drop the booster,
   then burn sideways until the orbit clears the atmosphere all the way round. The booster alone
   can't do it, and the upper stage's engine only lights once it has separated. Bonus: drop the
   booster at the blue landing-fuel mark on its fuel gauge (40%), and it has enough left to fly back
   down (Tab switches to it) to a drone ship about 2.4 km downrange (east), a floating deck that
   rocks with the waves. A good way: set the upper stage climbing towards an orbit well clear of
   the atmosphere, cut its engine, land the booster while the upper stage climbs with its engine off, then switch
   back and circularise at the top. Slow motion (comma) helps.
3. **Deploy a satellite:** put a satellite into a circular orbit 9 to 11 km up (both its lowest and
   highest points in that band) and bring everything else back. The upper stage leaves it lower
   (about 3.3 to 4.5 km), so the satellite's own small engine has to raise it: burn along its
   direction of travel until the far side of its orbit is about 10 km up (the instruments show
   it), go up there with the engine off (about four minutes), and burn again at the top to round it out. Fuel
   left over scores. On the way up the satellite rides inside a fairing (200 kg): once above the
   thick air (about 2 km), Space jettisons it in two halves, which fall as debris, and the next
   Space releases the satellite. Jettisoned too soon, low and fast, the bare satellite breaks up.
   The
   booster is Reach Orbit's: drop it at its mark and land it, as there. Then reach orbit, release
   the satellite (Space: you fly on with the satellite) and switch (Tab) to the upper stage left
   behind, which has legs of its own. Bring it down anywhere safely: burn against the direction of
   travel to drop out of orbit, and land it on its legs. Each landing counts anywhere safe; on a
   landing site it scores more (the drone ship for the booster, by the launch pad for the upper stage,
   which means waiting most of a lap for it to come round). The upper stage's fuel gauge has a
   landing-fuel mark too (50%): keep at least that much once the satellite is away.
4. **Land on the moon:** flown with the lander launcher. Land the lander on the moon, as near its
   landing spot (70° N on its near side, marked by a yellow square, or an arrow at the screen's
   edge, while this mission is flown) as you can, and once it is down open its bay doors (right-click
   it: they have no key). The landing scores 1,000 for coming down safely and up to 3,000 more for
   its nearness to the spot, measured along the moon's ground once it has settled: 3,000 on the
   spot, falling evenly to none 1 km off; the open bay 500. The booster is Reach Orbit's: drop it
   at its landing-fuel mark and land it for a bonus (anywhere safe, most on Home's second drone
   ship, about 2 km south-east, where it comes down on the climb into the trip's plane launched at
   its descending crossing, heading south of east; launched at the ascending one, heading north of
   east, no ship is there). Jettison
   the lander's fairing (Space) once above the thick air (about 2 km). The upper stage, with no
   landing-fuel mark of its own, takes the lander to the moon and into an orbit round it, and is
   left there: losing it after the lander is released costs nothing. Release the lander (Space)
   and bring it down on its own engine and legs. Losing the lander fails the mission.
5. **Free play:** the satellite launcher, with no objectives: nothing to tick off, win, fail or
   score, and no mission titles, for trying things out (the test pads, the legs, staging). Staging
   it gives every smaller part to fly too: the booster, the upper stage and the satellite. Nothing
   is its landing site, so a landing is just a landing; any landing zone or test pad can still be
   made the target by clicking its marker. What happens to the vessels still shows: lift-off,
   separation, the air, orbit, touchdowns and crashes.

The controls are the same for every mission:

| Controls | |
|---|---|
| Space | Light the engine, at once, at the throttle set (75% until you change it: it lifts off once that beats its weight), then drop a stage, whenever you like: empty or not, flying, landed or afloat. Before a payload in a fairing (a satellite, a lander) is let go, it jettisons the fairing first. |
| Shift / Ctrl | Throttle up / down. Z: full throttle, X: cut the engine. |
| Left drag on the rings | Steer: see below. |
| W A S D (or arrows) | Lean the nose forward, left, back or right, in the vessel's own frame: they turn with it, never with the camera, so holding one keeps it turning (round in a loop, above the air). On the pad, as the starting camera sees it, D leans it east (the gravity turn) and W away from the camera. The steering rings show while a key is held, following the nose. |
| P / R | Prograde / retrograde hold: keep the nose pointed the way the rocket is moving, or against it (to slow down, as for a landing burn): by its velocity in the frame of reference in use: over the ground (surface), along its orbit among the stars (orbit), or relative to its target (target). One or the other: turning one on turns the other off, and steering lets go of either. Below 5 m/s (0.2 m/s relative to a flying target, or in orbit) either leaves the nose where it is. |
| Frame buttons (top left, in the instruments) | Auto, Surface, Orbit, Target (with a target): which frame of reference the speeds, the holds and the travel markers go by. Auto turns from surface to orbit as the orbit clears the air, and back; the frame in use is shown beside them (lit for 3 s after it changes). |
| G | Deploy or stow the landing legs. |
| Right drag / wheel | Orbit the camera round the rocket, from straight down to straight up (looking up from below a landed rocket it slides along the ground under it, clear of it; below level it also comes in nearer than its distance where the ground is in the way) / zoom (far enough out to see the planet). |
| Right click on the vessel | Its actions, in a menu where you clicked: the bay doors (the lander's: "Open bay doors" or "Close bay doors", which no key does) and the legs ("Deploy legs" or "Stow legs", as G does), each saying what it will do now. Only on the vessel you fly, in the main view, not zoomed out; a vessel with no actions (a satellite) opens none. A right press that moves 4 px or more before it is let go is a drag (the camera turns), not a click. The menu closes on an item, a click elsewhere, Esc, M, or that vessel no longer flown (Tab), wrecked or staged; the keys still fly while it is open. |
| Right click on anything else | Centres the camera on it. In the map: a body (its disc, whether markers are shown or not), a site or a point of interest (by its marker, so only with markers on) or a vessel; in the chase view: another vessel. A site or a point of interest is looked at from beside it. Right click on your own vessel brings the camera back to it (while it is elsewhere; on it, in the chase view, it opens the actions as above). While the camera is not on your vessel, the steering rings are not drawn (the keys steer as always, in the vessel's own frame), and the strip's "Reset camera" button brings the camera back to it. |
| Middle drag | Free look: turn the view in place, up to straight up or down, the camera staying where it is (to frame the high sky, or the planet overhead from the moon). A right drag or the wheel looks at the rocket again. |
| M | Zoom all the way out, to see the whole planet and the path ahead of the vessel you're flying (the white dot on it); M again comes back to the vessel, just where the camera was. From the map centred on a vessel, a site or a point of interest, M gives the chase view of it; centred on a body, the chase view of your vessel; from the chase view, M goes to the map with the same centre. It takes the same moment whatever the time warp. Out there, right drag and the wheel turn and zoom round the planet instead, and you keep flying with the keys. The wheel zooms on out to the whole system: the sun (ringed and named), the planet's path round it, the moon's round the planet, and every vessel flying faintly round the body it is at. The path also fades in by itself as you zoom out with the wheel, and is what the vessel would do with its engine off and only gravity on it (in the air, drag pulls it short). |
| Tab, or click its view | Fly another vessel: a part that has come away and has guidance of its own (see below). Every switch (and every carry-on and rewind) shows the communications link-up, and the camera comes back as that vessel left it. |
| , / . | Time (they read as < and >): comma slower, full stop faster, through 1/4, 1/2, normal, then time warp at 2, 4, 8, 16 and 32x. Everything runs at that speed: the flight, the effects, the camera. Warp runs the same physics as normal speed, just more of it each frame, so engines, steering and landings all work in it; it stays where you set it, but for a fast collision coming at the vessel you fly: its warning ("COLLISION: another vessel in 4.2 s (2,400 m/s)", on its own line over the time line, in time to dodge it, or as soon as it is seen with no fuel to dodge with) drops you to normal speed, once for that collision, so warping again is up to you. As a fast collision comes near (the flown vessel's, a small view's, or one between vessels near the camera), the whole game is slowed for it, from 1 km out, easing evenly down to about 10 m/s to the eye, held from 10 m before the collision to 10 m after, never so fast a step's travel could pass through anything, the time line saying how slow and what for ("SLOWED 1/37x for a collision with Booster 1 (2,400 m/s)", to a tenth as it starts and eases back: "1/1.6x"; in your own slower slow motion, that too: "..., in slow motion 1/4x"); these keys do nothing then, and once it is over (hit, passed, or flown away from) it eases back to your own rate over 3 s. There is no time limit: how long the way in takes follows from the speed (at 1,000 m/s, about 5.6 s). With **Slow motion for near misses** on (in Settings; off by default), another vessel passing the one you fly, or a small view's, within about its length without touching, faster than 300 m/s, slows the game too, in the same way: shown ahead as soon as it is foreseen ("NEAR MISS: Booster 4 in 3.2 s (1,000 m/s)", which drops the warp once; a pass that close that is a fast collision too is one event: once its COLLISION line shows, the NEAR MISS line goes, and it drops the warp only once) ("SLOWED 1/100x for a near miss with Booster 4"). While anything is coming at a small view's vessel (a fast collision, or with the setting on a near miss), that view's border flashes: red for a collision, amber for a near miss (it misses if nothing is done). The same colours go for the rest: the COLLISION line is red and the NEAR MISS line amber, and so are the main view's mark and the compass's tick. A busy world may not keep up: the instruments then show how fast it is really running ("WARP 32x (running 20x)"). |
| H | The controls list (bottom left): open it, or fold it back to its one line, "Controls (H)", as it starts (open, it takes the small views' place, once there are any). |
| F2 | Hide the whole HUD, for a screenshot of the world alone: the instruments, markers, paths, small views, controls and credits all go; F2 again brings it back. Esc still opens the game menu over it. Each run starts with it shown. |
| Click a marker | Make it your target (see Markers and targets above); click it again, or elsewhere, to let it go. |
| Click the path | Zoomed out: place a manoeuvre node there. Pull its handles to plan the burn, drag it along the path to move it, right-click it (or its x) to delete it (see Planning burns above). |
| Esc | Pause, in the game menu: resume, restart flight (start the mission again, or rewind), abandon mission, missions, the world log, change world, settings, quit. Esc again resumes; from the missions or the world list, Esc goes to the game menu and their Close button back to flying (but in a world just picked, nothing leaves the carry-on screen or the missions until something is chosen). |

**Steering.** Hover the pointer over the rocket and two rings appear round its centre of mass: a
flat blue one marked N E S W (the heading: which compass direction to point the nose) and an
upright orange one standing along that heading (the pitch: how far up from the horizon). The ring
nearest the pointer lights up; press the left button on it and drag its handle round. While the
button is held the rocket is asked to hold that direction. Hollow markers on the rings (and on the
heading tape) show where the rocket is actually going, as prograde holds it: through the air, low
down, and round its orbit among the stars above the air. Nothing about the steering is special to a mission:

- the rocket turns only as fast as it can: fastest at full throttle (the engine's gimbal), and at a
  third of that with the engine off (small thrusters);
- let go, and nothing steers: in the atmosphere the air turns the nose along the direction of travel
  (nose first, or tail first when falling backwards, as a returning booster does), harder the
  thicker the air and the faster it goes, so in thick, fast air it can't be held far off that
  direction. That is what makes a gravity turn: tip over a little, let go, and the air carries the
  nose round as gravity bends the path. Above the atmosphere nothing turns it: it keeps pointing
  wherever it was.

A compass along the top shows which way the camera faces, with markers for the nose's heading
(blue), the direction of travel (white ring), when landing, the landing zone (yellow) and, while a
vessel is coming at the one you fly, the bearing it comes from (a red tick down from the top, held
at the tape's end when it is behind you). The
instruments start with the clocks, in every frame of reference: the world's time ("Day 3, 07:42",
Home's day and time of day) and the local time where the vessel is ("Local 09:15", by its
longitude). They show how thick the air is (as a share of the air at the ground), how far it is to
space, and when the rocket is above the atmosphere.

The rocket is destroyed if any part of it hits the ground faster than 6 m/s (judged with a little
leeway, `Scene.TouchdownLimit`: 6.27 m/s at the touch, and 3.01 for the bell's 2.5, as a free fall
from where touchdowns were once read adds that much) (its feet, its bell,
or its side as it falls over: judged by how fast that part closes with the ground where it
touches, so a tall one falling over hits hardest at its nose), if a part already on the ground
runs into something solid faster than 15 m/s (sliding into a pad's side, say), or if it touches
the service tower or a light mast at all. How it
leans doesn't matter: once down, it has landed when it comes to rest, however it lies (standing,
leaning, on its side), and a landed vessel can still be moved, knocked over or flown again, and
is judged again as soon as it moves. A landing is a stop, not the end: landed or afloat, wherever
it is, throttling up lights the engine (if it has fuel) and it lifts off once the thrust beats its
weight; and it can stage there too (afloat, both parts float; on land, the top may fall).
Vessels collide with each other, judged by how fast the two closed where they touched, whatever
their masses: slower than 6 m/s just pushes them apart, and faster wrecks both ("Hit another
vessel"). Only a touch is a hit: a hard burn beside another vessel never is. A vessel can come to
rest on others, and has landed there. With its legs stowed it can still land on
its engine bell, as it stands on the pad, but only gently: no faster than 2.5 m/s. The legs give a
little, with no bounce: in the air they hang at full stroke, and each pushes square to the
ground under its foot, which it grips. Until all four feet are down the legs share the load, so
the rocket settles onto a slope; then they lock, until it leaves the ground. The touchdown speed is judged as the first foot touches the
ground; coming down off centre, the first legs down slow it, and the rest settle on more gently.
Landing within 10 m of the landing site's centre (a landing zone's, or the drone ship's) scores
points for accuracy, gentleness and fuel left. Reaching orbit scores points too, more with fuel
to spare; then the booster scores more for
landing safely anywhere, and most on the drone ship. Crashing it scores nothing for it,
but the mission still succeeds. The score shows on the mission panel once the mission has succeeded,
and keeps counting. The game starts with the BRUTAL credits on screen (bottom right), which covers licence sections 2.1 and 2.2
(see below); in flight it goes after 5 s, and every menu shows it again.

**Vessels and views.** When the rocket comes apart, each part is a vessel of its own. One with
guidance can be flown; one without is debris. You fly one at a time, in the main view, and the
rest carry on with whatever they were last given (throttle, direction, prograde or retrograde hold, legs). Each
part that can be flown gets a small view of its own, in a strip along the bottom left, labelled
with how it is doing. Click a small view (or press Tab) to fly that vessel: the two swap places.
The x on a view closes it, without changing anything about its vessel; "Add view" lists every
vessel without a view, debris included, to give one back. Each small view is lit by what is
round its own vessel: the lamps and floodlights (and their shadows) of the lit place nearest it,
and the sun's shadows round it. (A view of a wreck has nothing left to cast a shadow, so it
gets no sun shadows of its own: it borrows another view's that covers where its camera is, or
else the main view's.) So a booster on a far pad or the drone ship at night is lit there
as the main view would light it. Views at the same place share their lights and shadows, so only
places apart cost more. They are otherwise cheap to draw (a coarser planet, no smoke or lens
flare, and their sun shadows at half the main view's sharpness): enough to see each vessel is
doing what you expect.

### The planet

Its radius is 10 km: small enough that an orbit takes about five minutes. Its
gravity is 1 g at the surface, far more than a real body that size would have, so flying near the
ground feels as it would on Earth; gravity falls off with distance from its centre as real gravity
does, so orbits behave properly. Its atmosphere thins exponentially and ends 3 km above the sea, with
no drag at all above that. A circular orbit just above the atmosphere needs about 270 m/s among the
stars.

It turns. The planet makes one turn against the stars every 1,108 s (a day, noon to noon, is 1,200 s,
as it also goes round the sun, once a year of 12 days), west to east about its poles, so the ground
at the equator moves at about 57 m/s: an eastward launch starts with that for free, and a westward
one has it against it. The world turns with the planet (the ground, the pads and the sea stay where
they are in the game's frame), so everything off the ground feels what a turning ground does: a fall
bends east, and in the air it drags with the ground. Above the air a vessel keeps its attitude among
the stars, and its orbit is the one it flies among the stars. The sun moves: it crosses every
place's sky once a day, a day of 20 minutes of play at normal speed, from the world's clock (which
is saved with the world, and which the time warp runs faster, so at 32x a day passes in about 37 s).
A new world starts at 07:30 at Home; midnight is 00:00 and noon 10:00 on the clock, whose "hours"
are the day's minutes, and with no tilt the sun rises at 05:00 and sets at 15:00 everywhere. Each
place's time of day goes by its longitude, and how high the sun climbs by its latitude: at Polar,
70° south, it circles low all day, never more than about 20° up. The stars turn with the planet
too, rising in the east and setting in the west each night, as the sun does.
The HUD's Orbit speed, under the apoapsis and periapsis, is the speed among the stars (on the pad
about 57 m/s at the equator, less towards the poles; the speed a circular orbit needs is shown
quietly below it; it isn't shown while a landing's path stays in the air), while Horizontal and
Vertical stay speeds over the ground. The orbit path drawn is among the stars, with the ground's
turn drawn under it, so a landing site comes round to meet it: a target, a transfer window and a
de-orbit allow for the turning ground.

The sun and the moon are bodies on their paths (`SolarSystem`). The sun is `Scene.SunDistance`
(3,000 km) from the planet's centre and `Scene.SunRadius` (89 km) in radius, so its disc's angular
radius is the one over the other (`SolarSystem.SunAngularRadius`); its pull is whatever makes the
planet's lap round it the year. The moon is `Scene.MoonDistance` (40 km) from the planet's centre
and `Scene.MoonRadius` (2.5 km) in radius, its pull at its surface `Scene.MoonSurfaceGravity` (a
sixth of the planet's). Its lap among the stars follows from the planet's pull at its distance
(`SolarSystem.MoonLapSeconds`, about 27 minutes), and its day, its lap against the sun, is a little
longer (`SolarSystem.MoonDaySeconds`, about 30 minutes): it keeps one face to the planet, and has
its own local time by the sun seen from it (`SolarSystem.MoonLocalSeconds`, read as the planet's
clock is). It goes round the way the planet turns, on an orbit tilted `Scene.MoonTiltDegrees` (30°)
from the equator, so eclipses come in two seasons a year, while the sun is near the line the orbit
crosses the equator on, and never between. Its spin axis is tilted `Scene.MoonSpinTiltDegrees`
(20°) from its orbit's normal (fixed among the stars), so its face nods at the planet by that
each lap, and from its north pole the planet wholly rises and sets once a lap. Its own axes
(`SolarSystem.MoonOrientation`, `ToMoon`, `FromMoon`) are laid out as the planet's map's are
(`CubeSphere`: north `Planet.NorthPole`), lat 0 lon 0 its mean point under the planet. The landing
spot, 70° N on the near side's middle line, has the sun up with the planet in its sky for a fair
stretch of every lap of the year.

The frames of reference: the speeds, the holds and the travel markers go by one frame at a time,
chosen by the buttons in the instruments (Auto, Surface, Orbit, Target). Surface is the velocity
over the ground, Orbit the velocity among the stars, and Target the velocity relative to the picked
target (its holds work down to 0.2 m/s against a flying target or in orbit, where the others, and
Target against something on the ground, stop at 5). Auto goes by Orbit once the orbit's periapsis is
100 m clear of the air, and by Surface again as it dips into the air (the two thresholds differ so
it doesn't flip back and forth at the edge). The path, its marks and the manoeuvre nodes are drawn
in the frame in use, from the vessel's centre of mass: in Surface the path is the one the ground
sees, and its end is where the vessel will hit the ground. The instruments show the speed in each
frame (and the relative speed to a target), and the To target distance is the straight line to the
picked target (none is picked by default). The frames follow the body the flown vessel is at:
inside the moon's pull Surface is over the moon's ground and Orbit round its centre, and Auto,
with no air there, goes by Orbit once the periapsis is 100 m clear of the moon's highest ground,
and by Surface again below it.

Every height is measured from the sea: the HUD's Altitude, the missions' heights, the apoapsis and
periapsis, and each launch site's height in the mission menu (Home's pad is 35 m up). Under the
Altitude, the HUD shows Ground, the height over whatever is under you (the ground, a pad, or the
sea), which is what a hop or a landing goes by; it is shown quietly once you are in orbit. Below the
sea, the altitude is negative. Near the moon the HUD's numbers are the moon's: the heights over
the zero of its heights (its datum: it has no sea), the tilt and speeds over its ground, the orbit
round it (the apoapsis, periapsis and the period, a lap's minutes and seconds, under them; an
apoapsis past where the vessel would leave the moon's pull reads "leaves its pull", with no
period, as it never flies that lap), the
local time by its sun, the compass by its north, and its air (none: "NO AIR: no drag").

Its surface is made, not smooth. The launch pad stands on raised ground 35 m above the sea, with the
coast about 330 m to the east and a range of mountains (Westwall, up to about 1,350 m) inland to the
west, its crest about 2.5 km away, so a return from orbit comes in over it; the landing zone is
about 320 m north-west of the pad. The rest is hills, plateaus, deserts, beaches, snow on the
heights, and seven seas (about 60% of the surface) of different depths, deep under the drone ship.
Heights run from about -610 m (the seabed) to 1,350 m, measured from the radius above. The land is a
fixed map, made once by `tools/TerrainGen` (below) from an authored layout, so every world has the
same planet; it is eroded, so the valleys and ridges look carved. The game adds fine detail (dunes
on the desert, scree under cliffs) that follows the kind of ground. The pad and the landing zones
sit level on foundations that reach down to the ground (or seabed) under them; away from Home, on a
slab narrowing in at 45° down into the slope. North of the pad is a test range: six more landing
zones in a row, 100 m apart from 500 m north, tilted 5, 10, 15, 20, 25 and 45 degrees (falling away
to the east) for trying landing on a slope, each on a plinth whose sides run straight down to the
ground. There are no shadows from the terrain and no weather yet.

**The drone ship.** About 2.4 km east of the pad, over deep sea (228 to 257 m under it), a drone
ship holds its position: a deck 52 m long (east-west) and 32 m across, 3 m above the waterline,
with a 4 m draft. It is not floated like a vessel: its pose is worked out from the waves (a plane
fitted to the water under its hull), so it heaves, pitches and rolls with them, and it is a moving
ground. Legs, touchdown, impact and settling are all judged against the deck's own motion, so a
booster lands on it as it would on a pad that is rocking; the hull's side is solid but is not a
deck, and one that misses it splashes down in the sea. A vessel left on the deck is carried with it
(nothing about this is saved: it is rebuilt when the world loads). Its numbers are `Scene.DroneShip*`.
Home has a second, the same (its marker "Drone ship 2"), about 2 km out 43° south of east over 22 m
of sea (`Scene.TripDroneShipCentre`): the moon trip's low orbit is tilted, so a climb into it at
its descending crossing heads some 30° south of east, and its booster comes down there (at the
ascending crossing the climb heads north of east, where Home has no ship). A drone ship rides no
shallower sea than `Scene.DroneShipMinDepth` (10 m: its draught and the roughest troughs, with
room to spare; the terrain generator refuses a ship in shallower water). Sentinel
has one of its own, as Home's first is from its pad.

**The sea** is water, with its calm level at 0, which every height is measured from (Home's pad
stands on raised ground 35 m above it, `Scene.HomeElevation`).
Every sea has waves, one fixed set of them over the whole planet, moving with the world's clock (so
they stop when the game is paused and run faster in time warp). How big they are is each sea's own
(`waves` in the layout, the crest to trough at most: smallest in the Amber Gulf, largest in the
icy Boreal and Austral Seas), blended where seas meet and dying away over the last
few metres of the shallows, so the shore has none. The colour of the water comes from each sea too,
from its shallow colour to its deep one with depth. The sea itself is never solid: what is, in it,
is the seabed, the drone ship's hull, and the sea ice's floes.
The two icy seas are frozen round their middles: a sheet of pack ice reaching 1.1 km (Boreal) and
1.3 km (Austral) from the sea's middle on average, so about 2.2 to 2.6 km across (`ice` in the
layout), its edge ragged, standing `Scene.PackIceFreeboard` (0.4 m) above the sea with
low pressure ridges on it. It is ground, not water: anything coming down on it lands (or crashes) as
on land, and nothing gets under it. At its edge it ends in a foot under the water, sloping down
0.3 m a metre for 20 m and then dropping to the seabed, so the water right by the edge is shallow
and pale. Round it, a band of floes 500 to 600 m wide is still sea, but the floes halve its waves
(`Scene.FloeWaveDamping`). The floes (about 5,800 of them, laid out from the map, the same in every
world, so none is saved) are discs of ice 4 to 14 m in radius (`Scene.FloeMinRadius` and
`FloeMaxRadius`; most of them small, about half too small for a booster's legs), 1.6 m thick with
0.2 m out of the water, densest by the sheet (kept a few metres off its foot) and thinning out
across the band. Each rides the waves as the drone ship does, and is solid: a landing on one is a
landing on ground (not on a target), never a splashdown, and a vessel left on one rides it.

A vessel in the water is held up by it: it is pushed up by how much of it is under, slice by slice
down its length (`Scene.WaterSamples`), so it floats upright with as much of itself in as
`Scene.WaterBuoyancy` holds up (the stronger it is, the less) and the waves rock it. The water also slows whatever moves through it, each
slice by how far under it is (`Scene.WaterDrag`), which resists turning as well, and its plunge
along its length (`Scene.WaterEndDrag`). A tall rocket has no way to stay standing: it sinks in,
then slowly lies down on the water, tail low. The waves carry it round in small circles only, to
and fro, so it stays about where it came down (currents and wind, which would drift it, wait for
the weather). The legs never stand on the water, and nothing steers a vessel in it unless the
throttle is over `Scene.SurfaceSteeringThrottle` (as on the ground), or it could never lie down.

A **splashdown** is judged by `Scene.SafeSplashdownSpeed` (15 m/s), more forgiving than a touchdown
on the ground, so a rocket can come down upright and tip over onto its side in the water without
being lost. The first part of it into the water counts by its whole speed; any part going under
after that, by its speed across the vessel's axis. Faster than the limit, it is lost ("Hit the
water at ...", with no explosion, and it sinks). The faster it goes in, the deeper it plunges.
The water slows a tall vessel's fall onto its side, so even a very tall one is hardly ever lost
by tipping over in it: that is left as it is (the user, 2026-10-31), rather than tightening the
limit or the water's drag for it.
The splash is spray thrown up where it hits, and a sound (louder the faster). The instruments' "Too
fast!" warning over water goes by the depth: where it is deep enough to float in, the splashdown
limit; shallower, the ground's, as it will reach the seabed. The seabed is ground: in shallow water
a vessel resting on it, however it lies, is landed as on land, and hitting it too hard destroys
it. Afloat, a vessel is `Floating` once it has stopped moving through the water and
turning (`Scene.FloatSettledSpeed` and `Scene.FloatSettledTurnRate`, which grow with the waves of a
rougher sea) and is no longer top-heavy (`Scene.FloatTopHeavy`), so a tall one is only floating
once it has finished tipping over. It then shows "SPLASHDOWN", with the touchdown speed, and
"floating" in the vessel list; where the mission wanted it on a landing site, "SPLASHDOWN OFF
TARGET" in amber, as for landing beside the zone, with the line about the zone. A splashdown
scores as landing safely anywhere and counts for "Land the booster", but is never "on the landing
zone". Fairing halves that fall in the sea are lost ("sunk" in the vessel list).

A vessel afloat is never at rest, so it is frozen by distance instead: it keeps moving while the
vessel you fly is within `Scene.FloatWakeDistance`, and freezes only beyond `Scene.FloatFreezeDistance`
(a little further, so one right at the edge isn't frozen and woken again and again). One still
settling in the water never freezes. Frozen, it wakes as you come near.

The planet is drawn as patches of the cube's faces, split finer nearer the camera (up to about 1 m
squares) and culled to the view; the small views use a coarser split. Its colours come from the
kind of ground. It is collided with through small height-field tiles laid round each craft that is
awake and near the ground, in the same shape the eye sees (the planet's curve included).

The map, `src/GameOne/Assets/terrain.bin.gz`, is about 11 MB. It holds, for each point of a
1,024-by-1,024 grid on each cube face, the height, the kind of ground and which sea it is in. It is
made for one radius and one height of Home's pad above the sea: the game refuses a map made for
another, so after changing `Scene.PlanetRadius` or `Scene.HomeElevation` (or where the pad and
zones sit), regenerate it:

```sh
dotnet run --project tools/TerrainGen -c Release -- tools/TerrainGen/layout.json src/GameOne/Assets/terrain.bin.gz
```

It takes about 30 to 90 seconds. Add `--preview <folder>` to also write pictures of the whole planet,
of the launch site and of the icy seas' ice, to judge a change to `tools/TerrainGen/layout.json` before running the game.
See `tools/TerrainGen/README.md`. A much bigger planet would also need the changes in "Why the
planet is small", below.

The moon has its own map, `src/GameOne/Assets/moon.bin.gz` (about 1.7 MB, 512 by 512 a cube face),
made by `tools/TerrainGen --moon` from `tools/TerrainGen/moon.json`: two big craters well apart on
the near side, one higher than the other, and a rough crescent canyon well south of them, gentle grey plains with small craters, and a
levelled landing spot 20° from the north pole; on the far side, a long ridge curving like a tail to a
800 m mountain with a crater in its top, joining its flank as a lower spur. `Terrain/MoonGround.cs` reads it.

The rockets burn fuel by the rocket equation, so they get lighter and accelerate harder as they go:

| Rocket | Stage | Thrust (×weight) | Burn at full | Speed change |
|---|---|---|---|---|
| Booster (landing) | booster | 2.2 | 50 s | |
| Two-stage (orbit) | booster | 1.64 (whole stack) | 27 s | ~470 m/s (~270 to its landing-fuel mark) |
| | upper stage | 1.5 | 29 s | ~790 m/s |
| Satellite launcher | booster | 1.64 (whole stack) | 27 s | as above |
| | upper stage (legs) | 1.5 (with the satellite) | 44 s | ~1,050 m/s with the satellite |
| | satellite | 0.3 | 39 s | ~120 m/s (raising it to 10 km takes ~40–50) |
| Lander launcher | booster | 1.62 (whole stack) | 27 s | as above |
| | upper stage (legs) | 1.5 (with the lander) | 44 s | ~1,000 m/s with the lander |
| | lander (legs) | 1.8 on the moon | 111 s | ~370 m/s (about three times a landing's from an orbit round the moon) |

The booster's engine is as efficient as the landing booster's. Dropped at its landing-fuel mark, it
keeps about 430 m/s for itself: enough to brake, steer to the drone ship and land. A
good ascent needs about 570 m/s, or about 510 counting the 57 m/s an eastward launch gets from the
planet's turn, so even burning all its fuel (~470) the booster alone falls short, and the
heavy upper stage has room for a less than perfect flight. The satellite launcher's stack is just as
heavy, so its booster flies the same; its upper stage has a more efficient engine, to get to orbit
and then come back down and land. The lander launcher is the satellite launcher with a lander in
place of the satellite: its upper stage has no landing-fuel mark, as it is left in an orbit round
the moon, and the lander rides under a wider fairing (3.7 m across, 300 kg), so the stack is 21,950
kg, 250 kg more, inside the 22,200 kg its booster's thrust is written for. The lander is a square
body 2.3 m across and 2.4 m tall, on four short legs at its corners (stowed up them for the trip),
with a payload bay at its foot made for a rover (about 2 m by 1.5 m), open at two faces, tanks on
the deck over it and an antenna on top. The bay's two faces are doors, 1.6 m tall, hinged on the
bay floor's edge, each with two more panels as long folded flat against its outside, as wide as it
(the legs are drawn slim, so stowed they clear the folded panels at the corners): over 6 s each
door swings out and down as its panels unfold, all together, into one straight ramp 4.8 m long,
past level until, on level ground with the legs at full stroke, its far edge is 0.1 m above it
(about 105° from shut, the ramp about 15° below level); a door whose far edge meets the ground first (on a slope, or with the legs pushed up) swings only as far
as that (found as they begin to open), resting on it. Once they are fully open the collider has
the two ramps (a ramp's touch is the hull's, judged as any is), until they start to close; opened
before a landing, the ramps are out of the collider from its touchdown until it has landed on its
legs; then their stops are found again (one that would be in the ground swings up onto it, at
the doors' own pace, never in a jump) and they go back in. They are found again each time it
comes to rest landed, too (set down upright under formula on a slope and handed back, it leans onto
its legs, and a ramp raised for the upright pose comes back down onto the ground); a stop is
where any of the straight ramp, along its length, meets the ground. Shut, the collider's X faces
are out at the folded panels.
Swinging, the doors leave a lander frozen at rest as it is. They have no key (they are opened and closed by
`FlightInput.ToggleBayDoors`), and the HUD shows them under the legs. It weighs 900 kg empty, the rover's 300 kg counted in,
with 250 kg of fuel; its engine is weaker than the upper stage's but more efficient (an exhaust
speed of about 1,500 m/s against 1,000). The numbers are in `Vehicle.cs`, the doors' in `Scene.cs`.

It is sunlit with real-time shadows, and the planet has a day and a night side, which the sun
carries round once a day. The sky, the haze and the sunlight all come from the air (`Air.cs`):
the sun's light is scattered by the air's molecules (blue most) and a little haze, and dimmed by
the air it crosses (and a thin layer of ozone high up), worked out the same way for the sky, for
what far-off things fade into, and for the light reaching each place. The air's numbers are
scaled up from Earth's: this small planet's own air would be too thin to colour anything. So the
noon sky is blue, paler towards the horizon, with a faint glow round the sun, and its light
nearly white; a low sun's light turns orange and dims, on the ground, the vessels, the smoke and
the sea. A higher place sees a deeper sky, and at Polar the sun stays low and its light warm all
day. After sunset the air high up stays lit for a while (on this small planet the twilight is
grey, a little green-tinged low down, rather than Earth's deep blue), then the sky goes close to
black, leaving the stars, the pads' lights and the engines' flames; the HUD, the markers and the
paths show as they do by day. The sun's disc is its true size (its radius over its distance,
`Scene.SunRadius` and `Scene.SunDistance`), and the moon is seen through the haze as far-off
things are: its own ground from its map (its craters' pale rims and rays, its canyon), lit by the
sun with the long shadows of its own ground where the sun is low on it; its dark side faintly lit
by the planet's lit face (`Scene.PlanetshineStrength`), so a crescent's dark part shows its
craters dimly; and in the planet's shadow a dim red, the sunlight grazing the planet through its
air (`Scene.PlanetShadowGlow`), the shadow's edge coloured as the sun goes behind the air. From
above the air the sky is black but for the atmosphere glowing along the planet's rim; near the
line between day and night the ridges are lit gold by the low sun and the haze over them is blue, fading to the dark of the night side. That
night is lit dimly by the moon too (`Scene.MoonlightStrength`, no shadows), more as it is fuller,
none at a new moon, with it set or in a lunar eclipse.
The engine's plume lights up the pad and the smoke around it, and its exhaust stirs that smoke into
a billowing cloud; in thin air the plume billows out wider. An explosion leaves fireballs and a pall
of smoke. The engine roars and crackles in 3D from the nozzle, muffled as the air thins; touchdowns
thud, the legs clank, explosions boom.

Rendering is Vulkan 1.3. Everything is in metres, with the launch pad at the origin on top of the planet.

| File | What it does |
|---|---|
| `Scene.cs` | Where everything sits, and the tuning: the planet and its atmosphere, sun and sky, pads and tower, the rockets' sizes, flight handling, camera, and smoke. Tweak the numbers here. |
| `Planet.cs` | The planet: up, east and north wherever you are, gravity, how thick the air is, the height above the sea (`Altitude`: every height, the terrain's included, is above the sea), the height above whatever is underfoot (`SurfaceAltitude`: the ground, or the calm sea over water) or above something solid (`GroundAltitude`: the ground, or the seabed), a raft's top (the drone ship's deck, or a floe) as the ground over it (in those queries, and `GroundVelocity`, how fast the ground under a point is moving), and the orbit something is on. These are the planet's own values, fixed (`GroundBody.Planet`'s, each member its), for what is the planet's alone (its sites, rafts and sea, the launch, its drawing); code that can run at any body reads that body's members on `GroundBody` instead. |
| `GroundBody.cs` | A body something can be at, in its own frame: its centre, size, pull, turn, sea, air and sites (`HasSea`, `HasAir`, `HasSites`: the planet's sea, rafts and sites are read only where it has them), and its ground's map and heights (`Height`, `MaxHeight`, `OrbitFloor`: the air's top, or with none its highest ground; `LowestRadius`: no surface below it, its sea or with none its lowest ground, where every path ends at the latest); everything read of the ground, the sea and the air at a place there (`Up`, `Altitude`, `AboveAir`, `AirDensity`, `Gravity`, the turn's pulls and `FallStep`, `SurfaceAltitude`, `GroundAltitude`, `Surface`, `Ground`, `Normal`, `IsSea`, the orbit something is on, and kin), so every read says which body it asks; its place among the bodies (`Index`, `GroundName`, `Parent`, its sphere of influence `SphereRadius` worked out from the masses, its pose in its parent's frame `PoseIn`, and its centre's place alone `PlaceIn`; `Circling` makes another going round a parent, with another's ground if given, for a list of bodies other than the game's). `GroundBody.Planet` is the planet; `GroundBody.Moon` (centre under the origin at its datum, turning once a lap, no sea or air) has a physics of its own whenever a vessel there is in the physics (watched, flown, or near one that is), as any body does (`Worlds`). |
| `Bodies.cs` | The bodies as a list (`Bodies.All`: the planet, then the moon, each by its `GroundBody.Index`; `Named`), each in its own ground-fixed frame and each but the planet going round a `Parent`: the change between any two frames at a time (`Bodies.Change`, a `FrameChange`: places, directions, velocities, orientations, spins and a drawn model placed from a point, composed through the parents, in double), a body's centre in another's frame (`CentreIn`), a body's centre's orbit round its parent (`OrbitOf`, what a transfer to it is worked out against) and its path round it as the map draws it (`Path`, in a view's frame, in double; the root's round the sun), and which body a place is at (`Choose`, by the spheres of influence: into a child's at its `SphereRadius`, out to the parent only past `Scene.SphereLeaveFactor` times it; with an overload taking a list of bodies of its own, and one taking their places at the time already worked out, `PlacesIn`, so a step asking every vessel works each body's out once; a child's sphere is tested by its centre's place alone, `GroundBody.PlaceIn`, and a place changed into a frame only as it goes in or out), and how far from a body's centre no edge comes (`ClearWithin`: a place nearer is at that body without asking). `BodyPose` is a body's place in its parent's frame (`GroundBody.PoseIn`). |
| `Doubles.cs` | `Vector3Double` and `QuaternionDouble`: vectors and rotations in double, for what is worked out across the bodies' frames (`Orbit`, `SolarSystem`, `FrameChange`), made floats only near their own frame's origin. |
| `Terrain/Terrain.cs` | The planet's ground: the one height function its physics, altitude, foundations, predicted paths and drawing all read (`GroundBody.Planet.Height` reads it straight). A height is the baked map plus fine detail made here (value noise on a hash, octaves by kind of ground); it also answers the slope, the kind of ground and where the sea is, and the higher of the ground and the calm sea (`SurfaceHeight`, for altitude and paths). The planet shader repeats the detail in GLSL, so the two must stay alike. |
| `Terrain/Sea.cs` | The sea's one wave function: one fixed set of waves over the whole planet, their phases at a moment, each sea's wave size where you are (blended between seas and dying away in the shallows), and from them the water's height and its flow at the surface (and the height taken apart by wave, `Terms`, for a point read at many moments). The planet shader repeats it in GLSL (and is written from the wave set), so the two must stay alike. The flow only goes to and fro, so it carries a floating vessel round in circles rather than away. Also `SeaInfo`, each sea's climate, wave size and colours, from the map. |
| `Terrain/TerrainMap.cs` | The baked map as loaded from `Assets/terrain.bin.gz` (or the moon's, `Assets/moon.bin.gz`): per cube face, the heights, the kinds of ground and the sea each point is in, with a one-sample apron round each face. Refuses a map made for another radius or elevation (Home's, or the moon's 0). |
| `Terrain/CubeSphere.cs` | Directions to and from a cube face's coordinates (equal-angle, the same as the planet's vertex shader), and latitude and longitude. |
| `Terrain/MoonGround.cs` | The moon's ground, as `Terrain` is the planet's, in moon-fixed directions: its map (`Assets/moon.bin.gz`) plus fine detail (`Scene.MoonDetail`), its normal, its kind (`MoonKind`: plain, rim, ray, floor) and albedo (`Scene.MoonAlbedos`), and `SunSeenOver`, the ground's own shadow by a march of `Scene.MoonShadowSteps` over the map towards the sun, written so a GLSL twin can transcribe the detail and the march. |
| `Terrain/SurfaceKind.cs` | The kinds of ground (sea floor, beach, desert, scrub, grass, rock, snow, site, the icy seas' pack ice, and the sea floor under their floes, which is also the sheet's foot sloping under the water and is drawn in the ice's colour) and the seas' kinds. |
| `Raft.cs` | Rafts, things riding the waves that a craft can stand on (`Raft`: a rectangle, the drone ship, or a disc): each one's pose from the waves, a plane fitted to the sea's heights over its top, whose middle is its heave and slopes its pitch and roll (the waves under its points taken apart once, `Sea.Terms`, so each pose after is a sum: the floes in sight are posed every frame to be drawn), holding its position, and the height above its top and its velocity at a point. `Rafts` holds them: `Ships` (every launch site's drone ships, `LaunchSite.DroneShips`, site by site; `Ship` Home's first; sizes in `Scene.DroneShip*`), `Under` (the raft a point is over, and how high), `Active` (those set each step) and `Time` (the moment they are at: a floe not kept up to date is set to it when a point is looked for over it, and drawn as it is then, `PoseNow`). Fixed once per step so the ground queries (`Planet`, including `GroundVelocity`), the physics' kinematic hull bodies and the drawing agree on where each top is. |
| `Floes.cs` | The sea ice's floes (`Floes`): discs laid out once, from the map, on a jittered grid of cube-face cells (`Scene.FloeSpacing`) over the icy seas' floe bands only, each cell's floe (or none), its radius (most of them small) and its place from a hash of the cell, densest by the sheet and thinning out across the band (a distance field over the band's texels), in clusters by noise, the band ending on a noisy line rather than the texels', kept clear of the cell's edges so none overlap; the same every run, so none is saved. Each is a `Raft` (`Scene.Floe*`). `Near` (the floes near a point, cell by cell), `Under` (the floe a point is over: its cell and the eight round it), `CellOf`, and `Cover` (how much of a band they cover, which its water is tinted by beyond `Scene.FloeDrawDistance`, where they aren't drawn). |
| `Foundations.cs` | How deep the launch pads and landing zones' concrete reaches. Home's pad and zone and the test pads reach from the top down to below the lowest ground under them, so none floats. Each far pad and far zone stands on a `Footing` (`PadFootings`, `Plinth.Footing`): a straight-sided slab `Scene.FoundationSlab` (3 m) thick, then narrowing inward at 45° (a pyramid frustum under a pad, a cone frustum under a zone, upside down), tucked under the slab, down until it is in the ground all across, so on a slope the downhill side is no tall sheer wall; it is drawn smooth (`Meshes`), and its collider is boxes (or drums) stepping in every `Scene.FoundationStep` to follow it. `Plinth` is each zone's shape: its round top (tilted, on a test pad) and the plinth or footing under it. Physics, drawing and `Planet.SurfaceAltitude` take it from here. |
| `PadLights.cs` | Where every lamp and floodlight mast is, at each pad, landing zone and drone ship (each a `LitPlace`, in its own frame), the lights of the places nearest a point for the shaders (`Nearest`, allocating nothing, each place's by how far it has faded on: `PlaceLights`, which `Flight` steps by real seconds, a place going dark at `Sun.IsDark`), and each view's light set (`LightSets`: the main view's from its camera, each small view's from its vessel, views at one place sharing one set and one block of floodlight shadows, then the four nearest lit vessels' landing lights, `VesselLight`, which cast none). |
| `Air.cs` | The air's light, the reference the shaders repeat: how much of the sun's light gets through the air to a place (`Sunlight`, `Transmittance`), the sky's light along a view (`Sky`) and between two points (the haze: `InScatter`, `TransmittanceBetween`), the sky's light on an upward face (`SkyLight`) and the lens flare's dimming, by single scattering (molecules, haze) and the ozone's absorption, marched along each ray. The coefficients are `Scene`'s. |
| `AirTables.cs` | The air's light worked out once at start for the GPU: two tables, by height and the sun's angle, of the sunlight's transmittance and the sky's light on a face, built from `Air`, with the lookup the shaders repeat. |
| `SolarSystem.cs` | The sun, the planet and the moon as bodies on their paths, from the world's clock: the sun still at the centre, the planet round it in the plane of its equator once a year (the sun's pull, `SunMu`, worked out from that), the moon round the planet on an orbit tilted from the equator (`Scene.MoonTiltDegrees`), its line fixed among the stars, so eclipses come in two seasons a year. Where the sun is (`SunDirection`) and the moon (`MoonCentre`, `MoonSpinAxis`, tilted from its orbit's normal by `Scene.MoonSpinTiltDegrees`, and its own axes, `MoonOrientation`, `ToMoon` and `FromMoon`; in double `MoonCentreAt`, `MoonOrientationAt`, and its velocity and spin over the ground, `MoonVelocity` and `MoonSpin`, worked out rather than differenced) in the ground's frame, worked out in double and wrapped to a turn before any float; the moon's lap, its day against the sun and its local time (`MoonLocalSeconds`, read as the planet's clock over its longer day); the planet's height over a place on the moon (`PlanetHeightFromMoon`); the sun's disc's size (`SunAngularRadius`), its centre in double (`SunCentreAt`, what a camera far out sees it from: `DiscSeenFrom`) and the planet's path round it (`PlanetPath`); the sunlight the planet lets past to a place beyond it (`PlanetShadowLight`: its shadow, coloured by its air; `ShadowLight` the same past any body with air, what the dark is judged by there) and the light off its lit face there (`Planetshine`); the moon's light on the planet (`Moonlight`: what it gives back, `MoonShine`, by its lit share seen from a place, through the air towards it). |
| `Sun.cs` | The sun from the world's clock, at any body (each read names its body, by its pose among the bodies: no member is one body's): where it is from the body (`Direction`, in its frame, from the planet's path round the sun and its turn, `SolarSystem`; the renderer reads `SolarSystem.SunDirection`, the planet's ground's frame), how high it is and whether it is dark at a place (`Height`, `IsDark`: held between dusk's and dawn's heights, or another body over it, `Seen`: the share of the sun's disc seen past every body but the place's own, the least any one leaves, a body with no air a sphere and one with air by the brightness of the light it lets past, `SolarSystem.ShadowLight`, as the ground is drawn; a body clear of the sun's way passed over by its reach alone; worked out in the root's frame with the bodies' places once a world time a thread), each place's time of day (`LocalSeconds`: on the planet the world's clock by its longitude, elsewhere over the body's own day; `WorldTimeFor` the other way, for starting a world at a time of day somewhere on the planet), and the clocks' text (`Clock`, `WorldClock`). |
| `SunAreas.cs` | Each view's sun shadows' area (`SunAreas`, chosen each frame, allocating nothing): the main view's round the flown vessel (and its pad, near it), then one round each small view's vessel unless it is well inside one already chosen (`Scene.SunAreaMargin`, held by `Scene.SunAreaHysteresis`). |
| `Vehicle.cs` | The rockets: each stage's mass, fuel, thrust and burn time, and whether it carries guidance (can be flown once dropped). |
| `Mission.cs` | What the player is asked to do: a name, briefing, the rocket, the steps ticked off on the HUD, the target altitude, where each stage should land, bonus steps that it succeeds without, and what each step scores. *Launch and land*, *Reach orbit*, *Deploy a satellite*, *Land on the moon* (its landing spot its point of interest), and *Free play* (no steps: nothing to win or score). |
| `MissionRun.cs`, `MissionProgress.cs` | A run: one flight of a mission, with its own vessels (the rocket and the parts that come off it), its site and flight number; its steps are judged against those vessels whichever is flown, so any number of runs can be in progress at once. Whether it has succeeded or failed, and its score; its progress keeps each step done, with its points as they were then. |
| `Flight.cs` | The flight, in its world, over the physics at each body in use (`Worlds`; `PhysicsAt`, and `Body`, the flown vessel's): the launch (at the sites' body), every vessel in this flight and in the world, the pilot's input sent to the one being flown (switched with Tab), staging, and the sounds called for; the time rate, and the warning of the flown vessel's soonest fast collision (`Warning`, from `Collisions`, looked for every step), which drops the time warp once for each collision; and the forced slow-down (`Slowed`, a `Slowdown`) for the flown vessel's, a small view's or one of the few nearest the main view's camera (within `Scene.NearByDistance`, at most `Scene.MainViewWitnessed`), following whichever collision asks the least scale (not only the soonest), which slows the game's clock and the physics' step together (`Worlds.StepScale`, every body's physics' at once) from `Scene.SlowdownFrom` out, easing evenly with the distance to `Scene.SlowdownPassSpeed` to the eye, held within `Scene.SlowdownHoldWithin` of the collision, never so fast that a step's travel is more than a satellite's width as it hits, then eased back over `Scene.SlowdownEaseSeconds`. Starting another flight leaves the world as it is (clearing the pad); starting again puts it back as it was; a world is brought to life from a `WorldState` and captured back into one. Each step every vessel's body is chosen (`Bodies.Choose`) and one crossing a sphere of influence's edge handed across to the new body's physics or formula (`Cross`, `Vessel.CrossTo`), each by itself; the path of each vessel flying in the physics is looked along (`Patched.Legs`) for the bodies it will be at within `Scene.PhysicsAheadSeconds` (times the rate in warp), whose physics is asked for ahead of need (`Worlds.Ahead`); the flown vessel's frame changing raises `ViewRebased`. Every switch, carry-on and rewind shows the link-up first (`LinkUp`), the physics made if it isn't and the vessel flown the frame after, which raises `ViewTaken` (the camera is given the view that vessel was left with, `Vessel.View`, or `GameView.DefaultView`). A small view given to a vessel at a body whose physics isn't made gets one of its own (`OpenView`, `ViewLink`), the vessel left under formula and witnessed by nothing until the view has been drawn with its static (the frame after the click), the frame after which brings it in; a view closed meanwhile lets it go (`CloseView`). |
| `LinkUp.cs` | The communications link-up, shown as any vessel is switched to or carried on, and over a small view given to one: its state and real-time clock (`Begin`, `Due`, `Ready`, `Step`), `Scene.LinkUpSeconds` (1 s) in all however soon the vessel is flown in its body's physics, longer only if that takes longer, clearing over the last `Scene.LinkUpClearShare` of it (`Static`, `Reading`); the HUD draws it, the readout's `Link` reads it. Flavour only. |
| `Frames.cs` | The frames of reference: `Frame` (surface, orbit or target), `FrameChoice` (the player's pick, or Auto), Auto's choice at the vessel's body with its 100 m margin on the periapsis over that body's orbit floor (its air's top, or with no air its highest ground), the velocity of a vessel in a frame, the velocity relative to another, and a path turned into a frame (`OnLine`, over the ground of the body its orbit goes round); `LegPlace`, a leg of a patched path round another body placed in the view's frame as that body sees it: the last leg by the change at its start (a loop round the moon, from the planet), one with a leg after it (a pass) point by point, each by the change at its own time, so it joins the legs either side. |
| `World.cs` | A world as data: `WorldState` (its name, clock, flights, every vessel and its log of losses) and `VesselState` (everything needed to bring one vessel back exactly: its body by name, `Body`, and its place, attitude and velocity in that body's frame). |
| `WorldLog.cs` | The world's log of losses: `LogEntry` (when, what, how, how fast, where, whether it was seen) and how it is worded (where, on a body's ground by its name: "on the moon"; the crash reason's first clause, its speed taken out to be the entry's: a collision's closing speed, a touch's, else the vessel's own). `Flight` writes one for each vessel lost once a step has run (the physics', the formula's wrecks and collisions, or removed to make room), two wrecked by one collision as one, the newest `Scene.WorldLogEntries` kept and saved with the world; the game menu's "World log..." shows it newest first. Fairing halves are not logged. |
| `WorldStore.cs` | Saved worlds: one JSON file per world (in local app data, `GameOne/Worlds`, or `GAMEONE_WORLDS`), saved by writing a new file and swapping it in, in one format (`FormatVersion`: a file in any other, or with a vessel at an unknown body, is listed as unreadable); the world list's summaries (in orbit by each vessel's body); making, renaming, resetting and deleting worlds. |
| `WorldPresets.cs` | The worlds a new one is made from: Empty, and Satellites in orbit. |
| `Vessel.cs` | One vessel (the rocket, or a part that came away), at one body (`Body`: everything it exposes is in that body's frame; `Physics`, that body's physics while it is in it, none under formula): its throttle and fuel, staging, steering (held directions, leans, prograde and retrograde hold, how fast it can turn, and the air turning it), the legs, the bay doors (each stopped where it meets the ground), the orbit, and what counts as a landing, a splashdown or a crash (hitting the water, settling afloat, the seabed as ground). A vessel not being flown keeps its last inputs; one without guidance is debris. Its landing lights (`LandingLights`: a fraction, on in its body's dark near its ground and faded by real time, never saved). Crossing a sphere's edge (`CrossTo`): its motion, aim and burn begun changed into the new body's frame, in that body's physics or under formula there, its best altitude begun again over the new body. Its heights, orbit (and its period), stable orbit and Auto frame are its body's; the next node's burn (`Guide`) is measured round the body the node is at, along its patched path; the mission's landing site (on the sites' body, `Sites.Body`) is read across the bodies from another (`LandingSiteHere`, `DistanceToLandingSite`: there the straight line to it). |
| `Physics/Worlds.cs` | The physics at each body in use: one `RocketPhysics` a body, made as a vessel there first needs one (`Need`, from a world in the pool when there is one) or ahead of need on the tile worker's idle time (`Ahead`, never in the step), found by body (`At`), stepped while a craft is in it, each in turn in `Bodies.All` order with its own crafts' controls (`Step`), and released to the pool after `Scene.PhysicsIdleSeconds` with none, its wants of the tile worker dropped (`InUse`, `Released`); one step scale for all (`StepScale`), and one leftover of a step (`Lag`), so a craft taken from one world into another across a sphere's edge is at the same moment in both. |
| `Physics/RocketPhysics.cs` | Jolt Physics (`Brutal.Jolt`), a world made for one body (`RocketPhysics(GroundBody)`, `Body`: its ground, sea, air, sites and pull are the ones read, in its frame; any number side by side; `Rebuild` makes one from the pool another body's, its Jolt world and its tiles' placeholders kept; each craft knows its world, `Craft.World`, and a step takes only its own crafts' controls; a craft crossing a sphere's edge is taken out of one world and into another's, the same craft and shapes, its motion changed across, `TakeOut` and `TakeIn` with a `CraftState`; the process's rafts are let go on disposal only by the world that holds them, `Rafts.HeldBy`): the pads and tower are static, each raft's hull (the drone ship's, and a floe's while an awake craft is within `Scene.FloeReach` of it, or of the way it goes in `Scene.FloeReachSeconds`, the nearest `Scene.MaxFloeBodies` at most) is kinematic, moved with the waves each step (a craft frozen aboard is carried with it, and one frozen afloat beside it is stopped again should the hull wake it), the ground is height-field tiles round each awake craft near it (`TerrainTiles.cs`), and each craft (a vessel's body) is dynamic; when one stages, it splits into two crafts, and one that blows up is removed. Gravity (towards its body's centre) and drag (by its air's density) are applied per body. Arcade handling: thrust is an acceleration along its axis, and the steering sets how fast it turns. Its collider gains feet when the legs are fully out, at the legs' pushed-up position; within their stroke below that each foot holds the craft up, sharing the load until enough feet are down and then locking, and pushes along the ground's normal with some grip. Crafts at rest are frozen (out of the simulation, with no tiles) until something moving could touch one: its body, swept along the way it goes in `Scene.WakeSeconds`, comes within 5 m (`Scene.WakeMargin`) of the frozen one's, as the gap between their shapes (so a fast one wakes what it is coming at, not what it passes). In the sea a craft is floated by the water (`Float`: buoyancy and drag slice by slice, against the waves' own flow), and one afloat is frozen by distance from the vessel flown instead. See the Jolt note below. |
| `Physics/Touches.cs` | What a craft touched, read from Jolt's contacts after each step (`RocketPhysics.TouchesBegun`): what it was (`SolidKind`: the ground, the tower, a mast, a raft or another craft, told by each body's user data, and a drone ship's masts by height over its deck), where on the craft (`CraftPart`: its feet, bell or hull), the point and normal, and how fast the two were closing just before the step; and whether it is a new touch. The legs' landing is a touch at the feet, found by the legs (`Cushion`). `RocketPhysics.Touching` says what each craft touches now, from the contacts live (a body the game takes out forgets its own at once): freezing, waking on a touch and whether a vessel's ground is measured closely read it. |
| `Physics/TerrainTiles.cs` | The ground for Jolt: small static height-field tiles (about 33 m across, 0.5 m between samples) laid on a fixed grid round each awake craft near the ground, of the world's body's ground (the terrain: a raft it is over doesn't count, so over deep water there are none), up to `Scene.MaxTerrainTiles`, and let go when nothing needs them. A tile's shape is made off the step by `TileWorker` (soonest needed first); the step only adds the body, and makes at once itself only a tile a craft needs within `Scene.TerrainTileUrgentSeconds` (a quarter second's travel along its way, in full-length steps, so shortened with the step in a forced slow-down). A craft faster than `Scene.FastCollisionSpeed` has ground laid only round where it is predicted to come down (`Craft.GroundAhead`), its way in within reach of the ground there. A frozen craft about to wake has its ground wanted of the worker ahead of need; a wreck has none laid for it. A tile let go keeps its shape for a while, for a craft coming back. A world reused for another body has its tiles made that body's ground (`Rebuild`). Also `HeightFields`, which makes a tile's shape through Jolt's own height-field constructor. |
| `Physics/TileWorker.cs` | Makes the ground's tiles' shapes (their heights and Jolt height fields) on one background thread every world shares (below normal priority, started on the first cell any world wants), soonest needed first of every world's (by the time each cell has to go, as each world's clock is its own), ahead of need. Each world opens its own `Jobs` on it, which carry its body, so a tile is made of its world's ground whatever else runs; a step tells its jobs which cells it wants and by when, and takes what is finished; it never waits on the worker, and the worker never touches a body or `Timings`. The game's worker lives as long as the process; one a test makes is stopped by `Dispose`. With no cell of any world's to make, it does what was asked of its idle time (`WhenIdle`: a body's physics made ahead of need). A world released to the pool drops what it still wanted (`Jobs.Drop`). |
| `Physics/JoltShared.cs` | What every physics world shares, set up once for the process (`Ensure`): Jolt's process-wide state, the job system and temporary memory the steps run on (each world stepped on the main thread in turn), and the one tile worker (`Tiles`). |
| `Rendering/PlanetPatches.cs` | Which patches of the cube's faces to draw this frame: a quadtree split by distance from the camera, culled to the view, within `Scene.TerrainPatchBudget`. The one place that decides them. |
| `Rendering/GroundColors.cs` | The planet's map as colours (each texel its kind's colour, `Scene.KindColors`), RGBA8 a layer per cube face, with a full mip chain averaged in linear light on the CPU at start-up (about 34 MB on the GPU), so the ground's colour is read per pixel, filtered, however far off: no triangles of colour from afar, and no shimmer. |
| `Rendering/FaceLevels.cs` | The mip chain the far maps share (`GroundColors`, `Waterline`, `SeaColors`): over the six faces' insides (a power-of-two side), each level's texel the mean of the 2 by 2 under it, the faces in parallel. |
| `Rendering/Waterline.cs` | Where the sea meets the ground far off: the map's heights clamped to the ice sheets' freeboard (±0.4 m) about the sea level, R16_SFLOAT a layer per cube face with a mip chain (about 17 MB on the GPU), read by the planet's vertex shader at each corner at the level whose texels are half a square, so a far corner says how much of the ground round it is water and the waterline falls in proportion between corners (not in steps along the squares where an ice sheet meets deep water); near ground (squares two texels apart or closer) reads its own heights as before. Its C# twin of the shader's read is what the tests check the drawn waterline by. |
| `Rendering/SeaColors.cs` | The seas' shallow and deep colours as a map, blended across the borders between seas by each sea's weight `exp(-d / Scene.SeaColorBlend)` (d its distance through the water, from a shortest-path search on a grid of 61 m cells), as the generator blends their depths, so two seas meet over about 2.2 km (10% to 90%, 1.1 km either side) rather than on a line (which showed from afar as straight-edged wedges); land cells take the colours of the water round them. RGBA16F, 256 a face in twelve layers (the faces' shallow, then deep colours) with a mip chain averaged in linear light (about 8 MB on the GPU, some 0.2 to 0.4 s to make at start-up). |
| `Rendering/VulkanRenderer.Terrain.cs` | The terrain's drawing: the map's textures on the GPU (the ground's colour map, the waterline's and the seas' colours among them), and the air's tables (`AirTables`) built and uploaded; each patch drawn as a displaced grid with a skirt. The sea is drawn as an opaque surface moved by the waves over the patches with water in them, coloured by each sea's own shallow and deep colours (from `SeaColors`, blended where seas meet); the seabed is pushed back in depth so it never shows through (by the waterline, so wherever the water is drawn). |
| `RocketGeometry.cs` | The rocket's shape where physics, rules and rendering all need it: where the legs hinge and swing to, the feet, the engine bells, how the stages stack, the lander's bay doors (where they hinge, how far they swing, how each one's panels fold and unfold into its ramp, their ramps' tips). |
| `StageShapes.cs` | Each stage's own shape of legs (`LegGeometry`: hinge, length, swing, stroke, foot pads; its feet worked out once) and of fairing (`FairingGeometry`: radius, height, nose, mass), read by the physics, the rules and the renderer: the rocket's and the lander's legs, the satellite's and the lander's fairings. |
| `Controls.cs` | The keyboard, turned into a `FlightInput` each frame: held keys read as they are, one-off presses caught as they happen, and steering made camera-relative. The flight only ever sees `FlightInput`, which is how the tests fly it. |
| `ErrorLog.cs` | Counts every line written to stderr, for the HUD's error sign (how many in the last 5 s). |
| `ChaseCamera.cs` | The third-person camera: follows the rocket with a little lag, in its body's frame, stays upright to its body's ground below wherever round it it is (and clear of it), and can be orbited (right drag: a right press barely moved, under `Scene.RightClickSlopPixels` all told, is a click instead, `TakeRightClick`, for the vessel's actions), zoomed and turned in place (middle drag, free look), or zoomed all the way out from its vessel (M) and back (the map looking at the vessel, its angles from the body's north pole, opening on its own view until the player turns or zooms it, then on the view its vessel left, as kept in its `CameraView`), and with the wheel out to the whole system (`Scene.ZoomOutMaxDistance`: the planet's whole path round the sun fitted on the screen), never coming within `Scene.ZoomOutSunClearance` of the sun's radii of its centre. Across a sphere of influence's edge it is changed into the new body's frame keeping its place and view (`Rebase`), its up, its trail behind the vessel and, zoomed out, its pole eased to the new body's over `Scene.CameraBodyEaseSeconds`. Its near and far planes (`Clip`, `Planes`: near `Scene.CameraNearPlane`, far at infinity, at every zoom) and its projection (`Projection`, the depth reversed) are what the renderer draws its view with. It also projects points onto the screen for the HUD. |
| `Formula.cs` | A vessel out of the physics (`Vessel.OnFormula`: its body taken out of Jolt's world by `Vessel.LeavePhysics`, made again where it is by `Vessel.ReturnToPhysics`), moved by formula instead, at its own body by that body's ground, air and pull: above the air and clear of its highest ground with its engine off it is on a Kepler orbit (`Orbit.StateAfter`, `FormulaOrbit`) from where it last changed; in the air, or burning, it is stepped every `Scene.FormulaStepSeconds` as the physics falls it (its body's `FallStep`, its drift matched to the physics' own step), its thrust along its aim; a steered one's attitude follows its aim at once (its holds turn the aim as in the physics), one that can't be steered turns on as it was; landed or afloat it doesn't move. Each step is checked for its touch: the towers and masts (`TowersAndMasts`, the predictions' own check), and its lowest point onto the ground, the calm sea, a pad's, a zone's or a raft's top (read along the step, no further apart than the smallest floe is wide, so a fast one doesn't step over a ridge; where the seabed is within its plunge's reach, it is handed back to the physics there, which plays the plunge), judged once by its whole speed against the physics' limits and reasons (`Scene.FormulaUprightDegrees` says standing or lying), landed (`Vessel.LandByFormula`), wrecked (`Vessel.WreckByFormula`), or, its engine still lit, standing there until it goes out (or lifting off again, its thrust over its weight); one come down on a raft (landed, or standing burning) comes back into the physics at once, for the raft to carry it. It is still drawn (`Vessel.FormulaParts`, its legs as they are) and saved as any other. |
| `Unwatched.cs` | Who is in each body's physics, chosen each flight step, per body (only what is at one body is near anything there; the main view's camera is at the flown vessel's): the witnessed (the flown vessel, the small views', the main view's near its camera), whatever could come within `Scene.HandoverDistance` of something in the physics or of the camera within `Scene.HandoverSeconds` at their closing speed (grown out from the witnessed, never one under formula against another), and one parting, settling in the water, on something but not yet at rest, on the pad, or on a raft (landed on one by the formula too: the raft carries it in the physics). The rest leave the physics; one leaves only past `Scene.HandoverHysteresis` times its reach, and at most `Scene.HandoversInPerStep` come back a step, every body's together, each into its own body's physics, soonest needed first (one switched to or given a view comes back at once: `Flight.BringIn`). Those under formula are moved on after the physics each step (`Formula.Advance`), and their collisions with each other found every `Scene.FormulaCollisionSeconds` of game time over the interval ahead (`FindCollisions`: their size-spheres, or their feet's reach if further, swept and boxed, sorted along one axis, each overlapping pair at one body solved as `Collisions` solves one; then, within reach, their bodies, each an axis as wide as itself, sampled for the beginning of a touch, as the formula checks the towers). Each closing faster than its `ImpactTolerance` as their bodies begin to touch (face on along a nose's axis, else side to side) is wrecked as part 1 wrecks one hit by another ("Hit another vessel"), when the collision comes and only if their bodies still touch then; a touch already begun as a check starts is not judged again; each vessel's earliest in a check is kept; two at different bodies are never paired, and one crossing a sphere's edge under formula has its collisions looked for again at once, in its new body's frame (`Crossed`). Never slows the game. |
| `Orbit.cs` | The path a vessel follows with its engine off and only gravity acting: an ellipse (or, escaping, a hyperbola) round a body (`Orbit.Body`: everything it reads, the centre, the pull, the turn, the ground and the sea, is that body's; every one is made for a body), worked out from where it is and how it is moving, and laid out as points ahead of it, once round, to the ground (with no sea, as low as its map's lowest ground), or out to the edge; where it will be when (Kepler's equation; straight up or down, Kepler's equation for a line, `RisingTo` a distance), and its highest and lowest points and crossings ahead (`Ascending`, `Descending`). |
| `Patched.cs` | The path across the bodies' spheres of influence (`Patched.Legs`, into a list of `PathLeg`s, allocating nothing): an `Orbit` round each body in turn, up to `Scene.PatchedLegs`, patched where it crosses a sphere's edge (`Crossing`): into each child's (sampled every `Scene.PatchSampleSeconds`, each sample's closest approach over the next reckoned from how the two move, so a graze shorter than a sample is found, then bisected to a millisecond) and out of its own past `Scene.SphereLeaveFactor` times its radius (solved on its conic, or along its line straight up or down, `Orbit.RisingTo`), the state changed across by `Bodies.Change` in double so the legs meet. |
| `Collisions.cs` | Each witnessed vessel's soonest fast collision ahead (for the warning), and the one asking the slow-down's least scale (`Deepest`, for the slow-down: a slow near miss can come just before a fast hit) (closing faster as it hits, with its present thrust, than `Scene.FastCollisionSpeed`): with another vessel (among the stars, from the size-spheres round their middles, each bent by gravity and the air), any body's ground, the sea or a raft on it (its path's end, patched across the spheres of influence (`Patched.Legs`) so it comes down on the ground of whichever body it is at by then, the body hit kept with it (`Collision.Body`), asked every `Scene.GroundCheckSeconds`, a floe or drone ship there hit as the raft), a service tower or a light mast (the path flown ahead as the physics falls); how long ahead it must be warned of to dodge, and what is coming, named by its kind. `Flight` asks it of the flown vessel, each small view's and the main view's nearest few once a step, after the step's motion, for its warning and the slow-down. Its ground is its size-sphere's path coming within its size of the terrain, so a pass whose middle clears a ridge its body hits is a collision. Pairs are flown ahead together in chords (`Closest`: 2 s down to 0.25 s as they near). With the player's setting on, the near misses too (`NearestMiss`: another vessel passing within `Scene.NearMissLengths` lengths, outline to outline, without touching, faster than `Scene.NearMissSlowAbove`; the deepest for the slowing, `SoonestMiss` for the warning), from the same snapshot and march, their thrust in their paths. |
| `Encounters.cs` | How a vessel's path meets its target's: the closest approach over the lap ahead, and the transfer and de-orbit windows (every place round the orbit where a simple burn would get there: to a body, into its sphere of influence; `TransferBurn`, the path after one); a body's closest approach along a patched path (`BodyApproach`: an encounter's periapsis, or with none the closest the orbits come). |
| `CollisionSearch.cs` | The map's predicted collisions between any two vessels in the world (`Flight.MapCollisions`), on a thread of its own as `TileWorker` is (the flight hands it every vessel but the wrecks while the map is drawn, `Flight.LookForCollisions`, never waiting on it): each flying one along the orbit of its present state, each landed, afloat or on the pad held where it is on its body's turning ground (on the moon, the moon's), a lap of the flown vessel's orbit ahead up to `Scene.MapCollisionHorizonSeconds`; pairs ruled out by their heights' spans, then, for planes apart, looked at only where both are on the short arcs round their line of nodes together, and in one plane by the angle between them, stepped on as far as it can't close; then sampled and narrowed down. Each pair's earliest within their sizes kept, and any after a vessel's wrecking one dropped. Information only: nothing acts on it. |
| `MapLabels.cs` | The labels over the view at the path's marks (Ap, Pe, Closest, the crosses), the map's (the sun's) and the markers', gathered as each is drawn and laid out together (`Lay`, a pure function of their rectangles and the points they label) and drawn last: a label under a body's (the sun's, a body's marker's) faded to `Scene.MapLabelUnderAlpha`, the body's drawn over it; labels piled on each other spread into a column beside their marks (`MapLabels.SpreadAside` right of the furthest, or left of the nearest by the screen's right edge, kept on the screen), in their order down the screen, only while the pointer is over the pile (`Markers.Pointer`; never from under a panel or a menu), held while it is on a label where it has moved, each with a thin line back to the point it labels, and back as they were once it leaves. What a click is on is chosen by `Hit` (which `Markers` asks): a spread label wins over anything under it (a marker's is that marker's, wherever it is drawn: `SpreadAt`), a click anywhere on a spread pile is on it, never off every marker, so it never lets the target go; off the spread labels, the first marker by its shape or label. |
| `VesselActions.cs` | A vessel's actions (`VesselAction`): what it can do now (`Of`: its bay's doors first, by what is wanted of them, then its legs; none once crashed), each one's label, how one goes into the flight (`Into`: the toggle in `FlightInput`, as the legs key's), and whether a click is on the vessel on the screen (`OnVessel`: within its radius of its axis, or `Scene.ActionMenuMinPixels`). |
| `ActionMenu.cs` | The flown vessel's actions menu's state (the HUD draws it as a popup where it was right-clicked: `ChaseCamera.TakeRightClick`): its items worked out again each frame (`Keep`), closed under a game menu, the map, or its vessel no longer flown, crashed or staged since it opened, and an item chosen given to the flight only for the vessel still flown, unstaged (`TakeChosen`, `Hud.TakeAction`); where its popup goes, kept on the screen (`Place`). |
| `Markers.cs` | The markers for things in the distance (vessels, places, every body by its name: any but the one the flown vessel is at clicked to target it), each labelled with its distance (a body's to its surface, over its datum: `LabelDistance`), each changed into the main view's frame from its own body's and hidden behind any body (`BodyFrames`): which show, in what order, how each kind looks, giving way, edge arrows, and clicking one to target it; and the mark on a vessel coming at the flown one (`Collision`: red, amber for a near miss), drawn whatever the settings, clicked as its marker. |
| `Comings.cs` | What is coming at each player-controlled vessel (`Flight.ComingAt`), kept each step from what the look ahead already asked (its fast collision from its warning on, its near miss with the setting on): what the small views flash for and the main view marks. |
| `NavTarget.cs` | A vessel's target (another vessel, a place: `Sites`, a mission's point of interest: `TargetKind.Point`, or a body other than the one it is at: `TargetKind.Body`, by its `GroundBody.Index`), named rather than held, so it saves as it is and one that has gone is simply missing. |
| `Settings.cs` | The player's settings, the same in every world and saved beside them: which kinds of marker show, whether the timings panel shows, whether frames are capped at the screen's rate (vsync; off by default, so the frame rate shows how high it goes), and "Slow motion for near misses" (`SlowNearMisses`, off by default). |
| `Paths.cs` | Draws the flown vessel's predicted path (and a dot where it is) over the view, fading in as the camera pulls back, hidden where a body is in the way; zoomed out, under it, the map's system (`SystemMap`); with nodes, the path they plan, over the orbit as it is; the marks on it (Ap, Pe, into the air, the ground), and with a target its path, the closest approach and the window (worked out a few times a second); the path patched at the spheres of influence, each leg (the flown vessel's, a flying target's) drawn round its own body (`LegPlace`: the last leg placed where the path comes to it, a pass point by point), its marks and nodes with it, the closest approach (the closest of every leg round the target's body, each over its own span) and a body target's over its datum, and the window only along the orbit as far as it is flown, shaded beside the line on its outer side (one side for the whole window, by its points' ways out from the body in the world as seen, `OuterSide`, so it never zigzags seen near edge-on); and the predicted collisions between any two vessels (`CollisionSearch`): a red cross where each is, with both names, in how long and how fast, and the stretch of each path into it (its last `Scene.CollisionLeadSeconds`), faint (`Scene.OtherPathAlpha`) for a vessel neither flown nor the target: the soonest `Scene.CollisionsShown`, laid once a pass, a cross within `Scene.CollisionMergePixels` of a sooner one's left out, and none drawn once dodged (`Scene.CollisionDriftMetres`) or from a pass over `Scene.CollisionStaleSeconds` old: each pair's in its body's frame, changed into the view's as it is drawn. The window, worked out round the flown vessel's body, is dropped and worked out afresh when that body changes (`Rebase`, on `Flight.ViewRebased`). |
| `Manoeuvres.cs` | Manoeuvre nodes: a planned burn (its seconds after the one before, and speed prograde, radial and normal, measured round the body it is at: `Manoeuvres.Frame`), the guide to flying the next one (delta-v left, overshoot, burn time, and whether the path flown matches the planned one: `MatchTolerance`), and the plan a chain makes: legs from its anchor with the engine off to each node, patched at the spheres of influence (each a `PathLeg` round its `Body`; `NodeLeg` the one a node's burn ends, `Final` the first after the last), its burn, and on. |
| `SystemMap.cs` | What the map draws of the system, zoomed out: the sun's mark, each body's path round what it goes round (`Bodies.Path`, faded in as the camera pulls back from the body, by the path's size), each body (and the sun) at least `Scene.MapBodyMinPixels` across, and every other vessel flying, faint (`Scene.MapOtherPathAlpha`): one in the physics patched at the spheres of influence each frame in the line's frame; one under formula its lap among its body's stars (out to where it leaves its body's pull, or to the first sphere it would enter), kept until its orbit changes and placed each frame by one transform for its body (one that doesn't come round, drawn from where the vessel is now). A lap too small, under its body's dot or off the screen is left out whole; each drawn in as many pieces as its size there needs (`Scene.MapLineSagPixels`), hidden only by the spheres that could cover it, and drawn as polylines. Allocates nothing. |
| `NodeEditor.cs` | Placing, dragging and deleting nodes on the zoomed-out path (each leg where `Paths` draws it, round its own body), and their handles; what is planned goes to the flight as its input. |
| `ManoeuvreChain.cs` | A vessel's nodes as a chain, fixed once set: its anchor (the state on the path the first sits on, at its time, in its body's frame as the ground was then), each node's gap after the one before, and the edits (`WithNode`, `Insert`, `MoveTo`, `Delete`), each redoing the chain after it; the first moved or the first deleted is anchored afresh on the vessel's path as it is. |
| `NavigationControl.cs` | The direction rings: drawn round the rocket's centre of mass when the pointer is near, dragged with the left button to hold a heading and pitch (`FlightInput.Hold`). |
| `Effects.cs` | Explosions (the rocket, or a dropped stage hitting the ground): fireballs, a flash of light and rising smoke, all worked out from the time since each blast. Also the splash where something hits the water: spray thrown out and up, falling back as it thins. Each kept in the frame of the body it happened at and drawn changed into the scene's, so one on the moon stays on its ground; at a body with no air, its flash and fireballs alone (they don't rise), no pall of smoke. |
| `SmokeStirs.cs` | How the booster stirs the smoke on the ground: a push where its body is and eddies in its wake, rings blasted out where the exhaust hits the ground, and how thick the cloud is at each pad. Passed to the smoke shader in the frame uniforms. Visual only. |
| `SmokeTrail.cs` | The smoke engines leave behind: a trail of puffs along the path through the thick air (about 90 s each, none above 2 km; only for a vessel at the planet, by its air), and low puffs pouring out across the ground where the exhaust reaches it. Drawn as smoke clouds, with the explosions'. |
| `GameView.cs` | Shows a flight each frame: camera, rings, effects, smoke, sound, HUD, small views and the renderer (and the floes in sight of each view's camera, posed as the physics has them). The camera and the HUD are in the flown vessel's body's frame (the camera rebased as that body changes), the renderer in the scene's (the planet's): each body's change worked out once a frame (`BodyFrames`), every vessel drawn by its body's (`SceneInput`). It never changes the flight, so the flight tests' watch mode (`GAMEONE_WATCH=1`) shows a test's flight the same way. |
| `BodyFrames.cs` | The bodies' frames as one frame is drawn: each body's change into the scene's frame (the planet's ground frame, the renderer's) and into the main view's (the flown vessel's body's, the camera's and the HUD's), each worked out in double once a frame on first asking; the sun's centre in the view's (`SunInView`); and whether a body or the sun hides a point from the camera (in double, as the map's camera is millions of metres out). |
| `SceneInput.cs` | What is drawn of every vessel each frame, for the renderer: each stage's model, every engine, plume and lit landing light, changed into the scene's frame by its body's change, the stages and the moon placed from the main view's camera (worked out in double first, so a vessel on the moon holds still to the millimetre), and the sun's disc as that camera sees it (`SunDisc`, `SunFrom`). Allocates nothing. |
| `Program.cs` | The game loop: input, the menus and world list (carrying out what is asked of it), the flight, saving the world, and drawing. |
| `Views.cs` | Which vessels are on the screen: the flown one in the main view, others in small views, each with its own camera. Opens a view for each new part that can be flown, a view at a body whose physics isn't made behind its link-up (`Flight.OpenView`), swaps when the flight flies another vessel, and asks the flight to (`FlightInput.SwitchTo`) when a view is clicked. Each view keeps what it held across the middle between two lit places (the main view's too), and a swap hands it on with its vessel. Each small view's camera is in its vessel's body's frame, rebased as the vessel crosses a sphere's edge. The HUD lays the views out. |
| `Hud.cs` | The overlay, drawn with Dear ImGui (`Brutal.ImGui`): the game menu (Esc), the mission menu, world list, carry-on screen (what a world just picked has to carry on), try-again window (start the mission again, or rewind), settings and world log screen, the word of a mission ended while another vessel was flown, the error sign, instruments (with the air, and the orbit with its period; all of the body the flown vessel is at, its air or that it has none, its orbit floor, its north on the compass), the compass, mission, controls, and messages, and with "Show timings" on, the timings panel (where each frame's time goes: the flight's physics parts, the frame's own, and the GPU's passes, a one-second average, bottom left over the folded controls, or else where it fits without covering the other panels or the credits). Its bottom-right panel shows the official BRUTAL logo and the text of `NOTICE.txt`, for the licence (below), in every menu and for the first 5 s of play. Over the main view, while a vessel at a body with no physics yet is switched to, the communications link-up (`Flight.LinkUp`: static, "ESTABLISHING LINK" and the vessel's name, clearing as it connects), and the same over a small view given to one (`Flight.ViewLink`). The strip (the small views along the bottom left, with the Add view, Reset camera (while the camera is not on the flown vessel) and Controls buttons over them) keeps its buttons below the instruments' panel however many lines it has, the views made smaller to fit under them (`StripPlace`). |
| `Timings.cs` | The game's own timings, in one place: each physics step's parts (rules, Jolt, the ground's tiles, the water, contact and nearness, the rest), each frame's (the flight, preparing, the HUD, waiting, recording the shadow pass and the rest, submitting) and the GPU's passes, kept in ring buffers that allocate nothing, and a one-second average for the panel. Recorded only while on ("Show timings", or a test): off, each lap is a bool read. The short performance tests read it. Each frame also counts the world: the crafts awake, frozen and afloat in the physics, the ground's tiles, and the vessels out of the physics, under formula. |
| `Readout.cs` | GameOne's whole side of the GameDriver's interface: the HUD's readings as text (`hud Altitude=4120 m` lines on stdout, as they change), only when the GameDriver asks (`GAMEDRIVER_READOUT=1`), so its scripts can wait on what the HUD shows (including `State`, the flown vessel's state: flying, landed, floating, crashed, and `Link`, the link-up's: connecting, connected; `Link <vessel>`, a small view's link-up's; `Body`, where the flown vessel is, every reading after it that body's; and each body's distance and height in the sky, by its name). |
| `Audio/AudioSystem.cs` | The OpenAL device and context shared by all sounds, and the listener at the camera. |
| `Audio/EngineSound.cs` | The engine: rumble, roar and crackle synthesised in code and played through OpenAL (`Brutal.OpenAl`) from the nozzle, following the throttle, and more muffled the further off the camera is. |
| `Audio/ImpactSounds.cs` | Touchdown, leg, splash and explosion sounds, synthesised at start-up and played in 3D where they happen (each changed from the frame of the body it was made at into the listener's). |
| `Rendering/Meshes.cs` | The shared vertex format, a small mesh builder (boxes, girders, turned shapes), the pads, the lattice tower, the drone ship and the floe (one unit disc, scaled and posed per floe by `FloeModel` to the cylinder its hull is). |
| `Rendering/RocketMesh.cs` | The booster (charcoal body with BRUTAL-yellow stripes, grid fins, engine bell, the BRUTAL logo wrapped up each side, and a nose cone or an interstage), the upper stage under its fairing (or open, to carry a satellite), the satellite (gold, with solar panels and an antenna), the lander (a square frame, its bay open at two faces, tanks on its deck, an antenna) and its bay's doors (each three panels, drawn folded or unfolded: gold outside, a grey ramp deck inside, the last with a yellow edge), each one's fairing (two halves, drawn round it while on; the lander's wider, with a cone in to the stage at its foot), and the legs. |
| `Rendering/Shaders.cs` | All the GLSL: the planet (patches of a cube's faces pushed out onto a sphere and displaced by the terrain, built in the vertex shader, with the fine detail repeated from `Terrain.Detail`; the ground's colour read per pixel: near, from the map's kinds, their edges wandering, cross-faded out where a texel is down to a pixel or two to the mipmapped colour map's filtered read), lighting (sun with soft shadows, coloured and dimmed by the air it crosses, sky light from the air's tables, the moon's light (unshadowed), the flame), the haze between the eye and what it sees, marched through the air (in a few steps, closer together where the air is thick, and only along the stretch the sun lights), the sea (its waves lifting the water and tipping its normal, its colours by depth from the seas' colour map, filtered, the waves' phases passed in the frame uniforms, and far off the floe band's water tinted towards the ice by the floes' cover), the floes (one instanced draw, each one's model from a storage buffer, their ice the frozen sheet's colour, each floe a little brighter, duller or bluer by its id, wet and darker round its rim), the sky (the air's light marched along each view, as `Air` does) with its rim glow, stars, the sun's disc at its true size and the moon (ray-cast from the camera, its ground read from its map through its own axes, lit by the sun with its ground's shadows, the planet's shadow and planetshine, under the haze), smoke, fire and the lens flare. |
| `Rendering/FrameInput.cs` | Everything the renderer needs for one frame, in the scene's frame (the planet's ground frame): each stage's model and the moon placed from the main view's camera, each small view's moon from its own, each view's sun's disc as its camera sees it (`SunDisc`), and each view's near and far planes (its camera's). |
| `Rendering/GpuTexture.cs` | Decodes a PNG with `Brutal.Stb` and uploads it through a staging buffer into a device-local image. Used for the logo, and for raw layered textures (the terrain's maps, the air's tables), filtered linearly if asked, with mip levels given (the ground's colours, the waterline, the seas' colours). |
| `Rendering/VulkanRenderer.cs` | Device setup, per-frame uniform buffers and descriptor sets (one set per view), the floes' per-frame model buffer (drawn instanced, in the views and the shadow pass), and the frame loop: shadow pass, then scene pass, its depth reversed (`CameraProjection`: cleared to 0, tested `Greater`, the far plane at infinity; the shadow map's not). Every mesh's model is pushed from the bound view's camera (`FromEye`; the stages', given from the main view's, moved by the step to this one's), and the shaders place it from there, so what is near a camera tens of kilometres out (on the moon) holds to the millimetre. |
| `Rendering/VulkanRenderer.Timings.cs` | The GPU's side of the timings: timestamp queries at the frame's start and each pass's end (shadows, scene, effects, small views, HUD), read back after the frame's fence so nothing stalls, only while timings are on and where the queue writes timestamps. |
| `Rendering/VulkanRenderer.Views.cs` | The small views: each drawn into its own rectangle of the scene pass after the main view, with its own camera's uniforms (its own light set's lights, and its own sun area's shadows), its depth written afresh by its sky (which always passes): the sky, a coarser planet, the pads, the tower, every stage and the fire, through its own camera's planes. |
| `Rendering/VulkanRenderer.Shadows.cs` | Shadow mapping, into one depth map laid out in 1024² cells, 11 by 3: the main view's sun area (an orthographic box that follows the booster, and takes in the pad and tower when it is near them) two by two cells in the top left, a cell for each small view's own sun area (round its own vessel, over the same ground), and a cell for each floodlight tile, four to a block, a block for each light set whose floods cast shadows. Only the squares in use are cleared and drawn, each sun area's with only what is in it. |
| `Rendering/VulkanRenderer.Swapchain.cs` | Swapchain, 4x MSAA target, depth buffer and main render pass, rebuilt on resize. |
| `Rendering/VulkanRenderer.Pipelines.cs` | Shader modules and one pipeline builder for every style used: scene, shadow, sky, translucent (smoke), glow (fire), additive (flare) and invisible probes. The camera's depth is reversed (`Greater`), the shadow map's not (`Less`). The sky writes depth (always passing): the moon's or the sun's where it covers a pixel, else the far plane's. |
| `Rendering/VulkanRenderer.Sky.cs` | The sky: one screen-covering triangle drawn first, its colour the air marched along the view direction (the sun's light scattered, and dimmed by ozone), with the stars, the sun's disc (where the sun is from each view's camera, `FrameInput.SunDisc`) and the moon (its centre from each view's own camera and its axes, in the frame uniforms; its map's textures are made with the terrain's), writing the moon's depth where it covers a pixel (a sphere under its lowest ground by two of the depth's steps, at least a metre, or round its limb the middle of the ray's way through it), else the sun's where its sphere does, else the far plane's, so whatever is behind either is hidden by it. |
| `Rendering/VulkanRenderer.Smoke.cs` | Smoke: camera-facing puffs whose billowing is noise in the fragment shader, lying round each pad (where they are is worked out from the time alone) plus explosion clouds. Drawn after the solid scene, depth-tested but not depth-writing. |
| `Rendering/VulkanRenderer.Glow.cs` | Fire: the exhaust plume with its shock diamonds, the glow at the nozzle and where the exhaust splashes on the ground, and fireballs, and every lit vessel's far glow (one instanced draw a view). Added onto the scene, depth-tested. |
| `Rendering/VulkanRenderer.Flare.cs` | Lens flare from the sun, its strength from how much of the sun is visible (a probe the size of its disc just in front of its sphere, where it is from the camera), measured with Vulkan occlusion queries and read back a frame later so nothing stalls, and dimmed as the sun's light is by the air along its ray (`Air.FlareDimming`), and in the map to `Scene.MapFlareShare` (`MapFlare`, by `FrameInput.MapShown`), so its ghosts are faint over the paths. |
| `tools/TerrainGen` | Makes the terrain map from an authored layout (`layout.json`), during development only: land and seas, ranges, erosion, the levelled sites, and the kinds of ground; and with `--moon` the moon's from `moon.json`. The same layout always gives the same bytes. See its README. |

### Why the planet is small

Positions are single-precision floats. On a 10 km planet nothing is ever more than a few tens of
kilometres from the origin, where floats are still good to a few millimetres, so neither Jolt nor the
renderer needs double precision or a floating origin. A bigger planet (or flights much further out)
would need one.

The logo texture is `Brutal/docs/branding/LOGO_BRUTAL_WHT.png`, copied to
`Assets/brutal-logo-white.png` in the build output. GLSL is compiled to SPIR-V at start-up by
`Brutal.ShaderC`, so the Vulkan SDK isn't required. If it's installed, the Khronos validation
layer is enabled automatically.

### Notes on BRUTAL's Vulkan binding

- Wrapped calls throw `VkResultError` for `VK_ERROR_*` results instead of returning them. That
  includes `VK_ERROR_OUT_OF_DATE_KHR` from `AcquireNextImageKHR`/`PresentKHR`, which happens on
  window resize, so the renderer catches that specific error and rebuilds the swapchain.
- The renderer prefers a discrete GPU, so on a hybrid laptop it runs on the dedicated one.

### Notes on BRUTAL's Jolt binding

The Jolt binding was generated against a newer `jolt.dll` than BRUTAL first shipped: the
2026.10.2 package's lacked 774 of its 3,246 native entry points. Since BRUTAL v2026.10.1
(`Ahwoo.BRUTAL.Jolt.Native` 2026.10.4) every one is there and works, but the game was built
round the gaps and still is, until each is replaced (see "Using Jolt fully" in `TODO.md`):

- Shapes are built as `XShapeSettings.Create(...).CreateShape()`.
- The booster's collider is a compound of cylinders and boxes, and floes are discs, as mesh and
  convex-hull shapes couldn't be given geometry.
- Touchdowns, crashes and hits are judged from Jolt's contacts (`RocketPhysics.TouchesBegun`, read
  from the native `ContactEventListener` queue after each step), with the game's own care where
  Jolt says nothing: a sleeping body removed reports no contact ending, so the game forgets a
  removed body's contacts itself; the legs' landing is found by the legs (`Cushion`), as they
  hold the craft up themselves; and a part rocking down about a contact already held makes no new
  contact, so the body's and the bell's lowest points are still measured near the ground.

## Adding more BRUTAL libraries

1. In `../Brutal`, add `Brutal.X/Brutal.X.csproj` following the existing ones. Get its
   project and package references from the `Ahwoo.BRUTAL.X` nuspec. If it wraps a native library, add
   `<PackageDownload Include="Ahwoo.BRUTAL.X" Version="[ver]" BrutalNative="true" />`.
2. Add it to `../Brutal/Brutal.Local.slnx` and `GameOne.slnx`.
3. Add a `ProjectReference` to `$(BrutalRoot)Brutal.X/Brutal.X.csproj` in `GameOne.csproj`.

## Licence obligations (BRUTAL Licence v1.0)

- Credits screen or equivalent includes the BRUTAL copyright notice (§2.1). Done: the HUD shows it in every menu (the world list the game starts on, and the game menu on Esc) and for the first 5 s of play (`Scene.CreditsSeconds`), then gives its room back; read word for word from the shipped `NOTICE.txt`.
- BRUTAL logo shown before gameplay, unaltered except for scaling (§2.2). Done: the HUD shows the official logo image, only scaled, from the first frame, on the world list before any play. The logo wrapped round the booster is decoration: it alters the logo, so it is not what meets this clause.
- `NOTICE.txt` ships alongside the product (§2.3). Done, it's copied to the output.
- Third-party licences (§4.0): each BRUTAL library's `LICENSES` folder is copied to `LICENSES/` in the output (e.g. OpenAL Soft is LGPL).
