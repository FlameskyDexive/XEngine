// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;
using XEngine.Editor.Projects.Build;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSPackagingContractTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSPackagingContractTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadAsync_ParsesLockedAdapterTargetsAndNormalizesDependencies()
    {
        await WriteContractAsync(
            "outputs/Smoke.hap",
            "outputs/Smoke.app",
            "[\"libnative_window.so\",\"libxengine_player.so\",\"libace_napi.z.so\"]");

        HarmonyOSPackagingContract contract = await HarmonyOSPackagingContract.ReadAsync(_testRoot);

        Assert.Equal("XEngineBuildHap", contract.GetAction(HarmonyOSOutputMode.Hap).BuildTarget);
        Assert.Equal("XEngineVerifyApp", contract.GetAction(HarmonyOSOutputMode.App).VerifyTarget);
        Assert.Equal("outputs/Smoke.hap", contract.Hap.ArtifactRelativePath);
        Assert.Equal(
            ["libace_napi.z.so", "libnative_window.so", "libxengine_player.so"],
            contract.AllowedElfDependencies);
    }

    [Theory]
    [InlineData("../Smoke.hap")]
    [InlineData("outputs\\Smoke.hap")]
    [InlineData("outputs/Smoke.app")]
    public async Task ReadAsync_RejectsUnsafeOrMismatchedHapArtifact(string artifact)
    {
        await WriteContractAsync(artifact, "outputs/Smoke.app", "[\"libc.so\"]");

        await Assert.ThrowsAsync<InvalidDataException>(() => HarmonyOSPackagingContract.ReadAsync(_testRoot));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"libc.so\",\"libc.so\"]")]
    [InlineData("[\"../libc.so\"]")]
    public async Task ReadAsync_RejectsInvalidDependencyAllowlist(string dependencies)
    {
        await WriteContractAsync("outputs/Smoke.hap", "outputs/Smoke.app", dependencies);

        await Assert.ThrowsAsync<InvalidDataException>(() => HarmonyOSPackagingContract.ReadAsync(_testRoot));
    }

    [Fact]
    public async Task ReadAsync_RejectsCommandInjectionInTarget()
    {
        Directory.CreateDirectory(_testRoot);
        await File.WriteAllTextAsync(
            Path.Combine(_testRoot, HarmonyOSPackagingContract.RelativePath),
            CreateContract("outputs/Smoke.hap", "outputs/Smoke.app", "[\"libc.so\"]")
                .Replace("XEngineBuildHap", "XEngineBuildHap;Remove-Item", StringComparison.Ordinal),
            new UTF8Encoding(false));

        await Assert.ThrowsAsync<InvalidDataException>(() => HarmonyOSPackagingContract.ReadAsync(_testRoot));
    }

    [Fact]
    public async Task RequireNativeHostDependencies_RejectsIncompleteProviderAllowlist()
    {
        await WriteContractAsync(
            "outputs/Smoke.hap",
            "outputs/Smoke.app",
            "[\"libace_napi.z.so\",\"libnative_window.so\",\"libxengine_player.so\"]");
        HarmonyOSPackagingContract contract = await HarmonyOSPackagingContract.ReadAsync(_testRoot);

        InvalidDataException error = Assert.Throws<InvalidDataException>(contract.RequireNativeHostDependencies);
        Assert.Contains("libace_ndk.z.so", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireNativeHostDependencies_AcceptsNativeHostClosure()
    {
        await WriteContractAsync(
            "outputs/Smoke.hap",
            "outputs/Smoke.app",
            "[\"libace_napi.z.so\",\"libace_ndk.z.so\",\"libhilog_ndk.z.so\",\"libnative_window.so\",\"libvulkan.so\",\"libxengine_player.so\"]");
        HarmonyOSPackagingContract contract = await HarmonyOSPackagingContract.ReadAsync(_testRoot);

        contract.RequireNativeHostDependencies();
    }

    [Fact]
    public async Task RequireNativeHostDependencies_AcceptsCppRuntimeNameWithPlusSign()
    {
        // libc++_shared.so (the C++ runtime the NativeAOT player links) contains a '+', which the
        // name validator must permit so the provider contract can declare it as an allowed ELF dep.
        await WriteContractAsync(
            "outputs/Smoke.hap",
            "outputs/Smoke.app",
            "[\"libace_napi.z.so\",\"libace_ndk.z.so\",\"libhilog_ndk.z.so\",\"libnative_window.so\",\"libvulkan.so\",\"libxengine_player.so\",\"libc++_shared.so\"]");
        HarmonyOSPackagingContract contract = await HarmonyOSPackagingContract.ReadAsync(_testRoot);

        contract.RequireNativeHostDependencies();
        Assert.Contains("libc++_shared.so", contract.AllowedElfDependencies);
    }

    [Fact]
    public async Task RequireNativeHostDependencies_RequiresHilogNativeLibrary()
    {
        await WriteContractAsync(
            "outputs/Smoke.hap",
            "outputs/Smoke.app",
            "[\"libace_napi.z.so\",\"libace_ndk.z.so\",\"libnative_window.so\",\"libvulkan.so\",\"libxengine_player.so\"]");
        HarmonyOSPackagingContract contract = await HarmonyOSPackagingContract.ReadAsync(_testRoot);

        InvalidDataException error = Assert.Throws<InvalidDataException>(contract.RequireNativeHostDependencies);
        Assert.Contains("libhilog_ndk.z.so", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private async Task WriteContractAsync(string hapArtifact, string appArtifact, string dependencies)
    {
        Directory.CreateDirectory(_testRoot);
        await File.WriteAllTextAsync(
            Path.Combine(_testRoot, HarmonyOSPackagingContract.RelativePath),
            CreateContract(hapArtifact, appArtifact, dependencies),
            new UTF8Encoding(false));
    }

    private static string CreateContract(string hapArtifact, string appArtifact, string dependencies) => $$"""
        {
          "schemaVersion": 1,
          "hap": {
            "buildTarget": "XEngineBuildHap",
            "verifyTarget": "XEngineVerifyHap",
            "artifact": "{{hapArtifact}}"
          },
          "app": {
            "buildTarget": "XEngineBuildApp",
            "verifyTarget": "XEngineVerifyApp",
            "artifact": "{{appArtifact}}"
          },
          "allowedElfDependencies": {{dependencies}}
        }
        """;
}
