// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Groups adjacent Canvas nodes that share material, texture, clip and stencil into a
/// persistent mesh page. Single exclusive items keep their own mesh so stencil writers
/// and unmatched graphics stay one-to-one with CollectRenderNodes.
/// </summary>
internal sealed class UIBatchComposer
{
    private readonly List<UIBatchPage> _pages = new(8);
    private readonly List<UIBatchDraw> _draws = new(16);

    public IReadOnlyList<UIBatchDraw> Draws => _draws;
    public int MeshUploadsThisCompose { get; private set; }
    public int MergedBatchCount { get; private set; }

    public void Compose(IReadOnlyList<CanvasRenderNode> items, in ViewerData viewer)
    {
        _draws.Clear();
        MeshUploadsThisCompose = 0;
        MergedBatchCount = 0;
        int pageCursor = 0;

        int i = 0;
        while (i < items.Count)
        {
            if (!TryPrepare(items[i], viewer, out PreparedItem first))
            {
                i++;
                continue;
            }

            int start = i;
            int count = 1;
            i++;
            if (!first.Exclusive)
            {
                while (i < items.Count)
                {
                    if (!TryPrepare(items[i], viewer, out PreparedItem next))
                        break;
                    if (!CanMerge(in first, in next))
                        break;
                    count++;
                    i++;
                }
            }

            if (count == 1)
            {
                _draws.Add(new UIBatchDraw
                {
                    Mesh = first.Mesh,
                    Material = first.Material,
                    Properties = first.Properties,
                    Clip = first.Clip,
                    Model = first.Model,
                    VertexCount = first.Mesh.VertexCount,
                    IndexCount = first.Mesh.IndexCount,
                    Merged = false,
                });
                continue;
            }

            UIBatchPage page = GetPage(pageCursor++);
            if (page.Write(items, start, count, viewer))
                MeshUploadsThisCompose++;
            MergedBatchCount++;
            _draws.Add(page.ToDraw());
        }
    }

    private UIBatchPage GetPage(int index)
    {
        while (_pages.Count <= index)
            _pages.Add(new UIBatchPage());
        return _pages[index];
    }

    private static bool TryPrepare(CanvasRenderNode node, in ViewerData viewer, out PreparedItem prepared)
    {
        prepared = default;
        Material? material = node.GetMaterial();
        if (material == null || material.Shader.IsNotValid())
            return false;
        node.GetRenderingData(viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out _);
        if (mesh == null || mesh.VertexCount <= 0 || mesh.IndexCount <= 0)
            return false;

        prepared = new PreparedItem
        {
            Material = material,
            Properties = properties,
            Mesh = mesh,
            Model = model,
            Texture = properties.GetTexture("_MainTex"),
            Tiling = properties.GetVector2("_Tiling"),
            Offset = properties.GetVector2("_Offset"),
            Tint = properties.GetColor("_MainColor"),
            Clip = node.Clip,
            HasUv2 = mesh.HasUV2,
            Exclusive = node.Clip.IsStencilWrite || node.Clip.IsStencilUnwind,
        };
        return true;
    }

    private static bool CanMerge(in PreparedItem a, in PreparedItem b)
        => !a.Exclusive
        && !b.Exclusive
        && ReferenceEquals(a.Material, b.Material)
        && ReferenceEquals(a.Texture, b.Texture)
        && a.HasUv2 == b.HasUv2
        && a.Tiling.Equals(b.Tiling)
        && a.Offset.Equals(b.Offset)
        && a.Clip.Equals(b.Clip);

    private struct PreparedItem
    {
        public Material Material;
        public PropertyState Properties;
        public Mesh Mesh;
        public Float4x4 Model;
        public Texture2D? Texture;
        public Float2 Tiling;
        public Float2 Offset;
        public Color Tint;
        public CanvasClipState Clip;
        public bool HasUv2;
        public bool Exclusive;
    }
}

internal struct UIBatchDraw
{
    public Mesh Mesh;
    public Material Material;
    public PropertyState Properties;
    public CanvasClipState Clip;
    public Float4x4 Model;
    public int VertexCount;
    public int IndexCount;
    public bool Merged;
}

internal sealed class UIBatchPage
{
    public Mesh Mesh { get; } = new Mesh();
    public PropertyState Properties { get; } = new PropertyState();
    public Material? Material;
    public CanvasClipState Clip;
    public Float4x4 Model = Float4x4.Identity;
    public int VertexCount;
    public int IndexCount;

    private Float3[] _verts = [];
    private Float2[] _uvs = [];
    private Float2[]? _uv2;
    private Color32[] _colors = [];
    private uint[] _indices = [];
    private UIBatchItemSlot[] _slots = [];
    private int _itemCount;
    private ulong _signature;
    private bool _uploaded;

