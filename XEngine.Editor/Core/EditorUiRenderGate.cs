namespace XEngine.Editor.Core;

/// <summary>
/// When to keep the editor Paper pass alive. Skip-present + UI-thread sleep after the
/// 5s background animation left File-menu catchers and widgets on a dead framebuffer —
/// Windows still reported Responding, but the window looked frozen.
/// </summary>
internal static class EditorUiRenderGate
{
    /// <summary>
    /// Seconds to keep presenting after a consumed click so IMGUI mutations applied after
    /// layout (tree expand, foldout, toggle) and their short <c>AnimateBool</c> windows paint
    /// without waiting for the next mouse move.
    /// </summary>
    public const double PaperClickFollowUpSeconds = 0.2;

    public static bool ShouldKeepUiLive(
        bool modalOrMenuOpen,
        bool windowGroupActive,
        bool pointerOverEditor)
        => modalOrMenuOpen || windowGroupActive || pointerOverEditor;

    /// <summary>
    /// Queue a repaint when a button edge is still unconsumed, or when the last Paper frame
    /// already consumed a click whose widget mutation has not been laid out yet.
    /// </summary>
    public static bool ShouldInvalidateForPaperInput(bool hasPendingButtonEdge, bool needsFollowUpFrame)
        => hasPendingButtonEdge || needsFollowUpFrame;

    public static bool ShouldRegisterClickFollowUpAnimation(bool needsFollowUpFrame)
        => needsFollowUpFrame;
}
