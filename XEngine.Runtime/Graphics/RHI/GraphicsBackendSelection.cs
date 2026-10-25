// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.RHI;

/// <summary>
/// Resolves the preferred <see cref="GraphicsBackend"/> from CLI args, environment,
/// and platform defaults. Used by hosts before window/device creation.
/// </summary>
public static class GraphicsBackendSelection
{
    /// <summary>Override set by hosts (CLI / preferences). Null means unresolved.</summary>
    public static GraphicsBackend? Preferred { get; set; }

    /// <summary>Resolve Auto consistently before creating a window and its graphics device.</summary>
    public static GraphicsBackend ResolveWindowedAuto(GraphicsBackend backend)
    {
        if (backend != GraphicsBackend.Auto)
            return backend;
#if XENGINE_IOS
        return GraphicsBackend.Metal;
#elif XENGINE_WEB
        return GraphicsBackend.Auto; // Factory probes WebGPU, then WebGL2.
#else
        return OperatingSystem.IsMacOS() ? GraphicsBackend.Metal : GraphicsBackend.Vulkan;
#endif
    }

    /// <summary>
    /// Parse common CLI forms:
    /// <c>--graphics=vulkan|d3d12|opengl|webgpu|webgl2|metal|null|auto</c>,
    /// <c>--graphics vulkan</c>, <c>-g vulkan</c>.
    /// Also honors <c>XENGINE_GRAPHICS_BACKEND</c>.
    /// </summary>
    public static GraphicsBackend Parse(string[]? args)
    {
        if (Preferred.HasValue)
            return Preferred.Value;

        string? fromEnv = Environment.GetEnvironmentVariable("XENGINE_GRAPHICS_BACKEND");
        if (TryParseName(fromEnv, out GraphicsBackend envBackend))
            return envBackend;

        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--graphics=", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseName(a.AsSpan("--graphics=".Length).ToString(), out GraphicsBackend b))
                        return b;
                }
                else if (string.Equals(a, "--graphics", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(a, "-g", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && TryParseName(args[i + 1], out GraphicsBackend b))
                        return b;
                }
            }
        }

        return GraphicsBackend.Auto;
    }

    public static bool TryParseName(string? name, out GraphicsBackend backend)
    {
        backend = GraphicsBackend.Auto;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        switch (name.Trim().ToLowerInvariant())
        {
            case "auto":
                backend = GraphicsBackend.Auto;
                return true;
            case "null":
            case "headless":
            case "none":
                backend = GraphicsBackend.Null;
                return true;
            case "opengl":
            case "gl":
            case "ogl":
                backend = GraphicsBackend.OpenGL;
                return true;
            case "vulkan":
            case "vk":
                backend = GraphicsBackend.Vulkan;
                return true;
            case "d3d12":
            case "dx12":
            case "direct3d12":
            case "directx12":
                backend = GraphicsBackend.Direct3D12;
                return true;
            case "webgpu":
            case "wgpu":
                backend = GraphicsBackend.WebGPU;
                return true;
            case "metal":
            case "mtl":
                backend = GraphicsBackend.Metal;
                return true;
            case "webgl2":
            case "webgl":
            case "gles3":
            case "gles":
                backend = GraphicsBackend.WebGL2;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Human-readable name for diagnostics / editor footer.</summary>
    public static string GetDisplayName(IGraphicsDevice? device)
    {
        if (device == null)
            return "None (headless)";
        return string.IsNullOrEmpty(device.Capabilities.BackendName)
            ? device.Backend.ToString()
            : device.Capabilities.BackendName;
    }
}
