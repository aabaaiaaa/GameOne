using System.Numerics;

namespace GameOne;

/// <summary>
/// Where everything sits, and the numbers to tune. Units are metres, with the launch pad centred on
/// the origin, on top of a small planet (see <see cref="Planet"/>), Y up, and the camera starting out
/// to the south (+Z) looking north.
/// </summary>
internal static class Scene
{
    // The planet: tiny, so an orbit takes minutes, but with full-strength gravity at its surface,
    // so flying near the ground feels as it would on a planet the size of Earth.
    public const float PlanetRadius = 10000f;

    /// <summary>
    /// How long a day is, noon to noon, in seconds: the sun's crossing the sky as the planet turns
    /// under it and goes round it. The planet's own turn against the stars is a little quicker
    /// (<see cref="Planet.TurnSeconds"/>).
    /// </summary>
    public const float DaySeconds = 1200f;

    /// <summary>How many days make a year, which is the planet's going once round the sun.</summary>
    public const float YearDays = 12f;

    /// <summary>
    /// Where Home's clock is at world time 0, in seconds from its midnight (<see cref="Sun"/>):
    /// 07:30, mid-morning, so a new world's first flight is by day. With no tilt the sun rises at
    /// 05:00 everywhere (and sets at 15:00), so that is two and a half "hours" after sunrise.
    /// </summary>
    public const double ClockStart = 450;

    /// <summary>
    /// The planet's axial tilt, in degrees: none, and not a tunable. Nothing in the game reads it:
    /// the planet goes round the sun in the plane of its equator (<see cref="SolarSystem"/>), so the
    /// sun is always over the equator and there are no seasons. Only the tests read it, as the tilt
    /// of the clock's sun the paths' direction must match, and to check the day's sun is untilted.
    /// (A tilt is tried only by <see cref="Sun.Direction(double, float)"/>, which takes its own.)
    /// </summary>
    public const float AxialTilt = 0f;

    // The solar system (SolarSystem): the sun still at its centre, the planet round it on a circle
    // in the plane of its equator once a year, the moon round the planet. The sun's distance and
    // size are judged by eye from the planet's surface; its pull is worked out so the planet's lap
    // is the year.
    public const float SunDistance = 3_000_000f;   // from the planet's centre to the sun's
    public const float SunRadius = 89_000f;        // its disc about 3.4° across

    /// <summary>
    /// The solar system's edge: a sphere this far (metres) round the sun's centre, the star nothing
    /// else goes round. A path that reaches it ends there, marked (<see cref="Patched.Legs"/>). Far
    /// enough out to leave room for bodies a long way from the sun, and well short of where a
    /// double loses its millimetres (it holds a place here to about 0.002 mm; a float only to a km).
    /// </summary>
    public const double SolarSystemEdge = 1e10;

    // The moon: its distance (judged by how big it looks), size and pull; its lap follows from the
    // planet's pull (about 27 minutes). It goes round the way the planet turns, keeping one face to it.
    public const float MoonDistance = 40_000f;     // from the planet's centre to the moon's
    public const float MoonRadius = 2_500f;
    public const float MoonSurfaceGravity = SurfaceGravity / 6f;

    /// <summary>
    /// How far out a vessel leaves a body's sphere of influence (<see cref="GroundBody.SphereRadius"/>,
    /// worked out from the masses), as a multiple of it: it enters at the radius and leaves only past
    /// this, so a path grazing the edge or a vessel hovering on it never changes body step after step.
    /// </summary>
    public const float SphereLeaveFactor = 1.1f;

    /// <summary>
    /// How long (seconds of real time) the communications link-up is shown, as a vessel at a body
    /// whose physics isn't made yet is switched to or given a small view (<see cref="LinkUp"/>):
    /// the picture broken up while the link is made (and the physics behind it), then clearing as
    /// it connects, over the last <see cref="LinkUpClearShare"/> of it. Always this long, however
    /// soon the physics is made, so every link-up looks the same; longer only if the physics
    /// takes longer.
    /// </summary>
    public const float LinkUpSeconds = 1f;

    /// <summary>The share of <see cref="LinkUpSeconds"/> the picture takes to clear once connected.</summary>
    public const float LinkUpClearShare = 0.25f;

    /// <summary>
    /// How far ahead (game seconds) a body's physics is asked for before a vessel is to need it
    /// there (<see cref="Physics.Worlds.Ahead"/>: made on the tile worker's idle time, so the step
    /// never waits on one being made).
    /// </summary>
    public const float PhysicsAheadSeconds = 10f;

    /// <summary>
    /// How long (game seconds) a body's physics is kept with no craft in it before it is released
    /// to the pool (<see cref="Physics.Worlds"/>), so a vessel handed out and back in a moment later
    /// finds it still there.
    /// </summary>
    public const float PhysicsIdleSeconds = 30f;

    /// <summary>
    /// How often a path round a body is sampled for its children's spheres (<see cref="Patched"/>),
    /// in seconds: each sample's closest approach to each child over the next is reckoned from how
    /// the two move there, so a path under a sphere for less than this is still found, then the
    /// crossing bisected to a millisecond.
    /// </summary>
    public const float PatchSampleSeconds = 2f;

    /// <summary>How many legs a patched path is followed for (<see cref="Patched.Legs"/>): to the moon, round it, and out again.</summary>
    public const int PatchedLegs = 3;

    /// <summary>
    /// How closely a node's burn must be flown for the path to match the one it plans: what is
    /// still needed under this share of the node's size (<see cref="ManoeuvreNode.MatchTolerance"/>),
    /// held between <see cref="NodeMatchMin"/> and <see cref="NodeMatchMax"/>. Matched, cutting the
    /// engine lets the node go.
    /// </summary>
    public const float NodeMatchShare = 0.03f;

    /// <summary>The least a node's match tolerance is (see <see cref="NodeMatchShare"/>), m/s.</summary>
    public const float NodeMatchMin = 1f;

    /// <summary>The most a node's match tolerance is (see <see cref="NodeMatchShare"/>), m/s.</summary>
    public const float NodeMatchMax = 5f;

    /// <summary>
    /// Once a node is matched with the engine lit, it stays matched until what is still needed is
    /// this many times its tolerance (<see cref="ManoeuvreNode.MatchTolerance"/>), so it doesn't
    /// flicker as the burn ends; with the engine off it is matched only within the tolerance.
    /// </summary>
    public const float NodeMatchHold = 1.5f;

    /// <summary>A node's handle pushed in takes away at this share of the rate the same distance out adds.</summary>
    public const float NodeHandleInwardShare = 0.25f;

    /// <summary>The next node's colour (and its planned legs') once its trajectory is matched (<see cref="NodeMatchShare"/>).</summary>
    public static readonly (byte R, byte G, byte B) NodeMatchedColor = (120, 230, 120);

    /// <summary>
    /// How many points along a stretch of a node's planned path are looked at for the one nearest
    /// the vessel (a late burn's guide: see <see cref="Vessel.BurnLeft"/>), before Newton's steps
    /// home in on it: enough that the nearest of them is on the right side of the path.
    /// </summary>
    public const int NodeNearestSamples = 48;

    // How the sky draws them: the moon's plains' grey (the share of the sun's light they give back,
    // a linear reflectance, not a display colour: MoonAlbedos has each kind of its ground's), and
    // how much of the sun's disc's angular radius its edge is softened over (half inside the disc's
    // true edge, half outside, and never less than a pixel's width), so it doesn't alias.
    public const float MoonAlbedo = 0.12f;
    public const float SunDiscSoftening = 0.04f;

    /// <summary>
    /// How much of the sunlight grazing the planet through its air reaches a place deep in the
    /// planet's shadow (<see cref="SolarSystem.PlanetShadowLight"/>): the air bends that light into
    /// the shadow (not modelled as rays), so the moon eclipsed there is dark in the grazing light's
    /// colour (red, as the blue is scattered out on the way) rather than black. Deeper than a scale
    /// height of the air below the planet's edge it is this share of the grazing light; nearer the
    /// edge it rises to all of it, so the shadow has no hard line.
    /// </summary>
    public const float PlanetShadowGlow = 0.1f;

    /// <summary>
    /// How many points across the sun's disc the planet's shadow is worked out at (each one the
    /// light along its own ray past the planet: the edge of the shadow is the disc going behind the
    /// planet's air and then the planet). A texture read each on the GPU, only near the shadow's edge.
    /// </summary>
    public const int PlanetShadowSamples = 8;

    /// <summary>
    /// The light off the planet onto the moon with all its lit face turned to the moon, as a share
    /// of the sunlight (<see cref="SolarSystem.Planetshine"/>): the planet's own grey (about 0.3)
    /// times the share of the moon's sky it fills (the sine squared of its angular radius, about
    /// 0.06), made a little more so the moon's dark side shows its craters faintly on a crescent.
    /// </summary>
    public const float PlanetshineStrength = 0.03f;

    /// <summary>
    /// The moon's light on the planet (<see cref="SolarSystem.Moonlight"/>): a face turned to a
    /// full moon has its grey (<see cref="MoonAlbedo"/>) times this share of the sunlight, before
    /// the air. The share of the sky it fills from the ground (the sine squared of its angular
    /// radius, about 0.007 from 30 km) made about six times more, so a full moon's night shows
    /// the ground (about twice the starlight, <see cref="NightLight"/>) and a crescent's barely.
    /// </summary>
    public const float MoonlightStrength = 0.04f;

    /// <summary>
    /// The sun eclipsed by another body (<see cref="Sun.Seen(GroundBody, Vector3, double)"/>):
    /// a place's lights (the pads' and the vessels' landing lights, <see cref="Sun.IsDark(GroundBody, Vector3, double, bool)"/>)
    /// come on as less of the sun's disc than <see cref="EclipseDarkSeen"/> is left uncovered, and
    /// go off as more than <see cref="EclipseLightSeen"/> is again: the gap holds the state, as
    /// <see cref="DuskSunHeight"/> and <see cref="DawnSunHeight"/> do, so they never flicker at
    /// the shadow's edge. A few percent of the sun is about the light of the sun at dusk's height.
    /// </summary>
    public const float EclipseDarkSeen = 0.03f;
    public const float EclipseLightSeen = 0.06f;

    /// <summary>
    /// The corona round the moon in an eclipse, as the sky draws it: its brightness at the sun's
    /// edge (linear light, times the fourth power of how much of the sun is covered: strongest in
    /// totality, and hidden by the sun's own glare before it), fading out over
    /// <see cref="CoronaReach"/> of the sun's radii beyond its edge, and coming in over the moon's
    /// limb by <see cref="CoronaLimbSoftening"/> sun radii, so the black disc's edge glows. The
    /// moon looks about three times as wide as the sun, so the reach is enough to show past its
    /// edge wherever the sun is behind it. Pale white-blue (display values).
    /// </summary>
    public const float CoronaStrength = 3f;
    public const float CoronaReach = 6f;
    public const float CoronaLimbSoftening = 0.15f;
    public static readonly Vector3 CoronaColour = new(0.86f, 0.92f, 1.0f);

