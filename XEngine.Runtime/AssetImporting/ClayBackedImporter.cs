// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Clay;
using XEngine.Clay.Importer;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using ClayMesh = XEngine.Clay.Mesh;
using ClayMaterial = XEngine.Clay.Material;
using ClayAnim = XEngine.Clay.AnimationClip;
using ClayBinding = XEngine.Clay.AnimationBinding;
using ClayCurve = XEngine.Vector.AnimationCurve;
using ClayTexture = XEngine.Clay.Texture;
using ClaySettings = XEngine.Clay.Importer.ModelImporterSettings;
using PMesh = XEngine.Runtime.Resources.Mesh;
using PMaterial = XEngine.Runtime.Resources.Material;
using PAnim = XEngine.Runtime.AnimationClip;
using PCurve = XEngine.Runtime.AnimationCurve;
using PBlendShape = XEngine.Runtime.Resources.BlendShape;
using PBlendShapeFrame = XEngine.Runtime.Resources.BlendShapeFrame;

namespace XEngine.Runtime.AssetImporting;

/// <summary>
/// Bakes a <see cref="XEngine.Clay.Model"/> into a XEngine <see cref="ModelImportResult"/>:
/// runtime <see cref="PMesh"/>es, <see cref="PMaterial"/>s wired to the Standard shader, a
/// <see cref="GameObject"/> hierarchy with MeshRenderer / SkinnedMeshRenderer components, and
/// <see cref="PAnim"/>s with per-axis curves.
/// </summary>
internal static class ClayBackedImporter
{
    public static ModelImportResult Import(FileInfo assetPath, ModelImporterSettings settings)
    {
        string ext = assetPath.Extension.ToLowerInvariant();
        var clayModel = Clay.Importer.ModelImporter.Load(assetPath.FullName, MapSettings(settings, ext));
        return Bake(clayModel, Path.GetFileNameWithoutExtension(assetPath.Name), settings);
    }

    public static ModelImportResult Import(Stream stream, string virtualPath, ModelImporterSettings settings)
    {
        string ext = Path.GetExtension(virtualPath).ToLowerInvariant();
        string format = ext switch
        {
            ".gltf" => "gltf",
            ".glb" => "glb",
            ".vrm" => "vrm",
            ".obj" => "obj",
            ".fbx" => "fbx",
            _ => throw new NotSupportedException($"Unsupported model format: {ext}"),
        };
        var clayModel = Clay.Importer.ModelImporter.Load(stream, format, MapSettings(settings, ext));
        return Bake(clayModel, Path.GetFileNameWithoutExtension(virtualPath), settings);
    }

    // ----------------------------------------------------------------------------------------
    // Settings translation
    // ----------------------------------------------------------------------------------------

    private static ClaySettings MapSettings(ModelImporterSettings s, string fileExt)
    {
        // Start from Clay's GameQuality preset (triangulate, dedup, tangents, bone-weight limit,
        // populate skeletons, bounds, RH->LH coord convert, sort by topology, etc.) then layer the
        // XEngine-specific toggles on top.
        var flags = PostProcessPresets.GameQuality;

        // XEngine does its own GenerateNormals/RecalculateNormals at the runtime mesh layer (via
        // PMesh.RecalculateNormals after bake), so we drop CalcTangentSpace + smooth normals from
        // the Clay pipeline and let XEngine decide afterwards. This matches the existing
        // GltfImporter / ObjImporter behavior.
        flags &= ~PostProcessFlags.CalcTangentSpace;
        flags &= ~PostProcessFlags.GenerateSmoothNormals;
        flags &= ~PostProcessFlags.GenerateNormals;

        // FlipUVs: glTF stores UVs with V=0 at the top of the texture (top-left origin convention).
        // FBX and OBJ store V=0 at the bottom (OpenGL convention) which is what XEngine's shaders
        // expect post-import. So only flip when the source is glTF/GLB/VRM - flipping FBX or OBJ
        // would invert UVs and ship textures upside down.
        bool sourceNeedsFlip = fileExt is ".gltf" or ".glb" or ".vrm";
        if (s.FlipUVs && sourceNeedsFlip) flags |= PostProcessFlags.FlipUVs;
        else flags &= ~PostProcessFlags.FlipUVs;

        // Clay GlobalScale is composed with FBX SourceUnitToMeters (cm→m). Unity's
        // ModelImporter.globalScale is applied *after* that conversion, so we keep Clay
        // at 1 here and multiply by UnitScale on the baked meshes/nodes/clips.
        return new ClaySettings
        {
            PostProcess = flags,
            GlobalScale = 1f,
            ConvertFileUnits = s.ConvertFileUnits,
            BoneWeightLimit = s.SkinBoneCount is 1 or 2 or 4 ? s.SkinBoneCount : 4,
        };
    }

