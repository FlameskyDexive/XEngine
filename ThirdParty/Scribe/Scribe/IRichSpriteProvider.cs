// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Scribe;

/// <summary>Resolved metrics for a rich-text inline sprite placeholder.</summary>
public struct RichSpriteMetrics
{
    public float Width;
    public float Height;
    /// <summary>Horizontal offset from the pen to the sprite's left edge.</summary>
    public float BearingX;
    /// <summary>Vertical offset from the baseline to the sprite's top (typically negative in Y-down).</summary>
    public float BearingY;
    public Float2 UvMin;
    public Float2 UvMax;
    /// <summary>Host texture key (e.g. Texture2D). Compared by reference for batching.</summary>
    public object TextureKey;
    public FontColor Color;
}

/// <summary>Host-supplied resolver for <c>&lt;sprite&gt;</c> tags during rich layout.</summary>
public interface IRichSpriteProvider
{
    bool TryGetMetrics(in RichInlineSprite sprite, float pixelSize, out RichSpriteMetrics metrics);
}
