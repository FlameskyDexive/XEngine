#ifndef XENGINE_STANDARD_LIGHTING_HLSL
#define XENGINE_STANDARD_LIGHTING_HLSL

#include "XEngineCG"
#include "ShaderLibrary/ShaderConfig"
#include "StandardShadowSampling"
#include "ShaderLibrary/RenderingLayers"

// Realtime GI probe volume: shaders that sample it include ShaderLibrary/GiProbes BEFORE this
// file; the stubs below keep StandardCalculateGI's mode-3 branch compiling for everyone else
// (Terrain/Grass sit at the D3D12 64-root-parameter ceiling and must reflect no GI slots).
#ifndef GI_PROBES_HLSL
bool GiProbesActive() { return false; }
float3 SampleGiProbes(float3 worldPos, float3 worldNormal) { return 0.0; }
#endif

cbuffer StandardLighting : register(b3)
{
    int4 _StandardLightFlags;
    int4 _StandardStaticLightMeta;
    int4 _StandardDynamicLightMeta;
    float4 _StandardDirectionalDirectionIntensity;
    float4 _StandardDirectionalColor;
    float4 _StandardAmbientModeStrength;
    float4 _StandardAmbientColor;
    float4 _StandardAmbientSkyColor;
    float4 _StandardAmbientGroundColor;
    float4 _StandardFogColor;
    float4 _StandardFogParams;
    float4 _StandardFogStates;
    // Clustered light list (P3.2). Appended after the legacy 192-byte block so existing member
    // offsets stay stable. Must mirror StandardLightingUniformsData (MaterialUniforms.cs).
    float4 _ClusterGridParams;    // x=tilesX, y=tilesY, z=zSlices, w=enabled
    float4 _ClusterZParams;       // x=sliceNear, y=slicesPerLog2Z, z=texSize, w=texShift
    float4 _ClusterCameraForward; // xyz = camera forward (world space)
};

// Standard shadow state for the modern backends. Mirrors the GLSL Lighting.glsl uniform surface
// (directional cascade + closest-N point/spot slots that share the single shadow atlas) and is
// populated by MaterialUniformPacking.PackStandardShadows from the same command-ordered globals
// SceneLightSystem.UploadGlobalUniforms writes. Point slots use six faces; spot slots use one.
//
// Every member is a full float4 / float4x4 so the HLSL constant-buffer layout matches the way the
// C# StandardShadowsUniformsData (LayoutKind.Explicit, all-16-byte-aligned) marshals: no scalar
// packing surprises across 16-byte slot boundaries. Scalars are read out of the .x/.y/etc channel
// the packer fills (e.g. _DirectionalLightShadowEnabled is _ShadowParams0.x).
cbuffer StandardShadows : register(b4)
{
    float4 _ShadowAtlasSizePad;        // xy = _ShadowAtlasSize, z = _ScreenSpaceShadowsEnabled, w = pad
    float4 _ShadowParams0;             // x = _DirectionalLightShadowEnabled, y = bias, z = normalBias, w = strength
    float4 _ShadowParams1;             // x = quality, y = _CascadeCount, z = atlas projected-Y scale, w = _SoftShadowTaps
    float4x4 _CascadeShadowMatrix0;
    float4x4 _CascadeShadowMatrix1;
    float4x4 _CascadeShadowMatrix2;
    float4x4 _CascadeShadowMatrix3;
    float4 _CascadeAtlasParams0;
    float4 _CascadeAtlasParams1;
    float4 _CascadeAtlasParams2;
    float4 _CascadeAtlasParams3;
    float4x4 _PointShadowMatrices[XENGINE_STANDARD_MAX_SHADOW_CASTERS * 6];
    float4 _PointShadowFaceParams[XENGINE_STANDARD_MAX_SHADOW_CASTERS * 6];
    float4x4 _SpotShadowMatrices[XENGINE_STANDARD_MAX_SHADOW_CASTERS];
    float4 _SpotShadowAtlasParams[XENGINE_STANDARD_MAX_SHADOW_CASTERS];
};

#ifndef XENGINE_STANDARD_STATIC_LIGHT_DATA_TEXTURE_REGISTER
#define XENGINE_STANDARD_STATIC_LIGHT_DATA_TEXTURE_REGISTER t4
#define XENGINE_STANDARD_STATIC_LIGHT_DATA_SAMPLER_REGISTER s4
#define XENGINE_STANDARD_STATIC_LIGHT_NODES_TEXTURE_REGISTER t5
#define XENGINE_STANDARD_STATIC_LIGHT_NODES_SAMPLER_REGISTER s5
#define XENGINE_STANDARD_DYNAMIC_LIGHT_DATA_TEXTURE_REGISTER t6
#define XENGINE_STANDARD_DYNAMIC_LIGHT_DATA_SAMPLER_REGISTER s6
#define XENGINE_STANDARD_DYNAMIC_LIGHT_NODES_TEXTURE_REGISTER t7
#define XENGINE_STANDARD_DYNAMIC_LIGHT_NODES_SAMPLER_REGISTER s7
#define XENGINE_STANDARD_SHADOW_ATLAS_TEXTURE_REGISTER t8
#define XENGINE_STANDARD_SHADOW_ATLAS_SAMPLER_REGISTER s8
#define XENGINE_STANDARD_SCREEN_SPACE_SHADOW_TEXTURE_REGISTER t14
#define XENGINE_STANDARD_SCREEN_SPACE_SHADOW_SAMPLER_REGISTER s14
#define XENGINE_STANDARD_LIGHTMAP_TEXTURE_REGISTER t11
#define XENGINE_STANDARD_LIGHTMAP_SAMPLER_REGISTER s11
#endif

