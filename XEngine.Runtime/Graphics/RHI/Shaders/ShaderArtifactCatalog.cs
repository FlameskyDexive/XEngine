// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;

namespace XEngine.Runtime.RHI.Shaders;

public readonly record struct ShaderArtifactCatalogEntry(
    Guid ShaderAssetGuid,
    string ShaderName,
    string PassStableId,
    GraphicsBackend Backend,
    string Tier,
    string CanonicalKeywordSet,
    ShaderArtifactKey ArtifactKey);

public sealed class ShaderArtifactCatalog
{
    public const int CurrentSchemaVersion = 2;

    private const int LegacySchemaVersion = 1;

    private const uint Magic = 0x43485358;
    private const int DigestLength = 32;
    private const int MaximumPayloadLength = 64 * 1024 * 1024;
    private const int MaximumAssetCount = 1_000_000;
    private const int MaximumKeysPerAsset = 65_536;

    // When false, Read() skips the SHA-256 catalog payload checksum. The checksum is
    // computed at build time (Write) and verified on read to detect catalog corruption.
    // Platforms without a native OpenSSL/libcrypto backend (HarmonyOS ships no libcrypto)
    // set this to false because System.Security.Cryptography.SHA256 statically depends on
    // libcrypto and aborts the process on first use when it is unavailable. The HAP is
    // already signature-verified at install time, so skipping the in-process checksum is
    // acceptable on such platforms.
    private static bool s_verifyChecksumOnRead = true;

    /// <summary>
    /// Controls whether <see cref="Read"/> verifies the catalog payload SHA-256 checksum.
    /// Defaults to <c>true</c>. Set to <c>false</c> on platforms without an OpenSSL
    /// backend (e.g. HarmonyOS) before loading catalogs.
    /// </summary>
    public static bool VerifyChecksumOnRead
    {
        get => s_verifyChecksumOnRead;
        set => s_verifyChecksumOnRead = value;
    }

    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<ShaderArtifactKey>> _entries;
    private readonly IReadOnlyList<ShaderArtifactCatalogEntry> _stableEntries;

