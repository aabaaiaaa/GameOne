using System.Numerics;
using Brutal.VulkanApi;
using GameOne.Physics;

namespace GameOne.Rendering;

// Shadow mapping. Each frame the booster and the tower are first drawn from the sun's point of view
// into a depth texture (the shadow map). The scene pass then compares each pixel's distance from the
// sun with what the sun "saw" there: if something nearer was recorded, the pixel is in shadow.
//
// The sun is so far off that its rays are parallel, so its view is orthographic: a box along its
// rays. That box (a SunArea) follows the booster, so its shadow is sharp wherever it flies; near the
// pad it stretches to take in the pad and tower too. Each small view whose vessel is away from the
// main one has a box of its own, round its vessel in the same way (SunAreas; views close together
// share one).
//
// The lit places' floodlights cast shadows too, each from its own perspective view, into the same
// map. The map is laid out in cells of ShadowCell (1024²), three rows high:
//
//   - the main view's sun area: two by two cells (2048²) in the top left, the sharpest;
//   - then every other cell, one each: the two under the main view's area, then column by column
//     to its right, top to bottom. The first ShadowAreas - 1 are the small views' sun areas (each
//     over the same ground a main view's would cover there, at half its resolution: a small view
//     can't show finer), the rest the floodlight tiles, PadLights.MaxShadowed to a block: tile
//     MaxShadowed × b + i is floodlight i of the light set given block b (LightSet.Block). Tile t
//     is spotViewProj[t], and while it is drawn every draw carries t in its material's w (_spotShadow).
//
// So 1 + 5 + 24 squares in 11 x 3 cells (11264 x 3072 texels; 132 MiB at 32-bit depth), none
// wasted. The GLSL's spotTiles table is written from SpotTileRect, and each view's own sun area is
// in its uniforms (sunArea), so the shaders find every square where the renderer drew it, each
// lookup kept a texel inside its own square. Each frame the shadow pass clears and draws only the
// squares in use (each sun area chosen, and the tiles of the light sets with floods casting
// shadows), so views far from every lit place, and views close together, cost nothing more.
internal sealed unsafe partial class VulkanRenderer
{
    /// <summary>The shadow map's unit: a small view's sun area, or a floodlight's tile, is one cell square.</summary>
    private const int ShadowCell = 1024;

    /// <summary>The main view's sun area is this many cells each way (2048²).</summary>
    private const int MainSunCells = 2;

    private const int ShadowRows = 3;

    /// <summary>The shadow map's sun areas, and its floodlight blocks: one each per view (the main one and each small one). The GLSL's <c>SHADOW_AREAS</c>.</summary>
    internal const int ShadowAreas = 1 + GameOne.Scene.MaxSmallViews;

    /// <summary>Every floodlight tile: <see cref="PadLights.MaxShadowed"/> in each of <see cref="ShadowAreas"/> blocks (<c>spotViewProj</c>'s length, the GLSL's <c>SPOT_TILES</c>).</summary>
    internal const int SpotTiles = PadLights.MaxShadowed * ShadowAreas;

    /// <summary>The cells beside the main view's sun area that are under it; the rest are whole columns to its right.</summary>
    private const int UnderMainCells = MainSunCells * (ShadowRows - MainSunCells);

    private const int ShadowColumns = MainSunCells + (ShadowAreas - 1 + SpotTiles - UnderMainCells + ShadowRows - 1) / ShadowRows;

    internal const int ShadowMapWidth = ShadowColumns * ShadowCell, ShadowMapHeight = ShadowRows * ShadowCell;

    /// <summary>Cell <paramref name="n"/> of those besides the main view's sun area, its corner in texels: under that area first, then column by column to its right.</summary>
    private static (int X, int Y) OtherCell(int n) => n < UnderMainCells
        ? (n % MainSunCells * ShadowCell, (MainSunCells + n / MainSunCells) * ShadowCell)
        : ((MainSunCells + (n - UnderMainCells) / ShadowRows) * ShadowCell, (n - UnderMainCells) % ShadowRows * ShadowCell);

    /// <summary>Sun area <paramref name="area"/>'s square of the shadow map, in texels: the main view's (0) two cells across, a small view's one.</summary>
    internal static (int X, int Y, int Size) SunAreaRect(int area)
    {
        if (area == 0)
            return (0, 0, MainSunCells * ShadowCell);
        var (x, y) = OtherCell(area - 1);
        return (x, y, ShadowCell);
    }

