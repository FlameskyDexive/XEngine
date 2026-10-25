using XEngine.Editor;
using XEngine.Editor.Inspector;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Spine38;

namespace XEngine.Spine38.Editor;

[CustomAssetEditor(typeof(SpineAtlasAsset))]
public sealed class SpineAtlasAssetEditor : AssetImporterEditor
{
    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        id = $"{id}_{entry.Guid:N}";
        var atlas = asset as SpineAtlasAsset;
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Image}  Spine Atlas").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();
        if (atlas == null)
        {
            Origami.Label(paper, $"{id}_none", "SpineAtlasAsset failed to load.").Show();
            return;
        }

        Origami.Label(paper, $"{id}_pages",
            atlas.PageFileNames.Count == 0
                ? "Pages: (none)"
                : "Pages: " + string.Join(", ", atlas.PageFileNames)).Show();
        Origami.Label(paper, $"{id}_regions",
            atlas.RegionNames.Count == 0
                ? "Regions: (none)"
                : "Regions: " + string.Join(", ", atlas.RegionNames)).Show();
    }
}
