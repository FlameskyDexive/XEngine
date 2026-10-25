/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Generic;
using Jitter2.LinearMath;
using Jitter2.Collision.Shapes;

namespace Jitter2.Collision;

public partial class DynamicTree
{
    /// <summary>
    /// Represents the result of a sweep against the dynamic tree.
    /// </summary>
    public struct SweepCastResult
    {
        /// <summary>The proxy that was hit.</summary>
        public IDynamicTreeProxy? Entity;

        /// <summary>The collision point on the moving query shape in world space at the sweep origin. Undefined when the shapes already overlap.</summary>
        public JVector PointA;

        /// <summary>The collision point on the hit object in world space at the sweep origin. Undefined when the shapes already overlap.</summary>
        public JVector PointB;

        /// <summary>
        /// The collision normal in world space, or <see cref="JVector.Zero"/> if the shapes already overlap.
        /// Do not use this to test whether a hit occurred.
        /// </summary>
        public JVector Normal;

        /// <summary>The time of impact expressed in units of the sweep direction vector. Zero if the shapes already overlap.</summary>
        public Real Lambda;
    }

    /// <summary>
    /// Delegate for filtering sweep results after the exact shape sweep test.
    /// </summary>
    /// <param name="result">The sweep result to evaluate.</param>
    /// <returns><c>false</c> to filter out this hit; <c>true</c> to keep it.</returns>
    public delegate bool SweepCastFilterPost(SweepCastResult result);

    /// <summary>
    /// Delegate for filtering sweep candidates before the exact shape sweep test.
    /// </summary>
    /// <param name="proxy">The proxy to evaluate.</param>
    /// <returns><c>false</c> to skip this proxy; <c>true</c> to test it.</returns>
    public delegate bool SweepCastFilterPre(IDynamicTreeProxy proxy);

    /// <summary>
    /// Convenience overload of <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that sweeps a <see cref="SupportPrimitives.Sphere"/>.
    /// </summary>
    /// <param name="radius">The sphere radius.</param>
    public bool SweepCastSphere(Real radius, in JVector position, in JVector direction,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda) =>
        SweepCast(SupportPrimitives.CreateSphere(radius), JQuaternion.Identity, position, direction, pre,
            post, out proxy, out pointA, out pointB, out normal, out lambda);

    /// <summary>
    /// Convenience overload of <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, Real, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that sweeps a <see cref="SupportPrimitives.Sphere"/>.
    /// </summary>
    /// <param name="radius">The sphere radius.</param>
    public bool SweepCastSphere(Real radius, in JVector position, in JVector direction, Real maxLambda,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda) =>
        SweepCast(SupportPrimitives.CreateSphere(radius), JQuaternion.Identity, position, direction, maxLambda, pre,
            post, out proxy, out pointA, out pointB, out normal, out lambda);

    /// <summary>
    /// Convenience overload of <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that sweeps a <see cref="SupportPrimitives.Box"/>.
    /// </summary>
    /// <param name="halfExtents">The half extents of the box.</param>
    public bool SweepCastBox(in JVector halfExtents, in JQuaternion orientation, in JVector position, in JVector direction,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda) =>
        SweepCast(SupportPrimitives.CreateBox(halfExtents), orientation, position, direction, pre,
            post, out proxy, out pointA, out pointB, out normal, out lambda);

    /// <summary>
    /// Convenience overload of <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, Real, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that sweeps a <see cref="SupportPrimitives.Box"/>.
    /// </summary>
    /// <param name="halfExtents">The half extents of the box.</param>
    public bool SweepCastBox(in JVector halfExtents, in JQuaternion orientation, in JVector position, in JVector direction, Real maxLambda,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda) =>
        SweepCast(SupportPrimitives.CreateBox(halfExtents), orientation, position, direction, maxLambda, pre,
            post, out proxy, out pointA, out pointB, out normal, out lambda);

    /// <summary>
    /// Convenience overload of <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that sweeps a <see cref="SupportPrimitives.Capsule"/>.
    /// </summary>
    /// <param name="radius">The capsule radius.</param>
    /// <param name="halfLength">Half the cylindrical section length of the capsule.</param>
    public bool SweepCastCapsule(Real radius, Real halfLength, in JQuaternion orientation, in JVector position, in JVector direction,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda) =>
        SweepCast(SupportPrimitives.CreateCapsule(radius, halfLength), orientation, position, direction, pre,
            post, out proxy, out pointA, out pointB, out normal, out lambda);

