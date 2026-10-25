// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

#if XENGINE_MOBILE
using StbImageSharp;
#else
using ImageMagick;
#endif

using XEngine.Echo;

namespace XEngine.Runtime.Resources;

/// <summary>How an imported image supplies the texture alpha channel.</summary>
public enum TextureAlphaSource
{
    /// <summary>Ignore source alpha and make every pixel opaque.</summary>
    None = 0,

    /// <summary>Keep the alpha channel supplied by the source image.</summary>
    FromInput = 1,

    /// <summary>Replace alpha with the un-gamma-corrected RGB average.</summary>
    FromGrayScale = 2,
}

/// <summary>
/// A <see cref="Texture"/> whose image has two dimensions and support for multisampling.
/// </summary>
public sealed class Texture2D : Texture, ISerializable
{
    private uint _width;
    private uint _height;

    /// <summary>Known-good pixel bytes captured at import time (FromImage). Preferred over the
    /// GPU readback in <see cref="Serialize"/> for the first cache write — the async render-thread
    /// readback can silently return zeros, which would poison the cache for every subsequent load
    /// (the "all textures are black" root cause). Cleared after first serialization.</summary>
    internal byte[]? _importedPixels;

    /// <summary>The width of this <see cref="Texture2D"/>.</summary>
    public uint Width { get { EnsureNotDisposed(); return _width; } private set => _width = value; }

    /// <summary>The height of this <see cref="Texture2D"/>.</summary>
    public uint Height { get { EnsureNotDisposed(); return _height; } private set => _height = value; }

    /// <summary>
    /// Authored color space of the source pixels: <c>true</c> = sRGB color art (the default),
    /// <c>false</c> = linear data (normal maps, masks, lightmaps). Sampling decodes RGB only when
    /// <see cref="Texture.ImageFormat"/> is an sRGB format. Legacy UNORM textures retain their
    /// shader-side decoding contract.
    /// </summary>
    public bool Srgb { get; set; } = true;

    public Texture2D() : base(TextureType.Texture2D, TextureImageFormat.Color4b) { }

    /// <summary>
    /// Creates a <see cref="Texture2D"/> with the desired parameters but no image data.
    /// </summary>
    /// <param name="width">The width of the <see cref="Texture2D"/>.</param>
    /// <param name="height">The height of the <see cref="Texture2D"/>.</param>
    /// <param name="generateMipmaps">Whether to generate mipmaps for this <see cref="Texture2D"/>.</param>
    /// <param name="imageFormat">The image format for this <see cref="Texture2D"/>.</param>
    public Texture2D(uint width, uint height, bool generateMipmaps = false, TextureImageFormat imageFormat = TextureImageFormat.Color4b)
        : this(width, height, generateMipmaps, imageFormat, 1)
    {
    }

    /// <param name="sampleCount">
    /// Multisample count (1 = single-sample). Above 1 this becomes a render-target-only attachment:
    /// it has no mip chain, cannot be uploaded to or sampled, and must be resolved into a
    /// single-sample texture before anything reads it.
    /// </param>
    public Texture2D(uint width, uint height, bool generateMipmaps, TextureImageFormat imageFormat, uint sampleCount)
        : base(TextureType.Texture2D, imageFormat, sampleCount)
    {
        if (sampleCount > 1 && generateMipmaps)
            throw new ArgumentException("Multisample textures cannot be mipmapped.", nameof(generateMipmaps));

        RecreateImage(width, height); //This also binds the texture

        if (generateMipmaps)
            GenerateMipmaps();

        // Sampler state is meaningless for a multisample attachment and some backends reject it.
        if (sampleCount == 1)
            Graphics.SetTextureFilters(Handle, IsMipmapped ? DefaultMipmapMinFilter : DefaultMinFilter, DefaultMagFilter);
        MinFilter = IsMipmapped ? DefaultMipmapMinFilter : DefaultMinFilter;
        MagFilter = DefaultMagFilter;
    }

