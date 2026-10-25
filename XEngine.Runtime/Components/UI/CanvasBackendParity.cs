// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.UI;

/// <summary>
/// Backend-neutral CanvasRenderer fingerprint used to gate OpenGL / Vulkan / D3D12
/// screenshot diffs. GPU captures still go through the RHI image harness; this hash
/// proves the host submitted the same mesh, clip, and sort payload to every backend.
/// </summary>
public readonly struct CanvasNodeFingerprint : IEquatable<CanvasNodeFingerprint>
{
    public readonly long SortKey;
    public readonly UISurface Surface;
    public readonly int VertexCount;
    public readonly int IndexCount;
    public readonly bool ClipEnabled;
    public readonly bool UsesStencil;

    public CanvasNodeFingerprint(long sortKey, UISurface surface, int vertexCount, int indexCount,
        bool clipEnabled, bool usesStencil)
    {
        SortKey = sortKey;
        Surface = surface;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        ClipEnabled = clipEnabled;
        UsesStencil = usesStencil;
    }

    public bool Equals(CanvasNodeFingerprint other)
        => SortKey == other.SortKey
           && Surface == other.Surface
           && VertexCount == other.VertexCount
           && IndexCount == other.IndexCount
           && ClipEnabled == other.ClipEnabled
           && UsesStencil == other.UsesStencil;

    public override bool Equals(object? obj) => obj is CanvasNodeFingerprint other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(SortKey, (int)Surface, VertexCount, IndexCount, ClipEnabled, UsesStencil);
}

/// <summary>CPU fingerprint and GPU screenshot-diff policy for XGUI CanvasRenderer submissions.</summary>
public static class CanvasBackendParity
{
    /// <summary>Backends that must consume the same CanvasRenderer contract. Null is not a fallback.</summary>
    public static readonly GraphicsBackend[] ScreenshotBackends =
    [
        GraphicsBackend.OpenGL,
        GraphicsBackend.Vulkan,
        GraphicsBackend.Direct3D12,
    ];

    /// <summary>Per-channel GPU screenshot tolerance matching the RHI comparison harness.</summary>
    public const byte MaxChannelDelta = 3;

    /// <summary>UI overlay screenshots must not differ beyond rounding noise.</summary>
    public const double MaxMismatchRatio = 0d;

    public static bool IsScreenshotBackend(GraphicsBackend backend)
        => backend == GraphicsBackend.OpenGL
           || backend == GraphicsBackend.Vulkan
           || backend == GraphicsBackend.Direct3D12;

    /// <summary>
    /// Returns false when a requested screenshot backend was replaced by Null or another API.
    /// Auto may resolve to any screenshot backend.
    /// </summary>
    public static bool TryValidateBackend(GraphicsBackend requested, GraphicsBackend actual, out string error)
    {
        error = string.Empty;
        if (!IsScreenshotBackend(actual))
        {
            error = "XGUI screenshot backends must stay OpenGL, Vulkan, or Direct3D12; Null is not a silent fallback.";
            return false;
        }

        if (requested != GraphicsBackend.Auto && requested != actual)
        {
            error = "Requested XGUI screenshot backend must not be replaced by a different API.";
            return false;
        }

        return true;
    }

    public static CanvasNodeFingerprint Fingerprint(CanvasRenderNode node)
    {
        Mesh? mesh = node.Mesh;
        bool usable = mesh != null && !mesh.IsDisposed;
        int vertices = usable ? mesh!.VertexCount : 0;
        int indices = usable ? mesh.IndexCount : 0;
        return new CanvasNodeFingerprint(
            node.SortKey,
            node.Surface,
            vertices,
            indices,
            node.Clip.Enabled,
            node.Clip.UsesStencil);
    }

    public static bool AreEqual(List<CanvasRenderNode> left, List<CanvasRenderNode> right)
    {
        if (left == null || right == null || left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!Fingerprint(left[i]).Equals(Fingerprint(right[i])))
                return false;
        }
        return true;
    }
}
