#ifndef XENGINE_SHADOW_HLSL
#define XENGINE_SHADOW_HLSL

float FogShadowAtlasSample(
    Texture2D shadowAtlas,
    SamplerState shadowSampler,
    float2 atlasCoordinates,
    float2 shadowMin,
    float2 shadowMax,
    float currentDepth,
    float shadowQuality,
    float shadowStrength)
{
    atlasCoordinates = clamp(atlasCoordinates, shadowMin, shadowMax);
    float lit = shadowAtlas.SampleLevel(shadowSampler, atlasCoordinates, 0).r;
    if (shadowQuality >= 0.5)
    {
        uint width;
        uint height;
        shadowAtlas.GetDimensions(width, height);
        float2 texel = 1.0.xx / max(float2(width, height), 1.0.xx);
        float4 taps = float4(
            shadowAtlas.SampleLevel(shadowSampler, clamp(atlasCoordinates + texel * float2(-1, -1), shadowMin, shadowMax), 0).r,
            shadowAtlas.SampleLevel(shadowSampler, clamp(atlasCoordinates + texel * float2(1, -1), shadowMin, shadowMax), 0).r,
            shadowAtlas.SampleLevel(shadowSampler, clamp(atlasCoordinates + texel * float2(-1, 1), shadowMin, shadowMax), 0).r,
            shadowAtlas.SampleLevel(shadowSampler, clamp(atlasCoordinates + texel * float2(1, 1), shadowMin, shadowMax), 0).r);
        lit = dot(taps, 0.25.xxxx);
    }
    return (currentDepth > lit ? 1.0 : 0.0) * shadowStrength;
}

float2 FogShadowAtlasUv(float3 projectedCoordinates, float4 atlasParameters, float atlasSize, out float2 shadowMin, out float2 shadowMax)
{
    float2 texel = 1.0.xx / max(atlasSize, 1.0);
    shadowMin = atlasParameters.xy / atlasSize + texel * 0.5;
    shadowMax = (atlasParameters.xy + atlasParameters.zz) / atlasSize - texel * 0.5;
    return (atlasParameters.xy + projectedCoordinates.xy * atlasParameters.z) / atlasSize;
}

#endif
