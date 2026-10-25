// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

namespace XEngine.Editor.Build.Profiles;

/// <summary>
/// One-time conversion of a project's legacy <see cref="BuildSettings.PlatformProfiles"/> list into
/// <c>.buildprofile</c> assets.
/// </summary>
/// <remarks>
/// <para>Runs when a project is opened. It is a no-op for projects that have never configured a
/// platform (a fresh project starts with an empty list and gets no profiles, matching Unity, where
/// the profile list starts empty) and for projects that already contain profile assets, so opening
/// a migrated project repeatedly does not keep minting duplicates.</para>
/// <para>The legacy list is intentionally left on disk. It stays readable for one release so that
/// downgrading the editor, or opening the project on a branch that predates build profiles, still
/// finds its platform settings.</para>
/// </remarks>
public static class BuildProfileMigration
{
    /// <summary>
    /// Convert legacy platform profiles into assets. Returns the number of profiles created.
    /// </summary>
    public static int Migrate(BuildSettings settings)
    {
        if (settings == null) return 0;
        if (settings.PlatformProfiles.Count == 0) return 0;
        if (EditorAssetBackend.Instance == null) return 0;

        // Already migrated: any profile asset at all means this project has moved over, and a second
        // pass would duplicate every platform.
        if (BuildProfileRegistry.LoadAll().Count > 0)
            return 0;

        int created = 0;
        var createdHandles = new List<BuildProfileHandle>();

        foreach (PlatformBuildProfile legacy in settings.PlatformProfiles)
        {
            Type? pipelineType = legacy?.GetPipelineType();
            if (legacy == null || pipelineType == null) continue;

            string? pipelineId = BuildProfileRegistry.ResolvePipelineId(pipelineType);
            if (string.IsNullOrEmpty(pipelineId)) continue;

            BuildProfileHandle? handle = BuildProfileRegistry.Create(
                BuildProfileRegistry.ResolvePipelineDisplayName(pipelineId!), pipelineId!);
            if (handle == null) continue;

            // Carry the authored settings across rather than the pipeline's defaults, otherwise the
            // migration would silently reset things like keystore paths and window size.
            handle.Value.Profile.PlatformSettings = legacy;
            BuildProfileHandle? saved = BuildProfileRegistry.Save(handle.Value);
            if (saved == null) continue;

            createdHandles.Add(saved.Value);
            created++;
        }

        if (created == 0) return 0;

        BuildProfileHandle active = createdHandles.Find(
            h => string.Equals(h.Profile.PipelineId, settings.ActivePipelineId, StringComparison.OrdinalIgnoreCase));
        if (!active.IsValid)
            active = createdHandles[0];

        BuildProfileRegistry.SetActive(settings, active.Guid, requestRecompile: false);
        Debug.Log($"[Build Profiles] Migrated {created} legacy platform profile(s) to .buildprofile assets.");
        return created;
    }
}
