namespace XEngine.Editor.Core;

/// <summary>Shared visibility gate for Scene/Game View camera submissions.</summary>
public static class EditorViewportRenderGate
{
    public static bool ShouldRender(
        bool panelVisible,
        bool hostVisible,
        int width,
        int height,
        bool scheduled)
        => panelVisible && hostVisible && width > 0 && height > 0 && scheduled;
}