    /// <summary>
    /// Convenience overload of <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, Real, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that sweeps a <see cref="SupportPrimitives.Capsule"/>.
    /// </summary>
    /// <param name="radius">The capsule radius.</param>
    /// <param name="halfLength">Half the cylindrical section length of the capsule.</param>
    public bool SweepCastCapsule(Real radius, Real halfLength, in JQuaternion orientation, in JVector position, in JVector direction, Real maxLambda,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda) =>
        SweepCast(SupportPrimitives.CreateCapsule(radius, halfLength), orientation, position, direction, maxLambda, pre,
            post, out proxy, out pointA, out pointB, out normal, out lambda);

    /// <summary>
    /// Convenience overload of <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that sweeps a <see cref="SupportPrimitives.Cylinder"/>.
    /// </summary>
    /// <param name="radius">The cylinder radius.</param>
    /// <param name="halfHeight">Half the cylinder height.</param>
    public bool SweepCastCylinder(Real radius, Real halfHeight, in JQuaternion orientation, in JVector position, in JVector direction,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda) =>
        SweepCast(SupportPrimitives.CreateCylinder(radius, halfHeight), orientation, position, direction, pre,
            post, out proxy, out pointA, out pointB, out normal, out lambda);

    /// <summary>
    /// Convenience overload of <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, Real, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that sweeps a <see cref="SupportPrimitives.Cylinder"/>.
    /// </summary>
    /// <param name="radius">The cylinder radius.</param>
    /// <param name="halfHeight">Half the cylinder height.</param>
    public bool SweepCastCylinder(Real radius, Real halfHeight, in JQuaternion orientation, in JVector position, in JVector direction, Real maxLambda,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda) =>
        SweepCast(SupportPrimitives.CreateCylinder(radius, halfHeight), orientation, position, direction, maxLambda, pre,
            post, out proxy, out pointA, out pointB, out normal, out lambda);

    /// <summary>
    /// Sweeps a support-mapped query shape against all <see cref="ISweepTestable"/> proxies in the tree
    /// and returns the closest exact hit.
    /// </summary>
    /// <param name="support">The query shape.</param>
    /// <param name="orientation">The query shape orientation in world space.</param>
    /// <param name="position">The query shape position in world space at the start of the sweep.</param>
    /// <param name="direction">The sweep direction vector in world space.</param>
    /// <param name="pre">Optional pre-filter that can skip candidate proxies before the exact sweep test.</param>
    /// <param name="post">Optional post-filter that can reject exact results and continue the search.</param>
    /// <param name="proxy">The closest accepted hit proxy, or <c>null</c> if no hit is found.</param>
    /// <param name="pointA">Collision point on the query shape in world space at the sweep origin. Undefined when the shapes already overlap.</param>
    /// <param name="pointB">Collision point on the hit proxy in world space at the sweep origin. Undefined when the shapes already overlap.</param>
    /// <param name="normal">Collision normal in world space, or <see cref="JVector.Zero"/> when the shapes already overlap.</param>
    /// <param name="lambda">Time of impact in units of <paramref name="direction"/>. Zero when the shapes already overlap.</param>
    /// <returns>
    /// <c>true</c> if an accepted hit is found. This includes the overlapping case, where <paramref name="lambda"/>
    /// is zero. <c>false</c> if no accepted hit is found, in which case <paramref name="proxy"/> is <c>null</c>.
    /// </returns>
    public bool SweepCast<T>(in T support, in JQuaternion orientation, in JVector position, in JVector direction,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda)
        where T : ISupportMappable
        => SweepCast(support, orientation, position, direction, Real.MaxValue, pre, post,
            out proxy, out pointA, out pointB, out normal, out lambda);

    /// <summary>
    /// Bounded variant of <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that limits the sweep to <paramref name="maxLambda"/>.
    /// </summary>
    /// <param name="maxLambda">Maximum sweep parameter to consider along <paramref name="direction"/>.</param>
    public bool SweepCast<T>(in T support, in JQuaternion orientation, in JVector position, in JVector direction, Real maxLambda,
        SweepCastFilterPre? pre, SweepCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real lambda)
        where T : ISupportMappable
    {
        var handler = new DelegateSweepHitHandler { FilterPre = pre, FilterPost = post };
        Real bound = maxLambda;
        SweepCast(support, orientation, position, direction, ref bound, ref handler);
        proxy = handler.Best.Entity;
        pointA = handler.Best.PointA;
        pointB = handler.Best.PointB;
        normal = handler.Best.Normal;
        lambda = handler.Hit ? handler.Best.Lambda : maxLambda;
        return handler.Hit;
    }

