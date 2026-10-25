// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.FrameDebug;

public sealed class FrameDebuggerTests : IDisposable
{
    public FrameDebuggerTests()
    {
        FrameDebugger.Disable();
        FrameDebugger.CaptureScope = FrameDebuggerCaptureScope.GameRendering;
    }

    public void Dispose()
    {
        FrameDebugger.Disable();
        FrameDebugger.CaptureScope = FrameDebuggerCaptureScope.GameRendering;
    }

    [Fact]
    public void Ingest_BuildsTreeWithClearAndDrawLeaves()
    {
        FrameDebugger.Enable();
        Graphics.BeginFrame();
        Assert.True(FrameDebugger.IsCapturing);

        using GraphicsVertexArray vao = CreateDummyVao();
        CommandBuffer cmd = Graphics.GetCommandBuffer("ColorPass");
        cmd.SetRenderTarget(null);
        cmd.ClearRenderTarget(ClearFlags.Color, new Color(1f, 0f, 0f, 1f));
        cmd.SetRasterState(new RasterizerState { DoBlend = true });
        cmd.DrawArrays(vao, Topology.Triangles, 0, 3);
        SubmitInScope(cmd);

        Graphics.EndFrameAndWait();
        Assert.False(FrameDebugger.IsCapturing);

        FrameCapture? capture = FrameDebugger.LastCapture;
        Assert.NotNull(capture);
        Assert.True(capture.LeafEvents.Count >= 2);
        Assert.True(HasKind(capture, FrameEventKind.Clear));
        Assert.True(HasKind(capture, FrameEventKind.Draw));
        Assert.True(HasKind(capture, FrameEventKind.SetRenderTarget));
        Assert.True(capture.Root.Children.Count >= 1);
        Assert.Equal(FrameDebuggerScopeKind.GameView, capture.LeafEvents[0].Scope);
        Assert.Equal(capture.LeafEvents.Count, capture.ScopeLeafTotals[(int)FrameDebuggerScopeKind.GameView]);
    }

    [Fact]
    public void GpuStateRebuilder_SeesStickyRasterAndRtAcrossOps()
    {
        FrameDebugger.Enable();
        Graphics.BeginFrame();

        using GraphicsVertexArray vao = CreateDummyVao();
        CommandBuffer cmd = Graphics.GetCommandBuffer("Pass");
        cmd.SetRenderTarget(null);
        cmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, Color.Black);
        var raster = new RasterizerState { DepthWrite = false, DoBlend = true };
        cmd.SetRasterState(raster);
        cmd.DrawArrays(vao, Topology.Triangles, 0, 3);
        SubmitInScope(cmd);

        Graphics.EndFrameAndWait();
        FrameCapture capture = FrameDebugger.LastCapture!;
        int clearIndex = IndexOfKind(capture, FrameEventKind.Clear);
        int drawIndex = IndexOfKind(capture, FrameEventKind.Draw);
        Assert.True(clearIndex >= 0);
        Assert.True(drawIndex > clearIndex);

        GpuStateSnapshot afterClear = GpuStateRebuilder.Rebuild(capture, clearIndex);
        Assert.True(afterClear.RenderTarget.IsBackbuffer);
        Assert.True(afterClear.ClearedColorInPass);

