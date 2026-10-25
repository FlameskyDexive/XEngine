// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

/// <summary>Unity Model Import Settings tabs (Inspector body).</summary>
public enum ModelImportTab : int
{
    Model = 0,
    Rig = 1,
    Animation = 2,
    Materials = 3,
}

/// <summary>Unity Animation Type on the Rig tab. Humanoid mapping is v2.</summary>
public enum ModelAnimationType : int
{
    Generic = 0,
    Humanoid = 1,
    Legacy = 2,
}

/// <summary>Unity Avatar Definition on the Rig tab.</summary>
public enum ModelAvatarDefinition : int
{
    CreateFromThisModel = 0,
    CopyFromOtherAvatar = 1,
    NoAvatar = 2,
}

/// <summary>
/// Unit-testable rules for the Model Import Settings inspector: when the bottom
/// preview should play a clip, which clip index is valid, and clip frame range.
/// </summary>
public static class ModelImportEditorModel
{
    public static bool ShouldPreviewClip(ModelImportTab tab, bool importAnimation, int clipCount)
        => importAnimation && clipCount > 0;

    public static int ClampClipIndex(int index, int clipCount)
    {
        if (clipCount <= 0) return -1;
        if (index < 0) return 0;
        return index >= clipCount ? clipCount - 1 : index;
    }

    public static float ClipStartFrame(AnimationClip? clip) => 0f;

    public static float ClipEndFrame(AnimationClip? clip)
    {
        if (clip == null) return 0f;
        if (clip.DurationInTicks > 0f) return clip.DurationInTicks;
        float fps = clip.TicksPerSecond > 0.01f ? clip.TicksPerSecond : 30f;
        return clip.Duration * fps;
    }
}
