// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.Text.Json.Nodes;
using XEngine.PackageRegistry;
using XEngine.PackageRegistry.Testing;

namespace XEngine.PackageRegistry.Tests;

public class NpmRegistryClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xengine-npm-client-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNpmServer _server = new();

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private NpmRegistryClient CreateClient(string? token = "test-token")
        => new(new NpmRegistryOptions { RegistryUrl = _server.RegistryUrl, AuthToken = token });

    [Fact]
    public async Task PublishThenFetch_RoundTripsThroughTheRegistry()
    {
        string source = TestPackageDir.Create(_root, "com.xengine.spine", "0.2.0", dependency: "com.xengine.xgui");

        using (var publisher = CreateClient())
        {
            (string wireName, string version) = await publisher.PublishAsync(source);
            Assert.Equal("@inxep/spine", wireName);
            Assert.Equal("0.2.0", version);
        }

        using var reader = CreateClient(token: null);
        NpmPackument? packument = await reader.GetPackumentAsync("com.xengine.spine");
        Assert.NotNull(packument);
        Assert.Equal("@inxep/spine", packument!.Name);
        Assert.Equal("com.xengine.spine", packument.EngineName);
        Assert.Equal("0.2.0", packument.LatestVersion);
        NpmVersionInfo versionInfo = Assert.Single(packument.Versions, v => v.Version == "0.2.0");
        Assert.Equal("0.1.0", versionInfo.EngineDependencies()["com.xengine.xgui"]);
        Assert.NotNull(versionInfo.TarballUrl);
        Assert.StartsWith("sha512-", versionInfo.Integrity);

        // Download + verify + extract matches the source package.
        string tgz = Path.Combine(_root, "downloaded.tgz");
        string sri = await reader.DownloadTarballAsync(versionInfo, tgz, "com.xengine.spine");
        Assert.Equal(versionInfo.Integrity, sri);
        string extracted = Path.Combine(_root, "extracted");
        NpmTarball.Extract(tgz, extracted);
        Assert.True(File.Exists(Path.Combine(extracted, "Runtime", "Marker.cs")));
    }

    [Fact]
    public async Task Publish_DocumentUsesWireNamesAndPublicAccess()
    {
        string source = TestPackageDir.Create(_root, "com.xengine.demo", "1.0.0");
        _server.ExpectedToken = "secret";

        using (var publisher = CreateClient(token: "secret"))
        {
            await publisher.PublishAsync(source);
        }

        JsonObject document = Assert.Single(_server.PublishedDocuments);
        Assert.Equal("@inxep/demo", document["name"]!.GetValue<string>());
        Assert.Equal("public", document["access"]!.GetValue<string>());
        Assert.Equal("1.0.0", document["dist-tags"]!["latest"]!.GetValue<string>());
        JsonObject attachment = document["_attachments"]!.AsObject().First().Value!.AsObject();
        Assert.Contains("content_type", attachment);
        Assert.Contains("data", attachment);
    }

    [Fact]
    public async Task Publish_WithoutToken_FailsWithSetupGuidance()
    {
        string source = TestPackageDir.Create(_root, "com.xengine.demo", "1.0.0");
        using var publisher = CreateClient(token: null);

        NpmRegistryException exception = await Assert.ThrowsAsync<NpmRegistryException>(
            () => publisher.PublishAsync(source));
        Assert.Contains(NpmRegistryOptions.TokenEnvVar, exception.Message);
    }

    [Fact]
    public async Task Publish_WithWrongToken_IsRejected()
    {
        _server.ExpectedToken = "right";
        string source = TestPackageDir.Create(_root, "com.xengine.demo", "1.0.0");
        using var publisher = CreateClient(token: "wrong");

        NpmRegistryException exception = await Assert.ThrowsAsync<NpmRegistryException>(
            () => publisher.PublishAsync(source));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task Publish_SameVersionAgain_FailsWithConflict()
    {
        string source = TestPackageDir.Create(_root, "com.xengine.demo", "0.1.0");
        using var publisher = CreateClient();

        await publisher.PublishAsync(source);
        NpmRegistryException exception = await Assert.ThrowsAsync<NpmRegistryException>(
            () => publisher.PublishAsync(source));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task Publish_AppendVersion_MergesHistoryAndMovesLatest()
    {
        // npmjs.com silently drops an append whose PUT omits the package's existing versions and
        // dist-tags (found against the real registry); the client must merge them like npm CLI.
        string first = TestPackageDir.Create(Path.Combine(_root, "v1"), "com.xengine.demo", "1.0.0");
        string second = TestPackageDir.Create(Path.Combine(_root, "v2"), "com.xengine.demo", "1.1.0");
        using var publisher = CreateClient();

        await publisher.PublishAsync(first);
        await publisher.PublishAsync(second);

        using var reader = CreateClient(token: null);
        NpmPackument packument = (await reader.GetPackumentAsync("com.xengine.demo"))!;
        Assert.Equal("1.1.0", packument.LatestVersion);
        Assert.Equal(2, packument.Versions.Count);
        Assert.Contains(packument.Versions, v => v.Version == "1.0.0");
        Assert.Contains(packument.Versions, v => v.Version == "1.1.0");
    }

    [Fact]
    public async Task GetPackument_ForUnknownPackage_ReturnsNull()
    {
        using var client = CreateClient(token: null);
        Assert.Null(await client.GetPackumentAsync("com.xengine.nosuch"));
    }

    [Fact]
    public async Task Search_FindsScopedPackages()
    {
        await CreateClient().PublishAsync(TestPackageDir.Create(_root, "com.xengine.alpha", "0.1.0"));
        await CreateClient().PublishAsync(TestPackageDir.Create(_root, "com.xengine.beta", "0.1.0"));
        // Third-party scope stays out of the official scope search.
        await CreateClient().PublishAsync(TestPackageDir.Create(_root, "@other/third", "0.1.0"));

        using var client = CreateClient(token: null);
        IReadOnlyList<NpmSearchResult> scoped = await client.SearchAsync("scope:inxep");
        Assert.Equal(2, scoped.Count);
        Assert.All(scoped, result => Assert.StartsWith("@inxep/", result.Name));
        Assert.Contains(scoped, result => result.EngineName == "com.xengine.alpha");
    }

    [Fact]
    public async Task DownloadTarball_WithTamperedRegistry_FailsIntegrity()
    {
        string source = TestPackageDir.Create(_root, "com.xengine.demo", "0.1.0");
        using (var publisher = CreateClient())
        {
            await publisher.PublishAsync(source);
        }

        _server.TamperTarball = true;
        using var client = CreateClient(token: null);
        NpmPackument packument = (await client.GetPackumentAsync("com.xengine.demo"))!;

        NpmRegistryException exception = await Assert.ThrowsAsync<NpmRegistryException>(
            () => client.DownloadTarballAsync(packument.Versions[0], Path.Combine(_root, "t.tgz"), "com.xengine.demo"));
        Assert.Contains("Integrity mismatch", exception.Message);
    }

    [Fact]
    public async Task Disabled_FailsFastWithoutNetwork()
    {
        using var client = new NpmRegistryClient(new NpmRegistryOptions
        {
            RegistryUrl = _server.RegistryUrl,
            Disabled = true,
        });

        NpmRegistryException exception = await Assert.ThrowsAsync<NpmRegistryException>(
            () => client.GetPackumentAsync("com.xengine.demo"));
        Assert.Contains(NpmRegistryOptions.DisableEnvVar, exception.Message);
    }
}
