using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Vector;
using XEngine.VoxelWorld;
using XEngine.VoxelWorld.Editor;

using VoxelWorldHost = XEngine.VoxelWorld.VoxelWorld;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class VoxelWorldEditorTests : EditorTestHarness
{
    public VoxelWorldEditorTests()
    {
        EditorRegistries.Initialize();
    }

    [Fact]
    public void Registries_ResolveVoxelWorldEditors()
    {
        Assert.IsType<VoxelWorldEditor>(EditorRegistries.GetCustomEditor(typeof(VoxelWorldHost)));
        Assert.IsType<VoxelDefinitionEditor>(EditorRegistries.GetCustomEditor(typeof(VoxelDefinition)));
        Assert.IsType<WorldDefinitionEditor>(EditorRegistries.GetCustomEditor(typeof(WorldDefinition)));
    }

    [Fact]
    public void CreateMenu_AddsVoxelWorldEntries()
    {
        MenuRegistry.Clear();
        MenuItemAttribute.PopulateMenuRegistry();
        AppMenuItem gameObject = Assert.Single(MenuRegistry.RootMenus, item => item.Label == "GameObject");
        AppMenuItem voxel = Assert.Single(gameObject.SubItems, item => item.Label == "Voxel World");
        Assert.Contains(voxel.SubItems, item => item.Label == "Flat Playable");
        Assert.Contains(voxel.SubItems, item => item.Label == "Earth Playable");
        Assert.Contains(voxel.SubItems, item => item.Label == "First Person");
    }

    [Fact]
    public void Menus_CreateFlatAndEarthHosts()
    {
        GameObject flat = VoxelWorldMenus.CreateFlatPlayableObject();
        var world = flat.GetComponent<VoxelWorldHost>();
        Assert.NotNull(world);
        Assert.True(world.EnableStreaming);
        Assert.False(world.UseEarthGenerator);
        Assert.NotNull(flat.GetComponentInChildren<VoxelFirstPerson>());
        flat.Dispose();

        GameObject earth = VoxelWorldMenus.CreateEarthPlayableObject();
        Assert.True(earth.GetComponent<VoxelWorldHost>()!.UseEarthGenerator);
        Assert.NotNull(earth.GetComponent<VoxelDayCycle>());
        earth.Dispose();
    }

    [Fact]
    public void Inspectors_OnGUI_HeadlessPaper()
    {
        var go = new GameObject("VoxelWorldInspect");
        var host = go.AddComponent<VoxelWorldHost>();
        host.EnableStreaming = false;
        host.UseEarthGenerator = true;
        host.ViewDistance = 3;
        var definition = new VoxelDefinition { Title = "Stone", RenderType = VoxelRenderType.Opaque, Alpha = 1f };
        var world = new WorldDefinition { Seed = 7 };

        var paper = new Paper(new NullRenderer(), 360, 640, new FontAtlasSettings());
        paper.BeginFrame(0.016f);
        Origami.BeginFrame(paper, 0.016f);
        EditorRegistries.GetCustomEditor(typeof(VoxelWorldHost))!.OnGUI(paper, "vw", host);
        EditorRegistries.GetCustomEditor(typeof(VoxelDefinition))!.OnGUI(paper, "vd", definition);
        EditorRegistries.GetCustomEditor(typeof(WorldDefinition))!.OnGUI(paper, "wd", world);
        Origami.EndFrame(paper);
        paper.EndFrame();

        Assert.Contains("Earth", VoxelWorldEditor.Summarize(host), StringComparison.Ordinal);
        Assert.Contains("Stone", VoxelDefinitionEditor.Summarize(definition), StringComparison.Ordinal);
        Assert.Contains("Seed 7", WorldDefinitionEditor.Summarize(world), StringComparison.Ordinal);
        go.Dispose();
    }

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
