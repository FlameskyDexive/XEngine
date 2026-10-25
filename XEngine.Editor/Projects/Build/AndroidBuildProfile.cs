// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Android;
using XEngine.Editor.AssetBundles.PlatformVariants;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;

namespace XEngine.Editor.Projects.Build;

/// <summary>
/// Android-specific build profile. Knobs here drive the generated <c>net10.0-android</c> player
/// project and the Gradle/APK packaging step. See
/// <c>docs/superpowers/specs/2026-07-28-android-platform-support-plan.md</c> for the full design.
/// </summary>
/// <remarks>
/// G0 skeleton: only the fields needed for platform registration and define injection are live.
/// Identification / Rendering / Signing / ABI knobs land in M1-M4 alongside their consumers.
/// </remarks>
public class AndroidBuildProfile : PlatformBuildProfile
{
    private readonly AndroidToolchainImportSession _toolchainImportSession = new();
    private readonly AndroidToolchainResolveSession _toolchainResolveSession = new();
    private readonly AndroidToolchainDownloadSession _toolchainDownloadSession = new();
    private string _permissionToAdd = string.Empty;
    private string? _persistedImportFingerprint;
    private bool _acceptAndroidSdkLicenses;

    public Type PipelineType => typeof(AndroidBuildPipeline);

    public override Type GetPipelineType() => PipelineType;

    /// <summary>Target ABIs. <c>arm64-v8a</c> is the primary; <c>armeabi-v7a</c> best-effort.</summary>
    public List<string> TargetAbis = ["arm64-v8a"];

    public AndroidOutputMode OutputMode = AndroidOutputMode.ExportProject;

    /// <summary>
    /// Managed trimming policy. Safe enables conservative ILLink roots for generated Android
    /// projects; Full remains blocked until the complete reflection/device evidence gate passes.
    /// </summary>
    public AndroidManagedStrippingMode ManagedStripping = AndroidManagedStrippingMode.Disabled;

    public string ApplicationId = "com.xengine.game";

    public int VersionCode = 1;

    /// <summary>Minimum Android API level (Vulkan 1.1 requires API 24+).</summary>
    public int MinSdkVersion = 24;

    /// <summary>Compile/target Android API level.</summary>
    public int TargetSdkVersion = 36;

    /// <summary>Requested screen orientation for the generated Android Activity.</summary>
    public AndroidScreenOrientation ScreenOrientation = AndroidScreenOrientation.Unspecified;

    /// <summary>Whether Android may resize this Activity for multi-window and larger displays.</summary>
    public bool ResizableActivity = true;

    /// <summary>Additional package-style permissions emitted into the generated manifest.</summary>
    public List<string> AdditionalPermissions = [];

    public int MaxPackageSizeMiB;
    public int MaxManagedSizeMiB;
    public int MaxNativeSizeMiB;
    public int MaxAssetSizeMiB;
    public int MaxShaderSizeMiB;

    /// <summary>
    /// Android texture variant request. Automatic selects the ETC2 baseline, but the build falls
    /// back to RGBA32 until a deterministic ETC2/ASTC encoder is configured.
    /// </summary>
    public TextureVariantFormat TextureFormat = TextureVariantFormat.Automatic;

    /// <summary>Maximum Android texture dimension; zero means no resize limit.</summary>
    public int TextureMaxSize = 2048;

    /// <summary>Offline texture encoder quality in the inclusive range [0, 100].</summary>
    public int TextureCompressionQuality = 50;

    /// <summary>
    /// Optional adb serial used by Build &amp; Run. Empty is valid only when exactly one online
    /// Android device is connected, preventing an accidental install on the wrong device.
    /// </summary>
    public string DeploymentDeviceSerial = string.Empty;

    /// <summary>
    /// Project-relative JSONL produced by a Development Player and explicitly reviewed for Release
    /// collection. Empty keeps runtime traces out of the build.
    /// </summary>
    public string ApprovedShaderVariantTracePath = string.Empty;

