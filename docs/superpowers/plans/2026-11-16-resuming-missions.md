# Resuming Missions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Missions belong to their vessels. A world keeps any number of missions in progress across
saves, and loading a world lets the player carry one on (or any intact vessel of no mission) exactly
where it was. Trying again is either "start the mission again" or "rewind" to the session's chosen
point.

**Architecture:** A new `MissionRun` (one per launch, keyed by its flight number, which every vessel
already carries in `Vessel.Flight`) replaces `Flight`'s single `Mission`/`Progress`/`_vessels`. Steps
are judged against a run's vessels, and every run in progress is updated each step. The world
saves the runs in progress (`RunState`) and the flown vessel. A new `Flight.Restore` brings a
world to life without starting a flight. New `FlightInput` fields carry the menu's choices, so the
tests drive everything as the HUD does.

**Tech Stack:** C# / .NET 10, xUnit, Jolt via BRUTAL, ImGui HUD.

**Spec:** `docs/superpowers/specs/2026-11-16-resuming-missions-design.md`. Read it before each task;
it holds the decisions this plan argues from.

## Global Constraints

- No migration of old saves. `WorldStore.FormatVersion` goes from 2 to 3, and older files list as
  unreadable.
- Game logic is reachable through `FlightInput`. No test code goes in the game.
- Every new saved field round-trips in `WorldStoreTests`.
- New menu screens are placed with `Hud.MenuAcross` and never cover the credits (logo and NOTICE).
- The readout (`Readout.cs`) lists every new button and the runs in progress, for GameDriver scripts.
- The rewind point is never saved and never earlier than the load.
- Resuming never adds, moves or removes anything in the world.
- ImGui: escape `%` in `TextColored`/`TextWrapped`/`SetTooltip`. Changing labels use `###id`.
- Build and test with `--disable-build-servers -nodeReuse:false`, then run `dotnet build-server shutdown`.
- Files are CRLF. Comments say what and why, never history.
- Commit only when the user asks; each task's "Commit" step is a checkpoint, run once the user
  has approved committing on this branch.

## Review Focus

1. **A world saved while its only vessel is a placeholder rocket on the pad** (an empty world
   loaded, then quit before choosing). Nothing is saved until a choice is made. Pinned in Task 2
   (`A_world_left_before_a_choice_is_saved_as_it_was`).
2. **A run in progress whose unlaunched rocket stands on the pad a new mission launches from.**
   That run is abandoned and logged, and its rocket is removed. A run's rocket on another site's
   pad is untouched. Pinned in Task 1 (`A_new_mission_leaves_another_sites_waiting_rocket`) and
   Task 4 (`A_waiting_rocket_cleared_from_the_pad_abandons_its_run`).
3. **The last-flown vessel of a run lost while another run is flown.** Carrying the run on flies
   another intact vessel of it. Pinned in Task 3 (`Carrying_on_a_run_whose_last_flown_is_gone_flies_another_of_it`).
4. **Rewinding after Tab to another run's vessel.** It goes back to the menu choice, not to the
   Tab. Pinned in Task 5 (`A_rewind_goes_back_to_the_menu_choice_not_a_switch`).
5. **The 1,000-vessel cap removing a run's required vessel.** Debris and no-run vessels go first,
   and a run that loses a required vessel fails. Pinned in Task 6.

---

### Task 1: Runs: missions judged by their own vessels, many at once

**Files:**
- Create: `src/GameOne/MissionRun.cs`
- Modify: `src/GameOne/Mission.cs` (steps take a run; `Succeeded`/`Failed`/`Score`/`IsLost` move to the run)
- Modify: `src/GameOne/MissionProgress.cs` (points per step; updated from a run)
- Modify: `src/GameOne/Flight.cs` (`_vessels`, `Mission`, `Progress` and `FlightNumber` replaced by runs; `StartFlight` clears only its own pad; `Step` updates every run; staging adds parts to their run)
- Modify: `src/GameOne/Hud.cs`, `src/GameOne/Readout.cs` (read `flight.Run`)
- Test: `tests/GameOne.Tests/MissionRunTests.cs` (new); update the tests that read `Flight.Mission.Failed(flight)`, `Score(flight)`, `IsLost(i, flight)` or `Flight.Progress` (FlightTests, FreePlayTests, HeightTests, LanderTripTests, MissionFailureTests, MissionProgressTests, MoonMissionTests, OrbitTests, SatelliteTests, SoakTests, WorldTests, FrameFlightTests)

