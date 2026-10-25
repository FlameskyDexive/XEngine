// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XEngine.Editor.Build.Web;

internal sealed class WebPackageSizeBudgetExceededException(string message, string reportPath)
    : IOException(message)
{
    internal string ReportPath { get; } = reportPath;
}

internal sealed class WebPackageSizeSnapshot
{
    internal WebPackageSizeSnapshot(
        long frameworkUniqueBytes,
        long frameworkBrotliBytes,
        long weChatMainBytes,
        long weChatSubpackageBytes,
        long douyinMainBytes,
        long douyinSubpackageBytes,
        long browserWwwBytes,
        bool containsMagick,
        bool containsVulkan,
        bool containsSlang)
    {
        FrameworkUniqueBytes = frameworkUniqueBytes;
        FrameworkBrotliBytes = frameworkBrotliBytes;
        WeChatMainBytes = weChatMainBytes;
        WeChatSubpackageBytes = weChatSubpackageBytes;
        DouyinMainBytes = douyinMainBytes;
        DouyinSubpackageBytes = douyinSubpackageBytes;
        BrowserWwwBytes = browserWwwBytes;
        ContainsMagick = containsMagick;
        ContainsVulkan = containsVulkan;
        ContainsSlang = containsSlang;
    }

    internal long FrameworkUniqueBytes { get; }
    internal long FrameworkBrotliBytes { get; }
    internal long WeChatMainBytes { get; }
    internal long WeChatSubpackageBytes { get; }
    internal long DouyinMainBytes { get; }
    internal long DouyinSubpackageBytes { get; }
    internal long BrowserWwwBytes { get; }
    internal bool ContainsMagick { get; }
    internal bool ContainsVulkan { get; }
    internal bool ContainsSlang { get; }

    internal bool ContainsForbiddenDesktopPackages =>
        ContainsMagick || ContainsVulkan || ContainsSlang;

    internal bool WeChatMainWithinBudget =>
        WeChatMainBytes <= WebPackageSizeReport.WeChatMainPackageLimitBytes;

    internal bool WeChatTotalWithinDefaultBudget =>
        WeChatMainBytes + WeChatSubpackageBytes <= WebPackageSizeReport.WeChatTotalPackageLimitBytes;

    internal bool WithinBudget =>
        !ContainsForbiddenDesktopPackages && WeChatMainWithinBudget && WeChatTotalWithinDefaultBudget;

    internal void Enforce(string reportPath)
    {
        var failures = new List<string>();
        if (ContainsMagick)
            failures.Add("payload contains Magick");
        if (ContainsVulkan)
            failures.Add("payload contains Vulkan");
        if (ContainsSlang)
            failures.Add("payload contains Slang");
        if (!WeChatMainWithinBudget)
        {
            failures.Add(
                "weChatMain " + WeChatMainBytes + " > " +
                WebPackageSizeReport.WeChatMainPackageLimitBytes);
        }

        if (!WeChatTotalWithinDefaultBudget)
        {
            failures.Add(
                "weChatTotal " + (WeChatMainBytes + WeChatSubpackageBytes) + " > " +
                WebPackageSizeReport.WeChatTotalPackageLimitBytes);
        }

        if (failures.Count == 0)
            return;

        throw new WebPackageSizeBudgetExceededException(
            "Web package size budget exceeded: " + string.Join("; ", failures) +
            ". Report: " + reportPath,
            reportPath);
    }
}

/// <summary>
/// Writes <c>verification/web-package-size.json</c>. Official WeChat limits are 4 MiB main /
/// 20 MiB total (30 MiB only with virtual pay). Framework brotli is not a WeChat 4M pass.
/// </summary>
internal static class WebPackageSizeReport
{
    internal const int SchemaVersion = 2;
    internal const string FileName = "web-package-size.json";
    internal const long WeChatMainPackageLimitBytes = 4L * 1024 * 1024;
    internal const long WeChatTotalPackageLimitBytes = 20L * 1024 * 1024;
    internal const long WeChatTotalWithVirtualPayBytes = 30L * 1024 * 1024;
    private const int LargestEntryCount = 20;

