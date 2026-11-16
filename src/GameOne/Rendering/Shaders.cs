using System.Numerics;

namespace GameOne.Rendering;

/// <summary>
/// GLSL for every pass, compiled to SPIR-V at start-up by ShaderC. All share one descriptor set
/// (per-frame uniforms, the logo texture, the shadow map, the terrain's heights, kinds and colours, the air's tables, the moon's map); each kind of draw has its own
/// push-constant block, within the shared 80-byte push range.
/// </summary>
/// <remarks>
/// Colours are given in display (sRGB-like) values, as picked by eye. Shaders convert them to linear
/// light with <c>toLinear</c>, light them, then tone-map and convert back with <c>finish</c>, since the
/// swapchain is UNORM (it shows values exactly as written).
/// </remarks>
internal static class Shaders
{
    /// <summary>Must match <c>VulkanRenderer.FrameUniforms</c> and <c>VulkanRenderer.DrawConstants</c>.</summary>
    private static string Common => FrameResources + """

        layout(push_constant) uniform Draw
        {
            mat4 model;
            vec4 material; // x = kind: 0 lit, 1 ground, 2 launch pad, 3 landing zone, 4 water, 5 the drone ship, 6 the floes, 7 a lamp's face; yzw = tint (multiplies the mesh colour)
        } draw;

        const float KindLit = 0.0, KindGround = 1.0, KindPad = 2.0, KindLandingZone = 3.0, KindWater = 4.0, KindDeck = 5.0, KindIce = 6.0, KindLamp = 7.0;
        """;

    /// <summary>
    /// The floes' models (<c>VulkanRenderer</c>'s floe buffer), one an instance, for the vertex
    /// shaders that draw meshes: the floes are one instanced draw (<c>KindIce</c>), each taking
    /// its model from here; every other draw takes its model from its constants. A floe's model
    /// carries its id in its first row's fourth entry (which a model, moving and turning, leaves 0:
    /// see <c>GameView</c>), for its looks; it is taken out before the model is used.
    /// <para>The model returned is placed from the camera (<c>frame.cameraPos</c>): a draw's own is
    /// pushed so (<c>VulkanRenderer.FromEye</c>), worked out on the CPU where it keeps its precision,
    /// so a vessel on the moon, tens of kilometres from the scene's origin, holds still to the
    /// millimetre on the screen; a floe's (near the planet's origin) is moved there here.</para>
    /// </summary>
    private const string MeshModel = """

        layout(std430, set = 0, binding = 5) readonly buffer FloeModels { mat4 floeModels[]; };

        mat4 meshModel(out float floe)
        {
            floe = 0.0;
            if (draw.material.x != KindIce)
                return draw.model;
            mat4 m = floeModels[gl_InstanceIndex];
            floe = m[0][3];
            m[0][3] = 0.0;
            m[3].xyz -= frame.cameraPos.xyz;
            return m;
        }
        """;

    /// <summary>The descriptor set every scene-pass shader shares. Must match <c>VulkanRenderer.FrameUniforms</c>.</summary>
    private static string FrameResources => "#version 450\n" + ShadowLayout + FrameBlock + AirLookup + AirLight;

    /// <summary>
    /// The air's tables (<c>AirTables</c>: layer 0 the sun's light through the air, layer 1 the sky's
    /// light on an upward face) and their lookups, which are <c>AirTables.Look</c>'s: change one and
    /// change the other. Both take the height above the sea and the sun's cosine from straight up.
    /// </summary>
    private static string AirLookup => $$"""

        layout(set = 0, binding = 6) uniform sampler2DArray uAir;

        // The table's texel at a column and a height, by its centre (the sampler filters between them, clamped at the edges).
        vec3 airTexel(int layer, float column, float aboveSea)
        {
            float row = clamp(aboveSea / frame.atmosphere.x, 0.0, 1.0) * float({{AirTables.Heights - 1}});
            vec2 uv = vec2((clamp(column, 0.0, float({{AirTables.SunAngles - 1}})) + 0.5) / {{AirTables.SunAngles}}.0, (row + 0.5) / {{AirTables.Heights}}.0);
            return texture(uAir, vec3(uv, float(layer))).rgb;
        }

        // How much of the sun's light gets through the air to space from aboveSea, the sun's cosine from up being sunUp:
        // 1 in each colour through no air, 0 where the sea is in the way.
        vec3 airTransmittance(float aboveSea, float sunUp)
        {
            aboveSea = max(aboveSea, 0.0);
            float r = frame.planet.w + aboveSea;
            float ratio = frame.planet.w / r;
            if (sunUp < -sqrt(max(1.0 - ratio * ratio, 0.0)))
                return vec3(0.0); // under the horizon
            const float lowColumns = {{AirTables.LowColumns - 1}}.0;
            float column;
            if (sunUp >= 0.0)
                column = lowColumns + sqrt(min(sunUp, 1.0)) * float({{AirTables.SunAngles - AirTables.LowColumns}});
            else
            {
                // Under the level the columns follow how clear the ray's closest approach is of the sea, as a part of the room under the eye (of the air's whole height at most: from above the air only the chord round
                // the closest approach is air, the same chord the top row describes).
                float room = max(aboveSea, {{AirTables.RoomFloor}}.0);
                float share = clamp(((frame.planet.w + room) * sqrt(max(1.0 - sunUp * sunUp, 0.0)) - frame.planet.w) / min(room, frame.atmosphere.x), 0.0, 1.0);
                float q = sqrt(share / max(1.0 - share, 1e-6));
                column = share >= 1.0 ? lowColumns : q / (1.0 + q) * lowColumns;
            }
            return airTexel(0, column, aboveSea);
        }

        // The sky's light on an upward face, in the same units as the sun's.
        vec3 airSkyLight(float aboveSea, float sunUp)
        {
            float column = (sign(sunUp) * sqrt(min(abs(sunUp), 1.0)) + 1.0) * 0.5 * float({{AirTables.SunAngles - 1}});
            return airTexel(1, column, max(aboveSea, 0.0));
        }
        """;

    /// <summary>
    /// The air's light, as <c>Air</c> works it out (its march, phases, ozone and spheres: change one and
    /// change the other), its coefficients written in from <c>Scene</c>: the sun's light at a place,
    /// the sky's light on a face, the sky seen from the camera and the haze between the camera and
    /// what it sees (for the smoke, at its corners). Every path to the sun is the table's (<c>airTransmittance</c>), not a march.
    /// </summary>
    private static string AirLight => $$"""

        const vec3 RAYLEIGH_SCATTER = vec3({{Exact(Scene.RayleighScatter.X)}}, {{Exact(Scene.RayleighScatter.Y)}}, {{Exact(Scene.RayleighScatter.Z)}}); // per metre at the sea
        const float MIE_SCATTER = {{Exact(Scene.MieScatter)}}, MIE_EXTINCTION = {{Exact(Scene.MieExtinction)}};
        const float MIE_SCALE_HEIGHT = {{Exact(Scene.MieScaleHeight)}}, MIE_G = {{Exact(Scene.MieG)}};
        const float SKY_EXPOSURE = {{Exact(Scene.SkyExposure)}}; // the scattered light's scale on the screen (Scene.SkyExposure)
        const float SUN_EXPOSURE = {{Exact(Scene.SunExposure)}}; // the direct sunlight's on a surface (Scene.SunExposure)
        const vec3 OZONE_ABSORPTION = vec3({{Exact(Scene.OzoneAbsorption.X)}}, {{Exact(Scene.OzoneAbsorption.Y)}}, {{Exact(Scene.OzoneAbsorption.Z)}}); // per metre at its thickest
        const float OZONE_HEIGHT = {{Exact(Scene.OzoneHeight)}}, OZONE_HALF_WIDTH = {{Exact(Scene.OzoneHalfWidth)}};
        const float NIGHT_LIGHT = {{Exact(Scene.NightLight)}};   // starlight, on every surface
        const int SKY_SAMPLES = {{Scene.SkySteps}};      // steps along a view ray into the sky (Scene.SkySteps; Air, the reference, takes more)
        const int FOG_SAMPLES = {{Scene.FogSteps}};       // and along the way to something seen through the haze
        const float AIR_STEP = {{Exact(Scene.AirStepLength)}};  // ... a step for each this much of that way, up to FOG_SAMPLES (Scene.AirStepLength)
        const float PI = 3.14159265;
        const float SUN_DISTANCE = {{Exact(Scene.SunDistance)}};    // from the planet's centre (Scene.SunDistance)
        const float SUN_RADIUS = {{Exact(Scene.SunRadius)}};        // its sphere's (Scene.SunRadius)

        // SolarSystem.DiscSeen: the share of a disc of angular radius r left uncovered by one of
        // radius cover whose centre is apart from it (radians; the discs taken as flat): 1 exactly
        // once they don't overlap, else 1 less the lens the two make over the disc's area.
        float discSeen(float apart, float r, float cover)
        {
            if (cover <= 0.0 || apart >= r + cover)
                return 1.0;
            if (apart <= cover - r)
                return 0.0; // wholly covered
            if (apart <= r - cover)
                return 1.0 - cover * cover / (r * r); // the cover wholly on the disc (a ring left)
            float aa = apart * apart, rr = r * r, cc = cover * cover;
            float a = acos(clamp((aa + rr - cc) / (2.0 * apart * r), -1.0, 1.0));
            float b = acos(clamp((aa + cc - rr) / (2.0 * apart * cover), -1.0, 1.0));
            float k = sqrt(max((r + cover - apart) * (apart + r - cover) * (apart - r + cover) * (apart + r + cover), 0.0));
            return clamp(1.0 - (rr * a + cc * b - 0.5 * k) / (PI * rr), 0.0, 1.0);
        }

        // SolarSystem.SunSeen: the share of the sun's disc the moon leaves uncovered seen from p, the
        // sun where it truly is (its direction from p) and the moon its sphere (its centre given from
        // the camera, frame.moon). Exactly 1 outside the moon's shadow, so nothing it multiplies
        // changes there. A few ALU ops and no reads; never asked per step of a march. The planet is
        // not in it (so at twilight with the moon in line beyond it the sky and haze dim too: the
        // air above is in the moon's shadow).
        float sunSeen(vec3 p)
        {
            vec3 toMoon = frame.moon.xyz - (p - frame.cameraPos.xyz);
            vec3 sun = normalize(frame.sunDir.xyz * SUN_DISTANCE - (p - frame.planet.xyz));
            float moon = asin(min(frame.moon.w / max(length(toMoon), 1e-3), 1.0));
            float apart = atan(length(cross(sun, toMoon)), dot(sun, toMoon));
            return discSeen(apart, frame.atmosphere.w, moon);
        }

        // sunSeen at the camera itself, by the sun's disc as it is drawn from there (frame.sunDisc:
        // its way and its size from the camera, worked out in double), so the eclipse's dimming and
        // the corona go by the disc the moon is seen covering, from anywhere (the map's furthest
        // zoom too). VulkanRenderer.Flare's dimming is its twin.
        float sunSeenFromCamera()
        {
            vec3 toMoon = frame.moon.xyz;
            float moon = asin(min(frame.moon.w / max(length(toMoon), 1e-3), 1.0));
            float apart = atan(length(cross(frame.sunDisc.xyz, toMoon)), dot(frame.sunDisc.xyz, toMoon));
            return discSeen(apart, frame.sunDisc.w, moon);
        }

        // The sun's light at p (linear): its own, dimmed by the air between p and it (none with the
        // sea in the way) and by the moon covering it.
        vec3 sunlightAt(vec3 p)
        {
            return toLinear(frame.sunColor.rgb) * airTransmittance(altitudeOf(p), dot(upAt(p), frame.sunDir.xyz)) * sunSeen(p);
        }

        // The sky's light falling on an upward face at p, as the radiance of a sky as bright all over
        // (the table's irradiance over pi), exposed as the sky is on the screen: none at night or above
        // the air, and dimmed with the sun covered at p (the sky above a place in the moon's shadow
        // is as dark as the sky drawn from there, skyColor).
        vec3 skyLightAt(vec3 p)
        {
            return airSkyLight(altitudeOf(p), dot(upAt(p), frame.sunDir.xyz)) * (SKY_EXPOSURE / PI) * sunSeen(p);
        }

        // The unit direction from p to the moon's centre (given from the camera, frame.moon).
        vec3 moonDirAt(vec3 p)
        {
            return normalize(frame.moon.xyz - (p - frame.cameraPos.xyz));
        }

        // SolarSystem.Moonlight: the moon's light at p (linear) on a face turned to it, its direction
        // from p toMoon (moonDirAt): what it gives back (frame.moonlight, once a frame) times the lit
        // share of its face seen from p, through the air towards it (none below the horizon). A dim
        // light with no shadow, lit as the sun is (its colour and exposure).
        vec3 moonlight(vec3 p, vec3 toMoon)
        {
            float lit = (1.0 - dot(frame.sunDir.xyz, toMoon)) * 0.5;
            vec3 shine = frame.moonlight.rgb * (lit * SUN_EXPOSURE);
            if (max(shine.r, max(shine.g, shine.b)) <= 0.0)
                return vec3(0.0); // a new moon, or one wholly in the planet's shadow: no air to read (airTransmittance skips its read under the horizon itself)
            return toLinear(frame.sunColor.rgb) * shine * airTransmittance(altitudeOf(p), dot(upAt(p), toMoon));
        }

        // How thick the haze is h above the sea: the air's own shape, with its own (lower) scale height.
        float hazeDensity(float h)
        {
            if (h >= frame.atmosphere.x)
                return 0.0;
            h = max(h, 0.0);
            return exp(-h / MIE_SCALE_HEIGHT) * (1.0 - h / frame.atmosphere.x);
        }

        // How thick the ozone is h above the sea: a tent, 1 at OZONE_HEIGHT, none OZONE_HALF_WIDTH above and below (it only absorbs).
        float ozoneDensity(float h) { return max(0.0, 1.0 - abs(h - OZONE_HEIGHT) / OZONE_HALF_WIDTH); }

        float rayleighPhase(float mu) { return 3.0 / (16.0 * PI) * (1.0 + mu * mu); }

        // Cornette-Shanks, forward-peaked by MIE_G.
        float hazePhase(float mu)
        {
            float g2 = MIE_G * MIE_G;
            return 3.0 / (8.0 * PI) * (1.0 - g2) * (1.0 + mu * mu) / ((2.0 + g2) * pow(1.0 + g2 - 2.0 * MIE_G * mu, 1.5));
        }

        // Where the ray o + d t (o from the planet's centre) meets the sphere of that radius about it (near, far), if it does.
        bool sphereHit(vec3 o, vec3 d, float radius, out float near, out float far)
        {
            // The ray's closest approach to the centre as a difference of vectors (o less its way along
            // the ray), not of squares: so the sphere's edge keeps its precision in float both near it
            // (a ray grazing the sea) and millions of metres out (the map's furthest zoom), where the
            // squares' difference would be rounded by more than the sphere's radius squared.
            float b = dot(o, d), miss = length(o - d * b);
            float disc = (radius - miss) * (radius + miss);
            near = 0.0;
            far = 0.0;
            if (disc < 0.0)
                return false;
            float root = sqrt(disc);
            near = -b - root;
            far = -b + root;
            return true;
        }

        // The camera from the planet's centre, lifted onto the sea if under it (below it there is no
        // air to speak of), and half a metre over it, so float's rounding never sees a ray up from
        // the sea as going into it.
        vec3 eyeFromCentre()
        {
            vec3 o = frame.cameraPos.xyz - frame.planet.xyz;
            float r = length(o);
            return r < frame.planet.w + 0.5 ? o * ((frame.planet.w + 0.5) / max(r, 1e-3)) : o;
        }

        // Where the ray o + d t (o from the centre) is in the planet's shadow, the sea's sphere
        // between it and the sun: the shadow is a half cylinder behind the planet, so it is one
        // stretch of the ray, from enter to leave (false if the ray misses it). A point there gets no
        // sunlight (Air's Escape gives it none, as airTransmittance does under the horizon).
        bool shadowStretch(vec3 o, vec3 d, out float enter, out float leave)
        {
            vec3 s = frame.sunDir.xyz;
            float os = dot(o, s), ds = dot(d, s);
            vec3 across = o - s * os, acrossD = d - s * ds;
            float a = dot(acrossD, acrossD), b = dot(across, acrossD), r = length(across);
            float c = (r - frame.planet.w) * (r + frame.planet.w); // (factored, as sphereHit's, for its precision)
            enter = -1e30;
            leave = 1e30;
            if (a > 1e-8)
            {
                float disc = b * b - a * c;
                if (disc <= 0.0)
                    return false;
                float root = sqrt(disc);
                enter = (-b - root) / a;
                leave = (-b + root) / a;
            }
            else if (c >= 0.0)
                return false; // along the axis, outside it
            // ... and behind the planet, not on the sun's side of it.
            if (abs(ds) > 1e-8)
            {
                float level = -os / ds;
                if (ds > 0.0)
                    leave = min(leave, level);
                else
                    enter = max(enter, level);
            }
            else if (os >= 0.0)
                return false;
            return leave > enter;
        }

        // How much the air from t = from to t = to along the ray dims light, as an optical depth (for
        // the stretches in the planet's shadow, which scatter nothing): in two even steps along the
        // ground, where the air is about as thick all along (within 1% of the exact depth over 5 km),
        // and in three squeezed towards the lower end on a way that climbs or falls through much of
        // it (looking down from orbit at night: within 6% over the whole 3 km, where two even steps
        // missed a quarter, and lights seen from up there came out too bright).
        vec3 airDepth(vec3 o, vec3 d, float from, float to)
        {
            float rise = length(o + d * to) - length(o + d * from);
            float dense = rise > 1000.0 ? 1.0 : rise < -1000.0 ? -1.0 : 0.0;
            int steps = dense == 0.0 ? 2 : 3;
            float span = to - from, n = float(steps);
            vec3 depth = vec3(0.0);
            for (int i = 0; i < steps; i++)
            {
                // Each step's middle and length, the way put at f(u) of it as in airMarch.
                float u = (float(i) + 0.5) / n, start = float(i) / n, end = start + 1.0 / n;
                float h = length(o + d * (from + span * (u + dense * (u * u - u)))) - frame.planet.w;
                float width = span * (end - start) * (1.0 + dense * (start + end - 1.0));
                depth += (RAYLEIGH_SCATTER * airDensity(h) + vec3(MIE_EXTINCTION * hazeDensity(h)) + OZONE_ABSORPTION * ozoneDensity(h)) * width;
            }
            return depth;
        }

        // The sun's light scattered towards the eye along the ray from o (from the centre) along d,
        // from t = from to t = to, in that many steps (Air.March): at each, the sun's light there,
        // scattered by the phase of the angle to the sun and dimmed on its way to the eye.
        // through: how much of what is behind it gets through. Only the stretch of the way that the
        // sun lights is marched for its light: where the way starts or ends in the planet's shadow
        // (at twilight, the eye in it and the air above lit), that part only dims (airDepth, in two
        // or three steps), so every step falls where there is light to gather; a way all in the shadow (at
        // night) is only dimmed. Each step's sample is not at its middle (as Air's are) but somewhere
        // in it that differs from pixel to pixel, by the way the ray looks, so what few steps miss
        // shows as fine grain, not bands, and on average is the same light. With dense, the steps are
        // short at the end where the air is thick (1: by the eye, as in a view from inside the air;
        // -1: at the far end, as looking down from orbit) and lengthen away from it, as the square of
        // the way along; with 0, even.
        vec3 airMarch(vec3 o, vec3 d, float from, float to, int steps, float dense, out vec3 through)
        {
            float a = from, b = to, enter, leave;
            if (shadowStretch(o, d, enter, leave) && enter < to && leave > from)
            {
                if (enter <= from && leave >= to)
                {
                    through = exp(-airDepth(o, d, from, to)); // all in the shadow
                    return vec3(0.0);
                }
                if (enter <= from)
                    a = leave; // starts in the shadow
                else if (leave >= to)
                    b = enter; // ends in it
                // (and one that crosses it in the middle, from orbit past the night side, is marched whole)
            }
            vec3 before = a > from ? airDepth(o, d, from, a) : vec3(0.0);
            vec3 after = b < to ? airDepth(o, d, b, to) : vec3(0.0);
            if (dense > 0.0 && a > from || dense < 0.0 && b < to)
                dense = 0.0; // (the thick end in the shadow: evenly)
            float span = b - a, n = float(steps);
            float offset = hash3(ivec3(floor(d * 4096.0)));
            float mu = dot(d, frame.sunDir.xyz);
            float rayleigh = rayleighPhase(mu), haze = hazePhase(mu);
            vec3 sun = toLinear(frame.sunColor.rgb);
            vec3 depth = before, light = vec3(0.0);
            for (int i = 0; i < steps; i++)
            {
                // Where the sample is: u of the way along the lit stretch, put at f(u) of it, f(x) = x
                // (even), x squared (dense 1) or 2x - x squared (dense -1), so the steps are short
                // where f is flat; then its step's length, how far into the step it is, and its weight
                // (its share of the way, span f'(u) / n).
                float u = (float(i) + offset) / n, start = float(i) / n, end = start + 1.0 / n;
                float fu = u + dense * (u * u - u);
                float t = a + span * fu;
                float width = span * (end - start) * (1.0 + dense * (start + end - 1.0));
                float weight = span / n * (1.0 + dense * (2.0 * u - 1.0));
                float into = span * (fu - (start + dense * (start * start - start)));
                vec3 p = o + d * t;
                float r = length(p);
                float h = r - frame.planet.w;
                float molecules = airDensity(h), mist = hazeDensity(h);
                vec3 extinction = RAYLEIGH_SCATTER * molecules + vec3(MIE_EXTINCTION * mist) + OZONE_ABSORPTION * ozoneDensity(h);
                if (molecules > 0.0 || mist > 0.0)
                {
                    // The eye's dimming runs to the sample, so that much of this step's own is in it.
                    vec3 sunHere = sun * airTransmittance(h, dot(p / r, frame.sunDir.xyz));
                    vec3 scattered = RAYLEIGH_SCATTER * (molecules * rayleigh) + vec3(MIE_SCATTER * mist * haze);
                    light += sunHere * exp(-(depth + extinction * into)) * scattered * weight;
                }
                depth += extinction * width;
            }
            through = exp(-(depth + after));
            return light;
        }

        // How many steps a march along this much of a ray in the air takes: one each AIR_STEP, from
        // least to most (Scene.AirStepLength: a short way needs few, as little changes along it).
        int airSteps(float way, int least, int most) { return clamp(int(ceil(way / AIR_STEP)), least, most); }

        // The sky in direction d from the camera, in linear light (Air.Sky, exposed): the sun's light
        // scattered by the air along the view, from the eye to where it leaves the air or meets the
        // sea. Blue overhead, paler at the horizon, orange and dimming as the sun sets, the air above
        // still lit at twilight; from above the air, black but for the rim. Also what the haze
        // between the eye and the ground is made of (fog), so the ground meets the sky seamlessly.
        // In that many steps. With the moon over the sun, the whole of it is dimmed by the share of
        // the sun seen at the camera (sunSeen): an approximation, as the air far off along the ray
        // may be outside the moon's shadow, but it keeps the march's steps free of it (the glow of
        // that far, lit air round the horizon is drawn by the sky itself).
        vec3 skyColor(vec3 d, int steps)
        {
            vec3 o = eyeFromCentre();
            float near, far;
            if (!sphereHit(o, d, frame.planet.w + frame.atmosphere.x, near, far) || far <= 0.0)
                return vec3(0.0); // no air this way
            float from = max(near, 0.0), to = far;
            float seaNear, seaFar;
            if (sphereHit(o, d, frame.planet.w, seaNear, seaFar) && seaFar > 0.01)
                to = min(to, max(seaNear, 0.0));
            if (to <= from)
                return vec3(0.0);
            vec3 through;
            return airMarch(o, d, from, to, steps, near <= 0.0 ? 1.0 : 0.0, through) * SKY_EXPOSURE * sunSeenFromCamera();
        }

        // The sky as the camera sees it, in SKY_SAMPLES steps.
        vec3 skyColor(vec3 d) { return skyColor(d, SKY_SAMPLES); }

        // The air between the camera and worldPos (Air.TransmittanceBetween and Air.InScatter, in one
        // shorter march): through, how much of a lit colour there gets to the eye, and light, what the
        // air scatters towards the eye on the way (exposed). A point under the sea counts the air down
        // to the water; only the part of the way inside the air is marched (looking down from orbit,
        // all its steps are in the air). Its light is dimmed by the sun seen at the camera, as the
        // sky's is (skyColor), so the haze in the moon's shadow is as dark as the sky there.
        void fogTerms(vec3 worldPos, out vec3 through, out vec3 light)
        {
            through = vec3(1.0);
            light = vec3(0.0);
            vec3 o = eyeFromCentre();
            vec3 e = worldPos - frame.planet.xyz;
            float len = length(e - o);
            if (len <= 0.0)
                return;
            vec3 d = (e - o) / len;
            float near, far;
            if (length(e) < frame.planet.w)
                len = sphereHit(o, d, frame.planet.w, near, far) ? clamp(near, 0.0, len) : 0.0;
            if (!sphereHit(o, d, frame.planet.w + frame.atmosphere.x, near, far))
                return;
            float from = max(near, 0.0), to = min(far, len);
            if (to <= from)
                return;
            if (to - from <= AIR_STEP)
            {
                // A short way (most of what is near the camera): one sample at its middle, where little
                // changes along it, without the march's search for the planet's shadow (the midpoint's
                // own sunlight has it) or its jitter.
                vec3 p = o + d * (0.5 * (from + to));
                float r = length(p), h = r - frame.planet.w, inAir = to - from, mu = dot(d, frame.sunDir.xyz);
                float molecules = airDensity(h), mist = hazeDensity(h);
                vec3 extinction = RAYLEIGH_SCATTER * molecules + vec3(MIE_EXTINCTION * mist) + OZONE_ABSORPTION * ozoneDensity(h);
                vec3 scattered = RAYLEIGH_SCATTER * (molecules * rayleighPhase(mu)) + vec3(MIE_SCATTER * mist * hazePhase(mu));
                through = exp(-extinction * inAir);
                light = toLinear(frame.sunColor.rgb) * airTransmittance(h, dot(p / r, frame.sunDir.xyz)) * exp(-extinction * (0.5 * inAir)) * scattered * (inAir * SKY_EXPOSURE) * sunSeenFromCamera();
                return;
            }
            // Its steps closer together at the lower end, where the air is thicker, on a way that climbs
            // or falls through much of it (looking down from orbit, the far end); along the ground,
            // evenly (there the air is as thick all along, and squeezing them by the eye shows as grain).
            float rise = length(o + d * to) - length(o + d * from);
            float dense = rise > 1000.0 ? 1.0 : rise < -1000.0 ? -1.0 : 0.0;
            light = airMarch(o, d, from, to, airSteps(to - from, 1, FOG_SAMPLES), dense, through) * SKY_EXPOSURE * sunSeenFromCamera();
        }

        // A lit colour seen through the air between the camera and worldPos: dimmed by the air in the
        // way, and the light it scatters towards the eye added.
        vec3 fog(vec3 rgb, vec3 worldPos)
        {
            vec3 through, light;
            fogTerms(worldPos, through, light);
            return rgb * through + light;
        }
        """;

