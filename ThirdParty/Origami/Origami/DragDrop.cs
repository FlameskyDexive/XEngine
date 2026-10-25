// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;

using Color = System.Drawing.Color;

namespace XEngine.OrigamiUI;

/// <summary>
/// Base class for drag payloads. Subclass to carry any data type during a drag operation.
/// </summary>
public abstract class DragPayload
{
    /// <summary>Display name shown in the drag ghost tooltip.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Icon glyph shown in the drag ghost.</summary>
    public virtual string Icon => "";
}

/// <summary>Target feedback only; setting a mode never performs a drop.</summary>
public enum DragVisualMode { None, Copy, Move, Link, Generic, Rejected }

/// <summary>
/// Global drag and drop system for Origami. Supports typed payloads, hover feedback,
/// drop acceptance with validation, and an animated drag ghost visual.
///
/// Start a drag from any element, drop in any element that accepts the payload type.
/// One drag operation at a time. The payload persists for one frame after mouse release
/// so drop targets can detect and consume it.
/// </summary>
public static class DragDrop
{
    /// <summary>True while a drag is in progress (mouse held).</summary>
    public static bool IsDragging { get; private set; }

    /// <summary>The current drag payload, or null.</summary>
    public static DragPayload? Payload { get; private set; }

    /// <summary>Current drag position in Paper coordinates.</summary>
    public static XEngine.Vector.Float2 DragPosition { get; private set; }

    /// <summary>True on the frame the mouse was released after a drag. Payload still available for drop targets.</summary>
    public static bool IsDropFrame => !IsDragging && Payload != null;

    /// <summary>Optional host ownership gates for native multi-surface editors.</summary>
    public static Func<Paper, bool>? PointerSurfaceFilter;
    public static Func<Paper, bool>? KeyboardSurfaceFilter;
    public static Func<bool>? PointerOutsideSurfaces;
    public static Func<Paper, bool>? ButtonDownProvider;
    public static Func<Paper, XEngine.Vector.Float2>? PointerPositionProvider;
    public static DragVisualMode VisualMode { get; private set; }
    private static Paper? s_sourcePaper;
    private static Paper? s_pointerPaper;
    private static string s_display = "";
    private static XEngine.Scribe.FontFile? s_measuredFont;
    private static float s_measuredSize, s_titleWidth;

    public static bool CanReceive(Paper paper) => PointerSurfaceFilter?.Invoke(paper) ?? true;
    public static bool IsDropFrameFor(Paper paper) => IsDropFrame && CanReceive(paper);

    internal static void BeginFrame(Paper paper)
    {
        CancelOnEscape(paper);
        if (Payload == null || !CanReceive(paper)) return;
        s_pointerPaper = paper;
        if (PointerPositionProvider is { } position) paper.PointerPos = position(paper);
        VisualMode = DragVisualMode.None;
    }

    private static void CancelOnEscape(Paper paper)
    {
        if (Payload != null && paper.IsKeyPressed(PaperKey.Escape) && (KeyboardSurfaceFilter?.Invoke(paper) ?? true)) Cancel();
    }

    /// <summary>Report from the hovered target on the owning surface; last nested target wins.</summary>
    public static void SetVisualMode(Paper paper, DragVisualMode mode)
    {
        if (!IsDragging || !CanReceive(paper)) return;
        s_pointerPaper = paper;
        VisualMode = mode;
    }

    public static void CancelForSurface(Paper paper)
    {
        if (ReferenceEquals(s_sourcePaper, paper) || ReferenceEquals(s_pointerPaper, paper)) Cancel();
    }

    // ── Start / End / Cancel ─────────────────────────────────

    /// <summary>Begin a drag operation with the given payload.</summary>
    public static void StartDrag(DragPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Cancel();
        IsDragging = true;
        Payload = payload;
        s_sourcePaper = s_pointerPaper = Origami.ActivePaper;
        s_display = string.IsNullOrEmpty(payload.Icon) ? payload.DisplayName : $"{payload.Icon}  {payload.DisplayName}";
    }

    /// <summary>End the drag and consume the payload. Returns the payload or null.</summary>
    public static DragPayload? EndDrag()
    {
        var p = Payload;
        Cancel();
        return p;
    }

    /// <summary>Cancel the drag, discarding the payload.</summary>
    public static void Cancel()
    {
        IsDragging = false;
        Payload = null;
        VisualMode = DragVisualMode.None;
        s_sourcePaper = s_pointerPaper = null;
        s_display = "";
        s_measuredFont = null;
        s_measuredSize = s_titleWidth = 0;
    }

    // ── Query ────────────────────────────────────────────────

    /// <summary>Check if a specific payload type is being dragged (mouse still held).</summary>
    public static bool IsDraggingType<T>() where T : DragPayload
        => IsDragging && Payload is T;

    /// <summary>Check if a payload type is present (dragging OR on the drop frame).</summary>
    public static bool HasPayloadType<T>() where T : DragPayload
        => Payload is T;

    /// <summary>Get the payload as a specific type, or null.</summary>
    public static T? GetPayload<T>() where T : DragPayload
        => Payload as T;

    // ── Drop acceptance ──────────────────────────────────────

    /// <summary>
    /// Try to accept a drop of a specific type. Returns the payload if the mouse was
    /// released this frame over the hovered element. Consumes the payload.
    /// </summary>
    public static T? AcceptDrop<T>(bool isHovered) where T : DragPayload
    {
        if (!isHovered || Origami.ActivePaper is { } paper && !CanReceive(paper)) return null;
        if (Payload is not T typed)
        {
            ReportAcceptance(false, mismatchedAlternative: true);
            return null;
        }
        ReportAcceptance(true);
        if (IsDropFrame)
        {
            Cancel();
            return typed;
        }
        return null;
    }

