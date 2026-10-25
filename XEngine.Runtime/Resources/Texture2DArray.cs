// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;

namespace XEngine.Runtime.Resources;

/// <summary>
/// A <see cref="Texture"/> with a stack of 2D layers sampled by (u, v, layer).
/// </summary>
public sealed class Texture2DArray : Texture, ISerializable
{
    private static Texture2DArray? _white;
    public static Texture2DArray White
    {
        get
        {
            if (_white == null || !_white.IsValid())
            {
                _white = new Texture2DArray(1, 1, 1, false, TextureImageFormat.Color4b);
                _white.SetData(new Memory<byte>([255, 255, 255, 255]));
            }
            return _white;
        }
    }

    private uint _width, _height, _layers;

    public uint Width { get { EnsureNotDisposed(); return _width; } private set => _width = value; }
    public uint Height { get { EnsureNotDisposed(); return _height; } private set => _height = value; }
    public uint Layers { get { EnsureNotDisposed(); return _layers; } private set => _layers = value; }

    public Texture2DArray() : base(TextureType.Texture2DArray, TextureImageFormat.Color4b) { }

    public Texture2DArray(
        uint width,
        uint height,
        uint layers,
        bool generateMipmaps = false,
        TextureImageFormat imageFormat = TextureImageFormat.Color4b)
        : base(TextureType.Texture2DArray, imageFormat)
    {
        RecreateImage(width, height, layers);

        if (generateMipmaps)
            GenerateMipmaps();

        Graphics.SetTextureFilters(Handle, IsMipmapped ? DefaultMipmapMinFilter : DefaultMinFilter, DefaultMagFilter);
        MinFilter = IsMipmapped ? DefaultMipmapMinFilter : DefaultMinFilter;
        MagFilter = DefaultMagFilter;
    }

    public unsafe void SetDataPtr(void* ptr, int x, int y, int layer, uint width, uint height, uint layerCount)
    {
        EnsureNotDisposed();
        ValidateBoxOperation(x, y, layer, width, height, layerCount);
        Graphics.TexSubImage3D(Handle, 0, x, y, layer, width, height, layerCount, ptr);
    }

    public unsafe void SetData<T>(Memory<T> data, int x, int y, int layer, uint width, uint height, uint layerCount)
        where T : unmanaged
    {
        EnsureNotDisposed();
        ValidateBoxOperation(x, y, layer, width, height, layerCount);
        if (data.Length < width * height * layerCount)
            throw new ArgumentException("Not enough layer data", nameof(data));

        fixed (void* ptr = data.Span)
            Graphics.TexSubImage3D(Handle, 0, x, y, layer, width, height, layerCount, ptr);
    }

    public void SetData<T>(Memory<T> data) where T : unmanaged
    {
        EnsureNotDisposed();
        SetData(data, 0, 0, 0, Width, Height, Layers);
    }

    public unsafe void SetLayerData<T>(int layer, Memory<T> data) where T : unmanaged
    {
        EnsureNotDisposed();
        SetData(data, 0, 0, layer, Width, Height, 1);
    }

    public unsafe void GetData<T>(Memory<T> data) where T : unmanaged
    {
        EnsureNotDisposed();
        if (data.Length < Width * Height * Layers)
            throw new ArgumentException("Insufficient space to store the requested layer data", nameof(data));

        fixed (void* ptr = data.Span)
            Graphics.GetTexImage(Handle, 0, ptr);
    }

    public int GetSize()
    {
        EnsureNotDisposed();
        return checked((int)Width * (int)Height * (int)Layers * GetBytesPerPixel(ImageFormat));
    }

    public void SetWrapModes(TextureWrap sWrapMode, TextureWrap tWrapMode)
    {
        EnsureNotDisposed();
        Graphics.SetWrapS(Handle, sWrapMode);
        Graphics.SetWrapT(Handle, tWrapMode);
    }

    public unsafe void RecreateImage(uint width, uint height, uint layers)
    {
        EnsureNotDisposed();
        ValidateTextureSize(width, height, layers);
        Width = width;
        Height = height;
        Layers = layers;
        Graphics.TexImage3D(Handle, 0, Width, Height, Layers, (void*)0);
    }

    private void ValidateTextureSize(uint width, uint height, uint layers)
    {
        if (width == 0 || width > Graphics.MaxTextureSize)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height == 0 || height > Graphics.MaxTextureSize)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (layers == 0 || layers > Graphics.MaxArrayTextureLayers)
            throw new ArgumentOutOfRangeException(nameof(layers));
    }

    private void ValidateBoxOperation(int x, int y, int layer, uint width, uint height, uint layerCount)
    {
        if (x < 0 || x >= Width)
            throw new ArgumentOutOfRangeException(nameof(x));
        if (y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(y));
        if (layer < 0 || layer >= Layers)
            throw new ArgumentOutOfRangeException(nameof(layer));
        if (width == 0 || height == 0 || layerCount == 0)
            throw new ArgumentOutOfRangeException("Box size must be greater than 0");
        if (width > Width - x || height > Height - y || layerCount > Layers - layer)
            throw new ArgumentOutOfRangeException("Specified box is outside of the texture's storage");
    }

    public void Serialize(ref EchoObject compoundTag, SerializationContext ctx)
    {
        compoundTag.Add("Width", new(Width));
        compoundTag.Add("Height", new(Height));
        compoundTag.Add("Layers", new(Layers));
        compoundTag.Add("IsMipMapped", new(IsMipmapped));
        compoundTag.Add("ImageFormat", new((int)ImageFormat));
        compoundTag.Add("MinFilter", new((int)MinFilter));
        compoundTag.Add("MagFilter", new((int)MagFilter));
        compoundTag.Add("Wrap", new((int)WrapMode));
        Memory<byte> memory = new byte[GetSize()];
        GetData(memory);
        compoundTag.Add("Data", new(memory.ToArray()));
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        Width = value["Width"].UIntValue;
        Height = value["Height"].UIntValue;
        Layers = value["Layers"].UIntValue;
        bool isMipMapped = value["IsMipMapped"].BoolValue;
        TextureImageFormat imageFormat = (TextureImageFormat)value["ImageFormat"].IntValue;
        var minFilter = (TextureMin)value["MinFilter"].IntValue;
        var magFilter = (TextureMag)value["MagFilter"].IntValue;
        var wrap = (TextureWrap)value["Wrap"].IntValue;

        typeof(Texture2DArray).GetConstructor(
            [typeof(uint), typeof(uint), typeof(uint), typeof(bool), typeof(TextureImageFormat)])!
            .Invoke(this, [Width, Height, Layers, false, imageFormat]);

        SetData(value["Data"].ByteArrayValue.AsMemory());

        if (isMipMapped)
            GenerateMipmaps();

        SetTextureFilters(minFilter, magFilter);
        SetWrapModes(wrap, wrap);
    }
}
