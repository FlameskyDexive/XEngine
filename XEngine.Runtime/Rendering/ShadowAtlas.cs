// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.RHI;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

public static class ShadowAtlas
{
    private static int size;
    private static RenderTexture? atlas;
    private static ulong shadowCacheKey;
    private static bool shadowCacheValid;

    /// <summary>
    /// Preferred atlas edge length. 0 = auto (4096, capped by the device maximum).
    /// Editors should prefer 2048/4096 to cut idle VRAM and clear bandwidth.
    /// Applied on the next <see cref="TryInitialize"/>, including resizing an existing atlas.
    /// </summary>
    public static int PreferredSize { get; set; }

    // Simple Guillotine algorithm - maintains a list of free rectangles
    private struct FreeRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public FreeRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    private static List<FreeRect> freeRects = [];

    /// <summary>True when the GPU atlas texture has been created.</summary>
    public static bool IsInitialized => atlas.IsValid();

    public static bool IsShadowCacheValid(ulong key) => key != 0 && shadowCacheValid && shadowCacheKey == key;

    public static void MarkShadowCacheValid(ulong key)
    {
        shadowCacheKey = key;
        shadowCacheValid = key != 0;
    }

    public static void InvalidateShadowCache()
    {
        shadowCacheKey = 0;
        shadowCacheValid = false;
    }

    public static void TryInitialize()
    {
        int requestedSize = ResolveSize();
        if (atlas.IsValid() && size == requestedSize) return;

        // A quality change starts a new packing epoch: cached depth and tile locations belong
        // to the old texture. RenderTexture.Dispose queues backend disposal commands; explicit
        // backends retire the underlying resources after their submission fences complete.
        Dispose();
        size = requestedSize;

        atlas = new RenderTexture(size, size, true, []);

        ConfigureDepthSampler(atlas.InternalDepth);

        // Initialize with one large free rectangle covering the entire atlas
        freeRects.Clear();
        freeRects.Add(new FreeRect(0, 0, size, size));
    }

    private static int ResolveSize()
    {
        int max = Graphics.MaxTextureSize > 0 ? Graphics.MaxTextureSize : 4096;
        int preferred = PreferredSize;
        if (preferred > 0)
        {
            // Clamp to power-of-two-ish bounds used by lights; never exceed device max.
            preferred = Maths.Clamp(preferred, 512, max);
            return preferred;
        }

        // No explicit configuration: a conservative 4096 default. Sizing to the device maximum
        // (8192² on desktop = 256+ MB depth VRAM, full-atlas clear per cache miss) must be an
        // explicit QualitySettings/UniversalRenderPipelineAsset choice, never the fallback.
        return Maths.Min(4096, max);
    }

    public static int GetMinShadowSize() => 32; // Minimum shadow resolution
    public static int GetSize() => size;
    public static RenderTexture? GetAtlas() => atlas;

    // ---- Layered static/dynamic shadow cache ----
    // The static caster layer renders once per static cache key into the main atlas, is copied
    // into this cache texture, and every frame with dynamic casters restores it with one depth
    // blit before only the dynamic casters are re-drawn (Unity's cached shadow-atlas model).
    private static RenderTexture? staticCacheAtlas;
    private static ulong staticCacheKey;
    private static bool staticCacheValid;

    public static bool IsStaticCacheValid(ulong key) =>
        staticCacheValid && staticCacheKey == key && key != 0 && staticCacheAtlas.IsValid() && atlas.IsValid();

    public static void MarkStaticCacheValid(ulong key)
    {
        staticCacheKey = key;
        staticCacheValid = key != 0;
    }

    public static void InvalidateStaticCache() => staticCacheValid = false;

    /// <summary>The depth texture holding the cached static caster layer, matching the main
    /// atlas size. Created on first use; recreated when the atlas size changes.</summary>
    public static RenderTexture EnsureStaticCache()
    {
        if (staticCacheAtlas == null || !staticCacheAtlas.IsValid() || staticCacheAtlas.Width != size)
        {
            staticCacheAtlas?.Dispose();
            staticCacheAtlas = new RenderTexture(size, size, true, []);
            ConfigureDepthSampler(staticCacheAtlas.InternalDepth);
            staticCacheValid = false;
        }
        return staticCacheAtlas;
    }

    public static RenderTexture? GetStaticCacheAtlas() => staticCacheAtlas;

    // Modern shaders use explicit depth reads and comparisons; only GLSL declares sampler2DShadow.
    internal static bool UsesHardwareDepthComparison(GraphicsBackend backend) =>
        backend is GraphicsBackend.OpenGL or GraphicsBackend.WebGL2;

    // Mobile-family Vulkan drivers (Mali, Huawei Maleoon) and Intel Arc use positive viewport
    // height while other modern backends use the conventional flipped viewport — player
    // processes only (the editor's UI/GameView presentation still assumes the legacy
    // convention). Shadow sampling must mirror the orientation used to rasterize the atlas.
    // Keep in sync with VulkanGraphicsDevice.UsesPositiveViewportHeight.
    internal static bool UsesPositiveViewportHeight(GraphicsBackend backend, string backendName) =>
        backend == GraphicsBackend.Vulkan &&
        Application.IsPlayerBuild
#if !XENGINE_IOS
        && Backends.Vulkan.VulkanGraphicsDevice.UsesPositiveViewportHeightConvention(backendName)
#endif
        ;

