// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

namespace XEngine.Editor.Build.Profiles;

/// <summary>
/// Shared <see cref="BuildSettings"/> fields after the active profile's <c>Override*</c> flags are
/// applied. Snapshotted at the start of a build so every pipeline reads one consistent view.
/// </summary>
/// <remarks>
/// An invocation output (CLI <c>-o</c>, or a folder the user picked for this build) wins over the
/// profile and the shared default. Everything else — scenes, configuration, packaging — comes from
/// the profile when its override flag is set, otherwise from <see cref="BuildSettings"/>.
/// </remarks>
public readonly struct ResolvedBuildOptions
{
    public IReadOnlyList<SceneBuildEntry> Scenes { get; init; }
    public string OutputDirectory { get; init; }
    public BuildConfiguration Config { get; init; }
    public AssetPackagingMode PackagingMode { get; init; }
    public AssetExportMode AssetMode { get; init; }
    public int MaxPakSizeMB { get; init; }

    /// <summary>
    /// Fold the active profile over <paramref name="settings"/>.
    /// <paramref name="outputOverride"/> is the per-invocation destination (CLI <c>-o</c>); a blank
    /// value means "use the profile, or the shared default".
    /// </summary>
    public static ResolvedBuildOptions From(BuildSettings settings, string? outputOverride = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        BuildProfile? profile = BuildProfileRegistry.GetActive(settings)?.Profile;
        string inherited = profile?.ResolveOutputDirectory(settings) ?? settings.OutputDirectory;
        string output = !string.IsNullOrWhiteSpace(outputOverride) ? outputOverride.Trim() : inherited;

        return new ResolvedBuildOptions
        {
            Scenes = profile?.ResolveScenes(settings) ?? settings.Scenes,
            OutputDirectory = output,
            Config = profile?.ResolveConfiguration(settings) ?? settings.Config,
            PackagingMode = profile?.ResolvePackagingMode(settings) ?? settings.PackagingMode,
            AssetMode = profile?.ResolveAssetMode(settings) ?? settings.AssetMode,
            MaxPakSizeMB = profile?.ResolveMaxPakSizeMB(settings) ?? settings.MaxPakSizeMB,
        };
    }
}
