# The solar system's edge, and each vessel's place and speed kept in double

Branch `edge-and-precision`, built on `map-and-camera` (at 0545925), in the worktree
`.claude/worktrees/edge-and-precision`. Everything merges together after the user's play test.

## Why

1. **The edge.** An escape path today runs on towards infinity, and everything that walks or
   searches along it works with ever larger numbers. A hang has already come from one: drawing
   along a line about a billion pixels long. The user wants an outer limit far enough out to leave
   lots of room for future bodies, including a world of four solar systems round a central star,
   and still well short of where doubles lose precision.
2. **The orbit creep.** A vessel's place and speed are floats in Jolt, which is built in single
   precision. Each step's rounding is tiny, but in a body's turning frame it leans the same way
   lap after lap. The Satellites preset's 60° satellite loses about 0.2 to 0.27 m of semi-major
   axis a lap. TODO.md's "Orbits creep a little, from float rounding" describes it. The user wants
   it fixed now, without any change to BRUTAL.

## Part 1: the edge (the user's decisions)

- **Where:** a sphere `Scene.SolarSystemEdge` = **10,000,000 km** (1e10 m) in radius, centred
  on the sun: the star that nothing else orbits. Today that is the sun, which is not one of
  `Bodies.All` (its place is `SolarSystem.SunCentreAt`). In a world of several systems round a
  central star, it would be that central star. A double holds a position there to about 0.002 mm;
  a float only to about 1 km. For the sizes (my call, which the user took up): each system about
  30,000 km across, each about 1 million km from the central star, and the edge ten times past
  that.
- **A vessel that crosses it is removed from the game**, and the world log records it as lost:
  "Left the solar system". This holds for every vessel, the ones flown, watched and under
  formula alike. Its log entry has the same form as other losses (`WorldLog`, a new reason
  beside `RemovedForRoom` and `ClearedFromPad`, worded as one).
- **The flown vessel crossing it is treated like a crash** (the user): it is removed, but the camera
  stays where it was and the HUD shows "Left the solar system" as a crash's message is shown,
  until the player Tabs to another vessel or restarts, as after a crash.
- **Its path ends at the edge, with an icon at the end**, as a path ends at a collision with its
  mark (`Paths.CollisionMarks`): its own symbol, labelled "Leaves the solar system" (the label is
  my call). This applies to every path drawn: the flown vessel's, a target's, a manoeuvre plan's
  legs, the map's (`SystemMap`), and any search along a path (`Patched.Legs` and whatever walks
  it), so nothing runs on past the edge.
- **No warning and no warp drop:** the icon is the only cue. It is information, not an
  instruction (CLAUDE.md, "Low hand-holding").

### Notes for the builder

- The planet is the root of `Bodies.All`, and its pull alone moves a vessel round it, so a path
  leaving the planet is a hyperbola round the planet. The edge is round the sun's place at the
  time the path gets there (worked out among the stars, in double: the ground's frame turns, and
  far out a turned frame moves fast).
- With the sun 3,000 km from the planet, the sphere is nearly centred on the planet, but it is
  still the sun's sphere, not the planet's.
- Reaching the edge takes a long time. At the top speed (4,000 m/s), 1e10 m is about 29 days of
  game time, so in play it is reached mostly by vessels left under formula in long play. What
  matters to the player is the path ending there.
- Drawing a path out to 1e10 m must stay cheap: check how the path's points are spaced along a
  long hyperbola (their count, and the work done per frame), and that every float walk along it
  stays safe at those lengths (see the glows' walk in `Paths`).
- The check for crossing is once a step for every vessel, and should cost nothing for a vessel
  near its body (as `Bodies.ClearWithin` saves the sphere checks).

## Part 2: each vessel's place and speed kept in double (my design, agreed by the user)

- **The rule:** the game keeps its own copy of each craft's centre of mass and velocity, in its
  body's frame, in double (`Vector3Double`). While a craft flies free, the game steps its copy:
  the fall's kick (`GroundBody.FallStep`), the thrust, then the drift, as Jolt would. Each step it
  hands Jolt that copy rounded to float. Jolt's rounding is then once a step, with nothing carried
  over, so it never builds up: a jitter of about 2 mm, and no lean lap after lap.
- **Touching anything, Jolt's result is the truth:** when the craft touches something (the
  ground, a raft, the tower, a mast, another craft, the water), or anything else the game does
  sets its place or velocity directly (a stage parting, a raft carrying it, freezing and waking,
  a crossing into another body's frame, a world loaded), the copy is taken from Jolt again (or
  set with it). So landings, collisions, floating and parting work exactly as now.
- **Across a crossing** (`Flight.Cross`, `RocketPhysics.TakeOut`/`TakeIn`, `CraftState`), the
  copy is changed into the new frame in double (`Bodies.Change` already works in double) and
  carried across.
- **Under formula,** the Kepler path is already exact. Where the formula steps a vessel (in the
  air, burning, or low), it steps it in double too, so a vessel's creep doesn't depend on
  whether anyone watches it.
