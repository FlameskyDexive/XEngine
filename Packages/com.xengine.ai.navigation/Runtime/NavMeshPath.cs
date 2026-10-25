// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

public enum NavMeshPathStatus
{
    PathInvalid = 0,
    PathPartial = 1,
    PathComplete = 2,
}

/// <summary>Reusable path buffer. Callers should keep one instance and pass it to <see cref="NavMesh.CalculatePath"/>.</summary>
public sealed class NavMeshPath
{
    private Float3[] _corners = [];
    private int _count;

    public NavMeshPathStatus Status { get; internal set; } = NavMeshPathStatus.PathInvalid;

    public int CornerCount => _count;

    public ReadOnlySpan<Float3> Corners => _corners.AsSpan(0, _count);

    public Float3 this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _corners[index];
        }
    }

    public void Clear()
    {
        _count = 0;
        Status = NavMeshPathStatus.PathInvalid;
    }

    internal void SetCorners(ReadOnlySpan<Float3> corners, NavMeshPathStatus status)
    {
        if (_corners.Length < corners.Length)
            _corners = new Float3[corners.Length];
        corners.CopyTo(_corners);
        _count = corners.Length;
        Status = status;
    }
}

public struct NavMeshHit
{
    public Float3 Position;
    public Float3 Normal;
    public float Distance;
    public bool Hit;
}
