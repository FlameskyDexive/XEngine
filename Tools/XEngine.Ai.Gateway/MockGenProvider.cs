using System.Collections.Concurrent;

namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P1.1 — The offline provider: no network, no key, deterministic outputs. Exists so the
/// whole gen_submit → gen_status → gen_fetch chain (and every workflow built on it) can run in
/// tests, CI, and keyless environments. Task ids derive from the recipe hash, so idempotent
/// resubmission is observable end-to-end; artifacts are tiny valid GLB/PNG/WAV files that the
/// real import pipeline accepts.
/// </summary>
public sealed class MockGenProvider : IGenProvider
{
    public const string ProviderName = "mock";

    private readonly int _succeedAfterPolls;
    private readonly ConcurrentDictionary<string, int> _pollCounts = new(StringComparer.Ordinal);

    /// <param name="succeedAfterPolls">Polls that report Running before Succeeded — 0 (default)
    /// succeeds immediately; tests use higher values to exercise the pending path.</param>
    public MockGenProvider(int succeedAfterPolls = 0)
    {
        _succeedAfterPolls = succeedAfterPolls;
    }

    public string Name => ProviderName;

    public IReadOnlyList<string> SupportedKinds => GenRecipe.Kinds.All;

    public Task<GenSubmitResult> SubmitAsync(GenRecipe recipe, CancellationToken cancellationToken)
    {
        // Hash-derived id: same recipe → same task id, making billing dedupe trivially testable.
        string taskId = "mock-" + recipe.ComputeInputHash()[..16];
        return Task.FromResult(new GenSubmitResult(taskId, EstimatedCredits: 0));
    }

    public Task<GenTaskStatus> PollAsync(string taskId, CancellationToken cancellationToken)
    {
        int polls = _pollCounts.AddOrUpdate(taskId, 1, (_, n) => n + 1);
        if (polls <= _succeedAfterPolls)
        {
            int progress = (int)(100.0 * polls / (_succeedAfterPolls + 1));
            return Task.FromResult(new GenTaskStatus(taskId, GenTaskState.Running, progress, "mock: generating"));
        }

        return Task.FromResult(new GenTaskStatus(taskId, GenTaskState.Succeeded, 100, "mock: done"));
    }

    public Task<IReadOnlyList<GenArtifact>> FetchAsync(
        string taskId, GenRecipe recipe, string downloadDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(downloadDirectory);

        (string role, string extension, byte[] bytes, string mime) = recipe.Kind switch
        {
            GenRecipe.Kinds.Motion
                => ("model", ".glb", MockArtifacts.AnimatedGlb(), "model/gltf-binary"),
            GenRecipe.Kinds.Model3d
                => ("model", ".glb", ModelBytes(recipe), "model/gltf-binary"),
            GenRecipe.Kinds.Sprite
                => ("sprite", ".png", MockArtifacts.SpritesheetPng(), "image/png"),
            GenRecipe.Kinds.Texture
                => ("texture", ".png", MockArtifacts.Png1x1(), "image/png"),
            GenRecipe.Kinds.Skybox
                => ("skybox", ".png", MockArtifacts.Png1x1(), "image/png"),
            GenRecipe.Kinds.Sfx
                => ("audio", ".wav", MockArtifacts.SilentWav(), "audio/wav"),
            _ => throw new InvalidOperationException($"Mock provider cannot fabricate kind '{recipe.Kind}'."),
        };

        string path = Path.Combine(downloadDirectory, taskId + extension);
        File.WriteAllBytes(path, bytes);
        IReadOnlyList<GenArtifact> artifacts = new[] { new GenArtifact(role, path, mime) };
        return Task.FromResult(artifacts);
    }

    /// <summary>Default mock model is the 1-triangle GLB. <c>extra.polycount=high</c> yields the
    /// 12-triangle unit cube so budget-gate tests have something to remesh.</summary>
    private static byte[] ModelBytes(GenRecipe recipe)
    {
        if (recipe.Extra is not null
            && recipe.Extra.TryGetValue("polycount", out string? poly)
            && string.Equals(poly, "high", StringComparison.OrdinalIgnoreCase))
        {
            return MockArtifacts.BoxGlb();
        }

        return MockArtifacts.MinimalGlb();
    }
}
