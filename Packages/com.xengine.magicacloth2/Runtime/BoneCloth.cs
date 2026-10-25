// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.MagicaCloth2;

/// <summary>One verlet particle per bone (MagicaCloth2's bone-cloth mapping).</summary>
public sealed class ClothParticle
{
    public Transform Bone = null!;
    public ClothParticle? Parent;
    public int Depth;
    public bool Fixed;

    public Float3 WorldPosition;
    public Float3 PreviousPosition;
    public Float3 RestLocalPosition;
    public Quaternion RestWorldRotation = Quaternion.Identity;
    public Quaternion RestLocalRotation = Quaternion.Identity;
    public float RestLength;
}

/// <summary>Wind configuration (MagicaCloth2 WindSettings subset).</summary>
[Serializable]
public sealed class ClothWindSettings
{
    public Float3 Direction = new(1f, 0f, 0f);
    public float Strength = 0.6f;
    /// <summary>0 = pure sin oscillation, 1 = pure per-particle noise (blend 0.7 default).</summary>
    public float Turbulence = 0.7f;
    public float Frequency = 1.2f;
    /// <summary>Scales wind by chain depth (1 = tips blow more, like depthWeight).</summary>
    public float DepthWeight = 0.6f;

    internal float Evaluate(ClothParticle particle, float time)
    {
        float sin = MathF.Sin(time * Frequency + particle.Depth * 0.9f) * 0.5f + 0.5f;
        float noise = ((particle.WorldPosition.X * 12.9898f + particle.WorldPosition.Y * 78.233f
                        + particle.WorldPosition.Z * 37.719f) * 43758.5453f) % 1f;
        noise = noise - MathF.Floor(noise);
        float oscillation = sin * (1f - Turbulence) + noise * Turbulence;
        float depthScale = 1f + DepthWeight * particle.Depth;
        return oscillation * depthScale;
    }
}

/// <summary>A sphere collider pushed against the cloth (MagicaSphereCollider subset).</summary>
[Serializable]
public sealed class ClothSphereCollider
{
    /// <summary>Transform tracked each frame (e.g. a thigh bone); null uses Center directly.</summary>
    public Transform? Bone;
    public Float3 Center;
    public float Radius = 0.12f;

    public Float3 WorldCenter => Bone != null && Bone.GameObject != null
        ? Bone.Position + Bone.Rotation * Center
        : Center;
}

/// <summary>
/// P0 port of MagicaCloth2's bone cloth (zonezero M11): each root bone's descendant chain
/// becomes verlet particles (root tethered), integrated with gravity + wind, constrained by
/// distance to the parent particle, pushed out of sphere colliders, and written back as bone
/// world rotations in LateUpdate AFTER the Animator (engine renders skin from bone transforms
/// lazily, so the same-frame correction is picked up). Attach once per character; assign the
/// skirt chain roots.
/// </summary>
[AddComponentMenu("MagicaCloth2/Bone Cloth")]
[ExecutionOrder(2000)]
public sealed class BoneCloth : MonoBehaviour
{
    /// <summary>Chain roots (e.g. Skirt_01..Skirt_08). Every descendant becomes a particle.</summary>
    [SerializeField] private List<Transform> _rootBones = new();

    public ClothWindSettings Wind = new();
    [SerializeField] private List<ClothSphereCollider> _colliders = new();

    public float Gravity = -9.8f;
    /// <summary>Velocity damping per second (0 = none, 1 = full).</summary>
    public float Damping = 2.5f;
    /// <summary>Constraint iterations per frame (MagicaCloth2 runs several).</summary>
    public int ConstraintIterations = 3;
    /// <summary>Stiffness of the rest-pose angular spring (0 = free swing, 1 = rigid).</summary>
    public float Stiffness = 0.35f;

    private readonly List<ClothParticle> _particles = new();
    private bool _initialized;

    public IReadOnlyList<ClothParticle> Particles => _particles;
    public IReadOnlyList<ClothSphereCollider> Colliders => _colliders;

    public void AddRootBone(Transform root)
    {
        _rootBones.Add(root);
        _initialized = false;
    }

    public void AddCollider(ClothSphereCollider collider) => _colliders.Add(collider);

    public override void OnEnable() => _initialized = false;

    /// <summary>(Re)builds the particle set from the current bone hierarchy — snapshots every
    /// rest pose BEFORE the character has moved, exactly like MagicaCloth2's
    /// ReadTransformInformation pass.</summary>
    public void Initialize()
    {
        _particles.Clear();
        foreach (Transform root in _rootBones)
        {
            if (root == null || root.GameObject == null) continue;
            BuildChain(root, parent: null, depth: 0, fixedRoot: true);
        }
        _initialized = true;
    }

    private void BuildChain(Transform bone, ClothParticle? parent, int depth, bool fixedRoot)
    {
        var particle = new ClothParticle
        {
            Bone = bone,
            Parent = parent,
            Depth = depth,
            Fixed = fixedRoot,
            WorldPosition = bone.Position,
            PreviousPosition = bone.Position,
            RestLocalPosition = bone.LocalPosition,
            RestWorldRotation = bone.Rotation,
            RestLocalRotation = bone.LocalRotation,
            RestLength = parent != null ? Float3.Length(bone.Position - parent.WorldPosition) : 0f,
        };
        _particles.Add(particle);

        for (int i = 0; i < bone.ChildCount; i++)
            BuildChain(bone.GetChild(i), particle, depth + 1, fixedRoot: false);
    }

