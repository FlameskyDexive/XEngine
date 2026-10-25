// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Platform a bundle/manifest was built for. Stored as a single byte in the bundle header and the
/// manifest so the loader can reject a mismatched-platform bundle (e.g. loading an iOS-compressed
/// bundle on desktop). Mirrors the values of XEngine.Editor.Build.BuildTarget so editor builds
/// and runtime loads agree on the encoding.
/// </summary>
/// <remarks>
/// Values are stable: do not renumber. <see cref="Any"/> is used for platform-agnostic bundles
/// (e.g. a shared dependency bundle whose content does not vary by platform - which is currently
/// ALL bundles until per-platform texture compression / shader bytecode lands, see the AssetBundle
/// platform-variant plan).
/// </remarks>
public enum AssetBundlePlatform : byte
{
    /// <summary>Platform-agnostic. Loads on any target. The default until per-platform variants exist.</summary>
    Any = 0,

    /// <summary>Windows desktop (D3D12/Vulkan/OpenGL, BC textures).</summary>
    Windows = 1,

    /// <summary>Linux desktop (Vulkan/OpenGL, BC textures).</summary>
    Linux = 2,

    /// <summary>macOS desktop (Metal via MoltenVK, BC/ASTC textures).</summary>
    MacOS = 3,

    /// <summary>Android (Vulkan/GLES, ETC2/ASTC textures).</summary>
    Android = 4,

    /// <summary>iOS (Metal, ASTC/PVRTC textures). Reserved: no build pipeline ships this target yet.</summary>
    iOS = 5,

    /// <summary>HarmonyOS/OpenHarmony mobile target (Vulkan, ASTC textures).</summary>
    HarmonyOS = 6,

    /// <summary>Web / mini-game (WebGL2 or WebGPU, ASTC/basis textures).</summary>
    Web = 7,
}
