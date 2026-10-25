using System;
using System.Collections.Generic;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Reuses geometry while supplying a separate bone-palette row and model for each visible
/// instance. Upload and draw share one command stream, so later cascades cannot overwrite
/// an earlier draw's palette. Unsupported per-object state takes the ordinary draw path.
/// </summary>
internal sealed class SkinnedInstanceBatch : IDisposable
{
    private Texture2D? _palette;
    private readonly List<Texture2D> _retiredPalettes = new();
    private Float4x4[] _matrices = [];
    private InstanceData[] _instances = [];
    private readonly InstanceData[][] _instanceSets = new InstanceData[257][];
    private readonly PropertyState _properties = new();
    private readonly BatchRenderable _renderable = new();
    public IRenderable Renderable => _renderable;

    // ★ N7 — per-instance morph weight palette (row = instance, texel = (layerIndex, weight)).
    private Texture2D? _morphPalette;
    private readonly List<Texture2D> _retiredMorphPalettes = new();
    private Float4[] _morphTexels = [];
    private int _morphMaxActive;

    internal static bool SupportsProperties(PropertyState p)
    {
        if (p.GetInt("_GIMode") is not (0 or 3) || p._colors.Count != 1 || !p.HasColor("_MainColor") ||
            p._vectors2.Count != 0 || p._vectors3.Count != 0 || p._vectors4.Count != 0 ||
            p._floats.Count != 0 || p._matrices.Count != 0 || p._matrixArr.Count != 0 ||
            p._buffers.Count != 0 || p._bufferBindings.Count != 0 ||
            p._textures3D.Count != 0 || p._textures2DArray.Count != 0 || p._texturesCube.Count != 0)
            return false;
        // ★ N7 — morph textures ride the batch (the palette itself is replaced per batch).
        foreach (var entry in p._textures)
            if (entry.Key is not ("boneMatrixTexture" or "morphPositionTexture" or "morphNormalTexture"
                or "morphTangentTexture" or "morphWeightTexture")) return false;
        foreach (var entry in p._ints)
            if (entry.Key is not ("_ObjectID" or "_GIMode" or "boneCount" or "_RenderingLayerMask"
                or "morphActiveCount" or "morphTexWidth" or "morphVertexCount"
                or "morphHasNormals" or "morphHasTangents")) return false;
        return true;
    }

