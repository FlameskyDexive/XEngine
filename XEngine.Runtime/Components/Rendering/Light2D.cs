// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>Shape of a 2D light (M22). Independent of 3D <see cref="Light"/> / LightBVH.</summary>
public enum Light2DType : byte
{
    Global = 0,
    Point = 1,
    Sprite = 2,
}

/// <summary>
/// XY light for <see cref="RendererKind.Renderer2D"/>. Global fills the light texture; Point has
/// radial falloff; Sprite multiplies a cookie. Does not participate in LightBVH.
/// </summary>
[AddComponentMenu("Rendering/Light 2D")]
[ComponentIcon("\uf0eb")]
public sealed class Light2D : MonoBehaviour
{
    [SerializeField] public Light2DType LightType = Light2DType.Point;
    [SerializeField] public Color Color = Color.White;
    [SerializeField] public float Intensity = 1f;
    [SerializeField] public float InnerRadius = 0.25f;
    [SerializeField] public float OuterRadius = 3f;
    [SerializeField] public float Falloff = 0.5f;
    [SerializeField] public bool CastShadows;
    [SerializeField] public float ShadowIntensity = 0.75f;
    [SerializeField] public AssetRef<Sprite> Cookie;
    [SerializeField] public int LightOrder;
    [SerializeField] public RenderingLayerMask RenderingLayers = RenderingLayerMask.Everything;

    public override void OnEnable() => Light2DSystem.Instance.Register(this);

    public override void OnDisable() => Light2DSystem.Instance.Unregister(this);

    public override void OnValidate()
    {
        int type = (int)LightType;
        if (type < 0 || type > 2)
            LightType = Light2DType.Point;
        if (!float.IsFinite(Intensity))
            Intensity = 1f;
        Intensity = MathF.Max(0f, Intensity);
        if (!float.IsFinite(InnerRadius))
            InnerRadius = 0.25f;
        InnerRadius = MathF.Max(0f, InnerRadius);
        if (!float.IsFinite(OuterRadius))
            OuterRadius = 3f;
        OuterRadius = MathF.Max(InnerRadius, OuterRadius);
        if (!float.IsFinite(Falloff))
            Falloff = 0.5f;
        Falloff = Maths.Clamp(Falloff, 0f, 1f);
        if (!float.IsFinite(ShadowIntensity))
            ShadowIntensity = 0.75f;
        ShadowIntensity = Maths.Clamp(ShadowIntensity, 0f, 1f);
    }

    /// <summary>
    /// CPU lighting used by tests and tools. Global ignores distance. Point/Sprite use radial
    /// falloff; Sprite cookies are 1 on CPU (GPU samples the sprite). Shadows use AABB casters.
    /// </summary>
    public Color EvaluateAt(Float3 worldPosition)
    {
        OnValidate();
        Color lit = new(Color.R * Intensity, Color.G * Intensity, Color.B * Intensity, 1f);
        if (LightType == Light2DType.Global)
            return lit;

        Float2 origin = new(Transform.Position.X, Transform.Position.Y);
        Float2 sample = new(worldPosition.X, worldPosition.Y);
        float dist = Float2.Distance(origin, sample);
        float inner = InnerRadius;
        float outer = OuterRadius;
        float t;
        if (dist <= inner)
            t = 1f;
        else if (dist >= outer)
            t = 0f;
        else
        {
            float u = (dist - inner) / MathF.Max(outer - inner, 1e-4f);
            t = 1f - u;
            t = t * t * (3f - 2f * t);
            if (Falloff > 0f)
                t = MathF.Pow(t, 1f + Falloff * 3f);
        }

        if (t <= 0f)
            return new Color(0f, 0f, 0f, 1f);

        float shadow = 1f;
        if (CastShadows)
        {
            ShadowCaster2DSystem casters = ShadowCaster2DSystem.Instance;
            for (int i = 0; i < casters.Count; i++)
            {
                ShadowCaster2D caster = casters[i];
                if (caster == null || caster.IsDisposed || !caster.Enabled)
                    continue;
                if (caster.GameObject == null || !caster.GameObject.EnabledInHierarchy)
                    continue;
                if (caster.Occludes(origin, sample))
                {
                    shadow = 1f - ShadowIntensity;
                    break;
                }
            }
        }

        return new Color(lit.R * t * shadow, lit.G * t * shadow, lit.B * t * shadow, 1f);
    }
}
