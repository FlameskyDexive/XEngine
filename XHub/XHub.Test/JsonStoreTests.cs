using System;
using System.IO;
using XHub.Infrastructure;
using Xunit;

namespace XHub.Test;

public class JsonStoreTests : IDisposable
{
    private readonly string _dir;

    public JsonStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class SampleData
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    [Fact]
    public void Save_WritesEnvelopeWithSchemaVersion()
    {
        var store = new JsonStore<SampleData>(Path.Combine(_dir, "sample.json"));
        store.Save(new SampleData { Name = "hello", Count = 3 });

        string json = File.ReadAllText(store.Path);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"Name\": \"hello\"", json);
    }

    [Fact]
    public void Load_RoundTripsData()
    {
        var path = Path.Combine(_dir, "sample.json");
        var store = new JsonStore<SampleData>(path);
        store.Save(new SampleData { Name = "中文路径", Count = 7 });

        var loaded = new JsonStore<SampleData>(path).Load();
        Assert.Equal("中文路径", loaded.Name);
        Assert.Equal(7, loaded.Count);
    }

    [Fact]
    public void Load_MissingFile_ReturnsFreshInstance()
    {
        var store = new JsonStore<SampleData>(Path.Combine(_dir, "absent.json"));
        var data = store.Load();
        Assert.NotNull(data);
        Assert.Equal("", data.Name);
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTempFile()
    {
        var store = new JsonStore<SampleData>(Path.Combine(_dir, "sample.json"));
        store.Save(new SampleData { Name = "a" });
        store.Save(new SampleData { Name = "b" });
        Assert.False(File.Exists(store.Path + ".tmp"));
        Assert.Equal("b", store.Load().Name);
    }

    [Fact]
    public void Save_KeepsPreviousGeneration_LoadRecoversFromCorruptMainFile()
    {
        var path = Path.Combine(_dir, "sample.json");
        var store = new JsonStore<SampleData>(path);
        store.Save(new SampleData { Name = "good", Count = 42 });
        store.Save(new SampleData { Name = "next", Count = 43 });

        // The second save archived the first generation as .bak.
        Assert.True(File.Exists(path + ".bak"));
        File.WriteAllText(path, "{ this is not json");

        // Recovery returns the last known-good generation (one save behind the corrupted file).
        var recovered = new JsonStore<SampleData>(path).Load();
        Assert.Equal("good", recovered.Name);
        Assert.Equal(42, recovered.Count);
    }

    [Fact]
    public void Load_NewerSchemaVersion_StillLoads()
    {
        var path = Path.Combine(_dir, "sample.json");
        File.WriteAllText(path,
            """{"schemaVersion": 99, "data": {"Name": "future", "Count": 1}}""");

        var data = new JsonStore<SampleData>(path).Load();
        Assert.Equal("future", data.Name);
    }
}