    /// <summary>
    /// The shadow map's layout, written from the renderer's (VulkanRenderer.Shadows.cs has it): its
    /// sun areas and floodlight blocks, one each per view, and where each floodlight's tile is.
    /// </summary>
    private static string ShadowLayout => $"""
        // One sun area and one floodlight block per view (VulkanRenderer.ShadowAreas: 1 + Scene.MaxSmallViews).
        #define SHADOW_AREAS {VulkanRenderer.ShadowAreas}
        // Every floodlight tile (PadLights.MaxShadowed to a block), and each one's square of the
        // shadow map: xy its corner, zw its size, as fractions of the map (VulkanRenderer.SpotTileRect).
        #define SPOT_TILES {VulkanRenderer.SpotTiles}
        const vec4 spotTiles[SPOT_TILES] = vec4[]({string.Join(", ", Enumerable.Range(0, VulkanRenderer.SpotTiles).Select(t => Vec4(VulkanRenderer.MapFraction(VulkanRenderer.SpotTileRect(t)))))});

        """;

    private static string Vec4(Vector4 v) => $"vec4({Exact(v.X)}, {Exact(v.Y)}, {Exact(v.Z)}, {Exact(v.W)})";

    private static string FrameBlock => $$"""
        #define MAX_STIRS 96 // must match SmokeStirs.MaxStirs
        #define DISPLAY_GAMMA {{Exact(Scene.DisplayGamma)}} // Scene.DisplayGamma: display values to linear light
        layout(set = 0, binding = 0) uniform Frame
        {
            mat4 viewProj;
            mat4 invViewRays;    // a pixel's clip-space point to its view ray's direction (the view's turn alone, and the projection, inverted)
            mat4 lightViewProj;  // this view's sun area's view, for the shadow map
            vec4 sunArea;        // where that area is in the shadow map: xy its corner, zw its size (fractions of the map)
            vec4 sunDir;         // xyz towards the sun; w = how far to nudge shadow lookups off a surface (a texel and a half of its area)
            vec4 sunColor;       // the sun's light before any air (a display colour: Scene.SunColor); the air dims it at each place (sunlightAt)
            vec4 cameraPos;      // world space; w = time in seconds (kept small)
            vec4 pointLightPos;  // the engine flame (or an explosion); w = range
            vec4 pointLight;     // rgb = colour times brightness (black when there is none)
            vec4 pad;            // x = launch pad half size, y = top, z = landing target radius, w = landing zone radius
            vec4 siteCount;      // x = how many of padSites[] are in use, y = of zoneSites[] (8 at most each: VulkanRenderer.MaxSites)
            vec4 padSites[8];    // xyz = each launch pad's centre, on its ground (LaunchSites.All)
            vec4 padEasts[8];    // xyz = each launch pad's east, as its frame has it (worked out once, in C#)
            vec4 padNorths[8];   // xyz = and its north
            vec4 zoneSites[8];   // xyz = each landing zone's centre, but the test pads' (Foundations.LandingZonePlinth)
            vec4 testRangeStart; // xyz = the ground under the first test pad
            vec4 testRangeEnd;   // xyz = and under the last
            vec4 planet;         // xyz = its centre, w = the sea's radius (every height is above the sea)
            vec4 atmosphere;     // x = how high it reaches, y = the height over which the air thins by a factor of e; z = how far the stars have turned (radians about the north pole), w = the sun's disc's angular radius (radians)
            vec4 moon;           // xyz = the moon's centre from this view's camera (so it keeps its precision at any distance), w = its radius
            mat4 moonFrame;      // the turn from the ground's frame into the moon's own axes, its map's (rotation only, the same in every view)
            vec4 moonlight;      // rgb = what the moon gives back of the sunlight, before the air, dimmed in a lunar eclipse (SolarSystem.MoonShine, the same in every view); w = 1 where this view draws the moon as a mesh (within Scene.MoonMeshReach), so the sky leaves it out
            vec4 sunDisc;        // xyz = the way to the sun's centre from this view's camera (worked out in double: far out it differs from sunDir, the way from the planet), w = its disc's angular radius from there
            vec4 terrain;        // x = the map's resolution, y = its lowest height, z = its highest, w = the scale the detail's and waves' noise is laid out on (Scene.DetailScale, as in C#)
            vec4 cameraClip;     // the camera itself in clip space, for points given from the camera: viewProj * (p - camera, 0) + this
            vec4 wavePhases[2];  // the sea's waves' phases at the world's time (Sea.Phases), four to a vec4, in Sea.Waves' order
            vec4 stirCount;      // x = how many of stirs[] are in use
            // Where the booster is stirring the smoke, two vec4s each (see SmokeStirs):
            // [2i] = xyz centre, w = radius; [2i + 1] = xyz drag, w = strength (its sign: which way it swirls).
            vec4 stirs[2 * MAX_STIRS];
            // The lights of the pads, zones and drone ships nearest this view (its LightSet, at most PadLights.MaxLights):
            vec4 lightCount;     // x = how many are in use, y = how many of the first cast shadows, z = the first one's tile (its spotViewProj)
            vec4 lightPlaces[32];  // xyz = where, w = range
            vec4 lightColours[32]; // rgb = colour times brightness, w = the cosine its beam fades out at (below -1: all round)
            vec4 lightAims[32];    // xyz = its beam's way, w = the cosine within which it is full
            mat4 spotViewProj[SPOT_TILES]; // every floodlight tile's view (PadLights.MaxShadowed in each of SHADOW_AREAS blocks), for their shadows
        } frame;
        layout(set = 0, binding = 1) uniform sampler2D uLogo;
        layout(set = 0, binding = 2) uniform sampler2DShadow uShadow;

        vec3 toLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3(DISPLAY_GAMMA)); }

        // Filmic tone mapping (an approximation of ACES), then back to display values.
        vec3 finish(vec3 c)
        {
            c *= 0.8;
            c = clamp((c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14), 0.0, 1.0);
            return pow(c, vec3(1.0 / DISPLAY_GAMMA));
        }

        float altitudeOf(vec3 p) { return length(p - frame.planet.xyz) - frame.planet.w; }
        vec3 upAt(vec3 p) { return normalize(p - frame.planet.xyz); }

        // How thick the air is h above the sea: 1 at the sea, thinning, and none above the atmosphere.
        float airDensity(float h)
        {
            if (h >= frame.atmosphere.x)
                return 0.0;
            h = max(h, 0.0);
            return exp(-h / frame.atmosphere.y) * (1.0 - h / frame.atmosphere.x);
        }

        // How much of the sun reaches p: none where the planet is in the way, with a soft edge
        // at the line between day and night. (Near the ground the air fades the sunlight, sunlightAt,
        // to nothing at the night line, but higher up a ray grazing the sea still gets through and the table
        // drops it to zero in one step, so the smoothstep here gives the edge there. It also serves the shadow
        // map's lookups beyond the map's edge, where only the planet can be in the way.) The planet's shadow is the sea's sphere: the land
        // standing above it (Home's ground 35 m up, the peaks far more) casts none, which moves the
        // night line by less than its 80 m soft edge at Home's height, and is small elsewhere (a
        // high peak near the night line isn't counted as blocking the sun).
        float planetShadow(vec3 p)
        {
            vec3 toCentre = frame.planet.xyz - p;
            float along = dot(toCentre, frame.sunDir.xyz);
            if (along <= 0.0)
                return 1.0; // the planet is behind p, as seen from the sun
            float miss = length(toCentre - frame.sunDir.xyz * along);
            return smoothstep(frame.planet.w - 40.0, frame.planet.w + 40.0, miss);
        }

        // The engine flame's light reaching worldPos (facing n), with no shadow: it falls to a
        // quarter of its brightness at its range.
        vec3 pointLighting(vec3 worldPos, vec3 n)
        {
            vec3 toLight = frame.pointLightPos.xyz - worldPos;
            float d = length(toLight);
            float falloff = 1.0 / (1.0 + 3.0 * pow(d / frame.pointLightPos.w, 2.0));
            return frame.pointLight.rgb * falloff * max(dot(n, toLight / max(d, 0.001)), 0.0);
        }

        // How much of floodlight i's light (of this view's) reaches worldPos past what stands in its
        // way, from its tile of the shadow map: tile t (lightCount.z + i), whose square is
        // spotTiles[t] (VulkanRenderer.Shadows.cs has the layout).
        float spotVisibility(int i, vec3 worldPos, vec3 n)
        {
            int t = int(frame.lightCount.z + 0.5) + i;
            vec4 clip = frame.spotViewProj[t] * vec4(worldPos + n * 0.08, 1.0);
            if (clip.w <= 0.0) return 1.0;
            vec3 ndc = clip.xyz / clip.w;
            vec2 uv = ndc.xy * 0.5 + 0.5;
            if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))) || ndc.z > 1.0)
                return 1.0;
            vec2 tile = spotTiles[t].xy, size = spotTiles[t].zw;
            // Kept a texel inside its tile (a gutter wider than the filter's half texel), so near
            // its edge it never reads its neighbour's.
            vec2 texel = 1.0 / vec2(textureSize(uShadow, 0));
            uv = clamp(tile + uv * size, tile + texel, tile + size - texel);
            // One filtered comparison (the hardware blends four texels): several lights' soft-ish
            // shadows overlap here, so it needs less than the sun's nine.
            return texture(uShadow, vec3(uv, ndc.z));
        }

        // The pads', zones' and drone ships' lamps reaching worldPos (facing n): each fades out over
        // its range, and a floodlight's within its beam (a cone along its aim); the floodlights of
        // the place this view's lights are chosen by cast shadows (spotVisibility).
        vec3 padLighting(vec3 worldPos, vec3 n)
        {
            vec3 sum = vec3(0.0);
            for (int i = 0; i < int(frame.lightCount.x); i++)
            {
                vec3 toLight = frame.lightPlaces[i].xyz - worldPos;
                float d = length(toLight);
                float range = frame.lightPlaces[i].w;
                if (d >= range) continue;
                vec3 l = toLight / max(d, 0.001);
                float beam = smoothstep(frame.lightColours[i].w, frame.lightAims[i].w, dot(-l, frame.lightAims[i].xyz));
                if (beam <= 0.0) continue;
                float near = 1.0 - d / range;
                float facing = max(dot(n, l), 0.0);
                if (facing <= 0.0) continue;
                // That place's floodlights (the first lightCount.y) cast shadows.
                float visible = i < int(frame.lightCount.y) ? spotVisibility(i, worldPos, n) : 1.0;
                sum += toLinear(frame.lightColours[i].rgb) * beam * near * near * facing * visible;
            }
            return sum;
        }

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

        float noise(vec2 p)
        {
            vec2 i = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
        }

        // Value noise in 3D, for the planet's surface, hashed with integers so it stays sharp
        // however far from the origin it is sampled.
        float hash3(ivec3 c)
        {
            uint h = uint(c.x) * 0x8da6b343u ^ uint(c.y) * 0xd8163841u ^ uint(c.z) * 0xcb1ab31fu;
            h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12; h *= 0x297a2d39u; h ^= h >> 15;
            return float(h) / 4294967295.0;
        }

        float noise3(vec3 p)
        {
            ivec3 i = ivec3(floor(p));
            vec3 f = fract(p), u = f * f * (3.0 - 2.0 * f);
            float a = mix(hash3(i), hash3(i + ivec3(1, 0, 0)), u.x);
            float b = mix(hash3(i + ivec3(0, 1, 0)), hash3(i + ivec3(1, 1, 0)), u.x);
            float c = mix(hash3(i + ivec3(0, 0, 1)), hash3(i + ivec3(1, 0, 1)), u.x);
            float e = mix(hash3(i + ivec3(0, 1, 1)), hash3(i + ivec3(1, 1, 1)), u.x);
            return mix(mix(a, b, u.y), mix(c, e, u.y), u.z);
        }

        float fbm3(vec3 p)
        {
            float v = 0.0, a = 0.5;
            for (int i = 0; i < 4; i++)
            {
                v += a * noise3(p);
                p = p * 2.03 + vec3(17.1, 9.4, 3.7);
                a *= 0.5;
            }
            return v;
        }

        float fbm(vec2 p)
        {
            float v = 0.0, a = 0.5;
            for (int i = 0; i < 4; i++)
            {
                v += a * noise(p);
                // Turned a little each octave, so the noise's square grid doesn't show.
                p = mat2(0.8, 0.6, -0.6, 0.8) * p * 2.03 + vec2(17.1, 9.4);
                a *= 0.5;
            }
            return v;
        }
        """;

