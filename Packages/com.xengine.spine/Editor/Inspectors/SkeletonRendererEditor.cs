using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Spine;

namespace XEngine.Spine.Editor;

[CustomEditor(typeof(SkeletonRenderer))]
public sealed class SkeletonRendererEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var renderer = (SkeletonRenderer)target;
        Undo.SnapshotIfInteracting(renderer);
        if (EditorTheme.DefaultFont != null)
            DrawDefaultInspector(paper, $"{id}_def", target);
        Origami.Label(paper, $"{id}_hint",
            renderer.Skeleton != null
                ? $"Pose ready  batches {renderer.MeshBatchCount}"
                : "Assign a Skeleton Data Asset to pose this renderer.").Show();
    }
}
