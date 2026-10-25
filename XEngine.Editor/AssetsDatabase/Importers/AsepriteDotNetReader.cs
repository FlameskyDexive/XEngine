// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

using AsepriteDotNet.IO;

using AsepriteDotNet.Aseprite.Types;
using AsepriteDotNet.Common;

using LibraryFile = AsepriteDotNet.Aseprite.AsepriteFile;
using LibrarySlice = AsepriteDotNet.Aseprite.Types.AsepriteSlice;
using LibraryTag = AsepriteDotNet.Aseprite.Types.AsepriteTag;

namespace XEngine.Editor.Importers;

/// <summary>Preferred Aseprite parse path via AsepriteDotNet 1.9.1. Falls back to the header/chunk reader.</summary>
internal static class AsepriteDotNetReader
{
    public static bool TryRead(byte[] bytes, [NotNullWhen(true)] out AsepriteDocument? document)
    {
        document = null;
        if (bytes.Length <= 128)
            return false;

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            LibraryFile file = AsepriteFileLoader.FromStream("import.aseprite", stream);
            document = FromLibrary(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AsepriteDocument FromLibrary(LibraryFile file)
    {
        var tags = new AsepriteTag[file.Tags.Length];
        for (int i = 0; i < file.Tags.Length; i++)
        {
            LibraryTag tag = file.Tags[i];
            tags[i] = new AsepriteTag(tag.Name, tag.From, tag.To);
        }

        var slices = new List<AsepriteSlice>(file.Slices.Length);
        for (int i = 0; i < file.Slices.Length; i++)
        {
            LibrarySlice slice = file.Slices[i];
            if (slice.Keys.Length == 0)
            {
                slices.Add(new AsepriteSlice(slice.Name, 0, 0, 0, 0));
                continue;
            }

            var key = slice.Keys[0];
            slices.Add(new AsepriteSlice(slice.Name, key.Bounds.X, key.Bounds.Y, key.Bounds.Width, key.Bounds.Height));
        }

        var frames = new AsepriteFramePixels[file.FrameCount];
        for (int i = 0; i < file.FrameCount; i++)
        {
            AsepriteFrame frame = file.GetFrame(i);
            Rgba32[] rgba = AsepriteFrameExtensions.FlattenFrame(frame,
                onlyVisibleLayers: true, includeBackgroundLayer: true, includeTilemapCels: true);
            frames[i] = new AsepriteFramePixels
            {
                Index = i,
                Width = file.CanvasWidth,
                Height = file.CanvasHeight,
                DurationMs = (int)Math.Round(frame.Duration.TotalMilliseconds),
                Rgba = ToRgbaBytes(rgba),
            };
        }

        return new AsepriteDocument(file.CanvasWidth, file.CanvasHeight, file.FrameCount, tags, slices, frames);
    }

    private static byte[] ToRgbaBytes(Rgba32[] pixels)
    {
        var bytes = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            int o = i * 4;
            bytes[o] = p.R;
            bytes[o + 1] = p.G;
            bytes[o + 2] = p.B;
            bytes[o + 3] = p.A;
        }

        return bytes;
    }
}
