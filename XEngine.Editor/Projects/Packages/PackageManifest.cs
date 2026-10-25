// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// Per-project dependency manifest stored at <c>Packages/manifest.json</c>. Declares which packages
/// the project consumes, their version specifiers, and (optionally) scoped registries / testables.
/// Pure JSON (not Echo) to match the npm/UPM convention: git-diffable and AI-editable with zero
/// format-specific knowledge.
/// </summary>
public sealed class ProjectPackageManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Package name -> source/version specifier. Recognised specifier forms:
    /// <list type="bullet">
    /// <item><c>"1.2.3"</c> / <c>"1.2.x"</c> — registry pin (P0: resolves to an embed package of the same name).</item>
    /// <item><c>"file:../path"</c> — local folder reference.</item>
    /// <item><c>"embed:name"</c> — explicit embed marker (Packages/&lt;name&gt;).</item>
    /// <item><c>"git@url#ref"</c> — git source (P1+).</item>
    /// </list>
    /// A missing entry for a Packages/&lt;name&gt; folder still resolves as embed by default.</summary>
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = new();

    /// <summary>Package names whose <c>Tests/</c> subfolder participates in the test assemblies.</summary>
    [JsonPropertyName("testables")]
    public List<string> Testables { get; set; } = new();

    /// <summary>Optional scoped registries (P2+). Keyed by registry nickname.</summary>
    [JsonPropertyName("scopedRegistries")]
    public Dictionary<string, ScopedRegistry>? ScopedRegistries { get; set; }

    // ------------------------------------------------------------------
    //  IO
    // ------------------------------------------------------------------

    public static ProjectPackageManifest Empty() => new();

    /// <summary>Read <c>manifest.json</c> from disk, returning an empty manifest when the file is
    /// missing (a project with no manifest consumes no packages — backward compatible).</summary>
    public static ProjectPackageManifest ReadOrDefault(string path)
    {
        if (!File.Exists(path)) return Empty();
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return Empty();
            return JsonSerializer.Deserialize<ProjectPackageManifest>(json, JsonOptions) ?? Empty();
        }
        catch (JsonException ex)
        {
            Runtime.Debug.LogError($"[Packages] Failed to parse manifest '{path}': {ex.Message}. No packages loaded.");
            return Empty();
        }
        catch (IOException ex)
        {
            Runtime.Debug.LogError($"[Packages] Could not read manifest '{path}': {ex.Message}.");
            return Empty();
        }
    }

    public void Write(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions) + "\n");
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

/// <summary>A named scoped registry entry from the manifest.</summary>
public sealed class ScopedRegistry
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = new();
}
