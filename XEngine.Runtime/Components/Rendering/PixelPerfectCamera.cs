// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>Integer viewport used by Game and Scene pixel-perfect previews.</summary>
public readonly struct PixelPerfectPreview
{
    public PixelPerfectPreview(int x, int y, int width, int height, bool cropped)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Cropped = cropped;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public bool Cropped { get; }
}

/// <summary>
/// Snaps an orthographic camera to an integer pixel zoom of a reference resolution (M22).
/// When <see cref="Upscale"/> is set, the camera renders at <see cref="RefResolutionX"/>×
/// <see cref="RefResolutionY"/> and the pipeline blits to the window (M23).
/// </summary>
[AddComponentMenu("Rendering/Pixel Perfect Camera")]
[ComponentIcon("\uf24d")]
public sealed class PixelPerfectCamera : MonoBehaviour
{
    /// <summary>Sprite pixels that map to one world unit (matches <see cref="Sprite.PixelsPerUnit"/>).</summary>
    [SerializeField] public int AssetsPPU = 100;

    [SerializeField] public int RefResolutionX = 320;
    [SerializeField] public int RefResolutionY = 180;

    /// <summary>Snap the camera XY position to whole pixels after zoom is chosen.</summary>
    [SerializeField] public bool Snap = true;

    /// <summary>
    /// Render at the reference resolution and let the pipeline upsample to the window.
    /// Default false keeps M22 1:1 pixel mapping.
    /// </summary>
    [SerializeField] public bool Upscale;

    /// <summary>Crop the viewport to an integer multiple of the reference resolution.</summary>
    [SerializeField] public bool CropFrame;

    /// <summary>Stretch the view to fill the window instead of keeping integer pixel zoom.</summary>
    [SerializeField] public bool StretchFill;

    /// <summary>Last integer zoom applied by <see cref="Apply"/> (1 = 1:1 with the reference).</summary>
    public int Zoom { get; private set; } = 1;

    public override void OnValidate()
    {
        if (AssetsPPU < 1)
            AssetsPPU = 1;
        if (RefResolutionX < 1)
            RefResolutionX = 1;
        if (RefResolutionY < 1)
            RefResolutionY = 1;
    }

    /// <summary>
    /// Integer zoom that fits <paramref name="pixelWidth"/>×<paramref name="pixelHeight"/> into the
    /// reference resolution (at least 1).
    /// </summary>
    public int ComputeZoom(int pixelWidth, int pixelHeight)
    {
        OnValidate();
        if (pixelWidth < 1)
            pixelWidth = 1;
        if (pixelHeight < 1)
            pixelHeight = 1;
        int zoomX = Math.Max(1, pixelWidth / RefResolutionX);
        int zoomY = Math.Max(1, pixelHeight / RefResolutionY);
        return Math.Max(1, Math.Min(zoomX, zoomY));
    }

    /// <summary>
    /// Full ortho height in world units for the given pixel height and zoom
    /// (<see cref="Camera.OrthographicSize"/> is the DirectX full height, not Unity half-height).
    /// </summary>
    public float ComputeOrthographicSize(int pixelHeight, int zoom)
    {
        OnValidate();
        if (pixelHeight < 1)
            pixelHeight = 1;
        if (zoom < 1)
            zoom = 1;
        return pixelHeight / (float)(AssetsPPU * zoom);
    }

    public void Apply(Camera camera, int pixelWidth, int pixelHeight)
    {
        if (camera == null)
            return;

        Zoom = ComputeZoom(pixelWidth, pixelHeight);
        camera.ProjectionMode = Camera.ProjectionType.Orthographic;
        int orthoHeight = Upscale ? RefResolutionY : pixelHeight;
        int zoomForOrtho = Upscale ? 1 : Zoom;
        camera.OrthographicSize = ComputeOrthographicSize(orthoHeight, zoomForOrtho);
        if (!ShouldSnapPosition())
            return;

        float unitsPerPixel = camera.OrthographicSize / MathF.Max(orthoHeight, 1);
        if (unitsPerPixel <= 0f || !float.IsFinite(unitsPerPixel))
            return;

        Float3 p = camera.Transform.Position;
        p.X = MathF.Round(p.X / unitsPerPixel) * unitsPerPixel;
        p.Y = MathF.Round(p.Y / unitsPerPixel) * unitsPerPixel;
        camera.Transform.Position = p;
    }

