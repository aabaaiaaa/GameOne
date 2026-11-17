# The camera, the map, targets and nodes: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix what the user's moon-landing play test found:
- each vessel's camera view is saved, and restored behind the link-up;
- the camera can be centred on other objects;
- the map is centred on the vessel;
- bodies and points of interest can be targeted, and plane crossings are marked;
- orbit lines show their direction;
- manoeuvre nodes are fixed once set and form a chain;
- holds are on-screen buttons, and the steering rings set a heading or pitch with one click;
- the chase camera can look from straight up to straight down;
- the moon's ring marker shows again.

**Architecture:**
- A vessel's view (`CameraView`, including what it is centred on, `CameraCentre`) is game state. It is saved in `VesselState`, handed to the flight through `FlightInput`, and applied to the `ChaseCamera` by `GameView` whenever the flown vessel changes.
- The nodes become a chain (`ManoeuvreChain`):
  - its first node is anchored to a fixed pre-burn state;
  - each later node keeps its gap after the one before it and its delta-v;
  - the planned path is built from the anchor, never from the vessel's present state.
- The holds become one `HoldMode` on the vessel, chosen through `FlightInput`. The steering rings' click-to-hold is one of those modes, with its own heading and pitch.

**Tech Stack:** C# / .NET 10, BRUTAL (ImGui HUD, Vulkan, Jolt), xUnit headless flight tests (`Simulation`), GameDriver for the running game.

**Spec:** `docs/superpowers/specs/2026-11-17-camera-map-and-targets-design.md`. Read it with this plan. Its "My calls" sections are decisions, not suggestions.

## Global Constraints
- **CLAUDE.md applies throughout.**
  - Game logic goes through `FlightInput`.
  - Every new HUD button is added to `_buttons`, so that it reads out as `Marker <name>` in `Readout.cs`.
  - New tunables go in `Scene.cs`.
  - Nothing is written for exactly two bodies.
  - Comments say what and why, never history.
  - Files are CRLF.
  - Build and test with `--disable-build-servers -nodeReuse:false`, then run `dotnet build-server shutdown`.
- **No backwards compatibility.** Bump `WorldStore.FormatVersion` (now 3) once on each branch whose saved form changes. Old worlds are then listed as unreadable, which the user accepts.
- **Low hand-holding.** Plane marks, the manoeuvre mark and the green node are information only. Nothing burns for the pilot.
- **POIs become targetable.** Change CLAUDE.md's "Low hand-holding" rule from "markers only … never a target" to "a target as a site is, and never an instruction".
- **Tests to run.**
  - Single tests: `dotnet test tests/GameOne.Tests --disable-build-servers -nodeReuse:false --filter "FullyQualifiedName~<Name>"`.
  - The suite: `dotnet run --project tools/TestRun`.
  - Before the merge: `dotnet run --project tools/TestRun -- --before-merge`.
  - The soak and performance tests are waived for this work by the user.
- **GameDriver.** Never drive or close the user's game: check `tasklist //FI "IMAGENAME eq GameOne.exe"` first. Use a new, empty scratch `GAMEONE_WORLDS`.
- **Commits.** Commit each task on its branch with the repo's own git identity (already configured). Never push or merge.
- **Branches.**
  - Tasks 1–2 go on `resume-missions` (the worktree `.claude/worktrees/resume-missions`).
  - Before Task 3, make the worktree `.claude/worktrees/map-and-camera` on a new branch `map-and-camera` from `resume-missions`'s tip, and build Tasks 3–17 there.

## Review Focus
1. **A camera centred on a vessel at another body**, the moon, while the flown vessel is at the planet. The main view is drawn in the flown vessel's body's frame, so the centred object's focus must be changed across through `BodyFrames`/`Bodies.Change`, in double. Expected: no jitter at 40 km out. Pinned in Task 5: `A_camera_on_a_vessel_at_the_moon_follows_it_without_jitter`.
2. **Saving while the camera is on something else, or during a link-up.** Expected: the saved view is the vessel's own, with its centre, and reloads as it was. Pinned in Task 1: `A_view_centred_elsewhere_round_trips`.
3. **A node chain whose anchor is at the moon while the vessel is still at the planet**, or the reverse. Expected: the plan is built round the anchor's body, and is drawn where it was set. Pinned in Task 12: `A_chain_anchored_at_the_moon_is_planned_round_the_moon`.
4. **Time warp with the manoeuvre hold on and a node turning green.** Expected: the hold steers by the burn left at every sub-step, and a node matched and cut in warp still goes. Pinned in Task 13: `A_node_matched_and_cut_in_warp_goes`.
5. **The centred object lost or removed while watched, or a rewind while centred on a site.** Expected: the camera goes back to the flown vessel, or takes the rewound vessel's saved view, and never throws. Pinned in Task 5: `A_camera_on_a_vessel_that_is_lost_goes_back_to_the_flown_one`.

---

## Part A, on `resume-missions`

### Task 1: The vessel's view, saved

**Files:**
- Create: `src/GameOne/CameraView.cs`
- Modify:
  - `src/GameOne/ChaseCamera.cs`
  - `src/GameOne/World.cs` (VesselState)
  - `src/GameOne/Vessel.cs` (Capture, the restoring constructor)
  - `src/GameOne/Flight.cs` (FlightInput, Step)
  - `src/GameOne/WorldStore.cs` (FormatVersion 4)
  - `src/GameOne/Program.cs`
- Test:
  - `tests/GameOne.Tests/WorldStoreTests.cs`
  - `tests/GameOne.Tests/CameraTests.cs`

