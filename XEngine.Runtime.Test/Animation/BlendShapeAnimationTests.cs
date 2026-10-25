// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Xunit;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Playables;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies the runtime blend-shape surface: <see cref="SkinnedMeshRenderer"/> weight API, the
/// legacy <see cref="AnimationComponent"/> blend-shape tracks, and the Playables path
/// (SkeletonBinding → AnimationClipPlayable → Pose → AnimationPlayableOutput).
/// </summary>
public class BlendShapeAnimationTests : RuntimeTestBase
{
    private static Mesh MakeFaceMesh()
    {
        var mesh = new Mesh
        {
            Vertices = new[] { new Float3(0, 0, 0), new Float3(1, 0, 0), new Float3(0, 1, 0) },
            BlendShapes = new[]
            {
                new BlendShape
                {
                    Name = "Smile",
                    Frames = new[] { new BlendShapeFrame { Weight = 100f, DeltaVertices = new[] { new Float3(0, 0.5f, 0), new Float3(0, 0, 0), new Float3(0, 0, 0) } } },
                },
                new BlendShape
                {
                    Name = "Frown",
                    Frames = new[] { new BlendShapeFrame { Weight = 50f, DeltaVertices = new[] { new Float3(0, -0.5f, 0), new Float3(0, 0, 0), new Float3(0, 0, 0) } } },
                },
            },
        };
        return mesh;
    }

    private (Scene scene, GameObject root, GameObject face, SkinnedMeshRenderer smr) BuildScene()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Model");
        var face = CreateGameObject("Face");
        face.SetParent(root);
        scene.Add(root);