    // ----------------------------------------------------------------------------------------
    // Bake: Clay model -> XEngine ModelImportResult
    // ----------------------------------------------------------------------------------------

    private static ModelImportResult Bake(Clay.Model clayModel, string modelName, ModelImporterSettings settings)
    {
        if (clayModel.Log.Entries.Count > 0)
        {
            foreach (var entry in clayModel.Log.Entries)
            {
                if (entry.Severity == ImportLogSeverity.Warning)
                    Debug.LogWarning($"[Clay] {entry}");
                else if (entry.Severity == ImportLogSeverity.Error)
                    Debug.LogError($"[Clay] {entry}");
            }
        }

        // 1. Textures - resolve once, share by Clay texture index. Resolution (not necessarily
        // decoding - see IModelTextureResolver) happens through the resolver, so the editor can
        // supply one that only ever produces AssetRefs and never touches pixel data itself.
        var resolver = settings.TextureResolver ?? DefaultModelTextureResolver.Instance;
        var textureCache = new AssetRef<Texture2D>[clayModel.Textures.Count];
        for (int i = 0; i < clayModel.Textures.Count; i++)
            textureCache[i] = ResolveModelTexture(clayModel.Textures[i], resolver);

        // 2. Materials.
        var materials = new List<PMaterial>(clayModel.Materials.Count);
        for (int i = 0; i < clayModel.Materials.Count; i++)
            materials.Add(BuildMaterial(clayModel.Materials[i], textureCache));

        // 3. Meshes (with per-submesh material index propagated).
        var meshes = new List<PMesh>(clayModel.Meshes.Count);
        var meshSubmeshMaterials = new List<int[]>(clayModel.Meshes.Count);
        for (int i = 0; i < clayModel.Meshes.Count; i++)
        {
            (PMesh pmesh, int[] submeshMatIndices) = BuildMesh(clayModel.Meshes[i], settings);
            meshes.Add(pmesh);
            meshSubmeshMaterials.Add(submeshMatIndices);
        }

        // 4. Build GameObject hierarchy. Index matches clayModel.Nodes.Index.
        var nodeGOs = new GameObject[clayModel.Nodes.Count];
        for (int i = 0; i < clayModel.Nodes.Count; i++)
        {
            var n = clayModel.Nodes[i];
            var go = new GameObject(string.IsNullOrEmpty(n.Name) ? $"Node_{i}" : n.Name);
            nodeGOs[i] = go;
            go.Transform.LocalPosition = n.LocalPosition;
            go.Transform.LocalRotation = n.LocalRotation;
            go.Transform.LocalScale = n.LocalScale;
        }
        // Parenting pass (after all GOs exist so SetParent can find the parent).
        for (int i = 0; i < clayModel.Nodes.Count; i++)
        {
            var parent = clayModel.Nodes[i].Parent;
            if (parent is null) continue;
            nodeGOs[i].SetParent(nodeGOs[parent.Index], worldPositionStays: false);
        }
        // Rename the model root.
        nodeGOs[clayModel.Root.Index].Name = string.IsNullOrEmpty(modelName) ? "Model" : modelName;
        var rootGO = nodeGOs[clayModel.Root.Index];

        // Clay often leaves mesh names empty, so EngineObject defaults them to "NewMesh".
        // Unity ripped prefabs name each SkinnedMeshRenderer GameObject after its mesh
        // (Anbi_Hair, Corin_face, NuoCha_Body, …). Stamp that scene-node name onto
        // generic meshes before they are registered as sub-assets — otherwise
        // UnityPrefabImporter's name match misses and every renderer collapses onto
        // the first mesh (every character looks like the same dummy).
        for (int i = 0; i < clayModel.Nodes.Count; i++)
        {
            int meshIndex = clayModel.Nodes[i].MeshIndex;
            if (meshIndex < 0 || meshIndex >= meshes.Count) continue;
            if (IsGenericMeshName(meshes[meshIndex].Name))
                meshes[meshIndex].Name = nodeGOs[i].Name;
        }

        // 5. Renderers + skin wiring.
        for (int i = 0; i < clayModel.Nodes.Count; i++)
        {
            var n = clayModel.Nodes[i];
            if (n.MeshIndex < 0) continue;
            var go = nodeGOs[i];
            var mesh = meshes[n.MeshIndex];
            var matRefs = BuildMatRefs(meshSubmeshMaterials[n.MeshIndex], materials);

            if (n.SkinIndex >= 0)
            {
                var clayskin = clayModel.Skins[n.SkinIndex];
                // Mirror Clay.Skin -> XEngine Mesh.BoneNames (relative paths).
                // No LINQ (PR0001): foreach into a List, then native ToArray (List has its own).
                mesh.BoneNames = new string[clayskin.BoneNodeIndices.Length];
                var boneTransforms = new Transform[clayskin.BoneNodeIndices.Length];
                for (int b = 0; b < clayskin.BoneNodeIndices.Length; b++)
                {
                    int boneNodeIdx = clayskin.BoneNodeIndices[b];
                    boneTransforms[b] = nodeGOs[boneNodeIdx].Transform;
                    mesh.BoneNames[b] = Transform.GetRelativePath(boneTransforms[b], rootGO.Transform);
                }

                // Rebuild bind poses in the RENDERER-ANCHORED frame XEngine's skinning contract
                // expects: bindPose_i = inverse(rendererW2L * boneWorld_i), so the REST skin is
                // the identity and raw (authored-space) vertices render through the renderer's
                // own transform — upright, attached to the renderer node, movable. Animation
                // then deforms relative to that rest. The Clay bind matrices are world-anchored
                // (or Maya-pose rebased): with them, model-space vertex meshes (Anbi/Corin/
                // Nostradamus bodies) render at their raw import placement and tear into strips
                // the moment animation moves any bone, while packed-vertex meshes (Claymore)
                // happen to work — which is why only some characters were affected.
                Float4x4 rendererW2L = go.Transform.WorldToLocalMatrix;
                var bindPoseList = new List<Float4x4>(boneTransforms.Length);
                foreach (var bone in boneTransforms)
                    bindPoseList.Add((rendererW2L * bone.LocalToWorldMatrix).Invert());
                mesh.BindPoses = bindPoseList.ToArray();

                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.SharedMesh = new AssetRef<PMesh>(mesh);
                smr.Materials = matRefs;
                Transform? rootBoneTransform = clayskin.RootNodeIndex >= 0
                    ? nodeGOs[clayskin.RootNodeIndex].Transform
                    : (boneTransforms.Length > 0 ? boneTransforms[0] : null);
                smr.SetBones(boneTransforms, rootBoneTransform);
            }
            else if (mesh.HasBlendShapes)
            {
                // Morph-only mesh (no skin): a SkinnedMeshRenderer still owns the blend-shape
                // weights. No bones to wire skinning stays disabled in-shader.
                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.SharedMesh = new AssetRef<PMesh>(mesh);
                smr.Materials = matRefs;
            }
            else
            {
                var mr = go.AddComponent<MeshRenderer>();
                mr.Mesh = new AssetRef<PMesh>(mesh);
                mr.Materials = matRefs;
            }
        }

        // 6. Animations.
        var animations = new List<PAnim>(clayModel.AnimationClips.Count);
        foreach (var clip in clayModel.AnimationClips)
            animations.Add(BuildAnimationClip(clip, clayModel, nodeGOs, rootGO));

        ModelRigBinding.Attach(rootGO, animations, settings.AnimationType);

        var result = new ModelImportResult
        {
            RootGO = rootGO,
            Meshes = meshes,
            Materials = materials,
            Animations = animations,
        };
        if (settings.StripBones) ModelSkinPalette.StripUnused(result);
        return result;
    }

