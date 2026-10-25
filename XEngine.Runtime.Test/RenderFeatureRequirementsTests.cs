// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class RenderFeatureRequirementsTests
{
    [Fact]
    public void ScreenSpaceEffectsDeclareOnlyTheBuffersTheyConsume()
    {
        var gtao = new GTAOEffect { UseTemporal = false };
        Assert.Equal(RenderFeatureRequirements.Depth | RenderFeatureRequirements.Normals, gtao.Requirements);

        gtao.UseTemporal = true;
        Assert.Equal(RenderFeatureRequirements.Depth | RenderFeatureRequirements.Normals |
            RenderFeatureRequirements.MotionVectors, gtao.Requirements);

        var bokeh = new BokehDepthOfFieldEffect { UseAutoFocus = false };
        Assert.Equal(RenderFeatureRequirements.None, bokeh.Requirements);
        bokeh.UseAutoFocus = true;
        Assert.Equal(RenderFeatureRequirements.Depth, bokeh.Requirements);

        Assert.Equal(RenderFeatureRequirements.None, new CinematicEffects().Requirements);
    }

    [Fact]
    public void PrepassIsSkippedOnlyWhenDepthCanBeInitializedByTheColorPass()
    {
        Assert.False(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Skybox, RenderFeatureRequirements.None));
        Assert.True(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.SolidColor, RenderFeatureRequirements.MotionVectors));
        Assert.True(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Depth, RenderFeatureRequirements.None));
        Assert.True(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Nothing, RenderFeatureRequirements.None));
    }

    [Fact]
    public void EditorGridAndGizmos_DoNotForcePrepass()
    {
        // Grid/gizmos consume a depth-only copy of the color RT depth instead of a prepass,
        // so an empty editor scene (Skybox clear, no feature inputs) must skip the prepass.
        Assert.False(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Skybox, RenderFeatureRequirements.None));
    }
}
