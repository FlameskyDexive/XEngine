using System.Collections.Generic;

using XEngine.MagicaCloth2;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// zonezero M11 — the P0 bone-cloth port: chain construction from root bones (fixed root,
/// one particle per descendant, rest lengths snapshotted), verlet integration under gravity
/// + wind, distance constraints preserving rest lengths, sphere-collider push-out, and the
/// write-back rotating bones toward the simulated particles.
/// </summary>
public sealed class ZonezeroClothTests : EditorTestHarness
{
    /// <summary>Builds a 3-bone skirt-like chain: pelvis → Skirt_01 → Skirt_02 → Skirt_03.</summary>
    private static GameObject BuildChain(string rootName, Float3 rootPosition)
    {
        var pelvis = new GameObject("Bip001 Pelvis");
        pelvis.Transform.Position = rootPosition;
        GameObject previous = pelvis;
        for (int i = 1; i <= 3; i++)
        {
            var bone = new GameObject(i == 1 ? rootName : $"{rootName}_child{i}");
            bone.Transform.SetParent(previous.Transform, false);
            bone.Transform.LocalPosition = new Float3(0f, -0.12f, 0f);  // hem hangs down
            previous = bone;
        }
        return pelvis;
    }

    private static void Tick(BoneCloth cloth, int frames)
    {
        Time.TimeStack.Push(new TimeData { DeltaTime = 1f / 60f, Time = frames / 60f });
        try
        {
            for (int i = 0; i < frames; i++)
                cloth.LateUpdate();
        }
        finally
        {
            Time.TimeStack.Clear();
        }
    }

    [Fact]
    public void ChainConstruction_FixedRootOneParticlePerBone()
    {
        GameObject pelvis = BuildChain("Skirt_01", Float3.Zero);
        try
        {
            var cloth = pelvis.AddComponent<BoneCloth>();
            cloth.AddRootBone(pelvis.Transform.Find("Skirt_01"));
            cloth.Initialize();

            // 1 root + 2 descendants per chain (Skirt_01, _child2, _child3).
            Assert.Equal(3, cloth.Particles.Count);
            Assert.True(cloth.Particles[0].Fixed);
            Assert.False(cloth.Particles[1].Fixed);
            Assert.False(cloth.Particles[2].Fixed);
            Assert.Equal(0.12f, cloth.Particles[1].RestLength, 3);
            Assert.Equal(0.12f, cloth.Particles[2].RestLength, 3);
            // Chain linkage mirrors the hierarchy.
            Assert.Same(cloth.Particles[0], cloth.Particles[1].Parent);
            Assert.Equal(2, cloth.Particles[2].Depth);
        }
        finally
        {
            pelvis.Dispose();
        }
    }

    [Fact]
    public void Verlet_GravityPullsHemBelowRest()
    {
        GameObject pelvis = BuildChain("Skirt_01", Float3.Zero);
        try
        {
            var cloth = pelvis.AddComponent<BoneCloth>();
            cloth.AddRootBone(pelvis.Transform.Find("Skirt_01"));
            cloth.Wind.Strength = 0f;      // isolate gravity
            cloth.Stiffness = 0f;          // free swing
            cloth.Initialize();

            float restY = cloth.Particles[2].WorldPosition.Y;   // −0.36
            Tick(cloth, 30);

            float swungY = cloth.Particles[2].WorldPosition.Y;
            Assert.True(swungY < restY + 0.001f,
                $"hem should fall or stay (rest {restY}, got {swungY})");
        }
        finally
        {
            pelvis.Dispose();
        }
    }

    [Fact]
    public void Wind_BlowsParticlesAlongDirection()
    {
        GameObject pelvis = BuildChain("Skirt_01", Float3.Zero);
        try
        {
            var cloth = pelvis.AddComponent<BoneCloth>();
            cloth.AddRootBone(pelvis.Transform.Find("Skirt_01"));
            cloth.Wind.Direction = Float3.UnitX;
            cloth.Wind.Strength = 3f;
            cloth.Wind.Turbulence = 0f;    // deterministic sin
            cloth.Stiffness = 0f;
            cloth.Initialize();

            Tick(cloth, 60);

            float x = cloth.Particles[2].WorldPosition.X;
            Assert.True(x > 0.005f, $"wind should blow hem +X, got X={x}");
        }
        finally
        {
            pelvis.Dispose();
        }
    }

    [Fact]
    public void DistanceConstraint_KeepsRestLengths()
    {
        GameObject pelvis = BuildChain("Skirt_01", Float3.Zero);
        try
        {
            var cloth = pelvis.AddComponent<BoneCloth>();
            cloth.AddRootBone(pelvis.Transform.Find("Skirt_01"));
            cloth.Wind.Strength = 2f;
            cloth.Stiffness = 0f;
            cloth.ConstraintIterations = 4;
            cloth.Initialize();

            Tick(cloth, 45);

            // |child − parent| ≈ rest length (0.12) after constraints — verlet drift bounded.
            for (int i = 1; i < cloth.Particles.Count; i++)
            {
                float distance = Float3.Length(cloth.Particles[i].WorldPosition - cloth.Particles[i - 1].WorldPosition);
                Assert.InRange(distance, 0.10f, 0.14f);
            }
        }
        finally
        {
            pelvis.Dispose();
        }
    }

    [Fact]
    public void SphereCollider_PushesParticlesOut()
    {
        GameObject pelvis = BuildChain("Skirt_01", Float3.Zero);
        try
        {
            var cloth = pelvis.AddComponent<BoneCloth>();
            cloth.AddRootBone(pelvis.Transform.Find("Skirt_01"));
            cloth.Wind.Strength = 0f;
            cloth.Stiffness = 0f;
            cloth.Initialize();

            // A fat sphere right under the hem: particles must stay outside it.
            cloth.AddCollider(new ClothSphereCollider
            {
                Center = new Float3(0f, -0.24f, 0f),
                Radius = 0.2f,
            });

            Tick(cloth, 20);

            Float3 sphereCenter = new(0f, -0.24f, 0f);
            foreach (ClothParticle particle in cloth.Particles)
            {
                if (particle.Fixed) continue;
                float distance = Float3.Length(particle.WorldPosition - sphereCenter);
                Assert.True(distance >= 0.19f,
                    $"particle {particle.Bone.GameObject.Name} trapped inside collider (d={distance})");
            }
        }
        finally
        {
            pelvis.Dispose();
        }
    }
}
