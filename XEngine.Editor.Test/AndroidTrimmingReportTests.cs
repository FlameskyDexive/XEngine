// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using XEngine.Editor.Build.Android;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidTrimmingReportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineAndroidTrimmingReportTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SafeModeWritesFiveDeterministicCategoriesAndEnablesConservativeLinker()
    {
        AndroidTrimmingReport report = AndroidTrimmingReport.Create(
            AndroidManagedStrippingMode.Safe,
            assetCount: 12,
            shaderVariantCount: 7,
            ["x86_64", "arm64-v8a", "arm64-v8a"],
            managedTypeRootCount: 9);

        Assert.Equal(AndroidManagedStrippingMode.Safe, report.RequestedManagedMode);
        Assert.Equal(AndroidManagedStrippingMode.Safe, report.EffectiveManagedMode);
        Assert.True(report.PublishTrimmed);
        Assert.Equal(9, report.ManagedTypeRootCount);
        Assert.Equal(2, report.TargetAbis.Count);
        Assert.Equal("arm64-v8a", report.TargetAbis[0]);
        Assert.Equal("x86_64", report.TargetAbis[1]);
        Assert.Equal(5, report.Categories.Count);
        Assert.Equal("platform-dependency", report.Categories[0].Id);
        Assert.Equal("managed-il-link", report.Categories[1].Id);
        Assert.Equal("asset-reachability", report.Categories[2].Id);
        Assert.Equal("shader-variant", report.Categories[3].Id);
        Assert.Equal("native-and-r8", report.Categories[4].Id);

        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "trimming", "android-trimming.json");
        AndroidTrimmingReport.WriteReport(path, report);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Safe", document.RootElement.GetProperty("requestedManagedMode").GetString());
        Assert.True(document.RootElement.GetProperty("publishTrimmed").GetBoolean());
        Assert.Equal(9, document.RootElement.GetProperty("managedTypeRootCount").GetInt32());
        Assert.Equal(5, document.RootElement.GetProperty("categories").GetArrayLength());
    }

    [Fact]
    public void FullModeIsBlockedUntilRootsAreImplemented()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            AndroidTrimmingReport.ValidateRequestedMode(AndroidManagedStrippingMode.Full));

        Assert.Contains("generated reflection roots", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
