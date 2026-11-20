# CLAUDE.md: the tests

What to know when writing, changing or running the tests in `tests/GameOne.Tests` (the root
[`CLAUDE.md`](../../CLAUDE.md) says when each kind is run; this file says how). The tests' [`README.md`](README.md) says what each
test covers.

## Flight tests

- **What they are:** `dotnet test GameOne.slnx` steps the real `Flight` rules and Jolt physics
  headless, about 75x real time, with autopilots (`Autopilot` for landing, `OrbitAutopilot` for
  orbit) flying through `FlightInput`, the same input the keyboard makes. Add a test for each new
  rule or mission.
- **The time rate is the flight's:** `Simulation` steps a real frame at a time by
  `Flight.RunFrame`, as the game does, so the player's rate, the warp and the forced slow-down
  (`Flight.Slowed`) run as in play; it gives each pilot the frame's game dt
  (`FrameSeconds × Flight.TimeScale` as the frame begins), not the real frame, as a pilot's rules
  (a throttle's rate, a hold's gain) run on the game's clock. `Simulation.Time` is game time; a
  test that needs real time counts frames (frames × `Simulation.FrameSeconds`). Never set the
  rate or the step from a test (`Worlds.StepScale` is the flight's to set), but in the
  time-rate tests themselves (`TimeRateTests`), which check the shortened step on its own (a
  run's steps a real second, a fall, the step's leftover after a sharp drop) and so set it directly.
- **Switches and link-ups:** every switch, carry-on and rewind shows a link-up and flies the vessel the frame after. `Simulation.Step` and `Run` make a switch asked in an input at once (its frame, then the frame it is flown in, then the rest of the input, both of no time: the frame a vessel is flown in takes nothing of its input made for the vessel left, its view, plan, holds, rings and target), so a test about something else needn't wait; a test of the link-up itself sets `Simulation.ShowLinkUp`. A test that steps `Flight.RunFrame` or `Update` directly runs one more frame before the new vessel is flown.
- **Start where the test is about:** a test that needs to start somewhere (a sea, a zone,
  mid-flight) puts its vessel there directly (`Drops`, a world it writes), never flies there from
  the pad: re-flying the launch wastes time and makes the test break with every change to the
  launch. A test that needs a time of day starts its world there (`WorldState.Time`, from
  `Sun.WorldTimeFor`), never by waiting.
- **Keep the game testable:** keep game logic reachable through `FlightInput` and out of
  `Controls` and the renderer. Never put test code (autopilots, test switches) in the game itself:
  the GameDriver is kept a separate application that other developers can use on their own games,
  so it must not depend on GameOne's insides. Where the game has to meet it, that is one small
  interface, kept to one place rather than spread through the game: the HUD's readout
  (`src/GameOne/Readout.cs`; see [`tools/GameDriver/CLAUDE.md`](../../tools/GameDriver/CLAUDE.md)).
- **Watching a test:** to let the user watch a test, set `GAMEONE_WATCH=1` (real time; larger is
  faster): the harness shows the flight through the game's `GameView`. Showing a flight belongs in
  `GameView`, so the game and the watched tests stay alike. It is paced by real frames, as the
  game is, so a slow-down shows slowed; once something is coming at the flown vessel
  (`Flight.ComingAt`), the camera turns to keep the other in view (`Watcher.Track`, test-only
  until the game has camera modes). A test written for the user to judge a feel by watching
  reads its tuning from `Scene`, so the user changes it there and watches again: so far
  `NearMissTests.A_near_miss_to_watch` (1,000 m/s) and `A_top_speed_near_miss_to_watch`
  (8,000 m/s), and `FastCollisionTests.A_fast_collision_to_watch` (1,000 m/s) and
  `A_top_speed_fast_collision_to_watch` (8,000 m/s), and
  `ComingTests.A_small_views_warning_switched_to_for_the_crash_to_watch` and
  `A_small_views_warning_switched_to_for_the_near_miss_to_watch` (a small view flashing red or
  amber, then switched to 5 s before), all shaped by the slow-downs' figures
  (`SlowdownFrom`, `SlowdownPassSpeed`, `SlowdownHoldWithin`, `SlowdownEaseSeconds`) and the near
  misses' (`NearMissLengths`, `NearMissSlowAbove`). Run one at a time.
- **Near misses and what is coming** are in `NearMissTests` (predicted, and slowed with the
  setting on, `FlightInput.NearMisses`) and `ComingTests` (`Flight.ComingAt`: the small views'
  flash, the main view's coming mark and its click, the compass's tick); the fast collisions
  themselves in `FastCollisionTests`, `CollisionTests` and `CollisionWarningTests`.
- **Whole flights by frame of reference** are in `FrameFlightTests` (`FramePilots.cs`).
- **The moon's whole flights** are in `MoonTripTests` (`MoonPilots.cs`): the upper stage from a
  low orbit into the moon's sphere, captured into an orbit round it, landed on it, and a dive
  into it; each leg starts where it is about (`MoonTrip`), with the fuel the satellite mission's
  ascent leaves the stage (`MoonTrip.AscentFuel`, measured by the playthrough beside them and
  written in: measure it again after a change to that ascent or the stage). The lander launcher's
  ascent into the trip's tilted plane from Home's pad (`LanderTripTests`, `LanderPilots.cs`) has
  its own measured `MoonTrip.AscentSeconds`, `AscentDegrees`, `AscentLapSeconds` (by which
  `Launch` times a launch so the stack comes round to the transfer's burn with the plane still
  its own) and `LanderAscentFuel`, written in likewise: measure
  them again after a change to that ascent, the lander launcher or the trip's plane. The lander's
  trip (`LanderTripTests`) is flown by the same pilots, each reading the flown vessel (its own
  stage, and its own state: the flight's stays flying while the upper stage left round the moon
  does), from `MoonTrip`'s starts with the lander (`lander: true`, and `MoonTrip.Lander` for the
  lander alone), and by `LanderMissionPilot` for the whole of it: the stack captured round the
  moon, the lander let go with its nose square to the orbit (so the push apart barely changes
  either orbit), and the lander flown down by the spot, its legs out and its bay opened. It is
  held to two margins (`LanderTripTests.UpperStageMargin`, 10% of the upper stage's tank once
  round the moon; `LanderMargin`, 40% of the lander's landed): if one falls short after a
  change, the lander's engine is made more efficient (`BurnSeconds` up), never more powerful, or
  the upper stage grown (its fuel up, its dry mass by a tenth of that). The whole trip, leg
  after leg, is long. The warp on a long stretch runs the same steps (a frame of it is many), so it
  saves no test time: the transfer, on its orbit with the engine off, is the time it takes.
- **The very low orbit of the moon** (`LowPassTests`, `LowPass`): the round orbit whose track
  over a number of laps crosses the lowest highest ground, found by searching the moon's height
  map (any body with no air: `LowPass.Find` takes a `GroundBody`), flown `LowPass.Clearance`
  (30 m) over that ground. The body turns under it, so a track depends on the circle's radius
  (its lap time) as well as its normal: the search is run again at the radius it finds. A search
  takes about 0.15 s for one lap and 0.9 s for five, kept once done (by body and laps), so the
  tests and the performance test share it. After a change to the moon's map or `Scene.MoonRadius`
  the circle found changes with it; nothing is written in. A lap 30 m over the ground is some
  250 s of flight with its tiles laid all the way round (15 s or more of test time), so the normal
  suite flies only the stretch over the track's highest ground (`LowPass.HighestAt`, started just
  before it by `LowPass.Start`'s `secondsOn`); the whole lap and five laps are long.

## Running them: the runner, the playthroughs, the record

- **The runner:** `dotnet run --project tools/TestRun` runs the normal suite split across 8
  processes (about 90 s, against minutes for plain `dotnet test GameOne.slnx`, which still runs the
  same tests serially). Its [`README.md`](../../tools/TestRun/README.md) has the options. Perf and
  soak tests keep their own commands below: the runner never runs them.
- **Before a merge:** `--before-merge` also runs the long tests (it sets `GAMEONE_LONG=1`, the one
  switch for all of them).
- **Playthroughs:** a test that flies a whole mission from the pad is a `[PlaythroughFact]`
  (`Playthrough.cs`), named as such, with `[Trait("Kind", "Playthrough")]`;
  `[PlaythroughFact(Long = true)]` is one over the limit, run only with `GAMEONE_LONG`. A long
  check beside a quicker one of the same rule (the same body, smaller) is a `[LongFact]`. Say what
  the quick one leaves out. Every long test also carries `[Trait("Run", "Long")]`, by which the
  runner counts it nothing in a normal run's balance; `TestRunTests` fails if either trait is
  missing or misplaced.
- **The limit:** no normal test should take longer than `Report.LimitSeconds` (7 s, in
  `tools/TestRun/Report.cs`, worked out from a parallel run's record); the runner lists the ones
  over it. A new test over the limit starts where it is about (see above), or is a playthrough.
  A test whose check is itself the slow part (a whole lap, an approach and a hold) may stay over
  the limit in the normal suite, named in the record (the user's choice, 2026-11-04). Today:
  the merged satellites orbit test (`WorldTests`, about 23 s), the 49-booster field
  (`TerrainTileTests`, 8 to 10 s), the target-frame rendezvous (`FrameFlightTests`, about
  10 s) and the transfer from a low orbit into the moon's sphere (`MoonTripTests`, about 15 s:
  some 400 s of flight that no start can shorten). Others go just over on a busy run: lying
  across the ice edge (`IceTests`, up to 9 s), coming home by the surface frame 300 m north
  (`FrameFlightTests`, up to 8 s), the capture into an orbit round the moon (`MoonTripTests`,
  up to 8 s: its start, just inside the moon's sphere, is some 150 s from the periapsis; the
  stack's, `LanderTripTests`, likewise up to 10 s), and the lander's landing from its orbit round
  the moon (`LanderTripTests`, 7 s alone, up to 9 s: some 155 s of flight, its gentle engine
  braking from high up). The
  fast collisions are under it: the 8,000 m/s head-on collision is flown 2.5 km up, clear of the
  ground's tiles.
- **The record:** `tests/GameOne.Tests/TestTimes.json` holds each machine's times (by hashed key),
  which balance the shards and show what got slower. Retake it on purpose, like
  `GAMEONE_PERF_REBASELINE`, after changing tests' times (many added, sped up or made long), from
  a committed tree and with the default process count: `dotnet run --project tools/TestRun --
  --before-merge --record`, then commit it. A normal `--record` keeps the long tests' old times. It only
  reports: a slower test never fails a run.
- **`Run=Alone`:** a test with `[Trait("Run", "Alone")]` is left out of the shards and run by
  itself afterwards, for one that can't share the machine: so far the map's collision search over a
  thousand vessels and the map's frame in that world (`CollisionSearchTests`), timed while the
  search's below-normal thread works, which beside the other shards waits on them; and the six
  boosters low and fast over rough ground (`TerrainTileTests`), whose one wait, in real time, is on
  the tile worker, below normal priority too; and the two crossings into and out of the moon's
  sphere in the physics (`CrossingTests`), whose crossing frame's time is weighed against its
  neighbours' in real time; and six worlds stepped at six bodies (`BodiesInPlayTests`), whose
  bound is on the process's private memory (Jolt's worlds are native), which the other shards move; and a burn past a node on a moon transfer at the top rate
  (`WarpBurnTests`), whose frames are timed in real time against the same burn's without the node.

## Soak tests

The soak tests (`GAMEONE_SOAK=1` with `--filter "FullyQualifiedName~SoakTests"`; see the README)
catch anything that grows over long play (new effects, sounds, bodies, per-frame HUD text) and
slows it down. They take about six minutes, so run them once, at the end of a change (before it is
merged), not after each step of it, and only for changes that could grow memory or what is kept
over play (new effects, sounds, bodies, caches, per-frame allocations, saved state). Data and
tuning changes (a map layout, a tunable) don't need one if a soak has passed since the last such
code change: say so rather than suggest one.

## The performance test (in a window)

For anything that could cost frame time (rendering, the views, the HUD), run the performance
test: `GAMEONE_PERF=1` with `--filter "FullyQualifiedName~PerformanceTests"` (seconds, in a
window; don't screenshot it while it times). Its very low pass over the moon
(`A_very_low_pass_over_the_moon_holds_60_fps`: the lander on `LowPass`'s orbit, flown with three
small views at normal speed and with the highest warp asked (timed at whatever the game leaves it,
printed), each frame timed whole, its steps and its drawing, with the
tiles' and the terrain patches' parts printed) is the moon feature's before-merge run: run it, as
the user's to start, with the soak tests before that feature is merged.

Each passes on its median frame (16.7 ms for a frame drawn, half that for the physics or a
path), so a spike doesn't fail it; the 95th and 99th percentiles and the worst frame are printed
beside it to keep track of. 60 fps is the target, kept even where it isn't met: on the user's
laptop (its security software busy, after a restart most of all) some fail on a slow run, main
as often as a branch, so compare a failure with main's on the same machine before calling it
a regression. The very low pass with the highest warp asked fails on any machine (a median of
about 23 ms: the warp's steps may take `Flight.WarpBudgetSeconds`, 12 ms, and the drawing
about 10 more), and that is accepted: the warp's budget stays as it is.

## The short performance tests (`ShortPerfTests`)

- **What they are:** four headless scenes, each loading a part of the physics (a hundred
  satellites coasting, the floe band with its floe bodies at their cap, boosters low over rough
  ground, five rockets launched together to orbit), timed part by part from `Timings` (and the
  tiles' and contact's pieces, `StepDetail`, under their parts) and compared with
  `PerfBaseline.json` (see the README), which holds five entries a configuration: the four scenes
  and the five rockets' stretch with all five boosters burning.
- **When:** only when working on performance: a change meant to make something faster, or one
  suspected of making it slower. Never as part of the normal suite (the user, 2026-11-02): they
  take about a minute and a half, and are skipped without `GAMEONE_PERF=1`:
  `GAMEONE_PERF=1 dotnet test GameOne.slnx --disable-build-servers -nodeReuse:false --filter
  "FullyQualifiedName~ShortPerfTests" --logger "console;verbosity=detailed"`.
- **The speed sweep and the paced checks:** three of them (`How_fast_a_craft_can_go_near_the_ground_before_a_frame_drops_under_60_fps`,
  `A_fast_pass_holds_60_fps` and `A_crowd_handed_in_and_out_reports_its_frame_rate`) are paced at real
  time and take a minute or so each; they are not baseline entries, so they are never compared
  with the baseline or written to it by a rebaseline (nor is `Fifty_boosters_landing_unwatched`,
  compared with the same scene in the physics, not with itself). The second fails if a frame's
  physics during a forced slow-down is over
  `ShortPerf.PlayableFrameMs` at the 95th percentile, or if more than one slowed frame of a case
  is over it, or that one is 1.5 times over (one a little over let pass for the OS's jitter; one
  further over is the scene's own cost, run after run; each over printed by case, frame, ms and
  where its time went): run it after a change to the slow-down, the
  predictions (`Collisions`, the near misses' included: it flies a near miss slowed by the setting
  too) or what the step does for a fast craft (the tiles, the floes, the wake).
  The third sets every frame beside the same rule while the flown vessel passes a thousand
  satellites, handed into the physics and out of it, with the map closed and open (its search's
  thread at work), and closed with near misses slowed for (the near misses' look over the crowd);
  it says whether each case holds it, never failing on its times (the user, 2026-11-10: this
  machine's load moves them; judge by the shares), and prints the worst frame and where its time
  went too: run it after a change
  to who is in the physics (`Unwatched`), the formula or its collisions, or the predictions
  (`Collisions`, the near misses' included). It flies 12 s untimed
  first, as the JIT's tier-up of the collisions' check (once a process) costs one frame 3 to 5 ms.
- **Where the field's slowed frames go (`FieldPerfTests`):** the fast pass's dive into the field
  of 49 boosters, its slowed frames' parts and pieces printed as multiples of Jolt's own step in
  the same frames, which hold whatever the machine's speed (the user, 2026-11-10: this machine's
  speed can't be relied on). Same switch, `--filter "FullyQualifiedName~FieldPerfTests"`, about
  half a minute. Run it before and after a change to what the slowed frames cost (who wakes, the
  vessels' checks, the contact), and judge by the multiples against their spread run to run.
- **Rebaselining, and the build:** add `GAMEONE_PERF_REBASELINE=1` to write this machine's
  numbers into the baseline, and `-c Release` for a Release build's (Debug's optimisations are off
  for the game's C# and BRUTAL's bindings, but not for Jolt's native step, so Debug overstates the
  game's own work beside Jolt's). A performance branch rebaselines and commits `PerfBaseline.json`
  once its change is done, before it is merged, so main's baseline is main's numbers.
- **The machine:** run them plugged in, on Windows' "Best performance" power mode. They hold
  themselves on the fast cores (`FastCores`: unthrottled, on the fastest cores' CPU sets, at High
  priority, all put back after), as a windowless test process is otherwise put on a hybrid CPU's
  efficiency cores for much of its run; each run says what share of its steps were on the fastest
  cores.
- **Reading them:** slower is reported ("worse"), never failed: the numbers move with the
  machine's load. A run compares only with its own machine's and build's entry (keyed by a hash,
  so the file names no machine; Debug and Release apart), and says how big a change its spreads
  (its own and the baseline's) let it see; this machine's load and power can't be controlled
  (the user, 2026-11-10), so a change shows only past 20% (`PerfBaseline.Tolerance`), a baseline
  may be committed with spreads up to that, and the paced checks' limit,
  `ShortPerf.PlayableFrameMs`, is 15 ms. Slower here is no concern: judge a change by the ratios. A slow machine slows every part alike, so
  each part's share of the step (printed beside its time) still holds when the times don't
  (the user, 2026-11-03): judge where the time goes by the shares.
