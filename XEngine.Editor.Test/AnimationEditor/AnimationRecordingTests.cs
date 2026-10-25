// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI.Panels;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;
using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>★ M42 — Rec mode snapshots pose, Tick writes keys, RecordKey works without Rec.</summary>
public class AnimationRecordingTests
{
    [Fact]
    public void Tick_WritesPositionKeys_WhenBoneMoved()
    {
        var root = new GameObject("Rig");
        var hips = new GameObject("Hips");
        hips.SetParent(root);
        var clip = AnimationWindowBinding.CreateEmptyClip("Idle");
        var rec = AnimationRecording.Instance;
        try
        {
            Assert.True(rec.Begin(root, clip));
            Assert.True(rec.IsRecording);
            rec.Tick(0.5f);
            Assert.Null(clip.GetBone("Hips"));

            hips.Transform.LocalPosition = new Float3(1f, 2f, 3f);
            rec.Tick(0.5f);

            var bone = clip.GetBone("Hips");
            Assert.NotNull(bone);
            Assert.NotNull(bone!.PosX);
            Assert.Equal(1f, bone.PosX.Evaluate(0.5f), 3);
            Assert.Equal(2f, bone.PosY.Evaluate(0.5f), 3);
            Assert.Equal(3f, bone.PosZ.Evaluate(0.5f), 3);
            Assert.True(rec.KeysWritten >= 3);
            rec.Tick(0.5f);
            Assert.Equal(1, bone.PosX.Keys.Count);
        }
        finally
        {
            rec.End();
        }
    }

    [Fact]
    public void Tick_WritesGenericIntensity_WhenLightChanged()
    {
        var root = new GameObject("Rig");
        var lamp = new GameObject("Lamp");
        lamp.SetParent(root);
        var light = lamp.AddComponent<DirectionalLight>();
        light.Intensity = 1f;
        var clip = AnimationWindowBinding.CreateEmptyClip("Lit");
        clip.GenericCurves.Add(new AnimationClip.GenericCurve
        {
            Path = "Lamp",
            TypeName = nameof(DirectionalLight),
            Property = "Intensity",
            Curve = new AnimationCurve(),
        });
        clip.GenericCurves[0].Curve.Keys.Clear();
        var rec = AnimationRecording.Instance;
        try
        {
            Assert.True(rec.Begin(root, clip));
            light.Intensity = 4.5f;
            rec.Tick(0.25f);
            var g = clip.FindGenericCurve("Lamp", nameof(DirectionalLight), "Intensity");
            Assert.NotNull(g);
            Assert.Equal(4.5f, g!.Curve.Evaluate(0.25f), 3);
        }
        finally
        {
            rec.End();
        }
    }

    [Fact]
    public void WriteKey_WithoutSession_StampsPosX()
    {
        var root = new GameObject("Rig");
        var hips = new GameObject("Hips");
        hips.SetParent(root);
        var clip = AnimationWindowBinding.CreateEmptyClip("Idle");
        Assert.True(AnimationRecording.WriteKey(clip, root, "Hips", "PosX", 0.25f, 7f));
        var bone = clip.GetBone("Hips");
        Assert.NotNull(bone);
        Assert.Equal(7f, bone!.PosX.Evaluate(0.25f), 3);
        AnimationRecording.Instance.End();
    }

    [Fact]
    public void Window_IsRecording_StopsPreviewAndWritesOnTick()
    {
        var root = new GameObject("Rig");
        root.AddComponent<Animator>();
        var hips = new GameObject("Hips");
        hips.SetParent(root);
        var clip = AnimationWindowBinding.CreateEmptyClip("Idle");
        var w = new AnimationClipWindow();
        try
        {
            w.Bind(root, clip);

            w.IsRecording = true;
            Assert.True(w.IsRecording);
            Assert.False(w.IsPreviewPlaying);
            Assert.True(AnimationRecording.Instance.IsRecording);
            Assert.False(AnimationPreviewSession.Instance.IsActive);

            hips.Transform.LocalPosition = new Float3(0f, 5f, 0f);
            AnimationRecording.Instance.Tick(w.PlaybackTime);
            Assert.Equal(5f, clip.GetBone("Hips")!.PosY.Evaluate(w.PlaybackTime), 3);

            w.IsPreviewPlaying = true;
            Assert.True(w.IsPreviewPlaying);
            Assert.False(w.IsRecording);
            Assert.False(AnimationRecording.Instance.IsRecording);

            Assert.True(w.RecordKey("Hips", "PosX", 9f));
            Assert.Equal(9f, clip.GetBone("Hips")!.PosX.Evaluate(w.PlaybackTime), 3);
            AnimationRecording.Instance.End();
        }
        finally
        {
            w.OnClosed();
        }
    }

    [Fact]
    public void Window_RecordKey_OnReadOnlyClip_Fails()
    {
        var root = new GameObject("Rig");
        root.AddComponent<Animator>();
        var clip = new AnimationClip { Name = "Take", AssetPath = "Hero.glb", AssetID = System.Guid.NewGuid() };
        var w = new AnimationClipWindow();
        w.Bind(root, clip);
        Assert.True(w.Model.ClipIsReadOnly);
        w.IsRecording = true;
        Assert.False(w.IsRecording);
        Assert.False(w.RecordKey("Hips", "PosX", 1f));
    }
}

