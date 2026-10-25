// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Packages.EditorModules;

namespace XEngine.Editor.Projects.Build.PlatformToolchains;

/// <summary>Platforms a toolchain manifest can drive. iOS is locator-only (Apple forbids
/// redistribution), so its manifests carry no components.</summary>
public enum PlatformToolchainKind
{
    Android,
    HarmonyOS,
    Ios,
}

/// <summary>How a component is acquired.</summary>
public enum ToolchainComponentKind
{
    /// <summary>Download an archive (zip/tar.gz) and extract it under the managed SDK root.</summary>
    Archive,
    /// <summary>Download a single file verbatim (e.g. bundletool.jar).</summary>
    File,
    /// <summary>Delegate to the Android sdkmanager (licenses accepted in-editor, never silently).</summary>
    SdkManager,
}

/// <summary>Where a source sits in the policy ladder. Vendor sources (official distribution,
/// including our own artifacts on GitHub Releases) come first; mirrors are pinned fallbacks.</summary>
public enum ToolchainSourceKind
{
    Vendor,
    Mirror,
}

public sealed class ToolchainSource
{
    [JsonPropertyName("kind")] public ToolchainSourceKind Kind { get; set; } = ToolchainSourceKind.Vendor;
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>Hex SHA-256 of the artifact. Optional for vendor sources (verified when present);
    /// mandatory for mirrors so a fallback can never be silently swapped.</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
}

public sealed class ToolchainComponent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public ToolchainComponentKind Kind { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    /// <summary>Optional components can be skipped by the user (e.g. the NDK for size).</summary>
    [JsonPropertyName("optional")] public bool Optional { get; set; }
    [JsonPropertyName("sources")] public List<ToolchainSource> Sources { get; set; } = new();
    /// <summary>sdkmanager component ids to install (kind=SdkManager only).</summary>
    [JsonPropertyName("packages")] public List<string> Packages { get; set; } = new();
    /// <summary>Relative target directory under the platform SDK root. No rooted paths, no "..".</summary>
    [JsonPropertyName("layout")] public string Layout { get; set; } = "";
    /// <summary>Leading path segments stripped from every archive entry before extracting into
    /// <see cref="Layout"/> (e.g. the single top-level "cmdline-tools/" or "jdk-17…/" folder most
    /// vendor archives wrap their content in). 0–3.</summary>
    [JsonPropertyName("stripRootSegments")] public int StripRootSegments { get; set; }

    /// <summary>Post-extract step for archives-of-archives (the OpenHarmony combined SDK is a
    /// tar.gz of per-component zips): every <c>*.zip</c> directly under this subfolder of the
    /// component root is unpacked into the component root — the inner zips carry their own
    /// top-level folders (<c>native/</c>, <c>toolchains/</c>).</summary>
    [JsonPropertyName("unpackZipsUnder")] public string? UnpackZipsUnder { get; set; }
    /// <summary>Sentinel file (relative to the installed component root) proving a good install.</summary>
    [JsonPropertyName("verify")] public string? Verify { get; set; }
    /// <summary>Environment exports for build processes; "{componentRoot}" expands to the installed path.</summary>
    [JsonPropertyName("env")] public Dictionary<string, string> Env { get; set; } = new();
}