    /// <summary>Accept drop with additional validation predicate.</summary>
    public static T? AcceptDrop<T>(bool isHovered, Func<T, bool> validate) where T : DragPayload
    {
        if (!isHovered || Origami.ActivePaper is { } paper && !CanReceive(paper)) return null;
        if (Payload is not T typed) { ReportAcceptance(false, mismatchedAlternative: true); return null; }
        // Standalone validators retain their previous drop-only invocation contract.
        if (!IsDropFrame && !Origami.Current.DragFeedback) return null;
        bool valid = validate(typed);
        ReportAcceptance(valid);
        if (IsDropFrame && valid)
        {
            Cancel();
            return typed;
        }
        return null;
    }

    private static void ReportAcceptance(bool valid, bool mismatchedAlternative = false)
    {
        // Fields can accept several payload types. An unrelated typed alternative must not
        // erase the compatibility already established by that same hovered field.
        if (mismatchedAlternative && VisualMode is not (DragVisualMode.None or DragVisualMode.Rejected)) return;
        if (Origami.Current.DragFeedback && Origami.ActivePaper is { } paper)
            SetVisualMode(paper, valid ? DragVisualMode.Link : DragVisualMode.Rejected);
    }

    // ── Per-frame update ─────────────────────────────────────

    /// <summary>Call once per frame. Tracks drag position and detects mouse release.</summary>
    public static void Update(Paper paper)
    {
        if (Payload == null) return;
        CancelOnEscape(paper);
        if (Payload == null) return;
        // A stale secondary Paper must neither release nor expire another surface's payload.
        if (!CanReceive(paper))
        {
            // A source release outside all targets cancels instead of dropping on stale hover.
            if (ReferenceEquals(s_sourcePaper, paper) && paper.IsPointerReleased(PaperMouseBtn.Left)
                && (PointerOutsideSurfaces?.Invoke() ?? true)) Cancel();
            return;
        }
        // Clear stale payload from previous drop frame
        if (!IsDragging && Payload != null)
        {
            Cancel();
            return;
        }

        if (!IsDragging) return;

        DragPosition = paper.PointerPos;

        // End drag on mouse release (payload persists for one frame for drop targets)
        if (!(ButtonDownProvider?.Invoke(paper) ?? paper.IsPointerDown(PaperMouseBtn.Left)))
            IsDragging = false;
    }

    // ── Visual ───────────────────────────────────────────────

    /// <summary>Draw the drag ghost visual (label following cursor). Call at end of frame.</summary>
    public static void DrawVisual(Paper paper)
    {
        if (!IsDragging || Payload == null || !CanReceive(paper)) return;

        var theme = Origami.Current;
        var font = theme.Font;
        var m = theme.Metrics;
        if (font == null) return;

        float mx = (float)DragPosition.X + 12;
        float my = (float)DragPosition.Y + m.Spacing;

        if (theme.DragFeedback)
        {
            // XEngine's status badge complements native target highlights; this is not Unity cursor art.
            var status = VisualMode switch
            {
                DragVisualMode.Copy => "+", DragVisualMode.Move => "→", DragVisualMode.Link => "↗",
                DragVisualMode.Generic => "✓", _ => "⊘"
            };
            bool rejected = VisualMode is DragVisualMode.None or DragVisualMode.Rejected;
            Color border = rejected ? theme.Red.C600 : theme.Primary.C500;
            if (!ReferenceEquals(s_measuredFont, font) || s_measuredSize != m.FontSizeSmall)
            {
                s_measuredFont = font; s_measuredSize = m.FontSizeSmall;
                s_titleWidth = (float)paper.MeasureText(s_display, m.FontSizeSmall, font).X;
            }
            float ghostWidth = Math.Min(paper.ScreenRect.Size.X, Math.Max(32, s_titleWidth + 29));
            mx = Math.Clamp(mx, 0, Math.Max(0, paper.ScreenRect.Size.X - ghostWidth));
            my = Math.Clamp(my, 0, Math.Max(0, paper.ScreenRect.Size.Y - 22));
            using (paper.Row("dd_ghost").PositionType(PositionType.SelfDirected).Position(mx, my)
                .Width(ghostWidth).Height(22).Clip().BackgroundColor(Color.FromArgb(238, 50, 50, 50))
                .BorderColor(border).BorderWidth(1).Rounded(2).ChildLeft(4).ChildRight(5).RowBetween(4)
                .IsNotInteractable().Layer(Layer.Topmost + 500).Enter())
            {
                paper.Box("dd_status").Width(14).Height(22).IsNotInteractable().Text(status, font)
                    .TextColor(rejected ? theme.Red.C600 : theme.Ink.C600).FontSize(12).Alignment(TextAlignment.MiddleCenter);
                paper.Box("dd_title").Width(UnitValue.Stretch()).Height(22).IsNotInteractable().Text(s_display, font)
                    .TextColor(theme.Ink.C500).FontSize(m.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();
            }
            return;
        }

        paper.Box("dd_ghost")
            .PositionType(PositionType.SelfDirected)
            .Position(mx, my)
            .Width(UnitValue.Auto).Height(m.HeaderHeight)
            .BackgroundColor(Color.FromArgb(200, 40, 40, 45))
            .BorderColor(theme.Primary.C400).BorderWidth(1)
            .Rounded(m.Rounding).ChildLeft(m.Padding).ChildRight(m.Padding)
            .IsNotInteractable()
            .Layer(Layer.Topmost + 500)
            .Text(s_display, font)
            .TextColor(theme.Ink.C500)
            .FontSize(m.FontSizeSmall)
            .Alignment(TextAlignment.MiddleLeft);
    }
}
