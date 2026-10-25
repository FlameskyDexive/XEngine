// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSNativeHostSourceTests : IDisposable
{
    private static readonly string[] s_fileNames =
    [
        "CMakeLists.txt",
        "xengine_host_napi.cpp",
        "xengine_player_abi.h",
    ];

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSNativeHostSourceTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MaterializeAsync_WritesExactRuntimeNativeSources()
    {
        string output = Path.Combine(_testRoot, "native");

        await HarmonyOSNativeHostSource.MaterializeAsync(output);

        Assert.Equal(s_fileNames, Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        string sourceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "XEngine.Runtime",
            "Platforms",
            "HarmonyOS",
            "Native");
        foreach (string fileName in s_fileNames)
        {
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(sourceDirectory, fileName)),
                await File.ReadAllBytesAsync(Path.Combine(output, fileName)));
        }
    }

    [Fact]
    public async Task MaterializeAsync_RejectsNonEmptyOutputDirectory()
    {
        string output = Path.Combine(_testRoot, "native");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "foreign.txt"), "foreign");

        await Assert.ThrowsAsync<IOException>(() => HarmonyOSNativeHostSource.MaterializeAsync(output));

        Assert.Equal("foreign", await File.ReadAllTextAsync(Path.Combine(output, "foreign.txt")));
    }

    [Fact]
    public async Task MaterializeAsync_EmitsHilogReadinessContract()
    {
        string output = Path.Combine(_testRoot, "native");

        await HarmonyOSNativeHostSource.MaterializeAsync(output);

        string source = await File.ReadAllTextAsync(Path.Combine(output, "xengine_host_napi.cpp"));
        string cmake = await File.ReadAllTextAsync(Path.Combine(output, "CMakeLists.txt"));
        Assert.Contains("#include <hilog/log.h>", source, StringComparison.Ordinal);
        Assert.Contains("XENGINE_HARMONYOS_READY", source, StringComparison.Ordinal);
        Assert.Contains("OH_LOG_Print", source, StringComparison.Ordinal);
        Assert.Contains("libhilog_ndk.z.so", cmake, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializeAsync_EmitsPlayerWorkerThreadContract()
    {
        string output = Path.Combine(_testRoot, "native");

        await HarmonyOSNativeHostSource.MaterializeAsync(output);

        string source = await File.ReadAllTextAsync(Path.Combine(output, "xengine_host_napi.cpp"));
        string cmake = await File.ReadAllTextAsync(Path.Combine(output, "CMakeLists.txt"));
        Assert.Contains("std::thread worker", source, StringComparison.Ordinal);
        Assert.Contains("std::condition_variable worker_signal", source, StringComparison.Ordinal);
        Assert.Contains("napi_add_env_cleanup_hook", source, StringComparison.Ordinal);
        Assert.Contains("Threads::Threads", cmake, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
