using XEngine.Editor;
using XEngine.Editor.GUI;
using XEngine.Editor.Inspector;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.VFX;

namespace XEngine.VFX.Editor;

[CustomAssetEditor(typeof(VfxGraphAsset))]
public sealed class VfxGraphAssetEditor : AssetImporterEditor
{
    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        id = $"{id}_{entry.Guid:N}";
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.WandMagicSparkles}  Visual Effect Graph").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        var graph = asset as VfxGraphAsset;
        if (graph == null)
        {
            Origami.Label(paper, $"{id}_none", "VfxGraphAsset failed to load.").Show();
            return;
        }

        Origami.Label(paper, $"{id}_sum",
            $"{graph.GraphName}  {graph.Template}  contexts {graph.Contexts.Count}  blocks {graph.Blocks.Count}").Show();
        Origami.Button(paper, $"{id}_open", "Open Visual Effect Graph",
            () => VfxGraphWindow.OpenGraph(graph)).Show();
    }
}
