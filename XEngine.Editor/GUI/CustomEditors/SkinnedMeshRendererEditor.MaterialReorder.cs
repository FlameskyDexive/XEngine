using System;
using System.Collections.Generic;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Inspector;

public partial class SkinnedMeshRendererEditor
{
    // Paper rebuilds ElementData before declaration; keep the last completed layout,
    // matching the hover path used during the next declaration pass.
    private sealed class MaterialRowGeometry { internal float Y, Width, Height; }

    internal sealed class MaterialSlotDragPayload : DragPayload
    {
        internal readonly object[] Targets;
        internal readonly AssetRef<Material>[][] Lists;
        internal readonly int Slot;
        public override string DisplayName { get; }
        public override string Icon => EditorIcons.GripLines;

        private MaterialSlotDragPayload(object[] targets, AssetRef<Material>[][] lists, int slot)
        { Targets = targets; Lists = lists; Slot = slot; DisplayName = $"Material Element {slot}"; }

        internal static MaterialSlotDragPayload? Capture(IReadOnlyList<object> targets, int slot)
        {
            if (targets.Count == 0 || slot < 0) return null;
            for (int i = 0; i < targets.Count; i++)
                if (!IsLiveMaterialTarget(targets[i], out var renderer) || slot >= renderer.Materials.Count) return null;
            var owned = new object[targets.Count];
            var lists = new AssetRef<Material>[targets.Count][];
            for (int i = 0; i < targets.Count; i++)
            { owned[i] = targets[i]; lists[i] = ((SkinnedMeshRenderer)targets[i]).Materials.ToArray(); }
            return new MaterialSlotDragPayload(owned, lists, slot);
        }
    }

    private static void DrawMaterialReorderTarget(Paper paper, ElementBuilder row, SkinnedMeshRenderer first,
        IReadOnlyList<object>? targets, int slot)
    {
        var geometry = paper.GetElementStorage<MaterialRowGeometry?>(row._handle, "material_geometry", null);
        if (geometry == null)
        {
            geometry = new MaterialRowGeometry();
            paper.SetElementStorage(row._handle, "material_geometry", geometry);
        }
        row.OnPostLayout(geometry, static (cached, _, rect) =>
        { cached.Y = rect.Min.Y; cached.Width = rect.Size.X; cached.Height = rect.Size.Y; });
        if (DragDrop.Payload is not MaterialSlotDragPayload payload || !paper.IsElementHovered(row._handle.Data.ID)
            || !DragDrop.CanReceive(paper)) return;
        // Single-target validation uses the captured exact object without allocating each hover frame.
        bool sameTargets = targets != null ? MaterialTargetsMatch(payload, targets)
            : payload.Targets.Length == 1 && ReferenceEquals(payload.Targets[0], first);
        bool before = paper.PointerPos.Y < geometry.Y + geometry.Height / 2;
        int insertion = slot + (before ? 0 : 1);
        bool valid = !Origami.IsReadOnly && sameTargets && CanReorderMaterials(payload, insertion);
        if (DragDrop.IsDragging)
        {
            DragDrop.SetVisualMode(paper, valid ? DragVisualMode.Move : DragVisualMode.Rejected);
            if (valid)
                using (row.Enter())
                    paper.Box($"{row._handle.Data.ID}_insert").PositionType(PositionType.SelfDirected)
                        .Position(8, before ? 0 : geometry.Height - 2)
                        .Width(Math.Max(0, geometry.Width - 16)).Height(2)
                        .BackgroundColor(EditorTheme.Purple400).IsNotInteractable();
        }
        else if (DragDrop.IsDropFrameFor(paper))
        {
            if (valid) TryReorderMaterials(payload, insertion);
            DragDrop.EndDrag();
        }
    }

    private static bool MaterialTargetsMatch(MaterialSlotDragPayload payload, IReadOnlyList<object> targets)
    {
        if (payload.Targets.Length != targets.Count) return false;
        for (int i = 0; i < targets.Count; i++)
            if (!ReferenceEquals(payload.Targets[i], targets[i])) return false;
        return true;
    }

    private static bool CanReorderMaterials(MaterialSlotDragPayload payload, int insertion)
    {
        if (insertion < 0) return false;
        for (int i = 0; i < payload.Targets.Length; i++)
        {
            if (!IsLiveMaterialTarget(payload.Targets[i], out var renderer)) return false;
            var saved = payload.Lists[i];
            if (renderer.Materials.Count != saved.Length || payload.Slot >= saved.Length || insertion > saved.Length) return false;
            for (int j = 0; j < saved.Length; j++)
                if (renderer.Materials[j] != saved[j]) return false;
        }
        return true;
    }

    internal static bool TryReorderMaterials(MaterialSlotDragPayload payload, int insertion)
    {
        if (!CanReorderMaterials(payload, insertion)) return false;
        int destination = insertion > payload.Slot ? insertion - 1 : insertion;
        if (destination == payload.Slot) return true;
        var actions = new List<(Action undo, Action redo)>(payload.Targets.Length);
        for (int i = 0; i < payload.Targets.Length; i++)
        {
            var previous = payload.Lists[i];
            var next = (AssetRef<Material>[])previous.Clone();
            var moved = next[payload.Slot];
            if (destination > payload.Slot) Array.Copy(next, payload.Slot + 1, next, payload.Slot, destination - payload.Slot);
            else Array.Copy(next, destination, next, destination + 1, payload.Slot - destination);
            next[destination] = moved;
            bool changed = false;
            for (int j = 0; j < next.Length; j++) changed |= next[j] != previous[j];
            if (!changed) continue;
            var componentId = ((SkinnedMeshRenderer)payload.Targets[i]).Identifier;
            actions.Add((() => ApplyMaterialList(componentId, previous), () => ApplyMaterialList(componentId, next)));
            ApplyMaterialList(componentId, next);
        }
        if (actions.Count != 0)
        {
            Undo.RegisterActionGroup("Reorder Materials", actions);
            EditorSceneManager.MarkDirty("Reorder Materials", "editor");
        }
        return true;
    }
}
