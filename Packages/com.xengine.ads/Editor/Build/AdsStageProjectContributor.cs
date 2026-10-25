// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Ads;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

namespace XEngine.Ads.Editor.Build;

/// <summary>
/// HarmonyOS Stage-project contribution for the ads package. No-ops for the mock provider;
/// active vendors get the ArkTS glue, the EntryAbility wiring, module permissions, and the
/// <c>Settings/Ads.json</c> runtime payload. Runs through the engine's
/// <see cref="IHarmonyOSStageProjectContributor"/> discovery after the Stage project is
/// materialized and before packaging/export.
/// </summary>
public sealed class AdsStageProjectContributor : IHarmonyOSStageProjectContributor
{
    public string Owner => "com.xengine.ads";

    public List<string> Contribute(HarmonyOSStageProjectContext context)
    {
        var errors = new List<string>();
        AdsProjectSettings? settings = EditorRegistries.GetSettings<AdsProjectSettings>();
        if (settings == null)
            return errors; // ads settings page never opened and no defaults registered — nothing to do

        AdsBuildConfig config = settings.ResolveBuildConfig(Project.Current);
        if (!config.IsActive)
            return errors; // mock provider: no HarmonyOS contribution

        if (!TryLocatePackage(errors, out string packageRoot))
            return errors;

        AdsStageProjectPatcher.Apply(context.StageProjectDirectory, packageRoot, config, errors);
        return errors;
    }

    /// <summary>Locates the installed com.xengine.ads package root (xts LocatePackage pattern).</summary>
    internal static bool TryLocatePackage(List<string> errors, out string packageRoot)
    {
        packageRoot = string.Empty;
        var candidateRoots = new List<string>();

        Project? project = Project.Current;
        if (project != null)
        {
            string dir = Path.GetFullPath(project.RootPath);
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                candidateRoots.Add(dir);
                dir = Path.GetDirectoryName(dir)!;
            }
        }
        // The package may also live in the editor install tree, above the running editor/test
        // assembly (typical for out-of-tree projects).
        string? assemblyDir = Path.GetDirectoryName(typeof(AdsStageProjectContributor).Assembly.Location);
        for (string? d = assemblyDir; !string.IsNullOrEmpty(d) && d.Length > 3; d = Path.GetDirectoryName(d))
            candidateRoots.Add(d);

        foreach (string root in candidateRoots)
        {
            string candidate = Path.Combine(root, "Packages", "com.xengine.ads");
            if (Directory.Exists(candidate))
            {
                packageRoot = candidate;
                return true;
            }
        }
        errors.Add("com.xengine.ads package not found near the project or editor; cannot contribute the ads Stage glue");
        return false;
    }
}