    public bool TryPrepare(CommandBuffer cmd, IReadOnlyList<IRenderable> renderables,
        List<int> indices, Material material, Mesh mesh, ViewerData viewer)
    {
        if (indices.Count < 2 || indices.Count > 256) return false;
        if (!mesh.HasBoneIndices || !mesh.HasBoneWeights) return false;
        if (material.Shader != Shader.LoadDefault(DefaultShader.Standard)) return false;
        if (renderables[indices[0]] is not SkinnedMeshRenderable first || first.SkinMatrices is not { Length: > 0 } bones)
            return false;
        int boneCount = bones.Length;
        if (boneCount > 512) return false;
        bool morph = mesh.HasBlendShapes;
        first.GetRenderingData(viewer, out var firstProps, out _, out _, out _);
        if (!SupportsProperties(firstProps)) return false;
        int subMesh = first.GetSubMeshIndex();
        for (int i = 0; i < indices.Count; i++)
        {
            if (renderables[indices[i]] is not SkinnedMeshRenderable skin ||
                skin.SkinMatrices?.Length != boneCount || skin.GetSubMeshIndex() != subMesh ||
                skin.GetRenderingLayerMask() != first.GetRenderingLayerMask()) return false;
            skin.GetRenderingData(viewer, out var props, out _, out _, out _);
            if (!SupportsProperties(props) || props.GetInt("_GIMode") != firstProps.GetInt("_GIMode")) return false;
            // Every batched instance must expose its CPU-side active morph layers so the batch
            // can pack the shared palette; a missing list takes the ordinary draw path.
            if (morph && skin.MorphActiveLayers == null) return false;
        }

        int count = indices.Count;
        _instances = _instanceSets[count] ??= new InstanceData[count];
        if (_matrices.Length < count * boneCount) _matrices = new Float4x4[count * boneCount];
        if (_palette == null || _palette.Width < boneCount * 4 || _palette.Height < count)
        {
            uint width = Math.Max(_palette?.Width ?? 0, (uint)(boneCount * 4));
            uint height = Math.Max(_palette?.Height ?? 0, (uint)count);
            if (_palette != null) _retiredPalettes.Add(_palette);
            _palette = new Texture2D(width, height, false, TextureImageFormat.Float4);
            _palette.SetTextureFilters(TextureMin.Nearest, TextureMag.Nearest);
        }

        // ★ N7 — pack the per-instance morph weight palette (row per instance).
        int morphMaxActive = 0;
        if (morph)
        {
            for (int i = 0; i < count; i++)
            {
                var skin = (SkinnedMeshRenderable)renderables[indices[i]];
                if (skin.MorphActiveLayers!.Count > morphMaxActive)
                    morphMaxActive = skin.MorphActiveLayers.Count;
            }
            if (_morphPalette == null || _morphPalette.Width < morphMaxActive || _morphPalette.Height < count)
            {
                uint width = Math.Max(_morphPalette?.Width ?? 0, (uint)Math.Max(1, morphMaxActive));
                uint height = Math.Max(_morphPalette?.Height ?? 0, (uint)count);
                if (_morphPalette != null) _retiredMorphPalettes.Add(_morphPalette);
                _morphPalette = new Texture2D(width, height, false, TextureImageFormat.Float4);
                _morphPalette.SetTextureFilters(TextureMin.Nearest, TextureMag.Nearest);
            }
            if (_morphTexels.Length < _morphPalette.Width * count)
                _morphTexels = new Float4[_morphPalette.Width * count];
        }

        for (int i = 0; i < count; i++)
        {
            var skin = (SkinnedMeshRenderable)renderables[indices[i]];
            skin.GetRenderingData(viewer, out var props, out _, out var model, out _);
            skin.SkinMatrices!.AsSpan().CopyTo(_matrices.AsSpan(i * boneCount, boneCount));
            if (morph)
            {
                var layers = skin.MorphActiveLayers!;
                for (int k = 0; k < layers.Count; k++)
                    _morphTexels[i * _morphPalette!.Width + k] = layers[k];
                _instances[i] = new InstanceData(model, props.GetColor("_MainColor"), new Float4(i, i, layers.Count, 0));
            }
            else
            {
                _instances[i] = new InstanceData(model, props.GetColor("_MainColor"), new Float4(i, 0, 0, 0));
            }
        }
        _properties.Clear();
        _properties.ApplyOverride(firstProps);
        _properties.SetColor("_MainColor", Color.White);
        _properties.SetTexture("boneMatrixTexture", _palette);
        _properties.SetInt("_ObjectID", 0);
        cmd.UpdateTexture<Float4x4>(_palette, 0, 0, (uint)(boneCount * 4), (uint)count, _matrices.AsSpan(0, count * boneCount));
        if (morph)
        {
            mesh.EnsureMorphTextures();
            if (mesh.MorphPositionTexture == null) return false;
            _properties.SetTexture("morphPositionTexture", mesh.MorphPositionTexture);
            _properties.SetTexture("morphNormalTexture", mesh.MorphNormalTexture ?? mesh.MorphPositionTexture);
            _properties.SetTexture("morphTangentTexture", mesh.MorphTangentTexture ?? mesh.MorphPositionTexture);
            _properties.SetTexture("morphWeightTexture", _morphPalette);
            _properties.SetInt("morphTexWidth", mesh.MorphTexWidth);
            _properties.SetInt("morphVertexCount", mesh.VertexCount);
            _properties.SetInt("morphHasNormals", mesh.MorphHasNormals ? 1 : 0);
            _properties.SetInt("morphHasTangents", mesh.MorphHasTangents ? 1 : 0);
            _properties.SetInt("morphActiveCount", morphMaxActive);
            cmd.UpdateTexture<Float4>(_morphPalette!, 0, 0, (uint)Math.Max(1, morphMaxActive), (uint)count,
                _morphTexels.AsSpan(0, (int)(count * _morphPalette!.Width)));
        }
        _renderable.First = first;
        _renderable.Properties = _properties;
        _renderable.Instances = _instances;
        return true;
    }

    public void Dispose()
    {
        ReleaseRetiredPalettes();
        _palette?.Dispose();
        _palette = null;
        _morphPalette?.Dispose();
        _morphPalette = null;
        _matrices = [];
        _instances = [];
        _morphTexels = [];
        Array.Clear(_instanceSets);
        _properties.Clear();
        _renderable.First = null;
        _renderable.Instances = [];
    }

    public void ReleaseDrawReferences() => _renderable.First = null;

    // Called after camera command buffers are submitted. A growth during recording must
    // not destroy the old texture while an earlier unsubmitted draw still references it.
    public void ReleaseRetiredPalettes()
    {
        foreach (var texture in _retiredPalettes) texture.Dispose();
        _retiredPalettes.Clear();
        foreach (var texture in _retiredMorphPalettes) texture.Dispose();
        _retiredMorphPalettes.Clear();
    }

    private sealed class BatchRenderable : IRenderable
    {
        public SkinnedMeshRenderable? First;
        public PropertyState Properties = null!;
        public InstanceData[] Instances = [];
        public Material GetMaterial() => First!.GetMaterial();
        public int GetLayer() => First!.GetLayer();
        public uint GetRenderingLayerMask() => First!.GetRenderingLayerMask();
        public GameObject? GetOwner() => First!.GetOwner();
        public Float3 GetPosition() => First!.GetPosition();
        public int GetSubMeshIndex() => First!.GetSubMeshIndex();
        public void GetCullingData(out bool isRenderable, out AABB bounds)
            => First!.GetCullingData(out isRenderable, out bounds);
        public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh mesh,
            out Float4x4 model, out InstanceData[]? instanceData)
        {
            First!.GetRenderingData(viewer, out _, out mesh, out _, out _);
            properties = Properties;
            model = Float4x4.Identity;
            instanceData = Instances;
        }
    }
}
