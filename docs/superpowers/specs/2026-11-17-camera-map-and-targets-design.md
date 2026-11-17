# The camera, the map, targets and nodes: what the moon landing needs

## Context and why
The user play-tested the resuming missions work (branch `resume-missions`) on the moon landing
mission and found what stands between them and flying it well:
- Carrying on a mission doesn't bring back the view they left it in.
- The map looks at the body, not at their vessel.
- There's no way to watch anything but their own vessel.
- Matching the moon's orbital plane is guesswork, because nothing on the orbit line shows where
  the planes cross.
- The moon, its orbit line and the landing spot can't be targeted.
- The manoeuvre nodes fight the steering rings for the mouse.
- The moon's ring marker never shows.
- A node vanishes the moment its time comes, and slides whenever the path changes, so burns
  can't be flown carefully or planned several at once.
- The chase camera can't look from straight below or above.

They see all of it as essential for testing the moon landing, so it is built now, and everything
here merges into `main` together with resuming missions.

## Branches
- Items 1 and 2 (each vessel's view, and the link-up) are built on `resume-missions`, as they
  finish what carrying on does.
- Everything else goes on a new branch, `map-and-camera`, built on top of `resume-missions`:
  - the camera and the map (3, 4, 13);
  - targets and marks (5 to 8, and the bug, 10);
  - the nodes, the holds and the rings (9, 11, 12, 14).
  Each part gets its own commits.
- Both merge into `main` together, once the user's moon play test passes.
- On the new branch, the moon's missing ring marker (item 10) is found and fixed first.

## Goals (the user's decisions)

### 1. Each vessel remembers its own view, saved with the world
- A vessel's view is:
  - map or chase;
  - the zoom and the angle;
  - what the camera is centred on (item 4).
- Carrying on restores the carried-on vessel's view.
- Rewinding restores the view as it was at the rewind point.
- Example: one mission is left in the map and another by the vessel. Carrying on each brings back
  its own view, centred on whatever it was centred on.

### 2. The link-up before the picture jumps
- Carrying on and rewinding play the communications link-up (`LinkUp`) every time, wherever the
  vessel is, and the restored view appears behind the static.
- Tab (switching vessel) also plays the link-up every time, for consistency for now. Today it
  plays only for a vessel at a body whose physics isn't made.
- **Tab brings back the new vessel's own saved view**, as carry-on does, behind the link-up. The
  user will try this and may change it, so it should be easy to change.
- The vessel switched away from keeps the view it had as its own.
- A vessel never flown before (with no saved view) gets the default view.

### 3. The map centres on the flown vessel
- Zoomed out, the map looks at the vessel the camera is on (normally the flown one), not at the
  centre of the body it orbits.
- Its angles are still measured from that body's north pole, so the orbits are seen from above
  their plane as now.

### 4. Moving the camera to something else: right-click
- **In the map:**
  - Right-clicking a body, a marker, a site, a point of interest or a vessel centres the camera on
    it, and a **"Reset camera"** button appears.
  - Right-clicking your own vessel recentres on it, and the button goes.
  - (There is no actions menu in the map, which is fine.)
- **In the chase view:**
  - Right-clicking another vessel that can be seen moves the camera to it: a chase view of it.
  - You still control your own vessel, and "Reset camera" shows.
- **Pressing M with the camera on something else:**
  - Centred on another vessel, a site or a point of interest, M gives the chase view of that
    object.
  - Centred on a body, M returns to your own vessel: a body has no obvious spot for a chase view.
- **A view of a place** (a site or a point of interest) starts a little above it and to one side.
  - It keeps looking at the place, and the player can drag and zoom round it as in the chase view.
    The user says "for now".
  - The point is to watch your own landing from the ground while you fly it.
- **"Reset camera"** puts the camera back on the flown vessel, in the same mode.

### 5. Targeting bodies and their orbit lines
- Left-clicking a body, or its orbit line, targets that body. Today clicking the moon's orbit line
  does nothing.
- The body your vessel is at still can't be a target, as now.

### 6. Points of interest can be targeted
- A mission's points of interest (the moon's landing spot) can be picked as targets, as a site can.
- This changes CLAUDE.md's rule that they are "markers only … never a target". They stay
  information, never an instruction.