Texture2D _StaticLightData : register(XENGINE_STANDARD_STATIC_LIGHT_DATA_TEXTURE_REGISTER);
SamplerState _StaticLightDataSampler : register(XENGINE_STANDARD_STATIC_LIGHT_DATA_SAMPLER_REGISTER);
Texture2D _StaticLightNodes : register(XENGINE_STANDARD_STATIC_LIGHT_NODES_TEXTURE_REGISTER);
SamplerState _StaticLightNodesSampler : register(XENGINE_STANDARD_STATIC_LIGHT_NODES_SAMPLER_REGISTER);
Texture2D _DynamicLightData : register(XENGINE_STANDARD_DYNAMIC_LIGHT_DATA_TEXTURE_REGISTER);
SamplerState _DynamicLightDataSampler : register(XENGINE_STANDARD_DYNAMIC_LIGHT_DATA_SAMPLER_REGISTER);
Texture2D _DynamicLightNodes : register(XENGINE_STANDARD_DYNAMIC_LIGHT_NODES_TEXTURE_REGISTER);
SamplerState _DynamicLightNodesSampler : register(XENGINE_STANDARD_DYNAMIC_LIGHT_NODES_SAMPLER_REGISTER);

// Shared shadow atlas + sampler. Modern backends do not expose GL's hardware depth-compare sampler,
// so StandardSampleShadowPcf does an explicit <= comparison per tap.
Texture2D _ShadowAtlas : register(XENGINE_STANDARD_SHADOW_ATLAS_TEXTURE_REGISTER);
SamplerState _ShadowAtlasSampler : register(XENGINE_STANDARD_SHADOW_ATLAS_SAMPLER_REGISTER);

Texture2D _ScreenSpaceShadowmap : register(XENGINE_STANDARD_SCREEN_SPACE_SHADOW_TEXTURE_REGISTER);
SamplerState _ScreenSpaceShadowmapSampler : register(XENGINE_STANDARD_SCREEN_SPACE_SHADOW_SAMPLER_REGISTER);

// Per-object baked lightmap (only sampled when _GIMode == 1). RGBM-encoded baked irradiance; the
// engine binds the active lightmap page through SetTexture("_Lightmap", ...) per renderable.
Texture2D _Lightmap : register(XENGINE_STANDARD_LIGHTMAP_TEXTURE_REGISTER);
SamplerState _LightmapSampler : register(XENGINE_STANDARD_LIGHTMAP_SAMPLER_REGISTER);

int StandardSoftShadowTapCount()
{
    int taps = (int)_ShadowParams1.w;
    if (taps <= 0)
        taps = 8;
    if (taps > MAX_SOFT_SHADOW_TAPS)
        taps = MAX_SOFT_SHADOW_TAPS;
    return taps;
}

float StandardApplyScreenSpaceShadows(float shadowFactor, float2 screenPosition)
{
    if (_ShadowAtlasSizePad.z == 0.0)
        return shadowFactor;
    float2 uv = screenPosition / max(_ScreenParams.xy, 1.0.xx);
    return shadowFactor * _ScreenSpaceShadowmap.SampleLevel(_ScreenSpaceShadowmapSampler, uv, 0).r;
}

struct StandardLightSample
{
    float3 Position;
    float Range;
    float3 Color;
    float Intensity;
    float3 Direction;
    int Type;
    float SpotCos;
    float InnerSpotCos;
    int ShadowEnabled;
    int ShadowSlot;
    float ShadowBias;
    float ShadowNormalBias;
    float ShadowStrength;
    float ShadowQuality;
    int CookieEnabled;
    float CookieIntensity;
    float4 CookieRect;
    int RenderingLayers;
};

int2 StandardTextureCoord(int linearIndex, int dimension, int shift)
{
    int mask = dimension - 1;
    return int2(linearIndex & mask, linearIndex >> shift);
}

StandardLightSample StandardFetchLight(Texture2D lightTexture, int dimension, int shift, int slot)
{
    int baseIndex = slot * LBVH_TEXELS_PER_LIGHT;
    float4 positionRange = lightTexture.Load(int3(StandardTextureCoord(baseIndex, dimension, shift), 0));
    float4 colorIntensity = lightTexture.Load(int3(StandardTextureCoord(baseIndex + 1, dimension, shift), 0));
    float4 directionFlags = lightTexture.Load(int3(StandardTextureCoord(baseIndex + 2, dimension, shift), 0));

    // typeAndFlags packs LightType (bits 0-1) and ShadowEnabled (bit 2). Matches the row layout
    // LightBVHTextures writes and LightBVH.hlsl decodes for the fog path.
    int typeAndFlags = asint(directionFlags.w);
    int type = typeAndFlags & 3;
    int shadowEnabled = (typeAndFlags >> 2) & 1;
    int cookieEnabled = (typeAndFlags >> 3) & 1;
    int renderingLayers = (typeAndFlags >> 8) & 255;

    StandardLightSample light;
    light.Position = positionRange.xyz;
    light.Range = positionRange.w;
    light.Color = colorIntensity.rgb;
    light.Intensity = colorIntensity.w;
    light.Direction = directionFlags.xyz;
    light.Type = type;
    light.ShadowEnabled = shadowEnabled;
    light.CookieEnabled = cookieEnabled;
    light.RenderingLayers = renderingLayers;
    light.SpotCos = -1.0;
    light.InnerSpotCos = 1.0;
    light.ShadowBias = 0.0;
    light.ShadowNormalBias = 0.0;
    light.ShadowStrength = 0.0;
    light.ShadowQuality = 0.0;
    light.ShadowSlot = -1;
    light.CookieIntensity = 0.0;
    light.CookieRect = 0.0.xxxx;

    if (light.Type == 2 || shadowEnabled != 0 || cookieEnabled != 0)
    {
        float4 spotShadow = lightTexture.Load(int3(StandardTextureCoord(baseIndex + 3, dimension, shift), 0));
        light.SpotCos = spotShadow.x;
        light.InnerSpotCos = spotShadow.y;
        light.ShadowBias = spotShadow.z;
        light.ShadowNormalBias = spotShadow.w;
    }

    if (shadowEnabled != 0 || cookieEnabled != 0)
    {
        float4 shadow = lightTexture.Load(int3(StandardTextureCoord(baseIndex + 4, dimension, shift), 0));
        light.ShadowStrength = shadow.x;
        light.ShadowQuality = shadow.y;
        light.ShadowSlot = asint(shadow.z);
        light.CookieIntensity = shadow.w;
    }

    if (cookieEnabled != 0)
        light.CookieRect = lightTexture.Load(int3(StandardTextureCoord(baseIndex + 5, dimension, shift), 0));
    return light;
}