    /// <summary>Floodlight tile <paramref name="tile"/>'s square of the shadow map, in texels (after the small views' sun areas).</summary>
    internal static (int X, int Y, int Size) SpotTileRect(int tile)
    {
        var (x, y) = OtherCell(ShadowAreas - 1 + tile);
        return (x, y, ShadowCell);
    }

    /// <summary><paramref name="rect"/> (a square of the shadow map, in texels) as a fraction of the map: corner, then size. A view's <c>sunArea</c>, and the GLSL's <c>spotTiles</c>.</summary>
    internal static Vector4 MapFraction((int X, int Y, int Size) rect) =>
        new((float)rect.X / ShadowMapWidth, (float)rect.Y / ShadowMapHeight, (float)rect.Size / ShadowMapWidth, (float)rect.Size / ShadowMapHeight);

    private readonly VkFormat _shadowFormat;
    private VkImage _shadowImage;
    private VkDeviceMemory _shadowMemory;
    private VkImageView _shadowView;
    private VkSampler _shadowSampler;
    private VkRenderPass _shadowRenderPass;
    private VkFramebuffer _shadowFramebuffer;
    private VkPipeline _shadowPipeline, _spotShadowPipeline;

    /// <summary>While a floodlight's shadow is drawn, which (its tile, numbered across every block: MaxShadowed × block + its place in it, and its view: <c>spotViewProj</c>); otherwise -1. Every draw carries it, in its material's w.</summary>
    private int _spotShadow = -1;

    /// <summary>The parts near the floodlights casting shadows, or in a sun area, each frame (filled in place: nothing is allocated).</summary>
    private readonly List<StagePart> _nearParts = [];

    /// <summary>This frame's sun areas' views (<see cref="FrameInput.SunAreas"/>, each as <see cref="SunViewProjection"/> makes it for its square), their texels' size in metres, and how many there are.</summary>
    private readonly Matrix4x4[] _sunViewProj = new Matrix4x4[ShadowAreas];
    private readonly float[] _sunTexel = new float[ShadowAreas];
    private readonly SunArea[] _sunAreaList = new SunArea[ShadowAreas];
    private int _sunAreas;

    /// <summary>Each square of the shadow map the pass clears this frame (filled in place).</summary>
    private readonly VkClearRect[] _shadowClears = new VkClearRect[ShadowAreas + SpotTiles];

