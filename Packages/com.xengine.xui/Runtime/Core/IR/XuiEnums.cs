// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.XUI.Core;

/// <summary>How a screen's Canvas renders. Mirrors <c>XEngine.Runtime.UI.RenderMode</c> without
/// referencing it — <c>Runtime/Core</c> stays pure BCL so it can be linked into the offline AI Host.</summary>
public enum XuiCanvasMode
{
    Overlay,
    Camera,
    World,
}

/// <summary>
/// Which root a screen is parented to. Ported from the XPlay XUI window-type model: an exclusive
/// full-screen view, an always-on HUD, a modal stack, and an escape hatch. Determines sibling order
/// and what the router is allowed to close.
/// </summary>
public enum XuiLayerKind
{
    Normal,
    Fixed,
    PopUp,
    Other,
}

/// <summary>Canvas scaling policy. <c>Pixel</c> forces an integer scale factor for pixel art.</summary>
public enum XuiScaleMode
{
    Auto,
    Pixel,
}

/// <summary>
/// Anchor presets. A preset fixes both anchor corners and the pivot; free positioning is expressed
/// with <c>pos</c> on top of a preset. <c>Stretch</c> variants leave one or both axes elastic.
/// </summary>
public enum XuiAnchor
{
    None = 0,
    Center,
    Top,
    Bottom,
    Left,
    Right,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Stretch,
    TopStretch,
    BottomStretch,
    LeftStretch,
    RightStretch,
}

/// <summary>
/// How an <c>Image</c> maps its sprite onto the rect. These are exactly the modes XGUI's
/// <c>ImageType</c> plus <c>PreserveAspect</c> can express; there is deliberately no <c>cover</c>,
/// because XGUI has no crop-to-fill path and a silently-wrong stretch is worse than a lint error.
/// </summary>
public enum XuiImageFit
{
    Auto = 0,
    Simple,
    Sliced,
    Tiled,
    Filled,

    /// <summary>Fit inside the rect keeping aspect: <c>Simple</c> + <c>PreserveAspect</c>.</summary>
    Contain,
}

/// <summary>Horizontal text alignment. Vertical alignment rides on the same attribute.</summary>
public enum XuiTextAlign
{
    Left = 0,
    Center,
    Right,
    Justify,
}

/// <summary>
/// Which way a <c>&lt;Progress&gt;</c> fills. Declared here rather than beside the component because
/// <c>Runtime/Core</c> is pure BCL and the lint rules have to validate the attribute offline.
/// </summary>
public enum XuiProgressFill
{
    LeftToRight,
    RightToLeft,
    BottomToTop,
    TopToBottom,
}
