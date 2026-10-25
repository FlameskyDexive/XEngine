using System.Runtime.InteropServices;

using XEngine.Vector;

namespace XEngine.VFX;

/// <summary>GPU SoA particle record. Layout must match <c>VfxParticles.shader</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VfxParticle
{
    public Float3 Position;
    public float Size;
    public Float3 Velocity;
    public float Pad0; // AngularVelocity spin angle; 0 is a no-op.
    public float Age;
    public float Lifetime;
    public uint Alive;
    public uint Pad1;
    public Float4 Color;
}

/// <summary>Per-frame CPU→GPU simulation constants. Layout must match <c>VfxParticles.shader</c> (160 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VfxSimParams
{
    public float DeltaTime;
    public uint Capacity;
    public uint SpawnCount;
    public uint IndexCount;
    public Float3 EmitterPosition;
    public float StartSpeed;
    public Float3 Gravity;
    public float Lifetime;
    public Float4 StartColor;
    public float StartSize;
    public float CollisionMode;
    public float CollisionBounce;
    public float SdfMode;
    public Float4 CollisionPlane;
    public Float4 SdfCenter; // xyz = SDF center; w = DepthCollisionMode
    public Float4 SdfSize;
    public float TurbulenceIntensity;
    public float TurbulenceFrequency;
    public float EventKind;
    public float EventCount;
    public float ConformAttraction;
    public float VectorFieldIntensity;
    public float AttributeMapMode;
    public float AttributeMapScale;
}

/// <summary>Counter buffer slots written by Init/Update compute.</summary>
public static class VfxCounterSlot
{
    public const int EventCount = 0;
    public const int WriteHead = 1;
    public const int AliveCount = 2;
    public const int Count = 4;
    public const int EventCountByteOffset = EventCount * sizeof(uint);
    public const int AliveCountByteOffset = AliveCount * sizeof(uint);
}
