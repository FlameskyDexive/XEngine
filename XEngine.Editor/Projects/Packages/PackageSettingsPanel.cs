// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Core;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// Unity-style package browser for the project's <c>Packages/manifest.json</c>. The resolver remains
/// the source of truth; this panel only provides the three-pane browsing and manifest editing surface.
/// </summary>
[ProjectSettings("Packages", EditorIcons.BoxOpen, order: 10, exportToBuild: false)]
public sealed class PackageSettingsPanel : ProjectSettingsBase
{
    private const string All = "all";
    private const string InProject = "in-project";
    private const string BuiltIn = "built-in";
    private const string Embedded = "embedded";
    private const string Local = "local";
    private const string Git = "git";
    private const string Registry = "registry";

    private readonly Dictionary<string, string> _editBuffer = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResolvedPackage> _filteredPackages = new();
    private readonly List<KeyValuePair<string, string>> _sortedDependencies = new();
    private readonly List<string> _resolutionProblems = new();
    private string _search = "";
    private string _sourceFilter = All;
    private string _selectedName = "";
    private string _detailTab = "Details";
    private bool _sortAscending = true;
    private bool _showAdd;
    private string _newName = "";
    private string _newSpecifier = "";
    private bool _showAddRegistry;
    private string _newRegistryName = "";
    private string _newRegistryUrl = "";
    private string _newRegistryScopes = "";

    private static string ManifestPath
        => Path.Combine(Project.Current?.PackagesPath ?? "", "manifest.json");

    private static ProjectPackageManifest Load()
        => ProjectPackageManifest.ReadOrDefault(ManifestPath);

    private static void Save(ProjectPackageManifest manifest)
    {
        manifest.Write(ManifestPath);
        ReloadResolved();
    }

    /// <summary>User-initiated remount. Safe on a click; must not run from <see cref="OnGUI"/> draw.</summary>
    private static void ReloadResolved()
    {
        PackageManager.Invalidate();
        if (Project.Current != null)
            PackageManager.Resolve(Project.Current);
    }

    public override void OnGUI(Paper paper, float width)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        if (Project.Current == null)
        {
            Origami.Label(paper, "pkg_no_proj", "No project open.").Show();
            return;
        }

        using (paper.Row("pkg_open_window_row").Height(34).Padding(8, 8, 2, 4).Enter())
        {
            paper.Box("pkg_open_window_text").Width(UnitValue.StretchOne).Height(28)
                .Text("Browse, install, update, and inspect project packages in the dockable Package Manager.", font)
                .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            Origami.Button(paper, "pkg_open_window", $"{EditorIcons.BoxOpen} Open Package Manager",
                () => EditorApplication.Instance?.OpenPanel(typeof(PackageManagerPanel))).Width(176).Show();
        }

        var manifest = Load();
        var resolved = PackageManager.PeekCached(Project.Current)
            ?? (IReadOnlyList<ResolvedPackage>)Array.Empty<ResolvedPackage>();
        var filtered = FilterPackages(resolved, manifest);

        if (filtered.Count > 0 && FindPackage(filtered, _selectedName) == null)
            _selectedName = filtered[0].Name;
        var selected = FindPackage(resolved, _selectedName);

        DrawToolbar(paper, font, resolved);
        DrawFilterBar(paper, font);

        float workspaceWidth = MathF.Max(0f, width);
        bool compact = workspaceWidth < 620f;
        float sideWidth = compact ? 0f : Math.Clamp(workspaceWidth * 0.21f, 132f, 164f);
        float listWidth = compact
            ? Math.Clamp(workspaceWidth * 0.42f, 180f, 240f)
            : Math.Clamp(workspaceWidth * 0.34f, 230f, 320f);
        float dividerWidth = compact ? 1f : 2f;
        float detailWidth = MathF.Max(180f, workspaceWidth - sideWidth - listWidth - dividerWidth);
        const float workspaceHeight = 424f;

        using (paper.Row("pkg_workspace").Width(workspaceWidth).Height(workspaceHeight).Clip().Enter())
        {
            if (!compact)
            {
                DrawSourceRail(paper, font, resolved, manifest, sideWidth, workspaceHeight);
                paper.Box("pkg_rail_divider").Width(1).Height(workspaceHeight).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
            }
            DrawPackageList(paper, font, filtered, manifest, listWidth, workspaceHeight);
            paper.Box("pkg_list_divider").Width(1).Height(workspaceHeight).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
            DrawDetails(paper, font, selected, manifest, detailWidth, workspaceHeight);
        }

