// Realtime GI dynamic irradiance probe volume sampling (Surface Cache GI, probe tier).
// Layout: a regular camera-following grid of probes; each probe owns one GI_PROBE_TILE_SIZE
// octahedral irradiance tile in _GiIrradianceAtlas (linear index = x + y*cx + z*cx*cy) and one
// mean-hit-distance tile in _GiProbeMoments. Grid params live in a 2-texel RGBA32F data
// texture (name-bound on every backend; no cbuffer ABI changes needed):
//   texelFetch(_GiProbeData, ivec2(0), 0) = (origin.xyz, spacing)
//   texelFetch(_GiProbeData, ivec2(1), 0) = (countX, countY, countZ, enabled)
#ifndef GI_PROBES_GLSL
#define GI_PROBES_GLSL

#include "ShaderLibrary/ShaderConfig"

uniform sampler2D _GiIrradianceAtlas;
uniform sampler2D _GiProbeMoments;
uniform sampler2D _GiProbeData;

// True while the dynamic irradiance volume is bound and sampling (data-texture flag).
bool GiProbesActive()
{
    return texelFetch(_GiProbeData, ivec2(1), 0).w > 0.5;
}

vec2 GiOctEncode(vec3 n)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    vec2 e = n.xy;
    if (n.z < 0.0)
        e = (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}

vec3 GiOctDecode(vec2 e)
{
    e = e * 2.0 - 1.0;
    vec3 n = vec3(e.xy, 1.0 - abs(e.x) - abs(e.y));
    float t = max(-n.z, 0.0);
    n.x -= n.x >= 0.0 ? t : -t;
    n.y -= n.y >= 0.0 ? t : -t;
    return normalize(n);
}

// One probe tap: evaluate the probe's octahedral irradiance map along the receiver-biased
// direction to the sample point (DDGI-style normal bias against self-occlusion).
vec3 GiSampleProbe(ivec3 probeCell, vec3 probeWorld, vec3 worldPos, vec3 worldNormal,
                   vec3 counts, float tilesPerRow, float tileUV)
{
    int probeIndex = probeCell.x + probeCell.y * int(counts.x) + probeCell.z * int(counts.x) * int(counts.y);
    vec2 tile = vec2(float(probeIndex % int(tilesPerRow)), float(probeIndex / int(tilesPerRow)));
    vec3 dir = normalize(worldPos - probeWorld + worldNormal * 0.3);
    // Clamp inside the tile so linear filtering never bleeds across probe borders.
    vec2 oct = clamp(GiOctEncode(dir), vec2(0.5 / float(GI_PROBE_TILE_SIZE)),
                     1.0 - vec2(0.5 / float(GI_PROBE_TILE_SIZE)));
    vec2 uv = (tile + oct) * tileUV;
    return texture(_GiIrradianceAtlas, uv).rgb;
}

// Trilinear blend of the 8 surrounding probes. Returns zero outside the volume so the
// caller can fall back to plain ambient.
vec3 SampleGiProbes(vec3 worldPos, vec3 worldNormal)
{
    vec4 t0 = texelFetch(_GiProbeData, ivec2(0), 0);
    vec4 t1 = texelFetch(_GiProbeData, ivec2(1), 0);
    if (t1.w < 0.5) return vec3(0.0);

    vec3 origin = t0.xyz;
    float spacing = max(t0.w, 0.001);
    vec3 counts = t1.xyz;
    int tilesTotal = int(counts.x) * int(counts.y) * int(counts.z);
    if (tilesTotal <= 0) return vec3(0.0);
    float tilesPerRow = ceil(sqrt(float(tilesTotal)));

    vec3 g = (worldPos - origin) / spacing - 0.5;
    if (any(lessThan(g, vec3(0.0))) || any(greaterThan(g, counts - 2.0 + vec3(1.0))))
        return vec3(0.0);

    vec3 baseF = clamp(floor(g), vec3(0.0), counts - 2.0);
    vec3 frac = clamp(g - baseF, vec3(0.0), vec3(1.0));
    ivec3 base = ivec3(baseF);
    float tileUV = 1.0 / (tilesPerRow * float(GI_PROBE_TILE_SIZE));

    vec3 result = vec3(0.0);
    for (int z = 0; z < 2; z++)
    for (int y = 0; y < 2; y++)
    for (int x = 0; x < 2; x++)
    {
        ivec3 cell = base + ivec3(x, y, z);
        vec3 w3 = mix(vec3(1.0) - frac, frac, vec3(float(x), float(y), float(z)));
        float w = w3.x * w3.y * w3.z;
        if (w <= 0.0) continue;
        vec3 probeWorld = origin + vec3(cell) * spacing;
        result += GiSampleProbe(cell, probeWorld, worldPos, worldNormal, counts, tilesPerRow, tileUV) * w;
    }
    return result;
}

#endif