#ifndef XENGINE_LIGHT_COOKIE_HLSL
float3 SampleMainLightCookie(float3 worldPos)
{
    return 1.0.xxx;
}
#endif

float3 SampleLocalLightCookie(StandardLightSample light, float3 worldPos)
{
#ifdef XENGINE_LIGHT_COOKIE_HLSL
    if (light.CookieEnabled == 0)
        return 1.0.xxx;
    float2 uv = light.Type == 2
        ? LightCookieSpotUv(worldPos, light.Position, light.Direction, light.Range, light.SpotCos)
        : LightCookiePointUv(worldPos, light.Position);
    return SampleLightCookieAtlas(uv, light.CookieRect, light.CookieIntensity);
#else
    return 1.0.xxx;
#endif
}

// ===========================================================================
//  Clustered light list (P3.2)  opt-in via XENGINE_CLUSTERED_LIGHTS
//
//  Replaces the per-fragment dual-BVH walk with one cluster lookup + a compact light index
//  loop. Layout is in lockstep with ClusterLightList.cs (headers then packed entry slots in a
//  single RGBA32F texture); see Lighting.glsl for the shared documentation. The texture is only
//  declared under the macro so shaders that stay on the BVH path (Terrain/Grass at the D3D12
//  64-root-parameter ceiling) reflect no extra bindings.
// ===========================================================================

#ifdef XENGINE_CLUSTERED_LIGHTS
#ifndef XENGINE_STANDARD_CLUSTER_LIGHT_LIST_TEXTURE_REGISTER
#define XENGINE_STANDARD_CLUSTER_LIGHT_LIST_TEXTURE_REGISTER t29
#define XENGINE_STANDARD_CLUSTER_LIGHT_LIST_SAMPLER_REGISTER s29
#endif

Texture2D _ClusterLightList : register(XENGINE_STANDARD_CLUSTER_LIGHT_LIST_TEXTURE_REGISTER);
#ifndef XENGINE_METAL
// The clustered list is read exclusively through Texture2D.Load, which has no sampler
// dependency. Keep the high logical sampler slot for backends that use the shared layout,
// but do not emit an out-of-range Metal sampler (Metal supports at most 16 per stage).
SamplerState _ClusterLightListSampler : register(XENGINE_STANDARD_CLUSTER_LIGHT_LIST_SAMPLER_REGISTER);
#endif

int2 StandardClusterCoord(int linearIndex)
{
    int dim = (int)_ClusterZParams.z;
    int shift = (int)_ClusterZParams.w;
    return int2(linearIndex & (dim - 1), linearIndex >> shift);
}

// Header of the cluster containing worldPosition: .x = first absolute entry slot, .y = count.
// The xy tile comes from re-projecting worldPosition with the same VP matrix the CPU build
// used, so no SV_Position y-origin / render-scale conventions are involved.
int2 StandardClusterHeader(float3 worldPosition)
{
    float4 clip = mul(xengine_MatVP, float4(worldPosition, 1.0));
    if (clip.w <= 1e-6)
        return int2(0, 0);
    float2 ndc = clip.xy / clip.w;
    int tilesX = (int)_ClusterGridParams.x;
    int tilesY = (int)_ClusterGridParams.y;
    int slices = (int)_ClusterGridParams.z;
    int tx = clamp((int)((ndc.x * 0.5 + 0.5) * (float)tilesX), 0, tilesX - 1);
    int ty = clamp((int)((ndc.y * 0.5 + 0.5) * (float)tilesY), 0, tilesY - 1);
    float viewZ = dot(worldPosition - _WorldSpaceCameraPos, _ClusterCameraForward.xyz);
    float relZ = max(viewZ / _ClusterZParams.x, 1.0);
    int slice = clamp((int)(log2(relZ) * _ClusterZParams.y), 0, slices - 1);
    int cluster = (slice * tilesY + ty) * tilesX + tx;
    float4 header = _ClusterLightList.Load(int3(StandardClusterCoord(cluster), 0));
    // min() caps the loop so a corrupt texture cannot stall the fragment shader.
    return int2(asint(header.x), min(asint(header.y), 1024));
}

// Resolve one entry slot to its light. encoded > 0 = static BVH slot (encoded-1),
// encoded < 0 = dynamic BVH slot (-encoded-1). Ternary channel select avoids dynamic vector
// indexing for maximum cross-target compatibility (WGSL/Metal via Slang).
StandardLightSample StandardClusterFetchLight(int entrySlot)
{
    float4 t = _ClusterLightList.Load(int3(StandardClusterCoord(entrySlot >> 2), 0));
    int ch = entrySlot & 3;
    float raw = ch == 0 ? t.x : (ch == 1 ? t.y : (ch == 2 ? t.z : t.w));
    int encoded = asint(raw);
    if (encoded > 0)
        return StandardFetchLight(_StaticLightData, _StandardStaticLightMeta.x,
                                  _StandardStaticLightMeta.y, encoded - 1);
    return StandardFetchLight(_DynamicLightData, _StandardDynamicLightMeta.x,
                              _StandardDynamicLightMeta.y, -encoded - 1);
}
#endif

