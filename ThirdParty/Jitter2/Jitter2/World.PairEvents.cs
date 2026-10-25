/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

// XENGINE: sensor overlap tracking. Sensors detect overlaps without generating contacts, so the
// solver never sees them and there is no arbiter whose lifetime could stand in for the overlap.
// This file owns the pair bookkeeping that produces begin/stay/end transitions instead.

using System;
using System.Collections.Generic;
using Jitter2.Collision.Shapes;
using Jitter2.DataStructures;
using Jitter2.LinearMath;
using Jitter2.Parallelization;

namespace Jitter2;

/// <summary>
/// An unordered pair of shapes, at least one of which is a sensor.
/// </summary>
/// <remarks>
/// XENGINE: <see cref="ShapeA"/> and <see cref="ShapeB"/> are ordered by
/// <see cref="Shape.ShapeId"/>, so a given pair of shapes always yields an equal value regardless
/// of the order the broad phase happened to report them in.
/// </remarks>
public readonly struct SensorOverlap : IEquatable<SensorOverlap>
{
    /// <summary>The shape with the lower <see cref="Shape.ShapeId"/>.</summary>
    public readonly RigidBodyShape ShapeA;

    /// <summary>The shape with the higher <see cref="Shape.ShapeId"/>.</summary>
    public readonly RigidBodyShape ShapeB;

    internal SensorOverlap(RigidBodyShape shapeA, RigidBodyShape shapeB)
    {
        if (shapeB.ShapeId < shapeA.ShapeId)
        {
            (shapeA, shapeB) = (shapeB, shapeA);
        }

        ShapeA = shapeA;
        ShapeB = shapeB;
    }

    /// <summary>
    /// Gets the other shape of the pair, given one of them.
    /// </summary>
    /// <returns>Null if <paramref name="shape"/> is not part of this pair.</returns>
    public RigidBodyShape? Other(RigidBodyShape shape)
    {
        if (ReferenceEquals(shape, ShapeA)) return ShapeB;
        if (ReferenceEquals(shape, ShapeB)) return ShapeA;
        return null;
    }

    /// <inheritdoc/>
    public bool Equals(SensorOverlap other)
        => ReferenceEquals(ShapeA, other.ShapeA) && ReferenceEquals(ShapeB, other.ShapeB);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SensorOverlap other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ShapeA.ShapeId, ShapeB.ShapeId);

    /// <summary>Compares two pairs for equality.</summary>
    public static bool operator ==(SensorOverlap left, SensorOverlap right) => left.Equals(right);

    /// <summary>Compares two pairs for inequality.</summary>
    public static bool operator !=(SensorOverlap left, SensorOverlap right) => !left.Equals(right);
}

public partial class World
{
    /// <summary>
    /// Per-worker append buffers for overlaps observed during the narrow phase. Sized to the worker
    /// count so each worker owns one and no synchronization is needed.
    /// </summary>
    private SlimBag<SensorOverlap>[] workerSensorOverlaps = [];

    private readonly Dictionary<SensorOverlap, bool> sensorOverlapFresh = new();
    private readonly List<SensorOverlap> sensorOverlapsBegan = [];
    private readonly List<SensorOverlap> sensorOverlapsEnded = [];
    private readonly List<SensorOverlap> sensorSweepScratch = [];

    /// <summary>
    /// The sensor overlaps that exist as of the last completed <see cref="Step"/>.
    /// </summary>
    /// <remarks>
    /// XENGINE: an overlap is present from the step that first detected it until the step that first
    /// failed to, which makes this the "stay" set. Enumerate it between steps only; the narrow phase
    /// mutates the underlying storage.
    /// </remarks>
    public Dictionary<SensorOverlap, bool>.KeyCollection SensorOverlaps => sensorOverlapFresh.Keys;

