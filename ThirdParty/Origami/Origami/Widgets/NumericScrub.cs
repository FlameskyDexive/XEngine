using System;
using XEngine.PaperUI;

namespace XEngine.OrigamiUI;

/// <summary>Shared opt-in editor scrub math. Gesture state belongs to the handle, not the widget builder.</summary>
internal static class NumericScrub
{
    private struct State
    {
        internal double Value, Sensitivity;
        internal bool YSign;
    }

    internal static void Attach(Paper paper, ElementBuilder handle, double value, bool floating, Action<double> setter)
    {
        handle.DragEditSession(NumericSubmit.DragEditSessionFactory)
            .OnPress(_ => paper.ClearFocus())
            .OnDragStart(e => paper.SetElementStorage(e.Source, "numeric.scrub", new State
            {
                Value = value,
                Sensitivity = !double.IsFinite(value) ? 0 : floating
                    ? Math.Max(1, Math.Sqrt(Math.Abs(value))) * .03
                    : Math.Max(1, Math.Floor(Math.Sqrt(Math.Abs(value)) * .03)),
            }))
            .OnDragging(e =>
            {
                var state = paper.GetElementStorage(e.Source, "numeric.scrub", default(State));
                if (state.Sensitivity == 0) return;
                double dx = e.Delta.X, dy = -e.Delta.Y;
                double ax = Math.Abs(dx), ay = Math.Abs(dy);
                if (Math.Max(ax, ay) == 0) return;
                if (Math.Abs(ax - ay) / Math.Max(ax, ay) > .1) state.YSign = ay > ax;
                double acceleration = (Down(paper, PaperKey.LeftShift, PaperKey.RightShift) ? 4 : 1)
                    * (Down(paper, PaperKey.LeftAlt, PaperKey.RightAlt) ? .25 : 1);
                double delta = Math.Sign(state.YSign ? dy : dx) * Math.Sqrt(dx * dx + dy * dy) * acceleration;
                state.Value += delta * state.Sensitivity;
                double result = floating
                    ? Math.Round(state.Value, Math.Clamp((int)-Math.Floor(Math.Log10(state.Sensitivity)), 0, 15), MidpointRounding.AwayFromZero)
                    : Math.Round(state.Value);
                paper.SetElementStorage(e.Source, "numeric.scrub", state);
                if (double.IsFinite(result)) setter(result);
                paper.WrapPointer();
            })
            .Cursor(PaperCursor.ResizeHorizontal);
    }

    private static bool Down(Paper paper, PaperKey left, PaperKey right) => paper.IsKeyDown(left) || paper.IsKeyDown(right);
}
