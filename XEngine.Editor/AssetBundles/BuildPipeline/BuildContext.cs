// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Editor.AssetBundles.BuildPipeline;

/// <summary>
/// A typed bag of <see cref="IBuildContextObject"/>s shared across all tasks in a pipeline run. Keys
/// are the concrete object types (or registered interface types). Mirrors SBP's BuildContext, but
/// uses a plain Type-keyed dictionary without SBP's assignable-types walk - tasks ask for the exact
/// type they need, which is simpler and avoids surprising aliasing.
/// </summary>
public sealed class BuildContext
{
    private readonly Dictionary<Type, IBuildContextObject> _objects = new();

    /// <summary>Register <paramref name="obj"/> under its own type AND any extra <paramref name="aliases"/>
    /// (typically interfaces). A later Set for the same key overwrites.</summary>
    public void Set<T>(T obj, params Type[] aliases) where T : class, IBuildContextObject
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        _objects[typeof(T)] = obj;
        foreach (var alias in aliases) _objects[alias] = obj;
    }

    /// <summary>Register under a specific (usually interface) type.</summary>
    public void Set(Type key, IBuildContextObject obj)
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        _objects[key] = obj;
    }

    public T Get<T>() where T : class, IBuildContextObject
    {
        if (!_objects.TryGetValue(typeof(T), out var obj))
            throw new InvalidOperationException($"BuildContext has no object of type {typeof(T).Name}. Register it before running the pipeline.");
        return (T)obj;
    }

    public bool TryGet<T>(out T obj) where T : class, IBuildContextObject
    {
        if (_objects.TryGetValue(typeof(T), out var raw))
        {
            obj = (T)raw;
            return true;
        }
        obj = null!;
        return false;
    }

    public bool Contains<T>() where T : class, IBuildContextObject => _objects.ContainsKey(typeof(T));

    /// <summary>Snapshot of all registered type keys (diagnostics only).</summary>
    public IEnumerable<Type> RegisteredTypes => _objects.Keys;
}

/// <summary>
/// Runs a list of <see cref="IBuildTask"/>s against a <see cref="BuildContext"/>. Tasks execute in
/// list order; the first negative return code aborts the run. Mirrors SBP's BuildTasksRunner.Run,
/// minus the reflection inject/extract (tasks here take the context explicitly).
/// </summary>
public static class BuildPipelineRunner
{
    /// <summary>Run <paramref name="tasks"/> in order against <paramref name="context"/>. Returns the
    /// final return code (Success if all tasks succeeded, otherwise the first failure code).</summary>
    public static BuildReturnCode Run(IList<IBuildTask> tasks, BuildContext context, IProgress<BuildTaskProgress>? progress = null)
    {
        if (tasks == null) throw new ArgumentNullException(nameof(tasks));
        if (context == null) throw new ArgumentNullException(nameof(context));

        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            progress?.Report(new BuildTaskProgress(task.Name, 0f, $"({i + 1}/{tasks.Count})"));

            BuildReturnCode result;
            try
            {
                result = task.Run(context, progress);
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"[BuildPipeline] Task '{task.Name}' threw: {ex}");
                return BuildReturnCode.Exception;
            }

            if (result < BuildReturnCode.Success)
            {
                Runtime.Debug.LogError($"[BuildPipeline] Task '{task.Name}' failed with code {result}.");
                return result;
            }
        }
        return BuildReturnCode.Success;
    }
}
