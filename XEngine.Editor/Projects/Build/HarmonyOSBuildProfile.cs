// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build;
using XEngine.Editor.Build.HarmonyOS;
using XEngine.Editor.GUI;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;

namespace XEngine.Editor.Projects.Build;

/// <summary>Output formats supported by the HarmonyOS player and provider packaging adapter.</summary>
public enum HarmonyOSOutputMode
{
    ExportProject,
    Hap,
    App,
}

public enum HarmonyOSBuildRunInstallMode
{
    InstallOrReplace,
    LaunchOnly,
}

/// <summary>HarmonyOS package and device-run settings.</summary>
public sealed class HarmonyOSBuildProfile : PlatformBuildProfile
{
    private CancellationTokenSource? _diagnosticCancellation;
    private Task? _diagnosticTask;
    private string _diagnosticStatus = string.Empty;
    private readonly HarmonyOSToolchainDownloadSession _toolchainDownloadSession = new();
    private string _localBundleArchive = string.Empty;

    public List<string> TargetAbis = ["arm64-v8a"];

    public int TargetApi = 13;

    public HarmonyOSOutputMode OutputMode = HarmonyOSOutputMode.ExportProject;

    public string ApplicationId = "com.xengine.game";

    public int VersionCode = 1;

    /// <summary>Non-secret signing reference, such as None or EnvironmentVariable:CI_OHOS_SIGNING.</summary>
    public string SigningReference = "None";

    /// <summary>Optional non-secret HDC target selector. Empty requires exactly one compatible target.</summary>
    public string RunDeviceId = string.Empty;

    public string AbilityName = "EntryAbility";

    public HarmonyOSBuildRunInstallMode InstallMode = HarmonyOSBuildRunInstallMode.InstallOrReplace;

    public bool CaptureRunScreenshot = true;

    public bool CleanupNewInstallationOnFailure;

    internal void SelectTargetAbi(string abi)
    {
        _ = HarmonyOSAbiMapper.ToRuntimeIdentifier(abi);
        TargetAbis = [abi];
    }

    public override Type GetPipelineType() => typeof(HarmonyOSBuildPipeline);

    public override void ModifyDefines(List<string> defines)
    {
        defines.Add("XENGINE_HARMONYOS");
        defines.Add("XENGINE_MOBILE");
    }

    public override void ToDefault()
    {
        TargetAbis = ["arm64-v8a"];
        TargetApi = 13;
        OutputMode = HarmonyOSOutputMode.ExportProject;
        ApplicationId = "com.xengine.game";
        VersionCode = 1;
        SigningReference = "None";
        RunDeviceId = string.Empty;
        AbilityName = "EntryAbility";
        InstallMode = HarmonyOSBuildRunInstallMode.InstallOrReplace;
        CaptureRunScreenshot = true;
        CleanupNewInstallationOnFailure = false;
    }

    internal void ValidateForBuild()
    {
        if (TargetApi < 13 || TargetApi == 25 || TargetApi > 26)
            throw new ArgumentOutOfRangeException(nameof(TargetApi), "HarmonyOS target API must be 13-24 or 26.");
        if (VersionCode < 1)
            throw new ArgumentOutOfRangeException(nameof(VersionCode), "HarmonyOS version code must be positive.");
        if (TargetAbis.Count == 0)
            throw new InvalidOperationException("HarmonyOS build profile must select at least one ABI.");
        for (int i = 0; i < TargetAbis.Count; i++)
            _ = HarmonyOSAbiMapper.ToRuntimeIdentifier(TargetAbis[i]);
        if (!IsApplicationId(ApplicationId))
            throw new ArgumentException("HarmonyOS application ID must be dot-separated lowercase identifiers.", nameof(ApplicationId));
        ValidateAbilityName(AbilityName);
        if (!Enum.IsDefined(InstallMode))
            throw new ArgumentOutOfRangeException(nameof(InstallMode), "HarmonyOS Build & Run install mode is invalid.");
        _ = HarmonyOSSigningReference.Validate(SigningReference);
    }

