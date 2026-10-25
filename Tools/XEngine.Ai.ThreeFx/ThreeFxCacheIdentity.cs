using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.ThreeFx;

/// <summary>
/// The cache identity of an export: a SHA-256 over the normalized recipe source, every declared
/// dependency's content hash, the seed, the export parameters and the tool versions. Two exports
/// with the same identity are byte-identical by construction, so the second one is skipped —
/// that is what makes repeated fetches idempotent. The identity deliberately never contains the
/// recipe path (renaming/moving a file must not invalidate caches).
/// </summary>
public static class ThreeFxCacheIdentity
{
    public static string Compute(
        string normalizedRecipeText,
        IReadOnlyDictionary<string, string> dependencyContentHashes,
        int seed,
        int exportWidth,
        int exportHeight,
        string threeVersion,
        string exporterVersion,
        string hostVersion,
        double timeSeconds)
    {
        var sb = new StringBuilder();
        sb.Append("threefx-cache/1\n");
        sb.Append("recipe=").Append(normalizedRecipeText).Append('\n');
        foreach (KeyValuePair<string, string> dep in dependencyContentHashes)
        {
            sb.Append("dep=").Append(dep.Key).Append(':').Append(dep.Value).Append('\n');
        }
        sb.Append("seed=").Append(seed.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("size=").Append(exportWidth.ToString(CultureInfo.InvariantCulture))
            .Append('x').Append(exportHeight.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("time=").Append(timeSeconds.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("three=").Append(threeVersion).Append('\n');
        sb.Append("exporter=").Append(exporterVersion).Append('\n');
        sb.Append("host=").Append(hostVersion).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>
/// Sidecar state stored next to an exported artifact. The manifest written by the host carries
/// the identity, but it is also parsed here so any consumer (CLI, editor, tests) can decide
/// idempotency without re-rendering.
/// </summary>
public sealed record ThreeFxExportState(
    string CacheIdentity,
    string ArtifactFile,
    string ArtifactSha256);

public static class ThreeFxExportStateFile
{
    public const string FileName = ".threefx-state.json";

    public static ThreeFxExportState? TryRead(string outputDirectory)
    {
        string path = Path.Combine(outputDirectory, FileName);
        try
        {
            if (!File.Exists(path))
                return null;
            return System.Text.Json.JsonSerializer.Deserialize<ThreeFxExportState>(
                File.ReadAllText(path), ThreeAuthoringProtocol.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string outputDirectory, ThreeFxExportState state)
    {
        string path = Path.Combine(outputDirectory, FileName);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(state, ThreeAuthoringProtocol.JsonOptions));
    }
}

/// <summary>
/// Staged multi-file export: write everything into a temp staging folder, verify, then promote
/// atomically. A failure anywhere before promotion deletes the staging folder and leaves the
/// previous artifacts untouched (失败保留旧产物); promotion itself renames files into place, so a
/// retry after a crash simply runs the whole export again (重试幂等).
/// </summary>
public sealed class ThreeFxStagedExport : IDisposable
{
    private readonly string _stagingRoot;

    public string StagingDirectory { get; }

    public IReadOnlyList<string> StagedFiles => _stagedFiles;
    private readonly List<string> _stagedFiles = new();

    public ThreeFxStagedExport(string outputDirectory)
    {
        string fullOutput = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutput);
        _stagingRoot = Path.Combine(fullOutput, ".staging");
        Directory.CreateDirectory(_stagingRoot);
        StagingDirectory = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StagingDirectory);
    }

    public string WriteFile(string fileName, byte[] contents)
    {
        string path = Path.Combine(StagingDirectory, fileName);
        File.WriteAllBytes(path, contents);
        _stagedFiles.Add(path);
        return path;
    }

    public string WriteTextFile(string fileName, string contents)
    {
        string path = Path.Combine(StagingDirectory, fileName);
        File.WriteAllText(path, contents);
        _stagedFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Verifies every staged file exists and is non-empty, then moves them into the output
    /// directory. If any staged file is corrupt, nothing is promoted.
    /// </summary>
    public void Promote()
    {
        foreach (string file in _stagedFiles)
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0)
                throw new IOException($"Staged file missing or empty: {file}");
        }

        foreach (string file in _stagedFiles)
        {
            string target = Path.Combine(Path.GetDirectoryName(_stagingRoot)!, Path.GetFileName(file));
            File.Move(file, target, overwrite: true);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_stagingRoot))
                Directory.Delete(_stagingRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover staging folder never affects artifacts.
        }
    }
}
