using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;

namespace GameOne.Rendering;

// The terrain: the map's heights, kinds, the kinds' colours (GroundColors), the waterline (Waterline) and the seas' colours (SeaColors) as textures (and, made here too, the moon's map and the air's tables), and the patches each view chooses
// (PlanetPatches.Select) drawn one draw each, the ground first, then the sea over the patches that
// reach under it. Every patch is the same grid, built in the vertex shader from its indices
// (Shaders.PlanetVertex), so it needs no vertex buffer; the indices let the GPU work out each
// grid point once rather than once for each of the six triangles round it.
// Not yet: the terrain casts no sun shadows (the shadow map is the vessel's surroundings only).
// The moon up close (a view's camera within Scene.MoonMeshReach of its centre) is drawn the same
// way, from its own map and cube (DrawMoon), in place of the sky's ray-cast of it.
internal sealed unsafe partial class VulkanRenderer
{
    private GpuTexture _heightTexture = null!, _kindTexture = null!, _groundColorTexture = null!, _waterlineTexture = null!, _seaColorTexture = null!, _airTexture = null!, _moonHeightTexture = null!, _moonKindTexture = null!;
    private VkBuffer _patchIndexBuffer;
    private VkDeviceMemory _patchIndexMemory;
    private int _patchIndexCount;
    private VkShaderModule _planetVertex, _moonVertex, _moonFragment;

    /// <summary>The patches being drawn, refilled for each view: kept, so choosing them allocates nothing.</summary>
    private readonly List<PlanetPatch> _patches = new(GameOne.Scene.TerrainPatchBudget);

    /// <summary>The moon's patches, as <see cref="_patches"/> are the planet's.</summary>
    private readonly List<PlanetPatch> _moonPatches = new(GameOne.Scene.TerrainPatchBudget);

    /// <summary>The moon's centre from each view's camera this frame (0 the main view's), as its uniforms have it: the mesh is drawn where they leave the moon to it.</summary>
    private readonly Vector3[] _moonFromCamera = new Vector3[MaxViews];

    /// <summary>The moon's orientation this frame (<see cref="FrameInput.MoonOrientation"/>).</summary>
    private Quaternion _moonOrientation = Quaternion.Identity;

    private void CreateTerrainResources()
    {
        var map = GroundBody.Planet.Map;
        var side = map.Resolution + 2;
        _heightTexture = new GpuTexture(_device, _queue, _commandPool, VkFormat.R16UNorm, side, side, 6,
            MemoryMarshal.AsBytes(map.Heights.AsSpan()));
        var kinds = new byte[map.Kinds.Length * 2];
        for (var i = 0; i < map.Kinds.Length; i++)
        {
            kinds[2 * i] = map.Kinds[i];
            kinds[2 * i + 1] = map.Regions[i];
        }
        _kindTexture = new GpuTexture(_device, _queue, _commandPool, VkFormat.R8G8UInt, side, side, 6, kinds);

        // The kinds' colours, mipmapped, for the ground's colour read per pixel however far off
        // (GroundColors: display values, so UNORM, the levels averaged in linear light on the CPU).
        var colors = GroundColors.Build(map);
        _groundColorTexture = new GpuTexture(_device, _queue, _commandPool, VkFormat.R8G8B8A8UNorm, colors.Side, colors.Side, 6,
            colors.Pixels, linear: true, mipLevels: colors.Levels);

        // The heights clamped about the sea level, mipmapped, for where the water is drawn far off (Waterline).
        var waterline = Waterline.Build(map);
        _waterlineTexture = new GpuTexture(_device, _queue, _commandPool, VkFormat.R16SFloat, waterline.Side, waterline.Side, 6,
            waterline.Pixels(), linear: true, mipLevels: waterline.Levels);

        // The seas' colours, blended across their borders, mipmapped (SeaColors: the faces' shallow
        // colours, then their deep ones, twelve layers).
        var seaColors = SeaColors.Build(map);
        _seaColorTexture = new GpuTexture(_device, _queue, _commandPool, VkFormat.R16G16B16A16SFloat, seaColors.Side, seaColors.Side, 12,
            seaColors.Pixels(), linear: true, mipLevels: seaColors.Levels);

        // The moon's map, as the planet's: its heights, and its kinds (MoonKind, one byte), for the
        // sky's ray-cast of it (read with gathers: a nearest sampler, its four texels at once).
        var moon = MoonGround.Map;
        var moonSide = moon.Resolution + 2;
        _moonHeightTexture = new GpuTexture(_device, _queue, _commandPool, VkFormat.R16UNorm, moonSide, moonSide, 6,
            MemoryMarshal.AsBytes(moon.Heights.AsSpan()));
        _moonKindTexture = new GpuTexture(_device, _queue, _commandPool, VkFormat.R8UInt, moonSide, moonSide, 6, moon.Kinds);

        // The air's tables, made once here with the terrain's textures: linearly filtered, as Vulkan requires of this format.
        _airTexture = new GpuTexture(_device, _queue, _commandPool, VkFormat.R16G16B16A16SFloat, AirTables.SunAngles, AirTables.Heights, 2,
            MemoryMarshal.AsBytes(AirTables.Build().AsSpan()), linear: true);

        var indices = Shaders.PatchIndices();
        _patchIndexCount = indices.Length;
        var bytes = MemoryMarshal.AsBytes(indices.AsSpan());
        (_patchIndexBuffer, _patchIndexMemory, var mapped) = CreateHostBuffer(ByteSize.Of(bytes.Length), VkBufferUsageFlags.IndexBufferBit);
        bytes.CopyTo(new Span<byte>((void*)mapped, bytes.Length));
        _device.UnmapMemory(_patchIndexMemory);

        _planetVertex = CreateShaderModule(Shaders.PlanetVertex(map), Brutal.ShaderCApi.ShaderKind.VertexShader, "planet.vert");
        _moonVertex = CreateShaderModule(Shaders.MoonVertex, Brutal.ShaderCApi.ShaderKind.VertexShader, "moon.vert");
        _moonFragment = CreateShaderModule(Shaders.MoonFragment, Brutal.ShaderCApi.ShaderKind.FragmentShader, "moon.frag");
    }

