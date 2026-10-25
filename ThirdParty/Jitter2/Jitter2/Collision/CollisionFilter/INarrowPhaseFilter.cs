/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

namespace Jitter2.Collision;

/// <summary>
/// Provides a hook into the narrowphase collision detection pipeline.
/// </summary>
/// <remarks>
/// Implement this interface to intercept collisions after contact generation.
/// This can be used to modify contact data, implement custom collision responses,
/// or filter out specific collisions.
/// </remarks>
public interface INarrowPhaseFilter
{
    /// <summary>
    /// Called for each detected collision with its contact data.
    /// </summary>
    /// <param name="shapeA">The first shape in the collision.</param>
    /// <param name="shapeB">The second shape in the collision.</param>
    /// <param name="pointA">Contact point on shape A (modifiable).</param>
    /// <param name="pointB">Contact point on shape B (modifiable).</param>
    /// <param name="normal">Collision normal from B to A (modifiable).</param>
    /// <param name="penetration">Penetration depth (modifiable).</param>
    /// <returns><c>true</c> to keep the collision; <c>false</c> to discard it.</returns>
    [CallbackThread(ThreadContext.Any)]
    bool Filter(RigidBodyShape shapeA, RigidBodyShape shapeB,
        ref JVector pointA, ref JVector pointB,
        ref JVector normal, ref Real penetration);

    /// <summary>
    /// Called for each detected collision with its contact data, plus the index of the worker
    /// running the callback.
    /// </summary>
    /// <param name="shapeA">The first shape in the collision.</param>
    /// <param name="shapeB">The second shape in the collision.</param>
    /// <param name="pointA">Contact point on shape A (modifiable).</param>
    /// <param name="pointB">Contact point on shape B (modifiable).</param>
    /// <param name="normal">Collision normal from B to A (modifiable).</param>
    /// <param name="penetration">Penetration depth (modifiable). Negative for speculative contacts.</param>
    /// <param name="workerIndex">Index of the worker running this callback, in
    /// <c>[0, <see cref="Parallelization.ThreadPool.ThreadCount"/>)</c>. Use it to index per-worker
    /// scratch storage without synchronization.</param>
    /// <returns><c>true</c> to keep the collision; <c>false</c> to discard it.</returns>
    /// <remarks>
    /// XENGINE: this is the overload the world invokes. It forwards to the worker-agnostic
    /// <see cref="Filter(RigidBodyShape, RigidBodyShape, ref JVector, ref JVector, ref JVector, ref Real)"/>
    /// by default, so filters that do not need per-worker state implement only that one.
    /// </remarks>
    [CallbackThread(ThreadContext.Any)]
    bool Filter(RigidBodyShape shapeA, RigidBodyShape shapeB,
        ref JVector pointA, ref JVector pointB,
        ref JVector normal, ref Real penetration, int workerIndex)
        => Filter(shapeA, shapeB, ref pointA, ref pointB, ref normal, ref penetration);
}
