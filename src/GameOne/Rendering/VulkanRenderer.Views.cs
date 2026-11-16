using System.Numerics;
using Brutal.VulkanApi;
using GameOne.Physics;

namespace GameOne.Rendering;

// The small views: other vessels, each seen through its own camera in a rectangle over the main
// view. They are drawn into the same render pass, after the main view, so they cost no more than
// the pixels they cover and what they draw: the sky, coarser terrain, the pads and the tower, every
// stage, and the fire (plumes and explosions). The smoke and the lens flare are left out. Each is
// lit by the lit place nearest its own vessel (its LightSet, shared with any other view at the same
// place, the main one's included), with that place's floodlights' shadows from their own block of
// the shadow map; and each has the sun's shadows round its own vessel, from its own sun area of the
// shadow map (SunAreas: half the main view's resolution, over the same ground), or the area of a
// view whose vessel is close by (the main one's included).
internal sealed unsafe partial class VulkanRenderer
{
    /// <summary>Each small view's view-projection this frame, for choosing its terrain's patches.</summary>
    private readonly Matrix4x4[] _smallViewProj = new Matrix4x4[GameOne.Scene.MaxSmallViews];

    /// <summary>
    /// The main view's uniforms, but seen from <paramref name="view"/>'s camera through its own
    /// planes (its camera's, so one on the moon draws the planet in its sky as the main view does),
    /// shaped like its rectangle (small view <paramref name="index"/>), and lit by its own light set
    /// (of <paramref name="sets"/>).
    /// </summary>
    private FrameUniforms SmallViewUniforms(in FrameUniforms main, in ViewInput view, int index, ReadOnlySpan<LightSet> sets)
    {
        var rect = PixelRect(view);
        var aspect = rect.Extent.Height > 0 ? (float)rect.Extent.Width / rect.Extent.Height : 1f;
        var (near, far) = view.Clip;
        _eyes[1 + index] = view.CameraPosition;
        var projection = CameraProjection(float.DegreesToRadians(GameOne.Scene.CameraVerticalFovDegrees), aspect, near, far);
        var lookAt = Matrix4x4.CreateLookAt(view.CameraPosition, view.CameraTarget, view.CameraUp);
        var viewProj = lookAt * projection;
        var uniforms = main;
        uniforms.ViewProj = viewProj;
        _smallViewProj[index] = viewProj;
        uniforms.InvViewRays = ViewRays(lookAt, projection);
        uniforms.CameraPos = new Vector4(view.CameraPosition, main.CameraPos.W);
        uniforms.CameraClip = CameraClip(projection);
        // The moon from this view's own camera, worked out in double (ViewInput.MoonFromCamera).
        uniforms.Moon = new Vector4(view.MoonFromCamera, main.Moon.W);
        uniforms.SunDisc = view.SunDisc; // and the sun's disc as it sees it
        SetMoonMesh(ref uniforms, 1 + index);
        SetSun(ref uniforms, view.SunArea); // its own sun area's shadows (the main view's, or another's, if it shares one)
        // Its own lights, unless it shares the main view's (set 0, which the copy already has).
        if (view.LightSet != 0)
            SetLights(ref uniforms, (uint)view.LightSet < (uint)sets.Length ? sets[view.LightSet] : default);
        return uniforms;
    }

    /// <summary>Where <paramref name="view"/> goes on the swapchain image, in pixels, kept inside it.</summary>
    private VkRect2D PixelRect(in ViewInput view)
    {
        var x0 = Math.Clamp((int)MathF.Round(view.Min.X * _extent.Width), 0, _extent.Width);
        var y0 = Math.Clamp((int)MathF.Round(view.Min.Y * _extent.Height), 0, _extent.Height);
        var x1 = Math.Clamp((int)MathF.Round((view.Min.X + view.Size.X) * _extent.Width), x0, _extent.Width);
        var y1 = Math.Clamp((int)MathF.Round((view.Min.Y + view.Size.Y) * _extent.Height), y0, _extent.Height);
        return new VkRect2D
        {
            Offset = new VkOffset2D { X = x0, Y = y0 },
            Extent = new VkExtent2D { Width = x1 - x0, Height = y1 - y0 },
        };
    }

    private void DrawSmallViews(CommandBuffer cb, in FrameInput input, ReadOnlySpan<ViewInput> views)
    {
        if (views.IsEmpty)
            return;

        for (var i = 0; i < views.Length; i++)
        {
            var rect = PixelRect(views[i]);
            if (rect.Extent.Width == 0 || rect.Extent.Height == 0)
                continue;
            cb.SetViewport(0, [new VkViewport { X = rect.Offset.X, Y = rect.Offset.Y, Width = rect.Extent.Width, Height = rect.Extent.Height, MaxDepth = 1f }]);
            cb.SetScissor(0, [rect]);
            // Nothing is cleared: the sky, drawn first, covers every pixel and sample of the rectangle
            // and writes each one's colour and depth whatever was there (its depth test always
            // passes), so the main view's depth underneath is gone before anything tests against it.
            BindView(cb, 1 + i);

            DrawSky(cb);
            DrawTerrain(cb, views[i].CameraPosition, _smallViewProj[i], GameOne.Scene.SmallViewTerrainLodRatio);
            DrawMoon(cb, 1 + i, views[i].CameraPosition, _smallViewProj[i], GameOne.Scene.SmallViewTerrainLodRatio);
            DrawSolids(cb, input);
            DrawGlows(cb, input, views[i].CameraPosition); // (the lamps' glow sized for this view's camera, not the main one's)
        }

        // Back to the whole screen, and the main view's uniforms, for whatever comes next.
        cb.SetViewport(0, [new VkViewport { Width = _extent.Width, Height = _extent.Height, MaxDepth = 1f }]);
        cb.SetScissor(0, [new VkRect2D { Extent = _extent }]);
        BindView(cb, 0);
    }
}
