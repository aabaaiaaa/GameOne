# TerrainGen

Makes the planet's terrain map, `src/GameOne/Assets/terrain.bin.gz`, from the authored layout in
`layout.json`. It runs during development, not in the game: the map is committed, and every world
uses the same one.

```sh
dotnet run --project tools/TerrainGen -c Release -- tools/TerrainGen/layout.json src/GameOne/Assets/terrain.bin.gz
dotnet run --project tools/TerrainGen -c Release -- tools/TerrainGen/layout.json src/GameOne/Assets/terrain.bin.gz --preview <folder>
```

It takes about 30 to 90 seconds on a 22-core machine (Release), and the same layout always gives the same bytes.
`--preview` also writes `world.png` (the whole planet, equirectangular), `launch-site.png`
(20 km round the pad, north up) and an `ice-<sea>.png` for each sea with ice (its sheet, floes
and some open water round them, north up). All are coloured by surface kind and sea depth and shaded from
the north-west, so you can judge the look without starting the game.

**Regenerate the map** after changing the layout, `Scene.PlanetRadius`, `Scene.HomeElevation`, or where
`Scene` puts the pad, the landing zone, the test pads or the drone ship. The map records the radius and
Home's elevation (`Scene.HomeElevation`) it was made for, and the game refuses a map made for another of
either. The generator works on heights above Home's pad (as the layout's numbers are), with the sea 35 m
below, and writes the map above the sea, as every height in the game is: the map's version (4) says so,
and an older one is refused.

## How it works

1. **Land and sea** come from the outlines. Each outline is read through a broad warp, and then the
   coast is moved in and out by fractal noise on the distance to it, which gives bays, coves,
   headlands and islets at every scale. Near the sites, both calm down, so the coast east of the
   pad stays where the layout puts it.
2. **Seas.** Each water texel belongs to the nearest sea seed joined to it by water. Its seabed
   is a shelf off the coast, then a slope down to the sea's depth, with banks where the sea has
   them. Depths blend over about 1 km where two seas meet.
3. **Land.** A rise from the shore (cliffs along some stretches), warped hills and plateaus
   inland, and the ranges: a ridge along each crest line, with peaks and saddles along it and
   spurs down its flanks. Rough noise is added in proportion to the relief, for the water to
   organise.
4. **Erosion** runs over the whole cube sphere (flow crosses face edges like any other step),
   coarse to fine: at 1/8, 1/4, 1/2 and the full resolution. Each level fills hollows (a priority
   flood from the sea), routes water to the two steepest neighbours downhill, and cuts channels by
   stream power against an uplift back towards the layout's shape. So ridges keep the layout's
   heights, and valleys get deeper the more water drains down them. Slopes are then softened, and
   anything steeper than the talus angle slumps. The coarse levels cut the big, broad valleys;
   the finer levels add side valleys and gullies.
5. **Sea ice.** Round each icy sea's `at`, the water out to the sheet's edge is frozen: `pack`
   metres out on each bearing, moved in and out by up to a quarter of that by noise along the edge
   (a few hundred metres across its bights and tongues), and broken by finer noise (some 14 m in
   and out, 70 m and 35 m along), so it is ragged, not a circle. The sheet is ground (pack ice) at
   sea level plus `Scene.PackIceFreeboard`, with no seabed under it and no sea's region. The water
   texels beside it are lifted so that the heights, read bilinearly as the game reads them, cross
   the sea level where the edge's curve is, not on straight chords between texel centres (a tooth
   a texel); past that the ice goes on under the water as a foot sloping down at 0.3 m a metre for
   20 m, so the waves rise smoothly off it, its face then dropping at 2 m a metre to the seabed. The next `floes` metres out on the same bearing are the floes: still water over
   the seabed, of their own kind, which the game's waves are damped over.
6. **Finishing.** The sites are levelled (Home's pad, landing zone and test pads to 0 on the generator's own scale, Home's ground, 35 m above the sea; the other
   launch sites' pads and zones, from `LaunchSites.All`, each at the highest ground under its own footprint, and
   only lowered to it, never raised, over the "Far pad" and "Far zone" sizes in the layout: no more than the map's
   texels need, so a site on a slope keeps its drop, and its foundation reaches down to the ground), and the drone ship's place (from `Scene`)
   is checked to be at sea. Peaks are rounded off under 1,590 m above the sea. Each texel gets its kind: pack ice, site, seabed (or the floes'), rock (steeper than
   35°), snow, beach, desert, or grass and scrub (grass where it is wetter: near the sea, in the
   valleys, and away from the tropics).
7. **The map**: each face gets its apron (the next face's texels just over the edge, so reads
   meet exactly across edges). Heights are quantised so that 0 is exact.

## The layout

