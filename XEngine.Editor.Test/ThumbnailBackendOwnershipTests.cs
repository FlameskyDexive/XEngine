using System.Collections;
using System.Reflection;
using XEngine.Editor.Thumbnails;
using XEngine.Runtime;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class ThumbnailBackendOwnershipTests : EditorTestHarness
{
    [Fact]
    public void PendingEditorThumbnailWaitsForEditorBackendAndResumesAfterPlay()
    {
        using var queueScope = new ThumbnailQueueScope();
        var generators = (Dictionary<Type, IThumbnailGenerator>)typeof(EditorRegistries)
            .GetField("_thumbnailGenerators", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var generator = new RecordingGenerator();
        generators[typeof(ThumbnailAsset)] = generator;
        var source = ScriptableObject.CreateInstance<ThumbnailAsset>();
        Assets.CreateAsset(source, "Thumbnail.asset");
        var imported = Assert.IsType<ThumbnailAsset>(Assets.Get(source.AssetID));
        string thumbnail = ThumbnailGenerator.GetThumbnailPath(source.AssetID, Assets.ThumbnailsPath);
        var gameBackend = new GameBackend();
        using var gameAsset = new ThumbnailAsset { AssetID = source.AssetID };
        gameBackend.Seed(gameAsset);
        ThumbnailGenerator.Enqueue(source.AssetID, imported);

        try
        {
            // Even if the game backend owns the same GUID, the preview subject and its
            // dependencies belong to the editor. Play must preserve this queued work.
            AssetDatabase.Current = gameBackend;
            ThumbnailGenerator.ProcessOne();
            Assert.Equal(0, generator.Calls);
            Assert.False(File.Exists(thumbnail));

            AssetDatabase.Current = Assets;
            ThumbnailGenerator.ProcessOne();
            Assert.Equal(1, generator.Calls);
            Assert.Same(imported, generator.Subject);
            Assert.Same(Assets, generator.Backend);
            Assert.True(File.Exists(thumbnail));
            ThumbnailGenerator.ProcessOne();
            Assert.Equal(1, generator.Calls);
        }
        finally
        {
            AssetDatabase.Current = Assets;
            generators.Remove(typeof(ThumbnailAsset));
        }
    }

    public sealed class ThumbnailAsset : ScriptableObject { }

    private sealed class RecordingGenerator : IThumbnailGenerator
    {
        public int Calls;
        public EngineObject? Subject;
        public AssetBackendBase? Backend;

        public byte[] Generate(EngineObject asset, string? sourceFilePath)
        {
            Calls++;
            Subject = asset;
            Backend = AssetDatabase.Current;
            return new byte[ThumbnailGenerator.ThumbnailSize * ThumbnailGenerator.ThumbnailSize * 4];
        }
    }

    private sealed class GameBackend : AssetBackendBase
    {
        public void Seed(EngineObject asset) => SetLoaded(asset.AssetID, asset);
        protected override EngineObject? LoadFresh(Guid assetId) => null;
    }

    // Isolate the static thumbnail queue without consuming another fixture's pending jobs.
    private sealed class ThumbnailQueueScope : IDisposable
    {
        private readonly object _queue = typeof(ThumbnailGenerator)
            .GetField("_queue", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        private readonly HashSet<Guid> _queued = (HashSet<Guid>)typeof(ThumbnailGenerator)
            .GetField("_queued", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        private readonly List<object> _jobs = [];
        private readonly Guid[] _ids;

        public ThumbnailQueueScope()
        {
            foreach (object job in (IEnumerable)_queue) _jobs.Add(job);
            _ids = new Guid[_queued.Count];
            _queued.CopyTo(_ids);
            Clear();
        }

        private void Clear()
        {
            _queue.GetType().GetMethod("Clear")!.Invoke(_queue, null);
            _queued.Clear();
        }

        public void Dispose()
        {
            Clear();
            MethodInfo enqueue = _queue.GetType().GetMethod("Enqueue")!;
            foreach (object job in _jobs) enqueue.Invoke(_queue, [job]);
            foreach (Guid id in _ids) _queued.Add(id);
        }
    }
}
