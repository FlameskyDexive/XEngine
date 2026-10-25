// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Rendering;

namespace XEngine.Runtime;

/// <summary>
/// Eight named bits used by lights and decals to select which renderers they affect.
/// Independent of <see cref="LayerMask"/> / <see cref="GameObject.LayerIndex"/> (camera culling
/// and physics). Default is <see cref="Everything"/> so existing scenes keep full lighting.
/// </summary>
public struct RenderingLayerMask
{
    public const int Count = ShaderConfig.MaxRenderingLayers;

    public static readonly RenderingLayerMask Everything = new() { mask = (1u << Count) - 1 };
    public static readonly RenderingLayerMask Nothing = new() { mask = 0 };
    public static readonly RenderingLayerMask Default = FromIndex(0);

    public static readonly string[] Names =
    [
        "Default",
        "Layer1",
        "Layer2",
        "Layer3",
        "Layer4",
        "Layer5",
        "Layer6",
        "Layer7",
    ];

    [SerializeField]
    private uint mask;

    public uint Mask => mask;

    public void Clear() => mask = 0;

    public bool HasLayer(int index) => index >= 0 && index < Count && (mask & (1u << index)) != 0;

    public void SetLayer(int index)
    {
        if (index < 0 || index >= Count) return;
        mask |= 1u << index;
    }

    public void RemoveLayer(int index)
    {
        if (index < 0 || index >= Count) return;
        mask &= ~(1u << index);
    }

    public static RenderingLayerMask FromIndex(int index)
    {
        var value = new RenderingLayerMask();
        value.SetLayer(index);
        return value;
    }

    public static bool Overlaps(uint lightLayers, uint objectLayers) => (lightLayers & objectLayers) != 0;

    public static RenderingLayerMask operator |(RenderingLayerMask mask1, RenderingLayerMask mask2)
        => new() { mask = mask1.mask | mask2.mask };

    public static RenderingLayerMask operator &(RenderingLayerMask mask1, RenderingLayerMask mask2)
        => new() { mask = mask1.mask & mask2.mask };

    public override bool Equals(object? obj)
    {
        if (obj is not RenderingLayerMask other)
            return false;
        return mask == other.mask;
    }

    public override int GetHashCode() => mask.GetHashCode();

    public static string LayerToName(int index)
        => index >= 0 && index < Names.Length ? Names[index] : string.Empty;

    public static int NameToLayer(string name)
    {
        if (string.IsNullOrEmpty(name))
            return -1;
        for (int i = 0; i < Names.Length; i++)
        {
            if (Names[i] == name)
                return i;
        }
        return -1;
    }
}
