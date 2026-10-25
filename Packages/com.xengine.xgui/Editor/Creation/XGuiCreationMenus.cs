using XEngine.Editor;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;

namespace XEngine.XGUI.Editor;

/// <summary>
/// Package-owned standard UI creation entries. Control hierarchies and defaults are declared in
/// package templates; this layer only chooses the parent Canvas and supplies editor side effects.
/// </summary>
internal static class XGuiCreationMenus
{
    [MenuItem("GameObject/UI/Canvas", priority: 70, Icon = EditorIcons.BorderAll)]
    private static void CreateCanvas() => XGuiTemplateFactory.Create("Canvas", MenuContext.ActiveGameObject);

    [MenuItem("GameObject/UI/Text", priority: 71, Icon = EditorIcons.Font)]
    private static void CreateText() => CreateControl("Text");

    [MenuItem("GameObject/UI/Image", priority: 72, Icon = EditorIcons.Image)]
    private static void CreateImage() => CreateControl("Image");

    [MenuItem("GameObject/UI/Raw Image", priority: 73, Icon = EditorIcons.Image)]
    private static void CreateRawImage() => CreateControl("RawImage");

    [MenuItem("GameObject/UI/Button", priority: 74, Icon = EditorIcons.MobileButton)]
    private static void CreateButton() => CreateControl("Button");

    [MenuItem("GameObject/UI/Toggle", priority: 75, Icon = EditorIcons.Check)]
    private static void CreateToggle() => CreateControl("Toggle");

    [MenuItem("GameObject/UI/Toggle Group", priority: 76, Icon = EditorIcons.List)]
    private static void CreateToggleGroup() => CreateControl("ToggleGroup");

    [MenuItem("GameObject/UI/Panel", priority: 77, Icon = EditorIcons.WindowMaximize)]
    private static void CreatePanel() => CreateControl("Panel");

    [MenuItem("GameObject/UI/Safe Area", priority: 77, Icon = EditorIcons.Crop)]
    private static void CreateSafeArea() => CreateControl("SafeArea");

    [MenuItem("GameObject/UI/Slider", priority: 78, Icon = EditorIcons.Sliders)]
    private static void CreateSlider() => CreateControl("Slider");

    [MenuItem("GameObject/UI/Scrollbar", priority: 79, Icon = EditorIcons.RectangleList)]
    private static void CreateScrollbar() => CreateControl("Scrollbar");

    [MenuItem("GameObject/UI/Scroll View", priority: 80, Icon = EditorIcons.RectangleList)]
    private static void CreateScrollView() => CreateControl("ScrollView");

    [MenuItem("GameObject/UI/Rect Mask 2D", priority: 81, Icon = EditorIcons.Square)]
    private static void CreateRectMask() => CreateControl("RectMask2D");

    [MenuItem("GameObject/UI/Mask", priority: 81, Icon = EditorIcons.Square)]
    private static void CreateMask() => CreateControl("Mask");

    [MenuItem("GameObject/UI/Input Field", priority: 82, Icon = EditorIcons.Keyboard)]
    private static void CreateInputField() => CreateControl("InputField");

    [MenuItem("GameObject/UI/Dropdown", priority: 83, Icon = EditorIcons.ChevronDown)]
    private static void CreateDropdown() => CreateControl("Dropdown");

    [MenuItem("GameObject/UI/Raycast Receiver", priority: 84, Icon = EditorIcons.Bullseye)]
    private static void CreateRaycastReceiver() => CreateControl("RaycastReceiver");

    [MenuItem("GameObject/UI/Event System", priority: 90, Icon = EditorIcons.ArrowPointer, Separator = true)]
    private static void CreateEventSystem() => XGuiTemplateFactory.Create("EventSystem", MenuContext.ActiveGameObject);

    private static void CreateControl(string templateName)
    {
        GameObject parent = ResolveCanvasParent(MenuContext.ActiveGameObject);
        EnsureEventSystem(parent.Scene);
        XGuiTemplateFactory.Create(templateName, parent);
    }

    private static GameObject ResolveCanvasParent(GameObject? candidate)
    {
        if (candidate?.GetComponentInParent<GameCanvas>(includeSelf: true) != null)
            return candidate;

        Scene? scene = Scene.Current;
        if (scene != null)
        {
            foreach (GameCanvas canvas in scene.FindObjectsOfType<GameCanvas>())
                if (canvas != null) return canvas.GameObject;
        }

        return XGuiTemplateFactory.Create("Canvas", null);
    }

    private static void EnsureEventSystem(Scene? scene)
    {
        if (scene == null) return;
        foreach (EventSystem system in scene.FindObjectsOfType<EventSystem>())
            if (system != null) return;

        XGuiTemplateFactory.Create("EventSystem", null);
    }
}
