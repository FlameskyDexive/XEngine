using XEngine.Runtime;
using XEngine.Runtime.RHI;
using XEngine.Vector.Spatial;

namespace XEngine.Editor.GUI;

/// <summary>Maps a rendered camera target into Paper's top-down screen coordinates.</summary>
internal static class RenderTargetPreview
{
    internal static bool RequiresYFlip => Graphics.Device?.Backend != GraphicsBackend.Direct3D12;

    internal static Transform2D Transform(float x, float y, float width, float height) =>
        Transform(x, y, width, height, Graphics.Device?.Backend ?? GraphicsBackend.OpenGL);

    internal static Transform2D Transform(float x, float y, float width, float height, GraphicsBackend backend)
    {
        // D3D12 rasterizes targets top-down. OpenGL and the desktop Vulkan negative-height
        // viewport retain the engine's bottom-up convention. Imported images are separate.
        bool flip = backend != GraphicsBackend.Direct3D12;
        return Transform2D.CreateTranslation(x, flip ? y + height : y)
            * Transform2D.CreateScale(width, flip ? -height : height);
    }
}
