using System;
using System.Collections.Generic;

using XEngine.Ai.Gateway;
using XEngine.Editor.Core;
using XEngine.Editor.Projects;
using EditorAssetBackend = XEngine.Editor.EditorAssetBackend;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Scribe;

using TextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// ★ Gen-P4.1 — Browse imported gen_fetch results, pick a winner, hide rejects.
/// Data lives in <c>.xengine/gen-tasks.json</c> + <c>.xengine/gen-gallery.json</c>.
/// </summary>
public sealed class GenCandidateGalleryPanel : DockPanel
{
    [MenuItem("Window/AI/Candidate Gallery", priority: 40)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(GenCandidateGalleryPanel));

    public override string Title => "Candidate Gallery";
    public override string Icon => EditorIcons.Images;
    public override float HeaderWidth => 34f;

    private string _search = "";
    private string _kind = "";
    private string _selectedTaskId = "";
    private string _status = "";
    private IReadOnlyList<GenCandidate> _cards = Array.Empty<GenCandidate>();

    public override void OnHeaderContent(Paper paper, float width, float height)
        => Origami.Button(paper, "gcg_hdr_refresh", EditorIcons.ArrowsRotate, Reload)
            .Width(MathF.Min(28f, width)).Height(MathF.Min(26f, height))
            .Tooltip("Reload gen-tasks.json").Show();

    public override void OnGUI(Paper paper, float width, float height)
    {
        FontFile? font = EditorTheme.DefaultFont;
        if (font == null)
            return;

        Project? project = Project.Current;
        if (project == null)
        {
            EditorGUI.EmptyState(paper, "gcg_no_project", "Open a project to browse generation candidates.", font);
            return;
        }

        if (_cards.Count == 0 && string.IsNullOrEmpty(_status))
            Reload();

        GenCandidateGalleryLayout layout = GenCandidateCatalog.ComputeLayout(width, height);
        DrawToolbar(paper, font, width);
        using (paper.Row("gcg_workspace").Width(width).Height(layout.WorkspaceHeight).Clip().Enter())
        {
            DrawList(paper, font, layout.ListWidth, layout.WorkspaceHeight);
            paper.Box("gcg_div").Width(1).Height(layout.WorkspaceHeight)
                .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
            DrawDetail(paper, font, layout.DetailWidth, layout.WorkspaceHeight, project);
        }

        paper.Box("gcg_footer").Width(width).Height(layout.FooterHeight).Padding(9, 9, 0, 0)
            .Text(string.IsNullOrEmpty(_status) ? $"{_cards.Count} candidates" : _status, font)
            .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
    }

    private void DrawToolbar(Paper paper, FontFile font, float width)
    {
        using (paper.Row("gcg_toolbar").Width(width).Height(40).Padding(8, 8, 6, 4).RowBetween(6).Enter())
        {
            Origami.SearchField(paper, "gcg_search", _search, value =>
            {
                _search = value;
                Reload();
            }, "Search prompt / provider…").Width(UnitValue.StretchOne).Show();
            KindChip(paper, font, "", "All");
            KindChip(paper, font, "model3d", "3D");
            KindChip(paper, font, "sprite", "2D");
            KindChip(paper, font, "motion", "Motion");
            KindChip(paper, font, "skybox", "Sky");
            KindChip(paper, font, "sfx", "SFX");
        }
    }

    private void KindChip(Paper paper, FontFile font, string kind, string label)
    {
        bool on = _kind == kind;
        Origami.Button(paper, "gcg_kind_" + label, on ? "· " + label : label, () =>
        {
            _kind = kind;
            Reload();
        }).Width(52).Show();
        _ = font;
    }

