// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.PostProcessing;

/// <summary>
/// Global or local (AABB) volume that contributes a <see cref="PostProcessProfile"/> to
/// <see cref="PostProcessManager"/> blending. Local extents are <see cref="Transform.LossyScale"/>
/// treated as AABB half-extents, matching the built-in <c>Volume</c> component.
/// </summary>
[AddComponentMenu("Rendering/Post-process Volume")]
[ExecuteAlways]
[Preserve]
[ComponentIcon("\uf1b2")]
public sealed class PostProcessVolume : MonoBehaviour
{
    [SerializeField] public AssetRef<PostProcessProfile> sharedProfile;

    [Tooltip("Apply this volume to the whole scene.")]
    [SerializeField] public bool isGlobal = true;

    [Tooltip("World-space distance past the AABB surface over which local influence falls to 0.")]
    [SerializeField] public float blendDistance;

    [Tooltip("0–1 contribution once inside (or for globals).")]
    [SerializeField] public float weight = 1f;

    [Tooltip("Higher priority overwrites. Negative values are allowed.")]
    [SerializeField] public float priority;

    [NonSerialized] private PostProcessProfile? _internalProfile;

    public PostProcessProfile? profile
    {
        get
        {
            if (_internalProfile == null)
            {
                _internalProfile = new PostProcessProfile();
                PostProcessProfile? shared = sharedProfile.Res;
                if (shared?.settings != null)
                {
                    for (int i = 0; i < shared.settings.Count; i++)
                    {
                        PostProcessEffectSettings? item = shared.settings[i];
                        if (item == null)
                            continue;
                        if (Activator.CreateInstance(item.GetType()) is PostProcessEffectSettings copy)
                        {
                            copy.Interp(item, 1f);
                            copy.enabled.Override(item.enabled.value);
                            _internalProfile.settings.Add(copy);
                        }
                    }
                }
            }

            return _internalProfile;
        }
        set => _internalProfile = value;
    }

    public PostProcessProfile? profileRef => _internalProfile ?? sharedProfile.Res;

    public bool HasInstantiatedProfile() => _internalProfile != null;

    public override void OnEnable() => PostProcessManager.instance.Register(this);

    public override void OnDisable() => PostProcessManager.instance.Unregister(this);

    public override void OnValidate()
    {
        if (!float.IsFinite(weight))
            weight = 1f;
        weight = Maths.Clamp(weight, 0f, 1f);
        if (!float.IsFinite(blendDistance) || blendDistance < 0f)
            blendDistance = 0f;
    }

    public float EvaluateInfluence(Float3 cameraPosition)
    {
        if (!Enabled || GameObject == null || !GameObject.EnabledInHierarchy)
            return 0f;

        OnValidate();
        if (weight <= 0f)
            return 0f;
        if (isGlobal)
            return weight;

        float distance = DistanceToAabb(cameraPosition);
        if (distance <= 0f)
            return weight;
        if (blendDistance <= 0f)
            return 0f;

        float falloff = 1f - Maths.Clamp(distance / blendDistance, 0f, 1f);
        return weight * falloff;
    }

    public override void DrawGizmos()
    {
        if (isGlobal)
        {
            Debug.DrawWireCube((Float3)Transform.Position, new Float3(0.5f, 0.5f, 0.5f), new Color(0.2f, 0.7f, 1f, 1f));
            return;
        }

        Debug.PushMatrix(Transform.LocalToWorldMatrix);
        Debug.DrawWireCube(Float3.Zero, new Float3(2f, 2f, 2f), new Color(0.2f, 0.7f, 1f, 1f));
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