int StandardBvhNext(
    inout int current,
    Texture2D nodeTexture,
    int dimension,
    int shift,
    float3 worldPosition)
{
    int budget = 4096;
    while (current >= 0 && budget > 0)
    {
        budget--;
        int baseIndex = current * 2;
        float4 lo = nodeTexture.Load(int3(StandardTextureCoord(baseIndex, dimension, shift), 0));
        float4 hi = nodeTexture.Load(int3(StandardTextureCoord(baseIndex + 1, dimension, shift), 0));
        int hitNext = asint(lo.w);
        int missNext = asint(hi.w);
        if (hitNext < 0)
        {
            float3 center = (lo.xyz + hi.xyz) * 0.5;
            float radius = max((hi.x - lo.x) * 0.5, 0.0);
            float3 delta = worldPosition - center;
            current = missNext;
            if (dot(delta, delta) <= radius * radius)
                return -hitNext - 1;
        }
        else
        {
            bool inside = all(worldPosition >= lo.xyz) && all(worldPosition <= hi.xyz);
            current = inside ? hitNext : missNext;
        }
    }
    return -1;
}

float StandardDistributionGGX(float3 normal, float3 halfway, float roughness)
{
    float alpha = roughness * roughness;
    float alpha2 = alpha * alpha;
    float nDotH = max(dot(normal, halfway), 0.0);
    float nDotH2 = nDotH * nDotH;
    float denominator = nDotH2 * (alpha2 - 1.0) + 1.0;
    return alpha2 / max(3.14159265 * denominator * denominator, 0.0001);
}

float StandardGeometrySchlickGGX(float nDot, float roughness)
{
    float k = (roughness + 1.0) * (roughness + 1.0) * 0.125;
    return nDot / max(nDot * (1.0 - k) + k, 0.0001);
}

float StandardGeometrySmith(float3 normal, float3 viewDirection, float3 lightDirection, float roughness)
{
    return StandardGeometrySchlickGGX(max(dot(normal, viewDirection), 0.0), roughness) *
           StandardGeometrySchlickGGX(max(dot(normal, lightDirection), 0.0), roughness);
}

float3 StandardFresnel(float cosTheta, float3 f0)
{
    return f0 + (1.0.xxx - f0) * pow(1.0 - saturate(cosTheta), 5.0);
}

// Indirect-specular Fresnel approximation used by the ambient block (matches FresnelSchlickRoughness
// in PBR.glsl): rougher surfaces scatter indirect specular more broadly, so the Fresnel term blends
// toward a roughness-scaled f0 at grazing angles instead of the sharp 1.0 of the direct-light form.
float3 StandardAmbientFresnel(float cosTheta, float3 f0, float roughness)
{
    float3 smoothness = max(1.0 - roughness, 0.04).xxx;
    return f0 + (max(smoothness, f0) - f0) * pow(1.0 - saturate(cosTheta), 5.0);
}

float StandardDisneyDiffuse(float nDotV, float nDotL, float lDotH, float roughness)
{
    float fd90 = 0.5 + 2.0 * lDotH * lDotH * roughness;
    float lightScatter = 1.0 + (fd90 - 1.0) * pow(1.0 - nDotL, 5.0);
    float viewScatter = 1.0 + (fd90 - 1.0) * pow(1.0 - nDotV, 5.0);
    return lightScatter * viewScatter * 0.318309886;
}

// ===========================================================================
//  Parallax Occlusion Mapping (matches PBR.glsl ParallaxOcclusionMapping)
// ===========================================================================

// Linear ray march + 3-iteration secant-method binary refinement. Height is sampled from the G
// channel of the height texture (matches the GLSL path). View direction must be in tangent space
// (transpose(TBN) * worldViewDir). SampleGrad replaces GLSL textureGrad so derivatives flow into
// the height fetch for correct mip selection.
float2 StandardParallaxOcclusionMapping(
    Texture2D heightTex,
    SamplerState heightSampler,
    float2 uv,
    float3 viewDirTS,
    float heightScale,
    int linearSteps)
{
    // Guard against degenerate inputs: zero height scale or non-positive steps are a no-op, and a
    // near-grazing view direction (viewDirTS.z ~= 0) makes vMaxOffset explode; the slope damping
    // below already cancels most of that, but we clamp the denominator to stay finite.
    if (heightScale <= 0.0 || linearSteps <= 0)
        return uv;

    // Slope damping: reduce parallax at grazing angles.
    float slopeDamp = 1.0 - clamp(dot(viewDirTS, float3(0.0, 0.0, 1.0)), 0.0, 1.0);

    float viewZ = max(abs(viewDirTS.z), 1e-4);
    float2 vMaxOffset = (viewDirTS.xy / -viewZ) * heightScale * (1.0 - slopeDamp * slopeDamp);

    float fStepSize = 1.0 / float(linearSteps);
    float2 finalStepSize = fStepSize * vMaxOffset;

    // Screen-space derivatives for correct mip selection (HLSL ddx/ddy mirror GLSL dFdx/dFdy).
    float2 dx = ddx(uv);
    float2 dy = ddy(uv);

    // Linear ray march from surface (1.0) downward.
    float fCurrRayHeight = 1.0;
    float2 vCurrOffset = float2(0.0, 0.0);
    float2 vLastOffset = float2(0.0, 0.0);
    float fLastSampledHeight = 1.0;
    float fCurrSampledHeight = 1.0;

    [loop]
    for (int i = 0; i < linearSteps; i++)
    {
        fCurrSampledHeight = heightTex.SampleGrad(heightSampler, uv + vCurrOffset, dx, dy).g;

        if (fCurrSampledHeight > fCurrRayHeight)
            break;

        fCurrRayHeight -= fStepSize;
        vLastOffset = vCurrOffset;
        vCurrOffset += finalStepSize;
        fLastSampledHeight = fCurrSampledHeight;
    }

    // Secant-method binary refinement (3 iterations).
    float pt0 = fCurrRayHeight + fStepSize;
    float pt1 = fCurrRayHeight;
    float delta0 = pt0 - fLastSampledHeight;
    float delta1 = pt1 - fCurrSampledHeight;

    [unroll]
    for (int j = 0; j < 3; j++)
    {
        float intersectionHeight = (pt0 * delta1 - pt1 * delta0) / (delta1 - delta0);
        vCurrOffset = (1.0 - intersectionHeight) * finalStepSize * float(linearSteps);

        fCurrSampledHeight = heightTex.SampleGrad(heightSampler, uv + vCurrOffset, dx, dy).g;

        float delta = intersectionHeight - fCurrSampledHeight;
        if (delta < 0.0)
        {
            delta1 = delta;
            pt1 = intersectionHeight;
        }
        else
        {
            delta0 = delta;
            pt0 = intersectionHeight;
        }
    }

    return uv + vCurrOffset;
}

