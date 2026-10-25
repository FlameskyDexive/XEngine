using System;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Spine38;

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
/// Builds triangle meshes from a posed 3.8 <see cref="Skeleton"/>. Follows spine-unity 3.8
/// MeshGenerator vertex/UV pairing (1:1 copy of ComputeWorldVertices + attachment UVs).
/// </summary>
public sealed class SpineMeshGenerator
{
    private static readonly int[] QuadTriangles = [0, 1, 2, 2, 3, 0];

    private readonly SkeletonClipping _clipper = new();
    private float[] _world = new float[8];
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

        ExposedList<Slot> drawOrder = skeleton.DrawOrder;
        Slot[] slots = drawOrder.Items;
        int slotCount = drawOrder.Count;
        for (int i = 0; i < slotCount; i++)
        {
            Slot slot = slots[i];
            if (!slot.Bone.Active)
            {
                _clipper.ClipEnd(slot);
                continue;
            }

            Attachment? attachment = slot.Attachment;
            if (attachment is ClippingAttachment clip)
            {
                _clipper.ClipStart(slot, clip);
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
        EnsureWorld(8);
        region.ComputeWorldVertices(slot.Bone, _world, 0);
        Color32 color = Premultiply(
            skeleton.R * slot.R * region.R * tint.R,
            skeleton.G * slot.G * region.G * tint.G,
            skeleton.B * slot.B * region.B * tint.B,
            skeleton.A * slot.A * region.A * tint.A);
        AppendVertices(slot, region.RendererObject, 8, QuadTriangles, QuadTriangles.Length, color, region.UVs);
    }

    private void AppendMesh(Skeleton skeleton, Slot slot, MeshAttachment mesh, Color tint)
    {
        int worldLength = mesh.WorldVerticesLength;
        EnsureWorld(worldLength);
        mesh.ComputeWorldVertices(slot, 0, worldLength, _world, 0);
        Color32 color = Premultiply(
            skeleton.R * slot.R * mesh.R * tint.R,
            skeleton.G * slot.G * mesh.G * tint.G,
            skeleton.B * slot.B * mesh.B * tint.B,
            skeleton.A * slot.A * mesh.A * tint.A);
        int[] triangles = mesh.Triangles;
        AppendVertices(slot, mesh.RendererObject, worldLength, triangles, triangles.Length, color, mesh.UVs);
    }

    private void AppendVertices(
        Slot slot,
        object? rendererObject,
        int worldLength,
        int[] triangles,
        int triangleLength,
        Color32 color,
        float[] uvs)
    {
        Texture2D? texture = ResolveTexture(rendererObject);
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
        float[] usedUvs = uvs;
        int[] usedTriangles = triangles;
        int usedTriangleLength = triangleLength;
        if (_clipper.IsClipping)
        {
            _clipper.ClipTriangles(world, worldLength, triangles, triangleLength, uvs);
            world = _clipper.ClippedVertices.Items;
            worldLength = _clipper.ClippedVertices.Count;
            usedUvs = _clipper.ClippedUVs.Items;
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
            _uvOut[baseVertex + i] = new Float2(usedUvs[src], usedUvs[src + 1]);
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

    private static Texture2D? ResolveTexture(object? rendererObject)
    {
        if (rendererObject is not AtlasRegion atlasRegion)
            return rendererObject as Texture2D;

        return ResolvePageTexture(atlasRegion.page?.rendererObject);
    }

    internal static Texture2D? ResolvePageTexture(object? pageObject)
    {
        switch (pageObject)
        {
            case Texture2D texture:
                if (texture.AssetID != Guid.Empty)
                {
                    AssetDatabase.Touch(texture.AssetID);
                    return AssetDatabase.Get(texture.AssetID) as Texture2D ?? texture;
                }

                return texture;
            case AssetRef<Texture2D> textureRef when textureRef.AssetID != Guid.Empty:
                AssetDatabase.Touch(textureRef.AssetID);
                return AssetDatabase.Get(textureRef.AssetID) as Texture2D;
            default:
                return null;
        }
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
