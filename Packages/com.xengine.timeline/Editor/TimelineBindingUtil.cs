// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core;
using XEngine.Runtime;
using XEngine.Runtime.ParticleSystem;

namespace XEngine.Timeline.Editor;

/// <summary>
/// Shared track-binding helpers for the Timeline window and PlayableDirector Inspector (M23).
/// Bind Selection uses the Hierarchy GameObject currently in <see cref="Selection"/>.
/// </summary>
internal static class TimelineBindingUtil
{
    public static string TypeLabel(TimelineTrack track) => track switch
    {
        AnimationTrack => "Anim",
        AudioTrack => "Audio",
        CameraTrack => "Cam",
        ActivationTrack => "Act",
        ParticleTrack => "FX",
        SignalTrack => "Sig",
        _ => "?",
    };

    public static string BindingKind(TimelineTrack track) => track switch
    {
        AnimationTrack => "Animator",
        AudioTrack => "AudioSource",
        CameraTrack => "Camera",
        ActivationTrack => "GameObject",
        ParticleTrack => "Particle",
        SignalTrack => "SignalReceiver",
        _ => "Object",
    };

    public static string BoundName(PlayableDirector? director, TimelineTrack track)
    {
        object? o = director?.GetBinding(track);
        return o switch
        {
            GameObject go => string.IsNullOrEmpty(go.Name) ? "(GameObject)" : go.Name,
            MonoBehaviour mb => mb.GameObject != null && !string.IsNullOrEmpty(mb.GameObject.Name)
                ? mb.GameObject.Name
                : mb.GetType().Name,
            EngineObject eo => string.IsNullOrEmpty(eo.Name) ? eo.GetType().Name : eo.Name,
            null => "(none)",
            _ => o.GetType().Name,
        };
    }

    public static void BindSelection(PlayableDirector director, TimelineTrack track)
    {
        if (Selection.ActiveObject is not GameObject go)
        {
            Debug.LogWarning("[Timeline] Select a GameObject in the Hierarchy, then Bind Selection.");
            return;
        }

        bool ok = track switch
        {
            AnimationTrack => Bind(director, track, go.GetComponentInChildren<Animator>()),
            AudioTrack => Bind(director, track, go.GetComponentInChildren<AudioSource>()),
            CameraTrack => Bind(director, track, go.GetComponentInChildren<Camera>()),
            ActivationTrack => BindGo(director, track, go),
            ParticleTrack => Bind(director, track, go.GetComponentInChildren<ParticleSystemComponent>()),
            SignalTrack => Bind(director, track, go.GetComponentInChildren<SignalReceiver>()),
            _ => false,
        };
        if (!ok)
            Debug.LogWarning($"[Timeline] '{go.Name}' has no {BindingKind(track)} to bind.");
        else
            director.RebuildGraph();
    }

    public static void ClearBinding(PlayableDirector director, TimelineTrack track)
    {
        director.SetBinding<object>(track, null);
        director.RebuildGraph();
    }

    private static bool Bind<T>(PlayableDirector director, TimelineTrack track, T? target) where T : class
    {
        if (target == null) return false;
        director.SetBinding(track, target);
        return true;
    }

    private static bool BindGo(PlayableDirector director, TimelineTrack track, GameObject go)
    {
        director.SetBinding(track, go);
        return true;
    }
}
