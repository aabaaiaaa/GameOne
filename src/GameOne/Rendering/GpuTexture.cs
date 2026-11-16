using Brutal;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace GameOne.Rendering;

/// <summary>
/// An image in device-local memory (an RGBA8 picture, or raw texels in layers), plus the view and
/// sampler a shader needs to read it.
/// </summary>
internal sealed unsafe class GpuTexture : IDisposable
{
    private readonly Device _device;

    public VkImage Image { get; }
    public VkDeviceMemory Memory { get; }
    public VkImageView View { get; }
    public VkSampler Sampler { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Decodes an image file with stb_image and uploads it as RGBA8, filtered linearly.</summary>
    public GpuTexture(Device device, Queue queue, VkCommandPool commandPool, string path)
    {
        _device = device;
        using var pixels = Stb.Load(path, desiredChannels: 4);
        if (pixels.IsNull())
            throw new FileNotFoundException($"Could not load image '{path}'.", path);
        Width = pixels.Width;
        Height = pixels.Height;
        (Image, Memory, View) = Upload(device, queue, commandPool, VkFormat.R8G8B8A8UNorm, Width, Height, 1, pixels.AsSpan());
        Sampler = CreateSampler(device, VkFilter.Linear);
    }

    /// <summary>
    /// Uploads raw texels as a 2D array of <paramref name="layers"/> images, each
    /// <paramref name="width"/> by <paramref name="height"/>, layer after layer and row after row in
    /// <paramref name="pixels"/>, with a nearest sampler (the shaders read it with <c>texelFetch</c>), or
    /// with <paramref name="linear"/> a linear one (the format must be one the device filters: Vulkan requires it of R16_SFLOAT,
    /// R16G16B16A16_SFLOAT and R8G8B8A8_UNORM). With <paramref name="mipLevels"/> over 1, <paramref name="pixels"/> holds every level in turn, base
    /// first, each half the last's size each way, all its layers in turn, and a linear sampler reads them trilinearly.
    /// </summary>
    public GpuTexture(Device device, Queue queue, VkCommandPool commandPool, VkFormat format, int width, int height, int layers, ReadOnlySpan<byte> pixels, bool linear = false, int mipLevels = 1)
    {
        _device = device;
        Width = width;
        Height = height;
        (Image, Memory, View) = Upload(device, queue, commandPool, format, width, height, layers, pixels, mipLevels);
        Sampler = CreateSampler(device, linear ? VkFilter.Linear : VkFilter.Nearest, mipLevels);
    }

    /// <summary>
    /// Pixels go into a host-visible staging buffer, then a one-off command buffer copies them into
    /// an optimally-tiled GPU image (a 2D array when there is more than one layer).
    /// </summary>
    private static (VkImage, VkDeviceMemory, VkImageView) Upload(
        Device device, Queue queue, VkCommandPool commandPool, VkFormat format, int width, int height, int layers, ReadOnlySpan<byte> pixels, int mipLevels = 1)
    {
        var physicalDevice = device.PhysicalDevice;
        var byteCount = ByteSize.Of((ulong)pixels.Length);

        // 1. Staging buffer the CPU can write into.
        var staging = device.CreateBuffer(
            new VkBufferCreateInfo
            {
                Size = byteCount,
                Usage = VkBufferUsageFlags.TransferSrcBit,
                SharingMode = VkSharingMode.Exclusive,
            },
            null
        );
        var stagingMemory = device.AllocateMemory(
            physicalDevice,
            staging,
            VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            null
        );
        device.BindBufferMemory(staging, stagingMemory, ByteSize.Zero);

        var mapped = device.MapMemory(stagingMemory);
        pixels.CopyTo(new Span<byte>(mapped, pixels.Length));
        device.UnmapMemory(stagingMemory);

        // 2. The GPU image itself.
        var image = device.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
                MipLevels = mipLevels,
                ArrayLayers = layers,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            null
        );
        var memory = device.AllocateMemory(physicalDevice, image, VkMemoryPropertyFlags.DeviceLocalBit, null);
        device.BindImageMemory(image, memory, ByteSize.Zero);

        // 3. Record and submit: transition -> copy -> transition to shader-readable.
        var cb = device.AllocateCommandBuffer(
            new VkCommandBufferAllocateInfo { CommandPool = commandPool, Level = VkCommandBufferLevel.Primary }
        );
        cb.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });

        var range = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            LevelCount = mipLevels,
            LayerCount = layers,
        };
        cb.PipelineBarrier(
            VkPipelineStageFlags.TopOfPipeBit,
            VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    DstAccessMask = VkAccessFlags.TransferWriteBit,
                    OldLayout = VkImageLayout.Undefined,
                    NewLayout = VkImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = range,
                },
            ]
        );
        // One copy a level, each from where the last one's texels end.
        long texels = 0;
        for (var level = 0; level < mipLevels; level++)
            texels += (long)Math.Max(width >> level, 1) * Math.Max(height >> level, 1) * layers;
        var texelBytes = TexelBytes(format);
        if (pixels.Length != texels * texelBytes)
            throw new ArgumentException($"{format} at {width}x{height}, {layers} layers and {mipLevels} levels needs {texels * texelBytes} bytes, not {pixels.Length}", nameof(pixels));
        var copies = new VkBufferImageCopy[mipLevels];
        long offset = 0;
        for (var level = 0; level < mipLevels; level++)
        {
            int w = Math.Max(width >> level, 1), h = Math.Max(height >> level, 1);
            copies[level] = new VkBufferImageCopy
            {
                BufferOffset = ByteSize.Of((ulong)offset),
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level, LayerCount = layers },
                ImageExtent = new VkExtent3D { Width = w, Height = h, Depth = 1 },
            };
            offset += (long)w * h * layers * texelBytes;
        }
        cb.CopyBufferToImage(staging, image, VkImageLayout.TransferDstOptimal, copies);
        cb.PipelineBarrier(
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferWriteBit,
                    DstAccessMask = VkAccessFlags.ShaderReadBit,
                    OldLayout = VkImageLayout.TransferDstOptimal,
                    NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = range,
                },
            ]
        );
        cb.End();

        var fence = device.CreateFence(VkFenceCreateFlags.None, null);
        queue.Submit([], [], [cb], [], fence);
        device.WaitForFence(fence, -1);

        device.DestroyFence(fence, null);
        device.FreeCommandBuffers(commandPool, [cb]);
        device.DestroyBuffer(staging, null);
        device.FreeMemory(stagingMemory, null);

        // 4. How shaders see it.
        var view = device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = layers > 1 ? VkImageViewType._2DArray : VkImageViewType._2D,
                Format = format,
                SubresourceRange = range,
            },
            null
        );
        return (image, memory, view);
    }

    /// <summary>A texel's size in bytes, for the formats uploaded here.</summary>
    private static int TexelBytes(VkFormat format) => format switch
    {
        VkFormat.R8UInt => 1,
        VkFormat.R16UNorm or VkFormat.R16SFloat or VkFormat.R8G8UInt => 2,
        VkFormat.R8G8B8A8UNorm => 4,
        VkFormat.R16G16B16A16SFloat => 8,
        _ => throw new ArgumentException($"no texel size known for {format}: add it here", nameof(format)),
    };

    private static VkSampler CreateSampler(Device device, VkFilter filter, int mipLevels = 1) =>
        device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = filter,
                MinFilter = filter,
                MipmapMode = filter == VkFilter.Linear ? VkSamplerMipmapMode.Linear : VkSamplerMipmapMode.Nearest,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
                MaxAnisotropy = 1f,
                MaxLod = mipLevels - 1,
            },
            null
        );

    public void Dispose()
    {
        _device.DestroySampler(Sampler, null);
        _device.DestroyImageView(View, null);
        _device.DestroyImage(Image, null);
        _device.FreeMemory(Memory, null);
    }
}
