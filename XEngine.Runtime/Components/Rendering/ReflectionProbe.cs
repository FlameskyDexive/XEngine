// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

public enum ReflectionProbeMode
{
    Baked = 0,
    Realtime = 1,
}

/// <summary>
/// Box-bounded environment probe. Standard IBL samples up to two probes (plus the existing
/// ambient specular fallback when none apply). Influence AABB is axis-aligned: center is
/// <see cref="Transform.Position"/>, full size is <see cref="Size"/> scaled by lossy scale.
/// </summary>
[AddComponentMenu("Rendering/Reflection Probe")]
[ComponentIcon("\uf1fc")]
public sealed class ReflectionProbe : MonoBehaviour
{
    /// <summary>Full local-space extents of the influence box (meters). Transform scale multiplies this.</summary>
    [SerializeField] public Float3 Size = new(10f, 10f, 10f);

    [SerializeField] public float Intensity = 1f;

    /// <summary>World-space distance past the AABB surface over which weight falls to 0.</summary>
    [SerializeField] public float BlendDistance = 1f;

    [SerializeField] public int Importance = 1;

    [SerializeField] public bool BoxProjection = true;

    [SerializeField] public ReflectionProbeMode Mode = ReflectionProbeMode.Baked;

    /// <summary>Face edge length for realtime capture / allocated cubemap.</summary>
    [SerializeField] public int Resolution = 128;

    /// <summary>Baked or captured environment cubemap. Realtime <see cref="RenderProbe"/> fills this.</summary>
    [SerializeField] public AssetRef<Cubemap> Texture;

    public Cubemap? ResolvedTexture => Texture.Res;

    public override void OnEnable() => ReflectionProbeManager.Instance.Register(this);

    public override void OnDisable() => ReflectionProbeManager.Instance.Unregister(this);

    public override void OnValidate()
    {
        if (!float.IsFinite(Intensity) || Intensity < 0f)
            Intensity = 1f;
        if (!float.IsFinite(BlendDistance) || BlendDistance < 0f)
            BlendDistance = 0f;
        Size = new Float3(
            MathF.Max(0.01f, float.IsFinite(Size.X) ? Size.X : 10f),
            MathF.Max(0.01f, float.IsFinite(Size.Y) ? Size.Y : 10f),
            MathF.Max(0.01f, float.IsFinite(Size.Z) ? Size.Z : 10f));
        Resolution = Maths.Clamp(Resolution, 16, 512);
    }

    public Float3 WorldHalfExtents
    {
        get
        {
            Float3 scale = Transform.LossyScale;
            return new Float3(
                MathF.Abs(scale.X) * Size.X * 0.5f,
                MathF.Abs(scale.Y) * Size.Y * 0.5f,
                MathF.Abs(scale.Z) * Size.Z * 0.5f);
        }
    }

    public void GetWorldBounds(out Float3 boxMin, out Float3 boxMax)
    {
        Float3 center = Transform.Position;
        Float3 half = WorldHalfExtents;
        boxMin = center - half;
        boxMax = center + half;
    }

    /// <summary>0–1 weight at <paramref name="worldPosition"/>, or 0 when disabled / no cubemap.</summary>
    public float EvaluateInfluence(Float3 worldPosition)
    {
        if (!Enabled || GameObject == null || !GameObject.EnabledInHierarchy)
            return 0f;
        OnValidate();
        if (Intensity <= 0f || ResolvedTexture == null)
            return 0f;

        GetWorldBounds(out Float3 boxMin, out Float3 boxMax);
        float distance = DistanceToAabb(worldPosition, boxMin, boxMax);
        if (distance <= 0f)
            return 1f;
        if (BlendDistance <= 0f)
            return 0f;
        return 1f - Maths.Clamp(distance / BlendDistance, 0f, 1f);
    }

    /// <summary>Allocates a mip cubemap when missing. GPU face capture is skipped while headless.</summary>
    public bool RenderProbe()
    {
        OnValidate();
        if (Texture.Res != null)
            return true;

        var cube = new Cubemap((uint)Resolution, mipChain: true);
        Texture = new AssetRef<Cubemap>(cube);
        return true;
    }

    public static Float3 FaceForward(int face) => face switch
    {
        0 => Float3.UnitX,
        1 => -Float3.UnitX,
        2 => Float3.UnitY,
        3 => -Float3.UnitY,
        4 => Float3.UnitZ,
        5 => -Float3.UnitZ,
        _ => Float3.UnitZ,
    };

    public static Float3 FaceUp(int face) => face switch
    {
        2 => Float3.UnitZ,
        3 => -Float3.UnitZ,
        _ => -Float3.UnitY,
    };

    public static Float4x4 FaceViewMatrix(Float3 probePosition, int face)
        => Float4x4.CreateLookAt(probePosition, probePosition + FaceForward(face), FaceUp(face));

    public static Float3 BoxProjectedDirection(Float3 dir, Float3 worldPos, Float3 probePos, Float3 boxMin, Float3 boxMax)
    {
        Float3 nrDir = Float3.Normalize(dir);
        Float3 rbmax = new(
            (boxMax.X - worldPos.X) / nrDir.X,
            (boxMax.Y - worldPos.Y) / nrDir.Y,
            (boxMax.Z - worldPos.Z) / nrDir.Z);
        Float3 rbmin = new(
            (boxMin.X - worldPos.X) / nrDir.X,
            (boxMin.Y - worldPos.Y) / nrDir.Y,
            (boxMin.Z - worldPos.Z) / nrDir.Z);
        Float3 rbminmax = new(
            nrDir.X >= 0f ? rbmax.X : rbmin.X,
            nrDir.Y >= 0f ? rbmax.Y : rbmin.Y,
            nrDir.Z >= 0f ? rbmax.Z : rbmin.Z);
        float fa = MathF.Min(rbminmax.X, MathF.Min(rbminmax.Y, rbminmax.Z));
        Float3 posOnBox = worldPos + nrDir * fa;
        return posOnBox - probePos;
    }

    public override void DrawGizmos()
    {
        GetWorldBounds(out Float3 boxMin, out Float3 boxMax);
        Float3 center = (boxMin + boxMax) * 0.5f;
        Float3 size = boxMax - boxMin;
        Debug.DrawWireCube(center, size, new Color(0.4f, 0.85f, 1f, 1f));
    }

    private static float DistanceToAabb(Float3 p, Float3 boxMin, Float3 boxMax)
    {
        float dx = MathF.Max(MathF.Max(boxMin.X - p.X, 0f), p.X - boxMax.X);
        float dy = MathF.Max(MathF.Max(boxMin.Y - p.Y, 0f), p.Y - boxMax.Y);
        float dz = MathF.Max(MathF.Max(boxMin.Z - p.Z, 0f), p.Z - boxMax.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
