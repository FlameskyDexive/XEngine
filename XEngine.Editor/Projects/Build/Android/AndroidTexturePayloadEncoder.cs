// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using ImageMagick;

using XEngine.Echo;
using XEngine.Editor.AssetBundles.PlatformVariants;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Build.Android;

/// <summary>Immutable identity of the external encoder used for an Android build.</summary>
internal sealed record AndroidTextureEncoderDescriptor(
    string Id,
    string Version,
    string ExecutablePath,
    string Sha256,
    bool SupportsAstc,
    bool SupportsEtc2);

/// <summary>One texture payload after platform selection and optional offline encoding.</summary>
internal sealed record AndroidTextureVariantBuildEntry(
    Guid Guid,
    string AssetPath,
    byte[] SourcePayload,
    byte[] PackagedPayload,
    long SourcePayloadBytes,
    string SourcePayloadSha256,
    string PackagedPayloadSha256,
    TextureVariantFormat RequestedFormat,
    TextureVariantFormat EffectiveFormat,
    bool IsFallback,
    string SelectionReason,
    int MaxTextureSize,
    int CompressionQuality,
    bool GenerateMipmaps,
    bool SRgb,
    bool NormalMap,
    int Width,
    int Height,
    int MipCount,
    bool EncodedPayload,
    string PayloadFormat,
    string PayloadPath);

internal sealed class AndroidTextureVariantBuildPlan
{
    internal AndroidTextureEncoderDescriptor? AstcEncoder { get; init; }
    internal AndroidTextureEncoderDescriptor? Etc2Encoder { get; init; }
    internal List<AndroidTextureVariantBuildEntry> Entries { get; } = new();
    internal Dictionary<Guid, byte[]> PayloadOverrides { get; } = new();
}

/// <summary>
/// Resolves and encodes Android texture payloads before the asset packaging mode writes its
/// container. ASTC uses ARM's astcenc and ETC2/EAC uses the pinned google/etc2comp source build,
/// so a build can never claim a compressed format without a matching payload.
/// </summary>
internal static class AndroidTexturePayloadEncoder
{
    private const string AstcEncoderRoot = @"F:\AndroidPlayer\Tools\texture-encoders\astcenc-5.7.0\bin";
    private const string AstcEncoderVersion = "5.7.0";
    private const string Etc2EncoderPath = @"F:\AndroidPlayer\Tools\texture-encoders\etc2comp-2022\bin\EtcTool.exe";
    private const string Etc2EncoderVersion = "2022-06-01-39422c1";

