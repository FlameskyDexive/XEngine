using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ParticleMaterialStreamingTests : RuntimeTestBase
{
    private sealed class Backend : AssetBackendBase
    {
        public readonly List<Guid> Requested = new();
        public void Seed(Guid id, EngineObject value) => SetLoaded(id, value);
        protected override EngineObject? LoadFresh(Guid id) { Requested.Add(id); return null; }
    }

    [Fact]
    public void MaterialRequestsAllMasksAndRecoversWhenTheyArrive()
    {
        var previous = AssetDatabase.Current;
        bool previousAsync = AssetLoadingConfig.AsyncEnabled;
        var backend = new Backend();
        AssetDatabase.Current = backend;
        AssetLoadingConfig.AsyncEnabled = false;
        Guid mainId = Guid.NewGuid(), maskId = Guid.NewGuid();
        using var material = new Material();
        using var main = new Texture2D(2, 2);
        using var mask = new Texture2D(2, 2);
        var scene = CreateScene();
        try
        {
            material.SetTexture("_MainTex", new AssetRef<Texture2D>(mainId));
            material.SetTexture("_Mask", new AssetRef<Texture2D>(maskId));
            material.SetTexture("_Noise", default(AssetRef<Texture2D>));
            Assert.False(material.PrepareRenderAssets(scene));
            Assert.Contains(mainId, backend.Requested);
            Assert.Contains(maskId, backend.Requested);
            Assert.True(AssetDatabase.IsLocked(mainId));
            Assert.True(AssetDatabase.IsLocked(maskId));
            backend.Seed(mainId, main);
            Assert.False(material.PrepareRenderAssets());
            backend.Seed(maskId, mask);
            Assert.True(material.PrepareRenderAssets());
            scene.Dispose();
            Assert.False(AssetDatabase.IsLocked(mainId));
            Assert.False(AssetDatabase.IsLocked(maskId));
        }
        finally
        {
            if (!scene.IsDisposed) scene.Dispose();
            AssetDatabase.Forget(mainId);
            AssetDatabase.Forget(maskId);
            AssetDatabase.Current = previous;
            AssetLoadingConfig.AsyncEnabled = previousAsync;
        }
    }

    [Fact]
    public void RuntimeTextureAndAuthoredNullUseValidShaderDefaults()
    {
        using var material = new Material();
        using var texture = new Texture2D(2, 2);
        material.SetTexture("_MainTex", texture);
        material.SetTexture("_Noise", default(AssetRef<Texture2D>));
        Assert.True(material.PrepareRenderAssets());
    }

    [Fact]
    public void PendingTrailOverrideDoesNotFallBackToParticleMaterial()
    {
        var previous = AssetDatabase.Current;
        bool previousAsync = AssetLoadingConfig.AsyncEnabled;
        var backend = new Backend();
        Guid id = Guid.NewGuid();
        using var system = new Material();
        using var trail = new Material();
        var module = new ParticleSystem.TrailsModule();
        AssetDatabase.Current = backend;
        AssetLoadingConfig.AsyncEnabled = false;
        try
        {
            Assert.Same(system, module.TrailMaterialResolution(system));
            module.TrailMaterial = default(AssetRef<Material>);
            Assert.Same(system, module.TrailMaterialResolution(system));
            module.TrailMaterial = new AssetRef<Material>(id);
            Assert.Null(module.TrailMaterialResolution(system));
            Assert.Contains(id, backend.Requested);
            backend.Seed(id, trail);
            Assert.Same(trail, module.TrailMaterialResolution(system));
        }
        finally
        {
            AssetDatabase.Forget(id);
            AssetDatabase.Current = previous;
            AssetLoadingConfig.AsyncEnabled = previousAsync;
        }
    }
}
