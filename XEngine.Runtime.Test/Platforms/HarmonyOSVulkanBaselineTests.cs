// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Platforms.HarmonyOS;

using Xunit;

namespace XEngine.Runtime.Test.Platforms;

public sealed class HarmonyOSVulkanBaselineTests
{
    // A snapshot that satisfies every baseline requirement.
    private static HarmonyOSVulkanCapabilitySnapshot BaselineCompliantSnapshot(string deviceName = "Maleoon 910") => new()
    {
        ApiVersion = (1u << 22) | (2u << 12), // 1.2.0 — exceeds the 1.1 floor
        DeviceName = deviceName,
        MaxImageDimension2D = 8192,
        MaxPerStageDescriptorUniformBuffers = 15,
        MaxPerStageDescriptorStorageBuffers = 15,
        MaxBoundDescriptorSets = 8,
        MaxColorAttachments = 8,
        FramebufferColorSampleCounts = 0b1111, // 1|2|4|8
        SupportsSamplerAnisotropy = true,
        SupportsPipelineCache = true,
        HasGraphicsQueue = true,
        SupportsFifoPresentMode = true,
        SupportsTimestampQueries = true,
        SubgroupSize = 32,
        SupportsSubgroupBasic = true,
        SupportedFormats = new Dictionary<string, FormatSupport>
        {
            ["R8G8B8A8Srgb"] = FormatSupport.OptimalTiling,
            ["B8G8R8A8Srgb"] = FormatSupport.OptimalTiling,
            ["D32Sfloat"] = FormatSupport.OptimalTiling,
            ["D24UnormS8Uint"] = FormatSupport.OptimalTiling,
            ["ETC2R8G8B8UnormBlock"] = FormatSupport.OptimalTiling,
            ["Astc4x4UnormBlock"] = FormatSupport.OptimalTiling,
        },
    };

    [Fact]
    public void BaselineCompliantSnapshotVerifiesCleanly()
    {
        var failures = HarmonyOSVulkanBaseline.Verify(BaselineCompliantSnapshot());

        Assert.Empty(failures);
        Assert.True(HarmonyOSVulkanBaseline.IsBaselineMet(BaselineCompliantSnapshot()));
    }

    [Fact]
    public void ApiVersionBelowOnePointOneIsReported()
    {
        var snapshot = BaselineCompliantSnapshot() with { ApiVersion = 1u << 22 }; // 1.0.0

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Contains(failures, f => f.Contains("apiVersion") && f.Contains("1.1"));
    }

    [Fact]
    public void TextureDimensionBelowFloorIsReported()
    {
        var snapshot = BaselineCompliantSnapshot() with { MaxImageDimension2D = 2048 };

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Contains(failures, f => f.Contains("maxImageDimension2D") && f.Contains("4096"));
    }

    [Fact]
    public void MissingSamplerAnisotropyIsReported()
    {
        var snapshot = BaselineCompliantSnapshot() with { SupportsSamplerAnisotropy = false };

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Contains(failures, f => f.Contains("samplerAnisotropy"));
    }

    [Theory]
    [InlineData(nameof(HarmonyOSVulkanCapabilitySnapshot.MaxPerStageDescriptorUniformBuffers), 8)]
    [InlineData(nameof(HarmonyOSVulkanCapabilitySnapshot.MaxPerStageDescriptorStorageBuffers), 4)]
    [InlineData(nameof(HarmonyOSVulkanCapabilitySnapshot.MaxBoundDescriptorSets), 2)]
    [InlineData(nameof(HarmonyOSVulkanCapabilitySnapshot.MaxColorAttachments), 1)]
    public void DescriptorAndAttachmentLimitsBelowFloorAreReported(string fieldName, int belowFloor)
    {
        var snapshot = fieldName switch
        {
            nameof(HarmonyOSVulkanCapabilitySnapshot.MaxPerStageDescriptorUniformBuffers) =>
                BaselineCompliantSnapshot() with { MaxPerStageDescriptorUniformBuffers = belowFloor },
            nameof(HarmonyOSVulkanCapabilitySnapshot.MaxPerStageDescriptorStorageBuffers) =>
                BaselineCompliantSnapshot() with { MaxPerStageDescriptorStorageBuffers = belowFloor },
            nameof(HarmonyOSVulkanCapabilitySnapshot.MaxBoundDescriptorSets) =>
                BaselineCompliantSnapshot() with { MaxBoundDescriptorSets = belowFloor },
            _ => BaselineCompliantSnapshot() with { MaxColorAttachments = belowFloor },
        };

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.NotEmpty(failures);
    }