/// <summary>★ N5 — Blend-shape weight keys: WriteKey("blendShape.<name>"), Rec Tick on slider
/// moves, and the model's BlendShape rows (add/append/key-times/remove).</summary>
public class BlendShapeRecordingTests
{
    private static (GameObject root, GameObject face, SkinnedMeshRenderer smr) BuildFace()
    {
        var root = new GameObject("Model");
        var face = new GameObject("Face");
        face.SetParent(root);
        var mesh = new Mesh
        {
            Vertices = new[] { new Float3(0, 0, 0) },
            BlendShapes = new[]
            {
                new BlendShape
                {
                    Name = "Smile",
                    Frames = new[] { new BlendShapeFrame { Weight = 100f, DeltaVertices = new[] { new Float3(0, 1, 0) } } },
                },
                new BlendShape
                {
                    Name = "Frown",
                    Frames = new[] { new BlendShapeFrame { Weight = 100f, DeltaVertices = new[] { new Float3(0, -1, 0) } } },
                },
            },
        };
        var smr = face.AddComponent<SkinnedMeshRenderer>();
        smr.SharedMesh = mesh;
        return (root, face, smr);
    }

    [Fact]
    public void WriteKey_BlendShape_CreatesTrackAndKey()
    {
        var (root, _, smr) = BuildFace();
        var clip = AnimationWindowBinding.CreateEmptyClip("Face");

        Assert.True(AnimationRecording.WriteKey(clip, root, "Face", "blendShape.Smile", 0.5f, 40f));

        var track = clip.FindBlendShapeTrack("Face", "Smile");
        Assert.NotNull(track);
        Assert.Equal(40f, track!.Weight.Evaluate(0.5f), 3);
        Assert.Equal(1, track.Weight.Keys.Count);
        Assert.Equal(1f, clip.Duration, 3); // existing 1s duration is kept (never shrunk)
        Assert.Equal(0f, smr.GetBlendShapeWeight(0), 3); // keying writes the curve, not the live slider
    }

    [Fact]
    public void WriteKey_BlendShape_UpdatesKeyAtSameTime()
    {
        var (root, _, _) = BuildFace();
        var clip = AnimationWindowBinding.CreateEmptyClip("Face");

        Assert.True(AnimationRecording.WriteKey(clip, root, "Face", "blendShape.Smile", 0.5f, 40f));
        Assert.True(AnimationRecording.WriteKey(clip, root, "Face", "blendShape.Smile", 0.5f, 80f));

        var track = clip.FindBlendShapeTrack("Face", "Smile");
        Assert.Equal(1, track!.Weight.Keys.Count);
        Assert.Equal(80f, track.Weight.Evaluate(0.5f), 3);
    }

    [Fact]
    public void WriteKey_BlendShape_UnknownShapeName_StillCreatesTrack()
    {
        // The editor cannot validate names against every mesh; typing a shape name creates the
        // track (runtime resolves by name and no-ops on missing shapes).
        var (root, _, _) = BuildFace();
        var clip = AnimationWindowBinding.CreateEmptyClip("Face");

        Assert.True(AnimationRecording.WriteKey(clip, root, "Face", "blendShape.Blink", 0f, 10f));
        Assert.NotNull(clip.FindBlendShapeTrack("Face", "Blink"));
    }

    [Fact]
    public void Tick_WritesBlendShapeKey_WhenWeightMoved()
    {
        var (root, _, smr) = BuildFace();
        var clip = AnimationWindowBinding.CreateEmptyClip("Face");
        var rec = AnimationRecording.Instance;
        try
        {
            Assert.True(rec.Begin(root, clip));
            rec.Tick(0.25f);
            Assert.Null(clip.FindBlendShapeTrack("Face", "Smile"));

            smr.SetBlendShapeWeight(0, 60f);
            rec.Tick(0.25f);

            var track = clip.FindBlendShapeTrack("Face", "Smile");
            Assert.NotNull(track);
            Assert.Equal(60f, track!.Weight.Evaluate(0.25f), 3);

            // Moving the slider again at the same playhead updates, not duplicates.
            smr.SetBlendShapeWeight(0, 70f);
            rec.Tick(0.25f);
            Assert.Equal(1, track.Weight.Keys.Count);
            Assert.Equal(70f, track.Weight.Evaluate(0.25f), 3);
        }
        finally
        {
            rec.End();
        }
    }

    [Fact]
    public void Model_BlendShapeRows_SupportAddKeyTimesAndRemove()
    {
        var (root, face, smr) = BuildFace();
        var clip = AnimationWindowBinding.CreateEmptyClip("Face");
        var model = new AnimationClipEditorModel();
        model.Bind(root, clip);

        Assert.True(model.AddBlendShapeProperty("Face", "Smile", 0.25f));
        smr.SetBlendShapeWeight(0, 50f); // live weight feeds the next key's value via curve eval
        Assert.True(model.AddBlendShapeProperty("Face", "Smile", 0.75f));

        var track = clip.FindBlendShapeTrack("Face", "Smile");
        Assert.Equal(2, track!.Weight.Keys.Count);

        var rows = new List<AnimationClipEditorModel.PropertyRow>();
        model.AppendBlendShapeRows(rows);
        var row = System.Linq.Enumerable.Single(rows, r => r.Property == "Smile");
        Assert.Equal(AnimationClipEditorModel.PropertyRowKind.BlendShape, row.Kind);
        Assert.Equal(AnimationClipEditorModel.Channel.BlendShape, row.Channel);

        var times = new List<float>();
        model.FillRowKeyTimes(row, times, 0f, 10f);
        Assert.Equal(2, times.Count);

        Assert.True(model.RemoveKeyframe("Face", AnimationClipEditorModel.Channel.BlendShape, 0.25f, "", "Smile"));
        Assert.Equal(1, track.Weight.Keys.Count);
    }
}
