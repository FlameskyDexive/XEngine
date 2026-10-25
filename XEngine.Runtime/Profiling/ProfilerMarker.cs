// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace XEngine.Runtime.Profiling;

/// <summary>
/// Named CPU instrumentation scope for the profiler's hierarchy/timeline views. Marker names are
/// interned to int ids at construction (static-init time), and a recorded scope is two Stopwatch
/// timestamps in a preallocated table — the hot path allocates nothing.
/// v1 records the main thread only; worker threads are a later extension.
/// </summary>
public readonly struct ProfilerMarker
{
    internal readonly int Id;

    public ProfilerMarker(string name) => Id = ProfilerFrames.Intern(name);

    /// <summary>Open a scope closed by disposing the returned struct (<c>using</c>).</summary>
    [HotPath]
    public ProfilerScope Auto() => ProfilerFrames.Begin(Id);
}

/// <summary>Live marker scope; <see cref="Dispose"/> stamps the end time.</summary>
public readonly struct ProfilerScope : IDisposable
{
    private readonly int _index; // slot in the current frame table; -1 when the table was full

    internal ProfilerScope(int index) => _index = index;

    [HotPath]
    public void Dispose() => ProfilerFrames.End(_index);
}

/// <summary>
/// Per-frame scope storage: a fixed-capacity double buffer the main loop flips once per simulation
/// step. Writers are main-thread only; the editor reads the completed buffer from the main thread
/// (panel OnGUI), so no synchronization is needed beyond the intern table's lock.
/// </summary>
public static class ProfilerFrames
{
    public const int MaxScopesPerFrame = 512;

    public struct Entry
    {
        public int Id;
        public long StartTicks;
        public long EndTicks;
        public byte Depth;
    }

    private static readonly List<string> s_names = new(64);
    private static readonly Dictionary<string, int> s_nameToId = new(64);
    private static readonly object s_internLock = new();

    private static readonly Entry[] s_bufferA = new Entry[MaxScopesPerFrame];
    private static readonly Entry[] s_bufferB = new Entry[MaxScopesPerFrame];
    private static Entry[] s_current = s_bufferA;
    private static Entry[] s_completed = s_bufferB;
    private static int s_count;
    private static int s_completedCount;
    private static int s_depth;
    private static long s_frameIndex;
    private static long s_completedFrameIndex = -1;

    /// <summary>Milliseconds per Stopwatch tick, for converting recorded spans.</summary>
    public static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    public static int Intern(string name)
    {
        lock (s_internLock)
        {
            if (s_nameToId.TryGetValue(name, out int id)) return id;
            id = s_names.Count;
            s_names.Add(name);
            s_nameToId[name] = id;
            return id;
        }
    }

    public static string NameOf(int id)
    {
        lock (s_internLock)
        {
            return (uint)id < (uint)s_names.Count ? s_names[id] : "?";
        }
    }

    /// <summary>Main loop, once per simulation step: publish the frame just recorded and start a new one.</summary>
    [HotPath]
    public static void BeginFrame()
    {
        Entry[] finished = s_current;
        s_current = s_completed;
        s_completed = finished;
        s_completedCount = s_count;
        s_completedFrameIndex = s_frameIndex++;
        s_count = 0;
        s_depth = 0;
    }

    [HotPath]
    internal static ProfilerScope Begin(int id)
    {
        int index = s_count;
        if (index >= MaxScopesPerFrame)
            return new ProfilerScope(-1); // full: drop silently, depth untouched (End(-1) is a no-op)
        s_count = index + 1;
        ref Entry e = ref s_current[index];
        e.Id = id;
        e.StartTicks = Stopwatch.GetTimestamp();
        e.EndTicks = 0;
        e.Depth = (byte)(s_depth > 255 ? 255 : s_depth);
        s_depth++;
        return new ProfilerScope(index);
    }

    [HotPath]
    internal static void End(int index)
    {
        if (index < 0) return;
        s_depth--;
        s_current[index].EndTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>Monotonic index of the last completed frame (-1 before the first flip).</summary>
    public static long CompletedFrameIndex => s_completedFrameIndex;

    /// <summary>Copy the last completed frame's scopes into the caller's buffer. Returns the count.</summary>
    public static int CopyCompletedFrame(Entry[] destination, out long frameIndex)
    {
        int n = Math.Min(s_completedCount, destination.Length);
        Array.Copy(s_completed, destination, n);
        frameIndex = s_completedFrameIndex;
        return n;
    }
}
