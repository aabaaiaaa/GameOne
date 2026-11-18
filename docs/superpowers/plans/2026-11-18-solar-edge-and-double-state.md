# The solar system's edge, and places kept in double: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Paths end at a 10,000,000 km edge round the sun (vessels crossing it removed and logged), and each craft's place and velocity are kept in double by the game, so orbits no longer creep from float rounding.

**Architecture:** The edge is a sphere round `SolarSystem.SunCentreAt` (worked out among the stars, in double); `Patched.Legs` ends a leg where it reaches it, the paths draw a mark there, and the step removes any vessel past it. The double copy lives with each `Craft` in `RocketPhysics`: the game steps it while the craft flies free and hands Jolt the rounded value; any touch or direct set takes the copy from Jolt again.

**Tech Stack:** C# / .NET 10, BRUTAL (Jolt, single precision, unchanged), xUnit headless `Simulation` tests.

**Spec:** `docs/superpowers/specs/2026-11-18-solar-edge-and-double-state-design.md` (read it whole: its "My calls" section is binding).

## Global Constraints

- Work only in the worktree `C:/Users/jeastaugh/source/repos/Experiments/BrutalOne/GameOne/.claude/worktrees/edge-and-precision`. Never build in, run from or touch the `map-and-camera` worktree, or a running `GameOne.exe`.
- Shell commands through the Bash tool (PowerShell, if needed, as `pwsh -NoProfile -Command` from Bash).
- No change to BRUTAL (`../Brutal`).
- `Scene.SolarSystemEdge` = 1e10 m (10,000,000 km), centred on the sun.
- Log reason: `WorldLog.LeftTheSolarSystem` = `"left the solar system"`; the flown vessel's message: `"Left the solar system."`; the path's mark label: `"Leaves the solar system"`.
- Saved worlds need no compatibility; bump `WorldStore.FormatVersion` where the saved form changes.
- Soak and performance tests waived. Before each commit: `dotnet run --project tools/TestRun` with 0 failed; build and test with `--disable-build-servers -nodeReuse:false`; `dotnet build-server shutdown` at the end.
- CLAUDE.md's rules hold, in particular: comments say what the code does and why, never its history; new tunables in `Scene.cs`; game logic reachable through `FlightInput`, no test code in the game; docs (CLAUDE.md, README, tests' README) kept right in the task that changes the behaviour.

## Review Focus

1. A bound orbit whose apoapsis is past the edge (not only a hyperbola): its path must end at the edge too, not loop back. (Task 1 test.)
2. Drawing an escape path out to 1e10 m must stay cheap and safe: bounded point count, no allocation per frame, no float walk running away. (Task 1 test.)
3. In time warp a vessel under formula may jump well past the edge in one step: it must still be removed (checked by distance, not by an exact crossing). (Task 2 test.)
4. A stage parting in orbit, or a crossing into the moon's sphere, while flying free: the copy must follow Jolt across the set or change, not snap the craft back. (Task 3 tests.)
5. A world saved with a vessel in orbit and loaded again keeps its place and velocity exactly (double round-trip). (Task 4 test.)

---

### Task 1: Paths end at the solar system's edge, with a mark

**Files:**
- Modify: `src/GameOne/Scene.cs` (the tunable), `src/GameOne/SolarSystem.cs` (the distance from the sun), `src/GameOne/Patched.cs` (legs end at the edge), `src/GameOne/Paths.cs` and `src/GameOne/SystemMap.cs` (the mark), whatever draws `CollisionMarks` in the HUD (draw the edge mark beside it)
- Test: `tests/GameOne.Tests/SolarEdgeTests.cs` (new)

**Interfaces:**
- Produces: `Scene.SolarSystemEdge` (double, 1e10); `SolarSystem.FromSun(GroundBody body, Vector3Double p, double worldTime) -> double` (the distance of a point in `body`'s frame from the sun's centre then, in double, through `Bodies.Change` to the root and `SunCentreAt`); `PathLeg.LeavesSystem` (bool: the leg ends at the edge); `Paths.EdgeMarks` (`IReadOnlyList<CollisionMark>`-like list, label `"Leaves the solar system"`).

- [ ] **Step 1: Write the failing tests** in `SolarEdgeTests`:
  - `An_escape_path_ends_at_the_edge`: a vessel on a hyperbola from the planet (e.g. 1,500 m/s outward at 20 km); `Patched.Legs` → the last leg has `LeavesSystem`, and `SolarSystem.FromSun` at its end is within 1% of `Scene.SolarSystemEdge`.
  - `An_orbit_reaching_past_the_edge_ends_there`: a bound ellipse with apoapsis at 2e10 m → same assertions (Review Focus 1).
  - `An_orbit_inside_the_edge_has_no_edge_end`: a 10 km-high circular orbit → no leg `LeavesSystem`.
  - `The_edge_is_round_the_sun_not_the_planet`: `FromSun` of the planet's centre equals `Scene.SunDistance` to 1 m.
  - `An_escape_path_is_drawn_to_the_edge_with_its_mark`: build the flown vessel's paths in a `Simulation` (as `PathTests` do) → `Paths.EdgeMarks` has one mark, labelled `"Leaves the solar system"`, the line's point count is within its existing cap, and building it again allocates nothing (Review Focus 2).
