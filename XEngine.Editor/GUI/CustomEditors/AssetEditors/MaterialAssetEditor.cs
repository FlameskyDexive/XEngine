using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Inspector;

[CustomAssetEditor(typeof(Material))]
public class MaterialAssetEditor : AssetImporterEditor
{
    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        id = $"{id}_{entry.Guid:N}";

        Origami.Header(paper, $"{id}_h_info", $"{EditorIcons.Palette}  Material").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        if (asset is not Material material) return;

        Origami.Separator(paper, $"{id}_sep_shader").Show();
        MaterialEditorGUI.Draw(paper, id, material);
    }
}
