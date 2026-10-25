// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Spatial;

using PaperTextAlignment = XEngine.PaperUI.TextAlignment;
using Color32 = XEngine.Vector.Color32;

namespace XEngine.Editor.GUI.Panels;

public sealed partial class FrameDebuggerPanel
{
    private const float EventTitleHeight = 24f;
    private const float TabsHeight = 26f;
    private const float SubToolbarHeight = 28f;

    // Channel/levels preview runs through a FrameDebugPreview blit into a panel-owned RT —
    // the Quill canvas then draws that RT plain (PaperRenderer can't bind custom shaders).
    private Material? _previewMaterial;
    private RenderTexture? _previewRt;

    // Mesh Preview tab: an isolated PreviewRenderer scene showing the selected draw's mesh,
    // optionally with a Lines edge-mesh wireframe overlay.
    private PreviewRenderer? _meshPreview;
    private Mesh? _meshPreviewMesh;
    private bool _meshPreviewWire;
    private int _meshDisplayMode; // 0=Shaded 1=Shaded+Wireframe

    private void DrawRightColumn(Paper paper, FontFile font, float width, float height)
    {
        FrameCapture? capture = FrameDebugger.LastCapture;
        FrameEvent? ev = GetSelectedEvent(capture);
        if (capture == null || ev == null)
        {
            using (paper.Column("fd_right").Width(width).Height(height).Enter())
                EditorGUI.EmptyState(paper, "fd_right_empty", "Select an event in the tree.", font);
            return;
        }

        CapturedOp? op = GetSelectedOp(capture);
        using (paper.Column("fd_right").Width(width).Height(height).Enter())
        {
            DrawEventTitle(paper, font, width, ev, op);
            DrawOutputTabs(paper, width, ev);

            float previewH = Math.Clamp(_previewHeight, PreviewMinHeight, Math.Max(PreviewMinHeight, height - 200f));
            using (paper.Column("fd_preview").Width(width).Height(previewH).Enter())
            {
                float imgH = previewH - SubToolbarHeight;
                if (imgH < 40f) imgH = 40f;
                try
                {
                    if (_outputTab == 0)
                    {
                        DrawOutputSubToolbar(paper, font, width, capture);
                        DrawOutputViewport(paper, font, width, imgH, capture);
                    }
                    else
                    {
                        DrawMeshSubToolbar(paper, font, width, ev);
                        DrawMeshViewport(paper, font, width, imgH, ev);
                    }
                }
                catch (Exception ex)
                {
                    // A failed preview (shader compile, RT churn) must degrade to a message,
                    // never take the details pane down with it.
                    Runtime.Debug.LogError("[FrameDebugger] preview failed: " + ex);
                    paper.Box("fd_preview_error").Width(width - 12).Height(40)
                        .Text("Preview failed: " + ex.Message, font)
                        .TextColor(EditorTheme.AccentText).FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(PaperTextAlignment.MiddleCenter);
                }
            }

            DrawHSplitter(paper, width);
            float detailsH = height - EventTitleHeight - TabsHeight - previewH - SplitterSize;
            if (detailsH < 80f) detailsH = 80f;
            try
            {
                DrawDetails(paper, font, width, detailsH);
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError("[FrameDebugger] details failed: " + ex);
                paper.Box("fd_details_error").Width(width - 12).Height(40)
                    .Text("Details failed: " + ex.Message, font)
                    .TextColor(EditorTheme.AccentText).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(PaperTextAlignment.MiddleCenter);
            }
        }
    }