    public override void OnGUI(Paper paper)
    {
        DrawToolchainSettings(paper);

        EditorGUI.SectionHeader(paper, "and_output_h", "Application");

        EditorGUI.SettingsRow(paper, "and_output_mode", "Output Mode", () =>
            Origami.EnumDropdown(paper, "and_output_mode_v", OutputMode,
                value => { OutputMode = value; EditorRegistries.SaveSettings(); }).Show(), separator: false);

        EditorGUI.SettingsRow(paper, "and_managed_stripping", "Managed Stripping", () =>
            Origami.EnumDropdown(paper, "and_managed_stripping_v", ManagedStripping,
                value => { ManagedStripping = value; EditorRegistries.SaveSettings(); }).Show(), separator: false);

        EditorGUI.SettingsRow(paper, "and_application_id", "Application ID", () =>
            Origami.TextField(paper, "and_application_id_v", ApplicationId,
                value => { ApplicationId = value; EditorRegistries.SaveSettings(); }).Show(), separator: false);

        EditorGUI.SettingsRow(paper, "and_version_code", "Version Code", () =>
            Origami.NumericField<int>(paper, "and_version_code_v", VersionCode,
                value => { VersionCode = Math.Max(1, value); EditorRegistries.SaveSettings(); })
                .Min(1).Show(), separator: false);

        EditorGUI.SettingsRow(paper, "and_abis", "Target ABIs", () =>
        {
            for (int i = 0; i < TargetAbis.Count; i++)
            {
                int idx = i;
                using (paper.Row($"and_abi_{i}").Height(EditorTheme.RowHeight).Enter())
                {
                    paper.Box($"and_abi_{i}_l").Height(EditorTheme.RowHeight)
                        .Text(TargetAbis[idx], EditorTheme.DefaultFont)
                        .TextColor(EditorTheme.Ink500)
                        .FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(TextAlignment.MiddleLeft);
                }
            }
        }, separator: false);

        EditorGUI.SettingsRow(paper, "and_minsdk", "Min SDK Version", () =>
            Origami.NumericField<int>(paper, "and_minsdk_v", MinSdkVersion,
                v => { MinSdkVersion = Math.Max(24, v); EditorRegistries.SaveSettings(); })
                .Min(24).Show(), separator: false);

        EditorGUI.SettingsRow(paper, "and_tgtsdk", "Target SDK Version", () =>
            Origami.NumericField<int>(paper, "and_tgtsdk_v", TargetSdkVersion,
                v => { TargetSdkVersion = Math.Max(MinSdkVersion, v); EditorRegistries.SaveSettings(); })
                .Min(MinSdkVersion).Show(), separator: false);

        EditorGUI.SectionHeader(paper, "and_display_h", "Display");

        EditorGUI.SettingsRow(paper, "and_orientation", "Screen Orientation", () =>
            Origami.EnumDropdown(paper, "and_orientation_v", ScreenOrientation,
                value => { ScreenOrientation = value; EditorRegistries.SaveSettings(); }).Show(), separator: false);

        EditorGUI.SettingsRow(paper, "and_resizable", "Resizable Activity", () =>
            Origami.Checkbox(paper, "and_resizable_v", ResizableActivity,
                value => { ResizableActivity = value; EditorRegistries.SaveSettings(); }).NoLabel().Show(), separator: false);

        EditorGUI.SectionHeader(paper, "and_permissions_h", "Permissions");

        EditorGUI.SettingsRow(paper, "and_permissions", "Additional Permissions", () =>
        {
            for (int i = 0; i < AdditionalPermissions.Count; i++)
            {
                int index = i;
                using (paper.Row($"and_permission_{i}").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
                {
                    paper.Box($"and_permission_{i}_name").Height(EditorTheme.RowHeight)
                        .Text(AdditionalPermissions[i], EditorTheme.DefaultFont)
                        .TextColor(EditorTheme.Ink500)
                        .FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(TextAlignment.MiddleLeft);
                    Origami.Button(paper, $"and_permission_{i}_remove", EditorIcons.Xmark, () =>
                    {
                        AdditionalPermissions.RemoveAt(index);
                        EditorRegistries.SaveSettings();
                    }).Width(32).Tooltip("Remove permission").Show();
                }
            }

            using (paper.Row("and_permission_add_row").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
            {
                string pendingPermission = _permissionToAdd.Trim();
                bool invalidPermission = pendingPermission.Length > 0 &&
                    !AndroidPlayerProjectGenerator.IsValidPermissionName(pendingPermission);
                var permissionField = Origami.TextField(
                    paper,
                    "and_permission_add",
                    _permissionToAdd,
                    value => _permissionToAdd = value);
                if (invalidPermission)
                    permissionField.Danger();
                permissionField.Placeholder("android.permission.CAMERA").Show();
                Origami.Button(paper, "and_permission_add_button", EditorIcons.Plus, AddPermission)
                    .Width(32)
                    .Tooltip(invalidPermission ? "Use a package-style permission name" : "Add permission")
                    .Show();
            }
        }, separator: false);

        EditorGUI.SectionHeader(paper, "and_size_budget_h", "Size Budgets (MiB)");
        DrawSizeBudget(paper, "and_size_package", "Package", MaxPackageSizeMiB, value => MaxPackageSizeMiB = value);
        DrawSizeBudget(paper, "and_size_managed", "Managed", MaxManagedSizeMiB, value => MaxManagedSizeMiB = value);
        DrawSizeBudget(paper, "and_size_native", "Native", MaxNativeSizeMiB, value => MaxNativeSizeMiB = value);
        DrawSizeBudget(paper, "and_size_assets", "Assets", MaxAssetSizeMiB, value => MaxAssetSizeMiB = value);
        DrawSizeBudget(paper, "and_size_shader", "Shader", MaxShaderSizeMiB, value => MaxShaderSizeMiB = value);

        EditorGUI.SectionHeader(paper, "and_texture_variants_h", "Texture Variants");
        EditorGUI.SettingsRow(paper, "and_texture_format", "Texture Format", () =>
            Origami.EnumDropdown(paper, "and_texture_format_v", TextureFormat,
                value => { TextureFormat = value; EditorRegistries.SaveSettings(); }).Show(), separator: false);
        EditorGUI.SettingsRow(paper, "and_texture_max_size", "Texture Max Size", () =>
            Origami.NumericField<int>(paper, "and_texture_max_size_v", TextureMaxSize,
                value => { TextureMaxSize = Math.Max(0, value); EditorRegistries.SaveSettings(); })
                .Min(0).Show(), separator: false);
        EditorGUI.SettingsRow(paper, "and_texture_quality", "Texture Quality", () =>
            Origami.NumericField<int>(paper, "and_texture_quality_v", TextureCompressionQuality,
                value => { TextureCompressionQuality = Math.Clamp(value, 0, 100); EditorRegistries.SaveSettings(); })
                .Min(0).Max(100).Show(), separator: false);

        EditorGUI.SettingsRow(paper, "and_shader_trace", "Approved Shader Trace", () =>
            Origami.TextField(paper, "and_shader_trace_v", ApprovedShaderVariantTracePath,
                value => { ApprovedShaderVariantTracePath = value; EditorRegistries.SaveSettings(); }).Show(),
            separator: false);

        EditorGUI.SettingsRow(paper, "and_deploy_serial", "Build & Run Device", () =>
            Origami.TextField(paper, "and_deploy_serial_v", DeploymentDeviceSerial,
                value => { DeploymentDeviceSerial = value; EditorRegistries.SaveSettings(); }).Show(),
            separator: false);
    }

    private static void DrawSizeBudget(Paper paper, string id, string label, int current, Action<int> assign)
    {
        EditorGUI.SettingsRow(paper, id, label, () =>
            Origami.NumericField<int>(paper, id + "_v", current, value =>
            {
                assign(Math.Max(0, value));
                EditorRegistries.SaveSettings();
            }).Min(0).Show(), separator: false);
    }

    private void AddPermission()
    {
        string permission = _permissionToAdd.Trim();
        if (!AndroidPlayerProjectGenerator.IsValidPermissionName(permission) ||
            AdditionalPermissions.Contains(permission))
        {
            return;
        }

        AdditionalPermissions.Add(permission);
        _permissionToAdd = string.Empty;
        EditorRegistries.SaveSettings();
    }

    private void DrawToolchainSettings(Paper paper)
    {
        EditorSettings settings = EditorSettings.Instance;
        AndroidToolchainImportSessionSnapshot snapshot = _toolchainImportSession.Snapshot;
        if (snapshot.State == AndroidToolchainImportSessionState.Succeeded &&
            snapshot.Result != null &&
            !string.Equals(_persistedImportFingerprint, snapshot.Result.Manifest.Fingerprint, StringComparison.Ordinal))
        {
            settings.AndroidToolchainResolvedPath = snapshot.Result.CachePath;
            settings.Save();
            _persistedImportFingerprint = snapshot.Result.Manifest.Fingerprint;
        }

        EditorGUI.SettingsRow(paper, "and_toolchain_source", "Unity AndroidPlayer", () =>
        {
            using (paper.Row("and_toolchain_source_row").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
            {
                Origami.TextField(paper, "and_toolchain_source_v", settings.AndroidToolchainUnitySourceRoot,
                    value => { settings.AndroidToolchainUnitySourceRoot = value; settings.Save(); }).Show();
                Origami.Button(paper, "and_toolchain_source_browse", EditorIcons.FolderOpen, () =>
                    EditorApplication.OpenFileDialog(FileDialogMode.SelectFolder, path =>
                    {
                        if (string.IsNullOrWhiteSpace(path))
                            return;
                        settings.AndroidToolchainUnitySourceRoot = path;
                        settings.Save();
                    }, settings.AndroidToolchainUnitySourceRoot)).Width(38).Tooltip("Select Unity AndroidPlayer folder").Show();
            }
        }, separator: false);

        EditorGUI.SettingsRow(paper, "and_toolchain_cache", "Toolchain Cache", () =>
        {
            using (paper.Row("and_toolchain_cache_row").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
            {
                Origami.TextField(paper, "and_toolchain_cache_v", settings.AndroidToolchainCacheRoot,
                    value => { settings.AndroidToolchainCacheRoot = value; settings.Save(); }).Show();
                Origami.Button(paper, "and_toolchain_cache_browse", EditorIcons.FolderOpen, () =>
                    EditorApplication.OpenFileDialog(FileDialogMode.SelectFolder, path =>
                    {
                        if (string.IsNullOrWhiteSpace(path))
                            return;
                        settings.AndroidToolchainCacheRoot = path;
                        settings.Save();
                    }, settings.AndroidToolchainCacheRoot)).Width(38).Tooltip("Select XEngine toolchain cache folder").Show();
            }
        }, separator: false);

        EditorGUI.SettingsRow(paper, "and_toolchain_import", "Toolchain Import", () =>
        {
            if (snapshot.State == AndroidToolchainImportSessionState.Running)
            {
                using (paper.Row("and_toolchain_import_running").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
                {
                    float value = snapshot.TotalBytes > 0
                        ? Math.Clamp((float)snapshot.CompletedBytes / snapshot.TotalBytes, 0f, 1f)
                        : 0f;
                    Origami.ProgressBar(paper, "and_toolchain_import_progress", value).Thickness(8).ShowPercent("F0").Show();
                    Origami.Button(paper, "and_toolchain_cancel", EditorIcons.Xmark,
                        _toolchainImportSession.Cancel).Width(38).Tooltip("Cancel import").Show();
                }
            }
            else
            {
                Origami.Button(paper, "and_toolchain_import_button",
                    $"{EditorIcons.Download}  Import from Unity", () =>
                    {
                        _persistedImportFingerprint = null;
                        _toolchainImportSession.Start(new AndroidToolchainImportOptions
                        {
                            SourceRoot = settings.AndroidToolchainUnitySourceRoot,
                            DestinationRoot = settings.AndroidToolchainCacheRoot,
                        });
                    }).Show();
            }
        }, separator: false);

        if (snapshot.State != AndroidToolchainImportSessionState.Idle)
        {
            string status = snapshot.State == AndroidToolchainImportSessionState.Failed
                ? snapshot.Error
                : snapshot.Stage;
            EditorGUI.SettingsRow(paper, "and_toolchain_status", "Import Status", () =>
                paper.Box("and_toolchain_status_v").Height(EditorTheme.RowHeight).IsNotInteractable()
                    .Text(status, EditorTheme.FontMono ?? EditorTheme.DefaultFont)
                    .TextColor(snapshot.State == AndroidToolchainImportSessionState.Failed
                        ? EditorTheme.Red400
                        : EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft)
                    .TextTruncate(), separator: false);
        }

        DrawManagedToolchainDownload(paper);

        DrawDirectoryOverride(paper, "and_dotnet", ".NET SDK", settings.AndroidDotNetRoot,
            value => settings.AndroidDotNetRoot = value);
        DrawDirectoryOverride(paper, "and_jdk", "JDK Override", settings.AndroidJavaSdkRoot,
            value => settings.AndroidJavaSdkRoot = value);
        DrawDirectoryOverride(paper, "and_sdk", "SDK Override", settings.AndroidSdkRoot,
            value => settings.AndroidSdkRoot = value);
        DrawDirectoryOverride(paper, "and_ndk", "NDK Override", settings.AndroidNdkRoot,
            value => settings.AndroidNdkRoot = value);
        DrawFileOverride(paper, "and_bundletool", "Bundletool Override", settings.AndroidBundletoolPath,
            value => settings.AndroidBundletoolPath = value, ["*.jar"], ["Java archives"]);
        DrawFileOverride(paper, "and_lock", "Toolchain Lock", settings.AndroidToolchainLockPath,
            value => settings.AndroidToolchainLockPath = value, ["*.json"], ["JSON files"], save: true);

        AndroidToolchainResolveSessionSnapshot resolveSnapshot = _toolchainResolveSession.Snapshot;
        EditorGUI.SettingsRow(paper, "and_toolchain_resolve", "Resolve & Verify", () =>
        {
            if (resolveSnapshot.State == AndroidToolchainResolveSessionState.Running)
            {
                using (paper.Row("and_toolchain_resolve_running").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
                {
                    float value = resolveSnapshot.Total > 0
                        ? Math.Clamp((float)resolveSnapshot.Completed / resolveSnapshot.Total, 0f, 1f)
                        : 0f;
                    Origami.ProgressBar(paper, "and_toolchain_resolve_progress", value).Thickness(8).ShowPercent("F0").Show();
                    Origami.Button(paper, "and_toolchain_resolve_cancel", EditorIcons.Xmark,
                        _toolchainResolveSession.Cancel).Width(38).Tooltip("Cancel toolchain verification").Show();
                }
            }
            else
            {
                Origami.Button(paper, "and_toolchain_resolve_button",
                    $"{EditorIcons.Check}  Resolve, Lock & Dry Run", () =>
                    {
                        _toolchainResolveSession.Start(new AndroidToolchainResolveOptions
                        {
                            UserPreferences = new AndroidToolchainPathOverrides
                            {
                                DotNetRoot = settings.AndroidDotNetRoot,
                                JavaSdkRoot = settings.AndroidJavaSdkRoot,
                                AndroidSdkRoot = settings.AndroidSdkRoot,
                                AndroidNdkRoot = settings.AndroidNdkRoot,
                                BundletoolPath = settings.AndroidBundletoolPath,
                            },
                            ImportedToolchainRoot = settings.AndroidToolchainResolvedPath,
                            ToolchainCacheRoot = settings.AndroidToolchainCacheRoot,
                            LockFilePath = settings.AndroidToolchainLockPath,
                            TargetApi = TargetSdkVersion,
                        });
                    }).Show();
            }
        }, separator: false);

        if (resolveSnapshot.State != AndroidToolchainResolveSessionState.Idle)
        {
            string status = resolveSnapshot.State == AndroidToolchainResolveSessionState.Failed
                ? resolveSnapshot.Error
                : resolveSnapshot.Stage + (string.IsNullOrWhiteSpace(resolveSnapshot.Detail) ? string.Empty : ": " + resolveSnapshot.Detail);
            EditorGUI.SettingsRow(paper, "and_toolchain_resolve_status", "Toolchain Status", () =>
                paper.Box("and_toolchain_resolve_status_v").Height(EditorTheme.RowHeight).IsNotInteractable()
                    .Text(status, EditorTheme.FontMono ?? EditorTheme.DefaultFont)
                    .TextColor(resolveSnapshot.State == AndroidToolchainResolveSessionState.Failed
                        ? EditorTheme.Red400
                        : resolveSnapshot.State == AndroidToolchainResolveSessionState.Succeeded
                            ? EditorTheme.Green400
                            : EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft)
                    .TextTruncate(), separator: false);
        }
    }

    /// <summary>Managed toolchain acquisition: installs the com.xengine.platform.android module's
    /// pinned components (JDK / cmdline-tools / sdkmanager packages / bundletool) into the shared
    /// SDK root. Google SDK licenses are consented by checkbox — never silently.</summary>
    private void DrawManagedToolchainDownload(Paper paper)
    {
        AndroidToolchainDownloadSnapshot snapshot = _toolchainDownloadSession.Snapshot;
        // Editor-level module presence (per-user store or Editor bundle) — the primary install path
        // (launcher Settings > Platforms). A project-resolved copy still counts for legacy pins.
        bool moduleInstalled = ToolchainManifest.IsEditorModulePresent(PlatformToolchainKind.Android)
            || (Project.Current != null
                && (PackageManager.PeekCached(Project.Current) ?? PackageManager.Resolve(Project.Current))
                    .Any(p => p.Name.Equals("com.xengine.platform.android", StringComparison.OrdinalIgnoreCase)));

        EditorGUI.SettingsRow(paper, "and_toolchain_download", "Managed Toolchain (module)", () =>
        {
            if (snapshot.State == AndroidToolchainDownloadState.Running)
            {
                using (paper.Row("and_toolchain_download_running").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
                {
                    paper.Box("and_toolchain_download_stage").Width(UnitValue.StretchOne).Height(EditorTheme.RowHeight)
                        .IsNotInteractable().Text(snapshot.Message ?? "Installing…", EditorTheme.DefaultFont)
                        .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(TextAlignment.MiddleLeft).TextTruncate();
                    Origami.Button(paper, "and_toolchain_download_cancel", EditorIcons.Xmark,
                        _toolchainDownloadSession.Cancel).Width(38).Tooltip("Cancel install").Show();
                }
                return;
            }

            using (paper.Row("and_toolchain_download_row").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
            {
                Origami.Switch(paper, "and_toolchain_download_consent", _acceptAndroidSdkLicenses,
                    value => _acceptAndroidSdkLicenses = value)
                    .LabelLeft("I accept the Google Android SDK licenses").Stretch().Small().Show();
                Origami.Button(paper, "and_toolchain_download_button",
                    $"{EditorIcons.Download}  Download & Install", () =>
                    {
                        if (Project.Current == null) return;
                        _toolchainDownloadSession.Start(Project.Current, _acceptAndroidSdkLicenses);
                    })
                    .Disabled(!_acceptAndroidSdkLicenses || !moduleInstalled)
                    .Tooltip(moduleInstalled
                        ? "Installs the module's pinned JDK / cmdline-tools / SDK packages / bundletool"
                        : "Install the 'Android Build Support' module first: launcher window → Settings (gear) → Platforms")
                    .Show();
            }
        }, separator: false);

        if (snapshot.State is AndroidToolchainDownloadState.Succeeded
            or AndroidToolchainDownloadState.Failed or AndroidToolchainDownloadState.NeedsConsent)
        {
            string status = snapshot.State switch
            {
                AndroidToolchainDownloadState.Succeeded =>
                    $"Ready — {snapshot.Message} (root: {snapshot.InstallRoot}). Next: Resolve & Verify below.",
                AndroidToolchainDownloadState.NeedsConsent =>
                    "Tick the license checkbox to let sdkmanager run (" + AndroidToolchainChannel.LicenseTermsUrl + ").",
                _ => snapshot.Error ?? "Failed.",
            };
            EditorGUI.SettingsRow(paper, "and_toolchain_download_status", "Install Status", () =>
                paper.Box("and_toolchain_download_status_v").Height(EditorTheme.RowHeight).IsNotInteractable()
                    .Text(status, EditorTheme.DefaultFont)
                    .TextColor(snapshot.State == AndroidToolchainDownloadState.Failed
                        ? EditorTheme.Red400 : EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft).TextTruncate(), separator: false);
        }
    }

    private static void DrawDirectoryOverride(
        Paper paper,
        string id,
        string label,
        string current,
        Action<string> assign)
    {
        EditorGUI.SettingsRow(paper, id, label, () =>
        {
            using (paper.Row(id + "_row").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
            {
                Origami.TextField(paper, id + "_v", current, value =>
                {
                    assign(value);
                    EditorSettings.Instance.Save();
                }).Show();
                Origami.Button(paper, id + "_browse", EditorIcons.FolderOpen, () =>
                    EditorApplication.OpenFileDialog(FileDialogMode.SelectFolder, path =>
                    {
                        if (string.IsNullOrWhiteSpace(path))
                            return;
                        assign(path);
                        EditorSettings.Instance.Save();
                    }, current)).Width(38).Tooltip("Select folder").Show();
            }
        }, separator: false);
    }

    private static void DrawFileOverride(
        Paper paper,
        string id,
        string label,
        string current,
        Action<string> assign,
        string[] filters,
        string[] filterLabels,
        bool save = false)
    {
        EditorGUI.SettingsRow(paper, id, label, () =>
        {
            using (paper.Row(id + "_row").Height(EditorTheme.RowHeight).RowBetween(6).Enter())
            {
                Origami.TextField(paper, id + "_v", current, value =>
                {
                    assign(value);
                    EditorSettings.Instance.Save();
                }).Show();
                Origami.Button(paper, id + "_browse", save ? EditorIcons.FloppyDisk : EditorIcons.FolderOpen, () =>
                    EditorApplication.OpenFileDialog(save ? FileDialogMode.Save : FileDialogMode.Open, path =>
                    {
                        if (string.IsNullOrWhiteSpace(path))
                            return;
                        assign(path);
                        EditorSettings.Instance.Save();
                    }, current, filters, filterLabels)).Width(38).Tooltip(save ? "Select lock output" : "Select file").Show();
            }
        }, separator: false);
    }

    public override void ModifyDefines(List<string> defines)
    {
        // Mirrors XENGINE_WINDOWS / XENGINE_LINUX / XENGINE_DESKTOP in DesktopBuildProfile.
        defines.Add("XENGINE_ANDROID");
        defines.Add("XENGINE_MOBILE");
    }

    public override void ToDefault()
    {
        TargetAbis = ["arm64-v8a"];
        OutputMode = AndroidOutputMode.ExportProject;
        ManagedStripping = AndroidManagedStrippingMode.Disabled;
        ApplicationId = "com.xengine.game";
        VersionCode = 1;
        MinSdkVersion = 24;
        TargetSdkVersion = 36;
        MaxPackageSizeMiB = 0;
        MaxManagedSizeMiB = 0;
        MaxNativeSizeMiB = 0;
        MaxAssetSizeMiB = 0;
        MaxShaderSizeMiB = 0;
        DeploymentDeviceSerial = string.Empty;
        ApprovedShaderVariantTracePath = string.Empty;
    }
}
