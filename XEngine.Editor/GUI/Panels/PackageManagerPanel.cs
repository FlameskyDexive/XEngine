// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using XEngine.Async;
using XEngine.Editor.Core;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Scribe;

using TextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

/// <summary>Catalog-backed, dockable package browser modeled after the Unity 6 package workflow.</summary>
public sealed class PackageManagerPanel : DockPanel
{
    private const string All = "all";
    private const string InProject = "in-project";
    private const string Updates = "updates";
    private const string Samples = "samples";
    private const string Registry = "registry";
    private const string BuiltIn = "built-in";
    private const string Embedded = "embedded";
    private const string Local = "local";
    private const string Git = "git";
    private const float FooterHeight = 25f;

    private readonly List<PackageCatalogEntry> _filtered = new();
    private PackageCatalogSnapshot? _snapshot;
    private string _filter = All;
    private string _search = "";
    private string _selectedPackage = "";
    private string _selectedVersion = "";
    private string _tab = "Details";
    private string _status = "";
    private bool _refreshing;
    private bool _operating;
    private bool _sortAscending = true;

    [MenuItem("Window/Package Management/Package Manager", priority: 5)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(PackageManagerPanel));

    public override string Title => "Package Manager";
    public override string Icon => EditorIcons.BoxOpen;
    public override float HeaderWidth => 34f;

    public override void OnHeaderContent(Paper paper, float width, float height)
        => Origami.Button(paper, "pmw_header_refresh", EditorIcons.ArrowsRotate, RequestRefresh)
            .Width(MathF.Min(28f, width)).Height(MathF.Min(26f, height)).Disabled(_refreshing || _operating)
            .Tooltip("Refresh package catalog").Show();

    public override void OnGUI(Paper paper, float width, float height)
    {
        FontFile? font = EditorTheme.DefaultFont;
        if (font == null) return;
        if (Project.Current == null)
        {
            EditorGUI.EmptyState(paper, "pmw_no_project", "Open a project to manage packages.", font);
            return;
        }

        if (_snapshot == null && !_refreshing)
            RequestRefresh();

        DrawToolbar(paper, font, width);
        PackageManagerLayout layout = ComputeLayout(width, height);
        float workspaceHeight = layout.WorkspaceHeight;
        bool compact = layout.Compact;
        float railWidth = layout.RailWidth;
        float listWidth = layout.ListWidth;
        float detailWidth = layout.DetailWidth;

        using (paper.Row("pmw_workspace").Width(width).Height(workspaceHeight).Clip().Enter())
        {
            if (!compact)
            {
                DrawRail(paper, font, railWidth, workspaceHeight);
                paper.Box("pmw_rail_divider").Width(1).Height(workspaceHeight)
                    .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
            }
            DrawList(paper, font, listWidth, workspaceHeight);
            paper.Box("pmw_list_divider").Width(1).Height(workspaceHeight)
                .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
            DrawDetails(paper, font, detailWidth, workspaceHeight);
        }

        string footer = _refreshing
            ? "Refreshing package catalog..."
            : _operating
                ? _status
            : !string.IsNullOrWhiteSpace(_status)
                ? _status
                : $"{_snapshot?.Entries.Count ?? 0} packages in catalog";
        paper.Box("pmw_footer").Width(width).Height(FooterHeight).Padding(9, 9, 0, 0)
            .Text(footer, font).TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
    }

    private void DrawToolbar(Paper paper, FontFile font, float width)
    {
        using (paper.Row("pmw_toolbar").Width(width).Height(66).Padding(8, 8, 6, 4).RowBetween(6).Enter())
        {
            Origami.SearchField(paper, "pmw_search", _search, value => _search = value, "Search packages...")
                .Width(UnitValue.StretchOne).Show();
            Origami.Button(paper, "pmw_sort", $"{EditorIcons.Sort} {(_sortAscending ? "A-Z" : "Z-A")}",
                () => _sortAscending = !_sortAscending).Width(74).Show();
            Origami.Button(paper, "pmw_clear", EditorIcons.Filter, () =>
            {
                _search = "";
                _filter = All;
            }).Width(30).Tooltip("Clear filters").Show();
        }
    }

