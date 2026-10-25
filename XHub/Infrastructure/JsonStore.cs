using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using XHub.Platform;

namespace XHub.Infrastructure;

/// <summary>
/// Envelope persisted for every XHub JSON file: all stores carry a <c>schemaVersion</c>
/// (planning doc §4.1) so future migrations can be keyed off it.
/// </summary>
public sealed class StoreEnvelope<TData>
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("data")]
    public TData Data { get; set; } = default!;

    [JsonIgnore]
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Atomic JSON persistence: writes go to a temp file in the same directory, then replace the
/// target (crash-safe on NTFS and APFS), and the previous good content is kept as a
/// <c>.bak</c> that Load falls back to when the main file is missing or unreadable.
/// </summary>
public sealed class JsonStore<TData> where TData : class, new()
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly int _schemaVersion;

    public JsonStore(string path, int schemaVersion = StoreEnvelope<TData>.CurrentSchemaVersion)
    {
        _path = path;
        _schemaVersion = schemaVersion;
    }

    public string Path => _path;

    public TData Load()
    {
        TData? data = TryRead(_path, fromBackup: false);
        if (data != null) return data;

        // Corrupt or missing main file: try the last good backup before starting over.
        string backup = _path + ".bak";
        if (File.Exists(backup))
        {
            data = TryRead(backup, fromBackup: true);
            if (data != null) return data;
        }
        return new TData();
    }

    /// <summary>
    /// Persists atomically. Throws on IO failure so callers can surface it; the target file is
    /// never left partially written.
    /// </summary>
    public void Save(TData data)
    {
        string dir = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var envelope = new StoreEnvelope<TData> { SchemaVersion = _schemaVersion, Data = data };
        string json = JsonSerializer.Serialize(envelope, WriteOptions);

        string temp = _path + ".tmp";
        File.WriteAllText(temp, json);

        // Keep one generation of known-good content for recovery.
        string backup = _path + ".bak";
        if (File.Exists(_path))
            File.Copy(_path, backup, overwrite: true);

        File.Move(temp, _path, overwrite: true);
    }

    private TData? TryRead(string path, bool fromBackup)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var envelope = JsonSerializer.Deserialize<StoreEnvelope<TData>>(File.ReadAllText(path), ReadOptions);
            if (envelope == null) return null;
            if (envelope.SchemaVersion > _schemaVersion)
            {
                HubLog.Warning(
                    $"{System.IO.Path.GetFileName(path)} schemaVersion {envelope.SchemaVersion} is newer than " +
                    $"supported {_schemaVersion}; loading anyway (downgrade tolerated).");
            }
            return envelope.Data ?? new TData();
        }
        catch (Exception ex)
        {
            HubLog.Error(
                $"Failed to read {path}{(fromBackup ? " (.bak)" : "")}; " +
                (fromBackup ? "starting fresh." : "will retry with backup."), ex);
            return null;
        }
    }
}
