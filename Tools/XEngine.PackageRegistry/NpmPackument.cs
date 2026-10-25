// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XEngine.PackageRegistry;

/// <summary>One version entry of a registry packument (the <c>versions["1.2.3"]</c> object).</summary>
public sealed class NpmVersionInfo
{
    public required string Version { get; init; }

    /// <summary>Dependencies exactly as stored on the registry (wire names, <c>@inxep/*</c>).</summary>
    public IReadOnlyDictionary<string, string> Dependencies { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Absolute or registry-relative tarball URL from <c>dist.tarball</c>.</summary>
    public string? TarballUrl { get; init; }

    /// <summary>SRI string from <c>dist.integrity</c> (e.g. <c>sha512-…</c>).</summary>
    public string? Integrity { get; init; }

    public string? Description { get; init; }

    /// <summary>Dependencies mapped to engine names (<c>@inxep/spine</c> → <c>com.xengine.spine</c>;
    /// other names unchanged).</summary>
    public IReadOnlyDictionary<string, string> EngineDependencies()
    {
        if (Dependencies.Count == 0) return Dependencies;
        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> dependency in Dependencies)
            mapped[NpmPackageName.FromNpmName(dependency.Key)] = dependency.Value;
        return mapped;
    }
}

/// <summary>The full packument npm serves at <c>GET /{name}</c>.</summary>
public sealed class NpmPackument
{
    /// <summary>Package name as served (wire name).</summary>
    public required string Name { get; init; }

    /// <summary>Engine-mapped name (<c>@inxep/spine</c> → <c>com.xengine.spine</c>).</summary>
    public string EngineName => NpmPackageName.FromNpmName(Name);

    public IReadOnlyDictionary<string, string> DistTags { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<NpmVersionInfo> Versions { get; init; } = Array.Empty<NpmVersionInfo>();

    public string? LatestVersion => DistTags.TryGetValue("latest", out string? latest) ? latest : null;

    /// <summary>Tolerant parser: skips versions whose metadata is malformed instead of failing the
    /// whole document, mirroring how npm clients degrade. Returns null when the document has no
    /// usable name or no parsable versions (callers treat null as "not on this registry").</summary>
    public static NpmPackument? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("name", out JsonElement nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
                return null;

            var versions = new List<NpmVersionInfo>();
            if (root.TryGetProperty("versions", out JsonElement versionsElement)
                && versionsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty versionProperty in versionsElement.EnumerateObject())
                {
                    if (versionProperty.Value.ValueKind != JsonValueKind.Object) continue;
                    JsonElement version = versionProperty.Value;
                    if (!version.TryGetProperty("version", out JsonElement versionString)
                        || versionString.ValueKind != JsonValueKind.String)
                        continue;

                    var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (version.TryGetProperty("dependencies", out JsonElement depsElement)
                        && depsElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty dep in depsElement.EnumerateObject())
                        {
                            if (dep.Value.ValueKind == JsonValueKind.String)
                                dependencies[dep.Name] = dep.Value.GetString() ?? "*";
                        }
                    }

                    string? tarball = null, integrity = null;
                    if (version.TryGetProperty("dist", out JsonElement dist)
                        && dist.ValueKind == JsonValueKind.Object)
                    {
                        if (dist.TryGetProperty("tarball", out JsonElement tarballElement)
                            && tarballElement.ValueKind == JsonValueKind.String)
                            tarball = tarballElement.GetString();
                        if (dist.TryGetProperty("integrity", out JsonElement integrityElement)
                            && integrityElement.ValueKind == JsonValueKind.String)
                            integrity = integrityElement.GetString();
                    }

                    string? description = version.TryGetProperty("description", out JsonElement descriptionElement)
                        && descriptionElement.ValueKind == JsonValueKind.String
                        ? descriptionElement.GetString()
                        : null;

                    versions.Add(new NpmVersionInfo
                    {
                        Version = versionString.GetString() ?? versionProperty.Name,
                        Dependencies = dependencies,
                        TarballUrl = tarball,
                        Integrity = integrity,
                        Description = description,
                    });
                }
            }

            if (versions.Count == 0) return null;

            var distTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("dist-tags", out JsonElement tags)
                && tags.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty tag in tags.EnumerateObject())
                {
                    if (tag.Value.ValueKind == JsonValueKind.String)
                        distTags[tag.Name] = tag.Value.GetString() ?? "";
                }
            }

            return new NpmPackument
            {
                Name = nameElement.GetString()!,
                DistTags = distTags,
                Versions = versions,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static NpmPackument? ReadFile(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
}

/// <summary>One hit from the registry search endpoint (<c>/-/v1/search</c>).</summary>
public sealed class NpmSearchResult
{
    public required string Name { get; init; }
    public string EngineName => NpmPackageName.FromNpmName(Name);
    public required string Version { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    public static IReadOnlyList<NpmSearchResult> Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("objects", out JsonElement objects)
                || objects.ValueKind != JsonValueKind.Array)
                return Array.Empty<NpmSearchResult>();

            var results = new List<NpmSearchResult>();
            foreach (JsonElement item in objects.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("package", out JsonElement package)
                    || package.ValueKind != JsonValueKind.Object) continue;
                if (!package.TryGetProperty("name", out JsonElement name)
                    || name.ValueKind != JsonValueKind.String) continue;

                string? version = package.TryGetProperty("version", out JsonElement versionElement)
                    && versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString()
                    : null;
                string? description = package.TryGetProperty("description", out JsonElement descriptionElement)
                    && descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString()
                    : null;

                var keywords = new List<string>();
                if (package.TryGetProperty("keywords", out JsonElement keywordsElement)
                    && keywordsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement keyword in keywordsElement.EnumerateArray())
                        if (keyword.ValueKind == JsonValueKind.String && keyword.GetString() is { Length: > 0 } k)
                            keywords.Add(k);
                }

                results.Add(new NpmSearchResult
                {
                    Name = name.GetString()!,
                    Version = version ?? "0.0.0",
                    Description = description,
                    Keywords = keywords,
                });
            }
            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<NpmSearchResult>();
        }
    }
}
