using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;

namespace GameOne.Rendering;

// Lens flare from the sun: a glow and starburst at the sun, and "ghosts" (discs, rings and
// aperture-shaped hexagons) along the line from the sun through the centre of the screen, where a
// real lens's internal reflections land.
//
// How visible the sun is decides how strong the flare is (with how much of its light the air lets
// through and the moon leaves uncovered: Air.FlareDimming, SolarSystem.SunSeen), so it dims and
// vanishes when the booster or the tower passes in front, and in a total eclipse. (The moon is
// drawn by the sky, which writes no depth, so the probe never sees it: SunSeen is its share.)
// That comes from two occlusion queries around invisible "probe" draws of
// a flat, camera-facing disc at the sun: one depth-tested against the scene, counting the pixels
// where nothing is in front; one untested, counting every pixel the disc covers. Their ratio is the
// fraction visible, which also handles the light being partly off-screen. Results are read back a
// couple of frames later, once that frame's fence has signalled, so the CPU never waits on the GPU.
internal sealed unsafe partial class VulkanRenderer
{
    /// <summary>One flare element's push constants. Must match <c>Shaders.FlareCommon</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FlareElement
    {
        public Vector4 Rect;   // centre xy, half size zw, in clip space
        public Vector4 Color;  // tint times brightness
        public Vector4 Params; // x = shape, y = rotation
    }

    private enum FlareShape
    {
        Glow = 0,
        Starburst = 1,
        Disc = 2,
        Ring = 3,
        Hexagon = 4,
    }

    /// <summary>The ghosts: where along the light-to-centre line (0 = the light, 1 = screen centre), shape, size (fraction of screen height) and colour.</summary>
    private static readonly (float Along, FlareShape Shape, float Size, Vector3 Color)[] Ghosts =
    [
        (0.40f, FlareShape.Disc, 0.035f, new Vector3(1.00f, 0.75f, 0.45f) * 0.20f),
        (0.65f, FlareShape.Hexagon, 0.070f, new Vector3(0.55f, 1.00f, 0.60f) * 0.12f),
        (0.90f, FlareShape.Ring, 0.120f, new Vector3(0.50f, 0.70f, 1.00f) * 0.11f),
        (1.20f, FlareShape.Hexagon, 0.050f, new Vector3(0.80f, 0.55f, 1.00f) * 0.15f),
        (1.50f, FlareShape.Disc, 0.150f, new Vector3(1.00f, 0.60f, 0.35f) * 0.06f),
        (2.00f, FlareShape.Ring, 0.260f, new Vector3(0.45f, 0.90f, 1.00f) * 0.05f),
    ];

    private VkShaderModule _flareVertex, _flareFragment;
    private VkPipeline _flarePipeline, _probeVisiblePipeline, _probeCoveragePipeline;
    private VkQueryPool _queryPool;
    private readonly bool[] _queryPending = new bool[MaxFramesInFlight];
    private bool _preciseOcclusion;
    private float _lightVisibility; // smoothed, 0..1
    private float _mapShown; // how far into the map the main view is (FrameInput.MapShown)

    // From this frame's uniforms, for placing the flare.
    private Matrix4x4 _viewProj, _projection;
    private Vector3 _cameraPosition, _sunDirection, _moonCentre;
    private Vector4 _sunDisc; // the main view's (FrameInput.SunDisc)

    /// <summary>
    /// How far short of the near side of the sun's sphere the probe's disc is (a share of the way
    /// there): in front of the depth the sky writes for the sun (its sphere's), by far more than the
    /// depth's step there, so the sun never hides its own probe and anything between hides it.
    /// </summary>
    private const float SunProbeShort = 1e-4f;

    /// <summary>
    /// Where the flare's probe is, and its disc's radius, for the main view: in the way to the
    /// sun's centre from the camera (<see cref="FrameInput.SunDisc"/>), just short of its sphere's
    /// near side, as wide there as the sun's disc looks (its angular radius from the camera); so
    /// the flare is as strong as how much of the sun is left uncovered by what is nearer, the moon
    /// and the planet included, from anywhere (the map's furthest zoom too). None from inside the sun.
    /// </summary>
    private (Vector3 At, float Radius)? SunProbe()
    {
        var angular = _sunDisc.W;
        var nearSide = GameOne.Scene.SunRadius / MathF.Sin(angular) - GameOne.Scene.SunRadius;
        if (!(angular > 0f && angular < MathF.PI / 2 && nearSide > 0f))
            return null;
        var distance = nearSide * (1f - SunProbeShort);
        return (_cameraPosition + new Vector3(_sunDisc.X, _sunDisc.Y, _sunDisc.Z) * distance, distance * MathF.Tan(angular));
    }

