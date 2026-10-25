using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;
using XEngine.Runtime.Audio;

namespace XEngine.Editor.Core;

/// <summary>
/// Reconciles the two editor-side audio switches — the Scene view's speaker toggle (edit mode) and the
/// Game view's mute button (play mode) — into the single global master volume, so neither toggle can
/// silently undo the other.
/// </summary>
internal static class EditorAudio
{
    /// <summary>Scene view speaker toggle: whether scene audio is audible while not playing.</summary>
    public static bool SceneViewAudible { get; set; } = true;

    /// <summary>Game view mute button: silences everything while playing.</summary>
    public static bool GameViewMuted { get; set; }

    public static void Apply()
    {
        if (!AudioContext.IsInitialized) return;

        bool audible = Application.IsPlaying ? !GameViewMuted : SceneViewAudible;
        float volume = 1f;
        try { volume = EditorRegistries.GetSettings<AudioSettings>().GlobalVolume; }
        catch { /* settings not loaded yet — fall back to full volume */ }

        AudioContext.MasterVolume = audible ? volume : 0f;
    }
}