- **Saved worlds** keep the place and velocity in double (`VesselState`: no compatibility needed;
  bump `WorldStore.FormatVersion`). This is my call: saving in float would round once a save,
  which is harmless, but double keeps one rule.
- **Not changed:** the attitude and turning stay Jolt's (floats); Jolt itself is not changed.

### Checks

- The orbit creep test in `WorldTests` (the Satellites preset, three laps): its bounds tighten
  from "about 0.4 m a lap" to what the double copy actually holds, measured and stated, aiming for
  millimetres. Its comment and TODO.md's "Orbits creep" entry are rewritten to match: the entry
  goes if the creep is gone, or says what is left.
- Landings, touchdowns, splashdowns, staging, the moon's crossing and the flight tests all pass
  unchanged, so the copy changes nothing while a craft touches anything.
- A craft's copy and Jolt's place never part by more than a float's step at that distance while
  it flies free.

## Part 3: TODO.md entries (to discuss before building)

1. **Double precision in Jolt, as a trial.** BRUTAL's Jolt built with `JPH_DOUBLE_PRECISION` (a
   BRUTAL build change, its natives built ourselves rather than from NuGet), with the game's
   positions in double beside it, and its frame rate and step time measured against today's float
   build. The user wants to know what it costs before deciding. What it would gain (full double
   positions for contacts too, far from any origin), what it costs (a native build of our own,
   maybe slower steps and more memory), and what it would help (vessels and contacts far from
   any body's origin).
2. **Precision for bodies further out.** Once systems sit millions of km out, anything kept or
   drawn in floats there loses precision (a float's step at 1 million km is about 64 m; at 10
   million km about 1 km). What would need to be in double or relative to a nearby body: a
   vessel's place in its body's frame (helped by each body having its own frame, and by Part 2),
   the map's and paths' points, and any float walk along a line (as the glows' walk was made
   safe). Links to the double copy (Part 2) and to item 1.
3. **Local physics, as an experiment.** Physics only where something is witnessed, each with
   its own local origin, near what it holds, rather than one world per body with its origin on the
   body's ground. Collisions can only happen locally, and the unwitnessed world already runs
   cheaply on formula (`Unwatched`, `Formula`), so this might allow better performance and better
   precision too. To try, measuring the frame rate and step time against today's. What it would
   gain, cost and risk: several worlds at one body, handing vessels between them, the ground's
   tiles per world, and the turning frame's pulls about a moved origin.

## Docs

- `CLAUDE.md`: the bodies and vessels paragraphs on what a vessel's place is kept in, and where a
  path ends; `README.md` (the path's ends, the log's reasons); the tests' README for the tightened
  creep test.
- `TODO.md` as in Part 3, and the "Orbits creep" entry as above.

## Constraints

- No change to BRUTAL.
- Saved worlds need no compatibility. The soak and performance tests are waived for this work,
  as for the rest of the branch; the full suite (`dotnet run --project tools/TestRun`) passes
  before each commit.
- Never build in, run from or touch the `map-and-camera` worktree (the user plays from it) or a
  running `GameOne.exe`.

## My calls (from the builder check)

- **A mission's vessel lost past the edge** counts as lost, as a wreck does: a run whose required
  vessel leaves fails as it would to any loss.
- **Which reads use the double copy:** the creep comes from integrating in floats, not from
  reading them. So the copy is what the physics steps from, and what a crossing, a save, the
  handover to formula and the edge check take; every other read (the HUD, the readout, the
  predictions, the paths) may go on reading the float, which is now the copy rounded, so it carries
  no creep.
- **Flying free:** no live contact, not in the water, and the legs not holding it (`Cushion`). The
  game's own changes in a free step (the fall's kick, thrust, the air's drag and its turning of the
  nose, steering) go into the copy. Anything else that changes its place or velocity (Jolt's
  contacts, the water, the legs, any direct set) makes the copy be taken from Jolt again. If Jolt's
  result after a step differs from what the copy expects by more than its rounding, that is taken
  as a touch, and the copy is taken from Jolt.
- **Cost:** near zero. Only free-flying crafts in the physics are handed their copy, once a step.
- **The creep test:** aim for none. If some is left (from the attitude, say), measure it, say
  where it comes from, and rewrite the TODO entry to say what is left.
- **The edge on a path** is found to within a tolerance, like the path's other ends, among the
  stars at the sun's place then. A vessel is removed in the step its crossing is seen, and logged
  then. A vessel at the moon is checked by its place changed to the sun's frame (`Bodies.Change`).
  In practice it can't get that far while still at the moon.
- **Nodes and targets:** a path ends at the edge, so no node can be placed past it. A target whose
  path leaves the system can still be picked while it exists.
- **A loaded world:** a vessel already past the edge is removed and logged on the first step.
  `WorldStore.FormatVersion` is bumped, so old files are listed as unreadable.