    internal static AndroidTextureVariantBuildPlan CreatePlan(
        Project project,
        IReadOnlyCollection<Guid> assets,
        TexturePlatformSettings settings,
        AndroidTextureCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(settings);

        var plan = new AndroidTextureVariantBuildPlan
        {
            AstcEncoder = DiscoverEncoder(),
            Etc2Encoder = DiscoverEtc2Encoder(),
        };
        EditorAssetBackend? database = EditorAssetBackend.Instance;
        if (database == null)
            return plan;

        var sorted = new List<Guid>(assets);
        sorted.Sort(CompareGuid);
        for (int i = 0; i < sorted.Count; i++)
        {
            Guid guid = sorted[i];
            AssetEntry? asset = database.GetEntry(guid);
            if (asset == null || !IsTexture(asset))
                continue;

            string cachePath = Path.Combine(project.CachePath, guid + ".asset");
            if (!File.Exists(cachePath))
                continue;
            byte[] source = File.ReadAllBytes(cachePath);
            ImportMetadata import = ReadImportMetadata(project, asset);
            AndroidTextureVariantSelection selected = ResolveSelection(
                settings, capabilities, plan.AstcEncoder, plan.Etc2Encoder);
            byte[] packaged = source;
            bool encoded = false;
            int width = 0;
            int height = 0;
            int mipCount = 1;
            string reason = selected.Reason;

            if (selected.EffectiveFormat == TextureVariantFormat.ASTC && plan.AstcEncoder != null)
            {
                try
                {
                    EncodedTexture result = EncodeAstc(
                        source,
                        guid,
                        import,
                        selected.MaxTextureSize,
                        selected.CompressionQuality,
                        plan.AstcEncoder);
                    packaged = result.Payload;
                    encoded = true;
                    width = result.Width;
                    height = result.Height;
                    mipCount = result.MipCount;
                    reason += ":encoded:astcenc";
                }
                catch (Exception exception)
                {
                    selected = new AndroidTextureVariantSelection(
                        selected.RequestedFormat,
                        TextureVariantFormat.RGBA32,
                        selected.MaxTextureSize,
                        selected.CompressionQuality,
                        true,
                        reason + ":encoder-failed:fallback:rgba32:" + SanitizeReason(exception.Message));
                    reason = selected.Reason;
                }
            }
            else if (selected.EffectiveFormat is TextureVariantFormat.ETC2_RGB or TextureVariantFormat.ETC2_RGBA &&
                plan.Etc2Encoder != null)
            {
                try
                {
                    EncodedTexture result = EncodeEtc2(
                        source,
                        guid,
                        import,
                        selected.EffectiveFormat,
                        selected.MaxTextureSize,
                        selected.CompressionQuality,
                        plan.Etc2Encoder);
                    packaged = result.Payload;
                    encoded = true;
                    width = result.Width;
                    height = result.Height;
                    mipCount = result.MipCount;
                    reason += ":encoded:etc2comp";
                }
                catch (Exception exception)
                {
                    selected = new AndroidTextureVariantSelection(
                        selected.RequestedFormat,
                        TextureVariantFormat.RGBA32,
                        selected.MaxTextureSize,
                        selected.CompressionQuality,
                        true,
                        reason + ":encoder-failed:fallback:rgba32:" + SanitizeReason(exception.Message));
                    reason = selected.Reason;
                }
            }

            plan.Entries.Add(new AndroidTextureVariantBuildEntry(
                guid,
                asset.Path.Replace('\\', '/'),
                source,
                packaged,
                source.LongLength,
                Convert.ToHexString(SHA256.HashData(source)),
                Convert.ToHexString(SHA256.HashData(packaged)),
                selected.RequestedFormat,
                selected.EffectiveFormat,
                selected.IsFallback,
                reason,
                selected.MaxTextureSize,
                selected.CompressionQuality,
                import.GenerateMipmaps,
                import.SRgb,
                import.NormalMap,
                width,
                height,
                mipCount,
                encoded,
                GetPayloadFormat(selected.EffectiveFormat, encoded, import.SRgb && !import.NormalMap),
                $"{guid}.asset"));

            if (encoded)
                plan.PayloadOverrides[guid] = packaged;
        }

        plan.Entries.Sort(static (left, right) => CompareGuid(left.Guid, right.Guid));
        return plan;
    }

    internal static AndroidTextureVariantSelection ResolveSelection(
        TexturePlatformSettings settings,
        AndroidTextureCapabilities capabilities,
        AndroidTextureEncoderDescriptor? astcEncoder,
        AndroidTextureEncoderDescriptor? etc2Encoder)
    {
        // An explicit ASTC profile is a packaging decision. The device capability probe is still
        // recorded in the manifest, but it must not silently rewrite an opted-in ASTC payload to
        // ETC2 before the encoder gets a chance to run.
        if (settings.Format is TextureVariantFormat.ASTC or TextureVariantFormat.ASTC_Mobile &&
            astcEncoder?.SupportsAstc == true)
        {
            return new AndroidTextureVariantSelection(
                settings.Format,
                TextureVariantFormat.ASTC,
                settings.MaxTextureSize,
                settings.CompressionQuality,
                false,
                "requested:astc:encoder-ready");
        }
        AndroidTextureVariantSelection selected = AndroidTextureVariantResolver.Resolve(settings, capabilities);
        bool encoderAvailable = selected.EffectiveFormat switch
        {
            TextureVariantFormat.ASTC or TextureVariantFormat.ASTC_Mobile => astcEncoder?.SupportsAstc == true,
            TextureVariantFormat.ETC2_RGB or TextureVariantFormat.ETC2_RGBA => etc2Encoder?.SupportsEtc2 == true,
            _ => true,
        };
        return AndroidTextureVariantResolver.ResolveForBuild(settings, capabilities, encoderAvailable);
    }

