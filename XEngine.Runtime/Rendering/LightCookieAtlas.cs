// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Guillotine packer for 2D light-cookie tiles. CPU-only so tests can reserve without a GPU atlas.
/// </summary>
public sealed class LightCookiePacker
{
    private struct FreeRect
    {
        public int X, Y, Width, Height;
        public FreeRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    private readonly List<FreeRect> _free = [];
    public int Size { get; private set; }

    public LightCookiePacker(int size) => Reset(size);

    public void Reset(int size)
    {
        Size = Math.Max(16, size);
        _free.Clear();
        _free.Add(new FreeRect(0, 0, Size, Size));
    }

    public bool TryReserve(int width, int height, out Int2 origin)
    {
        origin = default;
        width = Maths.Max(width, 16);
        height = Maths.Max(height, 16);
        if (width > Size || height > Size)
            return false;

        int bestIndex = -1;
        int bestShort = int.MaxValue;
        int bestLong = int.MaxValue;
        for (int i = 0; i < _free.Count; i++)
        {
            FreeRect rect = _free[i];
            if (rect.Width < width || rect.Height < height)
                continue;
            int leftoverX = rect.Width - width;
            int leftoverY = rect.Height - height;
            int shortFit = Maths.Min(leftoverX, leftoverY);
            int longFit = Maths.Max(leftoverX, leftoverY);
            if (shortFit < bestShort || (shortFit == bestShort && longFit < bestLong))
            {
                bestIndex = i;
                bestShort = shortFit;
                bestLong = longFit;
            }
        }

        if (bestIndex < 0)
            return false;

        FreeRect chosen = _free[bestIndex];
        origin = new Int2(chosen.X, chosen.Y);
        _free.RemoveAt(bestIndex);

        int leftoverWidth = chosen.Width - width;
        int leftoverHeight = chosen.Height - height;
        if (leftoverWidth > 0 && leftoverHeight > 0)
        {
            if (leftoverWidth <= leftoverHeight)
            {
                _free.Add(new FreeRect(chosen.X + width, chosen.Y, leftoverWidth, chosen.Height));
                _free.Add(new FreeRect(chosen.X, chosen.Y + height, width, leftoverHeight));
            }
            else
            {
                _free.Add(new FreeRect(chosen.X, chosen.Y + height, chosen.Width, leftoverHeight));
                _free.Add(new FreeRect(chosen.X + width, chosen.Y, leftoverWidth, height));
            }
        }
        else if (leftoverWidth > 0)
        {
            _free.Add(new FreeRect(chosen.X + width, chosen.Y, leftoverWidth, chosen.Height));
        }
        else if (leftoverHeight > 0)
        {
            _free.Add(new FreeRect(chosen.X, chosen.Y + height, chosen.Width, leftoverHeight));
        }

        return true;
    }
}

/// <summary>
/// Color atlas for local (point/spot) light cookies. Allocated only when a local cookie exists.
/// </summary>
public static class LightCookieAtlas
{
    public const int DefaultSize = 1024;

    private static int _size;
    private static RenderTexture? _atlas;
    private static LightCookiePacker? _packer;

    public static int PreferredSize { get; set; } = DefaultSize;
    public static bool IsInitialized => _atlas.IsValid();
    public static int GetSize() => _size;
    public static RenderTexture? GetAtlas() => _atlas;
    public static Texture2D? GetTexture() => _atlas.IsValid() ? _atlas!.MainTexture : null;

    public static void TryInitialize()
    {
        if (_atlas.IsValid())
            return;

        int max = Graphics.MaxTextureSize > 0 ? Graphics.MaxTextureSize : 4096;
        _size = Maths.Clamp(PreferredSize > 0 ? PreferredSize : DefaultSize, 256, max);
        _atlas = new RenderTexture(_size, _size, false, [TextureImageFormat.Color4b]);
        _packer = new LightCookiePacker(_size);
    }

    public static void Clear()
    {
        if (!_atlas.IsValid())
            return;
        _packer ??= new LightCookiePacker(_size);
        _packer.Reset(_size);
    }

    public static Int2? ReserveTiles(int width, int height)
    {
        TryInitialize();
        if (_packer == null)
            return null;
        return _packer.TryReserve(width, height, out Int2 origin) ? origin : null;
    }

    internal static void Dispose()
    {
        _atlas?.Dispose();
        _atlas = null;
        _packer = null;
        _size = 0;
    }
}
