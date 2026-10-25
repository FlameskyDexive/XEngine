#ifndef XENGINE_STANDARD_SHADOW_SAMPLING_HLSL
#define XENGINE_STANDARD_SHADOW_SAMPLING_HLSL

// Standard forward-path shadow sampling for the modern (Vulkan / D3D12) backends.
//
// Mirrors the GLSL Shadow.glsl + Lighting.glsl shadow surface: directional cascade shadows plus
// closest-N point and spot shadow slots that share the single shadow atlas. Modern backends do not
// expose GL's hardware depth-compare sampler, so each tap is a SampleLevel read followed by an
// explicit <= comparison. The Poisson-disk soft path matches the GLSL 8-tap kernel; the hard path
// is a single bilinearly filtered tap (equivalent to GL's 2x2 hardware PCF).
//
// The cbuffer + atlas texture are declared by the including shader (StandardLighting.hlsl wires
// them in) so the binding reflection stays owned by the Standard variant. Slot rejection (.z <= 0
// for spot/cascade face params, slot-disabled for point) matches Lighting.glsl so an unset slot
// samples as fully lit.

#include "ShaderLibrary/ShaderConfig"

// Interleaved gradient noise for rotated Poisson disk PCF (matches Shadow.glsl).
float StandardShadowInterleavedGradientNoise(float2 position)
{
    float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(position, magic.xy)));
}

static const float2 XENGINE_STANDARD_POISSON_DISK_8[8] =
{
    float2(-0.613392,  0.617481),
    float2( 0.170019, -0.040254),
    float2(-0.299417, -0.792901),
    float2( 0.645680, -0.530998),
    float2( 0.454148,  0.516511),
    float2(-0.507431,  0.281182),
    float2(-0.177186, -0.283153),
    float2( 0.100558,  0.765839)
};

// Modern backends use [0,1] clip depth. Most use a flipped viewport and need yScale = -0.5;
// Mali Vulkan uses a positive viewport and needs yScale = +0.5. Z stays in the projection
// matrix's native [0,1] range.
float3 StandardShadowProjectedCoordinates(float4 lightSpacePosition, float yScale)
{
    float3 ndc = lightSpacePosition.xyz / lightSpacePosition.w;
    return float3(ndc.x * 0.5 + 0.5, 0.5 + ndc.y * yScale, ndc.z);
}

// Atlas UV + boundary clamp (matches GetAtlasCoordinates in Shadow.glsl).
void StandardShadowAtlasCoordinates(
    float3 projectedCoordinates,
    float4 atlasParameters,
    float atlasSize,
    out float2 atlasCoordinates,
    out float2 shadowMin,
    out float2 shadowMax)
{
    float2 texel = 1.0.xx / max(atlasSize, 1.0);
    shadowMin = atlasParameters.xy / atlasSize + texel * 0.5;
    shadowMax = (atlasParameters.xy + atlasParameters.zz) / atlasSize - texel * 0.5;
    atlasCoordinates = (atlasParameters.xy + projectedCoordinates.xy * atlasParameters.z) / atlasSize;
}

// Combined slope + flat bias (matches CalculateSlopeBias in Shadow.glsl).
float StandardShadowSlopeBias(float3 worldNormal, float3 lightDirection, float baseBias)
{
    float cosTheta = clamp(dot(normalize(worldNormal), normalize(lightDirection)), 0.0, 1.0);
    float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
    float slopeScaleBias = baseBias * (sinTheta / max(cosTheta, 1e-4));
    slopeScaleBias = clamp(slopeScaleBias, 0.0, baseBias * 2.0);
    return baseBias + slopeScaleBias;
}

// Single-tap hard comparison or 8-tap rotated Poisson soft comparison against the shadow atlas.
// `shadowQuality >= 0.5` selects the soft path. Returns the occluded fraction scaled by strength.
float StandardSampleShadowPcf(
    Texture2D shadowAtlas,
    SamplerState shadowSampler,
    float2 atlasCoordinates,
    float2 shadowMin,
    float2 shadowMax,
    float currentDepth,
    float shadowQuality,
    float shadowStrength,
    float filterRadius,
    float2 screenPosition,
    int tapCount)
{
    float lit;
    if (shadowQuality < 0.5)
    {
        // Hard path: single bilinearly filtered tap + explicit depth comparison.
        float2 clamped = clamp(atlasCoordinates, shadowMin, shadowMax);
        float stored = shadowAtlas.SampleLevel(shadowSampler, clamped, 0).r;
        lit = (currentDepth <= stored) ? 1.0 : 0.0;
    }
    else
    {
        uint width;
        uint height;
        shadowAtlas.GetDimensions(width, height);
        float2 texelSize = 1.0.xx / max(float2(width, height), 1.0.xx);
        float randomRotation = StandardShadowInterleavedGradientNoise(screenPosition) * 6.2831853;
        float s = sin(randomRotation);
        float c = cos(randomRotation);
        float2x2 rotation = float2x2(c, -s, s, c);

        int taps = tapCount;
        if (taps <= 0)
            taps = 8;
        if (taps > MAX_SOFT_SHADOW_TAPS)
            taps = MAX_SOFT_SHADOW_TAPS;
        float radiusScale = 1.0;
        if (taps <= 4)
            radiusScale = 0.65;
        else if (taps >= 16)
            radiusScale = 1.35;

        float2 texelScale = texelSize * filterRadius * radiusScale;
        lit = 0.0;
        [loop]
        for (int i = 0; i < MAX_SOFT_SHADOW_TAPS; i++)
        {
            if (i >= taps)
                break;
            float innerScale = i < 8 ? 1.0 : 0.55;
            float2 offset = mul(rotation, XENGINE_STANDARD_POISSON_DISK_8[i & 7]) * texelScale * innerScale;
            float2 sampleCoordinates = clamp(atlasCoordinates + offset, shadowMin, shadowMax);
            float stored = shadowAtlas.SampleLevel(shadowSampler, sampleCoordinates, 0).r;
            lit += (currentDepth <= stored) ? 1.0 : 0.0;
        }
        lit /= float(taps);
    }

    float shadow = 1.0 - lit;
    return shadow * shadowStrength;
}

#endif
