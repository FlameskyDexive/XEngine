// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>Cycles output tiles by time. Frame index is <c>floor(time * fps) mod count</c>.</summary>
[CreateAssetMenu("2D/Animated Tile", Extension = ".animatedtile", Order = 63)]
public sealed class AnimatedTile : TileBase
{
    public TileBase?[] Frames = [];
    public float FramesPerSecond = 1f;

    public TileBase? GetFrame(float timeSeconds)
    {
        int count = Frames.Length;
        if (count == 0)
            return null;

        float fps = FramesPerSecond > 0f ? FramesPerSecond : 1f;
        if (timeSeconds < 0f)
            timeSeconds = 0f;

        int index = (int)(timeSeconds * fps) % count;
        if (index < 0)
            index += count;
        return Frames[index];
    }
}
