using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Builds a <see cref="Texture2DArray"/> from voxel face colors or loaded textures.</summary>
public static class TextureArrayPacker
{
    public const uint DefaultSize = 8;

    public static byte[] SolidRgba(uint size, Color color)
    {
        byte r = ToByte(color.R);
        byte g = ToByte(color.G);
        byte b = ToByte(color.B);
        byte a = ToByte(color.A);
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }
        return pixels;
    }

    public static VoxelAtlas PackSolidFaces(
        IReadOnlyList<(ushort Type, Color Top, Color Side, Color Bottom)> voxels,
        uint size = DefaultSize)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        int maxType = 0;
        for (int i = 0; i < voxels.Count; i++)
            maxType = Math.Max(maxType, voxels[i].Type);

        var top = new ushort[maxType + 1];
        var side = new ushort[maxType + 1];
        var bottom = new ushort[maxType + 1];
        var layers = new List<byte[]>();
        var interned = new Dictionary<ColorKey, ushort>();

        for (int i = 0; i < voxels.Count; i++)
        {
            (ushort type, Color topColor, Color sideColor, Color bottomColor) = voxels[i];
            top[type] = InternColor(interned, layers, size, topColor);
            side[type] = InternColor(interned, layers, size, sideColor);
            bottom[type] = InternColor(interned, layers, size, bottomColor);
        }

        return Finish(layers, size, top, side, bottom);
    }

    public static VoxelAtlas PackPixelFaces(
        IReadOnlyList<(ushort Type, byte[] Top, byte[] Side, byte[] Bottom)> voxels,
        uint size = DefaultSize)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        int maxType = 0;
        for (int i = 0; i < voxels.Count; i++)
            maxType = Math.Max(maxType, voxels[i].Type);

        var top = new ushort[maxType + 1];
        var side = new ushort[maxType + 1];
        var bottom = new ushort[maxType + 1];
        var layers = new List<byte[]>();
        var interned = new Dictionary<string, ushort>(StringComparer.Ordinal);

        for (int i = 0; i < voxels.Count; i++)
        {
            (ushort type, byte[] topPixels, byte[] sidePixels, byte[] bottomPixels) = voxels[i];
            top[type] = InternPixels(interned, layers, size, topPixels);
            side[type] = InternPixels(interned, layers, size, sidePixels);
            bottom[type] = InternPixels(interned, layers, size, bottomPixels);
        }

        return Finish(layers, size, top, side, bottom);
    }

    public static VoxelAtlas PackDefinitions(IReadOnlyList<VoxelDefinition> definitions, uint size = DefaultSize)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        int maxType = definitions.Count;
        var top = new ushort[maxType + 1];
        var side = new ushort[maxType + 1];
        var bottom = new ushort[maxType + 1];
        var layers = new List<byte[]>();
        var interned = new Dictionary<string, ushort>(StringComparer.Ordinal);

        for (int i = 0; i < definitions.Count; i++)
        {
            VoxelDefinition def = definitions[i];
            ushort type = (ushort)(i + 1);
            Color tint = def.TintColor;
            top[type] = InternFace(interned, layers, size, def.TextureTop, tint);
            side[type] = InternFace(interned, layers, size, def.TextureSide, tint);
            bottom[type] = InternFace(interned, layers, size, def.TextureBottom, tint);
        }

        return Finish(layers, size, top, side, bottom);
    }

    private static VoxelAtlas Finish(List<byte[]> layers, uint size, ushort[] top, ushort[] side, ushort[] bottom)
    {
        if (layers.Count == 0)
            layers.Add(SolidRgba(size, Color.White));

        var texture = new Texture2DArray(size, size, (uint)layers.Count);
        for (int i = 0; i < layers.Count; i++)
            texture.SetLayerData(i, layers[i].AsMemory());

        return new VoxelAtlas(texture, size, top, side, bottom, layers.ToArray());
    }

    private static ushort InternColor(
        Dictionary<ColorKey, ushort> interned,
        List<byte[]> layers,
        uint size,
        Color color)
    {
        var key = new ColorKey(ToByte(color.R), ToByte(color.G), ToByte(color.B), ToByte(color.A));
        if (interned.TryGetValue(key, out ushort slice))
            return slice;

        slice = (ushort)layers.Count;
        interned[key] = slice;
        layers.Add(SolidRgba(size, color));
        return slice;
    }

    private static ushort InternFace(
        Dictionary<string, ushort> interned,
        List<byte[]> layers,
        uint size,
        AssetRef<Texture2D> texture,
        Color tint)
    {
        string key;
        byte[] pixels;
        if (texture.ResWeak is Texture2D live)
        {
            key = live.AssetID != Guid.Empty
                ? live.AssetID.ToString("D")
                : "rt:" + RuntimeHelpers.GetHashCode(live);
            pixels = ReadTextureOrSolid(live, size, tint);
        }
        else if (texture.AssetID != Guid.Empty && VoxelFaceTextures.TryGetOwned(texture.AssetID, out byte[] owned, out uint ownedSize))
        {
            key = texture.AssetID.ToString("D");
            pixels = ownedSize == size ? owned : ResizeNearest(owned, ownedSize, ownedSize, size);
        }
        else if (texture.AssetID != Guid.Empty)
        {
            key = texture.AssetID.ToString("D");
            pixels = SolidRgba(size, tint);
        }
        else
        {
            key = TintKey(tint);
            pixels = SolidRgba(size, tint);
        }

        if (interned.TryGetValue(key, out ushort slice))
            return slice;

        slice = (ushort)layers.Count;
        interned[key] = slice;
        layers.Add(pixels);
        return slice;
    }

    private static ushort InternPixels(
        Dictionary<string, ushort> interned,
        List<byte[]> layers,
        uint size,
        byte[] pixels)
    {
        byte[] normalized = pixels.Length == size * size * 4
            ? pixels
            : ResizeNearest(pixels, (uint)Math.Sqrt(pixels.Length / 4d), (uint)Math.Sqrt(pixels.Length / 4d), size);
        string key = PixelKey(normalized);
        if (interned.TryGetValue(key, out ushort slice))
            return slice;

        slice = (ushort)layers.Count;
        interned[key] = slice;
        layers.Add(normalized);
        return slice;
    }

    private static string PixelKey(byte[] pixels)
    {
        uint hash = 2166136261;
        for (int i = 0; i < pixels.Length; i++)
            hash = (hash ^ pixels[i]) * 16777619;
        return hash.ToString("x8") + ":" + pixels.Length;
    }

    private static byte[] ReadTextureOrSolid(Texture2D texture, uint size, Color tint)
    {
        try
        {
            uint width = texture.Width;
            uint height = texture.Height;
            byte[] raw;
            if (texture._importedPixels is { Length: > 0 } imported && imported.Length == width * height * 4)
            {
                raw = imported;
            }
            else
            {
                raw = new byte[width * height * 4];
                texture.GetData<byte>(raw.AsMemory());
            }

            return width == size && height == size ? raw : ResizeNearest(raw, width, height, size);
        }
        catch
        {
            return texture.AssetID != Guid.Empty && VoxelFaceTextures.TryGetOwned(texture.AssetID, out byte[] owned, out uint ownedSize)
                ? (ownedSize == size ? owned : ResizeNearest(owned, ownedSize, ownedSize, size))
                : SolidRgba(size, tint);
        }
    }

    private static byte[] ResizeNearest(byte[] src, uint srcW, uint srcH, uint dst)
    {
        var dest = new byte[dst * dst * 4];
        for (uint y = 0; y < dst; y++)
        {
            uint sy = y * srcH / dst;
            for (uint x = 0; x < dst; x++)
            {
                uint sx = x * srcW / dst;
                int si = (int)((sy * srcW + sx) * 4);
                int di = (int)((y * dst + x) * 4);
                dest[di] = src[si];
                dest[di + 1] = src[si + 1];
                dest[di + 2] = src[si + 2];
                dest[di + 3] = src[si + 3];
            }
        }

        return dest;
    }

    private static string TintKey(Color color) =>
        $"tint:{ToByte(color.R):x2}{ToByte(color.G):x2}{ToByte(color.B):x2}{ToByte(color.A):x2}";

    private static byte ToByte(float channel) => (byte)Math.Clamp((int)(channel * 255f + 0.5f), 0, 255);

    private readonly struct ColorKey : IEquatable<ColorKey>
    {
        public readonly byte R, G, B, A;

        public ColorKey(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public bool Equals(ColorKey other) => R == other.R && G == other.G && B == other.B && A == other.A;
        public override bool Equals(object? obj) => obj is ColorKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
    }
}
