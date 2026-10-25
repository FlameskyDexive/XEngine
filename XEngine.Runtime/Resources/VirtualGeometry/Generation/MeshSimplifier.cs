// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Runtime.VirtualGeometry.Generation;

/// <summary>Output of <see cref="MeshSimplifier.Simplify"/>.</summary>
public sealed class SimplifiedMesh
{
    /// <summary>Output vertex positions (some moved from their input positions by collapses).</summary>
    public Float3[] Positions = Array.Empty<Float3>();

    /// <summary>Output triangles, 3 compacted vertex ids each.</summary>
    public int[] Triangles = Array.Empty<int>();

    /// <summary>Per output vertex: accumulated geometric deviation from the original geometry.</summary>
    public float[] VertexErrors = Array.Empty<float>();

    /// <summary>Per output vertex: the input vertex id whose attributes it inherited.</summary>
    public int[] OriginVertices = Array.Empty<int>();
}

/// <summary>
/// Quadric error metric (Garland-Heckbert) edge-collapse simplifier for small patches
/// (cluster groups). Boundary vertices are locked in place so sibling groups that share
/// an edge never pull apart, and collapses that flip a face normal are rejected. Errors are
/// accumulated as squared quadric costs and square-rooted once at output. Import time only —
/// allocation discipline is not required here.
/// </summary>
public static class MeshSimplifier
{
    /// <param name="positions">Welded input vertex pool.</param>
    /// <param name="triangles">Triangle list, 3 pool ids per triangle.</param>
    /// <param name="targetTriangles">Stop collapsing once at or below this triangle count.</param>
    /// <param name="initialErrors">Per-pool-vertex error distances already accumulated by earlier
    /// simplification rounds; new collapse costs max into them. Length 0 treated as all-zero.</param>
    /// <param name="maxErrorSq">Squared cost cutoff: candidates beyond it are dropped instead of
    /// executed, so the patch under-simplifies rather than baking a huge geometric error.
    /// Pass float.MaxValue to disable.</param>
    /// <summary>Per-call diagnostics (temp: bake investigation).</summary>

    public static SimplifiedMesh Simplify(ReadOnlySpan<Float3> positions, ReadOnlySpan<int> triangles, int targetTriangles, ReadOnlySpan<float> initialErrors = default, float maxErrorSq = float.MaxValue)
    {
        if (triangles.Length % 3 != 0)
            throw new ArgumentException("Triangle count must be a multiple of 3.", nameof(triangles));

        int vertexCount = positions.Length;
        int triangleCount = triangles.Length / 3;

        var result = new SimplifiedMesh();
        if (vertexCount == 0 || triangleCount == 0)
        {
            result.OriginVertices = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) result.OriginVertices[i] = i;
            result.Positions = positions.ToArray();
            result.VertexErrors = new float[vertexCount];
            result.Triangles = Array.Empty<int>();
            return result;
        }

        targetTriangles = Math.Max(1, targetTriangles);

        bool[] alive = new bool[vertexCount];
        int[] remap = new int[vertexCount];
        Float3[] pos = new Float3[vertexCount];
        for (int i = 0; i < vertexCount; i++) { alive[i] = true; remap[i] = i; pos[i] = positions[i]; }

        var quadrics = new Quadric[vertexCount];
        double[] vertError = new double[vertexCount]; // squared
        for (int i = 0; i < vertexCount && i < initialErrors.Length; i++)
            vertError[i] = (double)initialErrors[i] * initialErrors[i];

        // Adjacency. Edge keys pack (min,max) ids into one long.
        var edgeCount = new Dictionary<long, int>();
        var neighbors = new List<int>[vertexCount];
        var vertexTris = new List<int>[vertexCount];
        for (int i = 0; i < vertexCount; i++) { neighbors[i] = new List<int>(8); vertexTris[i] = new List<int>(8); }

        int[] triVertexA = new int[triangleCount];
        int[] triVertexB = new int[triangleCount];
        int[] triVertexC = new int[triangleCount];
        bool[] triAlive = new bool[triangleCount];
        int aliveTriangles = 0;

