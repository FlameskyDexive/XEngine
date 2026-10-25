// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Backends.Metal;

/// <summary>
/// iOS ASTC gate. Unsupported devices hard-fail; they must not decompress to RGBA.
/// </summary>
internal static class MetalAstcSupport
{
    internal const string UnsupportedMessage =
        "ASTC 4x4 is required on iOS and this MTLDevice does not advertise MTLGPUFamilyApple2. Refusing RGBA fallback.";

    internal static void EnsureTextureFormatSupported(TextureImageFormat format, bool deviceSupportsAstc)
    {
        if (format is TextureImageFormat.Astc4x4 or TextureImageFormat.Astc4x4Srgb && !deviceSupportsAstc)
            throw new InvalidOperationException(UnsupportedMessage);
    }
}
