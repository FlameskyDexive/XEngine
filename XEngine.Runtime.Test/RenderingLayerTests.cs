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

public sealed class RenderingLayerTests : RuntimeTestBase
{
    [Fact]
    public void EchoRoundTrip_PreservesRendererAndLightMasks()
    {
        var go = CreateGameObject("Lit");
        go.LayerIndex = 8;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.RenderingLayers = RenderingLayerMask.FromIndex(3);
        var lightGo = CreateGameObject("Lamp");
        var light = lightGo.AddComponent<PointLight>();
        light.RenderingLayers = RenderingLayerMask.FromIndex(1) | RenderingLayerMask.FromIndex(3);

        var cloneGo = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneRenderer = cloneGo.GetComponent<MeshRenderer>();
        var cloneLight = Serializer.Deserialize<GameObject>(Serializer.Serialize(lightGo)).GetComponent<PointLight>();
        Assert.NotNull(cloneRenderer);
        Assert.NotNull(cloneLight);
        Assert.Equal(renderer.RenderingLayers.Mask, cloneRenderer!.RenderingLayers.Mask);
        Assert.Equal(light.RenderingLayers.Mask, cloneLight!.RenderingLayers.Mask);
        Assert.Equal(8, cloneGo.LayerIndex);
        Assert.False(cloneRenderer.RenderingLayers.HasLayer(0));
        Assert.True(cloneRenderer.RenderingLayers.HasLayer(3));
    }

    [Fact]
    public void Defaults_AreEverything_AndIndependentOfLayerMask()
    {
        Assert.Equal(8, RenderingLayerMask.Count);
        Assert.Equal(8, RenderingLayerMask.Names.Length);
        Assert.Equal(0xFFu, RenderingLayerMask.Everything.Mask);
        Assert.Equal(1u, RenderingLayerMask.Default.Mask);
        Assert.Equal("Default", RenderingLayerMask.LayerToName(0));
        Assert.Equal(2, RenderingLayerMask.NameToLayer("Layer2"));

        var go = CreateGameObject("Cube");
        go.LayerIndex = 4;
        var renderer = go.AddComponent<MeshRenderer>();
        var light = go.AddComponent<DirectionalLight>();

        Assert.Equal(RenderingLayerMask.Everything.Mask, renderer.RenderingLayers.Mask);
        Assert.Equal(RenderingLayerMask.Everything.Mask, light.RenderingLayers.Mask);
        Assert.Equal(4, go.LayerIndex);
        Assert.True(RenderingLayerMask.Overlaps(light.RenderingLayers.Mask, renderer.RenderingLayers.Mask));
    }

    [Fact]
    public void Overlap_Layer1Light_DoesNotAffectDefaultOnlyObject()
    {
        uint light = RenderingLayerMask.FromIndex(1).Mask;
        uint objDefault = RenderingLayerMask.Default.Mask;
        uint objLayer1 = RenderingLayerMask.FromIndex(1).Mask;
        Assert.False(RenderingLayerMask.Overlaps(light, objDefault));
        Assert.True(RenderingLayerMask.Overlaps(light, objLayer1));
        Assert.True(RenderingLayerMask.Overlaps(RenderingLayerMask.Everything.Mask, objDefault));
    }

    [Fact]
    public void LightBvh_StoresRenderingLayers_AndSetRenderingLayersDirtiesSlot()
    {
        var bvh = new LightBVH();
        var stub = new LayerStubLight();
        var data = new ForwardLightData
        {
            Type = LightType.Point,
            Position = Float3.Zero,
            Direction = -Float3.UnitY,
            Color = new Float3(1, 1, 1),
            Intensity = 1f,
            Range = 5f,
            RenderingLayerMask = XEngine.Runtime.RenderingLayerMask.FromIndex(2).Mask,
        };
        int slot = bvh.Add(stub, in data);
        Assert.Equal(1u << 2, bvh.Slots[slot].RenderingLayerMask);

        bvh.SetRenderingLayers(stub, XEngine.Runtime.RenderingLayerMask.FromIndex(7).Mask);
        Assert.Equal(1u << 7, bvh.Slots[slot].RenderingLayerMask);
        Assert.True(bvh.HasSlotDirty);
    }

    [Fact]
    public void MeshRenderable_ReportsRendererMask()
    {
        using var mesh = Mesh.CreateCube(Float3.One);
        var mat = new Material(Shader.LoadDefault(DefaultShader.Unlit));
        var renderable = new MeshRenderable(mesh, mat, Float4x4.Identity, layerIndex: 3,
            renderingLayerMask: RenderingLayerMask.FromIndex(5).Mask);
        Assert.Equal(3, renderable.GetLayer());
        Assert.Equal(1u << 5, renderable.GetRenderingLayerMask());
    }

    [Fact]
    public void StandardShaders_InlineRenderingLayerFilter()
    {
        Shader standard = Shader.LoadDefault(DefaultShader.Standard);
        string glsl = PassSource(standard.GetPass(0), "_fragmentSource");
        string hlsl = PassSource(standard.GetPass(0), "_hlslFragmentSource");
        Assert.Contains("LightAffectsObject", glsl);
        Assert.Contains("_DirectionalLightRenderingLayers", glsl);
        Assert.Contains("LightAffectsObject", hlsl);
        Assert.Contains("_RenderingLayerMask", hlsl);
        Assert.Contains("#define MAX_RENDERING_LAYERS 8", glsl);
        Assert.Contains("#define MAX_RENDERING_LAYERS 8", hlsl);
        Assert.Equal(8, ShaderConfig.MaxRenderingLayers);
        Assert.Equal(352, System.Runtime.InteropServices.Marshal.SizeOf<ObjectUniformsData>());
    }

    private static string PassSource(ShaderPass pass, string field)
        => (string)typeof(ShaderPass)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;

    private sealed class LayerStubLight : IRenderableLight
    {
        public int GetLightID() => 1;
        public int GetLayer() => 0;
        public LightType GetLightType() => LightType.Point;
        public Float3 GetLightPosition() => Float3.Zero;
        public Float3 GetLightDirection() => -Float3.UnitY;
        public bool DoCastShadows() => false;
        public ForwardLightData GetForwardLightData() => default;
    }
}
