// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// The editor draws one <see cref="Paper"/> surface per window (the main window plus any detached
/// one), and each surface runs Origami's overlay pass. Popups therefore have to name the surface they
/// belong to, or a Scene view context menu also appears in every unrelated window.
/// </summary>
public class OrigamiSurfaceOwnershipTests
{
    [Fact]
    public void Modal_WithOwner_DrawsOnlyOnThatSurface()
    {
        Modal.PopAll();
        var main = NewSurface();
        var detached = NewSurface();
        List<Paper> drawnOn = [];

        Modal.Push(new CustomDrawModal((paper, _, _) => drawnOn.Add(paper)), main);
        try
        {
            DrawSurface(main);
            DrawSurface(detached);
        }
        finally
        {
            Modal.PopAll();
        }

        Assert.Equal([main], drawnOn);
    }

    [Fact]
    public void Modal_WithoutOwner_DrawsOnEverySurface()
    {
        Modal.PopAll();
        var main = NewSurface();
        var detached = NewSurface();
        List<Paper> drawnOn = [];

        Modal.Push(new CustomDrawModal((paper, _, _) => drawnOn.Add(paper)));
        try
        {
            DrawSurface(main);
            DrawSurface(detached);
        }
        finally
        {
            Modal.PopAll();
        }

        Assert.Equal([main, detached], drawnOn);
    }

    /// <summary>
    /// A click-opened popup reads <see cref="Origami.ActivePaper"/> to find its surface, and Paper
    /// dispatches click handlers from its own EndFrame — after Origami's. So the active surface has to
    /// outlive <see cref="Origami.EndFrame"/>, otherwise every such popup ends up unowned.
    /// </summary>
    [Fact]
    public void ActivePaper_SurvivesEndFrame()
    {
        var surface = NewSurface();
        DrawSurface(surface);
        Assert.Same(surface, Origami.ActivePaper);
    }

    [Fact]
    public void ContextMenu_IsOwnedByTheSurfaceThatOpenedIt()
    {
        Modal.PopAll();
        var main = NewSurface();
        var detached = NewSurface();
        List<Paper> menuDrawnOn = [];

        // Stand in for the click handler Paper runs at the end of the main surface's frame.
        DrawSurface(main);
        ContextMenu.Show(10f, 10f, menu => menu.Item("Show Gizmos", () => { }));
        try
        {
            // Replace the menu's own body with a recorder: RenderMenu needs a themed font, which a
            // bare test surface has no reason to carry.
            Modal.PopAll();
            Modal.Push(new CustomDrawModal((paper, _, _) => menuDrawnOn.Add(paper)), Origami.ActivePaper);

            DrawSurface(detached);
            DrawSurface(main);
        }
        finally
        {
            ContextMenu.Close();
            Modal.PopAll();
        }

        Assert.Equal([main], menuDrawnOn);
    }

    [Fact]
    public void ContextMenu_BackdropDismiss_ClearsOpenState()
    {
        Modal.PopAll();
        var main = NewSurface();
        DrawSurface(main);
        ContextMenu.Show(10f, 10f, menu => menu.Item("Show Gizmos", () => { }));
        try
        {
            Assert.True(ContextMenu.IsOpen);
            Assert.True(Modal.IsOpen);
            Modal.DismissAt(0);
            Assert.False(ContextMenu.IsOpen);
            Assert.False(Modal.IsOpen);
        }
        finally
        {
            ContextMenu.Close();
            Modal.PopAll();
        }
    }

    [Fact]
    public void ContextMenu_Show_SpreadsAcrossSurfaces()
    {
        Modal.PopAll();
        var main = NewSurface();
        DrawSurface(main);
        ContextMenu.Show(10f, 10f, menu => menu.Item("Show Gizmos", () => { }));
        try
        {
            Assert.True(ContextMenu.SpreadsToAllSurfaces);
        }
        finally
        {
            ContextMenu.Close();
            Modal.PopAll();
        }
    }

    private static Paper NewSurface() => new(new NullRenderer(), 400, 300, new FontAtlasSettings());

    private static void DrawSurface(Paper paper)
    {
        paper.BeginFrame(0.016f);
        Origami.BeginFrame(paper, 0.016f);
        Origami.EndFrame(paper);
        paper.EndFrame();
    }

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
