using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;

namespace XEngine.Editor.UI.Rendering;

/// <summary>Borrowed island adapter for Paper's ordered marker stream. The panel owns the
/// island and must invalidate host draw lists before closing it. Context changes require recapture.</summary>
public sealed class UiQuillCanvasLayer(UiQuillPaintIsland island, int width, int height,
    CanvasLayerContext capturedContext) : IUiCommandLayer
{
    private UiPaintEncoding _pending;
    public void Validate(in UiCommandLayerContext context)
    {
        if (_pending.IsValid) throw new InvalidOperationException("Layer already has a pending encoding.");
        if (width != context.Width || height != context.Height || !capturedContext.Equals(context.Canvas))
            throw new InvalidOperationException("Layer geometry context changed; recapture and replace the marker owner.");
        if (island.NeedsRecapture) throw new InvalidOperationException("Layer font atlas changed; repaint first.");
    }
    public UiRenderStats Encode(CommandBuffer command, in UiCommandLayerContext context)
    {
        Validate(context); _pending = island.Encode(command, context.Width, context.Height);
        var stats = _pending.Stats;
        return new UiRenderStats { UiDrawCalls = stats.Draws, VertexUploadBytes = stats.VertexBytes,
            IndexUploadBytes = stats.IndexBytes, BrushUploadBytes = stats.BrushBytes };
    }
    public void Complete(bool submitted)
    {
        try { if (_pending.IsValid) island.CompleteEncoding(_pending, submitted); }
        finally { _pending = default; }
    }
}
