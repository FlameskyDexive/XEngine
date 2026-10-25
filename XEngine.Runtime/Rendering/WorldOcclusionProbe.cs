using System.Collections.Generic;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// B7b — one pending world-surface visibility probe. The pipeline records a single-pixel
/// scissored, color-write-disabled draw of the probe's mesh/material wrapped in an
/// occlusion query (see <see cref="CommandOpcode.BeginOcclusionQuery"/>); the result answers
/// "was the panel visible at this pixel against the scene depth" with full MSAA/stencil/clip
/// semantics preserved, without colliders or CPU depth reads.
/// </summary>
public interface IWorldOcclusionProbe
{
    /// <summary>Id the pipeline encodes into Begin/EndOcclusionQuery; polled via
    /// <c>IGraphicsDevice.TryGetOcclusionQueryResult</c> once the frame's submission retires.</summary>
    ulong QueryId { get; }

    Mesh ProbeMesh { get; }
    Material ProbeMaterial { get; }
    int ProbePass { get; }
    Float4x4 ProbeModel { get; }

    /// <summary>
    /// Screen-space pixel the probe must scissor to for this viewer. Called per camera; return
    /// false when the probe point is outside the viewport (the pipeline then skips the probe for
    /// that viewer without consuming the query id).
    /// </summary>
    bool TryGetProbePixel(in Float4x4 viewProjection, int viewportWidth, int viewportHeight, out int pixelX, out int pixelY);
}

/// <summary>
/// Frame-global registry of pending world occlusion probes. Providers register while a click
/// verdict is pending and unregister when it resolves or is invalidated; the UI world pass
/// records every registered probe after the regular UI draws.
/// </summary>
public static class WorldOcclusionProbes
{
    private static readonly List<IWorldOcclusionProbe> s_probes = [];
    private static readonly object s_gate = new();

    public static void Register(IWorldOcclusionProbe probe)
    {
        lock (s_gate)
        {
            if (!s_probes.Contains(probe))
                s_probes.Add(probe);
        }
    }

    public static void Unregister(IWorldOcclusionProbe probe)
    {
        lock (s_gate)
        {
            s_probes.Remove(probe);
        }
    }

    /// <summary>Snapshot for the render thread; empty when nothing is pending.</summary>
    public static IWorldOcclusionProbe[] Snapshot()
    {
        lock (s_gate)
        {
            return s_probes.Count == 0 ? [] : s_probes.ToArray();
        }
    }
}