    internal static (byte[] Payload, int Width, int Height, int MipCount) EncodeAstcPayloadForTest(
        byte[] sourcePayload,
        Guid guid,
        bool generateMipmaps,
        bool sRgb,
        bool normalMap,
        int maxTextureSize,
        int quality,
        AndroidTextureEncoderDescriptor encoder)
    {
        EncodedTexture result = EncodeAstc(
            sourcePayload,
            guid,
            new ImportMetadata(generateMipmaps, sRgb, normalMap),
            maxTextureSize,
            quality,
            encoder);
        return (result.Payload, result.Width, result.Height, result.MipCount);
    }

    internal static (byte[] Payload, int Width, int Height, int MipCount) EncodeEtc2PayloadForTest(
        byte[] sourcePayload,
        Guid guid,
        bool generateMipmaps,
        bool sRgb,
        bool normalMap,
        TextureVariantFormat targetFormat,
        int maxTextureSize,
        int quality,
        AndroidTextureEncoderDescriptor encoder)
    {
        EncodedTexture result = EncodeEtc2(
            sourcePayload,
            guid,
            new ImportMetadata(generateMipmaps, sRgb, normalMap),
            targetFormat,
            maxTextureSize,
            quality,
            encoder);
        return (result.Payload, result.Width, result.Height, result.MipCount);
    }

