// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ScreenSpaceDecalTests : RuntimeTestBase
{
    public ScreenSpaceDecalTests()
    {
        DecalSystem.Instance.ResetForTests();
    }

    public override void Dispose()
    {
        DecalSystem.Instance.ResetForTests();
        base.Dispose();
    }

    [Fact]
    public void EchoRoundTrip_PreservesProjectorFields()
    {
        var go = CreateProjectorObject();
        var projector = go.GetComponent<DecalProjector>()!;
        projector.Size = new Float3(4f, 1f, 3f);
        projector.Opacity = 0.6f;
        projector.Color = new Color(0.2f, 0.4f, 0.8f, 1f);
        projector.RenderingLayers = RenderingLayerMask.FromIndex(2);

        var clone = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneProjector = clone.GetComponent<DecalProjector>();
        Assert.NotNull(cloneProjector);
        Assert.Equal(4f, cloneProjector!.Size.X, 3);
        Assert.Equal(1f, cloneProjector.Size.Y, 3);
        Assert.Equal(3f, cloneProjector.Size.Z, 3);
        Assert.Equal(0.6f, cloneProjector.Opacity, 3);
        Assert.Equal(0.2f, cloneProjector.Color.R, 3);
        Assert.Equal(RenderingLayerMask.FromIndex(2).Mask, cloneProjector.RenderingLayers.Mask);
    }

    [Fact]
    public void DisabledOrEmptyFeature_LeavesSevenPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceDecalRendererFeature { Enabled = false });
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.None, pipeline.RequestedInputsForTest);

        var enabled = new UniversalRenderPipeline();
        enabled.AddRendererFeature(new ScreenSpaceDecalRendererFeature());
        Assert.Equal(7, enabled.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.None, enabled.RequestedInputsForTest);
        Assert.False(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Skybox, UniversalRenderPipeline.MapPassInputs(enabled.RequestedInputsForTest)));
    }

    [Fact]
    public void EnabledWithProjector_EnqueuesPassAndRequestsPrepass()
    {
        CreateProjectorObject();
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceDecalRendererFeature());

        int count = pipeline.EnqueueBuiltInAndFeaturesForTest();
        ScriptableRenderPassInput expected =
            ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal;

        // 7 built-in + decal pass + CopyDepth (Depth consumer).
        Assert.Equal(9, count);
        Assert.Equal(expected, pipeline.RequestedInputsForTest);
        Assert.True(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Skybox, UniversalRenderPipeline.MapPassInputs(expected)));

        var events = new RenderPassEvent[9];
        Assert.Equal(9, pipeline.CopyEnqueuedEventsForTest(events));
        Assert.Equal(RenderPassEvent.AfterRenderingOpaques, events[3]);
        Assert.Equal(RenderPassEvent.AfterRenderingOpaques, events[7]);
        Assert.Equal(RenderPassEvent.AfterRenderingOpaques, events[8]);
    }

    [Fact]
    public void WorldToUv_ClipsOutsideUnitCube()
    {
        Float4x4 model = Float4x4.CreateTRS(Float3.Zero, Quaternion.Identity, Float3.One);
        Assert.True(Float4x4.Invert(model, out Float4x4 worldToLocal));

        Assert.True(DecalProjector.TryWorldToUv(Float3.Zero, worldToLocal, out Float2 uv));
        Assert.Equal(0.5f, uv.X, 3);
        Assert.Equal(0.5f, uv.Y, 3);

        Assert.True(DecalProjector.TryWorldToUv(new Float3(0.4f, 0f, -0.4f), worldToLocal, out Float2 edge));
        Assert.Equal(0.9f, edge.X, 3);
        Assert.Equal(0.1f, edge.Y, 3);

        Assert.False(DecalProjector.TryWorldToUv(new Float3(0.6f, 0f, 0f), worldToLocal, out _));
        Assert.False(DecalProjector.TryWorldToUv(new Float3(0f, 0.6f, 0f), worldToLocal, out _));
    }

    [Fact]
    public void Layers_ProjectorLayer1_DoesNotAffectDefaultObject()
    {
        uint projector = RenderingLayerMask.FromIndex(1).Mask;
        uint objDefault = RenderingLayerMask.Default.Mask;
        Assert.False(RenderingLayerMask.Overlaps(projector, objDefault));
        Assert.True(RenderingLayerMask.Overlaps(RenderingLayerMask.Everything.Mask, objDefault));
    }

    [Fact]
    public void Shader_HasDualSources()
    {
        Shader shader = Shader.LoadDefault(DefaultShader.ScreenSpaceDecal);
        ShaderPass pass = shader.GetPass(0);
        Assert.Equal("ScreenSpaceDecal", pass.Name);
        Assert.True(pass.HasGlsl);
        Assert.True(pass.HasHlsl);

        string glsl = PassSource(pass, "_fragmentSource");
        string hlsl = PassSource(pass, "_hlslFragmentSource");
        Assert.Contains("_CameraDepthTexture", glsl);
        Assert.Contains("_DecalRenderingLayers", glsl);
        Assert.Contains("xengine_WorldToObject", glsl);
        Assert.Contains("_CameraDepthTexture", hlsl);
        Assert.Contains("_DecalRenderingLayers", hlsl);
        Assert.Contains("xengine_WorldToObject", hlsl);
    }

    [Fact]
    public void OnEnable_RegistersAndOnDisable_Unregisters()
    {
        Assert.Equal(0, DecalSystem.Instance.Count);
        var go = CreateProjectorObject();
        Assert.Equal(1, DecalSystem.Instance.Count);
        go.Enabled = false;
        Assert.Equal(0, DecalSystem.Instance.Count);
    }

    private GameObject CreateProjectorObject(string name = "Decal")
    {
        var scene = CreateScene(enable: true);
        var go = CreateGameObject(name);
        scene.Add(go);
        go.AddComponent<DecalProjector>();
        return go;
    }

    private static string PassSource(ShaderPass pass, string field)
        => (string)typeof(ShaderPass)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
}
