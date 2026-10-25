// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>
/// Sorts child <see cref="SpriteRenderer"/>s as a group. The group's layer and order replace
/// each child's values when building the transparent sort key.
/// </summary>
[AddComponentMenu("Rendering/Sorting Group")]
[ComponentIcon("\uf0c9")]
public sealed class SortingGroup : MonoBehaviour
{
    public int SortingLayer;
    public int SortingOrder;
}

/// <summary>Resolves the effective 2D sort layer/order, honoring an ancestor <see cref="SortingGroup"/>.</summary>
public static class SpriteSorting
{
    public static void Resolve(SpriteRenderer renderer, out int sortingLayer, out int sortingOrder)
    {
        if (renderer == null)
        {
            sortingLayer = 0;
            sortingOrder = 0;
            return;
        }

        SortingGroup? group = renderer.GetComponentInParent<SortingGroup>(includeSelf: true);
        if (group != null)
        {
            sortingLayer = group.SortingLayer;
            sortingOrder = group.SortingOrder;
            return;
        }

        sortingLayer = renderer.SortingLayer;
        sortingOrder = renderer.SortingOrder;
    }
}
