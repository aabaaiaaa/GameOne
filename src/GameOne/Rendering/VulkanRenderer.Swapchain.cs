using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace GameOne.Rendering;

// The swapchain and everything sized to the window: MSAA colour target, depth buffer, the main
// render pass and its framebuffers. All of it is rebuilt when the window is resized.
internal sealed unsafe partial class VulkanRenderer
{
    private VkSurfaceFormatKHR _surfaceFormat;
    private VkExtent2D _extent;
    private VkSwapchainKHR _swapchain;
    private VkRenderPass _renderPass;
    private VkPipeline _scenePipeline, _planetPipeline, _moonPipeline;
    private VkImage _msaaImage;
    private VkDeviceMemory _msaaMemory;
    private VkImageView _msaaView;
    private VkImage _depthImage;
    private VkDeviceMemory _depthMemory;
    private VkImageView _depthView;
    private VkImageView[] _imageViews = [];
    private VkFramebuffer[] _framebuffers = [];
    private VkSemaphore[] _renderFinished = []; // one per swapchain image, as presentation holds it
    private bool _swapchainDirty;

    private bool Msaa => _samples != VkSampleCountFlags._1Bit;

    private void CreateSwapchain()
    {
        var caps = _physicalDevice.GetSurfaceCapabilitiesKHR(_surface);

        // Windows reports the exact extent; -1 means "you choose", so use the framebuffer size.
        var fb = _window.FramebufferSize;
        _extent = caps.CurrentExtent.Width != -1
            ? caps.CurrentExtent
            : new VkExtent2D { Width = fb.X, Height = fb.Y };

        _surfaceFormat = ChooseSurfaceFormat();
        ActivePresentMode = ChoosePresentMode();

        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount != 0 && imageCount > caps.MaxImageCount)
            imageCount = caps.MaxImageCount;

