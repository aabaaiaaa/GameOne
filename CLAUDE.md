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

- **Read the area's `CLAUDE.md` before working there:** the detail of how the game is simulated and drawn
  is beside its code, not here.
  [`src/GameOne/Physics/CLAUDE.md`](src/GameOne/Physics/CLAUDE.md): the terrain and its collision, touches,
  fast collisions, vessels nobody watches (formula, warp, handover, crossing a sphere's edge), the main
  camera's subject, touchdowns, the sea, saved worlds, the turning planet and the frames of reference;
  read it for any change to `Flight`, `Vessel`, `RocketPhysics`, `Formula`, `Collisions`, `Worlds` or
  the world store, whichever folder they are in.
  [`src/GameOne/Rendering/CLAUDE.md`](src/GameOne/Rendering/CLAUDE.md): the renderer, the depth, the shadow
  map, the lights, the matrices and colours, and every C# and GLSL pair that must change together (the
  terrain's detail, the sea, the air, the moon in the sky, the data layouts); read it for any change to
  `Rendering/`, `Shaders.cs` or a struct a shader reads.
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
  soak and performance tests), the two under "Read the area's `CLAUDE.md`" above and [`tools/GameDriver/CLAUDE.md`](tools/GameDriver/CLAUDE.md) (driving the running game, the
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
- **Math types at the boundary:** BRUTAL APIs take `Brutal.Numerics` types (`float3`, `floatQuat`); game
  code uses `System.Numerics`. Convert explicitly at the boundary.
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
    "Vessels touch" in the Physics `CLAUDE.md`), with the game's own care where Jolt says nothing: removing a sleeping
    body reports no contact ending (the game forgets a removed body's contacts itself), a frozen
    craft's contacts end in the step after it freezes, the legs' landing is the legs' own, and a
    part rocking down about a contact already held makes no new contact (see "Touchdowns" in the Physics `CLAUDE.md`).
  - Mass comes from shape volume and density. Thrust is applied as an acceleration
    (`AddLinearVelocity`), so it doesn't matter.
  - After a BRUTAL upgrade, check the native exports every entry point the bindings name
    (`NativeLibrary.TryGetExport` over the bindings' `EntryPoint`s), and that a call does what it
    should before relying on it: an export alone once ignored its arguments.
- **The HUD calls ImGui's internals for the link-up's order:** `Hud.OrderLinkUp` puts the
  link-up's full-screen window in front of the rest of the HUD, then the menu and the credits in
  front of it, with `Brutal.ImGuiApi.Internal`'s `GetCurrentWindow` and
  `BringWindowToDisplayFront` (ImGui's own `imgui_internal.h`, exported by BRUTAL's `imgui.dll`):
  the public API has no way to put one window in front of another every frame. The licence's
  logo and notice stay in front during a link-up by them. After a BRUTAL upgrade, check that the
  native still exports both (`NativeLibrary.TryGetExport` on `GetCurrentWindow_internal` and
  `BringWindowToDisplayFront_internal`: without them the game throws at the first link-up), and
  look at a link-up in the running game: the HUD behind its static, the credits and a menu in front.
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
- **Waiving the soak and performance tests:** skip them for a change that only touches the HUD, the
  camera or input handling (nothing that grows memory or costs frame time), and say in the report
  that they were skipped and why. The user has waived them for such changes before; for anything
  else they run as above unless the user waives them.
- **Giving the user a build to play:** the user runs the game themselves (never launch it from a
  tool call: it runs until closed and hangs against the timeout). From a worktree, give the **absolute**
  path: `dotnet run --project C:/Users/.../GameOne/.claude/worktrees/<name>/src/GameOne` (a relative one
  fails from another folder, and `src/GameOne` alone runs main's build, not the branch's). Build it first
  (`dotnet build` there, with the flags under "Build servers") so the user's run starts at once, and check no
  `GameOne.exe` is running (see the next item).
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
