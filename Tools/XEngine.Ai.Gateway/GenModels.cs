namespace XEngine.Ai.Gateway;

/// <summary>Lifecycle of a generation task, normalized across providers.</summary>
public enum GenTaskState
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>Result of submitting a recipe to a provider.</summary>
public sealed record GenSubmitResult(
    /// <summary>Provider task id — the handle for gen_status / gen_fetch.</summary>
    string TaskId,
    /// <summary>Provider credit/cost estimate, when the API reports one.</summary>
    double? EstimatedCredits = null);

/// <summary>Normalized status snapshot of a provider task.</summary>
public sealed record GenTaskStatus(
    string TaskId,
    GenTaskState State,
    /// <summary>0-100 when the provider reports progress; null otherwise.</summary>
    int? Progress = null,
    /// <summary>Provider status detail or failure reason.</summary>
    string? Message = null);

/// <summary>One downloaded output file of a finished task.</summary>
public sealed record GenArtifact(
    /// <summary>What the file is: model | texture | skybox | audio | thumbnail | metadata.</summary>
    string Role,
    /// <summary>Absolute local path the provider wrote the file to.</summary>
    string LocalPath,
    string? MimeType = null);
