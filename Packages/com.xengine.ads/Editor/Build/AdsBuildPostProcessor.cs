// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

namespace XEngine.Ads.Editor.Build;

/// <summary>
/// Player-build verification for the ads package. When a HarmonyOS vendor provider is active
/// the packaged <c>libxengine_ads.so</c> must exist in the package Plugins tree and match the
/// sha256 pinned by <c>Native/native-manifest.json</c> (the HAP embeds exactly that file).
/// Any problem fails the build.
/// </summary>
public sealed class AdsBuildPostProcessor : IBuildPostProcessor
{
    public string Owner => "com.xengine.ads";

    public List<string> Process(BuildPostContext context)
    {
        var errors = new List<string>();
        AdsProjectSettings? settings = EditorRegistries.GetSettings<AdsProjectSettings>();
        if (settings == null)
            return errors;
        AdsBuildConfig config = settings.ResolveBuildConfig(Project.Current);
        if (!config.IsActive)
            return errors;
        if (!string.Equals(context.Platform, "xengine.harmonyos", StringComparison.OrdinalIgnoreCase))
            return errors;

        if (!AdsStageProjectContributor.TryLocatePackage(errors, out string packageRoot))
            return errors;

        string manifestPath = Path.Combine(packageRoot, "Native", "native-manifest.json");
        if (!File.Exists(manifestPath))
        {
            errors.Add("package native manifest is missing (Native/native-manifest.json)");
            return errors;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("platforms", out JsonElement platforms) ||
            !platforms.TryGetProperty("harmonyos-arm64", out JsonElement entry) ||
            !entry.TryGetProperty("library", out JsonElement libraryElement) ||
            !entry.TryGetProperty("sha256", out JsonElement shaElement))
        {
            errors.Add("native manifest has no harmonyos-arm64 entry; build the .so via Native/build-harmonyos.ps1");
            return errors;
        }

        string libraryRelative = libraryElement.GetString() ?? string.Empty;
        string libraryPath = Path.Combine(packageRoot, libraryRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(libraryPath))
        {
            errors.Add($"native library '{libraryRelative}' is missing; build it via Native/build-harmonyos.ps1");
            return errors;
        }

        string shippedSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(libraryPath))).ToLowerInvariant();
        string pinnedSha = shaElement.GetString() ?? string.Empty;
        if (!string.Equals(shippedSha, pinnedSha, StringComparison.OrdinalIgnoreCase))
            errors.Add($"native library hash mismatch: manifest pins {pinnedSha}, package ships {shippedSha} (rebuild via Native/build-harmonyos.ps1)");
        return errors;
    }
}
