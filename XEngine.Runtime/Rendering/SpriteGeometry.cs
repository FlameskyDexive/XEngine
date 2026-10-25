// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>CPU-side inputs for <see cref="SpriteGeometry"/> (no GPU resources required).</summary>
public struct SpriteGeometryRequest
{
    public SpriteDrawMode Mode;
    public SpriteRect Rect;
    public int TextureWidth;
    public int TextureHeight;
    public Float2 Pivot;
    public float PixelsPerUnit;
    public Float4 Border;
    public Float2 Size;
    public bool FlipX;
    public bool FlipY;
}

/// <summary>
/// Builds Simple / Sliced / Tiled sprite meshes in local XY units. Simple and nine-slice emit
/// independent quads so later retained batching can instance them; tight meshes stay on <see cref="Sprite"/>.
/// </summary>
public static class SpriteGeometry
{
    public static void Build(in SpriteGeometryRequest request, out Float3[] vertices, out Float2[] uvs, out ushort[] indices)
    {
        float ppu = request.PixelsPerUnit > 0f ? request.PixelsPerUnit : 100f;
        float nativeW = request.Rect.Width / ppu;
        float nativeH = request.Rect.Height / ppu;
        float w = request.Size.X > 0f ? request.Size.X : nativeW;
        float h = request.Size.Y > 0f ? request.Size.Y : nativeH;
        if (w <= 0f || h <= 0f)
        {
            vertices = Array.Empty<Float3>();
            uvs = Array.Empty<Float2>();
            indices = Array.Empty<ushort>();
            return;
        }

        switch (request.Mode)
        {
            case SpriteDrawMode.Sliced:
                BuildSliced(request, w, h, ppu, out vertices, out uvs, out indices);
                break;
            case SpriteDrawMode.Tiled:
                BuildTiled(request, w, h, nativeW, nativeH, out vertices, out uvs, out indices);
                break;
            default:
                BuildSimple(request, w, h, out vertices, out uvs, out indices);
                break;
        }
    }

    private static void BuildSimple(in SpriteGeometryRequest request, float w, float h, out Float3[] vertices, out Float2[] uvs, out ushort[] indices)
    {
        vertices = new Float3[4];
        uvs = new Float2[4];
        indices = new ushort[6];
        EmitQuad(vertices, uvs, indices, 0, 0,
            -request.Pivot.X * w, -request.Pivot.Y * h,
            (1f - request.Pivot.X) * w, (1f - request.Pivot.Y) * h,
            SpriteU0(request), SpriteV0(request), SpriteU1(request), SpriteV1(request),
            request.FlipX, request.FlipY);
    }

