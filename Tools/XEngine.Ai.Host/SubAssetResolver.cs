using XEngine.Ai.Protocol;
using XEngine.Clay;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Gen-P0.5 — Offline sub-asset GUID derivation for model files (.gltf/.glb/.obj/.fbx).
///
/// <para>The editor assigns every sub-asset (mesh, material, animation clip, embedded texture) a
/// deterministic GUID: <c>SHA256(parentGuid + stableSourceId)[0..16]</c> (see the editor's
/// <c>AssetEntry.DeriveSubAssetGuid</c>). Those GUIDs are what scenes and prefabs reference. In the
/// headless flow (<c>asset_import</c> copies the file + mints a .meta, no editor import runs) an
/// agent needs those GUIDs *before* any editor has imported the file — e.g. to wire a freshly
/// generated GLB's mesh into a MeshRenderer in the same session.</para>
///
/// <para>Stable ids for models are the sub-asset names produced by the import pipeline, so this
/// resolver replays the exact same code path the editor runs — the Clay importer with the same
/// post-process preset (<c>ClayBackedImporter.MapSettings</c>: GameQuality minus normal/tangent
/// steps, FlipUVs only for glTF-family) — and then mirrors <c>EditorModelImporter</c>'s
/// registration rules name-for-name. Names are read from the real import, never guessed, so
/// primitive splitting ("Sword/prim0"), Clay's fallback names ("Mesh_1/prim0", "Animation_0") and
/// texture name resolution (texture.name ?? image.name) all match by construction.</para>
/// </summary>
internal static class SubAssetResolver
{
    public static SubAssetList Resolve(string absolutePath, string relativePath)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Asset file not found: {absolutePath}");
        }

        string extension = Path.GetExtension(absolutePath);
        if (!ModelImportReplay.ModelExtensions.Contains(extension))
        {
            string importer = AssetWriter.InferImporter(absolutePath);
            throw new InvalidOperationException(
                $"asset_subassets derives sub-assets for model files only (.gltf/.glb/.obj/.fbx). " +
                $"'{relativePath}' is imported by {importer}, whose sub-assets are not derivable offline yet.");
        }

        string metaPath = absolutePath + ".meta";
        if (!File.Exists(metaPath))
        {
            throw new FileNotFoundException(
                $"No .meta sidecar for '{relativePath}' — sub-asset GUIDs derive from the parent GUID. " +
                "Import the file first (asset_import mints the .meta).");
        }

        EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
        string guidText = meta.TryGet("guid", out EchoObject? g) && g!.TagType == EchoType.String ? g.StringValue : "";
        if (!Guid.TryParse(guidText, out Guid parentGuid))
        {
            throw new InvalidOperationException($"The .meta of '{relativePath}' has no parseable guid ('{guidText}').");
        }

        EchoObject? settings = meta.TryGet("settings", out EchoObject? s) && s!.TagType == EchoType.Compound ? s : null;
        bool importAnimation = settings == null
            || !settings.TryGet("importAnimation", out EchoObject? ia)
            || ia!.BoolValue;
        float unitScale = settings != null && settings.TryGet("unitScale", out EchoObject? us) ? us!.FloatValue : 1.0f;
        bool flipUVs = settings == null || !settings.TryGet("flipUVs", out EchoObject? fu) || fu!.BoolValue;

        Model model = ModelImportReplay.Load(absolutePath, extension, unitScale, flipUVs);

        var subAssets = new List<SubAssetInfo>();
        var warnings = new List<string>();
        // Model mesh/material/animation ids share one namespace.  EditorModelImporter preserves
        // the first source id and deterministically namespaces later collisions, so the offline
        // resolver must return the same ids rather than dropping the later sub-asset.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        void AddModel(string baseId, string kind, string collisionKind, int index)
        {
            string stableId = baseId;
            if (!seenIds.Add(stableId))
            {
                string collisionBase = $"{collisionKind}_{index}_{baseId}";
                stableId = collisionBase;
                int suffix = 2;
                while (!seenIds.Add(stableId))
                    stableId = $"{collisionBase}_{suffix++}";
            }

            // Keep the authored source name as the display name; only the deterministic stable
            // id receives the collision namespace, matching ctx.AddSubAsset(stableId, baseId,...).
            subAssets.Add(new SubAssetInfo(stableId, baseId, kind, DeriveSubAssetGuid(parentGuid, stableId).ToString()));
        }

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            AddModel(model.Meshes[i].Name ?? $"Mesh_{i}", "Mesh", "Mesh", i);
        }

        for (int i = 0; i < model.Materials.Count; i++)
        {
            // ClayBackedImporter.BuildMaterial coerces empty/null to "Material" before the editor
            // registers it, so the editor's own `?? $"Material_{i}"` fallback never fires.
            string? name = model.Materials[i].Name;
            AddModel(string.IsNullOrEmpty(name) ? "Material" : name!, "Material", "Material", i);
        }

        if (importAnimation)
        {
            for (int i = 0; i < model.AnimationClips.Count; i++)
            {
                AddModel(model.AnimationClips[i].Name ?? $"Animation_{i}", "AnimationClip", "Animation", i);
            }
        }

        foreach (Texture texture in model.Textures)
        {
            // External file textures resolve to the already-imported project asset (a dependency,
            // not a sub-asset). Only embedded images become Texture2D sub-assets of the model.
            bool isExternal = !string.IsNullOrEmpty(texture.SourcePath) && File.Exists(texture.SourcePath);
            if (isExternal || texture.EncodedBytes is not { Length: > 0 })
            {
                continue;
            }

            string stableId = string.IsNullOrEmpty(texture.Name) ? "EmbeddedTexture" : texture.Name!;
            if (!seenIds.Add(stableId))
            {
                warnings.Add(
                    $"Duplicate embedded texture stable id '{stableId}' — editor import uses the authored " +
                    "texture name and may reject this source; rename the embedded image to keep ids unique.");
                continue;
            }
            subAssets.Add(new SubAssetInfo(stableId, stableId, "Texture2D", DeriveSubAssetGuid(parentGuid, stableId).ToString()));
        }

        WarnOnEnabledMeshFeatures(settings, warnings);

        return new SubAssetList(relativePath, parentGuid.ToString(), "EditorModelImporter", subAssets.Count, subAssets, warnings);
    }

    /// <summary>Same GUID scheme as the editor's <c>AssetEntry.DeriveSubAssetGuid</c> (kept in
    /// sync by <c>SubAssetResolverTests</c>): SHA256 over parent GUID bytes + UTF8(stableId),
    /// first 16 bytes.</summary>
    internal static Guid DeriveSubAssetGuid(Guid parentGuid, string stableId)
    {
        byte[] parentBytes = parentGuid.ToByteArray();
        byte[] nameBytes = Encoding.UTF8.GetBytes(stableId);
        byte[] combined = new byte[parentBytes.Length + nameBytes.Length];
        parentBytes.CopyTo(combined, 0);
        nameBytes.CopyTo(combined, parentBytes.Length);
        byte[] hash = SHA256.HashData(combined);
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Mesh-feature generators (SDF, ...) add sub-assets named <c>{meshName}_{key}</c>
    /// but require the engine to generate; they are default-off. Surface a warning when the meta
    /// enables one so the agent knows the derived list is incomplete.</summary>
    private static void WarnOnEnabledMeshFeatures(EchoObject? settings, List<string> warnings)
    {
        if (settings == null)
        {
            return;
        }

        foreach (string key in settings.GetNames())
        {
            EchoObject? value = settings.Get(key);
            if (value is not { TagType: EchoType.Compound })
            {
                continue;
            }

            if (value.TryGet("enabled", out EchoObject? enabled) && enabled!.BoolValue)
            {
                warnings.Add(
                    $"Importer setting '{key}' enables engine-generated mesh-feature sub-assets " +
                    $"(named '{{meshName}}_{key}') that offline derivation cannot produce. " +
                    "Their GUIDs still follow SHA256(parentGuid + stableId) if you need them.");
            }
        }
    }
}