    private void CreateFlareResources()
    {
        _flareVertex = CreateShaderModule(Shaders.FlareVertex, ShaderKind.VertexShader, "flare.vert");
        _flareFragment = CreateShaderModule(Shaders.FlareFragment, ShaderKind.FragmentShader, "flare.frag");

        // Two queries per frame in flight: [visible pixels, all pixels].
        _queryPool = _device.CreateQueryPool(
            new VkQueryPoolCreateInfo { QueryType = VkQueryType.Occlusion, QueryCount = 2 * MaxFramesInFlight },
            null
        );
    }

    /// <summary>All three draw into the scene render pass, so they are rebuilt with it.</summary>
    private void CreateFlarePipelines(VkRenderPass renderPass)
    {
        _flarePipeline = CreatePipeline(renderPass, _flareVertex, _flareFragment, _samples, PipelineStyle.Additive);
        _probeVisiblePipeline = CreatePipeline(renderPass, _flareVertex, _flareFragment, _samples, PipelineStyle.ProbeOccluded);
        _probeCoveragePipeline = CreatePipeline(renderPass, _flareVertex, _flareFragment, _samples, PipelineStyle.Probe);
    }

    private void DestroyFlarePipelines()
    {
        _device.DestroyPipeline(_flarePipeline, null);
        _device.DestroyPipeline(_probeVisiblePipeline, null);
        _device.DestroyPipeline(_probeCoveragePipeline, null);
    }

    /// <summary>
    /// Reads this frame slot's queries from when it was last rendered. Call after its fence wait:
    /// the GPU has finished that submission, so the results are ready and reading them can't stall.
    /// </summary>
    private void ReadLightVisibility()
    {
        if (!_queryPending[_frame])
            return;
        _queryPending[_frame] = false;

        Span<ulong> counts = stackalloc ulong[2];
        var result = _device.GetQueryPoolResults(
            _queryPool, 2 * _frame, 2, MemoryMarshal.AsBytes(counts), ByteSize.Of(sizeof(ulong)), VkQueryResultFlags._64Bit);
        if (result != VkResult.Success)
            return;

        var visible = counts[1] == 0 ? 0f : Math.Clamp((float)counts[0] / counts[1], 0f, 1f);
        if (!_preciseOcclusion)
            visible = counts[0] > 0 ? 1f : 0f; // without precise queries the count is only "any or none"

        // Ease towards it, so the flare fades rather than flickers as things pass in front.
        _lightVisibility += (visible - _lightVisibility) * 0.35f;
    }

    /// <summary>Queries must be reset outside a render pass before they are used: do it first thing.</summary>
    private void ResetLightQueries(CommandBuffer cb) => cb.ResetQueryPool(_queryPool, 2 * _frame, 2);

    /// <summary>
    /// Counts how much of the light is visible, with two invisible draws of a disc at its centre and
    /// depth. Call after everything that could hide it.
    /// </summary>
    private void DrawLightProbes(CommandBuffer cb, Vector3 lightPosition, float radius)
    {
        var clip = Vector4.Transform(new Vector4(lightPosition, 1f), _viewProj);
        if (clip.W <= 0)
            return; // behind the camera: no queries this frame, so visibility simply eases off

        // A disc the light's apparent size: how much of the light the scene has to cover to hide it.
        var probe = new FlareElement
        {
            Rect = new Vector4(clip.X / clip.W, clip.Y / clip.W, radius * _projection.M11 / clip.W, radius * MathF.Abs(_projection.M22) / clip.W),
            Params = new Vector4((float)FlareShape.Disc, 0f, clip.Z / clip.W, 0f),
        };

        var flags = _preciseOcclusion ? VkQueryControlFlags.PreciseBit : VkQueryControlFlags.None;
        cb.PushConstants(_pipelineLayout, AllGraphics, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<FlareElement>(in probe)));

        cb.BindPipeline(VkPipelineBindPoint.Graphics, _probeVisiblePipeline);
        cb.BeginQuery(_queryPool, 2 * _frame, flags);
        cb.Draw(6, 1, 0, 0);
        cb.EndQuery(_queryPool, 2 * _frame);

        cb.BindPipeline(VkPipelineBindPoint.Graphics, _probeCoveragePipeline);
        cb.BeginQuery(_queryPool, 2 * _frame + 1, flags);
        cb.Draw(6, 1, 0, 0);
        cb.EndQuery(_queryPool, 2 * _frame + 1);

