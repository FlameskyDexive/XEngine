using System;
using System.Collections.Generic;
using XEngine.OrigamiUI;
using XEngine.Quill;
using XEngine.Scribe;

namespace XEngine.Editor.GUI.Panels;

/// <summary>Panel-owned horizontal measurements. Model and font changes rebuild once;
/// viewport scrolling only reads the retained numbers. Includes collapsed nodes so
/// opening a branch does not change the horizontal range underneath a drag.</summary>
internal sealed class HierarchyHorizontalLayout
{
    private readonly List<float> _labelLeft = new();
    private readonly List<float> _labelWidth = new();
    private Canvas? _canvas;
    private FontFile? _font;
    private long _modelVersion = -1;
    private int _atlasRevision;
    private float _size, _scale;
    public float ContentWidth { get; private set; }
    public int MeasurementBuilds { get; private set; }

    public void Update(Canvas canvas, FontFile font, float size, IReadOnlyList<TreeNode> nodes, long modelVersion)
    {
        if (ReferenceEquals(canvas, _canvas) && ReferenceEquals(font, _font) && size == _size
            && canvas.FramebufferScale == _scale && canvas.FontAtlasRevision == _atlasRevision
            && modelVersion == _modelVersion) return;

        _labelLeft.Clear(); _labelWidth.Clear();
        float width = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            // Tree base + depth indentation + caret + object icon + name inset.
            float left = HierarchyTableLayout.LeadingWidth + HierarchyTableLayout.ObjectIndent
                + nodes[i].Depth * HierarchyTableLayout.IndentSize + HierarchyTableLayout.CaretWidth
                + HierarchyTableLayout.IconWidth + HierarchyTableLayout.NameInset;
            float labelWidth = MathF.Ceiling(canvas.MeasureText(nodes[i].Label, size, font).X);
            _labelLeft.Add(left); _labelWidth.Add(labelWidth);
            // Visibility/picking are leading columns; only prefab navigation trails the name.
            width = MathF.Max(width, left + labelWidth + HierarchyTableLayout.NavigationWidth + 6);
        }
        ContentWidth = width;
        _canvas = canvas; _font = font; _size = size; _scale = canvas.FramebufferScale;
        _modelVersion = modelVersion; _atlasRevision = canvas.FontAtlasRevision;
        MeasurementBuilds++;
    }

    public float RevealX(int nodeIndex, float viewportWidth)
    {
        if ((uint)nodeIndex >= (uint)_labelLeft.Count || viewportWidth <= 0) return 0;
        float left = _labelLeft[nodeIndex];
        // Very long names reveal their beginning; users can scroll to the remaining text.
        float end = left + MathF.Min(_labelWidth[nodeIndex],
            MathF.Max(0, viewportWidth - HierarchyTableLayout.LeadingWidth - 24));
        return Math.Clamp(end + 12 - viewportWidth, 0, MathF.Max(0, ContentWidth - viewportWidth));
    }

    public void Clear()
    {
        _labelLeft.Clear(); _labelWidth.Clear();
        _canvas = null; _font = null; _modelVersion = -1; ContentWidth = 0;
    }
}
