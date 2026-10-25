using System;

using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.VoxelWorld;

namespace XEngine.VoxelWorld.Editor;

[CustomEditor(typeof(WorldDefinition))]
public sealed class WorldDefinitionEditor : CustomEditor
{
    public static string Summarize(WorldDefinition world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return $"Seed {world.Seed}  biomes {world.Biomes.Count}  extras {world.ExtraVoxels.Count}";
    }

    public override void OnGUI(Paper paper, string id, object target)
    {
        var world = (WorldDefinition)target;
        if (XEngine.Editor.Core.EditorApplication.PropertyGridConfig != null)
            DrawDefaultInspector(paper, id, world);
        Origami.Header(paper, id + "_wd_hdr", "World Definition").Underline().Show();
        Origami.Label(paper, id + "_wd_sum", Summarize(world)).Show();
        for (int i = 0; i < world.Biomes.Count; i++)
        {
            BiomeDefinition? biome = world.Biomes[i].ResWeak;
            string name = biome != null ? $"biome {i}: alt {biome.AltitudeMin:0.##}-{biome.AltitudeMax:0.##}" : $"biome {i}: unset";
            Origami.Label(paper, id + "_wd_b" + i, name).Show();
        }
    }
}
