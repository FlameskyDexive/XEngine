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

using PaperTextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

public sealed partial class FrameDebuggerPanel
{
    private void DrawDetails(Paper paper, FontFile font, float width, float height)
    {
        GpuStateSnapshot? snap = GetSelectedSnapshot();
        if (snap == null)
        {
            EditorGUI.EmptyState(paper, "fd_details_empty", "Select an event in the tree.", font);
            return;
        }

        FrameCapture? capture = FrameDebugger.LastCapture;
        CapturedOp? op = GetSelectedOp(capture);
        FrameEvent? ev = GetSelectedEvent(capture);
        _rowOrdinal = 0;

        Origami.ScrollView(paper, "fd_details_scroll", width, height).Body(() =>
        {
            using (paper.Column("fd_details").Width(width - 8).Padding(8, 8, 6, 8).Enter())
            {
                DrawDetailsBox(paper, font, width - 24, snap, op, ev);
                DrawPropertyFoldouts(paper, font, snap);
            }
        });
    }

    // ── The Unity "Details" key/value block (expanded by default) ────────────────

    private void DrawDetailsBox(
        Paper paper, FontFile font, float width, GpuStateSnapshot snap, CapturedOp? op, FrameEvent? ev)
    {
        FoldSection(paper, "fd_details_sec", "Details", () =>
        {
            RenderTargetInfo rt = snap.RenderTarget;
            KV(paper, font, "RenderTarget", rt.Name + (rt.IsTransient ? "  (transient)" : ""));
            KV(paper, font, "Size", $"{rt.Width}x{rt.Height}");
            KV(paper, font, "Format", rt.ColorAttachmentCount > 1
                ? $"{rt.ColorFormat} x{rt.ColorAttachmentCount}"
                : (string.IsNullOrEmpty(rt.ColorFormat) ? "-" : rt.ColorFormat));
            if (!string.IsNullOrEmpty(rt.DepthFormat))
                KV(paper, font, "Depth Format", rt.DepthFormat);
            KV(paper, font, "Color Actions", $"{snap.ColorLoadAction} / Store");
            KV(paper, font, "Depth Actions", $"{snap.DepthLoadAction} / Store");

            switch (snap.EventKind)
            {
                case FrameEventKind.Clear:
                    DrawClearDetails(paper, font, snap, op);
                    break;
                case FrameEventKind.Draw:
                case FrameEventKind.DrawInstanced:
                    DrawDrawDetails(paper, font, snap, ev);
                    break;
                case FrameEventKind.Blit:
                    KV(paper, font, "Blit", snap.EventLabel);
                    break;
                case FrameEventKind.Dispatch:
                    KV(paper, font, "Dispatch", snap.EventLabel);
                    if (!string.IsNullOrEmpty(snap.ShaderLabel))
                        KV(paper, font, "Compute Shader", snap.ShaderLabel);
                    break;
                default:
                    KV(paper, font, "Event", snap.EventLabel);
                    break;
            }

            KV(paper, font, "Command Buffer", snap.CommandBufferName ?? "-");
            uint vw = snap.ViewportW == 0 ? rt.Width : snap.ViewportW;
            uint vh = snap.ViewportH == 0 ? rt.Height : snap.ViewportH;
            KV(paper, font, "Viewport", $"({snap.ViewportX}, {snap.ViewportY}, {vw}, {vh})");
            if (snap.ScissorEnabled)
                KV(paper, font, "Scissor", "enabled");
        }, defaultExpanded: true);
    }

    private void DrawClearDetails(Paper paper, FontFile font, GpuStateSnapshot snap, CapturedOp? op)
    {
        if (op == null) return;
        if ((op.ClearFlags & ClearFlags.Color) != 0)
        {
            var c = op.ClearColor;
            KV(paper, font, "Clear Color", $"({c.R:0.00}, {c.G:0.00}, {c.B:0.00}, {c.A:0.00})");
        }
        if ((op.ClearFlags & ClearFlags.Depth) != 0)
            KV(paper, font, "Clear Depth", $"{op.ClearDepth:0.000}");
        if ((op.ClearFlags & ClearFlags.Stencil) != 0)
            KV(paper, font, "Clear Stencil", FormatStencil(op.ClearStencil));
    }