    /// <summary>
    /// The orange all round the horizon in an eclipse: the light from outside the moon's shadow,
    /// drawn as the sun's light through a grazing path of air (the sunset's colour, from the air's
    /// table) times how much of the sun is covered at the camera times this (linear light), fading
    /// out above the horizon over <see cref="EclipseHorizonHeight"/> (the sine of the elevation over
    /// which it falls by e), and with the air thinning above the camera.
    /// </summary>
    public const float EclipseHorizonGlow = 0.5f;
    public const float EclipseHorizonHeight = 0.08f;

    /// <summary>
    /// How far the moon's orbit is tilted from the planet's equator, in degrees. The planet is so big
    /// beside the moon's distance that no small tilt keeps eclipses rare: with this, they come in two
    /// seasons a year, while the sun is near the line the orbit crosses the equator on, and never between.
    /// </summary>
    public const float MoonTiltDegrees = 30f;

    /// <summary>
    /// Where the moon's orbit crosses the equator going north (its rising line, fixed among the
    /// stars), in degrees round the north pole the planet's way from Home's east at world time 0.
    /// With <see cref="MoonStartDegrees"/>, set so Home sees a total eclipse of the sun by day in
    /// a new world's first eclipse season (day 2, about 13:50 local, the sun some 20° up in the
    /// west, about 23 s of totality).
    /// </summary>
    public const float MoonNodeDegrees = 8f;

    /// <summary>
    /// Where the moon is along its orbit at world time 0, in degrees on from its rising line: set
    /// so a new world's first evening at Home (sunset to midnight of day 1) has the moon up, and
    /// (with <see cref="MoonNodeDegrees"/>) Home's total eclipse in its first year.
    /// </summary>
    public const float MoonStartDegrees = 3f;

    /// <summary>
    /// How far the moon's spin axis is tilted from its orbit's normal, in degrees (fixed among the
    /// stars, tilted about the orbit's rising line towards the planet's pole). Its face nods at the
    /// planet by this each lap, so from its north pole the planet wholly rises and sets once a lap:
    /// its centre must climb past its half-width (about 14.5°) and a degree more, and from a place
    /// on the moon's surface (its radius off the centre, towards the planet) it climbs about 3.5°
    /// less than the tilt: 20° clears it by a degree. Tilted towards the planet's pole, the moon's
    /// equator is only <see cref="MoonTiltDegrees"/> less this from the planet's (the sun's path), so
    /// its seasons are mild.
    /// </summary>
    public const float MoonSpinTiltDegrees = 20f;

    /// <summary>
    /// The moon's ground's share of the sun's light given back (linear reflectances), per
    /// <see cref="MoonKind"/> in the enum's order: the plains <see cref="MoonAlbedo"/>'s grey, the
    /// big craters' rims and their rays of thrown-out rock paler, their floors and the canyon's as
    /// the plains (it is dark by its own shadows, not by paint), and the larger small craters' rims
    /// faintly pale.
    /// </summary>
    public static readonly float[] MoonAlbedos =
    [
        MoonAlbedo, // Plain
        0.20f,      // Rim
        0.16f,      // Ray
        MoonAlbedo, // Floor
        0.145f,     // SmallRim
    ];

    /// <summary>
    /// The scale the moon's fine detail is laid out on (a moon-fixed direction times this is where
    /// its noise is sampled): its radius, so the octaves' wavelengths are metres along its ground.
    /// </summary>
    public const float MoonDetailScale = MoonRadius;

    /// <summary>
    /// The moon's fine detail on top of its map, the same over all its ground: octaves of value
    /// noise (<see cref="Terrain.Noise"/>), each (amplitude, wavelength) in metres. Low enough that
    /// the levelled landing spot stays under 3° of slope.
    /// </summary>
    public static readonly (float Amplitude, float Wavelength)[] MoonDetail = [(0.25f, 40f), (0.05f, 8f)];

    /// <summary>
    /// Where a lander is meant to come down on the moon (latitude, longitude in degrees, moon-fixed):
    /// on the near side's middle line, 20° from its north pole, where the planet never sets, low in the sky, nodding
    /// by <see cref="MoonSpinTiltDegrees"/> each lap, and the sun is up with the planet in the sky for a fair
    /// stretch of every lap of the year. TerrainGen levels the plains under it.
    /// </summary>
    public static readonly (float Latitude, float Longitude) MoonLandingSpot = (70f, 0f);

    /// <summary>How far round <see cref="MoonLandingSpot"/> TerrainGen levels the moon's plains, in metres along the ground.</summary>
    public const float MoonLandingSpotRadius = 150f;

    /// <summary>
    /// How far from <see cref="MoonLandingSpot"/>, in metres along the moon's ground, a landing on the
    /// moon still scores for its nearness (most at the spot, falling evenly to none here: <see cref="Mission.SpotPoints"/>).
    /// </summary>
    public const float MoonSpotScoreReach = 1000f;

    /// <summary>
    /// The camera's distance from the moon's centre within which a view draws the moon's ground as a
    /// mesh (its patches, as the planet's: <see cref="Rendering.PlanetPatches"/>), writing depth and
    /// with its true outline; beyond it the sky's ray-cast alone draws it (and leaves it to the mesh
    /// within). Four of its radii: some 7.5 km above its ground.
    /// </summary>
    public const float MoonMeshReach = 4f * MoonRadius;

    /// <summary>
    /// Within this distance of the moon's centre its mesh has all its relief; from here out to
    /// <see cref="MoonMeshReach"/> the relief eases away to none, the datum's sphere the sky's
    /// ray-cast draws (shaded alike: the same map's normals and shadows), so neither its outline nor
    /// its look jumps at the handover (the far side's 800 m mountain would stand some 60 pixels off
    /// the sphere's limb there). The ease is slow: over 2.5 km of the way in.
    /// </summary>
    public const float MoonReliefWithin = 3f * MoonRadius;

    /// <summary>
    /// How many times a face of the moon's cube is split at most, under the camera: ~31 m patches,
    /// ~1 m squares, as the planet's finest (its faces are a quarter the size).
    /// </summary>
    public const int MoonPatchMaxLevel = 7;

    /// <summary>
    /// The moon's ground casts its own shadows by a march over its map towards the sun
    /// (<see cref="MoonGround.SunSeenOver"/>, and its GLSL twin): this many steps, spaced out
    /// quadratically to this far along the way to the sun (each a map read: 20 texture reads a
    /// place at most on the GPU, fewer once the ray is over the highest ground or the sun is
    /// wholly hidden, where the march stops). Past about 2.2 km nothing on it can reach over the
    /// ray, as its ground curves away (the deepest floor to the far side's summit is under 960 m),
    /// so the reach covers a sun on the horizon; 20 steps keep the first few within 100 m of the
    /// place, where a wall beside it matters.
    /// </summary>
    public const int MoonShadowSteps = 20;
    public const float MoonShadowReach = 2200f;

    /// <summary>How far above its ground (metres) a place's march towards the sun starts, so its own fine bumps don't shadow it.</summary>
    public const float MoonShadowLift = 0.25f;

    /// <summary>
    /// The scale the ground's fine detail and the sea's waves are laid out on (metres: a direction
    /// times this is where their noise is sampled, in C# and in the GLSL alike, so the drawn ground
    /// is the collider). Not a height or a radius of anything: change it and every bump moves.
    /// </summary>
    public const float DetailScale = 10000f;
    public const float SurfaceGravity = 9.81f;
    public const float AtmosphereHeight = 3000f;      // no air at all above this: an orbit clear of it is stable
    /// <summary>
    /// How high over its datum Auto keeps a vessel in the surface frame at least, as a share of its
    /// body's radius (its datum's): the room needed to bleed off a body's orbital speed grows with
    /// that speed (v² = gR, so at a deceleration of g the distance is R/2), so in proportion to the
    /// radius. Its orbit floor is used where that is higher (<see cref="GroundBody.FrameSurfaceHeight"/>):
    /// on the planet (its air's top, 3 km, over this share's 2,990 m) and the moon (its highest
    /// ground, 822 m, over 750 m); the share takes over on a bigger body.
    /// </summary>
    public const float FrameSurfaceWithin = 0.3f;
    public const float FrameOrbitMargin = 100f;       // Auto's frame turns orbital this far above its surface height (GroundBody.FrameSurfaceHeight, and back below it), so a vessel at the edge doesn't flicker
    public const float AtmosphereScaleHeight = 700f;  // the air thins by a factor of e every this much higher
    // Below these speeds in the frame in use the holds leave the nose where it is. Over the ground
    // (or round an orbit) a slow vessel's velocity is mostly the noise of touching the ground and of
    // the waves, which would swing the nose about. Relative to a flying target, or from orbit, there
    // is no such noise (both move freely), and matching speed with one is done below 5 m/s, so
    // there the holds work down to a crawl. Relative to a place, a landed vessel or the drone ship
    // it is the vessel's own velocity over the ground (less the deck's rocking), noise and all, so
    // there the surface's floor holds (Flight.HoldFloor).
    public const float HoldFloorSpeed = 5f;
    public const float TargetHoldFloorSpeed = 0.2f;
    public const float MarkerFloorSpeed = 0.5f;       // the travel markers show slower travel than the holds steer by, but not the ground's jitter

    // Where the satellite mission wants the satellite: a circular orbit about 10 km up (both its
    // lowest and highest points in this band), which its own engine has to raise it to.
    public const float SatelliteOrbitLow = 9000f, SatelliteOrbitHigh = 11000f;
    public const float DragRate = 0.08f;              // per second, in surface air: a booster falling through it tops out ~120 m/s
    // Low over the ground, a craft's speed over it is held to this (m/s), by the game, in the
    // physics and under formula alike (GroundBody.LowOverTheGround): the ground's speed range (two
    // closing at up to twice it). What the ground's systems reach ahead by is each a speed times a
    // quarter second (the urgent tiles, the frozen crafts' wake, the floes taken up as bodies), and
    // each is tested to it, none tuned far past it. In open space there is no cap: a vessel goes as
    // fast as its engine and the pulls take it.
    public const float GroundTopSpeed = 4000f;
    // ... "low" being this far (metres) above a body's highest ground, its fine detail included:
    // the furthest any of those reaches from a craft at GroundTopSpeed (the floes': FloeReach, the
    // let-go and the widest floe, then GroundTopSpeed × FloeReachSeconds; 1,144 m, the tiles'
    // 1,030 m and the wake's 1,005 m inside it). Only the floes' reach relies on it: it is worked
    // out from the speed clamped to the cap (RocketPhysics.UpdateFloes), so that none is further
    // than Floes made room for, which holds because a faster craft is above this height, where that
    // reach finds no floe, and is held as it comes in. The tiles' and the wake's reaches go by the
    // real speed, unclamped, so they hold above it whatever it is; the height covers them too, as
    // it would have to were they ever clamped.
    public static readonly float GroundTopSpeedHeight = MathF.Max(
        TerrainTileUrgent + GroundTopSpeed * TerrainTileUrgentSeconds,
        MathF.Max(WakeMargin + GroundTopSpeed * WakeSeconds, FloeReach + Physics.RocketPhysics.FloeLetGo + FloeMaxRadius + GroundTopSpeed * FloeReachSeconds));

