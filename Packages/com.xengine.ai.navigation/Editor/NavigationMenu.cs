using XEngine.AI.Navigation;
using XEngine.Editor;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Runtime.Navigation;

namespace XEngine.AI.Navigation.Editor;

internal static class NavigationMenu
{
    [MenuItem("GameObject/AI/NavMesh Surface", priority: 50, Icon = EditorIcons.Map)]
    static void CreateSurface()
    {
        var go = HierarchyPanel.CreateGameObject("NavMesh Surface", MenuContext.ActiveGameObject);
        go.AddComponent<NavMeshSurface>();
    }

    [MenuItem("GameObject/AI/NavMesh Modifier", priority: 51, Icon = EditorIcons.LayerGroup)]
    static void CreateModifier()
    {
        var go = HierarchyPanel.CreateGameObject("NavMesh Modifier", MenuContext.ActiveGameObject);
        go.AddComponent<NavMeshModifier>();
    }

    [MenuItem("GameObject/AI/NavMesh Modifier Volume", priority: 52, Icon = EditorIcons.Cube)]
    static void CreateVolume()
    {
        var go = HierarchyPanel.CreateGameObject("NavMesh Modifier Volume", MenuContext.ActiveGameObject);
        go.AddComponent<NavMeshModifierVolume>();
    }

    [MenuItem("GameObject/AI/NavMesh Link", priority: 53, Icon = EditorIcons.DiamondTurnRight)]
    static void CreateLink()
    {
        var go = HierarchyPanel.CreateGameObject("NavMesh Link", MenuContext.ActiveGameObject);
        go.AddComponent<NavMeshLink>();
    }

    [MenuItem("GameObject/AI/NavMesh Agent", priority: 54, Icon = EditorIcons.PersonWalking)]
    static void CreateAgent()
    {
        var go = HierarchyPanel.CreateGameObject("NavMesh Agent", MenuContext.ActiveGameObject);
        go.AddComponent<NavMeshAgent>();
    }

    [MenuItem("GameObject/AI/NavMesh Obstacle", priority: 56, Icon = EditorIcons.Cube)]
    static void CreateObstacle()
    {
        var go = HierarchyPanel.CreateGameObject("NavMesh Obstacle", MenuContext.ActiveGameObject);
        go.AddComponent<NavMeshObstacle>();
    }
}
