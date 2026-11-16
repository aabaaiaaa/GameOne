using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;

namespace GameOne.Rendering;

// Fire: the engine's exhaust plume, its glow at the nozzle and where it splashes onto the ground,
// and the fireballs of an explosion. All are procedural, added onto the scene (so they light up what
// is behind them, smoke included) and depth-tested, so the booster and the ground hide them.
internal sealed unsafe partial class VulkanRenderer
{
    /// <summary>One glow's push constants. Must match <c>Shaders.GlowCommon</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GlowConstants
    {
        public Vector4 Origin; // xyz, w = length (plume) or radius (fireball)
        public Vector4 Axis;   // xyz = direction (plume), w = half width
        public Vector4 Params; // x = kind (0 plume, 1 fireball), y = brightness, z = heat, w = seed
    }

    private VkShaderModule _glowVertex, _glowFragment;
    private VkPipeline _glowPipeline;

    private void CreateGlowResources()
    {
        _glowVertex = CreateShaderModule(Shaders.GlowVertex, ShaderKind.VertexShader, "glow.vert");
        _glowFragment = CreateShaderModule(Shaders.GlowFragment, ShaderKind.FragmentShader, "glow.frag");
    }

    /// <summary>Draws into the scene render pass, so it is rebuilt with it.</summary>
    private void CreateGlowPipeline(VkRenderPass renderPass) =>
        _glowPipeline = CreatePipeline(renderPass, _glowVertex, _glowFragment, _samples, PipelineStyle.Glow);

    private void DestroyGlowPipeline() => _device.DestroyPipeline(_glowPipeline, null);

    /// <summary>The fire and the lamps' far glow, for the view whose camera is at <paramref name="camera"/> (the lamps' glow is sized by how far they are from it).</summary>
    private void DrawGlows(CommandBuffer cb, in FrameInput input, Vector3 camera)
    {
        cb.BindPipeline(VkPipelineBindPoint.Graphics, _glowPipeline);

        void Glow(Vector3 origin, float size, Vector3 axis, float halfWidth, float kind, float brightness, float heat, float seed)
        {
            var constants = new GlowConstants
            {
                Origin = new Vector4(origin, size),
                Axis = new Vector4(axis, halfWidth),
                Params = new Vector4(kind, brightness, heat, seed),
            };
            cb.PushConstants(_pipelineLayout, AllGraphics, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<GlowConstants>(in constants)));
            cb.Draw(6, 1, 0, 0);
        }

        foreach (var plume in input.Plumes)
        {
            if (plume.Throttle <= 0f)
                continue;
            // It flickers a little in length, and grows with the throttle.
            var flicker = 0.92f + 0.08f * MathF.Sin((float)input.Time * 47f) * MathF.Sin((float)input.Time * 29f);
            // A smaller engine makes a smaller plume; in thin air, it billows out wider and longer, and fainter.
            var size = plume.BellRadius / GameOne.Scene.RocketBellRadius;
            var length = GameOne.Scene.PlumeLength * size * (0.35f + 0.65f * plume.Throttle) * flicker * (1f + 0.6f * plume.Vacuum);
            var width = plume.BellRadius * 2.4f * (1f + 2.5f * plume.Vacuum);
            var brightness = (0.55f + 0.45f * plume.Throttle) * (1f - 0.45f * plume.Vacuum);
            Glow(plume.Nozzle, length, plume.Direction, width, 0f, brightness, 1f, 3f);
            Glow(plume.Nozzle, plume.BellRadius * 2.5f, default, 0f, 1f, 0.5f * brightness, 1f, 11f);

            // Where it hits the ground, it splashes out in a bright, flattened glow.
            // (Measured to what it hits: on a tilted test pad, along the top's normal.)
            var (height, normal) = GameOne.Planet.Surface(plume.Nozzle);
            var down = -Vector3.Dot(plume.Direction, normal);
            if (down > 0.3f)
            {
                var reach = height / down;
                if (reach < length)
                {
                    var closeness = 1f - reach / length;
                    var splash = plume.Nozzle + plume.Direction * reach + normal;
                    Glow(splash, 3f + 7f * closeness * plume.Throttle, default, 0f, 1f, 0.7f * closeness * brightness, 0.8f, 23f);
                }
            }
        }

        foreach (var fireball in input.Fireballs)
            Glow(fireball.Centre, fireball.Radius, default, 0f, 1f, fireball.Brightness, fireball.Heat, fireball.Seed);

        // Each floodlight and tower light, seen from afar: a point of light a few pixels across however
        // far it is, so a pad, zone or ship can be found at night; as far on as its place's lights, none when off.
        var colour = GameOne.Scene.LampColour;
        var places = PadLights.All;
        for (var p = 0; p < places.Count; p++) // (by index: a foreach over the list would allocate, every frame)
        {
            if (input.PlaceLit[p] <= 0f)
                continue;
            var place = places[p];
            var brightness = GameOne.Scene.LampGlowBrightness * input.PlaceLit[p];
            foreach (var lamp in place.Lamps)
            {
                if (lamp.Kind == LampKind.Floor)
                    continue; // (hundreds: their own faces show the outline near by, and the floods mark the place from afar)
                var at = place.ToWorld(lamp.Position);
                var size = MathF.Max(0.25f, Vector3.Distance(camera, at) * GameOne.Scene.GlowPixelPerMetre * GameOne.Scene.LampGlowPixels);
                Glow(at, size, default, 0f, 2f, brightness, colour.Y, colour.Z);
            }
        }

        // Every vessel's landing lights, seen from afar as a floodlight is: one instanced draw for
        // them all (there can be hundreds), each instance's place and fraction from the vessels'
        // glow buffer (written for this frame in RenderFrame), sized in the shader by its distance
        // from this view's camera.
        if (_vesselGlowCount > 0)
        {
            var constants = new GlowConstants
            {
                Axis = new Vector4(GameOne.Scene.LandingLightColour, 0f),
                Params = new Vector4(3f, GameOne.Scene.LandingLightGlowBrightness, 0f, 0f),
            };
            cb.PushConstants(_pipelineLayout, AllGraphics, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<GlowConstants>(in constants)));
            cb.Draw(6, _vesselGlowCount, 0, 0);
        }
    }

    private void DestroyGlowResources()
    {
        _device.DestroyShaderModule(_glowVertex, null);
        _device.DestroyShaderModule(_glowFragment, null);
    }
}