## Left to the builder

- How the copy is kept (on `Craft`, or beside it in `RocketPhysics`), and how the game knows a step
  touched something.
- The icon's look, as long as it reads as a path's end like the collision mark.

## Part 4: no speed cap in open space (the user's decision, after the final review)

The user wants no speed cap at all; on this branch the cap goes everywhere but low over the
ground. Today `Scene.TopSpeed` (4,000 m/s) is Jolt's `SetMaxLinearVelocity`, in the turning ground
frame, so past ~760 km from a body's axis Jolt clamps the frame's own motion and an escape leaves
its drawn path.

- **Jolt's own cap is lifted** (set far past anything flown, so Jolt never clamps).
- **Open space has no cap.** A vessel goes as fast as its engine and the pulls take it.
- **Low over the ground, the game caps the speed over the ground** at the figure that is
  `Scene.TopSpeed` today (renamed for what it now is, e.g. `Scene.GroundTopSpeed`), for vessels in
  the physics and under formula alike, as the physics' outcome and the formula's must match.
  *My call:* "low" is worked out from what the cap protects (the tiles laid ahead, the floes'
  reach, the wake's sweep: each `speed × its seconds`), as a height over the body's highest
  ground (`GroundBody` has it, as `OrbitFloor` uses), never a hand-picked number; the builder
  derives it and states it in `Scene`'s comment. Applied by the game (a velocity set on the craft
  and on its double copy together), never by Jolt.
- **Every bound worked out from `TopSpeed` as a worst case** uses the vessel's own speed instead
  (e.g. the Coriolis bound in `Collisions.Solids`); the floes' load-time check keeps the ground
  cap (floes are low over the ground by definition). Anything else that assumed speeds never pass
  4,000 m/s (the slow-down's least scale, prediction horizons, the trip tests' asserts) is checked
  and made to hold at any speed.
- **Tested off the pole:** an escape flown in the physics far from the axis (past 760 km) follows
  the path drawn for it; and a vessel fast low over the ground is held at the ground cap, in the
  physics and under formula.
- **TODO.md:** the user's bubble idea goes into the local physics entry, to discuss before
  building: physics bubbles that travel with vessels in open space, merged by closing speed as
  vessels near each other, and even the flown vessel on formula when nothing is near it (which
  needs the formula to steer and burn as the physics does). The "Raising the top speed" entry is
  brought up to date (open space done; what's left is the ground cap's costs).
- **Docs:** CLAUDE.md's "Fast collisions" (its first sentence is the cap), README's numbers.

## Part 5: from the play test (the user's decisions, 2026-11-18)

1. **A target is picked by a double left click** on its marker (or vessel), never a single click:
   a single left click on a marker or on empty space no longer touches the target (today a single
   click on a marker picks it, and one on empty space lets it go, which is how clicks meant for
   manoeuvre nodes low over the planet lose it). A double click on the target's own marker lets it
   go. Right click is unchanged (it anchors the camera, or opens the flown vessel's actions).
   *My calls:* double clicking empty space does nothing (letting go is by the target's own marker,
   so no stray click loses it); a double click whose first click lands on a node or the path is the
   node editor's, not a pick; the threshold is ImGui's own double-click time and distance.
2. **Paths are always drawn round their body, among the stars**, a plain ellipse (or hyperbola)
   round the body they are at, in every frame (Auto, Surface, Orbit and Target), for the flown
   vessel, its plan, a flying target and the map's other vessels. The frame in use still sets the
   speeds, the holds and the travel markers. Today the Surface frame (which Auto stays in until the
   periapsis is 100 m over the orbit floor: the planet's air top, 3,000 m) draws the track over the
   turning ground, which smears a low orbit into loops and leaves one round the moon unclosed.
   A landing's marks and a ground mark still show where the ground will be when the path gets
   there (as `Orbit.Landing`/`GroundAt` work them out). *My call:* the closest approach and the
   window are unchanged in what they find; they are drawn on the line as it now is.
3. **Right click on the flown vessel's path warps to that point:** the fastest rate (`Flight.Rates`'
   last, ×32) at once, dropping to ×1 as the game's clock reaches the time that point is flown,
   with a mark on the path where it will stop while it runs. *My calls:* the point is the time
   along the path as flown with the engine off (the orbit as it is, across its legs; on a plan's
   path only up to its first node, as that much is flown without a burn); it ends early, at ×1, on
   any rate key, a vessel switch, a collision or near-miss warning dropping the warp, or the flown
   vessel wrecked or left the system; an object under the pointer (a marker, a vessel, a body) wins
   over the path (right click there anchors the camera as now); the path is hit within a few pixels
   (a `Scene` figure); it works in the chase view and the map alike, through `FlightInput` (so the
   tests and GameDriver can ask for it), and the readout shows it.
- **Docs:** README's controls table (left, double and right click), CLAUDE.md (frames: paths drawn
  among the stars), the GameDriver CLAUDE.md where scripts click markers.
