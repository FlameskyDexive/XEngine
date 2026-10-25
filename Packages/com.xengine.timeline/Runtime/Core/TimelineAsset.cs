// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Timeline;

/// <summary>
/// The Echo-serialized timeline asset played by a <see cref="PlayableDirector"/>. Holds the duration
/// and a heterogeneous list of tracks (Animation / Audio / Activation / Camera / Particle / Signal)
/// that the director compiles into the playable graph.
/// </summary>
public sealed class TimelineAsset : EngineObject
{
    /// <summary>Total length of the timeline in seconds.</summary>
    public double Duration = 1.0;

    /// <summary>All tracks. Each is built into the director's graph on <see cref="PlayableDirector.RebuildGraph"/>.</summary>
    public List<TimelineTrack> Tracks = new();
}
