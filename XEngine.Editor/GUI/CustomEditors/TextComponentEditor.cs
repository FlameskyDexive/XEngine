// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;

namespace XEngine.Editor.Inspector;

// ================================================================
//  Text Component Custom Editor
// ================================================================

[CustomEditor(typeof(TextComponent))]
public class TextComponentEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var text = (TextComponent)target;

        Undo.SnapshotIfInteracting(text, paper);

        // ── Text Input ────────────────────────────────────────────
        Origami.Header(paper, $"{id}_h_text", "Text Input").Show();

        // Full-width multi-line area
        // Sits outside InspectorRow so it isn't clamped to a single row's height.
        Origami.TextArea(paper, $"{id}_text", text.Text, v => text.Text = v ?? string.Empty, rows: 6)
            .Placeholder("Enter text...")
            .Show();

        paper.Box($"{id}_sp0").Height(6);

        // ── Main Settings ─────────────────────────────────────────
        Origami.Header(paper, $"{id}_h_main", "Main Settings").Show();

        PropertyGridUtils.DrawField(paper, $"{id}_font", "Font Asset", typeof(AssetRef<FontAsset>), text.Font,
            v => text.Font = (AssetRef<FontAsset>)v!, 0);

        paper.Box($"{id}_sp0.1").Height(6);

        EditorGUI.Row(paper, $"{id}_size", "Font Size", () =>
            Origami.NumericField<int>(paper, $"{id}_size_v", text.Size, v => text.Size = v).Show());

        paper.Box($"{id}_sp0.15").Height(6);

        EditorGUI.Row(paper, $"{id}_quality", "Quality", () =>
            Origami.EnumDropdown<XEngine.Scribe.FontQuality>(paper, $"{id}_quality_v", text.Quality, v => text.Quality = v).Show());

        paper.Box($"{id}_sp0.18").Height(6);

        Origami.Checkbox(paper, $"{id}_rich", text.RichTextEnabled, v => text.RichTextEnabled = v)
            .LabelRight("Rich Text").Show();

        paper.Box($"{id}_sp0.2").Height(6);

        EditorGUI.Row(paper, $"{id}_color", "Text Color", () =>
        Origami.ColorField(paper, $"{id}_color_f", text.TextColor, v => text.TextColor = v).Show());

        paper.Box($"{id}_sp0.3").Height(6);

        EditorGUI.TextAlignmentRow(paper, $"{id}_align", "Alignment", text.Alignment, v => text.Alignment = v);

        paper.Box($"{id}_sp0.4").Height(6);

        EditorGUI.Row(paper, $"{id}_hoverflow", "Horizontal Overflow", () =>
            Origami.EnumDropdown<HorizontalWrapMode>(paper, $"{id}_hoverflow_v", text.HorizontalOverflow, v => text.HorizontalOverflow = v).Show());

        paper.Box($"{id}_sp0.5").Height(6);

        EditorGUI.Row(paper, $"{id}_voverflow", "Vertical Overflow", () =>
            Origami.EnumDropdown<VerticalWrapMode>(paper, $"{id}_voverflow_v", text.VerticalOverflow, v => text.VerticalOverflow = v).Show());

        paper.Box($"{id}_sp0.6").Height(6);

        Origami.Checkbox(paper, $"{id}_bestfit", text.BestFit, v => text.BestFit = v)
            .LabelRight("Best Fit").Show();

        if (text.BestFit)
        {
            paper.Box($"{id}_sp0.7").Height(6);
            EditorGUI.Row(paper, $"{id}_bfmin", "Best Fit Min", () =>
                Origami.NumericField<int>(paper, $"{id}_bfmin_v", text.BestFitMinSize, v => text.BestFitMinSize = v).Show());
            paper.Box($"{id}_sp0.8").Height(6);
            EditorGUI.Row(paper, $"{id}_bfmax", "Best Fit Max", () =>
                Origami.NumericField<int>(paper, $"{id}_bfmax_v", text.BestFitMaxSize, v => text.BestFitMaxSize = v).Show());
        }

        paper.Box($"{id}_sp1").Height(6);

        // ── Extra Settings ────────────────────────────────────────
        Origami.Header(paper, $"{id}_h_extra", "Extra Settings").Show();

        PropertyGridUtils.DrawField(paper, $"{id}_mat", "Material", typeof(AssetRef<Runtime.Resources.Material>), text.Material,
            v => text.Material = (AssetRef<Runtime.Resources.Material>)v!, 0);
    }

}