// ===========================================================================
//  Translucency (matches PBR.glsl CalculateTranslucency)
// ===========================================================================

// Two modes controlled by scatteringPower:
//   scatteringPower <= 0.001: wrapped diffuse + GGX backscatter lobe (foliage, thin surfaces)
//   scatteringPower > 0.001:  spherical-Gaussian approximation (skin, wax)
// Reference: Colin Barré-Brisebois, GDC 2011. `lightDir` is the surface-to-light direction; the
// GLSL path passes the same vector it uses for PBR (the negated surface-to-light vector), so we
// mirror that convention here.
float3 StandardCalculateTranslucency(
    float3 lightDir,
    float3 viewDir,
    float3 normal,
    float translucency,
    float scatteringPower,
    float distortion,
    float scale,
    float3 lightColor)
{
    if (translucency <= 0.0)
        return 0.0.xxx;

    float3 lightScattering = 0.0.xxx;

    if (scatteringPower < 0.001)
    {
        // Mode 1: wrapped diffuse + GGX-shaped backscatter lobe.
        float wrap = 0.5;
        float wrappedNdotL = clamp((dot(-normal, lightDir) + wrap) / ((1.0 + wrap) * (1.0 + wrap)), 0.0, 1.0);

        float VdotL = clamp(dot(viewDir, -lightDir), 0.0, 1.0);
        float a2 = 0.49; // 0.7^2
        float d = (VdotL * a2 - VdotL) * VdotL + 1.0;
        float GGX = (a2 / 3.14159265) / (d * d);

        lightScattering = wrappedNdotL * GGX * translucency * lightColor;
    }
    else
    {
        // Mode 2: spherical-Gaussian approximation.
        float3 transLightDir = lightDir + normal * distortion;
        float transDot = dot(-transLightDir, viewDir);
        transDot = exp2(clamp(transDot, 0.0, 1.0) * scatteringPower - scatteringPower) * translucency;

        lightScattering = transDot * lightColor;
    }

    return lightScattering * scale;
}

// ===========================================================================
//  Shadow sampling (matches Lighting.glsl SampleDirectional/Point/SpotShadow)
// ===========================================================================

// Directional cascade shadows. Selects a cascade by squared world distance (the GLSL path compares
// against the squared split so neither side takes a per-fragment sqrt). worldDist = distance * 2,
// hence the * 4 factor on the squared distance.
float StandardSampleDirectionalShadow(float3 worldPosition, float3 worldNormal, float2 screenPosition)
{
    int cascadeCount = (int)_ShadowParams1.y;
    if (cascadeCount == 0)
        return 0.0;

    float3 toCamera = worldPosition - _WorldSpaceCameraPos;
    float worldDistSq = dot(toCamera, toCamera) * 4.0;

    float4x4 cascadeMatrix = _CascadeShadowMatrix0;
    float4 cascadeParams = _CascadeAtlasParams0;

    [flatten]
    if (cascadeCount >= 1 && worldDistSq <= _CascadeAtlasParams0.w * _CascadeAtlasParams0.w)
    {
        cascadeMatrix = _CascadeShadowMatrix0;
        cascadeParams = _CascadeAtlasParams0;
    }
    else if (cascadeCount >= 2 && worldDistSq <= _CascadeAtlasParams1.w * _CascadeAtlasParams1.w)
    {
        cascadeMatrix = _CascadeShadowMatrix1;
        cascadeParams = _CascadeAtlasParams1;
    }
    else if (cascadeCount >= 3 && worldDistSq <= _CascadeAtlasParams2.w * _CascadeAtlasParams2.w)
    {
        cascadeMatrix = _CascadeShadowMatrix2;
        cascadeParams = _CascadeAtlasParams2;
    }
    else if (cascadeCount >= 4 && worldDistSq <= _CascadeAtlasParams3.w * _CascadeAtlasParams3.w)
    {
        cascadeMatrix = _CascadeShadowMatrix3;
        cascadeParams = _CascadeAtlasParams3;
    }
    else
    {
        if (cascadeCount == 1) { cascadeMatrix = _CascadeShadowMatrix0; cascadeParams = _CascadeAtlasParams0; }
        else if (cascadeCount == 2) { cascadeMatrix = _CascadeShadowMatrix1; cascadeParams = _CascadeAtlasParams1; }
        else if (cascadeCount == 3) { cascadeMatrix = _CascadeShadowMatrix2; cascadeParams = _CascadeAtlasParams2; }
        else { cascadeMatrix = _CascadeShadowMatrix3; cascadeParams = _CascadeAtlasParams3; }
    }

    if (cascadeParams.z <= 0.0)
        return 0.0;

    float3 worldPosBiased = worldPosition + normalize(worldNormal) * _ShadowParams0.z;
    float4 lightSpacePos = mul(cascadeMatrix, float4(worldPosBiased, 1.0));
    float3 projCoords = StandardShadowProjectedCoordinates(lightSpacePos, _ShadowParams1.z);

    if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0)
        return 0.0;

    float atlasSize = _ShadowAtlasSizePad.x;
    float2 atlasCoords, shadowMin, shadowMax;
    StandardShadowAtlasCoordinates(projCoords, cascadeParams, atlasSize, atlasCoords, shadowMin, shadowMax);

    float slopeBias = StandardShadowSlopeBias(worldNormal, _StandardDirectionalDirectionIntensity.xyz, _ShadowParams0.y);
    float texelWorldSize = ((cascadeParams.w * 4.0) / (cascadeParams.z * atlasSize)) * 8.0;
    float currentDepth = projCoords.z - (slopeBias + texelWorldSize);

    // World-normalized PCF radius holds the penumbra roughly constant across cascades.
    const float shadowPenumbraWorld = 0.04;
    float worldPerTexel = cascadeParams.w / max(cascadeParams.z, 1.0);
    float pcfRadius = clamp(shadowPenumbraWorld / worldPerTexel, 0.75, 4.0);

    return StandardSampleShadowPcf(_ShadowAtlas, _ShadowAtlasSampler, atlasCoords, shadowMin, shadowMax,
                                    currentDepth, _ShadowParams1.x,
                                    _ShadowParams0.w, pcfRadius, screenPosition, StandardSoftShadowTapCount());
}

