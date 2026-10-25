// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.RHI;

/// <summary>
/// Stable, low-cardinality device facts used to select Android render tiers and to make
/// device evidence reproducible. The collection is populated once during device creation;
/// it is not intended for per-frame feature probing.
/// </summary>
public sealed class GraphicsCapabilityBaseline
{
    public string Backend { get; init; } = "Unknown";
    public string DeviceName { get; init; } = "Unknown";
    public string DeviceType { get; init; } = "Unknown";
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public uint ApiVersion { get; init; }
    public uint DriverVersion { get; init; }
    public string ApiVersionName { get; init; } = "0.0.0";
    public string DriverVersionName { get; init; } = "0.0.0";
    public string CapabilityTier { get; init; } = "limited";
    public int MaxTextureSize { get; init; }
    public bool SupportsSamplerAnisotropy { get; init; }
    public bool SupportsPipelineCache { get; init; }
    public IReadOnlyList<string> DeviceExtensions { get; init; } = Array.Empty<string>();

    public string ToStableLogString()
    {
        string extensions = string.Join(',', DeviceExtensions);
        return $"backend={Backend};device={DeviceName};type={DeviceType};vendor=0x{VendorId:X8};" +
            $"deviceId=0x{DeviceId:X8};api={ApiVersionName};driver={DriverVersionName};" +
            $"tier={CapabilityTier};maxTexture={MaxTextureSize};anisotropy={SupportsSamplerAnisotropy};" +
            $"pipelineCache={SupportsPipelineCache};extensions={extensions}";
    }

    public static string FormatVulkanVersion(uint version)
    {
        uint major = version >> 22;
        uint minor = (version >> 12) & 0x3FF;
        uint patch = version & 0xFFF;
        return $"{major}.{minor}.{patch}";
    }

    public static string ClassifyVulkanTier(uint apiVersion, int maxTextureSize, bool supportsAnisotropy)
    {
        // Android baseline requires Vulkan 1.1, a 4096 texture, and sampler anisotropy.
        // Devices below that remain runnable but must use the limited quality profile.
        return apiVersion >= ((1u << 22) | (1u << 12)) &&
            maxTextureSize >= 4096 && supportsAnisotropy
            ? "baseline"
            : "limited";
    }
}