### 7. Plane-crossing marks
- The flown vessel's orbit line gets two marks where the target's orbital plane crosses its own:
  the ascending and the descending node.
- They are shown for a target vessel or a target body only, not for sites or points of interest
  (the user: "marks only for vessels and bodies").

### 8. Orbit lines show their direction
- Each orbit line shows, subtly, which way it is flown. This covers the flown vessel's path, the
  target's, the other vessels' lines on the map and the bodies' paths.

### 9. Nodes come before the rings
- While a manoeuvre node is selected, the steering rings round the vessel (heading and pitch,
  `NavigationControl`) don't react to hover or clicks, so a click goes to the node editor.

### 10. Bug: the moon's ring marker never shows
- Even zoomed out, the moon's ring (its `Look.Ring` body marker) never appears.
- Find the cause with systematic debugging and fix it.

### 11. Manoeuvre nodes: fixed once set, edited as a chain
- Today a node is tied to its time alone:
  - Its amounts are measured from whatever the vessel's path is by then, so it slides whenever the
    path changes.
  - It goes as soon as its time is past with the engine off (`Vessel.FollowNodes`), which leaves no
    time to start the burn and watch how well it lines up.
- **Fixed once set:**
  - A node keeps its place, its time and the trajectory it plans (the path after its burn),
    whatever the vessel does meanwhile.
  - The planned path may then not line up fully with where the vessel really is. The user accepts
    that: fixed nodes make for much better planning, of several nodes in turn especially.
- **A chain:**
  - The nodes are a chain. The first is on the vessel's own path. Each later one is on the path the
    node before it plans.
  - Each node keeps its delta-v (prograde, radial, normal) and its time after the node before it.
- **Editing cascades down the chain:**
  - When a node is changed (its handles, or dragged along its path), every node after it is worked
    out again from its new trajectory, each keeping its own delta-v and its time after the one
    before.
  - So editing the second node of a chain redoes the third and on.
- **The handles:**
  - Pulling a handle out from the node adds to its amount, as now.
  - Pushing it back in towards the node takes away again, past zero into the other way if pushed
    on, but at a slower rate, so it is for small adjustments: undoing an overshoot quickly without
    going between two handles over and over. For a larger change the other way, the opposite
    handle is pulled out. Both handles of a pair work this way.
  - The slower rate is a tunable in `Scene` (my call: a quarter of the outward rate to start).
- **Dragging a node's centre:**
  - It moves along the path it is on: the vessel's own path for the first node, the previous node's
    planned path for any later one.
  - It can't go before the node before it.
  - The nodes after it are based on it, keeping their times after it, so they move along with it
    and are worked out again. It never passes the next one.
  - It snaps to the nearest point of that path. A first node left off the vessel's path, because the
    vessel's orbit has changed since, snaps onto the orbit the vessel is on now when it is grabbed.
  - It keeps its delta-v, so it is the trajectory that changes, and the chain after it is redone.
- **Flying a node:**
  - Once the vessel reaches the node's time, the node stays visible, with the guide still counting,
    until its burn is done.
  - While the vessel's trajectory closely matches the planned one (see "closely matches" below),
    the node turns **green**.
  - If the engine is cut while it is green, the node goes. The next node then snaps onto the
    vessel's path as it now is (its orbit, with the engine off), keeping its delta-v, and the chain
    after it is redone.
  - Cutting the engine while it isn't green leaves it there to carry on with.
- **Deleting a node** joins the chain round it:
  - Deleting the first node makes the second the first, snapped onto the vessel's own path.
  - Deleting a middle one joins the nodes either side: the one after it goes onto the path of the
    one before it.
  - Either way, the chain after it is redone.
- **The guide** (the delta-v left and the burn time) counts against the planned trajectory: what
  is still needed to match it.
