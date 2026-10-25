// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using XEngine.Editor;
using XEngine.Editor.Build;
using XEngine.Editor.Build.HarmonyOS;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSBuildProfileTests
{
    [Fact]
    public void Profile_DefaultsToApi13Arm64AndMobileDefines()
    {
        var profile = new HarmonyOSBuildProfile();
        var defines = new List<string>();

        profile.ToDefault();
        profile.ModifyDefines(defines);

        Assert.Equal(13, profile.TargetApi);
        Assert.Equal(["arm64-v8a"], profile.TargetAbis);
        Assert.Equal(HarmonyOSOutputMode.ExportProject, profile.OutputMode);
        Assert.Equal("None", profile.SigningReference);
        Assert.Empty(profile.RunDeviceId);
        Assert.Equal("EntryAbility", profile.AbilityName);
        Assert.Equal(HarmonyOSBuildRunInstallMode.InstallOrReplace, profile.InstallMode);
        Assert.True(profile.CaptureRunScreenshot);
        Assert.False(profile.CleanupNewInstallationOnFailure);
        Assert.Contains("XENGINE_HARMONYOS", defines);
        Assert.Contains("XENGINE_MOBILE", defines);
        Assert.Equal(typeof(HarmonyOSBuildPipeline), profile.GetPipelineType());
    }

    [Fact]
    public void Profile_RejectsSigningMaterialAndAcceptsEnvironmentReference()
    {
        var profile = new HarmonyOSBuildProfile { SigningReference = "signing-password" };

        HarmonyOSBuildException error = Assert.Throws<HarmonyOSBuildException>(profile.ValidateForBuild);
        Assert.Equal(HarmonyOSBuildErrorCode.SigningReferenceInvalid, error.Code);

        profile.SigningReference = "EnvironmentVariable:CI_OHOS_SIGNING";
        profile.ValidateForBuild();
    }

    [Fact]
    public void Profile_SelectTargetAbi_ReplacesExistingSelection()
    {
        var profile = new HarmonyOSBuildProfile { TargetAbis = ["arm64-v8a", "x86_64"] };

        profile.SelectTargetAbi("x86_64");

        Assert.Equal(["x86_64"], profile.TargetAbis);
    }

    [Fact]
    public void Profile_RejectsNonPositiveVersionCode()
    {
        var profile = new HarmonyOSBuildProfile { VersionCode = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(profile.ValidateForBuild);
    }

    [Fact]
    public async Task Pipeline_RequiresAnOpenProjectBeforeResolvingTheLockedToolchain()
    {
        BuildResult result = await new HarmonyOSBuildPipeline().BuildAsync(
            "project",
            new BuildSettings(),
            "output");

        Assert.False(result.Success);
        Assert.Contains("No project open", result.Errors, StringComparison.Ordinal);
        Assert.Empty(result.OutputPath);
    }

    [Fact]
    public void Pipeline_DoesNotPretendAnUnpublishedPackageIsAnExecutable()
    {
        var settings = new BuildSettings();
        HarmonyOSBuildProfile profile = settings.GetProfile<HarmonyOSBuildProfile>(typeof(HarmonyOSBuildPipeline));
        profile.OutputMode = HarmonyOSOutputMode.Hap;

        Assert.Empty(new HarmonyOSBuildPipeline().GetExecutablePath(
            Path.Combine(Path.GetTempPath(), "xengine-harmonyos-no-artifact-" + Guid.NewGuid().ToString("N")),
            settings));
    }

    [Fact]
    public void Pipeline_ReturnsPublishedHapArtifactAsItsLaunchableOutput()
    {
        string artifact = Path.Combine(Path.GetTempPath(), "xengine-harmonyos-" + Guid.NewGuid().ToString("N") + ".hap");
        File.WriteAllText(artifact, "fixture");
        try
        {
            var settings = new BuildSettings();
            HarmonyOSBuildProfile profile = settings.GetProfile<HarmonyOSBuildProfile>(typeof(HarmonyOSBuildPipeline));
            profile.OutputMode = HarmonyOSOutputMode.Hap;

            Assert.Equal(artifact, new HarmonyOSBuildPipeline().GetExecutablePath(artifact, settings));
        }
        finally
        {
            File.Delete(artifact);
        }
    }

    [Fact]
    public void EditorSettings_ContainsOnlyNonSecretHarmonyOSPreferences()
    {
        var settings = new EditorSettings();

        Assert.Empty(settings.HarmonyOSSdkBundleSourcePath);
        Assert.Empty(settings.HarmonyOSOfficialSdkRoot);
        Assert.Empty(settings.HarmonyOSLastResolvedLockPath);
    }

    [Fact]
    public void ImportHarmonyOSBundle_RequiresProjectRootArgument()
    {
        ResetHarmonyOSProgramArguments();

        Exception error = Assert.Throws<Exception>(() => Program.ReadArguments([
            "--import-harmonyos-bundle",
            "bundle.zip",
        ]));

        Assert.Contains("--harmonyos-project-root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonyOSCliArguments_ParseExplicitProjectSdkApiAndAbi()
    {
        ResetHarmonyOSProgramArguments();

        Program.ReadArguments(
        [
            "--harmonyos-project-root", "project",
            "--import-harmonyos-bundle", "bundle.zip",
            "--harmonyos-sdk", "sdk",
            "--harmonyos-api", "26",
            "--harmonyos-abi", "x86_64",
        ]);

        Assert.Equal("project", Program.HarmonyOSProjectRoot);
        Assert.Equal("bundle.zip", Program.HarmonyOSBundleImportPath);
        Assert.Equal("sdk", Program.HarmonyOSOfficialSdkRoot);
        Assert.Equal(26, Program.HarmonyOSTargetApi);
        Assert.Equal("x86_64", Program.HarmonyOSTargetAbi);
    }

    [Fact]
    public void HarmonyOSBuildCli_AppliesExplicitNonInteractiveBuildAndRunOverrides()
    {
        ResetHarmonyOSProgramArguments();
        Program.ReadArguments(
        [
            "--buildmode", "project",
            "--platform", "harmonyos",
            "--build-and-run",
            "--harmonyos-lock", "project/ThirdParty/OpenHarmonySdk/openharmony-sdk-lock.json",
            "--harmonyos-sdk", "official-sdk",
            "--harmonyos-api", "26",
            "--harmonyos-abi", "x86_64",
            "--harmonyos-output", "hap",
            "--device", "emulator-26",
            "--harmonyos-ability", "MainAbility",
            "--harmonyos-install-mode", "launch-only",
            "--cleanup-new-install-on-failure",
        ]);
        var settings = new BuildSettings();
        var editorSettings = new EditorSettings();

        Program.ApplyBuildModeOverrides(settings, editorSettings);

        HarmonyOSBuildProfile profile = settings.GetProfile<HarmonyOSBuildProfile>(typeof(HarmonyOSBuildPipeline));
        Assert.True(Program.BuildAndRun);
        Assert.Equal("xengine.harmonyos", settings.ActivePipelineId);
        Assert.Equal(26, profile.TargetApi);
        Assert.Equal(["x86_64"], profile.TargetAbis);
        Assert.Equal(HarmonyOSOutputMode.Hap, profile.OutputMode);
        Assert.Equal("emulator-26", profile.RunDeviceId);
        Assert.Equal("MainAbility", profile.AbilityName);
        Assert.Equal(HarmonyOSBuildRunInstallMode.LaunchOnly, profile.InstallMode);
        Assert.True(profile.CleanupNewInstallationOnFailure);
        Assert.Equal("official-sdk", editorSettings.HarmonyOSOfficialSdkRoot);
        Assert.Equal("project/ThirdParty/OpenHarmonySdk/openharmony-sdk-lock.json", editorSettings.HarmonyOSLastResolvedLockPath);
    }

    [Theory]
    [InlineData("--platform", "unknown")]
    [InlineData("--harmonyos-output", "zip")]
    [InlineData("--harmonyos-install-mode", "overwrite")]
    public void HarmonyOSBuildCli_RejectsUnknownSelections(string option, string value)
    {
        ResetHarmonyOSProgramArguments();

        Assert.Throws<Exception>(() => Program.ReadArguments([option, value]));
    }

    private static void ResetHarmonyOSProgramArguments()
    {
        SetStaticAutoProperty(nameof(Program.BuildMode), false);
        SetStaticAutoProperty(nameof(Program.BuildAndRun), false);
        SetStaticAutoProperty(nameof(Program.BuildPipelineId), null);
        SetStaticAutoProperty(nameof(Program.BuildProfileToken), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSProjectRoot), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSBundleImportPath), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSResolveLockPath), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSOfficialSdkRoot), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSBuildLockPath), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSBuildOutputMode), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSDeviceId), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSAbilityName), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSBuildInstallMode), null);
        SetStaticAutoProperty(nameof(Program.HarmonyOSCleanupNewInstallationOnFailure), false);
        SetStaticAutoProperty(nameof(Program.HarmonyOSTargetApi), 13);
        SetStaticAutoProperty(nameof(Program.HarmonyOSTargetApiSpecified), false);
        SetStaticAutoProperty(nameof(Program.HarmonyOSTargetAbi), "arm64-v8a");
        SetStaticAutoProperty(nameof(Program.HarmonyOSTargetAbiSpecified), false);
    }

    private static void SetStaticAutoProperty(string name, object? value)
    {
        PropertyInfo property = typeof(Program).GetProperty(name, BindingFlags.Public | BindingFlags.Static)!;
        property.SetValue(null, value);
    }
}
