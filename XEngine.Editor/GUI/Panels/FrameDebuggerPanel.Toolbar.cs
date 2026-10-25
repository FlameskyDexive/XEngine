// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime.Rendering;
using XEngine.Scribe;

using PaperTextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

public sealed partial class FrameDebuggerPanel
{
    // Unity toolbar: [Enable] [Editor ▾] [—— scrubber ——] [N] [◀] [▶]  (+ Refresh / gear)
    private void DrawToolbar(Paper paper, FontFile font, float width)
    {
        using (paper.Row("fd_toolbar").Width(width).Height(ToolbarHeight).Padding(8, 8, 4, 4).RowBetween(6).Enter())
        {
            bool enabled = FrameDebugger.IsEnabled;

            Origami.Button(paper, "fd_enable", enabled ? "Disable Frame Debugger" : "Enable Frame Debugger",
                    ToggleEnabled)
                .Width(168)
                .Tooltip(enabled
                    ? "Stop capturing; live views render fully again"
                    : "Capture the next frame; draws after the selected event are skipped in the game view")
                .Show();

            // Unity's attach-target dropdown connects to remote players; XEngine has no remote
            // players yet, so it is a fixed "Editor" placeholder.
            using (Origami.BeginReadOnlyScope())
            {
                Origami.Dropdown(paper, "fd_target", 0, _ => { }, ["Editor"]).Width(96).Show();
            }

            FrameCapture? capture = FrameDebugger.LastCapture;
            int leafCount = capture?.LeafEvents.Count ?? 0;
            int sel = leafCount == 0 ? 0 : Math.Clamp(FrameDebugger.SelectedEventIndex, 0, leafCount - 1);

            if (leafCount > 0)
            {
                Origami.IntSlider(paper, "fd_scrub", sel, v =>
                        {
                            if (v != FrameDebugger.SelectedEventIndex)
                                SelectLeaf(v);
                        }, 0, leafCount - 1)
                    .ShowValue(false).ShowTooltip(false).Small()
                    .Width(UnitValue.StretchOne)
                    .Show();
            }
            else
            {
                paper.Box("fd_scrub_empty").Width(UnitValue.StretchOne).Height(22);
            }

            paper.Box("fd_index").Width(48).Height(22)
                .Text(leafCount == 0 ? "-" : (sel + 1).ToString(), font)
                .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleCenter)
                .BackgroundColor(EditorTheme.Neutral100);

            Origami.Button(paper, "fd_prev", EditorIcons.ChevronLeft, () => Step(-1))
                .Width(26).Disabled(leafCount == 0)
                .Tooltip("Previous event").Show();
            Origami.Button(paper, "fd_next", EditorIcons.ChevronRight, () => Step(1))
                .Width(26).Disabled(leafCount == 0)
                .Tooltip("Next event").Show();

            Origami.Button(paper, "fd_refresh", EditorIcons.Rotate, () =>
                {
                    FrameDebugger.Refresh();
                    InvalidateSnapshot();
                    RepaintViews();
                })
                .Width(26).Tooltip("Re-capture the next frame")
                .Show();

            Origami.Button(paper, "fd_opts", EditorIcons.Gear, () =>
                    Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, b =>
                    {
                        b.Header("Frame Debugger");
                        b.Toggle("Capture Full Frame (editor UI)", () =>
                            {
                                FrameDebugger.CaptureScope =
                                    FrameDebugger.CaptureScope == FrameDebuggerCaptureScope.FullFrame
                                        ? FrameDebuggerCaptureScope.GameRendering
                                        : FrameDebuggerCaptureScope.FullFrame;
                                InvalidateSnapshot();
                                RepaintViews();
                            },
                            () => FrameDebugger.CaptureScope == FrameDebuggerCaptureScope.FullFrame);
                        b.Toggle("Show State Events", () => _showStateEvents = !_showStateEvents,
                            () => _showStateEvents);
                        b.Toggle("Show Raster State Section", () => _showRasterSection = !_showRasterSection,
                            () => _showRasterSection);
                    }))
                .Width(26).Tooltip("Options")
                .Show();
        }
    }

    private static void DrawStatusStrip(Paper paper, FontFile font, float width, string message, bool warning)
    {
        paper.Box("fd_status").Width(width).Height(StatusHeight)
            .Text("  " + message, font)
            .TextColor(warning ? EditorTheme.AccentText : EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall)
            .Alignment(PaperTextAlignment.MiddleLeft)
            .BackgroundColor(EditorTheme.Neutral100);
    }
}
