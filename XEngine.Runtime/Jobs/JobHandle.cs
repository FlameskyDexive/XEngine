// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Jobs;

/// <summary>
/// A ticket for work scheduled on <see cref="JobSystem"/>. Handles are cheap value types; the
/// batch they point at is pooled and stamped with a version, so completing (or querying) a stale
/// handle after the batch was recycled is a safe no-op rather than a corruption.
/// </summary>
public readonly struct JobHandle : IEquatable<JobHandle>
{
    internal readonly JobBatch? Batch;
    internal readonly int Version;

    internal JobHandle(JobBatch batch, int version)
    {
        Batch = batch;
        Version = version;
    }

    /// <summary>A handle to nothing. Completing it is a no-op and it is always complete.</summary>
    public static JobHandle Null => default;

    /// <summary>False only while the batch this handle refers to still has work outstanding.</summary>
    public bool IsComplete => Batch == null || Batch.IsCompleted(Version);

    /// <summary>
    /// Blocks the calling thread until the batch has finished, helping to run its remaining
    /// chunks instead of idling. Rethrows the first worker exception on the caller.
    /// Idempotent: a handle whose batch was already retired returns immediately.
    /// </summary>
    public void Complete() => JobSystem.Complete(this);

    /// <summary>
    /// A handle that is complete once both inputs are. Returns the non-null one when either side
    /// is <see cref="Null"/>, so callers can chain without special-casing empty dependencies.
    /// </summary>
    public static JobHandle CombineDependencies(JobHandle a, JobHandle b)
    {
        if (a.Batch == null) return b;
        if (b.Batch == null) return a;
        return JobSystem.Combine(a, b);
    }

    public bool Equals(JobHandle other) => ReferenceEquals(Batch, other.Batch) && Version == other.Version;

    public override bool Equals(object? obj) => obj is JobHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Batch?.GetHashCode() ?? 0, Version);

    public static bool operator ==(JobHandle left, JobHandle right) => left.Equals(right);

    public static bool operator !=(JobHandle left, JobHandle right) => !left.Equals(right);
}
