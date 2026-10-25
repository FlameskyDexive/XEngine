// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;
using System.Text.Json;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Android;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidPackageSizeReportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineAndroidPackageSizeTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(AndroidOutputMode.Apk, "")]
    [InlineData(AndroidOutputMode.Aab, "base/")]
    public void ReportClassifiesFinalArchiveContributions(AndroidOutputMode mode, string prefix)
    {
        Directory.CreateDirectory(_root);
        string artifact = Path.Combine(_root, mode == AndroidOutputMode.Apk ? "game.apk" : "game.aab");
        CreatePackage(artifact, prefix);
        string reportPath = Path.Combine(_root, "verification", AndroidPackageSizeReport.FileName);
        AndroidPackageSizeBudget budget = new(4096, 512, 512, 512, 512);

        AndroidPackageSizeSnapshot snapshot = AndroidPackageSizeReport.MeasureAndWrite(
            artifact,
            mode,
            budget,
            reportPath);
        snapshot.Enforce(reportPath);

        Assert.True(snapshot.PackageBytes > 0);
        Assert.Equal(5, snapshot.Categories.Count);
        Assert.Equal(1, Category(snapshot, "managed").EntryCount);
        Assert.Equal(1, Category(snapshot, "native").EntryCount);
        Assert.Equal(1, Category(snapshot, "assets").EntryCount);
        Assert.Equal(1, Category(snapshot, "shader").EntryCount);
        Assert.Equal(2, Category(snapshot, "other").EntryCount);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal(AndroidPackageSizeReport.SchemaVersion, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(mode.ToString(), report.RootElement.GetProperty("outputMode").GetString());
        Assert.True(report.RootElement.GetProperty("budgetEnabled").GetBoolean());
        Assert.Equal(5, report.RootElement.GetProperty("categories").GetArrayLength());
        Assert.Equal(6, report.RootElement.GetProperty("largestEntries").GetArrayLength());
    }

    [Fact]
    public void ReportIsDeterministicAndBudgetFailureKeepsEvidence()
    {
        Directory.CreateDirectory(_root);
        string artifact = Path.Combine(_root, "game.apk");
        CreatePackage(artifact, string.Empty);
        string first = Path.Combine(_root, "first.json");
        string second = Path.Combine(_root, "second.json");
        AndroidPackageSizeBudget budget = new(1, 1, 1, 1, 1);

        AndroidPackageSizeSnapshot firstSnapshot = AndroidPackageSizeReport.MeasureAndWrite(
            artifact,
            AndroidOutputMode.Apk,
            budget,
            first);
        AndroidPackageSizeSnapshot secondSnapshot = AndroidPackageSizeReport.MeasureAndWrite(
            artifact,
            AndroidOutputMode.Apk,
            budget,
            second);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        AndroidPackageSizeBudgetExceededException exception = Assert.Throws<AndroidPackageSizeBudgetExceededException>(
            () => firstSnapshot.Enforce(first));
        Assert.Equal(first, exception.ReportPath);
        Assert.True(File.Exists(first));
        Assert.False(Category(firstSnapshot, "managed").WithinBudget);
        Assert.False(Category(secondSnapshot, "shader").WithinBudget);
    }

    [Fact]
    public void MebibyteBudgetsRejectNegativeValuesAndConvertExactly()
    {
        AndroidPackageSizeBudget budget = AndroidPackageSizeBudget.FromMebibytes(1, 2, 3, 4, 5);

        Assert.Equal(1024L * 1024L, budget.MaxPackageBytes);
        Assert.Equal(5L * 1024L * 1024L, budget.MaxShaderBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AndroidPackageSizeBudget.FromMebibytes(-1, 0, 0, 0, 0));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static AndroidPackageSizeCategory Category(AndroidPackageSizeSnapshot snapshot, string id)
    {
        for (int i = 0; i < snapshot.Categories.Count; i++)
        {
            if (snapshot.Categories[i].Id == id)
                return snapshot.Categories[i];
        }
        throw new InvalidOperationException($"Missing package size category '{id}'.");
    }

    private static void CreatePackage(string path, string prefix)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, prefix + "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so", 101);
        Add(archive, prefix + "lib/arm64-v8a/libmonodroid.so", 102);
        Add(archive, prefix + "assets/xengine/Content/default/game.bytes", 103);
        Add(archive, prefix + "res/raw/android_vulkan.xshaderpack", 104);
        Add(archive, prefix + "manifest/AndroidManifest.xml", 105);
        Add(archive, prefix + "dex/classes.dex", 106);
    }

    private static void Add(ZipArchive archive, string path, int length)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        using Stream output = entry.Open();
        for (int i = 0; i < length; i++)
            output.WriteByte((byte)(i * 31));
    }
}