    /// <summary>
    /// Sets the data of an area of the <see cref="Texture2D"/>.
    /// </summary>
    /// <param name="ptr">The pointer from which the pixel data will be read.</param>
    /// <param name="rectX">The X coordinate of the first pixel to write.</param>
    /// <param name="rectY">The Y coordinate of the first pixel to write.</param>
    /// <param name="rectWidth">The width of the rectangle of pixels to write.</param>
    /// <param name="rectHeight">The height of the rectangle of pixels to write.</param>
    public unsafe void SetDataPtr(void* ptr, int rectX, int rectY, uint rectWidth, uint rectHeight)
    {
        EnsureNotDisposed();
        ValidateRectOperation(rectX, rectY, rectWidth, rectHeight);

        Graphics.TexSubImage2D(Handle, 0, rectX, rectY, rectWidth, rectHeight, ptr);
    }

    /// <summary>
    /// Sets the data of an area of the <see cref="Texture2D"/>.
    /// </summary>
    /// <typeparam name="T">A struct with the same format as this <see cref="Texture2D"/>'s pixels.</typeparam>
    /// <param name="data">A <see cref="Memory{T}"/> containing the new pixel data.</param>
    /// <param name="rectX">The X coordinate of the first pixel to write.</param>
    /// <param name="rectY">The Y coordinate of the first pixel to write.</param>
    /// <param name="rectWidth">The width of the rectangle of pixels to write.</param>
    /// <param name="rectHeight">The height of the rectangle of pixels to write.</param>
    public unsafe void SetData<T>(Memory<T> data, int rectX, int rectY, uint rectWidth, uint rectHeight) where T : unmanaged
    {
        EnsureNotDisposed();
        ValidateRectOperation(rectX, rectY, rectWidth, rectHeight);
        if (data.Length < rectWidth * rectHeight)
            throw new ArgumentException("Not enough pixel data", nameof(data));

        fixed (void* ptr = data.Span)
            Graphics.TexSubImage2D(Handle, 0, rectX, rectY, rectWidth, rectHeight, ptr);
    }

    /// <summary>
    /// Sets the data of the entire <see cref="Texture2D"/>.
    /// </summary>
    /// <typeparam name="T">A struct with the same format as this <see cref="Texture2D"/>'s pixels.</typeparam>
    /// <param name="data">A <see cref="ReadOnlySpan{T}"/> containing the new pixel data.</param>
    public void SetData<T>(Memory<T> data) where T : unmanaged
    {
        EnsureNotDisposed();
        SetData(data, 0, 0, Width, Height);
    }

    /// <summary>
    /// Gets the data of the entire <see cref="Texture2D"/>.
    /// </summary>
    /// <param name="ptr">The pointer to which the pixel data will be written.</param>
    public unsafe void GetDataPtr(void* ptr)
    {
        EnsureNotDisposed();
        Graphics.GetTexImage(Handle, 0, ptr);
    }

    /// <summary>
    /// Gets the data of the entire <see cref="Texture2D"/>.
    /// </summary>
    /// <typeparam name="T">A struct with the same format as this <see cref="Texture2D"/>'s pixels.</typeparam>
    /// <param name="data">A <see cref="Span{T}"/> in which to write the pixel data.</param>
    public unsafe void GetData<T>(Memory<T> data) where T : unmanaged
    {
        EnsureNotDisposed();
        if (data.Length < Width * Height)
            throw new ArgumentException("Insufficient space to store the requested pixel data", nameof(data));

        fixed (void* ptr = data.Span)
            Graphics.GetTexImage(Handle, 0, ptr);
    }

