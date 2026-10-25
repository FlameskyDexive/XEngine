using System.Diagnostics;
using System.Text.Json;
using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class ImportProbeAsset : ScriptableObject
{
    [SerializeField] public byte[] Payload = Array.Empty<byte>();
}

[ImporterFor(".throughputprobe")]
public sealed class ThroughputProbeImporter : AssetImporter
{
    public override int Version => 1;
    public override bool Import(ImportContext ctx)
    {
        ctx.SetMainAsset(new ImportProbeAsset { Payload = File.ReadAllBytes(ctx.AbsolutePath) });
        return true;
    }
}

/// <summary>Opt-in end-to-end scan/import/cache-write/reopen probe, not a timing assertion.</summary>
public sealed class ImportThroughputProbeTests : EditorTestHarness
{
    [Fact]
    public void MeasureColdAndWarmDatabaseThroughput()
    {
        string? output = Environment.GetEnvironmentVariable("XENGINE_IMPORT_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var rows = new List<object>();
        int initialAssets = Assets.AssetCount;
        byte[] payload = new byte[128 * 1024];
        new Random(42).NextBytes(payload);
        for (int round = 0; round < 5; round++)
        {
            // Unique folders prevent reusing imported caches between measured cold batches.
            string folder = $"Round{round}";
            Directory.CreateDirectory(AssetAbsolutePath(folder));
            for (int i = 0; i < 128; i++)
                File.WriteAllBytes(AssetAbsolutePath($"{folder}/{i}.throughputprobe"), payload);
            Measure("cold-batch", round);
            Measure("warm-reopen", round);
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new {
            runtime = Environment.Version.ToString(), cpu = Environment.ProcessorCount,
            serverGC = System.Runtime.GCSettings.IsServerGC, assetsPerBatch = 128,
            sourceBytesPerAsset = payload.Length, rows
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Measure(string phase, int round)
        {
            GC.Collect();
            long allocated = GC.GetTotalAllocatedBytes(true);
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var watch = Stopwatch.StartNew();
            ReopenDatabase();
            watch.Stop();
            rows.Add(new { phase, round, milliseconds = watch.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                gen0 = GC.CollectionCount(0) - g0, gen1 = GC.CollectionCount(1) - g1,
                gen2 = GC.CollectionCount(2) - g2 });
            Assert.Equal(initialAssets + (round + 1) * 128, Assets.AssetCount);
        }
    }
}
