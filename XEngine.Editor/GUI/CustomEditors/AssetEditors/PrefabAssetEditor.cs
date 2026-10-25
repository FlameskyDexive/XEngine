using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Inspector;

[CustomAssetEditor(typeof(PrefabAsset))]
public class PrefabAssetEditor : AssetImporterEditor
{
    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        var prefab = asset as PrefabAsset;
        if (prefab == null) return;

        Origami.Header(paper, $"{id}_hdr", $"{EditorIcons.Cubes}  Prefab: {prefab.Name}").Underline().Show();

        if (prefab.GameObjectData != null)
            Origami.Label(paper, $"{id}_info", "Contains a serialized GameObject hierarchy.").Show();
        else
            Origami.Label(paper, $"{id}_empty", "Empty prefab (no data).").Show();

        paper.Box($"{id}_sp").Height(8);

        Origami.Button(paper, $"{id}_edit", $"{EditorIcons.PenToSquare}  Open Prefab", () => { PrefabEditingMode.Enter(entry.Guid); }).Width(140).Show();
        // Preview is rendered by the Inspector's universal preview bar.
    }
}
