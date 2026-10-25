using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class CameraRenderSceneTests : RuntimeTestBase
{
    [Fact]
    public void ExplicitRenderScenePreservesMembershipAndRestoresNestedFailure()
    {
        using var owner = CreateScene(enable: true);
        using var viewed = CreateScene(enable: true);
        using var nested = CreateScene(enable: true);
        var go = CreateGameObject("Camera");
        var camera = go.AddComponent<Camera>();
        owner.Add(go);
        long ownerVersion = owner.StructureVersion, viewedVersion = viewed.StructureVersion;
        using var pipeline = new ProbePipeline();
        pipeline.Probe = current =>
        {
            Assert.Same(viewed, current.RenderScene);
            Assert.Same(owner, current.Scene);
            Assert.Same(owner, go.Scene);
            using var inner = new ProbePipeline();
            inner.Probe = c => { Assert.Same(nested, c.RenderScene); throw new InvalidOperationException("Injected"); };
            Assert.Throws<InvalidOperationException>(() => inner.RenderScene(current, nested, new RenderingData()));
            Assert.Same(viewed, current.RenderScene);
            throw new InvalidOperationException("Outer");
        };
        Assert.Throws<InvalidOperationException>(() => pipeline.RenderScene(camera, viewed, new RenderingData()));
        Assert.Same(owner, camera.RenderScene);
        Assert.Equal(ownerVersion, owner.StructureVersion);
        Assert.Equal(viewedVersion, viewed.StructureVersion);

        owner.Remove(go);
        pipeline.Probe = c => { Assert.Same(viewed, c.RenderScene); Assert.Null(c.Scene); };
        for (int i = 0; i < 100; i++) pipeline.RenderScene(camera, viewed, new RenderingData());
        Assert.Null(camera.RenderScene);
        Assert.Null(go.Scene);
        Assert.Equal(viewedVersion, viewed.StructureVersion);
    }

    private sealed class ProbePipeline : RenderPipeline
    {
        public Action<Camera> Probe = null!;
        internal override void Render(Camera camera, in RenderingData data) => Probe(camera);
    }
}
