// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Draws a <see cref="Sprite"/> in world space on the XY plane. <see cref="SpriteDrawMode.Simple"/>
/// uses the sprite's own geometry (tight mesh when present). Sliced and Tiled generate quads from
/// <see cref="Size"/> and the sprite border.
/// </summary>
[AddComponentMenu("Rendering/Sprite Renderer")]
[ComponentIcon("")] // Image
[GenerateSerializer]
public partial class SpriteRenderer : MonoBehaviour
{
    /// <summary>Sorting-order spacing along Z (world units) so higher orders sort in front for the transparent queue.</summary>
    private const float SortBias = 0.0001f;

    public AssetRef<Sprite> Sprite;

    /// <summary>Tint multiplied into the sprite. Baked into vertex color.</summary>
    public Color Color = Color.White;

    /// <summary>Simple keeps native/tight geometry. Sliced and Tiled use <see cref="Size"/>.</summary>
    public SpriteDrawMode DrawMode = SpriteDrawMode.Simple;

    /// <summary>Explicit draw size in world units. (0,0) means the sprite's native size.</summary>
    public Float2 Size;

    public bool FlipX;
    public bool FlipY;

    /// <summary>Optional material override. Defaults to the built-in unlit alpha-blended sprite material.</summary>
    public AssetRef<Material> Material;

    /// <summary>Named sorting bucket. Combined with <see cref="SortingOrder"/> for transparent ordering.</summary>
    public int SortingLayer;

    /// <summary>Higher values render in front of lower ones (applied as a small Z bias for the transparent queue).</summary>
    public int SortingOrder;

    /// <summary>Which rendering layers this renderer belongs to. Independent of <see cref="GameObject.LayerIndex"/>.</summary>
    public RenderingLayerMask RenderingLayers = RenderingLayerMask.Everything;

    [System.NonSerialized] private Mesh? _mesh;
    [System.NonSerialized] private PropertyState? _props;

    // Snapshot of the inputs the baked mesh was built from, so we only rebuild when something changes.
    [System.NonSerialized] private Sprite? _bakedSprite;
    [System.NonSerialized] private bool _bakedFlipX, _bakedFlipY;
    [System.NonSerialized] private Color _bakedColor;
    [System.NonSerialized] private SpriteDrawMode _bakedDrawMode;
    [System.NonSerialized] private Float2 _bakedSize;

    private static Material? s_defaultMaterial;

    /// <summary>The shared built-in unlit, alpha-blended, tinted sprite material.</summary>
    public static Material DefaultSpriteMaterial =>
        s_defaultMaterial ??= new Material(Shader.LoadDefault(DefaultShader.Sprite));

    private static Material? s_defaultLitMaterial;

    /// <summary>Sprite material that multiplies <c>_LightTexture2D</c> (Renderer2D).</summary>
    public static Material DefaultSpriteLitMaterial =>
        s_defaultLitMaterial ??= new Material(Shader.LoadDefault(DefaultShader.SpriteLit));

    internal static Material ResolveDefaultMaterial()
        => UniversalRenderPipeline.ActiveRendererKind == RendererKind.Renderer2D
            ? DefaultSpriteLitMaterial
            : DefaultSpriteMaterial;

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        if (SpriteRendererRegistry.TrySubmit(this, camera))
            return;

        Sprite? sprite = Sprite.Res;
        if (sprite == null) return;

        Texture2D? tex = sprite.Texture.Res;
        if (tex == null || tex.Width == 0 || tex.Height == 0) return;
        if (sprite.Rect.Width <= 0 || sprite.Rect.Height <= 0) return;

        EnsureMesh(sprite, tex);
        if (_mesh == null) return;

        Material mat = Material.Res ?? ResolveDefaultMaterial();

        _props ??= new PropertyState();
        _props.Clear();
        _props.SetTexture("_MainTex", tex);

        // Bind any secondary maps (e.g. "_NormalMap", "_MaskMap") so custom sprite materials can sample them.
        if (sprite.SecondaryTextures.Count > 0)
        {
            foreach (var key in sprite.SecondaryTextures.Keys)
            {
                Texture2D? secondary = CollectionsMarshal.GetValueRefOrNullRef(sprite.SecondaryTextures, key).Res;
                if (secondary != null)
                    _props.SetTexture(key, secondary);
            }
        }

        SpriteSorting.Resolve(this, out int layer, out int order);
        Float4x4 world = Transform.LocalToWorldMatrix;
        if (layer != 0 || order != 0)
            world *= Float4x4.CreateTranslation(new Float3(0, 0, (layer * 1000 + order) * SortBias));

