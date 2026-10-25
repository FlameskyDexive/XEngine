// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.RHI;

/// <summary>Device limits and feature flags queried after <see cref="IGraphicsDevice.Initialize"/>.</summary>
public sealed class GraphicsDeviceCapabilities
{
    public int MaxTextureSize { get; init; } = 16384;
    public int MaxCubeMapTextureSize { get; init; } = 16384;
    public int MaxArrayTextureLayers { get; init; } = 2048;
    public int MaxFramebufferColorAttachments { get; init; } = 8;

    /// <summary>Maximum frames the backend may keep in flight before retiring GPU work.</summary>
    public int MaxFramesInFlight { get; init; } = 2;

    public bool SupportsCompute { get; init; }
    public bool SupportsGeometryShader { get; init; }

    /// <summary>
    /// Multisample counts usable for color render targets, as a bitmask in which the bit whose
    /// value equals the sample count is set: 1 = 1×, 2 = 2×, 4 = 4×, 8 = 8×, 16 = 16×. This is the
    /// same encoding as Vulkan's <c>VkSampleCountFlags</c>, so a backend can forward the driver
    /// limit unchanged. Defaults to 1 (single-sample only) for backends without MSAA support.
    /// </summary>
    public int SupportedColorSampleCounts { get; init; } = 1;

    /// <summary>Depth/stencil counterpart of <see cref="SupportedColorSampleCounts"/>, same encoding.</summary>
    public int SupportedDepthSampleCounts { get; init; } = 1;

    /// <summary>True when the device can render to multisample color <em>and</em> depth targets.</summary>
    public bool SupportsMultisampleTargets => MaxSampleCount > 1;

    /// <summary>
    /// Highest sample count valid for a combined color+depth MSAA target. A render pass requires
    /// every attachment to share one sample count, so this is the intersection of the color and
    /// depth masks.
    /// </summary>
    public int MaxSampleCount
    {
        get
        {
            int mask = SupportedColorSampleCounts & SupportedDepthSampleCounts;
            for (int samples = MaxSupportedSampleCount; samples > 1; samples >>= 1)
            {
                if ((mask & samples) != 0)
                    return samples;
            }
            return 1;
        }
    }

    /// <summary>Largest sample count the engine will ever request (matches the MSAA 8× quality tier).</summary>
    internal const int MaxSupportedSampleCount = 8;

    /// <summary>True when <paramref name="samples"/> can back a color+depth MSAA target.</summary>
    public bool SupportsSampleCount(int samples)
    {
        if (samples <= 1)
            return true;
        if (samples > MaxSupportedSampleCount || (samples & (samples - 1)) != 0)
            return false;
        return ((SupportedColorSampleCounts & SupportedDepthSampleCounts) & samples) != 0;
    }

    /// <summary>
    /// Round <paramref name="samples"/> down to the nearest supported power-of-two sample count,
    /// returning 1 when the device has no MSAA support. Callers use this so an unsupported quality
    /// setting silently degrades to single-sample instead of failing target creation.
    /// </summary>
    public int ClampSampleCount(int samples)
    {
        if (samples <= 1)
            return 1;
        if (samples > MaxSupportedSampleCount)
            samples = MaxSupportedSampleCount;
        // Snap a non-power-of-two request down to the next power of two before probing the mask.
        while (samples > 1 && (samples & (samples - 1)) != 0)
            samples--;
        int mask = SupportedColorSampleCounts & SupportedDepthSampleCounts;
        for (; samples > 1; samples >>= 1)
        {
            if ((mask & samples) != 0)
                return samples;
        }
        return 1;
    }

    /// <summary>True when XEngine has wired asynchronous timestamp collection for this backend.</summary>
    public bool SupportsTimestampQueries { get; init; }

    /// <summary>Hardware queue capability discovered during device initialization.</summary>
    public bool HardwareSupportsTimestampQueries { get; init; }

    /// <summary>Stable device facts captured during backend initialization, when available.</summary>
    public GraphicsCapabilityBaseline? Baseline { get; init; }

    /// <summary>Human-readable backend name (e.g. "Null", "OpenGL 4.1", "Vulkan").</summary>
    public string BackendName { get; init; } = "Unknown";
}
