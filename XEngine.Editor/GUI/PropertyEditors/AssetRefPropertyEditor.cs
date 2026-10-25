using System;
using XEngine.Editor.GUI.Popups;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.OrigamiUI;
using XEngine.Runtime;

using Color = System.Drawing.Color;
using XEngine.Editor.Core;
using XEngine.Editor.Theming;

namespace XEngine.Editor.GUI.PropertyEditors;

/// <summary>
/// PropertyEditor for AssetRef&lt;T&gt; fields (via the IAssetRef interface).
/// Supports asset references, runtime instances, drag-drop from project/hierarchy/inspector.
/// </summary>
[CustomPropertyEditor(typeof(IAssetRef))]
public class AssetRefPropertyEditor : PropertyEditor
{

    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
        => Draw(paper, id, label, value, onChange, PropertyGridRenderer.IsMixedField);

    internal ElementBuilder? Draw(Paper paper, string id, string label, object? value, Action<object?> onChange,
        bool mixed = false, bool compact = false, Action? onLabelDragStart = null)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return null;

        // Match the Origami PropertyGrid standard row (metrics, muted label, horizontal padding).
        var m = Origami.Current.Metrics;
        float rh = m.RowHeight;

        var assetRef = value as IAssetRef;
        if (assetRef == null) return null;

        Type fieldType = assetRef.InstanceType;
        var instance = mixed ? null : assetRef.GetInstance() as EngineObject;

        bool isAsset = instance != null && instance.AssetID != Guid.Empty;
        bool isInstance = instance != null && instance.AssetID == Guid.Empty;
        string suffix = isAsset ? instance!.GetType().Name : isInstance ? "Instance" : fieldType.Name;
        string displayName = mixed ? "—" : instance != null ? $"{instance.Name} ({suffix})"
            : assetRef.AssetID != Guid.Empty ? $"Unresolved ({fieldType.Name})" : $"None ({fieldType.Name})";
        string icon = isAsset ? EditorIcons.Cube : isInstance ? EditorIcons.CircleDot : EditorIcons.Circle;
        var iconColor = isAsset ? EditorTheme.Purple400 : isInstance ? EditorTheme.Ink500 : EditorTheme.Ink300;

        var row = paper.Row(id).Height(UnitValue.Auto).MinHeight(rh).Padding(m.PaddingLarge, m.PaddingLarge, 0, 0).RowBetween(m.Padding);
        using (row.Enter())
        {
            // Label
            if (onLabelDragStart != null && !string.IsNullOrEmpty(label))
            {
                using (paper.Row($"{id}_label_area").Width(m.LabelWidth).Height(rh).RowBetween(4).Enter())
                {
                    var grip = paper.Box($"{id}_grip").Width(10).Height(rh)
                        .Text(EditorIcons.GripLines, font).TextColor(EditorTheme.Ink300)
                        .FontSize(10).Alignment(TextAlignment.MiddleCenter);
                    if (!Origami.IsReadOnly) grip.OnDragStart(onLabelDragStart, static (start, _) => start());
                    else grip.IsNotInteractable();
                    paper.Box($"{id}_lbl").Width(UnitValue.Stretch()).Height(rh).IsNotInteractable()
                        .Text(label, font).TextColor(Origami.Current.Ink.C300)
                        .FontSize(m.FontSize).Alignment(TextAlignment.MiddleLeft).TextTruncate();
                }
            }
            else if (!string.IsNullOrEmpty(label))
                paper.Box($"{id}_lbl")
                    .Width(m.LabelWidth).Height(rh).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch())
                    .IsNotInteractable()
                    .Text(label, font).TextColor(Origami.Current.Ink.C300)
                    .FontSize(m.FontSize).Alignment(TextAlignment.MiddleLeft).TextTruncate();

            bool isDragTarget = EditorGUI.IsCompatibleDragTarget(fieldType);

