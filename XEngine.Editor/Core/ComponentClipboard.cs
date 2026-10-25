// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using XEngine.Echo;
using XEngine.Editor.Inspector;
using XEngine.Editor.Utils;
using XEngine.OrigamiUI;
using XEngine.Rosetta;
using XEngine.Runtime;

namespace XEngine.Editor.Core;

/// <summary>
/// Copy / paste for a single component (Unity's Copy Component, Paste Component As New,
/// Paste Component Values). The payload is placed on the system clipboard as Echo text with a
/// header tag, mirroring <see cref="GameObjectClipboard"/>; fresh default values for Reset are
/// produced by instantiating a scratch instance of the component type.
/// </summary>
public static class ComponentClipboard
{
    private const string ClipboardHeader = "XEngineComponent:";

    /// <summary>Serialize the component to the system clipboard.</summary>
    public static void Copy(MonoBehaviour comp)
    {
        var echo = Serializer.Serialize(comp.GetType(), comp);
        if (echo == null) return;
        Input.Clipboard = ClipboardHeader + comp.GetType().AssemblyQualifiedName + "\n" + echo.WriteToString();
    }

    /// <summary>True when the clipboard holds a component whose type can be resolved.</summary>
    public static bool CanPasteAsNew => TryRead(out _, out _);

    /// <summary>True when the clipboard holds a component of exactly <paramref name="comp"/>'s type.</summary>
    public static bool CanPasteValues(MonoBehaviour comp)
        => TryRead(out var type, out _) && type == comp.GetType();

    /// <summary>
    /// Paste the clipboard component as a new instance onto <paramref name="go"/>
    /// (Paste Component As New). Undoable; returns the added component or null.
    /// </summary>
    public static MonoBehaviour? PasteAsNew(GameObject go)
    {
        if (!TryRead(out var type, out var echo)) return null;

        MonoBehaviour? instance;
        try { instance = Serializer.Deserialize(echo, type) as MonoBehaviour; }
        catch (Exception ex) { Runtime.Debug.LogWarning($"Paste Component As New failed: {ex.Message}"); return null; }
        if (instance == null) return null;

        // A pasted instance keeps a fresh identity rather than the copied component's id.
        instance.Identifier = Guid.NewGuid();
        return GameObjectInspector.AddComponentInstanceWithUndo(go, instance);
    }

    /// <summary>
    /// Apply the clipboard component's serialized values onto an existing component of the same
    /// type (Paste Component Values). Undoable.
    /// </summary>
    public static bool PasteValues(MonoBehaviour comp)
    {
        if (!TryRead(out var type, out var echo) || type != comp.GetType()) return false;

        MonoBehaviour? source;
        try { source = Serializer.Deserialize(echo, type) as MonoBehaviour; }
        catch (Exception ex) { Runtime.Debug.LogWarning($"Paste Component Values failed: {ex.Message}"); return false; }
        if (source == null) return false;

        return ApplyFieldsFrom(comp, source, Loc.Get("inspector.paste_component_values"));
    }

    /// <summary>
    /// Reset the component to fresh default values by instantiating a scratch instance of its
    /// type and copying the serialized fields over (undoable). Types without a parameterless
    /// constructor are reported and left untouched.
    /// </summary>
    public static bool Reset(MonoBehaviour comp)
    {
        var type = comp.GetType();
        if (type.GetConstructor(Type.EmptyTypes) == null)
        {
            Runtime.Debug.LogWarning($"[Inspector] Cannot reset {type.Name}: no parameterless constructor.");
            return false;
        }

        object? defaults;
        try { defaults = Activator.CreateInstance(type); }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"[Inspector] Reset of {type.Name} failed: {ex.Message}");
            return false;
        }
        if (defaults == null) return false;

        return ApplyFieldsFrom(comp, defaults, Loc.Get("inspector.reset"));
    }

    /// <summary>
    /// Copy every serializable field from <paramref name="source"/> onto <paramref name="target"/>,
    /// registering an Echo-snapshot undo step (before/after) under the given description.
    /// Fields named in <paramref name="exclude"/> are skipped (preset property exclusions).
    /// </summary>
    public static bool ApplyFieldsFrom(MonoBehaviour target, object source, string undoDescription, HashSet<string>? exclude = null)
    {
        var type = target.GetType();
        var before = Serializer.Serialize(type, target);
        var after = Serializer.Serialize(type, source);
        if (before == null || after == null) return false;

        var compId = target.Identifier;
        Undo.RegisterAction(undoDescription,
            undo: () =>
            {
                var c = Undo.FindComponent(compId);
                var restored = Serializer.Deserialize(before, type) as MonoBehaviour;
                if (c != null && restored != null) CopySerializableFields(restored, c, exclude);
            },
            redo: () =>
            {
                var c = Undo.FindComponent(compId);
                var applied = Serializer.Deserialize(after, type) as MonoBehaviour;
                if (c != null && applied != null) CopySerializableFields(applied, c, exclude);
            });

        CopySerializableFields(source, target, exclude);
        target.OnValidate();
        return true;
    }

    /// <summary>Copy all serializable fields (by name) between two instances of the same type.</summary>
    public static void CopySerializableFields(object source, MonoBehaviour target, HashSet<string>? exclude = null)
    {
        var type = target.GetType();
        foreach (var field in PropertyGridRenderer.GetSerializableFields(type))
        {
            if (exclude != null && exclude.Contains(field.Name)) continue;
            try { field.SetValue(target, field.GetValue(source)); }
            catch { /* a field that refuses assignment is skipped; the rest still apply */ }
        }
    }

    private static bool TryRead(out Type type, out EchoObject echo)
    {
        type = null!;
        echo = null!;

        string? text = Input.Clipboard;
        if (string.IsNullOrEmpty(text) || !text.StartsWith(ClipboardHeader, StringComparison.Ordinal)) return false;

        int newline = text.IndexOf('\n', ClipboardHeader.Length);
        if (newline < 0) return false;

        string typeName = text[ClipboardHeader.Length..newline].Trim();
        type = ResolveType(typeName);
        if (type == null) return false;

        try { echo = EchoObject.ReadFromString(text[(newline + 1)..]); }
        catch { return false; }
        return echo != null;
    }

    /// <summary>Resolve a type by assembly-qualified name, falling back to a scan across all
    /// loaded editor/script types (user script assemblies are not in the default ALC).</summary>
    private static Type? ResolveType(string typeName)
    {
        var direct = Type.GetType(typeName);
        if (direct != null) return direct;

        return Utils.EditorUtils.GetAllTypes().FirstOrDefault(t => t.AssemblyQualifiedName == typeName);
    }
}
