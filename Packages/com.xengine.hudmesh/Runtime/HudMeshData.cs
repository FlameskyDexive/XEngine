using System;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// Per-material combined HUD geometry: fixed-capacity arrays refilled every frame and padded
/// with the last real vertex plus degenerate (zero-area) triangles, so the arrays handed to
/// <see cref="Mesh.AssignStreams"/> keep a constant length — the engine reuses the GPU buffers
/// when the count matches, giving steady-state zero allocation. Slot 0 is reserved for the SDF
/// font material; slots 1..N are sprite textures.
/// </summary>
internal sealed class HudMeshData
{
    public const int InitialVertexCapacity = 4096;
    public const int InitialIndexCapacity = 6144;

    public readonly Mesh Mesh = new();
    public readonly PropertyState Props = new();
    public Material? Material;
    public Texture2D? Texture;

    private Float3[] _vertices = new Float3[InitialVertexCapacity];
    private Float2[] _uvs = new Float2[InitialVertexCapacity];
    private Color32[] _colors = new Color32[InitialVertexCapacity];
    private uint[] _indices = new uint[InitialIndexCapacity];

    public int VertexCapacity = InitialVertexCapacity;
    public int IndexCapacity = InitialIndexCapacity;
    public int VertexCount;
    public int IndexCount;
    public bool HasContent;

    public void BeginFrame()
    {
        VertexCount = 0;
        IndexCount = 0;
        HasContent = false;
    }

    public void EnsureCapacity(int vertices, int indices)
    {
        if (vertices > VertexCapacity)
        {
            int cap = VertexCapacity;
            while (cap < vertices) cap *= 2;
            _vertices = new Float3[cap];
            _uvs = new Float2[cap];
            _colors = new Color32[cap];
            VertexCapacity = cap;
        }
        if (indices > IndexCapacity)
        {
            int cap = IndexCapacity;
            while (cap < indices) cap *= 2;
            _indices = new uint[cap];
            IndexCapacity = cap;
        }
    }

    /// <summary>
    /// Appends one camera-facing quad. Pixel offsets expand against the camera basis
    /// (already scaled to world units per pixel): corner world = anchor + right * (offsetX + cx) + up * (offsetY + cy).
    /// </summary>
    public void AppendQuad(in Float3 anchor, in Float3 rightStep, in Float3 upStep,
        in HudQuad quad, float offsetX, float offsetY, float scale)
    {
        EnsureCapacity(VertexCount + 4, IndexCount + 6);
        uint baseIndex = (uint)VertexCount;
        WriteCorner(VertexCount, anchor, rightStep, upStep, quad.OffsetBL, offsetX, offsetY, scale, quad.UvBL, quad.ColorBL);
        WriteCorner(VertexCount + 1, anchor, rightStep, upStep, quad.OffsetBR, offsetX, offsetY, scale, quad.UvBR, quad.ColorBR);
        WriteCorner(VertexCount + 2, anchor, rightStep, upStep, quad.OffsetTR, offsetX, offsetY, scale, quad.UvTR, quad.ColorTR);
        WriteCorner(VertexCount + 3, anchor, rightStep, upStep, quad.OffsetTL, offsetX, offsetY, scale, quad.UvTL, quad.ColorTL);
        VertexCount += 4;

        _indices[IndexCount++] = baseIndex;
        _indices[IndexCount++] = baseIndex + 1;
        _indices[IndexCount++] = baseIndex + 2;
        _indices[IndexCount++] = baseIndex + 2;
        _indices[IndexCount++] = baseIndex + 3;
        _indices[IndexCount++] = baseIndex;
        HasContent = true;
    }

    private void WriteCorner(int slot, in Float3 anchor, in Float3 rightStep, in Float3 upStep,
        in Float2 corner, float offsetX, float offsetY, float scale, in Float2 uv, in Color32 color)
    {
        float px = offsetX + corner.X * scale;
        float py = offsetY + corner.Y * scale;
        _vertices[slot] = new Float3(
            anchor.X + rightStep.X * px + upStep.X * py,
            anchor.Y + rightStep.Y * px + upStep.Y * py,
            anchor.Z + rightStep.Z * px + upStep.Z * py);
        _uvs[slot] = uv;
        _colors[slot] = color;
    }

    /// <summary>
    /// Pads the buffers to capacity by repeating the last real vertex and emitting degenerate
    /// triangles, keeping array lengths — and therefore GPU buffer sizes — constant across frames.
    /// Pure array work; safe to call from tests without a GPU.
    /// </summary>
    public void FillPadding()
    {
        if (VertexCount <= 0)
            return;

        Float3 lastVertex = _vertices[VertexCount - 1];
        Float2 lastUv = _uvs[VertexCount - 1];
        Color32 lastColor = _colors[VertexCount - 1];
        for (int i = VertexCount; i < VertexCapacity; i++)
        {
            _vertices[i] = lastVertex;
            _uvs[i] = lastUv;
            _colors[i] = lastColor;
        }
        uint lastIndex = (uint)(VertexCount - 1);
        for (int i = IndexCount; i < IndexCapacity; i++)
            _indices[i] = lastIndex;
    }

    /// <summary>Pads, then assigns the streams and uploads. Call only when <see cref="HasContent"/> is true.</summary>
    public void SealAndUpload()
    {
        FillPadding();
        Mesh.AssignStreams(_vertices, _uvs, _colors, _indices);
        Mesh.RecalculateBounds();
        Mesh.Upload();
    }

    public void Dispose()
    {
        Mesh.OnDispose();
        HasContent = false;
    }

    /// <summary>
    /// Appends four pre-expanded world-space vertices (text path). Corner order matches Scribe's
    /// flipped quad order — BL, BR, TL, TR — triangulated as (0,1,3)+(0,3,2).
    /// </summary>
    public void AppendQuadCorners(
        in Float3 c0, in Float3 c1, in Float3 c2, in Float3 c3,
        in Float2 uv0, in Float2 uv1, in Float2 uv2, in Float2 uv3,
        in Color32 col0, in Color32 col1, in Color32 col2, in Color32 col3)
    {
        EnsureCapacity(VertexCount + 4, IndexCount + 6);
        uint baseIndex = (uint)VertexCount;
        _vertices[VertexCount] = c0; _uvs[VertexCount] = uv0; _colors[VertexCount] = col0;
        _vertices[VertexCount + 1] = c1; _uvs[VertexCount + 1] = uv1; _colors[VertexCount + 1] = col1;
        _vertices[VertexCount + 2] = c2; _uvs[VertexCount + 2] = uv2; _colors[VertexCount + 2] = col2;
        _vertices[VertexCount + 3] = c3; _uvs[VertexCount + 3] = uv3; _colors[VertexCount + 3] = col3;
        VertexCount += 4;

        _indices[IndexCount++] = baseIndex;
        _indices[IndexCount++] = baseIndex + 1;
        _indices[IndexCount++] = baseIndex + 3;
        _indices[IndexCount++] = baseIndex;
        _indices[IndexCount++] = baseIndex + 3;
        _indices[IndexCount++] = baseIndex + 2;
        HasContent = true;
    }

    /// <summary>Test/diagnostic read access to the written vertices (before padding).</summary>
    internal Float3 VertexAt(int slot) => _vertices[slot];
}