    public int GetSize()
    {
        EnsureNotDisposed();
        if (Texture.IsCompressedFormat(ImageFormat))
            return Texture.GetDataSize(ImageFormat, Width, Height);
        int size = (int)Width * (int)Height;
        switch (ImageFormat)
        {
            case TextureImageFormat.UnsignedInt:
            case TextureImageFormat.Int:
            case TextureImageFormat.Float:
                return size * 4;
            case TextureImageFormat.UnsignedInt2:
            case TextureImageFormat.Int2:
            case TextureImageFormat.Float2:
                return size * 4 * 2;
            case TextureImageFormat.UnsignedInt3:
            case TextureImageFormat.Int3:
            case TextureImageFormat.Float3:
                return size * 4 * 3;
            case TextureImageFormat.UnsignedInt4:
            case TextureImageFormat.Int4:
            case TextureImageFormat.Float4:
                return size * 4 * 4;
            case TextureImageFormat.Depth16f:
                return size * 2;
            case TextureImageFormat.Depth24f:
                return size * 3;
            case TextureImageFormat.Depth32f:
                return size * 4;

            case TextureImageFormat.Short:
            case TextureImageFormat.UnsignedShort:
                return size * 1 * 2;
            case TextureImageFormat.Short2:
            case TextureImageFormat.UnsignedShort2:
                return size * 2 * 2;
            case TextureImageFormat.Short3:
            case TextureImageFormat.UnsignedShort3:
                return size * 3 * 2;
            case TextureImageFormat.Short4:
            case TextureImageFormat.UnsignedShort4:
                return size * 4 * 2;

            default: return size * 4;
        }
    }

    /// <summary>
    /// Sets the texture coordinate wrapping modes for when a texture is sampled outside the [0, 1] range.
    /// </summary>
    /// <param name="sWrapMode">The wrap mode for the S (or texture-X) coordinate.</param>
    /// <param name="tWrapMode">The wrap mode for the T (or texture-Y) coordinate.</param>
    public void SetWrapModes(TextureWrap sWrapMode, TextureWrap tWrapMode)
    {
        EnsureNotDisposed();
        Graphics.SetWrapS(Handle, sWrapMode);
        Graphics.SetWrapT(Handle, tWrapMode);
    }

    /// <summary>
    /// Enable hardware depth-comparison sampling on this (depth) texture, so a
    /// <c>sampler2DShadow</c> uniform performs the depth test in fixed-function hardware.
    /// Pair with LINEAR filtering for free 2x2 PCF.
    /// </summary>
    public void SetDepthCompareMode(bool enabled) { EnsureNotDisposed(); Graphics.SetTextureCompareMode(Handle, enabled); }

    /// <summary>
    /// Recreates this <see cref="Texture2D"/>'s image with a new size,
    /// resizing the <see cref="Texture2D"/> but losing the image data.
    /// </summary>
    /// <param name="width">The new width for the <see cref="Texture2D"/>.</param>
    /// <param name="height">The new height for the <see cref="Texture2D"/>.</param>
    public unsafe void RecreateImage(uint width, uint height)
    {
        EnsureNotDisposed();
        ValidateTextureSize(width, height);

        Width = width;
        Height = height;

        Graphics.TexImage2D(Handle, 0, Width, Height, 0, (void*)0);
    }

    private void ValidateTextureSize(uint width, uint height)
    {
        if (width <= 0 || width > Graphics.MaxTextureSize)
            throw new ArgumentOutOfRangeException(nameof(width), width, nameof(width) + " must be in the range (0, " + nameof(Graphics.MaxTextureSize) + "]");

        if (height <= 0 || height > Graphics.MaxTextureSize)
            throw new ArgumentOutOfRangeException(nameof(height), height, nameof(height) + " must be in the range (0, " + nameof(Graphics.MaxTextureSize) + "]");
    }

