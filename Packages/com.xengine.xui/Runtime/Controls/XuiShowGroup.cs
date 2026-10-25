// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.XUI;

/// <summary>
/// A subtree a controller can show or hide by name.
/// </summary>
/// <remarks>
/// The rule everywhere else in XUI is that a controller never touches a GameObject — it reads
/// <c>View.BtnLogin</c>. Toggling a section had no sanctioned way to obey that, and
/// <c>View.RectEmpty.GameObject.Enabled = true</c> both breaks the rule and reads badly.
/// <c>&lt;Show&gt;</c> is the answer: <c>View.ShowEmptyState.Visible = true</c>.
/// </remarks>
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("UI/XUI/Show Group")]
public sealed class XuiShowGroup : UIBehaviour
{
    [SerializeField] private bool _visible = true;

    /// <summary>Whether the subtree is shown. Setting it enables or disables this GameObject.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            if (GameObject is { IsDisposed: false } go) go.Enabled = value;
        }
    }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void ApplyLayout(in Rect parentRect) { }

    /// <summary>Flips the current state. Convenient for a controller wiring a disclosure toggle.</summary>
    public void Toggle() => Visible = !_visible;
}
