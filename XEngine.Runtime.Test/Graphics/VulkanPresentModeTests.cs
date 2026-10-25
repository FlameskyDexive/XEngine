using Silk.NET.Vulkan;
using XEngine.Runtime.Backends.Vulkan;
using Xunit;

namespace XEngine.Runtime.Test;

public class VulkanPresentModeTests
{
    [Fact]
    public void SecondarySurfaceDoesNotRequireMailboxSupport()
    {
        Assert.Equal(PresentModeKHR.FifoKhr, VulkanGraphicsDevice.SelectPresentMode(true, [PresentModeKHR.MailboxKhr, PresentModeKHR.FifoKhr]));
        Assert.Equal(PresentModeKHR.MailboxKhr, VulkanGraphicsDevice.SelectPresentMode(false, [PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr]));
        Assert.Equal(PresentModeKHR.ImmediateKhr, VulkanGraphicsDevice.SelectPresentMode(false, [PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr]));
        Assert.Equal(PresentModeKHR.FifoKhr, VulkanGraphicsDevice.SelectPresentMode(false, [PresentModeKHR.FifoKhr]));
    }
}
