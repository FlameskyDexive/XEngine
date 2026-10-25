// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.UnityImporter.Editor;
using XEngine.Vector;

namespace XEngine.RpgVfx.Editor;

/// <summary>
/// Converts one Hovl Studio Unity .mat file into a native XEngine .mat:
/// shader via <see cref="HovlShaderRemap"/>, colors/floats/vectors by name, Unity texture
/// GUIDs resolved through <paramref name="textureGuidResolver"/> (by texture file name),
/// and per-texture scale/offset baked into "X_ST" Vector4 properties (the engine has no
/// implicit _ST convention).
/// </summary>
public static class HovlMaterialConverter
{
    public static bool Convert(string unityMatPath, string outputPath,
        Func<string, Shader?> shaderResolver,
        Func<string, string?> textureGuidResolver,
        SerializationContext context,
        List<string> warnings)
    {
        List<UnityYamlDocument> docs = UnityYaml.ParseFile(unityMatPath);
        UnityYamlDocument? doc = docs.Count > 0 ? docs[0] : null;
        if (doc is null)
        {
            warnings.Add($"[mat] {Path.GetFileName(unityMatPath)}: no YAML documents");
            return false;
        }
        UnityYamlNode root = doc.Root;

        string? shaderGuid = root["m_Shader"]?.ScalarOf("guid");
        if (string.IsNullOrEmpty(shaderGuid))
        {
            warnings.Add($"[mat] {Path.GetFileName(unityMatPath)}: no m_Shader guid");
            return false;
        }
        float blend2 = ReadFloat(root, "m_Floats", "_Blend2");
        if (!HovlShaderRemap.TryResolveEngineShader(shaderGuid, blend2, out var engineShaderName, out var remapWarning))
        {
            warnings.Add($"[mat] {Path.GetFileName(unityMatPath)}: unknown shader guid {shaderGuid}");
            return false;
        }
        if (remapWarning is not null)
            warnings.Add($"[mat] {Path.GetFileName(unityMatPath)}: {remapWarning}");

        Shader? shader = shaderResolver(engineShaderName);
        if (shader is null)
        {
            warnings.Add($"[mat] {Path.GetFileName(unityMatPath)}: engine shader not resolvable: {engineShaderName}");
            return false;
        }

        var material = new Material(shader);
        material.Name = Path.GetFileNameWithoutExtension(unityMatPath);

        // Colors: name → {r,g,b,a}.
        foreach (UnityYamlNode entry in ListOf(root["m_SavedProperties"]?["m_Colors"]))
        {
            string? colorName = entry.ScalarOf("first");
            UnityYamlNode? second = entry["second"];
            if (colorName is null || second is null) continue;
            var color = new Color(
                second.FloatOf("r", 1f), second.FloatOf("g", 1f),
                second.FloatOf("b", 1f), second.FloatOf("a", 1f));
            material.SetColor(colorName, color);
        }

        // Floats.
        foreach (UnityYamlNode entry in ListOf(root["m_SavedProperties"]?["m_Floats"]))
        {
            string? floatName = entry.ScalarOf("first");
            if (floatName is null) continue;
            material.SetFloat(floatName, ParseFloat(entry["second"]?.Scalar));
        }

        // Texture envs: texture reference + scale/offset → "X_ST".
        foreach (UnityYamlNode entry in ListOf(root["m_SavedProperties"]?["m_TexEnvs"]))
        {
            string? texName = entry.ScalarOf("first");
            UnityYamlNode? second = entry["second"];
            if (texName is null || second is null) continue;

            string? texGuid = second["m_Texture"]?.ScalarOf("guid");
            if (!string.IsNullOrEmpty(texGuid))
            {
                string? packageGuid = textureGuidResolver(texGuid);
                if (packageGuid is not null)
                {
                    material.SetTexture(texName, new AssetRef<Texture2D>(Guid.Parse(packageGuid)));
                }
                else
                {
                    warnings.Add($"[mat] {Path.GetFileName(unityMatPath)}: texture guid {texGuid} unresolved for {texName}");
                }
            }

            float sx = second["m_Scale"]?.FloatOf("x", 1f) ?? 1f;
            float sy = second["m_Scale"]?.FloatOf("y", 1f) ?? 1f;
            float ox = second["m_Offset"]?.FloatOf("x") ?? 0f;
            float oy = second["m_Offset"]?.FloatOf("y") ?? 0f;
            if (sx != 1f || sy != 1f || ox != 0f || oy != 0f)
            {
                material.SetVector($"{texName}_ST", new Float4(sx, sy, ox, oy));
            }
        }

        EchoObject echo = Echo.Serializer.Serialize(material, context);
        string text = echo.WriteToString();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, text);
        return true;
    }

    private static IReadOnlyList<UnityYamlNode> ListOf(UnityYamlNode? node) =>
        node?.List ?? (IReadOnlyList<UnityYamlNode>)Array.Empty<UnityYamlNode>();

    private static float ReadFloat(UnityYamlNode root, string section, string name)
    {
        foreach (UnityYamlNode entry in ListOf(root["m_SavedProperties"]?[section]))
        {
            if (string.Equals(entry.ScalarOf("first"), name, StringComparison.Ordinal))
                return ParseFloat(entry["second"]?.Scalar);
        }
        return 0f;
    }

    private static float ParseFloat(string? text) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : 0f;
}