        GpuStateSnapshot afterDraw = GpuStateRebuilder.Rebuild(capture, drawIndex);
        Assert.False(afterDraw.Raster.DepthWrite);
        Assert.True(afterDraw.Raster.DoBlend);
        Assert.Equal(3u, afterDraw.IndexCount);
    }

    [Fact]
    public void StickyRenderTarget_CarriesAcrossCommandBuffers()
    {
        FrameDebugger.Enable();
        Graphics.BeginFrame();

        using GraphicsVertexArray vao = CreateDummyVao();
        CommandBuffer a = Graphics.GetCommandBuffer("A");
        a.SetRenderTarget(null);
        a.SetViewport(0, 0, 64, 32);
        SubmitInScope(a);

        CommandBuffer b = Graphics.GetCommandBuffer("B");
        b.DrawArrays(vao, Topology.Triangles, 0, 6);
        SubmitInScope(b);

        Graphics.EndFrameAndWait();
        FrameCapture capture = FrameDebugger.LastCapture!;
        int drawIndex = IndexOfKind(capture, FrameEventKind.Draw);
        GpuStateSnapshot snap = GpuStateRebuilder.Rebuild(capture, drawIndex);
        Assert.True(snap.RenderTarget.IsBackbuffer);
        Assert.Equal(64u, snap.ViewportW);
        Assert.Equal(32u, snap.ViewportH);
    }

    [Fact]
    public void SetGlobalFloat_VisibleInRebuildBeforeAndAfter()
    {
        FrameDebugger.Enable();
        Graphics.BeginFrame();

        using GraphicsVertexArray vao = CreateDummyVao();
        CommandBuffer cmd = Graphics.GetCommandBuffer("Globals");
        cmd.SetGlobalFloat("_Test", 1.5f);
        cmd.DrawArrays(vao, Topology.Triangles, 0, 3);
        cmd.SetGlobalFloat("_Test", 9f);
        SubmitInScope(cmd);

        Graphics.EndFrameAndWait();
        FrameCapture capture = FrameDebugger.LastCapture!;
        int drawIndex = IndexOfKind(capture, FrameEventKind.Draw);
        GpuStateSnapshot atDraw = GpuStateRebuilder.Rebuild(capture, drawIndex);
        Assert.True(atDraw.Globals._floats.TryGetValue("_Test", out float v));
        Assert.Equal(1.5f, v);
    }

    [Fact]
    public void Disabled_SubmitDoesNotCreateCapture()
    {
        FrameDebugger.Disable();
        Graphics.BeginFrame();
        CommandBuffer cmd = Graphics.GetCommandBuffer("NoCapture");
        cmd.ClearRenderTarget(ClearFlags.Color, Color.Black);
        Graphics.Submit(cmd);
        Graphics.EndFrameAndWait();
        Assert.Null(FrameDebugger.LastCapture);
    }

    [Fact]
    public void GameMode_ExcludesUnscopedSubmissions()
    {
        FrameDebugger.Enable();
        Graphics.BeginFrame();

        // Editor chrome: submitted with no target scope — must not enter the capture.
        CommandBuffer editorUi = Graphics.GetCommandBuffer("Paper UI");
        editorUi.ClearRenderTarget(ClearFlags.Color, Color.Black);
        Graphics.Submit(editorUi);

        // Game rendering: scoped — captured.
        using GraphicsVertexArray vao = CreateDummyVao();
        CommandBuffer game = Graphics.GetCommandBuffer("ColorPass");
        game.DrawArrays(vao, Topology.Triangles, 0, 3);
        SubmitInScope(game);

        Graphics.EndFrameAndWait();
        FrameCapture capture = FrameDebugger.LastCapture!;
        Assert.True(capture.LeafEvents.Count > 0);
        for (int i = 0; i < capture.LeafEvents.Count; i++)
        {
            Assert.Equal(FrameDebuggerScopeKind.GameView, capture.LeafEvents[i].Scope);
            Assert.NotEqual("Paper UI", capture.LeafEvents[i].CommandBufferName);
        }
        Assert.Equal(0, capture.ScopeLeafTotals[(int)FrameDebuggerScopeKind.None]);
    }

    [Fact]
    public void FullFrame_CapturesUnscopedSubmissionsInsideFrame()
    {
        FrameDebugger.CaptureScope = FrameDebuggerCaptureScope.FullFrame;
        FrameDebugger.Enable();
        Graphics.BeginFrame();

        CommandBuffer editorUi = Graphics.GetCommandBuffer("Paper UI");
        editorUi.ClearRenderTarget(ClearFlags.Color, Color.Black);
        Graphics.Submit(editorUi);

        Graphics.EndFrameAndWait();
        FrameCapture capture = FrameDebugger.LastCapture!;
        Assert.Equal(FrameDebuggerCaptureScope.FullFrame, capture.CaptureScope);
        Assert.True(capture.LeafEvents.Count > 0);
        Assert.Equal(FrameDebuggerScopeKind.EditorFrame, capture.LeafEvents[0].Scope);
    }

    [Fact]
    public void NoteLeafGpuOpAndShouldSkip_RespectsSelectionPerScope()
    {
        FrameDebugger.Enable();
        Graphics.BeginFrame();
        using GraphicsVertexArray vao = CreateDummyVao();
        CommandBuffer cmd = Graphics.GetCommandBuffer("SkipTest");
        cmd.ClearRenderTarget(ClearFlags.Color, Color.Black);
        cmd.DrawArrays(vao, Topology.Triangles, 0, 3);
        cmd.DrawArrays(vao, Topology.Triangles, 0, 3);
        SubmitInScope(cmd);
        Graphics.EndFrameAndWait();

        FrameDebugger.SelectedEventIndex = 1;
        Graphics.BeginFrame();
        Assert.False(FrameDebugger.IsCapturing);

        // Unscoped (editor) ops never count, never skip — regardless of selection.
        Assert.False(FrameDebugger.NoteLeafGpuOpAndShouldSkip(FrameDebuggerScopeKind.None));
        Assert.False(FrameDebugger.NoteLeafGpuOpAndShouldSkip(FrameDebuggerScopeKind.None));
        Assert.False(FrameDebugger.NoteLeafGpuOpAndShouldSkip(FrameDebuggerScopeKind.None));
        Assert.False(FrameDebugger.NoteLeafGpuOpAndShouldSkip(FrameDebuggerScopeKind.None));

        // Game-scope ops truncate after the selected event's per-scope prefix.
        Assert.False(FrameDebugger.NoteLeafGpuOpAndShouldSkip(FrameDebuggerScopeKind.GameView));
        Assert.False(FrameDebugger.NoteLeafGpuOpAndShouldSkip(FrameDebuggerScopeKind.GameView));
        Assert.True(FrameDebugger.NoteLeafGpuOpAndShouldSkip(FrameDebuggerScopeKind.GameView));

        // A scope absent from the capture (scene view here) is never truncated — a scene view
        // that starts repainting after the capture must render normally, not go black.
        Assert.False(FrameDebugger.NoteLeafGpuOpAndShouldSkip(FrameDebuggerScopeKind.SceneView));
    }

    [Fact]
    public void Capture_CollectsNamedRenderTargetsWithLiveTexture()
    {
        FrameDebugger.Enable();
        Graphics.BeginFrame();

        var rt = new Resources.RenderTexture(8, 8, false, [TextureImageFormat.Color4b]);
        try
        {
            rt.SetDebugName("_TestColor");
            CommandBuffer cmd = Graphics.GetCommandBuffer("RTPass");
            cmd.SetRenderTarget(rt.frameBuffer);
            cmd.ClearRenderTarget(ClearFlags.Color, Color.Black);
            cmd.SetRenderTarget(null);
            SubmitInScope(cmd);
            Graphics.EndFrameAndWait();

            FrameCapture capture = FrameDebugger.LastCapture!;
            Assert.Single(capture.RenderTargets);
            Assert.Equal("_TestColor", capture.RenderTargets[0].Info.Name);
            Assert.Same(rt, capture.RenderTargets[0].Texture);

            bool labeled = false;
            for (int i = 0; i < capture.LeafEvents.Count; i++)
                if (capture.LeafEvents[i].Label == "SetRenderTarget _TestColor")
                    labeled = true;
            Assert.True(labeled);
        }
        finally
        {
            FrameDebugger.Disable(); // drop the capture's strong ref before disposing the RT
            rt.Dispose();
        }
    }

    [Fact]
    public void DrawAnnotations_PairByStreamOffset_NotPosition()
    {
        FrameDebugger.Enable();
        Graphics.BeginFrame();
        using GraphicsVertexArray vao = CreateDummyVao();
        CommandBuffer cmd = Graphics.GetCommandBuffer("AnnPair");

        // Raw (unannotated) draw first — positional pairing would wrongly hand it the annotation.
        cmd.DrawArrays(vao, Topology.Triangles, 0, 3);

        // Annotated draw: the record lands at the stream offset just before the draw opcode,
        // exactly like production draw paths.
        cmd.AddDrawAnnotation(new DrawAnnotation { MeshName = "Skybox" });
        cmd.DrawArrays(vao, Topology.Triangles, 0, 3);

        // Raw draw after — must not inherit the consumed annotation.
        cmd.DrawArrays(vao, Topology.Triangles, 0, 3);

        SubmitInScope(cmd);
        Graphics.EndFrameAndWait();

        FrameCapture capture = FrameDebugger.LastCapture!;
        var draws = new System.Collections.Generic.List<FrameEvent>();
        for (int i = 0; i < capture.LeafEvents.Count; i++)
            if (capture.LeafEvents[i].Kind == FrameEventKind.Draw)
                draws.Add(capture.LeafEvents[i]);

        Assert.Equal(3, draws.Count);
        Assert.Null(draws[0].Annotation);
        Assert.Equal("Skybox", draws[1].Annotation?.MeshName);
        Assert.Equal("Draw Mesh Skybox", draws[1].Label);
        Assert.Null(draws[2].Annotation);
    }

    [Fact]
    public void Decoder_ShowInTree_MatchesOpClassifier()
    {
        FrameDebugger.Enable();
        Graphics.BeginFrame();
        using GraphicsVertexArray vao = CreateDummyVao();
        CommandBuffer cmd = Graphics.GetCommandBuffer("ClassifierParity");
        cmd.SetRenderTarget(null);
        cmd.SetViewport(0, 0, 8, 8);
        cmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, Color.Black);
        cmd.SetRasterState(new RasterizerState());
        cmd.SetGlobalFloat("_X", 1f);
        cmd.DrawArrays(vao, Topology.Triangles, 0, 3);
        cmd.DrawIndexed(vao, Topology.Triangles, 3, 0, 0, false);
        SubmitInScope(cmd);
        Graphics.EndFrameAndWait();

        FrameCapture capture = FrameDebugger.LastCapture!;
        Assert.True(capture.Ops.Count > 0);
        for (int i = 0; i < capture.Ops.Count; i++)
        {
            CapturedOp op = capture.Ops[i];
            Assert.Equal(FrameDebuggerOpClassifier.IsLeafGpuOp(op.Opcode), op.ShowInTree);
        }
    }

    [Fact]
    public void Labels_DescribeKind_MatchesUnityStyle()
    {
        var clearOp = new CapturedOp { ClearFlags = ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil };
        var clearEv = new FrameEvent { Kind = FrameEventKind.Clear };
        Assert.Equal("Clear (color+depth+stencil)", FrameDebuggerLabels.DescribeKind(clearEv, clearOp));

        var meshEv = new FrameEvent
        {
            Kind = FrameEventKind.Draw,
            Annotation = new DrawAnnotation { MeshName = "Cube" },
        };
        Assert.Equal("Draw Mesh", FrameDebuggerLabels.DescribeKind(meshEv, new CapturedOp { Opcode = CommandOpcode.DrawIndexed }));

        var procEv = new FrameEvent { Kind = FrameEventKind.Draw };
        Assert.Equal("Draw Procedural", FrameDebuggerLabels.DescribeKind(procEv, new CapturedOp { Opcode = CommandOpcode.DrawArrays }));
    }

    private static void SubmitInScope(
        CommandBuffer cmd, FrameDebuggerScopeKind scope = FrameDebuggerScopeKind.GameView)
    {
        FrameDebugger.BeginTargetScope(scope);
        try
        {
            Graphics.Submit(cmd);
        }
        finally
        {
            FrameDebugger.EndTargetScope();
        }
    }

    private static bool HasKind(FrameCapture capture, FrameEventKind kind)
    {
        for (int i = 0; i < capture.LeafEvents.Count; i++)
            if (capture.LeafEvents[i].Kind == kind) return true;
        return false;
    }

    private static int IndexOfKind(FrameCapture capture, FrameEventKind kind)
    {
        for (int i = 0; i < capture.LeafEvents.Count; i++)
            if (capture.LeafEvents[i].Kind == kind) return i;
        return -1;
    }

    private static GraphicsVertexArray CreateDummyVao()
    {
        var format = new VertexFormat([new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3)]);
        byte[] data = new byte[36];
        var verts = new GraphicsBuffer(BufferType.VertexBuffer, data, dynamic: false);
        return new GraphicsVertexArray(format, verts, null);
    }
}
