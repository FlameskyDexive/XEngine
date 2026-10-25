using System;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Packed world atlas plus per-type top/side/bottom slice indices.</summary>
public sealed class VoxelAtlas
{
    public Texture2DArray Texture { get; }
    public uint Size { get; }

    private readonly ushort[] _top;
    private readonly ushort[] _side;
    private readonly ushort[] _bottom;
    private readonly byte[][] _layers;

    public VoxelAtlas(Texture2DArray texture, uint size, ushort[] top, ushort[] side, ushort[] bottom)
        : this(texture, size, top, side, bottom, layers: null)
    {
    }

    public VoxelAtlas(
        Texture2DArray texture,
        uint size,
        ushort[] top,
        ushort[] side,
        ushort[] bottom,
        byte[][]? layers)
    {
        Texture = texture;
        Size = size;
        _top = top;
        _side = side;
        _bottom = bottom;
        _layers = layers ?? [];
    }

    public int LayerCount => (int)Texture.Layers;

    public ushort Slice(ushort typeIndex, int axis, bool positive)
    {
        ushort[] table = axis == 1 ? (positive ? _top : _bottom) : _side;
        return typeIndex < table.Length ? table[typeIndex] : (ushort)0;
    }

    public void GetFaceSlices(ushort typeIndex, out ushort top, out ushort side, out ushort bottom)
    {
        top = typeIndex < _top.Length ? _top[typeIndex] : (ushort)0;
        side = typeIndex < _side.Length ? _side[typeIndex] : (ushort)0;
        bottom = typeIndex < _bottom.Length ? _bottom[typeIndex] : (ushort)0;
    }

    public byte[] CopyLayer(int layer)
    {
        if ((uint)layer >= (uint)_layers.Length)
            throw new ArgumentOutOfRangeException(nameof(layer));
        return (byte[])_layers[layer].Clone();
    }

    public Color SampleLayer(int layer, float u, float v)
    {
        if ((uint)layer >= (uint)_layers.Length)
            throw new ArgumentOutOfRangeException(nameof(layer));
        return VoxelFaceTextures.Sample(_layers[layer], Size, u, v);
    }
}