    public bool Write(IReadOnlyList<CanvasRenderNode> items, int start, int count, in ViewerData viewer)
    {
        EnsureCapacity(count);

        int vertTotal = 0;
        int indexTotal = 0;
        ulong signature = (ulong)(uint)count;
        Material? material = null;
        PropertyState? firstProps = null;
        CanvasClipState clip = default;
        bool hasUv2 = false;

        for (int i = 0; i < count; i++)
        {
            CanvasRenderNode node = items[start + i];
            node.GetRenderingData(viewer, out PropertyState props, out Mesh mesh, out Float4x4 model, out _);
            if (i == 0)
            {
                material = node.GetMaterial();
                firstProps = props;
                clip = node.Clip;
                hasUv2 = mesh.HasUV2;
            }

            int vc = mesh.VertexCount;
            int ic = mesh.IndexCount;
            _slots[i] = new UIBatchItemSlot
            {
                MeshVersion = mesh.Version,
                ModelHash = HashModel(model),
                ColorHash = HashColor(props.GetColor("_MainColor")),
                VertexStart = vertTotal,
                VertexCount = vc,
                IndexStart = indexTotal,
                IndexCount = ic,
            };
            vertTotal += vc;
            indexTotal += ic;
            signature = Mix(signature, mesh.Version);
            signature = Mix(signature, HashModel(model));
            signature = Mix(signature, HashColor(props.GetColor("_MainColor")));
            signature = Mix(signature, (uint)(mesh.HasUV2 ? 1 : 0));
        }

        Texture2D? tex = firstProps?.GetTexture("_MainTex");
        signature = Mix(signature, (uint)(tex?.InstanceID ?? 0));
        signature = Mix(signature, (uint)clip.GetHashCode());

        bool sameTopology = _itemCount == count
            && _verts.Length == vertTotal
            && _indices.Length == indexTotal
            && ((_uv2 != null) == hasUv2);
        bool sameSignature = sameTopology && _signature == signature && _uploaded;

        Material = material;
        Clip = clip;
        Model = Float4x4.Identity;
        VertexCount = vertTotal;
        IndexCount = indexTotal;
        _itemCount = count;
        Properties.Clear();
        if (firstProps != null)
            Properties.ApplyOverride(firstProps);
        Properties.SetColor("_MainColor", Color.White);

        if (sameSignature)
            return false;

        if (!sameTopology)
        {
            _verts = new Float3[vertTotal];
            _uvs = new Float2[vertTotal];
            _uv2 = hasUv2 ? new Float2[vertTotal] : null;
            _colors = new Color32[vertTotal];
            _indices = new uint[indexTotal];
        }

        int vOff = 0;
        int iOff = 0;
        for (int i = 0; i < count; i++)
        {
            CanvasRenderNode node = items[start + i];
            node.GetRenderingData(viewer, out PropertyState props, out Mesh mesh, out Float4x4 model, out _);
            Color tint = props.GetColor("_MainColor");
            Float3[] srcV = mesh.Vertices;
            Float2[] srcUv = mesh.UV;
            Float2[]? srcUv2 = mesh.HasUV2 ? mesh.UV2 : null;
            Color32[]? srcC32 = mesh.HasColors32 ? mesh.Colors32 : null;
            Color[]? srcC = !mesh.HasColors32 && mesh.HasColors ? mesh.Colors : null;
            uint[] srcI = mesh.Indices;

            for (int v = 0; v < srcV.Length; v++)
            {
                _verts[vOff + v] = Float4x4.TransformPoint(srcV[v], model);
                _uvs[vOff + v] = v < srcUv.Length ? srcUv[v] : Float2.Zero;
                if (_uv2 != null)
                    _uv2[vOff + v] = srcUv2 != null && v < srcUv2.Length ? srcUv2[v] : Float2.Zero;
                Color32 srcColor = Color.White;
                if (srcC32 != null && v < srcC32.Length)
                    srcColor = srcC32[v];
                else if (srcC != null && v < srcC.Length)
                    srcColor = srcC[v];
                _colors[vOff + v] = Multiply(srcColor, tint);
            }

            for (int n = 0; n < srcI.Length; n++)
                _indices[iOff + n] = srcI[n] + (uint)vOff;

            vOff += srcV.Length;
            iOff += srcI.Length;
        }

        Mesh.AssignUiPageStreams(_verts, _uvs, _uv2, _colors, _indices);
        Mesh.RecalculateBounds();
        long vertexBytes = (long)vertTotal * (hasUv2 ? 44 : 36);
        long indexBytes = (long)indexTotal * 4;
        RenderStats.RecordMeshUpload(vertTotal, indexTotal, vertexBytes + indexBytes);
        _signature = signature;
        _uploaded = true;
        return true;
    }

    public UIBatchDraw ToDraw() => new()
    {
        Mesh = Mesh,
        Material = Material!,
        Properties = Properties,
        Clip = Clip,
        Model = Model,
        VertexCount = VertexCount,
        IndexCount = IndexCount,
        Merged = true,
    };

    private void EnsureCapacity(int count)
    {
        if (_slots.Length < count)
            _slots = new UIBatchItemSlot[count];
    }

    private static Color32 Multiply(Color32 src, Color tint)
    {
        Color c = src;
        return new Color32(
            (byte)Maths.Clamp(c.R * tint.R * 255f, 0f, 255f),
            (byte)Maths.Clamp(c.G * tint.G * 255f, 0f, 255f),
            (byte)Maths.Clamp(c.B * tint.B * 255f, 0f, 255f),
            (byte)Maths.Clamp(c.A * tint.A * 255f, 0f, 255f));
    }

    private static uint HashModel(in Float4x4 m)
    {
        HashCode hash = new();
        hash.Add(m.c0);
        hash.Add(m.c1);
        hash.Add(m.c2);
        hash.Add(m.c3);
        return (uint)hash.ToHashCode();
    }

    private static uint HashColor(in Color c)
        => (uint)HashCode.Combine(c.R, c.G, c.B, c.A);

    private static ulong Mix(ulong hash, uint value)
    {
        hash ^= value + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2);
        return hash;
    }

    private struct UIBatchItemSlot
    {
        public uint MeshVersion;
        public uint ModelHash;
        public uint ColorHash;
        public int VertexStart;
        public int VertexCount;
        public int IndexStart;
        public int IndexCount;
    }
}
