using XEngine.Scribe;
using XEngine.Vector;
using System;

namespace XEngine.Quill
{
    /// <summary>
    /// Configuration settings for the font atlas used in text rendering.
    /// </summary>
    public class FontAtlasSettings
    {
        /// <summary>
        /// Whether to allow the atlas to expand when it runs out of space. Default is true.
        /// </summary>
        public bool AllowExpansion = true;

        /// <summary>
        /// The factor by which to expand the atlas when more space is needed. Default is 2.
        /// </summary>
        public float ExpansionFactor = 2f;

        /// <summary>
        /// The initial size of the font atlas in pixels. Default 2048: adaptive raster buckets
        /// (see FontQualityExtensions.PickBucket) keep glyphs at several em sizes, and an atlas
        /// that has to grow mid-frame releases the old texture out from under in-flight draws
        /// (Vulkan bind failure) — better to start with headroom.
        /// </summary>
        public int AtlasSize = 2048;

        /// <summary>
        /// The maximum size the atlas can expand to. Default is 4096.
        /// </summary>
        public int MaxAtlasSize = 4096;

        /// <summary>
        /// Whether to cache text layouts for improved performance. Default is true.
        /// </summary>
        public bool UseLayoutCache = true;

        /// <summary>
        /// The maximum number of layouts to cache. Default is 256.
        /// </summary>
        public int MaxLayoutCacheSize = 256;

        /// <summary>
        /// The padding between glyphs in the atlas. Default is 1.
        /// </summary>
        public int AtlasPadding = 1;
    }

    /// <summary>
    /// Handles text rendering by integrating with the Scribe font system.
    /// </summary>
    public class TextRenderer : IFontRenderer
    {
        private readonly Canvas _canvas;

        private FontSystem _fontSystem;
        private readonly TextRenderer? _fontSource;
        private System.Collections.Generic.List<object>? _ownedAtlases;

        internal void EnableAtlasRetirement()
        {
            if (_fontSource != null) throw new InvalidOperationException("Enable retirement on the font owner, not a borrower.");
            if (!_canvas._renderer.SupportsFontAtlasRetirement) throw new NotSupportedException("Renderer cannot retire font atlases.");
            if (_ownedAtlases == null) _ownedAtlases = new() { FontEngine.Texture };
        }

        internal void RetireAtlases(bool all)
        {
            if (_ownedAtlases == null) return;
            object current = _fontSystem.Texture;
            for (int i = _ownedAtlases.Count - 1; i >= 0; i--)
            {
                object texture = _ownedAtlases[i];
                if (!all && ReferenceEquals(texture, current)) continue;
                _canvas._renderer.RetireFontAtlas(texture);
                _ownedAtlases.RemoveAt(i);
            }
        }

        // Reused across DrawQuads calls so a whole glyph run flushes as one batch (one draw-state
        // check, one triangle-count update) instead of one per triangle.
        private readonly System.Collections.Generic.List<Vertex> _runVertices = new();
        private readonly System.Collections.Generic.List<uint> _runIndices = new();

        /// <summary>
        /// Gets the underlying font system for advanced text operations.
        /// </summary>
        public FontSystem FontEngine
        {
            get
            {
                if (_canvas.IsDisposed) throw new ObjectDisposedException(nameof(Canvas));
                return _fontSource?.FontEngine ?? _fontSystem;
            }
        }

        internal TextRenderer(Canvas canvas, TextRenderer fontSource)
        {
            _canvas = canvas;
            _fontSystem = fontSource.FontEngine;
            _fontSource = fontSource;
        }

        internal bool UsesRenderer(ICanvasRenderer renderer)
            => ReferenceEquals(_canvas._renderer, renderer) || (_fontSource?.UsesRenderer(renderer) ?? false);

        internal TextRenderer(Canvas canvas, FontAtlasSettings settings)
        {
            _canvas = canvas;

            _fontSystem = new FontSystem(this, settings.AtlasSize, settings.AtlasSize, true);

            _fontSystem.AllowExpansion = settings.AllowExpansion;
            _fontSystem.ExpansionFactor = settings.ExpansionFactor;
            _fontSystem.MaxAtlasSize = settings.MaxAtlasSize;
            _fontSystem.CacheLayouts = settings.UseLayoutCache;
            _fontSystem.MaxLayoutCacheSize = settings.MaxLayoutCacheSize;
            _fontSystem.Padding = settings.AtlasPadding;
        }

