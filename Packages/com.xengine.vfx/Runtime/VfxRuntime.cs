namespace XEngine.VFX;

/// <summary>Marker used by package/build audits to prove VFX Runtime is present.</summary>
public static class VfxRuntime
{
    public const string PackageName = "com.xengine.vfx";
    public const string Version = "0.1.0";
    public const int ThreadGroupSize = 64;

    /// <summary>True when the active graphics device can run VFX compute (Vulkan / D3D12 / Metal).</summary>
    public static bool DeviceSupportsCompute =>
        XEngine.Runtime.Graphics.Device is { IsInitialized: true, Capabilities.SupportsCompute: true };
}
