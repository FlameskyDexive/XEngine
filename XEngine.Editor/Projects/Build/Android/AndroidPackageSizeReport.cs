// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidPackageSizeBudget(
    long MaxPackageBytes,
    long MaxManagedBytes,
    long MaxNativeBytes,
    long MaxAssetBytes,
    long MaxShaderBytes)
{
    internal static readonly AndroidPackageSizeBudget Disabled = new(0, 0, 0, 0, 0);

    internal bool IsEnabled =>
        MaxPackageBytes > 0 || MaxManagedBytes > 0 || MaxNativeBytes > 0 ||
        MaxAssetBytes > 0 || MaxShaderBytes > 0;

    internal static AndroidPackageSizeBudget FromMebibytes(
        int maxPackageMiB,
        int maxManagedMiB,
        int maxNativeMiB,
        int maxAssetMiB,
        int maxShaderMiB)
    {
        const long bytesPerMebibyte = 1024L * 1024L;
        if (maxPackageMiB < 0 || maxManagedMiB < 0 || maxNativeMiB < 0 ||
            maxAssetMiB < 0 || maxShaderMiB < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPackageMiB), "Android size budgets cannot be negative.");
        }
        return new AndroidPackageSizeBudget(
            checked(maxPackageMiB * bytesPerMebibyte),
            checked(maxManagedMiB * bytesPerMebibyte),
            checked(maxNativeMiB * bytesPerMebibyte),
            checked(maxAssetMiB * bytesPerMebibyte),
            checked(maxShaderMiB * bytesPerMebibyte));
    }

    internal void Validate()
    {
        if (MaxPackageBytes < 0 || MaxManagedBytes < 0 || MaxNativeBytes < 0 ||
            MaxAssetBytes < 0 || MaxShaderBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AndroidPackageSizeBudget), "Android size budgets cannot be negative.");
        }
    }
}

internal sealed record AndroidPackageSizeCategory(
    string Id,
    int EntryCount,
    long Bytes,
    long CompressedBytes,
    long MaxBytes,
    bool WithinBudget);

internal sealed class AndroidPackageSizeSnapshot
{
    internal AndroidPackageSizeSnapshot(
        long packageBytes,
        long maxPackageBytes,
        long archiveEntryCompressedBytes,
        long archiveOverheadBytes,
        IReadOnlyList<AndroidPackageSizeCategory> categories)
    {
        PackageBytes = packageBytes;
        MaxPackageBytes = maxPackageBytes;
        ArchiveEntryCompressedBytes = archiveEntryCompressedBytes;
        ArchiveOverheadBytes = archiveOverheadBytes;
        Categories = categories;
    }

    internal long PackageBytes { get; }
    internal long MaxPackageBytes { get; }
    internal long ArchiveEntryCompressedBytes { get; }
    internal long ArchiveOverheadBytes { get; }
    internal IReadOnlyList<AndroidPackageSizeCategory> Categories { get; }

    internal void Enforce(string reportPath)
    {
        var failures = new List<string>();
        if (MaxPackageBytes > 0 && PackageBytes > MaxPackageBytes)
            failures.Add($"package {PackageBytes} > {MaxPackageBytes}");
        for (int i = 0; i < Categories.Count; i++)
        {
            AndroidPackageSizeCategory category = Categories[i];
            if (!category.WithinBudget)
                failures.Add($"{category.Id} {category.CompressedBytes} > {category.MaxBytes}");
        }
        if (failures.Count > 0)
        {
            throw new AndroidPackageSizeBudgetExceededException(
                "Android package size budget exceeded: " + string.Join("; ", failures) +
                $". Report: {reportPath}",
                reportPath);
        }
    }
}

internal sealed class AndroidPackageSizeBudgetExceededException(string message, string reportPath)
    : IOException(message)
{
    internal string ReportPath { get; } = reportPath;
}

internal static class AndroidPackageSizeReport
{
    internal const int SchemaVersion = 1;
    internal const string FileName = "android-package-size.json";

    private static readonly string[] s_categoryOrder = ["managed", "native", "assets", "shader", "other"];

