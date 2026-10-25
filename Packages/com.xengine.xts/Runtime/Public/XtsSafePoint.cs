using System.Linq;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace XEngine.XTS;

/// <summary>
/// Safe-point rebuild (plan §4): rebuild the shared script environment at a
/// safe point — export every live behaviour's state, create a replacement
/// environment alongside the old one, re-create + re-import there, health
/// check, and only then swap (the old environment is disposed last, so late
/// callbacks of the old domain die with it and can never touch the new
/// domain). Any failure keeps the old environment alive and returns the
/// errors — no half-migrated state is ever visible.
/// </summary>
public static class XtsSafePoint
{
    public sealed class Result
    {
        public bool Rebuilt { get; init; }
        /// <summary>Errors that aborted the rebuild (empty on success).</summary>
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        /// <summary>The environment now backing behaviours (new on success, old on failure).</summary>
        public XtsRuntime? Runtime { get; init; }
    }

    /// <summary>Rebuild the shared environment carrying these behaviours over.
    /// <paramref name="scripts"/> are the script sources (bundles) re-loaded
    /// into the new environment before the behaviours are re-created.</summary>
    public static Result Rebuild(IReadOnlyList<XtsBehaviour> behaviours, IEnumerable<string> scripts)
    {
        var errors = new List<string>();
        var old = XtsSharedRuntime.Current;
        if (old == null)
            return new Result { Rebuilt = false, Errors = new[] { "no live script environment" } };

        // 1. export state from the old domain
        var snapshots = new List<(XtsBehaviour Behaviour, string StateJson, int OldHandle)>();
        foreach (var behaviour in behaviours)
        {
            if (!behaviour.IsScriptAlive)
                continue;
            snapshots.Add((behaviour, behaviour.ExportStateJson(), behaviour.Handle));
        }

        // 2. build the replacement alongside the old domain and re-load the
        //    script content into it
        var replacement = XtsSharedRuntime.CreateReplacement();
        foreach (var script in scripts)
        {
            var status = replacement.EvaluateExpression(script, out _);
            if (status != XtsStatus.Ok)
                errors.Add($"script failed to load into the replacement environment ({status}): {replacement.LastError}");
        }

        foreach (var (behaviour, stateJson, _) in snapshots)
        {
            // destroyExisting=false: the old-domain instance stays alive until
            // the swap commits, so an aborted rebuild loses nothing
            var create = behaviour.CreateScriptIn(replacement, importStateJson: stateJson, destroyExisting: false);
            if (create != null)
            {
                errors.Add($"{behaviour.ScriptModule}:{behaviour.BehaviourName}: {create}");
                continue;
            }
            var health = replacement.EvaluateExpression(
                $"__xts_bhv_alive({behaviour.Handle})", out var alive);
            if (health != XtsStatus.Ok || alive.Kind != XtsValueKind.Bool || !alive.AsBool())
            {
                errors.Add($"{behaviour.ScriptModule}:{behaviour.BehaviourName}: health check failed after migration");
                continue;
            }
        }

        // 3. commit only when every behaviour migrated; otherwise keep old domain
        if (errors.Count > 0)
        {
            // old handles are still valid — the old environment was untouched
            foreach (var (behaviour, _, oldHandle) in snapshots)
                behaviour.RestoreHandle(oldHandle);
            replacement.Dispose();
            return new Result { Rebuilt = false, Errors = errors, Runtime = old };
        }

        var replaced = XtsSharedRuntime.SwapTo(replacement);
        replaced?.Dispose(); // old domain dies here: its late callbacks die with it
        return new Result { Rebuilt = true, Runtime = replacement };
    }
}
