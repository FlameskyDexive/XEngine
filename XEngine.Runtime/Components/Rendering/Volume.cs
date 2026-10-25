// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Global or local (AABB) volume that contributes a <see cref="VolumeProfile"/> to
/// <see cref="VolumeManager"/> blending. Local extents are <see cref="Transform.LossyScale"/>
/// treated as AABB half-extents, matching <see cref="FogVolumeShape.Box"/>.
/// </summary>
[AddComponentMenu("Rendering/Volume")]
[ComponentIcon("\uf1b2")] // Cube
public sealed class Volume : MonoBehaviour
{
    /// <summary>When true, influence is <see cref="Weight"/> everywhere (no bounds test).</summary>
    [SerializeField] public bool IsGlobal = true;

    /// <summary>0–1 contribution once inside (or for globals).</summary>
    [SerializeField] public float Weight = 1f;

    /// <summary>World-space distance past the AABB surface over which local influence falls to 0.</summary>
    [SerializeField] public float BlendDistance;

    /// <summary>Lower priority applies first; higher overwrites (stable for equal values).</summary>
    [SerializeField] public int Priority;

    /// <summary>Shared profile asset (<c>.volumeprofile</c>). Runtime-only profiles use an inline AssetRef instance.</summary>
    [SerializeField] public AssetRef<VolumeProfile> Profile;

    public VolumeProfile? ResolvedProfile => Profile.Res;

    public override void OnEnable() => VolumeManager.Instance.Register(this);

    public override void OnDisable() => VolumeManager.Instance.Unregister(this);

    public override void OnValidate()
    {
        if (!float.IsFinite(Weight))
            Weight = 1f;
        Weight = Maths.Clamp(Weight, 0f, 1f);
        if (!float.IsFinite(BlendDistance) || BlendDistance < 0f)
            BlendDistance = 0f;
    }

    /// <summary>0–1 blend weight at <paramref name="cameraPosition"/>, or 0 when disabled.</summary>
    public float EvaluateInfluence(Float3 cameraPosition)
    {
        if (!Enabled || GameObject == null || !GameObject.EnabledInHierarchy)
            return 0f;

        OnValidate();
        if (Weight <= 0f)
            return 0f;

        if (IsGlobal)
            return Weight;

        float distance = DistanceToAabb(cameraPosition);
        if (distance <= 0f)
            return Weight;
        if (BlendDistance <= 0f)
            return 0f;

        float falloff = 1f - Maths.Clamp(distance / BlendDistance, 0f, 1f);
        return Weight * falloff;
    }

    public override void DrawGizmos()
    {
        if (IsGlobal)
        {
            Debug.DrawWireCube((Float3)Transform.Position, new Float3(0.5f, 0.5f, 0.5f), new Color(0.6f, 0.4f, 1f, 1f));
            return;
        }

        Debug.PushMatrix(Transform.LocalToWorldMatrix);
        Debug.DrawWireCube(Float3.Zero, new Float3(2f, 2f, 2f), new Color(0.6f, 0.4f, 1f, 1f));
        Debug.PopMatrix();
    }

    private float DistanceToAabb(Float3 cameraPosition)
    {
        Float3 center = Transform.Position;
        Float3 scale = Transform.LossyScale;
        float hx = MathF.Abs(scale.X);
        float hy = MathF.Abs(scale.Y);
        float hz = MathF.Abs(scale.Z);
        float dx = MathF.Max(MathF.Abs(cameraPosition.X - center.X) - hx, 0f);
        float dy = MathF.Max(MathF.Abs(cameraPosition.Y - center.Y) - hy, 0f);
        float dz = MathF.Max(MathF.Abs(cameraPosition.Z - center.Z) - hz, 0f);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