    /// <summary>The shadow-map lookup, for the fragment shaders that are lit by the sun.</summary>
    private const string ShadowLookup = """

        // 1 = fully lit, 0 = fully in shadow. Looks the point up in the shadow map (depth as seen
        // from the sun) with a 3x3 grid of hardware-filtered comparisons, for soft edges: what is
        // drawn into it (the vessels, the pads, the tower) alone, 1 outside it.
        float shadowMapVisibility(vec3 worldPos, vec3 n)
        {
            // Nudging the lookup point off the surface stops it shadowing itself ("shadow acne").
            vec4 clip = frame.lightViewProj * vec4(worldPos + n * frame.sunDir.w, 1.0);
            vec3 ndc = clip.xyz / clip.w;
            vec2 uv = ndc.xy * 0.5 + 0.5;
            if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))) || ndc.z > 1.0)
                return 1.0; // outside the shadow map
            // This view's sun area (the main view's, its own, or one it shares: VulkanRenderer.Shadows.cs).
            vec2 area = frame.sunArea.xy, size = frame.sunArea.zw;
            uv = area + uv * size;

            vec2 texel = 1.0 / vec2(textureSize(uShadow, 0));
            // Each lookup kept a texel inside the sun's area (a gutter wider than the filter's half
            // texel): at its edges the grid's would otherwise read the areas and blocks beside it.
            vec2 lo = area + texel, hi = area + size - texel;
            float lit = 0.0;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                    lit += texture(uShadow, vec3(clamp(uv + vec2(x, y) * texel * 1.5, lo, hi), ndc.z));
            return lit / 9.0;
        }

        // The shadow map's, and the planet's in the way (its shadow: outside the map only the planet can be).
        float lightVisibility(vec3 worldPos, vec3 n)
        {
            return shadowMapVisibility(worldPos, n) * planetShadow(worldPos);
        }
        """;

    public static string SceneVertex => Common + MeshModel + """

        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aColor;
        layout(location = 4) in float aTextured;

        layout(location = 0) out vec3 vWorldPos;
        layout(location = 1) out vec3 vNormal;
        layout(location = 2) out vec2 vUv;
        layout(location = 3) out vec4 vColor;
        layout(location = 4) out float vTextured;
        layout(location = 5) flat out int vFace;   // the terrain's: see PlanetVertex
        layout(location = 6) out vec4 vTerrain;
        layout(location = 7) out float vWaterline; // the terrain's

        void main()
        {
            vFace = 0;
            vTerrain = vec4(0.0);
            vWaterline = 0.0;
            float floe;
            mat4 model = meshModel(floe); // placed from the camera: see meshModel
            vec3 fromCamera = (model * vec4(aPosition, 1.0)).xyz;
            vec4 world = vec4(frame.cameraPos.xyz + fromCamera, 1.0);
            gl_Position = frame.viewProj * vec4(fromCamera, 0.0) + frame.cameraClip;
            if (draw.material.x == KindIce)
            {
                // A floe's top stands only a hand's breadth over the waves. Away from the camera
                // the sea is drawn coarser (its short waves faded, its squares wider), a little
                // off the waves the floe rides, and it would show through the floe, so the floe
                // is pulled towards the camera in depth only (along the line of sight, so it
                // stays where it is on the screen): by up to 0.6 m, all of it from 250 m off.
                // Close by, where the sea is drawn whole, hardly at all.
                float d = max(length(fromCamera), 1.0);
                float pull = 0.6 * clamp(d / 250.0, 0.0, 1.0);
                gl_Position = frame.viewProj * vec4(fromCamera * (1.0 - pull / d), 0.0) + frame.cameraClip;
            }
            vWorldPos = world.xyz;
            // Rotation only, or (a floe's) a scale that keeps each normal straight out of its face,
            // so no inverse-transpose is needed: the fragment shader normalises it.
            vNormal = mat3(model) * aNormal;
            vUv = aUv;
            vColor = aColor;
            vTextured = aTextured;
            if (draw.material.x == KindIce)
            {
                // Each floe its own ice, from a hash of its id: a little brighter or duller, and
                // about one in four bare, greyer and bluer, not snow-covered. Never brighter than
                // the sheet. And how far in from its rim each point is, in metres (its mesh's UV is
                // where it is across the unit disc), for the wet band round its waterline.
                int id = int(floe);
                float bright = hash3(ivec3(id, 17, 3)), bare = step(0.74, hash3(ivec3(id, 5, 29))), blue = hash3(ivec3(id, 41, 11));
                vColor.rgb *= mix(vec3(1.0), vec3(0.80, 0.88, 0.97), bare * (0.6 + 0.4 * blue)) * (0.82 + 0.16 * bright);
                vUv = vec2(length(model[0].xyz) * (1.0 - length(aUv)), 0.0);
            }
        }
        """;

    /// <summary>
    /// The terrain's maps and its one height function, as <see cref="GameOne.Terrain"/> has it, for
    /// the planet's vertex and fragment shaders: the cube's faces, the bilinear read between texel
    /// centres (with <c>texelFetch</c>, the same arithmetic as <see cref="TerrainMap.Texels"/>, never
    /// the hardware's filtering) and the fine detail (the same hash, noise and per-kind octaves, the
    /// octaves written in from <see cref="Scene.TerrainDetail"/>). Written as the C# is, it gives
    /// the same heights to within a driver's fused multiply-adds (well under a millimetre). Also each kind's colour, and the
    /// sea as <see cref="Sea"/> has it: its waves (written in from <see cref="Sea.Waves"/>, to the
    /// last bit, so the sines' arguments are the C#'s), and each sea's wave size and colours (from
    /// <paramref name="map"/>).
    /// </summary>
    private static string TerrainCommon(TerrainMap map)
    {
        var octaves = new List<string>();
        var starts = new List<int>();
        foreach (var kind in Scene.TerrainDetail)
        {
            starts.Add(octaves.Count);
            octaves.AddRange(kind.Select(o => $"vec2({Glsl(o.Amplitude)}, {Glsl(o.Wavelength)})"));
        }
        starts.Add(octaves.Count);

        // Each wave's wavenumber worked out as Sea.Height does, in floats, so it is the same number.
        var directions = Sea.Waves.Select(w => $"vec3({Exact(w.Direction.X)}, {Exact(w.Direction.Y)}, {Exact(w.Direction.Z)})");
        var shapes = Sea.Waves.Select(w => $"vec3({Exact(w.Wavelength)}, {Exact(2f * MathF.PI / w.Wavelength)}, {Exact(w.Share)})");
        // Region 0 is land: water there (ground below sea level inland) has no waves. (The seas' colours are SeaColors'.)
        float[] waves = [0f, .. map.Seas.Select(s => s.Waves)];

        return $"""

            #define PATCH_GRID {Scene.PlanetPatchGrid}
            #define TERRAIN_FINEST_SPACING {Glsl(Scene.TerrainFinestSpacing)}
            #define KIND_COUNT {Scene.TerrainDetail.Length}
            const vec2 octaves[{octaves.Count}] = vec2[]({string.Join(", ", octaves)}); // (amplitude, wavelength), each kind's in turn
            const int octaveStart[KIND_COUNT + 1] = int[]({string.Join(", ", starts)});

            #define WAVE_COUNT {Sea.WaveCount}
            #define TWO_PI {Exact(2f * MathF.PI)}
            #define SEA_WAVE_FULL_DEPTH {Exact(Scene.SeaWaveFullDepth)}
            #define SEA_REGIONS {waves.Length}
            #define KIND_FLOE_FIELD {(int)SurfaceKind.FloeField}u
            #define KIND_PACK_ICE {(int)SurfaceKind.PackIce}
            #define FLOE_WAVE_DAMPING {Exact(Scene.FloeWaveDamping)}
            // The waterline far off (Waterline): a texel's metres, the heights' clamp and the seabed's push near.
            #define WATERLINE_TEXEL {Exact(Waterline.TexelMetres(map.Resolution))}
            #define WATERLINE_REACH {Exact(Waterline.Reach)}
            #define WATERLINE_NEAR_PUSH {Exact(Waterline.NearPushDepth)}
            const vec3 waveDirections[WAVE_COUNT] = vec3[]({string.Join(", ", directions)});
            const vec3 waveShapes[WAVE_COUNT] = vec3[]({string.Join(", ", shapes)}); // (wavelength, wavenumber, share)
            // Per region (0 is land, then each of the map's seas): its wave size, crest to trough.
            const float seaWaves[SEA_REGIONS] = float[]({string.Join(", ", waves.Select(Exact))});
            // Each SurfaceKind's colour, in its order (Scene.KindColors).
            const vec3 kindColors[KIND_COUNT] = vec3[]({string.Join(", ", Scene.KindColors.Select(c => $"vec3({Glsl(c.X)}, {Glsl(c.Y)}, {Glsl(c.Z)})"))});
            """ + TerrainFunctions;
    }