Positions are `[latitude, longitude]` in degrees: the pad is at `[0, 0]`, east is +longitude and
north is +latitude. Because positions are angles, the layout follows the planet's radius. Sizes
and heights are in metres, and the features keep their size whatever the radius. Comments
(`//`) are allowed. A misspelt field is an error.

| Field | Meaning |
|---|---|
| `seed` | Seeds every noise field. Change it for another world with the same layout. |
| `resolution` | Texels per cube face each way (1024: ~15 m at 10 km). It must halve evenly once per erosion level. |
| `coast` | `warp`/`warpWavelength`: how far (m) and at what size the outlines wander. `roughness`/`roughWavelength`: the fractal noise that moves the coast in and out. `cliffs` (0–1) and `cliffHeight`: how much of the coast is cliffs, and how tall. `calmRadius`: the distance past a site's edge before the coast wanders fully. |
| `relief` | `coastRise`/`coastRiseLength`: the land's rise from the shore. `hills`/`hillWavelength` and `plateaus`/`plateauWavelength`. `roughness`: the share of the relief added as noise for erosion to organise. `calmRadius`: as for the coast, for hills. |
| `land` | Continents and islands: `{ name, outline: [[lat, lon], ...] }`, joined by straight lines in latitude/longitude. Overlapping outlines cancel, so one can cut a lake into another. An outline may cross ±180° but not reach a pole. Everything else is sea. |
| `seas` | `{ name, at, kind: warm/temperate/icy, depth, shelf, banks, waves, shallow, deep }`. `at` must be in water. `depth` is how deep its basin goes; `shelf` is the width of its shallow shelf; `banks` (0–1, optional) is how much of the basin rises to shallow banks. `waves` (above 0) is its wave size: the metres from crest to trough of its biggest waves, which the game's one set of waves is scaled to there (blending where two seas meet, and dying away in the shallows). `shallow` and `deep` (`[r, g, b]`, display values) are its water's colours over the shelf and over deep water. These three are only carried into the map's sea table: changing them leaves the heights as they were. `ice` (optional, icy seas only): `{ pack, floes }`, the frozen sheet out to about `pack` metres from `at` (its edge wandering by a quarter of that), then a band of floes `floes` metres wide. |
| `ranges` | `{ name, path: [[lat, lon], ...], height, width }`: the crest line, the height of its highest peaks before erosion (erosion lowers them by roughly a fifth), and the distance from the crest to its foot. |
| `deserts` | Land within `maxLatitude`, at least `minDistanceFromSea` from water and below `maxHeight` can be desert. `coverage` (0–1) sets how much of that land is. The edges are irregular, but never outside the limits. |
| `snow` | Snow towards the poles from `minLatitude` (lower on high ground), and on peaks above `minHeight` anywhere. |
| `sites` | `{ name, at, radius, blend }`: levelled flat at 0 inside `radius`, blending back over `blend`, with the coasts and hills calmed round them (`coast.calmRadius`, `relief.calmRadius`). `Launch pad`, `Landing zone` and `Test pads` leave out `at`: their places come from `Scene`, so they always match the game (`Test pads` is one entry for all six pads of the test range, each levelled the same). Other sites need `at`. The other launch sites (`LaunchSites.All` after Home) aren't listed: each pad and landing zone takes `Launch pad`'s and `Landing zone`'s sizes, and is levelled at the land's own height there, without the calm (the country round them is what they are for). |
| `erosion` | `iterations`: one count per level, coarsest first. `incision`: how deep running water cuts (at rest, the depth below the layout is incision × (√area − √channelArea) × slope; finer levels cut less). `channelArea`: the m² that must drain through a texel before water cuts a channel. `diffusion`: how much slopes soften each iteration (finer levels soften more). `talus`: the steepest a loose slope stands, in degrees. |

The tests in `tests/GameOne.Tests/MapLayoutTests.cs` check the shipped map against the launch
site's requirements: the pad level at 0, the coast 280–400 m east, sea from 400 m to 5 km east
and deeper than 100 m at 5 km, deep water under the drone ship, peaks over the horizon to the west, no peak over
1,600 m above the sea, the seas, their kinds, waves and colours, deserts, and no seams. Run them after regenerating.

## The moon