**Interfaces:**
- Produces:
  - `internal enum RunEnd { InProgress, Completed, Failed, Abandoned }`
  - `internal sealed class MissionRun`, with:
    - `Mission Mission`, `LaunchSite Site`, `int Number` (its flight number), `double BegunAt`;
    - `IReadOnlyList<Vessel> Vessels` (rocket first, then parts in the order they came off);
    - `MissionProgress Progress`, `int LastFlownId { get; set; }`, `RunEnd End`, `string? EndReason`;
    - `double WorldTime` (its flight's);
    - `bool Succeeded`, `bool Failed`, `int? Score`, `bool IsLost(int step)`;
    - `bool OptionalOpen` (every required step done, and an optional step neither done nor lost);
    - `internal void Add(Vessel v)`, `internal void Remove(Vessel v)`, `internal void Update()` (ticks steps).
  - `MissionStep(string Label, Func<MissionRun, bool> Done, bool Optional = false, Func<MissionRun, bool>? Lost = null, Func<MissionRun, int>? Points = null)`
  - `MissionProgress.PointsFor(int step) -> int`
  - On `Flight`:
    - `IReadOnlyList<MissionRun> Runs`: in progress with objectives, oldest first;
    - `MissionRun? Run`: the flown vessel's run while it is in memory (one in progress, or this session's free-play run);
    - `MissionRun? RunOf(Vessel v)`;
    - `Mission Mission => Run?.Mission ?? Mission.FreePlay`;
    - `IReadOnlyList<Vessel> Vessels`: every vessel in the world with the flown vessel's `Flight` number, in order.

- [ ] **Step 1: Write the failing tests** in `MissionRunTests`, each flying through `Simulation`:
  - `Two_runs_in_progress_each_tick_their_own_steps`: launch a test mission A (a booster, with a "Launch" step and a switch step), then choose mission B. Assert `Runs.Count == 2`. With A's switch on and B's vessel flown, one `Step` ticks A's step 1 (`Runs[0].Progress.IsDone(1)`) and leaves B's not done.
  - `The_panels_run_follows_the_flown_vessel`: after the two launches, `SwitchTo` A's rocket gives `flight.Run == Runs[0]`, and switching back gives `Runs[1]`.
  - `A_new_mission_leaves_another_sites_waiting_rocket`: choose mission A at Highcrest and don't launch it, then choose B at Home. A's rocket is still in `WorldVessels` with `State == Ready`, and `Runs[0].End == RunEnd.InProgress`.
  - `Parts_that_come_off_belong_to_their_run`: stage a two-stage rocket of run A while flying it. Both parts are in `Runs[0].Vessels`.
