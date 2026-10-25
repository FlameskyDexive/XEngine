// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// Ties a controller to the screen it drives.
/// </summary>
/// <remarks>
/// The screen name here must match the <c>&lt;Screen name="..."&gt;</c> in the document and the
/// controller's own type name. That three-way agreement is the same constraint XPlay's XUI enforces
/// between <c>UIViewId</c>, prefab name, and type name, for the same reason: it makes "which document
/// does this class drive" answerable from any one of the three without a lookup table to maintain.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class XuiScreenAttribute : Attribute
{
    public XuiScreenAttribute(string screenName)
    {
        ScreenName = screenName;
    }

    public string ScreenName { get; }

    /// <summary>
    /// Overrides the layer the document declares. Leave unset — a screen's layer is a property of the
    /// screen, and having it in two places invites them to disagree.
    /// </summary>
    public XuiLayerKind Layer { get; set; } = XuiLayerKind.Normal;

    /// <summary>True when <see cref="Layer"/> was set explicitly.</summary>
    public bool OverridesLayer { get; set; }
}
