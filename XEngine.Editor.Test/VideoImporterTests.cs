// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Editor.Importers;
using XEngine.Runtime.Video;
using XEngine.Runtime.Test.Video;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class VideoImporterTests : IDisposable
{
    private static readonly string MediaRoot = Path.Combine(
        FindRepositoryRoot(), "XEngine.Runtime.Test", "Media");

    private readonly string _tempDir;

    public VideoImporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "xengine-videoimporter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    private (VideoClip? Clip, ImportContext Context) ImportCopy(string sourceMedia)
    {
        string path = Path.Combine(_tempDir, sourceMedia);
        File.Copy(Path.Combine(MediaRoot, sourceMedia), path, true);

        var guid = Guid.NewGuid();
        var ctx = new ImportContext(guid, path, settings: null);
        var importer = new VideoImporter();

        Assert.True(importer.Import(ctx), $"Import of '{sourceMedia}' failed");
        var clip = Assert.IsType<VideoClip>(ctx.MainAsset);
        return (clip, ctx);
    }

    [FFmpegFact]
    public void Import_Mp4_ProducesMetadataCoverAndGuid()
    {
        FFmpegNativeLoader.EnsureLoaded();
        (VideoClip clip, ImportContext ctx) = ImportCopy("n0-h264-aac.mp4");

        Assert.Equal(ctx.AssetGuid, clip.SourceGuid);
        Assert.Equal(ctx.AssetGuid, clip.AssetID);
        Assert.Equal(".mp4", clip.SourceExtension);
        Assert.Equal(64, clip.ContentHash.Length);
        Assert.Equal("h264", clip.VideoCodec);
        Assert.Equal((320, 240), clip.CodedSize);
        Assert.Equal((320, 240), clip.DisplaySize);
        Assert.InRange(clip.Fps, 29.0, 31.0);
        Assert.InRange(clip.DurationSeconds, 1.8, 2.2);
        Assert.True(clip.HasAudio);
        Assert.Equal("aac", clip.AudioCodec);
        Assert.Equal(44100, clip.AudioSampleRate);
        Assert.Equal(2, clip.AudioChannels);
        Assert.True(clip.CanSeek);

        Texture2D? cover = clip.Cover;
        Assert.NotNull(cover);
        Assert.Equal(320u, cover.Width);
        Assert.Equal(240u, cover.Height);
    }

    [FFmpegFact]
    public void Import_RotatedMp4_AppliesRotationToDisplaySize()
    {
        FFmpegNativeLoader.EnsureLoaded();
        (VideoClip clip, _) = ImportCopy("n0-rotated.mp4");

        Assert.Equal(90, clip.RotationDegrees);
        Assert.Equal((240, 320), clip.DisplaySize);
        Assert.Equal((320, 240), clip.CodedSize);
    }

    [FFmpegFact]
    public void Import_WebM_ProbesVp9Opus()
    {
        FFmpegNativeLoader.EnsureLoaded();
        (VideoClip clip, _) = ImportCopy("n0-vp9-opus.webm");

        Assert.Equal("vp9", clip.VideoCodec);
        Assert.Equal("opus", clip.AudioCodec);
        Assert.Equal(".webm", clip.SourceExtension);
    }

    [FFmpegFact]
    public void Import_CorruptMedia_FailsImport()
    {
        FFmpegNativeLoader.EnsureLoaded();
        string path = Path.Combine(_tempDir, "n0-corrupt.mp4");
        File.Copy(Path.Combine(MediaRoot, "n0-corrupt.mp4"), path, true);

        var ctx = new ImportContext(Guid.NewGuid(), path, settings: null);
        Assert.False(new VideoImporter().Import(ctx));
        Assert.Null(ctx.MainAsset);
    }

    [FFmpegFact]
    public void Reimport_SameBytes_SameContentHash_Dedups()
    {
        FFmpegNativeLoader.EnsureLoaded();
        (VideoClip first, _) = ImportCopy("n0-h264-aac.mp4");
        (VideoClip second, _) = ImportCopy("n0-h264-aac.mp4");

        // Distinct assets (distinct source GUIDs), identical media identity.
        Assert.NotEqual(first.SourceGuid, second.SourceGuid);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.True(first.SameMedia(second));
    }

    [FFmpegFact]
    public void Reimport_ModifiedBytes_ChangesContentHash()
    {
        FFmpegNativeLoader.EnsureLoaded();
        string path = Path.Combine(_tempDir, "changing.mp4");
        string source = Path.Combine(MediaRoot, "n0-h264-aac.mp4");

        File.Copy(source, path, true);
        var firstCtx = new ImportContext(Guid.NewGuid(), path, settings: null);
        Assert.True(new VideoImporter().Import(firstCtx));
        var first = (VideoClip)firstCtx.MainAsset!;

        // Simulate an edited source: append bytes (container tolerates trailing junk,
        // the hash must not).
        using (var stream = File.OpenWrite(path))
        {
            stream.Seek(0, SeekOrigin.End);
            stream.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        }

        var secondCtx = new ImportContext(Guid.NewGuid(), path, settings: null);
        Assert.True(new VideoImporter().Import(secondCtx));
        var second = (VideoClip)secondCtx.MainAsset!;

        Assert.NotEqual(first.ContentHash, second.ContentHash);
        Assert.False(first.SameMedia(second));
    }

    [FFmpegFact]
    public void VideoClip_RoundTripsThroughEchoSerialization()
    {
        FFmpegNativeLoader.EnsureLoaded();
        (VideoClip clip, _) = ImportCopy("n0-h264-aac.mp4");

        var compound = new EchoObject(EchoType.Compound, null);
        var serializationCtx = new SerializationContext();
        clip.Serialize(ref compound, serializationCtx);

        var restored = new VideoClip();
        restored.Deserialize(compound, serializationCtx);

        Assert.Equal(clip.Name, restored.Name);
        Assert.Equal(clip.SourceGuid, restored.SourceGuid);
        Assert.Equal(clip.SourceExtension, restored.SourceExtension);
        Assert.Equal(clip.ContentHash, restored.ContentHash);
        Assert.Equal(clip.VideoCodec, restored.VideoCodec);
        Assert.Equal(clip.DisplaySize, restored.DisplaySize);
        Assert.Equal(clip.DurationSeconds, restored.DurationSeconds);
        Assert.Equal(clip.HasAudio, restored.HasAudio);
        Assert.Equal(clip.PayloadFileName, restored.PayloadFileName);

        // Cover survives as PNG bytes and lazily decodes again.
        Texture2D? cover = restored.Cover;
        Assert.NotNull(cover);
        Assert.Equal(320u, cover.Width);
    }

    [Fact]
    public void EncodeCoverPng_DownsamplesLargeFrames()
    {
        // 1280x720 RGBA frame of a gradient.
        var frame = new VideoFrame
        {
            Data = new byte[1280 * 720 * 4],
            Width = 1280,
            Height = 720,
        };
        for (int y = 0; y < 720; y++)
        {
            for (int x = 0; x < 1280; x++)
            {
                int i = (y * 1280 + x) * 4;
                frame.Data[i] = (byte)(x * 255 / 1280);
                frame.Data[i + 1] = (byte)(y * 255 / 720);
                frame.Data[i + 2] = 128;
                frame.Data[i + 3] = 255;
            }
        }

        byte[] png = VideoImporter.EncodeCoverPng(frame);
        Assert.NotEmpty(png);

        using var stream = new MemoryStream(png);
        var image = new ImageMagick.MagickImage(stream);
        Assert.True(image.Width <= VideoImporter.MaxCoverEdge);
        Assert.True(image.Height <= VideoImporter.MaxCoverEdge);
        Assert.Equal(512u, image.Width); // 16:9 keeps 512 as the wide edge
        Assert.Equal(288u, image.Height);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!.FullName;
    }
}
