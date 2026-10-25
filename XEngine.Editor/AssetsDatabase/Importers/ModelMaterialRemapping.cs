using System;
using System.Collections.Generic;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Importers;

/// <summary>Source material identities remain embedded subassets; only renderer references change.</summary>
public static class ModelMaterialRemapping
{
    public const string SettingsKey = "materialRemaps";

    // Standalone project materials avoid recursive imports through another model's subassets.
    public static bool IsExternalMaterial(Guid guid)
    {
        var backend = EditorAssetBackend.Instance;
        var entry = backend?.GetEntry(guid);
        if (entry == null || (entry.ImporterType != "MaterialImporter" && entry.ImporterType != "UnityMatImporter")) return false;
        if (!backend!.TryGetSourcePath(guid, out _)) return false;
        return AssetDatabase.Get(guid) is Material material && !material.IsDisposed;
    }

    public static void Apply(ImportContext context, ModelImportResult data, Guid siblingDefault,
        Func<Guid, bool> isExternalMaterial, Action<string> warning)
    {
        if (data.RootGO == null) return;
        var sourceIds = new HashSet<Guid>();
        for (int i = 0; i < data.Materials.Count; i++) sourceIds.Add(data.Materials[i].AssetID);
        var remaps = new Dictionary<Guid, Guid>();
        if (context.Settings?.TryGet(SettingsKey, out var settings) == true)
        {
            if (settings.TagType != EchoType.Compound)
                warning("Material remaps must be a compound; keeping importer defaults.");
            else foreach (var pair in settings.Tags)
            {
                if (!Guid.TryParse(pair.Key, out var source) || !sourceIds.Contains(source)) continue;
                if (pair.Value.TagType != EchoType.String || !Guid.TryParse(pair.Value.StringValue, out var target))
                {
                    warning($"Invalid material remap for {source}; keeping importer default.");
                    continue;
                }
                if (target == Guid.Empty) continue;
                if (target == context.AssetGuid || sourceIds.Contains(target) || !isExternalMaterial(target))
                {
                    warning($"Material remap {source} -> {target} requires a standalone Material asset; keeping importer default.");
                    continue;
                }
                remaps[source] = target;
            }
        }

        foreach (var renderer in data.RootGO.GetComponentsInChildren<SkinnedMeshRenderer>(includeSelf: true, includeInactive: true))
            ApplySlots(renderer.Materials, remaps, siblingDefault, context);
        foreach (var renderer in data.RootGO.GetComponentsInChildren<MeshRenderer>(includeSelf: true, includeInactive: true))
            ApplySlots(renderer.Materials, remaps, siblingDefault, context);
    }

    private static void ApplySlots(List<AssetRef<Material>> slots, Dictionary<Guid, Guid> remaps,
        Guid siblingDefault, ImportContext context)
    {
        Guid originalFirst = slots.Count > 0 ? slots[0].AssetID : Guid.Empty;
        if (siblingDefault != Guid.Empty)
        {
            if (slots.Count == 0) slots.Add(new AssetRef<Material>(siblingDefault));
            else slots[0] = new AssetRef<Material>(siblingDefault);
        }
        for (int i = 0; i < slots.Count; i++)
        {
            Guid source = i == 0 ? originalFirst : slots[i].AssetID;
            if (!remaps.TryGetValue(source, out var target)) continue;
            slots[i] = new AssetRef<Material>(target);
            context.AddDependency(target, "Material");
        }
    }
}
