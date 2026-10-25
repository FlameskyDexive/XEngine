using Silk.NET.Vulkan;
using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.RHI;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class VulkanVertexInputTests
{
    [Fact]
    public void UnconsumedMeshAttributes_CannotCollideWithReflectedLocations()
    {
        var format = new VertexFormat([
            new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3),
            new(VertexFormat.VertexSemantic.Normal, VertexFormat.VertexType.Float, 3),
        ]);
        uint normalSemantic = format.Elements[1].Semantic;
        var reflection = new ShaderVertexInputLayout
        {
            Inputs = [new ShaderVertexInputSlot(format.Elements[0].Semantic, normalSemantic)],
        };
        var attributes = new VertexInputAttributeDescription[2];
        int count = VulkanGraphicsDevice.AddVertexInputAttributes(attributes, 0, format.Elements, 0, false, reflection);
        Assert.Equal(1, count);
        Assert.Equal(normalSemantic, attributes[0].Location);
        Assert.Equal(0u, attributes[0].Offset);
    }

    [Fact]
    public void MissingReflection_RetainsLegacySemanticMapping()
    {
        var format = new VertexFormat([new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3)]);
        var attributes = new VertexInputAttributeDescription[1];
        Assert.Equal(1, VulkanGraphicsDevice.AddVertexInputAttributes(attributes, 0, format.Elements, 0, false, null));
        Assert.Equal(format.Elements[0].Semantic, attributes[0].Location);
    }
}