    private void DrawRail(Paper paper, FontFile font, float width, float height)
    {
        using (paper.Column("pmw_rail").Width(width).Height(height).Padding(8, 8, 8, 8)
            .BackgroundColor(EditorTheme.Neutral200).ColBetween(2).Enter())
        {
            RailHeader(paper, font, "Project", "project");
            RailItem(paper, font, "all", "All Packages", EditorIcons.BoxOpen, All, Count(All), width);
            RailItem(paper, font, "project", "In Project", EditorIcons.Folder, InProject, Count(InProject), width);
            RailItem(paper, font, "updates", "Updates", EditorIcons.ArrowUp, Updates, Count(Updates), width);
            RailItem(paper, font, "samples", "Samples", EditorIcons.FileLines, Samples, Count(Samples), width);
            RailHeader(paper, font, "Sources", "sources");
            RailItem(paper, font, "registry", "XEngine Registry", EditorIcons.CloudArrowDown, Registry, Count(Registry), width);
            RailItem(paper, font, "builtin", "Built-in", EditorIcons.Cube, BuiltIn, Count(BuiltIn), width);
            RailItem(paper, font, "embedded", "Embedded", EditorIcons.Link, Embedded, Count(Embedded), width);
            RailItem(paper, font, "local", "Local", EditorIcons.FolderOpen, Local, Count(Local), width);
            RailItem(paper, font, "git", "Git", EditorIcons.CodeBranch, Git, Count(Git), width);
            paper.Box("pmw_rail_spacer").Height(UnitValue.StretchOne).IsNotInteractable();
            paper.Box("pmw_rail_manifest").Height(22).Text("manifest.json + lock", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 2)
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
        }
    }

    private static void RailHeader(Paper paper, FontFile font, string label, string id)
        => paper.Box("pmw_rail_header_" + id).Height(24).Padding(5, 5, 0, 0)
            .Text(label.ToUpperInvariant(), font).TextColor(EditorTheme.InkDim)
            .FontSize(EditorTheme.FontSizeSmall - 1).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

