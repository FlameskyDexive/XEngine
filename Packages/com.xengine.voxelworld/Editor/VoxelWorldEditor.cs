using System;

using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.VoxelWorld;

namespace XEngine.VoxelWorld.Editor;

[CustomEditor(typeof(VoxelWorld))]
public sealed class VoxelWorldEditor : CustomEditor
{
    public static string Summarize(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        string generator = world.UseEarthGenerator ? "Earth" : "Flat";
        int biomes = 0;
        if (world.World.ResWeak is WorldDefinition definition)
            biomes = definition.Biomes.Count;
        return $"Streaming {world.EnableStreaming}  Generator {generator}  View {world.ViewDistance}  Biomes {biomes}";
    }

    public override void OnGUI(Paper paper, string id, object target)
    {
        var world = (VoxelWorld)target;
        if (XEngine.Editor.Core.EditorApplication.PropertyGridConfig != null)
            DrawDefaultInspector(paper, id, world);
        Origami.Header(paper, id + "_vw_hdr", "Voxel World").Underline().Show();
        Origami.Label(paper, id + "_vw_sum", Summarize(world)).Show();
        Origami.Label(paper, id + "_vw_save", $"World dir {world.SaveFileName}  store {(world.RegionStore != null ? "on" : "off")}").Show();
        Origami.Label(paper, id + "_vw_hint", "Play: 1-8 hotbar  LMB break  RMB place  F5 save  F9 load").Show();
    }
}
