using System;

using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.VoxelWorld;

namespace XEngine.VoxelWorld.Editor;

[CustomEditor(typeof(VoxelDefinition))]
public sealed class VoxelDefinitionEditor : CustomEditor
{
    public static string Summarize(VoxelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return $"{definition.Title}  {definition.RenderType}  alpha {definition.Alpha:0.##}";
    }

    public override void OnGUI(Paper paper, string id, object target)
    {
        var definition = (VoxelDefinition)target;
        if (XEngine.Editor.Core.EditorApplication.PropertyGridConfig != null)
            DrawDefaultInspector(paper, id, definition);
        Origami.Header(paper, id + "_vd_hdr", "Voxel Definition").Underline().Show();
        Origami.Label(paper, id + "_vd_sum", Summarize(definition)).Show();
        if (definition.Hidden)
            Origami.Label(paper, id + "_vd_hidden", "Hidden from collect / inventory").Show();
    }
}
