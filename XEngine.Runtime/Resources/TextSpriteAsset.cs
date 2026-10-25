// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.Runtime.Resources;

/// <summary>
/// Named/indexed sprite table for rich-text <c>&lt;sprite&gt;</c> tags (TMP-style).
/// Entries should typically share one atlas texture so UI can draw them in a single batch.
/// </summary>
public sealed class TextSpriteAsset : EngineObject
{
    [SerializeField] private List<Entry> _sprites = new();

    public TextSpriteAsset() : base("TextSpriteAsset") { }

    public IReadOnlyList<Entry> Sprites => _sprites;

    public void SetSprites(IEnumerable<Entry> entries)
    {
        _sprites.Clear();
        if (entries == null) return;
        foreach (Entry e in entries)
            _sprites.Add(e);
    }

    public bool TryGet(int index, out Sprite? sprite)
    {
        sprite = null;
        if (index < 0 || index >= _sprites.Count) return false;
        sprite = _sprites[index].Sprite.Res;
        return sprite != null;
    }

    public bool TryGet(string name, out Sprite? sprite)
    {
        sprite = null;
        if (string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i < _sprites.Count; i++)
        {
            Entry e = _sprites[i];
            if (!string.Equals(e.Name, name, StringComparison.Ordinal)) continue;
            sprite = e.Sprite.Res;
            return sprite != null;
        }
        return false;
    }

    public bool TryGetMetrics(in RichInlineSprite tag, float pixelSize, out RichSpriteMetrics metrics)
    {
        metrics = default;
        Sprite? sprite = null;
        if (!string.IsNullOrEmpty(tag.Name))
            TryGet(tag.Name, out sprite);
        else if (tag.Index >= 0)
            TryGet(tag.Index, out sprite);
        if (sprite?.Texture.Res is not Texture2D texture || texture.Width <= 0 || texture.Height <= 0)
            return false;

        float srcW = Maths.Max(1, sprite.Rect.Width);
        float srcH = Maths.Max(1, sprite.Rect.Height);
        float scale = pixelSize / srcH;
        float w = srcW * scale;
        float h = srcH * scale;

        // Sit on the baseline: top is BearingY above baseline in Y-down layout space.
        metrics = new RichSpriteMetrics
        {
            Width = w,
            Height = h,
            BearingX = 0f,
            BearingY = -h,
            UvMin = new Float2(sprite.Rect.X / (float)texture.Width, sprite.Rect.Y / (float)texture.Height),
            UvMax = new Float2(sprite.Rect.MaxX / (float)texture.Width, sprite.Rect.MaxY / (float)texture.Height),
            TextureKey = texture,
            Color = FontColor.White,
        };
        return true;
    }

    [Serializable]
    public struct Entry
    {
        public string Name;
        public AssetRef<Sprite> Sprite;

        public Entry(string name, AssetRef<Sprite> sprite)
        {
            Name = name ?? string.Empty;
            Sprite = sprite;
        }
    }
}
