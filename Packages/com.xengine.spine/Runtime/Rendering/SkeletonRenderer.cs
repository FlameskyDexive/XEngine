using System.Collections.Generic;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Spine;

/// <summary>Draws a posed Spine skeleton as one or more <see cref="MeshRenderable"/>s.</summary>
[AddComponentMenu("Spine/Skeleton Renderer")]
public class SkeletonRenderer : MonoBehaviour
{
    private const float SortBias = 0.0001f;

    public AssetRef<SkeletonDataAsset> SkeletonDataAsset;
    public Color Color = Color.White;
    public AssetRef<Material> Material;
    public int SortingLayer;
    public int SortingOrder;
    public RenderingLayerMask RenderingLayers = RenderingLayerMask.Everything;

    [System.NonSerialized] protected Skeleton? _skeleton;
    [System.NonSerialized] private readonly SpineMeshGenerator _generator = new();
    [System.NonSerialized] private readonly List<Mesh> _meshes = [];
    [System.NonSerialized] private readonly List<PropertyState> _props = [];
    [System.NonSerialized] private SkeletonDataAsset? _loadedAsset;

    public Skeleton? Skeleton
    {
        get
        {
            EnsureSkeleton();
            return _skeleton;
        }
    }

    public int MeshBatchCount => _generator.BatchCount;

    public Mesh? GetBatchMesh(int index)
        => index >= 0 && index < _meshes.Count ? _meshes[index] : null;

    public virtual void InvalidateSkeleton()
    {
        _skeleton = null;
        _loadedAsset = null;
    }

    /// <summary>Assign a posed skeleton without an imported <see cref="SkeletonDataAsset"/> (tests / tooling).</summary>
    public void SetSkeleton(Skeleton skeleton)
    {
        _skeleton = skeleton;
        _loadedAsset = null;
    }

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        EnsureSkeleton();
        if (_skeleton == null)
            return;

        _skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        _generator.Build(_skeleton, Color);
        int batchCount = _generator.BatchCount;
        if (batchCount == 0)
            return;

        EnsureMeshSlots(batchCount);
        Float4x4 world = Transform.LocalToWorldMatrix;
        if (SortingLayer != 0 || SortingOrder != 0)
            world *= Float4x4.CreateTranslation(new Float3(0f, 0f, (SortingLayer * 1000 + SortingOrder) * SortBias));

        for (int i = 0; i < batchCount; i++)
        {
            SpineMeshBatch batch = _generator.GetBatch(i);
            Mesh mesh = _meshes[i];
            mesh.AssignStreams(batch.Vertices, batch.UVs, batch.Colors, batch.Indices);
            mesh.RecalculateBounds();
            mesh.Upload();

            Material mat = ResolveMaterial(batch.BlendMode);
            PropertyState props = _props[i];
            props.Clear();
            Texture2D? tex = batch.Texture ?? Texture2D.LoadDefault(DefaultTexture.White);
            props.SetTexture("_MainTex", tex);
            renderables.Add(new MeshRenderable(mesh, mat, world, GameObject.LayerIndex, props,
                renderingLayerMask: RenderingLayers.Mask));
        }
    }

    public override void OnDispose()
    {
        for (int i = 0; i < _meshes.Count; i++)
            _meshes[i].Dispose();
        _meshes.Clear();
        _props.Clear();
        _skeleton = null;
    }

    protected void EnsureSkeleton()
    {
        SkeletonDataAsset? asset = SkeletonDataAsset.ResWeak
            ?? AssetDatabase.Get(SkeletonDataAsset.AssetID) as SkeletonDataAsset;
        if (asset == null)
            return;

        if (_skeleton != null && ReferenceEquals(_loadedAsset, asset))
            return;

        _skeleton = new Skeleton(asset.GetSkeletonData());
        // If the atlas was not resident during parsing, retry instead of caching empty attachments.
        if (asset.AtlasAssets.Count > 0 && !HasAnyAttachment(_skeleton))
        {
            asset.ClearCache();
            _skeleton = null;
            return;
        }

        _skeleton.SetupPose();
        _skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        _loadedAsset = asset;
    }

    private static bool HasAnyAttachment(Skeleton skeleton)
    {
        ExposedList<Slot> slots = skeleton.Slots;
        for (int i = 0; i < slots.Count; i++)
            if (slots.Items[i].AppliedPose.Attachment != null)
                return true;
        return false;
    }

    private void EnsureMeshSlots(int count)
    {
        while (_meshes.Count < count)
        {
            _meshes.Add(new Mesh());
            _props.Add(new PropertyState());
        }
    }

    private Material ResolveMaterial(BlendMode blend)
    {
        if (Material.Res != null)
            return Material.Res;
        if (blend == BlendMode.Additive)
            return SpineShaderLibrary.ResolveAdditiveMaterial();
        return UniversalRenderPipeline.ActiveRendererKind == RendererKind.Renderer2D
            ? SpineShaderLibrary.ResolveLitMaterial()
            : SpineShaderLibrary.ResolveSkeletonMaterial();
    }
}
