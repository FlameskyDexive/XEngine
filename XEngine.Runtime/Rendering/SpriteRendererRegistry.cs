// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Retained 2D collect: Simple full-rect sprites submit instances, then one sort/batch pass emits
/// <see cref="SpriteBatchRenderable"/> draws. Tight meshes and Sliced/Tiled keep the legacy mesh path.
/// </summary>
public static class SpriteRendererRegistry
{
    private const float SortBias = 0.0001f;

    private static readonly SpriteBatchCollector Collector = new();
    private static readonly Dictionary<int, SpriteRenderer> Renderers = new();
    private static Mesh? s_quad;

    public static int LastInstanceCount { get; private set; }
    public static int LastBatchCount { get; private set; }

    public static Mesh SharedQuad
    {
        get
        {
            if (s_quad == null)
            {
                s_quad = new Mesh();
                s_quad.Vertices =
                [
                    new Float3(-0.5f, -0.5f, 0f),
                    new Float3(0.5f, -0.5f, 0f),
                    new Float3(0.5f, 0.5f, 0f),
                    new Float3(-0.5f, 0.5f, 0f),
                ];
                s_quad.UV =
                [
                    new Float2(0f, 0f),
                    new Float2(1f, 0f),
                    new Float2(1f, 1f),
                    new Float2(0f, 1f),
                ];
                s_quad.Indices = [0, 1, 2, 2, 3, 0];
                s_quad.RecalculateBounds();
            }

            return s_quad;
        }
    }

    public static void ResetForTests()
    {
        Collector.Begin();
        Renderers.Clear();
        LastInstanceCount = 0;
        LastBatchCount = 0;
    }

    public static void BeginCollect()
    {
        Collector.Begin();
        Renderers.Clear();
    }

    public static bool TrySubmit(SpriteRenderer renderer, Camera camera)
    {
        if (renderer == null)
            return false;
        if (!UsesInstancedQuad(renderer))
            return false;

        Submit(renderer, camera);
        return true;
    }

    public static void Submit(SpriteRenderer renderer, Camera camera)
    {
        if (renderer == null)
            return;

        Sprite? sprite = renderer.Sprite.Res;
        if (sprite == null || sprite.Rect.Width <= 0 || sprite.Rect.Height <= 0)
            return;

        SpriteSorting.Resolve(renderer, out int layer, out int order);
        int materialId = renderer.Material.Res?.InstanceID ?? 0;
        Texture2D? tex = sprite.Texture.Res;
        int atlasId = tex != null ? tex.InstanceID : 0;
        float distance = 0f;
        if (camera != null)
        {
            Float3 pos = renderer.Transform.Position;
            Float3 cam = camera.Transform.Position;
            float dx = pos.X - cam.X;
            float dy = pos.Y - cam.Y;
            float dz = pos.Z - cam.Z;
            distance = dx * dx + dy * dy + dz * dz;
        }

        Renderers[renderer.InstanceID] = renderer;
        Collector.Add(new SpriteInstanceRecord(renderer.InstanceID, layer, order, distance, materialId, atlasId));
    }

    public static void EndCollect(List<IRenderable>? renderables = null)
    {
        Collector.SortAndBatch();
        LastInstanceCount = Collector.InstanceCount;
        LastBatchCount = Collector.BatchCount;
        RenderStats.RecordSprites(
            LastInstanceCount,
            culled: 0,
            LastInstanceCount,
            LastBatchCount,
            LastBatchCount,
            uploadBytes: 0,
            atlasSwitches: 0);

        if (renderables == null || LastBatchCount == 0)
            return;

        IReadOnlyList<SpriteBatchRange> batches = Collector.Batches;
        SpriteInstanceRecord[] sorted = Collector.Sorted;
        for (int b = 0; b < batches.Count; b++)
        {
            SpriteBatchRange range = batches[b];
            var instances = new InstanceData[range.Count];
            Float3 sortPos = default;
            Material? material = null;
            int layerIndex = 0;
            uint renderingLayers = 0xFFu;
            for (int i = 0; i < range.Count; i++)
            {
                SpriteInstanceRecord rec = sorted[range.Start + i];
                if (!Renderers.TryGetValue(rec.RendererId, out SpriteRenderer? spriteRenderer) || spriteRenderer == null)
                    continue;

                instances[i] = new InstanceData(BuildInstanceMatrix(spriteRenderer), spriteRenderer.Color);
                if (i == 0)
                {
                    sortPos = spriteRenderer.Transform.Position;
                    material = spriteRenderer.Material.Res;
                    layerIndex = spriteRenderer.GameObject.LayerIndex;
                    renderingLayers = spriteRenderer.RenderingLayers.Mask;
                }
            }

            renderables.Add(new SpriteBatchRenderable(
                range.MaterialId,
                range.AtlasId,
                instances,
                sortPos,
                material,
                layerIndex,
                renderingLayers));
        }
    }

    internal static bool UsesInstancedQuad(SpriteRenderer renderer)
    {
        if (renderer.DrawMode != SpriteDrawMode.Simple)
            return false;
        Sprite? sprite = renderer.Sprite.Res;
        if (sprite == null || sprite.Rect.Width <= 0 || sprite.Rect.Height <= 0)
            return false;
        int n = sprite.Vertices.Length;
        return n == 0 || n == 4;
    }

    private static Float4x4 BuildInstanceMatrix(SpriteRenderer renderer)
    {
        Sprite? sprite = renderer.Sprite.Res;
        float ppu = sprite != null && sprite.PixelsPerUnit > 0f ? sprite.PixelsPerUnit : 100f;
        float w = sprite != null ? sprite.Rect.Width / ppu : 1f;
        float h = sprite != null ? sprite.Rect.Height / ppu : 1f;
        if (renderer.Size.X > 0f) w = renderer.Size.X;
        if (renderer.Size.Y > 0f) h = renderer.Size.Y;
        Float2 pivot = sprite != null ? sprite.Pivot : new Float2(0.5f, 0.5f);

        SpriteSorting.Resolve(renderer, out int layer, out int order);
        Float4x4 world = renderer.Transform.LocalToWorldMatrix;
        world *= Float4x4.CreateTranslation(new Float3((0.5f - pivot.X) * w, (0.5f - pivot.Y) * h, (layer * 1000 + order) * SortBias));
        world *= Float4x4.CreateScale(new Float3(w, h, 1f));
        return world;
    }
}