    /// <summary>A float as a GLSL literal.</summary>
    private static string Glsl(float value) => value.ToString("0.0#######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A float as a GLSL literal that reads back as exactly the same float (the shortest that
    /// does), for what must match the C# to the last bit.
    /// </summary>
    private static string Exact(float value)
    {
        var text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    /// <summary>The cube's faces as <see cref="CubeSphere"/> lays them out, for the planet's map and the moon's alike.</summary>
    private const string CubeFaces = """

        // Each face's outward direction, and its across and up directions (across x up = out): CubeSphere's.
        const vec3 faceNormals[6] = vec3[](vec3(1, 0, 0), vec3(-1, 0, 0), vec3(0, 1, 0), vec3(0, -1, 0), vec3(0, 0, 1), vec3(0, 0, -1));
        const vec3 faceAcrosses[6] = vec3[](vec3(0, 0, -1), vec3(0, 0, 1), vec3(1, 0, 0), vec3(1, 0, 0), vec3(1, 0, 0), vec3(-1, 0, 0));
        const vec3 faceUps[6] = vec3[](vec3(0, 1, 0), vec3(0, 1, 0), vec3(0, 0, -1), vec3(0, 0, 1), vec3(0, 1, 0), vec3(0, 1, 0));
        """;

    /// <summary><see cref="Terrain.Noise"/> and its hash, for the planet's fine detail and the moon's alike.</summary>
    private const string NoiseFunctions = """

        // Terrain.Lattice: the uint multiplies wrap as C#'s unchecked ones do.
        float lattice(ivec3 c)
        {
            uint x = uint(c.x) * 73856093u ^ uint(c.y) * 19349663u ^ uint(c.z) * 83492791u;
            x ^= x >> 13;
            x *= 0x5bd1e995u;
            x ^= x >> 15;
            return float(x) / 4294967296.0;
        }

        float quintic(float t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }
        float lerp(float a, float b, float t) { return a + (b - a) * t; }

        // Terrain.Noise: value noise, trilinear between the lattice's corners, quintic-smoothed, in [-1, 1].
        float valueNoise(vec3 q)
        {
            vec3 f = floor(q);
            ivec3 i = ivec3(f);
            float sx = quintic(q.x - f.x), sy = quintic(q.y - f.y), sz = quintic(q.z - f.z);
            float x00 = lerp(lattice(i), lattice(i + ivec3(1, 0, 0)), sx);
            float x10 = lerp(lattice(i + ivec3(0, 1, 0)), lattice(i + ivec3(1, 1, 0)), sx);
            float x01 = lerp(lattice(i + ivec3(0, 0, 1)), lattice(i + ivec3(1, 0, 1)), sx);
            float x11 = lerp(lattice(i + ivec3(0, 1, 1)), lattice(i + ivec3(1, 1, 1)), sx);
            return lerp(lerp(x00, x10, sy), lerp(x01, x11, sy), sz) * 2.0 - 1.0;
        }
        """;

    private const string TerrainFunctions = CubeFaces + """

        layout(set = 0, binding = 3) uniform sampler2DArray uHeights; // R16_UNORM, a layer per face: stored height / 65535
        layout(set = 0, binding = 4) uniform usampler2DArray uKinds;  // r = SurfaceKind, g = region
        // The kinds' colours (GroundColors: RGBA8 display values, a layer per face, the faces' insides
        // without their aprons, with a mip chain averaged in linear light), filtered trilinearly.
        layout(set = 0, binding = 10) uniform sampler2DArray uGroundColors;
        // The heights clamped about the sea level (Waterline: R16_SFLOAT metres, a layer per face, the
        // faces' insides, with a mip chain), filtered trilinearly, for where the water is drawn far off.
        layout(set = 0, binding = 11) uniform sampler2DArray uWaterline;
        // The seas' shallow and deep colours, blended across their borders (SeaColors: RGBA16F
        // display values, the faces' shallow colours in layers 0 to 5, their deep ones in 6 to 11,
        // with a mip chain averaged in linear light), filtered trilinearly.
        layout(set = 0, binding = 12) uniform sampler2DArray uSeaColors;
        const vec3 slopeRock = vec3(0.34, 0.31, 0.28);

        // The direction from the planet's centre at a point on a face (equal-angle, as CubeSphere.FromFace).
        vec3 faceDirection(int face, vec2 uv)
        {
            vec2 t = tan(uv * 0.7853982);
            return normalize(faceNormals[face] + faceAcrosses[face] * t.x + faceUps[face] * t.y);
        }

        const ivec2 texelSteps[4] = ivec2[](ivec2(0, 0), ivec2(1, 0), ivec2(0, 1), ivec2(1, 1));

        // The four texels a bilinear read at uv uses, as TerrainMap.Texels: the first one, and the
        // weights of it and those +u, +v and +u+v from it. Texel t's centre is at ((t - 0.5) / N) * 2 - 1.
        void terrainTexels(vec2 uv, out ivec2 first, out vec4 weight)
        {
            float n = frame.terrain.x;
            vec2 t = clamp((uv + 1.0) * 0.5 * n + 0.5, 0.0, n + 1.0);
            first = min(ivec2(t), ivec2(int(n)));
            vec2 f = t - vec2(first);
            weight = vec4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
        }

        // A stored height in metres, as TerrainMap.Metres.
        float storedHeight(ivec3 at)
        {
            float h = round(texelFetch(uHeights, at, 0).r * 65535.0);
            return frame.terrain.y + (frame.terrain.z - frame.terrain.y) * h / 65535.0;
        }
        """ + NoiseFunctions + """

        // One kind's octaves at p (metres from the centre). An octave too fine for the squares
        // drawn here (spacing metres apart) fades out, so it never shimmers: all of it at 4 squares
        // a wavelength, none at 2. Near the camera every octave is drawn, so the ground drawn is
        // the ground collided with.
        float kindDetail(uint kind, vec3 p, float spacing)
        {
            float sum = 0.0;
            for (int o = octaveStart[kind]; o < octaveStart[kind + 1]; o++)
            {
                float fade = clamp(octaves[o].y / spacing * 0.5 - 1.0, 0.0, 1.0);
                if (fade > 0.0)
                    sum += fade * octaves[o].x * valueNoise(p / octaves[o].y);
            }
            return sum;
        }

        // Terrain.Height: the map's height plus the fine detail, at uv on a face (dir is its
        // direction), the detail faded for squares spacing metres apart.
        float terrainHeight(int face, vec2 uv, vec3 dir, float spacing)
        {
            ivec2 first;
            vec4 weight;
            terrainTexels(uv, first, weight);
            float h = 0.0;
            uint kinds[4];
            for (int t = 0; t < 4; t++)
            {
                ivec3 at = ivec3(first + texelSteps[t], face);
                h += storedHeight(at) * weight[t];
                kinds[t] = texelFetch(uKinds, at, 0).r;
            }

            // Each kind once, with the weight of all its texels, as Terrain.Detail adds them.
            vec3 p = dir * frame.terrain.w;
            float detail = 0.0;
            for (int t = 0; t < 4; t++)
            {
                bool seen = false;
                for (int s = 0; s < t; s++)
                    seen = seen || kinds[s] == kinds[t];
                float w = 0.0;
                for (int s = t; s < 4; s++)
                    w += kinds[s] == kinds[t] ? weight[s] : 0.0;
                if (!seen && w > 0.0)
                    detail += w * kindDetail(kinds[t], p, spacing);
            }
            return h + detail;
        }

        // Waterline.Level: the waterline's level for corners spacing metres apart, its texels half a
        // square; 0 or below, the corners read the heights themselves.
        float waterlineLevel(float spacing) { return log2(spacing / WATERLINE_TEXEL) - 1.0; }

        // Sea.Wrap: an angle brought into [-pi, pi], where the GPU's sine is accurate.
        float seaWrap(float angle) { return angle - TWO_PI * floor(angle / TWO_PI + 0.5); }

        // Sea.WaveScale: the sea's wave size at uv on a face (dir is its direction), blended over
        // the four texels as the heights are, land counting 0 and the floes damping theirs, then
        // faded out as the water shallows. The depth is the ground's with all its detail, as the
        // C# reads it, whatever the squares' spacing here, so the waves die away at the same shore.
        float seaWaveScale(int face, vec2 uv, vec3 dir)
        {
            ivec2 first;
            vec4 weight;
            terrainTexels(uv, first, weight);
            float scale = 0.0;
            for (int t = 0; t < 4; t++)
            {
                uvec4 texel = texelFetch(uKinds, ivec3(first + texelSteps[t], face), 0);
                if (texel.g != 0u)
                    scale += seaWaves[min(int(texel.g), SEA_REGIONS - 1)] * weight[t] * (texel.r == KIND_FLOE_FIELD ? FLOE_WAVE_DAMPING : 1.0);
            }

            if (scale <= 0.0)
                return 0.0;
            float depth = -terrainHeight(face, uv, dir, TERRAIN_FINEST_SPACING);
            float f = clamp(depth / SEA_WAVE_FULL_DEPTH, 0.0, 1.0);
            return scale * f * f * (3.0 - 2.0 * f);
        }

        // How much of wave i is drawn on squares (or pixels) spacing metres apart: as the ground's
        // octaves, all of it at 4 a wavelength, none at 2, so it never shimmers. Near the camera
        // every wave is drawn whole.
        float waveFade(int i, float spacing) { return clamp(waveShapes[i].x / spacing * 0.5 - 1.0, 0.0, 1.0); }

        // Sea.Height, term for term: the sea's surface (an elevation) in dir, a unit vector, where
        // its wave size is scale (seaWaveScale), each wave faded for the spacing (waveFade).
        float seaHeight(vec3 dir, float scale, float spacing)
        {
            if (scale <= 0.0)
                return 0.0;
            vec3 p = dir * frame.terrain.w;
            float sum = 0.0;
            for (int i = 0; i < WAVE_COUNT; i++)
            {
                float fade = waveFade(i, spacing);
                if (fade <= 0.0)
                    continue;
                vec3 d = waveDirections[i];
                float along = dot(d, dir);
                float tangential = sqrt(max(1.0 - along * along, 0.0));
                float k = waveShapes[i].y;
                sum += fade * waveShapes[i].z * tangential * sin(seaWrap(k * dot(d, p) - frame.wavePhases[i / 4][i % 4]));
            }
            return scale * 0.5 * sum;
        }

        // Straight out of the sea's surface in dir (as seaHeight): up, tipped against the slope,
        // the analytic derivative of each wave's height along the ground (its wavenumber times its
        // cosine, the way it runs across the ground). The slow changes, of a wave's weight round
        // the planet's curve and of the wave size from texel to texel, are left out: beside the
        // waves' own slopes they are tiny.
        vec3 seaNormal(vec3 dir, float scale, float spacing)
        {
            if (scale <= 0.0)
                return dir;
            vec3 p = dir * frame.terrain.w;
            vec3 slope = vec3(0.0);
            for (int i = 0; i < WAVE_COUNT; i++)
            {
                float fade = waveFade(i, spacing);
                if (fade <= 0.0)
                    continue;
                vec3 d = waveDirections[i];
                float along = dot(d, dir);
                float tangential = sqrt(max(1.0 - along * along, 0.0));
                float k = waveShapes[i].y;
                slope += (d - along * dir) * (fade * waveShapes[i].z * tangential * k * cos(seaWrap(k * dot(d, p) - frame.wavePhases[i / 4][i % 4])));
            }
            return normalize(dir - scale * 0.5 * slope);
        }

        // The kinds' colours at uv on a face (p is there, metres from the centre), blended by the
        // four texels' weights. Read a texel or two off, by noise, so the edge between two kinds
        // wanders rather than following the texels' grid in steps.
        vec3 kindColor(int face, vec2 uv, vec3 p)
        {
            vec2 wander = vec2(valueNoise(p / 40.0), valueNoise(p / 40.0 + vec3(31.7, 11.3, 5.1)))
                + 0.5 * vec2(valueNoise(p / 13.0 + vec3(7.9, 3.3, 17.5)), valueNoise(p / 13.0 + vec3(2.1, 23.9, 9.7)));
            ivec2 first;
            vec4 weight;
            terrainTexels(uv + wander * (2.0 / frame.terrain.x), first, weight);
            vec3 c = vec3(0.0);
            for (int t = 0; t < 4; t++)
                c += kindColors[texelFetch(uKinds, ivec3(first + texelSteps[t], face), 0).r] * weight[t];
            return c;
        }

        // The kinds' colours at uv on a face averaged over what a pixel there covers: one filtered
        // read of the colour map (GroundColors), whose texel i has its centre at the map's texel
        // i + 1, so uv in [-1, 1] runs over [0, 1]. Read where the derivatives are uniform.
        vec3 groundColors(int face, vec2 uv)
        {
            return texture(uGroundColors, vec3((uv + 1.0) * 0.5, float(face))).rgb;
        }
        """;

    /// <summary>
    /// The terrain, one patch (<see cref="PlanetPatch"/>) a draw: a grid of
    /// <see cref="Scene.PlanetPatchGrid"/> squares each way over its square of a cube face, each
    /// vertex pushed out to the ground's height (or, for the water, the sea's), and a skirt round
    /// its edge hanging below it, which hides the cracks where it meets a patch of another size.
    /// Built from gl_VertexIndex, so it needs no vertex buffer: the indices
    /// (<see cref="PatchIndices"/>) number the grid's (N+1)^2 points row by row, then the skirt's
    /// lower edge, one point for each of the 4N round the patch's edge.
    /// </summary>
    /// <remarks>
    /// The draw's model matrix carries the patch: row 0 = (face, u0, v0, size), row 1 = (the skirt's
    /// depth in metres, the view's level-of-detail ratio, 0, 0). Positions are worked out from the
    /// camera (the planet's centre as seen from it, plus the way out to the ground), so no large
    /// world coordinates are subtracted in the projection, and the ground keeps still as it moves.
    /// Shaded by <see cref="SceneFragment"/> as ground or water.
    /// </remarks>
    public static string PlanetVertex(TerrainMap map) => Common + TerrainCommon(map) + PlanetVertexBody;

    private const string PlanetVertexBody = """

        layout(location = 0) out vec3 vWorldPos;
        layout(location = 1) out vec3 vNormal;
        layout(location = 2) out vec2 vUv;
        layout(location = 3) out vec4 vColor;
        layout(location = 4) out float vTextured;
        layout(location = 5) flat out int vFace;
        layout(location = 6) out vec4 vTerrain; // xy = where on the face, z = sea level less the ground's height, w = the sea's wave size (water)
        layout(location = 7) out float vWaterline; // sea level less the ground's height as the water is drawn by (above 0, water)

        // The ground's point at uv, from the planet's centre.
        vec3 groundPoint(int face, vec2 uv, float spacing)
        {
            vec3 dir = faceDirection(face, uv);
            return dir * (frame.planet.w + terrainHeight(face, uv, dir, spacing));
        }

        // How far the seabed is pushed back, in depth-buffer units (the depth is reversed: the near
        // plane's distance over its own), taken off its depth: as far back as the square of its
        // distance over the near plane's times this, some 2 cm at 100 m, 2 m at 1 km and 200 m at
        // 10 km. So far off, where a square's corner can say water over ground standing metres above
        // the sea (the waterline is the corner's surroundings', the height its own point's), the
        // ground under the drawn water is pushed well behind it and never shows through it at a
        // slant; near, a few metres of water show clearly apart from the seabed. Further than the
        // near plane's distance over this (500 km) it is at the far plane, and the water covers it.
        const float SEABED_PUSH = 1e-6;

        void main()
        {
            const int side = PATCH_GRID + 1;
            vec4 tile = draw.model[0]; // "patch" is a GLSL keyword
            int face = int(tile.x + 0.5);
            float skirtDepth = draw.model[1].x, lodRatio = draw.model[1].y;

            // Which grid point: the skirt's lower points go round the edge anticlockwise (seen from
            // outside), each under the grid point it hangs from.
            bool skirt = gl_VertexIndex >= side * side;
            ivec2 cell;
            if (!skirt)
                cell = ivec2(gl_VertexIndex % side, gl_VertexIndex / side);
            else
            {
                int k = gl_VertexIndex - side * side, m = k % PATCH_GRID;
                int edge = k / PATCH_GRID;
                cell = edge == 0 ? ivec2(m, 0) : edge == 1 ? ivec2(PATCH_GRID, m) : edge == 2 ? ivec2(PATCH_GRID - m, PATCH_GRID) : ivec2(0, PATCH_GRID - m);
            }
            // Exact (sizes are powers of 2), so neighbouring patches of the same size share their edges' points exactly.
            vec2 uv = tile.yz + tile.w * (vec2(cell) / float(PATCH_GRID));
            vec3 dir = faceDirection(face, uv);

            // About how far apart the squares are here (the patches are chosen to make them so):
            // the detail drawn and the normal's steps follow it. It is estimated from the sea's
            // sphere (as is the step's metres to the face's uv) and ignores the ground's height,
            // at most 0.35% of the distance at Home, which only shifts the detail level slightly.
            vec3 centre = frame.planet.xyz - frame.cameraPos.xyz; // from the camera
            float spacing = max(TERRAIN_FINEST_SPACING, length(centre + dir * frame.planet.w) / (lodRatio * float(PATCH_GRID)));
            float height = terrainHeight(face, uv, dir, spacing);

            // Where the water is drawn (Waterline.Depth), and the depth the seabed's push ramps up
            // over. Near, by the ground's own height. Far off, where the squares are many texels
            // across and a corner's one point would say only water or land (so the waterline would
            // run along the squares where a sheet of ice meets deep water), by how much of the
            // ground round the corner is water: the clamped heights averaged over half a square,
            // blended in over the first level.
            float waterline = -height, pushOver = WATERLINE_NEAR_PUSH;
            float level = waterlineLevel(spacing);
            if (level > 0.0)
            {
                float t = min(level, 1.0);
                waterline = mix(waterline, -textureLod(uWaterline, vec3((uv + 1.0) * 0.5, float(face)), level).r, t);
                pushOver = mix(WATERLINE_NEAR_PUSH, WATERLINE_REACH, t);
            }

            // The water is lifted to the waves (its normal is the fragment shader's, a pixel's
            // worth of waves at a time, finer than the squares here).
            bool water = draw.material.x == KindWater;
            float waves = water ? seaWaveScale(face, uv, dir) : 0.0;
            float surface = water ? seaHeight(dir, waves, spacing) : height;
            vec3 normal = dir;
            if (!water && !skirt)
            {
                // Differences of the same height, a square's spacing on along each of the face's ways
                // (forward ones: two more heights a vertex, not four). The skirt, seen only in
                // cracks, keeps the plain upward normal. Along a face's +u and +v edges a step
                // forward would run off the face, past the half-texel apron, where the texels clamp
                // and the ground reads flat (a straight line of flat shading down the cube's
                // edges); there it steps backward instead, the difference turned round to match.
                float step = spacing / (frame.planet.w * 0.7853982);
                vec2 ways = vec2(uv.x + step > 1.0 ? -step : step, uv.y + step > 1.0 ? -step : step);
                vec3 here = dir * (frame.planet.w + height);
                vec3 across = (groundPoint(face, uv + vec2(ways.x, 0.0), spacing) - here) * sign(ways.x);
                vec3 up = (groundPoint(face, uv + vec2(0.0, ways.y), spacing) - here) * sign(ways.y);
                normal = normalize(cross(across, up));
            }
            if (skirt)
                surface -= skirtDepth;

            vec3 fromCamera = centre + dir * (frame.planet.w + surface);
            gl_Position = frame.viewProj * vec4(fromCamera, 0.0) + frame.cameraClip;
            // The seabed is pushed back in depth (only: it stays where it is on the screen), the
            // more the deeper it is, over its first 2 m (far off, over the waterline's clamp), by
            // the waterline, so it is pushed wherever the water is drawn (never past the far plane, 0).
            // Otherwise, far off, ground the coarse squares raise above the sea under the drawn water
            // would show through it, and the shore would flicker; the sea covers all of it, so nothing
            // that should show is hidden. (Pulling the sea forward instead would hide whatever stands
            // in it, far off.)
            if (!water)
                gl_Position.z = max(gl_Position.z - SEABED_PUSH * clamp(waterline / pushOver, 0.0, 1.0) * gl_Position.w, 0.0);
            vWorldPos = frame.cameraPos.xyz + fromCamera;
            vNormal = normal;
            vUv = vec2(0.0);
            vColor = vec4(1.0); // the ground's colour and the water's are read per pixel
            vTextured = 0.0;
            vFace = face;
            vTerrain = vec4(uv, -height, waves);
            vWaterline = waterline;
        }
        """;

    /// <summary>
    /// The terrain's indices (see <see cref="PlanetVertex"/>): two triangles a square of the grid,
    /// then two a stretch of the skirt, all anticlockwise seen from outside (the back faces are
    /// culled).
    /// </summary>
    public static ushort[] PatchIndices()
    {
        const int n = Scene.PlanetPatchGrid, side = n + 1;
        var indices = new List<ushort>(n * n * 6 + 4 * n * 6);
        for (var j = 0; j < n; j++)
        for (var i = 0; i < n; i++)
        {
            int a = j * side + i, b = a + 1, c = a + side + 1, d = a + side;
            indices.AddRange([(ushort)a, (ushort)b, (ushort)c, (ushort)a, (ushort)c, (ushort)d]);
        }

        // Round the edge: grid point k (as the shader numbers the skirt) and the skirt's point under it.
        int Top(int k)
        {
            k %= 4 * n;
            int m = k % n;
            var (i, j) = (k / n) switch { 0 => (m, 0), 1 => (n, m), 2 => (n - m, n), _ => (0, n - m) };
            return j * side + i;
        }
        int Bottom(int k) => side * side + k % (4 * n);
        for (var k = 0; k < 4 * n; k++)
            indices.AddRange([(ushort)Top(k), (ushort)Bottom(k), (ushort)Bottom(k + 1), (ushort)Top(k), (ushort)Bottom(k + 1), (ushort)Top(k + 1)]);
        return [.. indices];
    }

    /// <summary>Every lit surface in the scene: meshes (<see cref="SceneVertex"/>) and the terrain (<see cref="PlanetVertex"/>).</summary>
    public static string SceneFragment(TerrainMap map) => Common + TerrainCommon(map) + ShadowLookup + DeckSize + FloeTint + SceneFragmentBody;

    /// <summary>The drone ship's deck's size, written in from <see cref="Scene"/>, for its paint (the band round its edge).</summary>
    private static string DeckSize => $"""

        const vec2 deckHalf = vec2({Glsl(Scene.DroneShipHalfLength)}, {Glsl(Scene.DroneShipHalfBeam)}); // half its length (x) and its beam (z)
        """;

    /// <summary>
    /// Where the floes stop being drawn and the floe band's water is tinted instead, and by how
    /// much (see <see cref="Scene.FloeDrawDistance"/> and <see cref="Floes.Cover"/>), written in from the C#.
    /// </summary>
    private static string FloeTint => $"""

        #define FLOE_TINT_FROM {Glsl(Scene.FloeTintFrom)}
        #define FLOE_DRAW_DISTANCE {Glsl(Scene.FloeDrawDistance)}
        #define FLOE_COVER {Glsl(Floes.Cover)}
        """;

    private const string SceneFragmentBody = """

        layout(location = 0) in vec3 vWorldPos;
        layout(location = 1) in vec3 vNormal;
        layout(location = 2) in vec2 vUv;
        layout(location = 3) in vec4 vColor;
        layout(location = 4) in float vTextured;
        layout(location = 5) flat in int vFace;
        layout(location = 6) in vec4 vTerrain;
        layout(location = 7) in float vWaterline;
        layout(location = 0) out vec4 oColor;

        // The ground: its kinds' colours (kinds, already blended), bare rock where it is steeper
        // than about 35 degrees, mottled by patches and fine grain that fade out with distance
        // before they can shimmer. A dusty apron surrounds each pad.
        vec3 groundColor(vec3 p, float distance, vec3 n, vec3 kinds)
        {
            vec3 dir = upAt(p);
            vec3 q = dir * frame.terrain.w; // on the sphere, in metres, so the patterns don't stretch
            float patches = fbm3(q * 0.03 + 7.0);
            float fine = noise3(q * 0.5);
            vec3 c = mix(kinds, slopeRock, smoothstep(0.86, 0.78, dot(n, dir)));
            c *= 0.82 + 0.36 * mix(patches, 0.5, smoothstep(800.0, 2500.0, distance));
            c *= 0.9 + 0.2 * mix(fine, 0.5, smoothstep(60.0, 200.0, distance));
            // A dusty apron round every pad and landing zone: each pad's square to its own east and
            // north (as LaunchSite.Frame is), each zone's round. Measured across the ground there,
            // and only near it (the far side of the planet is straight under it too). The test
            // range's pads share one apron, a strip along the row.
            vec3 range = frame.testRangeEnd.xyz - frame.testRangeStart.xyz;
            vec3 alongRange = frame.testRangeStart.xyz + range * clamp(dot(p - frame.testRangeStart.xyz, range) / dot(range, range), 0.0, 1.0);
            float r = length(p - alongRange) - frame.pad.w;
            for (int i = 0; i < int(frame.siteCount.x); i++)
            {
                vec3 q = p - frame.padSites[i].xyz;
                vec3 east = frame.padEasts[i].xyz, north = frame.padNorths[i].xyz;
                if (abs(dot(q, cross(east, north))) > 500.0) continue; // near it, not under it on the far side
                r = min(r, max(abs(dot(q, east)), abs(dot(q, north))) - frame.pad.x);
            }
            for (int i = 0; i < int(frame.siteCount.y); i++)
            {
                vec3 q = p - frame.zoneSites[i].xyz;
                vec3 up = upAt(frame.zoneSites[i].xyz);
                float along = dot(q, up);
                if (abs(along) > 500.0) continue;
                r = min(r, length(q - up * along) - frame.pad.w);
            }
            float apron = 1.0 - smoothstep(2.0, 35.0 + 10.0 * patches, r);
            return mix(c, vec3(0.55, 0.52, 0.47) * (0.9 + 0.2 * patches), apron);
        }

        // The sea (for now: opaque): its own colours, light over the shallows and dark over deep
        // water, by depth (sea level less the ground's height under it). Each sea's are blended
        // with its neighbours' over a few hundred metres either side of where they meet, and
        // averaged over what a pixel covers (SeaColors: its texel i centred at the map's 4i + 2.5,
        // so uv in [-1, 1] runs over [0, 1]; the faces' shallow colours in layers 0 to 5, their deep
        // ones in 6 to 11), so two seas meet gradually seen from afar, not on a line. Read by the
        // derivatives taken before the water's discard (the ones after it are undefined).
        vec3 waterColor(int face, vec2 uv, vec2 uvDx, vec2 uvDy, float depth)
        {
            vec2 st = (uv + 1.0) * 0.5;
            vec3 shallow = textureGrad(uSeaColors, vec3(st, float(face)), uvDx * 0.5, uvDy * 0.5).rgb;
            vec3 deep = textureGrad(uSeaColors, vec3(st, float(face + 6)), uvDx * 0.5, uvDy * 0.5).rgb;
            return mix(shallow, deep, 1.0 - exp(-depth / 8.0));
        }

        // How much of the sea at uv on a face is floe band, blended over the four texels as the
        // water's colours are.
        float floeBand(int face, vec2 uv)
        {
            ivec2 first;
            vec4 weight;
            terrainTexels(uv, first, weight);
            float band = 0.0;
            for (int t = 0; t < 4; t++)
                band += texelFetch(uKinds, ivec3(first + texelSteps[t], face), 0).r == KIND_FLOE_FIELD ? weight[t] : 0.0;
            return band;
        }

        // A floe's ice: the frozen sheet's colour (its kind's), mottled a little as the ground is, so
        // the two read as the same ice. p is on it, in metres.
        vec3 iceColor(vec3 p, float distance)
        {
            vec3 q = upAt(p) * frame.terrain.w;
            float patches = fbm3(q * 0.03 + 7.0);
            float fine = noise3(q * 0.5);
            vec3 c = kindColors[KIND_PACK_ICE] * (0.82 + 0.36 * mix(patches, 0.5, smoothstep(800.0, 2500.0, distance)));
            return c * (0.9 + 0.2 * mix(fine, 0.5, smoothstep(60.0, 200.0, distance)));
        }

        // Concrete in 4 m slabs, p measured from the pad's centre, scorched there on its top.
        vec3 concrete(vec2 p, bool top)
        {
            vec3 c = vec3(0.64, 0.63, 0.60) * (0.92 + 0.16 * fbm(p * 0.8));
            vec2 seam = abs(fract(p / 4.0 + 0.5) - 0.5) * 4.0;
            c *= 1.0 - 0.25 * (1.0 - smoothstep(0.03, 0.08, min(seam.x, seam.y)));
            if (!top)
                return c;
            float scorch = exp(-dot(p, p) / 30.0) * (0.6 + 0.4 * fbm(p * 1.5));
            return mix(c, vec3(0.08, 0.07, 0.06), scorch * 0.75);
        }

        const vec3 targetYellow = vec3(0.95, 0.77, 0.06);

        // The target painted on a landing site, p measured from its centre: a ring at the target
        // radius and a cross through the centre. How much paint there is at p (0..1).
        float targetPaint(vec2 p)
        {
            float r = length(p);
            float ring = 1.0 - smoothstep(0.25, 0.32, abs(r - frame.pad.z));
            vec2 q = abs(vec2(p.x + p.y, p.x - p.y)) * 0.7071; // the cross runs corner to corner
            float mark = (1.0 - smoothstep(0.45, 0.52, min(q.x, q.y))) * (1.0 - smoothstep(frame.pad.z * 0.7, frame.pad.z * 0.7 + 0.1, r));
            return max(ring, mark);
        }

        // The landing zone: concrete with the yellow target painted on its top, and a yellow band
        // round its rim.
        vec3 landingZoneColor(vec2 p, bool top)
        {
            vec3 c = concrete(p, top);
            if (!top)
                return c;

            float r = length(p);
            float rim = smoothstep(frame.pad.w - 1.3, frame.pad.w - 1.2, r) * (1.0 - smoothstep(frame.pad.w - 0.5, frame.pad.w - 0.4, r));
            return mix(c, targetYellow, max(targetPaint(p), rim) * 0.9);
        }

        // The drone ship's deck, p measured from its middle (x along it, y across): steel plates,
        // 6 m by 2 m, scorched in the middle as the pads are, with the landing zones' target (a
        // landing site looks the same wherever it is) and a yellow band a metre in from its edge.
        vec3 deckColor(vec2 p)
        {
            vec3 c = vec3(0.34, 0.35, 0.37) * (0.88 + 0.24 * fbm(p * 0.6));
            vec2 seam = abs(fract(p / vec2(6.0, 2.0) + 0.5) - 0.5) * vec2(6.0, 2.0);
            c *= 1.0 - 0.3 * (1.0 - smoothstep(0.02, 0.06, min(seam.x, seam.y)));
            float scorch = exp(-dot(p, p) / 30.0) * (0.6 + 0.4 * fbm(p * 1.5));
            c = mix(c, vec3(0.06, 0.055, 0.05), scorch * 0.7);

            vec2 edge = deckHalf - abs(p); // how far in from each edge
            float inset = min(edge.x, edge.y);
            float band = smoothstep(0.9, 1.0, inset) * (1.0 - smoothstep(1.6, 1.7, inset));
            return mix(c, targetYellow, max(targetPaint(p), band) * 0.9);
        }

        void main()
        {
            float kind = draw.material.x;
            // A lamp's colour is its mesh's, and its material's w how far its place's lights are on (0 to 1).
            vec4 color = vec4(vColor.rgb * (kind == KindLamp ? vec3(1.0) : draw.material.yzw), vColor.a);
            vec3 n = normalize(vNormal);
            vec3 v = normalize(frame.cameraPos.xyz - vWorldPos);
            float distance = length(frame.cameraPos.xyz - vWorldPos);
            float shine = 0.35, gloss = 48.0;
            float ice = 0.0; // how much of the water is the far floes' ice

            if (vTextured > 0.5)
            {
                // The logo: don't draw the side facing away (it would show through the gaps between
                // the letters), nor its transparent texels, which would still write depth.
                if (dot(n, v) < 0.0)
                    discard;
                color *= texture(uLogo, vUv);
                if (color.a < 0.02)
                    discard;
            }
            if (kind == KindGround)
            {
                // The kinds' colours: near, read here from the four texels at full precision with
                // their edges wandering; where a texel is down to a pixel or two (far off, or seen
                // edge on) and those would shimmer, the colour map's average over the pixel,
                // cross-faded between the two. The map's read is the same blend without the wander,
                // so the two agree but for where the edges lie.
                float texelsAcross = max(fwidth(vTerrain.x), fwidth(vTerrain.y)) * frame.terrain.x * 0.5; // a pixel's
                float coarse = max(smoothstep(2500.0, 5000.0, distance), smoothstep(0.3, 0.7, texelsAcross));
                vec3 kinds = groundColors(vFace, vTerrain.xy);
                if (coarse < 1.0)
                    kinds = mix(kindColor(vFace, vTerrain.xy, upAt(vWorldPos) * frame.terrain.w), kinds, coarse);
                color.rgb = groundColor(vWorldPos, distance, n, kinds);
                shine = 0.03;
            }
            else if (kind == KindWater)
            {
                // How far a pixel spans here (worked out before the discard, which would leave
                // its neighbours' values undefined).
                float pixel = max(length(dFdx(vWorldPos)), length(dFdy(vWorldPos)));
                vec2 uvDx = dFdx(vTerrain.xy), uvDy = dFdy(vTerrain.xy);
                if (vWaterline < 0.0)
                    discard; // the ground is above the sea here, and drawn already
                // Far off the waterline can draw water a little over ground above the sea: no depth there.
                color = vec4(waterColor(vFace, vTerrain.xy, uvDx, uvDy, max(vTerrain.z, 0.0)), 1.0);
                // The waves' slope, for each pixel (with each wave faded as a pixel grows to a
                // quarter of its length, so it never shimmers), then small ripples that catch the
                // sun: that normal tipped by noise drifting with time, fading out with distance
                // before it can shimmer.
                vec3 surface = seaNormal(upAt(vWorldPos), vTerrain.w, pixel);
                float ripples = 1.0 - smoothstep(150.0, 600.0, distance);
                vec3 q = vWorldPos * 0.4 + vec3(0.0, 0.0, frame.cameraPos.w * 0.5);
                vec3 tip = vec3(noise3(q) - 0.5, noise3(q + vec3(19.1, 7.3, 3.9)) - 0.5, noise3(q + vec3(5.7, 31.3, 11.1)) - 0.5);
                n = normalize(surface + (tip - surface * dot(tip, surface)) * 0.3 * ripples);
                shine = 2.5;
                gloss = 400.0;
                // Far off, where the floes aren't drawn, the floe band's water is tinted towards
                // their ice by how much of it they cover, fading in as they go out of range, so
                // the band reads from afar and no floe pops in as it comes near. As much of the
                // sea's shine and reflection gives way to it.
                ice = floeBand(vFace, vTerrain.xy) * FLOE_COVER * smoothstep(FLOE_TINT_FROM, FLOE_DRAW_DISTANCE, distance);
                color.rgb = mix(color.rgb, iceColor(vWorldPos, distance) * 0.86, ice); // the floes' mean look: duller than the sheet, wet round the rims
                shine *= 1.0 - ice;
            }
            else if (kind == KindIce)
            {
                // A floe: its top and its side (the mesh's colours: the side a little bluer) the ice's,
                // in the floe's own look (the vertex shader's), darker and bluer where it is wet: a
                // band round its rim, wider or narrower by noise, and its side.
                vec3 q = upAt(vWorldPos) * frame.terrain.w;
                float wet = 1.0 - smoothstep(0.1, 0.8 + 0.6 * noise3(q * 0.4), vUv.x); // vUv.x: metres in from the rim
                color.rgb = iceColor(vWorldPos, distance) * vColor.rgb * mix(vec3(1.0), vec3(0.62, 0.74, 0.86), wet * 0.85);
                shine = 0.12;
                gloss = 24.0;
            }
            else if (kind == KindPad)
            {
                color.rgb = concrete(vUv, dot(n, upAt(vWorldPos)) > 0.5); // vUv: where it is across the pad
                shine = 0.08;
            }
            else if (kind == KindLandingZone)
            {
                color.rgb = landingZoneColor(vUv, dot(n, upAt(vWorldPos)) > 0.5); // vUv: where it is across the slab
                shine = 0.08;
            }
            else if (kind == KindDeck)
            {
                // The hull keeps its mesh's colours (steel, red under the waterline); its top is painted.
                if (dot(n, upAt(vWorldPos)) > 0.5)
                    color.rgb = deckColor(vUv); // vUv: where it is across the deck
                shine = 0.15;
            }

            // Sunlight with shadows, as the air leaves it here (orange and dim as the sun sets, none
            // at night), light from the sky (from above, the air's own; from below, bounced off the
            // ground, the ground's colour lit by the sun and that sky), starlight, the moon's light
            // (unshadowed: by day dwarfed, at night more as the moon is fuller), and the engine flame.
            vec3 albedo = toLinear(color.rgb);
            vec3 l = frame.sunDir.xyz;
            vec3 up = upAt(vWorldPos);
            float diffuse = max(dot(n, l), 0.0);
            float visible = diffuse > 0.0 ? lightVisibility(vWorldPos, n) : 0.0;
            vec3 sun = sunlightAt(vWorldPos) * SUN_EXPOSURE;
            // The sky's light thins with the air and goes as night falls (the air's table has both).
            vec3 skyLight = skyLightAt(vWorldPos);
            vec3 bounce = toLinear(vec3(0.45, 0.42, 0.36)) * (sun * max(dot(up, l), 0.0) / PI + skyLight);
            vec3 toMoon = moonDirAt(vWorldPos);
            vec3 sky = mix(bounce, skyLight, dot(n, up) * 0.5 + 0.5) * 0.8 + NIGHT_LIGHT
                + moonlight(vWorldPos, toMoon) * max(dot(n, toMoon), 0.0);
            float specular = pow(max(dot(n, normalize(l + v)), 0.0), gloss) * shine;
            vec3 rgb = albedo * (sky + sun * diffuse * visible + pointLighting(vWorldPos, n) + padLighting(vWorldPos, n));
            if (kind == KindLamp)
                rgb = mix(rgb, albedo * 6.0, draw.material.w); // a lamp's own face: off, lit as any surface; on, it shines, whatever lights it
            if (kind == KindWater)
            {
                // The sky reflected, more of it the flatter the view (Schlick's Fresnel term).
                vec3 r = reflect(-v, n);
                r = normalize(r + up * max(0.02 - dot(r, up), 0.0)); // never below the horizon
                float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(n, v), 0.0), 5.0);
                // (In 2 steps: a reflection, broken up by the waves and held back, shows no difference,
                // and the water's fragments are most of a view over the sea.)
                rgb = mix(rgb, skyColor(r, 2), fresnel * 0.5 * (1.0 - ice)); // (held back: the sea stays blue out to the horizon)
            }
            rgb += sun * specular * visible;

            oColor = vec4(finish(fog(rgb, vWorldPos)), color.a);
        }
        """;

    /// <summary>A triangle covering the whole screen, behind everything (at the far plane, depth 0 as the depth is reversed): the sky.</summary>
    public static string SkyVertex => FrameResources + """

        layout(location = 0) out vec2 vNdc;

        void main()
        {
            vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0;
            vNdc = p;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The moon's ground and its light, for the sky's ray-cast of it and its mesh up close (both shade
    /// it by <c>moonLight</c>, so the handover between them doesn't show): its map (<see cref="MoonGround.Map"/>,
    /// bound at 8 and 9) read by moon-fixed direction, as <see cref="MoonGround"/> reads it
    /// (<c>moonBaseHeight</c>, <c>moonGround</c>'s albedo, <c>sunSeenOver</c>: twins of its
    /// <c>BaseHeight</c>, <c>Albedo</c> and <c>SunSeenOver</c>), and the light the planet lets past and
    /// gives back (<c>planetShadowLight</c> and <c>planetshine</c>: twins of
    /// <see cref="SolarSystem.PlanetShadowLight"/> and <see cref="SolarSystem.Planetshine"/>). Change one and change the other.
    /// </summary>
    private static string MoonFunctions(TerrainMap moon) => CubeFaces + $$"""

        layout(set = 0, binding = 8) uniform sampler2DArray uMoonHeights; // R16_UNORM, a layer per face (aprons and all, as the map has them): stored height / 65535
        layout(set = 0, binding = 9) uniform usampler2DArray uMoonKinds;  // r = MoonKind

        const float MOON_RADIUS = {{Exact(Scene.MoonRadius)}};      // the datum sphere its heights are above (Scene.MoonRadius)
        const float MOON_RESOLUTION = {{moon.Resolution}}.0;      // texels across a face of its map, aprons not counted
        const float MOON_LOWEST = {{Exact(moon.MinHeight)}}, MOON_HIGHEST = {{Exact(moon.MaxHeight)}}; // its map's range of heights, metres
        const float MOON_TEXEL = {{Exact(MathF.PI / 2 * Scene.MoonRadius / moon.Resolution)}}; // a texel's width at a face's middle, metres
        const float moonAlbedos[{{Scene.MoonAlbedos.Length}}] = float[]({{string.Join(", ", Scene.MoonAlbedos.Select(Exact))}}); // by MoonKind (Scene.MoonAlbedos)
        const int MOON_SHADOW_STEPS = {{Scene.MoonShadowSteps}};
        const float MOON_SHADOW_REACH = {{Exact(Scene.MoonShadowReach)}}, MOON_SHADOW_LIFT = {{Exact(Scene.MoonShadowLift)}};
        const float PLANET_SHADOW_GLOW = {{Exact(Scene.PlanetShadowGlow)}};
        const int PLANET_SHADOW_SAMPLES = {{Scene.PlanetShadowSamples}};
        const float PLANETSHINE_STRENGTH = {{Exact(Scene.PlanetshineStrength)}};
        const float CORONA_STRENGTH = {{Exact(Scene.CoronaStrength)}}, CORONA_REACH = {{Exact(Scene.CoronaReach)}}, CORONA_LIMB_SOFTENING = {{Exact(Scene.CoronaLimbSoftening)}};
        const vec3 CORONA_COLOUR = vec3({{Exact(Scene.CoronaColour.X)}}, {{Exact(Scene.CoronaColour.Y)}}, {{Exact(Scene.CoronaColour.Z)}}); // a display colour

        // CubeSphere.ToFace: the face a moon-fixed direction is on, and where on it (equal-angle).
        void moonFace(vec3 d, out int face, out vec2 uv)
        {
            vec3 a = abs(d);
            if (a.x >= a.y && a.x >= a.z)
                face = d.x >= 0.0 ? 0 : 1;
            else if (a.y >= a.z)
                face = d.y >= 0.0 ? 2 : 3;
            else
                face = d.z >= 0.0 ? 4 : 5;
            float along = dot(d, faceNormals[face]);
            uv = atan(vec2(dot(d, faceAcrosses[face]), dot(d, faceUps[face])) / along) * (4.0 / PI);
        }

        // TerrainMap.Texels: the four texels' weights, in its order (0, 0), (+u, 0), (0, +v), (+u, +v),
        // and where in the texture a gather reads those four (the corner they share: one read for all four).
        vec4 moonTexels(vec3 d, out vec3 gather)
        {
            int face;
            vec2 uv;
            moonFace(d, face, uv);
            vec2 t = clamp((uv + 1.0) * 0.5 * MOON_RESOLUTION + 0.5, 0.0, MOON_RESOLUTION + 1.0);
            vec2 first = min(floor(t), vec2(MOON_RESOLUTION));
            vec2 f = t - first;
            gather = vec3((first + 1.0) / (MOON_RESOLUTION + 2.0), float(face));
            return vec4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
        }

        // MoonGround.BaseHeight: the map's height in moon-fixed direction d, bilinear, metres above the
        // datum. (A gather gives (0, +v), (+u, +v), (+u, 0), (0, 0): wzxy puts them in the weights' order.)
        float moonBaseHeight(vec3 d)
        {
            vec3 at;
            vec4 w = moonTexels(d, at);
            vec4 h = round(textureGather(uMoonHeights, at) * 65535.0);
            return MOON_LOWEST + (MOON_HIGHEST - MOON_LOWEST) * dot(h.wzxy, w) / 65535.0;
        }

        // The map's height in d and, as MoonGround.Albedo, how pale the ground is there: the four
        // texels' kinds' albedos by their weights. Two reads.
        float moonGround(vec3 d, out float albedo)
        {
            vec3 at;
            vec4 w = moonTexels(d, at);
            vec4 h = round(textureGather(uMoonHeights, at) * 65535.0);
            uvec4 k = textureGather(uMoonKinds, at);
            albedo = moonAlbedos[k.w] * w.x + moonAlbedos[k.z] * w.y + moonAlbedos[k.x] * w.z + moonAlbedos[k.y] * w.w;
            return MOON_LOWEST + (MOON_HIGHEST - MOON_LOWEST) * dot(h.wzxy, w) / 65535.0;
        }

        // Straight out of the ground in d (height there), worked out as MoonGround.Normal does but not
        // its twin: from the map's heights alone (its fine detail is far under a pixel from where the
        // sky draws it), spread metres each way across it (the pixel's footprint, not the C#'s
        // TerrainNormalStep), so don't make the two alike. Each neighbour is a direction turned onto the cube's faces
        // on its own, so a slope across the edge of a face is read from both faces: no crease there.
        // Four reads. moonSlope is the rise across the ground (a vector along it, metres a metre),
        // the normal less the straight up: the mesh adds its fine detail's to it.
        vec3 moonSlope(vec3 d, float height, float spread)
        {
            vec3 a = normalize(cross(abs(d.x) < 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0), d));
            vec3 b = cross(d, a);
            float stride = spread * 0.5;
            float angle = stride / MOON_RADIUS;
            float span = 2.0 * stride * (MOON_RADIUS + height) / MOON_RADIUS;
            float slopeA = (moonBaseHeight(normalize(d + a * angle)) - moonBaseHeight(normalize(d - a * angle))) / span;
            float slopeB = (moonBaseHeight(normalize(d + b * angle)) - moonBaseHeight(normalize(d - b * angle))) / span;
            return a * slopeA + b * slopeB;
        }

        vec3 moonNormal(vec3 d, float height, float spread)
        {
            return normalize(d - moonSlope(d, height, spread));
        }

        // MoonGround.SunSeenOver: how much of the sun's disc is seen over the ground from the ground in
        // d (its map's height there given), the sun along sun (both moon-fixed). A read a step, at
        // most MOON_SHADOW_STEPS, fewer once the ray is over the highest ground or the disc is hidden.
        float sunSeenOver(vec3 d, float height, vec3 sun)
        {
            vec3 start = d * (MOON_RADIUS + height + MOON_SHADOW_LIFT);
            float lowest = 3.0e38;
            for (int i = 1; i <= MOON_SHADOW_STEPS; i++)
            {
                float f = float(i) / float(MOON_SHADOW_STEPS);
                float along = MOON_SHADOW_REACH * f * f;
                vec3 at = start + sun * along;
                float reach = length(at);
                if (reach - MOON_RADIUS > MOON_HIGHEST)
                    break;
                float clear = reach - MOON_RADIUS - moonBaseHeight(at / reach);
                lowest = min(lowest, clear / along);
                if (lowest <= -frame.atmosphere.w)
                    break;
            }
            return clamp(0.5 + lowest / (2.0 * frame.atmosphere.w), 0.0, 1.0);
        }

        // SolarSystem.PastTheBody (for the planet): the light along a ray from distance from the planet's centre at
        // angle from the way to it, through the air's whole chord at its closest approach, or (into
        // the planet) the grazing ray's light the air bends into the shadow.
        vec3 pastThePlanet(float distance, float angle, vec3 grazing)
        {
            float closest = (angle >= 0.5 * PI ? distance : distance * sin(angle)) - frame.planet.w;
            if (closest >= frame.atmosphere.x)
                return vec3(1.0);
            if (closest >= 0.0)
            {
                vec3 halfWay = airTransmittance(closest, 0.0);
                return halfWay * halfWay;
            }
            return grazing * (PLANET_SHADOW_GLOW + (1.0 - PLANET_SHADOW_GLOW) * exp(closest / frame.atmosphere.y));
        }

        // SolarSystem.PlanetShadowLight: the share and colour of the sunlight reaching p (out past the
        // planet's air) past the planet, 1 in the open, across the sun's disc. No reads in the open; a
        // read a point of the disc near the shadow's edge.
        vec3 planetShadowLight(vec3 p)
        {
            vec3 o = p - frame.planet.xyz;
            vec3 sun = normalize(frame.sunDir.xyz * SUN_DISTANCE - o);
            float distance = length(o);
            vec3 toCentre = -o / distance;
            float fromCentre = atan(length(cross(sun, toCentre)), dot(sun, toCentre));
            float r = frame.atmosphere.w;
            float top = asin(min((frame.planet.w + frame.atmosphere.x) / distance, 1.0));
            if (fromCentre - r >= top)
                return vec3(1.0);
            vec3 grazing = airTransmittance(0.0, 0.0);
            grazing *= grazing;
            vec3 sum = vec3(0.0);
            float weights = 0.0;
            for (int i = 0; i < PLANET_SHADOW_SAMPLES; i++)
            {
                float x = (float(i) + 0.5) / float(PLANET_SHADOW_SAMPLES) * 2.0 - 1.0;
                float weight = sqrt(1.0 - x * x);
                sum += pastThePlanet(distance, fromCentre + x * r, grazing) * weight;
                weights += weight;
            }
            return sum / weights;
        }

        // SolarSystem.Planetshine: the light off the planet at p, as a share of the sunlight on a face
        // turned to it: the lit share of the planet's face seen from p.
        float planetshine(vec3 p)
        {
            return (1.0 + dot(frame.sunDir.xyz, normalize(p - frame.planet.xyz))) * 0.5 * PLANETSHINE_STRENGTH;
        }

        // The moon's ground as the sky's ray-cast and its mesh both shade it: at moon-fixed m (its
        // map's height and paleness there given), its normal n and the sun along sun (both
        // moon-fixed), the place world in the ground's frame: lit by the sun, less its own ground's
        // shadow (the march, only where the ground faces the sun) and what the planet keeps of the
        // sunlight (its shadow, reddened by its air), and by the light off the planet's lit face (so
        // the dark side of a crescent shows its craters faintly). The sunlight times shadowed too: what
        // the vessels on it shade of it (the mesh's, from the shadow map; 1 for the sky's). Linear
        // light, before any air.
        vec3 moonLight(vec3 m, float height, float albedo, vec3 n, vec3 sun, vec3 world, float shadowed)
        {
            mat3 toMoon = mat3(frame.moonFrame);
            float lit = max(dot(n, sun), 0.0);
            vec3 light = vec3(0.0);
            if (lit > 0.0)
                light = planetShadowLight(world) * (lit * sunSeenOver(m, height, sun) * shadowed);
            light += vec3(planetshine(world) * max(dot(n, toMoon * normalize(frame.planet.xyz - world)), 0.0));
            return toLinear(frame.sunColor.rgb) * light * (albedo * SUN_EXPOSURE);
        }

        // SolarSystem.SunDirectionFromMoon, in the moon's own axes.
        vec3 sunFromMoon()
        {
            vec3 moonCentre = frame.cameraPos.xyz + frame.moon.xyz - frame.planet.xyz; // from the planet's centre
            return normalize(mat3(frame.moonFrame) * normalize(frame.sunDir.xyz * SUN_DISTANCE - moonCentre));
        }

        // The corona along view direction d, with the moon over the sun (covered of its disc, from
        // the camera: 0 outside its shadow, so nothing is added then): a pale glow centred on the
        // sun, fading out over CORONA_REACH of its radii beyond its edge, over the sky and coming
        // in over the moon's limb (by CORONA_LIMB_SOFTENING sun radii inside its sphere's edge) so
        // the black disc's edge glows, its middle still black. By the fourth power of how covered
        // the sun is, so it shows only near totality, as the sun's own glare hides it before;
        // dimmed by the air along the ray as the moon is. The sky draws it all; the moon's mesh
        // (onMesh), which covers the sky within its reach, draws again the part over the moon's
        // sphere (none where its ground stands out past it, which hides the corona behind).
        vec3 corona(vec3 d, float covered, bool onMesh)
        {
            float sunRadius = frame.sunDisc.w; // the sun's disc as the camera sees it
            float fromSun = atan(length(cross(d, frame.sunDisc.xyz)), dot(d, frame.sunDisc.xyz));
            if (covered <= 0.0 || frame.moon.w <= 0.0 || fromSun >= sunRadius * (1.0 + CORONA_REACH))
                return vec3(0.0);
            vec3 c = frame.moon.xyz;
            float fade = 1.0 - max(fromSun - sunRadius, 0.0) / (CORONA_REACH * sunRadius);
            float moonRadius = asin(min(frame.moon.w / length(c), 1.0));
            float fromMoon = atan(length(cross(d, c)), dot(d, c));
            if (onMesh && fromMoon >= moonRadius)
                return vec3(0.0);
            float limb = smoothstep(moonRadius - CORONA_LIMB_SOFTENING * sunRadius, moonRadius, fromMoon);
            float strength = covered * covered;
            strength *= strength;
            return toLinear(CORONA_COLOUR) * airTransmittance(altitudeOf(frame.cameraPos.xyz), dot(upAt(frame.cameraPos.xyz), d))
                * (CORONA_STRENGTH * strength * fade * fade * fade * limb);
        }
        """;

    /// <summary>
    /// The moon's ground up close (within <see cref="Scene.MoonMeshReach"/>), one patch
    /// (<see cref="PlanetPatch"/>, of the moon's own cube: <see cref="PatchBody.Moon"/>) a draw, as
    /// the planet's are (<see cref="PlanetVertex"/>: the same grid, skirt and indices, built from
    /// gl_VertexIndex): each grid point's moon-fixed direction pushed out to its ground's height (the
    /// map plus <see cref="MoonGround.Detail"/>'s octaves, faded for the squares' spacing as the
    /// planet's are) times the relief drawn, turned out into the ground's frame by
    /// <c>frame.moonFrame</c>'s inverse and placed from the camera (the moon's centre as seen from
    /// it, <c>frame.moon</c>), so no large coordinates are subtracted and it keeps still up close.
    /// </summary>
    /// <remarks>
    /// The draw's model matrix carries the patch: row 0 = (face, u0, v0, size), row 1 = (the skirt's
    /// depth in metres, the view's level-of-detail ratio, the relief drawn, 0). Shaded by
    /// <see cref="MoonFragment"/>.
    /// </remarks>
    public static string MoonVertex => Common + NoiseFunctions + MoonFunctions(MoonGround.Map) + MoonDetailFunctions + """

        layout(location = 0) out vec3 vWorldPos;
        layout(location = 1) out vec3 vMoonPoint;   // from the moon's centre, in its own axes
        layout(location = 2) out vec3 vDetailSlope; // the fine detail's rise across the ground (moon-fixed, metres a metre)

        // The direction from the moon's centre at a point on a face (equal-angle, as CubeSphere.FromFace).
        vec3 moonDirection(int face, vec2 uv)
        {
            vec2 t = tan(uv * 0.7853982);
            return normalize(faceNormals[face] + faceAcrosses[face] * t.x + faceUps[face] * t.y);
        }

        void main()
        {
            const int side = PATCH_GRID + 1;
            vec4 tile = draw.model[0];
            int face = int(tile.x + 0.5);
            float skirtDepth = draw.model[1].x, lodRatio = draw.model[1].y, relief = draw.model[1].z;

            // Which grid point, as the planet's patches number them.
            bool skirt = gl_VertexIndex >= side * side;
            ivec2 cell;
            if (!skirt)
                cell = ivec2(gl_VertexIndex % side, gl_VertexIndex / side);
            else
            {
                int k = gl_VertexIndex - side * side, m = k % PATCH_GRID;
                int edge = k / PATCH_GRID;
                cell = edge == 0 ? ivec2(m, 0) : edge == 1 ? ivec2(PATCH_GRID, m) : edge == 2 ? ivec2(PATCH_GRID - m, PATCH_GRID) : ivec2(0, PATCH_GRID - m);
            }
            vec2 uv = tile.yz + tile.w * (vec2(cell) / float(PATCH_GRID));
            vec3 dir = moonDirection(face, uv);

            mat3 toGround = transpose(mat3(frame.moonFrame)); // a rotation: its inverse is its transpose
            vec3 centre = frame.moon.xyz; // from the camera
            float spacing = max(TERRAIN_FINEST_SPACING, length(centre + toGround * (dir * MOON_RADIUS)) / (lodRatio * float(PATCH_GRID)));
            float detail = moonDetail(dir, spacing);
            float height = (moonBaseHeight(dir) + detail) * relief;

            // The detail's slope, from differences a square's spacing on along two ways across the
            // ground (the map's is the fragment's, read by direction, so the faces' edges show no
            // crease); the skirt, seen only in cracks, has none.
            vec3 slope = vec3(0.0);
            if (!skirt && detail != 0.0)
            {
                vec3 a = normalize(cross(abs(dir.x) < 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0), dir));
                vec3 b = cross(dir, a);
                float angle = spacing / MOON_RADIUS;
                slope = (a * (moonDetail(normalize(dir + a * angle), spacing) - detail)
                    + b * (moonDetail(normalize(dir + b * angle), spacing) - detail)) * (relief / spacing);
            }
            if (skirt)
                height -= skirtDepth;

            vec3 point = dir * (MOON_RADIUS + height);
            vec3 fromCamera = centre + toGround * point;
            gl_Position = frame.viewProj * vec4(fromCamera, 0.0) + frame.cameraClip;
            vWorldPos = frame.cameraPos.xyz + fromCamera;
            vMoonPoint = point;
            vDetailSlope = slope;
        }
        """;

    /// <summary>
    /// <see cref="MoonGround.Detail"/>, the moon's fine detail: its octaves written in from
    /// <see cref="Scene.MoonDetail"/>, each faded out for squares too far apart to draw it (as the
    /// planet's are), and the patches' grid.
    /// </summary>
    private static string MoonDetailFunctions => $$"""

