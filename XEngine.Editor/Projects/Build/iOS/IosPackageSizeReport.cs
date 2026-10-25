// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XEngine.Editor.Build.Ios;

internal sealed record IosPackageSizeBudget(
    long MaxPackageBytes,
    long MaxManagedBytes,
    long MaxNativeBytes,
    long MaxAssetBytes,
    long MaxShaderBytes)
{
    internal static readonly IosPackageSizeBudget Disabled = new(0, 0, 0, 0, 0);

    internal bool IsEnabled =>
        MaxPackageBytes > 0 || MaxManagedBytes > 0 || MaxNativeBytes > 0 ||
        MaxAssetBytes > 0 || MaxShaderBytes > 0;

    internal static IosPackageSizeBudget FromMebibytes(
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
            throw new ArgumentOutOfRangeException(nameof(maxPackageMiB), "iOS size budgets cannot be negative.");
        }
        return new IosPackageSizeBudget(
            checked(maxPackageMiB * bytesPerMebibyte),
            checked(maxManagedMiB * bytesPerMebibyte),
            checked(maxNativeMiB * bytesPerMebibyte),
            checked(maxAssetMiB * bytesPerMebibyte),
            checked(maxShaderMiB * bytesPerMebibyte));
    }
}

internal sealed record IosPackageSizeCategory(
    string Id,
    int EntryCount,
    long Bytes,
    long MaxBytes,
    bool WithinBudget);

internal sealed class IosPackageSizeSnapshot
{
    internal IosPackageSizeSnapshot(
        long packageBytes,
        long maxPackageBytes,
        IReadOnlyList<IosPackageSizeCategory> categories)
    {
        PackageBytes = packageBytes;
        MaxPackageBytes = maxPackageBytes;
        Categories = categories;
    }

    internal long PackageBytes { get; }
    internal long MaxPackageBytes { get; }
    internal IReadOnlyList<IosPackageSizeCategory> Categories { get; }

    internal void Enforce(string reportPath)
    {
        var failures = new List<string>();
        if (MaxPackageBytes > 0 && PackageBytes > MaxPackageBytes)
            failures.Add($"package {PackageBytes} > {MaxPackageBytes}");
        for (int i = 0; i < Categories.Count; i++)
        {
            IosPackageSizeCategory category = Categories[i];
            if (!category.WithinBudget)
                failures.Add($"{category.Id} {category.Bytes} > {category.MaxBytes}");
        }
        if (failures.Count > 0)
        {
            throw new IosPackageSizeBudgetExceededException(
                "iOS package size budget exceeded: " + string.Join("; ", failures) +
                ". Report: " + reportPath,
                reportPath);
        }
    }
}

internal sealed class IosPackageSizeBudgetExceededException(string message, string reportPath)
    : IOException(message)
{
    internal string ReportPath { get; } = reportPath;
}

/// <summary>
/// Measures the generated Player export layout before signed-app publish.
/// Export sizes do not establish the size or signing state of a published app or IPA.
/// </summary>
internal static class IosPackageSizeReport
{
    internal const int SchemaVersion = 1;
    internal const string FileName = "ios-package-size.json";

    private static readonly string[] s_categoryOrder = ["managed", "native", "assets", "shader", "other"];

    internal static IosPackageSizeSnapshot MeasureAndWrite(
        string exportRoot,
        IosPackageSizeBudget budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        ArgumentNullException.ThrowIfNull(budget);

        string playerRoot = Path.Combine(exportRoot, "Player");
        if (!Directory.Exists(playerRoot))
            throw new DirectoryNotFoundException("iOS package size input is missing: " + playerRoot);

        var builders = new Dictionary<string, CategoryBuilder>(StringComparer.Ordinal);
        for (int i = 0; i < s_categoryOrder.Length; i++)
            builders.Add(s_categoryOrder[i], new CategoryBuilder(s_categoryOrder[i]));

        long packageBytes = 0;
        foreach (string file in Directory.EnumerateFiles(playerRoot, "*", SearchOption.AllDirectories))
        {
            long length = new FileInfo(file).Length;
            packageBytes = checked(packageBytes + length);
            string relative = Path.GetRelativePath(playerRoot, file).Replace('\\', '/');
            builders[Classify(relative)].Add(length);
        }

        var categories = new IosPackageSizeCategory[s_categoryOrder.Length];
        for (int i = 0; i < s_categoryOrder.Length; i++)
        {
            CategoryBuilder builder = builders[s_categoryOrder[i]];
            long maxBytes = GetCategoryBudget(builder.Id, budget);
            categories[i] = new IosPackageSizeCategory(
                builder.Id,
                builder.EntryCount,
                builder.Bytes,
                maxBytes,
                maxBytes == 0 || builder.Bytes <= maxBytes);
        }

        var snapshot = new IosPackageSizeSnapshot(packageBytes, budget.MaxPackageBytes, categories);
        string reportPath = Path.Combine(exportRoot, "verification", FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = SchemaVersion,
                    measuredLayout = "Player/",
                    signedApp = false,
                    hostCanMeasureApp = OperatingSystem.IsMacOS(),
                    packageBytes,
                    maxPackageBytes = budget.MaxPackageBytes,
                    withinBudget = budget.MaxPackageBytes == 0 || packageBytes <= budget.MaxPackageBytes,
                    categories,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        return snapshot;
    }

    private static long GetCategoryBudget(string id, IosPackageSizeBudget budget) => id switch
    {
        "managed" => budget.MaxManagedBytes,
        "native" => budget.MaxNativeBytes,
        "assets" => budget.MaxAssetBytes,
        "shader" => budget.MaxShaderBytes,
        _ => 0,
    };

    private static string Classify(string relativePath)
    {
        if (relativePath.StartsWith("Managed/", StringComparison.OrdinalIgnoreCase))
            return "managed";
        if (relativePath.StartsWith("NativeLibraries/", StringComparison.OrdinalIgnoreCase))
            return "native";
        if (relativePath.StartsWith("Content/Shaders/", StringComparison.OrdinalIgnoreCase))
            return "shader";
        if (relativePath.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
            return "assets";
        return "other";
    }

    private sealed class CategoryBuilder(string id)
    {
        internal string Id { get; } = id;
        internal int EntryCount { get; private set; }
        internal long Bytes { get; private set; }

        internal void Add(long bytes)
        {
            EntryCount++;
            Bytes = checked(Bytes + bytes);
        }
    }
}
