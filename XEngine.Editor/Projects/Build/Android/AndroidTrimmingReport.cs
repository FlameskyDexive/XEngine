// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XEngine.Editor.Build.Android;

/// <summary>
/// Controls the managed trimming contract exposed by the Android build profile.
/// Full is intentionally not executable until generated roots and linker descriptors are complete.
/// </summary>
public enum AndroidManagedStrippingMode
{
    Disabled,
    Safe,
    Full,
}

internal sealed record AndroidTrimEntry(
    string Id,
    string Action,
    string Scope,
    IReadOnlyList<string> Reasons);

internal sealed record AndroidTrimCategory(
    string Id,
    string Mode,
    string Execution,
    IReadOnlyList<AndroidTrimEntry> Entries);

internal sealed class AndroidTrimmingReport
{
    internal const int SchemaVersion = 1;

    private AndroidTrimmingReport(
        AndroidManagedStrippingMode requestedManagedMode,
        AndroidManagedStrippingMode effectiveManagedMode,
        bool publishTrimmed,
        int assetCount,
        int shaderVariantCount,
        int managedTypeRootCount,
        IReadOnlyList<string> targetAbis,
        IReadOnlyList<AndroidTrimCategory> categories)
    {
        RequestedManagedMode = requestedManagedMode;
        EffectiveManagedMode = effectiveManagedMode;
        PublishTrimmed = publishTrimmed;
        AssetCount = assetCount;
        ShaderVariantCount = shaderVariantCount;
        ManagedTypeRootCount = managedTypeRootCount;
        TargetAbis = targetAbis;
        Categories = categories;
    }

    internal AndroidManagedStrippingMode RequestedManagedMode { get; }
    internal AndroidManagedStrippingMode EffectiveManagedMode { get; }
    internal bool PublishTrimmed { get; }
    internal int AssetCount { get; }
    internal int ShaderVariantCount { get; }
    internal int ManagedTypeRootCount { get; }
    internal IReadOnlyList<string> TargetAbis { get; }
    internal IReadOnlyList<AndroidTrimCategory> Categories { get; }

    internal static void ValidateRequestedMode(AndroidManagedStrippingMode mode)
    {
        if (mode == AndroidManagedStrippingMode.Full)
        {
            throw new NotSupportedException(
                "Android Full managed trimming is blocked until generated reflection roots, linker descriptors, " +
                "and trimmed/untrimmed behavior evidence are complete. Use Disabled or Safe.");
        }
    }