    private void DestroyTerrainResources()
    {
        _heightTexture.Dispose();
        _kindTexture.Dispose();
        _groundColorTexture.Dispose();
        _waterlineTexture.Dispose();
        _seaColorTexture.Dispose();
        _airTexture.Dispose();
        _moonHeightTexture.Dispose();
        _moonKindTexture.Dispose();
        _device.DestroyBuffer(_patchIndexBuffer, null);
        _device.FreeMemory(_patchIndexMemory, null);
        _device.DestroyShaderModule(_planetVertex, null);
        _device.DestroyShaderModule(_moonVertex, null);
        _device.DestroyShaderModule(_moonFragment, null);
    }

    /// <summary>
    /// The ground, then the sea, as seen from <paramref name="camera"/> through
    /// <paramref name="viewProj"/>, at <paramref name="lodRatio"/> (see <see cref="PlanetPatches"/>).
    /// </summary>
    private void DrawTerrain(CommandBuffer cb, Vector3 camera, in Matrix4x4 viewProj, float lodRatio)
    {
        PlanetPatches.Select(camera, lodRatio, _patches, viewProjection: viewProj);
        cb.BindIndexBuffer(_patchIndexBuffer, ByteSize.Zero, VkIndexType.UInt16);

        cb.BindPipeline(VkPipelineBindPoint.Graphics, _planetPipeline);
        foreach (var patch in _patches)
            DrawPatch(cb, patch, lodRatio, MaterialKind.Ground);

        // Opaque, and depth-tested and written like the ground, so it hides the seabed under it
        // (which the vertex shader pushes back in depth, so the two can't fight far off).
        foreach (var patch in _patches)
            if (patch.Water)
                DrawPatch(cb, patch, lodRatio, MaterialKind.Water);
    }

    /// <summary>
    /// The moon's ground as a mesh, if view <paramref name="view"/>'s camera (at
    /// <paramref name="camera"/>, seeing through <paramref name="viewProj"/>) is near enough that
    /// its uniforms leave the moon to it (<see cref="PatchBody.DrawsMoon"/>), its relief as much as
    /// that nearness draws (<see cref="PatchBody.MoonRelief"/>).
    /// </summary>
    private void DrawMoon(CommandBuffer cb, int view, Vector3 camera, in Matrix4x4 viewProj, float lodRatio)
    {
        var fromCamera = _moonFromCamera[view];
        var distance = fromCamera.Length();
        if (!PatchBody.DrawsMoon(distance))
            return;
        var body = PatchBody.Moon(camera + fromCamera, _moonOrientation, PatchBody.MoonRelief(distance));
        PlanetPatches.Select(body, camera, lodRatio, _moonPatches, viewProjection: viewProj);
        cb.BindIndexBuffer(_patchIndexBuffer, ByteSize.Zero, VkIndexType.UInt16);
        cb.BindPipeline(VkPipelineBindPoint.Graphics, _moonPipeline);
        foreach (var patch in _moonPatches)
            DrawPatch(cb, patch, lodRatio, MaterialKind.Ground, body.Radius, body.Relief);
    }

    private void DrawPatch(CommandBuffer cb, PlanetPatch patch, float lodRatio, MaterialKind kind, float radius = GroundBody.PlanetRadius, float relief = 1f)
    {
        var side = patch.Size * radius * MathF.PI / 4f;
        var carried = new Matrix4x4(
            patch.Face, patch.U0, patch.V0, patch.Size,
            GameOne.Scene.TerrainSkirt * side, lodRatio, relief, 0,
            0, 0, 0, 0,
            0, 0, 0, 0);
        PushDraw(cb, carried, kind);
        cb.DrawIndexed(_patchIndexCount, 1, 0, 0, 0);
    }
}