    private static void BuildSliced(in SpriteGeometryRequest request, float w, float h, float ppu, out Float3[] vertices, out Float2[] uvs, out ushort[] indices)
    {
        float bl = request.Border.X / ppu;
        float bt = request.Border.Y / ppu;
        float br = request.Border.Z / ppu;
        float bb = request.Border.W / ppu;
        float scale = 1f;
        if (bl + br > w && bl + br > 0f) scale = MathF.Min(scale, w / (bl + br));
        if (bt + bb > h && bt + bb > 0f) scale = MathF.Min(scale, h / (bt + bb));
        bl *= scale; bt *= scale; br *= scale; bb *= scale;

        float x0 = -request.Pivot.X * w;
        float y0 = -request.Pivot.Y * h;
        float x3 = x0 + w;
        float y3 = y0 + h;
        float x1 = x0 + bl;
        float x2 = x3 - br;
        float y1 = y0 + bb;
        float y2 = y3 - bt;

        float u0 = SpriteU0(request), u3 = SpriteU1(request);
        float v0 = SpriteV0(request), v3 = SpriteV1(request);
        float su = u3 - u0, sv = v3 - v0;
        float tw = MathF.Max(1, request.Rect.Width);
        float th = MathF.Max(1, request.Rect.Height);
        float u1 = u0 + su * (request.Border.X / tw);
        float u2 = u3 - su * (request.Border.Z / tw);
        float v1 = v0 + sv * (request.Border.W / th);
        float v2 = v3 - sv * (request.Border.Y / th);

        vertices = new Float3[36];
        uvs = new Float2[36];
        indices = new ushort[54];

        ReadOnlySpan<float> xs = stackalloc[] { x0, x1, x2, x3 };
        ReadOnlySpan<float> ys = stackalloc[] { y0, y1, y2, y3 };
        ReadOnlySpan<float> us = stackalloc[] { u0, u1, u2, u3 };
        ReadOnlySpan<float> vs = stackalloc[] { v0, v1, v2, v3 };

        int quad = 0;
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                EmitQuad(vertices, uvs, indices, quad * 4, quad * 6,
                    xs[col], ys[row], xs[col + 1], ys[row + 1],
                    us[col], vs[row], us[col + 1], vs[row + 1],
                    request.FlipX, request.FlipY);
                quad++;
            }
        }
    }

    private static void BuildTiled(in SpriteGeometryRequest request, float w, float h, float tileW, float tileH, out Float3[] vertices, out Float2[] uvs, out ushort[] indices)
    {
        if (tileW <= 0f) tileW = w;
        if (tileH <= 0f) tileH = h;
        int tilesX = Math.Max(1, (int)MathF.Ceiling(w / tileW));
        int tilesY = Math.Max(1, (int)MathF.Ceiling(h / tileH));
        int quads = tilesX * tilesY;
        vertices = new Float3[quads * 4];
        uvs = new Float2[quads * 4];
        indices = new ushort[quads * 6];

        float originX = -request.Pivot.X * w;
        float originY = -request.Pivot.Y * h;
        float u0 = SpriteU0(request), u1 = SpriteU1(request);
        float v0 = SpriteV0(request), v1 = SpriteV1(request);

        int quad = 0;
        for (int ty = 0; ty < tilesY; ty++)
        {
            float y0 = originY + ty * tileH;
            float y1 = MathF.Min(y0 + tileH, originY + h);
            float vScale = tileH > 0f ? (y1 - y0) / tileH : 1f;
            float tv1 = v0 + (v1 - v0) * vScale;
            for (int tx = 0; tx < tilesX; tx++)
            {
                float x0 = originX + tx * tileW;
                float x1 = MathF.Min(x0 + tileW, originX + w);
                float uScale = tileW > 0f ? (x1 - x0) / tileW : 1f;
                float tu1 = u0 + (u1 - u0) * uScale;
                EmitQuad(vertices, uvs, indices, quad * 4, quad * 6,
                    x0, y0, x1, y1, u0, v0, tu1, tv1, request.FlipX, request.FlipY);
                quad++;
            }
        }
    }

    private static float SpriteU0(in SpriteGeometryRequest r) => r.Rect.X / (float)Math.Max(1, r.TextureWidth);
    private static float SpriteV0(in SpriteGeometryRequest r) => r.Rect.Y / (float)Math.Max(1, r.TextureHeight);
    private static float SpriteU1(in SpriteGeometryRequest r) => r.Rect.MaxX / (float)Math.Max(1, r.TextureWidth);
    private static float SpriteV1(in SpriteGeometryRequest r) => r.Rect.MaxY / (float)Math.Max(1, r.TextureHeight);

    private static void EmitQuad(
        Float3[] vertices, Float2[] uvs, ushort[] indices, int vert, int idx,
        float x0, float y0, float x1, float y1,
        float u0, float v0, float u1, float v1,
        bool flipX, bool flipY)
    {
        if (flipX) (u0, u1) = (u1, u0);
        if (flipY) (v0, v1) = (v1, v0);

        vertices[vert] = new Float3(x0, y0, 0f);
        vertices[vert + 1] = new Float3(x1, y0, 0f);
        vertices[vert + 2] = new Float3(x1, y1, 0f);
        vertices[vert + 3] = new Float3(x0, y1, 0f);
        uvs[vert] = new Float2(u0, v0);
        uvs[vert + 1] = new Float2(u1, v0);
        uvs[vert + 2] = new Float2(u1, v1);
        uvs[vert + 3] = new Float2(u0, v1);
        indices[idx] = (ushort)vert;
        indices[idx + 1] = (ushort)(vert + 1);
        indices[idx + 2] = (ushort)(vert + 2);
        indices[idx + 3] = (ushort)(vert + 2);
        indices[idx + 4] = (ushort)(vert + 3);
        indices[idx + 5] = (ushort)vert;
    }
}