    internal static AndroidTrimmingReport Create(
        AndroidManagedStrippingMode requestedManagedMode,
        int assetCount,
        int shaderVariantCount,
        IEnumerable<string> targetAbis,
        int managedTypeRootCount = 0)
    {
        if (assetCount < 0)
            throw new ArgumentOutOfRangeException(nameof(assetCount));
        if (shaderVariantCount < 0)
            throw new ArgumentOutOfRangeException(nameof(shaderVariantCount));
        if (managedTypeRootCount < 0)
            throw new ArgumentOutOfRangeException(nameof(managedTypeRootCount));
        ArgumentNullException.ThrowIfNull(targetAbis);

        var normalizedAbis = new List<string>();
        var seenAbis = new HashSet<string>(StringComparer.Ordinal);
        foreach (string abi in targetAbis)
        {
            if (string.IsNullOrWhiteSpace(abi))
                throw new ArgumentException("Android trimming reports require non-empty ABI names.", nameof(targetAbis));
            if (seenAbis.Add(abi))
                normalizedAbis.Add(abi);
        }
        if (normalizedAbis.Count == 0)
            throw new ArgumentException("Android trimming reports require at least one ABI.", nameof(targetAbis));
        normalizedAbis.Sort(StringComparer.Ordinal);

        AndroidManagedStrippingMode effectiveManagedMode = requestedManagedMode == AndroidManagedStrippingMode.Safe
            ? AndroidManagedStrippingMode.Safe
            : AndroidManagedStrippingMode.Disabled;
        bool publishTrimmed = effectiveManagedMode == AndroidManagedStrippingMode.Safe;
        string managedExecution = requestedManagedMode switch
        {
            AndroidManagedStrippingMode.Disabled => "disabled",
            AndroidManagedStrippingMode.Safe => "enabled",
            AndroidManagedStrippingMode.Full => "blocked",
            _ => throw new ArgumentOutOfRangeException(nameof(requestedManagedMode)),
        };

        var managedEntries = new List<AndroidTrimEntry>
        {
            new(
                "android-entrypoint",
                "keep",
                "MainActivity + generated Android host",
                ["activity-entrypoint", "jni-callback"]),
            new(
                "runtime-reflection-roots",
                "keep",
                managedTypeRootCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " serialized/build-asset type roots",
                ["echo-type-metadata", "generated-dynamic-dependency", "safe-mode-never-remove"]),
        };

        var platformEntries = new List<AndroidTrimEntry>
        {
            new(
                "android-target-closure",
                "keep",
                "net10.0-android target and selected runtime identifiers",
                ["target-framework", "target-abi"]),
            new(
                "desktop-and-editor-closure",
                "remove",
                "desktop windowing, D3D12/OpenGL and editor-only dependencies",
                ["android-platform-boundary", "no-post-package-delete"]),
        };

        var assetEntries = new List<AndroidTrimEntry>
        {
            new(
                "asset-reachability-closure",
                "keep",
                assetCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " collected assets",
                ["build-scenes", "transitive-dependencies", "resources-and-sub-assets"]),
        };

        var shaderEntries = new List<AndroidTrimEntry>
        {
            new(
                "android-vulkan-variant-closure",
                "keep",
                shaderVariantCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " Vulkan descriptors",
                ["android-vulkan-target", "pass-stable-id", "canonical-keyword-set", "reason-report"]),
        };

        var nativeEntries = new List<AndroidTrimEntry>(normalizedAbis.Count + 2);
        for (int i = 0; i < normalizedAbis.Count; i++)
        {
            nativeEntries.Add(new AndroidTrimEntry(
                "native-abi-" + normalizedAbis[i],
                "keep",
                normalizedAbis[i] + " ELF payload",
                ["selected-target-abi", "elf-and-16kb-gate"]));
        }
        nativeEntries.Add(new AndroidTrimEntry(
            "r8-java-binding-keep-rules",
            "planned",
            "Android Java/JNI binding callbacks",
            ["r8-is-independent-from-managed-il-link", "mapping-archive-required"]));

        var categories = new List<AndroidTrimCategory>
        {
            new("platform-dependency", "Safe", "observed", platformEntries),
            new("managed-il-link", requestedManagedMode.ToString(), managedExecution, managedEntries),
            new("asset-reachability", "Safe", "collected", assetEntries),
            new("shader-variant", "Safe", "collected", shaderEntries),
            new("native-and-r8", "Safe", "planned", nativeEntries),
        };

        return new AndroidTrimmingReport(
            requestedManagedMode,
            effectiveManagedMode,
            publishTrimmed,
            assetCount,
            shaderVariantCount,
            managedTypeRootCount,
            Array.AsReadOnly(normalizedAbis.ToArray()),
            Array.AsReadOnly(categories.ToArray()));
    }

    internal static void WriteReport(string path, AndroidTrimmingReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);

        var payload = new
        {
            schemaVersion = SchemaVersion,
            requestedManagedMode = report.RequestedManagedMode.ToString(),
            effectiveManagedMode = report.EffectiveManagedMode.ToString(),
            publishTrimmed = report.PublishTrimmed,
            assetCount = report.AssetCount,
            shaderVariantCount = report.ShaderVariantCount,
            managedTypeRootCount = report.ManagedTypeRootCount,
            targetAbis = report.TargetAbis,
            categories = report.Categories,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) + Environment.NewLine);
    }
}
