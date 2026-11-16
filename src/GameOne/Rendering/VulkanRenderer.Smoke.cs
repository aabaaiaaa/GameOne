using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;

namespace GameOne.Rendering;

// Smoke: soft, upright puffs lying across the ground round the pad, drifting on a slow breeze and
// each swelling and thinning as it goes, which the engine's exhaust thickens into a billowing cloud;
// and the clouds from an explosion. Each puff is one camera-facing quad (built in the vertex shader,
// so no vertex buffer) whose billowing comes from noise in the fragment shader. They are lit by the
// sun and the engine, and catch the booster's shadow, through the shared shadow map.
//
// The ground puffs have no state: where each one is and how thick it is are worked out from the time
// alone, wrapping round to the upwind side once it has drifted across.
internal sealed unsafe partial class VulkanRenderer
{
    /// <summary>One puff's push constants. Must match <c>Shaders.SmokeCommon</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SmokePuffConstants
    {
        public Vector4 Centre; // world xyz, w = half width
        public Vector4 Params; // x = seed, y = opacity, z = 1 for a cloud (not stirred)
        public Vector4 Shape;  // x = height / width, y = the ground's distance from the planet's centre, z = fade-out height above it, w = fade-in height
        public Vector4 Color;
    }

    /// <summary>A puff's fixed character: where it starts, how big it is, and how it drifts and breathes.</summary>
    private readonly record struct SmokePuff(
        float Along, float Across, float HalfWidth, float Lift, float Speed, float Seed, float Phase, float BreathSeconds);

    private static readonly SmokePuff[] SmokePuffs = CreateSmokePuffs();

    private VkShaderModule _smokeVertex, _smokeFragment;
    private VkPipeline _smokePipeline;

    private static SmokePuff[] CreateSmokePuffs()
    {
        var random = new Random(7); // the same smoke every run
        float Range(float min, float max) => min + (float)random.NextDouble() * (max - min);

        var puffs = new SmokePuff[GameOne.Scene.SmokePuffCount];
        for (var i = 0; i < puffs.Length; i++)
        {
            var halfWidth = Range(GameOne.Scene.SmokeRadiusMin, GameOne.Scene.SmokeRadiusMax);
            puffs[i] = new SmokePuff(
                Along: Range(-1f, 1f) * GameOne.Scene.SmokeExtent,
                Across: Range(-1f, 1f) * GameOne.Scene.SmokeSpread,
                HalfWidth: halfWidth,
                // Raised enough that the taller puffs' tops reach up to the smoke's full height.
                Lift: halfWidth * GameOne.Scene.SmokeSquash * Range(0.3f, 0.7f),
                Speed: Range(0.75f, 1.25f), // so they don't all move in lockstep
                Seed: Range(0f, 100f),
                Phase: Range(0f, 1f),
                BreathSeconds: Range(9f, 18f));
        }
        return puffs;
    }

    private void CreateSmokeResources()
    {
        _smokeVertex = CreateShaderModule(Shaders.SmokeVertex, ShaderKind.VertexShader, "smoke.vert");
        _smokeFragment = CreateShaderModule(Shaders.SmokeFragment, ShaderKind.FragmentShader, "smoke.frag");
    }

    /// <summary>Draws into the scene render pass, so it is rebuilt with it.</summary>
    private void CreateSmokePipeline(VkRenderPass renderPass) =>
        _smokePipeline = CreatePipeline(renderPass, _smokeVertex, _smokeFragment, _samples, PipelineStyle.Translucent);

    private void DestroySmokePipeline() => _device.DestroyPipeline(_smokePipeline, null);