        DrawResolutionProblems(paper, font, manifest, resolved);
        DrawRegistries(paper, font, manifest);
        if (_showAdd)
            DrawAddPackage(paper, font, manifest);
        DrawFooter(paper, font, resolved);
    }

    private void DrawToolbar(Paper paper, Scribe.FontFile font, IReadOnlyList<ResolvedPackage> resolved)
    {
        using (paper.Row("pkg_toolbar").Height(34).Padding(8, 8, 2, 2).RowBetween(6).Enter())
        {
            paper.Box("pkg_toolbar_icon").Width(22).Height(28).Text(EditorIcons.BoxOpen, font)
                .TextColor(EditorTheme.AccentText).FontSize(14f).Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
            paper.Box("pkg_toolbar_title").Width(130).Height(28).Text("Package Manager", font)
                .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

            Origami.Button(paper, "pkg_toolbar_add", $"{EditorIcons.Plus} Add package", () => _showAdd = !_showAdd)
                .Width(112).Show();
            Origami.Button(paper, "pkg_toolbar_refresh", EditorIcons.ArrowsRotate, ReloadResolved)
                .Width(28).Tooltip("Refresh packages").Show();

            paper.Box("pkg_toolbar_spacer").Width(UnitValue.StretchOne).Height(28).IsNotInteractable();
            paper.Box("pkg_toolbar_count").Width(110).Height(28)
                .Text($"{resolved.Count} installed", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight).IsNotInteractable();
        }
    }

    private void DrawFilterBar(Paper paper, Scribe.FontFile font)
    {
        using (paper.Row("pkg_filterbar").Height(34).Padding(8, 8, 0, 4).RowBetween(6).Enter())
        {
            Origami.SearchField(paper, "pkg_search", _search, value => _search = value, "Search packages...")
                .Width(UnitValue.StretchOne).Show();

            Origami.Button(paper, "pkg_sort", $"{EditorIcons.Sort} Name {(_sortAscending ? "(asc)" : "(desc)")}",
                () => _sortAscending = !_sortAscending).Width(116).Show();
            Origami.Button(paper, "pkg_clear_filter", $"{EditorIcons.Filter} Filters", () =>
            {
                _search = "";
                _sourceFilter = All;
            }).Width(74).Show();
        }
    }

    private void DrawSourceRail(Paper paper, Scribe.FontFile font, IReadOnlyList<ResolvedPackage> resolved,
        ProjectPackageManifest manifest, float width, float height)
    {
        int inProjectCount = 0;
        int registryCount = 0;
        int builtInCount = 0;
        int embeddedCount = 0;
        int localCount = 0;
        int gitCount = 0;
        for (int i = 0; i < resolved.Count; i++)
        {
            ResolvedPackage package = resolved[i];
            if (manifest.Dependencies.ContainsKey(package.Name))
                inProjectCount++;

            switch (package.Source)
            {
                case PackageSource.Registry:
                    registryCount++;
                    break;
                case PackageSource.BuiltIn:
                    builtInCount++;
                    break;
                case PackageSource.Embed:
                    embeddedCount++;
                    break;
                case PackageSource.Local:
                    localCount++;
                    break;
                case PackageSource.Git:
                    gitCount++;
                    break;
            }
        }

        using (paper.Column("pkg_rail").Width(width).Height(height).Padding(8, 8, 8, 8)
            .BackgroundColor(EditorTheme.Neutral200).ColBetween(2).Enter())
        {
            DrawRailHeader(paper, font, "Packages", "pkg_rail_h");
            DrawRailItem(paper, font, "pkg_rail_all", "All packages", EditorIcons.BoxOpen, All,
                resolved.Count, width);
            DrawRailItem(paper, font, "pkg_rail_updates", "Updates", EditorIcons.ArrowUp, "updates", 0, width,
                muted: true);

            DrawRailHeader(paper, font, "Sources", "pkg_rail_sources");
            DrawRailItem(paper, font, "pkg_rail_project", "In project", EditorIcons.Folder, InProject,
                inProjectCount, width);
            DrawRailItem(paper, font, "pkg_rail_registry", "XEngine Registry", EditorIcons.CloudArrowDown, Registry,
                registryCount, width);
            DrawRailItem(paper, font, "pkg_rail_built", "Built-in", EditorIcons.Cube, BuiltIn,
                builtInCount, width);
            DrawRailItem(paper, font, "pkg_rail_embed", "Embedded", EditorIcons.Link, Embedded,
                embeddedCount, width);
            DrawRailItem(paper, font, "pkg_rail_local", "Local", EditorIcons.FolderOpen, Local,
                localCount, width);
            DrawRailItem(paper, font, "pkg_rail_git", "Git", EditorIcons.CodeBranch, Git,
                gitCount, width);

            paper.Box("pkg_rail_spacer").Height(UnitValue.StretchOne).IsNotInteractable();
            paper.Box("pkg_rail_manifest").Height(30).Text("manifest.json", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft).Padding(6, 6, 0, 0).IsNotInteractable();
        }
    }

    private void DrawRailHeader(Paper paper, Scribe.FontFile font, string label, string id)
    {
        paper.Box(id).Height(24).Text(label.ToUpperInvariant(), EditorTheme.FontSemiBold ?? font)
            .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
            .Alignment(TextAlignment.MiddleLeft).Padding(6, 6, 0, 0).IsNotInteractable();
    }

    private void DrawRailItem(Paper paper, Scribe.FontFile font, string id, string label, string icon,
        string filter, int count, float width, bool muted = false)
    {
        bool active = _sourceFilter == filter;
        using (paper.Row(id).Width(width - 16).Height(27).Rounded(4).Padding(6, 6, 0, 0)
            .BackgroundColor(active ? EditorTheme.Selected : System.Drawing.Color.Transparent)
            .Hovered.BackgroundColor(active ? EditorTheme.Selected : EditorTheme.Hover).End()
            .OnClick(filter, (value, _) => _sourceFilter = value).Enter())
        {
            paper.Box(id + "_i").Width(16).Height(27).Text(icon, font)
                .TextColor(muted ? EditorTheme.InkDim : active ? EditorTheme.AccentText : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
            paper.Box(id + "_l").Width(UnitValue.StretchOne).Height(27).Margin(8, 0, 0, 0).Text(label, font)
                .TextColor(muted ? EditorTheme.InkDim : active ? EditorTheme.Ink500 : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            paper.Box(id + "_c").Width(26).Height(27).Text(count.ToString(), font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
                .Alignment(TextAlignment.MiddleRight).IsNotInteractable();
        }
    }

    private void DrawPackageList(Paper paper, Scribe.FontFile font, IReadOnlyList<ResolvedPackage> packages,
        ProjectPackageManifest manifest, float width, float height)
    {
        using (paper.Column("pkg_list_panel").Width(width).Height(height).BackgroundColor(EditorTheme.Neutral300).Enter())
        {
            using (paper.Row("pkg_list_head").Height(34).Padding(10, 8, 3, 3).Enter())
                paper.Box("pkg_list_head_t").Height(28).Text("Packages", EditorTheme.FontSemiBold ?? font)
                    .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

            float listHeight = height - 34f;
            Origami.ScrollView(paper, "pkg_list_scroll", width, listHeight).Body(() =>
            {
                using (paper.Column("pkg_list_content").Height(UnitValue.Auto).Padding(4, 4, 0, 4).ColBetween(1).Enter())
                {
                    if (packages.Count == 0)
                    {
                        EditorGUI.EmptyState(paper, "pkg_empty", "No packages match the current filters.", font);
                        return;
                    }

                    for (int i = 0; i < packages.Count; i++)
                        DrawPackageRow(paper, font, packages[i], manifest, i);
                }
            });
        }
    }

    private void DrawPackageRow(Paper paper, Scribe.FontFile font, ResolvedPackage package,
        ProjectPackageManifest manifest, int index)
    {
        bool active = string.Equals(_selectedName, package.Name, StringComparison.OrdinalIgnoreCase);
        var sourceColor = SourceColor(package.Source);
        string version = string.IsNullOrWhiteSpace(package.Version) || package.Version == "0.0.0" ? "local" : package.Version;
        using (paper.Row($"pkg_row_{index}").Height(34).Rounded(3).Padding(8, 6, 0, 0)
            .BackgroundColor(active ? EditorTheme.Selected : System.Drawing.Color.Transparent)
            .Hovered.BackgroundColor(active ? EditorTheme.Selected : EditorTheme.Hover).End()
            .OnClick(package.Name, (name, _) => _selectedName = name).Enter())
        {
            paper.Box($"pkg_row_i_{index}").Width(18).Height(34).Text(EditorIcons.Box, font)
                .TextColor(active ? EditorTheme.AccentText : sourceColor).FontSize(12f)
                .Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
            using (paper.Column($"pkg_row_c_{index}").Width(UnitValue.StretchOne).Height(34).Margin(8, 0, 0, 0).Enter())
            {
                paper.Box($"pkg_row_n_{index}").Height(18).Text(package.DisplayNameOrName(), font)
                    .TextColor(active ? EditorTheme.Ink500 : EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                paper.Box($"pkg_row_s_{index}").Height(14).Text(version, font)
                    .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 2)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            }
            if (manifest.Dependencies.ContainsKey(package.Name))
                paper.Box($"pkg_row_lock_{index}").Width(14).Height(34).Text(EditorIcons.Lock, font)
                    .TextColor(EditorTheme.InkDim).FontSize(10f).Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
        }
    }

    private void DrawDetails(Paper paper, Scribe.FontFile font, ResolvedPackage? package,
        ProjectPackageManifest manifest, float width, float height)
    {
        using (paper.Column("pkg_details").Width(width).Height(height).BackgroundColor(EditorTheme.Neutral200).Enter())
        {
            if (package == null)
            {
                EditorGUI.EmptyState(paper, "pkg_details_empty", "Select a package to inspect it.", font);
                return;
            }

            PackageJson? metadata = PackageJson.Read(Path.Combine(package.ResolvedRoot, "package.json"));
            string displayName = string.IsNullOrWhiteSpace(metadata?.DisplayName) ? package.Name : metadata!.DisplayName!;
            string sourceLabel = SourceLabel(package.Source);

            using (paper.Row("pkg_detail_header").Height(74).Padding(14, 14, 10, 4).Enter())
            {
                using (paper.Column("pkg_detail_title").Width(UnitValue.StretchOne).Height(60).Enter())
                {
                    paper.Box("pkg_detail_name").Height(27).Text(displayName, EditorTheme.FontSemiBold ?? font)
                        .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSizeLarge).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                    paper.Box("pkg_detail_version").Height(20).Text($"{package.Version}  •  {sourceLabel}", font)
                        .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                }
                Origami.Button(paper, "pkg_detail_action", manifest.Dependencies.ContainsKey(package.Name) ? "Remove" : "Add to project",
                    () => ToggleManifestPackage(manifest, package)).Width(94).Show();
            }

            using (paper.Row("pkg_detail_links").Height(30).Padding(14, 14, 0, 2).RowBetween(14).Enter())
            {
                DrawLink(paper, font, "Documentation", "pkg_link_doc");
                DrawLink(paper, font, "Changelog", "pkg_link_change");
                DrawLink(paper, font, "Licenses", "pkg_link_license");
                paper.Box("pkg_link_spacer").Width(UnitValue.StretchOne).Height(24).IsNotInteractable();
            }

            using (paper.Row("pkg_detail_tabs").Height(30).Padding(8, 8, 0, 0).RowBetween(2).Enter())
            {
                DrawDetailTab(paper, font, "Details");
                DrawDetailTab(paper, font, "Version History");
                DrawDetailTab(paper, font, "Dependencies");
                DrawDetailTab(paper, font, "Samples");
            }
            paper.Box("pkg_detail_tabline").Height(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

            float bodyHeight = height - 136f;
            Origami.ScrollView(paper, "pkg_detail_scroll", width, bodyHeight).Body(() =>
            {
                using (paper.Column("pkg_detail_body").Height(UnitValue.Auto).Padding(14, 14, 8, 12).ColBetween(6).Enter())
                {
                    switch (_detailTab)
                    {
                        case "Version History": DrawVersionHistory(paper, font, package); break;
                        case "Dependencies": DrawDependencies(paper, font, package); break;
                        case "Samples": DrawSamples(paper, font, package); break;
                        default: DrawPackageDetails(paper, font, package, metadata, manifest); break;
                    }
                }
            });
        }
    }

    private void DrawLink(Paper paper, Scribe.FontFile font, string label, string id)
        => paper.Box(id).Width(UnitValue.Auto).Height(24).Text(label, font).TextColor(EditorTheme.Blue400)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

    private void DrawDetailTab(Paper paper, Scribe.FontFile font, string tab)
    {
        bool active = _detailTab == tab;
        paper.Box("pkg_tab_" + tab.Replace(" ", "").ToLowerInvariant()).Width(UnitValue.Auto).Height(29)
            .Padding(8, 8, 0, 0).Text(tab, font).TextColor(active ? EditorTheme.Ink500 : EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter)
            .BackgroundColor(active ? EditorTheme.Selected : System.Drawing.Color.Transparent)
            .Hovered.BackgroundColor(active ? EditorTheme.Selected : EditorTheme.Hover).End()
            .OnClick(tab, (value, _) => _detailTab = value);
    }

    private void DrawPackageDetails(Paper paper, Scribe.FontFile font, ResolvedPackage package,
        PackageJson? metadata, ProjectPackageManifest manifest)
    {
        EditorGUI.Group(paper, "pkg_info_group", "Package information", () =>
        {
            InfoRow(paper, font, "Technical Name", package.Name);
            InfoRow(paper, font, "Version", package.Version);
            InfoRow(paper, font, "Source", SourceLabel(package.Source));
            InfoRow(paper, font, "Location", package.LogicalRoot);
            InfoRow(paper, font, "Minimum Editor", metadata?.MinimumEngineVersion ?? "Any supported version");
            if (!string.IsNullOrWhiteSpace(metadata?.License)) InfoRow(paper, font, "License", metadata!.License!);
        }, EditorIcons.CircleInfo);

        string description = string.IsNullOrWhiteSpace(metadata?.Description)
            ? "No description provided by this package."
            : metadata!.Description!;
        EditorGUI.Group(paper, "pkg_description_group", "Description", () =>
        {
            paper.Box("pkg_description").Height(UnitValue.Auto).Text(description, font)
                .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.Left)
                .Wrap(Scribe.TextWrapMode.Wrap);
        }, EditorIcons.FileLines);

        DrawProjectControls(paper, font, package, manifest);

        if (package.Warnings.Count > 0)
            DrawWarnings(paper, font, package);
    }

    private void DrawProjectControls(Paper paper, Scribe.FontFile font, ResolvedPackage package,
        ProjectPackageManifest manifest)
    {
        bool isDirectDependency = manifest.Dependencies.TryGetValue(package.Name, out string? specifier);
        bool isTestable = ContainsPackageName(manifest.Testables, package.Name);

        EditorGUI.Group(paper, "pkg_project_group", "Project settings", () =>
        {
            if (isDirectDependency)
            {
                if (!_editBuffer.ContainsKey(package.Name))
                    _editBuffer[package.Name] = specifier!;

                using (paper.Row("pkg_specifier_row").Height(28).RowBetween(6).Enter())
                {
                    paper.Box("pkg_specifier_label").Width(84).Height(28).Text("Specifier", font)
                        .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
                        .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                    Origami.TextField(paper, "pkg_specifier_value", _editBuffer[package.Name],
                        value => _editBuffer[package.Name] = value).Width(UnitValue.StretchOne).Show();
                    Origami.Button(paper, "pkg_specifier_apply", EditorIcons.Check, () =>
                    {
                        string value = _editBuffer[package.Name].Trim();
                        if (value.Length == 0) return;
                        manifest.Dependencies[package.Name] = value;
                        Save(manifest);
                    }).Width(28).Tooltip("Apply package specifier").Show();
                }
            }
            else
            {
                paper.Box("pkg_transitive_note").Height(22)
                    .Text("Resolved transitively or supplied by the Editor installation.", font)
                    .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            }

            Origami.Switch(paper, "pkg_testable", isTestable, enabled =>
            {
                manifest.Testables.RemoveAll(name =>
                    string.Equals(name, package.Name, StringComparison.OrdinalIgnoreCase));
                if (enabled) manifest.Testables.Add(package.Name);
                Save(manifest);
            }).LabelLeft("Include package tests").Stretch().Small().Show();
        }, EditorIcons.Gear);
    }

    private void DrawVersionHistory(Paper paper, Scribe.FontFile font, ResolvedPackage package)
    {
        EditorGUI.Group(paper, "pkg_history_group", "Version history", () =>
        {
            InfoRow(paper, font, "Installed", package.Version);
            InfoRow(paper, font, "Resolved from", package.SourceSpecifier ?? SourceLabel(package.Source));
            if (!string.IsNullOrWhiteSpace(package.GitCommit)) InfoRow(paper, font, "Git commit", package.GitCommit!);
            if (!string.IsNullOrWhiteSpace(package.ContentHash)) InfoRow(paper, font, "Content hash", package.ContentHash);
            paper.Box("pkg_history_note").Height(28).Text("The lock file records the exact resolved package for reproducible builds.", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1).Alignment(TextAlignment.Left)
                .Wrap(Scribe.TextWrapMode.Wrap);
        }, EditorIcons.Clock);
    }

    private void DrawDependencies(Paper paper, Scribe.FontFile font, ResolvedPackage package)
    {
        EditorGUI.Group(paper, "pkg_dependency_group", "Dependencies", () =>
        {
            if (package.DependencySpecifiers.Count == 0)
            {
                EditorGUI.EmptyState(paper, "pkg_no_dependencies", "This package has no dependencies.", font);
                return;
            }
            _sortedDependencies.Clear();
            foreach (KeyValuePair<string, string> dependency in package.DependencySpecifiers)
                _sortedDependencies.Add(dependency);
            _sortedDependencies.Sort(static (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Key, right.Key));
            for (int i = 0; i < _sortedDependencies.Count; i++)
            {
                KeyValuePair<string, string> dependency = _sortedDependencies[i];
                using (paper.Row("pkg_dependency_" + i).Height(24).RowBetween(4).Enter())
                {
                    paper.Box("pkg_dependency_name_" + i).Width(UnitValue.StretchOne).Height(24).Text(dependency.Key, font)
                        .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                    paper.Box("pkg_dependency_spec_" + i).Width(84).Height(24).Text(dependency.Value, font)
                        .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1).Alignment(TextAlignment.MiddleRight).IsNotInteractable();
                }
            }
        }, EditorIcons.Link);
    }

    private static void DrawSamples(Paper paper, Scribe.FontFile font, ResolvedPackage package)
    {
        string samplesRoot = Path.Combine(package.ResolvedRoot, "Samples~");
        string[] samples = Directory.Exists(samplesRoot) ? Directory.GetDirectories(samplesRoot) : Array.Empty<string>();
        EditorGUI.Group(paper, "pkg_samples_group", "Samples", () =>
        {
            if (samples.Length == 0)
            {
                EditorGUI.EmptyState(paper, "pkg_no_samples", "No samples are included with this package.", font);
                return;
            }
            for (int i = 0; i < samples.Length; i++)
                InfoRow(paper, font, Path.GetFileName(samples[i]), "Available sample");
        }, EditorIcons.FileLines);
    }

    private static void DrawWarnings(Paper paper, Scribe.FontFile font, ResolvedPackage package)
    {
        EditorGUI.Group(paper, "pkg_package_warning_group", "Warnings", () =>
        {
            for (int i = 0; i < package.Warnings.Count; i++)
                paper.Box("pkg_package_warning_" + i).Height(24).Text($"{EditorIcons.TriangleExclamation}  {package.Warnings[i]}", font)
                    .TextColor(EditorTheme.Amber400).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft)
                    .Wrap(Scribe.TextWrapMode.Wrap);
        }, EditorIcons.TriangleExclamation);
    }

    private void DrawResolutionProblems(Paper paper, Scribe.FontFile font,
        ProjectPackageManifest manifest, IReadOnlyList<ResolvedPackage> resolved)
    {
        _resolutionProblems.Clear();
        foreach (string name in manifest.Dependencies.Keys)
        {
            if (FindPackage(resolved, name) == null)
                _resolutionProblems.Add($"{name}: Could not be resolved. Check its source or version specifier.");
        }
        foreach (ResolvedPackage package in resolved)
        {
            foreach (string warning in package.Warnings)
                _resolutionProblems.Add($"{package.Name}: {warning}");
        }
        if (_resolutionProblems.Count == 0) return;

        EditorGUI.Group(paper, "pkg_resolution_problems", $"Problems ({_resolutionProblems.Count})", () =>
        {
            for (int i = 0; i < _resolutionProblems.Count; i++)
            {
                paper.Box("pkg_resolution_problem_" + i).Height(UnitValue.Auto).MinHeight(22)
                    .Text($"{EditorIcons.TriangleExclamation}  {_resolutionProblems[i]}", font)
                    .TextColor(EditorTheme.Amber400).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.Left).Wrap(Scribe.TextWrapMode.Wrap).IsNotInteractable();
            }
        }, EditorIcons.TriangleExclamation);
    }

    private void DrawAddPackage(Paper paper, Scribe.FontFile font, ProjectPackageManifest manifest)
    {
        EditorGUI.Group(paper, "pkg_add_group", "Add package", () =>
        {
            using (paper.Row("pkg_add_row").Height(28).RowBetween(6).Enter())
            {
                Origami.TextField(paper, "pkg_add_name", _newName, value => _newName = value)
                    .Width(UnitValue.StretchOne).Show();
                Origami.TextField(paper, "pkg_add_spec", _newSpecifier, value => _newSpecifier = value)
                    .Width(190).Show();
                Origami.Button(paper, "pkg_add_commit", $"{EditorIcons.Check} Add", () =>
                {
                    string name = (_newName ?? "").Trim();
                    string specifier = (_newSpecifier ?? "").Trim();
                    if (name.Length == 0 || specifier.Length == 0) return;
                    manifest.Dependencies[name] = specifier;
                    Save(manifest);
                    _selectedName = name;
                    _newName = "";
                    _newSpecifier = "";
                    _showAdd = false;
                }).Width(76).Show();
            }
            paper.Box("pkg_add_hint").Height(20).Text("Examples: 1.2.3  |  file:../package  |  git@https://...#v1  |  embed:name", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 2).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
        }, EditorIcons.Plus);
    }

    /// <summary>Scoped-registry editor for <c>manifest.json</c> plus the effective official npm
    /// registry (env-configured, read-only here). npm-protocol http(s) registries feed the Package
    /// Manager's remote catalog; file:// entries keep the P2 local-registry behavior.</summary>
    private void DrawRegistries(Paper paper, Scribe.FontFile font, ProjectPackageManifest manifest)
    {
        var options = XEngine.PackageRegistry.NpmRegistryOptions.Load(Project.Current?.RootPath);

        EditorGUI.Group(paper, "pkg_registries_group", "Package registries", () =>
        {
            using (paper.Row("pkg_official_row").Height(26).RowBetween(6).Enter())
            {
                paper.Box("pkg_official_label").Width(116).Height(26).Text("Official registry", font)
                    .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                paper.Box("pkg_official_value").Width(UnitValue.StretchOne).Height(26)
                    .Text(options.Disabled ? "Disabled (XENGINE_NPM_DISABLE)" : options.RegistryUrl, font)
                    .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall - 1)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            }

            if (manifest.ScopedRegistries != null)
            {
                int index = 0;
                foreach (KeyValuePair<string, ScopedRegistry> pair in manifest.ScopedRegistries)
                {
                    KeyValuePair<string, ScopedRegistry> captured = pair;
                    using (paper.Row("pkg_registry_" + index).Height(26).RowBetween(6).Enter())
                    {
                        paper.Box("pkg_registry_name_" + index).Width(116).Height(26).Text(pair.Key, font)
                            .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall - 1)
                            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                        paper.Box("pkg_registry_url_" + index).Width(UnitValue.StretchOne).Height(26)
                            .Text(pair.Value.Url + "   scopes: " + string.Join(", ", pair.Value.Scopes), font)
                            .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
                            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                        Origami.Button(paper, "pkg_registry_remove_" + index, EditorIcons.Trash, () =>
                        {
                            manifest.ScopedRegistries!.Remove(captured.Key);
                            Save(manifest);
                        }).Width(28).Tooltip("Remove scoped registry").Show();
                    }
                    index++;
                }
            }

            if (_showAddRegistry)
            {
                using (paper.Row("pkg_registry_add_row").Height(28).RowBetween(6).Enter())
                {
                    Origami.TextField(paper, "pkg_registry_add_name", _newRegistryName, value => _newRegistryName = value)
                        .Width(120).Show();
                    Origami.TextField(paper, "pkg_registry_add_url", _newRegistryUrl, value => _newRegistryUrl = value)
                        .Width(UnitValue.StretchOne).Show();
                    Origami.TextField(paper, "pkg_registry_add_scopes", _newRegistryScopes, value => _newRegistryScopes = value)
                        .Width(150).Show();
                    Origami.Button(paper, "pkg_registry_add_commit", $"{EditorIcons.Check} Add", () =>
                    {
                        string nickname = (_newRegistryName ?? "").Trim();
                        string url = (_newRegistryUrl ?? "").Trim();
                        if (nickname.Length == 0 || url.Length == 0) return;
                        manifest.ScopedRegistries ??= new Dictionary<string, ScopedRegistry>(StringComparer.OrdinalIgnoreCase);
                        var registry = new ScopedRegistry { Url = url };
                        foreach (string scope in (_newRegistryScopes ?? "").Split(',', ';'))
                        {
                            string trimmed = scope.Trim();
                            if (trimmed.Length > 0) registry.Scopes.Add(trimmed);
                        }
                        manifest.ScopedRegistries[nickname] = registry;
                        Save(manifest);
                        _showAddRegistry = false;
                        _newRegistryName = "";
                        _newRegistryUrl = "";
                        _newRegistryScopes = "";
                    }).Width(76).Show();
                }
                paper.Box("pkg_registry_add_hint").Height(18)
                    .Text("Fields: registry name | registry URL | scopes (comma-separated, e.g. com.myteam). npm-protocol http(s) registries feed the remote catalog; com.xengine.* also works via the official registry with no entry.", font)
                    .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 2)
                    .Alignment(TextAlignment.MiddleLeft).Wrap(Scribe.TextWrapMode.Wrap).IsNotInteractable();
            }
            else
            {
                Origami.Button(paper, "pkg_registry_add", $"{EditorIcons.Plus} Add scoped registry",
                    () => _showAddRegistry = true).Width(160).Show();
            }
        }, EditorIcons.CloudArrowDown);
    }

    private static void InfoRow(Paper paper, Scribe.FontFile font, string label, string value)
    {
        using (paper.Row("pkg_info_" + label.Replace(" ", "_").ToLowerInvariant()).Height(24).RowBetween(6).Enter())
        {
            paper.Box("_label").Width(116).Height(24).Text(label, font).TextColor(EditorTheme.InkDim)
                .FontSize(EditorTheme.FontSizeSmall - 1).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            paper.Box("_value").Width(UnitValue.StretchOne).Height(24).Text(value, font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
        }
    }

    private static void DrawFooter(Paper paper, Scribe.FontFile font, IReadOnlyList<ResolvedPackage> resolved)
    {
        using (paper.Row("pkg_footer").Height(24).Padding(8, 8, 4, 0).Enter())
            paper.Box("pkg_footer_text").Height(20).Text($"{resolved.Count} packages resolved  •  manifest.json is the project source of truth", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
    }

    private List<ResolvedPackage> FilterPackages(IReadOnlyList<ResolvedPackage> resolved, ProjectPackageManifest manifest)
    {
        _filteredPackages.Clear();
        if (_filteredPackages.Capacity < resolved.Count)
            _filteredPackages.Capacity = resolved.Count;

        string search = _search.Trim();
        for (int i = 0; i < resolved.Count; i++)
        {
            ResolvedPackage package = resolved[i];
            if (!MatchesSourceFilter(package, manifest))
                continue;
            if (search.Length > 0
                && !package.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !package.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            _filteredPackages.Add(package);
        }

        if (_sortAscending)
            _filteredPackages.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                left.DisplayNameOrName(), right.DisplayNameOrName()));
        else
            _filteredPackages.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                right.DisplayNameOrName(), left.DisplayNameOrName()));
        return _filteredPackages;
    }

    private bool MatchesSourceFilter(ResolvedPackage package, ProjectPackageManifest manifest)
    {
        return _sourceFilter switch
        {
            InProject => manifest.Dependencies.ContainsKey(package.Name),
            BuiltIn => package.Source == PackageSource.BuiltIn,
            Embedded => package.Source == PackageSource.Embed,
            Local => package.Source == PackageSource.Local,
            Git => package.Source == PackageSource.Git,
            Registry => package.Source == PackageSource.Registry,
            "updates" => false,
            _ => true,
        };
    }

    private static ResolvedPackage? FindPackage(IReadOnlyList<ResolvedPackage> packages, string name)
    {
        for (int i = 0; i < packages.Count; i++)
        {
            if (string.Equals(packages[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return packages[i];
        }

        return null;
    }

    private static bool ContainsPackageName(IReadOnlyList<string> packages, string name)
    {
        for (int i = 0; i < packages.Count; i++)
        {
            if (string.Equals(packages[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void ToggleManifestPackage(ProjectPackageManifest manifest, ResolvedPackage package)
    {
        if (manifest.Dependencies.ContainsKey(package.Name))
            manifest.Dependencies.Remove(package.Name);
        else
            manifest.Dependencies[package.Name] = package.SourceSpecifier ?? package.Version;
        Save(manifest);
    }

    private static string SourceLabel(PackageSource source) => source switch
    {
        PackageSource.BuiltIn => "Built-in",
        PackageSource.Embed => "Embedded",
        PackageSource.Local => "Local",
        PackageSource.Git => "Git",
        PackageSource.Registry => "XEngine Registry",
        _ => source.ToString(),
    };

    private static System.Drawing.Color SourceColor(PackageSource source) => source switch
    {
        PackageSource.BuiltIn => EditorTheme.Ink300,
        PackageSource.Embed => EditorTheme.Blue400,
        PackageSource.Local => EditorTheme.Green400,
        PackageSource.Git => EditorTheme.Purple400,
        PackageSource.Registry => EditorTheme.Amber400,
        _ => EditorTheme.Ink300,
    };
}

internal static class ResolvedPackageDisplayExtensions
{
    public static string DisplayNameOrName(this ResolvedPackage package)
        => string.IsNullOrWhiteSpace(package.DisplayName) ? package.Name : package.DisplayName;
}