    private void ValidateRectOperation(int rectX, int rectY, uint rectWidth, uint rectHeight)
    {
        if (rectX < 0 || rectX >= Width)
            throw new ArgumentOutOfRangeException(nameof(rectX), rectX, nameof(rectX) + " must be in the range [0, " + nameof(Width) + ")");

        if (rectY < 0 || rectY >= Height)
            throw new ArgumentOutOfRangeException(nameof(rectY), rectY, nameof(rectY) + " must be in the range [0, " + nameof(Height) + ")");

        if (rectWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(rectWidth), rectWidth, nameof(rectWidth) + " must be greater than 0");

        if (rectHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(rectHeight), rectHeight, nameof(rectHeight) + "must be greater than 0");

        if (rectWidth > Width - rectX || rectHeight > Height - rectY)
            throw new ArgumentOutOfRangeException("Specified area is outside of the texture's storage");
    }

    public void Serialize(ref EchoObject compoundTag, SerializationContext ctx)
    {
        compoundTag.Add("Width", new(Width));
        compoundTag.Add("Height", new(Height));
        compoundTag.Add("IsMipMapped", new(IsMipmapped));
        compoundTag.Add("ImageFormat", new((int)ImageFormat));
        compoundTag.Add("MinFilter", new((int)MinFilter));
        compoundTag.Add("MagFilter", new((int)MagFilter));
        compoundTag.Add("Wrap", new((int)WrapMode));
        compoundTag.Add("Srgb", new(Srgb));
        if (Texture.IsCompressedFormat(ImageFormat))
            throw new InvalidOperationException("Compressed textures must be serialized from their source payload.");
        byte[] data;
        if (_importedPixels is { Length: > 0 } imported && imported.Length == GetSize())
        {
            // Prefer the known-good import-time pixels: the GPU readback below can silently
            // return zeros on the async render-thread path, which would poison the cache for
            // every subsequent load. After the first cache write, clear so a later explicit
            // Serialize (user save) reflects any runtime GPU changes.
            data = imported;
            _importedPixels = null;
        }
        else
        {
            data = new byte[GetSize()];
            GetData<byte>(data);
        }
        compoundTag.Add("Data", new(data));
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        Width = value["Width"].UIntValue;
        Height = value["Height"].UIntValue;
        bool isMipMapped = value["IsMipMapped"].BoolValue;
        TextureImageFormat imageFormat = (TextureImageFormat)value["ImageFormat"].IntValue;
        var MinFilter = (TextureMin)value["MinFilter"].IntValue;
        var MagFilter = (TextureMag)value["MagFilter"].IntValue;
        var Wrap = (TextureWrap)value["Wrap"].IntValue;
        // Older caches predate the flag; absence means sRGB (color art is the overwhelmingly
        // common case and matches the previous implicit behavior).
        bool srgb = !value.TryGet("Srgb", out EchoObject? srgbTag) || srgbTag.BoolValue;
        bool compressed = Texture.IsCompressedFormat(imageFormat);
        int compressedMipCount = compressed && value.TryGet("MipCount", out EchoObject? mipCountValue)
            ? mipCountValue!.IntValue
            : 1;
        if (compressed && (compressedMipCount <= 0 || compressedMipCount > 32))
            throw new InvalidDataException($"Compressed texture mip count {compressedMipCount} is outside the supported range.");

        Type[] param = new[] { typeof(uint), typeof(uint), typeof(bool), typeof(TextureImageFormat) };
        object[] values = new object[] { Width, Height, isMipMapped || compressedMipCount > 1, imageFormat };
        typeof(Texture2D).GetConstructor(param).Invoke(this, values);
        Srgb = srgb;

        Memory<byte> memory = value["Data"].ByteArrayValue;
        if (compressed)
        {
            // The constructor has the correct public mipmapped state, then gets the exact chain
            // level count required by the encoded payload before the first upload.
            if (compressedMipCount > 1)
            {
                Handle.MipLevelsHint = checked((uint)compressedMipCount);
                RecreateImage(Width, Height);
            }

            int offset = 0;
            uint mipWidth = Width;
            uint mipHeight = Height;
            for (int mip = 0; mip < compressedMipCount; mip++)
            {
                int expected = Texture.GetDataSize(imageFormat, mipWidth, mipHeight);
                if (memory.Length - offset < expected)
                    throw new InvalidDataException($"Compressed texture mip {mip} expected {expected} bytes, payload has {memory.Length - offset}.");
                Graphics.CompressedTexImage2D(Handle, mip, mipWidth, mipHeight, memory.Span.Slice(offset, expected));
                offset += expected;
                mipWidth = Math.Max(1u, mipWidth >> 1);
                mipHeight = Math.Max(1u, mipHeight >> 1);
            }
            if (offset != memory.Length)
                throw new InvalidDataException($"Compressed texture payload has {memory.Length - offset} trailing bytes after {compressedMipCount} mip levels.");
        }
        else
        {
            SetData(memory);
        }

        if (isMipMapped && !Texture.IsCompressedFormat(imageFormat))
            GenerateMipmaps();

        SetTextureFilters(MinFilter, MagFilter);
        SetWrapModes(Wrap, Wrap);
    }