    public override void OnGUI(Paper paper)
    {
        EditorSettings settings = EditorSettings.Instance;
        EditorGUI.SectionHeader(paper, "ohos_sdk_h", "HarmonyOS SDK");
        DrawManagedToolchainDownload(paper);
        DrawSetting(paper, "ohos_bundle", "SDK Bundle", settings.HarmonyOSSdkBundleSourcePath, value => settings.HarmonyOSSdkBundleSourcePath = value);
        DrawSetting(paper, "ohos_sdk", "Official SDK", settings.HarmonyOSOfficialSdkRoot, value => settings.HarmonyOSOfficialSdkRoot = value);
        DrawSetting(paper, "ohos_lock", "Last Resolved Lock", settings.HarmonyOSLastResolvedLockPath, value => settings.HarmonyOSLastResolvedLockPath = value);
        DrawDiagnostics(paper, settings);

        EditorGUI.SectionHeader(paper, "ohos_output_h", "Application");
        EditorGUI.SettingsRow(paper, "ohos_output", "Output Mode", () =>
            Origami.EnumDropdown(paper, "ohos_output_v", OutputMode, value =>
            {
                OutputMode = value;
                EditorRegistries.SaveSettings();
            }).Show(), separator: false);
        EditorGUI.SettingsRow(paper, "ohos_api", "Target API", () =>
            Origami.NumericField<int>(paper, "ohos_api_v", TargetApi, value =>
            {
                TargetApi = Math.Max(13, value);
                EditorRegistries.SaveSettings();
            }).Min(13).Show(), separator: false);
        EditorGUI.SettingsRow(paper, "ohos_abi", "Target ABI", () =>
            Origami.EnumDropdown(paper, "ohos_abi_v", GetSelectedAbi(), value =>
            {
                SelectTargetAbi(value == HarmonyOSTargetAbi.X86_64 ? "x86_64" : "arm64-v8a");
                EditorRegistries.SaveSettings();
            }).Show(), separator: false);
        EditorGUI.SettingsRow(paper, "ohos_application", "Application ID", () =>
            Origami.TextField(paper, "ohos_application_v", ApplicationId, value =>
            {
                ApplicationId = value;
                EditorRegistries.SaveSettings();
            }).Show(), separator: false);
        EditorGUI.SettingsRow(paper, "ohos_version_code", "Version Code", () =>
            Origami.NumericField<int>(paper, "ohos_version_code_v", VersionCode, value =>
            {
                VersionCode = Math.Max(1, value);
                EditorRegistries.SaveSettings();
            }).Min(1).Show(), separator: false);
        EditorGUI.SettingsRow(paper, "ohos_signing", "Signing Reference", () =>
            Origami.TextField(paper, "ohos_signing_v", SigningReference, value =>
            {
                SigningReference = value;
                EditorRegistries.SaveSettings();
            }).Show(), separator: false);

        EditorGUI.SectionHeader(paper, "ohos_run_h", "Build & Run");
        EditorGUI.SettingsRow(paper, "ohos_run_device", "Device ID", () =>
            Origami.TextField(paper, "ohos_run_device_v", RunDeviceId, value =>
            {
                RunDeviceId = value;
                EditorRegistries.SaveSettings();
            }).Show(), separator: false);
        EditorGUI.SettingsRow(paper, "ohos_run_ability", "Ability", () =>
            Origami.TextField(paper, "ohos_run_ability_v", AbilityName, value =>
            {
                AbilityName = value;
                EditorRegistries.SaveSettings();
            }).Show(), separator: false);
        EditorGUI.SettingsRow(paper, "ohos_run_install", "Install Mode", () =>
            Origami.EnumDropdown(paper, "ohos_run_install_v", InstallMode, value =>
            {
                InstallMode = value;
                EditorRegistries.SaveSettings();
            }).Show(), separator: false);
        EditorGUI.SettingsToggle(paper, "ohos_run_screenshot", "Capture Screenshot", CaptureRunScreenshot,
            value => { CaptureRunScreenshot = value; EditorRegistries.SaveSettings(); }, separator: false);
        EditorGUI.SettingsToggle(paper, "ohos_run_cleanup", "Cleanup New Install on Failure", CleanupNewInstallationOnFailure,
            value => { CleanupNewInstallationOnFailure = value; EditorRegistries.SaveSettings(); }, separator: false);
    }

    private static void DrawSetting(Paper paper, string id, string label, string value, Action<string> assign)
    {
        EditorGUI.SettingsRow(paper, id, label, () =>
            Origami.TextField(paper, id + "_v", value, input =>
            {
                assign(input);
                EditorSettings.Instance.Save();
            }).Show(), separator: false);
    }

