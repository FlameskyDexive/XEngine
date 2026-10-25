// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Linq;
using System.Reflection;

using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.Prefabs;
using XEngine.Editor.Theming;
using XEngine.Editor.Utils;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Rosetta;
using XEngine.Runtime;

namespace XEngine.Editor.GUI;

/// <summary>
/// Builds the field-row context menu for the property grid: Copy/Paste value, per-field prefab
/// Apply/Revert, and multi-object "Set to Value of X". Registered on the editor's
/// PropertyGridConfig (<see cref="PropertyGridConfig.OnFieldContextMenu"/>).
/// </summary>
public static class FieldContextMenu
{
    private const string ValueHeader = "XEngineFieldValue:";

    public static void Build(Paper paper, ContextBuilder menu, FieldContextMenuRequest req)
    {
        var memberType = req.Member switch
        {
            FieldInfo f => f.FieldType,
            PropertyInfo p => p.PropertyType,
            _ => req.GetValue()?.GetType(),
        };

        // ── Copy / Paste value ──
        menu.Item(Loc.Get("inspector.copy_value"), () => CopyValue(req, memberType),
            icon: EditorIcons.Copy, enabled: memberType != null);
        menu.Item(Loc.Get("inspector.paste_value"), () => PasteValue(req, memberType),
            icon: EditorIcons.Paste, enabled: memberType != null && CanPasteValue(memberType));

        // ── Prefab overrides: per-field Apply / Revert ──
        if (req.IsOverridden && req.Targets[0] is MonoBehaviour comp)
        {
            var go = comp.GameObject;
            if (go != null && go.IsPrefabInstance)
            {
                var overrideHost = PrefabUtility.GetPrefabInstanceRoot(go) ?? go;
                string goPath = PrefabUtility.BuildGOPath(go);
                int compIdx = go._components.IndexOf(comp);
                string prefix = string.IsNullOrEmpty(goPath) ? $"c{compIdx}." : $"{goPath}.c{compIdx}.";
                string fullPath = prefix + req.FieldName;

                var ov = overrideHost.PrefabOverrides.FirstOrDefault(o => o.Path == fullPath);
                if (ov != null)
                {
                    menu.Separator();
                    menu.Item(Loc.Get("inspector.apply"), () => PrefabUtility.ApplySingleOverride(overrideHost, ov));
                    menu.Item(Loc.Get("inspector.revert"), () => PrefabUtility.RevertSingleOverride(overrideHost, fullPath));
                }
            }
        }

        // ── Multi-object: propagate one target's value to all ──
        if (req.Targets.Count > 1 && req.Member is FieldInfo)
        {
            menu.Separator();
            for (int i = 0; i < req.Targets.Count && i < 12; i++)
            {
                int idx = i;
                string name = TargetDisplayName(req.Targets[i]);
                menu.Item(Loc.Get("inspector.set_to_value_of", new { name }), () =>
                {
                    var value = (req.Member as FieldInfo)?.GetValue(req.Targets[idx]);
                    req.SetValue(value);
                });
            }
        }
    }

    private static string TargetDisplayName(object target) => target switch
    {
        Runtime.EngineObject eo => eo.Name,
        _ => target.GetType().Name,
    };

    // ── Clipboard payload: XEngineFieldValue:{type full name}\n{Echo text} ──

    private static void CopyValue(FieldContextMenuRequest req, Type? fieldType)
    {
        var value = req.GetValue();
        var type = value?.GetType() ?? fieldType;
        if (type == null) return;

        var echo = Serializer.Serialize(type, value);
        if (echo == null) return;
        Input.Clipboard = ValueHeader + type.AssemblyQualifiedName + "\n" + echo.WriteToString();
    }

    private static bool CanPasteValue(Type fieldType)
    {
        return TryReadValue(out var type, out _) && fieldType.IsAssignableFrom(type);
    }

    private static void PasteValue(FieldContextMenuRequest req, Type? fieldType)
    {
        if (fieldType == null || !TryReadValue(out var type, out var echo)) return;
        if (!fieldType.IsAssignableFrom(type)) return;

        object? value;
        try { value = Serializer.Deserialize(echo, fieldType); }
        catch (Exception ex) { Debug.LogWarning($"Paste value failed: {ex.Message}"); return; }
        if (value == null) return;

        req.SetValue(value);
        foreach (var t in req.Targets)
            (t as Runtime.EngineObject)?.OnValidate();
    }

    private static bool TryReadValue(out Type type, out EchoObject echo)
    {
        type = null!;
        echo = null!;

        string? text = Input.Clipboard;
        if (string.IsNullOrEmpty(text) || !text.StartsWith(ValueHeader, StringComparison.Ordinal)) return false;

        int newline = text.IndexOf('\n', ValueHeader.Length);
        if (newline < 0) return false;

        string typeName = text[ValueHeader.Length..newline].Trim();
        type = Type.GetType(typeName)
               ?? EditorUtils.GetAllTypes().FirstOrDefault(t => t.AssemblyQualifiedName == typeName);
        if (type == null) return false;

        try { echo = EchoObject.ReadFromString(text[(newline + 1)..]); }
        catch { return false; }
        return echo != null;
    }
}
