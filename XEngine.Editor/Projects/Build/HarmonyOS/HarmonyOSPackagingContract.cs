// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Projects.Build;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>One locked provider target that builds and verifies a Stage package.</summary>
internal sealed record HarmonyOSPackagingAction(
    string BuildTarget,
    string VerifyTarget,
    string ArtifactRelativePath);

/// <summary>
/// Provider-owned packaging adapter contract materialized with the Stage template. The adapter
/// targets come from OpenHarmony.NET.HapTooling; XEngine invokes them through the local .NET SDK.
/// </summary>
internal sealed class HarmonyOSPackagingContract
{
    internal const string RelativePath = "xengine-packaging.json";
    private static readonly string[] s_requiredNativeHostDependencies =
    [
        "libace_napi.z.so",
        "libace_ndk.z.so",
        "libhilog_ndk.z.so",
        "libnative_window.so",
        "libvulkan.so",
        "libxengine_player.so",
    ];

    private HarmonyOSPackagingContract(
        HarmonyOSPackagingAction hap,
        HarmonyOSPackagingAction app,
        IReadOnlyList<string> allowedElfDependencies)
    {
        Hap = hap;
        App = app;
        AllowedElfDependencies = allowedElfDependencies;
    }

    internal HarmonyOSPackagingAction Hap { get; }
    internal HarmonyOSPackagingAction App { get; }
    internal IReadOnlyList<string> AllowedElfDependencies { get; }

    internal HarmonyOSPackagingAction GetAction(HarmonyOSOutputMode outputMode) => outputMode switch
    {
        HarmonyOSOutputMode.Hap => Hap,
        HarmonyOSOutputMode.App => App,
        _ => throw new ArgumentOutOfRangeException(nameof(outputMode), "Export Project has no package action."),
    };

    internal void RequireNativeHostDependencies()
    {
        var allowed = new HashSet<string>(AllowedElfDependencies, StringComparer.Ordinal);
        for (int i = 0; i < s_requiredNativeHostDependencies.Length; i++)
        {
            string dependency = s_requiredNativeHostDependencies[i];
            if (!allowed.Contains(dependency))
            {
                throw new InvalidDataException(
                    $"HarmonyOS packaging contract is missing native host dependency '{dependency}'.");
            }
        }
    }

    internal static async Task<HarmonyOSPackagingContract> ReadAsync(
        string stageProjectDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageProjectDirectory);
        string stageRoot = Path.GetFullPath(stageProjectDirectory);
        string path = Path.Combine(stageRoot, RelativePath);
        if (!File.Exists(path))
            throw new InvalidDataException($"HarmonyOS Stage template is missing {RelativePath}.");

        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 1)
            {
                throw new InvalidDataException("HarmonyOS packaging contract must declare schemaVersion 1.");
            }

            HarmonyOSPackagingAction hap = ReadAction(root, "hap", ".hap");
            HarmonyOSPackagingAction app = ReadAction(root, "app", ".app");
            IReadOnlyList<string> dependencies = ReadDependencies(root);
            return new HarmonyOSPackagingContract(hap, app, dependencies);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("HarmonyOS packaging contract is invalid JSON.", exception);
        }
    }

    private static HarmonyOSPackagingAction ReadAction(JsonElement root, string propertyName, string extension)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement action) || action.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"HarmonyOS packaging contract must declare '{propertyName}'.");

        string buildTarget = ReadString(action, "buildTarget");
        string verifyTarget = ReadString(action, "verifyTarget");
        string artifact = ReadString(action, "artifact");
        if (!IsMsBuildTarget(buildTarget) || !IsMsBuildTarget(verifyTarget))
            throw new InvalidDataException($"HarmonyOS packaging contract '{propertyName}' contains an invalid MSBuild target.");
        if (!IsSafeRelativePath(artifact) ||
            !string.Equals(Path.GetExtension(artifact), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"HarmonyOS packaging contract '{propertyName}' artifact must be a safe Stage-relative {extension} path.");
        }

        return new HarmonyOSPackagingAction(buildTarget, verifyTarget, artifact);
    }

    private static IReadOnlyList<string> ReadDependencies(JsonElement root)
    {
        if (!root.TryGetProperty("allowedElfDependencies", out JsonElement dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("HarmonyOS packaging contract must declare allowedElfDependencies.");
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (dependency.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("HarmonyOS packaging contract contains a non-string ELF dependency.");
            string value = dependency.GetString() ?? string.Empty;
            if (!IsNativeLibraryName(value) || !seen.Add(value))
                throw new InvalidDataException($"HarmonyOS packaging contract contains an invalid ELF dependency: {value}");
            result.Add(value);
        }
        if (result.Count == 0)
            throw new InvalidDataException("HarmonyOS packaging contract ELF dependency allowlist cannot be empty.");
        result.Sort(StringComparer.Ordinal);
        return result.AsReadOnly();
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"HarmonyOS packaging contract property '{propertyName}' is required.");
        return value.GetString() ?? string.Empty;
    }

    private static bool IsMsBuildTarget(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
            return false;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsLetterOrDigit(c) && c is not '_' and not '.')
                return false;
        }
        return true;
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] == '/' || value.Contains('\\') || Path.IsPathFullyQualified(value))
            return false;
        string[] segments = value.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] is "." or "..")
                return false;
        }
        return true;
    }

    private static bool IsNativeLibraryName(string value)
    {
        if (!value.StartsWith("lib", StringComparison.Ordinal) || value.Length <= 6)
            return false;
        // Accept both unversioned (libcrypto.so) and versioned (libssl.so.3, libssl.so.1.1)
        // ELF SONAMEs. Versioned names are required for OpenSSL libs whose SONAMEs the
        // .NET System.Security.Cryptography shim dlopens by exact versioned name.
        int soIndex = value.IndexOf(".so", StringComparison.Ordinal);
        if (soIndex < 0 || soIndex + 3 > value.Length)
            return false;
        string suffix = value.AsSpan(soIndex + 3).ToString();
        if (suffix.Length > 0)
        {
            // Everything after ".so" must be version digits/dots, e.g. ".3" or ".1.1".
            if (suffix[0] != '.')
                return false;
            for (int i = 1; i < suffix.Length; i++)
            {
                char c = suffix[i];
                if (!char.IsDigit(c) && c != '.')
                    return false;
            }
        }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            // '+' is required for C++ runtime names such as libc++_shared.so.
            if (!char.IsLetterOrDigit(c) && c is not '_' and not '-' and not '.' and not '+')
                return false;
        }
        return true;
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
}
