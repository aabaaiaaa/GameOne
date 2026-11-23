# CLAUDE.md: rendering

The renderer and everything the shaders mirror, split out of the repo-root `CLAUDE.md`, which holds the
rules that hold across the project (read it too). What is named here as another section ("Touchdowns",
"Low hand-holding", "The sea") is in the root file or in [`../Physics/CLAUDE.md`](../Physics/CLAUDE.md).

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
- **Matrices:**
  - Use `System.Numerics` with row vectors (`v * M`, so `model * view * projection`), uploaded as-is.
    GLSL's column-major read transposes them, so `M * v` in shaders is correct.
  - Use `CameraProjection` for a view's projection (`ChaseCamera.Projection`, its depth reversed and
    its far plane at infinity, with Y flipped, because Vulkan clip space is Y-down), and
    `PerspectiveVulkan` only for a floodlight's shadow tile (standard depth, as the shadow map's).
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
