# Resuming missions: missions that belong to their vessels and carry on across sessions

(The design. Once it is approved it is written to `docs/superpowers/specs/2026-11-16-resuming-missions-design.md`
on a feature branch. Then comes the plan, via superpowers:writing-plans.)

## Context and why
A world keeps its vessels from flight to flight, but not its missions. Loading a world always
starts a new flight (`Flight.Load` → `StartFlight`): a fresh rocket goes on the pad, every Ready
vessel in the world is removed, and the old flight's `MissionProgress` is dropped (its doc comment
says "there is nothing of it to save"). LAND ON THE MOON takes about 20 minutes of flight, so a
quit means losing it. More long missions are coming (the satellite caught round the moon, the
return from the moon, building a vessel in orbit), and each one makes this worse.

The user's view is that **a mission belongs to its vessels**. A world can have several missions
in progress at once, as there can be several worlds. When a world loads, the player can start a
new mission or carry on one already in progress. A vessel can also belong to no mission. Free
play is a mission with no objectives.

## Goals (the user's decisions)
1. **Missions in progress, many per world.** Each mission run (a "flight" in today's terms,
   numbered per world) owns its vessels, and the vessels that come off them (stages, fairings)
   belong to it too. A run is in progress until it completes, fails or is abandoned.
2. **Every run in progress keeps scoring from its own vessels**, whichever vessel is flown. If a
   booster from run A lands on its zone, unwatched and under formula, while the player flies run
   B's lander, A's step ticks off. (Steps stay done once done, as `MissionProgress` does today.)
3. **The mission panel follows the flown vessel.** It shows the flown vessel's own run: its
   mission, steps and points. Tab to a vessel of another run and the panel switches to that run.
   A vessel of no run shows no mission panel.
