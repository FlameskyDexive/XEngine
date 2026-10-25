using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Animation.Humanoid;
using XEngine.Runtime.MeshFeatures;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.Editor.Importers;

[ImporterFor(".gltf", ".glb", ".obj", ".fbx")]
public partial class EditorModelImporter : AssetImporter
{
    // v23: Generic Create produces an Avatar/root policy; disabled animation has no dangling components.
    // v22: optional unused skin-palette compaction, retaining the complete hierarchy and root references.
    // v21: effective 1/2/4 skin influence limits; strongest selection includes all source influences.
    // v20: explicit file-unit conversion; user scale includes morph deltas and position-key tangents.
    // v19: namespace colliding model sub-asset ids (mesh/material names can be identical).
    // v18: complete missing FBX motion-root binds/origins from proven child constraints, preserve authored
    // transform-stack samples, and bake source interpolation without re-smoothing in Runtime.
    // Invalidate clips whose source reference mixed a skill frame with the skin bind hierarchy.
    private const int BaseVersion = 23;
    public override int Version => BaseVersion + MeshFeatureRegistry.AggregateVersion;

    public override bool Import(ImportContext ctx)
    {
        try
        {
            // The editor's resolver never decodes another asset's pixel data itself: external
            // textures resolve to the already-imported project asset by path/GUID, and embedded ones
            // are registered as sub-assets. Unconditional, not settings-gated: holds for every
            // editor import.
            var importSettings = new ModelImporterSettings { TextureResolver = new EditorModelTextureResolver(ctx) };
            if (ctx.Settings != null)
            {
                var s = ctx.Settings;
                importSettings.GenerateNormals = !s.TryGet("generateNormals", out var gn) || gn.BoolValue;
                importSettings.GenerateSmoothNormals = !s.TryGet("generateSmoothNormals", out var gsn) || gsn.BoolValue;
                importSettings.RecalculateNormals = s.TryGet("recalculateNormals", out var rn) && rn.BoolValue;
                importSettings.CalculateTangentSpace = !s.TryGet("calculateTangents", out var ct) || ct.BoolValue;
                importSettings.FlipUVs = !s.TryGet("flipUVs", out var fu) || fu.BoolValue;
                importSettings.UnitScale = s.TryGet("unitScale", out var us) ? us.FloatValue : 1.0f;
                importSettings.ConvertFileUnits = !s.TryGet("convertFileUnits", out var cfu) || cfu.BoolValue;
                importSettings.SkinBoneCount = s.TryGet("skinBoneCount", out var skin) ? skin.IntValue : 4;
                importSettings.StripBones = s.TryGet("stripBones", out var strip) && strip.BoolValue;
                if (importSettings.UnitScale != 1.0f)
                    Debug.Log($"[ModelImport] '{Path.GetFileName(ctx.AbsolutePath)}' unitScale={importSettings.UnitScale}");
                // Off by default (slow; some models ship their own UV2). The importer runs the
                // unwrap in its post-process so the baked UV2 is captured before serialization.
                importSettings.GenerateLightmapUVs = s.TryGet("generateLightmapUVs", out var glu) && glu.BoolValue;
                importSettings.AnimationType = s.TryGet("animationType", out var at) ? at.IntValue : 0;
            }

            // 1. Import creates live meshes, materials, animations, GO hierarchy (+ UV2 if enabled).
            var importer = new ModelImporter();
            var data = importer.Import(new FileInfo(ctx.AbsolutePath), importSettings);
            ApplyUnitScale(data, importSettings.UnitScale);
            RebuildBindPosesRendererAnchored(data);

            // 2. Register sub-assets assigns deterministic GUIDs immediately.  Some FBX files
            // use the same source name for a mesh and its material (hero51010f is one example).
            // ImportResult intentionally rejects duplicate stable ids, so preserve the first
            // source id and deterministically namespace later collisions instead of rejecting a
            // perfectly usable model.  The display name remains the authored name; only the
            // stable id receives the collision suffix.
            var usedModelSubAssetIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < data.Meshes.Count; i++)
                AddModelSubAsset(ctx, usedModelSubAssetIds, data.Meshes[i].Name, $"Mesh_{i}", "Mesh", i, data.Meshes[i]);

            for (int i = 0; i < data.Materials.Count; i++)
                AddModelSubAsset(ctx, usedModelSubAssetIds, data.Materials[i].Name, $"Material_{i}", "Material", i, data.Materials[i]);

            ModelMaterialRemapping.Apply(ctx, data, ResolveSiblingUnityMaterial(ctx),
                ModelMaterialRemapping.IsExternalMaterial, message => Debug.LogWarning($"[ModelImport] '{ctx.AbsolutePath}': {message}"));

            bool importAnimation = ctx.Settings == null
                || !ctx.Settings.TryGet("importAnimation", out var importAnimFlag)
                || importAnimFlag.BoolValue;
            if (importAnimation)
            {
                for (int i = 0; i < data.Animations.Count; i++)
                    AddModelSubAsset(ctx, usedModelSubAssetIds, data.Animations[i].Name, $"Animation_{i}", "Animation", i, data.Animations[i]);
            }
            else
                RemoveImportedAnimation(data);

            // A Generic Avatar is a model sub-asset even when no animation component is needed.
            // Validate full-path identity before a main asset or cache can be published.
            GenerateGenericAvatar(ctx, data);

            // M53 — humanoid avatar wiring. avatarDefinition: 0 = CreateFromThisModel (auto-map this
            // rig, register an Avatar sub-asset, bind it on the attached Animator), 1 = CopyFromOther
            // (bind an existing avatar by the avatarSourceGuid setting), 2 = No Avatar (default).
            // M54 — with a fresh avatar, the imported clips are baked to muscle curves in place.
            GenerateHumanoidAvatar(ctx, usedModelSubAssetIds, data.RootGO, data.Animations);

            // Note: model-referenced textures (both external and embedded) are already fully
            // resolved by this point - materials carry AssetRefs, and any embedded texture is
            // already registered as a sub-asset - both as side effects of EditorModelTextureResolver
            // running during importer.Import() above.

            // 2b. Generate mesh features (SDF, BVH, Prism, ...) per mesh, registered as sub-assets.
            for (int i = 0; i < data.Meshes.Count; i++)
                MeshFeatureImporter.GenerateAll(data.Meshes[i], ctx.Settings, ctx);

            // 3. Serialize GO hierarchy sub-assets have correct IDs, AssetRefs serialize as GUIDs.
            //    Tracked (matching SceneImporter/PrefabImporter) so the prefab's own dependency list
            //    reflects what its GameObject hierarchy actually references.
            //    Model files import into a PrefabAsset (prowl-sync M4.1, upstream 9d976099 — the
            //    separate Model asset type was removed).
            var prefab = new PrefabAsset
            {
                Name = Path.GetFileNameWithoutExtension(ctx.AbsolutePath),
                InstanceType = PrefabInstanceType.Model,
            };
            if (data.RootGO != null)
            {
                var goSerCtx = ImportHelper.CreateTrackingContext();
                prefab.GameObjectData = Serializer.Serialize(typeof(object), data.RootGO, goSerCtx);
                foreach (var dep in goSerCtx.Dependencies)   // ★ A3: forward typed edges
                    ctx.AddDependency(dep.Guid, dep.Role);
            }

            ctx.SetMainAsset(prefab);
            return true;
        }
        catch (Exception ex)
        {
            return ctx.Fail(ex.Message);
        }
    }

    private static void AddModelSubAsset(
        ImportContext ctx,
        HashSet<string> usedIds,
        string? sourceName,
        string fallbackName,
        string kind,
        int index,
        EngineObject asset)
    {
        string baseId = string.IsNullOrWhiteSpace(sourceName) ? fallbackName : sourceName!;
        string stableId = baseId;
        if (!usedIds.Add(stableId))
        {
            string collisionBase = $"{kind}_{index}_{baseId}";
            stableId = collisionBase;
            int suffix = 2;
            while (!usedIds.Add(stableId))
                stableId = $"{collisionBase}_{suffix++}";
        }

        // Keep the authored display name where possible. ImportContext will make display names
        // unique independently; stable ids are the only values used for deterministic GUIDs.
        ctx.AddSubAsset(stableId, baseId, asset);
    }

    /// <summary>
    /// M53 — creates/binds the humanoid <see cref="Avatar"/> per the model's Rig-page settings.
    /// Only runs for AnimationType=Humanoid; default settings (avatarDefinition=2) are a no-op so
    /// existing assets re-import identically.
    /// </summary>
    private static void GenerateHumanoidAvatar(
        ImportContext ctx,
        HashSet<string> usedIds,
        GameObject? rootGO,
        List<AnimationClip> animations)
    {
        if (rootGO == null || ctx.Settings == null)
            return;

        if (!ctx.Settings.TryGet("animationType", out var typeTag) || typeTag.IntValue != ModelRigBinding.Humanoid)
            return;
        if (!ctx.Settings.TryGet("avatarDefinition", out var defTag))
            return;

        var animator = rootGO.GetComponent<Animator>();
        if (animator == null)
            return;

        int definition = defTag.IntValue;
        if (definition == 0) // CreateFromThisModel
        {
            var avatar = Avatar.BuildAuto(rootGO.Transform,
                Path.GetFileNameWithoutExtension(ctx.AbsolutePath) + "Avatar");
            string? problems = avatar.Validate();
            if (problems != null)
                Debug.LogWarning($"[ModelImport] auto-mapped avatar for '{ctx.AbsolutePath}' has gaps:\n{problems}");

            AddModelSubAsset(ctx, usedIds, avatar.Name, "Avatar_0", "Avatar", 0, avatar);
            animator.Avatar = new AssetRef<Avatar>(avatar);

            // M54 — bake every imported clip to role-keyed muscle curves against this avatar.
            for (int i = 0; i < animations.Count; i++)
                HumanoidClipBaker.Bake(animations[i], avatar);
        }
        else if (definition == 1) // CopyFromOtherAvatar (guid in the avatarSourceGuid setting)
        {
            if (ctx.Settings.TryGet("avatarSourceGuid", out var srcTag)
                && System.Guid.TryParse(srcTag.StringValue ?? "", out System.Guid guid)
                && guid != System.Guid.Empty)
            {
                animator.Avatar = new AssetRef<Avatar>(guid);

                // M54 — bake the imported clips against the SOURCE rig's T-pose. Baking against
                // this file's own rest pose is wrong for anim-only FBX (its node defaults are the
                // animation's first frame, so every muscle bakes to ~zero and retargeting pins
                // characters in their bind pose).
                animator.Avatar.EnsureLoaded();
                var sourceAvatar = animator.Avatar.Res;
                if (sourceAvatar != null)
                {
                    for (int i = 0; i < animations.Count; i++)
                        HumanoidClipBaker.Bake(animations[i], sourceAvatar);
                }
                else
                {
                    Debug.LogWarning(
                        $"[ModelImport] '{ctx.AbsolutePath}': source avatar {guid} failed to load; " +
                        "clips left unbaked. If the source model imported after this file, reimport once it exists.");
                }
            }
            else
            {
                Debug.LogWarning($"[ModelImport] '{ctx.AbsolutePath}' asks for a copied avatar but avatarSourceGuid is unset.");
            }
        }
        // 2 = No Avatar: nothing to do.
    }

    /// <summary>
    /// Re-bakes every skinned mesh's bind poses into the RENDERER-ANCHORED frame XEngine's
    /// skinning contract expects: bindPose_i = inverse(rendererW2L * boneWorld_i), so the REST
    /// skin is the identity and raw (authored-space) vertices render through the renderer's own
    /// transform — upright, attached to the renderer node, movable — and animation deforms
    /// relative to that rest. Must run AFTER <see cref="ApplyUnitScale"/>: that pass scales
    /// bind-pose translations and node positions asymmetrically, which would break an
    /// identity-rest invariant if the rebuild happened earlier. Measured failure without this
    /// (Anbi/Corin/Nostradamus v14 imports): rest skin carried a -90°X rotation (skinY=(0,0,-1)),
    /// laying characters flat in edit mode and shredding them into strips under animation.
    /// </summary>
    private static void RebuildBindPosesRendererAnchored(ModelImportResult data)
    {
        if (data.RootGO == null) return;
        foreach (var smr in data.RootGO.GetComponentsInChildren<SkinnedMeshRenderer>(true, true))
        {
            var mesh = smr.SharedMesh.Res;
            var bones = smr.Bones;
            if (mesh == null || bones == null || bones.Length == 0) continue;

            Float4x4 worldToLocal = smr.Transform.WorldToLocalMatrix;
            var rebuilt = new Float4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null)
                    rebuilt[i] = (worldToLocal * WorldOf(bones[i]!)).Invert();
                else
                    rebuilt[i] = Float4x4.Identity;
            }
            mesh.BindPoses = rebuilt;
            mesh.RecalculateBounds();
        }
    }

    /// <summary>World matrix of a transform walking up the (scene-less) import hierarchy.</summary>
    private static Float4x4 WorldOf(XEngine.Vector.Transform t)
    {
        var local = XEngine.Vector.Float4x4.CreateTRS(t.LocalPosition, t.LocalRotation, t.LocalScale);
        return t.Parent != null ? WorldOf(t.Parent) * local : local;
    }

    /// <summary>Apply the numeric user scale after Clay's optional file-unit conversion.
    /// Keep this second multiplication separate to preserve the rounding order of legacy
    /// imports. Scales every distance and positional curve tangent in the baked result.</summary>
    private static void ApplyUnitScale(ModelImportResult data, float scale)
    {
        if (MathF.Abs(scale - 1f) <= 1e-5f) return;

        // 1. Mesh vertices + bind poses (translations only).
        for (int i = 0; i < data.Meshes.Count; i++)
        {
            var mesh = data.Meshes[i];
            var src = mesh.Vertices;
            if (src.Length == 0) continue;
            var scaled = new Float3[src.Length];
            for (int v = 0; v < src.Length; v++)
                scaled[v] = src[v] * scale;
            mesh.Vertices = scaled;
            // Morph position deltas use the same distance units as the base mesh. Direction
            // deltas and frame weights remain unchanged. Reassign to invalidate any morph cache.
            var shapes = mesh.BlendShapes;
            for (int shapeIndex = 0; shapeIndex < shapes.Length; shapeIndex++)
            {
                var frames = shapes[shapeIndex].Frames;
                for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                {
                    var deltas = frames[frameIndex].DeltaVertices;
                    for (int v = 0; v < deltas.Length; v++) deltas[v] *= scale;
                }
            }
            if (shapes.Length > 0) mesh.BlendShapes = shapes;
            if (mesh.BindPoses is { Length: > 0 } poses)
            {
                var scaledPoses = new Float4x4[poses.Length];
                for (int b = 0; b < poses.Length; b++)
                {
                    var m = poses[b];
                    var c3 = new Float4(m.c3.X * scale, m.c3.Y * scale, m.c3.Z * scale, m.c3.W);
                    scaledPoses[b] = new Float4x4(m.c0, m.c1, m.c2, c3);
                }
                mesh.BindPoses = scaledPoses;
            }
            mesh.RecalculateBounds();
        }

        // 2. Skeleton hierarchy node translations must be scaled by the same factor as the
        //    vertices. Without this the bone offsets stay in raw FBX units while the vertices
        //    are scaled — the skeleton collapses, skinned vertices fall back to bind pose,
        //    and characters render frozen in T-pose despite the animation graph running.
        if (data.RootGO != null)
            ScaleHierarchyPositions(data.RootGO.Transform, scale);

        // 3. Animation position curves MUST scale with the node hierarchy: Clay emits curve
        // values in the raw authoring space, which for these FBX is 1/100 of the scaled node
        // space. Without this, animated bones sit at ~1/100 their bind positions (Head at
        // y=0.01 instead of y=1.0) and skinned meshes collapse. Measured empirically on the
        // ZZZ character FBX (unitScale=100): fresh unscaled clip → Head y=0.01; scaled → y=1.04.
        for (int i = 0; i < data.Animations.Count; i++)
            ScaleAnimationPositionCurves(data.Animations[i], scale);

        var size = data.Meshes.Count > 0 ? data.Meshes[0].bounds.Size : new Float3(0f, 0f, 0f);
        Debug.Log($"[ModelImport] applied unitScale={scale} firstMeshSize={size}");
    }

    private static void ScaleHierarchyPositions(XEngine.Vector.Transform t, float scale)
    {
        t.LocalPosition = t.LocalPosition * scale;
        for (int i = 0; i < t.ChildCount; i++)
            ScaleHierarchyPositions(t.GetChild(i), scale);
    }

    private static void ScaleAnimationPositionCurves(AnimationClip clip, float scale)
    {
        foreach (var bone in clip.Bones)
        {
            if (bone.HasSourceReferencePose)
                bone.SourcePosition *= scale;
            bone.PosX = ScaleCurveValues(bone.PosX, scale);
            bone.PosY = ScaleCurveValues(bone.PosY, scale);
            bone.PosZ = ScaleCurveValues(bone.PosZ, scale);
        }
    }

    private static AnimationCurve? ScaleCurveValues(AnimationCurve? curve, float scale)
    {
        if (curve == null) return curve;
        var keys = curve.Keys;
        for (int k = 0; k < keys.Count; k++)
        {
            var key = keys[k];
            key.Value = key.Value * scale;
            key.TangentIn *= scale;
            key.TangentOut *= scale;
            keys[k] = key;
        }
        return curve;
    }

    /// <summary>
    /// Ripped FBX files (Claymore) often reference Original\TGA\*.tga that were never copied.
    /// The copier lands Unity .mat + PNG under Texture/Materials next to the FBX; bind those
    /// so instantiated models get a real _MainTex instead of the Grid fallback.
    /// </summary>
    private static Guid ResolveSiblingUnityMaterial(ImportContext ctx)
    {
        string? dir = Path.GetDirectoryName(ctx.AbsolutePath);
        if (string.IsNullOrEmpty(dir)) return Guid.Empty;
        string matDir = Path.Combine(dir, "Texture", "Materials");
        if (!Directory.Exists(matDir)) return Guid.Empty;

        var db = EditorAssetBackend.Instance;
        string? assetsRoot = Project.Current?.AssetsPath;
        if (db == null || string.IsNullOrEmpty(assetsRoot)) return Guid.Empty;

        Guid matGuid = Guid.Empty;
        foreach (string matFile in Directory.GetFiles(matDir, "*.mat"))
        {
            string relative = EditorAssetBackend.NormalizePath(
                Path.GetRelativePath(Path.GetFullPath(assetsRoot), Path.GetFullPath(matFile)));
            AssetEntry? entry = db.GetEntry(relative);
            if (entry == null) continue;
            ctx.AddDependency(entry.Guid, "Material");
            if (matGuid == Guid.Empty)
                matGuid = entry.Guid;
        }
        if (matGuid != Guid.Empty)
            Debug.Log($"[ModelImport] '{Path.GetFileName(ctx.AbsolutePath)}' found sibling Unity material default {matGuid}");
        return matGuid;
    }

    public override EchoObject? DefaultSettings()
    {
        var s = EchoObject.NewCompound();
        s["generateNormals"] = new EchoObject(true);
        s["generateSmoothNormals"] = new EchoObject(true);
        s["recalculateNormals"] = new EchoObject(false);
        s["calculateTangents"] = new EchoObject(true);
        s["flipUVs"] = new EchoObject(true);
        s["unitScale"] = new EchoObject(1.0f);
        s["convertFileUnits"] = new EchoObject(true);
        s["generateLightmapUVs"] = new EchoObject(false);
        s["importAnimation"] = new EchoObject(true);
        s["resampleCurves"] = new EchoObject(true);
        s["animationType"] = new EchoObject(0);
        s["avatarDefinition"] = new EchoObject(2); // No Avatar
        s["skinBoneCount"] = new EchoObject(4);
        s["stripBones"] = new EchoObject(true);
        MeshFeatureRegistry.PopulateDefaultSettings(s);
        return s;
    }
}