`--moon` makes the moon's map, `src/GameOne/Assets/moon.bin.gz` (about 6.3 MB), from `moon.json`,
in the planet map's format: heights in metres above the datum sphere of `Scene.MoonRadius`, the kinds
byte a `MoonKind` (plain, rim, ray, floor, small rim), no seas and every region 0. `MoonGround` reads it (moon-fixed
directions: north (0, 0, -1), `[0, 0]` the near side's middle, facing the planet).

```sh
dotnet run --project tools/TerrainGen -c Release -- --moon tools/TerrainGen/moon.json src/GameOne/Assets/moon.bin.gz [--preview <folder>]
```

It takes about two seconds, and the same layout always gives the same bytes. There is no erosion:
every texel's height is worked out from its own direction, the sum of the plains' gentle noise, the
big craters (a parabolic bowl from the floor up to the rim's crest, then thrown-out rock thinning as
the cube of the distance out to four radii; the floor, the rim and the rays as kinds), the canyon (a
flat floor between steep walls, rough rather than an even arc: its middle line wandering, its width
and depth swinging along it, its walls ragged with spurs and slumped bays, slumps filling it part way
here and there, side branches off its outer wall, and its horns shallowing to nothing, one longer than
the other; all from the seed), the far side's tail (a ridge along a smooth curve through its points,
a rounded hump across, rising and widening from its thin tip to its base, its line wandering and its
crest uneven, then a mountain with concave flanks (steepest under its summit, level at its foot),
furrowed broadly, and a crater in its top: its rim pale, its floor a
floor) and the small craters (thousands, binned by cells of the cube's faces about 60 m across so a
texel adds up only those that can reach it, each a bowl and rim like a big one's, a younger one's
adding to an older's where they overlap, those wider than `paleOver` with a faintly pale rim; few
and only the smallest in the flats), then the
landing spot (`Scene.MoonLandingSpot`, `Scene.MoonLandingSpotRadius`) levelled at the plains' height
there. `--preview` writes the near side from the planet with the sun low from the east and from the
west (`near-side-east.png`, `near-side-west.png`), lit as the game lights the moon (albedo, slant and
the ground's own shadows), the far side from beyond it with the sun low from the east
(`far-side.png`), and the landing spot from above (`landing-spot.png`, 3 km across).

**Regenerate it** after changing `moon.json`, `Scene.MoonRadius` (the game refuses a map made for
another), `Scene.MoonLandingSpot` or `Scene.MoonLandingSpotRadius`. `MoonGroundTests` checks the
shipped map is the layout's, and the craters', the canyon's and the landing spot's numbers.

| Field | Meaning |
|---|---|
| `seed` | Seeds the plains' noise, the rays and the small craters. |
| `resolution` | Texels per cube face each way (1024: ~3.8 m on the 2.5 km moon, so the smallest craters, 15 m, are four texels across). |
| `craters` | `{ name, at, diameter, depth, rimHeight, rays }`: the big craters. `diameter` is rim crest to rim crest, `depth` how far its floor's middle is below the plains, `rimHeight` its crest above them, `rays` how many pale streaks run out from it (each 2.5 to 4.5 radii long). |
| `canyon` | `{ at, radius, wander, branches, startBearing, endBearing, width, depth }`: an arc about `radius` metres round `at`, its middle line wandering in and out by up to `wander`, from `startBearing` clockwise to `endBearing` (compass degrees from `at`, 180 due south), about `width` from rim to rim and its floor about `depth` below the plains (both swinging along it), with `branches` side canyons. |
| `tail` | `{ path, tipHeight, baseHeight, tipWidth, baseWidth, wander, mountainHeight, mountainRadius, craterDiameter, craterDepth }`: the far side's ridge along a curve through `path` (tip first), its crest `tipHeight` to `baseHeight` above the plains (at the line's end, under the summit: where the mountain's flank rises past it the ridge goes under it, so it joins the flank as a spur well below the top) and its foot `tipWidth` to `baseWidth` across, its line wandering by about `wander`; at its base a mountain `mountainRadius` to its foot, its summit crater's rim `mountainHeight` above the plains, `craterDiameter` across and `craterDepth` deep. |
| `smallCraters` | `{ count, smallest, largest, paleOver, nearFace? }`: `count` craters `smallest` to `largest` metres across, scattered by the seed (the count over a size falling as its square), overlapping where they fall together, none on the big craters, the canyon, the tail or the landing spot; those wider than `paleOver` with a faintly pale rim (`MoonKind.SmallRim`). `nearFace: { within, largest }`, if given, keeps craters wider than `largest` out of the near side within `within` degrees of `[0, 0]`, should the larger ones ever blur the face seen from Home (they don't today, so it is left out). |
| `flats` | `{ share, wavelength, density, largest }`: patches of smooth plains between the craters, `share` of the ground where a low noise about `wavelength` metres across is highest (its level found from 20,000 directions spread over the sphere), and all round the landing spot (300 m past its blend): craters fall there at `density` times the rate elsewhere, and none wider than `largest`. |
| `plains` | `{ relief, wavelength }`: the plains' swells, about `relief` metres up and down at `wavelength` metres across. |
| `site` | `{ blend }`: the metres over which the levelled landing spot blends back into the plains (its place and radius are `Scene`'s). |
