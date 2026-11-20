# GameOne.Tests

End-to-end flight tests. Each one flies a whole flight through the game's real flight rules
(`Flight`) and Jolt physics (`RocketPhysics`), with no window, rendering or audio, stepped in fixed
60 fps frames as fast as the CPU allows. A two-minute mission takes under a second, about 70–80x
real time (a lap of the planet in orbit, ten minutes of flight, takes under ten seconds), and every
run is the same.

```sh
dotnet test GameOne.slnx
dotnet test GameOne.slnx --logger "console;verbosity=detailed"   # with each flight's report
```

To **watch** tests fly, in the game's window in real time, set `GAMEONE_WATCH` (a number above 1
starts that many times faster). While watching, change the speed to get to the part you want to
see: `]` / `+` doubles it, `[` / `-` halves it, `0` is real time and Space pauses; a caption at the
bottom shows the speed and the flight time. Run one test at a time with `--filter`. A long test
(see below) is skipped, and so shows nothing, unless `GAMEONE_LONG=1` is set as well, as for the
orbit test here:

```sh
GAMEONE_WATCH=1 dotnet test GameOne.slnx --filter "FullyQualifiedName~Autopilot_flies_the_mission_and_lands"
GAMEONE_LONG=1 GAMEONE_WATCH=4 dotnet test GameOne.slnx --filter "FullyQualifiedName~Autopilot_reaches_a_stable_orbit"
```

**The slow-downs to watch** are there to judge how they feel: `NearMissTests.A_near_miss_to_watch`
(a booster passing the flown one at 1,000 m/s, within a length, with "Slow motion for near
misses" on) and `A_top_speed_near_miss_to_watch` (8,000 m/s), and
`FastCollisionTests.A_fast_collision_to_watch` (two boosters hitting head on at 1,000 m/s) and
`A_top_speed_fast_collision_to_watch` (8,000 m/s), each from its warning to the end of its
slow-down. They read their feel from `Scene`: `SlowdownFrom`, where the slowing begins;
`SlowdownPassSpeed`, the speed to the eye at the pass; `SlowdownHoldWithin`, how far either
side of it that is held; `SlowdownEaseSeconds`, how long it eases back; and for the near misses
`NearMissLengths`, how near is near, and `NearMissSlowAbove`, how fast a pass must be to be
slowed. Change one there and watch again. The viewer is paced by real frames, as the game is, so
a slow-down is seen at its real pace. In any
watched test, once something is coming at the flown vessel the camera tracks it to the end,
swinging round the flown vessel to keep the other in view as it comes and goes (the mouse still
tips and zooms it).

```sh
GAMEONE_WATCH=1 dotnet test GameOne.slnx --filter "FullyQualifiedName~NearMissTests.A_near_miss_to_watch"
```

The flight is exactly the one the headless test flies; the window just shows it (`Watcher`, through
the game's own `GameView`). Turn the camera with the right mouse button and zoom with the wheel.
Close the window and the test carries on headless.

**Long tests** take longer than the test record's limit (`tools/TestRun`'s `Report.LimitSeconds`),
so the normal suite skips them unless `GAMEONE_LONG=1`, which the runner's `--before-merge` sets:
the long playthroughs (`[PlaythroughFact(Long = true)]`, whole missions flown from the pad) and the
long checks beside a quicker one of the same rule (`[LongFact]`). Each also carries
`[Trait("Run", "Long")]`, by which the runner knows a normal run skips it.

```sh
GAMEONE_LONG=1 dotnet test GameOne.slnx --filter "FullyQualifiedName~SatelliteTests"
```

**Running them in parallel:** `dotnet run --project tools/TestRun` (see its
[`README.md`](../../tools/TestRun/README.md)) splits the normal suite across processes;
`-- --before-merge` adds the long tests. `TestTimes.json` is its record of each test's seconds
(per machine, Debug's), which balances the shards and shows the tests much slower than before and
those over the limit; how and when to retake it is in [`CLAUDE.md`](CLAUDE.md). The playthroughs
are `[PlaythroughFact]`s, found by name and by `--filter Kind=Playthrough`, quick and long alike (a
long one only runs under `GAMEONE_LONG`). A test marked `[Trait("Run", "Alone")]` runs by itself
after the others. Plain `dotnet test GameOne.slnx` is still a serial run of the normal suite.

**Soak tests** (`SoakTests`) look for leaks over long play. Leaks grow with what happens (frames
drawn, flights, staging, crashes, restarts), not with the clock, so they fly hours of play in
minutes: every mission, done right and wrong, over and over (one flight splashes down offshore,
near enough to the pad that, in one world, it floats there awake through the flights after it). They take minutes, so they are skipped
unless `GAMEONE_SOAK=1`:

```sh
GAMEONE_SOAK=1 dotnet test GameOne.slnx --filter "FullyQualifiedName~SoakTests" --logger "console;verbosity=detailed"
```

- **Headless** (`Hours_of_headless_play_leak_nothing`): 120 minutes of play (`GAMEONE_SOAK_MINUTES`)
  through the flight rules and Jolt, in about 90 s, each flight in a fresh, empty world.
- **Headless, one world** (`Hours_of_play_into_one_world_saved_and_loaded_after_every_flight_leak_nothing`):
  the same playlist flown into one world, which fills up with what each flight leaves, saved and
  loaded back (through a `WorldStore` in a temporary folder) after every flight, and started over
  every 6 flights; memory is sampled each time it starts over. It fails too if the world never
  held at least 5 vessels.
- **Rendered** (`Forty_minutes_of_rendered_play_with_views_coming_and_going_leaks_nothing`):
  36,000 frames (`GAMEONE_SOAK_FRAMES`) drawn through the game's own view with vsync off, a few
  hundred frames a second, so the renderer, HUD and audio are soaked too. A frame is drawn every 4
  steps of the flight, as 4x time warp does, so the flights come round sooner: forty minutes of
  play.
  Small views come and go all through it: every 90 frames a view of every vessel without one (as
  "Add view" would), every 240 the oldest closed, besides the views each flight opens and swaps
  itself. It is flown into one world (started over every 6 flights), so there is more to give
  views to and the HUD lists a busy world. It fails too if no more than one small view was ever
  open. A window opens: leave it open.

Each samples the managed heap, the process's private memory, its OS handles, the Jolt bodies and
the ground's height-field tiles as it goes, and fails if they keep growing after a warm-up, if a
fresh rocket on the pad ever leaves a different number of bodies (the tiles aside, which come and
go with where things are), if the tiles are not let go
(more than a handful round a fresh rocket on the pad) once a flight is over, or if drawing fails.
Each prints its samples, to see the trend (and the most tiles at once).
Leaks tied to the clock itself (per second rather than per thing done) would still need a long
real-time run; nothing in the game works that way today.

**The performance test** (`PerformanceTests`) times frames with several views open: the main view
plus 0 to 5 small ones, in the game's 1280x720 window, through the game's own view with vsync off.
It needs six vessels, so it flies a six-part test rocket (every part with guidance), drops every
stage, lights every engine, then holds the flight still while each number of views is drawn: a
warm-up, then 600 frames timed. It prints the median, 95th percentile and worst frame time for 1
to 6 views, and fails if the main view and 3 small ones take more than 16.7 ms (60 fps) for the
median frame (spikes from a busy machine don't fail it). Another does the same over the Austral Sea's floe band, then times the physics there:
with two dozen more boosters crossing the band low (as many floe bodies as `Scene.MaxFloeBodies`
allows), and with one booster flown down onto a floe until it lands; each must take no more than
half a frame (8.3 ms) for the median frame. Another puts the main view and three small ones at four lit places (Home's pad, Desert's,
Highcrest's and Sentinel's drone ship), so the shadow map holds four light sets' floodlight shadows
and four sun areas, twice: with the world started at Home's noon and again at Home's midnight, each
printing which places are by day, and timing it with the main view at Home and again at Highcrest,
to the same 16.7 ms. And the six-part rocket's case is run again at dusk, in a world started at
Home's sunset (the sky and the haze at their costliest to work out through the air), to the same
16.7 ms. Another puts a hundred landed boosters, every one's landing lights on, in a world at
Home's midnight with the rocket on the pad (twelve within 500 m of it, so each view has its four
ground lights, and the rest far glows), to the same 16.7 ms. The last puts five upper stages in orbit together, 1.5 m apart, all five in the main
view (the camera placed to see them, `ChaseCamera.Place`), with 4 and 5 small views, to the same
16.7 ms, and prints where the frame's time goes (`Timings`: the CPU's parts and the GPU's passes).
And one flies, rather than holding the flight still: the lander alone on the very low orbit of
the moon (`LowPass.Start`, found over three laps, 30 m over the highest ground its track crosses),
the main view on it and three small views of three more landers on the same track 30, 60 and 90 s
ahead, each frame of 60 fps's real time run and drawn (in warp within the game's own budget for
its steps, `Flight.WarpBudgetSeconds`, 12 ms), at normal speed and again with the highest warp
asked (32x; timed at whatever the game leaves it at): the moon's ground streamed in fast under the
cameras, the tiles laid and the patches chosen as it goes. Each to the same 16.7 ms for the whole
frame, printing the warp the game left and the warp reached, the ground tiles laid, the frame's parts, and the
tiles' and the terrain patches' (`Timings`). It is the moon feature's before-merge performance run.
It needs a GPU and a desktop, so it is skipped unless `GAMEONE_PERF=1`:

```sh
GAMEONE_PERF=1 dotnet test GameOne.slnx --filter "FullyQualifiedName~PerformanceTests" --logger "console;verbosity=detailed"
```

Leave the window alone while it runs: anything that captures or covers it (a screenshot tool,
say) stalls frames and spoils the timing.

- **`Simulation`:** the game's flight without the game. It creates the physics and the flight, then
  steps them with a pilot until a condition is met (`Run`) or the flight ends (`RunToEnd`), a real
  frame at a time by `Flight.RunFrame` as the game does (so the player's rate, the warp and the
  forced slow-down all run as in play), telling the pilot the frame's game time.
- **`Autopilot`:** flies the landing mission as a player would, only through `FlightInput`, the
  same input the keyboard produces. It presses launch, climbs, coasts, brakes on the way down,
  steers to the target, puts the legs out, then holds over the target and settles. Its settings
  make it get things wrong on purpose: no legs, landing too hard, the wrong target, or leaning one
  way the whole time.
- **`OrbitAutopilot`:** flies the orbit mission the same way: a gravity turn east, dropping the
  booster when it runs dry (or at a given fuel level), a coast to the top of the arc, then a burn
  along the direction of travel until the orbit clears the atmosphere. It can be told not to stage,
  or to go straight up.
- **`Autopilot.ReturningBooster()`:** the landing autopilot set up to bring back a booster dropped
  on the way to orbit: it falls fast, brakes late and hard, and leans harder to lose its speed.
  **`OrbitAndReturnPilot`** flies both, switching between them as a player would (for the soak).
- **`SatellitePilot`:** flies the satellite mission, every vessel in turn: up as for orbit, landing
  the booster while the upper stage coasts; jettisoning the fairing above the atmosphere;
  circularising and releasing the satellite; then the
  upper stage on round the planet until home is coming up, a burn just long enough that it would
  fall there (worked out by `Pilots.HowFarItFalls`, which follows a fall through gravity and the
  air as the physics does), and a landing; then back to the satellite, to raise its orbit to 10 km:
  a burn along its direction of travel, a coast up to the top, and a burn there to round it out.
- **The frames' pilots (`FramePilots.cs`):** fly by the frames of reference with only what the player
  has: the frame selector, the holds, the throttle and the direction rings, and what the HUD shows
  (the travel markers' velocity in the frame in use, the relative speed, the way to the target and
  "To target", the air, and the line's landing mark, built as `Paths` draws it: `FrameReadings`).
  `HoldLanding` lands on the target on the retrograde hold and throttle alone, braking with
  whatever throttle its own reckoning says stops it over the target; `ComingHomePilot` brings a
  stage down from orbit on Auto (a retrograde burn until the landing mark has swept back to just
  beyond the landing zone, then `HoldLanding`); `HopPilot` hops from the pad to a place nearby on
  the surface frame, steering the landing mark beyond and onto it, then lands by `HoldLanding`;
  `RendezvousPilot` closes on another vessel in orbit by the target frame alone. They read the
  body the vessel is at (its ground, pull and air), so `HoldLanding` lands on the moon too, on a
  place picked out there (`Place`) rather than a target, cutting the engine as its braking stops
  (`CutAtStop`) where the stage pushes so much harder than the moon's pull, and, for an engine
  only a little stronger than the pull (the lander's), coming straight down no faster than
  braking at 80% of its push would stop by the ground (`DownBrake`). It reckons the way to go at
  its own height (the ground's distance by its height over the body's radius).
- **The moon trip's pilots (`MoonPilots.cs`):** fly the flown vessel to the moon as a player would
  (the upper stage, alone or with the lander on it, and the lander down), each by its own stage,
  through `FlightInput`, by the window, the line patched across the spheres (`MoonReadings`), the
  holds and the HUD's orbit readouts. `TransferPilot` targets the moon, waits for its window and
  burns at its best place along the way it goes until the line's moon leg has its periapsis under
  1 km over the moon's datum, trims the path out on the transfer (the least push, by its own
  reckoning from the line, that puts the moon leg over the moon's poles with its periapsis 400 m
  over its highest ground) and goes on its orbit with the warp up to the sphere; `CapturePilot`
  burns against the way it goes round the moon, centred on the periapsis, down to a round orbit's
  speed; `MoonLandingPilot` lines its track over the ground up with a `PointOfInterest` (the
  moon's landing spot by default; a burn square to its orbit), burns against the way it goes at
  2 m/s² until the landing mark is its lead beyond the spot (as far as braking from 114 m/s at
  its push would take: some 300 m for the upper stage, 2.2 km for the lander), lands there by
  `HoldLanding` with its legs put out 600 m up, and, landed, opens a bay's doors
  (`FlightInput.ToggleBayDoors`). `LanderMissionPilot` (`LanderPilots.cs`) flies the moon landing
  mission leg after leg from the pad, or from any leg on: `PlaneAscentPilot`, `TransferPilot`
  (from the next window on the orbit the ascent leaves it on), `CapturePilot`, the release (the
  nose held square to the orbit round the moon 3 s, then staged, and held 5 s more while clear,
  so the push apart barely changes either orbit) and `MoonLandingPilot`, each leg's end kept with
  the fuel then (`Ends`).
- **`FlightTests`:** the landing mission succeeding (and, flown on, a landed booster that takes off
  again keeping its landing and its score), and each way of failing it (engine bell, too
  fast, tower, off target), plus fuel, the launch (Space lights the engine at the throttle set) and starting the mission again.
- **`EncounterTests`:** where an `Orbit` says a vessel will be and when (against gravity stepped
  by hand from several climbs and speeds, and a vessel in orbit in the real physics a minute on);
  its highest and lowest points ahead and where it comes into the air; the closest approach
  (`Encounters`) where two paths meet, and to a place on the ground followed round as the ground
  turns; the transfer window where one burn would reach a target on a higher circle; the de-orbit
  window where a burn would bring it down by a place, always within two laps, one across the
  lap's end whole (one stretch on into the next lap), and a leg starting later finding the same
  windows shifted by its start; and the window shaded on the orbit's outer side, one side for
  the whole window (`Paths.Across`, `OuterSide`: away from the body's middle all along a window
  on each eighth of a circle run either way, none across a line with no length; a straight
  stretch, as an orbit near edge-on, its points' ways out mostly one side and some the other,
  shaded on the most's side all along, and over the line with no say).
- **`OrbitTests`:** a narrow ellipse (0.93, a stage thrown high and falling back) sampled 48 pieces a lap with no piece further from the curve than a circle its major axis across would be, where sampled evenly in its true anomaly its pieces bow out more than five times that; a path straight up and down moved by Kepler's equation for a line (falling back, just escaping and escaping: within 0.5 m and 0.01 m/s of the line stepped finely, its top where that has it, and the time it rises through 97% of the way up found to a centimetre, from three heights at three shares of the escape speed); reaching a stable orbit and staying in it for a whole lap; in the same flight,
  dropping the booster at its landing-fuel mark, landing it on the drone ship and then reaching
  orbit (every step, the bonuses too); landing the booster off the pad, or crashing it, still
  succeeding but scoring less; failing without staging, the upper stage lighting after
  separation, the dropped booster crashing if left alone, and reaching space without orbit not counting;
  and an orbit round any body: a circle 500 m up round the moon with the moon's period (within
  0.1 s) and closed, and a low orbit round the planet (4 km up) one patched leg, worked out apart
  from `Orbit`: its period Kepler's (within 1e-5 of it), closed and circular, and every point over
  a lap within 2 cm of the circle turned at its rate.
- **`SatelliteTests`:** the satellite raised to its orbit about 10 km up (and staying in the band
  for a lap, with a fifth of its fuel to spare) with the booster and upper stage both landed on
  their landing zones (every step and bonus), the upper stage landed on the zone by the pad (flown
  home from orbit from the west, over the Westwall range); the upper stage leaving it in orbit but too low, so
  its own engine is needed; staging jettisoning the fairing (once the booster is gone, before
  the satellite is let go) in two halves that fall as debris and come to rest without exploding;
  the satellite breaking up if the fairing comes off low and fast; the fairing colliding until it
  is jettisoned (the stack dropped upside down rests on its tip, or on the antenna without it);
  the upper stage landed away from its zone
  still succeeding but scoring less (the on-the-zone flight it compares with is the first test's,
  flown once for both by a class fixture); releasing the satellite leaving the upper stage behind, with
  its legs.
- **`FrameFlightTests`:** whole flights flown by the frames' pilots: the orbit mission on Auto
  turning orbital once, as it climbs past the air's top and the margin (its periapsis still in the
  air), before its orbit step is ticked, and staying orbital through the burn and on in its low
  orbit, over high ground too; the
  satellite mission's upper stage, put in orbit, brought home on Auto (orbital through its burn above
  the air, surface once down in it) onto the landing zone; a rendezvous in the target frame, closing from 1 km to
  under 50 m and holding there, under 0.5 m/s; and a hop from the pad onto the nearest test pad in
  the surface frame, by its line's landing mark.
- **`FrameTests`:** the frames of reference one rule at a time: Auto by height over the datum,
  not by the orbit, against the higher of the orbit floor and a share of the radius (the planet's
  and the moon's floors, a body ten times the moon's size its share; each side of the height and
  the margin, held between; a low orbit over the planet's highest ground, 2 km over it, orbital
  and staying so; a booster above the air with its
  periapsis under the ground orbital, 400 m up surface; a frame picked by hand kept as a booster thrown up through the floor passes both switches); the
  line among the stars round its body in every frame (a hop's and a de-orbit's real path above the
  air on it, its landing mark on the ground it comes down on; the straight-up top among the stars;
  an orbit the same closed ellipse in every frame, round the planet and round the moon; a later
  lap's window and landing marked as the next lap's); a suborbital hop's line starting along
  orbital prograde, within 3°, in Orbit, and in Surface still along the velocity among the stars
  while the travel marker's way is the ground's.
- **`FreePlayTests`:** free play: the satellite launcher with no steps, its vessels of no run, so
  never won, failed or scored (on the pad, past 400 m, after staging), nothing its landing site; its booster flown down
  and landed, and still nothing won.
- **`LegTests`:** the crush legs: a touchdown taken over a moment as they give, coming to rest on
  them without bouncing, and coming down off centre, the first feet down slowing it for the rest.
- **`StageShapeTests`:** each stage's legs and fairing by its own shape (`LegGeometry`,
  `FairingGeometry`): the existing stages have the rocket's legs and the satellite's fairing; a
  stage with longer legs stands on its own feet; a payload's fairing sets its height, width and mass.