    private void RailItem(Paper paper, FontFile font, string id, string label, string icon,
        string filter, int count, float width)
    {
        bool selected = _filter == filter;
        using (paper.Row("pmw_rail_" + id).Width(width - 16f).Height(28).Rounded(4).Padding(6, 6, 0, 0)
            .BackgroundColor(selected ? EditorTheme.Selected : System.Drawing.Color.Transparent)
            .Hovered.BackgroundColor(selected ? EditorTheme.Selected : EditorTheme.Hover).End()
            .OnClick(filter, (value, _) => _filter = value).Enter())
        {
            paper.Box("pmw_rail_icon_" + id).Width(16).Height(28).Text(icon, font)
                .TextColor(selected ? EditorTheme.AccentText : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
            paper.Box("pmw_rail_label_" + id).Width(UnitValue.StretchOne).Height(28).Margin(7, 0, 0, 0)
                .Text(label, font).TextColor(selected ? EditorTheme.Ink500 : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            paper.Box("pmw_rail_count_" + id).Width(27).Height(28).Text(count.ToString(), font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
                .Alignment(TextAlignment.MiddleRight).IsNotInteractable();
        }
    }

    private void DrawList(Paper paper, FontFile font, float width, float height)
    {
        RebuildFiltered();
        if (_filtered.Count > 0 && FindEntry(_selectedPackage) == null)
        {
            _selectedPackage = _filtered[0].Name;
            _selectedVersion = DefaultSelectedVersion(_filtered[0]);
        }

        using (paper.Column("pmw_list_panel").Width(width).Height(height).BackgroundColor(EditorTheme.Neutral300).Enter())
        {
            paper.Box("pmw_list_title").Height(34).Padding(10, 10, 0, 0).Text("Packages", font)
                .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSize)
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            Origami.ScrollView(paper, "pmw_list_scroll", width, height - 34f).Body(() =>
            {
                using (paper.Column("pmw_list_content").Height(UnitValue.Auto).Padding(4, 4, 0, 4).ColBetween(1).Enter())
                {
                    if (_filtered.Count == 0)
                    {
                        EditorGUI.EmptyState(paper, "pmw_list_empty", _refreshing ? "Refreshing catalog..." : "No packages match.", font);
                        return;
                    }
                    for (int i = 0; i < _filtered.Count; i++)
                        DrawPackageRow(paper, font, _filtered[i], i);
                }
            });
        }
    }

    private void DrawPackageRow(Paper paper, FontFile font, PackageCatalogEntry entry, int index)
    {
        bool selected = string.Equals(_selectedPackage, entry.Name, StringComparison.OrdinalIgnoreCase);
        string version = entry.InstalledVersion ?? entry.LatestVersion ?? "unknown";
        string state = StateLabel(entry.State);
        using (paper.Row($"pmw_row_{index}").Height(42).Rounded(3).Padding(8, 7, 0, 0)
            .BackgroundColor(selected ? EditorTheme.Selected : System.Drawing.Color.Transparent)
            .Hovered.BackgroundColor(selected ? EditorTheme.Selected : EditorTheme.Hover).End()
            .OnClick(entry.Name, (name, _) => SelectPackage(name)).Enter())
        {
            paper.Box($"pmw_row_icon_{index}").Width(18).Height(42).Text(EditorIcons.Box, font)
                .TextColor(StateColor(entry.State)).FontSize(12).Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
            using (paper.Column($"pmw_row_body_{index}").Width(UnitValue.StretchOne).Height(40).Margin(7, 0, 0, 0).Enter())
            {
                paper.Box($"pmw_row_name_{index}").Height(21).Text(entry.DisplayName, font)
                    .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                paper.Box($"pmw_row_meta_{index}").Height(17).Text($"{version}  {state}", font)
                    .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 2)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            }
        }
    }

    private void DrawDetails(Paper paper, FontFile font, float width, float height)
    {
        PackageCatalogEntry? entry = FindEntry(_selectedPackage);
        using (paper.Column("pmw_details").Width(width).Height(height).BackgroundColor(EditorTheme.Neutral200).Enter())
        {
            if (entry == null)
            {
                EditorGUI.EmptyState(paper, "pmw_details_empty", "Select a package to inspect it.", font);
                return;
            }

            using (paper.Row("pmw_details_header").Height(76).Padding(14, 14, 10, 6).RowBetween(8).Enter())
            {
                using (paper.Column("pmw_details_title").Width(UnitValue.StretchOne).Height(62).Enter())
                {
                    paper.Box("pmw_details_name").Height(29).Text(entry.DisplayName, font)
                        .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSizeLarge)
                        .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                    paper.Box("pmw_details_id").Height(20).Text(entry.Name, font)
                        .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
                        .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                }
                DrawActionButton(paper, entry);
            }

            DrawVersionAndLinks(paper, font, entry, width);
            using (paper.Row("pmw_detail_tabs").Height(31).Padding(8, 8, 0, 0).RowBetween(2).Enter())
            {
                DetailTab(paper, font, "Details");
                DetailTab(paper, font, "Versions");
                DetailTab(paper, font, "Dependencies");
                DetailTab(paper, font, "Samples");
            }
            paper.Box("pmw_detail_line").Height(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

            Origami.ScrollView(paper, "pmw_detail_scroll", width, MathF.Max(80, height - 143f)).Body(() =>
            {
                using (paper.Column("pmw_detail_body").Height(UnitValue.Auto).Padding(14, 14, 10, 12).ColBetween(7).Enter())
                {
                    switch (_tab)
                    {
                        case "Versions": DrawVersions(paper, font, entry); break;
                        case "Dependencies": DrawDependencies(paper, font, entry); break;
                        case "Samples": DrawSamples(paper, font, entry); break;
                        default: DrawOverview(paper, font, entry); break;
                    }
                }
            });
        }
    }

    private void DrawActionButton(Paper paper, PackageCatalogEntry entry)
    {
        string label;
        bool disabled = _refreshing || _operating;
        Action action;
        PackageCatalogVersion? selected = FindSelectedVersion(entry);
        bool changingVersion = entry.InstalledVersion != null && selected != null
            && !string.Equals(entry.InstalledVersion, selected.Version, StringComparison.OrdinalIgnoreCase);
        if (entry.InstalledVersion == null && entry.State == PackageInstallState.Available)
        {
            label = "Install";
            action = () => RunInstall(entry);
        }
        else if (changingVersion)
        {
            label = IsNewer(selected!.Version, entry.InstalledVersion!) ? "Update" : "Change";
            action = () => RunInstall(entry);
        }
        else if (entry.IsDirectDependency)
        {
            label = "Remove";
            action = () => RunRemove(entry);
        }
        else
        {
            label = entry.State == PackageInstallState.BuiltIn ? "Built-in" : "Dependency";
            action = static () => { };
            disabled = true;
        }
        Origami.Button(paper, "pmw_detail_action", label, action).Width(92).Disabled(disabled).Show();
    }

    private void DrawVersionAndLinks(Paper paper, FontFile font, PackageCatalogEntry entry, float width)
    {
        PackageCatalogVersion? selected = FindSelectedVersion(entry);
        bool compact = width < 380f;
        using (paper.Row("pmw_version_links").Width(width).Height(35).Padding(14, 14, 0, 4).RowBetween(7).Enter())
        {
            if (entry.Versions.Count > 0 && selected != null)
            {
                Origami.Dropdown(paper, "pmw_version", selected, value => _selectedVersion = value.Version, entry.Versions)
                    .Display(static value => value.Version).Secondary(static value => SourceLabel(value.Source))
                    .Width(126).Height(27).Show();
            }
            paper.Box("pmw_links_spacer").Width(UnitValue.StretchOne).Height(27).IsNotInteractable();
            string root = selected?.RootPath ?? entry.Resolved?.ResolvedRoot ?? "";
            LinkButton(paper, "docs", "Docs", EditorIcons.BookOpen, root, entry.Metadata?.DocumentationUrl, compact);
            LinkButton(paper, "changes", "Changelog", EditorIcons.ClockRotateLeft, root, entry.Metadata?.ChangelogUrl, compact);
            LinkButton(paper, "license", "License", EditorIcons.ScaleBalanced, root, entry.Metadata?.LicensesUrl, compact);
        }
    }

    private static void LinkButton(Paper paper, string id, string label, string icon, string root, string? url, bool compact)
        => Origami.Button(paper, "pmw_link_" + id, compact ? icon : label, () => OpenPackageLink(root, url))
            .Width(compact ? 28 : UnitValue.Auto).Ghost().Tooltip(label)
            .Disabled(string.IsNullOrWhiteSpace(url)).Show();

    private void DetailTab(Paper paper, FontFile font, string tab)
    {
        bool selected = _tab == tab;
        paper.Box("pmw_tab_" + tab.ToLowerInvariant()).Width(UnitValue.Auto).Height(30).Padding(8, 8, 0, 0)
            .Text(tab, font).TextColor(selected ? EditorTheme.Ink500 : EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter)
            .BackgroundColor(selected ? EditorTheme.Selected : System.Drawing.Color.Transparent)
            .Hovered.BackgroundColor(selected ? EditorTheme.Selected : EditorTheme.Hover).End()
            .OnClick(tab, (value, _) => _tab = value);
    }

    private static void DrawOverview(Paper paper, FontFile font, PackageCatalogEntry entry)
    {
        EditorGUI.Group(paper, "pmw_info", "Package information", () =>
        {
            InfoRow(paper, font, "Technical Name", entry.Name, "name");
            InfoRow(paper, font, "Installed", entry.InstalledVersion ?? "Not installed", "installed");
            InfoRow(paper, font, "Latest", entry.LatestVersion ?? "Unknown", "latest");
            InfoRow(paper, font, "Source", SourceLabel(entry.Source), "source");
            InfoRow(paper, font, "Category", string.IsNullOrWhiteSpace(entry.Category) ? "Uncategorized" : entry.Category, "category");
            InfoRow(paper, font, "Minimum Engine", entry.Metadata?.MinimumEngineVersion ?? "Any supported version", "engine");
        }, EditorIcons.CircleInfo);

        EditorGUI.Group(paper, "pmw_description", "Description", () =>
        {
            paper.Box("pmw_description_text").Height(UnitValue.Auto)
                .Text(string.IsNullOrWhiteSpace(entry.Description) ? "No description provided." : entry.Description, font)
                .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.Left).Wrap(TextWrapMode.Wrap).IsNotInteractable();
        }, EditorIcons.FileLines);

        if (entry.Resolved is { Warnings.Count: > 0 } resolved)
        {
            EditorGUI.Group(paper, "pmw_warnings", "Warnings", () =>
            {
                for (int i = 0; i < resolved.Warnings.Count; i++)
                    paper.Box("pmw_warning_" + i).Height(UnitValue.Auto).Text(resolved.Warnings[i], font)
                        .TextColor(EditorTheme.Amber400).FontSize(EditorTheme.FontSizeSmall)
                        .Wrap(TextWrapMode.Wrap).IsNotInteractable();
            }, EditorIcons.TriangleExclamation);
        }
    }