/// <summary>Locator for toolchains that cannot be downloaded (Xcode). The chain is declarative so
/// the resolver stays data-driven per platform module.</summary>
public sealed class ToolchainLocator
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Primary probe executable (e.g. "xcode-select"); PATH/DEVELOPER_DIR fallbacks are
    /// policy of the locator implementation, declared here for documentation.</summary>
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("minVersion")] public string? MinVersion { get; set; }
    [JsonPropertyName("verifyExecutable")] public string? VerifyExecutable { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public sealed class ToolchainManifest
{
    public const int CurrentManifestVersion = 1;
    public const string FileName = "toolchain.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [JsonPropertyName("manifestVersion")] public int ManifestVersion { get; set; }
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    /// <summary>Free-form host gate documentation (e.g. "windows-x64"); enforced by the installer UI.</summary>
    [JsonPropertyName("hostRequirement")] public string? HostRequirement { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("components")] public List<ToolchainComponent> Components { get; set; } = new();
    [JsonPropertyName("locators")] public List<ToolchainLocator> Locators { get; set; } = new();

    public static ToolchainManifest? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ToolchainManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Engine package name of the platform module driving <paramref name="platform"/>
    /// (<c>com.xengine.platform.&lt;folder&gt;</c>).</summary>
    public static string ModulePackageName(PlatformToolchainKind platform)
        => "com.xengine.platform." + platform switch
        {
            PlatformToolchainKind.HarmonyOS => "harmonyos",
            PlatformToolchainKind.Ios => "ios",
            _ => "android",
        };

    /// <summary>
    /// Read the platform module's manifest from editor-level state — the per-user editor module
    /// store first (launcher/CLI npm installs), then the Editor's bundled packages (repo dev
    /// layout and full installs ship them alongside). No project is required: this is what the
    /// launcher settings page and the editor-level CLI path resolve through.
    /// </summary>
    public static ToolchainManifest? ReadFromEditorModule(PlatformToolchainKind platform)
    {
        string packageName = ModulePackageName(platform);
        if (EditorModuleStore.TryGetModuleRoot(packageName, out _, out string storeRoot))
        {
            ToolchainManifest? fromStore = Read(Path.Combine(storeRoot, FileName));
            if (fromStore != null) return fromStore;
        }
        string builtInRoot = Path.Combine(PackageManager.BuiltInPackagesPath, packageName);
        if (Directory.Exists(builtInRoot))
            return Read(Path.Combine(builtInRoot, FileName));
        return null;
    }

    /// <summary>True when the platform module is available without a project: installed into the
    /// editor module store or bundled with the Editor. Build profiles use this to enable the
    /// managed toolchain row before any project resolves the module.</summary>
    public static bool IsEditorModulePresent(PlatformToolchainKind platform)
    {
        string packageName = ModulePackageName(platform);
        return EditorModuleStore.TryGetModuleRoot(packageName, out _, out _)
               || Directory.Exists(Path.Combine(PackageManager.BuiltInPackagesPath, packageName));
    }

    /// <summary>
    /// Read the manifest of an installed platform module. A project-resolved copy wins — a project
    /// that vendored or patched the module keeps its own — then editor-level sources (per-user
    /// module store, then the Editor bundle) cover the no-project launcher path and projects that
    /// never pinned the module.
    /// </summary>
    public static ToolchainManifest? ReadForPlatform(Project? project, PlatformToolchainKind platform)
    {
        if (project != null)
        {
            string packageName = ModulePackageName(platform);
            foreach (ResolvedPackage package in PackageManager.Resolve(project))
            {
                if (!string.Equals(package.Name, packageName, StringComparison.OrdinalIgnoreCase)) continue;
                ToolchainManifest? fromProject = Read(Path.Combine(package.ResolvedRoot, FileName));
                if (fromProject != null) return fromProject;
            }
        }
        return ReadFromEditorModule(platform);
    }

    /// <summary>
    /// Structural validation. Returns a list of problems (empty = valid). Policy highlights:
    /// mirror sources must pin a sha256 (a fallback that can be silently swapped is worse than no
    /// fallback); archive/file components need at least one absolute http(s) source; layouts are
    /// relative and traversal-free; locator-only platforms (iOS) carry no components.
    /// </summary>
    public static List<string> Validate(ToolchainManifest manifest)
    {
        var problems = new List<string>();
        if (manifest.ManifestVersion != CurrentManifestVersion)
            problems.Add($"manifestVersion must be {CurrentManifestVersion}, got {manifest.ManifestVersion}.");
        if (!TryMapPlatform(manifest.Platform, out _))
            problems.Add($"Unknown platform '{manifest.Platform}' (expected android/harmonyos/ios).");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ToolchainComponent component in manifest.Components)
        {
            string scope = string.IsNullOrEmpty(component.Id) ? "<unnamed component>" : component.Id;
            if (!ids.Add(component.Id))
                problems.Add($"Duplicate component id '{component.Id}'.");

            switch (component.Kind)
            {
                case ToolchainComponentKind.Archive:
                case ToolchainComponentKind.File:
                    if (component.Sources.Count == 0)
                        problems.Add($"Component '{scope}' ({component.Kind}) has no sources.");
                    break;
                case ToolchainComponentKind.SdkManager:
                    if (component.Packages.Count == 0)
                        problems.Add($"Component '{scope}' (sdkmanager) lists no packages.");
                    break;
                default:
                    problems.Add($"Component '{scope}' has unknown kind '{component.Kind}'.");
                    break;
            }

            foreach (ToolchainSource source in component.Sources)
            {
                if (!Uri.TryCreate(source.Url, UriKind.Absolute, out Uri? uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    problems.Add($"Component '{scope}' source url '{source.Url}' is not an absolute http(s) url.");
                }
                bool pinned = !string.IsNullOrWhiteSpace(source.Sha256);
                if (pinned && !IsHex64(source.Sha256!))
                    problems.Add($"Component '{scope}' source '{source.Url}' has a malformed sha256.");
                if (source.Kind == ToolchainSourceKind.Mirror && !pinned)
                    problems.Add($"Mirror source '{source.Url}' must pin a sha256 (fallbacks may never be silently swappable).");
            }

            if (!IsSafeRelativePath(component.Layout))
                problems.Add($"Component '{scope}' layout '{component.Layout}' must be a relative, traversal-free path.");
            if (component.StripRootSegments is < 0 or > 3)
                problems.Add($"Component '{scope}' stripRootSegments must be 0–3, got {component.StripRootSegments}.");
            if (component.UnpackZipsUnder != null && !IsSafeRelativePath(component.UnpackZipsUnder))
                problems.Add($"Component '{scope}' unpackZipsUnder '{component.UnpackZipsUnder}' must be a relative, traversal-free path.");
            if (component.Verify != null && !IsSafeRelativePath(component.Verify))
                problems.Add($"Component '{scope}' verify '{component.Verify}' must be a relative, traversal-free path.");
        }

        if (manifest.Locators.Count > 0 && manifest.Components.Count > 0)
            problems.Add("A manifest with locators must not carry components (locator platforms download nothing).");
        foreach (ToolchainLocator locator in manifest.Locators)
        {
            if (string.IsNullOrWhiteSpace(locator.Id) || string.IsNullOrWhiteSpace(locator.Tool))
                problems.Add("Locators require non-empty id and tool.");
        }

        return problems;
    }

    public static bool TryMapPlatform(string platform, out PlatformToolchainKind kind)
        => platform switch
        {
            "android" => Success(PlatformToolchainKind.Android, out kind),
            "harmonyos" => Success(PlatformToolchainKind.HarmonyOS, out kind),
            "ios" => Success(PlatformToolchainKind.Ios, out kind),
            _ => Failure(out kind),
        };

    private static bool Success(PlatformToolchainKind value, out PlatformToolchainKind kind)
    {
        kind = value;
        return true;
    }

    private static bool Failure(out PlatformToolchainKind kind)
    {
        kind = PlatformToolchainKind.Android;
        return false;
    }

    private static bool IsHex64(string value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal)) return false;
        foreach (char c in Path.GetInvalidPathChars())
            if (path.Contains(c)) return false;
        return true;
    }
}
