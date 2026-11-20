# CLAUDE.md

GameOne is a small C# (.NET 10) game built directly on the BRUTAL framework
(https://github.com/ahwootech/BRUTAL): an arcade rocket game in the third person, on a tiny planet
with 1 g at its surface. Four missions: launch a booster and land it on a landing zone; fly a
two-stage rocket into a stable orbit (and, as a bonus, fly its booster back to the drone ship
downrange); put a satellite into orbit (raising it to 10 km with its own engine) and land the
booster and upper stage; or carry a lander to the moon and land it as near its landing spot as
can be, then open its bay doors; and free play, the satellite launcher with no objectives. A
rocket that comes apart becomes several vessels, flown one at a time (Tab). Missions are flown in
saved worlds that keep everything left in them from one flight to the next (picked from a world
list at start-up). A mission belongs to its vessels, several in progress in a world at once,
saved with it and carried on after a load from the carry-on screen. It uses Jolt physics, Vulkan
rendering (sun shadows, planet, sky, smoke, fire, lens flare), OpenAL positional audio, and an
ImGui HUD crediting BRUTAL. See `README.md` for what each file does, the controls, and the
planet's and rockets' numbers.

This repo is public on GitHub (`origin`, github.com/aabaaiaaa/GameOne): only `main` is pushed,
and only when the user asks. Its commits carry the public identity set in the repo's own git
config, never a work name or email; nothing private (names, emails, paths, keys) goes into a
tracked file.

## Build and run

```sh
dotnet build GameOne.slnx
dotnet run --project tools/TestRun                 # the tests, split across processes (see tests/GameOne.Tests/CLAUDE.md)
dotnet run --project tools/TestRun -- --before-merge   # and the long ones: before a merge
dotnet test GameOne.slnx          # the same normal tests, one process, serially (a few minutes)
dotnet run --project src/GameOne
```

- **The local BRUTAL checkout:** BRUTAL is referenced as *source* from `../Brutal` (`$(BrutalRoot)` in
  `Directory.Build.props`), on that clone's `local-build` branch. Upstream BRUTAL ships no project
  files, so `local-build` adds a hand-written `.csproj` per library; dependencies mirror the published
  `Ahwoo.BRUTAL.*` nuspecs.
- **Adding a library:** a new BRUTAL library needs a csproj in `../Brutal` (committed on `local-build`),
  an entry in both `../Brutal/Brutal.Local.slnx` and `GameOne.slnx`, a `ProjectReference` here, and its
  `LICENSES/` folder added to the copy list in `GameOne.csproj`.
- **`Directory.Build.props` (in `../Brutal`):**
  - applies `[assembly: DisableRuntimeMarshalling]` to every BRUTAL project, as the published
    packages do; the generated `LibraryImport`s need it.
- **`Directory.Build.targets` (in `../Brutal`):**
  - native binaries aren't in the BRUTAL repo, so they come from NuGet:
    `<PackageDownload ... BrutalNative="true" />` copies `runtimes/<rid>/native/*` to the output.
    Since v2026.10.1 they are in their own `Ahwoo.BRUTAL.<X>.Native` packages (not in
    `Ahwoo.BRUTAL.<X>`), so after an upgrade the pins name the `.Native` id at its own version;
    a pin left on the main package builds clean but warns "No win-x64 native binaries found" and
    the game can't find its natives;
  - `BrutalAnalyzers="true"` feeds a package's analyzers or source generators to the compiler (needed
    for anything built on `Brutal.Interop`).
- **Tests:** `tests/GameOne.Tests` flies whole missions headless (see "Verifying changes" below,
  and its [`CLAUDE.md`](tests/GameOne.Tests/CLAUDE.md) and README).

## Layout and conventions