    /// <summary>True when the camera XY position should snap to whole pixels.</summary>
    public bool ShouldSnapPosition() => Snap && !StretchFill;

    /// <summary>
    /// When <see cref="CropFrame"/> is on, returns the integer-pixel viewport (reference × zoom)
    /// that fits inside the window. Otherwise returns the full window size.
    /// </summary>
    public bool TryGetCroppedViewport(int pixelWidth, int pixelHeight, out int width, out int height)
    {
        OnValidate();
        if (pixelWidth < 1) pixelWidth = 1;
        if (pixelHeight < 1) pixelHeight = 1;
        if (!CropFrame)
        {
            width = pixelWidth;
            height = pixelHeight;
            return false;
        }

        int zoom = ComputeZoom(pixelWidth, pixelHeight);
        width = Math.Min(pixelWidth, RefResolutionX * zoom);
        height = Math.Min(pixelHeight, RefResolutionY * zoom);
        return true;
    }

    /// <summary>Game view applies crop; Scene view keeps the full framebuffer so unused pixels stay visible.</summary>
    public PixelPerfectPreview GetPreview(int pixelWidth, int pixelHeight, bool sceneView)
    {
        OnValidate();
        if (pixelWidth < 1) pixelWidth = 1;
        if (pixelHeight < 1) pixelHeight = 1;
        if (sceneView || !CropFrame)
            return new PixelPerfectPreview(0, 0, pixelWidth, pixelHeight, cropped: false);

        TryGetCroppedViewport(pixelWidth, pixelHeight, out int width, out int height);
        int x = (pixelWidth - width) / 2;
        int y = (pixelHeight - height) / 2;
        return new PixelPerfectPreview(x, y, width, height, cropped: true);
    }

    /// <summary>Apply from the camera's current target / window size. No-ops without a camera.</summary>
    public static void TryApply(Camera camera)
    {
        if (camera == null)
            return;
        PixelPerfectCamera? ppc = camera.GetComponent<PixelPerfectCamera>();
        if (ppc == null || !ppc.Enabled)
            return;

        int width;
        int height;
        RenderTexture? target = camera.Target;
        if (target != null && target.IsValid())
        {
            width = target.Width;
            height = target.Height;
        }
        else
        {
            try
            {
                var size = Window.InternalWindow.FramebufferSize;
                width = size.X;
                height = size.Y;
            }
            catch (Exception)
            {
                return;
            }
        }

        ppc.Apply(camera, width, height);
    }

    /// <summary>
    /// Scale that maps the current viewrect to <see cref="RefResolutionX"/> when
    /// <see cref="Upscale"/> is enabled. Returns false when upsample is off.
    /// </summary>
    public static bool TryGetUpscaleRenderScale(Camera camera, out float scale)
    {
        scale = 1f;
        if (camera == null)
            return false;
        PixelPerfectCamera? ppc = camera.GetComponent<PixelPerfectCamera>();
        if (ppc == null || !ppc.Enabled || !ppc.Upscale)
            return false;

        int width;
        int height;
        RenderTexture? target = camera.Target;
        if (target != null && target.IsValid())
        {
            width = target.Width;
            height = target.Height;
        }
        else
        {
            try
            {
                var size = Window.InternalWindow.FramebufferSize;
                width = size.X;
                height = size.Y;
            }
            catch (Exception)
            {
                return false;
            }
        }

        scale = ppc.ComputeUpscaleRenderScale(width, height);
        return true;
    }

    /// <summary>RenderScale multiplier so <see cref="Camera.PixelWidth"/> matches <see cref="RefResolutionX"/>.</summary>
    public float ComputeUpscaleRenderScale(int pixelWidth, int pixelHeight)
    {
        OnValidate();
        if (!Upscale)
            return 1f;
        _ = pixelHeight;
        return RefResolutionX / (float)Math.Max(1, pixelWidth);
    }
}
