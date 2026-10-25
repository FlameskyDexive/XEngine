// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Keywords owned by optional Universal Features. Player-build collection drops variants that
/// enable these when the matching Feature is absent or disabled (M19).
/// </summary>
public static class ShaderStripKeywords
{
    public const string Decal = "DECAL";
    public const string ScreenSpaceShadows = "SCREEN_SPACE_SHADOWS";
}

/// <summary>Optional Feature shaders/keywords that player-build variant collection can strip.</summary>
[Flags]
public enum ShaderStripFeature : uint
{
    None = 0,
    ScreenSpaceDecal = 1 << 0,
    ScreenSpaceShadows = 1 << 1,
    Deferred = 1 << 2,
    Renderer2D = 1 << 3,
}

/// <summary>
/// Pre-filters shader variants from <see cref="UniversalRenderPipelineAsset"/> + enabled <see cref="RendererFeature"/>s.
/// Editor import still compiles every declared variant; player CollectForBuild drops Feature-owned
/// shaders, DeferredLighting unless Deferred or DeferredPlus, and keyword combinations
/// that cannot run. <see cref="UniversalRenderPipelineGlobalSettings"/> gates the whole pass
/// (StripUnusedVariants) and the 2D family (Strip2DUnusedVariants). Not a hot path (build /
/// collect time).
/// </summary>
public sealed class ShaderVariantStripper
{
    private readonly bool _strip;
    private readonly ShaderStripFeature _enabled;
    // Snapshot of UniversalRenderPipelineGlobalSettings at construction; a null settings asset
    // (legacy projects) keeps the pre-settings behavior exactly.
    private readonly bool _stripAll;
    private readonly bool _alwaysKeep2D;

    private ShaderVariantStripper(bool strip, ShaderStripFeature enabled)
    {
        _strip = strip;
        _enabled = enabled;
        UniversalRenderPipelineGlobalSettings? settings = UniversalRenderPipelineGlobalSettings.Active;
        _stripAll = strip && (settings?.StripUnusedVariants ?? true);
        // No settings asset → old feature-mask behavior; with one → Unity 6.7 default keeps the
        // 2D family unless Strip2DUnusedVariants is explicitly on.
        _alwaysKeep2D = settings != null && !settings.Strip2DUnusedVariants;
    }

    /// <summary>Keep every shader and keyword (editor import, explicit Collect tests).</summary>
    public static ShaderVariantStripper Unfiltered { get; } = new(strip: false, ShaderStripFeature.None);

    public ShaderStripFeature EnabledFeatures => _enabled;

    public static ShaderVariantStripper FromFeatures(IReadOnlyList<RendererFeature>? features)
    {
        ShaderStripFeature enabled = ShaderStripFeature.None;
        if (features == null)
            return new ShaderVariantStripper(strip: true, enabled);

        for (int i = 0; i < features.Count; i++)
        {
            RendererFeature feature = features[i];
            if (feature == null || !feature.Enabled)
                continue;
            if (feature is ScreenSpaceDecalRendererFeature)
                enabled |= ShaderStripFeature.ScreenSpaceDecal;
            else if (feature is ScreenSpaceShadowsRendererFeature)
                enabled |= ShaderStripFeature.ScreenSpaceShadows;
        }

        return new ShaderVariantStripper(strip: true, enabled);
    }

    internal static ShaderVariantStripper FromPipeline(UniversalRenderPipeline? pipeline)
    {
        ShaderVariantStripper features = FromFeatures(pipeline?.RendererFeatures);
        ShaderStripFeature enabled = features.EnabledFeatures;
        if (pipeline != null && pipeline.RendererKind == RendererKind.Renderer2D)
            enabled |= ShaderStripFeature.Renderer2D;
        else if (pipeline != null && pipeline.UsesDeferredShading)
            enabled |= ShaderStripFeature.Deferred;
        return new ShaderVariantStripper(strip: true, enabled);
    }

    public bool KeepDefaultShader(DefaultShader shader)
        => KeepShaderAsset(BuiltInAssets.GuidFor(shader));

    public bool KeepShaderAsset(Guid shaderAssetGuid)
    {
        if (!_stripAll)
            return true;
        if (shaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.ScreenSpaceDecal))
            return (_enabled & ShaderStripFeature.ScreenSpaceDecal) != 0;
        if (shaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.ScreenSpaceShadows))
            return (_enabled & ShaderStripFeature.ScreenSpaceShadows) != 0;
        if (shaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.DeferredLighting))
            return (_enabled & ShaderStripFeature.Deferred) != 0;
        if (shaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.SpriteLit)
            || shaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.Light2D)
            || shaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.Shadow2D))
            return _alwaysKeep2D || (_enabled & ShaderStripFeature.Renderer2D) != 0;
        return true;
    }

    public bool KeepShader(Shader? shader)
    {
        if (!_stripAll || shader == null)
            return true;
        string name = shader.ArtifactName;
        if (IsSuffix(name, "ScreenSpaceDecal"))
            return (_enabled & ShaderStripFeature.ScreenSpaceDecal) != 0;
        if (IsSuffix(name, "ScreenSpaceShadows"))
            return (_enabled & ShaderStripFeature.ScreenSpaceShadows) != 0;
        if (IsSuffix(name, "DeferredLighting"))
            return (_enabled & ShaderStripFeature.Deferred) != 0;
        if (IsSuffix(name, "SpriteLit") || IsSuffix(name, "Light2D") || IsSuffix(name, "Shadow2D"))
            return _alwaysKeep2D || (_enabled & ShaderStripFeature.Renderer2D) != 0;
        return true;
    }

    public bool KeepKeyword(string? keyword)
    {
        if (!_stripAll || string.IsNullOrEmpty(keyword))
            return true;
        return KeepKeyword(keyword.AsSpan());
    }

    public bool KeepCanonicalKeywordSet(string? canonicalKeywordSet)
    {
        if (!_stripAll || string.IsNullOrEmpty(canonicalKeywordSet))
            return true;

        ReadOnlySpan<char> span = canonicalKeywordSet.AsSpan();
        int start = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != ';')
                continue;
            if (i > start && !KeepKeyword(span.Slice(start, i - start)))
                return false;
            start = i + 1;
        }

        return start >= span.Length || KeepKeyword(span.Slice(start));
    }

    private bool KeepKeyword(ReadOnlySpan<char> keyword)
    {
        if (keyword.Equals(ShaderStripKeywords.Decal.AsSpan(), StringComparison.Ordinal))
            return (_enabled & ShaderStripFeature.ScreenSpaceDecal) != 0;
        if (keyword.Equals(ShaderStripKeywords.ScreenSpaceShadows.AsSpan(), StringComparison.Ordinal))
            return (_enabled & ShaderStripFeature.ScreenSpaceShadows) != 0;
        return true;
    }

    private static bool IsSuffix(string name, string suffix)
    {
        if (name.Length < suffix.Length)
            return false;
        if (name.Length == suffix.Length)
            return string.Equals(name, suffix, StringComparison.Ordinal);
        int slash = name.Length - suffix.Length - 1;
        return name[slash] == '/'
            && name.AsSpan(slash + 1).Equals(suffix.AsSpan(), StringComparison.Ordinal);
    }
}
