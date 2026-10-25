using Spine;

using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Samples.OtherExamples;

/// <summary>Click toggles goblin / goblingirl. Same bones, different authored skin.</summary>
public class Goblins : MonoBehaviour
{
    public float ExtraRotation;
    public bool GirlSkin;
    public SkeletonAnimation? SkeletonAnimation;

    private Bone? _head;

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        SkeletonAnimation?.Initialize(overwrite: false);
        if (SkeletonAnimation != null)
        {
            SkeletonAnimation.UpdateLocal += OnUpdateLocal;
            _head = SkeletonAnimation.Skeleton?.FindBone("head");
        }

        ApplySkin();
    }

    public override void Update()
    {
        if (Input.GetMouseButtonDown(0))
            ToggleSkin();
    }

    public void ToggleSkin()
    {
        GirlSkin = !GirlSkin;
        ApplySkin();
    }

    public void ApplySkin()
    {
        Skeleton? skeleton = SkeletonAnimation?.Skeleton;
        if (skeleton == null)
            return;

        skeleton.SetSkin(GirlSkin ? "goblingirl" : "goblin");
        skeleton.SetupPoseSlots();
        if (GirlSkin)
        {
            Slot? rightItem = skeleton.FindSlot("right-hand-item");
            if (rightItem != null)
                rightItem.Pose.Attachment = null;
            skeleton.SetAttachment("left-hand-item", "spear");
        }
        else
            skeleton.SetAttachment("left-hand-item", "dagger");
    }

    private void OnUpdateLocal(SkeletonAnimation _)
    {
        if (_head != null)
            _head.Pose.Rotation += ExtraRotation;
    }
}
