// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Editor.Importers;

/// <summary>
/// Imports Echo-serialized text assets: <c>.asset</c> (ScriptableObject / EngineObject), <c>.controller</c>
/// (AnimatorController), <c>.overridecontroller</c> (AnimatorOverrideController), <c>.anim</c> (AnimationClip), <c>.motion</c> (BlendTree), <c>.mask</c>
/// (AvatarMask), <c>.avatar</c> (Avatar), <c>.transition</c> (TransitionAsset), <c>.pipeline</c> (UniversalRenderPipelineAsset), <c>.renderer</c> (UniversalRenderData),
/// <c>.volumeprofile</c> (VolumeProfile), <c>.postprofile</c> (PostProcessProfile),
/// <c>.shadergraph</c> (ShaderGraphAsset),
/// <c>.vfx</c> (VfxGraphAsset),
/// <c>.buildprofile</c> (BuildProfile), <c>.navmesh</c> (NavMeshData),
/// <c>.physicmaterial</c> (PhysicsMaterial) and <c>.physicmaterial2d</c> (PhysicsMaterial2D),
/// and <c>.preset</c> (Preset).
/// Files are written in Echo text format with a full <c>$type</c> envelope, so the
/// concrete runtime type is recovered from the data itself rather than known ahead of time.
/// </summary>
[ImporterFor(".asset", ".controller", ".overridecontroller", ".anim", ".motion", ".mask", ".avatar", ".transition", ".transitionlibrary", ".timeline", ".pipeline", ".renderer", ".volumeprofile", ".postprofile", ".shadergraph", ".vfx", ".nodeplay", ".buildprofile", ".navmesh", ".physicmaterial", ".physicmaterial2d", ".preset")]
public sealed class ScriptableObjectImporter : AssetImporter
{
    public override int Version => 1;

    public override bool Import(ImportContext ctx)
    {
        try
        {
            string text = File.ReadAllText(ctx.AbsolutePath);
            var echo = EchoObject.ReadFromString(text);

            Type? targetType = ResolveMainAssetType(echo);
            if (targetType == null)
            {
                Debug.LogError($"Failed to import .asset '{ctx.AbsolutePath}': missing, unresolved, or invalid $type.");
                return false;
            }

            var serCtx = ImportHelper.CreateTrackingContext();

            var asset = Serializer.Deserialize(echo, targetType, serCtx) as EngineObject;
            if (asset == null)
            {
                Debug.LogError($"Failed to import .asset '{ctx.AbsolutePath}': deserialization returned null (target type {targetType.FullName}).");
                return false;
            }

            ctx.SetMainAsset(asset);
            foreach (var dep in serCtx.Dependencies)   // ★ A3: forward typed edges
                ctx.AddDependency(dep.Guid, dep.Role);

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to import .asset: {ctx.AbsolutePath}\n{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Recover the concrete runtime type from the Echo type envelope so the deserializer
    /// instantiates the right subclass. Files written by <c>AssetCreateMenu</c> use the full
    /// <c>$type</c> wrapper (the compound carries the type name, optionally with a nested
    /// <c>$value</c> holding the data); the cache writer (<c>SerializeToCache</c>) uses the same
    /// shape. Returns null when the envelope is absent, unresolved, or does not name an
    /// <see cref="EngineObject"/> type.
    /// </summary>
    private static Type? ResolveMainAssetType(EchoObject echo)
    {
        if (echo != null && echo.TagType == EchoType.Compound
            && echo.TryGet("$type", out var typeTag) && !string.IsNullOrEmpty(typeTag.StringValue))
        {
            string name = typeTag.StringValue;

            // TypeNameRegistry.ResolveFullTypeName accepts both assembly-qualified names and bare
            // FullNames (it falls back to ReflectionUtils.FindTypeByName), so try it first.
            Type? resolved = TypeNameRegistry.ResolveFullTypeName(name);

            // RuntimeUtils.ResolveType additionally reaches user-script types living in collectible
            // AssemblyLoadContexts that Type.GetType cannot see - important for authored .asset files.
            resolved ??= RuntimeUtils.ResolveType(name);

            if (resolved != null && typeof(EngineObject).IsAssignableFrom(resolved))
                return resolved;
        }

        return null;
    }
}
