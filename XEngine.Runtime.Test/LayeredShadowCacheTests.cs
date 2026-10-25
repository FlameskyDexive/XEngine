// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Layered static/dynamic shadow cache (P3.5): a scene containing both static and dynamic
/// casters renders the static layer once per static key, then per frame restores it with one
/// depth blit and re-draws only the dynamic casters. All-static scenes keep the whole-atlas
/// cache (zero shadow work per frame).
/// </summary>
public sealed class LayeredShadowCacheTests : IDisposable
{
    private static readonly RenderingData s_renderingData = new() { SkipUI = true };

    private readonly RHI.GraphicsBackend? _previousBackend;
    private readonly bool _previousPlaying;

    public LayeredShadowCacheTests()
    {
        _previousBackend = RHI.GraphicsBackendSelection.Preferred;
        _previousPlaying = Application.IsPlaying;
        Application.IsPlaying = true;
        RHI.GraphicsBackendSelection.Preferred = RHI.GraphicsBackend.Null;
        Graphics.Initialize(debug: false);
        ShadowAtlas.InvalidateShadowCache();
        ShadowAtlas.InvalidateStaticCache();
    }

    public void Dispose()
    {
        Graphics.Dispose();
        RHI.GraphicsBackendSelection.Preferred = _previousBackend;
        Application.IsPlaying = _previousPlaying;
    }

    private sealed class ShadowScene : IDisposable
    {
        public readonly Scene Scene = new();
        public readonly Camera Camera;
        public readonly UniversalRenderPipeline Pipeline = new();
        private readonly RenderTexture _target;
        private readonly Mesh _mesh;
        private readonly Material _material;

        public ShadowScene(int staticCount, int dynamicCount)
        {
            var cameraObject = new GameObject("shadow cam");
            Camera = cameraObject.AddComponent<Camera>();
            _target = new RenderTexture(64, 64, true, [TextureImageFormat.Color4b]);
            Camera.Target = _target;
            Camera.ClearFlags = CameraClearFlags.SolidColor;
            Camera.Pipeline = Pipeline;
            cameraObject.Transform.Position = new Float3(0f, 2f, -12f);
            Scene.Add(cameraObject);

            var lightObject = new GameObject("sun");
            lightObject.Transform.LocalEulerAngles = new Float3(-45f, 45f, 0f);
            DirectionalLight light = lightObject.AddComponent<DirectionalLight>();
            light.CastShadows = true;
            Scene.Add(lightObject);

            _mesh = Mesh.CreateCube(Float3.One);
            _material = new Material(Shader.LoadDefault(DefaultShader.Standard));

            for (int i = 0; i < staticCount; i++)
                AddCube($"static {i}", new Float3(i * 2f - staticCount, 0f, 0f), isStatic: true);
            for (int i = 0; i < dynamicCount; i++)
                AddCube($"dynamic {i}", new Float3(i * 2f - dynamicCount, 2f, 0f), isStatic: false);

            Scene.Enable();
        }

        private void AddCube(string name, Float3 position, bool isStatic)
        {
            var go = new GameObject(name);
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.Mesh = _mesh;
            renderer.Material = _material;
            go.Transform.Position = position;
            go.IsStatic = isStatic;
            Scene.Add(go);
        }

        public RenderStats.Frame RenderFrame()
        {
            RenderStats.BeginFrame();
            Pipeline.Render(Camera, in s_renderingData);
            RenderStats.EndFrame();
            return RenderStats.Last;
        }

        public void Dispose()
        {
            if (Scene.IsActive)
                Scene.Disable();
            Scene.Dispose();
            _target.Dispose();
            Pipeline.Dispose();
            _mesh.Dispose();
        }
    }

    private static GameObject? FindByName(Scene scene, string name)
    {
        foreach (GameObject? go in scene.FindObjectsOfType<GameObject>())
        {
            if (go != null && go.Name == name)
                return go;
        }
        return null;
    }

    [Fact]
    public void MixedCasters_BuildStaticLayerOnce_ThenRedrawOnlyDynamics()
    {
        using var scene = new ShadowScene(staticCount: 4, dynamicCount: 1);

        RenderStats.Frame first = scene.RenderFrame();
        Assert.Equal(1, first.ShadowAtlasClears);
        Assert.True(first.ShadowDrawCalls > 0);

        RenderStats.Frame second = scene.RenderFrame();
        // Static layer restored by blit: no atlas clear, no static re-render; the dynamic cube
        // still draws its shadow every frame.
        Assert.Equal(0, second.ShadowAtlasClears);
        Assert.True(second.ShadowDrawCalls > 0);
        Assert.True(second.ShadowDrawCalls < first.ShadowDrawCalls);

        RenderStats.Frame third = scene.RenderFrame();
        Assert.Equal(0, third.ShadowAtlasClears);
        Assert.Equal(second.ShadowDrawCalls, third.ShadowDrawCalls);
    }

    [Fact]
    public void AllStaticCasters_KeepWholeAtlasCache()
    {
        using var scene = new ShadowScene(staticCount: 3, dynamicCount: 0);

        RenderStats.Frame first = scene.RenderFrame();
        Assert.Equal(1, first.ShadowAtlasClears);
        Assert.True(first.ShadowDrawCalls > 0);

        RenderStats.Frame second = scene.RenderFrame();
        Assert.Equal(0, second.ShadowAtlasClears);
        Assert.Equal(0, second.ShadowDrawCalls);
        Assert.True(second.ShadowPassesCached > 0);
    }

    [Fact]
    public void DynamicCasterMoving_DoesNotRebuildStaticLayer()
    {
        using var scene = new ShadowScene(staticCount: 4, dynamicCount: 1);
        scene.RenderFrame();

        GameObject? dynamicGo = FindByName(scene.Scene, "dynamic 0");
        Assert.NotNull(dynamicGo);

        for (int i = 0; i < 3; i++)
        {
            dynamicGo!.Transform.Position += new Float3(0.25f, 0f, 0f);
            RenderStats.Frame frame = scene.RenderFrame();
            Assert.Equal(0, frame.ShadowAtlasClears);
            Assert.True(frame.ShadowDrawCalls > 0);
        }
    }

    [Fact]
    public void StaticCasterChange_RebuildsStaticLayer()
    {
        using var scene = new ShadowScene(staticCount: 4, dynamicCount: 1);
        scene.RenderFrame();
        scene.RenderFrame();

        GameObject? staticGo = FindByName(scene.Scene, "static 0");
        Assert.NotNull(staticGo);
        staticGo!.Transform.Position += new Float3(0f, 1f, 0f);

        RenderStats.Frame rebuilt = scene.RenderFrame();
        Assert.Equal(1, rebuilt.ShadowAtlasClears);
        Assert.True(rebuilt.ShadowDrawCalls > 0);

        RenderStats.Frame after = scene.RenderFrame();
        Assert.Equal(0, after.ShadowAtlasClears);
    }
}
