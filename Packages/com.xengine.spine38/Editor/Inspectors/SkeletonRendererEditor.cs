using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Spine38;

namespace XEngine.Spine38.Editor;

[CustomEditor(typeof(SkeletonRenderer))]
public sealed class SkeletonRendererEditor : CustomEditor
{
    private readonly SpineInspectorPreview _preview = new();

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

        SkeletonDataAsset? data = renderer.SkeletonDataAsset.ResWeak ?? renderer.SkeletonDataAsset.Res;
        _preview.Draw(paper, $"{id}_pv", data, animationName: null, skinName: null);
    }
}
