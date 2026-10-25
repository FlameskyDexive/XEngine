using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Inspector;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class GameObjectInspectorMaterialTests
{
    [Fact]
    public void CollectRendererMaterials_EmptyWithoutRenderer()
    {
        using var go = new GameObject("Empty");
        var dest = new List<Material>();
        GameObjectInspector.CollectRendererMaterials(go, dest);
        Assert.Empty(dest);
    }

    [Fact]
    public void CollectRendererMaterials_DedupesSharedAndSameAssetId()
    {
        using var go = new GameObject("Cube");
        var renderer = go.AddComponent<MeshRenderer>();
        Guid id = Guid.NewGuid();
        var shared = new Material { Name = "Shared", AssetID = id };
        var alias = new Material { Name = "Alias", AssetID = id };
        renderer.Materials.Add(new AssetRef<Material>(shared));
        renderer.Materials.Add(new AssetRef<Material>(shared));
        renderer.Materials.Add(new AssetRef<Material>(alias));

        var dest = new List<Material>();
        GameObjectInspector.CollectRendererMaterials(go, dest);
        Assert.Single(dest);
        Assert.Same(shared, dest[0]);
    }

    [Fact]
    public void CollectRendererMaterials_IncludesSkinnedMeshRenderer()
    {
        using var go = new GameObject("Skin");
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        var mat = new Material { Name = "SkinMat" };
        renderer.Materials.Add(new AssetRef<Material>(mat));

        var dest = new List<Material>();
        GameObjectInspector.CollectRendererMaterials(go, dest);
        Assert.Single(dest);
        Assert.Same(mat, dest[0]);
    }

    [Fact]
    public void CollectRendererMaterials_NullGoClearsDest()
    {
        var dest = new List<Material> { new Material { Name = "Keep" } };
        GameObjectInspector.CollectRendererMaterials(null!, dest);
        Assert.Empty(dest);
    }

    [Fact]
    public void ShouldPreviewSelection_GameObject_IsFalse()
    {
        using var go = new GameObject("Cube");
        Assert.False(InspectorPanel.ShouldPreviewSelection(go));
        Assert.False(InspectorPanel.ShouldPreviewSelection(null));
    }

    [Fact]
    public void ShouldPreviewSelection_ContentItem_IsTrue()
    {
        var item = new ContentItem { Name = "Foo.mat", RelativePath = "Mats/Foo.mat" };
        Assert.True(InspectorPanel.ShouldPreviewSelection(item));
    }
}