- **The renderer:** `Rendering/VulkanRenderer*.cs` is one class split by concern: core/frame loop,
  `.Swapchain`, `.Pipelines`, `.Shadows`, `.Sky`, `.Smoke`, `.Glow`, `.Flare`, `.Views` (the small
  views, drawn into the scene pass after the main view, each with its own uniforms' descriptor set).
  It is handed a `FrameInput` each frame and keeps no game state. Which vessels have views is
  `Views.cs`; where they go on the screen is the HUD's (`Hud.Strip`), so it is laid out before
  rendering. GLSL lives as C# raw strings in `Rendering/Shaders.cs` and is compiled at start-up by
  `Brutal.ShaderC` (no Vulkan SDK needed).
  **What is drawn at every body:** the renderer draws in the scene's frame, the planet's ground
  frame (the terrain, the sea, the sites and the sky are its), while each vessel, its camera and
  the HUD are in the frame of the body it is at. Each body's change into the scene's (and into
  the main view's, the flown vessel's body's) is worked out once a frame, in double, on first
  asking (`BodyFrames`), and every vessel's stages, engine, plume and landing lights changed by
  its body's (`SceneInput`); explosions and splashes are kept in their body's frame and drawn
  changed (`Effects`; at a body with no air, its flash and fireballs alone, no pall); the trail and
  the ground smoke are the planet's air's, given the scene's places (made only for a vessel at
  the planet); sounds are played changed into the listener's frame. For
  precision (the moon is 40 km out, where a float's step is 4 mm), what is hard-edged and seen
  close is given from the camera, worked out in double first: each stage's model from the main
  view's camera (`FrameInput.Parts`), the moon's centre from each view's own camera; the
  renderer pushes every mesh's model from the bound view's camera (`FromEye`, `BindView`) and
  the shaders place it from there (`meshModel`), so floats are only small offsets. The markers,
  the paths' lead-ins and the small views' marks are changed into their view's frame, and
  hidden behind any body (`BodyFrames.Behind`). The main camera is changed across as the flown
  vessel's body changes (`ChaseCamera.Rebase`, at the frame's time: place and view kept, its up
  and trail eased to the new body's over `Scene.CameraBodyEaseSeconds`), each small view's as
  its vessel's does (`Views`), and the paths' window dropped (`Flight.ViewRebased`). **The depth
  is reversed** (D32F, 1 at the near plane falling to 0 at the far one, which is at infinity:
  `ChaseCamera.Planes` and `Projection`, `VulkanRenderer.CameraProjection`), so its precision
  follows the distance (a float's relative step) at every zoom, from the pad to the map's
  furthest, with the near plane at `Scene.CameraNearPlane` (0.5 m) everywhere: the scene pass's
  depth is cleared to 0 and every pipeline but the sky's and the shadows' tests `Greater`; the
  seabed's push takes a constant off its depth (as far back as the square of its distance over
  the near plane's, so ground the coarse squares raise under the drawn water never shows through
  it far off); anything new that writes or tests depth goes
  by that. The shadow map is its own, not reversed (cleared to 1, `Less`, with its own depth bias).
  The sky writes depth (its compare always passing, drawn at 0, `depth_greater`): the moon's
  where it covers a pixel (`gl_FragDepth`, at a sphere under its lowest ground by two of the
  depth's own steps, at least a metre), else the sun's where its sphere does, else the far
  plane's, so whatever is behind either is hidden by it from anywhere, and a small view's sky
  replaces the main view's depth under it with no clear; up close the moon's mesh writes its
  own. The sun's disc is drawn where the sun is from each view's camera (`FrameInput.SunDisc`,
  in double: from the map's furthest zoom, millions of metres out, it isn't the planet's way to
  it; the eclipse's dimming and the corona go by it too), and the lens flare's probe sits just
  in front of its sphere.
- **Game code:** `Flight` holds the flight and its vessels, in its world, `Vessel` each one's rules
  and state, `RocketPhysics` a body's Jolt world (one `Craft` per vessel in its physics there; one world a body in use, `Worlds`), `Mission` what success means
  (and which `Vehicle` flies it), `MissionRun` one flight of a mission (its own vessels, the rocket and the
  parts off it, by which its steps are judged whichever is flown; any number in progress at once,
  `Flight.Runs`, the flown vessel's `Flight.Run`), `MissionProgress` a run's steps done (each stays done, with
  its points as they were then, whatever happens after), `GroundBody` a body's shape, gravity, air
  and orbits. A new mode should be a new `Mission` where it can be. `Flight`'s properties are the
  controlled vessel's. `Flight.Vessels` is the flown vessel's flight's (what Tab goes round);
  `Flight.WorldVessels` is everything in the world, earlier flights' included: anything that must
  work for every vessel (sounds, effects, plumes, drawing) iterates that.
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
  `Flight.WarpHeld`), Faster past the physics' top rate does nothing; one in the air seen, or
  switched to, at a formula's rate drops the warp before the frame runs. So does a fast collision
  coming at a witnessed vessel (`CollisionHolds`: warned of by the physics, or one the warp
  dropped for still coming and closing past `Scene.FastCollisionSpeed`), until it has hit or
  passed, so its slow-down plays out in full; its warning (or the drop's line) says why. At a
  formula's rate a link-up waits on its body's physics made ahead (`MadeFor`), so the drop never
  makes it in a frame. **The drops** (`WarpDrop`: `Air`, `Ground`, `SetMoving`,
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
- **No backwards compatibility (yet):** the game is early in development and features are expected
  to need game-wide refactoring. Don't keep old code paths, shims or migrations alive for
  compatibility: refactor freely. Tests that become redundant can be deleted, and tests whose
  expected outcome changes should be changed to match. Saved worlds from older builds may simply
  stop loading: an incompatible file is listed as unreadable (to delete) or ignored, never
  migrated, unless the user asks for that.
- **One set of controls:** the controls are a single set shared by every mission, growing as
  missions are added. No mission gets special handling (an auto-level or tilt limit for landing,
  say): steering allows only what is physically realistic (turn rate set by the engine and
  thrusters, the air turning the nose). Put new handling rules in `Flight`/`Vessel` for every
  mission, never as `Mission` flags. Only a genuinely different kind of control (driving a rover
  rather than flying a rocket) would justify different controls, and that needs discussing with the
  user first. Document any new key in the HUD's controls list and the README.
- **Keys are for flying; the game is in the menu:** keyboard keys control the vessel (and flying
  aids such as slow motion on `,` / `.`). Anything about the game rather than the flight
  (restarting, missions, worlds, quitting, settings) goes in the game menu, which Esc opens. Esc
  does nothing else: it pauses and opens that menu, and is the only way to pause. The one
  key that is about the screen instead is F2, which hides the whole HUD for a screenshot (`Hud.Hidden`);
  it changes nothing in the game, and a menu still shows over it. A vessel's own actions (its bay
  doors, its legs) are on its menu, a right click on the flown vessel in the main view
  (`VesselActions`, `ActionMenu`, drawn by `Hud`): a new action goes there, into the flight
  through `FlightInput` as a key's would, and gets a key too only if it is for flying (the legs
  keep G; the bay doors have none).
- **Low hand-holding:** cues for the player (the HUD, markers, marks on the path, guides) give the
  information an experienced player would still use, and never tell the player what to do or
  when: no "burn now" or "stop now" prompts. A transfer window is a shaded stretch of the orbit,
  not a moment; a manoeuvre node's guide shows the delta-v left and the burn time, and the player
  starts and stops the burn. So too the marks where a target's plane crosses the path (AN and DN,
  with the angle between the planes: where, never when), the glows travelling along each orbit
  line (which way it is flown), the manoeuvre mark in the view (the way the burn still needed points) and the
  next node turning green as the path flown matches its plan: information only, and nothing
  burns for the pilot (the manoeuvre hold only points the nose). New cues (the moon, docking, RCS, weather) follow the same rule. A
  mission's points of interest (`Mission.PointsOfInterest`, `PointOfInterest`) are markers and targets: a
  place on a body's ground shown by a marker (and an arrow off the screen) and built nowhere, a
  target as a site is (`TargetKind.Point`, by its index in the flight's mission's list) and never
  an instruction; only the flight's own mission's are gathered.
- **Branches and merging:** a feature is built on a branch of its own (a worktree under
  `.claude/worktrees/<name>`); one in several parts gets a commit per part, and is merged into
  main only once every part is done and the soak tests pass. Commit and merge only when the user
  asks. TODO notes and small doc fixes can go straight onto main. A merge is usually a
  fast-forward, which leaves no merge commit: say so, as it can look as though nothing was merged.
  Afterwards, the worktree and branch are removed (on asking).
- **Specs and plans are transient:** a feature's design and plan files (`docs/superpowers/`) are
  working papers for building it, not part of the source. Once its work is complete they are
  removed before the merge (git history keeps them). Nothing that stays (code, comments, a
  `CLAUDE.md`, a README, `TODO.md`) points to one or relies on it: what must outlast it (how
  something works, a rule, a decision still in force, an unbuilt part) is written where it
  belongs first.
- **Ideas for later:** agreed-but-unbuilt ideas live in `TODO.md`. Most say "to discuss before
  building": raise them as a discussion first, with design questions asked one at a time, and
  record the decisions there. Check it when asked what's next, and keep it up to date as things
  are built. Performance ideas found but not built (while cutting a cost, say) are recorded there
  too, each with what it would gain, what it would cost or risk and which case it would help, so
  they can be tried later.
- **Nothing deferred is lost:** anything found on a branch (a review's minors, a known gap, a
  follow-up) is fixed on that branch, before it is merged, not put off: the plan's ledger and the
  worktree are scratch and go with it. Only a design question or a future feature goes into
  `TODO.md` ("to discuss before building"). Say in the report where each item went; remove an
  item from `TODO.md` once it is built.
- **Where to keep what Claude must know:** project rules and preferences go in a `CLAUDE.md`, not
  in Claude's own memory. This one keeps to what holds across the project; the knowledge of one
  area (its how-tos, its quirks, its numbers) goes in a `CLAUDE.md` in that area's folder, read
  when working there (the user, 2026-11-03): so far [`tests/GameOne.Tests/CLAUDE.md`](tests/GameOne.Tests/CLAUDE.md) (the flight,
  soak and performance tests) and [`tools/GameDriver/CLAUDE.md`](tools/GameDriver/CLAUDE.md) (driving the running game, the
  HUD's readout). This file keeps a line or two on each, with what must be known before going
  there (when to run what, what never to do), and points to it. A new area's knowledge, or
  this file's grown long about one area, starts one there.
- **Tunable numbers:** layout and tuning values (sun, sky, pads, booster, flight handling, camera,
  smoke) live in `Scene.cs`. Put new tunables there.
- **Units:** everything (scene, physics, audio) is in **metres**, with the launch pad centred on the
  origin, on top of the planet (whose centre is at `(0, -PlanetRadius, 0)`). A stage's own space
  has its origin at the bottom of its body.
- **The bodies, and which body a read asks:** the bodies are a list (`Bodies.All`: the planet and
  the moon for now), each a `GroundBody` in its own ground-fixed frame; nothing is written for
  exactly two, and nothing asks for "the other one". Every read of the ground, the sea, the air
  and the sites says which body it asks, by asking the body it is about (a vessel's `Vessel.Body`,
  a physics' `RocketPhysics.Body`, an orbit's `Orbit.Body`, a tile job's, a search course's):
  never a body "stood on". A physics is made for a body (`RocketPhysics(GroundBody)`), any number
  side by side (Jolt's state, the job system and the one tile worker shared: `JoltShared`).
  `Planet` (and `Terrain`, `Sea`, `Rafts`, the sites) is the planet's own, fixed, for what is the
  planet's alone; the sea and rafts are read only where `HasSea`, the sites only where `HasSites`.
- **Which way is up:** +Y is up only near the pad (each body's frame has its own origin on its
  ground, up +Y there). Anywhere else, use the body's `Up(p)` (and `East` / `North`), and its
  `SurfaceAltitude(p)` for height above what's underfoot, the calm sea over water (what the HUD's
  Ground and paths use). `GroundAltitude(p)` is the height above something solid (the pad, the
  ground or the seabed): contact, the legs and the ground's tiles go by that. Every height is
  above the body's zero (the planet's sea, at 0, `SeaRadius` from the centre): `Altitude`, the
  terrain's, the sea's, the air's and orbits'; Home's pad (the planet's origin) is
  `Scene.HomeElevation` (35 m) up. `Radius` is only the geometry of the sphere through the
  origin. Gravity and drag are applied per body in `RocketPhysics`, by its body's pull and air:
  Jolt's own gravity is off. **A craft's place and velocity are kept in double** by the physics
  (`Craft.KeptCentre`, `KeptVelocity`; read by `RocketPhysics.TrueCentreOfMass` and
  `TrueVelocity`): Jolt is single precision, and a float state stepped step after step leans one
  way in a turning frame, so an orbit would creep. While a craft flies free (no live contact, not
  in the water, its legs not holding it) the game steps the copy (the fall's kick, the thrust, the
  drift, as Jolt would) and hands Jolt it rounded once a step (`HandOver`); anything else that
  moves it (a contact, the water, the legs, any set) is Jolt's, and the copy is taken from Jolt
  again whenever Jolt's state is no longer the copy's to its rounding (`Resync`, at each step's
  start and on each read). The copy is what a crossing, the edge's check, the handover to formula
  and saving take (`Vessel.TrueCentreOfMass`, `TrueVelocity`: the copy, or under formula the
  formula's double place, handed back into the physics as the copy, `RocketPhysics.Keep`, as a
  vessel loaded flying is); every other read may read Jolt's float, which is the copy rounded. Anything
  new that changes a free craft's velocity in the step goes into the copy too.
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
- **Matrices:**
  - Use `System.Numerics` with row vectors (`v * M`, so `model * view * projection`), uploaded as-is.
    GLSL's column-major read transposes them, so `M * v` in shaders is correct.
  - Use `CameraProjection` for a view's projection (`ChaseCamera.Projection`, its depth reversed and
    its far plane at infinity, with Y flipped, because Vulkan clip space is Y-down), and
    `PerspectiveVulkan` only for a floodlight's shadow tile (standard depth, as the shadow map's).