    private static void DrawVersions(Paper paper, FontFile font, PackageCatalogEntry entry)
    {
        EditorGUI.Group(paper, "pmw_versions", "Available versions", () =>
        {
            for (int i = 0; i < entry.Versions.Count; i++)
            {
                PackageCatalogVersion version = entry.Versions[i];
                string suffix = string.Equals(entry.InstalledVersion, version.Version, StringComparison.OrdinalIgnoreCase)
                    ? "Installed"
                    : SourceLabel(version.Source);
                InfoRow(paper, font, version.Version, suffix, "version_" + i);
            }
        }, EditorIcons.Clock);
    }

    private static void DrawDependencies(Paper paper, FontFile font, PackageCatalogEntry entry)
    {
        PackageJson? metadata = entry.Metadata;
        EditorGUI.Group(paper, "pmw_dependencies", "Dependencies", () =>
        {
            if (metadata == null || metadata.Dependencies.Count == 0)
            {
                EditorGUI.EmptyState(paper, "pmw_no_dependencies", "This package has no dependencies.", font);
                return;
            }
            int index = 0;
            foreach (KeyValuePair<string, string> dependency in metadata.Dependencies)
            {
                InfoRow(paper, font, dependency.Key, dependency.Value, "dependency_" + index);
                index++;
            }
        }, EditorIcons.Link);
    }