    internal static AndroidTextureEncoderDescriptor? DiscoverEncoder()
    {
        string? configured = Environment.GetEnvironmentVariable("XENGINE_ANDROID_ASTCENC");
        string[] candidates =
        [
            configured ?? string.Empty,
            Path.Combine(AstcEncoderRoot, "astcenc-avx2.exe"),
            Path.Combine(AstcEncoderRoot, "astcenc-sse4.1.exe"),
            Path.Combine(AstcEncoderRoot, "astcenc-sse2.exe"),
        ];
        for (int i = 0; i < candidates.Length; i++)
        {
            string candidate = candidates[i];
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                continue;
            string version = ProbeVersion(candidate);
            if (!version.Contains(AstcEncoderVersion, StringComparison.Ordinal))
                continue;
            return new AndroidTextureEncoderDescriptor(
                "arm.astcenc",
                AstcEncoderVersion,
                Path.GetFullPath(candidate),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate))),
                SupportsAstc: true,
                SupportsEtc2: false);
        }
        return null;
    }

    internal static AndroidTextureEncoderDescriptor? DiscoverEtc2Encoder()
    {
        string? configured = Environment.GetEnvironmentVariable("XENGINE_ANDROID_ETC2COMP");
        string candidate = string.IsNullOrWhiteSpace(configured) ? Etc2EncoderPath : configured;
        if (!File.Exists(candidate))
            return null;

        string help = ProbeHelp(candidate);
        if (!help.Contains("RGBA8", StringComparison.Ordinal) || !help.Contains("RGB8", StringComparison.Ordinal))
            return null;
        return new AndroidTextureEncoderDescriptor(
            "google.etc2comp",
            Etc2EncoderVersion,
            Path.GetFullPath(candidate),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate))),
            SupportsAstc: false,
            SupportsEtc2: true);
    }

    private static string ProbeVersion(string executable)
    {
        try
        {
            using var process = Start(executable, ["-version"]);
            process.WaitForExit(10_000);
            return process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ProbeHelp(string executable)
    {
        try
        {
            using var process = Start(executable, ["-help"]);
            process.WaitForExit(10_000);
            return process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static EncodedTexture EncodeAstc(
        byte[] sourcePayload,
        Guid guid,
        ImportMetadata import,
        int maxTextureSize,
        int quality,
        AndroidTextureEncoderDescriptor encoder)
    {
        EchoObject source = ReadEcho(sourcePayload, guid);
        uint sourceWidth = source["Width"].UIntValue;
        uint sourceHeight = source["Height"].UIntValue;
        TextureImageFormat sourceFormat = (TextureImageFormat)source["ImageFormat"].IntValue;
        if (sourceFormat != TextureImageFormat.Color4b)
            throw new InvalidDataException($"Texture {guid} uses {sourceFormat}; ASTC encoding requires Color4b RGBA source data.");
        byte[] rgba = source["Data"].ByteArrayValue;
        int expected = checked((int)(sourceWidth * sourceHeight * 4u));
        if (rgba.Length != expected)
            throw new InvalidDataException($"Texture {guid} RGBA payload expected {expected} bytes, got {rgba.Length}.");

        int width = checked((int)sourceWidth);
        int height = checked((int)sourceHeight);
        if (maxTextureSize > 0 && Math.Max(width, height) > maxTextureSize)
        {
            double scale = maxTextureSize / (double)Math.Max(width, height);
            width = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.ToEven));
            height = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.ToEven));
        }

        bool mipmapped = source.TryGet("IsMipMapped", out EchoObject? mipValue) && mipValue!.BoolValue && import.GenerateMipmaps;
        int mipCount = mipmapped ? CalculateMipCount(width, height) : 1;
        string work = Path.Combine(Path.GetTempPath(), "XEngine", "AndroidTexture", guid.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var settings = new PixelReadSettings(sourceWidth, sourceHeight, StorageType.Char, PixelMapping.RGBA);
            using var image = new MagickImage();
            image.ReadPixels(rgba, settings);
            image.ColorType = ColorType.TrueColorAlpha;
            TextureImageFormat imageFormat = import.SRgb && !import.NormalMap
                ? TextureImageFormat.Astc4x4Srgb
                : TextureImageFormat.Astc4x4;
            using var output = new MemoryStream();
            for (int mip = 0; mip < mipCount; mip++)
            {
                int mipWidth = Math.Max(1, width >> mip);
                int mipHeight = Math.Max(1, height >> mip);
                using var level = image.Clone();
                if (mipWidth != width || mipHeight != height || width != (int)sourceWidth || height != (int)sourceHeight)
                {
                    level.Resize(new MagickGeometry((uint)mipWidth, (uint)mipHeight) { IgnoreAspectRatio = true });
                }
                string input = Path.Combine(work, $"{mip:D2}.png");
                string astc = Path.Combine(work, $"{mip:D2}.astc");
                level.Write(input, MagickFormat.Png32);

                string profile = import.SRgb && !import.NormalMap ? "-cs" : "-cl";
                string qualityPreset = QualityPreset(quality);
                using var process = Start(encoder.ExecutablePath,
                    [profile, input, astc, "4x4", qualityPreset, "-silent", "-j", "1"]);
                process.WaitForExit(120_000);
                string error = process.StandardError.ReadToEnd();
                string stdout = process.StandardOutput.ReadToEnd();
                if (process.ExitCode != 0 || !File.Exists(astc))
                    throw new InvalidDataException($"astcenc failed for mip {mip}: exit={process.ExitCode}, {SanitizeReason(error + stdout)}");

                byte[] astcFile = File.ReadAllBytes(astc);
                int expectedBytes = Texture.GetDataSize(imageFormat, (uint)mipWidth, (uint)mipHeight);
                if (astcFile.Length != expectedBytes + 16 || astcFile[0] != 0x13 || astcFile[1] != 0xAB ||
                    astcFile[2] != 0xA1 || astcFile[3] != 0x5C)
                {
                    throw new InvalidDataException($"astcenc produced an invalid 4x4 payload for mip {mip}.");
                }
                output.Write(astcFile, 16, expectedBytes);
            }

            EchoObject rewritten = source.Clone();
            rewritten["Width"] = new EchoObject((uint)width);
            rewritten["Height"] = new EchoObject((uint)height);
            rewritten["ImageFormat"] = new EchoObject((int)imageFormat);
            rewritten["IsMipMapped"] = new EchoObject(mipCount > 1);
            rewritten["MipCount"] = new EchoObject(mipCount);
            rewritten["Data"] = new EchoObject(output.ToArray());
            using var binary = new MemoryStream();
            using (var writer = new BinaryWriter(binary, Encoding.UTF8, leaveOpen: true))
                rewritten.WriteToBinary(writer);
            return new EncodedTexture(binary.ToArray(), width, height, mipCount);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch { }
        }
    }

    private static EncodedTexture EncodeEtc2(
        byte[] sourcePayload,
        Guid guid,
        ImportMetadata import,
        TextureVariantFormat targetFormat,
        int maxTextureSize,
        int quality,
        AndroidTextureEncoderDescriptor encoder)
    {
        EchoObject source = ReadEcho(sourcePayload, guid);
        uint sourceWidth = source["Width"].UIntValue;
        uint sourceHeight = source["Height"].UIntValue;
        TextureImageFormat sourceFormat = (TextureImageFormat)source["ImageFormat"].IntValue;
        if (sourceFormat != TextureImageFormat.Color4b)
            throw new InvalidDataException($"Texture {guid} uses {sourceFormat}; ETC2 encoding requires Color4b RGBA source data.");
        byte[] rgba = source["Data"].ByteArrayValue;
        int expected = checked((int)(sourceWidth * sourceHeight * 4u));
        if (rgba.Length != expected)
            throw new InvalidDataException($"Texture {guid} RGBA payload expected {expected} bytes, got {rgba.Length}.");

        int width = checked((int)sourceWidth);
        int height = checked((int)sourceHeight);
        if (maxTextureSize > 0 && Math.Max(width, height) > maxTextureSize)
        {
            double scale = maxTextureSize / (double)Math.Max(width, height);
            width = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.ToEven));
            height = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.ToEven));
        }

        bool mipmapped = source.TryGet("IsMipMapped", out EchoObject? mipValue) && mipValue!.BoolValue && import.GenerateMipmaps;
        int mipCount = mipmapped ? CalculateMipCount(width, height) : 1;
        bool sRgb = import.SRgb && !import.NormalMap;
        TextureImageFormat imageFormat = targetFormat == TextureVariantFormat.ETC2_RGB
            ? (sRgb ? TextureImageFormat.Etc2Rgb8Srgb : TextureImageFormat.Etc2Rgb8)
            : (sRgb ? TextureImageFormat.Etc2Rgba8Srgb : TextureImageFormat.Etc2Rgba8);
        string etcFormat = targetFormat == TextureVariantFormat.ETC2_RGB
            ? (sRgb ? "SRGB8" : "RGB8")
            : (sRgb ? "SRGBA8" : "RGBA8");
        string errorMetric = import.NormalMap ? "normalxyz" : "rec709";
        string work = Path.Combine(Path.GetTempPath(), "XEngine", "AndroidTexture", guid.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var settings = new PixelReadSettings(sourceWidth, sourceHeight, StorageType.Char, PixelMapping.RGBA);
            using var image = new MagickImage();
            image.ReadPixels(rgba, settings);
            image.ColorType = ColorType.TrueColorAlpha;
            using var output = new MemoryStream();
            for (int mip = 0; mip < mipCount; mip++)
            {
                int mipWidth = Math.Max(1, width >> mip);
                int mipHeight = Math.Max(1, height >> mip);
                using var level = image.Clone();
                if (mipWidth != width || mipHeight != height || width != (int)sourceWidth || height != (int)sourceHeight)
                    level.Resize(new MagickGeometry((uint)mipWidth, (uint)mipHeight) { IgnoreAspectRatio = true });

                string input = Path.Combine(work, $"{mip:D2}.png");
                string ktx = Path.Combine(work, $"{mip:D2}.ktx");
                level.Write(input, MagickFormat.Png32);
                using var process = Start(encoder.ExecutablePath,
                    [input, "-format", etcFormat, "-errormetric", errorMetric, "-effort", quality.ToString(System.Globalization.CultureInfo.InvariantCulture), "-jobs", "1", "-output", ktx]);
                process.WaitForExit(120_000);
                string error = process.StandardError.ReadToEnd();
                string stdout = process.StandardOutput.ReadToEnd();
                if (process.ExitCode != 0 || !File.Exists(ktx))
                    throw new InvalidDataException($"etc2comp failed for mip {mip}: exit={process.ExitCode}, {SanitizeReason(error + stdout)}");
                byte[] encoded = ReadKtxPayload(ktx, imageFormat, mipWidth, mipHeight);
                output.Write(encoded, 0, encoded.Length);
            }

            EchoObject rewritten = source.Clone();
            rewritten["Width"] = new EchoObject((uint)width);
            rewritten["Height"] = new EchoObject((uint)height);
            rewritten["ImageFormat"] = new EchoObject((int)imageFormat);
            rewritten["IsMipMapped"] = new EchoObject(mipCount > 1);
            rewritten["MipCount"] = new EchoObject(mipCount);
            rewritten["Data"] = new EchoObject(output.ToArray());
            using var binary = new MemoryStream();
            using (var writer = new BinaryWriter(binary, Encoding.UTF8, leaveOpen: true))
                rewritten.WriteToBinary(writer);
            return new EncodedTexture(binary.ToArray(), width, height, mipCount);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch { }
        }
    }

    private static byte[] ReadKtxPayload(string path, TextureImageFormat expectedFormat, int width, int height)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        byte[] identifier = reader.ReadBytes(12);
        ReadOnlySpan<byte> expectedIdentifier = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!identifier.AsSpan().SequenceEqual(expectedIdentifier) || reader.ReadUInt32() != 0x04030201)
            throw new InvalidDataException("etc2comp did not produce a little-endian KTX 1 payload.");
        reader.ReadUInt32(); // glType
        reader.ReadUInt32(); // glTypeSize
        reader.ReadUInt32(); // glFormat
        uint internalFormat = reader.ReadUInt32();
        reader.ReadUInt32(); // glBaseInternalFormat
        uint ktxWidth = reader.ReadUInt32();
        uint ktxHeight = reader.ReadUInt32();
        reader.ReadUInt32(); // pixelDepth
        reader.ReadUInt32(); // arrayElements
        uint faces = reader.ReadUInt32();
        uint mips = reader.ReadUInt32();
        uint keyValueBytes = reader.ReadUInt32();
        uint expectedInternalFormat = expectedFormat switch
        {
            TextureImageFormat.Etc2Rgb8 => 0x9274u,
            // etc2comp encodes SRGB8/SRGBA8 correctly, but its KTX writer records the
            // bit-compatible UNORM internal format. The KTX container is discarded and
            // the payload is uploaded with the explicit sRGB Vulkan format below.
            TextureImageFormat.Etc2Rgb8Srgb => 0x9274u,
            TextureImageFormat.Etc2Rgba8 => 0x9278u,
            TextureImageFormat.Etc2Rgba8Srgb => 0x9278u,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedFormat)),
        };
        if (internalFormat != expectedInternalFormat || ktxWidth != (uint)width || ktxHeight != (uint)height || faces != 1 || mips != 1)
            throw new InvalidDataException("etc2comp KTX header does not match the requested ETC2 mip.");
        stream.Position = checked(64L + keyValueBytes);
        uint imageBytes = reader.ReadUInt32();
        int expectedBytes = Texture.GetDataSize(expectedFormat, (uint)width, (uint)height);
        if (imageBytes != (uint)expectedBytes)
            throw new InvalidDataException($"etc2comp KTX mip payload expected {expectedBytes} bytes, got {imageBytes}.");
        byte[] payload = reader.ReadBytes(expectedBytes);
        if (payload.Length != expectedBytes)
            throw new EndOfStreamException("etc2comp KTX payload was truncated.");
        return payload;
    }

    private static string GetPayloadFormat(TextureVariantFormat format, bool encoded, bool sRgb)
    {
        if (!encoded)
            return "rgba8";
        return format switch
        {
            TextureVariantFormat.ETC2_RGB => sRgb ? "etc2-rgb8-srgb" : "etc2-rgb8",
            TextureVariantFormat.ETC2_RGBA => sRgb ? "etc2-rgba8-srgb" : "etc2-rgba8",
            TextureVariantFormat.ASTC or TextureVariantFormat.ASTC_Mobile => sRgb ? "astc4x4-srgb" : "astc4x4",
            _ => "rgba8",
        };
    }

    private static Process Start(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        for (int i = 0; i < arguments.Count; i++)
            info.ArgumentList.Add(arguments[i]);
        return Process.Start(info) ?? throw new InvalidOperationException("Failed to start texture encoder.");
    }

    private static EchoObject ReadEcho(byte[] payload, Guid guid)
    {
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            return EchoObject.ReadFromBinary(reader);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException($"Texture {guid} cache is not a valid Echo payload: {exception.Message}", exception);
        }
    }

    private static int CalculateMipCount(int width, int height)
    {
        int size = Math.Max(width, height);
        int levels = 1;
        while (size > 1)
        {
            size >>= 1;
            levels++;
        }
        return levels;
    }

    private static string QualityPreset(int quality) => quality switch
    {
        <= 0 => "-fastest",
        <= 25 => "-fast",
        <= 70 => "-medium",
        <= 98 => "-thorough",
        _ => "-exhaustive",
    };

    private static string SanitizeReason(string value)
    {
        string compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length > 160 ? compact[..160] : compact;
    }

    private static bool IsTexture(AssetEntry asset)
        => string.Equals(asset.ImporterType, "TextureImporter", StringComparison.OrdinalIgnoreCase) ||
            (asset.MainAssetTypeName?.Contains("Texture2D", StringComparison.OrdinalIgnoreCase) ?? false);

    private static ImportMetadata ReadImportMetadata(Project project, AssetEntry asset)
    {
        try
        {
            string source = Path.Combine(project.AssetsPath, asset.Path);
            string metaPath = MetaFile.GetMetaPath(source);
            if (!File.Exists(metaPath))
                return new ImportMetadata(true, true, false);
            EchoObject? settings = MetaFile.Read(metaPath).Settings;
            return new ImportMetadata(
                ReadBool(settings, "generateMipmaps", true),
                ReadBool(settings, "sRGB", true),
                ReadBool(settings, "normalMap", false));
        }
        catch
        {
            return new ImportMetadata(true, true, false);
        }
    }

    private static bool ReadBool(EchoObject? settings, string key, bool fallback)
        => settings != null && settings.TryGet(key, out EchoObject? value) ? value!.BoolValue : fallback;

    private static int CompareGuid(Guid left, Guid right)
        => StringComparer.Ordinal.Compare(left.ToString("N"), right.ToString("N"));

    private readonly record struct ImportMetadata(bool GenerateMipmaps, bool SRgb, bool NormalMap);
    private readonly record struct EncodedTexture(byte[] Payload, int Width, int Height, int MipCount);
}
