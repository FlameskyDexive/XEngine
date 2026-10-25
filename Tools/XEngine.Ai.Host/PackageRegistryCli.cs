// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;

using XEngine.Ai.Protocol;
using XEngine.PackageRegistry;

namespace XEngine.Ai.Host;

/// <summary>
/// npm-registry verbs for xengine-cli: search, inspect versions, install/update manifest pins, and
/// publish a package directory. Install/update are manifest-only mutations (same contract as
/// package_add): the editor's manifest watcher re-resolves and downloads into PackageCache; the CLI
/// itself never mutates the cache. Registry/token configuration comes from XENGINE_NPM_REGISTRY /
/// XENGINE_NPM_TOKEN (see <see cref="NpmRegistryOptions"/>); --registry overrides per invocation.
/// </summary>
internal static class PackageRegistryCli
{
    private static NpmRegistryOptions BaseOptions(IServiceProvider services, string? registryOverride)
    {
        string projectRoot = services.GetRequiredService<ProjectDescriptor>().ProjectRoot;
        NpmRegistryOptions loaded = NpmRegistryOptions.Load(projectRoot);
        if (string.IsNullOrWhiteSpace(registryOverride)) return loaded;
        return new NpmRegistryOptions
        {
            RegistryUrl = registryOverride.Trim(),
            AuthToken = loaded.AuthToken,
            OneTimePassword = loaded.OneTimePassword,
            Timeout = loaded.Timeout,
            Disabled = loaded.Disabled,
            ProjectRoot = loaded.ProjectRoot,
        };
    }

    private static string ManifestPath(IServiceProvider services)
        => Path.Combine(services.GetRequiredService<ProjectDescriptor>().ProjectRoot, "Packages", "manifest.json");

    // ---------------------------------------------------------------------------------------------
    //  Verbs
    // ---------------------------------------------------------------------------------------------

