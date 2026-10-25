// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Linq;
using System.Collections.Generic;

namespace XEngine.Editor.Projects.Build;

/// <summary>Context handed to post-processors after a successful build.</summary>
public sealed class BuildPostContext
{
    public required string ProjectPath;
    public required string OutputDirectory;
    public required string Platform;
    /// <summary>Shared bag so processors can pass data (e.g. manifest paths).</summary>
    public Dictionary<string, string> Data { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Optional build post-processing extension point (plan §7: a generic optional-package
/// build interface so the core never hardcodes package internals). Packages implement
/// <see cref="IBuildPostProcessor"/>; processors are discovered from loaded assemblies on
/// first build and run after every successful build. A processor returns the list of
/// human-readable errors it produced (empty/null = success).
/// </summary>
public interface IBuildPostProcessor
{
    /// <summary>Stable owner identifier (usually the package name).</summary>
    string Owner { get; }

    /// <summary>Runs after a successful build. Return errors; empty/null = success.</summary>
    List<string> Process(BuildPostContext context);
}

public static class BuildPostProcessor
{
    private static readonly List<IBuildPostProcessor> _registered = new();
    private static List<IBuildPostProcessor>? _discovered;
    private static readonly object _lock = new();

    /// <summary>Registers a processor programmatically (tests, host tools).</summary>
    public static void Register(IBuildPostProcessor processor)
    {
        lock (_lock)
        {
            if (_registered.Any(p => p.GetType() == processor.GetType())) return;
            _registered.Add(processor);
            _discovered = null; // force re-snapshot
        }
    }

    /// <summary>Discovers IBuildPostProcessor implementations across loaded assemblies.</summary>
    public static List<IBuildPostProcessor> Discover()
    {
        lock (_lock)
        {
            if (_discovered is not null) return _discovered;

            var found = new List<IBuildPostProcessor>(_registered);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.IsAbstract || !typeof(IBuildPostProcessor).IsAssignableFrom(t)) continue;
                    if (found.Any(p => p.GetType() == t)) continue;
                    try
                    {
                        if (Activator.CreateInstance(t) is IBuildPostProcessor proc) found.Add(proc);
                    }
                    catch
                    {
                        // A processor that cannot construct must not block builds; it simply
                        // does not participate. Discovery errors are not build errors.
                    }
                }
            }
            found.Sort((a, b) => string.CompareOrdinal(a.Owner, b.Owner));
            _discovered = found;
            return found;
        }
    }

    /// <summary>Runs every discovered processor. Returns aggregated errors; empty = success.</summary>
    public static List<string> RunAll(BuildPostContext context)
    {
        var errors = new List<string>();
        foreach (var proc in Discover())
        {
            try
            {
                var issues = proc.Process(context);
                if (issues is { Count: > 0 })
                    errors.AddRange(issues.Select(i => $"[{proc.Owner}] {i}"));
            }
            catch (Exception ex)
            {
                errors.Add($"[{proc.Owner}] post-processor threw: {ex.Message}");
            }
        }
        return errors;
    }
}
