// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>CPU import product for a Photoshop document. Layer sprites are added later.</summary>
public sealed class PsdAsset : ScriptableObject
{
    public int Width;
    public int Height;
}

/// <summary>Named PSD layer. Stable source id is <c>layer:{Index}</c> so a rename keeps references.</summary>
public sealed class PsdLayerAsset : ScriptableObject
{
    public int Index;
    public string LayerName = "";
}
