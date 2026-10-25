// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.InputSystem;

/// <summary>
/// Component analog of Unity's PlayerInput (zonezero M4): resolves an
/// <see cref="InputActionAsset"/>, registers + enables the default action map with the engine
/// Input layer on scene add, and tears it down on removal/dispose. Game code polls through
/// <see cref="FindAction"/> (the ZZZ demo polls instead of using Unity's message dispatch).
/// </summary>
[AddComponentMenu("Input/Player Input")]
public sealed class PlayerInput : MonoBehaviour
{
    [SerializeField] private AssetRef<InputActionAsset>? _asset;
    [SerializeField] private string _defaultActionMap = "Player";

    private InputActionAsset? _resolved;
    private InputActionMap? _activeMap;

    /// <summary>The input asset to drive this component (project asset reference).</summary>
    public AssetRef<InputActionAsset>? Asset
    {
        get => _asset;
        set { _asset = value; _resolved = null; }
    }

    /// <summary>Map enabled on scene add ("Player" for the ZZZ demo asset).</summary>
    public string DefaultActionMap
    {
        get => _defaultActionMap;
        set => _defaultActionMap = value;
    }

    /// <summary>The currently enabled map (null until the component joins a scene).</summary>
    public InputActionMap? Actions => _activeMap;

    public override void OnAddedToScene()
    {
        ResolveAndEnable();
    }

    public override void OnRemovedFromScene()
    {
        TearDown();
    }

    public override void OnDispose()
    {
        TearDown();
        base.OnDispose();
    }

    /// <summary>Looks an action up in the active map (Unity's playerInput.actions["Move"]).</summary>
    public InputAction? FindAction(string actionName) => _activeMap?.FindAction(actionName);

    /// <summary>Switches to a different map from the asset (disables the current one).</summary>
    public bool SwitchCurrentActionMap(string mapName)
    {
        InputActionAsset? asset = ResolveAsset();
        if (asset == null) return false;
        InputActionMap? map = asset.FindMap(mapName);
        if (map == null) return false;

        _activeMap?.Disable();
        Input.UnregisterActionMap(_activeMap);
        Input.RegisterActionMap(map);
        map.Enable();
        _activeMap = map;
        return true;
    }

    private void ResolveAndEnable()
    {
        InputActionAsset? asset = ResolveAsset();
        if (asset == null)
        {
            Debug.LogWarning("[PlayerInput] No InputActionAsset assigned — component is inert.");
            return;
        }
        InputActionMap? map = asset.FindMap(_defaultActionMap);
        if (map == null)
        {
            Debug.LogWarning($"[PlayerInput] Map '{_defaultActionMap}' not found in '{asset.Name}'.");
            return;
        }
        Input.RegisterActionMap(map);
        map.Enable();
        _activeMap = map;
    }

    private InputActionAsset? ResolveAsset()
    {
        if (_resolved != null && !_resolved.IsDisposed) return _resolved;
        if (_asset.HasValue)
        {
            // EnsureLoaded, not Res: with async loading enabled Res returns a transient null
            // while the asset streams in, and this component resolves exactly once at scene
            // add — a miss there leaves it permanently inert (every FindAction null).
            // Nullable<T> member calls operate on a copy, so load via a local and write back.
            AssetRef<InputActionAsset> assetRef = _asset.GetValueOrDefault();
            assetRef.EnsureLoaded();
            _asset = assetRef;
            _resolved = assetRef.Res;
        }
        else
        {
            _resolved = null;
        }
        return _resolved;
    }

    private void TearDown()
    {
        if (_activeMap == null) return;
        _activeMap.Disable();
        Input.UnregisterActionMap(_activeMap);
        _activeMap = null;
    }
}