    internal static AndroidPackageSizeSnapshot MeasureAndWrite(
        string artifactPath,
        AndroidOutputMode outputMode,
        AndroidPackageSizeBudget budget,
        string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();
        if (outputMode == AndroidOutputMode.ExportProject)
            throw new ArgumentException("ExportProject has no package to measure.", nameof(outputMode));
        if (!File.Exists(artifactPath))
            throw new FileNotFoundException("Android package size input is missing.", artifactPath);

        var builders = new Dictionary<string, CategoryBuilder>(StringComparer.Ordinal);
        for (int i = 0; i < s_categoryOrder.Length; i++)
            builders.Add(s_categoryOrder[i], new CategoryBuilder(s_categoryOrder[i]));
        var largestEntries = new List<PackageEntry>();
        long compressedBytes = 0;

        using (ZipArchive archive = ZipFile.OpenRead(artifactPath))
        {
            for (int i = 0; i < archive.Entries.Count; i++)
            {
                ZipArchiveEntry entry = archive.Entries[i];
                if (entry.Name.Length == 0)
                    continue;
                string path = entry.FullName.Replace('\\', '/');
                string category = Classify(path, outputMode);
                builders[category].Add(entry.Length, entry.CompressedLength);
                compressedBytes = checked(compressedBytes + entry.CompressedLength);
                largestEntries.Add(new PackageEntry(path, category, entry.Length, entry.CompressedLength));
            }
        }

        largestEntries.Sort(static (left, right) =>
        {
            int compressed = right.CompressedBytes.CompareTo(left.CompressedBytes);
            return compressed != 0 ? compressed : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });
        int largestCount = Math.Min(20, largestEntries.Count);
        var largest = new PackageEntry[largestCount];
        for (int i = 0; i < largestCount; i++)
            largest[i] = largestEntries[i];

        long packageBytes = new FileInfo(artifactPath).Length;
        long archiveOverheadBytes = Math.Max(0, packageBytes - compressedBytes);
        var categories = new AndroidPackageSizeCategory[s_categoryOrder.Length];
        for (int i = 0; i < s_categoryOrder.Length; i++)
        {
            CategoryBuilder builder = builders[s_categoryOrder[i]];
            long maxBytes = GetCategoryBudget(builder.Id, budget);
            categories[i] = new AndroidPackageSizeCategory(
                builder.Id,
                builder.EntryCount,
                builder.Bytes,
                builder.CompressedBytes,
                maxBytes,
                maxBytes == 0 || builder.CompressedBytes <= maxBytes);
        }

        var snapshot = new AndroidPackageSizeSnapshot(
            packageBytes,
            budget.MaxPackageBytes,
            compressedBytes,
            archiveOverheadBytes,
            Array.AsReadOnly(categories));
        Write(reportPath, Path.GetFileName(artifactPath), outputMode, budget, snapshot, largest);
        return snapshot;
    }

    private static void Write(
        string reportPath,
        string artifactName,
        AndroidOutputMode outputMode,
        AndroidPackageSizeBudget budget,
        AndroidPackageSizeSnapshot snapshot,
        IReadOnlyList<PackageEntry> largestEntries)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (directory != null)
            Directory.CreateDirectory(directory);
        var payload = new
        {
            schemaVersion = SchemaVersion,
            artifact = artifactName,
            outputMode = outputMode.ToString(),
            budgetEnabled = budget.IsEnabled,
            package = new
            {
                bytes = snapshot.PackageBytes,
                maxBytes = snapshot.MaxPackageBytes,
                withinBudget = snapshot.MaxPackageBytes == 0 || snapshot.PackageBytes <= snapshot.MaxPackageBytes,
                archiveEntryCompressedBytes = snapshot.ArchiveEntryCompressedBytes,
                archiveOverheadBytes = snapshot.ArchiveOverheadBytes,
            },
            categories = snapshot.Categories,
            largestEntries,
        };
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static string Classify(string path, AndroidOutputMode outputMode)
    {
        string packagePath = outputMode == AndroidOutputMode.Aab && path.StartsWith("base/", StringComparison.Ordinal)
            ? path[5..]
            : path;
        if (packagePath.StartsWith("lib/", StringComparison.Ordinal) &&
            packagePath.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(packagePath);
            if (fileName.StartsWith("libassemblies.", StringComparison.Ordinal) &&
                fileName.EndsWith(".blob.so", StringComparison.Ordinal))
            {
                return "managed";
            }
            return "native";
        }
        if (packagePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return "managed";
        if (packagePath.StartsWith("assets/xengine/", StringComparison.Ordinal))
            return "assets";
        if (string.Equals(packagePath, "res/raw/android_vulkan.xshaderpack", StringComparison.Ordinal))
            return "shader";
        return "other";
    }

    private static long GetCategoryBudget(string category, AndroidPackageSizeBudget budget) => category switch
    {
        "managed" => budget.MaxManagedBytes,
        "native" => budget.MaxNativeBytes,
        "assets" => budget.MaxAssetBytes,
        "shader" => budget.MaxShaderBytes,
        _ => 0,
    };

    private sealed class CategoryBuilder(string id)
    {
        internal string Id { get; } = id;
        internal int EntryCount { get; private set; }
        internal long Bytes { get; private set; }
        internal long CompressedBytes { get; private set; }

        internal void Add(long bytes, long compressedBytes)
        {
            EntryCount++;
            Bytes = checked(Bytes + bytes);
            CompressedBytes = checked(CompressedBytes + compressedBytes);
        }
    }

    private sealed record PackageEntry(string Path, string Category, long Bytes, long CompressedBytes);
}