        for (int t = 0; t < triangleCount; t++)
        {
            int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
            triVertexA[t] = a; triVertexB[t] = b; triVertexC[t] = c;
            if (a == b || b == c || a == c) { triAlive[t] = false; continue; }
            triAlive[t] = true;

            Float3 n = Cross(pos[b] - pos[a], pos[c] - pos[a]);
            float area2 = Float3.Length(n);
            if (area2 <= 1e-16f) continue; // degenerate: no quadric, but keep adjacency consistent

            // Area-weighted plane quadric distributed to the three corners.
            float w = area2 * 0.5f;
            Float3 nN = n * (1f / area2);
            float d = -Float3.Dot(nN, pos[a]);
            var q = Quadric.FromPlane(nN.X, nN.Y, nN.Z, d, w);
            quadrics[a].Add(in q);
            quadrics[b].Add(in q);
            quadrics[c].Add(in q);

            vertexTris[a].Add(t);
            vertexTris[b].Add(t);
            vertexTris[c].Add(t);

            AddEdge(edgeCount, neighbors, a, b);
            AddEdge(edgeCount, neighbors, b, c);
            AddEdge(edgeCount, neighbors, c, a);
            aliveTriangles++;
        }

        // Boundary vertices (touched by an edge used by exactly one triangle) never move
        // and never disappear, which keeps adjacent groups crack-free at shared borders.
        bool[] boundary = new bool[vertexCount];
        foreach (var kv in edgeCount)
        {
            if (kv.Value != 1) continue;
            boundary[UnpackLow(kv.Key)] = true;
            boundary[UnpackHigh(kv.Key)] = true;
        }

        var heap = new CollapseHeap(triangles.Length * 3 + vertexCount * 4);
        foreach (var kv in edgeCount)
        {
            int v1 = UnpackLow(kv.Key), v2 = UnpackHigh(kv.Key);
            heap.Push(EvaluateCollapse(pos, quadrics, boundary, v1, v2).Cost, v1, v2);
        }

        while (aliveTriangles > targetTriangles && heap.TryPop(out float cost, out int v1, out int v2))
        {
            if (!alive[v1] || !alive[v2]) continue;

            // Quadrics changed since this entry was pushed — reinsert with the fresh cost.
            var fresh = EvaluateCollapse(pos, quadrics, boundary, v1, v2);
            if (fresh.Cost > cost * 1.000001 + 1e-20)
            {
                
                heap.Push(fresh.Cost, v1, v2);
                continue;
            }

            // Beyond the caller's error budget: drop the candidate entirely (under-simplify
            // rather than bake a huge deviation) and let the loop drain the heap.
            if (fresh.Cost > maxErrorSq)
            {
                
                continue;
            }

            // Keep the boundary endpoint on boundary-interior collapses; skip border-border edges.
            if (boundary[v1] && boundary[v2]) continue;
            if (boundary[v2]) (v1, v2) = (v2, v1);
            // v1 survives, v2 collapses into v1.

            if (CollapseFlipsNormal(pos, vertexTris, triVertexA, triVertexB, triVertexC, triAlive, v1, v2, in fresh.Target))
            {
                
                continue;
            }

            

            quadrics[v1].Add(in quadrics[v2]);
            vertError[v1] = Math.Max(vertError[v1], Math.Max(vertError[v2], fresh.Cost));
            if (!boundary[v1])
                pos[v1] = fresh.Target;

            alive[v2] = false;
            remap[v2] = v1;

            // Rewire v2's triangles onto v1, dropping the ones that degenerate.
            for (int ti = 0; ti < vertexTris[v2].Count; ti++)
            {
                int t = vertexTris[v2][ti];
                if (!triAlive[t]) continue;
                ReplaceTriVertex(triVertexA, triVertexB, triVertexC, t, v2, v1);
                if (triVertexA[t] == triVertexB[t] || triVertexB[t] == triVertexC[t] || triVertexA[t] == triVertexC[t])
                {
                    triAlive[t] = false;
                    aliveTriangles--;
                    continue;
                }
                vertexTris[v1].Add(t);
            }
            vertexTris[v2].Clear();

            // Re-key v2's edges around v1 and queue any brand-new adjacencies.
            for (int ni = 0; ni < neighbors[v2].Count; ni++)
            {
                int other = neighbors[v2][ni];
                if (!alive[other] || other == v1) continue;
                if (edgeCount.Remove(EdgeKey(v2, other)))
                    neighbors[other].Remove(v2);
                AddEdge(edgeCount, neighbors, v1, other);
                heap.Push(EvaluateCollapse(pos, quadrics, boundary, v1, other).Cost, v1, other);
            }
            neighbors[v2].Clear();

            // v1's existing edges changed quadrics; re-queue them (lazy validation rescores).
            for (int ni = 0; ni < neighbors[v1].Count; ni++)
            {
                int other = neighbors[v1][ni];
                if (!alive[other] || !edgeCount.ContainsKey(EdgeKey(v1, other))) continue;
                heap.Push(EvaluateCollapse(pos, quadrics, boundary, v1, other).Cost, v1, other);
            }
        }

