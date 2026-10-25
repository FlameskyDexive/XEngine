using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using XEngine.Echo;

namespace XEngine.Editor;

/// <summary>
/// Persists the full asset index to Library/metadata.db for fast startup.
///
/// New caches use a small, typed binary format. This avoids rebuilding an Echo tree and running
/// the reflection serializer for every entry during the normal editor reopen path. The old v2 Echo
/// text format remains readable so an existing project migrates on its next cache save. Asset source
/// files are unaffected and remain Echo text.
/// </summary>
public static class MetadataCache
{
    // The logical schema is unchanged: the binary format carries the same v2 AssetEntry fields as
    // the legacy Echo document. Keeping this number at 2 is what allows v2 text caches to migrate.
    private const int CurrentVersion = 2;

    // Binary format version is separate from the logical metadata schema version. Version 2 adds
    // AssetEntry.MetaFingerprint while retaining the logical v2 schema. Keep reading v1 so an
    // existing cache upgrades naturally on the next save/scan instead of forcing a cold failure.
    private const byte LegacyBinaryFormatVersion = 1;
    private const byte BinaryFormatVersion = 2;

    // A distinctive header makes accidental recognition of an Echo text file effectively impossible while still
    // being cheap to check before falling back to the legacy reader.
    private static readonly byte[] BinaryMagic =
        "XENGINE-METADATA"u8.ToArray();

    private const int MaxEntryCount = 10_000_000;
    private const int MaxArrayCount = 10_000_000;
    private const int MaxStringBytes = 16 * 1024 * 1024;
    private const int IoBufferSize = 64 * 1024;

    public static Dictionary<Guid, AssetEntry> Load(string metadataDbPath)
    {
        if (!File.Exists(metadataDbPath))
            return new Dictionary<Guid, AssetEntry>();

        try
        {
            using var stream = new FileStream(
                metadataDbPath,
                FileMode.Open,
                FileAccess.Read,
                // Readers may finish against the old file while an atomic save publishes the
                // next index. Without Delete sharing Windows rejects Replace during reopen.
                FileShare.Read | FileShare.Delete,
                IoBufferSize,
                FileOptions.SequentialScan);

            if (TryConsumeBinaryMagic(stream))
                return LoadBinary(stream);

            // Existing projects have v2 Echo text caches. Rewind after the magic probe and keep
            // this path intentionally compatible with the previous on-disk representation.
            stream.Position = 0;
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: IoBufferSize,
                leaveOpen: false);
            return LoadLegacyText(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            // Never expose a partially decoded dictionary. A malformed/truncated cache must cause
            // the caller to rebuild the complete index from Assets/ instead.
            Runtime.Debug.LogWarning($"Failed to load metadata cache (will rebuild): {ex.Message}");
            return new Dictionary<Guid, AssetEntry>();
        }
    }

