# To do

Ideas agreed for later, to discuss before building. (Done and gone from here, their decisions in the
git history: flying every part that comes away, with small views of them, merged 2026-10-21; the
persistent world, with the game menu, the keys in the vessel's own frame and retrograde hold from
its review, merged 2026-10-21; time warp, physics warp at 2 to 32x, merged 2026-10-21; launch smoke
that lingers, with smoke pouring across the pad, merged 2026-10-21; navigation (zooming out,
predicted paths, markers and targets, the marks and windows on the path, and manoeuvre nodes), with
landing on the bell and the legs' give, merged 2026-10-22; terrain (the generated planet, legs for
uneven ground and the test range, the sea and splashdown, the drone ship, and sea ice), merged 2026-10-28; flying from anywhere (a
landing as a stop: landed and floating vessels staying live, landed however they lie, mission steps
that stay done, throttling up to take off from anywhere, the launch with no countdown, staging
landed or afloat, and vessels colliding), merged 2026-10-28; launch sites
(seven round the planet, each with its own coloured tower, some with a landing zone or a drone ship,
picked in the mission menu, with quick tests at every site: a lift-off, and a booster down on its
ground), merged 2026-10-29; heights
from the sea and lit pads (every height from the sea, Altitude and Ground on the HUD, the far pads
and zones at the highest ground under them on foundations that step back into the slope, and every
pad, zone and drone ship lit: floor lights, floodlight masts where the ground allows, tower lights,
far glows and floodlight shadows), merged 2026-10-29; the solar system and a moon (the sun, the
planet and the moon as bodies on their paths, the moon's own ground, eclipses both ways, flying
there and landing, and the lander and its mission), merged 2026-11-16.)

- **Using Jolt fully** (the user, 2026-11-02, after BRUTAL v2026.10.1: every Jolt call now works;
  see CLAUDE.md's Jolt notes). The user hopes it also helps performance: measure each against
  main with the performance and flight tests. Done: the ground's tiles as Jolt's own height
  fields, made on a worker ahead of need (merged 2026-11-03). The rest, in this order,
  each to discuss before building:
  1. Ray, point and shape queries (`NarrowPhaseQuery` `CastRay`, `CollidePoint`, `CollideShape`,
     `CastShape`) where the game asks the ground or another vessel something; they see only the
     ground tiles laid, so `Planet.GroundAltitude` stays for the rest.
  2. Floes of real outlines, as convex hulls (see "Floe shapes" below); the booster's collider of
     its real shape too, if that helps.
  3. Built (collisions part 1):
     touchdowns, the tower, the masts and hits between vessels are judged from Jolt's contacts
     (`RocketPhysics.TouchesBegun`), what each craft touches is its live contacts, and
     `RocketPhysics.NearAnotherCraft` is gone. Part 2 is built too (2026-11-07: fast collisions
     predicted ahead, `Collisions`; the flown vessel's warning, `Flight.Warning`; and the forced
     slow-down, `Flight.Slowed`, which shortens Jolt's step so nothing passes through), and part 3
     (2026-11-08: vessels nobody watches moved by formula, `Unwatched`, with the same outcomes;
     the map's predicted collisions; the world log), and part 4 (2026-11-08: near misses slowed
     as a setting, "Slow motion for near misses", off by default; what is coming at a
     player-controlled vessel marked in red, and a small view's border flashing; then, tuned by
     watching, 2026-11-09: every slow-down shaped alike, from 1 km out to 10 m/s to the eye at
     the pass, and a fast craft's ground laid only round where it comes down): that design is
     built.
  4. Mass set directly (`SetMassPropertiesOverride`), once contacts are real.
  5. Mesh shapes, constraints (docking, stages held together), shape casts and `Character` when a
     feature needs them.

  What the measurements say now (the numbers themselves are in `PerfBaseline.json`, retaken at
  the end of collisions part 2; ms per flight step and each part's share, from the short perf
  tests). Read the
  shares, not the times, and Release, not Debug: Debug's optimisations are off for the game's C#
  and BRUTAL's bindings but not for Jolt's native step, so Debug inflates the game's own work
  beside Jolt's (Contact about 5x, Water 2.6x, Tiles 2x, Jolt 1-1.3x). In Release, Jolt's own
  step leads where there are few vessels (the five rockets) and is about level with the water
  over the floe band; the game's contact work led with a crowd. Before collisions part 1 (split by
  `StepDetail`, Release, 2026-11-03), a hundred satellites' contact share (45% of the step) was
  each vessel's own checks (`Vessel.CheckContact`) about 70% of it, what each craft stands on
  (`FindGrounded`) about 15%, finding movers and freezing the rest: it is the vessels' rules, not
  the nearness, that grow with the vessels. After it (3 built, Release, 2026-11-07), a vessel far
  from the ground and touching nothing measures its ground from the terrain alone
  (`Vessel.MeasureFar`) and skips the water's points: the hundred satellites' contact share is
  31% of the step, its vessels' own checks 11% (from 32%), what each stands on 9%, and the step
  is about a quarter shorter (0.74 ms against 0.98), so Jolt's step (35%) now leads there too. After
  part 2 (Release, 2026-11-07, means' shares against part 1's): predicting the fast collisions
  (`Rules: Collisions`) is the new cost, 7% of the five rockets' step (0.013 ms of 0.19; Rules 8%
  from 2%), 2% low over rough ground and 1% of a hundred satellites' (it is asked only of the
  witnessed vessels); the floes reached along a craft's way take the floe band's water from 38%
  to 40% (0.03 ms: with `Scene.FloeReachSeconds` 0 it is 38% again, so it is that reach's
  cost); the rest are as they were. The
  next gains are still "The step's cost for each vessel" (below) in the game's own code, or 1
  (queries) where it asks what Jolt already knows.
- **To try: the step's cost for each vessel, and the fixed cost of a world** (performance ideas,
  from the `StepDetail` split above, 2026-11-03, and the test-run-times work's slowest tests,
  2026-11-04, Debug, timed with `Timings`; one piece of work, as they share the idea and the
  risk). The costs that stand out:
  - A vessel far from anything still ran every contact check. Built in collisions part 1
    (2026-11-07): far from the ground (only the ground near, its middle clear by four times its
    reach) and touching nothing, `Vessel.CheckContact` reads one terrain height for its ground and
    none for the water, so the hundred satellites' vessels' own checks are 11% of the step, not
    30%. What is left of them (its orbit, its record, the collisions read) for vessels no one is
    watching: built in collisions part 3 (2026-11-08): such a vessel is out of the physics, moved
    by formula, and the hundred satellites' step is 0.20 ms against 0.77 (Release, the short
    tests against main: Jolt 53% of it, the formula 26%).
  - Awake boosters standing on the ground: 167 of them, woken together, cost 57 ms a step. 70% of
    that is the contact part, nearly all of it each vessel's own checks (`StepDetail.Vessels`,
    `Vessel.CheckContact`): about 0.17 ms per awake booster per step, in step with their number.
  - Water: 17% of the same step, though every one of them stands on land, far from the sea: the
    water's slices look worked out for crafts that can't be in it.
  - A nearly empty world: one to four satellites and the rocket on the pad cost 0.39 to 0.52 ms a
    step (Jolt about 40%, contact about 45%): the pad's rocket, foundations and masts are in every
    flight.

  Would gain: these steps cut directly, and with them the whole flight test suite, as every
  flight test pays them every step. The slowest normal tests (the frozen field, the satellites'
  lap, the rendezvous) are mostly these costs. Also the game's own step in a busy world (the
  floe band, many landed vessels near the player). Costs or risks: a check skipped that should
  have run (a touchdown, a hit or a splashdown missed): the margins must cover a step's travel at
  the top speed, a vessel come near between checks must still be caught, and water skipped where
  no part can reach the sea must still catch a craft coming down onto it; the contact and
  splashdown tests must all still pass. Jolt's contacts replaced most of these checks in part 1
  of the collisions work (built 2026-11-07), and part 3
  (built 2026-11-08) takes unwitnessed vessels out of the physics; what is left is what the
  witnessed cost (the water slices, the world's fixed cost, awake boosters standing on the
  ground near the player), unchanged by part 3. To discuss before building.
