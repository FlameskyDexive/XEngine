// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;

namespace XEngine.Runtime.UI;

public class CanvasGroup : UIBehaviour
{
    /// <summary>Opacity multiplier applied to all child elements (0 = transparent, 1 = opaque).</summary>
    [SerializeField] private float _alpha = 1f;
    public float Alpha
    {
        get => _alpha;
        set
        {
            // Alpha is published as a per-renderer material property. Do not dirty descendant
            // vertices: text shaping and local meshes remain valid when only opacity changes.
            SetField(ref _alpha, value, UIDirtyFlags.Material);
        }
    }

    /// <summary>When <c>false</c>, disables pointer interaction for all descendants.</summary>
    [SerializeField] private bool _interactable = true;
    public bool Interactable
    {
        get => _interactable;
        set
        {
            // Descendant Selectables read this through Selectable.IsInteractable(), but nothing
            // notifies them when it flips - so push a visual refresh across the subtree so they
            // grey out (or come back) immediately.
            if (SetField(ref _interactable, value, UIDirtyFlags.Hierarchy))
                RefreshDescendantSelectables();
        }
    }

    private void RefreshDescendantSelectables()
    {
        Walk(GameObject, isRoot: true);

        static void Walk(GameObject go, bool isRoot)
        {
            if (!isRoot)
            {
                if (go.GetComponent<GameCanvas>() != null) return;
                CanvasGroup? nested = go.GetComponent<CanvasGroup>();
                if (nested is { IgnoreParentGroups: true }) return;
            }

            foreach (Selectable s in go.GetComponents<Selectable>())
                s.RefreshInteractable();

            foreach (GameObject child in go.Children)
                Walk(child, isRoot: false);
        }
    }

    /// <summary>When <c>false</c>, raycasts pass through all descendant elements.</summary>
    [SerializeField] private bool _blocksRaycasts = true;
    public bool BlocksRaycasts
    {
        get => _blocksRaycasts;
        set => SetField(ref _blocksRaycasts, value, UIDirtyFlags.Hierarchy);
    }

    /// <summary>When <c>true</c>, this group and its children ignore parent <see cref="CanvasGroup"/> settings.</summary>
    [SerializeField] private bool _ignoreParentGroups = false;
    public bool IgnoreParentGroups
    {
        get => _ignoreParentGroups;
        set
        {
            // This changes the inherited material context, not local geometry.
            SetField(ref _ignoreParentGroups, value, UIDirtyFlags.Material);
        }
    }

    /// <summary>Applies this group's settings to the given context.</summary>
    internal UIContext ApplyTo(UIContext parent)
    {
        UIContext ctx = IgnoreParentGroups ? UIContext.Default : parent;
        ctx.Alpha *= Alpha;
        ctx.Interactable = ctx.Interactable && Interactable;
        ctx.BlocksRaycasts = ctx.BlocksRaycasts && BlocksRaycasts;
        return ctx;
    }

    /// <summary>CanvasGroup itself does not emit visual elements.</summary>
    public override void GenerateMesh(UIMeshBuilder _, in UIContext __) { /* no geometry */ }
}