- **"Closely matches" (the user's words):**
  - It isn't a fixed figure, because a longer trajectory makes a fixed figure the wrong measure. It
    is more a percentage of how well the trajectory matches.
  - The user is happy to try anywhere between 1 and 5 m/s.
  - The aim is to give the next node the best chance of still being right, while giving the player
    a range of time in which they know they have reached the planned trajectory, not a single spot
    that is either right or wrong. Matching a trajectory exactly by hand is very hard.

### 12. Holds on buttons, and a manoeuvre hold
- Three holds: **Prograde**, **Retrograde** and **Manoeuvre**.
  - They are buttons on the screen, not keys: P and R stop being hold keys.
  - They are one at a time, as prograde and retrograde are now.
  - Manoeuvre can be picked only while a node is set.
- The **manoeuvre hold** points the nose the way the next node's burn still needs: the guide's
  delta-v left.
- **The way to point for the coming node is shown:**
  - with a mark on the steering rings, so the player can fly it by hand: this is there already
    (the rings' burn diamonds, `NavigationControl.Burn`), and the user just hadn't seen it, so it
    is kept as it is;
  - and with a mark in the view, whatever the rings are doing.

### 13. The chase camera turns from straight down to straight up
- The chase view's drag (round the vessel) goes all the way from straight under the vessel to
  straight over it. Today it stops just below level and short of overhead (`ChaseCamera.MinPitch`,
  `MaxPitch`).
- It stops at both ends and never goes over the top and round.
- **Near the ground**, turning to look up from below pushes the camera along the ground under the
  vessel, never into it. Landed, that may be a close-up of the engine bell, which is fine.
- The user: as much free camera movement as possible is vital.
- **Free look** (the middle drag) still turns the view all the way round, hinged at the camera's
  place, as now.

### 14. The steering rings: one click to turn, both rings at once
- Today a ring steers only while it is held. A tap steers once, as a hold that ends at once.
- Instead, **clicking once on a ring** sets that angle (the heading ring a heading, the pitch ring
  a pitch). The vessel turns to it and holds it, with no need to hold the button.
- **Both rings can be clicked**, one after the other, and the vessel turns on both together, with
  no wait for one turn to finish before the other is set.
- Dragging a ring still works, as now.

## My calls (for the user to check), nodes and holds
- **"Closely matches"** is the delta-v still needed to match the planned trajectory, under a share
  of the node's own burn (a few per cent, tunable in `Scene`), held between 1 and 5 m/s. To be
  tuned by flying.
- **A node never matched stays** until it is deleted.
- **Once a node's time is past, the delta-v left is to the planned trajectory from where the
  vessel is now:** the velocity the planned path has nearest the vessel's place, less the
  vessel's own. So the guide and the hold still mean something if the burn starts late.
- **A held ring heading or pitch** (item 14) is a hold like the others:
  - Picking prograde, retrograde or manoeuvre ends it, and clicking a ring ends those.
  - Steering by the keys ends it.
  - Clicking a ring again sets it anew. With one ring's angle held, clicking the other keeps the
    first.
  - It shows on its ring as a mark, so the angle aimed for can be seen while the nose swings
    round.
- **The manoeuvre mark in the view** is in the travel markers' style, in the nodes' colour (green
  with the node).
- **The holds' buttons** sit with the HUD's flight readouts, where "Prograde hold: off (P)" is
  shown today. They go through `FlightInput`, as the keys did, and are listed in the readout. A
  hold that can't hold (manoeuvre with no node left) turns itself off.
- **Saving:** the chain (each node's delta-v, its place on its path and its time) and each vessel's
  view are saved with the vessel. The user doesn't need worlds kept from build to build, and is
  happy to start a fresh world each time, so `WorldStore.FormatVersion` is bumped wherever the
  saved form changes.
- **The chase camera at the extremes:** looking straight down or up, the camera's own up is the
  way it faced before, so the view never spins. The steering's "away from the camera" goes by the
  facing it had last at a usable angle.

## My calls (for the user to check), camera and map
- **Orbit direction:** small, faint chevrons spaced along each line, pointing the way it is flown,
  in the line's own colour.
- **Plane-crossing marks:** labelled AN and DN, with the angle between the planes, which is
  information, not an instruction (CLAUDE.md, "Low hand-holding").
  - They are shown only while the target is round the same body as the leg of the flown vessel's
    path they are on (the moon targeted from orbit round the planet, say). They are not shown for
    a target at another body.
- **What can be right-clicked to centre on:** objects only: a body, a vessel, a site or a point
  of interest, by its marker or the thing itself. The marks along paths (apoapsis, periapsis,
  nodes, AN/DN, closest approach, the travel markers) are not.
- **A swap by clicking a small view** goes by Tab's rule: the link-up every time, and the new
  vessel's own saved view.
- **A watched vessel behaves as a small view's does:** its fast collisions and near misses slow
  the game and drop the warp, as a small view's already do, and the main view's few witnessed
  near the camera are those near where the camera is. The flown vessel's own warning, coming mark
  and compass tick stay its own.
- **A camera object that goes away:** if the vessel the camera is on is lost or removed, the
  camera goes back to the flown vessel, as "Reset camera" would.
- **Where "Reset camera" sits:** a small HUD button, shown only while the camera is on something
  other than the flown vessel. It is listed in the readout for GameDriver scripts, with what the
  camera is on.
- **What the main camera is on counts as witnessed,** as a small view's vessel is. It is in its
  body's physics while watched, and moving the camera to a vessel at a body whose physics isn't
  made plays the link-up, as a small view's opening does. Watching works the same at either body.
- **While the camera is on something else:**
  - Steering by keys goes by the facing the camera last had on the flown vessel.
  - The steering rings aren't shown: they are round your own vessel, which may be far off.
  - Right-clicking your own vessel in the chase view, when it can be seen, recentres on it.
  - With the camera on your own vessel, the right-click opens its actions menu, as now.
  - In the chase view, M with the camera on another vessel gives the map centred on that vessel.
- **A vessel that has never been flown** (a stage just dropped) has no saved view, and gets the
  default view when it is first flown.
- **Free look is not saved.** A carried-on view looks at its object.
- **The map's per-body memory** of the angle and zoom it was left at (`_mapViews`) gives way to
  each vessel's own view.

## Non-goals and anti-goals
- No new keys: right-click and M are already the camera's. "Reset camera" and the three holds
  are buttons, and the P and R keys go (the controls list and the README say so).
- Nothing burns a node for the pilot: the manoeuvre hold only points the nose, and the pilot
  starts and stops the burn.
- No autopilot, and no "burn now" cue. The plane marks say where, never when to burn.
- Anti-goal: watching something else must never take control away from the flown vessel. What
  is watched is witnessed (as a small view's vessel is), and that is the only way watching
  changes the flight.

## Constraints
- CLAUDE.md's rules hold:
  - game logic through `FlightInput`;
  - every new button in the readout;
  - menus via `Hud.MenuAcross`;
  - store round-trip tests for every new `VesselState` field;
  - nothing written for exactly two bodies;
  - the main view's frame, precision and `BodyFrames` rules for a camera at another body.
- Docs to update:
  - `README.md` (the controls: right-click, M, "Reset camera");
  - `CLAUDE.md` (the point-of-interest rule; the camera's centre and the witnessed);
  - `TODO.md` (anything found is fixed on the branch, not deferred).

## Left to the builder
- How a view and its centre are represented and saved (a record on `Vessel`/`VesselState`, say).
- How a point of interest is named as a target (a new `TargetKind`), and how `Flight.Locate`
  finds it.
- The chevrons' spacing and size, and the plane marks' look.
- The exact place and height of a site's view.

## Verification
- Flight tests (`dotnet run --project tools/TestRun`), new ones for:
  - the view round-trip through the store;
  - carry-on and rewind restoring the view behind a link-up;
  - Tab and a small view's swap playing the link-up and bringing back the new vessel's view;
  - targeting a body by its orbit line and a point of interest by its marker;
  - the plane-crossing points (against a worked case);
  - the camera on another vessel keeping it witnessed;
  - the camera going back when its object is lost;
  - the rings ignoring the pointer while a node is selected;
  - the moon's marker gathered and drawn;
  - the chase camera reaching straight up and down, kept clear of the ground;
  - a node staying where it was set as the path changes before it;
  - editing, dragging and deleting a node redoing the chain after it;
  - a handle pushed back in taking away again at the slower rate, and on past zero;
  - a node dragged never before the one before it, the later ones moving with it;
  - one click on each ring turning the vessel to both angles together, and holding them;
  - a node turning green when matched, going when the engine is cut then, and the next snapping on;
  - the manoeuvre hold pointing along the burn left, offered only with a node;
  - the hold buttons working through `FlightInput`, with P and R doing nothing.
- `--before-merge` before the merge. The user has waived the soak and performance tests for this
  work, as for resuming missions.
- A GameDriver script in a scratch `GAMEONE_WORLDS`: right-click to recentre, Reset camera, M to a
  site's view, target the moon by its line, and check the readout.