/// <summary>
/// The editor's <see cref="IModelTextureResolver"/>: never decodes another asset's pixel data.
/// An externally referenced texture is resolved purely by path, against the asset database's
/// existing GUID for that file. An embedded texture is registered as a proper sub-asset of the
/// model for the asset database to own and cache (this is the one case that still has to decode -
/// there's no separate file for the asset database to already know about).
/// </summary>
internal sealed class EditorModelTextureResolver : IModelTextureResolver
{
    private readonly ImportContext _ctx;
    private readonly string _assetsRoot;
    private readonly EditorAssetBackend? _db;

    public EditorModelTextureResolver(ImportContext ctx)
    {
        _ctx = ctx;
        // Project.AssetsPath is a plain Path.Combine(RootPath, "Assets"), not run through
        // GetFullPath - sourcePath (below) comes from Clay's own, separately-normalized
        // Path.GetFullPath pipeline. Normalizing both through GetFullPath here means a prefix
        // comparison between them is comparing like with like, even if RootPath itself has a
        // trailing separator or other cosmetic difference GetFullPath would otherwise collapse.
        string assetsRoot = Project.Current?.AssetsPath ?? "";
        _assetsRoot = string.IsNullOrEmpty(assetsRoot) ? "" : Path.GetFullPath(assetsRoot);
        _db = EditorAssetBackend.Instance;
    }

