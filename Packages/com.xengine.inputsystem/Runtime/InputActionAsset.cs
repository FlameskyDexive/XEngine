// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.InputSystem;

/// <summary>
/// Multi-map input asset mirroring Unity's InputActionAsset (zonezero M4): a named set of
/// engine <see cref="InputActionMap"/>s plus the control schemes declared by the source
/// Unity asset. Maps resolve as sub-asset references so each one stays a standalone,
/// registrable map on the engine's Input layer.
/// </summary>
public class InputActionAsset : EngineObject
{
    [SerializeField] private List<AssetRef<InputActionMap>> _maps = new();
    [SerializeField] private List<string> _controlSchemes = new();

    public IReadOnlyList<AssetRef<InputActionMap>> Maps => _maps;
    public IReadOnlyList<string> ControlSchemes => _controlSchemes;

    public void AddMap(InputActionMap map)
    {
        EnsureNotDisposed();
        if (map == null) throw new ArgumentNullException(nameof(map));
        _maps.Add(new AssetRef<InputActionMap>(map));
    }

    public void AddControlScheme(string scheme)
    {
        EnsureNotDisposed();
        if (!string.IsNullOrEmpty(scheme) && !_controlSchemes.Contains(scheme))
            _controlSchemes.Add(scheme);
    }

    /// <summary>Resolves a map by name (resolved live through the asset database).</summary>
    public InputActionMap? FindMap(string mapName)
    {
        for (int i = 0; i < _maps.Count; i++)
        {
            // PlayerInput binds once during scene initialization. Loading the parent asset
            // does not load its sub-asset maps when asynchronous asset loading is enabled.
            AssetRef<InputActionMap> reference = _maps[i];
            reference.EnsureLoaded();
            _maps[i] = reference;
            InputActionMap? map = reference.Res;
            if (map != null && string.Equals(map.Name, mapName, StringComparison.OrdinalIgnoreCase))
                return map;
        }
        return null;
    }

    /// <summary>Resolves an action as map/action (Unity's asset.FindAction("Player/Move")).</summary>
    public InputAction? FindAction(string mapAndAction)
    {
        int slash = mapAndAction.IndexOf('/');
        if (slash < 0) return null;
        return FindMap(mapAndAction[..slash])?.FindAction(mapAndAction[(slash + 1)..]);
    }

    /// <summary>Registers every contained map with the engine Input layer (without enabling).</summary>
    public void RegisterAll()
    {
        foreach (AssetRef<InputActionMap> reference in _maps)
        {
            InputActionMap? map = reference.Res;
            if (map != null) Input.RegisterActionMap(map);
        }
    }

    public void UnregisterAll()
    {
        foreach (AssetRef<InputActionMap> reference in _maps)
        {
            InputActionMap? map = reference.Res;
            if (map != null) Input.UnregisterActionMap(map);
        }
    }

    public void EnableAll()
    {
        foreach (AssetRef<InputActionMap> reference in _maps)
            reference.Res?.Enable();
    }

    public void DisableAll()
    {
        foreach (AssetRef<InputActionMap> reference in _maps)
            reference.Res?.Disable();
    }
}
