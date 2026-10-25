// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root.

using System;

using XEngine.Editor.Projects.Settings;

namespace XEngine.Editor.AssetBundles.PlatformVariants;

/// <summary>Android texture capabilities measured from the target Vulkan device.</summary>
public readonly struct AndroidTextureCapabilities
{
    public readonly bool SupportsEtc2Rgb;
    public readonly bool SupportsEtc2Rgba;
    public readonly bool SupportsAstc;

    public AndroidTextureCapabilities(bool supportsEtc2Rgb, bool supportsEtc2Rgba, bool supportsAstc)
    {
        SupportsEtc2Rgb = supportsEtc2Rgb;
        SupportsEtc2Rgba = supportsEtc2Rgba;
        SupportsAstc = supportsAstc;
    }

    public static AndroidTextureCapabilities Baseline => new(true, true, false);
}

/// <summary>Result of resolving one texture's Android platform override.</summary>
public readonly struct AndroidTextureVariantSelection
{
    public readonly TextureVariantFormat RequestedFormat;
    public readonly TextureVariantFormat EffectiveFormat;
    public readonly int MaxTextureSize;
    public readonly int CompressionQuality;
    public readonly bool IsFallback;
    public readonly string Reason;

    public AndroidTextureVariantSelection(
        TextureVariantFormat requestedFormat,
        TextureVariantFormat effectiveFormat,
        int maxTextureSize,
        int compressionQuality,
        bool isFallback,
        string reason)
    {
        RequestedFormat = requestedFormat;
        EffectiveFormat = effectiveFormat;
        MaxTextureSize = maxTextureSize;
        CompressionQuality = compressionQuality;
        IsFallback = isFallback;
        Reason = reason;
    }
}

/// <summary>
/// Resolves Android texture settings before an encoder is invoked. The resolver is deliberately
/// pure and deterministic: the same settings and measured capabilities always produce the same
/// selection. Encoding remains a separate step so unsupported formats can be rejected or reported
/// before a bundle is written.
/// </summary>
public static class AndroidTextureVariantResolver
{
    /// <summary>Returns whether the format requires an offline GPU texture encoder.</summary>
    public static bool IsCompressedFormat(TextureVariantFormat format)
        => format is TextureVariantFormat.BC1 or TextureVariantFormat.BC3 or TextureVariantFormat.BC5 or
            TextureVariantFormat.BC7 or TextureVariantFormat.ETC2_RGB or TextureVariantFormat.ETC2_RGBA or
            TextureVariantFormat.ASTC or TextureVariantFormat.ASTC_Mobile or TextureVariantFormat.PVRTC;

    public static AndroidTextureVariantSelection Resolve(
        TexturePlatformSettings? settings,
        AndroidTextureCapabilities capabilities)
    {
        TexturePlatformSettings effective = settings ?? new TexturePlatformSettings
        {
            Platform = BuildTarget.Android,
            Override = true,
            MaxTextureSize = 2048,
            Format = TextureVariantFormat.Automatic,
            CompressionQuality = 50,
        };

        Validate(effective);
        TextureVariantFormat requested = effective.Format;

        if (requested == TextureVariantFormat.Automatic)
        {
            // ETC2 is the Android baseline and is available on every Vulkan-capable Android
            // profile XEngine supports. Automatic intentionally does not select ASTC because ASTC
            // support is a device tier and must be opted into by a profile.
            if (capabilities.SupportsEtc2Rgba)
                return Create(effective, requested, TextureVariantFormat.ETC2_RGBA, false, "automatic:etc2-rgba-baseline");
            return Create(effective, requested, TextureVariantFormat.RGBA32, true, "automatic:fallback:rgba32");
        }

        if (IsSupported(requested, capabilities))
            return Create(effective, requested, requested, false, "requested:supported");

        if (capabilities.SupportsEtc2Rgba)
            return Create(effective, requested, TextureVariantFormat.ETC2_RGBA, true, "requested:unsupported:fallback:etc2-rgba");
        if (capabilities.SupportsEtc2Rgb)
            return Create(effective, requested, TextureVariantFormat.ETC2_RGB, true, "requested:unsupported:fallback:etc2-rgb");
        return Create(effective, requested, TextureVariantFormat.RGBA32, true, "requested:unsupported:fallback:rgba32");
    }

    /// <summary>
    /// Resolves a build-time variant while accounting for the availability of the offline encoder.
    /// Device capability selection and encoding availability are kept separate so a build never
    /// emits a compressed-format claim for which it has no payload.
    /// </summary>
    public static AndroidTextureVariantSelection ResolveForBuild(
        TexturePlatformSettings? settings,
        AndroidTextureCapabilities capabilities,
        bool encoderAvailable)
    {
        AndroidTextureVariantSelection selected = Resolve(settings, capabilities);
        if (encoderAvailable || !IsCompressedFormat(selected.EffectiveFormat))
            return selected;

        return new AndroidTextureVariantSelection(
            selected.RequestedFormat,
            TextureVariantFormat.RGBA32,
            selected.MaxTextureSize,
            selected.CompressionQuality,
            true,
            selected.Reason + ":encoder-unavailable:fallback:rgba32");
    }

    private static AndroidTextureVariantSelection Create(
        TexturePlatformSettings settings,
        TextureVariantFormat requested,
        TextureVariantFormat effective,
        bool fallback,
        string reason)
        => new(requested, effective, settings.MaxTextureSize, settings.CompressionQuality, fallback, reason);

    private static bool IsSupported(TextureVariantFormat format, AndroidTextureCapabilities capabilities)
        => format switch
        {
            TextureVariantFormat.RGBA32 => true,
            TextureVariantFormat.ETC2_RGB => capabilities.SupportsEtc2Rgb,
            TextureVariantFormat.ETC2_RGBA => capabilities.SupportsEtc2Rgba,
            TextureVariantFormat.ASTC or TextureVariantFormat.ASTC_Mobile => capabilities.SupportsAstc,
            _ => false,
        };

    private static void Validate(TexturePlatformSettings settings)
    {
        if (settings.MaxTextureSize < 0)
            throw new ArgumentOutOfRangeException(nameof(settings.MaxTextureSize), settings.MaxTextureSize, "MaxTextureSize must be non-negative.");
        if (settings.CompressionQuality is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(settings.CompressionQuality), settings.CompressionQuality, "CompressionQuality must be in [0, 100].");
        if (settings.Platform != BuildTarget.Android && settings.Platform != 0)
            throw new ArgumentException("Android texture settings must target BuildTarget.Android.", nameof(settings));
    }
}
