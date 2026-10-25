using System;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;

namespace XEngine.Editor.GUI;

/// <summary>Panel toolbar metrics sampled from the installed Unity 7 reference.</summary>
internal static class EditorToolbar
{
    internal const float Height = 21f;
    internal const float ButtonHeight = 20f;
    internal const float SearchHeight = 16f;
    // The new UI Toolkit Hierarchy uses a taller query strip than the IMGUI windows.
    internal const float HierarchyHeight = 27f;
    internal const float HierarchyButtonHeight = 25f;
    internal const float HierarchySearchHeight = 22f;

    internal static TextFieldBuilder Search(Paper paper, string id, string value,
        Action<string> setter, UnitValue width, string placeholder = "Search...",
        float height = SearchHeight, float fontSize = 11f)
        => Origami.SearchField(paper, id, value, setter, placeholder)
            .Width(width).Toolbar(height, fontSize);

    internal static ButtonBuilder IconButton(Paper paper, string id, IOrigamiIcon icon,
        Action onClick, bool active = false, bool enabled = true, float height = ButtonHeight)
    {
        var button = Origami.IconButton(paper, id, icon, onClick)
            .Width(24).Height(height).Rounding(EditorTheme.Roundness).Ghost();
        if (active) button.Primary();
        if (!enabled) button.Disabled();
        return button;
    }
}
