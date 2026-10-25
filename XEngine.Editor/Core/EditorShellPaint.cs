// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;

namespace XEngine.Editor.Core;

/// <summary>
/// Compared subtree regions for the main-window menu bar, toolbar and status bar.
/// Opt-in via <c>--shell-paint-incremental</c>. Nebula, dock tabs and Origami popups stay
/// on the host: nebula is animated, dock chrome lives in Origami (no Editor.UI.Rendering
/// reference) and popups are already painted onto the presenting host's Paper.
/// </summary>
internal sealed class EditorShellPaint : IDisposable
{
    private UiPaperSubtreeRegion? _menu;
    private UiPaperSubtreeRegion? _toolbar;
    private UiPaperSubtreeRegion? _status;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _geometryBudget = new(4 * 1024 * 1024);
    private readonly UiMemoryBudget _atlasBudget = new(8 * 1024 * 1024);

    internal long MenuCaptures => _menu?.CaptureCount ?? -1;
    internal long ToolbarCaptures => _toolbar?.CaptureCount ?? -1;
    internal long StatusCaptures => _status?.CaptureCount ?? -1;
    internal long GeometryReserved => _geometryBudget.ReservedBytes;
    internal long AtlasReserved => _atlasBudget.ReservedBytes;

    internal void AttachMenu(Paper paper) => Attach(paper, ref _menu);
    internal void AttachToolbar(Paper paper) => Attach(paper, ref _toolbar);
    internal void AttachStatus(Paper paper) => Attach(paper, ref _status);

    private void Attach(Paper paper, ref UiPaperSubtreeRegion? region)
    {
        if (!Program.ShellPaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
        if (!ReferenceEquals(_paintRegionPaper, paper))
        {
            DisposeRegions();
            _paintRegionPaper = paper;
        }
        if (region == null)
        {
            paper.StabilizeFontAtlasBeforeRender = true;
            paper.Canvas.EnableFontAtlasRetirement();
            region = new UiPaperSubtreeRegion(paper, _geometryBudget, _atlasBudget, 512,
                shareHostFonts: true, compareVisualState: true);
        }
        region.Attach(paper.CurrentParent);
    }

    public void Dispose() => DisposeRegions();

    private void DisposeRegions()
    {
        _menu?.Dispose(); _menu = null;
        _toolbar?.Dispose(); _toolbar = null;
        _status?.Dispose(); _status = null;
        _paintRegionPaper = null;
    }
}
