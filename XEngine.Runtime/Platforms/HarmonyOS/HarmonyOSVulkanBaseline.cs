// This file is part of the XEngine game engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Platforms.HarmonyOS;

/// <summary>
/// The minimum Vulkan capability envelope a HarmonyOS device must meet before the player is
/// allowed to render. The plan (§4.2) forbids substituting a "Vulkan 1.1" version check for
/// feature/format/limit verification: a device below this envelope must use a quality downgrade
/// or fail at build/init, never draw into a silently-broken pipeline and crash later.
/// </summary>
/// <remarks>
/// This is a pure data validator so it can be unit-tested without a live Vulkan device. The
/// <see cref="VulkanGraphicsDevice"/> captures the observed facts into a snapshot and asks the
/// baseline to verify them on the first frame.
/// </remarks>
public static class HarmonyOSVulkanBaseline
{
    /// <summary>Required Vulkan instance/device API version (major.minor encoded by Silk).</summary>
    public const uint RequiredApiVersion = (1u << 22) | (1u << 12); // 1.1.0

    /// <summary>Minimum 2D texture dimension the default pipeline depends on (shadow maps, atlases).</summary>
    public const int RequiredMaxTextureSize = 4096;

    /// <summary>Minimum per-stage uniform buffer bindings (UBO array limits).</summary>
    public const int RequiredMaxPerStageDescriptorUniformBuffers = 15;

    /// <summary>Minimum per-stage storage buffer bindings (compute / instancing data).</summary>
    public const int RequiredMaxPerStageDescriptorStorageBuffers = 15;

    /// <summary>Minimum descriptor set slots the default pipeline binds per draw.</summary>
    public const int RequiredMaxBoundDescriptorSets = 4;

    /// <summary>Minimum framebuffer color attachments (render targets / MRT).</summary>
    public const int RequiredMaxColorAttachments = 4;

    /// <summary>Minimum MSAA sample bits the quality tier may use (4x).</summary>
    public const int RequiredFramebufferColorSampleBits = 4;

    /// <summary>
    /// Formats the pipeline requires; the device must support at least optimal or linear tiling.
    /// Focuses on mobile-relevant compression (ETC2 baseline, ASTC optional); BC7 is a desktop
    /// format and is intentionally excluded from the HarmonyOS envelope.
    /// </summary>
    public static readonly IReadOnlyCollection<string> RequiredFormats =
    [
        "R8G8B8A8Srgb",         // default swapchain / render-target color
        "B8G8R8A8Srgb",         // alternate swapchain color
        "D32Sfloat",            // primary depth
        "D24UnormS8Uint",       // fallback depth-stencil
        "ETC2R8G8B8UnormBlock", // HarmonyOS baseline texture compression
        "Astc4x4UnormBlock",    // optional higher-quality compression (preferred when present)
    ];

    /// <summary>
    /// Verifies a captured device snapshot against the baseline. Returns the list of unmet
    /// requirements (empty when the device is baseline-compliant). The caller decides whether a
    /// non-empty list is a hard failure (Release build) or a downgrade signal (Development).
    /// </summary>
    public static IReadOnlyList<string> Verify(HarmonyOSVulkanCapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var failures = new List<string>();

        if (snapshot.ApiVersion < RequiredApiVersion)
            failures.Add($"apiVersion={GraphicsCapabilityBaseline.FormatVulkanVersion(snapshot.ApiVersion)} < 1.1");

        if (snapshot.MaxImageDimension2D < RequiredMaxTextureSize)
            failures.Add($"maxImageDimension2D={snapshot.MaxImageDimension2D} < {RequiredMaxTextureSize}");

        if (!snapshot.SupportsSamplerAnisotropy)
            failures.Add("samplerAnisotropy=false");

        if (!snapshot.SupportsPipelineCache)
            failures.Add("pipelineCache=false");

        if (snapshot.MaxPerStageDescriptorUniformBuffers < RequiredMaxPerStageDescriptorUniformBuffers)
            failures.Add($"maxPerStageDescriptorUniformBuffers={snapshot.MaxPerStageDescriptorUniformBuffers} < {RequiredMaxPerStageDescriptorUniformBuffers}");

        if (snapshot.MaxPerStageDescriptorStorageBuffers < RequiredMaxPerStageDescriptorStorageBuffers)
            failures.Add($"maxPerStageDescriptorStorageBuffers={snapshot.MaxPerStageDescriptorStorageBuffers} < {RequiredMaxPerStageDescriptorStorageBuffers}");

        if (snapshot.MaxBoundDescriptorSets < RequiredMaxBoundDescriptorSets)
            failures.Add($"maxBoundDescriptorSets={snapshot.MaxBoundDescriptorSets} < {RequiredMaxBoundDescriptorSets}");

        if (snapshot.MaxColorAttachments < RequiredMaxColorAttachments)
            failures.Add($"maxColorAttachments={snapshot.MaxColorAttachments} < {RequiredMaxColorAttachments}");

        if ((snapshot.FramebufferColorSampleCounts & RequiredFramebufferColorSampleBits) == 0)
            failures.Add($"framebufferColorSampleCounts=0x{snapshot.FramebufferColorSampleCounts:X} lacks 4x");

        if (!snapshot.HasGraphicsQueue)
            failures.Add("no graphics-capable present queue");

        if (!snapshot.SupportsFifoPresentMode)
            failures.Add("FIFO present mode unavailable (required baseline)");

        if (!snapshot.SupportsTimestampQueries)
            failures.Add("timestamp queries unavailable (required for frame diagnostics)");

        if (snapshot.SubgroupSize == 0 || !snapshot.SupportsSubgroupBasic)
            failures.Add("subgroup operations unavailable (shader variant selection depends on them)");

        // Required formats: each must be reported with at least optimal or linear tiling support.
        foreach (string format in RequiredFormats)
        {
            if (!snapshot.SupportedFormats.TryGetValue(format, out FormatSupport support) ||
                (support & (FormatSupport.OptimalTiling | FormatSupport.LinearTiling)) == 0)
            {
                // ASTC is optional-but-preferred; downgrade, do not block the baseline gate.
                if (format == "Astc4x4UnormBlock")
                    continue;
                failures.Add($"format {format} unsupported");
            }
        }

        return failures;
    }

