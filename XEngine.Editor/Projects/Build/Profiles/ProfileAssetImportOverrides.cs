// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.AssetBundles.PlatformVariants;

namespace XEngine.Editor.Build.Profiles;

/// <summary>
/// Build-time asset downgrades that trade artifact quality for build speed and package size,
/// mirroring Unity's "Asset Import Overrides" section of a build profile.
/// </summary>
/// <remarks>
/// These are applied while packaging the payload, never by rewriting <c>.meta</c> files. Writing
/// import settings would dirty every texture and force a full project reimport when a profile is
/// selected, which defeats the purpose of a fast iteration build.
/// </remarks>
public sealed class ProfileAssetImportOverrides
{
    public bool Enabled;

    /// <summary>
    /// Clamp for the longest edge of a texture, in pixels. Zero leaves the imported size alone.
    /// Values are rounded down to a power of two by <see cref="ResolveMaxTextureSize"/>.
    /// </summary>
    public int MaxTextureSize;

    /// <summary>Skip platform texture compression and ship uncompressed payloads.</summary>
    public bool ForceUncompressed;
    /// <summary>Pack bounded normal/tangent/color GPU vertex streams; preserves CPU data and skinning precision.</summary>
    public bool CompressMeshVertexData;

    /// <summary>
    /// Effective clamp, or 0 when no clamping applies. Non-power-of-two requests round down to the
    /// nearest power of two so the value always matches what the encoders can emit.
    /// </summary>
    public int ResolveMaxTextureSize()
    {
        if (!Enabled || MaxTextureSize <= 0) return 0;
        if (MaxTextureSize < 32) return 32;

        int pow = 32;
        while (pow * 2 <= MaxTextureSize && pow < 16384)
            pow *= 2;
        return pow;
    }

    public bool ResolveForceUncompressed() => Enabled && ForceUncompressed;

    public bool HasAnyEffect() => ResolveMaxTextureSize() > 0 || ResolveForceUncompressed();

    /// <summary>
    /// A copy of <paramref name="platform"/> with this profile's downgrades folded in. The clamp can
    /// only ever lower the platform's own limit - a profile is a way to build something smaller and
    /// faster, so it must not quietly upgrade a texture budget the project deliberately set.
    /// Returns the input unchanged when nothing applies.
    /// </summary>
    public TexturePlatformSettings ApplyTo(TexturePlatformSettings platform)
    {
        if (platform == null || !HasAnyEffect()) return platform!;

        var result = new TexturePlatformSettings
        {
            Platform = platform.Platform,
            Override = platform.Override,
            MaxTextureSize = platform.MaxTextureSize,
            Format = platform.Format,
            CompressionQuality = platform.CompressionQuality,
            ResizeAlgorithm = platform.ResizeAlgorithm,
        };

        int clamp = ResolveMaxTextureSize();
        if (clamp > 0)
            result.MaxTextureSize = platform.MaxTextureSize <= 0 ? clamp : Math.Min(platform.MaxTextureSize, clamp);

        if (ResolveForceUncompressed())
            result.Format = TextureVariantFormat.RGBA32;

        return result;
    }
}
