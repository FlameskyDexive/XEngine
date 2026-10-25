// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using XEngine.Editor.Build;
using XEngine.Editor.Build.HarmonyOS;
using XEngine.Editor.Projects.Build;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSStageProjectContributionTests
{
    private static string CreateStageProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "xengine-stage-contrib-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(Path.Combine(stage, "entry", "src", "main", "ets"));
        File.WriteAllText(
            Path.Combine(stage, "entry", "src", "main", "module.json5"),
            """
            {
              "module": {
                "name": "entry",
                "abilities": [
                  {
                    "name": "EntryAbility",
                    "srcEntry": "./ets/entryability/EntryAbility.ets"
                  }
                ]
              }
            }
            """,
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(stage, "entry", "oh-package.json5"),
            """
            {
              "name": "entry",
              "version": "1.0.0",
              "dependencies": {
              }
            }
            """,
            new UTF8Encoding(false));
        return stage;
    }

    private static HarmonyOSStageProjectContext CreateContext(string stage) => new()
    {
        StageProjectDirectory = stage,
        ProjectPath = Path.GetDirectoryName(stage)!,
        ApplicationId = "com.example.game",
        Abi = "arm64-v8a",
        ApiLevel = 13,
    };

    [Fact]
    public void RunAll_AggregatesContributorErrorsWithOwnerPrefix()
    {
        var failing = new ThrowingContributor();
        var erroring = new ErroringContributor();

        List<string> errors = HarmonyOSStageProjectContribution.RunAll(CreateContext(CreateStageProject()));

        Assert.Contains($"[{nameof(ErroringContributor)}] slot exhausted", errors);
        Assert.Contains($"[{nameof(ThrowingContributor)}] contribution threw: boom", errors);
        Assert.All(errors, e => Assert.StartsWith("[", e));
        GC.KeepAlive(failing);
    }

    [Fact]
    public void RunAll_FakeContributorReceivesMaterializedContext()
    {
        string stage = CreateStageProject();
        // Discovery snapshots contributor instances once per process; observe the invoked
        // instance through static state, not the test-local instance.
        RecordingContributor.Reset();

        HarmonyOSStageProjectContribution.RunAll(CreateContext(stage));

        Assert.True(RecordingContributor.Called);
        Assert.Equal(stage, RecordingContributor.Context!.StageProjectDirectory);
        Assert.Equal("com.example.game", RecordingContributor.Context.ApplicationId);
        Assert.Equal("arm64-v8a", RecordingContributor.Context.Abi);
        Assert.Equal(13, RecordingContributor.Context.ApiLevel);
    }

    [Fact]
    public void EnsureModulePermissions_InsertsBlockBeforeAbilities()
    {
        string stage = CreateStageProject();
        string modulePath = Path.Combine(stage, "entry", "src", "main", "module.json5");

        HarmonyOSStageProjectPatch.EnsureModulePermissions(stage, "ohos.permission.INTERNET", "ohos.permission.APP_TRACKING_CONSENT");

        string json = File.ReadAllText(modulePath);
        Assert.Contains("\"requestPermissions\": [ { \"name\": \"ohos.permission.INTERNET\" }, { \"name\": \"ohos.permission.APP_TRACKING_CONSENT\" } ],", json);
        Assert.True(json.IndexOf("requestPermissions", StringComparison.Ordinal) < json.IndexOf("abilities", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureModulePermissions_AppendsToExistingArrayAndSkipsDuplicates()
    {
        string stage = CreateStageProject();
        string modulePath = Path.Combine(stage, "entry", "src", "main", "module.json5");
        File.WriteAllText(modulePath,
            """
            {
              "module": {
                "name": "entry",
                "requestPermissions": [
                  { "name": "ohos.permission.INTERNET" }
                ],
                "abilities": [
                  { "name": "EntryAbility" }
                ]
              }
            }
            """,
            new UTF8Encoding(false));

        HarmonyOSStageProjectPatch.EnsureModulePermissions(stage, "ohos.permission.INTERNET", "ohos.permission.GET_NETWORK_INFO");

        string json = File.ReadAllText(modulePath);
        Assert.Equal(1, CountOccurrences(json, "ohos.permission.INTERNET"));
        Assert.Contains("ohos.permission.GET_NETWORK_INFO", json);
        Assert.Contains("requestPermissions", json);
    }

    [Fact]
    public void EnsureModulePermissions_ThrowsWhenModuleManifestMissing()
    {
        string stage = CreateStageProject();
        File.Delete(Path.Combine(stage, "entry", "src", "main", "module.json5"));

        Assert.Throws<FileNotFoundException>(() =>
            HarmonyOSStageProjectPatch.EnsureModulePermissions(stage, "ohos.permission.INTERNET"));
    }

    [Fact]
    public void WriteEntrySourceFile_WritesNestedPathUnderEtsRoot()
    {
        string stage = CreateStageProject();

        HarmonyOSStageProjectPatch.WriteEntrySourceFile(
            stage,
            "xengineads/XengineAds.ets",
            "export const kAdsVersion = '1.0.0';\n");

        string written = File.ReadAllText(Path.Combine(stage, "entry", "src", "main", "ets", "xengineads", "XengineAds.ets"));
        Assert.Contains("kAdsVersion", written);
    }

    [Fact]
    public void WriteEntrySourceFile_RejectsPathEscapingEtsRoot()
    {
        string stage = CreateStageProject();

        Assert.Throws<InvalidDataException>(() =>
            HarmonyOSStageProjectPatch.WriteEntrySourceFile(stage, "../../evil.ets", "x"));
    }

    [Fact]
    public void AddLocalHarDependency_CopiesArchiveAndPatchesOhPackage()
    {
        string stage = CreateStageProject();
        string harSource = Path.Combine(Path.GetTempPath(), "openadsdk-" + Guid.NewGuid().ToString("N") + ".har");
        File.WriteAllBytes(harSource, [0x50, 0x4b, 0x03, 0x04]);

        HarmonyOSStageProjectPatch.AddLocalHarDependency(stage, "openadsdk", harSource);
        // Idempotent: a repeated contribution must not duplicate the dependency entry.
        HarmonyOSStageProjectPatch.AddLocalHarDependency(stage, "openadsdk", harSource);

        Assert.True(File.Exists(Path.Combine(stage, "entry", "libs", "openadsdk.har")));
        string json = File.ReadAllText(Path.Combine(stage, "entry", "oh-package.json5"));
        Assert.Equal(1, CountOccurrences(json, "\"openadsdk\": \"file:./libs/openadsdk.har\""));
    }

    [Fact]
    public void AddLocalHarDependency_RejectsNonHarArchive()
    {
        string stage = CreateStageProject();
        string notHar = Path.Combine(Path.GetTempPath(), "not-a-har-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(notHar, [0x00]);

        Assert.Throws<InvalidDataException>(() =>
            HarmonyOSStageProjectPatch.AddLocalHarDependency(stage, "openadsdk", notHar));
    }

    [Fact]
    public void PipelineContributions_AggregatedErrorsFailBuildWithStageContributionFailed()
    {
        string stage = CreateStageProject();
        var layout = new HarmonyOSPlayerProjectLayout(
            Path.GetDirectoryName(stage)!,
            Path.GetDirectoryName(stage)!,
            stage,
            Path.Combine(Path.GetDirectoryName(stage)!, "ownership.json"),
            "AssemblyName",
            "openharmony-arm64",
            "arm64-v8a");
        var profile = new HarmonyOSBuildProfile();
        profile.ToDefault();

        HarmonyOSBuildException error = Assert.Throws<HarmonyOSBuildException>(
            () => HarmonyOSBuildPipeline.RunStageProjectContributions(layout, profile));
        Assert.Equal(HarmonyOSBuildErrorCode.StageContributionFailed, error.Code);
        Assert.Contains(nameof(ErroringContributor), error.Message);
    }

    private static int CountOccurrences(string value, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    private sealed class RecordingContributor : IHarmonyOSStageProjectContributor
    {
        public static bool Called { get; private set; }
        public static HarmonyOSStageProjectContext? Context { get; private set; }

        public static void Reset()
        {
            Called = false;
            Context = null;
        }

        public string Owner => nameof(RecordingContributor);

        public List<string> Contribute(HarmonyOSStageProjectContext context)
        {
            Called = true;
            Context = context;
            return [];
        }
    }

    private sealed class ErroringContributor : IHarmonyOSStageProjectContributor
    {
        public string Owner => nameof(ErroringContributor);

        public List<string> Contribute(HarmonyOSStageProjectContext context) => ["slot exhausted"];
    }

    private sealed class ThrowingContributor : IHarmonyOSStageProjectContributor
    {
        public string Owner => nameof(ThrowingContributor);

        public List<string> Contribute(HarmonyOSStageProjectContext context) => throw new InvalidOperationException("boom");
    }
}