        #define PATCH_GRID {{Scene.PlanetPatchGrid}}
        #define TERRAIN_FINEST_SPACING {{Glsl(Scene.TerrainFinestSpacing)}}
        const float MOON_DETAIL_SCALE = {{Exact(Scene.MoonDetailScale)}};
        const vec2 moonOctaves[{{Scene.MoonDetail.Length}}] = vec2[]({{string.Join(", ", Scene.MoonDetail.Select(o => $"vec2({Exact(o.Amplitude)}, {Exact(o.Wavelength)})"))}}); // (amplitude, wavelength)

        // MoonGround.Detail in moon-fixed direction d, on squares spacing metres apart: all of an
        // octave at 4 squares a wavelength, none at 2, so it never shimmers.
        float moonDetail(vec3 d, float spacing)
        {
            vec3 p = d * MOON_DETAIL_SCALE;
            float sum = 0.0;
            for (int o = 0; o < moonOctaves.length(); o++)
            {
                float fade = clamp(moonOctaves[o].y / spacing * 0.5 - 1.0, 0.0, 1.0);
                if (fade > 0.0)
                    sum += fade * moonOctaves[o].x * valueNoise(p / moonOctaves[o].y);
            }
            return sum;
        }
        """;

    /// <summary>
    /// The moon's ground up close (<see cref="MoonVertex"/>): shaded as the sky's ray-cast shades it
    /// from far (<c>moonLight</c>: the same map's paleness, normals read by direction over the
    /// pixel's width, its own shadows, the planet's shadow and planetshine), so the handover between
    /// them doesn't show (the map's slopes in full whatever relief the mesh draws), its normal with
    /// the fine detail's slope too; and, as the planet's ground is, the view's lights (the landing
    /// lights of the vessels near it) and the engine flame, which the sky's far ray-cast has none of
    /// (they reach no further than the mesh); and in an eclipse the corona's glow over the moon's
    /// limb, which the sky paints and the mesh covers. No air: no fog or haze, no sky light, no sea.
    /// </summary>
    public static string MoonFragment => Common + MoonFunctions(MoonGround.Map) + ShadowLookup + """

