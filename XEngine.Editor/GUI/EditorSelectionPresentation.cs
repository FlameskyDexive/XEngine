using XEngine.Editor.Core;
using XEngine.PaperUI;

namespace XEngine.Editor.GUI;

/// <summary>Resolve collection focus once at its root, independently of Selection data.</summary>
internal static class EditorSelectionPresentation
{
    internal static bool IsFocused(Paper paper) =>
        paper.IsParentFocusWithin && EditorPopupMapping.IsSurfaceFocused(paper);
}