- **Math types at the boundary:** BRUTAL APIs take `Brutal.Numerics` types (`float3`, `floatQuat`); game
  code uses `System.Numerics`. Convert explicitly at the boundary.
- **Colours:** given in display (sRGB-like) values. Shaders convert to linear with `toLinear`,
  light, then tone-map back with `finish`, because the swapchain is UNORM.
- **Data layouts that must stay in sync:** push-constant and uniform structs must match their GLSL
  blocks exactly (`FrameUniforms`, `DrawConstants`, `FlareElement`, `SmokePuffConstants`,
  `GlowConstants`). The vessels' far glows are a storage buffer at descriptor binding 7 (`vesselGlows[]`,
  one vec4 a vessel, `Flight.Capacity` of them for each frame in flight: where, and how far on with its twinkle's seed, `VulkanRenderer.GlowW`), read by
  instance in the glow's vertex shader. The push range is 80 bytes, shared by all pipelines. `SmokeStirs.MaxStirs` must
  match `MAX_STIRS` in the GLSL. `FrameUniforms.WavePhases` (the waves' phases, filled from
  `Sea.Phases` at the world's time) is one of them, and `ShaderTests` checks the Frame block's
  members, offsets and size against `FrameUniforms`, and that every shader compiles. The Frame
  block's `moon` is the moon's centre from the view's own camera and its radius (worked out in double from each view's
  own camera, so it keeps its precision at any distance), and `atmosphere.w` is
  the sun's angular radius (`SolarSystem.SunAngularRadius`). The pad
  lights' arrays (`lightPlaces[32]` and kin) hold `PadLights.MaxLights`, the pad lights capped at
  `MaxLights - Scene.LandingLightsLit` with the vessels' lights after them, and `spotViewProj[SPOT_TILES]`
  holds `PadLights.MaxShadowed` for each of `VulkanRenderer.ShadowAreas` blocks; the site arrays
  (`padSites[8]` and kin) hold `VulkanRenderer.MaxSites`, and the renderer refuses to start with
  more sites. The shadow map is laid out in 1024² cells, 11 by 3 (11264 x 3072, 132 MiB at
  32-bit depth), none wasted: the main view's sun area two by two cells (2048²) in the top left,
  then every other cell one each (the two under it, then column by column to its right): first the
  small views' own sun areas (`ShadowAreas - 1`, the GLSL's `SHADOW_AREAS` being
  `1 + Scene.MaxSmallViews`, one per view; each 1024² over the same ground a main view's would cover
  there), then the floodlight tiles, `PadLights.MaxShadowed` to a block, one block per light set
  whose floods cast shadows (`LightSet.Block`). Tile `MaxShadowed × block + i` is
  `spotViewProj[MaxShadowed × block + i]`, and while it is drawn every draw carries that index in
  its material's w (`_spotShadow`); a view's `lightCount.z` is its block's first tile. Each tile's
  square is the GLSL's `spotTiles[SPOT_TILES]`, written from `VulkanRenderer.SpotTileRect`; each
  view's own sun area is its `lightViewProj` and `sunArea` (the Frame block's), from
  `FrameInput.SunAreas` (`SunAreas`, chosen in `GameView`: the main view's first, then one per small
  view whose vessel isn't `Scene.SunAreaMargin` inside an area already chosen, held a
  `Scene.SunAreaHysteresis` further while it shares; a view of a wreck never has its own). Each
  lookup is kept a texel inside its own square, and the shadow pass clears and draws only the
  squares in use (each sun area's casters
  only what is in it, drawn from the uniforms of the first view it is chosen for). `ShaderTests`
  checks `SHADOW_AREAS`, `spotViewProj`'s length, `spotTiles` and that the squares fill the map
  without overlapping; the layout is set out at the top of `VulkanRenderer.Shadows.cs`, and the
  renderer refuses to start on a device whose images can't hold it. Each view's place whose floods
  cast shadows is held until another is nearer by `Scene.ShadowPlaceHysteresis` (`Views` keeps the
  main view's from frame to frame, each `Views.SmallView` its own, and a swap hands each on with
  its vessel).
