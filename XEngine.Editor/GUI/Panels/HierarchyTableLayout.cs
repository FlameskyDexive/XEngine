namespace XEngine.Editor.GUI.Panels;

/// <summary>Measured Unity 7 Hierarchy table geometry; scoped to this panel.</summary>
internal static class HierarchyTableLayout
{
    public const float HeaderHeight = 20;
    public const float RowHeight = 16;
    public const float ColumnWidth = 20;
    public const float LeadingWidth = ColumnWidth * 2;
    public const float SceneIndent = 6;
    public const float ObjectIndent = SceneIndent + IndentSize;
    public const float IndentSize = 14;
    public const float CaretWidth = 12;
    public const float IconWidth = 18;
    public const float NameInset = 4;
    public const float NavigationWidth = 18;
}
