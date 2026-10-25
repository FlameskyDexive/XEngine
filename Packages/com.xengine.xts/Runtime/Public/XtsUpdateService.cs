using System.Runtime.InteropServices;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace XEngine.XTS;

/// <summary>
/// Content update pipeline (plan §4): a downloaded candidate content
/// directory is verified (manifest signature + every file hash), then
/// atomically promoted to <c>current</c>; the previous content is kept as
/// <c>previous</c>. <see cref="VerifyCurrent"/> re-checks the shipped content
/// at startup and automatically falls back to <c>previous</c> (or refuses
/// into factory content) when the current content is missing or corrupted —
/// a bad update can never brick the install.
///
/// Layout under the update root:
/// <code>
///   current/manifest.json + files   (activated content)
///   previous/manifest.json + files  (one-generation rollback copy)
///   factory/manifest.json + files   (shipped baseline, never modified)
/// </code>
/// </summary>
public sealed class XtsUpdateService
{
    private readonly string _root;
    private readonly string _signingKey;

    public XtsUpdateService(string root, string signingKey)
    {
        _root = root;
        _signingKey = signingKey;
        Directory.CreateDirectory(Path.Combine(_root, "factory"));
        Directory.CreateDirectory(Path.Combine(_root, "current"));
        Directory.CreateDirectory(Path.Combine(_root, "previous"));
    }

    public string CurrentRoot => Path.Combine(_root, "current");
    public string PreviousRoot => Path.Combine(_root, "previous");

    /// <summary>Seed the immutable factory baseline (first install).</summary>
    public void SeedFactory(string contentDir, string version)
    {
        CopyDirectory(contentDir, Path.Combine(_root, "factory"), overwrite: false);
        WriteManifest(Path.Combine(_root, "factory"), version);
        // first activation: factory becomes current
        if (!File.Exists(Path.Combine(CurrentRoot, XtsContentManifest.ManifestFileName)))
            CopyDirectory(Path.Combine(_root, "factory"), CurrentRoot, overwrite: true);
    }

    /// <summary>
    /// Verify and activate a candidate: signature first, then every file
    /// hash. Returns the list of problems; empty = activated.
    /// </summary>
    public List<string> ActivateCandidate(string candidateDir, string version)
    {
        var errors = VerifyContent(candidateDir, version);
        if (errors.Count > 0)
            return errors;

        // previous := current (best effort); current := candidate
        if (Directory.Exists(PreviousRoot))
            Directory.Delete(PreviousRoot, true);
        if (Directory.Exists(CurrentRoot) && Directory.EnumerateFileSystemEntries(CurrentRoot).Any())
            CopyDirectory(CurrentRoot, PreviousRoot, overwrite: true);
        Directory.Delete(CurrentRoot, true);
        CopyDirectory(candidateDir, CurrentRoot, overwrite: true);
        return errors;
    }

    /// <summary>Verify the current content. Empty list = healthy.</summary>
    public List<string> VerifyCurrent() => VerifyContent(CurrentRoot, version: null);

    /// <summary>
    /// Startup recovery: verify current; on failure, roll back to previous
    /// (or factory). Returns the directory the host should load plus the
    /// problems that triggered the fallback (empty when healthy).
    /// </summary>
    public (string ContentRoot, List<string> Problems) LoadCurrentWithRollback()
    {
        var problems = VerifyCurrent();
        if (problems.Count == 0)
            return (CurrentRoot, problems);

        // fall back: previous, else factory
        foreach (var fallback in new[] { PreviousRoot, Path.Combine(_root, "factory") })
        {
            var fallbackErrors = VerifyContent(fallback, version: null);
            if (fallbackErrors.Count == 0)
            {
                Directory.Delete(CurrentRoot, true);
                CopyDirectory(fallback, CurrentRoot, overwrite: true);
                problems.Insert(0, $"current content failed verification; rolled back to {Path.GetFileName(fallback)}");
                return (CurrentRoot, problems);
            }
        }
        problems.Insert(0, "current content failed verification and no intact fallback exists");
        return (CurrentRoot, problems);
    }

    /// <summary>Signature + hash verification of a content directory.</summary>
    public List<string> VerifyContent(string contentDir, string? version)
    {
        var errors = new List<string>();
        var manifestPath = Path.Combine(contentDir, XtsContentManifest.ManifestFileName);
        if (!File.Exists(manifestPath))
            return new List<string> { $"content manifest missing under {contentDir}" };

        XtsContentManifest manifest;
        try
        {
            manifest = XtsContentManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception e)
        {
            return new List<string> { $"content manifest unreadable: {e.Message}" };
        }

        if (manifest.Schema != XtsContentManifest.CurrentSchema)
            errors.Add($"content manifest schema '{manifest.Schema}' is not {XtsContentManifest.CurrentSchema}");
        if (version != null && manifest.Version != version)
            errors.Add($"content manifest version '{manifest.Version}' != expected '{version}'");
        if (!manifest.Verify(_signingKey))
        {
            errors.Add("content manifest signature verification failed — content is not from the trusted publisher");
            return errors; // a bad signature invalidates every hash claim
        }

        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(contentDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                errors.Add($"content file '{file.Path}' is missing");
                continue;
            }
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                errors.Add($"content file '{file.Path}' hash mismatch (tampered or corrupt)");
        }
        return errors;
    }

    /// <summary>Write manifest.json (signed) for the files currently in <paramref name="contentDir"/>.</summary>
    public void WriteManifest(string contentDir, string version)
    {
        var manifest = new XtsContentManifest
        {
            Version = version,
            Files = Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(XtsContentManifest.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .Select(f => new XtsContentFile
                {
                    Path = Path.GetRelativePath(contentDir, f).Replace('\\', '/'),
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant(),
                })
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToList(),
        };
        manifest.Sign(_signingKey);
        File.WriteAllText(Path.Combine(contentDir, XtsContentManifest.ManifestFileName), manifest.ToJson());
    }

    private static void CopyDirectory(string source, string target, bool overwrite)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite);
        }
    }
}
