// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;

namespace XEngine.Editor.Inspector;

[CustomAssetEditor(typeof(ShaderGraphAsset))]
public sealed class ShaderGraphAssetEditor : AssetImporterEditor
{
    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        id = $"{id}_{entry.Guid:N}";
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.WandMagicSparkles}  Shader Graph").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        var graph = asset as ShaderGraphAsset;
        if (graph == null)
        {
            Origami.Label(paper, $"{id}_none", "ShaderGraphAsset failed to load.").Show();
            return;
        }

        Origami.Label(paper, $"{id}_sum",
            $"{graph.ShaderName}  {graph.Target}  nodes {graph.Nodes.Count}  edges {graph.Edges.Count}").Show();
        Origami.Button(paper, $"{id}_open", "Open Shader Graph Window",
            () => ShaderGraphWindow.OpenGraph(graph)).Show();
    }
}
