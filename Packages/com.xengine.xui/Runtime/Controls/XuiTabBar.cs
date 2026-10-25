// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.XUI;

/// <summary>
/// One tab: a button that selects itself and reveals the subtree it is bound to.
/// </summary>
/// <remarks>
/// The panel a tab shows is named in the document (<c>bind="XRectAudio"</c>) rather than wired in
/// code. A tab and its panel are one authoring decision, and splitting them across two files is how a
/// tab ends up showing the wrong panel after someone reorders the bar.
/// </remarks>
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("UI/XUI/Tab")]
public sealed class XuiTab : UIBehaviour
{
    [SerializeField] private string _boundId = string.Empty;

    /// <summary>Document id of the subtree this tab reveals. Empty means the tab shows nothing.</summary>
    public string BoundId { get => _boundId; set => _boundId = value ?? string.Empty; }

    /// <summary>The panel resolved from <see cref="BoundId"/>. Assigned by the assembler.</summary>
    public GameObject? Target { get; set; }

    /// <summary>Set by the owning bar when the selection changes.</summary>
    public bool IsSelected { get; internal set; }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void ApplyLayout(in Rect parentRect) { }
}

/// <summary>
/// A row of mutually exclusive tabs. Selecting one shows its panel and hides the others'.
/// </summary>
/// <remarks>
/// Exclusivity lives here rather than in each tab for the same reason it lives in the router rather
/// than in call sites: a rule enforced in one place cannot be half-applied. A tab bar whose tabs each
/// hid "the others" would break the moment someone added a tab and forgot.
/// </remarks>
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("UI/XUI/Tab Bar")]
public sealed class XuiTabBar : UIBehaviour
{
    [SerializeField] private int _selectedIndex;

    [SerializeIgnore] private readonly List<XuiTab> _tabs = new();

    /// <summary>Raised when the selection changes, with the new index.</summary>
    public event Action<int>? OnSelected;

    /// <summary>The tabs in this bar, in document order.</summary>
    public IReadOnlyList<XuiTab> Tabs => _tabs;

    /// <summary>
    /// The selected tab. Out-of-range values are clamped: a controller restoring a saved index should
    /// not crash because the bar has fewer tabs than it did last release.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => Select(value, notify: true);
    }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void ApplyLayout(in Rect parentRect) { }

    /// <summary>Registers a tab. Called by the assembler in document order.</summary>
    public void AddTab(XuiTab tab)
    {
        if (tab is not { IsDisposed: false } || _tabs.Contains(tab)) return;
        _tabs.Add(tab);
    }

    /// <summary>Applies the current selection. Called once the whole bar is built.</summary>
    public void Refresh() => Select(_selectedIndex, notify: false);

    /// <summary>Selects a tab, optionally without raising <see cref="OnSelected"/>.</summary>
    public void Select(int index, bool notify)
    {
        if (_tabs.Count == 0)
        {
            _selectedIndex = 0;
            return;
        }

        int clamped = Math.Clamp(index, 0, _tabs.Count - 1);
        bool changed = clamped != _selectedIndex;
        _selectedIndex = clamped;

        for (int i = 0; i < _tabs.Count; i++)
        {
            XuiTab tab = _tabs[i];
            if (tab is not { IsDisposed: false }) continue;

            bool selected = i == clamped;
            tab.IsSelected = selected;
            if (tab.Target is { IsDisposed: false } target) target.Enabled = selected;
        }

        if (notify && changed) OnSelected?.Invoke(clamped);
    }

    /// <summary>Selects the tab bound to a document id. Returns false when no tab is.</summary>
    public bool SelectBound(string boundId)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (!string.Equals(_tabs[i].BoundId, boundId, StringComparison.Ordinal)) continue;
            Select(i, notify: true);
            return true;
        }
        return false;
    }
}