- [ ] **Step 2:** run `dotnet test tests/GameOne.Tests --disable-build-servers -nodeReuse:false --filter SolarEdgeTests` → FAIL (names undefined).
- [ ] **Step 3:** implement. A leg ends at the earlier of its crossing and its edge time, found to within a tolerance like the path's other ends; the leg's orbit is round its body, so its points are changed to the sun's frame among the stars at their own times. Every consumer of `PathLeg` (`Paths`, `SystemMap`, `ManoeuvrePlan`, `NodeEditor`, `Collisions`, `Worlds.Ahead`) keeps working at an edge end; nodes can't be placed past it (they are placed on the drawn path). Docs: CLAUDE.md (where a path ends), README (the path's ends and marks).
- [ ] **Step 4:** the filter run passes; then `dotnet run --project tools/TestRun` → 0 failed.
- [ ] **Step 5:** commit `"Paths end at the solar system's edge, 10,000,000 km round the sun, with a mark"`.

### Task 2: A vessel crossing the edge is removed and logged; the flown one as a crash

**Files:**
- Modify: `src/GameOne/Flight.cs` (the step's check, removal, logging, the flown vessel), `src/GameOne/WorldLog.cs`, `src/GameOne/Vessel.cs` if the crash reason lives there, `src/GameOne/Hud.cs` if the message needs it
- Test: `tests/GameOne.Tests/SolarEdgeTests.cs`

**Interfaces:**
- Consumes: `SolarSystem.FromSun`, `Scene.SolarSystemEdge` (Task 1).
- Produces: `WorldLog.LeftTheSolarSystem` = `"left the solar system"`.

- [ ] **Step 1: Write the failing tests:**
  - `A_vessel_under_formula_past_the_edge_is_removed_and_logged`: a world with an unflown vessel at 1e10 m − 1 km moving outward at 2,000 m/s; step a few seconds → it is not in `Flight.WorldVessels`, and `Flight.Log` has one entry for it with `How == WorldLog.LeftTheSolarSystem`.
  - `A_vessel_jumping_past_the_edge_in_warp_is_removed`: the same at the highest time rate, starting 50 km inside → removed (Review Focus 3).
  - `The_flown_vessel_leaving_is_treated_as_a_crash`: the flown vessel crosses → `Flight.CrashReason == "Left the solar system."`, it is in no saved capture of the world afterwards, its run (if any) has failed as for any lost required vessel, and Tab to another vessel works as after a crash.
  - `A_loaded_world_with_a_vessel_past_the_edge_removes_it_on_the_first_step`.
  - `A_vessel_near_its_body_is_not_asked`: the check costs nothing for vessels within `Bodies.ClearWithin`-like reach (assert by whatever counter or timing detail the step already has, or by the check being skipped on a near vessel; the builder picks a testable form).
- [ ] **Step 2:** run the filter → FAIL.
- [ ] **Step 3:** implement: once a step, after the bodies are chosen, every vessel past the edge is removed and logged (as a loss, `Witnessed` as for other losses). The flown one: the camera stays where it was and the HUD shows its message as a crash's is shown, until the player Tabs or restarts. Docs: CLAUDE.md (the world log's reasons, Vessels nobody watches), README.
- [ ] **Step 4:** filter passes; full suite → 0 failed.
- [ ] **Step 5:** commit `"A vessel crossing the solar system's edge is removed and logged; the flown one as a crash"`.

### Task 3: Each craft's place and velocity kept in double in the physics

**Files:**
- Modify: `src/GameOne/Physics/RocketPhysics.cs` (the copy with each `Craft`, `Fall`, thrust, drag, steering's velocity changes, every `SetLinearVelocity`/`SetPositionAndRotation`, `TakeOut`/`TakeIn`, `CraftState`), `src/GameOne/Vessel.cs` / `src/GameOne/Flight.cs` (crossing: `CrossTo`, `Cross`)
- Test: `tests/GameOne.Tests/WorldTests.cs` (the creep test), `tests/GameOne.Tests/PrecisionTests.cs` (new)

**Interfaces:**
- Produces: `CraftState` carries the centre of mass and velocity as `Vector3Double` (its float fields may stay for the rest); `RocketPhysics.TrueCentreOfMass(Craft) -> Vector3Double` and `TrueVelocity(Craft) -> Vector3Double` (the copy), for Task 4's handover and saving.

- [ ] **Step 1: Write the failing tests:**
  - In `WorldTests`, the Satellites preset's creep test: tighten every orbit's bound to what the copy is expected to hold (start at 0.01 m a lap; set the final bound from the measurement and state it in the comment, rewritten to say what the creep is now and why).
  - `PrecisionTests.A_free_craft_s_copy_and_Jolt_never_part_by_more_than_a_float_step`: a satellite in orbit for a lap → at every step |Jolt's centre of mass − the copy| ≤ 2 float steps at that distance.
  - `PrecisionTests.A_stage_parting_in_orbit_follows_Jolt`: stage an upper stage in orbit → both crafts' copies equal Jolt's places (to a float step) the step after parting, and neither jumps (Review Focus 4).
  - `PrecisionTests.Crossing_into_the_moon_s_sphere_carries_the_copy`: a craft in the physics crossing into the moon's sphere (as the existing crossing tests set one up) → after the crossing its copy, changed in double, matches Jolt's to a float step, and its orbit round the moon matches the one predicted before the crossing as closely as today or better (Review Focus 4).
  - `PrecisionTests.A_landing_is_unchanged`: an existing landing test's touchdown speed and outcome are the same with the copy (pick one of the booster landings; assert its touchdown speed to within 0.01 m/s of the value it gives before this change, measured first).
- [ ] **Step 2:** run them → the creep test fails its tightened bound; the new ones fail (names undefined).
- [ ] **Step 3:** implement per the spec's Part 2 and "My calls": flying free = no live contact, not in the water, legs not holding; the game's own free-step changes go into the copy; any other change, or Jolt's result differing from the copy's expectation by more than its rounding, takes the copy from Jolt. Jolt is handed the rounded copy once a step, only for free crafts. If some creep is left, find where from and say it in the test's comment.
- [ ] **Step 4:** the tests pass; full suite → 0 failed.
- [ ] **Step 5:** commit `"Each craft's place and velocity kept in double, so an orbit no longer creeps from float rounding"`.

### Task 4: Double under formula and in saved worlds

**Files:**
- Modify: `src/GameOne/Formula.cs` (the stepped motion in double), `src/GameOne/World.cs` (`VesselState` place and velocity as `Vector3Double`, and its centre of mass), `src/GameOne/WorldStore.cs` (`FormatVersion` +1), `src/GameOne/Vessel.cs` (`Capture`, the restoring constructor, the handover to and from formula using Task 3's copy)
- Test: `tests/GameOne.Tests/WorldStoreTests.cs` (or wherever the round-trips are), `tests/GameOne.Tests/PrecisionTests.cs`

**Interfaces:**
- Consumes: `RocketPhysics.TrueCentreOfMass`, `TrueVelocity` (Task 3).

- [ ] **Step 1: Write the failing tests:**
  - The store's round-trip test: a vessel's place and velocity with more digits than a float holds come back exactly (Review Focus 5).
  - `PrecisionTests.A_vessel_handed_to_formula_and_back_keeps_its_place_in_double`: hand a craft in orbit out to formula and back in → its copy equals the formula's place to 1e-6 m.
  - `PrecisionTests.A_stepped_vessel_under_formula_holds_its_orbit_as_in_the_physics`: a vessel under formula stepped (burning, then coasting low) for a lap → its semi-major axis holds within the bound Task 3 set.
- [ ] **Step 2:** run → FAIL.
- [ ] **Step 3:** implement. Docs: CLAUDE.md (Worlds: what a vessel's place is saved in), the tests' README if it describes the store tests.
- [ ] **Step 4:** pass; full suite → 0 failed.
- [ ] **Step 5:** commit `"Places and velocities in double under formula and in saved worlds"`.

### Task 5: TODO.md entries and the docs' last sweep

**Files:**
- Modify: `TODO.md`, and `CLAUDE.md` / `README.md` for anything earlier tasks left.

- [ ] **Step 1:** add the spec's Part 3 entries to `TODO.md`, each "to discuss before building", each with what it would gain, what it would cost or risk and which case it would help, in the file's style: (1) double precision in Jolt as a trial, its frame rate and step time measured against today's float build; (2) precision for bodies further out (a float's step about 64 m at 1 million km, about 1 km at 10 million km; what must be double or body-relative; linking to the double copy and to (1)); (3) local physics as an experiment (physics only where something is witnessed, each with its own local origin; collisions only local; the rest on formula; measured against today's). Update or remove the "Orbits creep a little, from float rounding" entry to match Task 3's result. Nothing in TODO.md, CLAUDE.md or README points to the spec or plan.
- [ ] **Step 2:** check every doc mention of how a vessel's place is kept, where a path ends and the log's reasons (grep `float`, `creep`, `Path`, `RemovedForRoom`).
- [ ] **Step 3:** full suite → 0 failed (docs only, but run it once).
- [ ] **Step 4:** commit `"TODO: Jolt in double as a trial, precision further out, local physics; the creep entry brought up to date"`.
