// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Runtime.InteropServices;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class LightCookieTests : RuntimeTestBase
{
    public LightCookieTests()
    {
        LightCookieManager.Instance.ResetForTests();
    }

    public override void Dispose()
    {
        LightCookieManager.Instance.ResetForTests();
        base.Dispose();
    }

    [Fact]
    public void EchoRoundTrip_PreservesCookieFields()
    {
        var go = CreateGameObject("Sun");
        var light = go.AddComponent<DirectionalLight>();
        light.CookieSize = 24f;
        light.CookieIntensity = 0.75f;

        var clone = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneLight = clone.GetComponent<DirectionalLight>();
        Assert.NotNull(cloneLight);
        Assert.Equal(24f, cloneLight!.CookieSize, 3);
        Assert.Equal(0.75f, cloneLight.CookieIntensity, 3);
    }

    [Fact]
    public void Packer_PlacesTwoTilesWithoutOverlap()
    {
        var packer = new LightCookiePacker(256);
        Assert.True(packer.TryReserve(64, 64, out Int2 a));
        Assert.True(packer.TryReserve(64, 64, out Int2 b));
        bool overlapX = a.X < b.X + 64 && b.X < a.X + 64;
        bool overlapY = a.Y < b.Y + 64 && b.Y < a.Y + 64;
        Assert.False(overlapX && overlapY);
    }

    [Fact]
    public void DirectionalWorldToCookie_CentersOnLightPosition()
    {
        Float4x4 worldToCookie = LightCookieMath.DirectionalWorldToCookie(
            Float3.Zero, Float3.UnitY, Float3.UnitZ, 10f);
        Assert.True(LightCookieMath.TryWorldToUv(worldToCookie, Float3.Zero, out Float2 uv));
        Assert.Equal(0.5f, uv.X, 3);
        Assert.Equal(0.5f, uv.Y, 3);
    }

    [Fact]
    public void DirectionalWorldToCookie_OutsideSlabIsRejected()
    {
        Float4x4 worldToCookie = LightCookieMath.DirectionalWorldToCookie(
            Float3.Zero, Float3.UnitY, Float3.UnitZ, 4f);
        Assert.False(LightCookieMath.TryWorldToUv(worldToCookie, new Float3(8f, 0f, 0f), out _));
    }

    [Fact]
    public void SpotWorldToCookie_CentersOnConeAxis()
    {
        Float3 origin = Float3.Zero;
        Float3 forward = Float3.UnitZ;
        Float4x4 worldToCookie = LightCookieMath.SpotWorldToCookie(
            origin, forward, LightCookieMath.ReconstructUp(forward), 30f, 10f);
        Float3 onAxis = origin + forward * 5f;
        Assert.True(LightCookieMath.TryWorldToUv(worldToCookie, onAxis, out Float2 uv));
        Assert.Equal(0.5f, uv.X, 2);
        Assert.Equal(0.5f, uv.Y, 2);
    }

    [Fact]
    public void PointLatLong_ForwardMapsToUvCenterLongitude()
    {
        Float2 uv = LightCookieMath.PointLatLongUv(Float3.Zero, Float3.UnitZ);
        Assert.Equal(0.5f, uv.X, 3);
        Assert.Equal(0.5f, uv.Y, 2);
    }

    [Fact]
    public void NoCookie_LeavesManagerDisabled()
    {
        Assert.False(LightCookieManager.Instance.DirectionalEnabled);
        Assert.Equal(0, LightCookieManager.Instance.PackedLocalCount);
        Assert.False(LightCookieAtlas.IsInitialized);
    }

    [Fact]
    public void StandardShaders_InlineCookieSampling()
    {
        Shader standard = Shader.LoadDefault(DefaultShader.Standard);
        string glsl = PassSource(standard.GetPass(0), "_fragmentSource");
        string hlsl = PassSource(standard.GetPass(0), "_hlslFragmentSource");
        Assert.Contains("SampleMainLightCookie", glsl);
        Assert.Contains("SampleLocalLightCookie", glsl);
        Assert.Contains("SampleMainLightCookie", hlsl);
        Assert.Contains("StandardLightCookies", hlsl);
        Assert.Contains("t12", hlsl);
        Assert.Contains("t13", hlsl);
        Assert.Equal(80, Marshal.SizeOf<StandardLightCookiesUniformsData>());
        Assert.Equal(6, ShaderConfig.LightBvhTexelsPerLight);
    }

    private static string PassSource(ShaderPass pass, string field)
        => (string)typeof(ShaderPass)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
}
