// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.OpenGL;

namespace XEngine.Runtime.Backends.OpenGL;

/// <summary>OpenGL texture format mapping and mutation kept behind the backend boundary.</summary>
internal static unsafe class OpenGLTextureOperations
{
    public static void Create(GL gl, GraphicsTexture texture) => texture.Handle = gl.GenTexture();

    public static void Bind(GL gl, GraphicsTexture texture) =>
        gl.BindTexture(GetTarget(texture.Type), texture.Handle);

    public static void BindToUnit(GL gl, int unit, GraphicsTexture texture)
    {
        gl.ActiveTexture((TextureUnit)((uint)TextureUnit.Texture0 + unit));
        Bind(gl, texture);
    }

    public static void GenerateMipmap(GL gl, GraphicsTexture texture)
    {
        Bind(gl, texture);
        gl.GenerateMipmap(GetTarget(texture.Type));
    }

    public static void SetWrap(GL gl, GraphicsTexture texture, byte axis, TextureWrap wrap)
    {
        GLEnum parameter = axis switch
        {
            0 => GLEnum.TextureWrapS,
            1 => GLEnum.TextureWrapT,
            2 => GLEnum.TextureWrapR,
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, null),
        };
        GLEnum value = wrap switch
        {
            TextureWrap.Repeat => GLEnum.Repeat,
            TextureWrap.ClampToEdge => GLEnum.ClampToEdge,
            TextureWrap.MirroredRepeat => GLEnum.MirroredRepeat,
            TextureWrap.ClampToBorder => GLEnum.ClampToBorder,
            _ => throw new ArgumentOutOfRangeException(nameof(wrap), wrap, null),
        };

