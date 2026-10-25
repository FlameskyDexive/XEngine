/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Generic;
using Jitter2.LinearMath;

namespace Jitter2.Collision;

public partial class DynamicTree
{
    /// <summary>
    /// Represents the result of a ray cast against the dynamic tree.
    /// </summary>
    public struct RayCastResult
    {
        /// <summary>The proxy that was hit.</summary>
        public IDynamicTreeProxy? Entity;

        /// <summary>The ray parameter at the hit: <c>hitPoint = origin + Lambda * direction</c>.</summary>
        public Real Lambda;

        /// <summary>
        /// The surface normal at the hit point, or <see cref="JVector.Zero"/> when the ray origin
        /// is inside the hit shape. Do not use this to test whether a hit occurred.
        /// </summary>
        public JVector Normal;
    }

    /// <summary>
    /// Delegate for filtering ray cast results after the shape intersection test.
    /// </summary>
    /// <param name="result">The ray cast result to evaluate.</param>
    /// <returns><c>false</c> to filter out this hit; <c>true</c> to keep it.</returns>
    public delegate bool RayCastFilterPost(RayCastResult result);

    /// <summary>
    /// Delegate for filtering ray cast candidates before the shape intersection test.
    /// </summary>
    /// <param name="proxy">The proxy to evaluate.</param>
    /// <returns><c>false</c> to skip this proxy; <c>true</c> to test it.</returns>
    public delegate bool RayCastFilterPre(IDynamicTreeProxy proxy);

    /// <summary>
    /// Ray cast against the world.
    /// </summary>
    /// <param name="origin">Origin of the ray.</param>
    /// <param name="direction">Direction of the ray. Does not have to be normalized.</param>
    /// <param name="pre">Optional pre-filter which allows to skip shapes in the detection.</param>
    /// <param name="post">Optional post-filter which allows to skip detections.</param>
    /// <param name="proxy">The shape which was hit.</param>
    /// <param name="normal">
    /// The surface normal at the hit point. <see cref="JVector.Zero"/> if the ray does not hit,
    /// or if the ray origin is inside the hit shape. Use the return value to determine whether
    /// a hit occurred; do not rely on this being non-zero as a hit indicator.
    /// </param>
    /// <param name="lambda">Distance from the origin to the hit point in units of the ray direction. Zero if the origin is inside the hit shape.</param>
    /// <returns>True if the ray hits, false otherwise.</returns>
    public bool RayCast(JVector origin, JVector direction, RayCastFilterPre? pre, RayCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector normal, out Real lambda)
        => RayCast(origin, direction, Real.MaxValue, pre, post, out proxy, out normal, out lambda);

    /// <inheritdoc cref="RayCast(JVector, JVector, RayCastFilterPre?, RayCastFilterPost?, out IDynamicTreeProxy?, out JVector, out Real)"/>
    /// <param name="maxLambda">Maximum lambda of the ray's length to consider for intersections.</param>
    public bool RayCast(JVector origin, JVector direction, Real maxLambda, RayCastFilterPre? pre, RayCastFilterPost? post,
        out IDynamicTreeProxy? proxy, out JVector normal, out Real lambda)
    {
        var handler = new DelegateRayHitHandler { FilterPre = pre, FilterPost = post };
        Real bound = maxLambda;
        RayCast(origin, direction, ref bound, ref handler);
        proxy = handler.Best.Entity;
        normal = handler.Best.Normal;
        lambda = handler.Hit ? handler.Best.Lambda : maxLambda;
        return handler.Hit;
    }

    /// <summary>
    /// Casts a ray against the tree, reporting every accepted hit to <paramref name="handler"/>.
    /// </summary>
    /// <param name="maxLambda">Search bound in units of <paramref name="direction"/>. The handler may
    /// shrink this as hits are found so later branches can be pruned.</param>
    /// <remarks>
    /// XENGINE: struct-generic counterpart of the delegate overloads. A closest-hit handler that
    /// writes the hit lambda back into <paramref name="maxLambda"/> gets the same AABB pruning as
    /// the original <see cref="RayCast(JVector, JVector, Real, RayCastFilterPre?, RayCastFilterPost?, out IDynamicTreeProxy?, out JVector, out Real)"/>.
    /// </remarks>
    public void RayCast<THandler>(in JVector origin, in JVector direction, ref Real maxLambda, ref THandler handler)
        where THandler : struct, IRayHitHandler
    {
        if (root == -1) return;

        Stack<int> stack = QueryStack;
        int baseCount = stack.Count;
        stack.Push(root);

        while (stack.Count > baseCount)
        {
            int pop = stack.Pop();
            ref Node node = ref nodes[pop];

            if (node.IsLeaf)
            {
                if (node.Proxy is not IRayCastable irc) continue;
                if (!handler.AllowTest(node.Proxy)) continue;

                bool hit = irc.RayCast(origin, direction, out JVector normal, out Real lambda);
                if (!hit || lambda >= maxLambda) continue;

                handler.OnHit(node.Proxy, in normal, lambda, ref maxLambda);
                continue;
            }

            ref Node lNode = ref nodes[node.Left];
            ref Node rNode = ref nodes[node.Right];

            bool lRes = lNode.ExpandedBox.RayIntersect(origin, direction, out Real lEnter);
            bool rRes = rNode.ExpandedBox.RayIntersect(origin, direction, out Real rEnter);

            if (lEnter >= maxLambda) lRes = false;
            if (rEnter >= maxLambda) rRes = false;

            if (lRes && rRes)
            {
                if (lEnter < rEnter)
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
                if (lRes) stack.Push(node.Left);
                if (rRes) stack.Push(node.Right);
            }
        }
    }

    // XENGINE: adapts the original delegate filters to IRayHitHandler so the public delegate
    // overloads share the generic traversal instead of keeping a second copy of it.
    private struct DelegateRayHitHandler : IRayHitHandler
    {
        public RayCastFilterPre? FilterPre;
        public RayCastFilterPost? FilterPost;
        public RayCastResult Best;
        public bool Hit;

        public bool AllowTest(IDynamicTreeProxy proxy) => FilterPre == null || FilterPre(proxy);

        public void OnHit(IDynamicTreeProxy proxy, in JVector normal, Real hitLambda, ref Real maxLambda)
        {
            var res = new RayCastResult { Entity = proxy, Normal = normal, Lambda = hitLambda };
            if (FilterPost != null && !FilterPost(res)) return;
            Best = res;
            Hit = true;
            maxLambda = hitLambda;
        }
    }
}