    #region ImageMagick integration

    /// <summary>
    /// Applies an import-time alpha policy to an RGBA8 pixel buffer. RGB bytes are never changed;
    /// grayscale conversion uses the source 8-bit values directly and does not apply sRGB gamma.
    /// </summary>
    /// <param name="rgbaPixels">Packed RGBA8 pixels.</param>
    /// <param name="source">The alpha policy to apply.</param>
    /// <exception cref="ArgumentException">The buffer does not contain complete RGBA pixels.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The policy is not defined.</exception>
    public static void ApplyAlphaSourceToPixels(Span<byte> rgbaPixels, TextureAlphaSource source)
    {
        if ((rgbaPixels.Length & 3) != 0)
            throw new ArgumentException("RGBA pixel data must contain a whole number of pixels.", nameof(rgbaPixels));

        switch (source)
        {
            case TextureAlphaSource.FromInput:
                return;
            case TextureAlphaSource.None:
                for (int i = 3; i < rgbaPixels.Length; i += 4)
                    rgbaPixels[i] = byte.MaxValue;
                return;
            case TextureAlphaSource.FromGrayScale:
                for (int i = 0; i < rgbaPixels.Length; i += 4)
                    rgbaPixels[i + 3] = (byte)((rgbaPixels[i] + rgbaPixels[i + 1] + rgbaPixels[i + 2]) / 3);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown texture alpha source.");
        }
    }

    private static void ValidateAlphaSource(TextureAlphaSource source)
    {
        if (source != TextureAlphaSource.None
            && source != TextureAlphaSource.FromInput
            && source != TextureAlphaSource.FromGrayScale)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown texture alpha source.");
        }
    }

