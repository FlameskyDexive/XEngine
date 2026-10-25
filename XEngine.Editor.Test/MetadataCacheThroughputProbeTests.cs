using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

using XEngine.Echo;
using XEngine.Editor;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Opt-in local metadata-cache comparison. Set XENGINE_METADATA_CACHE_PROBE_OUTPUT to a JSON path
/// before running this test. It deliberately measures cache read/write only, not editor startup or
/// asset importing, so the typed-binary change can be compared independently of filesystem scanning.
/// </summary>
public sealed class MetadataCacheThroughputProbeTests
{
    private const int EntryCount = 4096;
    private const int SampleCount = 5;

    [Fact]
    public void MeasureLegacyTextAndTypedBinaryReadWrite()
    {
        string? output = Environment.GetEnvironmentVariable("XENGINE_METADATA_CACHE_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            return;

        string root = Path.Combine(Path.GetTempPath(), "XEngineMetadataCacheProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string legacyPath = Path.Combine(root, "legacy.metadata.db");
        string binaryPath = Path.Combine(root, "binary.metadata.db");
        var entries = CreateEntries(EntryCount);
        var rows = new List<object>(SampleCount * 4);

        try
        {
            WriteLegacyText(legacyPath, entries);
            MetadataCache.Save(binaryPath, entries);

            // Exercise both complete paths before recording; first-use Echo reflection/JIT
            // otherwise dominates the first legacy samples and exaggerates steady-state gains.
            for (int warmup = 0; warmup < 5; warmup++)
            {
                Assert.Equal(EntryCount, MetadataCache.Load(legacyPath).Count);
                Assert.Equal(EntryCount, MetadataCache.Load(binaryPath).Count);
                WriteLegacyText(legacyPath, entries);
                MetadataCache.Save(binaryPath, entries);
            }

            MeasureLoad("legacy-text-load", legacyPath);
            MeasureLoad("typed-binary-load", binaryPath);
            MeasureSave("legacy-text-save", legacyPath, () => WriteLegacyText(legacyPath, entries));
            MeasureSave("typed-binary-save", binaryPath, () => MetadataCache.Save(binaryPath, entries));

            Assert.Equal(EntryCount, MetadataCache.Load(binaryPath).Count);

            string fullOutput = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            File.WriteAllText(fullOutput, JsonSerializer.Serialize(new
            {
                runtime = Environment.Version.ToString(),
                cpu = Environment.ProcessorCount,
                serverGC = System.Runtime.GCSettings.IsServerGC,
                entryCount = EntryCount,
                sampleCount = SampleCount,
                warmupCount = 5,
                rows,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Probe output is the artifact; temporary inputs are best-effort cleanup only.
            }
        }

        void MeasureLoad(string phase, string path)
        {
            for (int sample = 0; sample < SampleCount; sample++)
            {
                GC.Collect();
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                int gen0 = GC.CollectionCount(0);
                int gen1 = GC.CollectionCount(1);
                int gen2 = GC.CollectionCount(2);
                var watch = Stopwatch.StartNew();
                var loaded = MetadataCache.Load(path);
                watch.Stop();

                Assert.Equal(EntryCount, loaded.Count);
                rows.Add(new
                {
                    phase,
                    sample,
                    milliseconds = watch.Elapsed.TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
                    gen0 = GC.CollectionCount(0) - gen0,
                    gen1 = GC.CollectionCount(1) - gen1,
                    gen2 = GC.CollectionCount(2) - gen2,
                });
            }
        }

        void MeasureSave(string phase, string path, Action action)
        {
            for (int sample = 0; sample < SampleCount; sample++)
            {
                GC.Collect();
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                int gen0 = GC.CollectionCount(0);
                int gen1 = GC.CollectionCount(1);
                int gen2 = GC.CollectionCount(2);
                var watch = Stopwatch.StartNew();
                action();
                watch.Stop();

                Assert.True(File.Exists(path));
                rows.Add(new
                {
                    phase,
                    sample,
                    milliseconds = watch.Elapsed.TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
                    gen0 = GC.CollectionCount(0) - gen0,
                    gen1 = GC.CollectionCount(1) - gen1,
                    gen2 = GC.CollectionCount(2) - gen2,
                });
            }
        }
    }

    private static List<AssetEntry> CreateEntries(int count)
    {
        var entries = new List<AssetEntry>(count);
        for (int i = 0; i < count; i++)
        {
            Guid guid = GuidFor(i * 4 + 1);
            Guid dependency = GuidFor(i * 4 + 2);
            Guid subGuid = GuidFor(i * 4 + 3);
            Guid subDependency = GuidFor(i * 4 + 4);
            entries.Add(new AssetEntry
            {
                Guid = guid,
                Path = $"Probe/{i:D5}.asset",
                ImporterType = "MetadataProbeImporter",
                ImporterVersion = 1,
                LastModifiedTicks = 638000000000000000L + i,
                MainAssetTypeName = typeof(AssetEntry).AssemblyQualifiedName,
                Dependencies = new[] { dependency },
                Labels = new[] { "probe", "metadata" },
                SubAssets = new[]
                {
                    new SubAssetEntry
                    {
                        Guid = subGuid,
                        Name = $"Sub-{i:D5}",
                        TypeName = typeof(SubAssetEntry).AssemblyQualifiedName!,
                        Dependencies = new[] { subDependency },
                    },
                },
                Provenance = new AssetProvenance
                {
                    EditedBy = "metadata-probe",
                    EditReason = "throughput baseline",
                    EditedAtUtcTicks = 638000000000000000L + i,
                    LastEditOperationId = $"probe-{i:D5}",
                },
            });
        }

        return entries;
    }

    private static Guid GuidFor(int value)
    {
        byte[] bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        bytes[15] = 0x5A;
        return new Guid(bytes);
    }

    private static void WriteLegacyText(string path, IEnumerable<AssetEntry> entries)
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
}