// Point shadow: pick one of six cube faces by dominant axis, then sample that face's atlas tile.
float StandardSamplePointShadow(StandardLightSample light, float3 worldPosition, float3 worldNormal, float2 screenPosition)
{
    float3 lightToFrag = worldPosition - light.Position;
    float3 absDir = abs(lightToFrag);

    int faceIndex = 0;
    if (absDir.x >= absDir.y && absDir.x >= absDir.z)
        faceIndex = lightToFrag.x > 0.0 ? 0 : 1;
    else if (absDir.y >= absDir.x && absDir.y >= absDir.z)
        faceIndex = lightToFrag.y > 0.0 ? 2 : 3;
    else
        faceIndex = lightToFrag.z > 0.0 ? 4 : 5;

    int idx = light.ShadowSlot * 6 + faceIndex;
    float4x4 shadowMatrix = _PointShadowMatrices[idx];
    float4 faceParams = _PointShadowFaceParams[idx];

    float3 worldPosBiased = worldPosition + normalize(worldNormal) * light.ShadowNormalBias;
    float4 lightSpacePos = mul(shadowMatrix, float4(worldPosBiased, 1.0));
    float3 projCoords = StandardShadowProjectedCoordinates(lightSpacePos, _ShadowParams1.z);

    if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0)
        return 0.0;

    float atlasSize = _ShadowAtlasSizePad.x;
    float2 atlasCoords, shadowMin, shadowMax;
    StandardShadowAtlasCoordinates(projCoords, faceParams, atlasSize, atlasCoords, shadowMin, shadowMax);

    float finalBias = StandardShadowSlopeBias(worldNormal, normalize(lightToFrag), light.ShadowBias);
    float currentDepth = projCoords.z - finalBias;

    return StandardSampleShadowPcf(_ShadowAtlas, _ShadowAtlasSampler, atlasCoords, shadowMin, shadowMax,
                                    currentDepth, light.ShadowQuality, light.ShadowStrength, 1.5, screenPosition, StandardSoftShadowTapCount());
}

// Spot shadow: single atlas tile per shadow-casting spot light.
float StandardSampleSpotShadow(StandardLightSample light, float3 worldPosition, float3 worldNormal, float2 screenPosition)
{
    float4 atlasParams = _SpotShadowAtlasParams[light.ShadowSlot];
    if (atlasParams.z <= 0.0)
        return 0.0;

    float3 worldPosBiased = worldPosition + normalize(worldNormal) * light.ShadowNormalBias;
    float4 lightSpacePos = mul(_SpotShadowMatrices[light.ShadowSlot], float4(worldPosBiased, 1.0));
    float3 projCoords = StandardShadowProjectedCoordinates(lightSpacePos, _ShadowParams1.z);

    if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0)
        return 0.0;

    float atlasSize = _ShadowAtlasSizePad.x;
    float2 atlasCoords, shadowMin, shadowMax;
    StandardShadowAtlasCoordinates(projCoords, atlasParams, atlasSize, atlasCoords, shadowMin, shadowMax);

    float finalBias = StandardShadowSlopeBias(worldNormal, light.Direction, light.ShadowBias);
    float currentDepth = projCoords.z - finalBias;

    return StandardSampleShadowPcf(_ShadowAtlas, _ShadowAtlasSampler, atlasCoords, shadowMin, shadowMax,
                                    currentDepth, light.ShadowQuality, light.ShadowStrength, 1.5, screenPosition, StandardSoftShadowTapCount());
}

