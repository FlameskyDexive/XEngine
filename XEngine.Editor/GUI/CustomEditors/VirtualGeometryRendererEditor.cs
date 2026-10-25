using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Rendering.VirtualGeometry;

using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;

namespace XEngine.Editor.Inspector;

// ================================================================
//  Virtual Geometry Renderer Component Editor
//  Draws the default fields, then a live stats section fed by the
//  baked feature data and the most recent per-view selection.
// ================================================================

[CustomEditor(typeof(VirtualGeometryRenderer))]
public class VirtualGeometryRendererEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var renderer = (VirtualGeometryRenderer)target;
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        Undo.SnapshotIfInteracting(renderer, paper);

        // Default inspector (mesh, materials, pixel error, GPU culling toggle, etc.).
        DrawDefaultInspector(paper, id, renderer);

        var data = renderer.Data;
        Origami.Separator(paper, $"{id}_vg_sep").Show();
        Origami.Header(paper, $"{id}_vg_header", "Virtual Geometry").Underline().Show();

        if (data == null)
        {
            Origami.Label(paper, $"{id}_vg_missing",
                "No baked virtual geometry data. Enable Virtual Geometry in the mesh import settings and reimport.").Show();
            return;
        }

        long totalClusters = data.Clusters.LongLength;
        long totalTris = 0;
        for (int l = 0; l < data.Levels.Length; l++)
            totalTris += data.Levels[l].TriangleCount;

        EditorGUI.Row(paper, $"{id}_vg_source", "Source", () =>
            Origami.Label(paper, $"{id}_vg_source_v",
                $"{data.SourceTriangleCount:N0} tris / {data.SourceVertexCount:N0} verts").Show());
        EditorGUI.Row(paper, $"{id}_vg_levels", "LOD levels", () =>
            Origami.Label(paper, $"{id}_vg_levels_v", $"{data.Levels.Length}").Show());
        EditorGUI.Row(paper, $"{id}_vg_clusters", "Clusters (all levels)", () =>
            Origami.Label(paper, $"{id}_vg_clusters_v",
                $"{totalClusters:N0} ({data.Levels[0].ClusterCount:N0} at level 0)").Show());
        EditorGUI.Row(paper, $"{id}_vg_tris", "Baked triangles (all levels)", () =>
            Origami.Label(paper, $"{id}_vg_tris_v", $"{totalTris:N0}").Show());
        EditorGUI.Row(paper, $"{id}_vg_sel", "Last selection", () =>
            Origami.Label(paper, $"{id}_vg_sel_v",
                $"{renderer.LastVisibleClusters:N0} clusters / {renderer.LastVisibleTriangles:N0} tris").Show());
        EditorGUI.Row(paper, $"{id}_vg_gpu", "Cull path", () =>
            Origami.Label(paper, $"{id}_vg_gpu_v",
                renderer.GpuCullingActive ? "GPU compute" : "CPU selector").Show());
    }
}