- **Looked at and set aside: the planet as a BRUTAL mesh** (the user, 2026-11-03). BRUTAL's
  `HelloRenderMesh` sample (upstream, 7a3b72d) shows `Brutal.Render.Mesh`'s `ProcGenMesh`
  making a sphere: a smooth latitude-longitude grid on the CPU, with no heights and no level of
  detail. The planet is drawn from a GPU quadtree of patches whose heights the vertex shader
  works out from the map, culled and within a budget, so a CPU mesh would cost more to draw, not
  less; one fine enough to stand on (the tiles' 0.5 m over a 10 km planet) would be billions of
  vertices. For collision, Jolt's mesh shapes now work, but a height field is Jolt's own shape for
  terrain (smaller, quicker to collide with); a mesh tile would take longer to make than a height
  field and still need its heights from the terrain. Meshes belong to the floes, the booster and
  later parts ("Using Jolt fully", 2 and 5), not the ground.
- **Jolt's worker pool sized to the work** (a performance idea, from the short tests' fast-cores
  work, 2026-11-02). `RocketPhysics` starts `ProcessorCount - 1` Jolt workers (21 here), far more
  than these scenes' few bodies keep busy, and more than the 12 fast logical cores the short
  tests hold the process to. Even with the main thread on the fast cores in every step, the
  five rockets' Release runs still came in at 0.15 to 0.20 ms a step by invocation (its Jolt
  part 0.10 to 0.15, most of the difference): where the workers wake may be why. Would gain:
  steadier, perhaps faster, Jolt steps in small scenes. Costs or risks: a pool too small for a
  big scene (the hundred satellites, the floe band at its cap) is slower; to be measured with the
  short tests in both configurations. To discuss before building.
- **To try: the live contacts indexed by body** (a performance idea, from the final review of
  collisions part 1, 2026-11-07). `RocketPhysics.ForgetContacts` walks every live contact each
  time the game takes a body out (a craft, a tile, a floe's hull) to find that body's own. Would
  gain: a removal's cost in step with that body's contacts, not the whole world's; it would show
  in a big pile, such as `FrameTests`' 999 landed boosters woken together (over 18,000 contact
  events in a step), with tiles coming and going round them, though it is unmeasured (its time
  is within the tiles' removals and `StepDetail.Touches`). Costs or risks: a second map to keep
  in step with the live contacts at every contact's beginning and end (each listed under both its
  bodies), a little more work for each contact event in every step. To discuss before building.
- **Fast crafts' ground: questions left open** (from the tiles laid only where a fast craft comes
  down, `Craft.GroundAhead`).
  - **Tried and set aside:** a 3 s tile lookahead (`Scene.TerrainTileLookahead`), which asked for
    ground under paths that never come near it and made as many in the step; starting the
    slowing further out by the size of what is hit, which by the planet's radius held ordinary
    falls in slow motion for a minute or more; and asking the worker for the whole of a fast
    craft's way near the ground, as a slower craft's is, about 900 tiles for a shallow dive.
  - **Why is there a cap on the world's tiles?** `Scene.MaxTerrainTiles` holds 1,024 at once, a
    share of the physics' 4,096 bodies (`RocketPhysics.MaxBodies`) beside the world's 1,000
    vessels. To check what it is really for (the bodies' budget, memory, or the step's cost).
  - To test more once there are more bodies (moons, some airless, and larger bodies), against the
    speed sweep (`How_fast_a_craft_can_go_near_the_ground_before_a_frame_drops_under_60_fps`) and
    the paced fast-pass check.