        _swapchain = _device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = _surfaceFormat.Format,
                ImageColorSpace = _surfaceFormat.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = caps.CurrentTransform,
                CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
                PresentMode = ActivePresentMode,
                Clipped = true,
            },
            null
        );

        Span<VkImage> images = stackalloc VkImage[_device.GetSwapchainImagesKHR_Count(_swapchain)];
        _device.GetSwapchainImagesKHR_Write(_swapchain, images);

        _renderPass = CreateRenderPass(_surfaceFormat.Format);
        _scenePipeline = CreateScenePipeline(_renderPass);
        _planetPipeline = CreatePlanetPipeline(_renderPass);
        _moonPipeline = CreateMoonPipeline(_renderPass);
        CreateSkyPipeline(_renderPass);
        CreateFlarePipelines(_renderPass);
        CreateSmokePipeline(_renderPass);
        CreateGlowPipeline(_renderPass);
        if (Msaa)
            CreateMsaaTarget();
        CreateDepthTarget();

        _imageViews = new VkImageView[images.Length];
        _framebuffers = new VkFramebuffer[images.Length];
        _renderFinished = new VkSemaphore[images.Length];

        for (var i = 0; i < images.Length; i++)
        {
            _imageViews[i] = CreateView(images[i], _surfaceFormat.Format, VkImageAspectFlags.ColorBit);

            // With MSAA we draw into the shared multisampled image and resolve into the swapchain image.
            // The order must match the render pass attachments.
            Span<VkImageView> attachments = Msaa
                ? [_msaaView, _imageViews[i], _depthView]
                : [_imageViews[i], _depthView];
            fixed (VkImageView* attachmentsPtr = attachments)
                _framebuffers[i] = _device.CreateFramebuffer(
                    new VkFramebufferCreateInfo
                    {
                        RenderPass = _renderPass,
                        AttachmentCount = attachments.Length,
                        Attachments = attachmentsPtr,
                        Width = _extent.Width,
                        Height = _extent.Height,
                        Layers = 1,
                    },
                    null
                );

            _renderFinished[i] = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }

        _swapchainDirty = false;
    }

    private VkImageView CreateView(VkImage image, VkFormat format, VkImageAspectFlags aspect) =>
        _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = aspect,
                    LevelCount = 1,
                    LayerCount = 1,
                },
            },
            null
        );

    /// <summary>A device-local 2D image with its memory bound.</summary>
    private (VkImage, VkDeviceMemory) CreateImage(
        VkFormat format, int width, int height, VkSampleCountFlags samples, VkImageUsageFlags usage)
    {
        var image = _device.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = samples,
                Tiling = VkImageTiling.Optimal,
                Usage = usage,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            null
        );
        var memory = _device.AllocateMemory(_physicalDevice, image, VkMemoryPropertyFlags.DeviceLocalBit, null);
        _device.BindImageMemory(image, memory, ByteSize.Zero);
        return (image, memory);
    }

    // Both live only within the render pass (never read back), so they are marked transient and
    // tilers can keep them in on-chip memory.
    private void CreateMsaaTarget()
    {
        (_msaaImage, _msaaMemory) = CreateImage(
            _surfaceFormat.Format, _extent.Width, _extent.Height, _samples,
            VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransientAttachmentBit);
        _msaaView = CreateView(_msaaImage, _surfaceFormat.Format, VkImageAspectFlags.ColorBit);
    }

    private void CreateDepthTarget()
    {
        (_depthImage, _depthMemory) = CreateImage(
            _depthFormat, _extent.Width, _extent.Height, _samples,
            VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.TransientAttachmentBit);
        _depthView = CreateView(_depthImage, _depthFormat, VkImageAspectFlags.DepthBit);
    }

    /// <summary>
    /// The views' depth format: D32SFloat, which the reversed depth (<see cref="CameraProjection"/>)
    /// needs, as its precision is a float's relative step at every distance (in 24 bits fixed it
    /// would be some 190 m at the moon's distance, and the sky's moon a metre under its lowest ground
    /// would flicker); the renderer refuses to start without it.
    /// </summary>
    private static VkFormat CameraDepthFormat(PhysicalDevice physicalDevice)
    {
        var props = new VkFormatProperties();
        physicalDevice.GetFormatProperties(VkFormat.D32SFloat, ref props);
        return (props.OptimalTilingFeatures & VkFormatFeatureFlags.DepthStencilAttachmentBit) != 0
            ? VkFormat.D32SFloat
            : throw new InvalidOperationException("this GPU can't render to a 32-bit float depth buffer (D32SFloat), which the views' reversed depth needs");
    }

    /// <summary>The first depth format the GPU can render to (and, if asked, sample from).</summary>
    private static VkFormat ChooseDepthFormat(PhysicalDevice physicalDevice, bool sampled)
    {
        var needed = VkFormatFeatureFlags.DepthStencilAttachmentBit;
        if (sampled)
            needed |= VkFormatFeatureFlags.SampledImageBit | VkFormatFeatureFlags.SampledImageFilterLinearBit;

        foreach (var format in (ReadOnlySpan<VkFormat>)[VkFormat.D32SFloat, VkFormat.D24UNormS8UInt, VkFormat.D16UNorm])
        {
            var props = new VkFormatProperties();
            physicalDevice.GetFormatProperties(format, ref props);
            if ((props.OptimalTilingFeatures & needed) == needed)
                return format;
        }

        throw new InvalidOperationException("No supported depth format.");
    }

    private VkSurfaceFormatKHR ChooseSurfaceFormat()
    {
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[
            _physicalDevice.GetSurfaceFormatsKHR_Count(_surface)
        ];
        _physicalDevice.GetSurfaceFormatsKHR_Write(_surface, formats);

        // UNORM writes colour values straight through, so #F1C40F in the shader is #F1C40F on screen,
        // matching the branding PNG pixel for pixel.
        foreach (var f in formats)
            if (f.Format is VkFormat.B8G8R8A8UNorm or VkFormat.R8G8B8A8UNorm)
                return f;
        return formats[0];
    }

    /// <summary>How the swapchain presents frames: see <see cref="ChoosePresentMode"/>.</summary>
    public VkPresentModeKHR ActivePresentMode { get; private set; } = VkPresentModeKHR.FifoKHR;

    /// <summary>
    /// FIFO (vsync, always available) normally. Without vsync, immediate if the driver has it, else
    /// mailbox (never waits, drops frames the display can't show), else FIFO after all.
    /// </summary>
    private VkPresentModeKHR ChoosePresentMode()
    {
        if (_vsync)
            return VkPresentModeKHR.FifoKHR;
        Span<VkPresentModeKHR> modes = stackalloc VkPresentModeKHR[_physicalDevice.GetSurfacePresentModesKHR_Count(_surface)];
        _physicalDevice.GetSurfacePresentModesKHR_Write(_surface, modes);
        if (modes.Contains(VkPresentModeKHR.ImmediateKHR))
            return VkPresentModeKHR.ImmediateKHR;
        if (modes.Contains(VkPresentModeKHR.MailboxKHR))
            return VkPresentModeKHR.MailboxKHR;
        return VkPresentModeKHR.FifoKHR;
    }

    private void DestroySwapchain()
    {
        foreach (var semaphore in _renderFinished)
            _device.DestroySemaphore(semaphore, null);
        foreach (var framebuffer in _framebuffers)
            _device.DestroyFramebuffer(framebuffer, null);
        foreach (var view in _imageViews)
            _device.DestroyImageView(view, null);
        if (_msaaImage.IsNotNull())
        {
            _device.DestroyImageView(_msaaView, null);
            _device.DestroyImage(_msaaImage, null);
            _device.FreeMemory(_msaaMemory, null);
            _msaaImage = default;
        }
        _device.DestroyImageView(_depthView, null);
        _device.DestroyImage(_depthImage, null);
        _device.FreeMemory(_depthMemory, null);
        _device.DestroyPipeline(_scenePipeline, null);
        _device.DestroyPipeline(_planetPipeline, null);
        _device.DestroyPipeline(_moonPipeline, null);
        DestroySkyPipeline();
        DestroyFlarePipelines();
        DestroySmokePipeline();
        DestroyGlowPipeline();
        _device.DestroyRenderPass(_renderPass, null);
        _device.DestroySwapchainKHR(_swapchain, null);
    }

    private void RecreateSwapchain()
    {
        // A minimised window has a 0x0 framebuffer, which is not a valid swapchain extent.
        if (_window.FramebufferSize.X == 0 || _window.FramebufferSize.Y == 0)
            return;

        _device.WaitIdle();
        DestroySwapchain();
        CreateSwapchain();
    }

    private VkRenderPass CreateRenderPass(VkFormat format)
    {
        Span<VkAttachmentDescription> attachments =
        [
            // 0: what we draw into. With MSAA it is the multisampled image, discarded after the resolve.
            new()
            {
                Format = format,
                Samples = _samples,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = Msaa ? VkAttachmentStoreOp.DontCare : VkAttachmentStoreOp.Store,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = VkImageLayout.Undefined,
                FinalLayout = Msaa ? VkImageLayout.ColorAttachmentOptimal : VkImageLayout.PresentSrcKHR,
            },
            // 1 (MSAA only): the swapchain image the samples are resolved into.
            new()
            {
                Format = format,
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = VkAttachmentLoadOp.DontCare,
                StoreOp = VkAttachmentStoreOp.Store,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = VkImageLayout.Undefined,
                FinalLayout = VkImageLayout.PresentSrcKHR,
            },
            // Last: depth. Cleared every frame and not needed afterwards.
            new()
            {
                Format = _depthFormat,
                Samples = _samples,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.DontCare,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = VkImageLayout.Undefined,
                FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
            },
        ];
        if (!Msaa) // no resolve attachment: depth moves up to index 1
            attachments[1] = attachments[2];
        var attachmentCount = Msaa ? 3 : 2;

        var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var resolveRef = new VkAttachmentReference { Attachment = 1, Layout = VkImageLayout.ColorAttachmentOptimal };
        var depthRef = new VkAttachmentReference
        {
            Attachment = attachmentCount - 1,
            Layout = VkImageLayout.DepthStencilAttachmentOptimal,
        };
        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorRef,
            ResolveAttachments = Msaa ? &resolveRef : null,
            DepthStencilAttachment = &depthRef,
        };
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
        };

        fixed (VkAttachmentDescription* attachmentsPtr = attachments)
            return _device.CreateRenderPass(
                new VkRenderPassCreateInfo
                {
                    AttachmentCount = attachmentCount,
                    Attachments = attachmentsPtr,
                    SubpassCount = 1,
                    Subpasses = &subpass,
                    DependencyCount = 1,
                    Dependencies = &dependency,
                },
                null
            );
    }
}
