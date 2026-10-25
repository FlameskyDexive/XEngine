using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Animation;

public partial class AnimationClipPlayable
{
    // A model-root trajectory is not a bone output. Its clip path is empty; the explicit Root
    // selection uses "." to distinguish it from None. Never put the actor into the pose table:
    // authored absolute root TRS would overwrite gameplay placement before extracting a delta.
    internal bool BoundRootMotionUsesModelRoot { get; private set; }
    private Quaternion _modelRootReferenceRotation;
    private Quaternion _modelRootInverseRotation;
    private Float3 _modelRootInverseScale;

    private void ResetModelRootMotionBinding()
    {
        BoundRootMotionUsesModelRoot = false;
        _modelRootReferenceRotation = Quaternion.Identity;
        _modelRootInverseRotation = Quaternion.Identity;
        _modelRootInverseScale = Float3.One;
    }

    private void CaptureModelRootMotionBinding(List<AnimationClip.AnimBone> bones)
    {
        int found = -1;
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].BoneName != string.Empty) continue;
            if (found >= 0) return; // Ambiguous authored root tracks fail closed; no first match.
            found = i;
        }
        if (found < 0) return;
        AnimationClip.AnimBone root = bones[found];
        bool position = root.PosX != null || root.PosY != null || root.PosZ != null;
        bool rotation = root.RotX != null || root.RotY != null || root.RotZ != null || root.RotW != null;
        if ((!position && !rotation)
            || (position && (!HasKeys(root.PosX) || !HasKeys(root.PosY) || !HasKeys(root.PosZ)))
            || (rotation && (!HasKeys(root.RotX) || !HasKeys(root.RotY) || !HasKeys(root.RotZ) || !HasKeys(root.RotW))))
            return;

        if (root.HasSourceReferencePose)
        {
            Quaternion reference = root.SourceRotation;
            Float3 scale = root.SourceScale;
            float length = Quaternion.Dot(reference, reference);
            if (!float.IsFinite(length) || length <= 1e-12f
                || !ValidScale(scale.X) || !ValidScale(scale.Y) || !ValidScale(scale.Z))
                return;
            _modelRootReferenceRotation = Quaternion.NormalizeSafe(reference);
            _modelRootInverseRotation = Quaternion.Inverse(_modelRootReferenceRotation);
            _modelRootInverseScale = new Float3(1f / scale.X, 1f / scale.Y, 1f / scale.Z);
        }
        BoundRootMotionUsesModelRoot = true;
        _rootMotionClipBone = found;
        // Source/visual bone indices deliberately remain -1. Only Animator consumes this delta.
    }

    private static bool HasKeys(XEngine.Runtime.AnimationCurve? curve) => curve != null && curve.KeyCount > 0;
    private static bool ValidScale(float value) => float.IsFinite(value) && MathF.Abs(value) > 1e-8f;

    [HotPath]
    private void ConvertModelRootMotion(ref Float3 position, ref Quaternion rotation)
    {
        // Curves live in the source model's parent space. Remove the imported root reference
        // frame once; Animator applies the current model frame, including gameplay placement.
        position = (_modelRootInverseRotation * position) * _modelRootInverseScale;
        rotation = Quaternion.NormalizeSafe(_modelRootInverseRotation * rotation * _modelRootReferenceRotation);
    }
}
