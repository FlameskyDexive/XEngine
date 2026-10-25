// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

public class VertexFormat
{
    public Element[] Elements;
    public int Size;

    private int _layoutId;

    /// <summary>
    /// Interned identity of this vertex declaration's CONTENT (semantics, types, counts,
    /// offsets, strides, divisors, normalization, total size). Two formats describing the same
    /// layout share one id, so pipeline-state caches can key on the declaration instead of the
    /// vertex-array object — identical-layout meshes then share PSOs. Interning uses exact
    /// content equality (no hashing shortcuts), so distinct layouts can never collide.
    /// </summary>
    public int LayoutId => _layoutId != 0 ? _layoutId : _layoutId = VertexLayoutRegistry.GetId(this);

    /// <summary>Exact-content interning registry backing <see cref="LayoutId"/>.</summary>
    private static class VertexLayoutRegistry
    {
        private static readonly object s_lock = new();
        private static readonly System.Collections.Generic.List<VertexFormat> s_canonical = new(32);

        public static int GetId(VertexFormat format)
        {
            lock (s_lock)
            {
                for (int i = 0; i < s_canonical.Count; i++)
                {
                    if (SameLayout(s_canonical[i], format))
                        return i + 1; // ids start at 1 so 0 means "not yet interned"
                }
                s_canonical.Add(format);
                return s_canonical.Count;
            }
        }

        private static bool SameLayout(VertexFormat a, VertexFormat b)
        {
            if (a.Size != b.Size || a.Elements.Length != b.Elements.Length)
                return false;
            for (int i = 0; i < a.Elements.Length; i++)
            {
                Element x = a.Elements[i];
                Element y = b.Elements[i];
                if (x.Semantic != y.Semantic || x.Type != y.Type || x.Count != y.Count
                    || x.Offset != y.Offset || x.Stride != y.Stride || x.Divisor != y.Divisor
                    || x.Normalized != y.Normalized)
                    return false;
            }
            return true;
        }
    }

    public VertexFormat() { }

    public VertexFormat(Element[] elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        if (elements.Length == 0) throw new($"The argument '{nameof(elements)}' is null!");

        Elements = elements;

        foreach (Element element in Elements)
        {
            element.Offset = (short)Size;
            int s = 0;
            if ((int)element.Type > 5122) s = 4 * element.Count; //Greater than short then its either a Float or Int
            else if ((int)element.Type > 5121) s = 2 * element.Count; //Greater than byte then its a Short
            else s = 1 * element.Count; //Byte or Unsigned Byte
            Size += s;
            element.Stride = (short)s;
        }
    }

    public class Element
    {
        public uint Semantic;
        public VertexType Type;
        public byte Count;
        public short Offset; // Automatically assigned in VertexFormats constructor
        public short Stride; // Automatically assigned in VertexFormats constructor
        public short Divisor;
        public bool Normalized;
        public Element() { }
        public Element(VertexSemantic semantic, VertexType type, byte count, short divisor = 0, bool normalized = false)
        {
            Semantic = (uint)semantic;
            Type = type;
            Count = count;
            Divisor = divisor;
            Normalized = normalized;
        }
        public Element(uint semantic, VertexType type, byte count, short divisor = 0, bool normalized = false)
        {
            Semantic = semantic;
            Type = type;
            Count = count;
            Divisor = divisor;
            Normalized = normalized;
        }
    }

    public enum VertexSemantic { Position, TexCoord0, TexCoord1, Normal, Color, Tangent, BoneIndex, BoneWeight }

    /// <summary>
    /// Component type for a vertex attribute.
    /// <para>
    /// Numeric values match OpenGL <c>GL_BYTE</c> / <c>GL_UNSIGNED_BYTE</c> /
    /// <c>GL_SHORT</c> / <c>GL_INT</c> / <c>GL_FLOAT</c> so the OpenGL path can pass
    /// them through raw. The stride logic in <see cref="VertexFormat"/> constructors
    /// also depends on these thresholds (5121 / 5122).
    /// </para>
    /// <para>
    /// Other backends must not treat these as native API enums — map via a
    /// <c>VertexTypeMapper</c> table (OpenGL: identity; Vulkan/D3D12: to
    /// <c>VkFormat</c> / <c>DXGI_FORMAT</c>). Do not renumber these values without
    /// updating Size calculation and any serialized mesh data.
    /// </para>
    /// </summary>
    public enum VertexType
    {
        Byte = 5120,
        UnsignedByte = 5121,
        Short = 5122,
        Int = 5124,
        Float = 5126,
    }
}
