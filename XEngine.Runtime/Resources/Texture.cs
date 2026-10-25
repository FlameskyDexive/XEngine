// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Resources;

/// <summary>
/// This is the base class for all texture types and manages some of their internal workings.
/// </summary>
public abstract class Texture : EngineObject
{

    private protected const TextureMin DefaultMinFilter = TextureMin.Nearest, DefaultMipmapMinFilter = TextureMin.NearestMipmapLinear;
    private protected const TextureMag DefaultMagFilter = TextureMag.Nearest;

    private readonly GraphicsTexture _handle;
    /// <summary>The handle for the GL Texture Object.</summary>
    public GraphicsTexture Handle { get { EnsureNotDisposed(); return _handle; } }

    private readonly TextureType _type;
    /// <summary>The type of this <see cref="Texture"/>, such as 1D, 2D, Multisampled 2D, Array 2D, CubeMap, etc.</summary>
    public TextureType Type { get { EnsureNotDisposed(); return _type; } }

    private TextureMin _minFilter;
    private TextureMag _magFilter;
    private TextureWrap _wrapMode;
    public TextureMin MinFilter { get { EnsureNotDisposed(); return _minFilter; } protected set => _minFilter = value; }
    public TextureMag MagFilter { get { EnsureNotDisposed(); return _magFilter; } protected set => _magFilter = value; }
    public TextureWrap WrapMode { get { EnsureNotDisposed(); return _wrapMode; } protected set => _wrapMode = value; }

    private readonly TextureImageFormat _imageFormat;
    /// <summary>The format for this <see cref="Texture"/>'s image.</summary>
    public TextureImageFormat ImageFormat { get { EnsureNotDisposed(); return _imageFormat; } }

    private bool _isMipmapped;
    /// <summary>Gets whether this <see cref="Texture"/> is mipmapped.</summary>
    public bool IsMipmapped { get { EnsureNotDisposed(); return _isMipmapped; } private set => _isMipmapped = value; }

    /// <summary>False if this <see cref="Texture"/> can be mipmapped (depends on texture type).</summary>
    private readonly bool isNotMipmappable;

    /// <summary>Gets whether this <see cref="Texture"/> can be mipmapped (depends on texture type).</summary>
    public bool IsMipmappable { get { EnsureNotDisposed(); return !isNotMipmappable; } }

    /// <summary>
    /// Creates a <see cref="Texture"/> with specified <see cref="TextureType"/> and <see cref="TextureImageFormat"/>.
    /// </summary>
    /// <param name="type">The type of texture (or texture target) the texture will be.</param>
    /// <param name="imageFormat">The type of image format this texture will store.</param>
    internal Texture(TextureType type, TextureImageFormat imageFormat)
        : this(type, imageFormat, 1)
    {
    }

    /// <param name="sampleCount">
    /// Multisample count (1 = single-sample). Above 1 the texture is a render-target-only
    /// attachment; wrap/filter state is skipped because it is resolved rather than sampled.
    /// </param>
    internal Texture(TextureType type, TextureImageFormat imageFormat, uint sampleCount) : base("New Texture")
    {
        if (!Enum.IsDefined(typeof(TextureType), type))
            throw new FormatException("Invalid texture target");

        if (!Enum.IsDefined(typeof(TextureImageFormat), imageFormat))
            throw new FormatException("Invalid texture image format");

        _type = type;
        _imageFormat = imageFormat;
        _isMipmapped = false;
        isNotMipmappable = !IsTextureTypeMipmappable(type);
        _handle = Graphics.CreateTexture(type, imageFormat, sampleCount);
        if (sampleCount == 1)
        {
            Graphics.SetWrapS(_handle, TextureWrap.Repeat);
            Graphics.SetWrapT(_handle, TextureWrap.Repeat);
            Graphics.SetTextureFilters(_handle, DefaultMinFilter, DefaultMagFilter);
        }
        _minFilter = DefaultMinFilter;
        _magFilter = DefaultMagFilter;
        _wrapMode = TextureWrap.Repeat;
    }

    /// <summary>
    /// Sets this <see cref="Texture"/>'s minifying and magnifying filters.
    /// </summary>
    /// <param name="minFilter">The desired minifying filter for the <see cref="Texture"/>.</param>
    /// <param name="magFilter">The desired magnifying filter for the <see cref="Texture"/>.</param>
    public void SetTextureFilters(TextureMin minFilter, TextureMag magFilter)
    {
        EnsureNotDisposed();
        Graphics.SetTextureFilters(_handle, minFilter, magFilter);
        _minFilter = minFilter;
        _magFilter = magFilter;
    }