    // "Event #3 Clear (color+depth+stencil)"
    private static void DrawEventTitle(Paper paper, FontFile font, float width, FrameEvent ev, CapturedOp? op)
    {
        FontFile titleFont = EditorTheme.FontSemiBold ?? font;
        paper.Box("fd_evtitle").Width(width).Height(EventTitleHeight)
            .Text($"  Event #{ev.Index + 1} {FrameDebuggerLabels.DescribeKind(ev, op)}", titleFont)
            .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSizeLarge)
            .Alignment(PaperTextAlignment.MiddleLeft);
    }

    // [ Output | Mesh Preview ] joined full-width segmented control (Unity's two tab buttons).
    private void DrawOutputTabs(Paper paper, float width, FrameEvent ev)
    {
        bool isDraw = ev.Kind is FrameEventKind.Draw or FrameEventKind.DrawInstanced;
        using (paper.Row("fd_tabs_row").Width(width).Height(TabsHeight).Padding(4, 4, 1, 1).Enter())
        {
            var group = Origami.ButtonGroup(paper, "fd_tabs", _outputTab, i => _outputTab = i)
                .Joined().FullWidth()
                .Item("Output");
            if (isDraw)
                group.Item("Mesh Preview");
            else
                group.DisabledItem("Mesh Preview", tooltip: "Mesh preview is available for draw events");
            group.Show();
        }
        if (!isDraw)
            _outputTab = 0;
    }

    // [RT 0 ▾] Channels [All|R|G|B|A]  Levels [——■———■——]
    private void DrawOutputSubToolbar(Paper paper, FontFile font, float width, FrameCapture capture)
    {
        using (paper.Row("fd_prevbar").Width(width).Height(SubToolbarHeight).Padding(6, 6, 3, 3).RowBetween(4).Enter())
        {
            RebuildRtOptions(capture);
            if (_rtIndex >= _rtOptions.Count) _rtIndex = 0;
            Origami.Dropdown(paper, "fd_rt", _rtIndex, i => _rtIndex = i, _rtOptions.ToArray())
                .Width(150).Show();

            paper.Box("fd_ch_lbl").Width(56).Text("Channels", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft);

            Origami.ButtonGroup(paper, "fd_channels", _channelIndex, i => _channelIndex = i)
                .Joined()
                .Item("All").Item("R").Item("G").Item("B").Item("A")
                .Show();

            paper.Box("fd_lv_lbl").Width(40).Text("Levels", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft);
            Origami.RangeSlider(paper, "fd_levels", _levelsMin, _levelsMax,
                    (lo, hi) => { _levelsMin = lo; _levelsMax = hi; }, 0f, 1f)
                .ShowValue(false)
                .TrackGradient(System.Drawing.Color.Black, System.Drawing.Color.White)
                .Width(UnitValue.StretchOne).Show();
        }
    }

    private void DrawOutputViewport(Paper paper, FontFile font, float width, float height, FrameCapture capture)
    {
        Texture2D? source = ResolvePreviewTexture(capture, out string? note);
        Texture2D? display = source != null ? BlitPreview(source) : null;

        Texture2D? captured = display;
        paper.Box("fd_preview_img").Width(width - 12).Height(height)
            .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, rct) =>
            {
                float rx = (float)rct.Min.X, ry = (float)rct.Min.Y;
                float rw = (float)rct.Size.X, rh = (float)rct.Size.Y;
                DrawCheckerboard(canvas, rx, ry, rw, rh);

                if (captured == null || captured.IsDisposed)
                    return;

                float aspect = captured.Width / MathF.Max(1f, captured.Height);
                float drawW = rw - 8f;
                float drawH = drawW / aspect;
                if (drawH > rh - 8f) { drawH = rh - 8f; drawW = drawH * aspect; }
                float drawX = rx + (rw - drawW) * 0.5f;
                float drawY = ry + (rh - drawH) * 0.5f;

                canvas.SetBrushTexture(captured);
                canvas.SetBrushTextureTransform(
                    RenderTargetPreview.Transform(drawX, drawY, drawW, drawH));
                canvas.RectFilled(drawX, drawY, drawW, drawH, Color32.FromArgb(255, 255, 255, 255));
                canvas.ClearBrushTexture();
            }));

        if (display == null || note != null)
        {
            paper.Box("fd_preview_msg").Width(width - 12).Height(20)
                .Text(note ?? "No preview available for this target", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleCenter)
                .PositionType(PositionType.SelfDirected);
        }
    }

    /// <summary>Runs the channel/levels shader over the source into the panel-owned preview RT.
    /// Re-blits every panel frame: under truncated replay the source content changes each
    /// frame while its identity stays the same.</summary>
    private Texture2D? BlitPreview(Texture2D source)
    {
        if (source.IsDisposed || Graphics.IsHeadless)
            return null;

        // Never blit during the capture frame — in full-frame mode the panel's own preview
        // work would be recorded into the capture. Show the previous preview instead.
        if (FrameDebugger.IsCapturing)
            return TextureOf(_previewRt) ?? source;

        if (_previewMaterial == null)
        {
            Shader shader = Shader.LoadDefault(DefaultShader.FrameDebugPreview);
            if (!shader.IsValid()) return source;
            _previewMaterial = new Material(shader);
        }

        int w = (int)source.Width, h = (int)source.Height;
        if (_previewRt == null || _previewRt.IsDisposed || _previewRt.Width != w || _previewRt.Height != h)
        {
            if (_previewRt != null && !_previewRt.IsDisposed)
                Graphics.DeferDispose(_previewRt);
            _previewRt = new RenderTexture(w, h, false, [TextureImageFormat.Color4b]);
            _previewRt.SetDebugName("FrameDebugger preview");
        }

        _previewMaterial.SetFloat("_ChannelMode", _channelIndex);
        _previewMaterial.SetFloat("_LevelsMin", _levelsMin);
        _previewMaterial.SetFloat("_LevelsMax", _levelsMax);

        using CommandBuffer cmd = Graphics.GetCommandBuffer("FrameDebugger.Preview");
        cmd.Blit(source, _previewRt, _previewMaterial);
        Graphics.Submit(cmd);

        return TextureOf(_previewRt);
    }

    public override void OnClosed()
    {
        if (_previewRt is { IsDisposed: false })
            Graphics.DeferDispose(_previewRt);
        _previewRt = null;
        _previewMaterial = null;
        _meshPreview?.Dispose();
        _meshPreview = null;
        _meshPreviewMesh = null;
    }

    /// <summary>Preview source: index 0 = the selected event's bound target; N = capture RT N-1.
    /// A backbuffer target falls back to the Game view's texture (the closest live image).</summary>
    private Texture2D? ResolvePreviewTexture(FrameCapture capture, out string? note)
    {
        note = null;
        if (_rtIndex > 0)
        {
            if (_rtIndex - 1 >= capture.RenderTargets.Count)
                return null;
            CapturedRenderTarget crt = capture.RenderTargets[_rtIndex - 1];
            if (crt.Info.IsTransient)
                note = "transient RT — contents may have been reused";
            return TextureOf(crt.Texture);
        }

        GpuStateSnapshot? snap = GetSelectedSnapshot();
        if (snap == null)
            return null;
        RenderTargetInfo info = snap.RenderTarget;
        if (!info.IsBackbuffer)
        {
            IReadOnlyList<CapturedRenderTarget> targets = capture.RenderTargets;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].Info.Name != info.Name) continue;
                if (targets[i].Info.IsTransient)
                    note = "transient RT — contents may have been reused";
                return TextureOf(targets[i].Texture);
            }
            note = $"contents of {info.Name} are not available";
            return null;
        }

        var game = GameViewPanel.ActiveInstance;
        Texture2D? tex = game != null ? GameViewPanel.GetDisplayTexture(game.RenderTarget) : null;
        if (tex == null)
            note = "no Game view open — backbuffer contents unavailable";
        return tex;
    }

    private static Texture2D? TextureOf(RenderTexture? rt)
    {
        if (rt == null || rt.IsDisposed) return null;
        Texture2D tex = rt.MainTexture;
        return tex.IsDisposed ? null : tex;
    }

    /// <summary>RT dropdown: "RT 0" (bound target) + every distinct capture RT.</summary>
    private void RebuildRtOptions(FrameCapture capture)
    {
        if (ReferenceEquals(_cachedCapture, capture) && _rtOptions.Count > 0)
            return;
        _rtOptions.Clear();
        _rtOptions.Add("RT 0");
        IReadOnlyList<CapturedRenderTarget> targets = capture.RenderTargets;
        for (int i = 0; i < targets.Count; i++)
        {
            RenderTargetInfo info = targets[i].Info;
            _rtOptions.Add($"{info.Name} {info.Width}x{info.Height}");
        }
    }

    /// <summary>Unity behavior: the RT preview follows the selected event's render target.</summary>
    private void FollowSelectedRt()
    {
        _rtIndex = 0;
    }

    // ── Mesh Preview tab ─────────────────────────────────────────────────────────

    private void DrawMeshSubToolbar(Paper paper, FontFile font, float width, FrameEvent ev)
    {
        using (paper.Row("fd_meshbar").Width(width).Height(SubToolbarHeight).Padding(6, 6, 3, 3).RowBetween(4).Enter())
        {
            Origami.ButtonGroup(paper, "fd_mesh_mode", _meshDisplayMode, i => _meshDisplayMode = i)
                .Joined()
                .Item("Shaded").Item("Wireframe")
                .Show();

            GpuStateSnapshot? snap = GetSelectedSnapshot();
            Mesh? mesh = ev.Annotation?.Mesh.Res;
            string caption = mesh != null
                ? $"{mesh.Name}   {mesh.VertexCount} verts, {mesh.IndexCount} indices, {mesh.MeshTopology}"
                : ev.Annotation != null && !string.IsNullOrEmpty(ev.Annotation.MeshName)
                    ? $"{ev.Annotation.MeshName}   {snap?.IndexCount ?? 0} indices, {snap?.Topology}"
                    : "(no mesh identity for this draw)";
            paper.Box("fd_mesh_caption").Width(UnitValue.StretchOne)
                .Text(caption, font)
                .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft);
        }
    }

    private void DrawMeshViewport(Paper paper, FontFile font, float width, float height, FrameEvent ev)
    {
        Mesh? mesh = ev.Annotation?.Mesh.Res;
        if (mesh == null || mesh.IsDisposed)
        {
            paper.Box("fd_mesh_viewport").Width(width - 12).Height(height)
                .Text("No live mesh reference for this draw", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleCenter)
                .BackgroundColor(EditorTheme.Neutral100);
            return;
        }

        _meshPreview ??= new PreviewRenderer();
        bool wire = _meshDisplayMode == 1;
        if (!ReferenceEquals(_meshPreviewMesh, mesh) || _meshPreviewWire != wire)
        {
            _meshPreviewMesh = mesh;
            _meshPreviewWire = wire;
            _meshPreview.SetupForMesh(mesh, new Material(Shader.LoadDefault(DefaultShader.Standard)));
            if (wire)
                AttachWireframeOverlay(_meshPreview, mesh);
            _meshPreview.MarkPreviewDirty();
        }

        _meshPreview.DrawPreview(paper, "fd_mesh_preview", width - 12, height);
    }

    /// <summary>Adds a Lines edge mesh over the shaded subject (deduped triangle edges,
    /// capped so a dense mesh cannot stall the editor).</summary>
    private static void AttachWireframeOverlay(PreviewRenderer preview, Mesh mesh)
    {
        GameObject? subject = preview.SubjectGameObject;
        if (subject == null) return;

        Mesh? edges = BuildEdgeMesh(mesh);
        if (edges == null) return;

        var wireGo = new GameObject("PreviewWireframe") { HideFlags = HideFlags.HideAndDontSave };
        var renderer = wireGo.AddComponent<MeshRenderer>();
        renderer.Mesh = edges;
        var material = new Material(Shader.LoadDefault(DefaultShader.Unlit));
        material.SetColor("_Color", new Color(0.55f, 0.75f, 1f, 1f));
        renderer.Material = material;
        wireGo.SetParent(subject);
    }

    private static Mesh? BuildEdgeMesh(Mesh source)
    {
        const int maxEdges = 200_000;
        if (source.MeshTopology != Topology.Triangles)
            return null;
        Float3[] vertices = source.Vertices;
        uint[] indices = source.Indices;
        if (vertices.Length == 0 || indices.Length < 3)
            return null;

        var seen = new HashSet<ulong>();
        var edgeIndices = new List<uint>(Math.Min(indices.Length * 2, maxEdges * 2));
        for (int i = 0; i + 2 < indices.Length && seen.Count < maxEdges; i += 3)
        {
            AddEdge(indices[i], indices[i + 1], seen, edgeIndices);
            AddEdge(indices[i + 1], indices[i + 2], seen, edgeIndices);
            AddEdge(indices[i + 2], indices[i], seen, edgeIndices);
        }
        if (edgeIndices.Count < 2)
            return null;

        var edgeMesh = new Mesh
        {
            Vertices = vertices,
            Indices = edgeIndices.ToArray(),
            MeshTopology = Topology.Lines,
        };
        return edgeMesh;
    }

    private static void AddEdge(uint a, uint b, HashSet<ulong> seen, List<uint> indices)
    {
        ulong key = a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;
        if (!seen.Add(key)) return;
        indices.Add(a);
        indices.Add(b);
    }

    private static void DrawCheckerboard(XEngine.Quill.Canvas canvas, float rx, float ry, float rw, float rh)
    {
        const float cell = 10f;
        var ca = Color32.FromArgb(255, 44, 40, 54);
        var cb = Color32.FromArgb(255, 34, 30, 44);
        int cols = (int)MathF.Ceiling(rw / cell);
        int rows = (int)MathF.Ceiling(rh / cell);
        for (int cy = 0; cy < rows; cy++)
            for (int cx = 0; cx < cols; cx++)
            {
                float px = rx + cx * cell, py = ry + cy * cell;
                float cw = MathF.Min(cell, rx + rw - px), ch = MathF.Min(cell, ry + rh - py);
                canvas.RectFilled(px, py, cw, ch, ((cx + cy) & 1) == 0 ? ca : cb);
            }
    }
}