        var spriteRenderable = new MeshRenderable(_mesh, mat, world, GameObject.LayerIndex, _props,
            renderingLayerMask: RenderingLayers.Mask);
        spriteRenderable.SetOwner(GameObject);
        renderables.Add(spriteRenderable);
    }

    private void EnsureMesh(Sprite sprite, Texture2D tex)
    {
        if (!NeedsRebuild(sprite)) return;

        bool useGenerated = DrawMode != SpriteDrawMode.Simple || Size.X > 0f || Size.Y > 0f;
        Float3[] verts;
        Float2[] uvs;
        uint[] idx;
        if (useGenerated)
        {
            SpriteGeometry.Build(new SpriteGeometryRequest
            {
                Mode = DrawMode,
                Rect = sprite.Rect,
                TextureWidth = (int)tex.Width,
                TextureHeight = (int)tex.Height,
                Pivot = sprite.Pivot,
                PixelsPerUnit = sprite.PixelsPerUnit,
                Border = sprite.Border,
                Size = Size,
                FlipX = FlipX,
                FlipY = FlipY,
            }, out verts, out Float2[] builtUvs, out ushort[] builtIdx);
            if (verts.Length < 3 || builtIdx.Length < 3)
                return;
            uvs = builtUvs;
            idx = new uint[builtIdx.Length];
            for (int i = 0; i < builtIdx.Length; i++) idx[i] = builtIdx[i];
        }
        else
        {
            Float2[] srcVerts = sprite.Vertices;
            Float2[] srcUV = sprite.UV;
            ushort[] srcIdx = sprite.Indices;
            int n = srcVerts.Length;
            if (n < 3 || srcIdx.Length < 3 || srcUV.Length != n)
                return; // nothing valid to draw; keep the previous mesh (if any)

            float u0 = sprite.Rect.X / (float)tex.Width, u1 = sprite.Rect.MaxX / (float)tex.Width;
            float v0 = sprite.Rect.Y / (float)tex.Height, v1 = sprite.Rect.MaxY / (float)tex.Height;

            verts = new Float3[n];
            uvs = new Float2[n];
            idx = new uint[srcIdx.Length];
            for (int i = 0; i < n; i++)
            {
                verts[i] = new Float3(srcVerts[i].X, srcVerts[i].Y, 0f);
                Float2 uv = srcUV[i];
                if (FlipX) uv.X = u0 + u1 - uv.X;
                if (FlipY) uv.Y = v0 + v1 - uv.Y;
                uvs[i] = uv;
            }
            for (int i = 0; i < srcIdx.Length; i++) idx[i] = srcIdx[i];
        }

        _mesh ??= new Mesh();
        var colors = new Color32[verts.Length];
        Color32 tint = (Color32)Color;
        for (int i = 0; i < colors.Length; i++)
            colors[i] = tint;

        _mesh.Vertices = verts;
        _mesh.UV = uvs;
        _mesh.Colors32 = colors;
        _mesh.Indices = idx;
        _mesh.RecalculateBounds();
        _mesh.Upload();

        Snapshot(sprite);
    }

    private bool NeedsRebuild(Sprite sprite) =>
        _mesh == null ||
        !ReferenceEquals(_bakedSprite, sprite) ||
        _bakedFlipX != FlipX || _bakedFlipY != FlipY ||
        !_bakedColor.Equals(Color) ||
        _bakedDrawMode != DrawMode ||
        !_bakedSize.Equals(Size);

    private void Snapshot(Sprite sprite)
    {
        _bakedSprite = sprite;
        _bakedFlipX = FlipX; _bakedFlipY = FlipY;
        _bakedColor = Color;
        _bakedDrawMode = DrawMode;
        _bakedSize = Size;
    }

    public override void DrawGizmosSelected()
    {
        Sprite? sprite = Sprite.Res;
        if (sprite == null) return;

        float ppu = sprite.PixelsPerUnit > 0 ? sprite.PixelsPerUnit : 100f;
        float w = sprite.Rect.Width / ppu, h = sprite.Rect.Height / ppu;
        Float2 pivot = sprite.Pivot;
        float x0 = -pivot.X * w, y0 = -pivot.Y * h, x1 = (1f - pivot.X) * w, y1 = (1f - pivot.Y) * h;

        // Rect outline, following the GameObject's transform (so rotation/scale are respected).
        Float4x4 world = Transform.LocalToWorldMatrix;
        Float3 a = Float4x4.TransformPoint(new Float3(x0, y0, 0f), world);
        Float3 b = Float4x4.TransformPoint(new Float3(x1, y0, 0f), world);
        Float3 c = Float4x4.TransformPoint(new Float3(x1, y1, 0f), world);
        Float3 d = Float4x4.TransformPoint(new Float3(x0, y1, 0f), world);

        var outline = new Color(0.4f, 1f, 0.55f, 1f);
        Debug.DrawLine(a, b, outline);
        Debug.DrawLine(b, c, outline);
        Debug.DrawLine(c, d, outline);
        Debug.DrawLine(d, a, outline);

        // Pivot cross at the sprite's origin (the GameObject position).
        Float3 p = Transform.Position;
        Float3 right = Transform.Right * 0.08f;
        Float3 up = Transform.Up * 0.08f;
        var pivotColor = new Color(1f, 0.85f, 0.2f, 1f);
        Debug.DrawLine(p - right, p + right, pivotColor);
        Debug.DrawLine(p - up, p + up, pivotColor);
    }

    public override void OnDispose()
    {
        _mesh?.Dispose();
        _mesh = null;
    }
}
