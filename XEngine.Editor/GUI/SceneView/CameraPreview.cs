using System;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// Renders a scene <see cref="Camera"/> into its own small render target so the Scene view can show
/// what that camera actually sees. The camera's real target is swapped in and out around the render,
/// the same way the Game view borrows scene cameras.
/// </summary>
internal sealed class CameraPreview : IDisposable
{
    private RenderTexture? _renderTarget;

    /// <summary>The camera rendered by the most recent <see cref="Render"/> call.</summary>
    public Camera? LastCamera { get; private set; }

    public Texture2D? Texture
    {
        get
        {
            if (_renderTarget == null || _renderTarget.IsDisposed) return null;
            Texture2D texture = _renderTarget.MainTexture;
            return texture.IsDisposed ? null : texture;
        }
    }

    public void Render(Camera camera, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (camera.IsDisposed || camera.GameObject == null) return;

        EnsureRenderTarget(width, height);
        if (_renderTarget == null) return;

        RenderTexture? original = camera.Target;
        try
        {
            camera.Target = _renderTarget;
            camera.UpdateRenderData();
            var pipeline = camera.Pipeline ?? UniversalRenderPipeline.Default;
            pipeline.Render(camera, new RenderingData());
            LastCamera = camera;
        }
        catch (Exception exception)
        {
            // A broken camera must not take the Scene view down with it; drop the preview instead.
            Debug.LogError($"[SceneView] Camera preview render failed: {exception.Message}");
            LastCamera = null;
        }
        finally
        {
            camera.Target = original;
            camera.UpdateRenderData();
        }
    }

    private void EnsureRenderTarget(int width, int height)
    {
        if (_renderTarget != null && !_renderTarget.IsDisposed
            && _renderTarget.Width == width && _renderTarget.Height == height)
            return;

        if (_renderTarget != null)
            Graphics.DeferDispose(_renderTarget);
        _renderTarget = new RenderTexture(width, height, true, new[] { TextureImageFormat.Color4b });
    }

    public void Dispose()
    {
        if (_renderTarget != null)
            Graphics.DeferDispose(_renderTarget);
        _renderTarget = null;
        LastCamera = null;
    }
}
