namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P1.4 — Base for providers whose HTTP API is *synchronous* (PixelLab, ElevenLabs): the
/// generation happens inside <see cref="SubmitAsync"/> and results are written to an on-disk cache
/// keyed by the recipe hash, because the CLI process that submits exits before the one that
/// fetches. Poll reports Succeeded when the cache exists; fetch copies out of it. The cache lives
/// under .xengine/ (gitignored) beside the task ledger, so idempotent resubmission is free and
/// re-fetch works across processes.
/// </summary>
public abstract class CachedResultGenProvider : IGenProvider
{
    private readonly string _cacheRoot;

    /// <param name="cacheRoot">Directory for cached results (e.g. {project}/.xengine/gen-cache).</param>
    protected CachedResultGenProvider(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
    }

    public abstract string Name { get; }

    public abstract IReadOnlyList<string> SupportedKinds { get; }

    /// <summary>Run the synchronous generation and return the artifacts to cache:
    /// (fileName with extension, bytes, mime, role) per output.</summary>
    protected abstract Task<IReadOnlyList<(string FileName, byte[] Bytes, string Mime, string Role)>> GenerateAsync(
        GenRecipe recipe, CancellationToken cancellationToken);

    public async Task<GenSubmitResult> SubmitAsync(GenRecipe recipe, CancellationToken cancellationToken)
    {
        string hash = recipe.ComputeInputHash();
        string taskId = $"{Name}-{hash[..16]}";
        string dir = CacheDir(taskId);

        // Same recipe → same cache: a resubmission that slipped past the task-store dedupe
        // (deleted ledger, cross-project copy) must not bill again.
        if (!HasCachedResult(dir))
        {
            IReadOnlyList<(string FileName, byte[] Bytes, string Mime, string Role)> artifacts =
                await GenerateAsync(recipe, cancellationToken).ConfigureAwait(false);
            if (artifacts.Count == 0)
            {
                throw new InvalidOperationException($"{Name} generation produced no artifacts.");
            }

            Directory.CreateDirectory(dir);
            var manifest = new StringBuilder();
            foreach ((string fileName, byte[] bytes, string mime, string role) in artifacts)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes, cancellationToken).ConfigureAwait(false);
                manifest.Append(fileName).Append('\t').Append(mime).Append('\t').Append(role).Append('\n');
            }

            // Manifest written last = commit marker; a crash mid-write never yields a half cache.
            await File.WriteAllTextAsync(Path.Combine(dir, "manifest.tsv"), manifest.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }

        return new GenSubmitResult(taskId);
    }

    public Task<GenTaskStatus> PollAsync(string taskId, CancellationToken cancellationToken)
    {
        bool cached = HasCachedResult(CacheDir(taskId));
        return Task.FromResult(cached
            ? new GenTaskStatus(taskId, GenTaskState.Succeeded, 100, $"{Name}: cached result ready")
            : new GenTaskStatus(taskId, GenTaskState.Failed, null,
                $"{Name}: no cached result for '{taskId}' — {Name} generates synchronously at submit, " +
                "so a missing cache means the submit never completed. Resubmit the recipe."));
    }

    public Task<IReadOnlyList<GenArtifact>> FetchAsync(
        string taskId, GenRecipe recipe, string downloadDirectory, CancellationToken cancellationToken)
    {
        string dir = CacheDir(taskId);
        if (!HasCachedResult(dir))
        {
            throw new InvalidOperationException(
                $"{Name} task '{taskId}' has no cached result. Resubmit the recipe (synchronous provider).");
        }

        Directory.CreateDirectory(downloadDirectory);
        var artifacts = new List<GenArtifact>();
        foreach (string line in File.ReadAllLines(Path.Combine(dir, "manifest.tsv")))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            string source = Path.Combine(dir, parts[0]);
            string target = Path.Combine(downloadDirectory, parts[0]);
            File.Copy(source, target, overwrite: true);
            artifacts.Add(new GenArtifact(parts[2], target, parts[1]));
        }

        return Task.FromResult<IReadOnlyList<GenArtifact>>(artifacts);
    }

    private string CacheDir(string taskId)
    {
        // Task ids are provider-generated ({name}-{hex16}) — path-safe by construction, but guard
        // anyway against hand-edited ledgers.
        foreach (char c in taskId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                throw new ArgumentException($"'{taskId}' is not a valid {Name} task id.");
            }
        }

        return Path.Combine(_cacheRoot, taskId);
    }

    private static bool HasCachedResult(string dir) => File.Exists(Path.Combine(dir, "manifest.tsv"));
}
