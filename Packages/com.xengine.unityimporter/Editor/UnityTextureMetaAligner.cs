using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.UnityImporter.Editor;

/// <summary>
/// Shared Unity TextureImporter-meta → engine TextureImporter-settings alignment, used by the
/// per-game asset copiers when copying textures out of a Unity source project. Unity meta files
/// are headerless YAML (`fileFormatVersion/guid/TextureImporter:`).
/// </summary>
public static class UnityTextureMetaAligner
{
    /// <summary>Maps sRGBTexture→sRGB, mipmaps.enableMipMap→generateMipmaps,
    /// textureSettings.filterMode→min/magFilter, textureSettings.wrapU→wrapMode, and the
    /// current/legacy alpha-source fields. Returns null when the meta cannot be read (warnings
    /// explain why).</summary>
    public static EchoObject? AlignSettings(string unityMetaPath, ICollection<string> warnings)
    {
        try
        {
            if (!File.Exists(unityMetaPath)) return null;
            UnityYamlNode root = UnityYaml.ParsePlainFile(unityMetaPath);
            UnityYamlNode? importer = root["TextureImporter"];
            if (importer?.Map == null) return null;
            UnityYamlNode? mipmaps = importer["mipmaps"];
            UnityYamlNode? textureSettings = importer["textureSettings"];
            bool srgb = mipmaps?.ScalarOf("sRGBTexture") != "0";
            bool generateMipmaps = mipmaps?.ScalarOf("enableMipMap") == "1";
            long filterMode = textureSettings?.LongOf("filterMode") ?? 1;   // 0=Point 1=Bilinear 2=Trilinear
            long wrapU = textureSettings?.LongOf("wrapU") ?? 0;             // 0=Repeat 1=Clamp (-1=default→Repeat)

            // Unity's Point/Bilinear select one mip level; only Trilinear blends adjacent levels.
            TextureMag mag = filterMode == 0 ? TextureMag.Nearest : TextureMag.Linear;
            TextureMin min = !generateMipmaps
                ? (filterMode == 0 ? TextureMin.Nearest : TextureMin.Linear)
                : filterMode switch
                {
                    0 => TextureMin.NearestMipmapNearest,
                    2 => TextureMin.LinearMipmapLinear,
                    _ => TextureMin.LinearMipmapNearest,
                };
            TextureWrap wrap = wrapU switch
            {
                1 or 3 => TextureWrap.ClampToEdge,
                2 => TextureWrap.MirroredRepeat,
                _ => TextureWrap.Repeat,
            };
            if (wrapU == 3)
                warnings.Add($"Texture '{unityMetaPath}' uses unsupported MirrorOnce wrapping; using ClampToEdge.");
            long wrapV = textureSettings?.LongOf("wrapV") ?? wrapU;
            if (wrapV != wrapU)
                warnings.Add($"Texture '{unityMetaPath}' uses different U/V wrapping; the importer currently applies U to both axes.");

            var settings = EchoObject.NewCompound();
            settings["sRGB"] = new EchoObject(srgb);
            settings["generateMipmaps"] = new EchoObject(generateMipmaps);
            settings["minFilter"] = new EchoObject((int)min);
            settings["magFilter"] = new EchoObject((int)mag);
            settings["wrapMode"] = new EchoObject((int)wrap);
            settings["alphaSource"] = new EchoObject((int)ReadAlphaSource(importer, unityMetaPath, warnings));
            return settings;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to align texture settings from '{unityMetaPath}': {ex.Message}");
            return null;
        }
    }

    private static TextureAlphaSource ReadAlphaSource(UnityYamlNode importer, string unityMetaPath,
        ICollection<string> warnings)
    {
        // A current alphaSource is authoritative, including an explicit FromInput value. This
        // avoids stale legacy fields changing a setting that a user has deliberately selected.
        string? currentText = importer.ScalarOf("alphaSource");
        if (TryReadInt(currentText, out int current))
        {
            if (IsValidAlphaSource(current))
                return (TextureAlphaSource)current;

            warnings.Add($"Texture '{unityMetaPath}' has unsupported alphaSource '{current}'; trying legacy fields.");
        }
        else if (currentText != null)
        {
            warnings.Add($"Texture '{unityMetaPath}' has malformed alphaSource '{currentText}'; trying legacy fields.");
        }

        string? legacyText = importer.ScalarOf("alphaUsage");
        if (TryReadInt(legacyText, out int legacy))
        {
            if (IsValidAlphaSource(legacy))
                return (TextureAlphaSource)legacy;

            warnings.Add($"Texture '{unityMetaPath}' has unsupported alphaUsage '{legacy}'; using FromInput.");
        }
        else if (legacyText != null)
        {
            warnings.Add($"Texture '{unityMetaPath}' has malformed alphaUsage '{legacyText}'; using FromInput.");
        }

        if (IsTrue(importer.ScalarOf("grayScaleToAlpha"))
            || IsTrue(importer.ScalarOf("grayscaleToAlpha")))
        {
            return TextureAlphaSource.FromGrayScale;
        }

        return TextureAlphaSource.FromInput;
    }

    private static bool TryReadInt(string? value, out int result)
    {
        result = default;
        return value != null
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool IsTrue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidAlphaSource(int value)
        => value == (int)TextureAlphaSource.None
            || value == (int)TextureAlphaSource.FromInput
            || value == (int)TextureAlphaSource.FromGrayScale;
}
