using System;

using Spine;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Spine38;

/// <summary>Copies a Spine bone's world pose onto this GameObject each LateUpdate.</summary>
[AddComponentMenu("Spine 3.8/Bone Follower")]
public class BoneFollower : MonoBehaviour
{
    public SkeletonRenderer? SkeletonRenderer;
    public string BoneName = "";
    public bool FollowXYPosition = true;
    public bool FollowBoneRotation = true;

    [NonSerialized] private Bone? _bone;

    public Bone? Bone => _bone;

    public bool SetBone(string name)
    {
        BoneName = name;
        _bone = SkeletonRenderer?.Skeleton?.FindBone(name);
        return _bone != null;
    }

    public override void LateUpdate()
    {
        if (_bone == null && !string.IsNullOrEmpty(BoneName))
            SetBone(BoneName);
        if (_bone == null || SkeletonRenderer == null)
            return;

        Transform self = Transform;
        Transform skeleton = SkeletonRenderer.Transform;
        if (FollowXYPosition)
        {
            if (ReferenceEquals(self.Parent, skeleton))
                self.LocalPosition = new Float3(_bone.WorldX, _bone.WorldY, self.LocalPosition.Z);
            else
            {
                Float3 world = skeleton.TransformPoint(new Float3(_bone.WorldX, _bone.WorldY, 0f));
                self.Position = new Float3(world.X, world.Y, FollowXYPosition ? world.Z : self.Position.Z);
            }
        }

        if (FollowBoneRotation)
            self.LocalEulerAngles = new Float3(self.LocalEulerAngles.X, self.LocalEulerAngles.Y, _bone.WorldRotationX);
    }
}
