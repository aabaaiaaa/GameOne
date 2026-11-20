using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using GameOne.Physics;
using static Brutal.GlfwApi.Glfw.Constants;

namespace GameOne.Rendering;

/// <summary>
/// Vulkan 1.3 renderer for the scene: the booster over its launch pad, in sunlight with real-time
/// shadows, under a sky that fades into the distance. GLSL (see <see cref="Shaders"/>) is compiled to
/// SPIR-V at start-up with ShaderC, so no offline shader toolchain (Vulkan SDK) is required.
/// </summary>
/// <remarks>
/// Each frame records two render passes into one command buffer: the shadow pass (depth from the
/// sun, see VulkanRenderer.Shadows.cs) and the scene pass (4x MSAA resolved into the swapchain,
/// see VulkanRenderer.Swapchain.cs). The small views are drawn into the scene pass too, each into
/// its own rectangle, after the main view (see VulkanRenderer.Views.cs).
/// <see cref="MaxFramesInFlight"/> frames can be in flight.
/// </remarks>
internal sealed unsafe partial class VulkanRenderer : IDisposable
{
    private const int MaxFramesInFlight = 2;

    /// <summary>Per-frame values shared by every draw. std140: only mat4/vec4 members, so C# layout matches.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewRays;   // a pixel's clip-space point to its view ray's direction (ViewRays)
        public Matrix4x4 LightViewProj; // this view's sun area's view
        public Vector4 SunArea;         // where that area is in the shadow map: xy its corner, zw its size (fractions of the map)
        public Vector4 SunDir;          // w = shadow lookup nudge
        public Vector4 SunColor;
        public Vector4 CameraPos;       // w = time
        public Vector4 PointLightPos;   // w = range
        public Vector4 PointLight;
        public Vector4 Pad;             // x = launch pad half size, y = top, z = landing target radius, w = landing zone radius
        public Vector4 SiteCount;       // x = how many of PadSites are in use, y = of ZoneSites
        public SiteArray PadSites;      // xyz = each launch pad's centre (LaunchSites.All)
        public SiteArray PadEasts;      // xyz = each launch pad's east (its frame's, LaunchSite.Frame), worked out once
        public SiteArray PadNorths;     // xyz = and its north
        public SiteArray ZoneSites;     // xyz = each landing zone's centre, but the test pads'
        public Vector4 TestRangeStart;  // xyz = the ground under the first test pad
        public Vector4 TestRangeEnd;    // xyz = and under the last
        public Vector4 Planet;          // xyz = centre, w = the sea's radius (heights are above the sea)
        public Vector4 Atmosphere;      // x = height, y = scale height (both from the sea); z = how far the stars have turned (FrameInput.StarTurn), w = the sun's disc's angular radius (radians)
        public Vector4 Moon;            // xyz = the moon's centre from this view's camera, w = its radius
        public Matrix4x4 MoonFrame;     // the ground's frame into the moon's own axes (FrameInput.MoonOrientation turned back): a rotation, the same in every view
        public Vector4 Moonlight;       // rgb = what the moon gives back of the sunlight (FrameInput.MoonShine, the same in every view); w = 1 where this view draws the moon as a mesh (PatchBody.DrawsMoon), so the sky leaves it out
        public Vector4 SunDisc;         // xyz = the way to the sun's centre from this view's camera (worked out in double: far out, it is not the sun's direction from the planet), w = its disc's angular radius from there (FrameInput.SunDisc)
        public Vector4 Averaged;        // the averaged light: x = its weight (FrameInput.LightAveraged), y = its sun's strength (Scene.AveragedSunStrength), z = the sine of its sky's sun's height (Scene.AveragedSunElevationDegrees)
        public Vector4 Terrain;         // x = map resolution, y = min height, z = max height (above the sea), w = the noise's scale (Scene.DetailScale)
        public Vector4 CameraClip;      // the camera in clip space: the projection's last row
        public WavePhaseArray WavePhases; // the sea's waves' phases (Sea.Phases), four to a vec4
        public Vector4 StirCount;       // x = how many of Stirs are in use
        public StirArray Stirs;
        public Vector4 LightCount;      // x = how many of the lights below are in use (this view's LightSet), y = how many of the first cast shadows, z = the first's tile (its SpotViewProj)
        public LightArray LightPlaces;  // xyz = each light's place, w = its range
        public LightArray LightColours; // rgb = its colour times brightness, w = the cosine its beam fades out at (below -1: all round)
        public LightArray LightAims;    // xyz = its beam's way, w = the cosine within which it is full
        public SpotArray SpotViewProj;  // every floodlight tile's view (a block of four per light set with floods), for their shadows: the same in every view's
    }

    /// <summary>The waves' phases at the world's time, in <see cref="Sea.Waves"/>' order: the shader's <c>wavePhases</c>.</summary>
    [System.Runtime.CompilerServices.InlineArray(Sea.WaveCount / 4)]
    private struct WavePhaseArray
    {
        private Vector4 _first;
    }

    /// <summary>The booster's stirs of the smoke, two vec4s each (see <see cref="SetStirs"/>).</summary>
    [System.Runtime.CompilerServices.InlineArray(2 * SmokeStirs.MaxStirs)]
    private struct StirArray
    {
        private Vector4 _first;
    }

    /// <summary>A view for each floodlight tile of the shadow map: <see cref="PadLights.MaxShadowed"/> in each of its <see cref="ShadowAreas"/> blocks (<c>spotViewProj</c>).</summary>
    [System.Runtime.CompilerServices.InlineArray(PadLights.MaxShadowed * ShadowAreas)]
    private struct SpotArray
    {
        private Matrix4x4 _first;
    }

    /// <summary>A vec4 for each of up to <see cref="PadLights.MaxLights"/> lights (<c>lightPlaces</c>, <c>lightColours</c>, <c>lightAims</c>).</summary>
    [System.Runtime.CompilerServices.InlineArray(PadLights.MaxLights)]
    private struct LightArray
    {
        private Vector4 _first;
    }

    /// <summary>The most launch pads, and landing zones (but the test pads), the shader's apron is drawn round: its <c>padSites[8]</c> and <c>zoneSites[8]</c>.</summary>
    internal const int MaxSites = 8;

    /// <summary>A place for each of up to <see cref="MaxSites"/> sites (<c>padSites</c>, <c>zoneSites</c>).</summary>
    [System.Runtime.CompilerServices.InlineArray(MaxSites)]
    private struct SiteArray
    {
        private Vector4 _first;
    }

    /// <summary>Packs the stirs as the smoke shader reads them: centre and radius, then drag and strength.</summary>
    private static void SetStirs(ref FrameUniforms uniforms, ReadOnlySpan<SmokeStir> stirs)
    {
        var count = Math.Min(stirs.Length, SmokeStirs.MaxStirs);
        for (var i = 0; i < count; i++)
        {
            var stir = stirs[i];
            uniforms.Stirs[2 * i] = new Vector4(stir.Position, stir.Radius);
            uniforms.Stirs[2 * i + 1] = new Vector4(stir.Drag, stir.Strength);
        }
        uniforms.StirCount = new Vector4(count, 0, 0, 0);
    }

    /// <summary>Packs a view's lights (its <see cref="LightSet"/>; none for <c>default</c>) as the shaders read them, with where its floods' shadows are.</summary>
    private static void SetLights(ref FrameUniforms uniforms, in LightSet set)
    {
        var lights = set.Lights.Span;
        var count = Math.Min(lights.Length, PadLights.MaxLights);
        for (var i = 0; i < count; i++)
        {
            var light = lights[i];
            uniforms.LightPlaces[i] = new Vector4(light.Position, light.Range);
            uniforms.LightColours[i] = new Vector4(light.Colour, light.CosOuter); // display values: the shader makes them linear
            uniforms.LightAims[i] = new Vector4(light.Aim, light.CosInner);
        }
        // The first few (its place's floodlights) cast shadows, from its block's tiles.
        var shadowed = Shadowed(set);
        uniforms.LightCount = new Vector4(count, shadowed, shadowed > 0 ? set.Block * PadLights.MaxShadowed : 0, 0);
    }

    /// <summary>How many of <paramref name="set"/>'s first lights cast shadows: as many as it says, within what it has and what a block holds; none with no block.</summary>
    private static int Shadowed(in LightSet set) =>
        set.Block is >= 0 and < ShadowAreas ? Math.Min(Math.Min(set.Shadowed, Math.Min(set.Lights.Length, PadLights.MaxLights)), PadLights.MaxShadowed) : 0;

    /// <summary>
    /// Every light set's floodlights' views (a cone along each one's beam), each into its tile of its
    /// set's block: <c>spotViewProj</c>, the same in every view's uniforms, as the shadow pass draws
    /// every block, whichever view's it is.
    /// </summary>
    private static void SetSpots(ref FrameUniforms uniforms, ReadOnlySpan<LightSet> sets)
    {
        foreach (var set in sets)
        {
            var lights = set.Lights.Span;
            for (var i = 0; i < Shadowed(set); i++)
            {
                var light = lights[i];
                var up = MathF.Abs(Vector3.Dot(light.Aim, GameOne.Planet.Up(light.Position))) > 0.99f ? GameOne.Planet.North(light.Position) : GameOne.Planet.Up(light.Position);
                // From just out along its beam: clear of its own mast's crossbar and housings, which would shadow everything.
                var eye = light.Position + light.Aim * 1.2f;
                var view = Matrix4x4.CreateLookAt(eye, eye + light.Aim, up);
                uniforms.SpotViewProj[set.Block * PadLights.MaxShadowed + i] = view * PerspectiveVulkan(2f * MathF.Acos(light.CosOuter) + 0.1f, 1f, 0.5f, light.Range);
            }
        }
    }

    /// <summary>Per-draw values, as push constants.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public Matrix4x4 Model;
        public Vector4 Material; // x = MaterialKind, yzw = tint multiplied into the mesh colour (a lamp's: none, w its place's lit fraction)
    }

    private enum MaterialKind
    {
        Lit = 0,
        Ground = 1,
        Pad = 2,
        LandingZone = 3,
        Water = 4,
        Deck = 5,
        Ice = 6,
        Lamp = 7, // a lamp's face: it shines (the scene shader's KindLamp)
    }

    private const VkShaderStageFlags AllGraphics = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private readonly GlfwWindow _window;
    private readonly Instance _instance;
    private readonly VkSurfaceKHR _surface;
    private readonly PhysicalDevice _physicalDevice;
    private readonly DeviceEx _device;
    private readonly Queue _queue;
    private readonly VkSampleCountFlags _samples;
    private readonly VkFormat _depthFormat;

    private readonly VkCommandPool _commandPool;
    private readonly CommandBuffer[] _commandBuffers = new CommandBuffer[MaxFramesInFlight];
    private readonly VkFence[] _inFlight = new VkFence[MaxFramesInFlight];
    private readonly VkSemaphore[] _imageAvailable = new VkSemaphore[MaxFramesInFlight];
    private int _frame;

    // Meshes: one vertex buffer holding them all, back to back.
    private readonly VkBuffer _vertexBuffer;
    private readonly VkDeviceMemory _vertexMemory;
    private readonly MeshRange[] _landingZoneMeshes, _padMeshes; // one per landing zone (Foundations.LandingZonePlinth), one per launch site (LaunchSites.All)
    private readonly MeshRange[] _fittingMeshes, _lampMeshes; // one each per lit place (PadLights.All): its masts and housings, and its lamps' faces
    private readonly MeshRange _towerMesh, _towerDeckMesh, _droneShipMesh, _floeMesh, _boosterMesh, _boosterStackMesh, _upperStageMesh, _upperStageOpenMesh, _satelliteMesh, _landerMesh;
    private readonly MeshRange[] _landerPanelMeshes; // each of a bay door's ramp's panels, by its index
    // One fairing half and one leg mesh per shape of either among the vehicles (Vehicle.All), each drawn for the stages of its shape.
    private readonly (FairingGeometry Shape, MeshRange Mesh)[] _fairingHalfMeshes;
    private readonly (LegGeometry Shape, MeshRange Mesh)[] _legMeshes;
    private readonly GpuTexture _logoTexture;

    // The floes to draw (FrameInput.Floes): per frame in flight, a storage buffer (kept mapped) of
    // their models, which the floe mesh is drawn instanced from (one draw for them all: there can
    // be well over a thousand in sight), and how many of them there are this frame. Room for every
    // floe in the world, as each is drawn once at most.
    private readonly VkBuffer[] _floeBuffers = new VkBuffer[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _floeMemory = new VkDeviceMemory[MaxFramesInFlight];
    private readonly Matrix4x4*[] _floeMapped = new Matrix4x4*[MaxFramesInFlight];
    private readonly int _floeCapacity = Math.Max(1, GameOne.Floes.Count);
    private int _floeCount;

    // The vessels' landing lights' far glows (FrameInput.VesselGlows): per frame in flight, a
    // storage buffer (kept mapped) of each one's place (xyz) and how far on it is with its twinkle's
    // seed (w: see GlowW), which the glow is drawn instanced from (one draw a view for them all),
    // and how many this frame. Room for every vessel a world holds.
    private readonly VkBuffer[] _vesselGlowBuffers = new VkBuffer[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _vesselGlowMemory = new VkDeviceMemory[MaxFramesInFlight];
    private readonly Vector4*[] _vesselGlowMapped = new Vector4*[MaxFramesInFlight];
    private const int VesselGlowCapacity = Flight.Capacity;
    private int _vesselGlowCount;

    /// <summary>How many seeds a vessel's glow twinkles by (its id, wrapped): few enough that twice one plus its fraction is exact in a float.</summary>
    internal const int GlowSeeds = 4096;

    /// <summary>
    /// A vessel's glow's w in the glow buffer: twice its seed (its id, wrapped to <see cref="GlowSeeds"/>)
    /// plus how far on it is (0 to 1), so the shader has both from one float (seed = floor(w / 2),
    /// lit = w - 2 seed). Its twinkle is seeded by the vessel, not where it is, which it moves on from
    /// every frame (a lander carried by the moon, a booster coming down at night).
    /// </summary>
    internal static float GlowW(in VesselLight light) => 2f * (light.Id % GlowSeeds) + Math.Clamp(light.Lit, 0f, 1f);

    /// <summary>The main view and the small ones: each has its own camera, so its own frame uniforms.</summary>
    private const int MaxViews = 1 + GameOne.Scene.MaxSmallViews;

    // Each view's camera this frame (0 the main one's), and the one whose uniforms are bound (BindView):
    // every model is pushed from it (see Draw), as the shaders place it from frame.cameraPos.
    private readonly Vector3[] _eyes = new Vector3[MaxViews];
    private Vector3 _eye;

    /// <summary>Binds view <paramref name="view"/>'s uniforms (0 the main view's, 1 + i small view i's), and draws from its camera after.</summary>
    private void BindView(CommandBuffer cb, int view)
    {
        cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0, [DescriptorSet(_frame, view)], []);
        _eye = _eyes[view];
    }

    // Per frame in flight: a uniform buffer (kept mapped) holding each view's uniforms, one after
    // another _uniformStride bytes apart, and a descriptor set per view pointing at its uniforms
    // (the main view's is view 0). See DescriptorSet.
    private readonly VkBuffer[] _uniformBuffers = new VkBuffer[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _uniformMemory = new VkDeviceMemory[MaxFramesInFlight];
    private readonly byte*[] _uniformMapped = new byte*[MaxFramesInFlight];
    private readonly VkDescriptorSet[] _descriptorSets = new VkDescriptorSet[MaxFramesInFlight * MaxViews];
    private readonly int _uniformStride;
    private readonly DescriptorSetLayoutEx _setLayout;
    private readonly DescriptorPoolEx _descriptorPool;
    private readonly VkPipelineLayout _pipelineLayout;

    private readonly VkShaderModule _sceneVertex, _sceneFragment, _shadowVertex, _shadowFragment, _spotShadowVertex;

    // The HUD: BRUTAL's Dear ImGui Vulkan backend, drawing into the scene pass after everything else.
    private readonly VulkanBackend _ui;

    /// <summary>The official logo image, for the HUD to show with <see cref="ImGui.Image"/>.</summary>
    public ImTextureRef LogoTexture { get; }

    /// <summary>The logo image's size in pixels.</summary>
    public Brutal.Numerics.float2 LogoTextureSize => new(_logoTexture.Width, _logoTexture.Height);

    /// <summary>Window hints required before the window is created.</summary>
    public static void ApplyWindowHints() => Glfw.WindowHint(GlfwWindowHint.ClientApi, GLFW_NO_API);

    /// <summary>Whether frames wait for the display's refresh. Off draws as fast as the GPU can (for soak runs), where the driver allows it.</summary>
    private bool _vsync;

    /// <summary>
    /// Whether frames wait for the display's refresh (see <see cref="_vsync"/>). Changing it takes
    /// effect at the next frame, as a resize does: the swapchain is made again with the other mode.
    /// </summary>
    public bool VSync
    {
        get => _vsync;
        set
        {
            if (value == _vsync)
                return;
            _vsync = value;
            _swapchainDirty = true;
        }
    }

    public VulkanRenderer(GlfwWindow window, bool vsync = true)
    {
        _window = window;
        _vsync = vsync;
        _window.OnFramebufferSize += (_, _) => _swapchainDirty = true;

        _instance = CreateInstance();
        _surface = CreateSurface(_instance, window);
        (_physicalDevice, var queueFamily, var timestampBits) = PickPhysicalDevice(_instance, _surface);
        _device = CreateDevice(_physicalDevice, queueFamily, out _preciseOcclusion);
        _queue = _device.GetQueue(queueFamily, 0);

        var props = _physicalDevice.GetProperties();
        _samples = (props.Limits.FramebufferColorSampleCounts & VkSampleCountFlags._4Bit) != 0
            ? VkSampleCountFlags._4Bit
            : VkSampleCountFlags._1Bit;
        _depthFormat = CameraDepthFormat(_physicalDevice);
        _shadowFormat = ChooseDepthFormat(_physicalDevice, sampled: true);
        Console.WriteLine($"Vulkan: {props.ApiVersion >> 22}.{(props.ApiVersion >> 12) & 0x3FF}.{props.ApiVersion & 0xFFF}");
        Console.WriteLine($"GPU:    {Marshal.PtrToStringUTF8((nint)props.DeviceName)} ({props.DeviceType}), MSAA {_samples}");

        _commandPool = _device.CreateCommandPool(
            new VkCommandPoolCreateInfo
            {
                QueueFamilyIndex = queueFamily,
                Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
            },
            null
        );

        // Each view's uniforms start where a descriptor set is allowed to point.
        var alignment = Math.Max(1, (int)props.Limits.MinUniformBufferOffsetAlignment);
        _uniformStride = (sizeof(FrameUniforms) + alignment - 1) / alignment * alignment;
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            _commandBuffers[i] = _device.AllocateCommandBuffer(
                new VkCommandBufferAllocateInfo { CommandPool = _commandPool, Level = VkCommandBufferLevel.Primary }
            );
            _inFlight[i] = _device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.SignaledBit }, null);
            _imageAvailable[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            (_uniformBuffers[i], _uniformMemory[i], var mapped) = CreateHostBuffer(
                ByteSize.Of(_uniformStride * MaxViews), VkBufferUsageFlags.UniformBufferBit);
            _uniformMapped[i] = (byte*)mapped;
            (_floeBuffers[i], _floeMemory[i], var floes) = CreateHostBuffer(
                ByteSize.Of(sizeof(Matrix4x4) * _floeCapacity), VkBufferUsageFlags.StorageBufferBit);
            _floeMapped[i] = (Matrix4x4*)floes;
            (_vesselGlowBuffers[i], _vesselGlowMemory[i], var glows) = CreateHostBuffer(
                ByteSize.Of(sizeof(Vector4) * VesselGlowCapacity), VkBufferUsageFlags.StorageBufferBit);
            _vesselGlowMapped[i] = (Vector4*)glows;
        }

        _logoTexture = new GpuTexture(_device, _queue, _commandPool, Path.Combine(AppContext.BaseDirectory, RocketMesh.TexturePath));
        (_vertexBuffer, _vertexMemory) = CreateMeshes(out _padMeshes, out _landingZoneMeshes, out _fittingMeshes, out _lampMeshes, out _towerMesh, out _towerDeckMesh, out _droneShipMesh, out _floeMesh, out _boosterMesh, out _boosterStackMesh, out _upperStageMesh, out _upperStageOpenMesh, out _satelliteMesh, out _landerMesh, out _landerPanelMeshes, out _fairingHalfMeshes, out _legMeshes);

        _setLayout = CreateSetLayout();
        var pushRange = new VkPushConstantRange { StageFlags = AllGraphics, Size = (ByteSize32)sizeof(DrawConstants) };
        _pipelineLayout = _device.CreatePipelineLayout([_setLayout], [pushRange], null);

        CreateTerrainResources();
        _sceneVertex = CreateShaderModule(Shaders.SceneVertex, ShaderKind.VertexShader, "scene.vert");
        _sceneFragment = CreateShaderModule(Shaders.SceneFragment(GroundBody.Planet.Map), ShaderKind.FragmentShader, "scene.frag");
        _shadowVertex = CreateShaderModule(Shaders.ShadowVertex, ShaderKind.VertexShader, "shadow.vert");
        _spotShadowVertex = CreateShaderModule(Shaders.SpotShadowVertex, ShaderKind.VertexShader, "spotshadow.vert");
        _shadowFragment = CreateShaderModule(Shaders.ShadowFragment, ShaderKind.FragmentShader, "shadow.frag");

        CreateShadowResources(props.Limits);
        CreateSkyResources();
        CreateFlareResources();
        CreateTimestampResources(timestampBits, props.Limits.TimestampPeriod);
        CreateSmokeResources();
        CreateGlowResources();
        _descriptorPool = CreateDescriptorSets();
        CreateSwapchain();

        // Requires an ImGui context to exist already (the HUD creates it). The backend's pipeline is
        // built for the scene render pass; the pass is recreated on resize, but with the same formats
        // and sample count, so it stays compatible.
        _ui = new VulkanBackend(
            new VulkanBackend.CreateInfo
            {
                Device = _device,
                GraphicsQueue = _queue,
                RenderPass = _renderPass,
                SampleCount = _samples,
                MinImageCount = 2,
                ImageCount = MaxFramesInFlight + 1, // vertex buffers rotate; one more than can be in flight
                DescriptorPoolSize = 16,
            }
        );
        LogoTexture = _ui.AddTexture(_logoTexture.Sampler, _logoTexture.View, VkImageLayout.ShaderReadOnlyOptimal);
    }

    #region Setup

    private static Instance CreateInstance()
    {
        var extensions = new HashSet<string>();
        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var count);
        for (var i = 0; i < count; i++)
            extensions.Add(Marshal.PtrToStringUTF8((nint)glfwExtensions[i])!);
        VulkanHelpers.AddPlatformRequiredExtensions(extensions);

        // Validation layers ship with the Vulkan SDK; use them when installed.
        var layers = new HashSet<string>();
        if (VulkanHelpers.Validation.IsSupported)
        {
            VulkanHelpers.AddValidationLayers(layers);
            Console.WriteLine("Vulkan validation layers enabled.");
        }

        fixed (byte* appName = "GameOne"u8)
            return Vulkan.CreateInstance(
                new Instance.CreateInfo
                {
                    AppInfo = new VkApplicationInfo
                    {
                        ApplicationName = appName,
                        ApiVersion = (int)VulkanHelpers.Api.VERSION_1_3,
                    },
                    Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                    EnabledExtensions = extensions,
                    EnabledLayers = layers,
                }
            );
    }

    private static VkSurfaceKHR CreateSurface(Instance instance, GlfwWindow window)
    {
        var result = (VkResult)Glfw.CreateWindowSurface(instance.VkHandle, window, out var surface);
        if (result != VkResult.Success)
            throw new InvalidOperationException($"glfwCreateWindowSurface failed: {result}");
        return new VkSurfaceKHR((nint)surface);
    }

    /// <summary>
    /// Prefers a discrete GPU; needs one queue family that does both graphics and present. With
    /// the family, how many bits of its timestamps count (0: it writes none), for the timings.
    /// </summary>
    private static (PhysicalDevice, int Family, int TimestampBits) PickPhysicalDevice(Instance instance, VkSurfaceKHR surface)
    {
        (PhysicalDevice Device, int Family, int TimestampBits, int Score)? best = null;

        foreach (var device in instance.PhysicalDevices)
        {
            if (!device.SupportsExtension(VK.KHR_SWAPCHAIN_EXTENSION_NAME))
                continue;

            var families = new VkQueueFamilyProperties[device.GetQueueFamilyProperties_Count()];
            device.GetQueueFamilyProperties_Write(families);

            for (var family = 0; family < families.Length; family++)
            {
                if (!families[family].QueueFlags.HasFlag(VkQueueFlags.GraphicsBit))
                    continue;
                if (!device.GetSurfaceSupportKHR(family, surface))
                    continue;

                var score = device.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu ? 2 : 1;
                if (best is null || score > best.Value.Score)
                    best = (device, family, families[family].TimestampValidBits, score);
                break;
            }
        }

        return best is { } b
            ? (b.Device, b.Family, b.TimestampBits)
            : throw new InvalidOperationException("No Vulkan device can render to this window.");
    }

    /// <param name="preciseOcclusion">Whether occlusion queries count pixels exactly (for the lens flare's visibility).</param>
    private static DeviceEx CreateDevice(PhysicalDevice physicalDevice, int queueFamily, out bool preciseOcclusion)
    {
        var priority = 1f;

        // Precise occlusion queries report how many pixels passed, not just whether any did.
        var features = new VkPhysicalDeviceFeatures { OcclusionQueryPrecise = physicalDevice.GetFeatures().OcclusionQueryPrecise };
        preciseOcclusion = features.OcclusionQueryPrecise;
        var extensions = new HashSet<string>();
        VulkanHelpers.AddSwapchainExtensions(extensions);
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(extensions, physicalDevice);

        return physicalDevice.CreateDevice(
            new DeviceEx.CreateInfo
            {
                Extensions = extensions,
                Features = &features,
                QueueCreateInfos =
                [
                    new VkDeviceQueueCreateInfo
                    {
                        QueueFamilyIndex = queueFamily,
                        QueueCount = 1,
                        QueuePriorities = &priority,
                    },
                ],
            }
        );
    }

    /// <summary>A buffer in host-visible, coherent memory, left mapped so the CPU can write it directly.</summary>
    private (VkBuffer, VkDeviceMemory, nint Mapped) CreateHostBuffer(ByteSize size, VkBufferUsageFlags usage)
    {
        var buffer = _device.CreateBuffer(
            new VkBufferCreateInfo { Size = size, Usage = usage, SharingMode = VkSharingMode.Exclusive },
            null
        );
        var memory = _device.AllocateMemory(
            _physicalDevice,
            buffer,
            VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            null
        );
        _device.BindBufferMemory(buffer, memory, ByteSize.Zero);
        return (buffer, memory, (nint)(void*)_device.MapMemory(memory));
    }

    /// <summary>
    /// All meshes in one host-visible vertex buffer. Fine for a few thousand static vertices; larger
    /// meshes would go through a staging buffer into device-local memory, as the texture does.
    /// </summary>
    private (VkBuffer, VkDeviceMemory) CreateMeshes(
        out MeshRange[] pads, out MeshRange[] landingZones, out MeshRange[] fittings, out MeshRange[] lamps, out MeshRange tower, out MeshRange towerDeck, out MeshRange droneShip, out MeshRange floe,
        out MeshRange booster, out MeshRange boosterStack, out MeshRange upperStage, out MeshRange upperStageOpen,
        out MeshRange satellite, out MeshRange lander, out MeshRange[] landerPanels, out (FairingGeometry, MeshRange)[] fairingHalves, out (LegGeometry, MeshRange)[] legs)
    {
        var all = new List<MeshVertex>();
        MeshRange Add(MeshVertex[] mesh)
        {
            var range = new MeshRange(all.Count, mesh.Length);
            all.AddRange(mesh);
            return range;
        }
        pads = [.. Foundations.PadFootings.Select((footing, s) => Add(Meshes.BuildPad(footing, Foundations.LaunchPads[s])))];
        landingZones = [.. Foundations.LandingZonePlinth.Select(plinth => Add(Meshes.BuildLandingZone(plinth)))];
        fittings = [.. PadLights.All.Select(place => Add(Meshes.BuildFittings(place)))];
        lamps = [.. PadLights.All.Select(place => Add(Meshes.BuildLamps(place)))];
        tower = Add(Meshes.BuildTower());
        towerDeck = Add(Meshes.BuildTowerDeck());
        droneShip = Add(Meshes.BuildDroneShip());
        floe = Add(Meshes.BuildFloe());
        booster = Add(RocketMesh.BuildBooster(nose: true));
        boosterStack = Add(RocketMesh.BuildBooster(nose: false));
        upperStage = Add(RocketMesh.BuildUpperStage(nose: true));
        upperStageOpen = Add(RocketMesh.BuildUpperStage(nose: false));
        satellite = Add(RocketMesh.BuildSatellite());
        lander = Add(RocketMesh.BuildLander());
        landerPanels = [.. Enumerable.Range(0, RocketGeometry.PanelCount).Select(panel => Add(RocketMesh.BuildLanderPanel(panel)))];
        var stages = Vehicle.All.SelectMany(vehicle => vehicle.Stages).ToArray();
        fairingHalves = [.. stages.Where(stage => stage.Fairing || stage.Kind == StageKind.Fairing).Select(stage => stage.FairingGeometry).Distinct(ReferenceEqualityComparer.Instance).Cast<FairingGeometry>()
            .Select(shape => (shape, Add(RocketMesh.BuildFairingHalf(shape))))];
        legs = [.. stages.Where(stage => stage.Legs).Select(stage => stage.LegGeometry).Distinct(ReferenceEqualityComparer.Instance).Cast<LegGeometry>()
            .Select(shape => (shape, Add(RocketMesh.BuildLeg(shape))))];

        var bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(all));
        var (buffer, memory, mapped) = CreateHostBuffer(ByteSize.Of(bytes.Length), VkBufferUsageFlags.VertexBufferBit);
        bytes.CopyTo(new Span<byte>((void*)mapped, bytes.Length));
        _device.UnmapMemory(memory);
        return (buffer, memory);
    }

    /// <summary>Binding 0: per-frame uniforms. 1: the logo texture. 2: the shadow map. 3: the terrain's heights. 4: its kinds and regions. 5: the floes' models. 6: the air's tables (<see cref="AirTables"/>: the sun's light through the air, and the sky's light). 7: the vessels' landing lights' glows. 8: the moon's heights. 9: its kinds (both read by the sky's ray-cast and the moon's mesh). 10: the planet's kinds' colours, mipmapped (<see cref="GroundColors"/>). 11: its heights clamped about the sea level, mipmapped, for the waterline far off (<see cref="Waterline"/>). 12: the seas' colours, blended across their borders, mipmapped (<see cref="SeaColors"/>).</summary>
    private DescriptorSetLayoutEx CreateSetLayout()
    {
        Span<VkDescriptorSetLayoutBinding> bindings =
        [
            new() { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = AllGraphics },
            new() { Binding = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 2, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 3, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = AllGraphics },
            new() { Binding = 4, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = AllGraphics },
            new() { Binding = 5, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.VertexBit },
            new() { Binding = 6, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = AllGraphics },
            new() { Binding = 7, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.VertexBit },
            new() { Binding = 8, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = AllGraphics },
            new() { Binding = 9, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = AllGraphics },
            new() { Binding = 10, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = AllGraphics },
            new() { Binding = 11, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = AllGraphics },
            new() { Binding = 12, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = AllGraphics },
        ];
        return _device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings }, null);
    }

    /// <summary>The descriptor set for view <paramref name="view"/> (0 is the main one) of frame-in-flight <paramref name="frame"/>.</summary>
    private VkDescriptorSet DescriptorSet(int frame, int view) => _descriptorSets[frame * MaxViews + view];

    /// <summary>Where view <paramref name="view"/>'s uniforms go in frame-in-flight <paramref name="frame"/>'s buffer.</summary>
    private FrameUniforms* Uniforms(int frame, int view) => (FrameUniforms*)(_uniformMapped[frame] + view * _uniformStride);

    private DescriptorPoolEx CreateDescriptorSets()
    {
        const int sets = MaxFramesInFlight * MaxViews;
        Span<VkDescriptorPoolSize> sizes =
        [
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = sets },
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 10 * sets },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 2 * sets },
        ];
        var pool = _device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo { MaxSets = sets, PoolSizes = sizes }, null);

        var logoInfo = new VkDescriptorImageInfo
        {
            Sampler = _logoTexture.Sampler,
            ImageView = _logoTexture.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        var shadowInfo = new VkDescriptorImageInfo
        {
            Sampler = _shadowSampler,
            ImageView = _shadowView,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        var heightInfo = new VkDescriptorImageInfo
        {
            Sampler = _heightTexture.Sampler,
            ImageView = _heightTexture.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        var kindInfo = new VkDescriptorImageInfo
        {
            Sampler = _kindTexture.Sampler,
            ImageView = _kindTexture.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        var airInfo = new VkDescriptorImageInfo
        {
            Sampler = _airTexture.Sampler,
            ImageView = _airTexture.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        var moonHeightInfo = new VkDescriptorImageInfo
        {
            Sampler = _moonHeightTexture.Sampler,
            ImageView = _moonHeightTexture.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        var moonKindInfo = new VkDescriptorImageInfo
        {
            Sampler = _moonKindTexture.Sampler,
            ImageView = _moonKindTexture.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        var groundColorInfo = new VkDescriptorImageInfo
        {
            Sampler = _groundColorTexture.Sampler,
            ImageView = _groundColorTexture.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        var waterlineInfo = new VkDescriptorImageInfo
        {
            Sampler = _waterlineTexture.Sampler,
            ImageView = _waterlineTexture.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        var seaColorInfo = new VkDescriptorImageInfo
        {
            Sampler = _seaColorTexture.Sampler,
            ImageView = _seaColorTexture.View,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        for (var i = 0; i < sets; i++)
        {
            var set = _descriptorSets[i] = _device.AllocateDescriptorSet(pool, _setLayout);
            var bufferInfo = new VkDescriptorBufferInfo
            {
                Buffer = _uniformBuffers[i / MaxViews],
                Offset = ByteSize.Of(i % MaxViews * _uniformStride),
                Range = ByteSize.Of(sizeof(FrameUniforms)),
            };
            var floeInfo = new VkDescriptorBufferInfo
            {
                Buffer = _floeBuffers[i / MaxViews],
                Range = ByteSize.Of(sizeof(Matrix4x4) * _floeCapacity),
            };
            var vesselGlowInfo = new VkDescriptorBufferInfo
            {
                Buffer = _vesselGlowBuffers[i / MaxViews],
                Range = ByteSize.Of(sizeof(Vector4) * VesselGlowCapacity),
            };
            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &bufferInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &logoInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &shadowInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &heightInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &kindInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 5, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &floeInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 6, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &airInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 7, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &vesselGlowInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 8, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &moonHeightInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 9, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &moonKindInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 10, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &groundColorInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 11, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &waterlineInfo },
                    new VkWriteDescriptorSet { DstSet = set, DstBinding = 12, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &seaColorInfo },
                ],
                []
            );
        }

        return pool;
    }

    #endregion

    /// <param name="ui">This frame's ImGui draw data (after <see cref="ImGui.Render"/>), drawn over the scene.</param>
    public void RenderFrame(in FrameInput input, ImDrawDataPtr ui)
    {
        if (_swapchainDirty)
            RecreateSwapchain();
        if (_swapchainDirty) // still minimised
            return;

        var fence = _inFlight[_frame];
        _device.WaitForFences([fence], true, -1);
        ReadLightVisibility(); // this slot's occlusion queries from last time, now finished
        ReadTimestamps(); // and its timestamps, while timings are on

        // BRUTAL throws VkResultError for VK_ERROR_* results (including OUT_OF_DATE) rather than
        // returning them, so a resize shows up as an exception here and at present.
        int imageIndex;
        try
        {
            _device.AcquireNextImageKHR(_swapchain, -1, _imageAvailable[_frame], default, out imageIndex);
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
        {
            _swapchainDirty = true;
            return;
        }

        _device.ResetFences([fence]);
        // For the GPU (the fence) and the display (the image). The readbacks after the fence, above,
        // fall in this lap too, and the uniforms' setup below in Record's.
        Timings.FrameLap(FramePart.Wait);

        // The fence wait above means the GPU is done with this frame's uniform buffer, so it can be rewritten.
        SetSunAreas(input);
        var uniforms = BuildFrameUniforms(input);
        SetSun(ref uniforms, 0); // the main view's area is the first
        SetStirs(ref uniforms, input.Stirs);
        SetSpots(ref uniforms, input.LightSets); // (before the small views copy the main view's: every view's has them all)
        SetLights(ref uniforms, input.LightSets.IsEmpty ? default : input.LightSets[0]); // the main view's set is the first
        *Uniforms(_frame, 0) = uniforms;
        var views = input.Views[..Math.Min(input.Views.Length, GameOne.Scene.MaxSmallViews)];
        for (var i = 0; i < views.Length; i++)
            *Uniforms(_frame, 1 + i) = SmallViewUniforms(uniforms, views[i], i, input.LightSets);
        _floeCount = Math.Min(input.Floes.Length, _floeCapacity);
        input.Floes[.._floeCount].CopyTo(new Span<Matrix4x4>(_floeMapped[_frame], _floeCount));
        _vesselGlowCount = Math.Min(input.VesselGlows.Length, VesselGlowCapacity);
        for (var i = 0; i < _vesselGlowCount; i++)
            _vesselGlowMapped[_frame][i] = new Vector4(input.VesselGlows[i].At, GlowW(input.VesselGlows[i]));
        RecordCommands(_commandBuffers[_frame], imageIndex, input, views, ui);
        Timings.FrameLap(FramePart.Record);

        var waitSemaphore = _imageAvailable[_frame];
        var signalSemaphore = _renderFinished[imageIndex];
        var waitStage = VkPipelineStageFlags.ColorAttachmentOutputBit;
        var commandBuffer = _commandBuffers[_frame].Handle;
        _queue.Submit(
            [
                new VkSubmitInfo
                {
                    WaitSemaphoreCount = 1,
                    WaitSemaphores = &waitSemaphore,
                    WaitDstStageMask = &waitStage,
                    CommandBufferCount = 1,
                    CommandBuffers = &commandBuffer,
                    SignalSemaphoreCount = 1,
                    SignalSemaphores = &signalSemaphore,
                },
            ],
            fence
        );

        var swapchain = _swapchain;
        try
        {
            var present = _queue.PresentKHR(
                new VkPresentInfoKHR
                {
                    WaitSemaphoreCount = 1,
                    WaitSemaphores = &signalSemaphore,
                    SwapchainCount = 1,
                    Swapchains = &swapchain,
                    ImageIndices = &imageIndex,
                }
            );
            if (present == VkResult.SuboptimalKHR)
                _swapchainDirty = true;
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
        {
            _swapchainDirty = true;
        }
        Timings.FrameLap(FramePart.Submit);

        _frame = (_frame + 1) % MaxFramesInFlight;
    }

    private FrameUniforms BuildFrameUniforms(in FrameInput input)
    {
        var aspect = (float)_extent.Width / _extent.Height;
        var view = Matrix4x4.CreateLookAt(input.CameraPosition, input.CameraTarget, input.CameraUp);
        // The camera's planes (ChaseCamera.Planes: the far one at infinity), its depth reversed.
        var (near, far) = input.CameraClip;
        _projection = CameraProjection(float.DegreesToRadians(GameOne.Scene.CameraVerticalFovDegrees), aspect, near, far);
        _viewProj = view * _projection;
        _cameraPosition = input.CameraPosition;
        _mapShown = input.MapShown;
        _eyes[0] = input.CameraPosition;
        _sunDirection = input.SunDirection;
        _sunDisc = input.SunDisc;
        _lightAveraged = input.LightAveraged;
        _moonCentre = input.CameraPosition + input.MoonFromCamera;
        _moonOrientation = input.MoonOrientation;

        var uniforms = new FrameUniforms
        {
            // System.Numerics matrices multiply row vectors (v * M). Uploading their row-major data
            // into GLSL's column-major mat4 transposes them, so in the shader `M * v` is the same thing.
            ViewProj = _viewProj,
            InvViewRays = ViewRays(view, _projection),
            SunDir = new Vector4(input.SunDirection, 0), // w: the shadow lookups' nudge, its sun area's (SetSun)
            SunColor = new Vector4(GameOne.Scene.SunColor, 1),
            // Time is kept small so the noise keeps its float precision in a long-running session.
            CameraPos = new Vector4(input.CameraPosition, (float)(input.Time % 2000.0)),
            PointLightPos = new Vector4(input.PointLightPosition, GameOne.Scene.FlameLightRange),
            PointLight = new Vector4(input.PointLight, 0),
            Pad = new Vector4(GameOne.Scene.PadHalfSize, GameOne.Scene.PadTop, GameOne.Scene.LandingTargetRadius, GameOne.Scene.LandingZoneRadius),
            SiteCount = new Vector4(PadSitePlaces.Length, ZoneSitePlaces.Length, 0, 0),
            TestRangeStart = new Vector4(OnTheSphere(GameOne.Scene.TestPads[0].Centre), 0),
            TestRangeEnd = new Vector4(OnTheSphere(GameOne.Scene.TestPads[^1].Centre), 0),
            Planet = new Vector4(GroundBody.Planet.Centre, GroundBody.Planet.SeaRadius),
            Atmosphere = new Vector4(GameOne.Scene.AtmosphereHeight, GameOne.Scene.AtmosphereScaleHeight, input.StarTurn, GameOne.SolarSystem.SunAngularRadius), // z = the stars' turn, w = the sun's disc's angular radius
            // From the camera (worked out in double), so the sky's ray-cast of it and its mesh keep
            // their precision however far out either is.
            Moon = new Vector4(input.MoonFromCamera, input.MoonRadius),
            MoonFrame = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(input.MoonOrientation)),
            Moonlight = new Vector4(input.MoonShine, 0),
            SunDisc = input.SunDisc,
            Averaged = new Vector4(input.LightAveraged, GameOne.Scene.AveragedSunStrength, MathF.Sin(float.DegreesToRadians(GameOne.Scene.AveragedSunElevationDegrees)), 0),
            Terrain = TerrainUniform,
            CameraClip = CameraClip(_projection),
        };
        // From the world's time, not the session's (CameraPos.w): the waves are part of the world,
        // in step with what floats on them. Written straight into the uniforms, so nothing is allocated.
        Sea.Phases(input.WorldTime, MemoryMarshal.Cast<Vector4, float>((Span<Vector4>)uniforms.WavePhases));
        PadSitePlaces.CopyTo((Span<Vector4>)uniforms.PadSites);
        PadEastPlaces.CopyTo((Span<Vector4>)uniforms.PadEasts);
        PadNorthPlaces.CopyTo((Span<Vector4>)uniforms.PadNorths);
        ZoneSitePlaces.CopyTo((Span<Vector4>)uniforms.ZoneSites);
        SetMoonMesh(ref uniforms, 0);
        return uniforms;
    }

    /// <summary>
    /// Whether view <paramref name="view"/> (0 the main one) draws the moon as a mesh, by its
    /// uniforms' moon (from its camera): said in them (<c>moonlight.w</c>, so the sky leaves it out)
    /// and kept for <see cref="DrawMoon"/>, so the two always agree.
    /// </summary>
    private void SetMoonMesh(ref FrameUniforms uniforms, int view)
    {
        var fromCamera = new Vector3(uniforms.Moon.X, uniforms.Moon.Y, uniforms.Moon.Z);
        var meshed = uniforms.Moon.W > 0f && PatchBody.DrawsMoon(fromCamera.Length());
        uniforms.Moonlight.W = meshed ? 1f : 0f;
        _moonFromCamera[view] = meshed ? fromCamera : new Vector3(float.PositiveInfinity);
    }

    /// <summary>The point on the sphere straight under <paramref name="p"/>: where a test pad's ground is, levelled to Home's elevation.</summary>
    private static Vector3 OnTheSphere(Vector3 p) => GameOne.Planet.Centre + GameOne.Planet.Up(p) * GameOne.Planet.Radius;

    /// <summary>Each launch pad's centre, for the shader's apron (<c>padSites</c>).</summary>
    private static readonly Vector4[] PadSitePlaces = [.. Fitted(LaunchSites.All, "launch sites").Select(site => new Vector4(site.Pad, 0))];

    /// <summary>Each launch pad's east and north, as its frame has them (<c>padEasts</c>, <c>padNorths</c>): the shader's apron is square to them.</summary>
    private static readonly Vector4[] PadEastPlaces = [.. LaunchSites.All.Select(site => new Vector4(Vector3.Transform(Vector3.UnitX, site.Frame), 0))];
    private static readonly Vector4[] PadNorthPlaces = [.. LaunchSites.All.Select(site => new Vector4(Vector3.Transform(-Vector3.UnitZ, site.Frame), 0))];

    /// <summary><paramref name="places"/>, if the shader has room for them all: one too many would go undrawn, so the renderer refuses to start.</summary>
    private static IReadOnlyList<T> Fitted<T>(IReadOnlyList<T> places, string what) =>
        places.Count <= MaxSites ? places : throw new InvalidOperationException($"{places.Count} {what}, but the shader holds {MaxSites} (VulkanRenderer.MaxSites, and the GLSL's arrays)");

    /// <summary>
    /// Each landing zone's centre but the test pads' (which share one apron along their row), for
    /// the shader's apron (<c>zoneSites</c>): Home's (the first), then the other sites' (after the test pads).
    /// </summary>
    private static readonly Vector4[] ZoneSitePlaces =
        [.. Fitted([.. Foundations.LandingZonePlinth.Where((_, i) => i == 0 || i > GameOne.Scene.TestPads.Length)], "landing zones").Select(zone => new Vector4(zone.Centre, 0))];

    private static Vector4 TerrainUniform =>
        new(GroundBody.Planet.Map.Resolution, GroundBody.Planet.Map.MinHeight, GroundBody.Planet.Map.MaxHeight, GameOne.Scene.DetailScale); // w: the scale the detail and waves are laid out on, as in C#

    /// <summary>
    /// Where the camera itself lands in clip space: (0, 0, 0, 1) in view space, so the projection's
    /// last row. The terrain is placed from the camera (see <see cref="Shaders.PlanetVertex"/>):
    /// <c>viewProj * (p - camera, 0)</c> plus this is <c>viewProj * (p, 1)</c>, without subtracting
    /// large world coordinates.
    /// </summary>
    private static Vector4 CameraClip(in Matrix4x4 projection) => new(projection.M41, projection.M42, projection.M43, projection.M44);

    /// <summary>
    /// A camera's projection for Vulkan (<see cref="ChaseCamera.Projection"/>: its depth reversed,
    /// 1 at the near plane falling to 0 at the far one, at infinity), clip space's Y pointing down
    /// (System.Numerics points it up, so it is flipped). Everything drawn in a view's scene pass
    /// goes by it: the depth is cleared to 0 (the far plane), tested Greater, and the sky writes its
    /// own (the moon's and the sun's where they cover a pixel, else 0).
    /// </summary>
    internal static Matrix4x4 CameraProjection(float verticalFov, float aspect, float near, float far)
    {
        var projection = ChaseCamera.Projection(verticalFov, aspect, near, far);
        projection.M22 = -projection.M22;
        return projection;
    }

    /// <summary>
    /// A floodlight's perspective projection for Vulkan, for its tile of the shadow map, whose
    /// depth is not reversed (it is its own, cleared to 1 and tested Less): depth 0..1
    /// (System.Numerics already does that) and clip space Y pointing down (System.Numerics points
    /// it up, so it is flipped).
    /// </summary>
    private static Matrix4x4 PerspectiveVulkan(float verticalFov, float aspect, float near, float far)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(verticalFov, aspect, near, far);
        projection.M22 = -projection.M22;
        return projection;
    }

    /// <summary>
    /// What turns a pixel's clip-space point into the direction of its view ray (the sky pass's
    /// <c>invViewRays</c>): the inverse of the view's turn alone (its translation left out) and
    /// <paramref name="projection"/>. The whole view-projection's inverse would give a point out
    /// there, less the camera's place to make a direction; tens of kilometres out (on the moon,
    /// over the planet's far side) that difference of two large floats loses pixels, frame to frame,
    /// and the sky swims while the ground, drawn through the view-projection itself, holds still.
    /// </summary>
    internal static Matrix4x4 ViewRays(Matrix4x4 view, Matrix4x4 projection)
    {
        view.Translation = Vector3.Zero;
        Matrix4x4.Invert(view * projection, out var inverse);
        return inverse;
    }

    private void RecordCommands(CommandBuffer cb, int imageIndex, in FrameInput input, ReadOnlySpan<ViewInput> views, ImDrawDataPtr ui)
    {
        cb.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });
        BindView(cb, 0);
        cb.BindVertexBuffer(0, _vertexBuffer);

        ResetLightQueries(cb);
        ResetTimestamps(cb);
        Timestamp(cb, 0);
        // The shadow pass's recording is a part of its own; the rest of the recording is Record's.
        Timings.FrameLap(FramePart.Record);
        RecordShadowPass(cb, input, views);
        Timings.FrameLap(FramePart.Shadows);
        Timestamp(cb, 1 + (int)GpuPass.Shadows);

        // One clear value per attachment, by index: colour, (resolve, ignored), depth: 0, the far
        // plane, as the depth is reversed (CameraProjection). The sky covers every pixel, so the
        // colour clear is never seen.
        var clears = stackalloc VkClearValue[3];
        clears[0] = new VkClearValue { Color = new VkClearColorValue { Float32 = new(0f, 0f, 0f, 1f) } };
        clears[Msaa ? 2 : 1] = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 0f } };
        cb.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = _renderPass,
                Framebuffer = _framebuffers[imageIndex],
                RenderArea = new VkRect2D { Extent = _extent },
                ClearValueCount = Msaa ? 3 : 2,
                ClearValues = clears,
            },
            VkSubpassContents.Inline
        );
        cb.SetViewport(0, [new VkViewport { Width = _extent.Width, Height = _extent.Height, MaxDepth = 1f }]);
        cb.SetScissor(0, [new VkRect2D { Extent = _extent }]);

        DrawSky(cb);

        DrawTerrain(cb, input.CameraPosition, _viewProj, GameOne.Scene.TerrainLodRatio);
        DrawMoon(cb, 0, input.CameraPosition, _viewProj, GameOne.Scene.TerrainLodRatio);

        DrawSolids(cb, input);
        Timestamp(cb, 1 + (int)GpuPass.Scene);

        // See-through, so after everything solid: depth-tested against it, but hiding nothing. Fire
        // glows through smoke, so the flames go over the top.
        DrawSmoke(cb, input);
        DrawGlows(cb, input, input.CameraPosition);
        // The sun is far beyond everything: what you see of it besides its disc is the flare. Its
        // visibility is measured once everything that could hide it is in the depth buffer.
        if (SunProbe() is { } probe)
        {
            DrawLightProbes(cb, probe.At, probe.Radius);
            DrawLensFlare(cb, probe.At, Vector3.Distance(probe.At, input.CameraPosition));
        }
        Timestamp(cb, 1 + (int)GpuPass.Effects);

        // The small views over the main one, each in its own corner of the screen.
        DrawSmallViews(cb, input, views);
        Timestamp(cb, 1 + (int)GpuPass.SmallViews);

        // The HUD last, over everything. The backend binds its own pipeline and descriptors.
        _ui.RenderDrawData(ui, cb);
        Timestamp(cb, 1 + (int)GpuPass.Hud);

        cb.EndRenderPass();
        cb.End();
    }

    /// <summary>Everything solid on the planet: the pad, the landing zones, the tower, the drone ship, the floes, and every stage.</summary>
    private void DrawSolids(CommandBuffer cb, in FrameInput input)
    {
        cb.BindPipeline(VkPipelineBindPoint.Graphics, _scenePipeline);
        // Each zone has a mesh of its own, down to its own foundation (and, tilted, its plinth's shape).
        var zones = Foundations.LandingZonePlinth;
        for (var i = 0; i < zones.Count; i++)
            Draw(cb, _landingZoneMeshes[i], Matrix4x4.CreateFromQuaternion(zones[i].Tilt) * Matrix4x4.CreateTranslation(zones[i].Centre), MaterialKind.LandingZone);
        DrawSites(cb);
        DrawLitPlaces(cb, input.PlaceLit);
        foreach (var ship in input.DroneShips)
            Draw(cb, _droneShipMesh, ship, MaterialKind.Deck);
        DrawFloes(cb);
        DrawStages(cb, input.Parts);
    }

    /// <summary>
    /// The floes this frame (see <see cref="FrameInput.Floes"/>), in one instanced draw: the
    /// vertex shaders take each one's model from the floes' buffer by its instance, not from
    /// the draw's constants.
    /// </summary>
    private void DrawFloes(CommandBuffer cb)
    {
        if (_floeCount == 0)
            return;
        PushDraw(cb, Matrix4x4.Identity, MaterialKind.Ice);
        cb.Draw(_floeMesh.VertexCount, _floeCount, _floeMesh.FirstVertex, 0);
    }

    /// <summary>
    /// Every launch site's pad (with <paramref name="pads"/>) and service tower, in the site's frame:
    /// its girders in its own colour, its deck grey. The shadow pass draws only the towers.
    /// </summary>
    /// <summary>Every site's pad (unless only shadows are wanted) and tower; with <paramref name="within"/>, only those that near <paramref name="near"/>, and with <paramref name="cull"/>, only those in that sun area.</summary>
    private void DrawSites(CommandBuffer cb, bool pads = true, Vector3 near = default, float within = float.PositiveInfinity, SunCull? cull = null)
    {
        var sites = LaunchSites.All;
        for (var i = 0; i < sites.Length; i++)
        {
            if (float.IsFinite(within) && Vector3.Distance(sites[i].Pad, near) > within || cull is { } area && !area.Sees(sites[i].Pad, SiteReach))
                continue;
            var model = sites[i].Model;
            if (pads)
                Draw(cb, _padMeshes[i], model, MaterialKind.Pad);
            Draw(cb, _towerMesh, model, MaterialKind.Lit, sites[i].TowerColour);
            Draw(cb, _towerDeckMesh, model, MaterialKind.Lit);
        }
    }

    /// <summary>
    /// Every lit place's fittings (its masts, steel) and, given <paramref name="lit"/> (each place's
    /// fraction, <see cref="FrameInput.PlaceLit"/>; none when only shadows are wanted), its lamps'
    /// faces, shining as far as its fraction has them: each in its frame as it is now (a ship's
    /// rides the waves).
    /// </summary>
    private void DrawLitPlaces(CommandBuffer cb, ReadOnlySpan<float> lit = default, Vector3 near = default, float within = float.PositiveInfinity, SunCull? cull = null)
    {
        var places = PadLights.All;
        for (var i = 0; i < places.Count; i++)
        {
            if (float.IsFinite(within) && Vector3.Distance(places[i].Origin, near) > within || cull is { } area && !area.Sees(places[i].Origin, SiteReach))
                continue; // (only those that near, with a within: the floodlights' shadows; or in the sun area, with a cull)
            var model = Matrix4x4.CreateFromQuaternion(places[i].Frame) * Matrix4x4.CreateTranslation(places[i].Origin);
            if (_fittingMeshes[i].VertexCount > 0)
                Draw(cb, _fittingMeshes[i], model, MaterialKind.Lit);
            if (!lit.IsEmpty && _lampMeshes[i].VertexCount > 0)
            {
                // The lamps' colour is their mesh's, so the tint's room is free: w carries the
                // place's fraction, by which the shader mixes the face lit as a surface with its
                // shine. Pushed directly, not by PushDraw (which would put a floodlight shadow's
                // index in w): lamp faces are never drawn while a floodlight's shadow is.
                PushMaterial(cb, FromEye(model), new Vector4((float)MaterialKind.Lamp, 1f, 1f, lit[i]));
                cb.Draw(_lampMeshes[i].VertexCount, 1, _lampMeshes[i].FirstVertex, 0);
            }
        }
    }

    /// <summary>
    /// <paramref name="model"/> (placed in the scene's frame) placed from the bound view's camera
    /// instead, as every mesh's model is pushed: the shaders place it from there
    /// (<c>meshModel</c>), so what is near the camera keeps its precision however far out both are.
    /// </summary>
    private Matrix4x4 FromEye(Matrix4x4 model)
    {
        model.Translation -= _eye;
        return model;
    }

    /// <summary>Draws <paramref name="mesh"/> placed by <paramref name="model"/>, in the scene's frame.</summary>
    private void Draw(CommandBuffer cb, MeshRange mesh, Matrix4x4 model, MaterialKind kind, Vector3? tint = null) =>
        DrawFromEye(cb, mesh, FromEye(model), kind, tint);

    /// <summary>Draws <paramref name="mesh"/> placed by <paramref name="model"/>, from the bound view's camera.</summary>
    private void DrawFromEye(CommandBuffer cb, MeshRange mesh, Matrix4x4 model, MaterialKind kind, Vector3? tint = null)
    {
        PushDraw(cb, model, kind, tint);
        cb.Draw(mesh.VertexCount, 1, mesh.FirstVertex, 0);
    }

    private void PushDraw(CommandBuffer cb, Matrix4x4 model, MaterialKind kind, Vector3? tint = null)
    {
        var t = tint ?? Vector3.One;
        // (While a floodlight's shadow is drawn, w says which: the depth pass has no use for the tint.)
        PushMaterial(cb, model, new Vector4((float)kind, t.X, t.Y, _spotShadow >= 0 ? _spotShadow : t.Z));
    }

    private void PushMaterial(CommandBuffer cb, Matrix4x4 model, Vector4 material)
    {
        var constants = new DrawConstants { Model = model, Material = material };
        cb.PushConstants(_pipelineLayout, AllGraphics, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<DrawConstants>(in constants)));
    }

    /// <summary>
    /// Each stage with its own mesh, its legs (if it has them) where they have swung to, its bay's
    /// doors (if it has a bay) as far open as they are (each short of where it stopped on the ground),
    /// and its fairing round it while that is on. Their models are placed from the main view's camera
    /// (<see cref="FrameInput.Parts"/>): moved by the step from it to the bound view's. In a small
    /// view at another body than the main view's (its camera some 40 km from the main one), that
    /// step and the models, both floats that far out, leave its stages a few millimetres off, about
    /// 0.05 px at a small view's size: the models are given once for every view, and a list of them
    /// for each view isn't worth that. A small view at the main view's body, the step small, is exact.
    /// </summary>
    private void DrawStages(CommandBuffer cb, ReadOnlySpan<StagePart> parts)
    {
        var halfTurn = Matrix4x4.CreateRotationY(MathF.PI);
        var shift = _eyes[0] - _eye;
        void Draw(CommandBuffer cb, MeshRange mesh, Matrix4x4 model, MaterialKind kind)
        {
            model.Translation += shift;
            DrawFromEye(cb, mesh, model, kind);
        }
        foreach (var part in parts)
        {
            var mesh = part.Stage.Kind switch
            {
                StageKind.Booster => part.Stage.Nose ? _boosterMesh : _boosterStackMesh,
                StageKind.Upper => part.Stage.Nose ? _upperStageMesh : _upperStageOpenMesh,
                StageKind.Satellite => _satelliteMesh,
                StageKind.Lander => _landerMesh,
                _ => FairingHalfMesh(part.Stage.FairingGeometry),
            };
            Draw(cb, mesh, part.Model, MaterialKind.Lit);
            if (part.Fairing)
            {
                var half = FairingHalfMesh(part.Stage.FairingGeometry);
                Draw(cb, half, part.Model, MaterialKind.Lit);
                Draw(cb, half, halfTurn * part.Model, MaterialKind.Lit);
            }
            if (part.Stage.Legs)
            {
                var legs = part.Stage.LegGeometry;
                var legMesh = LegMesh(legs);
                for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
                    Draw(cb, legMesh, RocketGeometry.LegTransform(legs, leg, part.LegDeploy, part.LegExtension[leg]) * part.Model, MaterialKind.Lit);
            }
            if (part.Stage.Bay)
                for (var door = 0; door < RocketGeometry.DoorCount; door++)
                    for (var panel = 0; panel < RocketGeometry.PanelCount; panel++)
                        Draw(cb, _landerPanelMeshes[panel], RocketGeometry.PanelTransform(door, panel, part.BayDoors, part.DoorStops[door]) * part.Model, MaterialKind.Lit);
        }
    }

    /// <summary>The fairing half mesh built for <paramref name="shape"/> (every one of <see cref="Vehicle.All"/>'s has one), matched as the instance its stage holds, never by its numbers.</summary>
    private MeshRange FairingHalfMesh(FairingGeometry shape)
    {
        foreach (var (each, mesh) in _fairingHalfMeshes)
            if (ReferenceEquals(each, shape))
                return mesh;
        throw new InvalidOperationException("A fairing of a shape no vehicle has.");
    }

    /// <summary>The leg mesh built for <paramref name="shape"/> (every one of <see cref="Vehicle.All"/>'s has one), matched as the instance its stage holds, never by its numbers.</summary>
    private MeshRange LegMesh(LegGeometry shape)
    {
        foreach (var (each, mesh) in _legMeshes)
            if (ReferenceEquals(each, shape))
                return mesh;
        throw new InvalidOperationException("Legs of a shape no vehicle has.");
    }

    public void Dispose()
    {
        _device.WaitIdle();

        _ui.Dispose(); // needs the ImGui context, so the HUD must be disposed after the renderer
        DestroySwapchain();
        DestroyShadowResources();
        DestroySkyResources();
        DestroyFlareResources();
        DestroyTimestampResources();
        DestroySmokeResources();
        DestroyGlowResources();
        DestroyTerrainResources();
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            _device.DestroyFence(_inFlight[i], null);
            _device.DestroySemaphore(_imageAvailable[i], null);
            _device.UnmapMemory(_uniformMemory[i]);
            _device.DestroyBuffer(_uniformBuffers[i], null);
            _device.FreeMemory(_uniformMemory[i], null);
            _device.UnmapMemory(_floeMemory[i]);
            _device.DestroyBuffer(_floeBuffers[i], null);
            _device.FreeMemory(_floeMemory[i], null);
            _device.UnmapMemory(_vesselGlowMemory[i]);
            _device.DestroyBuffer(_vesselGlowBuffers[i], null);
            _device.FreeMemory(_vesselGlowMemory[i], null);
        }
        _device.DestroyBuffer(_vertexBuffer, null);
        _device.FreeMemory(_vertexMemory, null);
        _descriptorPool.Dispose();
        _setLayout.Dispose();
        _logoTexture.Dispose();
        _device.DestroyCommandPool(_commandPool, null);
        foreach (var module in (ReadOnlySpan<VkShaderModule>)[_sceneVertex, _sceneFragment, _shadowVertex, _shadowFragment, _spotShadowVertex])
            _device.DestroyShaderModule(module, null);
        _device.DestroyPipelineLayout(_pipelineLayout, null);
        _device.Dispose();
        _instance.DestroySurfaceKHR(_surface, null);
        _instance.Dispose();
    }
}
