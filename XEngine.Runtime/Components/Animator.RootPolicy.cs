using XEngine.Animation;

namespace XEngine.Runtime;

public partial class Animator
{
    [HotPath]
    private void PrepareRootMotionPolicy()
    {
        if (_binding == null) return;
        _binding.PinRootMotionPerState = _layerMixer != null && ContainsConfiguredRootPolicy(_layerMixer);
        _binding.ExtractRootMotion = ApplyRootMotion;
        _binding.PlanarRootMotion = RootMotionPlanar;
        _binding.RotateRootMotion = ApplyRootMotionRotation;
        _binding.RootMotionBody = Transform;
    }

    [HotPath]
    private static bool ContainsConfiguredRootPolicy(AnimationPlayable playable)
    {
        if (playable is AnimationClipPlayable clip)
            return (clip.Weight > 0f || clip.IsFading)
                && clip.BoundRootMotionPolicy != ClipRootMotionPolicy.LegacyAutomatic;
        for (int i = 0; i < playable.InputCount; i++)
            if (playable.GetInput(i) is AnimationPlayable child && ContainsConfiguredRootPolicy(child))
                return true;
        return false;
    }
}
