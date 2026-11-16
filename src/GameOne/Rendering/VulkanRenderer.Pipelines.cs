using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;

namespace GameOne.Rendering;

// Shader modules, and one pipeline builder for every kind of draw (see PipelineStyle).
internal sealed unsafe partial class VulkanRenderer
{
    private VkShaderModule CreateShaderModule(string source, ShaderKind kind, string name)
    {
        using var compiler = ShaderC.CreateCompiler();
        using var spirv = compiler.Compile(source, kind, name);

        return _device.CreateShaderModule(
            new VkShaderModuleCreateInfo { CodeSize = spirv.GetLength(), Code = (int*)spirv.GetBytes() },
            null
        );
    }

    /// <summary>What a pipeline is for, which decides its blending and depth state.</summary>
    private enum PipelineStyle
    {
        /// <summary>Lit, alpha-blended, depth-tested and depth-writing: the scene itself.</summary>
        Scene,

        /// <summary>Depth only, with a slope-scaled bias: the shadow map.</summary>
        Shadow,

        /// <summary>
        /// Covers the screen behind everything, drawn first: the sky. Its depth always passes, and is
        /// written: the moon's or the sun's where it covers the pixel, else the far plane's
        /// (<c>gl_FragDepth</c>), so each hides what is behind it.
        /// </summary>
        Sky,

        /// <summary>
        /// Alpha-blended and depth-tested, but not depth-writing: see-through effects such as the
        /// floor smoke, which mustn't hide whatever is drawn behind it afterwards.
        /// </summary>
        Translucent,

        /// <summary>Additive and always on top (no depth): light-like effects such as the lens flare.</summary>
        Additive,

        /// <summary>
        /// Additive, depth-tested but not depth-writing: fire, which lights up what is behind it but
        /// is hidden by anything solid in front.
        /// </summary>
        Glow,

        /// <summary>
        /// Draws nothing at all (no colour, no depth test or write), but still produces fragments, so
        /// an occlusion query around it counts every pixel it covers, hidden or not.
        /// </summary>
        Probe,

        /// <summary>
        /// Like <see cref="Probe"/>, but depth-tested (without writing depth), so an occlusion query
        /// around it counts only the pixels where nothing in the scene is in front.
        /// </summary>
        ProbeOccluded,
    }

    /// <summary>Lit, alpha-blended, multisampled: draws into the main render pass.</summary>
    private VkPipeline CreateScenePipeline(VkRenderPass renderPass) =>
        CreatePipeline(renderPass, _sceneVertex, _sceneFragment, _samples, PipelineStyle.Scene);

    /// <summary>
    /// The scene pipeline for the terrain's patches (the ground's and the sea's), which build their
    /// own vertices: no vertex buffer is read, and back faces (the far sides of hills) are culled.
    /// </summary>
    private VkPipeline CreatePlanetPipeline(VkRenderPass renderPass) =>
        CreatePipeline(renderPass, _planetVertex, _sceneFragment, _samples, PipelineStyle.Scene, vertexBuffer: false);

    /// <summary>The moon's ground up close, its patches built as the planet's are, with its own shading (no air, no sea).</summary>
    private VkPipeline CreateMoonPipeline(VkRenderPass renderPass) =>
        CreatePipeline(renderPass, _moonVertex, _moonFragment, _samples, PipelineStyle.Scene, vertexBuffer: false);

    /// <summary>Depth only, from the light: draws into the shadow map's render pass.</summary>
    private VkPipeline CreateShadowPipeline(VkRenderPass renderPass) =>
        CreatePipeline(renderPass, _shadowVertex, _shadowFragment, VkSampleCountFlags._1Bit, PipelineStyle.Shadow);

