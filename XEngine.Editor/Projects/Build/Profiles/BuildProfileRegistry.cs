// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

namespace XEngine.Editor.Build.Profiles;

/// <summary>A build profile asset paired with the identity the asset database tracks it under.</summary>
public readonly record struct BuildProfileHandle(Guid Guid, string RelativePath, BuildProfile Profile)
{
    public string DisplayName
    {
        get
        {
            string fromFile = Path.GetFileNameWithoutExtension(RelativePath);
            return string.IsNullOrWhiteSpace(fromFile) ? Profile.DisplayName : fromFile;
        }
    }

    public bool IsValid => Profile != null && Guid != Guid.Empty;
}

/// <summary>
/// Discovery, creation and activation of <see cref="BuildProfile"/> assets.
/// </summary>
/// <remarks>
/// The registry is a thin view over the editor asset database rather than a cache: profiles are
/// authored assets that the user can add, rename or delete outside the Build window, so every query
/// re-reads the database instead of trusting a snapshot.
/// </remarks>
public static class BuildProfileRegistry
{
    /// <summary>All build profile assets in the project, ordered by path for a stable UI listing.</summary>
    public static List<BuildProfileHandle> LoadAll()
    {
        var results = new List<BuildProfileHandle>();
        var backend = EditorAssetBackend.Instance;
        if (backend == null) return results;

        foreach (var entry in backend.GetAllEntries())
        {
            if (!IsProfilePath(entry.Path)) continue;
            // Editor backend explicitly (see Load) — not AssetDatabase.Current, which is the
            // bundle backend during EditorSimulate play.
            if (backend.Get(entry.Guid) is not BuildProfile profile) continue;
            results.Add(new BuildProfileHandle(entry.Guid, entry.Path, profile));
        }

        results.Sort(static (a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>Load a single profile by asset GUID, or null when it is missing or of another type.</summary>
    public static BuildProfileHandle? Load(Guid guid)
    {
        if (guid == Guid.Empty) return null;

        var backend = EditorAssetBackend.Instance;
        var entry = backend?.GetEntry(guid);
        if (entry == null || !IsProfilePath(entry.Path)) return null;
        // Resolve against the EDITOR backend explicitly: during EditorSimulate play
        // AssetDatabase.Current is the bundle backend, which owns game assets only —
        // build profiles are editor assets and would fail (per-frame) through it.
        if (backend?.Get(guid) is not BuildProfile profile) return null;

        return new BuildProfileHandle(guid, entry.Path, profile);
    }

    /// <summary>
    /// Resolve a profile from a CLI-friendly token: an asset GUID, a display name, or a project
    /// relative path. Name matching is case-insensitive and must be unambiguous.
    /// </summary>
    public static BuildProfileHandle? Resolve(string nameOrGuid)
    {
        if (string.IsNullOrWhiteSpace(nameOrGuid)) return null;
        string token = nameOrGuid.Trim();

        if (Guid.TryParse(token, out Guid parsed))
        {
            BuildProfileHandle? byGuid = Load(parsed);
            if (byGuid != null) return byGuid;
        }

        List<BuildProfileHandle> all = LoadAll();

        var byPath = all.Where(h => string.Equals(h.RelativePath, token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byPath.Count == 1) return byPath[0];

        var byName = all.Where(h => string.Equals(h.DisplayName, token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
            throw new InvalidOperationException(
                $"Build profile name '{token}' is ambiguous ({byName.Count} matches). Pass the asset GUID instead.");

        return null;
    }

    /// <summary>The profile referenced by <see cref="BuildSettings.ActiveProfileGuid"/>, if any.</summary>
    public static BuildProfileHandle? GetActive(BuildSettings settings)
        => settings == null ? null : Load(settings.ActiveProfileGuid);

    /// <summary>
    /// Point <see cref="BuildSettings.ActiveProfileGuid"/> at a profile and mirror its pipeline id into
    /// the legacy <see cref="BuildSettings.ActivePipelineId"/> so `--platform` and the editor's
    /// platform defines keep working. Returns true when the active profile actually changed.
    /// </summary>
    public static bool SetActive(BuildSettings settings, Guid guid, bool requestRecompile = true)
    {
        if (settings == null) return false;
        if (settings.ActiveProfileGuid == guid) return false;

        BuildProfileHandle? handle = Load(guid);
        settings.ActiveProfileGuid = guid;
        settings.ActivePipelineId = handle?.Profile.PipelineId ?? "";
        EditorRegistries.SaveSettings();

        if (requestRecompile)
            ScriptAssemblyManager.RequestRecompile();

        return true;
    }

    /// <summary>
    /// Create a new profile asset for a pipeline under <see cref="BuildProfile.DefaultFolder"/>,
    /// seeding it with that pipeline's default platform settings object. The file name is made unique
    /// so creating "Windows" twice yields "Windows" and "Windows 1" instead of silently overwriting.
    /// </summary>
    public static BuildProfileHandle? Create(string displayName, string pipelineId, string? folder = null)
    {
        var backend = EditorAssetBackend.Instance;
        var project = Project.Current;
        if (backend == null || project == null) return null;
        if (string.IsNullOrWhiteSpace(pipelineId)) return null;

        string targetFolder = string.IsNullOrWhiteSpace(folder) ? BuildProfile.DefaultFolder : folder!;
        string relativeFolder = StripAssetsPrefix(targetFolder);

        var profile = ScriptableObject.CreateInstance<BuildProfile>();
        profile.PipelineId = pipelineId;
        profile.PlatformSettings = CreatePlatformSettings(pipelineId);
        profile.Name = SanitizeFileName(displayName);

        string fileName = MakeUniqueFileName(project, relativeFolder, profile.Name);
        string relativePath = string.IsNullOrEmpty(relativeFolder)
            ? fileName + BuildProfile.FileExtension
            : relativeFolder + "/" + fileName + BuildProfile.FileExtension;

        profile.Name = fileName;
        backend.CreateAsset(profile, relativePath);

        return Load(profile.AssetID);
    }

    /// <summary>
    /// Write an in-memory profile back to its asset file and reimport it, returning a handle to the
    /// reimported instance.
    /// </summary>
    /// <remarks>
    /// Saving replaces the cached instance, so the <see cref="BuildProfile"/> inside the handle that
    /// was passed in is stale afterwards - use the returned handle. Note this deliberately does not
    /// go through <c>CreateAsset</c>, which mints a fresh GUID and would register a second asset
    /// entry for the same file on every save.
    /// </remarks>
    public static BuildProfileHandle? Save(BuildProfileHandle handle)
    {
        if (!handle.IsValid) return null;
        var backend = EditorAssetBackend.Instance;
        if (backend == null) return null;

        Guid guid = handle.Profile.AssetID;
        backend.SaveAsset(handle.Profile);
        return Load(guid);
    }

    /// <summary>
    /// Instantiate the <see cref="PlatformBuildProfile"/> subclass bound to a pipeline id, or null
    /// when no subclass declares that pipeline.
    /// </summary>
    public static PlatformBuildProfile? CreatePlatformSettings(string pipelineId)
    {
        Type? pipelineType = ResolvePipelineType(pipelineId);
        if (pipelineType == null) return null;

        foreach (var type in ScriptAssemblyManager.GetAllTypes())
        {
            if (!type.IsSubclassOf(typeof(PlatformBuildProfile)) || type.IsAbstract) continue;

            PlatformBuildProfile? candidate;
            try { candidate = Activator.CreateInstance(type) as PlatformBuildProfile; }
            catch { continue; }

            if (candidate != null && candidate.GetPipelineType() == pipelineType)
                return candidate;
        }

        return null;
    }

    /// <summary>Resolve a <see cref="BuildPipeline"/> concrete type from its stable id.</summary>
    public static Type? ResolvePipelineType(string pipelineId)
    {
        if (string.IsNullOrWhiteSpace(pipelineId)) return null;

        foreach (BuildPipeline pipeline in EnumeratePipelines())
        {
            if (string.Equals(pipeline.Id, pipelineId, StringComparison.OrdinalIgnoreCase))
                return pipeline.GetType();
        }

        return null;
    }

    /// <summary>Stable id of a concrete <see cref="BuildPipeline"/> type, or null when unavailable.</summary>
    public static string? ResolvePipelineId(Type pipelineType)
    {
        if (pipelineType == null) return null;

        try { return (Activator.CreateInstance(pipelineType) as BuildPipeline)?.Id; }
        catch { return null; }
    }

    /// <summary>
    /// Human-readable platform name for a pipeline id, used to seed new profile names. Falls back to
    /// the id itself so an unknown pipeline still produces a usable file name.
    /// </summary>
    public static string ResolvePipelineDisplayName(string pipelineId)
    {
        foreach (BuildPipeline pipeline in EnumeratePipelines())
        {
            if (string.Equals(pipeline.Id, pipelineId, StringComparison.OrdinalIgnoreCase))
                return pipeline.DisplayName;
        }

        return pipelineId;
    }

    /// <summary>Every concrete build pipeline that can be instantiated in this editor session.</summary>
    /// <remarks>
    /// Cached: the uncached version swept every loaded assembly (Assembly.GetTypes allocates a
    /// RuntimeType[] per assembly per call) and re-instantiated every pipeline PER CALL, while
    /// BuildSettingsPanel resolves display name + icon through here per profile row per frame.
    /// Pipelines are stateless descriptors, so one shared instance list is safe. The cache clears
    /// on script assembly load/unload so hot-reloaded pipeline types are re-discovered and no
    /// collectible ALC is pinned across a reload.
    /// </remarks>
    public static IEnumerable<BuildPipeline> EnumeratePipelines()
        => s_pipelineCache ??= DiscoverPipelines();

    private static List<BuildPipeline>? s_pipelineCache;

    private static List<BuildPipeline> DiscoverPipelines()
    {
        var pipelines = new List<BuildPipeline>();
        foreach (var type in ScriptAssemblyManager.GetAllTypes())
        {
            if (!type.IsSubclassOf(typeof(BuildPipeline)) || type.IsAbstract) continue;

            BuildPipeline? instance;
            try { instance = Activator.CreateInstance(type) as BuildPipeline; }
            catch { continue; }

            if (instance != null) pipelines.Add(instance);
        }
        return pipelines;
    }

    [OnAssemblyLoad]
    [OnAssemblyUnload]
    internal static void InvalidatePipelineCache() => s_pipelineCache = null;

    public static bool IsProfilePath(string? relativePath)
        => !string.IsNullOrEmpty(relativePath)
           && relativePath!.EndsWith(BuildProfile.FileExtension, StringComparison.OrdinalIgnoreCase);

    private static string StripAssetsPrefix(string folder)
    {
        string normalized = folder.Replace('\\', '/').Trim('/');
        if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["Assets/".Length..];
        else if (string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase))
            normalized = "";
        return normalized;
    }

    private static string MakeUniqueFileName(Project project, string relativeFolder, string baseName)
    {
        string absoluteFolder = string.IsNullOrEmpty(relativeFolder)
            ? project.AssetsPath
            : Path.Combine(project.AssetsPath, relativeFolder.Replace('/', Path.DirectorySeparatorChar));

        string candidate = baseName;
        int suffix = 1;
        while (File.Exists(Path.Combine(absoluteFolder, candidate + BuildProfile.FileExtension)))
            candidate = $"{baseName} {suffix++}";

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        string cleaned = string.IsNullOrWhiteSpace(name) ? "Build Profile" : name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(invalid, '_');
        return cleaned;
    }
}