    public static async Task<object> SearchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string query = CliRunner.GetRequiredOption(args, "--query");
        using var client = new NpmRegistryClient(BaseOptions(services, CliRunner.GetOptionalOption(args, "--registry")));
        IReadOnlyList<NpmSearchResult> results = await client.SearchAsync(query, CliRunner.GetIntOption(args, "--limit", 50)).ConfigureAwait(false);
        return new PackageSearchCliResult(results.Count,
            results.Select(r => new PackageSearchCliEntry(r.EngineName, r.Name, r.Version, r.Description)).ToArray());
    }

    public static async Task<object> InfoAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string name = CliRunner.GetRequiredOption(args, "--name");
        using var client = new NpmRegistryClient(BaseOptions(services, CliRunner.GetOptionalOption(args, "--registry")));
        NpmPackument? packument = await client.GetPackumentAsync(name).ConfigureAwait(false);
        if (packument == null)
            throw new FileNotFoundException($"Package '{name}' was not found on the registry.");
        return new PackageInfoCliResult(
            packument.EngineName, packument.Name, packument.LatestVersion,
            packument.Versions.Select(v => new PackageInfoCliVersion(
                v.Version, v.Description,
                v.EngineDependencies().Select(d => $"{d.Key} {d.Value}").ToArray())).ToArray());
    }

    public static async Task<object> InstallAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string name = CliRunner.GetRequiredOption(args, "--name");
        string? version = CliRunner.GetOptionalOption(args, "--version");
        string? registryOverride = CliRunner.GetOptionalOption(args, "--registry");
        bool dryRun = CliRunner.GetFlag(args, "--dry-run");

        using var client = new NpmRegistryClient(BaseOptions(services, registryOverride));
        NpmPackument? packument = await client.GetPackumentAsync(name).ConfigureAwait(false);
        if (packument == null)
            throw new FileNotFoundException(
                $"Package '{name}' was not found on the registry. Search with package-search first.");
        string selected = version ?? packument.LatestVersion
            ?? throw new FileNotFoundException($"Package '{name}' exposes no versions on the registry.");
        if (!packument.Versions.Any(v => string.Equals(v.Version, selected, StringComparison.OrdinalIgnoreCase)))
            throw new FileNotFoundException(
                $"Version '{selected}' of '{name}' was not found. Available: {string.Join(", ", packument.Versions.Select(v => v.Version))}");

        // Bare version pins resolve through scoped registries / the implicit official registry; an
        // explicit custom registry needs the registry: form so resolution finds it without config.
        string specifier = registryOverride == null ? selected : $"registry:{registryOverride.Trim().TrimEnd('/')}#{selected}";

        string path = ManifestPath(services);
        var manifest = PackageTools.ReadManifest(path);
        string? previous = manifest.Dependencies.TryGetValue(name, out string found) ? found : null;
        if (!dryRun)
        {
            manifest.Dependencies[name] = specifier;
            PackageTools.WriteManifest(path, manifest);
        }
        return new PackageInstallCliResult(name, selected, specifier, previous, dryRun,
            "The editor's manifest watcher picks up the change and downloads into Library/PackageCache.");
    }

    public static async Task<object> UpdateAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string? name = CliRunner.GetOptionalOption(args, "--name");
        bool all = CliRunner.GetFlag(args, "--all");
        bool dryRun = CliRunner.GetFlag(args, "--dry-run");
        if (name == null && !all)
            throw new ArgumentException("package-update requires --name <package> or --all.");

        string path = ManifestPath(services);
        var manifest = PackageTools.ReadManifest(path);
        using var client = new NpmRegistryClient(BaseOptions(services, CliRunner.GetOptionalOption(args, "--registry")));

        var changes = new List<PackageUpdateCliChange>();
        foreach (KeyValuePair<string, string> dependency in manifest.Dependencies)
        {
            if (name != null && !string.Equals(dependency.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            // Only version-pinned official names resolve through the registry; embed/file/git
            // specifiers have nothing to update against.
            if (!XEngine.PackageRegistry.NpmPackageName.IsInternalName(dependency.Key)) continue;
            if (!IsBareVersionPin(dependency.Value)) continue;

            NpmPackument? packument = await client.GetPackumentAsync(dependency.Key).ConfigureAwait(false);
            if (packument?.LatestVersion is not { Length: > 0 } latest) continue;
            if (string.Equals(latest, dependency.Value, StringComparison.OrdinalIgnoreCase)) continue;

            changes.Add(new PackageUpdateCliChange(dependency.Key, dependency.Value, latest, dryRun));
            if (!dryRun) manifest.Dependencies[dependency.Key] = latest;
        }

        if (!dryRun && changes.Count > 0)
            PackageTools.WriteManifest(path, manifest);
        return new PackageUpdateCliResult(changes.Count, dryRun, changes.ToArray());
    }

    public static async Task<object> PublishAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string packageDir = CliRunner.GetRequiredOption(args, "--package");
        if (!Directory.Exists(packageDir))
            throw new DirectoryNotFoundException($"Package directory '{packageDir}' does not exist.");
        bool dryRun = CliRunner.GetFlag(args, "--dry-run");

        // Dry-run packs without uploading so release notes can be reviewed first.
        if (dryRun)
        {
            string temp = Path.Combine(Path.GetTempPath(), "xengine-npm-dryrun-" + Guid.NewGuid().ToString("N") + ".tgz");
            try
            {
                (string fileName, string wireName, string version) = NpmTarball.Pack(packageDir, temp);
                return new PackagePublishCliResult(wireName, version, fileName,
                    new FileInfo(temp).Length, DryRun: true, Uploaded: false);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }

        NpmRegistryOptions options = BaseOptions(services, CliRunner.GetOptionalOption(args, "--registry"));
        // --otp overrides XENGINE_NPM_OTP for accounts that require 2FA to publish.
        string? otp = CliRunner.GetOptionalOption(args, "--otp");
        if (!string.IsNullOrWhiteSpace(otp))
            options = new NpmRegistryOptions
            {
                RegistryUrl = options.RegistryUrl,
                AuthToken = options.AuthToken,
                OneTimePassword = otp.Trim(),
                Timeout = options.Timeout,
                Disabled = options.Disabled,
                ProjectRoot = options.ProjectRoot,
            };

        using var client = new NpmRegistryClient(options);
        (string publishedWireName, string publishedVersion) = await client.PublishAsync(packageDir).ConfigureAwait(false);
        return new PackagePublishCliResult(publishedWireName, publishedVersion,
            NpmPackageName.TarballFileName(publishedWireName, publishedVersion), null, DryRun: false, Uploaded: true);
    }

    private static bool IsBareVersionPin(string specifier)
        => !specifier.Contains(':') && !specifier.Contains('/') && specifier.Length > 0;
}

// -------------------------------------------------------------------------------------------------
//  CLI DTOs (JsonOutput serializes these verbatim)
// -------------------------------------------------------------------------------------------------

public sealed record PackageSearchCliResult(int Total, IReadOnlyList<PackageSearchCliEntry> Results);
public sealed record PackageSearchCliEntry(string Name, string RegistryName, string LatestVersion, string? Description);
public sealed record PackageInfoCliResult(string Name, string RegistryName, string? LatestVersion,
    IReadOnlyList<PackageInfoCliVersion> Versions);
public sealed record PackageInfoCliVersion(string Version, string? Description, IReadOnlyList<string> Dependencies);
public sealed record PackageInstallCliResult(string Name, string Version, string Specifier, string? PreviousSpecifier,
    bool DryRun, string Note);
public sealed record PackageUpdateCliResult(int Updated, bool DryRun, IReadOnlyList<PackageUpdateCliChange> Changes);
public sealed record PackageUpdateCliChange(string Name, string FromVersion, string ToVersion, bool DryRun);
public sealed record PackagePublishCliResult(string RegistryName, string Version, string Tarball, long? TarballBytes,
    bool DryRun, bool Uploaded);
