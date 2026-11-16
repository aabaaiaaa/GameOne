using Brutal.ShaderCApi;
using Brutal.VulkanApi;

namespace GameOne.Rendering;

// The sky: one triangle covering the screen, drawn first, behind everything but the moon it draws,
// whose depth it writes (else the far plane's), so the moon hides what is behind it. Its colour is the
// air's light along the way each pixel looks (Shaders' skyColor, marched as Air.Sky is), with the
// sun's disc; the same light is what the haze adds between the eye and distant things (fog), so
// the ground's far edge melts into it.
internal sealed unsafe partial class VulkanRenderer
{
    private VkShaderModule _skyVertex, _skyFragment;
    private VkPipeline _skyPipeline;

    private void CreateSkyResources()
    {
        _skyVertex = CreateShaderModule(Shaders.SkyVertex, ShaderKind.VertexShader, "sky.vert");
        _skyFragment = CreateShaderModule(Shaders.SkyFragment, ShaderKind.FragmentShader, "sky.frag");
    }

    /// <summary>Draws into the scene render pass, so it is rebuilt with it.</summary>
    private void CreateSkyPipeline(VkRenderPass renderPass) =>
        _skyPipeline = CreatePipeline(renderPass, _skyVertex, _skyFragment, _samples, PipelineStyle.Sky);

    private void DestroySkyPipeline() => _device.DestroyPipeline(_skyPipeline, null);

    private void DrawSky(CommandBuffer cb)
    {
        cb.BindPipeline(VkPipelineBindPoint.Graphics, _skyPipeline);
        cb.Draw(3, 1, 0, 0);
    }

    private void DestroySkyResources()
    {
        _device.DestroyShaderModule(_skyVertex, null);
        _device.DestroyShaderModule(_skyFragment, null);
    }
}
