// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Backends.Metal;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// M3: explicit Metal bind-points from <see cref="ShaderDescriptorLayoutPlan"/>.
/// Per-kind indices (buffer / texture / sampler), not argument buffers.
/// </summary>
public class MetalBindPointTests
{
    [Fact]
    public void LogicalRegisters_MapToPerKindMetalIndices()
    {
        var layout = new ShaderBindingLayout
        {
            Buffers =
            [
                new ShaderBindingSlot(ShaderBindingKind.Buffer, slot: 2, name: "Globals",
                    stages: ShaderStageMask.Vertex | ShaderStageMask.Fragment),
            ],
            Textures =
            [
                new ShaderBindingSlot(ShaderBindingKind.Texture, slot: 3, name: "MainTexture",
                    stages: ShaderStageMask.Fragment, resourceShape: ShaderResourceShape.Texture2D),
            ],
            Samplers =
            [
                new ShaderBindingSlot(ShaderBindingKind.Sampler, slot: 4, name: "MainSampler",
                    stages: ShaderStageMask.Fragment),
            ],
        };

        ShaderDescriptorLayoutPlan plan = ShaderDescriptorLayoutPlan.Create(layout);
        MetalBindPoint[] points = MetalBindPoints.ToBindPoints(plan);
        Assert.Equal(3, points.Length);

        MetalBindPoint globals = MetalBindPoints.Find(plan, "Globals");
        Assert.Equal(MetalBindKind.Buffer, globals.Kind);
        Assert.Equal(2, globals.Index);
        Assert.True(globals.Vertex);
        Assert.True(globals.Fragment);

        MetalBindPoint texture = MetalBindPoints.Find(plan, "MainTexture");
        Assert.Equal(MetalBindKind.Texture, texture.Kind);
        Assert.Equal(3, texture.Index);
        Assert.False(texture.Vertex);
        Assert.True(texture.Fragment);

        MetalBindPoint sampler = MetalBindPoints.Find(plan, "MainSampler");
        Assert.Equal(MetalBindKind.Sampler, sampler.Kind);
        Assert.Equal(4, sampler.Index);
        Assert.False(sampler.Vertex);
        Assert.True(sampler.Fragment);
    }

    [Fact]
    public void PhysicalBinding_WinsOverLogicalSlot()
    {
        var layout = new ShaderBindingLayout
        {
            Buffers =
            [
                new ShaderBindingSlot(ShaderBindingKind.Buffer, slot: 2, name: "Globals",
                    physicalBinding: 7, physicalSpace: 0),
            ],
            Textures =
            [
                new ShaderBindingSlot(ShaderBindingKind.Texture, slot: 3, name: "MainTexture",
                    physicalBinding: 8, physicalSpace: 0),
            ],
            Samplers =
            [
                new ShaderBindingSlot(ShaderBindingKind.Sampler, slot: 4, name: "MainSampler",
                    physicalBinding: 9, physicalSpace: 0),
            ],
        };

        ShaderDescriptorLayoutPlan plan = ShaderDescriptorLayoutPlan.Create(layout);
        Assert.Equal(7, MetalBindPoints.Find(plan, "Globals").Index);
        Assert.Equal(MetalBindKind.Buffer, MetalBindPoints.Find(plan, "Globals").Kind);
        Assert.Equal(8, MetalBindPoints.Find(plan, "MainTexture").Index);
        Assert.Equal(MetalBindKind.Texture, MetalBindPoints.Find(plan, "MainTexture").Kind);
        Assert.Equal(9, MetalBindPoints.Find(plan, "MainSampler").Index);
        Assert.Equal(MetalBindKind.Sampler, MetalBindPoints.Find(plan, "MainSampler").Kind);
    }

    [Fact]
    public void BindPoints_AreNotFoldedIntoAnArgumentBuffer()
    {
        var layout = new ShaderBindingLayout
        {
            Buffers = [new ShaderBindingSlot(ShaderBindingKind.Buffer, 0, "Constants")],
            Textures = [new ShaderBindingSlot(ShaderBindingKind.Texture, 0, "Albedo")],
            Samplers = [new ShaderBindingSlot(ShaderBindingKind.Sampler, 0, "AlbedoSampler")],
        };

        MetalBindPoint[] points = MetalBindPoints.ToBindPoints(ShaderDescriptorLayoutPlan.Create(layout));
        Assert.Equal(3, points.Length);
        Assert.Equal(MetalBindKind.Buffer, points[0].Kind);
        Assert.Equal(0, points[0].Index);
        Assert.Equal(MetalBindKind.Texture, points[1].Kind);
        Assert.Equal(0, points[1].Index);
        Assert.Equal(MetalBindKind.Sampler, points[2].Kind);
        Assert.Equal(0, points[2].Index);
    }
}
