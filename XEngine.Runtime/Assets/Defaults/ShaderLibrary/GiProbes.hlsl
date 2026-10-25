// Realtime GI dynamic irradiance probe volume sampling (Surface Cache GI, probe tier).
// HLSL/Slang mirror of GiProbes.glsl — see that file for the data layout notes.
// INCLUDED EXPLICITLY (before StandardLighting) only by shaders that sample the volume:
// the binding-layout parser scans source text for register declarations, so conditional
// compilation cannot hide them. Shaders that do not include this file get the zero-cost
// stubs StandardLighting provides instead.
#ifndef GI_PROBES_HLSL
#define GI_PROBES_HLSL

#include "ShaderLibrary/ShaderConfig"

// Slots t32..t34 / s32..s34 sit above every current consumer (Terrain remaps the standard
// lighting block up to t29); shaders that need more room can override the macros before
// including this file.
#ifndef XENGINE_GI_IRRADIANCE_ATLAS_TEXTURE_REGISTER
#define XENGINE_GI_IRRADIANCE_ATLAS_TEXTURE_REGISTER t32
#endif
#ifndef XENGINE_GI_IRRADIANCE_ATLAS_SAMPLER_REGISTER
#define XENGINE_GI_IRRADIANCE_ATLAS_SAMPLER_REGISTER s32
#endif
#ifndef XENGINE_GI_PROBE_MOMENTS_TEXTURE_REGISTER
#define XENGINE_GI_PROBE_MOMENTS_TEXTURE_REGISTER t33
#endif
#ifndef XENGINE_GI_PROBE_MOMENTS_SAMPLER_REGISTER
#define XENGINE_GI_PROBE_MOMENTS_SAMPLER_REGISTER s33
#endif
#ifndef XENGINE_GI_PROBE_DATA_TEXTURE_REGISTER
#define XENGINE_GI_PROBE_DATA_TEXTURE_REGISTER t34
#endif
#ifndef XENGINE_GI_PROBE_DATA_SAMPLER_REGISTER
#define XENGINE_GI_PROBE_DATA_SAMPLER_REGISTER s34
#endif

Texture2D _GiIrradianceAtlas : register(XENGINE_GI_IRRADIANCE_ATLAS_TEXTURE_REGISTER);
#ifndef XENGINE_METAL
SamplerState _GiIrradianceAtlasSampler : register(XENGINE_GI_IRRADIANCE_ATLAS_SAMPLER_REGISTER);
#else
// The irradiance atlas also uses linear/clamp sampling; reuse the shared Metal sampler.
#define _GiIrradianceAtlasSampler _LightCookieAtlasSampler
#endif
Texture2D _GiProbeMoments : register(XENGINE_GI_PROBE_MOMENTS_TEXTURE_REGISTER);
#ifndef XENGINE_METAL
SamplerState _GiProbeMomentsSampler : register(XENGINE_GI_PROBE_MOMENTS_SAMPLER_REGISTER);
#endif
Texture2D _GiProbeData : register(XENGINE_GI_PROBE_DATA_TEXTURE_REGISTER);
#ifndef XENGINE_METAL
SamplerState _GiProbeDataSampler : register(XENGINE_GI_PROBE_DATA_SAMPLER_REGISTER);
#endif

// True while the dynamic irradiance volume is bound and sampling (data-texture flag).
bool GiProbesActive()
{
    return _GiProbeData.Load(int3(1, 0, 0)).w > 0.5;
}

float2 GiOctEncode(float3 n)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 e = n.xy;
    if (n.z < 0.0)
        e = (1.0 - abs(e.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}

float3 GiOctDecode(float2 e)
{
    e = e * 2.0 - 1.0;
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    float t = max(-n.z, 0.0);
    n.x -= n.x >= 0.0 ? t : -t;
    n.y -= n.y >= 0.0 ? t : -t;
    return normalize(n);
}

float3 GiSampleProbe(int3 probeCell, float3 probeWorld, float3 worldPos, float3 worldNormal,
                     float3 counts, float tilesPerRow, float tileUV)
{
    int probeIndex = probeCell.x + probeCell.y * (int)counts.x + probeCell.z * (int)counts.x * (int)counts.y;
    float2 tile = float2(probeIndex % (int)tilesPerRow, probeIndex / (int)tilesPerRow);
    float3 dir = normalize(worldPos - probeWorld + worldNormal * 0.3);
    float2 oct = clamp(GiOctEncode(dir), 0.5 / GI_PROBE_TILE_SIZE, 1.0 - 0.5 / GI_PROBE_TILE_SIZE);
    return _GiIrradianceAtlas.Sample(_GiIrradianceAtlasSampler, (tile + oct) * tileUV).rgb;
}

float3 SampleGiProbes(float3 worldPos, float3 worldNormal)
{
    float4 t0 = _GiProbeData.Load(int3(0, 0, 0));
    float4 t1 = _GiProbeData.Load(int3(1, 0, 0));
    if (t1.w < 0.5) return 0.0;

    float3 origin = t0.xyz;
    float spacing = max(t0.w, 0.001);
    float3 counts = t1.xyz;
    int tilesTotal = (int)(counts.x * counts.y * counts.z);
    if (tilesTotal <= 0) return 0.0;
    float tilesPerRow = ceil(sqrt((float)tilesTotal));

    float3 g = (worldPos - origin) / spacing - 0.5;
    if (any(g < 0.0) || any(g > counts - 1.0)) return 0.0;

    float3 baseF = clamp(floor(g), 0.0, counts - 2.0);
    float3 f = clamp(g - baseF, 0.0, 1.0);
    int3 baseCell = (int3)baseF;
    float tileUV = 1.0 / (tilesPerRow * GI_PROBE_TILE_SIZE);

    float3 result = 0.0;
    [unroll]
    for (int z = 0; z < 2; z++)
    [unroll]
    for (int y = 0; y < 2; y++)
    [unroll]
    for (int x = 0; x < 2; x++)
    {
        int3 cell = baseCell + int3(x, y, z);
        float3 w3 = lerp(1.0 - f, f, float3(x, y, z));
        float w = w3.x * w3.y * w3.z;
        if (w <= 0.0) continue;
        float3 probeWorld = origin + (float3)cell * spacing;
        result += GiSampleProbe(cell, probeWorld, worldPos, worldNormal, counts, tilesPerRow, tileUV) * w;
    }
    return result;
}

#endif