- [ ] **Step 2: Run them to see them fail.** Run: `dotnet test GameOne.slnx --disable-build-servers -nodeReuse:false --filter MissionRunTests`. Expected: build errors (no `MissionRun`).
- [ ] **Step 3: Implement `MissionRun`, the step signature change and `Flight`'s runs.**
  - The steps' `f.Vessels`/`f.Mission` read the run's own.
  - LAUNCH AND LAND's "Climb past" reads `r.Vessels[0].ReachedTarget`, the rocket's, not the flown vessel's.
  - `StartFlight` keeps removing wrecks and anything `InTheWay` of its own site's pad, but no longer every Ready vessel in the world.
  - `Step` calls `Update()` on every run in progress (and on this session's free-play run) where it called `Progress.Update(this)`.
  - `SwitchToNext` cycles `Flight.Vessels`.
  - Remove `FlightNumber`; its users read `Run?.Number` or `Controlled.Flight`.
- [ ] **Step 4: Update the existing tests and the HUD to the new names.** Run: `dotnet run --project tools/TestRun`. Expected: all pass.
- [ ] **Step 5: Commit** (`Runs: each mission judged by its own vessels, any number in progress`).

### Task 2: The world saves its runs and its flown vessel; Restore starts no flight

**Files:**
- Modify: `src/GameOne/World.cs` (add `RunState`; `WorldState.Runs`, `WorldState.Flown`; `VesselState.Site`)
- Modify: `src/GameOne/WorldStore.cs` (`FormatVersion = 3`)
- Modify: `src/GameOne/Vessel.cs` (`LaunchSite Site`, captured and restored)
- Modify: `src/GameOne/Flight.cs` (`Restore`, `Chosen`, `Capture` of runs and the flown vessel; `Load` = `Restore` + `StartFlight`)
- Test: `tests/GameOne.Tests/WorldStoreTests.cs`, `tests/GameOne.Tests/MissionRunTests.cs`

**Interfaces:**
- Consumes: `MissionRun` (Task 1).
- Produces:
  - `internal sealed record RunState { required Mission Mission; required string Site; required int Number; required bool[] Done; required int[] Points; int LastFlown; double BegunAt; }`
  - `WorldState.Runs: IReadOnlyList<RunState>` (in progress with objectives only); `WorldState.Flown: int?` (vessel id)
  - `VesselState.Site: string` (`LaunchSite.Name`, default `LaunchSites.Home.Name`)
  - `MissionRun(Flight flight, RunState state)`, `MissionRun.Capture() -> RunState`
  - `Flight.Restore(WorldState world)`: brings the world to life, its runs restored and `Controlled` the saved flown vessel. A world with no flyable vessel gets a placeholder rocket of free play, Ready, on the current `Site`'s pad. The placeholder is removed unlogged when a choice is made, with `_flights` and `_nextId` put back.
  - `Flight.Chosen: bool`: false after `Restore` until a menu choice (Tasks 3 and 5) or `Load`.
  - `Flight.Load(WorldState, Mission, LaunchSite?)` stays (tests rely on it), as `Restore` then a chosen `StartFlight`.

- [ ] **Step 1: Write the failing tests.**
  - `WorldStoreTests.Runs_and_the_flown_vessel_round_trip`: a `WorldState` with one `RunState` (every field set: `Done = [true, false, true]`, `Points = [0, 0, 1250]`, `LastFlown = 4`, `BegunAt = 123.5`, `Site = "Highcrest"`) and `Flown = 4` saves and loads equal field by field.
  - Extend the existing vessel round-trip to cover `Site`.
  - `MissionRunTests.A_restored_world_keeps_its_runs_and_flown_vessel`: launch LAND ON THE MOON's test stand-in, tick a step, then `Restore(Capture())`. `Runs[0].Progress.IsDone` matches, `Controlled.Id` is the same, and `WorldVessels` holds the same ids (no new rocket).
  - `MissionRunTests.A_world_left_before_a_choice_is_saved_as_it_was`: `Restore` an empty world. `Chosen` is false, and `Capture()` with the placeholder excluded has no vessels and the same `Flights` and `NextVesselId`.
- [ ] **Step 2: Run them to see them fail.** Run: `dotnet test ... --filter "WorldStoreTests|MissionRunTests"`. Expected: build errors.
- [ ] **Step 3: Implement** the records, `FormatVersion = 3`, `Vessel.Site`, `Restore`, `Chosen` and `Capture`. `Capture` leaves out the placeholder while `!Chosen`, and `Program.Save` will skip saving while `!Chosen` (Task 7).
- [ ] **Step 4: Run all the tests.** Run: `dotnet run --project tools/TestRun`. Expected: all pass.
- [ ] **Step 5: Commit** (`Worlds save their missions in progress and the vessel flown`).

### Task 3: Carrying on a run, or a vessel of no run

**Files:**
- Modify: `src/GameOne/Flight.cs` (with `FlightInput`, the input record defined there)
- Test: `tests/GameOne.Tests/MissionRunTests.cs`, `tests/GameOne.Tests/MoonMissionTests.cs`

**Interfaces:**
- Consumes: `Restore`, `Chosen`, `RunState` (Task 2).
- Produces:
  - `internal sealed record CarryOnChoice(Vessel Vessel, MissionRun? Run)`
  - `Flight.CarryOnChoices() -> IReadOnlyList<CarryOnChoice>`, listing:
    - each run in progress, oldest first, with its last-flown vessel if intact, else its first intact guided vessel; a failed run is never listed;
    - then every intact vessel with `Guided` true and no run in progress: the last flown first, then by `Flight`, newest first.
  - `FlightInput.CarryOn: int?` (a vessel id). It flies that vessel (`Controlled`, its run's `LastFlownId`), removes the placeholder, sets `Chosen`, and sets the rewind point (Task 5's `RewindPoint` with `Resumed = true`).
  - `SwitchTo` keeps the flown vessel's `Run.LastFlownId` up to date.

- [ ] **Step 1: Write the failing tests.**
  - `MoonMissionTests.A_moon_mission_saved_on_the_way_is_carried_on_where_it_was`: a world with the lander landed by the spot and its bay closed, its run's first two steps done. Use `Simulation.Reloaded()` (changed in this task to `Restore` and then carry on `Flown`). Assert the same `Controlled.Id`, its `Position` within 1 mm, no vessel in `State == Ready`, and the steps still done. Then open the bay doors (as `BayDoorTests` does) and assert `Run.Progress.IsDone` for "Open its bay doors".
  - `MissionRunTests.Carrying_on_a_run_whose_last_flown_is_gone_flies_another_of_it`: run A staged into two parts, last flown part 2, part 2 removed. `CarryOnChoices()[0].Vessel` is part 1, and `CarryOn` gives `Controlled == part 1` and `Run == A`.
  - `MissionRunTests.A_free_play_vessel_is_carried_on_with_no_run`: a free-play launch, saved and restored. The choices list its vessel with `Run == null`. After `CarryOn`, `flight.Run` is null and `flight.Mission.HasObjectives` is false.
  - `MissionRunTests.Debris_and_wrecks_are_never_choices`: a fairing half and a crashed vessel are absent from `CarryOnChoices()`.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement** `CarryOnChoices`, `CarryOn` and `Simulation.Reloaded`'s carry-on.
- [ ] **Step 4: Run all the tests.** Run: `dotnet run --project tools/TestRun`. Expected: all pass.
- [ ] **Step 5: Commit** (`Carry on a mission in progress, or any vessel, from where it was`).

### Task 4: How a run ends, and the world log of missions

**Files:**
- Modify: `src/GameOne/MissionRun.cs`, `src/GameOne/Flight.cs`, `src/GameOne/WorldLog.cs`
- Test: `tests/GameOne.Tests/MissionRunTests.cs`, `tests/GameOne.Tests/WorldLogTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3.
- Produces:
  - `MissionRun` ends itself in `Update()`:
    - `Completed` once `Succeeded` and not `OptionalOpen`;
    - `Failed` once `Failed`, with `EndReason` the first lost required step's label.
  - Inputs:
    - `FlightInput.CompleteRun: bool`: completes the flown vessel's run while `OptionalOpen`; optional steps not done are lost;
    - `FlightInput.AbandonRun: bool`: `End = Abandoned` for the flown vessel's run.
  - `Flight.Run` keeps returning an ended run of the flown vessel until the next switch or start (`_shownEnded`), so the panel and the result stay.
  - `Flight.Notice: (string Text, double RealTime)?`: set when a run the flown vessel isn't in ends, for example `"REACH ORBIT (flight 5) complete: 1,800 pts"`.
  - Log entries, as `LogEntry` with `What` = `$"{mission.Name} (flight {n})"`, `Speed` null, `Where` = the site's name, and `Witnessed` true. `How` is one of these new `WorldLog` texts:
    - `Begun`;
    - `StepDone(label, points)`;
    - `Completed(score)`;
    - `FailedBecause(label)`;
    - `Abandoned`;
    - `StartedAgain`.
  - Free play writes no mission entries.
  - `StartFlight` abandons, and logs as abandoned, a run in progress whose Ready rocket it clears from its own pad.

- [ ] **Step 1: Write the failing tests.**
  - `A_run_with_no_optional_steps_completes_when_its_steps_are_done`: `End == Completed`, the run is gone from `Runs`, and its vessels are not in any run.
  - `A_run_with_an_optional_step_open_waits_for_complete_mission`: after the required steps, `End == InProgress` and `OptionalOpen`. After `CompleteRun`, `End == Completed` and the open step is not done.
  - `A_failed_run_ends_with_the_lost_step_named`.
  - `An_abandoned_run_leaves_its_vessels_in_the_world`.
  - `A_run_ending_unflown_gives_a_notice`: run A completes while B's vessel is flown, and `Notice?.Text` starts with A's name.
  - `The_ended_runs_result_stays_until_a_switch`: `Run` is still the ended run after the end, and null after `SwitchTo` another vessel.
  - `A_waiting_rocket_cleared_from_the_pad_abandons_its_run`: A unlaunched at Home, then B chosen at Home. A is `Abandoned` and the log has its entry.
  - `WorldLogTests.Missions_are_logged`: begun, one step with its points, then completed with its score, in order.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all the tests.** Run: `dotnet run --project tools/TestRun`. Expected: all pass.
- [ ] **Step 5: Commit** (`Missions complete, fail or are abandoned, and the world log keeps them`).

### Task 5: Trying again: start the mission again, or rewind

**Files:**
- Modify: `src/GameOne/Flight.cs` (remove `_atStart` and `FlightInput.Restart`)
- Test: `tests/GameOne.Tests/MissionRunTests.cs`; update tests that use `Restart = true`

**Interfaces:**
- Consumes: Tasks 1 to 4.
- Produces:
  - `internal sealed record RewindPoint(WorldState World, bool Resumed, int? VesselId, Mission Mission, LaunchSite Site)`. It is taken at each menu choice: before `StartFlight`'s rocket for a new mission or a start-again (`Resumed = false`), and at `CarryOn` (`Resumed = true`).
  - `Flight.CanRewind => _rewind is not null`; `Flight.RewindSeconds => WorldTime - _rewind.World.Time`; `Flight.RewindResumed`.
  - Inputs:
    - `FlightInput.StartAgain: bool`: abandons the flown vessel's run if it is in progress (logged `StartedAgain`), then starts `Run?.Mission ?? Controlled.Mission` at `Run?.Site ?? Controlled.Site`;
    - `FlightInput.Rewind: bool`: `Restore(_rewind.World)`, then the same choice again (`StartFlight(Mission, Site)`, or `CarryOn(VesselId)`), keeping the same rewind point.

- [ ] **Step 1: Write the failing tests.**
  - `Starting_again_leaves_the_old_vessels_and_the_rest_untouched`: in flight, with another run's vessel in the world, `StartAgain` gives a new Ready rocket, the old vessels still in `WorldVessels`, the other run's progress unchanged, and the old run `Abandoned`.
  - `Starting_again_after_a_failure_starts_the_vessels_own_mission`: a crashed run, flying its surviving booster, `StartAgain` starts a new run of that mission at its site.
  - `A_rewind_brings_back_the_whole_world_as_it_was_at_the_choice`: carry on, fly 30 s and change another run's vessel, then `Rewind`. `WorldTime` equals the point's time, every vessel's position equals the snapshot's, and `Log.Count` is as it was. `RewindSeconds` before the rewind was 30 within a frame.
  - `A_rewind_goes_back_to_the_menu_choice_not_a_switch`: carry on A, Tab to B's vessel, fly, `Rewind`. Back at the carry-on time, flying A's vessel.
  - `There_is_nothing_to_rewind_to_before_a_choice`: after `Restore`, `CanRewind` is false.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement**, and change the existing tests' `Restart = true` to `StartAgain` or `Rewind`, whichever each meant: a rewind where it checked the world came back.
- [ ] **Step 4: Run all the tests.** Run: `dotnet run --project tools/TestRun`. Expected: all pass.
- [ ] **Step 5: Commit** (`Try again: start the mission again, or rewind to the moment chosen`).

### Task 6: Making room removes debris first

**Files:**
- Modify: `src/GameOne/Flight.cs` (`MakeRoom`, and the over-capacity trim in `Restore`)
- Test: `tests/GameOne.Tests/WorldTests.cs`

**Interfaces:**
- Consumes: `Flight.RunOf` (Task 1).
- Produces: `MakeRoom` removes oldest first within groups, in this order:
  - debris (`!Guided`);
  - then vessels in no run in progress;
  - then runs' vessels.
  It never removes `Controlled`.

- [ ] **Step 1: Write the failing tests.**
  - `Making_room_takes_debris_before_vessels`: a world at `Capacity` with old guided vessels and newer fairing halves. Adding a vessel removes a fairing half, not the oldest guided vessel.
  - `Making_room_takes_a_runs_vessels_last`: a run's old vessel survives while no-run vessels remain. Once it is removed, a run needing it fails.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all the tests.** Run: `dotnet run --project tools/TestRun`. Expected: all pass.
- [ ] **Step 5: Commit** (`Making room takes debris first, a mission's vessels last`).

### Task 7: The HUD and the game: carry-on screen, try again, complete and abandon

**Files:**
- Modify: `src/GameOne/Hud.cs`:
  - `MenuScreen.CarryOn` and `MenuScreen.TryAgain`;
  - `MustChoose`;
  - the carry-on screen (mockup B);
  - the game menu's "Restart flight" opening TryAgain, and "Abandon mission";
  - the panel's "Complete mission" button and its following of `flight.Run`;
  - the notice.
- Modify: `src/GameOne/Program.cs`:
  - Play, New, Reset and Delete use `Restore`, then `hud.OpenCarryOn(flight)`, which goes to Missions when there are no choices;
  - `Save()` returns early while `!flight.Chosen`;
  - the new inputs passed into `FlightInput`.
- Modify: `src/GameOne/Readout.cs` (the carry-on lines, the try-again buttons, `Run`, `Notice`)
- Test: `tests/GameOne.Tests/MenuLayoutTests.cs`, `tests/GameOne.Tests/CreditsTests.cs`

**Interfaces:**
- Consumes: every earlier task's inputs and properties.
- Produces:
  - The carry-on screen, titled with the world's name:
    - one button a choice: "Carry on: {MISSION} · flight {n} · {done} of {steps}" with its points and a quiet line on what its vessel is doing, for a run; the vessel's name, body and state for a vessel of no run;
    - "New mission…" (to Missions) and "Back to worlds";
    - it scrolls when long, as the missions list does (`ListRegion`).
  - `Hud.MustChoose: bool`, true from `OpenCarryOn` until a choice. While it is true, `PressEsc` and Close do nothing on the carry-on and missions screens.
  - TryAgain is a small window. Its buttons:
    - "Start the mission again";
    - "Rewind {m} min {s} s, to when this mission {began | was resumed}" (only `CanRewind`), which asks for confirmation with the text "Everything since then is undone: other vessels, other missions and the world log.";
    - Cancel.
  - Readout button names: `"Carry on: <text>"`, `"Start again"`, `"Rewind"`, `"Confirm rewind"`, `"Abandon mission"`, `"Complete mission"`, `"New mission"`, `"Back to worlds"`.

- [ ] **Step 1: Write the failing layout tests**: `MenuLayoutTests.The_carry_on_screen_keeps_clear_of_the_credits` and `The_try_again_window_keeps_clear_of_the_credits`, at the window sizes the existing menu tests use.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement the HUD, Program and readout.**
- [ ] **Step 4: Run all the tests.** Run: `dotnet run --project tools/TestRun`. Expected: all pass.
- [ ] **Step 5: Check the running game with GameDriver** (see `tools/GameDriver/CLAUDE.md`). First check `tasklist //FI "IMAGENAME eq GameOne.exe"` shows none of the user's, and set `GAMEONE_WORLDS` to a new empty scratch folder. Then script the following and confirm the readout shows the same vessel id, its place within a metre, and the steps done:
  1. new world, LAUNCH AND LAND, launch, fly 10 s;
  2. quit, relaunch, pick the world;
  3. "Carry on: LAUNCH AND LAND".
  Then: Esc, "Restart flight", "Rewind", "Confirm rewind", and confirm the time is back.
- [ ] **Step 6: Commit** (`The carry-on screen, try again, and completing or abandoning a mission`).

### Task 8: Docs, and the branch made ready to merge

**Files:**
- Modify:
  - `CLAUDE.md`: the opening paragraph's "Missions are flown in saved worlds…"; "Game code" (`MissionRun`, `Flight.Runs`/`Run`/`Vessels`); "Worlds" (runs saved, `Restore`, the rewind point);
  - `README.md` (the menus, carrying on, trying again);
  - `tests/GameOne.Tests/README.md` (the new tests);
  - `TODO.md` (remove "To discuss before building: resuming a mission after quitting"; under "More in the world log", note that missions are now logged).
- Delete before the merge: `docs/superpowers/specs/2026-11-16-resuming-missions-design.md` and this plan.

- [ ] **Step 1: Write the docs.**
- [ ] **Step 2: Run the long tests.** Run: `dotnet run --project tools/TestRun -- --before-merge`. Expected: all pass. Also run the soak tests (what is kept over play changed).
- [ ] **Step 3: Commit** (`Docs: missions that carry on`). Run `dotnet build-server shutdown`.
