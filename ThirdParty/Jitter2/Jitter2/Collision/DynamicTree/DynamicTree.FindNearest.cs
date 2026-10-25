/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Generic;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

namespace Jitter2.Collision;

public partial class DynamicTree
{
    /// <summary>
    /// Represents the result of a distance query against the dynamic tree.
    /// </summary>
    public struct FindNearestResult
    {
        /// <summary>The closest proxy found.</summary>
        public IDynamicTreeProxy? Entity;

        /// <summary>Closest point on the query shape in world space. Undefined when the shapes overlap.</summary>
        public JVector PointA;

        /// <summary>Closest point on the found proxy in world space. Undefined when the shapes overlap.</summary>
        public JVector PointB;

        /// <summary>
        /// Unit direction from the query shape toward the found proxy, or <see cref="JVector.Zero"/>
        /// when the shapes overlap. Do not use this to test whether a result was found.
        /// </summary>
        public JVector Normal;

        /// <summary>The separation distance between the query shape and the found proxy. Zero when the shapes overlap.</summary>
        public Real Distance;
    }

    /// <summary>
    /// Delegate for filtering distance query results after the exact shape distance test.
    /// </summary>
    /// <param name="result">The distance result to evaluate.</param>
    /// <returns><c>false</c> to filter out this result; <c>true</c> to keep it.</returns>
    public delegate bool FindNearestFilterPost(FindNearestResult result);

    /// <summary>
    /// Delegate for filtering distance query candidates before the exact shape distance test.
    /// </summary>
    /// <param name="proxy">The proxy to evaluate.</param>
    /// <returns><c>false</c> to skip this proxy; <c>true</c> to test it.</returns>
    public delegate bool FindNearestFilterPre(IDynamicTreeProxy proxy);

    /// <summary>
    /// Convenience overload of <see cref="FindNearest{T}(in T, in JQuaternion, in JVector, FindNearestFilterPre?, FindNearestFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that queries with a <see cref="SupportPrimitives.Point"/>.
    /// </summary>
    public bool FindNearestPoint(in JVector position,
        FindNearestFilterPre? pre, FindNearestFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real distance) =>
        FindNearest(SupportPrimitives.CreatePoint(), JQuaternion.Identity, position, pre, post,
            out proxy, out pointA, out pointB, out normal, out distance);

    /// <summary>
    /// Convenience overload of <see cref="FindNearest{T}(in T, in JQuaternion, in JVector, Real, FindNearestFilterPre?, FindNearestFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that queries with a <see cref="SupportPrimitives.Point"/>.
    /// </summary>
    public bool FindNearestPoint(in JVector position, Real maxDistance,
        FindNearestFilterPre? pre, FindNearestFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real distance) =>
        FindNearest(SupportPrimitives.CreatePoint(), JQuaternion.Identity, position, maxDistance, pre, post,
            out proxy, out pointA, out pointB, out normal, out distance);

    /// <summary>
    /// Convenience overload of <see cref="FindNearest{T}(in T, in JQuaternion, in JVector, FindNearestFilterPre?, FindNearestFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that queries with a <see cref="SupportPrimitives.Sphere"/>.
    /// </summary>
    /// <param name="radius">The sphere radius.</param>
    public bool FindNearestSphere(Real radius, in JVector position,
        FindNearestFilterPre? pre, FindNearestFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real distance) =>
        FindNearest(SupportPrimitives.CreateSphere(radius), JQuaternion.Identity, position, pre, post,
            out proxy, out pointA, out pointB, out normal, out distance);

    /// <summary>
    /// Convenience overload of <see cref="FindNearest{T}(in T, in JQuaternion, in JVector, Real, FindNearestFilterPre?, FindNearestFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that queries with a <see cref="SupportPrimitives.Sphere"/>.
    /// </summary>
    /// <param name="radius">The sphere radius.</param>
    public bool FindNearestSphere(Real radius, in JVector position, Real maxDistance,
        FindNearestFilterPre? pre, FindNearestFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real distance) =>
        FindNearest(SupportPrimitives.CreateSphere(radius), JQuaternion.Identity, position, maxDistance, pre, post,
            out proxy, out pointA, out pointB, out normal, out distance);

    /// <summary>
    /// Finds the closest <see cref="IDistanceTestable"/> proxy in the tree to the query shape
    /// and returns the exact closest points.
    /// </summary>
    /// <param name="support">The query shape.</param>
    /// <param name="orientation">The query shape orientation in world space.</param>
    /// <param name="position">The query shape position in world space.</param>
    /// <param name="pre">Optional pre-filter that can skip candidate proxies before the exact distance test.</param>
    /// <param name="post">Optional post-filter that can reject exact results and continue the search.</param>
    /// <param name="proxy">The nearest accepted proxy, or <c>null</c> if no proxy is found.</param>
    /// <param name="pointA">Closest point on the query shape in world space. Undefined when the shapes overlap.</param>
    /// <param name="pointB">Closest point on the found proxy in world space. Undefined when the shapes overlap.</param>
    /// <param name="normal">Unit direction from the query shape toward the found proxy, or <see cref="JVector.Zero"/> when the shapes overlap.</param>
    /// <param name="distance">Separation distance between the query shape and the found proxy. Zero when they overlap.</param>
    /// <returns>
    /// <c>true</c> if an accepted proxy is found. This includes the overlapping case, where <paramref name="distance"/>
    /// is zero. <c>false</c> if no accepted proxy is found, in which case <paramref name="proxy"/> is <c>null</c>.
    /// </returns>
    /// <remarks>
    /// To skip overlapping proxies and continue searching for separated ones, use a <paramref name="post"/>
    /// filter that returns <c>false</c> for results with <c>distance == 0</c>.
    /// </remarks>
    public bool FindNearest<T>(in T support, in JQuaternion orientation, in JVector position,
        FindNearestFilterPre? pre, FindNearestFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real distance)
        where T : ISupportMappable
        => FindNearest(support, orientation, position, Real.MaxValue, pre, post,
            out proxy, out pointA, out pointB, out normal, out distance);