**Interfaces:**
- **Produces:**
  - The view itself, as a record:
    `internal sealed record CameraView(bool Map, float Yaw, float Pitch, float Distance, float MapYaw, float MapPitch, float MapDistance, CameraCentre Centre)`
    with `static CameraView? None => null`.
  - What the view is centred on:
    `internal enum CentreKind { Own, Vessel, Body, Site, Point }`
    and `internal readonly record struct CameraCentre(CentreKind Kind, int Id)`.
    - `Id` is a vessel id, a `GroundBody.Index`, a `Sites.All` index, or an index into the flown vessel's mission's `PointsOfInterest`.
    - It has `static CameraCentre Own => new(CentreKind.Own, 0)`.
  - `ChaseCamera.View` (a `CameraView` property) reads the current setup; `ChaseCamera.Apply(CameraView view)` sets it and snaps.
  - `Vessel.View`: `CameraView? { get; internal set; }`.
  - `VesselState.View`: `CameraView?`.
  - `FlightInput.View`: `CameraView?`. When it is not null, `Flight.Step` sets `Controlled.View` to it.
- **Consumes:** nothing new.

- [ ] **Step 1: Write the failing tests.**
  - In `WorldStoreTests`, add `A_view_centred_elsewhere_round_trips`. Save a world whose flown vessel has `View = new CameraView(true, 1f, 0.3f, 60f, 2f, 0.9f, 90_000f, new CameraCentre(CentreKind.Vessel, 7))`. Load it back, and `Assert.Equal` the view.
  - `AssertSameVessels` already reflects over every property, and a record compares by value.
  - In `CameraTests`, add `A_view_read_from_the_camera_applies_back_the_same`:
    - place the camera with `Place(0.7f, 0.4f, 120f)` and toggle the map;
    - read `View` into `v`;
    - give a new `ChaseCamera(null)` `Apply(v)`;
    - its `View` must equal `v` once both have had one `Update`.
- [ ] **Step 2: Run them to make sure they fail.**
  Run: `dotnet test tests/GameOne.Tests --disable-build-servers -nodeReuse:false --filter "FullyQualifiedName~A_view_"`
  Expected: build errors (`CameraView` is not defined).
- [ ] **Step 3: Implement.**
  - `ChaseCamera.View` reads `_zoomedOut` and the chase and map angles. Its centre is `CameraCentre.Own` until Task 5.
  - `Apply` sets the angles and `_zoomedOut`, sets `_out` to match at once (so it lands there behind the link-up rather than zooming), clears free look and snaps.
  - In `Program.cs`, pass `View = view.Camera.View` in the `FlightInput`.
  - Bump `FormatVersion` to 4.
- [ ] **Step 4: Run the tests and make sure they pass.**
  Run the same command. Expected: PASS.
- [ ] **Step 5: Commit.** Message: "A vessel's view: saved with it, read from and applied to the camera".

### Task 2: Restoring the view behind the link-up, on carry-on, rewind, Tab and a swap

**Files:**
- Modify:
  - `src/GameOne/Flight.cs` (`SwitchTo`, `CarryOn`, `Rewind`)
  - `src/GameOne/GameView.cs` (`Follow`)
  - `src/GameOne/Views.cs`
  - `README.md`
  - `CLAUDE.md` (the link-up paragraph under "Vessels nobody watches")
- Test:
  - `tests/GameOne.Tests/ViewsTests.cs`
  - `tests/GameOne.Tests/CrossingTests.cs`

**Interfaces:**
- **Consumes:** Task 1's `Vessel.View`, `ChaseCamera.Apply`, and `ChaseCamera.View`.
- **Produces:**
  - `Flight.ViewTaken` (`event Action<Vessel>`). It is raised in `Fly` when the flown vessel changes, and after a carry-on or rewind.
  - `GameView` handles it with `Camera.Apply(vessel.View ?? GameView.DefaultView)`.
  - `internal static CameraView DefaultView` is a chase view at `Scene.CameraDistance` and `Scene.CameraPitchDegrees`, centred on its own vessel.

- [ ] **Step 1: Write the failing tests.**
  - In `CrossingTests`, add `Every_switch_shows_the_link_up_even_where_the_physics_is_made`:
    - two vessels at the planet;
    - Tab;
    - assert `sim.Flight.LinkUp.On` on the next frame;
    - step while it is on, then assert the new vessel is flown.
  - In `ViewsTests`, add `Tab_brings_back_the_new_vessels_own_view`:
    - give vessel B `View` with `Map = true` and `Distance = 300`, and fly A;
    - Tab to B;
    - after the link-up, a headless `ChaseCamera(null)` driven by a `ViewTaken` handler reports `View == B.View`;
    - A's `View` holds what the camera had before the switch.
  - Add `A_swap_by_a_small_view_brings_back_its_vessels_view`: the same as above, through `views.Swap`.
  - Add `A_carry_on_and_a_rewind_bring_back_the_saved_view_after_a_link_up`. Use `sim.Reloaded()` and `Rewind`. Assert `LinkUp.On` was seen and the camera's `View` equals the saved one.
  - Add `A_vessel_never_flown_gets_the_default_view`.
- [ ] **Step 2: Run them to make sure they fail.**
  Run: `dotnet test tests/GameOne.Tests --disable-build-servers -nodeReuse:false --filter "FullyQualifiedName~ViewsTests|FullyQualifiedName~Every_switch_shows"`
  Expected: the new tests FAIL; the old ones pass.
