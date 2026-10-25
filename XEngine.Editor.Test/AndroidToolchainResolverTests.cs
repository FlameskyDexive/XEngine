// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

using XEngine.Editor.Build.Android;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidToolchainResolverTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineAndroidResolverTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ToolchainFingerprintIgnoresNondeterministicDryRunArtifactTelemetry()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        AndroidToolchainLock first = CreateFingerprintLock(
            targetApi: 36,
            durationMilliseconds: 12_000,
            artifactBytes: 20_000_001,
            artifactSha256: new string('A', 64));
        AndroidToolchainLock second = CreateFingerprintLock(
            targetApi: 36,
            durationMilliseconds: 45_000,
            artifactBytes: 20_000_003,
            artifactSha256: new string('B', 64));
        AndroidToolchainLock differentTarget = CreateFingerprintLock(
            targetApi: 35,
            durationMilliseconds: 12_000,
            artifactBytes: 20_000_001,
            artifactSha256: new string('A', 64));

        Assert.Equal(
            AndroidToolchainLockFile.ComputeFingerprint(first),
            AndroidToolchainLockFile.ComputeFingerprint(second));
        Assert.NotEqual(
            AndroidToolchainLockFile.ComputeFingerprint(first),
            AndroidToolchainLockFile.ComputeFingerprint(differentTarget));
    }

    [Fact]
    public async Task ResolverUsesFixedPrecedenceWritesValidatedLockAndDoesNotReadUnitySeed()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        TestToolchain toolchain = await CreateImportedToolchainAsync();
        string cliJdk = CreateJdk("cli-jdk", "17.0.20", "java-cli");
        string preferredJdk = CreateJdk("preferred-jdk", "17.0.19", "java-preference");
        var runner = new FakeProcessRunner(toolchain.WorkloadManifestPath, "17.0.20");
        string lockPath = Path.Combine(_testRoot, "evidence", AndroidToolchainLockFile.DefaultFileName);

        Directory.Delete(toolchain.SeedRoot, recursive: true);
        AndroidToolchainSnapshot snapshot = await new AndroidToolchainResolver(runner).ResolveAsync(
            new AndroidToolchainResolveOptions
            {
                CommandLineOverrides = new AndroidToolchainPathOverrides
                {
                    DotNetRoot = toolchain.DotNetRoot,
                    JavaSdkRoot = cliJdk,
                },
                UserPreferences = new AndroidToolchainPathOverrides
                {
                    JavaSdkRoot = preferredJdk,
                },
                ImportedToolchainRoot = toolchain.ImportedRoot,
                ToolchainCacheRoot = toolchain.CacheRoot,
                LockFilePath = lockPath,
                TemporaryRoot = Path.Combine(_testRoot, "probes"),
            });

        Assert.True(snapshot.DryRun.Succeeded);
        Assert.True(File.Exists(lockPath));
        AndroidToolchainComponentLock java = FindComponent(snapshot, "java");
        AndroidToolchainComponentLock sdk = FindComponent(snapshot, "aapt2");
        Assert.Equal(AndroidToolchainSourceKind.CommandLineOverride, java.Source);
        Assert.StartsWith(cliJdk, java.ResolvedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AndroidToolchainSourceKind.UnityImport, sdk.Source);
        Assert.StartsWith(toolchain.ImportedRoot, sdk.ResolvedPath, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<ReadOnlyCollection<AndroidToolchainComponentLock>>(snapshot.Components);

        FakeProcessCall sdkmanager = runner.FindCall("sdkmanager.bat");
        Assert.Contains(sdkmanager.Arguments, static value => value.StartsWith("--sdk_root=", StringComparison.Ordinal));
        Assert.DoesNotContain(sdkmanager.Arguments, value => value.Contains(toolchain.ImportedRoot, StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(toolchain.ImportedRoot, "SDK", ".knownPackages")));

        AndroidToolchainSnapshot reloaded = await AndroidToolchainLockFile.ReadAndValidateAsync(lockPath);
        Assert.Equal(snapshot.Fingerprint, reloaded.Fingerprint);
        Assert.Equal(snapshot.DotNetExecutable, reloaded.DotNetExecutable);
    }

    [Fact]
    public async Task ResolverRejectsExplicitInvalidOverrideInsteadOfFallingBack()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        TestToolchain toolchain = await CreateImportedToolchainAsync();
        var runner = new FakeProcessRunner(toolchain.WorkloadManifestPath, "17.0.20");

        DirectoryNotFoundException exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new AndroidToolchainResolver(runner).ResolveAsync(new AndroidToolchainResolveOptions
            {
                CommandLineOverrides = new AndroidToolchainPathOverrides
                {
                    DotNetRoot = toolchain.DotNetRoot,
                    JavaSdkRoot = Path.Combine(_testRoot, "missing-jdk"),
                },
                ImportedToolchainRoot = toolchain.ImportedRoot,
                ToolchainCacheRoot = toolchain.CacheRoot,
                RunDryRun = false,
                TemporaryRoot = Path.Combine(_testRoot, "probes"),
            }));

        Assert.Contains("Configured JDK path is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ResolverRejectsUnityJdk11WithActionableComponentOverrideMessage()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        TestToolchain toolchain = await CreateImportedToolchainAsync();
        var runner = new FakeProcessRunner(toolchain.WorkloadManifestPath, "11.0.14.1");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AndroidToolchainResolver(runner).ResolveAsync(new AndroidToolchainResolveOptions
            {
                CommandLineOverrides = new AndroidToolchainPathOverrides { DotNetRoot = toolchain.DotNetRoot },
                ImportedToolchainRoot = toolchain.ImportedRoot,
                ToolchainCacheRoot = toolchain.CacheRoot,
                RunDryRun = false,
                TemporaryRoot = Path.Combine(_testRoot, "probes"),
            }));

        Assert.Contains("requires JDK 17", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Override only the JDK component", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockValidationRejectsPostResolveCachePollution()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        TestToolchain toolchain = await CreateImportedToolchainAsync();
        string cliJdk = CreateJdk("cli-jdk-pollution", "17.0.20", "java-cli");
        var runner = new FakeProcessRunner(toolchain.WorkloadManifestPath, "17.0.20");
        string lockPath = Path.Combine(_testRoot, "pollution", AndroidToolchainLockFile.DefaultFileName);
        await new AndroidToolchainResolver(runner).ResolveAsync(new AndroidToolchainResolveOptions
        {
            CommandLineOverrides = new AndroidToolchainPathOverrides
            {
                DotNetRoot = toolchain.DotNetRoot,
                JavaSdkRoot = cliJdk,
            },
            ImportedToolchainRoot = toolchain.ImportedRoot,
            LockFilePath = lockPath,
            TemporaryRoot = Path.Combine(_testRoot, "probes"),
        });
        File.WriteAllText(Path.Combine(toolchain.ImportedRoot, "SDK", ".knownPackages"), "probe-state");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AndroidToolchainLockFile.ReadAndValidateAsync(lockPath));

        Assert.Contains("undeclared file", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineCacheVerifierRejectsTamperedComponentMetadata()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        TestToolchain toolchain = await CreateImportedToolchainAsync();
        string manifestPath = Path.Combine(toolchain.ImportedRoot, AndroidToolchainBootstrapper.ManifestFileName);
        JsonNode manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["Components"]![0]!["Sha256"] = new string('0', 64);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AndroidToolchainCacheVerifier.VerifyAsync(toolchain.ImportedRoot));

        Assert.Contains("component failed integrity verification", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRunnerExecutesBatchFileWithQuotedPathAndArgument()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        if (!OperatingSystem.IsWindows())
            return;
        string directory = Path.Combine(_testRoot, "batch path");
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "probe tool.bat");
        File.WriteAllText(script, "@echo off\r\necho %~1\r\n");

        AndroidToolchainProcessResult result = await new AndroidToolchainProcessRunner().RunAsync(
            script,
            ["hello world"],
            directory,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.CombinedOutput);
        Assert.Equal("hello world", result.StandardOutput.Trim());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private async Task<TestToolchain> CreateImportedToolchainAsync()
    {
        string source = Path.Combine(_testRoot, "Unity2022.3.62f2_Test", "Editor", "Data", "PlaybackEngines", "AndroidPlayer");
        Write(source, "OpenJDK/release", "JAVA_VERSION=\"11.0.14.1\"\n");
        Write(source, "OpenJDK/bin/java.exe", "java-import");
        Write(source, "NDK/source.properties", "Pkg.Revision = 23.1.7779620\n");
        Write(source, "NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/clang.exe", "clang");
        Write(source, "SDK/build-tools/36.0.0/aapt2.exe", "aapt2");
        Write(source, "SDK/build-tools/36.0.0/apksigner.bat", "apksigner");
        Write(source, "SDK/build-tools/36.0.0/zipalign.exe", "zipalign");
        Write(source, "SDK/platforms/android-36/android.jar", "android-36");
        Write(source, "SDK/platform-tools/source.properties", "Pkg.Revision = 37.0.0\n");
        Write(source, "SDK/platform-tools/adb.exe", "adb");
        Write(source, "SDK/cmdline-tools/6.0/bin/sdkmanager.bat", "sdkmanager");
        Write(source, "SDK/licenses/android-sdk-license", "accepted\n");
        Write(source, "Tools/bundletool-all-1.17.2.jar", "bundletool");

        string cache = Path.Combine(_testRoot, "cache");
        AndroidToolchainImportResult import = await new AndroidToolchainBootstrapper().ImportAsync(
            new AndroidToolchainImportOptions
            {
                SourceRoot = source,
                DestinationRoot = cache,
                SeedId = "test-unity",
            });

        string dotnetRoot = Path.Combine(_testRoot, "dotnet");
        Write(dotnetRoot, "dotnet.exe", "dotnet");
        string workloadManifest = Path.Combine(
            dotnetRoot,
            "sdk-manifests",
            "10.0.100",
            "microsoft.net.sdk.android",
            "36.1.2",
            "WorkloadManifest.json");
        Write(dotnetRoot, Path.GetRelativePath(dotnetRoot, workloadManifest), "{}");
        return new TestToolchain(source, cache, import.CachePath, dotnetRoot, workloadManifest);
    }

    private string CreateJdk(string name, string version, string executableContents)
    {
        string root = Path.Combine(_testRoot, name);
        Write(root, "release", $"JAVA_VERSION=\"{version}\"\n");
        Write(root, "bin/java.exe", executableContents);
        return root;
    }

    private static AndroidToolchainComponentLock FindComponent(AndroidToolchainSnapshot snapshot, string id)
    {
        for (int i = 0; i < snapshot.Components.Count; i++)
        {
            if (string.Equals(snapshot.Components[i].Id, id, StringComparison.Ordinal))
                return snapshot.Components[i];
        }
        throw new Xunit.Sdk.XunitException($"Component not found: {id}");
    }

    private static void Write(string root, string relativePath, string contents)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private sealed record TestToolchain(
        string SeedRoot,
        string CacheRoot,
        string ImportedRoot,
        string DotNetRoot,
        string WorkloadManifestPath);

    private static AndroidToolchainLock CreateFingerprintLock(
        int targetApi,
        long durationMilliseconds,
        long artifactBytes,
        string artifactSha256) => new()
    {
        Host = new AndroidToolchainHostLock("Windows", "10.0", "X64", true),
        DotNetSdkVersion = "10.0.300",
        DotNetRuntimeVersion = "10.0.8",
        AndroidWorkloadVersion = "36.1.2/10.0.100",
        ImportedCacheFingerprint = new string('C', 64),
        DryRun = new AndroidToolchainDryRunLock(
            true,
            "net10.0-android",
            "android-arm64",
            targetApi,
            durationMilliseconds,
            "probe.apk",
            artifactBytes,
            artifactSha256,
            string.Empty),
    };

    private sealed record FakeProcessCall(string ExecutablePath, string[] Arguments);

    private sealed class FakeProcessRunner(string workloadManifestPath, string javaVersion) : IAndroidToolchainProcessRunner
    {
        internal List<FakeProcessCall> Calls { get; } = [];

        public Task<AndroidToolchainProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] copiedArguments = new string[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
                copiedArguments[i] = arguments[i];
            Calls.Add(new FakeProcessCall(executablePath, copiedArguments));

            string fileName = Path.GetFileName(executablePath);
            string output = "ok";
            string error = string.Empty;
            if (string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments.Count == 1 && string.Equals(arguments[0], "--info", StringComparison.Ordinal))
                {
                    output = $"""
                        .NET SDK:
                         Version:           10.0.300
                         Workload version:  10.0.300-manifests.test

                        .NET workloads installed:
                         [android]
                           Manifest Version:    36.1.2/10.0.100
                           Manifest Path:       {workloadManifestPath}

                        Host:
                          Version:      10.0.8
                        """;
                }
                else if (arguments.Count >= 2 && string.Equals(arguments[0], "workload", StringComparison.Ordinal))
                {
                    output = "android                    36.1.2/10.0.100       SDK 10.0.300";
                }
                else if (arguments.Count > 0 && string.Equals(arguments[0], "build", StringComparison.Ordinal))
                {
                    string artifact = Path.Combine(
                        workingDirectory,
                        "bin",
                        "Release",
                        "net10.0-android",
                        "android-arm64",
                        "com.xengine.toolchainprobe-Signed.apk");
                    Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                    File.WriteAllText(artifact, "apk");
                    output = "Build succeeded.";
                }
            }
            else if (string.Equals(fileName, "java.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments.Count == 1 && string.Equals(arguments[0], "-version", StringComparison.Ordinal))
                    error = $"openjdk version \"{javaVersion}\" LTS";
                else
                    output = "1.17.2";
            }
            else if (string.Equals(fileName, "clang.exe", StringComparison.OrdinalIgnoreCase))
            {
                output = "Android clang version 12.0.8";
            }
            return Task.FromResult(new AndroidToolchainProcessResult(0, 5, output, error));
        }

        internal FakeProcessCall FindCall(string fileName)
        {
            for (int i = 0; i < Calls.Count; i++)
            {
                if (string.Equals(Path.GetFileName(Calls[i].ExecutablePath), fileName, StringComparison.OrdinalIgnoreCase))
                    return Calls[i];
            }
            throw new Xunit.Sdk.XunitException($"Process call not found: {fileName}");
        }
    }
}
