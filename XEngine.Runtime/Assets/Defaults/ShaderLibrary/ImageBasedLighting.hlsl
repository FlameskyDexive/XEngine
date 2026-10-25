#ifndef XENGINE_IMAGE_BASED_LIGHTING_HLSL
#define XENGINE_IMAGE_BASED_LIGHTING_HLSL

#include "ShaderLibrary/ShaderConfig"

#ifndef XENGINE_STANDARD_SPEC_CUBE0_TEXTURE_REGISTER
#define XENGINE_STANDARD_SPEC_CUBE0_TEXTURE_REGISTER t26
#endif
#ifndef XENGINE_STANDARD_SPEC_CUBE0_SAMPLER_REGISTER
#define XENGINE_STANDARD_SPEC_CUBE0_SAMPLER_REGISTER s26
#endif
#ifndef XENGINE_STANDARD_SPEC_CUBE1_TEXTURE_REGISTER
#define XENGINE_STANDARD_SPEC_CUBE1_TEXTURE_REGISTER t27
#endif
#ifndef XENGINE_STANDARD_SPEC_CUBE1_SAMPLER_REGISTER
#define XENGINE_STANDARD_SPEC_CUBE1_SAMPLER_REGISTER s27
#endif
#ifndef XENGINE_STANDARD_BRDF_LUT_TEXTURE_REGISTER
#define XENGINE_STANDARD_BRDF_LUT_TEXTURE_REGISTER t28
#endif
#ifndef XENGINE_STANDARD_BRDF_LUT_SAMPLER_REGISTER
#define XENGINE_STANDARD_BRDF_LUT_SAMPLER_REGISTER s28
#endif

TextureCube _SpecCube0 : register(XENGINE_STANDARD_SPEC_CUBE0_TEXTURE_REGISTER);
#ifndef XENGINE_METAL
SamplerState _SpecCube0Sampler : register(XENGINE_STANDARD_SPEC_CUBE0_SAMPLER_REGISTER);
#endif
TextureCube _SpecCube1 : register(XENGINE_STANDARD_SPEC_CUBE1_TEXTURE_REGISTER);
#ifndef XENGINE_METAL
SamplerState _SpecCube1Sampler : register(XENGINE_STANDARD_SPEC_CUBE1_SAMPLER_REGISTER);
#endif
Texture2D _BRDFLut : register(XENGINE_STANDARD_BRDF_LUT_TEXTURE_REGISTER);
#ifndef XENGINE_METAL
SamplerState _BRDFLutSampler : register(XENGINE_STANDARD_BRDF_LUT_SAMPLER_REGISTER);
#endif

#ifdef XENGINE_METAL
// These engine-owned probe/LUT textures all use linear filtering and clamp-to-edge.
// Reuse the linear/clamp cookie-atlas sampler to stay within Metal's 16 sampler slots.
#define _SpecCube0Sampler _LightCookieAtlasSampler
#define _SpecCube1Sampler _LightCookieAtlasSampler
#define _BRDFLutSampler _LightCookieAtlasSampler
#endif

// All-float4 so C# StandardReflectionProbesUniformsData marshals 1:1 (112 bytes).
cbuffer StandardReflectionProbes : register(b5)
{
    float4 _ProbeCountWeights;       // x = count, y = weight0, z = weight1
    float4 _Probe0PositionIntensity; // xyz position, w intensity
    float4 _Probe0BoxMinProjection;  // xyz boxMin, w boxProjection
    float4 _Probe0BoxMaxMips;        // xyz boxMax, w mipCount
    float4 _Probe1PositionIntensity;
    float4 _Probe1BoxMinProjection;
    float4 _Probe1BoxMaxMips;
};

float3 BoxProjectedCubemapDirection(float3 dir, float3 worldPos, float3 probePos, float3 boxMin, float3 boxMax)
{
    float3 nrDir = normalize(dir);
    float3 rbmax = (boxMax - worldPos) / nrDir;
    float3 rbmin = (boxMin - worldPos) / nrDir;
    float3 rbminmax = lerp(rbmin, rbmax, step(0.0, nrDir));
    float fa = min(min(rbminmax.x, rbminmax.y), rbminmax.z);
    float3 posOnBox = worldPos + nrDir * fa;
    return posOnBox - probePos;
}

float3 SampleIBLCube(TextureCube cube, SamplerState cubeSampler, float3 dir, float roughness, float mipCount)
{
    float mip = roughness * max(mipCount - 1.0, 0.0);
    return cube.SampleLevel(cubeSampler, dir, mip).rgb;
}

float3 MixIndirectSpecular(float3 approx, float3 worldPos, float3 worldNormal, float3 viewDir, float3 F0, float roughness)
{
    if (_ProbeCountWeights.x < 0.5)
        return approx;

    float3 N = normalize(worldNormal);
    float3 V = normalize(viewDir);
    float3 R = reflect(-V, N);
    float NdotV = max(dot(N, V), 0.0);

    float3 dir0 = R;
    if (_Probe0BoxMinProjection.w > 0.5)
        dir0 = BoxProjectedCubemapDirection(R, worldPos, _Probe0PositionIntensity.xyz, _Probe0BoxMinProjection.xyz, _Probe0BoxMaxMips.xyz);
    float3 env = SampleIBLCube(_SpecCube0, _SpecCube0Sampler, dir0, roughness, _Probe0BoxMaxMips.w)
        * _Probe0PositionIntensity.w * _ProbeCountWeights.y;

    if (_ProbeCountWeights.x > 1.5)
    {
        float3 dir1 = R;
        if (_Probe1BoxMinProjection.w > 0.5)
            dir1 = BoxProjectedCubemapDirection(R, worldPos, _Probe1PositionIntensity.xyz, _Probe1BoxMinProjection.xyz, _Probe1BoxMaxMips.xyz);
        env += SampleIBLCube(_SpecCube1, _SpecCube1Sampler, dir1, roughness, _Probe1BoxMaxMips.w)
            * _Probe1PositionIntensity.w * _ProbeCountWeights.z;
    }

    float w = saturate(_ProbeCountWeights.y + _ProbeCountWeights.z);
    float2 brdf = _BRDFLut.Sample(_BRDFLutSampler, float2(NdotV, roughness)).rg;
    float3 ibl = env * (F0 * brdf.x + brdf.y);
    return lerp(approx, ibl, w);
}

#endif