    public ShaderArtifactCatalog(IReadOnlyDictionary<Guid, IReadOnlyList<ShaderArtifactKey>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var copy = new Dictionary<Guid, IReadOnlyList<ShaderArtifactKey>>(entries.Count);
        foreach (KeyValuePair<Guid, IReadOnlyList<ShaderArtifactKey>> entry in entries)
        {
            if (entry.Key == Guid.Empty)
                throw new ArgumentException("Shader artifact catalog asset GUIDs cannot be empty.", nameof(entries));
            ArgumentNullException.ThrowIfNull(entry.Value);

            var keys = new ShaderArtifactKey[entry.Value.Count];
            for (int i = 0; i < entry.Value.Count; i++)
            {
                ShaderArtifactKey key = entry.Value[i];
                if (string.IsNullOrEmpty(key.Value))
                    throw new ArgumentException("Shader artifact catalog keys cannot be empty.", nameof(entries));
                keys[i] = key;
            }

            Array.Sort(keys, static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
            int uniqueCount = RemoveDuplicateKeys(keys);
            if (uniqueCount != keys.Length)
                Array.Resize(ref keys, uniqueCount);
            copy.Add(entry.Key, Array.AsReadOnly(keys));
        }

        _entries = new ReadOnlyDictionary<Guid, IReadOnlyList<ShaderArtifactKey>>(copy);
        _stableEntries = Array.Empty<ShaderArtifactCatalogEntry>();
    }

    public ShaderArtifactCatalog(IReadOnlyList<ShaderArtifactCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new ArgumentException("Shader artifact catalog entries cannot be empty.", nameof(entries));

        var stable = new ShaderArtifactCatalogEntry[entries.Count];
        var byAsset = new Dictionary<Guid, List<ShaderArtifactKey>>();
        var identities = new HashSet<StableIdentity>();
        for (int i = 0; i < entries.Count; i++)
        {
            ShaderArtifactCatalogEntry entry = entries[i];
            ValidateStableEntry(entry);
            var identity = new StableIdentity(
                entry.ShaderAssetGuid,
                entry.PassStableId,
                entry.Backend,
                entry.Tier,
                entry.CanonicalKeywordSet);
            if (!identities.Add(identity))
                throw new ArgumentException("Shader artifact catalog contains duplicate stable identities.", nameof(entries));
            stable[i] = entry;
            if (!byAsset.TryGetValue(entry.ShaderAssetGuid, out List<ShaderArtifactKey>? keys))
            {
                keys = [];
                byAsset.Add(entry.ShaderAssetGuid, keys);
            }
            keys.Add(entry.ArtifactKey);
        }

        Array.Sort(stable, CompareStableEntries);
        var canonical = new Dictionary<Guid, IReadOnlyList<ShaderArtifactKey>>(byAsset.Count);
        foreach (KeyValuePair<Guid, List<ShaderArtifactKey>> pair in byAsset)
        {
            pair.Value.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
            int uniqueCount = RemoveDuplicateKeys(pair.Value);
            if (uniqueCount != pair.Value.Count)
                pair.Value.RemoveRange(uniqueCount, pair.Value.Count - uniqueCount);
            canonical.Add(pair.Key, Array.AsReadOnly(pair.Value.ToArray()));
        }

        _entries = new ReadOnlyDictionary<Guid, IReadOnlyList<ShaderArtifactKey>>(canonical);
        _stableEntries = Array.AsReadOnly(stable);
    }

    public IReadOnlyDictionary<Guid, IReadOnlyList<ShaderArtifactKey>> Entries => _entries;

    /// <summary>Stable v2 records used by the Player to index artifacts without decoding bytecode.</summary>
    public IReadOnlyList<ShaderArtifactCatalogEntry> StableEntries => _stableEntries;

    public bool HasStableEntries => _stableEntries.Count != 0;

    public bool TryGetArtifacts(Guid assetGuid, out IReadOnlyList<ShaderArtifactKey> keys) =>
        _entries.TryGetValue(assetGuid, out keys!);

    public void Write(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Shader artifact catalog destination must be writable.", nameof(destination));

        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            if (_stableEntries.Count != 0)
            {
                writer.Write(_stableEntries.Count);
                for (int i = 0; i < _stableEntries.Count; i++)
                {
                    ShaderArtifactCatalogEntry entry = _stableEntries[i];
                    writer.Write(entry.ShaderAssetGuid.ToByteArray());
                    WriteString(writer, entry.ShaderName);
                    WriteString(writer, entry.PassStableId);
                    writer.Write((int)entry.Backend);
                    WriteString(writer, entry.Tier);
                    WriteString(writer, entry.CanonicalKeywordSet);
                    writer.Write(entry.ArtifactKey.ToBytes());
                }
            }
            else
            {
                var assetGuids = new Guid[_entries.Count];
                int assetIndex = 0;
                foreach (Guid assetGuid in _entries.Keys)
                    assetGuids[assetIndex++] = assetGuid;
                Array.Sort(assetGuids);

                writer.Write(assetGuids.Length);
                for (int i = 0; i < assetGuids.Length; i++)
                {
                    Guid assetGuid = assetGuids[i];
                    writer.Write(assetGuid.ToByteArray());
                    IReadOnlyList<ShaderArtifactKey> keys = _entries[assetGuid];
                    writer.Write(keys.Count);
                    for (int keyIndex = 0; keyIndex < keys.Count; keyIndex++)
                        writer.Write(keys[keyIndex].ToBytes());
                }
            }
        }

        ReadOnlySpan<byte> payloadBytes = payload.GetBuffer().AsSpan(0, checked((int)payload.Length));
        byte[] digest = SHA256.HashData(payloadBytes);
        using var output = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        output.Write(Magic);
        output.Write(_stableEntries.Count == 0 ? LegacySchemaVersion : CurrentSchemaVersion);
        output.Write(payloadBytes.Length);
        output.Write(digest);
        output.Write(payloadBytes);
    }

    public static ShaderArtifactCatalog Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Shader artifact catalog source must be readable.", nameof(source));