    public static void Save(string metadataDbPath, IEnumerable<AssetEntry> entries)
    {
        try
        {
            // Snapshot before opening the temporary file. If the source enumerable throws, the
            // previous cache remains untouched and no replacement is attempted.
            var snapshot = new List<AssetEntry>();
            foreach (var entry in entries)
            {
                ValidateEntry(entry);
                snapshot.Add(entry);
            }

            string fullPath = Path.GetFullPath(metadataDbPath);
            string directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);

            string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    IoBufferSize,
                    FileOptions.SequentialScan))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    WriteBinary(writer, snapshot);
                    writer.Flush();
                    // Ensure the complete temporary image reaches disk before it becomes visible.
                    stream.Flush(flushToDisk: true);
                }

                ReplaceAtomically(temporaryPath, fullPath);
            }
            finally
            {
                // A failed write/replace must not leave a file that a later caller could mistake
                // for the real cache. The destination is never deleted here.
                TryDelete(temporaryPath);
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"Failed to save metadata cache: {ex.Message}");
        }
    }

    private static bool TryConsumeBinaryMagic(Stream stream)
    {
        byte[] header = new byte[BinaryMagic.Length];
        int offset = 0;
        while (offset < header.Length)
        {
            int read = stream.Read(header, offset, header.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }

        if (offset == BinaryMagic.Length && header.AsSpan().SequenceEqual(BinaryMagic))
            return true;

        stream.Position = 0;
        return false;
    }

    private static Dictionary<Guid, AssetEntry> LoadBinary(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        byte formatVersion = reader.ReadByte();
        if (formatVersion != LegacyBinaryFormatVersion && formatVersion != BinaryFormatVersion)
            throw new InvalidDataException($"Unsupported metadata binary format v{formatVersion}.");

        int schemaVersion = reader.ReadInt32();
        if (schemaVersion != CurrentVersion)
        {
            Runtime.Debug.Log($"Metadata cache version mismatch (found v{schemaVersion}, expected v{CurrentVersion}). Rebuilding.");
            return new Dictionary<Guid, AssetEntry>();
        }

        int entryCount = ReadCount(reader, MaxEntryCount, "entry");
        var result = new Dictionary<Guid, AssetEntry>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            AssetEntry entry = ReadAssetEntry(reader, formatVersion);
            ValidateEntry(entry);
            if (!result.TryAdd(entry.Guid, entry))
                throw new InvalidDataException($"Metadata cache contains duplicate GUID {entry.Guid}.");
        }

        // Trailing bytes usually indicate a torn or incompatible write. Reject them instead of
        // silently accepting a prefix that could represent only part of the index.
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Metadata cache contains trailing data.");

        return result;
    }

    private static Dictionary<Guid, AssetEntry> LoadLegacyText(string text)
    {
        EchoObject root = EchoObject.ReadFromString(text);
        if (root.TagType != EchoType.Compound)
            throw new InvalidDataException("Metadata cache root is not a compound.");

        if (!root.TryGet("version", out var versionTag) || versionTag.TagType != EchoType.Int)
            throw new InvalidDataException("Metadata cache has no valid version.");

        int version = versionTag.IntValue;
        if (version != CurrentVersion)
        {
            Runtime.Debug.Log($"Metadata cache version mismatch (found v{version}, expected v{CurrentVersion}). Rebuilding.");
            return new Dictionary<Guid, AssetEntry>();
        }

        if (!root.TryGet("entries", out var entriesTag) || entriesTag.TagType != EchoType.List)
            throw new InvalidDataException("Metadata cache has no valid entries list.");

        // Decode into a temporary dictionary and publish it only after every row validates. The
        // previous implementation could return rows decoded before a later malformed row failed.
        var result = new Dictionary<Guid, AssetEntry>(entriesTag.Count);
        foreach (var entryTag in entriesTag.List)
        {
            var entry = Serializer.Deserialize<AssetEntry>(entryTag, new SerializationContext());
            if (entry == null)
                throw new InvalidDataException("Metadata cache contains a null entry.");

            ValidateEntry(entry);
            if (!result.TryAdd(entry.Guid, entry))
                throw new InvalidDataException($"Metadata cache contains duplicate GUID {entry.Guid}.");
        }

        return result;
    }

    private static void WriteBinary(BinaryWriter writer, IReadOnlyList<AssetEntry> entries)
    {
        writer.Write(BinaryMagic);
        writer.Write(BinaryFormatVersion);
        writer.Write(CurrentVersion);
        writer.Write(entries.Count);

        foreach (var entry in entries)
            WriteAssetEntry(writer, entry);
    }

    private static void WriteAssetEntry(BinaryWriter writer, AssetEntry entry)
    {
        WriteGuid(writer, entry.Guid);
        WriteString(writer, entry.Path);
        WriteString(writer, entry.ImporterType);
        writer.Write(entry.ImporterVersion);
        writer.Write(entry.LastModifiedTicks);
        WriteString(writer, entry.MetaFingerprint);
        WriteNullableString(writer, entry.MainAssetTypeName);
        WriteGuidArray(writer, entry.Dependencies);
        WriteStringArray(writer, entry.Labels);

        var subAssets = entry.SubAssets ?? Array.Empty<SubAssetEntry>();
        writer.Write(subAssets.Length);
        foreach (var sub in subAssets)
        {
            if (sub == null)
                throw new InvalidDataException("Metadata cache cannot save a null sub-asset entry.");

            WriteGuid(writer, sub.Guid);
            WriteString(writer, sub.Name);
            WriteString(writer, sub.TypeName);
            WriteGuidArray(writer, sub.Dependencies);
        }

        // AssetProvenance is a reference field. Preserve its presence while normalizing a null
        // runtime value to the same empty object legacy deserialization produces.
        writer.Write(entry.Provenance != null);
        if (entry.Provenance != null)
        {
            WriteString(writer, entry.Provenance.EditedBy);
            WriteString(writer, entry.Provenance.EditReason);
            writer.Write(entry.Provenance.EditedAtUtcTicks);
            WriteNullableString(writer, entry.Provenance.LastEditOperationId);
        }
    }

    private static AssetEntry ReadAssetEntry(BinaryReader reader, byte formatVersion)
    {
        var entry = new AssetEntry
        {
            Guid = ReadGuid(reader),
            Path = ReadString(reader) ?? throw new InvalidDataException("Asset entry path is null."),
            ImporterType = ReadString(reader) ?? throw new InvalidDataException("Importer type is null."),
            ImporterVersion = reader.ReadInt32(),
            LastModifiedTicks = reader.ReadInt64(),
            // v1 caches predate meta fingerprints. Leaving this empty lets the startup scan either
            // baseline from a cache newer than source/.meta or conservatively reimport once.
            MetaFingerprint = formatVersion >= BinaryFormatVersion ? ReadString(reader) ?? "" : "",
            MainAssetTypeName = ReadNullableString(reader),
            Dependencies = ReadGuidArray(reader),
            Labels = ReadStringArray(reader),
        };

        int subAssetCount = ReadCount(reader, MaxArrayCount, "sub-asset");
        if (subAssetCount == 0)
        {
            entry.SubAssets = Array.Empty<SubAssetEntry>();
        }
        else
        {
            var subAssets = new SubAssetEntry[subAssetCount];
            for (int i = 0; i < subAssetCount; i++)
            {
                subAssets[i] = new SubAssetEntry
                {
                    Guid = ReadGuid(reader),
                    Name = ReadString(reader) ?? throw new InvalidDataException("Sub-asset name is null."),
                    TypeName = ReadString(reader) ?? throw new InvalidDataException("Sub-asset type is null."),
                    Dependencies = ReadGuidArray(reader),
                };
            }
            entry.SubAssets = subAssets;
        }

        bool hasProvenance = reader.ReadBoolean();
        if (hasProvenance)
        {
            entry.Provenance = new AssetProvenance
            {
                EditedBy = ReadString(reader) ?? throw new InvalidDataException("Provenance author is null."),
                EditReason = ReadString(reader) ?? throw new InvalidDataException("Provenance reason is null."),
                EditedAtUtcTicks = reader.ReadInt64(),
                LastEditOperationId = ReadNullableString(reader),
            };
        }
        else
        {
            entry.Provenance = new AssetProvenance();
        }

        return entry;
    }

    private static void ValidateEntry(AssetEntry entry)
    {
        if (entry.Guid == Guid.Empty)
            throw new InvalidDataException("Metadata cache contains an entry with an empty GUID.");
        if (entry.Path == null || entry.ImporterType == null)
            throw new InvalidDataException($"Metadata cache entry {entry.Guid} has a null path or importer.");
        if (entry.Dependencies == null || entry.Labels == null || entry.SubAssets == null)
            throw new InvalidDataException($"Metadata cache entry {entry.Guid} has a null collection.");

        foreach (var sub in entry.SubAssets)
        {
            if (sub == null || sub.Guid == Guid.Empty || sub.Name == null || sub.TypeName == null || sub.Dependencies == null)
                throw new InvalidDataException($"Metadata cache entry {entry.Guid} has an invalid sub-asset.");
        }
    }

    private static void WriteGuid(BinaryWriter writer, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        writer.Write(bytes);
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        reader.BaseStream.ReadExactly(bytes);
        return new Guid(bytes);
    }

    private static void WriteGuidArray(BinaryWriter writer, Guid[]? values)
    {
        if (values == null)
        {
            writer.Write(0);
            return;
        }

        writer.Write(values.Length);
        foreach (var value in values)
            WriteGuid(writer, value);
    }

    private static Guid[] ReadGuidArray(BinaryReader reader)
    {
        int count = ReadCount(reader, MaxArrayCount, "GUID");
        if (count == 0)
            return Array.Empty<Guid>();

        var values = new Guid[count];
        for (int i = 0; i < count; i++)
            values[i] = ReadGuid(reader);
        return values;
    }

    private static void WriteStringArray(BinaryWriter writer, string[]? values)
    {
        if (values == null)
        {
            writer.Write(0);
            return;
        }

        writer.Write(values.Length);
        foreach (var value in values)
            WriteString(writer, value);
    }

    private static string[] ReadStringArray(BinaryReader reader)
    {
        int count = ReadCount(reader, MaxArrayCount, "string");
        if (count == 0)
            return Array.Empty<string>();

        var values = new string[count];
        for (int i = 0; i < count; i++)
            values[i] = ReadString(reader) ?? throw new InvalidDataException("Metadata cache contains a null label.");
        return values;
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value != null)
            WriteString(writer, value);
    }

    private static string? ReadNullableString(BinaryReader reader)
        => reader.ReadBoolean() ? ReadString(reader) : null;

    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value == null)
        {
            writer.Write(-1);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
            throw new InvalidDataException($"Metadata cache string exceeds {MaxStringBytes} bytes.");

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string? ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length == -1)
            return null;
        if (length < 0 || length > MaxStringBytes)
            throw new InvalidDataException($"Metadata cache string length {length} is invalid.");

        byte[] bytes = ReadBytes(reader, length, "string");
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] ReadBytes(BinaryReader reader, int length, string kind)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException($"Metadata cache ended while reading {kind}.");
        return bytes;
    }

    private static int ReadCount(BinaryReader reader, int maximum, string kind)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException($"Metadata cache {kind} count {count} is invalid.");
        if (count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException($"Metadata cache {kind} count exceeds remaining data.");
        return count;
    }

    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            try
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // Same-volume Move with overwrite is the fallback on filesystems without Replace.
                File.Move(temporaryPath, destinationPath, overwrite: true);
                return;
            }
        }

        File.Move(temporaryPath, destinationPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The original cache remains valid; an orphaned uniquely named temp is harmless.
        }
    }
}