        layout(location = 0) in vec3 vWorldPos;
        layout(location = 1) in vec3 vMoonPoint;
        layout(location = 2) in vec3 vDetailSlope;
        layout(location = 0) out vec4 oColor;

        void main()
        {
            vec3 m = normalize(vMoonPoint);
            float albedo;
            float height = moonGround(m, albedo);
            // The map's slopes in full whatever the relief drawn (so at the handover, its relief
            // none, it is shaded as the sky shades the sphere), and the detail's as drawn.
            float footprint = length(fwidth(vMoonPoint));
            vec3 n = normalize(m - moonSlope(m, height, clamp(footprint, MOON_TEXEL, 32.0 * MOON_TEXEL)) - vDetailSlope);
            // Its normal turned out of the moon's own axes into the scene's frame, the frame the
            // shadow map and the lights are in.
            vec3 nScene = transpose(mat3(frame.moonFrame)) * n;
            // The vessels' sun shadows on it, from the shadow map (by that normal, for the lookup's
            // nudge), only where the sun is on it.
            vec3 sun = sunFromMoon();
            float shadowed = dot(n, sun) > 0.0 ? shadowMapVisibility(vWorldPos, nScene) : 1.0;
            vec3 rgb = moonLight(m, height, albedo, n, sun, vWorldPos, shadowed);
            // And the view's lights (the landing lights of the vessels near it) and the engine
            // flame, as on the planet's ground.
            rgb += albedo * (pointLighting(vWorldPos, nScene) + padLighting(vWorldPos, nScene));
            rgb += corona(normalize(vWorldPos - frame.cameraPos.xyz), 1.0 - sunSeenFromCamera(), true);
            oColor = vec4(finish(rgb), 1.0);
        }
        """;

    /// <summary>The sky, with the stars, the sun's disc and the moon (its map's, <see cref="MoonGround.Map"/>, loaded for its numbers), writing the moon's depth where it covers a pixel (else the far plane's), so the moon hides what is behind it.</summary>
    public static string SkyFragment => FrameResources + MoonFunctions(MoonGround.Map) + $$"""

