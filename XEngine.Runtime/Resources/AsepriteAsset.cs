// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime;

/// <summary>CPU import product for an Aseprite document. Opaque frames also emit a packed sheet + sprites.</summary>
public sealed class AsepriteAsset : ScriptableObject
{
    public int Width;
    public int Height;
    public int FrameCount;
    public AssetRef<Texture2D> Texture;
}

/// <summary>One Aseprite frame. Stable source id is <c>frame:{Index}</c>.</summary>
public sealed class AsepriteFrameAsset : ScriptableObject
{
    public int Index;
    public int DurationMs;
    public AssetRef<Sprite> Sprite;
}

/// <summary>Named Aseprite tag. Stable source id is <c>tag:{TagName}</c>.</summary>
public sealed class AsepriteTagAsset : ScriptableObject
{
    public string TagName = "";
    public int FromFrame;
    public int ToFrame;
    public List<AssetRef<Sprite>> Sprites = new();
}

/// <summary>Named Aseprite slice. Stable source id is <c>slice:{SliceName}</c>.</summary>
public sealed class AsepriteSliceAsset : ScriptableObject
{
    public string SliceName = "";
    public int X;
    public int Y;
    public int Width;
    public int Height;
}
