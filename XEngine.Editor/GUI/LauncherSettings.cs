// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Core;
using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Packages.EditorModules;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Rosetta;
using XEngine.Editor.Theming;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI;

/// <summary>
/// The launcher's Settings modal (gear button in the header): editor-level module management,
/// starting with the platform build modules. Installs land in the per-user editor module store
/// (<c>%LOCALAPPDATA%\XEngine\EditorModules\&lt;version&gt;\</c>) — they belong to this Editor
/// installation and never to any project's package manager. Works with no project open; that is
/// the whole point of resolving through <see cref="EditorModuleStore"/> instead of PackageManager.
/// SDK toolchains are installed through <see cref="PlatformModuleInstallSession"/> (module →
/// [Android license gate] → channel), with per-card status chips mirroring both layers.
/// </summary>
public static class LauncherSettings
{
    private static DialogModal? _modal;

    private sealed class CardState
    {
        public Task? Operation;
        public CancellationTokenSource? Cancellation;
        public EditorModuleProgress? Progress;
        public string? Error;
        public bool Running => Operation is { IsCompleted: false };
    }

    /// <summary>Adapter so worker-thread progress lands as one atomic reference swap the UI reads.</summary>
    private sealed class CardProgress : IProgress<EditorModuleProgress>
    {
        private readonly CardState _state;
        public CardProgress(CardState state) => _state = state;
        public void Report(EditorModuleProgress value) => _state.Progress = value;
    }

    private static readonly Dictionary<string, CardState> _cards = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PlatformModuleInstallSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _androidLicenseConsented = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<EditorModuleRecord> _installed = Array.Empty<EditorModuleRecord>();
    private static readonly Dictionary<string, string> _latest = new(StringComparer.OrdinalIgnoreCase);
    private static bool _updateCheckRunning;
    private static int _category;   // 0 = Platforms, 1 = General

    public static bool IsOpen => _modal != null;

    public static void Open()
    {
        if (_modal != null) return;
        RefreshInstalled();
        StartUpdateCheck();

        var modal = new DialogModal
        {
            Title = Loc.Get("launcher.settings"),
            Icon = new EditorGlyphIcon(EditorIcons.Gear, weight: GlyphWeight.Solid),
            Width = 700f,
            Height = 520f,
            CloseOnEscape = true,
        };
        modal.DrawContent = DrawContent;
        modal.OnDismissed = _ => Close();
        _modal = modal;
        Modal.Push(modal);
    }

    /// <summary>Deep-link variant (CLI <c>--launcher-settings-page general</c>): open straight
    /// onto the General page.</summary>
    public static void OpenGeneral()
    {
        _category = 1;
        Open();
    }

    private static void Close()
    {
        _modal = null;
        _updateCheckRunning = false;
        _category = 0;
        // Module installs are seconds-scale; a straggler keeps writing the store and the editor
        // module watcher picks the change up — nothing to tear down here.
        _cards.Clear();
    }

    private static void RefreshInstalled()
        => _installed = EditorModuleStore.ListInstalled();

