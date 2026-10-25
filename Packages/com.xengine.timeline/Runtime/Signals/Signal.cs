// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Playables;
using XEngine.Runtime;

namespace XEngine.Timeline;

/// <summary>
/// A reusable signal definition (a named fire-able event). Referenced by <see cref="SignalTrack"/>
/// markers and received by a <see cref="SignalReceiver"/>.
/// </summary>
public sealed class SignalAsset : EngineObject
{
    /// <summary>The signal name receivers match against.</summary>
    public string SignalName = string.Empty;
}

/// <summary>
/// Component placed on a GameObject to receive timeline signals. Bindings map a signal name to a
/// callback; <see cref="Raise"/> invokes the matching callbacks.
/// </summary>
[AddComponentMenu("Cutscene/Signal Receiver")]
public class SignalReceiver : MonoBehaviour
{
    /// <summary>One name → callback binding.</summary>
    public class Binding
    {
        public string Name = string.Empty;
        public Action? Callback;
    }

    public List<Binding> Bindings = new();

    /// <summary>Invokes every binding whose name matches <paramref name="name"/>.</summary>
    public void Raise(string name)
    {
        for (int i = 0; i < Bindings.Count; i++)
            if (Bindings[i].Name == name && Bindings[i].Callback != null)
                Bindings[i].Callback!();
    }
}

/// <summary>A signal marker: fires at <see cref="Time"/> on the timeline.</summary>
public class SignalMarker
{
    public double Time;
    public string Name = string.Empty;
}

/// <summary>
/// A signal track: fires its markers' names on the bound <see cref="SignalReceiver"/> as the playhead
/// crosses them (forward play and loop wrap; scrubbing back does not refire).
/// </summary>
public class SignalTrack : TimelineTrack
{
    public List<SignalMarker> Markers = new();
    private double _prevTime;

    public override void BuildInto(PlayableGraph graph, PlayableDirector director)
    {
        SignalReceiver? receiver = director.GetBinding<SignalReceiver>(this);
        graph.AddOutput(new SignalOutputImpl(this, receiver));
    }

    private sealed class SignalOutputImpl : PlayableOutput
    {
        private readonly SignalTrack _track;
        private readonly SignalReceiver? _receiver;
        internal SignalOutputImpl(SignalTrack track, SignalReceiver? receiver) { _track = track; _receiver = receiver; }

        [XEngine.HotPath]
        public override void Apply(PlayableContext ctx)
        {
            if (_receiver == null) return;
            double cur = ctx.GlobalTime;
            double prev = _track._prevTime;
            var markers = _track.Markers;
            if (cur < prev)
            {
                // Loop wrap: fire the tail [prev, +inf) then the head (-inf, cur].
                Fire(markers, _receiver, prev, double.MaxValue);
                Fire(markers, _receiver, -double.MaxValue, cur);
            }
            else Fire(markers, _receiver, prev, cur);
            _track._prevTime = cur;
        }

        private static void Fire(List<SignalMarker> markers, SignalReceiver r, double from, double to)
        {
            for (int i = 0; i < markers.Count; i++)
                if (markers[i].Time > from && markers[i].Time <= to)
                    r.Raise(markers[i].Name);
        }
    }
}