    /// <summary>
    /// The overlaps that began during the last completed <see cref="Step"/>.
    /// </summary>
    /// <remarks>XENGINE: ordered by shape id, so the sequence does not depend on thread scheduling.</remarks>
    public ReadOnlySpan<SensorOverlap> SensorOverlapsBegan => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sensorOverlapsBegan);

    /// <summary>
    /// The overlaps that ended during the last completed <see cref="Step"/>.
    /// </summary>
    /// <remarks>
    /// XENGINE: an overlap ends when the shapes separate, when either shape leaves the world, or when
    /// a sensor stops being one. Ordered by shape id.
    /// </remarks>
    public ReadOnlySpan<SensorOverlap> SensorOverlapsEnded => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sensorOverlapsEnded);

    /// <summary>
    /// Records an overlap found by the narrow phase. Called concurrently, once per worker.
    /// </summary>
    private void RecordSensorOverlap(RigidBodyShape shapeA, RigidBodyShape shapeB, int workerIndex)
    {
        // The pool can grow between steps; a stale index would corrupt another worker's buffer.
        if ((uint)workerIndex >= (uint)workerSensorOverlaps.Length) return;

        workerSensorOverlaps[workerIndex].Add(new SensorOverlap(shapeA, shapeB));
    }

    /// <summary>
    /// Prepares the freshness state for a step. Runs single-threaded before the narrow phase.
    /// </summary>
    /// <remarks>
    /// Sleeping bodies are skipped by the narrow phase, so an overlap between two of them would look
    /// stale and be reported as ended. Following BEPU's contact-event bookkeeping, such pairs are
    /// pre-marked fresh so the sweep keeps them. The bounding-box test is what makes this safe: a
    /// sensor that is moved away from a sleeping body separates their boxes, and the overlap is then
    /// allowed to end even though neither body woke up.
    /// </remarks>
    private void PrepareSensorOverlaps()
    {
        sensorOverlapsBegan.Clear();
        sensorOverlapsEnded.Clear();

        int workerCount = ThreadPool.InstanceInitialized ? ThreadPool.Instance.ThreadCount : 1;
        if (workerSensorOverlaps.Length != workerCount)
        {
            var resized = new SlimBag<SensorOverlap>[workerCount];
            for (int i = 0; i < workerCount; i++)
                resized[i] = i < workerSensorOverlaps.Length ? workerSensorOverlaps[i] : [];
            workerSensorOverlaps = resized;
        }

        for (int i = 0; i < workerSensorOverlaps.Length; i++)
            workerSensorOverlaps[i].Clear();

        if (sensorOverlapFresh.Count == 0) return;

        sensorSweepScratch.Clear();
        foreach (var pair in sensorOverlapFresh.Keys)
            sensorSweepScratch.Add(pair);

        foreach (var pair in sensorSweepScratch)
        {
            // A shape removed from the world has no body left to query, and cannot overlap anything
            // either. Leave the pair stale so the flush ends it.
            if (!pair.ShapeA.IsRegistered || !pair.ShapeB.IsRegistered)
            {
                sensorOverlapFresh[pair] = false;
                continue;
            }

            bool bothAsleep = !pair.ShapeA.RigidBody.Data.IsActive && !pair.ShapeB.RigidBody.Data.IsActive;
            bool stillNearby = bothAsleep &&
                               !JBoundingBox.Disjoint(pair.ShapeA.WorldBoundingBox, pair.ShapeB.WorldBoundingBox);

            sensorOverlapFresh[pair] = stillNearby;
        }
    }

    /// <summary>
    /// Folds the per-worker buffers into the tracked set and derives the begin/end lists. Runs
    /// single-threaded after the narrow phase.
    /// </summary>
    private void FlushSensorOverlaps()
    {
        for (int i = 0; i < workerSensorOverlaps.Length; i++)
        {
            foreach (var pair in workerSensorOverlaps[i].AsSpan())
            {
                // Both shapes can report the same pair when both are sensors, and a pair can be
                // pre-marked fresh, so only a genuinely new key counts as a begin.
                if (sensorOverlapFresh.TryGetValue(pair, out bool wasFresh))
                {
                    if (!wasFresh) sensorOverlapFresh[pair] = true;
                    continue;
                }

                sensorOverlapFresh.Add(pair, true);
                sensorOverlapsBegan.Add(pair);
            }

            workerSensorOverlaps[i].Clear();
        }

        if (sensorOverlapFresh.Count > 0)
        {
            sensorSweepScratch.Clear();
            foreach (var entry in sensorOverlapFresh)
            {
                // A shape that left the world can no longer be tested, so its overlaps end here.
                if (!entry.Value || !entry.Key.ShapeA.IsRegistered || !entry.Key.ShapeB.IsRegistered)
                    sensorSweepScratch.Add(entry.Key);
            }

            foreach (var pair in sensorSweepScratch)
            {
                sensorOverlapFresh.Remove(pair);
                sensorOverlapsEnded.Add(pair);
            }
        }

        // Worker order and dictionary order both depend on scheduling and insertion history, so sort
        // to give consumers a stable sequence across runs.
        sensorOverlapsBegan.Sort(CompareOverlaps);
        sensorOverlapsEnded.Sort(CompareOverlaps);
    }

    private static int CompareOverlaps(SensorOverlap x, SensorOverlap y)
    {
        int result = x.ShapeA.ShapeId.CompareTo(y.ShapeA.ShapeId);
        return result != 0 ? result : x.ShapeB.ShapeId.CompareTo(y.ShapeB.ShapeId);
    }

    private void ClearSensorOverlaps()
    {
        sensorOverlapFresh.Clear();
        sensorOverlapsBegan.Clear();
        sensorOverlapsEnded.Clear();
        sensorSweepScratch.Clear();

        for (int i = 0; i < workerSensorOverlaps.Length; i++)
            workerSensorOverlaps[i].Clear();
    }
}