    /// <summary>Managed toolchain acquisition: the com.xengine.platform.harmonyos module's pinned
    /// ohos-sdk (Huawei Cloud open mirror) plus — when a local archive is configured — the SDK
    /// Bundle through the verified importer.</summary>
    private void DrawManagedToolchainDownload(Paper paper)
    {
        HarmonyOSToolchainDownloadSnapshot snapshot = _toolchainDownloadSession.Snapshot;
        // Editor-level module presence (per-user store or Editor bundle) — the primary install path
        // (launcher Settings > Platforms). A project-resolved copy still counts for legacy pins.
        bool moduleInstalled = ToolchainManifest.IsEditorModulePresent(PlatformToolchainKind.HarmonyOS)
            || (Project.Current != null
                && (PackageManager.PeekCached(Project.Current) ?? PackageManager.Resolve(Project.Current))
                    .Any(p => p.Name.Equals("com.xengine.platform.harmonyos", StringComparison.OrdinalIgnoreCase)));

        EditorGUI.SettingsRow(paper, "ohos_toolchain_download", "Managed Toolchain (module)", () =>
        {
            if (snapshot.State == HarmonyOSToolchainDownloadState.Running)
            {
                using (paper.Row("ohos_toolchain_running").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
                {
                    paper.Box("ohos_toolchain_stage").Width(UnitValue.StretchOne).Height(EditorTheme.RowHeight)
                        .IsNotInteractable().Text(snapshot.Message ?? "Installing…", EditorTheme.DefaultFont)
                        .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(TextAlignment.MiddleLeft).TextTruncate();
                    Origami.Button(paper, "ohos_toolchain_cancel", EditorIcons.Xmark,
                        _toolchainDownloadSession.Cancel).Width(38).Tooltip("Cancel install").Show();
                }
                return;
            }

            using (paper.Row("ohos_toolchain_row").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
            {
                paper.Box("ohos_toolchain_spacer").Width(UnitValue.StretchOne).Height(EditorTheme.RowHeight)
                    .IsNotInteractable();
                Origami.Button(paper, "ohos_toolchain_download", $"{EditorIcons.Download}  Download & Install SDK", () =>
                {
                    if (Project.Current == null) return;
                    _toolchainDownloadSession.Start(Project.Current, null,
                        string.IsNullOrWhiteSpace(_localBundleArchive) ? null : _localBundleArchive);
                })
                .Disabled(!moduleInstalled)
                .Tooltip(moduleInstalled
                    ? "Installs the pinned OpenHarmony SDK (native toolchain + hdc) into the managed SDK root"
                    : "Install the 'HarmonyOS Build Support' module first: launcher window → Settings (gear) → Platforms")
                .Show();
            }
        }, separator: false);

        if (snapshot.State is HarmonyOSToolchainDownloadState.Succeeded
            or HarmonyOSToolchainDownloadState.Partial or HarmonyOSToolchainDownloadState.Failed)
        {
            string status = snapshot.State switch
            {
                HarmonyOSToolchainDownloadState.Succeeded =>
                    $"Ready — {snapshot.Message} (SDK root: {snapshot.OfficialSdkRoot}; hdc: {snapshot.HdcPath})",
                HarmonyOSToolchainDownloadState.Partial =>
                    $"SDK ready at {snapshot.OfficialSdkRoot}; hdc at {snapshot.HdcPath}. Bundle: {snapshot.Error}",
                _ => snapshot.Error ?? "Failed.",
            };
            EditorGUI.SettingsRow(paper, "ohos_toolchain_status", "Install Status", () =>
                paper.Box("ohos_toolchain_status_v").Height(EditorTheme.RowHeight).IsNotInteractable()
                    .Text(status, EditorTheme.DefaultFont)
                    .TextColor(snapshot.State == HarmonyOSToolchainDownloadState.Failed
                        ? EditorTheme.Red400
                        : snapshot.State == HarmonyOSToolchainDownloadState.Partial
                            ? EditorTheme.Amber400
                            : EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft).TextTruncate(), separator: false);
        }
    }

    private void DrawDiagnostics(Paper paper, EditorSettings settings)
    {
        bool running = _diagnosticTask != null && !_diagnosticTask.IsCompleted;
        EditorGUI.SettingsRow(paper, "ohos_diagnostics", "SDK Diagnostics", () =>
        {
            using (paper.Row("ohos_diagnostics_row").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
            {
                if (running)
                {
                    Origami.Button(paper, "ohos_diagnostics_cancel", EditorIcons.Xmark, CancelDiagnostics)
                        .Width(38).Tooltip("Cancel HarmonyOS SDK operation").Show();
                }
                else
                {
                    Origami.Button(paper, "ohos_bundle_import", "Import Bundle", () => StartImport(settings)).Show();
                    Origami.Button(paper, "ohos_sdk_resolve", "Resolve SDK", () => StartResolve(settings)).Show();
                }
            }
        }, separator: false);

        if (!string.IsNullOrWhiteSpace(_diagnosticStatus))
        {
            EditorGUI.SettingsRow(paper, "ohos_diagnostics_status", "Diagnostics Status", () =>
                paper.Box("ohos_diagnostics_status_v").Height(EditorTheme.RowHeight).IsNotInteractable()
                    .Text(_diagnosticStatus, EditorTheme.FontMono ?? EditorTheme.DefaultFont)
                    .TextColor(EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft)
                    .TextTruncate(), separator: false);
        }
    }

    private void StartImport(EditorSettings settings)
    {
        Project? project = Project.Current;
        if (project == null)
        {
            _diagnosticStatus = "No project is open.";
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.HarmonyOSSdkBundleSourcePath))
        {
            _diagnosticStatus = "Select an SDK Bundle archive first.";
            return;
        }

        StartDiagnostic(async cancellationToken =>
        {
            HarmonyOSSdkBundleImportResult result = await new HarmonyOSSdkBundleImporter()
                .ImportAsync(new HarmonyOSSdkBundleImportRequest(project.RootPath, settings.HarmonyOSSdkBundleSourcePath), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            settings.HarmonyOSLastResolvedLockPath = result.LockPath;
            settings.Save();
            return result.Reused ? "Verified existing SDK Bundle: " + result.Lock.Fingerprint : "Imported SDK Bundle: " + result.Lock.Fingerprint;
        });
    }

    private void StartResolve(EditorSettings settings)
    {
        Project? project = Project.Current;
        if (project == null)
        {
            _diagnosticStatus = "No project is open.";
            return;
        }

        string lockPath = string.IsNullOrWhiteSpace(settings.HarmonyOSLastResolvedLockPath)
            ? Path.Combine(project.RootPath, "ThirdParty", "OpenHarmonySdk", HarmonyOSSdkBundleLockFile.DefaultFileName)
            : settings.HarmonyOSLastResolvedLockPath;
        StartDiagnostic(async cancellationToken =>
        {
            HarmonyOSToolchainSnapshot snapshot = await new HarmonyOSToolchainResolver()
                .ResolveAsync(new HarmonyOSToolchainResolveOptions
                {
                    ProjectRoot = project.RootPath,
                    LockFilePath = lockPath,
                    UserPreferenceSdkRoot = settings.HarmonyOSOfficialSdkRoot,
                    EnvironmentSdkRoot = Environment.GetEnvironmentVariable("OHOS_SDK_HOME"),
                    TargetApi = TargetApi,
                    TargetAbi = TargetAbis.Count == 0 ? "arm64-v8a" : TargetAbis[0],
                }, cancellationToken)
                .ConfigureAwait(false);
            settings.HarmonyOSLastResolvedLockPath = lockPath;
            settings.Save();
            return "Resolved API " + snapshot.TargetApi + " " + snapshot.TargetAbi + ": " + snapshot.BundleFingerprint;
        });
    }

    private void StartDiagnostic(Func<CancellationToken, Task<string>> action)
    {
        if (_diagnosticTask != null && !_diagnosticTask.IsCompleted)
            return;
        _diagnosticCancellation?.Dispose();
        _diagnosticCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _diagnosticCancellation.Token;
        _diagnosticStatus = "Running...";
        _diagnosticTask = Task.Run(async () =>
        {
            try
            {
                _diagnosticStatus = await action(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _diagnosticStatus = "Cancelled.";
            }
            catch (HarmonyOSBundleException exception)
            {
                _diagnosticStatus = exception.Code + ": " + exception.Message;
            }
            catch (Exception exception)
            {
                _diagnosticStatus = exception.Message;
            }
        }, cancellationToken);
    }

    private void CancelDiagnostics() => _diagnosticCancellation?.Cancel();

    private HarmonyOSTargetAbi GetSelectedAbi() =>
        TargetAbis.Count == 1 && string.Equals(TargetAbis[0], "x86_64", StringComparison.Ordinal)
            ? HarmonyOSTargetAbi.X86_64
            : HarmonyOSTargetAbi.Arm64V8a;

    private static bool IsApplicationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255)
            return false;
        string[] segments = value.Split('.', StringSplitOptions.None);
        if (segments.Length < 2)
            return false;
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0 || segment[0] < 'a' || segment[0] > 'z')
                return false;
            for (int j = 1; j < segment.Length; j++)
            {
                char c = segment[j];
                if ((c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '_')
                    return false;
            }
        }
        return true;
    }

    private static void ValidateAbilityName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255)
            throw new ArgumentException("HarmonyOS ability name must be non-empty and bounded.", nameof(AbilityName));
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                throw new ArgumentException("HarmonyOS ability name contains unsupported characters.", nameof(AbilityName));
        }
    }

    private enum HarmonyOSTargetAbi
    {
        Arm64V8a,
        X86_64,
    }
}
