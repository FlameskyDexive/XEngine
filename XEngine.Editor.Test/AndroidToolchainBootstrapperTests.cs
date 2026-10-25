// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Security.Cryptography;
using System.Text.Json.Nodes;

using XEngine.Editor.Build.Android;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidToolchainBootstrapperTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineAndroidToolchainTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportCopiesOnlySelectedToolchainFilesAndWritesVerifiedManifest()
    {
        string source = CreateSeed();
        string destination = Path.Combine(_testRoot, "cache");
        var bootstrapper = new AndroidToolchainBootstrapper();

        AndroidToolchainImportResult result = await bootstrapper.ImportAsync(new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = destination,
            SeedId = "test-unity",
        });

        Assert.False(result.Reused);
        Assert.Equal(AndroidToolchainBootstrapper.ManifestSchemaVersion, result.Manifest.SchemaVersion);
        Assert.Equal(64, result.Manifest.Fingerprint.Length);
        Assert.Equal("test-unity-" + result.Manifest.Fingerprint, Path.GetFileName(result.CachePath));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(result.Manifest.MaximumPathLength > result.CachePath.Length);
        Assert.Contains("OpenJDK", result.Manifest.IncludedComponents);
        Assert.Contains("Bee/", result.Manifest.ExcludedUnityContent);
        Assert.All(result.Manifest.Components, static component => Assert.Equal(64, component.Sha256.Length));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "OpenJDK", "bin", "java.exe")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "NDK", "toolchains", "clang.exe")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "SDK", "build-tools", "36.0.0", "aapt2.exe")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "SDK", "platforms", "android-36", "android.jar")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "SDK", "platform-tools", "adb.exe")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "SDK", "cmdline-tools", "6.0", "bin", "sdkmanager.bat")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "SDK", "licenses", "android-sdk-license")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "Tools", "bundletool-all-1.17.2.jar")));

        Assert.False(Directory.Exists(Path.Combine(result.CachePath, "SDK", "build-tools", "34.0.0")));
        Assert.False(Directory.Exists(Path.Combine(result.CachePath, "SDK", "platforms", "android-34")));
        Assert.False(Directory.Exists(Path.Combine(result.CachePath, "Tools", "gradle")));
        Assert.False(Directory.Exists(Path.Combine(result.CachePath, "Bee")));
        Assert.False(File.Exists(Path.Combine(result.CachePath, "Unity.Android.Gradle.dll")));
        Assert.False(Directory.Exists(Path.Combine(result.CachePath, "Variations")));

        var manifestPaths = new HashSet<string>(StringComparer.Ordinal);
        long manifestBytes = 0;
        for (int i = 0; i < result.Manifest.Files.Count; i++)
        {
            AndroidToolchainImportedFile file = result.Manifest.Files[i];
            Assert.True(manifestPaths.Add(file.RelativePath), $"Duplicate manifest path: {file.RelativePath}");
            manifestBytes += file.Length;
            string importedPath = Path.Combine(result.CachePath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(importedPath))));
        }
        Assert.Equal(result.Manifest.Files.Count, manifestPaths.Count);
        Assert.Equal(result.Manifest.TotalBytes, manifestBytes);
    }

    [Fact]
    public async Task RepeatedImportReusesContentAddressedCache()
    {
        string source = CreateSeed();
        string destination = Path.Combine(_testRoot, "cache");
        var options = new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = destination,
            SeedId = "test-unity",
        };
        var bootstrapper = new AndroidToolchainBootstrapper();

        AndroidToolchainImportResult first = await bootstrapper.ImportAsync(options);
        DateTime manifestWriteTime = File.GetLastWriteTimeUtc(first.ManifestPath);
        AndroidToolchainImportResult second = await bootstrapper.ImportAsync(options);

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.CachePath, second.CachePath);
        Assert.Equal(first.Manifest.Fingerprint, second.Manifest.Fingerprint);
        Assert.Equal(manifestWriteTime, File.GetLastWriteTimeUtc(second.ManifestPath));
        Assert.Empty(Directory.GetDirectories(destination, "*.staging", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ChangedSeedCreatesNewCacheWithoutOverwritingPreviousImport()
    {
        string source = CreateSeed();
        string destination = Path.Combine(_testRoot, "cache");
        var options = new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = destination,
            SeedId = "test-unity",
        };
        var bootstrapper = new AndroidToolchainBootstrapper();

        AndroidToolchainImportResult first = await bootstrapper.ImportAsync(options);
        File.WriteAllText(Path.Combine(source, "OpenJDK", "bin", "java.exe"), "java-v2");
        AndroidToolchainImportResult second = await bootstrapper.ImportAsync(options);

        Assert.NotEqual(first.Manifest.Fingerprint, second.Manifest.Fingerprint);
        Assert.NotEqual(first.CachePath, second.CachePath);
        Assert.True(Directory.Exists(first.CachePath));
        Assert.True(Directory.Exists(second.CachePath));
        Assert.Equal("java-v1", File.ReadAllText(Path.Combine(first.CachePath, "OpenJDK", "bin", "java.exe")));
        Assert.Equal("java-v2", File.ReadAllText(Path.Combine(second.CachePath, "OpenJDK", "bin", "java.exe")));
    }

    [Fact]
    public async Task CorruptPublishedCacheIsRejectedAndStagingIsRemoved()
    {
        string source = CreateSeed();
        string destination = Path.Combine(_testRoot, "cache");
        var options = new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = destination,
            SeedId = "test-unity",
        };
        var bootstrapper = new AndroidToolchainBootstrapper();

        AndroidToolchainImportResult first = await bootstrapper.ImportAsync(options);
        File.WriteAllText(Path.Combine(first.CachePath, "OpenJDK", "bin", "java.exe"), "java-v0");

        IOException exception = await Assert.ThrowsAsync<IOException>(() => bootstrapper.ImportAsync(options));

        Assert.Contains("integrity verification", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(destination, "*.staging", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task UnexpectedFileInPublishedCacheIsRejected()
    {
        string source = CreateSeed();
        string destination = Path.Combine(_testRoot, "cache");
        var options = new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = destination,
            SeedId = "test-unity",
        };
        var bootstrapper = new AndroidToolchainBootstrapper();

        AndroidToolchainImportResult first = await bootstrapper.ImportAsync(options);
        File.WriteAllText(Path.Combine(first.CachePath, "SDK", ".knownPackages"), "probe-state");

        IOException exception = await Assert.ThrowsAsync<IOException>(() => bootstrapper.ImportAsync(options));

        Assert.Contains("integrity verification", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangedManifestMetadataIsRejected()
    {
        string source = CreateSeed();
        string destination = Path.Combine(_testRoot, "cache");
        var options = new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = destination,
            SeedId = "test-unity",
        };
        var bootstrapper = new AndroidToolchainBootstrapper();

        AndroidToolchainImportResult first = await bootstrapper.ImportAsync(options);
        JsonNode manifest = JsonNode.Parse(await File.ReadAllTextAsync(first.ManifestPath))!;
        manifest["Components"]![0]!["Version"] = "tampered";
        await File.WriteAllTextAsync(first.ManifestPath, manifest.ToJsonString());

        IOException exception = await Assert.ThrowsAsync<IOException>(() => bootstrapper.ImportAsync(options));

        Assert.Contains("integrity verification", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullManifestCollectionsAreRejectedAsIntegrityFailure()
    {
        string source = CreateSeed();
        string destination = Path.Combine(_testRoot, "cache");
        var options = new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = destination,
            SeedId = "test-unity",
        };
        var bootstrapper = new AndroidToolchainBootstrapper();

        AndroidToolchainImportResult first = await bootstrapper.ImportAsync(options);
        JsonNode manifest = JsonNode.Parse(await File.ReadAllTextAsync(first.ManifestPath))!;
        manifest["Components"] = null;
        await File.WriteAllTextAsync(first.ManifestPath, manifest.ToJsonString());

        IOException exception = await Assert.ThrowsAsync<IOException>(() => bootstrapper.ImportAsync(options));

        Assert.Contains("integrity verification", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitVersionsAndGradleLibrariesAreHonored()
    {
        string source = CreateSeed();
        string destination = Path.Combine(_testRoot, "cache");
        var bootstrapper = new AndroidToolchainBootstrapper();

        AndroidToolchainImportResult result = await bootstrapper.ImportAsync(new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = destination,
            SeedId = "explicit",
            BuildToolsVersion = "34.0.0",
            PlatformApi = 34,
            CommandLineToolsVersion = "6.0",
            IncludeGradleLibraries = true,
        });

        Assert.True(File.Exists(Path.Combine(result.CachePath, "SDK", "build-tools", "34.0.0", "aapt2.exe")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "SDK", "platforms", "android-34", "android.jar")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "Tools", "gradle", "lib", "gradle-core-7.5.1.jar")));
        Assert.True(File.Exists(Path.Combine(result.CachePath, "Tools", "gradle", "plugins", "plugin.jar")));
        Assert.DoesNotContain(
            result.Manifest.Components,
            static component => component.Id == "sdk-build-tools" && component.Version == "36.0.0");
        Assert.Contains(
            result.Manifest.Components,
            static component => component.Id == "gradle-libraries" && component.Version == "7.5.1");
    }

    [Fact]
    public async Task ImportRejectsDestinationInsideSourceAndGitWorktree()
    {
        string source = CreateSeed();
        var bootstrapper = new AndroidToolchainBootstrapper();

        await Assert.ThrowsAsync<InvalidOperationException>(() => bootstrapper.ImportAsync(new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = Path.Combine(source, "Cache"),
        }));

        string worktree = Path.Combine(_testRoot, "worktree");
        Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bootstrapper.ImportAsync(new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = Path.Combine(worktree, "toolchains"),
        }));
        Assert.False(Directory.Exists(Path.Combine(worktree, "toolchains")));
    }

    [Fact]
    public async Task PreCancelledImportDoesNotCreateDestination()
    {
        string source = CreateSeed();
        string destination = Path.Combine(_testRoot, "cancelled-cache");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var bootstrapper = new AndroidToolchainBootstrapper();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bootstrapper.ImportAsync(
            new AndroidToolchainImportOptions
            {
                SourceRoot = source,
                DestinationRoot = destination,
            },
            cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ImportSessionReportsSuccessAndFailureWithoutBlockingCaller()
    {
        string source = CreateSeed();
        using var session = new AndroidToolchainImportSession();

        Assert.True(session.Start(new AndroidToolchainImportOptions
        {
            SourceRoot = source,
            DestinationRoot = Path.Combine(_testRoot, "session-cache"),
        }));
        await session.Completion;

        Assert.Equal(AndroidToolchainImportSessionState.Succeeded, session.Snapshot.State);
        Assert.NotNull(session.Snapshot.Result);

        Assert.True(session.Start(new AndroidToolchainImportOptions
        {
            SourceRoot = Path.Combine(_testRoot, "missing"),
            DestinationRoot = Path.Combine(_testRoot, "failed-cache"),
        }));
        await session.Completion;

        Assert.Equal(AndroidToolchainImportSessionState.Failed, session.Snapshot.State);
        Assert.Contains("missing", session.Snapshot.Error, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private string CreateSeed()
    {
        string source = Path.Combine(_testRoot, "Unity2022.3.62f2_Test", "Editor", "Data", "PlaybackEngines", "AndroidPlayer");
        Write(source, "OpenJDK/release", "JAVA_VERSION=\"11.0.14.1\"\n");
        Write(source, "OpenJDK/bin/java.exe", "java-v1");
        Write(source, "NDK/source.properties", "Pkg.Desc = Android NDK\nPkg.Revision = 23.1.7779620\n");
        Write(source, "NDK/toolchains/clang.exe", "clang");
        Write(source, "SDK/build-tools/34.0.0/aapt2.exe", "aapt2-34");
        Write(source, "SDK/build-tools/36.0.0/aapt2.exe", "aapt2-36");
        Write(source, "SDK/platforms/android-34/android.jar", "android-34");
        Write(source, "SDK/platforms/android-36/android.jar", "android-36");
        Write(source, "SDK/platform-tools/source.properties", "Pkg.Revision = 37.0.0\n");
        Write(source, "SDK/platform-tools/adb.exe", "adb");
        Write(source, "SDK/cmdline-tools/6.0/bin/sdkmanager.bat", "sdkmanager");
        Write(source, "SDK/licenses/android-sdk-license", "accepted\n");
        Write(source, "Tools/bundletool-all-1.17.2.jar", "bundletool");
        Write(source, "Tools/gradle/lib/gradle-core-7.5.1.jar", "gradle-core");
        Write(source, "Tools/gradle/plugins/plugin.jar", "gradle-plugin");

        Write(source, "Bee/Bee.Toolchain.Android.dll", "unity-proprietary");
        Write(source, "Variations/il2cpp/libil2cpp.so", "unity-player");
        Write(source, "Unity.Android.Gradle.dll", "unity-proprietary");
        Write(source, "Tools/GradleTemplates/mainTemplate.gradle", "unity-template");
        return source;
    }

    private static void Write(string root, string relativePath, string contents)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
