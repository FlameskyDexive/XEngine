namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P1.1 — One external (or local) generation service, adapted to the pipeline's canonical
/// submit → poll → fetch shape. Implementations own the HTTP specifics; callers own persistence
/// (the <see cref="GenTaskStore"/> keeps the recipe + task id between CLI processes) and pass the
/// recipe back in where a stateless provider needs it.
/// </summary>
public interface IGenProvider
{
    /// <summary>Registry name, lowercase (e.g. "meshy", "pixellab", "mock").</summary>
    string Name { get; }

    /// <summary>Which <see cref="GenRecipe.Kinds"/> this provider can generate.</summary>
    IReadOnlyList<string> SupportedKinds { get; }

    /// <summary>Start a generation. Returns the provider task id (+ credit estimate when known).
    /// Must NOT be called for a recipe kind outside <see cref="SupportedKinds"/>.</summary>
    Task<GenSubmitResult> SubmitAsync(GenRecipe recipe, CancellationToken cancellationToken);

    /// <summary>Normalized status of a previously submitted task.</summary>
    Task<GenTaskStatus> PollAsync(string taskId, CancellationToken cancellationToken);

    /// <summary>Download every output of a succeeded task into <paramref name="downloadDirectory"/>
    /// (created if missing). The recipe is passed back in so stateless providers know what was
    /// requested (kind, format options) without server round-trips.</summary>
    Task<IReadOnlyList<GenArtifact>> FetchAsync(
        string taskId, GenRecipe recipe, string downloadDirectory, CancellationToken cancellationToken);
}
