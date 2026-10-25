// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;

using TextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

public partial class AnimationClipWindow
{
    private string _addSearch = "";
    private readonly List<AnimationPropertyCatalog.Entry> _addCatalog = new();

    public bool AddPropertyPopupOpen
    {
        get => _showAddProperty;
        set => _showAddProperty = value;
    }

    public string AddPropertySearch
    {
        get => _addSearch;
        set => _addSearch = value ?? "";
    }

    public IReadOnlyList<AnimationPropertyCatalog.Entry> AddPropertyEntries => _addCatalog;

    public bool TryAddCatalogEntry(int index, float time)
    {
        if (index < 0 || index >= _addCatalog.Count || _model.ClipIsReadOnly) return false;
        AnimationPropertyCatalog.Entry e = _addCatalog[index];
        SnapshotClip();
        if (e.IsTransformChannel)
        {
            if (string.IsNullOrEmpty(e.Path)) return false;
            return _model.AddProperty(e.Path, e.Channel, time);
        }
        if (e.IsBlendShape)
            return _model.AddBlendShapeProperty(e.Path, e.Property, time);
        return _model.AddGenericProperty(e.Path, e.TypeName, e.Property, time);
    }

    private void DrawAddPropertyPopup(Paper paper, float w, float h)
    {
        AnimationPropertyCatalog.Collect(_model.Root, _model.Clip, _addSearch, _addCatalog);
        using (paper.Column("animclip_add").Width(w).Height(h).Padding(2).Enter())
        {
            Origami.Button(paper, "animclip_addprop", "Add Property", () => _showAddProperty = !_showAddProperty)
                .FullWidth().Height(20).Show();
            if (!_showAddProperty) return;
            Origami.SearchField(paper, "animclip_addsearch", _addSearch, v => _addSearch = v ?? "", "Search properties")
                .Height(22).Show();
            float listH = Math.Max(40f, h - 20f - 22f - 8f);
            Origami.ScrollView(paper, "animclip_addlist", w - 4f, listH).Body(_ =>
            {
                string? lastGroup = null;
                for (int i = 0; i < _addCatalog.Count; i++)
                {
                    AnimationPropertyCatalog.Entry e = _addCatalog[i];
                    string group = string.IsNullOrEmpty(e.Path) ? "(Root)" : e.Path;
                    if (group != lastGroup)
                    {
                        lastGroup = group;
                        paper.Box($"animclip_addg{i}").Width(w - 8f).Height(18)
                            .Text(group, EditorTheme.DefaultFont!)
                            .TextColor(AnimationEditorChrome.TextMuted)
                            .Alignment(TextAlignment.MiddleLeft);
                    }
                    int idx = i;
                    Origami.Button(paper, $"animclip_add{i}", "    " + EntryTail(e.Label), () =>
                    {
                        if (TryAddCatalogEntry(idx, (float)_playbackTime))
                            _showAddProperty = false;
                    }).Ghost().FullWidth().Height(20).Show();
                }
                if (_addCatalog.Count == 0)
                    Origami.Label(paper, "animclip_addnone", _model.Root == null
                        ? "Select a scene object to add properties"
                        : "No matching properties").Show();
            });
        }
    }

    /// <summary>Catalog labels are "path : Type.Property" — the tree groups by path, so rows show only the tail.</summary>
    private static string EntryTail(string label)
    {
        int sep = label.IndexOf(" : ", StringComparison.Ordinal);
        return sep >= 0 ? label.Substring(sep + 3) : label;
    }
}
