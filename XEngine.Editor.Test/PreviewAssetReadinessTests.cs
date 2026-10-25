using XEngine.Echo;
using XEngine.Editor.GUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class PreviewAssetReadinessTests
{
    private sealed class Backend : AssetBackendBase
    {
        internal readonly Dictionary<Guid, EngineObject> Sources = new();
        internal int Loads;
        protected override EngineObject? LoadFresh(Guid id)
        {
            Loads++;
            if (!Sources.TryGetValue(id, out var value)) return null;
            SetLoaded(id, value);
            return value;
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void OneShotPrefabSetupLoadsColdMaterialsBeforeFirstRender(bool skinned, int setup)
    {
        var previous = AssetDatabase.Current;
        bool asyncEnabled = AssetLoadingConfig.AsyncEnabled;
        var backend = new Backend();
        using var mesh = new Mesh { AssetID = Guid.NewGuid() };
        mesh.bounds = AABB.FromCenterAndSize(Float3.Zero, new Float3(4f));
        using var material = new Material { AssetID = Guid.NewGuid() };
        backend.Sources[mesh.AssetID] = mesh;
        backend.Sources[material.AssetID] = material;
        using var source = new GameObject("Source");
        var child = new GameObject("Renderer"); child.SetParent(source, false);
        if (skinned)
        {
            var renderer = child.AddComponent<SkinnedMeshRenderer>();
            renderer.SharedMesh = new AssetRef<Mesh>(mesh.AssetID);
            renderer.Materials.Add(new AssetRef<Material>(material.AssetID));
        }
        else
        {
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.Mesh = new AssetRef<Mesh>(mesh.AssetID);
            renderer.Materials.Add(new AssetRef<Material>(material.AssetID));
        }
        using var prefab = new PrefabAsset { GameObjectData = Serializer.Serialize(typeof(object), source) };
        AssetDatabase.Current = backend;
        AssetLoadingConfig.AsyncEnabled = true;
        try
        {
            Assert.Null(backend.GetCached(material.AssetID));
            using var preview = new PreviewRenderer();
            if (setup == 1) preview.SetupForModel(prefab);
            else if (setup == 2)
            {
                using var clip = new AnimationClip();
                preview.SetupForAnimationClip(clip, prefab);
            }
            else preview.SetupForPrefab(prefab);
            Assert.Same(material, backend.GetCached(material.AssetID));
            Assert.Same(mesh, backend.GetCached(mesh.AssetID));
            var normalized = setup == 2 ? preview.Subject!.Parent! : preview.Subject!;
            Assert.Equal(new Float3(0.25f), normalized.Transform.LocalScale);
            Assert.True(AssetLoadingConfig.AsyncEnabled);
            Assert.False(PreviewRenderer.ShouldRenderPreview(false, true, false));
        }
        finally
        {
            AssetLoader.Stop();
            AssetDatabase.Forget(mesh.AssetID); AssetDatabase.Forget(material.AssetID);
            AssetDatabase.Current = previous;
            AssetLoadingConfig.AsyncEnabled = asyncEnabled;
        }
    }

    [Fact]
    public void MaterialSphereLoadsColdShaderAndAllTextureKindsWithoutChangingAssetData()
    {
        var previous = AssetDatabase.Current;
        bool asyncEnabled = AssetLoadingConfig.AsyncEnabled;
        var backend = new Backend();
        using var material = new Material();
        using var shader = new Shader("Preview", [], []) { AssetID = Guid.NewGuid() };
        using var texture = new Texture2D { AssetID = Guid.NewGuid() };
        using var volume = new Texture3D { AssetID = Guid.NewGuid() };
        using var array = new Texture2DArray { AssetID = Guid.NewGuid() };
        using var cube = new Cubemap { AssetID = Guid.NewGuid() };
        typeof(Material).GetField("_shader", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(material, new AssetRef<Shader>(shader.AssetID));
        material._properties.SetTexture("albedo", new AssetRef<Texture2D>(texture.AssetID));
        material._properties.SetTexture3D("volume", new AssetRef<Texture3D>(volume.AssetID));
        material._properties.SetTexture2DArray("layers", new AssetRef<Texture2DArray>(array.AssetID));
        material._properties.SetTextureCube("environment", new AssetRef<Cubemap>(cube.AssetID));
        EngineObject[] assets = [shader, texture, volume, array, cube];
        foreach (var asset in assets) backend.Sources[asset.AssetID] = asset;
        string before = Serializer.Serialize(typeof(Material), material).WriteToString();
        AssetDatabase.Current = backend; AssetLoadingConfig.AsyncEnabled = true;
        try
        {
            using var preview = new PreviewRenderer();
            preview.SetupForMaterial(material);
            foreach (var asset in assets) Assert.Same(asset, backend.GetCached(asset.AssetID));
            Assert.Equal(before, Serializer.Serialize(typeof(Material), material).WriteToString());
            Assert.True(AssetLoadingConfig.AsyncEnabled);
            preview.SetupForMaterial(material);
            Assert.Equal(assets.Length, backend.Loads);
        }
        finally
        {
            AssetLoader.Stop();
            foreach (var asset in assets) AssetDatabase.Forget(asset.AssetID);
            AssetDatabase.Current = previous; AssetLoadingConfig.AsyncEnabled = asyncEnabled;
        }
        foreach (var asset in assets) Assert.False(asset.IsDisposed);
    }

    [Fact]
    public void MissingMaterialDoesNotPreventSetupOrTurnStaticPreviewIntoContinuousRendering()
    {
        var previous = AssetDatabase.Current;
        bool asyncEnabled = AssetLoadingConfig.AsyncEnabled;
        var backend = new Backend();
        using var subject = new GameObject("Missing material");
        subject.AddComponent<MeshRenderer>().Materials.Add(new AssetRef<Material>(Guid.NewGuid()));
        AssetDatabase.Current = backend; AssetLoadingConfig.AsyncEnabled = true;
        try
        {
            using var preview = new PreviewRenderer();
            preview.ReplaceSubject(subject);
            Assert.Same(subject, preview.Subject);
            Assert.Equal(1, backend.Loads);
            Assert.False(PreviewRenderer.ShouldRenderPreview(false, false, false));
            Assert.False(PreviewRenderer.ShouldRenderPreview(false, true, false));
        }
        finally
        {
            AssetLoader.Stop();
            AssetDatabase.Current = previous; AssetLoadingConfig.AsyncEnabled = asyncEnabled;
        }
    }
}