- **The pad lights:** where every lamp and mast is, `PadLights` (each `LitPlace`: a pad, a zone or
  a drone ship, in its own frame), works out from the sites and the terrain, as `Foundations`
  does; physics makes the masts solid ("Hit a light mast.") and the renderer draws them, their
  light and their far glow. They are part of the world, never a cue: on in each place's own dark
  (`Sun.IsDark` there: below `Scene.DuskSunHeight`, until above `Scene.DawnSunHeight`, or in
  another body's shadow: less of the sun seen past every other body than `Scene.EclipseDarkSeen`,
  until more than `Scene.EclipseLightSeen`, `Sun.Seen`; a body with air by the brightness of the
  light it lets past, `SolarSystem.ShadowLight`, what the moon's ground is drawn lit by), faded on
  and off over `Scene.PadLightFadeSeconds` of real time by `PlaceLights` (`Flight.Lights`, a lit
  fraction per place by its `PadLights.All` index, stepped once a frame in `RunFrame`, so neither
  the warp nor slow motion changes it; a world loaded at night starts lit, nothing saved). A place
  at 0 hands in no lights and shadows nothing (the next lit place takes the floodlight shadows);
  the glow (`FrameInput.PlaceLit`) is none at 0. The lamp faces' colour is in their meshes
  (`Meshes.BuildLamps`) and `Material.w` carries the place's fraction, by which the
  shader mixes the face's ordinary lighting toward its shine: off by day it is an unlit surface.
  Each view is lit by the place nearest it (the main view's camera, each small view's vessel:
  `PadLights.Nearest`, each with its own held `shadowPlace`), chosen each frame by `LightSets`
  (allocating nothing): views at the same place share one `LightSet` and one block of floodlight
  shadows, so only places apart cost more, and a view near no lit place has a set of its own
  (never shared, with no floodlight shadows). The set's pad lights are capped at
  `PadLights.MaxLights - Scene.LandingLightsLit`, with the landing lights of the nearest vessels
  (`VesselLight`) appended after them (see the landing lights).
- **The landing lights:** a vessel's own (`Vessel.LandingLights`, a fraction; `HasLandingLights`:
  the booster, the upper stage, the satellite and the lander, not a fairing half), by the same rules as the pad
  lights, so they are part of the world and never a cue: on while it is dark where it is
  (`Sun.IsDark`, held between the dusk and dawn heights) and near the ground (landed or afloat, or
  below `Scene.LandingLightsBelow` above what is underfoot, held up to `Scene.LandingLightsOffAbove`),
  not once it has crashed (off at once, no fade, and nothing asked of the sun or terrain for a
  wreck or a fairing half), faded over `Scene.PadLightFadeSeconds` of real time by
  `StepLandingLights`, which `Flight` calls for every vessel in the world once a frame. Nothing is
  saved: they are worked out again where it is and the time (`ResetLandingLights`, for a world
  loaded, a stage dropped and the new rocket, `StartFlight`). `SceneInput.Gather` collects every lit vessel's
  light (`VesselLight`: at its bell's foot, aimed down its axis, by its fraction, in the scene's frame) into a fixed
  array, with no allocation, for `LightSets.Choose`, which lights each view's ground with the
  nearest `Scene.LandingLightsLit` within `Scene.LandingLightReach` (no shadows), and for
  `FrameInput.VesselGlows`, where every one, near or far, is a far glow: one instanced draw a view
  (`DrawGlows`, glow kind 3), the quad sized in the vertex shader as the lamps' glows are.