#if !XENGINE_MOBILE
    /// <summary>
    /// Creates a <see cref="Texture2D"/> from a <see cref="MagickImage"/>.
    /// </summary>
    /// <param name="image">The image to create the <see cref="Texture2D"/> with.</param>
    /// <param name="generateMipmaps">Whether to generate mipmaps for the <see cref="Texture2D"/>.</param>
    public static Texture2D FromImage(MagickImage image, bool generateMipmaps = false, bool srgbSampling = false,
        TextureAlphaSource alphaSource = TextureAlphaSource.FromInput)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateAlphaSource(alphaSource);

        image.Flip();

        image.ColorSpace = ColorSpace.sRGB;
        image.ColorType = ColorType.TrueColorAlpha;

        // Magick.NET-Q8's native quantum is 8-bit, so this is always the actual decoded precision
        // regardless of source depth - a 16-bit PNG/.hdr/.exr source is quantized to 8-bit here (a
        // library limitation, not a choice this code makes).
        //
        // Read straight from Magick's own pixel cache instead of ToByteArray(PixelMapping.RGBA),
        // which would allocate a whole extra managed byte[] just to re-pack data that's already in
        // the right layout. ColorType.TrueColorAlpha above forces the cache to tightly-packed R,G,B,A
        // - exactly the byte layout Color4b/TexSubImage2D expects for Q8's byte-quantum pixels - so
        // the raw pointer can go straight to the GPU with no extra copy.
        Texture2D texture = new(image.Width, image.Height, false,
            srgbSampling ? TextureImageFormat.Color4bSrgb : TextureImageFormat.Color4b);
        try
        {
            nint pixels = image.GetPixelsUnsafe().GetAreaPointer(0, 0, image.Width, image.Height);
            int byteCount = checked((int)(image.Width * image.Height * 4));
            unsafe
            {
                // Keep the CPU-side copy for serialization: the GPU readback in Serialize() can
                // silently return zeros on the async render-thread path (the root cause of
                // all-black cached textures — see Texture2D.Serialize below), so the importer's
                // known-good pixels are preferred for the first cache write.
                byte[] importedPixels = new byte[byteCount];
                texture._importedPixels = importedPixels;
                fixed (byte* dst = importedPixels)
                    System.Buffer.MemoryCopy((void*)pixels, dst, byteCount, byteCount);

                if (alphaSource == TextureAlphaSource.FromInput)
                {
                    // Preserve the existing zero-copy upload for the common path. The CPU copy
                    // above remains the source of truth for the first cache serialization.
                    Graphics.TexSubImage2D(texture.Handle, 0, 0, 0, image.Width, image.Height, (void*)pixels);
                }
                else
                {
                    // Apply the policy before the first GPU upload. In particular, do not upload
                    // source pixels and then read them back just to patch alpha: async readback
                    // can be unavailable and would also add a needless synchronization point.
                    ApplyAlphaSourceToPixels(importedPixels.AsSpan(), alphaSource);
                    texture.SetData<byte>(importedPixels.AsMemory());
                }
            }

            if (generateMipmaps)
                texture.GenerateMipmaps();

            // Image-content textures default to linear filtering (the base Texture default is Nearest,
            // which suits depth/data textures). Importer/user settings still override this afterward.
            texture.SetTextureFilters(generateMipmaps ? TextureMin.LinearMipmapLinear : TextureMin.Linear, TextureMag.Linear);

            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }
