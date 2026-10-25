using System;
using System.Collections.Generic;
using System.Text.Json;

namespace XEngine.Ai.Protocol;

/// <summary>
/// Wire-level constants for the Windows three.js authoring host. The contract is deliberately
/// engine-agnostic so the editor, CLI and a future remote host cannot accidentally take a Runtime
/// or WebView2 dependency.
/// </summary>
public static class ThreeAuthoringProtocol
{
    public const string Version = "three-authoring/1";
    public const int SchemaVersion = 1;
    public const int DefaultTimeoutMs = 120_000;
    public const int MaxTimeoutMs = 900_000;
    public const int MaxPreviewDimension = 4096;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

/// <summary>One operation handled by the shared authoring host.</summary>
public enum ThreeAuthoringOperation
{
    Validate,
    Preview,
    Export,
    Bake,
}

/// <summary>
/// Request sent from the editor/CLI to the Windows authoring host. Paths are project-relative
/// forward-slash paths; the host resolves them below the project root and never accepts a rooted
/// path or a parent traversal.
/// </summary>
public sealed record ThreeAuthoringRequest(
    string ProtocolVersion,
    string RequestId,
    string Operation,
    string SourcePath,
    string? OutputDirectory,
    string? PreviewId,
    double TimeSeconds,
    int Width,
    int Height,
    int TimeoutMs,
    string? Intent,
    IReadOnlyDictionary<string, string>? Options);

/// <summary>Actionable host diagnostic. Severity is error, warning or info.</summary>
public sealed record ThreeAuthoringDiagnostic(
    string Code,
    string Severity,
    string Message,
    string? Path = null);

/// <summary>One output in a validated, atomically imported export manifest.</summary>
public sealed record ThreeArtifactEntry(
    string StableId,
    string RelativePath,
    string Kind,
    string Status,
    string ContentHash,
    IReadOnlyList<string> Dependencies);

/// <summary>
/// Deterministic export description. The source and tool hashes are part of the cache identity;
/// a consumer must not treat a manifest as valid when either version changes.
/// </summary>
public sealed record ThreeExportManifest(
    int SchemaVersion,
    string ProtocolVersion,
    string ThreeVersion,
    string ExporterVersion,
    string HostVersion,
    string SourceHash,
    IReadOnlyList<ThreeArtifactEntry> Artifacts,
    IReadOnlyList<ThreeAuthoringDiagnostic> Diagnostics);

/// <summary>Response envelope shared by preview, validation, export and bake.</summary>
public sealed record ThreeAuthoringResponse(
    string ProtocolVersion,
    string RequestId,
    string Operation,
    bool Succeeded,
    string? ErrorCode,
    string? Message,
    ThreeExportManifest? Manifest,
    IReadOnlyList<ThreeAuthoringDiagnostic> Diagnostics);

/// <summary>Pure validation for the host boundary; it performs no filesystem or process work.</summary>
public static class ThreeAuthoringRequestValidator
{
    public static IReadOnlyList<ThreeAuthoringDiagnostic> Validate(ThreeAuthoringRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<ThreeAuthoringDiagnostic>();

        if (!string.Equals(request.ProtocolVersion, ThreeAuthoringProtocol.Version, StringComparison.Ordinal))
            Add(diagnostics, "protocol_version", "error", "Unsupported authoring protocol version.");

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
            Add(diagnostics, "request_id", "error", "RequestId must contain 1 to 128 characters.");

        if (!Enum.TryParse(request.Operation, ignoreCase: true, out ThreeAuthoringOperation operation))
            Add(diagnostics, "operation", "error", "Operation must be validate, preview, export or bake.");

        ValidateRelativePath(request.SourcePath, "source_path", diagnostics, required: true);

        if (operation is ThreeAuthoringOperation.Export or ThreeAuthoringOperation.Bake)
            ValidateRelativePath(request.OutputDirectory, "output_directory", diagnostics, required: true);
        else if (!string.IsNullOrWhiteSpace(request.OutputDirectory))
            ValidateRelativePath(request.OutputDirectory, "output_directory", diagnostics, required: false);

        if (request.TimeSeconds < 0 || double.IsNaN(request.TimeSeconds) || double.IsInfinity(request.TimeSeconds))
            Add(diagnostics, "time_seconds", "error", "TimeSeconds must be a finite non-negative value.");

        if (operation is ThreeAuthoringOperation.Preview or ThreeAuthoringOperation.Bake)
        {
            ValidateDimension(request.Width, "width", diagnostics);
            ValidateDimension(request.Height, "height", diagnostics);
        }

        if (request.TimeoutMs is < 1 or > ThreeAuthoringProtocol.MaxTimeoutMs)
            Add(diagnostics, "timeout_ms", "error", $"TimeoutMs must be between 1 and {ThreeAuthoringProtocol.MaxTimeoutMs}.");

        return diagnostics;
    }

    private static void ValidateDimension(int value, string field, List<ThreeAuthoringDiagnostic> diagnostics)
    {
        if (value is < 1 or > ThreeAuthoringProtocol.MaxPreviewDimension)
            Add(diagnostics, field, "error", $"{field} must be between 1 and {ThreeAuthoringProtocol.MaxPreviewDimension}.");
    }

    private static void ValidateRelativePath(
        string? value,
        string field,
        List<ThreeAuthoringDiagnostic> diagnostics,
        bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
                Add(diagnostics, field, "error", $"{field} is required.");
            return;
        }

        string normalized = value.Replace('\\', '/');
        if (System.IO.Path.IsPathRooted(value)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Equals("..", StringComparison.Ordinal)
            || normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal))
        {
            Add(diagnostics, field, "error", $"{field} must stay below the project root.");
        }
    }

    private static void Add(
        List<ThreeAuthoringDiagnostic> diagnostics,
        string code,
        string severity,
        string message)
        => diagnostics.Add(new ThreeAuthoringDiagnostic(code, severity, message));
}