float3 StandardEvaluateLight(
    StandardLightSample light,
    float3 worldPosition,
    float3 worldNormal,
    float3 viewDirection,
    float2 screenPosition,
    float3 albedo,
    float metallic,
    float roughness,
    float ao,
    float translucency,
    float scatterPower,
    float scatterDistortion,
    float scatterScale)
{
    if (!LightAffectsObject(light.RenderingLayers))
        return 0.0.xxx;

    float3 toPixel = worldPosition - light.Position;
    float distanceSquared = dot(toPixel, toPixel);
    float distanceToLight = sqrt(distanceSquared);
    float3 lightDirection = -toPixel / max(distanceToLight, 0.0001);
    if (light.Type == 2)
    {
        float coneCos = dot(normalize(light.Direction), -lightDirection);
        if (coneCos <= light.SpotCos)
            return 0.0.xxx;
    }

    float nDotL = dot(worldNormal, lightDirection);

    // Attenuation is shared between the front-lit PBR term and the translucency term (matches the
    // GLSL EvaluateLocalLightTranslucent path), so compute it before the NdotL early-out: a
    // back-facing fragment still receives translucency scatter.
    float range = max(light.Range, 0.0001);
    float rangeFactor = distanceSquared / (range * range);
    float window = saturate(1.0 - rangeFactor * rangeFactor);
    window *= window;
    float attenuation = (1.0 / max(distanceSquared, 0.01)) * window;
    if (light.Type == 2)
        attenuation *= smoothstep(light.SpotCos, light.InnerSpotCos, dot(normalize(light.Direction), -lightDirection));
    if (attenuation <= 0.0001)
        return 0.0.xxx;

    // Shadow: only sample the atlas when this leaf actually won a slot this frame and something will
    // use it (front-lit PBR or translucency). Point lights pick one of six cube faces by dominant
    // axis; spots use their single tile directly. Mirrors SamplePointShadow / SampleSpotShadow in
    // Lighting.glsl.
    float shadowFactor = 1.0;
    if ((nDotL > 0.0 || translucency > 0.0) && light.ShadowEnabled != 0 && light.ShadowSlot >= 0)
    {
        float shadow = (light.Type == 1)
            ? StandardSamplePointShadow(light, worldPosition, worldNormal, screenPosition)
            : StandardSampleSpotShadow(light, worldPosition, worldNormal, screenPosition);
        shadowFactor = 1.0 - shadow;
    }

    float3 radiance = light.Color * (light.Intensity * 8.0) * attenuation * SampleLocalLightCookie(light, worldPosition);
    float3 result = 0.0.xxx;

    // PBR (front-lit only).
    if (nDotL > 0.0)
    {
        float3 halfway = normalize(lightDirection + viewDirection);
        float nDotV = max(dot(worldNormal, viewDirection), 0.0);
        float lDotH = max(dot(lightDirection, halfway), 0.0);
        float3 f0 = lerp(0.04.xxx, albedo, metallic);
        float3 fresnel = StandardFresnel(lDotH, f0);
        float3 diffuseWeight = (1.0.xxx - fresnel) * (1.0 - metallic);
        float3 specular = StandardDistributionGGX(worldNormal, halfway, roughness) *
                          StandardGeometrySmith(worldNormal, viewDirection, lightDirection, roughness) * fresnel /
                          max(4.0 * nDotV * nDotL, 0.0001);
        float3 diffuse = diffuseWeight * albedo * StandardDisneyDiffuse(nDotV, nDotL, lDotH, roughness);
        result = (diffuse + specular) * radiance * nDotL * shadowFactor * ao;
    }

    // Translucency (uses the same attenuation + shadow, works regardless of NdotL).
    if (translucency > 0.0)
    {
        float3 scatter = StandardCalculateTranslucency(lightDirection, viewDirection, worldNormal,
                            translucency, scatterPower, scatterDistortion, scatterScale,
                            light.Color * light.Intensity * attenuation * SampleLocalLightCookie(light, worldPosition));
        result += scatter * albedo * shadowFactor;
    }

    return result;
}

float3 StandardEvaluateDirectional(
    float3 worldPosition,
    float3 worldNormal,
    float3 viewDirection,
    float2 screenPosition,
    float3 albedo,
    float metallic,
    float roughness,
    float ao,
    float translucency,
    float scatterPower,
    float scatterDistortion,
    float scatterScale)
{
    if (_StandardLightFlags.x == 0)
        return 0.0.xxx;
    if (!LightAffectsObject(_StandardLightFlags.w))
        return 0.0.xxx;

    float3 lightDirection = normalize(_StandardDirectionalDirectionIntensity.xyz);
    float3 halfway = normalize(lightDirection + viewDirection);
    float nDotL = max(dot(worldNormal, lightDirection), 0.0);

    // Shadow: cascade selection by squared world distance (matches SampleDirectionalShadow in
    // Lighting.glsl; worldDist = distance * 2 so the squared form scales by 4). Sampled when either
    // the front-lit PBR term or the translucency term will use it.
    float shadowFactor = 1.0;
    if (nDotL > 0.0 || translucency > 0.0)
    {
        float shadow = 0.0;
        if (_ShadowParams0.x != 0.0)
            shadow = StandardSampleDirectionalShadow(worldPosition, worldNormal, screenPosition);
        shadowFactor = StandardApplyScreenSpaceShadows(1.0 - shadow, screenPosition);
    }

    float3 radiance = _StandardDirectionalColor.rgb * (_StandardDirectionalDirectionIntensity.w * 8.0) * SampleMainLightCookie(worldPosition);
    float3 result = 0.0.xxx;

    // PBR (front-lit only).
    if (nDotL > 0.0)
    {
        float nDotV = max(dot(worldNormal, viewDirection), 0.0);
        float lDotH = max(dot(lightDirection, halfway), 0.0);
        float3 f0 = lerp(0.04.xxx, albedo, metallic);
        float3 fresnel = StandardFresnel(lDotH, f0);
        float3 diffuseWeight = (1.0.xxx - fresnel) * (1.0 - metallic);
        float3 specular = StandardDistributionGGX(worldNormal, halfway, roughness) *
                          StandardGeometrySmith(worldNormal, viewDirection, lightDirection, roughness) * fresnel /
                          max(4.0 * nDotV * nDotL, 0.0001);
        float3 diffuse = diffuseWeight * albedo * StandardDisneyDiffuse(nDotV, nDotL, lDotH, roughness);
        result = (diffuse + specular) * radiance * nDotL * shadowFactor * ao;
    }

    // Translucency (same shadow; no distance attenuation for the directional light, matching GLSL).
    if (translucency > 0.0)
    {
        float3 scatter = StandardCalculateTranslucency(lightDirection, viewDirection, worldNormal,
                            translucency, scatterPower, scatterDistortion, scatterScale,
                            _StandardDirectionalColor.rgb * _StandardDirectionalDirectionIntensity.w * SampleMainLightCookie(worldPosition));
        result += scatter * albedo * shadowFactor;
    }

    return result;
}

