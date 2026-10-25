using System;
using System.Runtime.InteropServices;

using XEngine.Runtime;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VFX;

/// <summary>
/// GPU particle SoA: Spawn (CPU count) → Init compute → Update compute → Sort → WriteArgs → DrawIndexedIndirect.
/// </summary>
public sealed class GpuParticleSystem : IDisposable
{
    private readonly GraphicsBuffer _particles;
    private readonly GraphicsBuffer _counters;
    private readonly GraphicsBuffer _params;
    private readonly GraphicsBuffer _liveList;
    private readonly GraphicsBuffer _drawArgs;
    private readonly uint[] _aliveReset = [0];
    private VfxSimParams _simParams;

    public GpuParticleSystem(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        int particleBytes = Capacity * Marshal.SizeOf<VfxParticle>();
        int liveBytes = Capacity * sizeof(uint);
        int counterBytes = VfxCounterSlot.Count * sizeof(uint);
        int paramBytes = Marshal.SizeOf<VfxSimParams>();
        int argsBytes = 5 * sizeof(uint);

        _particles = new GraphicsBuffer(BufferType.StructuredBuffer, new byte[particleBytes], dynamic: true);
        _counters = new GraphicsBuffer(BufferType.StructuredBuffer, new byte[counterBytes], dynamic: true);
        _params = new GraphicsBuffer(BufferType.StructuredBuffer, new byte[paramBytes], dynamic: true);
        _liveList = new GraphicsBuffer(BufferType.StructuredBuffer, new byte[liveBytes], dynamic: true);
        _drawArgs = new GraphicsBuffer(BufferType.IndirectBuffer, new byte[argsBytes], dynamic: true);
    }

    public int Capacity { get; }
    public GraphicsBuffer Particles => _particles;
    public GraphicsBuffer Counters => _counters;
    public GraphicsBuffer Params => _params;
    public GraphicsBuffer LiveList => _liveList;
    public GraphicsBuffer DrawArgs => _drawArgs;

    public static uint DispatchGroups(uint count, uint threadGroupSize = VfxRuntime.ThreadGroupSize)
        => count == 0 ? 0u : (count + threadGroupSize - 1u) / threadGroupSize;

    /// <summary>
    /// Records Spawn/Init/Update/WriteArgs into <paramref name="cmd"/>. Caller submits.
    /// </summary>
    public void EncodeSimulate(
        CommandBuffer cmd,
        ShaderVariant init,
        ShaderVariant update,
        ShaderVariant writeArgs,
        float deltaTime,
        uint spawnCount,
        in VfxSimParams sim,
        Texture3D? sdfVolume = null,
        Texture2D? cameraDepth = null,
        Texture3D? vectorField = null,
        Texture2D? attributeMap = null,
        ShaderVariant? sort = null,
        float cameraX = 0f,
        float cameraY = 0f,
        float cameraZ = 0f,
        bool inheritVelocity = false,
        float inheritVelX = 0f,
        float inheritVelY = 0f,
        float inheritVelZ = 0f)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(init);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(writeArgs);

        _simParams = sim;
        _simParams.DeltaTime = deltaTime;
        _simParams.Capacity = (uint)Capacity;
        _simParams.SpawnCount = spawnCount;
        Float3 updateGravity = _simParams.Gravity;
        if (inheritVelocity)
            _simParams.Gravity = new Float3(inheritVelX, inheritVelY, inheritVelZ);

        cmd.UpdateBuffer(_params, MemoryMarshal.CreateReadOnlySpan(ref _simParams, 1));
        cmd.UpdateBuffer(_counters, _aliveReset.AsSpan(), VfxCounterSlot.EventCountByteOffset);
        cmd.UpdateBuffer(_counters, _aliveReset.AsSpan(), VfxCounterSlot.AliveCountByteOffset);

        BindCompute(cmd, init, sdfVolume, cameraDepth, vectorField, attributeMap);
        uint initGroups = DispatchGroups(spawnCount);
        if (initGroups > 0)
        {
            cmd.DispatchCompute(initGroups, 1, 1);
            cmd.ResourceBarrier(GpuBarrierKind.ComputeToCompute, _particles);
            cmd.ResourceBarrier(GpuBarrierKind.ComputeToCompute, _counters);
        }

        if (inheritVelocity)
        {
            _simParams.Gravity = updateGravity;
            cmd.UpdateBuffer(_params, MemoryMarshal.CreateReadOnlySpan(ref _simParams, 1));
        }

        BindCompute(cmd, update, sdfVolume, cameraDepth, vectorField, attributeMap);
        cmd.DispatchCompute(DispatchGroups((uint)Capacity), 1, 1);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToCompute, _counters);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToCompute, _liveList);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToCompute, _particles);

        if (sort != null && VfxSort.IsEnabled(_simParams.IndexCount))
        {
            _simParams.EmitterPosition = new Float3(cameraX, cameraY, cameraZ);
            uint size = VfxSort.NextPow2((uint)Capacity);
            for (uint k = 2u; k <= size; k <<= 1)
            {
                for (uint j = k >> 1; j > 0u; j >>= 1)
                {
                    _simParams.StartSpeed = k;
                    _simParams.StartSize = j;
                    cmd.UpdateBuffer(_params, MemoryMarshal.CreateReadOnlySpan(ref _simParams, 1));
                    BindCompute(cmd, sort, sdfVolume, cameraDepth, vectorField, attributeMap);
                    cmd.DispatchCompute(DispatchGroups((uint)Capacity), 1, 1);
                    cmd.ResourceBarrier(GpuBarrierKind.ComputeToCompute, _liveList);
                }
            }
        }

        BindCompute(cmd, writeArgs, sdfVolume, cameraDepth, vectorField, attributeMap);
        cmd.DispatchCompute(1, 1, 1);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToIndirect, _drawArgs);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToVertex, _particles);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToVertex, _liveList);
    }

    public void BindOutput(PropertyState properties)
    {
        properties.SetBuffer("_Particles", _particles);
        properties.SetBuffer("_LiveList", _liveList);
    }

    private void BindCompute(
        CommandBuffer cmd, ShaderVariant variant, Texture3D? sdfVolume, Texture2D? cameraDepth, Texture3D? vectorField,
        Texture2D? attributeMap)
    {
        cmd.SetComputeShader(variant);
        cmd.SetBuffer("_Particles", _particles);
        cmd.SetBuffer("_Counters", _counters);
        cmd.SetBuffer("_Params", _params);
        cmd.SetBuffer("_LiveList", _liveList);
        cmd.SetBuffer("_DrawArgs", _drawArgs);
        cmd.SetTexture(VfxExposedNames.SdfVolumeShader, sdfVolume ?? Texture3D.White);
        cmd.SetTexture(VfxExposedNames.VectorFieldShader, vectorField ?? Texture3D.White);
        Texture2D depth = cameraDepth ?? Texture2D.LoadDefault(DefaultTexture.White);
        cmd.SetTexture(VfxExposedNames.VfxCameraDepthShader, depth);
        cmd.SetTexture(VfxExposedNames.CameraDepthShader, depth);
        cmd.SetTexture(VfxExposedNames.AttributeMapShader, attributeMap ?? Texture2D.LoadDefault(DefaultTexture.White));
    }

    public void Dispose()
    {
        _particles.Dispose();
        _counters.Dispose();
        _params.Dispose();
        _liveList.Dispose();
        _drawArgs.Dispose();
    }
}
