using System;
using System.IO;
using System.Reflection;

using XEngine.Async;
using XEngine.Editor.Core;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.Editor.Utils;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Rosetta;
using XEngine.Vector;

using Color = System.Drawing.Color;

using static XEngine.Editor.GUI.EditorGUI;

namespace XEngine.Editor.GUI;

/// <summary>
/// Startup project picker. The OS window is sized to this UI; there is no surrounding empty frame.
/// </summary>
public static class ProjectLauncher
{
    public static bool IsOpen { get; private set; } = true;

    private static string _newProjectName = "XEngine";
    private static string _newProjectPath = "";
    private static string _search = "";
    private static int _tab;   // 0 = Recent, 1 = New Project
    private static bool _readingProjectVersion;

    // Favorite-star icon in both Font Awesome weights: outline when unfavored, filled when favored.
    // EditorGlyphIcon resolves the face at draw time, so building these before fonts load is fine.
    private static readonly IOrigamiIcon _starOutline = new EditorGlyphIcon(EditorIcons.Star, weight: GlyphWeight.Outline);
    private static readonly IOrigamiIcon _starSolid = new EditorGlyphIcon(EditorIcons.Star, weight: GlyphWeight.Solid);

    private const float TS = 1.4f;

    /// <summary>Native client size of the launcher window — the card is the window, not a pane in a void.</summary>
    public const int NativeWindowWidth = 940;
    public const int NativeWindowHeight = 620;
    private const float HeaderHeight = 64f;
    private const float TipStripHeight = 32f;
    private const float ProgressFooterHeight = 56f;

    private static Color Col(int r, int g, int b, float a = 1f) => Color.FromArgb((int)Math.Round(a * 255), r, g, b);
    private static Color Raised => EditorTheme.Field;
    private static Color InputBd => EditorTheme.BorderSoft;
    private static Color CardBg => EditorTheme.Field;

    // Cycled tip strip drawn at the bottom of the launcher background.
    private static readonly string[] _tipKeys =
    {
        "launcher.tip.orbit",
        "launcher.tip.dolly",
        "launcher.tip.pan",
        "launcher.tip.fly",
        "launcher.tip.fly_speed",
        "launcher.tip.fly_updown",
        "launcher.tip.focus",
        "launcher.tip.gizmos",
        "launcher.tip.snap",
        "launcher.tip.duplicate",
        "launcher.tip.rename",
        "launcher.tip.math",
        "launcher.tip.math_ops",
        "launcher.tip.drag_asset",
        "launcher.tip.orient_cube",
        "launcher.tip.undo",
        "launcher.tip.discord",
        "launcher.tip.assignref",
        "launcher.tip.pingref",
        "launcher.tip.pickref",
        "launcher.tip.go_to_component",
        "launcher.tip.create_menu",
        "launcher.tip.component_menu",
        "launcher.tip.escape_unlock",
        "launcher.tip.nuget",
    };

    private const float _tipDuration = 8f;
    private const float _tipFadeTime = 0.45f;
    private static int _tipIndex;
    private static float _tipTimer;

    public static void Initialize()
    {
        _newProjectPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XEngine Projects");
        IsOpen = true;
        _tab = 0;
        _tipIndex = Random.Shared.Next(_tipKeys.Length);
        _tipTimer = 0;
        ProjectOpenProgress.Cancel();
        MenuBarBuilder.ClearMirroredMenu();
        if (ContextMenu.IsOpen)
            ContextMenu.Close();
    }

    private static void AdvanceTip()
    {
        _tipIndex = (_tipIndex + 1) % _tipKeys.Length;
        _tipTimer = 0;
    }

    public static void Close()
    {
        IsOpen = false;
    }

    private static bool _inDraw;

    public static void Draw(Paper paper, float dt, bool forceDraw = false)
    {
        if (!IsOpen && !forceDraw) return;
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        // Same-frame re-entry means something built the launcher before the GUI pass — the second
        // build throws "Element already exists: pl_window". Capture the first entry's stack instead
        // of dying with an opaque duplicate-ID error.
        if (_inDraw)
        {
            Runtime.Debug.LogError(
                "[LauncherDup] ProjectLauncher.Draw re-entered in the same frame — first entry stack:\n"
                + _firstDrawStack + "\n--- re-entry stack:\n" + Environment.StackTrace);
            return;
        }
        _inDraw = true;
        _firstDrawStack = Environment.StackTrace;
        try
        {
            DrawCore(paper, dt, forceDraw);
        }
        finally
        {
            _inDraw = false;
        }
    }

    private static string _firstDrawStack = "";

