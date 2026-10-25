// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

using ImageMagick;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Video;

namespace XEngine.Editor.Importers;

/// <summary>
/// Imports video containers (.mp4, .mov, .m4v, .webm, .mkv) into <see cref="VideoClip"/>
/// assets. Runs the FFmpeg probe + first-frame decode on the background import worker
/// (file IO and a self-contained texture upload only), extracts a cover frame as PNG, and
/// records a SHA-256 content hash. The source media itself is NOT embedded — the build
/// copies it to Content/payload/ keyed by the source GUID (see VideoClip.TryGetPayloadPath).
/// </summary>
[ImporterFor(".mp4", ".mov", ".m4v", ".webm", ".mkv")]
public class VideoImporter : AssetImporter
{
    /// <summary>Cover images are downscaled to fit within this box before PNG encoding.</summary>
    public const int MaxCoverEdge = 512;

    public override int Version => 1;

    public override bool Import(ImportContext ctx)
    {
        try
        {
            using var decoder = new FFmpegVideoDecoder();
            decoder.Open(ctx.AbsolutePath, CancellationToken.None);

            VideoMediaInfo info = decoder.Metadata;
            (int displayWidth, int displayHeight) = info.Video.DisplaySize;

            var clip = new VideoClip
            {
                AssetPath = ctx.AbsolutePath,
            };
            clip.Name = Path.GetFileNameWithoutExtension(ctx.AbsolutePath);
            clip.ImportProbe(
                sourceGuid: ctx.AssetGuid,
                sourceExtension: Path.GetExtension(ctx.AbsolutePath),
                contentHash: ComputeContentHash(ctx.AbsolutePath),
                info);

            VideoFrame? coverFrame = TryDecodeCover(decoder, ctx.AbsolutePath);
            if (coverFrame is not null)
            {
                byte[] png = EncodeCoverPng(coverFrame);
                clip.ImportCoverPng(png);
            }

            ctx.SetMainAsset(clip);
        }
        catch (VideoException ex)
        {
            Debug.LogError($"Failed to import video '{ctx.AbsolutePath}' [{ex.ErrorCode}]: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to import video '{ctx.AbsolutePath}': {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>Streams the source file through SHA-256 (payload identity for dedup/builds).</summary>
    internal static string ComputeContentHash(string absolutePath)
    {
        using var stream = File.OpenRead(absolutePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Decodes the first presentable frame for the cover. A media without a decodable
    /// first frame still imports (audio-only probes are rejected earlier by Open); the
    /// clip simply has no cover.
    /// </summary>
    private static VideoFrame? TryDecodeCover(FFmpegVideoDecoder decoder, string path)
    {
        try
        {
            return decoder.DecodeFirstFrame(CancellationToken.None);
        }
        catch (VideoException ex)
        {
            Debug.LogWarning($"Video '{path}' has no decodable first frame for the cover: {ex.Message}");
            return null;
        }
    }

    /// <summary>RGBA frame → downscaled PNG bytes, top-down rows preserved.</summary>
    internal static byte[] EncodeCoverPng(VideoFrame frame)
    {
        var settings = new MagickReadSettings
        {
            Format = MagickFormat.Rgba,
            Width = (uint)frame.Width,
            Height = (uint)frame.Height,
            Depth = 8,
        };
        using var image = new MagickImage(frame.Data, settings);

        // sws output is top-down; Magick reads Rgba raw as top-down too, so rows match.
        if (image.Width > MaxCoverEdge || image.Height > MaxCoverEdge)
        {
            double scale = Math.Min(
                MaxCoverEdge / (double)image.Width,
                MaxCoverEdge / (double)image.Height);
            image.Resize((uint)Math.Max(1, (int)(image.Width * scale)), (uint)Math.Max(1, (int)(image.Height * scale)));
        }

        return image.ToByteArray(MagickFormat.Png);
    }
}
