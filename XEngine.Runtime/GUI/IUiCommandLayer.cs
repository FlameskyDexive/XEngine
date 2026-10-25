using XEngine.Quill;

namespace XEngine.Runtime.GUI;

public readonly record struct UiCommandLayerContext(int Width, int Height, CanvasLayerContext Canvas);

/// <summary>Ordered Paper renderer extension. Encode must not submit, switch targets or
/// mutate the source draw list. It must roll back its own partial encoding on failure.
/// The host calls Complete exactly once for each successfully encoded layer.</summary>
public interface IUiCommandLayer : ICanvasRenderLayer
{
    void Validate(in UiCommandLayerContext context);
    UiRenderStats Encode(CommandBuffer command, in UiCommandLayerContext context);
    void Complete(bool submitted);
}