    // Fast collisions, predicted ahead (see Collisions): one closing faster, as it hits, than a
    // satellite's width a physics step can pass between two steps unseen, so it is warned of and
    // the game slowed for it. Slower touches Jolt sees step by step.
    public const float FastCollisionStep = 2f * SatelliteRadius; // 1.8 m a step: the narrowest thing that flies, a satellite
    public const float FastCollisionSpeed = FastCollisionStep / Physics.RocketPhysics.StepSeconds; // 216 m/s
    public const float CollisionHorizonSeconds = 20f;  // how far ahead collisions are looked for
    public const float GroundCheckSeconds = 0.25f;    // the ground under a path is asked again this often (its orbit samples the terrain), counted down between
    public const float WarningReactionSeconds = 1.5f; // the player's moment to take a warning in, on top of the turn and the dodge
    public const float WarnedGraceSeconds = 1f;       // a collision warned of is the same one (not warned of, nor the warp dropped, again) this long after it was last the soonest
    // A collision with a solid (the ground, a raft, a tower or a mast) is told from another of its
    // kind by where it is hit: within this of where it was last, it is the same one. Under half the
    // closest two of one kind stand (a drone ship's masts, 31 m apart across its deck; a zone's,
    // 40 m; one tower to a site), so two are never run together; and far more than one thing's
    // collision's place moves in a step (a mast's or tower's is on its own middle line).
    public const float SameSolidRadius = 12f;
    // A vessel in the main view, near its camera, is witnessed too (its fast collisions slow the
    // game, as the flown vessel's and the small views' do): within NearByDistance of the camera,
    // the main view's own reach for what is near (its markers label anything that near), the
    // nearest first and at most this many. Each costs a prediction a step (a pass over every
    // vessel in the world, and the ground's answer), so a crowd in the view (the five rockets
    // launched together, a field of landed boosters) costs a few, not one each.
    public const int MainViewWitnessed = 4;
    // ... and the main view's near reach: metres from its camera that a vessel is near enough to
    // be witnessed, and its marker labelled however quiet (see Markers).
    public const float NearByDistance = 2000f;
    // A marker fades out as its object grows on the screen to be seen for itself (see Markers.Fade):
    // from this radius in pixels to this; a body's ring, the way to point at the body, only from
    // this share of the screen's short side to this, as the body fills a good part of it.
    public const float MarkerSeenFromPixels = 8f, MarkerSeenByPixels = 20f;
    public const float BodyRingSeenFrom = 0.15f, BodyRingSeenBy = 0.3f;
    // Who is in the physics (see Unwatched): the witnessed, and whatever could come within
    // HandoverDistance of something in the physics, or of the
    // main view's camera, within HandoverSeconds at their closing speed now; the rest are moved by
    // formula. The lead is long enough for a queue of returns (HandoversInPerStep a step, so the
    // bodies made never cost a frame its budget) to be in well before anything hits.
    public const float HandoverDistance = 200f;
    public const float HandoverSeconds = 5f;
    // ... and one in the physics leaves only past this times that reach, so one at the edge isn't
    // swapped in and out step after step (as the floats' wake and freeze distances).
    public const float HandoverHysteresis = 1.5f;
    // ... and the most brought back into the physics in one flight step, soonest needed first (one
    // switched to or given a view comes back at once, outside it; leaving costs little, so isn't held).
    public const int HandoversInPerStep = 4;
    // The world's log (see WorldLog) keeps this many of the newest losses; and tells where one
    // happened by the launch site within this many metres of it, else by its height. Up to
    // LogAirborne above what is underfoot it is "on the ground" or "in the sea": a wreck's centre
    // is a little up from where it lay, and the sea's waves a little more.
    public const int WorldLogEntries = 300;
    public const float LogSiteDistance = 5000f;
    public const float LogAirborne = 50f;
    // Under formula (see Formula), anything not on its orbit with its engine off (above the air, clear of the ground) is stepped this often (game
    // seconds): gravity, the turn's pulls, drag and its thrust, as the physics falls it, but a
    // twelfth as often; its touch (the ground, the sea, a deck, a tower or mast) is found along
    // each step. A vessel coming down within this many degrees of upright stands where it touches
    // (on its feet, or its bell), judged by their limits; leaning further, by its hull's, and it lies down.
    public const float FormulaStepSeconds = 0.1f;
    public const float FormulaUprightDegrees = 15f;
    // Two vessels under formula colliding (see Unwatched.FindCollisions) are looked for this often
    // (game seconds), each check over the interval ahead to the next: a few times a game second
    // at most, and short enough that their paths over it are near enough
    // straight (gravity bows 0.25 s of an orbit with the engine off by under 8 cm) to be solved as Collisions solves a pair.
    public const float FormulaCollisionSeconds = 0.25f;
    // The slow-downs (Flight.Slowed), the forced one for a witnessed fast collision and, with the
    // player's setting on, a near miss's: the game's clock and the physics' step slow together.
    // Each begins this far out (middle to middle), and its speed to the eye eases evenly with its
    // distance from its own down to SlowdownPassSpeed, with no speed held on the way: so how long
    // its way in takes follows from these two and its speed (at 1,000 m/s, about 5.6 s). A fast
    // collision is never slower to the eye than the least that keeps each step's travel within
    // FastCollisionStep as it hits (Slowdown.Least), so nothing passes through. The watched
    // near-miss and fast-collision tests are for judging these.
    public const float SlowdownFrom = 1000f;
    public const float SlowdownPassSpeed = 10f;
    // ... reached this far short of the collision or closest approach, along its way, and held until
    // it is as far past, so the pass is seen slow as it comes beside and as it goes ...
    public const float SlowdownHoldWithin = 10f;
    // ... and then eased back from the slowest over this much real time.
    public const float SlowdownEaseSeconds = 3f;
    // Near misses (see Collisions.NearestMiss), slowed only with the player's setting "Slow motion
    // for near misses" on (Settings.SlowNearMisses, off by default): another vessel passing a
    // controlled one within this many of the controlled one's own lengths, outline to outline,
    // without touching. A length is what reads as close beside a rocket, whatever the other's size.
    public const float NearMissLengths = 1f;
    // ... and only one passing faster than this: slower, it is seen well enough at normal speed,
    // and slowing it would hold the game in slow motion for long over a slow drift past.
    public const float NearMissSlowAbove = 300f;
    // How often a small view's border flashes red while anything is coming at its vessel: slow
    // enough to read as a warning, not a flicker.
    public const float ComingFlashHz = 2f;

    /// <summary>
    /// Home's ground (where its pad stands) above the sea, which every height is measured from (the
    /// sea is at 0): the pad is on raised ground looking down on the sea ~330 m east of it, and the
    /// coast between rises steeply from the water rather than lying as a low shore. The world's
    /// origin is the pad's centre, so the sea's sphere is this much smaller than
    /// <see cref="PlanetRadius"/> (the sphere through the pad). The terrain map is generated for it
    /// (tools/TerrainGen), so regenerate the map after changing it.
    /// </summary>
    public const float HomeElevation = 35f;

    /// <summary>
    /// The water depth (metres) from which a sea's waves (<see cref="Sea"/>) are at full size: they
    /// fade out over the shallows above it, to nothing at the shore. Each sea's own wave size is in
    /// the terrain map (tools/TerrainGen's layout).
    /// </summary>
    public const float SeaWaveFullDepth = 4f;

    /// <summary>
    /// How far (metres) two seas' colours blend where they meet: each sea weighs
    /// <c>exp(-d / SeaColorBlend)</c> by its distance d through the water (<see cref="Rendering.SeaColors"/>),
    /// so at the border they are half and half, and a sea's own colour is 90% of the mix 1.1 km in.
    /// The seas' depths blend as much (tools/TerrainGen's 1 km is over the difference of two seas'
    /// distances, which changes twice as fast as the distance to the border), so the colour changes
    /// where the seabed does; seen from afar (a pixel some 30 m at 30 km) the change, 10% to 90% over
    /// 2.2 km, is gradual, and a sea a few kilometres across keeps its own colour in its middle.
    /// </summary>
    public const float SeaColorBlend = 500f;

    /// <summary>
    /// How far (metres) the frozen sheet in the icy seas (<see cref="SurfaceKind.PackIce"/>) stands
    /// above sea level, before its detail: the ice floats with most of it under the water, so its
    /// top is only a little above the sea. The map is generated with it (tools/TerrainGen).
    /// </summary>
    public const float PackIceFreeboard = 0.4f;

    /// <summary>
    /// How much of a sea's wave size is left among the floes round the frozen sheet
    /// (<see cref="SurfaceKind.FloeField"/>): the floes break up the swell. <see cref="Sea.WaveScale"/>
    /// (and its GLSL twin) scales each floe texel's share of the wave size by it.
    /// </summary>
    public const float FloeWaveDamping = 0.5f;

    /// <summary>
    /// How far over a body's map's highest ground (<see cref="GroundBody.MaxHeight"/>) the fine
    /// detail on top of it may reach, with room (it is a few metres): what is higher than this over
    /// the map's highest is clear of every ground (the tiles' reach, a vessel's lights, the formula's orbit).
    /// </summary>
    public const float FineDetailMargin = 20f;

    /// <summary>
    /// The ground's fine detail (rubble, dunes, roughness) on top of the map, per
    /// <see cref="SurfaceKind"/> (indexed by its value, in the enum's order): octaves of value noise,
    /// each (amplitude in metres, wavelength in metres). The collider's 0.5 m samples must follow it
    /// to within 5 cm, so a wavelength under 6 m has to be low: the pack ice's 3 m ripple is 8 cm
    /// tall, and the samples miss it by about 1 cm. The site is left flat. The planet shader copies
    /// this table, so change both together.
    /// </summary>
    public static readonly (float Amplitude, float Wavelength)[][] TerrainDetail =
    [
        [(0.6f, 25f), (0.2f, 6f)], // Scrub
        [(0.3f, 30f)],             // Grass
        [(3f, 60f), (0.4f, 8f)],   // Desert
        [(1.5f, 20f), (0.6f, 6f)], // Rock
        [(0.4f, 20f)],             // Snow
        [(0.1f, 10f)],             // Beach
        [(0.5f, 30f)],             // Seabed
        [],                        // Site
        [(0.25f, 12f), (0.08f, 3f)], // PackIce: low pressure ridges
        [],                        // FloeField: none (the seabed under the floes is never seen, and at the sheet's edge, where it slopes up to the ice, it would move the waterline off the edge's curve)
    ];