    [Fact]
    public void FramebufferLackingFourXMsaaIsReported()
    {
        var snapshot = BaselineCompliantSnapshot() with { FramebufferColorSampleCounts = 0b0011 }; // 1|2 only

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Contains(failures, f => f.Contains("framebufferColorSampleCounts") && f.Contains("4x"));
    }

    [Fact]
    public void MissingGraphicsQueueIsReported()
    {
        var snapshot = BaselineCompliantSnapshot() with { HasGraphicsQueue = false };

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Contains(failures, f => f.Contains("graphics-capable present queue"));
    }

    [Fact]
    public void MissingFifoPresentModeIsReported()
    {
        var snapshot = BaselineCompliantSnapshot() with { SupportsFifoPresentMode = false };

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Contains(failures, f => f.Contains("FIFO"));
    }

    [Fact]
    public void MissingTimestampQueriesAreReported()
    {
        var snapshot = BaselineCompliantSnapshot() with { SupportsTimestampQueries = false };

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Contains(failures, f => f.Contains("timestamp"));
    }

    [Fact]
    public void MissingSubgroupSupportIsReported()
    {
        var snapshot = BaselineCompliantSnapshot() with { SupportsSubgroupBasic = false };

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Contains(failures, f => f.Contains("subgroup"));
    }

    [Fact]
    public void UnsupportedRequiredFormatIsReportedButAstcStaysOptional()
    {
        // ASTC is optional-but-preferred; its absence must NOT fail the baseline gate.
        var formats = new Dictionary<string, FormatSupport>(BaselineCompliantSnapshot().SupportedFormats)
        {
            ["Astc4x4UnormBlock"] = FormatSupport.None,
            ["D32Sfloat"] = FormatSupport.None, // this one IS required
        };
        var snapshot = BaselineCompliantSnapshot() with { SupportedFormats = formats };

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Contains(failures, f => f.Contains("D32Sfloat"));
        Assert.DoesNotContain(failures, f => f.Contains("Astc4x4UnormBlock"));
    }

    [Fact]
    public void FormatSupportedOnlyViaLinearTilingStillPasses()
    {
        var formats = new Dictionary<string, FormatSupport>(BaselineCompliantSnapshot().SupportedFormats)
        {
            ["ETC2R8G8B8UnormBlock"] = FormatSupport.LinearTiling,
        };
        var snapshot = BaselineCompliantSnapshot() with { SupportedFormats = formats };

        var failures = HarmonyOSVulkanBaseline.Verify(snapshot);

        Assert.Empty(failures);
    }

    [Fact]
    public void CapabilityLogReportsBaselineTierWhenCompliant()
    {
        string log = HarmonyOSVulkanBaseline.FormatCapabilityLog(BaselineCompliantSnapshot("Test GPU"));

        Assert.Contains("tier=baseline", log);
        Assert.Contains("device=Test GPU", log);
        Assert.Contains("failures=none", log);
        Assert.Contains("api=1.2.0", log);
    }

    [Fact]
    public void CapabilityLogReportsLimitedTierAndFailuresWhenNonCompliant()
    {
        var snapshot = BaselineCompliantSnapshot() with
        {
            SupportsSamplerAnisotropy = false,
            MaxImageDimension2D = 2048,
        };

        string log = HarmonyOSVulkanBaseline.FormatCapabilityLog(snapshot);

        Assert.Contains("tier=limited", log);
        Assert.Contains("samplerAnisotropy", log);
        Assert.Contains("maxImageDimension2D", log);
    }
}