        // Compact surviving vertices; dead ones redirect through remap chains.
        int[] compact = new int[vertexCount];
        int outCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            if (!alive[i]) continue;
            compact[i] = outCount++;
        }

        result.Positions = new Float3[outCount];
        result.VertexErrors = new float[outCount];
        result.OriginVertices = new int[outCount];
        for (int i = 0; i < vertexCount; i++)
        {
            if (!alive[i]) continue;
            result.Positions[compact[i]] = pos[i];
            result.VertexErrors[compact[i]] = (float)Math.Sqrt(Math.Max(0, vertError[i]));
            result.OriginVertices[compact[i]] = i;
        }

        var outTris = new List<int>(aliveTriangles * 3);
        for (int t = 0; t < triangleCount; t++)
        {
            if (!triAlive[t]) continue;
            int ra = Resolve(remap, triVertexA[t]);
            int rb = Resolve(remap, triVertexB[t]);
            int rc = Resolve(remap, triVertexC[t]);
            if (ra == rb || rb == rc || ra == rc) continue;
            outTris.Add(compact[ra]);
            outTris.Add(compact[rb]);
            outTris.Add(compact[rc]);
        }
        result.Triangles = outTris.ToArray();
        return result;
    }

    /// <summary>Squared collapse cost plus the resulting target position (squared so heap
    /// ordering matches the accumulated error bookkeeping).</summary>
    private readonly struct EvalResult
    {
        public readonly float Cost;
        public readonly Float3 Target;
        public EvalResult(float cost, Float3 target) { Cost = cost; Target = target; }
    }

    private static EvalResult EvaluateCollapse(Float3[] pos, Quadric[] quadrics, bool[] boundary, int v1, int v2)
    {
        Quadric q = quadrics[v1];
        q.Add(in quadrics[v2]);

        // A pinned endpoint keeps its position, but the collapse still costs the quadric
        // error of moving the other endpoint onto it.
        if (boundary[v1] && !boundary[v2]) { Float3 t = pos[v1]; return new((float)q.ErrorAt(t), t); }
        if (boundary[v2] && !boundary[v1]) { Float3 t = pos[v2]; return new((float)q.ErrorAt(t), t); }

        Float3 a = pos[v1], b = pos[v2];
        float edgeLenSq = Float3.LengthSquared(b - a);

        // Near-singular quadrics "solve" to positions arbitrarily far outside the patch;
        // anything past a few edge lengths of both endpoints is treated as unstable and
        // falls through to the endpoint/midpoint candidates.
        if (q.TrySolveOptimal(out Float3 p) && IsStableTarget(p, a, b, edgeLenSq))
        {
            double optCost = q.ErrorAt(p);
            double e1 = q.ErrorAt(a);
            double e2 = q.ErrorAt(b);
            if (optCost <= e1 && optCost <= e2)
                return new((float)Math.Max(0, optCost), p);
        }

        // Fallback: cheapest of the two endpoints and their midpoint.
        Float3 mid = (a + b) * 0.5f;
        double c1 = Math.Max(0, q.ErrorAt(a));
        double c2 = Math.Max(0, q.ErrorAt(b));
        double cm = Math.Max(0, q.ErrorAt(mid));
        if (c1 <= c2 && c1 <= cm) return new((float)c1, a);
        if (c2 <= cm) return new((float)c2, b);
        return new((float)cm, mid);
    }

    private static bool IsStableTarget(in Float3 p, in Float3 a, in Float3 b, float edgeLenSq)
    {
        // Degenerate (near-zero-length) edges always take the fallback path.
        if (edgeLenSq <= 1e-20f)
            return false;
        float limitSq = 9f * edgeLenSq; // within 3 edge lengths of both endpoints
        return Float3.LengthSquared(p - a) <= limitSq && Float3.LengthSquared(p - b) <= limitSq;
    }

    private static bool CollapseFlipsNormal(Float3[] pos, List<int>[] vertexTris,
        int[] triA, int[] triB, int[] triC, bool[] triAlive, int keep, int remove, in Float3 target)
    {
        for (int side = 0; side < 2; side++)
        {
            int v = side == 0 ? keep : remove;
            var tris = vertexTris[v];
            for (int ti = 0; ti < tris.Count; ti++)
            {
                int t = tris[ti];
                if (!triAlive[t]) continue;
                int a = triA[t], b = triB[t], c = triC[t];
                bool touchesBoth =
                    (a == keep || b == keep || c == keep) && (a == remove || b == remove || c == remove);
                if (touchesBoth)
                    continue; // dies in this collapse anyway

                Float3 before = Cross(pos[b] - pos[a], pos[c] - pos[a]);
                Float3 pa = a == remove ? target : pos[a];
                Float3 pb = b == remove ? target : pos[b];
                Float3 pc = c == remove ? target : pos[c];
                Float3 after = Cross(pb - pa, pc - pa);
                // Reject only genuine folds. Late-stage slivers wobble through zero area,
                // and rejecting those stalls simplification long before the target.
                float dot = Float3.Dot(before, after);
                float lenSq = Float3.LengthSquared(before) * Float3.LengthSquared(after);
                if (dot < 0f && dot * dot > 0.04f * lenSq) // angle change beyond ~101°
                    return true;
            }
        }
        return false;
    }

    private static void ReplaceTriVertex(int[] triA, int[] triB, int[] triC, int t, int from, int to)
    {
        if (triA[t] == from) triA[t] = to;
        if (triB[t] == from) triB[t] = to;
        if (triC[t] == from) triC[t] = to;
    }

    private static void AddEdge(Dictionary<long, int> edgeCount, List<int>[] neighbors, int a, int b)
    {
        long key = EdgeKey(a, b);
        if (edgeCount.TryGetValue(key, out int count))
        {
            edgeCount[key] = count + 1;
            return;
        }

        edgeCount[key] = 1;
        neighbors[a].Add(b);
        neighbors[b].Add(a);
    }

    private static long EdgeKey(int a, int b)
        => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static int UnpackLow(long key) => (int)(key & 0xFFFFFFFF);
    private static int UnpackHigh(long key) => (int)((ulong)key >> 32);

    private static int Resolve(int[] remap, int v)
    {
        while (remap[v] != v) v = remap[v];
        return v;
    }

    private static Float3 Cross(in Float3 a, in Float3 b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Symmetric 4x4 quadric matrix stored as 10 upper-triangle doubles.</summary>
    private struct Quadric
    {
        public double A11, A12, A13, A14, A22, A23, A24, A33, A34, A44;

        public static Quadric FromPlane(float a, float b, float c, float d, float w)
        {
            Quadric q;
            q.A11 = w * a * a; q.A12 = w * a * b; q.A13 = w * a * c; q.A14 = w * a * d;
            q.A22 = w * b * b; q.A23 = w * b * c; q.A24 = w * b * d;
            q.A33 = w * c * c; q.A34 = w * c * d;
            q.A44 = w * d * d;
            return q;
        }

        public void Add(in Quadric o)
        {
            A11 += o.A11; A12 += o.A12; A13 += o.A13; A14 += o.A14;
            A22 += o.A22; A23 += o.A23; A24 += o.A24;
            A33 += o.A33; A34 += o.A34;
            A44 += o.A44;
        }

        public double ErrorAt(in Float3 v)
        {
            double x = v.X, y = v.Y, z = v.Z;
            return A11 * x * x + 2 * A12 * x * y + 2 * A13 * x * z + 2 * A14 * x
                 + A22 * y * y + 2 * A23 * y * z + 2 * A24 * y
                 + A33 * z * z + 2 * A34 * z
                 + A44;
        }

        /// <summary>Solve the 3x3 normal equations for the quadric minimum via Cramer's rule.</summary>
        public bool TrySolveOptimal(out Float3 p)
        {
            // [A11 A12 A13; A12 A22 A23; A13 A23 A33] p = -[A14 A24 A34]
            double det = A11 * (A22 * A33 - A23 * A23)
                       - A12 * (A12 * A33 - A23 * A13)
                       + A13 * (A12 * A23 - A22 * A13);
            if (Math.Abs(det) < 1e-12)
            {
                p = default;
                return false;
            }

            double b1 = -A14, b2 = -A24, b3 = -A34;
            double invDet = 1.0 / det;
            double x = (b1 * (A22 * A33 - A23 * A23) - A12 * (b2 * A33 - A23 * b3) + A13 * (b2 * A23 - A22 * b3)) * invDet;
            double y = (A11 * (b2 * A33 - A23 * b3) - b1 * (A12 * A33 - A23 * A13) + A13 * (A12 * b3 - b2 * A13)) * invDet;
            double z = (A11 * (A22 * b3 - b2 * A23) - A12 * (A12 * b3 - b2 * A13) + b1 * (A12 * A23 - A22 * A13)) * invDet;
            p = new Float3((float)x, (float)y, (float)z);
            return true;
        }
    }

    /// <summary>Binary min-heap of edge-collapse candidates with lazy rescoring on pop.</summary>
    private sealed class CollapseHeap
    {
        private float[] _costs;
        private int[] _v1;
        private int[] _v2;
        private int _count;

        public CollapseHeap(int capacity)
        {
            if (capacity < 16) capacity = 16;
            _costs = new float[capacity];
            _v1 = new int[capacity];
            _v2 = new int[capacity];
        }

        public void Push(float cost, int a, int b)
        {
            if (_count == _costs.Length) Grow();
            int i = _count++;
            _costs[i] = cost; _v1[i] = a; _v2[i] = b;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_costs[parent] <= _costs[i]) break;
                Swap(parent, i);
                i = parent;
            }
        }

        public bool TryPop(out float cost, out int a, out int b)
        {
            if (_count == 0) { cost = 0; a = b = 0; return false; }

            cost = _costs[0]; a = _v1[0]; b = _v2[0];
            _count--;
            _costs[0] = _costs[_count]; _v1[0] = _v1[_count]; _v2[0] = _v2[_count];
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, s = i;
                if (l < _count && _costs[l] < _costs[s]) s = l;
                if (r < _count && _costs[r] < _costs[s]) s = r;
                if (s == i) break;
                Swap(s, i);
                i = s;
            }
            return true;
        }

        private void Swap(int i, int j)
        {
            (_costs[i], _costs[j]) = (_costs[j], _costs[i]);
            (_v1[i], _v1[j]) = (_v1[j], _v1[i]);
            (_v2[i], _v2[j]) = (_v2[j], _v2[i]);
        }

        private void Grow()
        {
            int newCap = _costs.Length * 2;
            Array.Resize(ref _costs, newCap);
            Array.Resize(ref _v1, newCap);
            Array.Resize(ref _v2, newCap);
        }
    }
}