4. **Loading a world that has runs in progress** opens a screen of its own (mockup B), between
   the world list and today's CHOOSE A MISSION. It is titled with the world's name and lists each
   run in progress: "Carry on: <MISSION> · flight N · k of n steps", its points, and a line on
   what its vessel is doing. Under them, the screen lists **every intact vessel that belongs to no
   run** (free play's own, and those left over from ended missions; nothing wrecked), each one
   resumable in free play: its name, its body and what it is doing (landed at, afloat, in orbit,
   flying), in a list that scrolls when long. Below are "New mission…" (today's menu) and "Back to
   worlds". A world with no runs in progress and no intact vessels goes straight to today's menu,
   as now. This rounds out free play: a player just flying about can carry on a vessel that
   has no mission.
5. **Carrying a run on puts the player back exactly where they left it**, from the first second:
   on the vessel last flown in that run, paused under the menu like any load, with no fresh
   rocket and nothing cleared from any pad.
6. **Completion:**
   - A run with no optional steps completes as soon as its required steps are done.
   - A run with optional steps still open once the required ones are done shows a **"Complete
     mission"** button at the end of its objectives (in the mission panel). Pressing it completes
     the run, and any optional step not done is lost.
   - Failure: a run fails once a required step can no longer be done (the existing `Lost`).
   - Abandoned: the player gives a run up.
   A run that is complete, failed or abandoned is no longer in progress and drops off the
   carry-on list. Its vessels stay in the world, belonging to no run.
7. **The world log records missions too:** a run started, each step done (with its points), and
   a run completed (with its score), failed (and why) or abandoned.
8. **Trying again: two clearly different choices, and the player is told what each does.** The
   player should understand saved games. The state a world was loaded or a run carried on from is
   the "base" they can go back to, never anything before it. "Restart flight" in the game menu
   opens a small choice that names both options and what each does:
   - **"Start the mission again"** (always offered): the flown vessel's run is abandoned and a new
     run of its mission begins at its site, with a fresh rocket on its pad. Its old vessels stay in
     the world, belonging to no run. Nothing saved is reset, and nothing else in the world
     changes. This is offered whether the run is in progress, failed or completed. For a run that
     has ended, it is a new run of the flown vessel's own mission (`Vessel.Mission`) at its old
     site. So every vessel keeps the site of the run it came from, even after the run ends.
   - **"Rewind to when this mission began"** (or "...was resumed"): the world is loaded again
     exactly as it was when the player began this run or carried it on this session. The player
     picked that moment themselves, from the menu. The option says how far back it goes in game
     time, for example "Rewind 12 min 40 s, to when this mission was resumed": the world's clock now
     less its clock at the rewind point. Before it happens, a warning says plainly that
     everything since then is undone, including other vessels, other missions and the world log.
     The player must confirm it. It is a quick way to try again from a point they know.
   - Cancel.
   Today's silent rewind of the whole world (`_atStart`) goes. The rewind's snapshot is taken at
   the player's menu choice instead. The crash message's "To try again: Restart flight" stays,
   pointing at this choice.
9. **When a run ends:** its objectives panel and its MISSION COMPLETE or FAILED line (with the
   score) stay on screen for the flown vessel until the player switches vessel or starts
   something new. Then they go, and the run's vessels show no panel. A run that ends while
   the player flies a vessel of another run gets a brief notice that fades after a few seconds
   (for example "REACH ORBIT (flight 5) complete: 1,800 pts"), as well as its world-log entry.
10. **After a world loads, the menu can't be closed without a choice:** the carry-on screen and the
    mission menu reached from it stay until the player carries a run on, starts a new mission
    (free play counts) or goes back to worlds. Esc and Close don't dismiss them then. Behind
    the menu is the world as saved, with nothing new on any pad. `Load` no longer starts a
    flight, and the world is saved once the choice is made.

## My calls (for the user to check)
- **Free play is flying a vessel of no run:** a free play launch makes vessels that belong to no
  run (it has no steps to keep), and carrying on any vessel of no run is free play. No mission
  panel is shown, and the rewind point is set at that choice as for a run. Its "Start the mission
  again" starts a new run of that vessel's own mission (`Vessel.Mission`: free play for its own
  vessels, the old mission for a mission's leftovers) at its site.
- **The free play list's order:** the vessel last flown first, then by flight number, newest
  first. Each line is a button, as the runs' are, and the readout lists them for GameDriver scripts.
  (My call; the user chose to list every intact one.)
- **"Abandon mission"** goes in the game menu (Esc) beside "Restart flight", acting on the flown
  vessel's run. It is shown only when that vessel's run is in progress.
- **Starting the mission again logs the old run as abandoned** ("started again"), and the new run
  gets the next flight number. A rewind writes nothing to the log: the log is rewound with the
  rest of the world.
- **The rewind point is the last menu choice this session:** beginning a run, carrying one on, or
  starting free play. Tabbing to a vessel of another run doesn't move it, so the rewind always goes
  back to the moment the player picked. Until a menu choice has been made, there is nothing to
  rewind to. The rewind point is held in memory only and not saved, because the saved world is the
  base anyway. The choice names its moment ("began" for a new run, "was resumed" for one carried on).
- **The carry-on screen lists only what is still there:** a lost or removed vessel is never an
  option. The one case to handle: a run's last-flown vessel can be lost while the player flies
  another run (say, run A's stage is hit while the player flies run B). If run A hasn't failed,
  carrying it on flies another intact vessel of its own, and its line describes that vessel. If
  run A has failed, it isn't listed.
- **A run's state is saved with the world:** for each run in progress, its mission (by name), its
  site, its flight number, its steps done with their points, and the id of the vessel last flown
  in it. Each vessel already saves its flight number (`VesselState.Flight`), which links it to its
  run. The world also saves which vessel was being flown, which is what "carry on" resumes. The
  small views are not saved; they are opened again by hand.
- **Steps find their vessels by run, not by `Flight.Vessels`:** today's steps read
  `f.Vessels[0]` (the flight's rocket) and `flight.Vessels`. They are handed the run's own
  vessels instead, so several runs can be judged each step.
- **Ready rockets of a run in progress are not swept from the pads:** today `StartFlight` removes
  every Ready vessel in the world. A new mission clears only its own pad. A run whose unlaunched
  rocket is in the way there is abandoned (and logged), and the rocket is removed, as anything
  else in the way is today.
- **`MakeRoom`** (the 1,000-vessel cap) removes in this order, oldest first within each group:
  - debris, meaning what can't be flown (`!Vessel.Guided`: fairing halves and the like, as the HUD
    already calls them);
  - then vessels belonging to no run;
  - then the vessels of runs in progress.
  It never removes the flown vessel. A run that loses a required vessel this way fails, as it
  would to any loss. (The user: debris first.)
- **Debris is never listed for free play** on the carry-on screen, as it can't be flown. "Every
  intact one" means every intact vessel that can be flown.

## Non-goals and anti-goals
- No migration of old saves: `WorldStore.FormatVersion` is bumped and old files are listed as
  unreadable (CLAUDE.md, "No backwards compatibility").
- No change to the controls, and no new key: everything here is in the menus and the mission panel.
- No autopilots, and nothing that flies a run for the player.
- Anti-goal: resuming must never put a fresh rocket on the pad, or move or remove anything in the
  world. "Exactly where you left it" is the point.

## Constraints
- CLAUDE.md's rules hold: game logic reachable through `FlightInput` (carry on, abandon, complete
  and restart are inputs, as Restart is today); new menu screens use `Hud.MenuAcross` and keep the
  credits in view; the readout (`Readout.cs`) lists the new buttons and the runs in progress for
  GameDriver scripts; `VesselState`/`WorldState` round-trip tests cover every new field.

## Where it touches (for the builder)
- `Flight.cs`: `StartFlight`, `Load`, `Mission`/`Progress`/`Vessels` (becoming per run), the
  restart input, and `Capture`.
- `MissionProgress.cs`, `Mission.cs` (steps judged against a run's vessels).
- `World.cs` (`WorldState`, new run records), `WorldStore.cs` (`FormatVersion`).
- `WorldLog.cs` (mission entries).
- `Hud.cs`: the carry-on screen; the game menu's Abandon; the mission panel's "Complete mission"
  and following the flown vessel; `Readout.cs`.
- `Program.cs` (the world list's Play goes to the carry-on screen when there are runs).
- Docs: `README.md`, `CLAUDE.md` (Worlds; MissionProgress's line), `TODO.md` (remove the
  "resuming a mission" entry).

## Left to the builder
- The run's type and name in code, and how the per-run step evaluation is organised.
- The exact wording of the log entries and of the carry-on screen's status line.

## Verification
- `dotnet run --project tools/TestRun`, with new tests for:
  - a world saved mid-moon-mission, reloaded, carried on and completed;
  - two runs in progress at once, with steps ticking off unflown;
  - "Start the mission again" leaving the old vessels in place and the rest of the world untouched;
  - "Rewind" bringing back the world (other runs, other vessels, the log) exactly as it was at the
    last menu choice, and never earlier than the load, with the game time it names matching the
    world clock's change;
  - Complete mission with an optional step open;
  - Abandon;
  - a free play vessel (and a leftover from an ended mission) carried on after a reload, in place,
    with no mission panel;
  - the store round-trips of the new fields.
- `--before-merge` before merging.
- A GameDriver script in a scratch `GAMEONE_WORLDS`: launch, quit, relaunch, carry on, and check
  the readout shows the same vessel, place and steps.
