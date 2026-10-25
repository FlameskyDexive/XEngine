// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.UI;

/// <summary>How text behaves when it is wider than its RectTransform.</summary>
public enum HorizontalWrapMode
{
    Wrap,
    Overflow,
}

/// <summary>How text behaves when it is taller than its RectTransform.</summary>
public enum VerticalWrapMode
{
    Truncate,
    Overflow,
    /// <summary>Like Truncate, but the last visible line ends with an ellipsis character.</summary>
    Ellipsis,
}
