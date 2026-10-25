using System;
using System.Collections.Generic;
using System.IO;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.UnityImporter.Editor;

/// <summary>
/// Claims <c>.mat</c> files whose content is Unity serialized YAML. M1: emits a Default/Standard
/// placeholder (or Default/Unlit when the source shader GUID is URP Unlit) with the diffuse map
/// wired, so prefabs render immediately after import. Per-game packages layer their own
/// shader/parameter mapping on top (values re-read from this same file at import time). Native
/// XEngine .mat files (Echo JSON) are delegated to the engine's own
/// <see cref="MaterialImporter"/>.
/// </summary>
[ImporterFor(".mat", Priority = 100)]
public sealed class UnityMatImporter : AssetImporter
{
    /// <summary>Unity URP Unlit shader GUID (Packages/com.unity.render-pipelines.universal/.../Unlit.shadergraph).
    /// Corin's ripped materials use this; they map to engine Default/Unlit for M1 placeholders.</summary>
    public const string UrpUnlitShaderGuid = "650dd9526735d5b46b79224bc6e94025";

    public override int Version => 4;

    public override bool CanImport(string absolutePath) => true; // owns .mat; sniffs in Import and delegates

    public override bool Import(ImportContext ctx)
    {
        if (!UnityAssetSniffer.IsUnityYaml(ctx.AbsolutePath))
            return new MaterialImporter().Import(ctx);

        try
        {
            List<UnityYamlDocument> documents = UnityYaml.ParseFile(ctx.AbsolutePath);
            UnityYamlDocument? doc = null;
            foreach (UnityYamlDocument candidate in documents)
                if (candidate.ClassId == 21) { doc = candidate; break; }
            if (doc == null) throw new InvalidDataException("no Material document in file.");
            UnityYamlNode root = doc.Root;

            string? shaderGuid = root["m_Shader"]?.ScalarOf("guid");
            bool unlit = string.Equals(shaderGuid, UrpUnlitShaderGuid, StringComparison.OrdinalIgnoreCase);
            var material = new Material(Shader.LoadDefault(unlit ? DefaultShader.Unlit : DefaultShader.Standard));
            material.Name = root.ScalarOf("m_Name") is { Length: > 0 } name
                ? name
                : Path.GetFileNameWithoutExtension(ctx.AbsolutePath);

            string? diffuseGuid = FindTextureGuid(root, "_BaseMap") ?? FindTextureGuid(root, "_MainTex");
            if (diffuseGuid != null)
            {
                Guid engineGuid = UnityPrefabImporter.ResolveEngineGuid(ctx.AbsolutePath, diffuseGuid);
                if (engineGuid != Guid.Empty)
                {
                    material.SetTexture("_MainTex", new AssetRef<Texture2D>(engineGuid));
                    ctx.AddDependency(engineGuid, "Texture2D");
                    Debug.Log($"[UnityImport] Material '{material.Name}': _MainTex <- {engineGuid}");
                }
                else
                {
                    Debug.LogWarning($"[UnityImport] Material '{material.Name}': diffuse guid {diffuseGuid} " +
                                     "could not be resolved via unity-guid-map.json.");
                }
            }

            ctx.SetMainAsset(material);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UnityImport] Failed to import Unity material '{ctx.AbsolutePath}': {ex.Message}");
            return false;
        }
    }

    private static string? FindTextureGuid(UnityYamlNode root, string propertyName)
    {
        List<UnityYamlNode>? entries = root["m_SavedProperties"]?["m_TexEnvs"]?.List;
        if (entries == null) return null;
        foreach (UnityYamlNode entry in entries)
        {
            if (entry.Map == null || !entry.Map.ContainsKey(propertyName)) continue;
            string? guid = entry[propertyName]?["m_Texture"]?.ScalarOf("guid");
            if (!string.IsNullOrEmpty(guid)) return guid;
        }
        return null;
    }
}