- **`LanderTests`:** the lander and its launcher: its engine's numbers (1.8 times its full weight
  on the moon; weaker than the upper stage's, more efficient), the rover's 300 kg in its dry mass,
  it and its stowed legs (every point of their mesh) inside its fairing, the stack's weight for its
  booster; dropped on the moon's plains at the landing spot on its legs (`LanderTests.Alone`), and
  on its bell (landed at 2 m/s, wrecked at 4); its landing lights in the moon's night; and the
  launcher's fairing jettisoned as two lander fairing halves, then the lander let go without a touch.
- **`MoonMissionTests`:** the mission LAND ON THE MOON: its landing's points for nearness to the
  spot (3,000 on it, 1,500 at 500 m, none from 1 km, never rising with the distance); the lander
  counted down only landed on the moon (not flying, not landed on the planet) and its bay only
  fully open there; from the pad, the lander released at 2.5 km and lost fails it (its two steps
  lost), the booster lost only its bonus, and the upper stage lost once the lander is away
  nothing; the mission saved with its lander landed by the spot and carried on where it was (no
  fresh rocket, its steps still done, its bay then opened ticking the last); and the satellite
  launcher and the missions that fly it as they were.
- **`VesselActionTests`:** a vessel's actions (`VesselActions`, `ActionMenu`): the lander offering
  its bay doors and its legs, the launcher and its upper stage (each a stack) their bottom stage's
  legs, a satellite nothing (and no menu); each item saying what it will do now, going into the
  flight as the legs key does (`Into`), the legs key still working with the menu open; a click on
  the flown vessel on the screen and 60 px beside it, and on one 5 km off within
  `Scene.ActionMenuMinPixels` (`OnVessel`); and the menu closing, nothing chosen given to the
  flight, on Tab, a crash, a game menu, the map, and the lander staged away from its upper stage
  (the vessel flown goes on as the lander, its stages changed: what the menu offered was the
  upper stage's), one on the upper stage left behind closing too; and the popup placed at the
  click, moved in from the right and bottom edges to stay on the screen (`ActionMenu.Place`).
- **`BayDoorTests`:** the lander's bay doors: open and shut in their 6 s (the ramps in the collider
  only fully open); fully open on level ground each one straight ramp, its panels in line, about
  15° below level and its tip 0.1 m over the ground; shut, its panels inside the fairing and more than
  2 cm from the stowed legs, and its collider out at the folded panels; their swing, sampled,
  never into the ground, the folded panels never into the body, none within 2 cm of the legs out;
  stopped short on a 5°, 10° and 15° slope (the lander upright), the unfolding never into it; landed at the landing spot, their ramps' tips within 0.2 m of the ground; on
  the planet's 10° test pad, the legs pushed up by its pull, each door stopping on the ground, and
  the lander, woken, neither lifted nor tipped, open or shut again; opened before a touchdown at
  2 m/s, judged as any landing; on the 10° pad come down with them open, the ramps raised onto the
  ground (never faster than the doors' pace) and the lander standing where it does with them
  shut; a hop with them open, the ramps back in the collider clear of the ground, out coming
  down, and on the ground landed again; landed under formula with them open on the 10° pad
  (upright, the upslope ramp raised onto the slope), handed back and settled onto it, the ramps
  found again and on the ground; swinging on level ground, the
  frozen lander never woken (and its open colliders made only as they first open); its outline
  (what touching is told by) reaching out to the ramps; opened in an orbit round the moon, the
  orbit unchanged; none on a booster (and no stops saved); the open collider kept under formula
  and back; saved opening, loaded and finishing.
- **`SlopeLandingTests`:** standing on a slope, on the test range's pads and on real ground (placed
  by `Drops`): the legs sharing the load then locking, gripping rather than sliding, a vessel
  standing near upright across a gentle slope and staying put on a steeper one, landing and
  toppling judged from the vessel's own shape (landed at rest standing at any lean, toppled when
  its centre of mass is past its feet), and touching down only within the safe lean.
- **`TestPadTests`:** the test pads' collider against the drawn plinth, and a box dropped on each
  pad (and beside it, on the ground) coming to rest.
- **`TerrainTileTests`:** the height-field tiles the ground is collided with: the sample count
  Jolt accepts, every cell inside its tile (even at a cube corner), the tiles a craft needs
  covering the ground round it, a tile following the solid ground (and the seabed over the sea),
  tiles building quickly and made again from their kept shape, a booster dropped on open ground or
  at a cube corner resting on it, a box resting at the terrain's height, a booster flown into a
  mountainside crashing, and nothing falling through low and fast over rough ground, at a steep
  slope or in a crash (a theory). Frozen boosters: one at rest freezes without tiles, wakes when
  flown (or its legs move, or its stage jettisons its fairing) and sleeps through a launch beside
  it, and a world of many loads frozen. *A field of frozen boosters waking only the row flown over,
  with none falling through* is two tests of one body: the normal one has 49 boosters on the zone's
  flat top, each as crowded on its tile as in the whole field, with a flyer passing low over the
  middle row; the long one (`[LongFact]`, before a merge) is the whole field of about 170, its row
  running over the ground's fall too. Tile bodies' own ids, a slot made again past its sequence numbers, tiles let
  go once nothing needs them, a craft's tiles chosen by its bottom end's height (and none far above
  the ground or over a floe), the background worker's shapes made ahead of need and laid when
  urgent (with no second body for a late one), a full set of tiles keeping a finished shape, and
  the urgent stretch shortening with the step (a twentieth of the step, a twentieth of the cells).
- **`TileWorkerTests`:** the tile worker (`TileWorker`), through a world's jobs on it (`TileWorker.Jobs`):
  the soonest wanted made first, a cell no longer wanted dropped, a cell in hand or finished never
  made twice, two worlds' jobs on one worker made soonest first across both and each taking only
  its own, an exception rethrown on the main thread, and a world's jobs closed mid-tile waiting for it.
- **`SeaTests`:** the one wave function (`Sea`): the same every time, rising and falling about sea
  level, smooth in time even after hours (the phases are worked out in double), the water running
  forward under the crests, continuous across the coast and between seas, and each sea having the
  wave size its layout gives, the icy seas roughest.
- **`WaterTests`:** floating, on `RocketPhysics` directly (the craft set on the water as `Drops.At`
  places one, without the flight's rules judging it): a craft wholly under pushed back up, a booster
  set on the sea floating and bobbing with the waves (and lying down tail low), carried about by
  the waves without drifting away, the legs never standing on the water, the sea not solid, and
  freezing afloat by distance: frozen far from the player, awake again as they come (and at the edge,
  neither frozen nor woken over and over; loaded frozen on a wave, waking as they come).
- **`SplashdownTests`:** the rules for a vessel touching the water, flown through the flight:
  a gentle splashdown ending `Floating` (also in the roughest sea, where settling is judged against
  bigger waves), a tall vessel tipping over in the water judged by how fast it hits it, the plunge
  growing with the speed of the splashdown, a hard one destroying it, a splashdown counting as
  landing safely but not on the landing zone (through the missions' steps, and "SPLASHDOWN OFF
  TARGET" where a landing site was wanted, a plain "SPLASHDOWN" where none was), a booster flown
  from the pad to a splashdown offshore (the soak's flight, `Offshore`), a fairing half lost in
  the sea listed as "sunk", a vessel standing in
  shallow water landed and not floating, the shallow seabed being ground (falling onto it, set
  down leaning, lying on it toppled), and one lying afloat in water just deeper than it draws still
  floating. Helpers: `Drops.At(seabed: true)` starts a vessel over the seabed, not the calm sea;
  `fairingOn:` flies the satellite launcher's upper stage as it flies, with its fairing; `first`,
  `last`, `mission` and `flight` choose which stages of which flight; and `LaidOnItsSide` sets a
  stowed-leg vessel down lying level.
- **`RaftTests`:** rafts measured over their own footprint (a disc's round, not square, and nothing under its bottom), and the drone ships the first rafts (every site's, site by site: Home's two, then Sentinel's), each the one a point over its deck is over.
- **`FloeTests`:** the floes: laid out only over the floe band, never overlapping, the same when worked out again (and how many in each sea); Boreal's waves tilting and heaving them; a booster landing on one (Landed, off target), riding it frozen and carried, and saved and loaded aboard; open water between them, where it splashes down; every floe found all round its rim (the one nearest its cell's edge too, leaning out over it, and a booster landing there); and the floes' bodies never more than `Scene.MaxFloeBodies` with twenty-four boosters over the band, none once they have gone. And their drawing: the floe mesh, posed as a floe is drawn, lying within its hull's cylinder and reaching its rim, top and bottom (what is drawn is what is collided with); the far tint (`Floes.Cover`) as much ice as the floes laid out are (and how many are drawn from over the band's middle); and a floe's sea, taken apart by wave (`Sea.Terms`), the sea all the same, and drawn where the physics would set it.
- **`IceWorld`:** not a test of the game but a writer, for the GameDriver: with `GAMEONE_WRITE_ICE_WORLD` set to a folder (otherwise skipped), it writes a saved world (through a `WorldStore`) holding one booster hovering 150 m over the Austral Sea's frozen sheet at its edge (the world starts at the Austral Sea's local noon, so the sheet is lit), for `tools/GameDriver/examples/gameone-ice.txt` to load from the world list: the icy seas are further from the pad than a script can fly. It checks the booster does hover there first.
- **`DroneShipTests`:** the ship's pose from the waves: level at rest, its heave, pitch and roll
  following the sea, holding its position, and the deck's height and velocity the ground queries give;
  Home's two drone ships (the second where the lander launcher's booster came down on the climb
  into the moon trip's plane, `LanderTripTests.BoosterCameDown`, within the booster's reach of it, 500 m), every ship in sea at
  least `Scene.DroneShipMinDepth` deep (itself at least the draught and the roughest troughs); the
  second riding the waves, landed on as the first is (on target with it as the site), lit by its own
  dark, with its masts and a marker ("Drone ship 2"); and the other missions keeping the first, and
  Sentinel its one ship.
- **`DeckLandingTests`:** landing on the moving deck through the flight: a booster landing and
  scoring on target, its touchdown judged against the deck where and when it falls fastest,
  landing off target on the deck, missing the ship (splashing down), touching the
  hull's side, one frozen on the deck and carried with it, woken, and saved and reloaded aboard.
- **`CollisionSearchTests`:** the map's predicted collisions between any two vessels
  (`CollisionSearch`, on its own thread): two on crossing orbits found to collide when and where they
  were made to (to the second and 50 m), two in one plane too, two at different bodies never paired (the same two crossing, one at a twin of the planet), orbits that never come near giving
  none, a dodge (3 m/s) taking the collision away on the next pass, one coming down onto a vessel
  on the ground (two on the ground never colliding), one coming down at 45° onto one landed on a
  moon crater's floor below its datum paired, from 20 s off and from 1 s off already below the
  datum (paths followed to `GroundBody.LowestRadius`), each vessel wrecked by its own tolerance (its
  later collisions dropped, or kept after a bump), a pass over 100 and over 1,000 satellites
  (`WorldTests.ManySatellites(250)`), and over 1,000 with a crowd on the ground and in the air
  over it, within `Scene.CollisionSearchSeconds` (printed; the thousands run alone: `Run=Alone`),
  the map (`Paths.Build`) drawing one between two vessels neither flown nor the target as a cross
  with both names and two lead-ins at `Scene.OtherPathAlpha` (the flown one's at 0.9 and the
  target's at 0.55), and no longer once one is wrecked; the map's frame in the thousand world
  (run alone, the world stepped on between frames and two passes taken: under
  `ShortPerf.PlayableFrameMs` at the 95th percentile, printed); no pass asked while the world
  stands still (paused), the next once it moves on; and the search's thread stopped with its flight.
  A lander standing on the moon held on the moon's ground as it turns: one at the moon coming down
  into it from 90 s off found at the lander's place then, within 5 m.
- **`TouchTests`:** Jolt's contacts read as touches, on `RocketPhysics` directly: a booster dropped on level ground touches it
  at its bell at its speed, once it stands no new touch; two crafts touch each other at their closing speed, and one a frozen craft at
  its own (woken as it comes, or too slow to wake it); the tower, a pad's mast, the drone ship's deck (at the speed against it) and
  its masts touched as what they are; with its legs out, its body touched at its body, not its feet; its
  feet and bell one place to touch down, its body another; a
  craft or tile taken out leaves no contact live; and a crowd (ten boosters stood touching in a
  ring) drops no contact event from Jolt's queue.
- **`TouchRuleTests`:** the rules judged from the collisions, through the flight: a touchdown just
  under the tolerance landing, and just over it crashing, on each solid thing (level ground, Home's
  pad, the drone ship's deck, a floe), at the speed against it; a touchdown that read just under
  the limit 12 cm up (where they once were read) still landing on its legs or its bell, and one
  just over still crashing; a skid judged by its whole speed;
  one touchdown sound for one touch (on its feet, its bell or its side); coming down beside a zone
  from where it is measured from afar judged as anywhere; sliding onto the next ground tile no new
  touch, but sliding on its side into a pad's side hard "Hit the ground" (and slowly nothing); the
  drone ship's mast wrecking on a gentle touch; a fairing half told by what it came down on; and a
  landed vessel woken where it stands, or lighting its engine there, not touched down again.
- **`HitTests`:** vessels touching each other, judged by their closing speed where they
  touched: a gentle knock pushing them apart, a hard hit (head on, from the side, foot first, on
  a floating vessel or the rocket on the pad) wrecking both, a frozen one hit just under and just
  over the tolerance; a hard burn, or a touchdown (gentle or a hard skid) beside another, no hit;
  a frozen vessel touched slowly woken by the touch; one at rest on others landed and frozen
  there (its hardest touch on them its touchdown), and woken as they move; one laid slowly on
  frozen ones waking them by its touch alone and freezing on them; a chain of them
  freezing together; stages and fairing halves parting without a hit; a wreck left in the world, undrawn,
  while its hit plays out; and touching another's foot touching it (Jolt's live contacts).
- **`FormulaCollisionTests`:** collisions between vessels under formula inside one top-rate
  frame (`FormulaCollisions`): two boosters meeting head on 150 s in (at 500, 2,000 and
  8,000 m/s, put on their Kepler ways back from the meeting) both wrecked "Hit another vessel"
  where they met, the warp going on; a pass just outside their reach, and one within their sizes
  but clear of their bodies, leaving both flying; a slow bump (3 m/s) wrecking neither and one at
  10 m/s wrecking both, judged as their touch begins; five pairs among the hundred satellites
  (their polar copies raised apart, so as not to meet over the pole) set to cross at random
  moments in the frame, exactly those wrecked and as many judged as a brute-force check of every
  pair's Kepler ways finds; and a top-rate frame among the hundred allocating nothing.
- **`FormulaRateTests`:** a frame at a formula's rate as one advance by formula, in windows
  (`Scene.FormulaWindowSeconds`): a low orbit coasted 60 frames at the top rate ending within a
  metre of where Kepler puts it, the clock 60 frames' worth on; nothing in the physics then (its
  world kept), 34 windows or more a frame; the new rocket on its pad and a lander on the moon each
  standing where they stood, and the launch (or throttle) dropping the warp to the physics' top
  rate as set moving, flown in the physics from that frame; the drop bringing the flown vessel
  and a small view's back into the physics where the formula had them; a slow frame (0.1 s, a
  millisecond's budget, the hundred satellites) stopped at a window's end; only the witnessed
  vessel's bodies asked ahead on a moon transfer (no moon physics made while the crossing is
  further than a frame and the physics' top rate's horizon); a world saved at the top rate coming
  back within a millimetre of where the formula had each vessel; starting again coming back at
  normal speed in the physics; a frame allocating nothing; an orbit dipping 500 m into the air
  (some 60 s of it) within one top-rate frame stepped from the moment it falls to its floor
  (`Formula.FloorRadius`, `ComesDownAt`), its apoapsis lowered by the drag as at normal speed within a
  metre, and one dipping 10 m into it for under 7 s, wholly between two of the frame's window ends,
  stepped through too (its apoapsis 1.3 m lower, as at normal speed); at the moon an orbit under its lowest ground coming down where it does at normal speed,
  for the same reason, within a metre; and a vessel past the solar system's edge in a top-rate frame
  lost there and logged, the warp kept; three boosters nobody watches at the top rate, into the deep sea, onto level ground at 30 m/s and onto the drone ship's deck, afloat, wrecked and landed aboard (carried by the deck) with nothing in the physics in any frame and the warp unchanged; and the flown vessel afloat, or aboard the drone ship, letting the top rate in (nothing holds it), still afloat or on the deck there.
