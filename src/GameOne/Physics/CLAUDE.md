# CLAUDE.md: flight, physics and worlds

How the game is simulated and saved, split out of the repo-root `CLAUDE.md`, which holds the rules that
hold across the project (read it too): the terrain, touches, fast collisions, vessels nobody watches,
the main camera's subject, touchdowns, the sea, worlds, the turning planet and the frames of reference.
What is named here as another section ("Low hand-holding", "Which way is up") is in the root file; the
drawing of any of it, and the C# and GLSL pairs, are in [`../Rendering/CLAUDE.md`](../Rendering/CLAUDE.md).

- **The terrain:** the planet is not a sphere. `Terrain/` holds `CubeSphere` (directions to and from
  cube-face coordinates), `TerrainMap` (the baked map), `SurfaceKind` and `Terrain` (the planet's one height
  function: map plus fine detail; a body's is its `GroundBody.Height`, the planet's this). The map, `Assets/terrain.bin.gz`, is made
  deterministically by `tools/TerrainGen` from `layout.json` (see its README). **Regenerate it**
  after changing `Scene.PlanetRadius`, `Scene.HomeElevation`, the layout, where `Scene` puts the
  pad and zones, or their footprints (`Scene.PadHalfSize`, `Scene.LandingZoneRadius`: TerrainGen
  levels the far sites' ground under them): the game refuses a map made for another radius or
  Home's elevation. The moon's ground is `Terrain/MoonGround.cs` (moon-fixed directions), its map
  `Assets/moon.bin.gz` made by TerrainGen's `--moon` mode from `moon.json`: regenerate it after
  changing that, `Scene.MoonRadius`, `Scene.MoonLandingSpot` or its radius. Collision is
  `Physics/TerrainTiles.cs` (height-field tiles round awake crafts;
  resting crafts are frozen and wake by `Scene.WakeSpeed` and `WakeMargin`, the ground under one
  about to wake made ahead of need, `Craft.WakingSoon`). The tiles' shapes are
  made ahead of need on one background thread every world shares (`Physics/TileWorker.cs`, soonest first, each job its world's body), so the step
  only adds a finished one's body, and makes a tile itself only when a craft needs it within
  `Scene.TerrainTileUrgentSeconds` (0.25 s of its travel along its way, in full-length steps: shortened
  with the step in a slow-down, so the same real time ahead); a wreck has none made for it; a tile
  taken out keeps its shape a while. A craft faster than `Scene.FastCollisionSpeed` has no ground
  laid under its way: only round where the look ahead predicts it comes down
  (`Craft.GroundAhead`, kept for every step of the physics' next update), its way in within reach
  of the ground there, asked of the worker by when each cell is due and laid in the step only
  once due within the urgent time. `Foundations` sits
  under the pads and zones (Home's and the test pads' down to below the lowest ground under them;
  each far one a `Footing`: a 3 m straight-sided slab, then narrowing in at 45° down into the
  ground, drawn smooth, its collider boxes stepping in to follow it); `Orbit.End` ends a path
  where it meets the terrain; `Rendering/PlanetPatches.cs` chooses the patches drawn (frustum
  culled, within `Scene.TerrainPatchBudget`) and `VulkanRenderer.Terrain.cs` draws them. The
  seabed has a depth push so it never shows through the water. Where the water is drawn (and the
  seabed pushed) is each corner's waterline (`Waterline`, binding 11, `vWaterline`): near, its own
  depth; where a square is over two texels, the map's heights clamped to ±`Scene.PackIceFreeboard`
  and averaged over half a square (a mip chain read at the level for the spacing, `Waterline.Level`,
  twinned in the GLSL's `waterlineLevel`), so an ice sheet's edge falls in proportion between
  corners rather than in steps along the squares. The far maps (`GroundColors`, `Waterline`) share
  `FaceLevels`' layout and averaging. The ground's colour is read per
  pixel, never from the vertices: near, from the kinds' four texels with their edges wandering
  (`kindColor`), cross-faded (from 2.5 km, or a texel down to a pixel or two) to one filtered
  read of `GroundColors` (binding 10: the kinds' colours, RGBA8 display values, the faces'
  insides without aprons, a mip chain averaged in linear light; it needs the map's resolution a
  power of two). The kinds' colours are `Scene.KindColors`, and the GLSL's `kindColors` is
  written from it.
- **Vessels touch:** crafts touch each other (object layer `Moving` with `Moving`). What
  each craft touched is read from Jolt's contacts after each step (`RocketPhysics.TouchesBegun`, from the
  native `ContactEventListener` queue; `Physics/Touches.cs`): what it was (`SolidKind`, kept in
  each body's user data: the ground, the tower, a mast, a raft or another craft), where on the
  craft (`CraftPart`: its feet, bell or hull), the point and normal, and how fast the two were
  closing there just before the step. A touch is a contact's beginning, and only if they were
  closing. Two vessels touching are each judged by that closing speed, whatever their masses: over
  `Vessel.ImpactTolerance` (the ground's 6 m/s for now, asked of the vessel so that parts can
  judge their own later) both are wrecked ("Hit another vessel"). Only a contact can be a hit, so
  a hard manoeuvre beside another never is. What each craft touches now is its live contacts
  (`RocketPhysics.Touching`, by kind and by part), which freezing, the wake on a touch and the
  ground's measuring (see "Touchdowns") read. Jolt reports no contact ending when a sleeping body
  is removed (and ends a frozen craft's in the step after it freezes), so whenever the game takes
  a body out (a craft, a tile, a floe's hull) it forgets that body's contacts itself
  (`ForgetContacts`). A wreck's body is left in the world, unseen and
  unjudged, for `Scene.WreckSeconds` (0.4 s: `RocketPhysics.Wreck`), so what it hit takes the
  whole push (no new ground tiles are laid for it meanwhile); a new flight takes every wreck out at once (`RocketPhysics.FlushWrecks`), as the menu
  that starts it pauses the game. Just as a vessel touches down, a hit is held until its
  touchdown is judged (wrecked by that, it was the ground's). Stages and fairing halves start inside
  what they came off, so they are put in the `Parting` layer (touching only what doesn't move)
  until clear of it (`Craft.PartingFrom`, `RocketPhysics.EndParting`: told by each one's axis and
  widest radius and, with its legs out, its feet, `RocketPhysics.Apart`, which also tells what
  rests on what and the freezing's groups). A vessel at rest on another is landed (its touch on that one
  counts among its touches), and freezes there (`FreezeResting`); a frozen one wakes only as something
  moving could touch it: its body, swept along its way ahead over `Scene.WakeSeconds`, comes within
  `Scene.WakeMargin` (5 m) of the frozen one's, as the gap between their shapes whatever their
  sizes (so a fast one wakes what it is coming at in time, and nothing it only passes: not the rest
  of a field it dives into), or as another touches it. A raft's heave wakes nothing on it (what is
  frozen there is carried with it).
- **Fast collisions:** open space has no speed cap (Jolt's own is set far past anything flown,
  `RocketPhysics.JoltTopSpeed`, so it never takes hold: the ground's turning frame itself moves
  faster than 4,000 m/s some 700 km from the planet's axis). Low over the ground
  (`GroundBody.LowOverTheGround`: within `Scene.GroundTopSpeedHeight`, 1,144 m, of a body's
  highest ground, the furthest the ground's systems reach ahead at the cap) the game holds a
  craft's speed over the ground to `Scene.GroundTopSpeed` (4,000 m/s, so two close at up to
  8,000 m/s there), in the physics (`Fall`, Jolt's velocity and its double copy together) and under
  formula (each step that starts or would end in the band; stepped there while faster, its orbit
  asked at every flight step's place, so it can't pass over the band unheld) alike. That figure is written in that one
  place: every limit that follows from it (the band's height, from the urgent tiles', the wake's and
  the floes' reaches at it; the floes' reach, clamped to it, and their room from a face's edge) is
  worked out from it, and every other bound (the slow-down's
  least scale, the predictions' horizons, the reach to the towers) from the speeds hit at or the
  vessel's own speed, never tuned to it by hand. A fast collision is one closing
  faster than `Scene.FastCollisionSpeed` (a satellite's width a Jolt step, 216 m/s) as it hits,
  predicted with the vessels' present thrust (so a landing burn is none). `Collisions` (one per
  `Flight`) predicts each witnessed vessel's collisions (the flown one's, each small view's, and
  the few in the main view nearest its camera: within `Scene.NearByDistance`, at most
  `Scene.MainViewWitnessed`): another vessel (only one at the same body), the ground, the sea, a
  raft, the tower or a mast, named by its kind (`Collisions.Name`: a new kind of object brings its
  name and its check, nothing else), asked once a step after the motion; the main view's few in
  turn, one a step, but any new to the view or whose last asking found a collision every step (so
  a resting one there that something fast out of view starts closing on waits up to
  `Scene.MainViewWitnessed - 1`, 3, steps to be asked). The ground is any body's, one kind named
  by its body (`GroundBody.GroundName`: "the ground", "the moon"): the path's, patched across the
  spheres of influence (`Patched.Legs`, from the body it would be at: one just inside another's
  sphere before its body is changed is looked at from that one), comes down on the ground
  of whichever body it is at by then. So a vessel about to cross into the moon's pull has the
  moon's ground found before it crosses, and is warned of it at its lead like any collision (for
  a fuelled stage, after the crossing; with no fuel, at once); `Collision.Hit` and
  `Collision.Body` keep the body hit, in whose frame its place is, as what was warned of is kept,
  so it holds across the crossing. A slow touch there is judged by the ground's rules, unwarned,
  as on the planet. Each body's lowest surface (`GroundBody.LowestRadius`: its sea, or with none
  its lowest ground, the moon's plains and craters lying below its datum) is where every path
  ends at the latest coming down; going out, every path ends at the solar system's edge
  (`Scene.SolarSystemEdge`, 10,000,000 km round the sun's centre, found among the stars at the
  sun's place then, in double: `Patched.LeavesAt`, `SolarSystem.FromSun`), its last leg marked
  `PathLeg.LeavesSystem` and drawn with its mark ("Leaves the solar system", `Paths.EdgeMarks`;
  the map's laps too), so nothing walks or searches on past it (an `Orbit` alone runs out to
  `Orbit.MaxRange`, twice the edge, so the edge comes first). The map's search (`CollisionSearch`) checks only pairs of vessels, never
  the ground. The flown vessel's soonest is its
  `Flight.Warning` (what is coming and in how long, never which way to go: see "Low
  hand-holding"), which drops the warp once for each collision, in time to dodge. As one comes
  near, `Flight.Slowed` slows the game's clock and the physics' step together
  (`Worlds.StepScale`, every body's physics' at once) for the collision asking the least scale (`Collisions.Deepest`: the
  soonest can hide a faster one just behind it); the rate keys do nothing while it is on.
  **The slow-downs' shape**, the fast collisions' and the near misses' alike, is a few figures in
  `Scene`, tuned by watching (see the tests' `CLAUDE.md`): it begins `Scene.SlowdownFrom` (1 km)
  out, middle to middle; the speed to the eye eases evenly with the distance down to
  `Scene.SlowdownPassSpeed` (10 m/s); that is reached `Scene.SlowdownHoldWithin` (10 m) short of
  the collision or closest approach and held until as far past; then it eases back over
  `Scene.SlowdownEaseSeconds` (3 s real). There is no time limit: how long the way in takes follows
  from these and the speed (at 1,000 m/s, about 5.6 s). A fast collision is never slower to the eye
  than `Slowdown.Least`, which keeps each step's travel within `Scene.FastCollisionStep` as it hits
  so nothing passes through (`Slowdown.Deepest`, `Lead`, `Hold`, `ScaleFor`); its hold is
  reached before the two sizes touch (`Collision.Touch`), and once they overlap it counts from the
  true closest approach of their middles. A fast collision slows only the way in for now; once
  vessels are made of parts, the parts passing on will have the way out too.
  **Near misses** are slowed only with the player's setting on ("Slow motion for near misses",
  `Settings.SlowNearMisses`, off by default; `FlightInput.NearMisses`): another vessel passing a
  controlled one (the flown one or a small view's) within `Scene.NearMissLengths` (1) of its own
  length (`Vessel.Height`), by the gap between their outlines (each body an axis as wide as
  itself, `Unwatched.Body`), not their centres, without touching (a touch is a collision), and
  faster than `Scene.NearMissSlowAbove` (300 m/s: slower, it is seen well enough at normal
  speed) (`Collisions.NearestMiss`, a `NearMiss`; only vessels, never the ground, the sea, a raft,
  the tower or a mast). It is slowed by the same shape (`NearMiss.ScaleAt`, worked out in the
  game's seconds from the pass, so it needs no clock of its own). `NearestMiss` is the pass asking the least scale now (one just gone,
  still easing back, can be deeper than the next), `SoonestMiss` the soonest still ahead: the
  flown vessel's is `Flight.NearMissWarning`, shown ahead from the moment it is predicted (the
  NEAR MISS line), which drops the warp once for each other vessel, forgotten as the collisions
  warned of are. It shares `Flight.Slowed` (`Slowdown.NearMiss`, "a near miss with ..."): with a
  fast collision at once, the deeper scale wins (the reason shown changes only once the other is
  deeper by `Flight.ReasonMargin`, 1%, so it doesn't flicker). The other of a pass may be under formula while
  far off (it is handed in as it nears): it is predicted from where the formula has it, as a fast
  collision's other is, so the warning isn't held to the handover's reach (`HandoverSeconds`).
  **Both are found 20 s ahead:** a pair's closest approach, for a fast collision between two vessels
  (`Pairs`) and for a near miss, is found by flying both paths ahead (`Collisions.Closest`, Verlet
  among the stars, each by its own pull and present thrust) in chords of half the time to go,
  from `PathStepSeconds` (0.25 s) to `LongestChordSeconds` (2 s), a dozen to a pass 20 s off,
  as one pull taken half-way bends a pass that far off by tens of metres, more than a near
  miss's gap; the march ends at the horizon (`Scene.CollisionHorizonSeconds`, 20 s), at a count of
  chords, or on a NaN. **What is coming** at each controlled vessel is `Flight.ComingAt` (all of
  them `Flight.Comings`, kept each step in `Comings` from what the look ahead already asked: no
  prediction of its own): the sooner of its fast collision, from its warning on (its lead,
  `Collisions.WarningSeconds`, or once the game is slowed for it), and, with the setting on, its
  near miss still coming, until it has hit or passed. A small view's border flashes red for a collision, amber for a near miss
  (`Hud.ComingColor`: `Hud.Danger`, the error sign's red, or `Hud.Caution`) while anything is coming at its vessel (the ground and the
  towers too), at `Scene.ComingFlashHz` (2 Hz), by real time (`Hud.FlashOn`), so it keeps its
  rate while slowed. In the main view a vessel coming at the flown one (never the ground, the
  sea, a raft, the tower or a mast) has the coming mark (`Markers.ComingMark`): a diamond in the same colour, with its
  name, the seconds to go and its distance, drawn whatever the markers' settings (markers off
  too), never faded as its vessel grows on the screen nor hidden behind the planet (it is wanted
  most as it passes close). It is that vessel's one mark, not two (its ordinary marker, its
  target's too, left out while it comes), double clicked as its marker would be (`Markers.Click`), and
  listed among the readout's markers for a script. The compass tape has a red tick at the bearing
  it comes from (`Hud.TapeOffset`: held at the tape's end when behind; none from straight above or
  below). They say what is coming, from where and when, never which way to go (see "Low
  hand-holding").
  **The time rate is the flight's:** `Flight.RunFrame` takes a real frame and the rate keys and runs
  that times `Flight.TimeScale` of game time (the player's rate, or the slow-down's if slower),
  so the game and the tests (`Simulation`) step alike; nothing else sets the rate or the step
  (but the time-rate tests themselves, `TimeRateTests`, which set the physics' step scale,
  `Worlds.StepScale`, every body's at once, directly to check the shortened step on its own).
  Slowed, the steps a real second are the same, each shorter, so the frame rate holds (checked
  paced, `A_fast_pass_holds_60_fps`: see the tests' [`CLAUDE.md`](tests/GameOne.Tests/CLAUDE.md)).
  **The ladder** (`Flight.Rates`, `Flight.Warp.cs`) doubles from 1/4 to `Scene.TopRate`
  (16,384x), one step a press of a rate key (holding one doesn't step on): the physics' rates up
  to `Scene.PhysicsTopRate` (8x: a warped frame that many full 1/60 s steps, within
  `Flight.WarpBudgetSeconds`), the formula's past it (`Flight.IsFormulaRate`, from 16x). A frame
  at a formula's rate (`AdvanceByFormula`) steps nothing of Jolt's: every vessel, the flown one
  too, is under formula (`EnterFormula`, the worlds kept made but not stepped; given back as the
  rate drops, `LeaveFormula`), the frame cut into windows of at most `Scene.FormulaWindowSeconds`
  (12 s, `FormulaStep`: the vessels' rules once a window, every vessel moved to its end, the
  crossings, the edge, the collisions under formula, `FormulaCollisions`, and the runs), each cut
  short at the earliest event in it (`NextCut`: the warp to a point's time, a vessel's crossing,
  `FormulaMotion.CrossesAt`, and its reaching the solar system's edge, `LeavesAt`, a burn's fuel
  running out, a drop's moment), so nothing is stepped over (one that goes on its orbit within a
  window and crosses in it is shown at its crossing, `FormulaMotion.Behind`, and `Cross` hands it
  across then and moves it on); it stops at a window's end once the budget is spent, and a drop
  due as the formula's rate is taken is dropped before anything is handed out (`DropDueNow`). The witnessed fly there by formula:
  burns, the holds, the rings (a click or a drag turns the vessel at every rate) and their nodes
  (`BurnByFormula`, their rules every `Scene.FormulaBurnStepSeconds` of a burn above the air).
  **The gate:** while a witnessed vessel (the flown one, a small view's, the main camera's, and the
  one a switch's link-up waits on: `WarpWitnessed`, the one set the gate, the cuts and the rules
  along each move count) flies in the air or low over the ground, or isn't yet at rest
  (`Flight.WarpHeldBy`, the readout's `Warp held`; the time line says which and why,
  `Flight.WarpHeld`), Faster past the physics' top rate does nothing; one in the air seen at a
  formula's rate drops the warp before the frame runs. So does a fast collision
  coming at a witnessed vessel (`CollisionHolds`: warned of by the physics, or one the warp
  dropped for still coming and closing past `Scene.FastCollisionSpeed`), until it has hit or
  passed, or a pair dropped for is no longer predicted (dodged: `Collisions.StillComing`, and
  then forgotten as dropped for), so its slow-down plays out in full; the time line says why
  (`Flight.WarpHeld`, from `CollisionHeld`: "held: a collision coming at Booster 0 with Booster 1",
  the readout's `Collision hold`), from the drop on. At a
  formula's rate a link-up waits on its body's physics made ahead (`MadeFor`), so the drop never
  makes it in a frame. **A switch** (Tab, a click on a small view: any that flies another
  vessel) drops any warp, the physics' rates' or a formula's, to normal speed as the frame that
  takes it begins (`DropForSwitch`, `WarpDrop.Switched`, kept as `Flight.LastDrop`), ending a warp
  to a point, so no window of it runs past the switch and nothing is carried on by formula after
  it; so do a carry-on (`Switched` too) and a rewind, as each shows a link-up to fly a vessel. A
  small view opened or the main camera moved is no switch; a flight begun keeps a physics' rate. **The drops** (`WarpDrop`: `Air`, `Ground`, `SetMoving`,
  `Steering`, `Collision`, `NearMiss`, `Node`, `FuelOut`; `DropToPhysics`, kept as
  `Flight.LastDrop` and said on the time line for `Scene.WarpDropShownSeconds`) go to the physics'
  top rate, `Scene.WarpDropLeadSeconds` (4 s of game time) ahead of what each is for (the way into
  the air or the airless ground's reach, a node less half its burn, the fuel's end), and at once for
  a vessel action (launch, staging, the legs, the bay doors, the throttle at rest) or a steering
  key, before the input is taken, so that is taken in the physics. A collision or near miss
  coming at a witnessed vessel is asked once a window (`Collisions.AheadOfTheWarp`) and drops it
  once for each other vessel (`ForgetWarpWarned` forgets one only once it is out of reach); the
  physics' warnings, not asked at a formula's rate, are let go as it is taken (`EnterFormula`). The
  warp never climbs back by itself (only the rate keys and a warp to a point raise it), and
  crossing a sphere's edge doesn't drop it; at the physics' rates the warnings still drop it to
  normal speed (`DropWarp`). From `Scene.AveragedLightRate` (256x) the light is shown averaged
  (`AveragedLight`, eased over `Scene.AveragedLightEaseSeconds` of real time: see "The air, in
  C# and GLSL"), the pad and landing lights held at their place's dark share of a day
  (`Sun.DarkShare`). A right click on the flown vessel's path (zoomed out, where the path is
  drawn) warps to that point (`FlightInput.WarpTo`, from `NodeEditor.WarpPoint`, a marker or a
  vessel under the pointer winning: `Hud.RightClickWarp`): `Flight.WarpingTo` runs at the
  fastest rate the gate allows and `RunFrame` cuts the frame that gets there short at it, so it
  stops at normal speed on that moment; while the engine burns the time is held to the path's new
  end once a frame. A rate key, a switch, a warning dropping the warp (`DropWarp`), the vessel no
  longer flying (landed, afloat, lost) or a world loaded or a flight begun (`Begun`) ends it early
  at normal speed (a drop, at the physics' top rate).
- **Vessels nobody watches:** each vessel is at one body (`Vessel.Body`), and everything it
  exposes is in that body's frame; each body in use has its own physics (`Physics/Worlds.cs`, one
  `RocketPhysics` a body, `Vessel.Physics` a vessel's own body's), made as a vessel there first
  needs one (`Worlds.Need`, from a world released to the pool when there is one, rebuilt for the
  body: `RocketPhysics.Rebuild`), or ahead of need on the tile worker's idle time (`Worlds.Ahead`,
  never in the step), stepped while a craft is in it, each in turn in `Bodies.All` order, and
  released after `Scene.PhysicsIdleSeconds` with none: a body with nothing in its physics has
  none, so it costs nothing, and the witnessed (the flown vessel, the small views' and the one the
  main camera is on) are at seven bodies at most, one each (one emptied is kept a while: nothing
  nobody watches keeps one running). Nothing is written for two bodies: a vessel is asked which
  body it is at, never "the other one". At each body, only the witnessed and what could soon hit
  them are in its physics; every other vessel is out of it, moved by formula (`Vessel.OnFormula`:
  its body taken out by `LeavePhysics`, its removed `Craft` kept as a stand-in so `Craft` reads
  stay safe, every live Jolt read in `Vessel` guarded by `OnFormula`; made again where the formula
  has it by `ReturnToPhysics`), at a fraction of the cost (the user: "the cost of simulating them
  is kept as low as possible"). `Unwatched` chooses each step, per body (only what is at one body
  is near anything there; the main view's camera is at what it is on): the witnessed (the flown
  vessel, the small views', the vessel the main camera is on, `FlightInput.Watching`, and the main
  view's near its camera), and whatever could come within `Scene.HandoverDistance` of something in
  the physics, or of the camera, within `Scene.HandoverSeconds` at their closing speed, grown out
  from the witnessed, and nothing else: whatever one nobody watches is doing (parting, settling in
  the water, on the pad, landed, aboard a raft), the formula plays it out, at every rate, never
  bringing the physics in nor changing the warp (a wreck already in the physics plays its hit out
  there, `RocketPhysics.Wreck`). Being near a site keeps nothing in the physics (the formula
  checks the towers and masts itself). One leaves only past `Scene.HandoverHysteresis`
  times its reach; at most `Scene.HandoversInPerStep` come back a step, every body's together,
  each into its own body's physics, soonest needed first (one
  switched to or given a view at once: `Flight.BringIn`, and the other of a collision under formula
  it is in with it: `Unwatched.Return`), so a crowd handed in and out holds the frame rate
  (reported paced, `A_crowd_handed_in_and_out_reports_its_frame_rate`). A world loads those far from the new
  rocket's pad (or at another body) that nothing would hold in the physics straight under formula, with no body made
  (`Unwatched.MayLoadOnFormula`, a bodiless `RocketPhysics.StandIn`, and no physics made for its body), so its first step hands
  nothing out; `Flight.Load` resets `Unwatched` (`Reset`). `Formula` moves them, each at its own
  body by that body's ground, air and pull (the moon: no air, no sea)
  (simpler, even over-efficient, as the user allows: thrust along the aim at once, no bounce, hop
  or tip; engines, holds and fuel go on, the fuel burnt by the formula's steps with the thrust;
  nodes flown by the witnessed alone, at the formula's rates, `Flight.BurnByFormula`; staged
  (`Flight.StageVessel`, nothing in the game asks it under formula yet: autopilots will)
  by the numbers, `Vessel.SeparateByFormula`: the parts at their places in the stack, each with its
  own mass and fuel, parting as the physics' do, a fairing's halves pushed out on their sides,
  clipping ignored, nothing brought into the physics): on its orbit by Kepler with
  its engine off, above the air and clear of the body's highest ground (`Formula.OnItsOrbitAt`:
  at a body with no air only its height keeps it clear); in the air, burning, or below that
  height, stepped every `Scene.FormulaStepSeconds` (burning where it could be on its orbit, every
  `Scene.FormulaBurnStepSeconds`, `Formula.StepFor`; in double, as the physics' copy is: its pull
  and air read where its drift takes it, `Formula.Lead` on, so a stepped orbit holds its size as a
  plain kick and drift does); landed and afloat still. It goes on from the physics' double copy
  as it leaves, and is handed back as that copy (`RocketPhysics.Keep`); on its orbit, it is moved
  from where it started in double by what the Kepler orbit (in floats) says since (`FormulaOrbit`).
  Its touch is judged once, by its whole speed, against the physics' limits and with the
  physics' own reasons (the tower's is "Flew into the service tower."), read along each step so
  no ridge or floe is stepped over. Nothing is handed back to the physics for it: into water
  shallower than its plunge's reach it comes onto the seabed at `Scene.FormulaSeabedSpeedShare`
  of its speed into the water, judged there by the ground's rules; come down on a raft (landed,
  afloat or standing burning) it is aboard it (`Vessel.AboardUnderFormula`), carried by the
  raft's pose at the world's time (`Rafts.Time`, which the physics at the sea sets as it steps,
  and the flight where none does: `Worlds.SeaStepped`), and comes back into the physics frozen
  aboard there (`RocketPhysics.Board`) only when something witnessed brings it in. Two under formula collide as
  part 1 judges them: at the physics' rates by `Unwatched.FindCollisions`, every
  `Scene.FormulaCollisionSeconds` over the interval ahead (swept boxes, then their bodies, each an
  axis as wide as itself, judged only as a touch begins, face on along a nose, else side to
  side; two at different bodies never); at the formula's, by `FormulaCollisions`, back along each
  window's ways once every vessel is at its end (each way the cubic Hermite through its ends,
  boxed and swept, each overlapping pair's closest approach found, and within reach their bodies
  solved as at the physics' rates, `FormulaContact`; one whose motion was remade in the window
  left out of it), so none is skipped however long the frame: both wrecked over `ImpactTolerance`,
  and never slowed for, nor the warp dropped; a gentler touch passes through (nothing changes: one
  may fall through another's top to the ground). The map shows predicted collisions between any two vessels at
  one body (`CollisionSearch`, off the step on its own thread, the landed and afloat held where they stand;
  `Paths` draws them; no pass is asked while the world's clock stands still), information only.
  Every vessel lost, watched or not, and every one removed (to make room, as a world loads or is
  added to, cleared from the launch pad, or gone past the solar system's edge), is written to the world's log (`Flight.Log`,
  `WorldLog`: fairing halves are not; a removal with no speed, and seen only if the last step had
  it in the physics; two lost to one collision one entry, paired by the vessel actually hit,
  `Vessel.HitCraft`, however many steps apart; saved with the world, and a rewind (`FlightInput.Rewind`) reverts
  it with the world it brings back), shown by the game menu's "World log...". A vessel not wrecked
  that is past the solar system's edge (`Scene.SolarSystemEdge`, round the sun) is lost as a crash
  (`Vessel.LeaveTheSystem`, its run failed if it needed it) and taken out: checked once a step,
  after the bodies are chosen, by its distance from the sun, not a crossing, so the warp can't
  step it past unseen; one nearer its body's centre than the edge less the farthest that body
  gets from the sun (`SolarSystem.FarthestFromSun`) is not asked (`Flight.EdgeAsked`); the flown
  one is kept as the one flown, its message shown, until another is flown. The timings panel counts those under formula (the readout's
  "Timings under formula"); its step's parts are every body's physics' together.
  **Crossing a sphere's edge:** each step, after the physics and the vessels' rules, every vessel's
  body is chosen by `Bodies.Choose` (into a child's sphere at its radius, out to the parent only
  past `Scene.SphereLeaveFactor` times it, so a path grazing the edge never flip-flops; each
  body's place worked out once a step, `Bodies.PlacesIn`, and a vessel nearer its body's centre
  than any edge, `Bodies.ClearWithin`, not asked at all: the landed, the afloat and most in
  orbit), from the body it is at into whichever that gives, parent or child, and a vessel whose body changes is
  handed across (`Flight.Cross`, `Vessel.CrossTo`) at the moment its motion is at (every body's
  physics is kept at the same leftover of a step, `Worlds.Lag`): its place, velocity, attitude,
  turning, aim and a burn begun changed into the new frame in double (`Bodies.Change`), so nothing
  jumps. In the physics its craft is taken out of the one world and into the other, the same
  craft and shapes (`RocketPhysics.TakeOut`, a `CraftState`, its centre of mass and velocity the double copy's, then `TakeIn`), into the new body's
  physics made ahead of need: the path of each vessel flying in the physics (the witnessed, and
  what is in it near them) is looked along `Scene.PhysicsAheadSeconds` (times the rate in warp)
  across the spheres (`Patched.Legs`)
  and each body they will be at asked for (`Worlds.Ahead`), so the step never waits on one being
  made. Under formula it stays under formula, on its orbit there (or stepped) from where it
  crossed, its body's formula collisions looked for again. **Vessels at different bodies never
  pair or touch**: each vessel crosses by itself, the moment its own place says so, so two side by
  side at the edge can't touch until both have crossed. The flown vessel's frame changing
  (its crossing, or a switch to one at another body) raises `Flight.ViewRebased` with the change.
  Every switch, carry-on and rewind shows the communications link-up first, and the view comes
  back as that vessel left it (`Flight.ViewTaken`, raised as it is flown: `GameView` gives the
  camera `Vessel.View`, or `GameView.DefaultView` for one never flown, as each new rocket is; a
  flight started or a world restored puts the camera back on the flown vessel, `GameView.Restart`;
  each frame's `FlightInput.View` is kept as the flown vessel's, but not from a world restored
  until the vessel carried on is taken, as the camera shows what it did meanwhile; `LinkUp`,
  drawn by `Hud` over the whole screen, the small views' row and the rest of the HUD behind it,
  only the credits and a menu in front of it, taking no clicks: the picture breaking into static, clearing as it connects, `Scene.LinkUpSeconds`
  (1 s) of real time in all, the same however soon the physics is made; the vessel is flown, its
  body's physics made if it isn't, the frame after the link-up is first drawn, never the switch's
  own, and the link-up is longer only if that takes longer; that frame's input, gathered for the
  vessel left, gives the new one nothing of its view, plan, holds, rings or target), flavour only,
  telling the player nothing to do; the readout's `Link`. A small view given to a vessel at a body
  whose physics isn't made has its own link-up over it (`Flight.OpenView`, `ViewLink`): the HUD
  opens a view after drawing the others, so it is first drawn, with its static, the frame after
  the click, and the physics is made and the vessel brought in the frame after that, behind the
  static; until then the vessel is left under formula and witnessed by nothing, and a view closed
  meanwhile lets its link-up go (`CloseView`), nothing made; the readout's `Link <vessel>`.
  The main camera put on a vessel at a body whose physics isn't made has one over the whole screen
  likewise (`Flight.WatchLink`; the readout's `Camera link`).
- **The main camera's subject:** the main camera is on the flown vessel, or, by a right click,
  on another vessel, a body, a site or a point of interest (`CameraCentre`, `CameraSubject`:
  `Resolve`, `Seen`, its focus changed into the main view's frame in double). A place is looked at
  from beside it, a body from the map; M from the map gives the chase view of a vessel or a place,
  and of the flown vessel from a body; "Reset camera" (the strip's) and a right click on the
  flown vessel bring it back (`CentreKind.Own`), and so does losing what it was on
  (`CameraSubject.Kept`). A vessel watched is witnessed as a small view's is, but control never
  leaves the flown vessel: its warning, coming mark and compass tick stay its own, the rings are
  not drawn while the camera is elsewhere, and the keys steer as always. The map looks at what the
  camera is on, its angles from that body's north pole. Each vessel keeps its own view
  (`Vessel.View`, saved), given back as it is flown again.
- **Touchdowns:** a landing's touchdown speed (what its softness scores, and the HUD shows) is its
  hardest touch from its first touch to settling: on its feet, bell or body, in the water, or on
  another vessel. A new landing begins only once it has settled, or flown clear of the ground
  (1.5 m) with its engine lit; a bounce, a fall or a low hop is the same landing. Each touch is
  judged where that part touched, by how fast it closed with what it touched (a skid included), against
  its limit given the leeway of `Scene.TouchdownLimit` (touchdowns were once read 12 cm up, and
  landings aren't to be made harder yet: the user, 2026-11-07), from the touches: a touch on the ground or a raft is new only where that part (its feet and bell
  together, or its body) had no live contact with either. Its feet's are the legs' own touches
  (`RocketPhysics.FeetTouch`, from `Cushion`: the legs hold it up themselves, so Jolt never sees a
  landing on them). Its body's lowest point (`Vessel.HullContact`) and, its legs not out, its
  bell's rim (`Vessel.Measure`) coming onto the ground are judged from those too, as a part
  rocking down about a contact it already has gives Jolt no new contact (a body tipping onto its
  side, far end first). Those are measured only near the ground or while it touches anything
  (`Vessel.HullMayTouch`), and once landed, again only after it has moved or tilted a millimetre
  (`Vessel.KeptHullContact`: the ground doesn't move; near a raft, it is always measured); far from it, `Vessel.MeasureFar` reads one terrain height. A part
  touching the ground or a raft where it already touches it (sliding into a pad's side) is no new
  touch, but hit faster than `Vessel.ImpactSpeed` (15 m/s) straight into it, it is "Hit the
  ground". The tower and the masts wreck it on any touch.
- **The sea:** the sea is water, never a solid. `Terrain.SurfaceHeight` (once `SolidHeight`) is the
  higher of the ground and the sea's calm level (0): the calm sea, for paths, altitude and anything
  that has no clock. `Sea` is the one wave function (a fixed wave set over the whole planet, its
  phases at a world time, each sea's wave size where you are, the water's height and its flow); the
  planet's GLSL repeats it. The waves run on the world's clock (`Flight.WorldTime`), so what floats
  a vessel is what is drawn. `RocketPhysics.Float` floats a craft by slices down its length
  (`Scene.WaterSamples`): each is pushed up by `Scene.WaterBuoyancy` by how far under it is
  (against gravity and the turning ground's outward pull together, so off the equator a craft
  isn't pulled along the sea towards it), and
  slowed by `Scene.WaterDrag` against the water's own flow, so the waves rock the craft and the
  water resists its turning; `Scene.WaterEndDrag` damps its plunge along its length. A point over a
  raft (`Rafts.Under`: a floe or a drone ship) is on it, never in the water, for the floating and
  the water's rules alike (`RocketPhysics.DepthUnder`). The flow only goes to and fro, so a craft
  stays about where it came down (a drift waits for currents and wind). A craft afloat never rests,
  so it freezes by distance instead: it wakes within `Scene.FloatWakeDistance` of the vessel being
  flown and freezes beyond `Scene.FloatFreezeDistance` (further, so one at the edge isn't frozen and
  woken over and over); one still settling in the water (`Craft.Settling`) never freezes. The rules
  are in `Vessel`: `FlightState.Floating` is a vessel settled afloat (slower through the water than
  `Scene.FloatSettledSpeed` and turning slower than `Scene.FloatSettledTurnRate`, both scaled up by
  `Sea.WaveScale` in a rougher sea, and not top-heavy (`Scene.FloatTopHeavy`), for
  `Scene.LandedSettleSeconds`). The first part of a vessel into the water is judged by its whole
  speed against `Scene.SafeSplashdownSpeed`; later parts only by their speed across its axis, so it
  can come down upright and tip onto its side. The seabed is ground, so in shallow water the
  ground's rules hold (landed resting on it however it lies, wrecked by coming down on it too hard).
  Nothing steers a vessel in the water unless its throttle is over `Scene.SurfaceSteeringThrottle`.
  A splashdown scores as landing safely; `LandedOnTarget` doesn't count it (the HUD calls it
  "SPLASHDOWN OFF TARGET" where the mission wanted a landing site).
- **Worlds:** a world carries on from flight to flight and is saved (`WorldStore`, one JSON file
  each, in `WorldStore.FormatVersion`: a file in any other is listed as unreadable). Each vessel is
  saved by its body (`VesselState.Body`, by name), its place, attitude and velocity in that body's
  frame (the place and velocity in double: flying, its origin is placed from its centre of mass in
  double, `Vessel.TrueCentreOfMass`, so that comes back exactly, the physics' copy or the
  formula's place), and comes back into that body's physics, or under formula there. Anything new about a
  vessel that should survive a save goes in `VesselState`, in `Vessel.Capture` and in the
  `Vessel` constructor that restores one (the store tests check every field round-trips): so
  far, besides its motion, fuel and inputs, its hold (`VesselState.Hold`, a `HoldMode`, with the
  rings' angles, `RingHeading` and `RingPitch`), its nodes (`Chain`, a `ManoeuvreChain`: its
  anchor and each node's gap and amounts, with `MatchHeld`) and its view (`View`, a `CameraView`:
  map or chase, each one's angles and zoom, and what the camera is centred on, a `CameraCentre`;
  free look is not kept). The
  missions in progress are saved too (`RunState`, `WorldState.Runs`: each run's mission, site,
  start time, steps done with their points, the vessel flown last of it, and its flight number,
  which is all that ties a vessel to it: its vessels are the world's of that number, `Vessel.Flight`;
  a run ended is not kept), with the vessel flown (`WorldState.Flown`). `Flight.Restore` brings a
  world back as saved and starts no flight (`Flight.Load` brings it back looked at from the pad
  and starts a mission): `Flight.Chosen` is false until the player picks what to fly (a new
  mission or one carried on, `Flight.CarryOnChoices`), and a world saved before then is saved as
  it was, without the rocket of free play put behind the menu meanwhile (the placeholder: never
  saved nor counted in the world's numbers, taken away unlogged once a choice is made). The rewind
  point (`Flight.CanRewind`, `FlightInput.Rewind`) is the world captured at the last menu choice
  this session (a mission begun, begun again or carried on): never saved, none after a `Restore`
  until a choice is made, so never earlier than the load. Missions and vehicles are saved by name:
  renaming one makes old saves unreadable, which is fine (see below). A world holds
  `Flight.Capacity` vessels (1,000, under the physics' 4,096 bodies (`RocketPhysics.MaxBodies`)):
  past it debris (nothing guided) goes first, then vessels of no run in progress, then runs'
  vessels, the oldest first in each and never the flown one, whenever vessels are added or a world
  loads. The player's `Settings` (the same in every world) are saved beside the worlds, in
  `player.settings`; a new one goes in `Settings` and the settings screen.
- **The planet turns, and the world's frame turns with it:** each body's Jolt world is its
  ground's frame (ground-fixed: the pad, the sea and the terrain don't move in it), and each body
  turns about its own `NorthPole` once every `TurnSeconds`: the planet's (`Planet.NorthPole`,
  `Planet.TurnSeconds`) as here, the moon once a lap. A day on the planet (`Scene.DaySeconds`) is
  a little longer than its turn, as it goes round the sun too. The pulls, conversions and tests
  below are each body's members (`GroundBody`), named here by the planet's. The sun moves: it is `Sun.Direction(flight.WorldTime)`
  (in the ground's frame), and anything lit by it or asking whether it is day reads it from that,
  at the world's time, never a constant; a place's time of day is `Sun.LocalSeconds` (by its
  longitude). The direction itself comes from `SolarSystem`'s paths: the planet round the sun, and
  the year among the stars derived from the day and the turn so that the day is exact. The moon's
  place is `SolarSystem.MoonCentre` (in the ground's frame) and its local time
  `SolarSystem.MoonLocalSeconds` (by the true sun seen from the moon, `SunDirectionFromMoon`, as `Sun.LocalSeconds` is the planet's). A test that needs a time of day starts its world there (`WorldState.Time`, from
  `Sun.WorldTimeFor`), never by waiting. As the world's frame turns, `RocketPhysics.Fall` adds
  the turning frame's pulls (`Planet.TurningPull`: centrifugal and Coriolis, stepped as a Boris step
  so an orbit doesn't drift in size), and above the air (`Planet.AboveAir`, the one test for it) a craft keeps its
  attitude among the stars (a guided vessel's `Vessel.Steer` turns its aim back by the planet's
  turn). Positions and velocities in the game are over the ground; an orbit is worked out among
  the stars: convert with `Planet.AmongStars` and `Planet.OverGround` (velocities), and
  `Planet.Turned` / `TurnedDirection` (a place or direction after some seconds of the turn;
  `Planet.GroundSpin` is the ground's own velocity at a place). `Orbit` takes a stars-frame
  velocity and a `from` offset (how long from now it starts, for a node or a target's site that
  has turned in the meantime); `Orbit.OverGround`, `GroundAt` and `Landing` turn what it finds
  back into the ground's frame, so a path's ground mark and a landing site are where the ground
  will be, not where it is. An `Orbit` is round one body (`Orbit.Body`, in its frame, by its
  pull alone); a path across the spheres of influence is `Patched.Legs`, an orbit round each body
  in turn, patched at each child's sphere from the list (`Bodies.All`), as the plan's legs are
  (`ManoeuvrePlan`). The holds (`HoldMode`, one at a time, chosen on the HUD's buttons through
  `FlightInput.ChooseHold`: no keys) are prograde and retrograde, by the velocity in the frame of
  reference in use (`Flight.FrameVelocity`; `Frames`: surface, orbital or target), which the
  travel markers show too; manoeuvre, along the next node's burn still needed (only with a node,
  letting go once the last is gone); and the rings' (`Rings`: a click on a ring sets its angle,
  `FlightInput.RingHeading` and `RingPitch`, by the body the vessel is at), each let go by
  steering with the keys or a ring's drag; a vessel nobody flies holds by its own `Vessel.AutoFrame`. Anything new
  that predicts a path, aims a burn or reaches a place must ask which frame it is in.
- **Frames of reference:** the player picks one from the HUD's selector (`Flight.FrameChoice`:
  Auto, Surface, Orbit, Target); `Flight.Frame` is the one in use. The frames follow the body the
  flown vessel is at (`Flight.Body`): Surface is over its ground, Orbit round its centre among its
  stars, and Auto (`Frames.Auto(body, …)`) goes by height, not by the orbit: the height of the
  vessel's centre of mass over its body's datum (`Altitude`, the sea's zero; never over the ground
  under it, so a low orbit stays orbital over high ground), against `GroundBody.FrameSurfaceHeight`:
  the higher of the body's orbit floor (`GroundBody.OrbitFloor`: the air's top, or with no air its
  highest ground) and `Scene.FrameSurfaceWithin` (0.3) of its datum's radius (the room to bleed off
  a body's orbital speed grows with that speed, v² = gR, so with its radius). The planet's is its
  air's top (3 km, over the share's 2,990 m), the moon's its highest ground (822 m, over 750 m), so
  it is never orbital low over the moon's mountain; the share takes over on a bigger body. Orbital
  once above that height by `Scene.FrameOrbitMargin` (100 m), surface again under the height itself
  (held between, so it doesn't flip-flop at the edge; landed or afloat always surface). So the
  ascent's upper part and the circularising burn have orbital prograde, and the way down and the
  landing surface prograde. The HUD's and the readout's numbers are that body's too (heights over its datum, its orbit, local time, the
  compass's north), never the planet's by assumption. The holds follow the frame in use (floor
  5 m/s; 0.2 m/s in Target against a flying target or in orbit, `Flight.HoldFloor`), as do the
  speeds and the travel markers, but not the paths: every path (the flown vessel's, its plan's, a
  flying target's, the map's other vessels') is drawn among the stars round the body it is at, in
  every frame (`Paths.Build`, `LegPlace`), predicted from the centre of mass, so an orbit is its
  own ellipse (or hyperbola) closed on itself, never the track over the turning ground. A landing
  mark, and a later lap's, is the place on the ground it comes down on, as the ground is now
  (`Orbit.Landing`), so it reads against the sites; the line's end is where that ground will have
  turned to by then. Each leg of a patched path is drawn round its own body as that body sees it
  (`LegPlace`): placed whole by the change from its body's frame to the view's now, round that
  body as it is now (where it is drawn), so a pass by the moon is a hyperbola round it, an orbit
  round it a loop, or a line onto its ground, wherever the view is; and the other way about, flown
  at the moon, a leg round the planet is round the planet as it is now. The legs either side of a
  crossing don't meet (the body moves on meanwhile), and the line breaks there: each leg is its
  own stretch, nothing joining them (`Paths.LineStarts`, the map's runs).
  `Flight.RelativeSpeed` and `TowardsTarget` are to the picked target (a vessel, a place, or a
  body other than the one the flown vessel is at, `TargetKind.Body`: its centre), located in the
  flown vessel's body's frame across the bodies (`Flight.Locate`); `DistanceToTarget` is the
  straight line to it (nothing is targeted by default), and `DistanceToLandingSite` the
  scoring's. Whole flights by frame are in `FrameFlightTests` (`FramePilots.cs`).
