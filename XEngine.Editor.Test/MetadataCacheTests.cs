using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Editor;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Focused tests for the local Library/metadata.db format. These tests do not create an editor or
/// scan Assets/, so they remain useful while the full editor build is occupied by another task.
/// </summary>
public sealed class MetadataCacheTests
{
    [Fact]
    public void BinaryRoundtrip_PreservesAllAssetEntryFields()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "metadata.db");
        try
        {
            var expected = new[] { CreateEntry(0), CreateEntry(1, includeNullableValues: true) };

            MetadataCache.Save(path, expected);

            Assert.True(File.Exists(path));
            var actual = MetadataCache.Load(path);

            Assert.Equal(expected.Length, actual.Count);
            foreach (var expectedEntry in expected)
            {
                Assert.True(actual.TryGetValue(expectedEntry.Guid, out var actualEntry));
                AssertEntryEqual(expectedEntry, actualEntry!);
            }
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void LegacyV2EchoText_LoadsAndCanBeMigratedToBinary()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "metadata.db");
        try
        {
            var expected = new[] { CreateEntry(7) };
            WriteLegacyV2Text(path, expected);

            var loadedLegacy = MetadataCache.Load(path);
            Assert.Equal(expected.Length, loadedLegacy.Count);
            AssertEntryEqual(expected[0], loadedLegacy[expected[0].Guid]);

            // The normal save path is the migration boundary: the next write replaces the old
            // Echo text image with the typed binary image, without changing the logical schema.
            MetadataCache.Save(path, loadedLegacy.Values);
            var loadedBinary = MetadataCache.Load(path);
            Assert.Equal(expected.Length, loadedBinary.Count);
            AssertEntryEqual(expected[0], loadedBinary[expected[0].Guid]);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void CorruptLegacyText_DoesNotReturnEntriesDecodedBeforeTheFailure()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "metadata.db");
        try
        {
            var root = EchoObject.NewCompound();
            root["version"] = new EchoObject(2);
            var entries = EchoObject.NewList();
            entries.ListAdd(Serializer.Serialize(CreateEntry(10), new SerializationContext()));
            entries.ListAdd(EchoObject.NewCompound()); // Empty GUID: malformed second row.
            root["entries"] = entries;
            File.WriteAllText(path, root.WriteToString());

            Assert.Empty(MetadataCache.Load(path));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void TruncatedBinary_DoesNotReturnEntriesDecodedBeforeTheFailure()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "metadata.db");
        try
        {
            MetadataCache.Save(path, new[] { CreateEntry(20), CreateEntry(21) });
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 64);

            Array.Resize(ref bytes, bytes.Length / 2);
            File.WriteAllBytes(path, bytes);

            Assert.Empty(MetadataCache.Load(path));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void FailedSave_PreservesPreviousCacheAndLeavesNoTemporaryFile()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "metadata.db");
        try
        {
            var original = CreateEntry(30);
            MetadataCache.Save(path, new[] { original });
            byte[] before = File.ReadAllBytes(path);
            var invalid = CreateEntry(31);
            invalid.Guid = Guid.Empty;

            MetadataCache.Save(path, new[] { invalid });

            Assert.Equal(before, File.ReadAllBytes(path));
            AssertEntryEqual(original, MetadataCache.Load(path)[original.Guid]);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { TryDelete(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateGuidOrTrailingData_RejectsEntireCache(bool trailingData)
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "metadata.db");
        try
        {
            var entry = CreateEntry(40);
            MetadataCache.Save(path, trailingData ? new[] { entry } : new[] { entry, entry });
            if (trailingData)
            {
                using var stream = new FileStream(path, FileMode.Append);
                stream.WriteByte(0x7f);
            }

            Assert.Empty(MetadataCache.Load(path));
        }
        finally { TryDelete(directory); }
    }

    private static AssetEntry CreateEntry(int index, bool includeNullableValues = false)
    {
        Guid guid = Guid.NewGuid();
        Guid dependency = Guid.NewGuid();
        Guid subGuid = Guid.NewGuid();
        Guid subDependency = Guid.NewGuid();

        return new AssetEntry
        {
            Guid = guid,
            Path = $"Textures/Entry{index}.asset",
            ImporterType = "TextureImporter",
            ImporterVersion = 100 + index,
            LastModifiedTicks = DateTime.UtcNow.AddMinutes(-index).Ticks,
            MetaFingerprint = $"meta-fingerprint-{index}",
            MainAssetTypeName = includeNullableValues ? null : typeof(AssetEntry).AssemblyQualifiedName,
            Dependencies = new[] { dependency },
            Labels = new[] { "environment", $"label-{index}" },
            SubAssets = new[]
            {
                new SubAssetEntry
                {
                    Guid = subGuid,
                    Name = $"SubAsset-{index}",
                    TypeName = typeof(SubAssetEntry).AssemblyQualifiedName!,
                    Dependencies = new[] { subDependency },
                },
            },
            Provenance = new AssetProvenance
            {
                EditedBy = $"agent-{index}",
                EditReason = $"roundtrip-{index}",
                EditedAtUtcTicks = DateTime.UtcNow.Ticks,
                LastEditOperationId = includeNullableValues ? null : $"operation-{index}",
            },
        };
    }

    private static void AssertEntryEqual(AssetEntry expected, AssetEntry actual)
    {
        Assert.Equal(expected.Guid, actual.Guid);
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.ImporterType, actual.ImporterType);
        Assert.Equal(expected.ImporterVersion, actual.ImporterVersion);
        Assert.Equal(expected.LastModifiedTicks, actual.LastModifiedTicks);
        Assert.Equal(expected.MetaFingerprint, actual.MetaFingerprint);
        Assert.Equal(expected.MainAssetTypeName, actual.MainAssetTypeName);
        Assert.Equal(expected.Dependencies, actual.Dependencies);
        Assert.Equal(expected.Labels, actual.Labels);
        Assert.Equal(expected.SubAssets.Length, actual.SubAssets.Length);

        for (int i = 0; i < expected.SubAssets.Length; i++)
        {
            Assert.Equal(expected.SubAssets[i].Guid, actual.SubAssets[i].Guid);
            Assert.Equal(expected.SubAssets[i].Name, actual.SubAssets[i].Name);
            Assert.Equal(expected.SubAssets[i].TypeName, actual.SubAssets[i].TypeName);
            Assert.Equal(expected.SubAssets[i].Dependencies, actual.SubAssets[i].Dependencies);
        }

        Assert.NotNull(actual.Provenance);
        Assert.Equal(expected.Provenance.EditedBy, actual.Provenance.EditedBy);
        Assert.Equal(expected.Provenance.EditReason, actual.Provenance.EditReason);
        Assert.Equal(expected.Provenance.EditedAtUtcTicks, actual.Provenance.EditedAtUtcTicks);
        Assert.Equal(expected.Provenance.LastEditOperationId, actual.Provenance.LastEditOperationId);
    }

    private static void WriteLegacyV2Text(string path, IEnumerable<AssetEntry> entries)
    {
        var root = EchoObject.NewCompound();
        root["version"] = new EchoObject(2);
        root["lastScanTime"] = new EchoObject(DateTime.UtcNow.ToString("o"));
        var list = EchoObject.NewList();
        foreach (var entry in entries)
            list.ListAdd(Serializer.Serialize(entry, new SerializationContext()));
        root["entries"] = list;

        File.WriteAllText(path, root.WriteToString());
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "XEngineMetadataCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best effort cleanup; test assertions are about cache contents, not temp cleanup.
        }
    }
}