- [ ] **Step 3: Implement.**
  - `SwitchTo` always calls `LinkUp.Begin` (it no longer checks for a body without physics). The `Due` branch makes the physics only if it is missing.
  - A carry-on of the vessel already flown also goes through the link-up.
  - `Fly` stores nothing about the camera: the outgoing vessel's view is already its own, kept up to date by Task 1's `FlightInput.View` each frame.
  - `GameView.Follow` replaces `Switched += Camera.Snap` with the `ViewTaken` handler.
  - `Views.CarryOn` is unchanged.
  - Update the README's Tab text and CLAUDE.md's link-up paragraph: "every switch, carry-on and rewind shows the link-up, and the view comes back as that vessel left it".
  - Change the old tests that assert no link-up at a body whose physics is made: `A_view_given_to_a_vessel_at_a_body_whose_physics_is_made_shows_no_link_up` concerns small views and stays. A test for a main-view switch with no link-up is changed to expect one.
- [ ] **Step 4: Run the suite.**
  Run: `dotnet run --project tools/TestRun`
  Expected: 0 failed.
- [ ] **Step 5: Commit.** Message: "Each switch, carry-on and rewind shows the link-up and brings back that vessel's view".

---

## Part B, on `map-and-camera`

Create the branch and worktree first (see Global Constraints).

### Task 3: The moon's ring marker (bug)

**Files:**
- Modify: `src/GameOne/Markers.cs`
- Test: `tests/GameOne.Tests/MoonTargetTests.cs`

**Interfaces:**
- **Produces:** `internal static` helpers on `Markers` for the per-candidate decisions that `Draw` makes now: the fade, and the overlap with what is already taken. Tests can call them headless. `Draw` calls them too.

- [ ] **Step 1: Find the cause with superpowers:systematic-debugging before changing anything.**
  - There are two leading hypotheses, with evidence:
    - **(a)** The ring fades by its on-screen size: `SeenFrom` 8 px to `SeenBy` 20 px. Zoomed out to the moon's orbit, about 100 km out, the moon is about 26 px in radius, so the ring has faded out completely.
    - **(b)** The Rank-5 "Landing spot" POI is drawn first. Its box or label goes into `_taken` and swallows the ring's box whenever the moon is small.
  - Confirm which (or both) with a headless reproduction before fixing.
- [ ] **Step 2: Write the failing test** `The_moons_ring_shows_zoomed_out_with_the_landing_spot_on_it`.
  - LAND ON THE MOON, in orbit at the planet.
  - The camera is zoomed out to fit the moon's orbit (`SystemMapTests.ZoomedOut` helper), on a 1280×720 screen.
  - The moon's ring candidate must come out drawn, with fade > 0.5 and not skipped for overlap, alongside the landing spot.