    /// <summary>
    /// Each <see cref="SurfaceKind"/>'s colour (display values, indexed by its value, in the enum's
    /// order): scrub, grass, desert, rock, snow, beach, seabed, the pads' site, the pack ice (white
    /// with a blue cast), and the floe band: under the water (never seen) but for the sheet's foot
    /// sloping into it at the edge, so the sheet's ice. The planet's GLSL (<c>kindColors</c>) and the
    /// ground's colour map (<see cref="Rendering.GroundColors"/>) are both made from it.
    /// </summary>
    public static readonly Vector3[] KindColors =
    [
        new(0.47f, 0.43f, 0.28f), new(0.33f, 0.40f, 0.21f), new(0.70f, 0.60f, 0.42f), new(0.36f, 0.33f, 0.30f),
        new(0.85f, 0.88f, 0.92f), new(0.76f, 0.70f, 0.52f), new(0.46f, 0.43f, 0.35f), new(0.55f, 0.52f, 0.47f),
        new(0.86f, 0.91f, 0.95f), new(0.86f, 0.91f, 0.95f),
    ];

    /// <summary>
    /// The power between display values (every colour is given in them) and linear light: the
    /// shaders' <c>toLinear</c> raises a colour to it and <c>finish</c> takes the root, and the C#
    /// that works in linear light (<see cref="Air"/>'s sunlight, <see cref="Rendering.GroundColors"/>'
    /// averaging) uses the same. The GLSL's <c>DISPLAY_GAMMA</c> is written from it.
    /// </summary>
    public const float DisplayGamma = 2.2f;

    /// <summary>How far apart (metres) the heights the ground's normal is worked out from are.</summary>
    public const float TerrainNormalStep = 0.5f;

    // Drawing the terrain: square patches of the cube's faces, split finer near the camera (see
    // PlanetPatches). A patch splits while the camera is nearer to it than its side times the
    // ratio, so the ratio sets how big the squares look on the screen.
    public const int TerrainMaxLevel = 9;               // a face split this many times: ~31 m patches, ~1 m squares
    /// <summary>
    /// The patches a view is expected to need at most (not enforced): PlanetPatchTests checks views
    /// all round the planet stay under it, and the renderer's list starts this big.
    /// </summary>
    public const int TerrainPatchBudget = 600;
    public const float TerrainLodRatio = 3f;            // the main view's
    public const float SmallViewTerrainLodRatio = 1.2f; // the small views': coarser
    public const int PlanetPatchGrid = 32;              // squares across a patch, each way
    /// <summary>
    /// The finest the drawn terrain's squares are taken to be (metres), however near: the fine
    /// detail is drawn in full, and the normals are worked out, at no finer a spacing. (Its own
    /// number, not the physics' <see cref="TerrainNormalStep"/>, which needn't change with it.)
    /// </summary>
    public const float TerrainFinestSpacing = 0.5f;
    /// <summary>How far each patch's skirt hangs below its edge, as a fraction of its side: it hides the cracks where patches of different sizes meet.</summary>
    public const float TerrainSkirt = 0.05f;

    // The ground the physics collides with: height-field tiles laid round each awake craft near
    // the ground (see TerrainTiles), on a fixed grid so crafts near each other share them.
    /// <summary>
    /// Samples across a tile, each way (32.5 m across at the spacing below). 66 rather than 65: Jolt
    /// takes 65 at its default block size (2), but rounds it up to whole blocks itself, adding a
    /// row and column that mirror the one before the last (not repeat the last), a fold half a
    /// metre past the tile's edge that could stand proud of the ground. 66 is a whole number of
    /// blocks, taken as it is (sample count over block size needn't be a power of 2).
    /// </summary>
    public const int TerrainTileSamples = 66;
    public const float TerrainTileSpacing = 0.5f;    // metres between a tile's samples
    /// <summary>A craft has the tiles under where it is and where it will be this many seconds on ...</summary>
    public const float TerrainTileLookahead = 1f;
    /// <summary>... and this far round that stretch, metres.</summary>
    public const float TerrainTileMargin = 16f;
    /// <summary>Only while it is lower than this above the ground (metres), plus how far it goes in <see cref="TerrainTileLookahead"/>.</summary>
    public const float TerrainTileReach = 150f;
    /// <summary>The most tiles at once: past it, the crafts nearest the ground get theirs first.</summary>
    public const int MaxTerrainTiles = 1024;
    /// <summary>A tile nothing has needed for this long (seconds) is let go.</summary>
    public const float TerrainTileLingerSeconds = 2f;
    /// <summary>
    /// How many shapes of tiles taken out are kept for laying them again (the least recently kept
    /// let go past it). A few hundred covers a craft coming back over the ground it has just left,
    /// and each shape is compressed, a few KB, so they cost little to hold.
    /// </summary>
    public const int KeptTerrainShapes = 256;
    /// <summary>
    /// The tiles a craft could reach within this many seconds are urgent: made in the step at once
    /// (each takes about a millisecond), not left to the worker that makes the rest ahead of need.
    /// A time, so the stretch grows with the craft's speed; a quarter second is under it and just
    /// ahead, while the worker has the rest of the lookahead's second to make the others.
    /// </summary>
    public const float TerrainTileUrgentSeconds = 0.25f;
    /// <summary>
    /// ... while it is lower than this (metres) above the ground, plus how far it goes in
    /// <see cref="TerrainTileUrgentSeconds"/>.
    /// </summary>
    public const float TerrainTileUrgent = 30f;

    // Crafts at rest are frozen: taken out of the simulation (no tiles round them) until something
    // happens to them (see RocketPhysics.Freeze), so a world of landed boosters costs nothing.
    public const float FreezeSpeed = 0.05f;       // m/s: moving slower than this ...
    public const float FreezeTurnRate = 0.02f;    // ... and turning slower than this (radians per second) ...
    public const float FreezeSeconds = 3f;        // ... for this long ...
    public const float FreezeClearance = 1f;      // ... within this of the ground (metres), it is frozen
    /// <summary>
    /// Seconds a wrecked craft is left in the world before it is taken out (see RocketPhysics.Wreck),
    /// for a hit it was wrecked in to play out: struck at 16 m/s, the rocket on the pad is
    /// knocked to 3.2 m/s in the moment the striker is wrecked, and the wreck, leaning on it, goes
    /// on pushing it to 3.8 m/s over the next 0.4 s (left 0.1 s, 3.3; 0.3 s, 3.75; 0.4 s or more,
    /// 3.78). Unseen: it is gone from the scene at once.
    /// </summary>
    public const float WreckSeconds = 0.4f;
    /// <summary>
    /// A frozen craft wakes when an awake one moving faster than <see cref="WakeSpeed"/> could come
    /// within this many metres of it, as the gap between their shapes (whatever their sizes); and
    /// while one could, a craft at rest doesn't freeze. It is room for what the way ahead leaves
    /// out: the other turning, tumbling or changing course before it is looked at again.
    /// </summary>
    public const float WakeMargin = 5f;
    /// <summary>
    /// ... its body swept along the way it goes in this long (seconds), not only where it is, so a fast
    /// one wakes what it is coming at in time to hit it (as the tiles' urgency reaches ahead,
    /// <see cref="TerrainTileUrgentSeconds"/>): at the ground's top speed, a kilometre ahead; and nothing
    /// further beside or behind it.
    /// </summary>
    public const float WakeSeconds = 0.25f;
    /// <summary>m/s: slower than this (settling on its legs, say), a craft wakes nothing and keeps nothing awake.</summary>
    public const float WakeSpeed = 1f;

    // The sea is water (see RocketPhysics.Float): a craft in it is buoyed up, slowed, and carried
    // about by the waves, slice by slice down its length, so the waves rock it too.
    /// <summary>How many g a craft wholly under the water is pushed up by: at rest, it floats with a third of itself in.</summary>
    public const float WaterBuoyancy = 3f;
    /// <summary>The slices down a craft's length the water is worked out for, each at its middle.</summary>
    public const int WaterSamples = 8;
    /// <summary>Per second: how fast the water slows each slice's movement through it, by how far under that slice is.</summary>
    public const float WaterDrag = 3f;
    /// <summary>
    /// Per second: how fast the water slows a craft's movement along its length once either end is
    /// in it, however little of it is in. It damps the plunge (a craft set on the water sinks to
    /// about where it floats rather than far past it, so how deep one plunges comes mostly from how
    /// fast it went in) without touching how it tips or bobs lying down.
    /// </summary>
    public const float WaterEndDrag = 0.5f;
    /// <summary>
    /// A craft afloat never comes to rest (the waves move it), so it is frozen by distance instead:
    /// frozen, it wakes when the vessel being flown comes this near (metres) ...
    /// </summary>
    public const float FloatWakeDistance = 2000f;
    /// <summary>
    /// ... and awake, it freezes where it is only once that vessel is further than this: a little
    /// further, so one held near the edge isn't frozen and woken over and over.
    /// </summary>
    public const float FloatFreezeDistance = 2200f;

    // Sun: where it is comes from the world's clock (Sun.Direction); this is its light's colour
    // before any air, white (the colour the eye is adapted to): the air dims and colours it at each
    // place (Air.Sunlight, the shaders' sunlightAt), yellower through more air, orange low down.
    public static readonly Vector3 SunColor = new(1.0f, 1.0f, 1.0f);

    // Each view's sun shadows (SunAreas): a small view's vessel shares an area already chosen this
    // frame while it is this much inside its edge (measured from the vessel's middle, the point an
    // area is centred on: from there to its nose is about 23 m, so nearly all of it is in the
    // area), else it has its own. One sharing keeps on sharing until it is the hysteresis
    // further out, so a vessel hovering at the edge doesn't change areas every frame.
    public const float SunAreaMargin = 22f;
    public const float SunAreaHysteresis = 5f;