- **The terrain's detail, in C# and GLSL:** `Terrain.Detail` (the hash, the value noise and the
  per-kind octave table `Scene.TerrainDetail`) is repeated in the planet's GLSL
  (`Shaders.SceneFragment` and the vertex shader), so the drawn ground matches the collider. Change
  one and change the other. The same goes for `FrameUniforms.Terrain` (map resolution, lowest and
  highest height, `Scene.DetailScale`, the scale the fine detail's noise is laid out on) and the patch encoded in
  `DrawConstants` (`Model` row 0 = face, u0, v0, size; row 1 = skirt depth, level-of-detail ratio).
- **The sea, in C# and GLSL:** `Sea`'s wave function (`Wrap`, `WaveScale` and `Height`) is repeated
  in the planet's GLSL (`seaWrap`, `seaWaveScale`, `seaHeight`), so the drawn water is where the
  physics floats things. Change one and change the other. The wave set (`Sea.Waves`) and each
  sea's wave size (`SeaInfo`, from the map) are not repeated by hand: the GLSL is written from
  them, so a change there reaches both. The seas' colours (`SeaInfo.Shallow` and `Deep`) reach the
  GLSL as a map made at start-up (`SeaColors`, binding 12: blended where seas meet over
  `Scene.SeaColorBlend`, mipmapped), which `waterColor` reads filtered; the wave size is still
  each sea's own up to its border (the physics floats by it).
- **The air, in C# and GLSL:** `Air` (the model: the spheres, the phases, the haze and ozone profiles,
  the sun's transmittance and the sky's light) and `AirTables.Look` (the lookup of its tables) are
  mirrored by the shaders' air code (`Shaders.AirLookup`: `airTransmittance` and `airSkyLight`, the
  line-for-line twins of `Look`; `Shaders.AirLight`: `airMarch`, `sunlightAt`, `skyColor`, `fogTerms`
  and `skyLightAt`, the twins of `Air`'s march). The tables are bound at 6 as `uAir`. Change one and
  change the other. The coefficients are not repeated by hand: the GLSL is written from `Scene`.
  `AirTableTests` checks the tables against the model (heights up to and above the air); the
  GLSL's formulas are checked only by compiling and by the screenshots. The averaged light's
  branch (the Frame block's `averaged`, from `FrameInput.LightAveraged`: `sunHeightAt`,
  `sunSeenAveraged`, `skySun`, `averagedSunlight` and its mixes into the ground's, the moon's and
  the smoke's light) is the GLSL's alone, with no C# twin: `Air`, `SolarSystem` and the eclipses
  stay the true light's, and only the pad and landing lights' dark share (`Sun.DarkShare`) is
  worked out in C#.
- **The moon in the sky, in C# and GLSL:** the sky's ray-cast of the moon (`Shaders.MoonFunctions`)
  reads its map (`MoonGround.Map`, heights and kinds bound at 8 and 9, read by gathers) through
  `FrameUniforms.MoonFrame` (the ground's frame into its own axes), and repeats `MoonGround`'s
  `BaseHeight`, `Albedo` and `SunSeenOver` (`moonBaseHeight`, `moonGround`, `sunSeenOver`), and
  `Normal` in a form of its own, not a twin (`moonSlope`, `moonNormal`: the map's heights alone,
  without the fine detail, read across the pixel's footprint rather than `Scene.TerrainNormalStep`,
  by direction, so no crease at a face's edge; the mesh adds the fine detail's slope to it), and
  `SolarSystem.PlanetShadowLight` and `Planetshine` (`planetShadowLight`, `planetshine`, the
  former reading the air's table at 6). The sun eclipsed, `SolarSystem.SunSeen` and `DiscSeen`
  (the share of the sun's disc the moon's sphere leaves uncovered, exactly 1 outside its shadow;
  the C# asks once a world time whether the shadow can reach the air at all, `NoShadowWithinTheAir`),
  is repeated in `Shaders.AirLight` (`sunSeen`, `discSeen`): it multiplies `sunlightAt` and
  `skyLightAt` at each place, and the sky's and the haze's light (`skyColor`, `fogTerms`) by the
  camera's, never per step of the march; the corona and the horizon's glow are the sky's
  (`SkyFragment`), and the flare takes the camera's (`Air.FlareDimming`). The moon's own face is
  dimmed by the air along the way to it only (`Air.MoonSeenThrough`, twin of `SkyFragment`'s
  `throughAir` and `dimmed`; 1 when that way misses the air), never by the ray's to infinity,
  which from beyond the moon meets the planet's sea behind it; the corona and the sun's disc keep
  the to-infinity one, as the planet hides the sun there. Change one and change
  the other; the numbers (the map's range, `Scene`'s) are written into the GLSL, not repeated by hand.
- **Timings:** `Timings` is the game's own diagnostic, in one place: where each physics step's
  time goes (`StepPart`: rules, Jolt, tiles, water, contact, the rest), each frame's (`FramePart`)
  and the GPU's passes (`GpuPass`, from timestamp queries: `VulkanRenderer.Timings.cs`). The
  settings' "Show timings" panel shows it, and the short performance tests read it. Off (the
  default) means not measured, not merely hidden: with it off each lap is one bool read and no
  clock is taken, so play costs nothing for it. A new costly part of a step or a frame gets a lap
  of its own (or goes into the part it belongs to), and the parts add up to the whole, so nothing
  is left out unseen. To see what within a part is the cost, a piece of it is timed on its own
  (`StepDetail`, with `Timings.Start` and `Stop`: the tiles' heights, bodies, additions (`Adds`: bodies
  laid from finished or kept shapes) and removals, the
  contact's movers, nearness, grounding, freezing, wrecks, touches and the vessels' own checks, and the
  rules' collisions ahead, `Collisions`, and the vessels under formula, moved and their collisions found, each apart), counted
  within its part, never beside it; the readout and the short tests' report show them, the panel
  doesn't (no room).
- **Code style:** match the existing comments. They explain *why*, especially workarounds for BRUTAL
  issues, so keep them accurate when changing code.
- **Comments only where they add to the code:** a comment says what the code does and why, as it
  stands. Never use one to record a change or its history: no dates, no "was once" or "the old
  ...", no quoting or crediting who asked for something. That belongs in the commit message
  or `TODO.md`. A reason a request gave can stay, said as the code's own reason.

## BRUTAL quirks (read before touching these areas)

- **Vulkan errors are exceptions:** `Brutal.Vulkan` throws `VkResultError` for `VK_ERROR_*` results
  instead of returning them, including `VK_ERROR_OUT_OF_DATE_KHR` from acquire/present on resize. Catch
  that specific error; don't check the returned `VkResult` for it.
- **The ImGui backend needs `DeviceEx`:** `Brutal.ImGui.Backends`' Vulkan backend needs a `DeviceEx`
  (from `Brutal.Vulkan.Abstractions`), and the ImGui context must exist before it is created. That's
  why `Hud` is constructed before the renderer and disposed after it.
- **Jolt's natives now match its bindings, but the game still works round the old gaps:** the
  `jolt.dll` of 2026.10.2 lacked 774 of the bindings' 3,246 entry points (ahwootech/BRUTAL#13);
  `Ahwoo.BRUTAL.Jolt.Native` 2026.10.4 (BRUTAL v2026.10.1) exports all of them, and mesh and
  convex-hull shapes, contact listeners (the managed proxy and the native `ContactEventListener`
  queue), `SetMassPropertiesOverride`, ray, shape and point queries, height fields given their
  samples, the direct `XShape.Create(size)` overloads, constraints and `Character` were each
  checked working in a small scene (2026-11-02). Until each is put to use (TODO.md), the game
  keeps its workarounds:
  - Shapes are built via `XShapeSettings.Create(...).CreateShape()`.
  - The booster's collider is cylinders and boxes in a `StaticCompoundShape`, and floes are discs.
  - Touchdowns, crashes and hits are judged in `Vessel.CheckContact` from Jolt's contacts (see
    "Vessels collide"), with the game's own care where Jolt says nothing: removing a sleeping
    body reports no contact ending (the game forgets a removed body's contacts itself), a frozen
    craft's contacts end in the step after it freezes, the legs' landing is the legs' own, and a
    part rocking down about a contact already held makes no new contact (see "Touchdowns").
  - Mass comes from shape volume and density. Thrust is applied as an acceleration
    (`AddLinearVelocity`), so it doesn't matter.
  - After a BRUTAL upgrade, check the native exports every entry point the bindings name
    (`NativeLibrary.TryGetExport` over the bindings' `EntryPoint`s), and that a call does what it
    should before relying on it: an export alone once ignored its arguments.
- **ImGui strings fill an arena that must be emptied every frame:** `ImString` (every interpolated
  string passed to ImGui) is copied into `ImString.SharedStorage`, a fixed 8 MB arena that BRUTAL
  never clears itself. `Hud.Build` clears it at the start of each frame; without that the game throws
  "Cannot grow beyond MaxCapacity" after a few minutes. Keep that call first.
- **OpenGL in the build:** `Brutal.OpenGl` is built only because `Brutal.ImGui.Backends` depends on it.
  GameOne renders with Vulkan only.

## Licence obligations (BRUTAL Licence v1.0): don't regress these

- **§2.1 and §2.2:** the HUD (`Hud.cs`) must keep showing the **unaltered** official logo image (scaling
  only) and the text of the shipped `NOTICE.txt`, from the first frame, in every menu (the world list
  the game starts on, and the game menu: the "Credits screen or equivalent" of §2.1), and for
  `Scene.CreditsSeconds` (5 s) once play first begins, then gives its room back (`Hud.ShowsCredits`):
  §2.2 asks for the logo before play begins, legibly. No menu screen covers it at any window size
  the HUD supports: each is placed (and narrowed if it must be) clear of it by `Hud.MenuAcross`,
  which a new menu screen uses too. F2 hides it with the rest of the HUD in flight;
  a menu still shows it. The logo wrapped round the
  booster is decoration and doesn't count for §2.2.
- **§2.3:** `NOTICE.txt` must stay copied to the output.
- **§4.0:** each used BRUTAL library's `LICENSES/` folder is copied to `LICENSES/` in the output.
  Add new libraries to that list.

## Verifying changes

There are two layers. Use both for gameplay changes. Each has a `CLAUDE.md` of its own, beside
it, with how to write, run and read it: read it before working there.

- **Flight tests (`dotnet run --project tools/TestRun`):** `tests/GameOne.Tests` steps the real `Flight`
  rules and Jolt physics headless, with autopilots flying through `FlightInput`. Run them with
  the runner (it splits the suite across processes; plain `dotnet test GameOne.slnx` still runs
  the same tests serially), and with `--before-merge` before a merge, which adds the long
  playthroughs and checks. Add a test for
  each new rule or mission; keep game logic reachable through `FlightInput`, and never put test
  code (autopilots, test switches) in the game itself. Run the soak tests once at the end of a
  change that could grow memory or what is kept over play; the performance test for anything that
  could cost frame time; the short performance tests only when working on performance, never as
  part of the normal suite. See [`tests/GameOne.Tests/CLAUDE.md`](tests/GameOne.Tests/CLAUDE.md).
- **The running game (GameDriver):** `tools/GameDriver` plays a script at the real game, for what
  the player sees and hears. Never drive or close the user's game (check `tasklist //FI
  "IMAGENAME eq GameOne.exe"` first), and run it with `GAMEONE_WORLDS` set to a new, empty scratch
  folder. The game meets it in one place only, the HUD's readout (`src/GameOne/Readout.cs`):
  something new the HUD shows that a script might fly by is added there. See
  [`tools/GameDriver/CLAUDE.md`](tools/GameDriver/CLAUDE.md) (the readings, where the buttons are, writing a script).
- **ImGui text:** `TextColored`, `TextWrapped` and `SetTooltip` are printf formats, so escape `%` as
  `%%`. Plain `Text` and `ProgressBar` overlays aren't, so don't escape those.
- **ImGui IDs:** a label whose text changes (a height, a fuel level) needs `###id`, not `##id`: with
  `##` the whole label is the ID, so it changes every frame and clicks on it never land.
- **Build servers:** repeated builds leave MSBuild worker `dotnet.exe` processes behind (~110 MB
  each), which add up. Build and test with `--disable-build-servers -nodeReuse:false`, and run
  `dotnet build-server shutdown` when done.
- **Logging:** temporary `Console.WriteLine` tracing is fine for checking behaviour. Remove it before
  committing.

## Gotchas when running locally

- **Stuck process locking the build:** a crashed Vulkan run can leave a zombie `GameOne.exe` (0
  threads) that can't be killed and keeps files in `bin/Debug/net10.0` locked; it clears on reboot.
  Builds then fail with MSB3021/MSB3027. Workaround: rename the locked files into
  `src/GameOne/bin/Debug/locked/` (renaming a locked file is allowed) and rebuild.
- **Build-error filters can miss locks:** when filtering build output, check for MSB copy errors as
  well as `.cs` errors. A failed copy means the previous exe ran.
- **Line endings:** git stores files with LF and checks them out with CRLF (`core.autocrlf`), so in
  the working tree they are CRLF and `sed` patterns anchored with `$` may not match. Keep a file
  CRLF when editing it (some tools write LF), and prefer exact-text edits.
