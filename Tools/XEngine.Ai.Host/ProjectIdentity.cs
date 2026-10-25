using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Inspects a target XEngine project and produces a stable <see cref="ProjectDescriptor"/>.
/// De-Unity-ified from XMcp.ProjectIdentity: instead of ProjectVersion.txt it reads the
/// <c>*.xengine</c> project file (JSON: name/engine/version) + the solution file at the repo
/// root. The fingerprint is a 16-char SHA-256 truncation over (normalizedRoot, engineVersion)
/// so the same logical project yields a stable id across runs (XMcp pattern).
/// </summary>
internal static class ProjectIdentity
{
    public static ProjectDescriptor Inspect(string projectRoot)
    {
        string normalizedRoot = NormalizeRoot(projectRoot);
        string engineVersion = ReadEngineVersion(normalizedRoot);
        string? solutionPath = FindSolution(normalizedRoot);
        string fingerprint = ComputeFingerprint(normalizedRoot, engineVersion);

        return new ProjectDescriptor(
            ProjectId: fingerprint[..16],
            ProjectRoot: normalizedRoot,
            EngineVersion: engineVersion,
            SolutionPath: solutionPath,
            GitBranch: ReadGitBranch(normalizedRoot),
            Fingerprint: fingerprint);
    }

    public static string NormalizeRoot(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        string fullPath = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Project directory not found: {fullPath}");
        }

        return fullPath;
    }

    public static string ComputeFingerprint(string normalizedRoot, string? engineVersion)
    {
        // Same canonical form as XMcp: uppercase root + engine version, SHA-256, lowercase hex.
        string canonical = $"{normalizedRoot.ToUpperInvariant()}\n{engineVersion ?? string.Empty}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Reads the engine version from the <c>*.xengine</c> project file (JSON: {engine,version}).
    /// Returns "unknown" when no project file is present (e.g. running against the engine
    /// repo itself rather than a game project).
    /// </summary>
    private static string ReadEngineVersion(string projectRoot)
    {
        string? xengineFile = Directory.GetFiles(projectRoot, "*.xengine").FirstOrDefault();
        if (xengineFile is null)
        {
            return "unknown";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(xengineFile));
            if (doc.RootElement.TryGetProperty("version", out JsonElement versionEl)
                && versionEl.ValueKind == JsonValueKind.String)
            {
                string? v = versionEl.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed project file: fall through to "unknown".
        }

        return "unknown";
    }

    /// <summary>Locates XEngine.sln at the project root (the Roslyn code index needs it).</summary>
    private static string? FindSolution(string projectRoot)
    {
        string path = Path.Combine(projectRoot, "XEngine.sln");
        return File.Exists(path) ? path : null;
    }

    private static string? ReadGitBranch(string projectRoot)
    {
        string headPath = Path.Combine(projectRoot, ".git", "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        string value = File.ReadAllText(headPath).Trim();
        const string prefix = "ref: refs/heads/";
        return value.StartsWith(prefix, StringComparison.Ordinal)
            ? value[prefix.Length..]
            : value.Length >= 12 ? value[..12] : value;
    }
}