- **`FormulaTests`:** a vessel out of the physics, moved by formula: one going straight up above the air rising to its top when its orbit says and falling back into the air, never standing still; a satellite on its
  orbit by Kepler (60 s; 600 s as a long test), coming back into the physics where the formula
  had it with no jump and flying on there on the same orbit (after 30 s out; 300 s as a long
  test), still drawn from its own state, and taking no body meanwhile. And the formula against
  Jolt, each flown twice (watched, in the physics; unwatched, a satellite 50 km off flown): a
  coast (a minute; a lap as a long test), a held burn in orbit, a fall and a burn through the
  air ending within 5, 20 and 50 m of each other, and the attitude after a coast and a held burn
  within a degree or two; the touch ending alike (on its legs, on its bell, too fast onto the
  ground, a splashdown, too fast into the sea, and down with its engine lit, landing once its tank
  is dry; coming down lying; lying down onto shallow water, afloat; and three plunges onto a seabed 3, 7.5 and 10 m down, by formula throughout: `Scene.FormulaSeabedSpeedShare`), as do touching down on more thrust than its weight (lifting off again, one touchdown sound), a booster at 1,000 m/s at a peak narrower than a step (wrecked by it within a step of where Jolt is), landings on the drone ship's deck (one with its engine lit) and on a floe (aboard by formula, carried there 600 s on at the top rate to within a centimetre of where the deck puts it, and one switched to back in the physics frozen aboard, kept on the deck), a fall onto Home's tower (wrecked by it, the physics' reason), onto a landing zone's light mast and onto one of the drone ship's, riding the waves (each "Hit a light mast.", within 5 m of where Jolt is), and onto its pad
  (landed on its deck, under formula throughout); a burn running out of fuel coasting on, a
  landed one not moving, no node flown nor stage dropped, and a step allocating nothing.
- **`UnwatchedTests`:** who is in the physics (`Unwatched`): satellites nobody watches leaving
  it; a satellite under formula switched to, flown in the physics from where the formula had it
  on that frame; one coming at a watched vessel at 500 m/s back in the physics over 1 s before
  it is within the handover distance; one at 1.2 times the reach neither leaving nor coming back
  (the hysteresis), and one the main view witnessed drawing off past its reach kept until past
  the hysteresis times it, then let go once; twenty due at once brought back four a flight step,
  and eight due one after another brought back the soonest needed first; choosing allocating
  nothing; a parting stage, a booster settling in the sea and a rocket on its pad, far from the witnessed, leaving the physics (the formula plays each out: settled afloat, waiting on its pad, and the stage back in the physics still parting from what it came off as it is switched to), and one afloat with an end over a floe loading under formula aboard it.
  Collisions between two under formula (`Unwatched.FindCollisions`), 5 km
  over the far side of the planet, nobody near: head on at 500, 2,000 and 8,000 m/s (and at
  8,000 at the physics' top rate) and crossing at 5,657 m/s, both wrecked "Hit another vessel" within 50 m
  of where they collide, under formula throughout and the game never slowed (and two leaving the
  physics just before they collide, the collision inside what the last check looked at without them);
  a pair passing 10 m apart, within their sizes but clear of each other's bodies, untouched; two
  boosters landing 15 m apart both standing, one hovering past one standing within their sizes
  leaving both whole, one falling onto one standing wrecking both, and one swept sideways
  across the other's end at 2,000 m/s wrecking both; a collision found, then remade by a burn
  before it came, judged as the new motion hits (kicked aside, a miss; slowed to 3 m/s, a bump; to
  20 m/s, both wrecked at that); two satellites
  bumping at 3 m/s left whole, at 10 m/s both wrecked; a pair passing just outside their sizes
  untouched; and the check allocating nothing. One left whole by a collision that came to nothing
  (the other kicked aside) looked for again from then, and wrecked by a third 0.02 s behind; one
  of a collision found brought into the physics bringing the other with it, both wrecked there; a
  world loaded at the same time starting its choice and its collisions afresh; and a world loading
  those far from the pad straight under formula, no body made, flying on where the physics would
  have them (within 0.5 m in 3 s). Who is chosen against the rule by brute force, step after step,
  in a 7 by 7 field of landed boosters 150 m apart (found through the grid), with three on cells'
  faces off its corners (190 m: kept; 250 m: kept by the hysteresis; 360 m: let go); and one the
  main view held, dropped from its few by rank, kept within the hold and let go past it.
- **`FastCollisionTests`:** fast collisions flown as the game flies them, each hit half a step
  after 3 s (the hardest moment for the physics' steps): two boosters head on or crossing, in the
  vacuum and the air, at up to 8,000 m/s closing head on and crossing (crossing, each at 5,657 m/s:
  5 km up, above the band where the game holds a craft to `Scene.GroundTopSpeed`), and one at up to 4,000 m/s
  into level ground, a frozen booster, a floe and a light mast, each hit and judged for what it
  hit, never passed through (three did, before the forced slow-down: see `SlowdownTests`). Its
  world builders (`Pair`, `Into`, `PastTwoMasts`) set such collisions up for the other fast collision tests.
- **`SlowdownTests`:** the forced slow-down (`Flight.Slowed`): a pass at 500 to 8,000 m/s closing slowed
  gradually (never quicker while closing, never slower once past), to its deepest
  (`Slowdown.Deepest`: `Scene.SlowdownPassSpeed` to the eye, or the least scale that keeps each
  step's travel within `Scene.FastCollisionStep` if that is deeper), held from
  `Scene.SlowdownHoldWithin` before the collision to as far past; only from `Scene.SlowdownFrom`
  out (`Slowdown.Lead`, with the sizes' touch, `Collision.Touch`); the rate keys doing nothing meanwhile and the player's rate
  given back; eased back over `Scene.SlowdownEaseSeconds` after a crash, or on switching away (still
  slowed until the other is flown, after its link-up, then easing back in the ease's own time); a pause changing nothing;
  its reason and the HUD's and readout's words (the time line slowed always saying so, with the
  player's slower slow motion, never the rate keys' hint); a small view's vessel slowing the game
  too, and two near the flown one in no small view (the main view's, near its camera), even past a
  frozen crowd nearer the camera (those on the move witnessed first), and a booster at 4,000 m/s
  out of the view diving onto one resting in it, asked only in turn, slowed for to its least
  scale before they collide; a slow near
  miss not hiding a fast hit just behind it (the step slowed to the hit's least); a powered
  landing never slowed (a playthrough); a fast craft waking a frozen one in time (`Scene.WakeSeconds`),
  and nothing it passes beside or leaves behind; a wreck laying no new tiles;
  dives through the air slowed and eased back; a fast low pass at rising ground hit (the ground's
  prediction is its size-sphere's); a craft on the drone ship's deck riding the step's changes; and
  looking ahead mid-slow-down allocating nothing.
- **`CollisionWarningTests`:** the warning of the flown vessel's soonest fast collision
  (`Flight.Warning`): given within a frame of its lead (in time to dodge), in the HUD's words and
  the readout's alike; a dodge after it clearing it, both thrusting out of the way at once and
  the whole lead used (the player's moment, a right angle's turn at its own rate, then the burn:
  the turn no longer than the lead's, the burn left no shorter than its dodge);
  given at once with no fuel (but by the next stage's push with only the firing one empty); dropping the warp once (warped again by hand, not dropped again
  for the same collision, nor after it is no fast collision for a few steps, but again once it has
  been none for longer than `Scene.WarnedGraceSeconds`); two masts in a row each a collision of
  its own, each dropping the warp, and solids told apart by `Scene.SameSolidRadius`, under half
  the closest two masts' or towers' spacing; a switch of vessel forgetting what was warned
  of; dropping the physics' top rate's warp at the step it is due, not a frame later; never for a vessel only watched
  in a small view; and looking ahead allocating nothing.
- **`NearMissTests`:** near misses predicted (`Collisions.NearestMiss`): a booster passing another
  within a length outline to outline at 1,000 m/s found, with its time, gap, pass speed, the
  seconds from `Scene.SlowdownFrom` out and its scale, and one just faster than
  `Scene.NearMissSlowAbove` found too; one just gone kept while its
  slowing eases back, then dropped, and the next pass a second later, easing in, winning over it
  (the one asking the least scale now); one inside each other's size-spheres that still misses
  found by its outline gap; none for a slow pass, one wider than a length, a touch (a
  collision's), nor the ground, the tower or a mast; the ease (`NearMiss.ScaleAt`) easing in
  evenly with the distance, holding at its deepest within `Scene.SlowdownHoldWithin` of the pass
  and easing back to 1 over `Scene.SlowdownEaseSeconds`; and asking
  allocating nothing. And the setting ("Slow motion for near misses", `FlightInput.NearMisses`):
  off, a near miss never slows the game; on, it is slowed (`Slowdown.NearMiss`) so its way in
  from `Scene.SlowdownFrom` takes the real time its shape gives, easing back over about
  `Scene.SlowdownEaseSeconds` with no jump; a slow pass or one wider than a length never slows; a
  small view's vessel's near miss slows it too; with a fast collision at once, never slower than
  either alone would be (the forced run's scale, frame by frame, and the near miss's own); shown
  ahead (`Flight.NearMissWarning`), dropping the warp once before its slowing and not again on
  warping by hand; a pass whose burn closes it into a collision by their sizes one slow-down, never falling
  twice; the setting turned off midway easing back by the near miss's own rate; the NEAR MISS
  and SLOWED lines and the readout's words; a near miss 18 s out predicted from the first step
  (the paths flown ahead); the rate keys doing nothing while a near miss slows the game; the
  warning naming the pass coming, never one gone and still easing back; and looking ahead for
  near misses mid-slow-down allocating nothing. `A_near_miss_to_watch` is the slowed near miss
  again, for watching (see above).
- **`ComingTests`:** what is coming at each controlled vessel (`Flight.ComingAt`): a fast collision
  coming at the flown one from its warning on, until it has hit; the ground coming at a small
  view's vessel (and the readout's "Coming at" line); a near miss coming only with the setting on;
  the flown one's and a small view's each their own at once; one wrecked by a third while coming
  no longer coming, from that step, and one removed to make room likewise; a small view's warning
  switched to, for the user to watch, before a crash (flashing red) and a near miss (amber, and
  read out as one); the small views' flash on and off at its rate
  (`Hud.FlashOn`, by real time); keeping what is coming allocating nothing; the compass tape's
  offset (`Hud.TapeOffset`: the shorter way round, held at its ends); and the main view's red mark
  on a vessel coming at the flown one, never on the ground, the sea, a floe, the drone ship, the
  tower or a mast (`Markers.ComingVessel`); and what a click does to the target, on a marker or the
  red mark (its vessel targeted, or let go if it already is) or off them all (`Markers.Click`); and
  what a press is on, first of these: the red mark, a marker, a body's path in the map or its disc
  (a body whose ring has faded, or with markers off), else nothing (`Markers.Pressed`).
- **`CollisionTests`:** each witnessed vessel's soonest fast collision predicted ahead (`Collisions`):
  two boosters head on (at 500 and 8,000 m/s closing) seen coming with the right time and
  speeds, a collision 19 s out (head on and 15 m aside) seen from the first step, a pass timed to its closest approach, a collision just over the threshold found
  and one under it not, a dive into the ground (the time its orbit says, counted down between
  the ground's checks, and asked again on real time when slowed), dives onto a floe and the
  drone ship (hit as the raft), and a craft at a mast and at the tower; none for a pass wide of another, a slow collision, a landing burn,
  or a launch's lift-off beside the tower and its stages parting (asked of each part); the warning's lead
  (`WarningSeconds`, infinite without fuel), the names of what is coming, a pair whose
  march goes to NaN ending at once beyond the horizon, and that asking allocates nothing.
- **`ShaderTests`:** every shader compiles (headless, with ShaderC, so a GLSL error fails the suite
  and not only the game's start-up), and the Frame uniform block in the GLSL (std140) matches
  `FrameUniforms` member by member: names, offsets and total size, the waves' phases included; and
  the ground's colour map is bound at 10 and the ground's colour read from it per pixel, never from
  the vertices' colour (the waterline's map at 11: `WaterlineTests`); and the sky writes its depth on
  every path (`gl_FragDepth`: the far plane's, 0 as the depth is reversed, but where the moon covers the
  pixel its hit's at least a metre under its lowest ground, else where the sun's sphere does its,
  placed from the camera as the meshes are), its disc drawn by `sunDisc` and `depth_greater`
  declared; and the moon's ground mesh lit by the view's lights (a vessel's landing lights) and
  the engine flame, by its normal in the scene's frame, as the planet's ground is; and, zoomed
  out over Home at the map's first zoom and further, every patch corner well inside the drawn
  water where the ground stands above the sea pushed back by the seabed's push (taken off its
  reversed depth) further than it stands out along the view.
- **`GroundColorsTests`:** the ground's colour map (`GroundColors`): the GLSL's kind colours are
  `Scene.KindColors`, its base level each texel's kind's colour, and each level above the average
  in linear light (not display values) of the 2^k by 2^k block of the base under it, on the
  planet's map at a few spots and every texel of a small made-up one; a map whose resolution isn't
  a power of two is refused.
- **`SeaColorsTests`:** the seas' colour map (`SeaColors`): a map with water of a sea it names no
  colours for refused (by the map itself); on a made-up map of two seas each cell's colours along
  a row across the border are the two seas' mixed by their weights (`exp(-d /
  Scene.SeaColorBlend)`, d from the border), each sea's own far from it and land taking the
  water's round it; on the planet's map no channel changes by over 0.0125 between neighbouring
  water cells (61 m, about 0.003 a map texel; the steepest pair's blend is 0.011), the Grey and
  Coral seas' border at 20°N 144.8°E is gradual (where their own deep blues differ by 0.25), and
  each sea's colours at its layout point are its own; the water reads them at binding 12 by the
  derivatives taken before its discard.
- **`WaterlineTests`:** the waterline far off (`Waterline`): each level the mean of the map's
  heights clamped to the sheets' freeboard over the block under it (every texel of a small map,
  spots on the planet's); near ground (squares two texels apart or closer) reads no level, so its
  water is drawn by its own heights, to the bit; the GLSL's level and numbers are the C#'s and the
  seabed's push and the water's discard read the same waterline; and on the planet's map, along
  the lines of a far patch's squares (245 m and 122 m) across both ice sheets, the drawn waterline
  crosses on average within about a tenth of a square of where the map's heights cross the sea
  level (90% within a quarter, all within half), and half a level finer and coarser (as the
  shader's spacing varies within a patch) a little looser (90% within 0.3, all within 0.6), where
  the corners' own heights put it up to most of a square off in steps; tongues and bights under a
  square and a half across are not measured.
- **`SteeringTests`:** the steering every mission shares: leaning, holding a direction (as the
  direction rings do), the air turning the nose along the direction of travel once let go (a
  gravity turn), the nose holding still above the atmosphere, turning slowly with the engine off;
  plus dropping the booster early, and picking a mission mid-flight; the rings' hold (`HoldMode.Rings`):
  one click on the heading ring turning to that heading and holding it, the pitch left as it
  was; both rings clicked turning on both together; a pitch clicked while the nose is still
  turning to a held heading keeping that heading; the keys and a ring's drag ending it; the same
  round the moon, where the rings are drawn and read by the moon's compass as the hold steers
  by it (`RingAngles`: the nose settles on the ring's point); and a ring press let go without
  moving (under `Scene.RightClickSlopPixels` all told, however many frames it is held) a click,
  moved further a drag (`NavigationControl.RingPress`), and what each press does as `Update` wires
  it (`NavigationControl.Judge`: moves, holds, clicks, lets go).
- **`VesselTests`:** several vessels in one flight: staging makes the dropped stage a vessel, one
  without guidance is debris that can't be flown, switching hands the controls over while the other
  keeps its last inputs, losing the flown vessel doesn't end the flight while another flies, and
  a rewind leaves just the rocket; and a rocket tumbling end over end over the air comes
  apart each part going on as it moved with the whole (its centre of mass's own velocity, the
  turning's share included, within 0.05 m/s), and the push apart, the whole's momentum kept (the parts' velocities less their pushes, weighed by their shares of its mass); and its fairing halves likewise (their mean against the rocket the turning's share where they are). They fly the orbit mission (its booster carries guidance), and a test rocket whose booster has none.
- **`WorldTests`:** the world carrying on from flight to flight: choosing a mission leaves the last
  flight's vessels where they are (and flying); a mission counts only its own flight; a rewind undoes the
  flight; wrecks go (one wrecked on the pad at once, body and all, not left inside the new rocket);
  anything left on the launch pad is cleared for the new rocket (a rocket that
  never launched is just replaced); Tab goes round the flown vessel's flight, and another flight's
  vessel is flown through its view; a captured world comes back exactly and carries on the same; the
  satellites preset's satellites hold their orbits for a lap of the highest (each its height all the way round, and
  the size of each orbit, among the stars, held within 0.01 m a lap, tilted or not, as the ground turns under it); a world of a hundred vessels can still
  put another in orbit.
- **`PrecisionTests`:** each craft's place and velocity kept in double by the physics
  (`RocketPhysics.TrueCentreOfMass`, `TrueVelocity`): a satellite flying free for a quarter of a lap,
  Jolt's centre of mass and velocity never more than two float steps from the copy's; a two-stage rocket
  staged in orbit, each part's copy Jolt's from the step after and going on with no jump; a craft
  crossing into the moon's sphere in the physics, its copy changed across in double onto the orbit round
  the moon worked out again in double (exact to a micrometre), and keeping to that path; and the
  autopilot's landing on the landing zone, its touchdown within 0.01 m/s of Jolt's floats alone; a
  craft handed to the formula and back (on its orbit by Kepler, and stepped low over the moon), the
  formula going on from the copy and the copy coming back where the formula had it, to a micrometre;
  and the lander stepped by the formula low over the moon, burning then coasting two laps, each lap's
  mean size within a millimetre of the one before's (read where it is rather than where its drift
  takes it, the pull made it gain some 34 m a lap).
- **`MissionRunTests`:** runs (`MissionRun`): two in progress at once, each ticking its own steps
  whichever vessel is flown; the mission panel's run (`Flight.Run`) following the flown vessel;
  a new mission at Home leaving another site's waiting rocket, and its run, in place; the
  parts staged off a rocket belonging to its run; a world restored keeping its runs and the
  vessel flown, and one left before a choice saved as it was; and carrying on
  (`Flight.CarryOnChoices`, `FlightInput.CarryOn`): a run whose vessel flown last is gone by
  another of its own, a free-play vessel in no run, and debris and wrecks never offered; and how
  a run ends: completed once its steps are done with no bonus open, or by "Complete mission"
  (`FlightInput.CompleteRun`, the bonus left open lost), failed with its lost step named, left
  with no vessels (failed), abandoned (`FlightInput.AbandonRun`, its vessels left in no run) or
  by its waiting rocket cleared from its pad (abandoned, logged once); an ended run's result
  shown until a switch, a notice for one ended unflown (`Flight.Notice`), and carrying on every
  kind of choice flying the choice's own run; and trying again: starting the mission again
  (`FlightInput.StartAgain`) leaving the old vessels and the other runs untouched (the old run
  abandoned, logged as started again), and after a failure, for a vessel of no run, starting its
  own mission at its site; a rewind (`FlightInput.Rewind`) bringing back the whole world (its
  clock, every vessel where it was, the log) as it was at the menu choice, `RewindSeconds` naming
  how far, going back to the choice (a carry-on) and not a switch made since, and nothing to
  rewind to after a restore before a choice; a rewind to a mission begun coming back as it was
  chosen, every time (its clock, its fresh rocket, the log). And what a restore and a carry-on
  start from: a saved flown vessel that is debris or missing falling back to the newest that can
  be flown (and no choice marked "(last flown)" for it, `CarryOnRow`); a run's points kept; the
  placeholder's body gone with it; the choices' order (runs oldest first, then the vessel flown,
  then flights newest first; a failed run, or one with nothing to fly, not offered); carrying on
  the vessel flown letting its ended run's result go; a world loaded over its capacity losing
  debris, then vessels of no run, then runs', never the one flown; and the readout's runs, rewind,
  vessel id and position.
- **`NodeEditorTests`:** the node editor's handle rate: out as it adds, in a quarter of that
  taking away, past zero; and a chain changed by the flight rather than the editor told apart
  (`NodeEditor.ChangedElsewhere`: a node gone as it matched), by which a grab is let go. Its
  handles shown (giving the steering rings way) only with the node and one of them on the screen
  (`NodeEditor.ShowsHandles`).
- **`DeltaVTests`:** the delta-v left (`Vessel.DeltaV`, `DeltaVInAll`): a burn in vacuum giving
  what it uses within 1% (against the same flight coasting), none with no fuel, and a stack's
  stages burnt in turn as worked out by hand from the stage table, a payload's fairing carried
  until the payload is let go.
- **`NodeTests`:** manoeuvre nodes as a chain (`ManoeuvreChain`): a prograde node raising the far
  side, one flown by its guide turning matched and going as the engine is cut (in warp too),
  giving the orbit it planned, the guide counting down and saying when it goes past; a node past
  its time staying until matched, and cut before it is matched staying; the match tolerance (3% of
  the burn, held between 1 and 5 m/s), held while lit until half as much again is left, and a cut while
  held letting it go; drag in the air slowing a coasting vessel into the tolerance letting it go; an early
  burn cut matched letting it go before its time, and a fresh unburned one never matched before
  it; a late burn counted against the planned path at its point nearest the vessel (a lap and
  more late at any phase, a large burn flown well late, checked against a brute-force nearest
  point), flown to match it, and allocating nothing once warm; one matched only after the next
  one's time leaving the next late on the present path; a node staying where it was set (to a millimetre) when the vessel burns
  before it; changing a node redoing the chain after it, each later one keeping its gap and
  amounts; moving one moving the later ones with it, never before the one before it (nor the first
  before now); deleting a middle one joining its neighbours, and the first anchoring the second on
  the vessel's path; moving the first snapping it onto the orbit the vessel is on now; inserting
  one before the first or between two; a chain round the moon planned round it; the next node, as
  the first goes, snapping onto the orbit the vessel is on then, at its time with its amounts, the
  chain after it redone; an empty chain clearing the plan; one anchored past
  where the path ends (falling, a hop, escaping) anchored where it ends, and saving; a node moved
  only once the pointer leaves where it was grabbed; each vessel keeping its own; and the readout
  giving each node's world time (`Node times`), none with no nodes, written once a chain
  (`Readout.NodeTimes`); the manoeuvre hold let go by a ring's drag, steering in warp as at normal
  speed, and steering nothing under formula; the hold buttons' and frame buttons' readout names and
  the `Hold` reading; the view's burn mark (`NavigationControl.BurnMark`: along the burn left, green
  once matched); a landed or afloat vessel's node judged only as a touch begins or ends
  (`Vessel.Rejudged`); and anchoring a chain allocating only the chain.
- **`WorldLogTests`:** the world's log: a crash on the ground, two vessels colliding (one entry; and two wrecked by one collision steps apart joined into the first's entry; two pairs colliding side by side an entry each, by who hit whom), an unwatched loss under formula (not witnessed, near its site), those removed to make room as a world loads (oldest first, unseen, no speed, printed without one), one cleared from the launch pad (unseen as a world loads; seen, flown, as a flight from its site starts), and the newest 300 kept; the missions' entries (a run begun, a step done with its points, completed with its score, and nothing for free play); and each crash reason worded without its advice (or the break-up's cause after its colon), its speed taken out to be the entry's (a line never says two speeds).
- **`WorldStoreTests`:** saving and loading, in a folder of the test's own: a world loads back
  exactly (every field of every vessel, a body targeted from the planet and from the moon
  included, each still its vessel's target once loaded) and flies on the same as the one saved; a node chain
  anchored round the moon loading back as it was, and one anchored at an unknown body or saved with
  no nodes refused (listed as unreadable); the world list
  (the latest first, what is in each, a file it can't read listed with why); a file from a newer
  version refused; a world flown in free play saved and loaded; worlds made from presets with names of their own, renamed, reset and deleted; a vessel afloat saving and coming back floating (and one saved while still settling in the water, coming back and floating with nobody near); vessels saved while out of the physics, under formula, saved where the formula had them and loading back there; a place and velocity with more digits than a float holds coming back from the file exactly, satellites saved in orbit (one flown in the physics, the rest under formula) loading back where they were in double and brought into the physics with that as their copy, and a vessel loaded flying into the physics with its saved place as its copy.
- **`ControlsListTests`:** every line of the HUD's controls list (`Hud.ControlLine`) short enough to
  fit a small window.
- **`CreditsTests`:** the BRUTAL credits panel (licence §2.1 and §2.2): shown before play and in
  every menu, and in flight for `Scene.CreditsSeconds` once play first begins, then gone; and no
  menu screen over it at 800 to 1920 px across (`Hud.MenuAcross`: each clear of it, on the screen,
  and at 1280 centred at its full width); and, laid out by ImGui itself (a context of the test's
  own, with the HUD's font and style, no window), the game menu and the mission menu's world line
  with a 63-byte world name at 800x600 still clear of it, the name wrapped.
- **`MenuLayoutTests`:** the menu screens' lists (`Hud.ListRegion`, `Hud.MenuMiddle`): from one
  saved world to two hundred, at 600, 720 and 1080 px high, a list that doesn't fit scrolls in a
  region that keeps its whole window on the screen, and one that fits is as tall as it is; a short
  menu keeps its place a little above the middle; and, laid out by ImGui, the carry-on screen,
  the try-again window and the game menu (its "Abandon mission" asking to be confirmed) on the
  screen and clear of the credits.
- **`ViewsTests`:** the small views, kept in step with the flight as `GameView` does (no window
  needed): each part that comes away and can be flown gets a view; clicking one (or Tab) flies it
  and the two vessels swap places; a closed view's vessel carries on and can be given a view again;
  debris gets a view only when asked and can't be swapped in; starting again closes them all, and
  a world restored or something carried on (or rewound to) opens none and swaps nothing in; a
  swap (or Tab to a vessel with no view) hands each view's held shadow place on with its vessel;
  the strip's place (`Hud.StripPlace`): its buttons below the instruments' panel however tall it
  is, the views under them made smaller to fit; under a panel reaching nearly to the bottom, the
  views at their least and the buttons just over them, never over the views; and each vessel's own
  view (`Vessel.View`): Tab, a swap by a small view, a carry-on and a rewind each bringing back the
  vessel's view after its link-up, the one left keeping its own, one never flown getting the
  default; and a world picked and carried on, each frame's input carrying the camera's view as it
  stands, still bringing back the saved view (the camera's taken as the vessel's only once it is);
  a new rocket getting the default view whatever the camera was on, and a world restored putting
  the camera back on the flown vessel; and the frame a vessel is flown in after its link-up taking
  nothing of that frame's input made for the vessel left (its view, plan, hold, ring angle, target).
- **`PadLightTests`:** where each pad's, zone's and drone ship's lamps and masts stand (the masts
  only where the ground allows, clear of the towers, in a zone's own frame, aimed at the place's
  top, solid); which place's floods cast shadows, held for a few metres; the nearest lights and
  each view's set chosen allocating nothing, views at one place sharing a set and views apart each
  their own; a place's lights scaling by its lit fraction; a place that is off handing in no lights
  and shadowing nothing; the shadows moving on to the next lit place as the shadowing one goes off;
  a view's nearest four lit vessels' lights following the pad lights (capped to leave them room) by
  how far on each is, within reach, none at 0, casting no shadow; a view near no lit place getting
  a set of its own, views apart never sharing one; choosing with vessels allocating nothing.
- **`PlaceLightsTests`:** the pad lights' fractions (`PlaceLights`, `Sun.IsDark`): at Home off at
  noon and on at midnight; fading on at dusk and off at dawn over `Scene.PadLightFadeSeconds` of
  real time, never at once, and the same under the warp; between the dusk and dawn heights keeping
  their state; each place switching at its own dark; stepping allocating nothing; a world loaded at
  night starting lit.
- **`LandingLightsTests`:** each vessel's landing lights (`Vessel.LandingLights`): a booster
  coming down at midnight off above `Scene.LandingLightsBelow` and fading on below it, never faster
  than the fade; off by day; off high up, and kept on in the gap up to `Scene.LandingLightsOffAbove`;
  a booster landed in the dark on until dawn, then fading off; none for a fairing half or a wreck;
  a landed vessel under formula switching on at its own dusk under the warp; a world loaded at night
  and a stage parting near the ground at night starting lit; stepping every vessel allocating
  nothing; the readout's `Landing lights`.
- **`SunAreaTests`:** each view's sun shadows' area: the main view's round its camera; views
  together sharing one, far apart each their own (and holding a shared one within the
  hysteresis); a view of a wreck never its own; which view's uniforms draw each area, and an
  area no drawn view chose skipped; choosing them allocating nothing. At Home's noon, with the sun
  straight overhead, the sun's view is still a proper one and takes in the pad.
- **`SunTests`:** the sun from the world's clock: overhead at Home's noon, under it at midnight,
  rising in the east at 05:00 and setting in the west at 15:00; a new world at 07:30 at Home; a day
  of 20 minutes; local time by longitude (and at Polar the sun low all day, but up at noon); the
  world time for a local time round-tripping at every site; once round the stars a year,
  eastward; the time warp moving the sun as much as the clock; the clock surviving a save; the
  clock's text.
- **`BodiesTests`:** the bodies as a list and the frames between them (`Bodies`, `FrameChange`):
  the moon's sphere of influence from the masses (6.3 to 6.5 km), the planet's infinite; places
  up to 8 km from the moon's centre changed to the planet's frame and back within a millimetre, at
  time 0 and 200 years in, rotations within 1e-6, and a body to itself no change; a point fixed on
  the moon carried over the planet's ground at the change's velocity (within 1 mm/s of its motion)
  and the moon's spin its orientation's turn; velocities up to 500 m/s there and back within
  1 mm/s; a place entering the moon's sphere at its radius and leaving it only past 1.1 times it;
  the game's list read-only (no array behind it to cast to) and made once; and the choice going by the list handed it (a second moon found by its own sphere, from the
  planet and from one moon to the other through the planet); and, with two moons at several
  times, every place nearer a body's centre than its clear distance (`ClearWithin`) chosen as at
  that body, the bound within 2 m of the nearest edge, and choosing with the places worked out
  ahead (`PlacesIn`) the same as choosing alone, inside the bound and 10 m past it.
- **`PatchedPathTests`:** the path patched at the spheres of influence (`Patched`): a path straight out of the moon's sphere topping 1 to 5% past its leaving radius, patched into the planet's leg where it goes up through it; a transfer from
  4 km up (the test's Hohmann burn, aimed to pass the moon about 1 km over its ground) cut within
  1 m of the moon's sphere, the moon leg's start changed back to the planet's frame within 1 cm
  and 1 mm/s of the planet leg's end; a path escaping the moon cut within 1 m of 1.1 times its
  sphere, the two legs meeting as closely; a path under the sphere for less than a sample (about
  1.4 s on the planet's conic) still patched planet, moon, planet; the moon leg's periapsis
  within 20 m of a Verlet march at 0.01 s of the same pulls (each body's alone, the moon's
  in its frame as it falls round the planet); a node 30 s after the crossing burned prograde
  round the moon (within 0.01 m/s), and one burned radial, normal and a little prograde the same
  (its radial out from the moon's centre and its normal square to the orbit round it; measured
  round the planet it would be 3.7 m/s off); a node placed past the crossing, anchored round the moon, planned
  and guided as the same node anchored at the planet; the burn guide of a vessel on that transfer, its node
  on the moon's leg, the plan's burn round the moon changed into the planet's frame (within
  0.01 m/s; on the planet's orbit alone, as though it never crossed, metres a second off), and
  allocating nothing asked every frame; the plan with nodes on the moon's leg, worked out again
  each frame, allocating nothing; patching allocating nothing once warm; and the head-on pairs'
  hyperbolas (`FormulaCollisionTests.Meeting`, put back 600 s and 10,000 s: some 500 to 11,000 km
  out, coming in past the moon's distance and out again for good) followed to 20 s, 3,600 s and
  the solar system's edge in under 50 ms a call (about 0.3 ms: the way out past the moon's
  distance isn't sampled for its sphere; sampled every 2 s to the edge, it took some 32 s).
- **`SolarEdgeTests`:** the solar system's edge (`Scene.SolarSystemEdge`, round the sun): a path
  escaping the planet at 1,500 m/s from 20 km up, sideways and straight up, and a bound ellipse
  whose apoapsis is 2e10 m out, each ending at the edge (`PathLeg.LeavesSystem`), its end within
  100 km of the edge's radius from the sun (closer than the sun is to the planet, so the edge is
  the sun's); bound orbits whose farthest from the sun is 1,000 km and 1 km past the edge (out
  there for a moment a year as the sun comes round opposite, tuned by a small outward speed)
  ending there too, and one 1 km short of it not; a 10 km orbit with no such end; the planet's
  centre the sun's distance from the sun through the year (`SolarSystem.FromSun`); the sun never
  further from a place than `SolarSystem.FarthestSunFrom`, and within tens of metres of it once in
  a year; and the flown vessel's escape path drawn out to the
  edge with one mark, "Leaves the solar system" (`Paths.EdgeMarks`), at the line's end, in no more
  than a lap's samples a leg, built again allocating nothing. A vessel past the edge is taken out
  and logged "left the solar system" (`WorldLog.LeftTheSolarSystem`, no speed): one under formula
  1 km inside going out at 2,000 m/s (unseen), one 50 km inside at the highest warp, and one in a
  world loaded past it, on the first step; the flown one, flying out across it at 2,000 m/s in the
  physics (its place the physics' double copy: Jolt's floats, a kilometre a step out there, can't move
  it by a step's travel), lost as a crash ("Left the solar system.", still the one flown, seen, its run failed and
  in no capture), written once, and gone from the world once Tab flies its run's booster; and
  `Flight.EdgeAsked` counting only the vessel 1,000 km inside, never the one in orbit or on the
  pad, at warp too.
- **`WarpDropTests`:** the warp's drops from a formula's rate to the physics' top rate
  (`Flight.LastDrop`, a `WarpDrop`): `Scene.WarpDropLeadSeconds` ahead of an orbit coming down
  into the planet's air (`Air`) and into reach of the moon's ground (`Ground`), still above it
  and in the physics; never climbing back as it dips into the air and out again; a launch,
  staging, the legs, the bay doors and the throttle at rest each dropping it (`SetMoving`) with
  the action taken in the physics; a steering key (`Steering`) turning the aim as far as at the
  physics' top rate; a node 2,000 s ahead dropping it (`Node`) its lead and half its burn before
  its time, once, and again once deleted and made again the same; Faster (or a warp to a point)
  from the physics' top rate in a frame with a launch or a steering key held there, the input
  taken in the physics; a switch and a small view at the top rate to a booster in the air dropping it by
  its name, flown or seen in the physics; and the readout's `Warp dropped` saying why for
  `Scene.WarpDropShownSeconds`.
- **`WarpWarningTests`:** a collision or near miss coming at a witnessed vessel at a formula's
  rate (`Collisions.AheadOfTheWarp`), two levels: two boosters head on, 600 s of travel apart
  closing at 2,000 m/s, dropping the top rate to the physics' top rate (`Collision`) between
  `Scene.CollisionHorizonSeconds` and that plus `Scene.WarpDropLeadSeconds` ahead of their
  meeting, then the physics' warning dropping it to normal speed and the slow-down reaching its
  least scale as both are wrecked; the same pass a little wider than their sizes dropping it
  (`NearMiss`) only with the setting on, the warning there naming the other; the warp raised
  again after the drop not dropped again by the same vessel, the collision then the formula's;
  an other dropped for as it came at one witnessed vessel still found coming at another; two
  satellites drifting side by side within their sizes, their gap wavering, dropping it once in
  eight raises; and two satellites 300 m apart on one orbit never dropping it in 120 frames at
  the top rate.
- **`WarpToTests`:** warping to a point on the flown vessel's path (`FlightInput.WarpTo`,
  `Flight.WarpingTo`, a right click on the path in the map): 40 s on round a low orbit at the top
  rate, there in one frame and at normal speed on the time asked (its last window cut there), the
  vessel within metres of where the path had it; a rate key (either), a switch to another vessel,
  the ground's collision warning, a near miss coming (the setting on: at the top rate ended at
  the physics' top rate ahead of it, then its warning there dropping it to normal speed), a landing (a booster
  settling on the moon before its path's end) and the vessel wrecked (driven nose down into the
  ground, under a fast collision's speed, so unwarned) each ending it early at normal speed; a
  world restored mid-warp, its clock past the warp's time or before it, dropping it with the clock
  never stepped back; under a slow-down, no faster than it and still stopped on the time; a time
  past the path's end held to it, where it comes down and at the solar system's edge, and while
  the engine burns to where the path as each frame begins comes down; none for a time behind or
  a vessel on its pad; the hit on the drawn line (`NodeEditor.NearestPiece`: the nearest point
  within `Scene.PathPixels`, nothing beyond, nothing on a piece to or from a point not drawn),
  on a plan only up to the first node (`NodeEditor.FlownWithoutABurn`, `WarpTime`), a right press
  that deleted a node warping nowhere (`NodeEditor.RightPress`), and a marker or a vessel under
  the pointer winning over it (`Hud.RightClickWarp`).
- **`TopSpeedTests`:** no speed cap in open space, one low over the ground: the upper stage
  1,000 km from the planet's axis, escaping at about 5,800 m/s over the ground, flown in the physics
  for 20 s on the path drawn at its start (`Patched.Legs`), within half as much again as the
  physics' own step lead there (measured 2.9 m; with Jolt capping it at 4,000 m/s it would leave
  its path by over a kilometre);
  boosters at 6,000 m/s 1 km over the ground held to `Scene.GroundTopSpeed`, checked every frame
  for half a second, the flown one in the physics and one on the far side of the planet under
  formula; the upper stage under formula 500 m over the moon's highest ground at 6,000 m/s, loaded
  stepped rather than on its orbit (only the hold keeps it off it there, with no air), and held from
  its first formula step until it rises out of the band; and the upper stage burning 20 km up past
  4,000 and 16,000 m/s over the ground.
- **`MoonTargetTests`:** a body as a target and the path drawn round each body: the moon targeted
  as a vessel is (its centre in the view's frame within 1 m, flying, its velocity over the
  planet's ground as its centre moves; the planet, the body it is at, refused; the readout's
  `Target` its name); in Target the speed relative to its centre (as the test reckons it among the
  planet's stars) and the prograde hold turning the nose to it; a lander on the moon targeted
  from the planet located and measured across the bodies; from a low orbit in the moon's plane its
  window a stretch of 3° or more, the game's path working out the same points as bright as a miss
  within the sphere makes them, and its best burn's path patched into the moon's sphere; on
  course (the 1.1 km pass), the line one stretch a leg (`Paths.LineStarts`), every point of the
  moon leg its orbit's own round the moon as it is now (within 5 cm, as far from its centre as on
  the orbit), its ends far from the planet legs' either side (the moon moves on meanwhile), the
  planet leg after it unchanged, and the leg in the moon's sphere round it; flown at the moon on
  a way out of its sphere, the leg round the planet round the planet as it is now (within 5 cm);
  the closest approach the moon leg's periapsis (within 1 m, drawn on its Pe
  mark, over its datum) and, with no encounter, `Encounters.Closest`'s against the moon's orbit;
  an orbit round the moon a closed loop, flown there and seen from the planet as a target's path;
  every body's marker gathered by its name where it is, the moon's targeting it from the planet
  and the planet's from the moon; crossing into the targeted moon's sphere letting it go (the
  frame back to Auto, the hold on by Auto's, no window, closest approach or target's path left);
  and `Paths.Build` allocating nothing with a node and the moon, or a vessel round the moon,
  targeted; a vessel target's closest approach found on the leg round its body that comes
  closest (after the pass, not the first), each leg only over its own span, and none with no leg
  round its body; a body's marker hidden behind another body (faint as the target), never by its
  own; and the window looked for only as far as the orbit is flown to a crossing. The pass joins
  the planet's legs either side within a centimetre.
- **`MoonFrameTests`:** the frames, the HUD's numbers and the readout by the body the flown
  vessel is at: on the moon Auto orbital once the vessel is above its highest ground (822 m, over
  its share of the radius, 750 m) by the margin, surface under it, each frame kept between (the
  planet's hysteresis), and a vessel 400 m over it orbital, 50 m over it the surface's; Surface over the moon's ground and Orbit round
  its centre among its stars, in orbit and landed; a lander's readings the moon's (`Body`, its
  altitude over the moon's datum and the ground under it, tilt 0, still, no air, falling back,
  its local time by the moon's sun, the compass (`Hud.CompassFor`) facing its north 0° and its
  east 90°, the nearest pad's lights found across the bodies, the planet's distance and height
  in its sky); a moon orbit's readings round the moon (apoapsis and periapsis within 3 m of its
  height, its period within 2 s of a lap by the moon's pull, its orbit speed and the speed
  needed, level over its ground, the compass's travel mark east); one topping 0.9 times the
  radius the moon's pull is left at, its apoapsis and lap shown, and one topping 1.2 times it,
  "leaves its pull" and no lap, and as one that reached orbit at the planet first, not in a stable
  orbit there and no "in orbit" in its small view's line (`Hud.Status`), and one that never
  reached orbit, flown with its engine off, earning it on the lap and never on the way out;
  Launch and Land's booster set down on the moon where its frame has the landing zone's
  coordinates (0.1 m off them round the moon's ground) never landed on target, the zone 40.7 km
  off across the bodies, no points; and on the planet, every reading the planet's
  (Auto by the air's top, the pad's readings, the moon's distance and height by its centre and Home's up, a planet orbit's
  period).
- **`SolarSystemTests`:** the bodies on their paths: the sun's direction from them the clock's as
  it always was (to 1e-4 rad, the first day to 200 years in); the planet's lap round the sun the
  year; the moon on its circle, its lap from the planet's pull, its tilt, its way round and its
  orbit's plane fixed among the stars; its spin among the stars its lap rate to 1e-13 (about
  the axis its orientation turns about, exactly unit in double); its spin axis tilted from its orbit's normal and fixed
  among the stars, so its face nods at the planet by the tilt each lap and faces it on average
  (lat 0 lon 0 of its own axes), the planet wholly rising and setting once a lap from its north
  pole and never setting from the middle of its near side; its own axes there and back within a
  millimetre, 200 years in too; eclipses only in their two seasons a year, and some in each; the
  moon's clock by its own sun, as seen from the moon rather than the planet's centre (noon under
  it, midnight opposite, a fixed point's clock again a moon's day on, but for the tilt's swing);
  the sun's disc its radius over its distance; the moon's pull; the moon up at Home through a new
  world's first evening; the planet's shadow on the moon (`PlanetShadowLight`: the full sun out of
  it, deep in it dim and red, the grazing light through the air's colour, and its edge smooth) and
  the light off the planet (`Planetshine`: most at a new moon, none at a full); the moon's light
  on the planet (`Moonlight`: a full moon's more than a crescent's, a new moon's none, none with
  it set, under 2% of the noon sun's, all but gone in a total lunar eclipse); none of it
  allocating; the readout's `Moon` (the distance to its surface) and `Moon height` (above the
  horizon, negative below), and the frame's fill of the sun and the moon (`GameView.FillSky`)
  allocating nothing.
- **`EclipseTests`:** the sun eclipsed by the moon (`SolarSystem.SunSeen`) and by any body (`Sun.Seen`): Home's pad in
  totality by day in the first year, in its season, not in the world's first minutes and for at
  least 10 s (found by search; the moon's orbit's start and line are set for it); a total eclipse by
  day somewhere on the planet in the first year, only in its season; there the sun is wholly
  covered and `Sun.IsDark` is dark with the sun high, and a lit place the shadow crosses (a pad,
  zone or drone ship) has its lights come fully on and go off after (`PlaceLights`, stepped a
  frame at a time); out of season, 500 places and times with the whole sun seen and the dark as
  by the sun's height alone; across the shadow's edge, 100 steps with the light rising and never
  by more than 0.05 a step, and the lights switching once each way and held between the two
  thresholds (the hysteresis); the once-a-time reject (`NoShadowWithinTheAir`) firing only where
  the full answer is the whole sun, always well out of season, the answer through it the full
  one's to the bit; the sun seen past every body (`Sun.Seen`) on the planet the moon's eclipse to
  1e-4 (in totality, round the shadow and far off, partial shares among them); from the moon, a
  place behind the planet from the sun given the light the planet's shadow is drawn with
  (`PlanetShadowLight`'s brightness, under the dark's share) and dark, one on the sun's side
  seeing it whole; a lander under the sun on the moon through the planet's shadow and out, dark
  exactly when its ground's drawn light is under the dark's share (held to the light's), the
  air's edge included; `Sun.Seen`, passing over bodies clear of the sun's way and stopping at
  none seen, giving the full sum's answers (1e-5) at 3,000 places on the planet and the moon,
  with the game's list and a test list's second moon; and a test list's second moon 10 km
  towards the sun from a place covering it wholly where the game's list leaves it whole, and
  some 27 degrees round, whole again; the flare none in totality and half with half the sun covered (`Air.FlareDimming`).
- **`MoonGroundTests`:** the moon's ground (`MoonGround`, its map made by TerrainGen's moon mode):
  a run of the generator over every sixteenth row of each face giving the shipped map's kinds
  there and its heights within their half step, and before a merge a whole run giving its very
  bytes (so the same layout gives the same map, and the shipped one is the layout's); a map made
  for another radius or elevation refused, naming `moon.bin.gz`; the two big craters a fifth and
  an eighth of the moon's width rim to rim, their rims above and floors below the plains round
  them, well apart and not level with each other; the canyon a rough crescent well south of them
  (its width and path wandering), its floor below the plains along its path by most of its depth
  there (that depth mostly over half the layout's) and its horns curving up round them; the plains
  crowded with craters (over 6% of places out on them on a crater's wall, over 15° across 10 m),
  some on a small crater's faintly pale rim; the flats about a third of the ground (28 to 40%),
  97% of them under 10° of slope and the landing spot in one; from 50 places outside the big
  features, a patch 60 m across under 5° across 10 m within 300 m; the landing spot level (under
  3°), the map itself flat there (levelled), plain and over 1 km from the big features; the rims
  and rays paler than the plains; and the ground's own shadow (`SunSeenOver`): the canyon's floor
  dark with the sun 5° up over its wall, lit with it overhead, nearly all of 60 places in the
  flats lit at 5° from any way, and the sun on the landing spot's horizon only partly seen; and
  the far side's tail, about 5 km long and all on the far side, far from the landing spot, its
  ridge rising from its tip to its base, its mountain's summit rim about 800 m above the plains
  and its crater's floor well below the rim, the ridge a spur standing out of its flank and well
  below its summit where it joins, and the mountain's shadow reaching 1.8 km over the plains with
  the sun just up.
- **`MoonLandingTests`:** a flight with a vessel at the moon, in the moon's physics and its own frame
  (`Simulation(body: GroundBody.Moon)`, whose `Physics` is the moon's): a booster placed upright 5 m over the landing spot with its
  legs out lands under a sixth of g (touching down at about 4 m/s), one thrown down at 20 m/s
  crashes, its first second's fall is within 2% of g/12, and what it reads is the moon's (its
  physics' and its vessel's body, centre, no sea, air or sites, its heights, its orbit's floor its
  highest ground) while the planet's own values stay the planet's; and the HUD's "Too fast!" line
  (`Hud.TooFastLine`) judged for the body coming down on: each body's touchdown limit by its own
  pull, named by its ground ("on the moon"), within `Scene.TooFastBelow` only once up off it, a
  splashdown's limit only over the planet's deep water, and ground below the moon's datum still
  ground; each body's line the same string each time, nothing allocated (`GroundBody.TooFastToTouchDown`).
- **`MoonCollisionTests`:** hitting the moon, its ground the ground's one kind named by its body
  ("the moon"): the upper stage diving at 400 m/s from 3.5 km up is warned of the moon at its lead,
  the warp dropped once (not again when warped by hand), slowed for the moon as it nears, and
  wrecked "Hit the moon ..."; from the planet's side 300 m short of the moon's sphere and about
  15 s from its ground, it is warned before it crosses along the patched path (its tank empty, so
  at once; its closing speed over the moon's ground; the readout's coming line naming the moon;
  asked over and over, allocating nothing) and hits when predicted, and with fuel it is found
  before the crossing and warned at its lead after it, the warp dropped once; one 10 m inside the
  moon's sphere is warned of the moon's ground; another body's ground is asked only of one that could reach it fast
  (`Collisions.MayReachFast`: not landed on the moon or on the pad, nor drifting outside the
  sphere, but diving in from 300 m out); every body's ground (a made-up second moon's too) is
  named by its body; a lander thrown down at 40 m/s is wrecked by the ground's rules, unwarned and unslowed,
  logged "Hit the moon" "on the moon"; one let down softly lands with no warning.
- **`BodyReadTests`:** every read says which body it asks: a lander dropped on the moon and a
  booster landing on Home's zone, each in a physics of its own body, stepped frame by frame side
  by side (a physics step a frame, the tile worker waited for until idle after each, so the run is
  the same every time) are where each is alone, every frame, bit for bit, both landed; `Planet` and
  `GroundBody` have no settable property or field (by reflection); and a tile the shared worker
  makes for the moon among 49 of the planet's is the moon's ground (`MoonGround.Height`) within
  1 mm, and the planet's the planet's within 1 cm.
- **`MoonDrawingTests`:** the moon drawn up close as a mesh (`PlanetPatches.Select` for a
  `PatchBody`): the planet's own patches, to the bit, its own selection's (held as hashes) at six
  places (with both views' ratios, culled and not); 100 m over the moon's landing spot, the
  patches under the camera at the finest level, within the budget, and wherever the moon's ground
  is on the screen a patch kept there, choosing them again allocating nothing; 400 places near the
  moon within the budget; at `Scene.MoonMeshReach` the relief none and the mesh's outline (rays
  against its coarse patches' triangles all round the limb) within a pixel of the sphere's the sky
  draws (at 1280x720), and (by the GLSL's text) the mesh shaded by the map's slopes in full
  whatever its relief, as the sky shades it, and drawing the corona's glow over the moon's limb
  that it covers; and the ground placed from the camera in floats to under a millimetre, so it
  doesn't jitter; the sky over a lander on the moon moving under 0.2 px a frame as the camera
  follows it (seen from the planet's ground's frame); a small view's planes at the moon the main view's; and the sky's rays (`VulkanRenderer.ViewRays`) within 0.2 px of true from Coral
  Isle's pad, 20 km from the origin (made by subtracting the camera from a far point, they were
  5.7 px off: the moon drawn in blocks). Drawn at every body (`SceneInput`, as GameView gathers
  it): the planes near 0.5 m and far at infinity, every body's and the sun's far side in front of
  it and each near side told from a millionth further in the reversed depth's float, from Home's
  pad, low orbits over Home and on the far side, the moon's sphere's edge, landed on the moon and
  the map's furthest zoom, round a lap of the moon; a lander flown on the moon, the camera following
  it for 600 frames, its model as handed to the renderer less its ground as the renderer places
  it moving under 5 mm a frame (0.8 mm); the new rocket on Home's pad, a lander on the moon in its
  physics and another under formula there, each stage handed to the renderer within 1 cm of its
  own model changed by `Bodies.Change(its body, planet)` and turned as its body is; and the
  gathering, with an explosion on the moon, allocating nothing.
- **`SystemMapTests`:** the map (`SystemMap`) zoomed all the way out: from over Home and from the
  moon, at the start's angles and turned over the pole and down to the orbits' plane, through a
  year, the sun's whole disc, every body's centre and the planet's whole path round the sun on a
  1280x720 screen; the camera never within `Scene.ZoomOutSunClearance` of the sun's radii of its
  centre, swept all round in the sun's plane through its distance; the planet's path round the
  sun (`SolarSystem.PlanetPath`, `Bodies.Path`) within a metre of the sun's distance and the moon's
  of its from the planet, each starting and ending at its centre now, a quarter of a lap on where
  the body will be then among the stars; flying a lander on the moon, the map's paths through the
  planet's centre and the moon's in the moon's frame, at their distances, and the sun's mark where
  the sun is; the planet's path faded out at 16 km and 150 km and in full at 600 km, the moon's in
  full from either body's closest zoom; from out past the sun the planet's dot hidden (drawn,
  moved aside) and from the planet's night side the sun's mark hidden by the planet (drawn, moved
  aside); the sky's sun (`SceneInput.SunFrom`) from Home's pad and the furthest zoom at either body
  within 0.01° of where the sun is from there (far out, more than a degree from the planet's way
  to it); flying the pad's rocket, a stage on a round orbit of the moon drawn round the moon and one
  of the planet round the planet, closed laps from where each is; of three stages into the moon's
  sphere, the one beside the flown one (in the physics) patched, ending as its path round the moon
  does, its last leg a stretch of its own, and laid on the screen in runs none of which goes across a leg's start; and one under formula only to where it would cross into the moon's sphere (within 2% of
  the sphere's radius from where the moon will be then); a stage going straight up drawn as its
  line; one under formula escaping, sideways and straight up, drawn only out to the solar
  system's edge (within 1% of its radius from the sun), in no more than a lap's points, and burning, its lap made again to the edge every frame allocating nothing; a stage with its engine off keeping its lap for 120 frames and a burning one's made again
  as its orbit changes; a lap behind the planet cut there and drawn only where it is seen; an
  open lap drawn from where its vessel is now; a stage thrown high on a narrow ellipse (0.93) and
  falling back under formula drawn from where it is over its apoapsis to the ground and no
  further (not on round under it, a second strand), no piece more than a circle its major axis
  across would bow out from the curve; a body's marker labelled with the distance to its
  surface (on the moon the lander's height over its datum, from the pad the pad's over the sea);
  and building and laying out the map allocating nothing; a click on the moon's drawn path (or a
  few pixels off it) the moon's, never the flown vessel's own body's, a click off every line
  nothing, and the point the readout gives a script on the moon's line (`SystemMap.LinePoint`)
  on it; a line takes a click only as a body's, not the flown vessel's body's, and drawn at least
  `Scene.MapLineClickAlpha` strongly (`SystemMap.Clickable`), none once the map is forgotten
  (`Forget`); and a sphere hiding what is behind it, nothing in front or beside, millions of metres
  out too (`SystemMap.Covers`). Each of the map's tests was seen failing with its fix taken out.
- **`MapLabelTests`:** the labels over the view laid out together (`MapLabels.Lay`): a vessel's
  label under a body's faded to `Scene.MapLabelUnderAlpha`, the body's and the rest in full and
  none moved, and the same with the pointer elsewhere; two of a vessel's on each other neither
  faded; a pile ("Closest" over "Pe" under the moon's name) spread only while the pointer is over
  it, into a column `MapLabels.SpreadAside` right of the furthest mark, in the order they lay, none
  left overlapping, each in full with its line, a label apart untouched; still spread with the
  pointer on a label where it has gone only when held by last frame's spread (never spread from
  the pointer only where its column would go), and back as they were (the faded under the moon's)
  with the pointer away or none (on a panel); a marker's label spread judged where it is drawn
  (`SpreadAt`), a click anywhere on the spread pile on it (`OnSpread`), held the next frame with the
  pointer on the moved label, and none once the pointer is on a panel; 600 labels in piles of
  three, the pointer on one, laid out frame after frame (past the first two) with nothing
  allocated, the layout the same each frame; what a click is on
  (`MapLabels.Hit`, what `Markers` asks): a marker's spread label over another marker's dot under
  it, a path label spread the pile's and on no marker, off the column the first marker by its dot
  or label, and off everything none; a pile by the screen's
  right edge spread to the left of its marks, and by the bottom or the top kept on the screen; a pile spread onto the
  moon's name just above it taking that in, none left overlapping; eight on one spot spread
  into a column no taller than it needs, about the pile; and the lens flare dimmed in the map
  to `Scene.MapFlareShare` (`VulkanRenderer.MapFlare`, by `Paths.MapFade`, the map's own fade: 0 by the vessel,
  1 zoomed out), not hidden.
- **`CameraTests`:** the chase camera's following (close behind however fast, keeping up as the
  middle jumps at staging), keeping clear of the waves and a mountainside, placed, free look; the
  mouse's buttons routed as its events are (`ChaseCamera.TurnFor`, `ButtonFor`, `Turn`: the middle
  drag to free look, the right to orbiting, the left to neither) and a press judged as the mouse's
  is (`JudgePress`: never at its first Update, waiting until ImGui has taken it in or for at most
  `MaxJudgeWait` Updates, then a drag only off the panels); a right press moved under
  `Scene.RightClickSlopPixels` all told a click where it is let go (judged before or after it
  is let go), the camera barely turned, one out 3 px and back a drag, one of 100 px turning it as
  any right drag does, and none on a panel (`IsClick`, `TakeRightClick`); and
  across a sphere of influence's edge (`ChaseCamera.Rebase`), coming in to the moon from the side
  where the two ups are 80 degrees apart: its place and view kept within 1 cm and 0.1 degrees, at
  once and after the next frame against a camera left in the planet's frame, then its up easing
  round to the moon's over `Scene.CameraBodyEaseSeconds`, under 5 degrees a frame, still well
  short half way; "Reset camera" after a place bringing back the chase view as it was left; a
  still camera handing back the same view each frame; and turning down under a low vessel, the
  camera keeping on the slide's least sphere round it, with no jump. In `PathTests`, zoomed out
  (M): the vessel looked at from far enough to fit the orbit, and back to just where it was, in
  the same real time in a warp; the map looking down from the pole's side; and the map opening on
  the view its vessel left (turned or zoomed by hand, or given with a view brought back). There
  too, the path's travelling glows (`Paths.Glows`): spaced along a line and on it round a corner,
  none on a short line, the cap spent only on the screen (and half a glow's length past it), a
  line in stretches (hidden, or broken at a crossing) keeping the whole line's places, the phase
  moving each on the way a circle is flown, a line across the screen showing one or two whatever
  the phase (spaced about its width, two at most however it winds), the phase going by real
  seconds at the glows' pace, a line millions of pixels long placed at once on its part on the
  screen, allocating nothing; with a plan, the line's own glows only from its first point past
  the first node (`Paths.LineGlowsFrom`: the path to the node lies over it before that and carries
  them), every point before it on that path; and a label at the half-way point
  between two steps not flipping (`Paths.StillShows`).
- **`CameraSubjectTests`:** the main camera on something other than the flown vessel
  (`CameraSubject`, `GameView.Aim`): each kind of subject found at its body (a vessel's middle, a
  body's centre, a site's top, a point of interest), and none for a number naming nothing; a
  vessel it is on kept in its body's physics (`FlightInput.Watching`), and let go once it is off;
  a stage round the moon followed from the planet's frame within 1 cm, each frame's step within
  1 cm of its own motion, and the camera's target and place in line frame to frame within 1 cm;
  one crossing into the moon's sphere while the flown one stays at the planet, its view kept at the
  crossing and its up eased round to the moon's, under 5 degrees a frame; one lost (crashed, or no such vessel) giving the camera back to the
  flown one (`CameraSubject.Kept`); one at a body with no physics shown its link-up over the main
  view (`Flight.WatchLink`, the readout's `Camera link`), the physics made behind it the frame
  after, and one taken off it before that lets it go, nothing made; and a place (Home's pad) looked at from `Scene.PlaceViewHeight` over it and
  `Scene.PlaceViewAside` aside, turned and zoomed round it; and the markers' distances measured
  from the flown vessel wherever the camera is (`Markers.DistancesFrom`: on the moon, not 0 m to it);
  and the map centred on a body, the one the flown vessel is at as well as another, looking at its
  centre from over its pole, nothing in the camera not a number; on a body the camera held among
  the stars as the body turns under it (the planet's, in the map and the chase view, its place
  about the centre kept where it was put after 200 s while the ground turns 65 degrees; the moon's
  from the planet's frame, then across the view's frame changing to the moon's); a right click finding a vessel
  seen at another body (`Hud.VesselClicked`, changed into the view's frame); a click on a body's
  disc finding it, and none off it, behind the camera or behind the sun (`Hud.BodyDisc`); and the
  compass tape keeping the facing it had on the flown vessel while the camera is elsewhere
  (`Hud.TapeFacing`); the main view's sun area and the camera's velocity by what it is on
  (`GameView.MainSunArea`, `CameraVelocityOf`); a camera on a vessel of another world watching
  nothing; and the stage round the moon's checks bounded by a float's rounding there.
- **`BodiesInPlayTests`:** a physics at each body in use (`Worlds`), every vessel at its own body,
  all played out at once: flying a lander in a moon orbit (the moon's physics), a small view's
  booster flies its landing burn down onto Home's zone by its retrograde hold (the planet's
  physics: its thrust there), landed with the touchdown it has alone, the lander's orbit within 1 m; flying the rocket on Home's pad, a small view's lander comes down on
  the moon's plains, landed and frozen within 10 s; a lander on the moon in a world played 200
  years within 1 cm of its place over 60 s; a vessel 400 m over the moon's highest ground under
  formula keeping its orbit (apoapsis and periapsis) within 1 m over ten laps; under formula at
  the planet (the camera at the moon) a falling booster landing by the formula's rules, never held
  in the physics; under formula at the moon one diving in at 60 m/s wrecked ("Hit the moon at",
  logged unwitnessed), the moon given no physics; the moon's landers under formula with no
  physics made for it (one body's in use); the moon's physics, emptied, released after
  `Scene.PhysicsIdleSeconds` and the same world taken again from the pool; the moon's physics asked
  for ahead of need made off the step (on the tile worker's idle time), once, in order; a world
  loaded with a vessel's legs on their way out under formula, and one handed out with its legs on
  their way, each swinging them on out under formula; six bodies' worlds
  made and stepped side by side on the pool alone (a test list: the planet, the moon and four more
  with the moon's ground, a craft 2 km up at each: nothing at more than two bodies through `Flight`), each world's memory under 9.75 MB (half as much again as the 6.5 MB measured); a
  lander's lights by the moon's own day (off at its noon, on at its midnight, at the near side's
  middle and the landing spot); a world of vessels at both bodies saved and loaded, each at its
  body within 1 mm, in the physics or under formula as it was when saved; a file of the old format, or with
  a vessel at an unknown body, refused (listed as unreadable); the readout's `Planet height` from
  the moon (and -90° from the pad); and a frame's parts for vessels at both bodies (their rules,
  the formula at each, who is in which physics, the look ahead, the lights and sun at each body,
  which body each is at, the drawing's parts, the camera at the moon, the reading) allocating nothing;
  and a world made off the main thread (as one made ahead is), or another body's released and let
  go, disposed without taking the process's rafts from the planet's physics in use (a floe kept up
  to date under a booster stays so).
- **`CrossingTests`:** crossing a sphere of influence's edge, a vessel handed to the next body's
  physics or formula: the upper stage with its engine off coming at the moon's sphere at 100 m/s,
  started 30 s out, into the moon's physics made ahead (there the step before), its place across
  the crossing's frame, in the planet's frame, within 0.5 m of where the frame before's velocity
  carries it and its velocity within 0.05 m/s of where the frames before's change carries it,
  `Body` changed once, and no frame from the crossing's over 1.5 times its neighbours' (a crossing
  flown first, so its code is compiled before it is timed; these two `Run=Alone`, being timed);
  the same out of the sphere into the planet's physics, straight out; the best altitude begun
  again over the moon as it crosses (25.6 km at the planet, 3.9 km from the crossing), Reach
  Orbit's 3,000 m still reached, and kept as it falls on in; under formula into it and
  out again, never given a physics; the crossing within 0.1 s and 5 m of where `Patched`
  predicted it; no flip-flop at the edge, each 120 s: an orbit round the moon out past the
  sphere's radius but short of its leaving edge never changing body, one just past the leaving
  edge leaving once and never back, and a path from the planet grazing 30 m inside the radius
  going in once at it and out only past the leaving edge; two under formula one behind the other
  into the sphere, the first crossing alone, their collision found at the planet dropped and
  found again at the moon once the second crosses, both wrecked there;
  two side by side a few metres apart, one just inside the sphere, each changing body at its own
  step (the first, then the second) and never in the same physics while at two bodies, flown (in the
  physics) and under formula; a booster landed on the planet's zone and a lander landed on the
  moon (and the new rocket on its pad) never asked which body they are at (`Flight.CrossAsked`
  0 every frame), at 1x and at 32x; a vessel at the planet coming straight at the moon's middle
  at 1,000 m/s from 4 km out, left unasked at first, then never a step inside the sphere still at
  the planet, flown (in the physics) and under formula; switching from the pad, warped up, to a lander on the moon with no
  physics there: flown in the moon's physics, `ViewRebased` once, the frame Surface over the moon,
  no warning nor anything coming and the warp kept, and back again the same; the link-up on a
  switch to a body with no physics (on from the click's frame, the physics made the frame after,
  never in the click's, for `Scene.LinkUpSeconds` of real time to within a frame, `Link` reading
  "connecting" then "connected", made by then) and none to a body whose physics is made; the
  link-up's own state on a hand-held clock (its fixed length with the physics made at once, and a physics slow to make); a switch
  to a body whose physics is made while the link clears, or a new flight while it waits, ending
  it at once; and a frame's parts crossing and after (the flown vessel in the physics, two under
  formula crossing beside it) allocating nothing, each body's physics' step counted apart, and the
  link-up's frames through `RunFrame` allocating no more than as many without it. A small
  view given to a lander on the moon with no physics there: its link-up on from the click, the
  frame after (as the view is first drawn, with its static) making nothing, the lander left under
  formula though watched, the moon's physics made and the lander brought in the frame after that
  (once: its craft the same to the end), `Link <vessel>` "connecting" then "connected" for
  `Scene.LinkUpSeconds` to within a frame; a view closed while its link-up waits letting it go (nothing made,
  no reading left, a fresh link-up when opened again), and one closed while it clears let go at
  once; a waiting view dropped from a full strip (a switch to a vessel with none) let go as one
  closed is; a view given to a satellite under formula at the planet, whose physics is made, brought in
  at once with no link-up. At the top rate (`Scene.TopRate`): the moon transfer, flown and nobody watching it, handed
  across at its crossing's moment (its frame cut there), within a metre of the moon leg's place a minute
  on, the warp kept; a crossing 0.3 s past where its orbit was first looked along to
  (`Scene.MapCollisionHorizonSeconds` on), still handed across at its moment; and a warp to a point
  30 s past the crossing, crossing on the way and stopping on its time, on the moon leg within a metre.
- **`MoonTripTests`:** the moon trip flown by `MoonPilots.cs`, each leg from where it is about
  (`MoonTrip`): from a low orbit round the planet (300 m over the air, the moon a transfer's lead
  ahead, with the fuel the satellite mission's ascent leaves the upper stage, `MoonTrip.AscentFuel`)
  into the moon's sphere within 1,200 s, fuel left, never faster than 1,000 m/s over the ground
  (about 15 s: the transfer itself is the slow part); arriving 200 m inside the sphere, captured
  into a closed orbit round the moon, its periapsis over 100 m above its highest ground, Auto
  orbital; from a round orbit over the moon's poles, landed within the levelled plains round the
  landing spot (150 m) under 2 m/s, frozen within 10 s, the fuel it took reported; and a dive at
  the moon, closing at 300 m/s, warned of as "the moon" (hitting over the fast collisions' speed),
  crashed "Hit the moon at", and logged as hit on the moon. Long: the whole trip from the low
  orbit to the moon's ground, landed as the landing is, the fuel at each leg's end reported (short
  of it, it fails), and the speed over the ground low over it under `Scene.GroundTopSpeed` every frame of it (the game's hold never taking hold); and a
  playthrough of the satellite mission's ascent, its upper stage left (the satellite let go) with
  `AscentFuel` within 5%.
- **`MoonTrip`:** not a test: the trip's starts (`LowOrbit`, `Arriving`, `MoonOrbit`), the upper
  stage alone with `AscentFuel` in its tank, or (`lander: true`) the lander launcher's stack, its
  upper stage with `LanderAscentFuel`, the lander on and full, the fairing gone; the lander alone
  where `MoonOrbit` puts the stack (`Lander`); the low orbit's plane tilted a little from the moon's
  own (found on the patched path), so the transfer passes over the moon's pole and the capture is
  into an orbit over both its poles, over the landing spot; that plane for a burn at any time
  (`TripNormal`), and when a pad (Home's by default) is next under the trip's plane as it will be
  when the stack it launches comes round to the transfer's burn (`Launch`, by the ascent's measured
  `AscentSeconds`, `AscentDegrees` round and `AscentLapSeconds`, measured at the descending
  crossing and checked at the ascending; the wait from the ascent's end to the burn is the
  stack's first meeting with the burn's place after `Wait`, which moves on meanwhile; a step in
  which that wait goes round to the next meeting is searched on both, so no crossing in it is
  missed; the plane follows the moon round, so Home's launches come at its descending and
  ascending crossings in turn, its first four after the clock's start 485 to 590 s apart),
  with the fuel the lander launcher's upper stage has once in it (`LanderAscentFuel`).
- **`LanderTripTests`:** the lander launcher's ascent into the trip's tilted low orbit: Home's
  first two launches, the pad within 0.1° of the trip's plane at each and the stack, gone round
  from the ascent's end to the burn, within 0.1° of where `MoonTrip.LowOrbit` has the stage at
  it, one heading south of east and one north, under a moon lap apart; a launch found in one
  step that also holds its wait going round to the next meeting, as the usual step finds it
  (Home's third, just before; long, its fourth, just after); (long, some 90 s) every launch site surveyed for the moving
  plane (its first two launches within 3,000 s, the plane's tilt then, the heading off due east as flown, and whether a
  drone ship is in the booster's reach on it), printed as a table; and (long) a playthrough from
  Home's pad at `MoonTrip.Launch(Scene.ClockStart)` by `PlaneAscentPilot` (`LanderPilots.cs`): its
  orbit within 0.5° of the trip's plane, its periapsis over the air, the booster landed on Home's
  second drone ship (its bonus won), the upper stage's fuel within 5% of `LanderAscentFuel`, the
  next transfer window within a lap and the trip's plane then within 1° of the stack's, and where
  the booster came down reported; and (long) the same from Home's next launch, at its ascending
  crossing, north of east (the booster, with no drone ship in reach, reported only). The lander's
  trip, by `MoonPilots.cs` and `LanderMissionPilot`, each leg from where it is about (`MoonTrip`'s
  starts with the lander), with the margins it is flown with (`UpperStageMargin`, 10% of the
  upper stage's tank once round the moon; `LanderMargin`, 40% of the lander's landed): the stack
  arriving in the moon's sphere captured into a closed orbit over the moon's highest ground, the
  upper stage's margin kept; in that orbit, the lander let go cleanly (the lander alone flown on,
  no touch within 10 s, both still flying, each one's periapsis within 2 m of the stack's and its
  apoapsis within 2 m of the stack's lifted by the push's own rise, square to the orbit); the
  lander from its orbit landed under 3 m/s within 50 m of the spot (`PointOfInterest.MoonLandingSpot`)
  with its legs out and its margin kept, its fuel, distance and time reported; and landed at the
  spot, its bay doors opened by the pilot (`Mission.BayOpen`). Long: the stack from the low orbit
  into the moon's sphere (as `MoonTripTests`' transfer, the lander's weight added); the lander
  released from the stack round the moon and flown down by the pilot (the release takes the spot
  past where the line-up begins, so it waits a lap with its engine off and lines its track up on
  the next pass, some 800 m), landed within 50 m of the spot with its margin kept, the upper stage
  left flying (some 570 s of flight: the quick one beside it starts the lander already let go);
  the whole trip
  from the low orbit to the spot, the bay opened, both margins kept, every leg's fuel reported and
  the speed low over the ground under `Scene.GroundTopSpeed`; and a playthrough of the mission from Home's pad
  (`Playthrough_land_on_the_moon`): succeeded, the score and each step's points printed, both
  margins kept, the booster's landing reported.
- **`LowPassTests`:** a very low orbit of the moon, found from its height map (`LowPass`, not a
  test: `Find`, the round orbit whose track over a number of laps crosses the lowest highest
  ground, searched over 2,000 orbit normals on a Fibonacci sphere, prograde and retrograde, each
  track read every 1° of the orbit, the ground under it turned back by the moon's turn since the
  start, then twice refined round the best at a quarter of the spacing, the search run again at
  the radius found and that radius raised until the track, read every 0.05°, clears its highest
  ground by `LowPass.Clearance`, 30 m; kept once found, by body and laps; `Start`, the lander alone,
  full, legs in, engine off, on it, or as far ahead along its track as asked): over one lap and
  five, the found track read every 0.25° never over its `Highest`, read every 1° no higher than
  the sphere's best normal's alone (the refinement lost nothing), no higher than the moon's
  equatorial circle (either way round) or two polar ones at the same radius, its normal, `Highest`,
  `Radius`, `LapSeconds` and the search's time printed; and the lander flown on the one-lap circle
  in the physics over the stretch of its track's highest ground (`LowPass.HighestAt`, from 15 s
  before it to 15 s after), still flying, touching nothing, its least Ground no less than 25 m and
  no more than 31 m (so it was over that ground), its periapsis and apoapsis within 2 m of the
  circle's, its fuel untouched. Long: the same over the whole lap (some 250 s of flight, its tiles
  laid all the way round), and over five laps of the five-lap circle.
- **`MoonTripWorlds`:** not a test of the game but a writer, for the GameDriver, as `MoonWorld`
  is (with `GAMEONE_WRITE_MOON_WORLD`, otherwise skipped; the writing shared, `DriverWorlds`): the
  trip as six saved worlds for `gameone-moon-trip.txt`, each with the upper stage flown and a
  satellite 9 km over the planet for a small view there: three of the starts
  (`moon-trip-low-orbit`, the moon targeted and the window 90 s ahead, `moon-trip-arriving`,
  `moon-trip-moon-orbit`) and three captured from the trip flown from the low orbit by its
  autopilots (`moon-trip-burnt`, the transfer burn just done; `moon-trip-way-down`, the de-orbit
  burn just done; `moon-trip-landed`, frozen at the landing spot by night), each flown 5 s first;
  written in the order the script plays them, so the next is always the world list's foot
  (`Writes_the_moon_trip_worlds`). A second writer, `Writes_the_lander_worlds`, writes three more
  into a folder of their own for `gameone-lander.txt`, by day at the landing spot with the planet
  in its sky (the year's fair time with the sun highest over the stack and the spot): the lander
  launcher's stack on `moon-trip-moon-orbit`'s orbit 60° short of the spot (`lander-moon-orbit`),
  and, flown on from its release by `LanderMissionPilot`, the lander alone just after its de-orbit
  burn (`lander-way-down`) and landed on the spot, its legs out and doors shut (`lander-landed`),
  the upper stage on its orbit above in both.
- **`OnTheMoon`:** not a test: the helper standing a vessel on the moon in its frame (`Standing`,
  its feet half their stroke in) and a lander landed there (`Lander`: the satellite launcher's
  upper stage, at the moon).
- **`MoonWorld`:** not a test of the game but a writer, for the GameDriver, as `IceWorld` is:
  with `GAMEONE_WRITE_MOON_WORLD` set to a folder (otherwise skipped), it writes a saved world of
  two landers landed on the moon (`OnTheMoon.Lander`), one at the landing spot and one at the near side's middle,
  at the first time in the year the landing spot sees the planet rise through 2° with the sun at
  least 3° up there (the one with the sun nearest 100° round from the planet), for
  `tools/GameDriver/examples/gameone-moon.txt`. It checks both stay put in a flight first.
- **`AirTests`:** the air's light (`Air`, which the shaders repeat): at noon a blue sky, paler and
  brighter at the horizon, as bright overhead as the sky was before the air's model, and nearly
  white sunlight; a low sun's light orange; no sunlight on the night side; after sunset the air
  above still lit, and the sky overhead blue; a darker sky from higher up; Polar's light warmer
  than Home's; from orbit, the rim glowing and space black; the noon haze half faded between
  2.2 and 2.8 km; the lens flare dimming with the sun; finite light from rays up, down, grazing,
  and from under the sea; sunlight from above the air the sun's own; and a segment's light (the
  haze's) what its ray gives.
- **`AirTableTests`:** the tables the shaders read for the sun's light and the sky's light on a
  face, looked up as the shaders do, stay within 3% of `Air` across every height and sun angle.

Both autopilots steer as the direction rings do, by holding the direction they want
(`FlightInput.Hold`), which the rocket turns to only as fast as it can.

The game reaches its flight only through `FlightInput`, so none of this needs test code in the game
itself. For what the player sees and hears, use `tools/GameDriver`, which runs the real game.

Jolt's set-up is process-wide, so only one `Simulation` can exist at a time, and the tests don't
run in parallel.

## Short performance tests

`ShortPerfTests` time the physics headless, part by part, so a change meant to make it faster (or
one that might make it slower) can be seen to. They are run only when working on performance,
never as part of the normal suite: they are skipped unless `GAMEONE_PERF=1`, and take about a
minute and a half.

```sh
GAMEONE_PERF=1 dotnet test GameOne.slnx --filter "FullyQualifiedName~ShortPerfTests" --logger "console;verbosity=detailed"
GAMEONE_PERF=1 GAMEONE_PERF_REBASELINE=1 dotnet test GameOne.slnx --filter "FullyQualifiedName~ShortPerfTests" --logger "console;verbosity=detailed"
```

Add `-c Release` for a Release build's numbers (an entry of their own). Run them on a laptop
plugged in, with Windows' power mode on "Best performance": on battery, or in a quieter mode, the
CPU's clock and its turbo budget vary, and the numbers with them. Close what you can of the
machine's other work, too.

- **The scenes** (`PerfScenes`), each loading a part of the physics:
  - *A hundred satellites coasting*: the world of a hundred satellites, and a rocket on the pad,
    2400 steps (40 s). The rules, and the contact and nearness work, for many vessels.
  - *Over the floe band*: four boosters 40 m over the Austral Sea's floe band and 24 more crossing
    it, with the floe bodies at their cap (`Scene.MaxFloeBodies`), 120 steps until the four come
    down. The water.
  - *Low over rough ground*: six boosters thrown up and out over a rough slope, 300 steps while
    they are in the air. The ground's tiles, laid and taken up; its report says how many came
    from the worker, were made in the step, or came from kept shapes.
  - *Five rockets to orbit*: five two-stage rockets launched together (one on Home's pad, four
    landed round it, `PerfScenes.FiveRocketsOffset`, 23 m out: at 20 m the west one hits the service tower),
    each flown by its own `OrbitAutopilot` a step in turn, until all five are in orbit; and, as a
    stretch of its own, the steps with all five boosters burning. It fails as soon as any rocket
    crashes, with the cause.
  Each checks its flight did what it should (in orbit, at the cap, the tiles laid), so a broken
  scene fails rather than timing something else.
- **The speed sweep** (`How_fast_a_craft_can_go_near_the_ground_before_a_frame_drops_under_60_fps`,
  `PerfScenes.SpeedSweep`): how fast a craft can go near the ground before a frame's physics
  outgrows `ShortPerf.PlayableFrameMs` (15 ms at the 95th percentile, within a 60 fps frame's 16.7
  ms: set for a test machine whose load and power can't be controlled). It is paced at real time (the test's loop waits out each 1/60 s
  frame before the next step, so the worker has the time it would have in play; nothing in the game
  paces it). Each case is flown once: coasting low over the rough slope at 50, 100, 200 and
  300 m/s (low orbit is about 313 m/s), each in a low ballistic arc that stays inside the band
  where its tiles are urgent (`Drops.Level`), measured until it comes down, and at the ground's top speed
  (`Scene.GroundTopSpeed`), rising away from it; and crashing into it
  (`Drops.Crashing`, as the crash theory in `TerrainTileTests`) at 100, 300, 1000 and 3000 m/s and
  the ground's top speed, straight down, at 45 degrees and at 10 (those over `Scene.FastCollisionSpeed`
  slowed for as they come, as in play). It reports each case's median and 95th percentile
  frame, the tiles by the worker and in the step, and whether it fell through (more than a metre
  into the ground, or near it in some frame with no tile under its lowest point:
  `Drops.TileUnder`), then the fastest level speed and crash at each angle that is
  playable. Reported, never failed. It is not one of the baseline's entries: it is neither compared
  with the baseline nor written to it (a rebaseline leaves it out).
- **Fifty boosters landing unwatched** (`Fifty_boosters_landing_unwatched`,
  `PerfScenes.UnwatchedLandings`): fifty boosters coming down on their engines onto level ground,
  nobody watching them, moved and their touchdowns judged by the formula; each part's time and
  share, for the formula's cost against the same scene in the physics. Not a baseline entry: it
  is neither compared with the baseline nor written to it.
- **The paced fast collisions** (`A_fast_pass_holds_60_fps`, `PerfScenes.FastCollisions`): the frame
  rate held through a forced slow-down, paced at real time as the sweep is. Two boosters head on
  at twice the ground's top speed closing in the vacuum at 5 km, passing 10 m apart and colliding, and one
  into the rough slope at the ground's top speed at 10 and 45 degrees below level, and one diving at the ground's
  top speed into the field of 49 boosters frozen on the zone (`TerrainTileTests.FrozenField`),
  whose boosters on and beside its way in wake as it comes, mid-slow-down, each flown from a little before its slow-down to
  its end; for the record, a pass level at the ground's top speed 30 m over
  the Boreal Sea's floe band, which hits nothing and is never slowed; and a near miss slowed by
  the setting ("near miss 1000": `NearMissTests.NearMissPass`, flown with
  `FlightInput.NearMisses` on), held by the same rule. It prints each one's frames
  before the slow-down and during it (median, 95th percentile and worst frame's physics), the
  least scale, the slow-down's real seconds, and the shares of its slowed frames' physics that
  predicting the collisions (`Rules: Collisions`) and the tiles took; it fails if a slowed frame's
  physics is over `ShortPerf.PlayableFrameMs` at the 95th percentile, or if more than one slowed
  frame of a case is over it, or that one is 1.5 times over (one a little over is let pass for the
  OS's jitter; each over is printed by case, frame, ms and where its time went: its parts and
  pieces, the crafts awake, the tiles and those made in the step): slowed, the game runs the same steps a real second, each shorter, so the frame
  rate must hold. Not a baseline entry either.
- **Where the field's slowed frames go** (`FieldPerfTests`, run by the same filter's sibling,
  `FullyQualifiedName~FieldPerfTests`, with `GAMEONE_PERF=1`): the paced fast collisions' dive
  into the field of 49 boosters, flown paced to the end of its slow-down after a warm-up, three
  times, and each part and piece of a slowed frame's physics printed in ms and as a multiple of
  Jolt's own step in the same frames, with the steps a frame, the crafts awake and the slowed
  frames' count. Jolt's step does the same work whatever the game's code round it does, and runs
  faster or slower with the machine, so the multiples hold on a slow or busy machine where the
  times don't: judge a change to the slowed frames' cost by them (each run's are printed, so
  their spread shows how big a change they can see). It only reports: never compared, never
  failed.
- **The paced crowd** (`A_crowd_handed_in_and_out_reports_its_frame_rate`, `PerfScenes.Crowd`): the frame
  rate while a crowd is handed into the physics and out of it, paced at real time as the
  fast collisions are. The satellites preset copied 250 times round the planet (a thousand
  satellites: `WorldTests.ManySatellites`), the last made the one flown, going round the other
  way between the two rings in the launch's plane, 500 m from each: it passes one of each about
  every 0.4 s, closing at some 590 m/s, so each ahead comes into the physics some 3 km out and
  leaves it behind. Flown three times: with the map closed; open (`Paths.Build` each frame, so the
  map's collision search works on its thread beside the step); and closed with near misses slowed
  for (`FlightInput.NearMisses`, so each step also looks for the flown vessel's nearest miss over
  the whole crowd, `Collisions.NearestMiss`, its looser reach sending satellites to the chorded
  march the collisions' prediction runs); each 12 s untimed first (the
  world's first step hands the crowd out at once, and the JIT tiers up the collisions' check), then
  1,200 frames timed. It prints each case's frames' physics (median, 95th percentile, worst),
  how many came in and went out, the most in the physics at once, the rules', the formula's and
  the predictions' (`Collision`, the near misses' included) shares, the map's own frame (95th percentile and worst), and the worst frame and each over
  with where its time went (its parts and pieces, the crafts awake and under formula, its
  handovers and the garbage collector's collections); it says whether each case holds the fast collisions' rule (every
  frame, not only slowed ones), but never fails on its times, which move with the test machine's
  load (its shares hold); it fails only if fewer than twenty came in or went out, or anything was
  wrecked. Not a baseline entry.
- **The runs** (`ShortPerf.Measure`): a warm-up of at least 8 s of wall time
  (`ShortPerf.WarmUpSeconds`, so the JIT has settled), then 3 runs timed. The floe band and rough
  ground are short, so each run repeats them 4 times (`PerfScenes.ShortRepeats`), each in a fresh
  simulation. Each step is sampled from `Timings`: its parts (Rules, Jolt, Tiles, Water, Contact,
  Rest), the pieces of Tiles and Contact under them (`StepDetail`, as `Tiles: Samples`,
  `Contact: Vessels` and so on: timed within their part, so they don't add to the step, and a
  part's come to a little less than it) and the whole step, in ms; and the speed (game time over the steps' time, beside the 32x
  the warp asks for). A step here is a flight step, a frame's worth of game time (1/60 s), which
  is two of Jolt's 1/120 s steps (a sample's `PhysicsSteps`): "ms per step" is per flight step,
  never per Jolt step, whereas the panel's "steps a frame" counts Jolt's. Each prints the median,
  95th percentile, mean, the mean's share of the step's mean, and spread (the runs' medians, the
  largest less the smallest, over the median) of each. The mean is what a part costs in all
  (its share of the time the warp is limited by): a part that does its work in spikes (the
  ground's tiles, laid a few steps in many) has a mean well over its median, and its median alone
  understates it.
- **On the fast cores** (`FastCores`): on Windows, the warm-up and the runs are held on the
  fastest cores, as the game is in play with its window in front. The process is opted out of
  power throttling (so Windows doesn't treat it as background work), its default CPU sets are
  the cores of the highest efficiency class (skipped where every core is alike), and its priority
  is raised to High; all put back after. Without it, a windowless test process on a hybrid CPU
  was put on the efficiency cores for half its steps or more, by invocation (2026-11-02: the
  floe band 1.5 or 2.3 ms a step). Each run says which kind of core its steps were on
  (`CoreKinds`, the main thread's, noted between steps): it should read 100% on the fastest.
- **The verdicts:** each part and the speed against the baseline's median: "better", "worse", or
  "about the same" within the largest of this run's spread, the baseline's and 20% (`PerfBaseline.Tolerance`: the test machine's runs vary that much); a part under 0.01 ms in both
  is too small to time, and reads "about the same". Slower is reported, never failed, as the numbers move with the machine's
  load. Each scene's report says how big a change its spreads (and the baseline's) would let it
  see: on a busy machine, or one on battery, that can be 15% or more, so check it before reading
  anything into a verdict.
- **The baseline** (`PerfBaseline.json`, beside these tests, committed): an entry per machine (its
  key a SHA-256 of its name, CPU and processor count, so the file names no machine) and per build
  configuration (Debug and Release are apart), with the commit and the time it was measured (the
  commit with `+changes` when the tree had changes beyond the baseline itself, and `+changes?`
  when git couldn't say). A run
  compares only with its own machine's and build's entry, and says when there is none. On main it
  holds main's numbers.
- **Rebaselining** (`GAMEONE_PERF_REBASELINE=1`): writes this run's numbers into this machine's
  entry, keeping every other machine's. A performance branch does it once its change is done, and
  commits the file, before it is merged. A `PerfBaseline.json` that is there but can't be read (a
  merge's conflict markers, say) is never saved over, as that would lose the other machines'
  entries: the run says so, and the rebaseline fails, until the file is mended.