    /// <summary>
    /// Bounded variant of <see cref="FindNearest{T}(in T, in JQuaternion, in JVector, FindNearestFilterPre?, FindNearestFilterPost?, out IDynamicTreeProxy?, out JVector, out JVector, out JVector, out Real)"/>
    /// that limits the search to <paramref name="maxDistance"/>.
    /// </summary>
    /// <param name="maxDistance">Maximum separation distance to consider. Proxies farther than this are ignored.</param>
    public bool FindNearest<T>(in T support, in JQuaternion orientation, in JVector position, Real maxDistance,
        FindNearestFilterPre? pre, FindNearestFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector pointA, out JVector pointB, out JVector normal, out Real distance)
        where T : ISupportMappable
    {
        var handler = new DelegateNearestHitHandler { FilterPre = pre, FilterPost = post };
        Real bound = maxDistance;
        FindNearest(support, orientation, position, ref bound, ref handler);
        proxy = handler.Best.Entity;
        pointA = handler.Best.PointA;
        pointB = handler.Best.PointB;
        normal = handler.Best.Normal;
        distance = handler.Hit ? handler.Best.Distance : maxDistance;
        return handler.Hit;
    }

    /// <summary>
    /// Finds proxies near the query shape, reporting every accepted result to <paramref name="handler"/>.
    /// </summary>
    /// <param name="maxDistance">Search bound. The handler may shrink this as closer results are
    /// found so later branches can be pruned.</param>
    /// <remarks>
    /// XENGINE: struct-generic counterpart of the delegate overloads.
    /// </remarks>
    public void FindNearest<T, THandler>(in T support, in JQuaternion orientation, in JVector position,
        ref Real maxDistance, ref THandler handler)
        where T : ISupportMappable
        where THandler : struct, INearestHitHandler
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
                if (node.Proxy is not IDistanceTestable distCastable) continue;
                if (!handler.AllowTest(node.Proxy!)) continue;

                bool separated = distCastable.Distance(support,
                    orientation, position,
                    out JVector pointA, out JVector pointB, out JVector normal, out Real distance);

                if (!separated)
                {
                    distance = (Real)0.0;
                    normal = JVector.Zero;
                }

                if (distance > maxDistance) continue;

                handler.OnHit(node.Proxy!, in pointA, in pointB, in normal, distance, ref maxDistance);
                continue;
            }

            ref Node leftNode = ref nodes[node.Left];
            ref Node rightNode = ref nodes[node.Right];

            Real leftDist = MinDistBox(box, leftNode.ExpandedBox);
            Real rightDist = MinDistBox(box, rightNode.ExpandedBox);

            bool leftHit = leftDist <= maxDistance;
            bool rightHit = rightDist <= maxDistance;

            if (leftHit && rightHit)
            {
                if (leftDist < rightDist)
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

    // Returns the minimum distance between the query AABB and a tree node's expanded AABB.
    // Uses the Minkowski sum: expand the target by the query half-extents, then measure
    // point-to-AABB distance from the query center. Returns 0 if the AABBs overlap.
    private static Real MinDistBox(in JBoundingBox queryBox, in TreeBox targetBox)
    {
        JVector extents = (queryBox.Max - queryBox.Min) * (Real)0.5;
        JVector center = (queryBox.Max + queryBox.Min) * (Real)0.5;

        Real dx = MathR.Max(MathR.Max(targetBox.Min.X - extents.X - center.X, center.X - targetBox.Max.X - extents.X), (Real)0.0);
        Real dy = MathR.Max(MathR.Max(targetBox.Min.Y - extents.Y - center.Y, center.Y - targetBox.Max.Y - extents.Y), (Real)0.0);
        Real dz = MathR.Max(MathR.Max(targetBox.Min.Z - extents.Z - center.Z, center.Z - targetBox.Max.Z - extents.Z), (Real)0.0);

        return MathR.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // XENGINE: adapts the original delegate filters to INearestHitHandler so the public delegate
    // overloads share the generic traversal.
    private struct DelegateNearestHitHandler : INearestHitHandler
    {
        public FindNearestFilterPre? FilterPre;
        public FindNearestFilterPost? FilterPost;
        public FindNearestResult Best;
        public bool Hit;

        public bool AllowTest(IDynamicTreeProxy proxy) => FilterPre == null || FilterPre(proxy);

        public void OnHit(IDynamicTreeProxy proxy, in JVector pointA, in JVector pointB, in JVector normal,
            Real hitDistance, ref Real maxDistance)
        {
            var res = new FindNearestResult
            {
                Entity = proxy,
                PointA = pointA,
                PointB = pointB,
                Normal = normal,
                Distance = hitDistance
            };
            if (FilterPost != null && !FilterPost(res)) return;
            Best = res;
            Hit = true;
            maxDistance = hitDistance;
        }
    }
}
