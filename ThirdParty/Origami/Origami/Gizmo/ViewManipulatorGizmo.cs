// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.OrigamiUI.Gizmo;

/// <summary>
/// Unity-style scene orientation gizmo: RGB axes with X/Y/Z labels, a small cube at the origin,
/// and a Persp/Iso caption. Click an axis to snap the camera; click the caption to toggle projection.
/// </summary>
public class ViewManipulatorGizmo
{
    // Fractions of the widget radius. All six handles sit at one distance, the way points on a
    // sphere do, and HandleRing keeps the largest of them off the backdrop's rim.
    private const float HandleRing = 0.70f;
    private const float PositiveRadius = 0.18f;
    private const float NegativeRadius = 0.17f;
    private const float LabelSize = 0.26f;

    // Slightly wider than the discs themselves, so a click that grazes the edge still lands.
    private const float PositiveHitRadius = 0.22f;
    private const float NegativeHitRadius = 0.21f;

    // An axis is unusable once its projection is this short, and starts fading here.
    private const float FadeEnd = 0.10f;
    private const float FadeStart = 0.34f;

    private static readonly Float3[] Axes =
    [
        Float3.UnitX, Float3.UnitY, Float3.UnitZ,
        -Float3.UnitX, -Float3.UnitY, -Float3.UnitZ,
    ];

    private static readonly Color32[] AxisColors =
    [
        Color32.FromArgb(255, 226, 55, 56),
        Color32.FromArgb(255, 94, 234, 141),
        Color32.FromArgb(255, 39, 117, 255),
    ];

    private static readonly string[] AxisLabels = ["X", "Y", "Z"];

    private struct Handle
    {
        public Float3 Axis;
        public Float2 Tip;
        public Float2 Direction;
        public float Depth;
        public float Planar;
        public float Alpha;
        public int AxisIndex;
        public bool Positive;
    }

    private readonly Handle[] _handles = new Handle[Axes.Length];

    private Rect _rect;
    private Float3 _camForward = Float3.UnitZ;
    private Float3 _camUp = Float3.UnitY;
    private FontFile? _font;
    private bool _isHovering;

    /// <summary>True while the cursor is over the widget, so the caller can hold off camera drags.</summary>
    public bool IsOver => _isHovering;

    /// <summary>
    /// Set by the last <see cref="Update"/> when the click landed on the widget but not on a handle.
    /// The caller decides what that means; the scene view swaps the camera's projection.
    /// </summary>
    public bool BackgroundClicked { get; private set; }

    public void SetRect(Rect rect) => _rect = rect;
    public void SetCamera(Float3 camForward, Float3 camUp)
    {
        _camForward = camForward;
        _camUp = camUp;
    }

    /// <summary>Font for the axis letters. Without one the arrows draw unlabelled.</summary>
    public void SetFont(FontFile? font) => _font = font;

