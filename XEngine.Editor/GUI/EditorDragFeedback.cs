using System;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.GUI;

/// <summary>Pure compatibility feedback. Mutations remain in the existing drop handlers.</summary>
internal static class EditorDragFeedback
{
    internal static DragVisualMode HierarchyAssetMode(Type? type) => type == typeof(Scene) ? DragVisualMode.Link
        : type == typeof(Mesh) || type == typeof(Sprite) || type == typeof(PrefabAsset) ? DragVisualMode.Copy : DragVisualMode.Rejected;

    internal static void Reference(Paper paper, Type fieldType)
    {
        if (DragDrop.IsDragging && paper.IsParentHovered)
            DragDrop.SetVisualMode(paper, CompatibleReference(DragDrop.Payload, fieldType)
                ? DragVisualMode.Link : DragVisualMode.Rejected);
    }

    internal static bool CompatibleReference(DragPayload? payload, Type fieldType) => payload switch
    {
        AssetDragPayload asset => asset.AssetType != null && fieldType.IsAssignableFrom(asset.AssetType),
        ComponentDragPayload component => fieldType.IsAssignableFrom(component.Component.GetType()),
        GameObjectDragPayload objects when objects.GameObjects.Length > 0 =>
            typeof(GameObject).IsAssignableFrom(fieldType) && fieldType.IsInstanceOfType(objects.GameObjects[0]) || fieldType == typeof(Transform)
            || typeof(MonoBehaviour).IsAssignableFrom(fieldType) && objects.GameObjects[0].GetComponent(fieldType) != null,
        _ => false
    };

    internal static bool CanHierarchyDrop(GameObjectDragPayload payload, GameObject? target, bool into)
    {
        if (into && target is { IsPrefabInstance: true, PrefabChildCount: >= 0 }) return false;
        for (int i = 0; i < payload.GameObjects.Length; i++)
        {
            var dragged = payload.GameObjects[i];
            if (!dragged.IsValid() || ReferenceEquals(dragged, target)) continue;
            bool cycle = false;
            for (var parent = target?.Parent; parent != null && parent.IsValid(); parent = parent.Parent)
                if (ReferenceEquals(parent, dragged)) { cycle = true; break; }
            if (cycle) continue;
            if (dragged.Parent is { IsPrefabInstance: true, PrefabChildCount: >= 0 } prefab
                && prefab.Children.IndexOf(dragged) is var index && index >= 0 && index < prefab.PrefabChildCount) continue;
            return true;
        }
        return false;
    }
}
