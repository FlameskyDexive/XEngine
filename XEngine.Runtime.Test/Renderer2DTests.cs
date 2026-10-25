// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class Renderer2DTests : RuntimeTestBase
{
    public Renderer2DTests()
    {
        Light2DSystem.Instance.ResetForTests();
        ShadowCaster2DSystem.Instance.ResetForTests();
        UniversalRenderPipeline.ActiveRendererKind = RendererKind.Universal;
    }

    public override void Dispose()
    {
        Light2DSystem.Instance.ResetForTests();
        ShadowCaster2DSystem.Instance.ResetForTests();
        UniversalRenderPipeline.ActiveRendererKind = RendererKind.Universal;
        base.Dispose();
    }

    [Fact]
    public void Universal_StillEnqueuesSevenBuiltInPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        Assert.Equal(RendererKind.Universal, pipeline.RendererKind);
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

    [Fact]
    public void Renderer2D_EnqueuesSevenBuiltInPasses()
    {
        var pipeline = new UniversalRenderPipeline { RendererKind = RendererKind.Renderer2D };
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(
        [
            RenderPassEvent.BeforeRendering,
            RenderPassEvent.BeforeRenderingShadows,
            RenderPassEvent.BeforeRenderingOpaques,
            RenderPassEvent.AfterRenderingOpaques,
            RenderPassEvent.BeforeRenderingTransparents,
            RenderPassEvent.BeforeRenderingPostProcessing,
            RenderPassEvent.AfterRendering,
        ], pipeline.GetBuiltInPassEvents());

        pipeline.CompileRenderGraphForTest(out int culled, out int executed);
        Assert.Equal(2, culled);
        Assert.Equal(7, executed);
    }

    private sealed class EmptyFeature : RendererFeature
    {
        public override void Create() { }

        public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context) { }
    }

    [Fact]
    public void Renderer2D_EmptyFeature_DoesNotAddPasses()
    {
        var pipeline = new UniversalRenderPipeline { RendererKind = RendererKind.Renderer2D };
        pipeline.AddRendererFeature(new EmptyFeature { Enabled = false });
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

    [Fact]
    public void Renderer2D_IgnoresDeferredPlus_KeepsSevenPasses()
    {
        var pipeline = new UniversalRenderPipeline
        {
            RendererKind = RendererKind.Renderer2D,
            RenderingPath = RenderingPath.DeferredPlus,
        };
        Assert.False(pipeline.UsesDeferredShading);
        Assert.False(pipeline.UsesClusteredLighting);
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

    [Fact]
    public void Overlay_StillRendersOnRenderer2D()
    {
        var overlay = new Camera { RenderType = CameraRenderType.Overlay };
        var pipeline = new UniversalRenderPipeline { RendererKind = RendererKind.Renderer2D };
        Assert.True(pipeline.ShouldRenderCamera(overlay));
    }

    [Fact]
    public void Kind_ReadsRendererDataAsset()
    {
        var renderer = new UniversalRenderData { Kind = RendererKind.Renderer2D };
        var pipeline = new UniversalRenderPipeline
        {
            Asset = new UniversalRenderPipelineAsset { Renderer = new AssetRef<UniversalRenderData>(renderer) },
        };
        Assert.Equal(RendererKind.Renderer2D, pipeline.RendererKind);
    }

    [Fact]
    public void EchoRoundTrip_PreservesKindAndLight2D()
    {
        var data = new UniversalRenderData { DepthPriming = true, Kind = RendererKind.Renderer2D };
        var cloneData = Serializer.Deserialize<UniversalRenderData>(Serializer.Serialize(typeof(object), data));
        Assert.NotNull(cloneData);
        Assert.Equal(RendererKind.Renderer2D, cloneData!.Kind);

        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Lamp");
        scene.Add(go);
        var light = go.AddComponent<Light2D>();
        light.LightType = Light2DType.Global;
        light.Intensity = 0.8f;
        light.Color = new Color(1f, 0.5f, 0.25f, 1f);

        var cloneGo = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneLight = cloneGo.GetComponent<Light2D>();
        Assert.NotNull(cloneLight);
        Assert.Equal(Light2DType.Global, cloneLight!.LightType);
        Assert.Equal(0.8f, cloneLight.Intensity, 3);
        Assert.Equal(1f, cloneLight.Color.R, 3);
    }

    [Fact]
    public void OnValidate_ClampsUnknownKind()
    {
        var data = new UniversalRenderData { Kind = (RendererKind)99 };
        data.OnValidate();
        Assert.Equal(RendererKind.Universal, data.Kind);
    }

    [Fact]
    public void Light2D_RegistersAndUnregisters()
    {
        Assert.Equal(0, Light2DSystem.Instance.Count);
        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Light");
        scene.Add(go);
        go.AddComponent<Light2D>();
        Assert.Equal(1, Light2DSystem.Instance.Count);
        go.Enabled = false;
        Assert.Equal(0, Light2DSystem.Instance.Count);
    }

    [Fact]
    public void GlobalLight_EvaluateAt_IsUniform()
    {
        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Global");
        scene.Add(go);
        var light = go.AddComponent<Light2D>();
        light.LightType = Light2DType.Global;
        light.Color = new Color(0.2f, 0.4f, 0.8f, 1f);
        light.Intensity = 2f;

        Color a = light.EvaluateAt(Float3.Zero);
        Color b = light.EvaluateAt(new Float3(10f, -4f, 0f));
        Assert.Equal(0.4f, a.R, 3);
        Assert.Equal(0.8f, a.G, 3);
        Assert.Equal(1.6f, a.B, 3);
        Assert.Equal(a.R, b.R, 3);
        Assert.Equal(a.G, b.G, 3);
        Assert.Equal(a.B, b.B, 3);
    }

    [Fact]
    public void PointLight_Falloff_InsideAndOutside()
    {
        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Point");
        scene.Add(go);
        go.Transform.Position = Float3.Zero;
        var light = go.AddComponent<Light2D>();
        light.LightType = Light2DType.Point;
        light.InnerRadius = 1f;
        light.OuterRadius = 4f;
        light.Falloff = 0f;
        light.Color = Color.White;
        light.Intensity = 1f;

        Color inside = light.EvaluateAt(new Float3(0.5f, 0f, 0f));
        Color outside = light.EvaluateAt(new Float3(5f, 0f, 0f));
        Assert.Equal(1f, inside.R, 3);
        Assert.Equal(0f, outside.R, 3);
    }

    [Fact]
    public void ShadowCaster_OccludesBehindBox()
    {
        var scene = CreateScene(enable: true);
        var casterGo = CreateGameObject("Wall");
        scene.Add(casterGo);
        casterGo.Transform.Position = new Float3(2f, 0f, 0f);
        var caster = casterGo.AddComponent<ShadowCaster2D>();
        caster.Size = new Float2(1f, 2f);

        var lightGo = CreateGameObject("Lamp");
        scene.Add(lightGo);
        lightGo.Transform.Position = Float3.Zero;
        var light = lightGo.AddComponent<Light2D>();
        light.LightType = Light2DType.Point;
        light.OuterRadius = 10f;
        light.InnerRadius = 0f;
        light.CastShadows = true;
        light.ShadowIntensity = 1f;
        light.Falloff = 0f;

        Color lit = light.EvaluateAt(new Float3(0.5f, 0f, 0f));
        Color shadowed = light.EvaluateAt(new Float3(4f, 0f, 0f));
        Assert.True(lit.R > 0.5f);
        Assert.Equal(0f, shadowed.R, 3);
        Assert.True(caster.Occludes(new Float2(0f, 0f), new Float2(4f, 0f)));
        Assert.False(caster.Occludes(new Float2(0f, 0f), new Float2(0.5f, 0f)));
    }

    [Fact]
    public void PixelPerfect_ComputesZoomAndSnapsPosition()
    {
        var ppc = new PixelPerfectCamera
        {
            AssetsPPU = 100,
            RefResolutionX = 320,
            RefResolutionY = 180,
            Snap = true,
        };
        Assert.Equal(2, ppc.ComputeZoom(640, 360));
        Assert.Equal(1.8f, ppc.ComputeOrthographicSize(360, 2), 3);

        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Cam");
        scene.Add(go);
        var camera = go.AddComponent<Camera>();
        go.AddComponent<PixelPerfectCamera>().Apply(camera, 640, 360);
        go.Transform.Position = new Float3(0.013f, 0.007f, 0f);
        var live = go.GetComponent<PixelPerfectCamera>()!;
        live.Apply(camera, 640, 360);
        Assert.Equal(2, live.Zoom);
        Assert.Equal(Camera.ProjectionType.Orthographic, camera.ProjectionMode);
        float upp = camera.OrthographicSize / 360f;
        Assert.Equal(0f, go.Transform.Position.X % upp, 4);
        Assert.Equal(0f, go.Transform.Position.Y % upp, 4);
    }

    [Fact]
    public void SpriteRenderer_UsesSpriteLitOnRenderer2D()
    {
        UniversalRenderPipeline.ActiveRendererKind = RendererKind.Universal;
        Assert.Equal("Sprite", SpriteRenderer.ResolveDefaultMaterial().Shader.Name);

        UniversalRenderPipeline.ActiveRendererKind = RendererKind.Renderer2D;
        Assert.Equal("SpriteLit", SpriteRenderer.ResolveDefaultMaterial().Shader.Name);
    }

    [Fact]
    public void Light2DShaders_Parse()
    {
        Shader lit = Shader.LoadDefault(DefaultShader.SpriteLit);
        Assert.Equal("SpriteLit", lit.GetPass("SpriteLit").Name);
        Shader light = Shader.LoadDefault(DefaultShader.Light2D);
        Assert.Equal("Light2D", light.GetPass("Light2D").Name);
        Shader shadow = Shader.LoadDefault(DefaultShader.Shadow2D);
        Assert.Equal("Shadow2D", shadow.GetPass("Shadow2D").Name);
    }
}
