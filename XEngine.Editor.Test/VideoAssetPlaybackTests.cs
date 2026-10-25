using System.Diagnostics;
using System.Threading;

using XEngine.Editor.GUI;
using XEngine.Runtime;
using XEngine.Runtime.Test.Video;
using XEngine.Runtime.Video;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class VideoAssetPlaybackTests : EditorTestHarness
{
    [FFmpegFact]
    public void ImportedClip_PlaysAfterCacheReload_AndSourceMove()
    {
        FFmpegNativeLoader.EnsureLoaded();
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "XEngine.sln")))
            root = root.Parent;
        Assert.NotNull(root);

        const string original = "Media/视频 clip.mp4";
        CopyIntoAssets(Path.Combine(root!.FullName, "XEngine.Runtime.Test", "Media", "n0-h264-aac.mp4"), original);
        Guid guid = Assets.ImportFile(original);
        var clip = Assert.IsType<VideoClip>(AssetDatabase.Get(guid));
        Assert.Equal(original, clip.AssetPath);
        AssertPlayable(clip, AssetAbsolutePath(original));

        ReopenDatabase();
        clip = Assert.IsType<VideoClip>(AssetDatabase.Get(guid));
        Assert.Equal(original, clip.AssetPath);
        AssertPlayable(clip, AssetAbsolutePath(original));

        const string moved = "Moved/另一个 视频.mp4";
        Assert.True(Assets.MoveAsset(original, moved));
        Assert.False(File.Exists(AssetAbsolutePath(original)));
        AssertPlayable(clip, AssetAbsolutePath(moved));
    }

    private static void AssertPlayable(VideoClip clip, string expectedSource)
    {
        Assert.True(clip.TryGetPayloadPath(out string path), $"No source for logical path '{clip.AssetPath}'");
        Assert.Equal(expectedSource, path);
        using var preview = VideoPreviewSession.CreateForClip(clip, withAudio: false, loop: false, speed: 1f);
        preview.Play();
        var watch = Stopwatch.StartNew();
        while (preview.Texture is null && watch.ElapsedMilliseconds < 10_000)
        {
            preview.Tick();
            Thread.Yield();
        }
        Assert.True(preview.IsPlaying, $"preview state={preview.State}");
        Assert.NotNull(preview.Texture);
    }
}
