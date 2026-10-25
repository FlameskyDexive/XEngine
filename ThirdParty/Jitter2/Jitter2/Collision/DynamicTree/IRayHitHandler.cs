/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using Jitter2.LinearMath;

namespace Jitter2.Collision;

/// <summary>
/// Receives ray hits from <see cref="DynamicTree.RayCast{THandler}"/> without allocating.
/// </summary>
/// <remarks>
/// XENGINE: modelled on BEPUphysics v2's <c>IRayHitHandler</c>. The handler is a struct passed by
/// <c>ref</c>, so the JIT specialises the traversal for it and there is no delegate or boxing on
/// the per-proxy path. <see cref="OnHit"/> may shrink <c>maxLambda</c> to prune the rest of the
/// tree (closest-hit) or leave it alone (collect-all).
/// </remarks>
public interface IRayHitHandler
{
    /// <summary>
    /// Called before the exact ray test against <paramref name="proxy"/>.
    /// </summary>
    /// <returns><c>false</c> to skip this proxy.</returns>
    bool AllowTest(IDynamicTreeProxy proxy);

    /// <summary>
    /// Called when the ray hits <paramref name="proxy"/> at <paramref name="hitLambda"/>, which is
    /// already known to be strictly less than <paramref name="maxLambda"/>.
    /// </summary>
    /// <param name="maxLambda">Current search bound. Assign a smaller value to prune farther
    /// branches; leave it unchanged to keep collecting hits out to the original bound.</param>
    void OnHit(IDynamicTreeProxy proxy, in JVector normal, Real hitLambda, ref Real maxLambda);
}

/// <summary>
/// Receives sweep hits from <see cref="DynamicTree.SweepCast{T, THandler}"/> without allocating.
/// </summary>
/// <remarks>XENGINE: the sweep counterpart of <see cref="IRayHitHandler"/>.</remarks>
public interface ISweepHitHandler
{
    /// <summary>
    /// Called before the exact sweep test against <paramref name="proxy"/>.
    /// </summary>
    /// <returns><c>false</c> to skip this proxy.</returns>
    bool AllowTest(IDynamicTreeProxy proxy);

    /// <summary>
    /// Called when the sweep hits <paramref name="proxy"/> at <paramref name="hitLambda"/>, which is
    /// already known to be at most <paramref name="maxLambda"/>.
    /// </summary>
    /// <param name="maxLambda">Current search bound, in units of the sweep direction vector.</param>
    void OnHit(IDynamicTreeProxy proxy, in JVector pointA, in JVector pointB, in JVector normal,
        Real hitLambda, ref Real maxLambda);
}

/// <summary>
/// Receives nearest-shape results from <see cref="DynamicTree.FindNearest{T, THandler}"/> without allocating.
/// </summary>
/// <remarks>XENGINE: the distance counterpart of <see cref="IRayHitHandler"/>.</remarks>
public interface INearestHitHandler
{
    /// <summary>
    /// Called before the exact distance test against <paramref name="proxy"/>.
    /// </summary>
    /// <returns><c>false</c> to skip this proxy.</returns>
    bool AllowTest(IDynamicTreeProxy proxy);

    /// <summary>
    /// Called when <paramref name="proxy"/> is no farther than <paramref name="maxDistance"/>.
    /// </summary>
    /// <param name="hitDistance">Zero when the shapes overlap.</param>
    /// <param name="maxDistance">Current search bound. Assign a smaller value to prune farther
    /// branches.</param>
    void OnHit(IDynamicTreeProxy proxy, in JVector pointA, in JVector pointB, in JVector normal,
        Real hitDistance, ref Real maxDistance);
}
