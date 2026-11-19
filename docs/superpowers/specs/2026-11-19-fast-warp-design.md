# Fast warp: the formula above the physics' rates, up to ×16,384

The design, from the discussion with the user on 2026-11-19 (recorded in `TODO.md`, "Time warp
that keeps up with its rate"). Branch `fast-warp`, worktree `.claude/worktrees/fast-warp`.

## Intent and why

The fastest warp, ×32, reaches about ×7 in play, so a trip to the moon is bearable and anything
further takes far too long in real time. The user wants a vessel going round the sun to finish a
lap in one to two seconds of real time: the planet's year is 12 days of 1,200 s (14,400 s), so
about ×7,000 to ×14,000, two to four minutes of game time each frame at 60 fps.

Today's warp can't get there by being made cheaper. `Flight.Advance` runs one full flight step
(every vessel's rules, a Jolt step at each body in use, the formula, the crossings, the look
ahead, the runs) per 1/60 s of game time, so a frame at ×N is N full steps, governed only by the
12 ms real-time budget (`Flight.WarpBudgetSeconds`). ×16,384 would be 16,384 steps a frame.

So, in the user's words, the formula must be able to do the trajectories, the orbits and the
unwitnessed collisions, with the physics brought in only as needed, as bubbles, for actual
collisions and near misses. At the high rates nothing is stepped at 1/60 s: every vessel is moved
by formula, over the whole frame at once, from event to event.

## Goals (the user's decisions)

1. **Two kinds of rate.** The low rates keep the physics for the flown and witnessed vessels, as
   today: ×1, ×2, ×4, perhaps ×8 (where the physics stops, sooner than ×32, is the measurements'
   call). Above them, the **formula's rates**: every vessel, the flown one included, moved by
   formula; no physics stepped anywhere. The user: keeping the physics at the low rates makes any
   difference in handling between the formula and the physics less noticeable.
2. **The ladder doubles all the way** ("the formula scale should not jump massively"):
   ¼, ½, ×1, ×2, ×4 in the physics (the paused warp under ¼ is `TODO.md`'s "Pausing with P",
   not this work), then the formula's from the next doubling on, ×8, ×16, …,
   ×8,192, **×16,384** (one past ×8,192, the user's choice: a lap of the sun in 0.88 s; at ×1,024
   the moon's lap takes 1.6 s). If the measurements let the physics go to ×8, the formula starts
   at ×16; the ladder is the same either way.
3. **One step a press, as today** (the user: "Just one at a time"; holding a rate key does not
   step on). A warp to a point on the path (`FlightInput.WarpTo`) goes at the new top rate.
4. **Where the formula's rates work** (the user: "the 'above the air' should be targeted at
   vessels flying in the air only"): every **witnessed** vessel (the flown one, each small view's,
   and the one the main camera is on: the user, "small views vessels count and where you might
   have your camera anchored to") must be either at rest (landed or afloat, carried round with its
   ground or its raft: a vessel that isn't moving needs no physics, the flown one included) or
   flying above the air (a body with none: above its orbit floor, its highest ground). While any
   witnessed vessel flies in the air or low over the ground, the physics' top rate is the fastest.
   So the formula's rates work waiting on a pad, on the moon, afloat, and in orbit alike.
5. **What drops the warp by itself** to the physics' top rate, ahead of it (within a frame or so),
   so the physics is running as it happens, for any witnessed vessel:
   - the air coming up, or the ground coming within reach (flying);
   - a vessel at rest set moving: the throttle raised, the legs, staging, any steering key;
   - a collision or a near miss warned of (see 5a);
   - a manoeuvre node coming up;
   - a burn's fuel running out.
   **Crossing a sphere's edge doesn't drop it**, so a long coast past the moon goes on. A warp to a
   point on the path still stops on that moment; dropped on the way, it ends, as today.
   **The warp never climbs back by itself** (the user): a drop lands at the physics' top rate and
   stays there until the player raises it; climbing out of the air at ×4 stays at ×4; a rate key
   past the physics' top rate while it isn't allowed does nothing.
   - **5a. A collision or near miss coming at a witnessed vessel, two levels:** warned of at a
     formula rate, the warp drops to the physics' top rate, early enough that the physics' look
     ahead (20 s) has it in time; at the physics' rates today's behaviour holds (its warning drops
     the warp to ×1, once each). So one first seen at ×16,384 drops to ×4, then to ×1 as it nears.
     The user: the collision's slow-down is a gameplay mechanic, and what matters is that it plays
     out in full as it passes; if that needs the time scaled outside the rates the player can pick,
     that is fine.
6. **Burns work in warp at any rate**, under the formula, the aim held by a hold (prograde,
   retrograde, the manoeuvre hold, the rings'). The user: a burn with little delta-v left would
   burn out too fast to stop by hand, but the burns this is for (long ones, later between solar
   systems) have plenty; and the fuel running out drops the warp (5).
7. **The steering rings work in warp at every rate** ("less twitchy controls compared to keyboard
   taps"): a click on a ring sets its angle and the vessel turns to it, as at ×1; at a high rate
   the turn, a few seconds of game time, is over within a frame. **A steering key at a formula
   rate drops the warp** to the physics' top rate, so the keys always steer as they do today. The
   throttle and the holds go on working in warp.
8. **The light averaged at the high rates** (on the ground as much as in orbit, as the formula's
   rates work there too). At ×16,384 the planet's day passes some 14 times a real second. Above the rate where a day passes faster than about one in 5 s real (about ×256)
   the light is shown averaged, not flickering: an even dusk over the ground, the sun's disc
   hidden or blurred into its band; the true light comes back as the warp drops. The pad and
   landing lights hold steady at their average meanwhile (my call, told to the user).
9. **Physics only as bubbles.** Vessels in orbit near each other but plainly on no collision course
   need no physics; nor does a vessel that isn't moving (landed, even the one flown); the vessels
   the player controls are what set the physics off (anything on course to collide with one or
   pass it in a near miss, or the player moving a landed one). At the formula's rates that means
   no physics at all: a collision or near miss coming at a controlled vessel drops the warp (5),
   and the physics' rates' handover (`Unwatched`) brings the physics in round it as today.
   Collisions between vessels nobody controls are the formula's (as `Unwatched.FindCollisions`
   judges them today), at every rate.
10. **The unwitnessed never bring the physics in, nor change the warp** (the user: "Physics
    cannot be enabled and warp changed for any unwitnessed vessel"). Whatever happens to a vessel
    nobody watches, the formula plays it out, at every rate:
    - **Staging:** a simple version of the physics' (the user: "numbers wise it's about delta v"):
      the stage dropped, the rest going on with its own mass and fuel; a fairing's halves given an
      estimated way (clipping ignored, as there is no physics to see it), each then under formula
      like any vessel. Today `Vessel.Separate` returns nothing under formula, so this is new.
    - **Settling in the water:** afloat by the formula's own outcome, never handed back (today a
      plunge into shallow water, `Formula.PlungeReach`, and a landing on a raft go back to the
      physics: no more).
    - **Crashing:** wrecked by the formula's judgement at once, with no wreck played out in the
      physics.
    - Parting, touching and not yet at rest, aboard a raft (carried by the raft's pose, a function
      of world time), on the pad and landed: all held or carried by the formula, never by bringing
      the physics in.
    My reading (told to the user): this is about a vessel on its own. One near a witnessed vessel,
    within the handover's reach (`Unwatched`, `Scene.HandoverDistance`), is in the physics with it,
    as today, at the physics' rates: that is the witnessed vessel's bubble, not the unwitnessed one
    bringing the physics in.
11. **Only one reason restricts the warp:** a witnessed vessel flying in the air (or low over the
    ground: goal 4), shown on the HUD while it holds the rate, with the vessel's name (and in the
    readout). My call: a witnessed vessel not yet at rest (settling, still sliding) counts as
    flying for this. Every other drop (goal 5) is a drop, not a restriction, and nothing holds the
    warp for a vessel nobody watches.
12. **First, measure.** Nothing yet has timed a frame at ×32. Before building, `Timings` at ×32 on
    a moon transfer, in a low orbit and in a busy world (the hundred satellites), to find what a
    frame spends its time on. It sets where the physics' rates stop (1), and says which of
    `TODO.md`'s performance ideas are worth doing for the physics' rates.

## Non-goals

- Leaving the physics out at ×1 where nothing needs it (a flown vessel alone): the user wants it
  tried, but as an optimisation of its own, after this (it stays in `TODO.md`).
- Physics worlds with their own local origins, travelling with their vessels ("Local physics, as
  an experiment" and its bubbles that travel, in `TODO.md`): the bubbles here are today's physics
  at each body, used only round what needs it. The local-origin worlds stay that entry's.
- The flown vessel staging under formula: its fuel running out drops the warp (5), so its stage
  is dropped at the physics' rates, as today. (The unwitnessed do stage under formula: goal 10.)
- Rates past ×16,384, and travel between solar systems: later, doubling on past it.
- Faster physics steps: the physics' rates stay full Jolt steps of 1/60 s of game time.

## Anti-goals

- **Nothing skipped.** A step of minutes must never pass a vessel through the ground, the air's
  top, another vessel, a sphere's edge or the solar system's edge unseen. Today four things are
  safe only because a step is 1/60 s, and must not be at the formula's rates: `Flight.Cross` (a
  vessel's body chosen by its place at the step's end), `Unwatched.FindCollisions` (which resets
  if more than `MostBehind`, 1 s, passes between calls), the look ahead's 20 s horizon
  (`Scene.CollisionHorizonSeconds`, game seconds, unscaled), and the dusk and dawn of the lights
  (sampled once a frame).
- **No change to the physics' rates' behaviour.** Flying at ×1 to ×4 is as it is today, and the
  flight tests that fly there pass unchanged.
- **No hand-holding.** The warp dropping is information, never a prompt (CLAUDE.md, "Low
  hand-holding").

## Constraints

- CLAUDE.md holds: one set of controls (the rate keys' ladder goes in the HUD's list and the
  README), game logic reachable through `FlightInput`, `Scene` for the tunables (the ladder, the
  hold's repeat, the averaging's rate), everything in each body's frame, double where precision
  needs it, the readout (`Readout.cs`) showing what a script might fly by (the rate asked and
  reached, whether the formula's rates are in use, why the warp dropped).
- Nothing deferred: what is found on the branch is fixed there.
- The frame holds 60 fps at the top rate: my call for the target, ×16,384 reached (within 10%)
  at 60 fps paced in Release on a moon transfer and in the hundred satellites' world (the short
  performance tests' `HundredSatellites`), and the `Flight.Capacity` world (1,000 vessels)
  measured and reported, its target set from what is measured.

## What exists to build on (from reading the code)

- `Formula` already moves every unwitnessed vessel: Kepler in closed form on its orbit
  (`Orbit.StateAfter`), stepped every `Scene.FormulaStepSeconds` (0.1 s) in the air, low or
  burning, with fuel burnt and touches judged once (ground, sea, towers, masts, break-up). It
  snaps a guided vessel's attitude to its aim at once, flies no nodes (`FollowNodes` and the
  manoeuvre hold's `BurnLeft` skipped) and never stages.
- `Patched.Legs` finds sphere crossings analytically (`Enters`, sampled every 2 s with a graze
  allowance, then bisected), and the edge (`LeavesAt`); only the step-by-step actual crossing is
  point-sampled.
- The rafts' poses are functions of world time (`Rafts`), set by the physics each step.
- Warp is today a loop of full steps (`Flight.Advance`), with the real-time budget, the "asked,
  reached" readout (`Timings.WarpAsked`, `WarpReached`) and the paced windowed test
  (`PerformanceTests`) that measures a frame at the highest rate.

## How (decided parts)

- **A frame at a formula rate is one advance, not N steps.** The world clock moves by the frame's
  game time (real seconds × the rate, up to 273 s at ×16,384 with a 1/60 s frame, more in a slow
  frame), each vessel by formula over it, cut at the earliest event that matters (5, a warp
  target, a crossing, a collision between vessels under formula, the solar system's edge) and
  carried on from there within the frame. The physics' worlds are not stepped.
- **The flown and witnessed vessels leave the physics** as a formula rate is taken (as
  `LeavePhysics` does for the unwitnessed), and come back as it drops (`ReturnToPhysics`), the
  flown one first, its body's physics made ahead (`Worlds.Ahead`) so the drop never waits.
- **The formula flies a controlled vessel's holds and burns** (6, 7): the aim turned by the hold
  at the vessel's turn rate (not snapped: at the high rates the turn is over within a frame
  anyway), the manoeuvre hold along the next node's burn still needed, the throttle as set; fuel
  burnt over the advance.
- **Crossings at the formula's rates** from the orbit (the moment its leg crosses, as
  `Patched.Legs` finds), each vessel handed across at that moment, never late.

## Left to the builder

- How the formula's simple staging, settling and crashing (goal 10) are worked out, within the
  user's allowance (delta-v by the numbers, a fairing's way estimated, clipping ignored), and the
  tests that show their outcomes near enough the physics' for the vessels the game has.
- How the rate is modelled (the player's rate, and what holds it lower and why, for the HUD and
  the readout), given that nothing raises it by itself (5).
- How collisions between vessels under formula are found over minutes a frame without the cost of
  `Unwatched.Check`'s 0.25 s windows (960 a frame at the top rate): longer windows bounded by the
  orbits, the map's `CollisionSearch`'s way, or another; none skipped.
- How a burn is stepped over a frame (today 0.1 s; 2,700 steps a frame at the top rate per
  burning vessel), within the budget and without the orbit drifting.
- What the HUD, the map and the small views show at the formula's rates (everything as at ×1,
  only faster, is my suggestion), and how the warp's reason for dropping is shown.
- The sea's waves and the eclipses at those rates (whether they skip rather than ease), and how
  the averaged light is drawn (the sky, the ground, the sun's disc, the shadows, the moon's face).
- The parts and their order. My suggestion: (1) measure, and the ladder;
  (2) the formula's rates for coasting (every vessel on Kepler, the flown one included,
  crossings and collisions over the frame, what drops and holds the warp); (3) the unwitnessed
  played out by the formula alone (staging, settling, crashing, rafts); (4) burns, the holds and
  the rings in warp; (5) the averaged light.

