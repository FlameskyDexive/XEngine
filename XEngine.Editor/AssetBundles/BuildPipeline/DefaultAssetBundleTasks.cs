// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Editor.AssetBundles.BuildPipeline;

/// <summary>
/// The canonical AssetBundle task list: Collect -> ComputeDependency -> WriteBundles -> WriteManifest.
/// Mirrors SBP's DefaultBuildTasks.Preset.AssetBundleCompatible (which is Setup -> Scripts ->
/// Dependency -> Packing -> Writing); XEngine has no separate script-compilation or built-in-shader
/// phases, so the list reduces to the four bundle-relevant tasks. Callers may insert custom tasks
/// anywhere in this list (e.g. an encryption task between WriteBundles and WriteManifest).
/// </summary>
public static class DefaultAssetBundleTasks
{
    /// <summary>The default ordered task list for a full AssetBundle build.</summary>
    public static IList<IBuildTask> Create()
    {
        return new List<IBuildTask>
        {
            new CollectTask(),
            new ComputeDependencyTask(),
            new WriteBundlesTask(),
            new WriteManifestTask(),
        };
    }
}