        Bind(gl, texture);
        gl.TexParameter(GetTarget(texture.Type), parameter, (int)value);
    }

    public static void SetFilters(GL gl, GraphicsTexture texture, TextureMin min, TextureMag mag)
    {
        GLEnum minFilter = min switch
        {
            TextureMin.Nearest => GLEnum.Nearest,
            TextureMin.Linear => GLEnum.Linear,
            TextureMin.NearestMipmapNearest => GLEnum.NearestMipmapNearest,
            TextureMin.LinearMipmapNearest => GLEnum.LinearMipmapNearest,
            TextureMin.NearestMipmapLinear => GLEnum.NearestMipmapLinear,
            TextureMin.LinearMipmapLinear => GLEnum.LinearMipmapLinear,
            _ => throw new ArgumentOutOfRangeException(nameof(min), min, null),
        };
        GLEnum magFilter = mag switch
        {
            TextureMag.Nearest => GLEnum.Nearest,
            TextureMag.Linear => GLEnum.Linear,
            _ => throw new ArgumentOutOfRangeException(nameof(mag), mag, null),
        };

        TextureTarget target = GetTarget(texture.Type);
        Bind(gl, texture);
        gl.TexParameter(target, GLEnum.TextureMinFilter, (int)minFilter);
        gl.TexParameter(target, GLEnum.TextureMagFilter, (int)magFilter);
    }

    public static void SetCompareMode(GL gl, GraphicsTexture texture, bool enabled)
    {
        TextureTarget target = GetTarget(texture.Type);
        Bind(gl, texture);
        if (enabled)
        {
            gl.TexParameter(target, GLEnum.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
            gl.TexParameter(target, GLEnum.TextureCompareFunc, (int)GLEnum.Lequal);
        }
        else
        {
            gl.TexParameter(target, GLEnum.TextureCompareMode, (int)GLEnum.None);
        }
    }

    public static void Allocate2D(
        GL gl,
        GraphicsTexture texture,
        int face,
        int mip,
        uint width,
        uint height,
        int border,
        ReadOnlySpan<byte> data)
    {
        GetFormat(texture.ImageFormat, out InternalFormat internalFormat, out PixelType pixelType, out PixelFormat pixelFormat);
        TextureTarget target = face >= 0
            ? TextureTarget.TextureCubeMapPositiveX + face
            : GetTarget(texture.Type);
        Bind(gl, texture);
        fixed (byte* dataPtr = data)
        {
            gl.TexImage2D(
                target,
                mip,
                internalFormat,
                width,
                height,
                border,
                pixelFormat,
                pixelType,
                data.Length == 0 ? null : dataPtr);
        }
    }

    public static void Allocate3D(
        GL gl,
        GraphicsTexture texture,
        int mip,
        uint width,
        uint height,
        uint depth,
        ReadOnlySpan<byte> data)
    {
        GetFormat(texture.ImageFormat, out InternalFormat internalFormat, out PixelType pixelType, out PixelFormat pixelFormat);
        TextureTarget target = GetTarget(texture.Type);
        Bind(gl, texture);
        fixed (byte* dataPtr = data)
        {
            gl.TexImage3D(
                target,
                mip,
                internalFormat,
                width,
                height,
                depth,
                0,
                pixelFormat,
                pixelType,
                data.Length == 0 ? null : dataPtr);
        }
    }

    public static void Update2D(
        GL gl,
        GraphicsTexture texture,
        int mip,
        int x,
        int y,
        uint width,
        uint height,
        ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        GetFormat(texture.ImageFormat, out _, out PixelType pixelType, out PixelFormat pixelFormat);
        TextureTarget target = GetTarget(texture.Type);
        Bind(gl, texture);
        fixed (byte* dataPtr = data)
            gl.TexSubImage2D(target, mip, x, y, width, height, pixelFormat, pixelType, dataPtr);
    }

    public static void Update3D(
        GL gl,
        GraphicsTexture texture,
        int mip,
        int x,
        int y,
        int z,
        uint width,
        uint height,
        uint depth,
        ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        GetFormat(texture.ImageFormat, out _, out PixelType pixelType, out PixelFormat pixelFormat);
        TextureTarget target = GetTarget(texture.Type);
        Bind(gl, texture);
        fixed (byte* dataPtr = data)
            gl.TexSubImage3D(target, mip, x, y, z, width, height, depth, pixelFormat, pixelType, dataPtr);
    }

    public static void Read(GL gl, GraphicsTexture texture, int face, int mip, void* destination)
    {
        GetFormat(texture.ImageFormat, out _, out PixelType pixelType, out PixelFormat pixelFormat);
        TextureTarget target = face >= 0
            ? TextureTarget.TextureCubeMapPositiveX + face
            : GetTarget(texture.Type);
        Bind(gl, texture);
        gl.GetTexImage(target, mip, pixelFormat, pixelType, destination);
    }

    internal static TextureTarget GetTarget(TextureType type) => type switch
    {
        TextureType.Texture2D => TextureTarget.Texture2D,
        TextureType.Texture3D => TextureTarget.Texture3D,
        TextureType.TextureCubeMap => TextureTarget.TextureCubeMap,
        TextureType.Texture2DArray => TextureTarget.Texture2DArray,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static void GetFormat(
        TextureImageFormat imageFormat,
        out InternalFormat internalFormat,
        out PixelType pixelType,
        out PixelFormat pixelFormat)
    {
        pixelType = imageFormat switch
        {
            TextureImageFormat.Color4b or TextureImageFormat.Color4bSrgb or TextureImageFormat.Byte => PixelType.UnsignedByte,
            TextureImageFormat.Float or TextureImageFormat.Float2 or TextureImageFormat.Float3 or TextureImageFormat.Float4 => PixelType.Float,
            TextureImageFormat.Short or TextureImageFormat.Short2 or TextureImageFormat.Short3 or TextureImageFormat.Short4 => PixelType.Short,
            TextureImageFormat.Int or TextureImageFormat.Int2 or TextureImageFormat.Int3 or TextureImageFormat.Int4 => PixelType.Int,
            TextureImageFormat.UnsignedShort or TextureImageFormat.UnsignedShort2 or TextureImageFormat.UnsignedShort3 or TextureImageFormat.UnsignedShort4 => PixelType.UnsignedShort,
            TextureImageFormat.UnsignedInt or TextureImageFormat.UnsignedInt2 or TextureImageFormat.UnsignedInt3 or TextureImageFormat.UnsignedInt4 => PixelType.UnsignedInt,
            TextureImageFormat.Depth16f or TextureImageFormat.Depth24f or TextureImageFormat.Depth32f => PixelType.Float,
            TextureImageFormat.Depth24Stencil8 => (PixelType)GLEnum.UnsignedInt248,
            _ => throw new ArgumentOutOfRangeException(nameof(imageFormat), imageFormat, null),
        };

        internalFormat = imageFormat switch
        {
            TextureImageFormat.Color4b => InternalFormat.Rgba8,
            TextureImageFormat.Color4bSrgb => InternalFormat.Srgb8Alpha8,
            TextureImageFormat.Byte => InternalFormat.R8ui,
            TextureImageFormat.Float => InternalFormat.R32f,
            TextureImageFormat.Float2 => InternalFormat.RG32f,
            TextureImageFormat.Float3 => InternalFormat.Rgb32f,
            TextureImageFormat.Float4 => InternalFormat.Rgba32f,
            TextureImageFormat.Short => InternalFormat.R16f,
            TextureImageFormat.Short2 => InternalFormat.RG16f,
            TextureImageFormat.Short3 => InternalFormat.Rgb16f,
            TextureImageFormat.Short4 => InternalFormat.Rgba16f,
            TextureImageFormat.Int => InternalFormat.R32i,
            TextureImageFormat.Int2 => InternalFormat.RG32i,
            TextureImageFormat.Int3 => InternalFormat.Rgb32i,
            TextureImageFormat.Int4 => InternalFormat.Rgba32i,
            TextureImageFormat.UnsignedShort => InternalFormat.R16f,
            TextureImageFormat.UnsignedShort2 => InternalFormat.RG16f,
            TextureImageFormat.UnsignedShort3 => InternalFormat.Rgb16f,
            TextureImageFormat.UnsignedShort4 => InternalFormat.Rgba16f,
            TextureImageFormat.UnsignedInt => InternalFormat.R32ui,
            TextureImageFormat.UnsignedInt2 => InternalFormat.RG32ui,
            TextureImageFormat.UnsignedInt3 => InternalFormat.Rgb32ui,
            TextureImageFormat.UnsignedInt4 => InternalFormat.Rgba32ui,
            TextureImageFormat.Depth16f => InternalFormat.DepthComponent16,
            TextureImageFormat.Depth24f => InternalFormat.DepthComponent24,
            TextureImageFormat.Depth32f => InternalFormat.DepthComponent32f,
            TextureImageFormat.Depth24Stencil8 => InternalFormat.Depth24Stencil8,
            _ => throw new ArgumentOutOfRangeException(nameof(imageFormat), imageFormat, null),
        };

        pixelFormat = imageFormat switch
        {
            TextureImageFormat.Color4b or TextureImageFormat.Color4bSrgb => PixelFormat.Rgba,
            TextureImageFormat.Byte => PixelFormat.RedInteger,
            TextureImageFormat.Short or TextureImageFormat.Float or TextureImageFormat.UnsignedShort => PixelFormat.Red,
            TextureImageFormat.Short2 or TextureImageFormat.Float2 or TextureImageFormat.UnsignedShort2 => PixelFormat.RG,
            TextureImageFormat.Short3 or TextureImageFormat.Float3 or TextureImageFormat.UnsignedShort3 => PixelFormat.Rgb,
            TextureImageFormat.Short4 or TextureImageFormat.Float4 or TextureImageFormat.UnsignedShort4 => PixelFormat.Rgba,
            TextureImageFormat.Int => PixelFormat.RgbaInteger,
            TextureImageFormat.Int2 or TextureImageFormat.UnsignedInt2 => PixelFormat.RGInteger,
            TextureImageFormat.Int3 or TextureImageFormat.UnsignedInt3 => PixelFormat.RgbInteger,
            TextureImageFormat.Int4 or TextureImageFormat.UnsignedInt4 => PixelFormat.RgbaInteger,
            TextureImageFormat.UnsignedInt => PixelFormat.RedInteger,
            TextureImageFormat.Depth16f or TextureImageFormat.Depth24f or TextureImageFormat.Depth32f => PixelFormat.DepthComponent,
            TextureImageFormat.Depth24Stencil8 => PixelFormat.DepthStencil,
            _ => throw new ArgumentOutOfRangeException(nameof(imageFormat), imageFormat, null),
        };
    }
}
