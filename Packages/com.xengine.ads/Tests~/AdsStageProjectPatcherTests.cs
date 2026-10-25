// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Ads;
using XEngine.Ads.Editor.Build;

using Xunit;

namespace XEngine.Ads.Tests;

public sealed class AdsStageProjectPatcherTests
{
    private static string CreateStageProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "xengine-ads-stage-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(Path.Combine(stage, "entry", "src", "main", "ets", "entryability"));
        Directory.CreateDirectory(Path.Combine(stage, "entry", "src", "main", "resources", "resfile"));
        File.WriteAllText(
            Path.Combine(stage, "entry", "src", "main", "module.json5"),
            """
            {
              "module": {
                "name": "entry",
                "abilities": [
                  { "name": "EntryAbility" }
                ]
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(stage, "entry", "oh-package.json5"),
            """
            {
              "name": "entry",
              "version": "1.0.0",
              "dependencies": {
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(stage, "entry", "src", "main", "ets", "entryability", "EntryAbility.ets"),
            """
            import { AbilityConstant, ConfigurationConstant, UIAbility, Want } from '@kit.AbilityKit';
            import { hilog } from '@kit.PerformanceAnalysisKit';
            import { window } from '@kit.ArkUI';

            export default class EntryAbility extends UIAbility {
              onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
                this.context.getApplicationContext().setColorMode(ConfigurationConstant.ColorMode.COLOR_MODE_NOT_SET);
              }
            }
            """);
        return stage;
    }

    private static string CreatePackage()
    {
        string packageRoot = Path.Combine(Path.GetTempPath(), "xengine-ads-pkg-" + Guid.NewGuid().ToString("N"));
        string glueDir = Path.Combine(packageRoot, "Native", "glue");
        Directory.CreateDirectory(glueDir);
        File.WriteAllText(Path.Combine(glueDir, "XengineAdsHuawei.ets"), "export function setAdsContext(c: object): void {}");
        File.WriteAllText(Path.Combine(glueDir, "XengineAdsPangle.ets"), "export function setAdsContext(c: object): void {}");
        return packageRoot;
    }

    private static AdsBuildConfig HuaweiConfig() => new()
    {
        ProviderId = "huawei-adskit",
        UseTestAds = true,
        VendorAppId = "hw-app",
        RewardVideoSlotId = "reward-slot",
        InterstitialSlotId = "inter-slot",
    };

    [Fact]
    public void Apply_InjectsGlueEntryWiringPermissionsAndPayload()
    {
        string stage = CreateStageProject();
        string packageRoot = CreatePackage();
        var errors = new List<string>();

        AdsStageProjectPatcher.Apply(stage, packageRoot, HuaweiConfig(), errors);

        Assert.Empty(errors);
        string glue = Path.Combine(stage, "entry", "src", "main", "ets", "xengineads", "XengineAdsHuawei.ets");
        Assert.True(File.Exists(glue));

        string entryAbility = File.ReadAllText(Path.Combine(stage, "entry", "src", "main", "ets", "entryability", "EntryAbility.ets"));
        Assert.Contains("import { setAdsContext } from '../xengineads/XengineAdsHuawei';", entryAbility);
        Assert.Contains("setAdsContext(this.context);", entryAbility);
        Assert.True(entryAbility.IndexOf("import { setAdsContext }", StringComparison.Ordinal) <
                    entryAbility.IndexOf("export default class", StringComparison.Ordinal));

        string module = File.ReadAllText(Path.Combine(stage, "entry", "src", "main", "module.json5"));
        Assert.Contains("ohos.permission.INTERNET", module);

        string payload = File.ReadAllText(Path.Combine(stage, "entry", "src", "main", "resources", "resfile", "Settings", "Ads.json"));
        Assert.Contains("\"providerId\": \"huawei-adskit\"", payload);
        Assert.Contains("\"rewardVideoSlotId\": \"reward-slot\"", payload);
    }

    [Fact]
    public void Apply_IsIdempotentAcrossRebuilds()
    {
        string stage = CreateStageProject();
        string packageRoot = CreatePackage();
        var errors = new List<string>();

        AdsStageProjectPatcher.Apply(stage, packageRoot, HuaweiConfig(), errors);
        string entryAbility = File.ReadAllText(Path.Combine(stage, "entry", "src", "main", "ets", "entryability", "EntryAbility.ets"));
        AdsStageProjectPatcher.Apply(stage, packageRoot, HuaweiConfig(), errors);

        Assert.Empty(errors);
        string reread = File.ReadAllText(Path.Combine(stage, "entry", "src", "main", "ets", "entryability", "EntryAbility.ets"));
        Assert.Equal(entryAbility, reread);
    }

    [Fact]
    public void Apply_MockProviderIsANoOp()
    {
        string stage = CreateStageProject();
        string packageRoot = CreatePackage();
        var errors = new List<string>();

        AdsStageProjectPatcher.Apply(stage, packageRoot, new AdsBuildConfig { ProviderId = "mock" }, errors);

        Assert.Empty(errors);
        Assert.False(Directory.Exists(Path.Combine(stage, "entry", "src", "main", "ets", "xengineads")));
    }

    [Fact]
    public void Apply_MissingGlueSourceReportsError()
    {
        string stage = CreateStageProject();
        string packageRoot = CreatePackage();
        File.Delete(Path.Combine(packageRoot, "Native", "glue", "XengineAdsHuawei.ets"));
        var errors = new List<string>();

        AdsStageProjectPatcher.Apply(stage, packageRoot, HuaweiConfig(), errors);

        Assert.Single(errors);
        Assert.Contains("glue source is missing", errors[0]);
    }

    [Fact]
    public void Apply_MissingEntryAbilityReportsError()
    {
        string stage = CreateStageProject();
        string packageRoot = CreatePackage();
        File.Delete(Path.Combine(stage, "entry", "src", "main", "ets", "entryability", "EntryAbility.ets"));
        var errors = new List<string>();

        AdsStageProjectPatcher.Apply(stage, packageRoot, HuaweiConfig(), errors);

        Assert.Contains(errors, e => e.Contains("EntryAbility"));
    }

    [Fact]
    public void Apply_PangleInjectsHarDependencyAndPayload()
    {
        string stage = CreateStageProject();
        string packageRoot = CreatePackage();
        string harSource = Path.Combine(Path.GetTempPath(), "openadsdk-" + Guid.NewGuid().ToString("N") + ".har");
        File.WriteAllBytes(harSource, [0x50, 0x4b, 0x03, 0x04]);
        var errors = new List<string>();
        var config = new AdsBuildConfig
        {
            ProviderId = "pangle",
            UseTestAds = true,
            VendorAppId = "pg-app",
            RewardVideoSlotId = "pg-reward",
            HarSourcePath = harSource,
        };

        AdsStageProjectPatcher.Apply(stage, packageRoot, config, errors);

        Assert.Empty(errors);
        Assert.True(File.Exists(Path.Combine(stage, "entry", "libs", "openadsdk.har")));
        string ohPackage = File.ReadAllText(Path.Combine(stage, "entry", "oh-package.json5"));
        Assert.Contains("\"openadsdk\": \"file:./libs/openadsdk.har\"", ohPackage);
        Assert.True(File.Exists(Path.Combine(stage, "entry", "src", "main", "ets", "xengineads", "XengineAdsPangle.ets")));
        string payload = File.ReadAllText(Path.Combine(stage, "entry", "src", "main", "resources", "resfile", "Settings", "Ads.json"));
        Assert.Contains("\"providerId\": \"pangle\"", payload);
    }

    [Fact]
    public void Apply_PangleWithoutHarReportsErrorButStillInjectsGlue()
    {
        string stage = CreateStageProject();
        string packageRoot = CreatePackage();
        var errors = new List<string>();
        var config = new AdsBuildConfig { ProviderId = "pangle", RewardVideoSlotId = "pg-reward", HarSourcePath = "" };

        AdsStageProjectPatcher.Apply(stage, packageRoot, config, errors);

        Assert.Contains(errors, e => e.Contains("openadsdk.har"));
        Assert.False(File.Exists(Path.Combine(stage, "entry", "libs", "openadsdk.har")));
        // The glue and payload still land so the build reports a precise vendor error on device.
        Assert.True(File.Exists(Path.Combine(stage, "entry", "src", "main", "ets", "xengineads", "XengineAdsPangle.ets")));
    }

    [Fact]
    public void BuildPayloadJson_ProducesStableFlatSchema()
    {
        string json = AdsStageProjectPatcher.BuildPayloadJson(HuaweiConfig());

        Assert.Equal(
            """
            {
              "schema": "xengine.ads.settings.v1",
              "providerId": "huawei-adskit",
              "useTestAds": true,
              "appId": "hw-app",
              "rewardVideoSlotId": "reward-slot",
              "interstitialSlotId": "inter-slot"
            }
            """.ReplaceLineEndings("\n"),
            json);
    }
}

public sealed class AdsBootstrapperTests
{
    [Fact]
    public void TryLoad_ParsesTheContributedPayload()
    {
        string settingsDir = Path.Combine(Path.GetTempPath(), "xengine-ads-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, AdsBootstrapper.PayloadFileName),
            """
            {
              "schema": "xengine.ads.settings.v1",
              "providerId": "pangle",
              "useTestAds": false,
              "appId": "pg-app",
              "rewardVideoSlotId": "pg-reward",
              "interstitialSlotId": ""
            }
            """);

        bool loaded = AdsBootstrapper.TryLoad(settingsDir, out AdsSettings settings);

        Assert.True(loaded);
        Assert.Equal("pangle", settings.ProviderId);
        Assert.False(settings.UseTestAds);
        AdsProviderConfig? config = settings.GetProvider("pangle");
        Assert.NotNull(config);
        Assert.Equal("pg-app", config!.AppId);
        Assert.Equal("pg-reward", config.RewardVideoSlotId);
    }

    [Fact]
    public void TryLoad_MissingPayloadReturnsFalse()
    {
        Assert.False(AdsBootstrapper.TryLoad(Path.Combine(Path.GetTempPath(), "xengine-ads-none-" + Guid.NewGuid().ToString("N")), out _));
    }

    [Fact]
    public void TryLoad_MalformedPayloadReturnsFalse()
    {
        string settingsDir = Path.Combine(Path.GetTempPath(), "xengine-ads-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, AdsBootstrapper.PayloadFileName), "{ not json");

        Assert.False(AdsBootstrapper.TryLoad(settingsDir, out _));
    }

    [Fact]
    public void TryParse_RejectsPayloadWithoutProviderId()
    {
        var settings = new AdsSettings();
        Assert.False(AdsBootstrapper.TryParse("{\"schema\":\"xengine.ads.settings.v1\"}", ref settings));
    }
}