        var smr = face.AddComponent<SkinnedMeshRenderer>();
        smr.SharedMesh = MakeFaceMesh();
        return (scene, root, face, smr);
    }

    // ─────────────────────────── SMR weight API ───────────────────────────

    [Fact]
    public void SetBlendShapeWeight_ByIndexAndName()
    {
        var smr = BuildScene().smr;

        smr.SetBlendShapeWeight(0, 40f);
        Assert.Equal(40f, smr.GetBlendShapeWeight(0));

        smr.SetBlendShapeWeight("Frown", 25f);
        Assert.Equal(25f, smr.GetBlendShapeWeight(1));

        // Setting the same shape by name overwrites the index write.
        smr.SetBlendShapeWeight("Smile", 80f);
        Assert.Equal(80f, smr.GetBlendShapeWeight(0));
    }

    [Fact]
    public void GetBlendShapeWeight_InvalidIndex_ReturnsZero()
    {
        var smr = BuildScene().smr;

        Assert.Equal(0f, smr.GetBlendShapeWeight(-1));
        Assert.Equal(0f, smr.GetBlendShapeWeight(99));
    }

    [Fact]
    public void SetBlendShapeWeight_InvalidIndex_IsIgnored()
    {
        var smr = BuildScene().smr;

        smr.SetBlendShapeWeight(-1, 50f);
        smr.SetBlendShapeWeight(99, 50f);

        Assert.Equal(0f, smr.GetBlendShapeWeight(0));
        Assert.Equal(0f, smr.GetBlendShapeWeight(1));
    }

    [Fact]
    public void GetBlendShapeIndex_UnknownName_ReturnsMinusOne()
    {
        var smr = BuildScene().smr;

        Assert.Equal(0, smr.GetBlendShapeIndex("Smile"));
        Assert.Equal(-1, smr.GetBlendShapeIndex("Blink"));
    }

    [Fact]
    public void WeightsArray_GrowsPreservingExistingValues()
    {
        var (_, _, _, smr) = BuildScene();
        smr.SetBlendShapeWeight(0, 33f);
        smr.SetBlendShapeWeight(1, 66f);

        // Swap in a mesh with more shapes: the weight array must grow without losing prior poses.
        var bigger = MakeFaceMesh();
        var extra = new List<BlendShape>(bigger.BlendShapes)
        {
            new BlendShape { Name = "Blink", Frames = new[] { new BlendShapeFrame { Weight = 100f, DeltaVertices = Array.Empty<Float3>() } } },
        };
        bigger.BlendShapes = extra.ToArray();
        smr.SharedMesh = bigger;

        smr.SetBlendShapeWeight(2, 10f);

        Assert.Equal(33f, smr.GetBlendShapeWeight(0));
        Assert.Equal(66f, smr.GetBlendShapeWeight(1));
        Assert.Equal(10f, smr.GetBlendShapeWeight(2));
    }

    [Fact]
    public void BlendShapeCount_MirrorsSharedMesh()
    {
        var smr = BuildScene().smr;
        Assert.Equal(2, smr.BlendShapeCount);
    }

    // ─────────────────────────── SkeletonBinding ───────────────────────────

    [Fact]
    public void SkeletonBinding_RegistersBlendShapesWithRendererPaths()
    {
        var (_, root, _, smr) = BuildScene();
        var binding = SkeletonBinding.Build(root.Transform);

        Assert.Equal(2, binding.BlendShapeCount);
        Assert.Equal(0, binding.IndexOfBlendShape("Face", "Smile"));
        Assert.Equal(1, binding.IndexOfBlendShape("Face", "Frown"));
        Assert.Equal(-1, binding.IndexOfBlendShape("Face", "Blink"));
        Assert.Equal(-1, binding.IndexOfBlendShape("Body", "Smile"));
        Assert.Same(smr, binding.BlendShapes[0].Renderer);
        Assert.Equal(0, binding.BlendShapes[0].ShapeIndex);
    }

    [Fact]
    public void SkeletonBinding_RendererOnRoot_HasEmptyPath()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Head");
        scene.Add(root);
        var smr = root.AddComponent<SkinnedMeshRenderer>();
        smr.SharedMesh = MakeFaceMesh();

        var binding = SkeletonBinding.Build(root.Transform);

        Assert.Equal(0, binding.IndexOfBlendShape("", "Smile"));
    }

    // ─────────────────────────── Playables path ───────────────────────────

    private static AnimationClip MakeSmileClip()
    {
        var clip = new AnimationClip { Name = "Smile", Duration = 1f, Wrap = AnimationWrapMode.ClampForever };
        clip.AddBlendShape(new AnimationClip.BlendShapeAnim
        {
            Path = "Face",
            ShapeName = "Smile",
            Weight = AnimationTestHelpers.LinearCurve(0f, 100f, 1f),
        });
        return clip;
    }

    [Fact]
    public void PlayableOutput_WritesWeightToRenderer()
    {
        var (_, root, _, smr) = BuildScene();
        var binding = SkeletonBinding.Build(root.Transform);

        var playable = new AnimationClipPlayable(MakeSmileClip());
        playable.Bind(binding);
        playable.Play();
        playable.Weight = 1f;
        playable.SetTime(1.0); // end of the ramp → 100.

        var output = new AnimationPlayableOutput { Source = playable, Binding = binding };
        playable.ProcessFrame(new PlayableContext(0f, 0d, false));
        output.Apply(new PlayableContext(0f, 0d, false));

        Assert.Equal(100f, smr.GetBlendShapeWeight(0), 2);
    }

    [Fact]
    public void PlayableOutput_UnboundShapeName_IsNotWritten()
    {
        var (_, root, _, smr) = BuildScene();
        var binding = SkeletonBinding.Build(root.Transform);

        // The clip references a shape the mesh doesn't have: the map entry is unresolved and the
        // output must leave every weight untouched.
        var clip = new AnimationClip { Name = "Bad", Duration = 1f, Wrap = AnimationWrapMode.ClampForever };
        clip.AddBlendShape(new AnimationClip.BlendShapeAnim
        {
            Path = "Face",
            ShapeName = "Blink",
            Weight = AnimationTestHelpers.ConstantCurve(100f),
        });
        var playable = new AnimationClipPlayable(clip);
        playable.Bind(binding);
        playable.Play();
        playable.Weight = 1f;
        playable.SetTime(0.5);

        var output = new AnimationPlayableOutput { Source = playable, Binding = binding };
        playable.ProcessFrame(new PlayableContext(0f, 0d, false));
        output.Apply(new PlayableContext(0f, 0d, false));

        Assert.Equal(0f, smr.GetBlendShapeWeight(0));
        Assert.Equal(0f, smr.GetBlendShapeWeight(1));
    }

    // ─────────────────────────── Legacy AnimationComponent ───────────────────────────

    [Fact]
    public void AnimationComponent_AppliesBlendShapeTrackOverTime()
    {
        var (scene, root, _, smr) = BuildScene();

        var anim = root.AddComponent<AnimationComponent>();
        anim.PlayAutomatically = false;
        anim.Play(MakeSmileClip());

        Update(scene, frames: 30); // 0.5s of the 1s ramp → 50.

        Assert.True(anim.IsPlaying);
        Assert.Equal(50f, smr.GetBlendShapeWeight(0), 1);

        Update(scene, frames: 30); // reaches the end → 100.
        Assert.Equal(100f, smr.GetBlendShapeWeight(0), 1);
    }

    [Fact]
    public void AnimationComponent_BlendShapeOnlyClip_StillAppliesWithoutBones()
    {
        // A clip with no AnimBone tracks must still drive blend shapes: the bone-cache early-out
        // keys on cached renderers, not on bones.
        var (scene, root, _, smr) = BuildScene();

        var anim = root.AddComponent<AnimationComponent>();
        anim.PlayAutomatically = false;

        var clip = new AnimationClip { Name = "OnlyBS", Duration = 1f, Wrap = AnimationWrapMode.ClampForever };
        clip.AddBlendShape(new AnimationClip.BlendShapeAnim
        {
            Path = "Face",
            ShapeName = "Frown",
            Weight = AnimationTestHelpers.ConstantCurve(25f),
        });
        anim.Play(clip);
        Update(scene, frames: 1);

        Assert.Equal(25f, smr.GetBlendShapeWeight(1), 2);
    }

    // ─────────────────────────── Clip serialization ───────────────────────────

    [Fact]
    public void ClipSerialization_RoundTripsBlendShapeTrack()
    {
        var clip = MakeSmileClip();

        EchoObject echo = Serializer.Serialize(clip, TypeMode.Aggressive);
        string text = echo.WriteToString();
        Assert.Contains("BlendShapes", text);
        Assert.Contains("Smile", text);

        var loaded = Serializer.Deserialize<AnimationClip>(EchoObject.ReadFromString(text));
        var track = Assert.Single(loaded!.BlendShapes);
        Assert.Equal("Face", track.Path);
        Assert.Equal("Smile", track.ShapeName);
        Assert.NotNull(track.Weight);
        Assert.Equal(100f, track.EvaluateAt(1f), 2);
        Assert.Equal(0f, track.EvaluateAt(0f), 2);
    }
}
