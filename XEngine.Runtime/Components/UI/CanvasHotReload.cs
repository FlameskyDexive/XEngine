// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.UI;

/// <summary>
/// Rebinds live Canvas hosts after script or package assembly reload. Unload hooks drop static
/// registries so collectible load contexts can unload; this walks still-active scenes and restores
/// Canvas, EventSystem, and raycast candidate registrations without waiting for OnEnable.
/// </summary>
public static class CanvasHotReload
{
    /// <summary>
    /// Re-registers enabled Canvas hosts and EventSystems in <paramref name="scene"/> after static
    /// UI registries were cleared. Marks each Canvas dirty so the next rebuild restores meshes.
    /// </summary>
    public static void Rebind(Scene scene)
    {
        if (scene == null || scene.IsDisposed || !scene.IsActive) return;

        GameCanvas?[] canvases = scene.FindObjectsOfType<GameCanvas>();
        for (int i = 0; i < canvases.Length; i++)
        {
            GameCanvas? canvas = canvases[i];
            if (canvas == null || canvas.IsDisposed || !canvas.EnabledInHierarchy) continue;
            CanvasRegistry.Register(canvas);
            canvas.MarkDirty(UIDirtyFlags.All);
            RebindRaycastTree(canvas.GameObject);
        }

        EventSystem.Rebind(scene);
    }

    [OnAssemblyLoad(Order = 0)]
    internal static void RebindCurrentScene()
    {
        Scene? scene = Scene.Current;
        if (scene != null) Rebind(scene);
    }

    private static void RebindRaycastTree(GameObject root)
    {
        foreach (UIBehaviour element in root.GetComponentsInChildren<UIBehaviour>(includeSelf: true, includeInactive: false))
            UIRaycastRegistry.Register(element);
    }
}