- **To try: each craft's urgent cells cached** (a performance idea, from the final review of the
  tiles made off the step, 2026-11-03). `TerrainTiles.Update` works out the cells a craft could
  reach within `Scene.TerrainTileUrgentSeconds` afresh every Jolt step while it is urgent (about a
  hundred grid points, each a `CellOf`), where the whole stretch's cells are cached until either
  end has moved `SeekSlack` (`Craft.TileCells`). The likeliest cause of the floe band's Tiles part
  growing about 8 µs a step when the tiles went off the step, though not measured on its own.
  Would gain: about 8 µs a step over the floe band (a few per cent of its step), more with many
  crafts low and awake. Costs or risks: a second cache per craft to keep right (it must still
  cover the quarter second's travel as the craft moves within its slack, so it is worked out a
  little wider, as the stretch's is); a cell missed there is a craft falling through, so the
  crash theory and its per-frame tile check must still pass. To discuss before building.
- **To try: the tiles' lookahead by the game's clock** (a performance idea, from collisions part
  2's paced fast collisions, 2026-11-07). A craft's tiles reach a second's travel ahead
  (`Scene.TerrainTileLookahead`) in game time; slowed for a fast collision, its stretch is as many
  metres long, walked every step (the craft moves a step's `Scene.FastCollisionStep`, about
  `SeekSlack`, each), and the steps a real second are unchanged, so the tiles cost as much a real
  second slowed as at full speed. The urgent quarter second's (`Scene.TerrainTileUrgentSeconds`)
  is built that way already (in the final fixes, 2026-11-07: in full-length steps, shortened with
  the step), as its burst broke the frame rate at the shipped top speed: as a slowed dive turned
  urgent, its whole quarter second of urgent cells (a kilometre at 4,000 m/s) was made in the step
  at once. Measured (Release, paced, a 4,000 m/s dive at the rough slope at 10°, slowed, worst
  frame): 8.7, 9.3, 12.5 and 22.8 ms (42 tiles made in one step) before; with a wreck given no
  new tiles too (its hit throws it along a new way, which wanted a whole urgent stretch), 5.7,
  2.7, 2.7, 3.8 and 5.9 ms, then 3.9, 3.3 and 3.6 ms with the urgent stretch in steps (10 tiles
  made in the step over the whole dive, against 37 to 72). At a top speed of 8,000 m/s, the
  worst was 109 ms before either (as the table under "One speed range" has it), not measured
  since. Would gain: most of the walking, by the lookahead's length in the real time the worker
  has. Costs or risks: as the slow-down eases back (1 s real) the stretch grows twentyfold, so
  the worker must catch up or the cells turn urgent and are made in the step; a cell missed is a
  craft falling through, so the crash theory, the sweep and the paced check must still pass.
  Only worth it for a higher top speed (at 4,000 m/s the frames hold). To discuss before building.
- **To try: a cap on the kept shapes laid in one update** (a performance idea, from the final
  review of the tiles made off the step, 2026-11-03). A wanted cell with a shape already made
  (the worker's or a kept one) is laid at once, however many there are: each is only a body made
  and added, but a craft coming back over fifty or more kept cells lays them all in one step, a
  spike in that step. A small cap on those that aren't urgent (the urgent ones laid whatever the
  cap) would spread them over a few steps. Would gain: a smoother step when a craft comes back
  over ground it has been over (a booster flying back over its own launch, a fleet woken
  together). Costs or risks: a tile laid a few steps later than it could be, which nothing needs
  sooner (the urgent ones are never held back); one more number to tune. To discuss before
  building.
- **The tiles' worker skipping cells it can't finish in time** (a design question for the user,
  from the final review of the tiles made off the step, 2026-11-03). When the worker lags (time
  warp, a fast craft, a slow machine), it still works soonest first, so it spends its time on
  cells that turn urgent (made in the step anyway) before it finishes them, and their late copies
  take kept slots from useful shapes. One way: the step hands it a cut-off with each round of wants
  (now, plus the urgent time, plus the worker's recent time to make a tile, in physics seconds,
  which grows with warp), and it skips cells due before it, making those it can still finish
  first. Would gain: the worker's time spent on tiles that are laid from its shapes, so less made
  in the step when it lags. Costs or risks: it changes the worker's "soonest first" (the soonest the
  worker can still be in time for, rather than the soonest); a cut-off misjudged leaves the worker
  idle or no better. To discuss before building: it is the user's call whether the order should
  change.
- **A gentle touch between two vessels under formula** (a design question for the user, from
  collisions part 3, 2026-11-08). Two under formula that touch slower than `ImpactTolerance`
  pass through each other (nothing changes: a bump's afterwards is allowed to differ from
  Jolt's), so a booster settling onto another's top with nobody near falls through it to the
  ground, where in the physics it would rest on it. Rare (both unwatched, landing on each other).
  Ways: hand such a pair back to the physics to settle (a body each until they rest), or set the
  one coming down on the other's top by formula as it is set on the ground. To discuss before
  building: the user's call whether it matters.
- **The formula's bodies: capsules, or flat-ended as the physics' are?** (a design question for
  the user, from collisions part 3, 2026-11-08). Each body under formula is "an axis as wide
  as itself", so its ends are rounded (a capsule) where Jolt's cylinders are flat: a
  nose hitting another's end at a corner can touch up to a width sooner or later than in the
  physics, and so be judged a step apart (the same outcome either way, both wrecked or neither).
  Should they be made flat-ended? It would bring the touch's time and place nearer Jolt's at the
  corners; a cylinder's test costs more for each close pair (few), and the face-on rule (a
  nose's axis) must still hold. To discuss before building.
- **The map's collisions from the pad, or not zoomed out?** (a design question for the user, from
  collisions part 3, 2026-11-08). The predicted collisions between vessels are drawn with the path
  marks (they are tied to those), so only while the flown vessel is flying and the map is
  zoomed out (`Paths.Draw` returns early otherwise): a player on the pad, or landed, sees none,
  though two satellites overhead may be about to collide. Should they show there too (and how,
  with no path of the flown vessel's drawn)? To discuss before building.
- **To try: the map's paths of the vessels in the physics kept between frames** (a performance
  idea, from the solar system part 3, 2026-11-13). Zoomed out, each vessel in the physics but the
  flown one (the witnessed, and what is by them) has its path patched at the spheres of influence
  every frame (`SystemMap.PlacePatched`), as they are few; those under formula keep their laps
  until their orbits change. Would gain: a crowd handed into the physics by a small view (tens of
  vessels) costs no patching a frame on the map. Costs or risks: a patched path is placed among
  each leg's stars at its own time, so keeping it means placing kept legs as the frames turn on,
  and knowing when one in the physics has changed its orbit (its float noise every step). Measured
  first: in Release, with a thousand satellites under formula, the map takes about 0.6 ms a frame
  at the 16 km zoom and 0.1 ms all the way out. To discuss before building.
- **To try: a plunge into shallow water by formula** (a performance idea, from collisions part
  3, 2026-11-08). A vessel under formula coming down into water shallower than its plunge's
  reach (`Formula.PlungeReach`, 1.5, times its draft and feet) is handed back to the physics at
  the surface, as a 1-D plunge with the formula's drag missed Jolt's (feet on the seabed at
  10 m): it costs a body, its tiles and the water's slices until it settles and freezes, though
  nobody watches it. And `PlungeReach` was tuned on the booster alone. Would gain: no body for
  it at all. Costs or risks: the plunge must end as Jolt's does (afloat, or landed or wrecked on
  the seabed) for every vessel's draft; until then the hand-back is the safe outcome. To discuss
  before building.
- **To try: the formula's ground read low and fast** (a performance idea, from collisions part
  3, 2026-11-08). Under formula, a vessel low over the ground or the sea reads what is under it
  along each step at points no more than 8 m apart (the smallest floe's width), each of its
  feet, bell and hull's points: measured (Release, a booster lying 30 m over the open sea or the
  floe band) 21 to 34 µs a formula step at 1,000 m/s, 6 to 9 µs at 50 m/s, 1 µs 2 km up. Most
  of it is the terrain's height and fine detail at each point (asking only the terrain where
  nothing else is near, as `Planet.OnlyGroundNear` lets the physics' hull, saved nothing
  measurable). Would gain: such vessels cheaper (few: one skimming fast and low with nobody near).
  Costs or risks: skipping the detail over deep water needs a bound on its height (none kept);
  a sample further apart steps over a floe or a ridge. To discuss before building.
- **To try: the JIT's tier-up in a big world** (a performance idea, from collisions part 3's
  paced crowd, 2026-11-08). With a thousand vessels under formula, the frame in which the JIT
  swaps in its faster code for the collisions under formula (checked four times a game second,
  so some 8 s into a process) costs 3 to 5 ms of its own, once a process (still under the frame
  budget; the paced crowd check warms past it). Not so the first steps after loading a
  thousand-vessel world, the first load of a session: 23 to 43 ms each (Release, the crowd's
  world, measured in the final fix round), over the frame's budget. The handovers cost nothing
  there now (the far ones load under formula with no body); what is left is the new rocket's pad
  laying its first tiles (15 to 33 ms, made in the step, in any world's first step) and
  `Formula.Advance` over 999 coasts in the JIT's first, unoptimised code (10 to 20 ms a step for
  the first few steps; a later load in the session runs the optimised). Two things to try:
  warming the formula's hot path up and laying the pad's urgent tiles during the load itself,
  under the menu (as the loading already is); and full optimisation for the formula's hot path
  (`[MethodImpl(MethodImplOptions.AggressiveOptimization)]`) or ReadyToRun. Would gain: those
  first frames, and the one tier-up frame. Costs or risks: a longer load; start-up time and the
  build's size for ReadyToRun; aggressive optimisation gives up the tiering's profile-guided code
  for those methods. To measure first. To discuss before building.
