// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace PDNWrapper
{
    // The vendored Paint.NET PSD plugin (PsdFile/, PhotoShopFileType/) was written against
    // Paint.NET's own geometry and bitmap types. These are XEngine's stand-ins, kept in the
    // namespace the plugin already imports so the MIT-licensed sources compile with minimal
    // diff — every type here is deliberately pure BCL, because the whole parser is linked
    // into the offline AI Host, where no engine assembly exists.

    /// <summary>Integer rectangle mirroring the System.Drawing API surface the plugin relies on.</summary>
    internal struct Rectangle : IEquatable<Rectangle>
    {
        public static readonly Rectangle Empty = default;

        public int X;
        public int Y;
        public int Width;
        public int Height;

        public Rectangle(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int Left => X;
        public int Top => Y;
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public bool IsEmpty => Width <= 0 || Height <= 0;

        public Size Size => new Size(Width, Height);

        public static Rectangle FromLTRB(int left, int top, int right, int bottom)
            => new Rectangle(left, top, right - left, bottom - top);

        public void Offset(int dx, int dy)
        {
            X += dx;
            Y += dy;
        }

        /// <summary>
        /// Shrinks this rectangle to its intersection with <paramref name="other"/>, mutating like
        /// System.Drawing's — the vendored plugin's <c>IntersectWith</c> extension builds on this.
        /// </summary>
        public void Intersect(Rectangle other)
        {
            int left = Math.Max(Left, other.Left);
            int top = Math.Max(Top, other.Top);
            int right = Math.Min(Right, other.Right);
            int bottom = Math.Min(Bottom, other.Bottom);
            this = right > left && bottom > top ? FromLTRB(left, top, right, bottom) : Empty;
        }

        /// <summary>The smallest rectangle containing both.</summary>
        public static Rectangle Union(Rectangle a, Rectangle b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            return FromLTRB(
                Math.Min(a.Left, b.Left),
                Math.Min(a.Top, b.Top),
                Math.Max(a.Right, b.Right),
                Math.Max(a.Bottom, b.Bottom));
        }

        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        public bool Equals(Rectangle other)
            => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

        public override bool Equals(object obj) => obj is Rectangle other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

        public static bool operator ==(Rectangle a, Rectangle b) => a.Equals(b);

        public static bool operator !=(Rectangle a, Rectangle b) => !a.Equals(b);

        public override string ToString()
            => "(" + X + "," + Y + " " + Width + "x" + Height + ")";
    }

    /// <summary>Integer size, mirroring System.Drawing.Size.</summary>
    internal struct Size
    {
        public int Width;
        public int Height;

        public Size(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public bool IsEmpty => Width <= 0 || Height <= 0;
    }

    /// <summary>
    /// One RGBA pixel. Field names match the lowercase convention the vendored decoder writes
    /// (<c>c.r</c>, <c>c.a</c>, …) so its row loops compile unchanged.
    /// </summary>
    internal struct Color32
    {
        public byte r;
        public byte g;
        public byte b;
        public byte a;

        public Color32(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
    }

    /// <summary>A decoded bitmap: one RGBA pixel per cell, row-major, top row first.</summary>
    internal sealed class Surface
    {
        public Surface(int w, int h)
        {
            width = w;
            height = h;
            color = new Color32[Math.Max(0, w) * Math.Max(0, h)];
        }

        public Color32[] color { get; }

        public int width { get; }

        public int height { get; }

        public void Dispose()
        {
            // Managed memory; kept so vendored call sites compile unchanged.
        }
    }

    internal enum MeasurementUnit
    {
        Pixel = 1,
        Inch = 2,
        Centimeter = 3,
    }

    /// <summary>
    /// Blend modes the plugin maps PSD blend keys onto. XEngine only reads the value through —
    /// UI import flattens per layer and never composites across layers — but the fidelity is kept
    /// so a future compositor has the authored intent.
    /// </summary>
    internal enum LayerBlendMode
    {
        Normal = 0,
        Multiply = 1,
        Additive = 2,
        ColorBurn = 3,
        ColorDodge = 4,
        Reflect = 5,
        Glow = 6,
        Overlay = 7,
        Difference = 8,
        Negation = 9,
        Lighten = 10,
        Darken = 11,
        Screen = 12,
        Xor = 13,
    }

    /// <summary>A loaded PSD as a layer tree of decoded bitmaps, plus document-level metadata.</summary>
    internal sealed class Document
    {
        public Document(int w, int h)
        {
            width = w;
            height = h;
            Layers = new List<BitmapLayer>();
        }

        public int width { get; }

        public int height { get; }

        /// <summary>Root layers, top-most first (layers-panel order). Groups carry their children.</summary>
        public List<BitmapLayer> Layers { get; }

        public MeasurementUnit DpuUnit { get; set; }

        public double DpuX { get; set; }

        public double DpuY { get; set; }

        public void Dispose()
        {
            foreach (BitmapLayer layer in Layers) layer.Dispose();
        }
    }

    internal static class Layer
    {
        public static BitmapLayer CreateBackgroundLayer(int w, int h)
            => new BitmapLayer(new Rectangle(0, 0, w, h));
    }

    /// <summary>One decoded layer: its pixels, its place in the document, and its children when a group.</summary>
    internal sealed class BitmapLayer
    {
        private readonly List<BitmapLayer> m_ChildLayers = new List<BitmapLayer>();

        public BitmapLayer(Rectangle documentRect)
        {
            localRect = new Rectangle(0, 0, documentRect.Width, documentRect.Height);
            this.documentRect = documentRect;
            Surface = new Surface(localRect.Width, localRect.Height);
        }

        public int LayerID { get; set; }

        public bool IsGroup { get; set; }

        public BitmapLayer ParentLayer { get; set; }

        public IEnumerable<BitmapLayer> ChildLayer => m_ChildLayers;

        public string Name { get; set; }

        public byte Opacity { get; set; }

        public bool Visible { get; set; }

        public LayerBlendMode BlendMode { get; set; }

        public Surface Surface { get; }

        /// <summary>Layer bounds in document space. For a group, grows to enclose its children.</summary>
        public Rectangle documentRect { get; private set; }

        /// <summary>The same bounds with the origin at the layer's own top-left.</summary>
        public Rectangle localRect { get; }

        public bool IsEmpty => documentRect.IsEmpty;

        public void AddChildLayer(BitmapLayer child)
        {
            m_ChildLayers.Add(child);

            // A group's own record carries no pixels and an empty rect; its meaningful bounds are
            // the union of everything inside it.
            Rectangle bounds = documentRect;
            foreach (BitmapLayer c in m_ChildLayers)
                bounds = Rectangle.Union(bounds, c.documentRect);
            documentRect = bounds;
        }

        public void Dispose()
        {
            Surface.Dispose();
            foreach (BitmapLayer layer in m_ChildLayers) layer.Dispose();
        }
    }
}