        using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Shader artifact catalog magic is invalid.");
        int schemaVersion = reader.ReadInt32();
        if (schemaVersion is not LegacySchemaVersion and not CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported shader artifact catalog schema {schemaVersion}.");
        int payloadLength = reader.ReadInt32();
        if (payloadLength < sizeof(int) || payloadLength > MaximumPayloadLength)
            throw new InvalidDataException("Shader artifact catalog payload length is invalid.");

        byte[] expectedDigest = ReadExactly(reader, DigestLength);
        byte[] payload = ReadExactly(reader, payloadLength);
        if (source.ReadByte() != -1)
            throw new InvalidDataException("Shader artifact catalog contains trailing data.");
        if (!s_verifyChecksumOnRead)
        {
            // Checksum verification skipped (e.g. HarmonyOS: no libcrypto). The catalog is
            // produced by the build pipeline and the HAP is install-signed, so the payload
            // is trusted.
        }
        else if (!CryptographicOperations.FixedTimeEquals(expectedDigest, SHA256.HashData(payload)))
        {
            throw new InvalidDataException("Shader artifact catalog checksum is invalid.");
        }

        using var payloadStream = new MemoryStream(payload, writable: false);
        using var payloadReader = new BinaryReader(payloadStream);
        if (schemaVersion == CurrentSchemaVersion)
        {
            int entryCount = payloadReader.ReadInt32();
            if (entryCount < 0 || entryCount > MaximumPayloadLength / 128)
                throw new InvalidDataException("Shader artifact catalog stable entry count is invalid.");
            var stableEntries = new ShaderArtifactCatalogEntry[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                Guid assetGuid = new Guid(ReadExactly(payloadReader, 16));
                stableEntries[i] = new ShaderArtifactCatalogEntry(
                    assetGuid,
                    ReadString(payloadReader),
                    ReadString(payloadReader),
                    (GraphicsBackend)payloadReader.ReadInt32(),
                    ReadString(payloadReader),
                    ReadString(payloadReader),
                    ShaderArtifactKey.FromBytes(ReadExactly(payloadReader, ShaderArtifactKey.ByteLength)));
            }
            if (payloadStream.Position != payloadStream.Length)
                throw new InvalidDataException("Shader artifact catalog payload contains trailing data.");
            try
            {
                return new ShaderArtifactCatalog(stableEntries);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Shader artifact catalog stable entries are invalid.", exception);
            }
        }

        int assetCount = payloadReader.ReadInt32();
        if (assetCount < 0 || assetCount > MaximumAssetCount)
            throw new InvalidDataException("Shader artifact catalog asset count is invalid.");

        var entries = new Dictionary<Guid, IReadOnlyList<ShaderArtifactKey>>(assetCount);
        for (int i = 0; i < assetCount; i++)
        {
            var assetGuid = new Guid(ReadExactly(payloadReader, 16));
            if (assetGuid == Guid.Empty || entries.ContainsKey(assetGuid))
                throw new InvalidDataException("Shader artifact catalog contains an invalid or duplicate asset GUID.");

            int keyCount = payloadReader.ReadInt32();
            if (keyCount < 0 || keyCount > MaximumKeysPerAsset)
                throw new InvalidDataException("Shader artifact catalog key count is invalid.");
            var keys = new ShaderArtifactKey[keyCount];
            for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
                keys[keyIndex] = ShaderArtifactKey.FromBytes(ReadExactly(payloadReader, ShaderArtifactKey.ByteLength));
            entries.Add(assetGuid, keys);
        }

        if (payloadStream.Position != payloadStream.Length)
            throw new InvalidDataException("Shader artifact catalog payload contains trailing data.");
        return new ShaderArtifactCatalog(entries);
    }

    private static int RemoveDuplicateKeys(ShaderArtifactKey[] keys)
    {
        if (keys.Length < 2)
            return keys.Length;

        int writeIndex = 1;
        for (int readIndex = 1; readIndex < keys.Length; readIndex++)
        {
            if (StringComparer.Ordinal.Equals(keys[readIndex].Value, keys[writeIndex - 1].Value))
                continue;
            keys[writeIndex++] = keys[readIndex];
        }
        return writeIndex;
    }

    private static int RemoveDuplicateKeys(List<ShaderArtifactKey> keys)
    {
        if (keys.Count < 2)
            return keys.Count;
        int writeIndex = 1;
        for (int readIndex = 1; readIndex < keys.Count; readIndex++)
        {
            if (keys[readIndex] == keys[writeIndex - 1])
                continue;
            keys[writeIndex++] = keys[readIndex];
        }
        return writeIndex;
    }

    private static void ValidateStableEntry(ShaderArtifactCatalogEntry entry)
    {
        if (entry.ShaderAssetGuid == Guid.Empty ||
            string.IsNullOrWhiteSpace(entry.ShaderName) ||
            string.IsNullOrWhiteSpace(entry.PassStableId) ||
            string.IsNullOrWhiteSpace(entry.Tier) ||
            entry.ArtifactKey.Value == null ||
            !ShaderBytecodeFormats.IsArtifactBackend(entry.Backend))
        {
            throw new ArgumentException("Shader artifact catalog stable entry is invalid.", nameof(entry));
        }
    }

    private static int CompareStableEntries(ShaderArtifactCatalogEntry left, ShaderArtifactCatalogEntry right)
    {
        int comparison = left.ShaderAssetGuid.CompareTo(right.ShaderAssetGuid);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.PassStableId, right.PassStableId);
        if (comparison != 0)
            return comparison;
        comparison = left.Backend.CompareTo(right.Backend);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Tier, right.Tier);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.CanonicalKeywordSet, right.CanonicalKeywordSet);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.ArtifactKey.Value, right.ArtifactKey.Value);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumPayloadLength)
            throw new InvalidDataException("Shader artifact catalog string length is invalid.");
        byte[] bytes = ReadExactly(reader, length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private readonly record struct StableIdentity(
        Guid AssetGuid,
        string PassStableId,
        GraphicsBackend Backend,
        string Tier,
        string CanonicalKeywordSet);

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException();
        return bytes;
    }
}
