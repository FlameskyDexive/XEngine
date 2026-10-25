// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.XUI;

/// <summary>Everything the assembler needs that does not come from the document itself.</summary>
public sealed class XuiBuildOptions
{
    /// <summary>Token table for <c>color=</c>. Defaults to literals only.</summary>
    public XuiPalette Palette { get; set; } = XuiPalette.Empty;

    /// <summary>Sprite / font key resolution. Defaults to "every key misses".</summary>
    public IXuiAssetResolver Assets { get; set; } = XuiNullAssetResolver.Instance;

    /// <summary>
    /// Scene the Canvas root is added to. Null builds a detached tree, which is what the offline and
    /// unit-test paths want.
    /// </summary>
    public Scene? Scene { get; set; }

    /// <summary>
    /// Layer root to parent the Canvas under. Null leaves the screen as a scene root — the normal case,
    /// since each screen owns its own Canvas.
    /// </summary>
    public GameObject? Parent { get; set; }

    /// <summary>Canvas sort order. The router raises this per layer so PopUp always covers Normal.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Translate authored text through the active locale as the screen is built, and record where each
    /// string came from so a later locale switch can re-translate in place.
    /// </summary>
    /// <remarks>
    /// On by default: with no catalog loaded every lookup falls back to the source string, so leaving it
    /// on costs one component per label and changes nothing on screen. A tool that wants the document's
    /// literal text — a diff, a screenshot comparison — turns it off.
    /// </remarks>
    public bool Localize { get; set; } = true;
}
