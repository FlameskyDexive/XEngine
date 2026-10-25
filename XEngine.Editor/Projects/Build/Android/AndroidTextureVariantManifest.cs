// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

using XEngine.Echo;
using XEngine.Editor.AssetBundles.PlatformVariants;
using XEngine.Editor.Projects;

namespace XEngine.Editor.Build.Android;

/// <summary>
/// Writes the build-time texture variant contract consumed by the Android player. The manifest is
/// generated from the exact payload plan used by LooseFiles, XEnginePak, and AssetBundle writers,
/// so the runtime hash check observes the bytes that are actually packaged.
/// </summary>
internal static class AndroidTextureVariantManifest
{
    internal const string FileName = "android-texture-variants.json";
    internal const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static int Write(
        string path,
        Project project,
        IReadOnlyCollection<Guid> assets,
        TexturePlatformSettings settings,
        AndroidTextureCapabilities capabilities,
        AndroidTextureVariantBuildPlan? suppliedPlan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(settings);

        AndroidTextureVariantBuildPlan plan = suppliedPlan ??
            AndroidTexturePayloadEncoder.CreatePlan(project, assets, settings, capabilities);
        var entries = new List<TextureEntry>(plan.Entries.Count);
        for (int i = 0; i < plan.Entries.Count; i++)
        {
            AndroidTextureVariantBuildEntry entry = plan.Entries[i];
            entries.Add(new TextureEntry(
                entry.Guid,
                entry.AssetPath,
                entry.SourcePayloadBytes,
                entry.SourcePayloadSha256,
                entry.PackagedPayload.LongLength,
                entry.PackagedPayloadSha256,
                entry.RequestedFormat,
                entry.EffectiveFormat,
                entry.IsFallback,
                entry.SelectionReason,
                entry.MaxTextureSize,
                entry.CompressionQuality,
                entry.GenerateMipmaps,
                entry.SRgb,
                entry.NormalMap,
                entry.Width,
                entry.Height,
                entry.MipCount,
                entry.EncodedPayload,
                entry.PayloadFormat,
                entry.PayloadPath));
        }

        entries.Sort(static (left, right) => CompareGuid(left.Guid, right.Guid));
        var document = new
        {
            schemaVersion = SchemaVersion,
            platform = "Android",
            contract = "packaged-payload-sha256-v2",
            encoders = new
            {
                astc = plan.AstcEncoder == null
                    ? new { id = "arm.astcenc", status = "unavailable", version = "", sha256 = "" }
                    : new { id = plan.AstcEncoder.Id, status = "ready", version = plan.AstcEncoder.Version, sha256 = plan.AstcEncoder.Sha256 },
                etc2 = plan.Etc2Encoder == null
                    ? new { id = "google.etc2comp", status = "unavailable", version = "", sha256 = "" }
                    : new { id = plan.Etc2Encoder.Id, status = "ready", version = plan.Etc2Encoder.Version, sha256 = plan.Etc2Encoder.Sha256 },
            },
            capabilities = new
            {
                etc2Rgb = capabilities.SupportsEtc2Rgb,
                etc2Rgba = capabilities.SupportsEtc2Rgba,
                astc = capabilities.SupportsAstc,
            },
            requested = new
            {
                format = settings.Format.ToString(),
                maxTextureSize = settings.MaxTextureSize,
                compressionQuality = settings.CompressionQuality,
            },
            textureCount = entries.Count,
            textures = CreateTextureDocuments(entries),
        };

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(document, s_jsonOptions);
        File.WriteAllText(path, json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n");
        return entries.Count;
    }

    private static bool IsTexture(AssetEntry asset)
        => string.Equals(asset.ImporterType, "TextureImporter", StringComparison.OrdinalIgnoreCase) ||
            (asset.MainAssetTypeName?.Contains("Texture2D", StringComparison.OrdinalIgnoreCase) ?? false);

    private static ImportMetadata ReadImportMetadata(Project project, AssetEntry asset)
    {
        try
        {
            string source = Path.Combine(project.AssetsPath, asset.Path);
            string metaPath = MetaFile.GetMetaPath(source);
            if (!File.Exists(metaPath))
                return new ImportMetadata(true, true, false);
            EchoObject? settings = MetaFile.Read(metaPath).Settings;
            return new ImportMetadata(
                ReadBool(settings, "generateMipmaps", true),
                ReadBool(settings, "sRGB", true),
                ReadBool(settings, "normalMap", false));
        }
        catch
        {
            // Import settings are advisory metadata. A malformed/legacy .meta must not make the
            // payload contract disappear; the importer defaults remain the conservative fallback.
            return new ImportMetadata(true, true, false);
        }
    }

    private static bool ReadBool(EchoObject? settings, string key, bool fallback)
        => settings != null && settings.TryGet(key, out EchoObject value) ? value.BoolValue : fallback;

    private static int CompareGuid(Guid left, Guid right)
        => StringComparer.Ordinal.Compare(left.ToString("N"), right.ToString("N"));

    private static object[] CreateTextureDocuments(List<TextureEntry> entries)
    {
        var documents = new object[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            TextureEntry entry = entries[i];
            documents[i] = new
            {
                guid = entry.Guid.ToString("N"),
                assetPath = entry.AssetPath,
                sourcePayloadBytes = entry.SourcePayloadBytes,
                sourcePayloadSha256 = entry.SourcePayloadSha256,
                packagedPayloadBytes = entry.PackagedPayloadBytes,
                packagedPayloadSha256 = entry.PackagedPayloadSha256,
                requestedFormat = entry.RequestedFormat.ToString(),
                effectiveFormat = entry.EffectiveFormat.ToString(),
                isFallback = entry.IsFallback,
                selectionReason = entry.SelectionReason,
                maxTextureSize = entry.MaxTextureSize,
                compressionQuality = entry.CompressionQuality,
                generateMipmaps = entry.GenerateMipmaps,
                sRgb = entry.SRgb,
                normalMap = entry.NormalMap,
                alphaMode = entry.AlphaMode,
                width = entry.Width,
                height = entry.Height,
                mipCount = entry.MipCount,
                payloadPath = entry.PayloadPath,
                encodedPayload = entry.EncodedPayload,
            };
        }
        return documents;
    }

    private readonly record struct ImportMetadata(bool GenerateMipmaps, bool SRgb, bool NormalMap);

    private sealed record TextureEntry(
        Guid Guid,
        string AssetPath,
        long SourcePayloadBytes,
        string SourcePayloadSha256,
        long PackagedPayloadBytes,
        string PackagedPayloadSha256,
        TextureVariantFormat RequestedFormat,
        TextureVariantFormat EffectiveFormat,
        bool IsFallback,
        string SelectionReason,
        int MaxTextureSize,
        int CompressionQuality,
        bool GenerateMipmaps,
        bool SRgb,
        bool NormalMap,
        int Width,
        int Height,
        int MipCount,
        bool EncodedPayload,
        string AlphaMode,
        string PayloadPath);
}
