// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XEngine.Editor.Projects.Build.PlatformToolchains;

/// <summary>
/// Per-component install state stored at <c>&lt;installRoot&gt;/.state/&lt;id&gt;.json</c>. The
/// fingerprint hashes the component's acquisition spec (id + version + every source url and pinned
/// hash) so a matching state + a present sentinel means the component can be reused without any
/// network round-trip; a changed manifest spec invalidates the state and reinstalls.
/// </summary>
public sealed class ToolchainInstallState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
    /// <summary>Url the artifact was actually fetched from (first reachable source).</summary>
    [JsonPropertyName("sourceUrl")] public string SourceUrl { get; set; } = "";
    /// <summary>Actual sha256 of the downloaded artifact — pinned by the manifest when present,
    /// computed-and-recorded for unpinned vendor sources so subsequent installs verify against it.</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("installedAtUtc")] public string InstalledAtUtc { get; set; } = "";
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    /// <summary>sdkmanager components have no archive; their state is written by the Android channel.</summary>
    [JsonPropertyName("managedBy")] public string ManagedBy { get; set; } = "archive";

    public static ToolchainInstallState? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ToolchainInstallState>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Write(string path, ToolchainInstallState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions) + "\n");
    }

    /// <summary>Stable fingerprint of a component's acquisition spec: any change to version,
    /// sources, pinned hashes, layout, strip count or verify sentinel forces a reinstall.</summary>
    public static string ComputeFingerprint(ToolchainComponent component)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Append(string text) => hash.AppendData(System.Text.Encoding.UTF8.GetBytes(text + "\n"));
        Append(component.Id);
        Append(component.Version ?? "");
        Append(component.Kind.ToString());
        Append(component.Layout);
        Append(component.StripRootSegments.ToString());
        Append(component.UnpackZipsUnder ?? "");
        Append(component.Verify ?? "");
        foreach (ToolchainSource source in component.Sources)
        {
            Append(source.Url);
            Append(source.Sha256 ?? "");
        }
        foreach (string package in component.Packages)
            Append("pkg:" + package);
        byte[] digest = hash.GetHashAndReset();
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
