using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

public sealed class GraphicsCapabilityBaselineTests
{
    [Theory]
    [InlineData(0x00402000u, "1.2.0")]
    [InlineData(0x00403012u, "1.3.18")]
    public void VulkanVersionIsDecoded(uint packed, string expected)
    {
        Assert.Equal(expected, GraphicsCapabilityBaseline.FormatVulkanVersion(packed));
    }

    [Fact]
    public void TierRequiresVulkan11TextureAndAnisotropy()
    {
        uint vulkan11 = (1u << 22) | (1u << 12);

        Assert.Equal("baseline", GraphicsCapabilityBaseline.ClassifyVulkanTier(vulkan11, 4096, true));
        Assert.Equal("limited", GraphicsCapabilityBaseline.ClassifyVulkanTier(vulkan11, 2048, true));
        Assert.Equal("limited", GraphicsCapabilityBaseline.ClassifyVulkanTier(vulkan11, 4096, false));
        Assert.Equal("limited", GraphicsCapabilityBaseline.ClassifyVulkanTier(1u << 22, 8192, true));
    }

    [Fact]
    public void StableLogStringPreservesSortedExtensionOrder()
    {
        var baseline = new GraphicsCapabilityBaseline
        {
            Backend = "Vulkan",
            DeviceName = "Test GPU",
            DeviceType = "IntegratedGpu",
            VendorId = 0x1234,
            DeviceId = 0x5678,
            ApiVersionName = "1.1.0",
            DriverVersionName = "1.2.3",
            CapabilityTier = "baseline",
            MaxTextureSize = 8192,
            SupportsSamplerAnisotropy = true,
            SupportsPipelineCache = true,
            DeviceExtensions = new[] { "VK_A", "VK_B" },
        };

        Assert.Equal(
            "backend=Vulkan;device=Test GPU;type=IntegratedGpu;vendor=0x00001234;deviceId=0x00005678;api=1.1.0;driver=1.2.3;tier=baseline;maxTexture=8192;anisotropy=True;pipelineCache=True;extensions=VK_A,VK_B",
            baseline.ToStableLogString());
    }
}
