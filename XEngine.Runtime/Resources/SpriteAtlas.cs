// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

using XEngine.Vector;

namespace XEngine.Runtime.Resources;

/// <summary>CPU pixel source for <see cref="SpriteAtlas.Bake"/>. <see cref="SourceId"/> is the stable Sprite GUID/sub-asset id.</summary>
public readonly struct SpriteAtlasBakeSource
{
    public SpriteAtlasBakeSource(
        string sourceId,
        int width,
        int height,
        Color32[] pixels,
        Float2 pivot,
        float pixelsPerUnit,
        Float4 border,
        Color32[]? secondaryPixels = null,
        string? secondaryName = null)
    {
        SourceId = sourceId;
        Width = width;
        Height = height;
        Pixels = pixels;
        Pivot = pivot;
        PixelsPerUnit = pixelsPerUnit;
        Border = border;
        SecondaryPixels = secondaryPixels;
        SecondaryName = secondaryName;
    }

    public string SourceId { get; }
    public int Width { get; }
    public int Height { get; }
    public Color32[] Pixels { get; }
    public Float2 Pivot { get; }
    public float PixelsPerUnit { get; }
    public Float4 Border { get; }
    public Color32[]? SecondaryPixels { get; }
    public string? SecondaryName { get; }
}

/// <summary>
/// Packed sprite atlas. Bake copies CPU pixels into a shared page, assigns each Sprite a GUID derived
/// from the atlas id + source id, and keeps those Sprite instances across re-packs so references stay valid.
/// </summary>
public sealed class SpriteAtlas : EngineObject
{
    public int Padding = 2;
    public int Extrude;
    public int MaxSize = 2048;
    public bool Trim;

    private readonly Dictionary<string, Sprite> _sprites = new();
    private SpriteAtlasPackResult _packed;
    private Color32[] _pixels = Array.Empty<Color32>();
    private Color32[] _secondaryPixels = Array.Empty<Color32>();

    public int PackedWidth { get; private set; }
    public int PackedHeight { get; private set; }

    public SpriteAtlas() : base("SpriteAtlas") { }

    public static Guid DeriveSpriteGuid(Guid atlasGuid, string sourceId)
    {
        byte[] parentBytes = atlasGuid.ToByteArray();
        byte[] nameBytes = Encoding.UTF8.GetBytes(sourceId ?? string.Empty);
        byte[] combined = new byte[parentBytes.Length + nameBytes.Length];
        parentBytes.CopyTo(combined, 0);
        nameBytes.CopyTo(combined, parentBytes.Length);
        byte[] hash = SHA256.HashData(combined);
        return new Guid(hash.AsSpan(0, 16));
    }

    public Sprite? GetSprite(string sourceId)
    {
        EnsureNotDisposed();
        return _sprites.TryGetValue(sourceId, out Sprite? sprite) ? sprite : null;
    }

    public SpriteAtlasPackedRect FindPackedRect(string sourceId)
    {
        EnsureNotDisposed();
        return _packed.Find(sourceId);
    }

    public Color32 SamplePacked(int x, int y) => Sample(_pixels, PackedWidth, PackedHeight, x, y);

    public Color32 SamplePackedSecondary(int x, int y) => Sample(_secondaryPixels, PackedWidth, PackedHeight, x, y);

    public void Bake(ReadOnlySpan<SpriteAtlasBakeSource> sources)
    {
        EnsureNotDisposed();
        if (AssetID == Guid.Empty)
            AssetID = Guid.NewGuid();

        var requests = new SpriteAtlasPackRequest[sources.Length];
        bool wantsSecondary = false;
        for (int i = 0; i < sources.Length; i++)
        {
            SpriteAtlasBakeSource src = sources[i];
            requests[i] = new SpriteAtlasPackRequest(src.SourceId, src.Width, src.Height);
            if (src.SecondaryPixels != null && src.SecondaryPixels.Length > 0)
                wantsSecondary = true;
        }

        _packed = SpriteAtlasPacker.Pack(requests, new SpriteAtlasPackSettings
        {
            Padding = Padding,
            Extrude = Extrude,
            MaxSize = MaxSize,
            Trim = Trim,
        });
        PackedWidth = _packed.Width;
        PackedHeight = _packed.Height;

        int pixelCount = PackedWidth * PackedHeight;
        _pixels = pixelCount == 0 ? Array.Empty<Color32>() : new Color32[pixelCount];
        _secondaryPixels = wantsSecondary && pixelCount > 0 ? new Color32[pixelCount] : Array.Empty<Color32>();

        for (int i = 0; i < sources.Length; i++)
        {
            SpriteAtlasBakeSource src = sources[i];
            SpriteAtlasPackedRect rect = _packed.Find(src.SourceId);
            Blit(src.Pixels, src.Width, src.Height, _pixels, PackedWidth, PackedHeight, rect.X, rect.Y);
            if (wantsSecondary && src.SecondaryPixels != null)
                Blit(src.SecondaryPixels, src.Width, src.Height, _secondaryPixels, PackedWidth, PackedHeight, rect.X, rect.Y);

            if (!_sprites.TryGetValue(src.SourceId, out Sprite? sprite))
            {
                sprite = new Sprite { Name = src.SourceId };
                sprite.AssetID = DeriveSpriteGuid(AssetID, src.SourceId);
                _sprites[src.SourceId] = sprite;
            }

            sprite.Rect = new SpriteRect(rect.X, rect.Y, src.Width, src.Height);
            sprite.Pivot = src.Pivot;
            sprite.PixelsPerUnit = src.PixelsPerUnit > 0f ? src.PixelsPerUnit : 100f;
            sprite.Border = src.Border;
            sprite.BuildQuadGeometry(Math.Max(1, PackedWidth), Math.Max(1, PackedHeight));

            if (!string.IsNullOrEmpty(src.SecondaryName))
                sprite.SecondaryTextures[src.SecondaryName] = default;
        }
    }

    private static void Blit(Color32[] source, int srcW, int srcH, Color32[] dest, int destW, int destH, int destX, int destY)
    {
        if (source == null || dest == null || srcW <= 0 || srcH <= 0 || destW <= 0 || destH <= 0)
            return;
        int copyW = srcW;
        int copyH = srcH;
        for (int y = 0; y < copyH; y++)
        {
            int dy = destY + y;
            if ((uint)dy >= (uint)destH)
                continue;
            int srcRow = y * srcW;
            int destRow = dy * destW + destX;
            for (int x = 0; x < copyW; x++)
            {
                int dx = destX + x;
                if ((uint)dx >= (uint)destW)
                    continue;
                int srcIndex = srcRow + x;
                if ((uint)srcIndex >= (uint)source.Length)
                    continue;
                dest[destRow + x] = source[srcIndex];
            }
        }
    }

    private static Color32 Sample(Color32[] pixels, int width, int height, int x, int y)
    {
        if (pixels.Length == 0 || (uint)x >= (uint)width || (uint)y >= (uint)height)
            return default;
        return pixels[y * width + x];
    }

    public override void OnDispose()
    {
        foreach (KeyValuePair<string, Sprite> pair in _sprites)
            pair.Value.Dispose();
        _sprites.Clear();
        base.OnDispose();
    }
}