float3 StandardCalculateLighting(
    float3 worldPosition,
    float3 worldNormal,
    float3 viewDirection,
    float2 screenPosition,
    float3 albedo,
    float metallic,
    float roughness,
    float ao,
    float translucency,
    float scatterPower,
    float scatterDistortion,
    float scatterScale)
{
    float3 result = StandardEvaluateDirectional(worldPosition, worldNormal, viewDirection, screenPosition, albedo, metallic, roughness, ao, translucency, scatterPower, scatterDistortion, scatterScale);
#ifdef XENGINE_CLUSTERED_LIGHTS
    // Clustered path: one lookup + compact index loop. Superset of the BVH result; out-of-range
    // lights contribute exactly 0 (attenuation window), so lighting is identical.
    if (_ClusterGridParams.w > 0.5)
    {
        int2 header = StandardClusterHeader(worldPosition);
        for (int i = 0; i < header.y; i++)
        {
            StandardLightSample light = StandardClusterFetchLight(header.x + i);
            result += StandardEvaluateLight(light, worldPosition, worldNormal, viewDirection, screenPosition, albedo, metallic, roughness, ao, translucency, scatterPower, scatterDistortion, scatterScale);
        }
        return result;
    }
#endif
    if (_StandardLightFlags.y >= 0 && _StandardStaticLightMeta.x > 0 && _StandardStaticLightMeta.z > 0)
    {
        int current = _StandardLightFlags.y;
        int slot;
        while ((slot = StandardBvhNext(current, _StaticLightNodes, _StandardStaticLightMeta.z,
                                       _StandardStaticLightMeta.w, worldPosition)) >= 0)
        {
            StandardLightSample light = StandardFetchLight(_StaticLightData, _StandardStaticLightMeta.x,
                                                            _StandardStaticLightMeta.y, slot);
            result += StandardEvaluateLight(light, worldPosition, worldNormal, viewDirection, screenPosition, albedo, metallic, roughness, ao, translucency, scatterPower, scatterDistortion, scatterScale);
        }
    }
    if (_StandardLightFlags.z >= 0 && _StandardDynamicLightMeta.x > 0 && _StandardDynamicLightMeta.z > 0)
    {
        int current = _StandardLightFlags.z;
        int slot;
        while ((slot = StandardBvhNext(current, _DynamicLightNodes, _StandardDynamicLightMeta.z,
                                       _StandardDynamicLightMeta.w, worldPosition)) >= 0)
        {
            StandardLightSample light = StandardFetchLight(_DynamicLightData, _StandardDynamicLightMeta.x,
                                                            _StandardDynamicLightMeta.y, slot);
            result += StandardEvaluateLight(light, worldPosition, worldNormal, viewDirection, screenPosition, albedo, metallic, roughness, ao, translucency, scatterPower, scatterDistortion, scatterScale);
        }
    }
    return result;
}

float3 StandardCalculateAmbient(float3 normal)
{
    float3 ambient = _StandardAmbientColor.rgb;
    if (_StandardAmbientModeStrength.y > 0.5)
        ambient = lerp(_StandardAmbientGroundColor.rgb, _StandardAmbientSkyColor.rgb, saturate(normal.y * 0.5 + 0.5));
    return ambient * _StandardAmbientModeStrength.z;
}

// ===========================================================================
//  Baked GI (matches StandardSurface.glsl CalculateGI / DecodeRGBM / ShadeSH9)
// ===========================================================================

// RGBM decode: the lightmap stores baked irradiance as RGB * (A * 8), matching the GLSL path.
float3 StandardDecodeRGBM(float4 rgbm)
{
    return rgbm.rgb * (rgbm.a * 8.0);
}

// Per-object SH-L2 diffuse irradiance (the standard ShadeSH9). Mirrors Lighting.glsl's GLSL
// implementation: the L1 terms come from xengine_SH{A,B}{r,g,b}, the single L2 trace term comes from
// xengine_SHC. Returns max(0) so a noisy probe bake never subtracts light.
float3 StandardShadeSH9(float3 n)
{
    float4 nrm = float4(n, 1.0);
    float3 x;
    x.r = dot(xengine_SHAr, nrm);
    x.g = dot(xengine_SHAg, nrm);
    x.b = dot(xengine_SHAb, nrm);

    float4 vB = n.xyzz * n.yzzx; // (xy, yz, z^2, zx)
    float3 x1;
    x1.r = dot(xengine_SHBr, vB);
    x1.g = dot(xengine_SHBg, vB);
    x1.b = dot(xengine_SHBb, vB);

    float vC = n.x * n.x - n.y * n.y;
    float3 x2 = xengine_SHC.rgb * vC;

    return max(x + x1 + x2, 0.0.xxx);
}

// Per-object baked GI. _GIMode selects realtime-ambient (0), baked lightmap (1), light-probe SH
// (2), or the realtime GI probe volume (3, SampleGiProbes). The lightmap samples the UV set the
// bake used (UV2 when _LightmapUV == 1, else UV0), scaled + offset by _LightmapScaleOffset; the SH
// path evaluates StandardShadeSH9 with the surface normal. Realtime-ambient falls through to
// StandardCalculateAmbient so the modern backends reproduce the existing default-scene behavior
// exactly. Mode 3 floors the probes with a small ambient so un-converged areas keep light.
float3 StandardCalculateGI(float3 worldPos, float3 worldNormal, float2 lightmapUV2, float2 uv0)
{
    if (_GIMode == 1)
    {
        float2 base = (_LightmapUV == 1) ? lightmapUV2 : uv0;
        float2 lmUV = base * _LightmapScaleOffset.xy + _LightmapScaleOffset.zw;
        return StandardDecodeRGBM(_Lightmap.Sample(_LightmapSampler, lmUV));
    }
    if (_GIMode == 2)
        return StandardShadeSH9(worldNormal);
    if (_GIMode == 3)
        return max(SampleGiProbes(worldPos, worldNormal), StandardCalculateAmbient(worldNormal) * 0.25);

    return StandardCalculateAmbient(worldNormal);
}

float3 StandardApplyFog(float3 color, float3 worldPosition)
{
    float depth = length(_WorldSpaceCameraPos - worldPosition);
    float factor = 0.0;
    if (_StandardFogStates.x > 0.5)
        factor = saturate(_StandardFogParams.z * depth + _StandardFogParams.w);
    else if (_StandardFogStates.y > 0.5)
        factor = 1.0 - exp(-_StandardFogParams.x * depth);
    else if (_StandardFogStates.z > 0.5)
    {
        float density = _StandardFogParams.y * depth;
        factor = 1.0 - exp(-density * density);
    }
    return lerp(color, _StandardFogColor.rgb, saturate(factor));
}

#endif
