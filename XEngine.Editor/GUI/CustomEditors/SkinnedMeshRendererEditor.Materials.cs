using System;
using System.Collections.Generic;
using System.Globalization;
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
    internal const int MaxMaterialSlots = 1024;

    private static IReadOnlyList<object> MaterialTargets(SkinnedMeshRenderer first, IReadOnlyList<object>? targets)
        => targets ?? new object[] { first }; // Single-target array is created only by an action.

    private static void DrawMaterials(Paper paper, string id, SkinnedMeshRenderer first, IReadOnlyList<object>? targets)
    {
        var font = EditorTheme.DefaultFont!;
        float height = Origami.Current.Metrics.RowHeight;
        int common = first.Materials.Count, largest = common;
        if (targets != null)
            for (int i = 1; i < targets.Count; i++)
            {
                int count = ((SkinnedMeshRenderer)targets[i]).Materials.Count;
                common = Math.Min(common, count); largest = Math.Max(largest, count);
            }
        bool expanded;
        using (paper.Row($"{id}_header").Height(height).Padding(8, 8, 0, 0).RowBetween(4).Enter())
        {
            var fold = paper.Row($"{id}_fold").Width(UnitValue.Stretch()).Height(height).RowBetween(4);
            var handle = fold._handle;
            expanded = paper.GetElementStorage(handle, "expanded", true);
            fold.OnClick(expanded, (wasExpanded, _) => paper.SetElementStorage(handle, "expanded", !wasExpanded));
            using (fold.Enter())
            {
                paper.Box($"{id}_caret").Width(10).Height(height).IsNotInteractable()
                    .Text(expanded ? EditorIcons.CaretDown : EditorIcons.CaretRight, font)
                    .TextColor(EditorTheme.Ink300).FontSize(10).Alignment(TextAlignment.MiddleCenter);
                paper.Box($"{id}_label").Width(UnitValue.Stretch()).Height(height).IsNotInteractable()
                    .Text("Materials", font).TextColor(EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);
            }
            Origami.TextField(paper, $"{id}_size", common == largest ? common.ToString(CultureInfo.InvariantCulture) : "", text =>
            {
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                    TryResizeMaterials(MaterialTargets(first, targets), count);
            }).Width(44).Height(height).Placeholder(common != largest ? "—" : "")
                .Delayed().SelectAllOnFocus().IntFilter().MaxLength(4).Show();
        }
        if (!expanded) return;
        if (common != largest)
            paper.Box($"{id}_shared").Height(height).Padding(8, 8, 0, 0).Clip().IsNotInteractable()
                .Text($"Shared slots: {common}", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
        for (int index = 0; index < common; index++)
        {
            int slot = index;
            var value = first.Materials[slot];
            bool mixed = false;
            if (targets != null)
                for (int i = 1; i < targets.Count; i++)
                    mixed |= value != ((SkinnedMeshRenderer)targets[i]).Materials[slot];
            var row = s_assetField.Draw(paper, $"{id}_slot_{slot}", $"Element {slot}", value, next =>
            {
                if (next is AssetRef<Material> material)
                    TryAssignMaterialSlot(MaterialTargets(first, targets), slot, material);
            }, mixed, compact: true, onLabelDragStart: () =>
            {
                var payload = MaterialSlotDragPayload.Capture(MaterialTargets(first, targets), slot);
                if (payload != null) DragDrop.StartDrag(payload);
            });
            if (row != null) DrawMaterialReorderTarget(paper, row, first, targets, slot);
        }
        using (paper.Row($"{id}_footer").Height(height).Padding(8, 8, 0, 0).RowBetween(2).Enter())
        {
            paper.Box($"{id}_spacer").Width(UnitValue.Stretch()).Height(height).IsNotInteractable();
            MaterialListButton(paper, $"{id}_add", "+", height, largest < MaxMaterialSlots,
                () => TryResizeMaterials(MaterialTargets(first, targets), 1, relative: true));
            MaterialListButton(paper, $"{id}_remove", "−", height, common > 0,
                () => TryResizeMaterials(MaterialTargets(first, targets), -1, relative: true));
        }
    }

    private static void MaterialListButton(Paper paper, string id, string label, float height, bool enabled, Action action)
    {
        enabled &= !Origami.IsReadOnly;
        var button = paper.Box(id).Width(24).Height(height).Rounded(3).BackgroundColor(EditorTheme.Glass)
            .Text(label, EditorTheme.DefaultFont!).TextColor(enabled ? EditorTheme.Ink400 : EditorTheme.Ink200)
            .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter);
        if (enabled) button.Hovered.BackgroundColor(EditorTheme.Hover).End().OnClick(_ => action());
        else button.IsNotInteractable();
    }

    private static bool IsLiveMaterialTarget(object target, out SkinnedMeshRenderer renderer)
    {
        renderer = target as SkinnedMeshRenderer;
        return renderer != null && !renderer.IsDisposed && !renderer.GameObject.IsDisposed
            && ReferenceEquals(renderer.Scene, Scene.Current)
            && ReferenceEquals(Undo.FindComponent(renderer.Identifier), renderer) && renderer.Materials != null;
    }

    internal static bool TryAssignMaterialSlot(IReadOnlyList<object> targets, int slot, AssetRef<Material> material)
    {
        if (targets.Count == 0 || slot < 0 || (material.IsRuntimeResource && material.ResWeak == null)) return false;
        for (int i = 0; i < targets.Count; i++)
            if (!IsLiveMaterialTarget(targets[i], out var renderer) || slot >= renderer.Materials.Count) return false;
        var actions = new List<(Action undo, Action redo)>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            var renderer = (SkinnedMeshRenderer)targets[i];
            var previous = renderer.Materials[slot];
            if (previous == material) continue;
            var componentId = renderer.Identifier;
            actions.Add((() => ApplyMaterialSlot(componentId, slot, previous), () => ApplyMaterialSlot(componentId, slot, material)));
            renderer.Materials[slot] = material;
            renderer.OnValidate();
        }
        if (actions.Count != 0)
        {
            Undo.RegisterActionGroup("Set Material", actions);
            EditorSceneManager.MarkDirty("Set Material", "editor");
        }
        return true;
    }

    private static void ApplyMaterialSlot(Guid id, int slot, AssetRef<Material> material)
    {
        if (Undo.FindComponent(id) is not SkinnedMeshRenderer renderer || renderer.Materials == null
            || slot >= renderer.Materials.Count) return;
        renderer.Materials[slot] = material;
        renderer.OnValidate();
    }

    internal static bool TryResizeMaterials(IReadOnlyList<object> targets, int count, bool relative = false)
    {
        if (targets.Count == 0) return false;
        for (int i = 0; i < targets.Count; i++)
        {
            if (!IsLiveMaterialTarget(targets[i], out var renderer)) return false;
            long size = relative ? (long)renderer.Materials.Count + count : count;
            if (size < 0 || size > MaxMaterialSlots) return false;
        }
        var actions = new List<(Action undo, Action redo)>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            var renderer = (SkinnedMeshRenderer)targets[i];
            int size = relative ? renderer.Materials.Count + count : count;
            if (size == renderer.Materials.Count) continue;
            var previous = renderer.Materials.ToArray();
            var next = new AssetRef<Material>[size];
            Array.Copy(previous, next, Math.Min(previous.Length, size));
            var componentId = renderer.Identifier;
            actions.Add((() => ApplyMaterialList(componentId, previous), () => ApplyMaterialList(componentId, next)));
            renderer.Materials.Clear(); renderer.Materials.AddRange(next);
            renderer.OnValidate();
        }
        if (actions.Count != 0)
        {
            Undo.RegisterActionGroup("Resize Materials", actions);
            EditorSceneManager.MarkDirty("Resize Materials", "editor");
        }
        return true;
    }

    private static void ApplyMaterialList(Guid id, AssetRef<Material>[] materials)
    {
        if (Undo.FindComponent(id) is not SkinnedMeshRenderer renderer) return;
        renderer.Materials ??= new();
        renderer.Materials.Clear(); renderer.Materials.AddRange(materials);
        renderer.OnValidate();
    }
}