    /// <summary>
    /// True when the snapshot meets the baseline (no unmet requirements). Convenience over
    /// <see cref="Verify"/> for the common init-time gate.
    /// </summary>
    public static bool IsBaselineMet(HarmonyOSVulkanCapabilitySnapshot snapshot)
        => Verify(snapshot).Count == 0;

    /// <summary>
    /// One-line, redaction-safe capability summary suitable for the first-frame capability log
    /// the plan (§4.2) requires. Contains only device facts, never paths, identifiers or secrets.
    /// </summary>
    public static string FormatCapabilityLog(HarmonyOSVulkanCapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyList<string> failures = Verify(snapshot);
        string tier = failures.Count == 0 ? "baseline" : "limited";
        string formatSummary = string.Join(',', snapshot.SupportedFormats.Keys);
        string failureSummary = failures.Count == 0 ? "none" : string.Join("; ", failures);
        return $"[HarmonyOS-Vulkan] tier={tier};api={GraphicsCapabilityBaseline.FormatVulkanVersion(snapshot.ApiVersion)};" +
            $"device={snapshot.DeviceName};maxTexture2D={snapshot.MaxImageDimension2D};" +
            $"anisotropy={snapshot.SupportsSamplerAnisotropy};pipelineCache={snapshot.SupportsPipelineCache};" +
            $"subgroupSize={snapshot.SubgroupSize};queueGraphics={snapshot.HasGraphicsQueue};" +
            $"fifoPresent={snapshot.SupportsFifoPresentMode};formats={formatSummary};" +
            $"failures={failureSummary}";
    }
}

/// <summary>
/// Device-captured Vulkan facts that <see cref="HarmonyOSVulkanBaseline"/> verifies. Populated by
/// the device on init from <c>vkGetPhysicalDevice*</c> queries; never retains identifiers or paths.
/// </summary>
public sealed record HarmonyOSVulkanCapabilitySnapshot
{
    public required uint ApiVersion { get; init; }
    public required string DeviceName { get; init; } = "Unknown";
    public required int MaxImageDimension2D { get; init; }
    public required int MaxPerStageDescriptorUniformBuffers { get; init; }
    public required int MaxPerStageDescriptorStorageBuffers { get; init; }
    public required int MaxBoundDescriptorSets { get; init; }
    public required int MaxColorAttachments { get; init; }
    public required int FramebufferColorSampleCounts { get; init; }
    public required bool SupportsSamplerAnisotropy { get; init; }
    public required bool SupportsPipelineCache { get; init; }
    public required bool HasGraphicsQueue { get; init; }
    public required bool SupportsFifoPresentMode { get; init; }
    public required bool SupportsTimestampQueries { get; init; }
    public required int SubgroupSize { get; init; }
    public required bool SupportsSubgroupBasic { get; init; }
    public required IReadOnlyDictionary<string, FormatSupport> SupportedFormats { get; init; } =
        new Dictionary<string, FormatSupport>(0);
}

/// <summary>
/// Mirrors <c>VkFormatFeatureFlagBits</c> for the subset the baseline inspects. Kept as plain
/// flags so the snapshot stays free of Silk Vulkan enum coupling and stays unit-testable.
/// </summary>
[Flags]
public enum FormatSupport
{
    None = 0,
    SampledImage = 1 << 0,
    OptimalTiling = 1 << 1,
    LinearTiling = 1 << 2,
}
