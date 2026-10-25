// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Spatial;

namespace XEngine.Editor.Inspector;

/// <summary>
/// Inspector for a <see cref="Sprite"/> sub-asset: a preview of its sub-rect in the source texture, its
/// read-only stats, and a button to edit the texture's sprites in the Sprite Editor. Sprites aren't edited
/// here - they're configured on their source texture (Sprite Mode + Sprite Editor).
/// </summary>
[CustomAssetEditor(typeof(Sprite))]
public class SpriteAssetEditor : AssetImporterEditor
{
    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        if (asset is Sprite sprite)
            DrawInspector(paper, id, sprite);
        else
            Origami.Label(paper, $"{id}_none", "Sprite failed to load.").Show();
    }

    /// <summary>Draws the sprite preview + read-only stats + edit button. Reused for sprite sub-assets in the InspectorPanel.</summary>
    public static void DrawInspector(Paper paper, string id, Sprite sprite)
    {
        var m = Origami.Current.Metrics;

        Texture2D? tex = sprite.Texture.Res;
        // Preview is rendered by the Inspector's universal preview bar.

        Origami.Header(paper, $"{id}_hdr", $"{EditorIcons.Image}  Sprite").Show();

        InfoRow(paper, $"{id}_src", "Source Texture", tex?.Name ?? "None");
        InfoRow(paper, $"{id}_rect", "Rect", $"{sprite.Rect.X}, {sprite.Rect.Y}   {sprite.Rect.Width} x {sprite.Rect.Height}");
        InfoRow(paper, $"{id}_pivot", "Pivot", $"{sprite.Pivot.X:0.###}, {sprite.Pivot.Y:0.###}");
        InfoRow(paper, $"{id}_ppu", "Pixels Per Unit", $"{sprite.PixelsPerUnit:0.##}");
        InfoRow(paper, $"{id}_border", "Border (L/T/R/B)", $"{sprite.Border.X:0} / {sprite.Border.Y:0} / {sprite.Border.Z:0} / {sprite.Border.W:0}");

        paper.Box($"{id}_sp").Height(8);

        Guid texGuid = sprite.Texture.AssetID;
        if (texGuid != Guid.Empty)
        {
            // "Edit Sprites" is navigation, not data editing - keep it enabled even in the read-only sub-asset view.
            bool ro = Origami.IsReadOnly;
            if (ro) Origami.EndReadOnly();
            Origami.Button(paper, $"{id}_edit", $"{EditorIcons.PenToSquare}  Edit Sprites",
                () => SpriteEditorWindow.OpenFor(texGuid)).Width(200).Show();
            if (ro) Origami.BeginReadOnly();
        }
    }

    private static void InfoRow(Paper paper, string id, string label, string value)
    {
        EditorGUI.Row(paper, id, label, () =>
            paper.Box($"{id}_v").Height(EditorTheme.RowHeight)
                .Text(value, EditorTheme.DefaultFont!).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft));
    }
}