    /// <summary>
    /// Sweeps a support-mapped query shape against the tree, reporting every accepted hit to
    /// <paramref name="handler"/>.
    /// </summary>
    /// <param name="maxLambda">Search bound in units of <paramref name="direction"/>. The handler may
    /// shrink this as hits are found so later branches can be pruned.</param>
    /// <remarks>
    /// XENGINE: struct-generic counterpart of the delegate overloads. A closest-hit handler that
    /// writes the hit lambda back into <paramref name="maxLambda"/> gets the same AABB pruning as
    /// the original bounded <see cref="SweepCast{T}(in T, in JQuaternion, in JVector, in JVector, Real, SweepCastFilterPre?, SweepCastFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>.
    /// </remarks>
    public void SweepCast<T, THandler>(in T support, in JQuaternion orientation, in JVector position,
        in JVector direction, ref Real maxLambda, ref THandler handler)
        where T : ISupportMappable
        where THandler : struct, ISweepHitHandler
    {
        if (root == NullNode) return;

        ShapeHelper.CalculateBoundingBox(support, orientation, position, out JBoundingBox box);

        Stack<int> stack = QueryStack;
        int baseCount = stack.Count;
        stack.Push(root);

        while (stack.Count > baseCount)
        {
            int index = stack.Pop();
            ref Node node = ref nodes[index];

            if (node.IsLeaf)
            {
                if (node.Proxy is not ISweepTestable sweepCastable) continue;
                if (!handler.AllowTest(node.Proxy!)) continue;

                bool hit = sweepCastable.Sweep(support,
                    orientation, position, direction,
                    out JVector pointA, out JVector pointB, out JVector normal, out Real lambda);

                if (!hit || lambda > maxLambda) continue;

                handler.OnHit(node.Proxy!, in pointA, in pointB, in normal, lambda, ref maxLambda);
                continue;
            }

            ref Node leftNode = ref nodes[node.Left];
            ref Node rightNode = ref nodes[node.Right];

            bool leftHit = SweepBox(box, direction, leftNode.ExpandedBox, out Real leftEnter);
            bool rightHit = SweepBox(box, direction, rightNode.ExpandedBox, out Real rightEnter);

            if (leftEnter > maxLambda) leftHit = false;
            if (rightEnter > maxLambda) rightHit = false;

            if (leftHit && rightHit)
            {
                if (leftEnter < rightEnter)
                {
                    stack.Push(node.Right);
                    stack.Push(node.Left);
                }
                else
                {
                    stack.Push(node.Left);
                    stack.Push(node.Right);
                }
            }
            else
            {
                if (leftHit) stack.Push(node.Left);
                if (rightHit) stack.Push(node.Right);
            }
        }
    }

    private static bool SweepBox(in JBoundingBox movingBox, in JVector translation, in TreeBox targetBox, out Real enter)
    {
        JVector extents = (movingBox.Max - movingBox.Min) * (Real)0.5;
        JVector center = (movingBox.Max + movingBox.Min) * (Real)0.5;

        JBoundingBox expanded = new(targetBox.Min - extents, targetBox.Max + extents);
        return expanded.RayIntersect(center, translation, out enter);
    }

    // XENGINE: adapts the original delegate filters to ISweepHitHandler so the public delegate
    // overloads share the generic traversal.
    private struct DelegateSweepHitHandler : ISweepHitHandler
    {
        public SweepCastFilterPre? FilterPre;
        public SweepCastFilterPost? FilterPost;
        public SweepCastResult Best;
        public bool Hit;

        public bool AllowTest(IDynamicTreeProxy proxy) => FilterPre == null || FilterPre(proxy);

        public void OnHit(IDynamicTreeProxy proxy, in JVector pointA, in JVector pointB, in JVector normal,
            Real hitLambda, ref Real maxLambda)
        {
            var res = new SweepCastResult
            {
                Entity = proxy,
                PointA = pointA,
                PointB = pointB,
                Normal = normal,
                Lambda = hitLambda
            };
            if (FilterPost != null && !FilterPost(res)) return;
            Best = res;
            Hit = true;
            maxLambda = hitLambda;
        }
    }
}