## Verification

- Flight tests (`dotnet run --project tools/TestRun`) for each rule: the ladder and the
  one step a press; a coast at ×16,384 ending where Kepler puts it; ×16,384 waiting on the pad and on
  the moon, the vessel where it stood, and the throttle raised dropping it; a small view's vessel
  in the air holding it at ×4; a drop never climbing back; a crossing into the moon's sphere
  at the top rate handed across at its moment, the warp carrying on; the air, the ground, a
  collision and near-miss warning, a node and a burn's fuel running out each dropping the warp
  to the physics' top rate ahead of it; a collision first warned of at ×16,384 dropping to ×4,
  then to ×1, its slow-down played out in full; a steering key dropping it and the rings not; a burn in
  warp along prograde and along a node matching the same burn at ×1 within a tolerance; two
  vessels under formula colliding during a top-rate frame, judged as at ×1; the solar system's
  edge never passed unseen; an unwitnessed vessel staging (its fairing's halves too), settling in
  the water (shallow water and a raft included) and crashing under formula at ×16,384, none of it
  bringing the physics in or changing the warp; the only restriction shown being a witnessed
  vessel flying in the air, by name; the physics' rates' tests unchanged.
- The paced windowed performance test at ×16,384 (a moon transfer and the hundred satellites) and
  the measurements of (12) before and after, reported.
- Playtesting and tuning by the user (the user: it "will need playtesting and tuning for sure to
  get the right balance between performance and scale of warp"): so the ladder's top, where the
  physics' rates stop, the averaging's rate and the formula's step sizes are each one number in
  `Scene`, and the branch is handed over for a playtest before the merge.
- `--before-merge` before the merge; a GameDriver script in a scratch `GAMEONE_WORLDS` warping a
  moon trip at the top rate, with the readout's rate asked and reached.
