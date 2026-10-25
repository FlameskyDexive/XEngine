using XEngine.AI.Navigation;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime.Navigation;

namespace XEngine.AI.Navigation.Editor;

[CustomEditor(typeof(NavMeshSurface))]
public sealed class NavMeshSurfaceEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var surface = (NavMeshSurface)target;
        DrawDefaultInspector(paper, $"{id}_def", target);

        Origami.Header(paper, $"{id}_h", "Bake").Underline().Show();
        Origami.Button(paper, $"{id}_bake", "Bake NavMesh", () =>
        {
            Undo.Snapshot(surface);
            surface.BuildNavMesh();
            EditorSceneManager.MarkDirty("Bake NavMesh", "editor");
        }).Show();

        if (surface.BakedData != null)
        {
            Origami.Button(paper, $"{id}_clear", "Clear", () =>
            {
                Undo.Snapshot(surface);
                surface.ClearBaked();
                EditorSceneManager.MarkDirty("Clear NavMesh", "editor");
            }).Show();

            NavMeshData data = surface.BakedData;
            Origami.Label(paper, $"{id}_info",
                $"{data.Tiles.Count} tile(s)  {data.TileData.Length} bytes  hash={data.SettingsHash}").Show();
        }
    }
}

[CustomEditor(typeof(NavMeshModifier))]
public sealed class NavMeshModifierEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target) => DrawDefaultInspector(paper, id, target);
}

[CustomEditor(typeof(NavMeshModifierVolume))]
public sealed class NavMeshModifierVolumeEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target) => DrawDefaultInspector(paper, id, target);
}

[CustomEditor(typeof(NavMeshLink))]
public sealed class NavMeshLinkEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target) => DrawDefaultInspector(paper, id, target);
}