    private static void DrawCore(Paper paper, float dt, bool forceDraw)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        MenuBarBuilder.ClearMirroredMenu();

        float w = paper.ScreenRect.Size.X;
        float h = paper.ScreenRect.Size.Y;

        using (paper.Column("pl_window")
            .PositionType(PositionType.SelfDirected).Position(0, 0).Size(w, h)
            .BackgroundColor(EditorTheme.Neutral200)
            .Clip()
            .Enter())
        {
            Header(paper, font);
            paper.Box("pl_head_div").Height(1).BackgroundColor(EditorTheme.BorderSoft);
            if (_tab == 0) RecentBody(paper, font, w, h);
            else NewProjectBody(paper, font);
            if (ProjectOpenProgress.IsActive)
                DrawOpenProgress(paper, font);
            paper.Box("pl_tip_div").Height(1).BackgroundColor(EditorTheme.BorderSoft);
            DrawTipFooter(paper, font, dt);
        }

        if (ProjectOpenProgress.IsActive)
        {
            // Absorb clicks on the picker while the project loads; leave the tip strip tappable.
            paper.Box("pl_load_block")
                .PositionType(PositionType.SelfDirected).Position(0, 0)
                .Size(w, MathF.Max(0f, h - TipStripHeight))
                .Layer(Layer.Overlay)
                .BackgroundColor(Color.FromArgb(1, 0, 0, 0))
                .OnClick(0, (_, _) => { });
        }
    }

    private static string EngineVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1";
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        return v;
    }

    // ---- Header (brand + tab pills) ---------------------------------
    private static void Header(Paper P, Scribe.FontFile font)
    {
        var display = EditorTheme.FontDisplay ?? EditorTheme.DefaultBoldFont ?? font;

        using (P.Row("pl_header").Height(64).Padding(26, 26, 0, 0)
            .BackgroundColor(EditorTheme.Glass).Enter())
        {
            P.Box("pl_logo").Width(60).Height(60).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .IsNotInteractable()
                .OnPostLayout((handle, rect) => P.Draw(ref handle, (canvas, r) => EditorBranding.Draw(canvas, r)));

            // Wordmark scaled up to fill the header height (version line removed), vertically centered.
            P.Box("pl_xengine").Width(UnitValue.Auto).Height(UnitValue.Auto).Margin(13, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .Text("XENGINE", EditorTheme.FontLogo ?? display).FontSize(30f * TS).LetterSpacing(6f).TextColor(EditorTheme.Ink500).Alignment(TextAlignment.MiddleLeft);

            P.Box("pl_hspacer");

            using (P.Row("pl_lang_wrap").Width(UnitValue.Auto).Height(34).Margin(0, 10, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                LanguageDropdown(P, font);

            // Editor-level settings: platform build modules install into the per-user module
            // store, shared by every project (this window needs no project open — that's the point).
            Origami.IconButton(P, "pl_settings", new EditorGlyphIcon(EditorIcons.Gear, weight: GlyphWeight.Solid),
                    LauncherSettings.Open)
                .Width(34).Height(34).Tooltip(Loc.Get("launcher.settings")).Show();

            using (P.Row("pl_tabs").Width(UnitValue.Auto).Height(34).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .BackgroundColor(EditorTheme.Glass).BorderColor(InputBd).BorderWidth(1).Rounded(9).Enter())
            {
                TabChip(P, Loc.Get("launcher.tab_recent"), 0);
                TabChip(P, Loc.Get("launcher.new_project"), 1);
            }
        }
    }

    // Language picker: globe + name + region chip + chevron trigger; rows show chip + name + a
    // checkmark on the current locale.
    private static void LanguageDropdown(Paper P, Scribe.FontFile font)
    {
        int cur = LocaleHelper.GetIndex(EditorSettings.Instance.Locale);
        var globe = new EditorGlyphIcon(EditorIcons.Globe, weight: GlyphWeight.Solid);

        var items = new int[LocaleHelper.Codes.Length];
        for (int i = 0; i < items.Length; i++) items[i] = i;

        Origami.Dropdown<int>(P, "pl_lang", cur, LocaleHelper.SetLocale, items)
            .Width(UnitValue.Auto).Height(34)
            .PopoverWidth(210).ItemHeight(30)
            .CustomTrigger(ctx =>
            {
                P.Box("pl_lang_ic").Width(20).Margin(12, 8, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Icon(P, globe, EditorTheme.Ink300, size: 15f);
                P.Box("pl_lang_nm").Width(UnitValue.Auto).Margin(0, 8, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Text(LocaleHelper.Names[cur], font).FontSize(13f * TS).TextColor(EditorTheme.Ink500).Alignment(TextAlignment.MiddleLeft);
                LangChip(P, "pl_lang_tag", LocaleHelper.Tags[cur], font);
                P.Box("pl_lang_chev").Width(16).Margin(6, 12, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Icon(P, new EditorGlyphIcon(ctx.IsOpen ? EditorIcons.ChevronUp : EditorIcons.ChevronDown, weight: GlyphWeight.Solid), EditorTheme.Ink300, size: 10f);
            })
            .ItemRender((i, ctx) =>
            {
                LangChip(P, $"pl_lang_r{i}_tag", LocaleHelper.Tags[i], font);
                P.Box($"pl_lang_r{i}_nm").IsNotInteractable()
                    .Text(LocaleHelper.Names[i], font).FontSize(12.5f * TS)
                    .TextColor(ctx.IsSelected ? EditorTheme.Ink500 : EditorTheme.Ink300).Alignment(TextAlignment.MiddleLeft);
                if (ctx.IsSelected)
                    P.Box($"pl_lang_r{i}_ck").Width(16).IsNotInteractable()
                        .Icon(P, new EditorGlyphIcon(EditorIcons.Check, weight: GlyphWeight.Solid), EditorTheme.Accent, size: 11f);
            })
            .Show();
    }

    // Small pill showing a language's region tag (e.g. "US"), sized to its text.
    private static void LangChip(Paper P, string id, string text, Scribe.FontFile font)
    {
        P.Box(id).Width(UnitValue.Auto).Height(17).Rounded(5).Margin(5, 5, UnitValue.StretchOne, UnitValue.StretchOne).Padding(6, 6, 0, 0)
            .BackgroundColor(Col(255, 255, 255, 0.06f)).BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .IsNotInteractable()
            .Text(text, font).FontSize(9.5f * TS).TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleCenter);
    }

    private static void TabChip(Paper P, string label, int index)
    {
        bool on = _tab == index;
        float leftM = index == 0 ? 3 : 4;
        float rightM = index == 1 ? 3 : 0;
        P.Box("pl_tab" + index).Width(UnitValue.Auto).Height(28).Rounded(8)
            .Margin(leftM, rightM, UnitValue.StretchOne, UnitValue.StretchOne).Padding(14, 14, 0, 0)
            .BackgroundColor(on ? EditorTheme.Selected : Color.Transparent)
            .Text(label, EditorTheme.FontMedium ?? EditorTheme.DefaultFont)
            .FontSize(13f * TS).TextColor(on ? EditorTheme.Ink700 : EditorTheme.Ink300).Alignment(TextAlignment.MiddleCenter)
            .OnClick(_ => { if (!ProjectOpenProgress.IsActive) _tab = index; });
    }

    // ---- Recent tab -------------------------------------------------
    private static void RecentBody(Paper P, Scribe.FontFile font, float windowW, float windowH)
    {
        using (P.Column("pl_recent").Enter())
        {
            using (P.Row("pl_rbar").Height(58).Enter())
            {
                P.Box("pl_rtitle").Height(UnitValue.Auto).Margin(26, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                    .Text(Loc.Get("launcher.recent_projects"), EditorTheme.FontSemiBold ?? font).FontSize(18f * TS).TextColor(EditorTheme.Ink500).Alignment(TextAlignment.MiddleLeft);

                using (P.Box("pl_search_wrap").Width(220).Height(34).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                    Origami.TextField(P, "pl_search", _search, v => _search = v)
                        .Search(Loc.Get("launcher.search"))
                        .Height(34).Show();

                using (P.Row("pl_open").Width(UnitValue.Auto).Height(34).Rounded(9).Margin(9, 26, UnitValue.StretchOne, UnitValue.StretchOne)
                    .BackgroundColor(EditorTheme.Glass).BorderColor(InputBd).BorderWidth(1)
                    .Hovered.BorderColor(EditorTheme.BorderStrong).End()
                    .OnClick(_ => OpenProjectDialog())
                    .Enter())
                {
                    IconBox(P, "pl_oico", EditorIcons.FolderOpen_I, 15, EditorTheme.Ink300, 1.4f, 12);
                    P.Box("pl_otxt").Width(UnitValue.Auto).Height(UnitValue.Auto).Margin(7, 13, UnitValue.StretchOne, UnitValue.StretchOne)
                        .Text(Loc.Get("launcher.open"), font).FontSize(13 * TS).TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleLeft);
                }
            }

            var entries = RecentProjects.FavoritesFirst();
            // Kick the background existence probe (draw code never stats — a dead path on an
            // unreachable share once blocked the UI thread for seconds per frame, which is what
            // made "remove from recents" and card clicks feel frozen).
            RecentProjects.BeginRefreshLiveness(onChanged: () =>
                EditorApplication.Instance?.InvalidateSurface(EditorSurfaceDirtyReason.Layout));
            bool searching = !string.IsNullOrWhiteSpace(_search);
            if (searching)
            {
                string q = _search.Trim();
                entries = entries.FindAll(e =>
                    e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    e.Path.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            float extra = ProjectOpenProgress.IsActive ? ProgressFooterHeight : 0f;
            float scrollH = MathF.Max(80f, windowH - HeaderHeight - 1f - 58f - TipStripHeight - 1f - extra);
            Origami.ScrollView(P, "pl_cards_sv", windowW, scrollH).Padding(22, 22, 4, 22).ColSpacing(7).Body(() =>
            {
                if (entries.Count == 0)
                {
                    P.Box("pl_empty").Height(120)
                        .Text(Loc.Get(searching ? "launcher.no_results" : "launcher.no_recent"), font).FontSize(14f * TS).TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleCenter);
                    return;
                }
                for (int i = 0; i < entries.Count; i++)
                    Card(P, font, entries[i], i);
            });
        }
    }

    private static void Card(Paper P, Scribe.FontFile font, RecentProjectEntry entry, int i)
    {
        // Cached only — never stat during draw (see RecentProjects liveness cache).
        bool exists = RecentProjects.GetCachedExists(entry.Path);
        var mono = EditorTheme.FontMono ?? font;

        using (P.Row("pl_card" + i).Height(70).Rounded(3)
            .BackgroundColor(CardBg).BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Hovered
                .BackgroundColor(EditorTheme.Selected)
                .BorderColor(EditorTheme.BorderStrong)
                .End()
            .OnClick(entry, (e, _) => TryOpenProject(e.Path))
            .Enter())
        {
            RecentCardContextMenu(P, "pl_cardctx" + i, entry, exists);

            // Favorite toggle: outline star normally, filled yellow when favored. Drawn with the FA
            // weight directly (solid vs regular) because the fallback chain otherwise always resolves
            // the star to its outline. No background unless hovered; StopEventPropagation keeps the
            // click off the card's open-project handler.
            bool fav = entry.Favorite;
            Color amber = EditorTheme.Amber400;
            using (P.Box("pl_fav" + i).Width(44).Height(44).Rounded(12).Margin(16, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .Hovered.BackgroundColor(Col(255, 255, 255, 0.08f)).End()
                .StopEventPropagation()
                .OnClick(entry, (e, _) => RecentProjects.SetFavorite(e.Path, !e.Favorite))
                .Enter())
                P.Draw((vg, r) => DrawIcon(vg, r, fav ? _starSolid : _starOutline, 22, fav ? amber : Color.White, 1.5f));

            using (P.Column("pl_ci" + i).Height(UnitValue.Auto).Margin(15, 0, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
            {
                using (P.Row("pl_cn" + i).Height(UnitValue.Auto).Enter())
                {
                    P.Box("pl_cname" + i).Width(UnitValue.Auto).Height(UnitValue.Auto).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                        .Text(entry.Name, EditorTheme.FontSemiBold ?? font).FontSize(15 * TS).TextColor(exists ? EditorTheme.Ink500 : EditorTheme.Ink300).Alignment(TextAlignment.MiddleLeft);

                    // Version chip (or a "missing" chip when the folder is gone).
                    if (exists)
                        P.Box("pl_cver" + i).Width(UnitValue.Auto).Height(UnitValue.Auto).Rounded(5).Margin(9, 0, UnitValue.StretchOne, UnitValue.StretchOne).Padding(7, 7, 3, 3)
                            .BackgroundColor(Raised)
                            .Text(EngineVersion(), mono).FontSize(10 * TS).TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleCenter);
                    else
                        P.Box("pl_cmiss" + i).Width(UnitValue.Auto).Height(UnitValue.Auto).Rounded(5).Margin(9, 0, UnitValue.StretchOne, UnitValue.StretchOne).Padding(7, 7, 3, 3)
                            .BackgroundColor(Color.FromArgb(128, EditorTheme.Red300))
                            .Text(Loc.Get("launcher.missing"), mono).FontSize(10 * TS).TextColor(EditorTheme.Red400).Alignment(TextAlignment.MiddleCenter);

                    P.Box("pl_cnsp" + i);
                }

                P.Box("pl_cp" + i).Height(UnitValue.Auto).Margin(0, 0, 5, 0)
                    .Text(entry.Path, mono).FontSize(11.5f * TS).TextColor(EditorTheme.InkDim).Alignment(TextAlignment.MiddleLeft);
            }

            P.Box("pl_ct" + i).Width(UnitValue.Auto).Height(UnitValue.Auto).Margin(8, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .Text(FormatTimeAgo(entry.LastOpened), font).FontSize(12 * TS).TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleRight);

            // Arrow, revealed on hover
            Color arrowCol = Color.FromArgb(P.IsParentHovered ? 255 : 0, EditorTheme.AccentText.R, EditorTheme.AccentText.G, EditorTheme.AccentText.B);
            using (P.Box("pl_carr" + i).Width(18).Height(18).Margin(10, 16, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable().Enter())
                P.Draw((vg, r) => DrawIcon(vg, r, EditorIcons.ArrowRight_I, 17, arrowCol, 1.6f));
        }
    }

    private static void RecentCardContextMenu(Paper P, string id, RecentProjectEntry entry, bool exists)
    {
        Origami.RightClickMenu(P, id, b =>
        {
            if (exists)
            {
                b.Item(Loc.Get("launcher.open"), () => TryOpenProject(entry.Path), icon: EditorIcons.FolderOpen);
                b.Item(Loc.Get("project.show_in_explorer"), () => EditorUtils.OpenFileSystemPath(entry.Path), icon: EditorIcons.FolderTree);
                b.Separator();
            }
            b.Item(Loc.Get("project.copy_path"), () => P.SetClipboard(entry.Path), icon: EditorIcons.Copy);
            b.Item(Loc.Get("launcher.remove_recent"), () => RecentProjects.Remove(entry.Path), icon: EditorIcons.Xmark);
            if (exists)
            {
                b.Separator();
                b.Item(Loc.Get("launcher.delete_project"), () => Origami.Confirm(
                    Loc.Get("launcher.delete_confirm_title"),
                    Loc.Get("launcher.delete_confirm_body", new { name = entry.Name }),
                    () =>
                    {
                        try { if (Directory.Exists(entry.Path)) Directory.Delete(entry.Path, true); }
                        catch (Exception ex) { Runtime.Debug.LogError($"Failed to delete project: {ex.Message}"); }
                        RecentProjects.Remove(entry.Path);
                    }), icon: EditorIcons.Trash, danger: true);
            }
        });
    }

    // ---- New Project tab --------------------------------------------
    private static void NewProjectBody(Paper P, Scribe.FontFile font)
    {
        using (P.Row("pl_new").Enter())
        {
            using (P.Column("pl_tplcol").Padding(26, 26, 20, 20).Enter())
            {
                P.Box("pl_tplhead").Height(UnitValue.Auto).Margin(0, 0, 0, 16)
                    .Text(Loc.Get("launcher.choose_template"), EditorTheme.FontSemiBold ?? font).FontSize(18f * TS).TextColor(EditorTheme.Ink500).Alignment(TextAlignment.MiddleLeft);

                for (int r = 0; r < 2; r++)
                    using (P.Row("pl_tplrow" + r).Height(134).Margin(0, 0, r == 0 ? 0 : 12, 0).Enter())
                        for (int c = 0; c < 2; c++)
                        {
                            int idx = r * 2 + c;
                            if (idx == 0) BlankCard(P, font, c);
                            else EmptySlot(P, idx, c);
                        }
            }

            using (P.Column("pl_cfg").Width(308).Padding(24, 24, 24, 24)
                .BackgroundColor(EditorTheme.Field).BorderColor(EditorTheme.BorderSoft).BorderWidth(1).Enter())
            {
                P.Box("pl_cfghead").Height(UnitValue.Auto).Margin(0, 0, 0, 14)
                    .Text(Loc.Get("launcher.configure"), EditorTheme.FontSemiBold ?? font).FontSize(11f * TS).LetterSpacing(1f).TextColor(EditorTheme.InkDim).Alignment(TextAlignment.MiddleLeft);

                P.Box("pl_nmlbl").Height(UnitValue.Auto).Margin(0, 0, 0, 7)
                    .Text(Loc.Get("launcher.name"), font).FontSize(12f * TS).TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleLeft);
                Origami.TextField(P, "pl_nm", _newProjectName, v => _newProjectName = v)
                    .Height(38).Show();

                P.Box("pl_loclbl").Height(UnitValue.Auto).Margin(0, 0, 16, 7)
                    .Text(Loc.Get("launcher.path"), font).FontSize(12f * TS).TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleLeft);
                Origami.TextField(P, "pl_loc", _newProjectPath, v => _newProjectPath = v)
                    .Height(38).Mono()
                    .TrailingContent(() =>
                    {
                        using (P.Box("pl_locbtn").Width(24).Height(24).Margin(2, 6, UnitValue.StretchOne, UnitValue.StretchOne).Rounded(6)
                            .Hovered.BackgroundColor(Col(255, 255, 255, 0.07f)).End()
                            .OnClick(_ => BrowseLocation())
                            .Enter())
                            P.Draw((vg, r) => DrawIcon(vg, r, EditorIcons.FolderOpen_I, 14, EditorTheme.Ink300, 1.4f));
                    })
                    .Show();

                P.Box("pl_cfgsp");

                using (P.Row("pl_cta").Height(44).Rounded(3)
                    .BackgroundColor(EditorTheme.Selected)
                    .Hovered.BackgroundColor(EditorTheme.AccentBright).End()
                    .OnClick(_ => TryCreateProject())
                    .Enter())
                {
                    P.Box("pl_ctal");
                    IconBox(P, "pl_ctaico", EditorIcons.Bolt_I, 16, EditorTheme.Ink700, 1.4f, 0);
                    P.Box("pl_ctatxt").Width(UnitValue.Auto).Height(UnitValue.Auto).Margin(8, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                        .Text(Loc.Get("launcher.create_project"), EditorTheme.FontSemiBold ?? font).FontSize(14f * TS).TextColor(EditorTheme.Ink700).Alignment(TextAlignment.MiddleLeft);
                    P.Box("pl_ctar");
                }
            }
        }
    }

    // The only real template today (a blank project). Remaining grid cells are drawn as empty
    // placeholder slots to signal that more templates are coming.
    private static void BlankCard(Paper P, Scribe.FontFile font, int col)
    {
        (Color c1, Color c2) = GlyphColors(0.0, true);
        using (P.Column("pl_tpl0").Margin(col == 0 ? 0 : 6, col == 1 ? 0 : 6, 0, 0)
            .Rounded(3).Padding(17, 17, 17, 17)
            .BackgroundColor(EditorTheme.Field)
            .BorderColor(EditorTheme.Selected).BorderWidth(1)
            .Enter())
        {
            using (P.Box("pl_tplico0").Width(46).Height(46).Rounded(12).Margin(0, 0, 0, 11)
                .BackgroundLinearGradient(0, 0, 1, 1, c1, c2).Enter())
                P.Draw((vg, r) => DrawIcon(vg, r, EditorIcons.FileLines_I, 24, EditorTheme.Ink700, 1.3f));

            P.Box("pl_tpln0").Height(UnitValue.Auto).Margin(0, 0, 0, 4)
                .Text(Loc.Get("launcher.tpl_blank_name"), EditorTheme.FontSemiBold ?? font).FontSize(15f * TS).TextColor(EditorTheme.Ink500).Alignment(TextAlignment.MiddleLeft);
            P.Box("pl_tpld0").Height(UnitValue.Auto)
                .Text(Loc.Get("launcher.tpl_blank_desc"), font).FontSize(12f * TS).TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleLeft);
        }
    }

    // Placeholder for a future template: the card frame with no content inside.
    private static void EmptySlot(Paper P, int idx, int col)
    {
        using (P.Column("pl_tpl" + idx).Margin(col == 0 ? 0 : 6, col == 1 ? 0 : 6, 0, 0)
            .Rounded(12)
            .BackgroundColor(Col(255, 255, 255, 0.015f))
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Enter())
        { }
    }

    // ---- helpers ----------------------------------------------------
    private static void IconBox(Paper P, string id, IOrigamiIcon icon, float size, Color color, float sw, float leftM, float rightM = 0)
    {
        using (P.Box(id).Width(size).Height(size).Margin(leftM, rightM, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
            P.Draw((vg, r) => icon.Draw(vg, r, color, sw));
    }

    private static void DrawIcon(Canvas vg, Rect box, IOrigamiIcon icon, float size, Color color, float sw)
    {
        float ox = (float)(box.Min.X + (box.Size.X - size) / 2);
        float oy = (float)(box.Min.Y + (box.Size.Y - size) / 2);
        icon.Draw(vg, new Rect(new Float2(ox, oy), new Float2(ox + size, oy + size)), color, sw);
    }


    private static double HueFor(string name)
    {
        int hash = 0;
        foreach (char ch in name) hash = hash * 31 + ch;
        return ((hash % 360) + 360) % 360;
    }

    private static (Color, Color) GlyphColors(double hue, bool mono)
    {
        if (mono) return (Col(74, 74, 74), Col(56, 56, 56));
        return (Hsl(hue, 0.70, 0.64), Hsl(hue + 28, 0.62, 0.50));
    }

    private static Color Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        double r, g, b;
        if (s == 0) { r = g = b = l; }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = Hue2(p, q, h + 1.0 / 3); g = Hue2(p, q, h); b = Hue2(p, q, h - 1.0 / 3);
        }
        return Col((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }
    private static double Hue2(double p, double q, double t)
    {
        if (t < 0) t += 1; if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static void BeginProjectOpen()
    {
        ProjectOpenProgress.Begin();
        if (EditorApplication.Instance is { } editor)
        {
            editor.InvalidateSurface(EditorSurfaceDirtyReason.Layout);
            editor.FrameScheduler.RegisterAnimation(Runtime.Time.UnscaledTotalTime + 30);
        }
    }

    private static void OpenProjectDialog()
    {
        if (ProjectOpenProgress.IsActive) return;
        EditorApplication.OpenFileDialog(FileDialogMode.SelectFolder, path =>
        {
            if (path != null) TryOpenProject(path);
        });
    }

    private static void BrowseLocation()
    {
        EditorApplication.OpenFileDialog(FileDialogMode.SelectFolder, path =>
        {
            if (path != null) _newProjectPath = path;
        }, _newProjectPath);
    }

    private static void DrawOpenProgress(Paper paper, Scribe.FontFile font)
    {
        using (paper.Column("pl_progress").Height(ProgressFooterHeight).Padding(26, 26, 8, 10).Enter())
        {
            paper.Box("pl_progress_step").Height(18)
                .Text(Loc.Get(ProjectOpenProgress.StepKey), font)
                .TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft);

            // Unity-style detail line: "编译 XEngine.XGUI.Runtime (3/18)…" while the scripts step
            // runs. Hidden when empty so other steps keep the compact single-line footer.
            string detail = ProjectOpenProgress.Detail;
            if (!string.IsNullOrEmpty(detail))
            {
                paper.Box("pl_progress_detail").Height(16)
                    .Text(detail, font)
                    .TextColor(EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft);
            }

            Origami.ProgressBar(paper, "pl_progress_bar", ProjectOpenProgress.Fraction)
                .Thickness(6)
                .Striped()
                .Show();
        }
    }

    private static void DrawTipFooter(Paper paper, Scribe.FontFile font, float dt)
    {
        TickTip(dt);
        DrawTipRow(paper, font, TipStripHeight, 1f, layoutFlow: true);
    }

    /// <summary>
    /// Draws the cycling tip strip at the bottom of the screen. Called separately from
    /// <see cref="Draw"/> so it can render on top of the intro animation while the project loads.
    /// </summary>
    public static void DrawTipStrip(Paper paper, float dt, float globalAlpha = 1f)
    {
        if (globalAlpha <= 0f) return;

        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        TickTip(dt);
        float w = paper.ScreenRect.Size.X;
        float h = paper.ScreenRect.Size.Y;
        const float stripHeight = 36f;
        float y = h - stripHeight - 16f;
        DrawTipRow(paper, font, stripHeight, globalAlpha, layoutFlow: false, x: 0, y: y, width: w);
    }

    private static void TickTip(float dt)
    {
        _tipTimer += dt;
        if (_tipTimer >= _tipDuration)
            AdvanceTip();
    }

    private static void DrawTipRow(Paper paper, Scribe.FontFile font, float stripHeight, float globalAlpha,
        bool layoutFlow, float x = 0, float y = 0, float width = 0)
    {
        float fadeIn = Math.Min(_tipTimer / _tipFadeTime, 1f);
        float fadeOut = Math.Min((_tipDuration - _tipTimer) / _tipFadeTime, 1f);
        float alpha = Math.Clamp(Math.Min(fadeIn, fadeOut), 0f, 1f) * Math.Clamp(globalAlpha, 0f, 1f);

        var iconBase = EditorTheme.Ink400;
        var textBase = EditorTheme.Ink400;
        var labelBase = EditorTheme.Ink300;
        var iconColor = Color.FromArgb((int)(iconBase.A * alpha), iconBase.R, iconBase.G, iconBase.B);
        var textColor = Color.FromArgb((int)(textBase.A * alpha), textBase.R, textBase.G, textBase.B);
        var labelColor = Color.FromArgb((int)(labelBase.A * alpha), labelBase.R, labelBase.G, labelBase.B);
        string tipText = Loc.Get(_tipKeys[_tipIndex]);

        var row = paper.Row("pl_tip_strip").Height(stripHeight).RowBetween(6)
            .BackgroundColor(EditorTheme.Field)
            .OnClick(_ => AdvanceTip());
        if (layoutFlow)
            row.Padding(16, 16, 0, 0);
        else
            row.PositionType(PositionType.SelfDirected).Position(x, y).Size(width, stripHeight)
                .ChildLeft(UnitValue.StretchOne).ChildRight(UnitValue.StretchOne);

        using (row.Enter())
        {
            paper.Box("pl_tip_icon").Width(20).Height(stripHeight).IsNotInteractable()
                .Icon(paper, new EditorGlyphIcon(EditorIcons.Lightbulb, weight: GlyphWeight.Solid), iconColor, size: 12f);

            paper.Box("pl_tip_label").Width(UnitValue.Auto).Height(stripHeight)
                .Text(Loc.Get("launcher.tip_label"), font)
                .TextColor(labelColor)
                .FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft);

            paper.Box("pl_tip_text").Width(UnitValue.Auto).Height(stripHeight)
                .Text(tipText, font)
                .TextColor(textColor)
                .FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft);
        }
    }

    private static void TryOpenProject(string path)
    {
        if (ProjectOpenProgress.IsActive || _readingProjectVersion) return;
        OpenProjectAfterVersionRead(path).Forget();
    }

    private static async XTaskVoid OpenProjectAfterVersionRead(string path)
    {
        _readingProjectVersion = true;
        string? stamped;
        try
        {
            // An external-volume read can wait on the OS. Keep pumping the native window
            // while it completes so the launcher and any system permission UI remain responsive.
            await XTask.SwitchToThreadPool();
            stamped = ProjectEngineVersion.Read(path);
        }
        finally
        {
            await XTask.SwitchToMainThread();
            _readingProjectVersion = false;
        }

        // Unity parity: a project stamped with a different engine version raises the upgrade
        // prompt before anything loads. Missing stamp (pre-versioning project) prompts too.
        if (stamped == null || !string.Equals(ProjectEngineVersion.Canonicalize(stamped),
            XEngine.Editor.Core.EditorApplication.CanonicalEngineVersion, StringComparison.OrdinalIgnoreCase))
        {
            Origami.Confirm(
                "XEngine",
                $"This project was last opened with XEngine {stamped ?? "an older version"}."
                + $"Current engine is XEngine {XEngine.Editor.Core.EditorApplication.EngineVersion}."
                + "\nOpening will upgrade the project. Continue?",
                onYes: () => ProceedOpenProject(path));
            return;
        }

        ProceedOpenProject(path);
    }

    private static void ProceedOpenProject(string path)
    {
        if (ProjectOpenProgress.IsActive) return;
        try
        {
            var project = Project.Open(path, exclusive: true);
            project.SetActive();
            BeginProjectOpen();
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError(Loc.Get("launcher.open_failed", new { message = ex.Message }));
            Toasts.Warning(Loc.Get("launcher.open_failed_title"), ex.Message);
        }
    }

    private static void TryCreateProject()
    {
        if (ProjectOpenProgress.IsActive || _readingProjectVersion) return;
        if (string.IsNullOrWhiteSpace(_newProjectName))
        {
            Toasts.Show(Loc.Get("launcher.invalid_name"), Loc.Get("launcher.name_empty"), ToastType.Warning, 3f);
            return;
        }

        string targetPath = Path.Combine(_newProjectPath, _newProjectName);
        if (Directory.Exists(targetPath) && Directory.GetFileSystemEntries(targetPath).Length > 0)
        {
            Toasts.Show(Loc.Get("launcher.folder_exists"), Loc.Get("launcher.folder_exists_msg", new { name = _newProjectName }), ToastType.Error, 5f);
            return;
        }

        try
        {
            var project = Project.Create(_newProjectPath, _newProjectName, exclusive: true);
            project.SetActive();
            BeginProjectOpen();
        }
        catch (Exception ex)
        {
            Toasts.Show(Loc.Get("launcher.create_failed"), ex.Message, ToastType.Error, 5f);
        }
    }

    private static string FormatTimeAgo(DateTime utcTime)
    {
        var span = DateTime.UtcNow - utcTime;
        if (span.TotalMinutes < 1) return Loc.Get("launcher.just_now");
        if (span.TotalHours < 1) return Loc.Get("launcher.minutes_ago", new { count = (int)span.TotalMinutes });
        if (span.TotalDays < 1) return Loc.Get("launcher.hours_ago", new { count = (int)span.TotalHours });
        if (span.TotalDays < 30) return Loc.Get("launcher.days_ago", new { count = (int)span.TotalDays });
        if (span.TotalDays < 365) return Loc.Get("launcher.months_ago", new { count = (int)(span.TotalDays / 30) });
        return Loc.Get("launcher.years_ago", new { count = (int)(span.TotalDays / 365) });
    }
}
