using XEngine.Runtime.Backends.Vulkan;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class VulkanSwapchainClearTests
{
    [Theory]
    [InlineData(ClearFlags.Stencil, false)]
    [InlineData(ClearFlags.Depth, false)]
    [InlineData(ClearFlags.Depth | ClearFlags.Stencil, false)]
    [InlineData(ClearFlags.Color, true)]
    [InlineData(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, true)]
    public void MissingDepthStencil_DoesNotClearPresentedColor(ClearFlags flags, bool clear)
        => Assert.Equal(clear, VulkanCommandTranslator.ShouldClearSwapchainColor(flags));
}