    private void DrawDrawDetails(Paper paper, FontFile font, GpuStateSnapshot snap, FrameEvent? ev)
    {
        DrawAnnotation? ann = snap.Annotation ?? ev?.Annotation;

        string shader = ann != null && !string.IsNullOrEmpty(ann.ShaderName)
            ? ann.ShaderName
            : snap.ShaderAssetName;
        if (!string.IsNullOrEmpty(shader))
            KV(paper, font, "Shader", shader);

        string pass = ann != null && !string.IsNullOrEmpty(ann.PassName)
            ? $"{ann.PassName} (#{ann.PassIndex})"
            : snap.ShaderPassName;
        if (!string.IsNullOrEmpty(pass))
            KV(paper, font, "Pass", pass);

        if (ann != null && !string.IsNullOrEmpty(ann.MaterialName))
            KV(paper, font, "Material", ann.MaterialName);
        if (ann != null && !string.IsNullOrEmpty(ann.ObjectName))
            KV(paper, font, "GameObject", ann.ObjectName);

        string keywords = ann is { Keywords.Length: > 0 }
            ? string.Join(" ", ann.Keywords)
            : snap.VariantKeywords;
        if (!string.IsNullOrEmpty(keywords))
            KV(paper, font, "Keywords", keywords);

        RasterizerState raster = snap.Raster;
        KV(paper, font, "Blend", raster.DoBlend
            ? $"{raster.BlendSrc} {raster.BlendDst} ({raster.Blend})"
            : "Off");
        KV(paper, font, "ZTest", raster.DepthTest ? raster.Depth.ToString() : "Off");
        KV(paper, font, "ZWrite", raster.DepthWrite ? "On" : "Off");
        KV(paper, font, "Cull", raster.CullFace.ToString());
        if (raster.StencilEnabled)
            KV(paper, font, "Stencil", $"{raster.StencilFunc} Ref {raster.StencilRef}");

        KV(paper, font, "Topology", snap.Topology.ToString());
        KV(paper, font, "Indices", snap.IndexCount.ToString());
        if (snap.InstanceCount > 1)
            KV(paper, font, "Instances", snap.InstanceCount.ToString());
        if (!string.IsNullOrEmpty(snap.ShaderLabel))
            KV(paper, font, "Variant", snap.ShaderLabel);
    }

    // ── Property foldouts (Unity section order) ──────────────────────────────────

