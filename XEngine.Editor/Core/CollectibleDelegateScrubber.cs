// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Async.Internal;
using XEngine.Runtime;

namespace XEngine.Editor.Core;

/// <summary>
/// Hot-reload hygiene for static events and static delegate fields.
///
/// Field-like events (e.g. <see cref="Runtime.Window.Update"/>, <see cref="Runtime.Input.OnKeyEvent"/>,
/// <see cref="Runtime.Debug.OnLog"/>) in long-lived engine assemblies hold their subscribers alive.
/// A user script that subscribes and never unsubscribes pins the script AssemblyLoadContext, so the
/// unload verification in <c>ScriptAssemblyManager</c> fails and the editor restarts on every reload.
///
/// This scrubber runs once per script unload and strips every static delegate entry whose method or
/// target belongs to a collectible assembly. It is scoped to XEngine's own assemblies (engine,
/// editor, and the vendored ThirdParty libraries): the leak class it fixes lives there, and walking
/// framework/test-host internals would be both slower and riskier. Anything that must survive a
/// reload has to be re-registered via <see cref="OnAssemblyLoadAttribute"/>.
/// </summary>
public static class CollectibleDelegateScrubber
{
    [OnAssemblyUnload]
    internal static void Scrub()
    {
        try
        {
            (int fields, int delegates) = ScrubCore();
            if (delegates > 0)
            {
                Runtime.Debug.Log($"[CollectibleDelegateScrubber] Removed {delegates} static event/delegate subscription(s) bound to unloaded script assemblies across {fields} field(s).");
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"[CollectibleDelegateScrubber] Scrub failed: {ex.Message}");
        }
    }

    private static (int fields, int delegates) ScrubCore()
    {
        int scrubbedFields = 0;
        int removedDelegates = 0;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Collectible assemblies die with their context; dynamic ones can't be reflected safely.
            if (assembly.IsCollectible || assembly.IsDynamic || !IsEngineAssembly(assembly))
            {
                continue;
            }

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = Array.FindAll(ex.Types, static t => t != null)!; }
            catch { continue; }

            foreach (Type type in types)
            {
                FieldInfo[] fields;
                try { fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (FieldInfo field in fields)
                {
                    if (!typeof(Delegate).IsAssignableFrom(field.FieldType))
                    {
                        continue;
                    }

                    Delegate? current;
                    try { current = field.GetValue(null) as Delegate; }
                    catch { continue; }
                    if (current == null)
                    {
                        continue;
                    }

                    int removed;
                    Delegate? cleaned;
                    try { (cleaned, removed) = StripCollectible(current); }
                    catch { continue; }

                    if (removed == 0)
                    {
                        continue;
                    }

                    try
                    {
                        field.SetValue(null, cleaned);
                        scrubbedFields++;
                        removedDelegates += removed;
                    }
                    catch
                    {
                        // Init-only or otherwise protected field; leave it alone.
                    }
                }
            }
        }

        return (scrubbedFields, removedDelegates);
    }

    private static (Delegate? cleaned, int removed) StripCollectible(Delegate @delegate)
    {
        Delegate?[] list = @delegate.GetInvocationList();
        int removed = 0;

        Delegate? cleaned = null;
        foreach (Delegate entry in list)
        {
            if (IsCollectibleBound(entry))
            {
                removed++;
                continue;
            }
            cleaned = cleaned == null ? entry : Delegate.Combine(cleaned, entry);
        }

        // Signal "nothing changed" by returning the original instance when no entry was removed.
        return removed == 0 ? (@delegate, 0) : (cleaned, removed);
    }

    /// <summary>A delegate entry pins a collectible assembly when its method is declared there,
    /// its target instance lives there (e.g. an instance method of a user MonoBehaviour), or its
    /// target is an engine object instantiated over collectible types (e.g. a pooled async runner).
    /// Detection is shared with the XTask frame loop via <see cref="CollectibleBinding"/>.</summary>
    internal static bool IsCollectibleBound(Delegate entry) => CollectibleBinding.IsBound(entry);

    private static bool IsEngineAssembly(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? "";
        return name.StartsWith("XEngine.", StringComparison.Ordinal)
            || s_vendoredAssemblies.Contains(name);
    }

    private static readonly HashSet<string> s_vendoredAssemblies = new(StringComparer.Ordinal)
    {
        // ThirdParty libraries that host engine-facing static events/caches.
        "Echo", "Quill", "Rosetta", "Scribe", "Vector", "XEngine.Slang", "Unwrapper",
    };
}