    /// <summary>
    /// Draw the gizmo and hit-test the pointer. Returns true when a left-click should change the view:
    /// either snap <paramref name="newCamForward"/> or toggle ortho via <paramref name="toggleOrtho"/>.
    /// </summary>
    public bool Update(XEngine.Quill.Canvas canvas, FontFile? font, Float2 mousePos, bool mouseClicked,
        bool blockPicking, bool isOrthographic, out Float3 newCamForward, out bool toggleOrtho)
    {
        newCamForward = _camForward;
        toggleOrtho = false;

        float width = (float)_rect.Size.X;
        float height = (float)_rect.Size.Y;
        if (width < 8f || height < 8f) return false;

        // Caption sits in the bottom strip; the cube/axes use the square above it.
        float captionH = 16f;
        var cubeRect = new Rect(_rect.Min, new Float2(_rect.Max.X, _rect.Max.Y - captionH));
        float size = (float)Math.Min(cubeRect.Size.X, cubeRect.Size.Y);
        Float2 center = new(
            (float)(cubeRect.Min.X + cubeRect.Size.X / 2),
            (float)(cubeRect.Min.Y + cubeRect.Size.Y / 2));
        float radius = size * 0.40f;

        canvas.CircleFilled((float)center.X, (float)center.Y, radius + 5f,
            Color32.FromArgb(140, 28, 28, 32), 48);

        Float3 up = _camUp;
        if (Float3.LengthSquared(up) < 1e-8f
            || MathF.Abs(Float3.Dot(Float3.Normalize(_camForward), Float3.Normalize(up))) > 0.98f)
            up = MathF.Abs(_camForward.Y) > 0.9f ? Float3.UnitZ : Float3.UnitY;

        Float3 eye = -_camForward * 6f;
        var view = Float4x4.CreateLookAt(eye, Float3.Zero, up);
        var proj = Float4x4.CreateOrthoOffCenter(-1.85f, 1.85f, -1.85f, 1.85f, 0.1f, 20f);
        var vp = proj * view;

        DrawCube(canvas, cubeRect, vp);

        var axes = new (Float3 dir, string label, Color32 color, bool negative)[]
        {
            (Float3.UnitX, "X", new Color32(226, 55, 56, 255), false),
            (Float3.UnitY, "Y", new Color32(94, 234, 141, 255), false),
            (Float3.UnitZ, "Z", new Color32(39, 117, 255, 255), false),
            (-Float3.UnitX, "", new Color32(226, 55, 56, 180), true),
            (-Float3.UnitY, "", new Color32(94, 234, 141, 180), true),
            (-Float3.UnitZ, "", new Color32(39, 117, 255, 180), true),
        };

        Array.Sort(axes, (a, b) =>
        {
            float da = Float3.Dot(a.dir, -_camForward);
            float db = Float3.Dot(b.dir, -_camForward);
            return da.CompareTo(db);
        });

        int hoverAxis = -1;
        Float3 hoverForward = _camForward;
        float hoverBest = float.MaxValue;

        for (int i = 0; i < axes.Length; i++)
        {
            var (dir, label, color, negative) = axes[i];
            Float2? tipN = GizmoUtils.WorldToScreen(cubeRect, vp, dir);
            Float2? originN = GizmoUtils.WorldToScreen(cubeRect, vp, Float3.Zero);
            if (tipN == null || originN == null) continue;

            Float2 origin = originN.Value;
            Float2 tip = tipN.Value;
            DrawAxis(canvas, font, origin, tip, label, color, negative);

            if (blockPicking) continue;
            float hitR = negative ? 7f : 12f;
            float dist = Float2.Length(mousePos - tip);
            if (dist < hitR && dist < hoverBest)
            {
                hoverBest = dist;
                hoverAxis = i;
                // Clicking +X looks along -X (Right), matching Unity's scene gizmo.
                hoverForward = -dir;
            }
        }

        bool hoveringCaption = false;
        float capX = (float)center.X;
        float capY = (float)(_rect.Min.Y + _rect.Size.Y) - 10f;
        string caption = isOrthographic ? "Iso" : "Persp";
        if (font != null)
        {
            canvas.DrawText(caption, capX, capY,
                Color32.FromArgb(230, 210, 210, 214), 11f, font, origin: new Float2(0.5f, 0.5f));
        }

        if (!blockPicking && MathF.Abs(mousePos.X - capX) < 22f && MathF.Abs(mousePos.Y - capY) < 9f)
            hoveringCaption = true;

        float distToCenter = Float2.Length(mousePos - center);
        bool overWidget = distToCenter < radius + 10f || hoveringCaption || hoverAxis >= 0
            || GizmoUtils.IsPointInPolygon(mousePos, RectCorners(_rect));
        if (overWidget && !blockPicking)
            _isHovering = true;

        if (hoverAxis >= 0)
        {
            Float2? tip = GizmoUtils.WorldToScreen(cubeRect, vp, axes[hoverAxis].dir);
            if (tip != null)
                canvas.CircleFilled((float)tip.Value.X, (float)tip.Value.Y, 13f,
                    Color32.FromArgb(50, 255, 255, 255), 24);
        }

        if (!mouseClicked || blockPicking || !_isHovering)
            return false;

        if (hoveringCaption)
        {
            toggleOrtho = true;
            return true;
        }

        if (hoverAxis >= 0)
        {
            newCamForward = hoverForward;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The disc the handles sit on, which lifts on hover.
    /// </summary>
    /// <remarks>
    /// It lifts most when the cursor is on the backdrop itself rather than on a handle, because that
    /// is the state where clicking does something of its own: the backdrop is what swaps the
    /// projection, and nothing else says so.
    /// </remarks>
    private void DrawBackdrop(XEngine.Quill.Canvas canvas, Float2 center, float radius, int hovered)
    {
        byte alpha = !_isHovering ? (byte)120 : hovered >= 0 ? (byte)160 : (byte)200;
        canvas.CircleFilled((float)center.X, (float)center.Y, radius, Color32.FromArgb(alpha, 25, 25, 30), 48);

        if (_isHovering && hovered < 0)
            canvas.CircleFilled((float)center.X, (float)center.Y, radius, Color32.FromArgb(22, 255, 255, 255), 48);
    }

    private void DrawCube(XEngine.Quill.Canvas canvas, Rect viewport, Float4x4 vp)
    {
        const float s = 0.28f;
        Float3[] cubeVerts =
        [
            new(-s, -s,  s), new( s, -s,  s), new( s,  s,  s), new(-s,  s,  s),
            new(-s, -s, -s), new( s, -s, -s), new( s,  s, -s), new(-s,  s, -s),
        ];

        int[][] faces = [[0, 1, 2, 3], [1, 5, 6, 2], [5, 4, 7, 6], [4, 0, 3, 7], [3, 2, 6, 7], [4, 5, 1, 0]];
        Float3[] faceNormals =
        [
            Float3.UnitZ, Float3.UnitX, -Float3.UnitZ, -Float3.UnitX, Float3.UnitY, -Float3.UnitY,
        ];
        Color32[] faceColors =
        [
            new Color32(70, 78, 96, 255),
            new Color32(96, 62, 62, 255),
            new Color32(70, 78, 96, 255),
            new Color32(96, 62, 62, 255),
            new Color32(62, 96, 74, 255),
            new Color32(62, 96, 74, 255),
        ];

        for (int i = 0; i < faces.Length; i++)
        {
            float dot = Float3.Dot(faceNormals[i], -_camForward);
            if (dot <= 0.01f) continue;

            var screenPts = new List<Float2>();
            foreach (int vi in faces[i])
            {
                var sp = GizmoUtils.WorldToScreen(viewport, vp, cubeVerts[vi]);
                if (sp != null) screenPts.Add(sp.Value);
            }
            if (screenPts.Count < 3) continue;

            byte alpha = (byte)(160 + (int)(dot * 80));
            var col = new Color32(faceColors[i].R, faceColors[i].G, faceColors[i].B, alpha);

            canvas.BeginPath();
            canvas.MoveTo((float)screenPts[0].X, (float)screenPts[0].Y);
            for (int j = 1; j < screenPts.Count; j++)
                canvas.LineTo((float)screenPts[j].X, (float)screenPts[j].Y);
            canvas.ClosePath();
            canvas.SetFillColor(col);
            canvas.Fill();

            canvas.SetStrokeColor(new Color32(20, 20, 22, 180));
            canvas.SetStrokeWidth(1f);
            canvas.Stroke();
        }
    }

    private static void DrawAxis(XEngine.Quill.Canvas canvas, FontFile? font, Float2 origin, Float2 tip,
        string label, Color32 color, bool negative)
    {
        Float2 dir = tip - origin;
        float len = Float2.Length(dir);
        if (len < 1f) return;
        Float2 nrm = dir / len;

        if (negative)
        {
            canvas.CircleFilled((float)tip.X, (float)tip.Y, 4.5f, color, 16);
            return;
        }

        Float2 shaftEnd = tip - nrm * 9f;
        canvas.BeginPath();
        canvas.MoveTo((float)origin.X, (float)origin.Y);
        canvas.LineTo((float)shaftEnd.X, (float)shaftEnd.Y);
        canvas.SetStrokeColor(color);
        canvas.SetStrokeWidth(2.4f);
        canvas.Stroke();

        Float2 perp = new Float2(-nrm.Y, nrm.X);
        Float2 a = shaftEnd + perp * 4.2f;
        Float2 b = shaftEnd - perp * 4.2f;
        canvas.BeginPath();
        canvas.MoveTo((float)tip.X, (float)tip.Y);
        canvas.LineTo((float)a.X, (float)a.Y);
        canvas.LineTo((float)b.X, (float)b.Y);
        canvas.ClosePath();
        canvas.SetFillColor(color);
        canvas.Fill();

        if (font != null && label.Length > 0)
        {
            Float2 textAt = tip + nrm * 10f;
            canvas.DrawText(label, (float)textAt.X, (float)textAt.Y, color, 11f, font,
                origin: new Float2(0.5f, 0.5f));
        }
    }

    private static List<Float2> RectCorners(Rect r) =>
    [
        new((float)r.Min.X, (float)r.Min.Y),
        new((float)r.Max.X, (float)r.Min.Y),
        new((float)r.Max.X, (float)r.Max.Y),
        new((float)r.Min.X, (float)r.Max.Y),
    ];
}