- [ ] **Step 3: Run it to make sure it fails.**
- [ ] **Step 4: Fix by the cause found.**
  - The intended behaviour is that a body's ring stays drawn at its body's size on the screen (so it doesn't fade while the body is in view and small).
  - A marker sitting on a body never takes the body's ring's place. Only labels are spread (`MapLabels`).
- [ ] **Step 5: Run the test and `MoonTargetTests`, `PointOfInterestTests` and `SystemMapTests`.** Expected: PASS.
- [ ] **Step 6: Commit.** Message: "The moon's ring shows on the map, beside the landing spot on it".

### Task 4: The map centres on what the camera is on

**Files:**
- Modify: `src/GameOne/ChaseCamera.cs` (`ZoomedOutView`, `ToggleZoomOut`, `_mapViews` removed, `ForgetMapViews` removed)
- Modify: `src/GameOne/GameView.cs`
- Test:
  - `tests/GameOne.Tests/PathTests.cs`
  - `tests/GameOne.Tests/SystemMapTests.cs`

**Interfaces:**
- **Consumes:** Task 1's `CameraView`, which now carries the map's angles per vessel, in place of the per-body `_mapViews`.

- [ ] **Step 1: Change the tests.**
  - `Zooming_out_looks_at_the_planet_...` becomes `Zooming_out_looks_at_the_vessel_from_far_enough_to_see_the_orbit_and_comes_back`. Zoomed out, `Target` is within 1 m of `Focus`.
  - `The_map_opens_on_its_own_view_until_turned_by_hand_then_on_that_one_at_that_body` becomes `The_map_opens_on_the_view_its_vessel_left`.
  - Angles stay measured from the body's north pole. A new test, `The_map_looks_from_above_the_orbits_plane`, checks this: zoomed out with pitch at the default, the camera's offset from the focus has a positive dot with the pole.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.**
  - `ZoomedOutView` lerps `Target` to `_seen` (the focus), not `_zoomCentre`.
  - The camera's offset direction keeps the pole frame.
  - Remove `_mapViews` and `ForgetMapViews`, and their call in `GameView.Follow`.
- [ ] **Step 4: Run the camera, path and system map tests.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: "The map centres on the vessel, its angles still from the pole".

### Task 5: The camera on another object, witnessed

**Files:**
- Create: `src/GameOne/CameraSubject.cs`
- Modify:
  - `src/GameOne/GameView.cs`
  - `src/GameOne/ChaseCamera.cs` (place view: look at a fixed point with the drag and zoom round it)
  - `src/GameOne/Flight.cs` (`FlightInput.Watching`; `Witnessed`; `InView`'s body check)
  - `src/GameOne/Unwatched.cs` (the camera at the camera's body)
  - `src/GameOne/Scene.cs`
- Test: `tests/GameOne.Tests/CameraSubjectTests.cs` (new)

**Interfaces:**
- **Consumes:** `CameraCentre` (Task 1); `Flight.Locate`, `Sites`, `Mission.PointsOfInterest`.
- **Produces:**
  - `internal static class CameraSubject`, with
    `static (GroundBody Body, Vector3 Focus, bool Place)? Resolve(Flight flight, CameraCentre centre)`.
    - The focus is in the subject's own body's frame, so a vessel's focus is `Views.Focus(v)`.
    - A site's or POI's focus is its top.
    - A body's focus is its centre.
    - It returns null if the subject is gone.
  - `GameView` changes that focus into the flown vessel's body's frame (`Bodies.Change`, in double) and feeds `Camera.Update`.
  - `FlightInput.Watching` (`Vessel?`) is the vessel the main camera is on, if it isn't the flown one.
  - `FlightInput.Camera` becomes the camera's place in the frame of the body named by `FlightInput.CameraBody` (`GroundBody?`).
  - `Scene.PlaceViewHeight` = 30 m and `Scene.PlaceViewAside` = 60 m: where a place's view starts.

- [ ] **Step 1: Write the failing tests.**
  - `A_camera_on_another_vessel_keeps_it_in_its_bodys_physics`: send `Watching = v` each frame, and `v.OnFormula` stays false.
  - `A_camera_on_a_vessel_at_the_moon_follows_it_without_jitter`:
    - fly at the planet with the camera centred on a vessel at the moon;
    - over 120 frames, the focus fed to the camera, changed into the flown frame, steps by less than 1 cm over the vessel's own motion.
  - `A_camera_on_a_vessel_that_is_lost_goes_back_to_the_flown_one`: `Resolve` returns null, and the `GameView` logic (factored into `internal static CameraCentre Kept(Flight, CameraCentre)`) gives `Own`.
  - `Watching_a_vessel_whose_bodys_physics_isnt_made_shows_its_link_up`: as `Flight.OpenView` does, a `ViewLink`-style link-up comes up for the watched vessel.
  - `A_place_view_looks_at_the_place_from_above_and_aside`: `CentreKind.Site` for Home's pad; after `Update`, `Target` is within 1 m of the pad's top, and `Position` is about 30 m above it and 60 m aside.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.**
  - `Flight.Witnessed` adds `input.Watching`, the way it adds a small view's vessel, with its own link-up through the `OpenView` path.
  - `InView` and `Unwatched` use `CameraBody` rather than assuming the flown vessel's body.
  - The camera following its subject in a place view orbits round the fixed point, with the same drag and zoom as the chase view.
- [ ] **Step 4: Run the new tests plus `UnwatchedTests` and `SlowdownTests`.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: "The camera can be on another vessel, a place or a body; what it watches is witnessed".

### Task 6: Right-click to centre, Reset camera, and M

**Files:**
- Modify:
  - `src/GameOne/Hud.cs` (`Actions`, a "Reset camera" button)
  - `src/GameOne/Markers.cs` (an `internal (CameraCentre? Centre) At(Vector2 click)` over the candidates drawn this frame)
  - `src/GameOne/GameView.cs` (`ToggleZoomOut` by subject)
  - `src/GameOne/Readout.cs`
  - `README.md` (controls)
- Test: `tests/GameOne.Tests/CameraSubjectTests.cs`, `tests/GameOne.Tests/VesselActionTests.cs`

**Interfaces:**
- **Consumes:** Task 5's `CameraSubject` and `CameraCentre`.
- **Produces:**
  - `internal static CameraCentre? Hud.RightClickCentre(bool map, CameraCentre current, CameraCentre? onMarker, Vessel? onVessel, Vessel flown)`, a pure function for the rules below:
    - In the map, a marker or vessel clicked becomes the centre, and the own vessel gives `Own`.
    - In the chase view, another vessel becomes the centre. The own vessel gives `Own` while the camera is elsewhere, and null while it is already on it: the actions menu opens as now.
  - `internal static (CameraCentre Centre, bool Map) GameView.Toggled(CameraCentre centre, bool map)` is M's rule:
    - From the map, centred on a vessel, site or POI, M gives the chase view of it.
    - From the map, centred on a body, M gives `Own` in the chase view.
    - From the chase view, M goes to the map with the same centre.
  - The readout: `Marker Reset camera=x y` while it is shown, and `Camera=<what it is on>` (for example, "Own", "Vessel 7", or "Site Home pad").

- [ ] **Step 1: Write the failing tests.**
  - `Right_clicks_centre_by_the_rules`: a table over `RightClickCentre`, covering each case above.
  - `M_from_a_centred_map_goes_by_the_rules`: a table over `Toggled`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.**
  - In `Hud.Actions`, a right-click when `RightClickCentre` gives non-null sets the camera's centre. Otherwise the action menu works as now, and only while the camera is on the flown vessel.
  - The "Reset camera" button sits in the `StripButtons` window, beside "Add view", shown only while the centre isn't `Own`, and added to `_buttons`.
  - The steering rings aren't drawn, and the keys steer by the last `GroundForward` the camera had while it was on the flown vessel, while the centre isn't `Own`.
  - In the README, the right-click and M rows, and "Reset camera".
- [ ] **Step 4: Run the tests.** Then do a GameDriver check in a scratch world:
  - right-click a site on the map, and `Camera=Site …` reads;
  - press M, and a chase view of the place shows;
  - press Reset, and `Camera=Own` reads.
- [ ] **Step 5: Commit.** Message: "Right-click centres the camera on what was clicked; Reset camera; M by what it is on".

### Task 7: The chase camera from straight down to straight up

**Files:**
- Modify: `src/GameOne/ChaseCamera.cs` (`MinPitch`/`MaxPitch` to ±(π/2 − a quarter degree), `KeepClear`, the up at the extremes, `GroundForward`)
- Test: `tests/GameOne.Tests/CameraTests.cs`

**Interfaces:** none new.
- [ ] **Step 1: Write the failing tests.**
  - `The_chase_camera_reaches_straight_up_and_down_without_going_over`: drag far past each end, and `_pitch` stops within a quarter degree of ±90°.
  - `Looking_up_from_below_a_landed_vessel_the_camera_slides_along_the_ground_under_it`:
    - landed on Home's pad;
    - pitch at −89°;
    - the camera is at least `Scene.CameraGroundClearance` above the ground everywhere under it;
    - it is within the vessel's width of straight under its focus, horizontally.
  - `At_the_extremes_the_view_does_not_spin`: `Up` stays within 1° frame to frame while yaw holds.
  - `Steering_away_from_the_camera_keeps_its_last_usable_facing_at_the_extremes`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.**
  - Below the ground, `KeepClear` moves the camera along the ground towards under the focus (not straight up), keeping the clearance.
  - The camera's up comes from its yaw's horizontal facing when it is near the vertical.
  - `GroundForward` keeps the last facing it had under 80° pitch.
- [ ] **Step 4: Run `CameraTests`.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: "The chase camera turns from straight down to straight up, kept clear of the ground".

### Task 8: Points of interest as targets

**Files:**
- Modify:
  - `src/GameOne/NavTarget.cs` (`TargetKind.Point`)
  - `src/GameOne/Flight.cs` (`Locate`, `Find`)
  - `src/GameOne/Markers.cs` (the POI candidates' target)
  - `src/GameOne/Readout.cs` (`TargetName`)
  - `src/GameOne/Paths.cs` (each `TargetKind` switch)
  - `src/GameOne/PointOfInterest.cs` (doc)
  - `CLAUDE.md` (the "Low hand-holding" sentence on POIs)
- Test:
  - `tests/GameOne.Tests/PointOfInterestTests.cs`
  - `tests/GameOne.Tests/WorldStoreTests.cs`

**Interfaces:**
- **Produces:** `TargetKind.Point`, added on the end of the enum.
  - Its `Id` is the index in the vessel's mission's `PointsOfInterest`.
  - `NavTarget.OfPoint(int index)`.
  - `Locate` places it at `PointOfInterest.Place` on its body, turned to now, the way a site's is.
- [ ] **Step 1: Write the failing tests.**
  - `The_landing_spot_can_be_targeted_and_its_distance_is_read`: the distance matches `PointOfInterest.DistanceFrom` within 1 m.
  - `A_point_target_round_trips` (store).
  - `A_point_target_of_another_mission_is_not_found`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement the case in each `TargetKind` switch** named under Files.
- [ ] **Step 4: Run `PointOfInterestTests`, `TargetTests` and `MoonTargetTests`.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: "A mission's points of interest can be targeted".

### Task 9: Clicking a body's orbit line targets it

**Files:**
- Modify:
  - `src/GameOne/SystemMap.cs` (hit-test the bodies' runs)
  - `src/GameOne/Paths.cs` (a target body's `_theirLine`)
  - `src/GameOne/Markers.cs` (fold a line hit into `TakeClick`)
- Test: `tests/GameOne.Tests/SystemMapTests.cs`

**Interfaces:**
- **Produces:**
  - `internal GroundBody? SystemMap.BodyLineAt(Vector2 pointer, float within)`, with `within` set to `NodeEditor.PathPixels` (8 px).
  - The flown body never counts.
  - Its click is a target click, after markers and nodes: a node ghost on the flown path wins.
- [ ] **Step 1: Write the failing tests.**
  - `Clicking_the_moons_orbit_line_targets_the_moon`.
  - `A_click_off_every_line_hits_nothing`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.** Use segment distance over the laid runs, as `NodeEditor.Nearest` does.
- [ ] **Step 4: Run `SystemMapTests`.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: "Clicking a body's orbit line targets it".

### Task 10: Where the target's plane crosses the path

**Files:**
- Create: `src/GameOne/PlaneCrossings.cs`
- Modify: `src/GameOne/Paths.cs` (marks, following the Ap/Pe pattern)
- Test: `tests/GameOne.Tests/PlaneCrossingTests.cs` (new)

**Interfaces:**
- **Produces:**
  `internal static (float Ascending, float Descending, float Inclination)? PlaneCrossings.Of(Orbit own, Vector3 targetNormal)`.
  - It returns true anomalies on `own`, and the angle between the planes in radians.
  - It is null when the planes are within 0.01° of each other.
  - The ascending node is where `own` crosses the target's plane going in the target's normal's direction.
  - The target's normal is either a target body's `Bodies.OrbitOf(body, now).Normal`, or a target vessel's own orbit's `Normal`.
  - Each is used only if the target is round the same body as the leg being marked.
- [ ] **Step 1: Write the failing tests.**
  - `An_equatorial_orbit_crosses_a_tilted_plane_where_their_planes_meet`. Use a worked case: the own orbit is equatorial, and the target's plane is tilted 30° about the X axis. The anomalies are where the position direction is ±X, and `Inclination` is 30° ± 0.01°.
  - `The_same_plane_has_no_crossings`.
  - `A_target_at_another_body_gets_no_marks` (through `Paths.Build`'s marks).
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.**
  - The line of nodes is `cross(targetNormal, own.Normal)`, then use `own.AnomalyToward(direction)`.
  - In `Paths.Build`, add `Mark`s labelled `$"AN {deg:0.0}°"` and `$"DN {deg:0.0}°"` on each leg from `plan.Final` that is round the target's body, in the target's colour.
- [ ] **Step 4: Run the tests and `PathTests`.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: "AN and DN marks where a target's plane crosses the path".

### Task 11: The way each orbit line is flown

**Files:**
- Modify:
  - `src/GameOne/Paths.cs` (`DrawLine`)
  - `src/GameOne/SystemMap.cs` (`Draw`)
  - `src/GameOne/Scene.cs`
- Test: `tests/GameOne.Tests/PathTests.cs`

**Interfaces:**
- **Produces:**
  - `internal static int Chevrons(ReadOnlySpan<Vector2> line, float every, Span<(Vector2 At, Vector2 Way)> into)` places chevrons along a screen polyline every `every` pixels, pointing along it. Both draws call it.
  - `Scene.ChevronEveryPixels` = 120.
  - `Scene.ChevronPixels` = 5.
  - `Scene.ChevronAlpha` = 0.5 × the line's own alpha.
- [ ] **Step 1: Write the failing tests.**
  - `Chevrons_are_spaced_along_a_line_and_point_its_way`: a straight 600 px line gives 5 chevrons, each `Way` within 1e-3 of the line's direction.
  - `Chevrons_allocate_nothing`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement, and draw each chevron as two short lines in the line's colour.**
  - Every line's points are already in flown order:
    - for laps, by time from now;
    - for bodies' paths, by time.
  - Check this, and reverse any that is not.
- [ ] **Step 4: Run the tests, and `SystemMapTests.Building_the_map_allocates_nothing`.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: "Each orbit line shows faintly which way it is flown".

---

## Part C, on `map-and-camera`: nodes, holds and rings

### Task 12: The node chain

**Files:**
- Create: `src/GameOne/ManoeuvreChain.cs`
- Modify:
  - `src/GameOne/Manoeuvres.cs` (`ManoeuvreNode` amounts with `Gap`; `ManoeuvrePlan.Update` from the chain)
  - `src/GameOne/Vessel.cs` (`_nodes` becomes a chain; `Replan`; `Planned`; capture and restore)
  - `src/GameOne/World.cs`
  - `src/GameOne/Flight.cs` (`FlightInput.Plan` becomes `ManoeuvreChain?`)
  - `src/GameOne/Hud.cs`
  - `src/GameOne/WorldStore.cs` (FormatVersion 5)
- Test:
  - `tests/GameOne.Tests/NodeTests.cs`
  - `tests/GameOne.Tests/WorldStoreTests.cs`
  - the tests that build plans: `FrameTests`, `MoonTargetTests`, `PatchedPathTests`, `SpinTests`, `FormulaTests`, `FramePilots`

**Interfaces:**
- **Produces:**
  - Each node: `internal readonly record struct ManoeuvreNode(double Gap, float Prograde = 0f, float Radial = 0f, float Normal = 0f)`.
    - `Gap` is seconds after the node before it.
    - The first node's `Gap` is seconds after the anchor's time, which is 0 for a freshly anchored chain.
  - The anchor: `internal readonly record struct ChainAnchor(string Body, double Time, Vector3 Position, Vector3 StarsVelocity)`. It is the pre-burn state on the path the first node sits on, at its time, in that body's frame.
  - The chain: `internal sealed record ManoeuvreChain(ChainAnchor Anchor, ImmutableArray<ManoeuvreNode> Nodes)`, with:
    - `double TimeOf(int i)`;
    - `static ManoeuvreChain AnchoredOn(Vessel v, double time, ImmutableArray<ManoeuvreNode> nodes, double now)`, which takes the anchor from `v`'s present path at `time`;
    - `ManoeuvreChain WithNode(int i, ManoeuvreNode n)`;
    - `ManoeuvreChain Insert(double time, …)`;
    - `ManoeuvreChain Delete(int i, Vessel v, double now)`;
    - `ManoeuvreChain MoveTo(int i, double time, Vessel v, double now)`.
  - `ManoeuvrePlan.Update(ManoeuvreChain chain, double now, Vector3? firstLeft)` builds:
    - the first leg from the anchor's state;
    - every later leg from the previous planned burn.
  - The vessel's own path to the first node is drawn separately, from its present state: `Paths` keeps `_line` as it is.
  - `Vessel.Chain` is `ManoeuvreChain?`, and `VesselState.Chain` the same.
- [ ] **Step 1: Write the failing tests** (in `NodeTests`, replacing those whose expectation changes).
  - `A_node_stays_where_it_was_set_when_the_path_changes_before_it`: set a node, burn a little prograde before it, and the plan's node place and planned path are unchanged within 1 mm.
  - `Changing_a_node_redoes_the_chain_after_it_keeping_their_gaps_and_amounts`.
  - `Moving_a_node_moves_the_later_ones_with_it`: `MoveTo(1, …)` moves node 2's time by the same seconds.
  - `A_node_cannot_be_moved_before_the_one_before_it`: the time is clamped to the previous node's time plus `NodeEditor.MinimumGap`.
  - `Deleting_a_middle_node_joins_its_neighbours`: node 3's absolute time is kept, and its `Gap` is now from node 1.
  - `Deleting_the_first_node_anchors_the_second_on_the_vessels_path`.
  - `Moving_the_first_node_snaps_it_onto_the_vessels_present_orbit`.
  - `A_chain_anchored_at_the_moon_is_planned_round_the_moon`.
  - `A_chain_round_trips` (store).
  - Replace `A_later_node_keeps_its_time_when_an_earlier_burn_changes_the_orbit` with `Changing_a_node_redoes_the_chain_after_it_keeping_their_gaps_and_amounts`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.**
  - Moving a node to a time is a re-anchor for node 0 (from the vessel's present path) and a change of `Gap` for any later node. The later nodes keep their gaps.
  - Deleting the first node re-anchors at the second's absolute time.
  - Update every test helper that builds a `ManoeuvreNode[]` plan to build a chain with `ManoeuvreChain.AnchoredOn`.
- [ ] **Step 4: Run the suite.** Expected: 0 failed.
- [ ] **Step 5: Commit.** Message: "Manoeuvre nodes are a chain, fixed once set: each keeps its gap and amounts".

### Task 13: Flying a node until it is matched

**Files:**
- Modify:
  - `src/GameOne/Vessel.cs` (`FollowNodes`, `BurnLeft`, `Guide`)
  - `src/GameOne/Manoeuvres.cs` (`NodeGuide` gains `Matched`)
  - `src/GameOne/Scene.cs`
  - `src/GameOne/Readout.cs` (`Node matched`)
- Test: `tests/GameOne.Tests/NodeTests.cs`

**Interfaces:**
- **Produces:**
  - `NodeGuide(double Seconds, Vector3 Left, bool Overshot, float BurnSeconds, bool Short, bool Matched)`.
  - The tunables:
    - `Scene.NodeMatchShare` = 0.03;
    - `Scene.NodeMatchMin` = 1 m/s;
    - `Scene.NodeMatchMax` = 5 m/s.
  - The match tolerance is `Math.Clamp(NodeMatchShare * node.Size, NodeMatchMin, NodeMatchMax)`.
  - From the node's time on, the burn left is the planned path's velocity at its point nearest the vessel's place, less the vessel's velocity, in the node's body's frame, changed to the vessel's body.
  - Before then, the burn left is as now.
- [ ] **Step 1: Write the failing tests.**
  - Replace `A_node_flown_by_its_guide_gives_the_orbit_it_planned_and_then_goes` with `A_node_flown_by_its_guide_turns_matched_and_goes_when_the_engine_is_cut`.
  - `A_node_past_its_time_stays_until_matched`: no burn, step 20 s past the node's time, and the node is still there.
  - `Cutting_the_engine_before_it_is_matched_leaves_the_node`.
  - `When_a_node_goes_the_next_snaps_onto_the_present_orbit`: its anchor is re-taken from the vessel's path, its `Gap` and amounts are kept, and the chain is redone.
  - `The_match_tolerance_is_a_share_of_the_burn_held_between_1_and_5_ms`: a table with 10 m/s giving 1, 100 giving 3, and 1000 giving 5.
  - `A_node_matched_and_cut_in_warp_goes`.
  - `A_late_burn_counts_against_the_planned_path_nearest_the_vessel`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.**
  - The node goes when it is matched and the engine is off. The old "past its time and the engine off" rule is gone.
  - On its going, call `ManoeuvreChain.Delete(0, this, now)`.
- [ ] **Step 4: Run `NodeTests`, `PatchedPathTests` and `SpinTests`.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: "A node stays until its trajectory is matched, and goes when the engine is cut then".

### Task 14: The node editor: handles both ways, dragging along the path, green, and the rings out of the way

**Files:**
- Modify:
  - `src/GameOne/NodeEditor.cs`
  - `src/GameOne/GameView.cs` (`Navigation.Blocked` also while a node is selected)
  - `src/GameOne/Scene.cs`
  - `src/GameOne/Paths.cs` (green)
- Test: `tests/GameOne.Tests/NodeEditorTests.cs` (new; pure helpers only)

**Interfaces:**
- **Consumes:** Task 12's `ManoeuvreChain` operations, and Task 13's `NodeGuide.Matched`.
- **Produces:**
  - `internal static float NodeEditor.Rate(float along)`, where `along` is the pointer's signed distance past `HandleRest` on the handle's own axis:
    - outward, as now: `0.3 + FastestRate * (pull/HandlePull)^2`;
    - inward, `-Scene.NodeHandleInwardShare` times the outward rate at the same distance, with no clamp at zero.
  - `Scene.NodeHandleInwardShare` = 0.25.
  - `Scene.NodeMatchedColor` = (120, 230, 120).
  - `NodeEditor.Selected` is a bool.
- [ ] **Step 1: Write the failing tests.**
  - `A_handle_pushed_in_takes_away_at_a_quarter_of_the_rate_and_goes_past_zero`.
  - `A_handle_pulled_out_adds_as_before`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.**
  - The handle maths uses `Rate`.
  - A centre drag calls `ManoeuvreChain.MoveTo`, over samples of the node's own path: for node 0, the vessel's present path, so grabbing snaps it there; for a later node, the previous node's planned leg. It is clamped after the previous node.
  - Delete calls `ManoeuvreChain.Delete`.
  - The matched node and its planned legs are drawn in `NodeMatchedColor`.
  - `GameView` sets `Navigation.Blocked = Hud.PointerOnMarker || Hud.NodeSelected`.
- [ ] **Step 4: Run the tests.** Then do a GameDriver check:
  - with a node selected, a click on the rings' place does not steer (`Hold` reads none);
  - a handle pushed in lowers the node's `Delta-v`.
- [ ] **Step 5: Commit.** Message: "Node handles work both ways, nodes drag along their own path, a matched node is green, and the rings give way to a selected node".

### Task 15: Holds as buttons, with a manoeuvre hold and a manoeuvre mark

**Files:**
- Modify:
  - `src/GameOne/Vessel.cs` (`HoldMode Hold` replaces `ProgradeHold`/`RetrogradeHold`; `Steer`)
  - `src/GameOne/World.cs`
  - `src/GameOne/Flight.cs` (`FlightInput.ChooseHold`, replacing `ToggleProgradeHold`/`ToggleRetrogradeHold`)
  - `src/GameOne/Controls.cs` (P and R removed)
  - `src/GameOne/Hud.cs` (the buttons in place of the hold lines; the controls list; `ControlLineCount`)
  - `src/GameOne/NavigationControl.cs` (the manoeuvre mark in the view)
  - `src/GameOne/Readout.cs`
  - `README.md`
- Test:
  - `tests/GameOne.Tests/KeySteeringTests.cs`
  - `tests/GameOne.Tests/FrameTests.cs`
  - every pilot using the toggles: `FramePilots`, `MoonPilots`, `SpinTests`, `VesselActionTests`, `MoonTargetTests`

**Interfaces:**
- **Produces:**
  - `internal enum HoldMode { None, Prograde, Retrograde, Manoeuvre, Rings }`.
  - `FlightInput.ChooseHold` is `HoldMode?`. Choosing the mode already on turns it off (to `None`).
  - `Manoeuvre` is refused without a node.
  - The manoeuvre hold steers along `BurnLeft(now)`, and turns itself off once the chain is empty.
  - The keys' lean and `input.Hold` end any mode, as now.
  - The readout: `Hold=<mode>`, plus `Marker Hold Prograde`, `Marker Hold Retrograde` and `Marker Hold Manoeuvre` buttons. `Marker Hold Manoeuvre` is shown disabled without a node.
- [ ] **Step 1: Write the failing tests.**
  - `The_manoeuvre_hold_points_the_nose_along_the_burn_left`: within 2° after 10 s.
  - `The_manoeuvre_hold_cannot_be_chosen_without_a_node`.
  - `The_manoeuvre_hold_turns_off_when_the_last_node_goes`.
  - `Choosing_a_hold_again_turns_it_off`.
  - `P_and_R_do_nothing_now`: `Controls` maps neither to an action.
  - Change the existing hold tests and pilots to `ChooseHold`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.**
  - The buttons are a row of `SmallButton`s built like `FrameButtonRow`, where "Prograde hold: off (P)" is drawn today.
  - The manoeuvre mark is drawn in the view at the projection of `centre + BurnLeft.normalised * ring radius`, as a diamond in `Paths.PlanColor` (green when matched), whether or not the rings show. The rings' burn diamonds stay.
  - Remove the P / R line from the HUD's controls list and the README, and describe the buttons.
- [ ] **Step 4: Run the suite.** Expected: 0 failed.
- [ ] **Step 5: Commit.** Message: "The holds are buttons, with a manoeuvre hold; P and R go; a manoeuvre mark in the view".

### Task 16: The steering rings: one click holds an angle, both at once

**Files:**
- Modify:
  - `src/GameOne/NavigationControl.cs`
  - `src/GameOne/Vessel.cs` (the ring hold's angles)
  - `src/GameOne/Flight.cs` (`FlightInput.RingHeading`, `RingPitch`)
  - `src/GameOne/Program.cs`
  - `README.md`
  - `src/GameOne/Hud.cs` (the controls list's ring line)
- Test: `tests/GameOne.Tests/SteeringTests.cs`

**Interfaces:**
- **Consumes:** Task 15's `HoldMode.Rings`.
- **Produces:**
  - `FlightInput.RingHeading` and `RingPitch` (each `float?`, in radians) are sent once on a click. Either sets `HoldMode.Rings`. The angle not given is the one already held, or, with none, the nose's.
  - `Vessel.RingAim` is `(float Heading, float Pitch)`, saved in `VesselState`.
  - The hold's direction is worked out each step from those angles round the vessel's own body (`Body.Up`/`East`/`North`, never `Planet`).
  - `NavigationControl` draws a mark at each held angle on its ring.
  - A drag still sends `input.Hold` each frame, as now, and ends the ring hold.
- [ ] **Step 1: Write the failing tests.**
  - `One_click_on_the_heading_ring_turns_to_that_heading_and_holds_it`: within 2° after 15 s, with no further input.
  - `Clicking_both_rings_turns_on_both_together`: heading and pitch both reach their angles, and the pitch's turn has begun before the heading's has finished.
  - `Steering_by_the_keys_ends_the_ring_hold`.
  - `The_ring_hold_works_round_the_moon`.
- [ ] **Step 2: Run them to make sure they fail.**
- [ ] **Step 3: Implement.** A tap (press and release judged together) sends the angle at the press point, through `Drag`'s snapping, as `RingHeading` or `RingPitch`.
- [ ] **Step 4: Run `SteeringTests` and `KeySteeringTests`.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: "One click on a steering ring turns to that angle and holds it; both rings at once".

### Task 17: Docs, the GameDriver run and the merge's checks

**Files:**
- Modify:
  - `README.md`
  - `CLAUDE.md` (Frames, Worlds: the view and the chain in `VesselState`; the link-up; Low hand-holding)
  - `tests/GameOne.Tests/README.md` (the new tests)
  - `tools/GameDriver/CLAUDE.md` (the new readings and buttons)
  - `TODO.md` (anything found is fixed on the branch; only design questions go here)
- Delete:
  - `docs/superpowers/specs/2026-11-17-camera-map-and-targets-design.md`
  - this plan
- [ ] **Step 1: Bring each doc up to date** with what the tasks built. Nothing may point to the spec or the plan.
- [ ] **Step 2: Run a GameDriver script in a scratch `GAMEONE_WORLDS`.** It must:
  - launch LAND ON THE MOON and reach orbit;
  - target the moon by its line, and read the AN and DN marks;
  - right-click the landing spot on the map, press M, then Reset;
  - set two nodes, drag the first, and see the second move;
  - choose the Manoeuvre hold;
  - click the heading ring once;
  - quit, relaunch, carry on, and read the same `Camera` and nodes.
- [ ] **Step 3: Run `dotnet run --project tools/TestRun -- --before-merge`.** Expected: 0 failed.
- [ ] **Step 4: Run `dotnet build-server shutdown`.** Then commit: "Docs for the camera, map, targets and nodes; the design and plan removed".
