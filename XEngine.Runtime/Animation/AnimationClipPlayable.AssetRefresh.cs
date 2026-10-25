using XEngine.Animation.Humanoid;

namespace XEngine.Animation;

public partial class AnimationClipPlayable
{
    private Avatar? _samplingAvatar;
    private System.Guid _samplingAvatarId;
    internal HumanoidPoseSampler? HumanoidSampler { get; private set; }
    protected bool HumanoidMirror;
    internal System.Guid SamplingAvatarAssetId => _samplingAvatarId;

    internal void ConfigureHumanoidSampler(Avatar? avatar)
    {
        _samplingAvatar = avatar;
        if (avatar != null) _samplingAvatarId = avatar.AssetID;
        HumanoidSampler = null;
    }

    private void BindHumanoidSampler(SkeletonBinding binding)
    {
        HumanoidSampler = null;
        if (Clip?.IsHumanoid == true && _samplingAvatar is { IsHumanoid: true, IsDisposed: false })
        {
            var sampler = new HumanoidPoseSampler(Clip, _samplingAvatar)
            { Mirror = HumanoidMirror || MirrorBones };
            sampler.Bind(binding);
            HumanoidSampler = sampler;
        }
    }
}
