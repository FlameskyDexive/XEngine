using System;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Spine;

/// <summary>CPU mesh for one texture / blend-mode run of a skeleton draw order.</summary>
public struct SpineMeshBatch
{
    public Texture2D? Texture;
    public BlendMode BlendMode;
    public Float3[] Vertices;
    public Float2[] UVs;
    public Color32[] Colors;
    public uint[] Indices;
}

/// <summary>
/// Builds triangle meshes from a posed <see cref="Skeleton"/>. Reuses scratch and per-batch
/// arrays; capacity grows only when the topology needs more vertices.
/// </summary>
public sealed class SpineMeshGenerator
{
    private static readonly int[] QuadTriangles = [0, 1, 2, 2, 3, 0];

    private readonly SkeletonClipping _clipper = new();
    private float[] _world = new float[8];
    private float[] _uvs = new float[8];
    private SpineMeshBatch[] _batches = new SpineMeshBatch[4];
    private int _batchCount;
    private Float3[] _verts = [];
    private Float2[] _uvOut = [];
    private Color32[] _colors = [];
    private uint[] _indices = [];
    private int _vertexCount;
    private int _indexCount;
    private Texture2D? _currentTexture;
    private BlendMode _currentBlend;
    private bool _batchOpen;

    public int BatchCount => _batchCount;

    public SpineMeshBatch GetBatch(int index) => _batches[index];

    public void Build(Skeleton skeleton, Color tint)
    {
        _batchCount = 0;
        _vertexCount = 0;
        _indexCount = 0;
        _batchOpen = false;
        _clipper.ClipEnd();

        ExposedList<Slot> drawOrder = skeleton.DrawOrder.AppliedPose;
        Slot[] slots = drawOrder.Items;
        int slotCount = drawOrder.Count;
        for (int i = 0; i < slotCount; i++)
        {
            Slot slot = slots[i];
            SlotPose slotPose = slot.AppliedPose;
            if (!slot.Bone.Active)
            {
                _clipper.ClipEnd(slot);
                continue;
            }

            Attachment? attachment = slotPose.Attachment;
            if (attachment is ClippingAttachment clip)
            {
                _clipper.ClipStart(skeleton, slot, clip);
                continue;
            }

            if (attachment is RegionAttachment region)
                AppendRegion(skeleton, slot, region, tint);
            else if (attachment is MeshAttachment mesh)
                AppendMesh(skeleton, slot, mesh, tint);

            _clipper.ClipEnd(slot);
        }

        _clipper.ClipEnd();
        Flush();
    }

    private void AppendRegion(Skeleton skeleton, Slot slot, RegionAttachment region, Color tint)
    {
        Sequence sequence = region.Sequence;
        int sequenceIndex = sequence.ResolveIndex(slot.AppliedPose);
        EnsureWorld(8);
        region.ComputeWorldVertices(slot, sequence.GetOffsets(sequenceIndex), _world, 0);
        // RegionAttachment offsets and sequence UVs share the BL,UL,UR,BR index order, and
        // ComputeWorldVertices writes world verts in that same order — a 1:1 copy keeps
        // each vertex paired with its own UV. (An earlier reorder rotated region quads'
        // texture content, displacing small region attachments like head/feet parts.)
        Color32F skeletonColor = skeleton.GetColor();
        Color32F slotColor = slot.AppliedPose.GetColor();
        Color32F regionColor = region.GetColor();
        Color32 color = Premultiply(
            skeletonColor.r * slotColor.r * regionColor.r * tint.R,
            skeletonColor.g * slotColor.g * regionColor.g * tint.G,
            skeletonColor.b * slotColor.b * regionColor.b * tint.B,
            skeletonColor.a * slotColor.a * regionColor.a * tint.A);
        AppendVertices(slot, sequence.GetRegion(sequenceIndex), 8, QuadTriangles, QuadTriangles.Length, color, sequence.GetUVs(sequenceIndex));
    }

    private void AppendMesh(Skeleton skeleton, Slot slot, MeshAttachment mesh, Color tint)
    {
        int worldLength = mesh.WorldVerticesLength;
        EnsureWorld(worldLength);
        mesh.ComputeWorldVertices(skeleton, slot, 0, worldLength, _world, 0);
        Sequence sequence = mesh.Sequence;
        int sequenceIndex = sequence.ResolveIndex(slot.AppliedPose);
        Color32F skeletonColor = skeleton.GetColor();
        Color32F slotColor = slot.AppliedPose.GetColor();
        Color32F meshColor = mesh.GetColor();
        Color32 color = Premultiply(
            skeletonColor.r * slotColor.r * meshColor.r * tint.R,
            skeletonColor.g * slotColor.g * meshColor.g * tint.G,
            skeletonColor.b * slotColor.b * meshColor.b * tint.B,
            skeletonColor.a * slotColor.a * meshColor.a * tint.A);
        int[] triangles = mesh.Triangles;
        AppendVertices(slot, sequence.GetRegion(sequenceIndex), worldLength, triangles, triangles.Length, color, sequence.GetUVs(sequenceIndex));
    }

