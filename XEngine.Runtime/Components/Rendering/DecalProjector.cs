// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Oriented box that projects an albedo texture onto scene depth (M16 screen-space decal).
/// Local Y is the projection axis; UV is local XZ mapped to [0,1]. Full size is
/// <see cref="Size"/> scaled by lossy scale. Independent of <see cref="LayerMask"/>.
/// </summary>
[AddComponentMenu("Rendering/Decal Projector")]
[ComponentIcon("\uf1fc")]
public sealed class DecalProjector : MonoBehaviour
{
    /// <summary>Full local-space extents of the projection box (meters). Transform scale multiplies this.</summary>
    [SerializeField] public Float3 Size = new(2f, 2f, 2f);

    [SerializeField] public Color Color = Color.White;

    [SerializeField] public float Opacity = 1f;

    [SerializeField] public AssetRef<Texture2D> Texture;

    /// <summary>Which object rendering layers this projector affects. Default Everything.</summary>
    [SerializeField] public RenderingLayerMask RenderingLayers = RenderingLayerMask.Everything;

    public override void OnEnable() => DecalSystem.Instance.Register(this);

    public override void OnDisable() => DecalSystem.Instance.Unregister(this);

    public override void OnValidate()
    {
        if (!float.IsFinite(Opacity))
            Opacity = 1f;
        Opacity = Maths.Clamp(Opacity, 0f, 1f);
        Size = new Float3(
            MathF.Max(0.01f, float.IsFinite(Size.X) ? Size.X : 2f),
            MathF.Max(0.01f, float.IsFinite(Size.Y) ? Size.Y : 2f),
            MathF.Max(0.01f, float.IsFinite(Size.Z) ? Size.Z : 2f));
    }

    /// <summary>Model matrix for a unit cube (±0.5) that fills the projector volume.</summary>
    public Float4x4 GetVolumeMatrix()
    {
        Float3 lossy = Transform.LossyScale;
        Float3 scale = new(
            MathF.Abs(lossy.X) * Size.X,
            MathF.Abs(lossy.Y) * Size.Y,
            MathF.Abs(lossy.Z) * Size.Z);
        return Float4x4.CreateTRS(Transform.Position, Transform.Rotation, scale);
    }

    /// <summary>
    /// True when <paramref name="worldPosition"/> is inside the unit cube in projector local space.
    /// UV is local XZ + 0.5 (projection along local Y).
    /// </summary>
    public static bool TryWorldToUv(Float3 worldPosition, in Float4x4 worldToLocal, out Float2 uv)
    {
        Float4 local = worldToLocal * new Float4(worldPosition, 1f);
        if (MathF.Abs(local.X) > 0.5f || MathF.Abs(local.Y) > 0.5f || MathF.Abs(local.Z) > 0.5f)
        {
            uv = default;
            return false;
        }

        uv = new Float2(local.X + 0.5f, local.Z + 0.5f);
        return true;
    }

    public override void DrawGizmos()
    {
        var icon = Resources.Texture2D.LoadDefault(Resources.DefaultTexture.IconDecal);
        if (icon != null) Debug.DrawIcon(icon, Transform.Position, 0.5f, Color.White);

        Float3 lossy = Transform.LossyScale;
        Float3 size = new(
            MathF.Abs(lossy.X) * Size.X,
            MathF.Abs(lossy.Y) * Size.Y,
            MathF.Abs(lossy.Z) * Size.Z);
        Debug.DrawWireCube(Transform.Position, size, new Color(1f, 0.55f, 0.2f, 1f));
    }
}
