// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;

namespace XEngine.InputSystem;

/// <summary>
/// Unity Input System polling-compat surface (zonezero M4). The ZZZ demo scripts read
/// `action.triggered` / `action.IsPressed()` / `action.ReadValue&lt;T&gt;()` — these extensions
/// give ported code the same shape on top of the engine's InputAction.
/// </summary>
public static class InputActionExtensions
{
    /// <summary>Unity's `triggered`: the action performed this frame (press with the default
    /// interaction). Game scripts poll this once per Update.</summary>
    public static bool Triggered(this InputAction action) => action.WasPressedThisFrame();

    /// <summary>Unity naming alias for <see cref="InputAction.WasPressedThisFrame"/>.</summary>
    public static bool WasPerformedThisFrame(this InputAction action) => action.WasPressedThisFrame();
}
