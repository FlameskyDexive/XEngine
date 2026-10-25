// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Runtime.InteropServices;

using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;

namespace XEngine.Runtime.Backends.Vulkan;

/// <summary>
/// macOS/Linux loader discovery for the Vulkan loader library. Homebrew installs the Vulkan
/// loader under /opt/homebrew/lib (Intel Macs: /usr/local/lib), which is NOT part of dyld's
/// default search path for bare library names — Silk.NET's Vk.GetApi() then fails with
/// FileNotFoundException even though the loader exists (assembly-level DllImport resolvers do
/// not intercept Silk's internal TryLoad-by-name). When such a loader is found we hand
/// VulkanGraphicsDevice an explicit native context bound to the absolute path instead.
/// </summary>
internal static class VulkanLoader
{
    /// <summary>
    /// Returns a native context bound to a Vulkan loader discovered outside the default search
    /// paths, or null when the default <see cref="Vk.GetApi"/> resolution should be used.
    /// </summary>
    internal static INativeContext? TryCreateExplicitContext()
    {
        string? loaderPath = FindLoader();
        return loaderPath is not null && NativeLibrary.TryLoad(loaderPath, out nint handle)
            ? new LoadedLibraryContext(loaderPath, handle)
            : null;
    }

    private static string? FindLoader()
    {
        string? overridePath = Environment.GetEnvironmentVariable("XENGINE_VULKAN_LIB");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        if (OperatingSystem.IsMacOS())
        {
            string[] roots = ["/opt/homebrew/lib", "/usr/local/lib"];
            string[] names = ["libvulkan.1.dylib", "libvulkan.dylib"];
            foreach (string root in roots)
            {
                foreach (string name in names)
                {
                    string candidate = Path.Combine(root, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        // Linux distributions place the loader on the default library path; nothing to plug in.
        return null;
    }

    /// <summary>A native context resolving Vulkan entry points from one preloaded module.</summary>
    private sealed class LoadedLibraryContext : INativeContext
    {
        private readonly string _path;
        private readonly nint _handle;

        public LoadedLibraryContext(string path, nint handle)
        {
            _path = path;
            _handle = handle;
        }

        public nint GetProcAddress(string procName, int? symbolIndex) => NativeLibrary.GetExport(_handle, procName);

        public bool TryGetProcAddress(string procName, out nint address, int? symbolIndex)
        {
            address = nint.Zero;
            try
            {
                address = NativeLibrary.GetExport(_handle, procName);
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        // The loader module intentionally stays loaded for the process lifetime; Vk owns the
        // context and must not unload the library out from under live Vulkan objects.
        public void Dispose()
        {
        }

        public override string ToString() => $"VulkanLoader({_path})";
    }
}
