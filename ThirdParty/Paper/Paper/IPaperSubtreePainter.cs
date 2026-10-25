using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;

namespace XEngine.PaperUI;

/// <summary>Optional paint-only replacement for a fully laid-out subtree. Input and layout
/// still belong to Paper. Return false without emitting anything to use normal painting.
/// Subtrees with deferred layer breakouts bypass this hook.</summary>
public interface IPaperSubtreePainter
{
    bool TryPaint(Paper paper, ElementHandle root, Canvas host);
}
