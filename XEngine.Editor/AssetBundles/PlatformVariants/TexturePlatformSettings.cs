// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// Platform-variant interfaces for the AssetBundle build pipeline (Stage F). These types lay the
// groundwork for per-platform texture compression (BC for PC, ETC2/ASTC for mobile) and offline
// shader bytecode (SPIR-V / DXIL / Metal). The interfaces + storage are in place now so a later
// milestone can plug in real compressors without changing the build pipeline's structure.
//
// Per the AssetBundle plan, bundle CONTENT for each platform is currently byte-identical (textures
// are always RGBA32, shaders ship source). What IS written per-platform today is the bundle's
// header Platform field and the manifest's BuildTarget, so the loader can reject mismatches.

using System;
using System.Collections.Generic;

using XEngine.Editor.Projects.Settings;

namespace XEngine.Editor.AssetBundles.PlatformVariants;

/// <summary>Per-platform texture override. Mirrors Unity's TextureImporterPlatformSettings shape:
/// max size, format, compression quality, and a resize algorithm. None of the GPU-compression
/// formats are wired into the importer yet (textures stay RGBA32); the model is here so build code
/// can be platform-aware now.</summary>
[Serializable]
public sealed class TexturePlatformSettings
{
    /// <summary>The platform these settings apply to (Windows/Linux/MacOS/Android/iOS). Empty = default.</summary>
    public BuildTarget Platform;

    /// <summary>Whether this override is active. Inactive overrides fall back to the default settings.</summary>
    public bool Override = false;

    /// <summary>Maximum texture dimension (longer side) after resize. 0 = no limit.</summary>
    public int MaxTextureSize = 2048;

    /// <summary>Target format enum value (see <see cref="TextureVariantFormat"/>).</summary>
    public TextureVariantFormat Format = TextureVariantFormat.Automatic;

    /// <summary>Compression quality 0-100 (or a fixed-level enum mapped by the compressor). Reserved
    /// until a real encoder lands.</summary>
    public int CompressionQuality = 50;

    /// <summary>How to resize when the source exceeds <see cref="MaxTextureSize"/>.</summary>
    public TextureResizeAlgorithm ResizeAlgorithm = TextureResizeAlgorithm.Mitchell;
}

/// <summary>Texture formats the variant system understands. Most map to GPU block-compression that
/// the engine does NOT yet encode; <see cref="Automatic"/> leaves the choice to a future
/// platform-aware default and currently resolves to uncompressed RGBA32.</summary>
public enum TextureVariantFormat
{
    /// <summary>Pick a sensible default per platform (currently uncompressed RGBA32 everywhere).</summary>
    Automatic = 0,
    /// <summary>Uncompressed RGBA8 (the engine's current on-disk format).</summary>
    RGBA32 = 1,
    // PC block compression (Windows/Linux/macOS):
    BC1 = 10,  // RGB, 0.5 bpp - diffuse/color albedo
    BC3 = 11,  // RGBA, 1 bpp - color + 1-bit alpha
    BC5 = 12,  // RG, 1 bpp - normal maps
    BC7 = 13,  // RGBA, 1 bpp - high quality color+alpha
    // Android block compression:
    ETC2_RGB = 20,
    ETC2_RGBA = 21,
    ASTC = 22, // 4x4..12x12 block sizes
    // iOS / macOS:
    ASTC_Mobile = 30,
    PVRTC = 31,
}

/// <summary>Resize filter applied when a texture exceeds the platform's MaxTextureSize.</summary>
public enum TextureResizeAlgorithm
{
    Mitchell = 0,
    Bilinear = 1,
    Nearest = 2,
}

/// <summary>A texture's full platform-override set: one entry per platform that has an override,
/// plus helpers to resolve the effective settings for a target.</summary>
[Serializable]
public sealed class TexturePlatformOverrides
{
    /// <summary>Per-platform overrides. Missing platform = use the texture's default settings.</summary>
    public Dictionary<BuildTarget, TexturePlatformSettings> Overrides = new();

    /// <summary>Resolve the effective settings for <paramref name="target"/>. Returns the override
    /// if it exists and is active, otherwise null (caller uses the default).</summary>
    public TexturePlatformSettings? Resolve(BuildTarget target)
        => Overrides.TryGetValue(target, out var s) && s.Override ? s : null;
}

/// <summary>
/// Maps a build target to the <see cref="XEngine.Runtime.AssetBundles.AssetBundlePlatform"/> recorded
/// in bundle headers and manifests. Used by the AssetBundleBuilder and the build pipeline so the
/// header Platform field matches what the loader expects.
/// </summary>
public static class PlatformVariantMapping
{
    public static XEngine.Runtime.AssetBundles.AssetBundlePlatform ToAssetBundlePlatform(BuildTarget target) => target switch
    {
        BuildTarget.Windows => XEngine.Runtime.AssetBundles.AssetBundlePlatform.Windows,
        BuildTarget.Linux => XEngine.Runtime.AssetBundles.AssetBundlePlatform.Linux,
        BuildTarget.MacOS => XEngine.Runtime.AssetBundles.AssetBundlePlatform.MacOS,
        BuildTarget.Android => XEngine.Runtime.AssetBundles.AssetBundlePlatform.Android,
        BuildTarget.iOS => XEngine.Runtime.AssetBundles.AssetBundlePlatform.iOS,
        BuildTarget.HarmonyOS => XEngine.Runtime.AssetBundles.AssetBundlePlatform.HarmonyOS,
        BuildTarget.Web => XEngine.Runtime.AssetBundles.AssetBundlePlatform.Web,
        _ => XEngine.Runtime.AssetBundles.AssetBundlePlatform.Any,
    };
}
