// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

namespace XEngine.Editor.Build.Profiles;

/// <summary>
/// A named, per-platform build configuration stored as a <c>.buildprofile</c> asset.
/// </summary>
/// <remarks>
/// <para>Replaces the old "one <see cref="BuildSettings.ActivePipelineId"/> plus one
/// <see cref="PlatformBuildProfile"/> per pipeline type" model, which could not represent two
/// configurations of the same platform (a dev and a store build for Android, say). Each profile
/// binds to a <see cref="BuildPipeline.Id"/> and carries that platform's settings object, so a
/// project can hold as many profiles per platform as it needs.</para>
/// <para>Lives in the editor assembly on purpose: it holds <see cref="PlatformBuildProfile"/>,
/// which is editor-only. The player never reads a profile - it reads the
/// <c>Content/Settings/*.yaml</c> that <see cref="BuildPipeline.ExportSettings"/> emits.</para>
/// <para>Every <c>Override*</c> flag follows the same rule: when false the corresponding value is
/// inherited from the shared <see cref="BuildSettings"/>, when true this profile's own value wins.
/// Use the <c>Resolve*</c> helpers rather than reading the raw fields so the inheritance is applied
/// consistently.</para>
/// </remarks>
[CreateAssetMenu("Build/Build Profile", Extension = BuildProfile.FileExtension, Order = 60)]
public sealed class BuildProfile : ScriptableObject
{
    /// <summary>File extension (including the dot) used for build profile assets.</summary>
    public const string FileExtension = ".buildprofile";

    /// <summary>Default project-relative folder new profiles are created in.</summary>
    public const string DefaultFolder = "Assets/Settings/BuildProfiles";

    /// <summary>
    /// Stable id of the <see cref="BuildPipeline"/> this profile targets (for example
    /// <c>xengine.android</c>). A profile with an empty or unknown id cannot be built.
    /// </summary>
    public string PipelineId = "";

    /// <summary>
    /// Platform-specific settings for <see cref="PipelineId"/>. Serialized polymorphically, so the
    /// concrete subclass (<c>AndroidBuildProfile</c>, <c>HarmonyOSBuildProfile</c>, ...) round-trips.
    /// </summary>
    public PlatformBuildProfile? PlatformSettings;

    public bool OverrideScenes;
    public List<SceneBuildEntry> Scenes = [];

    public bool OverrideOutputDirectory;
    public string OutputDirectory = "Builds";

    public bool OverrideConfiguration;
    public BuildConfiguration Configuration = BuildConfiguration.Release;

    public bool OverridePackaging;
    public AssetPackagingMode PackagingMode = AssetPackagingMode.XEnginePak;
    public AssetExportMode AssetMode = AssetExportMode.DependenciesOnly;
    public int MaxPakSizeMB = 2048;

    /// <summary>Per-profile Player Settings overrides applied only while this profile builds.</summary>
    public List<ProfileSettingOverride> SettingOverrides = [];

    /// <summary>Per-profile asset import downgrades used to speed up iteration builds.</summary>
    public ProfileAssetImportOverrides AssetImportOverrides = new();

    /// <summary>Display name, falling back to the asset file name when <see cref="Name"/> is unset.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Build Profile" : Name;

    public List<SceneBuildEntry> ResolveScenes(BuildSettings shared)
        => OverrideScenes ? Scenes : shared.Scenes;

    public string ResolveOutputDirectory(BuildSettings shared)
        => OverrideOutputDirectory && !string.IsNullOrWhiteSpace(OutputDirectory)
            ? OutputDirectory
            : shared.OutputDirectory;

    public BuildConfiguration ResolveConfiguration(BuildSettings shared)
        => OverrideConfiguration ? Configuration : shared.Config;

    public AssetPackagingMode ResolvePackagingMode(BuildSettings shared)
        => OverridePackaging ? PackagingMode : shared.PackagingMode;

    public AssetExportMode ResolveAssetMode(BuildSettings shared)
        => OverridePackaging ? AssetMode : shared.AssetMode;

    public int ResolveMaxPakSizeMB(BuildSettings shared)
        => OverridePackaging ? MaxPakSizeMB : shared.MaxPakSizeMB;

    /// <summary>
    /// This profile's scripting define symbols: the authored list on
    /// <see cref="PlatformBuildProfile.ScriptingDefineSymbols"/> followed by whatever the platform
    /// derives in <see cref="PlatformBuildProfile.ModifyDefines"/>, blanks dropped and duplicates
    /// collapsed. The ordering matches <c>BuildPipeline.FinalizeDefineString</c>.
    /// </summary>
    public List<string> CollectDefines()
    {
        var defines = new List<string>();
        if (PlatformSettings == null) return defines;

        defines.AddRange(PlatformSettings.ScriptingDefineSymbols);
        PlatformSettings.ModifyDefines(defines);

        var unique = new List<string>(defines.Count);
        foreach (string define in defines)
        {
            if (string.IsNullOrWhiteSpace(define)) continue;
            string trimmed = define.Trim();
            if (!unique.Contains(trimmed, StringComparer.Ordinal))
                unique.Add(trimmed);
        }

        return unique;
    }
}
