// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// Programmable AssetBundle build pipeline - a composable task-graph framework for building bundles.
// Inspired by Unity's ScriptableBuildPipeline (com.unity.scriptablebuildpipeline, Unity Companion
// License) but independently implemented for XEngine under MIT. SBP's reflection-based
// [InjectContext] field injection is replaced by explicit constructor injection here, which keeps the
// task contracts compile-time checked and free of reflection overhead.
//
// Core design (mirrors SBP):
//   - IBuildTask: a single step (Version + BuildReturnCode Run()).
//   - BuildContext: a typed bag of IBuildContextObject shared across tasks (parameters, content,
//     dependency data, write data, results).
//   - BuildPipeline.Run(taskList, context): runs tasks in order, aborts on the first failure.
//   - The concrete AssetBundle task list (DefaultAssetBundleTasks) wires the phases:
//       Collect -> ComputeDependency -> Pack -> WriteBundles -> WriteManifest.

using System;

namespace XEngine.Editor.AssetBundles.BuildPipeline;

/// <summary>Return code from a build task or the whole pipeline. Non-negative = success, negative = failure.</summary>
public enum BuildReturnCode
{
    Exception = -3,
    Canceled = -2,
    Error = -1,

    /// <summary>Task succeeded and produced output.</summary>
    Success = 0,

    /// <summary>Task was a cache hit and produced no new output (reserved for future caching).</summary>
    SuccessCached = 1,

    /// <summary>Task intentionally skipped (e.g. an optional phase with nothing to do).</summary>
    SuccessNotRun = 2,
}

/// <summary>Marker for objects that live in the <see cref="BuildContext"/>. Mirrors SBP's IContextObject.</summary>
public interface IBuildContextObject { }

/// <summary>
/// A single build step. Tasks are run in list order by <see cref="BuildPipeline.Run"/>; each reads its
/// inputs from and writes its outputs to the shared <see cref="BuildContext"/>. Declare a task's
/// required context objects as constructor parameters so the contract is compile-time checked.
/// </summary>
public interface IBuildTask
{
    /// <summary>Bump when a task's behavior or context contract changes, so caches can invalidate
    /// (reserved for future incremental-build support).</summary>
    int Version { get; }

    /// <summary>Human-readable task name (for progress reporting / logs).</summary>
    string Name { get; }

    /// <summary>Run the step. Return a non-negative code on success; a negative code aborts the pipeline.</summary>
    BuildReturnCode Run(BuildContext context, IProgress<BuildTaskProgress>? progress = null);
}

/// <summary>Progress report for a single task (name, fraction complete, optional info string).</summary>
public readonly struct BuildTaskProgress
{
    public readonly string TaskName;
    public readonly float Fraction;   // 0..1 within this task
    public readonly string? Info;

    public BuildTaskProgress(string taskName, float fraction, string? info = null)
    {
        TaskName = taskName;
        Fraction = fraction;
        Info = info;
    }
}
