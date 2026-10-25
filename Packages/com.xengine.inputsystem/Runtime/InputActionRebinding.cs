// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text.Json;

using XEngine.Runtime;

namespace XEngine.InputSystem;

/// <summary>
/// Interactive rebinding + override persistence (zonezero M4 — Unity's
/// PerformInteractiveRebinding / SaveBindingOverridesAsJson equivalents, sized to what the
/// demo needs: single-device keyboard/mouse/gamepad-button overrides). The ZZZ project itself
/// ships no rebinding; the plan requires the capability on the engine side.
/// </summary>
public static class InputActionRebinding
{
    /// <summary>Rebinds the binding at <paramref name="bindingIndex"/> to a keyboard key.</summary>
    public static bool RebindToKey(this InputAction action, int bindingIndex, KeyCode key)
    {
        if (!TryGetBindingSlot(action, bindingIndex, out InputActionMap? _, out _))
            return false;
        action.Bindings[bindingIndex] = InputBinding.CreateKeyBinding(key);
        return true;
    }

    /// <summary>Rebinds the binding at <paramref name="bindingIndex"/> to a mouse button.</summary>
    public static bool RebindToMouseButton(this InputAction action, int bindingIndex, MouseButton button)
    {
        if (!TryGetBindingSlot(action, bindingIndex, out _, out _))
            return false;
        action.Bindings[bindingIndex] = InputBinding.CreateMouseButtonBinding(button);
        return true;
    }

    /// <summary>
    /// Unity's PerformInteractiveRebinding (simplified): waits for the next key press and
    /// applies it as the override. Hooking <see cref="Input.OnKeyEvent"/> keeps this device
    /// driven — real hardware or a pushed test handler both work.
    /// </summary>
    public static void PerformInteractiveRebinding(this InputAction action, int bindingIndex,
        Action<KeyCode?> onComplete)
    {
        void Handler(KeyCode key, bool down)
        {
            if (!down) return;
            Input.OnKeyEvent -= Handler;
            if (key != KeyCode.Escape)
                action.Bindings[bindingIndex] = InputBinding.CreateKeyBinding(key);
            onComplete(key == KeyCode.Escape ? null : key);
        }
        Input.OnKeyEvent += Handler;
    }

    /// <summary>
    /// Serializes every current keyboard/mouse binding as a JSON override table
    /// (map/action/index/kind/code). Composites and axis bindings are skipped — rebinding
    /// targets the discrete bindings a player remaps.
    /// </summary>
    public static string SaveBindingOverridesAsJson(this InputActionAsset asset)
    {
        var entries = new List<BindingOverrideEntry>();
        foreach (AssetRef<InputActionMap> mapReference in asset.Maps)
        {
            InputActionMap? map = mapReference.Res;
            if (map == null) continue;
            foreach (InputAction action in map.Actions)
            {
                for (int i = 0; i < action.Bindings.Count; i++)
                {
                    InputBinding binding = action.Bindings[i];
                    entries.Add(new BindingOverrideEntry
                    {
                        Map = map.Name,
                        Action = action.Name,
                        Index = i,
                        Kind = binding.BindingType.ToString(),
                        Code = binding.Key?.ToString()
                            ?? binding.MouseButton?.ToString()
                            ?? binding.GamepadButton?.ToString()
                            ?? "",
                    });
                }
            }
        }
        return JsonSerializer.Serialize(entries);
    }

    /// <summary>Applies a JSON override table produced by <see cref="SaveBindingOverridesAsJson"/>.</summary>
    public static void LoadBindingOverridesFromJson(this InputActionAsset asset, string json)
    {
        BindingOverrideEntry[]? entries = JsonSerializer.Deserialize<BindingOverrideEntry[]>(json);
        if (entries == null) return;
        foreach (BindingOverrideEntry entry in entries)
        {
            InputAction? action = asset.FindMap(entry.Map)?.FindAction(entry.Action);
            if (action == null || entry.Index < 0 || entry.Index >= action.Bindings.Count) continue;

            action.Bindings[entry.Index] = entry.Kind switch
            {
                nameof(InputBindingType.Key) when Enum.TryParse(entry.Code, out KeyCode key) =>
                    InputBinding.CreateKeyBinding(key),
                nameof(InputBindingType.MouseButton) when Enum.TryParse(entry.Code, out MouseButton mouse) =>
                    InputBinding.CreateMouseButtonBinding(mouse),
                nameof(InputBindingType.GamepadButton) when Enum.TryParse(entry.Code, out GamepadButton pad) =>
                    InputBinding.CreateGamepadButtonBinding(pad),
                _ => action.Bindings[entry.Index],
            };
        }
    }

    private static bool TryGetBindingSlot(InputAction action, int bindingIndex,
        out InputActionMap? map, out int slot)
    {
        map = null;
        slot = bindingIndex;
        return action != null && bindingIndex >= 0 && bindingIndex < action.Bindings.Count;
    }

    private sealed class BindingOverrideEntry
    {
        public string Map { get; set; } = "";
        public string Action { get; set; } = "";
        public int Index { get; set; }
        public string Kind { get; set; } = "";
        public string Code { get; set; } = "";
    }
}
