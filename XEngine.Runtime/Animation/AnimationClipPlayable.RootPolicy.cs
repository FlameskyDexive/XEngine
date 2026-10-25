using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Animation;

public partial class AnimationClipPlayable
{
    // With an explicit policy anywhere in this graph, pin a state's own sampled pose before
    // mixing. Pinning the final mixed Transform would also remove Disabled states' animation.
    [HotPath]
    private void PinRootMotionContribution()
    {
        SkeletonBinding? binding = Binding;
        if (binding == null || !binding.PinRootMotionPerState || Output == null
            || BoundRootMotionUsesModelRoot
            || BoundRootMotionPolicy is not (ClipRootMotionPolicy.LegacyAutomatic or ClipRootMotionPolicy.ExplicitBone))
            return;
        if (binding.ExtractRootMotion)
        {
            if (_rootMotionClipBone < 0) return;
            int sourceIndex = _boneMap![_rootMotionClipBone];
            Transform? sourceParent = binding.Bones[sourceIndex]?.Parent;
            PinContribution(sourceIndex, binding.RotateRootMotion, binding.PlanarRootMotion,
                in _rootMotionPinProjection, sourceParent, bodySpace: true);
            if (_rootMotionVisualBone != sourceIndex)
                PinContribution(_rootMotionVisualBone, false, binding.PlanarRootMotion,
                    in _rootMotionPinProjection, sourceParent, bodySpace: true);
        }
        else if (binding.HasSkinnedRenderer)
        {
            if (BoundRootMotionPolicy == ClipRootMotionPolicy.ExplicitBone)
            {
                if (_rootMotionClipBone >= 0)
                    PinContribution(_boneMap![_rootMotionClipBone], false, true,
                        in _rootMotionPinProjection, null, bodySpace: false);
            }
            else if (_travelProjection.AxisCount > 0)
                PinContribution(_travelProjection.BoneIndex, false, true,
                    in _travelProjection, null, bodySpace: false);
            else
                PinContribution(_legacyInPlaceRoot, false, false,
                    in _travelProjection, null, bodySpace: false);
        }
    }

    [HotPath]
    private void PinContribution(int index, bool rotation, bool project,
        in TravelProjection projection, Transform? sourceParent, bool bodySpace)
    {
        SkeletonBinding binding = Binding!;
        if ((uint)index >= (uint)binding.BoneCount || binding.Bones[index] is not Transform bone)
            return;
        ref BonePose pose = ref Output!.Bones[index];
        BonePose reference = binding.ReferencePose[index];
        if ((pose.Channels & PoseChannel.Position) != 0)
        {
            if (!project)
                pose.LocalPosition = reference.LocalPosition;
            else
            {
                Float3 offset = pose.LocalPosition - reference.LocalPosition;
                Transform? parent = bone.Parent;
                Transform? body = binding.RootMotionBody;
                if (bodySpace)
                {
                    Float3 worldOffset = parent != null ? parent.TransformVector(offset) : offset;
                    offset = body != null ? body.InverseTransformVector(worldOffset) : worldOffset;
                }
                // Project against the span, not independently against non-orthogonal axes after
                // planar conversion (scale/parent rotation can make them linearly dependent).
                byte count = 0;
                Float3 a0 = default, a1 = default, a2 = default;
                for (int i = 0; i < projection.AxisCount; i++)
                {
                    Float3 axis = projection.GetAxis(i);
                    if (bodySpace)
                    {
                        axis = sourceParent != null ? sourceParent.TransformVector(axis) : axis;
                        axis = body != null ? body.InverseTransformVector(axis) : axis;
                        axis.Y = 0f;
                    }
                    AddRootMotionAxis(axis, ref count, ref a0, ref a1, ref a2);
                }
                if (count > 0) offset -= a0 * Float3.Dot(offset, a0);
                if (count > 1) offset -= a1 * Float3.Dot(offset, a1);
                if (count > 2) offset -= a2 * Float3.Dot(offset, a2);
                if (bodySpace)
                {
                    Float3 worldResidual = body != null ? body.TransformVector(offset) : offset;
                    offset = parent != null ? parent.InverseTransformVector(worldResidual) : worldResidual;
                }
                pose.LocalPosition = reference.LocalPosition + offset;
            }
        }
        if (rotation && (pose.Channels & PoseChannel.Rotation) != 0)
            pose.LocalRotation = reference.LocalRotation;
    }
}
