using System.Runtime.InteropServices;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.VFX;

using Xunit;

namespace XEngine.Runtime.Test.VFX;

public sealed class VisualEffectTests : RuntimeTestBase
{
    [Fact]
    public void ParticleLayout_MatchesShaderStride()
    {
        Assert.Equal(64, Marshal.SizeOf<VfxParticle>());
        Assert.Equal(160, Marshal.SizeOf<VfxSimParams>());
        Assert.Equal(8, VfxCounterSlot.AliveCountByteOffset);
    }

    [Fact]
    public void DispatchGroups_RoundsUpToThreadGroupSize()
    {
        Assert.Equal(0u, GpuParticleSystem.DispatchGroups(0));
        Assert.Equal(1u, GpuParticleSystem.DispatchGroups(1));
        Assert.Equal(1u, GpuParticleSystem.DispatchGroups(64));
        Assert.Equal(2u, GpuParticleSystem.DispatchGroups(65));
    }

    [Fact]
    public void VisualEffect_PlayStop_TracksPlaying()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        Assert.True(vfx.IsPlaying);
        vfx.Stop();
        Assert.False(vfx.IsPlaying);
        vfx.Play();
        Assert.True(vfx.IsPlaying);
        vfx.Pause();
        Assert.False(vfx.IsPlaying);
    }

    [Fact]
    public void VisualEffect_ApplyGraph_CopiesCompiledSettings()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateBurst());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.SpawnRate);
        Assert.Equal(256f, vfx.BurstCount);
        Assert.Equal(0f, vfx.PeriodicBurstCount);
        Assert.Equal(1f, vfx.PeriodicBurstPeriod);
        Assert.Equal(-2f, vfx.Gravity.Y);
        Assert.Equal(0.85f, vfx.StartColor.G, 3);
        vfx.Stop();
        vfx.Play();
        Assert.True(vfx.PendingBurst);
        vfx.Stop();
        Assert.False(vfx.PendingBurst);
    }

    [Fact]
    public void VisualEffect_SetFloatVectorTexture_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();

        Assert.True(vfx.HasFloat(VfxExposedNames.SpawnRate));
        Assert.True(vfx.HasVector3(VfxExposedNames.Gravity));
        Assert.True(vfx.HasTexture(VfxExposedNames.MainTexture));
        Assert.Equal(64f, vfx.GetFloat(VfxExposedNames.SpawnRate));

        vfx.SetFloat(VfxExposedNames.SpawnRate, 99f);
        vfx.SetVector3(VfxExposedNames.Gravity, new Float3(0f, -4f, 0f));
        vfx.SetVector4(VfxExposedNames.StartColor, new Float4(1f, 0f, 0f, 1f));
        Assert.Equal(99f, vfx.SpawnRate);
        Assert.Equal(-4f, vfx.Gravity.Y);
        Assert.Equal(0f, vfx.StartColor.G);

        vfx.ApplyGraph();
        Assert.Equal(99f, vfx.GetFloat(VfxExposedNames.SpawnRate));
        Assert.Equal(-4f, vfx.GetVector3(VfxExposedNames.Gravity).Y);

        var tex = new Texture2D(1, 1);
        vfx.SetTexture(VfxExposedNames.MainTexture, tex);
        Assert.Same(tex, vfx.GetTexture(VfxExposedNames.MainTexture));

        vfx.ResetExposedOverrides();
        Assert.Equal(64f, vfx.SpawnRate);
        Assert.Equal(-1.5f, vfx.Gravity.Y);
        Assert.Null(vfx.GetTexture(VfxExposedNames.MainTexture));
        tex.Dispose();
    }

    [Fact]
    public void VfxFlipbook_FrameUv_MapsAgeToCells()
    {
        VfxFlipbook.FrameUv(0f, 0f, 0f, 1f, 2f, 2f, out float u0, out float v0);
        Assert.Equal(0f, u0, 3);
        Assert.Equal(0.5f, v0, 3);

        VfxFlipbook.FrameUv(0f, 0f, 0.6f, 1f, 2f, 2f, out float u2, out float v2);
        Assert.Equal(0f, u2, 3);
        Assert.Equal(0f, v2, 3);
    }

    [Fact]
    public void VfxOverLife_MixesColorAndSize()
    {
        VfxOverLife.Color(0f, 1f, 0f, 0f, 1f, 0f, 1f, 0f, 0f, out float r0, out float g0, out _, out float a0);
        Assert.Equal(1f, r0, 3);
        Assert.Equal(0f, g0, 3);
        Assert.Equal(1f, a0, 3);
        VfxOverLife.Color(0.5f, 1f, 0f, 0f, 1f, 0f, 1f, 0f, 0f, out float r1, out float g1, out _, out float a1);
        Assert.Equal(0.5f, r1, 3);
        Assert.Equal(0.5f, g1, 3);
        Assert.Equal(0.5f, a1, 3);
        Assert.Equal(0.2f, VfxOverLife.Size(0.4f, 0.5f, 1f, 0f), 3);
        Assert.Equal(0.4f, VfxOverLife.Size(0.4f, 0f, 1f, 0f), 3);
        Assert.Equal(0f, VfxOverLife.Size(0.4f, 1f, 1f, 0f), 3);
    }

    [Fact]
    public void VfxSizeOverSpeed_ScalesByVelocity()
    {
        Assert.Equal(0.4f, VfxSizeOverSpeed.Apply(0.4f, 3f, 4f, 0f, 0f), 3);
        Assert.Equal(1.4f, VfxSizeOverSpeed.Apply(0.4f, 3f, 4f, 0f, 0.5f), 3);
    }

    [Fact]
    public void VfxMultiplySize_ScalesSize()
    {
        Assert.Equal(0.2f, VfxMultiplySize.Apply(0.2f, 1f), 3);
        Assert.Equal(0.4f, VfxMultiplySize.Apply(0.2f, 2f), 3);
    }

    [Fact]
    public void VfxMultiplyColor_ScalesColor()
    {
        VfxMultiplyColor.Apply(1f, 0.5f, 0.25f, 1f, 1f, 1f, 1f, 1f, out float r0, out float g0, out float b0, out float a0);
        Assert.Equal(1f, r0, 3);
        Assert.Equal(0.5f, g0, 3);
        Assert.Equal(0.25f, b0, 3);
        Assert.Equal(1f, a0, 3);
        VfxMultiplyColor.Apply(1f, 0.5f, 0.25f, 1f, 0.5f, 1f, 1f, 1f, out float r1, out float g1, out float b1, out float a1);
        Assert.Equal(0.5f, r1, 3);
        Assert.Equal(0.5f, g1, 3);
        Assert.Equal(0.25f, b1, 3);
        Assert.Equal(1f, a1, 3);
    }

    [Fact]
    public void VfxAddSize_OffsetsSize()
    {
        Assert.Equal(0.2f, VfxAddSize.Apply(0.2f, 0f), 3);
        Assert.Equal(0.3f, VfxAddSize.Apply(0.2f, 0.1f), 3);
    }

    [Fact]
    public void VfxAddLifetime_OffsetsLifetime()
    {
        Assert.Equal(1.2f, VfxAddLifetime.Apply(1.2f, 0f), 3);
        Assert.Equal(1.7f, VfxAddLifetime.Apply(1.2f, 0.5f), 3);
    }

    [Fact]
    public void VfxMultiplyLifetime_ScalesLifetime()
    {
        Assert.Equal(1.2f, VfxMultiplyLifetime.Apply(1.2f, 1f), 3);
        Assert.Equal(2.4f, VfxMultiplyLifetime.Apply(1.2f, 2f), 3);
    }

    [Fact]
    public void VfxAddAge_OffsetsAge()
    {
        Assert.Equal(0.4f, VfxAddAge.Apply(0.4f, 0f), 3);
        Assert.Equal(0.9f, VfxAddAge.Apply(0.4f, 0.5f), 3);
    }

    [Fact]
    public void VfxMultiplyAge_ScalesAge()
    {
        Assert.Equal(0.4f, VfxMultiplyAge.Apply(0.4f, 1f), 3);
        Assert.Equal(0.8f, VfxMultiplyAge.Apply(0.4f, 2f), 3);
    }

    [Fact]
    public void VfxSetAge_OverwritesAge()
    {
        Assert.Equal(0.4f, VfxSetAge.Apply(0.4f, 0f), 3);
        Assert.Equal(0.5f, VfxSetAge.Apply(0.4f, 0.5f), 3);
    }

    [Fact]
    public void VfxAddColor_OffsetsColor()
    {
        VfxAddColor.Apply(0.2f, 0.3f, 0.4f, 1f, 0f, 0f, 0f, 0f, out float r0, out float g0, out float b0, out float a0);
        Assert.Equal(0.2f, r0, 3);
        Assert.Equal(0.3f, g0, 3);
        Assert.Equal(0.4f, b0, 3);
        Assert.Equal(1f, a0, 3);
        VfxAddColor.Apply(0.2f, 0.3f, 0.4f, 1f, 0.25f, 0.1f, 0f, 0f, out float r1, out float g1, out float b1, out float a1);
        Assert.Equal(0.45f, r1, 3);
        Assert.Equal(0.4f, g1, 3);
        Assert.Equal(0.4f, b1, 3);
        Assert.Equal(1f, a1, 3);
    }

    [Fact]
    public void VfxAddVelocity_OffsetsVelocity()
    {
        float vx = 1f, vy = 2f, vz = 3f;
        VfxAddVelocity.Apply(ref vx, ref vy, ref vz, 0f, 0f, 0f);
        Assert.Equal(1f, vx, 3);
        Assert.Equal(2f, vy, 3);
        Assert.Equal(3f, vz, 3);
        VfxAddVelocity.Apply(ref vx, ref vy, ref vz, 1.5f, 0.5f, 0f);
        Assert.Equal(2.5f, vx, 3);
        Assert.Equal(2.5f, vy, 3);
        Assert.Equal(3f, vz, 3);
    }

    [Fact]
    public void VfxAddPosition_OffsetsPosition()
    {
        float px = 1f, py = 2f, pz = 3f;
        VfxAddPosition.Apply(ref px, ref py, ref pz, 0f, 0f, 0f);
        Assert.Equal(1f, px, 3);
        Assert.Equal(2f, py, 3);
        Assert.Equal(3f, pz, 3);
        VfxAddPosition.Apply(ref px, ref py, ref pz, 0.5f, 0.25f, 0f);
        Assert.Equal(1.5f, px, 3);
        Assert.Equal(2.25f, py, 3);
        Assert.Equal(3f, pz, 3);
    }

    [Fact]
    public void VfxSetPosition_OverwritesPosition()
    {
        float px = 1f, py = 2f, pz = 3f;
        VfxSetPosition.Apply(ref px, ref py, ref pz, 0f, 0f, 0f);
        Assert.Equal(1f, px, 3);
        Assert.Equal(2f, py, 3);
        Assert.Equal(3f, pz, 3);
        VfxSetPosition.Apply(ref px, ref py, ref pz, 0.5f, 0.25f, 0f);
        Assert.Equal(0.5f, px, 3);
        Assert.Equal(0.25f, py, 3);
        Assert.Equal(0f, pz, 3);
    }

    [Fact]
    public void VfxMultiplyVelocity_ScalesVelocity()
    {
        float vx = 1f, vy = 2f, vz = 3f;
        VfxMultiplyVelocity.Apply(ref vx, ref vy, ref vz, 1f);
        Assert.Equal(1f, vx, 3);
        Assert.Equal(2f, vy, 3);
        Assert.Equal(3f, vz, 3);
        VfxMultiplyVelocity.Apply(ref vx, ref vy, ref vz, 2f);
        Assert.Equal(2f, vx, 3);
        Assert.Equal(4f, vy, 3);
        Assert.Equal(6f, vz, 3);
    }

    [Fact]
    public void VfxSetSize_OverwritesSize()
    {
        Assert.Equal(0.2f, VfxSetSize.Apply(0.2f, 0f), 3);
        Assert.Equal(0.35f, VfxSetSize.Apply(0.2f, 0.35f), 3);
    }

    [Fact]
    public void VfxSetColor_OverwritesColor()
    {
        VfxSetColor.Apply(0.2f, 0.3f, 0.4f, 1f, 0f, 0f, 0f, 0f, out float r0, out float g0, out float b0, out float a0);
        Assert.Equal(0.2f, r0, 3);
        Assert.Equal(0.3f, g0, 3);
        Assert.Equal(0.4f, b0, 3);
        Assert.Equal(1f, a0, 3);
        VfxSetColor.Apply(0.2f, 0.3f, 0.4f, 1f, 0.25f, 0.1f, 0f, 1f, out float r1, out float g1, out float b1, out float a1);
        Assert.Equal(0.25f, r1, 3);
        Assert.Equal(0.1f, g1, 3);
        Assert.Equal(0f, b1, 3);
        Assert.Equal(1f, a1, 3);
    }

    [Fact]
    public void VfxSetVelocity_OverwritesVelocity()
    {
        float vx = 1f, vy = 2f, vz = 3f;
        VfxSetVelocity.Apply(ref vx, ref vy, ref vz, 0f, 0f, 0f);
        Assert.Equal(1f, vx, 3);
        Assert.Equal(2f, vy, 3);
        Assert.Equal(3f, vz, 3);
        VfxSetVelocity.Apply(ref vx, ref vy, ref vz, 1.5f, 0.5f, 0f);
        Assert.Equal(1.5f, vx, 3);
        Assert.Equal(0.5f, vy, 3);
        Assert.Equal(0f, vz, 3);
    }

    [Fact]
    public void VfxSetLifetime_OverwritesLifetime()
    {
        Assert.Equal(2f, VfxSetLifetime.Apply(2f, 0f), 3);
        Assert.Equal(1.5f, VfxSetLifetime.Apply(2f, 1.5f), 3);
    }

    [Fact]
    public void VfxColorOverSpeed_MixesByVelocity()
    {
        VfxColorOverSpeed.Apply(2f, 0f, 0f, 0f, 1f, 1f, 1f, 1f, 0.2f, 0.85f, 1f, 1f, out float r0, out float g0, out float b0, out float a0);
        Assert.Equal(1f, r0, 3);
        Assert.Equal(1f, g0, 3);
        Assert.Equal(1f, b0, 3);
        Assert.Equal(1f, a0, 3);
        VfxColorOverSpeed.Apply(2f, 0f, 0f, 4f, 1f, 1f, 1f, 1f, 0.2f, 0.85f, 1f, 1f, out float r1, out float g1, out float b1, out float a1);
        Assert.Equal(0.6f, r1, 3);
        Assert.Equal(0.925f, g1, 3);
        Assert.Equal(1f, b1, 3);
        Assert.Equal(1f, a1, 3);
    }

    [Fact]
    public void VfxScreenSpaceSize_ScalesByCameraDistance()
    {
        Assert.Equal(0.2f, VfxScreenSpaceSize.Apply(0.2f, 4f, 0f, 0f, 0f), 3);
        Assert.Equal(0.4f, VfxScreenSpaceSize.Apply(0.2f, 4f, 0f, 0f, 2f), 3);
    }

    [Fact]
    public void VfxMotionVector_ReconstructsPreviousAndScreenDelta()
    {
        VfxMotionVector.PrevPosition(1f, 2f, 3f, 4f, 0f, -2f, 0.5f, out float qx, out float qy, out float qz);
        Assert.Equal(-1f, qx, 3);
        Assert.Equal(2f, qy, 3);
        Assert.Equal(4f, qz, 3);
        VfxMotionVector.NdcDelta(2f, 0f, 2f, 0f, 0f, 1f, out float dx, out float dy);
        Assert.Equal(0.5f, dx, 3);
        Assert.Equal(0f, dy, 3);
        VfxMotionVector.EncodeRgb(1f, -1f, 1f, out float r, out float g, out float b);
        Assert.Equal(1f, r, 3);
        Assert.Equal(0f, g, 3);
        Assert.Equal(0.5f, b, 3);
        float ar = 0f, ag = 0f, ab = 0f;
        VfxMotionVector.Apply(ref ar, ref ag, ref ab, 1f, 1f, -1f, 0f, 1f);
        Assert.Equal(0f, ar, 3);
        VfxMotionVector.Apply(ref ar, ref ag, ref ab, 1f, 1f, -1f, 1f, 1f);
        Assert.Equal(1f, ar, 3);
        Assert.Equal(0f, ag, 3);
        Assert.Equal(0.5f, ab, 3);
    }

    [Fact]
    public void VfxOrient_BuildsVelocityBasis()
    {
        VfxOrient.AlongVelocity(0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 8f, 1f, 0f, 0f, 1f, out float sx, out float sy, out float sz);
        Assert.Equal(1f, sx, 3);
        Assert.Equal(0f, sy, 3);
        Assert.Equal(0f, sz, 3);
        VfxOrient.AlongVelocity(0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 8f, 0f, 1f, 0f, 1f, out float ax, out float ay, out float az);
        Assert.Equal(0f, ax, 3);
        Assert.Equal(1f, ay, 3);
        Assert.Equal(0f, az, 3);
        VfxOrient.AlongVelocity(2f, 3f, 4f, 0f, 0f, 0f, 0f, 0f, 8f, 0f, 1f, 0f, 1f, out float zx, out float zy, out float zz);
        Assert.Equal(2f, zx, 3);
        Assert.Equal(4f, zy, 3);
        Assert.Equal(4f, zz, 3);
        VfxOrient.ResolveAlong(0f, 1f, 2f, 3f, 0f, 1f, 0f, out float vx, out float vy, out float vz);
        Assert.Equal(1f, vx, 3);
        Assert.Equal(2f, vy, 3);
        Assert.Equal(3f, vz, 3);
        VfxOrient.ResolveAlong(2f, 1f, 2f, 3f, 0f, 0f, 1f, out float fx, out float fy, out float fz);
        Assert.Equal(0f, fx, 3);
        Assert.Equal(0f, fy, 3);
        Assert.Equal(1f, fz, 3);
        VfxOrient.ResolveAlong(2f, 1f, 2f, 3f, 0f, 0f, 0f, out float dx, out float dy, out float dz);
        Assert.Equal(0f, dx, 3);
        Assert.Equal(1f, dy, 3);
        Assert.Equal(0f, dz, 3);
    }

    [Fact]
    public void VfxPositionShape_OffsetsSphereBoxConeCircle()
    {
        VfxPositionShape.Offset(3u, 0f, 1f, 1f, 1f, out float zx, out float zy, out float zz);
        Assert.Equal(0f, zx, 3);
        Assert.Equal(0f, zy, 3);
        Assert.Equal(0f, zz, 3);
        VfxPositionShape.Offset(3u, 1f, 0.4f, 0.4f, 0.4f, out float sx, out float sy, out float sz);
        Assert.True(sx * sx + sy * sy + sz * sz <= 0.4f * 0.4f + 1e-4f);
        VfxPositionShape.Offset(3u, 2f, 0.5f, 0.2f, 0.3f, out float bx, out float by, out float bz);
        Assert.InRange(bx, -0.5f, 0.5f);
        Assert.InRange(by, -0.2f, 0.2f);
        Assert.InRange(bz, -0.3f, 0.3f);
        VfxPositionShape.Offset(3u, 3f, 0.5f, 0.8f, 0.5f, out _, out float cy, out _);
        Assert.InRange(cy, 0f, 0.8f);
        VfxPositionShape.Offset(3u, 4f, 0.6f, 0.6f, 0.6f, out float rx, out float ry, out float rz);
        Assert.Equal(0f, ry, 3);
        Assert.True(rx * rx + rz * rz <= 0.6f * 0.6f + 1e-4f);
    }

    [Fact]
    public void VfxDrag_ScalesVelocityByExp()
    {
        float vx = 4f, vy = -2f, vz = 1f;
        VfxDrag.Apply(ref vx, ref vy, ref vz, 0f, 0.1f);
        Assert.Equal(4f, vx, 3);
        Assert.Equal(-2f, vy, 3);
        Assert.Equal(1f, vz, 3);
        VfxDrag.Apply(ref vx, ref vy, ref vz, 2f, 0.1f);
        float w = MathF.Exp(-0.2f);
        Assert.Equal(4f * w, vx, 4);
        Assert.Equal(-2f * w, vy, 4);
        Assert.Equal(1f * w, vz, 4);
    }

    [Fact]
    public void VfxForce_AddsAccelerationTimesDt()
    {
        float vx = 1f, vy = 2f, vz = 3f;
        VfxForce.Apply(ref vx, ref vy, ref vz, 0f, 0f, 0f, 0.1f);
        Assert.Equal(1f, vx, 3);
        Assert.Equal(2f, vy, 3);
        Assert.Equal(3f, vz, 3);
        VfxForce.Apply(ref vx, ref vy, ref vz, 4f, -2f, 1f, 0.1f);
        Assert.Equal(1.4f, vx, 3);
        Assert.Equal(1.8f, vy, 3);
        Assert.Equal(3.1f, vz, 3);
    }

    [Fact]
    public void VfxSpin_IntegratesAngleAndRotatesLocalXy()
    {
        float angle = 0.5f;
        VfxSpin.Integrate(ref angle, 2f, 0.25f);
        Assert.Equal(1f, angle, 3);
        VfxSpin.Local(1f, 0f, 0f, out float zx, out float zy);
        Assert.Equal(1f, zx, 3);
        Assert.Equal(0f, zy, 3);
        VfxSpin.Local(1f, 0f, MathF.PI * 0.5f, out float sx, out float sy);
        Assert.Equal(0f, sx, 3);
        Assert.Equal(1f, sy, 3);
    }

    [Fact]
    public void VfxCustomHlsl_DetectsCode()
    {
        Assert.False(VfxCustomHlsl.HasCode(null));
        Assert.False(VfxCustomHlsl.HasCode(""));
        Assert.False(VfxCustomHlsl.HasCode("   "));
        Assert.True(VfxCustomHlsl.HasCode("particle.velocity.y += 0.5;"));
    }

    [Fact]
    public void VfxSequential_OffsetsCircleAndLine()
    {
        VfxSequential.Offset(0u, 0f, 1f, 1f, 8f, out float zx, out float zy, out float zz);
        Assert.Equal(0f, zx, 3);
        Assert.Equal(0f, zy, 3);
        Assert.Equal(0f, zz, 3);
        VfxSequential.Offset(0u, 1f, 0.5f, 0.5f, 8f, out float c0x, out float c0y, out float c0z);
        Assert.Equal(0.5f, c0x, 3);
        Assert.Equal(0f, c0y, 3);
        Assert.Equal(0f, c0z, 3);
        VfxSequential.Offset(2u, 1f, 0.5f, 0.5f, 8f, out float c2x, out _, out float c2z);
        Assert.True(c2x * c2x + c2z * c2z <= 0.5f * 0.5f + 1e-4f);
        VfxSequential.Offset(0u, 2f, 0.8f, 0.4f, 5f, out float lx, out float ly, out float lz);
        Assert.Equal(-0.8f, lx, 3);
        Assert.Equal(0f, ly, 3);
        Assert.Equal(0f, lz, 3);
        VfxSequential.Offset(4u, 2f, 0.8f, 0.4f, 5f, out float rx, out _, out _);
        Assert.Equal(0.8f, rx, 3);
        VfxSequential.Offset(2u, 3f, 0.8f, 0.4f, 5f, out _, out float my, out _);
        Assert.Equal(0f, my, 3);
    }

    [Fact]
    public void VfxInheritVelocity_AddsEmitterDelta()
    {
        VfxInheritVelocity.Sample(false, 0f, 0f, 0f, 1f, 0f, 0f, 0.5f, out float zvx, out float zvy, out float zvz);
        Assert.Equal(0f, zvx);
        Assert.Equal(0f, zvy);
        Assert.Equal(0f, zvz);
        VfxInheritVelocity.Sample(true, 0f, 1f, 0f, 2f, 1f, 0f, 0.5f, out float vx, out float vy, out float vz);
        Assert.Equal(4f, vx, 3);
        Assert.Equal(0f, vy, 3);
        Assert.Equal(0f, vz, 3);
        float px = 1f, py = 2f, pz = 3f;
        VfxInheritVelocity.Add(ref px, ref py, ref pz, 4f, 0f, -2f, 0f);
        Assert.Equal(1f, px, 3);
        VfxInheritVelocity.Add(ref px, ref py, ref pz, 4f, 0f, -2f, 0.5f);
        Assert.Equal(3f, px, 3);
        Assert.Equal(2f, py, 3);
        Assert.Equal(2f, pz, 3);
    }

    [Fact]
    public void VfxFlipbook_BlendsAdjacentFrames()
    {
        VfxFlipbook.FrameUvBlend(0f, 0f, 0.375f, 1f, 2f, 2f, 0f, out float b0u, out float b0v, out float b1u, out _, out float bw0);
        Assert.Equal(0.5f, b0u, 3);
        Assert.Equal(0.5f, b0v, 3);
        Assert.Equal(0f, bw0);
        Assert.Equal(b0u, b1u, 3);
        VfxFlipbook.FrameUvBlend(0f, 0f, 0.375f, 1f, 2f, 2f, 1f, out float c0u, out float c0v, out float c1u, out float c1v, out float cw);
        Assert.Equal(0.5f, c0u, 3);
        Assert.Equal(0.5f, c0v, 3);
        Assert.Equal(0f, c1u, 3);
        Assert.Equal(0f, c1v, 3);
        Assert.Equal(0.5f, cw, 3);
        float r = 1f, g = 0f, b = 0f, a = 1f;
        VfxFlipbook.Blend(ref r, ref g, ref b, ref a, 0f, 1f, 0f, 1f, 0.5f);
        Assert.Equal(0.5f, r, 3);
        Assert.Equal(0.5f, g, 3);
    }

    [Fact]
    public void VfxFlipbookMotion_WarpsUvFromRg()
    {
        VfxFlipbookMotion.Decode(1f, 0.5f, out float mx, out float my);
        Assert.Equal(1f, mx, 3);
        Assert.Equal(0f, my, 3);
        VfxFlipbookMotion.Warp(0.25f, 0.5f, mx, my, 0.5f, 0f, out float nu, out float nv);
        Assert.Equal(0.25f, nu, 3);
        Assert.Equal(0.5f, nv, 3);
        VfxFlipbookMotion.WarpPair(0.25f, 0.5f, 0.75f, 0.5f, 1f, 0f, 1f, 0f, 0.25f, 2f,
            out float au, out float av, out float bu, out float bv);
        Assert.Equal(0.25f + 1f * 0.25f * 2f, au, 3);
        Assert.Equal(0.5f, av, 3);
        Assert.Equal(0.75f + 1f * (0.25f - 1f) * 2f, bu, 3);
        Assert.Equal(0.5f, bv, 3);
    }

    [Fact]
    public void VisualEffect_SetFloat_Flipbook_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.FlipbookColumns));
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.FlipbookColumns));
        vfx.SetFloat(VfxExposedNames.FlipbookColumns, 4f);
        vfx.SetFloat(VfxExposedNames.FlipbookRows, 2f);
        vfx.ApplyGraph();
        Assert.Equal(4f, vfx.FlipbookColumns);
        Assert.Equal(2f, vfx.FlipbookRows);
        vfx.ResetExposedOverrides();
        Assert.Equal(1f, vfx.FlipbookColumns);
        Assert.Equal(1f, vfx.FlipbookRows);
    }

    [Fact]
    public void VfxCollision_KillAndBounce_PlaneY()
    {
        float px = 0f, py = -0.1f, pz = 0f;
        float vx = 0f, vy = -1f, vz = 0f;
        uint alive = 1u;
        VfxCollision.CollidePlane(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 1f, 0f, 0f, VfxCollision.ModeKill, 0.4f);
        Assert.Equal(0u, alive);

        px = 0f; py = -0.1f; pz = 0f;
        vx = 0f; vy = -1f; vz = 0f;
        alive = 1u;
        VfxCollision.CollidePlane(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 1f, 0f, 0f, VfxCollision.ModeBounce, 0.5f);
        Assert.Equal(1u, alive);
        Assert.Equal(0f, py, 4);
        Assert.Equal(0.5f, vy, 4);
    }

    [Fact]
    public void VisualEffect_SetFloat_Collision_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.CollisionMode));
        Assert.True(vfx.HasVector4(VfxExposedNames.CollisionPlane));
        Assert.Equal(2f, vfx.GetFloat(VfxExposedNames.CollisionMode));
        vfx.SetFloat(VfxExposedNames.CollisionMode, 1f);
        vfx.SetFloat(VfxExposedNames.CollisionBounce, 0.2f);
        vfx.SetVector4(VfxExposedNames.CollisionPlane, new Float4(0f, 1f, 0f, 0.25f));
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.CollisionMode);
        Assert.Equal(0.2f, vfx.CollisionBounce);
        Assert.Equal(0.25f, vfx.CollisionPlane.W);
        vfx.ResetExposedOverrides();
        Assert.Equal(2f, vfx.CollisionMode);
        Assert.Equal(0.4f, vfx.CollisionBounce, 3);
        Assert.Equal(0f, vfx.CollisionPlane.W);
    }

    [Fact]
    public void VfxCollision_KillAndBounce_SdfSphereAndBox()
    {
        float px = 0f, py = 0.55f, pz = 0f;
        float vx = 0f, vy = -1f, vz = 0f;
        uint alive = 1u;
        VfxCollision.CollideSdf(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 0.55f, 0f, 0.4f, 0.4f, 0.4f,
            VfxCollision.ShapeSphere, VfxCollision.ModeKill, 0.4f);
        Assert.Equal(0u, alive);

        px = 0f; py = 0.7f; pz = 0f;
        vx = 0f; vy = -1f; vz = 0f;
        alive = 1u;
        VfxCollision.CollideSdf(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 0.55f, 0f, 0.4f, 0.4f, 0.4f,
            VfxCollision.ShapeSphere, VfxCollision.ModeBounce, 0.5f);
        Assert.Equal(1u, alive);
        float dist = VfxCollision.SampleSdf(
            px, py, pz, 0f, 0.55f, 0f, 0.4f, 0.4f, 0.4f, VfxCollision.ShapeSphere);
        Assert.True(dist >= -1e-3f, $"expected outside or on sphere, dist={dist}");
        Assert.True(vy > 0f, $"expected upward bounce, vy={vy}");

        px = 0f; py = 0f; pz = 0f;
        vx = 0f; vy = -1f; vz = 0f;
        alive = 1u;
        VfxCollision.CollideSdf(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 0f, 0f, 0.5f, 0.5f, 0.5f,
            VfxCollision.ShapeBox, VfxCollision.ModeBounce, 0.5f);
        Assert.Equal(1u, alive);
        float box = VfxCollision.SampleSdf(
            px, py, pz, 0f, 0f, 0f, 0.5f, 0.5f, 0.5f, VfxCollision.ShapeBox);
        Assert.True(box >= -1e-3f, $"expected outside or on box, dist={box}");
    }

    [Fact]
    public void VfxCollision_KillAndBounce_SdfCylinderAndCone()
    {
        float inside = VfxCollision.SampleSdf(
            0f, 0f, 0f, 0f, 0f, 0f, 0.4f, 0.4f, 0.4f, VfxCollision.ShapeCylinder);
        Assert.True(inside < 0f, $"expected inside cylinder, dist={inside}");
        float wall = VfxCollision.SampleSdf(
            0.4f, 0f, 0f, 0f, 0f, 0f, 0.4f, 0.4f, 0.4f, VfxCollision.ShapeCylinder);
        Assert.Equal(0f, wall, 3);
        float equalRadii = VfxCollision.SampleCone(0.2f, 0f, 0f, 0.4f, 0.4f, 0.4f);
        float cyl = VfxCollision.SampleCylinder(0.2f, 0f, 0f, 0.4f, 0.4f);
        Assert.Equal(cyl, equalRadii, 3);

        float px = 0f, py = 0f, pz = 0f;
        float vx = 0f, vy = -1f, vz = 0f;
        uint alive = 1u;
        VfxCollision.CollideSdf(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 0f, 0f, 0.4f, 0.4f, 0.4f,
            VfxCollision.ShapeCylinder, VfxCollision.ModeKill, 0.4f);
        Assert.Equal(0u, alive);

        px = 0f; py = 0.2f; pz = 0f;
        vx = 0f; vy = -1f; vz = 0f;
        alive = 1u;
        VfxCollision.CollideSdf(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 0f, 0f, 0.4f, 0.4f, 0.4f,
            VfxCollision.ShapeCylinder, VfxCollision.ModeBounce, 0.5f);
        Assert.Equal(1u, alive);
        float after = VfxCollision.SampleSdf(
            px, py, pz, 0f, 0f, 0f, 0.4f, 0.4f, 0.4f, VfxCollision.ShapeCylinder);
        Assert.True(after >= -1e-3f, $"expected outside or on cylinder, dist={after}");
        Assert.True(vy > 0f, $"expected upward bounce, vy={vy}");

        px = 0f; py = -0.15f; pz = 0f;
        vx = 0f; vy = -1f; vz = 0f;
        alive = 1u;
        VfxCollision.CollideSdf(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 0f, 0f, 0.4f, 0.4f, 0.02f,
            VfxCollision.ShapeCone, VfxCollision.ModeBounce, 0.5f);
        Assert.Equal(1u, alive);
        float cone = VfxCollision.SampleSdf(
            px, py, pz, 0f, 0f, 0f, 0.4f, 0.4f, 0.02f, VfxCollision.ShapeCone);
        Assert.True(cone >= -1e-3f, $"expected outside or on cone, dist={cone}");
    }

    [Fact]
    public void VfxCollision_KillAndBounce_VolumeSphere()
    {
        const int res = 8;
        float[] voxels = new float[res * res * res];
        VfxCollision.BakeSphereVolume(
            voxels, res,
            0f, 0.55f, 0f, 0.4f,
            0f, 0.55f, 0f, 0.6f, 0.6f, 0.6f);

        float center = VfxCollision.SampleVolumeSdf(
            0f, 0.55f, 0f, 0f, 0.55f, 0f, 0.6f, 0.6f, 0.6f, voxels, res, res, res);
        Assert.True(center < 0f, $"expected inside baked sphere, dist={center}");

        float px = 0f, py = 0.55f, pz = 0f;
        float vx = 0f, vy = -1f, vz = 0f;
        uint alive = 1u;
        VfxCollision.CollideVolumeSdf(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 0.55f, 0f, 0.6f, 0.6f, 0.6f,
            VfxCollision.ModeKill, 0.4f, voxels, res, res, res);
        Assert.Equal(0u, alive);

        px = 0f; py = 0.7f; pz = 0f;
        vx = 0f; vy = -1f; vz = 0f;
        alive = 1u;
        VfxCollision.CollideVolumeSdf(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, 0.55f, 0f, 0.6f, 0.6f, 0.6f,
            VfxCollision.ModeBounce, 0.5f, voxels, res, res, res);
        Assert.Equal(1u, alive);
        float dist = VfxCollision.SampleVolumeSdf(
            px, py, pz, 0f, 0.55f, 0f, 0.6f, 0.6f, 0.6f, voxels, res, res, res);
        Assert.True(dist >= -0.05f, $"expected outside or on volume, dist={dist}");
        Assert.True(vy > 0f, $"expected upward bounce, vy={vy}");
    }

    [Fact]
    public void VisualEffect_SetTexture3D_SdfVolume_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasTexture3D(VfxExposedNames.SdfVolume));
        var volume = new Texture3D(2, 2, 2, false, TextureImageFormat.Float);
        vfx.SetTexture3D(VfxExposedNames.SdfVolume, volume);
        Assert.Same(volume, vfx.GetTexture3D(VfxExposedNames.SdfVolume));
        Assert.Same(volume, vfx.SdfVolume);
        vfx.ResetExposedOverrides();
        Assert.Null(vfx.GetTexture3D(VfxExposedNames.SdfVolume));
        volume.Dispose();
    }

    [Fact]
    public void VisualEffect_SetFloat_Sdf_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.SdfCollisionMode));
        Assert.True(vfx.HasVector3(VfxExposedNames.SdfCenter));
        Assert.Equal(2f, vfx.GetFloat(VfxExposedNames.SdfCollisionMode));
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.SdfShape));
        vfx.SetFloat(VfxExposedNames.SdfCollisionMode, 1f);
        vfx.SetFloat(VfxExposedNames.SdfShape, 2f);
        vfx.SetVector3(VfxExposedNames.SdfCenter, new Float3(0f, 1f, 0f));
        vfx.SetVector3(VfxExposedNames.SdfSize, new Float3(0.8f, 0.5f, 0.5f));
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.SdfCollisionMode);
        Assert.Equal(2f, vfx.SdfShape);
        Assert.Equal(1f, vfx.SdfCenter.Y);
        Assert.Equal(0.8f, vfx.SdfSize.X);
        vfx.ResetExposedOverrides();
        Assert.Equal(2f, vfx.SdfCollisionMode);
        Assert.Equal(1f, vfx.SdfShape);
        Assert.Equal(0.55f, vfx.SdfCenter.Y, 3);
        Assert.Equal(0.4f, vfx.SdfSize.X, 3);
    }

    [Fact]
    public void VfxTurbulence_Apply_AddsCurlWhenIntensityPositive()
    {
        float vx = 0f, vy = 0f, vz = 0f;
        VfxTurbulence.Apply(ref vx, ref vy, ref vz, 0.3f, 0.7f, 0.1f, 0f, 1.25f, 1f / 30f);
        Assert.Equal(0f, vx);
        Assert.Equal(0f, vy);
        Assert.Equal(0f, vz);

        VfxTurbulence.Apply(ref vx, ref vy, ref vz, 0.3f, 0.7f, 0.1f, 1.2f, 1.25f, 1f / 30f);
        Assert.True(vx * vx + vy * vy + vz * vz > 0f);

        float ax = 0f, ay = 0f, az = 0f;
        float bx = 0f, by = 0f, bz = 0f;
        VfxTurbulence.Apply(ref ax, ref ay, ref az, 0.3f, 0.7f, 0.1f, 1.2f, 1.25f, 1f / 30f);
        VfxTurbulence.Apply(ref bx, ref by, ref bz, 1.1f, 0.2f, 0.8f, 1.2f, 1.25f, 1f / 30f);
        Assert.True(Math.Abs(ax - bx) + Math.Abs(ay - by) + Math.Abs(az - bz) > 1e-5f);
    }

    [Fact]
    public void VisualEffect_SetFloat_Turbulence_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.TurbulenceIntensity));
        Assert.Equal(1.2f, vfx.GetFloat(VfxExposedNames.TurbulenceIntensity), 3);
        Assert.Equal(1.25f, vfx.GetFloat(VfxExposedNames.TurbulenceFrequency), 3);
        vfx.SetFloat(VfxExposedNames.TurbulenceIntensity, 2.5f);
        vfx.SetFloat(VfxExposedNames.TurbulenceFrequency, 3f);
        vfx.ApplyGraph();
        Assert.Equal(2.5f, vfx.TurbulenceIntensity);
        Assert.Equal(3f, vfx.TurbulenceFrequency);
        vfx.ResetExposedOverrides();
        Assert.Equal(1.2f, vfx.TurbulenceIntensity, 3);
        Assert.Equal(1.25f, vfx.TurbulenceFrequency, 3);
    }

    [Fact]
    public void VfxSoftParticles_Fade_MatchesOutputFormula()
    {
        Assert.Equal(1f, VfxSoftParticles.Fade(1f, 4f, 0f));
        Assert.Equal(0f, VfxSoftParticles.Fade(3f, 2f, 1f));
        Assert.Equal(1f, VfxSoftParticles.Fade(1f, 3f, 1f));
        Assert.Equal(0.5f, VfxSoftParticles.Fade(1.5f, 2f, 1f), 3);
    }

    [Fact]
    public void VisualEffect_SetFloat_SoftParticleFade_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.SoftParticleFade));
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.SoftParticleFade));
        vfx.SetFloat(VfxExposedNames.SoftParticleFade, 2.5f);
        vfx.ApplyGraph();
        Assert.Equal(2.5f, vfx.SoftParticleFade);
        vfx.ResetExposedOverrides();
        Assert.Equal(1f, vfx.SoftParticleFade);
    }

    [Fact]
    public void VfxGpuEvent_EmitCount_OnDieAndOnCollide()
    {
        Assert.Equal(0u, VfxGpuEvent.EmitCount(VfxGpuEvent.KindOff, 2f, died: true, collided: true));
        Assert.Equal(0u, VfxGpuEvent.EmitCount(VfxGpuEvent.KindOnDie, 0f, died: true, collided: false));
        Assert.Equal(2u, VfxGpuEvent.EmitCount(VfxGpuEvent.KindOnDie, 2f, died: true, collided: false));
        Assert.Equal(0u, VfxGpuEvent.EmitCount(VfxGpuEvent.KindOnDie, 2f, died: false, collided: true));
        Assert.Equal(3u, VfxGpuEvent.EmitCount(VfxGpuEvent.KindOnCollide, 3f, died: false, collided: true));
        Assert.Equal(0u, VfxGpuEvent.EmitCount(VfxGpuEvent.KindOnCollide, 3f, died: true, collided: false));
        Assert.Equal(0u, VfxGpuEvent.EmitCount(VfxGpuEvent.KindAlways, 2f, died: true, collided: true));
        Assert.Equal(0u, VfxGpuEvent.EmitCount(VfxGpuEvent.KindOnPlay, 2f, died: true, collided: true));
        Assert.Equal(2u, VfxGpuEvent.EmitAlways(VfxGpuEvent.KindAlways, 2f));
        Assert.Equal(0u, VfxGpuEvent.EmitAlways(VfxGpuEvent.KindOnPlay, 2f));
        Assert.Equal(0u, VfxGpuEvent.EmitAlways(VfxGpuEvent.KindAlways, 0f));
        Assert.Equal(4u, VfxGpuEvent.EmitOnPlay(VfxGpuEvent.KindOnPlay, 4f));
        Assert.Equal(0u, VfxGpuEvent.EmitOnPlay(VfxGpuEvent.KindAlways, 4f));
        Assert.Equal(2u, VfxGpuEvent.ChildSpawnCount(VfxGpuEvent.KindOnDie, 2f, died: true, collided: false));
        Assert.Equal(0u, VfxGpuEvent.ChildSpawnCount(VfxGpuEvent.KindOnCollide, 3f, died: false, collided: true));
        Assert.Equal(VfxGpuEvent.MaxChildSpawn, VfxGpuEvent.ChildSpawnCount(VfxGpuEvent.KindOnDie, 32f, died: true, collided: false));
        uint pending = 0;
        Assert.Equal(3u, VfxGpuEvent.QueueTarget(ref pending, 3u, 64));
        Assert.Equal(3u, pending);
        Assert.Equal(2u, VfxGpuEvent.QueueTarget(ref pending, 8u, 5));
        Assert.Equal(5u, VfxGpuEvent.Consume(ref pending));
        Assert.Equal(0u, pending);
        Assert.False(VfxGpuEvent.HasTarget(""));
        Assert.True(VfxGpuEvent.HasTarget("ChildFx"));
    }

    [Fact]
    public void VisualEffect_ForwardsGpuEventsToTarget()
    {
        Scene scene = CreateScene(enable: true);
        GameObject sourceGo = CreateGameObject("SourceFx");
        GameObject targetGo = CreateGameObject("ChildFx");
        scene.Add(sourceGo);
        scene.Add(targetGo);
        VisualEffect source = sourceGo.AddComponent<VisualEffect>();
        VisualEffect target = targetGo.AddComponent<VisualEffect>();
        source.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        source.ApplyGraph();
        source.EventTarget = target;
        source.ForwardGpuEvents(died: true, collided: false);
        Assert.Equal(2u, target.PendingEventSpawn);
        source.SendEvent();
        Assert.Equal(4u, target.PendingEventSpawn);
        source.EventTarget = null;
        source.EventTargetName = "ChildFx";
        source.ForwardGpuEvents(died: true, collided: false);
        Assert.Equal(6u, target.PendingEventSpawn);
        target.Stop();
        Assert.Equal(0u, target.PendingEventSpawn);
    }

    [Fact]
    public void VisualEffect_PlayAndAlways_QueuesEvents()
    {
        Scene scene = CreateScene(enable: true);
        GameObject sourceGo = CreateGameObject("SourceFx");
        GameObject targetGo = CreateGameObject("ChildFx");
        scene.Add(sourceGo);
        scene.Add(targetGo);
        VisualEffect source = sourceGo.AddComponent<VisualEffect>();
        VisualEffect target = targetGo.AddComponent<VisualEffect>();
        source.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        source.ApplyGraph();
        source.EventTarget = target;
        source.EventKind = VfxGpuEvent.KindOnPlay;
        source.EventCount = 3f;
        source.Play();
        Assert.Equal(3u, target.PendingEventSpawn);
        source.EventKind = VfxGpuEvent.KindAlways;
        source.PulseAlwaysEvents();
        Assert.Equal(6u, target.PendingEventSpawn);
        source.EventKind = VfxGpuEvent.KindOnDie;
        source.PulseAlwaysEvents();
        Assert.Equal(6u, target.PendingEventSpawn);
        target.Stop();
        Assert.Equal(0u, target.PendingEventSpawn);
    }

    [Fact]
    public void VisualEffect_SetFloat_GpuEvent_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.EventKind));
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.EventKind));
        Assert.Equal(2f, vfx.GetFloat(VfxExposedNames.EventCount));
        vfx.SetFloat(VfxExposedNames.EventKind, 2f);
        vfx.SetFloat(VfxExposedNames.EventCount, 4f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.EventKind);
        Assert.Equal(4f, vfx.EventCount);
        vfx.ResetExposedOverrides();
        Assert.Equal(1f, vfx.EventKind);
        Assert.Equal(2f, vfx.EventCount);
    }

    [Fact]
    public void VfxDepthCollision_KillAndBounce_AgainstNearDepth()
    {
        Assert.True(VfxDepthCollision.TryProject(0f, 0.4f, 0f, out float u, out float v, out float eye));
        Assert.InRange(u, 0f, 1f);
        Assert.InRange(v, 0f, 1f);
        Assert.True(eye > 1f);
        Assert.Equal(VfxDepthCollision.Far, VfxDepthCollision.LinearEyeDepth(1f), 2);
        Assert.Equal(VfxDepthCollision.Near, VfxDepthCollision.LinearEyeDepth(0f), 2);
        Assert.Equal(5f, VfxDepthCollision.LinearEyeDepth(VfxDepthCollision.RawFromEye(5f)), 2);

        float px = 0f, py = 0.4f, pz = 0f, vx = 0f, vy = -1f, vz = 0f;
        uint alive = 1u;
        VfxDepthCollision.Collide(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            1f, VfxDepthCollision.ModeKill, 0.4f);
        Assert.Equal(1u, alive);

        VfxDepthCollision.Collide(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, VfxDepthCollision.ModeKill, 0.4f);
        Assert.Equal(0u, alive);

        px = 0f; py = 0.4f; pz = 0f; vx = 0f; vy = -1f; vz = 0f; alive = 1u;
        VfxDepthCollision.Collide(
            ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
            0f, VfxDepthCollision.ModeBounce, 0.5f);
        Assert.Equal(1u, alive);
        Assert.True(vz > 0f);
        Assert.True(pz > 0f);
    }

    [Fact]
    public void VisualEffect_SetFloat_DepthCollision_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.DepthCollisionMode));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.DepthCollisionMode));
        vfx.SetFloat(VfxExposedNames.DepthCollisionMode, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.DepthCollisionMode);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.DepthCollisionMode);
    }

    [Fact]
    public void VfxConform_SnapsOutsideParticleOntoSphere()
    {
        float px = 0f, py = 1.2f, pz = 0f;
        VfxConform.Apply(
            ref px, ref py, ref pz,
            0f, 0.55f, 0f, 0.4f, 0.4f, 0.4f, VfxCollision.ShapeSphere,
            attraction: 0f, dt: 1f);
        Assert.Equal(1.2f, py, 3);

        VfxConform.Apply(
            ref px, ref py, ref pz,
            0f, 0.55f, 0f, 0.4f, 0.4f, 0.4f, VfxCollision.ShapeSphere,
            attraction: 1f, dt: 1f);
        float dist = VfxCollision.SampleSdf(
            px, py, pz, 0f, 0.55f, 0f, 0.4f, 0.4f, 0.4f, VfxCollision.ShapeSphere);
        Assert.Equal(0f, dist, 2);
    }

    [Fact]
    public void VisualEffect_SetFloat_ConformAttraction_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.ConformAttraction));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.ConformAttraction));
        vfx.SetFloat(VfxExposedNames.ConformAttraction, 4f);
        vfx.ApplyGraph();
        Assert.Equal(4f, vfx.ConformAttraction);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.ConformAttraction);
    }

    [Fact]
    public void VfxVectorField_AddsConstantWind()
    {
        Span<float> field = stackalloc float[3];
        VfxVectorField.BakeConstant(field, 1, 1f, 0f, 0f);
        float vx = 0f, vy = 0f, vz = 0f;
        VfxVectorField.Apply(
            ref vx, ref vy, ref vz,
            0f, 0.55f, 0f, 0f, 0.55f, 0f, 0.4f, 0.4f, 0.4f,
            intensity: 2f, dt: 0.5f, field, 1, 1, 1);
        Assert.Equal(1f, vx, 3);
        Assert.Equal(0f, vy, 3);
        Assert.Equal(0f, vz, 3);

        VfxVectorField.Apply(
            ref vx, ref vy, ref vz,
            0f, 0.55f, 0f, 0f, 0.55f, 0f, 0.4f, 0.4f, 0.4f,
            intensity: 0f, dt: 1f, field, 1, 1, 1);
        Assert.Equal(1f, vx, 3);

        float ox = 0f, oy = 0f, oz = 0f;
        VfxVectorField.Apply(
            ref ox, ref oy, ref oz,
            4f, 0.55f, 0f, 0f, 0.55f, 0f, 0.4f, 0.4f, 0.4f,
            intensity: 2f, dt: 1f, field, 1, 1, 1);
        Assert.Equal(0f, ox, 3);

        VfxVectorField.DecodeUnorm(0.5f, 1f, 0f, out float dx, out float dy, out float dz);
        Assert.Equal(0f, dx, 3);
        Assert.Equal(1f, dy, 3);
        Assert.Equal(-1f, dz, 3);
    }

    [Fact]
    public void VisualEffect_SetFloat_VectorField_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.VectorFieldIntensity));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.VectorFieldIntensity));
        vfx.SetFloat(VfxExposedNames.VectorFieldIntensity, 2.5f);
        vfx.ApplyGraph();
        Assert.Equal(2.5f, vfx.VectorFieldIntensity);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.VectorFieldIntensity);
    }

    [Fact]
    public void VisualEffect_SetTexture3D_VectorField_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasTexture3D(VfxExposedNames.VectorField));
        var field = new Texture3D(2, 2, 2, false, TextureImageFormat.Float);
        vfx.SetTexture3D(VfxExposedNames.VectorField, field);
        Assert.Same(field, vfx.GetTexture3D(VfxExposedNames.VectorField));
        Assert.Same(field, vfx.VectorField);
        vfx.ResetExposedOverrides();
        Assert.Null(vfx.GetTexture3D(VfxExposedNames.VectorField));
        field.Dispose();
    }

    [Fact]
    public void VfxSpawnOverDistance_EmitsAlongTravel()
    {
        float leftover = 0f, lx = 0f, ly = 0f, lz = 0f;
        bool hasPrev = false;
        Assert.Equal(0u, VfxSpawnOverDistance.Apply(
            ref leftover, ref lx, ref ly, ref lz, ref hasPrev, 0f, 0f, 0f, 4f, 64));
        Assert.Equal(4u, VfxSpawnOverDistance.Apply(
            ref leftover, ref lx, ref ly, ref lz, ref hasPrev, 1f, 0f, 0f, 4f, 64));
        leftover = 0f;
        hasPrev = false;
        VfxSpawnOverDistance.Apply(ref leftover, ref lx, ref ly, ref lz, ref hasPrev, 0f, 0f, 0f, 2.5f, 64);
        Assert.Equal(2u, VfxSpawnOverDistance.Apply(
            ref leftover, ref lx, ref ly, ref lz, ref hasPrev, 1f, 0f, 0f, 2.5f, 64));
        Assert.Equal(1u, VfxSpawnOverDistance.Apply(
            ref leftover, ref lx, ref ly, ref lz, ref hasPrev, 1.2f, 0f, 0f, 2.5f, 64));
        Assert.Equal(0u, VfxSpawnOverDistance.Apply(
            ref leftover, ref lx, ref ly, ref lz, ref hasPrev, 8f, 0f, 0f, 0f, 64));
        leftover = 0f;
        hasPrev = false;
        VfxSpawnOverDistance.Apply(ref leftover, ref lx, ref ly, ref lz, ref hasPrev, 0f, 0f, 0f, 100f, 8);
        Assert.Equal(8u, VfxSpawnOverDistance.Apply(
            ref leftover, ref lx, ref ly, ref lz, ref hasPrev, 1f, 0f, 0f, 100f, 8));
    }

    [Fact]
    public void VfxPeriodicBurst_EmitsAfterEachPeriod()
    {
        float leftover = 0f;
        Assert.Equal(0u, VfxPeriodicBurst.Apply(ref leftover, 0.4f, 8f, 1f, 64));
        Assert.Equal(8u, VfxPeriodicBurst.Apply(ref leftover, 0.6f, 8f, 1f, 64));
        Assert.Equal(0u, VfxPeriodicBurst.Apply(ref leftover, 0.2f, 8f, 1f, 64));
        leftover = 0f;
        Assert.Equal(16u, VfxPeriodicBurst.Apply(ref leftover, 2f, 8f, 1f, 64));
        leftover = 0f;
        Assert.Equal(0u, VfxPeriodicBurst.Apply(ref leftover, 1f, 0f, 1f, 64));
        Assert.Equal(0u, VfxPeriodicBurst.Apply(ref leftover, 1f, 8f, 0f, 64));
        leftover = 0f;
        Assert.Equal(5u, VfxPeriodicBurst.Apply(ref leftover, 1f, 8f, 1f, 5));
    }

    [Fact]
    public void VfxTriggerEventRate_EmitsAtRate()
    {
        float leftover = 0f;
        Assert.Equal(0u, VfxTriggerEventRate.Apply(ref leftover, 0.1f, 8f, 64));
        Assert.Equal(1u, VfxTriggerEventRate.Apply(ref leftover, 0.1f, 8f, 64));
        Assert.Equal(1u, VfxTriggerEventRate.Apply(ref leftover, 0.125f, 8f, 64));
        leftover = 0f;
        Assert.Equal(0u, VfxTriggerEventRate.Apply(ref leftover, 1f, 0f, 64));
        leftover = 0f;
        Assert.Equal(2u, VfxTriggerEventRate.Apply(ref leftover, 1f, 8f, 2));
    }

    [Fact]
    public void VfxTriggerEventOverDistance_EmitsAlongTravel()
    {
        float leftover = 0f;
        Assert.Equal(0u, VfxTriggerEventOverDistance.Apply(
            ref leftover, false, 0f, 0f, 0f, 1f, 0f, 0f, 4f, 64));
        Assert.Equal(4u, VfxTriggerEventOverDistance.Apply(
            ref leftover, true, 0f, 0f, 0f, 1f, 0f, 0f, 4f, 64));
        leftover = 0f;
        Assert.Equal(0u, VfxTriggerEventOverDistance.Apply(
            ref leftover, true, 0f, 0f, 0f, 0.2f, 0f, 0f, 2.5f, 64));
        Assert.Equal(2u, VfxTriggerEventOverDistance.Apply(
            ref leftover, true, 0f, 0f, 0f, 0.8f, 0f, 0f, 2.5f, 64));
        leftover = 0f;
        Assert.Equal(0u, VfxTriggerEventOverDistance.Apply(
            ref leftover, true, 0f, 0f, 0f, 8f, 0f, 0f, 0f, 64));
        leftover = 0f;
        Assert.Equal(2u, VfxTriggerEventOverDistance.Apply(
            ref leftover, true, 0f, 0f, 0f, 1f, 0f, 0f, 8f, 2));
    }

    [Fact]
    public void VfxTriggerEventOnce_FiresOnce()
    {
        bool fired = false;
        Assert.Equal(0u, VfxTriggerEventOnce.Apply(ref fired, 0f, 64));
        Assert.False(fired);
        Assert.Equal(3u, VfxTriggerEventOnce.Apply(ref fired, 3f, 64));
        Assert.True(fired);
        Assert.Equal(0u, VfxTriggerEventOnce.Apply(ref fired, 3f, 64));
        fired = false;
        Assert.Equal(2u, VfxTriggerEventOnce.Apply(ref fired, 8f, 2));
    }

    [Fact]
    public void VfxCollisionFriction_ScalesTangent()
    {
        float vx = 3f, vy = 2f, vz = 0f;
        VfxCollisionFriction.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, 0f);
        Assert.Equal(3f, vx, 3);
        Assert.Equal(2f, vy, 3);
        vx = 3f;
        vy = 2f;
        VfxCollisionFriction.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, 0.5f);
        Assert.Equal(1.5f, vx, 3);
        Assert.Equal(2f, vy, 3);
        vx = 3f;
        vy = 2f;
        VfxCollisionFriction.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, 1f);
        Assert.Equal(0f, vx, 3);
        Assert.Equal(2f, vy, 3);
    }

    [Fact]
    public void VfxCollisionElasticity_ScalesNormal()
    {
        float vx = 3f, vy = 2f, vz = 0f;
        VfxCollisionElasticity.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, 0f);
        Assert.Equal(3f, vx, 3);
        Assert.Equal(2f, vy, 3);
        vx = 3f;
        vy = 2f;
        VfxCollisionElasticity.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, 0.5f);
        Assert.Equal(3f, vx, 3);
        Assert.Equal(1f, vy, 3);
        vx = 3f;
        vy = 2f;
        VfxCollisionElasticity.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, 1f);
        Assert.Equal(3f, vx, 3);
        Assert.Equal(0f, vy, 3);
    }

    [Fact]
    public void VfxCollisionLifetimeLoss_ScalesRemaining()
    {
        Assert.Equal(3f, VfxCollisionLifetimeLoss.Apply(1f, 3f, 0f), 3);
        Assert.Equal(2f, VfxCollisionLifetimeLoss.Apply(1f, 3f, 0.5f), 3);
        Assert.Equal(1f, VfxCollisionLifetimeLoss.Apply(1f, 3f, 1f), 3);
    }

    [Fact]
    public void VfxLimitVelocity_ClampsSpeed()
    {
        float vx = 3f, vy = 4f, vz = 0f;
        VfxLimitVelocity.Apply(ref vx, ref vy, ref vz, 0f);
        Assert.Equal(3f, vx, 3);
        Assert.Equal(4f, vy, 3);
        vx = 3f;
        vy = 4f;
        VfxLimitVelocity.Apply(ref vx, ref vy, ref vz, 10f);
        Assert.Equal(3f, vx, 3);
        Assert.Equal(4f, vy, 3);
        vx = 3f;
        vy = 4f;
        VfxLimitVelocity.Apply(ref vx, ref vy, ref vz, 2.5f);
        Assert.Equal(1.5f, vx, 3);
        Assert.Equal(2f, vy, 3);
    }

    [Fact]
    public void VfxKillSphere_KillsInside()
    {
        Assert.False(VfxKillSphere.Apply(0.5f, 0f, 0f, 0f, 0f, 0f, 0f));
        Assert.True(VfxKillSphere.Apply(0.5f, 0f, 0f, 0f, 0f, 0f, 1f));
        Assert.False(VfxKillSphere.Apply(2f, 0f, 0f, 0f, 0f, 0f, 1f));
    }

    [Fact]
    public void VfxKillAABox_KillsInside()
    {
        Assert.False(VfxKillAABox.Apply(0.5f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f));
        Assert.True(VfxKillAABox.Apply(0.5f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f));
        Assert.False(VfxKillAABox.Apply(2f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f));
    }

    [Fact]
    public void VfxAttract_PullsTowardTarget()
    {
        float vx = 0f, vy = 0f, vz = 0f;
        VfxAttract.Apply(ref vx, ref vy, ref vz, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0.5f);
        Assert.Equal(0f, vx, 3);
        vx = 0f;
        VfxAttract.Apply(ref vx, ref vy, ref vz, 1f, 0f, 0f, 0f, 0f, 0f, 2f, 0.5f);
        Assert.Equal(-1f, vx, 3);
        Assert.Equal(0f, vy, 3);
    }

    [Fact]
    public void VfxConformToSphere_PullsTowardSurface()
    {
        float vx = 0f, vy = 0f, vz = 0f;
        VfxConformToSphere.Apply(ref vx, ref vy, ref vz, 2f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0.5f);
        Assert.Equal(0f, vx, 3);
        vx = 0f;
        VfxConformToSphere.Apply(ref vx, ref vy, ref vz, 2f, 0f, 0f, 0f, 0f, 0f, 1f, 2f, 0.5f);
        Assert.Equal(-1f, vx, 3);
        Assert.Equal(0f, vy, 3);
    }

    [Fact]
    public void VfxVortex_SpinsAroundAxis()
    {
        float vx = 0f, vy = 0f, vz = 0f;
        VfxVortex.Apply(ref vx, ref vy, ref vz, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0.5f);
        Assert.Equal(0f, vx, 3);
        vx = 0f; vz = 0f;
        VfxVortex.Apply(ref vx, ref vy, ref vz, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 2f, 0f, 0.5f);
        Assert.Equal(0f, vx, 3);
        Assert.Equal(0f, vy, 3);
        Assert.Equal(-1f, vz, 3);
        vx = 0f; vz = 0f;
        VfxVortex.Apply(ref vx, ref vy, ref vz, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 2f, 0.5f);
        Assert.Equal(-1f, vx, 3);
        Assert.Equal(0f, vz, 3);
    }

    [Fact]
    public void VfxConformToCylinder_PullsTowardSurface()
    {
        float vx = 0f, vy = 0f, vz = 0f;
        VfxConformToCylinder.Apply(ref vx, ref vy, ref vz, 2f, 5f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f, 0.5f);
        Assert.Equal(0f, vx, 3);
        vx = 0f; vy = 0f;
        VfxConformToCylinder.Apply(ref vx, ref vy, ref vz, 2f, 5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 2f, 0.5f);
        Assert.Equal(0f, vx, 3);
        vx = 0f; vy = 0f;
        VfxConformToCylinder.Apply(ref vx, ref vy, ref vz, 2f, 5f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 2f, 0.5f);
        Assert.Equal(-1f, vx, 3);
        Assert.Equal(0f, vy, 3);
        Assert.Equal(0f, vz, 3);
    }

    [Fact]
    public void VfxOrientLookAt_PointsTowardTarget()
    {
        VfxOrientLookAt.Apply(1f, 0f, 0f, 0f, 0f, 0f, out float ax, out float ay, out float az);
        Assert.Equal(-1f, ax, 3);
        Assert.Equal(0f, ay, 3);
        Assert.Equal(0f, az, 3);
    }

    [Fact]
    public void VfxFlipbook_Frame_UsesRateOrLifetime()
    {
        Assert.Equal(2f, VfxFlipbook.Frame(0.5f, 1f, 4f, 0f), 3);
        Assert.Equal(1f, VfxFlipbook.Frame(0.5f, 1f, 4f, 2f), 3);
        Assert.Equal(1f, VfxFlipbook.Frame(0.5f, 1f, 4f, 10f), 3);
    }

    [Fact]
    public void VfxFlipbook_Frame_AppliesStart()
    {
        Assert.Equal(2f, VfxFlipbook.Frame(0.5f, 1f, 4f, 0f, 0f), 3);
        Assert.Equal(2f, VfxFlipbook.Frame(0f, 1f, 4f, 0f, 2f), 3);
        Assert.Equal(1f, VfxFlipbook.Frame(0.5f, 1f, 4f, 0f, 3f), 3);
    }

    [Fact]
    public void VfxKillPlane_KillsBackSide()
    {
        Assert.False(VfxKillPlane.Apply(0f, -1f, 0f, 0f, 0f, 0f, 0f));
        Assert.True(VfxKillPlane.Apply(0f, -1f, 0f, 0f, 1f, 0f, 0f));
        Assert.False(VfxKillPlane.Apply(0f, 1f, 0f, 0f, 1f, 0f, 0f));
    }

    [Fact]
    public void VisualEffect_SetFloat_SpawnOverDistance_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.SpawnOverDistance));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.SpawnOverDistance));
        vfx.SetFloat(VfxExposedNames.SpawnOverDistance, 8f);
        vfx.ApplyGraph();
        Assert.Equal(8f, vfx.SpawnOverDistance);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.SpawnOverDistance);
    }

    [Fact]
    public void VfxAttributeMap_AppliesPositionColorSize()
    {
        float[] map = { 1f, 0.5f, 0.25f, 0.8f };
        float px = 0f, py = 0f, pz = 0f, size = 0.2f, cr = 0f, cg = 0f, cb = 0f, ca = 1f;
        VfxAttributeMap.Apply(
            ref px, ref py, ref pz, ref size, ref cr, ref cg, ref cb, ref ca,
            1f, 2f, 3f, 0.2f, VfxAttributeMap.ModeOff, 2f, map, 1, 1, 0);
        Assert.Equal(0f, px);
        Assert.Equal(0.2f, size);

        VfxAttributeMap.Apply(
            ref px, ref py, ref pz, ref size, ref cr, ref cg, ref cb, ref ca,
            1f, 2f, 3f, 0.2f, VfxAttributeMap.ModePosition, 2f, map, 1, 1, 0);
        Assert.Equal(3f, px, 3);
        Assert.Equal(2f, py, 3);
        Assert.Equal(2f, pz, 3);

        VfxAttributeMap.Apply(
            ref px, ref py, ref pz, ref size, ref cr, ref cg, ref cb, ref ca,
            0f, 0f, 0f, 0.2f, VfxAttributeMap.ModeColor, 1f, map, 1, 1, 0);
        Assert.Equal(1f, cr, 3);
        Assert.Equal(0.5f, cg, 3);
        Assert.Equal(0.25f, cb, 3);
        Assert.Equal(0.8f, ca, 3);

        size = 0.2f;
        float[] sizeMap = { 0.5f, 0f, 0f, 1f };
        VfxAttributeMap.Apply(
            ref px, ref py, ref pz, ref size, ref cr, ref cg, ref cb, ref ca,
            0f, 0f, 0f, 0.2f, VfxAttributeMap.ModeSize, 1f, sizeMap, 1, 1, 0);
        Assert.Equal(0.1f, size, 3);

        float[] wrap = { 0.5f, 0.5f, 0.5f, 1f, 1f, 0.5f, 0.5f, 1f };
        px = 0f;
        VfxAttributeMap.Apply(
            ref px, ref py, ref pz, ref size, ref cr, ref cg, ref cb, ref ca,
            0f, 0f, 0f, 0.2f, VfxAttributeMap.ModePosition, 1f, wrap, 2, 1, 3);
        Assert.Equal(1f, px, 3);
    }

    [Fact]
    public void VisualEffect_SetFloat_AttributeMap_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.AttributeMapMode));
        Assert.True(vfx.HasFloat(VfxExposedNames.AttributeMapScale));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.AttributeMapMode));
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.AttributeMapScale));
        vfx.SetFloat(VfxExposedNames.AttributeMapMode, 4f);
        vfx.SetFloat(VfxExposedNames.AttributeMapScale, 2.5f);
        vfx.ApplyGraph();
        Assert.Equal(4f, vfx.AttributeMapMode);
        Assert.Equal(2.5f, vfx.AttributeMapScale);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.AttributeMapMode);
        Assert.Equal(1f, vfx.AttributeMapScale);
    }

    [Fact]
    public void VisualEffect_SetTexture_AttributeMap_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasTexture(VfxExposedNames.AttributeMap));
        var tex = new Texture2D(1, 1);
        vfx.SetTexture(VfxExposedNames.AttributeMap, tex);
        Assert.Same(tex, vfx.GetTexture(VfxExposedNames.AttributeMap));
        Assert.Same(tex, vfx.AttributeMap);
        vfx.ResetExposedOverrides();
        Assert.Null(vfx.GetTexture(VfxExposedNames.AttributeMap));
        tex.Dispose();
    }

    [Fact]
    public void VfxDistortion_OffsetsUvAndBlendsScene()
    {
        VfxDistortion.OffsetUv(0.5f, 0.5f, 0f, 1f, 0f, out float u0, out float v0);
        Assert.Equal(0.5f, u0, 3);
        Assert.Equal(0.5f, v0, 3);
        VfxDistortion.OffsetUv(0.5f, 0.5f, 0.25f, 1f, 0f, out float u1, out float v1);
        Assert.Equal(0.75f, u1, 3);
        Assert.Equal(0.25f, v1, 3);
        VfxDistortion.OffsetUv(0.95f, 0.05f, 1f, 1f, 0f, out float u2, out float v2);
        Assert.Equal(1f, u2, 3);
        Assert.Equal(0f, v2, 3);

        float r = 0f, g = 0f, b = 0f;
        VfxDistortion.Apply(ref r, ref g, ref b, 1f, 0.5f, 0.25f, 1f, 0f);
        Assert.Equal(0f, r);
        VfxDistortion.Apply(ref r, ref g, ref b, 1f, 0.5f, 0.25f, 1f, 0.5f);
        Assert.Equal(0.5f, r, 3);
        Assert.Equal(0.25f, g, 3);
        Assert.Equal(0.125f, b, 3);
    }

    [Fact]
    public void VisualEffect_SetFloat_Distortion_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.DistortionIntensity));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.DistortionIntensity));
        vfx.SetFloat(VfxExposedNames.DistortionIntensity, 0.35f);
        vfx.ApplyGraph();
        Assert.Equal(0.35f, vfx.DistortionIntensity);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.DistortionIntensity);
    }

    [Fact]
    public void VisualEffect_SetTexture_SceneColor_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        var tex = new Texture2D(1, 1);
        vfx.SetTexture(VfxExposedNames.SceneColor, tex);
        Assert.Same(tex, vfx.GetTexture(VfxExposedNames.SceneColor));
        Assert.Same(tex, vfx.SceneColor);
        vfx.ResetExposedOverrides();
        Assert.Null(vfx.GetTexture(VfxExposedNames.SceneColor));
        tex.Dispose();
    }

    [Fact]
    public void VfxParticleLight_PlacesBallisticPointLights()
    {
        Assert.Equal(0, VfxParticleLight.ResolveCount(0f));
        Assert.Equal(16, VfxParticleLight.ResolveCount(100f));
        VfxParticleLight.HashDir(0, out float dx, out float dy, out float dz);
        Assert.Equal(1f, dx * dx + dy * dy + dz * dz, 3);

        Assert.False(VfxParticleLight.TryEvaluate(
            0, 4, 0f, 0f, 0f, 2f, 0f, -1.5f, 0f, 2f, 1f, 1f, 1f, 0f, 1.5f,
            out _, out _, out _, out _, out _, out _, out _, out _));
        Assert.True(VfxParticleLight.TryEvaluate(
            0, 4, 0f, 1f, 0f, 2f, 0f, -1.5f, 0f, 2f, 1f, 0.5f, 0.25f, 2.5f, 1.5f,
            out float px, out float py, out float pz, out float cr, out float cg, out float cb,
            out float intensity, out float range));
        Assert.True(Math.Abs(px) + Math.Abs(py - 1f) + Math.Abs(pz) > 0.05f);
        Assert.Equal(1f, cr, 3);
        Assert.Equal(0.5f, cg, 3);
        Assert.Equal(0.25f, cb, 3);
        Assert.Equal(2.5f, intensity, 3);
        Assert.Equal(1.5f, range, 3);
    }

    [Fact]
    public void VisualEffect_SetFloat_ParticleLight_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.ParticleLightIntensity));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.ParticleLightIntensity));
        Assert.Equal(1.5f, vfx.GetFloat(VfxExposedNames.ParticleLightRange));
        vfx.SetFloat(VfxExposedNames.ParticleLightIntensity, 2.5f);
        vfx.SetFloat(VfxExposedNames.ParticleLightRange, 3f);
        vfx.SetFloat(VfxExposedNames.ParticleLightCount, 8f);
        vfx.ApplyGraph();
        Assert.Equal(2.5f, vfx.ParticleLightIntensity);
        Assert.Equal(3f, vfx.ParticleLightRange);
        Assert.Equal(8f, vfx.ParticleLightCount);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.ParticleLightIntensity);
        Assert.Equal(1.5f, vfx.ParticleLightRange);
        Assert.Equal(0f, vfx.ParticleLightCount);
    }

    [Fact]
    public void VisualEffect_OnRenderCollect_AddsParticleLights()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        vfx.SetFloat(VfxExposedNames.ParticleLightIntensity, 2f);
        vfx.SetFloat(VfxExposedNames.ParticleLightCount, 4f);
        vfx.ApplyGraph();
        var renderables = new List<IRenderable>();
        var lights = new List<IRenderableLight>();
        vfx.OnRenderCollect(null!, renderables, lights);
        Assert.Equal(4, lights.Count);
        Assert.Equal(LightType.Point, lights[0].GetLightType());
        Assert.Equal(2f, lights[0].GetForwardLightData().Intensity, 3);
        Assert.False(lights[0].DoCastShadows());
        vfx.SetFloat(VfxExposedNames.ParticleLightIntensity, 0f);
        vfx.ApplyGraph();
        lights.Clear();
        vfx.OnRenderCollect(null!, renderables, lights);
        Assert.Empty(lights);
    }

    [Fact]
    public void VfxStrip_Extrude_OffsetsAlongCameraFacingSide()
    {
        VfxStrip.Extrude(
            0f, 0f, 0f, 1f, 0f, 0f,
            0.5f, 0.5f, 2f,
            0.5f, 0f, 1f,
            out float ox, out float oy, out float oz);
        Assert.Equal(0.5f, ox, 3);
        Assert.True(Math.Abs(oy) + Math.Abs(oz) > 0.1f);

        VfxStrip.Extrude(
            0f, 0f, 0f, 1f, 0f, 0f,
            0f, 0.25f, 2f,
            0.5f, 0f, 1f,
            out float mx, out float my, out float mz);
        Assert.Equal(0.25f, mx, 3);
        Assert.Equal(0f, my, 3);
        Assert.Equal(0f, mz, 3);
    }

    [Fact]
    public void VfxSort_OrdersLiveIndicesFarToNear()
    {
        Assert.False(VfxSort.IsEnabled(0f));
        Assert.True(VfxSort.IsEnabled(1f));
        uint packed = VfxSort.PackIndexCount(6, 0, 1f);
        Assert.True(VfxSort.IsEnabled(packed));
        Assert.Equal(6u, packed & 0xffffu);
        Assert.Equal(8u, VfxSort.NextPow2(5));
        Assert.Equal(6, VfxSort.BitonicPassCount(8));

        float[] px = [0f, 0f, 0f];
        float[] py = [0f, 0f, 0f];
        float[] pz = [1f, 3f, 2f];
        uint[] live = [0, 1, 2];
        VfxSort.SortLiveByCamera(live, 3, px, py, pz, 0f, 0f, 0f);
        Assert.Equal(1u, live[0]);
        Assert.Equal(2u, live[1]);
        Assert.Equal(0u, live[2]);

        Assert.True(VfxSort.BitonicShouldSwap(0, 2, 1f, 4f));
        Assert.False(VfxSort.BitonicShouldSwap(0, 2, 4f, 1f));
    }

    [Fact]
    public void VfxCameraFade_FadesNearAndFar()
    {
        Assert.Equal(1f, VfxCameraFade.Fade(1f, 0f, 0f));
        Assert.Equal(0f, VfxCameraFade.Fade(0f, 0.5f, 0f));
        Assert.Equal(1f, VfxCameraFade.Fade(0.5f, 0.5f, 0f));
        Assert.Equal(0.5f, VfxCameraFade.Fade(0.25f, 0.5f, 0f), 3);
        Assert.Equal(1f, VfxCameraFade.Fade(0f, 0f, 2f));
        Assert.Equal(0.5f, VfxCameraFade.Fade(1f, 0f, 2f), 3);
        Assert.Equal(0f, VfxCameraFade.Fade(2f, 0f, 2f));
        Assert.Equal(0.4375f, VfxCameraFade.Fade(0.25f, 0.5f, 2f), 3);
    }

    [Fact]
    public void VisualEffect_SetFloat_CameraFade_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.CameraFadeNear));
        Assert.True(vfx.HasFloat(VfxExposedNames.CameraFadeFar));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.CameraFadeNear));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.CameraFadeFar));
        vfx.SetFloat(VfxExposedNames.CameraFadeNear, 0.4f);
        vfx.SetFloat(VfxExposedNames.CameraFadeFar, 2.5f);
        vfx.ApplyGraph();
        Assert.Equal(0.4f, vfx.CameraFadeNear, 3);
        Assert.Equal(2.5f, vfx.CameraFadeFar, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.CameraFadeNear);
        Assert.Equal(0f, vfx.CameraFadeFar);
    }

    [Fact]
    public void VisualEffect_SetFloat_FlipbookBlend_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.FlipbookBlend));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.FlipbookBlend));
        vfx.SetFloat(VfxExposedNames.FlipbookBlend, 1f);
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.FlipbookBlend);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.FlipbookBlend);
    }

    [Fact]
    public void VisualEffect_SetFloat_FlipbookMotion_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.FlipbookMotionScale));
        vfx.SetFloat(VfxExposedNames.FlipbookMotionScale, 1.25f);
        vfx.ApplyGraph();
        Assert.Equal(1.25f, vfx.FlipbookMotionScale, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.FlipbookMotionScale);
    }

    [Fact]
    public void VisualEffect_SetFloat_InheritVelocity_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.InheritVelocity));
        vfx.SetFloat(VfxExposedNames.InheritVelocity, 0.75f);
        vfx.ApplyGraph();
        Assert.Equal(0.75f, vfx.InheritVelocityAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.InheritVelocityAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_TriggerEventRate_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.TriggerEventRate));
        vfx.SetFloat(VfxExposedNames.TriggerEventRate, 8f);
        vfx.ApplyGraph();
        Assert.Equal(8f, vfx.TriggerEventRate, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.TriggerEventRate);
    }

    [Fact]
    public void VisualEffect_SetFloat_TriggerEventOverDistance_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.TriggerEventOverDistance));
        vfx.SetFloat(VfxExposedNames.TriggerEventOverDistance, 8f);
        vfx.ApplyGraph();
        Assert.Equal(8f, vfx.TriggerEventOverDistance, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.TriggerEventOverDistance);
    }

    [Fact]
    public void VisualEffect_SetFloat_TriggerEventOnce_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.TriggerEventOnce));
        vfx.SetFloat(VfxExposedNames.TriggerEventOnce, 3f);
        vfx.ApplyGraph();
        Assert.Equal(3f, vfx.TriggerEventOnceCount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.TriggerEventOnceCount);
    }

    [Fact]
    public void VisualEffect_SetFloat_CollisionFriction_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.CollisionFriction));
        vfx.SetFloat(VfxExposedNames.CollisionFriction, 0.4f);
        vfx.ApplyGraph();
        Assert.Equal(0.4f, vfx.CollisionFrictionAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.CollisionFrictionAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_CollisionElasticity_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.CollisionElasticity));
        vfx.SetFloat(VfxExposedNames.CollisionElasticity, 0.4f);
        vfx.ApplyGraph();
        Assert.Equal(0.4f, vfx.CollisionElasticityAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.CollisionElasticityAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_CollisionLifetimeLoss_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.CollisionLifetimeLoss));
        vfx.SetFloat(VfxExposedNames.CollisionLifetimeLoss, 0.4f);
        vfx.ApplyGraph();
        Assert.Equal(0.4f, vfx.CollisionLifetimeLossAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.CollisionLifetimeLossAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_LimitVelocity_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.LimitVelocity));
        vfx.SetFloat(VfxExposedNames.LimitVelocity, 4f);
        vfx.ApplyGraph();
        Assert.Equal(4f, vfx.LimitVelocitySpeed, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.LimitVelocitySpeed);
    }

    [Fact]
    public void VisualEffect_SetFloat_KillSphere_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.KillSphere));
        vfx.SetFloat(VfxExposedNames.KillSphere, 1.5f);
        vfx.ApplyGraph();
        Assert.Equal(1.5f, vfx.KillSphereRadius, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.KillSphereRadius);
    }

    [Fact]
    public void VisualEffect_SetFloat_KillAABox_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.KillAABox));
        vfx.SetFloat(VfxExposedNames.KillAABox, 1.5f);
        vfx.ApplyGraph();
        Assert.Equal(1.5f, vfx.KillAABoxSx, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.KillAABoxSx);
    }

    [Fact]
    public void VisualEffect_SetFloat_Attract_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.Attract));
        vfx.SetFloat(VfxExposedNames.Attract, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.AttractStrength, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.AttractStrength);
    }

    [Fact]
    public void VisualEffect_SetFloat_ConformToSphere_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.ConformToSphere));
        vfx.SetFloat(VfxExposedNames.ConformToSphere, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.ConformSphereStrength, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.ConformSphereStrength);
    }

    [Fact]
    public void VisualEffect_SetFloat_Vortex_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.Vortex));
        vfx.SetFloat(VfxExposedNames.Vortex, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.VortexRotation, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.VortexRotation);
    }

    [Fact]
    public void VisualEffect_SetFloat_ConformToCylinder_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.ConformToCylinder));
        vfx.SetFloat(VfxExposedNames.ConformToCylinder, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.ConformCylinderStrength, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.ConformCylinderStrength);
    }

    [Fact]
    public void VisualEffect_SetFloat_OrientLookAt_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.OrientLookAt));
        vfx.SetFloat(VfxExposedNames.OrientLookAt, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.LookAtMode, 3);
        Assert.Equal(2f, vfx.OrientMode, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.LookAtMode);
    }

    [Fact]
    public void VisualEffect_SetFloat_FlipbookRate_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.FlipbookRate));
        vfx.SetFloat(VfxExposedNames.FlipbookRate, 8f);
        vfx.ApplyGraph();
        Assert.Equal(8f, vfx.FlipbookRate, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.FlipbookRate);
    }

    [Fact]
    public void VisualEffect_SetFloat_FlipbookStart_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.FlipbookStart));
        vfx.SetFloat(VfxExposedNames.FlipbookStart, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.FlipbookStart, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.FlipbookStart);
    }

    [Fact]
    public void VisualEffect_SetFloat_KillPlane_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.KillPlane));
        vfx.SetFloat(VfxExposedNames.KillPlane, 0.5f);
        vfx.ApplyGraph();
        Assert.Equal(0.5f, vfx.KillPlaneDistance, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.KillPlaneDistance);
    }

    [Fact]
    public void VisualEffect_SetFloat_SizeOverSpeed_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.SizeOverSpeed));
        vfx.SetFloat(VfxExposedNames.SizeOverSpeed, 0.5f);
        vfx.ApplyGraph();
        Assert.Equal(0.5f, vfx.SizeOverSpeedAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.SizeOverSpeedAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_MultiplySize_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.MultiplySize));
        vfx.SetFloat(VfxExposedNames.MultiplySize, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.MultiplySizeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(1f, vfx.MultiplySizeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_MultiplyColor_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.MultiplyColor));
        vfx.SetFloat(VfxExposedNames.MultiplyColor, 0.5f);
        vfx.ApplyGraph();
        Assert.Equal(0.5f, vfx.MultiplyColorR, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(1f, vfx.MultiplyColorR);
    }

    [Fact]
    public void VisualEffect_SetFloat_AddSize_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.AddSize));
        vfx.SetFloat(VfxExposedNames.AddSize, 0.1f);
        vfx.ApplyGraph();
        Assert.Equal(0.1f, vfx.AddSizeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.AddSizeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_AddColor_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.AddColor));
        vfx.SetFloat(VfxExposedNames.AddColor, 0.25f);
        vfx.ApplyGraph();
        Assert.Equal(0.25f, vfx.AddColorR, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.AddColorR);
    }

    [Fact]
    public void VisualEffect_SetFloat_AddVelocity_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.AddVelocity));
        vfx.SetFloat(VfxExposedNames.AddVelocity, 1.5f);
        vfx.ApplyGraph();
        Assert.Equal(1.5f, vfx.AddVelocityX, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.AddVelocityX);
    }

    [Fact]
    public void VisualEffect_SetFloat_MultiplyVelocity_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.MultiplyVelocity));
        vfx.SetFloat(VfxExposedNames.MultiplyVelocity, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.MultiplyVelocityAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(1f, vfx.MultiplyVelocityAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_OverwriteSize_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.OverwriteSize));
        vfx.SetFloat(VfxExposedNames.OverwriteSize, 0.2f);
        vfx.ApplyGraph();
        Assert.Equal(0.2f, vfx.OverwriteSizeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.OverwriteSizeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_OverwriteColor_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.OverwriteColor));
        vfx.SetFloat(VfxExposedNames.OverwriteColor, 0.25f);
        vfx.ApplyGraph();
        Assert.Equal(0.25f, vfx.OverwriteColorR, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.OverwriteColorR);
    }

    [Fact]
    public void VisualEffect_SetFloat_OverwriteVelocity_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.OverwriteVelocity));
        vfx.SetFloat(VfxExposedNames.OverwriteVelocity, 1.5f);
        vfx.ApplyGraph();
        Assert.Equal(1.5f, vfx.OverwriteVelocityX, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.OverwriteVelocityX);
    }

    [Fact]
    public void VisualEffect_SetFloat_OverwriteLifetime_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.OverwriteLifetime));
        vfx.SetFloat(VfxExposedNames.OverwriteLifetime, 1.5f);
        vfx.ApplyGraph();
        Assert.Equal(1.5f, vfx.OverwriteLifetimeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.OverwriteLifetimeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_AddLifetime_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.AddLifetime));
        vfx.SetFloat(VfxExposedNames.AddLifetime, 0.5f);
        vfx.ApplyGraph();
        Assert.Equal(0.5f, vfx.AddLifetimeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.AddLifetimeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_MultiplyLifetime_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.MultiplyLifetime));
        vfx.SetFloat(VfxExposedNames.MultiplyLifetime, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.MultiplyLifetimeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(1f, vfx.MultiplyLifetimeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_AddAge_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.AddAge));
        vfx.SetFloat(VfxExposedNames.AddAge, 0.5f);
        vfx.ApplyGraph();
        Assert.Equal(0.5f, vfx.AddAgeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.AddAgeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_MultiplyAge_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.MultiplyAge));
        vfx.SetFloat(VfxExposedNames.MultiplyAge, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.MultiplyAgeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(1f, vfx.MultiplyAgeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_SetAge_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.SetAge));
        vfx.SetFloat(VfxExposedNames.SetAge, 0.5f);
        vfx.ApplyGraph();
        Assert.Equal(0.5f, vfx.SetAgeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.SetAgeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_AddPosition_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.AddPosition));
        vfx.SetFloat(VfxExposedNames.AddPosition, 0.5f);
        vfx.ApplyGraph();
        Assert.Equal(0.5f, vfx.AddPositionX, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.AddPositionX);
    }

    [Fact]
    public void VisualEffect_SetFloat_SetPosition_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.SetPosition));
        vfx.SetFloat(VfxExposedNames.SetPosition, 0.5f);
        vfx.ApplyGraph();
        Assert.Equal(0.5f, vfx.SetPositionX, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.SetPositionX);
    }

    [Fact]
    public void VisualEffect_SetFloat_ColorOverSpeed_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.ColorOverSpeed));
        vfx.SetFloat(VfxExposedNames.ColorOverSpeed, 4f);
        vfx.ApplyGraph();
        Assert.Equal(4f, vfx.ColorOverSpeedScale, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.ColorOverSpeedScale);
    }

    [Fact]
    public void VisualEffect_SetFloat_ScreenSpaceSize_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.ScreenSpaceSize));
        vfx.SetFloat(VfxExposedNames.ScreenSpaceSize, 2f);
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.ScreenSpaceSizeAmount, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.ScreenSpaceSizeAmount);
    }

    [Fact]
    public void VisualEffect_SetFloat_MotionVector_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.MotionVectorMode));
        Assert.True(vfx.HasFloat(VfxExposedNames.MotionVectorScale));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.MotionVectorMode));
        Assert.Equal(1f, vfx.GetFloat(VfxExposedNames.MotionVectorScale));
        vfx.SetFloat(VfxExposedNames.MotionVectorMode, 1f);
        vfx.SetFloat(VfxExposedNames.MotionVectorScale, 2.5f);
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.MotionVectorMode);
        Assert.Equal(2.5f, vfx.MotionVectorScale, 3);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.MotionVectorMode);
        Assert.Equal(1f, vfx.MotionVectorScale);
    }

    [Fact]
    public void VisualEffect_SetFloat_OrientMode_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.OrientMode));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.OrientMode));
        vfx.SetFloat(VfxExposedNames.OrientMode, 1f);
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.OrientMode);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.OrientMode);
    }

    [Fact]
    public void VisualEffect_SetFloat_SortMode_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.SortMode));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.SortMode));
        vfx.SetFloat(VfxExposedNames.SortMode, 1f);
        vfx.ApplyGraph();
        Assert.Equal(1f, vfx.SortMode);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.SortMode);
    }

    [Fact]
    public void VfxDecal_ProjectsOntoPlane()
    {
        Assert.True(VfxDecal.Project(
            0.5f, 2f, -0.25f, 0f, 1f, 0f, 0f,
            out float qx, out float qy, out float qz,
            out float nx, out float ny, out float nz));
        Assert.Equal(0.5f, qx, 3);
        Assert.Equal(0f, qy, 3);
        Assert.Equal(-0.25f, qz, 3);
        Assert.Equal(0f, nx, 3);
        Assert.Equal(1f, ny, 3);
        Assert.Equal(0f, nz, 3);

        Assert.False(VfxDecal.Project(
            1f, 2f, 3f, 0f, 0f, 0f, 0.5f,
            out float dx, out float dy, out float dz,
            out float dnx, out float dny, out float dnz));
        Assert.Equal(1f, dx, 3);
        Assert.Equal(-0.5f, dy, 3);
        Assert.Equal(3f, dz, 3);
        Assert.Equal(0f, dnx, 3);
        Assert.Equal(1f, dny, 3);
        Assert.Equal(0f, dnz, 3);

        VfxDecal.Extrude(
            0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f, 0.2f,
            out float wx, out float wy, out float wz);
        Assert.True(Math.Abs(wx) + Math.Abs(wz) > 0.05f);
        Assert.Equal(VfxDecal.Bias, wy, 3);
    }

    [Fact]
    public void VisualEffect_ApplyGraph_Decal_SetsOutputDecal()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateDecal());
        vfx.ApplyGraph();
        Assert.True(vfx.OutputDecal);
        Assert.False(vfx.OutputMesh);
        Assert.False(vfx.OutputStrip);
        Assert.Equal(64f, vfx.SpawnRate);
        Assert.Equal(0f, vfx.CollisionPlane.X, 3);
        Assert.Equal(1f, vfx.CollisionPlane.Y, 3);
    }

    [Fact]
    public void VisualEffect_ApplyGraph_Trail_SetsOutputStrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateTrail());
        vfx.ApplyGraph();
        Assert.True(vfx.OutputStrip);
        Assert.False(vfx.OutputMesh);
    }

    [Fact]
    public void VfxLighting_UnlitLambertBlinn()
    {
        VfxLighting.Shade(
            1f, 0.5f, 0.25f, 1f,
            0f, 0f, 1f, 0f, 0f, 1f,
            0f, 0f, 1f, 1f, 1f, 1f, 1f,
            0.2f, 0.2f, 0.2f, VfxLighting.ModeUnlit, 32f,
            out float ur, out float ug, out float ub, out float ua);
        Assert.Equal(1f, ur, 3);
        Assert.Equal(0.5f, ug, 3);
        Assert.Equal(0.25f, ub, 3);
        Assert.Equal(1f, ua, 3);

        VfxLighting.Shade(
            1f, 1f, 1f, 1f,
            0f, 0f, 1f, 0f, 0f, 1f,
            0f, 0f, 1f, 1f, 1f, 1f, 1f,
            0.2f, 0.2f, 0.2f, VfxLighting.ModeLambert, 32f,
            out float lr, out _, out _, out _);
        Assert.Equal(1.2f, lr, 3);

        VfxLighting.Shade(
            0f, 0f, 0f, 1f,
            0f, 0f, 1f, 0f, 0f, 1f,
            0f, 0f, 1f, 1f, 1f, 1f, 1f,
            0f, 0f, 0f, VfxLighting.ModeBlinn, 1f,
            out float sr, out _, out _, out _);
        Assert.Equal(1f, sr, 3);
    }

    [Fact]
    public void VisualEffect_SetFloat_Lit_RoundTrip()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateLoop());
        vfx.ApplyGraph();
        Assert.True(vfx.HasFloat(VfxExposedNames.LitMode));
        Assert.Equal(0f, vfx.GetFloat(VfxExposedNames.LitMode));
        vfx.SetFloat(VfxExposedNames.LitMode, 2f);
        vfx.SetFloat(VfxExposedNames.Shininess, 8f);
        vfx.SetVector3(VfxExposedNames.LightDirection, new Float3(0f, 1f, 0f));
        vfx.ApplyGraph();
        Assert.Equal(2f, vfx.LitMode);
        Assert.Equal(8f, vfx.Shininess);
        Assert.Equal(1f, vfx.LightDirection.Y);
        vfx.ResetExposedOverrides();
        Assert.Equal(0f, vfx.LitMode);
        Assert.Equal(32f, vfx.Shininess);
    }

    [Fact]
    public void VisualEffect_ApplyGraph_Mesh_SetsOutputMesh()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = new AssetRef<VfxGraphAsset>(VfxGraphTemplates.CreateMesh());
        vfx.ApplyGraph();
        Assert.True(vfx.OutputMesh);
        Assert.True(vfx.HasMesh(VfxExposedNames.OutputMesh));
        Assert.Equal(32f, vfx.SpawnRate);
        Mesh cube = Mesh.CreateCube(Float3.One);
        Assert.Equal(36, cube.IndexCount);
        vfx.SetMesh(VfxExposedNames.OutputMesh, cube);
        Assert.Same(cube, vfx.GetMesh(VfxExposedNames.OutputMesh));
        Assert.Same(cube, vfx.ParticleMesh);
        vfx.ResetExposedOverrides();
        Assert.Null(vfx.GetMesh(VfxExposedNames.OutputMesh));
        Assert.True(vfx.OutputMesh);
        cube.Dispose();
    }

    [Fact]
    public void VisualEffect_SkipsGpuWhenComputeUnsupported()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Vfx");
        scene.Add(go);
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        Assert.Equal(VfxRuntime.DeviceSupportsCompute, vfx.UsesGpuParticles);

        var renderables = new List<IRenderable>();
        var lights = new List<IRenderableLight>();
        vfx.OnRenderCollect(null!, renderables, lights);
        if (!vfx.UsesGpuParticles)
            Assert.Empty(renderables);
    }

    [Fact]
    public void GpuParticleSystem_EncodeSimulate_RecordsInitUpdateWriteArgs()
    {
        var bytecode = new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            ShaderBytecodeFormat.SpirV,
            [],
            [],
            computeBytecode: [0x03, 0x02, 0x23, 0x07]);
        using var init = new ShaderVariant(bytecode);
        using var update = new ShaderVariant(bytecode);
        using var writeArgs = new ShaderVariant(bytecode);
        using var system = new GpuParticleSystem(64);
        using CommandBuffer cmd = Graphics.GetCommandBuffer();

        var sim = new VfxSimParams
        {
            StartSpeed = 1f,
            Lifetime = 1f,
            StartColor = Color.White,
            StartSize = 0.1f,
        };
        system.EncodeSimulate(cmd, init, update, writeArgs, 0.016f, 8, in sim);

        var decoded = new List<CapturedOp>();
        var owned = new List<PropertyState>();
        CommandBufferDecoder.Decode(cmd, "vfx-simulate", null, decoded, owned);

        int dispatches = 0;
        bool sawIndirectBarrier = false;
        for (int i = 0; i < decoded.Count; i++)
        {
            if (decoded[i].VisibleKind == FrameEventKind.Dispatch)
                dispatches++;
            if (decoded[i].Label == "ResourceBarrier ComputeToIndirect")
                sawIndirectBarrier = true;
        }

        Assert.Equal(3, dispatches);
        Assert.True(sawIndirectBarrier);
        Assert.NotNull(system.DrawArgs);
        Assert.Same(system.DrawArgs, ((IRenderable)new ProbeRenderable(system)).GetIndirectDrawArgs());
    }

    [Fact]
    public void GpuParticleSystem_EncodeSimulate_Sort_AddsBitonicDispatches()
    {
        var bytecode = new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            ShaderBytecodeFormat.SpirV,
            [],
            [],
            computeBytecode: [0x03, 0x02, 0x23, 0x07]);
        using var init = new ShaderVariant(bytecode);
        using var update = new ShaderVariant(bytecode);
        using var writeArgs = new ShaderVariant(bytecode);
        using var sort = new ShaderVariant(bytecode);
        using var system = new GpuParticleSystem(8);
        using CommandBuffer cmd = Graphics.GetCommandBuffer();

        var sim = new VfxSimParams
        {
            IndexCount = VfxSort.PackIndexCount(6, 0, 1f),
            StartSpeed = 1f,
            Lifetime = 1f,
            StartColor = Color.White,
            StartSize = 0.1f,
        };
        system.EncodeSimulate(cmd, init, update, writeArgs, 0.016f, 4, in sim, sort: sort, cameraZ: 2f);

        var decoded = new List<CapturedOp>();
        var owned = new List<PropertyState>();
        CommandBufferDecoder.Decode(cmd, "vfx-sort", null, decoded, owned);
        int dispatches = 0;
        for (int i = 0; i < decoded.Count; i++)
        {
            if (decoded[i].VisibleKind == FrameEventKind.Dispatch)
                dispatches++;
        }

        Assert.Equal(3 + VfxSort.BitonicPassCount(8), dispatches);
    }

    private sealed class ProbeRenderable : IRenderable
    {
        private readonly GpuParticleSystem _system;
        public ProbeRenderable(GpuParticleSystem system) => _system = system;
        public Material GetMaterial() => null!;
        public int GetLayer() => 0;
        public Float3 GetPosition() => Float3.Zero;
        public GraphicsBuffer? GetIndirectDrawArgs() => _system.DrawArgs;
        public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out InstanceData[]? instanceData)
        {
            properties = null!;
            mesh = null!;
            model = default;
            instanceData = null;
        }
        public void GetCullingData(out bool isRenderable, out AABB bounds)
        {
            isRenderable = false;
            bounds = default;
        }
    }
}
