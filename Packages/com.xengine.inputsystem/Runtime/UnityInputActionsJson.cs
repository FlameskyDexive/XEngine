// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text.Json;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.InputSystem;

/// <summary>
/// Parses Unity's .inputactions JSON (InputSystem 1.x schema) into engine objects
/// (zonezero M4). Device paths translate onto the engine InputManagement layer:
/// keyboard/mouse/gamepad buttons and sticks become typed engine bindings; unsupported
/// devices (Joystick/XR/Touchscreen) are skipped with a warning — the ZZZ demo map only
/// needs Keyboard+Mouse and Gamepad.
/// </summary>
public static class UnityInputActionsJson
{
    /// <summary>Sniffs whether the text is Unity's JSON schema (vs the engine's Echo format).</summary>
    public static bool IsUnityJson(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.StartsWith('{') && trimmed.Contains("\"maps\"");
    }

    public static InputActionAsset Parse(string json, List<string> warnings)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        var asset = new InputActionAsset { Name = GetString(root, "name", "InputActions") };

        if (root.TryGetProperty("controlSchemes", out JsonElement schemes) && schemes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement scheme in schemes.EnumerateArray())
            {
                string? name = GetStringOrNull(scheme, "name");
                if (name != null) asset.AddControlScheme(name);
            }
        }

        if (!root.TryGetProperty("maps", out JsonElement maps) || maps.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("InputActions JSON has no 'maps' array.");
            return asset;
        }

        foreach (JsonElement mapElement in maps.EnumerateArray())
        {
            InputActionMap? map = ParseMap(mapElement, warnings);
            if (map != null) asset.AddMap(map);
        }
        return asset;
    }

    private static InputActionMap? ParseMap(JsonElement mapElement, List<string> warnings)
    {
        string mapName = GetString(mapElement, "name", "Map");
        var map = new InputActionMap { Name = mapName };

        // Pass 1: actions.
        if (mapElement.TryGetProperty("actions", out JsonElement actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement actionElement in actions.EnumerateArray())
            {
                string actionName = GetString(actionElement, "name", "Action");
                InputActionType type = ParseActionType(GetString(actionElement, "type", "Value"));
                InputAction action = map.AddAction(actionName, type);
                if (GetString(actionElement, "expectedControlType", "") == "Vector2")
                    action.ExpectedValueType = typeof(Float2);
            }
        }

        // Pass 2: bindings — composites consume their isPartOfComposite followers.
        if (!mapElement.TryGetProperty("bindings", out JsonElement bindings) ||
            bindings.ValueKind != JsonValueKind.Array)
            return map;

        List<JsonElement> pending = new();
        foreach (JsonElement bindingElement in bindings.EnumerateArray())
        {
            if (GetBool(bindingElement, "isPartOfComposite", false))
            {
                pending.Add(bindingElement);
                continue;
            }

            FlushComposite(map, pending, warnings);

            string actionName = GetString(bindingElement, "action", "");
            InputAction? action = map.FindAction(actionName);
            if (action == null)
            {
                warnings.Add($"[{mapName}] binding for unknown action '{actionName}' skipped.");
                continue;
            }
            if (GetBool(bindingElement, "isComposite", false))
            {
                pending.Add(bindingElement); // composite header — its parts follow
                continue;
            }

            string path = GetString(bindingElement, "path", "");
            if (path == "<Pointer>/delta")
            {
                // Look-style pointer motion: dual-axis composite of mouse deltas.
                InputCompositeBinding? delta = TranslatePointerDelta(warnings);
                if (delta != null) action.AddBinding(delta);
                continue;
            }
            InputBinding? engineBinding = TranslateSingle(path, warnings);
            if (engineBinding != null)
                action.AddBinding(engineBinding);
        }
        FlushComposite(map, pending, warnings);
        return map;
    }

    /// <summary>Builds a Vector2 composite from a 2DVector/Dpad composite header + parts.</summary>
    private static void FlushComposite(InputActionMap map, List<JsonElement> pending, List<string> warnings)
    {
        if (pending.Count == 0) return;
        JsonElement header = pending[0];
        pending.RemoveAt(0);

        string actionName = GetString(header, "action", "");
        InputAction? action = map.FindAction(actionName);
        if (action == null)
        {
            warnings.Add($"[{map.Name}] composite for unknown action '{actionName}' skipped.");
            pending.Clear();
            return;
        }

        InputBinding? up = null, down = null, left = null, right = null;
        foreach (JsonElement part in pending)
        {
            string partName = GetString(part, "name", "");
            InputBinding? binding = TranslateSingle(GetString(part, "path", ""), warnings);
            switch (partName)
            {
                case "up": up ??= binding; break;
                case "down": down ??= binding; break;
                case "left": left ??= binding; break;
                case "right": right ??= binding; break;
            }
        }
        pending.Clear();

        if (up != null && down != null && left != null && right != null)
        {
            // Unity's 2DVector normalize defaults off; the game code normalizes (ZZZ does).
            action.AddBinding(new Vector2CompositeBinding(up, down, left, right, normalize: false));
        }
        else
        {
            warnings.Add($"[{map.Name}] composite on '{actionName}' missing parts — skipped.");
        }
    }

    private static InputBinding? TranslateSingle(string path, List<string> warnings)
    {
        if (string.IsNullOrEmpty(path)) return null;

        int slash = path.IndexOf('/');
        if (slash <= 0 || !path.StartsWith('<'))
        {
            warnings.Add($"Binding path '{path}' is not a device path — skipped.");
            return null;
        }
        string device = path[..slash];
        string control = path[(slash + 1)..];
        // Strip trailing component (e.g. "<Touchscreen>/primaryTouch/tap").
        int sub = control.IndexOf('/');
        if (sub >= 0) control = control[..sub];

        switch (device)
        {
            case "<Keyboard>":
                KeyCode? key = TranslateKey(control);
                if (key == null)
                {
                    warnings.Add($"Keyboard control '{control}' has no engine KeyCode — skipped.");
                    return null;
                }
                return InputBinding.CreateKeyBinding(key.Value);

            case "<Mouse>":
                switch (control)
                {
                    case "leftButton": return InputBinding.CreateMouseButtonBinding(MouseButton.Left);
                    case "rightButton": return InputBinding.CreateMouseButtonBinding(MouseButton.Right);
                    case "middleButton": return InputBinding.CreateMouseButtonBinding(MouseButton.Middle);
                    case "scroll": return InputBinding.CreateMouseAxisBinding(2);
                    case "position":
                        warnings.Add("Mouse position bindings are not translated (UI-only) — skipped.");
                        return null;
                    default:
                        warnings.Add($"Mouse control '{control}' not translated — skipped.");
                        return null;
                }

            case "<Gamepad>":
                switch (control)
                {
                    case "leftStick": return InputBinding.CreateGamepadAxisBinding(0);
                    case "rightStick": return InputBinding.CreateGamepadAxisBinding(1);
                    case "leftTrigger": return InputBinding.CreateGamepadTriggerBinding(0);
                    case "rightTrigger": return InputBinding.CreateGamepadTriggerBinding(1);
                    case "dpad/up": return InputBinding.CreateGamepadButtonBinding(GamepadButton.DPadUp);
                    case "dpad/down": return InputBinding.CreateGamepadButtonBinding(GamepadButton.DPadDown);
                    case "dpad/left": return InputBinding.CreateGamepadButtonBinding(GamepadButton.DPadLeft);
                    case "dpad/right": return InputBinding.CreateGamepadButtonBinding(GamepadButton.DPadRight);
                    case "buttonSouth": return InputBinding.CreateGamepadButtonBinding(GamepadButton.A);
                    case "buttonWest": return InputBinding.CreateGamepadButtonBinding(GamepadButton.X);
                    case "buttonNorth": return InputBinding.CreateGamepadButtonBinding(GamepadButton.Y);
                    case "buttonEast": return InputBinding.CreateGamepadButtonBinding(GamepadButton.B);
                    case "leftShoulder": return InputBinding.CreateGamepadButtonBinding(GamepadButton.LeftBumper);
                    case "rightShoulder": return InputBinding.CreateGamepadButtonBinding(GamepadButton.RightBumper);
                    default:
                        warnings.Add($"Gamepad control '{control}' not translated — skipped.");
                        return null;
                }

            case "<Pointer>":
                // Pointer/delta is consumed as a DualAxis of mouse deltas (Look).
                if (control == "delta")
                    return null; // handled by the caller as a composite — see TranslatePointerDelta
                warnings.Add($"Pointer control '{control}' not translated — skipped.");
                return null;

            default:
                warnings.Add($"Device '{device}' not supported by the engine input layer — skipped.");
                return null;
        }
    }

    /// <summary>Pointer/delta as a dual-axis composite (engine has no single "delta" binding).</summary>
    public static InputCompositeBinding? TranslatePointerDelta(List<string> warnings)
    {
        // Mouse delta = x axis + y axis composite.
        return new DualAxisCompositeBinding(InputBinding.CreateMouseAxisBinding(0), InputBinding.CreateMouseAxisBinding(1));
    }

    /// <summary>Unity keyboard control names → engine KeyCode (letters/digits map 1:1).</summary>
    internal static KeyCode? TranslateKey(string control)
    {
        switch (control)
        {
            case "upArrow": return KeyCode.Up;
            case "downArrow": return KeyCode.Down;
            case "leftArrow": return KeyCode.Left;
            case "rightArrow": return KeyCode.Right;
            case "leftShift": return KeyCode.ShiftLeft;
            case "rightShift": return KeyCode.ShiftRight;
            case "leftCtrl": return KeyCode.ControlLeft;
            case "rightCtrl": return KeyCode.ControlRight;
            case "leftAlt": return KeyCode.AltLeft;
            case "rightAlt": return KeyCode.AltRight;
            case "escape": return KeyCode.Escape;
            case "enter": return KeyCode.Enter;
            case "tab": return KeyCode.Tab;
            case "backspace": return KeyCode.Backspace;
            case "space": return KeyCode.Space;
        }

        if (control.Length == 1)
        {
            char c = control[0];
            if (c is >= 'a' and <= 'z')
                return Enum.TryParse(((char)(c - 32)).ToString(), out KeyCode code) ? code : null;
            if (c is >= '0' and <= '9')
                return Enum.TryParse("Number" + c, out KeyCode code) ? code : null;
        }
        return Enum.TryParse(control, ignoreCase: true, out KeyCode named) ? named : null;
    }

    private static InputActionType ParseActionType(string type) => type switch
    {
        "Value" => InputActionType.Value,
        "PassThrough" => InputActionType.PassThrough,
        _ => InputActionType.Button,
    };

    private static string GetString(JsonElement element, string property, string fallback)
        => GetStringOrNull(element, property) ?? fallback;

    private static string? GetStringOrNull(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string property, bool fallback)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
