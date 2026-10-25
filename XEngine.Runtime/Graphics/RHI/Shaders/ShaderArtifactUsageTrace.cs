// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>
/// Development-only persistent record of unique offline shader artifact lookups. The JSONL schema
/// deliberately contains only the stable lookup descriptor so a trace can be reviewed and replayed
/// by a later build without tying it to a device-specific timestamp or path.
/// </summary>
public sealed class ShaderArtifactUsageTrace : IDisposable
{
    public const int SchemaVersion = 1;
    public const string FileName = "shader-variant-usage.jsonl";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly HashSet<LookupIdentity> _seen = [];
    private bool _disposed;

    public ShaderArtifactUsageTrace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Shader usage trace must have a parent directory.", nameof(path));

        Directory.CreateDirectory(directory);
        LoadExistingIdentities(Path, _seen);
        _stream = new FileStream(
            Path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4 * 1024,
            FileOptions.WriteThrough);
    }

    public string Path { get; }

    /// <summary>Records a descriptor once for the lifetime of this process and flushes it durably.</summary>
    public void Record(ShaderArtifactRuntimeLookup lookup)
    {
        var identity = new LookupIdentity(
            lookup.ShaderName,
            lookup.PassName,
            lookup.Backend,
            lookup.VariantKey);
        lock (_gate)
        {
            if (_disposed || !_seen.Add(identity))
                return;

            byte[] line = JsonSerializer.SerializeToUtf8Bytes(
                new UsageRecord(
                    SchemaVersion,
                    lookup.ShaderName,
                    lookup.PassName,
                    lookup.Backend.ToString(),
                    lookup.VariantKey,
                    lookup.Found),
                s_jsonOptions);
            _stream.Write(line);
            _stream.WriteByte((byte)'\n');
            _stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stream.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private static void LoadExistingIdentities(string path, HashSet<LookupIdentity> seen)
    {
        if (!File.Exists(path))
            return;

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("schemaVersion", out JsonElement schemaElement) ||
                        !schemaElement.TryGetInt32(out int schemaVersion) ||
                        schemaVersion != SchemaVersion ||
                        !TryReadString(root, "shaderName", out string shaderName) ||
                        !TryReadString(root, "passName", out string passName) ||
                        !TryReadString(root, "backend", out string backendName) ||
                        !TryReadString(root, "variantKey", out string variantKey) ||
                        !root.TryGetProperty("found", out JsonElement foundElement) ||
                        (foundElement.ValueKind != JsonValueKind.True && foundElement.ValueKind != JsonValueKind.False) ||
                        !Enum.TryParse(backendName, out GraphicsBackend backend))
                    {
                        continue;
                    }

                    seen.Add(new LookupIdentity(shaderName, passName, backend, variantKey));
                }
                catch (JsonException)
                {
                    // A partially-written Development line is rejected by the build importer,
                    // but must not prevent the Player from collecting later valid lookups.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.LogWarning($"[ShaderArtifacts] Existing usage trace cannot be read: {exception.Message}");
        }
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value) || propertyName == "variantKey";
    }

    private readonly record struct LookupIdentity(
        string ShaderName,
        string PassName,
        GraphicsBackend Backend,
        string VariantKey);

    private sealed record UsageRecord(
        int SchemaVersion,
        string ShaderName,
        string PassName,
        string Backend,
        string VariantKey,
        bool Found);
}
