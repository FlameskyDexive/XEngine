using XEngine.Editor;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Spine38;

namespace XEngine.Spine38.Editor;

internal static class Spine38Menu
{
    [MenuItem("GameObject/Spine 3.8/Skeleton Animation", priority: 40, Icon = EditorIcons.PersonWalking)]
    public static void CreateSkeletonAnimation()
        => CreateSkeletonAnimationObject();

    [MenuItem("GameObject/Spine 3.8/Skeleton Renderer", priority: 41, Icon = EditorIcons.Bone)]
    public static void CreateSkeletonRenderer()
        => CreateSkeletonRendererObject();

    [MenuItem("GameObject/Spine 3.8/Skeleton Graphic", priority: 42, Icon = EditorIcons.Image)]
    public static void CreateSkeletonGraphic()
        => CreateSkeletonGraphicObject();

    public static GameObject CreateSkeletonAnimationObject()
    {
        GameObject go = HierarchyPanel.CreateGameObject("Skeleton Animation", MenuContext.ActiveGameObject);
        go.AddComponent<SkeletonAnimation>();
        return go;
    }

    public static GameObject CreateSkeletonRendererObject()
    {
        GameObject go = HierarchyPanel.CreateGameObject("Skeleton Renderer", MenuContext.ActiveGameObject);
        go.AddComponent<SkeletonRenderer>();
        return go;
    }

    public static GameObject CreateSkeletonGraphicObject()
    {
        GameObject go = HierarchyPanel.CreateGameObject("Skeleton Graphic", MenuContext.ActiveGameObject);
        go.AddComponent<SkeletonGraphic>();
        return go;
    }
}