#endif

    /// <summary>
    /// Creates a <see cref="Texture2D"/> from a <see cref="Stream"/>.
    /// </summary>
    /// <param name="stream">The stream from which to load an image.</param>
    /// <param name="generateMipmaps">Whether to generate mipmaps for the <see cref="Texture2D"/>.</param>
    public static Texture2D FromStream(Stream stream, bool generateMipmaps = false, bool srgbSampling = false,
        TextureAlphaSource alphaSource = TextureAlphaSource.FromInput)
    {
#if XENGINE_MOBILE
        ArgumentNullException.ThrowIfNull(stream);
        ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        FlipRows(image.Data, image.Width, image.Height);
        ApplyAlphaSourceToPixels(image.Data.AsSpan(), alphaSource);
        var texture = new Texture2D((uint)image.Width, (uint)image.Height, false,
            srgbSampling ? TextureImageFormat.Color4bSrgb : TextureImageFormat.Color4b);
        texture._importedPixels = image.Data;
        texture.SetData<byte>(image.Data.AsMemory());
        if (generateMipmaps)
            texture.GenerateMipmaps();
        texture.SetTextureFilters(
            generateMipmaps ? TextureMin.LinearMipmapLinear : TextureMin.Linear,
            TextureMag.Linear);
        return texture;
#else
        using var image = new MagickImage(stream);
        return FromImage(image, generateMipmaps, srgbSampling, alphaSource);
#endif
    }

    /// <summary>
    /// Creates a <see cref="Texture2D"/> by loading an image from a file.
    /// </summary>
    /// <param name="file">The file containing the image to create the <see cref="Texture2D"/> with.</param>
    /// <param name="generateMipmaps">Whether to generate mipmaps for the <see cref="Texture2D"/>.</param>
    public static Texture2D FromFile(string file, bool generateMipmaps = false, bool srgbSampling = false,
        TextureAlphaSource alphaSource = TextureAlphaSource.FromInput)
    {
#if XENGINE_MOBILE
        using Stream stream = File.OpenRead(file);
        return FromStream(stream, generateMipmaps, srgbSampling, alphaSource);
#else
        using var image = new MagickImage(file);
        return FromImage(image, generateMipmaps, srgbSampling, alphaSource);
#endif
    }

    /// <summary>
    /// Loads a texture from a file path (alias for FromFile for consistency)
    /// </summary>
    public static Texture2D LoadFromFile(string filePath, bool generateMipmaps = false,
        TextureAlphaSource alphaSource = TextureAlphaSource.FromInput)
    {
        Texture2D texture = FromFile(filePath, generateMipmaps, false, alphaSource);
        texture.AssetPath = filePath;
        return texture;
    }

    /// <summary>
    /// Loads a texture from a stream (alias for FromStream for consistency)
    /// </summary>
    public static Texture2D LoadFromStream(Stream stream, bool generateMipmaps = false,
        TextureAlphaSource alphaSource = TextureAlphaSource.FromInput)
    {
        return FromStream(stream, generateMipmaps, false, alphaSource);
    }

    /// <summary>
    /// Get the shared instance of a default embedded texture. Returns the same GPU
    /// texture across the whole app callers that need a unique mutable copy should
    /// call <see cref="FromImage"/>/<see cref="FromStream"/> directly.
    /// </summary>
    public static Texture2D LoadDefault(DefaultTexture texture)
    {
        if (BuiltInAssets.Get(BuiltInAssets.GuidFor(texture)) is Texture2D cached)
            return cached;
        return ParseDefault(texture);
    }

    /// <summary>
    /// Raw load of a default embedded texture invoked by <see cref="BuiltInAssets"/>
    /// on first cache miss. Public callers should use <see cref="LoadDefault"/>.
    /// </summary>
    internal static Texture2D ParseDefault(DefaultTexture texture)
    {
        string fileName = texture switch
        {
            DefaultTexture.White => "default_white.png",
            DefaultTexture.Gray18 => "default_gray18.png",
            DefaultTexture.Normal => "default_normal.png",
            DefaultTexture.Surface => "default_surface.png",
            DefaultTexture.Emission => "default_emission.png",
            DefaultTexture.Grid => "grid.png",
            DefaultTexture.UIPanel => "UI_Panel.png",
            DefaultTexture.Handle => "handle_ui.png",
            DefaultTexture.Noise => "noise.png",
            DefaultTexture.IconCamera => "icon_camera.png",
            DefaultTexture.IconLight => "icon_light.png",
            DefaultTexture.IconAudioSource => "icon_audiosource.png",
            DefaultTexture.IconParticleSystem => "icon_particlesystem.png",
            DefaultTexture.IconDecal => "icon_decal.png",
            _ => throw new ArgumentException($"Unknown default texture: {texture}")
        };

        string resourcePath = $"Assets/Defaults/{fileName}";
        using Stream stream = EmbeddedResources.GetStream(resourcePath);
        return FromStream(stream, true);
        // AssetID/AssetPath/Name are set by BuiltInAssets.Get after this returns.
    }

    internal const string ImageNotContiguousError = "To load/save an image, it's backing memory must be contiguous. Consider using smaller image sizes or changing your ImageSharp memory allocation settings to allow larger buffers.";

    internal const string ImageSizeMustMatchTextureSizeError = "The size of the image must match the size of the texture";

    internal const string TextureFormatMustBeColor4bError = "The texture's format must be Color4b (RGBA)";

#if XENGINE_MOBILE
    private static void FlipRows(byte[] pixels, int width, int height)
    {
        int stride = checked(width * 4);
        var row = new byte[stride];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bottom = (height - 1 - y) * stride;
            Buffer.BlockCopy(pixels, top, row, 0, stride);
            Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
            Buffer.BlockCopy(row, 0, pixels, bottom, stride);
        }
    }
#endif

    #endregion
}