    private void DrawPropertyFoldouts(Paper paper, FontFile font, GpuStateSnapshot snap)
    {
        FoldSection(paper, "fd_meshes", "Meshes", () =>
        {
            KV(paper, font, "Mesh", snap.Annotation?.MeshName is { Length: > 0 } m ? m : "(none)");
            KV(paper, font, "Topology", snap.Topology.ToString());
            KV(paper, font, "Indices", snap.IndexCount.ToString());
            KV(paper, font, "Instances", Math.Max(1u, snap.InstanceCount).ToString());
        });

        FoldSection(paper, "fd_kw", "Keywords", () =>
        {
            if (snap.Annotation?.Keywords is { Length: > 0 } kws)
            {
                for (int i = 0; i < kws.Length; i++)
                    Line(paper, font, kws[i]);
            }
            else if (!string.IsNullOrEmpty(snap.VariantKeywords))
                Line(paper, font, snap.VariantKeywords);
            else
                Line(paper, font, "(none)");
        });

        FoldSection(paper, "fd_tex", "Textures", () =>
        {
            bool any = false;
            if (snap.MaterialProperties != null)
            {
                foreach (KeyValuePair<string, AssetRef<Texture2D>> kv in snap.MaterialProperties._textures)
                {
                    Texture2D? tex = kv.Value.Res;
                    KV(paper, font, kv.Key, tex == null ? "(null)" : $"{tex.Name} {tex.Width}x{tex.Height}");
                    any = true;
                }
            }
            foreach (KeyValuePair<string, string> kv in snap.UniformTextures)
            {
                KV(paper, font, kv.Key, kv.Value);
                any = true;
            }
            if (!any) Line(paper, font, "(none)");
        });

        FoldSection(paper, "fd_vals", "Ints/Floats/Vectors/Matrices", () =>
        {
            bool any = false;
            if (snap.MaterialProperties != null)
            {
                foreach (KeyValuePair<string, int> kv in snap.MaterialProperties._ints)
                { KV(paper, font, kv.Key, kv.Value.ToString()); any = true; }
                foreach (KeyValuePair<string, float> kv in snap.MaterialProperties._floats)
                { KV(paper, font, kv.Key, $"{kv.Value:0.####}"); any = true; }
                foreach (KeyValuePair<string, Float2> kv in snap.MaterialProperties._vectors2)
                { KV(paper, font, kv.Key, FormatVec2(kv.Value)); any = true; }
                foreach (KeyValuePair<string, Float3> kv in snap.MaterialProperties._vectors3)
                { KV(paper, font, kv.Key, FormatVec3(kv.Value)); any = true; }
                foreach (KeyValuePair<string, Float4> kv in snap.MaterialProperties._vectors4)
                { KV(paper, font, kv.Key, FormatVec4(kv.Value)); any = true; }
                foreach (KeyValuePair<string, Float4x4> kv in snap.MaterialProperties._matrices)
                { KV(paper, font, kv.Key, "(matrix)"); any = true; }
            }
            foreach (KeyValuePair<string, int> kv in snap.UniformInts)
            { KV(paper, font, kv.Key, kv.Value.ToString()); any = true; }
            foreach (KeyValuePair<string, float> kv in snap.UniformFloats)
            { KV(paper, font, kv.Key, $"{kv.Value:0.####}"); any = true; }
            foreach (KeyValuePair<string, Float2> kv in snap.UniformVec2)
            { KV(paper, font, kv.Key, FormatVec2(kv.Value)); any = true; }
            foreach (KeyValuePair<string, Float3> kv in snap.UniformVec3)
            { KV(paper, font, kv.Key, FormatVec3(kv.Value)); any = true; }
            foreach (KeyValuePair<string, Float4> kv in snap.UniformVec4)
            { KV(paper, font, kv.Key, FormatVec4(kv.Value)); any = true; }
            foreach (KeyValuePair<string, Float4x4> kv in snap.UniformMatrices)
            { KV(paper, font, kv.Key, "(matrix)"); any = true; }
            foreach (KeyValuePair<string, int> kv in snap.Globals._ints)
            { KV(paper, font, "(global) " + kv.Key, kv.Value.ToString()); any = true; }
            foreach (KeyValuePair<string, float> kv in snap.Globals._floats)
            { KV(paper, font, "(global) " + kv.Key, $"{kv.Value:0.####}"); any = true; }
            if (!any) Line(paper, font, "(none)");
        });

        FoldSection(paper, "fd_bufs", "Buffers", () =>
        {
            bool any = false;
            foreach (KeyValuePair<string, string> kv in snap.UniformBuffers)
            {
                if (!kv.Key.StartsWith("global:", StringComparison.Ordinal))
                    continue;
                KV(paper, font, kv.Key["global:".Length..], kv.Value);
                any = true;
            }
            if (!any) Line(paper, font, "(none)");
        });

        FoldSection(paper, "fd_cbuffers", "Constant Buffers", () =>
        {
            bool any = false;
            foreach (KeyValuePair<string, string> kv in snap.UniformBuffers)
            {
                if (kv.Key.StartsWith("global:", StringComparison.Ordinal))
                    continue;
                KV(paper, font, kv.Key, kv.Value);
                any = true;
            }
            if (!any) Line(paper, font, "(none)");
        });

        if (_showRasterSection)
        {
            RasterizerState raster = snap.Raster;
            FoldSection(paper, "fd_raster", "Raster State (XEngine)", () =>
            {
                KV(paper, font, "Blend", $"{raster.DoBlend}  Src {raster.BlendSrc}  Dst {raster.BlendDst}  Mode {raster.Blend}");
                KV(paper, font, "Depth", $"Test {raster.DepthTest}  Write {raster.DepthWrite}  Func {raster.Depth}");
                KV(paper, font, "Cull", $"{raster.CullFace}  Winding {raster.Winding}");
                KV(paper, font, "Stencil", $"{raster.StencilEnabled}  Func {raster.StencilFunc}  Ref {raster.StencilRef}");
                KV(paper, font, "Stencil Ops", $"Pass {raster.StencilPassOp}  Fail {raster.StencilFailOp}  ZFail {raster.StencilZFailOp}");
            });
        }
    }

    // ── Row helpers (deterministic ordinal IDs — no text-hash collisions) ────────

    private void FoldSection(Paper paper, string id, string title, Action body, bool defaultExpanded = false)
    {
        bool expanded = _foldStates.TryGetValue(id, out bool stored) ? stored : defaultExpanded;
        Origami.Foldout(paper, id, title)
            .Expanded(expanded, v => _foldStates[id] = v)
            .Body(body);
    }

    private void KV(Paper paper, FontFile font, string key, string value)
    {
        int ordinal = _rowOrdinal++;
        using (paper.Row($"fd_kv_{ordinal}").Width(UnitValue.StretchOne).Height(18).Enter())
        {
            paper.Box($"fd_kvk_{ordinal}").Width(150)
                .Text(key, font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft);
            paper.Box($"fd_kvv_{ordinal}").Width(UnitValue.StretchOne)
                .Text(value, EditorTheme.FontMono ?? font).TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft);
        }
    }

    private void Line(Paper paper, FontFile font, string text)
    {
        int ordinal = _rowOrdinal++;
        paper.Box($"fd_line_{ordinal}").Width(UnitValue.StretchOne).Height(18)
            .Text(text, font).TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft);
    }

    private static string FormatStencil(int stencil) =>
        $"(0b{Convert.ToString(stencil & 0xFF, 2).PadLeft(8, '0')}) {stencil}";

    private static string FormatVec2(Float2 v) => $"{v.X:0.###}, {v.Y:0.###}";
    private static string FormatVec3(Float3 v) => $"{v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}";
    private static string FormatVec4(Float4 v) => $"{v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}, {v.W:0.###}";
}