    /// <summary>
    /// Generates mipmaps for this <see cref="Texture"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"/>
    public void GenerateMipmaps()
    {
        EnsureNotDisposed();

        if (isNotMipmappable)
            throw new InvalidOperationException(string.Concat("This texture type is not mipmappable! Type: ", _type.ToString()));

        Graphics.GenerateMipmap(_handle);
        _isMipmapped = true;
        Graphics.SetTextureFilters(_handle, _isMipmapped ? DefaultMipmapMinFilter : DefaultMinFilter, DefaultMagFilter);
    }

    public override void OnDispose()
    {
        _handle.Dispose();
    }

    // Safety net: once nothing references this Texture, the idle-timeout sweep in
    // EditorAssetBackend/PlayerAssetBackend no longer keeps it alive either, so something must
    // still free the GPU handle. Handle.Dispose() only enqueues a thread-safe render-thread command
    // (see GraphicsTexture.Dispose), so calling it from the finalizer thread is safe.
    //
    // Finalizers must never throw: an unhandled exception here tears down GC.RunFinalizers and
    // terminates the whole process (observed in the wild as "Object reference not set..." at
    // Texture.OnDispose when the graphics device was already gone). Swallow any failure — a GPU
    // handle leaking once at shutdown is harmless compared to killing the editor.
    ~Texture()
    {
        try { Dispose(); }
        catch { /* GPU handle may leak once at shutdown; never crash the process from a finalizer. */ }
    }

    /// <summary>
    /// Gets whether the specified <see cref="TextureType"/> type is mipmappable.
    /// </summary>
    public static bool IsTextureTypeMipmappable(TextureType textureType)
    {
        return textureType == TextureType.Texture2D || textureType == TextureType.Texture3D
            || textureType == TextureType.TextureCubeMap || textureType == TextureType.Texture2DArray;
    }

    /// <summary>Bytes per texel for a given image format.</summary>
    public static int GetBytesPerPixel(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b or TextureImageFormat.Color4bSrgb => 4,
        TextureImageFormat.Byte => 1,

        TextureImageFormat.Short or TextureImageFormat.UnsignedShort => 2,
        TextureImageFormat.Short2 or TextureImageFormat.UnsignedShort2 => 4,
        TextureImageFormat.Short3 or TextureImageFormat.UnsignedShort3 => 6,
        TextureImageFormat.Short4 or TextureImageFormat.UnsignedShort4 => 8,

        TextureImageFormat.Float or TextureImageFormat.Int or TextureImageFormat.UnsignedInt => 4,
        TextureImageFormat.Float2 or TextureImageFormat.Int2 or TextureImageFormat.UnsignedInt2 => 8,
        TextureImageFormat.Float3 or TextureImageFormat.Int3 or TextureImageFormat.UnsignedInt3 => 12,
        TextureImageFormat.Float4 or TextureImageFormat.Int4 or TextureImageFormat.UnsignedInt4 => 16,

        TextureImageFormat.Depth16f => 2,
        TextureImageFormat.Depth24f or TextureImageFormat.Depth24Stencil8 => 4,
        TextureImageFormat.Depth32f => 4,

        _ => 4,
    };

    /// <summary>Returns true for Vulkan block-compressed texture formats.</summary>
    public static bool IsCompressedFormat(TextureImageFormat format) => format is
        TextureImageFormat.Etc2Rgb8 or TextureImageFormat.Etc2Rgba8 or TextureImageFormat.Astc4x4 or
        TextureImageFormat.Etc2Rgb8Srgb or TextureImageFormat.Etc2Rgba8Srgb or TextureImageFormat.Astc4x4Srgb;

    /// <summary>
    /// Returns the exact tight payload size for one mip level. Compressed textures use block
    /// dimensions rather than a bytes-per-texel approximation, which is required for edge mips.
    /// </summary>
    public static int GetDataSize(TextureImageFormat format, uint width, uint height)
    {
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(width == 0 ? nameof(width) : nameof(height));
        if (!IsCompressedFormat(format))
            return checked((int)(width * height * (uint)GetBytesPerPixel(format)));

        uint blockWidth = 4;
        uint blockHeight = 4;
        uint bytesPerBlock = format is TextureImageFormat.Etc2Rgb8 or TextureImageFormat.Etc2Rgb8Srgb ? 8u : 16u;
        uint blocksWide = (width + blockWidth - 1) / blockWidth;
        uint blocksHigh = (height + blockHeight - 1) / blockHeight;
        return checked((int)(blocksWide * blocksHigh * bytesPerBlock));
    }
}
