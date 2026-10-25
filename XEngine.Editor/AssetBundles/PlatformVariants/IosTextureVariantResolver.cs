// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root.

using System;

using XEngine.Editor.Projects.Settings;

namespace XEngine.Editor.AssetBundles.PlatformVariants;

/// <summary>iOS GPU texture capabilities. Apple GPUs since A8 expose ASTC LDR.</summary>
public readonly struct IosTextureCapabilities
{
    public readonly bool SupportsAstc;

    public IosTextureCapabilities(bool supportsAstc) => SupportsAstc = supportsAstc;

    /// <summary>iOS 16 / Apple GPU baseline: ASTC 4x4 is required, not optional.</summary>
    public static IosTextureCapabilities AppleGpu => new(true);
}

/// <summary>Result of resolving one texture's iOS platform override.</summary>
public readonly struct IosTextureVariantSelection
{
    public readonly TextureVariantFormat RequestedFormat;
    public readonly TextureVariantFormat EffectiveFormat;
    public readonly int MaxTextureSize;
    public readonly int CompressionQuality;
    public readonly bool IsFallback;
    public readonly string Reason;

    public IosTextureVariantSelection(
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
/// Resolves iOS texture settings. Automatic and ASTC both select ASTC 4x4. Missing
/// encoders and missing GPU ASTC support fail closed — they never rewrite the payload
/// as RGBA32.
/// </summary>
public static class IosTextureVariantResolver
{
    public const string AstcEncoderMissingError =
        "iOS ASTC 4x4 encoder is required; refusing RGBA fallback.";

    public const string DeviceAstcUnsupportedError =
        "iOS textures require ASTC 4x4 and the target GPU family does not support it. Refusing RGBA fallback.";

    public const string UnsupportedFormatError =
        "iOS texture pack only accepts Automatic, ASTC, ASTC_Mobile, or explicit RGBA32.";

    public static bool IsAstcFormat(TextureVariantFormat format)
        => format is TextureVariantFormat.ASTC or TextureVariantFormat.ASTC_Mobile or TextureVariantFormat.Automatic;

    public static IosTextureVariantSelection Resolve(
        TexturePlatformSettings? settings,
        IosTextureCapabilities capabilities)
    {
        TexturePlatformSettings effective = settings ?? new TexturePlatformSettings
        {
            Platform = BuildTarget.iOS,
            Override = true,
            MaxTextureSize = 2048,
            Format = TextureVariantFormat.Automatic,
            CompressionQuality = 50,
        };

        Validate(effective);
        TextureVariantFormat requested = effective.Format;

        if (requested == TextureVariantFormat.RGBA32)
            return Create(effective, requested, TextureVariantFormat.RGBA32, false, "requested:rgba32");

        if (requested is TextureVariantFormat.Automatic or TextureVariantFormat.ASTC or TextureVariantFormat.ASTC_Mobile)
        {
            if (!capabilities.SupportsAstc)
                throw new InvalidOperationException(DeviceAstcUnsupportedError);

            string reason = requested == TextureVariantFormat.Automatic
                ? "automatic:astc-4x4-baseline"
                : "requested:astc-4x4";
            return Create(effective, requested, TextureVariantFormat.ASTC, false, reason);
        }

        throw new InvalidOperationException(UnsupportedFormatError);
    }

    /// <summary>
    /// Resolves a build-time variant. Unlike Android, a missing encoder does not fall
    /// back to RGBA32 — the pack fails so Windows hosts cannot silently emit uncompressed
    /// payloads labeled as iOS ASTC.
    /// </summary>
    public static IosTextureVariantSelection ResolveForBuild(
        TexturePlatformSettings? settings,
        IosTextureCapabilities capabilities,
        bool encoderAvailable)
    {
        IosTextureVariantSelection selected = Resolve(settings, capabilities);
        if (selected.EffectiveFormat == TextureVariantFormat.ASTC && !encoderAvailable)
            throw new InvalidOperationException(AstcEncoderMissingError);
        return selected;
    }

    private static IosTextureVariantSelection Create(
        TexturePlatformSettings settings,
        TextureVariantFormat requested,
        TextureVariantFormat effective,
        bool fallback,
        string reason)
        => new(requested, effective, settings.MaxTextureSize, settings.CompressionQuality, fallback, reason);

    private static void Validate(TexturePlatformSettings settings)
    {
        if (settings.MaxTextureSize < 0)
            throw new ArgumentOutOfRangeException(nameof(settings.MaxTextureSize), settings.MaxTextureSize, "MaxTextureSize must be non-negative.");
        if (settings.CompressionQuality is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(settings.CompressionQuality), settings.CompressionQuality, "CompressionQuality must be in [0, 100].");
        if (settings.Platform != BuildTarget.iOS && settings.Platform != 0)
            throw new ArgumentException("iOS texture settings must target BuildTarget.iOS.", nameof(settings));
    }
}