    private void CreateShadowResources(in VkPhysicalDeviceLimits limits)
    {
        // Every desktop GPU takes 16384 (this laptop's both do); Vulkan only promises 4096, so a
        // device that can't hold the whole map is refused at start-up rather than drawn wrongly.
        var most = Math.Min(limits.MaxImageDimension2D, Math.Min(limits.MaxFramebufferWidth, limits.MaxFramebufferHeight));
        if (Math.Max(ShadowMapWidth, ShadowMapHeight) > most)
            throw new InvalidOperationException($"the shadow map is {ShadowMapWidth} x {ShadowMapHeight}, but this GPU's images and framebuffers go to {most}");
        (_shadowImage, _shadowMemory) = CreateImage(
            _shadowFormat, ShadowMapWidth, ShadowMapHeight, VkSampleCountFlags._1Bit, // the sun's areas and the floodlights' tiles: see the top
            VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit);
        _shadowView = CreateView(_shadowImage, _shadowFormat, VkImageAspectFlags.DepthBit);

        // A comparison sampler: sampling returns "is the reference depth <= the stored depth", and
        // linear filtering blends four such results, which gives slightly soft shadow edges for free.
        // Anything outside the map reads as the far plane (white), i.e. lit.
        _shadowSampler = _device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Nearest,
                AddressModeU = VkSamplerAddressMode.ClampToBorder,
                AddressModeV = VkSamplerAddressMode.ClampToBorder,
                AddressModeW = VkSamplerAddressMode.ClampToBorder,
                BorderColor = VkBorderColor.FloatOpaqueWhite,
                CompareEnable = true,
                CompareOp = VkCompareOp.LessOrEqual,
                MaxAnisotropy = 1f,
            },
            null
        );

        var attachment = new VkAttachmentDescription
        {
            Format = _shadowFormat,
            Samples = VkSampleCountFlags._1Bit,
            // Not cleared as a whole: the pass clears each square it draws (RecordShadowPass), and
            // what it leaves is never read, as every lookup stays inside a square in use.
            LoadOp = VkAttachmentLoadOp.DontCare,
            StoreOp = VkAttachmentStoreOp.Store, // the scene pass reads it
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        var depthRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            DepthStencilAttachment = &depthRef,
        };

        // There is one shadow map shared by both frames in flight, so:
        //  - before writing it, wait for the previous frame's scene pass to finish reading it;
        //  - after writing it, make the depth visible to this frame's scene pass.
        Span<VkSubpassDependency> dependencies =
        [
            new()
            {
                SrcSubpass = VK.SUBPASS_EXTERNAL,
                DstSubpass = 0,
                SrcStageMask = VkPipelineStageFlags.FragmentShaderBit,
                SrcAccessMask = VkAccessFlags.ShaderReadBit,
                DstStageMask = VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
                DstAccessMask = VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
            },
            new()
            {
                SrcSubpass = 0,
                DstSubpass = VK.SUBPASS_EXTERNAL,
                SrcStageMask = VkPipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = VkAccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = VkPipelineStageFlags.FragmentShaderBit,
                DstAccessMask = VkAccessFlags.ShaderReadBit,
            },
        ];

        fixed (VkSubpassDependency* dependenciesPtr = dependencies)
            _shadowRenderPass = _device.CreateRenderPass(
                new VkRenderPassCreateInfo
                {
                    AttachmentCount = 1,
                    Attachments = &attachment,
                    SubpassCount = 1,
                    Subpasses = &subpass,
                    DependencyCount = dependencies.Length,
                    Dependencies = dependenciesPtr,
                },
                null
            );

        var view = _shadowView;
        _shadowFramebuffer = _device.CreateFramebuffer(
            new VkFramebufferCreateInfo
            {
                RenderPass = _shadowRenderPass,
                AttachmentCount = 1,
                Attachments = &view,
                Width = ShadowMapWidth,
                Height = ShadowMapHeight,
                Layers = 1,
            },
            null
        );

        _shadowPipeline = CreateShadowPipeline(_shadowRenderPass);
        _spotShadowPipeline = CreatePipeline(_shadowRenderPass, _spotShadowVertex, _shadowFragment, VkSampleCountFlags._1Bit, PipelineStyle.Shadow);
    }

    /// <summary>
    /// The sun "camera" for <paramref name="area"/>: looking along the rays of the sun (towards
    /// <paramref name="sun"/>) at a box round it, drawn into a square <paramref name="size"/> texels
    /// across. Also returns how wide one of those texels is, in metres. Its up is the north pole,
    /// not +Y: the sun goes overhead at Home at noon, along +Y, which would leave the view with no
    /// way up, but it never lies along the pole (it keeps to the equator but for the tilt).
    /// </summary>
    internal static (Matrix4x4 ViewProj, float TexelSize) SunViewProjection(SunArea area, int size, Vector3 sun)
    {
        const float back = 2000f; // far enough up the sun's rays that nothing is behind the camera
        var view = Matrix4x4.CreateLookAt(area.Centre + sun * back, area.Centre, GroundBody.Planet.NorthPole);
        var projection = Matrix4x4.CreateOrthographic(2 * area.Radius, 2 * area.Radius, 1f, 2 * back);
        projection.M22 = -projection.M22; // Vulkan's clip space is Y-down, as in PerspectiveVulkan
        return (view * projection, 2 * area.Radius / size);
    }

    /// <summary>This frame's sun areas' views, for the uniforms and the shadow pass (at most <see cref="ShadowAreas"/>; the main view's first).</summary>
    private void SetSunAreas(in FrameInput input)
    {
        _sunAreas = FillSunAreas(input.SunAreas, input.CameraPosition, _sunAreaList);
        for (var i = 0; i < _sunAreas; i++)
            (_sunViewProj[i], _sunTexel[i]) = SunViewProjection(_sunAreaList[i], SunAreaRect(i).Size, input.SunDirection);
    }

    /// <summary>
    /// Copies <paramref name="areas"/> (at most <see cref="ShadowAreas"/>) into <paramref name="into"/>,
    /// and says how many. GameView always chooses the main view's first, but if none comes (a
    /// frame handed over without) the main view's is the one round its camera, as for a wreck:
    /// with no area there'd be a 0 by 0 render area and an empty clear in the shadow pass (both
    /// forbidden by Vulkan) and every view would read a sun matrix left from an earlier frame.
    /// </summary>
    internal static int FillSunAreas(ReadOnlySpan<SunArea> areas, Vector3 camera, Span<SunArea> into)
    {
        if (areas.Length == 0)
        {
            into[0] = SunAreas.Around(null, camera);
            return 1;
        }
        var count = Math.Min(areas.Length, ShadowAreas);
        areas[..count].CopyTo(into);
        return count;
    }

    /// <summary>A view's sun shadows: its area's view, the lookup's nudge (a texel and a half of its area: enough to stop acne), and where its area is in the map.</summary>
    private void SetSun(ref FrameUniforms uniforms, int area)
    {
        if ((uint)area >= (uint)_sunAreas)
            area = 0; // (none chosen for it: the main view's, as before small views had their own)
        uniforms.LightViewProj = _sunViewProj[area];
        uniforms.SunDir.W = 1.5f * _sunTexel[area];
        uniforms.SunArea = MapFraction(SunAreaRect(area));
    }

    private static VkRect2D Texels((int X, int Y, int Size) rect) =>
        new() { Offset = new VkOffset2D { X = rect.X, Y = rect.Y }, Extent = new VkExtent2D { Width = rect.Size, Height = rect.Size } };

    /// <summary>
    /// Whose uniforms draw sun area <paramref name="area"/> (their light matrix is that area's): 0,
    /// the main view's, for the first; 1 + i for the first small view i chosen for it; -1 if no
    /// drawn small view has it (the HUD left that view out), so it is neither cleared nor drawn.
    /// </summary>
    internal static int SunAreaOwner(int area, ReadOnlySpan<ViewInput> views)
    {
        if (area == 0)
            return 0;
        for (var i = 0; i < views.Length; i++)
            if (views[i].SunArea == area)
                return 1 + i;
        return -1;
    }

    private void RecordShadowPass(CommandBuffer cb, in FrameInput input, ReadOnlySpan<ViewInput> views)
    {
        // Only the squares in use are cleared and drawn: each sun area a drawn view has, and the
        // tiles of the light sets with floods casting shadows. The pass's area is the smallest
        // rectangle round them all; what is in it but not in use is undefined (the attachment's
        // load is DontCare), and never read, as every lookup is kept inside its own square.
        var clears = 0;
        for (var area = 0; area < _sunAreas; area++)
            if (SunAreaOwner(area, views) >= 0)
                _shadowClears[clears++] = new VkClearRect { Rect = Texels(SunAreaRect(area)), LayerCount = 1 };
        foreach (var set in input.LightSets)
            for (var i = 0; i < Shadowed(set); i++)
                _shadowClears[clears++] = new VkClearRect { Rect = Texels(SpotTileRect(set.Block * PadLights.MaxShadowed + i)), LayerCount = 1 };
        var (right, bottom) = (0, 0);
        foreach (var clear in _shadowClears.AsSpan(0, clears))
            (right, bottom) = (Math.Max(right, clear.Rect.Offset.X + (int)clear.Rect.Extent.Width), Math.Max(bottom, clear.Rect.Offset.Y + (int)clear.Rect.Extent.Height));
        cb.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = _shadowRenderPass,
                Framebuffer = _shadowFramebuffer,
                RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = right, Height = bottom } },
            },
            VkSubpassContents.Inline
        );
        cb.ClearAttachments(
            [new VkClearAttachment { AspectMask = VkImageAspectFlags.DepthBit, ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1f } } }],
            _shadowClears.AsSpan(0, clears));

        // Each sun area, from the uniforms of the first view it is chosen for (whose light matrix is
        // that area's): the main view's for the first, a small view's for each of the others.
        cb.BindPipeline(VkPipelineBindPoint.Graphics, _shadowPipeline);
        for (var area = 0; area < _sunAreas; area++)
        {
            var owner = SunAreaOwner(area, views);
            if (owner < 0)
                continue; // (no view of it is drawn)
            BindView(cb, owner);
            var rect = SunAreaRect(area);
            cb.SetViewport(0, [new VkViewport { X = rect.X, Y = rect.Y, Width = rect.Size, Height = rect.Size, MaxDepth = 1f }]);
            cb.SetScissor(0, [Texels(rect)]);
            DrawCasters(cb, input, new SunCull(_sunViewProj[area], _sunAreaList[area].Radius));
        }
        // (The floodlights' views are the same in every view's uniforms; the scene pass is the main view's.)
        BindView(cb, 0);

        // Each light set's floodlights' shadows (the main view's place's, and each other place a
        // small view is at), each into its own tile, as each sees them (spotViewProj): none for a
        // set with no place near.
        foreach (var set in input.LightSets)
        {
            var shadowed = Shadowed(set);
            if (shadowed == 0)
                continue;
            // Only what is within the floodlights' reach of the place they light (the place of the
            // set's first light, its own): its tower and masts, and the vessels there. Not every
            // vessel in the world, four times over.
            var at = set.Lights.Span[0].Position;
            var reach = GameOne.Scene.FloodRange + GameOne.Scene.MastOut + 40f;
            _nearParts.Clear();
            foreach (var part in input.Parts)
                if (Vector3.Distance(_eyes[0] + part.Model.Translation, at) < reach + 60f) // (a vessel's length beyond; its model from the main camera)
                    _nearParts.Add(part);
            cb.BindPipeline(VkPipelineBindPoint.Graphics, _spotShadowPipeline);
            for (var i = 0; i < shadowed; i++)
            {
                _spotShadow = set.Block * PadLights.MaxShadowed + i;
                var tile = SpotTileRect(_spotShadow);
                cb.SetViewport(0, [new VkViewport { X = tile.X, Y = tile.Y, Width = tile.Size, Height = tile.Size, MaxDepth = 1f }]);
                cb.SetScissor(0, [Texels(tile)]);
                // What can stand in a floodlight's beam: the towers, the masts and the vessels (no floe or ship is ever at a pad).
                DrawSites(cb, pads: false, near: at, within: reach);
                DrawLitPlaces(cb, near: at, within: reach);
                DrawStages(cb, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nearParts));
            }
            _spotShadow = -1;
        }

        cb.EndRenderPass();
    }

    /// <summary>
    /// A sun area's box, to leave out what is outside it: a point is in it if it falls inside its
    /// square seen down the sun's rays (its view is orthographic, so that is its clip x and y),
    /// widened by how far what stands there reaches.
    /// </summary>
    private readonly record struct SunCull(Matrix4x4 ViewProj, float Radius)
    {
        public bool Sees(Vector3 p, float reach)
        {
            var clip = Vector3.Transform(p, ViewProj);
            var edge = 1f + reach / Radius;
            return MathF.Abs(clip.X) <= edge && MathF.Abs(clip.Y) <= edge;
        }
    }

    /// <summary>How far a site's tower and pad, a lit place's masts, or a drone ship reach from its origin, at most, for <see cref="SunCull"/>.</summary>
    private const float SiteReach = 100f;

    /// <summary>How far a vessel reaches from its bottom stage's origin, at most (as the floodlights' shadows take it).</summary>
    private const float VesselReach = 60f;

    /// <summary>What casts the sun's shadows into one of its areas: only what is in it (a small view's area need not draw every vessel in the world).</summary>
    private void DrawCasters(CommandBuffer cb, in FrameInput input, SunCull cull)
    {
        DrawSites(cb, pads: false, cull: cull);
        DrawLitPlaces(cb, cull: cull);
        foreach (var ship in input.DroneShips)
            if (cull.Sees(ship.Translation, SiteReach))
                Draw(cb, _droneShipMesh, ship, MaterialKind.Deck);
        // The floes: one instanced draw (of those near a view's camera) into every area, not culled
        // to it: what is outside is clipped. Measured over the floe band (2,129 floes, four views,
        // four areas), leaving them out of every area saves about 0.2 ms a frame, so culling them
        // per area isn't worth an instance buffer per area.
        DrawFloes(cb);
        _nearParts.Clear();
        foreach (var part in input.Parts)
            if (cull.Sees(_eyes[0] + part.Model.Translation, VesselReach)) // (its model from the main camera)
                _nearParts.Add(part);
        DrawStages(cb, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nearParts));
    }

    private void DestroyShadowResources()
    {
        _device.DestroyPipeline(_shadowPipeline, null);
        _device.DestroyPipeline(_spotShadowPipeline, null);
        _device.DestroyFramebuffer(_shadowFramebuffer, null);
        _device.DestroyRenderPass(_shadowRenderPass, null);
        _device.DestroySampler(_shadowSampler, null);
        _device.DestroyImageView(_shadowView, null);
        _device.DestroyImage(_shadowImage, null);
        _device.FreeMemory(_shadowMemory, null);
    }
}