    internal static WebPackageSizeSnapshot MeasureAndWrite(string exportRoot, string frameworkDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkDirectory);
        if (!Directory.Exists(frameworkDirectory))
            throw new DirectoryNotFoundException("Published _framework was not found at " + frameworkDirectory);

        var largest = new List<FileEntry>(64);
        Bucket framework = MeasureDirectory(frameworkDirectory, exportRoot, "framework", largest, skipStableDotnetJsAlias: true);
        Bucket weChatMain = MeasureMiniGameMain(Path.Combine(exportRoot, "wechat"), exportRoot, "weChatMain", largest);
        Bucket weChatCdn = MeasureMiniGameSubpackages(
            Path.Combine(exportRoot, "wechat"),
            exportRoot,
            "weChatCdn",
            largest);
        Bucket douyinMain = MeasureMiniGameMain(Path.Combine(exportRoot, "douyin"), exportRoot, "douyinMain", largest);
        Bucket douyinCdn = MeasureDirectory(
            Path.Combine(exportRoot, "douyin", "cdn"),
            exportRoot,
            "douyinCdn",
            largest,
            skipStableDotnetJsAlias: false);
        string wwwRoot = Path.Combine(exportRoot, "publish", "wwwroot");
        Bucket browser = MeasureDirectory(wwwRoot, exportRoot, "browserWww", largest, skipStableDotnetJsAlias: true);

        largest.Sort(static (left, right) =>
        {
            int size = right.Bytes.CompareTo(left.Bytes);
            return size != 0 ? size : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });
        int take = Math.Min(LargestEntryCount, largest.Count);
        var top = new FileEntry[take];
        for (int i = 0; i < take; i++)
            top[i] = largest[i];

        var snapshot = new WebPackageSizeSnapshot(
            framework.UniqueBytes,
            framework.BrotliBytes,
            weChatMain.UniqueBytes + weChatMain.BrotliBytes,
            weChatCdn.UniqueBytes + weChatCdn.BrotliBytes,
            douyinMain.UniqueBytes + douyinMain.BrotliBytes,
            douyinCdn.UniqueBytes + douyinCdn.BrotliBytes,
            browser.UniqueBytes,
            framework.Magick | weChatMain.Magick | weChatCdn.Magick | douyinMain.Magick | douyinCdn.Magick | browser.Magick,
            framework.Vulkan | weChatMain.Vulkan | weChatCdn.Vulkan | douyinMain.Vulkan | douyinCdn.Vulkan | browser.Vulkan,
            framework.Slang | weChatMain.Slang | weChatCdn.Slang | douyinMain.Slang | douyinCdn.Slang | browser.Slang);