    internal static float GetProjectedYScale()
    {
        var device = Graphics.Device;
        return device != null && UsesPositiveViewportHeight(device.Backend, device.Capabilities.BackendName)
            ? 0.5f
            : -0.5f;
    }

    internal static void ConfigureDepthSampler(Texture2D depthTexture)
    {
        ArgumentNullException.ThrowIfNull(depthTexture);
        depthTexture.SetTextureFilters(TextureMin.Linear, TextureMag.Linear);
        if (UsesHardwareDepthComparison(Graphics.Device?.Backend ?? GraphicsBackend.Null))
            depthTexture.SetDepthCompareMode(true);
    }

    public static Int2? ReserveTiles(int width, int height, int lightID)
    {
        // Callers only reach here when shadows are needed; create atlas lazily.
        TryInitialize();

        // Clamp to min/max bounds
        width = Maths.Max(width, 32);
        height = Maths.Max(height, 32);

        if (width > size || height > size)
            return null;

        // Find the best free rectangle using Best-Short-Side-Fit (BSSF) heuristic
        int bestIndex = -1;
        int bestShortSideFit = int.MaxValue;
        int bestLongSideFit = int.MaxValue;

        for (int i = 0; i < freeRects.Count; i++)
        {
            FreeRect rect = freeRects[i];

            // Check if the rectangle fits
            if (rect.Width >= width && rect.Height >= height)
            {
                int leftoverX = rect.Width - width;
                int leftoverY = rect.Height - height;
                int shortSideFit = Maths.Min(leftoverX, leftoverY);
                int longSideFit = Maths.Max(leftoverX, leftoverY);

                if (shortSideFit < bestShortSideFit ||
                    (shortSideFit == bestShortSideFit && longSideFit < bestLongSideFit))
                {
                    bestIndex = i;
                    bestShortSideFit = shortSideFit;
                    bestLongSideFit = longSideFit;
                }
            }
        }

        if (bestIndex == -1)
            return null; // No suitable space found

        // Place the rectangle in the best location
        FreeRect chosen = freeRects[bestIndex];
        int placedX = chosen.X;
        int placedY = chosen.Y;

        // Split the chosen rectangle using the Guillotine method
        // We split along the shorter leftover axis for better space utilization
        List<FreeRect> newRects = new List<FreeRect>();

        int leftoverWidth = chosen.Width - width;
        int leftoverHeight = chosen.Height - height;

        if (leftoverWidth > 0 && leftoverHeight > 0)
        {
            // Both dimensions have leftover space - split into two rectangles
            if (leftoverWidth <= leftoverHeight)
            {
                // Horizontal split
                newRects.Add(new FreeRect(chosen.X + width, chosen.Y, leftoverWidth, chosen.Height));
                newRects.Add(new FreeRect(chosen.X, chosen.Y + height, width, leftoverHeight));
            }
            else
            {
                // Vertical split
                newRects.Add(new FreeRect(chosen.X, chosen.Y + height, chosen.Width, leftoverHeight));
                newRects.Add(new FreeRect(chosen.X + width, chosen.Y, leftoverWidth, height));
            }
        }
        else if (leftoverWidth > 0)
        {
            // Only horizontal space left
            newRects.Add(new FreeRect(chosen.X + width, chosen.Y, leftoverWidth, chosen.Height));
        }
        else if (leftoverHeight > 0)
        {
            // Only vertical space left
            newRects.Add(new FreeRect(chosen.X, chosen.Y + height, chosen.Width, leftoverHeight));
        }

        // Remove the used rectangle and add the new free rectangles
        freeRects.RemoveAt(bestIndex);
        freeRects.AddRange(newRects);

        // TODO: Merge adjacent rectangles to reduce fragmentation

        return new Int2(placedX, placedY);
    }

    /// <summary>
    /// Reset packing free-rects only. No-op if the atlas has not been created yet
    /// (empty scenes never pay for 4K/8K allocation).
    /// </summary>
    public static void Clear()
    {
        if (!atlas.IsValid())
            return;

        InvalidateShadowCache();
        // Clearing resets tile packing, so the static-layer depth (whose tiles were placed by a
        // previous packing epoch) can no longer be composited over this atlas.
        InvalidateStaticCache();

        // Reset to initial state with one large free rectangle
        freeRects.Clear();
        freeRects.Add(new FreeRect(0, 0, size, size));
    }

    internal static void Dispose()
    {
        InvalidateStaticCache();
        staticCacheAtlas?.Dispose();
        staticCacheAtlas = null;
        atlas?.Dispose();
        atlas = null;
        size = 0;
        freeRects.Clear();
        InvalidateShadowCache();
    }
}
