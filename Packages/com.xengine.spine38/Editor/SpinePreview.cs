using System;

using XEngine.Editor.GUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Spine38;
using XEngine.Vector;

namespace XEngine.Spine38.Editor;

/// <summary>
/// Builds and drives the <see cref="SkeletonAnimation"/> subject shown by
/// <see cref="PreviewRenderer"/> (SkeletonDataAsset inspector preview).
/// </summary>
public static class SpinePreview
{
    // Skeleton.GetBounds scratch (editor main thread only).
    private static float[] s_boundsBuffer = new float[8];

    public static GameObject CreateSubject(SkeletonDataAsset asset, string? animationName = null, string? skinName = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var go = new GameObject("SpinePreview");
        var animation = go.AddComponent<SkeletonAnimation>();
        animation.SkeletonDataAsset = asset;
        animation.Loop = true;
        if (!string.IsNullOrEmpty(skinName))
            animation.InitialSkinName = skinName;
        if (!string.IsNullOrEmpty(animationName))
            animation.AnimationName = animationName;
        animation.Initialize(overwrite: true);
        animation.UpdateAnimation(0f);
        return go;
    }

    /// <summary>
    /// Real posed-skeleton bounds via <c>Skeleton.GetBounds</c>; falls back to a scale-derived
    /// guess when the skeleton has no visible attachments (e.g. atlas not yet resident, or a
    /// skin with no attachments like mix-and-match's default).
    /// </summary>
    public static AABB ComputeBounds(GameObject subject, SkeletonDataAsset? asset)
    {
        var renderer = subject.GetComponent<SkeletonRenderer>();
        global::Spine.Skeleton? skeleton = renderer?.Skeleton;
        if (skeleton != null)
        {
            skeleton.GetBounds(out float x, out float y, out float w, out float h, ref s_boundsBuffer);
            if (w > 0f && h > 0f)
            {
                // Slight padding so extremities don't kiss the viewport edge; keep a thin Z so
                // FitToSubject's max-dimension math is driven by the visible XY extents.
                float pad = MathF.Max(w, h) * 0.05f;
                return AABB.FromCenterAndSize(
                    new Float3(x + w * 0.5f, y + h * 0.5f, 0f),
                    new Float3(w + pad, h + pad, 0.1f));
            }
        }

        return EstimateBounds(asset);
    }

    public static AABB EstimateBounds(SkeletonDataAsset? asset)
    {
        float size = asset != null && asset.Scale > 0f ? Math.Max(1f, 1f / asset.Scale * 0.25f) : 2f;
        size = Math.Clamp(size, 0.5f, 8f);
        return AABB.FromCenterAndSize(Float3.Zero, new Float3(size, size, size));
    }

    /// <summary>Install a freshly built subject and frame it straight-on (Unity's 2D skeleton preview).</summary>
    public static void Apply(PreviewRenderer preview, SkeletonDataAsset asset, string? animationName = null, string? skinName = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        GameObject subject = CreateSubject(asset, animationName, skinName);
        AABB bounds = ComputeBounds(subject, asset);
        preview.ReplaceSubject(subject, bounds);
        preview.FrameSubject(bounds, yaw: 0f, pitch: 0f);
    }

    /// <summary>Advance the preview skeleton by <paramref name="deltaTime"/> (the preview scene
    /// is never Update()d, so the inspector pumps this from its GUI pass).</summary>
    public static void Tick(PreviewRenderer preview, float deltaTime)
    {
        var animation = preview.Subject?.GetComponent<SkeletonAnimation>();
        if (animation == null)
            return;
        animation.UpdateAnimation(deltaTime);
        preview.MarkPreviewDirty();
    }

    /// <summary>Switch the previewed animation on track 0 (looping) and pose the first frame.</summary>
    public static void SetAnimation(PreviewRenderer preview, string animationName)
    {
        var animation = preview.Subject?.GetComponent<SkeletonAnimation>();
        if (animation == null)
            return;
        animation.SetAnimation(0, animationName, loop: true);
        animation.UpdateAnimation(0f);
        preview.MarkPreviewDirty();
    }

    /// <summary>Clear playback and show the setup pose (Unity's "Setup Pose" button).</summary>
    public static void SetupPose(PreviewRenderer preview)
    {
        var animation = preview.Subject?.GetComponent<SkeletonAnimation>();
        if (animation == null)
            return;
        animation.AnimationName = "";
        animation.UpdateAnimation(0f); // syncs the cleared name -> ClearTrack(0)
        global::Spine.Skeleton? skeleton = animation.Skeleton;
        if (skeleton != null)
        {
            skeleton.SetToSetupPose();
            skeleton.UpdateWorldTransform();
        }
        preview.MarkPreviewDirty();
    }

    /// <summary>Switch skin, keeping the current animation posed.</summary>
    public static void SetSkin(PreviewRenderer preview, string skinName)
    {
        var animation = preview.Subject?.GetComponent<SkeletonAnimation>();
        if (animation == null)
            return;
        animation.InitialSkinName = skinName;
        animation.Initialize(overwrite: true);
        animation.UpdateAnimation(0f);
        preview.MarkPreviewDirty();
    }

    /// <summary>Rebuild the AnimationState (picks up edited mix durations) without losing the pose.</summary>
    public static void ReinitializeState(PreviewRenderer preview)
    {
        var animation = preview.Subject?.GetComponent<SkeletonAnimation>();
        if (animation == null)
            return;
        animation.Initialize(overwrite: true);
        animation.UpdateAnimation(0f);
        preview.MarkPreviewDirty();
    }

    /// <summary>Name of the animation currently on track 0, or "" in setup pose.</summary>
    public static string CurrentAnimation(PreviewRenderer preview)
        => preview.Subject?.GetComponent<SkeletonAnimation>()?.AnimationName ?? "";
}
