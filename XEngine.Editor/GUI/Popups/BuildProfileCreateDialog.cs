// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI.Popups;

/// <summary>
/// "Add Build Profile" browser: pick a platform, name the profile, and create the
/// <c>.buildprofile</c> asset. The equivalent of Unity's Platform Browser.
/// </summary>
public static class BuildProfileCreateDialog
{
    private static readonly ModalHandle _handle = new();

    private static List<BuildPipeline> _pipelines = [];
    private static int _selected;
    private static string _name = "";
    private static bool _nameEditedByUser;
    private static Action<BuildProfileHandle>? _onCreated;

    private const float DialogWidth = 520f;
    private const float DialogHeight = 420f;
    private const float RowHeight = 40f;

    public static bool IsOpen => _handle.IsOpen;

    public static void Open(Action<BuildProfileHandle>? onCreated = null)
    {
        _pipelines = [.. BuildProfileRegistry.EnumeratePipelines()];
        _pipelines.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        if (_pipelines.Count == 0)
        {
            Origami.Message(Loc.Get("buildprofile.create_title"), Loc.Get("buildprofile.no_platforms"));
            return;
        }

        _selected = 0;
        _nameEditedByUser = false;
        _name = _pipelines[0].DisplayName;
        _onCreated = onCreated;

        _handle.Open(DrawInternal, closeOnBackdrop: true);
    }

    public static void Close()
    {
        _handle.Close();
        _onCreated = null;
    }

    private static void SelectPlatform(int index)
    {
        if (index < 0 || index >= _pipelines.Count) return;
        _selected = index;

        // Keep the name in step with the platform until the user types something of their own,
        // so the common case (one profile per platform) needs no typing at all.
        if (!_nameEditedByUser)
            _name = _pipelines[index].DisplayName;
    }

    private static void Create()
    {
        if (_selected < 0 || _selected >= _pipelines.Count) return;

        BuildProfileHandle? created = BuildProfileRegistry.Create(_name, _pipelines[_selected].Id);
        if (created == null)
        {
            Toasts.Warning(Loc.Get("buildprofile.create_failed"), Loc.Get("buildprofile.create_failed_msg"));
            return;
        }

        Action<BuildProfileHandle>? callback = _onCreated;
        Close();
        callback?.Invoke(created.Value);
    }

    private static void DrawInternal(Paper paper, int layer)
    {
        if (!_handle.IsOpen) return;
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        using (paper.Column("bpc_window")
            .Size(DialogWidth, DialogHeight)
            .Margin(UnitValue.StretchOne)
            .BackgroundColor(EditorTheme.Neutral300)
            .BorderColor(EditorTheme.Ink200).BorderWidth(1).Rounded(8)
            .Layer(layer)
            .StopEventPropagation()
            .Enter())
        {
            DrawTitle(paper, font);
            DrawPlatformList(paper, font);
            DrawBottomBar(paper, font);
        }
    }

    private static void DrawTitle(Paper paper, Scribe.FontFile font)
    {
        using (paper.Row("bpc_title").Height(32).BackgroundColor(EditorTheme.Neutral200).Rounded(8).ChildLeft(12).Enter())
        {
            paper.Box("bpc_title_text").Height(32)
                .Text(Loc.Get("buildprofile.create_title"), font).TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSizeLarge).Alignment(TextAlignment.MiddleLeft);

            paper.Box("bpc_title_spacer").Width(UnitValue.Stretch());

            paper.Box("bpc_close").Size(32).Rounded(4)
                .Text(EditorIcons.Xmark, font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter)
                .Hovered.BackgroundColor(EditorTheme.Ink200).End()
                .OnClick(_ => Close());
        }
    }

    private static void DrawPlatformList(Paper paper, Scribe.FontFile font)
    {
        Origami.ScrollView(paper, "bpc_list", DialogWidth, DialogHeight - 32 - 84)
            .Padding(10, 10, 10, 10)
            .Body(() =>
            {
                for (int i = 0; i < _pipelines.Count; i++)
                    DrawPlatformRow(paper, font, i);
            });
    }

    private static void DrawPlatformRow(Paper paper, Scribe.FontFile font, int index)
    {
        BuildPipeline pipeline = _pipelines[index];
        bool selected = _selected == index;

        using (paper.Row($"bpc_row_{index}").Height(RowHeight).Rounded(7).Margin(0, 0, 0, 4)
            .Padding(10, 10, 0, 0).RowBetween(10)
            .BackgroundColor(selected ? EditorTheme.Selected : Color.Transparent)
            .BorderColor(selected ? EditorTheme.Accent : Color.Transparent).BorderWidth(1)
            .Hovered.BackgroundColor(selected ? EditorTheme.Selected : EditorTheme.Hover).End()
            .OnClick(index, (i, _) => SelectPlatform(i))
            .Enter())
        {
            paper.Box($"bpc_row_{index}_ic").Width(20).IsNotInteractable()
                .Text(pipeline.Icon, font).TextColor(selected ? EditorTheme.Ink500 : EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter);

            paper.Box($"bpc_row_{index}_nm").IsNotInteractable()
                .Text(pipeline.DisplayName, font).TextColor(selected ? EditorTheme.Ink500 : EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();

            paper.Box($"bpc_row_{index}_id").Width(150).IsNotInteractable()
                .Text(pipeline.Id, EditorTheme.FontMono ?? font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight).TextTruncate();
        }
    }

    private static void DrawBottomBar(Paper paper, Scribe.FontFile font)
    {
        paper.Box("bpc_sep").Height(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

        using (paper.Row("bpc_bottom").Height(83).Padding(12, 12, 0, 0).RowBetween(8).Enter())
        {
            paper.Box("bpc_name_l").Width(46).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Text(Loc.Get("buildprofile.name"), font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            using (paper.Row("bpc_name_w").Height(UnitValue.Auto).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                Origami.TextField(paper, "bpc_name_v", _name, v => { _name = v; _nameEditedByUser = true; }).Show();

            EditorGUI.Chip(paper, "bpc_cancel", Loc.Get("common.cancel"), Close);

            paper.Box("bpc_gap").Width(6).Height(1).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable();

            EditorGUI.CtaButton(paper, "bpc_create", $"{EditorIcons.Plus}  {Loc.Get("buildprofile.create")}", EditorTheme.Accent, Create);
        }
    }
}