    private void DrawList(Paper paper, FontFile font, float width, float height)
    {
        Origami.ScrollView(paper, "gcg_list", width, height).Body(() =>
        {
            using (paper.Column("gcg_list_col").Width(width).Height(UnitValue.Auto).Padding(6, 6, 6, 6).ColBetween(4).Enter())
            {
                if (_cards.Count == 0)
                {
                    EditorGUI.EmptyState(paper, "gcg_empty",
                        "No imported candidates. Run gen_fetch, then refresh.", font);
                    return;
                }

                for (int i = 0; i < _cards.Count; i++)
                {
                    GenCandidate card = _cards[i];
                    bool selected = card.TaskId == _selectedTaskId;
                    string title = Truncate(card.Prompt, 48);
                    string meta = $"{card.Kind} · {card.Provider} · {card.State}";
                    if (card.Chosen)
                        meta = "CHOSEN · " + meta;

                    using (paper.Column("gcg_card_" + i).Width(width - 12).Height(56).Rounded(4).Padding(8, 8, 6, 6)
                        .BackgroundColor(selected ? EditorTheme.Selected : EditorTheme.Neutral200)
                        .Hovered.BackgroundColor(selected ? EditorTheme.Selected : EditorTheme.Hover).End()
                        .OnClick(card.TaskId, (id, _) => _selectedTaskId = id).Enter())
                    {
                        paper.Box("gcg_card_t_" + i).Height(20)
                            .Text(title, font).TextColor(EditorTheme.Ink400)
                            .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft)
                            .IsNotInteractable();
                        paper.Box("gcg_card_m_" + i).Height(18)
                            .Text(meta, font).TextColor(card.Chosen ? EditorTheme.Accent : EditorTheme.InkDim)
                            .FontSize(EditorTheme.FontSizeSmall - 1).Alignment(TextAlignment.MiddleLeft)
                            .IsNotInteractable();
                    }
                }
            }
        });
    }

    private void DrawDetail(Paper paper, FontFile font, float width, float height, Project project)
    {
        GenCandidate? selected = null;
        for (int i = 0; i < _cards.Count; i++)
        {
            if (_cards[i].TaskId == _selectedTaskId)
            {
                selected = _cards[i];
                break;
            }
        }

        Origami.ScrollView(paper, "gcg_detail", width, height).Body(() =>
        {
            using (paper.Column("gcg_detail_col").Width(width).Height(UnitValue.Auto).Padding(10, 10, 8, 8).ColBetween(6).Enter())
            {
                if (selected is null)
                {
                    EditorGUI.EmptyState(paper, "gcg_nodetail", "Select a candidate.", font);
                    return;
                }

                paper.Box("gcg_d_prompt").Height(UnitValue.Auto)
                    .Text(selected.Prompt, font).TextColor(EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSize)                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                paper.Box("gcg_d_meta").Height(36)
                    .Text($"{selected.Provider} / {selected.Kind}\n{selected.State}  {selected.TaskId}", font)
                    .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

                for (int a = 0; a < selected.AssetPaths.Count; a++)
                {
                    paper.Box("gcg_d_asset_" + a).Height(20)
                        .Text(selected.AssetPaths[a], font).TextColor(EditorTheme.Accent)
                        .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft)
                        .IsNotInteractable();
                }

                using (paper.Row("gcg_d_actions").Width(width - 20).Height(30).RowBetween(6).Enter())
                {
                    Origami.Button(paper, "gcg_choose", "Choose", () =>
                    {
                        new GenCandidateCatalog(project.RootPath).Choose(selected.TaskId);
                        _status = "Chosen " + selected.TaskId;
                        Reload();
                    }).Width(80).Show();
                    Origami.Button(paper, "gcg_discard", "Discard", () =>
                    {
                        new GenCandidateCatalog(project.RootPath).Discard(selected.TaskId);
                        _selectedTaskId = "";
                        _status = "Discarded " + selected.TaskId;
                        Reload();
                    }).Width(80).Show();
                    Origami.Button(paper, "gcg_reveal", "Reveal", () => Reveal(selected)).Width(80).Show();
                }
            }
        });
    }

    private void Reload()
    {
        Project? project = Project.Current;
        if (project == null)
        {
            _cards = Array.Empty<GenCandidate>();
            return;
        }

        _cards = new GenCandidateCatalog(project.RootPath).List(
            string.IsNullOrEmpty(_kind) ? null : _kind,
            string.IsNullOrWhiteSpace(_search) ? null : _search);
        if (_selectedTaskId.Length == 0)
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i].Chosen)
                {
                    _selectedTaskId = _cards[i].TaskId;
                    break;
                }
            }
        }
    }

    private static void Reveal(GenCandidate card)
    {
        if (card.AssetPaths.Count == 0)
            return;
        AssetEntry? entry = EditorAssetBackend.Instance?.GetEntry(card.AssetPaths[0]);
        if (entry == null)
            return;
        Selection.Select(entry);
        Selection.Ping(entry.Guid);
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? "";
        return text[..(max - 1)] + "…";
    }
}