        layout(location = 0) in vec2 vNdc;
        layout(location = 0) out vec4 oColor;
        // Its depth is never less than the triangle's own (it is drawn at the far plane, 0, as the
        // depth is reversed): saying so keeps the hardware's coarse depth test good for what is drawn after.
        layout(depth_greater) out float gl_FragDepth;

        const float SUN_DISC_SOFTENING = {{Exact(Scene.SunDiscSoftening)}}; // the share of the sun's disc's radius its edge is softened over (Scene.SunDiscSoftening)
        const float ECLIPSE_HORIZON_GLOW = {{Exact(Scene.EclipseHorizonGlow)}}, ECLIPSE_HORIZON_HEIGHT = {{Exact(Scene.EclipseHorizonHeight)}};

        void main()
        {
            // The view ray through this pixel: a point on the near plane (depth 1, reversed)
            // unprojected with the camera at the origin, which is its direction (no camera's place
            // to take off: see ViewRays).
            vec4 onNear = frame.invViewRays * vec4(vNdc, 1.0, 1.0);
            vec3 d = normalize(onNear.xyz / onNear.w);

            vec3 sky = skyColor(d);
            // With the moon over the sun here (covered, 0 outside its shadow, so nothing is added
            // then), the light from outside its shadow: the air far off all round still in the sun,
            // seen as a band of the sunset's colour along the horizon (the sun's light through a
            // grazing path of air, the table's, from here), fading upwards and with the air
            // thinning above the camera (none from above the air).
            vec3 cameraUp = upAt(frame.cameraPos.xyz);
            float cameraHeight = altitudeOf(frame.cameraPos.xyz);
            float covered = 1.0 - sunSeenFromCamera();
            if (covered > 0.0)
                sky += toLinear(frame.sunColor.rgb) * airTransmittance(cameraHeight, 0.0)
                    * (covered * ECLIPSE_HORIZON_GLOW * airDensity(cameraHeight) * exp(-abs(dot(cameraUp, d)) / ECLIPSE_HORIZON_HEIGHT));
            vec3 rgb = sky;
            // Stars, where the sky is dark enough to show them: a few bright points on a fine grid
            // of directions, each twinkling a little. The grid is laid among the stars, not over the
            // ground: the ray is turned about the north pole by how far the planet has turned
            // (Rodrigues' rotation, the same turn as Planet.TurnedDirection's), so the stars rise and
            // set while each stays put among the others. The sun's disc below keeps the true ray.
            vec3 pole = vec3(0.0, 0.0, -1.0);
            float turnSin = sin(frame.atmosphere.z);
            float turnCos = cos(frame.atmosphere.z);
            vec3 ds = d * turnCos + cross(pole, d) * turnSin + pole * dot(pole, d) * (1.0 - turnCos);
            vec3 cell = floor(ds * 400.0);
            float star = hash3(ivec3(cell));
            vec3 at = (cell + 0.5) / 400.0;
            float point = smoothstep(0.0014, 0.0, length(ds - normalize(at)));
            float brightness = step(0.996, star) * point * (0.6 + 0.4 * sin(frame.cameraPos.w * 3.0 + star * 50.0));
            rgb += vec3(0.9, 0.92, 1.0) * brightness * 2.0 * clamp(1.0 - dot(rgb, vec3(3.0)), 0.0, 1.0);
            // The sun's disc at its true size from here (frame.sunDisc: the way to its centre from
            // the camera and its angular radius, so from the map's furthest zoom it is where the sun
            // is and as big as it looks there), its edge softened over a small share of that radius
            // but never less than a pixel's width (fwidth, so it doesn't step or shimmer in a small
            // view or at a narrow field), in the colour the air leaves it here (orange low down, gone
            // below the horizon). Not dimmed by the moon (not sunlightAt, which is): the moon, drawn
            // over it, hides the part it covers, and the crescent left is as bright as ever. The
            // angle from the sun's centre is taken by atan, as acos loses it near 0. (Its glare is
            // the lens flare, drawn later.)
            vec3 toSun = frame.sunDisc.xyz;
            float sunRadius = frame.sunDisc.w;
            float fromSun = atan(length(cross(d, toSun)), dot(d, toSun));
            float soft = max(0.5 * SUN_DISC_SOFTENING * sunRadius, 0.5 * fwidth(fromSun));
            vec3 sunHere = toLinear(frame.sunColor.rgb) * airTransmittance(altitudeOf(frame.cameraPos.xyz), dot(upAt(frame.cameraPos.xyz), toSun));
            rgb += sunHere * (1.0 - smoothstep(sunRadius - soft, sunRadius + soft, fromSun)) * 20.0;
            // Where the ray meets the sun's sphere, if it does (its distance from its angular radius).
            float sunDistance = SUN_RADIUS / sin(sunRadius);
            float sunMiss = sunDistance * sin(fromSun);
            float sunHit = fromSun < sunRadius ? sunDistance * cos(fromSun) - sqrt(max(SUN_RADIUS * SUN_RADIUS - sunMiss * sunMiss, 0.0)) : -1.0;

            // The moon, where the ray meets its datum sphere (its centre given from the camera, so it
            // holds still and sharp however far either is), over the sky, the stars and the sun's
            // disc. The place hit, turned into the moon's own axes, is its ground there (its map's
            // height, slope and paleness), lit as moonLight has it; seen through the air as the sky
            // is, its light dimmed by the air from here to the air's top along the ray (none below
            // the horizon) and the sky's own light along the ray added, if the way to it crosses the
            // air at all (else the sky's light is from beyond the moon: the planet's air behind it
            // seen from out past it, which it hides). Its edge covers the pixel by how far inside it
            // the ray passes, over a pixel's width there, so it doesn't alias. (Only where the sky is
            // drawn: the ground and the vessels cover it; and not where this view draws the moon as
            // a mesh, frame.moonlight.w, which is the same ground shaded alike.) Its slopes are read
            // over the pixel's width on the ground (from a texel to 32), so they don't shimmer from far.
            vec3 c = frame.moon.xyz;
            float along = dot(d, c);
            float miss = length(c - d * along);
            float cover = clamp((frame.moon.w - miss) / max(fwidth(miss), 1e-3) + 0.5, 0.0, 1.0);
            float into = sqrt(max(frame.moon.w * frame.moon.w - miss * miss, 0.0));
            vec3 hit = d * (along - into) - c; // from the moon's centre
            float footprint = length(fwidth(hit));
            // Not where the planet's sea sphere is on the way to it: the planet's ground and sea are
            // drawn over this pixel there, so the moon's shading would be thrown away (from Home, the
            // moon below the horizon; from the moon, the planet in front of it).
            // (As sphereHit: by the closest approach as a difference of vectors, so it holds from far out.)
            vec3 toSea = frame.planet.xyz - frame.cameraPos.xyz;
            float towards = dot(toSea, d), seaMiss = length(toSea - d * towards);
            bool outside = length(toSea) > frame.planet.w;
            float meets = (frame.planet.w - seaMiss) * (frame.planet.w + seaMiss);
            bool planetInFront = outside && towards > 0.0 && meets > 0.0 && towards - sqrt(meets) < along - into;
            // Nor where the sun is in front of it (seen from out past the sun, at the map's furthest zoom).
            bool sunInFront = sunHit > 0.0 && sunHit < along - into;
            bool moonHere = frame.moon.w > 0.0 && frame.moonlight.w == 0.0 && along > 0.0 && !sunInFront;
            if (moonHere && cover > 0.0 && !planetInFront)
            {
                vec3 m = normalize(mat3(frame.moonFrame) * hit);
                vec3 world = frame.cameraPos.xyz + c + hit;
                float albedo;
                float height = moonGround(m, albedo);
                vec3 n = moonNormal(m, height, clamp(footprint, MOON_TEXEL, 32.0 * MOON_TEXEL));
                vec3 face = moonLight(m, height, albedo, n, sunFromMoon(), world, 1.0);
                // The nearest the way to the moon comes to the planet's centre, against the air's top.
                vec3 toPlanet = frame.planet.xyz - frame.cameraPos.xyz;
                float nearest = length(toPlanet - d * clamp(dot(toPlanet, d), 0.0, along - into));
                vec3 inFront = nearest < frame.planet.w + frame.atmosphere.x ? sky : vec3(0.0);
                vec3 seen = face * airTransmittance(altitudeOf(frame.cameraPos.xyz), dot(upAt(frame.cameraPos.xyz), d)) + inFront;
                rgb = mix(rgb, seen, cover);
            }

            // The corona, with the moon over the sun, over the sky and the moon's limb (where the
            // moon is a mesh, the mesh draws the part over it).
            rgb += corona(d, covered, false);
            oColor = vec4(finish(rgb), 1.0);

            // Its depth: the moon's or the sun's where it covers the pixel, else the far plane's (0,
            // as the depth was cleared: it is reversed, 1 at the near plane), so whatever is behind
            // either (the planet seen from beyond the moon, a vessel past it; the planet and the moon
            // seen past the sun from the map's furthest zoom) is hidden by it from anywhere, as every
            // draw after tests against it (the moon's mesh up close writes its own). The moon's where
            // the ray meets a sphere under its lowest ground: so nothing on its ground or above it is
            // cut, and all behind it is. That sphere is under the lowest ground by two of the depth's
            // own steps where the ray would meet the lowest ground (reversed in a float, a step is
            // the distance times 2^-23, whatever the near plane: a few millimetres at 10 km), and at
            // least a metre, so a stage or a landing light's glow on the moon's lowest ground never
            // ties with it and flickers. Round the limb, in the ring where the ray passes over that
            // sphere but through the datum's (the lowest ground's depth and the metre, some fourteen
            // pixels wide at 10 km out and two at 60 km, at 1080 lines), the depth is the middle of
            // its way through the datum sphere, where it comes nearest the moon's centre: so all
            // behind the moon's middle there (the planet past its edge, a vessel behind it) is hidden
            // too, and only what is on the near half of that way (on or by the moon's ground at its
            // edge) is drawn over it. The sun's where the ray meets its sphere.
            float depth = 0.0;
            float reach = -1.0;
            if (moonHere && cover >= 0.5)
            {
                float lowest = frame.moon.w + MOON_LOWEST;
                float inLowest = lowest * lowest - miss * miss;
                float hit = inLowest > 0.0 ? along - sqrt(inLowest) : along; // where the lowest ground would be met on this ray (round the limb, the way's middle)
                float margin = max(1.0, 2.0 * hit * exp2(-23.0));
                float low = max(lowest - margin, 0.0);
                float inLow = low * low - miss * miss;
                reach = inLow > 0.0 ? along - sqrt(inLow) : along;
            }
            else if (sunHit > 0.0)
                reach = sunHit;
            if (reach > 0.0)
            {
                vec4 clip = frame.viewProj * vec4(d * reach, 0.0) + frame.cameraClip; // from the camera, as the meshes are placed
                depth = clamp(clip.z / clip.w, 0.0, 1.0);
            }
            gl_FragDepth = depth;
        }
        """;

    /// <summary>Must match <c>VulkanRenderer.FlareElement</c>.</summary>
    private const string FlareCommon = """
        #version 450
        layout(push_constant) uniform Flare
        {
            vec4 rect;   // xy = centre in clip space, zw = half size in clip space
            vec4 color;  // rgb = tint, already scaled by brightness
            vec4 params; // x = shape (0 glow, 1 starburst, 2 disc, 3 ring, 4 hexagon), y = rotation (radians), z = depth
        } flare;
        """;

    /// <summary>One flare element: a quad built from gl_VertexIndex (no vertex buffer needed).</summary>
    public const string FlareVertex = FlareCommon + """

        layout(location = 0) out vec2 vLocal; // -1..1 across the element