    /// <summary>Draws the ground smoke and any explosion clouds together, farthest first, so nearer smoke blends over farther.</summary>
    private void DrawSmoke(CommandBuffer cb, in FrameInput input)
    {
        var wind = GameOne.Scene.SmokeWind;
        var windSpeed = wind.Length();
        var downwind = wind / windSpeed;
        var crosswind = new Vector2(-downwind.Y, downwind.X);
        var extent = GameOne.Scene.SmokeExtent;
        var t = (float)input.Time;

        // The same drifting field of puffs lies round each pad and zone near enough to be seen,
        // thickened by the engine separately, laid across the ground there: its x and z are the
        // site's east and south (Home's, the world's), its up the planet's there.
        var sites = GameOne.LaunchSites.SmokeSites;
        Span<int> nearSites = stackalloc int[sites.Length];
        var nearCount = 0;
        for (var s = 0; s < sites.Length; s++)
            if (Vector3.Distance(sites[s], input.CameraPosition) < GameOne.Scene.SmokeDrawDistance)
                nearSites[nearCount++] = s;
        var ground = SmokePuffs.Length * nearCount;
        var count = ground + input.Clouds.Length;
        Span<SmokePuffConstants> puffs = stackalloc SmokePuffConstants[count];
        Span<float> distances = stackalloc float[count];

        // Each puff's drift and breathing is the same at every site, so it is worked out once a
        // frame (and only when a site is near), not once for each site it is laid at.
        var fieldCount = nearCount > 0 ? SmokePuffs.Length : 0;
        Span<Vector2> drifts = stackalloc Vector2[fieldCount]; // across the ground: east, south
        Span<float> shades = stackalloc float[fieldCount];     // its opacity before the site's thickness
        Span<float> clouds = stackalloc float[fieldCount];     // how much of the site's engine cloud it carries
        for (var p = 0; p < fieldCount; p++)
        {
            var puff = SmokePuffs[p];

            // Drift downwind, wrapping round to the upwind edge, with a slow sideways meander.
            var along = Wrap(puff.Along + windSpeed * puff.Speed * t, extent);
            var across = puff.Across + 4f * MathF.Sin(t * 0.11f + puff.Phase * MathF.Tau);
            var xz = downwind * along + crosswind * across;
            drifts[p] = xz;

            // Swells and thins as it goes, and fades out before it wraps (and in after), so it never pops.
            var breath = 0.5f - 0.5f * MathF.Cos(MathF.Tau * (t / puff.BreathSeconds + puff.Phase));
            var edge = 1f - SmoothStep(0.7f, 1f, MathF.Abs(along) / extent);
            shades[p] = GameOne.Scene.SmokeOpacity * (0.35f + 0.65f * breath) * edge;
            // The engine's cloud is thickest round the pad, where it was made, thinning out to the haze.
            clouds[p] = MathF.Exp(-xz.LengthSquared() / (GameOne.Scene.SmokeCloudRadius * GameOne.Scene.SmokeCloudRadius));
        }

        for (var n = 0; n < nearCount; n++)
        {
            // The site's frame, once for all its puffs.
            var site = nearSites[n];
            var siteCentre = sites[site];
            var (east, south, up) = (GroundBody.Planet.East(siteCentre), -GroundBody.Planet.North(siteCentre), GroundBody.Planet.Up(siteCentre));
            var groundRadius = Vector3.Distance(siteCentre, GroundBody.Planet.Centre); // the ground there, as far from the centre as the site
            var siteThickness = input.SmokeThickness[site];
            for (var p = 0; p < fieldCount; p++)
            {
                var puff = SmokePuffs[p];
                var xz = drifts[p];
                var centre = siteCentre + east * xz.X + south * xz.Y + up * puff.Lift;
                var opacity = shades[p] * float.Lerp(GameOne.Scene.SmokeHaze, siteThickness, clouds[p]);
                var i = n * fieldCount + p;
                puffs[i] = new SmokePuffConstants
                {
                    Centre = new Vector4(centre, puff.HalfWidth),
                    Params = new Vector4(puff.Seed, opacity, 0f, 0f),
                    Shape = new Vector4(GameOne.Scene.SmokeSquash, groundRadius, GameOne.Scene.SmokeHeight, 1f),
                    Color = new Vector4(GameOne.Scene.SmokeColor, 1f),
                };
                distances[i] = -Vector3.DistanceSquared(centre, input.CameraPosition); // ascending sort: farthest first
            }
        }

        // Explosion clouds (and engines' trails) fade in at the ground but never out above it. One
        // the camera is inside fades away: seen from within, a flat puff is just a sheet across the
        // view, and it would cover every pixel of it, which is what costs most to draw.
        for (var i = 0; i < input.Clouds.Length; i++)
        {
            var cloud = input.Clouds[i];
            var near = Vector3.Distance(cloud.Centre, input.CameraPosition) / cloud.Radius;
            puffs[ground + i] = new SmokePuffConstants
            {
                Centre = new Vector4(cloud.Centre, cloud.Radius),
                Params = new Vector4(cloud.Seed, cloud.Opacity * SmoothStep(0.8f, 2f, near), 1f, 0f),
                Shape = new Vector4(cloud.Squash, cloud.Ground, 1e6f, 2f),
                Color = new Vector4(cloud.Color, 1f),
            };
            distances[ground + i] = -Vector3.DistanceSquared(cloud.Centre, input.CameraPosition);
        }
        distances.Sort(puffs);

        cb.BindPipeline(VkPipelineBindPoint.Graphics, _smokePipeline);
        foreach (ref readonly var puff in puffs)
        {
            if (puff.Params.Y < 0.002f)
                continue;
            cb.PushConstants(_pipelineLayout, AllGraphics, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<SmokePuffConstants>(in puff)));
            cb.Draw(6, 1, 0, 0);
        }
    }

    /// <summary>Wraps <paramref name="x"/> into -<paramref name="half"/>..<paramref name="half"/>.</summary>
    private static float Wrap(float x, float half)
    {
        var span = 2f * half;
        return x - span * MathF.Floor((x + half) / span);
    }

    private void DestroySmokeResources()
    {
        _device.DestroyShaderModule(_smokeVertex, null);
        _device.DestroyShaderModule(_smokeFragment, null);
    }
}
