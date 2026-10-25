// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// Owns the four layer roots every screen is parented under.
/// </summary>
/// <remarks>
/// <para>
/// Ported from XPlay XUI's window-root model. The layers exist so "what can cover what" is a property
/// of the screen rather than a race between whoever opened last: a modal is always above the HUD
/// because it declared <c>layer="PopUp"</c>, not because of call order.
/// </para>
/// <para>
/// Each layer owns a canvas sort-order band, and screens within a layer step up inside their band.
/// A band is wide enough that no realistic screen count in one layer can reach the next.
/// </para>
/// </remarks>
public sealed class XuiRoot : MonoBehaviour
{
    /// <summary>Sort orders reserved per layer. Screens step by one inside their band.</summary>
    public const int LayerBand = 1000;

    private readonly GameObject?[] _roots = new GameObject?[4];
    private readonly int[] _next = new int[4];

    /// <summary>The container screens of this layer are parented under, created on first use.</summary>
    public GameObject RootFor(XuiLayerKind layer)
    {
        int index = (int)layer;
        if (_roots[index] is { IsDisposed: false } existing) return existing;

        var go = new GameObject(layer.ToString() + "Root");
        go.SetParent(GameObject);
        go.EnsureRectTransform();
        _roots[index] = go;
        return go;
    }

    /// <summary>
    /// The next canvas sort order in a layer's band. Later screens in a layer cover earlier ones, and
    /// every screen in a higher layer covers every screen in a lower one.
    /// </summary>
    public int NextSortOrder(XuiLayerKind layer)
    {
        int index = (int)layer;
        int step = _next[index]++;
        return index * LayerBand + step;
    }

    /// <summary>Forgets the sort-order cursors. Called when the last screen of a layer closes.</summary>
    public void ResetOrder(XuiLayerKind layer) => _next[(int)layer] = 0;

    /// <summary>
    /// Finds this scene's root, creating it if absent. One per scene: a screen belongs to the scene
    /// that opened it, so a scene unload takes its UI with it.
    /// </summary>
    public static XuiRoot GetOrCreate(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        foreach (GameObject candidate in scene.RootObjects)
        {
            XuiRoot? found = candidate.GetComponent<XuiRoot>();
            if (found is { IsDisposed: false }) return found;
        }

        var host = new GameObject("XUI");
        host.EnsureRectTransform();
        XuiRoot root = host.AddComponent<XuiRoot>();
        scene.Add(host);
        return root;
    }
}
