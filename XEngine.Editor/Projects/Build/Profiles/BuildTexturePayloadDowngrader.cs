// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using ImageMagick;

using XEngine.Echo;
using XEngine.Editor.AssetBundles.PlatformVariants;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

namespace XEngine.Editor.Build.Profiles;

/// <summary>
/// Pack-time texture downgrades for a <see cref="BuildProfile"/>'s Asset Import Overrides.
/// Resizes Color4b Echo cache payloads when <see cref="TexturePlatformSettings.MaxTextureSize"/>
/// is set; never writes <c>.meta</c> or triggers a reimport.
/// </summary>
/// <remarks>
/// Compression (ASTC and friends) stays with the platform encoder. This helper only rewrites the
/// uncompressed RGBA cache the importer already produced, which is what Desktop and HarmonyOS
/// ship today and what Android ships when ForceUncompressed skips the encoder.
/// </remarks>
public static class BuildTexturePayloadDowngrader
{
    /// <summary>
    /// Fold the active profile's Asset Import Overrides over a platform baseline. A null baseline
    /// means "no platform limit" so the profile clamp is the only budget.
    /// </summary>
    public static TexturePlatformSettings ResolveSettings(
        BuildSettings settings,
        BuildTarget target,
        TexturePlatformSettings? platformBaseline = null)
    {
        TexturePlatformSettings baseline = platformBaseline ?? new TexturePlatformSettings
        {
            Platform = target,
            Override = true,
            MaxTextureSize = 0,
            Format = TextureVariantFormat.Automatic,
        };

        ProfileAssetImportOverrides? overrides =
            BuildProfileRegistry.GetActive(settings)?.Profile.AssetImportOverrides;
        return overrides?.ApplyTo(baseline) ?? baseline;
    }

    /// <summary>
    /// Payload replacements keyed by asset GUID. Empty when nothing needs rewriting.
    /// </summary>
    public static Dictionary<Guid, byte[]> CreateOverrides(
        Project project,
        IReadOnlyCollection<Guid> assets,
        TexturePlatformSettings settings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(settings);

        var overrides = new Dictionary<Guid, byte[]>();
        if (settings.MaxTextureSize <= 0)
            return overrides;

        EditorAssetBackend? database = EditorAssetBackend.Instance;
        if (database == null)
            return overrides;

        foreach (Guid guid in assets)
        {
            AssetEntry? asset = database.GetEntry(guid);
            if (asset == null || !IsTexture(asset))
                continue;

            string cachePath = Path.Combine(project.CachePath, guid + ".asset");
            if (!File.Exists(cachePath))
                continue;

            byte[] source = File.ReadAllBytes(cachePath);
            if (!TryRewrite(source, settings, out byte[] rewritten))
                continue;

            overrides[guid] = rewritten;
        }

        return overrides;
    }

    /// <summary>Copy extras into <paramref name="target"/> without replacing encoded payloads.</summary>
    public static void MergeMissing(Dictionary<Guid, byte[]> target, Dictionary<Guid, byte[]> extras)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(extras);

        foreach (KeyValuePair<Guid, byte[]> pair in extras)
        {
            if (!target.ContainsKey(pair.Key))
                target.Add(pair.Key, pair.Value);
        }
    }

    /// <summary>
    /// Rewrite a Color4b Echo texture payload so its longest edge is at most
    /// <see cref="TexturePlatformSettings.MaxTextureSize"/>. Returns false when the payload is
    /// already small enough, is not RGBA, or cannot be parsed.
    /// </summary>
    internal static bool TryRewrite(byte[] source, TexturePlatformSettings settings, out byte[] rewritten)
    {
        rewritten = source;
        if (source == null || source.Length == 0 || settings == null || settings.MaxTextureSize <= 0)
            return false;

        EchoObject echo;
        try
        {
            using var stream = new MemoryStream(source, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            echo = EchoObject.ReadFromBinary(reader);
        }
        catch
        {
            return false;
        }

        if (!echo.TryGet("Width", out EchoObject? widthTag) ||
            !echo.TryGet("Height", out EchoObject? heightTag) ||
            !echo.TryGet("ImageFormat", out EchoObject? formatTag) ||
            !echo.TryGet("Data", out EchoObject? dataTag))
        {
            return false;
        }

        var format = (TextureImageFormat)formatTag!.IntValue;
        if (format != TextureImageFormat.Color4b)
            return false;

        int width = checked((int)widthTag!.UIntValue);
        int height = checked((int)heightTag!.UIntValue);
        if (width <= 0 || height <= 0)
            return false;

        byte[] rgba = dataTag!.ByteArrayValue;
        int expected = checked(width * height * 4);
        if (rgba == null || rgba.Length < expected)
            return false;

        int longest = Math.Max(width, height);
        if (longest <= settings.MaxTextureSize)
            return false;

        double scale = settings.MaxTextureSize / (double)longest;
        int newWidth = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.ToEven));
        int newHeight = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.ToEven));
        if (newWidth == width && newHeight == height)
            return false;

        byte[] mip0 = rgba;
        if (rgba.Length != expected)
        {
            mip0 = new byte[expected];
            Buffer.BlockCopy(rgba, 0, mip0, 0, expected);
        }

        byte[] resized = ResizeRgba(mip0, width, height, newWidth, newHeight, settings.ResizeAlgorithm);

        EchoObject clone = echo.Clone();
        clone["Width"] = new EchoObject((uint)newWidth);
        clone["Height"] = new EchoObject((uint)newHeight);
        clone["Data"] = new EchoObject(resized);

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            clone.WriteToBinary(writer);
        rewritten = output.ToArray();
        return true;
    }

    private static byte[] ResizeRgba(
        byte[] rgba,
        int width,
        int height,
        int newWidth,
        int newHeight,
        TextureResizeAlgorithm algorithm)
    {
        var read = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA);
        using var image = new MagickImage();
        image.ReadPixels(rgba, read);
        image.ColorType = ColorType.TrueColorAlpha;
        image.FilterType = algorithm switch
        {
            TextureResizeAlgorithm.Nearest => FilterType.Point,
            TextureResizeAlgorithm.Bilinear => FilterType.Triangle,
            _ => FilterType.Mitchell,
        };
        image.Resize((uint)newWidth, (uint)newHeight);

        byte[]? pixels = image.GetPixels().ToByteArray(PixelMapping.RGBA);
        if (pixels == null || pixels.Length != newWidth * newHeight * 4)
            throw new InvalidDataException("Texture resize did not produce an RGBA payload of the expected size.");
        return pixels;
    }

    private static bool IsTexture(AssetEntry asset)
        => string.Equals(asset.ImporterType, "TextureImporter", StringComparison.OrdinalIgnoreCase) ||
            (asset.MainAssetTypeName?.Contains("Texture2D", StringComparison.OrdinalIgnoreCase) ?? false);
}