    public override void LateUpdate()
    {
        if (!_initialized) Initialize();
        if (_particles.Count == 0) return;

        float dt = Time.DeltaTime;
        if (dt <= 0f) return;
        float windTime = Time.TimeSinceStartup;

        // --- Integrate: fixed roots follow their bone (already Animator-driven) ----------
        foreach (ClothParticle particle in _particles)
        {
            if (particle.Fixed)
            {
                particle.WorldPosition = particle.Bone.Position;
                particle.PreviousPosition = particle.WorldPosition;
                continue;
            }

            Float3 velocity = (particle.WorldPosition - particle.PreviousPosition) *
                (1f - MathF.Min(Damping * dt, 0.9f));
            particle.PreviousPosition = particle.WorldPosition;

            Float3 acceleration = new Float3(0f, Gravity, 0f);
            Float3 windDirection = Float3.Normalize(Wind.Direction);
            acceleration += windDirection * (Wind.Strength * Wind.Evaluate(particle, windTime));

            // Rest-pose spring toward the animated bone position (Stiffness blend).
            Float3 animated = particle.Bone.Position;
            acceleration += (animated - particle.WorldPosition) * (Stiffness * 60f);

            particle.WorldPosition += velocity + acceleration * (dt * dt);
        }

        // --- Constraints: parent distance + sphere colliders -----------------------------
        for (int iteration = 0; iteration < ConstraintIterations; iteration++)
        {
            foreach (ClothParticle particle in _particles)
            {
                if (particle.Fixed || particle.Parent == null) continue;

                // Distance constraint: pull toward the parent at the rest length.
                Float3 toParent = particle.Parent.WorldPosition - particle.WorldPosition;
                float distance = Float3.Length(toParent);
                if (distance > 1e-5f)
                {
                    float error = (distance - particle.RestLength) / distance * 0.5f;
                    if (!particle.Parent.Fixed)
                        particle.Parent.WorldPosition -= toParent * error;
                    particle.WorldPosition += toParent * error;
                }

                // Sphere colliders: push out along the contact normal.
                foreach (ClothSphereCollider collider in _colliders)
                {
                    Float3 center = collider.WorldCenter;
                    Float3 toParticle = particle.WorldPosition - center;
                    float distanceSquared = Float3.LengthSquared(toParticle);
                    float radius = collider.Radius;
                    if (distanceSquared < radius * radius && distanceSquared > 1e-8f)
                    {
                        float d = MathF.Sqrt(distanceSquared);
                        particle.WorldPosition = center + toParticle * (radius / d);
                    }
                }
            }
        }

        // --- Write back: bone world rotations (MagicaCloth2's Flag_WorldRotWrite) --------
        foreach (ClothParticle particle in _particles)
        {
            if (particle.Fixed) continue;
            Transform bone = particle.Bone;
            Transform? parentBone = bone.Parent;
            if (parentBone == null) continue;

            // Aim the bone at the simulated particle with its rest-pose twist preserved:
            // position the bone so its origin tracks the particle chain, rotating the parent
            // frame toward the particle direction (rotationalInterpolation ≈ 0.5 → aim at the
            // parent-relative direction of the particle).
            Float3 currentParentPosition = particle.Parent!.Bone.Position;
            Float3 simulatedDirection = particle.WorldPosition - currentParentPosition;
            if (Float3.LengthSquared(simulatedDirection) < 1e-8f) continue;
            simulatedDirection = Float3.Normalize(simulatedDirection);

            // Rest direction in parent space → how the chain hangs at bind pose.
            Float3 restDirection = particle.RestLocalPosition;
            if (Float3.LengthSquared(restDirection) < 1e-8f) continue;
            restDirection = Float3.Normalize(parentBone.Rotation * restDirection);

            // Rotate the parent frame so restDirection maps onto the simulated direction.
            Quaternion correction = FromToRotation(restDirection, simulatedDirection);
            Quaternion targetRotation = correction * parentBone.Rotation;
            // Re-apply the bone's rest local twist in the corrected frame.
            bone.Rotation = targetRotation * particle.RestLocalRotation;
            // Keep the bone origin pinned to the simulated particle (positions follow the
            // rotation chain through the hierarchy; world-write keeps drift out).
            bone.Position = particle.WorldPosition;
        }
    }

    /// <summary>Minimal-quaternion rotation taking one direction onto another (Unity's
    /// Quaternion.FromToRotation); picks an orthogonal axis for near-opposite inputs.</summary>
    private static Quaternion FromToRotation(Float3 from, Float3 to)
    {
        float dot = Float3.Dot(from, to);
        if (dot > 0.99999f)
            return Quaternion.Identity;
        if (dot < -0.99999f)
        {
            // Opposite: rotate 180° around any axis orthogonal to `from`.
            Float3 axis = MathF.Abs(from.Y) < 0.9f ? Float3.Normalize(Float3.Cross(from, Float3.UnitY)) : Float3.UnitX;
            float half = MathF.PI;
            return new Float4(axis * MathF.Sin(half * 0.5f), MathF.Cos(half * 0.5f));
        }
        Float3 cross = Float3.Cross(from, to);
        float w = MathF.Sqrt((1f + dot) * 0.5f);
        float s = MathF.Sqrt(MathF.Max(1f - w * w, 0f)) / MathF.Max(Float3.Length(cross), 1e-6f);
        return new Float4(cross * s, w);
    }
}