        _queryPending[_frame] = true;
    }

    /// <summary>
    /// The share of the sun's disc as it is drawn from the main view's camera (<see cref="FrameInput.SunDisc"/>)
    /// that the moon leaves uncovered: the shaders' <c>sunSeenFromCamera</c>, its twin.
    /// </summary>
    private float SunSeenFromCamera()
    {
        var toMoon = _moonCentre - _cameraPosition;
        var sun = new Vector3(_sunDisc.X, _sunDisc.Y, _sunDisc.Z);
        var moon = MathF.Asin(MathF.Min(GameOne.Scene.MoonRadius / MathF.Max(toMoon.Length(), 1e-3f), 1f));
        var apart = MathF.Atan2(Vector3.Cross(sun, toMoon).Length(), Vector3.Dot(sun, toMoon));
        return GameOne.SolarSystem.DiscSeen(apart, _sunDisc.W, moon);
    }

    /// <summary>Draws the flare over the scene, if the light is on screen and visible at all.</summary>
    private void DrawLensFlare(CommandBuffer cb, Vector3 lightPosition, float probeDistance)
    {
        var clip = Vector4.Transform(new Vector4(lightPosition, 1f), _viewProj);
        if (clip.W <= 0)
            return; // behind the camera
        var light = new Vector2(clip.X, clip.Y) / clip.W;

        // Fade out as the light nears and leaves the edge of the screen.
        var edge = MathF.Max(MathF.Abs(light.X), MathF.Abs(light.Y));
        var onScreen = 1f - SmoothStep(0.85f, 1.25f, edge);
        // And dimmed as the air dims the sun (low in the sky, or gone under the horizon), and as the
        // moon covers it: by the share of the disc it covers, unless the moon is nearer than the probe,
        // so that its depth (the mesh's, or the sky's) hides the probe itself where it covers it (its
        // true outline: the share again would dim it twice).
        var hidesProbe = PatchBody.DrawsMoon(_moonFromCamera[0].Length())
            || Vector3.Distance(_moonCentre, _cameraPosition) - GameOne.Scene.MoonRadius < probeDistance;
        var seen = hidesProbe ? 1f : SunSeenFromCamera();
        var brightness = GameOne.Scene.FlareStrength * _lightVisibility * onScreen * GameOne.Air.FlareDimming(_cameraPosition, _sunDirection, seen)
            * MapFlare(_mapShown); // and in the map, its ghosts kept faint over the paths and labels
        if (brightness < 0.002f)
            return;

        cb.BindPipeline(VkPipelineBindPoint.Graphics, _flarePipeline);
        var aspect = (float)_extent.Width / _extent.Height;

        void Element(Vector2 at, FlareShape shape, float size, Vector3 color, float rotation = 0f)
        {
            var element = new FlareElement
            {
                // Sizes are fractions of the screen height; clip space spans 2 units each way.
                Rect = new Vector4(at, size * 2f / aspect, size * 2f),
                Color = new Vector4(color * brightness, 0f),
                Params = new Vector4((float)shape, rotation, 0f, 0f),
            };
            cb.PushConstants(_pipelineLayout, AllGraphics, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<FlareElement>(in element)));
            cb.Draw(6, 1, 0, 0);
        }

        // At the light: a glow, and a starburst that turns as the light moves across the screen.
        Element(light, FlareShape.Glow, 0.32f, new Vector3(1.0f, 0.90f, 0.75f) * 0.9f);
        Element(light, FlareShape.Starburst, 0.45f, new Vector3(1.0f, 0.95f, 0.85f) * 0.55f, rotation: light.X * 0.6f);

        // Ghosts, along the line from the light through the screen centre, a little stronger the
        // closer the light is to the centre, as with a real lens.
        var central = 0.5f + 0.5f * (1f - MathF.Min(light.Length(), 1f));
        foreach (var (along, shape, size, color) in Ghosts)
            Element(light * (1f - along), shape, size, color * central, rotation: 0.3f);
    }

    /// <summary>The share of the lens flare left <paramref name="mapShown"/> of the way into the map: all of it by the vessel, <see cref="GameOne.Scene.MapFlareShare"/> in the map.</summary>
    internal static float MapFlare(float mapShown) => 1f - Math.Clamp(mapShown, 0f, 1f) * (1f - GameOne.Scene.MapFlareShare);

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private void DestroyFlareResources()
    {
        _device.DestroyQueryPool(_queryPool, null);
        _device.DestroyShaderModule(_flareVertex, null);
        _device.DestroyShaderModule(_flareFragment, null);
    }
}
