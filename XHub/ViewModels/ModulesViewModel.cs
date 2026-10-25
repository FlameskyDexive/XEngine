using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Resources;
using XHub.Services;

namespace XHub.ViewModels;

/// <summary>Per-platform row inside the manage-modules dialog (§3.4).</summary>
public partial class PlatformModuleViewModel : ViewModelBase
{
    public PlatformModuleViewModel(PlatformToolchainTarget target, InstallationRecord install)
    {
        Target = target;
        _install = install;
        NameKey = target switch
        {
            PlatformToolchainTarget.Android => "modules.platform.android",
            PlatformToolchainTarget.HarmonyOS => "modules.platform.harmonyos",
            _ => "modules.platform.ios",
        };
        UpdateLocalizedText();
    }

    public PlatformToolchainTarget Target { get; }
    private readonly InstallationRecord _install;

    public string NameKey { get; }

    /// <summary>Localized platform display name.</summary>
    public string Name => Loc.Instance[NameKey];

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private bool _needsLicenseConsent;

    [ObservableProperty]
    private bool _licenseAccepted;

    /// <summary>HarmonyOS local bundle archive the user imported (§3.4 本地 Bundle 导入).</summary>
    [ObservableProperty]
    private string _harmonyBundlePath = "";

    public bool IsAndroid => Target == PlatformToolchainTarget.Android;
    public bool IsHarmonyOS => Target == PlatformToolchainTarget.HarmonyOS;
    public bool IsIos => Target == PlatformToolchainTarget.Ios;

    /// <summary>iOS toolchain detection only exists on macOS hosts (§3.4 主机限制).</summary>
    public bool IsIosBlocked => Target == PlatformToolchainTarget.Ios && !PlatformToolchainRunner.IosSupportedOnHost;

    public string IosBlockHint => IsIosBlocked ? Loc.Instance["modules.ios.hostLimit"] : "";

    public InstallationRecord Install => _install;

    public void OnLanguageChanged()
    {
        UpdateLocalizedText();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IosBlockHint));
    }

    public void SetStatus(string key, bool failed = false, bool busy = false)
    {
        StatusText = Loc.Instance[key];
        IsFailed = failed;
        IsBusy = busy;
        OnPropertyChanged(nameof(StatusText));
    }

    private void UpdateLocalizedText()
    {
        StatusText = StatusText.Length == 0 ? Loc.Instance["modules.status.notDetected"] : StatusText;
    }
}

/// <summary>
/// Manage-modules dialog state (§3.4): per-platform toolchain detection, install with explicit
/// Android SDK license consent, HarmonyOS local bundle import, cancellation and diagnostics —
/// all executed through the editor installation's own CLI.
/// </summary>
public partial class ModulesViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly InstallationRecord _installation;
    private readonly List<PlatformModuleViewModel> _platforms = [];
    private CancellationTokenSource? _runCts;

    public ModulesViewModel(InstallationRecord installation, IDialogService dialogs)
    {
        _installation = installation;
        _dialogs = dialogs;
        _platforms =
        [
            new PlatformModuleViewModel(PlatformToolchainTarget.Android, installation),
            new PlatformModuleViewModel(PlatformToolchainTarget.HarmonyOS, installation),
            new PlatformModuleViewModel(PlatformToolchainTarget.Ios, installation),
        ];

        Loc.Instance.PropertyChanged += (_, _) =>
        {
            foreach (PlatformModuleViewModel platform in _platforms)
                platform.OnLanguageChanged();
        };

        RefreshDiagnostics(); // seed the diagnostics pane with host/install facts
    }

    public InstallationRecord Installation => _installation;

    public string Title => $"{Loc.Instance["modules.title"]} — {_installation.VersionDisplay}";

    public IReadOnlyList<PlatformModuleViewModel> Platforms => _platforms;

    [ObservableProperty]
    private string _diagnosticsText = "";

    private void AppendDiagnostic(string line)
    {
        void Run() => DiagnosticsText = $"{DiagnosticsText}{(DiagnosticsText.Length > 0 ? "\n" : "")}{line}".TrimEnd();
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Run();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Run);
    }

    [RelayCommand]
    public async Task InstallAsync(PlatformModuleViewModel? platform)
    {
        if (platform == null || platform.IsBusy || platform.IsIosBlocked) return;

        if (platform.Target == PlatformToolchainTarget.Android && !platform.LicenseAccepted)
        {
            // §3.4: explicit license consent gate — no install without the user's acceptance.
            platform.SetStatus("modules.status.licenseRequired", failed: true);
            AppendDiagnostic(Loc.Instance["modules.status.licenseRequired"]);
            return;
        }

        platform.IsBusy = true;
        platform.SetStatus("modules.status.installing", busy: true);
        _runCts = new CancellationTokenSource();
        ToolchainRunResult result = await PlatformToolchainRunner.RunAsync(
            _installation, platform.Target,
            acceptLicenses: platform.LicenseAccepted,
            harmonyBundlePath: platform.HarmonyBundlePath,
            onLine: line => AppendDiagnostic($"{DateTime.Now:HH:mm:ss} [{(line.IsError ? "E" : "I")}] {line.Text}"),
            ct: _runCts.Token);

        if (result.LicenseConsentRequired)
            platform.SetStatus("modules.status.licenseRequired", failed: true);
        else if (result.Ok)
            platform.SetStatus("modules.status.installed");
        else
            platform.SetStatus("modules.status.failed", failed: true);
        platform.IsBusy = false;
    }

    [RelayCommand]
    public void Cancel(PlatformModuleViewModel? platform)
    {
        if (platform == null || !platform.IsBusy) return;
        _runCts?.Cancel();
        AppendDiagnostic(Loc.Instance["modules.canceled"]);
    }

    [RelayCommand]
    public async Task ImportBundleAsync(PlatformModuleViewModel? platform)
    {
        if (platform == null || !platform.IsHarmonyOS) return;
        string? archive = await _dialogs.PickAnyFolderAsync();
        if (archive != null)
        {
            platform.HarmonyBundlePath = archive;
            OnPropertyChanged(nameof(platform.HarmonyBundlePath));
            AppendDiagnostic($"bundle: {archive}");
        }
    }

    /// <summary>Runs the wizard's diagnostic dump into the log area (按钮: 诊断信息).</summary>
    [RelayCommand]
    public void RefreshDiagnostics()
    {
        AppendDiagnostic($"editor: {_installation.EditorExePath}");
        AppendDiagnostic($"host: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }
}
