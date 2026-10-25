// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Playables;
using XEngine.Runtime;

namespace XEngine.Timeline;

/// <summary>
/// Drives a <see cref="PlayableGraph"/> from a <see cref="TimelineAsset"/> using a scrubable
/// <see cref="ManualClock"/>. On <see cref="RebuildGraph"/>, each track builds its subgraph + output
/// into the graph (an Animation track drives a bound Animator's pose; audio / activation / camera /
/// particle / signal tracks drive their bound targets). Tracks bind to scene objects via
/// <see cref="SetBinding{T}"/>.
/// </summary>
[AddComponentMenu("Cutscene/Playable Director")]
[ExecuteAlways]
public class PlayableDirector : MonoBehaviour
{
    /// <summary>The timeline asset to play.</summary>
    public AssetRef<TimelineAsset> Timeline;

    /// <summary>Wrap behaviour at the timeline's end.</summary>
    public DirectorWrapMode Wrap = DirectorWrapMode.Hold;

    private PlayableGraph? _graph;
    private ManualClock? _clock;
    private bool _playing;
    private readonly Dictionary<TimelineTrack, object> _bindings = new();

    public bool IsPlaying => _playing;

    /// <summary>The director's playhead time in seconds. Set to scrub, then call <see cref="Evaluate"/>.</summary>
    public double Time
    {
        get => _clock?.CurrentTime ?? 0d;
        set
        {
            if (_clock == null) EnsureGraph();
            if (_clock != null) _clock.CurrentTime = value < 0d ? 0d : value;
        }
    }

    public double Duration => _clock?.Duration ?? -1d;

    public override void OnEnable() => RebuildGraph();

    public override void OnDisable()
    {
        _graph?.Destroy();
        _graph = null;
        _clock = null;
        _playing = false;
    }

    public override void Update()
    {
        if (!_playing || _graph == null) return;
        _graph.Evaluate(XEngine.Runtime.Time.DeltaTime); // qualified: the Time property shadows the static.
        // Hold keeps sampling the clamped last frame; None stops once the playhead reaches the end.
        if (Wrap == DirectorWrapMode.None && Duration > 0d && Time >= Duration)
            _playing = false;
    }

    public void Play()
    {
        EnsureGraph();
        if (Wrap == DirectorWrapMode.None && Duration > 0d && Time >= Duration)
            Time = 0d;
        _playing = true;
    }

    public void Pause() => _playing = false;

    public void Stop()
    {
        _playing = false;
        if (_clock != null) _clock.CurrentTime = 0d;
        _graph?.Sample();
    }

    /// <summary>Evaluate the graph at the current playhead without advancing (scrub / edit-mode preview).</summary>
    public void Evaluate()
    {
        EnsureGraph();
        _graph?.Sample();
    }

    /// <summary>Binds <paramref name="target"/> to <paramref name="track"/> (the Animator/AudioSource/GameObject/etc. the track drives).</summary>
    public void SetBinding<T>(TimelineTrack track, T? target) where T : class
    {
        if (target == null) _bindings.Remove(track);
        else _bindings[track] = target;
    }

    /// <summary>Returns the binding for <paramref name="track"/> as <typeparamref name="T"/>, or null.</summary>
    public T? GetBinding<T>(TimelineTrack track) where T : class
        => _bindings.TryGetValue(track, out object? t) ? t as T : null;

    /// <summary>Untyped binding lookup for the Timeline window and Inspector.</summary>
    public object? GetBinding(TimelineTrack track)
        => _bindings.TryGetValue(track, out object? t) ? t : null;

    /// <summary>Rebuilds the graph + clock from the current <see cref="Timeline"/> asset and its tracks.</summary>
    public void RebuildGraph()
    {
        _graph?.Destroy();
        _clock = new ManualClock();
        TimelineAsset? ta = Timeline.Res;
        if (ta != null && ta.Duration > 0d)
            _clock.Duration = ta.Duration;
        _clock.Wrap = Wrap switch
        {
            DirectorWrapMode.Loop => ClockWrapMode.Loop,
            _ => ClockWrapMode.Clamp, // Hold and None both clamp; None stops in Update
        };
        _graph = new PlayableGraph(_clock);

        if (ta != null)
        {
            var tracks = ta.Tracks;
            for (int i = 0; i < tracks.Count; i++)
                tracks[i].BuildInto(_graph, this);
        }
    }

    private void EnsureGraph() { if (_graph == null) RebuildGraph(); }
}
