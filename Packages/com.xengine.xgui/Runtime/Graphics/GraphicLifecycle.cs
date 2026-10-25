using XEngine.Runtime.UI;

namespace XEngine.Runtime.UI;

internal static class GraphicLifecycle
{
    public static void Register(Graphic graphic) => GraphicRegistry.RegisterGraphic(graphic);
    public static void Unregister(Graphic graphic) => GraphicRegistry.UnregisterGraphic(graphic);
}
