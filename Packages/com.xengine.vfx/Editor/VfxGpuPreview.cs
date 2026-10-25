using System;

using XEngine.Editor.GUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.VFX;

namespace XEngine.VFX.Editor;

/// <summary>
/// Isolated-scene GPU preview for a <see cref="VfxGraphAsset"/>: compute simulate + Output pass
/// into <see cref="PreviewRenderer"/>'s render target. Falls back to CPU when compute is unavailable.
/// </summary>
public static class VfxGpuPreview
{
    public static bool DeviceReady => VfxRuntime.DeviceSupportsCompute;

    public static void Setup(PreviewRenderer renderer, VfxGraphAsset graph)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(graph);

        var go = new GameObject("VfxPreview");
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = graph;
        vfx.PlayOnEnable = true;
        renderer.ReplaceSubject(go, AABB.FromCenterAndSize(Float3.Zero, new Float3(4f, 4f, 4f)));
        vfx.ApplyGraph();
        vfx.Play();
    }

    public static void Tick(PreviewRenderer renderer, float dt)
    {
        VisualEffect? vfx = renderer.Subject?.GetComponent<VisualEffect>();
        vfx?.Simulate(dt);
    }

    public static bool TryCaptureRgba(PreviewRenderer renderer, byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            return false;

        renderer.Resize(width, height);
        Tick(renderer, 1f / 30f);
        renderer.Render();

        Texture2D? tex = renderer.Result?.MainTexture;
        if (tex == null || tex.IsDisposed)
            return false;

        int size = tex.GetSize();
        if (size < 4)
            return false;

        byte[] data = new byte[size];
        tex.GetData<byte>(data);

        CopyOrScaleRgba(data, (int)tex.Width, (int)tex.Height, rgba, width, height);
        return true;
    }

    private static void CopyOrScaleRgba(byte[] src, int srcW, int srcH, byte[] dst, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH && src.Length >= dst.Length)
        {
            Buffer.BlockCopy(src, 0, dst, 0, dstW * dstH * 4);
            return;
        }

        for (int y = 0; y < dstH; y++)
        {
            int sy = Math.Clamp(y * srcH / dstH, 0, srcH - 1);
            for (int x = 0; x < dstW; x++)
            {
                int sx = Math.Clamp(x * srcW / dstW, 0, srcW - 1);
                int si = (sy * srcW + sx) * 4;
                int di = (y * dstW + x) * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
    }
}