    private void DrawSamples(Paper paper, FontFile font, PackageCatalogEntry entry)
    {
        EditorGUI.Group(paper, "pmw_samples", "Samples", () =>
        {
            if (entry.Metadata == null || entry.Metadata.Samples.Count == 0)
            {
                EditorGUI.EmptyState(paper, "pmw_no_samples", "No samples are included with this package.", font);
                return;
            }
            for (int i = 0; i < entry.Metadata.Samples.Count; i++)
            {
                PackageSample sample = entry.Metadata.Samples[i];
                using (paper.Row("pmw_sample_" + i).Height(34).RowBetween(6).Enter())
                {
                    using (paper.Column("pmw_sample_text_" + i).Width(UnitValue.StretchOne).Height(34).Enter())
                    {
                        paper.Box("pmw_sample_name_" + i).Height(18).Text(sample.DisplayName, font)
                            .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                        paper.Box("pmw_sample_desc_" + i).Height(16).Text(sample.Description ?? sample.Path, font)
                            .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 2)
                            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                    }
                    PackageSample captured = sample;
                    Origami.Button(paper, "pmw_sample_import_" + i, "Import", () => ImportSample(entry, captured))
                        .Width(68).Disabled(entry.Resolved == null || _refreshing || _operating).Show();
                }
            }
        }, EditorIcons.FileLines);
    }

    private void RequestRefresh()
    {
        if (_refreshing || _operating || Project.Current == null) return;
        _refreshing = true;
        _status = "";
        RefreshAsync(Project.Current).Forget();
    }

    private async XTaskVoid RefreshAsync(Project project)
    {
        PackageCatalogSnapshot? snapshot = null;
        string? error = null;
        try
        {
            await XTask.SwitchToThreadPool();
            // Local build first, then npm registry search + packuments (update detection).
            snapshot = await PackageCatalog.BuildWithRemoteAsync(project);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        await XTask.SwitchToMainThread();
        if (Project.Current == project)
        {
            _snapshot = snapshot;
            if (error != null)
                _status = "Refresh failed: " + error;
            else if (snapshot is { Problems.Count: > 0 })
                _status = $"Package catalog refreshed ({snapshot.Problems.Count} registry problem(s): {snapshot.Problems[0]})";
            else
                _status = "Package catalog refreshed.";
        }
        _refreshing = false;
    }

    private void RunInstall(PackageCatalogEntry entry)
    {
        if (Project.Current == null || _operating) return;
        PackageCatalogVersion? version = FindSelectedVersion(entry);
        if (version == null)
        {
            _status = "No installable version is available.";
            return;
        }
        _operating = true;
        _status = $"Installing {entry.Name} {version.Version}...";
        RunInstallAsync(Project.Current, entry.Name, version.SourceSpecifier).Forget();
    }

    private void RunRemove(PackageCatalogEntry entry)
    {
        if (Project.Current == null || _operating) return;
        _operating = true;
        _status = $"Removing {entry.Name}...";
        RunRemoveAsync(Project.Current, entry.Name).Forget();
    }

    private void ImportSample(PackageCatalogEntry entry, PackageSample sample)
    {
        if (Project.Current == null || entry.Resolved == null || _operating) return;
        string source = Path.GetFullPath(Path.Combine(entry.Resolved.ResolvedRoot, sample.Path));
        string packageRoot = Path.GetFullPath(entry.Resolved.ResolvedRoot) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source))
        {
            _status = $"Sample path '{sample.Path}' is invalid.";
            return;
        }
        string destination = Path.Combine(Project.Current.AssetsPath, "Samples", entry.DisplayName, sample.DisplayName);
        if (Directory.Exists(destination))
        {
            _status = $"Sample already exists at '{destination}'.";
            return;
        }
        _operating = true;
        _status = $"Importing sample '{sample.DisplayName}'...";
        ImportSampleAsync(Project.Current, source, destination, sample.DisplayName).Forget();
    }

    private async XTaskVoid RunInstallAsync(Project project, string packageName, string sourceSpecifier)
    {
        PackageOperationResult result;
        try
        {
            await XTask.SwitchToThreadPool();
            result = PackageOperations.Install(project, packageName, sourceSpecifier);
        }
        catch (Exception ex)
        {
            result = PackageOperationResult.Fail($"Could not install '{packageName}': {ex.Message}");
        }
        await CompleteOperation(project, result);
    }

    private async XTaskVoid RunRemoveAsync(Project project, string packageName)
    {
        PackageOperationResult result;
        try
        {
            await XTask.SwitchToThreadPool();
            result = PackageOperations.Remove(project, packageName);
        }
        catch (Exception ex)
        {
            result = PackageOperationResult.Fail($"Could not remove '{packageName}': {ex.Message}");
        }
        await CompleteOperation(project, result);
    }

    private async XTaskVoid ImportSampleAsync(Project project, string source, string destination, string displayName)
    {
        PackageOperationResult result;
        try
        {
            await XTask.SwitchToThreadPool();
            CopyDirectory(source, destination);
            result = PackageOperationResult.Ok($"Imported sample '{displayName}'.");
        }
        catch (Exception ex)
        {
            result = PackageOperationResult.Fail($"Could not import sample '{displayName}': {ex.Message}");
        }
        await CompleteOperation(project, result);
    }

    private async XTask CompleteOperation(Project project, PackageOperationResult result)
    {
        await XTask.SwitchToMainThread();
        _operating = false;
        if (Project.Current != project)
            return;
        _status = result.Message;
        if (result.Success)
            RequestRefresh();
    }

    private void RebuildFiltered()
    {
        _filtered.Clear();
        if (_snapshot == null) return;
        string search = _search.Trim();
        for (int i = 0; i < _snapshot.Entries.Count; i++)
        {
            PackageCatalogEntry entry = _snapshot.Entries[i];
            if (!MatchesFilter(entry)) continue;
            if (search.Length > 0
                && !entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !entry.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !entry.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            _filtered.Add(entry);
        }
        _filtered.Sort(_sortAscending ? CompareAscending : CompareDescending);
    }

    private bool MatchesFilter(PackageCatalogEntry entry) => _filter switch
    {
        InProject => entry.InstalledVersion != null,
        Updates => entry.State == PackageInstallState.UpdateAvailable,
        Samples => entry.HasSamples,
        Registry => entry.Source == PackageSource.Registry,
        BuiltIn => entry.Source == PackageSource.BuiltIn,
        Embedded => entry.Source == PackageSource.Embed,
        Local => entry.Source == PackageSource.Local,
        Git => entry.Source == PackageSource.Git,
        _ => true,
    };

    private int Count(string filter)
    {
        if (_snapshot == null) return 0;
        string previous = _filter;
        _filter = filter;
        int count = 0;
        for (int i = 0; i < _snapshot.Entries.Count; i++)
            if (MatchesFilter(_snapshot.Entries[i])) count++;
        _filter = previous;
        return count;
    }

    private PackageCatalogEntry? FindEntry(string name)
    {
        if (_snapshot == null) return null;
        for (int i = 0; i < _snapshot.Entries.Count; i++)
        {
            if (string.Equals(_snapshot.Entries[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return _snapshot.Entries[i];
        }
        return null;
    }

    private void SelectPackage(string name)
    {
        _selectedPackage = name;
        PackageCatalogEntry? entry = FindEntry(name);
        _selectedVersion = entry == null ? "" : DefaultSelectedVersion(entry);
    }

    private PackageCatalogVersion? FindSelectedVersion(PackageCatalogEntry entry)
    {
        for (int i = 0; i < entry.Versions.Count; i++)
            if (string.Equals(entry.Versions[i].Version, _selectedVersion, StringComparison.OrdinalIgnoreCase))
                return entry.Versions[i];
        return entry.Versions.Count > 0 ? entry.Versions[0] : null;
    }

    private static void InfoRow(Paper paper, FontFile font, string label, string value, string id)
    {
        using (paper.Row("pmw_info_" + id).Height(24).RowBetween(6).Enter())
        {
            paper.Box("pmw_info_label_" + id).Width(116).Height(24).Text(label, font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            paper.Box("pmw_info_value_" + id).Width(UnitValue.StretchOne).Height(24).Text(value, font)
                .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
        }
    }

    private static string StateLabel(PackageInstallState state) => state switch
    {
        PackageInstallState.Available => "Available",
        PackageInstallState.Installed => "Installed",
        PackageInstallState.InstalledTransitively => "Dependency",
        PackageInstallState.BuiltIn => "Built-in",
        PackageInstallState.UpdateAvailable => "Update available",
        PackageInstallState.Unsupported => "Unsupported",
        _ => state.ToString(),
    };

    private static System.Drawing.Color StateColor(PackageInstallState state) => state switch
    {
        PackageInstallState.Available => EditorTheme.Blue400,
        PackageInstallState.UpdateAvailable => EditorTheme.Amber400,
        PackageInstallState.Unsupported => EditorTheme.Red400,
        PackageInstallState.BuiltIn => EditorTheme.Ink300,
        _ => EditorTheme.Green400,
    };

    private static string SourceLabel(PackageSource source) => source switch
    {
        PackageSource.BuiltIn => "Built-in",
        PackageSource.Embed => "Embedded",
        PackageSource.Local => "Local",
        PackageSource.Git => "Git",
        PackageSource.Registry => "XEngine Registry",
        _ => source.ToString(),
    };

    internal readonly record struct PackageManagerLayout(
        bool Compact, float RailWidth, float ListWidth, float DetailWidth, float WorkspaceHeight);

    internal static PackageManagerLayout ComputeLayout(float width, float height)
    {
        float workspaceHeight = MathF.Max(180f, height - 66f - FooterHeight);
        bool compact = width < 700f;
        float railWidth = compact ? 0f : Math.Clamp(width * 0.2f, 150f, 190f);
        float listWidth = compact ? Math.Clamp(width * 0.42f, 190f, 280f) : Math.Clamp(width * 0.32f, 230f, 340f);
        float gutter = compact ? 1f : 2f;
        float detailWidth = MathF.Max(220f, width - railWidth - listWidth - gutter);
        return new PackageManagerLayout(compact, railWidth, listWidth, detailWidth, workspaceHeight);
    }

    private static int CompareAscending(PackageCatalogEntry left, PackageCatalogEntry right)
        => StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);

    private static int CompareDescending(PackageCatalogEntry left, PackageCatalogEntry right)
        => StringComparer.OrdinalIgnoreCase.Compare(right.DisplayName, left.DisplayName);

    private static string DefaultSelectedVersion(PackageCatalogEntry entry)
        => entry.State == PackageInstallState.UpdateAvailable
            ? entry.LatestVersion ?? entry.InstalledVersion ?? ""
            : entry.InstalledVersion ?? entry.LatestVersion ?? "";

    private static bool IsNewer(string candidate, string installed)
        => SemanticVersion.TryParse(candidate, out SemanticVersion candidateVersion)
            && SemanticVersion.TryParse(installed, out SemanticVersion installedVersion)
            && candidateVersion > installedVersion;

    private static void OpenPackageLink(string root, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        string target = url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            string fullRoot = Path.GetFullPath(root);
            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, url));
            if (!fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            target = fullPath;
        }
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Runtime.Debug.LogWarning($"Could not open '{url}': {ex.Message}"); }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        for (int i = 0; i < files.Length; i++)
        {
            string relative = Path.GetRelativePath(source, files[i]);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(files[i], target, overwrite: false);
        }
    }
}
