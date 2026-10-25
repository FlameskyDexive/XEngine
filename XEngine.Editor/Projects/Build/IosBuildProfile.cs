using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Ios;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;

namespace XEngine.Editor.Projects.Build;

public enum IosSigningStyle
{
    Automatic = 0,
    Manual = 1,
}

/// <summary>iOS scripting and signing profile used while the iOS player pipeline is selected.</summary>
/// <remarks>
/// Fields rather than properties: Echo only serializes fields. Team ID / Bundle ID / signing
/// knobs drive the generated <c>net10.0-ios</c> csproj and the P5 publish / <c>devicectl</c> plan.
/// </remarks>
public sealed class IosBuildProfile : PlatformBuildProfile
{
    /// <summary>Reverse-DNS bundle identifier written to Info.plist and ApplicationId.</summary>
    public string ApplicationId = "com.xengine.game";

    /// <summary>Apple Developer Team ID required for Automatic signing and recorded on the report.</summary>
    public string TeamId = string.Empty;

    /// <summary>
    /// Debug defaults to Automatic. Release always uses Manual (explicit profile + certificate)
    /// regardless of this field.
    /// </summary>
    public IosSigningStyle SigningStyle = IosSigningStyle.Automatic;

    /// <summary>Path to a provisioning profile (XML plist on Windows; CMS on Mac).</summary>
    public string ProvisioningProfilePath = string.Empty;

    /// <summary>Codesign identity. Debug Automatic uses Apple Development.</summary>
    public string SigningCertificate = "Apple Development";

    /// <summary>Target device UDID for Build &amp; Run via <c>xcrun devicectl</c>.</summary>
    public string DeploymentDeviceUdid = string.Empty;

    /// <summary>
    /// Build for the iOS Simulator (<c>iossimulator-arm64</c>) instead of a physical device.
    /// Simulator builds consume the RID-built XEngine.Runtime and skip code signing entirely.
    /// </summary>
    public bool UseSimulator;

    /// <summary>ILLink policy. Full is fail-closed until a separate evidence gate passes.</summary>
    public IosManagedStrippingMode ManagedStripping = IosManagedStrippingMode.Disabled;

    public int MaxPackageSizeMiB;
    public int MaxManagedSizeMiB;
    public int MaxNativeSizeMiB;
    public int MaxAssetSizeMiB;
    public int MaxShaderSizeMiB;

    public override Type GetPipelineType() => typeof(IosBuildPipeline);

    public override void ToDefault()
    {
        ApplicationId = "com.xengine.game";
        TeamId = string.Empty;
        SigningStyle = IosSigningStyle.Automatic;
        ProvisioningProfilePath = string.Empty;
        SigningCertificate = "Apple Development";
        DeploymentDeviceUdid = string.Empty;
        UseSimulator = false;
        ManagedStripping = IosManagedStrippingMode.Disabled;
        MaxPackageSizeMiB = 0;
        MaxManagedSizeMiB = 0;
        MaxNativeSizeMiB = 0;
        MaxAssetSizeMiB = 0;
        MaxShaderSizeMiB = 0;
        ScriptingDefineSymbols = [];
    }

    public override void ModifyDefines(List<string> defines)
    {
        defines.Add("XENGINE_IOS");
        defines.Add("XENGINE_MOBILE");
    }

    private XcodeLocatorResult? _xcodeCheck;

    /// <summary>Toolchain preflight: Xcode locate/validate (macOS; Apple forbids redistribution so
    /// there is nothing to download) plus the signing-plan readiness summary this host can prepare
    /// from Windows.</summary>
    public override void OnGUI(Paper paper)
    {
        EditorGUI.SettingsRow(paper, "ios_toolchain", "Toolchain (Xcode)", () =>
        {
            using (paper.Row("ios_toolchain_row").Height(28).RowBetween(6).Enter())
            {
                paper.Box("ios_toolchain_status").Width(UnitValue.StretchOne).Height(28).IsNotInteractable()
                    .Text(XcodeStatusText(), EditorTheme.DefaultFont)
                    .TextColor(_xcodeCheck is { Found: true, MeetsMinimum: true }
                        ? EditorTheme.Green400
                        : EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft).TextTruncate();
                Origami.Button(paper, "ios_toolchain_check", $"{EditorIcons.CircleInfo}  Check Xcode", () =>
                {
                    _xcodeCheck = new XcodeLocator().LocateAsync(new Version(16, 0))
                        .GetAwaiter().GetResult();
                }).Show();
            }
        }, separator: false);

        EditorGUI.SettingsRow(paper, "ios_preflight", "Signing Preflight", () =>
        {
            bool teamIdSet = !string.IsNullOrWhiteSpace(TeamId);
            bool profileReady = SigningStyle == IosSigningStyle.Automatic
                || (File.Exists(ProvisioningProfilePath) && !string.IsNullOrWhiteSpace(SigningCertificate));
            string summary =
                $"TeamId: {(teamIdSet ? "set" : "missing")} · Signing: {SigningStyle} · " +
                $"Profile: {(SigningStyle == IosSigningStyle.Automatic ? "n/a (automatic)" : profileReady ? "ready" : "missing")} · " +
                $"Device UDID: {(string.IsNullOrWhiteSpace(DeploymentDeviceUdid) ? "not set (Build & Run needs one)" : "set")}";
            paper.Box("ios_preflight_status").Height(28).IsNotInteractable()
                .Text(summary, EditorTheme.DefaultFont)
                .TextColor(teamIdSet && profileReady ? EditorTheme.Green400 : EditorTheme.Amber400)
                .FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft).TextTruncate();
        }, separator: false);
    }

    private string XcodeStatusText()
    {
        if (_xcodeCheck == null)
            return OperatingSystem.IsMacOS()
                ? "Not checked yet — run Check Xcode to locate and validate (DEVELOPER_DIR / xcode-select, 16.0+)."
                : "Signing/publishing run on macOS; this host prepares signing plans and preflights.";
        XcodeLocatorResult check = _xcodeCheck;
        if (check is { Found: true, MeetsMinimum: true })
            return $"Xcode {check.Version} at {check.DeveloperDir}";
        return check.Guidance ?? check.ErrorText();
    }
}

file static class XcodeLocatorResultExtensions
{
    public static string ErrorText(this XcodeLocatorResult result)
        => result.CommandLineToolsOnly ? "Command-Line Tools only — full Xcode required." : "Xcode not found.";
}
