// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Public layout rebuild API. Marks a RectTransform subtree dirty and can force the owning
/// Canvas to rebuild immediately so fitters and groups apply the latest preferred sizes.
/// </summary>
public static class LayoutRebuilder
{
    public static void MarkLayoutForRebuild(RectTransform? rect)
    {
        if (rect is null || rect.IsDisposed) return;

        GameObject current = rect.GameObject;
        GameCanvas? canvas = null;
        while (true)
        {
            foreach (MonoBehaviour component in current.GetComponents<MonoBehaviour>())
            {
                if (component is UIBehaviour ui && component.EnabledInHierarchy)
                    ui.MarkDirty(UIDirtyFlags.Layout);
                if (component is GameCanvas found)
                    canvas = found;
            }

            GameObject? parent = current.Parent;
            if (parent is null) break;
            current = parent;
        }

        canvas?.MarkDirty(UIDirtyFlags.Layout);
    }

    public static void ForceRebuildLayoutImmediate(RectTransform? rect)
    {
        MarkLayoutForRebuild(rect);
        if (rect is null || rect.IsDisposed) return;
        rect.GameObject.GetComponentInParent<GameCanvas>(includeSelf: true)?.RebuildIfDirty();
    }
}