    private void AppendVertices(
        Slot slot,
        TextureRegion? region,
        int worldLength,
        int[] triangles,
        int triangleLength,
        Color32 color,
        float[]? meshUvs = null)
    {
        Texture2D? texture = ResolveTexture(region);
        BlendMode blend = slot.Data.BlendMode;
        if (_batchOpen && (!ReferenceEquals(texture, _currentTexture) || blend != _currentBlend))
            Flush();
        if (!_batchOpen)
        {
            _currentTexture = texture;
            _currentBlend = blend;
            _batchOpen = true;
        }

        float[] world = _world;
        float[] uvs = meshUvs ?? _uvs;
        int[] usedTriangles = triangles;
        int usedTriangleLength = triangleLength;
        if (_clipper.IsClipping)
        {
            _clipper.ClipTriangles(world, triangles, triangleLength, uvs);
            world = _clipper.ClippedVertices.Items;
            worldLength = _clipper.ClippedVertices.Count;
            uvs = _clipper.ClippedUVs.Items;
            usedTriangles = _clipper.ClippedTriangles.Items;
            usedTriangleLength = _clipper.ClippedTriangles.Count;
        }

        int addVerts = worldLength >> 1;
        if (addVerts <= 0 || usedTriangleLength < 3)
            return;

        EnsureOutput(_vertexCount + addVerts, _indexCount + usedTriangleLength);
        int baseVertex = _vertexCount;
        for (int i = 0; i < addVerts; i++)
        {
            int src = i << 1;
            _verts[baseVertex + i] = new Float3(world[src], world[src + 1], 0f);
            _uvOut[baseVertex + i] = new Float2(uvs[src], uvs[src + 1]);
            _colors[baseVertex + i] = color;
        }

        for (int i = 0; i < usedTriangleLength; i++)
            _indices[_indexCount + i] = (uint)(baseVertex + usedTriangles[i]);

        _vertexCount += addVerts;
        _indexCount += usedTriangleLength;
    }

    private void Flush()
    {
        if (!_batchOpen || _vertexCount < 3 || _indexCount < 3)
        {
            _vertexCount = 0;
            _indexCount = 0;
            _batchOpen = false;
            return;
        }

        if (_batchCount == _batches.Length)
            Array.Resize(ref _batches, _batches.Length * 2);

        SpineMeshBatch batch = _batches[_batchCount];
        CopyExact(ref batch.Vertices, _verts, _vertexCount);
        CopyExact(ref batch.UVs, _uvOut, _vertexCount);
        CopyExact(ref batch.Colors, _colors, _vertexCount);
        CopyExact(ref batch.Indices, _indices, _indexCount);
        batch.Texture = _currentTexture;
        batch.BlendMode = _currentBlend;
        _batches[_batchCount] = batch;
        _batchCount++;

        _vertexCount = 0;
        _indexCount = 0;
        _batchOpen = false;
    }

    private void EnsureWorld(int length)
    {
        if (_world.Length < length)
            _world = new float[length];
        if (_uvs.Length < length)
            _uvs = new float[length];
    }

    private void EnsureOutput(int verts, int indices)
    {
        if (_verts.Length < verts)
        {
            int cap = Math.Max(verts, Math.Max(16, _verts.Length * 2));
            Array.Resize(ref _verts, cap);
            Array.Resize(ref _uvOut, cap);
            Array.Resize(ref _colors, cap);
        }

        if (_indices.Length < indices)
            Array.Resize(ref _indices, Math.Max(indices, Math.Max(24, _indices.Length * 2)));
    }

    private static void CopyExact<T>(ref T[] dest, T[] src, int count)
    {
        dest ??= [];
        if (dest.Length != count)
            dest = new T[count];
        Array.Copy(src, dest, count);
    }

    private static Texture2D? ResolveTexture(TextureRegion? region)
    {
        if (region is not AtlasRegion atlasRegion)
            return null;

        object? rendererObject = atlasRegion.page.rendererObject;
        Guid id = rendererObject switch
        {
            Texture2D texture => texture.AssetID,
            AssetRef<Texture2D> textureRef => textureRef.AssetID,
            _ => Guid.Empty,
        };
        if (id == Guid.Empty)
            return null;

        // Resolve through the database (synchronous on the main thread): the GPU upload stays on
        // the render thread and a page texture disposed by idle-timeout eviction is revived here.
        // Touch resets the idle timer for as long as this skeleton keeps drawing the page.
        AssetDatabase.Touch(id);
        return AssetDatabase.Get(id) as Texture2D;
    }

    private static Color32 Premultiply(float r, float g, float b, float a)
    {
        a = Math.Clamp(a, 0f, 1f);
        return new Color32(
            (byte)(Math.Clamp(r, 0f, 1f) * a * 255f),
            (byte)(Math.Clamp(g, 0f, 1f) * a * 255f),
            (byte)(Math.Clamp(b, 0f, 1f) * a * 255f),
            (byte)(a * 255f));
    }
}