    // The air's light (Air): the sky, the haze the ground fades into with distance, and the sun's
    // colour at each place, all from single scattering of the sun, by the air's molecules (Rayleigh:
    // blue scatters most) and by its haze (Mie: forward-peaked, grey), and the ozone's absorption.
    // Coefficients are per metre at the sea, and thin with height as the air does (Mie's with its
    // own scale height: haze hugs the ground; the ozone a layer high up). The shaders repeat the
    // model (skyColor, fog, sunlightAt) from these same numbers.
    /// <summary>Rayleigh scattering per metre at sea level, red, green, blue: Earth's times about 17.4: the planet being that much smaller, so its sky looks like Earth's, and a little more for a clearly orange low sun. The noon haze it makes is half faded by about 2.3 km (AirTests pins 2.2 to 2.8 km).</summary>
    public static readonly Vector3 RayleighScatter = new(1.0e-4f, 2.35e-4f, 5.75e-4f);
    public const float MieScatter = 4e-6f;      // per metre at sea level: the haze's scattering (small: Rayleigh makes nearly all the extinction, which AirTests pins to half faded at 2.2 to 2.8 km)
    public const float MieExtinction = 4.4e-6f; // ... and its scattering plus absorption, what dims the light
    public const float MieScaleHeight = 200f;   // the haze thins by a factor of e every this much higher (as the air's 700 m, but lower)
    public const float MieG = 0.83f;            // how far forward the haze scatters (Cornette-Shanks asymmetry): a glow round the sun, kept to what keeps the noon zenith (the sun overhead) as bright as before (AirTests: 0.85 already makes it 1.17 times, over its bound)
    /// <summary>
    /// What the ozone absorbs per metre at its thickest, red, green, blue: Earth's times 17.4, as
    /// the Rayleigh's is (it absorbs, never scatters). Its Chappuis band takes orange and green out
    /// of the long, grazing way a low sun's light comes: so the twilight sky overhead is blue, not
    /// the olive that scattering alone gives.
    /// </summary>
    public static readonly Vector3 OzoneAbsorption = new(1.13e-5f, 3.27e-5f, 1.5e-6f);
    public const float OzoneHeight = 2000f;     // the ozone is thickest this high above the sea (Earth's 25 km, scaled as the air's height is) ...
    public const float OzoneHalfWidth = 1000f;  // ... and thins evenly to none this far above and below (a tent, as Bruneton's and Hillaire's)
    public const int SkySamples = 16;           // steps along a view ray (in Air, the reference: the shaders take fewer, below)
    public const int SunSamples = 8;            // steps from each of those towards the sun
    /// <summary>
    /// The GPU's march (the shaders'), cheaper than the reference's (<see cref="SkySamples"/>) for the frame
    /// time (the performance tests: 16 and 8 steps everywhere cost a fifth to two thirds more): <see cref="SkySteps"/>
    /// into the sky, short by the eye and lengthening away from it, where the air thins (measured against
    /// <c>Air.Sky</c> in the daytime and at a low sun: within 1% on average, and 8% at worst in one pixel, as
    /// fine grain); to something seen through the haze, a step for each <see cref="AirStepLength"/> of the way,
    /// up to <see cref="FogSteps"/> (the ground near the camera takes one, at its middle); the sky in the water, 2.
    /// Each marches only the stretch of the way the sun lights (none at night), and its samples are jittered
    /// from pixel to pixel, so what few steps miss shows as fine grain, not bands.
    /// </summary>
    public const float AirStepLength = 500f;
    public const int SkySteps = 6;
    public const int FogSteps = 6;
    /// <summary>
    /// What the sky's and the haze's scattered light is scaled by on the screen, and the sky's light
    /// on a surface: the model's radiance is physical, a few hundredths of the sunlight, where lit
    /// ground is shown about as bright as the sunlight. Chosen so the noon zenith from Home's pad is
    /// as bright to the eye (in luminance) as a chosen zenith colour (AirTests pins it within 15%).
    /// </summary>
    public const float SkyExposure = 10.5f;
    /// <summary>
    /// What the sun's direct light on a surface is scaled by on the screen (the sky's light on it is
    /// <see cref="SkyExposure"/>'s, over pi). So a face shows the sky against the sun at about 1.5 times the model's
    /// ratio: a look chosen by eye, not physics (the lit ground as bright as before the air's model, the sky too).
    /// </summary>
    public const float SunExposure = 2.2f;
    /// <summary>The light (linear) left on every surface at night, starlight: enough that shapes still show at a lamp's edge.</summary>
    public const float NightLight = 0.004f;

    /// <summary>Overall strength of the lens flare from the sun (0 turns it off).</summary>
    public const float FlareStrength = 0.7f;
    /// <summary>The share of it left in the map (zoomed out), where its ghosts would cross the paths and labels: dimmed, not hidden.</summary>
    public const float MapFlareShare = 0.25f;


    // The launch pad: a square concrete slab, scorched by launches.
    public const float PadHalfSize = 16f;
    public const float PadTop = 0.5f; // the landing zone's top is at the same height
    /// <summary>How far a pad's concrete foundation reaches below the lowest ground under it (see <see cref="Foundations"/>).</summary>
    public const float FoundationMargin = 1f;
    /// <summary>
    /// How thick the straight-sided slab under a far pad's or far zone's top is (see
    /// <see cref="Footing"/>): below it the foundation narrows inward at 45° into the ground, so
    /// on a slope the downhill side is never a tall sheer wall.
    /// </summary>
    public const float FoundationSlab = 3f;
    /// <summary>
    /// How tall each of the boxes is that the collider steps in by to follow that 45° taper
    /// (<see cref="Footing.Boxes"/>): no box's edge is more than this from the smooth taper, and
    /// the underside is seldom touched.
    /// </summary>
    public const float FoundationStep = 0.5f;
    /// <summary>
    /// The shortest the last of those steps may be: a remainder of the taper shorter than this is
    /// folded into the step above it, so the collider never ends in a sliver of a box millimetres
    /// tall (which would do nothing but add a body part), and no step is more than this over
    /// <see cref="FoundationStep"/> tall.
    /// </summary>
    public const float FoundationShortestStep = 0.05f;

    // The landing zone: a round concrete pad out across the scrub, with a target painted on it,
    // ~320 m north-west of the launch pad: inland, as the coast runs ~330 m east of the pad. The
    // planet is small enough that it sits 5 m lower, tilted a couple of degrees to stand level on
    // the curve.
    public static readonly Vector3 LandingZoneCentre = Planet.SurfacePoint(-220f, -230f);
    public static readonly Quaternion LandingZoneTilt = Planet.UprightAt(LandingZoneCentre);
    public const float LandingZoneRadius = 18f;
    /// <summary>Touch down within this distance of the landing zone's centre to count as landed on it.</summary>
    public const float LandingTargetRadius = 10f;
    /// <summary>
    /// How long (game seconds) the middle of the screen says a vessel has landed or splashed down,
    /// with its touchdown and score, before it clears so the view is the player's again (the mission
    /// panel keeps the score). A loss stays said, with how to fly on. In game seconds, as the HUD's
    /// other news of the flight is timed (lift-off, staging, the mission failing): it passes with the
    /// flight, so in a warp it is brief, and the mission panel still has the score.
    /// </summary>
    public const double CameDownMessageSeconds = 8;

    /// <summary>How long (real seconds) the BRUTAL credits panel stays on the screen once play first begins, then gives its room back: as long as the licence asks (legible before play), and no longer. Every menu shows it again.</summary>
    public const double CreditsSeconds = 5;

    /// <summary>
    /// How long (real seconds) the word of a run that ended while another was flown (<see cref="Flight.Notice"/>)
    /// stays on the screen, the last <see cref="NoticeFadeSeconds"/> of it fading: real time, so a
    /// warp doesn't hurry it past. The world log keeps it.
    /// </summary>
    public const double NoticeSeconds = 6, NoticeFadeSeconds = 1.5;

    // The drone ship: a barge riding the Sunrise Sea ~2.4 km east round the planet, where a booster
    // dropped at its landing-fuel mark on the way to orbit would fall if left alone (that far round,
    // up is 14° from the launch pad's), its length running east. Its centre is where its
    // waterline would be on a calm sea, straight under that spot; the waves lift and tilt it from
    // there (see Raft, Rafts.Ship). Its deck is a rectangle, DroneShipHalfLength and DroneShipHalfBeam
    // either side of the centre, with room for a booster's legs (the landing zone is 18 m round).
    public static readonly Vector3 DroneShipCentre =
        Planet.Centre + Planet.Up(Planet.SurfacePoint(2390f, 0f)) * Planet.SeaRadius;
    // Home's second drone ship, for the moon trip's booster: launched at the trip's tilted plane's
    // descending crossing, the climb heads some 30° south of east, and the booster dropped at its
    // landing-fuel mark on that heading comes down in the Sunrise Sea about 2 km out, 43° south of
    // east (where the lander launcher's ascent's booster came down, measured by LanderTripTests'
    // playthrough). The sea there is 22 m deep, deeper than DroneShipMinDepth, so it stands where
    // the booster comes down. (Launched at the ascending crossing, heading north of east, the
    // booster comes down where there is no ship.) Its length runs east, as the first's does.
    public static readonly Vector3 TripDroneShipCentre =
        Planet.Centre + Planet.Up(Planet.SurfacePoint(1431f, 1338f)) * Planet.SeaRadius;
    /// <summary>
    /// The shallowest sea a drone ship rides (metres, under its waterline on a calm sea): its
    /// draught (<see cref="DroneShipDraft"/>) with the roughest sea's troughs (<see cref="Sea.Highest"/>,
    /// some 1.5 m) and a few metres more, so its keel never meets the seabed however the waves move it.
    /// </summary>
    public const float DroneShipMinDepth = 10f;
    public const float DroneShipHalfLength = 26f;
    public const float DroneShipHalfBeam = 16f;
    /// <summary>How far the deck's top stands above the waterline.</summary>
    public const float DroneShipFreeboard = 3f;
    /// <summary>How far the hull reaches below the waterline.</summary>
    public const float DroneShipDraft = 4f;

    // The floes in the icy seas' floe bands (SurfaceKind.FloeField; see Floes): discs of ice laid
    // out on a jittered grid FloeSpacing across, over the band only: a floe in up to FloeCover of
    // its cells by the sheet, thinning out across the band to open water and clustered by noise
    // FloeClusterSize across; each FloeMinRadius to FloeMaxRadius round (most of them small) and
    // kept clear of its cell's edges, so no two overlap. Each is a raft (see Raft): FloeThickness thick, its top FloeFreeboard above the
    // waterline, held in the pack where it is and heaving and tilting with the waves under it.
    // The layout is fixed (the same every run and in every world), so none is saved.
    public const float FloeSpacing = 26f;
    public const float FloeCover = 0.95f;
    public const float FloeClusterSize = 220f;
    public const float FloeMinRadius = 4f;
    public const float FloeMaxRadius = 14f;
    public const float FloeThickness = 1.6f;
    public const float FloeFreeboard = 0.2f;
    /// <summary>
    /// How far (metres) under the sea the ground must be under all of a floe, so it is kept off the
    /// sheet's foot (where its edge slopes into the water): its draft, and half a metre more for
    /// its heave, so it never bobs through the ice. Past the sheet that leaves a lead of open water
    /// a few metres wide.
    /// </summary>
    public const float FloeFootClearance = FloeThickness - FloeFreeboard + 0.5f;
    /// <summary>A floe is a body in the physics while an awake craft is within this (metres) of its rim.</summary>
    public const float FloeReach = 120f;
    /// <summary>
    /// ... and further by how far that craft goes in this long (seconds), so a fast one has the
    /// floes ahead of it as bodies before it reaches them (as the tiles' urgency reaches ahead,
    /// <see cref="TerrainTileUrgentSeconds"/>).
    /// </summary>
    public const float FloeReachSeconds = 0.25f;
    /// <summary>How many floes can be bodies at once (the nearest an awake craft win).</summary>
    public const int MaxFloeBodies = 256;
    /// <summary>
    /// The floes are drawn within this (metres) of the camera. Beyond it the floe band's water is
    /// tinted towards the ice by how much of it the floes cover (<see cref="Floes.Cover"/>), the
    /// tint fading in from <see cref="FloeTintFrom"/>, so the band reads from far off and no floe
    /// pops in as it comes into range.
    /// </summary>
    public const float FloeDrawDistance = 2500f;
    public const float FloeTintFrom = 2000f;

