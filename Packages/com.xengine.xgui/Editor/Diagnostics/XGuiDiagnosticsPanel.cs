using System;
using System.Collections.Generic;

using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.Editor;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using PaperTextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.XGUI.Editor;

/// <summary>Read-only runtime view of the XGUI host registry and dirty queues.</summary>
internal sealed class XGuiDiagnosticsPanel : DockPanel
{
    [MenuItem("Window/Analysis/XGUI Diagnostics", priority: 21, Icon = EditorIcons.Bug)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(XGuiDiagnosticsPanel));

    public override string Title => "XGUI Diagnostics";
    public override string Icon => EditorIcons.Bug;

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        Scene? scene = Scene.Current;
        IReadOnlyList<GameCanvas> canvases = scene == null
            ? Array.Empty<GameCanvas>()
            : CanvasRegistry.GetCanvases(scene);

        int graphics = 0;
        int activeGraphics = 0;
        using (paper.Column("xgui_diag_root").Size(width, height).Padding(10, 10, 10, 10).Clip().Enter())
        {
            XGuiHostDiagnosticsSnapshot host = XGuiHostDiagnostics.Capture(scene);
            DrawHeader(paper, font, "Host", $"Canvas count: {host.CanvasCount}");
            DrawHeader(paper, font, "Batches",
                $"Overlay {host.OverlayNodes}   World {host.WorldNodes}   Active {host.ActiveNodes}   " +
                $"UIBatches {host.UIBatches}   Draws {host.UIDrawCalls}   Uploads {host.MeshUploads}");
            DrawHeader(paper, font, "Clip chain",
                $"Rect clips {host.RectClips}   Stencil {host.StencilClips}   " +
                $"Layout rebuilds {host.LayoutRebuilds}   Graphic rebuilds {host.GraphicRebuilds}");
            for (int i = 0; i < canvases.Count; i++)
            {
                GameCanvas canvas = canvases[i];
                IReadOnlyList<Graphic> canvasGraphics = GraphicRegistry.GetGraphics(canvas);
                graphics += canvasGraphics.Count;
                for (int j = 0; j < canvasGraphics.Count; j++)
                {
                    Graphic graphic = canvasGraphics[j];
                    if (graphic.EnabledInHierarchy && graphic.CanvasRenderer.RenderNode.IsAttached)
                        activeGraphics++;
                }

                DrawCanvas(paper, font, i, canvas, canvasGraphics.Count);
            }

            DrawHeader(paper, font, "Graphics", $"Registered: {graphics}   Active: {activeGraphics}");
            DrawHeader(paper, font, "Dirty queues",
                $"Layout {CanvasUpdateRegistry.LayoutCount}   Clip {CanvasUpdateRegistry.ClipCount}   " +
                $"Vertices {CanvasUpdateRegistry.VerticesCount}   Material {CanvasUpdateRegistry.MaterialCount}   " +
                $"Hierarchy {CanvasUpdateRegistry.HierarchyCount}");
            DrawHeader(paper, font, "Layout roots",
                $"Highest effective roots: {CanvasUpdateRegistry.LayoutRootCount}   Stage: {CanvasUpdateRegistry.LastPhase}");

            XGuiMigrationReport migration = XGuiMigrationScanner.Scan(scene);
            DrawHeader(paper, font, "Migration",
                $"Objects {migration.ObjectsScanned}   Package {migration.PackageComponentCount}   "
                + $"Legacy {migration.LegacyCompatibilityCount}   Missing {migration.MissingXGuiComponentCount}");

            string cycle = CanvasUpdateRegistry.LastLayoutCycleDiagnostic;
            if (!string.IsNullOrEmpty(cycle))
                paper.Box("xgui_diag_layout_cycle").Height(44).Text(cycle, font)
                    .TextColor(EditorTheme.Amber500).FontSize(EditorTheme.FontSizeSmall);

            if (canvases.Count == 0)
                paper.Box("xgui_diag_empty").Height(28).Text("No active Canvas hosts in the current scene.", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall);

            DrawMissingComponents(paper, font, migration);
        }
    }

    private static void DrawCanvas(Paper paper, XEngine.Scribe.FontFile font, int index, GameCanvas canvas, int graphics)
    {
        string id = "xgui_diag_canvas_" + index;
        string state = canvas.EnabledInHierarchy ? "enabled" : "disabled";
        paper.Box(id).Height(22).Text(
                $"{canvas.GameObject.Name}  [{state}]  mode={canvas.RenderMode}  sort={canvas.SortOrder}  graphics={graphics}", font)
            .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall);
    }

    private static void DrawHeader(Paper paper, XEngine.Scribe.FontFile font, string title, string value)
    {
        using (paper.Row("xgui_diag_" + title.ToLowerInvariant()).Height(28).RowBetween(4).Enter())
        {
            paper.Box("title").Width(130).Text(title, font).TextColor(EditorTheme.AccentText)
                .FontSize(EditorTheme.FontSize).Alignment(PaperTextAlignment.MiddleLeft);
            paper.Box("value").Text(value, font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft);
        }
    }

    private static void DrawMissingComponents(Paper paper, XEngine.Scribe.FontFile font, XGuiMigrationReport report)
    {
        if (report.MissingXGuiComponentCount == 0) return;

        int shown = 0;
        for (int i = 0; i < report.Findings.Count && shown < 5; i++)
        {
            XGuiMigrationFinding finding = report.Findings[i];
            if (finding.Kind != XGuiMigrationFindingKind.MissingXGuiComponent) continue;
            paper.Box("xgui_diag_missing_" + shown).Height(22)
                .Text("Missing: " + finding.HierarchyPath + " (" + finding.SerializedType + ")", font)
                .TextColor(EditorTheme.Amber500).FontSize(EditorTheme.FontSizeSmall);
            shown++;
        }
    }
}