    private VkPipeline CreatePipeline(
        VkRenderPass renderPass,
        VkShaderModule vertexShader,
        VkShaderModule fragmentShader,
        VkSampleCountFlags samples,
        PipelineStyle style,
        bool vertexBuffer = true)
    {
        var colorOutput = style != PipelineStyle.Shadow;
        var depthBias = style == PipelineStyle.Shadow;
        var depthTest = style is PipelineStyle.Scene or PipelineStyle.Shadow or PipelineStyle.Sky or PipelineStyle.Translucent or PipelineStyle.Glow or PipelineStyle.ProbeOccluded;
        var depthWrite = style is PipelineStyle.Scene or PipelineStyle.Shadow or PipelineStyle.Sky;
        var entryPoint = "main"u8;
        fixed (byte* main = entryPoint)
        {
            Span<VkPipelineShaderStageCreateInfo> stages =
            [
                new() { Stage = VkShaderStageFlags.VertexBit, Module = vertexShader, Name = main },
                new() { Stage = VkShaderStageFlags.FragmentBit, Module = fragmentShader, Name = main },
            ];

            var binding = new VkVertexInputBindingDescription
            {
                Binding = 0,
                Stride = ByteSize.Of<MeshVertex>(),
                InputRate = VkVertexInputRate.Vertex,
            };
            Span<VkVertexInputAttributeDescription> attributes =
            [
                new() { Location = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.OffsetOf<MeshVertex>(nameof(MeshVertex.Position)) },
                new() { Location = 1, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.OffsetOf<MeshVertex>(nameof(MeshVertex.Normal)) },
                new() { Location = 2, Format = VkFormat.R32G32SFloat, Offset = ByteSize.OffsetOf<MeshVertex>(nameof(MeshVertex.Uv)) },
                new() { Location = 3, Format = VkFormat.R32G32B32A32SFloat, Offset = ByteSize.OffsetOf<MeshVertex>(nameof(MeshVertex.Color)) },
                new() { Location = 4, Format = VkFormat.R32SFloat, Offset = ByteSize.OffsetOf<MeshVertex>(nameof(MeshVertex.Textured)) },
            ];

            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
            var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                PolygonMode = VkPolygonMode.Fill,
                // Everything is drawn from both sides, except the terrain, whose back faces are hidden anyway.
                CullMode = vertexBuffer ? VkCullModeFlags.None : VkCullModeFlags.BackBit,
                FrontFace = VkFrontFace.CounterClockwise,
                LineWidth = 1f,
                // Pushes shadow-map depths slightly away from the light, so lit surfaces don't
                // test as being behind themselves. The slope term handles surfaces seen at an angle.
                DepthBiasEnable = depthBias,
                DepthBiasConstantFactor = depthBias ? 1.5f : 0f,
                DepthBiasSlopeFactor = depthBias ? 2.0f : 0f,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = samples };
            var depthStencil = new VkPipelineDepthStencilStateCreateInfo
            {
                DepthTestEnable = depthTest,
                DepthWriteEnable = depthWrite,
                // The camera's depth is reversed (CameraProjection: nearer is greater); the shadow map's
                // is its own, not reversed.
                DepthCompareOp = style switch
                {
                    PipelineStyle.Sky => VkCompareOp.Always,
                    PipelineStyle.Shadow => VkCompareOp.Less,
                    _ => VkCompareOp.Greater,
                },
            };

            // Scene and translucent: standard alpha blending (the logo's anti-aliased edges, the smoke).
            // Additive and glow: light adds to what's there. Probe: writes no colour at all.
            var additive = style is PipelineStyle.Additive or PipelineStyle.Glow;
            var blendAttachment = new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = additive ? VkBlendFactor.One : VkBlendFactor.SrcAlpha,
                DstColorBlendFactor = additive ? VkBlendFactor.One : VkBlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = VkBlendOp.Add,
                SrcAlphaBlendFactor = additive ? VkBlendFactor.Zero : VkBlendFactor.One,
                DstAlphaBlendFactor = additive ? VkBlendFactor.One : VkBlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = style is PipelineStyle.Probe or PipelineStyle.ProbeOccluded
                    ? VkColorComponentFlags.None
                    : VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
            };
            var blend = new VkPipelineColorBlendStateCreateInfo
            {
                AttachmentCount = colorOutput ? 1 : 0,
                Attachments = colorOutput ? &blendAttachment : null,
            };
            Span<VkDynamicState> dynamicStates = [VkDynamicState.Viewport, VkDynamicState.Scissor];

            fixed (VkDynamicState* dynamicStatesPtr = dynamicStates)
            fixed (VkPipelineShaderStageCreateInfo* stagesPtr = stages)
            fixed (VkVertexInputAttributeDescription* attributesPtr = attributes)
            {
                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    VertexBindingDescriptionCount = vertexBuffer ? 1 : 0,
                    VertexBindingDescriptions = vertexBuffer ? &binding : null,
                    VertexAttributeDescriptionCount = vertexBuffer ? attributes.Length : 0,
                    VertexAttributeDescriptions = vertexBuffer ? attributesPtr : null,
                };
                var dynamic = new VkPipelineDynamicStateCreateInfo
                {
                    DynamicStateCount = dynamicStates.Length,
                    DynamicStates = dynamicStatesPtr,
                };

                VkPipeline pipeline;
                var result = _device.CreateGraphicsPipelines(
                    default,
                    [
                        new VkGraphicsPipelineCreateInfo
                        {
                            StageCount = stages.Length,
                            Stages = stagesPtr,
                            VertexInputState = &vertexInput,
                            InputAssemblyState = &inputAssembly,
                            ViewportState = &viewportState,
                            RasterizationState = &raster,
                            MultisampleState = &multisample,
                            DepthStencilState = &depthStencil,
                            ColorBlendState = &blend,
                            DynamicState = &dynamic,
                            Layout = _pipelineLayout,
                            RenderPass = renderPass,
                            Subpass = 0,
                        },
                    ],
                    null,
                    new ReadOnlySpan<VkPipeline>(&pipeline, 1)
                );
                if (result != VkResult.Success)
                    throw new InvalidOperationException($"vkCreateGraphicsPipelines failed: {result}");
                return pipeline;
            }
        }
    }
}
