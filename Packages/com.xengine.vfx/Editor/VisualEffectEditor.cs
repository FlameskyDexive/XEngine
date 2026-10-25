using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.VFX;

namespace XEngine.VFX.Editor;

[CustomEditor(typeof(VisualEffect))]
public sealed class VisualEffectEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var vfx = (VisualEffect)target;
        DrawDefaultInspector(paper, $"{id}_def", target);
        Origami.Button(paper, $"{id}_open", "Open Graph", () =>
        {
            if (vfx.Graph.Res is VfxGraphAsset graph)
                VfxGraphWindow.OpenGraph(graph);
        }).Show();
        Origami.Button(paper, $"{id}_apply", "Apply Graph Settings", () =>
        {
            Undo.Snapshot(vfx);
            vfx.ApplyGraph();
        }).Show();
    }
}