        /// <summary>
        /// Creates a new texture with the specified dimensions.
        /// </summary>
        public object CreateTexture(int width, int height)
        {
            if (_ownedAtlases?.Count >= 16) throw new InvalidOperationException("Font atlas generation limit exceeded before retirement.");
            object texture = _canvas._renderer.CreateTexture((uint)width, (uint)height);
            _ownedAtlases?.Add(texture);
            return texture;
        }

        /// <summary>
        /// Updates texture data in the specified region. Scribe now produces multi-channel signed
        /// distance fields, so the data is already RGBA (4 bytes per pixel) and is uploaded directly.
        /// </summary>
        public void UpdateTextureRegion(object texture, AtlasRect bounds, byte[] data)
        {
            _canvas._renderer.SetTextureData(texture, new IntRect(bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y + bounds.Height), data);
        }

        /// <summary>
        /// Draws a quad with the given texture and coordinates.
        /// Called by Quill when rendering glyphs.
        /// </summary>
        public void DrawQuads(object texture, ReadOnlySpan<IFontRenderer.Vertex> vertices, ReadOnlySpan<int> indices)
        {
            // Bind the font atlas as dedicated canvas state (a separate sampler unit) rather than the
            // brush texture, so this text batches into the same draw call as surrounding shapes.
            _canvas.SetFontAtlas(texture);

            // UV offset of 2.0 signals text mode to shader (UV >= 2 means text)
            var uvOffset = new Float2(2.0f, 2.0f);

            var transform = _canvas.GetTransform();
            float fbScale = _canvas.FramebufferScale;

            _runVertices.Clear();
            _runIndices.Clear();

            // Scribe hands us an indexed mesh (4 unique vertices + 6 indices per glyph). Transform
            // each unique vertex once and reuse Scribe's indices offset by our base, rather than
            // de-indexing to 6 vertices per glyph.
            uint baseIndex = (uint)_canvas.VertexCount;

            if (transform.IsIdentityOrTranslation)
            {
                // Fast path for the overwhelming majority of UI text (no rotation/scale/skew).
                // Scribe positions are already in pixel space; the divide-to-logical then
                // re-multiply-by-framebuffer-scale round-trip cancels out, leaving a plain offset,
                // so we skip the full affine evaluation entirely.
                float ox = transform.E * fbScale;
                float oy = transform.F * fbScale;

                for (int i = 0; i < vertices.Length; i++)
                {
                    var v = vertices[i];
                    _runVertices.Add(new Vertex(new Float2(v.Position.X + ox, v.Position.Y + oy), v.TextureCoordinate + uvOffset, ToColor(v.Color)));
                }
            }
            else
            {
                // General path: rotation/scale/skew present, go through the full transform.
                // Scribe glyph positions are in pixel space, so divide out the framebuffer scale
                // first (TransformPoint re-applies it).
                float invScale = 1.0f / fbScale;

                for (int i = 0; i < vertices.Length; i++)
                {
                    var v = vertices[i];
                    _runVertices.Add(new Vertex(_canvas.TransformPoint(new Float2(v.Position.X * invScale, v.Position.Y * invScale)), v.TextureCoordinate + uvOffset, ToColor(v.Color)));
                }
            }

            // Append Scribe's indices offset by our base. Each source triangle (i0,i1,i2) is emitted
            // as (i0,i2,i1) to preserve the winding the previous de-indexed path produced.
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                _runIndices.Add(baseIndex + (uint)indices[i]);
                _runIndices.Add(baseIndex + (uint)indices[i + 2]);
                _runIndices.Add(baseIndex + (uint)indices[i + 1]);
            }

            // Flush the whole run as one batch: bulk vertices then bulk indices (single draw-state
            // check + one triangle-count update) instead of per-triangle.
            _canvas.AddVertices(_runVertices);
            _canvas.AddTriangles(_runIndices);

            // The atlas stays bound as canvas state (persistent, part of the batch hash) so shapes
            // drawn after this text keep batching with it.
        }

        private static FontColor ToFSColor(XEngine.Vector.Color32 color)
        {
            return new FontColor(color.R, color.G, color.B, color.A);
        }

        private static Color32 ToColor(FontColor color)
        {
            return Color32.FromArgb(color.A, color.R, color.G, color.B);
        }
    }
}