- **The map's merged crosses and their labels** (a design question for the user, from collisions
  part 3's look at the running game, 2026-11-08). Where collisions stack on the screen (a thousand
  satellites' polar copies all coming over the pole together), one cross is drawn, the soonest's,
  named by its two alone, while every merged collision's lead-ins are still drawn (a fan of red
  lines into the one cross), so the label says nothing of the others (it could say "and 19
  more"). And a cross's label is drawn over the path's own marks (Ap, Pe) where they fall
  together, neither giving way. To discuss before building.
- **A transfer window to a vessel or site at another body** (a future feature, from the solar
  system's Part 3, 2026-11-13): today a vessel or a place targeted at another body than the one
  the flown vessel is at (a lander on the moon, from a planet orbit) is located and measured across
  the bodies (the distance, the speed relative to it, the Target frame), and a flying one's path
  is drawn round its own body, but it has no transfer or de-orbit window, and a closest approach
  only on a leg of the path round its body. A window there would be a burn now that comes into
  its body's sphere and then to it (or down by it): a transfer onto another body's orbit, which
  `Encounters.Transfer` doesn't do. To discuss before building.
- **More in the world log** (a future feature, from collisions part 3's design, 2026-11-07): the
  log has only losses (and those removed to make room); launches, landings and the like could
  be entries too, as could fairing halves (left out as debris that would flood its 300). To
  discuss before building.
- **Vessels left on the pads, flown when the player likes** (the user, 2026-11-02). Today a new
  flight clears its pad by itself (`Flight.StartFlight`): it removes every vessel still Ready in
  the world, and anything within the pad and 3 m round it (up to 58 m high), before putting the
  fresh rocket there. Instead, nothing is removed without asking: if something is in the way on
  the pad the player is starting from, the game says so and asks before removing it. That lets
  the player prepare several vessels on the pads (Home's and the far sites') and fly any of them
  later by switching to it, rather than each new flight sweeping the last one away. To discuss
  before building: what "in the way" asks about (a Ready rocket, a landed booster, debris), what
  the choices are (remove it, start from another pad, cancel), whether a Ready vessel left on a
  pad can be launched once switched to (today only the flight's own fresh rocket launches; a
  landed one lights from the throttle), and how it meets the missions (which vessel a mission
  counts).
- **Camera modes** (the user, 2026-11-09, watching the near-miss test). Today the camera has one
  mode: it follows the flown vessel from behind and never turns on its own. The game needs more,
  picked by the player: one tracking the target (turning to keep the targeted vessel in the
  picture with the flown one, so a vessel coming and going can be watched); one lined up with the
  solar system (the stars and planets still, the vessel turning in it); one lined up with the
  planet's horizon (level with the ground below, as today's); possibly others. The watched tests'
  viewer tracks what is coming at the flown vessel itself for now (`Watcher.Track`, test code
  only); once the game has a tracking mode, the viewer switches to that. To discuss before
  building: how each frames the view, what the mouse does in each, how a mode is picked (a key is
  for flying; a camera choice may be either), and how it hands back.
- **The camera under the water** (from the user's try-out of the sea, 2026-10-27). The camera can
  go under the water, to see what part of a vessel is under it: brighter near the surface, darker
  the deeper the camera goes, as in real life. To discuss before building.
- **Light from under the water** (from the landing lights' work, 2026-11-11: the user's call to
  keep the light where it is). A vessel afloat at night has its landing lights at its engine
  bell, under the sea, where they light nothing that is seen, and its far glow is hidden by the
  water. Light from under the surface should glow up through it and light the water from below.
  To discuss before building.
- **A bigger splash** (from the user's try-out of the sea, 2026-10-27). The splash thicker and
  bigger. To discuss before building.
- **Floe shapes, and the floe band from afar** (from the terrain work). Floes are round discs (the
  only shape Jolt could be given until BRUTAL v2026.10.1; now convex hulls can be, see "Using Jolt
  fully") and about half are smaller than a booster's leg spread; and
  from 2 km out the floe band is a single tint, slightly uneven where the floes cluster. To
  discuss before building.
- **Terrain detail where it can be seen.** A quality level of detail for the terrain, in place of
  the terrain work's simple one (by distance from the camera): detail that can't be seen isn't
  drawn, but it isn't simply by distance either, as horizons must keep a smooth detail, so that
  mountain tops forming the skyline don't pop in and out of detail. Also: for now the fine
  detail fades out with distance (so it never shimmers), so far from the camera the drawn ground
  departs from the collider (about 0.7 m at 500 m, 2 m at 1 km), and a vessel resting there on
  rough ground can look a little sunk or afloat. To discuss before building.
- **Weather.** A weather system making wind, clouds, rain, lightning and snow, and climates such
  as deserts, behaving differently at different altitudes. With it, currents and wind that drift
  what floats: for now the waves only rock a floating vessel to and fro, so it stays about where
  it came down. And the weather's part in the sky's colours: the air's haze is one thin layer the
  same everywhere for now (`Scene.MieScatter`, `Air`), so dust over the desert, haze over the sea
  or a hazy day would be the weather's, varying it by place and time. To discuss before building.
- **Day and night: left out of it.** Designed in five parts (the user, 2026-10-29), all built (the
  planet's spin and the frames of reference, the sun and the clocks, the sky from the air's
  scattering, the pad lights at each place's dusk and dawn, the vessels' landing lights: see "The
  planet" in `README.md`; the user decided the sky round a low sun stays pale and the twilight
  horizon grey-olive, as single scattering on this small planet gives, and Highcrest keeps its
  stars by day). Not part of it: axial tilt and seasons (this planet has none; later worlds may),
  the moon's light and eclipses, weather's part in the sky's colours, and picking the time of day
  for a flight (it starts at whatever time the world has reached). The clocks and lights stay
  information, never a cue ("sunset in 30 s" or a light saying "land here" are out). Left to
  discuss (from the landing lights' review, 2026-11-11): a small view at the same lit place as the
  main view shares its light set, so its own vessel can miss a ground light when four other lit
  vessels are nearer the main view (always including each view's own vessel, say).
- **To try: the sky and haze's cost** (from Part 3's performance work, 2026-11-02). The air's light
  is marched per pixel (`Shaders.cs`: `airMarch`, `skyColor`, `fogTerms`; the step counts in
  `Scene`). It costs most where the main view sees much sky and far ground or sea: before Part 3
  against after, the median frame (main view and three small ones) at Home's noon with the main view
  at Highcrest went from about 5.4 ms to 6.0-6.7 ms (+11% to +29% over runs), and over the floe
  band from 9.1 to 10.4-10.6 ms (+13% to +15%), the rest within 15% (accepted, as every frame-time bound passes and this machine's runs
  vary by about 20%). Measured
  at Highcrest's noon, the sky is about 0.5 ms of that and the haze about 0.25 ms; over the floe
  band, with no air at all it ran 8.3 ms. Ideas not built, to try:
  - A sky-view lookup table per view, rendered once a frame (Hillaire 2020: about 200x100 texels
    over the directions round the camera, spaced finer near the horizon and the sun), in place of
    the per-pixel sky march; the sky in the water would read it too. Gain: most of the sky's 0.5 ms
    at Highcrest's noon, and the water's reflection over the floe band. Cost: a pass and a texture
    per view, and its descriptor. Risk: the sun's glow and the twilight's shadow edge need the
    spacing near them, or they blur.
  - An aerial-perspective volume per view (froxels: about 32x32x32 over the view's frustum, the
    haze's light and dimming at each), read once per fragment in place of the haze's march. Gain:
    most of the haze's 0.25 ms at Highcrest, more over the floe band, where the sea fills the view.
    Cost: a compute pass and a 3D texture per view. Risk: the depth slices must reach from by the
    camera out to a view from orbit, and a thin slice count shows as steps in the fog.
  - Five sky steps, not six (`Scene.SkySteps`). Gain: about a sixth of the sky's cost (about
    0.08 ms at Highcrest). Risk: more grain; six measured 8% at worst in one pixel (1% on
    average), five would be somewhat worse.
  - The sky at half resolution, scaled up. Gain: about three quarters of the sky's cost. Risk: a
    soft or haloed edge where the ground and vessels meet the sky.
  - The water's reflection from the sky's light table (`airSkyLight`) where Fresnel holds it to a
    few percent, the march only for grazing views. Gain: small over the floe band, as most of the
    sea seen from a low camera is at grazing angles. Risk: a faint edge where it switches.
  - Haze per vertex for the terrain, as the smoke has it. Gain: doubtful, as the main view's
    patches have nearly as many vertices as it has pixels. Risk: seams where far ground meets the
    sky.
  - Tried, no measurable gain: a longer step for the haze (`Scene.AirStepLength` 700 m, not 500).
- **To try: the moon's shadow march ending sooner** (a performance idea, from the moon's
  drawing, 2026-11-11). The march towards the sun over the moon's map (`MoonGround.SunSeenOver`
  and its GLSL twin `sunSeenOver`, up to `Scene.MoonShadowSteps`, 20 reads a pixel) stops once
  the ray is above the whole map's highest ground (`MOON_HIGHEST`, the far side's summit, some
  820 m), which over the plains near the sun's horizon is most of the way. Ending it once the
  ray is over the highest ground of the coarse cell it is in (a max mip of the heights, a new
  texture and binding, and the same in the C# twin) would cut the moon pixel's reads where the
  sun is low over low ground, the costliest case. Costs a texture, a read a step of the coarse
  map, and keeping the twins alike; gains only at a low sun, and the moon pixel was not
  measurably costly in the user's GPU timings, so it waits for a case that needs it.
- **To try: the flight's step allocates** (from the timings work's five-rocket scene,
  2026-11-02). The game's own flight step allocates about 208 B a step (measured with five rockets
  flying to orbit: the test harness allocates nothing between steps, so it is all inside
  `Flight.Update`); no gen-0 collection happened in the timed stretch, so it costs little now. To
  find (an allocation profile of one step, or `GC.GetAllocatedBytesForCurrentThread` round each
  part) and remove. Found since (collisions part 2, 2026-11-07): two boosters coasting in the
  vacuum, touching nothing, allocate 48 B a step, all of it in `RocketPhysics.Update` (the rules,
  the legs and the looking ahead for collisions allocate nothing). Found again (the solar
  system's Part 2): a new world with only the rocket on Home's pad, frame by frame
  (`Simulation.Step`), allocates about 6.2 KB in 120 frames, about 52 B a frame; the look ahead,
  `Unwatched`'s advance and update, the place lights, the rocket's `AfterPhysics` and landing
  lights and the mission's progress allocate nothing, and `RocketPhysics.Update` alone, called
  120 times, allocates about 8.1 KB (`GC.GetAllocatedBytesForCurrentThread` round each). It is
  per world stepped: with a physics at each body in use (`Worlds`), each body's `Update` allocates
  its own, so a frame with several bodies' physics running allocates that many times over (the
  solar system's Part 3: 48 B a frame with the planet's alone, 96 B with the planet's and the
  moon's, `CrossingTests`' allocation test, which counts each body's step apart or compares
  frames stepping the same worlds). Gain: fewer gen-0 collections over long play, and steadier frame times with
  many vessels (it may grow with them). Cost: small once found. Risk: none to the rules, if each
  fix keeps the same numbers (the flight tests check it).
- **The satellite caught into orbit round the moon** (a mission idea, from the solar system,
  2026-11-10; to discuss before building). The satellite launcher's satellite caught into orbit
  round the moon, its lowest and highest points both 1 to 2 km above its surface, scored on its
  fuel left; its booster landing on the drone ship and its upper stage coming back to land as a
  bonus.
- **To try: the crossing search in `Patched.Legs` kept or shared** (a performance idea, from the
  solar system's Part 3, 2026-11-13). On any path that crosses a sphere (a transfer) the search
  costs 0.2 to 0.27 ms a call (p99 0.6 to 0.7 ms, a worst of 8 ms), against about 3 us on the
  planet with no crossing. The flown vessel on a transfer asks it four or five times a frame (the
  map's patched path, the looking ahead at warp, `Vessel.Planned`, the manoeuvre plan, and a
  targeted vessel once more): about 1 ms a frame in Release and 3.5 ms in Debug. Ideas: keep a
  leg's crossing while its orbit is unchanged (with its engine off: the same orbit, the same
  crossing time), or patch each vessel's path once a frame and share it between those. Beside
  it, a line: the per-point `Bodies.Change` and `LegPlace.Point` (about 0.26 ms a frame at 260
  points in Release, 1.6 ms in Debug), where the moon's place could be worked out once per point
  time and reused across views. Gain: about 1 ms a frame in Release on a transfer, more with
  vessels on transfers (10 of them 1.9 ms). Cost: small for the kept crossing (when the orbit
  changes is what to get right); a shared path is a wider change. Risk: a kept crossing going
  stale if anything changes the orbit that the key misses (the crossing tests check it). Helps:
  any flight to the moon, and a map full of vessels on transfers.
- **Sunlight judged by the horizon seen** (a design question for the user, from Part 3's
  screenshots, 2026-11-13, to discuss before building). The dark (the landing and pad lights)
  goes by the ideal flat horizon: landed on the moon at 88 degrees N, near the pole, the sun was 8.9 degrees
  under the horizontal, so the lights were on, yet in free look the sun stood just over the
  moon's visible horizon, several degrees lower than the ideal one on a 2.5 km moon with that
  terrain. The user would like it judged by the horizon seen, later.
- **The local time near a pole** (a design question for the user, from Part 3's screenshots,
  2026-11-13, to discuss before building). The local time is by longitude, so it jumps near a
  pole (on the moon it went 18:44 to 08:42 in 5 s on a polar pass, and 07:38, 05:25, 18:31 on the
  way down at 88 degrees N). Kept for now by the user. Perhaps by the sun's
  height there instead.
- **Part 3's loose ends in the look of it** (design questions for the user, from the moon trip's
  screenshots and Part 3's reports, 2026-11-13, to discuss before building):
  - The map turning with the ground once a day.
  - The sun's glint.
  - The dark disc (the moon's shadow).
  - The grey block (sea ice).
  - The wording of "NO AIR", "Period" and "Apoapsis leaves its pull".
- **Drawing a list of bodies** (a future feature, from the solar system's Part 3, 2026-11-13).
  The renderer draws the planet and the moon by name; more bodies need them drawn from
  `Bodies.All`. So does what an eclipse draws (the sun's dimming, the corona and the lens flare,
  `SolarSystem.SunSeen` and the GLSL's twin, which know the moon alone), while
  the dark it brings (the lights, `Sun.Seen`) already goes by every body.
- **Extreme terrain, and two more planets.** An extension of the terrain work: features too big
  for an ordinary world, such as volcanoes, mountains so high they reach above a body's upper
  atmosphere, and deep pits far into its crust. They may not suit the current planet; they are for
  giving new bodies in this solar system each a character of their own. Two more planets, to give
  enough of that. To discuss before building.
- **A rover, driven out of the lander's bay** (a future feature, from the solar system's Part 4,
  2026-11-15). The lander's bay is sized for one (about 2 x 1.5 m, its 300 kg budgeted in the
  lander's mass, `Scene.RoverAllowance`), and a rover would drive out of it, down the bay doors'
  ramps. Driving is a different kind of control from flying, so it needs discussing with the user
  first (see "One set of controls" in `CLAUDE.md`): to discuss before building.
- **Asteroids, and asteroid showers.** The detail to be worked out: to discuss before building.
  One thing decided (the user, 2026-11-08): an asteroid coming to hit the flown vessel is
  marked as the vessels coming are (collisions part 4's coming mark, showing where it comes from),
  unlike the ground, the towers and the masts, which are never marked.
- **A binary star.** The system's centre as two stars, with the planets orbiting the pair. To
  discuss before building.
- **BRUTAL features roadmap** (from the framework feature list): logging, and ImPlot for flight
  graphs, first.
- **Autopilots for vessels** (the user, 2026-10-30). Something like an autopilot, for the complex
  cases where hand-flying part of a vessel isn't practical (a stage coming home while the player
  flies the payload to orbit, say): a particular autopilot can be picked for a vessel in some
  cases (ascent, coming home, rendezvous, a hop, as the test pilots fly them now), so the player
  can concentrate on just one vessel. What it looks like is still open. To discuss before
  building: which autopilots, when each can be picked, how it is picked and shown, what it may
  use (the same frames, holds and readings the player has, as the test pilots do), how it
  hands back, and how it sits with "low hand-holding" (it flies for the player only when asked,
  and never tells the player what to do). Needs nothing first; "Calling up another vessel"
  needs it.
- **Calling up another vessel** (the user, 2026-10-30). An extension of the autopilots (above):
  the player asks for another vessel to be launched and to rendezvous with theirs, flown there by
  its autopilot, for themed play such as refuelling a vessel in orbit without hand-flying the
  tanker to it. Needs the autopilots and fuel lines (below). To discuss before building.
- **Fuel lines** (the user, 2026-10-30). Every tank has a fuel port built in, and two tanks near
  enough to each other can have a fuel line connected, so fuel flows from one to the other:
  tanks in the same vessel, or in two vessels (refuelling in orbit, above). Also for drop tanks:
  tanks used only for fuel feed the rest, which stay topped up until the drop tanks are empty and
  decoupled, so the thrust-to-weight ratio rises and the tanks kept still hold most of their
  fuel. Needs vessels made of parts (below). To discuss before building.
- **Parachutes** (the user, 2026-10-30). Building on fuel lines: decoupled empty tanks can carry
  parachutes to try to save them as they fall; and any vessel part can use one to let the air
  slow its descent. They can be repacked and used again, and each has a maximum safe drag beyond
  which it fails. Needs vessels made of parts (a parachute is a part) and fuel lines (above). To
  discuss before building.
- **A crew pod** (the user, 2026-10-30). A pod or command module that carries beings into space,
  with a heat shield for a safe re-entry from orbit, and parachutes. Its shape, the shield's weight
  and where its weight sits are low enough that its bottom naturally points down on re-entry.
  Needs vessels made of parts and parachutes (above); its heat shield is worth designing with
  "Re-entry heat" (below), which gives it something to shield against. To discuss before
  building.
- **A crewed vessel from those parts** (the user, 2026-10-30). A new vessel type using the pod,
  parachutes and tanks on fuel lines that drops its used tanks. A test flies it through: orbits
  the planet at least once, de-orbits, and touches down near where it was launched. After the
  pod, parachutes and fuel lines (above), so the end of the chain that starts with vessels made
  of parts (below): parts, then fuel lines, then parachutes, then the pod, then this. To discuss
  before building.
- **Back from the moon: a return mission** (a future feature, the user, 2026-11-16; the moon
  trip is one way for now). Most likely a new mission: a crewed vessel (above) goes to the moon,
  lands or orbits it, and comes home, its pod re-entering behind its heat shield and coming down
  under its parachutes. After the crewed vessel and the chain before it (the pod, parachutes,
  fuel lines, vessels made of parts), and after re-entry heat (below), which the shield is there
  to meet. To discuss before building.
- **Re-entry heat and plasma trails** (the user, 2026-10-30). Every vessel and piece of debris
  travelling fast enough through the air heats up and leaves a plasma trail across the sky, all
  the more special seen from the ground, looking up. To discuss before building.
- **RCS thrusters.** Small thrusters round a vessel for fine control in space: moving it every
  way (forward and back, up and down, side to side) as well as turning it, without the main
  engine. The controls need discussing, as there are many kinds of movement to give keys to
  (and CLAUDE.md's one set of controls applies). Needed for docking (below), so before it. To
  discuss before building.
- **Docking: joining two vessels into one.** After RCS thrusters (above), which it needs (vessels
  collide already). Docking rings, in two kinds, which join two vessels into a single vessel:
  - Magnetic: the two rings attach once they come near each other.
  - Extending: the port reaches out from the vessel, so two vessels that couldn't otherwise come
    so close (in each other's way) can still dock. Each vessel extends its port, and the ends then
    behave as the magnetic kind, attaching once near each other.
  - The two kinds work together, as long as both ports are the same size.
  - A mission: docking with a vessel already in orbit.
  - To discuss before building.
- **A mission to build a vessel in orbit, and fly it to the moon.** After docking and the moon
  (above), which it needs. Two launches, one flown in the main view and the other in a small
  view, each carrying a payload that is part of a new vessel: one the top (a fuel tank, say), the
  other the bottom (an engine with a small fuel tank and landing legs). The two are docked
  together in orbit into the new vessel, which then flies to the moon's orbit. To discuss before
  building.
- **The star box flickers in space.** The stars seem to flicker once up in space: to look into.
  Then to discuss: what the sky out there could have, such as galactic features (a band like the
  Milky Way, nebulae) or star patterns (constellations to steer by).
- **Smoke and explosions in a vacuum** (the user, 2026-11-10, watching the collision tests at 5 km
  up). Above the air, an explosion's smoke and flames still rise as if buoyant and seem pulled by
  gravity, as they would in air. With no air there is nothing to lift them or hold them back: a
  blast there should spread out evenly from where it was, carried on at the wreck's own velocity,
  and fade fast, with no smoke column. Ask for each effect which way is up and how thick the air is
  where it is (`Planet.AboveAir`, `Air`), as the engines' plumes may need too. To discuss before
  building: how a blast in thin air between the two should look.
- **Shaking: earthquakes, raft shakes and shockwaves** (the user, 2026-11-10). One system that
  shakes what vessels stand on: the ground in an earthquake, a raft (the drone ship's deck, a
  floe) in a heavy sea or a knock, and the ground round a large collision on the planet (its
  shockwave). A shake wakes the vessels standing on what shakes, and can tip them over. Until it
  is built, a raft's own movement is part of being landed on it: it carries what is frozen on it
  and wakes nothing (only something on a course to hit them does). To discuss before building:
  how strong a shake is and how far it reaches, what sets one off, and how it is shown and heard.
- **The square edge in the pad's ground smoke.** A faint straight edge shows beside the rocket on
  the pad, in the drifting ground smoke (it was there before the trails). Probably a puff pushed
  off its quad by the exhaust's stirs, as the trail clouds were. To look into.
- **Scorch marks that stay.** A crashed vessel leaves a scorch mark on the ground (or pad) where
  it came down, kept in the world (saved with it) from flight to flight; and an engine fired
  against the ground or a pad (launching, landing, a hover low over the terrain) scorches it
  too, building up the longer it burns there. To discuss before building: how marks fade or
  build up, how many a world keeps, and how they are drawn on the terrain and the concrete.
- **Impact craters that stay** (the user, 2026-11-11, during the moon's ground). Something that
  hits a body's surface hard (a crashed vessel, a spent stage on the moon, later an asteroid)
  digs a crater sized by its speed and mass, and the crater stays for the rest of that world's
  play. The user's idea for how: each saved world keeps its own copy of each body's terrain map
  (the planet's and the moon's), made from the shipped maps when the world is made, and an impact
  is cut into that world's copy, so the ground's height, its drawing and its collision all follow
  from the one map. To discuss before building: how big a crater a given impact makes, what the
  copy costs in disk and load time (the planet's map is about 11 MB gzipped, the moon's 2.5 MB: a world could keep only the
  parts it has cut, as patches over the shipped map), and redrawing the textures
  and tiles where a crater is cut while flying. Goes with scorch marks (above) and asteroids.
- **Comments that don't go stale.** Review the code's comments: inline comments that restate
  detail (numbers, how another file works, why a value was picked) are easily forgotten when the
  code changes, and then mislead later changes. Move that kind of detail where it fits better,
  such as a CLAUDE.md per sub-folder, and keep comments in code to what the code beside them
  can't say and is unlikely to drift from. Begun: the tests (`tests/GameOne.Tests/CLAUDE.md`) and
  the GameDriver (`tools/GameDriver/CLAUDE.md`) have their own; the game's folders (Physics,
  Rendering, Terrain) and the other tools have none yet. To discuss before starting: which of
  those get their own file, and what stays inline.
- **Friction by surface.** The feet grip every surface alike (`Scene.FootFriction`); sand, rock,
  snow, grass and ice should differ. Landing tests on each kind of real ground. To discuss before
  building.
- **Every body's ground acts as what it is** (the user, 2026-11-12). Today the ground is land or
  water, and all land is as hard as the rest (the kinds, `SurfaceKind` and the moon's
  `MoonKind`, only colour it). Each kind should carry how it behaves under a vessel: land hard
  in places and soft in others; and on bodies with low gravity, powdery ground (the moon's dust)
  that gives under a landing like a sponge, cushioning it, or that a heavy vessel sinks into.
  Goes with "Friction by surface" above (the same kinds, how they grip and how they give).
  To discuss before building: which kinds there are on each body, how giving and sinking are
  modelled (the feet, the touchdown's judging, a vessel left standing), and how the player
  tells them apart.
- **A testing area in the game.** The test range could seed an in-game testing area, to grow later
  (more pads and surfaces, a place to try a vessel out). To discuss before building.
- **A demo run of every feature** (the user, 2026-11-01). Not really a test: it uses the test
  framework (the watched flights, `GAMEONE_WATCH`, through `GameView`) to fly an automated tour of
  every gameplay feature the game has. It lets the user see new features, and judge whether
  existing ones still feel right for the game. Recorded to video, it can be shared to show the
  game's progress. To discuss before building: which features it shows and in what order, how
  it keeps up as features are added, and how it is recorded (resolution, frame rate, sound).
- **Vessels that take on water and sink.** A vessel in the sea can take on water and go under,
  down to the seabed, so heights below the sea (a negative altitude) are real. Not realistic but
  fun: empty fuel tanks could be what floods (and full ones what keeps it up). To discuss before
  building.
- **Vessels made of parts.** Eventually a vessel is put together from parts, and each part has its
  own impact tolerance: each part that collides judges whether it is destroyed (the user,
  2026-10-28). For now every vessel takes the ground's 6 m/s (`Vessel.ImpactTolerance`, the one
  place parts would take over). With it (the user, 2026-11-06): a vessel hit on one part comes
  apart there, rather than being destroyed whole; the collisions design's hits carry the
  point they happened at for this. The start of the longest chain here: fuel lines, parachutes,
  the crew pod and the crewed vessel all need it. To discuss before building.
- **Near misses at orbital speed over an airless body** (the user, 2026-11-03): 6,000 m/s close
  passes and crashes over a body without air; the speed sweep (to the top speed, 4,000 m/s, now) extended to them
  now there is one: the moon (40 km out, 2.5 km round, airless) is the first.
- **Raising the top speed** (to discuss before raising it). `Scene.TopSpeed` (4,000 m/s, a
  craft's cap, so two close at up to 8,000 m/s) is the one place the game's speed range is
  written: every limit follows from it or the speeds hit at, and the speed sweep and the paced fast
  collisions (`ShortPerfTests`) measure it. The world is the ground's frame, so something still
  among the stars moves over the ground at the planet's turn times its distance, 4,000 m/s about
  700 km from the centre: the moon, 40 km out, is far inside that, but a body further out, or
  anything sent past 700 km, would need the cap raised, or far things flown in another frame.
  Higher top speeds were tried (scratch runs, Release,
  not kept: the fast collision tests' collisions scaled up to twice the top speed closing, and the
  two paced checks; frames are a frame's physics, 95th percentile, 10 ms the budget; taken with
  the slow-down's earlier shape, whose least scale is the same):

  | Top speed | What hits | Least scale, real s slowed | Slowed frames (95%, worst) | Full-speed frames low over the ground | What breaks first |
  |---|---|---|---|---|---|
  | 4,000 (as built) | all 22 probe cases | 0.027 at 8,000 closing; 4.4 to 5.6 s | 0.55 ms in the vacuum; 2.1 ms (worst 8.7) diving at the slope at 10°, 1.9 at 45° | 2.1 ms coasting low; 1.8 ms (worst 8.1) level over the floe band | nothing |
  | 8,000 | all, to 16,000 closing (crossing 11,314) | 0.0137 at 16,000; 4.4 to 5.6 s | 3.2 to 3.5 ms in the vacuum at 5 km (the tiles laid under a second's 8 km stretch); 3.5 ms (worst 109) at the slope at 10°, 2.8 at 45° | 3.5 ms coasting low, 3.0 diving at 10° and 45°; 12.6 ms (worst 15) level over the floe band (the seabed's tiles made in the step, the worker outrun) | the ground's tiles: at full speed low over the floe band, and the urgent tiles made all at once as a slowed dive turns urgent (its worst frame) |
  | 16,000 | all, to 32,000 closing, once the floes fit (below) | 0.0068 at 32,000; 4.4 to 5.5 s | 4.3 ms in the vacuum; 14.5 ms (worst 186) at the slope at 10° (which, started 24 km out on a planet 31 km round, came down in the sea), 3.9 at 45° | 5.3 ms coasting low (it leaves the ground sooner); 24 ms over the floe band | the floes, at load: `Floes` throws "a floe band is 179 cells (about 4654 m) from cube face 4's edge, but a craft at the top speed (Scene.TopSpeed, 16000 m/s) takes up floes 4120 m round it (Scene.FloeReach + the top speed × Scene.FloeReachSeconds), which needs 230 cells (about 5980 m): lower the top speed or FloeReachSeconds, or lay the bands further from the edge"; with `FloeReachSeconds` halved (0.125 s) everything else ran, and then the tiles, as at 8,000 |

  Jolt held at every speed (its cap follows `Scene.TopSpeed`, and no collision passed through),
  and so did the wake (woken 2,171 m off at 8,000 m/s against its 2,200 m reach, 4,076 m at
  16,000 against 4,200) and the float precision (nothing within 10 km of the origin went
  wrong; a moon 40 km out would be the test of it). Two things found were fixed for every
  speed: the tiles' stretch was walked over its whole bounding box, a square its length wide
  when it lay across the plane's axes (`TerrainTiles.Needed`: most of a slowed frame at
  8,000 m/s; it halved the 4,000 m/s dive's slowed frames, 4.3 ms to 2.1), and the slow-down test checked a step's travel over
  a second before the pass, where the least scale holds only for the collision's own lead (T0,
  0.85 s at 16,000 closing). To raise the top speed: the floe bands laid further from the
  cube's face edges, or a shorter `FloeReachSeconds`, past about 12,000 m/s; the tiles' cost
  near the ground at full speed (see "the tiles' stretches by the game's clock" above for the
  slowed part); then re-run these.
- **A fast body's leading shape stretched ahead** (an idea, not built: the forced slow-down was
  chosen instead): a fast craft's shape projected ahead along its travel as far as it goes in a
  step, so each step finds anything within that reach. Kept in case a slow-down ever can't be
  (collisions between vessels under formula aren't slowed for).
- **Fewer markers on the screen** (the user, playing on the moon: they clutter; to discuss before
  building). Today they are switched by kind in the settings, fade as their object grows, and the
  quieter ones give way where they would overlap. Ideas raised, none chosen yet:
  1. A key stepping through all markers, only the important ones (the target, the landing site,
     planets and moons), and none, the level shown briefly as it changes.
  2. Only the important ones as a rule, the rest while a key is held.
  3. A reach per kind: places only within some distance, or only on the body you are on or
     nearest (no Home pads from the moon); debris only within a few km. (With 1, the one
     recommended: 1 for a moment's choice, 3 so the clutter needn't be switched off by hand.)
  4. Markers bunched on the screen grouped as one ("Home +3"), spread out under the pointer.
  5. The quieter ones fading after a few seconds with no input, back on a mouse move or a new target.
  6. Which show by the flight's phase (on the pad, in orbit, landed): the most automatic, the
     hardest to predict.
  Note that the keys are for flying (CLAUDE.md): a marker key would be a second exception beside F2.
- **The mission brief, to read again and to close** (the user; to discuss before building): the
  brief shown in the middle as a flight begins (the mission's name and what it asks) can be
  opened again later from the mission objectives panel, top right. The brief gets an x so the
  player closes it when they like, rather than waiting for it to go. Opening or closing it never
  pauses the game.
- **The eclipses and the planet from the moon, drawn closer to life** (seen in the moon's
  screenshots; the user's call to leave them for later; to discuss before building):
  1. A lunar eclipse has no shadow edge: the moon's whole disc dims and reddens evenly, as its
     light is the planet's shadow worked out at its centre (`SolarSystem.MoonShine`). A curved
     umbra crossing the face would need the shadow per pixel on the moon.
  2. In a deep partial eclipse of the sun, the stars come out over a sky that is still blue and
     lit (Home's, day 2 13:47, and again after totality): they go by the sun's covered share,
     not by how bright the sky still is.
  3. The planet's limb from the moon is ragged (terrain bumps on its silhouette), and the air's
     ring round it speckled and noisy at its inner edge (during the moon's eclipse, seen from it).
  4. The pad lights on the planet's night side, from the moon, are a pixel or two at most (a warm
     dot by Home's marker): too small to show the sites or a twinkle, and there is no zoom to
     bring them closer.
  5. On the moon, the planet over the sun (the moon in the planet's shadow) doesn't bring a
     lander's landing lights on: `Sun.IsDark` at the moon asks the sun's height and the moon's
     own cover, not the planet's shadow (`SolarSystem.PlanetShadowLight`, which could be asked
     with the same held thresholds as the moon's eclipse of the sun).
- **The moon's ground: a younger crater replacing the older ground in its bowl** (from the
  moon's craters, 2026-11-12; the user has seen them and likes them as they are; to discuss
  before building). Where two craters overlap, TerrainGen's `MoonGenerator` adds their profiles
  (`Profile`), so a young bowl cuts an old rim but keeps the old bowl's shape under it. A real
  impact wipes out what was there: the younger crater's bowl would replace the ground inside it
  (laid in age order, each bowl and rim blended over what is under it), so overlapping craters
  read as one on top of the other.

## Known limits

Things found, understood and accepted for now (the user's call), kept here with why, so they
can be picked up again if they start to matter.

- **Orbits creep a little, from float rounding** (found by the merged orbit test in `WorldTests`,
  2026-11-04; explained and accepted, the user, 2026-11-06). The Satellites preset's 60°
  satellite loses about 0.27 m of semi-major axis a lap (0.81 m over three laps, 19,565 m), the
  90° one gains about 0.07 m a lap, and the two in the equator's plane hold to about 0.01 m.
  - **Why:** a vessel's position is a float (Jolt is built in single precision, and the game
    reads it into `Vector3`), and about 20 km from the world's origin a float holds it to about
    2 mm. Each step's rounding is tiny, but in the ground's turning frame it doesn't average out:
    for a given orbit it leans one way, lap after lap. Which way, and how much, depends on the
    orbit's tilt, so it is no decay bringing everything down, rather an error of about one part
    in 100,000 a lap.
  - **How it was split** (scratch runs, not kept, three laps each): the game's step
    (`Planet.FallStep`, kick then drift) in doubles holds every orbit to 0.000 m, so the turning
    frame's pulls and the Boris step are exact; the same step in floats with no Jolt creeps
    -0.807 m on the 60° orbit, as the game through Jolt does (-0.808 m), so Jolt adds nothing;
    rounding only the position to float gives -0.65 m, only the velocity +0.27 m, both -0.86 m.
    Drag plays no part: the air ends at 3 km (`Scene.AtmosphereHeight`), the orbits are 9 to
    10 km up. A planet that doesn't turn, in floats, wanders by about 0.1 m with no lean.
  - **Why accepted:** at that rate the 60° satellite would take about 24,000 laps (over 100
    hours of play at the 32x warp) to sink into the air.
  - **When to come back to it:** if long missions need orbits held for longer (a station kept
    for days of game time, say), the farther a body is from the origin (a moon 40 km out would
    be held to about 4 mm, so creep more), or if the test's bound (0.4 m a lap on the 60°
    orbit) starts to fail. The fix would be positions in double: Jolt built in double precision
    (`JPH_DOUBLE_PRECISION`, which is BRUTAL's build, not the game's), with the game's own
    positions in double beside it. Moving the world's origin wouldn't help, as orbits are about
    20 km across wherever it sits.

## To discuss before building: resuming a mission after quitting

A world loaded mid-mission starts a new flight, so a long mission (LAND ON THE MOON, about 20
minutes of flight) can't be carried on from where it was left: the lander is still in the world,
but it is no longer this flight's mission, and a fresh rocket stands on the pad. Should a saved
world keep its mission and its progress, and how?
