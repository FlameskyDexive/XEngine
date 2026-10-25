Shader "XEngine/Virtual Geometry Cull"

Variants
{
}
    // Compute-only shader: per-view cluster culling for virtual geometry. Three passes:
    //   CullCount  — one thread per cluster, decides visibility, atomically accumulates the
    //                selected index count per submesh;
    //   CullPrefix — single thread, turns counts into per-submesh base offsets, indirect
    //                draw args, and resets the write cursors;
    //   CullWrite  — one thread per cluster, re-decides visibility, reserves a slice via an
    //                atomic cursor and copies the cluster's indices into the output buffer.
    //
    // A cluster renders when it is inside the view frustum, its screen-space error is within
    // the threshold, and its parent's error is NOT within the threshold (or it is a root /
    // its parent fell outside the frustum). This is the per-cluster O(1) form of the CPU
    // selector's descent, so both paths select the same clusters for the same view.

    Pass "CullCount"
    {
        SLANGPROGRAM
        Compute
        {
            struct VgCluster
            {
                float4 centerRadius;       // xyz local-space center, w local-space radius
                float4 errorStartCount;    // x error, y indexStart, z indexCount, w submesh
                float4 parentCenterRadius; // xyz parent center, w parent radius
                float4 parentErrorRoot;    // x parent error, y isRoot (1/0), zw unused
            };

            struct VgParams
            {
                float4x4 world;          // renderer local-to-world
                float4 camPosMaxScale;   // xyz camera position, w renderer max axis scale
                float4 camFwdTanY;       // xyz camera forward, w tan(fovY/2)
                float4 camRightTanX;     // xyz camera right,   w tan(fovX/2)
                float4 camUpSseFactor;   // xyz camera up,      w pixels per unit-error at unit distance
                float4 thresholdCounts;  // x pixel-error threshold, y clusterCount, z subMeshCount, w unused
            };

            [[vk::binding(0, 1)]] StructuredBuffer<VgCluster> _Clusters : register(t0);
            [[vk::binding(2, 1)]] StructuredBuffer<VgParams> _Params : register(t2);

            // Metal shares one buffer index space for t/u/b resources, so keep this UAV
            // register aligned with the explicit Vulkan binding and away from t0/t2.
            [[vk::binding(8, 1)]] RWStructuredBuffer<uint> _Counters : register(u8);

            bool VgFrustumAndSse(VgParams p, float3 center, float radius, float error, float threshold)
            {
                float3 wc = mul(p.world, float4(center, 1.0)).xyz;
                float r = radius * p.camPosMaxScale.w;
                float3 rel = wc - p.camPosMaxScale.xyz;
                float z = dot(rel, p.camFwdTanY.xyz);
                float x = dot(rel, p.camRightTanX.xyz);
                float y = dot(rel, p.camUpSseFactor.xyz);
                float zt = z * p.camRightTanX.w + r;
                float yt = z * p.camFwdTanY.w + r;
                if (z < -r || x < -zt || x > zt || y < -yt || y > yt)
                    return false;
                float sse = error * p.camUpSseFactor.w / max(z, 0.01);
                return sse <= threshold;
            }

            [numthreads(64, 1, 1)]
            void main(uint3 tid : SV_DispatchThreadID)
            {
                VgParams p = _Params[0];
                uint count = (uint)p.thresholdCounts.y;
                if (tid.x >= count)
                    return;

                VgCluster c = _Clusters[tid.x];
                float threshold = p.thresholdCounts.x;
                if (!VgFrustumAndSse(p, c.centerRadius.xyz, c.centerRadius.w, c.errorStartCount.x, threshold))
                    return;

                // The parent must be too coarse (or missing / off-frustum) for this cluster
                // to be the replacement geometry.
                bool parentTooCoarse = c.parentErrorRoot.y > 0.5f;
                if (!parentTooCoarse)
                {
                    parentTooCoarse = !VgFrustumAndSse(p, c.parentCenterRadius.xyz, c.parentCenterRadius.w, c.parentErrorRoot.x, threshold)
                        || c.parentErrorRoot.x * p.camUpSseFactor.w / max(dot(mul(p.world, float4(c.parentCenterRadius.xyz, 1.0)).xyz - p.camPosMaxScale.xyz, p.camFwdTanY.xyz), 0.01) > threshold;
                }
                if (!parentTooCoarse)
                    return;

                uint subMesh = (uint)c.errorStartCount.w;
                InterlockedAdd(_Counters[subMesh], (uint)c.errorStartCount.z);
            }
        }
        ENDSLANG
    }

    Pass "CullPrefix"
    {
        SLANGPROGRAM
        Compute
        {
            struct VgParams
            {
                float4x4 world;
                float4 camPosMaxScale;
                float4 camFwdTanY;
                float4 camRightTanX;
                float4 camUpSseFactor;
                float4 thresholdCounts;
            };

            [[vk::binding(2, 1)]] StructuredBuffer<VgParams> _Params : register(t2);

            [[vk::binding(8, 1)]] RWStructuredBuffer<uint> _Counters : register(u8);  // [0..n): counts, [n..2n): cursors, [2n..3n): base offsets
            [[vk::binding(10, 1)]] RWStructuredBuffer<uint> _Args : register(u10);    // 5 uints per submesh

            [numthreads(1, 1, 1)]
            void main(uint3 tid : SV_DispatchThreadID)
            {
                uint n = (uint)_Params[0].thresholdCounts.z;
                uint sum = 0;
                for (uint i = 0; i < n; i++)
                {
                    uint count = _Counters[i];
                    _Counters[2 * n + i] = sum;   // base offset for submesh i
                    _Counters[n + i] = 0;         // reset write cursor
                    _Args[i * 5 + 0] = count;     // indexCount
                    _Args[i * 5 + 1] = 1;         // instanceCount
                    _Args[i * 5 + 2] = sum;       // firstIndex
                    _Args[i * 5 + 3] = 0;         // vertexOffset
                    _Args[i * 5 + 4] = 0;         // startInstance
                    sum += count;
                }
            }
        }
        ENDSLANG
    }

    Pass "CullWrite"
    {
        SLANGPROGRAM
        Compute
        {
            struct VgCluster
            {
                float4 centerRadius;
                float4 errorStartCount;
                float4 parentCenterRadius;
                float4 parentErrorRoot;
            };

            struct VgParams
            {
                float4x4 world;
                float4 camPosMaxScale;
                float4 camFwdTanY;
                float4 camRightTanX;
                float4 camUpSseFactor;
                float4 thresholdCounts;
            };

            [[vk::binding(0, 1)]] StructuredBuffer<VgCluster> _Clusters : register(t0);
            [[vk::binding(1, 1)]] StructuredBuffer<uint> _Indices : register(t1);
            [[vk::binding(2, 1)]] StructuredBuffer<VgParams> _Params : register(t2);

            [[vk::binding(8, 1)]] RWStructuredBuffer<uint> _Counters : register(u8);
            [[vk::binding(9, 1)]] RWStructuredBuffer<uint> _OutIndices : register(u9);

            bool VgFrustumAndSse(VgParams p, float3 center, float radius, float error, float threshold)
            {
                float3 wc = mul(p.world, float4(center, 1.0)).xyz;
                float r = radius * p.camPosMaxScale.w;
                float3 rel = wc - p.camPosMaxScale.xyz;
                float z = dot(rel, p.camFwdTanY.xyz);
                float x = dot(rel, p.camRightTanX.xyz);
                float y = dot(rel, p.camUpSseFactor.xyz);
                float zt = z * p.camRightTanX.w + r;
                float yt = z * p.camFwdTanY.w + r;
                if (z < -r || x < -zt || x > zt || y < -yt || y > yt)
                    return false;
                float sse = error * p.camUpSseFactor.w / max(z, 0.01);
                return sse <= threshold;
            }

            [numthreads(64, 1, 1)]
            void main(uint3 tid : SV_DispatchThreadID)
            {
                VgParams p = _Params[0];
                uint count = (uint)p.thresholdCounts.y;
                uint n = (uint)p.thresholdCounts.z;
                if (tid.x >= count)
                    return;

                VgCluster c = _Clusters[tid.x];
                float threshold = p.thresholdCounts.x;
                if (!VgFrustumAndSse(p, c.centerRadius.xyz, c.centerRadius.w, c.errorStartCount.x, threshold))
                    return;

                bool parentTooCoarse = c.parentErrorRoot.y > 0.5f;
                if (!parentTooCoarse)
                {
                    parentTooCoarse = !VgFrustumAndSse(p, c.parentCenterRadius.xyz, c.parentCenterRadius.w, c.parentErrorRoot.x, threshold)
                        || c.parentErrorRoot.x * p.camUpSseFactor.w / max(dot(mul(p.world, float4(c.parentCenterRadius.xyz, 1.0)).xyz - p.camPosMaxScale.xyz, p.camFwdTanY.xyz), 0.01) > threshold;
                }
                if (!parentTooCoarse)
                    return;

                uint indexCount = (uint)c.errorStartCount.z;
                uint subMesh = (uint)c.errorStartCount.w;
                uint slot;
                InterlockedAdd(_Counters[n + subMesh], indexCount, slot);
                uint dst = _Counters[2 * n + subMesh] + slot;
                uint src = (uint)c.errorStartCount.y;
                for (uint k = 0; k < indexCount; k++)
                    _OutIndices[dst + k] = _Indices[src + k];
            }
        }
        ENDSLANG
    }
