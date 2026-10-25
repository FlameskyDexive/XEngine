using System;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Spine38;

/// <summary>Follows a bone on a <see cref="SkeletonGraphic"/> via <see cref="RectTransform"/>.</summary>
[AddComponentMenu("Spine 3.8/Bone Follower Graphic")]
public class BoneFollowerGraphic : UIBehaviour
{
    public SkeletonGraphic? SkeletonGraphic;
    public string BoneName = "";
    public float MeshScale = 1f;

    [NonSerialized] private Bone? _bone;

    public Bone? Bone => _bone;

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public bool SetBone(string name)
    {
        BoneName = name;
        _bone = SkeletonGraphic?.Skeleton?.FindBone(name);
        return _bone != null;
    }

    public override void LateUpdate()
    {
        if (_bone == null && !string.IsNullOrEmpty(BoneName))
            SetBone(BoneName);
        if (_bone == null)
            return;

        RectTransform? rt = GameObject.RectTransform;
        if (rt == null)
            return;

        rt.AnchoredPosition = new Float2(_bone.WorldX * MeshScale, _bone.WorldY * MeshScale);
    }
}
