// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.RHI;

/// <summary>Creation parameters for <see cref="GraphicsDeviceFactory.Create"/>.</summary>
public sealed class GraphicsDeviceOptions
{
    public GraphicsBackend Backend { get; init; } = GraphicsBackend.Auto;

    /// <summary>Enable backend debug layers / extra validation messaging when available.</summary>
    public bool Debug { get; init; }

    /// <summary>Preferred physical adapter index, or -1 for the default adapter.</summary>
    public int PreferredAdapterIndex { get; init; } = -1;

    /// <summary>Enable API validation (Vulkan validation layers, D3D12 debug layer, GL debug output).</summary>
    public bool EnableValidation { get; init; }

    /// <summary>Request presentation vsync when a surface is attached.</summary>
    public bool VSync { get; init; } = true;
}
