// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

#if !XENGINE_ANDROID && !XENGINE_HARMONYOS && !XENGINE_WEB
using XEngine.Runtime.Backends.Metal;
#endif
#if XENGINE_WEB
using XEngine.Runtime.Backends.WebGL2;
using XEngine.Runtime.Backends.WebGPU;
#elif !XENGINE_MOBILE
using XEngine.Runtime.Backends.D3D12;
using XEngine.Runtime.Backends.OpenGL;
using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.Backends.WebGL2;
#endif
#if !XENGINE_IOS && !XENGINE_WEB
using XEngine.Runtime.Backends.Vulkan;
#endif

namespace XEngine.Runtime.RHI;

/// <summary>Creates an <see cref="IGraphicsDevice"/> for the requested backend.</summary>
public static class GraphicsDeviceFactory
{
    /// <summary>
    /// Create a device for <paramref name="options"/>.
    /// <see cref="GraphicsBackend.Auto"/> prefers the host's native backend.
    /// On iOS, Auto is Metal. On web, Auto is WebGPU then WebGL2.
    /// </summary>
    public static IGraphicsDevice Create(GraphicsDeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Backend == GraphicsBackend.Auto)
        {
            foreach (GraphicsBackend candidate in AutoCandidates())
            {
                try
                {
                    return CreateExplicit(candidate, WithBackend(options, candidate));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Backend {candidate} unavailable: {ex.Message}");
                }
            }

#if XENGINE_IOS
            throw new PlatformNotSupportedException("No Metal graphics device is available on this iOS target.");
#elif XENGINE_WEB
            throw new PlatformNotSupportedException("No WebGPU or WebGL2 graphics device is available on this web target.");
#elif XENGINE_MOBILE
            throw new PlatformNotSupportedException("No Vulkan graphics device is available on this mobile target.");
#else
            return new OpenGLGraphicsDevice(WithBackend(options, GraphicsBackend.OpenGL));
#endif
        }

        return CreateExplicit(options.Backend, options);
    }

    internal static GraphicsBackend[] GetAutoCandidatesForTests() => AutoCandidates();

    /// <summary>
    /// Web Auto prefers WebGPU (emdawnwebgpu) and falls back to WebGL2. Desktop Auto never
    /// includes either. Douyin publishes force WebGL2 and do not link emdawnwebgpu.
    /// </summary>
    internal static GraphicsBackend[] GetWebAutoCandidates() => [GraphicsBackend.WebGPU, GraphicsBackend.WebGL2];

    private static IGraphicsDevice CreateExplicit(GraphicsBackend backend, GraphicsDeviceOptions options) =>
        backend switch
        {
            GraphicsBackend.Null => new NullGraphicsDevice(),
#if XENGINE_IOS
            GraphicsBackend.Metal => new MetalGraphicsDevice(options),
            GraphicsBackend.Vulkan or GraphicsBackend.WebGPU or GraphicsBackend.WebGL2 or GraphicsBackend.OpenGL
                or GraphicsBackend.Direct3D12 =>
                throw new PlatformNotSupportedException(
                    $"Graphics backend {backend} is not supported on iOS. Use Metal."),
#elif XENGINE_WEB
            GraphicsBackend.WebGL2 => new WebGL2GraphicsDevice(options),
            GraphicsBackend.WebGPU => new WebGPUGraphicsDevice(options),
            GraphicsBackend.Vulkan or GraphicsBackend.Metal or GraphicsBackend.OpenGL
                or GraphicsBackend.Direct3D12 =>
                throw new PlatformNotSupportedException(
                    $"Graphics backend {backend} is not supported on the web player. Use WebGPU or WebGL2."),
#elif XENGINE_MOBILE
            GraphicsBackend.Vulkan => new VulkanGraphicsDevice(options),
            GraphicsBackend.Metal or GraphicsBackend.WebGPU or GraphicsBackend.WebGL2 or GraphicsBackend.OpenGL
                or GraphicsBackend.Direct3D12 =>
                throw new PlatformNotSupportedException(
                    $"Graphics backend {backend} is not supported on this mobile target."),
#else
            GraphicsBackend.Vulkan => new VulkanGraphicsDevice(options),
            GraphicsBackend.Metal => new MetalGraphicsDevice(options),
            GraphicsBackend.WebGPU => new WebGPUGraphicsDevice(options),
            GraphicsBackend.WebGL2 => new WebGL2GraphicsDevice(options),
            GraphicsBackend.OpenGL => new OpenGLGraphicsDevice(options),
            GraphicsBackend.Direct3D12 => new D3D12GraphicsDevice(options),
#endif
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown graphics backend."),
        };

    private static GraphicsBackend[] AutoCandidates()
    {
#if XENGINE_IOS
        return [GraphicsBackend.Metal];
#elif XENGINE_WEB
        return GetWebAutoCandidates();
#elif XENGINE_MOBILE
        return [GraphicsBackend.Vulkan];
#else
        if (OperatingSystem.IsWindows())
            return [GraphicsBackend.Direct3D12, GraphicsBackend.Vulkan, GraphicsBackend.OpenGL];
        if (OperatingSystem.IsMacOS())
            return [GraphicsBackend.Metal, GraphicsBackend.Vulkan, GraphicsBackend.OpenGL];
        return [GraphicsBackend.Vulkan, GraphicsBackend.OpenGL];
#endif
    }

    private static GraphicsDeviceOptions WithBackend(GraphicsDeviceOptions options, GraphicsBackend backend) =>
        new()
        {
            Backend = backend,
            Debug = options.Debug,
            PreferredAdapterIndex = options.PreferredAdapterIndex,
            EnableValidation = options.EnableValidation,
            VSync = options.VSync,
        };
}
