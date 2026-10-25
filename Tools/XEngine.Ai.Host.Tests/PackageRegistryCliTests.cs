// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Microsoft.Extensions.DependencyInjection;

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.PackageRegistry;
using XEngine.PackageRegistry.Testing;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// P3 CLI verbs + MCP registry tools against the in-process FakeNpmServer: search/info read the
/// registry, install/update mutate only Packages/manifest.json (the editor watcher re-resolves),
/// publish packs and uploads through the npm PUT protocol.
/// </summary>
public class PackageRegistryCliTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "XEnginePackageRegistryCliTests", Guid.NewGuid().ToString("N"));
    private readonly FakeNpmServer _server = new();
    private readonly ServiceProvider _services;
    private readonly string? _prevTokenEnv;

    public PackageRegistryCliTests()
    {
        _prevTokenEnv = Environment.GetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar);
        Environment.SetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar, "cli-test-token");
        Environment.SetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar, null);
        Environment.SetEnvironmentVariable(NpmRegistryOptions.RegistryEnvVar, null);

        Directory.CreateDirectory(Path.Combine(_root, "Packages"));
        File.WriteAllText(Path.Combine(_root, "Packages", "manifest.json"), """
            {
              "dependencies": {},
              "testables": []
            }
            """);

        var descriptor = new ProjectDescriptor("test-project", _root, null, null, null, "fingerprint");
        var collection = new ServiceCollection();
        collection.AddSingleton(descriptor);
        _services = collection.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _server.Dispose();
        Environment.SetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar, _prevTokenEnv);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private string CreateSourcePackage(string name, string version)
    {
        string dir = Path.Combine(_root, "src", name.Replace('.', '-'));
        Directory.CreateDirectory(Path.Combine(dir, "Runtime"));
        File.WriteAllText(Path.Combine(dir, "package.json"),
            $$"""{ "name": "{{name}}", "version": "{{version}}", "description": "cli test package" }""");
        File.WriteAllText(Path.Combine(dir, "Runtime", "Marker.cs"), $"// {name} {version}\n");
        return dir;
    }

    private void PublishToRegistry(string name, string version)
    {
        using var client = new NpmRegistryClient(new NpmRegistryOptions
        {
            RegistryUrl = _server.RegistryUrl,
            AuthToken = "cli-test-token",
        });
        client.PublishAsync(CreateSourcePackage(name, version)).GetAwaiter().GetResult();
    }

    private void WriteManifest(params (string name, string specifier)[] dependencies)
    {
        var entries = dependencies.Select(d => $"    \"{d.name}\": \"{d.specifier}\"");
        File.WriteAllText(Path.Combine(_root, "Packages", "manifest.json"), $$"""
            {
              "dependencies": {
            {{string.Join(",\n", entries)}}
              },
              "testables": []
            }
            """);
    }

    private string ReadManifestText()
        => File.ReadAllText(Path.Combine(_root, "Packages", "manifest.json"));

    // ------------------------------------------------------------------
    //  Verbs
    // ------------------------------------------------------------------

    [Fact]
    public async Task Search_MapsRegistryScopeToEngineNames()
    {
        PublishToRegistry("com.xengine.demo", "0.2.0");
        PublishToRegistry("@other/third", "1.0.0");

        var result = (PackageSearchCliResult)await PackageRegistryCli.SearchAsync(
            new[] { "--query", "com.xengine", "--registry", _server.RegistryUrl }, _services);

        PackageSearchCliEntry demo = Assert.Single(result.Results, r => r.Name == "com.xengine.demo");
        Assert.Equal("@inxep/demo", demo.RegistryName);
        Assert.Equal("0.2.0", demo.LatestVersion);
        Assert.DoesNotContain(result.Results, r => r.Name == "@other/third");
    }

    [Fact]
    public async Task Info_ListsAllVersionsWithLatest()
    {
        PublishToRegistry("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");

        var result = (PackageInfoCliResult)await PackageRegistryCli.InfoAsync(
            new[] { "--name", "com.xengine.demo", "--registry", _server.RegistryUrl }, _services);

        Assert.Equal("com.xengine.demo", result.Name);
        Assert.Equal("0.2.0", result.LatestVersion);
        Assert.Equal(2, result.Versions.Count);
    }

    [Fact]
    public async Task Install_MissingVersion_FailsWithoutTouchingManifest()
    {
        PublishToRegistry("com.xengine.demo", "0.2.0");

        await Assert.ThrowsAsync<FileNotFoundException>(() => PackageRegistryCli.InstallAsync(
            new[] { "--name", "com.xengine.demo", "--version", "9.9.9", "--registry", _server.RegistryUrl },
            _services));
        Assert.DoesNotContain("com.xengine.demo", ReadManifestText());
    }

    [Fact]
    public async Task Install_WithRegistryOverride_WritesRegistrySpecifier()
    {
        PublishToRegistry("com.xengine.demo", "0.2.0");

        var result = (PackageInstallCliResult)await PackageRegistryCli.InstallAsync(
            new[] { "--name", "com.xengine.demo", "--registry", _server.RegistryUrl.TrimEnd('/') },
            _services);

        Assert.Equal("0.2.0", result.Version);
        Assert.Equal("registry:" + _server.RegistryUrl.TrimEnd('/') + "#0.2.0", result.Specifier);
        Assert.Contains("\"com.xengine.demo\": \"registry:" + _server.RegistryUrl.TrimEnd('/') + "#0.2.0\"",
            ReadManifestText());
    }

    [Fact]
    public async Task Update_All_BumpsRegistryPinsOnly()
    {
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest(
            ("com.xengine.demo", "0.1.1"),
            ("com.other.tools", "file:../tools"));

        var result = (PackageUpdateCliResult)await PackageRegistryCli.UpdateAsync(
            new[] { "--all", "--registry", _server.RegistryUrl }, _services);

        PackageUpdateCliChange change = Assert.Single(result.Changes);
        Assert.Equal("com.xengine.demo", change.Name);
        Assert.Equal("0.1.1", change.FromVersion);
        Assert.Equal("0.2.0", change.ToVersion);
        Assert.Contains("\"com.xengine.demo\": \"0.2.0\"", ReadManifestText());
        Assert.Contains("\"com.other.tools\": \"file:../tools\"", ReadManifestText());
    }

    [Fact]
    public async Task Publish_DryRun_PacksWithoutUploading()
    {
        string source = CreateSourcePackage("com.xengine.dryrun", "1.0.0");

        var result = (PackagePublishCliResult)await PackageRegistryCli.PublishAsync(
            new[] { "--package", source, "--dry-run" }, _services);

        Assert.Equal("@inxep/dryrun", result.RegistryName);
        Assert.Equal("1.0.0", result.Version);
        Assert.True(result.DryRun);
        Assert.False(result.Uploaded);
        Assert.NotNull(result.TarballBytes);
        Assert.True(result.TarballBytes!.Value > 0);
        Assert.DoesNotContain(_server.PackageNames, n => n == "@inxep/dryrun");
    }

    [Fact]
    public async Task Publish_UploadsAndIsQueryableAfterwards()
    {
        string source = CreateSourcePackage("com.xengine.upload", "1.2.3");

        var result = (PackagePublishCliResult)await PackageRegistryCli.PublishAsync(
            new[] { "--package", source, "--registry", _server.RegistryUrl }, _services);

        Assert.True(result.Uploaded);
        Assert.Equal("@inxep/upload", result.RegistryName);
        Assert.Equal("1.2.3", result.Version);

        var info = (PackageInfoCliResult)await PackageRegistryCli.InfoAsync(
            new[] { "--name", "com.xengine.upload", "--registry", _server.RegistryUrl }, _services);
        Assert.Single(info.Versions, v => v.Version == "1.2.3");
    }

    [Fact]
    public async Task Publish_WithOtpRequired_SendsNpmOtpHeader()
    {
        _server.ExpectedOtp = "246810";
        string source = CreateSourcePackage("com.xengine.otp", "1.0.0");

        // Without the code: npm-style 2FA rejection with actionable guidance.
        NpmRegistryException rejected = await Assert.ThrowsAsync<NpmRegistryException>(
            () => PackageRegistryCli.PublishAsync(
                new[] { "--package", source, "--registry", _server.RegistryUrl }, _services));
        Assert.Contains(NpmRegistryOptions.OtpEnvVar, rejected.Message);

        // With --otp (or XENGINE_NPM_OTP): publish succeeds.
        var result = (PackagePublishCliResult)await PackageRegistryCli.PublishAsync(
            new[] { "--package", source, "--registry", _server.RegistryUrl, "--otp", "246810" }, _services);
        Assert.True(result.Uploaded);
        Assert.Equal("@inxep/otp", result.RegistryName);
    }

    // ------------------------------------------------------------------
    //  MCP tools
    // ------------------------------------------------------------------

    [Fact]
    public async Task McpPackageSearch_ReturnsEngineNames()
    {
        PublishToRegistry("com.xengine.spine", "0.2.0");

        PackageSearchMcpResult result = await PackageTools.SearchRegistry(
            _services, "spine", registry: _server.RegistryUrl);

        PackageSearchMcpEntry entry = Assert.Single(result.Results, r => r.Name == "com.xengine.spine");
        Assert.Equal("@inxep/spine", entry.RegistryName);
    }

    [Fact]
    public async Task McpPackageVersions_ListsVersionsAndDependencies()
    {
        PublishToRegistry("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");

        PackageVersionsMcpResult result = await PackageTools.ListVersions(
            _services, "com.xengine.demo", registry: _server.RegistryUrl);

        Assert.Equal("com.xengine.demo", result.Name);
        Assert.Equal("0.2.0", result.LatestVersion);
        Assert.Equal(2, result.Versions.Count);
    }

    [Fact]
    public async Task McpPackageVersions_UnknownPackage_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => PackageTools.ListVersions(
            _services, "com.xengine.nosuch", registry: _server.RegistryUrl));
    }
}