            var fieldEl = paper.Row($"{id}_field")
                .Height(rh)
                .BackgroundColor(isDragTarget ? Color.FromArgb(60, EditorTheme.Purple400) : EditorTheme.Glass)
                .Hovered.BorderColor(EditorTheme.BorderStrong).End()
                .Rounded(compact ? 3 : 6).Padding(compact ? 3 : m.SpacingLarge, compact ? 2 : m.PaddingSmall, 0, 0).RowBetween(compact ? 2 : m.SpacingLarge)
                .BorderColor(isDragTarget ? EditorTheme.Purple400 : EditorTheme.BorderSoft).BorderWidth(1)
                .OnClick((fieldType, assetRef, onChange, instance, isAsset, isInstance), (cap, e) =>
                {
                    if (cap.instance != null)
                        Selection.Ping(cap.instance!.AssetID);
                })
                .OnDoubleClick((fieldType, assetRef, onChange, instance, isAsset, isInstance), (cap, _) =>
                {
                    if (cap.isAsset)
                    {
                        // Asset -> focus in project
                        Selection.Ping(cap.instance!.AssetID);
                    }
                    else if (cap.isInstance)
                    {
                        // Instance -> select it in inspector
                        Selection.Select(cap.instance!);
                    }
                    else
                    {
                        // None -> open selector
                        OpenAssetSelector(cap.fieldType, asset => Assign(cap.assetRef, asset, cap.onChange));
                    }
                });

            using (fieldEl.Enter())
            {
                // Accept drops
                HandleDrops(paper, assetRef, fieldType, onChange);

                // Leading type icon (no chip background)
                paper.Box($"{id}_ico")
                    .Width(compact ? 16 : UnitValue.Auto).Height(rh).IsNotInteractable()
                    .Text(icon, font).TextColor(iconColor)
                    .FontSize(11f).Alignment(TextAlignment.MiddleCenter);

                // Name
                paper.Box($"{id}_name")
                    .Width(UnitValue.Stretch()).Height(rh).Clip()
                    .IsNotInteractable()
                    .Text(displayName, font)
                    .TextColor(instance != null ? EditorTheme.Ink500 : EditorTheme.Ink200)
                    .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);

                // Picker button
                paper.Box($"{id}_pick")
                    .Width(compact ? 20 : 18).Height(18).Rounded(4).Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch())
                    .Text(EditorIcons.CircleDot, font).TextColor(EditorTheme.Ink200)
                    .FontSize(12f).Alignment(TextAlignment.MiddleCenter)
                    .Hovered.BackgroundColor(EditorTheme.Hover).End()
                    .OnClick((fieldType, assetRef, onChange), (cap, e) =>
                    {
                        e.StopPropagation();
                        OpenAssetSelector(cap.Item1, asset => Assign(cap.Item2, asset, cap.Item3));
                    });
            }
        }
        return row;
    }

    private static void HandleDrops(Paper paper, IAssetRef assetRef, Type fieldType, Action<object?> onChange)
    {
        EditorDragFeedback.Reference(paper, fieldType);
        if (!paper.IsParentHovered || !DragDrop.IsDropFrameFor(paper)) return;

        // Asset drop
        if (DragDrop.Payload is AssetDragPayload adp && adp.AssetType != null && fieldType.IsAssignableFrom(adp.AssetType))
        {
            var droppedAsset = Runtime.AssetDatabase.Get(adp.AssetGuid);
            if (droppedAsset != null)
            {
                Assign(assetRef, droppedAsset, onChange);
            }
            DragDrop.EndDrag();
            return;
        }

        // GameObject drop
        if (DragDrop.Payload is GameObjectDragPayload goDrop && goDrop.GameObjects.Length > 0)
        {
            var go = goDrop.GameObjects[0];

            if (typeof(GameObject).IsAssignableFrom(fieldType) && fieldType.IsInstanceOfType(go))
            {
                // Direct GO reference
                assetRef.SetInstance(go);
                onChange(assetRef);
            }
            else if (typeof(MonoBehaviour).IsAssignableFrom(fieldType))
            {
                // Search GO for matching component
                var comp = go.GetComponent(fieldType);
                if (comp != null)
                {
                    assetRef.SetInstance(comp);
                    onChange(assetRef);
                }
            }
            DragDrop.EndDrag();
            return;
        }

        // Component drop
        if (DragDrop.Payload is ComponentDragPayload cdp && fieldType.IsAssignableFrom(cdp.Component.GetType()))
        {
            assetRef.SetInstance(cdp.Component);
            onChange(assetRef);
            DragDrop.EndDrag();
            return;
        }
    }

    private static void OpenAssetSelector(Type type, Action<object?> onChange)
    {
        SelectorModal.Open($"Select {type.Name}", type, SelectorTabs.Assets, onChange);
    }

    private static void Assign(IAssetRef assetRef, object? asset, Action<object?> onChange)
    {
        if (asset is EngineObject engineObject && engineObject.IsDisposed) return;
        if (asset != null && !assetRef.InstanceType.IsInstanceOfType(asset)) return;
        assetRef.SetInstance(asset);
        onChange(assetRef);
    }

}