        string reportPath = Path.Combine(exportRoot, "verification", FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = SchemaVersion,
                    frameworkRelativePath = "_framework",
                    uniqueBytes = snapshot.FrameworkUniqueBytes,
                    brotliBytes = snapshot.FrameworkBrotliBytes,
                    buckets = new
                    {
                        frameworkUnique = snapshot.FrameworkUniqueBytes,
                        frameworkBrotli = snapshot.FrameworkBrotliBytes,
                        weChatMain = snapshot.WeChatMainBytes,
                        weChatCdn = snapshot.WeChatSubpackageBytes,
                        douyinMain = snapshot.DouyinMainBytes,
                        douyinCdn = snapshot.DouyinSubpackageBytes,
                        browserWwwUnique = snapshot.BrowserWwwBytes,
                    },
                    largestEntries = top,
                    containsMagick = snapshot.ContainsMagick,
                    containsVulkan = snapshot.ContainsVulkan,
                    containsSlang = snapshot.ContainsSlang,
                    weChatMainPackageLimitBytes = WeChatMainPackageLimitBytes,
                    weChatTotalPackageLimitBytes = WeChatTotalPackageLimitBytes,
                    weChatTotalPackageLimitWithVirtualPayBytes = WeChatTotalWithVirtualPayBytes,
                    weChatMainWithinBudget = snapshot.WeChatMainWithinBudget,
                    weChatTotalWithinDefaultBudget = snapshot.WeChatTotalWithinDefaultBudget,
                    withinBudget = snapshot.WithinBudget,
                    note = "Brotli of _framework is not a WeChat 4M pass. Official main is 4 MiB; total is 20 MiB (30 MiB only with virtual pay). Runtime wasm cannot go on CDN. weChatCdn includes cdn/ plus code subpackages (dotnet*).",
                },
                new JsonSerializerOptions { WriteIndented = true }));
        return snapshot;
    }

    private static Bucket MeasureMiniGameMain(
        string miniGameDirectory,
        string exportRoot,
        string bucket,
        List<FileEntry> largest)
    {
        if (!Directory.Exists(miniGameDirectory))
            return default;
        var result = new Bucket();
        foreach (string file in Directory.EnumerateFiles(miniGameDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(miniGameDirectory, file).Replace('\\', '/');
            if (IsMiniGameSubpackageRelative(relative))
                continue;

            AddFile(file, exportRoot, bucket, ref result, largest);
        }

        return result;
    }

    internal static bool IsMiniGameSubpackageRelative(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        relative = relative.Replace('\\', '/');
        if (relative.StartsWith("cdn/", StringComparison.Ordinal) ||
            string.Equals(relative, "cdn", StringComparison.Ordinal))
        {
            return true;
        }

        if (!relative.StartsWith("dotnet", StringComparison.Ordinal))
            return false;
        if (relative.Length == 6)
            return true;
        char next = relative[6];
        return next == '/' || (next >= '0' && next <= '9');
    }

    private static Bucket MeasureMiniGameSubpackages(
        string miniGameDirectory,
        string exportRoot,
        string bucket,
        List<FileEntry> largest)
    {
        if (!Directory.Exists(miniGameDirectory))
            return default;

        var result = new Bucket();
        foreach (string file in Directory.EnumerateFiles(miniGameDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(miniGameDirectory, file).Replace('\\', '/');
            if (!IsMiniGameSubpackageRelative(relative))
                continue;
            AddFile(file, exportRoot, bucket, ref result, largest);
        }

        return result;
    }

    private static Bucket MeasureDirectory(
        string directory,
        string exportRoot,
        string bucket,
        List<FileEntry> largest,
        bool skipStableDotnetJsAlias)
    {
        if (!Directory.Exists(directory))
            return default;
        var result = new Bucket();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (skipStableDotnetJsAlias &&
                string.Equals(Path.GetFileName(file), "dotnet.js", StringComparison.Ordinal))
            {
                continue;
            }

            AddFile(file, exportRoot, bucket, ref result, largest);
        }

        return result;
    }

    private static void AddFile(
        string file,
        string exportRoot,
        string bucket,
        ref Bucket result,
        List<FileEntry> largest)
    {
        string name = Path.GetFileName(file);
        if (name is ".keep" or ".gitkeep")
            return;

        long length = new FileInfo(file).Length;
        string relative = Path.GetRelativePath(exportRoot, file).Replace('\\', '/');
        if (name.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
            result.BrotliBytes += length;
        else if (!name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            result.UniqueBytes += length;

        result.Magick |= name.Contains("Magick", StringComparison.OrdinalIgnoreCase);
        result.Vulkan |= name.Contains("Vulkan", StringComparison.OrdinalIgnoreCase);
        result.Slang |= name.Contains("Slang", StringComparison.OrdinalIgnoreCase);
        largest.Add(new FileEntry(relative, bucket, length));
    }

    private struct Bucket
    {
        internal long UniqueBytes;
        internal long BrotliBytes;
        internal bool Magick;
        internal bool Vulkan;
        internal bool Slang;
    }

    private sealed record FileEntry(string Path, string Bucket, long Bytes);
}
