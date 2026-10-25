using XEngine.Editor;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;

namespace XEngine.Terrain.Editor;

internal static class TerrainMenu
{
    [MenuItem("GameObject/3D Object/Terrain", priority: 25, Icon = EditorIcons.Mountain)]
    static void CreateTerrain()
    {
        var go = HierarchyPanel.CreateGameObject("Terrain", MenuContext.ActiveGameObject);
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.Material = new AssetRef<Material>(TerrainShaderLibrary.TerrainMaterialGuid);
        go.AddComponent<TerrainCollider>();

        var terrainData = new TerrainData();
        terrain.Data = new AssetRef<TerrainData>(terrainData);

        var db = EditorAssetBackend.Instance;
        if (db != null)
        {
            string name = AssetCreateMenu.FindUniqueName(Project.Current.AssetsPath, "New Terrain Data", ".terraindata");
            db.CreateAsset(terrainData, name);
        }
    }

    [MenuItem("GameObject/3D Object/Wind Zone", priority: 26, Icon = EditorIcons.Wind)]
    static void CreateWindZone()
    {
        var go = HierarchyPanel.CreateGameObject("Wind Zone", MenuContext.ActiveGameObject);
        go.AddComponent<WindZone>();
    }
}