    /// <summary>
    /// The test range: six landing zones tilted on purpose, for trying out landing on a slope, the
    /// last (45°) too steep to stand on. They stand in a row running north of the launch pad. The
    /// ground there is a low coastal plain below the pad; the map levels each pad's site to the
    /// pad's height, so the row stands on a raised embankment, as the pad and the landing zone stand
    /// on mounds of their own. It is kept clear of the launch path (east), of the way back to the
    /// landing zone, of the coast and of the satellite tests' off-target landing. Each falls away to the east, and stands on a plinth with
    /// vertical sides (see <see cref="Plinth"/>), raised so that its downhill edge is
    /// <see cref="PadTop"/> above the ground, as a level zone's edge is.
    /// </summary>
    public static readonly (string Name, Vector3 Centre, float SlopeDegrees, Quaternion Tilt)[] TestPads =
        [.. ((float[])[5f, 10f, 15f, 20f, 25f, 45f]).Select((slope, i) => TestPad(slope, Planet.SurfacePoint(-50f, -500f - TestPadSpacing * i)))];
    public const float TestPadSpacing = 100f;

    /// <summary>
    /// A test pad over <paramref name="ground"/>: upright there, then turned about the local north
    /// by <paramref name="slopeDegrees"/> (so its top falls away to the east), and raised so its
    /// downhill edge stands <see cref="PadTop"/> above the ground.
    /// </summary>
    private static (string, Vector3, float, Quaternion) TestPad(float slopeDegrees, Vector3 ground)
    {
        var (sin, cos) = MathF.SinCos(float.DegreesToRadians(slopeDegrees));
        var centre = ground + Planet.Up(ground) * (LandingZoneRadius * sin + PadTop * (1f - cos));
        var tilt = Quaternion.Concatenate(Planet.UprightAt(centre), Quaternion.CreateFromAxisAngle(Planet.North(centre), float.DegreesToRadians(slopeDegrees)));
        return ($"Test pad {slopeDegrees:0}°", centre, slopeDegrees, tilt);
    }

    /// <summary>
    /// Every landing zone (each the same round pad): where it is, and its tilt. The landing zone
    /// comes first, then the <see cref="TestPads"/>. (The drone ship is a landing site too, but not
    /// one of these: it floats.)
    /// </summary>
    public static readonly (Vector3 Centre, Quaternion Tilt)[] LandingZones =
        [(LandingZoneCentre, LandingZoneTilt), .. TestPads.Select(pad => (pad.Centre, pad.Tilt))];


    // The service tower beside the pad: a lattice of girders, close enough to be in the way.
    public static readonly Vector3 TowerOffset = new(-12f, 0f, -2f); // its foot, in the pad's frame (see LaunchSite.Frame)
    public const float TowerHalfWidth = 1.8f;
    public const float TowerHeight = 28f;

    // The lights at every pad, zone and drone ship (PadLights): floor lights round each edge,
    // floodlights on masts round it where the ground allows, and lights at each tower's foot.
    public const float FloorLampSpacing = 2f;      // metres between the floor lights round an edge
    public const float MastHeight = 20f;           // a floodlight mast's (under the tower's 28 m)
    public const float ShipMastHeight = 6f;        // the drone ship's, at its deck's corners
    public const float MastHalfWidth = 0.3f;
    public const float MastOut = 10f;              // beyond the concrete's furthest point, along a diagonal: clear of a landing's legs
    public const float MastLevelTolerance = 3f;    // a mast stands only on ground this near its place's level ...
    public const float MastMaxSlopeDegrees = 20f;  // ... and no steeper than this
    public static readonly Vector3 LampColour = new(1.0f, 0.86f, 0.66f); // warm white (display values)
    public const float FloodBrightness = 2.2f;       // a floodlight's light, as the shaders add it
    public const float DuskSunHeight = 0.08f;      // a place's lights come on as the sun sinks below this height (1 overhead, 0 on the horizon) ...
    public const float DawnSunHeight = 0.12f;      // ... and go off as it climbs above this one: the gap between holds the state, so they never flicker
    public const float PadLightFadeSeconds = 3f;   // real seconds for a place's lights to fade fully on or off
    public const float FloodRange = 80f;           // metres: no light from it past this
    public const float FloodConeDegrees = 34f;     // its beam's edge (half angle), fading in from FloodCoreDegrees
    public const float FloodCoreDegrees = 22f;
    public const float TowerLightBrightness = 2f;  // the tower's lights, up its girders
    public const float TowerLightRange = 36f;
    public const float FloorLightBrightness = 0.35f; // each edge's floor lights, washing across the concrete
    public const float FloorLightRange = 14f;
    public const float LightReach = 1500f;         // metres from the camera: a place nearer has its lights in the shaders
    public const float ShadowPlaceHysteresis = 5f; // metres nearer another place must be before its floods take over the shadows
    public const float LampGlowPixels = 5f;        // a lamp seen from far off: about this many pixels across, however far
    public const float GlowPixelPerMetre = 0.0016f; // about a pixel's size a metre from the camera: a far glow's size, per metre off, for each of LampGlowPixels
    public const float LampGlowBrightness = 1.4f;  // a floodlight's or tower light's far glow (a point of light), fully on
    // Far glows (the lamps' and the landing lights') seen from further than GlowTwinkleDistance (the
    // planet's night side from the moon, or zoomed out) twinkle: each one's brightness shimmers by up
    // to GlowTwinkle of itself either way, by its own beat (a hash of where it is) and the clock,
    // eased in over half as far again; nearer, steady.
    public const float GlowTwinkleDistance = 15_000f;
    public const float GlowTwinkle = 0.3f;

    // Each vessel's landing lights (Vessel.LandingLights): on in the dark where it is, near the
    // ground, faded over PadLightFadeSeconds of real time; one spot at its bell, down its axis.
    public const float LandingLightsBelow = 500f;    // metres above what is underfoot: they come on below this ...
    public const float LandingLightsOffAbove = 550f; // ... and go off only above this one: the gap between holds them, so one hovering at the height doesn't blink
    public const int LandingLightsLit = 4;           // the nearest lit vessels that light the ground in each view (room kept for them among the pad lights)
    public const float LandingLightReach = 500f;     // metres from a view: a lit vessel nearer can light its ground
    public const float LandingLightRange = 150f;     // metres: no light from it past this (its pool shows from about 100 m up)
    public static readonly Vector3 LandingLightColour = new(0.85f, 0.92f, 1.0f); // cool white (display values), apart from the pads' warm lamps
    public const float LandingLightBrightness = 2f;  // its light, as the shaders add it (beside FloodBrightness)
    public const float LandingLightConeDegrees = 60f; // its beam's edge (half angle), fading in from LandingLightCoreDegrees
    public const float LandingLightCoreDegrees = 40f;
    public const float LandingLightGlowBrightness = LampGlowBrightness; // its far glow (a point of light, sized as a floodlight's: LampGlowPixels), as bright as a floodlight's fully on

    // The booster: its origin is the bottom of the body (the top of the engine bell).
    public const float RocketRadius = 1.2f;
    public const float RocketBodyHeight = 19f;
    public const float RocketNoseHeight = 3f;
    public const float RocketBellHeight = 1f;
    public const float RocketBellRadius = 0.8f;
    /// <summary>Where the body's base sits at the start, in the pad's frame: standing on its engine bell on the pad.</summary>
    public static readonly Vector3 RocketStart = new(0f, PadTop + RocketBellHeight, 0f);

    // The upper stage, for reaching orbit: it sits on the booster in place of its nose cone, its
    // engine bell tucked inside the booster's interstage until they separate.
    public const float InterstageHeight = 1.2f;
    public const float UpperStageHeight = 8f;
    public const float UpperNoseHeight = 3f;          // the payload fairing
    public const float UpperBellHeight = 0.9f;
    public const float UpperBellRadius = 0.55f;
    // A satellite, carried on top of an upper stage in place of its fairing: a squat body with solar
    // panels down its sides, an antenna on top, and a little engine underneath.
    public const float SatelliteRadius = 0.9f;
    public const float SatelliteBodyHeight = 1.8f;
    public const float SatelliteNoseHeight = 0.7f;     // the antenna
    public const float SatelliteBellHeight = 0.3f;
    public const float SatelliteBellRadius = 0.25f;
    // The satellite's fairing, round it on top of the upper stage until jettisoned: a cylinder as
    // wide as the rocket, and a nose like the upper stage's own. It comes away in two halves.
    public const float FairingHeight = 3.0f;           // the straight part, over the satellite
    public const float FairingNoseHeight = 2.4f;
    public const float FairingMass = 200f;             // kg, both halves
    public const float FairingSeparationSpeed = 3f;    // m/s each half is pushed away at
    // A lander, carried on top of an upper stage in place of its fairing and flown down onto the
    // moon: a square body, its payload bay at its foot (open at its two X faces, where the doors
    // go: room for a 2 m by 1.5 m rover to drive out along X), tanks on the deck over the bay, an
    // antenna on top and an engine bell underneath. Four legs at its corners, stowed folded up them.
    public const float LanderHalfWidth = 1.15f;        // the body 2.3 m across ...
    public static readonly float LanderCornerRadius = LanderHalfWidth * MathF.Sqrt(2f); // ... its corners 1.63 m from the axis
    public const float LanderBodyHeight = 2.4f;
    public const float LanderBayFloor = 0.15f;         // the bay's floor, this far up the body
    public const float LanderBayHeight = 1.5f;         // the bay's room inside, floor to roof
    public const float LanderBayHalfWidth = 1.05f;     // the bay 2.1 m by 2.1 m inside
    public const float LanderAntennaHeight = 0.3f;     // on top of the body
    public const float LanderBellHeight = 0.5f;
    public const float LanderBellRadius = 0.35f;
    public const float LanderLegHingeOut = 0.05f;      // each leg's hinge this far out from its corner
    public const float LanderLegHingeHeight = 0.6f;
    public const float LanderLegLength = 1.8f;         // stowed, up its corner to the top of the body
    public const float LanderLegDeployedDegrees = 135f;
    public const float LanderLegStroke = 0.4f;
    // Its foot pads: small, so stowed (tilted with the leg, level only once it is out) they stay
    // inside the fairing round it; the rocket's are FootHalfWidth.
    public const float LanderFootHalfWidth = 0.07f;
    // Its legs drawn slimmer than the rocket's (LegGeometry's widths), and their struts nearer in,
    // so stowed up the corners they clear the bay's doors, folded shut, by more than 2 cm.
    public const float LanderLegHalfDepth = 0.09f;     // out from the body, the leg 0.18 m ...
    public const float LanderLegHalfWidth = 0.06f;     // ... by 0.12 m across
    public const float LanderStrutInward = 0.1f;
    public const float LanderStrutHalfWidth = 0.025f;
    // The bay's doors, its two X faces, each hinged along the bay floor's edge. Each is the first
    // of LanderRampPanels panels hinged end to end, the others folded flat against its outside,
    // concertina-wise, while it is shut. Over LanderDoorSeconds each door swings out and down as
    // its panels unfold (RocketGeometry.PanelTransform), ending as one straight ramp, past level,
    // until on level ground with the legs at full stroke its far edge is LanderRampClearance above
    // it (RocketGeometry.DoorOpenRadians). Every panel is the bay's width.
    public const float LanderDoorHeight = 1.6f;        // from the floor's edge to the top of the deck: each panel's length
    public const float LanderDoorThickness = 0.06f;    // each panel's
    public const int LanderRampPanels = 3;             // the ramp 4.8 m long, about 15° below level on level ground
    public const float LanderDoorSeconds = 6f;
    public const float LanderRampClearance = 0.1f;
    // The rover the bay is made for, counted in the lander's dry mass (nothing drawn), so the
    // mission adds up once one rides in it.
    public const float RoverAllowance = 300f;          // kg
    // The lander's fairing, wider than the stack (the bay doesn't fit inside the rocket's 2.4 m),
    // drawn with a short cone in from its radius to the upper stage's at its foot. It comes away
    // in two halves, as the satellite's does.
    public const float LanderFairingRadius = 1.85f;
    public const float LanderFairingHeight = 3.2f;     // the straight part, over the lander
    public const float LanderFairingNoseHeight = 2.6f;
    public const float LanderFairingMass = 300f;       // kg, both halves
    // Without its fairing, a payload breaks up in thick, fast air: past this much air pressure (the
    // air's thickness as a share of the ground's, times the speed squared). Climbing at 200 m/s,
    // that is anywhere below about 1.9 km; above 2 km it is safe.
    public const float FairingMaxAirPressure = 1000f;

