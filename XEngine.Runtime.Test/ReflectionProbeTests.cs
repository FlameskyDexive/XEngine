// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
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

public sealed class ReflectionProbeTests : RuntimeTestBase
{
    public ReflectionProbeTests()
    {
        ReflectionProbeManager.Instance.ResetForTests();
    }

    public override void Dispose()
    {
        ReflectionProbeManager.Instance.ResetForTests();
        base.Dispose();
    }

    [Fact]
    public void EchoRoundTrip_PreservesProbeFields()
    {
        var go = CreateGameObject("Probe");
        var probe = go.AddComponent<ReflectionProbe>();
        probe.Size = new Float3(4f, 5f, 6f);
        probe.Intensity = 1.25f;
        probe.BlendDistance = 2.5f;
        probe.Importance = 7;
        probe.BoxProjection = false;
        probe.Mode = ReflectionProbeMode.Realtime;
        probe.Resolution = 64;

        var clone = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneProbe = clone.GetComponent<ReflectionProbe>();
        Assert.NotNull(cloneProbe);
        Assert.Equal(4f, cloneProbe!.Size.X, 3);
        Assert.Equal(5f, cloneProbe.Size.Y, 3);
        Assert.Equal(6f, cloneProbe.Size.Z, 3);
        Assert.Equal(1.25f, cloneProbe.Intensity, 3);
        Assert.Equal(2.5f, cloneProbe.BlendDistance, 3);
        Assert.Equal(7, cloneProbe.Importance);
        Assert.False(cloneProbe.BoxProjection);
        Assert.Equal(ReflectionProbeMode.Realtime, cloneProbe.Mode);
        Assert.Equal(64, cloneProbe.Resolution);
    }

    [Fact]
    public void BoxProjectedDirection_HitsNearBoxFace()
    {
        Float3 dir = ReflectionProbe.BoxProjectedDirection(
            Float3.UnitX,
            new Float3(4f, 0f, 0f),
            Float3.Zero,
            new Float3(-5f, -5f, -5f),
            new Float3(5f, 5f, 5f));

        Assert.Equal(5f, dir.X, 3);
        Assert.Equal(0f, dir.Y, 3);
        Assert.Equal(0f, dir.Z, 3);
    }

    [Fact]
    public void Prepare_SelectsHigherImportanceAsSlot0()
    {
        using var cube = new Cubemap(4);
        ReflectionProbe low = CreateProbe("Low", Float3.Zero, importance: 1, cube);
        ReflectionProbe high = CreateProbe("High", Float3.Zero, importance: 8, cube);

        ReflectionProbeManager.Instance.Prepare(Float3.Zero);

        Assert.Equal(2, ReflectionProbeManager.Instance.PreparedCount);
        Assert.Same(high, ReflectionProbeManager.Instance.GetPrepared(0));
        Assert.Same(low, ReflectionProbeManager.Instance.GetPrepared(1));
        Assert.Equal(1f, ReflectionProbeManager.Instance.GetPreparedWeight(0), 3);
    }

    [Fact]
    public void Prepare_SelectsNearerWeightWhenImportanceTies()
    {
        using var cube = new Cubemap(4);
        ReflectionProbe inside = CreateProbe("Inside", Float3.Zero, importance: 1, cube);
        inside.Size = new Float3(10f, 10f, 10f);
        inside.BlendDistance = 2f;

        ReflectionProbe edge = CreateProbe("Edge", new Float3(6f, 0f, 0f), importance: 1, cube);
        edge.Size = new Float3(10f, 10f, 10f);
        edge.BlendDistance = 2f;

        ReflectionProbeManager.Instance.Prepare(Float3.Zero);

        Assert.Equal(2, ReflectionProbeManager.Instance.PreparedCount);
        Assert.Same(inside, ReflectionProbeManager.Instance.GetPrepared(0));
        Assert.True(ReflectionProbeManager.Instance.GetPreparedWeight(0)
            > ReflectionProbeManager.Instance.GetPreparedWeight(1));
    }

    [Fact]
    public void Prepare_SkipsProbeOutsideBlendDistance()
    {
        using var cube = new Cubemap(4);
        ReflectionProbe probe = CreateProbe("Far", Float3.Zero, importance: 1, cube);
        probe.BlendDistance = 0f;

        ReflectionProbeManager.Instance.Prepare(new Float3(20f, 0f, 0f));

        Assert.Equal(0, ReflectionProbeManager.Instance.PreparedCount);
    }

    [Fact]
    public void ProbeWithoutCubemap_HasZeroInfluence()
    {
        ReflectionProbe probe = CreateProbe("Empty", Float3.Zero, importance: 9, cube: null);

        Assert.Equal(0f, probe.EvaluateInfluence(Float3.Zero), 3);
        ReflectionProbeManager.Instance.Prepare(Float3.Zero);
        Assert.Equal(0, ReflectionProbeManager.Instance.PreparedCount);
    }

    [Fact]
    public void RenderProbe_AllocatesCubemap()
    {
        var go = CreateGameObject("Capture");
        var probe = go.AddComponent<ReflectionProbe>();
        ReflectionProbeManager.Instance.Register(probe);
        probe.Resolution = 32;

        Assert.True(probe.RenderProbe());
        Assert.NotNull(probe.ResolvedTexture);
        Assert.Equal(32u, probe.ResolvedTexture!.Size);
        Assert.True(probe.ResolvedTexture.MipLevels > 1);
    }

    [Fact]
    public void FaceViews_AreSixUniqueForwards()
    {
        var seen = new HashSet<Float3>();
        for (int face = 0; face < 6; face++)
        {
            Assert.True(seen.Add(ReflectionProbe.FaceForward(face)));
            Float4x4 view = ReflectionProbe.FaceViewMatrix(Float3.Zero, face);
            Assert.False(view.Equals(Float4x4.Identity));
        }

        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void StandardShader_InlinesMixIndirectSpecular()
    {
        Shader standard = Shader.LoadDefault(DefaultShader.Standard);
        string glsl = PassSource(standard.GetPass(0), "_fragmentSource");
        string hlsl = PassSource(standard.GetPass(0), "_hlslFragmentSource");
        Assert.Contains("MixIndirectSpecular", glsl);
        Assert.Contains("MixIndirectSpecular", hlsl);
        Assert.Contains($"#define MAX_REFLECTION_PROBES {ShaderConfig.MaxReflectionProbes}", glsl);
        Assert.Equal(2, ShaderConfig.MaxReflectionProbes);
        Assert.Equal(112, Marshal.SizeOf<StandardReflectionProbesUniformsData>());
        Assert.Contains(ShaderLibrary.ImageBasedLighting, ShaderLibrary.ForwardLitGlslIncludes);
        Assert.Contains(ShaderLibrary.ImageBasedLighting, ShaderLibrary.ForwardLitHlslIncludes);
    }

    private ReflectionProbe CreateProbe(string name, Float3 position, int importance, Cubemap? cube)
    {
        var go = CreateGameObject(name);
        go.Transform.Position = position;
        var probe = go.AddComponent<ReflectionProbe>();
        probe.Importance = importance;
        probe.Size = new Float3(10f, 10f, 10f);
        probe.BlendDistance = 1f;
        probe.Intensity = 1f;
        if (cube != null)
            probe.Texture = new AssetRef<Cubemap>(cube);
        ReflectionProbeManager.Instance.Register(probe);
        return probe;
    }

    private static string PassSource(ShaderPass pass, string field)
        => (string)typeof(ShaderPass)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
}