    private static EditorModuleRecord? RecordFor(string name)
    {
        foreach (EditorModuleRecord record in _installed)
            if (string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                return record;
        return null;
    }

    private static CardState StateFor(string name)
    {
        if (_cards.TryGetValue(name, out CardState? state)) return state;
        state = new CardState();
        _cards[name] = state;
        return state;
    }

    private static PlatformModuleInstallSession SessionFor(string name)
    {
        if (_sessions.TryGetValue(name, out PlatformModuleInstallSession? session)) return session;
        session = new PlatformModuleInstallSession();
        _sessions[name] = session;
        return session;
    }

    private static bool TryMapPlatform(EditorModuleInfo module, out PlatformToolchainKind kind)
        => ToolchainManifest.TryMapPlatform(module.PlatformId ?? "", out kind);

    private static void StartUpdateCheck()
    {
        if (_updateCheckRunning) return;
        _updateCheckRunning = true;

        var names = new string[EditorModuleCatalog.KnownPlatformModules.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = EditorModuleCatalog.KnownPlatformModules[i].Name;

        _ = Task.Run(async () =>
        {
            try
            {
                IReadOnlyDictionary<string, string> latest =
                    await EditorModuleCatalog.QueryLatestVersionsAsync(names).ConfigureAwait(false);
                _latest.Clear();
                foreach (var pair in latest)
                    _latest[pair.Key] = pair.Value;
            }
            catch
            {
                // Offline / registry unreachable: the page simply shows installed versions.
            }
            finally
            {
                _updateCheckRunning = false;
            }
        });
    }

    // ------------------------------------------------------------------
    //  Install / uninstall operations
    // ------------------------------------------------------------------

    /// <summary>Deep-link entry (CLI <c>--install-module</c>): start installing
    /// <paramref name="moduleName"/> right away. Known platform modules run the FULL session
    /// (module + SDK toolchain); any other name installs straight into the store.</summary>
    public static void StartDeepLinkInstall(string moduleName)
    {
        EditorModuleInfo? known = EditorModuleCatalog.FindKnown(moduleName);
        if (known != null && TryMapPlatform(known, out PlatformToolchainKind kind))
        {
            SessionFor(known.Name).Start(new PlatformModuleInstallOptions
            {
                Platform = kind,
                Project = null,
                AcceptAndroidLicenses = _androidLicenseConsented.Contains(known.Name),
            });
            return;
        }
        StartInstall(known?.Name ?? moduleName, range: null);
    }

    private static void StartInstall(string moduleName, string? range)
    {
        CardState state = StateFor(moduleName);
        if (state.Running) return;
        state.Error = null;
        state.Progress = null;
        state.Cancellation = new CancellationTokenSource();
        CardProgress progress = new(state);
        CancellationToken token = state.Cancellation.Token;

        state.Operation = Task.Run(() => EditorModuleInstaller.InstallAsync(
            moduleName, range, progress: progress, cancellation: token));
        Observe(state);
    }

    private static void StartUninstall(string moduleName)
    {
        CardState state = StateFor(moduleName);
        if (state.Running) return;
        state.Error = null;
        state.Progress = null;
        state.Cancellation = new CancellationTokenSource();

        state.Operation = Task.Run(() => EditorModuleInstaller.UninstallAsync(moduleName));
        Observe(state);
    }

    private static void Observe(CardState state)
    {
        _ = state.Operation!.ContinueWith(t =>
        {
            RefreshInstalled();
            if (t.IsFaulted)
                state.Error = t.Exception?.GetBaseException().Message ?? "failed";
        }, TaskScheduler.Default);
    }

    private static void Cancel(CardState state)
        => state.Cancellation?.Cancel();

    // ------------------------------------------------------------------
    //  Drawing
    // ------------------------------------------------------------------

    private static void DrawContent(Paper paper)
    {
        string engineVersion = EditorModuleStore.EngineVersionFolderName();

        using (paper.Row("ls_body").Height(UnitValue.Stretch()).Enter())
        {
            DrawCategoryRail(paper);

            using (paper.Column("ls_pages").Width(UnitValue.Stretch()).Padding(4, 16, 4, 4).ColBetween(10).Enter())
            {
                if (_category == 1)
                {
                    DrawGeneralPage(paper);
                }
                else
                {
                    paper.Box("ls_hint").Height(UnitValue.Auto)
                        .Text(Loc.Get("launcher.settings.platforms_hint", new { version = engineVersion }),
                            EditorTheme.DefaultFont)
                        .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(TextAlignment.MiddleLeft).Wrap(Scribe.TextWrapMode.Wrap);

                    for (int i = 0; i < EditorModuleCatalog.KnownPlatformModules.Count; i++)
                        DrawModuleCard(paper, EditorModuleCatalog.KnownPlatformModules[i]);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    //  General page: engine-level editor preferences (download location today)
    // ------------------------------------------------------------------

    private static void DrawGeneralPage(Paper paper)
    {
        string configured = Theming.EditorSettings.Instance.PlatformSdkCacheRoot;
        bool usingDefault = string.IsNullOrWhiteSpace(configured);
        string display = usingDefault
            ? Loc.Get("launcher.settings.using_default") + "  " + PlatformToolchainService.BuiltinCacheRoot
            : configured;

        using (paper.Column("ls_general_card").Height(UnitValue.Auto)
            .Padding(12, 12, 12, 12).ColBetween(8)
            .BackgroundColor(EditorTheme.Field)
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Rounded(10)
            .Enter())
        {
            paper.Box("ls_dl_label").Height(20).IsNotInteractable()
                .Text(Loc.Get("launcher.settings.download_location"),
                    EditorTheme.FontSemiBold ?? EditorTheme.DefaultFont)
                .FontSize(13f).TextColor(EditorTheme.Ink500).Alignment(TextAlignment.MiddleLeft);

            paper.Box("ls_dl_path").Height(UnitValue.Auto).IsNotInteractable()
                .Text(display, EditorTheme.FontMono ?? EditorTheme.DefaultFont)
                .FontSize(EditorTheme.FontSizeSmall)
                .TextColor(usingDefault ? EditorTheme.Ink300 : EditorTheme.Ink400)
                .Alignment(TextAlignment.MiddleLeft).Wrap(Scribe.TextWrapMode.Wrap);

            using (paper.Row("ls_dl_actions").Height(28).RowBetween(8).Enter())
            {
                paper.Box("ls_dl_sp").Width(UnitValue.Stretch());
                Origami.Button(paper, "ls_dl_browse", Loc.Get("launcher.settings.browse"), () =>
                {
                    EditorApplication.OpenFileDialog(FileDialogMode.SelectFolder, path =>
                    {
                        if (string.IsNullOrWhiteSpace(path)) return;
                        Theming.EditorSettings.Instance.PlatformSdkCacheRoot = path;
                        Theming.EditorSettings.Instance.Save();
                    }, usingDefault ? PlatformToolchainService.BuiltinCacheRoot : configured);
                }).Small().Show();

                if (!usingDefault)
                {
                    Origami.Button(paper, "ls_dl_reset", Loc.Get("launcher.settings.reset_default"), () =>
                    {
                        Theming.EditorSettings.Instance.PlatformSdkCacheRoot = string.Empty;
                        Theming.EditorSettings.Instance.Save();
                    }).Small().Soft().Show();
                }
            }
        }

        paper.Box("ls_dl_hint").Height(UnitValue.Auto).IsNotInteractable()
            .Text(Loc.Get("launcher.settings.download_location_hint"), EditorTheme.DefaultFont)
            .FontSize(EditorTheme.FontSizeSmall).TextColor(EditorTheme.Ink300)
            .Alignment(TextAlignment.MiddleLeft).Wrap(Scribe.TextWrapMode.Wrap);
    }

    private static void DrawCategoryRail(Paper paper)
    {
        using (paper.Column("ls_rail").Width(148).Padding(0, 0, 14, 0).ColBetween(4).Enter())
        {
            DrawRailItem(paper, "ls_rail_platforms", Loc.Get("launcher.settings.platforms"),
                EditorIcons.MobileScreenButton, active: _category == 0, onClick: () => _category = 0);
            DrawRailItem(paper, "ls_rail_general", Loc.Get("launcher.settings.general"),
                EditorIcons.Sliders, active: _category == 1, onClick: () => _category = 1);
        }
    }

    private static void DrawRailItem(Paper paper, string id, string label, string glyph, bool active,
        Action onClick)
    {
        using (paper.Row(id).Height(30).RowBetween(8).Padding(8, 8, 0, 0)
            .Rounded(8)
            .BackgroundColor(active ? EditorTheme.Selected : Color.Transparent)
            .OnClick(_ => onClick())
            .Enter())
        {
            paper.Box(id + "_ic").Width(16).Height(30).IsNotInteractable()
                .Icon(paper, new EditorGlyphIcon(glyph, weight: GlyphWeight.Solid),
                    active ? EditorTheme.Ink600 : EditorTheme.Ink300, size: 12f);
            paper.Box(id + "_nm").Width(UnitValue.Auto).Height(30).IsNotInteractable()
                .Text(label, EditorTheme.FontMedium ?? EditorTheme.DefaultFont)
                .FontSize(12.5f).TextColor(active ? EditorTheme.Ink600 : EditorTheme.Ink300)
                .Alignment(TextAlignment.MiddleLeft);
            if (!active && id == "ls_rail_general")
                paper.Box(id + "_soon").Width(UnitValue.Stretch()).Height(30).IsNotInteractable()
                    .Text(Loc.Get("launcher.settings.general_soon"), EditorTheme.DefaultFont)
                    .FontSize(9.5f).TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleRight);
        }
    }

    private static void DrawModuleCard(Paper paper, EditorModuleInfo module)
    {
        EditorModuleRecord? record = RecordFor(module.Name);
        CardState state = StateFor(module.Name);
        PlatformModuleInstallSession session = SessionFor(module.Name);
        PlatformModuleInstallSnapshot sessionSnapshot = session.Snapshot;
        bool sessionActive = sessionSnapshot.State != PlatformModuleInstallState.Idle;
        bool updateAvailable = record != null
            && _latest.TryGetValue(module.Name, out string? latest)
            && !string.Equals(latest, record.Version, StringComparison.OrdinalIgnoreCase);

        using (paper.Column("ls_card_" + module.PlatformId).Height(UnitValue.Auto)
            .Padding(12, 12, 12, 12).ColBetween(7)
            .BackgroundColor(EditorTheme.Field)
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Rounded(10)
            .Enter())
        {
            // Title row: platform icon + display name + status chip on the right.
            using (paper.Row("ls_card_head").Height(22).RowBetween(9).Enter())
            {
                paper.Box("ls_card_ic").Width(18).Height(22).IsNotInteractable()
                    .Icon(paper, new EditorGlyphIcon(EditorIcons.MobileScreenButton, weight: GlyphWeight.Solid),
                        EditorTheme.Ink400, size: 13f);
                paper.Box("ls_card_nm").Width(UnitValue.Auto).Height(22).IsNotInteractable()
                    .Text(module.DisplayName, EditorTheme.FontSemiBold ?? EditorTheme.DefaultFont)
                    .FontSize(13.5f).TextColor(EditorTheme.Ink500).Alignment(TextAlignment.MiddleLeft);
                paper.Box("ls_card_sp").Width(UnitValue.Stretch());
                DrawModuleStatusChip(paper, module, record, updateAvailable, state, sessionSnapshot);
            }

            paper.Box("ls_card_desc").Height(UnitValue.Auto).IsNotInteractable()
                .Text(module.Description, EditorTheme.DefaultFont)
                .FontSize(EditorTheme.FontSizeSmall).TextColor(EditorTheme.Ink300)
                .Alignment(TextAlignment.MiddleLeft).Wrap(Scribe.TextWrapMode.Wrap);

            // SDK layer status (independent of the module): managed SDK root components.
            if (TryMapPlatform(module, out PlatformToolchainKind kind))
                DrawSdkStatusLine(paper, kind);

            if (sessionActive)
                DrawSessionState(paper, session, sessionSnapshot);
            else if (state.Running)
                DrawCardProgress(paper, state);
            else
                DrawCardActions(paper, module, record, updateAvailable);

            if (!state.Running && !sessionActive && state.Error != null)
                paper.Box("ls_card_err").Height(UnitValue.Auto).IsNotInteractable()
                    .Text(state.Error, EditorTheme.DefaultFont)
                    .FontSize(EditorTheme.FontSizeSmall).TextColor(EditorTheme.Red400)
                    .Alignment(TextAlignment.MiddleLeft).Wrap(Scribe.TextWrapMode.Wrap);
        }
    }

    private static void DrawModuleStatusChip(Paper paper, EditorModuleInfo module, EditorModuleRecord? record,
        bool updateAvailable, CardState state, PlatformModuleInstallSnapshot sessionSnapshot)
    {
        string text;
        Color color;
        if (sessionSnapshot.Running || state.Running)
        {
            text = Loc.Get("launcher.settings.installing");
            color = EditorTheme.Accent;
        }
        else if (sessionSnapshot.State == PlatformModuleInstallState.AwaitingConsent)
        {
            text = Loc.Get("launcher.settings.awaiting_consent");
            color = EditorTheme.Amber400;
        }
        else if (record == null)
        {
            text = Loc.Get("launcher.settings.not_installed");
            color = EditorTheme.Ink300;
        }
        else if (updateAvailable && _latest.TryGetValue(module.Name, out string? latest))
        {
            text = Loc.Get("launcher.settings.installed_v", new { version = record.Version })
                   + "  ·  " + Loc.Get("launcher.settings.update_v", new { version = latest });
            color = EditorTheme.Amber400;
        }
        else
        {
            text = Loc.Get("launcher.settings.installed_v", new { version = record.Version });
            color = EditorTheme.Green400;
        }

        paper.Box("ls_card_status").Width(UnitValue.Auto).Height(20).IsNotInteractable()
            .Padding(10, 10, 0, 0)
            .Text(text, EditorTheme.FontMedium ?? EditorTheme.DefaultFont)
            .FontSize(11f).TextColor(color).Alignment(TextAlignment.MiddleCenter);
    }

    /// <summary>Second status line: managed SDK components (the module is metadata; this is the toolchain).</summary>
    private static void DrawSdkStatusLine(Paper paper, PlatformToolchainKind kind)
    {
        PlatformModuleInstallSession.SdkStatus sdk = PlatformModuleInstallSession.GetSdkStatus(kind);
        string text;
        Color color;
        if (sdk.Ready)
        {
            text = Loc.Get("launcher.settings.sdk_ready") + "  ·  " + sdk.Root;
            color = EditorTheme.Green400;
        }
        else if (sdk.Partial)
        {
            text = Loc.Get("launcher.settings.sdk_partial",
                new { current = sdk.ComponentsInstalled, total = sdk.ComponentsTotal }) + "  ·  " + sdk.Root;
            color = EditorTheme.Amber400;
        }
        else
        {
            text = Loc.Get("launcher.settings.sdk_none");
            color = EditorTheme.Ink300;
        }

        paper.Box("ls_card_sdk").Height(15).IsNotInteractable()
            .Text(text, EditorTheme.DefaultFont)
            .FontSize(EditorTheme.FontSizeSmall).TextColor(color)
            .Alignment(TextAlignment.MiddleLeft).TextTruncate();
    }

    private static void DrawCardActions(Paper paper, EditorModuleInfo module, EditorModuleRecord? record,
        bool updateAvailable)
    {
        bool canRunSession = TryMapPlatform(module, out PlatformToolchainKind kind);
        using (paper.Row("ls_card_actions").Height(28).RowBetween(8).Enter())
        {
            // Android needs explicit license consent before sdkmanager runs; the checkbox gates the
            // SDK part of the install (the module alone stays allowed without it).
            if (module.PlatformId == "android")
            {
                Origami.Switch(paper, "ls_card_consent", _androidLicenseConsented.Contains(module.Name),
                    value =>
                    {
                        if (value) _androidLicenseConsented.Add(module.Name);
                        else _androidLicenseConsented.Remove(module.Name);
                    })
                    .LabelLeft(Loc.Get("launcher.settings.accept_android")).Stretch().Small().Show();
            }
            else
            {
                paper.Box("ls_card_act_sp").Width(UnitValue.Stretch());
            }

            if (record != null)
                Origami.Button(paper, "ls_card_uninstall", Loc.Get("launcher.settings.uninstall"),
                        () => StartUninstall(module.Name))
                    .Small().Soft().Danger().Show();

            if (updateAvailable && record != null)
                Origami.Button(paper, "ls_card_update", Loc.Get("launcher.settings.update"),
                        () => StartInstall(module.Name, ">" + record!.Version))
                    .Small().Primary().Show();
            else if (record == null && canRunSession)
                Origami.Button(paper, "ls_card_install", Loc.Get("launcher.settings.install_full"),
                        () => SessionFor(module.Name).Start(new PlatformModuleInstallOptions
                        {
                            Platform = kind,
                            Project = null,
                            AcceptAndroidLicenses = _androidLicenseConsented.Contains(module.Name),
                        }))
                    .Small().Primary().Show();
            else if (canRunSession)
                Origami.Button(paper, "ls_card_sdk", Loc.Get("launcher.settings.install_sdk"),
                        () => SessionFor(module.Name).Start(new PlatformModuleInstallOptions
                        {
                            Platform = kind,
                            Project = null,
                            AcceptAndroidLicenses = _androidLicenseConsented.Contains(module.Name),
                        }))
                    .Small().Show();
        }
    }

    private static void DrawCardProgress(Paper paper, CardState state)
    {
        string message = state.Progress?.Message ?? Loc.Get("launcher.settings.installing");

        paper.Box("ls_card_stage").Height(16).IsNotInteractable()
            .Text(message, EditorTheme.DefaultFont)
            .FontSize(EditorTheme.FontSizeSmall).TextColor(EditorTheme.Ink400)
            .Alignment(TextAlignment.MiddleLeft).TextTruncate();

        using (paper.Row("ls_card_prog").Height(22).RowBetween(8).Enter())
        {
            var bar = Origami.ProgressBar(paper, "ls_card_bar", 0f);
            EditorModuleProgress? progress = state.Progress;
            if (progress is { BytesTotal: > 0 })
                bar.Value((float)Math.Clamp((double)progress.BytesReceived / progress.BytesTotal, 0.0, 1.0));
            else
                bar.Indeterminate();
            bar.Thickness(5).Striped().Show();

            Origami.Button(paper, "ls_card_cancel", Loc.Get("launcher.settings.cancel"), () => Cancel(state))
                .Small().Soft().Show();
        }
    }

    /// <summary>Session (module + SDK) states: running progress, consent gate, partial/succeeded notes.</summary>
    private static void DrawSessionState(Paper paper, PlatformModuleInstallSession session,
        PlatformModuleInstallSnapshot snapshot)
    {
        if (snapshot.Running)
        {
            paper.Box("ls_card_stage").Height(16).IsNotInteractable()
                .Text(snapshot.Message ?? snapshot.State.ToString(), EditorTheme.DefaultFont)
                .FontSize(EditorTheme.FontSizeSmall).TextColor(EditorTheme.Ink400)
                .Alignment(TextAlignment.MiddleLeft).TextTruncate();

            using (paper.Row("ls_card_prog").Height(22).RowBetween(8).Enter())
            {
                var bar = Origami.ProgressBar(paper, "ls_card_bar", 0f);
                if (snapshot.Total > 0)
                    bar.Value((float)Math.Clamp((double)snapshot.Current / snapshot.Total, 0.0, 1.0));
                else
                    bar.Indeterminate();
                bar.Thickness(5).Striped().Show();

                Origami.Button(paper, "ls_card_cancel", Loc.Get("launcher.settings.cancel"), session.Cancel)
                    .Small().Soft().Show();
            }
            return;
        }

        Color color = snapshot.State switch
        {
            PlatformModuleInstallState.Succeeded => EditorTheme.Green400,
            PlatformModuleInstallState.Partial => EditorTheme.Amber400,
            PlatformModuleInstallState.Failed => EditorTheme.Red400,
            PlatformModuleInstallState.Canceled => EditorTheme.Ink300,
            _ => EditorTheme.Amber400,
        };
        string text = snapshot.State switch
        {
            PlatformModuleInstallState.Succeeded => snapshot.Message ?? "Ready.",
            PlatformModuleInstallState.AwaitingConsent => Loc.Get("launcher.settings.awaiting_consent"),
            PlatformModuleInstallState.Canceled => Loc.Get("launcher.settings.cancel") + ".",
            _ => snapshot.Error ?? snapshot.Message ?? snapshot.State.ToString(),
        };

        paper.Box("ls_card_sess").Height(UnitValue.Auto).IsNotInteractable()
            .Text(text, EditorTheme.DefaultFont)
            .FontSize(EditorTheme.FontSizeSmall).TextColor(color)
            .Alignment(TextAlignment.MiddleLeft).Wrap(Scribe.TextWrapMode.Wrap);
        if (!string.IsNullOrEmpty(snapshot.Guidance) && snapshot.State == PlatformModuleInstallState.Partial)
        {
            paper.Box("ls_card_sess_guide").Height(UnitValue.Auto).IsNotInteractable()
                .Text(snapshot.Guidance, EditorTheme.DefaultFont)
                .FontSize(EditorTheme.FontSizeSmall).TextColor(EditorTheme.Ink400)
                .Alignment(TextAlignment.MiddleLeft).Wrap(Scribe.TextWrapMode.Wrap);
        }
    }
}