    public AssetRef<Texture2D> ResolveExternal(string sourcePath)
    {
        if (_db == null || string.IsNullOrEmpty(_assetsRoot)) return default;

        // sourcePath is always already a resolved, existing, absolute path (guaranteed by Clay's
        // Texture.SourcePath contract) - a plain prefix check + relative-path computation is enough,
        // no need to re-resolve it against the model's own directory.
        if (!sourcePath.StartsWith(_assetsRoot, StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning($"[Clay] External texture '{sourcePath}' is not under the project's " +
                $"Assets folder '{_assetsRoot}' - using the default fallback texture instead.");
            return default;
        }

        string relativePath = Path.GetRelativePath(_assetsRoot, sourcePath).Replace('\\', '/');
        var entry = _db.GetEntry(relativePath);
        if (entry == null)
        {
            Debug.LogWarning($"[Clay] External texture '{sourcePath}' (resolved to '{relativePath}') has no " +
                "tracked asset entry - using the default fallback texture instead. Has it been imported yet?");
            return default;
        }

        _ctx.AddDependency(entry.Guid);
        return new AssetRef<Texture2D>(entry.Guid);
    }

    public AssetRef<Texture2D> ResolveEmbedded(string? name, byte[] encodedBytes, string? mimeType)
    {
        try
        {
            using var ms = new MemoryStream(encodedBytes);
            var tex = Texture2D.LoadFromStream(ms, generateMipmaps: true);
            tex.Name = string.IsNullOrEmpty(name) ? "EmbeddedTexture" : name;
            _ctx.AddSubAsset(tex.Name, tex); // assigns tex.AssetID
            return new AssetRef<Texture2D>(tex);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Clay] Failed to load embedded texture '{name ?? "(unnamed)"}': {ex.Message}");
            return default;
        }
    }
}
