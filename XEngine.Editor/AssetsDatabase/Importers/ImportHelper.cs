using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Editor.Importers;

public static class ImportHelper
{
    /// <summary>
    /// Creates a SerializationContext that tracks all AssetRef references encountered during
    /// deserialization. ★ A3: dependencies carry roles (typeof(T).Name) on
    /// <see cref="DependencySerializationContext.Dependencies"/>; read .GuidsOnly for the legacy Guid view.
    /// </summary>
    public static DependencySerializationContext CreateTrackingContext()
        => new DependencySerializationContext();

    /// <summary>
    /// Full boilerplate for importers that simply deserialize a single Echo-serialized asset.
    /// Reads the file, deserializes as T with dependency tracking, sets it as the main asset,
    /// and forwards all discovered dependencies to ctx. Returns false and logs on any error.
    /// </summary>
    public static bool ImportEcho<T>(ImportContext ctx, string errorLabel) where T : EngineObject
    {
        try
        {
            string text = File.ReadAllText(ctx.AbsolutePath);
            var echo = EchoObject.ReadFromString(text);
            // ★ A3: use the parameterless tracking context; read ctx.Dependencies AFTER deserialize.
            // The legacy `out` overload snapshots Guids too early (before deserialization populates
            // the set), so read ctx.GuidsOnly here, post-deserialize.
            var serCtx = CreateTrackingContext();
            var asset = Serializer.Deserialize<T>(echo, serCtx);
            if (asset != null)
            {
                ctx.SetMainAsset(asset);
                foreach (var dep in serCtx.Dependencies)   // ★ A3: forward typed edges (with roles)
                    ctx.AddDependency(dep.Guid, dep.Role);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to import {errorLabel}: {ctx.AbsolutePath}\n{ex.Message}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Recursively walks an EchoObject tree collecting AssetID and PrefabAssetId Guid strings
    /// into deps. AssetID must be walked here even when a tracking context is active because
    /// prefab-instance PropertyOverride.Value blobs are pre-serialized without a context
    /// (see PrefabUtility.CompareField), so AssetRefs inside them never reach normal tracking.
    /// </summary>
    public static void CollectAssetDependencies(EchoObject echo, HashSet<Guid> deps)
    {
        if (echo == null) return;

        if (echo.TagType == EchoType.Compound)
        {
            if (echo.TryGet("AssetID", out var assetIdTag)
                && TryReadGuid(assetIdTag, out Guid assetGuid))
                deps.Add(assetGuid);

            if (echo.TryGet("PrefabAssetId", out var prefabIdTag)
                && TryReadGuid(prefabIdTag, out Guid prefabGuid))
                deps.Add(prefabGuid);

            foreach (var kvp in echo.Tags)
                CollectAssetDependencies(kvp.Value, deps);
        }
        else if (echo.TagType == EchoType.List && echo.List != null)
        {
            foreach (var item in echo.List)
                CollectAssetDependencies(item, deps);
        }
    }

    /// <summary>Read a Guid from either serialization form: a plain string tag, or Echo's compact
    /// Guid tag ({"$t":"g","$v":"…"}) which Guid-typed fields (e.g. MonoBehaviour.AssetID, written
    /// by offline-host scene_component_add) use — reading StringValue on those throws.</summary>
    private static bool TryReadGuid(EchoObject tag, out Guid guid)
    {
        guid = Guid.Empty;
        string? text;
        if (tag.TagType == EchoType.String)
            text = tag.StringValue;
        else if (tag.TagType == EchoType.Compound
            && tag.TryGet("$v", out EchoObject? value)
            && value is not null
            && value.TagType == EchoType.String)
            text = value.StringValue;
        else
            return false;
        return Guid.TryParse(text, out guid) && guid != Guid.Empty;
    }
}