        void main()
        {
            const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));
            vec2 c = corners[gl_VertexIndex];
            float s = sin(flare.params.y), k = cos(flare.params.y);
            vLocal = vec2(k * c.x - s * c.y, s * c.x + k * c.y); // rotate the shape, not the quad
            gl_Position = vec4(flare.rect.xy + c * flare.rect.zw, flare.params.z, 1.0);
        }
        """;

    /// <summary>
    /// The flare's shapes, generated procedurally: a soft glow, a starburst of streaks, and the
    /// "ghosts" a real lens makes (discs, rings and the hexagons of a camera's aperture).
    /// </summary>
    public const string FlareFragment = FlareCommon + """

        layout(location = 0) in vec2 vLocal;
        layout(location = 0) out vec4 oColor;

        void main()
        {
            float r = length(vLocal);
            int shape = int(flare.params.x + 0.5);
            float v = 0.0;

            if (shape == 0)      // glow: a tight bright core with a long soft tail
                v = exp(-r * r * 6.0) * 0.7 + 0.35 / (1.0 + pow(r * 6.0, 2.0)) - 0.03;
            else if (shape == 1) // starburst: six main streaks and six fainter ones between them
            {
                float a = atan(vLocal.y, vLocal.x);
                float rays = pow(abs(cos(a * 3.0)), 90.0) + 0.35 * pow(abs(cos(a * 3.0 + 1.5708)), 140.0);
                v = rays * max(1.0 - r, 0.0) * max(1.0 - r, 0.0);
            }
            else if (shape == 2) // disc ghost: soft-edged, a touch brighter at the rim
            {
                // Nothing outside the disc: so the sun's occlusion probe, drawn as a disc, counts
                // only the disc's pixels, not its quad's corners (a ghost is dark there anyway).
                if (r > 1.0)
                    discard;
                v = smoothstep(1.0, 0.86, r) * (0.55 + 0.45 * smoothstep(0.5, 0.95, r));
            }
            else if (shape == 3) // ring ghost
                v = exp(-pow((r - 0.82) / 0.07, 2.0));
            else                 // hexagon ghost, like the aperture blades of a camera
            {
                vec2 q = abs(vLocal);
                float hex = max(q.x * 0.8660254 + q.y * 0.5, q.y);
                v = smoothstep(1.0, 0.92, hex) * (0.6 + 0.4 * smoothstep(0.6, 0.98, hex));
            }

            oColor = vec4(flare.color.rgb * max(v, 0.0), 0.0); // additive: alpha is ignored
        }
        """;

    /// <summary>Must match <c>VulkanRenderer.GlowConstants</c>.</summary>
    private static string GlowCommon => FrameResources + """

        layout(push_constant) uniform Glow
        {
            vec4 origin; // xyz = the nozzle (plume) or centre (fireball); w = length (plume) or radius (fireball)
            vec4 axis;   // xyz = which way the plume points; w = its half width at its widest
            vec4 params; // x = kind (0 plume, 1 fireball, 2 a far lamp, 3 the vessels' landing lights), y = brightness, z = heat 0..1, w = seed (a lamp's z, w: its colour's green, blue)
        } glow;
        """;

    /// <summary>
    /// A plume: a quad along the exhaust, turned about it to face the camera. A fireball or a far
    /// lamp: a quad facing the camera. All built from gl_VertexIndex. The vessels' landing lights
    /// (kind 3) are one instanced draw: each instance a point of light from the vessels' glow buffer
    /// (<c>VulkanRenderer</c>'s, binding 7: xyz where, w how far on), sized as a far lamp is, by
    /// its distance from this view's camera, and coloured by <c>glow.axis</c>.
    /// </summary>
    public static string GlowVertex => GlowCommon + $$"""

        layout(std430, set = 0, binding = 7) readonly buffer VesselGlows { vec4 vesselGlows[]; };

        layout(location = 0) out vec2 vLocal; // plume: x across -1..1, y along 0..1; fireball: -1..1 both ways
        layout(location = 1) flat out vec4 vPoint; // a far lamp's or landing light's colour (rgb) and brightness (a)

        void main()
        {
            const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));
            vec2 c = corners[gl_VertexIndex];
            vec3 origin = glow.origin.xyz;
            float size = glow.origin.w;
            vPoint = vec4(1.0, glow.params.z, glow.params.w, glow.params.y);
            // What its twinkle's beats are seeded by: a lamp by where it is (it never moves).
            vec3 key = floor(origin * 0.5);
            if (glow.params.x > 2.5)
            {
                // w: twice the vessel's seed plus how far on it is (VulkanRenderer.GlowW). Seeded by
                // the vessel, not where it is, which it moves on from every frame.
                vec4 light = vesselGlows[gl_InstanceIndex];
                float seed = floor(light.w * 0.5);
                origin = light.xyz;
                size = max(0.25, distance(frame.cameraPos.xyz, origin) * {{Glsl(GameOne.Scene.GlowPixelPerMetre)}} * {{Glsl(GameOne.Scene.LampGlowPixels)}});
                vPoint = vec4(glow.axis.xyz, glow.params.y * (light.w - 2.0 * seed));
                key = vec3(seed, 7.0, 3.0);
            }
            if (glow.params.x > 1.5)
            {
                // A far lamp or landing light seen from far enough off twinkles (Scene.GlowTwinkle):
                // two beats of its own (from a hash of its key) on the clock, eased in past
                // GlowTwinkleDistance; nearer it is steady.
                float far = smoothstep({{Glsl(GameOne.Scene.GlowTwinkleDistance)}}, {{Glsl(1.5f * GameOne.Scene.GlowTwinkleDistance)}}, distance(frame.cameraPos.xyz, origin));
                if (far > 0.0)
                {
                    float h = fract(sin(dot(key, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
                    float t = frame.cameraPos.w;
                    float shimmer = sin(t * (4.0 + 3.0 * h) + h * 40.0) * sin(t * (2.3 + 1.7 * h) + h * 17.0);
                    vPoint.a *= 1.0 + {{Glsl(GameOne.Scene.GlowTwinkle)}} * far * shimmer;
                }
            }
            vec3 toCamera = frame.cameraPos.xyz - origin;
            vec3 world;
            if (glow.params.x < 0.5)
            {
                vec3 axis = glow.axis.xyz;
                vec3 side = cross(axis, toCamera);
                // Seen straight down its length, any side will do: the fireball at the nozzle covers it.
                side = length(side) > 1e-3 ? normalize(side) : normalize(cross(axis, vec3(0.6, 0.0, 0.8)));
                float along = c.y * 0.5 + 0.5;
                world = glow.origin.xyz + axis * (along * 1.05 - 0.05) * glow.origin.w + side * c.x * glow.axis.w;
                vLocal = vec2(c.x, along);
            }
            else
            {
                vec3 forward = normalize(toCamera);
                vec3 right = normalize(cross(vec3(0.0, 1.0, 0.0), forward));
                vec3 up = cross(forward, right);
                world = origin + (right * c.x + up * c.y) * size;
                vLocal = c;
            }
            gl_Position = frame.viewProj * vec4(world, 1.0);
        }
        """;

    /// <summary>
    /// Fire, added onto whatever is behind it. The plume: a white-hot core with bright shock
    /// diamonds near the nozzle, flaring out into flickering orange flame that fades with length.
    /// A fireball: a churning ball, white-hot and yellow when fresh, cooling to deep red. A far lamp
    /// or a vessel's landing lights: a point of its colour with a soft halo.
    /// </summary>
    public static string GlowFragment => GlowCommon + """

        layout(location = 0) in vec2 vLocal;
        layout(location = 1) flat in vec4 vPoint;
        layout(location = 0) out vec4 oColor;

        void main()
        {
            float time = frame.cameraPos.w;
            float seed = glow.params.w;
            float brightness = glow.params.y;
            vec3 rgb;
            if (glow.params.x > 1.5)
            {
                // A lamp or a vessel's landing lights seen from afar: a point with a soft halo, in its colour (vPoint.rgb) as bright as it is on (vPoint.a).
                float r = length(vLocal);
                rgb = vPoint.rgb * (exp(-r * r * 18.0) + 0.25 * exp(-r * r * 4.0)) * smoothstep(1.0, 0.7, r);
                brightness = vPoint.a;
            }
            else             if (glow.params.x < 0.5)
            {
                float t = vLocal.y, s = vLocal.x;
                float width = mix(0.3, 1.0, sqrt(t));
                float core = exp(-pow(s / (width * 0.35), 2.0));
                float outer = exp(-pow(s / width, 2.0) * 2.0);
                float flicker = fbm(vec2(t * 7.0 - time * 16.0, s * 2.0 + seed));
                float diamonds = 0.5 + 0.5 * cos(t * 34.0);
                float fade = pow(1.0 - t, 1.6) * smoothstep(0.0, 0.05, t);
                vec3 hot = vec3(1.0, 0.95, 0.85), mid = vec3(1.0, 0.55, 0.18), cool = vec3(0.7, 0.18, 0.04);
                rgb = hot * core * (0.6 + 0.8 * diamonds * exp(-t * 4.0))
                    + mix(mid, cool, t) * outer * (0.35 + 0.9 * flicker);
                rgb *= fade;
            }
            else
            {
                float r = length(vLocal);
                float churn = fbm(vLocal * 2.2 + seed + vec2(time * 0.4, -time * 0.9));
                float density = smoothstep(1.0, 0.25, r + (churn - 0.5) * 0.7);
                float heat = glow.params.z;
                rgb = mix(vec3(0.75, 0.12, 0.02), vec3(1.0, 0.7, 0.25), heat) * density * (0.6 + 0.6 * churn)
                    + vec3(1.0, 0.95, 0.8) * heat * smoothstep(0.55, 0.0, r);
            }
            oColor = vec4(rgb * brightness, 0.0); // additive: alpha is ignored
        }
        """;

    /// <summary>Must match <c>VulkanRenderer.SmokePuffConstants</c>.</summary>
    private static string SmokeCommon => FrameResources + """

        layout(push_constant) uniform Puff
        {
            vec4 centre; // xyz = world space, w = half width
            vec4 params; // x = seed, y = opacity, z = 1 for a cloud (not stirred), 0 for the ground field
            vec4 shape;  // x = height as a fraction of width, y = the ground's distance from the planet's centre, z = fades out this far above it, w = and in this far
            vec4 color;  // rgb
        } puff;
        """;

    /// <summary>
    /// One puff of smoke: an upright quad built from gl_VertexIndex, turned about the vertical to face
    /// the camera, so it stays standing on the ground rather than tipping with the camera's pitch.
    /// </summary>
    public static string SmokeVertex => SmokeCommon + """

        layout(location = 0) out vec2 vLocal; // -1..1 across the puff
        layout(location = 1) out vec3 vWorldPos;
        layout(location = 2) flat out vec3 vRight; // the puff's horizontal axis, in world space
        layout(location = 3) flat out vec3 vUp;    // and its upright one: the planet's up where it is
        layout(location = 4) out vec3 vFogThrough;  // the air between the camera and this corner (fogTerms)
        layout(location = 5) out vec3 vFogLight;

        void main()
        {
            const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));
            vec2 c = corners[gl_VertexIndex];
            vec3 up = upAt(puff.centre.xyz);
            vec3 toCamera = frame.cameraPos.xyz - puff.centre.xyz;
            vec3 right = normalize(cross(up, toCamera - up * dot(toCamera, up)));
            float halfWidth = puff.centre.w;
            vec3 world = puff.centre.xyz + right * c.x * halfWidth + up * (c.y * halfWidth * puff.shape.x);
            vLocal = c;
            vWorldPos = world;
            vRight = right;
            vUp = up;
            // The haze between the camera and the puff, worked out at its corners, not for every
            // pixel: a puff is tens of metres across, the haze's scale kilometres, and puffs overlap
            // many deep in a launch's cloud, where a march for each pixel of each would cost too much.
            fogTerms(world, vFogThrough, vFogLight);
            gl_Position = frame.viewProj * vec4(world, 1.0);
        }
        """;

    /// <summary>
    /// Billowing smoke from domain-warped value noise, lit by the sun (in the booster's shadow where
    /// the shadow map says so, and glowing when the camera looks through it towards the sun) and by
    /// the engine flame. The booster stirs it: around each stir the smoke is drawn from somewhere
    /// else, as if it had been pushed there, and thinned out where the booster has just been.
    /// </summary>
    public static string SmokeFragment => SmokeCommon + ShadowLookup + $$"""

        const float SMOKE_LIT_SHARE = {{Exact(Scene.SmokeLitShare)}};
        """ + """

        layout(location = 0) in vec2 vLocal;
        layout(location = 1) in vec3 vWorldPos;
        layout(location = 2) flat in vec3 vRight;
        layout(location = 3) flat in vec3 vUp;
        layout(location = 4) in vec3 vFogThrough; // the air between the camera and here (fogTerms), at the quad's corners
        layout(location = 5) in vec3 vFogLight;
        layout(location = 0) out vec4 oColor;

        // How far the stirs have pushed the smoke that is now at w: so what is here is what the
        // undisturbed smoke has at w minus this. Each stir shoves the smoke outwards from its centre,
        // swirls it round, and carries it along with its drag. Also how much the stirs have
        // thinned the smoke here (0 none .. 1 all of it), strongest right at each stir.
        vec3 stirred(vec3 w, out float thinned)
        {
            vec3 offset = vec3(0.0);
            thinned = 0.0;
            int count = int(frame.stirCount.x);
            for (int i = 0; i < count; i++)
            {
                vec4 at = frame.stirs[2 * i], push = frame.stirs[2 * i + 1];
                vec3 d = w - at.xyz;
                float q = dot(d, d) / (at.w * at.w);
                if (q > 4.0)
                    continue; // out of its reach
                float f = exp(-q);
                float strength = abs(push.w);
                vec3 outward = d / max(length(d), 0.05);
                vec3 around = cross(vUp, outward) * sign(push.w); // round the planet's up there
                offset += (outward * 0.6 + around * 0.5) * at.w * f * strength + push.xyz * f;
                thinned = max(thinned, strength * exp(-2.0 * q));
            }
            return offset;
        }

        void main()
        {
            // Where this smoke has been pushed from, in the puff's own -1..1 across it.
            // (Only the ground's drifting field is stirred, params.z = 0: clouds move by themselves,
            // and pushed off their quad they would show its square edge.)
            float thinned;
            vec3 offset = stirred(vWorldPos, thinned) * (1.0 - puff.params.z);
            thinned *= 1.0 - puff.params.z;
            float halfWidth = puff.centre.w;
            vec2 local = vLocal - vec2(dot(offset, vRight), dot(offset, vUp)) / vec2(halfWidth, halfWidth * puff.shape.x);

            // An oval blob, its edge broken up by noise that churns slowly and rolls upwards.
            float t = frame.cameraPos.w;
            float seed = puff.params.x;
            vec2 p = local * vec2(1.2, 1.2 * puff.shape.x) + seed * 13.7;
            vec2 warp = vec2(fbm(p + vec2(0.0, -t * 0.12)), fbm(p + vec2(5.2, 1.3) + t * 0.07));
            float billow = fbm(p + warp * 1.2 + vec2(t * 0.03, -t * 0.09));
            float blob = smoothstep(1.0, 0.1, length(local));
            float density = blob * blob * smoothstep(0.3, 0.72, billow) * (1.0 - 0.85 * thinned);

            // Hugs the ground: thick through its lower half, thinning out above that. Its foot fades
            // to nothing at the ground too, so the ground cutting through the quad leaves no hard line.
            // (Measured where the smoke came from, so smoke kicked upwards is carried up.)
            float above = dot(vWorldPos - offset - frame.planet.xyz, vUp) - puff.shape.y; // its height over the ground there, from the planet's centre
            density *= smoothstep(0.0, puff.shape.w, above) * (1.0 - smoothstep(puff.shape.z * 0.5, puff.shape.z, above));

            float alpha = density * puff.params.y;
            if (alpha < 0.003)
                discard;

            // Sunlit, with the booster's shadow falling through it, though only partly: a flat puff
            // is a stand-in for a volume, so a full-strength shadow on it smears into streaks, where
            // real smoke would be lit from around the shadow too. Smoke scatters light forwards, so
            // it glows when the sun is behind it. And the engine flame lights it up from inside. The
            // sunlight is the air's here, so its floor in shadow goes with it (none at night).
            vec3 l = frame.sunDir.xyz;
            float visible = mix(0.45, 1.0, lightVisibility(vWorldPos, l));
            vec3 sun = sunlightAt(vWorldPos) * SUN_EXPOSURE;
            float forward = pow(max(dot(normalize(vWorldPos - frame.cameraPos.xyz), l), 0.0), 8.0);
            // With the moon's light (unshadowed), at the share the sun's has without its forward glow.
            vec3 sky = skyLightAt(vWorldPos) * 0.8 + NIGHT_LIGHT + moonlight(vWorldPos, moonDirAt(vWorldPos)) * SMOKE_LIT_SHARE;
            vec3 toFlame = frame.pointLightPos.xyz - vWorldPos;
            vec3 flame = frame.pointLight.rgb * 1.5 / (1.0 + 3.0 * dot(toFlame, toFlame) / (frame.pointLightPos.w * frame.pointLightPos.w));
            vec3 rgb = toLinear(puff.color.rgb) * (sky + sun * visible * (SMOKE_LIT_SHARE + 0.8 * forward) + flame);

            oColor = vec4(finish(rgb * vFogThrough + vFogLight), alpha);
        }
        """;

    /// <summary>Shadow pass: depth only, from the sun's point of view (the sun area of the view whose uniforms are bound: each area's first view's).</summary>
    public static string ShadowVertex => Common + MeshModel + """

        layout(location = 0) in vec3 aPosition;
        layout(location = 2) in vec2 aUv;
        layout(location = 4) in float aTextured;

        layout(location = 0) out vec2 vUv;
        layout(location = 1) out float vTextured;

        void main()
        {
            float floe;
            gl_Position = frame.lightViewProj * vec4(frame.cameraPos.xyz + (meshModel(floe) * vec4(aPosition, 1.0)).xyz, 1.0); // (placed from the camera: see meshModel)
            vUv = aUv;
            vTextured = aTextured;
        }
        """;

    /// <summary>
    /// As <see cref="ShadowVertex"/>, but seen from a floodlight (into its tile of the shadow map):
    /// which one is the draw's material.w (set while the floodlights' shadows are drawn).
    /// </summary>
    public static string SpotShadowVertex => Common + MeshModel + """

        layout(location = 0) in vec3 aPosition;
        layout(location = 2) in vec2 aUv;
        layout(location = 4) in float aTextured;

        layout(location = 0) out vec2 vUv;
        layout(location = 1) out float vTextured;

        void main()
        {
            float floe;
            gl_Position = frame.spotViewProj[int(draw.material.w + 0.5)] * vec4(frame.cameraPos.xyz + (meshModel(floe) * vec4(aPosition, 1.0)).xyz, 1.0); // (placed from the camera: see meshModel)
            vUv = aUv;
            vTextured = aTextured;
        }
        """;

    /// <summary>Shadow pass: depth only. The logo decal sits on the body, which already casts its shadow, so it casts none of its own.</summary>
    public static string ShadowFragment => Common + """

        layout(location = 0) in vec2 vUv;
        layout(location = 1) in float vTextured;

        void main()
        {
            if (vTextured > 0.5)
                discard;
        }
        """;
}