    private static bool IsGenericMeshName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.StartsWith("NewMesh", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Length > 5 && name.StartsWith("Mesh_", StringComparison.OrdinalIgnoreCase) && char.IsDigit(name[5]))
            return true;
        return false;
    }

    private static List<AssetRef<PMaterial>> BuildMatRefs(int[] submeshMatIndices, List<PMaterial> materials)
    {
        var matRefs = new List<AssetRef<PMaterial>>(submeshMatIndices.Length);
        for (int s = 0; s < submeshMatIndices.Length; s++)
        {
            int idx = submeshMatIndices[s];
            matRefs.Add(idx >= 0 && idx < materials.Count
                ? new AssetRef<PMaterial>(materials[idx])
                : default);
        }
        return matRefs;
    }

    // ----------------------------------------------------------------------------------------
    // Mesh bake
    // ----------------------------------------------------------------------------------------

    private static (PMesh mesh, int[] submeshMaterials) BuildMesh(ClayMesh src, ModelImporterSettings settings)
    {
        var dst = new PMesh
        {
            Name = src.Name,
            MeshTopology = Topology.Triangles,
            // IndexFormat must be assigned BEFORE Indices below: XEngine's IndexFormat setter wipes
            // the index buffer as a side effect, so setting it after Indices would leave us with
            // a mesh that fails Upload() with "Mesh has no indices".
            IndexFormat = src.Has32BitIndices ? IndexFormat.UInt32 : IndexFormat.UInt16,
        };

        if (src.VertexCount == 0)
            return (dst, Array.Empty<int>());

        dst.Vertices = src.Vertices;
        if (src.Normals is not null) dst.Normals = src.Normals;
        if (src.Tangents is not null) dst.Tangents = src.Tangents;
        if (src.Colors is not null) dst.Colors = src.Colors;
        if (src.UVs.Length > 0 && src.UVs[0] is not null) dst.UV = src.UVs[0];
        if (src.UVs.Length > 1 && src.UVs[1] is not null) dst.UV2 = src.UVs[1];

        // Clay's BoneWeight (struct with 4 indices + 4 weights) -> XEngine's parallel Float4 arrays.
        // XEngine's skinning shader uses 1-based bone indices with 0 reserved as "no bone": every
        // shader iteration checks "boneIndex > 0" before fetching boneMatrix[boneIndex - 1].
        // Shift Clay's 0-based glTF joint indices by +1, and zero out any slot whose weight is
        // 0 so the shader's no-bone branch fires (rather than uselessly fetching bone[0]).
        if (src.BoneWeights is not null)
        {
            var indices4 = new Float4[src.BoneWeights.Length];
            var weights4 = new Float4[src.BoneWeights.Length];
            for (int v = 0; v < src.BoneWeights.Length; v++)
            {
                var bw = src.BoneWeights[v];
                indices4[v] = new Float4(
                    bw.Weight0 > 0f ? bw.Index0 + 1 : 0,
                    bw.Weight1 > 0f ? bw.Index1 + 1 : 0,
                    bw.Weight2 > 0f ? bw.Index2 + 1 : 0,
                    bw.Weight3 > 0f ? bw.Index3 + 1 : 0);
                weights4[v] = new Float4(bw.Weight0, bw.Weight1, bw.Weight2, bw.Weight3);
            }
            dst.BoneIndices = indices4;
            dst.BoneWeights = weights4;
        }

        // Blend shapes (morph targets). Clay already expands sparse deltas to full vertex count and
        // remaps them through vertex dedup, so the delta arrays line up 1:1 with dst.Vertices.
        if (src.BlendShapes is { Length: > 0 })
        {
            var shapes = new PBlendShape[src.BlendShapes.Length];
            for (int i = 0; i < src.BlendShapes.Length; i++)
            {
                var cb = src.BlendShapes[i];
                var frames = new PBlendShapeFrame[cb.Frames.Length];
                for (int f = 0; f < cb.Frames.Length; f++)
                {
                    var cf = cb.Frames[f];
                    frames[f] = new PBlendShapeFrame
                    {
                        Weight = cf.Weight,
                        DeltaVertices = cf.DeltaVertices,
                        DeltaNormals = cf.DeltaNormals,
                        DeltaTangents = cf.DeltaTangents,
                    };
                }
                shapes[i] = new PBlendShape { Name = cb.Name ?? $"BlendShape{i}", Frames = frames };
            }
            dst.BlendShapes = shapes;
        }

        dst.Indices = src.Indices;

        var submeshMatIndices = new int[src.SubMeshes.Length];
        if (src.SubMeshes.Length > 1)
        {
            dst.SetSubMeshCount(src.SubMeshes.Length);
            for (int s = 0; s < src.SubMeshes.Length; s++)
            {
                var sm = src.SubMeshes[s];
                dst.SetSubMesh(s, new SubMeshDescriptor(sm.IndexStart, sm.IndexCount, MapTopology(sm.Topology)));
                submeshMatIndices[s] = sm.MaterialIndex;
            }
        }
        else if (src.SubMeshes.Length == 1)
        {
            submeshMatIndices[0] = src.SubMeshes[0].MaterialIndex;
        }

        // Normal / tangent recalculation pass (matching the existing importers' policy).
        bool hadNormals = dst.HasNormals;
        if (settings.RecalculateNormals || (!hadNormals && settings.GenerateNormals))
        {
            if (settings.GenerateSmoothNormals || settings.RecalculateNormals)
                dst.RecalculateNormals();
            else
                GenerateFlatNormals(dst);
        }

        if (settings.CalculateTangentSpace && dst.HasNormals && dst.HasUV)
            dst.RecalculateTangents();

        dst.RecalculateBounds();
        return (dst, submeshMatIndices);
    }

    private static void GenerateFlatNormals(PMesh mesh)
    {
        var normals = new Float3[mesh.Vertices.Length];
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int i0 = (int)mesh.Indices[i], i1 = (int)mesh.Indices[i + 1], i2 = (int)mesh.Indices[i + 2];
            var e1 = mesh.Vertices[i1] - mesh.Vertices[i0];
            var e2 = mesh.Vertices[i2] - mesh.Vertices[i0];
            var fn = Float3.Cross(e1, e2);
            float lenSq = Float3.Dot(fn, fn);
            fn = lenSq > 1e-8f ? fn / MathF.Sqrt(lenSq) : Float3.UnitY;
            normals[i0] = fn; normals[i1] = fn; normals[i2] = fn;
        }
        mesh.Normals = normals;
    }

    private static Topology MapTopology(PrimitiveTopology t) => t switch
    {
        PrimitiveTopology.Triangles => Topology.Triangles,
        PrimitiveTopology.Lines => Topology.Lines,
        PrimitiveTopology.Points => Topology.Points,
        _ => Topology.Triangles,
    };

    // ----------------------------------------------------------------------------------------
    // Material bake
    // ----------------------------------------------------------------------------------------

    private static PMaterial BuildMaterial(ClayMaterial src, AssetRef<Texture2D>[] textureCache)
    {
        var mat = new PMaterial(Shader.LoadDefault(DefaultShader.Standard))
        {
            Name = string.IsNullOrEmpty(src.Name) ? "Material" : src.Name,
        };

        mat.SetColor("_MainColor", src.BaseColor);

        mat.SetTexture("_MainTex", OrDefault(ResolveTexture(src.BaseColorTexture, textureCache), DefaultTexture.Grid));
        mat.SetTexture("_NormalTex", OrDefault(ResolveTexture(src.NormalTexture, textureCache), DefaultTexture.Normal));

        mat.SetFloat("_Metallic", src.Metallic);
        mat.SetFloat("_Roughness", src.Roughness);
        mat.SetTexture("_SurfaceTex", OrDefault(ResolveTexture(src.MetallicRoughnessTexture, textureCache), DefaultTexture.Surface));

        mat.SetTexture("_EmissionTex", OrDefault(ResolveTexture(src.EmissiveTexture, textureCache), DefaultTexture.Emission));

        // EmissiveFactor is already linear-RGB in Clay; EmissiveStrength multiplies it.
        var e = src.EmissiveFactor;
        var emissiveColor = new Color(e.R * src.EmissiveStrength, e.G * src.EmissiveStrength, e.B * src.EmissiveStrength, 1f);
        mat.SetColor("_EmissiveColor", emissiveColor);
        float maxE = MathF.Max(emissiveColor.R, MathF.Max(emissiveColor.G, emissiveColor.B));
        mat.SetFloat("_EmissionIntensity", maxE > 0f ? 1f : 0f);

        return mat;
    }

    private static AssetRef<Texture2D> ResolveTexture(MaterialTextureSlot? slot, AssetRef<Texture2D>[] cache)
    {
        if (slot is null) return default;
        int idx = slot.TextureIndex;
        if ((uint)idx >= (uint)cache.Length) return default;
        return cache[idx];
    }

    // "No texture in this slot" is never resolved through IModelTextureResolver - it isn't a texture
    // reference to look up, it's the absence of one. Falls back to the shared, GUID-tagged default
    // texture singletons (same as always).
    private static AssetRef<Texture2D> OrDefault(AssetRef<Texture2D> tex, DefaultTexture fallback) =>
        tex.IsExplicitNull ? new AssetRef<Texture2D>(Texture2D.LoadDefault(fallback)) : tex;

    private static AssetRef<Texture2D> ResolveModelTexture(ClayTexture src, IModelTextureResolver resolver)
    {
        try
        {
            if (!string.IsNullOrEmpty(src.SourcePath) && File.Exists(src.SourcePath))
                return resolver.ResolveExternal(src.SourcePath);
            if (src.EncodedBytes is { Length: > 0 } bytes)
                return resolver.ResolveEmbedded(src.Name, bytes, src.MimeType);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Clay] Failed to resolve texture '{src.Name ?? src.SourcePath ?? "(embedded)"}': {ex.Message}");
        }
        return default;
    }

    // ----------------------------------------------------------------------------------------
    // Animation bake
    // ----------------------------------------------------------------------------------------

    private static PAnim BuildAnimationClip(ClayAnim src, Clay.Model clayModel, GameObject[] nodeGOs, GameObject rootGO)
    {
        var clip = new PAnim
        {
            Name = src.Name,
            Duration = src.Duration,
            DurationInTicks = src.Duration,
            TicksPerSecond = 1f,
            Wrap = AnimationWrapMode.Loop,
        };

        // Bin bindings by target node -> AnimBone. Blend-shape weight channels are handled
        // separately (they target a renderer + named shape, not a bone transform).
        var boneByNode = new Dictionary<int, PAnim.AnimBone>();
        foreach (var b in src.Bindings)
        {
            if (b.NodeIndex < 0 || b.NodeIndex >= nodeGOs.Length) continue;

            if (b.Property == AnimatedProperty.BlendShapeWeight)
            {
                ApplyBlendShapeBinding(b, clayModel, nodeGOs, rootGO, clip);
                continue;
            }

            var targetGO = nodeGOs[b.NodeIndex];
            string bonePath = Transform.GetRelativePath(targetGO.Transform, rootGO.Transform);
            if (!boneByNode.TryGetValue(b.NodeIndex, out var bone))
            {
                Transform sourceTransform = targetGO.Transform;
                bone = new PAnim.AnimBone
                {
                    BoneName = bonePath,
                    HasSourceReferencePose = true,
                    SourcePosition = sourceTransform.LocalPosition,
                    SourceRotation = sourceTransform.LocalRotation,
                    SourceScale = sourceTransform.LocalScale,
                };
                boneByNode[b.NodeIndex] = bone;
            }
            ApplyBinding(b, bone);
        }

        // P/R/S backfill is now done at Clay's SceneBaker so every consumer gets complete
        // 9-channel-per-bone clips. Any (NodeIndex, Property) tuple still null here would be
        // a Clay-side regression.

        // Retargeting a child needs the source/destination frame correction of its immediate
        // parent even when that parent has no authored animation channels. Preserve those
        // reference-only ancestors in the clip so AnimationClipPlayable can build an unbroken
        // top-down basis chain. They intentionally carry no curves: sampling must not overwrite
        // gameplay-authored transforms on otherwise unanimated bones.
        var animatedNodes = new List<int>(boneByNode.Count);
        foreach (int nodeIndex in boneByNode.Keys)
            animatedNodes.Add(nodeIndex);
        for (int i = 0; i < animatedNodes.Count; i++)
        {
            Clay.ModelNode? parent = clayModel.Nodes[animatedNodes[i]].Parent;
            while (parent != null && parent.Index != clayModel.Root.Index)
            {
                if (!boneByNode.ContainsKey(parent.Index))
                {
                    Transform sourceTransform = nodeGOs[parent.Index].Transform;
                    boneByNode[parent.Index] = new PAnim.AnimBone
                    {
                        BoneName = Transform.GetRelativePath(sourceTransform, rootGO.Transform),
                        HasSourceReferencePose = true,
                        SourcePosition = sourceTransform.LocalPosition,
                        SourceRotation = sourceTransform.LocalRotation,
                        SourceScale = sourceTransform.LocalScale,
                    };
                }
                parent = parent.Parent;
            }
        }

        foreach (var bone in boneByNode.Values)
            clip.AddBone(bone);

        clip.EnsureQuaternionContinuity();
        return clip;
    }

    private static void ApplyBinding(ClayBinding binding, PAnim.AnimBone bone)
    {
        var curve = binding.Curve;
        switch (binding.Property)
        {
            case AnimatedProperty.Position:
                bone.PosX = SampleComponent(curve, component: 0);
                bone.PosY = SampleComponent(curve, component: 1);
                bone.PosZ = SampleComponent(curve, component: 2);
                break;
            case AnimatedProperty.Rotation:
                bone.RotX = SampleComponent(curve, component: 0);
                bone.RotY = SampleComponent(curve, component: 1);
                bone.RotZ = SampleComponent(curve, component: 2);
                bone.RotW = SampleComponent(curve, component: 3);
                break;
            case AnimatedProperty.Scale:
                bone.ScaleX = SampleComponent(curve, component: 0);
                bone.ScaleY = SampleComponent(curve, component: 1);
                bone.ScaleZ = SampleComponent(curve, component: 2);
                break;
            // Visibility: not handled by XEngine yet. BlendShapeWeight is handled separately
            // (see ApplyBlendShapeBinding) since it targets a renderer + named shape, not a bone.
        }
    }

    /// <summary>
    /// Converts a Clay BlendShapeWeight binding into a XEngine <see cref="PAnim.BlendShapeAnim"/>.
    /// Resolves the renderer path and the blend-shape name. Clay normalizes weight curves to the
    /// 0-100 scale (matching <c>SetBlendShapeWeight</c> and the frame weights), so no scaling here.
    /// </summary>
    private static void ApplyBlendShapeBinding(ClayBinding binding, Clay.Model clayModel, GameObject[] nodeGOs, GameObject rootGO, PAnim clip)
    {
        var node = clayModel.Nodes[binding.NodeIndex];
        if (node.MeshIndex < 0 || node.MeshIndex >= clayModel.Meshes.Count) return;

        var clayMesh = clayModel.Meshes[node.MeshIndex];
        if (binding.SubIndex < 0 || binding.SubIndex >= clayMesh.BlendShapes.Length) return;

        string shapeName = clayMesh.BlendShapes[binding.SubIndex].Name ?? $"BlendShape{binding.SubIndex}";
        string path = Transform.GetRelativePath(nodeGOs[binding.NodeIndex].Transform, rootGO.Transform);

        clip.AddBlendShape(new PAnim.BlendShapeAnim
        {
            Path = path,
            ShapeName = shapeName,
            Weight = SampleComponent(binding.Curve, 0),
        });
    }

    /// <summary>
    /// Builds a XEngine <see cref="PCurve"/> by sampling one component of a Clay curve at each of its
    /// authored key times. Baked linear/step segments preserve their interpolation; cubic spline
    /// curves retain the existing value-sampling and runtime-smoothing behavior.
    /// </summary>
    private static PCurve SampleComponent(ClayCurve curve, int component)
    {
        int dim = curve.Dimension;
        if (component >= dim)
        {
            // Shouldn't happen if caller and binding agree on dimension.
            return new PCurve(new[] { new KeyFrame(0f, 0f) });
        }

        // Clay v3.3 packs curves as IReadOnlyList<Keyframe> with a Float4 value per key
        // (glTF cubic-spline in/out tangents are baked into InTangent4/OutTangent4; the
        // engine curve keeps only key values, same as the packed-array import it replaces).
        var keys = new List<KeyFrame>(curve.Count);
        foreach (var key in curve)
        {
            float v = component switch
            {
                0 => key.Value4.X,
                1 => key.Value4.Y,
                2 => key.Value4.Z,
                _ => key.Value4.W,
            };
            keys.Add(new KeyFrame(key.Time, v));
        }
        if (keys.Count == 0)
            keys.Add(new KeyFrame(0f, 0f));

        var result = new PCurve(keys);
        // FBX cubic and held segments are already baked by Clay. Smoothing those linear samples
        // again creates overshoot, including a visible pre-swing before a held key jumps.
        ReadOnlySpan<CurveInterpolation> modes = curve.GetInterpolations();
        for (int i = 0; i < modes.Length; i++)
        {
            CurveTangent incoming = i > 0 && modes[i - 1] == CurveInterpolation.Linear
                ? CurveTangent.Linear : CurveTangent.Smooth;
            CurveTangent outgoing = modes[i] == CurveInterpolation.Linear
                ? CurveTangent.Linear : CurveTangent.Smooth;
            result.SmoothTangent(i, incoming, outgoing);
            if (modes[i] == CurveInterpolation.Step)
                result.Keys[i].Continuity = CurveContinuity.Step;
        }
        return result;
    }
}