    public const float StageIgnitionSeconds = 1f;     // after separation, before the next engine lights
    public const float SeparationSpeed = 1.5f;        // m/s the stages are pushed apart at

    // Landing legs: four, hinged low on the body. Stowed they lie folded up its side; deployed they
    // swing down and out, so their feet reach out past the engine bell and below it.
    public const float LegHingeHeight = 2.5f;
    public const float LegLength = 5.2f;
    public const float LegDeployedDegrees = 136f; // from pointing straight up the body
    public const float LegDeploySeconds = 1.6f;
    public const float FootHalfWidth = 0.45f;     // each foot's pad, square, 0.9 m across
    // The legs are crush legs. In the air each hangs at full stroke, this far (in metres) below
    // where it is pushed right up (where the collider's feet are), so each foot reaches its own
    // ground: on a slope the downhill legs can stand further out than the uphill ones, by up to
    // the stroke over the feet's spread, as far as the touchdown pushed the uphill ones up.
    public const float LegStroke = 1.2f;
    // Once all its feet are down (LegsToHold, below), each leg holds rigid until stopping its foot
    // would change the rocket's speed by more than this (m/s²); past it, it pushes that hard and
    // gives, shortening. The push is along the ground's normal under the foot. The rocket's weight
    // (1 g) is well inside the legs' hold, so it stands on them without sinking, while a hard
    // touchdown is taken over a moment as they shorten. Only ever a push, never a pull or a
    // spring back out, so it can't bounce.
    public const float LegHoldAcceleration = 15f;
    // But the legs share the load, as linked legs would: until this many feet are within their
    // stroke of the ground, each one down holds only LegSettleHold (m/s², under the weight), so
    // the first feet down give, and don't tip the rocket onto the others, until those reach their
    // own ground. All four: with three (the uphill foot and the two beside it, on a slope with a
    // leg pointing down it), the two beside it stand on the line it tips about, so it would still
    // tip onto the downhill leg. Too high a settle hold and the uphill legs stop giving before
    // the downhill one is down (the slope tests show where).
    // Once that many have been down, the legs lock, each holding the full LegHoldAcceleration
    // until the rocket leaves the ground: settled, they stay rigid even where leaning on a slope
    // lifts the uphill foot out of its stroke, rather than giving again and letting it roll over.
    public const int LegsToHold = 4;
    public const float LegSettleHold = 5f;
    // Where a foot pushes, it also grips: its slide along the ground is cut by up to this much of
    // the push. The push is square to the ground, so on a slope it pushes the rocket downhill, and
    // the grip is what holds it there: it stays put on a slope gentler than this friction allows
    // (a slope needs its tangent: tan 15° = 0.27), and can't hold it on a steeper one. One that comes down
    // drifting sideways is stopped by its feet too. The same on every surface for now.
    public const float FootFriction = 0.8f;

    // Flight, tuned for arcade handling rather than realism. (Each rocket's engines and fuel are in Vehicle.cs.)
    public const float ThrottleRate = 0.9f;           // per second, while a throttle key is held
    public const float TiltRate = 0.9f;               // how fast the steering turns it at full throttle, radians per second
    public const float ThrusterAuthority = 0.3f;      // with the engine off, small thrusters turn it this fraction as fast
    public const float LaunchThrottle = 0.75f;        // a rocket on the pad starts at this throttle, until the player sets another

    // The air turns the rocket to face along its direction of travel (nose first, or tail first if
    // it is falling backwards), harder the thicker the air and the faster it goes: radians per second
    // for each kg/m³-ish of air times (m/s)², at right angles to the airflow. Above the atmosphere,
    // nothing turns it: it holds whatever way it points.
    public const float AeroStability = 6e-5f;
    public const float AeroMaxTurnRate = 1.5f;

    // Landing: climb past the target altitude, then bring the booster back down.
    public const float TargetAltitude = 400f;
    public const float SafeTouchdownSpeed = 6f;       // m/s: any faster and the legs give way
    public const float SafeBellTouchdownSpeed = 2.5f; // m/s: without legs, on the engine bell (which stands on the pad), only this gently
    // Leeway on the touchdown limits: each is given what falling this far (m) adds, and a physics
    // step's more (a touch is the speed going into the step it touches in), so 6 m/s is judged as
    // 6.27 and the bell's 2.5 as 3.01, keeping landings no harder than intended. The player is
    // still told the round numbers above. The fall and the step's are by the pull at the ground of
    // the body it touches down on (its SurfaceGravity: the planet's is SurfaceGravity).
    public const float TouchdownLeewayHeight = 0.12f;
    internal static float TouchdownLimit(GroundBody body, float safe) =>
        MathF.Sqrt(safe * safe + 2f * body.SurfaceGravity * TouchdownLeewayHeight) + body.SurfaceGravity * Physics.RocketPhysics.StepSeconds;
    // m: within this of what is under it, a vessel that has been up off it further than this is
    // told when it falls faster than a landing on that body survives (Hud.TooFastLine): the room
    // there is to slow down in, the same at every body; the speeds are each body's.
    public const float TooFastBelow = 60f;
    public const float LandedSettleSeconds = 1.5f;    // still on the ground (however it lies) for this long counts as landed (or afloat this long, as floating)
    // m/s: the fastest any part of a vessel can hit the water and come through it, more forgiving
    // than the ground, so one can come down upright and tip over onto its side in the water safely.
    public const float SafeSplashdownSpeed = 15f;
    // Afloat, a vessel has settled (and is floating) once it moves through the water slower than
    // this (m/s) and turns slower than this (rad/s), in a sea whose waves are up to a metre high;
    // in a rougher one, in proportion to its waves. The waves alone keep it under both.
    public const float FloatSettledSpeed = 1f;
    public const float FloatSettledTurnRate = 0.15f;
    // Metres: afloat, with its centre of mass this far above its centre of buoyancy, a vessel is
    // still to tip over (top-heavy), so it isn't floating yet.
    public const float FloatTopHeavy = 0.5f;
    // m/s: a part going back into the water slower than this (with the waves) makes no splash.
    public const float SplashSpeed = 3f;
    // Standing on the ground or in the water, a vessel can only be steered with its throttle over
    // this (the engine lifting it off again): otherwise one landed leaning over would be held up
    // when it should fall, and a tall one held upright in the water would never lie down in it.
    public const float SurfaceSteeringThrottle = 0.3f;

    // The direction rings round the rocket's centre of mass, for steering with the mouse: their
    // size on screen, and how close the pointer has to be to a ring to grab it.
    public const float NavigationRingPixels = 110f;
    public const float NavigationGrabPixels = 14f;
    public const float NavigationUpSnapDegrees = 4f;  // a pitch this close to straight up snaps to it

    // Chase camera: behind the booster and a little above, orbited with the right mouse button and zoomed with
    // the wheel. It trails a little behind the booster's movement, so speed can be felt.
    public const float CameraVerticalFovDegrees = 55f;
    public const float CameraDistance = 48f, CameraMinDistance = 20f, CameraMaxDistance = 3000f; // far out, to see the planet from orbit
    public const float CameraPitchDegrees = 10f;      // above the booster's middle, to start with
    // Where the camera starts when put on a place (a site or a point of interest): this high over
    // its top and this far to one side, looking at it, as if watching from beside it.
    public const float PlaceViewHeight = 30f, PlaceViewAside = 60f; // metres

    public const float CameraFollowRate = 10f;        // per second: how tightly it keeps up
    public const float CameraMaxLag = 2f;              // metres: never further behind than this (fast, or a stage dropped, it would trail far off)
    public const float CameraGroundClearance = 1.5f;   // metres: kept from the ground (and the waves) all round it, well past the near plane's corners
    public const float CameraSlideMinDistance = 3f;    // metres: looking up from under the ground, the camera slides in no nearer the vessel than this (a low vessel's middle is inside its own near plane)
    public const float CameraFacingPitchDegrees = 80f; // beyond this pitch the camera is looking too nearly up or down to say which way it faces: the compass keeps the last facing it had under it
    public const float CameraNearPlane = 0.5f;         // metres: the near plane, at every zoom (the far one is at infinity: ChaseCamera.Planes)

    // Real seconds the camera's up takes to turn from the body it was by to the one its vessel is
    // at now (across a sphere of influence's edge), its place and view kept as they were: so the
    // view rolls round to the new ground's up over a few seconds rather than in one frame.
    public const float CameraBodyEaseSeconds = 3f;

    // Zoomed out (M): the camera leaves the vessel to look at the whole of its body and the paths
    // round it, from above its north pole and a little to the side, far enough out to take in the
    // flown vessel's orbit; and comes back again. Turned and zoomed there as it is by the vessel,
    // from near enough to see the body fill the screen out to the whole system: the planet's whole
    // path round the sun (twice the sun's distance across) fitted on the screen as an orbit is
    // (ZoomOutMargin). Zoomed out, the camera keeps this many of the sun's radii from its centre,
    // so it is never in it, whichever way it is turned.
    public const float ZoomOutSeconds = 0.9f;          // real seconds, out or back, whatever the time warp
    public const float ZoomOutPitchDegrees = 62f;      // above the orbits' plane, to start with
    public const float ZoomOutMargin = 1.3f;           // the orbit fills the height of the screen less this
    public const float ZoomOutMinDistance = 16000f;    // from what it looks at (the vessel)
    // The furthest above its body's sea the zoom-out key fits a path in (an escape's, or an orbit
    // reaching that far), within the body's sphere of influence too: thirty of the planet's radii,
    // the moon's way round it and well past, rather than the whole solar system, which the player
    // zooms out to by hand.
    public const float ZoomOutReach = 30f * PlanetRadius;
    public static readonly float ZoomOutMaxDistance =
        (2f * SunDistance + SunRadius) * ZoomOutMargin / MathF.Tan(float.DegreesToRadians(CameraVerticalFovDegrees) / 2);
    public const float ZoomOutSunClearance = 2f;

    // The map (zoomed out, SystemMap): each body drawn no smaller than this across (a dot in its
    // place, where its own disc would be smaller), the sun's mark round its disc, and each body's
    // path round what it goes round (the planet's round the sun), among the stars: faded in as the
    // camera pulls back from the body itself, from this share of the path's radius to this (so the
    // planet's, a line straight through it close by, shows only once its curve can be seen, from
    // 150 km out; the moon's from the closest zoom). Every vessel flying that is neither flown nor
    // the target draws its path round its own body this strongly, unless it is under this many
    // pixels across on the screen (it is a dot's worth of its body then).
    public const float MapBodyMinPixels = 4f;
    public static readonly (byte R, byte G, byte B) MapBodyColor = (205, 210, 220), MapSunColor = (255, 215, 130);
    public const float MapBodyPathFadeFrom = 0.05f, MapBodyPathFadeTo = 0.2f;
    public const float MapBodyPathAlpha = 0.5f;
    public const float MapLineClickAlpha = 0.1f;       // a body's path takes a click only once drawn at least this strongly (a fifth of its full strength), not while it has barely faded in
    public const float MapOtherPathAlpha = 0.2f;
    public const float MapLineMinPixels = 2f;
    public const float MapLineSagPixels = 3f;          // how far a vessel's lap on the map may bow from its pieces, at most (it is drawn in fewer, the smaller it is)
    // A label of the path's or a vessel's that a body's label sits on (at the map's far zooms) is
    // faded to this share of its strength, the body's drawn over it (MapLabels).
    public const float MapLabelUnderAlpha = 0.3f;

    // The predicted path ahead of the vessel being flown: none close by it, fading in as the camera
    // pulls back (between these distances from the vessel) and full when zoomed out.
    public const float PathFadeFrom = 250f, PathFadeTo = 1200f;
    public static readonly Vector3 PathColor = new(0.45f, 0.85f, 1.0f);
    public const float PathWidth = 2f;                 // pixels
    // Each orbit line shows which way it is flown by soft brighter glows travelling along it the
    // way it is flown (Paths.Glows), the line itself solid: one every this share of the screen's
    // width along it, so a line across the screen shows one or two, and no more than
    // PathGlowsPerLine on a line's part on the screen; each moving on this many pixels a second of
    // real time (the same in warp, slow motion and pause), this many pixels long along the line,
    // fading in and out at its ends, its middle the line's colour this share of the way to white,
    // at this share of the line's own strength, this wide.
    public const float PathGlowEveryScreens = 1f;
    public const int PathGlowsPerLine = 2;
    public const float PathGlowPixelsPerSecond = 160f, PathGlowPixels = 110f;
    public const float PathGlowWhiten = 0.55f, PathGlowAlpha = 1f, PathGlowWidth = 3f;

    // The map's predicted collisions between any two vessels in the world (CollisionSearch, drawn by
    // Paths): a cross where they hit, and the stretch of each one's path it covers in the lead
    // before (so a faster pair's line is longer), in the collision's colour. A vessel neither flown
    // nor the target draws only that stretch, this much as strong as the flown path (0.9), so the
    // player still sees at a glance what they are flying. Searched a lap of the flown vessel's
    // orbit ahead, but no further than the horizon; a pass over a world of a thousand satellites
    // must finish within the last on its own thread (real seconds), or the marks aren't worth
    // their cost (see CollisionSearchTests).
    public const float CollisionLeadSeconds = 120f;
    public static readonly (byte R, byte G, byte B) CollisionColor = (255, 80, 80);
    public const float OtherPathAlpha = 0.35f;
    public const float MapCollisionHorizonSeconds = 3600f;
    public const float CollisionSearchSeconds = 2f;
    // What the map draws of them, so its frame is bounded however many there are: the soonest
    // this many; a cross within this many pixels of a sooner one's isn't drawn (its stack labelled
    // by the soonest); one whose two vessels, on their orbits as they are now, would be further
    // apart then than their sizes and this (a dodge since the pass) isn't drawn; nor are any from
    // a pass asked longer ago than this in real seconds (the map shut and opened again), until
    // the next comes.
    public const int CollisionsShown = 20;
    public const float CollisionMergePixels = 12f;
    public const float CollisionDriftMetres = 100f;
    public const double CollisionStaleSeconds = 5.0;

    // The marks on the path for a target: the stretch of orbit where one burn would take the vessel
    // to it (within this much of it, there), or, for a place on the ground, bring it down by it.
    public const float TransferWithin = 1000f;
    public const float DeorbitWithin = 500f;
    public const float DeorbitDepth = 1000f;           // a de-orbit burn brings the lowest point this far below the ground: down at a sensible angle
    // A place comes back under an orbit only every lap and a half or so (the ground turns under
    // it), so with none this lap the de-orbit window is looked for on the laps after, this many in
    // all. Among the stars the line is one lap, and a window on the next is shaded on it at this
    // much of the strength of this lap's, and marked as the next lap's.
    public const int DeorbitLaps = 2;
    public const float NextLapWindowAlpha = 0.45f;
    // A window takes tens of milliseconds to work out over two laps, so each frame works on it for
    // no longer than this, and the last one worked out is drawn until it is done (see Paths.Work).
    public const double WindowWorkMilliseconds = 1.5;
    // A window being worked out on an orbit the vessel has left (by a burn) is started again: where
    // that orbit would have it now is further than this from where it is, or moving this much
    // differently. With the engine off above the air, the two agree to a few metres over minutes; in the air, the most the
    // drag could have slowed it meanwhile is allowed on top (Paths.Left). The same goes for the
    // target's orbit. It is started again no more often than a plan is (Paths.PlanEverySeconds).
    // The window drawn is held to the same test every frame, and hidden once its orbit is left.
    public const float WindowDriftMetres = 20f, WindowDriftSpeed = 0.2f;
    // On the orbit a window was worked out on (above the air, its engine off), it holds: it is worked out
    // again only once the scan's horizon (up to DeorbitLaps from the plan) has moved on by this
    // much of a lap, so it still reaches as far ahead, or once all of it is passed.
    public const double WindowReplanLaps = 0.1;

    // Small views: other vessels, each through a chase camera of its own, in a strip along the
    // bottom of the screen. Drawn cheaply (a coarser planet, no smoke or lens flare).
    public const int MaxSmallViews = 5;
    public const float SmallViewMaxHeight = 300f;     // pixels, 4:3; smaller to fit the strip
    public const float CameraRadiansPerDragPixel = 0.006f;

    // A vessel's actions (ActionMenu): a right press whose path, from going down to letting go, is
    // shorter than this is a click (the menu), not a drag (the camera's turn); the length moved, so
    // a drag out and back is still a drag.
    public const float RightClickSlopPixels = 4f;
    // How near the pointer must be to a path on the screen, in pixels, to be on it: a click there
    // places a node, a right click warps to that point (Flight.WarpingTo), and a press on a body's
    // line on the map is on it.
    public const float PathPixels = 8f;
    // The mark on the flown vessel's path where a warp to a point stops, while it runs.
    public static readonly (byte R, byte G, byte B) WarpToColor = (255, 225, 120);
    // A click is on the flown vessel within its radius of its axis on the screen, or within this if
    // that is smaller (a vessel far off, a pixel or two across).
    public const float ActionMenuMinPixels = 12f;

    // The engine: its flame lights up everything around it, and its exhaust stirs the smoke.
    public static readonly Vector3 FlameColor = new(1.0f, 0.62f, 0.28f);
    public const float FlameLightRange = 40f;         // falls to a quarter of its brightness here
    public const float PlumeLength = 26f;             // at full throttle
    public const float JetReach = 45f;                // the exhaust stirs smoke this far below the nozzle

    // Smoke: a low haze drifting across each pad, which billows up thick around the booster while
    // its engine fires close to the ground and slowly thins again afterwards.
    public const int SmokePuffCount = 60;
    public const float SmokeExtent = 110f;            // puffs drift across +/- this along the wind, then wrap round
    public const float SmokeSpread = 85f;             // and are scattered across +/- this either side of it
    public const float SmokeRadiusMin = 9f, SmokeRadiusMax = 18f; // half-width of each puff
    public const float SmokeSquash = 0.7f;            // height as a fraction of width: wide banks
    public const float SmokeHeight = 14f;             // fades out by this far above the ground
    public const float SmokeDrawDistance = 4000f;     // a pad's or zone's smoke field is drawn while the camera is within this of it
    public const float SmokeOpacity = 0.85f;          // densest a single puff gets
    public const float SmokeHaze = 0.12f;             // how thick it is with the engine off: a faint ground haze
    public const float SmokeBuildSeconds = 0.6f;      // how fast the engine thickens it, close to the ground
    public const float SmokeCloudRadius = 55f;        // and how far round the pad that cloud reaches
    public const float SmokeSiteReach = 90f;          // the engine stirs up a pad's cloud when its exhaust hits within this of it
    public const float SmokeClearSeconds = 25f;       // how slowly it thins out again
    public static readonly Vector2 SmokeWind = new(1.4f, 0.5f); // drift, metres per second, across the ground (x, z)
    public static readonly Vector3 SmokeColor = new(0.80f, 0.80f, 0.82f);
    public const float SmokeLitShare = 0.45f;         // the share of the sun's light a puff gives back but for its forward glow (into the sun, more), and of the moon's (unshadowed, with none)

    // Stirring: the booster and its exhaust push the smoke about (see SmokeStirs).
    public const float SmokeStirFullSpeed = 20f;      // moving this fast (m/s) pushes as hard as it can
    public const float SmokeStirMinSpeed = 1.5f;      // slower than this, it leaves no wake
    public const float SmokeWakeSpacing = 3f;         // a moving booster leaves an eddy each time it has gone this far
    public const float SmokeWakeSeconds = 2.5f;       // how long an eddy takes to spread out and fade
}
